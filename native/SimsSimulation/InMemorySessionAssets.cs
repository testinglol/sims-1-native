using System.Collections.Concurrent;

namespace SimsSimulation;

/// <summary>Stores in-memory scene manifests and rasterized sprite, floor, and wall buffers to avoid disk
/// writes during in-process hosting.</summary>
internal static class InMemorySessionAssets
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed class Entry
    {
        private byte[]? _bytes;
        private Func<byte[]>? _factory;

        public Entry(byte[] bytes, object? payload = null)
        {
            _bytes = bytes;
            Payload = payload;
        }

        public Entry(object? payload, Func<byte[]> factory)
        {
            Payload = payload;
            _factory = factory;
        }

        public object? Payload { get; }
        public int ByteCount => Volatile.Read(ref _bytes)?.Length ?? 0;

        public byte[] GetBytes()
        {
            var existing = Volatile.Read(ref _bytes);
            if (existing is not null)
            {
                return existing;
            }

            var factory = _factory;
            if (factory is null)
            {
                return [];
            }

            var materialized = factory();
            var prior = Interlocked.CompareExchange(ref _bytes, materialized, null);
            _factory = null;
            return prior ?? materialized;
        }
    }

    private static readonly Lock RootsLock = new();
    private static readonly List<string> EnabledRoots = [];
    private static readonly ConcurrentDictionary<string, Entry> Files = new(PathComparer);

    public static void EnableRoot(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        var normalized = NormalizeDirectoryPrefix(rootDirectory);
        lock (RootsLock)
        {
            if (!EnabledRoots.Contains(normalized, PathComparer))
            {
                EnabledRoots.Add(normalized);
            }
        }
    }

    public static void DisableAndClearRoot(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return;
        }

        var normalized = NormalizeDirectoryPrefix(rootDirectory);
        lock (RootsLock)
        {
            EnabledRoots.RemoveAll(existing => string.Equals(existing, normalized, PathComparison));
        }

        foreach (var key in Files.Keys)
        {
            if (IsUnderRoot(key, normalized))
            {
                Files.TryRemove(key, out _);
            }
        }
    }

    public static bool IsInMemoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var full = Canonicalize(path);
        lock (RootsLock)
        {
            foreach (var root in EnabledRoots)
            {
                if (IsUnderRoot(full, root))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static void StoreFile(string path, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(bytes);
        Files[Canonicalize(path)] = new Entry(bytes);
    }

    public static void StoreScene(string path, object? payload, Func<byte[]> utf8JsonFactory)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(utf8JsonFactory);
        Files[Canonicalize(path)] = new Entry(payload, utf8JsonFactory);
    }

    public static bool TryGetPayload<T>(string path, out T? payload) where T : class
    {
        if (!string.IsNullOrWhiteSpace(path) && Files.TryGetValue(Canonicalize(path), out var entry) && entry.Payload is T typed)
        {
            payload = typed;
            return true;
        }

        payload = null;
        return false;
    }

    public static bool TryReadBytes(string path, out byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            bytes = [];
            return false;
        }

        if (Files.TryGetValue(Canonicalize(path), out var entry))
        {
            bytes = entry.GetBytes();
            return true;
        }

        bytes = [];
        return false;
    }

    public static bool FileExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return Files.ContainsKey(Canonicalize(path)) || File.Exists(path);
    }

    public static bool ContainsFile(string path) =>
        !string.IsNullOrWhiteSpace(path) && Files.ContainsKey(Canonicalize(path));

    public static byte[] ReadAllBytes(string path)
    {
        if (TryReadBytes(path, out var bytes))
        {
            return bytes;
        }

        return File.ReadAllBytes(path);
    }

    public static void DeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Files.TryRemove(Canonicalize(path), out _);
    }

    public static long TotalBytesUnder(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return 0;
        }

        var dirPrefix = NormalizeDirectoryPrefix(directory);
        long total = 0;
        foreach (var (key, entry) in Files)
        {
            if (IsUnderRoot(key, dirPrefix))
            {
                total += entry.ByteCount;
            }
        }

        return total;
    }

    public static long TotalDirectBytesIn(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return 0;
        }

        var dirPrefix = NormalizeDirectoryPrefix(directory);
        long total = 0;
        foreach (var (key, entry) in Files)
        {
            if (key.StartsWith(dirPrefix, PathComparison) &&
                key.IndexOf(Path.DirectorySeparatorChar, dirPrefix.Length) < 0 &&
                key.IndexOf(Path.AltDirectorySeparatorChar, dirPrefix.Length) < 0)
            {
                total += entry.ByteCount;
            }
        }

        return total;
    }

    public static void RetainOnly(string directory, IReadOnlySet<string> keepFileNames)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var dirPrefix = NormalizeDirectoryPrefix(directory);
        foreach (var key in Files.Keys)
        {
            if (!IsUnderRoot(key, dirPrefix))
            {
                continue;
            }

            var fileName = Path.GetFileName(key);
            if (fileName.StartsWith("scene-", PathComparison) && fileName.EndsWith(".json", PathComparison))
            {
                continue;
            }

            if (!keepFileNames.Contains(fileName))
            {
                Files.TryRemove(key, out _);
            }
        }
    }

    public static void PruneOldScenes(string directory, long keepFromRevision)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var dirPrefix = NormalizeDirectoryPrefix(directory);
        foreach (var key in Files.Keys)
        {
            if (!IsUnderRoot(key, dirPrefix))
            {
                continue;
            }

            var fileName = Path.GetFileName(key);
            if (!fileName.StartsWith("scene-", PathComparison) ||
                !fileName.EndsWith(".json", PathComparison))
            {
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(fileName);
            if (stem.Length > 6 &&
                long.TryParse(stem.AsSpan(6), out var revision) &&
                revision < keepFromRevision)
            {
                Files.TryRemove(key, out _);
            }
        }
    }

    private static bool IsUnderRoot(string candidatePath, string normalizedPrefix)
    {
        var trimmed = normalizedPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidatePath.StartsWith(normalizedPrefix, PathComparison) ||
               string.Equals(candidatePath, trimmed, PathComparison);
    }

    private static string NormalizeDirectoryPrefix(string path)
    {
        var canonical = Canonicalize(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return canonical + Path.DirectorySeparatorChar;
    }

    internal static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(full))
        {
            return Path.DirectorySeparatorChar.ToString();
        }

        try
        {
            var stack = new Stack<string>();
            var probe = full;
            while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe) && !File.Exists(probe))
            {
                var parent = Path.GetDirectoryName(probe);
                var leaf = Path.GetFileName(probe);
                if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf) || string.Equals(parent, probe, PathComparison))
                {
                    break;
                }

                stack.Push(leaf);
                probe = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            var resolved = ResolveExistingRealPath(probe);
            while (stack.Count > 0)
            {
                resolved = Path.Combine(resolved, stack.Pop());
            }

            return Path.GetFullPath(resolved).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return full;
        }
    }

    private static string ResolveExistingRealPath(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.GetDirectoryName(fullPath);
        var leaf = Path.GetFileName(fullPath);
        if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(leaf))
        {
            fullPath = Path.Combine(ResolveExistingRealPath(parent), leaf);
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var attrs = File.GetAttributes(fullPath);
                if (attrs == (FileAttributes)(-1) || (attrs & FileAttributes.ReparsePoint) == 0)
                {
                    return fullPath;
                }
                var link = (attrs & FileAttributes.Directory) != 0
                    ? Directory.ResolveLinkTarget(fullPath, returnFinalTarget: true)
                    : File.ResolveLinkTarget(fullPath, returnFinalTarget: true);
                return Path.TrimEndingDirectorySeparator(link?.FullName ?? fullPath);
            }
            catch
            {
                return fullPath;
            }
        }

        if (File.Exists(fullPath))
        {
            return Path.TrimEndingDirectorySeparator(
                File.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName ?? fullPath);
        }

        if (Directory.Exists(fullPath))
        {
            return Path.TrimEndingDirectorySeparator(
                Directory.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName ?? fullPath);
        }

        return fullPath;
    }
}
