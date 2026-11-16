using SimsEngine.Simulation;
using SimsEngine.Formats;
using Interaction = SimsEngine.Formats.Interaction;
using Xunit;

namespace SimsEngine.Tests;

public sealed class GosubFoundActionPrimitiveTests
{
    private sealed class ProbeStack : TreeStack
    {
        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction) =>
            throw new NotSupportedException("The stack is inspected before running its pushed tree.");
    }

    private sealed class BehaviorTable(Bhav tree, short treeId = 8408) : ITreeTable
    {
        public Bhav? Find(short id) => id == treeId ? tree : null;
    }

    private sealed class MissingBehaviorTable : ITreeTable
    {
        public Bhav? Find(short id) => null;
    }

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    private static InteractionTable TargetTable()
    {
        var builder = new ChunkBuilder().S16(1).S16(6).U16(8408).U16(8348).S32(0).U32(0x100).S32(7)
            .S32(0).F32(0).S32(50).S32(-1);
        var (file, chunk) = builder.InFile("TTAB");
        return InteractionTable.Read(file, chunk);
    }

    private static InteractionTable PersonTable()
    {
        var builder = new ChunkBuilder().S16(1).S16(6).U16(55).U16(8348).S32(0).U32(0x100).S32(7)
            .S32(0).F32(0).S32(50).S32(-1);
        var (file, chunk) = builder.InFile("TTAB");
        return InteractionTable.Read(file, chunk);
    }

    private static TreeContext WithNativeStack(TreeContext context)
    {
        var scope = new BehaviorTable(ActionTree(), 55);
        var stack = new ProbeStack();
        Assert.True(stack.Push(scope, [], false, 55));
        stack.Top!.StackObject = 2;
        return context with { Frame = stack.Top, Stack = stack };
    }

    private static Bhav ActionTree()
    {
        var builder = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(1).U8(0).U8(0).U32(0)
            .U16(2).U8(BhavInstruction.ReturnTrue).U8(BhavInstruction.ReturnFalse)
            .Raw(PrimitiveTestWorld.Expression(0, 0, (byte)ExpressionOperator.EqualTo));
        var (file, chunk) = builder.InFile("BHAV");
        return Bhav.Read(file, chunk);
    }

    [Fact]
    public void FoundActionMapsTheTtabActionTreeFromStoredWordPlusTwo()
    {
        var source = new Interaction(8408, 8348, 0, 7, 0, 0, 50, 0, [], null);

        Assert.Equal(new FoundAction(7, 8408), FoundAction.FromTtab(source));
    }

    private static (TreeContext Context, XPerson Me, PrimitiveTestHost Host) World(short stackObject = 2,
        bool targetHasScope = true, bool targetTreeAvailable = true)
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: stackObject);
        me[ObjectData.TreeTableEntry] = 7;
        ITreeTable? targetScope = !targetHasScope ? null : targetTreeAvailable
            ? new BehaviorTable(ActionTree())
            : new MissingBehaviorTable();
        var target = context.World.Add(new XObject(2, PrimitiveTestWorld.Definition(0))
        { TypeRecord = new TypeRecord(200) { Interactions = TargetTable(), BehaviorScope = targetScope } });
        me.TypeRecord = new TypeRecord(100) { Interactions = PersonTable() };
        host.ObjectTable[2] = target;
        me.SetObjectTableEntry(2, target);
        return (context, me, host);
    }

    [Fact]
    public void TheKeyFindBestStoresAtWord72IsTheTreeTableEntryGoSubFoundActionReads()
    {
        var (_, me, _) = World();
        me.InteractionActionWord72 = 41;
        Assert.Equal((short)41, me[ObjectData.TreeTableEntry]);
        me[ObjectData.TreeTableEntry] = 9;
        Assert.Equal((short)9, me.InteractionActionWord72);
    }

    [Fact]
    public void ANoStackObjectIsNoStackObjectPresent()
    {
        var (context, _, host) = World(stackObject: 0);
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => GosubFoundActionPrimitive.Execute(context)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void AnEmptyObjectTableEntryAnswersFalse()
    {
        var (context, me, host) = World();
        me.SetObjectTableEntry(2, null);
        Assert.Equal(PrimitiveResult.False, GosubFoundActionPrimitive.Execute(context));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void WithoutATargetFoundActionTableTheAnswerIsFalse()
    {
        var (context, me, host) = World();
        var target = Assert.IsType<XObject>(context.World.Objects[2]);
        target.TypeRecord = new TypeRecord(200) { BehaviorScope = new BehaviorTable(ActionTree()) };
        Assert.Equal(PrimitiveResult.False, GosubFoundActionPrimitive.Execute(context));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void AKeyWithNoEntryRaisesTheMissingTreeTableEntry()
    {
        var (context, me, host) = World();
        me[ObjectData.TreeTableEntry] = 8;
        Assert.Equal(SimulationError.ReferenceToTreeTableEntryThatDoesNotExist, Fails(() => GosubFoundActionPrimitive.Execute(context)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ARefusedActionYields()
    {
        var (context, me, host) = World(targetHasScope: false);
        var target = Assert.IsType<XObject>(context.World.Objects[2]);
        context = WithNativeStack(context);
        Assert.Equal(PrimitiveResult.Yield, GosubFoundActionPrimitive.Execute(context));
        Assert.Empty(host.Calls);
        Assert.Equal((short)0, target[ObjectData.UseCount]);
        Assert.Empty(me.PendingActionRequests);
        Assert.Empty(me.ActionQueue.Take(me.ActionQueueEnd));
        Assert.Equal("target has no hydrated behavior scope", Assert.Single(context.World.FoundActionPushFailures).Reason);
    }

    [Fact]
    public void ActionTreeAndKeyComeFromTheTargetsTtabEvenWhenThePersonsRowConflicts()
    {
        var (context, _, host) = World();
        context = WithNativeStack(context);
        Assert.Equal(PrimitiveResult.Again, GosubFoundActionPrimitive.Execute(context));
        Assert.Empty(host.Calls);
        Assert.Equal((short)8408, context.Stack!.Frames[1].TreeId);
        var person = Assert.IsType<XPerson>(context.World.Objects[100]);
        Assert.Empty(person.ActionQueue.Take(person.ActionQueueEnd));
        Assert.Equal(2, person.ActivePriority);
        Assert.Equal(7, person.CurrentActionRecord!.RawDword10);
        Assert.Equal((ushort)8408, person.CurrentActionRecord.ActionTree);
        Assert.Equal(1, person.CurrentActionRecord.Flags & 1);
    }

    [Fact]
    public void RefusedNativeTreePushDoesNotCreateActionOwnershipOrQueueState()
    {
        var (context, me, host) = World(targetTreeAvailable: false);
        var target = Assert.IsType<XObject>(context.World.Objects[2]);
        context = WithNativeStack(context);

        Assert.Equal(PrimitiveResult.Yield, GosubFoundActionPrimitive.Execute(context));
        Assert.Empty(host.Calls);
        Assert.Equal((short)0, target[ObjectData.UseCount]);
        Assert.Empty(me.PendingActionRequests);
        Assert.Empty(me.ActionQueue.Take(me.ActionQueueEnd));
        Assert.Equal("target behavior scope has no enterable action tree", Assert.Single(context.World.FoundActionPushFailures).Reason);
    }

    [Fact]
    public void NativePersonVirtualPushesActionAndTracksItsUseCount()
    {
        var (context, me, host) = World();
        var target = Assert.IsType<XObject>(context.World.Objects[2]);
        target.RequestKey = 55;
        me.SetObjectTableEntry(55, target);
        var scope = new BehaviorTable(ActionTree());
        target.TypeRecord = new TypeRecord(200) { Interactions = TargetTable(), BehaviorScope = scope };
        me.Temporaries[0] = 77;
        var stack = new ProbeStack();
        stack.DefaultParameters = me.Temporaries;
        Assert.True(stack.Push(new BehaviorTable(ActionTree(), 55), [], false, 55));
        stack.Top!.StackObject = 2;
        var nativeContext = context with { Frame = stack.Top!, Stack = stack };

        Assert.Equal(PrimitiveResult.Again, GosubFoundActionPrimitive.Execute(nativeContext));
        Assert.Equal(2, stack.Frames.Count);
        Assert.Equal((short)0, stack.Frames[1].Parameters[0]);
        Assert.Equal((short)55, stack.Frames[1].StackObject);
        Assert.Equal(2, me.PendingActionRequests.Single().Priority);
        Assert.Same(target, me.PendingActionRequests.Single().Target);
        Assert.Equal((short)1, target[ObjectData.UseCount]);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void NativeObjectTableWinsOverConflictingHostEntry()
    {
        var (context, _, host) = World();
        context = WithNativeStack(context);
        host.ObjectTable[2] = new XObject(99, PrimitiveTestWorld.Definition());

        Assert.Equal(PrimitiveResult.Again, GosubFoundActionPrimitive.Execute(context));
        Assert.Empty(host.Calls);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("ObjectTableEntry", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("QueueAction", StringComparison.Ordinal));
    }
}
