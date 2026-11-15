using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.World;

namespace SimsEngine.Simulation;

/// <summary>Builds the native VM's object model from a saved lot's object-type table and ObjM states. Sims.exe
/// FUN_0043fe4a first creates each class with FUN_004409db/FUN_0044350d, then restores its state through vtable
/// +0x7c; this loader preserves the ObjM walk order and applies each decoded state to its native instance.</summary>
public static class SavedLotObjectLoader
{
    private static readonly ResourceType ObjectTypesType = ResourceType.Parse("objt");
    private static readonly ResourceType LotObjectsType = ResourceType.Parse("ObjM");
    private static readonly ResourceType TypeAttributesType = ResourceType.Parse("TATT");
    private static readonly ResourceType SlotsType = ResourceType.Parse("SLOT");
    private static readonly HashSet<short> GenericSimsCallPersonRecordTypes = [2, 0x200, 0x400, 0x800, 0x1000];

    /// <summary>Reads the saved object structures from <paramref name="lot"/> and restores every object whose
    /// definition is installed or can be replaced by one of Sims.exe's type-specific missing-definition placeholders.</summary>
    public static SimulationWorld Load(IffFile lot, ObjectRegistry registry, GlobalLibrary library, byte language = 1,
        IffFile? neighborhood = null)
    {
        var typesChunk = lot.OfType(ObjectTypesType).FirstOrDefault()
            ?? throw MissingChunk(lot, ObjectTypesType);
        var objectsChunk = lot.OfType(LotObjectsType).FirstOrDefault()
            ?? throw MissingChunk(lot, LotObjectsType);
        var types = ObjectTypeTable.Read(lot, typesChunk);
        var entriesByGuid = new Dictionary<uint, ObjectEntry?>();
        foreach (var type in types.Entries)
        {
            var entry = registry.Find(type.Guid);
            if (entry is null && PlaceholderGuid(type) is { } placeholderGuid)
                entry = registry.Find(placeholderGuid);
            entriesByGuid[type.Guid] = entry;
        }
        var definition = new Func<uint, Objd?>(guid => entriesByGuid.GetValueOrDefault(guid)?.Definition);
        var objects = LotObjects.Read(lot, objectsChunk, types, definition, decodeUnknownAsObject: true);
        var attributes = ReadMergedTypeAttributes(lot, neighborhood);
        var neighbors = ReadAdjacentNeighbors(lot, neighborhood);
        var familyNumbers = ReadAdjacentFamilyNumbers(lot, neighborhood);
        // Sims.exe FUN_0046d0cf restores SIMI +0xac as the random state when FUN_004074bc holds game time;
        // otherwise it replaces the saved value with timeGetTime(). The native loader preserves the saved state
        // for deterministic replay; production startup still needs the original hold/time-seed gate.
        var simulationState = lot.OfType(ResourceType.Parse("SIMI")).FirstOrDefault() is { } simulationStateChunk
            ? Formats.SimulationState.Read(lot, simulationStateChunk)
            : null;
        var objectClasses = new Dictionary<short, (ObjectClass Class, bool Placeholder)>();
        foreach (var type in types.Entries)
            if (entriesByGuid.GetValueOrDefault(type.Guid)?.Definition is { } objd && LotObjects.ClassOf(objd) is { } objectClass)
                objectClasses[type.TypeIndex] = (objectClass, LotObjects.IsPlaceholder(type.Guid));
        var world = Restore(objects.Objects, types.Entries, guid => entriesByGuid.GetValueOrDefault(guid), library.ScopeOf,
            attributes, objects.Version, registry, neighbors, simulationState, language, familyNumbers);
        world.ObjectRegistry = registry;
        world.SavedLotObjects = objects;
        world.SavedObjectClasses = objectClasses;
        if (library.Global?.Find(ResourceType.Slot, 100) is { } lotRoutingChunk)
            world.LotRoutingSlots.AddRange(SlotTable.Read(library.Global, lotRoutingChunk).Slots
                .Where(slot => slot.Type == 3).Select(RoutingSlotRequest.FromDefinition));
        world.PersonMotiveDecayTuning = library.Global is { } global
            ? PersonMotiveDecayTuning.Read(global)
            : null;
        var constants = library.Global?.Find(ResourceType.Fcns, 2) is { } constantsChunk
            ? SimulatorConstants.Read(library.Global, constantsChunk)
            : SimulatorConstants.Empty;
        world.ApplySimulatorConstants(constants);
        // Room refresh lazily consumes the original Global FCNS values (FUN_004666a9) before computing
        // score/light records, so apply the constants before the saved-lot 0xf0 room-map lifecycle runs.
        world.LotTileFlags = SimsEngine.World.LotTiles.Read(lot);
        world.LoadRoomMaps(ReadRoomMaps(lot, simulationState?.Version ?? 0));
        world.RefreshSavedPortalWallStyles();
        world.PlacementEligibilityGrids[1] = ReadPlacementEligibilityGrid(lot, simulationState?.Version ?? 0);
        // FUN_00440b6d constructs created objects through the same definition registry the lot was restored from.
        world.ObjectFactory = guid => CreateInstance(world, unchecked((uint)guid), registry, library, language, registerSavedType: true);
        world.ObjectGroupFactory = guid => CreateObjectGroup(world, unchecked((uint)guid), registry, library, language, registerSavedType: true);
        world.PlacementPreviewFactory = guid => CreateInstance(world, unchecked((uint)guid), registry, library, language, registerSavedType: false);
        world.BuildCursorFactory = guid => CreateInstance(world, unchecked((uint)guid), registry, library, language, registerSavedType: false, toolObject: true);
        world.PlacementPreviewGroupFactory = guid => CreateObjectGroup(world, unchecked((uint)guid), registry, library, language, registerSavedType: false);
        return world;
    }

    private static IReadOnlyList<XObject>? CreateObjectGroup(SimulationWorld world, uint guid,
        ObjectRegistry registry, GlobalLibrary library, byte language, bool registerSavedType)
    {
        var masterEntry = registry.Find(guid);
        if (masterEntry is null || masterEntry.Definition[ObjdField.MasterId] == 0) return null;
        // FUN_00440b6d only creates a group when FUN_0043aedc accepts the requested definition (SubIndex -1).
        // A part definition by itself returns no object.
        if (masterEntry.Definition[ObjdField.SubIndex] != ushort.MaxValue) return [];

        var masterId = masterEntry.Definition[ObjdField.MasterId];
        // FUN_0043adda finds the lead part first: same file and MasterId, with MtLeadObject (OBJD +0x7e) nonzero,
        // or falling back to FUN_0043ad41(masterEntry, 0, 0, 0) (SubIndex 0, LevelOffset 0).
        // FUN_0043ae5e then scans the ordered type-definition vector for all parts in the same file with the same
        // MasterId whose SubIndex is not -1 (FUN_0043aedc == 0); FUN_00440b6d skips the lead part when FUN_0043ae5e
        // visits it again. The SubIndex -1 master definition itself is catalog-only and is never instantiated.
        var allParts = registry.Entries.Where(entry => ReferenceEquals(entry.File, masterEntry.File) &&
            entry.Definition[ObjdField.MasterId] == masterId &&
            entry.Definition[ObjdField.SubIndex] != ushort.MaxValue).ToArray();
        if (allParts.Length == 0) return [];
        var leadEntry = allParts.FirstOrDefault(entry => entry.Definition[ObjdField.MtLeadObject] != 0)
            ?? allParts.FirstOrDefault(entry => entry.Definition[ObjdField.SubIndex] == 0 && entry.Definition[ObjdField.LevelOffset] == 0)
            ?? allParts[0];
        var orderedParts = new List<ObjectEntry>(allParts.Length) { leadEntry };
        foreach (var entry in allParts)
            if (!ReferenceEquals(entry, leadEntry)) orderedParts.Add(entry);

        var created = new List<XObject>(orderedParts.Count);
        using var savedTypes = world.BeginSavedObjectTypeRegistrationScope();
        try
        {
            XObject? root = null;
            foreach (var entry in orderedParts)
            {
                var part = CreateInstance(world, entry.Guid, registry, library, language, registerSavedType);
                if (part is null) throw new InvalidDataException($"Multi-tile definition {entry.Guid:X8} could not be instantiated.");
                // FUN_004318eb reads SubIndex's high byte as signed X and its low byte as unsigned Y. The source
                // multi-tile initializer rotates that pair into +0x1a0/+0x1a4; MultiTileOffset consumes those
                // latter fields in sixteenths of a tile.
                var subIndex = unchecked((ushort)entry.Definition[ObjdField.SubIndex]);
                var offsetX = unchecked((sbyte)(subIndex >> 8));
                var offsetY = (byte)(subIndex & 0xff);
                var levelOffset = unchecked((short)entry.Definition[ObjdField.LevelOffset]);
                if (root is null)
                {
                    root = part;
                    root.SavedMultiTileState = new MultiTileState(offsetX, offsetY, levelOffset,
                        offsetX, offsetY, levelOffset, 0, null);
                }
                else
                {
                    part.MultiTileMasterId = root.Id;
                    part.SavedMultiTileState = new MultiTileState(offsetX, offsetY, levelOffset,
                        offsetX, offsetY, levelOffset, root.Id, null);
                    created[^1].MultiTileNextId = part.Id;
                }
                world.Add(part);
                created.Add(part);
            }
            if (root is not null)
                root[ObjectData.CurrentValue] = unchecked((short)masterEntry.Definition[ObjdField.Price]);
            world.InitializeCreatedObjectGroup(created);
            if (registerSavedType) savedTypes.Commit();
            return created;
        }
        catch
        {
            foreach (var instance in created.AsEnumerable().Reverse()) world.Remove(instance.Id);
            // The group may have registered its root/earlier members before a later constructor failed.
            return [];
        }
    }

    /// <summary>FUN_00440b6d's construction path for Create New Object Instance: a definition from the registry, its
    /// OBJD fields, own slot definitions, type record (function and check tables, behavior scope, interaction table),
    /// the +0x80 creation flag. Registration, activation and placement stay with the caller. Person data and the
    /// init trees are not initialized.</summary>
    /// <summary>FUN_0043194c sizes the instance attribute table (+0x2c, count at +0x30) from OBJD NumAttributes (+0x74),
    /// with a minimum of 8.</summary>
    private static int InstanceAttributeCount(ObjectEntry entry) => Math.Max((int)entry.Definition[ObjdField.NumAttributes], 8);

    internal static XObject? CreateInstance(SimulationWorld world, uint guid, ObjectRegistry registry, GlobalLibrary library, byte language,
        bool registerSavedType, bool toolObject = false)
    {
        var entry = registry.Find(guid);
        if (entry is null) return null;
        if (!toolObject && entry.Definition[ObjdField.MasterId] != 0 && entry.Definition[ObjdField.SubIndex] == ushort.MaxValue)
            return null;
        var objectClass = LotObjects.ClassOf(entry.Definition) ?? (toolObject ? ObjectClass.Object : null);
        if (objectClass is null) return null;
        var definition = new short[XObject.DefinitionCount];
        for (var field = 0; field < Math.Min(definition.Length, entry.Definition.Fields.Count); field++)
            definition[field] = unchecked((short)entry.Definition.Fields[field]);
        var id = world.NextObjectId();
        XObject instance = objectClass == ObjectClass.Person
            ? new XPerson(id, definition, InstanceAttributeCount(entry))
            : new XObject(id, definition, InstanceAttributeCount(entry));
        instance.RuntimeClass = objectClass.Value;
        instance.RequestKey = id;
        instance.Flags = 0x80; // FUN_00440b6d sets +0x114 bit 7
        instance.DynamicSpriteFlags = new short[entry.Definition[ObjdField.NumDynamicSprites]];
        instance.TypeAttributes = world.GetOrCreateTypeAttributes(guid, entry.Definition[ObjdField.NumAttributes]);
        RestoreSlotDefinitions(instance, entry);
        PadSlotVector(instance);
        var scope = library.ScopeOf(entry);
        if (registerSavedType)
            world.RegisterSavedObjectType(new ObjectTypeEntry(guid,
                SavedTreeFingerprint(scope, entry.ActionFunctions.ElementAtOrDefault(0)),
                SavedTreeFingerprint(scope, entry.ActionFunctions.ElementAtOrDefault(1)), 0,
                unchecked((short)entry.Definition[ObjdField.Type]),
                entry.Definition[ObjdField.MasterId] == 0 || entry.Definition[ObjdField.SubIndex] == ushort.MaxValue ? entry.Chunk.Label : string.Empty,
                unchecked((int)((uint)entry.Definition.Fields.ElementAtOrDefault(70) | ((uint)entry.Definition.Fields.ElementAtOrDefault(71) << 16)))),
                objectClass.Value, scope);
        var alternativeType = registry.FindAlternativeType(entry);
        instance.TypeRecord = new TypeRecord(unchecked((int)guid))
        {
            ObjectType = unchecked((short)entry.Definition[ObjdField.Type]),
            SubIndex = unchecked((short)entry.Definition[ObjdField.SubIndex]),
            ObjectVersion = unchecked((short)entry.Definition[ObjdField.ObjectVersion]),
            AnimatorBodyStringMode = AnimatorBodyStringMode(entry, language),
            FootprintMask = entry.Definition[ObjdField.FootprintMask],
            Name = entry.Chunk.Label,
            CatalogName = entry.CatalogString(0, language),
            CatalogDescription = entry.CatalogString(1, language),
            FunctionTable = entry.FunctionEntries.Select(function => function.Action).ToArray(),
            FunctionCheckTable = entry.FunctionEntries.Select(function => function.Guard).ToArray(),
            BehaviorScope = scope,
            Interactions = entry.HasNoMasterInteractionTable ? null :
                entry.FindInteractionTable(scope, language, registry.FindAlternativeType(entry)),
            AlternativeTypeKey = alternativeType is null ? null : unchecked((int)alternativeType.Guid),
            AlternativeObjectPrice = alternativeType is null ? null : unchecked((short)alternativeType.Definition[ObjdField.Price]),
            AutonomyAlternativeTypeFlag = alternativeType is null ? (short)0 :
                unchecked((short)alternativeType.Definition[ObjdField.BuildModeType]),
        };
        InitializeGenericSimsCallPersonTypeRecords(instance, registry, language);
        if (instance is XPerson person)
        {
            person.SavedCharacter = DefaultPersonCharacter(entry, language);
            person.AnimatorBodyStringMode = instance.TypeRecord.AnimatorBodyStringMode ?? 0;
        }
        return instance;
    }

    /// <summary>SAnimator::ResetSuits (00588907.c) calls cXObject::GetBodyString(0xd, -1) (1-based entry 13 = 0-based index 12) and maps its sex/species token to +0x60e.</summary>
    internal static short? AnimatorBodyStringMode(ObjectEntry entry, byte language)
    {
        var id = entry.Definition[ObjdField.BodyStringsId];
        if (id == 0 || entry.File.Find(ResourceType.Str, id) is not { } chunk) return null;
        var strings = StringTable.Read(entry.File, chunk).ForLanguage(language);
        return (strings.Count > 12 ? MapAnimatorBodyString13(strings[12]) : null)
            ?? (strings.Count > 13 ? MapAnimatorBodyString13(strings[13]) : null);
    }

    internal static short? MapAnimatorBodyString13(string value) => value.Trim().ToLowerInvariant() switch
        {
            "male" => 0,
            "female" => 1,
            "dogmale" => 2,
            "dogfemale" => 3,
            "catmale" => 4,
            "catfemale" => 5,
            _ => null,
        };

    /// <summary>New cXPerson instances load their selector's Body Strings during construction
    /// (Mac ObjSelector::GetBodyStrings). Seed the persistent character with its default body and head
    /// so the native renderer and lot serializer have the same base appearance before any saved state
    /// or clothing change is applied.</summary>
    private static PersonCharacter? DefaultPersonCharacter(ObjectEntry entry, byte language)
    {
        var id = entry.Definition[ObjdField.BodyStringsId];
        if (id == 0 || entry.File.Find(ResourceType.Str, id) is not { } chunk) return null;
        var strings = StringTable.Read(entry.File, chunk).ForLanguage(language);
        if (strings.Count < 3) return null;

        static (string, string) Pair(string value)
        {
            var separator = value.IndexOf(',');
            return separator < 0 ? (value, string.Empty) : (value[..separator], value[(separator + 1)..]);
        }

        var parts = Enumerable.Repeat((string.Empty, string.Empty), 5).ToArray();
        parts[0] = Pair(strings[1]);
        parts[4] = Pair(strings[2]);
        return new PersonCharacter(0, 0, parts, [], [string.Empty, string.Empty, string.Empty], 3, new float[9]);
    }

    /// <summary>FUN_00457759/0045778a select function 0/1; FUN_00402741 combines the root's version in the high
    /// word with the sum of versions of every distinct reachable tree. It masks each opcode with 0x7fff before the
    /// above-255 call test and pushes the masked id.</summary>
    public static uint SavedTreeFingerprint(ITreeTable? scope, ushort root)
    {
        if (scope is null) return 0;
        var pending = new Stack<ushort>();
        var seen = new HashSet<ushort>();
        pending.Push(root);
        var rootTree = scope.Find(unchecked((short)root));
        var result = unchecked((rootTree?.TreeVersion ?? 0) << 16);
        while (pending.TryPop(out var id))
        {
            if (!seen.Add(id)) continue;
            var tree = scope.Find(unchecked((short)id));
            if (tree is null) continue;
            result = unchecked(result + tree.TreeVersion);
            foreach (var instruction in tree.Instructions)
            {
                var call = (ushort)(instruction.Opcode & 0x7fff);
                if (call > 255) pending.Push(call);
            }
        }
        return result;
    }

    /// <summary>FUN_0047870b loads upper-level Arry wall layers, but clears their runtime wall grids when the SIMI
    /// stream version is at most 0x16. Keep the saved Arry-backed room data independently; the lot manager
    /// dimension is 64 tiles in Sims.exe FUN_00415c5b.</summary>
    public static IReadOnlyDictionary<int, RoomMap> ReadRoomMaps(IffFile lot, int simulationVersion)
    {
        ArgumentNullException.ThrowIfNull(lot);
        var result = new Dictionary<int, RoomMap>();
        for (var level = 1; level <= 2; level++)
            if (RoomMap.HasLevel(lot, level))
                result.Add(level, RoomMap.Build(lot, level, LotGeometry.LotDimension, LotGeometry.LotDimension,
                    importRuntimeWallGrid: level == 1 || simulationVersion > 0x16));
        return result;
    }

    /// <summary>FUN_0047870b loads Arry 10 into the level-one placement-eligibility grid for SIMI versions
    /// greater than 0x34. FUN_00477a73 initializes that grid to 0xff; older versions take FUN_004776fd, which
    /// walks the grid coordinates and writes the same 0xff sentinel across the plane.</summary>
    public static byte[] ReadPlacementEligibilityGrid(IffFile lot, int simulationVersion)
    {
        ArgumentNullException.ThrowIfNull(lot);
        var result = new byte[LotGeometry.LotDimension * LotGeometry.LotDimension];
        Array.Fill(result, (byte)0xff);
        // Before SIMI 0x35, FUN_0047870b calls FUN_004776fd rather than reading Arry 10. That helper enumerates
        // the full grid and writes 0xff into each cell, so the constructor-equivalent result above is final.
        if (simulationVersion < 0x35 || lot.Find(ResourceType.Parse("Arry"), 10) is not { } chunk)
            return result;

        var array = LotArray.Read(lot, chunk);
        if (array.Rows < LotGeometry.LotDimension || array.Width < LotGeometry.LotDimension || array.ElementSize != 1)
            throw new InvalidDataException("Arry 10 eligibility grid must contain a 64 by 64 byte plane.");
        // FUN_0047870b and FUN_004790bc index the grid [x][y].
        for (var x = 0; x < LotGeometry.LotDimension; x++)
        for (var y = 0; y < LotGeometry.LotDimension; y++)
            result[x * LotGeometry.LotDimension + y] = array.Element(x, y)[0];
        return result;
    }

    /// <summary>Restores already-decoded ObjM records. This overload is also used by differential tests so the
    /// object-state mapping can be verified independently of the resource-stream decoder.</summary>
    public static SimulationWorld Restore(
        IReadOnlyList<LotObject> savedObjects,
        IEnumerable<ObjectTypeEntry> savedTypes,
        Func<uint, ObjectEntry?> findDefinition,
        Func<ObjectEntry, ITreeTable?> findBehaviorScope,
        IReadOnlyDictionary<uint, short[]>? attributesByGuid = null,
        int objectManagerVersion = 0x39,
        ObjectRegistry? typeRegistry = null,
        Neighbors? savedNeighbors = null,
        SimulationState? savedSimulationState = null,
        byte language = 1,
        IReadOnlyDictionary<uint, int>? familyNumbersByGuid = null)
    {
        ArgumentNullException.ThrowIfNull(savedObjects);
        ArgumentNullException.ThrowIfNull(savedTypes);
        ArgumentNullException.ThrowIfNull(findDefinition);
        ArgumentNullException.ThrowIfNull(findBehaviorScope);

        var typesByIndex = new Dictionary<short, ObjectTypeEntry>();
        foreach (var type in savedTypes)
            if (!typesByIndex.TryAdd(type.TypeIndex, type))
                throw new InvalidDataException($"Lot object type index {type.TypeIndex} appears more than once.");

        var world = new SimulationWorld();
        world.SavedSimulationState = savedSimulationState;
        world.SavedObjectManagerVersion = objectManagerVersion;
        if (typeRegistry is not null)
        {
            // FUN_004e8c79 initializes DAT_006f2648 by walking the ordered type-record vector and adding a 0x44-byte
            // row for each OBJD whose type is Person (2). FUN_004e8b76 appends any later person type record missing
            // from that table. ObjectRegistry retains the source scan order, and duplicate keys remain ordered because
            // FUN_004e988f returns the first matching row.
            foreach (var entry in typeRegistry.Entries)
            {
                if (entry.Definition[ObjdField.Type] != XPerson.PersonType) continue;
                var key = unchecked((int)entry.Guid);
                world.PersonRecordKeys.Add(key);
            }
            world.PersonRecordManagerPresent = true;
        }
        if (savedSimulationState is { } loadedSimulationState)
        {
            // SIMI carries the lot's global table; FUN_0046d0cf reads it with the rest of the simulation state.
            for (var global = 0; global < Math.Min(loadedSimulationState.Globals.Count, world.Globals.Length); global++)
                world.Globals[global] = loadedSimulationState.Globals[global];
            // FUN_0046d0cf restores these SIMI entries into the date-manager fields at +0x10 (Hour), +0x12
            // (DayOfMonth), +0x1e (Month), and +0x20 (Year); FUN_00432396 reads the latter three directly.
            world.UseSimulationGlobalsForCurrentDate();
            // FUN_0046d0cf restores the clock words from the same global table; the native clock otherwise starts
            // at midnight and a saved 06:30 lot would export night lighting.
            world.Clock.Restore(world[SimulationGlobal.Hour], world[SimulationGlobal.Minute], world[SimulationGlobal.Second]);
            if (loadedSimulationState.Version > 0x0d)
                world.ObjectManagerRandomState = unchecked((uint)loadedSimulationState.Field0xAC);
        }
        else
        {
            // FUN_0046cfd5 initializes the date manager to 1997/6/15 before any saved SIMI data is applied.
            world.CurrentDate = SimulationDate.Initial;
        }
        // FUN_0046cfd5 and FUN_00413d0e derive GameEdition from the installed pack markers, not the saved lot's old
        // SIMI value. Keep the same mask that gated ObjectRegistry's FUN_0043c6ee expansion scans.
        world[SimulationGlobal.GameEdition] = typeRegistry?.GameEditionMask ?? 0xff;
        if (savedNeighbors is not null)
        {
            world.SavedNeighbors = savedNeighbors;
            RestoreNeighbors(world, savedNeighbors, typeRegistry, familyNumbersByGuid);
        }
        else if (typeRegistry is not null)
        {
            RestoreCatalogNeighbors(world, typeRegistry, familyNumbersByGuid);
        }
        foreach (var type in typesByIndex.Values)
        {
            world.SavedObjectTypes.Add(type.TypeIndex, type);
            world.CodeOwnerTypeGuids.Add(type.TypeIndex, type.Guid);
            var typeEntry = findDefinition(type.Guid);
            if (typeEntry is null && PlaceholderGuid(type) is { } placeholderGuid)
                typeEntry = findDefinition(placeholderGuid);
            var typeScope = typeEntry is null ? null : findBehaviorScope(typeEntry);
            world.CodeOwnerScopes.Add(type.TypeIndex, typeScope);
            // FUN_0043a5fb compares the saved TreeValue0/1 with the current function 0/1 fingerprints and sets
            // TypeRecord +0xb4 bit 2 on a mismatch; FUN_00431edd later resets that type's objects.
            if (typeEntry is not null)
            {
                var tree0 = SavedTreeFingerprint(typeScope, typeEntry.ActionFunctions.ElementAtOrDefault(0));
                var tree1 = SavedTreeFingerprint(typeScope, typeEntry.ActionFunctions.ElementAtOrDefault(1));
                if (tree0 != type.TreeValue0 || tree1 != type.TreeValue1)
                {
                    world.ChangedCodeTypeGuids.Add(type.Guid);
                    world.CurrentObjectTypes[type.TypeIndex] = type with { TreeValue0 = tree0, TreeValue1 = tree1 };
                }
            }
        }
        if (attributesByGuid is not null)
        {
            foreach (var (guid, attributes) in attributesByGuid)
                world.SharedTypeAttributes[guid] = attributes.ToArray();
        }
        var seenIds = new HashSet<short>();
        var pendingPersonStates = new List<(XPerson Person, PersonState State)>();
        foreach (var saved in savedObjects)
        {
            if (!seenIds.Add(saved.ObjectId))
                throw new InvalidDataException($"Lot object id {saved.ObjectId} appears more than once.");
            if (!typesByIndex.TryGetValue(saved.TypeIndex, out var savedType))
                throw new InvalidDataException($"Lot object {saved.ObjectId} refers to missing object type index {saved.TypeIndex}.");

            var entry = findDefinition(savedType.Guid);
            if (entry is null && PlaceholderGuid(savedType) is { } placeholderGuid)
                entry = findDefinition(placeholderGuid);
            if (entry is null)
                throw new InvalidDataException($"Lot object {saved.ObjectId} needs missing definition {savedType.Guid:X8} and its placeholder.");
            var objectClass = LotObjects.ClassOf(entry.Definition);
            if (objectClass is null) continue; // Sims.exe creates no cXObject for unmastered types 8 and 9.

            var state = saved.Decoded
                ?? throw new InvalidDataException($"Lot object {saved.ObjectId} ({savedType.Guid:X8}) has no decoded {objectClass} state.");
            if (state.Values0x108.Count != 6)
                throw new InvalidDataException($"Lot object {saved.ObjectId} has {state.Values0x108.Count} common position/bounds fields; expected 6.");
            if (objectClass == ObjectClass.Person && state.Person is null)
                throw new InvalidDataException($"Lot person {saved.ObjectId} has no person-state block.");

            var definition = new short[XObject.DefinitionCount];
            for (var i = 0; i < Math.Min(definition.Length, entry.Definition.Fields.Count); i++)
                definition[i] = unchecked((short)entry.Definition.Fields[i]);

            XObject instance = objectClass == ObjectClass.Person
                ? new XPerson(saved.ObjectId, definition, state.Attributes.Length)
                : new XObject(saved.ObjectId, definition, state.Attributes.Length);
            instance.RuntimeClass = objectClass;
            // FUN_00431b5e initializes +0x84 to -5 via FUN_00433ef5. The saved-state virtual FUN_00434353
            // then restores the 0x48 object-data words beginning at +0x4a for these version-0x2f+ lots;
            // +0x84 is data field 29, so the following Data copy restores its saved room value after the initializer.
            // FUN_0044350d registers the newly created object in the lot manager under the saved ObjM id
            // and writes that same one-based lookup key to cXObject +0xf0.
            instance.RequestKey = saved.ObjectId;
            // FUN_0043194c allocates the attribute table from OBJD NumAttributes; the saved list may be shorter.
            var instanceAttributeCount = InstanceAttributeCount(entry);
            instance.Attributes = new short[Math.Max(instanceAttributeCount, state.Attributes.Length)];
            state.Attributes.CopyTo(instance.Attributes, 0);
            RestoreSlotDefinitions(instance, entry);
            state.Temps.CopyTo(instance.Temporaries, 0);
            state.Data.CopyTo(instance.Data, 0);
            // FUN_00440b6d allocates the dynamic-sprite array from OBJD NumDynamicSprites; the saved list may be
            // shorter, and the unused entries stay 0.
            var dynamicSpriteCount = (int)entry.Definition[ObjdField.NumDynamicSprites];
            instance.DynamicSpriteFlags = new short[Math.Max(dynamicSpriteCount, state.Values0x34.Length)];
            state.Values0x34.CopyTo(instance.DynamicSpriteFlags, 0);
            instance.Slots.AddRange(state.Slots);
            PadSlotVector(instance);
            // FUN_00434578 serializes each +0x130 record, including entry 0 (the vector sentinel).
            // Type-0 definitions populate records 1..N, while FootprintSlots omits that sentinel.
            for (var footprintIndex = 0; footprintIndex < instance.FootprintSlots.Count; footprintIndex++)
            {
                var relationIndex = footprintIndex + 1;
                if (relationIndex >= instance.Slots.Count) break;
                instance.FootprintSlots[footprintIndex].OccupantObjectId = instance.Slots[relationIndex];
            }
            // FUN_0043426c reads +0x108,+0x10c,+0x100,+0x104,+0xf8,+0xf4 in this order, and the u32s at +0xf8 and
            // +0xf4 are the x and y positions in sixteenths of a tile (ObjectHead.X and ObjectHead.Y).
            instance.Position = new ObjectPosition(state.Values0x108[4], state.Values0x108[5], state.Value0x110);
            // FUN_0043426c reads +0x108 (MinY), +0x10c (MinX), +0x100 (MaxY), +0x104 (MaxX), +0xf8 (X), +0xf4 (Y).
            instance.FootprintBounds = new FootprintBounds(
                state.Values0x108[3], state.Values0x108[2], state.Values0x108[1], state.Values0x108[0]);
            instance.FootprintBoundsDirection = instance[ObjectData.Direction];
            // FUN_0043fe4a marks every newly registered object with +0x80 before its virtual +0x7c restore.
            // FUN_0043426c only overwrites the flag word for ObjM version 0x37, so older saved versions retain it.
            instance.Flags = unchecked((uint)(state.Value0x114 ?? 0x80));
            instance.SavedStack.AddRange(state.Stack);
            instance.SavedStackSize = state.StackSize;
            instance.SavedMultiTileState = state.MultiTile;
            if (state.MultiTile is { } multiTile)
                instance.MultiTileMasterId = multiTile.LeadObject == saved.ObjectId ? (short)0 : multiTile.LeadObject;

            foreach (var relationship in state.Relationships)
            {
                if (relationship is null) continue;
                var target = checked((short)relationship.Value0x0C);
                instance.Relationships.Resize(target, relationship.Values.Length);
                for (var index = 0; index < relationship.Values.Length; index++)
                    instance.Relationships.Set(target, index, relationship.Values[index]);
            }

            // FUN_00440b6d allocates the type-attribute table from OBJD NumAttributes; a lot without a TATT entry
            // leaves it zero-filled instead of empty. All instances of the same GUID share the same backing array
            // (0043e1d5.c:31-53, 00458001.c, 00458026.c).
            var typeAttributeCount = (int)entry.Definition[ObjdField.NumAttributes];
            instance.TypeAttributes = world.GetOrCreateTypeAttributes(savedType.Guid, typeAttributeCount);

            var behaviorScope = findBehaviorScope(entry);
            var resourceScope = behaviorScope as BehaviorScope;
            var treeTableId = entry.Definition[ObjdField.TreeTableId];
            InteractionTable? interactionTable = null;
            if (treeTableId != 0 && !entry.HasNoMasterInteractionTable)
                interactionTable = entry.FindInteractionTable(resourceScope, language, typeRegistry?.FindAlternativeType(entry));
            var alternativeType = typeRegistry?.FindAlternativeType(entry);
            var functionEntries = entry.FunctionEntries;
            instance.TypeRecord = new TypeRecord(unchecked((int)savedType.Guid))
            {
                ObjectType = unchecked((short)entry.Definition[ObjdField.Type]),
                SubIndex = unchecked((short)entry.Definition[ObjdField.SubIndex]),
                ObjectVersion = unchecked((short)entry.Definition[ObjdField.ObjectVersion]),
                AnimatorBodyStringMode = AnimatorBodyStringMode(entry, language),
                FootprintMask = entry.Definition[ObjdField.FootprintMask],
                Name = entry.Chunk.Label,
                CatalogName = entry.CatalogString(0, language),
                CatalogDescription = entry.CatalogString(1, language),
                FunctionTable = functionEntries.Select(function => function.Action).ToArray(),
                FunctionCheckTable = functionEntries.Select(function => function.Guard).ToArray(),
                BehaviorScope = behaviorScope,
                Interactions = interactionTable,
                AlternativeTypeKey = alternativeType is null ? null : unchecked((int)alternativeType.Guid),
                AlternativeObjectPrice = alternativeType is null ? null : unchecked((short)alternativeType.Definition[ObjdField.Price]),
                AutonomyAlternativeTypeFlag = alternativeType is null ? (short)0 :
                    unchecked((short)alternativeType.Definition[ObjdField.BuildModeType]),
            };

            InitializeGenericSimsCallPersonTypeRecords(instance, typeRegistry, language);

            if (instance is XPerson person)
            {
                var personState = state.Person!;
                personState.PersonData.CopyTo(person.PersonData, 0);
                if (personState.Values0x78C.Length != person.Motives.Length)
                    throw new InvalidDataException($"Lot person {saved.ObjectId} has {personState.Values0x78C.Length} motive values; expected {person.Motives.Length}.");
                personState.Values0x78C.CopyTo(person.Motives, 0);
                if (personState.Values0x7CC.Length == person.SavedPersonValues7CC.Length)
                    personState.Values0x7CC.CopyTo(person.SavedPersonValues7CC, 0);
                person.SavedCharacter = personState.Character;
                person.AnimatorBodyStringMode = instance.TypeRecord.AnimatorBodyStringMode ?? 0;
                person.SavedPersonActionStorageCount = personState.Count0xAF0;
                person.SavedCurrentAction = personState.First;
                person.SavedQueuedAction = personState.Second;
                person.SavedActions.AddRange(personState.Actions);
                person.SavedPersonValues.AddRange(personState.Values);
                pendingPersonStates.Add((person, personState));
            }

            world.Add(instance);
            world.LoadedObjectInstances[saved.ObjectId] = instance;
            ApplyLoadedClockEligibility(world, instance, entry.Definition.Guid, objectManagerVersion, state.ManagerFlags);
        }

        // FUN_004227e6 appends each constructed part to the end of its master's +0x18c chain; the save stores only
        // the part's +0x190 lead, so rebuild the chain in registration order (the order the parts were constructed).
        foreach (var part in world.WalkOrder)
        {
            if (part.MultiTileMasterId == 0 || part.MultiTileMasterId == part.Id) continue;
            var master = world.ObjectById(part.MultiTileMasterId);
            if (master is null) continue;
            var tail = master;
            var visited = new HashSet<short> { master.Id };
            while (tail.MultiTileNextId != 0)
            {
                var next = world.ObjectById(tail.MultiTileNextId);
                if (next is null || !visited.Add(next.Id))
                {
                    tail = null;
                    break;
                }
                tail = next;
            }
            if (tail is not null) tail.MultiTileNextId = part.Id;
        }

        // FUN_00431c90/FUN_00431edd initialize manager bit 19 from FUN_004359a1. Resolve the multitile lead first;
        // only that root's private interaction table can make the root an autonomy candidate.
        foreach (var instance in world.WalkOrder)
        {
            var root = InteractionMenus.Target(world, instance);
            world.InitializeAutonomyCandidate(instance, root, root.TypeRecord?.Interactions);
        }

        foreach (var (person, state) in pendingPersonStates)
        {
            // FUN_00459d62 (cXPerson::PostLoad, line 12): calls FUN_004258ed to bind the restored person to its
            // neighborhood record, updating PersonData[NeighborId] (+0x5ca), version-gated persistent fields (+0x58c),
            // and PersonData[FamilyNumber] (+0x606).
            if ((savedNeighbors is not null || typeRegistry is not null) && !LotObjects.IsPlaceholder(unchecked((uint)(person.TypeRecord?.Key ?? 0))))
                world.BindPersonToNeighborRecord(person);
            person.RestoreActionState(state, world);
        }

        // FUN_0045d099 restores the +0xb00 list after every lot object has been registered. FUN_0043fd68 reads
        // each saved +0xf0 key and resolves it through the lot manager's saved-id table; a removed target becomes null.
        foreach (var (person, state) in pendingPersonStates)
        foreach (var savedReference in state.Objects)
        {
            var target = savedReference.Object == 0 ? null : world.ObjectById(savedReference.Object);
            person.SavedPersonObjectReferences.Add(new PersonObjectReference(
                savedReference.Object, target, savedReference.Value, savedReference.Flag));
            if (target is not null) person.InteractingObjects.Add(target);
        }

        // FUN_0043a5fb restores OBJT TypeIndex into TypeRecord +0xa4, and FUN_0043de5b uses
        // that same value as the index in the code-owner table. Restore frames only after every
        // type scope and object has been registered, so cross-object code owners resolve too.
        foreach (var owner in world.WalkOrder.Where(item => item.SavedStack.Count != 0))
            world.SavedExecutionStacks.Add(owner.Id, TreeRunner.RestoreSavedStack(world, owner,
                codeOwner => world.CodeOwnerScopes.GetValueOrDefault(codeOwner)));

        return world;
    }

    /// <summary>Builds the person's source-ordered +0x148 type-record slots and current +0x5ca one-based index.
    /// FUN_00424850 adds one record for each registered type in its five supported OBJD categories, and
    /// FUN_004258ed stores the slot matching the person's current +0x11c type-record key.</summary>
    private static void InitializeGenericSimsCallPersonTypeRecords(XObject instance, ObjectRegistry? typeRegistry, byte language)
    {
        if (instance is not XPerson person) return;
        person.GenericSimsCallPersonTypeRecords.Clear();
        if (typeRegistry is not null)
        {
            foreach (var entry in typeRegistry.Entries)
            {
                var objectType = unchecked((short)entry.Definition[ObjdField.Type]);
                if (!GenericSimsCallPersonRecordTypes.Contains(objectType)) continue;
                person.GenericSimsCallPersonTypeRecords.Add(new GenericSimsCallPersonTypeRecord(
                    entry.Index,
                    checked((short)(person.GenericSimsCallPersonTypeRecords.Count + 1)),
                    unchecked((int)entry.Guid),
                    objectType,
                    entry.CatalogString(0, language),
                    entry.Chunk.Label));
            }
        }

        person.GenericSimsCallPersonTypeRecordIndex = person.GenericSimsCallPersonTypeRecords
            .FirstOrDefault(record => record.TypeRecordKey == instance.TypeRecord?.Key) is { Index: > 0 } current
            ? current.Index
            : (short)0;
    }

    private static IffFile? OpenAdjacentNeighborhoodFile(IffFile lot)
    {
        if (string.IsNullOrWhiteSpace(lot.Source)) return null;
        var houses = Path.GetDirectoryName(Path.GetFullPath(lot.Source));
        var neighborhood = houses is null ? null : Directory.GetParent(houses)?.FullName;
        var path = neighborhood is null ? null : Path.Combine(neighborhood, "Neighborhood.iff");
        // Imported Complete Collection profiles keep the original neighborhood files one level below the staged
        // Houses/ and Characters/ directories. Sims.exe FUN_00430b85 restores the selected neighborhood's ordered
        // NBRS slots; use that preserved root when the staged root has no Neighborhood.iff.
        if (path is not null && !File.Exists(path)) path = Path.Combine(neighborhood!, "UserData", "Neighborhood.iff");
        if ((path is null || !File.Exists(path)) && houses is not null) path = Path.Combine(houses, "Neighborhood.iff");
        return path is not null && File.Exists(path) ? IffFile.Open(path) : null;
    }

    /// <summary>Sims.exe FUN_00424850 (line 127) loads TATT #1 from Neighborhood.iff into the object-type manager
    /// (+0x1250); merge any lot-level TATT #1 on top so shared per-GUID type attributes are populated.</summary>
    private static IReadOnlyDictionary<uint, short[]>? ReadMergedTypeAttributes(IffFile lot, IffFile? neighborhood = null)
    {
        Dictionary<uint, short[]>? merged = null;
        var neighborhoodFile = neighborhood ?? OpenAdjacentNeighborhoodFile(lot);
        if ((neighborhoodFile?.Find(TypeAttributesType, 1) ?? neighborhoodFile?.OfType(TypeAttributesType).FirstOrDefault()) is { } neighborhoodChunk)
        {
            merged = new Dictionary<uint, short[]>();
            foreach (var (guid, values) in TypeAttributes.Read(neighborhoodFile!, neighborhoodChunk).AttributesByGuid)
                merged[guid] = values.ToArray();
        }

        if ((lot.Find(TypeAttributesType, 1) ?? lot.OfType(TypeAttributesType).FirstOrDefault()) is { } lotChunk)
        {
            merged ??= new Dictionary<uint, short[]>();
            foreach (var (guid, values) in TypeAttributes.Read(lot, lotChunk).AttributesByGuid)
                merged[guid] = values.ToArray();
        }

        return merged;
    }

    /// <summary>Neighborhood.iff is stored beside Houses/. FUN_00430b85 restores NBRS slots in order, and
    /// FUN_00423e3b links each record to the current type record by GUID. The type-record eligibility bits are set
    /// during registry construction: FUN_0043c6ee marks the People scan and FUN_0043bb37 stores its category in
    /// +0xb4 bits 2–3. Keep empty NBRS slots so FUN_00427162 sees the same ordered vector length and holes.</summary>
    private static Neighbors? ReadAdjacentNeighbors(IffFile lot, IffFile? neighborhood = null)
    {
        var file = neighborhood ?? OpenAdjacentNeighborhoodFile(lot);
        if (file is null) return null;
        var chunk = file.OfType(ResourceType.Parse("NBRS")).FirstOrDefault();
        if (chunk is null) return null;
        var neighbors = Neighbors.ReadRecoveringSupportedPrefix(file, chunk);
        if (!neighbors.IsComplete)
        {
            // Sims.exe's Neighbor::DoStream (Mac analysis/mac-reference/asm/code/Neighbor.s, 0x1009B220) accepts
            // version-73 formats 4 and 10. Retain complete earlier slots so their characters and relationships
            // remain available, but the incomplete object refuses to serialize over the original NBRS chunk.
            Console.Error.WriteLine($"Native load: recovered {neighbors.CompleteSlotPrefix} complete slots from malformed neighbor roster in {file.Source}; remaining slots are unknown and the roster will not be saved.");
        }
        return neighbors;
    }

    /// <summary>FUN_100AC850 derives each neighbor's family number from FAMI membership and copies it into
    /// Neighbor +0xee; FUN_100ABD30 then loads that value into person data 61 for a created cXPerson.</summary>
    private static IReadOnlyDictionary<uint, int>? ReadAdjacentFamilyNumbers(IffFile lot, IffFile? neighborhood = null)
    {
        var file = neighborhood ?? OpenAdjacentNeighborhoodFile(lot);
        if (file is null) return null;

        var result = new Dictionary<uint, int>();
        foreach (var chunk in file.OfType(ResourceType.Parse("FAMI")))
        {
            if (chunk.Id == 0) continue;
            var family = Family.Read(file, chunk);
            // Family::LoadFamily stores the FAMI chunk id in runtime +0x10c; Neighborhood::UpdateFamilyNumbers
            // copies that runtime value to each listed Neighbor, independently of the serialized FamilyNumber field.
            foreach (var member in family.Members) result[member] = chunk.Id;
        }
        return result;
    }

    private static void RestoreNeighbors(SimulationWorld world, Neighbors savedNeighbors, ObjectRegistry? registry,
        IReadOnlyDictionary<uint, int>? familyNumbersByGuid)
    {
        world.NeighborRosterComplete = savedNeighbors.IsComplete;
        world.CompleteNeighborSlotPrefix = savedNeighbors.CompleteSlotPrefix;
        var bySlot = savedNeighbors.Entries.ToDictionary(neighbor => neighbor.Slot);
        for (var slot = 0; slot < savedNeighbors.SlotCount; slot++)
        {
            if (!bySlot.TryGetValue(slot, out var saved))
            {
                world.NeighborWalkOrder.Add(null);
                continue;
            }

            var entry = registry?.Find(saved.Guid);
            // FUN_00424850 (lines 112-125): right after reading NBRS, Sims.exe removes any neighbor record whose
            // GUID does not resolve to a loaded object definition (`*(int *)(iVar6 + 8) == 0` -> `FUN_004259ec`),
            // clearing its slot in the +0x148 neighbor vector (`piVar4[iVar3 - 1] = 0`) and removing relationships to it.
            if (registry is not null && entry is null)
            {
                world.NeighborWalkOrder.Add(null);
                continue;
            }
            var definition = entry?.Definition.Fields.Select(value => unchecked((short)value)).ToArray() ?? [];
            var neighbor = new NeighborState(saved.NeighborId, definition)
            {
                Name = saved.Name,
                TypeRecordKey = unchecked((int)saved.Guid),
                PersonRecordVersion = saved.PersonData is null ? 0 : saved.RecordVersion,
                FriendEligibilityObjectFlagsB4 = entry is null ? null : entry.Scan == ObjectScan.People ? 4u : 0u,
                RelationshipsChangedFlag24 = true,
            };
            if (saved.PersonData is { } personData)
                personData.Take(neighbor.PersonData.Length).ToArray().CopyTo(neighbor.PersonData, 0);
            // Neighborhood::UpdateFamilyNumbers resets every neighbor's family field, then rebuilds it from FAMI
            // membership. Do this after loading NBRS persistent data so a stale saved PersonData[FamilyNumber] cannot
            // override the authoritative household roster.
            if (familyNumbersByGuid is not null)
                neighbor.PersonData[(int)PersonData.FamilyNumber] = familyNumbersByGuid.TryGetValue(saved.Guid, out var familyNumber)
                    ? unchecked((short)familyNumber)
                    : (short)0;
            foreach (var (otherId, values) in saved.Relationships)
            {
                var targetId = checked((short)otherId);
                neighbor.Relationships.Resize(targetId, values.Count);
                for (var index = 0; index < values.Count; index++)
                    neighbor.Relationships.Set(targetId, index, values[index]);
            }
            world.Neighbors[saved.NeighborId] = neighbor;
            world.NeighborWalkOrder.Add(neighbor);
        }

        if (registry is not null && world.NeighborRosterComplete)
            RestoreCatalogNeighbors(world, registry, familyNumbersByGuid);
    }

    /// <summary>FUN_00424850 (lines 209-291): after loading NBRS, Sims.exe scans all registered object definitions
    /// whose OBJD Type is 2, 0x200, 0x400, 0x800, or 0x1000 and calls FUN_004257ef(selector, -1) whenever
    /// no neighbor record exists for that selector yet, registering expansion/service NPCs into the neighbor vector.</summary>
    private static void RestoreCatalogNeighbors(SimulationWorld world, ObjectRegistry registry,
        IReadOnlyDictionary<uint, int>? familyNumbersByGuid)
    {
        foreach (var entry in registry.Entries)
        {
            var objectType = unchecked((short)entry.Definition[ObjdField.Type]);
            if (!GenericSimsCallPersonRecordTypes.Contains(objectType)) continue;
            if (entry.Definition[ObjdField.MasterId] != 0 && entry.Definition[ObjdField.SubIndex] != ushort.MaxValue) continue;
            if (entry.Definition[ObjdField.Disabled] != 0) continue;

            var key = unchecked((int)entry.Guid);
            if (world.NeighborWalkOrder.Any(existing => existing is not null && existing.TypeRecordKey == key)) continue;

            var slot = -1;
            for (var i = 0; i < world.NeighborWalkOrder.Count; i++)
            {
                if (world.NeighborWalkOrder[i] is null && !world.Neighbors.ContainsKey(checked((short)(i + 1))))
                    slot = i;
            }
            if (slot < 0)
            {
                slot = world.NeighborWalkOrder.Count;
                world.NeighborWalkOrder.Add(null);
            }
            while (world.Neighbors.ContainsKey(checked((short)(slot + 1))))
            {
                slot = world.NeighborWalkOrder.Count;
                world.NeighborWalkOrder.Add(null);
            }

            var neighborId = checked((short)(slot + 1));
            var definition = entry.Definition.Fields.Select(value => unchecked((short)value)).ToArray();
            var neighbor = new NeighborState(neighborId, definition)
            {
                TypeRecordKey = key,
                FriendEligibilityObjectFlagsB4 = entry.Scan == ObjectScan.People ? 4u : 0u,
                RelationshipsChangedFlag24 = true,
            };
            if (familyNumbersByGuid is not null && familyNumbersByGuid.TryGetValue(entry.Guid, out var familyNumber))
                neighbor.PersonData[(int)PersonData.FamilyNumber] = unchecked((short)familyNumber);
            world.Neighbors[neighborId] = neighbor;
            world.NeighborWalkOrder[slot] = neighbor;
        }
    }

    /// <summary>Ports the manager-flag tail of Sims.exe FUN_0043fe4a after the object's virtual state restore
    /// (0x440140–0x440251). From version 0x36 an ordinary object reads its saved flag dword into the manager's
    /// parallel +0x24 vector; a placeholder definition, which stores no dword, sets the eligibility bit instead.
    /// Versions 0x36–0x38 then keep only the low word and enable every object; 0x39 and later keep the saved word.</summary>
    private static void ApplyLoadedClockEligibility(
        SimulationWorld world, XObject instance, uint effectiveGuid, int version, int? savedFlags)
    {
        if (version <= 0x35) return;

        var entry = world.ClockManager.Entries[instance.ClockManagerIndex - 1];
        var flags = LotObjects.IsPlaceholder(effectiveGuid)
            ? entry.Flags | 0x00010000u
            : unchecked((uint)(savedFlags ?? unchecked((int)entry.Flags)));

        if (version < 0x39)
        {
            flags &= 0x0000ffff;
            flags |= 0x00010000;
        }

        entry.Flags = flags;
    }

    private static ResourceFormatException MissingChunk(IffFile lot, ResourceType type) =>
        new(lot.Source, type.ToString(), 0, "required saved-lot chunk is missing");

    /// <summary>Object construction calls FUN_0046e3ed on the definition's SLOT resource. It dispatches type 0 to
    /// FUN_0046dfd7/FUN_00435b8c (cXObject +0x130, 0x24-byte footprint entries), type 1 to
    /// FUN_0046e09f/FUN_0045f795 (+0x148, 0x3c-byte ticking/render records), and type 3 to its routing list. Retain
    /// every parsed source entry, type-0 footprint entries, and the already-modeled type-1 records without conflating
    /// the vectors.</summary>
    // The +0x130 relation vector holds the entry-0 sentinel plus one record per type-0 definition (FUN_00431b5e).
    // The cXPerson constructor (FUN_00459797) then appends five more records, and FUN_00434578 only overwrites the
    // leading records a saved lot lists, so a person keeps all of them however few were saved.
    private static void PadSlotVector(XObject instance)
    {
        var length = 1 + instance.FootprintSlots.Count;
        while (instance.Slots.Count < length) instance.Slots.Add(0);
    }

    /// <summary>The cXPerson constructor (FUN_00459797) appends five 0x24-byte records to the +0x130 vector through
    /// FUN_0046dfaf defaults: the first has height code 6 (the hand slot Grab and Create Object request as slot 0),
    /// the second keeps code 0, and the last three have code 1. It also appends one default 0x38-byte SpriteSlot
    /// record to +0x140 (00459797.c:35-37, 0046e076.c) with Z = 2.0f (+0x08) and -1 in +0x0c/+0x14.</summary>
    private static void RestoreSlotDefinitions(XObject instance, ObjectEntry entry)
    {
        RestoreObjectSlotDefinitions(instance, entry);
        if (instance is not XPerson) return;
        foreach (var heightCode in new[] { 6, 0, 1, 1, 1 })
            instance.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = heightCode });
        if (instance.RuntimeSlots.Count == 0)
        {
            var slot = new ObjectRuntimeSlot();
            slot.WriteWord(0x08, BitConverter.SingleToInt32Bits(2.0f));
            slot.WriteWord(0x0c, -1);
            slot.WriteWord(0x14, -1);
            instance.RuntimeSlots.Add(slot);
        }
    }

    private static void RestoreObjectSlotDefinitions(XObject instance, ObjectEntry entry)
    {
        var slotsId = entry.Definition[ObjdField.SlotsId];
        if (slotsId == 0) return;
        var chunk = entry.File.OfType(SlotsType).FirstOrDefault(candidate => candidate.Id == slotsId);
        if (chunk is null) return;
        var slots = SlotTable.Read(entry.File, chunk).Slots;
        for (var slotIndex = 0; slotIndex < slots.Count; slotIndex++)
        {
            var slot = slots[slotIndex];
            instance.DefinitionSlots.Add(slot);
            switch (slot.Type)
            {
                case 0:
                    instance.FootprintSlots.Add(ObjectFootprintSlot.FromDefinition(slot));
                    break;
                case 1:
                    instance.RuntimeSlots.Add(ObjectRuntimeSlot.FromObjectSlot(slot, slotIndex));
                    break;
                case 3:
                    instance.RoutingSlots.Add(RoutingSlotRequest.FromDefinition(slot));
                    break;
            }
        }
    }

    /// <summary>FUN_0043fe4a substitutes the general placeholder for missing non-person definitions and the
    /// template-person definition for missing type-2 definitions. Known dog/cat template GUIDs retain their own
    /// class-specific placeholders. This chooses from the saved OBJT type, before decoding the object's state.</summary>
    private static uint? PlaceholderGuid(ObjectTypeEntry type) => type.ObjectType switch
    {
        2 when type.Guid == 0x4A70DF92 || unchecked((uint)type.Field0x8C) == 0x4A70DF92 => 0x4A70DF92,
        2 when type.Guid == 0x7BEA0977 || unchecked((uint)type.Field0x8C) == 0x7BEA0977 => 0x7BEA0977,
        2 => 0x7FD96B54,
        _ => 0x3C08965F,
    };
}
