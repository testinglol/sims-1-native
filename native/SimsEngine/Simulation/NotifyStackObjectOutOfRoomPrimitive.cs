namespace SimsEngine.Simulation;

/// <summary>The Notify Stack Object Out Of Room primitive (opcode 0x31), after Sims.exe 0x452199 (read from the decompilation,
/// checked against the disassembly). It has no operand.
/// <para>Order: the stack object must exist ("No stack object present when required.", 21). The stack object's current wait
/// record is then read (<see cref="SimulationWorld.WaitInstructionOpcode"/>, FUN_004750ba, FUN_004754d8 and FUN_004020a6). With
/// no record the primitive does nothing. Opcode 0 (sleep) clears the record's counter (<see cref="SimulationWorld.ClearWaitCounter"/>,
/// FUN_004752f8) and sets the object's wait cell to 0 (the low word of its parallel clock-manager entry, FUN_00431e78). Opcode 0x11
/// (idle for input) clears the counter only. Any other opcode does nothing. The primitive always answers true.</para>
/// <para>Name: the primitive has no room test. It zeroes the stack object's sleep or idle counter, acting as an out-of-idle wake notification.</para>
/// <para>Inferred: the wait record projects to the restored thread's top frame and the counter to the parameter the
/// waiting instruction names.</para>
/// </summary>
public static class NotifyStackObjectOutOfRoomPrimitive
{
    private const short Sleep = 0;
    private const short IdleForInput = 0x11;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, _, _) = context;
        var stackObject = context.StackObject ?? throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
        switch (world.WaitInstructionOpcode(stackObject))
        {
            case Sleep:
                world.ClearWaitCounter(stackObject);
                world.ClockManager.SetWaitTicksAt(stackObject.ClockManagerIndex, 0);
                break;
            case IdleForInput:
                world.ClearWaitCounter(stackObject);
                break;
        }
        return PrimitiveResult.True;
    }
}
