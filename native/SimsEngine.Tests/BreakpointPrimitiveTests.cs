using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class BreakpointPrimitiveTests
{
    private static short[] Definition(short type = 0)
    {
        var definition = new short[XObject.DefinitionCount];
        definition[9] = type;
        return definition;
    }

    private static (TreeContext Context, XPerson Me) World()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, Definition(XPerson.PersonType), attributes: 4));
        return (new TreeContext(world, me, new StackFrame(0, [], [0, 0])), me);
    }

    private static byte[] Operand(short data, DataOwner owner) =>
        [(byte)data, (byte)(data >> 8), (byte)(short)owner, (byte)((short)owner >> 8), 0, 0, 0, 0];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Fact]
    public void ZeroLeavesTheBreakpointTrue()
    {
        var (context, _) = World();
        Assert.Equal(PrimitiveResult.True, BreakpointPrimitive.Execute(context, Operand(0, DataOwner.Literal)));
    }

    [Fact]
    public void NonzeroRaisesTheTreeBreakError()
    {
        var (context, _) = World();
        Assert.Equal(SimulationError.TreeBreakEncountered, Fails(() => BreakpointPrimitive.Execute(context, Operand(5, DataOwner.Literal))));
        Assert.Equal(SimulationError.TreeBreakEncountered, Fails(() => BreakpointPrimitive.Execute(context, Operand(-1, DataOwner.Literal))));
    }

    [Fact]
    public void ReadsTheVariableNamedByTheOwnerAndData()
    {
        var (context, me) = World();
        me.Temporaries[3] = 1;
        Assert.Equal(SimulationError.TreeBreakEncountered, Fails(() => BreakpointPrimitive.Execute(context, Operand(3, DataOwner.Temporary))));
        Assert.Equal(PrimitiveResult.True, BreakpointPrimitive.Execute(context, Operand(2, DataOwner.Temporary)));
    }

    [Fact]
    public void AnUnknownOwnerIsIllegal()
    {
        var (context, _) = World();
        Assert.Equal(SimulationError.IllegalOwnerFieldInDataReference, Fails(() => BreakpointPrimitive.Execute(context, Operand(0, (DataOwner)39))));
        Assert.Equal(SimulationError.IllegalOwnerFieldInDataReference, Fails(() => BreakpointPrimitive.Execute(context, Operand(0, (DataOwner)(-1)))));
    }
}
