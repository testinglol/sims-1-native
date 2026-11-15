namespace SimsEngine.Simulation;

/// <summary>A person's avatar inventory: the ordered 12-byte records the Manage Inventory primitive (opcode 0x33)
/// keeps, after the list helpers behind Sims.exe FUN_0041f38f (add), FUN_0041f429 (remove), FUN_0041f56d (adjust),
/// FUN_0041f662 (find index), FUN_0041f69e (find next) and FUN_0041f4e7 (filter). The disassembly of FUN_00455338
/// (0x455338) shows the calls, their argument order and the temp/operand bit encoding; the C bodies give the list
/// rules below. The container struct itself is 16 bytes (base, begin, populated end, capacity end); the port keeps
/// only the populated entries, which is all any primitive observes.
/// <para>Inferred: each entry is two keys and a count. The container is kept per person and animation key (see
/// <see cref="XPerson.InventoryOf"/>); the original resolves it through a global search keyed by the same value,
/// so a shared animation could share one list, which the port does not model.</para></summary>
public sealed class AvatarInventory
{
    private const int Capacity = 0x7fff;

    /// <summary>One 12-byte record: two key dwords and a count word.</summary>
    public sealed class Entry(int key1, int key2, short count)
    {
        public int Key1 { get; } = key1;
        public int Key2 { get; } = key2;
        public short Count { get; set; } = count;
    }

    private readonly List<Entry> _entries = [];

    /// <summary>Whether FUN_004cee86 set the inventory record's 0x04000000 refresh bit.</summary>
    public bool Changed { get; private set; }
    public void MarkChanged() => Changed = true;

    /// <summary>The entries in insertion order, after removals and filters.</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>FUN_0041f38f: a zero count answers true and changes nothing; otherwise the first entry with both
    /// keys takes the count added (capped at 0x7fff), or a new entry is appended (false when 0x7fff entries stand).</summary>
    public bool Add(int key1, int key2, short count)
    {
        if (count == 0) return true;
        var found = Find(key1, key2);
        if (found >= 0)
        {
            var sum = _entries[found].Count + count;
            _entries[found].Count = sum < Capacity ? unchecked((short)sum) : (short)Capacity;
            return true;
        }
        if (_entries.Count >= Capacity) return false;
        _entries.Add(new Entry(key1, key2, count));
        return true;
    }

    /// <summary>FUN_0041f429: a zero count answers true and changes nothing; otherwise the first entry with both keys
    /// loses one token, regardless of the positive count argument. The entry is erased when that token was its last,
    /// or when the count argument is -1. Missing entries answer false.</summary>
    public bool Remove(int key1, int key2, short count)
    {
        if (count == 0) return true;
        var found = Find(key1, key2);
        if (found < 0) return false;
        var entry = _entries[found];
        if (count == -1 || entry.Count <= 1)
            _entries.RemoveAt(found);
        else
            entry.Count--;
        return true;
    }

    /// <summary>Mac SimInventory::RemoveAllTokensOfType: erases every entry whose token type (first key) is
    /// <paramref name="type"/>.</summary>
    public void RemoveAllOfType(int type) => _entries.RemoveAll(entry => entry.Key1 == type);

    /// <summary>FUN_0041f56d: count 0 answers true and changes nothing; an index outside the entries answers
    /// false. Otherwise a positive count is subtracted from that entry, dropping it at zero or below, and a
    /// negative count removes the entry outright.</summary>
    public bool Adjust(int index, short count)
    {
        if (count == 0) return true;
        if (index < 0 || index >= _entries.Count) return false;
        if (count < 0)
        {
            _entries.RemoveAt(index);
            return true;
        }
        var next = _entries[index].Count - count;
        if (next <= 0) _entries.RemoveAt(index);
        else _entries[index].Count = unchecked((short)next);
        return true;
    }

    /// <summary>FUN_0041f662: the count of the first entry with both keys, with its index, or 0 with index -1.</summary>
    public short FindIndex(int key1, int key2, out short index)
    {
        var found = Find(key1, key2);
        index = found < 0 ? (short)-1 : (short)found;
        return found < 0 ? (short)0 : _entries[found].Count;
    }

    /// <summary>FUN_0041f69e through FUN_0041f748: starting after <paramref name="cursor"/> (which names the
    /// previously found index, -1 for a fresh search), the count of the first entry with <paramref name="key1"/>
    /// and, unless <paramref name="key2"/> is 0, with that second key. The cursor takes the found index, or -1
    /// with a 0 answer. The start truncates to a short, as the original reads it.</summary>
    public short FindNext(int key1, ref short cursor, int key2)
    {
        var start = unchecked((short)(cursor + 1));
        for (var i = start; i >= 0 && i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.Key1 == key1 && (key2 == 0 || entry.Key2 == key2))
            {
                cursor = unchecked((short)i);
                return entry.Count;
            }
        }
        cursor = -1;
        return 0;
    }

    /// <summary>FUN_0041f4e7: drops every entry whose first key is <paramref name="key1"/>.</summary>
    public void FilterOut(int key1) => _entries.RemoveAll(entry => entry.Key1 == key1);

    private int Find(int key1, int key2)
    {
        for (var i = 0; i < _entries.Count; i++)
            if (_entries[i].Key1 == key1 && _entries[i].Key2 == key2) return i;
        return -1;
    }
}
