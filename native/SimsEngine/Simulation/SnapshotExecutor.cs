namespace SimsEngine.Simulation;

internal static class SnapshotExecutor
{
    public static PrimitiveResult PlaceAtFreePosition(SimulationWorld world, XObject me, ObjectPosition position,
        int directionMask, bool bypassPlacementChecks, TreeStack? checkStack = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(me);
        var x = position.X >> 4;
        var y = position.Y >> 4;
        var size = world.ActiveLotDimension;
        if (!bypassPlacementChecks && (x <= 0 || y <= 0 || x >= size - 1 || y >= size - 1))
        {
            world.LastPlacementFailureCode = 1;
            return PrimitiveResult.False;
        }
        if (!world.LotRoomMaps.TryGetValue(position.Height, out var map))
            return PrimitiveResult.False;
        if (!bypassPlacementChecks && !SameRoomAndUnblocked(world, map, me, position)) return PrimitiveResult.False;

        if (!world.CanPlaceRouteCandidate(me, position, null, checkStack: checkStack))
            return PrimitiveResult.False;
        world.PlaceObject(me, position, checkStack);
        me[ObjectData.Direction] = (short)(directionMask & 7);
        CompletePlacement(me);
        return PrimitiveResult.True;
    }

    public static PrimitiveResult PlaceAtAnchorPosition(SimulationWorld world, XObject me, XObject target,
        ObjectPosition position, int directionMask, bool checkWallFootprint, TreeStack? checkStack = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(me);
        ArgumentNullException.ThrowIfNull(target);
        var x = position.X >> 4;
        var y = position.Y >> 4;
        var size = world.ActiveLotDimension;
        if (x <= 0 || y <= 0 || x >= size - 1 || y >= size - 1)
            return PrimitiveResult.False;
        if (!world.LotRoomMaps.TryGetValue(position.Height, out var map))
            return PrimitiveResult.False;
        if (!SameRoomAndUnblocked(world, map, me, position)) return PrimitiveResult.False;

        var occupant = world.TileValueAt(x, y, position.Height);
        if (occupant != 0 && occupant != target.ClockManagerIndex && occupant != me.ClockManagerIndex)
            return PrimitiveResult.False;

        if (checkWallFootprint && !PassesSlotWallCheck(world, me, position, directionMask))
            return PrimitiveResult.False;

        world.PlaceObject(me, position, checkStack);
        me[ObjectData.Direction] = (short)(directionMask & 7);
        CompletePlacement(me);
        return PrimitiveResult.True;
    }

    public static PrimitiveResult PlaceInFootprintSlot(SimulationWorld world, XObject me, XObject target, int slotIndex,
        int directionMask, bool bypassPlacementChecks, bool checkWallFootprint, TreeStack? checkStack = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(me);
        ArgumentNullException.ThrowIfNull(target);
        if (!bypassPlacementChecks)
        {
            var targetX = target.Position.X >> 4;
            var targetY = target.Position.Y >> 4;
            var size = world.ActiveLotDimension;
            if (targetX <= 0 || targetY <= 0 || targetX >= size - 1 || targetY >= size - 1)
            {
                world.LastPlacementFailureCode = 1;
                return PrimitiveResult.False;
            }
            if (!world.LotRoomMaps.TryGetValue(target.Position.Height, out var map) ||
                !SameRoomAndUnblocked(world, map, me, target.Position))
                return PrimitiveResult.False;
        }
        else if (!world.LotRoomMaps.TryGetValue(target.Position.Height, out _))
        {
            return PrimitiveResult.False;
        }
        if ((uint)slotIndex >= target.FootprintSlots.Count)
        {
            world.LastPlacementFailureCode = 0x0d;
            return PrimitiveResult.False;
        }

        if (!world.CanPlaceRouteCandidate(me, target.Position, target, slotIndex))
            return PrimitiveResult.False;
        if (checkWallFootprint && !PassesSlotWallCheck(world, me, target.Position, directionMask))
        {
            world.LastPlacementFailureCode = 7;
            me[ObjectData.PrimitiveResultId] = 0;
            return PrimitiveResult.False;
        }
        world.PlaceObject(me, target.Position, checkStack, target, checked((short)(slotIndex + 1)));
        me[ObjectData.Direction] = (short)(directionMask & 7);
        CompletePlacement(me);
        return PrimitiveResult.True;
    }

    private static void CompletePlacement(XObject me)
    {
        if (me is XPerson person)
        {
            person.RefreshFootprintBounds();
            person.AnimatorRootX = null;
            person.AnimatorRootY = null;
            person.AnimatorRootDirection = null;
        }
        else if (me.TypeRecord is not null)
        {
            me.FootprintBounds = SimulationWorld.PlacementFootprintBounds(me, me.Position);
            me.FootprintBoundsDirection = me[ObjectData.Direction];
        }
        me[ObjectData.PrimitiveResult] = 0;
    }

    private static bool SameRoomAndUnblocked(SimulationWorld world, SimsEngine.World.RoomMap map,
        XObject me, ObjectPosition destination)
    {
        var toX = destination.X >> 4;
        var toY = destination.Y >> 4;
        var destinationRoom = map.RoomAt(toX, toY);
        if (destinationRoom == SimsEngine.World.RoomMap.NoRoom ||
            me[ObjectData.Room] != unchecked((short)destinationRoom))
            return false;
        var fromX = me.Position.X >> 4;
        var fromY = me.Position.Y >> 4;
        var size = world.ActiveLotDimension;
        if (fromX <= 0 || fromY <= 0 || fromX >= size - 1 || fromY >= size - 1)
            return true;
        if (me.Position.X == destination.X && me.Position.Y == destination.Y) return true;

        var direction = RouteExecutor.DirectionToWaypoint((me.Position.X, me.Position.Y),
            (destination.X, destination.Y));
        return !world.BlocksRouteDirectionAt(map, fromX, fromY, direction) &&
               !world.BlocksRouteDirectionAt(map, toX, toY, (direction + 4) & 7);
    }

    private static bool PassesSlotWallCheck(SimulationWorld world, XObject me, ObjectPosition destination,
        int directionMask)
    {
        var previousDirection = me[ObjectData.Direction];
        FootprintBounds proposedBounds;
        try
        {
            me[ObjectData.Direction] = (short)(directionMask & 7);
            proposedBounds = me is XPerson person
                ? person.FootprintBoundsAt(destination)
                : SimulationWorld.PlacementFootprintBounds(me, destination);
        }
        finally
        {
            me[ObjectData.Direction] = previousDirection;
        }
        return !world.FootprintIntersectsWall(proposedBounds, destination.Height);
    }
}
