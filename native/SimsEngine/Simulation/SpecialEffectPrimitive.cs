namespace SimsEngine.Simulation;

/// <summary>The Special Effect primitive (opcode 0x23), after Sims.exe 0x44aae6 (read from the decompilation; the operand offsets
/// are the ones the decompiler gives, in 2-byte units for the word pointer).
/// <para>Operand: word 0 is the value's data (a literal, or a local when byte 4 bit 5 is set, read as <see cref="Variables.Get"/>
/// reads it). Bytes 2 to 5 go to the host unchanged.</para>
/// <para>The primitive runs over three calls, and the frame's state keeps the call count (<see cref="SimulationWorld.PrimitiveTicks"/>,
/// the frame's dword at +8). The first call sets the effect word (<see cref="ISimulationHost.SetEffectWord"/>, FUN_0046dae0 with
/// index 11 and value 1) and stops. The second needs the stack object ("Could not find object reference.", 23 otherwise); it reads
/// the value, and gives the effect to the host (<see cref="ISimulationHost.PlaySpecialEffect"/>). When execution reaches the
/// renderer dispatch, the primitive stops again; the third call answers true and clears the state. Headless execution records
/// the dispatch but does not render it, matching FUN_004a6b68's early return without a renderer manager.</para>
/// <para>The value is in milliseconds: the variable times 1000. Sims.exe multiplies the value by 1000 before the playback call.</para>
/// <para>With no host, the decoded request is recorded by <see cref="HeadlessSpecialEffectService"/> at the
/// playback boundary and preserves the source's wait tick without claiming that it renders effects. The source gate and
/// renderer playback remain host-backed. A session that sets <see cref="SimulationWorld.PictureInPicture"/> applies the Live PIP gate and
/// receives <see cref="PictureInPictureEvent"/>s for its viewer.</para>
/// </summary>
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
            if (host is null) world.GlobalObjectTableEffectWords[StartIndex] = StartValue;
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

    /// <summary>The gate FUN_0044aae6 applies before it reaches the renderer (<see cref="SimulationWorld.AllowsPictureInPicture"/>);
    /// a refused effect leaves the primitive answering true with nothing dispatched. A passed one dispatches notice 0x10c
    /// unless flag bit 6 is set and is handed to the session's viewer as a <see cref="PictureInPictureEvent"/>.</summary>
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
