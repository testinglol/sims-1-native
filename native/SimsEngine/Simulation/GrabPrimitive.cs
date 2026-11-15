namespace SimsEngine.Simulation;

/// <summary>The Grab primitive (opcode 4), after Sims.exe 0x451e1a: the running object takes the stack object into its
/// hand. The operand is not read.
/// <para>Sims.exe sends slot argument 0 through FUN_00432771, which stores it in hierarchy/slot-vector entry 1.
/// Entry 0 remains the overlap ancestry slot. An already-held object succeeds, an occupied first real slot or
/// containment cycle fails, and success detaches the item from its former container and moves it with the Sim.</para>
/// <para>The target is resolved through the actor's object table. A missing target answers false. The Sims.exe traces
/// establish these slot semantics; carry animation presentation is
/// still handled by the separate avatar-animation work.</para></summary>
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

        // FUN_00451e1a calls the held object's +0x54 validator with the Sim as relative object
        // and slot argument 0, then calls +0x58 with the same request. +0x130 reserves entry 0,
        // so the apply path stores the object in hierarchy slot 1.
        if (!world.CanPlaceRelativeObject(item, me.Position, me, 0)) return PrimitiveResult.False;
        world.PlaceObject(item, me.Position, context.Stack, me, 1);
        return PrimitiveResult.True;
    }
}
