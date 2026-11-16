using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class SavedLotObjectLoaderTests
{
    [Fact]
    public void FailedPlacementTypeRegistrationRollsBackOnlyItsOwnUnreferencedIndexes()
    {
        var world = new SimulationWorld();
        var cachedScope = new EmptyTreeTable();
        world.SavedObjectClasses = new Dictionary<short, (ObjectClass, bool)> { [1] = (ObjectClass.Object, true) };
        world.CodeOwnerTypeGuids[1] = 0x11111111;
        world.CodeOwnerScopes[1] = cachedScope;

        using (world.BeginSavedObjectTypeRegistrationScope())
        {
            var index = world.RegisterSavedObjectType(new ObjectTypeEntry(0x22222222, 0, 0, 0, 0, "new", 0),
                ObjectClass.Object, null);
            Assert.Equal((short)1, index);
            Assert.Equal(0x22222222u, world.CodeOwnerTypeGuids[index]);
        }

        Assert.Empty(world.SavedObjectTypes);
        Assert.Equal((ObjectClass.Object, true), world.SavedObjectClasses[1]);
        Assert.Equal(0x11111111u, world.CodeOwnerTypeGuids[1]);
        Assert.Same(cachedScope, world.CodeOwnerScopes[1]);

        using (var committed = world.BeginSavedObjectTypeRegistrationScope())
        {
            world.RegisterSavedObjectType(new ObjectTypeEntry(0x33333333, 0, 0, 0, 0, "placed", 0),
                ObjectClass.Object, null);
            world.Add(new XObject(1, new short[XObject.DefinitionCount]) { TypeRecord = new TypeRecord(0x33333333) });
        }
        Assert.Contains(world.SavedObjectTypes.Values, entry => entry.Guid == 0x33333333);
    }

    [Fact]
    public void RestoreCarriesTheSavedObjectManagerRandomStateIntoTheWorld()
    {
        var saved = new ChunkBuilder().U8(0).U8(0).U8(0).U8(0).S32(0x0e).Tag("IMIS");
        for (var index = 0; index < 0x10; index++) saved.S16((short)index);
        saved.U32(9).S32(10).S16(11).S32(12).S32(0x12345678).S32(13);
        byte[] resourceMap = [0xaa, 0xbb, 0xcc];
        var bytes = ChunkBuilder.File(("SIMI", 1, saved.ToArray()), ("rsmp", 0, resourceMap));
        var resourceMapOffset = IffFile.HeaderSize + IffFile.ChunkHeaderSize + saved.Length;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(60), (uint)resourceMapOffset);
        var lot = new IffFile(bytes, "saved-simi-source.iff");
        var simulationState = SimulationState.Read(lot, lot.Chunks[0]);
        var world = SavedLotObjectLoader.Restore(
            Array.Empty<LotObject>(),
            Array.Empty<ObjectTypeEntry>(),
            _ => null,
            _ => new EmptyTreeTable(),
            savedSimulationState: simulationState);

        Assert.Equal(0x12345678u, world.ObjectManagerRandomState);
        Assert.Same(simulationState, world.SavedSimulationState);
        Assert.Equal(Enumerable.Range(0, 0x10).Select(index => (short)index), world.Globals.Take(0x10));
        Assert.Equal(new SimulationDate(8, 7, 1), world.CurrentDate);
        world[SimulationGlobal.Month] = 9;
        Assert.Equal(new SimulationDate(8, 9, 1), world.CurrentDate);

        world.ObjectManagerRandomState = 0x87654321;
        world.Globals[3] = 999;
        var updated = world.WriteSavedSimulationStateTo(lot);
        var updatedSimi = updated.Find(ResourceType.Parse("SIMI"), 1)!;
        var reread = SimulationState.Read(updated, updatedSimi);
        Assert.Equal(unchecked((int)0x87654321u), reread.Field0xAC);
        Assert.Equal((short)999, reread.Globals[3]);
        Assert.Equal((uint)resourceMapOffset, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(updated.Bytes.Span[60..]));
        Assert.Equal(resourceMap, updated.Find(ResourceType.Rsmp, 0)!.Data.ToArray());
    }

    [Fact]
    public void RestoreDoesNotInventASeedForPreVersion14SimulationState()
    {
        var saved = new ChunkBuilder().U8(0).U8(0).U8(0).U8(0).S32(0x0d).Tag("IMIS");
        for (var index = 0; index < 0x10; index++) saved.S16((short)index);
        saved.U32(9).S32(10).S16(11).S32(12);
        var (file, chunk) = saved.InFile("SIMI");
        var world = SavedLotObjectLoader.Restore(
            Array.Empty<LotObject>(), Array.Empty<ObjectTypeEntry>(), _ => null, _ => new EmptyTreeTable(),
            savedSimulationState: SimulationState.Read(file, chunk));

        Assert.Equal(1u, world.ObjectManagerRandomState);
    }

    [Fact]
    public void LoadedEligibilityGridFeedsTheNativePlacementGateInSourceXYOrder()
    {
        var eligibility = Enumerable.Repeat((byte)0xff, 64 * 64).ToArray();
        eligibility[4 * 64 + 6] = 0;
        var chunk = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(1).U32(0).Raw(eligibility).ToArray();
        var lot = new IffFile(ChunkBuilder.File(("Arry", 10, chunk)), "placement-eligibility.iff");
        var world = new SimulationWorld();
        world.PlacementEligibilityGrids[1] = SavedLotObjectLoader.ReadPlacementEligibilityGrid(lot, 0x35);
        var item = world.Add(new XObject(1, new short[XObject.DefinitionCount]));
        item[ObjectData.AllowedHeightFlags] = 1;
        item[ObjectData.PlacementFlags] = 0x20;

        Assert.True(world.CanPlaceObject(item, new ObjectPosition(4 * 16 + 8, 6 * 16 + 8, 1)));
        Assert.False(world.CanPlaceObject(item, new ObjectPosition(6 * 16 + 8, 4 * 16 + 8, 1)));
        item[ObjectData.PlacementFlags] = 0;
        Assert.True(world.CanPlaceObject(item, new ObjectPosition(6 * 16 + 8, 4 * 16 + 8, 1)));
    }

    [Fact]
    public void EligibilityGridUsesSourceSentinelForOldOrMissingDataAndRejectsMalformedPresentData()
    {
        // In save versions prior to SIMI 0x35, the placement eligibility grid defaults to 0xFF, whereas
        // later versions import chunk Arry 10 where non-0xFF bytes indicate eligible cells.
        var emptyLot = new IffFile(ChunkBuilder.File([]), "empty-placement-grid.iff");
        Assert.All(SavedLotObjectLoader.ReadPlacementEligibilityGrid(emptyLot, 0x34),
            value => Assert.Equal((byte)0xff, value));
        Assert.All(SavedLotObjectLoader.ReadPlacementEligibilityGrid(emptyLot, 0x35),
            value => Assert.Equal((byte)0xff, value));

        var malformedChunk = new ChunkBuilder().S16(0).S16(0).S32(63).S32(64).S32(1).U32(0)
            .Raw(new byte[63 * 64]).ToArray();
        var malformedLot = new IffFile(ChunkBuilder.File(("Arry", 10, malformedChunk)), "bad-placement-grid.iff");
        Assert.Throws<InvalidDataException>(() => SavedLotObjectLoader.ReadPlacementEligibilityGrid(malformedLot, 0x35));
    }

    [Fact]
    public void RestoredRoomPlacementAndLoadedEligibilityGridBothReachOrdinaryPlacement()
    {
        const uint guid = 0x12345678;
        var definitionFields = new ushort[XObject.DefinitionCount];
        definitionFields[(int)ObjdField.Guid1] = unchecked((ushort)guid);
        definitionFields[(int)ObjdField.Guid2] = (ushort)(guid >> 16);
        var definitionChunk = new ChunkBuilder();
        foreach (var field in definitionFields) definitionChunk.U16(field);
        var definitionFile = new IffFile(ChunkBuilder.File(("OBJD", 128, definitionChunk.ToArray())), "loaded-placement-definition.iff");
        var definitionResource = definitionFile.Find(ResourceType.Objd, 128)!;
        var definition = new ObjectEntry(0, ObjectScan.Objects, definitionFile.Source, definitionFile,
            definitionResource, Objd.Read(definitionFile, definitionResource), null);
        var type = new ObjectTypeEntry(guid, 0, 0, 1, 0, "Object", 0);
        var objectData = new short[XObject.DataCount];
        objectData[(int)ObjectData.RoomPlacement] = 1;
        objectData[(int)ObjectData.AllowedHeightFlags] = 1;
        var saved = State() with { Data = objectData, Values0x108 = [0, 0, 0, 0, 0, 0], Value0x110 = 1 };
        var world = SavedLotObjectLoader.Restore([new LotObject(1, 1, [], 0, saved)], [type],
            requested => requested == guid ? definition : null, _ => new EmptyTreeTable());
        var restored = world.ObjectById(1)!;
        var loadedRoomPlacement = restored[ObjectData.RoomPlacement];
        Assert.Equal((short)1, loadedRoomPlacement);
        var item = restored;
        item[ObjectData.PlacementFlags] = 0x20;
        world.BuildModeType6PlacementGrids[1] = new byte[64 * 64];
        world.BuildModeType6PlacementGrids[1][5 * 64 + 6] = 0x04;
        world.BuildModeType6PlacementGrids[1][4 * 64 + 6] = 0x04;
        world.BuildModeType6PlacementGrids[1][8 * 64 + 6] = 0x04;

        var eligibility = Enumerable.Repeat((byte)0xff, 64 * 64).ToArray();
        eligibility[5 * 64 + 6] = 0;
        eligibility[4 * 64 + 6] = 0;
        eligibility[7 * 64 + 6] = 0;
        var eligibilityChunk = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(1).U32(0).Raw(eligibility).ToArray();
        var lot = new IffFile(ChunkBuilder.File(("Arry", 10, eligibilityChunk)), "loaded-placement-eligibility.iff");
        // Chunk Arry 10 stores placement eligibility, where any cell with a value other than 0xFF is
        // considered eligible.
        world.PlacementEligibilityGrids[1] = SavedLotObjectLoader.ReadPlacementEligibilityGrid(lot, 0x35);

        Assert.True(world.HasPlacementEligibilityAt(5, 6, 1));
        Assert.Equal((byte)0x04, world.BuildModeType6PlacementFlagsAt(5, 6, 1));
        Assert.True(world.CanPlaceObject(item, new ObjectPosition(5 * 16 + 8, 6 * 16 + 8, 1)));
        Assert.False(world.CanPlaceObject(item, new ObjectPosition(7 * 16 + 8, 6 * 16 + 8, 1)));
        Assert.False(world.CanPlaceObject(item, new ObjectPosition(8 * 16 + 8, 6 * 16 + 8, 1)));

        item[ObjectData.RoomPlacement] = 2;
        Assert.True(world.CanPlaceObject(item, new ObjectPosition(7 * 16 + 8, 6 * 16 + 8, 1)));
        Assert.False(world.CanPlaceObject(item, new ObjectPosition(4 * 16 + 8, 6 * 16 + 8, 1)));
    }

    private sealed class EmptyTreeTable : ITreeTable
    {
        public Bhav? Find(short id) => null;
    }

    private sealed class SingleTreeTable(short id, Bhav tree) : ITreeTable
    {
        public Bhav Tree { get; } = tree;
        public Bhav? Find(short requested) => requested == id ? Tree : null;
    }

    [Fact]
    public void RestoreKeepsObjfActionAndCheckTreesOnTheNativeTypeRecord()
    {
        const uint guid = 0x12345678;
        var fields = new ushort[XObject.DefinitionCount];
        fields[(int)ObjdField.Guid1] = unchecked((ushort)guid);
        fields[(int)ObjdField.Guid2] = (ushort)(guid >> 16);
        fields[(int)ObjdField.UsesFnTable] = 1;
        fields[(int)ObjdField.CatalogStringsId] = 7;
        var definitionBuilder = new ChunkBuilder();
        foreach (var field in fields) definitionBuilder.U16(field);
        var objf = new ChunkBuilder().U32(0).U32(1).Tag("fJBO").U32(2)
            .U16(10).U16(20).U16(30).U16(40).ToArray();
        var ctss = new ChunkBuilder().S16(-1).S16(2).CString("Catalog chair").CString("A sturdy seat").ToArray();
        var file = new IffFile(ChunkBuilder.File(("OBJD", 128, definitionBuilder.ToArray()), ("OBJf", 128, objf), ("CTSS", 7, ctss)), "native-functions.iff");
        var objd = file.Find(ResourceType.Objd, 128)!;
        var entry = new ObjectEntry(0, ObjectScan.Objects, file.Source, file, objd, Objd.Read(file, objd), null);
        var type = new ObjectTypeEntry(guid, 0, 0, 1, 0, "Object", 0);

        var world = SavedLotObjectLoader.Restore([new LotObject(1, 1, [], 0, State())], [type],
            requested => requested == guid ? entry : null, _ => new EmptyTreeTable());

        var record = world.ObjectById(1)!.TypeRecord!;
        Assert.Equal(new ushort[] { 20, 40 }, record.FunctionTable);
        Assert.Equal(new ushort[] { 10, 30 }, record.FunctionCheckTable);
        Assert.Equal("Catalog chair", record.CatalogName);
        Assert.Equal("A sturdy seat", record.CatalogDescription);
    }

    [Fact]
    public void RestoreBuildsTypedObjectsInLotOrderAndCopiesSourceBackedState()
    {
        const uint personGuid = 0x12345678;
        const uint objectGuid = 0x23456789;
        const uint portalGuid = 0x3456789a;
        var scope = new EmptyTreeTable();
        var entries = new Dictionary<uint, ObjectEntry>
        {
            [personGuid] = Definition(personGuid, 2, objectVersion: 0x1234, footprintMask: 0x0050),
            [objectGuid] = Definition(objectGuid, 0),
            [portalGuid] = Definition(portalGuid, 8, masterId: 1),
        };
        var personData = Enumerable.Range(0, 0x50).Select(index => (short)(index - 10)).ToArray();
        var motives = Enumerable.Range(0, 16).Select(index => index + 0.5f).ToArray();
        var auxiliary = Enumerable.Range(0, 16).Select(index => index + 100.5f).ToArray();
        var relationship = new ObjectRelationship(22, [4, 8]);
        var emptyAction = new PersonAction(1, 2, 20, 22, 23, 3, [4, 5, 6, 7], 8, 9, 10.5f, 11);
        var savedPersonState = PersonState(personData, motives, auxiliary) with
        {
            Character = new PersonCharacter(12, 13, [("head", "body")], [("extra", "part")], ["name", "file", "skeleton"], 2, [1, 2, 3, 4, 5, 6, 7, 8, 9]),
            Count0xAF0 = 3,
            First = emptyAction,
            Actions = [emptyAction],
            Objects = [new PersonObjectEntry(22, 101, true), new PersonObjectEntry(999, 202, false), new PersonObjectEntry(0, 303, true)],
            Values = [new PersonValueEntry(404, 1.25f, 2.5f)],
        };
        var saved = new LotObject[]
        {
            new(20, 1, [], 0, State(person: savedPersonState, relationships: [relationship])),
            new(22, 2, [], 0, State()),
            new(23, 3, [], 0, State(multiTile: new MultiTileState(1, 2, 3, 4, 5, 6, 20, [1.25f, 2.5f]))),
        };
        var types = new[]
        {
            new ObjectTypeEntry(personGuid, 0, 0, 1, 2, "Person", 0),
            new ObjectTypeEntry(objectGuid, 0, 0, 2, 0, "Object", 0),
            new ObjectTypeEntry(portalGuid, 0, 0, 3, 8, "Portal", 0),
        };
        var typeAttributes = new Dictionary<uint, short[]> { [objectGuid] = [9, 10] };

        var world = SavedLotObjectLoader.Restore(saved, types, guid => entries.GetValueOrDefault(guid), _ => scope, typeAttributes);

        Assert.Equal(SimulationDate.Initial, world.CurrentDate);
        Assert.Equal([20, 22, 23], world.WalkOrder.Select(item => (int)item.Id));
        Assert.All(world.WalkOrder, item =>
        {
            Assert.True(item.HasRequestKey);
            Assert.Equal(item.Id, item.RequestKey);
            Assert.Same(item, world.ObjectById(item.RequestKey));
        });
        var person = Assert.IsType<XPerson>(world.ObjectById(20));
        Assert.Equal(personGuid, (uint)person.TypeRecord!.Key);
        Assert.Equal((short)2, person.TypeRecord.ObjectType);
        Assert.Equal((short)0x1234, person.TypeRecord.ObjectVersion);
        Assert.Equal((ushort)0x0050, person.TypeRecord.FootprintMask); // OBJD field 77 defines the object footprint extent mask.
        Assert.Equal((short)1, person.InteractionScopeKey5CA);
        Assert.Equal((short)1, world.PersonTypeRecordSlots[person.TypeRecord.Key]);
        Assert.Same(scope, person.TypeRecord.BehaviorScope);
        Assert.Equal(Enumerable.Range(0, 0x50).Select(index => (short)(index - 10)), person.PersonData.Take(0x50));
        Assert.Equal(motives, person.Motives);
        Assert.Equal(auxiliary, person.SavedPersonValues7CC);
        Assert.Equal(savedPersonState.Character, person.SavedCharacter);
        Assert.Equal(3, person.SavedPersonActionStorageCount);
        Assert.Equal(emptyAction, person.SavedCurrentAction);
        Assert.Equal([emptyAction], person.SavedActions);
        Assert.Same(person, person.CurrentActionRecord!.Person);
        Assert.Same(world.ObjectById(22), person.CurrentActionRecord.Owner);
        Assert.Same(world.ObjectById(23), person.CurrentActionRecord.Secondary);
        Assert.Equal(emptyAction.Id, Assert.Single(person.WriteActionQueue()).Id);
        Assert.Equal(new[] { new PersonValueEntry(404, 1.25f, 2.5f) }, person.SavedPersonValues);
        Assert.Equal(3, person.SavedPersonObjectReferences.Count);
        Assert.Same(world.ObjectById(22), person.SavedPersonObjectReferences[0].Target);
        Assert.Null(person.SavedPersonObjectReferences[1].Target);
        Assert.Null(person.SavedPersonObjectReferences[2].Target);
        Assert.Equal(101, person.SavedPersonObjectReferences[0].Value);
        Assert.True(person.SavedPersonObjectReferences[0].Flag);
        Assert.Equal([22], person.InteractingObjects.Select(item => item.Id));
        // Object X and Y coordinates are stored in sixteenths of a tile.
        Assert.Equal(new ObjectPosition(256, 512, 2), person.Position);
        Assert.Equal((short)39, person.RoomWord84);
        Assert.Equal(new FootprintBounds(104, 100, 22, 14), person.FootprintBounds);
        Assert.Equal(new short[] { 1, 2, 0, 0, 0, 0, 0, 0 }, person.Attributes); // Object attributes require a minimum capacity of 8 entries.
        Assert.Equal(Enumerable.Range(0, 8).Select(value => (short)(value + 3)), person.Temporaries);
        Assert.Equal(Enumerable.Range(0, XObject.DataCount).Select(value => (short)(value + 10)), person.Data);
        Assert.Equal(person.RoomWord84, person.Data[(int)ObjectData.Room]);
        Assert.Equal((short)40, person[ObjectData.RoomPlacement]);
        Assert.Equal(new short[] { 6, 7 }, person.DynamicSpriteFlags);
        Assert.Equal(new short[] { 8, 9, 0, 0, 0, 0 }, person.Slots);
        Assert.Equal((uint)0x1234, person.Flags);
        Assert.Equal(1234u, (uint)world.ClockManager.FlagsAt(person.ClockManagerIndex));
        Assert.Single(person.SavedStack);
        Assert.Equal(4, person.Relationships.Get(22, 0));
        Assert.Equal(8, person.Relationships.Get(22, 1));
        Assert.Equal(new short[] { 9, 10 }, world.ObjectById(22)!.TypeAttributes);
        Assert.Equal((short)0, world.ObjectById(22)!.TypeRecord!.ObjectType);
        Assert.IsType<XObject>(world.ObjectById(22));
        Assert.Equal((short)20, world.ObjectById(23)!.MultiTileMasterId);
        Assert.Equal(new float[] { 1.25f, 2.5f }, world.ObjectById(23)!.SavedMultiTileState!.PortalValues);
        Assert.Equal((short)8, world.ObjectById(23)!.TypeRecord!.ObjectType);
    }

    [Fact]
    public void RestoreHydratesOrderedNeighborhoodRecordsAndTreeBudgetEligibility()
    {
        const uint personA = 0x12340001;
        const uint personB = 0x12340002;
        const uint objectGuid = 0x56780001;
        var game = Directory.CreateTempSubdirectory("saved-neighbor-registry").FullName;
        try
        {
            var objects = Directory.CreateDirectory(Path.Combine(game, "Objects"));
            var people = Directory.CreateDirectory(Path.Combine(game, "Characters"));
            File.WriteAllBytes(Path.Combine(people.FullName, "a.iff"), DefinitionFile(personA, 2, 0, 0, 0));
            File.WriteAllBytes(Path.Combine(people.FullName, "b.iff"), DefinitionFile(personB, 2, 0, 0, 0));
            File.WriteAllBytes(Path.Combine(objects.FullName, "chair.iff"), DefinitionFile(objectGuid, 0, 0, 0, 0));
            var registry = ObjectRegistry.Scan(game, people.FullName);
            var neighbors = SavedNeighbors((personA, (short)11), (0u, (short)0), (personB, (short)22), (objectGuid, (short)33));

            var world = SavedLotObjectLoader.Restore([], [], _ => null, _ => null,
                typeRegistry: registry, savedNeighbors: neighbors);

            Assert.Equal(4, world.NeighborWalkOrder.Count);
            Assert.Equal((short)11, world.NeighborWalkOrder[0]!.Id);
            Assert.Equal("Neighbor 11", world.NeighborWalkOrder[0]!.Name);
            Assert.Null(world.NeighborWalkOrder[1]);
            Assert.Equal((short)22, world.NeighborWalkOrder[2]!.Id);
            Assert.Equal((short)33, world.NeighborWalkOrder[3]!.Id);
            Assert.Equal(4u, world.NeighborWalkOrder[0]!.FriendEligibilityObjectFlagsB4);
            Assert.Equal(4u, world.NeighborWalkOrder[2]!.FriendEligibilityObjectFlagsB4);
            Assert.Equal(0u, world.NeighborWalkOrder[3]!.FriendEligibilityObjectFlagsB4);
            Assert.Equal(10060, world.TreeStepLimit); // Base limit 10,000 plus 15 times the square of eligible neighbors.
            Assert.Same(world.NeighborWalkOrder[0], world.Neighbors[11]);
        }
        finally { Directory.Delete(game, true); }
    }

    [Theory]
    // In save versions up to 0x35 without stored flag words, manager flags default to 0x0000FFFF.
    [InlineData(0x35, 0x23456789u, 0x00012345, 0x0000ffffu)]
    // Save versions 0x36 through 0x38 retain only the low 16 bits of saved flags and mark all objects
    // enabled.
    [InlineData(0x36, 0x23456789u, 0x00002345, 0x00012345u)]
    [InlineData(0x38, 0x23456789u, 0x00012345, 0x00012345u)]
    // Save versions 0x39 and later preserve the full 32-bit manager flag word, including eligibility bits.
    [InlineData(0x39, 0x23456789u, 0x00010005, 0x00010005u)]
    [InlineData(0x39, 0x23456789u, 0x00000005, 0x00000005u)]
    [InlineData(0x3e, 0x23456789u, 0x00014d96, 0x00014d96u)]
    // Placeholder object definitions omit saved flag words and default to fully eligible manager flags.
    [InlineData(0x39, 0x3c08965fu, 0x00000000, 0x0001ffffu)]
    [InlineData(0x3e, 0x3c08965fu, 0x00000000, 0x0001ffffu)]
    public void RestoreAppliesOriginalVersionedManagerFlags(int version, uint guid, int savedFlags, uint expectedFlags)
    {
        var entry = Definition(guid, 0);
        var saved = new[] { new LotObject(1, 1, [], 0, State() with { ManagerFlags = savedFlags }) };
        var types = new[] { new ObjectTypeEntry(guid, 0, 0, 1, 0, "Object", 0) };

        var world = SavedLotObjectLoader.Restore(saved, types, requested => requested == guid ? entry : null,
            _ => null, objectManagerVersion: version);

        Assert.Equal(expectedFlags, world.ClockManager.Entries[0].Flags);
        Assert.Equal((short)1, world.ObjectById(1)!.ClockManagerIndex);
    }

    [Fact]
    public void RestoreKeepsTheSavedWaitCellAndTickEligibility()
    {
        const uint objectGuid = 0x23456789;
        const int savedFlags = 0x00014d96;
        var entry = Definition(objectGuid, 0);
        var saved = new[] { new LotObject(7, 1, [], 0, State() with { ManagerFlags = savedFlags }) };
        var types = new[] { new ObjectTypeEntry(objectGuid, 0, 0, 1, 0, "Object", 0) };

        var world = SavedLotObjectLoader.Restore(saved, types, guid => guid == objectGuid ? entry : null,
            _ => null, objectManagerVersion: 0x3e);

        var objectIndex = world.ObjectById(7)!.ClockManagerIndex;
        Assert.Equal((uint)savedFlags, world.ClockManager.FlagsAt(objectIndex));
        Assert.Equal((short)0x4d96, world.ClockManager.WaitTicksAt(objectIndex));
        Assert.NotEqual(0u, world.ClockManager.FlagsAt(objectIndex) & 0x00010000u);
    }

    [Fact]
    public void RestoredManagerFlagsDriveTheClockManagerPass()
    {
        const uint objectGuid = 0x23456789;
        const uint otherGuid = 0x3456789a;
        var entry = Definition(objectGuid, 0);
        var otherEntry = Definition(otherGuid, 0);
        var saved = new[]
        {
            new LotObject(1, 1, [], 0, State() with { ManagerFlags = 0x00010002 }),
            new LotObject(2, 2, [], 0, State() with { ManagerFlags = 0x00000000 }),
        };
        var types = new[]
        {
            new ObjectTypeEntry(objectGuid, 0, 0, 1, 0, "Object", 0),
            new ObjectTypeEntry(otherGuid, 0, 0, 2, 0, "Object", 0),
        };
        var definitions = new Dictionary<uint, ObjectEntry> { [objectGuid] = entry, [otherGuid] = otherEntry };

        var world = SavedLotObjectLoader.Restore(saved, types,
            guid => definitions.GetValueOrDefault(guid), _ => null, objectManagerVersion: 0x3e);
        var control = new SimulationClockControl(1, 1, 1);
        var callbacks = 0;
        bool Tick(XObject _, int __) { callbacks++; return true; }

        Assert.Equal(SimulationClockManagerResult.PassComplete, world.ClockManager.Evaluate(control, 0, 0, Tick));
        Assert.Equal(0, callbacks);
        Assert.Equal((short)1, world.ClockManager.WaitTicksAt(world.ObjectById(1)!.ClockManagerIndex));

        Assert.Equal(SimulationClockManagerResult.PassComplete, world.ClockManager.Evaluate(control, 0, 0, Tick));
        Assert.Equal(0, callbacks);
        Assert.Equal((short)0, world.ClockManager.WaitTicksAt(world.ObjectById(1)!.ClockManagerIndex));

        Assert.Equal(SimulationClockManagerResult.PassComplete, world.ClockManager.Evaluate(control, 0, 0, Tick));
        Assert.Equal(1, callbacks);

        Assert.Equal(SimulationClockManagerResult.PassComplete, world.ClockManager.Evaluate(control, 0, 0, Tick));
        Assert.Equal(2, callbacks);
    }

    [Fact]
    public void RestorePadsInstanceAttributesToTheDefinitionsCount()
    {
        const uint objectGuid = 0x23456789;
        var entry = Definition(objectGuid, 0);
        var types = new[] { new ObjectTypeEntry(objectGuid, 0, 0, 1, 0, "Object", 0) };
        var saved = new[] { new LotObject(5, 1, [], 0, State() with { Attributes = [7, 8] }) };

        var withAttributes = DefinitionWithAttributeCount(objectGuid, type: 0, count: 4);
        var world = SavedLotObjectLoader.Restore(saved, types, guid => guid == objectGuid ? withAttributes : null, _ => null);

        Assert.Equal([7, 8, 0, 0, 0, 0, 0, 0], world.ObjectById(5)!.Attributes); // Object attribute arrays are padded to a minimum length of 8.
    }

    [Fact]
    public void RestoreAllocatesTheTypeAttributeTableFromTheDefinition()
    {
        const uint objectGuid = 0x23456789;
        var types = new[] { new ObjectTypeEntry(objectGuid, 0, 0, 1, 0, "Object", 0) };
        var saved = new[] { new LotObject(4, 1, [], 0, State()) };

        // Objects lacking a TATT chunk allocate attributes matching the count specified in their
        // definition.
        var withAttributes = DefinitionWithAttributeCount(objectGuid, type: 0, count: 32);
        var world = SavedLotObjectLoader.Restore(saved, types, guid => guid == objectGuid ? withAttributes : null, _ => null);
        Assert.Equal(new short[32], world.ObjectById(4)!.TypeAttributes);
    }

    [Fact]
    public void RestorePadsDynamicSpriteFlagsToTheDefinitionsCount()
    {
        const uint objectGuid = 0x23456789;
        var entry = Definition(objectGuid, 0, numDynamicSprites: 6);
        var saved = new[] { new LotObject(3, 1, [], 0, State() with { Values0x34 = [4, 5] }) };
        var types = new[] { new ObjectTypeEntry(objectGuid, 0, 0, 1, 0, "Object", 0) };

        var world = SavedLotObjectLoader.Restore(saved, types, guid => guid == objectGuid ? entry : null, _ => null);

        Assert.Equal([4, 5, 0, 0, 0, 0], world.ObjectById(3)!.DynamicSpriteFlags);
    }

    [Fact]
    public void RestoreKeepsTheOriginalLoadFlagWhenSavedVersionHasNoFlagWord()
    {
        const uint objectGuid = 0x23456789;
        var entry = Definition(objectGuid, 0);
        var saved = new[] { new LotObject(9, 1, [], 0, State() with { Value0x114 = null }) };
        var types = new[] { new ObjectTypeEntry(objectGuid, 0, 0, 1, 0, "Object", 0) };

        var world = SavedLotObjectLoader.Restore(saved, types, guid => guid == objectGuid ? entry : null, _ => null);

        Assert.Equal(0x80u, world.ObjectById(9)!.Flags);
    }

    [Fact]
    public void RestoreRebuildsTheMultitileNextChainInRegistrationOrder()
    {
        const uint masterGuid = 0x11111111;
        const uint firstGuid = 0x22222222;
        const uint secondGuid = 0x33333333;
        var master = Definition(masterGuid, 0, masterId: 1);
        var first = Definition(firstGuid, 0, masterId: 1);
        var second = Definition(secondGuid, 0, masterId: 1);
        var saved = new[]
        {
            new LotObject(10, 1, [], 0, State(multiTile: new MultiTileState(1, 2, 3, 4, 5, 6, 0, [])) with { Stack = [] }),
            new LotObject(11, 2, [], 0, State(multiTile: new MultiTileState(1, 2, 3, 4, 5, 6, 10, [])) with { Stack = [] }),
            new LotObject(12, 3, [], 0, State(multiTile: new MultiTileState(1, 2, 3, 4, 5, 6, 10, [])) with { Stack = [] }),
        };
        var types = new[]
        {
            new ObjectTypeEntry(masterGuid, 0, 0, 1, 0, "Master", 0),
            new ObjectTypeEntry(firstGuid, 0, 0, 2, 1, "Part", 0),
            new ObjectTypeEntry(secondGuid, 0, 0, 3, 2, "Part", 0),
        };
        var definitions = new Dictionary<uint, ObjectEntry> { [masterGuid] = master, [firstGuid] = first, [secondGuid] = second };

        var world = SavedLotObjectLoader.Restore(saved, types, guid => definitions.GetValueOrDefault(guid), _ => null);

        Assert.Equal((short)11, world.ObjectById(10)!.MultiTileNextId);
        Assert.Equal((short)12, world.ObjectById(11)!.MultiTileNextId);
        Assert.Equal((short)0, world.ObjectById(12)!.MultiTileNextId);
    }

    [Fact]
    public void RestoreInitializesAutonomyEligibilityOnTheMultitileRoot()
    {
        const uint rootGuid = 0x12345678;
        const uint partGuid = 0x23456789;
        var root = DefinitionWithInteraction(rootGuid, 0, 1, 7, threshold: 99, flags: 0, interactionGroup: -1);
        var part = DefinitionWithInteraction(partGuid, 0, 1, 8, threshold: 1, flags: 0, interactionGroup: 1);
        var saved = new[]
        {
            new LotObject(1, 1, [], 0, State(multiTile: new MultiTileState(1, 2, 3, 4, 5, 6, 0, [])) with { Stack = [] }),
            new LotObject(2, 2, [], 0, State(multiTile: new MultiTileState(1, 2, 3, 4, 5, 6, 1, [])) with { Stack = [] }),
        };
        var types = new[]
        {
            new ObjectTypeEntry(rootGuid, 0, 0, 1, 0, "Root", 0),
            new ObjectTypeEntry(partGuid, 0, 0, 2, 0, "Part", 0),
        };
        var definitions = new Dictionary<uint, ObjectEntry> { [rootGuid] = root, [partGuid] = part };

        var world = SavedLotObjectLoader.Restore(saved, types,
            guid => definitions.GetValueOrDefault(guid), _ => null);

        Assert.Equal(0x00080000u, world.ClockManager.FlagsAt(world.ObjectById(1)!.ClockManagerIndex) & 0x00080000u);
        Assert.Equal(0u, world.ClockManager.FlagsAt(world.ObjectById(2)!.ClockManagerIndex) & 0x00080000u);
    }

    [Fact]
    public void RestoreLoadsTheOriginalAlternativeTypeFlagFromTheNativeRegistry()
    {
        const uint rootGuid = 0x12345678;
        const uint partGuid = 0x23456789;
        var game = Directory.CreateTempSubdirectory("native-alternative-type").FullName;
        try
        {
            var objects = Directory.CreateDirectory(Path.Combine(game, "Objects"));
            File.WriteAllBytes(Path.Combine(objects.FullName, "Group.iff"), ChunkBuilder.File(
                ("OBJD", 128, DefinitionBody(rootGuid, 0, 7, ushort.MaxValue, 4)),
                ("OBJD", 129, DefinitionBody(partGuid, 0, 7, 2, 9))));
            var registry = ObjectRegistry.Scan(game);
            var saved = new[]
            {
                new LotObject(1, 1, [], 0, State()),
                new LotObject(2, 2, [], 0, State()),
            };
            var types = new[]
            {
                new ObjectTypeEntry(rootGuid, 0, 0, 1, 0, "Root", 0),
                new ObjectTypeEntry(partGuid, 0, 0, 2, 0, "Part", 0),
            };

            var world = SavedLotObjectLoader.Restore(saved, types, registry.Find, _ => null, typeRegistry: registry);

            Assert.Equal((short)4, world.ObjectById(1)!.TypeRecord!.AutonomyAlternativeTypeFlag);
            Assert.Equal((short)4, world.ObjectById(2)!.TypeRecord!.AutonomyAlternativeTypeFlag);
            Assert.Equal(unchecked((int)rootGuid), world.ObjectById(1)!.TypeRecord!.AlternativeTypeKey);
            Assert.Equal(unchecked((int)rootGuid), world.ObjectById(2)!.TypeRecord!.AlternativeTypeKey);
        }
        finally { Directory.Delete(game, true); }
    }

    [Fact]
    public void RestoreSeparatesFootprintAndRuntimeSlotsBySourceType()
    {
        const uint guid = 0x23456789;
        const ushort slotsId = 44;
        var fields = new ushort[XObject.DefinitionCount];
        fields[(int)ObjdField.Type] = 0;
        fields[(int)ObjdField.SlotsId] = slotsId;
        fields[(int)ObjdField.Guid1] = unchecked((ushort)guid);
        fields[(int)ObjdField.Guid2] = unchecked((ushort)(guid >> 16));
        var objd = new ChunkBuilder();
        foreach (var field in fields) objd.U16(field);
        var slot = new ChunkBuilder().U32(0).S32(10).Tag("TOLS").S32(4)
            .U16(0).F32(-4.5f).F32(6.25f).F32(2.0f)
            .S32(7).S32(8).S32(0).U32(0).S32(-1)
            .S32(16).S32(16).S32(16).S32(0).S32(0)
            .F32(0.1875f).S32(0).S32(-2).S32(16)
            .U16(0).F32(1.5f).F32(2.5f).F32(4.5f)
            .S32(9).S32(10).S32(2).U32(0).S32(-1)
            .S32(16).S32(16).S32(16).S32(0).S32(0)
            .F32(0.1875f).S32(0).S32(-2).S32(16)
            .U16(1).F32(1.25f).F32(-2.5f).F32(3.75f)
            .S32(1).S32(2).S32(3).U32(0).S32(-1)
            .S32(16).S32(16).S32(16).S32(0).S32(0)
            .F32(0.1875f).S32(0).S32(-2).S32(16);
        slot.U16(3).F32(11.25f).F32(-12.5f).F32(13.75f)
            .S32(4).S32(5).S32(6).U32(0x1234).S32(7)
            .S32(8).S32(9).S32(10).S32(11).S32(12)
            .F32(0.75f).S32(14).S32(2).S32(17);
        var file = new IffFile(ChunkBuilder.File(("OBJD", 1, objd.ToArray()), ("SLOT", slotsId, slot.ToArray())), "runtime-slot.iff");
        var definitionChunk = file.OfType(ResourceType.Objd).Single();
        var entry = new ObjectEntry(0, ObjectScan.Objects, "runtime-slot.iff", file, definitionChunk,
            Objd.Read(file, definitionChunk), null);

        var world = SavedLotObjectLoader.Restore(
            [new LotObject(9, 1, [], 0, State(slots: [0, 8, 9]))],
            [new ObjectTypeEntry(guid, 0, 0, 1, 0, "Object", 0)],
            requested => requested == guid ? entry : null,
            _ => null);

        var loaded = world.ObjectById(9)!;
        Assert.Collection(loaded.DefinitionSlots,
            footprint =>
            {
                Assert.Equal((ushort)0, footprint.Type);
                Assert.Equal((-4.5f, 6.25f, 2.0f), (footprint.X, footprint.Y, footprint.Z));
            },
            runtimeDefinition => Assert.Equal((ushort)0, runtimeDefinition.Type),
            runtimeDefinition => Assert.Equal((ushort)1, runtimeDefinition.Type),
            routingDefinition => Assert.Equal((ushort)3, routingDefinition.Type));
        Assert.Collection(loaded.FootprintSlots,
            footprint =>
            {
                Assert.Equal((-4.5f, 6.25f, 2.0f), (footprint.X, footprint.Y, footprint.Height));
                Assert.Equal((short)8, footprint.OccupantObjectId);
                Assert.Equal(5, footprint.HeightCode); // Ground code 0 maps to height mode 5 when elevation is non-zero.
                Assert.Equal((7, 8), (footprint.Standing, footprint.Sitting));
            },
            footprint =>
            {
                Assert.Equal(2.5f, footprint.Height); // Ground code 2 sets elevation height to 2.5.
                Assert.Equal((short)9, footprint.OccupantObjectId);
            });
        var runtime = Assert.Single(loaded.RuntimeSlots);
        Assert.Equal(1.25f, BitConverter.Int32BitsToSingle(runtime.ReadWord(0x00)));
        Assert.Equal(-2.5f, BitConverter.Int32BitsToSingle(runtime.ReadWord(0x04)));
        Assert.Equal(3.75f, BitConverter.Int32BitsToSingle(runtime.ReadWord(0x08)));
        // Slot definitions record 1-based indices into the master slot table rather than local
        // sprite-relative offsets.
        Assert.Equal(3, runtime.ReadWord(0x0c));
        var route = Assert.Single(loaded.RoutingSlots);
        Assert.Equal((11.25f, -12.5f, 13.75f), (route.X, route.Y, route.Z));
        Assert.Equal((4, 5, 6), (route.Standing, route.Sitting, route.Ground));
        Assert.Equal((uint)0x1234, route.Flags);
        Assert.Equal((7, 8, 9, 10, 2, 17),
            (route.SnapTargetSlot, route.MinProximity, route.MaxProximity, route.OptimalProximity, route.Facing, route.Resolution));
        Assert.Equal(0.75f, route.Gradient);
    }

    [Theory]
    [InlineData(0, 0f, 0f, 1)]
    [InlineData(0, 2f, 2f, 5)]
    [InlineData(1, 2f, 0f, 1)]
    [InlineData(2, 2f, 2.5f, 2)]
    [InlineData(3, 2f, 4f, 3)]
    [InlineData(4, 2f, 4.05f, 4)]
    [InlineData(8, 2f, 4f, 8)]
    [InlineData(9, 2f, 2f, 9)]
    public void TypeZeroSlotGroundCodeMatchesTheOriginalNormalization(int groundCode, float sourceHeight, float expectedHeight, int expectedCode)
    {
        var source = new Slot(0, 0f, 0f, sourceHeight, 1, 2, groundCode, 0, -1,
            16, 16, 16, 0, 0, 0.1875f, 0, -2, 16);

        var footprint = ObjectFootprintSlot.FromDefinition(source);

        Assert.Equal(expectedHeight, footprint.Height);
        Assert.Equal(expectedCode, footprint.HeightCode);
    }

    [Fact]
    public void RestoreUsesTheSavedObjectTypeIndexAsTheCrossObjectCodeOwnerSlot()
    {
        const uint ownerGuid = 0x23456789;
        const uint codeOwnerGuid = 0x3456789a;
        var ownerEntry = Definition(ownerGuid, 0);
        var codeOwnerEntry = Definition(codeOwnerGuid, 0);
        var ownerScope = new EmptyTreeTable();
        var codeOwnerScope = new SingleTreeTable(4096, Tree());
        var entries = new Dictionary<uint, ObjectEntry>
        {
            [ownerGuid] = ownerEntry,
            [codeOwnerGuid] = codeOwnerEntry,
        };
        var scopes = new Dictionary<uint, ITreeTable>
        {
            [ownerGuid] = ownerScope,
            [codeOwnerGuid] = codeOwnerScope,
        };
        var saved = new[] { new LotObject(9, 1, [], 0, State()) };
        var types = new[]
        {
            new ObjectTypeEntry(ownerGuid, 0, 0, 1, 0, "Owner", 0),
            new ObjectTypeEntry(codeOwnerGuid, 0, 0, 8, 0, "Code owner", 0),
        };

        var world = SavedLotObjectLoader.Restore(saved, types, guid => entries.GetValueOrDefault(guid),
            entry => scopes[entry.Guid]);

        Assert.Same(codeOwnerScope, world.CodeOwnerScopes[8]);
        var frame = Assert.Single(world.SavedExecutionStacks[9].Frames);
        Assert.Equal((short)8, frame.CodeOwner);
        Assert.Same(codeOwnerScope, frame.Table);
        Assert.Same(codeOwnerScope.Tree, frame.Tree);
    }

    [Fact]
    public void RestoreFlagsTypesWhoseSavedTreeFingerprintsNoLongerMatchAndTheResetDropsTheirSavedStacks()
    {
        const uint changedGuid = 0x23456789;
        const uint sameGuid = 0x3456789a;
        var entries = new Dictionary<uint, ObjectEntry>
        {
            [changedGuid] = Definition(changedGuid, 0),
            [sameGuid] = Definition(sameGuid, 0),
        };
        var saved = new[] { new LotObject(9, 1, [], 0, State(slots: [0, 0])), new LotObject(10, 2, [], 0, State(slots: [0, 0])) };
        var types = new[]
        {
            new ObjectTypeEntry(changedGuid, 5, 0, 1, 0, "Changed", 0),
            new ObjectTypeEntry(sameGuid, 0, 0, 2, 0, "Same", 0),
        };

        var world = SavedLotObjectLoader.Restore(saved, types, guid => entries.GetValueOrDefault(guid),
            _ => new SingleTreeTable(4096, Tree()));

        Assert.Equal([changedGuid], world.ChangedCodeTypeGuids);
        world.ResetObjectsWithChangedCode();
        Assert.False(world.SavedExecutionStacks.ContainsKey(9));
        Assert.True(world.SavedExecutionStacks.ContainsKey(10));
    }

    [Fact]
    public void RestoreRejectsAnUnresolvedLotDefinitionWithItsObjectAndGuid()
    {
        var saved = new[] { new LotObject(7, 1, [], 0, State()) };
        var types = new[] { new ObjectTypeEntry(0xdeadbeef, 0, 0, 1, 0, "Missing", 0) };

        var error = Assert.Throws<InvalidDataException>(() => SavedLotObjectLoader.Restore(
            saved, types, _ => null, _ => null));

        Assert.Contains("object 7", error.Message);
        Assert.Contains("DEADBEEF", error.Message);
    }

    [Fact]
    public void RestoreUsesTheOriginalMissingObjectPlaceholderBySavedObjectType()
    {
        const uint missingObjectGuid = 0xa8b7b894;
        const uint missingPersonGuid = 0x87654321;
        var objectPlaceholder = Definition(0x3c08965f, 4);
        var personPlaceholder = Definition(0x7fd96b54, 2);
        var entries = new Dictionary<uint, ObjectEntry>
        {
            [objectPlaceholder.Guid] = objectPlaceholder,
            [personPlaceholder.Guid] = personPlaceholder,
        };
        var personData = Enumerable.Range(0, 0x50).Select(index => (short)index).ToArray();
        var person = PersonState(personData, new float[16], new float[16]);
        var saved = new[]
        {
            new LotObject(5, 1, [], 0, State()),
            new LotObject(6, 2, [], 0, State(person: person)),
        };
        var types = new[]
        {
            new ObjectTypeEntry(missingObjectGuid, 0, 0, 1, 7, "Outside", 0),
            new ObjectTypeEntry(missingPersonGuid, 0, 0, 2, 2, "Missing Person", 0),
        };

        var world = SavedLotObjectLoader.Restore(saved, types, guid => entries.GetValueOrDefault(guid), _ => null);

        var outside = world.ObjectById(5)!;
        Assert.Equal(missingObjectGuid, (uint)outside.TypeRecord!.Key);
        Assert.Equal(objectPlaceholder.Guid, ReadGuid(outside.Definition));
        var missingPerson = Assert.IsType<XPerson>(world.ObjectById(6));
        Assert.Equal(missingPersonGuid, (uint)missingPerson.TypeRecord!.Key);
        Assert.Equal(personPlaceholder.Guid, ReadGuid(missingPerson.Definition));
    }

    [Fact]
    public void ManagerFlagsRewriteUsesTheStreamByteOrderAndTrailingDword()
    {
        var body = new byte[24];
        var chunk = new IffFile(ChunkBuilder.File(("ObjM", 1, body)), "flags.iff").Find(ResourceType.Parse("ObjM"), 1)!;
        var decoded = State() with { ManagerFlags = 0x0000ffff };
        var objects = new[] { new LotObject(7, 1, [], 0, decoded) { BlockEnd = 20 } };

        var little = LotObjects.WriteManagerFlags(objects, chunk, new Dictionary<short, uint> { [7] = 0x00010005 });
        Assert.Equal([5, 0, 1, 0, 0, 0, 0, 0], little[16..24]);

        var swappedBody = new byte[24];
        swappedBody[0] = 1;
        var swappedChunk = new IffFile(ChunkBuilder.File(("ObjM", 1, swappedBody)), "flags-be.iff").Find(ResourceType.Parse("ObjM"), 1)!;
        var big = LotObjects.WriteManagerFlags(objects, swappedChunk, new Dictionary<short, uint> { [7] = 0x00010005 });
        Assert.Equal([0, 1, 0, 5, 0, 0, 0, 0], big[16..24]);

        var codedBody = new byte[24];
        codedBody[12] = 1;
        var codedChunk = new IffFile(ChunkBuilder.File(("ObjM", 1, codedBody)), "flags-coded.iff").Find(ResourceType.Parse("ObjM"), 1)!;
        Assert.Throws<NotSupportedException>(() =>
            LotObjects.WriteManagerFlags(objects, codedChunk, new Dictionary<short, uint> { [7] = 1 }));
    }

    [Fact]
    public void NeighborhoodDataWriteRoundTripsVariablesAndInventoriesLosslessly()
    {
        var variables = Enumerable.Range(0, 16)
            .Select(index => (short)(index * 11 - 30))
            .ToArray();
        var inventories = new Dictionary<short, IReadOnlyList<NeighborhoodData.InventoryItem>>
        {
            [2] =
            [
                new NeighborhoodData.InventoryItem(1, 0x11223344, 3),
                new NeighborhoodData.InventoryItem(5, 0xAABBCCDD, 12),
            ],
            [7] =
            [
                new NeighborhoodData.InventoryItem(9, 0x99887766, 1),
            ],
        };

        var payload = NeighborhoodData.Write(variables, inventories, version: 0x49);
        var (file, chunk) = new ChunkBuilder().Raw(payload).InFile("NGBH", 1);
        var roundTripped = NeighborhoodData.Read(file, chunk);

        Assert.Equal(0x49, roundTripped.Version);
        Assert.Equal(variables, roundTripped.Data);
        Assert.Equal(2, roundTripped.Inventories.Count);
        Assert.Equal(inventories[2], roundTripped.Inventories[2]);
        Assert.Equal(inventories[7], roundTripped.Inventories[7]);

        var reserialized = NeighborhoodData.Write(roundTripped);
        var directRead = NeighborhoodData.Read((ReadOnlyMemory<byte>)reserialized);
        Assert.Equal(variables, directRead.Data);
        Assert.Equal(inventories[2], directRead.Inventories[2]);
        Assert.Equal(inventories[7], directRead.Inventories[7]);
    }

    [Fact]
    public void TypeAttributesWriteRoundTripsAndSharesBackingArraysPerGuid()
    {
        const uint guidA = 0x12345678;
        const uint guidB = 0x87654321;
        var entries = new Dictionary<uint, short[]>
        {
            [guidA] = [-15, 0, 42, 1024],
            [guidB] = [7, -1, 99],
        };

        var payload = TypeAttributes.Write(entries, Version: 0x49);
        var decoded = TypeAttributes.Read((ReadOnlyMemory<byte>)payload);
        Assert.Equal(0x49, decoded.Version);
        Assert.Equal(entries[guidA], decoded.AttributesByGuid[guidA]);
        Assert.Equal(entries[guidB], decoded.AttributesByGuid[guidB]);

        var uncodedPayload = TypeAttributes.Write(entries, Version: 0x49, coded: false);
        var decodedUncoded = TypeAttributes.Read((ReadOnlyMemory<byte>)uncodedPayload);
        Assert.Equal(entries[guidA], decodedUncoded.AttributesByGuid[guidA]);
        Assert.Equal(entries[guidB], decodedUncoded.AttributesByGuid[guidB]);

        var definitionA = DefinitionWithAttributeCount(guidA, type: 0, count: 6);
        var saved = new[]
        {
            new LotObject(10, 1, [], 0, State()),
            new LotObject(11, 1, [], 0, State()),
        };
        var types = new[] { new ObjectTypeEntry(guidA, 0, 0, 1, 0, "SharedObject", 0) };
        var world = SavedLotObjectLoader.Restore(saved, types,
            requested => requested == guidA ? definitionA : null,
            _ => null,
            decoded.AttributesByGuid);

        var first = world.ObjectById(10)!;
        var second = world.ObjectById(11)!;
        Assert.Same(first.TypeAttributes, second.TypeAttributes);
        Assert.Same(world.SharedTypeAttributes[guidA], first.TypeAttributes);
        Assert.Equal(new short[] { -15, 0, 42, 1024, 0, 0 }, first.TypeAttributes);

        first.TypeAttributes[4] = 555;
        Assert.Equal((short)555, second.TypeAttributes[4]);

        var dynamicObj = world.Add(new XObject(12, first.Definition.ToArray())
        {
            TypeRecord = new TypeRecord(unchecked((int)guidA)),
        });
        Assert.Same(first.TypeAttributes, dynamicObj.TypeAttributes);
    }

    [Fact]
    public void ReloadRoomMapsUpdatesRoomsInPlaceWithoutThrowing()
    {
        var world = new SimulationWorld();
        var item = world.Add(new XObject(1, new short[XObject.DefinitionCount])
        {
            Position = new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1),
        });

        static SimsEngine.World.RoomMap BuildRoomMap(byte[] wallRecords)
        {
            var body = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(8).U32(0).Raw(wallRecords);
            return SimsEngine.World.RoomMap.Build(
                new IffFile(ChunkBuilder.File(("Arry", 2, body.ToArray())), "reload-rooms.iff"), 1, 64, 64);
        }

        var initialMap = BuildRoomMap(new byte[64 * 64 * 8]);
        world.LoadRoomMaps(new Dictionary<int, SimsEngine.World.RoomMap> { [1] = initialMap });
        Assert.True(world.RoomManagerRecordsLoaded);
        var initialRoom = item.RoomWord84;
        Assert.True(initialRoom >= 0);
        Assert.True(world.RoomIsOutside(initialRoom));

        var boxedWalls = new byte[64 * 64 * 8];
        static void SetWall(byte[] records, int x, int y, byte segments, byte westStyle, byte northStyle)
        {
            var offset = (x * 64 + y) * 8;
            records[offset] = segments;
            records[offset + 1] = 0x0f;
            records[offset + 2] = westStyle;
            records[offset + 3] = northStyle;
        }
        SetWall(boxedWalls, 10, 10, 0x0f, 1, 1);
        SetWall(boxedWalls, 9, 10, 0x04, 0, 0);
        SetWall(boxedWalls, 10, 9, 0x08, 0, 0);
        SetWall(boxedWalls, 11, 10, 0x01, 1, 0);
        SetWall(boxedWalls, 10, 11, 0x02, 0, 1);
        var updatedMap = BuildRoomMap(boxedWalls);

        world.ReloadRoomMaps(new Dictionary<int, SimsEngine.World.RoomMap> { [1] = updatedMap });

        Assert.Same(updatedMap, world.LotRoomMaps[1]);
        Assert.NotEqual((short)-5, item.RoomWord84);
        Assert.False(world.RoomIsOutside(item.RoomWord84));
    }

    [Fact]
    public void CountFamilyFriendsCountsEligibleNonFamilyNeighborsAtOrAboveThreshold()
    {
        static Neighbor MakeNeighbor(int slot, short id, uint guid, short family, short species = 0,
            params (int TargetId, int Score)[] relationships)
        {
            var data = new short[80];
            data[61] = family;
            data[65] = species;
            var relMap = relationships.ToDictionary(r => r.TargetId, r => (IReadOnlyList<int>)[r.Score, 0]);
            return new Neighbor(slot, 4, $"Sim {id}", 0, data, id, guid, relMap);
        }

        var memberA = MakeNeighbor(0, 1, 0x1001, 5, 0, (2, 90), (3, 50), (4, 80), (5, 49), (6, 75), (7, 85));
        var memberB = MakeNeighbor(1, 2, 0x1002, 5, 0, (1, 90), (3, 60));
        var mutualFriend = MakeNeighbor(2, 3, 0x2001, 6, 0, (1, 50), (2, 60));
        var secondFriend = MakeNeighbor(3, 4, 0x2002, 7, 0, (1, 55));
        var belowThreshold = MakeNeighbor(4, 5, 0x2003, 7, 0, (1, 49));
        var noFamilyNeighbor = MakeNeighbor(5, 6, 0x2004, 0, 0, (1, 75));
        var petNeighbor = MakeNeighbor(6, 7, 0x2005, 8, 2, (1, 85));

        var neighbors = new Neighbors(0x49, 7,
            [memberA, memberB, mutualFriend, secondFriend, belowThreshold, noFamilyNeighbor, petNeighbor]);
        var family = Family.CreateNew(familyNumber: 5, houseNumber: 1, members: [0x1001u, 0x1002u]);

        Assert.Equal(2, SimulationWorld.CountFamilyFriends(family, neighbors));
    }

    [Fact]
    public void ComputeSaveHouseInfoUpdatesGlobalsAndReturnsBabyAndPhoneBits()
    {
        var world = new SimulationWorld();
        var emptyWallChunk = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(8).U32(0).Raw(new byte[64 * 64 * 8]);
        world.LoadRoomMaps(new Dictionary<int, SimsEngine.World.RoomMap>
        {
            [1] = SimsEngine.World.RoomMap.Build(
                new IffFile(ChunkBuilder.File(("Arry", 2, emptyWallChunk.ToArray())), "save-house-info.iff"), 1, 64, 64),
        });

        var emptyMask = world.ComputeSaveHouseInfo(out var emptyHasHouse, out var lotSize, out var emptyFilter, out var emptyFlags);
        Assert.False(emptyHasHouse);
        Assert.Equal(64, lotSize);
        Assert.Equal(0, emptyFilter);
        Assert.Equal(0, emptyFlags);
        Assert.Equal(0, emptyMask);
        Assert.Equal((short)64, world[SimulationGlobal.LotSize]);
        Assert.Equal((short)0, world[SimulationGlobal.LotHasHouse]);
        Assert.Equal((short)0, world[SimulationGlobal.FilterFlagsForTransitScreen]);

        var chairDef = new short[XObject.DefinitionCount];
        chairDef[(int)ObjdField.RoomSort] = 1;
        var chair = world.Add(new XObject(1, chairDef)
        {
            Position = new ObjectPosition(12 * 16 + 8, 12 * 16 + 8, 1),
        });
        chair[ObjectData.Category] = 4; // Category 4 maps to transit filter bit 0x1.

        var baby = world.Add(new XObject(2, new short[XObject.DefinitionCount])
        {
            Position = new ObjectPosition(14 * 16 + 8, 14 * 16 + 8, 1),
        });
        baby[ObjectData.Category] = 1; // Category 1 maps to baby bit 0x1.

        var phone = world.Add(new XObject(3, new short[XObject.DefinitionCount])
        {
            Position = new ObjectPosition(15 * 16 + 8, 15 * 16 + 8, 1),
        });
        phone[ObjectData.Category] = 9; // Category 9 maps to phone bit 0x2.

        var cashRegister = world.Add(new XObject(4, new short[XObject.DefinitionCount])
        {
            Position = new ObjectPosition(16 * 16 + 8, 16 * 16 + 8, 1),
        });
        cashRegister[ObjectData.Category] = 10;

        var shopRack = world.Add(new XObject(5, new short[XObject.DefinitionCount])
        {
            Position = new ObjectPosition(17 * 16 + 8, 17 * 16 + 8, 1),
        });
        shopRack[ObjectData.Category] = 0x11; // Category 0x11 maps to cash register transit filter bit 0x8.

        var mask = world.ComputeSaveHouseInfo(out var lotHasHouse, out var updatedSize, out var transitFilterFlags, out var familyFlagsMask);
        Assert.True(lotHasHouse);
        Assert.Equal(64, updatedSize);
        Assert.Equal(0x9, transitFilterFlags);
        Assert.Equal(0x3, familyFlagsMask);
        Assert.Equal(0x3, mask);
        Assert.Equal((short)1, world[SimulationGlobal.LotHasHouse]);
        Assert.Equal((short)64, world[SimulationGlobal.LotSize]);
        Assert.Equal((short)0x9, world[SimulationGlobal.FilterFlagsForTransitScreen]);
    }

    private static ObjectEntry Definition(uint guid, ushort type, ushort masterId = 0, ushort numDynamicSprites = 0,
        ushort objectVersion = 0, ushort footprintMask = 0)
    {
        var fields = new ushort[XObject.DefinitionCount];
        fields[(int)ObjdField.Type] = type;
        fields[(int)ObjdField.MasterId] = masterId;
        fields[(int)ObjdField.NumDynamicSprites] = numDynamicSprites;
        fields[(int)ObjdField.ObjectVersion] = objectVersion;
        fields[(int)ObjdField.FootprintMask] = footprintMask;
        fields[(int)ObjdField.Guid1] = unchecked((ushort)guid);
        fields[(int)ObjdField.Guid2] = unchecked((ushort)(guid >> 16));
        var builder = new ChunkBuilder();
        foreach (var field in fields) builder.U16(field);
        var (file, chunk) = builder.InFile("OBJD");
        return new ObjectEntry(0, ObjectScan.Objects, $"{guid:X8}.iff", file, chunk, Objd.Read(file, chunk), null);
    }

    private static byte[] DefinitionFile(uint guid, ushort type, ushort masterId, ushort subIndex, ushort buildModeType) =>
        ChunkBuilder.File(("OBJD", 4096, DefinitionBody(guid, type, masterId, subIndex, buildModeType)));

    private static byte[] DefinitionBody(uint guid, ushort type, ushort masterId, ushort subIndex, ushort buildModeType)
    {
        var fields = new ushort[XObject.DefinitionCount];
        fields[(int)ObjdField.Type] = type;
        fields[(int)ObjdField.MasterId] = masterId;
        fields[(int)ObjdField.SubIndex] = subIndex;
        fields[(int)ObjdField.BuildModeType] = buildModeType;
        fields[(int)ObjdField.Guid1] = unchecked((ushort)guid);
        fields[(int)ObjdField.Guid2] = unchecked((ushort)(guid >> 16));
        var builder = new ChunkBuilder();
        foreach (var field in fields) builder.U16(field);
        return builder.ToArray();
    }

    private static Neighbors SavedNeighbors(params (uint Guid, short Id)[] slots)
    {
        var builder = new ChunkBuilder().U8(0).U8(0).U8(0).U8(0).S32(0x45).Tag("SRBN").U32((uint)slots.Length);
        foreach (var (guid, id) in slots)
        {
            if (guid == 0) { builder.U32(0); continue; }
            builder.U32(1).S32(4).EvenString($"Neighbor {id}").S32(0).S32(0).S16(id).U32(guid).S32(0);
        }
        var (file, chunk) = builder.InFile("NBRS");
        return Neighbors.Read(file, chunk);
    }

    private static ObjectEntry DefinitionWithAttributeCount(uint guid, ushort type, int count)
    {
        var fields = new ushort[XObject.DefinitionCount];
        fields[(int)ObjdField.Type] = type;
        fields[(int)ObjdField.NumAttributes] = (ushort)count;
        fields[(int)ObjdField.Guid1] = unchecked((ushort)guid);
        fields[(int)ObjdField.Guid2] = unchecked((ushort)(guid >> 16));
        var definition = new ChunkBuilder();
        foreach (var field in fields) definition.U16(field);
        var file = new IffFile(ChunkBuilder.File(("OBJD", 128, definition.ToArray())), "type-attributes.iff");
        var objd = file.Find(ResourceType.Objd, 128)!;
        return new ObjectEntry(0, ObjectScan.Objects, file.Source, file, objd, Objd.Read(file, objd), null);
    }

    private static ObjectEntry DefinitionWithInteraction(uint guid, ushort type, ushort masterId, ushort treeTableId,
        uint threshold, uint flags, short interactionGroup = 0)
    {
        var fields = new ushort[XObject.DefinitionCount];
        fields[(int)ObjdField.Type] = type;
        fields[(int)ObjdField.InteractionGroup] = unchecked((ushort)interactionGroup);
        fields[(int)ObjdField.MasterId] = masterId;
        fields[(int)ObjdField.TreeTableId] = treeTableId;
        fields[(int)ObjdField.Guid1] = unchecked((ushort)guid);
        fields[(int)ObjdField.Guid2] = unchecked((ushort)(guid >> 16));
        var objd = new ChunkBuilder();
        foreach (var field in fields) objd.U16(field);
        var ttab = new ChunkBuilder().S16(1).S16(8).U16(4100).U16(0).S32(0).U32(flags)
            .S32(0).U32(0).F32(0).U32(threshold).S32(0);
        var file = new IffFile(ChunkBuilder.File(("OBJD", 128, objd.ToArray()), ("TTAB", treeTableId, ttab.ToArray())),
            $"{guid:X8}.iff");
        var chunk = Assert.Single(file.OfType(ResourceType.Objd));
        return new ObjectEntry(0, ObjectScan.Objects, file.Source, file, chunk, Objd.Read(file, chunk), null);
    }

    private static Bhav Tree()
    {
        var body = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(0).U8(0xFE).U8(0xFF).Raw(new byte[8]);
        var (file, chunk) = body.InFile("BHAV", 4096);
        return Bhav.Read(file, chunk);
    }

    private static uint ReadGuid(short[] fields) =>
        unchecked((uint)(ushort)fields[(int)ObjdField.Guid1] |
                  ((uint)(ushort)fields[(int)ObjdField.Guid2] << 16));

    private static ObjectState State(PersonState? person = null,
        IReadOnlyList<ObjectRelationship?>? relationships = null, MultiTileState? multiTile = null,
        short[]? slots = null) =>
        new(
            [14, 22, 100, 104, 256, 512],
            2,
            0x1234,
            [1, 2],
            Enumerable.Range(0, 8).Select(value => (short)(value + 3)).ToArray(),
            Enumerable.Range(0, XObject.DataCount).Select(value => (short)(value + 10)).ToArray(),
            16,
            [new ObjectStackFrame(22, 4096, 3, [4, 5], [6], 7, 8)],
            relationships ?? [],
            slots ?? [8, 9],
            [6, 7],
            1234,
            person,
            multiTile);

    private static PersonState PersonState(short[] data, float[] motives, float[] auxiliary)
    {
        var emptyAction = new PersonAction(0, 0, 0, 0, 0, 0, [0, 0, 0, 0], 0, 0, 0, 0);
        return new PersonState(new PersonCharacter(0, 0, [], [], [], null, []), motives, auxiliary, data,
            0, emptyAction, emptyAction, [], [], []);
    }
}
