namespace SimsEngine.Simulation;

/// <summary>The Relationship primitive (opcode 0x1a), after Sims.exe FUN_0044d1bc. It reads or writes one value of a
/// relationship list: the value at index <c>operand[0]</c> of the list owner A keeps toward target B. Owner and target
/// come from <c>operand[1]</c>, a mode 0 to 3 that picks the running object ("me"), the stack object, or the object
/// named by a local (modes 2 and 3). Object-list modes resolve stack/local indices directly through the lot object's
/// one-based manager vector, as `FUN_00407498` does, rather than through the running object's stack-object table.
/// <para>Operand: byte 0, the value index (unsigned). Byte 1, the mode: 0 is me to the stack object, 1 the stack object to
/// me, 2 the stack object to the neighbor named by local <c>operand[3]</c>, 3 that neighbor to the stack object; any other
/// mode raises error 21. Byte 2, the flags: bit 0 set makes a missing slot false (otherwise the list grows to hold it, with
/// 0s); bit 1 set uses the neighbor records (a neighbor-record list), clear uses the objects' own lists; bit 2 set writes
/// the variable into the relationship, clear reads the relationship into the variable. Byte 3, the local index for modes 2
/// and 3 (error 51 when it is out of range). Byte 4, the variable's owner (<see cref="DataOwner"/>). Byte 5 is not read.
/// Bytes 6 and 7, the variable's data (a short).</para>
/// <para>Modes with neighbor records: the list is the record's, and the target is the record's id; a mode 0 or 1 first
/// casts the running object to a person (error 28 when it is not one). A missing neighbor record raises error 21.</para>
/// <para>Modes with objects: the list is the object's, and the target is the object's id. When both objects are persons,
/// Sims.exe uses the first person's neighbor record instead, with the second person's neighbor index as the target (the
/// person path). A missing neighbor record there raises error 55 (inferred: Sims.exe does not check it and reads from
/// address 0xc).</para>
/// <para>When bit 2 is set, the native path raises the same neighbor dirty notifications as
/// FUN_004448a6/Neighborhood::RelationshipsChanged before slot lookup; object-list writes use the no-op
/// FUN_0044489b path. A float variable (a motive) cannot take the 16-bit write Sims.exe makes, so a read into one
/// raises error 19 (inferred).</para>
/// <para>Returns true or false, as the original does; it raises errors the original throws.</para></summary>
public static class RelationshipPrimitive
{
    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var index = operand[0];
        var mode = operand[1];
        var flags = operand[2];
        var localIndex = operand[3];
        var variableOwner = (DataOwner)operand[4];
        var data = (short)(operand[6] | operand[7] << 8);
        var noGrow = (flags & 1) != 0;
        var write = (flags & 4) != 0;
        if (mode > 3) throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);

        if ((flags & 2) != 0)
        {
            var (record, targetRecord) = NeighborRecords(context, mode, localIndex);
            return Relate(context, record.Relationships, targetRecord.Id, index, noGrow, write, variableOwner, data,
                record, targetRecord);
        }
        var world = context.World;
        XObject? a, b;
        switch (mode)
        {
            case 0:
                a = context.Me;
                b = world.ClockManager.ObjectAt(context.Frame.StackObject);
                break;
            case 1:
                a = world.ClockManager.ObjectAt(context.Frame.StackObject);
                b = context.Me;
                break;
            case 2:
            {
                var local = Local(context, localIndex);
                b = world.ClockManager.ObjectAt(local);
                a = world.ClockManager.ObjectAt(context.Frame.StackObject);
                break;
            }
            default:
            {
                var local = Local(context, localIndex);
                a = world.ClockManager.ObjectAt(local);
                b = world.ClockManager.ObjectAt(context.Frame.StackObject);
                break;
            }
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
            return Relate(context, neighbor.Relationships, targetIndex, index, noGrow, write, variableOwner, data,
                neighbor, targetNeighbor);
        }
        return Relate(context, a.Relationships, b.Id, index, noGrow, write, variableOwner, data);
    }

    private static NeighborState? ResolveNeighborSlot(SimulationWorld world, short key)
    {
        if (key >= 1 && key <= world.NeighborWalkOrder.Count)
            return world.NeighborWalkOrder[key - 1];
        return world.Neighbors.GetValueOrDefault(key);
    }

    // The neighbor-record modes resolve one-based manager slots (FUN_0042573e), then use the target record ID. Modes 0 and 1 cast the running object first.
    private static (NeighborState Record, NeighborState Target) NeighborRecords(in TreeContext context, byte mode, byte localIndex)
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
            {
                a = ResolveNeighborSlot(world, stackId);
                b = ResolveNeighborSlot(world, Local(context, localIndex));
                break;
            }
            default:
            {
                a = ResolveNeighborSlot(world, Local(context, localIndex));
                b = ResolveNeighborSlot(world, stackId);
                break;
            }
        }
        if (a is null || b is null) throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
        return (a, b);
    }

    // The tail both forms share: the slot at index (grown when allowed), then the variable is written or read.
    private static PrimitiveResult Relate(in TreeContext context, RelationshipTable table, short target, int index, bool noGrow, bool write, DataOwner variableOwner, short data,
        NeighborState? sourceNeighbor = null, NeighborState? targetNeighbor = null)
    {
        // FUN_0044d1bc notifies the neighborhood before slot lookup/growth when writing a neighbor-record relation.
        if (write && sourceNeighbor is not null && targetNeighbor is not null)
            context.World.MarkRelationshipsChanged(sourceNeighbor, targetNeighbor);
        if (table.Count(target) <= index)
        {
            if (noGrow) return PrimitiveResult.False;
            table.Resize(target, index + 1);
            if (table.Count(target) <= index) return PrimitiveResult.True;
        }
        var variable = Variables.Reference(context, variableOwner, data);
        if (write)
        {
            table.Set(target, index, variable.Value);
            return PrimitiveResult.True;
        }
        if (!variable.IsShort) throw new SimulationException(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea);
        variable.Short = (short)table.Get(target, index);
        return PrimitiveResult.True;
    }

    private static short Local(in TreeContext context, byte index) =>
        index < context.Frame.Locals.Length ? context.Frame.Locals[index] : throw new SimulationException(SimulationError.LocalOutOfRange);
}
