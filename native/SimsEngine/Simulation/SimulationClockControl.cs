namespace SimsEngine.Simulation;

public readonly record struct SimulationClockControl(short Word24, short Word32, short Word36)
{
    /// <summary>Indicates whether simulation time is paused based on the lot's global clock control flags.</summary>
    public bool GlobalTimeHold => Word32 == 0 && (Word36 == 0 || Word24 == 0);
}
