namespace SimsEngine.Simulation;

/// <summary>The Burn primitive (opcode 9), after Sims.exe 0x44d9af (traced from the decompilation; the lot side is
/// the host's). It starts a fire near the stack object: the Fire object type (key 0x24C95F99) is placed on the tile of
/// the stack object, or on the tiles of the burnable objects beside it.
/// <para>Operand: byte 0 is the mode. Mode 1 moves the position by the origin's footprint offset (the origin's
/// Direction field, <see cref="ObjectData.Direction"/>, through <see cref="LotGeometry.FootprintOffset"/>); any other
/// mode uses the position as it is. Byte 1 bit 0 clear is the strict flag check of the spread.</para>
/// <para>Order: a missing stack object raises "No stack object present when required." (21) before anything else. Then
/// the gate (<see cref="ISimulationHost.BurnEnabled"/>, FUN_00432644) answers false when it is closed, and only then is
/// the Fire type looked up (<see cref="ISimulationHost.FindObjectType"/>, FUN_0043acf5). The position is the origin's X
/// and Y (and Height), with the footprint offset in mode 1. The tile is the position divided by 16, as a signed byte.
/// The tile must lie strictly inside the lot: above zero and below the lot's size minus one, on both axes. Otherwise the
/// primitive answers false. Mode 1 then asks <see cref="ISimulationHost.BurnFootprintClear"/> (FUN_00479737 and the
/// direction mask; not traced), and answers false when the tile does not pass. The spread is left to
/// <see cref="ISimulationHost.BurnSpread"/>, which answers the primitive's result.</para>
/// <para>Inferred: Sims.exe's tile is a C char, so a position beyond the signed byte range wraps (kept by the cast).</para></summary>
public static class BurnPrimitive
{
    /// <summary>The Fire object type's key (Fire.iff 0x24C95F99).</summary>
    public const int FireType = 0x24C95F99;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var origin = world.ObjectByStackObject(me, frame.StackObject) ?? throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
        // FUN_00432644 gates on the running object's effective tile. The hostless world has the same
        // source-backed interior test; the host supplies the remaining global gate when present.
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
