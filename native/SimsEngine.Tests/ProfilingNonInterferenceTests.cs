using System.Diagnostics;
using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public class ProfilingNonInterferenceTests
{
    private static SimulationWorld CreateWorld(uint seed)
    {
        var world = new SimulationWorld
        {
            RandomState = seed,
            ObjectManagerRandomState = seed
        };
        world.Clock.Restore(10, 30, 0);
        return world;
    }

    [Fact]
    public void ProfilingDoesNotMutateRandomStream()
    {
        const uint Seed = 123456789;
        const int Ticks = 100;
        var control = new SimulationClockControl(1, 1, 1);

        var unprofiledWorld = CreateWorld(Seed);
        for (int i = 0; i < Ticks; i++)
        {
            unprofiledWorld.AdvanceSimulationTick(control, 0, unprofiledWorld.Clock.Ticks);
        }

        var profiledWorld = CreateWorld(Seed);
        var stopwatch = new Stopwatch();
        var tickTimes = new List<long>();

        for (int i = 0; i < Ticks; i++)
        {
            stopwatch.Restart();
            profiledWorld.AdvanceSimulationTick(control, 0, profiledWorld.Clock.Ticks);
            stopwatch.Stop();
            tickTimes.Add(stopwatch.ElapsedTicks);
        }

        Assert.Equal(unprofiledWorld.RandomState, profiledWorld.RandomState);
        Assert.Equal(unprofiledWorld.ObjectManagerRandomState, profiledWorld.ObjectManagerRandomState);
    }

    [Fact]
    public void ProfilingZeroBehaviorChanges()
    {
        const uint Seed = 987654321;
        const int Ticks = 200;
        var control = new SimulationClockControl(1, 1, 1);

        var worldA = CreateWorld(Seed);
        var worldB = CreateWorld(Seed);

        for (int i = 0; i < Ticks; i++)
        {
            worldA.AdvanceSimulationTick(control, 0, worldA.Clock.Ticks);
        }

        var elapsedList = new List<double>();
        for (int i = 0; i < Ticks; i++)
        {
            var start = Stopwatch.GetTimestamp();
            worldB.AdvanceSimulationTick(control, 0, worldB.Clock.Ticks);
            elapsedList.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        Assert.Equal(worldA.Clock.Ticks, worldB.Clock.Ticks);
        Assert.Equal(worldA.Clock.Hour, worldB.Clock.Hour);
        Assert.Equal(worldA.Clock.Minute, worldB.Clock.Minute);
        Assert.Equal(worldA.Clock.Day, worldB.Clock.Day);
        Assert.Equal(worldA.Clock.TimeOfDay, worldB.Clock.TimeOfDay);
        Assert.Equal(worldA.RandomState, worldB.RandomState);
        Assert.Equal(worldA.ObjectManagerRandomState, worldB.ObjectManagerRandomState);
    }
}
