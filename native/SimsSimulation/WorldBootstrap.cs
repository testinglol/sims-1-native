using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SimsSimulation;

internal sealed record RemovedObject(string Guid, short ObjectId, string Name, int Refund);
internal sealed record MigratedObject(string From, string To, short ObjectId);

internal sealed record LotMigrationSummary(
    IReadOnlyList<RemovedObject> RemovedObjects,
    IReadOnlyList<MigratedObject> MigratedObjects,
    int Refund,
    IReadOnlySet<short> ResetEntityIds);

internal sealed record WorldBootstrap(
    IffFile Lot,
    SimulationWorld World,
    ObjectRegistry Registry,
    GlobalLibrary Library,
    IffFile? Global,
    string? NeighborhoodPath,
    IffFile? Neighborhood,
    ushort? ActiveFamilyChunkId,
    Family? ActiveFamily,
    LotMigrationSummary? LotMigration = null)
{
    private static readonly ConcurrentDictionary<string, Lazy<GlobalLibrary>> GlobalLibraryCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, IffFile> GlobalSpritesCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, CareerCatalog> CareerCache = new(StringComparer.OrdinalIgnoreCase);

    internal static GlobalLibrary GetOrLoadGlobalLibrary(string gameRoot)
    {
        var key = Path.GetFullPath(gameRoot);
        return GlobalLibraryCache.GetOrAdd(key, static root => new Lazy<GlobalLibrary>(() =>
        {
            var library = GlobalLibrary.Open(root);
            WarmSemiGlobals(library);
            return library;
        })).Value;
    }

    internal static CareerCatalog? GetOrLoadCareers(string gameRoot)
    {
        var key = Path.GetFullPath(gameRoot);
        if (CareerCache.TryGetValue(key, out var cached)) return cached;

        var loaded = SimCareerCatalog.Load(gameRoot);
        if (loaded is not null) CareerCache.TryAdd(key, loaded);
        return loaded;
    }

    private static void WarmSemiGlobals(GlobalLibrary library)
    {
        if (library.Folder is null || !Directory.Exists(library.Folder)) return;
        foreach (var path in Directory.EnumerateFiles(library.Folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (path.EndsWith(".far", StringComparison.OrdinalIgnoreCase))
            {
                var archive = FarArchive.Open(path);
                foreach (var entry in archive.Entries)
                {
                    var name = Path.GetFileName(entry.Name.Replace('\\', '/'));
                    if (name.EndsWith(".iff", StringComparison.OrdinalIgnoreCase))
                        _ = library.Load(name);
                }
            }
            else if (path.EndsWith(".iff", StringComparison.OrdinalIgnoreCase))
            {
                _ = library.Load(Path.GetFileName(path));
            }
        }
    }

    public static WorldBootstrap Load(string gameRoot, string lotPath, byte language = 1,
        Func<bool>? isPaused = null, int? lotKey = null, string? peopleFolder = null, bool autonomyForced = false,
        ushort? activeFamilyChunkId = null, string? userDataPath = null, IffFile? preloadedLot = null,
        Func<XPerson, XObject, short, string, float, float>? scoreAutonomy = null,
        IReadOnlyDictionary<uint, uint>? objectMigrations = null, bool stripMissingObjects = false,
        ObjectRegistry? existingRegistry = null)
    {
        var profile = Environment.GetEnvironmentVariable("SIMS_PROFILE") is not null;
        var stageStarted = Stopwatch.GetTimestamp();
        void Mark(string stage)
        {
            if (!profile) return;
            Console.Error.WriteLine($"Native load: {stage} {Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds:F0} ms");
            stageStarted = Stopwatch.GetTimestamp();
        }

        var fullLotPath = Path.GetFullPath(lotPath);
        var userData = userDataPath is null
            ? Path.GetDirectoryName(Path.GetDirectoryName(fullLotPath))
                ?? throw new InvalidDataException($"Cannot locate profile UserData for {lotPath}.")
            : Path.GetFullPath(userDataPath);
        var registry = existingRegistry ?? ObjectRegistry.Scan(gameRoot, peopleFolder ?? Path.Combine(userData, "Characters"));
        Mark($"object registry scan ({registry.Entries.Count} entries)");
        var library = GetOrLoadGlobalLibrary(gameRoot);
        Mark("global library open");
        var lot = preloadedLot ?? IffFile.Open(fullLotPath);
        var remappedByTypeIndex = new Dictionary<short, (uint From, uint To)>();
        if (objectMigrations is { Count: > 0 } && lot.OfType(ResourceType.Parse("objt")).FirstOrDefault() is { } objtChunk)
        {
            var table = ObjectTypeTable.Read(lot, objtChunk);
            var updatedEntries = new List<ObjectTypeEntry>(table.Entries.Count);
            foreach (var entry in table.Entries)
            {
                if (objectMigrations.TryGetValue(entry.Guid, out var toGuid) && registry.Find(toGuid) is not null)
                {
                    remappedByTypeIndex[entry.TypeIndex] = (entry.Guid, toGuid);
                    updatedEntries.Add(entry with { Guid = toGuid, TreeValue0 = uint.MaxValue, TreeValue1 = uint.MaxValue });
                }
                else
                {
                    updatedEntries.Add(entry);
                }
            }
            if (remappedByTypeIndex.Count > 0)
            {
                var body = objtChunk.Data.Span;
                var swap = body.Length > 0 && body[0] != 0;
                var tag = body.Length >= 12 ? System.Text.Encoding.ASCII.GetString(body[8..12]) : "tjbo";
                lot = lot.WithChunkDataResized(objtChunk.Type, objtChunk.Id, table.Write(swap, tag, updatedEntries));
            }
        }
        Mark("lot file read");
        var neighborhoodPath = Path.Combine(userData, "Neighborhood.iff");
        if (!File.Exists(neighborhoodPath)) neighborhoodPath = Path.Combine(userData, "UserData", "Neighborhood.iff");
        var neighborhood = File.Exists(neighborhoodPath) ? IffFile.Open(neighborhoodPath) : null;
        Mark("neighborhood read");
        var world = SavedLotObjectLoader.Load(lot, registry, library, language, neighborhood);
        world.GlobalSprites = LoadGlobalSprites(gameRoot);
        if (registry.Find(0xB6C90029) is { } magicMaster)
        {
            var spellRows = magicMaster.File.Find(ResourceType.Str, 15);
            if (spellRows is not null)
            {
                world.MagicMasterSpellItemIds = StringTable.Read(magicMaster.File, spellRows).ForLanguage(language)
                    .Skip(1)
                    .Select(value => short.TryParse(value, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var itemId) ? (short?)itemId : null)
                    .Where(itemId => itemId is not null)
                    .Select(itemId => itemId!.Value)
                    .ToArray();
            }
        }
        Mark("world restore");
        world.CareerHost = GetOrLoadCareers(gameRoot);
        Mark("career catalog load");
        Neighborhoods.LoadScriptVariables(world, neighborhoodPath);
        var familyId = world.WalkOrder.OfType<XPerson>().Select(person => (ushort)person[PersonData.FamilyNumber])
            .FirstOrDefault(id => id != 0);
        var house = lotKey ?? ParseHouseNumber(fullLotPath);
        world.ActiveLotNumber = house;
        (ushort ChunkId, Family Family)? active = null;
        if (neighborhood is not null)
        {
            var records = neighborhood.OfType(ResourceType.Parse("FAMI"))
                .Where(chunk => chunk.Id != 0)
                .Select(chunk => (chunk.Id, Family: Family.Read(neighborhood, chunk))).ToArray();
            // Selects the household by FAMI resource chunk ID. Chunk ID 0 is a neighborhood sentinel and
            // cannot be chosen as an active family.
            active = activeFamilyChunkId is { } requestedFamilyId && requestedFamilyId != 0 &&
                records.FirstOrDefault(record => record.Id == requestedFamilyId) is var requested && requested.Family is not null
                ? requested
                : familyId != 0 && records.FirstOrDefault(record => record.Id == familyId) is var byFamily && byFamily.Family is not null
                ? byFamily
                : house != 0 && records.FirstOrDefault(record => record.Family.HouseNumber == house && (record.Family.Flags & 1) != 0) is var byHouse && byHouse.Family is not null
                    ? byHouse
                    : house != 0 && records.FirstOrDefault(record => record.Family.HouseNumber == house) is var byAnyHouse && byAnyHouse.Family is not null
                        ? byAnyHouse
                        : null;
        }
        var activeFamily = active?.Family;
        var homeFamily = unchecked((short)(active?.ChunkId ?? familyId));
        // Sets neighborhood data slot 2 to the active household's house number so visitor generators can
        // reject members of the current family.
        if (activeFamily is not null) world.NeighborhoodData[2] = checked((short)house);
        world.FamilyBudget = new FamilyBudget(world, activeFamily, active?.ChunkId);
        world.NeighborhoodFamilies = new NeighborhoodFamilies(world, neighborhood, active?.ChunkId);
        // Registers active family member GUIDs so neighbor interaction systems can check household
        // membership.
        if (activeFamily is not null)
            world.NeighborInteractionManagerTypeKeys.AddRange(activeFamily.Members.Select(guid => unchecked((int)guid)));
        world.MenuTextSources = MenuTextSources.Load(world, gameRoot, language, neighborhood);
        world.StateClasses.AddRange(Neighborhoods.StateClasses(userData));
        world[SimulationGlobal.CurrentFamily] = homeFamily;
        world[SimulationGlobal.CurrentHouse] = checked((short)house);
        var isNonResidentialLot =
            (house > 0 && Neighborhoods.IsCommunityLot(userData, checked((short)house))) ||
            house is (>= 21 and <= 31) or (>= 40 and <= 49) or (>= 81 and <= 89) or (>= 93 and <= 99);
        if (activeFamily is null && isNonResidentialLot)
            world[SimulationGlobal.SimlessBuildMode] = 1;
        world.RefreshNeighborInteractionManager();
        AvatarAnimations.Populate(world, library.Global, gameRoot, userData, registry);
        world.LocomotionStrideResolver = (person, animationName) =>
            AvatarSceneGeometry.For(gameRoot, registry).LocomotionStrides(person, animationName);
        Mark("avatar animation catalog");
        world.AutonomyForced = autonomyForced;
        if (library.Global is { } global)
            world.AutonomyHost = new AutonomyHost(world, global, house, isPaused ?? (() => false), scoreAutonomy);
        LotMigrationSummary? lotMigration = null;
        if (remappedByTypeIndex.Count > 0 || stripMissingObjects)
        {
            var (removed, migrated, refund, resetIds) = world.ApplyPackObjectMigration(registry, remappedByTypeIndex, stripMissingObjects);
            lotMigration = new LotMigrationSummary(
                removed.Select(r => new RemovedObject(r.Guid, r.ObjectId, r.Name, r.Refund)).ToArray(),
                migrated.Select(m => new MigratedObject(m.From, m.To, m.ObjectId)).ToArray(),
                refund,
                resetIds);
        }
        // Post-load object reset trees may access careers, neighborhood state, and autonomy, requiring
        // those services to be initialized first.
        world.ResetObjectsWithChangedCode();
        TutorialEvents.EnsureController(world);
        ExpansionBootstrap.EnsureGlobalSimObjects(world, registry);
        ExpansionBootstrap.EnsureSpellboundController(world, userData, house, activeFamily);
        ExpansionBootstrap.EnsurePhonePlugins(world);
        Mark("host setup");
        return new WorldBootstrap(lot, world, registry, library, library.Global, neighborhoodPath, neighborhood,
            active?.ChunkId, activeFamily, lotMigration);
    }

    private static int ParseHouseNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("House", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(name.AsSpan(5), out var house) ? house : 0;
    }

    private static IffFile? LoadGlobalSprites(string gameRoot)
    {
        var key = Path.GetFullPath(gameRoot);
        if (GlobalSpritesCache.TryGetValue(key, out var cached)) return cached;

        var loaded = ReadGlobalSprites(gameRoot);
        if (loaded is not null) GlobalSpritesCache.TryAdd(key, loaded);
        return loaded;
    }

    private static IffFile? ReadGlobalSprites(string gameRoot)
    {
        var dataFolder = Directory.Exists(gameRoot)
            ? Directory.EnumerateDirectories(gameRoot).FirstOrDefault(path =>
                string.Equals(Path.GetFileName(path), "GameData", StringComparison.OrdinalIgnoreCase))
            : null;
        foreach (var folder in new[] { dataFolder, gameRoot }.Where(path => path is not null))
        {
            var path = Directory.EnumerateFiles(folder!).FirstOrDefault(file =>
                string.Equals(Path.GetFileName(file), "Sprites.iff", StringComparison.OrdinalIgnoreCase));
            if (path is not null) return IffFile.Open(path);
        }
        return null;
    }
}

public static class ExpansionBootstrap
{
    public const int SpellboundControllerGuid = unchecked((int)0x994F1DEC);
    public const int MagicStarterKitGuid = 0x197CDB3C;

    public static readonly int[] ExpansionPhonePluginGuids =
    [
        unchecked((int)0xA6F31853),
        unchecked((int)0xABA9DF4A),
        unchecked((int)0xEAA79D86),
        unchecked((int)0xC61F8102),
        unchecked((int)0x99197314),
    ];

    private sealed record GlobalSimObjectsCache(int EntryCount, ObjectEntry[] Entries);
    private static readonly ConditionalWeakTable<ObjectRegistry, GlobalSimObjectsCache> GlobalSimObjectsByRegistry = new();

    private static ObjectEntry[] GetGlobalSimObjects(ObjectRegistry registry)
    {
        if (GlobalSimObjectsByRegistry.TryGetValue(registry, out var cached) &&
            cached.EntryCount == registry.Entries.Count)
        {
            return cached.Entries;
        }

        var filtered = registry.Entries
            .Where(entry =>
                entry.Definition[ObjdField.Disabled] == 0 &&
                entry.Definition[ObjdField.Type] is 7 or 12 &&
                entry.Definition[ObjdField.IsGlobalSimObject] != 0 &&
                (entry.Definition[ObjdField.MasterId] == 0 || entry.Definition[ObjdField.SubIndex] == ushort.MaxValue))
            .ToArray();
        GlobalSimObjectsByRegistry.AddOrUpdate(registry, new GlobalSimObjectsCache(registry.Entries.Count, filtered));
        return filtered;
    }

    /// <summary>Ensures that every enabled SimType or Controller object definition with the global Sim
    /// object flag set in its OBJD data has an active instance on the lot.</summary>
    public static void EnsureGlobalSimObjects(SimulationWorld world, ObjectRegistry registry)
    {
        if (world.ObjectFactory is null ||
            world[SimulationGlobal.IsTutorialHouseNoYes] != 0 ||
            world.ActiveLotNumber == 7) return;

        foreach (var entry in GetGlobalSimObjects(registry))
        {
            var masterId = entry.Definition[ObjdField.MasterId];

            var guid = unchecked((int)entry.Guid);
            var existing = world.WalkOrder.Where(item => item.TypeRecord?.Key == guid).ToArray();
            if (existing.Length > 1)
            {
                for (var i = 1; i < existing.Length; i++)
                    world.Remove(existing[i].Id);
            }
            if (existing.Length > 0) continue;

            if (masterId != 0 && world.ObjectGroupFactory is not null)
            {
                _ = world.ObjectGroupFactory(guid);
                continue;
            }

            var instance = world.ObjectFactory(guid);
            if (instance is null) continue;
            if (!world.WalkOrder.Contains(instance)) world.Add(instance);
            world.InitializeCreatedObject(instance);
            if (SimulationWorld.FunctionTreeId(instance, 2) is var loadTree && loadTree != 0 && guid != unchecked((int)0x8963D7C1))
                world.RunFunctionToCompletion(instance, 2);
        }
        if (world.PendingRemovals.Count > 0)
        {
            foreach (var id in world.PendingRemovals.Distinct().ToArray())
                world.KillObject(id);
            world.PendingRemovals.Clear();
        }
    }

    /// <summary>Spawns the magic starter box controller out-of-world on inhabited residential lots if not
    /// already present and not yet delivered.</summary>
    public static XObject? EnsureSpellboundController(SimulationWorld world, string userData, int house, Family? activeFamily)
    {
        if (activeFamily is null || house <= 0) return null;
        if (Neighborhoods.IsCommunityLot(userData, checked((short)house))) return null;

        if (world.WalkOrder.Any(item => item.TypeRecord?.Key == SpellboundControllerGuid ||
                                        item.TypeRecord?.Key == MagicStarterKitGuid))
        {
            return null;
        }

        var controller = world.ObjectFactory?.Invoke(SpellboundControllerGuid);
        if (controller is null) return null;

        world.Add(controller);
        world.InitializeCreatedObject(controller);
        return controller;
    }

    /// <summary>Ensures all expansion travel phone plugins are instantiated out-of-world with Category 1 so
    /// phones can discover them via SetToNext mode 6.</summary>
    public static void EnsurePhonePlugins(SimulationWorld world)
    {
        if (world.ObjectFactory is null) return;

        foreach (var guid in ExpansionPhonePluginGuids)
        {
            if (world.WalkOrder.Any(item => item.TypeRecord?.Key == guid)) continue;

            var plugin = world.ObjectFactory.Invoke(guid);
            if (plugin is null) continue;

            world.Add(plugin);
            world.InitializeCreatedObject(plugin);
            plugin[ObjectData.Category] = 1;
        }
    }
}
