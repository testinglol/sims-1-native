using SimsEngine.Simulation;
using SimsEngine.Formats;
using SimsEngine.Content;
using Xunit;

namespace SimsEngine.Tests;

public sealed class PushInteractionPrimitiveTests
{
    private const short StackObjectId = 5;

    private static byte[] Operand(byte id = 1, byte index = 0, byte mode = 3, byte flags = 0, byte secondary = 0) =>
        [id, index, mode, flags, secondary, 0, 0, 0];

    private static (TreeContext Context, XPerson Me, PrimitiveTestHost Host) Lot(short[] parameters, short[] locals,
        bool withTable = true, int interactionIndex = 1, uint interactionFlags = 0)
    {
        var host = new PrimitiveTestHost();
        var world = new SimulationWorld { Host = host };
        var me = world.Add(new XPerson(100, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        var owner = world.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            TypeRecord = withTable ? new TypeRecord(2) { Interactions = Table(interactionIndex, interactionFlags) } : null,
        });
        var stackPerson = world.Add(new XPerson(StackObjectId, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        var secondary = world.Add(new XObject(6, PrimitiveTestWorld.Definition()));
        host.ObjectTable[1] = owner;
        host.ObjectTable[StackObjectId] = stackPerson;
        host.ObjectTable[6] = secondary;
        me.SetObjectTableEntry(1, owner);
        me.SetObjectTableEntry(StackObjectId, stackPerson);
        me.SetObjectTableEntry(6, secondary);
        var frame = new StackFrame(StackObjectId, parameters, locals);
        return (new TreeContext(world, me, frame), me, host);
    }

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Fact]
    public void AParameterOwnerIsLookedUpThenPushedAtModeThreeFifty()
    {
        var (context, _, host) = Lot([1], [0, 0]);
        Assert.Equal(PrimitiveResult.True, PushInteractionPrimitive.Execute(context, Operand()));
        Assert.Empty(host.Calls);
        var person = (XPerson)context.World.ObjectById(StackObjectId)!;
        var action = person.ActionQueue[0];
        Assert.NotNull(action);
        Assert.Equal(50, action!.Threshold);
        Assert.Equal((byte)1, action.InteractionId);
        Assert.Equal((short)2, action.Owner!.Id);
        Assert.Same(action.Owner, action.Secondary);
        Assert.Equal(1, person.ActionQueueEnd);
    }

    [Fact]
    public void ALocalOwnerIsReadFromTheLocalsWhenFlagBitOneIsSet()
    {
        var (context, _, host) = Lot([9], [1, 0]);
        PushInteractionPrimitive.Execute(context, Operand(flags: 2));
        Assert.Empty(host.Calls);
        Assert.Equal(1, ((XPerson)context.World.ObjectById(StackObjectId)!).ActionQueueEnd);
    }

    [Theory]
    [InlineData(0, 7, 7)]
    [InlineData(1, 0, 100)]
    [InlineData(2, 0, 2)]
    [InlineData(3, 0, 50)]
    public void EachPriorityModeGivesItsPriority(byte mode, short activePriority, int expected)
    {
        var (context, me, host) = Lot([1], [0, 0]);
        var queuedPerson = (XPerson)context.World.ObjectById(StackObjectId)!;
        me.ActivePriority = activePriority;
        PushInteractionPrimitive.Execute(context, Operand(mode: mode));
        Assert.Empty(host.Calls);
        Assert.Equal(expected, queuedPerson.ActionQueue[0].Threshold);
    }

    [Fact]
    public void AnUnknownPriorityModeIsAnErrorBeforeThePush()
    {
        var (context, _, host) = Lot([1], [0, 0]);
        Assert.Equal(SimulationError.UnrecognizedPriorityValue, Fails(() => PushInteractionPrimitive.Execute(context, Operand(mode: 4))));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("PushInteraction"));
    }

    [Fact]
    public void TheSecondaryObjectAndRecordFlagsAreStoredOnAHeadContinuation()
    {
        var (context, _, host) = Lot([1], [0, 6]);
        PushInteractionPrimitive.Execute(context, Operand(flags: 0x0D, secondary: 1));
        var person = (XPerson)context.World.ObjectById(StackObjectId)!;
        Assert.Empty(host.Calls);
        Assert.Equal(1, person.ActionQueueEnd);
        Assert.Equal((short)6, person.ActionQueue[0].Secondary!.Id);
        Assert.Equal(0x42, person.ActionQueue[0].Flags);
    }

    [Fact]
    public void FlagBitThreeAloneSendsTheSixtyFourFlag()
    {
        var (context, _, host) = Lot([1], [0, 0]);
        PushInteractionPrimitive.Execute(context, Operand(flags: 8));
        Assert.Empty(host.Calls);
        Assert.Equal(0x40, ((XPerson)context.World.ObjectById(StackObjectId)!).ActionQueue[0].Flags);
    }

    [Fact]
    public void AnOutOfRangeParameterIsStackNumberError()
    {
        var (context, _, host) = Lot([1], [0, 0]);
        Assert.Equal(SimulationError.StackNumberOutOfRange, Fails(() => PushInteractionPrimitive.Execute(context, Operand(index: 1))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void AnOutOfRangeLocalOwnerIsLocalError()
    {
        var (context, _, host) = Lot([1], [0, 0]);
        Assert.Equal(SimulationError.LocalOutOfRange, Fails(() => PushInteractionPrimitive.Execute(context, Operand(index: 2, flags: 2))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void AMissingOwnerIsNotAnObjectReference()
    {
        var (context, _, _) = Lot([7], [0, 0]);
        Assert.Equal(SimulationError.CouldNotFindObjectReference, Fails(() => PushInteractionPrimitive.Execute(context, Operand())));
    }

    [Fact]
    public void AnOwnerWithoutATableIsTreeTableNotFound()
    {
        var (context, _, _) = Lot([1], [0, 0], withTable: false);
        Assert.Equal(SimulationError.TreeTableNotFound, Fails(() => PushInteractionPrimitive.Execute(context, Operand())));
    }

    [Fact]
    public void AnInteractionNotInTheTableIsTheEntryError()
    {
        var (context, _, host) = Lot([1], [0, 0]);
        Assert.Equal(SimulationError.ReferenceToTreeTableEntryThatDoesNotExist, Fails(() => PushInteractionPrimitive.Execute(context, Operand(id: 4))));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("PushInteraction"));
    }

    [Fact]
    public void InteractionLookupUsesTheTtaIndexRatherThanTheTablesArrayPosition()
    {
        var (context, _, host) = Lot([1], [0, 0], interactionIndex: 4);

        Assert.Equal(PrimitiveResult.True, PushInteractionPrimitive.Execute(context, Operand(id: 4)));
        Assert.Empty(host.Calls);
        Assert.Equal((byte)4, ((XPerson)context.World.ObjectById(StackObjectId)!).ActionQueue[0].InteractionId);
    }

    [Fact]
    public void AMissingStackObjectIsNoStackObjectError()
    {
        var (context, me, host) = Lot([1], [0, 0]);
        me.SetObjectTableEntry(StackObjectId, null);
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => PushInteractionPrimitive.Execute(context, Operand())));
    }

    [Fact]
    public void AStackObjectThatIsNotAPersonIsAnError()
    {
        var (context, me, _) = Lot([1], [0, 0]);
        me.SetObjectTableEntry(StackObjectId, new XObject(StackObjectId, PrimitiveTestWorld.Definition()));
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, Fails(() => PushInteractionPrimitive.Execute(context, Operand())));
    }

    [Fact]
    public void NativeObjectTableWinsOverConflictingHostLookup()
    {
        var (context, _, host) = Lot([1], [0, 0]);
        host.ObjectTable[1] = new XObject(99, PrimitiveTestWorld.Definition());
        host.ObjectTable[StackObjectId] = new XPerson(98, PrimitiveTestWorld.Definition(XPerson.PersonType));

        Assert.Equal(PrimitiveResult.True, PushInteractionPrimitive.Execute(context, Operand()));
        Assert.Empty(host.Calls);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("ObjectTableEntry", StringComparison.Ordinal));
    }

    [Fact]
    public void ASecondaryLocalOutOfRangeIsLocalError()
    {
        var (context, _, host) = Lot([1], [0, 0]);
        Assert.Equal(SimulationError.LocalOutOfRange, Fails(() => PushInteractionPrimitive.Execute(context, Operand(flags: 1, secondary: 2))));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("PushInteraction"));
    }

    [Fact]
    public void RunImmediatelyInteractionUsesTheNativeGuardAndBehaviorPath()
    {
        var (context, _, host) = Lot([1], [0, 0], interactionFlags: 4);
        var actionTree = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(2).U8(BhavInstruction.ReturnTrue).U8(BhavInstruction.ReturnFalse)
            .Raw(PrimitiveTestWorld.Expression(0, 0, (byte)ExpressionOperator.EqualTo))
            .InFile("BHAV", 4100);
        var scope = new BehaviorScope(actionTree.File, actionTree.File, null);
        var owner = context.World.ObjectById(2)!;
        owner.TypeRecord = new TypeRecord(2)
        {
            Interactions = Table(1, 4),
            BehaviorScope = scope,
        };
        context.World.CaptureExecutionTrace = true;
        var actor = (XPerson)context.World.ObjectById(StackObjectId)!;
        actor.ActivePriority = 17;

        Assert.Equal(PrimitiveResult.True, PushInteractionPrimitive.Execute(context, Operand()));

        Assert.Empty(host.Calls);
        Assert.Equal(0, actor.ActionQueueEnd);
        Assert.Equal(17, actor.ActivePriority);
        Assert.Contains(context.World.ExecutionTrace, trace => trace.Tree == 4100 && trace.Opcode == 2);
    }

    [Fact]
    public void RunImmediatelyInteractionRejectsAFalseGuardWithoutRunningItsActionTree()
    {
        var (context, _, _) = Lot([1], [0, 0], interactionFlags: 4);
        var actionBody = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(2).U8(BhavInstruction.ReturnTrue).U8(BhavInstruction.ReturnFalse)
            .Raw(PrimitiveTestWorld.Expression(0, 0, (byte)ExpressionOperator.EqualTo)).ToArray();
        var guardBody = new ChunkBuilder().U16(0x8000).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(2).U8(BhavInstruction.ReturnTrue).U8(BhavInstruction.ReturnFalse)
            .Raw(PrimitiveTestWorld.Expression(0, 0, (byte)ExpressionOperator.NotEqualTo))
            .ToArray();
        var file = new IffFile(ChunkBuilder.File(("BHAV", 4100, actionBody), ("BHAV", 4101, guardBody)), "immediate.iff");
        var scope = new BehaviorScope(file, file, null);
        var owner = context.World.ObjectById(2)!;
        owner.TypeRecord = new TypeRecord(2)
        {
            Interactions = Table(1, 4, guardTree: 4101),
            BehaviorScope = scope,
        };
        context.World.CaptureExecutionTrace = true;
        var actor = (XPerson)context.World.ObjectById(StackObjectId)!;
        Assert.Equal((ushort)4101, owner.TypeRecord.Interactions.Interactions.Single().GuardTree);
        Assert.NotNull(scope.Find(4101));

        Assert.Equal(PrimitiveResult.False, PushInteractionPrimitive.Execute(context, Operand()));

        Assert.Equal(0, actor.ActionQueueEnd);
        Assert.Contains(context.World.ExecutionTrace, trace => trace.Tree == 4101);
        Assert.DoesNotContain(context.World.ExecutionTrace, trace => trace.Tree == 4100);
    }

    [Fact]
    public void HigherPriorityInsertionDropsTheLowerPrioritySuffixInSourceOrder()
    {
        var (context, _, _) = Lot([1], [0, 0]);
        var person = (XPerson)context.World.ObjectById(StackObjectId)!;
        Assert.Equal(PrimitiveResult.True, PushInteractionPrimitive.Execute(context, Operand(mode: 3)));
        Assert.Equal(PrimitiveResult.True, PushInteractionPrimitive.Execute(context, Operand(mode: 2)));
        Assert.Equal(PrimitiveResult.True, PushInteractionPrimitive.Execute(context, Operand(mode: 1)));

        Assert.Equal(0, person.ActionQueueCurrent);
        Assert.Equal(1, person.ActionQueueEnd);
        Assert.Equal(100, person.ActionQueue[0].Threshold);
        Assert.Equal(new[] { 0, 1 }, context.World.ActionQueueRemovalNotices.Select(notice => notice.QueuePosition));
    }

    [Fact]
    public void EqualPriorityActionsRemainInArrivalOrder()
    {
        var (context, _, _) = Lot([1], [0, 0]);
        var person = (XPerson)context.World.ObjectById(StackObjectId)!;
        PushInteractionPrimitive.Execute(context, Operand(mode: 3));
        PushInteractionPrimitive.Execute(context, Operand(mode: 3));

        Assert.True(person.ActionQueue[0].Sequence < person.ActionQueue[1].Sequence);
        Assert.Equal(2, person.ActionQueueEnd);
    }

    [Fact]
    public void RequestedButMissingSecondaryObjectIsStoredAsNull()
    {
        var (context, _, _) = Lot([1], [0, 0]);
        PushInteractionPrimitive.Execute(context, Operand(flags: 1));
        var person = (XPerson)context.World.ObjectById(StackObjectId)!;
        Assert.Null(person.ActionQueue[0].Secondary);
    }

    [Fact]
    public void FullQueueRejectsAnActionThatWouldOnlyAppend()
    {
        var (context, _, _) = Lot([1], [0, 0]);
        var person = (XPerson)context.World.ObjectById(StackObjectId)!;
        for (var index = 0; index < 8; index++)
            Assert.Equal(PrimitiveResult.True, PushInteractionPrimitive.Execute(context, Operand(mode: 3)));

        Assert.Equal(PrimitiveResult.False, PushInteractionPrimitive.Execute(context, Operand(mode: 2)));
        Assert.Equal(8, person.ActionQueueEnd - person.ActionQueueCurrent);
    }

    [Fact]
    public void HeadContinuationPrecedesQueuedActionsAndRetainsTheirOrder()
    {
        var (context, _, host) = Lot([1], [0, 0]);
        var person = (XPerson)context.World.ObjectById(StackObjectId)!;
        Assert.Equal(PrimitiveResult.True, PushInteractionPrimitive.Execute(context, Operand(mode: 3)));
        Assert.Equal(PrimitiveResult.True, PushInteractionPrimitive.Execute(context, Operand(mode: 2, flags: 4)));

        Assert.Empty(host.Calls);
        Assert.Equal(2, person.ActionQueueEnd);
        Assert.Equal(2, person.ActionQueue[0].Threshold);
        Assert.Equal(2, person.ActionQueue[0].Flags & 2);
        Assert.Equal(50, person.ActionQueue[1].Threshold);
    }

    [Fact]
    public void FullHeadContinuationQueueKeepsTheFirstSevenExistingActions()
    {
        var (context, _, host) = Lot([1], [0, 0]);
        var person = (XPerson)context.World.ObjectById(StackObjectId)!;
        for (var index = 0; index < 8; index++)
            Assert.Equal(PrimitiveResult.True, PushInteractionPrimitive.Execute(context, Operand(mode: 3)));
        var oldSequences = Enumerable.Range(0, 7).Select(index => person.ActionQueue[index].Sequence).ToArray();

        Assert.Equal(PrimitiveResult.True, PushInteractionPrimitive.Execute(context, Operand(mode: 2, flags: 4)));

        Assert.Empty(host.Calls);
        Assert.Equal(8, person.ActionQueueEnd - person.ActionQueueCurrent);
        Assert.Equal(2, person.ActionQueue[0].Threshold);
        Assert.Equal(oldSequences, Enumerable.Range(1, 7).Select(index => person.ActionQueue[index].Sequence));
    }

    private static InteractionTable Table(int ttaIndex, uint interactionFlags = 0, ushort guardTree = 0)
    {
        var chunk = new ChunkBuilder().S16(1).S16(8)
            .U16(4100).U16(guardTree).S32(0).U32(interactionFlags).S32(ttaIndex).U32(0).F32(0).U32(0).S32(0);
        var (file, resource) = chunk.InFile("TTAB");
        return InteractionTable.Read(file, resource);
    }
}
