namespace SimsEngine.Simulation;

/// <summary>Provides access to neighborhood families indexed by their family number.</summary>
public interface INeighborhoodFamilies
{
    bool HasMember(short familyNumber, int guid);

    short ActiveFamilyNumber { get; }

    bool Exists(short familyNumber);

    /// <summary>Adds a member GUID to the family if not already present, clearing the modified family flag.</summary>
    void AddMember(short familyNumber, int guid);

    /// <summary>Removes a member GUID from the family and clears the modified family flag.</summary>
    void RemoveMember(short familyNumber, int guid);

    /// <summary>Returns the house number of the first family containing the specified member GUID, or zero
    /// if unmatched.</summary>
    short HouseNumberForMember(int guid) => 0;

    int HouseNumber(short familyNumber) => 0;

    uint Flags(short familyNumber) => 0;

    IReadOnlyList<int> MemberGuids(short familyNumber) => [];
}
