using SimsEngine.Content;
using SimsEngine.Formats;

namespace SimsEngine.World;

/// <summary>Loads wallpaper pattern prices from Build.iff STR# 0x83, where price strings are stored at
/// 3-string intervals (index 3*(id - 1)). Missing patterns default to a price of zero.</summary>
public sealed class WallpaperCatalog
{
    private readonly Dictionary<int, int> prices = [];

    private WallpaperCatalog() { }

    public static WallpaperCatalog Read(IffFile build, byte language = 1, IEnumerable<KeyValuePair<int, int>>? dynamicWalls = null)
    {
        var catalog = new WallpaperCatalog();
        var chunk = build.Find(ResourceType.Str, 0x83) ?? throw new InvalidDataException("Build.iff has no wall pattern STR# 0x83");
        var strings = StringTable.Read(build, chunk).ForLanguage(language);
        foreach (var id in Enumerable.Range(0, 31).Concat(Enumerable.Range(0xf8, 8)))
        {
            var index = 3 * (id - 1);
            catalog.prices[id] = id > 0 && index < strings.Count && int.TryParse(strings[index].Trim(), out var price) ? price : 0;
        }
        if (dynamicWalls is not null)
        foreach (var (pattern, price) in dynamicWalls)
            if (pattern is > 0 and < 65534) catalog.prices[pattern] = price;
        return catalog;
    }

    public static WallpaperCatalog Open(string game, byte language = 1, IEnumerable<KeyValuePair<int, int>>? dynamicWalls = null)
    {
        foreach (var folder in new[] { ObjectRegistry.Resolve(game, "GameData"), game })
        {
            if (folder is null || !Directory.Exists(folder)) continue;
            var path = Directory.EnumerateFiles(folder).FirstOrDefault(file =>
                string.Equals(Path.GetFileName(file), "Build.iff", StringComparison.OrdinalIgnoreCase));
            if (path is not null)
                return Read(IffFile.Open(path), language, dynamicWalls ?? ReadDynamicWalls(folder, language));
        }
        throw new FileNotFoundException("Build.iff was not found", game);
    }

    private static IEnumerable<KeyValuePair<int, int>> ReadDynamicWalls(string gameData, byte language)
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
        var root = ObjectRegistry.Resolve(gameData, "Walls");
        if (root is null) yield break;
        var pattern = 256;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".wll", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase))
        {
            if (pattern >= 65534) yield break;
            yield return KeyValuePair.Create(pattern++, path);
        }
    }

    public bool Contains(int pattern) => prices.ContainsKey(pattern);

    public int Price(int pattern) => prices.GetValueOrDefault(pattern);
}
