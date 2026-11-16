namespace SimsEngine.Simulation;

/// <summary>Executes the Look Towards primitive (opcode 0x16) to orient a Sim's head or body toward a
/// target, front offset, or reflected position, failing when sitting.</summary>
public static class LookTowardsPrimitive
{
    private const short LookAtStackObject = 0;
    private const short Front = 1;
    private const short Towards = 2;
    private const short Away = 3;

    // A busy turn status stops execution so the primitive retries on the next tick.
    private const int Busy = 2;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var mode = (short)(operand[0] | operand[1] << 8);
        var (world, me, frame) = context;
        var person = Person(context);
        if (mode == LookAtStackObject)
        {
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

    // Mode 3 reflects target X/Y coordinates across the Sim while preserving height.
    private static ObjectPosition Reflect(ObjectPosition me, ObjectPosition target) =>
        new(2 * me.X - target.X, 2 * me.Y - target.Y, target.Height);

    // Projects a forward path by rotating a 16x16 tile offset into lot coordinates.
    private static ObjectPosition[] BuildFrontFollowPath(SimulationWorld world, ObjectPosition current)
    {
        var orientation = world.LotTransformParameter & 3;
        var rotated = LotGeometry.TransformSearchCenter(current, orientation);
        var offset = rotated with { X = rotated.X + 16, Y = rotated.Y + 16 };
        var front = LotGeometry.TransformSearchCenter(offset, (4 - orientation) & 3) with { Height = current.Height };
        return [current, current, front];
    }

    private static XPerson Person(in TreeContext context) =>
        context.Me as XPerson ?? throw new InvalidOperationException($"Object {context.Me.Id} is not a person; FUN_0045cd1f runs for persons only.");
}
