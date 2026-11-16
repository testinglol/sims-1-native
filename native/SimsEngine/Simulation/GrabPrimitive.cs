namespace SimsEngine.Simulation;

/// <summary>Places the target object into the actor's hand hierarchy slot (slot index 1), detaching it from
/// any previous container and attaching it to the Sim.</summary>
public static class GrabPrimitive
{
    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var item = world.ObjectByStackObject(me, frame.StackObject);
        if (item is null) return PrimitiveResult.False;
        if (world.Host is { } host)
        {
            if (!host.CanGrab(item, me.Position, me, 0)) return PrimitiveResult.False;
            host.Grab(item, me.Position, me, 0);
            return PrimitiveResult.True;
        }

        // Slot 0 is reserved for overlap ancestry; hand placement attaches to slot 1.
        if (!world.CanPlaceRelativeObject(item, me.Position, me, 0)) return PrimitiveResult.False;
        world.PlaceObject(item, me.Position, context.Stack, me, 1);
        return PrimitiveResult.True;
    }
}
