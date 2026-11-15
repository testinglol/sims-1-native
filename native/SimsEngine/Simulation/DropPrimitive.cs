namespace SimsEngine.Simulation;

/// <summary>The Drop primitive (opcode 5), after Sims.exe 0x454470: the running object puts the object in its first slot
/// down on the footprint in front of it. The operand is not read.
/// <para>Order: the held object is the first real slot (<see cref="XObject.Slots"/> index 1; entry 0 is the reserved
/// overlap-chain slot). Sims.exe reads the first real +0x130 entry. No slot, or a manager key that does not resolve on
/// the lot, answers false. The footprint is the running object's
/// position plus <see cref="LotGeometry.FootprintOffset"/> of its direction (<see cref="ObjectData.Direction"/>). The
/// height is the running object's. With a legacy host, the held object's check (<see cref="ISimulationHost.CanDrop"/>,
/// vtable +0x54) is asked with that position, no other object and flag 0; a refusal answers false, then
/// <see cref="ISimulationHost.Drop"/> (vtable +0x58) runs. A hostless native world uses its placement validator and
/// placement transition for those same two steps.</para>
/// <para>Inferred: the two virtual slots are a can-be-placed check and the placement, from their order and arguments.</para></summary>
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
