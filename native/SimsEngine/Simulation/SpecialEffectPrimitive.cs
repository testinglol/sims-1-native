namespace SimsEngine.Simulation;

/// <summary>Executes the Special Effect primitive (opcode 0x23) across three ticks, initializing the effect
/// state, scaling duration to milliseconds, and dispatching playback to the host.</summary>
public static class SpecialEffectPrimitive
{
    private const short StartIndex = 11;
    private const short StartValue = 1;
    private const int MillisecondsPerUnit = 1000;
    private const byte LocalValueFlag = 0x20;
    private const int Starting = 1;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var host = world.Host;
        var ticks = world.PrimitiveTicks;
        if (!ticks.TryGetValue(frame, out var tick))
        {
            if (host is null)
            {
                world.Globals[StartIndex] = StartValue;
                world.GlobalObjectTableEffectWords[StartIndex] = StartValue;
            }
            else host.SetEffectWord(StartIndex, StartValue);
            ticks[frame] = Starting;
            return PrimitiveResult.Stop;
        }
        if (tick == Starting)
        {
            ticks[frame] = Starting + 1;
            var stackObject = context.StackObject ?? throw new SimulationException(SimulationError.CouldNotFindObjectReference);
            var owner = (operand[4] & LocalValueFlag) != 0 ? DataOwner.Local : DataOwner.Literal;
            var value = Variables.Get(context, owner, (short)(operand[0] | operand[1] << 8));
            var request = new SpecialEffectRequest(value * MillisecondsPerUnit, operand[2], operand[3], operand[4], operand[5]);
            var started = host is null
                ? StartHeadless(world, me, stackObject, request, frame.Table as Content.BehaviorScope, frame.TreeId)
                : host.PlaySpecialEffect(me, stackObject, request);
            if (started) return PrimitiveResult.Stop;
        }
        ticks.Remove(frame);
        return PrimitiveResult.True;
    }

    /// <summary>Gates special effect execution through picture-in-picture permissions, broadcasting notice
    /// 0x10C when permitted unless suppressed by flags.</summary>
    private static bool StartHeadless(SimulationWorld world, XObject me, XObject stackObject,
        SpecialEffectRequest request, Content.BehaviorScope? scope, short treeId)
    {
        if (!world.AllowsPictureInPicture(me, request.Byte4)) return false;
        if ((request.Byte4 & 0x40) == 0)
            world.DispatchNotice(0x10c, 0);
        world.RecordPictureInPicture(me, stackObject, request, scope, treeId);
        return world.HeadlessSpecialEffects.TryStart(me, stackObject, request);
    }
}
