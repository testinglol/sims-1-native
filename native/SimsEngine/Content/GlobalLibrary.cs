using SimsEngine.Formats;
using System.Runtime.CompilerServices;

namespace SimsEngine.Content;

/// <summary>Which file a tree id names, as Sims.exe FUN_004021ff classifies it.</summary>
public enum TreeScope
{
    /// <summary>0–255, and every id outside the ranges below: a primitive, not a tree.</summary>
    Primitive,
    /// <summary>256–4095: Global.iff.</summary>
    Global,
    /// <summary>4096–8191: the object's own file ("Private").</summary>
    Private,
    /// <summary>8192–9000: the object's semi-global file.</summary>
    SemiGlobal,
}

/// <summary>The trees one object can call: Global.iff, its own file and its semi-global file. Sims.exe builds one
/// per definition (FUN_00401e31, 0x18 bytes: +4 the global file, +8 the object's file, +0xc the semi-global file)
/// and picks the file by the tree id's range (FUN_004021ff).</summary>
public sealed record BehaviorScope(IffFile Global, IffFile Private, IffFile? SemiGlobal) : Simulation.ITreeTable
{
    // Sims.exe stores BCON overrides on the ObjResFile, shared by every object definition in that file.
    // ObjectRegistry shares one IffFile instance for those definitions, so this weak key gives the parsed file
    // the same lifetime and sharing boundary without retaining unloaded registries.
    private static readonly ConditionalWeakTable<IffFile, Dictionary<ushort, short[]>> ConstantOverrides = new();
    /// <summary>The object whose code-owner scope this is; used to resolve Object-scope animation resources.</summary>
    public int? ObjectKey { get; init; }
    /// <summary>The OBJD animation table resource id for this code owner.</summary>
    public ushort AnimationTableId { get; init; }
    private static readonly StringComparison[] NamePasses =
    [
        // IFFResFile::GetByName calls IFFNameList::GetFirstName, which compares with compareNoCase, regardless
        // of FUN_00402246's final FindResource flag. Both passes therefore use the same source-defined rule.
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

    /// <summary>FUN_00470383 loads STR# first and falls back to CTSS for the current BHAV's resource file;
    /// FUN_004705F2 returns a one-based entry or null when the selected table has no such entry.</summary>
    public byte[]? StringTableBytes(short treeId, ushort tableId, int oneBasedIndex, byte language)
    {
        var file = FileFor(treeId);
        if (oneBasedIndex <= 0 || file is null) return null;
        var chunk = file.Find(ResourceType.Str, tableId) ?? file.Find(ResourceType.Ctss, tableId);
        if (chunk is null) return null;

        var table = StringTable.Read(file, chunk);
        if (!table.ReadBySimsExe) return null;
        var strings = table.ForLanguage(language);
        return oneBasedIndex <= strings.Count
            ? ResourceReader.EncodingForLanguage(language == 0 ? (byte)1 : language).GetBytes(strings[oneBasedIndex - 1])
            : null;
    }

    /// <summary>The tree <paramref name="id"/> names, or null for a primitive or a tree its file lacks.</summary>
    public Bhav? Find(short id)
    {
        var file = FileFor(id);
        var chunk = file?.Find(ResourceType.Bhav, (ushort)id);
        return chunk is null ? null : Bhav.Read(file!, chunk);
    }

    /// <summary>FUN_0040235e searches the code owner's private, semi-global or global IFF for a BCON with this id.
    /// A flagged BCON from the private object file uses that ObjResFile's in-memory tuning override when the caller
    /// permits it; global and semi-global resource files are not ObjResFile instances.</summary>
    public IReadOnlyList<short>? Constants(ushort id, bool allowTuningOverride = false)
    {
        var file = id switch
        {
            >= 0x1000 and < 0x2000 => Private,
            >= 0x2000 and < 0x2329 => SemiGlobal,
            >= 0x100 and < 0x1000 => Global,
            _ => null,
        };
        var chunk = file?.Find(ResourceType.Bcon, id);
        if (chunk is null) return null;
        var bcon = Bcon.Read(file!, chunk);
        if (allowTuningOverride && bcon.TuningOverride && ReferenceEquals(file, Private) &&
            ConstantOverrides.TryGetValue(Private, out var overrides))
        {
            lock (overrides)
                if (overrides.TryGetValue(id, out var values)) return values;
        }
        return bcon.Constants;
    }

    /// <summary>Sets the private ObjResFile's runtime BCON override, copying the supplied words as Sims.exe does.
    /// Passing null removes that resource's override.</summary>
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

    /// <summary>FUN_004569a5: frees and clears every tuning override owned by this private object IFF.</summary>
    public void ClearConstantOverrides()
        => ClearConstantOverridesForFile(Private);

    internal static void ClearConstantOverridesForFile(IffFile privateFile)
    {
        if (!ConstantOverrides.TryGetValue(privateFile, out var overrides)) return;
        lock (overrides) overrides.Clear();
    }

    /// <summary>FUN_00402246 searches BHAV labels first in private, semi-global, global order, then repeats that
    /// order with the final FindResource flag set. IFFResFile::GetByName delegates to IFFNameList::GetFirstName,
    /// which compares names with compareNoCase in both passes. Returns the first matching label's chunk id, or zero.</summary>
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

/// <summary>The files of GameData\Global (folder code 0x18): Global.iff and the semi-global files objects name in
/// their GLOB chunk.
/// <para>Sims.exe FUN_0043bb37 appends the GLOB name to the global folder's path and hands it to FUN_0043e530, which
/// returns the file already loaded under that path or opens and caches it; a file that does not open leaves the
/// object without a semi-global file.</para>
/// <para>Inferred, not traced: the open goes through the archive layer, so a name is found loose in the folder or as
/// an entry of a .far there (the shipped game keeps every one in Global.far); names compare without case; ".iff" is
/// appended to a name that has no extension.</para></summary>
public sealed class GlobalLibrary
{
    private readonly Dictionary<string, IffFile?> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<ReadOnlyMemory<byte>>> _sources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>GameData\Global, or null when the installation has none.</summary>
    public string? Folder { get; }
    /// <summary>Global.iff, or null when it is missing.</summary>
    public IffFile? Global { get; }
    /// <summary>Names asked for that no file answered, and files that did not read as IFF, with the reason.</summary>
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

    /// <summary>Opens Sims.exe's GameData\Global folder. The root-level Global directory is also accepted for
    /// flat Complete Collection installs that place the extracted game directories directly under the install root.</summary>
    public static GlobalLibrary Open(string game) => new(
        ObjectRegistry.Resolve(game, @"GameData\Global") ?? ObjectRegistry.Resolve(game, "Global"));

    /// <summary>The file named <paramref name="name"/> (a GLOB name), loaded once.</summary>
    public IffFile? Load(string name)
    {
        // An empty name leaves the folder's own path, which does not open as a file.
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = Path.HasExtension(name) ? name : name + ".iff";
        if (_files.TryGetValue(key, out var file)) return file;
        if (!_sources.TryGetValue(key, out var source)) { Missing.Add((name, "not found")); return _files[key] = null; }
        try { file = new IffFile(source(), key); }
        catch (ResourceFormatException error) { Missing.Add((name, error.Message)); file = null; }
        return _files[key] = file;
    }

    /// <summary>The trees <paramref name="entry"/>'s object can call, or null when Global.iff is missing.</summary>
    public BehaviorScope? ScopeOf(ObjectEntry entry) =>
        Global is null ? null : new BehaviorScope(Global, entry.File, entry.SemiGlobal is { } name ? Load(name) : null)
        {
            ObjectKey = unchecked((int)entry.Guid),
            AnimationTableId = entry.Definition[ObjdField.AnimTableId],
        };
}
