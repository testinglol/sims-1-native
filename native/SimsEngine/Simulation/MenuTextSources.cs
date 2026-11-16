namespace SimsEngine.Simulation;

/// <summary>Provides external string and metadata lookups for dynamic UI tokens, including family lists and
/// fame titles.</summary>
public interface ISimulationMenuTextSources
{
    /// <summary>Returns the family name used to format <c>$Family</c> and <c>$NeighborLocal</c> tokens, or
    /// null if not found.</summary>
    string? FamilyName(int familyNumber);

    /// <summary>Returns the combined family funds and property value used to format the
    /// <c>$FamilyAssets</c> token.</summary>
    int? FamilyAssets(int familyNumber);

    /// <summary>Returns the title string for fame levels 0 through 10 used to format the
    /// <c>$FameTitleLocal</c> token.</summary>
    string? FameTitle(int level);
}
