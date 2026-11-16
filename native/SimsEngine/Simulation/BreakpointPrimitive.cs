namespace SimsEngine.Simulation;

/// <summary>Implements the conditional Break Point primitive (opcode 0x0f). Reads data and owner from the
/// operand as a value; a nonzero evaluated value raises a tree break error, while a zero value succeeds.
/// </summary>
public static class BreakpointPrimitive
{
    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var data = (short)(operand[0] | operand[1] << 8);
        var owner = (DataOwner)(short)(operand[2] | operand[3] << 8);
        if (Variables.Get(context, owner, data) != 0) throw new SimulationException(SimulationError.TreeBreakEncountered);
        return PrimitiveResult.True;
    }
}
