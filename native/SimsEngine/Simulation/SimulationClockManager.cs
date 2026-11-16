namespace SimsEngine.Simulation;

public sealed class SimulationClockManagerEntry(XObject? item, uint flags)
{
    public XObject? Object { get; } = item;
    public uint Flags { get; set; } = flags;
    /// <summary>Stores the remaining wait ticks in the lower signed 16 bits of the clock control flags
    /// word.</summary>
    public short WaitTicks
    {
        get => unchecked((short)Flags);
        set => Flags = (Flags & 0xffff0000u) | unchecked((ushort)value);
    }
}

public enum SimulationClockManagerResult
{
    PassComplete,
    ObjectRejected,
    SpecialUpdateRequired,
}

public sealed class SimulationClockManager
{
    private int _cursor;

    public List<SimulationClockManagerEntry> Entries { get; } = [];
    public int ObjectCallbacksLastEvaluation { get; private set; }
    public short? RejectedObjectLastEvaluation { get; private set; }
    public int Cursor => _cursor;

    public uint FlagsAt(int oneBasedIndex) =>
        oneBasedIndex > 0 && oneBasedIndex <= Entries.Count ? Entries[oneBasedIndex - 1].Flags : 0;

    public XObject? ObjectAt(int oneBasedIndex) =>
        oneBasedIndex > 0 && oneBasedIndex <= Entries.Count ? Entries[oneBasedIndex - 1].Object : null;

    public short WaitTicksAt(int oneBasedIndex) => EntryAt(oneBasedIndex).WaitTicks;

    public void SetWaitTicksAt(int oneBasedIndex, short ticks) => EntryAt(oneBasedIndex).WaitTicks = ticks;

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

    public bool Remove(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var slot = item.ClockManagerIndex;
        if (slot < 1 || slot > Entries.Count || !ReferenceEquals(Entries[slot - 1].Object, item)) return false;
        Entries[slot - 1] = new SimulationClockManagerEntry(null, Entries[slot - 1].Flags);
        item.ClockManagerIndex = 0;
        return true;
    }

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
