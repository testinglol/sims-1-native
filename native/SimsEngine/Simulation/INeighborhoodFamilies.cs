namespace SimsEngine.Simulation;

/// <summary>The neighborhood's family list (Mac Neighborhood +0x158/+0x15c), each family keyed by its number
/// (Family +0x10c, the FAMI chunk id). The host implements it.</summary>
public interface INeighborhoodFamilies
{
    /// <summary>Whether a family numbered <paramref name="familyNumber"/> exists and <c>Family::GetMemberByGUID</c> finds
    /// <paramref name="guid"/> in it.</summary>
    bool HasMember(short familyNumber, int guid);

    /// <summary>The number of the household on the lot (ObjectModule +0x10), or 0 when there is none.</summary>
    short ActiveFamilyNumber { get; }

    /// <summary><c>Neighborhood::GetFamily</c>: whether a family numbered <paramref name="familyNumber"/> exists.</summary>
    bool Exists(short familyNumber);

    /// <summary><c>Family::AddMember(guid)</c> (appended when it is not listed), then the family's +0x138 flag bit 3 is
    /// cleared, as <c>Neighborhood::AddToFamily</c> does.</summary>
    void AddMember(short familyNumber, int guid);

    /// <summary><c>Neighborhood::RemoveFromFamily</c>: <c>Family::RemoveMember(guid)</c>, then the family's +0x138 flag bit 3 is cleared.</summary>
    void RemoveMember(short familyNumber, int guid);

    /// <summary><c>FUN_00425ba7</c> case 6: walks the neighborhood's family vector (+0x154/+0x158), finds the first
    /// family whose member list contains <paramref name="guid"/> (<c>FUN_00410285</c>), and returns its house number
    /// (+0x110), or 0 when none matches.</summary>
    short HouseNumberForMember(int guid) => 0;

    /// <summary><c>Family</c> +0x110: the house number of the family numbered <paramref name="familyNumber"/>, or 0.</summary>
    int HouseNumber(short familyNumber) => 0;

    /// <summary><c>Family</c> +0x138: the flags word of the family numbered <paramref name="familyNumber"/>, or 0.</summary>
    uint Flags(short familyNumber) => 0;

    /// <summary><c>Family</c> +0x12c: the member GUIDs of the family numbered <paramref name="familyNumber"/>.</summary>
    IReadOnlyList<int> MemberGuids(short familyNumber) => [];
}
