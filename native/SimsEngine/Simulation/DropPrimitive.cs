namespace SimsEngine.Simulation;

/// <summary>Implements the Drop primitive (opcode 0x05), placing the object from the runner's first
/// container slot onto the footprint tile in front of the runner.</summary>
public static class DropPrimitive
{
    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, _) = context;
        if (me.Slots.Count <= 1) return PrimitiveResult.False;
        var item = world.ObjectByStackObject(me, me.Slots[1]);
        if (item is null) return PrimitiveResult.False;
        var offset = LotGeometry.FootprintOffset(me[ObjectData.Direction]);
        var at = new ObjectPosition(me.Position.X + offset.X, me.Position.Y + offset.Y, me.Position.Height);
        if (world.Host is null)
        {
            if (!world.CanPlaceObject(item, at, context.Stack)) return PrimitiveResult.False;
            world.PlaceObject(item, at, context.Stack);
            return PrimitiveResult.True;
        }

        var host = world.Host;
        if (!host.CanDrop(item, at, null, 0)) return PrimitiveResult.False;
        host.Drop(item, at, null, 0);
        return PrimitiveResult.True;
    }
}
