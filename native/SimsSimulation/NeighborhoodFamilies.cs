using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsSimulation;

/// <summary>The neighborhood's FAMI records as Mac Neighborhood keeps them, keyed by Family +0x10c (the FAMI chunk id,
/// stored by Family::LoadFamily). The active household is the record <see cref="FamilyBudget"/> holds, so a
/// removal from it also reaches the budget, the save, the active-person vector used by GenericSimsCall, and the
/// active-family member keys that <c>UpdateInstanceVisitorTypes</c> tests (Family::TestMember). Other changed records
/// are kept for the save.</summary>
internal sealed class NeighborhoodFamilies(SimulationWorld world, IffFile? neighborhood, ushort? activeChunkId)
    : INeighborhoodFamilies
{
    private static readonly ResourceType FamilyType = ResourceType.Parse("FAMI");
    private readonly Dictionary<ushort, Family> changed = [];
    private readonly Dictionary<ushort, Family> loaded = [];

    /// <summary>The non-active records changed since load, to write back with the neighborhood.</summary>
    public IReadOnlyDictionary<ushort, Family> Changed => changed;

    public bool HasMember(short familyNumber, int guid) => Find(familyNumber)?.Members.Contains(unchecked((uint)guid)) == true;

    public short ActiveFamilyNumber => activeChunkId is { } id && world.FamilyBudget is FamilyBudget { ActiveFamily: not null }
        ? unchecked((short)id)
        : (short)0;

    public bool Exists(short familyNumber) => Find(familyNumber) is not null;

    public void RemoveMember(short familyNumber, int guid) =>
        Store(familyNumber, Require(familyNumber).WithoutMember(unchecked((uint)guid)));

    public void AddMember(short familyNumber, int guid) =>
        Store(familyNumber, Require(familyNumber).WithMember(unchecked((uint)guid)));

    public short HouseNumberForMember(int guid)
    {
        var member = unchecked((uint)guid);
        if (neighborhood is not null)
        {
            foreach (var chunk in neighborhood.OfType(FamilyType))
            {
                if (chunk.Id == 0) continue;
                if (Find(unchecked((short)chunk.Id)) is { } family && family.Members.Contains(member))
                    return unchecked((short)family.HouseNumber);
            }
        }
        else if (activeChunkId is { } id && id != 0 && Find(unchecked((short)id)) is { } active && active.Members.Contains(member))
        {
            return unchecked((short)active.HouseNumber);
        }
        return 0;
    }

    public int HouseNumber(short familyNumber) => Find(familyNumber)?.HouseNumber ?? 0;

    public uint Flags(short familyNumber) => Find(familyNumber)?.Flags ?? 0;

    public IReadOnlyList<int> MemberGuids(short familyNumber) =>
        Find(familyNumber)?.Members.Select(guid => unchecked((int)guid)).ToArray() ?? [];

    private Family Require(short familyNumber) =>
        Find(familyNumber) ?? throw new InvalidOperationException($"Family {familyNumber} is not in the neighborhood.");

    private void Store(short familyNumber, Family family)
    {
        var id = unchecked((ushort)familyNumber);
        if (id == activeChunkId && world.FamilyBudget is FamilyBudget budget)
        {
            budget.ActiveFamily = family;
            // Family::TestMember reads the active family's +0x12c member vector.
            world.NeighborInteractionManagerTypeKeys.Clear();
            world.NeighborInteractionManagerTypeKeys.AddRange(family.Members.Select(member => unchecked((int)member)));
        }
        else changed[id] = family;
    }

    private Family? Find(short familyNumber)
    {
        if (familyNumber == 0) return null;
        var id = unchecked((ushort)familyNumber);
        if (id == activeChunkId && world.FamilyBudget is FamilyBudget { ActiveFamily: { } active }) return active;
        if (changed.TryGetValue(id, out var edited)) return edited;
        if (loaded.TryGetValue(id, out var cached)) return cached;
        if (neighborhood?.Find(FamilyType, id) is not { } chunk) return null;
        var read = Family.Read(neighborhood, chunk);
        loaded[id] = read;
        return read;
    }
}
