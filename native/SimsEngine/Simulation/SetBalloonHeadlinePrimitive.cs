using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>Displays or clears a thought balloon or headline icon above an object. Supports text-based
/// balloons and type-based object icon balloons with configurable priority, duration, and persistence.
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
        if (value != 0 && operand[6] > 3)
            throw new SimulationException(SimulationError.BalloonTypeIsInvalid);
        if (host is null)
        {
            // Attaches balloon display to the object's first runtime sprite slot.
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
            // Clears the object's idle status by setting its clock manager wait ticks to -2.
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

    private static bool ShowAlternative(ISimulationHost host, XObject owner, XObject? entry)
    {
        if (entry?.TypeRecord is not { } type) return false;
        host.ShowBalloonForType(owner, host.AlternativeObjectType(type));
        return true;
    }
}
