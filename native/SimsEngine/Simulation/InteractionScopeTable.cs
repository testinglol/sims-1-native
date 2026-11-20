namespace SimsEngine.Simulation;

public readonly record struct InteractionScopeEntry(int Value, int Mode, int Result);

public sealed class InteractionScopeTable
{
    private List<InteractionScopeEntry> _entries;
    private readonly AvatarInventory? _backing;
    private Func<AvatarInventory>? _backingResolver;

    private AvatarInventory? Backing => _backingResolver?.Invoke() ?? _backing;

    /// <summary>Maintains ordered avatar object definitions to match animation keys from type-13 records
    /// against scope entries.</summary>
    public List<TypeRecord> AvatarObjects { get; } = [];

    public InteractionScopeTable(IEnumerable<InteractionScopeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries.ToList();
    }

    public InteractionScopeTable(AvatarInventory backing)
    {
        ArgumentNullException.ThrowIfNull(backing);
        _backing = backing;
        _entries = [];
    }

    public InteractionScopeTable(Func<AvatarInventory> backingResolver)
    {
        ArgumentNullException.ThrowIfNull(backingResolver);
        _backingResolver = backingResolver;
        _entries = [];
    }

    internal bool IsBackedBy(AvatarInventory backing) => ReferenceEquals(Backing, backing);

    internal bool TryBindResolver(Func<AvatarInventory> backingResolver)
    {
        ArgumentNullException.ThrowIfNull(backingResolver);
        if (_backing is not null || _backingResolver is not null) return false;
        _backingResolver = backingResolver;
        _entries.Clear();
        return true;
    }

    public int Count => Backing?.Entries.Count ?? _entries.Count;

    public InteractionScopeTable Clone()
    {
        InteractionScopeTable copy;
        if (Backing is { } backing)
        {
            copy = new InteractionScopeTable(backing.Entries.Select(entry =>
                new InteractionScopeEntry(entry.Key1, entry.Key2, unchecked((ushort)entry.Count))));
        }
        else
        {
            copy = new InteractionScopeTable(_entries);
        }
        copy.AvatarObjects.AddRange(AvatarObjects);
        return copy;
    }

    /// <summary>Checks whether an animation key entry in the scope table restores interaction eligibility
    /// via a positive count.</summary>
    public bool HasVisitorInventoryEntry(int animationKey)
    {
        short cursor = -1;
        return TryFindNext(0, ref cursor, animationKey, out var count) && unchecked((short)count) > 0;
    }

    public int CountValue(int value)
    {
        if (Backing is { } backing)
        {
            var backedCount = 0;
            foreach (var entry in backing.Entries)
                if (entry.Key1 == value) backedCount++;
            return backedCount;
        }
        var count = 0;
        foreach (var entry in _entries)
            if (entry.Value == value) count++;
        return count;
    }

    /// <summary>Searches forward from the cursor for an entry matching the value and mode, matching any
    /// mode when zero and resetting the cursor to -1 on a miss.</summary>
    public bool TryFindNext(int value, ref short cursor, int mode, out ushort result)
    {
        if (Backing is { } backing)
        {
            var count = backing.FindNext(value, ref cursor, mode);
            result = cursor >= 0 ? unchecked((ushort)count) : (ushort)0;
            return cursor >= 0;
        }

        var first = cursor + 1;
        for (var index = first; index >= 0 && index < _entries.Count; index++)
        {
            var entry = _entries[index];
            if (entry.Value != value || (mode != 0 && entry.Mode != mode)) continue;

            cursor = unchecked((short)index);
            result = unchecked((ushort)entry.Result);
            return true;
        }

        cursor = -1;
        result = 0;
        return false;
    }

    public bool TryGet(short index, out InteractionScopeEntry entry)
    {
        if (Backing is { } backing)
        {
            if (index >= 0 && index < backing.Entries.Count)
            {
                var item = backing.Entries[index];
                entry = new InteractionScopeEntry(item.Key1, item.Key2, unchecked((ushort)item.Count));
                return true;
            }
            entry = default;
            return false;
        }

        if (index >= 0 && index < _entries.Count)
        {
            entry = _entries[index];
            return true;
        }

        entry = default;
        return false;
    }

    public void RemoveEntriesWithKey(int key)
    {
        if (Backing is { } backing)
        {
            backing.FilterOut(key);
            backing.MarkChanged();
            return;
        }
        _entries.RemoveAll(entry => entry.Value == key);
    }

    public void RemoveEntriesWithDifferentKey(int key) => RemoveEntriesWithKey(key);

    /// <summary>Adds a delta to a matching entry's low word saturating at 32767, or appends a new entry if
    /// absent, ignoring zero deltas and capping capacity at 32767 entries.</summary>
    public bool AddOrIncrement(int key, int mode, int delta)
    {
        var amount = unchecked((short)delta);
        if (amount == 0) return true;

        if (Backing is { } backing)
        {
            var added = backing.Add(key, mode, amount);
            if (added) backing.MarkChanged();
            return added;
        }

        var index = _entries.FindIndex(entry => entry.Value == key && entry.Mode == mode);
        if (index >= 0)
        {
            var entry = _entries[index];
            var updated = (int)unchecked((short)entry.Result) + amount;
            var lowWord = updated < short.MaxValue ? unchecked((short)updated) : short.MaxValue;
            _entries[index] = entry with
            {
                Result = (entry.Result & unchecked((int)0xffff0000)) | unchecked((ushort)lowWord),
            };
            return true;
        }

        if (_entries.Count > 0x7ffe) return false;
        _entries.Add(new InteractionScopeEntry(key, mode, unchecked((ushort)amount)));
        return true;
    }

    /// <summary>Reduces an indexed entry's count by a nonnegative delta while positive, removes the entry
    /// if the delta is negative, and succeeds immediately on a zero delta.</summary>
    public bool TryAdvance(short index, short delta)
    {
        if (delta == 0) return true;
        if (Backing is { } backing)
        {
            if (index < 0 || index >= backing.Entries.Count) return false;
            var adjusted = backing.Adjust(index, delta < 0 ? (short)-1 : delta);
            if (adjusted) backing.MarkChanged();
            return adjusted;
        }
        if (index < 0 || index >= _entries.Count) return false;

        var updated = new List<InteractionScopeEntry>(_entries.Count);
        for (var current = 0; current < _entries.Count; current++)
        {
            var entry = _entries[current];
            if (current != index)
            {
                updated.Add(entry);
                continue;
            }

            if (delta < 0) continue;

            var value = unchecked((short)entry.Result);
            var remaining = value <= delta ? (short)0 : (short)(value - delta);
            if (remaining > 0)
            {
                var lowWord = unchecked((ushort)remaining);
                updated.Add(entry with { Result = (entry.Result & unchecked((int)0xffff0000)) | lowWord });
            }
        }

        _entries = updated;
        return true;
    }
}
