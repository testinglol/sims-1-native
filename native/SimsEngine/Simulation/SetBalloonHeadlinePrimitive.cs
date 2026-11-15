using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>The Set Balloon/Headline primitive (opcode 0x29), after Sims.exe 0x44b764 (traced from the decompilation;
/// the balloon and string state are the host's). It shows a balloon or headline on an object, or clears it.
/// <para>Operand: word 0 has bit 0 for the owner (0 the running object, 1 the stack object) and bits 1 to 15 for the local
/// index of the object-type selector 2. Byte 2 is the balloon's text id, or the selector of the type balloon. Byte 3 is
/// the string table of the text, with 4 meaning priority 2 (1 otherwise) and 7 meaning a type balloon. Word 4 is the
/// balloon's value: 0 clears it, -1 makes it permanent. Byte 6 is the duration kind. Word 6 has bit 8 for the owner's
/// flag, bit 9 for the label (table 1, index 3), bit 10 for the text flags, bit 11 for a timed value, and bit 12 for
/// adding the running object's Temporaries[0] to the text id.</para>
/// <para>Order: a stack owner that is missing raises "No stack object present when required." (21). An owner with no
/// balloon record answers true. Then the priority is claimed (<see cref="ISimulationHost.ClaimBalloon"/>); a claim that
/// a higher priority refuses answers true. A value of 0 clears the state and answers true. The text id gets the
/// temporary when bit 12 is set. A type balloon (byte 3 of 7) then picks its object by selector: 0 is the stack
/// object's id in the running object's table, 1 is the list value of the stack object's id, and 2 is the local at
/// (word 0 >> 1) in the running object's table. Selector 2 raises "Local out of range." (51) for an index past the
/// locals. A missing object, a missing list value or an object without a type record ends the primitive with true. Any
/// other selector also answers true. A text balloon (any other byte 3) answers true when there is no text to show.</para>
/// <para>The common tail then sets the duration, the label (when bit 9 is set) and the flag, sets the state (a value of
/// -1 is permanent; a value otherwise is timed when bit 11 is set), refreshes the display and notifies the owner's
/// object table. The primitive answers true.</para>
/// <para>Inferred: Sims.exe also tests the text id against -1; a byte cannot hold that, so the test is left out. A
/// negative local index passes Sims.exe's bounds test and reads before the locals; the port raises error 51 for it.</para>
/// </summary>
public static class SetBalloonHeadlinePrimitive
{
    private const int PriorityTwoTable = 4;
    private const int TypeBalloonTable = 7;
    private const byte LabelTable = 1;
    private const short LabelIndex = 3;
    private const short Permanent = -1;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var host = world.Host;
        var word0 = (short)(operand[0] | operand[1] << 8);
        var owner = (word0 & 1) == 0 ? me : world.ObjectByStackObject(me, frame.StackObject) ?? throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
        var priority = operand[3] == PriorityTwoTable ? 2 : 1;
        var value = (short)(operand[4] | operand[5] << 8);
        var flags = operand[6] | operand[7] << 8;
        if (host is null)
        {
            // cXObject::TrySetBalloon operates on the first record in the same +0x148 SpriteSlot vector that native
            // ticking and Sleep use. IconGroup.s resolves ordinary text icons to SPR# resources in Sprites.iff.
            if (owner.RuntimeSlots.Count == 0) return PrimitiveResult.True;
            var slot = owner.RuntimeSlots[0];
            if (slot.ReadWord(0x10) != 0 && priority < slot.ReadWord(0x34)) return PrimitiveResult.True;
            slot.WriteWord(0x34, priority);
            if (value == 0)
            {
                slot.ActivateForTicks(0);
                return PrimitiveResult.True;
            }
            if (operand[3] == TypeBalloonTable)
            {
                if (!TryGetHostlessTypeSelectorKey(world, me, frame, word0, operand[2], out var typeKey))
                    return PrimitiveResult.True;
                slot.SetSelectorSprite(typeKey);
            }
            else
            {
                var icon = operand[2] + ((flags & 0x1000) != 0 ? me.Temporaries[0] : 0);
                var spriteId = IconGroupSpriteIds.GetSpriteId(operand[3], icon);
                var file = world.GlobalSprites;
                var chunk = spriteId == 0 ? null : file?.Find(ResourceType.Spr1, (ushort)spriteId);
                if (chunk is null) return PrimitiveResult.True;
                var frameCount = Sprite.Read(file!, chunk).Frames.Count / 3;
                if (frameCount == 0) return PrimitiveResult.True;

                var backwards = (flags & 0x400) != 0;
                var labelSpriteId = (flags & 0x200) != 0 ? IconGroupSpriteIds.GetSpriteId(LabelTable, LabelIndex) : (short)0;
                slot.SetRendererSprite(spriteId, frameCount, backwards, 0, 0, false);
                slot.WriteWord(0x2c, IconGroupSpriteIds.GetBalloonSpriteId(operand[6]));
                slot.WriteWord(0x30, labelSpriteId);
                slot.WriteWord(0x38, (flags & 0x100) != 0 ? 1 : 0);
            }

            if (operand[3] == TypeBalloonTable)
            {
                slot.WriteWord(0x2c, IconGroupSpriteIds.GetBalloonSpriteId(operand[6]));
                slot.WriteWord(0x30, (flags & 0x200) != 0 ? IconGroupSpriteIds.GetSpriteId(LabelTable, LabelIndex) : 0);
                slot.WriteWord(0x38, (flags & 0x100) != 0 ? 1 : 0);
            }
            if (value == Permanent) slot.ActivateForTicks(-1);
            else if ((flags & 0x800) != 0) slot.ActivateForLoops(value);
            else slot.ActivateForTicks(value);
            world.DisplayRefreshRequested = true;
            // ClearIdleStatus writes -2 to the object's clock-manager wait word after a successful Set Balloon.
            if (owner.ClockManagerIndex > 0)
                world.ClockManager.SetWaitTicksAt(owner.ClockManagerIndex, -2);
            return PrimitiveResult.True;
        }
        if (!host.HasBalloonEntries(owner)) return PrimitiveResult.True;
        if (!host.ClaimBalloon(owner, priority)) return PrimitiveResult.True;
        if (value == 0)
        {
            host.SetBalloonState(owner, 0);
            return PrimitiveResult.True;
        }
        var text = operand[2] + ((flags & 0x1000) != 0 ? me.Temporaries[0] : 0);
        if (operand[3] == TypeBalloonTable)
        {
            if (!ShowTypeBalloon(context, host, owner, word0, operand[2])) return PrimitiveResult.True;
        }
        else if (!host.ShowBalloonText(operand[3], text, (flags >> 10) & 0xff01)) return PrimitiveResult.True;
        var duration = host.BalloonDuration(operand[6]);
        var label = (flags & 0x200) != 0 ? host.BalloonString(LabelTable, LabelIndex) : 0;
        host.SetBalloonFields(owner, duration, label, (flags & 0x100) != 0);
        if (value == Permanent) host.SetBalloonState(owner, value);
        else if ((flags & 0x800) == 0) host.SetBalloonState(owner, value);
        else host.SetBalloonStateTimed(owner, value);
        host.RefreshDisplay();
        host.NotifyObjectTable(owner);
        return PrimitiveResult.True;
    }

    private static bool TryGetHostlessTypeSelectorKey(SimulationWorld world, XObject me, StackFrame frame, short word0,
        byte selector, out int typeKey)
    {
        typeKey = 0;
        switch (selector)
        {
            case 0:
                return MasterTypeKey(world, me.ObjectTableEntry(frame.StackObject)?.TypeRecord, out typeKey);
            case 1:
                if (!world.Neighbors.TryGetValue(frame.StackObject, out var neighbor) || neighbor.TypeRecordKey == 0)
                    return false;
                typeKey = MasterTypeKey(world, neighbor.TypeRecordKey);
                return true;
            case 2:
            {
                var index = word0 >> 1;
                if (index < 0 || index >= frame.Locals.Length)
                    throw new SimulationException(SimulationError.LocalOutOfRange);
                return MasterTypeKey(world, me.ObjectTableEntry(frame.Locals[index])?.TypeRecord, out typeKey);
            }
            default:
                return false;
        }
    }

    private static bool MasterTypeKey(SimulationWorld world, TypeRecord? type, out int typeKey)
    {
        if (type is null)
        {
            typeKey = 0;
            return false;
        }
        typeKey = type.AlternativeTypeKey ?? MasterTypeKey(world, type.Key);
        return true;
    }

    private static int MasterTypeKey(SimulationWorld world, int typeKey)
    {
        var loadedType = world.WalkOrder.Select(item => item.TypeRecord).FirstOrDefault(type => type?.Key == typeKey);
        if (loadedType?.AlternativeTypeKey is { } loadedAlternative) return loadedAlternative;
        var entry = world.ObjectRegistry?.Find(unchecked((uint)typeKey));
        var alternative = entry is null ? null : world.ObjectRegistry?.FindAlternativeType(entry);
        return alternative is null ? typeKey : unchecked((int)alternative.Guid);
    }

    /// <summary>The type balloon's three selectors (byte 2). Answers false when there is nothing to show, which ends the
    /// primitive with true, as Sims.exe's early returns do.</summary>
    private static bool ShowTypeBalloon(in TreeContext context, ISimulationHost host, XObject owner, short word0, byte selector)
    {
        var (_, me, frame) = context;
        switch (selector)
        {
            case 0:
                return ShowAlternative(host, owner, me.ObjectTableEntry(frame.StackObject));
            case 1:
            {
                var value = host.ListEntryValue(frame.StackObject);
                if (value == 0) return false;
                host.ShowBalloonForValue(owner, value);
                return true;
            }
            case 2:
            {
                var index = word0 >> 1;
                if (index < 0 || index >= frame.Locals.Length) throw new SimulationException(SimulationError.LocalOutOfRange);
                return ShowAlternative(host, owner, me.ObjectTableEntry(frame.Locals[index]));
            }
            default:
                return false;
        }
    }

    /// <summary>The alternative type of the object's type record, shown on the owner (FUN_004573f9, then FUN_0046e105).
    /// An object without a type record shows nothing (inferred).</summary>
    private static bool ShowAlternative(ISimulationHost host, XObject owner, XObject? entry)
    {
        if (entry?.TypeRecord is not { } type) return false;
        host.ShowBalloonForType(owner, host.AlternativeObjectType(type));
        return true;
    }
}
