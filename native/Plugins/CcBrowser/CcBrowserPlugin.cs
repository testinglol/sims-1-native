using System.Text.Json;
using SimsNative.Plugins;

namespace Local.CcBrowser;

/// <summary>
/// Buy Mode Custom Content (CC) Folder Browser plugin (API v5).
/// Groups custom .iff objects from Downloads/ and UserObjects/ by subfolder, exposes a dedicated
/// "Folders" Directory Lister view inside Buy Mode (hotkey F), and optionally hides matched CC
/// from the standard Buy Mode categories to keep the main catalog uncluttered.
/// </summary>
public sealed class CcBrowserPlugin : ISimsPlugin, IPluginBuyCatalogProvider
{
    private sealed record Configuration(
        string TabLabel = "Folders",
        string Hotkey = "f",
        bool HideFromStandardCatalog = true);

    private Configuration _config = new();

    public void Initialize(IPluginContext context)
    {
        try
        {
            var raw = context.ReadPluginData("cc-browser.json");
            var parsed = JsonSerializer.Deserialize<Configuration>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (parsed is not null)
                _config = parsed;
        }
        catch
        {
            _config = new Configuration();
        }
    }

    public PluginBuyCatalogView? GetBuyCatalogView(IReadOnlyList<PluginCatalogObject> objects)
    {
        var byFolder = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);

        foreach (var obj in objects)
        {
            if (!obj.IsCustomContent)
                continue;

            var folder = ExtractFolderLabel(obj.SourcePath);
            if (!byFolder.TryGetValue(folder, out var list))
            {
                list = [];
                byFolder[folder] = list;
            }
            list.Add(obj.Guid);
        }

        var collections = byFolder
            .OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static kv => new PluginBuyCollection(
                Id: kv.Key.ToLowerInvariant().Replace(' ', '-'),
                Label: kv.Key,
                Tooltip: $"{kv.Key} ({kv.Value.Count} item{(kv.Value.Count == 1 ? "" : "s")})",
                Guids: [.. kv.Value]))
            .ToArray();

        return new PluginBuyCatalogView(
            TabId: "cc-folders",
            TabLabel: string.IsNullOrWhiteSpace(_config.TabLabel) ? "Folders" : _config.TabLabel,
            Hotkey: string.IsNullOrWhiteSpace(_config.Hotkey) ? "f" : _config.Hotkey,
            HideMatchedFromStandardCatalog: _config.HideFromStandardCatalog,
            Collections: collections);
    }

    internal static string ExtractFolderLabel(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return "Downloads";

        var normalized = sourcePath.Replace('\\', '/').TrimStart('/');
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash <= 0)
            return "Downloads";

        var dir = normalized[..lastSlash];

        // Strip leading "Downloads/" or "GameData/UserObjects/" so subfolder names stay clean and readable.
        const string downloadsPrefix = "Downloads/";
        const string userObjectsPrefix = "GameData/UserObjects/";
        if (dir.StartsWith(downloadsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var sub = dir[downloadsPrefix.Length..].Trim('/');
            return string.IsNullOrEmpty(sub) ? "Downloads" : sub;
        }
        if (dir.StartsWith(userObjectsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var sub = dir[userObjectsPrefix.Length..].Trim('/');
            return string.IsNullOrEmpty(sub) ? "UserObjects" : sub;
        }
        if (dir.Equals("Downloads", StringComparison.OrdinalIgnoreCase))
            return "Downloads";
        if (dir.Equals("GameData/UserObjects", StringComparison.OrdinalIgnoreCase)
            || dir.Equals("UserObjects", StringComparison.OrdinalIgnoreCase))
            return "UserObjects";

        var idx = dir.IndexOf("/Downloads/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var sub = dir[(idx + "/Downloads/".Length)..].Trim('/');
            return string.IsNullOrEmpty(sub) ? "Downloads" : sub;
        }

        return Path.GetFileName(dir);
    }
}
