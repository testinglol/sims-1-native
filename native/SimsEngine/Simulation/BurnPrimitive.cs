namespace SimsEngine.Simulation;

/// <summary>Implements the Burn primitive (opcode 0x09), creating fire on or adjacent to the stack object's
/// tile. Mode 1 offsets the target position using the origin object's directional footprint offset,
/// requiring the tile to lie strictly within lot boundaries and pass footprint clearance checks before
/// spreading.</summary>
public static class BurnPrimitive
{
    /// <summary>Resource GUID for the standard Fire object type (Fire.iff).</summary>
    public const int FireType = 0x24C95F99;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var origin = world.ObjectByStackObject(me, frame.StackObject) ?? throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
        if (world.Host is null)
        {
            if (!SimulationWorld.BurnSourceOnLot(me)) return PrimitiveResult.False;
        }
        else if (!world.Host.BurnEnabled()) return PrimitiveResult.False;
        var mode = operand[0];
        var strictFlags = (operand[1] & 1) == 0;
        var (x, y, height) = origin.Position;
        var direction = origin[ObjectData.Direction];
        if (mode == 1)
        {
            var (dx, dy) = LotGeometry.FootprintOffset(direction);
            x += dx;
            y += dy;
        }
        var tileX = (sbyte)(x >> 4);
        var tileY = (sbyte)(y >> 4);
        var last = LotGeometry.LotDimension - 1;
        if (tileX <= 0 || tileY <= 0 || tileX >= last || tileY >= last) return PrimitiveResult.False;
        if (world.Host is null)
        {
            if (mode == 1)
            {
                if (!world.LotRoomMaps.TryGetValue(height, out var roomMap) ||
                    roomMap.ExpandedWallAt(tileX, tileY) is not { } walls)
                    return PrimitiveResult.False;
                var rotatedBit = SimulationWorld.RotatePlacementWallBit(8u, (direction & 7) / 2);
                if ((walls.UInt32At(0) & rotatedBit) != 0 &&
                    walls.WallCategory(rotatedBit) is not (3 or 5 or 6 or 15 or 23))
                    return PrimitiveResult.False;
                if ((walls.UInt32At(0) & 0x30u) != 0)
                    return PrimitiveResult.False;
            }
            return world.TryBurnTile(new ObjectPosition(x, y, height), strictFlags) ? PrimitiveResult.True : PrimitiveResult.False;
        }
        var host = world.Host;
        var fire = host.FindObjectType(FireType);
        if (mode == 1 && !host.BurnFootprintClear(tileX, tileY, height, direction)) return PrimitiveResult.False;
        return host.BurnSpread(me, new ObjectPosition(x, y, height), fire, strictFlags) ? PrimitiveResult.True : PrimitiveResult.False;
    }
}
