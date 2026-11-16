using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsSimulation;
using Xunit;

namespace SimsEngine.Tests.Simulation;

public sealed class LiveStatePersistenceTests
{
    private static IffFile EmptyLot(bool coded, int version = 0x45)
    {
        var objects = PersistWriter.Start(false, version, "MjbO");
        objects.WriteCodingFlag(coded);
        objects.Int16(0); objects.BeginBlock(); objects.Int16(0); objects.Int32(0); objects.String("");
        var types = PersistWriter.Start(false, 3, "tjbo"); types.Int32(0);
        var simi = new ChunkBuilder().U8(0).U8(0).U8(0).U8(0).S32(0x0e).Tag("IMIS");
        for (var i = 0; i < 0x10; i++) simi.S16(0);
        simi.U32(0).S32(0).S16(0).S32(0).S32(0x12345678).S32(0);
        return new IffFile(ChunkBuilder.File(("ObjM", 1, objects.ToArray()), ("objt", 0, types.ToArray()), ("SIMI", 1, simi.ToArray())), "live-state-test.iff");
    }

    private static byte[] Definition(uint guid, ushort type = 0, ushort master = 0, ushort subIndex = 0) =>
        ChunkBuilder.File(("OBJD", 1, DefinitionChunk(guid, type, master, subIndex)), ("BHAV", 4096, BehaviorChunk()));

    private static byte[] DefinitionChunk(uint guid, ushort type = 0, ushort master = 0, ushort subIndex = 0)
    {
        var fields = new ushort[XObject.DefinitionCount];
        fields[(int)ObjdField.Guid1] = unchecked((ushort)guid);
        fields[(int)ObjdField.Guid2] = (ushort)(guid >> 16);
        fields[(int)ObjdField.Type] = type;
        fields[(int)ObjdField.MasterId] = master;
        fields[(int)ObjdField.SubIndex] = subIndex;
        fields[(int)ObjdField.NumAttributes] = 5;
        fields[(int)ObjdField.NumDynamicSprites] = 4;
        fields[70] = 0x5678; fields[71] = 0x1234;
        var builder = new ChunkBuilder();
        foreach (var field in fields) builder.U16(field);
        return builder.ToArray();
    }

    private static byte[] BehaviorChunk() => new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(1)
        .U16(0).U8(254).U8(255).Raw(new byte[8]).ToArray();

    [Fact]
    public void LiveStateSerializationRoundtrip_PreservesMotivesQueuedActionsAndPersonDataInStandardIffChunks()
    {
        var game = Directory.CreateTempSubdirectory("m3-live-state-test").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(game, "Objects")).FullName;
            File.WriteAllBytes(Path.Combine(folder, "obj.iff"), Definition(0x11112222));
            File.WriteAllBytes(Path.Combine(folder, "person.iff"), Definition(0x33334444, type: 2));

            var registry = ObjectRegistry.Scan(game);
            var library = GlobalLibrary.Open(game);
            var empty = EmptyLot(coded: false);
            var world = SavedLotObjectLoader.Load(empty, registry, library);

            var obj = world.ObjectFactory!(0x11112222)!;
            world.Add(obj);
            obj.Position = new ObjectPosition(120, 240, 1);
            obj.Attributes[0] = 123;
            obj.Attributes[1] = 456;
            obj.Data[5] = 789;

            var person = (XPerson)world.ObjectFactory!(0x33334444)!;
            world.Add(person);
            person.SavedCharacter = new PersonCharacter(0, 0, Enumerable.Repeat(("", ""), 5).ToArray(), [],
                ["Tester", "", ""], 0, new float[9]);
            person.Motives[0] = 85.5f;
            person.Motives[3] = 42.0f;
            person.PersonData[10] = 99;
            person.PersonData[20] = 55;

            var queued = ActionQueueSlot.EmptyRecord();
            queued.RawDword10 = 15;
            queued.Sequence = 1;
            person.ActionQueue[0] = queued;
            person.ActionQueueEnd = 1;

            var (savedLot, _) = world.WriteSavedLotObjectsTo(empty);
            var savedWithSimi = world.WriteSavedSimulationStateTo(savedLot);

            Assert.Contains(savedWithSimi.Chunks, chunk => chunk.Type == ResourceType.Parse("ObjM"));
            Assert.Contains(savedWithSimi.Chunks, chunk => chunk.Type == ResourceType.Parse("SIMI"));

            var loaded = SavedLotObjectLoader.Load(savedWithSimi, registry, library);
            var loadedObj = loaded.ObjectById(obj.Id)!;
            Assert.Equal(new ObjectPosition(120, 240, 1), loadedObj.Position);
            Assert.Equal(123, loadedObj.Attributes[0]);
            Assert.Equal(456, loadedObj.Attributes[1]);
            Assert.Equal(789, loadedObj.Data[5]);

            var loadedPerson = Assert.IsType<XPerson>(loaded.ObjectById(person.Id));
            Assert.Equal(85.5f, loadedPerson.Motives[0]);
            Assert.Equal(42.0f, loadedPerson.Motives[3]);
            Assert.Equal(99, loadedPerson.PersonData[10]);
            Assert.Equal(55, loadedPerson.PersonData[20]);
            Assert.Equal(15, loadedPerson.ActionQueue[0].RawDword10);
        }
        finally { Directory.Delete(game, true); }
    }

    [Fact]
    public void GenericSimsCall_CallTwentySevenAndTwentyEight_RecoversWhenWaitStateOneAndNoPendingRequest()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 1234;
        context.Frame.WaitState = 1;
        Assert.False(context.World.GenericSimsCallRequests.HasPending(me));

        // Reloading an object in wait state 1 with no active pending request automatically re-issues the
        // generic call request.
        var result = GenericSimsCallPrimitive.Execute(context, [(byte)0x27, 0, 0, 0, 0, 0, 0, 0]);

        Assert.Equal(PrimitiveResult.Stop, result);
        Assert.True(context.World.GenericSimsCallRequests.HasPending(me));
        Assert.Equal(1, context.Frame.WaitState);
    }

    [Fact]
    public void ObjectReset_HierSever_DetachesSlotsAndResetsDynamicAttributes()
    {
        var game = Directory.CreateTempSubdirectory("m3-reset-test").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(game, "Objects")).FullName;
            File.WriteAllBytes(Path.Combine(folder, "parent.iff"), Definition(0x10101010));
            File.WriteAllBytes(Path.Combine(folder, "child.iff"), Definition(0x20202020));

            var registry = ObjectRegistry.Scan(game);
            var library = GlobalLibrary.Open(game);
            var empty = EmptyLot(coded: false);
            var world = SavedLotObjectLoader.Load(empty, registry, library);

            var parent = world.Add(world.ObjectFactory!(0x10101010)!);
            var child = world.Add(world.ObjectFactory!(0x20202020)!);

            parent.Slots.Add(child.Id);
            child[ObjectData.ParentId] = parent.Id;
            child[ObjectData.SlotHierNumber] = 1;
            child.Attributes[0] = 42;
            child.Temporaries[0] = 77;
            child.DynamicSpriteFlags[0] = 5;

            world.HierSever(child);
            Assert.Equal(0, child[ObjectData.ParentId]);
            Assert.DoesNotContain(child.Id, parent.Slots);

            child.Attributes[0] = 99;
            child.Temporaries[0] = 88;
            child.DynamicSpriteFlags[0] = 12;
            world.ResetObjectForChangedCode(child);

            Assert.All(child.Attributes, attr => Assert.Equal(0, attr));
            Assert.All(child.Temporaries, temp => Assert.Equal(0, temp));
            // Object reset clears attributes and temporaries while preserving dynamic sprite flags.
            Assert.Equal((short)12, child.DynamicSpriteFlags[0]);
            Assert.Empty(child.SavedStack);
        }
        finally { Directory.Delete(game, true); }
    }

    [Fact]
    public void MultiNeighborhood_StorageSlot_IsolatesSlotNamesAcrossUserData1Through99()
    {
        // Neighborhood 1 preserves bare storage slot names without prefixes.
        Assert.Equal("quicksave", Neighborhoods.StorageSlot("quicksave", 1));
        Assert.Equal("manual", Neighborhoods.StorageSlot("manual", 1));

        // Non-default neighborhoods prefix storage slot names with the neighborhood identifier to prevent
        // collisions.
        Assert.Equal("neighborhood2-quicksave", Neighborhoods.StorageSlot("quicksave", 2));
        Assert.Equal("neighborhood99-quicksave", Neighborhoods.StorageSlot("quicksave", 99));

        Assert.True(Neighborhoods.IsReservedSlot("neighborhood2-quicksave"));
        Assert.False(Neighborhoods.IsReservedSlot("quicksave"));
        Assert.Throws<ArgumentException>(() => Neighborhoods.StorageSlot("neighborhood2-test", 1));

        // Neighborhood 1 maps to directory UserData, while subsequent neighborhoods append their number
        // (e.g., UserData2).
        Assert.Equal("UserData", Neighborhoods.DirectoryName(1));
        Assert.Equal("UserData2", Neighborhoods.DirectoryName(2));
        Assert.Equal("UserData99", Neighborhoods.DirectoryName(99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Neighborhoods.DirectoryName(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Neighborhoods.DirectoryName(100));
    }

    [Fact]
    public void UnhousedCharacterSweep_PreservesFamilyBinSims()
    {
        var game = Directory.CreateTempSubdirectory("m3-sweep-test").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(game, "Objects")).FullName;
            File.WriteAllBytes(Path.Combine(folder, "person.iff"), Definition(0x55556666, type: 2));

            var registry = ObjectRegistry.Scan(game);
            var library = GlobalLibrary.Open(game);
            var empty = EmptyLot(coded: false);
            var world = SavedLotObjectLoader.Load(empty, registry, library);

            // Person data index 61 stores the family identifier, where zero denotes an unhoused orphan.
            var orphanPersonData = new short[80];
            orphanPersonData[61] = 0;
            orphanPersonData[67] = 0;
            var orphan = new NeighborState(1, orphanPersonData)
            {
                TypeRecordKey = unchecked((int)0x99998888),
                FriendEligibilityObjectFlagsB4 = 4u
            };

            var binPersonData = new short[80];
            binPersonData[61] = 5;
            binPersonData[67] = 0;
            var binSim = new NeighborState(2, binPersonData)
            {
                TypeRecordKey = 0x55556666,
                FriendEligibilityObjectFlagsB4 = 4u
            };

            var housedPersonData = new short[80];
            housedPersonData[61] = 10;
            housedPersonData[67] = 0;
            var housedSim = new NeighborState(3, housedPersonData)
            {
                TypeRecordKey = 0x55556666,
                FriendEligibilityObjectFlagsB4 = 4u
            };

            world.Neighbors[1] = orphan;
            world.Neighbors[2] = binSim;
            world.Neighbors[3] = housedSim;
            world.NeighborWalkOrder.Add(orphan);
            world.NeighborWalkOrder.Add(binSim);
            world.NeighborWalkOrder.Add(housedSim);

            var families = new List<Family>
            {
                Family.CreateNew(familyNumber: 5, houseNumber: 0, members: [0x55556666]),
                Family.CreateNew(familyNumber: 10, houseNumber: 1, members: [0x55556666])
            };

            var swept = world.DeleteUnhousedCharacters(guid =>
                families.Any(f => f.Members.Contains(unchecked((uint)guid))));

            Assert.Contains((short)1, swept);
            Assert.DoesNotContain((short)2, swept);
            Assert.DoesNotContain((short)3, swept);
        }
        finally { Directory.Delete(game, true); }
    }

    [Fact]
    public void FamilyFormats_WithHouseNumberAndCreateNew_RoundtripsVersionNine()
    {
        var members = new uint[] { 0x11112222, 0x33334444 };
        var family = Family.CreateNew(familyNumber: 7, houseNumber: 0, members: members, budget: 20_000);

        Assert.Equal(9, family.Version);
        Assert.Equal(7, family.FamilyNumber);
        Assert.Equal(0, family.HouseNumber);
        Assert.Equal(20_000, family.Budget);
        Assert.Equal(2, family.Members.Count);
        Assert.Equal(0x11112222u, family.Members[0]);
        Assert.Equal(0x33334444u, family.Members[1]);

        var moved = family.WithHouseNumber(12);
        Assert.Equal(12, moved.HouseNumber);
        Assert.Equal(7, moved.FamilyNumber);
        Assert.Equal(20_000, moved.Budget);
        Assert.Equal(2, moved.Members.Count);

        var bytes = moved.Write(chunkId: 7);
        Assert.NotEmpty(bytes);

        var iff = new IffFile(ChunkBuilder.File(("FAMI", 7, bytes)), "family-test.iff");
        var chunk = iff.Find(ResourceType.Parse("FAMI"), 7)!;
        var loaded = Family.Read(iff, chunk);

        Assert.Equal(9, loaded.Version);
        Assert.Equal(7, loaded.FamilyNumber);
        Assert.Equal(12, loaded.HouseNumber);
        Assert.Equal(20_000, loaded.Budget);
        Assert.Equal(2, loaded.Members.Count);
        Assert.Equal(0x11112222u, loaded.Members[0]);
        Assert.Equal(0x33334444u, loaded.Members[1]);
    }
}
