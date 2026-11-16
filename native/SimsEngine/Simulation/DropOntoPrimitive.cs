namespace SimsEngine.Simulation;

/// <summary>Implements the Drop Onto primitive (opcode 0x2b), transferring the stack object into a slot of
/// the target object held within one of the runner's slots. Operand words encode slot number and placement
/// flags as literal values or frame parameter indices.</summary>
public static class DropOntoPrimitive
{
    private static readonly ObjectPosition Place = new(-16, -16, 1);

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var slot = Value(frame, operand, 0);
        var flag = Value(frame, operand, 4);
        var slotVectorIndex = slot + 1; // Slot index 0 is reserved for the overlap ancestry chain.
        if (slot < 0 || slotVectorIndex >= me.Slots.Count) throw new SimulationException(SimulationError.SlotNumberOutOfRange);
        var stackObject = world.ObjectByStackObject(me, frame.StackObject) ?? throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
        var key = me.Slots[slotVectorIndex];
        if (key == 0) return PrimitiveResult.False;
        var target = world.ObjectByStackObject(me, key) ?? throw new InvalidOperationException($"Slot {slot} of object {me.Id} holds manager key {key}, which is not on the lot.");
        if (world.Host is { } host)
        {
            if (!host.CanDropOnto(target, Place, stackObject, flag)) return PrimitiveResult.False;
            host.DropOnto(target, Place, stackObject, flag);
            return PrimitiveResult.True;
        }

        if (!world.CanPlaceRelativeObject(target, Place, stackObject, flag))
            return PrimitiveResult.False;
        world.PlaceObject(target, Place, context.Stack, stackObject, checked((short)(flag + 1)));
        return PrimitiveResult.True;
    }

    /// <summary>Resolves an operand word pair as a literal value if selector is 0, or from the specified
    /// frame parameter index.</summary>
    private static short Value(StackFrame frame, ReadOnlySpan<byte> operand, int at)
    {
        var selector = (short)(operand[at] | operand[at + 1] << 8);
        var value = (short)(operand[at + 2] | operand[at + 3] << 8);
        if (selector == 0) return value;
        if (value < 0 || value >= frame.Parameters.Length) throw new SimulationException(SimulationError.StackNumberOutOfRange);
        return frame.Parameters[value];
    }
}
