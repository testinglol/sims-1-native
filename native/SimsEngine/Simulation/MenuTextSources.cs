namespace SimsEngine.Simulation;

/// <summary>The lookups the dynamic string parser (Sims.exe FUN_0044bf5a) makes outside the object tables: the family
/// list (FUN_00425db3, searched by the family number at neighbor +0xee) and the fame title table (DAT_006de804).</summary>
public interface ISimulationMenuTextSources
{
    /// <summary>The name of the family with this number (<c>$Family</c>, and the family part of <c>$NeighborLocal</c>), or
    /// null when no such family is loaded.</summary>
    string? FamilyName(int familyNumber);

    /// <summary>FUN_0042730f for the family with this number (<c>$FamilyAssets</c>): its funds plus the value of its
    /// property, or null when no such family is loaded.</summary>
    int? FamilyAssets(int familyNumber);

    /// <summary>The title of fame level <paramref name="level"/> (0 to 10, <c>$FameTitleLocal</c>), or null when the
    /// fame table is not loaded.</summary>
    string? FameTitle(int level);
}
