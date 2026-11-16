namespace SimsEngine.Simulation;

/// <summary>Manages ordered avatar inventory records for the Manage Inventory primitive (opcode 0x33). Each
/// record consists of two 32-bit keys and a 16-bit count.</summary>
public sealed class AvatarInventory
{
    private const int Capacity = 0x7fff;

    public sealed class Entry(int key1, int key2, short count)
    {
        public int Key1 { get; } = key1;
        public int Key2 { get; } = key2;
        public short Count { get; set; } = count;
    }

    private readonly List<Entry> _entries = [];

    public bool Changed { get; private set; }
    public void MarkChanged() => Changed = true;

    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>Adds to the first entry matching both keys, capping the count at 32767, or appends a new
    /// entry. A count of zero returns true without changes; returns false if maximum capacity is reached.
    /// </summary>
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

    /// <summary>Removes a single token from the first entry matching both keys regardless of the positive
    /// count value, deleting the entry when its count reaches zero or if count is -1. A count of zero
    /// returns true without changes; missing entries return false.</summary>
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

    public void RemoveAllOfType(int type) => _entries.RemoveAll(entry => entry.Key1 == type);

    /// <summary>Adjusts the entry at the specified index. A positive count subtracts from the entry and
    /// removes it if the count drops to zero or below, while a negative count removes the entry outright.
    /// </summary>
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

    public short FindIndex(int key1, int key2, out short index)
    {
        var found = Find(key1, key2);
        index = found < 0 ? (short)-1 : (short)found;
        return found < 0 ? (short)0 : _entries[found].Count;
    }

    /// <summary>Finds the next entry matching <paramref name="key1"/> (and <paramref name="key2"/> if
    /// nonzero) starting after <paramref name="cursor"/>, updating the cursor to the found index or -1.
    /// </summary>
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

    public void FilterOut(int key1) => _entries.RemoveAll(entry => entry.Key1 == key1);

    private int Find(int key1, int key2)
    {
        for (var i = 0; i < _entries.Count; i++)
            if (_entries[i].Key1 == key1 && _entries[i].Key2 == key2) return i;
        return -1;
    }
}
