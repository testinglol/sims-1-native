namespace SimsEngine.Simulation;

/// <summary>The Drop Onto primitive (opcode 0x2b), after Sims.exe 0x44e9aa: the stack object is dropped onto the object in
/// one of the running object's slots.
/// <para>Operand: two selector/value word pairs. Word 0 is the selector and word 1 the value of the slot number; word 2 and
/// word 3 are the same for the flag. A selector of 0 makes the value itself; any other selector reads the frame's
/// parameter at that index, and an index outside the frame's parameters raises "Stack number out of range." (8).</para>
/// <para>Order: the slot number must name a slot of the running object (slot n is the slot vector's entry n + 1), or the
/// error "Slot number out of range." (27). The stack object must exist ("No stack object present when required.", 21).
/// The slot's id is read: 0 answers false. Otherwise the object with that id gets its check
/// (<see cref="ISimulationHost.CanDropOnto"/>, vtable +0x54) with the position (-16, -16) at height 1, the stack object and
/// the flag; a refusal answers false. The action (<see cref="ISimulationHost.DropOnto"/>, vtable +0x58) then runs and the
/// primitive answers true.</para>
/// <para>Inferred: the position and height are the literal values Sims.exe pushes, and the flag is the operand's flag word
/// as read. A slot holding an id that is not on the lot raises <see cref="InvalidOperationException"/>; Sims.exe calls the
/// virtual on a null pointer there (not traced).</para></summary>
public static class DropOntoPrimitive
{
    private static readonly ObjectPosition Place = new(-16, -16, 1);

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var slot = Value(frame, operand, 0);
        var flag = Value(frame, operand, 4);
        var slotVectorIndex = slot + 1; // cXObject +0x130 reserves entry 0 for the overlap ancestry chain.
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

        // FUN_0044e9aa sends the selected object's +0x54 callback the held object as its relative
        // object. FUN_004333ef then applies placement to its receiver, with the held object as
        // container and hierarchy slot flag + 1.
        if (!world.CanPlaceRelativeObject(target, Place, stackObject, flag))
            return PrimitiveResult.False;
        world.PlaceObject(target, Place, context.Stack, stackObject, checked((short)(flag + 1)));
        return PrimitiveResult.True;
    }

    /// <summary>The word pair at <paramref name="at"/>: a selector of 0 gives the value; otherwise the frame's parameter at
    /// the value (8 when it is outside the parameters).</summary>
    private static short Value(StackFrame frame, ReadOnlySpan<byte> operand, int at)
    {
        var selector = (short)(operand[at] | operand[at + 1] << 8);
        var value = (short)(operand[at + 2] | operand[at + 3] << 8);
        if (selector == 0) return value;
        if (value < 0 || value >= frame.Parameters.Length) throw new SimulationException(SimulationError.StackNumberOutOfRange);
        return frame.Parameters[value];
    }
}
