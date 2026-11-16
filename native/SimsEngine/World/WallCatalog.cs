using SimsEngine.Content;
using SimsEngine.Formats;

namespace SimsEngine.World;

/// <summary>Loads wall styles and per-segment prices from Build.iff STR# 0x81, which stores price, name,
/// and description triples indexed by style. Styles omitted from the resource default to a price of zero.
/// </summary>
public sealed class WallCatalog
{
    private static readonly int[] StyleOrder = [1, 2, 13, 12, 14];
    private readonly Dictionary<int, int> prices = [];

    private WallCatalog() { }

    public static WallCatalog Read(IffFile build, byte language = 1)
    {
        var catalog = new WallCatalog();
        var chunk = build.Find(ResourceType.Str, 0x81) ?? throw new InvalidDataException("Build.iff has no wall STR# 0x81");
        var strings = StringTable.Read(build, chunk).ForLanguage(language);
        for (var index = 0; index < StyleOrder.Length && index * 3 < strings.Count; index++)
            catalog.prices[StyleOrder[index]] = int.TryParse(strings[index * 3].Trim(), out var price) ? price : 0;
        return catalog;
    }

    public static WallCatalog Open(string game, byte language = 1)
    {
        foreach (var folder in new[] { ObjectRegistry.Resolve(game, "GameData"), game })
        {
            if (folder is null || !Directory.Exists(folder)) continue;
            var path = Directory.EnumerateFiles(folder).FirstOrDefault(file =>
                string.Equals(Path.GetFileName(file), "Build.iff", StringComparison.OrdinalIgnoreCase));
            if (path is not null) return Read(IffFile.Open(path), language);
        }
        throw new FileNotFoundException("Build.iff was not found", game);
    }

    public bool Contains(int style) => prices.ContainsKey(style);

    public IReadOnlyCollection<int> Styles => prices.Keys;

    public int Price(int style) => prices.GetValueOrDefault(style);
}
