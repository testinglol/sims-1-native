using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;

namespace SimsEngine.Formats;

/// <summary>Represents a version 1 FAR archive file container ("FAR!byAZ"). The header contains an 8-byte
/// magic identifier, a version integer, and a manifest file offset pointing to entry descriptors with
/// sizes, data offsets, and filename strings.</summary>
public sealed class FarArchive
{
    public sealed record Entry(string Name, int Offset, int Size);

    private static ReadOnlySpan<byte> Signature => "FAR!byAZ"u8;
    private static readonly ConcurrentDictionary<string, (long Length, long LastWriteTicks, FarArchive Archive)> OpenCache =
        new(StringComparer.Ordinal);

    private readonly ReadOnlyMemory<byte> _data;
    private readonly SafeFileHandle? _handle;
    private readonly long _fileLength;
    private Dictionary<string, Entry>? _entriesByNormalizedName;
    public string Source { get; }
    public IReadOnlyList<Entry> Entries { get; }

    public FarArchive(ReadOnlyMemory<byte> data, string source)
    {
        _data = data;
        _fileLength = data.Length;
        Source = source;
        var reader = new ResourceReader(data.Span, source);
        var manifest = ReadHeader(reader, data.Length);
        reader.Position = (int)manifest;
        Entries = ReadManifest(reader, data.Length);
    }

    private FarArchive(SafeFileHandle handle, long fileLength, string source)
    {
        _handle = handle;
        _fileLength = fileLength;
        Source = source;
        if (fileLength < 16)
        {
            var smallReader = new ResourceReader(ReadExact(handle, 0, (int)Math.Max(0, fileLength)), source);
            _ = ReadHeader(smallReader, fileLength);
        }

        Span<byte> header = stackalloc byte[16];
        ReadExact(handle, 0, header);
        var headerReader = new ResourceReader(header, source);
        var manifest = ReadHeader(headerReader, fileLength);

        var manifestSize = fileLength - manifest;
        if (manifestSize > int.MaxValue)
            throw headerReader.Error($"manifest size {manifestSize} exceeds supported limits");
        var manifestBytes = GC.AllocateUninitializedArray<byte>((int)manifestSize);
        ReadExact(handle, manifest, manifestBytes);
        var manifestReader = new ResourceReader(manifestBytes, source);
        Entries = ReadManifest(manifestReader, fileLength);
    }

    private static uint ReadHeader(ResourceReader reader, long fileLength)
    {
        if (!reader.Bytes(8).SequenceEqual(Signature)) throw reader.Error("not a FAR archive");
        var version = reader.U32();
        if (version != 1) throw reader.Error($"FAR version {version} is not supported");
        var manifest = reader.U32();
        if (manifest < 16 || manifest > fileLength - 4) throw reader.Error($"manifest offset {manifest} is outside the file");
        return manifest;
    }

    private static List<Entry> ReadManifest(ResourceReader reader, long fileLength)
    {
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
            if (offset > (ulong)fileLength || size > (ulong)fileLength - offset)
                throw reader.Error($"entry {name} ({offset}+{size}) is outside the file");
            entries.Add(new Entry(name, (int)offset, (int)size));
        }
        return entries;
    }

    private static byte[] ReadExact(SafeFileHandle handle, long offset, int length)
    {
        if (length == 0) return [];
        var buffer = GC.AllocateUninitializedArray<byte>(length);
        ReadExact(handle, offset, buffer);
        return buffer;
    }

    private static void ReadExact(SafeFileHandle handle, long offset, Span<byte> destination)
    {
        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            var read = RandomAccess.Read(handle, destination[totalRead..], offset + totalRead);
            if (read == 0)
                throw new EndOfStreamException($"Unexpected end of FAR archive at offset {offset + totalRead}.");
            totalRead += read;
        }
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
            var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                var opened = new FarArchive(handle, length, path);
                OpenCache[fullPath] = (length, ticks, opened);
                return opened;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        return new FarArchive(File.ReadAllBytes(path), path);
    }

    public ReadOnlyMemory<byte> Read(Entry entry)
    {
        if (_handle is { } handle)
        {
            if (entry.Size == 0) return ReadOnlyMemory<byte>.Empty;
            if ((uint)entry.Offset > (ulong)_fileLength || (uint)entry.Size > (ulong)_fileLength - (uint)entry.Offset)
                throw new ArgumentOutOfRangeException(nameof(entry), $"entry {entry.Name} ({entry.Offset}+{entry.Size}) is outside {Source}");
            return ReadExact(handle, (uint)entry.Offset, entry.Size);
        }

        return _data.Slice(entry.Offset, entry.Size);
    }

    internal ReadOnlyMemory<byte> ReadSlice(int offset, int length)
    {
        if (length == 0) return ReadOnlyMemory<byte>.Empty;
        if ((uint)offset > (ulong)_fileLength || (uint)length > (ulong)_fileLength - (uint)offset)
            throw new ArgumentOutOfRangeException(nameof(offset), $"slice ({offset}+{length}) is outside {Source}");
        if (_handle is { } handle)
            return ReadExact(handle, (uint)offset, length);
        return _data.Slice(offset, length);
    }

    internal byte[] ReadSliceBytes(int offset, int length)
    {
        if (length == 0) return [];
        if ((uint)offset > (ulong)_fileLength || (uint)length > (ulong)_fileLength - (uint)offset)
            throw new ArgumentOutOfRangeException(nameof(offset), $"slice ({offset}+{length}) is outside {Source}");
        if (_handle is { } handle)
            return ReadExact(handle, (uint)offset, length);
        return _data.Span.Slice(offset, length).ToArray();
    }

    internal void ReadSpan(int offset, Span<byte> destination)
    {
        if (destination.IsEmpty) return;
        if ((uint)offset > (ulong)_fileLength || (uint)destination.Length > (ulong)_fileLength - (uint)offset)
            throw new ArgumentOutOfRangeException(nameof(offset), $"span ({offset}+{destination.Length}) is outside {Source}");
        if (_handle is { } handle)
        {
            ReadExact(handle, (uint)offset, destination);
            return;
        }
        _data.Span.Slice(offset, destination.Length).CopyTo(destination);
    }

    /// <summary>Opens an IFF entry, eagerly loading metadata chunks while lazily reading large graphic and
    /// audio chunk bodies on demand.</summary>
    public IffFile ReadIff(Entry entry, string? sourceOverride = null)
    {
        var source = sourceOverride ?? $"{Source}:{entry.Name}";
        if (_handle is null)
            return new IffFile(_data.Slice(entry.Offset, entry.Size), source);
        if ((uint)entry.Offset > (ulong)_fileLength || (uint)entry.Size > (ulong)_fileLength - (uint)entry.Offset)
            throw new ArgumentOutOfRangeException(nameof(entry), $"entry {entry.Name} ({entry.Offset}+{entry.Size}) is outside {Source}");
        return IffFile.FromFarEntry(this, entry, source);
    }

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
