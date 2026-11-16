namespace SimsEngine.Simulation;

/// <summary>Plans and executes a relative movement route for a Sim based on specified relative direction,
/// target facing, proximity thresholds, and footprint flags.</summary>
public static class GoToRelativePositionPrimitive
{
    private static void AddDirection(RoutingSlotRequest slot, int direction) => slot.Flags |= 1u << direction;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        if (frame.Waiting)
            return world.Host is { } waitingHost
                ? waitingHost.Route(me, frame, null)
                : RouteExecutor.Continue(world, me, frame, context.Stack);
        var slot = new RoutingSlotRequest
        {
            Standing = 1, MinProximity = 16, MaxProximity = 16, OptimalProximity = 16,
        };
        if ((operand[6] & 2) == 0) slot.Flags |= 0x4000;
        if ((operand[6] & 4) != 0) slot.Flags |= 0x8000;
        slot.CheckCandidateFootprintAgainstWall = (operand[6] & 4) != 0;

        var direction = (sbyte)operand[2];
        switch (direction)
        {
            case -2:
                slot.Flags &= ~0xffu;
                break;
            case 1 or 3 or 5 or 7:
                AddDirection(slot, direction - 1);
                AddDirection(slot, (direction + 1) % 8);
                break;
            case 0 or 2 or 4 or 6:
                AddDirection(slot, direction);
                break;
            default:
                AddDirection(slot, 0);
                AddDirection(slot, 2);
                AddDirection(slot, 4);
                AddDirection(slot, 6);
                slot.MaxProximity = 32;
                break;
        }

        var facing = (sbyte)operand[3];
        switch (facing)
        {
            case -2:
                slot.Facing = -2;
                break;
            case -1:
                slot.Facing = -3;
                break;
            case >= 0 and <= 7:
                slot.Facing = facing;
                break;
            default:
                return PrimitiveResult.False;
        }
        if (world.Host is { } host) return host.Route(me, frame, slot);
        if (context.StackObject is not { } anchor || !world.RoutingEnabled)
            return PrimitiveResult.False;
        return RouteExecutor.Start(world, me, frame, anchor, slot, context.Stack);
    }
}
