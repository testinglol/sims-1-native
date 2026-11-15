namespace SimsEngine.Simulation;

/// <summary>A pointer and its parallel dword from the lot object's clock-manager vectors (+0x18 and +0x24).</summary>
public sealed class SimulationClockManagerEntry(XObject? item, uint flags)
{
    public XObject? Object { get; } = item;
    public uint Flags { get; set; } = flags;
    /// <summary>Low signed short of the parallel flags dword. FUN_00431e78 updates this while preserving its high bits.</summary>
    public short WaitTicks
    {
        get => unchecked((short)Flags);
        set => Flags = (Flags & 0xffff0000u) | unchecked((ushort)value);
    }
}

public enum SimulationClockManagerResult
{
    /// <summary>The full list pass completed; the global flags determine whether game time advances.</summary>
    PassComplete,
    /// <summary>An object virtual returned a zero low byte; Sims.exe returns early from FUN_0046d66f.</summary>
    ObjectRejected,
    /// <summary>The source calls FUN_00439b54 and returns early; its manager side effects are not yet ported.</summary>
    SpecialUpdateRequired,
}

/// <summary>The ordered, resumable object-manager walk in Sims.exe FUN_0046d66f. Its object callback is supplied by
/// the caller because it is a class-specific virtual at +0x14. Unknown FUN_00439b54 branches are surfaced instead of
/// being treated as a successful tick.</summary>
public sealed class SimulationClockManager
{
    private int _cursor;

    public List<SimulationClockManagerEntry> Entries { get; } = [];
    public int ObjectCallbacksLastEvaluation { get; private set; }
    public short? RejectedObjectLastEvaluation { get; private set; }
    public int Cursor => _cursor;

    /// <summary>Reads the parallel +0x24 flag vector at a one-based lot object-manager slot.</summary>
    public uint FlagsAt(int oneBasedIndex) =>
        oneBasedIndex > 0 && oneBasedIndex <= Entries.Count ? Entries[oneBasedIndex - 1].Flags : 0;

    /// <summary>FUN_00407498: resolves a one-based cXObject +0xf0 manager index, returning null for zero, an
    /// out-of-range index or an empty slot. Relationship mode operands use this lookup directly.</summary>
    public XObject? ObjectAt(int oneBasedIndex) =>
        oneBasedIndex > 0 && oneBasedIndex <= Entries.Count ? Entries[oneBasedIndex - 1].Object : null;

    /// <summary>Reads the low word of the parallel +0x24 entry that FUN_00431e78 uses for Sleep's wait cell.</summary>
    public short WaitTicksAt(int oneBasedIndex) => EntryAt(oneBasedIndex).WaitTicks;

    /// <summary>Writes the low word without disturbing the manager's upper eligibility/special-update bits.</summary>
    public void SetWaitTicksAt(int oneBasedIndex, short ticks) => EntryAt(oneBasedIndex).WaitTicks = ticks;

    /// <summary>Sets or clears one of the upper 16 flag bits using Sims.exe FUN_0044582f semantics.</summary>
    public void SetFlag(int oneBasedIndex, int bitIndex, bool enabled)
    {
        var entry = EntryAt(oneBasedIndex);
        if (bitIndex is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(bitIndex));
        var mask = 1u << (bitIndex + 16);
        entry.Flags = enabled ? entry.Flags | mask : entry.Flags & ~mask;
    }

    private SimulationClockManagerEntry EntryAt(int oneBasedIndex) =>
        oneBasedIndex > 0 && oneBasedIndex <= Entries.Count
            ? Entries[oneBasedIndex - 1]
            : throw new ArgumentOutOfRangeException(nameof(oneBasedIndex));

    /// <summary>Registers an object in the one-based slot Sims.exe FUN_0044350d assigns to cXObject +0xf0. Its
    /// parallel flag word starts at 0x0000ffff; this deliberately leaves the object's tick-eligibility bits clear
    /// until the source event path enables them.</summary>
    public int Register(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var slot = item.Id > 0 ? item.Id : FirstFreeSlot();
        if (slot > short.MaxValue) throw new InvalidOperationException("The Sims.exe object-manager slot exceeds its signed-short range.");
        while (Entries.Count < slot) Entries.Add(new SimulationClockManagerEntry(null, 0x0000ffff));

        var entry = Entries[slot - 1];
        if (entry.Object is not null && !ReferenceEquals(entry.Object, item))
            throw new InvalidOperationException($"Clock-manager slot {slot} is already occupied by object {entry.Object.Id}.");
        Entries[slot - 1] = new SimulationClockManagerEntry(item, 0x0000ffff);
        item.ClockManagerIndex = (short)slot;
        return slot;
    }

    /// <summary>Detaches an object from its manager slot. Slot positions are retained because Sims.exe stores the
    /// object pointer and flag word in parallel indexed vectors; later registrations can reuse a vacant slot.</summary>
    public bool Remove(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var slot = item.ClockManagerIndex;
        if (slot < 1 || slot > Entries.Count || !ReferenceEquals(Entries[slot - 1].Object, item)) return false;
        Entries[slot - 1] = new SimulationClockManagerEntry(null, Entries[slot - 1].Flags);
        item.ClockManagerIndex = 0;
        return true;
    }

    /// <summary>Sets bit 16 in the object's parallel manager flag word, following Sims.exe FUN_0044582f(index, 1, enabled).</summary>
    public void SetTickEnabled(XObject item, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(item);
        var slot = item.ClockManagerIndex;
        if (slot < 1 || slot > Entries.Count || !ReferenceEquals(Entries[slot - 1].Object, item))
            throw new InvalidOperationException($"Object {item.Id} is not registered with the clock manager.");

        SetFlag(slot, 0, enabled);
    }

    private int FirstFreeSlot()
    {
        for (var index = 0; index < Entries.Count; index++)
            if (Entries[index].Object is null) return index + 1;
        return Entries.Count + 1;
    }

    /// <summary>Runs the manager entries from the retained cursor. <paramref name="tickObject"/> returns the source
    /// virtual's low-byte truth value. The cursor resets only after the complete pass, like clock field +0x1a8.</summary>
    public SimulationClockManagerResult Evaluate(
        SimulationClockControl control,
        short clockState28,
        int tickCounter,
        Func<XObject, int, bool> tickObject)
    {
        ArgumentNullException.ThrowIfNull(tickObject);
        ObjectCallbacksLastEvaluation = 0;
        RejectedObjectLastEvaluation = null;
        while (_cursor < Entries.Count)
        {
            var entry = Entries[_cursor];
            if (entry.Object is null)
            {
                _cursor++;
                continue;
            }

            if ((entry.Flags & 0x00040000u) != 0)
            {
                if (clockState28 != 0)
                {
                    entry.Object.PrepareClockSpecialUpdate();
                    return SimulationClockManagerResult.SpecialUpdateRequired;
                }
                entry.Flags &= ~0x00040000u;
            }

            if ((entry.Flags & 0x00010000u) != 0 &&
                (!control.GlobalTimeHold || (entry.Flags & 0x00020000u) != 0))
            {
                if (entry.WaitTicks < 1)
                {
                    ObjectCallbacksLastEvaluation++;
                    if (!tickObject(entry.Object, tickCounter))
                    {
                        RejectedObjectLastEvaluation = entry.Object.Id;
                        return SimulationClockManagerResult.ObjectRejected;
                    }
                    if (clockState28 != 0)
                    {
                        entry.Object.PrepareClockSpecialUpdate();
                        return SimulationClockManagerResult.SpecialUpdateRequired;
                    }
                }
                else
                {
                    entry.WaitTicks--;
                }
            }

            _cursor++;
        }

        _cursor = 0;
        return SimulationClockManagerResult.PassComplete;
    }
}
