using System.Collections.Concurrent;
using SimsEngine.Formats;

namespace SimsEngine.Content;

/// <summary>Defines the fixed folder search sequence used when scanning object definitions across the
/// installation.</summary>
public enum ObjectScan
{
    Objects,
    UserObjects,
    People,
    Downloads,
    LivinLarge,
    HouseParty,
    HotDate,
    Vacation,
    Deluxe,
    Unleashed,
    Superstar,
    ExpansionShared,
    NpcDate,
    Spellbound,
}

public sealed record ObjectEntry(int Index, ObjectScan Scan, string Source, IffFile File, IffChunk Chunk, Objd Definition,
    string? SemiGlobal)
{
    public uint Guid => Definition.Guid;

    /// <summary>Indicates whether an object has an unassigned interaction table ID (-1) without a master
    /// object fallback.</summary>
    public bool HasNoMasterInteractionTable => Definition[ObjdField.TreeTableId] == ushort.MaxValue &&
                                               Definition[ObjdField.MasterId] == 0;

    public string? CatalogString(int index, byte language)
    {
        var chunk = File.Find(ResourceType.Ctss, Definition[ObjdField.CatalogStringsId]);
        if (chunk is null) return null;
        var strings = StringTable.Read(File, chunk).ForLanguage(language);
        return index >= 0 && index < strings.Count ? strings[index] : null;
    }

    /// <summary>Returns the action function IDs resolved from the object's matching OBJf chunk or legacy
    /// OBJD function table fields.</summary>
    public IReadOnlyList<ushort> ActionFunctions => Objf.ActionFunctions(File, Chunk, Definition);

    /// <summary>Returns guard and action pairs from the function table, using zero guards for legacy OBJD
    /// entries.</summary>
    public IReadOnlyList<(ushort Guard, ushort Action)> FunctionEntries => Objf.FunctionEntries(File, Chunk, Definition);

    /// <summary>Resolves an object's interaction table (TTAB) from its private resource file or semi-global
    /// scope, falling back to the master object definition if the table ID is -1.</summary>
    public InteractionTable? FindInteractionTable(BehaviorScope? scope = null, byte language = 1, ObjectEntry? masterFallback = null)
    {
        var rawTreeTableId = Definition[ObjdField.TreeTableId];
        ushort treeTableId;
        if (rawTreeTableId == ushort.MaxValue)
        {
            if (masterFallback is null) return null;
            treeTableId = unchecked((ushort)masterFallback.Definition[ObjdField.TreeTableId]);
        }
        else treeTableId = (ushort)rawTreeTableId;
        if (treeTableId is 0 or ushort.MaxValue) return null;
        foreach (var file in new[] { File, scope?.SemiGlobal }.Where(file => file is not null).Distinct())
        {
            var chunk = file!.Find(ResourceType.Ttab, treeTableId);
            if (chunk is not null)
            {
                var namesChunk = file.Find(ResourceType.Parse("TTAs"), treeTableId);
                var names = namesChunk is null ? [] : StringTable.Read(file, namesChunk).ForLanguage(language);
                var table = InteractionTable.Read(file, chunk).WithNames(entry =>
                    entry.TtaIndex >= 0 && entry.TtaIndex < names.Count && names[entry.TtaIndex].Length > 0
                        ? names[entry.TtaIndex]
                        : file.Find(ResourceType.Bhav, entry.ActionTree)?.Label is { Length: > 0 } label ? label : entry.Name);
                if (rawTreeTableId == ushort.MaxValue && masterFallback is not null)
                    table = TuningOverrideImporter.ApplyGuidOverrides(masterFallback.Guid, table);
                return TuningOverrideImporter.ApplyGuidOverrides(Guid, table);
            }
        }
        return null;
    }
}

/// <summary>Catalogs object definitions by GUID across installation folders and archive packages in fixed
/// scan order, with later scanned definitions replacing earlier ones.</summary>
public sealed class ObjectRegistry
{
    private static readonly HashSet<string> FlatInstallNonPackageDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Animation", "Characters", "Downloads", "ExpansionShared", "Floors", "Global", "GFX", "Houses",
        "Lights", "Modes", "Objects", "Patch", "Readme", "Roofs", "Scripts", "Shared", "SimsResource",
        "Skins", "SkinsBuy", "Sound", "Stations", "TemplateNPCs", "TemplateUserData", "Textures", "TVStations",
        "UserObjects", "Walls", "Web Templates", "runtime",
    };

    private readonly Dictionary<uint, ObjectEntry> _byGuid;
    private readonly List<ObjectEntry> _entries;
    private readonly object? _archiveCachePin;

    public IReadOnlyList<ObjectEntry> Entries => _entries;
    public IReadOnlyDictionary<uint, ObjectEntry> ByGuid => _byGuid;
    public IReadOnlyList<(ObjectScan Scan, string Folder)> Folders { get; }
    public IReadOnlyList<(ObjectScan Scan, string Archive)> Archives { get; }
    public IReadOnlyList<(string Source, string Error)> Unreadable { get; }
    public List<string> TuningIssues { get; } = new();
    /// <summary>The bitmask of installed expansion packs detected from installation marker files.</summary>
    public byte GameEditionMask { get; }
    public IffFile? UiText { get; }
    public string? GameRoot { get; }
    public HashSet<uint> DirtyPeopleGuids { get; } = [];

    public ObjectRegistry() : this([], new(), [], [], [], 0, null, null, null)
    {
    }

    private ObjectRegistry(List<ObjectEntry> entries, Dictionary<uint, ObjectEntry> byGuid,
        List<(ObjectScan, string)> folders, List<(ObjectScan, string)> archives, List<(string, string)> unreadable,
        byte gameEditionMask, IffFile? uiText, string? gameRoot, object? archiveCachePin = null)
    {
        _entries = entries;
        _byGuid = byGuid;
        Folders = folders;
        Archives = archives;
        Unreadable = unreadable;
        GameEditionMask = gameEditionMask;
        UiText = uiText;
        GameRoot = gameRoot;
        _archiveCachePin = archiveCachePin;
    }

    public ObjectEntry? Find(uint guid) => _byGuid.GetValueOrDefault(guid);

    /// <summary>Returns a copy of the registry that rescans only the characters folder while retaining all
    /// other registered objects.</summary>
    public ObjectRegistry WithRefreshedPeople(string? peopleFolder = null)
    {
        if (string.IsNullOrEmpty(GameRoot))
            return ScanPeopleOnly(GameRoot ?? "", peopleFolder);

        var peopleEntries = ScanCore(GameRoot, peopleFolder, skipDisabled: false, peopleOnly: true)._entries
            .Where(entry => entry.Scan == ObjectScan.People)
            .ToArray();
        var merged = new List<ObjectEntry>(_entries.Count + peopleEntries.Length);
        var byGuid = new Dictionary<uint, ObjectEntry>(_byGuid.Count + peopleEntries.Length);
        var insertedPeople = false;

        void AddMerged(ObjectEntry item)
        {
            var reindexed = item.Index == merged.Count ? item : item with { Index = merged.Count };
            merged.Add(reindexed);
            byGuid[reindexed.Guid] = reindexed;
        }

        foreach (var existing in _entries)
        {
            if (existing.Scan == ObjectScan.People) continue;
            if (!insertedPeople && existing.Scan > ObjectScan.People)
            {
                insertedPeople = true;
                foreach (var person in peopleEntries) AddMerged(person);
            }
            AddMerged(existing);
        }
        if (!insertedPeople)
            foreach (var person in peopleEntries) AddMerged(person);

        var updatedFolders = Folders.Where(item => item.Item1 != ObjectScan.People).ToList();
        if (!string.IsNullOrEmpty(peopleFolder))
            updatedFolders.Add((ObjectScan.People, peopleFolder));
        return new ObjectRegistry(merged, byGuid, updatedFolders, Archives.ToList(), Unreadable.ToList(),
            GameEditionMask, UiText, GameRoot, _archiveCachePin);
    }

    /// <summary>Reads a string entry from a UIText.iff string table for the given language.</summary>
    public string? UiTextString(ushort tableId, int index, byte language)
    {
        if (UiText?.Find(ResourceType.Str, tableId) is not { } chunk) return null;
        var strings = StringTable.Read(UiText, chunk).ForLanguage(language);
        return index >= 0 && index < strings.Count && strings[index].Length > 0 ? strings[index] : null;
    }

    /// <summary>Removes a registered object definition by GUID so it can no longer be instantiated or
    /// referenced.</summary>
    public bool Remove(uint guid)
    {
        if (!_byGuid.Remove(guid, out var entry)) return false;
        _entries.Remove(entry);
        return true;
    }

    public void RegisterFile(IffFile file, string source, ObjectScan scan = ObjectScan.People)
    {
        var glob = file.OfType(ResourceType.Glob).FirstOrDefault();
        string? semiGlobal = null;
        if (glob is not null)
            try { semiGlobal = Glob.Read(file, glob).Name; }
            catch (ResourceFormatException) { }
        foreach (var chunk in file.OfType(ResourceType.Objd))
        {
            var definition = Objd.Read(file, chunk);
            var entry = new ObjectEntry(_entries.Count, scan, source, file, chunk, definition, semiGlobal);
            _entries.Add(entry);
            _byGuid[entry.Guid] = entry;
        }
    }

    /// <summary>Replaces the in-memory resource file for an existing registered object while preserving its
    /// registry index and scan position.</summary>
    public bool ReplaceEntryFile(uint guid, IffFile file, bool markDirty = true)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!_byGuid.TryGetValue(guid, out var existing)) return false;
        var chunk = file.Find(ResourceType.Objd, existing.Chunk.Id) ?? file.OfType(ResourceType.Objd).FirstOrDefault();
        if (chunk is null) return false;
        var definition = Objd.Read(file, chunk);
        var updated = existing with { File = file, Chunk = chunk, Definition = definition };
        _byGuid[guid] = updated;
        if (existing.Index >= 0 && existing.Index < _entries.Count && _entries[existing.Index].Guid == guid)
        {
            _entries[existing.Index] = updated;
        }
        else
        {
            var found = _entries.FindIndex(item => item.Guid == guid);
            if (found >= 0) _entries[found] = updated;
        }
        if (markDirty && existing.Scan == ObjectScan.People)
            DirtyPeopleGuids.Add(guid);
        return true;
    }

    /// <summary>Finds the alternative object definition sharing the same master ID and runtime class with
    /// sub-index -1 in the same file.</summary>
    public ObjectEntry? FindAlternativeType(ObjectEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var masterId = entry.Definition[ObjdField.MasterId];
        if (masterId == 0) return null;
        var objectClass = LotObjects.ClassOf(entry.Definition);
        if (objectClass is null) return null;
        // Master IDs only associate object definitions originating from the same file.
        return Entries.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.File, entry.File) &&
            LotObjects.ClassOf(candidate.Definition) == objectClass &&
            candidate.Definition[ObjdField.MasterId] == masterId &&
            candidate.Definition[ObjdField.SubIndex] == ushort.MaxValue);
    }

    public static ObjectRegistry ScanPeopleOnly(string game, string? people = null) =>
        ScanCore(game, people, skipDisabled: false, peopleOnly: true);

    public static ObjectRegistry Scan(string game, string? people = null, bool skipDisabled = false) =>
        ScanCore(game, people, skipDisabled, peopleOnly: false);

    private sealed record CachedArchiveIff(
        string ItemName,
        IffFile File,
        string? SemiGlobal,
        (IffChunk Chunk, Objd Definition)[] Objds);

    private static readonly ConcurrentDictionary<string, (long Length, long LastWriteTicks, WeakReference<CachedArchiveIff[]> Files, (string ItemName, string Error)[] Unreadable)> ArchiveMetadataCache =
        new(StringComparer.Ordinal);

    private static (CachedArchiveIff[] Files, (string ItemName, string Error)[] Unreadable) GetOrParseArchiveIffs(
        string path, out string? openError)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (info.Exists && ArchiveMetadataCache.TryGetValue(fullPath, out var cached) &&
            cached.Length == info.Length && cached.LastWriteTicks == info.LastWriteTimeUtc.Ticks &&
            cached.Files.TryGetTarget(out var cachedFiles))
        {
            openError = null;
            return (cachedFiles, cached.Unreadable);
        }

        FarArchive archive;
        try { archive = FarArchive.Open(path); }
        catch (ResourceFormatException error)
        {
            openError = error.Message;
            return ([], []);
        }

        var filesList = new List<CachedArchiveIff>();
        var badList = new List<(string ItemName, string Error)>();
        foreach (var item in archive.Entries)
        {
            if (!item.Name.EndsWith(".iff", StringComparison.OrdinalIgnoreCase)) continue;
            var source = path + "::" + item.Name;
            IffFile file;
            try { file = archive.ReadIff(item, source); }
            catch (ResourceFormatException error)
            {
                badList.Add((item.Name, error.Message));
                continue;
            }
            var glob = file.OfType(ResourceType.Glob).FirstOrDefault();
            string? semiGlobal = null;
            if (glob is not null)
                try { semiGlobal = Glob.Read(file, glob).Name; }
                catch (ResourceFormatException) { }
            var objdChunks = file.OfType(ResourceType.Objd).ToArray();
            var objds = new (IffChunk Chunk, Objd Definition)[objdChunks.Length];
            for (var j = 0; j < objdChunks.Length; j++)
                objds[j] = (objdChunks[j], Objd.Read(file, objdChunks[j]));
            filesList.Add(new CachedArchiveIff(item.Name, file, semiGlobal, objds));
        }

        var filesArray = filesList.ToArray();
        var badArray = badList.ToArray();
        if (info.Exists)
            ArchiveMetadataCache[fullPath] = (info.Length, info.LastWriteTimeUtc.Ticks, new WeakReference<CachedArchiveIff[]>(filesArray), badArray);
        openError = null;
        return (filesArray, badArray);
    }

    private static ObjectRegistry ScanCore(string game, string? people, bool skipDisabled, bool peopleOnly)
    {
        var entries = new List<ObjectEntry>();
        var byGuid = new Dictionary<uint, ObjectEntry>();
        var folders = new List<(ObjectScan, string)>();
        var archives = new List<(ObjectScan, string)>();
        var unreadable = new List<(string, string)>();
        var seen = new HashSet<(string, ushort)>(EqualityComparer<(string, ushort)>.Default);
        var pins = new List<CachedArchiveIff[]>();

        void RegisterFile(ObjectScan scan, string source, IffFile file)
        {
            // Uses the first GLOB chunk in the file, which may be numbered 128 or 1.
            var glob = file.OfType(ResourceType.Glob).FirstOrDefault();
            string? semiGlobal = null;
            if (glob is not null)
                try { semiGlobal = Glob.Read(file, glob).Name; }
                catch (ResourceFormatException) { }
            foreach (var chunk in file.OfType(ResourceType.Objd))
            {
                if (!seen.Add((source.ToUpperInvariant(), chunk.Id))) continue;
                var definition = Objd.Read(file, chunk);
                if (skipDisabled && definition[ObjdField.Disabled] != 0) continue;
                var entry = new ObjectEntry(entries.Count, scan, source, file, chunk, definition, semiGlobal);
                entries.Add(entry);
                byGuid[entry.Guid] = entry;
            }
        }

        void Register(ObjectScan scan, string source, ReadOnlyMemory<byte> bytes)
        {
            IffFile file;
            try { file = new IffFile(bytes, source); }
            catch (ResourceFormatException error) { unreadable.Add((source, error.Message)); return; }
            RegisterFile(scan, source, file);
        }

        void ReplayCachedArchive(ObjectScan scan, string archivePath, CachedArchiveIff[] files, (string ItemName, string Error)[] bad)
        {
            pins.Add(files);
            foreach (var err in bad)
                unreadable.Add((archivePath + "::" + err.ItemName, err.Error));
            foreach (var cachedFile in files)
            {
                var source = archivePath + "::" + cachedFile.ItemName;
                var upperSource = source.ToUpperInvariant();
                foreach (var (chunk, definition) in cachedFile.Objds)
                {
                    if (!seen.Add((upperSource, chunk.Id))) continue;
                    if (skipDisabled && definition[ObjdField.Disabled] != 0) continue;
                    var entry = new ObjectEntry(entries.Count, scan, source, cachedFile.File, chunk, definition, cachedFile.SemiGlobal);
                    entries.Add(entry);
                    byGuid[entry.Guid] = entry;
                }
            }
        }

        HashSet<string>? disabledDownloads = null;
        string disabledMarkerPath = Path.Combine(game, ".disabled-downloads.txt");
        if (File.Exists(disabledMarkerPath))
        {
            try
            {
                disabledDownloads = new HashSet<string>(
                    File.ReadAllLines(disabledMarkerPath)
                        .Select(static s => s.Trim())
                        .Where(static s => s.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
            }
        }
        if (Environment.GetEnvironmentVariable("SIMS_DISABLED_DOWNLOADS") is { Length: > 0 } envDisabled)
        {
            disabledDownloads ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string token in envDisabled.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                disabledDownloads.Add(token);
        }

        void Walk(ObjectScan scan, string folder, string? downloadsTopRoot = null)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(folder).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (Directory.Exists(path))
                {
                    if (scan == ObjectScan.Downloads
                        && downloadsTopRoot is not null
                        && string.Equals(folder, downloadsTopRoot, StringComparison.OrdinalIgnoreCase)
                        && disabledDownloads is not null
                        && disabledDownloads.Contains(Path.GetFileName(path)))
                    {
                        continue;
                    }
                    Walk(scan, path, downloadsTopRoot);
                    continue;
                }
                if (path.EndsWith(".iff", StringComparison.OrdinalIgnoreCase)) Register(scan, path, File.ReadAllBytes(path));
                else if (path.EndsWith(".far", StringComparison.OrdinalIgnoreCase))
                {
                    var (files, bad) = GetOrParseArchiveIffs(path, out var openError);
                    if (openError is not null) { unreadable.Add((path, openError)); continue; }
                    ReplayCachedArchive(scan, path, files, bad);
                }
            }
        }

        void ScanArchive(ObjectScan scan, string path)
        {
            var (files, bad) = GetOrParseArchiveIffs(path, out var openError);
            if (openError is not null) { unreadable.Add((path, openError)); return; }
            archives.Add((scan, path));
            ReplayCachedArchive(scan, path, files, bad);
        }

        void ScanPackage(ObjectScan scan, string[] directoryNames, string[] archiveNames)
        {
            foreach (var name in directoryNames)
                if (Resolve(game, name) is { } folder)
                {
                    folders.Add((scan, folder));
                    Walk(scan, folder);
                    return;
                }
            foreach (var name in archiveNames)
            {
                var path = Path.Combine(game, name);
                if (File.Exists(path))
                {
                    ScanArchive(scan, path);
                    return;
                }
            }
        }

        bool HasFile(string windowsPath)
        {
            var parts = windowsPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            var folder = parts.Length == 1 ? game : Resolve(game, string.Join('\\', parts[..^1]));
            return folder is not null && Directory.Exists(folder) && Directory.EnumerateFiles(folder)
                .Any(path => string.Equals(Path.GetFileName(path), parts[^1], StringComparison.OrdinalIgnoreCase));
        }

        // Checks expansion marker files and Ranger resources to set installed pack bits.
        byte gameEditionMask = 0x80;
        if (HasFile(@"ExpansionPack\xpack.hot") && HasFile(@"GameData\Ranger.iff")) gameEditionMask |= 0x01;
        if (HasFile(@"ExpansionShared\Sound\houseparty.hot") && HasFile(@"GameData\Ranger2.iff")) gameEditionMask |= 0x02;
        if (HasFile(@"ExpansionShared\Sound\HotDate.hot") && HasFile(@"GameData\Ranger3.iff")) gameEditionMask |= 0x04;
        if (HasFile(@"ExpansionShared\Sound\vacsnd.hot") && HasFile(@"GameData\Ranger4.iff")) gameEditionMask |= 0x08;
        if (HasFile(@"GameData\RangerD.iff")) gameEditionMask |= 0x10;
        if (HasFile(@"ExpansionPack5\Sound\sep5snds.hot") && HasFile(@"GameData\Ranger5.iff")) gameEditionMask |= 0x20;
        if (HasFile(@"GameData\Ranger6.iff")) gameEditionMask |= 0x40;

        if (peopleOnly)
        {
            if (people is not null && Directory.Exists(people)) { folders.Add((ObjectScan.People, people)); Walk(ObjectScan.People, people); }
            else ScanPackage(ObjectScan.People, [@"UserData\Characters", "Characters"], ["Characters.far"]);
            if (gameEditionMask > 3)
                ScanPackage(ObjectScan.NpcDate, ["TemplateNPCs"], ["TemplateNPCs.far"]);
        }
        else
        {
            ScanPackage(ObjectScan.Objects, [@"GameData\Objects", "Objects"], ["Objects.far"]);
            ScanPackage(ObjectScan.UserObjects, [@"GameData\UserObjects", "UserObjects"], ["UserObjects.far"]);
            if (people is not null && Directory.Exists(people)) { folders.Add((ObjectScan.People, people)); Walk(ObjectScan.People, people); }
            else ScanPackage(ObjectScan.People, [@"UserData\Characters", "Characters"], ["Characters.far"]);
            if (Resolve(game, "Downloads") is { } downloadFolder)
            {
                folders.Add((ObjectScan.Downloads, downloadFolder));
                Walk(ObjectScan.Downloads, downloadFolder, downloadFolder);
            }
            else if (File.Exists(Path.Combine(game, "Downloads.far")))
            {
                ScanArchive(ObjectScan.Downloads, Path.Combine(game, "Downloads.far"));
            }
            else if (Directory.Exists(game) && Resolve(game, @"GameData\Objects") is null && Resolve(game, "Objects") is not null)
            {
                // Scans downloaded pack directories at the standard Downloads scan position.
                foreach (var folder in Directory.EnumerateDirectories(game).Order(StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(folder);
                    if (FlatInstallNonPackageDirectories.Contains(name) || IsExpansionFolder(name) ||
                        name.StartsWith("UserData", StringComparison.OrdinalIgnoreCase))
                        continue;
                    folders.Add((ObjectScan.Downloads, folder));
                    Walk(ObjectScan.Downloads, folder);
                }
            }
            if ((gameEditionMask & 0x01) != 0) ScanPackage(ObjectScan.LivinLarge, ["ExpansionPack", "ExpansionPack1"], ["ExpansionPack.far", "ExpansionPack1.far"]);
            if ((gameEditionMask & 0x02) != 0) ScanPackage(ObjectScan.HouseParty, ["ExpansionPack2"], ["ExpansionPack2.far"]);
            if ((gameEditionMask & 0x04) != 0) ScanPackage(ObjectScan.HotDate, ["ExpansionPack3"], ["ExpansionPack3.far"]);
            if ((gameEditionMask & 0x08) != 0) ScanPackage(ObjectScan.Vacation, ["ExpansionPack4"], ["ExpansionPack4.far"]);
            if ((gameEditionMask & 0x10) != 0) ScanPackage(ObjectScan.Deluxe, ["ExpansionPackGOLD", "Deluxe"], ["ExpansionPackGOLD.far", "Deluxe.far"]);
            if ((gameEditionMask & 0x20) != 0) ScanPackage(ObjectScan.Unleashed, ["ExpansionPack5"], ["ExpansionPack5.far"]);
            if ((gameEditionMask & 0x40) != 0) ScanPackage(ObjectScan.Superstar, ["ExpansionPack6"], ["ExpansionPack6.far"]);
            // Shared expansion content is enabled when any pack after House Party is active.
            if (gameEditionMask > 3)
            {
                ScanPackage(ObjectScan.ExpansionShared, ["ExpansionShared"], ["ExpansionShared.far"]);
                ScanPackage(ObjectScan.NpcDate, ["TemplateNPCs"], ["TemplateNPCs.far"]);
            }
            ScanPackage(ObjectScan.Spellbound, ["ExpansionPack7"], ["ExpansionPack7.far"]);
        }
        IffFile? uiText = null;
        foreach (var folder in new[] { Resolve(game, "GameData"), game })
        {
            if (folder is null || !Directory.Exists(folder)) continue;
            var uiTextPath = Directory.EnumerateFiles(folder)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), "UIText.iff", StringComparison.OrdinalIgnoreCase));
            if (uiTextPath is null) continue;
            try { uiText = new IffFile(File.ReadAllBytes(uiTextPath), uiTextPath); }
            catch (ResourceFormatException error) { unreadable.Add((uiTextPath, error.Message)); }
            break;
        }
        var registry = new ObjectRegistry(entries, byGuid, folders, archives, unreadable, gameEditionMask, uiText, game,
            pins.Count > 0 ? pins.ToArray() : null);
        registry.TuningIssues.AddRange(TuningOverrideImporter.Apply(game, registry));
        return registry;
    }

    private static bool IsExpansionFolder(string name) =>
        name.Equals("Deluxe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ExpansionPack", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("ExpansionPack", StringComparison.OrdinalIgnoreCase);

    internal static string? Resolve(string root, string windowsPath)
    {
        var folder = root;
        foreach (var part in windowsPath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Directory.Exists(folder)
                ? Directory.EnumerateDirectories(folder).FirstOrDefault(path => string.Equals(Path.GetFileName(path), part, StringComparison.OrdinalIgnoreCase))
                : null;
            if (match is null) return null;
            folder = match;
        }
        return folder;
    }
}
