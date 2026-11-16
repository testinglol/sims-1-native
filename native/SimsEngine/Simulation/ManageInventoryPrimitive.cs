namespace SimsEngine.Simulation;

/// <summary>Executes the Manage Inventory primitive (opcode 0x33) to add, remove, search, or update
/// animation inventory entries on a Sim. Modes 0–2 manipulate token counts, modes 3–4 query entries into
/// temporary registers, and modes 5–6 associate neighbor GUIDs.</summary>
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
                var index = meAsObject.Temporaries[0];
                var target = (index >= 1 && index <= world.NeighborWalkOrder.Count ? world.NeighborWalkOrder[index - 1] : null)
                    ?? world.Neighbors.GetValueOrDefault(index);
                if (target is null) return PrimitiveResult.False;
                inventory.FilterOut(key);
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
