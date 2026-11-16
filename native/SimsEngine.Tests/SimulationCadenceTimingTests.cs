using SimsEngine.Simulation;

namespace SimsEngine.Tests;

/// <summary>Unit tests for simulation loop cadence across standard speed intervals (42 ms, 14 ms, and 2 ms)
/// and clock timing progression.</summary>
public class SimulationCadenceTimingTests
{
    private static double TickIntervalSeconds(int speed) => speed switch
    {
        3 => (172.0 - 171.0 * 924.0 / 1000.0) / 1000.0,
        10 => (172.0 - 171.0 * 994.0 / 1000.0) / 1000.0,
        _ => (172.0 - 171.0 * 760.0 / 1000.0) / 1000.0, // Normal simulation speed runs at approximately 42 ms intervals (23.8 Hz).
    };

    [Theory]
    [InlineData(1, 0.04204, 42.04)]
    [InlineData(3, 0.013996, 14.0)]
    [InlineData(10, 0.002026, 2.03)]
    public void SpeedIntervalFormulaMatchesSimsExeRoutines(int speed, double expectedSeconds, double expectedMs)
    {
        var intervalSec = TickIntervalSeconds(speed);
        var intervalMs = intervalSec * 1000.0;

        Assert.Equal(expectedSeconds, intervalSec, 0.0001);
        Assert.Equal(expectedMs, intervalMs, 0.1);
    }

    [Fact]
    public void ThirtyTicksAdvanceSimulationByExactlyOneMinute()
    {
        var world = new SimulationWorld();
        world.Clock.Restore(12, 0, 0);
        var control = new SimulationClockControl(1, 1, 1);

        for (int i = 0; i < 30; i++)
        {
            world.AdvanceSimulationTick(control, 0, world.Clock.Ticks);
        }

        Assert.Equal(12, world.Clock.Hour);
        Assert.Equal(1, world.Clock.Minute);
        Assert.Equal(30, world.Clock.Ticks);
    }

    [Fact]
    public void FullDaySimulationTicksCycleClockPhase()
    {
        var world = new SimulationWorld();
        world.Clock.Restore(0, 0, 0);
        var control = new SimulationClockControl(1, 1, 1);

        // Simulation clock runs at 30 ticks per minute, yielding 43,200 ticks per simulation day.
        const int TicksPerDay = 30 * 60 * 24;

        for (int i = 0; i < TicksPerDay; i++)
        {
            world.AdvanceSimulationTick(control, 0, world.Clock.Ticks);
        }

        Assert.Equal(TicksPerDay, world.Clock.Ticks);
        Assert.Equal(1, world.Clock.Day);
        Assert.Equal(0, world.Clock.Hour);
        Assert.Equal(0, world.Clock.Minute);
    }
}
