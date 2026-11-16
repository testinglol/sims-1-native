using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SimsSDL.Core;

public sealed record PluginDeclaration
{
    public const int MinSupportedAPIVersion = 1;
    public const int MaxSupportedAPIVersion = 3;

    [JsonRequired]
    [JsonPropertyName("assembly")]
    public required string Assembly { get; init; }

    [JsonRequired]
    [JsonPropertyName("entryType")]
    public required string EntryType { get; init; }

    [JsonRequired]
    [JsonPropertyName("apiVersion")]
    public required int ApiVersion { get; init; }

    [JsonRequired]
    [JsonPropertyName("stateVersion")]
    public required int StateVersion { get; init; }

    public static bool IsSupportedAPIVersion(int version) => version is >= MinSupportedAPIVersion and <= MaxSupportedAPIVersion;
}

public sealed class SaveMigration
{
    [JsonRequired]
    [JsonPropertyName("from")]
    public IReadOnlyList<string> From { get; init; }

    /// <summary>Maps replaced object GUIDs from older versions to new GUIDs. Saved instances load as the
    /// replacement object, which must belong to this pack and occupy the same tile footprint.</summary>
    [JsonPropertyName("objects")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Objects { get; set; }

    [JsonConstructor]
    public SaveMigration(IReadOnlyList<string> from, IReadOnlyDictionary<string, string>? objects = null)
    {
        From = from;
        Objects = objects;
    }

    internal static uint? ParseGuid(string text)
    {
        if (text.Length != 10 ||
            (!text.StartsWith("0x", StringComparison.Ordinal) && !text.StartsWith("0X", StringComparison.Ordinal)))
        {
            return null;
        }

        return uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Parses the object GUID migration map, rejecting malformed entries, self-mappings, and
    /// chained migrations.</summary>
    internal Dictionary<uint, uint> ObjectMap(string packID)
    {
        var map = new Dictionary<uint, uint>();
        if (Objects is not null)
        {
            foreach (var (oldText, newText) in Objects)
            {
                if (oldText is null || newText is null ||
                    ParseGuid(oldText) is not { } fromGuid ||
                    ParseGuid(newText) is not { } toGuid ||
                    fromGuid == 0 || toGuid == 0 || fromGuid == toGuid ||
                    map.ContainsKey(fromGuid))
                {
                    throw new ContentError($"Pack {packID} has an invalid saveMigration object entry: {oldText}");
                }

                map[fromGuid] = toGuid;
            }
        }

        if (map.Count > 4096 || !map.Values.All(v => !map.ContainsKey(v)))
        {
            throw new ContentError($"Pack {packID} saveMigration objects must map old objects directly to current ones");
        }

        return map;
    }
}

public sealed record ObjectMigrationReport(
    [property: JsonPropertyName("from")] uint From,
    [property: JsonPropertyName("to")] uint To);

public sealed partial class ContentPack
{
    internal static string ValidatedPlugin(PluginDeclaration plugin, string packID, string root)
    {
        if (!PluginDeclaration.IsSupportedAPIVersion(plugin.ApiVersion))
        {
            throw new ContentError($"Pack {packID} requires unsupported plugin API version {plugin.ApiVersion}");
        }

        if (plugin.StateVersion < 1 || plugin.StateVersion > 1_000_000)
        {
            throw new ContentError($"Pack {packID} plugin stateVersion must be a positive integer");
        }

        if (plugin.Assembly is null || plugin.EntryType is null)
        {
            throw new ContentError($"Pack {packID} has an invalid plugin declaration");
        }

        var entryParts = plugin.EntryType.Split('.', StringSplitOptions.None);
        var validEntry = plugin.EntryType.Length <= 256 &&
                         entryParts.Length > 0 &&
                         entryParts.All(part =>
                             part.Length > 0 &&
                             (char.IsAsciiLetter(part[0]) || part[0] == '_') &&
                             part.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '+'));
        if (!validEntry)
        {
            throw new ContentError($"Pack {packID} has an invalid plugin entryType");
        }

        return ContainedFile(plugin.Assembly, "plugin", ["dll"], packID, root);
    }

    internal static List<(string Path, string Url, PanelDescription Description)> ValidatedPanels(
        IReadOnlyList<string> paths,
        string packID,
        string root)
    {
        if (paths.Count > 32)
        {
            throw new ContentError($"Pack {packID} declares more than 32 panels");
        }

        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var seenIDs = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(string Path, string Url, PanelDescription Description)>(paths.Count);

        foreach (var path in paths)
        {
            if (path is null)
            {
                throw new ContentError($"Pack {packID} declares a null panel path");
            }

            var url = ContainedFile(path, "panels", ["json"], packID, root);
            if (!seenPaths.Add(url.ToLowerInvariant()))
            {
                throw new ContentError($"Pack {packID} lists panel {path} twice");
            }

            var data = File.ReadAllBytes(url);
            if (data.Length > 262_144)
            {
                throw new ContentError($"Panel {path} in pack {packID} exceeds 256 KiB");
            }

            PanelDescription description;
            try
            {
                description = JsonSerializer.Deserialize<PanelDescription>(data, PackContentPaths.CamelCaseJsonReadOptions)
                    ?? throw new JsonException("Null panel JSON");
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new ContentError($"Panel {path} in pack {packID} is not a valid panel description: {ex.Message}");
            }

            description.Validate();
            if (!seenIDs.Add(description.Id))
            {
                throw new ContentError($"Pack {packID} has two panels with ID {description.Id}");
            }

            result.Add((path, url, description));
        }

        return result;
    }

    private static string ContainedFile(string path, string folder, HashSet<string> extensions, string packID, string root)
    {
        var normalized = path.Replace('\\', '/');
        var components = normalized.Split('/', StringSplitOptions.None);
        if (normalized.StartsWith('/') ||
            components.Length < 2 ||
            !string.Equals(components[0], folder, StringComparison.OrdinalIgnoreCase) ||
            components.Any(c => c.Length == 0 || c == "." || c == ".."))
        {
            throw new ContentError($"Pack {packID} path must be inside {folder}/: {path}");
        }

        var candidate = root;
        foreach (var component in components)
        {
            candidate = System.IO.Path.Combine(candidate, component);
        }

        var ext = System.IO.Path.GetExtension(candidate).TrimStart('.').ToLowerInvariant();
        if (!extensions.Contains(ext))
        {
            throw new ContentError($"Pack {packID} file has an unsupported extension: {path}");
        }

        if (!PackContentPaths.IsRegularFile(candidate))
        {
            throw new ContentError($"Pack {packID} is missing {path}");
        }

        var resolved = PackContentPaths.ResolveSymlinksInPath(candidate);
        if (!PackContentPaths.IsUnderCanonicalRoot(resolved, root))
        {
            throw new ContentError($"Pack {packID} file escapes its directory: {path}");
        }

        return resolved;
    }
}

public sealed record PackPluginReport(
    [property: JsonPropertyName("packID")] string PackID,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("assembly")] string Assembly,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("entryType")] string EntryType,
    [property: JsonPropertyName("apiVersion")] int ApiVersion,
    [property: JsonPropertyName("stateVersion")] int StateVersion);

public sealed record PackPanelReport(
    [property: JsonPropertyName("packID")] string PackID,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record PackDetailReport(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("migratesFrom")] IReadOnlyList<string> MigratesFrom,
    [property: JsonPropertyName("objects"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ObjectMigrationReport>? Objects = null);

public static class PackPlugins
{
    /// <summary>Collects payload files for a plugin folder covered by trust verification, including regular
    /// <c>.dll</c> and <c>.json</c> files sorted by lowercase name.</summary>
    public static List<string> PluginPayload(string folder)
    {
        if (!Directory.Exists(folder))
        {
            throw new ContentError($"Missing plugin folder: {folder}");
        }

        return Directory.EnumerateFileSystemEntries(folder)
            .Where(static file =>
            {
                var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
                if (ext != "dll" && ext != "json")
                {
                    return false;
                }
                return PackContentPaths.IsRegularFile(file);
            })
            .OrderBy(static file => Path.GetFileName(file).ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Computes the trust digest of a plugin folder as the SHA-256 hash of newline-terminated
    /// <c>name:sha256</c> entries.</summary>
    public static string PluginDigest(string folder)
    {
        var sb = new StringBuilder();
        foreach (var file in PluginPayload(folder))
        {
            var data = File.ReadAllBytes(file);
            sb.Append(Path.GetFileName(file).ToLowerInvariant());
            sb.Append(':');
            sb.Append(PackContentPaths.Sha256Hex(data));
            sb.Append('\n');
        }
        return PackContentPaths.Sha256Hex(sb.ToString());
    }
}

public sealed partial class PackStack
{
    public const string MountedPluginFolder = ".plugins";

    public List<PackPluginReport> PluginReports()
    {
        var result = new List<PackPluginReport>();
        foreach (var pack in Ordered)
        {
            if (pack.Manifest.Plugin is not { } plugin || pack.PluginAssembly is not { } assembly)
            {
                continue;
            }

            var folder = Path.GetDirectoryName(assembly)!;
            result.Add(new PackPluginReport(
                PackID: pack.Manifest.Id,
                Version: pack.Manifest.Version,
                Assembly: MountedPluginFolder + "/" + pack.Manifest.Id + "/" + Path.GetFileName(assembly),
                Sha256: PackPlugins.PluginDigest(folder),
                EntryType: plugin.EntryType,
                ApiVersion: plugin.ApiVersion,
                StateVersion: plugin.StateVersion));
        }

        return result;
    }

    public List<PackPanelReport> PanelReports()
    {
        var result = new List<PackPanelReport>();
        foreach (var pack in Ordered)
        {
            foreach (var panel in pack.PanelFiles)
            {
                result.Add(new PackPanelReport(
                    PackID: pack.Manifest.Id,
                    Id: panel.Description.Id,
                    Path: panel.Path,
                    Sha256: PackContentPaths.Sha256Hex(File.ReadAllBytes(panel.Url))));
            }
        }

        return result;
    }

    public List<PackDetailReport> PackDetails()
    {
        var result = new List<PackDetailReport>(Ordered.Count);
        foreach (var pack in Ordered)
        {
            Dictionary<uint, uint>? map = null;
            if (pack.Manifest.SaveMigration is { } migration)
            {
                try
                {
                    map = migration.ObjectMap(pack.Manifest.Id);
                }
                catch
                {
                    map = null;
                }
            }

            List<ObjectMigrationReport>? objReports = map is { Count: > 0 }
                ? map.OrderBy(static kv => kv.Key).Select(static kv => new ObjectMigrationReport(kv.Key, kv.Value)).ToList()
                : null;

            result.Add(new PackDetailReport(
                Id: pack.Manifest.Id,
                Version: pack.Manifest.Version,
                MigratesFrom: pack.Manifest.SaveMigration?.From ?? [],
                Objects: objReports));
        }

        return result;
    }

    public Dictionary<string, Dictionary<string, PanelDescription>> PanelDescriptions()
    {
        var result = new Dictionary<string, Dictionary<string, PanelDescription>>(StringComparer.Ordinal);
        foreach (var pack in Ordered)
        {
            foreach (var panel in pack.PanelFiles)
            {
                if (!result.TryGetValue(pack.Manifest.Id, out var packPanels))
                {
                    packPanels = new Dictionary<string, PanelDescription>(StringComparer.Ordinal);
                    result[pack.Manifest.Id] = packPanels;
                }
                packPanels[panel.Description.Id] = panel.Description;
            }
        }

        return result;
    }
}

/// <summary>Stores trusted plugin assembly records. Each entry binds a pack ID, version, and assembly
/// SHA-256 hash, requiring reauthorization when an assembly changes.</summary>
public sealed class PluginTrustStore
{
    public sealed record Entry(
        [property: JsonRequired, JsonPropertyName("packId")] string PackId,
        [property: JsonRequired, JsonPropertyName("version")] string Version,
        [property: JsonRequired, JsonPropertyName("sha256")] string Sha256,
        [property: JsonRequired, JsonPropertyName("trustedAt")] string TrustedAt);

    private sealed class TrustFile
    {
        [JsonRequired]
        [JsonPropertyName("schemaVersion")]
        public required int SchemaVersion { get; init; }

        [JsonRequired]
        [JsonPropertyName("plugins")]
        public required List<Entry> Plugins { get; init; }
    }

    private readonly List<Entry> _entries;

    public string Url { get; }
    public IReadOnlyList<Entry> Entries => _entries;

    public static string StoreUrl(string dataRoot) => Path.Combine(dataRoot, "Settings", "plugin-trust.json");

    public PluginTrustStore(string dataRoot)
    {
        Url = StoreUrl(dataRoot);
        if (!File.Exists(Url))
        {
            _entries = [];
            return;
        }

        var data = File.ReadAllBytes(Url);
        var file = JsonSerializer.Deserialize<TrustFile>(data, PackContentPaths.JsonReadOptions)
            ?? throw new ContentError("Invalid plugin trust store");
        if (file.SchemaVersion != 1)
        {
            throw new ContentError($"Unsupported plugin trust store version {file.SchemaVersion}");
        }

        if (file.Plugins is null || file.Plugins.Any(static e => e is null || e.PackId is null || e.Version is null || e.Sha256 is null || e.TrustedAt is null))
        {
            throw new ContentError("Invalid plugin trust store entries");
        }

        _entries = file.Plugins;
    }

    public bool IsTrusted(PackPluginReport plugin)
    {
        return _entries.Any(e => e.PackId == plugin.PackID && e.Version == plugin.Version && e.Sha256 == plugin.Sha256);
    }

    public void Trust(PackPluginReport plugin, DateTimeOffset? now = null)
    {
        var timestamp = (now ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        _entries.RemoveAll(e => e.PackId == plugin.PackID);
        _entries.Add(new Entry(plugin.PackID, plugin.Version, plugin.Sha256, timestamp));
        Save();
    }

    public bool Revoke(string packID)
    {
        var removed = _entries.RemoveAll(e => e.PackId == packID);
        if (removed == 0)
        {
            return false;
        }
        Save();
        return true;
    }

    private void Save()
    {
        var parent = Path.GetDirectoryName(Url);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var pluginsArray = new JsonArray();
        foreach (var entry in _entries.OrderBy(static e => e.PackId, StringComparer.Ordinal))
        {
            pluginsArray.Add(new JsonObject
            {
                ["packId"] = entry.PackId,
                ["sha256"] = entry.Sha256,
                ["trustedAt"] = entry.TrustedAt,
                ["version"] = entry.Version
            });
        }

        var root = new JsonObject
        {
            ["plugins"] = pluginsArray,
            ["schemaVersion"] = 1
        };

        var bytes = GameContent.SerializeSortedJson(root);
        var tmp = Url + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, Url, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                {
                }
            }
        }
    }
}
