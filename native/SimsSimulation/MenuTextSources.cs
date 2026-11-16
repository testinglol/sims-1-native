using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsSimulation;

/// <summary>Provides family names and fame titles for dynamic dialog string substitutions.</summary>
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

    /// <summary>Finds a family record whose chunk ID matches the family number, falling back to matching
    /// the stored internal family index.</summary>
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
        // Live household funds for the active family come from the budget host, while other families read
        // directly from their saved record.
        var live = world.FamilyBudget is FamilyBudget { ActiveFamily: { } active, FamilyChunkId: { } chunk } && chunk == found.Chunk.Id
            ? world.FamilyBudget.ReadFamilyBudget() + active.Value : (int?)null;
        return live ?? unchecked(found.Family.Budget + found.Family.Value);
    }

    public string? FameTitle(int level) => fameTitles is not null && (uint)level < fameTitles.Count ? fameTitles[level] : null;
}
