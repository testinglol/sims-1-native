using SimsEngine.Simulation;

namespace SimsEngine.Tests;

/// <summary>Unit tests for A14 simulation loop cadence, speed intervals (42/14/2 ms), and clock timing progression.</summary>
public class SimulationCadenceTimingTests
{
    private static double TickIntervalSeconds(int speed) => speed switch
    {
        3 => (172.0 - 171.0 * 924.0 / 1000.0) / 1000.0,  // Fast: ~14 ms
        10 => (172.0 - 171.0 * 994.0 / 1000.0) / 1000.0, // Ultra: ~2 ms
        _ => (172.0 - 171.0 * 760.0 / 1000.0) / 1000.0,  // Normal: ~42 ms (23.8 Hz)
    };

    [Theory]
    [InlineData(1, 0.04204, 42.04)] // Normal speed: 760
    [InlineData(3, 0.013996, 14.0)] // Fast speed: 924
    [InlineData(10, 0.002026, 2.03)] // Ultra speed: 994
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

        // 30 ticks/min * 60 min/hr * 24 hr/day = 43,200 ticks per day
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
