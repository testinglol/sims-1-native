using System.Collections.Concurrent;

namespace SimsEngine.Formats;

/// <summary>A FAR archive ("FAR!byAZ", version 1): the game's container for IFF, sprite, sound and text files.
/// Every FAR in the base game and the seven expansion packs uses this layout: an 8-byte signature, a u32 version,
/// a u32 manifest offset; the manifest is a u32 count followed by entries of u32 size, u32 size again, u32 offset,
/// u32 name length and the name.</summary>
public sealed class FarArchive
{
    public sealed record Entry(string Name, int Offset, int Size);

    private static ReadOnlySpan<byte> Signature => "FAR!byAZ"u8;
    private static readonly ConcurrentDictionary<string, (long Length, long LastWriteTicks, FarArchive Archive)> OpenCache =
        new(StringComparer.Ordinal);

    private readonly ReadOnlyMemory<byte> _data;
    private Dictionary<string, Entry>? _entriesByNormalizedName;
    public string Source { get; }
    public IReadOnlyList<Entry> Entries { get; }

    public FarArchive(ReadOnlyMemory<byte> data, string source)
    {
        _data = data;
        Source = source;
        var reader = new ResourceReader(data.Span, source);
        if (!reader.Bytes(8).SequenceEqual(Signature)) throw reader.Error("not a FAR archive");
        var version = reader.U32();
        if (version != 1) throw reader.Error($"FAR version {version} is not supported");
        var manifest = reader.U32();
        if (manifest < 16 || manifest > data.Length - 4) throw reader.Error($"manifest offset {manifest} is outside the file");
        reader.Position = (int)manifest;
        var count = reader.U32();
        if (count > (uint)reader.Remaining / 16) throw reader.Error($"{count} entries cannot fit in the manifest");
        var entries = new List<Entry>((int)count);
        for (var index = 0; index < count; index++)
        {
            var size = reader.U32();
            var storedSize = reader.U32();
            var offset = reader.U32();
            var nameLength = reader.U32();
            if (nameLength > (uint)reader.Remaining) throw reader.Error($"entry {index} name length {nameLength} runs past the manifest");
            var name = ResourceReader.Latin1(reader.Bytes((int)nameLength));
            if (size != storedSize) throw reader.Error($"entry {name} has two different sizes ({size}, {storedSize})");
            if (offset > (uint)data.Length || size > (uint)data.Length - offset)
                throw reader.Error($"entry {name} ({offset}+{size}) is outside the file");
            entries.Add(new Entry(name, (int)offset, (int)size));
        }
        Entries = entries;
    }

    public static FarArchive Open(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (info.Exists)
        {
            var length = info.Length;
            var ticks = info.LastWriteTimeUtc.Ticks;
            if (OpenCache.TryGetValue(fullPath, out var cached) && cached.Length == length && cached.LastWriteTicks == ticks)
                return cached.Archive;
            var opened = new FarArchive(File.ReadAllBytes(fullPath), path);
            OpenCache[fullPath] = (length, ticks, opened);
            return opened;
        }
        return new FarArchive(File.ReadAllBytes(path), path);
    }

    public ReadOnlyMemory<byte> Read(Entry entry) => _data.Slice(entry.Offset, entry.Size);

    /// <summary>The entry whose name matches, ignoring case and the folder separator style.</summary>
    public Entry? Find(string name)
    {
        var index = _entriesByNormalizedName;
        if (index is null)
        {
            var built = new Dictionary<string, Entry>(Entries.Count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < Entries.Count; i++)
            {
                var entry = Entries[i];
                var key = entry.Name.Contains('\\') ? entry.Name.Replace('\\', '/') : entry.Name;
                built.TryAdd(key, entry);
            }
            index = Interlocked.CompareExchange(ref _entriesByNormalizedName, built, null) ?? built;
        }
        var wanted = name.Contains('\\') ? name.Replace('\\', '/') : name;
        return index.GetValueOrDefault(wanted);
    }
}
