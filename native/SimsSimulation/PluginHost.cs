using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using SimsNative.Plugins;

/// <summary>
/// Loads code-bearing gameplay packs described by the content mount's <c>.pack-stack.json</c>.
/// A plugin runs only when the data root's trust store names its pack ID, version and assembly SHA-256,
/// and only when the mounted copy still has that hash. Each plugin gets its own collectible load context
/// that shares the contract assembly with the host. A plugin that throws is disabled for the session.
/// </summary>
internal sealed class PluginHost : IDisposable
{
    internal sealed record PluginDescriptor(string PackId, string Version, string Assembly, string Sha256, string EntryType, int ApiVersion, int StateVersion);
    internal sealed record PanelDescriptor(string PackId, string Id, string Path, string Sha256 = "");
    internal sealed record ObjectMigration(uint From, uint To);
    internal sealed record PackDescriptor(string Id, string Version, string[] MigratesFrom, ObjectMigration[]? Objects = null);
    internal sealed record PluginSaveRecord(string PackVersion, int StateVersion, string? Data, Dictionary<string, string> Values, Dictionary<string, string>? NeighborhoodValues = null);
    internal sealed record PluginStatus(string PackId, string Version, string State, string? Reason);
    private sealed record TrustEntry(string PackId, string Version, string Sha256);
    private sealed record TrustFile(int SchemaVersion, TrustEntry[] Plugins);
    internal sealed record EnvironmentState(string PackId, string AssetRoot, string Scope, PluginEnvironment Effect);
    private sealed record NeighborhoodRecord(string PackVersion, int StateVersion, Dictionary<string, string> Values);

    public interface IPluginWorld
    {
        PluginEnvironmentClock Clock(int house, int mapMode);
        short GetObjectAttribute(string packId, short objectId, int index);
        void SetObjectAttribute(string packId, short objectId, int index, short value);
        void SetObjectGraphic(string packId, short objectId, short graphic);
    }

    private sealed class LoadedPlugin(PluginDescriptor descriptor, ISimsPlugin instance, PluginContext context, PluginLoadContext loadContext)
    {
        public PluginDescriptor Descriptor { get; } = descriptor;
        public ISimsPlugin Instance { get; } = instance;
        public PluginContext Context { get; } = context;
        public PluginLoadContext LoadContext { get; } = loadContext;
        public bool Failed { get; set; }
    }

    private const int MaxValues = 256;
    private const int MaxKeyLength = 64;
    private const int MaxValueLength = 4096;
    private const int MaxStateBytes = 1 << 20;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly List<LoadedPlugin> loaded = [];
    private readonly List<PluginStatus> statuses = [];
    private readonly Action<object> write;
    private readonly Action<string, short, string, bool>? playSoundResource;
    private readonly Action<short, string>? stopSoundResource;
    private readonly Func<short> currentHouse;
    private readonly Func<PluginHost, IPluginSimulation> simulation;
    private IPluginWorld? world;
    private readonly Dictionary<(string Pack, string Panel), Dictionary<string, string>> panelValues = [];
    private readonly HashSet<(string Pack, string Panel)> declaredPanels = [];
    private bool panelValuesChanged;
    private string mountRoot = "";
    private string? neighborhoodRoot;
    private bool environmentOnly;
    private readonly Dictionary<string, string> resourceOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NeighborhoodRecord> neighborhoodRecords = [];
    private LoadedPlugin? environmentOwner;
    private readonly Dictionary<string, Dictionary<string, string>> sealedResources = [];
    private readonly Dictionary<string, string> approvedResources = [];
    private static readonly HashSet<string> ShaderSlots = ["ambient", "ambientBlur", "atmosphere", "season", "trail", "groundDepth", "heightScatter", "depthOnly", "fullscreen"];
    private LoadedPlugin[] scorers = [];
    private LoadedPlugin[] interactionFilters = [];
    private LoadedPlugin[] menuPlugins = [];
    private string? choosingPack;
    private string? shownPanel;
    private short? shownPanelAnchor;
    private const int MaxMenuEntries = 8;

    public static string TrustStorePath(string dataRoot) => Path.Combine(dataRoot, "Settings", "plugin-trust.json");

    public static PluginHost Create(string mount, string dataRoot, Action<object> write, Func<short> currentHouse,
        Func<PluginHost, IPluginSimulation>? simulation = null, string? userData = null, bool environmentOnly = false,
        IPluginWorld? world = null, Action<string, short, string, bool>? playSoundResource = null,
        Action<short, string>? stopSoundResource = null)
    {
        var metadataPath = Path.Combine(mount, ".pack-stack.json");
        PluginDescriptor[] plugins = [];
        PanelDescriptor[] panels = [];
        PackDescriptor[] packs = [];
        if (File.Exists(metadataPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var root = document.RootElement;
            if (root.TryGetProperty("plugins", out var pluginElement))
                plugins = pluginElement.Deserialize<PluginDescriptor[]>(Json) ?? [];
            if (root.TryGetProperty("panels", out var panelElement))
                panels = panelElement.Deserialize<PanelDescriptor[]>(Json) ?? [];
            if (root.TryGetProperty("packDetails", out var packElement))
                packs = packElement.Deserialize<PackDescriptor[]>(Json) ?? [];
            else if (root.TryGetProperty("packs", out var legacyPacks))
                packs = legacyPacks.EnumerateArray().Select(value => value.GetString() ?? "").Select(ParsePackLabel).ToArray();
        }
        var host = new PluginHost(write, currentHouse, playSoundResource, stopSoundResource, simulation, packs, panels);
        host.mountRoot = Path.GetFullPath(mount);
        host.neighborhoodRoot = userData;
        host.environmentOnly = environmentOnly;
        host.world = world;
        if (File.Exists(metadataPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            if (document.RootElement.TryGetProperty("resources", out var resources))
                foreach (var resource in resources.EnumerateArray())
                    host.resourceOwners[resource.GetProperty("path").GetString()!] = resource.GetProperty("owner").GetString()!;
        }
        var trusted = ReadTrust(TrustStorePath(dataRoot));
        foreach (var plugin in plugins) host.TryLoad(mount, plugin, trusted);
        return host;
    }

    private PluginHost(Action<object> write, Func<short> currentHouse,
        Action<string, short, string, bool>? playSoundResource, Action<short, string>? stopSoundResource,
        Func<PluginHost, IPluginSimulation>? simulation, PackDescriptor[] packs, PanelDescriptor[] panels)
    {
        this.write = write;
        this.currentHouse = currentHouse;
        this.playSoundResource = playSoundResource;
        this.stopSoundResource = stopSoundResource;
        this.simulation = simulation ?? (_ => throw new InvalidOperationException("No plugin simulation is available"));
        MigratedPacks = packs;
        Panels = panels;
        foreach (var panel in panels) declaredPanels.Add((panel.PackId, panel.Id));
    }

    private static PackDescriptor ParsePackLabel(string label)
    {
        var separator = label.LastIndexOf('@');
        return separator <= 0 ? new PackDescriptor(label, "", []) : new PackDescriptor(label[..separator], label[(separator + 1)..], []);
    }

    private static HashSet<(string, string, string)> ReadTrust(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var file = JsonSerializer.Deserialize<TrustFile>(File.ReadAllText(path), Json);
            if (file == null || file.SchemaVersion != 1) return [];
            return file.Plugins.Select(entry => (entry.PackId, entry.Version, entry.Sha256.ToLowerInvariant())).ToHashSet();
        }
        catch (JsonException) { return []; }
    }

    /// <summary>Matches Swift's <c>pluginDigest</c>: SHA-256 over "name:sha256\n" lines for each .dll/.json file, by lowercase name.</summary>
    internal static string Digest(string folder) => PluginFolderDigest(folder);

    private static string PluginFolderDigest(string folder)
    {
        var files = new DirectoryInfo(folder).GetFiles()
            .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0 &&
                (file.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                 file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(file => file.Name.ToLowerInvariant(), StringComparer.Ordinal);
        var lines = new System.Text.StringBuilder();
        foreach (var file in files)
        {
            var fileHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.FullName))).ToLowerInvariant();
            lines.Append(file.Name.ToLowerInvariant()).Append(':').Append(fileHash).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lines.ToString()))).ToLowerInvariant();
    }

    private static Exception Unwrap(Exception error) => error is TargetInvocationException { InnerException: { } inner } ? inner : error;

    private void TryLoad(string mount, PluginDescriptor descriptor, HashSet<(string, string, string)> trusted)
    {
        void Block(string state, string reason)
        {
            statuses.Add(new PluginStatus(descriptor.PackId, descriptor.Version, state, reason));
            write(new { type = "plugin", packId = descriptor.PackId, version = descriptor.Version, state, reason });
        }

        if (!PluginApi.SupportedVersions.Contains(descriptor.ApiVersion))
        {
            Block("rejected", $"Plugin API version {descriptor.ApiVersion} is not supported");
            return;
        }
        if (descriptor.StateVersion < 1 ||
            string.IsNullOrWhiteSpace(descriptor.PackId) || string.IsNullOrWhiteSpace(descriptor.Version) ||
            string.IsNullOrWhiteSpace(descriptor.Assembly) || string.IsNullOrWhiteSpace(descriptor.Sha256) ||
            string.IsNullOrWhiteSpace(descriptor.EntryType))
        {
            Block("rejected", "Plugin state version must be at least 1");
            return;
        }

        var pluginRoot = Path.GetFullPath(Path.Combine(mount, ".plugins")) + Path.DirectorySeparatorChar;
        var assemblyPath = Path.GetFullPath(Path.Combine(mount, descriptor.Assembly));
        if (!assemblyPath.StartsWith(pluginRoot, StringComparison.Ordinal) || !File.Exists(assemblyPath))
        {
            Block("rejected", "Plugin assembly is missing from the content mount");
            return;
        }

        var folder = Path.GetDirectoryName(assemblyPath)!;
        var actualHash = PluginFolderDigest(folder);
        var assemblyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))).ToLowerInvariant();
        if (!string.Equals(actualHash, descriptor.Sha256, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(assemblyHash, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Block("rejected", "Plugin files changed after the content mount was validated");
            return;
        }

        if (!trusted.Contains((descriptor.PackId, descriptor.Version, descriptor.Sha256.ToLowerInvariant())))
        {
            Block("untrusted", "This plugin has not been trusted on this Mac");
            return;
        }

        PluginLoadContext? loadContext = null;
        try
        {
            loadContext = new PluginLoadContext(assemblyPath);
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType(descriptor.EntryType, throwOnError: false)
                ?? throw new InvalidDataException($"Entry type {descriptor.EntryType} was not found");
            if (!typeof(ISimsPlugin).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null)
                throw new InvalidDataException($"Entry type {descriptor.EntryType} must be a concrete ISimsPlugin with a public parameterless constructor");
            if (environmentOnly && !typeof(IPluginEnvironmentProvider).IsAssignableFrom(type))
            {
                loadContext.Unload();
                return;
            }
            var instance = (ISimsPlugin)Activator.CreateInstance(type)!;
            var context = new PluginContext(this, descriptor);
            var loadedPlugin = new LoadedPlugin(descriptor, instance, context, loadContext);
            instance.Initialize(context);
            loaded.Add(loadedPlugin);
            statuses.Add(new PluginStatus(descriptor.PackId, descriptor.Version, "loaded", null));
            write(new { type = "plugin", packId = descriptor.PackId, version = descriptor.Version, state = "loaded" });
            RefreshHooks();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            loadContext?.Unload();
            Block("failed", Unwrap(error).Message);
        }
    }

    public Func<PluginAutonomyChoice, float, float>? AutonomyScore =>
        scorers.Any(plugin => !plugin.Failed) ? ScoreAutonomy : null;

    public float ScoreAutonomy(PluginAutonomyChoice choice, float score)
    {
        foreach (var plugin in scorers)
            Guard(plugin, "ScoreAutonomy", () =>
            {
                var next = plugin.Instance.ScoreAutonomy(choice, score);
                if (!float.IsFinite(next)) throw new InvalidDataException("ScoreAutonomy returned a score that is not a finite number");
                score = Math.Clamp(next, 0f, 1e6f);
            });
        return score;
    }

    private void RefreshHooks()
    {
        scorers = loaded.Where(plugin => !plugin.Failed && plugin.Descriptor.ApiVersion >= 2 && Implements(plugin.Instance, nameof(ISimsPlugin.ScoreAutonomy))).ToArray();
        interactionFilters = loaded.Where(plugin => !plugin.Failed && plugin.Descriptor.ApiVersion >= 2 && Implements(plugin.Instance, nameof(ISimsPlugin.AllowInteraction))).ToArray();
        menuPlugins = loaded.Where(plugin => !plugin.Failed && plugin.Descriptor.ApiVersion >= 2 && Implements(plugin.Instance, nameof(ISimsPlugin.GetMenuEntries))).ToArray();
    }

    private static bool Implements(ISimsPlugin instance, string member)
    {
        var map = instance.GetType().GetInterfaceMap(typeof(ISimsPlugin));
        var index = Array.FindIndex(map.InterfaceMethods, method => method.Name == member);
        return index >= 0 && map.TargetMethods[index].DeclaringType != typeof(ISimsPlugin);
    }

    private void Guard(LoadedPlugin plugin, string operation, Action action)
    {
        if (plugin.Failed) return;
        try { action(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            plugin.Failed = true;
            var reason = $"{operation}: {Unwrap(error).Message}";
            var index = statuses.FindIndex(status => status.PackId == plugin.Descriptor.PackId);
            var status = new PluginStatus(plugin.Descriptor.PackId, plugin.Descriptor.Version, "failed", reason);
            if (index >= 0) statuses[index] = status; else statuses.Add(status);
            write(new
            {
                type = "plugin",
                packId = plugin.Descriptor.PackId,
                version = plugin.Descriptor.Version,
                state = "failed",
                reason
            });
            RefreshHooks();
        }
    }

    public IReadOnlyList<PluginStatus> Statuses => statuses;
    public IReadOnlyList<PanelDescriptor> Panels { get; }
    public IReadOnlyList<PackDescriptor> MigratedPacks { get; }

    public void Tick(long update)
    {
        foreach (var plugin in loaded) Guard(plugin, "OnTick", () => plugin.Instance.OnTick(update));
        FlushPanels();
    }

    public void FlushPanels()
    {
        if (!panelValuesChanged) return;
        panelValuesChanged = false;
        write(new { type = "pluginPanels", panels = PanelState() });
    }

    public object[] PanelState() => panelValues
        .OrderBy(pair => pair.Key.Pack, StringComparer.Ordinal).ThenBy(pair => pair.Key.Panel, StringComparer.Ordinal)
        .Select(pair => (object)new { packId = pair.Key.Pack, panelId = pair.Key.Panel, values = pair.Value })
        .ToArray();

    public bool AllowInteraction(short simId, PluginObject target, PluginInteraction interaction)
    {
        var allowed = true;
        foreach (var plugin in interactionFilters)
            Guard(plugin, "AllowInteraction", () =>
            {
                if (!plugin.Instance.AllowInteraction(simId, target, interaction)) allowed = false;
            });
        return allowed;
    }

    public sealed record MenuEntry(string PackId, string Entry, string Name);

    public IReadOnlyList<MenuEntry> MenuEntries(short simId, PluginObject target)
    {
        var entries = new List<MenuEntry>();
        foreach (var plugin in menuPlugins)
            Guard(plugin, "GetMenuEntries", () =>
            {
                var offered = (plugin.Instance.GetMenuEntries(simId, target) ?? []).Take(MaxMenuEntries + 1).ToArray();
                if (offered.Length > MaxMenuEntries) throw new InvalidDataException($"GetMenuEntries returned more than {MaxMenuEntries} entries");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in offered)
                {
                    if (entry == null || !ValidMenuId(entry.Id) || !ids.Add(entry.Id)) throw new InvalidDataException("GetMenuEntries returned a missing, malformed or repeated entry ID");
                    if (!ValidMenuName(entry.Name)) throw new InvalidDataException($"Menu entry {entry.Id} has a malformed name");
                }
                entries.AddRange(offered.Select(entry => new MenuEntry(plugin.Descriptor.PackId, entry.Id, entry.Name)));
            });
        return entries;
    }

    public object ChooseMenuEntry(short simId, PluginObject target, string packId, string entryId)
    {
        var plugin = menuPlugins.FirstOrDefault(candidate => candidate.Descriptor.PackId == packId && !candidate.Failed)
            ?? throw new InvalidOperationException("The menu entry's plugin is not running");
        if (!MenuEntries(simId, target).Any(entry => entry.PackId == packId && entry.Entry == entryId))
            throw new ArgumentException("The plugin no longer offers that menu entry");
        (choosingPack, shownPanel, shownPanelAnchor) = (packId, null, null);
        try { Guard(plugin, "OnMenuEntry", () => plugin.Instance.OnMenuEntry(simId, target, entryId)); }
        finally { choosingPack = null; }
        FlushPanels();
        var panel = plugin.Failed ? null : shownPanel;
        var anchorId = panel == null ? null : shownPanelAnchor;
        if (panel != null) write(new { type = "pluginPanelShow", packId, panelId = panel, anchorId });
        return new { accepted = !plugin.Failed, panel, anchorId };
    }

    private static bool ValidMenuId(string? id) =>
        id is { Length: > 0 and <= MaxKeyLength } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private static bool ValidMenuName(string? name) =>
        name is { Length: > 0 and <= MaxKeyLength } && !name.Any(char.IsControl) && name.Split('/').All(part => part.Length > 0);

    public object PanelAction(string packId, string panelId, string action)
    {
        if (!declaredPanels.Contains((packId, panelId))) throw new ArgumentException("Unknown plugin panel");
        if (string.IsNullOrEmpty(action) || action.Length > MaxKeyLength) throw new ArgumentException("Invalid plugin panel action");
        var plugin = loaded.FirstOrDefault(candidate => candidate.Descriptor.PackId == packId && !candidate.Failed)
            ?? throw new InvalidOperationException("The panel's plugin is not running");
        Guard(plugin, "OnPanelAction", () => plugin.Instance.OnPanelAction(panelId, action));
        FlushPanels();
        return new { accepted = !plugin.Failed };
    }

    public void PlayAnimationSound(string packId, string resource, short ownerObjectId, string cue, bool loop, bool stop)
    {
        if (stop)
        {
            stopSoundResource?.Invoke(ownerObjectId, packId + ":" + cue);
        }
        else
        {
            var descriptor = loaded.FirstOrDefault(p => p.Descriptor.PackId == packId)?.Descriptor;
            if (descriptor is not null)
            {
                var path = ResourcePath(descriptor, resource);
                playSoundResource?.Invoke(path, ownerObjectId, packId + ":" + cue, loop);
            }
        }
    }

    public EnvironmentState? Environment(int house, int mapMode = 0)
    {
        var clock = house > 0 && world is not null ? world.Clock(house, mapMode) : new PluginEnvironmentClock(house, mapMode, 0, 0, 720, []);
        EnvironmentState? result = null;
        environmentOwner = null;
        foreach (var plugin in loaded.Where(plugin => plugin.Descriptor.ApiVersion >= 3 && plugin.Instance is IPluginEnvironmentProvider))
        {
            Guard(plugin, "GetEnvironment", () =>
            {
                var effect = ((IPluginEnvironmentProvider)plugin.Instance).GetEnvironment(clock);
                if (effect == null) return;
                if (result != null) throw new InvalidDataException($"Environment conflicts with pack {result.PackId}");
                ValidateEnvironment(plugin.Descriptor, effect);
                result = new EnvironmentState(plugin.Descriptor.PackId, mountRoot, $"{Neighborhoods.Current}:{house}:{mapMode}", effect);
                environmentOwner = plugin;
            });
        }
        return result;
    }

    private void ValidateEnvironment(PluginDescriptor descriptor, PluginEnvironment effect)
    {
        static bool In(float value, float high) => float.IsFinite(value) && value >= 0 && value <= high;
        if (effect.SurfaceMode is < 0 or > 32 || !In(effect.SurfaceAmount, 4) || !In(effect.CloudCover, 1) ||
            !In(effect.Precipitation, 1) || !In(effect.Wetness, 1) || !In(effect.AudioGain, 1) || !In(effect.AudioCutoff, 24000) || !In(effect.TrailRecovery, 1) ||
            effect.OutdoorSuit is < 0 or > 255 || effect.Footprints.Length > 64 || effect.SnowLotObjects.Length > 64 || effect.Shaders.Count > ShaderSlots.Count)
            throw new InvalidDataException("Invalid plugin environment values");
        if (effect.Parameters.Count > 64 || effect.Parameters.Any(pair => pair.Key.Length > 64 || !float.IsFinite(pair.Value) || Math.Abs(pair.Value) > 10000))
            throw new InvalidDataException("Invalid plugin effect parameters");
        foreach (var mark in effect.Footprints)
            if (!float.IsFinite(mark.X) || !float.IsFinite(mark.Y) || !float.IsFinite(mark.Heading) || !In(Math.Abs(mark.Foot), 4))
                throw new InvalidDataException("Invalid plugin trail mark");
        foreach (var (slot, resource) in effect.Shaders)
        {
            if (!ShaderSlots.Contains(slot)) throw new InvalidDataException("Unsupported shader slot");
            ResourcePath(descriptor, resource + ".msl");
            ResourcePath(descriptor, resource + ".spv");
        }
        if (effect.Indicator is { } indicator)
        {
            if (indicator.Count is < 1 or > 16 || indicator.Index < 0 || indicator.Index >= indicator.Count ||
                indicator.WeatherTooltip.Length > 256 || indicator.CycleTooltip.Length > 256)
                throw new InvalidDataException("Invalid plugin cycle indicator");
            ResourcePath(descriptor, indicator.Strip);
            ResourcePath(descriptor, indicator.WeatherIcon);
            ResourcePath(descriptor, indicator.Window);
            ResourcePath(descriptor, indicator.Overlay);
        }
    }

    public PluginImage TransformNeighborhoodImage(PluginNeighborhoodImage image, EnvironmentState? environment)
    {
        if (environment == null || environmentOwner == null) return image.Image;
        var result = image.Image;
        Guard(environmentOwner, "TransformNeighborhoodImage", () =>
        {
            result = ((IPluginEnvironmentProvider)environmentOwner.Instance).TransformNeighborhoodImage(image, environment.Effect);
            if (result.Width != image.Image.Width || result.Height != image.Image.Height || result.Rgba.Length != checked(result.Width * result.Height * 4))
                throw new InvalidDataException("Plugin image dimensions changed");
        });
        return environmentOwner.Failed ? image.Image : result;
    }

    public void ExportEnvironment(string directory, int house)
    {
        var environment = Environment(house);
        var output = Path.Combine(directory, "environment.json");
        if (environment == null) { File.Delete(output); return; }
        var descriptor = loaded.Single(plugin => plugin.Descriptor.PackId == environment.PackId).Descriptor;
        var assets = Path.Combine(directory, "effect-resources");
        var resources = environment.Effect.Shaders.Values.SelectMany(resource => new[] { resource + ".msl", resource + ".spv" });
        if (environment.Effect.Indicator is { } indicator)
            resources = resources.Concat([indicator.Strip, indicator.WeatherIcon, indicator.Window, indicator.Overlay]);
        foreach (var resource in resources)
        {
            var source = ResourcePath(descriptor, resource);
            var target = Path.Combine(assets, resource);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) File.Copy(source, target);
        }
        File.WriteAllText(output, JsonSerializer.Serialize(environment with { AssetRoot = assets }, Json));
    }

    public void CommitNeighborhood()
    {
        foreach (var plugin in loaded.Where(plugin => plugin.Descriptor.ApiVersion >= 3))
        {
            if (plugin.Failed) throw new InvalidOperationException($"Plugin {plugin.Descriptor.PackId} failed; neighborhood state cannot be saved");
            var path = NeighborhoodPath(plugin.Descriptor.PackId);
            if (path == null) continue;
            ValidateValues(plugin.Context.NeighborhoodStore);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var record = new NeighborhoodRecord(plugin.Descriptor.Version, plugin.Descriptor.StateVersion, new(plugin.Context.NeighborhoodStore));
            var json = JsonSerializer.Serialize(record, Json);
            if (File.Exists(path) && File.ReadAllText(path) == json) continue;
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, true);
        }
    }

    private static void ValidateValues(IReadOnlyDictionary<string, string> values)
    {
        if (values.Count > MaxValues || values.Any(pair => pair.Key.Length is 0 or > MaxKeyLength || pair.Value == null || pair.Value.Length > MaxValueLength))
            throw new InvalidDataException("Neighborhood plugin values exceed the namespace limits");
    }

    private string? NeighborhoodPath(string packId) => neighborhoodRoot == null ? null : Path.Combine(neighborhoodRoot, "PluginState", packId + ".json");

    private BoundedValues LoadNeighborhood(PluginDescriptor descriptor)
    {
        var values = new BoundedValues();
        if (descriptor.ApiVersion < 3) return values;
        var path = NeighborhoodPath(descriptor.PackId);
        if (path == null || !File.Exists(path)) return values;
        var record = JsonSerializer.Deserialize<NeighborhoodRecord>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Invalid neighborhood plugin state");
        if (record.StateVersion > descriptor.StateVersion || record.StateVersion < 1)
            throw new InvalidDataException($"Neighborhood state for {descriptor.PackId} needs a different state version");
        ValidateValues(record.Values);
        foreach (var pair in record.Values) values[pair.Key] = pair.Value;
        neighborhoodRecords[descriptor.PackId] = record;
        return values;
    }

    private int NeighborhoodVersion(PluginDescriptor descriptor) => descriptor.ApiVersion < 3
        ? throw new NotSupportedException("Neighborhood state needs API 3")
        : neighborhoodRecords.GetValueOrDefault(descriptor.PackId)?.StateVersion ?? descriptor.StateVersion;

    private static string PlainFilename(string filename)
    {
        if (filename.Length is 0 or > 128 || filename != Path.GetFileName(filename) || filename.Contains('\\') || filename is "." or "..")
            throw new ArgumentException("Expected a plain filename");
        return filename;
    }

    private byte[]? ReadLegacy(PluginDescriptor descriptor, string filename)
    {
        if (descriptor.ApiVersion < 3) throw new NotSupportedException("Legacy import needs API 3");
        var path = neighborhoodRoot == null ? null : Path.Combine(neighborhoodRoot, PlainFilename(filename));
        if (path == null || !File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaxStateBytes) throw new InvalidDataException("Legacy plugin state is too large");
        return File.ReadAllBytes(path);
    }

    private byte[] ReadPluginData(PluginDescriptor descriptor, string filename)
    {
        if (descriptor.ApiVersion < 3) throw new NotSupportedException("Plugin data needs API 3");
        var path = Path.Combine(Path.GetDirectoryName(Path.Combine(mountRoot, descriptor.Assembly))!, PlainFilename(filename));
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length > MaxStateBytes)
            throw new InvalidDataException("Plugin data must be a bounded JSON file");
        return File.ReadAllBytes(path);
    }

    private string ResourcePath(PluginDescriptor descriptor, string resource)
    {
        var normalized = resource.Replace('\\', '/').ToLowerInvariant();
        if (descriptor.ApiVersion < 3 || !resourceOwners.TryGetValue(normalized, out var owner) || owner != descriptor.PackId)
            throw new InvalidDataException($"Resource {resource} is not owned by {descriptor.PackId}");
        var cacheKey = descriptor.PackId + "/" + normalized;
        if (approvedResources.TryGetValue(cacheKey, out var approved)) return approved;
        if (!sealedResources.TryGetValue(descriptor.PackId, out var seal))
        {
            seal = JsonSerializer.Deserialize<Dictionary<string, string>>(ReadPluginData(descriptor, "resources.json"))
                ?? throw new InvalidDataException("Missing signed resource list");
            sealedResources[descriptor.PackId] = seal;
        }
        var path = mountRoot;
        foreach (var component in normalized.Split('/'))
        {
            if (component is "" or "." or "..") throw new InvalidDataException("Invalid resource path");
            path = Directory.EnumerateFileSystemEntries(path).FirstOrDefault(entry => Path.GetFileName(entry).Equals(component, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Plugin resource is missing");
        }
        path = Path.GetFullPath(path);
        if (!path.StartsWith(mountRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            throw new InvalidDataException("Plugin resource escapes the mount or is missing");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        if (!seal.TryGetValue(normalized, out var expected) || hash != expected)
            throw new InvalidDataException("Plugin resource differs from its trusted digest");
        approvedResources[cacheKey] = path;
        return path;
    }

    public Dictionary<string, PluginSaveRecord> Save()
    {
        var records = new Dictionary<string, PluginSaveRecord>(StringComparer.Ordinal);
        foreach (var plugin in loaded)
        {
            byte[]? data = null;
            Guard(plugin, "SaveState", () => data = plugin.Instance.SaveState());
            if (plugin.Failed) throw new InvalidOperationException($"Plugin {plugin.Descriptor.PackId} failed and its state cannot be saved");
            if (data is { Length: > MaxStateBytes }) throw new InvalidOperationException($"Plugin {plugin.Descriptor.PackId} returned more than 1 MiB of state");
            records[plugin.Descriptor.PackId] = new PluginSaveRecord(plugin.Descriptor.Version, plugin.Descriptor.StateVersion,
                data == null ? null : Convert.ToBase64String(data), new Dictionary<string, string>(plugin.Context.ValueStore, StringComparer.Ordinal),
                plugin.Descriptor.ApiVersion >= 3 ? new Dictionary<string, string>(plugin.Context.NeighborhoodStore) : null);
        }
        return records;
    }

    public void ValidateSaved(IReadOnlyDictionary<string, PluginSaveRecord>? records)
    {
        foreach (var (packId, record) in records ?? new Dictionary<string, PluginSaveRecord>())
        {
            var plugin = loaded.FirstOrDefault(candidate => candidate.Descriptor.PackId == packId);
            if (plugin == null)
            {
                var status = statuses.FirstOrDefault(candidate => candidate.PackId == packId);
                throw new InvalidDataException(status == null
                    ? $"This save contains state for the missing plugin {packId}"
                    : $"This save requires plugin {packId}, which is {status.State}: {status.Reason}");
            }
            if (plugin.Failed) throw new InvalidDataException($"This save requires plugin {packId}, which failed in this session");
            if (record.StateVersion > plugin.Descriptor.StateVersion)
                throw new InvalidDataException($"This save was written by a newer state version ({record.StateVersion}) of plugin {packId}; restore a backup or update the pack");
            if (record.StateVersion < 1) throw new InvalidDataException($"Invalid saved state version for plugin {packId}");
            if (record.Values.Count > MaxValues || record.Values.Any(pair => pair.Key.Length is 0 or > MaxKeyLength || pair.Value.Length > MaxValueLength))
                throw new InvalidDataException($"Saved values for plugin {packId} exceed the namespace limits");
            if (record.NeighborhoodValues != null) ValidateValues(record.NeighborhoodValues);
            if (record.Data != null && (record.Data.Length > MaxStateBytes * 4 / 3 + 4 || !IsBase64(record.Data)))
                throw new InvalidDataException($"Saved state for plugin {packId} is not valid");
        }
    }

    private static bool IsBase64(string value)
    {
        var buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out _);
    }

    public void Restore(IReadOnlyDictionary<string, PluginSaveRecord>? records)
    {
        foreach (var plugin in loaded)
        {
            plugin.Context.ValueStore.Clear();
            if (records == null || !records.TryGetValue(plugin.Descriptor.PackId, out var record)) continue;
            if (record.NeighborhoodValues != null)
            {
                plugin.Context.NeighborhoodStore.Clear();
                foreach (var (key, value) in record.NeighborhoodValues) plugin.Context.NeighborhoodStore[key] = value;
            }
            foreach (var (key, value) in record.Values) plugin.Context.ValueStore[key] = value;
            var data = record.Data == null ? [] : Convert.FromBase64String(record.Data);
            Guard(plugin, "LoadState", () => plugin.Instance.LoadState(record.StateVersion, data));
        }
        FlushPanels();
    }

    public void Dispose()
    {
        foreach (var plugin in loaded) plugin.LoadContext.Unload();
        loaded.Clear();
    }

    private sealed class PluginLoadContext(string assemblyPath) : AssemblyLoadContext("plugin:" + Path.GetFileName(assemblyPath), isCollectible: true)
    {
        private readonly AssemblyDependencyResolver resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(ISimsPlugin).Assembly.GetName().Name) return null;
            var path = resolver.ResolveAssemblyToPath(name);
            return path == null ? null : LoadFromAssemblyPath(path);
        }
    }

    private sealed class PluginContext(PluginHost host, PluginDescriptor descriptor) : IPluginContext
    {
        public readonly BoundedValues ValueStore = new();
        public int ApiVersion => descriptor.ApiVersion;
        public string PackId => descriptor.PackId;
        public string PackVersion => descriptor.Version;
        public int StateVersion => descriptor.StateVersion;
        public IPluginSimulation Simulation { get; } = descriptor.ApiVersion switch
        {
            >= 4 => host.simulation(host),
            3 => new VersionThreeSimulation(host.simulation(host)),
            >= 2 => host.simulation(host),
            _ => new VersionOneSimulation(host.simulation(host))
        };
        public IDictionary<string, string> Values => ValueStore;
        internal BoundedValues NeighborhoodStore { get; } = host.LoadNeighborhood(descriptor);
        public IDictionary<string, string> NeighborhoodValues => descriptor.ApiVersion >= 3 ? NeighborhoodStore : throw new NotSupportedException("Neighborhood values need API 3");
        public int NeighborhoodStateVersion => host.NeighborhoodVersion(descriptor);
        public byte[]? ReadLegacyNeighborhoodData(string filename) => host.ReadLegacy(descriptor, filename);
        public byte[] ReadPluginData(string filename) => host.ReadPluginData(descriptor, filename);
        public void Log(string message) => host.write(new { type = "pluginLog", packId = descriptor.PackId, message });
        public PluginImage ReadImageResource(string resource)
        {
            var bytes = File.ReadAllBytes(host.ResourcePath(descriptor, resource));
            var (width, height, rgba) = BitmapCodec.Read(bytes);
            return new PluginImage(width, height, rgba);
        }

        public void PlaySoundResource(string resource, short ownerObjectId, string cue, bool loop = false)
        {
            if (descriptor.ApiVersion < 3) throw new NotSupportedException("Pack sound resources need API3");
            if (cue.Length is 0 or > MaxKeyLength || cue.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-' && ch != '_')) throw new ArgumentException("Invalid sound cue key");
            if (!host.simulation(host).Objects.Any(item => item.ObjectId == ownerObjectId)) throw new ArgumentException("Sound owner is not on the lot");
            host.playSoundResource?.Invoke(host.ResourcePath(descriptor, resource), ownerObjectId, descriptor.PackId + ":" + cue, loop);
        }

        public void StopSoundResource(short ownerObjectId, string cue)
        {
            if (descriptor.ApiVersion < 3) throw new NotSupportedException("Pack sound resources need API3");
            if (cue.Length is 0 or > MaxKeyLength || cue.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-' && ch != '_')) throw new ArgumentException("Invalid sound cue key");
            host.stopSoundResource?.Invoke(ownerObjectId, descriptor.PackId + ":" + cue);
        }

        private void CheckAttribute(int index)
        {
            if (descriptor.ApiVersion < 3) throw new NotSupportedException("Object attributes need API3");
            if (index is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(index));
        }

        public short GetObjectAttribute(short objectId, int index)
        {
            CheckAttribute(index);
            if (host.world is not { } world) throw new InvalidOperationException("No plugin world is available");
            return world.GetObjectAttribute(descriptor.PackId, objectId, index);
        }

        public void SetObjectAttribute(short objectId, int index, short value)
        {
            CheckAttribute(index);
            if (host.world is not { } world) throw new InvalidOperationException("No plugin world is available");
            world.SetObjectAttribute(descriptor.PackId, objectId, index, value);
        }

        public void SetObjectGraphic(short objectId, short graphic)
        {
            if (descriptor.ApiVersion < 3) throw new NotSupportedException("Object graphics need API3");
            if (graphic < 0) throw new ArgumentOutOfRangeException(nameof(graphic));
            if (host.world is not { } world) throw new InvalidOperationException("No plugin world is available");
            world.SetObjectGraphic(descriptor.PackId, objectId, graphic);
        }

        public void SetPanelValue(string panelId, string binding, string value)
        {
            if (!host.declaredPanels.Contains((descriptor.PackId, panelId))) throw new ArgumentException($"Panel {panelId} is not declared by pack {descriptor.PackId}");
            if (string.IsNullOrEmpty(binding) || binding.Length > MaxKeyLength) throw new ArgumentException("Invalid panel binding");
            if (value.Length > MaxValueLength) throw new ArgumentException("Panel values are limited to 4096 characters");
            var key = (descriptor.PackId, panelId);
            if (!host.panelValues.TryGetValue(key, out var map)) host.panelValues[key] = map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (map.TryGetValue(binding, out var existing) && existing == value) return;
            map[binding] = value;
            host.panelValuesChanged = true;
        }

        public void ShowPanel(string panelId) => ShowPanel(panelId, null);
        public void ShowPanel(string panelId, short anchorObjectId) => ShowPanel(panelId, (short?)anchorObjectId);

        public void ShowPanel(string panelId, short? anchorObjectId = null)
        {
            if (!host.declaredPanels.Contains((descriptor.PackId, panelId))) throw new ArgumentException($"Panel {panelId} is not declared by pack {descriptor.PackId}");
            if (host.choosingPack != descriptor.PackId) throw new InvalidOperationException("ShowPanel may only be called from OnMenuEntry");
            if (anchorObjectId is { } id &&
                !host.simulation(host).Objects.Any(item => item.ObjectId == id) &&
                !host.simulation(host).HouseholdSims.Any(sim => sim.ObjectId == id))
                throw new ArgumentException("Anchor object does not exist on this lot");
            (host.shownPanel, host.shownPanelAnchor) = (panelId, anchorObjectId);
        }
    }

    internal sealed class BoundedValues : Dictionary<string, string>, IDictionary<string, string>
    {
        public BoundedValues() : base(StringComparer.Ordinal) { }

        private void Check(string key, string value, bool allowReplace)
        {
            if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength) throw new ArgumentException("Invalid value key");
            if (value == null || value.Length > MaxValueLength) throw new ArgumentException("Invalid value payload");
            if ((!allowReplace || !ContainsKey(key)) && Count >= MaxValues)
                throw new ArgumentException("Too many plugin values");
        }

        public new string this[string key]
        {
            get => base[key];
            set { Check(key, value, allowReplace: true); base[key] = value; }
        }

        public new void Add(string key, string value) { Check(key, value, allowReplace: false); base.Add(key, value); }

        string IDictionary<string, string>.this[string key]
        {
            get => this[key];
            set => this[key] = value;
        }

        void IDictionary<string, string>.Add(string key, string value) => Add(key, value);
        void ICollection<KeyValuePair<string, string>>.Add(KeyValuePair<string, string> item) => Add(item.Key, item.Value);
    }

    private sealed class VersionOneSimulation(IPluginSimulation inner) : IPluginSimulation
    {
        public short House => inner.House;
        public int Hours => inner.Hours;
        public int Minutes => inner.Minutes;
        public bool Paused => inner.Paused;
        public int HouseholdFunds => inner.HouseholdFunds;
        public IReadOnlyList<PluginSim> HouseholdSims => inner.HouseholdSims;
        public short GetMotive(short objectId, int motive) => inner.GetMotive(objectId, motive);
        public bool TryCharge(int amount) => inner.TryCharge(amount);
        public bool TryPay(int amount) => inner.TryPay(amount);
    }

    private sealed class VersionThreeSimulation(IPluginSimulation inner) : IPluginSimulation
    {
        public short House => inner.House;
        public int Hours => inner.Hours;
        public int Minutes => inner.Minutes;
        public bool Paused => inner.Paused;
        public int HouseholdFunds => inner.HouseholdFunds;
        public IReadOnlyList<PluginSim> HouseholdSims => inner.HouseholdSims;
        public short GetMotive(short objectId, int motive) => inner.GetMotive(objectId, motive);
        public bool TryCharge(int amount) => inner.TryCharge(amount);
        public bool TryPay(int amount) => inner.TryPay(amount);
        public IReadOnlyList<PluginObject> Objects => inner.Objects;
        public IReadOnlyList<PluginInteraction> GetInteractions(short simId, short objectId) => inner.GetInteractions(simId, objectId);
        public bool TryQueueInteraction(short simId, short objectId, int interactionId, short param0 = 0, bool global = false) =>
            inner.TryQueueInteraction(simId, objectId, interactionId, param0, global);
        public IReadOnlyList<string> GetQueuedActions(short simId) => inner.GetQueuedActions(simId);
        public short ChangeMotive(short simId, int motive, int delta) => inner.ChangeMotive(simId, motive, delta);
        public bool TryQueuePairedSocialAction(PluginPairedSocialRole first, PluginPairedSocialRole second, out long actionId)
        { actionId = 0; throw new NotSupportedException("Paired social actions need plugin API version 4"); }
        public PluginPairedSocialActionStatus? GetPairedSocialActionStatus(long actionId) =>
            throw new NotSupportedException("Paired social actions need plugin API version 4");
    }
}
