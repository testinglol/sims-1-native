namespace SimsEngine.Simulation;

/// <summary>
/// One 12-byte interaction-executor entry from a Sims.exe behavior scope
/// (FUN_0041F748, FUN_0041F69E and FUN_0041F6FD).
/// </summary>
public readonly record struct InteractionScopeEntry(int Value, int Mode, int Result);

/// <summary>
/// The searchable part of Sims.exe's interaction-executor scope table. The table transformation models the logical effect of the scope compaction helpers; allocation and executor side effects
/// remain outside this type.
/// </summary>
public sealed class InteractionScopeTable
{
    private List<InteractionScopeEntry> _entries;

    /// <summary>The ordered avatar objects held by this source scope. FUN_0043e2ff scans this vector, keeps the
    /// animation key (+0x1c) of type-13 records, then FUN_0041f662 looks up (0, key) in this scope's entries.</summary>
    public List<TypeRecord> AvatarObjects { get; } = [];

    public InteractionScopeTable(IEnumerable<InteractionScopeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries.ToList();
    }

    public int Count => _entries.Count;

    public InteractionScopeTable Clone()
    {
        var copy = new InteractionScopeTable(_entries);
        copy.AvatarObjects.AddRange(AvatarObjects);
        return copy;
    }

    /// <summary>FUN_0041f662's lookup in FUN_00458485: the (0, animationKey) entry restores eligibility only
    /// when its signed count is positive.</summary>
    public bool HasVisitorInventoryEntry(int animationKey)
    {
        short cursor = -1;
        return TryFindNext(0, ref cursor, animationKey, out var count) && unchecked((short)count) > 0;
    }

    /// <summary>Counts entries whose first dword equals <paramref name="value"/> (FUN_0041F7A9).</summary>
    public int CountValue(int value)
    {
        var count = 0;
        foreach (var entry in _entries)
            if (entry.Value == value) count++;
        return count;
    }

    /// <summary>
    /// Searches from <paramref name="cursor"/> + 1 for an entry whose value matches and whose mode also matches,
    /// unless <paramref name="mode"/> is zero (FUN_0041F69E). A miss resets the cursor to -1. The returned value
    /// is the low 16 bits of the entry's third dword, matching the low word returned by the original helper.
    /// </summary>
    public bool TryFindNext(int value, ref short cursor, int mode, out ushort result)
    {
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

    /// <summary>Fetches a zero-based entry; negative and out-of-range indices resolve to the table end (FUN_0041F748).</summary>
    public bool TryGet(short index, out InteractionScopeEntry entry)
    {
        if (index >= 0 && index < _entries.Count)
        {
            entry = _entries[index];
            return true;
        }

        entry = default;
        return false;
    }

    /// <summary>FUN_0041F4E7: retain only entries whose first dword matches <paramref name="key"/>.</summary>
    public void RemoveEntriesWithDifferentKey(int key) => _entries.RemoveAll(entry => entry.Value != key);

    /// <summary>
    /// FUN_0041F38F/FUN_0041F3F8: add <paramref name="delta"/> to the matching entry's signed low word, saturating
    /// at 0x7fff; append a new (key, mode, delta) record when absent. A zero low word is a no-op. The original
    /// refuses an append when the existing vector already has 0x7fff records.
    /// </summary>
    public bool AddOrIncrement(int key, int mode, int delta)
    {
        var amount = unchecked((short)delta);
        if (amount == 0) return true;

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

    /// <summary>
    /// Applies Sims.exe FUN_0041F56D and its F957 compaction path. A zero delta succeeds without inspecting the
    /// index. Otherwise the indexed entry's low word is reduced by a nonnegative delta and retained only while
    /// positive; a negative delta removes the indexed entry. All other entries retain their order and contents.
    /// </summary>
    public bool TryAdvance(short index, short delta)
    {
        if (delta == 0) return true;
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
