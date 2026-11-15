using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>The Set to Next primitive (opcode 0x1f), after Sims.exe FUN_0044ebc0 (no decompilation; read from the
/// disassembly at 0x44ebc0, with the mode jump table at 0x44f29f). Byte 4 of the operand holds a mode in its low
/// seven bits and a flag in bit 7. With the flag, the target is the variable named by owner byte 5 and data byte 7
/// (both unsigned). Without it, the target is the stack object id (owner 10, data 0). The target is taken as a
/// reference first. Anything but a short raises "Expression attempting to write in a read-only area." (19),
/// before the mode is looked at.
/// <para>Mode 1 walks the lot persons in <see cref="SimulationWorld.WalkOrder"/> (the +0x84 vector). Mode 3 follows the current multitile
/// object's +0x18c next link, or its +0x190 master link when there is no next link. Mode 4 walks native manager-order objects sharing the type key resolved from
/// operand bytes 0 to 3, starting after the current target. Mode 5 advances the target
/// to the next occupied neighbor-manager slot (0 at the end). Mode 7 walks the table ids after the current target
/// for the first object whose native type-record key (<see cref="TypeRecord.Key"/>) matches the operand GUID.
/// Mode 8 walks the walk order for the next object on the target's tile (same tile coordinates and level); with
/// none it answers false and keeps the target. Mode 9 walks the tiles around the frame local named by operand
/// byte 6 (error 8 for a bad index, error 10 for a missing object): from the neighbor the current target points
/// at (or east when the target names nothing), it takes the first tile (bounded by
/// source-defined 64-tile lot dimension (<see cref="LotGeometry.LotDimension"/>) whose object
/// table entry (<see cref="XObject.ObjectTableEntry"/>) shares the local object's room word
/// (<see cref="XObject.RoomWord84"/>); a miss answers false and keeps the target. Mode 10 walks the career table
/// (<see cref="ISimulationHost.CareerTable"/>) past the current target; a missing entry takes the first entry,
/// the end answers false and keeps the target. Mode 11 walks the global id table
/// (<see cref="ISimulationHost.GlobalIdTable"/>) past the current target; a missing entry or the end answers
/// false and keeps the target. A target of 0 starts the walks at the first entry (modes 8 and 10 excepted:
/// 8 needs its tile, 10 looks the zero up like any other id); an id not on the list ends the walk, except
/// mode 10, which takes the first entry. The target takes the id of the object found, or 0 when there
/// is none (except modes 8, 9, 10, 11, 12 and the target-value dispatch, which keep it), and the primitive answers true when the new value is not 0. Mode 12 walks the walk order past the current target for the first person with the same family number; a zero target answers false, a non-person or missing target raises error 28, and the walk wraps around the list.</para>
/// <para>Inferred: the walks follow the list order (the +0xe0 chain, the tile scan order and the slot table's
/// 1-based indexing all read as the walk order), the object id is the object's +0xf0, mode 8's scan-state
/// scratch copy is unobservable bookkeeping, a mode-9 target that names nothing starts east of the local object,
/// the local index is operand byte 6 (shipped byte-6 values are 0 to 2), the +0xfc tile bytes read as the
/// position shifted right by 4 with the level (see <see cref="Formats.ObjectHead"/>; the dead [ebp-0x1a] write is
/// not modelled), the indexed +0x18 table's entry reads through <see cref="XObject.ObjectTableEntry"/>,
/// FUN_0044752FC returns the frame's locals, and a mode-12 target missing from the walk answers false instead of
/// following the exe's frame-pointer read. Not modelled: the me+0x128 zeroing in the prologue. Modes 0, 2 and 6
/// share the target-value dispatch: the walk starts after the target's slot (at the first entry for target 0)
/// and mode 0 takes the first entry, mode 2 the first non-person, and mode 6 the first whose +0xc0
/// (`ObjectData.Category`, the short at cXObject +0xc0) matches the frame's first word (FUN_004752f8 is frame +0x10, the first parameter); a miss answers false and keeps the
/// target, and anything else is error 30 (<see cref="SimulationError.SearchTypeIsInvalid"/>).</para></summary>
public static class SetToNextPrimitive
{
    // FUN_004752f8 returns frame +0x10, where the parameters start and the locals follow them.
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
            8 => WalkTile(world, target),
            9 => WalkNearTile(world, context.Me, frame, operand[6], target),
            10 => WalkCareers(CareerHost(), target),
            11 => WalkIdTable(world.Host is { } idHost ? idHost.GlobalIdTable() : NeighborIdTable(world), target),
            12 => WalkFamily(world, target),
            0 or 2 or 6 => WalkDefault(world, frame, target, mode),
            // Anything else is a mode above 12 (operands are masked to 0-127, so negatives are impossible).
            _ => throw new SimulationException(SimulationError.SearchTypeIsInvalid),
        };
    }

    private static PrimitiveResult LeadPart(in TreeContext context, Variable target)
    {
        var current = context.World.ObjectTableEntry(target.Short) ?? context.World.ObjectByStackObject(context.Me, target.Short);
        if (current is not null && current.Definition[(int)ObjdField.MasterId] != 0)
        {
            // FUN_0044ebc0 mode 3 (0x44ecb9-0x44ed03) takes cXMTObject +0x18c when nonzero, else +0x190,
            // then stores the selected object's +0xf0 id.
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

    /// <summary>Mode 1 (0x44eff4) advances through the table's +0x84 object-pointer vector, which holds the lot's
    /// persons: the shipped trees that follow it read StackObject person data, motives, relationships and
    /// neighborhood data, and the target-value modes 0/2/6 walk the separate +0xe0 object chain instead. Inferred:
    /// the vector's order is the registration order of the walk's persons.</summary>
    private static PrimitiveResult WalkPersons(SimulationWorld world, Variable target)
    {
        var current = target.Short;
        var start = 0;
        if (current != 0)
        {
            var index = -1;
            for (var position = 0; position < world.WalkOrder.Count; position++)
                if (world.WalkOrder[position].Id == current)
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
            if (world.WalkOrder[position] is XPerson)
            {
                target.Short = world.WalkOrder[position].Id;
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
                if (order[i].Id == current)
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
        target.Short = order[next].Id;
        return target.Short != 0 ? PrimitiveResult.True : PrimitiveResult.False;
    }

    /// <summary>Mode 7 (Mac cXObject::TrySetToNext 0x100EBC3C) steps Neighborhood::GetNextNeighborID
    /// from the target and takes the first neighbor whose ObjSelector GUID equals the operand GUID. It walks the neighbor
    /// vector, not the lot's objects, so service NPCs (maid, gardener, pizza) that are neighbors but not on the lot are found.
    /// The walk ends with the target 0 and false.</summary>
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
        // Sims.exe FUN_0044ebc0 mode 5 calls FUN_00425b5f on the lot manager's neighbor
        // manager (+0x24). That helper starts after the supplied 1-based slot and returns
        // the next non-null slot, preserving holes in the vector.
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

    /// <summary>Mac Neighborhood::GetNextNeighborID (called by TrySetToNext for this mode) returns the next one-based
    /// index of a non-null entry in the neighbor vector; Sims.exe 0x44ebc0 walks the equivalent id vector.</summary>
    private static IReadOnlyList<int> NeighborIdTable(SimulationWorld world)
    {
        var ids = new List<int>();
        for (var i = 0; i < world.NeighborWalkOrder.Count; i++)
            if (world.NeighborWalkOrder[i] is not null) ids.Add(i + 1);
        return ids;
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

    private static PrimitiveResult WalkDefault(SimulationWorld world, StackFrame frame, Variable target, int mode)
    {
        var order = world.WalkOrder;
        var next = 0;
        if (target.Short != 0)
        {
            // FUN_0044ebc0 reads the manager's id-indexed table (+0x18, entry id - 1) and continues from that
            // object's +0xe0 link, so the target is an object id, not a position in the walk.
            var current = order.FindIndex(item => item.Id == target.Short);
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
                target.Short = candidate.Id;
                return PrimitiveResult.True;
            }
        }
        return PrimitiveResult.False;
    }

    private static PrimitiveResult WalkFamily(SimulationWorld world, Variable target)
    {
        if (target.Short == 0) return PrimitiveResult.False;
        var reference = (world.ObjectTableEntry(target.Short) ?? world.ObjectById(target.Short)) as XPerson
            ?? throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
        var family = reference[PersonData.FamilyNumber];
        var order = world.WalkOrder;
        var found = order.FindIndex(item => ReferenceEquals(item, reference) || item.Id == target.Short);
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

    private static PrimitiveResult WalkTile(SimulationWorld world, Variable target)
    {
        if (target.Short == 0) return PrimitiveResult.False;
        var current = world.ObjectTableEntry(target.Short) ?? world.ObjectById(target.Short);
        if (current is null || !SimulationWorld.BurnSourceOnLot(current)) return PrimitiveResult.False;
        var tileX = (sbyte)(current.Position.X >> 4);
        var tileY = (sbyte)(current.Position.Y >> 4);
        var level = (sbyte)current.Position.Height;
        var objects = world.ObjectsAtTile(tileX, tileY, level).ToList();
        if (objects.Count > 0)
        {
            for (var index = 0; index < objects.Count; index++)
            {
                var item = objects[index];
                if (!ReferenceEquals(item, current) && item.StackObjectKey != target.Short && item.Id != target.Short) continue;
                var next = objects[(index + 1) % objects.Count];
                target.Short = next.StackObjectKey;
                return PrimitiveResult.True;
            }
        }
        return PrimitiveResult.False;
    }

    private static PrimitiveResult WalkNearTile(SimulationWorld world, XObject me, StackFrame frame, byte local, Variable target)
    {
        if (local >= frame.Locals.Length)
            throw new SimulationException(SimulationError.StackNumberOutOfRange);
        var anchor = world.ObjectById(frame.Locals[local])
            ?? throw new SimulationException(SimulationError.ReferenceToTreeParameterWhenNoParameterExists);
        var baseX = anchor.Position.X >> 4;
        var baseY = anchor.Position.Y >> 4;
        var room = anchor.RoomWord84;
        var runner = world.ObjectById(target.Short);
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
            // FUN_0044f2cf's delta, bytes wrapping mod 256 and read back signed.
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
            // Sims.exe adds the steps to the tile bytes with 8-bit wraparound, then bounds-checks signed.
            var tileX = unchecked((byte)(baseX + stepX));
            var tileY = unchecked((byte)(baseY + stepY));
            if (tileX > 127 || tileY > 127 || tileX >= size || tileY >= size) continue;
            var value = world.TileValueAt(tileX, tileY, unchecked((byte)anchor.Position.Height));
            if (value <= 0) continue;
            var entry = me.ObjectTableEntry(unchecked((short)value));
            if (entry is null || entry.RoomWord84 != room) continue;
            target.Short = entry.Id;
            return PrimitiveResult.True;
        }
    }
}
