namespace SimsEngine.Simulation;

/// <summary>Executes the Notify Stack Object Out Of Room primitive (opcode 0x31) to wake a waiting stack
/// object by clearing its sleep or idle counter.</summary>
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
