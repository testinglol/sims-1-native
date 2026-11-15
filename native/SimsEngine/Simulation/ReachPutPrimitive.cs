namespace SimsEngine.Simulation;

/// <summary>The Reach/Put primitive (opcode 0x2f), after Sims.exe FUN_0045cb26 (labelled "Reach/Put" in Behavior.iff STR# 139).
/// Sims.exe reaches it through the person dispatcher FUN_0045cd1f, so the running object is a person.
/// <para>Operand: the mode (a short at bytes 0 and 1: 0 is Grab, 1 is Put, 2 is neither), the flag (a short at bytes 2 and 3) and
/// the parameter index (a short at bytes 4 and 5, read by mode 1 and by the target resolution). Bytes 6 and 7 are not read.</para>
/// <para>The frame's <see cref="StackFrame.WaitState"/> is the phase, 0 at the start:</para>
/// <list type="bullet">
/// <item>0 (start): the target is resolved (<see cref="Resolve"/>, FUN_0045c9b4). Without one the answer is false. Otherwise the
/// reach animation starts at the target's position (<see cref="ISimulationHost.StartReachAnimation"/>): a start moves to phase 1
/// and stops (2). When it does not start, the answer is true for a zero flag and false otherwise.</item>
/// <item>1 (reaching): the avatar's events are drained (<see cref="ISimulationHost.NextAvatarEvent"/>); an event with code 0, or
/// a finished animation (<see cref="ISimulationHost.ReachAnimationDone"/>), lets the primitive go on. Otherwise it stops (2). The
/// target is resolved again. Without a target the answer is true for a zero flag, false otherwise. A zero flag moves to phase 2.
/// Mode 0 (Grab) checks the stack object with <see cref="ISimulationHost.CanGrab"/> (the person's position and height, the person,
/// flag 0). Mode 1 (Put) checks the slot object with <see cref="ISimulationHost.CanPlace"/> (the held object's position and
/// height, the held object, the parameter's value). A refusal, or a missing Put target, moves to phase 3. An accepted check runs
/// its action (<see cref="ISimulationHost.Grab"/> or <see cref="ISimulationHost.Place"/>), then the named tree "CT - Grab" or "CT
/// - Put" runs on the target (FUN_00452c2d), and the frame moves to phase 2. Any other mode stops (2) and keeps the phase.</item>
/// <item>2 and 3 (acting and refused): the animation must be done (<see cref="ISimulationHost.ReachAnimationDone"/>), or the
/// answer is stop (2). Then the reach is finished (<see cref="ISimulationHost.FinishReach"/>): phase 2 answers true, phase 3 false.</item>
/// <item>Any other phase yields (-1).</item>
/// </list>
/// <para>Target resolution (FUN_0045c9b4, <see cref="Resolve"/>): mode 2 gives no objects and the scale -1 (DAT_00666c18). Mode 0
/// takes the frame's stack object as the target and its holder and value from <see cref="ISimulationHost.GrabHolder"/>. A holder
/// gives the scale, the slot's position times 3 (<see cref="ISimulationHost.SlotPosition"/>, DAT_00667344), once the value is
/// within the holder's slots; with no holder the scale is 0. Mode 1 takes the stack object as the held object and the person's
/// first slot object (slot vector entry 1) as the target. A missing stack object answers no target. Otherwise the parameter index
/// is checked (error 8, <see cref="SimulationError.StackNumberOutOfRange"/>), and the value must be within the held object's
/// slots.</para>
/// <para>The post-action named tree resolves through <see cref="Content.BehaviorScope.FindNamedTree"/> and runs synchronously
/// on the target object as FUN_00452c2d does. The Grab and Put checks and actions, the reach animation and its events are host
/// calls. Without a legacy host <see cref="ReachOps"/> supplies them from the Mac SAnimator/cXPerson reach code (clip bands, Global.iff STR# 154/155, placement check and action). Mac IFFResFile named lookup uses case-insensitive IFFNameList matching (see <see cref="Content.BehaviorScope.FindNamedTree"/>).</para></summary>
public static class ReachPutPrimitive
{
    private const short ModeGrab = 0;
    private const short ModePut = 1;
    private const short ModeNeither = 2;

    private const int PhaseStart = 0;
    private const int PhaseReaching = 1;
    private const int PhaseActed = 2;
    private const int PhaseRefused = 3;

    // DAT_00666c18 (-1.0) is the scale mode 2 gives, and DAT_00667344 (3.0) multiplies a slot's position.
    private const float NeitherScale = -1f;
    private const float PositionScale = 3f;

    // The objects and values FUN_0045c9b4 hands back. Target is the object the check and action run on (the stack object for
    // Grab, the first slot object for Put), Held is the object Put moves (the holder for Grab, the stack object for Put), and
    // Scale is the reach position.
    private sealed record ReachTarget(XObject? Target, XObject? Held, int Value, float Scale);

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var frame = context.Frame;
        var me = Person(context);
        var host = Surface(context);
        switch (frame.WaitState)
        {
            case PhaseStart:
                return Start(context, me, host, operand);
            case PhaseReaching:
                return Reaching(context, me, host, operand);
            case PhaseActed:
                return Finish(me, host, true);
            case PhaseRefused:
                return Finish(me, host, false);
            default:
                return PrimitiveResult.Yield;
        }
    }

    // Phase 0 (FUN_0045cb26, the first branch).
    private static PrimitiveResult Start(in TreeContext context, XPerson me, IReachOps host, ReadOnlySpan<byte> operand)
    {
        var reach = Resolve(context, operand);
        if (reach is null) return PrimitiveResult.False;
        if (host.StartReachAnimation(me, reach.Scale))
        {
            me.ReachAnimationActive = true;
            context.Frame.WaitState = PhaseReaching;
            return PrimitiveResult.Stop;
        }
        return Flag(operand) == 0 ? PrimitiveResult.True : PrimitiveResult.False;
    }

    // Phase 1 (FUN_0045cb26, the second branch).
    private static PrimitiveResult Reaching(in TreeContext context, XPerson me, IReachOps host, ReadOnlySpan<byte> operand)
    {
        var reached = false;
        while (host.NextAvatarEvent(me, out var code))
        {
            if (code == 0) reached = true;
        }
        if (host.ReachAnimationDone(me)) reached = true;
        if (!reached) return PrimitiveResult.Stop;

        var reach = Resolve(context, operand);
        if (reach is null) return Flag(operand) == 0 ? PrimitiveResult.True : PrimitiveResult.False;
        var frame = context.Frame;
        if (Flag(operand) == 0)
        {
            frame.WaitState = PhaseActed;
            return PrimitiveResult.Stop;
        }

        var mode = Mode(operand);
        if (mode == ModeGrab)
        {
            if (reach.Target is null || !host.CanGrab(reach.Target, me.Position, me, 0))
            {
                frame.WaitState = PhaseRefused;
                return PrimitiveResult.Stop;
            }
            host.Grab(reach.Target, me.Position, me, 0);
            RunNamedTree(context, reach.Target, "CT - Grab");
        }
        else if (mode == ModePut)
        {
            if (reach.Target is null || reach.Held is null || !host.CanPlace(reach.Target, reach.Held.Position, reach.Held, reach.Value))
            {
                frame.WaitState = PhaseRefused;
                return PrimitiveResult.Stop;
            }
            host.Place(reach.Target, reach.Held.Position, reach.Held, reach.Value);
            RunNamedTree(context, reach.Target, "CT - Put");
        }
        else
        {
            // Sims.exe answers stop and keeps the phase for any other mode.
            return PrimitiveResult.Stop;
        }
        frame.WaitState = PhaseActed;
        return PrimitiveResult.Stop;
    }

    // Phases 2 and 3: wait for the animation, then finish the reach. Phase 2 answers true and phase 3 false.
    private static PrimitiveResult Finish(XPerson me, IReachOps host, bool answer)
    {
        if (!host.ReachAnimationDone(me)) return PrimitiveResult.Stop;
        host.FinishReach(me);
        me.ReachAnimationActive = false;
        return answer ? PrimitiveResult.True : PrimitiveResult.False;
    }

    // FUN_0045c9b4: the target of the operand's mode, or null when Sims.exe answers 0.
    private static ReachTarget? Resolve(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var host = Surface(context);
        switch (Mode(operand))
        {
            case ModeNeither:
                return new ReachTarget(null, null, 0, NeitherScale);
            case ModeGrab:
            {
                var stackObject = world.ObjectByStackObject(me, frame.StackObject);
                if (stackObject is null) return null;
                var (holder, value) = host.GrabHolder(stackObject);
                if (holder is null) return new ReachTarget(stackObject, null, value, 0);
                if (value < 0 || value >= host.SlotCount(holder)) return null;
                return new ReachTarget(stackObject, holder, value, host.SlotPosition(holder, value) * PositionScale);
            }
            case ModePut:
            {
                var stackObject = world.ObjectByStackObject(me, frame.StackObject);
                if (stackObject is null) return null;
                // FUN_00454470 and the reach/put source path read the first real +0x130 entry (vector index 1).
                var slot = me.Slots.Count > 1 ? world.ObjectByStackObject(me, me.Slots[1]) : null;
                var index = (short)(operand[4] | operand[5] << 8);
                if (index < 0 || index >= frame.Parameters.Length) throw new SimulationException(SimulationError.StackNumberOutOfRange);
                var value = frame.Parameters[index];
                if (value < 0 || value >= host.SlotCount(stackObject)) return null;
                return new ReachTarget(slot, stackObject, value, host.SlotPosition(stackObject, value) * PositionScale);
            }
            default:
                return null;
        }
    }

    // FUN_00452c2d resolves on the target object's behavior scope and runs the found BHAV synchronously with that
    // target as the stack object. A missing name (id 0) is a no-op, as in FUN_00452c2d.
    private static void RunNamedTree(in TreeContext context, XObject target, string name)
    {
        if (context.Stack is not { } stack || target.TypeRecord?.BehaviorScope is not Content.BehaviorScope scope) return;
        var treeId = scope.FindNamedTree(name);
        if (treeId == 0) return;

        var saved = context.World.SelfRemovalEndsRun;
        context.World.SelfRemovalEndsRun = false;
        try { stack.RunToReturn(scope, [], true, treeId, target.StackObjectKey); }
        finally { context.World.SelfRemovalEndsRun = saved; }
    }

    private static IReachOps Surface(in TreeContext context) =>
        context.World.Host is { } host ? new HostReachOps(host) : new ReachOps(context.World, context.Stack);

    private static short Mode(ReadOnlySpan<byte> operand) => (short)(operand[0] | operand[1] << 8);

    private static short Flag(ReadOnlySpan<byte> operand) => (short)(operand[2] | operand[3] << 8);

    // Routing guarantees a person (FUN_0045cd1f runs only for the person class).
    private static XPerson Person(in TreeContext context) =>
        context.Me as XPerson ?? throw new InvalidOperationException($"Object {context.Me.Id} is not a person; FUN_0045cd1f runs for persons only.");
}
