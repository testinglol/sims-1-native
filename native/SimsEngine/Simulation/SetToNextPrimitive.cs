using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>Advances a target variable or the stack object to the next matching item according to the
/// specified search mode. Supports iterating through characters, multi-tile parts, objects sharing a GUID,
/// neighbor slots, tile occupants, and household members.</summary>
public static class SetToNextPrimitive
{
    private static short? FirstFrameWord(StackFrame frame) =>
        frame.Parameters.Length > 0 ? frame.Parameters[0] : frame.Locals.Length > 0 ? frame.Locals[0] : null;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var world = context.World;
        var frame = context.Frame;
        var flags = operand[4];
        var (owner, data) = (flags & 0x80) != 0
            ? ((DataOwner)operand[5], (short)operand[7])
            : (DataOwner.StackObjectId, (short)0);
        var mode = flags & 0x7f;
        var target = Variables.Reference(context, owner, data);
        if (!target.IsShort) throw new SimulationException(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea);
        var guid = operand[0] | operand[1] << 8 | operand[2] << 16 | operand[3] << 24;
        ISimulationCareerHost CareerHost() => world.RequireCareerHost($"Set to Next mode {mode}");
        return mode switch
        {
            1 => WalkPersons(world, target),
            3 => LeadPart(context, target),
            4 => WalkList(world.ObjectsOfType(guid), target),
            5 => WalkNextNeighborSlot(world, target),
            7 => WalkGuid(world, guid, target),
            8 => WalkTile(world, context.Me, target),
            9 => WalkNearTile(world, context.Me, frame, operand[6], target),
            10 => WalkCareers(CareerHost(), target),
            11 => world.Host is { } idHost ? WalkIdTable(idHost.GlobalIdTable(), target) : WalkNeighborIdTable(world, target),
            12 => WalkFamily(world, context.Me, target),
            0 or 2 or 6 => WalkDefault(world, context.Me, frame, target, mode),
            _ => throw new SimulationException(SimulationError.SearchTypeIsInvalid),
        };
    }

    private static XObject? ResolveStackObject(SimulationWorld world, XObject? me, short key)
    {
        if (key == 0) return null;
        if (me is { HasRequestKey: true })
            return me.ObjectTableEntry(key) ?? world.ObjectTableEntry(key);
        var byTable = me?.ObjectTableEntry(key) ?? world.ObjectTableEntry(key);
        if (byTable is { HasRequestKey: true })
            return byTable;
        return (world.ObjectById(key) is { HasRequestKey: false } synthetic ? synthetic : null)
            ?? byTable;
    }

    private static PrimitiveResult LeadPart(in TreeContext context, Variable target)
    {
        var current = context.World.ObjectTableEntry(target.Short) ?? context.World.ObjectByStackObject(context.Me, target.Short);
        if (current is not null && current.Definition[(int)ObjdField.MasterId] != 0)
        {
            // Mode 3 traverses to the next part in a multi-tile object chain, wrapping back to the lead
            // master object at the end.
            var linkedId = current.MultiTileNextId != 0 ? current.MultiTileNextId : current.MultiTileMasterId;
            var lead = linkedId == 0 ? null : context.World.ObjectById(linkedId) ?? context.World.ObjectTableEntry(linkedId);
            if (lead is not null)
            {
                target.Short = lead.StackObjectKey;
                return PrimitiveResult.True;
            }
        }
        target.Short = 0;
        return PrimitiveResult.False;
    }

    /// <summary>Advances the target to the next character on the lot in registration order.</summary>
    private static PrimitiveResult WalkPersons(SimulationWorld world, Variable target)
    {
        var current = target.Short;
        var start = 0;
        if (current != 0)
        {
            var index = -1;
            for (var position = 0; position < world.WalkOrder.Count; position++)
                if (world.WalkOrder[position].StackObjectKey == current)
                {
                    index = position;
                    break;
                }
            if (index < 0 || world.WalkOrder[index] is not XPerson)
            {
                target.Short = 0;
                return PrimitiveResult.False;
            }
            start = index + 1;
        }

        for (var position = start; position < world.WalkOrder.Count; position++)
            if (world.WalkOrder[position] is XPerson person)
            {
                target.Short = person.StackObjectKey;
                return PrimitiveResult.True;
            }
        target.Short = 0;
        return PrimitiveResult.False;
    }

    private static PrimitiveResult WalkList(IReadOnlyList<XObject> order, Variable target)
    {
        var current = target.Short;
        var next = 0;
        if (current != 0)
        {
            var index = -1;
            for (var i = 0; i < order.Count; i++)
            {
                if (order[i].StackObjectKey == current)
                {
                    index = i;
                    break;
                }
            }
            next = index < 0 ? order.Count : index + 1;
        }
        if (next >= order.Count)
        {
            target.Short = 0;
            return PrimitiveResult.False;
        }
        target.Short = order[next].StackObjectKey;
        return target.Short != 0 ? PrimitiveResult.True : PrimitiveResult.False;
    }

    /// <summary>Mode 7 advances through the neighbor roster to find the next neighbor matching the
    /// specified GUID, including off-lot characters.</summary>
    private static PrimitiveResult WalkGuid(SimulationWorld world, int guid, Variable target)
    {
        if (target.Short < 0)
        {
            target.Short = 0;
            return PrimitiveResult.False;
        }
        for (var slot = (int)target.Short + 1; slot <= world.NeighborWalkOrder.Count; slot++)
        {
            if (world.NeighborWalkOrder[slot - 1] is not { } neighbor || neighbor.TypeRecordKey != guid) continue;
            target.Short = checked((short)slot);
            return PrimitiveResult.True;
        }
        target.Short = 0;
        return PrimitiveResult.False;
    }

    private static PrimitiveResult WalkNextNeighborSlot(SimulationWorld world, Variable target)
    {
        // Advances to the next occupied neighbor slot while preserving gaps in the neighbor table.
        if (target.Short < 0)
        {
            target.Short = 0;
            return PrimitiveResult.False;
        }
        for (var slot = (int)target.Short + 1; slot <= world.NeighborWalkOrder.Count; slot++)
        {
            if (world.NeighborWalkOrder[slot - 1] is null) continue;
            target.Short = checked((short)slot);
            return PrimitiveResult.True;
        }
        target.Short = 0;
        return PrimitiveResult.False;
    }

    private static PrimitiveResult WalkNeighborIdTable(SimulationWorld world, Variable target)
    {
        var neighbors = world.NeighborWalkOrder;
        var startSlot = 1;
        if (target.Short != 0)
        {
            if (target.Short < 1 || target.Short > neighbors.Count || neighbors[target.Short - 1] is null)
                return PrimitiveResult.False;
            startSlot = target.Short + 1;
        }
        for (var slot = startSlot; slot <= neighbors.Count; slot++)
        {
            if (neighbors[slot - 1] is null) continue;
            target.Short = unchecked((short)slot);
            return PrimitiveResult.True;
        }
        return PrimitiveResult.False;
    }

    private static PrimitiveResult WalkIdTable(IReadOnlyList<int> table, Variable target)
    {
        var index = 0;
        if (target.Short != 0)
        {
            index = -1;
            for (var i = 0; i < table.Count; i++)
            {
                if (table[i] == target.Short)
                {
                    index = i + 1;
                    break;
                }
            }
            if (index < 0) return PrimitiveResult.False;
        }
        if (index >= table.Count) return PrimitiveResult.False;
        target.Short = unchecked((short)table[index]);
        return PrimitiveResult.True;
    }

    private static PrimitiveResult WalkCareers(ISimulationCareerHost host, Variable target)
    {
        var table = host.CareerTable();
        var index = -1;
        for (var i = 0; i < table.Count; i++)
        {
            if (table[i] == target.Short)
            {
                index = i;
                break;
            }
        }
        var next = index + 1;
        if (next >= table.Count) return PrimitiveResult.False;
        target.Short = unchecked((short)table[next]);
        return PrimitiveResult.True;
    }

    private static PrimitiveResult WalkDefault(SimulationWorld world, XObject me, StackFrame frame, Variable target, int mode)
    {
        var order = world.WalkOrder;
        var next = 0;
        if (target.Short != 0)
        {
            var resolved = ResolveStackObject(world, me, target.Short);
            var current = resolved is null ? -1 : order.FindIndex(item => ReferenceEquals(item, resolved));
            if (current < 0) return PrimitiveResult.False;
            next = current + 1;
        }
        for (var i = next; i < order.Count; i++)
        {
            var candidate = order[i];
            var take = mode switch
            {
                0 => true,
                2 => candidate.Definition[(int)ObjdField.Type] != XPerson.PersonType,
                _ => FirstFrameWord(frame) is { } word && candidate[ObjectData.Category] == word,
            };
            if (take)
            {
                target.Short = candidate.StackObjectKey;
                return PrimitiveResult.True;
            }
        }
        return PrimitiveResult.False;
    }

    private static PrimitiveResult WalkFamily(SimulationWorld world, XObject me, Variable target)
    {
        if (target.Short == 0) return PrimitiveResult.False;
        var reference = ResolveStackObject(world, me, target.Short) as XPerson
            ?? throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
        var family = reference[PersonData.FamilyNumber];
        var order = world.WalkOrder;
        var found = order.FindIndex(item => ReferenceEquals(item, reference));
        if (found < 0) return PrimitiveResult.False;
        for (var step = 1; step < order.Count; step++)
        {
            var candidate = order[(found + step) % order.Count];
            if (candidate is XPerson person && person[PersonData.FamilyNumber] == family)
            {
                target.Short = candidate.StackObjectKey;
                return PrimitiveResult.True;
            }
        }
        return PrimitiveResult.False;
    }

    private static PrimitiveResult WalkTile(SimulationWorld world, XObject me, Variable target)
    {
        if (target.Short == 0) return PrimitiveResult.False;
        var current = ResolveStackObject(world, me, target.Short);
        if (current is null || !SimulationWorld.BurnSourceOnLot(current)) return PrimitiveResult.False;
        var tileX = (sbyte)(current.Position.X >> 4);
        var tileY = (sbyte)(current.Position.Y >> 4);
        var level = (sbyte)current.Position.Height;
        XObject? first = null;
        var seenCurrent = false;
        foreach (var item in world.ObjectsAtTile(tileX, tileY, level))
        {
            first ??= item;
            if (seenCurrent)
            {
                target.Short = item.StackObjectKey;
                return PrimitiveResult.True;
            }
            if (ReferenceEquals(item, current) || item.StackObjectKey == target.Short)
                seenCurrent = true;
        }
        if (seenCurrent && first is not null)
        {
            target.Short = first.StackObjectKey;
            return PrimitiveResult.True;
        }
        return PrimitiveResult.False;
    }

    private static PrimitiveResult WalkNearTile(SimulationWorld world, XObject me, StackFrame frame, byte local, Variable target)
    {
        if (local >= frame.Locals.Length)
            throw new SimulationException(SimulationError.StackNumberOutOfRange);
        var anchor = ResolveStackObject(world, me, frame.Locals[local])
            ?? throw new SimulationException(SimulationError.ReferenceToTreeParameterWhenNoParameterExists);
        var baseX = anchor.Position.X >> 4;
        var baseY = anchor.Position.Y >> 4;
        var room = anchor.RoomWord84;
        var runner = ResolveStackObject(world, me, target.Short);
        sbyte stepX, stepY;
        bool first;
        if (runner is null)
        {
            stepX = 1;
            stepY = 0;
            first = true;
        }
        else
        {
            // Calculates tile step deltas with 8-bit wraparound interpreted as signed byte offsets.
            stepX = unchecked((sbyte)((runner.Position.X >> 4) - baseX));
            stepY = unchecked((sbyte)((runner.Position.Y >> 4) - baseY));
            first = false;
        }
        var size = LotGeometry.LotDimension;
        while (true)
        {
            if (!first)
            {
                if (stepX > 0) { stepX = 0; stepY = 1; }
                else if (stepX < 0) { stepX = 0; stepY = -1; }
                else if (stepY > 0) { stepX = -1; stepY = 0; }
                else return PrimitiveResult.False;
            }
            first = false;
            var tileX = unchecked((byte)(baseX + stepX));
            var tileY = unchecked((byte)(baseY + stepY));
            if (tileX > 127 || tileY > 127 || tileX >= size || tileY >= size) continue;
            var value = world.TileValueAt(tileX, tileY, unchecked((byte)anchor.Position.Height));
            if (value <= 0) continue;
            var entry = me.ObjectTableEntry(unchecked((short)value)) ?? world.ObjectTableEntry(unchecked((short)value));
            if (entry is null || entry.RoomWord84 != room) continue;
            target.Short = entry.StackObjectKey;
            return PrimitiveResult.True;
        }
    }
}
