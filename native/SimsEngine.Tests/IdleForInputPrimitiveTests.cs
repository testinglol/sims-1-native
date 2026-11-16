using SimsEngine.Simulation;
using SimsEngine.Formats;
using Xunit;

namespace SimsEngine.Tests;

public sealed class IdleForInputPrimitiveTests
{
    // The primitive operand encodes the parameter index as a 16-bit integer at bytes 0-1 and the control
    // flag at bytes 2-3.
    private static byte[] Operand(int index, int flag) => [(byte)index, (byte)(index >> 8), (byte)flag, (byte)(flag >> 8), 0xEE, 0xEE, 0xEE, 0xEE];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    private static (TreeContext Context, XPerson Me, PrimitiveTestHost Host) Start(short[]? parameters = null)
    {
        var (context, me, host) = PrimitiveTestWorld.Create(parameters ?? [0]);
        me.IdleFloat7A8 = -100;
        return (context, me, host);
    }

    [Fact]
    public void AnIdlePersonWithNoQueuedActionSetsTheFlagAndAnswersTrue()
    {
        var (context, me, host) = Start();
        Assert.Equal(PrimitiveResult.True, IdleForInputPrimitive.Execute(context, Operand(0, 0)));
        Assert.Equal(XPerson.IdleForInputFlag, me.InputFlags);
        Assert.Equal(XPerson.IdleForInputFlag, unchecked((byte)me[ObjectData.Flags]));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void InputFlagSharesTheLowByteOfMyObjectDataFlagsAndPreservesItsHighByte()
    {
        var (context, me, _) = Start();
        me[ObjectData.Flags] = unchecked((short)0x1234);

        me.InputFlags |= XPerson.IdleForInputFlag;
        Assert.Equal(unchecked((short)0x1274), me[ObjectData.Flags]);
        Assert.Equal((byte)0x74, me.InputFlags);
        Assert.True(ExpressionPrimitive.Execute(context, DataOwner.My, (short)ObjectData.Flags,
            ExpressionOperator.IsFlagSet, DataOwner.Literal, 7));

        me.InputFlags &= unchecked((byte)~XPerson.IdleForInputFlag);
        Assert.Equal(unchecked((short)0x1234), me[ObjectData.Flags]);
        Assert.Equal((byte)0x34, me.InputFlags);
    }

    [Fact]
    public void IdlePredicateRunsWithoutAnySimulationHost()
    {
        var (context, me, _) = Start();
        context.World.Host = null;

        Assert.Equal(PrimitiveResult.True, IdleForInputPrimitive.Execute(context, Operand(0, 0)));
        Assert.Equal(XPerson.IdleForInputFlag, me.InputFlags);
    }

    [Fact]
    public void ANonInterruptiblePersonCountsTheParameterDownAndThenClearsTheFlag()
    {
        var (context, me, host) = Start([2]);
        me[PersonData.NonInterruptible] = 1;
        me.InputFlags = XPerson.IdleForInputFlag;

        Assert.Equal(PrimitiveResult.Stop, IdleForInputPrimitive.Execute(context, Operand(0, 0)));
        Assert.Equal(1, context.Frame.Parameters[0]);
        Assert.Equal(PrimitiveResult.Stop, IdleForInputPrimitive.Execute(context, Operand(0, 0)));
        Assert.Equal(0, context.Frame.Parameters[0]);
        // The idle input flag remains active during the countdown and clears once it reaches zero.
        Assert.Equal(XPerson.IdleForInputFlag, me.InputFlags);
        Assert.Equal(PrimitiveResult.True, IdleForInputPrimitive.Execute(context, Operand(0, 0)));
        Assert.Equal(0, me.InputFlags);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void AnInterruptingQueuedActionStartsAndTheFrameWaitsForIt()
    {
        var (context, me, host) = Start([5]);
        me.TypeRecord = new TypeRecord(0x1234) { FootprintMask = 0x1111 };
        me[PersonData.RouteFootprintMask] = 9;
        me[PersonData.FootprintExtension] = 2;
        me.MotiveChanges.Add(new MotiveChange(Motive.Energy, 0.25f, 100f));
        me.ActionQueueEnd = 1;
        me.ActionQueue[0].Threshold = 50;
        var target = context.World.Add(new XObject(9, PrimitiveTestWorld.Definition(0)));
        host.QueuedActionTarget = target;
        host.SetNonInterruptibleDuringQueuedActionStart = true;

        Assert.Equal(PrimitiveResult.Again, IdleForInputPrimitive.Execute(context, Operand(0, 1)));
        Assert.Equal(new[] { "StartQueuedAction(100)" }, host.Calls);
        Assert.Equal(0, context.Frame.Parameters[0]);
        Assert.Equal(1, context.Frame.WaitState);
        Assert.Equal(9, context.Frame.StackObject);
        Assert.Equal(1, me.ActionQueueCurrent);
        Assert.Equal(50, host.StartedAction?.Threshold);
        Assert.NotSame(me.ActionQueue[0], host.StartedAction);
        Assert.Equal((short)50, me.ActivePriority);
        Assert.Equal((short)0, me[PersonData.NonInterruptible]);
        Assert.Equal((short)0, me[PersonData.RouteFootprintMask]);
        Assert.Equal((short)0, me[PersonData.FootprintExtension]);
        Assert.Equal(new FootprintBounds(2, 2, -2, -2), me.FootprintBounds);
        Assert.Empty(me.MotiveChanges);
    }

    [Fact]
    public void NativeRunnerPushesQueuedActionTreeAndReleasesItsTargetWhenItReturns()
    {
        var (context, me, _) = Start([1]);
        me.Temporaries[0] = 1;
        var actionTree = PrimitiveTestWorld.Tree(0,
            (2, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse,
                PrimitiveTestWorld.Expression(0, 1, (byte)ExpressionOperator.EqualTo)));
        var actionScope = new TestTreeTable((4097, actionTree));
        var (tableFile, tableChunk) = new ChunkBuilder().S16(1).S16(6).U16(4097).U16(0).S32(0)
            .U32(0).S32(7).S32(0).F32(0).S32(0).S32(-1).InFile("TTAB");
        var target = context.World.Add(new XObject(9, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(0x1234)
            {
                BehaviorScope = actionScope,
                Interactions = InteractionTable.Read(tableFile, tableChunk),
            },
        });
        target.RequestKey = 77;
        me.ActionQueueEnd = 1;
        me.ActionQueue[0] = new ActionQueueSlot
        {
            Owner = target,
            InteractionId = 7,
            ActionTree = 4097,
            Threshold = 50,
        };
        var idleChunk = new ChunkBuilder().U16(0x8002).S16(2).U8(0).U8(1).U8(0).U8(0).U32(0)
            .U16(0x11).U8(1).U8(1).Raw(Operand(0, 1))
            .U16(2).U8(BhavInstruction.ReturnTrue).U8(BhavInstruction.ReturnFalse)
            .Raw(PrimitiveTestWorld.Expression(0, 1, (byte)ExpressionOperator.EqualTo)).InFile("BHAV");
        var idleTree = Bhav.Read(idleChunk.File, idleChunk.Chunk);

        Assert.True(TreeRunner.Run(context, idleTree));

        Assert.Empty(me.PendingActionRequests);
        Assert.Empty(me.SavedPersonObjectReferences);
        Assert.Empty(me.InteractingObjects);
        Assert.Equal((short)0, target[ObjectData.UseCount]);
        Assert.Equal((short)-1, me.CurrentActionRecord?.RawDword10);
    }

    private sealed class TestTreeTable(Dictionary<short, Bhav> trees) : ITreeTable
    {
        public TestTreeTable(params (short Id, Bhav Tree)[] entries) : this(entries.ToDictionary(entry => entry.Id, entry => entry.Tree)) { }
        public Bhav? Find(short treeId) => trees.GetValueOrDefault(treeId);
    }

    [Fact]
    public void AcceptedActionMovesThePreviousRecordThenReplacesIt()
    {
        var (context, me, host) = Start([5]);
        me.CurrentActionRecord = new ActionQueueSlot
        {
            Owner = context.World.Add(new XObject(8, PrimitiveTestWorld.Definition())),
            Threshold = 20,
            Flags = 0x61,
            Sequence = 4,
        };
        me.ActionFlagByte20 = 1;
        me.CurrentActionWord5D8 = 12;
        me.CurrentActionWord5DA = 34;
        me.ActionQueueEnd = 1;
        me.ActionQueue[0].Threshold = 50;
        me.ActionQueue[0].Sequence = 5;
        var target = context.World.Add(new XObject(9, PrimitiveTestWorld.Definition()));
        me.ActionQueue[0] = new ActionQueueSlot { Owner = target, Threshold = 50, Sequence = 5 };
        host.QueuedActionTarget = target;

        Assert.Equal(PrimitiveResult.Again, IdleForInputPrimitive.Execute(context, Operand(0, 1)));

        Assert.NotNull(me.PreviousActionRecord);
        Assert.Equal(20, me.PreviousActionRecord!.Threshold);
        Assert.Equal(0x61, me.PreviousActionRecord.Flags);
        Assert.Equal(4, me.PreviousActionRecord.Sequence);
        Assert.Equal(50, me.CurrentActionRecord!.Threshold);
        Assert.Equal(5, me.CurrentActionRecord.Sequence);
        Assert.Equal(0, me.CurrentActionWord5D8);
        Assert.Equal(0, me.CurrentActionWord5DA);
    }

    [Fact]
    public void BackedUpActionClearsFlagBitFiveWhenPersonByteTwentyIsZero()
    {
        var (context, me, host) = Start([5]);
        me.CurrentActionRecord = new ActionQueueSlot
        {
            Owner = context.World.Add(new XObject(8, PrimitiveTestWorld.Definition())),
            Flags = 0x60,
        };
        me.ActionQueueEnd = 1;
        me.ActionQueue[0].Threshold = 50;
        host.QueuedActionTarget = context.World.Add(new XObject(9, PrimitiveTestWorld.Definition()));

        Assert.Equal(PrimitiveResult.Again, IdleForInputPrimitive.Execute(context, Operand(0, 1)));

        Assert.Equal(0x40, me.PreviousActionRecord!.Flags);
    }

    [Fact]
    public void ARefusedOrMissingQueuedActionStopsAndLeavesTheFrameAlone()
    {
        var (context, me, host) = Start([5]);
        me.ActionQueueEnd = 1;
        me.ActionQueue[0].Threshold = 50;
        me.MotiveChanges.Add(new MotiveChange(Motive.Energy, 0.25f, 100f));
        host.QueuedActionTarget = null;

        Assert.Equal(PrimitiveResult.Stop, IdleForInputPrimitive.Execute(context, Operand(0, 1)));
        Assert.Equal(new[] { "StartQueuedAction(100)" }, host.Calls);
        Assert.Equal(5, context.Frame.Parameters[0]);
        Assert.Equal(0, context.Frame.WaitState);
        Assert.Equal(1, me.ActionQueueCurrent);
        Assert.Equal((short)0, me.ActivePriority);
        Assert.Single(me.MotiveChanges);
    }

    [Fact]
    public void AZeroFlagDoesNotStartTheQueuedAction()
    {
        var (context, me, host) = Start();
        me.ActionQueueEnd = 1;
        me.ActionQueue[0].Threshold = 50;

        Assert.Equal(PrimitiveResult.True, IdleForInputPrimitive.Execute(context, Operand(0, 0)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void AWaitingFrameStartsAQueuedActionOnlyWhenTheSlotHasFlagBitOne()
    {
        var (context, me, host) = Start();
        context.Frame.WaitState = 1;
        me.ActionQueueEnd = 1;
        me.ActionQueue[0].Threshold = 50;
        host.QueuedActionTarget = context.World.Add(new XObject(9, PrimitiveTestWorld.Definition(0)));

        Assert.Equal(PrimitiveResult.True, IdleForInputPrimitive.Execute(context, Operand(0, 1)));
        Assert.DoesNotContain("StartQueuedAction(100)", host.Calls);

        me.ActionQueue[0].Flags = 2;
        Assert.Equal(PrimitiveResult.Again, IdleForInputPrimitive.Execute(context, Operand(0, 1)));
        Assert.Contains("StartQueuedAction(100)", host.Calls);
        Assert.Equal(1, me.ActionQueueCurrent);
    }

    [Fact]
    public void QueueReadCopiesTheCurrentRingSlotAndWrapsTheIndex()
    {
        var (_, me, _) = Start();
        me.ActionQueueCurrent = 7;
        me.ActionQueueEnd = 8;
        me.ActionQueue[7].Threshold = 37;
        me.ActionQueue[7].Sequence = 12;

        Assert.True(SimulationWorld.TryDequeueInteractionAction(me, out var action));
        Assert.Equal(8, me.ActionQueueCurrent);
        Assert.Equal(37, action!.Threshold);
        Assert.Equal(12, action.Sequence);
        Assert.NotSame(me.ActionQueue[7], action);
        Assert.False(SimulationWorld.TryDequeueInteractionAction(me, out _));
    }

    [Fact]
    public void APriorityAtOrAboveTheThresholdDoesNotInterrupt()
    {
        var (context, me, host) = Start([1]);
        me.StateValue = 1;
        me.ActionQueueEnd = 1;
        me.ActionQueue[0].Threshold = 50;
        me[PersonData.Priority] = 50;

        Assert.Equal(PrimitiveResult.Stop, IdleForInputPrimitive.Execute(context, Operand(0, 0)));
        Assert.Empty(host.Calls);
        Assert.Equal(0, context.Frame.Parameters[0]);
    }

    [Fact]
    public void AStateOutsideClassOneIsNotIdleAndCountsDown()
    {
        var (context, me, host) = Start([1]);
        me.StateValue = 1;

        Assert.Equal(PrimitiveResult.Stop, IdleForInputPrimitive.Execute(context, Operand(0, 0)));
        Assert.Equal(0, context.Frame.Parameters[0]);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void AStateInManagerClassOneCanIdleWithoutAHost()
    {
        var (context, me, _) = Start();
        me.StateValue = 12;
        context.World.StateClasses.Add(new StateClassEntry(12, 1));
        context.World.Host = null;

        Assert.Equal(PrimitiveResult.True, IdleForInputPrimitive.Execute(context, Operand(0, 0)));
        Assert.Equal(XPerson.IdleForInputFlag, me.InputFlags);
    }

    [Fact]
    public void APersonTypeOfTwoOrMoreIsNotIdle()
    {
        var (context, me, host) = Start([1]);
        me[PersonData.PersonType] = 2;

        Assert.Equal(PrimitiveResult.Stop, IdleForInputPrimitive.Execute(context, Operand(0, 0)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void TheFloatsAndGenderDecideTheIdlePredicateWhenTheQueueIsEmpty()
    {
        var (context, me, _) = Start([2]);
        me.IdleFloat7A8 = 0;
        me.IdleFloat7B0 = 0;
        me.IdleFloat7A0 = 0;
        Assert.Equal(PrimitiveResult.Stop, IdleForInputPrimitive.Execute(context, Operand(0, 0)));

        me.IdleFloat7A0 = -100;
        Assert.Equal(PrimitiveResult.True, IdleForInputPrimitive.Execute(context, Operand(0, 0)));
        me[PersonData.Gender] = 2;
        Assert.Equal(PrimitiveResult.Stop, IdleForInputPrimitive.Execute(context, Operand(0, 0)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void AParameterIndexOutsideTheFrameIsStackNumberOutOfRange(int index)
    {
        var (context, _, host) = Start([0]);
        Assert.Equal(SimulationError.StackNumberOutOfRange, Fails(() => IdleForInputPrimitive.Execute(context, Operand(index, 0))));
        Assert.Empty(host.Calls);
    }
}
