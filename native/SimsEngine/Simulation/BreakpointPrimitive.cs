namespace SimsEngine.Simulation;

/// <summary>The Break Point primitive (opcode 0x0f), after Sims.exe FUN_00451e74. Its operand holds the data (a short at
/// byte 0) and the owner (a short at byte 2), read with FUN_00453181 as a value, not a reference. A nonzero value raises
/// error 62 ("Tree break encountered."); a zero value leaves the primitive true.
/// <para>The debugger flag on an instruction or a tree is handled by <see cref="TreeStack"/>; this primitive is the
/// conditional break only.</para></summary>
public static class BreakpointPrimitive
{
    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var data = (short)(operand[0] | operand[1] << 8);
        // FUN_00451e74 pushes the owner as a short, so Sims.exe sign-extends it: a negative owner is out of range.
        var owner = (DataOwner)(short)(operand[2] | operand[3] << 8);
        if (Variables.Get(context, owner, data) != 0) throw new SimulationException(SimulationError.TreeBreakEncountered);
        return PrimitiveResult.True;
    }
}
