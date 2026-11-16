using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public class HeadlessBatchRunnerTests
{
    private static SimulationWorld CreateInitialWorld()
    {
        var world = new SimulationWorld();
        world.Clock.Restore(8, 0, 0);
        return world;
    }

    [Theory]
    [InlineData(60)]
    [InlineData(300)]
    [InlineData(1800)]
    public void HeadlessBatchExecutionAdvancesExactTickCount(int ticksToRun)
    {
        var world = CreateInitialWorld();
        var initialTicks = world.Clock.Ticks;
        var control = new SimulationClockControl(1, 1, 1);

        for (int i = 0; i < ticksToRun; i++)
        {
            world.AdvanceSimulationTick(control, 0, world.Clock.Ticks);
        }

        Assert.Equal(initialTicks + ticksToRun, world.Clock.Ticks);
    }

    [Fact]
    public void HeadlessSimulationProducesDeterministicStateWithoutUI()
    {
        var worldA = CreateInitialWorld();
        var worldB = CreateInitialWorld();

        var control = new SimulationClockControl(1, 1, 1);
        const int Ticks = 150;

        for (int i = 0; i < Ticks; i++)
        {
            worldA.AdvanceSimulationTick(control, 0, worldA.Clock.Ticks);
            worldB.AdvanceSimulationTick(control, 0, worldB.Clock.Ticks);
        }

        Assert.Equal(worldA.Clock.Ticks, worldB.Clock.Ticks);
        Assert.Equal(worldA.Clock.Hour, worldB.Clock.Hour);
        Assert.Equal(worldA.Clock.Minute, worldB.Clock.Minute);
        Assert.Equal(worldA.Clock.Day, worldB.Clock.Day);
        Assert.Equal(worldA.Clock.TimeOfDay, worldB.Clock.TimeOfDay);
    }

    [Fact]
    public void HeadlessExecutionCompletesWithoutUIDelaysOrExceptions()
    {
        var world = CreateInitialWorld();
        var control = new SimulationClockControl(1, 1, 1);

        var exception = Record.Exception(() =>
        {
            for (int i = 0; i < 600; i++)
            {
                world.AdvanceSimulationTick(control, 0, world.Clock.Ticks);
            }
        });

        Assert.Null(exception);
        Assert.Equal(600, world.Clock.Ticks);
    }
}
