namespace SimsEngine.Simulation;

public static class SleepPrimitive
{
    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var index = (short)(operand[0] | operand[1] << 8);
        var (world, me, frame) = context;
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
