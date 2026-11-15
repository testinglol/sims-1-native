namespace SimsEngine.Simulation;

/// <summary>The Sleep primitive (opcode 0), after Sims.exe 0x451c87 (not in the decompilation; read from the
/// disassembly). Its operand's first word names a parameter of the frame, the counter. A parameter outside the
/// frame's parameters raises "Stack number out of range." (8).
/// <para>The wait cell is the low word of the running object's parallel clock-manager entry at +0x24, indexed by the
/// object's +0xf0 slot. A zero cell clears the counter and sets the cell to -1 (0xFFFF, FUN_00431e78). Then a counter at or
/// below zero (negative ones are set to zero) answers true. A positive counter answers "stop" (2), so the run ends and
/// this instruction runs again on the next tick. On that tick the counter either goes down by one, or, when
/// <see cref="SimulationWorld.SleepWritesWaitCell"/> answers true, the wait cell takes the counter minus one and the
/// counter is kept.</para>
/// <para>The wait-cell write gate is FUN_0044aa97, recovered from the original executable disassembly: OBJD type 2
/// returns false; otherwise it checks the first 0x3c-byte balloon record's dword at +0x10 and cXObject word +0x7c.
/// The raw +0x7c meaning and record initialization are not yet traced.</para></summary>
public static class SleepPrimitive
{
    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var index = (short)(operand[0] | operand[1] << 8);
        var (world, me, frame) = context;
        // Sims.exe checks the sign and the frame's parameter count (byte +7) with the same error.
        if (index < 0 || index >= frame.Parameters.Length) throw new SimulationException(SimulationError.StackNumberOutOfRange);
        var managerSlot = me.ClockManagerIndex;
        var waitCell = world.ClockManager.WaitTicksAt(managerSlot);
        ref var counter = ref frame.Parameters[index];
        if (waitCell == 0)
        {
            counter = 0;
            world.ClockManager.SetWaitTicksAt(managerSlot, -1);
        }
        if (counter <= 0)
        {
            counter = 0;
            return PrimitiveResult.True;
        }
        if (world.SleepWritesWaitCell(me)) world.ClockManager.SetWaitTicksAt(managerSlot, (short)(counter - 1));
        else counter--;
        return PrimitiveResult.Stop;
    }
}
