using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class SimulationClockTests
{
    [Fact]
    public void PausedTickDoesNotChangeClock()
    {
        var clock = new SimulationClock();

        var result = clock.Tick(advanceGameTime: false);

        Assert.Equal(default, result);
        Assert.Equal(0, clock.Ticks);
        Assert.Equal(0, clock.Second);
        Assert.Equal(0, clock.Minute);
    }

    [Fact]
    public void ThirtyAdvancingTicksMakeOneMinute()
    {
        var clock = new SimulationClock();
        for (var i = 0; i < 29; i++) Assert.False(clock.Tick(true).MinuteChanged);

        Assert.True(clock.Tick(true).MinuteChanged);
        Assert.Equal(30, clock.Ticks);
        Assert.Equal(1, clock.Minute);
        Assert.Equal(0, clock.Second);
    }

    [Fact]
    public void ClockRollsMinuteHourAndDayAndMatchesOriginalPhases()
    {
        var clock = new SimulationClock();
        ClockTickResult result = default;
        for (var i = 0; i < 30 * 60 * 24; i++) result = clock.Tick(true);

        Assert.True(result.MinuteChanged);
        Assert.True(result.HourChanged);
        Assert.True(result.DayChanged);
        Assert.Equal(1, clock.Day);
        Assert.Equal(0, clock.Hour);
        Assert.Equal(0, clock.Minute);
        Assert.Equal(2, clock.TimeOfDay);

        var phases = new[] { 2, 2, 2, 2, 2, 2, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 2, 2, 2, 2 };
        for (var hour = 0; hour < phases.Length; hour++)
        {
            var atHour = new SimulationClock();
            for (var i = 0; i < hour * 30 * 60; i++) atHour.Tick(true);
            Assert.Equal(phases[hour], atHour.TimeOfDay);
        }
    }

    [Theory]
    [InlineData(6, 3)]
    [InlineData(7, 0)]
    [InlineData(18, 1)]
    [InlineData(19, 2)]
    public void HourBoundaryReportsTheOriginalPhase(int hour, int phase)
    {
        var clock = new SimulationClock();
        ClockTickResult result = default;
        for (var i = 0; i < hour * 30 * 60; i++) result = clock.Tick(true);

        Assert.True(result.HourChanged);
        Assert.Equal(phase, clock.TimeOfDay);
    }
}
