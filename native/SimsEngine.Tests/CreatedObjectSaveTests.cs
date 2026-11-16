using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class CreatedObjectSaveTests
{
    private static IffFile EmptyLot(bool coded, int version = 0x45)
    {
        var objects = PersistWriter.Start(false, version, "MjbO");
        objects.WriteCodingFlag(coded);
        objects.Int16(0); objects.BeginBlock(); objects.Int16(0); objects.Int32(0); objects.String("");
        var types = PersistWriter.Start(false, 3, "tjbo"); types.Int32(0);
        return new IffFile(ChunkBuilder.File(("ObjM", 1, objects.ToArray()), ("objt", 0, types.ToArray())), "created-test.iff");
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
        fields[(int)ObjdField.NumAttributes] = 3;
        fields[70] = 0x5678; fields[71] = 0x1234;
        var builder = new ChunkBuilder();
        foreach (var field in fields) builder.U16(field);
        return builder.ToArray();
    }

    private static byte[] BehaviorChunk() => new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(1)
        .U16(0).U8(254).U8(255).Raw(new byte[8]).ToArray();

    [Fact]
    public void NativeCreationBuildsOrderedMultiTileGroupAndReloadsItsLinks()
    {
        var game = Directory.CreateTempSubdirectory("native-created-multitile").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(game, "Objects")).FullName;
            // Multi-tile objects defined in a single package group all root and child parts under a shared
            // master resource.
            File.WriteAllBytes(Path.Combine(folder, "multi.iff"), ChunkBuilder.File(
                ("OBJD", 1, DefinitionChunk(0x13570001, 0, 17, ushort.MaxValue)),
                ("OBJD", 2, DefinitionChunk(0x13570002, 0, 17, 0)),
                ("OBJD", 3, DefinitionChunk(0x13570003, 0, 17, 1)),
                ("OBJD", 4, DefinitionChunk(0x13570004, 0, 17, 0x0080)),
                ("BHAV", 4096, BehaviorChunk())));
            File.WriteAllBytes(Path.Combine(folder, "caller.iff"), Definition(0x13570005));
            var registry = ObjectRegistry.Scan(game);
            var library = GlobalLibrary.Open(game);
            var empty = EmptyLot(coded: false);
            var world = SavedLotObjectLoader.Load(empty, registry, library);
            var caller = world.ObjectFactory!(0x13570005)!;
            world.Add(caller);
            caller.Position = new ObjectPosition(32, 48, 0);
            var frame = new StackFrame(0, [], []);
            var context = new TreeContext(world, caller, frame);
            var operand = new byte[] { 1, 0, 87, 19, 6, 0, 0, 0 };

            var rejected = operand.ToArray();
            rejected[4] = 5;
            Assert.Equal(PrimitiveResult.False, CreateObjectInstancePrimitive.Execute(context, rejected));
            Assert.Single(world.Objects);
            Assert.All(world.ClockManager.Entries.Skip(1), entry => Assert.Null(entry.Object));

            Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, operand));
            var rootId = frame.StackObject;
            var group = new[] { world.ObjectById(rootId)!, world.ObjectById((short)(rootId + 1))!,
                world.ObjectById((short)(rootId + 2))! };

            Assert.Null(world.ObjectById((short)(rootId + 3))); // SubIndex -1 denotes an unused part slot and is never instantiated.
            Assert.Equal(3, group.Length);
            Assert.Equal(new short[] { rootId, (short)(rootId + 1), (short)(rootId + 2) },
                group.Select(item => item.Id));
            Assert.Equal((short)(rootId + 1), group[0].MultiTileNextId);
            Assert.Equal((short)(rootId + 2), group[1].MultiTileNextId);
            Assert.Equal((short)0, group[2].MultiTileNextId);
            Assert.Equal((short)0, group[0].MultiTileMasterId);
            Assert.All(group.Skip(1), item => Assert.Equal(rootId, item.MultiTileMasterId));
            Assert.All(group, item => Assert.Equal(0, item.SavedMultiTileState!.Value0x1A0));
            Assert.Equal(0, group[0].SavedMultiTileState!.Value0x1A4);
            Assert.Equal(1, group[1].SavedMultiTileState!.Value0x1A4); // The low byte of SubIndex represents the part's unsigned Y tile offset.
            Assert.Equal(128, group[2].SavedMultiTileState!.Value0x1A4);

            var (saved, _) = world.WriteSavedLotObjectsTo(empty);
            var restored = SavedLotObjectLoader.Load(saved, registry, library);

            Assert.Equal((short)(rootId + 1), restored.ObjectById(rootId)!.MultiTileNextId);
            Assert.Equal((short)(rootId + 2), restored.ObjectById((short)(rootId + 1))!.MultiTileNextId);
            Assert.Equal((short)0, restored.ObjectById((short)(rootId + 2))!.MultiTileNextId);
            Assert.Equal(rootId, restored.ObjectById((short)(rootId + 1))!.MultiTileMasterId);
            Assert.Equal(rootId, restored.ObjectById((short)(rootId + 2))!.MultiTileMasterId);
        }
        finally { Directory.Delete(game, recursive: true); }
    }

    private static byte[] DefinitionWithQueuedTree(uint guid)
    {
        var fields = new ushort[XObject.DefinitionCount];
        fields[(int)ObjdField.Guid1] = unchecked((ushort)guid);
        fields[(int)ObjdField.Guid2] = (ushort)(guid >> 16);
        fields[(int)ObjdField.Type] = 0;
        fields[(int)ObjdField.NumAttributes] = 3;
        var definition = new ChunkBuilder();
        foreach (var field in fields) definition.U16(field);
        static byte[] Assign(short temp, short value, byte operation) =>
            [(byte)temp, (byte)(temp >> 8), (byte)value, (byte)(value >> 8), 0, operation, 8, 7];
        var behavior = new ChunkBuilder().U16(0x8002).S16(2).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(2).U8(1).U8(0xff).Raw(Assign(3, 7, 5))
            .U16(2).U8(0xfe).U8(0xff).Raw(Assign(4, 9, 5));
        return ChunkBuilder.File(("OBJD", 1, definition.ToArray()), ("BHAV", 4096, behavior.ToArray()));
    }

    private static byte[] DefinitionWithWaitingRoute(uint guid)
    {
        var fields = new ushort[XObject.DefinitionCount];
        fields[(int)ObjdField.Guid1] = unchecked((ushort)guid);
        fields[(int)ObjdField.Guid2] = (ushort)(guid >> 16);
        fields[(int)ObjdField.Type] = 2;
        fields[(int)ObjdField.NumAttributes] = 3;
        var definition = new ChunkBuilder();
        foreach (var field in fields) definition.U16(field);
        // A resumed routing continuation evaluates to false when its transient route record is absent.
        var behavior = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(0x1b).U8(0xfe).U8(0xff).Raw(new byte[8]);
        return ChunkBuilder.File(("OBJD", 1, definition.ToArray()), ("BHAV", 4096, behavior.ToArray()));
    }

    private static byte[] DefinitionWithWaitingAnimation(uint guid)
    {
        var fields = new ushort[XObject.DefinitionCount];
        fields[(int)ObjdField.Guid1] = unchecked((ushort)guid);
        fields[(int)ObjdField.Guid2] = (ushort)(guid >> 16);
        fields[(int)ObjdField.Type] = 2;
        fields[(int)ObjdField.NumAttributes] = 3;
        var definition = new ChunkBuilder();
        foreach (var field in fields) definition.U16(field);
        var behavior = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(0x2c).U8(0xfe).U8(0xff).Raw([58, 0, 0, 0, 3, 8, 0, 0]);
        return ChunkBuilder.File(("OBJD", 1, definition.ToArray()), ("BHAV", 4096, behavior.ToArray()));
    }

    private static byte[] DefinitionWithCarryAnimation(uint guid)
    {
        var fields = new ushort[XObject.DefinitionCount];
        fields[(int)ObjdField.Guid1] = unchecked((ushort)guid);
        fields[(int)ObjdField.Guid2] = (ushort)(guid >> 16);
        fields[(int)ObjdField.Type] = 2;
        fields[(int)ObjdField.NumAttributes] = 3;
        var definition = new ChunkBuilder();
        foreach (var field in fields) definition.U16(field);
        static byte[] Assign(short temp, short value) =>
            [(byte)temp, (byte)(temp >> 8), (byte)value, (byte)(value >> 8), 0, 5, 8, 7];
        var behavior = new ChunkBuilder().U16(0x8002).S16(2).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(0x2c).U8(1).U8(0xff).Raw([78, 0, 0, 0, 3, 0x10, 0, 0])
            .U16(2).U8(0xfe).U8(0xff).Raw(Assign(3, 7));
        return ChunkBuilder.File(("OBJD", 1, definition.ToArray()), ("BHAV", 4096, behavior.ToArray()));
    }

    private static byte[] DefinitionWithMessageDialog(uint guid)
    {
        var fields = new ushort[XObject.DefinitionCount];
        fields[(int)ObjdField.Guid1] = unchecked((ushort)guid);
        fields[(int)ObjdField.Guid2] = (ushort)(guid >> 16);
        fields[(int)ObjdField.Type] = 0;
        fields[(int)ObjdField.NumAttributes] = 3;
        var definition = new ChunkBuilder();
        foreach (var field in fields) definition.U16(field);
        var behavior = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(0x24).U8(0xfe).U8(0xff).Raw([0, 0, 1, 0, 0, 0, 2, 0]);
        var strings = new ChunkBuilder().S16(2)
            .U8(13).Raw(System.Text.Encoding.Latin1.GetBytes("Saved message"))
            .U8(11).Raw(System.Text.Encoding.Latin1.GetBytes("Saved title"));
        return ChunkBuilder.File(("OBJD", 1, definition.ToArray()), ("BHAV", 4096, behavior.ToArray()),
            ("STR#", 301, strings.ToArray()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedSavedContinuationResumesOnceAfterObjMSaveAndReload(bool coded)
    {
        var game = Directory.CreateTempSubdirectory("native-queued-stack-roundtrip").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(game, "Objects")).FullName;
            File.WriteAllBytes(Path.Combine(folder, "queued.iff"), DefinitionWithQueuedTree(0x12345678));
            var globalTree = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
                .U16(0).U8(0xfe).U8(0xff).Raw(new byte[8]);
            var global = new IffFile(ChunkBuilder.File(("BHAV", 256, globalTree.ToArray())), "Global.iff");
            var registry = ObjectRegistry.Scan(game);
            var library = GlobalLibrary.Open(game);
            var lot = EmptyLot(coded);
            var world = SavedLotObjectLoader.Load(lot, registry, library);
            var owner = world.ObjectFactory!(0x12345678)!;
            world.Add(owner);
            var stackObject = world.ObjectFactory!(0x12345678)!;
            world.Add(stackObject);
            var codeOwner = world.SavedObjectTypes.Keys.Single();
            owner.SavedStack.Add(new ObjectStackFrame(0, ushort.MaxValue, 0, [], [], 0, 0));
            owner.SavedStack.Add(new ObjectStackFrame(stackObject.Id, 4096, 1, [6, 7], [8, 9], 0x1234, codeOwner));
            owner.SavedStackSize = 40;
            world.SavedExecutionStacks.Add(owner.Id, TreeRunner.RestoreSavedStack(world, owner,
                _ => new BehaviorScope(global, registry.Find(0x12345678)!.File, null)));
            world.ClockManager.SetTickEnabled(owner, true);
            world.ClockManager.SetWaitTicksAt(owner.ClockManagerIndex, -1);
            var (withFlags, _) = world.WriteSavedObjectManagerStateTo(lot);
            var (withStack, _) = world.WriteSavedLotObjectsTo(withFlags);

            var savedTypes = ObjectTypeTable.Read(withStack, withStack.OfType(ResourceType.Parse("objt")).Single());
            var savedObjects = LotObjects.Read(withStack, withStack.OfType(ResourceType.Parse("ObjM")).Single(),
                savedTypes, guid => registry.Find(guid)?.Definition);
            var restored = SavedLotObjectLoader.Restore(savedObjects.Objects, savedTypes.Entries,
                guid => registry.Find(guid), entry => new BehaviorScope(global, entry.File, null),
                objectManagerVersion: 0x3e);
            var restoredOwner = restored.ObjectById(owner.Id)!;
            var frames = restored.SavedExecutionStacks[owner.Id].Frames;
            Assert.Equal(2, frames.Count);
            var frame = frames[^1];
            Assert.Equal(stackObject.Id, frame.StackObject);
            Assert.Equal((short)4096, frame.TreeId);
            Assert.Equal(1, frame.Instruction);
            Assert.Equal(new short[] { 6, 7 }, frame.Parameters);
            Assert.Equal(new short[] { 8, 9 }, frame.Locals);
            Assert.Equal(0x1234, frame.WaitState);
            Assert.Equal(codeOwner, frame.CodeOwner);
            Assert.NotNull(frame.Tree);

            Assert.Equal(1, restored.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));

            Assert.Empty(restored.ThreadErrors);
            Assert.Equal((short)9, restoredOwner.Temporaries[4]);
            Assert.False(restored.SavedExecutionStacks.ContainsKey(owner.Id));
            Assert.Empty(restoredOwner.SavedStack);
        }
        finally { Directory.Delete(game, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WaitingRouteContinuationReturnsFalseAfterObjMSaveAndReload(bool coded)
    {
        var game = Directory.CreateTempSubdirectory("native-route-stack-roundtrip").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(game, "Objects")).FullName;
            File.WriteAllBytes(Path.Combine(folder, "person.iff"), DefinitionWithWaitingRoute(0x12345678));
            var globalTree = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
                .U16(0).U8(0xfe).U8(0xff).Raw(new byte[8]);
            var global = new IffFile(ChunkBuilder.File(("BHAV", 256, globalTree.ToArray())), "Global.iff");
            var registry = ObjectRegistry.Scan(game);
            var library = GlobalLibrary.Open(game);
            var lot = EmptyLot(coded, 0x3e);
            var world = SavedLotObjectLoader.Load(lot, registry, library);
            var person = (XPerson)world.ObjectFactory!(unchecked((int)0x12345678))!;
            person.SavedCharacter = new PersonCharacter(0, 0, Enumerable.Repeat(("", ""), 5).ToArray(), [],
                ["Test", "", ""], 0, new float[9]);
            person.Position = new ObjectPosition(160, 320, 0);
            world.Add(person);
            var codeOwner = world.SavedObjectTypes.Keys.Single();
            person.SavedStack.Add(new ObjectStackFrame(0, ushort.MaxValue, 0, [], [], 0, 0));
            person.SavedStack.Add(new ObjectStackFrame(person.Id, 4096, 0, [], [], 0x08, codeOwner));
            person.SavedStackSize = 40;
            world.SavedExecutionStacks.Add(person.Id, TreeRunner.RestoreSavedStack(world, person,
                _ => new BehaviorScope(global, registry.Find(0x12345678)!.File, null)));
            world.ClockManager.SetTickEnabled(person, true);
            world.ClockManager.SetWaitTicksAt(person.ClockManagerIndex, -1);
            var (withFlags, _) = world.WriteSavedObjectManagerStateTo(lot);
            var (saved, _) = world.WriteSavedLotObjectsTo(withFlags);

            var savedTypes = ObjectTypeTable.Read(saved, saved.OfType(ResourceType.Parse("objt")).Single());
            var savedObjects = LotObjects.Read(saved, saved.OfType(ResourceType.Parse("ObjM")).Single(),
                savedTypes, guid => registry.Find(guid)?.Definition);
            var restored = SavedLotObjectLoader.Restore(savedObjects.Objects, savedTypes.Entries,
                guid => registry.Find(guid), entry => new BehaviorScope(global, entry.File, null),
                objectManagerVersion: 0x3e);
            var restoredPerson = Assert.IsType<XPerson>(restored.ObjectById(person.Id));
            Assert.Equal(new ObjectPosition(160, 320, 0), restoredPerson.Position);
            Assert.Null(restoredPerson.NativeRoute);
            Assert.Equal(2, restored.SavedExecutionStacks[person.Id].Frames.Count);
            Assert.True(restored.SavedExecutionStacks[person.Id].Top!.Waiting);

            Assert.Equal(1, restored.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));

            Assert.Empty(restored.ThreadErrors);
            Assert.Equal(new ObjectPosition(160, 320, 0), restoredPerson.Position);
            Assert.Equal((short)0, restoredPerson[ObjectData.ParentId]);
            Assert.Equal((short)0, restoredPerson[ObjectData.SlotNumber]);
            Assert.Equal((short)0, restoredPerson[ObjectData.SlotHierNumber]);
            Assert.False(restored.SavedExecutionStacks.ContainsKey(person.Id));
            Assert.Empty(restoredPerson.SavedStack);
        }
        finally { Directory.Delete(game, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeldObjectRelationshipSurvivesObjMSaveAndReload(bool coded)
    {
        var game = Directory.CreateTempSubdirectory("native-held-object-roundtrip").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(game, "Objects")).FullName;
            File.WriteAllBytes(Path.Combine(folder, "person.iff"), DefinitionWithWaitingRoute(0x12345678));
            File.WriteAllBytes(Path.Combine(folder, "held.iff"), Definition(0x87654321));
            var globalTree = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
                .U16(0).U8(0xfe).U8(0xff).Raw(new byte[8]);
            var global = new IffFile(ChunkBuilder.File(("BHAV", 256, globalTree.ToArray())), "Global.iff");
            var registry = ObjectRegistry.Scan(game);
            var library = GlobalLibrary.Open(game);
            var lot = EmptyLot(coded, 0x3e);
            var world = SavedLotObjectLoader.Load(lot, registry, library);
            var person = (XPerson)world.ObjectFactory!(0x12345678)!;
            person.SavedCharacter = new PersonCharacter(0, 0, Enumerable.Repeat(("", ""), 5).ToArray(), [],
                ["Test", "", ""], 0, new float[9]);
            person.Position = new ObjectPosition(160, 320, 0);
            person.Slots.AddRange([0, 0]);
            world.Add(person);
            var held = world.ObjectFactory!(unchecked((int)0x87654321))!;
            world.Add(held);

            Assert.True(world.PlaceCreatedObjectInHand(person, held));
            Assert.Equal((short)0, person.Slots[0]);
            Assert.Equal(held.ClockManagerIndex, person.Slots[1]);
            Assert.Equal(person.ClockManagerIndex, held[ObjectData.ContainerId]);
            Assert.Equal(person.ClockManagerIndex, held[ObjectData.ParentId]);
            Assert.Equal((short)0, held[ObjectData.SlotNumber]);
            Assert.Equal((short)1, held[ObjectData.SlotHierNumber]);
            Assert.Equal(person.Position, held.Position);

            var (withFlags, _) = world.WriteSavedObjectManagerStateTo(lot);
            var (saved, _) = world.WriteSavedLotObjectsTo(withFlags);
            var savedTypes = ObjectTypeTable.Read(saved, saved.OfType(ResourceType.Parse("objt")).Single());
            var savedObjects = LotObjects.Read(saved, saved.OfType(ResourceType.Parse("ObjM")).Single(),
                savedTypes, guid => registry.Find(guid)?.Definition);
            var restored = SavedLotObjectLoader.Restore(savedObjects.Objects, savedTypes.Entries,
                guid => registry.Find(guid), entry => new BehaviorScope(global, entry.File, null),
                objectManagerVersion: 0x3e);
            var restoredPerson = Assert.IsType<XPerson>(restored.ObjectById(person.Id));
            var restoredHeld = Assert.IsType<XObject>(restored.ObjectById(held.Id));

            // Object slot assignments are serialized into the lot manifest and restored before saved thread
            // continuations resume.
            Assert.Equal((short)0, restoredPerson.Slots[0]);
            Assert.Equal(restoredHeld.ClockManagerIndex, restoredPerson.Slots[1]);
            Assert.Equal(restoredPerson.ClockManagerIndex, restoredHeld[ObjectData.ContainerId]);
            Assert.Equal(restoredPerson.ClockManagerIndex, restoredHeld[ObjectData.ParentId]);
            Assert.Equal((short)0, restoredHeld[ObjectData.SlotNumber]);
            Assert.Equal((short)1, restoredHeld[ObjectData.SlotHierNumber]);
            Assert.Equal(restoredPerson.Position, restoredHeld.Position);
        }
        finally { Directory.Delete(game, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedInteractionActionSurvivesObjMSaveAndReload(bool coded)
    {
        var game = Directory.CreateTempSubdirectory("native-queued-interaction-roundtrip").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(game, "Objects")).FullName;
            File.WriteAllBytes(Path.Combine(folder, "person.iff"), DefinitionWithWaitingRoute(0x12345678));
            File.WriteAllBytes(Path.Combine(folder, "fridge.iff"), Definition(0x87654321));
            var globalTree = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
                .U16(0).U8(0xfe).U8(0xff).Raw(new byte[8]);
            var global = new IffFile(ChunkBuilder.File(("BHAV", 256, globalTree.ToArray())), "Global.iff");
            var registry = ObjectRegistry.Scan(game);
            var library = GlobalLibrary.Open(game);
            var lot = EmptyLot(coded, 0x3e);
            var world = SavedLotObjectLoader.Load(lot, registry, library);
            var person = Assert.IsType<XPerson>(world.ObjectFactory!(0x12345678)!);
            person.SavedCharacter = new PersonCharacter(0, 0, Enumerable.Repeat(("", ""), 5).ToArray(), [],
                ["Test", "", ""], 0, new float[9]);
            person.Position = new ObjectPosition(160, 320, 1);
            world.Add(person);
            var fridge = world.ObjectFactory!(unchecked((int)0x87654321))!;
            fridge.Position = new ObjectPosition(176, 320, 1);
            world.Add(fridge);
            var action = new ActionQueueSlot
            {
                Person = person,
                Owner = fridge,
                Secondary = fridge,
                RawDword10 = 3,
                Threshold = 50,
                ActionTree = 4113,
                Flags = InteractionTestEvaluator.Evaluated | InteractionTestEvaluator.Passed,
                Parameters = [4, 5, 6, 7],
            };
            Assert.True(world.EnqueueInteractionAction(person, action));
            var queuedBeforeSave = Assert.Single(person.WriteActionQueue());

            var (withFlags, _) = world.WriteSavedObjectManagerStateTo(lot);
            var (saved, _) = world.WriteSavedLotObjectsTo(withFlags);
            var savedTypes = ObjectTypeTable.Read(saved, saved.OfType(ResourceType.Parse("objt")).Single());
            var savedObjects = LotObjects.Read(saved, saved.OfType(ResourceType.Parse("ObjM")).Single(),
                savedTypes, guid => registry.Find(guid)?.Definition);
            var restored = SavedLotObjectLoader.Restore(savedObjects.Objects, savedTypes.Entries,
                guid => registry.Find(guid), entry => new BehaviorScope(global, entry.File, null),
                objectManagerVersion: 0x3e);
            var restoredPerson = Assert.IsType<XPerson>(restored.ObjectById(person.Id));
            var queued = Assert.Single(restoredPerson.WriteActionQueue());

            // Person serialization saves and restores the queued interaction action ring.
            Assert.Equal(queuedBeforeSave.Id, queued.Id);
            Assert.Equal(person.Id, queued.Person);
            Assert.Equal(fridge.Id, queued.Object0x08);
            Assert.Equal(fridge.Id, queued.Object0x0C);
            Assert.Equal(50, queued.Value0x1C);
            Assert.Equal((short)4113, queued.Value0x20);
            Assert.Equal(action.Flags, queued.Flags);
            Assert.Equal(new short[] { 4, 5, 6, 7 }, queued.Values0x14);
        }
        finally { Directory.Delete(game, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WaitingAnimationContinuationRestartsAfterObjMSaveAndReload(bool coded)
    {
        var game = Directory.CreateTempSubdirectory("native-animation-stack-roundtrip").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(game, "Objects")).FullName;
            File.WriteAllBytes(Path.Combine(folder, "person.iff"), DefinitionWithWaitingAnimation(0x12345678));
            var globalTree = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
                .U16(0).U8(0xfe).U8(0xff).Raw(new byte[8]);
            var global = new IffFile(ChunkBuilder.File(("BHAV", 256, globalTree.ToArray())), "Global.iff");
            var registry = ObjectRegistry.Scan(game);
            var library = GlobalLibrary.Open(game);
            var lot = EmptyLot(coded, 0x3e);
            var world = SavedLotObjectLoader.Load(lot, registry, library);
            var person = (XPerson)world.ObjectFactory!(0x12345678)!;
            person.SavedCharacter = new PersonCharacter(0, 0, Enumerable.Repeat(("", ""), 5).ToArray(), [],
                ["Test", "", ""], 0, new float[9]);
            person[PersonData.PersonAge] = 18;
            person.Position = new ObjectPosition(160, 320, 0);
            world.AvatarAnimationTable[(3, false, 58)] = new AvatarAnimationClip("test-animation", 12, []);
            world.Add(person);
            var codeOwner = world.SavedObjectTypes.Keys.Single();
            person.SavedStack.Add(new ObjectStackFrame(0, ushort.MaxValue, 0, [], [], 0, 0));
            person.SavedStack.Add(new ObjectStackFrame(person.Id, 4096, 0, [], [], 0, codeOwner));
            person.SavedStackSize = 40;
            world.SavedExecutionStacks.Add(person.Id, TreeRunner.RestoreSavedStack(world, person,
                _ => new BehaviorScope(global, registry.Find(0x12345678)!.File, null)));
            world.ClockManager.SetTickEnabled(person, true);
            world.ClockManager.SetWaitTicksAt(person.ClockManagerIndex, -1);

            Assert.Equal(1, world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));
            Assert.Empty(world.ThreadErrors);
            Assert.Equal("test-animation", person.AnimationPlayback!.Clip.Name);
            Assert.True(person.AnimationPlayback.Frame > 0);
            Assert.True(world.SavedExecutionStacks.ContainsKey(person.Id));
            Assert.Equal(0, world.SavedExecutionStacks[person.Id].Top!.Instruction);

            var (withFlags, _) = world.WriteSavedObjectManagerStateTo(lot);
            var (saved, _) = world.WriteSavedLotObjectsTo(withFlags);
            var savedTypes = ObjectTypeTable.Read(saved, saved.OfType(ResourceType.Parse("objt")).Single());
            var savedObjects = LotObjects.Read(saved, saved.OfType(ResourceType.Parse("ObjM")).Single(),
                savedTypes, guid => registry.Find(guid)?.Definition);
            var restored = SavedLotObjectLoader.Restore(savedObjects.Objects, savedTypes.Entries,
                guid => registry.Find(guid), entry => new BehaviorScope(global, entry.File, null),
                objectManagerVersion: 0x3e);
            var restoredPerson = Assert.IsType<XPerson>(restored.ObjectById(person.Id));
            restored.AvatarAnimationTable[(3, false, 58)] = new AvatarAnimationClip("test-animation", 12, []);
            Assert.Null(restoredPerson.AnimationPlayback);
            Assert.Equal(new ObjectPosition(160, 320, 0), restoredPerson.Position);
            Assert.Equal(2, restored.SavedExecutionStacks[person.Id].Frames.Count);
            Assert.Equal(0, restored.SavedExecutionStacks[person.Id].Top!.Instruction);

            Assert.Equal(1, restored.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));

            Assert.Empty(restored.ThreadErrors);
            Assert.Equal("test-animation", restoredPerson.AnimationPlayback!.Clip.Name);
            Assert.Equal(1.2f, restoredPerson.AnimationPlayback.Frame);
            Assert.True(restored.SavedExecutionStacks.ContainsKey(person.Id));
            Assert.Equal(0, restored.SavedExecutionStacks[person.Id].Top!.Instruction);
            Assert.Equal(new ObjectPosition(160, 320, 0), restoredPerson.Position);
            Assert.Equal((short)0, restoredPerson[ObjectData.ParentId]);
            Assert.Equal((short)0, restoredPerson[ObjectData.SlotNumber]);
            Assert.Equal((short)0, restoredPerson[ObjectData.SlotHierNumber]);
        }
        finally { Directory.Delete(game, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedCarryAnimationInstructionDoesNotRestoreTransientPlayback(bool coded)
    {
        var game = Directory.CreateTempSubdirectory("native-carry-animation-roundtrip").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(game, "Objects")).FullName;
            File.WriteAllBytes(Path.Combine(folder, "person.iff"), DefinitionWithCarryAnimation(0x12345678));
            var globalTree = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
                .U16(0).U8(0xfe).U8(0xff).Raw(new byte[8]);
            var global = new IffFile(ChunkBuilder.File(("BHAV", 256, globalTree.ToArray())), "Global.iff");
            var registry = ObjectRegistry.Scan(game);
            var library = GlobalLibrary.Open(game);
            var lot = EmptyLot(coded, 0x3e);
            var world = SavedLotObjectLoader.Load(lot, registry, library);
            var person = (XPerson)world.ObjectFactory!(0x12345678)!;
            person.SavedCharacter = new PersonCharacter(0, 0, Enumerable.Repeat(("", ""), 5).ToArray(), [],
                ["Test", "", ""], 0, new float[9]);
            person[PersonData.PersonAge] = 18;
            person.Position = new ObjectPosition(160, 320, 0);
            world.AvatarAnimationTable[(3, false, 78)] = new AvatarAnimationClip("test-carry", 12, []);
            world.Add(person);
            var codeOwner = world.SavedObjectTypes.Keys.Single();
            person.SavedStack.Add(new ObjectStackFrame(0, ushort.MaxValue, 0, [], [], 0, 0));
            person.SavedStack.Add(new ObjectStackFrame(person.Id, 4096, 0, [], [], 0, codeOwner));
            person.SavedStackSize = 40;
            world.SavedExecutionStacks.Add(person.Id, TreeRunner.RestoreSavedStack(world, person,
                _ => new BehaviorScope(global, registry.Find(0x12345678)!.File, null)));
            world.ClockManager.SetTickEnabled(person, true);
            world.ClockManager.SetWaitTicksAt(person.ClockManagerIndex, -1);

            Assert.Equal(1, world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));
            Assert.Empty(world.ThreadErrors);
            Assert.Equal("test-carry", person.CarryAnimationPlayback!.Clip.Name);
            Assert.True(person.CarryAnimationPlayback.Frame > 0);
            Assert.False(world.SavedExecutionStacks.ContainsKey(person.Id));
            Assert.Empty(person.SavedStack);

            var (withFlags, _) = world.WriteSavedObjectManagerStateTo(lot);
            var (saved, _) = world.WriteSavedLotObjectsTo(withFlags);
            var savedTypes = ObjectTypeTable.Read(saved, saved.OfType(ResourceType.Parse("objt")).Single());
            var savedObjects = LotObjects.Read(saved, saved.OfType(ResourceType.Parse("ObjM")).Single(),
                savedTypes, guid => registry.Find(guid)?.Definition);
            var restored = SavedLotObjectLoader.Restore(savedObjects.Objects, savedTypes.Entries,
                guid => registry.Find(guid), entry => new BehaviorScope(global, entry.File, null),
                objectManagerVersion: 0x3e);
            var restoredPerson = Assert.IsType<XPerson>(restored.ObjectById(person.Id));
            Assert.Null(restoredPerson.CarryAnimationPlayback);
            Assert.Empty(restoredPerson.SavedStack);
            Assert.False(restored.SavedExecutionStacks.ContainsKey(person.Id));
            Assert.Equal(new ObjectPosition(160, 320, 0), restoredPerson.Position);
            Assert.Equal((short)0, restoredPerson[ObjectData.ParentId]);
            Assert.Equal((short)0, restoredPerson[ObjectData.SlotNumber]);
            Assert.Equal((short)0, restoredPerson[ObjectData.SlotHierNumber]);

            Assert.Equal(1, restored.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));
            Assert.Empty(restored.ThreadErrors);
            Assert.Null(restoredPerson.CarryAnimationPlayback);
            Assert.Equal(new ObjectPosition(160, 320, 0), restoredPerson.Position);
        }
        finally { Directory.Delete(game, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingMessageDialogIsRecreatedAfterObjMSaveAndReload(bool coded)
    {
        var game = Directory.CreateTempSubdirectory("native-dialog-stack-roundtrip").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(game, "Objects")).FullName;
            File.WriteAllBytes(Path.Combine(folder, "dialog.iff"), DefinitionWithMessageDialog(0x12345678));
            var globalTree = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
                .U16(0).U8(0xfe).U8(0xff).Raw(new byte[8]);
            var global = new IffFile(ChunkBuilder.File(("BHAV", 256, globalTree.ToArray())), "Global.iff");
            var registry = ObjectRegistry.Scan(game);
            var library = GlobalLibrary.Open(game);
            var lot = EmptyLot(coded, 0x3e);
            var world = SavedLotObjectLoader.Load(lot, registry, library);
            var owner = world.ObjectFactory!(0x12345678)!;
            world.Add(owner);
            var codeOwner = world.SavedObjectTypes.Keys.Single();
            owner.SavedStack.Add(new ObjectStackFrame(0, ushort.MaxValue, 0, [], [], 0, 0));
            owner.SavedStack.Add(new ObjectStackFrame(owner.Id, 4096, 0, [], [], 0, codeOwner));
            owner.SavedStackSize = 40;
            world.SavedExecutionStacks.Add(owner.Id, TreeRunner.RestoreSavedStack(world, owner,
                _ => new BehaviorScope(global, registry.Find(0x12345678)!.File, null)));
            world.ClockManager.SetTickEnabled(owner, true);
            world.ClockManager.SetWaitTicksAt(owner.ClockManagerIndex, -1);

            Assert.Equal(1, world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));
            Assert.Empty(world.ThreadErrors);
            var firstDialog = Assert.Single(world.DialogRecords);
            Assert.Equal("Saved title", firstDialog.Title);
            Assert.Equal("Saved message", firstDialog.Message);
            Assert.True(world.SavedExecutionStacks.ContainsKey(owner.Id));
            Assert.Equal(0, world.SavedExecutionStacks[owner.Id].Top!.Instruction);

            var (withFlags, _) = world.WriteSavedObjectManagerStateTo(lot);
            var (saved, _) = world.WriteSavedLotObjectsTo(withFlags);
            var savedTypes = ObjectTypeTable.Read(saved, saved.OfType(ResourceType.Parse("objt")).Single());
            var savedObjects = LotObjects.Read(saved, saved.OfType(ResourceType.Parse("ObjM")).Single(),
                savedTypes, guid => registry.Find(guid)?.Definition);
            var restored = SavedLotObjectLoader.Restore(savedObjects.Objects, savedTypes.Entries,
                guid => registry.Find(guid), entry => new BehaviorScope(global, entry.File, null),
                objectManagerVersion: 0x3e);
            var restoredOwner = Assert.IsType<XObject>(restored.ObjectById(owner.Id));
            Assert.Empty(restored.DialogRecords);
            Assert.Equal(2, restored.SavedExecutionStacks[owner.Id].Frames.Count);
            Assert.Equal(0, restored.SavedExecutionStacks[owner.Id].Top!.Instruction);

            Assert.Equal(1, restored.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));

            Assert.Empty(restored.ThreadErrors);
            var recreated = Assert.Single(restored.DialogRecords);
            Assert.Equal("Saved title", recreated.Title);
            Assert.Equal("Saved message", recreated.Message);
            restored.RespondMessageDialog(recreated.Token, 0);
            Assert.Equal(1, restored.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));
            Assert.Empty(restored.ThreadErrors);
            Assert.Empty(restored.DialogRecords);
            Assert.False(restored.SavedExecutionStacks.ContainsKey(owner.Id));
            Assert.Empty(restoredOwner.SavedStack);
        }
        finally { Directory.Delete(game, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearingLiveSavedStacksSurvivesObjMSaveAndReload(bool coded)
    {
        var game = Directory.CreateTempSubdirectory("native-cleared-stack-save").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(game, "Objects")).FullName;
            File.WriteAllBytes(Path.Combine(folder, "one.iff"), Definition(0x12345678));
            File.WriteAllBytes(Path.Combine(folder, "person.iff"), Definition(0x23456789, type: 2));
            var registry = ObjectRegistry.Scan(game);
            var library = GlobalLibrary.Open(game);
            var lot = EmptyLot(coded, 0x3e);
            var world = SavedLotObjectLoader.Load(lot, registry, library);
            var owner = world.ObjectFactory!(0x12345678)!;
            world.Add(owner);
            var person = (XPerson)world.ObjectFactory!(0x23456789)!;
            person.SavedCharacter = new PersonCharacter(0, 0, Enumerable.Repeat(("", ""), 5).ToArray(), [],
                ["Test", "", ""], 0, new float[9]);
            world.Add(person);
            var (withObject, _) = world.WriteSavedLotObjectsTo(lot);

            var live = SavedLotObjectLoader.Load(withObject, registry, library);
            var liveOwner = live.ObjectById(owner.Id)!;
            var livePerson = live.ObjectById(person.Id)!;
            liveOwner.SavedStack.Add(new ObjectStackFrame(0, 4096, 0, [], [], 0, 0));
            liveOwner.SavedStackSize = 32;
            livePerson.SavedStack.Add(new ObjectStackFrame(0, 4096, 0, [], [], 0, 0));
            livePerson.SavedStackSize = 32;
            live.SavedExecutionStacks.Add(liveOwner.Id, TreeRunner.RestoreSavedStack(live, liveOwner, _ => null));
            live.SavedExecutionStacks.Add(livePerson.Id, TreeRunner.RestoreSavedStack(live, livePerson, _ => null));
            live.ClockManager.SetWaitTicksAt(liveOwner.ClockManagerIndex, 9);
            var (withFlags, writtenFlags) = live.WriteSavedObjectManagerStateTo(withObject);
            Assert.NotEmpty(writtenFlags);
            var (withFrame, _) = live.WriteSavedLotObjectsTo(withFlags);
            var framed = SavedLotObjectLoader.Load(withFrame, registry, library);
            Assert.Single(framed.ObjectById(owner.Id)!.SavedStack);
            Assert.Single(framed.ObjectById(person.Id)!.SavedStack);

            var restoredOwner = framed.ObjectById(owner.Id)!;
            restoredOwner.SavedStack.Clear();
            restoredOwner.SavedStackSize = 0;
            framed.SavedExecutionStacks.Remove(owner.Id);
            var restoredPerson = framed.ObjectById(person.Id)!;
            restoredPerson.SavedStack.Clear();
            restoredPerson.SavedStackSize = 0;
            framed.SavedExecutionStacks.Remove(person.Id);
            var (clearedFlags, _) = framed.WriteSavedObjectManagerStateTo(withFrame);
            var (withoutFrame, _) = framed.WriteSavedLotObjectsTo(clearedFlags);
            var cleared = SavedLotObjectLoader.Load(withoutFrame, registry, library);
            Assert.Empty(cleared.ObjectById(owner.Id)!.SavedStack);
            Assert.Equal(0, cleared.ObjectById(owner.Id)!.SavedStackSize);
            Assert.Empty(cleared.ObjectById(person.Id)!.SavedStack);
            Assert.Equal(0, cleared.ObjectById(person.Id)!.SavedStackSize);
            Assert.False(cleared.SavedExecutionStacks.ContainsKey(owner.Id));
        }
        finally { Directory.Delete(game, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewTypesObjectsDeletionAndIdReuseSurviveRepeatedSaves(bool coded)
    {
        var game = Directory.CreateTempSubdirectory("native-created-save").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(game, "Objects")).FullName;
            File.WriteAllBytes(Path.Combine(folder, "one.iff"), Definition(0x12345678));
            File.WriteAllBytes(Path.Combine(folder, "two.iff"), Definition(0x23456789));
            File.WriteAllBytes(Path.Combine(folder, "multi.iff"), Definition(0x34567890, 0, 1));
            File.WriteAllBytes(Path.Combine(folder, "portal.iff"), Definition(0x45678901, 8, 2));
            File.WriteAllBytes(Path.Combine(folder, "person.iff"), Definition(0x56789012, 2));
            var registry = ObjectRegistry.Scan(game);
            var library = GlobalLibrary.Open(game);
            var empty = EmptyLot(coded);
            var world = SavedLotObjectLoader.Load(empty, registry, library);
            var first = world.ObjectFactory!(0x12345678)!;
            world.Add(first); first.Attributes[1] = -123; first.Position = new(160, 320, 1);
            var (saved, _) = world.WriteSavedLotObjectsTo(empty);
            var restored = SavedLotObjectLoader.Load(saved, registry, library);
            Assert.Equal(-123, restored.ObjectById(first.Id)!.Attributes[1]);
            Assert.Equal(0x12345678, Assert.Single(ObjectTypeTable.Read(saved, saved.OfType(ResourceType.Parse("objt")).Single()).Entries).Field0x8C);
            restored.Remove(first.Id);
            var replacement = restored.ObjectFactory!(0x23456789)!;
            restored.Add(replacement);
            Assert.Equal(first.Id, replacement.Id);
            replacement.Position = new(400, 500, 2);
            replacement.Attributes[0] = 987;
            var multi = restored.ObjectFactory!(0x34567890)!; restored.Add(multi);
            var portal = restored.ObjectFactory!(0x45678901)!; restored.Add(portal);
            portal.MultiTileMasterId = multi.Id;
            var person = (XPerson)restored.ObjectFactory!(0x56789012)!;
            restored.Add(person);
            Assert.Throws<InvalidDataException>(() => restored.WriteSavedLotObjectsTo(saved));
            person.SavedCharacter = new PersonCharacter(0, 0, Enumerable.Repeat(("", ""), 5).ToArray(), [],
                ["Test", "", ""], 0, new float[9]);
            person.PersonData[7] = 123;
            person.Motives[2] = 42.5f;
            var (updated, _) = restored.WriteSavedLotObjectsTo(saved);
            var loaded = SavedLotObjectLoader.Load(updated, registry, library);
            Assert.Equal(4, loaded.Objects.Count);
            var loadedPerson = Assert.IsType<XPerson>(loaded.ObjectById(person.Id));
            Assert.Equal(123, loadedPerson.PersonData[7]);
            Assert.Equal(42.5f, loadedPerson.Motives[2]);
            Assert.Equal("Test", loadedPerson.SavedCharacter!.Names[0]);
            Assert.Equal(0x23456789, loaded.ObjectById(first.Id)!.TypeRecord!.Key);
            Assert.Equal(new ObjectPosition(400, 500, 2), loaded.ObjectById(first.Id)!.Position);
            Assert.Equal(987, loaded.ObjectById(first.Id)!.Attributes[0]);
            Assert.Equal(multi.Id, loaded.ObjectById(portal.Id)!.MultiTileMasterId);
            var removedMulti = loaded.ObjectById(multi.Id)!;
            loaded.Remove(multi.Id);
            loaded.Add(loaded.ObjectById(person.Id)!); // Re-registering an existing object retains its position in iteration order.
            loaded.Add(removedMulti); // Removing and re-adding an object places it at the end of iteration order.
            var expectedOrder = loaded.WalkOrder.Select(item => item.Id).ToArray();
            var (reordered, _) = loaded.WriteSavedLotObjectsTo(updated);
            Assert.Equal(expectedOrder, SavedLotObjectLoader.Load(reordered, registry, library).WalkOrder.Select(item => item.Id));
            loaded.Remove(first.Id);
            var (deleted, _) = loaded.WriteSavedLotObjectsTo(updated);
            var afterDelete = SavedLotObjectLoader.Load(deleted, registry, library);
            Assert.Null(afterDelete.ObjectById(first.Id));
            Assert.Equal(3, afterDelete.Objects.Count);
            foreach (var item in afterDelete.WalkOrder.ToArray()) afterDelete.Remove(item.Id);
            var (cleared, count) = afterDelete.WriteSavedLotObjectsTo(deleted);
            Assert.Equal(0, count);
            Assert.Empty(SavedLotObjectLoader.Load(cleared, registry, library).Objects);
        }
        finally { Directory.Delete(game, true); }
    }

    [Fact]
    public void TreeFingerprintCountsDistinctReachableVersionsIncludingRootAndStopsCycles()
    {
        static Bhav Tree(uint version, params ushort[] calls)
        {
            var builder = new ChunkBuilder().U16(0x8002).S16((short)calls.Length).U8(0).U8(0).U8(0).U8(0).U32(version);
            foreach (var opcode in calls) builder.U16(opcode).U8(254).U8(255).Raw(new byte[8]);
            var (file, chunk) = builder.InFile("BHAV"); return Bhav.Read(file, chunk);
        }
        var scope = new TreeTable((4096, Tree(2, 4097, 4097, 2)), (4097, Tree(3, 4096, 4098)));
        Assert.Equal((2u << 16) + 5, SavedLotObjectLoader.SavedTreeFingerprint(scope, 4096));
        Assert.Equal(0u, SavedLotObjectLoader.SavedTreeFingerprint(scope, 4098));
    }
}
