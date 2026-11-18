using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimsSDL.Core;

namespace SimsSDL;

/// <summary>
/// Discovers structured content packs (pack.json) and classic Downloads subfolders, persists load order
/// and enable/disable state in Settings/mods.json, and evaluates soft file overrides vs hard conflicts.
/// </summary>
public static class ModManager
{
    public enum ModEntryKind
    {
        Pack,
        DownloadsSubfolder,
    }

    public sealed class ModSettingsFile
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("enabledPacks")]
        public List<string> EnabledPacks { get; set; } = [];

        [JsonPropertyName("disabledPacks")]
        public List<string> DisabledPacks { get; set; } = [];

        [JsonPropertyName("packOrder")]
        public List<string> PackOrder { get; set; } = [];

        [JsonPropertyName("disabledDownloads")]
        public List<string> DisabledDownloads { get; set; } = [];
    }

    public sealed record DiscoveredMod(
        string Key,
        string DisplayName,
        string Version,
        ModEntryKind Kind,
        string DirectoryPath,
        bool DefaultEnabled,
        int FileCount,
        IReadOnlyList<string> CategoryTags,
        ContentPack? LoadedPack,
        PackPluginReport? PluginReport,
        string? LoadError,
        string? Description = null,
        string? PreviewImagePath = null);

    public sealed record EvaluatedModStatus(
        DiscoveredMod Mod,
        bool Enabled,
        bool? PluginTrusted,
        IReadOnlyList<string> SoftOverlapNotes,
        IReadOnlyList<string> HardConflictErrors);

    public sealed record EvaluationReport(
        IReadOnlyList<EvaluatedModStatus> Entries,
        IReadOnlyList<string> GlobalHardErrors,
        bool CanApply);

    public static string SettingsPath(string dataRoot) =>
        Path.Combine(dataRoot, "Settings", "mods.json");

    public static string UserPacksDirectory(string dataRoot) =>
        Path.Combine(dataRoot, "Packs");

    public static ModSettingsFile LoadSettings(string dataRoot)
    {
        string path = SettingsPath(dataRoot);
        if (!File.Exists(path))
            return new ModSettingsFile();

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var parsed = JsonSerializer.Deserialize<ModSettingsFile>(bytes, PackContentPaths.CamelCaseJsonReadOptions);
            if (parsed is not null && parsed.SchemaVersion == 1)
            {
                parsed.EnabledPacks ??= [];
                parsed.DisabledPacks ??= [];
                parsed.PackOrder ??= [];
                parsed.DisabledDownloads ??= [];
                return parsed;
            }
        }
        catch
        {
        }

        return new ModSettingsFile();
    }

    public static void SaveSettings(string dataRoot, ModSettingsFile settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string path = SettingsPath(dataRoot);
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        settings.SchemaVersion = 1;
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(settings, jsonOptions);
        File.WriteAllBytes(path, bytes);
    }

    public static IReadOnlyList<DiscoveredMod> DiscoverAll(
        string gameRoot,
        string workspace,
        string dataRoot,
        string? cliPacks)
    {
        var results = new List<DiscoveredMod>();
        var seenDirs = new HashSet<string>(
            OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var seenPackIds = new HashSet<string>(StringComparer.Ordinal);

        var cliResolvedDirs = new HashSet<string>(
            OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(cliPacks) && Directory.Exists(cliPacks))
        {
            try
            {
                string canonicalCli = PackContentPaths.ResolveSymlinksInPath(cliPacks);
                if (File.Exists(Path.Combine(canonicalCli, "pack.json")))
                {
                    cliResolvedDirs.Add(canonicalCli);
                }
                else
                {
                    foreach (string sub in Directory.EnumerateDirectories(canonicalCli))
                    {
                        if (File.Exists(Path.Combine(sub, "pack.json")))
                            cliResolvedDirs.Add(PackContentPaths.ResolveSymlinksInPath(sub));
                    }
                }
            }
            catch
            {
            }
        }

        void ScanPackCandidateDir(string dir, bool defaultEnabled)
        {
            if (!Directory.Exists(dir))
                return;
            string canonical;
            try
            {
                canonical = PackContentPaths.ResolveSymlinksInPath(dir);
            }
            catch
            {
                return;
            }

            string manifestPath = Path.Combine(canonical, "pack.json");
            if (File.Exists(manifestPath))
            {
                if (!seenDirs.Add(canonical))
                    return;
                var inspected = InspectPackDirectory(canonical, defaultEnabled || cliResolvedDirs.Contains(canonical));
                if (inspected.LoadedPack is not null && IsEmptyTemplatePack(inspected.LoadedPack))
                    return;
                if (inspected.LoadedPack is null || seenPackIds.Add(inspected.LoadedPack.Manifest.Id))
                    results.Add(inspected);
                return;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(canonical)
                    .OrderBy(static p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return;
            }

            foreach (string child in children)
            {
                string childCanonical;
                try
                {
                    childCanonical = PackContentPaths.ResolveSymlinksInPath(child);
                }
                catch
                {
                    continue;
                }

                if (File.Exists(Path.Combine(childCanonical, "pack.json")) && seenDirs.Add(childCanonical))
                {
                    var inspected = InspectPackDirectory(childCanonical, defaultEnabled || cliResolvedDirs.Contains(childCanonical));
                    if (inspected.LoadedPack is not null && IsEmptyTemplatePack(inspected.LoadedPack))
                        continue;
                    if (inspected.LoadedPack is null || seenPackIds.Add(inspected.LoadedPack.Manifest.Id))
                        results.Add(inspected);
                }
            }
        }

        // 1. Explicit CLI packs (enabled by default for this session unless disabled in mods.json).
        if (!string.IsNullOrWhiteSpace(cliPacks))
            ScanPackCandidateDir(cliPacks, defaultEnabled: true);

        // 2. User DataRoot/Packs and Game/Packs (enabled by default unless disabled in mods.json).
        ScanPackCandidateDir(UserPacksDirectory(dataRoot), defaultEnabled: true);
        ScanPackCandidateDir(Path.Combine(gameRoot, "Packs"), defaultEnabled: true);

        // 3. Workspace/packs (discovered for easy 1-click toggling in Mods Menu; off by default unless in cliPacks or enabledPacks).
        if (!string.IsNullOrWhiteSpace(workspace))
            ScanPackCandidateDir(Path.Combine(workspace, "packs"), defaultEnabled: false);

        // 4. Classic <DataRoot>/Downloads/<Subfolder> and <Game>/Downloads/<Subfolder> directories,
        // automatically excluding official Maxis SimDownload packages (.far archives containing Maxis .ndx + .stx manifests).
        void ScanDownloadsRoot(string? downloadsRoot)
        {
            if (downloadsRoot is null || !Directory.Exists(downloadsRoot))
                return;

            string canonicalDlRoot;
            try
            {
                canonicalDlRoot = PackContentPaths.ResolveSymlinksInPath(downloadsRoot);
            }
            catch
            {
                return;
            }
            if (!seenDirs.Add(canonicalDlRoot))
                return;

            IEnumerable<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(canonicalDlRoot)
                    .OrderBy(static p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return;
            }

            foreach (string subdir in subdirs)
            {
                string folderName = Path.GetFileName(subdir);
                if (string.IsNullOrWhiteSpace(folderName) || folderName.StartsWith('.'))
                    continue;

                if (IsOfficialMaxisDownloadPackage(subdir))
                    continue;

                int count = CountCustomFilesSafe(subdir);
                if (count == 0)
                    continue;

                results.Add(new DiscoveredMod(
                    Key: "dl:" + folderName,
                    DisplayName: folderName,
                    Version: "",
                    Kind: ModEntryKind.DownloadsSubfolder,
                    DirectoryPath: subdir,
                    DefaultEnabled: true,
                    FileCount: count,
                    CategoryTags: ["Custom Content"],
                    LoadedPack: null,
                    PluginReport: null,
                    LoadError: null,
                    Description: null,
                    PreviewImagePath: ResolvePreviewImagePath(subdir, null)));
            }
        }

        if (!string.IsNullOrWhiteSpace(dataRoot))
            ScanDownloadsRoot(FindCaseInsensitiveSubdir(dataRoot, "Downloads"));
        ScanDownloadsRoot(FindCaseInsensitiveSubdir(gameRoot, "Downloads"));

        return results;
    }

    public static (List<DiscoveredMod> OrderedMods, HashSet<string> EnabledKeys) BuildInitialState(
        IReadOnlyList<DiscoveredMod> discovered,
        ModSettingsFile settings)
    {
        var enabledSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitEnabled = new HashSet<string>(settings.EnabledPacks, StringComparer.OrdinalIgnoreCase);
        var explicitDisabled = new HashSet<string>(settings.DisabledPacks, StringComparer.OrdinalIgnoreCase);
        var disabledDl = new HashSet<string>(settings.DisabledDownloads, StringComparer.OrdinalIgnoreCase);

        foreach (var mod in discovered)
        {
            if (mod.Kind == ModEntryKind.Pack)
            {
                string packId = mod.LoadedPack?.Manifest.Id ?? mod.Key;
                bool isOn = mod.DefaultEnabled
                    ? !explicitDisabled.Contains(packId) && !explicitDisabled.Contains(mod.Key)
                    : explicitEnabled.Contains(packId) || explicitEnabled.Contains(mod.Key);
                if (isOn && mod.LoadError is null)
                    enabledSet.Add(mod.Key);
            }
            else
            {
                if (!disabledDl.Contains(mod.DisplayName))
                    enabledSet.Add(mod.Key);
            }
        }

        // Order packs according to settings.PackOrder (index 0 = lowest priority / loaded first;
        // in UI we display highest priority at the top or show explicit load order #1 -> #N!).
        // Let's keep OrderedMods in actual load order (1..N for packs, followed by Downloads subfolders)
        // so Load Order #1 loads first and later packs override earlier ones cleanly.
        var orderLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < settings.PackOrder.Count; i++)
            orderLookup.TryAdd(settings.PackOrder[i], i);

        var packs = discovered
            .Where(static m => m.Kind == ModEntryKind.Pack)
            .OrderBy(m =>
            {
                string id = m.LoadedPack?.Manifest.Id ?? m.Key;
                return orderLookup.TryGetValue(id, out int idx) ? idx : int.MaxValue;
            })
            .ThenBy(static m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var downloads = discovered
            .Where(static m => m.Kind == ModEntryKind.DownloadsSubfolder)
            .OrderBy(static m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        packs.AddRange(downloads);
        return (packs, enabledSet);
    }

    public static ModSettingsFile BuildSettingsFromState(
        IReadOnlyList<DiscoveredMod> orderedMods,
        IReadOnlySet<string> enabledKeys)
    {
        var file = new ModSettingsFile { SchemaVersion = 1 };
        foreach (var mod in orderedMods)
        {
            bool isEnabled = enabledKeys.Contains(mod.Key);
            if (mod.Kind == ModEntryKind.Pack)
            {
                string id = mod.LoadedPack?.Manifest.Id ?? mod.Key;
                file.PackOrder.Add(id);
                if (isEnabled)
                    file.EnabledPacks.Add(id);
                else
                    file.DisabledPacks.Add(id);
            }
            else if (!isEnabled)
            {
                file.DisabledDownloads.Add(mod.DisplayName);
            }
        }
        return file;
    }

    public static EvaluationReport Evaluate(
        string gameRoot,
        string dataRoot,
        IReadOnlyList<DiscoveredMod> orderedMods,
        IReadOnlySet<string> enabledKeys)
    {
        PluginTrustStore? trustStore = null;
        try
        {
            trustStore = new PluginTrustStore(dataRoot);
        }
        catch
        {
        }

        var softNotes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var hardErrors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var globalErrors = new List<string>();

        void AddSoft(string key, string msg)
        {
            if (!softNotes.TryGetValue(key, out var list))
                softNotes[key] = list = [];
            if (!list.Contains(msg))
                list.Add(msg);
        }

        void AddHard(string key, string msg)
        {
            if (!hardErrors.TryGetValue(key, out var list))
                hardErrors[key] = list = [];
            if (!list.Contains(msg))
                list.Add(msg);
        }

        var enabledPackMods = orderedMods
            .Where(m => m.Kind == ModEntryKind.Pack && enabledKeys.Contains(m.Key))
            .ToList();

        // 1. Check broken manifests and duplicate Pack IDs among enabled packs.
        var byPackId = new Dictionary<string, DiscoveredMod>(StringComparer.Ordinal);
        var validEnabledPacks = new List<ContentPack>();
        var keyByPackId = new Dictionary<string, string>(StringComparer.Ordinal);
        var nameByPackId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var mod in enabledPackMods)
        {
            if (mod.LoadError is not null || mod.LoadedPack is null)
            {
                AddHard(mod.Key, mod.LoadError ?? "Invalid pack manifest");
                continue;
            }

            string id = mod.LoadedPack.Manifest.Id;
            if (byPackId.TryGetValue(id, out var existing))
            {
                string msg = $"Duplicate pack ID '{id}' with {existing.DisplayName}";
                AddHard(mod.Key, msg);
                AddHard(existing.Key, msg);
                continue;
            }

            byPackId[id] = mod;
            keyByPackId[id] = mod.Key;
            nameByPackId[id] = mod.DisplayName;
            validEnabledPacks.Add(mod.LoadedPack);
        }

        // 2. Check dependencies per enabled pack so missing dependencies highlight the exact pack.
        bool dependencyFailed = false;
        foreach (var pack in validEnabledPacks)
        {
            string modKey = keyByPackId[pack.Manifest.Id];
            foreach (var dep in pack.Manifest.Dependencies)
            {
                if (!byPackId.TryGetValue(dep.Id, out var found))
                {
                    AddHard(modKey, $"Requires missing/disabled pack '{dep.Id}' ({dep.Version})");
                    dependencyFailed = true;
                }
                else if (dep.Version != "*" && found.LoadedPack?.Manifest.Version != dep.Version)
                {
                    AddHard(modKey, $"Requires '{dep.Id}' version {dep.Version} (found {found.LoadedPack?.Manifest.Version})");
                    dependencyFailed = true;
                }
            }
        }

        // 3. Fast in-memory PackStack validation (<2 ms — catches Object GUID collisions, .iff basename collisions, skin path conflicts, and priority overrides without rescanning 3.5 GB base-game .far archives on UI clicks!).
        if (!dependencyFailed && hardErrors.Count == 0 && validEnabledPacks.Count > 0)
        {
            try
            {
                var stack = new PackStack(validEnabledPacks, preserveOrderAndAllowOverrides: true);

                // Record soft override badges between packs and over Base Game!
                foreach (var group in stack.OverriddenResources.GroupBy(static o => (o.WinnerPackId, o.LoserPackId)))
                {
                    int count = group.Count();
                    string winnerName = nameByPackId.GetValueOrDefault(group.Key.WinnerPackId, group.Key.WinnerPackId);
                    string loserName = nameByPackId.GetValueOrDefault(group.Key.LoserPackId, group.Key.LoserPackId);
                    if (keyByPackId.TryGetValue(group.Key.WinnerPackId, out string? winKey))
                        AddSoft(winKey, $"Overrides {count} file{(count == 1 ? "" : "s")} from {loserName}");
                    if (keyByPackId.TryGetValue(group.Key.LoserPackId, out string? loseKey))
                        AddSoft(loseKey, $"{count} file{(count == 1 ? "" : "s")} overridden by {winnerName}");
                }

                foreach (var pack in validEnabledPacks)
                {
                    if (pack.Manifest.Overrides.Count > 0 && keyByPackId.TryGetValue(pack.Manifest.Id, out string? modKey))
                    {
                        int count = pack.Manifest.Overrides.Count;
                        AddSoft(modKey, $"Declares {count} base game override{(count == 1 ? "" : "s")}");
                    }
                }
            }
            catch (Exception err)
            {
                bool attributed = false;
                foreach (var (packId, modKey) in keyByPackId)
                {
                    if (err.Message.Contains(packId, StringComparison.Ordinal))
                    {
                        AddHard(modKey, err.Message);
                        attributed = true;
                    }
                }
                if (!attributed)
                    globalErrors.Add(err.Message);
            }
        }

        var statuses = new List<EvaluatedModStatus>(orderedMods.Count);
        foreach (var mod in orderedMods)
        {
            bool enabled = enabledKeys.Contains(mod.Key);
            bool? trusted = null;
            if (mod.PluginReport is { } plugin)
                trusted = trustStore?.IsTrusted(plugin) ?? false;

            var modSoft = softNotes.TryGetValue(mod.Key, out var sList) ? sList : (IReadOnlyList<string>)[];
            var modHard = hardErrors.TryGetValue(mod.Key, out var hList) ? hList : (IReadOnlyList<string>)[];
            if (!enabled && mod.LoadError is not null)
                modHard = [mod.LoadError];

            statuses.Add(new EvaluatedModStatus(mod, enabled, trusted, modSoft, modHard));
        }

        bool anyActiveHardError = globalErrors.Count > 0
            || statuses.Any(static s => s.Enabled && s.HardConflictErrors.Count > 0);
        return new EvaluationReport(statuses, globalErrors, CanApply: !anyActiveHardError);
    }

    public static GameContent LoadConfiguredContent(
        string gameRoot,
        string workspace,
        string dataRoot,
        string? cliPacks)
    {
        string modsSettingsFile = SettingsPath(dataRoot);
        if (!File.Exists(modsSettingsFile))
        {
            // No saved mods.json yet: match exact CLI behavior while also picking up user DataRoot/Packs if present!
            string userPacks = UserPacksDirectory(dataRoot);
            if (!Directory.Exists(userPacks))
            {
                return PackTool.LoadContent(gameRoot, cliPacks);
            }
        }

        var settings = LoadSettings(dataRoot);
        var discovered = DiscoverAll(gameRoot, workspace, dataRoot, cliPacks);
        var (ordered, enabledKeys) = BuildInitialState(discovered, settings);

        string[] activePackDirs = ordered
            .Where(m => m.Kind == ModEntryKind.Pack && enabledKeys.Contains(m.Key) && m.LoadError is null)
            .Select(static m => m.DirectoryPath)
            .ToArray();

        string[] disabledDownloads = ordered
            .Where(m => m.Kind == ModEntryKind.DownloadsSubfolder && !enabledKeys.Contains(m.Key))
            .Select(static m => m.DisplayName)
            .ToArray();

        return new GameContent(
            gameRoot,
            activePackDirs,
            preserveOrderAndAllowOverrides: true,
            disabledDownloadFolders: disabledDownloads);
    }

    public static GameContent LoadStartupContentWithSafeMode(
        string gameRoot,
        string workspace,
        string dataRoot,
        string? cliPacks,
        out string? safeModeError)
    {
        safeModeError = null;
        try
        {
            return LoadConfiguredContent(gameRoot, workspace, dataRoot, cliPacks);
        }
        catch (Exception ex) when (File.Exists(SettingsPath(dataRoot)))
        {
            // Safe Mode fallback: never lock the player out on launch if a saved mod breaks or conflicts on disk.
            safeModeError = ex.Message;
            return new GameContent(gameRoot, []);
        }
    }

    public static void OpenUserModsDirectory(string dataRoot)
    {
        string dir = UserPacksDirectory(dataRoot);
        Directory.CreateDirectory(dir);
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", dir) { UseShellExecute = false });
            else if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open", dir) { UseShellExecute = false });
        }
        catch
        {
        }
    }

    private static DiscoveredMod InspectPackDirectory(string canonicalDir, bool defaultEnabled)
    {
        try
        {
            var pack = new ContentPack(canonicalDir);
            var tags = new List<string>();
            if (pack.PluginAssembly is not null)
                tags.Add("Gameplay");

            bool hasTown = false, hasUi = false, hasSkins = false, hasObjects = false;
            foreach (string key in pack.Assets.Keys)
            {
                string root = key.Split('/')[0];
                if (ContentPack.IsTownRoot(root) || string.Equals(root, "userdata", StringComparison.OrdinalIgnoreCase))
                    hasTown = true;
                else if (string.Equals(root, "uigraphics", StringComparison.OrdinalIgnoreCase))
                    hasUi = true;
                else if (key.Contains("/skins/", StringComparison.OrdinalIgnoreCase) || string.Equals(root, "skins", StringComparison.OrdinalIgnoreCase))
                    hasSkins = true;
                else if (key.EndsWith(".iff", StringComparison.OrdinalIgnoreCase))
                    hasObjects = true;
            }

            if (hasTown) tags.Add("Neighborhood");
            if (hasUi) tags.Add("UI");
            if (hasObjects) tags.Add("Objects");
            if (hasSkins) tags.Add("Skins");
            if (pack.Manifest.Panels is { Count: > 0 }) tags.Add("Panel");
            if (tags.Count == 0) tags.Add("Content Pack");

            PackPluginReport? pluginReport = null;
            if (pack.PluginAssembly is { } asmPath && pack.Manifest.Plugin is { } spec)
            {
                string pluginFolder = Path.GetDirectoryName(asmPath)!;
                string digest = PackPlugins.PluginDigest(pluginFolder);
                pluginReport = new PackPluginReport(
                    PackID: pack.Manifest.Id,
                    Version: pack.Manifest.Version,
                    Assembly: PackStack.MountedPluginFolder + "/" + pack.Manifest.Id + "/" + Path.GetFileName(asmPath),
                    Sha256: digest,
                    EntryType: spec.EntryType,
                    ApiVersion: spec.ApiVersion,
                    StateVersion: spec.StateVersion);
            }

            return new DiscoveredMod(
                Key: "pack:" + canonicalDir,
                DisplayName: pack.Manifest.Name,
                Version: pack.Manifest.Version,
                Kind: ModEntryKind.Pack,
                DirectoryPath: canonicalDir,
                DefaultEnabled: defaultEnabled,
                FileCount: pack.Assets.Count + (pack.PluginAssembly is not null ? 1 : 0),
                CategoryTags: tags,
                LoadedPack: pack,
                PluginReport: pluginReport,
                LoadError: null,
                Description: string.IsNullOrWhiteSpace(pack.Manifest.Description) ? null : pack.Manifest.Description.Trim(),
                PreviewImagePath: ResolvePreviewImagePath(canonicalDir, pack.Manifest.Preview));
        }
        catch (Exception ex)
        {
            string folderName = Path.GetFileName(canonicalDir);
            return new DiscoveredMod(
                Key: "pack:" + canonicalDir,
                DisplayName: folderName,
                Version: "Invalid",
                Kind: ModEntryKind.Pack,
                DirectoryPath: canonicalDir,
                DefaultEnabled: false,
                FileCount: 0,
                CategoryTags: ["Invalid Pack"],
                LoadedPack: null,
                PluginReport: null,
                LoadError: ex.Message,
                Description: null,
                PreviewImagePath: ResolvePreviewImagePath(canonicalDir, null));
        }
    }

    private static string? ResolvePreviewImagePath(string canonicalDir, string? manifestPreview)
    {
        if (!string.IsNullOrWhiteSpace(manifestPreview))
        {
            try
            {
                string candidate = PackContentPaths.ResolveSymlinksInPath(Path.Combine(canonicalDir, manifestPreview));
                if (PackContentPaths.IsUnderCanonicalRoot(candidate, canonicalDir)
                    && File.Exists(candidate)
                    && IsPreviewImageExtension(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        ReadOnlySpan<string> preferredNames =
        [
            "preview.png", "preview.bmp", "preview.tga",
            "thumbnail.png", "thumbnail.bmp", "thumbnail.tga",
            "cover.png", "cover.bmp", "icon.png"
        ];
        try
        {
            var topFiles = Directory.GetFiles(canonicalDir);
            foreach (string want in preferredNames)
            {
                string? hit = topFiles.FirstOrDefault(f => string.Equals(Path.GetFileName(f), want, StringComparison.OrdinalIgnoreCase));
                if (hit is not null)
                    return hit;
            }

            // Fallback: any .png/.bmp larger than a tiny 16x16 icon (~4 KB) inside the folder
            foreach (string file in Directory.EnumerateFiles(canonicalDir, "*", SearchOption.AllDirectories))
            {
                if (!IsPreviewImageExtension(file))
                    continue;
                if (new FileInfo(file).Length >= 4096)
                    return file;
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool IsEmptyTemplatePack(ContentPack pack)
    {
        if (pack.PluginAssembly is not null)
            return false;
        if (pack.Manifest.Panels is { Count: > 0 })
            return false;
        return pack.Assets.Keys.All(static key =>
            key.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPreviewImageExtension(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase);

    private static string? FindCaseInsensitiveSubdir(string parent, string name)
    {
        if (!Directory.Exists(parent))
            return null;
        try
        {
            return Directory.EnumerateDirectories(parent)
                .FirstOrDefault(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static int CountCustomFilesSafe(string folder)
    {
        int count = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".iff", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".far", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".skn", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".cmx", StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }
        }
        catch
        {
        }
        return count;
    }

    /// <summary>
    /// Detects built-in official Maxis SimDownload bonus packages (e.g. Complete Collection's 70 shipped
    /// Downloads/&lt;Object&gt;/&lt;Object&gt;.far sets) purely from file format metadata without hardcoded names:
    /// Official Maxis download packages contain only .far (+ optional Maxis .hit/.hot/.hsm/.hdb sound bank
    /// companions, zero loose .iff/.bmp/.skn/.cmx user files), and their FAR!byAZ directory table embeds
    /// Maxis's SimDownload .ndx + .stx package manifest entries.
    /// </summary>
    public static bool IsOfficialMaxisDownloadPackage(string folder)
    {
        try
        {
            var farFiles = new List<string>();
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith('.'))
                    continue;

                string ext = Path.GetExtension(name);
                if (ext.Equals(".far", StringComparison.OrdinalIgnoreCase))
                {
                    farFiles.Add(file);
                }
                else if (ext.Equals(".hit", StringComparison.OrdinalIgnoreCase)
                         || ext.Equals(".hot", StringComparison.OrdinalIgnoreCase)
                         || ext.Equals(".hsm", StringComparison.OrdinalIgnoreCase)
                         || ext.Equals(".hdb", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                else
                {
                    // Any loose .iff, .bmp, .skn, .cmx, .wll, .flr, .txt, etc. means user custom content!
                    return false;
                }
            }

            if (farFiles.Count == 0)
                return false;

            foreach (string farPath in farFiles)
            {
                if (!FarContainsMaxisSimDownloadManifest(farPath))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool FarContainsMaxisSimDownloadManifest(string farPath)
    {
        using var fs = new FileStream(farPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length < 20)
            return false;

        Span<byte> header = stackalloc byte[16];
        if (fs.Read(header) != 16)
            return false;

        if (!header[..8].SequenceEqual("FAR!byAZ"u8))
            return false;

        uint version = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
        uint dirOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
        if (version != 1 || dirOffset < 16 || dirOffset + 4 > fs.Length)
            return false;

        fs.Position = dirOffset;
        Span<byte> countBuf = stackalloc byte[4];
        if (fs.Read(countBuf) != 4)
            return false;

        uint entryCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(countBuf);
        if (entryCount == 0 || entryCount > 4096)
            return false;

        Span<byte> entryHdr = stackalloc byte[16];
        byte[] nameBuf = new byte[512];

        for (uint i = 0; i < entryCount; i++)
        {
            if (fs.Read(entryHdr) != 16)
                return false;

            uint nameLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entryHdr[12..16]);
            if (nameLen == 0 || nameLen > 2048 || fs.Position + nameLen > fs.Length)
                return false;

            if (nameLen > nameBuf.Length)
                nameBuf = new byte[nameLen];

            if (fs.Read(nameBuf, 0, (int)nameLen) != (int)nameLen)
                return false;

            string entryName = System.Text.Encoding.Latin1.GetString(nameBuf, 0, (int)nameLen);
            if (entryName.EndsWith(".ndx", StringComparison.OrdinalIgnoreCase)
                || entryName.EndsWith(".stx", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
