namespace SimsEngine.Simulation;

/// <summary>The raw year, month and day words read by Sims.exe FUN_00432396 when it recomputes an object's age fields.</summary>
public readonly record struct SimulationDate(short Year, short Month, short Day)
{
    /// <summary>FUN_0046cfd5's initial date, also used by FUN_0046d0cf for legacy SIMI versions below 6.</summary>
    public static SimulationDate Initial { get; } = new(1997, 6, 15);
}

/// <summary>Clock fields updated by Sims.exe FUN_0046d461. The manager-owned decision returned by
/// FUN_0046d66f is supplied by the caller because its full object-manager gate is not yet modeled.</summary>
public sealed class SimulationClock
{
    public int Ticks { get; private set; }
    public int Day { get; private set; }
    public int Hour { get; private set; }
    public int Minute { get; private set; }
    /// <summary>The short at clock +0x1c; Sims.exe adds two per advancing tick.</summary>
    public int Second { get; private set; }
    public int TimeOfDay => Hour < 6 ? 2 : Hour == 6 ? 3 : Hour < 18 ? 0 : Hour == 18 ? 1 : 2;

    /// <summary>Restores the SIMI hour, minute and second that FUN_0046d0cf copies into the date manager. The
    /// simulation clock otherwise starts at midnight for hand-built worlds.</summary>
    public void Restore(int hour, int minute, int second)
    {
        Hour = Math.Clamp(hour, 0, 23);
        Minute = Math.Clamp(minute, 0, 59);
        Second = Math.Clamp(second, 0, 59);
    }

    /// <returns>Which clock boundaries were crossed on this tick.</returns>
    public ClockTickResult Tick(bool advanceGameTime) => Tick(advanceGameTime, simulationTickCompleted: advanceGameTime);

    /// <summary>Advances the simulator's completed-pass counter independently from the calendar clock. Sims.exe
    /// increments cSimulator +0x98 after every successful object-manager pass, even when global time is held.</summary>
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
