using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>A relationship list: the values an object (or a neighbor record) keeps toward each target id, after the
/// list helpers Sims.exe FUN_00465062 (count of values for a target, 0 when it has no entry), FUN_004650d0 (create the
/// target's entry if missing, then resize it), FUN_004651b1 (write one value) and FUN_0046518a (read one value).
/// <para>New values are 0 when a resize grows the vector (Sims.exe passes 0 to FUN_0046534a). Inferred: FUN_00465150
/// creates an empty entry, which the resize then fills.</para>
/// <para>Sims.exe reads and writes the vector by index without a check; the primitives only reach an index below the
/// count, so the checks here are for the negative index the signed operand can name (inferred: the error is 7, Data
/// number out of range).</para></summary>
public sealed class RelationshipTable
{
    private readonly Dictionary<short, List<int>> _entries = [];

    /// <summary>FUN_00465062: the number of values held for <paramref name="target"/>, or 0 when there is no entry.</summary>
    public int Count(short target) => _entries.TryGetValue(target, out var values) ? values.Count : 0;

    /// <summary>FUN_004650d0: makes the entry for <paramref name="target"/> hold <paramref name="count"/> values, creating
    /// the entry when it is missing and filling new values with 0.</summary>
    public void Resize(short target, int count)
    {
        if (!_entries.TryGetValue(target, out var values)) _entries[target] = values = [];
        if (count > values.Count) values.AddRange(Enumerable.Repeat(0, count - values.Count));
        else if (count < values.Count) values.RemoveRange(count, values.Count - count);
    }

    /// <summary>FUN_0046518a: the value at <paramref name="index"/> of the entry for <paramref name="target"/>.</summary>
    public int Get(short target, int index) => Entry(target, index)[index];

    /// <summary>FUN_004651b1: writes <paramref name="value"/> at <paramref name="index"/> of the entry for <paramref name="target"/>.</summary>
    public void Set(short target, int index, int value) => Entry(target, index)[index] = value;

    /// <summary>Mac RelMatrix::RemoveArray: drops the entry for <paramref name="target"/>.</summary>
    public bool RemoveTarget(short target) => _entries.Remove(target);

    /// <summary>Copies the live relation vectors for neighborhood persistence.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<int>> Snapshot() =>
        _entries.ToDictionary(pair => (int)pair.Key, pair => (IReadOnlyList<int>)pair.Value.ToArray());

    /// <summary>Projects live vectors into FUN_00465631's sparse saved table, retaining loaded slot order and holes.
    /// New targets are appended; the runtime dictionary does not model the original slot allocator.</summary>
    public IReadOnlyList<ObjectRelationship?> WriteSavedEntries(IReadOnlyList<ObjectRelationship?> original)
    {
        var remaining = new HashSet<short>(_entries.Keys);
        var result = new List<ObjectRelationship?>(original.Count);
        foreach (var entry in original)
        {
            if (entry is null)
            {
                result.Add(null);
                continue;
            }
            var target = checked((short)entry.Value0x0C);
            result.Add(_entries.TryGetValue(target, out var values)
                ? new ObjectRelationship(target, values.ToArray()) : null);
            remaining.Remove(target);
        }
        foreach (var target in remaining.Order())
            result.Add(new ObjectRelationship(target, _entries[target].ToArray()));
        return result;
    }

    private List<int> Entry(short target, int index)
    {
        if (!_entries.TryGetValue(target, out var values) || index < 0 || index >= values.Count)
            throw new SimulationException(SimulationError.DataNumberOutOfRange);
        return values;
    }
}

/// <summary>The lookups the Relationship and Old Relationship primitives share.</summary>
internal static class RelationshipOperands
{
    private const int DefinitionTypeField = 9;

    /// <summary>FUN_0042573e: the neighbor record at position <paramref name="index"/> (1-based), or null. Inferred: the
    /// position is the neighbor's id, which is how <see cref="SimulationWorld.Neighbors"/> is keyed.</summary>
    public static NeighborState? Neighbor(SimulationWorld world, short index) =>
        index >= 1 ? world.Neighbors.GetValueOrDefault(index) : null;

    /// <summary>FUN_006359f2 cast to cXPerson, for the person that must be the running object. Sims.exe raises error 28
    /// when the cast fails.</summary>
    public static XPerson Person(XObject item) => item as XPerson ?? throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);

    /// <summary>Definition field 9 is 2 for a person.</summary>
    public static bool IsPersonType(XObject item) => item.Definition[DefinitionTypeField] == XPerson.PersonType;
}
