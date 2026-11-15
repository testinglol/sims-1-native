namespace SimsEngine.Simulation;

/// <summary>The Old Relationship primitive (opcode 0x18, marked "DO NOT USED" in the label table), after Sims.exe
/// FUN_0044d5d3. It copies one value between a relationship list and a tree parameter: the value at index
/// <c>operand[1]</c> of the list the owner A keeps toward target B, and parameter <c>operand[2]</c>.
/// <para>Operand: byte 0, nonzero writes the parameter into the relationship, zero reads the relationship into the
/// parameter (the change notice on a write is not modelled, see below). Byte 1, the value index (signed). Byte 2, the
/// parameter index (signed; error 8 when it is negative or when the frame has no parameters, and inferred: also when it is
/// past the last parameter). Byte 3, the mode (signed): 0 is me to the stack object, 1 the stack object to me, 2 the stack
/// object to parameter 0, 3 parameter 0 to the stack object; any other mode raises error 21. Byte 4, the flags: bit 0 set
/// makes a missing slot false (otherwise the list grows to hold it, with 0s); bit 1 set uses the neighbor records, clear
/// uses the objects' own lists. Bytes 5 to 7 are not read.</para>
/// <para>The records and objects are looked up as in <see cref="RelationshipPrimitive"/>: a mode 0 or 1 with neighbor
/// records casts the running object to a person (error 28), a missing record or object raises error 21, and when both
/// objects are persons the first person's neighbor record and the second person's neighbor index are used.</para>
/// <para>Mode 2 and 3 read parameter 0 without a check in Sims.exe; here a frame without parameters raises error 10
/// (inferred).</para>
/// <para>Writes send the original neighbor dirty notice (FUN_004448a6) before slot lookup for neighbor-record and
/// person paths; ordinary object writes use FUN_0044489b, which is a nullsub.</para>
/// <para>Returns true, or false when the slot is missing and the flags forbid growing.</para></summary>
public static class OldRelationshipPrimitive
{
    private const int ParameterZero = 0;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var write = operand[0] != 0;
        var index = (sbyte)operand[1];
        var parameterIndex = (sbyte)operand[2];
        var mode = (sbyte)operand[3];
        var flags = operand[4];
        if (mode is < 0 or > 3) throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);

        if ((flags & 2) != 0)
        {
            var (record, targetRecord) = NeighborRecords(context, mode);
            return Relate(context, record.Relationships, targetRecord.Id, index, (flags & 1) != 0, write, parameterIndex,
                record, targetRecord);
        }
        var world = context.World;
        var stackObject = context.StackObject;
        XObject? a, b;
        switch (mode)
        {
            case 0:
                a = context.Me;
                b = stackObject;
                break;
            case 1:
                a = stackObject;
                b = context.Me;
                break;
            case 2:
                a = stackObject;
                b = world.ObjectById(FirstParameter(context));
                break;
            default:
                a = world.ObjectById(FirstParameter(context));
                b = stackObject;
                break;
        }
        if (a is null || b is null) throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
        if (RelationshipOperands.IsPersonType(a) && RelationshipOperands.IsPersonType(b))
        {
            var neighbor = ResolveNeighborSlot(world, RelationshipOperands.Person(a)[PersonData.NeighborId])
                ?? throw new SimulationException(SimulationError.NeighborNotFound);
            var targetIndex = RelationshipOperands.Person(b)[PersonData.NeighborId];
            var targetNeighbor = write
                ? ResolveNeighborSlot(world, targetIndex) ?? throw new SimulationException(SimulationError.NeighborNotFound)
                : null;
            return Relate(context, neighbor.Relationships, targetIndex, index, (flags & 1) != 0, write, parameterIndex,
                neighbor, targetNeighbor);
        }
        return Relate(context, a.Relationships, b.Id, index, (flags & 1) != 0, write, parameterIndex);
    }

    private static NeighborState? ResolveNeighborSlot(SimulationWorld world, short key)
    {
        if (key >= 1 && key <= world.NeighborWalkOrder.Count)
            return world.NeighborWalkOrder[key - 1];
        return world.Neighbors.GetValueOrDefault(key);
    }

    // The record modes: the owner's record and the target's id. Modes 0 and 1 cast the running object first.
    private static (NeighborState Record, NeighborState Target) NeighborRecords(in TreeContext context, sbyte mode)
    {
        var world = context.World;
        var stackId = context.Frame.StackObject;
        NeighborState? a, b;
        switch (mode)
        {
            case 0:
            {
                var me = RelationshipOperands.Person(context.Me);
                a = ResolveNeighborSlot(world, me[PersonData.NeighborId]);
                b = ResolveNeighborSlot(world, stackId);
                break;
            }
            case 1:
            {
                var me = RelationshipOperands.Person(context.Me);
                a = ResolveNeighborSlot(world, stackId);
                b = ResolveNeighborSlot(world, me[PersonData.NeighborId]);
                break;
            }
            case 2:
                a = ResolveNeighborSlot(world, stackId);
                b = ResolveNeighborSlot(world, FirstParameter(context));
                break;
            default:
                a = ResolveNeighborSlot(world, FirstParameter(context));
                b = ResolveNeighborSlot(world, stackId);
                break;
        }
        if (a is null || b is null) throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
        return (a, b);
    }

    // The tail: a missing slot is false or grows (as Relationship does), then the parameter is copied to or from the slot.
    // The index is signed, so a negative index reaches the slot access and raises error 7 there (inferred).
    private static PrimitiveResult Relate(in TreeContext context, RelationshipTable table, short target, int index, bool noGrow, bool write, int parameterIndex,
        NeighborState? sourceNeighbor = null, NeighborState? targetNeighbor = null)
    {
        if (write && sourceNeighbor is not null && targetNeighbor is not null)
            context.World.MarkRelationshipsChanged(sourceNeighbor, targetNeighbor);
        if (table.Count(target) <= index)
        {
            if (noGrow) return PrimitiveResult.False;
            table.Resize(target, index + 1);
            if (table.Count(target) <= index) return PrimitiveResult.True;
        }
        var parameters = context.Frame.Parameters;
        if (parameterIndex < 0 || parameterIndex >= parameters.Length)
            throw new SimulationException(SimulationError.StackNumberOutOfRange);
        if (write) table.Set(target, index, parameters[parameterIndex]);
        else parameters[parameterIndex] = (short)table.Get(target, index);
        return PrimitiveResult.True;
    }

    private static short FirstParameter(in TreeContext context) =>
        context.Frame.Parameters.Length > ParameterZero
            ? context.Frame.Parameters[ParameterZero]
            : throw new SimulationException(SimulationError.ReferenceToTreeParameterWhenNoParameterExists);
}
