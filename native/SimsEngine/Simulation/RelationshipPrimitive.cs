namespace SimsEngine.Simulation;

/// <summary>Executes the Relationship primitive (opcode 0x1a) to read or write relationship table values
/// between Sims or objects, flagging the neighborhood matrix dirty on modification.</summary>
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

    private static PrimitiveResult Relate(in TreeContext context, RelationshipTable table, short target, int index, bool noGrow, bool write, DataOwner variableOwner, short data,
        NeighborState? sourceNeighbor = null, NeighborState? targetNeighbor = null)
    {
        // Flags neighborhood relationships dirty prior to slot lookup or expansion.
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
