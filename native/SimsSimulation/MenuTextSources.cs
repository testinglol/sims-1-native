using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsSimulation;

/// <summary>The family and fame lookups of the dynamic string parser (Sims.exe FUN_0044bf5a) for the loaded lot: every
/// family of Neighborhood.iff (its FAMI record and FAMs name, searched by family number as FUN_00425db3 does) and the
/// fame titles of fame.iff STR# 1.</summary>
internal sealed class MenuTextSources(SimulationWorld world, IReadOnlyList<string>? fameTitles, IffFile? neighborhood, byte language)
    : ISimulationMenuTextSources
{
    public static MenuTextSources Load(SimulationWorld world, string gameRoot, byte language, IffFile? neighborhood = null)
    {
        var path = Path.Combine(gameRoot, "GameData", "fame.iff");
        IReadOnlyList<string>? titles = null;
        if (File.Exists(path))
        {
            var file = IffFile.Open(path);
            if (file.Find(ResourceType.Str, 1) is { } chunk) titles = StringTable.Read(file, chunk).ForLanguage(language);
        }
        return new MenuTextSources(world, titles, neighborhood, language);
    }

    /// <summary>The FAMI chunk whose id is the family number, else the one whose stored family number matches.</summary>
    private (IffChunk Chunk, Family Family)? Find(int familyNumber)
    {
        if (neighborhood is null) return null;
        var fami = ResourceType.Parse("FAMI");
        if (neighborhood.Find(fami, unchecked((ushort)familyNumber)) is { } byId) return (byId, Family.Read(neighborhood, byId));
        foreach (var chunk in neighborhood.OfType(fami))
        {
            var family = Family.Read(neighborhood, chunk);
            if (family.FamilyNumber == familyNumber) return (chunk, family);
        }
        return null;
    }

    public string? FamilyName(int familyNumber)
    {
        if (Find(familyNumber) is not { } found) return null;
        if (found.Family.Name is { } stored) return stored;
        var names = neighborhood!.Find(ResourceType.Parse("FAMs"), found.Chunk.Id);
        return names is null ? null : StringTable.Read(neighborhood, names).ForLanguage(language).FirstOrDefault();
    }

    public int? FamilyAssets(int familyNumber)
    {
        if (Find(familyNumber) is not { } found) return null;
        // The active family's funds are live in the budget host; every other family keeps them in its FAMI record.
        var live = world.FamilyBudget is FamilyBudget { ActiveFamily: { } active, FamilyChunkId: { } chunk } && chunk == found.Chunk.Id
            ? world.FamilyBudget.ReadFamilyBudget() + active.Value : (int?)null;
        return live ?? unchecked(found.Family.Budget + found.Family.Value);
    }

    public string? FameTitle(int level) => fameTitles is not null && (uint)level < fameTitles.Count ? fameTitles[level] : null;
}
