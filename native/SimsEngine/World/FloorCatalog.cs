using SimsEngine.Content;
using SimsEngine.Formats;

namespace SimsEngine.World;

/// <summary>Manages floor catalog patterns and prices loaded from Build.iff STR# 0x82 (triplets of price,
/// name, and description for built-in patterns 1 through 29). Standalone .flr patterns begin at index 256.
/// </summary>
public sealed class FloorCatalog
{
    private readonly Dictionary<int, int> prices = [];

    private int terrainPrice = 1;

    private FloorCatalog() { }

    /// <summary>Price per unit of height for terrain modification tools, loaded from Build.iff STR# 0x80
    /// string 0.</summary>
    public int TerrainPrice() => terrainPrice;

    /// <summary>Price per tile for the pond and water tool (pattern 65534), loaded from Build.iff STR#
    /// 0x85.</summary>
    public int WaterPrice() => prices.GetValueOrDefault(65534, 10);

    public static FloorCatalog Read(IffFile build, byte language = 1, IEnumerable<KeyValuePair<int, int>>? dynamicFloors = null)
    {
        var catalog = new FloorCatalog();
        var chunk = build.Find(ResourceType.Str, 0x82) ?? throw new InvalidDataException("Build.iff has no floor STR# 0x82");
        var strings = StringTable.Read(build, chunk).ForLanguage(language);
        for (var pattern = 1; (pattern - 1) * 3 < strings.Count && pattern <= 29; pattern++)
            catalog.prices[pattern] = int.TryParse(strings[(pattern - 1) * 3].Trim(), out var price) ? price : 0;
        if (build.Find(ResourceType.Str, 0x80) is { } terrainChunk
            && StringTable.Read(build, terrainChunk).ForLanguage(language) is { Count: > 0 } terrainStrings
            && int.TryParse(terrainStrings[0].Trim(), out var unit))
            catalog.terrainPrice = unit;
        // Build.iff STR# 0x85 defines the pool tool (pattern 65535) followed by the pond tool (pattern
        // 65534).
        if (build.Find(ResourceType.Str, 0x85) is { } waterChunk)
        {
            var waterStrings = StringTable.Read(build, waterChunk).ForLanguage(language);
            if (waterStrings.Count >= 1 && int.TryParse(waterStrings[0].Trim(), out var poolPrice))
                catalog.prices[65535] = poolPrice;
            if (waterStrings.Count >= 4 && int.TryParse(waterStrings[3].Trim(), out var pondPrice))
                catalog.prices[65534] = pondPrice;
            else if (waterStrings.Count >= 3 && int.TryParse(waterStrings[2].Trim(), out var altPondPrice))
                catalog.prices[65534] = altPondPrice;
        }
        if (dynamicFloors is not null)
        foreach (var (pattern, price) in dynamicFloors)
            if (pattern is > 0 and < 65534) catalog.prices[pattern] = price;
        return catalog;
    }

    public static FloorCatalog Open(string game, byte language = 1, IEnumerable<KeyValuePair<int, int>>? dynamicFloors = null)
    {
        foreach (var folder in new[] { ObjectRegistry.Resolve(game, "GameData"), game })
        {
            if (folder is null || !Directory.Exists(folder)) continue;
            var path = Directory.EnumerateFiles(folder).FirstOrDefault(file =>
                string.Equals(Path.GetFileName(file), "Build.iff", StringComparison.OrdinalIgnoreCase));
            if (path is not null)
                return Read(IffFile.Open(path), language, dynamicFloors ?? ReadDynamicFloors(folder, language));
        }
        throw new FileNotFoundException("Build.iff was not found", game);
    }

    /// <summary>Standalone .flr floor files begin at ID 256, obtaining catalog prices from STR# 0. Filename
    /// ordering ensures stable pattern ID assignment across loads.</summary>
    private static IEnumerable<KeyValuePair<int, int>> ReadDynamicFloors(string gameData, byte language)
    {
        foreach (var (pattern, path) in DynamicFiles(gameData))
        {
            var iff = IffFile.Open(path);
            var chunk = iff.Find(ResourceType.Str, 0);
            IReadOnlyList<string> strings = chunk is null ? Array.Empty<string>() : StringTable.Read(iff, chunk).ForLanguage(language);
            var price = strings.Count > 1 && int.TryParse(strings[1].Trim(), out var parsed) ? parsed : 0;
            yield return KeyValuePair.Create(pattern, price);
        }
    }

    public static IEnumerable<KeyValuePair<int, string>> DynamicFiles(string gameData)
    {
        var root = ObjectRegistry.Resolve(gameData, "Floors");
        if (root is null) yield break;
        var pattern = 256;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".flr", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase))
        {
            if (pattern >= 65534) yield break;
            yield return KeyValuePair.Create(pattern++, path);
        }
    }

    public bool Contains(int pattern) => prices.ContainsKey(pattern);

    public IReadOnlyCollection<int> Patterns => prices.Keys;

    public int Price(int pattern) => prices.GetValueOrDefault(pattern);
}
