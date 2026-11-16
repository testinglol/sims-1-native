namespace SimsEngine.Simulation;

/// <summary>Calculates a target placement position and orientation relative to a stack object using routing
/// slots, footprint slots, or directional footprint offsets according to the specified snapping mode.</summary>
public static class SnapshotPrimitive
{
    private const short StackObjectMode = 1;
    private const short FrontMode = 2;
    private const byte DifferenceFlag = 1;
    private const byte SlotFlag = 4;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, _) = context;
        var stackObject = context.StackObject ?? throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
        var mode = (short)(operand[2] | operand[3] << 8);
        var flags = operand[4];
        XObject? target;
        ObjectPosition position;
        var snapTargetSlot = -1;
        var snapTargetFlag = false;
        var directionMask = me[ObjectData.Direction] & 7;
        switch (mode)
        {
            case StackObjectMode:
                target = stackObject;
                position = stackObject.Position;
                snapTargetSlot = 0;
                break;
            case FrontMode:
                target = null;
                var offset = LotGeometry.FootprintOffset(stackObject[ObjectData.Direction]);
                position = stackObject.Position with { X = stackObject.Position.X + offset.X, Y = stackObject.Position.Y + offset.Y };
                break;
            case 0:
            {
                var parameterIndex = (short)(operand[0] | operand[1] << 8);
                if (parameterIndex < 0 || parameterIndex >= context.Frame.Parameters.Length)
                    throw new SimulationException(SimulationError.StackNumberOutOfRange);
                var slotIndex = context.Frame.Parameters[parameterIndex];
                if (slotIndex < 0 || slotIndex >= stackObject.RoutingSlots.Count)
                    throw new SimulationException(SimulationError.SlotNumberOutOfRange);
                (target, position, snapTargetSlot, snapTargetFlag, directionMask) =
                    ProjectSlot(world, me, stackObject, stackObject.RoutingSlots[slotIndex]);
                break;
            }
            case 3:
            {
                var slotIndex = (short)(operand[0] | operand[1] << 8);
                if (slotIndex < 0 || slotIndex >= stackObject.RoutingSlots.Count)
                    throw new SimulationException(SimulationError.SlotNumberOutOfRange);
                (target, position, snapTargetSlot, snapTargetFlag, directionMask) =
                    ProjectSlot(world, me, stackObject, stackObject.RoutingSlots[slotIndex]);
                break;
            }
            case 4:
            {
                var slotIndex = (short)(operand[0] | operand[1] << 8);
                if (world.Host is { } slotHost)
                {
                    if (slotIndex < 0 || slotIndex >= slotHost.LotRoutingSlotCount(stackObject))
                        throw new SimulationException(SimulationError.SlotNumberOutOfRange);
                    (target, position, snapTargetSlot, snapTargetFlag, directionMask) =
                        ProjectSlot(world, me, stackObject, slotHost.LotRoutingSlotAt(stackObject, slotIndex));
                    break;
                }
                if (slotIndex < 0 || slotIndex >= world.LotRoutingSlots.Count)
                    throw new SimulationException(SimulationError.SlotNumberOutOfRange);
                (target, position, snapTargetSlot, snapTargetFlag, directionMask) =
                    ProjectSlot(world, me, stackObject, world.LotRoutingSlots[slotIndex]);
                break;
            }
            default:
                throw new SimulationException(SimulationError.InvalidSnappingModeSpecified);
        }
        (int Dy, int Dx, int Ddir)? relative = (flags & DifferenceFlag) != 0
            ? (me.Position.Y - position.Y, me.Position.X - position.X,
                me[ObjectData.Direction] - directionMask)
            : null;
        if (world.Host is { } host)
            return host.RunSnapshot(me, target, position, snapTargetSlot, snapTargetFlag, directionMask, (flags & SlotFlag) != 0, relative);
        var (displacementX, displacementY, displacementDirection) =
            ComputeAnimatorDisplacements(me, target, position, snapTargetSlot, directionMask, flags);
        world.ResetPlacementFailure();
        PrimitiveResult result = target is not null
            ? SnapshotExecutor.PlaceInFootprintSlot(world, me, target, snapTargetSlot, directionMask,
                snapTargetFlag, (flags & SlotFlag) != 0, context.Stack)
            : SnapshotExecutor.PlaceAtFreePosition(world, me, position, directionMask, snapTargetFlag, context.Stack);
        if (result == PrimitiveResult.True && me is XPerson person)
            person.SetAnimatorDisplacements(displacementX, displacementY, displacementDirection);
        return ApplyFailureTail(world, me, flags, result);
    }

    private static (int X, int Y, int Direction) ComputeAnimatorDisplacements(
        XObject me, XObject? target, ObjectPosition position, int snapTargetSlot, int directionMask, byte flags)
    {
        var dx = (flags & DifferenceFlag) != 0 ? me.Position.X - position.X : 0;
        var dy = (flags & DifferenceFlag) != 0 ? me.Position.Y - position.Y : 0;
        var ddir = (flags & DifferenceFlag) != 0 ? me[ObjectData.Direction] - directionMask : 0;
        if (target is not null && (uint)snapTargetSlot < (uint)target.FootprintSlots.Count)
        {
            var slot = target.FootprintSlots[snapTargetSlot];
            var (slotOffsetX, slotOffsetY) = (target[ObjectData.Direction] & 7) switch
            {
                0 => ((int)slot.X, (int)slot.Y),
                2 => ((int)-slot.Y, (int)slot.X),
                4 => ((int)-slot.X, (int)-slot.Y),
                6 => ((int)slot.Y, (int)-slot.X),
                _ => (0, 0),
            };
            dx += slotOffsetX;
            dy += slotOffsetY;
        }
        return (dx, dy, ddir);
    }

    private static PrimitiveResult ApplyFailureTail(SimulationWorld world, XObject me, byte flags, PrimitiveResult result)
    {
        me[ObjectData.PrimitiveResult] = 0;
        if (result == PrimitiveResult.True) return result;

        var blocker = world.LastPlacementBlockingObjectId == 0 ? null
            : world.ObjectTableEntry(world.LastPlacementBlockingObjectId)
              ?? world.WalkOrder.FirstOrDefault(candidate => candidate.ClockManagerIndex == world.LastPlacementBlockingObjectId);
        if (me is XPerson person && (flags & 2) != 0 && world.LastPlacementFailureCode == 0x0b &&
            blocker is XPerson blockerPerson &&
            RouteExecutor.MoveOutOfWay(world, blockerPerson, person.CurrentActionRecord?.Threshold ?? 0))
            me[ObjectData.PrimitiveResult] = 1;
        if (me[ObjectData.PrimitiveResult] == 0 && world.LastPlacementFailureCode == 1)
            me[ObjectData.PrimitiveResult] = 2;
        me[ObjectData.PrimitiveResultId] = blocker is null ? (short)0 : blocker.ClockManagerIndex;
        return result;
    }

    private static (XObject? Target, ObjectPosition Position, int SnapTargetSlot, bool SnapTargetFlag, int Direction) ProjectSlot(
        SimulationWorld world, XObject me, XObject stackObject, RoutingSlotRequest slot)
    {
        var snapTargetFlag = (slot.Flags & 0x800) != 0;
        short direction;
        if ((slot.Flags & 0x1000) == 0)
        {
            direction = me[ObjectData.Direction];
        }
        else
        {
            var allowed = (byte)slot.Flags;
            var relativeDirection = 0;
            while (relativeDirection < 7 && (allowed & (1 << relativeDirection)) == 0)
                relativeDirection++;
            if (relativeDirection == 7) relativeDirection = 0;
            direction = (short)(stackObject[ObjectData.Direction] + relativeDirection);
        }

        if (slot.SnapTargetSlot >= 0)
        {
            if ((world.Host is null || stackObject.FootprintSlots.Count > 0) &&
                (uint)slot.SnapTargetSlot >= (uint)stackObject.FootprintSlots.Count)
                throw new SimulationException(SimulationError.BadTargetSlotSpecifiedInRoutingSlot);
            return (stackObject, stackObject.Position, slot.SnapTargetSlot, snapTargetFlag, direction & 7);
        }

        return (null, LotGeometry.SlotPosition(stackObject.Position,
            stackObject[ObjectData.Direction], slot.X, slot.Y), -1, snapTargetFlag, direction & 7);
    }
}
