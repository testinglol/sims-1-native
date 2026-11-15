using SimsEngine.Formats;

namespace SimsEngine.Content;

/// <summary>The object folders Sims.exe scans for definitions, in the order FUN_0043c6ee scans them.</summary>
public enum ObjectScan
{
    /// <summary>"Scanning Sims 1 Objects": GameData\Objects\ (folder code 0x17).</summary>
    Objects,
    /// <summary>"Scanning User Objects": GameData\UserObjects\ (0x19).</summary>
    UserObjects,
    /// <summary>"Scanning UserData People". Inferred: the neighbourhood's Characters folder.</summary>
    People,
    /// <summary>"Scanning Downloaded Objects": Downloads\ (0x1a).</summary>
    Downloads,
    /// <summary>Livin' Large, game setting bit 1 (0x1b ExpansionPack\).</summary>
    LivinLarge,
    /// <summary>House Party, bit 2 (0x1e ExpansionPack2\).</summary>
    HouseParty,
    /// <summary>Hot Date, bit 4 (0x24 ExpansionPack3\).</summary>
    HotDate,
    /// <summary>Vacation, bit 8 (0x2a ExpansionPack4\).</summary>
    Vacation,
    /// <summary>Deluxe, bit 0x10 (0x30).</summary>
    Deluxe,
    /// <summary>Unleashed, bit 0x20 (0x36 ExpansionPack5\).</summary>
    Unleashed,
    /// <summary>Superstar, bit 0x40 (0x3c ExpansionPack6\).</summary>
    Superstar,
    /// <summary>"Scanning Expansion Shared Objects" (0x42), when the game setting short is above 3.</summary>
    ExpansionShared,
    /// <summary>"Scanning NPC Date Objects": TemplateNPCs\ (0x52), with Expansion Shared.</summary>
    NpcDate,
    /// <summary>Makin' Magic ("Spellbound"), bit 0x80, scanned last.</summary>
    Spellbound,
}

/// <summary>One registered object definition: its place in the registry (Sims.exe keeps it at +0xa8 of the entry),
/// the file and OBJD chunk it came from, and the semi-global file name its GLOB chunk 1 names.</summary>
public sealed record ObjectEntry(int Index, ObjectScan Scan, string Source, IffFile File, IffChunk Chunk, Objd Definition,
    string? SemiGlobal)
{
    public uint Guid => Definition.Guid;

    /// <summary>A -1 TreeTableId with no master has no table fallback: Sims.exe only resolves this through
    /// FUN_004573f9 when the master id is nonzero. Lot restore must not eagerly call that on non-interactive records.</summary>
    public bool HasNoMasterInteractionTable => Definition[ObjdField.TreeTableId] == ushort.MaxValue &&
                                               Definition[ObjdField.MasterId] == 0;

    /// <summary>Reads one catalog string from the object's CTSS resource, using Sims.exe's language selection rules.</summary>
    public string? CatalogString(int index, byte language)
    {
        var chunk = File.Find(ResourceType.Ctss, Definition[ObjdField.CatalogStringsId]);
        if (chunk is null) return null;
        var strings = StringTable.Read(File, chunk).ForLanguage(language);
        return index >= 0 && index < strings.Count ? strings[index] : null;
    }

    /// <summary>The action function IDs reached by Sims.exe FUN_00457da4/FUN_004563fd. OBJf uses the OBJD chunk id;
    /// when the definition does not use that table or the resource is absent, FUN_00456209 supplies the legacy fields.</summary>
    public IReadOnlyList<ushort> ActionFunctions => Objf.ActionFunctions(File, Chunk, Definition);

    /// <summary>The guard/action pairs read by FUN_004561c2 from the resolved function table. Legacy OBJD entries have
    /// action IDs and zero guards.</summary>
    public IReadOnlyList<(ushort Guard, ushort Action)> FunctionEntries => Objf.FunctionEntries(File, Chunk, Definition);

    /// <summary>FUN_00457e1c asks FUN_004573d6 for OBJD.TreeTableId, then FUN_00456b67 looks up that exact
    /// TTAB id in the private resource file (+8) and falls back to the semi-global file (+0xc). A TreeTableId of -1
    /// uses the master-object definition returned by FUN_004573f9; callers provide that definition when available.</summary>
    public InteractionTable? FindInteractionTable(BehaviorScope? scope = null, byte language = 1, ObjectEntry? masterFallback = null)
    {
        var rawTreeTableId = Definition[ObjdField.TreeTableId];
        ushort treeTableId;
        if (rawTreeTableId == ushort.MaxValue)
        {
            if (masterFallback is null) return null;
            // FUN_004573d6 resolves the table id through the master definition, then FUN_00457e1c
            // looks up that id in this object's private resource file before the semi-global file.
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

/// <summary>The object definitions of an installation, found by GUID as Sims.exe finds them.
/// <para>Sims.exe FUN_0043c6ee scans the object folders in a fixed order (<see cref="ObjectScan"/>): the base game,
/// user objects, people and downloads always, then each installed expansion pack. FUN_00412825 walks a folder
/// depth first. For each file FUN_0043b981 takes the OBJD chunks in order and skips one whose chunk id the same file
/// already registered; FUN_0043bb37 then skips a definition whose Disabled field (16) is set when the registry is
/// told to (flag +0x1240), adds an entry at the end of the list, reads the file's GLOB chunk for its semi-global
/// file, and stores the entry in the GUID map (FUN_0043ed84, find or insert), overwriting what was there, so the last
/// definition scanned for a GUID wins.</para>
/// <para>Inferred, not traced: the walk visits entries in the order Windows lists them (names compared without
/// case), and its file callback takes .iff files and the .iff files inside .far archives. Pack folder names follow
/// the installation (ExpansionPack or ExpansionPack1, ExpansionPackGOLD for Deluxe, ExpansionPack7 for Makin'
/// Magic). FUN_00413d0e's Ranger/.hot markers set <see cref="GameEditionMask"/>; FUN_0043c6ee scans each pack
/// folder only when its corresponding bit is set.</para>
/// <para>Compatibility inference for the extracted flat Complete Collection: when the standard GameData\Objects and
/// Downloads paths are absent, remaining named root folders are scanned at the Downloads position. Known game data
/// directories are excluded; root package classification and lexical ordering are not Sims.exe path rules.</para></summary>
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

    public IReadOnlyList<ObjectEntry> Entries => _entries;
    public IReadOnlyDictionary<uint, ObjectEntry> ByGuid => _byGuid;
    /// <summary>The folders scanned, in order, with the folder each one resolved to.</summary>
    public IReadOnlyList<(ObjectScan Scan, string Folder)> Folders { get; }
    /// <summary>Root-level FAR packages used by flat Mac/Complete Collection installs.</summary>
    public IReadOnlyList<(ObjectScan Scan, string Archive)> Archives { get; }
    /// <summary>Files that could not be read as IFF, with the reason.</summary>
    public IReadOnlyList<(string Source, string Error)> Unreadable { get; }
    /// <summary>Nonfatal messages from the optional Windows <c>GameData/Tuning.txt</c> BCON import.</summary>
    public List<string> TuningIssues { get; } = new();
    /// <summary>The installed-pack mask computed by Sims.exe FUN_00413d0e from its Ranger and .hot markers.</summary>
    public byte GameEditionMask { get; }
    /// <summary>UIText.iff resolved from GameData\UIText.iff or the install root (FUN_00435ff3).</summary>
    public IffFile? UiText { get; }
    /// <summary>Root directory of the scanned game installation when created via <see cref="Scan"/>.</summary>
    public string? GameRoot { get; }
    /// <summary>GUIDs of <see cref="ObjectScan.People"/> entries whose character IFFs were created or modified at runtime.</summary>
    public HashSet<uint> DirtyPeopleGuids { get; } = [];

    public ObjectRegistry() : this([], new(), [], [], [], 0, null, null)
    {
    }

    private ObjectRegistry(List<ObjectEntry> entries, Dictionary<uint, ObjectEntry> byGuid,
        List<(ObjectScan, string)> folders, List<(ObjectScan, string)> archives, List<(string, string)> unreadable,
        byte gameEditionMask, IffFile? uiText, string? gameRoot)
    {
        _entries = entries;
        _byGuid = byGuid;
        Folders = folders;
        Archives = archives;
        Unreadable = unreadable;
        GameEditionMask = gameEditionMask;
        UiText = uiText;
        GameRoot = gameRoot;
    }

    public ObjectEntry? Find(uint guid) => _byGuid.GetValueOrDefault(guid);

    /// <summary>Returns a copy of this registry keeping all non-People entries and rescanning only the Characters
    /// folder, avoiding a multi-thousand-object full disk scan after household creation.</summary>
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
            GameEditionMask, UiText, GameRoot);
    }

    /// <summary>FUN_00435ff3: reads one zero-based entry from UIText.iff STR# <paramref name="tableId"/> using Sims.exe's language selection rules.</summary>
    public string? UiTextString(ushort tableId, int index, byte language)
    {
        if (UiText?.Find(ResourceType.Str, tableId) is not { } chunk) return null;
        var strings = StringTable.Read(UiText, chunk).ForLanguage(language);
        return index >= 0 && index < strings.Count && strings[index].Length > 0 ? strings[index] : null;
    }

    /// <summary>Mac ObjectFolder::RemoveSelector (called by Neighborhood::DeleteCharacter): the selector for this GUID
    /// leaves the folder, so the object can no longer be found or chosen. False when the GUID is not registered.</summary>
    public bool Remove(uint guid)
    {
        if (!_byGuid.Remove(guid, out var entry)) return false;
        _entries.Remove(entry);
        return true;
    }

    /// <summary>Registers an object file directly into the registry.</summary>
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

    /// <summary>Replaces the in-memory IFF file backing an existing entry (such as a Sim's updated BodyStrings STR#),
    /// preserving its registry index and scan order.</summary>
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


    /// <summary>FUN_0043aeef walks the ordered type-record vector for a record whose OBJD has the same MasterId and
    /// SubIndex -1; FUN_0043aedc rejects a zero MasterId and every other SubIndex. FUN_005c17a6 also requires the
    /// same runtime class, modeled here with FUN_004409db's OBJD class mapping. Registry order is retained because
    /// Sims.exe returns the first match.</summary>
    public ObjectEntry? FindAlternativeType(ObjectEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var masterId = entry.Definition[ObjdField.MasterId];
        if (masterId == 0) return null;
        var objectClass = LotObjects.ClassOf(entry.Definition);
        if (objectClass is null) return null;
        // FUN_0043aeef also compares the registry entry's first field, the IFF file (FUN_005c17a6), so a master id only
        // pairs objects of one file.
        return Entries.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.File, entry.File) &&
            LotObjects.ClassOf(candidate.Definition) == objectClass &&
            candidate.Definition[ObjdField.MasterId] == masterId &&
            candidate.Definition[ObjdField.SubIndex] == ushort.MaxValue);
    }

    /// <summary>Scans only character and template-NPC IFFs for neighborhood family metadata without loading all 5,000+ lot objects.</summary>
    public static ObjectRegistry ScanPeopleOnly(string game, string? people = null) =>
        ScanCore(game, people, skipDisabled: false, peopleOnly: true);

    /// <summary>Scans the installation at <paramref name="game"/>. <paramref name="people"/> is the Characters
    /// folder of the neighbourhood in play (defaults to UserData\Characters).</summary>
    public static ObjectRegistry Scan(string game, string? people = null, bool skipDisabled = false) =>
        ScanCore(game, people, skipDisabled, peopleOnly: false);

    private static ObjectRegistry ScanCore(string game, string? people, bool skipDisabled, bool peopleOnly)
    {
        var entries = new List<ObjectEntry>();
        var byGuid = new Dictionary<uint, ObjectEntry>();
        var folders = new List<(ObjectScan, string)>();
        var archives = new List<(ObjectScan, string)>();
        var unreadable = new List<(string, string)>();
        var seen = new HashSet<(string, ushort)>(EqualityComparer<(string, ushort)>.Default);

        void Register(ObjectScan scan, string source, ReadOnlyMemory<byte> bytes)
        {
            IffFile file;
            try { file = new IffFile(bytes, source); }
            catch (ResourceFormatException error) { unreadable.Add((source, error.Message)); return; }
            // FUN_0043bb37 asks the file for (GLOB, 1, 0). Inferred: the file's first GLOB chunk, since shipped files
            // number it 128 as often as 1.
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

        void Walk(ObjectScan scan, string folder)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(folder).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (Directory.Exists(path)) { Walk(scan, path); continue; }
                if (path.EndsWith(".iff", StringComparison.OrdinalIgnoreCase)) Register(scan, path, File.ReadAllBytes(path));
                else if (path.EndsWith(".far", StringComparison.OrdinalIgnoreCase))
                {
                    FarArchive archive;
                    try { archive = FarArchive.Open(path); }
                    catch (ResourceFormatException error) { unreadable.Add((path, error.Message)); continue; }
                    foreach (var item in archive.Entries)
                        if (item.Name.EndsWith(".iff", StringComparison.OrdinalIgnoreCase))
                            Register(scan, path + "::" + item.Name, archive.Read(item));
                }
            }
        }

        void ScanArchive(ObjectScan scan, string path)
        {
            FarArchive archive;
            try { archive = FarArchive.Open(path); }
            catch (ResourceFormatException error) { unreadable.Add((path, error.Message)); return; }
            archives.Add((scan, path));
            foreach (var item in archive.Entries)
                if (item.Name.EndsWith(".iff", StringComparison.OrdinalIgnoreCase))
                    Register(scan, path + "::" + item.Name, archive.Read(item));
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

        // FUN_00413d0e tests both the expansion's shared .hot marker and its Ranger IFF before setting bits 1–4
        // and 0x20. Deluxe, Superstar use RangerD/Ranger6 alone; 0x80 is set unconditionally by the Complete
        // Collection executable. FUN_0043c6ee gates each expansion's object scan on these same bits.
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
                Walk(ObjectScan.Downloads, downloadFolder);
            }
            else if (File.Exists(Path.Combine(game, "Downloads.far")))
            {
                ScanArchive(ObjectScan.Downloads, Path.Combine(game, "Downloads.far"));
            }
            else if (Directory.Exists(game) && Resolve(game, @"GameData\Objects") is null && Resolve(game, "Objects") is not null)
            {
                // The extracted Complete Collection keeps each downloaded object pack in a named root directory.
                // Preserve Sims.exe's Downloads scan position; directory classification and order are compatibility inferences.
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
            // The game setting short is above 3 once any pack after House Party is installed.
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
        var registry = new ObjectRegistry(entries, byGuid, folders, archives, unreadable, gameEditionMask, uiText, game);
        registry.TuningIssues.AddRange(TuningOverrideImporter.Apply(game, registry));
        return registry;
    }

    private static bool IsExpansionFolder(string name) =>
        name.Equals("Deluxe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ExpansionPack", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("ExpansionPack", StringComparison.OrdinalIgnoreCase);

    /// <summary>Finds a folder under <paramref name="root"/> by a Windows path, matching each name without case.</summary>
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
