using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>Manages directed relationship values maintained by an object or neighbor toward target IDs,
/// expanding dynamically with zero-initialized entries.</summary>
public sealed class RelationshipTable
{
    private readonly Dictionary<short, List<int>> _entries = [];

    public int Count(short target) => _entries.TryGetValue(target, out var values) ? values.Count : 0;

    public void Resize(short target, int count)
    {
        if (!_entries.TryGetValue(target, out var values)) _entries[target] = values = [];
        if (count > values.Count) values.AddRange(Enumerable.Repeat(0, count - values.Count));
        else if (count < values.Count) values.RemoveRange(count, values.Count - count);
    }

    public int Get(short target, int index) => Entry(target, index)[index];

    public void Set(short target, int index, int value) => Entry(target, index)[index] = value;

    public bool RemoveTarget(short target) => _entries.Remove(target);

    public IReadOnlyDictionary<int, IReadOnlyList<int>> Snapshot() =>
        _entries.ToDictionary(pair => (int)pair.Key, pair => (IReadOnlyList<int>)pair.Value.ToArray());

    /// <summary>Serializes live relationship vectors into a sparse saved table layout, preserving existing
    /// slot order and holes while appending new targets.</summary>
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

internal static class RelationshipOperands
{
    private const int DefinitionTypeField = 9;

    public static NeighborState? Neighbor(SimulationWorld world, short index) =>
        index >= 1 ? world.Neighbors.GetValueOrDefault(index) : null;

    public static XPerson Person(XObject item) => item as XPerson ?? throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);

    /// <summary>Determines whether an object definition represents a person (definition type 2).</summary>
    public static bool IsPersonType(XObject item) => item.Definition[DefinitionTypeField] == XPerson.PersonType;
}
