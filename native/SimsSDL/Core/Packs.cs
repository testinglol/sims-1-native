using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace SimsSDL.Core;

internal static class PackContentPaths
{
    internal static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    internal static readonly JsonSerializerOptions CamelCaseJsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static string ResourceKey(string path)
    {
        var normalized = path.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.None);
        if (parts.Length == 0)
        {
            throw new ContentError($"Unsafe resource path: {path}");
        }

        foreach (var part in parts)
        {
            if (part.Length == 0 || part == "." || part == ".." || part.Contains(':') || part.Contains('\0'))
            {
                throw new ContentError($"Unsafe resource path: {path}");
            }
        }

        return string.Join('/', parts).ToLowerInvariant();
    }

    internal static string NormalizeSeparators(string path) => path.Replace('\\', '/');

    internal static string TrimTrailingSeparator(string path)
    {
        var norm = NormalizeSeparators(path);
        if (norm.Length <= 1)
        {
            return norm;
        }
        if (OperatingSystem.IsWindows() && norm.Length == 3 && norm[1] == ':' && norm[2] == '/')
        {
            return norm;
        }
        return norm.TrimEnd('/');
    }

    internal static string ResolveSymlinksInPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var remainder = full[root.Length..];
        if (string.IsNullOrEmpty(remainder))
        {
            return TrimTrailingSeparator(full);
        }

        var current = root;
        var parts = remainder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            if (TryResolveFinalLinkTarget(current, out var resolved))
            {
                current = resolved;
            }
        }

        return TrimTrailingSeparator(Path.GetFullPath(current));
    }

    internal static bool IsSymbolicLink(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var winAttrs = File.GetAttributes(path);
                return winAttrs != (FileAttributes)(-1) && (winAttrs & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return false;
            }
        }

        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null)
            {
                return true;
            }
            var attrs = info.Attributes;
            if (attrs != (FileAttributes)(-1) && (attrs & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }
        catch
        {
        }

        try
        {
            var dirInfo = new DirectoryInfo(path);
            if (dirInfo.LinkTarget is not null)
            {
                return true;
            }
            var dirAttrs = dirInfo.Attributes;
            if (dirAttrs != (FileAttributes)(-1) && (dirAttrs & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    internal static bool IsHiddenEntry(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name) || name.StartsWith('.'))
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.Hidden) != 0)
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    internal static bool IsRegularFile(string path)
    {
        if (IsSymbolicLink(path))
        {
            return false;
        }
        return File.Exists(path);
    }

    internal static bool SamePath(string a, string b)
    {
        var normA = ResolveSymlinksInPath(a);
        var normB = ResolveSymlinksInPath(b);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(normA, normB, comparison);
    }

    internal static string? RelativePath(string child, string parent)
    {
        var normParent = TrimTrailingSeparator(ResolveSymlinksInPath(parent));
        var prefix = normParent.EndsWith('/') ? normParent : normParent + "/";
        var normChild = NormalizeSeparators(ResolveSymlinksInPath(child));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!normChild.StartsWith(prefix, comparison))
        {
            return null;
        }
        return normChild[prefix.Length..];
    }

    internal static bool IsUnderCanonicalRoot(string canonicalCandidate, string canonicalParent)
    {
        var normParent = TrimTrailingSeparator(NormalizeSeparators(canonicalParent));
        var prefix = normParent.EndsWith('/') ? normParent : normParent + "/";
        var normCandidate = NormalizeSeparators(canonicalCandidate);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return normCandidate.StartsWith(prefix, comparison) && normCandidate.Length >= prefix.Length;
    }

    internal static string Sha256Hex(ReadOnlySpan<byte> data)
    {
        return Convert.ToHexStringLower(SHA256.HashData(data));
    }

    internal static string Sha256Hex(string utf8Text)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(utf8Text)));
    }

    internal static string HexGUID(uint guid) => $"0x{guid:X8}";

    internal static string TrimHorizontalWhitespace(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        int start = 0;
        while (start < value.Length && IsHorizontalWhitespace(value[start]))
        {
            start++;
        }

        int end = value.Length - 1;
        while (end >= start && IsHorizontalWhitespace(value[end]))
        {
            end--;
        }

        return value[start..(end + 1)];
    }

    internal static bool IsHorizontalWhitespaceOnly(string value)
    {
        foreach (var ch in value)
        {
            if (!IsHorizontalWhitespace(ch))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsHorizontalWhitespace(char ch)
    {
        return ch == ' ' || ch == '\t' || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.SpaceSeparator;
    }

    private static bool TryResolveFinalLinkTarget(string path, out string resolved)
    {
        bool isWindowsDir = false;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var attrs = File.GetAttributes(path);
                if (attrs == (FileAttributes)(-1) || (attrs & FileAttributes.ReparsePoint) == 0)
                {
                    resolved = string.Empty;
                    return false;
                }
                isWindowsDir = (attrs & FileAttributes.Directory) != 0;
            }
            catch
            {
                resolved = string.Empty;
                return false;
            }
        }

        try
        {
            var target = isWindowsDir
                ? Directory.ResolveLinkTarget(path, returnFinalTarget: true)
                : File.ResolveLinkTarget(path, returnFinalTarget: true);
            if (target is not null)
            {
                resolved = Path.GetFullPath(target.FullName);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var current = path;
        var changed = false;
        for (int depth = 0; depth < 16; depth++)
        {
            string? rawTarget = null;
            try
            {
                rawTarget = new FileInfo(current).LinkTarget ?? new DirectoryInfo(current).LinkTarget;
            }
            catch
            {
                break;
            }

            if (string.IsNullOrEmpty(rawTarget))
            {
                break;
            }

            changed = true;
            if (Path.IsPathRooted(rawTarget))
            {
                current = Path.GetFullPath(rawTarget);
            }
            else
            {
                var parent = Path.GetDirectoryName(current) ?? "/";
                current = Path.GetFullPath(Path.Combine(parent, rawTarget));
            }
        }

        if (changed)
        {
            resolved = current;
            return true;
        }

        resolved = string.Empty;
        return false;
    }

    internal static IEnumerable<string> EnumerateFilesRecursively(string rootDirectory, bool includeSymlinkFiles = false, Action<string>? onSymlinkEncountered = null)
    {
        var stack = new Stack<string>();
        stack.Push(rootDirectory);

        while (stack.Count > 0)
        {
            var currentDir = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(currentDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ContentError($"Cannot enumerate directory {currentDir}: {ex.Message}");
            }

            var orderedEntries = entries.ToList();
            orderedEntries.Sort(StringComparer.Ordinal);

            for (int i = orderedEntries.Count - 1; i >= 0; i--)
            {
                var entry = orderedEntries[i];
                if (IsHiddenEntry(entry))
                {
                    continue;
                }

                if (IsSymbolicLink(entry))
                {
                    onSymlinkEncountered?.Invoke(entry);
                    if (includeSymlinkFiles && !Directory.Exists(entry))
                    {
                        yield return entry;
                    }
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    stack.Push(entry);
                }
                else if (File.Exists(entry))
                {
                    yield return entry;
                }
            }
        }
    }
}

public sealed class PackManifest
{
    public sealed class Dependency
    {
        [JsonRequired]
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonRequired]
        [JsonPropertyName("version")]
        public required string Version { get; init; }
    }

    [JsonRequired]
    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    [JsonRequired]
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonRequired]
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonRequired]
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonRequired]
    [JsonPropertyName("baseGameVersion")]
    public required string BaseGameVersion { get; init; }

    [JsonRequired]
    [JsonPropertyName("dependencies")]
    public required IReadOnlyList<Dependency> Dependencies { get; init; }

    [JsonRequired]
    [JsonPropertyName("overrides")]
    public required IReadOnlyList<string> Overrides { get; init; }

    [JsonPropertyName("objectGUIDs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, uint>? ObjectGUIDs { get; set; }

    [JsonPropertyName("plugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginDeclaration? Plugin { get; set; }

    [JsonPropertyName("panels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Panels { get; set; }

    [JsonPropertyName("saveMigration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SaveMigration? SaveMigration { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("preview")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Preview { get; set; }
}

public sealed partial class ContentPack
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "iff", "far", "flr", "wll", "bmp", "png", "tga", "jpg", "jpeg", "skn", "cmx", "bcf", "cfp",
        "hit", "hot", "hsm", "wav", "mp3", "xa", "fnt", "pal", "rt", "txt", "bmf", "bco", "msl",
        "spv", "obj", "mtl"
    };

    private static readonly HashSet<string> SupportedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "gamedata", "uigraphics", "downloads", "sounddata", "music", "expansionshared"
    };

    public string Directory { get; }
    internal byte[] ManifestData { get; }
    public PackManifest Manifest { get; }
    public IReadOnlyDictionary<string, string> Assets { get; }

    internal IReadOnlyDictionary<string, string> AuthoredPaths { get; }

    public string? PluginAssembly { get; }
    public IReadOnlyList<(string Path, string Url, PanelDescription Description)> PanelFiles { get; }

    public ContentPack(string directory)
    {
        var canonicalDirectory = PackContentPaths.ResolveSymlinksInPath(directory);
        Directory = canonicalDirectory;

        var manifestPath = Path.Combine(directory, "pack.json");
        ManifestData = File.ReadAllBytes(manifestPath);
        Manifest = JsonSerializer.Deserialize<PackManifest>(ManifestData, PackContentPaths.JsonReadOptions)
            ?? throw new ContentError($"Unsupported pack manifest in {Path.GetFileName(directory)}");

        if (Manifest.SchemaVersion != 1 || Manifest.BaseGameVersion != "1.90" ||
            string.IsNullOrEmpty(Manifest.Id) ||
            !Manifest.Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '.') ||
            string.IsNullOrEmpty(Manifest.Version) || string.IsNullOrEmpty(Manifest.Name) ||
            Manifest.Dependencies is null || Manifest.Overrides is null ||
            Manifest.Dependencies.Any(d => d is null || d.Id is null || d.Version is null) ||
            Manifest.Overrides.Any(o => o is null))
        {
            throw new ContentError($"Unsupported pack manifest in {Path.GetFileName(directory)}");
        }

        var files = PackContentPaths.ResolveSymlinksInPath(Path.Combine(directory, "files"));
        if (!PackContentPaths.IsUnderCanonicalRoot(files, canonicalDirectory))
        {
            throw new ContentError("Pack files directory escapes its pack");
        }

        if (!System.IO.Directory.Exists(files))
        {
            throw new ContentError($"Pack {Manifest.Id} has no files directory");
        }

        var canonicalRoot = PackContentPaths.TrimTrailingSeparator(files);
        var rootPrefix = canonicalRoot.EndsWith('/') ? canonicalRoot : canonicalRoot + "/";

        var assets = new Dictionary<string, string>(StringComparer.Ordinal);
        var authored = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in PackContentPaths.EnumerateFilesRecursively(
            files,
            includeSymlinkFiles: false,
            onSymlinkEncountered: symPath =>
            {
                var resolvedSym = PackContentPaths.ResolveSymlinksInPath(symPath);
                if (!PackContentPaths.IsUnderCanonicalRoot(resolvedSym, canonicalRoot))
                {
                    throw new ContentError($"Pack resource escapes its directory: {Path.GetFileName(symPath)}");
                }
            }))
        {
            var resolvedFile = PackContentPaths.ResolveSymlinksInPath(file);
            if (!PackContentPaths.IsUnderCanonicalRoot(resolvedFile, canonicalRoot))
            {
                throw new ContentError($"Pack resource escapes its directory: {Path.GetFileName(file)}");
            }

            var relative = PackContentPaths.NormalizeSeparators(resolvedFile)[rootPrefix.Length..];
            var key = PackContentPaths.ResourceKey(relative);
            var slashIdx = key.IndexOf('/');
            var root = slashIdx >= 0 ? key[..slashIdx] : key;
            var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();

            if ((!SupportedRoots.Contains(root) && !IsTownRoot(root)) ||
                (!SupportedExtensions.Contains(ext) && !(IsTownRoot(root) && ext == "json")))
            {
                throw new ContentError($"Unsupported pack asset: {relative}");
            }

            if (assets.ContainsKey(key))
            {
                throw new ContentError($"Pack has duplicate resource: {relative}");
            }

            assets[key] = file;
            authored[key] = relative;
        }

        foreach (var overridePath in Manifest.Overrides)
        {
            if (!assets.ContainsKey(PackContentPaths.ResourceKey(overridePath)))
            {
                throw new ContentError($"Pack override does not name a supplied resource: {overridePath}");
            }
        }

        Assets = assets;
        AuthoredPaths = authored;

        var packID = Manifest.Id;
        PluginAssembly = Manifest.Plugin is { } pluginDecl
            ? ValidatedPlugin(pluginDecl, packID, canonicalDirectory)
            : null;
        PanelFiles = ValidatedPanels(Manifest.Panels ?? [], packID, canonicalDirectory);

        if (Manifest.SaveMigration is { } migration)
        {
            if (migration.From is null || migration.From.Count == 0 || migration.From.Count > 64 ||
                !migration.From.All(v => !string.IsNullOrEmpty(v) && v.Length <= 32 && v != Manifest.Version))
            {
                throw new ContentError($"Pack {Manifest.Id} has an invalid saveMigration list");
            }
            _ = migration.ObjectMap(Manifest.Id);
        }
    }

    /// <summary>Checks whether a directory defines a custom neighborhood. Folders <c>UserData2</c> through
    /// <c>UserData99</c> supply numbered neighborhoods, while base <c>UserData</c> is reserved for the
    /// primary neighborhood.</summary>
    internal static bool IsTownRoot(string root)
    {
        if (!root.StartsWith("userdata", StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = root["userdata".Length..];
        if (suffix.Length == 0 || !suffix.All(char.IsAsciiDigit))
        {
            return false;
        }

        return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 2 && number <= 99;
    }
}

public sealed partial class PackStack
{
    public IReadOnlyList<ContentPack> Ordered { get; }
    public IReadOnlyDictionary<string, string> Assets { get; }

    internal IReadOnlyDictionary<string, string> AuthoredPaths { get; }

    public IReadOnlyList<PackObjectDefinition> Objects { get; }
    public IReadOnlyList<PackAvatarResource> AvatarResources { get; }
    internal IReadOnlyDictionary<string, string> AvatarContainers { get; }
    public IReadOnlyList<ObjectGUIDReservation> Reservations { get; }
    internal IReadOnlyDictionary<string, string> ObjectResources { get; }
    public IReadOnlyList<(string Path, string WinnerPackId, string LoserPackId)> OverriddenResources { get; }

    public PackStack(IEnumerable<ContentPack> packs, bool preserveOrderAndAllowOverrides = false)
    {
        var byID = new Dictionary<string, ContentPack>(StringComparer.Ordinal);
        var inputOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        int nextIdx = 0;
        foreach (var pack in packs)
        {
            if (!byID.TryAdd(pack.Manifest.Id, pack))
            {
                throw new ContentError($"Duplicate pack ID: {pack.Manifest.Id}");
            }
            inputOrder[pack.Manifest.Id] = nextIdx++;
        }

        var pending = new HashSet<string>(byID.Keys, StringComparer.Ordinal);
        var complete = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<ContentPack>();

        while (pending.Count > 0)
        {
            var progress = false;
            IEnumerable<string> sequence = preserveOrderAndAllowOverrides
                ? pending.OrderBy(id => inputOrder[id])
                : pending.OrderBy(static x => x, StringComparer.Ordinal);

            foreach (var id in sequence.ToArray())
            {
                var pack = byID[id];
                foreach (var dependency in pack.Manifest.Dependencies)
                {
                    if (!byID.TryGetValue(dependency.Id, out var required) ||
                        (dependency.Version != "*" && required.Manifest.Version != dependency.Version))
                    {
                        throw new ContentError($"Pack {id} requires {dependency.Id} version {dependency.Version}");
                    }
                }

                if (pack.Manifest.Dependencies.All(d => complete.Contains(d.Id)))
                {
                    ordered.Add(pack);
                    pending.Remove(id);
                    complete.Add(id);
                    progress = true;
                }
            }

            if (!progress)
            {
                throw new ContentError("Pack dependencies contain a cycle");
            }
        }

        var resources = new Dictionary<string, string>(StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var authoredPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var skinPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var overridden = new List<(string Path, string WinnerPackId, string LoserPackId)>();

        foreach (var pack in ordered)
        {
            var declared = new HashSet<string>(pack.Manifest.Overrides.Select(PackContentPaths.ResourceKey), StringComparer.Ordinal);
            foreach (var (key, file) in pack.Assets)
            {
                if (GameContent.SkinName(key) is { } skinName)
                {
                    if (skinPaths.TryGetValue(skinName, out var existingSkinPath) && existingSkinPath != key)
                    {
                        throw new ContentError($"Skin {skinName} has conflicting paths; use {existingSkinPath} in every pack");
                    }
                    skinPaths[skinName] = key;
                }

                if (resources.ContainsKey(key))
                {
                    if (!preserveOrderAndAllowOverrides && !declared.Contains(key))
                    {
                        throw new ContentError($"Conflicting pack resource {key}; declare it in overrides");
                    }
                    overridden.Add((key, pack.Manifest.Id, owners[key]));
                }

                resources[key] = file;
                authoredPaths[key] = pack.AuthoredPaths[key];
                owners[key] = pack.Manifest.Id;
            }
        }

        var (avatarDefinitions, avatarContainers) = CollectAvatarResources(resources, owners);
        AvatarResources = avatarDefinitions;
        AvatarContainers = avatarContainers;

        var objectAssets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in resources)
        {
            var slash = k.IndexOf('/');
            var root = slash >= 0 ? k[..slash] : k;
            if (!ContentPack.IsTownRoot(root))
            {
                objectAssets[k] = v;
            }
        }

        Objects = CollectObjectDefinitions(objectAssets, owners);
        Reservations = ObjectGUIDAllocator.CollectReservations(ordered, Objects);

        var owned = new Dictionary<uint, string>();
        foreach (var obj in Objects)
        {
            owned.TryAdd(obj.Guid, obj.Owner);
        }

        foreach (var pack in ordered)
        {
            var migrationMap = pack.Manifest.SaveMigration?.ObjectMap(pack.Manifest.Id);
            if (migrationMap is null)
            {
                continue;
            }

            foreach (var (oldGuid, newGuid) in migrationMap)
            {
                if (!owned.TryGetValue(newGuid, out var newOwner) || newOwner != pack.Manifest.Id)
                {
                    throw new ContentError($"Pack {pack.Manifest.Id} migrates object {PackContentPaths.HexGUID(oldGuid)} to {PackContentPaths.HexGUID(newGuid)}, which is not one of its objects");
                }

                if (owned.ContainsKey(oldGuid))
                {
                    throw new ContentError($"Pack {pack.Manifest.Id} migrates object {PackContentPaths.HexGUID(oldGuid)}, which is still defined");
                }
            }
        }

        ObjectResources = BuildObjectResourceIndex(objectAssets);
        Ordered = ordered;
        Assets = resources;
        AuthoredPaths = authoredPaths;
        OverriddenResources = overridden;
    }
}

public sealed partial class GameContent
{
    public string Root { get; }
    public InstalledGameLanguage Language { get; }
    public FARArchive UI { get; }
    public PackStack Packs { get; }
    public IReadOnlyList<string> DisabledDownloadFolders { get; }
    public IReadOnlyList<(string Path, string PackId)> BaseOverriddenResources { get; }

    public GameContent(
        string root,
        IEnumerable<string>? packDirectories = null,
        bool preserveOrderAndAllowOverrides = false,
        IEnumerable<string>? disabledDownloadFolders = null)
    {
        var baseRoot = PackContentPaths.ResolveSymlinksInPath(root);
        Root = baseRoot;
        Language = InstalledGameLanguage.Detect(baseRoot);
        UI = new FARArchive(Path.Combine(baseRoot, "UIGraphics", "UIGraphics.far"));
        DisabledDownloadFolders = (disabledDownloadFolders ?? [])
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static s => s, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var loadedPacks = (packDirectories ?? []).Select(dir => new ContentPack(dir)).ToList();
        Packs = new PackStack(loadedPacks, preserveOrderAndAllowOverrides);
        _ = Packs.ValidateBaseObjects(baseRoot);
        Packs.ValidateBaseAvatarResources(baseRoot);

        var archivedSkins = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var looseSkins = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        if (Packs.Assets.Keys.Any(k => SkinName(k) is not null))
        {
            var basePrefix = PackContentPaths.TrimTrailingSeparator(baseRoot);
            var slashPrefix = basePrefix.EndsWith('/') ? basePrefix : basePrefix + "/";

            foreach (var file in PackContentPaths.EnumerateFilesRecursively(baseRoot))
            {
                var resolved = PackContentPaths.ResolveSymlinksInPath(file);
                if (!PackContentPaths.IsUnderCanonicalRoot(resolved, baseRoot))
                {
                    throw new ContentError("Base skin resource escapes game directory");
                }

                var relative = PackContentPaths.ResourceKey(PackContentPaths.NormalizeSeparators(resolved)[slashPrefix.Length..]);
                if (SkinName(relative) is { } sName)
                {
                    if (!looseSkins.TryGetValue(sName, out var paths))
                    {
                        paths = new HashSet<string>(StringComparer.Ordinal);
                        looseSkins[sName] = paths;
                    }
                    paths.Add(relative);
                }

                if (string.Equals(Path.GetExtension(file), ".far", StringComparison.OrdinalIgnoreCase))
                {
                    var archive = new FARArchive(file);
                    foreach (var entry in archive.Entries.Keys)
                    {
                        var lastSlash = entry.LastIndexOf('/');
                        var filename = lastSlash >= 0 ? entry[(lastSlash + 1)..] : entry;
                        if (filename.Length > 0 && SkinIdentity(filename) is { } identity)
                        {
                            if (!archivedSkins.TryGetValue(identity, out var set))
                            {
                                set = new HashSet<string>(StringComparer.Ordinal);
                                archivedSkins[identity] = set;
                            }
                            set.Add(filename);
                        }
                    }
                }
            }
        }

        var baseOverrides = new List<(string Path, string PackId)>();
        foreach (var pack in Packs.Ordered)
        {
            var declared = new HashSet<string>(pack.Manifest.Overrides.Select(PackContentPaths.ResourceKey), StringComparer.Ordinal);
            foreach (var key in pack.Assets.Keys)
            {
                var uiKey = key.StartsWith("uigraphics/", StringComparison.Ordinal) ? key["uigraphics/".Length..] : string.Empty;
                var name = SkinName(key);
                if (name is not null && archivedSkins.TryGetValue(name, out var originals))
                {
                    var lastSlash = key.LastIndexOf('/');
                    var filename = lastSlash >= 0 ? key[(lastSlash + 1)..] : key;
                    if (!originals.Contains(filename))
                    {
                        var sortedOriginals = originals.OrderBy(static x => x, StringComparer.Ordinal);
                        throw new ContentError($"Keep the original skin extension when replacing {name}: {string.Join(", ", sortedOriginals)}");
                    }
                }

                if (name is not null && looseSkins.TryGetValue(name, out var loosePaths) && loosePaths.Any(p => p != key))
                {
                    var sortedLoose = loosePaths.OrderBy(static x => x, StringComparer.Ordinal);
                    throw new ContentError($"Skin {name} conflicts with another base skin path: {string.Join(", ", sortedLoose)}");
                }

                var existsInArchive =
                    (key.StartsWith("uigraphics/", StringComparison.Ordinal) && UI.Entries.ContainsKey(uiKey)) ||
                    (name is not null && archivedSkins.ContainsKey(name));
                var existsOnDisk = DiskPath(key) is not null;

                if (existsInArchive || existsOnDisk)
                {
                    if (!preserveOrderAndAllowOverrides && !declared.Contains(key))
                    {
                        throw new ContentError($"Pack {pack.Manifest.Id} replaces a base resource without declaring an override: {key}");
                    }
                    baseOverrides.Add((key, pack.Manifest.Id));
                }
            }
        }
        BaseOverriddenResources = baseOverrides;
    }

    internal static string? SkinIdentity(string name)
    {
        var lastDot = name.LastIndexOf('.');
        var ext = lastDot >= 0 ? name[(lastDot + 1)..] : name;
        var dropLen = ext.Length + 1;
        var stem = dropLen >= name.Length ? string.Empty : name[..^dropLen];
        return ext switch
        {
            "bmp" or "tga" => "texture:" + stem,
            "skn" or "bmf" => "mesh:" + stem,
            "cmx" or "bcf" or "cfp" => name,
            _ => null
        };
    }

    internal static string? SkinName(string key)
    {
        if (!key.StartsWith("gamedata/skins/", StringComparison.Ordinal) &&
            !key.StartsWith("expansionshared/skinsbuy/", StringComparison.Ordinal))
        {
            return null;
        }

        var lastSlash = key.LastIndexOf('/');
        var filename = lastSlash >= 0 ? key[(lastSlash + 1)..] : key;
        return filename.Length > 0 ? SkinIdentity(filename) : null;
    }

    private string? DiskPath(string resourcePath)
    {
        var current = Root;
        foreach (var part in resourcePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(current))
            {
                return null;
            }

            string? matched = null;
            foreach (var candidate in Directory.EnumerateFileSystemEntries(current))
            {
                if (string.Equals(Path.GetFileName(candidate), part, StringComparison.OrdinalIgnoreCase))
                {
                    matched = candidate;
                    break;
                }
            }

            if (matched is null)
            {
                return null;
            }

            current = matched;
        }

        return current;
    }

    public byte[] Read(string path)
    {
        var key = PackContentPaths.ResourceKey(path);
        if (Packs.Assets.TryGetValue(key, out var overlayPath))
        {
            return File.ReadAllBytes(overlayPath);
        }

        if (DiskPath(key) is { } loosePath)
        {
            return File.ReadAllBytes(loosePath);
        }

        if (key.StartsWith("uigraphics/", StringComparison.Ordinal))
        {
            return UI.Read(key["uigraphics/".Length..]);
        }

        throw new ContentError($"Missing game resource: {path}");
    }
}
