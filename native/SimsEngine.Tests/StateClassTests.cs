using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class StateClassTests
{
    [Fact]
    public void ZeroStateAlwaysHasClassOne()
    {
        var world = new SimulationWorld();
        world.StateClasses.Add(new StateClassEntry(0, 7));

        Assert.Equal(1, world.StateClass(0));
    }

    [Fact]
    public void NonzeroStateReturnsItsFirstClassEntry()
    {
        var world = new SimulationWorld();
        world.StateClasses.Add(new StateClassEntry(12, 3));
        world.StateClasses.Add(new StateClassEntry(12, 4));

        Assert.Equal(3, world.StateClass(12));
    }

    [Fact]
    public void MissingStateHasClassZero()
    {
        var world = new SimulationWorld();

        Assert.Equal(0, world.StateClass(12));
    }
}
