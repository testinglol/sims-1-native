using SimsEngine.Formats;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace SimsEngine.Content;

public enum TreeScope
{
    /// <summary>Designates action primitives (IDs 0–255 and unmapped values outside tree ranges).</summary>
    Primitive,
    /// <summary>Designates global routines from Global.iff (IDs 256–4095).</summary>
    Global,
    /// <summary>Designates private routines defined in the object's own resource file (IDs 4096–8191).</summary>
    Private,
    /// <summary>Designates semi-global routines defined in the object class's shared resource file (IDs
    /// 8192–9000).</summary>
    SemiGlobal,
}

/// <summary>Resolves behavior trees across global, private, and semi-global resource files based on tree ID
/// numerical ranges.</summary>
public sealed record BehaviorScope(IffFile Global, IffFile Private, IffFile? SemiGlobal) : Simulation.ITreeTable
{
    // Shares BCON tuning overrides across definitions from the same resource file.
    private static readonly ConditionalWeakTable<IffFile, Dictionary<ushort, short[]>> ConstantOverrides = new();
    private static readonly ConditionalWeakTable<IffFile, ConcurrentDictionary<ushort, Bhav?>> BhavCache = new();
    private static readonly ConditionalWeakTable<IffFile, ConcurrentDictionary<ushort, Bcon?>> BconCache = new();
    public int? ObjectKey { get; init; }
    public ushort AnimationTableId { get; init; }
    private static readonly StringComparison[] NamePasses =
    [
        // Resource chunk name lookups are case-insensitive across search passes.
        StringComparison.OrdinalIgnoreCase,
        StringComparison.OrdinalIgnoreCase,
    ];
    public static TreeScope ScopeOf(short id) => id switch
    {
        >= 0x100 and < 0x1000 => TreeScope.Global,
        >= 0x1000 and < 0x2000 => TreeScope.Private,
        >= 0x2000 and < 0x2329 => TreeScope.SemiGlobal,
        _ => TreeScope.Primitive,
    };

    public IffFile? FileFor(short id) => ScopeOf(id) switch
    {
        TreeScope.Global => Global,
        TreeScope.Private => Private,
        TreeScope.SemiGlobal => SemiGlobal,
        _ => null,
    };

    private static readonly HashSet<string> LoggedMalformedChunks = new(StringComparer.OrdinalIgnoreCase);

    private static void LogMalformedChunkOnce(IffFile file, string kind, ushort id, Exception error)
    {
        string key = $"{file.Source}#{kind}:{id}";
        lock (LoggedMalformedChunks)
        {
            if (!LoggedMalformedChunks.Add(key))
                return;
        }
        Console.Error.WriteLine($"Native content: skipping malformed {kind} #{id} in {file.Source}: {error.Message}");
    }

    /// <summary>Loads string bytes by searching STR# resources first and falling back to CTSS, returning
    /// the one-based entry for the specified language.</summary>
    public byte[]? StringTableBytes(short treeId, ushort tableId, int oneBasedIndex, byte language)
    {
        var file = FileFor(treeId);
        if (oneBasedIndex <= 0 || file is null) return null;
        var chunk = file.Find(ResourceType.Str, tableId) ?? file.Find(ResourceType.Ctss, tableId);
        if (chunk is null) return null;

        try
        {
            var table = StringTable.Read(file, chunk);
            if (!table.ReadBySimsExe) return null;
            var strings = table.ForLanguage(language);
            return oneBasedIndex <= strings.Count
                ? ResourceReader.EncodingForLanguage(language == 0 ? (byte)1 : language).GetBytes(strings[oneBasedIndex - 1])
                : null;
        }
        catch (ResourceFormatException error)
        {
            LogMalformedChunkOnce(file, "STR#", tableId, error);
            return null;
        }
    }

    public Bhav? Find(short id)
    {
        var file = FileFor(id);
        if (file is null) return null;
        var uid = (ushort)id;
        var cache = BhavCache.GetOrCreateValue(file);
        if (cache.TryGetValue(uid, out var cached)) return cached;
        var chunk = file.Find(ResourceType.Bhav, uid);
        if (chunk is null)
        {
            cache.TryAdd(uid, null);
            return null;
        }
        try
        {
            var decoded = Bhav.Read(file, chunk);
            cache.TryAdd(uid, decoded);
            return decoded;
        }
        catch (ResourceFormatException error)
        {
            LogMalformedChunkOnce(file, "BHAV", uid, error);
            cache.TryAdd(uid, null);
            return null;
        }
    }

    /// <summary>Searches private, semi-global, and global scopes for a BCON resource by ID, applying
    /// in-memory tuning overrides only to private object resources when permitted.</summary>
    public IReadOnlyList<short>? Constants(ushort id, bool allowTuningOverride = false)
    {
        var file = id switch
        {
            >= 0x1000 and < 0x2000 => Private,
            >= 0x2000 and < 0x2329 => SemiGlobal,
            >= 0x100 and < 0x1000 => Global,
            _ => null,
        };
        if (file is null) return null;
        var cache = BconCache.GetOrCreateValue(file);
        if (!cache.TryGetValue(id, out var bcon))
        {
            var chunk = file.Find(ResourceType.Bcon, id);
            if (chunk is null)
            {
                cache.TryAdd(id, null);
                return null;
            }
            try
            {
                bcon = Bcon.Read(file, chunk);
                cache.TryAdd(id, bcon);
            }
            catch (ResourceFormatException error)
            {
                LogMalformedChunkOnce(file, "BCON", id, error);
                cache.TryAdd(id, null);
                return null;
            }
        }
        if (bcon is null) return null;
        if (allowTuningOverride && bcon.TuningOverride && ReferenceEquals(file, Private) &&
            ConstantOverrides.TryGetValue(Private, out var overrides))
        {
            lock (overrides)
                if (overrides.TryGetValue(id, out var values)) return values;
        }
        return bcon.Constants;
    }

    public void SetConstantOverride(ushort id, IReadOnlyList<short>? values)
        => SetConstantOverrideForFile(Private, id, values);

    internal static void SetConstantOverrideForFile(IffFile privateFile, ushort id, IReadOnlyList<short>? values)
    {
        var overrides = ConstantOverrides.GetOrCreateValue(privateFile);
        lock (overrides)
        {
            if (values is null) overrides.Remove(id);
            else overrides[id] = values.ToArray();
        }
    }

    public void ClearConstantOverrides()
        => ClearConstantOverridesForFile(Private);

    internal static void ClearConstantOverridesForFile(IffFile privateFile)
    {
        if (!ConstantOverrides.TryGetValue(privateFile, out var overrides)) return;
        lock (overrides) overrides.Clear();
    }

    /// <summary>Searches for a behavior tree by label across private, semi-global, and global scopes in
    /// order, returning the first matching chunk ID.</summary>
    public short FindNamedTree(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        var files = new[] { Private, SemiGlobal, Global };
        foreach (var comparison in NamePasses)
        foreach (var file in files)
        {
            if (file is null) continue;
            var chunk = file.OfType(ResourceType.Bhav)
                .FirstOrDefault(candidate => string.Equals(candidate.Label, name, comparison));
            if (chunk is not null) return (short)chunk.Id;
        }
        return 0;
    }
}

/// <summary>Manages the global library containing Global.iff and semi-global files referenced by object
/// GLOB chunks, resolving files loosely or from archives.</summary>
public sealed class GlobalLibrary
{
    private readonly Dictionary<string, IffFile?> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<ReadOnlyMemory<byte>>> _sources = new(StringComparer.OrdinalIgnoreCase);

    public string? Folder { get; }
    public IffFile? Global { get; }
    public List<(string Name, string Error)> Missing { get; } = new();

    private GlobalLibrary(string? folder)
    {
        Folder = folder;
        if (folder is null) return;
        foreach (var path in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (path.EndsWith(".far", StringComparison.OrdinalIgnoreCase))
            {
                var archive = FarArchive.Open(path);
                foreach (var entry in archive.Entries)
                    _sources.TryAdd(Path.GetFileName(entry.Name.Replace('\\', '/')), () => archive.Read(entry));
            }
            else _sources[Path.GetFileName(path)] = () => File.ReadAllBytes(path);
        }
        Global = Load("Global");
    }

    /// <summary>Opens the global directory, resolving either GameData/Global or a root Global folder for
    /// flat installations.</summary>
    public static GlobalLibrary Open(string game) => new(
        ObjectRegistry.Resolve(game, @"GameData\Global") ?? ObjectRegistry.Resolve(game, "Global"));

    public IffFile? Load(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = Path.HasExtension(name) ? name : name + ".iff";
        if (_files.TryGetValue(key, out var file)) return file;
        if (!_sources.TryGetValue(key, out var source)) { Missing.Add((name, "not found")); return _files[key] = null; }
        try { file = new IffFile(source(), key); }
        catch (ResourceFormatException error) { Missing.Add((name, error.Message)); file = null; }
        return _files[key] = file;
    }

    public BehaviorScope? ScopeOf(ObjectEntry entry) =>
        Global is null ? null : new BehaviorScope(Global, entry.File, entry.SemiGlobal is { } name ? Load(name) : null)
        {
            ObjectKey = unchecked((int)entry.Guid),
            AnimationTableId = entry.Definition[ObjdField.AnimTableId],
        };
}
