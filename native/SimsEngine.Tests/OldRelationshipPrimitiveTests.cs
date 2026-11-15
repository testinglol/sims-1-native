using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class OldRelationshipPrimitiveTests
{
    private static short[] Definition(short type = 0)
    {
        var definition = new short[XObject.DefinitionCount];
        definition[9] = type;
        return definition;
    }

    // The running object is 1 and the stack object is 2; both are plain objects. The frame's parameters are given.
    private static (TreeContext Context, SimulationWorld World, XObject Me, XObject Stack) ObjectWorld(short[]? parameters = null)
    {
        var world = new SimulationWorld();
        var me = world.Add(new XObject(1, Definition()));
        var stack = world.Add(new XObject(2, Definition()));
        return (new TreeContext(world, me, new StackFrame(2, parameters ?? new short[] { 0, 0 }, [])), world, me, stack);
    }

    // The running person is 1 with neighbor record 1, the stack object is person 2 with neighbor record 2.
    private static (TreeContext Context, SimulationWorld World, XPerson Me) PersonWorld()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, Definition(XPerson.PersonType)));
        var stack = world.Add(new XPerson(2, Definition(XPerson.PersonType)));
        me[PersonData.NeighborId] = 1;
        stack[PersonData.NeighborId] = 2;
        world.Neighbors[1] = new NeighborState(1, Definition());
        world.Neighbors[2] = new NeighborState(2, Definition());
        return (new TreeContext(world, me, new StackFrame(2, new short[] { 0, 0 }, [])), world, me);
    }

    // Byte 0 is the write flag, byte 1 the value index (signed), byte 2 the parameter index (signed), byte 3 the mode
    // (signed) and byte 4 the flags: bit 0 forbids growing a missing slot, bit 1 uses the neighbor records.
    private static byte[] Operand(byte write = 0, sbyte index = 0, sbyte parameter = 0, sbyte mode = 0, byte flags = 0) =>
        [write, (byte)index, (byte)parameter, (byte)mode, flags, 0, 0, 0];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Fact]
    public void ReadsTheObjectRelationIntoTheParameter()
    {
        var (context, _, me, _) = ObjectWorld();
        me.Relationships.Resize(2, 2);
        me.Relationships.Set(2, 1, 55);
        Assert.Equal(PrimitiveResult.True, OldRelationshipPrimitive.Execute(context, Operand(index: 1)));
        Assert.Equal(55, context.Frame.Parameters[0]);
    }

    [Fact]
    public void WritesTheParameterIntoTheObjectRelation()
    {
        var (context, _, me, _) = ObjectWorld();
        context.Frame.Parameters[1] = 66;
        me.Relationships.Resize(2, 1);
        Assert.Equal(PrimitiveResult.True, OldRelationshipPrimitive.Execute(context, Operand(write: 1, parameter: 1)));
        Assert.Equal(66, me.Relationships.Get(2, 0));
    }

    [Fact]
    public void ModeOneReadsTheStackObjectsRelationToMe()
    {
        var (context, _, _, stack) = ObjectWorld();
        stack.Relationships.Resize(1, 1);
        stack.Relationships.Set(1, 0, 12);
        Assert.Equal(PrimitiveResult.True, OldRelationshipPrimitive.Execute(context, Operand(mode: 1)));
        Assert.Equal(12, context.Frame.Parameters[0]);
    }

    [Fact]
    public void ModeTwoNamesParameterZeroAsTheOtherObject()
    {
        var (context, world, _, stack) = ObjectWorld(parameters: new short[] { 3, 0 });
        world.Add(new XObject(3, Definition()));
        stack.Relationships.Resize(3, 1);
        stack.Relationships.Set(3, 0, 4);
        Assert.Equal(PrimitiveResult.True, OldRelationshipPrimitive.Execute(context, Operand(parameter: 1, mode: 2)));
        Assert.Equal(4, context.Frame.Parameters[1]);
    }

    [Fact]
    public void ModeThreeNamesParameterZeroAsTheFirstObject()
    {
        var (context, world, _, _) = ObjectWorld(parameters: new short[] { 3, 0 });
        var first = world.Add(new XObject(3, Definition()));
        first.Relationships.Resize(2, 1);
        first.Relationships.Set(2, 0, 6);
        Assert.Equal(PrimitiveResult.True, OldRelationshipPrimitive.Execute(context, Operand(parameter: 1, mode: 3)));
        Assert.Equal(6, context.Frame.Parameters[1]);
    }

    [Fact]
    public void AMissingSlotIsFalseWhenFlagBitZeroForbidsGrowing()
    {
        var (context, _, me, _) = ObjectWorld();
        context.Frame.Parameters[0] = 9;
        Assert.Equal(PrimitiveResult.False, OldRelationshipPrimitive.Execute(context, Operand(index: 2, flags: 1)));
        Assert.Equal(0, me.Relationships.Count(2));
        Assert.Equal(9, context.Frame.Parameters[0]);
    }

    [Fact]
    public void AMissingSlotGrowsWithZeros()
    {
        var (context, _, me, _) = ObjectWorld();
        context.Frame.Parameters[0] = 9;
        Assert.Equal(PrimitiveResult.True, OldRelationshipPrimitive.Execute(context, Operand(index: 1)));
        Assert.Equal(2, me.Relationships.Count(2));
        Assert.Equal(0, context.Frame.Parameters[0]);
    }

    [Fact]
    public void ParameterIndexMustBeInRange()
    {
        var (context, _, me, _) = ObjectWorld();
        me.Relationships.Resize(2, 1);
        Assert.Equal(SimulationError.StackNumberOutOfRange, Fails(() => OldRelationshipPrimitive.Execute(context, Operand(parameter: -1))));
        Assert.Equal(SimulationError.StackNumberOutOfRange, Fails(() => OldRelationshipPrimitive.Execute(context, Operand(parameter: 2))));
    }

    [Fact]
    public void AFrameWithoutParametersIsAnError()
    {
        var (context, _, me, _) = ObjectWorld(parameters: new short[0]);
        me.Relationships.Resize(2, 1);
        Assert.Equal(SimulationError.StackNumberOutOfRange, Fails(() => OldRelationshipPrimitive.Execute(context, Operand())));
    }

    [Fact]
    public void ANegativeValueIndexIsAnError()
    {
        var (context, _, _, _) = ObjectWorld();
        Assert.Equal(SimulationError.DataNumberOutOfRange, Fails(() => OldRelationshipPrimitive.Execute(context, Operand(index: -1))));
    }

    [Fact]
    public void AModeOutsideZeroToThreeIsAnError()
    {
        var (context, _, _, _) = ObjectWorld();
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => OldRelationshipPrimitive.Execute(context, Operand(mode: 4))));
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => OldRelationshipPrimitive.Execute(context, Operand(mode: -1))));
    }

    [Fact]
    public void ModeTwoWithoutParametersIsAnError()
    {
        var (context, _, _, _) = ObjectWorld(parameters: new short[0]);
        Assert.Equal(SimulationError.ReferenceToTreeParameterWhenNoParameterExists, Fails(() => OldRelationshipPrimitive.Execute(context, Operand(mode: 2))));
    }

    [Fact]
    public void RecordModesReadTheNeighborRecords()
    {
        var (context, world, me) = PersonWorld();
        world.Neighbors[1].Relationships.Resize(2, 1);
        world.Neighbors[1].Relationships.Set(2, 0, 21);
        Assert.Equal(PrimitiveResult.True, OldRelationshipPrimitive.Execute(context, Operand(flags: 2)));
        Assert.Equal(21, context.Frame.Parameters[0]);
        Assert.Equal(0, me.Relationships.Count(2));
    }

    [Fact]
    public void RecordWritesGoToTheNeighborRecord()
    {
        var (context, world, _) = PersonWorld();
        context.Frame.Parameters[0] = 12;
        world.Neighbors[1].Relationships.Resize(2, 1);
        Assert.Equal(PrimitiveResult.True, OldRelationshipPrimitive.Execute(context, Operand(write: 1, flags: 2)));
        Assert.Equal(12, world.Neighbors[1].Relationships.Get(2, 0));
    }

    [Fact]
    public void APersonPathWithoutANeighborRecordIsAnError()
    {
        var (context, world, _) = PersonWorld();
        world.Neighbors.Remove(1);
        Assert.Equal(SimulationError.NeighborNotFound, Fails(() => OldRelationshipPrimitive.Execute(context, Operand())));
    }

    [Fact]
    public void APersonReadSucceedsWhenTargetNeighborRecordIsMissing()
    {
        var (context, world, _) = PersonWorld();
        world.Neighbors[1].Relationships.Resize(2, 1);
        world.Neighbors[1].Relationships.Set(2, 0, 27);
        world.Neighbors.Remove(2);

        Assert.Equal(PrimitiveResult.True, OldRelationshipPrimitive.Execute(context, Operand()));
        Assert.Equal(27, context.Frame.Parameters[0]);
    }
}
