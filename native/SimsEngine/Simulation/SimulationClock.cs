namespace SimsEngine.Simulation;

public readonly record struct SimulationDate(short Year, short Month, short Day)
{
    /// <summary>Initial simulation calendar date (June 15, 1997) used for default worlds and legacy
    /// simulation state versions below 6.</summary>
    public static SimulationDate Initial { get; } = new(1997, 6, 15);
}

public sealed class SimulationClock
{
    public int Ticks { get; private set; }
    public int Day { get; private set; }
    public int Hour { get; private set; }
    public int Minute { get; private set; }
    /// <summary>Simulation seconds elapsed within the current minute, advancing by two seconds per tick.</summary>
    public int Second { get; private set; }
    public int TimeOfDay => Hour < 6 ? 2 : Hour == 6 ? 3 : Hour < 18 ? 0 : Hour == 18 ? 1 : 2;

    public void Restore(int hour, int minute, int second)
    {
        Hour = Math.Clamp(hour, 0, 23);
        Minute = Math.Clamp(minute, 0, 59);
        Second = Math.Clamp(second, 0, 59);
    }

    public void RestoreProgress(int ticks, int day)
    {
        Ticks = Math.Max(0, ticks);
        Day = Math.Max(0, day);
    }

    public ClockTickResult Tick(bool advanceGameTime) => Tick(advanceGameTime, simulationTickCompleted: advanceGameTime);

    /// <summary>Advances the simulator's completed-pass counter after every object update pass even when
    /// global game time is paused.</summary>
    public ClockTickResult Tick(bool advanceGameTime, bool simulationTickCompleted)
    {
        if (simulationTickCompleted) Ticks++;
        if (!advanceGameTime) return default;
        Second += 2;
        var minuteChanged = Second > 59;
        if (!minuteChanged) return default;

        Minute++;
        Second -= 60;
        var hourChanged = Minute > 59;
        if (!hourChanged) return new ClockTickResult(true, false, false);

        Hour++;
        Minute -= 60;
        var dayChanged = Hour > 23;
        if (!dayChanged) return new ClockTickResult(true, true, false);

        Day++;
        Hour -= 24;
        return new ClockTickResult(true, true, true);
    }
}

public readonly record struct ClockTickResult(bool MinuteChanged, bool HourChanged, bool DayChanged);
