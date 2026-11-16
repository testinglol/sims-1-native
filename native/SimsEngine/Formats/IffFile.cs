using System.Buffers;
using System.Buffers.Binary;

namespace SimsEngine.Formats;

/// <summary>Represents a four-character IFF resource type code stored as a big-endian 32-bit integer.</summary>
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
    public static readonly ResourceType Bmp = Parse("BMP_");
    public static readonly ResourceType Palt = Parse("PALT");
    public static readonly ResourceType Dgrp = Parse("DGRP");
    public static readonly ResourceType Fcns = Parse("FCNS");
    public static readonly ResourceType Fwav = Parse("FWAV");
    public static readonly ResourceType NativeLifecycleCapability = Parse("NLCF");

    public static ResourceType Parse(string text)
    {
        if (text.Length != 4) throw new ArgumentException($"A resource type has four characters: {text}", nameof(text));
        return new ResourceType((uint)(text[0] << 24 | text[1] << 16 | text[2] << 8 | text[3]));
    }

    public override string ToString() => string.Concat((char)(Code >> 24), (char)(Code >> 16 & 0xff), (char)(Code >> 8 & 0xff), (char)(Code & 0xff));
}

/// <summary>Represents a single IFF resource chunk, storing its body data excluding the 76-byte header.</summary>
public sealed record IffChunk
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly FarArchive? _lazyArchive;
    private readonly int _lazyOffset;
    private readonly int _lazyLength;
    private WeakReference<byte[]>? _weakData;

    public IffChunk(ResourceType type, ushort id, ushort flags, string label, int offset, ReadOnlyMemory<byte> data)
    {
        Type = type;
        Id = id;
        Flags = flags;
        Label = label;
        Offset = offset;
        _data = data;
    }

    internal IffChunk(ResourceType type, ushort id, ushort flags, string label, int offset, FarArchive lazyArchive, int lazyOffset, int lazyLength)
    {
        Type = type;
        Id = id;
        Flags = flags;
        Label = label;
        Offset = offset;
        _lazyArchive = lazyArchive;
        _lazyOffset = lazyOffset;
        _lazyLength = lazyLength;
    }

    public ResourceType Type { get; init; }
    public ushort Id { get; init; }
    public ushort Flags { get; init; }
    public string Label { get; init; }
    public int Offset { get; init; }

    public ReadOnlyMemory<byte> Data
    {
        get
        {
            if (_lazyArchive is not { } archive)
                return _data;
            var weak = Volatile.Read(ref _weakData);
            if (weak is not null && weak.TryGetTarget(out var cached))
                return cached;
            var bytes = archive.ReadSliceBytes(_lazyOffset, _lazyLength);
            Volatile.Write(ref _weakData, new WeakReference<byte[]>(bytes));
            return bytes;
        }
        init
        {
            _data = value;
            _lazyArchive = null;
            _lazyOffset = 0;
            _lazyLength = 0;
            _weakData = null;
        }
    }

    public bool Equals(IffChunk? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        return Type == other.Type &&
               Id == other.Id &&
               Flags == other.Flags &&
               string.Equals(Label, other.Label, StringComparison.Ordinal) &&
               Offset == other.Offset &&
               (_lazyArchive is not null || other._lazyArchive is not null
                   ? ReferenceEquals(_lazyArchive, other._lazyArchive) &&
                     _lazyOffset == other._lazyOffset &&
                     _lazyLength == other._lazyLength
                   : _data.Equals(other._data));
    }

    public override int GetHashCode() =>
        HashCode.Combine(Type, Id, Flags, Label, Offset, _lazyArchive, _lazyOffset, _lazyLength);

    public override string ToString() => $"{Type} #{Id}";
}

/// <summary>Represents an IFF resource archive ("IFF FILE 2.0" or "2.5"). The file begins with a 64-byte
/// header containing a signature and a big-endian offset to the resource map (rsmp), followed by chunks
/// with 76-byte headers and little-endian payloads.</summary>
public sealed class IffFile
{
    public const int HeaderSize = 64;
    public const int ChunkHeaderSize = 76;
    private const int LazyChunkThresholdBytes = 4096;

    private readonly FarArchive? _lazyArchive;
    private readonly FarArchive.Entry? _lazyEntry;
    private ReadOnlyMemory<byte> _bytes;
    private Dictionary<ulong, IffChunk>? _chunksByTypeAndId;

    public string Source { get; }
    public ReadOnlyMemory<byte> Bytes => _lazyArchive is not null && _lazyEntry is not null && _bytes.IsEmpty && _lazyEntry.Size > 0
        ? (_bytes = _lazyArchive.Read(_lazyEntry))
        : _bytes;
    public IReadOnlyList<IffChunk> Chunks { get; }

    public IffFile(ReadOnlyMemory<byte> data, string source)
    {
        _bytes = data;
        Source = source;
        Chunks = ParseChunks(data.Span, data, null, 0, source);
    }

    private IffFile(FarArchive lazyArchive, FarArchive.Entry lazyEntry, string source, List<IffChunk> chunks)
    {
        _lazyArchive = lazyArchive;
        _lazyEntry = lazyEntry;
        Source = source;
        Chunks = chunks;
    }

    internal static IffFile FromFarEntry(FarArchive archive, FarArchive.Entry entry, string source)
    {
        if (entry.Size == 0)
            throw new ResourceFormatException(source, null, 0, "not an IFF file");

        var rented = ArrayPool<byte>.Shared.Rent(entry.Size);
        try
        {
            var span = rented.AsSpan(0, entry.Size);
            archive.ReadSpan(entry.Offset, span);
            var chunks = ParseChunks(span, ReadOnlyMemory<byte>.Empty, archive, entry.Offset, source);
            return new IffFile(archive, entry, source, chunks);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static List<IffChunk> ParseChunks(ReadOnlySpan<byte> span, ReadOnlyMemory<byte> backingMemory, FarArchive? lazyArchive, int archiveEntryOffset, string source)
    {
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
            var bodyLength = (int)size - ChunkHeaderSize;
            if (lazyArchive is null)
            {
                chunks.Add(new IffChunk(type, id, flags, label, offset, backingMemory.Slice(offset + ChunkHeaderSize, bodyLength)));
            }
            else if (ShouldKeepLazy(type, bodyLength))
            {
                chunks.Add(new IffChunk(type, id, flags, label, offset, lazyArchive, archiveEntryOffset + offset + ChunkHeaderSize, bodyLength));
            }
            else
            {
                var copy = bodyLength == 0
                    ? ReadOnlyMemory<byte>.Empty
                    : span.Slice(offset + ChunkHeaderSize, bodyLength).ToArray();
                chunks.Add(new IffChunk(type, id, flags, label, offset, copy));
            }
            offset += (int)size;
        }
        return chunks;
    }

    private static bool ShouldKeepLazy(ResourceType type, int bodyLength) =>
        bodyLength > LazyChunkThresholdBytes ||
        type == ResourceType.Spr2 ||
        type == ResourceType.Spr1 ||
        type == ResourceType.Bmp ||
        type == ResourceType.Dgrp ||
        type == ResourceType.Fwav ||
        type == ResourceType.Rsmp;

    public static IffFile Open(string path) => new(File.ReadAllBytes(path), path);

    public IEnumerable<IffChunk> OfType(ResourceType type) => Chunks.Where(chunk => chunk.Type == type);

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

    public IffFile WithChunkDataResized(ResourceType type, ushort id, ReadOnlyMemory<byte> replacement)
    {
        var chunks = Chunks.ToList();
        var found = chunks.FindIndex(chunk => chunk.Type == type && chunk.Id == id);
        if (found < 0) throw new KeyNotFoundException($"{Source}: no {type} chunk #{id} to replace.");
        chunks[found] = chunks[found] with { Data = replacement };
        return Rebuild(chunks);
    }

    public IffFile WithAddedChunk(ResourceType type, ushort id, ReadOnlyMemory<byte> data, ushort flags = 0, string label = "")
    {
        if (Find(type, id) is not null) throw new ArgumentException($"{Source}: {type} chunk #{id} already exists.", nameof(id));
        return Rebuild([.. Chunks, new IffChunk(type, id, flags, label, 0, data)]);
    }

    public IffFile WithAddedOrReplacedChunk(ResourceType type, ushort id, ReadOnlyMemory<byte> data, ushort flags = 0, string label = "") =>
        Find(type, id) is not null
            ? WithChunkDataResized(type, id, data)
            : WithAddedChunk(type, id, data, flags, label);

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
