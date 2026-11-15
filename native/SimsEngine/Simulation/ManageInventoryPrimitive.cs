namespace SimsEngine.Simulation;

/// <summary>The Manage Inventory primitive (opcode 0x33, labelled "Manage Inventory (1.x Animate Object in 2.0)"
/// in Behavior.iff STR# 139), after Sims.exe FUN_00455338 (directly analyzed from the original executable with REA;
/// 0x455338: ECX is the running person, ESI the operand, and the frame holds the stack object).
/// <para>Operand: byte 0 is the mode; byte 1 the first key; bytes 2 and 3 the flags; bytes 4 to 7 the animation
/// key, or 0 for the stack object's type animation key (<see cref="TypeRecord.AnimationKey"/>). Byte 5 bit 2 of
/// the flags is not read here. With flag bit 13 set, the inventory belongs to the person at the manager index in the
/// running person's temporary 4, instead of the running person; anything but a person is error 0x1c
/// (<see cref="SimulationError.ObjectReferredToMustBeAPerson"/>).</para>
/// <para>Modes 0 to 2 add, remove and adjust an entry of the person's animation inventory
/// (<see cref="AvatarInventory"/>): the second key is the animation key and the count comes from temporary
/// storage (flag bit 1 set takes the temporary named by bits 2 and 3, otherwise 1); mode 2 takes the entry index
/// from the temporary named by bits 8 and 9 instead. Modes 3 and 4 find entries: the count goes to the temporary
/// named by bits 5 and 6 and the index to the one named by bits 11 and 12; mode 4 starts after the cursor in the
/// temporary named by bits 8 and 9 (which also supplies the second key when flag bit 0 is set) and answers false
/// with cursor -1 when there is no next entry. Modes 5 and 6 remove every entry of key 1 or 3, then add that key
/// with the GUID (+4, FUN_00401f73) of the neighbor whose one-based id is the running person's temporary 0; a missing
/// neighbor answers false in this port. Any
/// other mode only marks the inventory record changed. Every result but the false ones sets the source refresh flag
/// (<see cref="AvatarInventory.Changed"/>).</para>
/// <para>Not modelled: what the animation key (type +0x1c) names beyond the two reads that consume it. Inferred: the
/// inventory is kept per person and animation key (the original resolves it through a global search keyed by the
/// same value, so a shared animation could share one list); a missing temporary-0 neighbor answers false (Sims.exe
/// dereferences it); a missing stack object or type record throws <see cref="InvalidOperationException"/>
/// (Sims.exe reads through the null pointer).</para></summary>
public static class ManageInventoryPrimitive
{
    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, meAsObject, frame) = context;
        var flags = (ushort)(operand[2] | operand[3] << 8);

        var owner = (((flags >> 13) & 1) == 0 ? meAsObject : world.ObjectByStackObject(meAsObject, meAsObject.Temporaries[4])) as XPerson
            ?? throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);

        var rawTypeField = operand[4] | operand[5] << 8 | operand[6] << 16 | operand[7] << 24;
        int ResolveTypeField()
        {
            if (rawTypeField != 0) return rawTypeField;
            var stack = world.ObjectByStackObject(meAsObject, frame.StackObject)
                ?? throw new InvalidOperationException(
                    $"Manage Inventory needs the stack object's type animation key, but object {frame.StackObject} is not on the lot.");
            if (stack.TypeRecord is not { } typeRecord)
                throw new InvalidOperationException(
                    $"Manage Inventory needs the animation key of object {stack.Id}'s type, but it has no type record.");
            return typeRecord.AnimationKey != 0 ? typeRecord.AnimationKey : typeRecord.Key;
        }

        var neighborId = owner[PersonData.NeighborId];
        var inventory = world.NeighborInventory(neighborId);
        switch (operand[0])
        {
            case 0:
            {
                var typeField = ResolveTypeField();
                var count = ((flags & 2) == 0) ? (short)1 : meAsObject.Temporaries[(flags >> 2) & 3];
                if (!inventory.Add(operand[1], typeField, count)) return PrimitiveResult.False;
                break;
            }
            case 1:
            {
                var typeField = ResolveTypeField();
                var count = ((flags & 2) == 0) ? (short)1 : meAsObject.Temporaries[(flags >> 2) & 3];
                if (!inventory.Remove(operand[1], typeField, count)) return PrimitiveResult.False;
                break;
            }
            case 2:
            {
                var count = ((flags & 2) == 0) ? (short)1 : meAsObject.Temporaries[(flags >> 2) & 3];
                if (!inventory.Adjust(meAsObject.Temporaries[(flags >> 8) & 3], count)) return PrimitiveResult.False;
                break;
            }
            case 3:
            {
                var typeField = ResolveTypeField();
                var count = inventory.FindIndex(operand[1], typeField, out var at);
                if (count == 0) return PrimitiveResult.False;
                meAsObject.Temporaries[(flags >> 5) & 3] = count;
                meAsObject.Temporaries[(flags >> 11) & 3] = at;
                break;
            }
            case 4:
            {
                var keyIndex = (flags >> 8) & 3;
                short cursor = meAsObject.Temporaries[keyIndex];
                var key2 = ((flags & 1) == 0) ? 0 : ResolveTypeField();
                var count = inventory.FindNext(operand[1], ref cursor, key2);
                meAsObject.Temporaries[(flags >> 5) & 3] = count;
                meAsObject.Temporaries[(flags >> 11) & 3] = cursor;
                if (cursor == -1) return PrimitiveResult.False;
                break;
            }
            case 5:
            case 6:
            {
                var key = operand[0] == 5 ? 1 : 3;
                // FUN_0042573e indexes the neighborhood's one-based neighbor vector (+0x148); Mac TryInventoryAction
                // calls Neighborhood::FindNeighborByID here.
                var index = meAsObject.Temporaries[0];
                var target = (index >= 1 && index <= world.NeighborWalkOrder.Count ? world.NeighborWalkOrder[index - 1] : null)
                    ?? world.Neighbors.GetValueOrDefault(index);
                if (target is null) return PrimitiveResult.False;
                inventory.FilterOut(key);
                // FUN_00401f73 returns neighbor +4, the GUID FUN_00423e3b reads after the neighbor id (Mac
                // Neighbor::GetGUID). TypeRecordKey holds that GUID.
                if (!inventory.Add(key, target.TypeRecordKey, 1)) return PrimitiveResult.False;
                break;
            }
            default:
                break;
        }

        inventory.MarkChanged();
        world.RaiseNeighborInventoryChanged(neighborId, inventory);
        return PrimitiveResult.True;
    }
}
