namespace SimsEngine.Simulation;

/// <summary>Executes the legacy Old Relationship primitive (opcode 0x18) to transfer values between
/// relationship records and tree parameters across specified owner and target modes.</summary>
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

    // Copies values between the parameter and relationship slot, expanding the table when growth is
    // permitted.
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
