namespace SimsEngine.Simulation;

/// <summary>The Look Towards primitive (opcode 0x16), after Sims.exe FUN_0045aafa (labelled "Look Towards" in Behavior.iff
/// STR# 139). Sims.exe reaches it through the person dispatcher FUN_0045cd1f, so the running object is a person.
/// <para>Operand: the mode is the short at byte 0. Mode 0 passes the frame stack-object key to <c>SAnimator::LookTowards</c>, which
/// stores it as the head-seek target and enables seeking when it is nonzero, then answers true. For modes 1 to 3,
/// Mac constructs a follow path; mode 1 uses the rotated front offset, mode 2 resolves the signed frame stack-object key as an
/// object id and follows to its position, and mode 3 follows to its reflection through the person. A missing object raises error 21
/// (<see cref="SimulationError.NoStackObjectPresentWhenRequired"/>). Any other mode raises error 39 (<see cref="SimulationError.BadParameterInLookTowardsPrimitive"/>),
/// before the frame's started flag is read.</para>
/// <para>The first tick of modes 1 to 3 (the frame's <see cref="StackFrame.Waiting"/> flag is clear) answers false when the
/// person is sitting (person data 0, "Sitting?"). Modes 2 and 3 resolve the frame stack-object key through the object manager, or raise error 21
/// (<see cref="SimulationError.NoStackObjectPresentWhenRequired"/>). Mode 2 uses that object's position; mode 3 reflects its
/// X/Y coordinates through the person (2 * me - target). Mac builds a three-point TileList path, starts SAnimator follow, and
/// calls FollowOneStep on each invocation; hostless native drives those points with saved root-motion state. A host receives
/// the point through the turn interface.</para>
/// <para>Every tick (the started ones too) reads the turn: <see cref="ISimulationHost.TurnStatus"/> (FUN_00589d4b) answers 0
/// for nothing to turn, which becomes 1, and 2 is busy. When the status is not busy, <see cref="ISimulationHost.TryStartTurn"/>
/// (FUN_00589e31) is asked to start a turn, and a refusal makes the answer busy. Busy answers stop (2), so the instruction
/// runs again on the next tick; otherwise the answer is true. The started flag is cleared by the runner when the frame
/// branches (<see cref="TreeStack"/>), as FUN_00474cae clears it.</para>
/// <para>Remaining gaps: Mac EndFollow (SAnimator.s 0x10356880) returns false and sets +0x27c when follow state +0x180 is 1 or -1;
/// otherwise it clears follow bookkeeping, copies the root coordinates, sets control state +0x188 to 3, and returns true.
/// TryLookTowards converts a deferred EndFollow into a busy result. Native route teardown does not represent these animator
/// fields or the deferred cleanup path. Interrupted and serialized mid-follow parity remain unverified. The original TileList
/// stores only X/Y, so following keeps the Sim's current height.</para></summary>
public static class LookTowardsPrimitive
{
    private const short LookAtStackObject = 0;
    private const short Front = 1;
    private const short Towards = 2;
    private const short Away = 3;

    // FUN_00589d4b answers 2 when the turn is busy; the primitive then stops and runs again next tick.
    private const int Busy = 2;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var mode = (short)(operand[0] | operand[1] << 8);
        var (world, me, frame) = context;
        var person = Person(context);
        if (mode == LookAtStackObject)
        {
            // Mac person.s 0x10106b60 loads the frame's stack-object key and passes it to
            // SAnimator::LookTowards (0x10356400), which stores the target and enables seeking iff nonzero.
            var target = frame.StackObject;
            person[PersonData.HeadSeekObject] = target;
            person[PersonData.HeadSeekState] = (short)(target != 0 ? 1 : 0);
            person[PersonData.HeadSeekFinishAction] = 0;
            person[PersonData.HeadSeekLimitAction] = 1;
            person[PersonData.HeadSeekTimeout] = 0;
            return PrimitiveResult.True;
        }
        if (mode is not (Front or Towards or Away)) throw new SimulationException(SimulationError.BadParameterInLookTowardsPrimitive);
        var host = world.Host;

        if (!frame.Waiting)
        {
            if (person[PersonData.Sitting] != 0) return PrimitiveResult.False;
            ObjectPosition? point = null;
            if (mode == Front)
            {
                me[ObjectData.RoutePreference] = 0;
            }
            else
            {
                // Mac person.s 0x10106D4C and Windows FUN_0045aafa resolve the frame stack-object key through
                // the object manager (FUN_00407498); this value is the BHAV stack-object key.
                var target = world.ObjectByStackObject(me, frame.StackObject)
                    ?? throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
                point = mode == Away ? Reflect(me.Position, target.Position) : target.Position;
            }
            if (host is null)
            {
                person.LastLookTowardsPoint = point;
                var followPoints = mode == Front
                    ? BuildFrontFollowPath(world, me.Position)
                    : [me.Position, me.Position, point!.Value with { Height = me.Position.Height }];
                return RouteExecutor.BeginLookTowardsFollow(world, person, frame, followPoints);
            }
            else host.TurnTowards(me, point);
            frame.Waiting = true;
        }

        if (host is null)
        {
            if (person.NativeRoute is not { LookTowardsFollow: true, OwnerFrame: var owner } || !ReferenceEquals(owner, frame))
                return PrimitiveResult.True;
            return RouteExecutor.Continue(world, person, frame, context.Stack);
        }
        var status = host.TurnStatus(me);
        if (status == 0) status = 1;
        if (status != Busy && !host.TryStartTurn(me)) status = Busy;
        return status == Busy ? PrimitiveResult.Stop : PrimitiveResult.True;
    }

    // Mode 3 reflects the target's X/Y through the person (FUN_0045aafa). Height is ignored by the source TileList path.
    private static ObjectPosition Reflect(ObjectPosition me, ObjectPosition target) =>
        new(2 * me.X - target.X, 2 * me.Y - target.Y, target.Height);

    // Mac TryLookTowards mode 1 rotates the current point into lot coordinates, adds the FTilePt (16, 16)
    // returned by FUN_00422055, then rotates the offset back before inserting [current, current, front].
    private static ObjectPosition[] BuildFrontFollowPath(SimulationWorld world, ObjectPosition current)
    {
        var orientation = world.LotTransformParameter & 3;
        var rotated = LotGeometry.TransformSearchCenter(current, orientation);
        var offset = rotated with { X = rotated.X + 16, Y = rotated.Y + 16 };
        var front = LotGeometry.TransformSearchCenter(offset, (4 - orientation) & 3) with { Height = current.Height };
        return [current, current, front];
    }

    // Routing guarantees a person (FUN_0045cd1f runs only for the person class).
    private static XPerson Person(in TreeContext context) =>
        context.Me as XPerson ?? throw new InvalidOperationException($"Object {context.Me.Id} is not a person; FUN_0045cd1f runs for persons only.");
}
