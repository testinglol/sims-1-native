using System.Buffers.Binary;

namespace SimsEngine.Formats;

/// <summary>A resource type code such as BHAV or STR#. Sims.exe passes these as the four characters read as a
/// big-endian integer (BHAV is 0x42484156 in its chunk lookups).</summary>
public readonly record struct ResourceType(uint Code)
{
    public static readonly ResourceType Bhav = Parse("BHAV");
    public static readonly ResourceType Bcon = Parse("BCON");
    public static readonly ResourceType Str = Parse("STR#");
    public static readonly ResourceType Ctss = Parse("CTSS");
    public static readonly ResourceType Ttas = Parse("TTAs");
    public static readonly ResourceType Objd = Parse("OBJD");
    public static readonly ResourceType Objf = Parse("OBJf");
    public static readonly ResourceType Glob = Parse("GLOB");
    public static readonly ResourceType Ttab = Parse("TTAB");
    public static readonly ResourceType Slot = Parse("SLOT");
    public static readonly ResourceType Tprp = Parse("TPRP");
    public static readonly ResourceType Trcn = Parse("TRCN");
    public static readonly ResourceType Rsmp = Parse("rsmp");
    public static readonly ResourceType Spr1 = Parse("SPR#");
    public static readonly ResourceType Spr2 = Parse("SPR2");
    public static readonly ResourceType Palt = Parse("PALT");
    public static readonly ResourceType Dgrp = Parse("DGRP");
    public static readonly ResourceType Fcns = Parse("FCNS");
    public static readonly ResourceType Fwav = Parse("FWAV");
    /// <summary>Repository-private native interaction capability chunk; never a Sims/SimAntics resource.</summary>
    public static readonly ResourceType NativeLifecycleCapability = Parse("NLCF");

    public static ResourceType Parse(string text)
    {
        if (text.Length != 4) throw new ArgumentException($"A resource type has four characters: {text}", nameof(text));
        return new ResourceType((uint)(text[0] << 24 | text[1] << 16 | text[2] << 8 | text[3]));
    }

    public override string ToString() => string.Concat((char)(Code >> 24), (char)(Code >> 16 & 0xff), (char)(Code >> 8 & 0xff), (char)(Code & 0xff));
}

/// <summary>One chunk of an IFF file. <see cref="Data"/> is the chunk body without its 76-byte header.</summary>
public sealed record IffChunk(ResourceType Type, ushort Id, ushort Flags, string Label, int Offset, ReadOnlyMemory<byte> Data)
{
    public override string ToString() => $"{Type} #{Id}";
}

/// <summary>An IFF resource file ("IFF FILE 2.0" or "2.5:TYPE FOLLOWED BY SIZE"): a 60-byte signature, a
/// big-endian u32 offset of the rsmp index, then chunks from byte 64. Each chunk header is 76 bytes, all
/// big-endian: type, total size including the header, id, flags and a 64-byte zero-padded label. Chunk bodies are
/// little-endian. The rsmp chunk is an index of the others and is kept as an ordinary chunk.</summary>
public sealed class IffFile
{
    public const int HeaderSize = 64;
    public const int ChunkHeaderSize = 76;

    public string Source { get; }
    /// <summary>The complete original IFF bytes, including its header and resource-map chunk.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }
    public IReadOnlyList<IffChunk> Chunks { get; }
    private Dictionary<ulong, IffChunk>? _chunksByTypeAndId;

    public IffFile(ReadOnlyMemory<byte> data, string source)
    {
        Bytes = data;
        Source = source;
        var span = data.Span;
        if (span.Length < HeaderSize || !span[..8].SequenceEqual("IFF FILE"u8))
            throw new ResourceFormatException(source, null, 0, "not an IFF file");
        var chunks = new List<IffChunk>();
        var offset = HeaderSize;
        while (span.Length - offset >= ChunkHeaderSize)
        {
            var header = span.Slice(offset, ChunkHeaderSize);
            var type = new ResourceType(BinaryPrimitives.ReadUInt32BigEndian(header));
            var size = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            if (size < ChunkHeaderSize || size > (uint)(span.Length - offset))
                throw new ResourceFormatException(source, type.ToString(), offset, $"chunk size {size} does not fit the file");
            var id = BinaryPrimitives.ReadUInt16BigEndian(header[8..]);
            var flags = BinaryPrimitives.ReadUInt16BigEndian(header[10..]);
            var labelBytes = header.Slice(12, 64);
            var end = labelBytes.IndexOf((byte)0);
            var label = end == 0 ? "" : ResourceReader.Latin1(end < 0 ? labelBytes : labelBytes[..end]);
            chunks.Add(new IffChunk(type, id, flags, label, offset, data.Slice(offset + ChunkHeaderSize, (int)size - ChunkHeaderSize)));
            offset += (int)size;
        }
        Chunks = chunks;
    }

    public static IffFile Open(string path) => new(File.ReadAllBytes(path), path);

    public IEnumerable<IffChunk> OfType(ResourceType type) => Chunks.Where(chunk => chunk.Type == type);

    /// <summary>The first chunk of this type and id, as Sims.exe's lookup by type and id returns one chunk.</summary>
    public IffChunk? Find(ResourceType type, ushort id)
    {
        var index = _chunksByTypeAndId;
        if (index is null)
        {
            var built = new Dictionary<ulong, IffChunk>(Chunks.Count);
            for (var i = 0; i < Chunks.Count; i++)
            {
                var chunk = Chunks[i];
                built.TryAdd(((ulong)chunk.Type.Code << 16) | chunk.Id, chunk);
            }
            index = Interlocked.CompareExchange(ref _chunksByTypeAndId, built, null) ?? built;
        }
        return index.GetValueOrDefault(((ulong)type.Code << 16) | id);
    }

    public ResourceReader Reader(IffChunk chunk) => new(chunk.Data.Span, Source, chunk.ToString());

    /// <summary>Replaces a chunk body and rebuilds sequential offsets and the resource-map index.</summary>
    public IffFile WithChunkDataResized(ResourceType type, ushort id, ReadOnlyMemory<byte> replacement)
    {
        var chunks = Chunks.ToList();
        var found = chunks.FindIndex(chunk => chunk.Type == type && chunk.Id == id);
        if (found < 0) throw new KeyNotFoundException($"{Source}: no {type} chunk #{id} to replace.");
        chunks[found] = chunks[found] with { Data = replacement };
        return Rebuild(chunks);
    }

    /// <summary>Adds a previously absent resource, rebuilding the resource-map index and its offsets.</summary>
    public IffFile WithAddedChunk(ResourceType type, ushort id, ReadOnlyMemory<byte> data, ushort flags = 0, string label = "")
    {
        if (Find(type, id) is not null) throw new ArgumentException($"{Source}: {type} chunk #{id} already exists.", nameof(id));
        return Rebuild([.. Chunks, new IffChunk(type, id, flags, label, 0, data)]);
    }

    /// <summary>Adds or replaces a resource, rebuilding the resource-map index and its offsets.</summary>
    public IffFile WithAddedOrReplacedChunk(ResourceType type, ushort id, ReadOnlyMemory<byte> data, ushort flags = 0, string label = "") =>
        Find(type, id) is not null
            ? WithChunkDataResized(type, id, data)
            : WithAddedChunk(type, id, data, flags, label);

    /// <summary>Removes all chunks matching <paramref name="type"/> and <paramref name="id"/>, rebuilding the resource-map index.</summary>
    public IffFile WithoutChunk(ResourceType type, ushort id) =>
        Rebuild(Chunks.Where(chunk => !(chunk.Type == type && chunk.Id == id)).ToList());


    private IffFile Rebuild(IReadOnlyList<IffChunk> chunks)
    {
        var bodies = chunks.Select(chunk => chunk.Data).ToList();
        var rsmpIndex = chunks.ToList().FindIndex(chunk => chunk.Type == ResourceType.Rsmp);
        var sizes = new int[chunks.Count];
        for (var index = 0; index < chunks.Count; index++)
            sizes[index] = ChunkHeaderSize + bodies[index].Length;
        IReadOnlyList<ResourceMapGroup>? originalIndex = null;
        if (rsmpIndex >= 0)
        {
            originalIndex = ResourceMap.Read(chunks[rsmpIndex]);
            var provisional = ResourceMap.Write(IndexEntries(chunks, originalIndex, null));
            sizes[rsmpIndex] = ChunkHeaderSize + provisional.Length;
        }
        var offsets = new int[chunks.Count];
        var total = HeaderSize;
        for (var index = 0; index < chunks.Count; index++)
        {
            offsets[index] = total;
            total += sizes[index];
        }
        var output = new byte[total];
        Bytes.Span[..HeaderSize].CopyTo(output);
        if (rsmpIndex >= 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(60, 4), (uint)offsets[rsmpIndex]);
            bodies[rsmpIndex] = ResourceMap.Write(IndexEntries(chunks, originalIndex, offsets));
            sizes[rsmpIndex] = ChunkHeaderSize + bodies[rsmpIndex].Length;
        }
        total = HeaderSize;
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            var header = output.AsSpan(total, ChunkHeaderSize);
            header.Clear();
            BinaryPrimitives.WriteUInt32BigEndian(header, chunk.Type.Code);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)sizes[index]);
            BinaryPrimitives.WriteUInt16BigEndian(header[8..], chunk.Id);
            BinaryPrimitives.WriteUInt16BigEndian(header[10..], chunk.Flags);
            var label = System.Text.Encoding.Latin1.GetBytes(chunk.Label);
            label.AsSpan(0, Math.Min(label.Length, 64)).CopyTo(header[12..]);
            bodies[index].Span.CopyTo(output.AsSpan(total + ChunkHeaderSize));
            total += sizes[index];
        }
        return new IffFile(output, Source);
    }

    private static IReadOnlyList<(ResourceType Type, ushort Id, ushort Flags, string Label, int Offset)> IndexEntries(
        IReadOnlyList<IffChunk> chunks, IReadOnlyList<ResourceMapGroup>? originalIndex, int[]? offsets)
    {
        var typeRank = new Dictionary<ResourceType, int>();
        var entryRank = new Dictionary<(ResourceType, ushort), int>();
        if (originalIndex is not null)
        {
            for (var group = 0; group < originalIndex.Count; group++)
            {
                typeRank.TryAdd(originalIndex[group].Type, group);
                for (var entry = 0; entry < originalIndex[group].Entries.Count; entry++)
                    entryRank.TryAdd((originalIndex[group].Type, originalIndex[group].Entries[entry].Id), entry);
            }
        }
        var entries = new List<(ResourceType Type, ushort Id, ushort Flags, string Label, int Offset)>();
        for (var index = 0; index < chunks.Count; index++)
        {
            if (chunks[index].Type == ResourceType.Rsmp) continue;
            entries.Add((chunks[index].Type, chunks[index].Id, chunks[index].Flags, chunks[index].Label,
                offsets?[index] ?? 0));
        }
        return entries
            .OrderBy(entry => typeRank.GetValueOrDefault(entry.Type, int.MaxValue))
            .ThenBy(entry => entryRank.GetValueOrDefault((entry.Type, entry.Id), int.MaxValue))
            .ToList();
    }

    public IffFile WithChunkData(ResourceType type, ushort id, ReadOnlyMemory<byte> replacement)
    {
        var chunk = Find(type, id) ?? throw new KeyNotFoundException($"{Source}: no {type} chunk #{id} to replace.");
        if (replacement.Length != chunk.Data.Length)
            throw new ArgumentException($"Replacement for {chunk} is {replacement.Length} bytes; the original body is {chunk.Data.Length} bytes.", nameof(replacement));
        var bytes = Bytes.ToArray();
        replacement.Span.CopyTo(bytes.AsSpan(chunk.Offset + ChunkHeaderSize, chunk.Data.Length));
        return new IffFile(bytes, Source);
    }
}
