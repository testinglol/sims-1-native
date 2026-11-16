using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class RelationshipPrimitiveTests
{
    private static short[] Definition(short type = 0)
    {
        var definition = new short[XObject.DefinitionCount];
        definition[9] = type;
        return definition;
    }

    private static (TreeContext Context, SimulationWorld World, XObject Me, XObject Stack) ObjectWorld(short[]? locals = null)
    {
        var world = new SimulationWorld();
        var me = world.Add(new XObject(1, Definition()));
        var stack = world.Add(new XObject(2, Definition()));
        return (new TreeContext(world, me, new StackFrame(2, [], locals ?? new short[] { 0, 0 })), world, me, stack);
    }

    private static (TreeContext Context, SimulationWorld World, XPerson Me, XPerson Stack) PersonWorld()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, Definition(XPerson.PersonType)));
        var stack = world.Add(new XPerson(2, Definition(XPerson.PersonType)));
        me[PersonData.NeighborId] = 1;
        stack[PersonData.NeighborId] = 2;
        world.Neighbors[1] = new NeighborState(1, Definition());
        world.Neighbors[2] = new NeighborState(2, Definition());
        world.NeighborWalkOrder.AddRange([world.Neighbors[1], world.Neighbors[2]]);
        return (new TreeContext(world, me, new StackFrame(2, [], [0, 0])), world, me, stack);
    }

    private static byte[] Operand(byte index = 0, byte mode = 0, byte flags = 0, byte local = 0, DataOwner owner = DataOwner.Temporary, short data = 0) =>
        [index, mode, flags, local, (byte)owner, 0, (byte)data, (byte)(data >> 8)];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Fact]
    public void ReadsTheObjectRelationToTheStackObject()
    {
        var (context, _, me, _) = ObjectWorld();
        me.Relationships.Resize(2, 3);
        me.Relationships.Set(2, 1, 42);
        me.Temporaries[0] = 9;
        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(context, Operand(index: 1)));
        Assert.Equal(42, me.Temporaries[0]);
    }

    [Fact]
    public void ObjectListModeResolvesStackKeyThroughTheManagerVectorNotTheObjectStackTable()
    {
        var (context, world, me, stack) = ObjectWorld();
        me.SetObjectTableEntry(2, me);
        me.Relationships.Resize(me.Id, 1);
        me.Relationships.Resize(stack.Id, 1);
        me.Relationships.Set(me.Id, 0, 11);
        me.Relationships.Set(stack.Id, 0, 42);
        me.Temporaries[0] = 0;

        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(context, Operand()));

        Assert.Equal(42, me.Temporaries[0]);
        Assert.NotSame(me, world.ClockManager.ObjectAt(context.Frame.StackObject));
    }

    [Fact]
    public void ModeThreeChecksLocalRangeBeforeResolvingTheStackManagerIndex()
    {
        var (context, _, _, _) = ObjectWorld(locals: []);
        context.Frame.StackObject = 99;

        Assert.Equal(SimulationError.LocalOutOfRange,
            Fails(() => RelationshipPrimitive.Execute(context, Operand(mode: 3))));
    }

    [Fact]
    public void ModeOneReadsTheStackObjectsRelationToMe()
    {
        var (context, _, me, stack) = ObjectWorld();
        stack.Relationships.Resize(1, 1);
        stack.Relationships.Set(1, 0, 5);
        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(context, Operand(mode: 1)));
        Assert.Equal(5, me.Temporaries[0]);
    }

    [Fact]
    public void AMissingSlotIsFalseWhenFlagBitZeroForbidsGrowing()
    {
        var (context, _, me, _) = ObjectWorld();
        me.Temporaries[0] = 9;
        Assert.Equal(PrimitiveResult.False, RelationshipPrimitive.Execute(context, Operand(index: 2, flags: 1)));
        Assert.Equal(0, me.Relationships.Count(2));
        Assert.Equal(9, me.Temporaries[0]);
    }

    [Fact]
    public void AMissingSlotGrowsWithZeros()
    {
        var (context, _, me, _) = ObjectWorld();
        me.Temporaries[0] = 9;
        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(context, Operand(index: 2)));
        Assert.Equal(3, me.Relationships.Count(2));
        Assert.Equal(0, me.Relationships.Get(2, 2));
        Assert.Equal(0, me.Temporaries[0]);
    }

    [Fact]
    public void WritesTheVariableIntoTheRelationship()
    {
        var (context, _, me, _) = ObjectWorld();
        me.Temporaries[0] = 9;
        me.Relationships.Resize(2, 1);
        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(context, Operand(flags: 4)));
        Assert.Equal(9, me.Relationships.Get(2, 0));
    }

    [Fact]
    public void ReadingIntoAReadOnlyVariableIsRejected()
    {
        var (context, _, me, _) = ObjectWorld();
        me.Relationships.Resize(2, 1);
        Assert.Equal(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea, Fails(() => RelationshipPrimitive.Execute(context, Operand(owner: DataOwner.Literal, data: 3))));
    }

    [Fact]
    public void LocalModesNameTheOtherObject()
    {
        var (context, world, me, stack) = ObjectWorld(locals: new short[] { 3, 0 });
        world.Add(new XObject(3, Definition()));
        stack.Relationships.Resize(3, 1);
        stack.Relationships.Set(3, 0, 8);
        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(context, Operand(mode: 2, local: 0)));
        Assert.Equal(8, me.Temporaries[0]);
    }

    [Fact]
    public void ALocalOutOfRangeIsAnError()
    {
        var (context, _, _, _) = ObjectWorld();
        Assert.Equal(SimulationError.LocalOutOfRange, Fails(() => RelationshipPrimitive.Execute(context, Operand(mode: 2, local: 2))));
    }

    [Fact]
    public void ALocalNamingNoObjectIsAnError()
    {
        var (context, _, _, _) = ObjectWorld(locals: new short[] { 9, 0 });
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => RelationshipPrimitive.Execute(context, Operand(mode: 3, local: 0))));
    }

    [Fact]
    public void AModeOutsideZeroToThreeIsAnError()
    {
        var (context, _, _, _) = ObjectWorld();
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => RelationshipPrimitive.Execute(context, Operand(mode: 4))));
    }

    [Fact]
    public void AMissingStackObjectIsAnError()
    {
        var (_, world, me, _) = ObjectWorld();
        var context = new TreeContext(world, me, new StackFrame(9, [], [0, 0]));
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => RelationshipPrimitive.Execute(context, Operand(mode: 0))));
    }

    [Fact]
    public void TwoPersonsReadTheFirstNeighborsRecord()
    {
        var (context, world, me, _) = PersonWorld();
        world.Neighbors[1].Relationships.Resize(2, 1);
        world.Neighbors[1].Relationships.Set(2, 0, 33);
        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(context, Operand()));
        Assert.Equal(33, me.Temporaries[0]);
        Assert.Equal(0, me.Relationships.Count(2));
    }

    [Fact]
    public void APersonPathWithoutANeighborRecordIsAnError()
    {
        var (context, world, _, _) = PersonWorld();
        world.Neighbors.Remove(1);
        world.NeighborWalkOrder[0] = null;
        Assert.Equal(SimulationError.NeighborNotFound, Fails(() => RelationshipPrimitive.Execute(context, Operand())));
    }

    [Fact]
    public void APersonReadSucceedsWhenTargetNeighborRecordIsMissing()
    {
        var (context, world, me, _) = PersonWorld();
        world.Neighbors[1].Relationships.Resize(2, 1);
        world.Neighbors[1].Relationships.Set(2, 0, 19);
        world.Neighbors.Remove(2);
        world.NeighborWalkOrder[1] = null;

        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(context, Operand()));
        Assert.Equal(19, me.Temporaries[0]);
    }

    [Fact]
    public void RecordModesReadTheNeighborRecords()
    {
        var (context, world, me, _) = PersonWorld();
        world.Neighbors[1].Relationships.Resize(2, 1);
        world.Neighbors[1].Relationships.Set(2, 0, 21);
        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(context, Operand(flags: 2)));
        Assert.Equal(21, me.Temporaries[0]);
    }

    [Fact]
    public void RecordModesResolveOrderedManagerSlotsAndUseTheResolvedRecordId()
    {
        var (context, world, me, stack) = PersonWorld();
        var owner = new NeighborState(34, Definition());
        var target = new NeighborState(32, Definition());
        me[PersonData.NeighborId] = 1;
        world.NeighborWalkOrder[0] = owner;
        world.NeighborWalkOrder[1] = target;
        world.Neighbors[34] = owner;
        world.Neighbors[32] = target;
        owner.Relationships.Resize(32, 1);
        owner.Relationships.Set(32, 0, 71);

        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(context, Operand(flags: 2)));
        Assert.Equal(71, me.Temporaries[0]);
        Assert.Equal(0, owner.Relationships.Count(2));
    }

    [Fact]
    public void RecordModeMissingOrderedSlotRaisesMissingStackObjectError()
    {
        var (context, world, _, _) = PersonWorld();
        world.NeighborWalkOrder[1] = null;
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => RelationshipPrimitive.Execute(context, Operand(flags: 2))));
    }

    [Fact]
    public void RecordWritesGoToTheNeighborRecord()
    {
        var (context, world, me, _) = PersonWorld();
        me.Temporaries[0] = 12;
        world.Neighbors[1].Relationships.Resize(2, 1);
        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(context, Operand(flags: 6)));
        Assert.Equal(12, world.Neighbors[1].Relationships.Get(2, 0));
    }

    [Fact]
    public void RecordModesNeedAPersonAndANeighborRecord()
    {
        var (objectContext, _, _, _) = ObjectWorld();
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, Fails(() => RelationshipPrimitive.Execute(objectContext, Operand(flags: 2))));

        var (context, world, _, _) = PersonWorld();
        world.Neighbors.Remove(1);
        world.NeighborWalkOrder[0] = null;
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => RelationshipPrimitive.Execute(context, Operand(flags: 2))));
    }
}
