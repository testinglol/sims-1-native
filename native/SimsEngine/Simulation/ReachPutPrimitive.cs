namespace SimsEngine.Simulation;

/// <summary>Executes the Reach/Put primitive (opcode 0x2f) across multi-tick animation phases for Grab and
/// Put operations, running callbacks on the target object once placement checks pass.</summary>
public static class ReachPutPrimitive
{
    private const short ModeGrab = 0;
    private const short ModePut = 1;
    private const short ModeNeither = 2;

    private const int PhaseStart = 0;
    private const int PhaseReaching = 1;
    private const int PhaseActed = 2;
    private const int PhaseRefused = 3;

    private const float NeitherScale = -1f;
    private const float PositionScale = 3f;

    // Identifies reach targets: Target is the stack object for Grab or slot object for Put, while Held is
    // the object being transferred.
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
            return PrimitiveResult.Stop;
        }
        frame.WaitState = PhaseActed;
        return PrimitiveResult.Stop;
    }

    // Waits for the reach animation to complete, returning true on success or false on refusal.
    private static PrimitiveResult Finish(XPerson me, IReachOps host, bool answer)
    {
        if (!host.ReachAnimationDone(me)) return PrimitiveResult.Stop;
        host.FinishReach(me);
        me.ReachAnimationActive = false;
        return answer ? PrimitiveResult.True : PrimitiveResult.False;
    }

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
                // The first held slot object resides at slot index 1.
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

    // Resolves and executes a named action tree synchronously within the target object's behavior scope.
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

    private static XPerson Person(in TreeContext context) =>
        context.Me as XPerson ?? throw new InvalidOperationException($"Object {context.Me.Id} is not a person; FUN_0045cd1f runs for persons only.");
}
