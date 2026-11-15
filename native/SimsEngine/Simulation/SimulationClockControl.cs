namespace SimsEngine.Simulation;

/// <summary>The three lot fields read at the start of Sims.exe FUN_0046d66f. They are kept as raw shorts because
/// their gameplay names are not established. The object-manager walk in that function consumes this global state.</summary>
public readonly record struct SimulationClockControl(short Word24, short Word32, short Word36)
{
    /// <summary>FUN_0046d66f's bVar6: time is globally held when +0x32 is zero and either +0x36 or +0x24 is zero.</summary>
    public bool GlobalTimeHold => Word32 == 0 && (Word36 == 0 || Word24 == 0);
}
