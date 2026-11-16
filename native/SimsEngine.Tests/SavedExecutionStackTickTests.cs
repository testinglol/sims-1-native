using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class SavedExecutionStackTickTests
{
    [Fact]
    public void TypeOneObjectSlotInitializesRuntimeRecordFromSourceFieldsAndSlotTableIndex()
    {
        var slot = ObjectRuntimeSlot.FromObjectSlot(new Slot(1, 1.25f, -2.5f, 3.75f,
            0, 0, 0, 0, -1, 16, 16, 16, 0, 0, 0.1875f, 0, -2, 16), slotTableIndex: 4);

        Assert.Equal(1.25f, BitConverter.Int32BitsToSingle(slot.ReadWord(0x00)));
        Assert.Equal(-2.5f, BitConverter.Int32BitsToSingle(slot.ReadWord(0x04)));
        Assert.Equal(3.75f, BitConverter.Int32BitsToSingle(slot.ReadWord(0x08)));
        Assert.Equal(5, slot.ReadWord(0x0c));
        Assert.Equal(0, slot.ReadWord(0x10));
    }

    [Fact]
    public void SpriteSlotActivateForLoopsUsesTheFrameCountAndResetsPhase()
    {
        var slot = new ObjectRuntimeSlot();
        slot.WriteWord(0x1c, 4);
        slot.WriteWord(0x28, 9);

        slot.ActivateForLoops(3);

        Assert.Equal(144, slot.ReadWord(0x10));
        Assert.Equal(0, slot.ReadWord(0x28));
    }

    [Fact]
    public void ReverseSpriteSlotTicksEachFrameForTwelveCallbacksAndStopsAtItsLoopLimit()
    {
        var slot = new ObjectRuntimeSlot();
        slot.SetRendererSprite(42, frameCount: 3, backwards: true, balloonSpriteId: 0, labelSpriteId: 0, flag: false);
        slot.ActivateForLoops(1);

        for (var tick = 0; tick < 12; tick++)
            Assert.Equal(tick == 11, slot.Tick());
        Assert.Equal(1, slot.ReadWord(0x20));

        for (var tick = 0; tick < 12; tick++)
            Assert.Equal(tick == 11, slot.Tick());
        Assert.Equal(0, slot.ReadWord(0x20));

        for (var tick = 0; tick < 12; tick++)
            Assert.False(slot.Tick());
        Assert.Equal(0, slot.ReadWord(0x10));
        Assert.Equal(0, slot.ReadWord(0x20));
    }

    private sealed class Table(Bhav tree) : ITreeTable
    {
        public Bhav? Find(short id) => id == 4096 ? tree : null;
    }

    private sealed class ScriptedStack(PrimitiveResult result, Action? onRun = null) : TreeStack
    {
        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction)
        {
            onRun?.Invoke();
            return result;
        }
    }

    private sealed class ThrowingStack : TreeStack
    {
        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction) =>
            throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
    }

    [Fact]
    public void StoppedThreadDecrementsNonzeroLockoutCountWithoutChangingUseCount()
    {
        var (world, owner) = Setup(PrimitiveResult.Stop, lockoutCount: 2);
        owner[ObjectData.UseCount] = 7;
        world.ClockManager.SetTickEnabled(owner, enabled: true);

        var callbacks = world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0);

        Assert.Equal(1, callbacks);
        Assert.Equal((short)1, owner[ObjectData.LockoutCount]);
        Assert.Equal((short)7, owner[ObjectData.UseCount]);
    }

    [Fact]
    public void RestoredThreadPassesTheObjectsTemporariesToAllFfGosubs()
    {
        var world = new SimulationWorld();
        var owner = world.Add(new XObject(3, new short[XObject.DefinitionCount]));
        owner.Temporaries[0] = 450;
        short seen = -1;
        ScriptedStack? stack = null;
        stack = new ScriptedStack(PrimitiveResult.Stop, () => seen = stack!.DefaultParameters[0]);
        Assert.True(stack.Push(new Table(Tree()), [], false, 4096));
        world.SavedExecutionStacks.Add(owner.Id, stack);

        Assert.True(TreeRunner.RunRestoredObjectStack(world, owner));

        Assert.Equal((short)450, seen);
    }

    [Fact]
    public void RestoredThreadUsesTheSourceDefinedObjectCountStepBudget()
    {
        var (world, owner) = Setup(PrimitiveResult.Stop, lockoutCount: 0);
        world.Add(new XObject(4, new short[XObject.DefinitionCount]));
        world.Add(new XObject(5, new short[XObject.DefinitionCount]));
        var stack = world.SavedExecutionStacks[owner.Id];

        Assert.True(TreeRunner.RunRestoredObjectStack(world, owner));

        Assert.Equal(10_075, stack.StepLimit);
    }

    [Fact]
    public void RestoredThreadAddsTheSquaredEligibleNeighborCountToItsStepBudget()
    {
        var (world, owner) = Setup(PrimitiveResult.Stop, lockoutCount: 0);
        world.Add(new XObject(4, new short[XObject.DefinitionCount]));
        world.Add(new XObject(5, new short[XObject.DefinitionCount]));
        world.NeighborWalkOrder.Add(new NeighborState(1, []) { FriendEligibilityObjectFlagsB4 = 4 });
        world.NeighborWalkOrder.Add(new NeighborState(2, []) { FriendEligibilityObjectFlagsB4 = 0x24 });
        world.NeighborWalkOrder.Add(new NeighborState(3, []) { FriendEligibilityObjectFlagsB4 = 0x0c });
        world.NeighborWalkOrder.Add(new NeighborState(4, []));
        world.NeighborWalkOrder.Add(null);
        var stack = world.SavedExecutionStacks[owner.Id];

        Assert.True(TreeRunner.RunRestoredObjectStack(world, owner));

        // Execution step limits add 15 per active manager slot and 15 times the square of qualifying
        // neighbors.
        Assert.Equal(10_135, stack.StepLimit);
    }

    [Fact]
    public void YieldedThreadLeavesLockoutCountAndUseCountUnchanged()
    {
        var (world, owner) = Setup(PrimitiveResult.Yield, lockoutCount: 2);
        owner[ObjectData.UseCount] = 7;
        world.ClockManager.SetTickEnabled(owner, enabled: true);

        var callbacks = world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0);

        Assert.Equal(1, callbacks);
        Assert.Equal((short)2, owner[ObjectData.LockoutCount]);
        Assert.Equal((short)7, owner[ObjectData.UseCount]);
    }

    [Fact]
    public void StoppedThreadLeavesZeroLockoutCountAndUseCountAlone()
    {
        var (world, owner) = Setup(PrimitiveResult.Stop, lockoutCount: 0);
        owner[ObjectData.UseCount] = 7;

        world.ClockManager.SetTickEnabled(owner, enabled: true);
        Assert.Equal(1, world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));

        Assert.Equal((short)0, owner[ObjectData.LockoutCount]);
        Assert.Equal((short)7, owner[ObjectData.UseCount]);
    }

    [Fact]
    public void StoppedThreadDecrementsNegativeNonzeroLockoutCountLikeTheOriginal()
    {
        var (world, owner) = Setup(PrimitiveResult.Stop, lockoutCount: -1);

        world.ClockManager.SetTickEnabled(owner, enabled: true);
        Assert.Equal(1, world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));

        Assert.Equal((short)-2, owner[ObjectData.LockoutCount]);
        Assert.Equal((short)0, owner[ObjectData.UseCount]);
    }

    [Fact]
    public void StoppedThreadTicksRuntimeSlotsAndRequestsRefreshOnRollover()
    {
        var (world, owner) = Setup(PrimitiveResult.Stop, lockoutCount: 1);
        var slot = new ObjectRuntimeSlot();
        slot.WriteWord(0x10, 2);
        slot.WriteWord(0x1c, 10);
        slot.WriteWord(0x20, -1);
        slot.WriteWord(0x24, 2);
        slot.WriteWord(0x28, 11);
        owner.RuntimeSlots.Add(slot);
        world.ClockManager.SetTickEnabled(owner, enabled: true);

        Assert.Equal(1, world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));

        Assert.Equal(1, slot.ReadWord(0x10));
        Assert.Equal(1, slot.ReadWord(0x20));
        Assert.Equal(0, slot.ReadWord(0x28));
        Assert.True(world.DisplayRefreshRequested);
    }

    [Fact]
    public void ObjectWithoutSavedTreeStillTicksRuntimeSlotsOnItsSimulationCallback()
    {
        var world = new SimulationWorld();
        var owner = world.Add(new XObject(3, new short[XObject.DefinitionCount]));
        var slot = new ObjectRuntimeSlot();
        slot.WriteWord(0x10, 2);
        slot.WriteWord(0x1c, 10);
        slot.WriteWord(0x20, 4);
        slot.WriteWord(0x24, 2);
        slot.WriteWord(0x28, 0);
        owner.RuntimeSlots.Add(slot);
        world.ClockManager.SetTickEnabled(owner, enabled: true);

        Assert.Equal(1, world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));

        Assert.Equal(1, slot.ReadWord(0x10));
        Assert.Equal(1, slot.ReadWord(0x28));
    }

    [Fact]
    public void YieldedThreadDoesNotTickRuntimeSlots()
    {
        var (world, owner) = Setup(PrimitiveResult.Yield, lockoutCount: 1);
        var slot = new ObjectRuntimeSlot();
        slot.WriteWord(0x10, 2);
        slot.WriteWord(0x28, 11);
        owner.RuntimeSlots.Add(slot);
        world.ClockManager.SetTickEnabled(owner, enabled: true);

        Assert.Equal(1, world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));

        Assert.Equal(2, slot.ReadWord(0x10));
        Assert.Equal(11, slot.ReadWord(0x28));
        Assert.False(world.DisplayRefreshRequested);
    }

    [Fact]
    public void YieldedPersonThreadDoesNotApplyPostUpdateMotiveChanges()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(3, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        person[Motive.Energy] = 10f;
        person.MotiveChanges.Add(new MotiveChange(Motive.Energy, 1f, 20f));
        var stack = new ScriptedStack(PrimitiveResult.Yield);
        Assert.True(stack.Push(new Table(Tree()), [], false, 4096));
        world.SavedExecutionStacks.Add(person.Id, stack);
        world.ClockManager.SetTickEnabled(person, enabled: true);

        Assert.Equal(1, world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));

        Assert.Equal(10f, person[Motive.Energy]);
        Assert.Equal((short)0, person[PersonData.TickCounter]);
        Assert.Equal(0, world.Clock.Ticks);
    }

    [Theory]
    [InlineData(false, PrimitiveResult.Yield, 0f)]
    [InlineData(false, PrimitiveResult.Stop, 1.2f)]
    [InlineData(true, PrimitiveResult.Yield, 0f)]
    [InlineData(true, PrimitiveResult.Stop, 1.2f)]
    public void AvatarPlaybackAdvancesOnlyAfterThePersonUpdateCompletes(
        bool useClockManager, PrimitiveResult result, float expectedFrame)
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(3, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        var playback = new AvatarAnimationPlayback(new AvatarAnimationClip("walk", 10, []), backwards: false, loop: false);
        person.AnimationPlayback = playback;
        var stack = new ScriptedStack(result);
        Assert.True(stack.Push(new Table(Tree()), [], false, 4096));
        world.SavedExecutionStacks.Add(person.Id, stack);

        if (useClockManager)
        {
            world.ClockManager.SetTickEnabled(person, enabled: true);
            world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0);
        }
        else
        {
            world.AdvanceSimulationTick();
        }

        Assert.Equal(expectedFrame, playback.Frame);
    }

    [Fact]
    public void WorldTicksOnlyLiveSavedStacksInLotWalkOrder()
    {
        var world = new SimulationWorld();
        var visited = new List<short>();
        foreach (short id in new short[] { 8, 3, 14 }) world.Add(new XObject(id, new short[XObject.DefinitionCount]));
        foreach (short id in new short[] { 8, 14 })
        {
            var stack = new ScriptedStack(PrimitiveResult.Stop, () => visited.Add(id));
            Assert.True(stack.Push(new Table(Tree()), [], false, 4096));
            world.SavedExecutionStacks.Add(id, stack);
        }

        var ticked = world.TickSavedExecutionStacks();

        Assert.Equal(2, ticked);
        Assert.Equal(new short[] { 8, 14 }, visited);
    }

    [Fact]
    public void WorldTickUsesStartOfTickSnapshotAndSkipsObjectsRemovedMidPass()
    {
        var world = new SimulationWorld();
        var visited = new List<short>();
        var first = world.Add(new XObject(8, new short[XObject.DefinitionCount]));
        var removed = world.Add(new XObject(3, new short[XObject.DefinitionCount]));
        var replacement = new XObject(14, new short[XObject.DefinitionCount]);
        var didMutate = false;
        Attach(first, () =>
        {
            visited.Add(first.Id);
            if (didMutate) return;
            didMutate = true;
            world.Remove(removed.Id);
            world.Add(replacement);
            Attach(replacement, () => visited.Add(replacement.Id));
        });
        Attach(removed, () => visited.Add(removed.Id));

        Assert.Equal(1, world.TickSavedExecutionStacks());
        Assert.Equal(new short[] { 8 }, visited);
        Assert.Equal(2, world.TickSavedExecutionStacks());
        Assert.Equal(new short[] { 8, 8, 14 }, visited);

        void Attach(XObject owner, Action onRun)
        {
            var stack = new ScriptedStack(PrimitiveResult.Stop, onRun);
            Assert.True(stack.Push(new Table(Tree()), [], false, 4096));
            world.SavedExecutionStacks[owner.Id] = stack;
        }
    }

    [Fact]
    public void SimulationTickRunsObjectStacksBeforeAgingAsyncRequests()
    {
        var world = new SimulationWorld();
        var owner = world.Add(new XObject(8, new short[XObject.DefinitionCount]));
        // A live person matching the request key keeps the interaction request pending for the picker
        // dialog.
        world.Add(new XPerson(9, new short[XObject.DefinitionCount]) { RequestKey = 4 });
        var requestId = world.GenericSimsCallRequests.Start(owner, 4, GenericSimsCallRequestKind.First);
        short? ageSeenByStack = null;
        var stack = new ScriptedStack(PrimitiveResult.Stop, () =>
            ageSeenByStack = world.GenericSimsCallRequests.Snapshot().Single().Age);
        Assert.True(stack.Push(new Table(Tree()), [], false, 4096));
        world.SavedExecutionStacks.Add(owner.Id, stack);

        Assert.Equal(1, world.AdvanceSimulationTick());

        Assert.Equal((short)0, ageSeenByStack);
        Assert.Equal((short)1, Assert.Single(world.GenericSimsCallRequests.Snapshot()).Age);
        Assert.Null(world.GenericSimsCallRequests.Poll(owner));
        Assert.Equal(requestId, world.GenericSimsCallRequests.Snapshot().Single().Id);
    }

    [Fact]
    public void ClockManagerOverloadExecutesAnEligibleRestoredStackOnlyOnceBeforeRequestAging()
    {
        var world = new SimulationWorld();
        var owner = world.Add(new XObject(8, new short[XObject.DefinitionCount]));
        // A live person matching the request key keeps the interaction request pending for the picker
        // dialog.
        world.Add(new XPerson(9, new short[XObject.DefinitionCount]) { RequestKey = 4 });
        owner[ObjectData.LockoutCount] = 2;
        var requestId = world.GenericSimsCallRequests.Start(owner, 4, GenericSimsCallRequestKind.First);
        short? ageSeenByStack = null;
        var stack = new ScriptedStack(PrimitiveResult.Stop, () =>
            ageSeenByStack = world.GenericSimsCallRequests.Snapshot().Single().Age);
        Assert.True(stack.Push(new Table(Tree()), [], false, 4096));
        world.SavedExecutionStacks.Add(owner.Id, stack);
        world.ClockManager.Entries[owner.ClockManagerIndex - 1].Flags |= 0x00010000;

        Assert.Equal(1, world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));

        Assert.Equal((short)1, owner[ObjectData.LockoutCount]);
        Assert.Equal((short)0, ageSeenByStack);
        Assert.Equal((short)1, Assert.Single(world.GenericSimsCallRequests.Snapshot()).Age);
        Assert.Null(world.GenericSimsCallRequests.Poll(owner));
        Assert.Equal(requestId, world.GenericSimsCallRequests.Snapshot().Single().Id);
        Assert.Equal(1, world.Clock.Ticks);
    }

    [Fact]
    public void AScriptErrorEndsTheThreadAndStopsThePassBeforeTimeAdvances()
    {
        var world = new SimulationWorld();
        var owner = world.Add(new XObject(3, new short[XObject.DefinitionCount]));
        var stack = new ThrowingStack();
        Assert.True(stack.Push(new Table(Tree()), [], false, 4096));
        world.SavedExecutionStacks.Add(owner.Id, stack);
        owner.SavedStack.Add(new ObjectStackFrame(0, 4096, 0, [], [], 0, 0));
        owner.SavedStackSize = 32;
        world.ClockManager.SetTickEnabled(owner, true);

        Assert.Equal(1, world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));

        var error = Assert.Single(world.ThreadErrors);
        Assert.Equal((short)3, error.ObjectId);
        Assert.False(world.SavedExecutionStacks.ContainsKey(owner.Id));
        Assert.Empty(owner.SavedStack);
        Assert.Equal(0, owner.SavedStackSize);
        Assert.Equal((short)-1, world.ClockManager.WaitTicksAt(owner.ClockManagerIndex));
        Assert.Equal(0, world.Clock.Ticks);
    }

    [Fact]
    public void ScriptErrorResetMovesFlaggedObjectsOffLotAndSetsManagerWaitWord()
    {
        var world = new SimulationWorld();
        var owner = world.Add(new XObject(3, new short[XObject.DefinitionCount]) { Flags = 0x40 });
        world.SetObjectPosition(owner, new ObjectPosition(20 * 16, 20 * 16, 1));
        var stack = new ThrowingStack();
        Assert.True(stack.Push(new Table(Tree()), [], false, 4096));
        world.SavedExecutionStacks.Add(owner.Id, stack);
        owner.SavedStack.Add(new ObjectStackFrame(0, 4096, 0, [], [], 0, 0));
        owner.SavedStackSize = 32;
        world.ClockManager.SetTickEnabled(owner, true);

        Assert.Equal(1, world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));

        Assert.Equal(new ObjectPosition(-16, -16, 1), owner.Position);
        Assert.Equal((short)-5, owner.RoomWord84);
        Assert.Equal((short)-1, owner.RemovalCallbackWord14);
        Assert.False(world.SavedExecutionStacks.ContainsKey(owner.Id));
        Assert.Empty(owner.SavedStack);
        Assert.Equal(0, owner.SavedStackSize);
        Assert.Equal((short)-1, world.ClockManager.WaitTicksAt(owner.ClockManagerIndex));
        Assert.Equal(0, world.Clock.Ticks);
    }

    [Fact]
    public void ScriptErrorResetClearsInstanceStateAndRestoresAnAcceptedRootPlacement()
    {
        var world = new SimulationWorld();
        var owner = world.Add(new XObject(3, new short[XObject.DefinitionCount], attributes: 2)
        {
            Attributes = [17, -4],
            TypeAttributes = [9, 8],
            DynamicSpriteFlags = [3, 4],
        });
        var position = new ObjectPosition(20 * 16 + 8, 20 * 16 + 8, 1);
        world.SetObjectPosition(owner, position);
        owner.TypeRecord = new TypeRecord(123) { ObjectVersion = 0x5a5a };
        owner[ObjectData.UseCount] = 7;
        owner[ObjectData.LockoutCount] = 3;
        owner[ObjectData.Direction] = -1;
        owner[ObjectData.BirthMinute] = 11;
        owner[ObjectData.BirthHour] = 12;
        owner[ObjectData.Room] = 13;
        owner[ObjectData.CurrentValue] = 14;
        owner[ObjectData.BirthYear] = 15;
        owner[ObjectData.BirthMonth] = 16;
        owner[ObjectData.BirthDay] = 17;
        world.CurrentDate = new SimulationDate(18, 3, 8);
        owner[ObjectData.SlotCount] = 18;
        owner.Temporaries[0] = 22;
        var stack = new ThrowingStack();
        Assert.True(stack.Push(new Table(Tree()), [], false, 4096));
        world.SavedExecutionStacks.Add(owner.Id, stack);
        world.ClockManager.SetTickEnabled(owner, true);

        Assert.Equal(1, world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));

        Assert.Equal(position, owner.Position);
        Assert.Equal(owner.ClockManagerIndex, owner[ObjectData.ObjectId]);
        Assert.Equal((short)0x5a5a, owner[ObjectData.ObjectVersion]);
        Assert.Equal((short)0, owner[ObjectData.UseCount]);
        Assert.Equal((short)0, owner[ObjectData.LockoutCount]);
        Assert.Equal((short)7, owner[ObjectData.Direction]);
        Assert.Equal((short)11, owner[ObjectData.BirthMinute]);
        Assert.Equal((short)12, owner[ObjectData.BirthHour]);
        Assert.Equal((short)-5, owner[ObjectData.Room]);
        Assert.Equal((short)14, owner[ObjectData.CurrentValue]);
        Assert.Equal((short)15, owner[ObjectData.BirthYear]);
        Assert.Equal((short)16, owner[ObjectData.BirthMonth]);
        Assert.Equal((short)17, owner[ObjectData.BirthDay]);
        Assert.Equal((short)2, owner[ObjectData.SimAge]);
        Assert.Equal((short)22, owner[ObjectData.AgeInMonths]);
        Assert.Equal((short)18, owner[ObjectData.SlotCount]);
        Assert.Equal(new short[] { 0, 0 }, owner.Attributes);
        Assert.Equal(new short[] { 9, 8 }, owner.TypeAttributes);
        Assert.Equal(new short[] { 3, 4 }, owner.DynamicSpriteFlags);
        Assert.All(owner.Temporaries, value => Assert.Equal((short)0, value));
        Assert.Equal(owner.ClockManagerIndex, world.TileValueAt(position.X >> 4, position.Y >> 4, position.Height));
        Assert.Equal((short)-1, world.ClockManager.WaitTicksAt(owner.ClockManagerIndex));
    }

    [Fact]
    public void ScriptErrorResetQueuesTheObjectsSecondFunctionAndResumesItAfterSaveRestore()
    {
        var body = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(4).U8(0).U8(0).U32(0)
            .U16(2).U8(0xFE).U8(0xFF).Raw(ExpressionOperand(0, 0, 2));
        var (file, chunk) = body.InFile("BHAV", 8194);
        var scope = new BehaviorScope(file, file, file);
        var world = new SimulationWorld();
        world.CaptureExecutionTrace = true;
        world.CaptureExecutionTrace = true;
        // Multi-tile parts with a non-zero MasterId defer secondary initialization, keeping function 1
        // queued for the subsequent manager tick.
        var definition = new short[XObject.DefinitionCount];
        definition[(int)ObjdField.MasterId] = 1;
        var owner = world.Add(new XObject(3, definition)
        {
            TypeRecord = new TypeRecord(123) { FunctionTable = [0, 8194], BehaviorScope = scope },
        });
        Assert.Equal((ushort)8194, owner.TypeRecord!.FunctionTable![1]);
        Assert.Same(scope, owner.TypeRecord.BehaviorScope);
        Assert.NotNull(scope.Find(8194));
        world.CodeOwnerTypeGuids.Add(0, 123);
        world.CodeOwnerScopes.Add(0, scope);
        world.SetObjectPosition(owner, new ObjectPosition(20 * 16 + 8, 20 * 16 + 8, 1));
        var failed = new ThrowingStack();
        Assert.True(failed.Push(new Table(Tree()), [], false, 4096));
        world.SavedExecutionStacks.Add(owner.Id, failed);
        world.ClockManager.SetTickEnabled(owner, true);

        Assert.Equal(1, world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));

        Assert.Equal(2, world.SavedExecutionStacks[owner.Id].Frames.Count);
        var initializer = world.SavedExecutionStacks[owner.Id].Frames[^1];
        Assert.Equal((short)8194, initializer.TreeId);
        Assert.Equal(new short[] { 0, 0, 0, 0 }, initializer.Parameters);
        Assert.Equal((short)0, initializer.CodeOwner);
        Assert.Equal(40, owner.SavedStackSize);
        world.SavedExecutionStacks[owner.Id] = TreeRunner.RestoreSavedStack(world, owner,
            codeOwner => world.CodeOwnerScopes.GetValueOrDefault(codeOwner));

        Assert.Equal(1, world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));

        Assert.Contains(world.ExecutionTrace, entry => entry.Tree == 8194);
        Assert.DoesNotContain(owner.Id, world.SavedExecutionStacks.Keys);
        Assert.Empty(owner.SavedStack);
        Assert.Single(world.ThreadErrors);
    }

    [Fact]
    public void ScriptErrorResetRestoresTheParentSlotOfAContainedObject()
    {
        var world = new SimulationWorld();
        var parent = world.Add(new XObject(1, new short[XObject.DefinitionCount]));
        var position = new ObjectPosition(20 * 16 + 8, 20 * 16 + 8, 1);
        world.SetObjectPosition(parent, position);
        var owner = world.Add(new XObject(3, new short[XObject.DefinitionCount]));
        owner.Position = position;
        owner[ObjectData.ParentId] = parent.ClockManagerIndex;
        owner[ObjectData.ContainerId] = parent.ClockManagerIndex;
        // Hierarchy slot numbers are 1-based indices with entry 0 reserved as a sentinel, while root
        // containers use slot -1.
        parent[ObjectData.SlotHierNumber] = -1;
        owner[ObjectData.SlotHierNumber] = 1;
        owner[ObjectData.SlotNumber] = 1;
        parent.Slots.Add(0);
        parent.Slots.Add(owner.ClockManagerIndex);
        parent.FootprintSlots.Add(new ObjectFootprintSlot { OccupantObjectId = owner.ClockManagerIndex });
        var stack = new ThrowingStack();
        Assert.True(stack.Push(new Table(Tree()), [], false, 4096));
        world.SavedExecutionStacks.Add(owner.Id, stack);
        world.ClockManager.SetTickEnabled(owner, true);

        Assert.Equal(1, world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));

        Assert.Equal(position, owner.Position);
        Assert.Equal(parent.ClockManagerIndex, owner[ObjectData.ParentId]);
        Assert.Equal(parent.ClockManagerIndex, owner[ObjectData.ContainerId]);
        Assert.Equal((short)1, owner[ObjectData.SlotHierNumber]);
        Assert.Equal((short)1, owner[ObjectData.SlotNumber]);
        Assert.Equal(owner.ClockManagerIndex, parent.Slots[1]);
        Assert.Equal(owner.ClockManagerIndex, parent.FootprintSlots[0].OccupantObjectId);
    }

    [Fact]
    public void ScriptErrorResetClimbsHierarchySlotZeroLinksToTheRootAndDropsTheStaleLink()
    {
        // When resolving slot-0 links, traversal climbs to the first object with a non-zero slot;
        // encountering a root clears the container link and re-places the object uncontained.
        var world = new SimulationWorld();
        var root = world.Add(new XObject(1, new short[XObject.DefinitionCount]));
        var position = new ObjectPosition(20 * 16 + 8, 20 * 16 + 8, 1);
        world.SetObjectPosition(root, position);
        root[ObjectData.SlotHierNumber] = -1;
        var owner = world.Add(new XObject(3, new short[XObject.DefinitionCount]));
        owner.Position = position;
        owner[ObjectData.ParentId] = root.ClockManagerIndex;
        owner[ObjectData.SlotHierNumber] = 0;
        root.Slots.Add(owner.ClockManagerIndex);
        var stack = new ThrowingStack();
        Assert.True(stack.Push(new Table(Tree()), [], false, 4096));
        world.SavedExecutionStacks.Add(owner.Id, stack);
        world.ClockManager.SetTickEnabled(owner, true);

        Assert.Equal(1, world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0));

        Assert.Equal(position, owner.Position);
        Assert.Equal((short)0, root.Slots[0]);
    }

    [Fact]
    public void WorldObjectRegistrationBuildsOneBasedClockManagerSlotsAndReusesRemovedHoles()
    {
        var world = new SimulationWorld();
        var first = world.Add(new XObject(3, new short[XObject.DefinitionCount]));
        var second = world.Add(new XObject(1, new short[XObject.DefinitionCount]));

        Assert.Equal(3, first.ClockManagerIndex);
        Assert.Equal(1, second.ClockManagerIndex);
        Assert.Same(second, world.ClockManager.Entries[0].Object);
        Assert.Null(world.ClockManager.Entries[1].Object);
        Assert.Same(first, world.ClockManager.Entries[2].Object);
        Assert.All(world.ClockManager.Entries, entry => Assert.Equal(0x0000ffffu, entry.Flags));

        Assert.True(world.Remove(1));
        Assert.Equal((short)0, second.ClockManagerIndex);
        var replacement = world.Add(new XObject(0, new short[XObject.DefinitionCount]));

        Assert.Equal(1, replacement.ClockManagerIndex);
        Assert.Same(replacement, world.ClockManager.Entries[0].Object);
        Assert.Equal(3, world.ClockManager.Entries.Count);
    }

    private static (SimulationWorld World, XObject Owner) Setup(PrimitiveResult result, short lockoutCount)
    {
        var world = new SimulationWorld();
        var owner = world.Add(new XObject(3, new short[XObject.DefinitionCount]));
        owner[ObjectData.LockoutCount] = lockoutCount;
        var stack = new ScriptedStack(result);
        Assert.True(stack.Push(new Table(Tree()), [], false, 4096));
        world.SavedExecutionStacks.Add(owner.Id, stack);
        return (world, owner);
    }

    private static Bhav Tree()
    {
        var body = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(0).U8(0xFE).U8(0xFF).Raw(new byte[8]);
        var (file, chunk) = body.InFile("BHAV", 4096);
        return Bhav.Read(file, chunk);
    }

    private static byte[] ExpressionOperand(short lhs, short rhs, byte operation) =>
        [(byte)lhs, (byte)(lhs >> 8), (byte)rhs, (byte)(rhs >> 8), 0, operation, 8, 7];
}
