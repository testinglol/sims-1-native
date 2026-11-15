using System.Globalization;

namespace SimsSDL.Client;

/// <summary>
/// Exports neighborhood manifests and artwork directly in-process using <c>SimsSimulation</c>, with post-export caching
/// keyed on game content, neighborhood mode, profile files, and export flags.
/// </summary>
public static class NeighborhoodExport
{
    private sealed record Cached(string Session, string Manifest, string[] Fingerprint);

    private static readonly Dictionary<string, Cached> Cache = new(StringComparer.Ordinal);
    private static readonly Lock CacheLock = new();
    private static readonly JsonSerializerOptions SortedKeyJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<(string Session, string Manifest)> Create(
        GameContent content,
        string workspace,
        string? dataRoot = null,
        int mode = 1,
        string profileId = "player",
        string? edit = null,
        int neighborhood = 1,
        bool discardsContent = false,
        bool nativeEngine = false,
        bool includeWebScenes = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(workspace);

        var storageRoot = dataRoot ?? workspace;
        var contentRoot = content.Packs.Ordered.Count == 0
            ? content.Root
            : await SimulationBridge.SharedContentMount(content, storageRoot, nativeEngine).ConfigureAwait(false);

        return await Task.Run(() => CreateSynchronously(
            content,
            workspace,
            dataRoot,
            mode,
            profileId,
            edit,
            neighborhood,
            discardsContent,
            contentRoot,
            nativeEngine,
            includeWebScenes)).ConfigureAwait(false);
    }

    public static Task<(string Session, string Manifest)> CreateAsync(
        GameContent content,
        string workspace,
        string? dataRoot = null,
        int mode = 1,
        string profileId = "player",
        string? edit = null,
        int neighborhood = 1,
        bool discardsContent = false,
        bool nativeEngine = false,
        bool includeWebScenes = false) =>
        Create(content, workspace, dataRoot, mode, profileId, edit, neighborhood, discardsContent, nativeEngine, includeWebScenes);

    public static (string Session, string Manifest) CreateSynchronously(
        GameContent content,
        string workspace,
        string? dataRoot = null,
        int mode = 1,
        string profileId = "player",
        string? edit = null,
        int neighborhood = 1,
        bool discardsContent = false,
        string? contentRoot = null,
        bool nativeEngine = false,
        bool includeWebScenes = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(workspace);
        SimulationHost.ValidateMode(mode);
        SimulationHost.ValidateNeighborhood(neighborhood);
        global::ProfileBootstrap.ValidateProfileId(profileId);

        var storageRoot = dataRoot ?? workspace;
        // An unchanged map is shown again from its earlier export: an export walks and renders lot artwork, and a session
        // keeps its manifest and images after its content mount is discarded, so callers that discard the mount share it.
        // Edits always export, and any change to the profile's files misses the cache.
        var key = edit is null && discardsContent
            ? CacheKey(content, workspace, storageRoot, mode, profileId, neighborhood, includeWebScenes)
            : null;
        var profile = Path.Combine(storageRoot, "runtime", "profiles", profileId);
        if (key is not null &&
            TryGetCached(key, out var hit) &&
            hit.Fingerprint.SequenceEqual(Fingerprint(profile)) &&
            File.Exists(hit.Manifest))
        {
            return (hit.Session, hit.Manifest);
        }

        var session = Path.Combine(storageRoot, "runtime", "sessions", "neighborhood-" + Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(session);
        SessionSweeper.Claim(session);

        var ownsMount = contentRoot is null;
        var mount = contentRoot ?? Path.Combine(session, "content");
        try
        {
            if (ownsMount)
            {
                content.Materialize(mount);
            }

            lock (SimulationHost.SimulationStateLock)
            {
                var previousNeighborhood = global::Neighborhoods.Current;
                var previousGameRoot = global::NeighborhoodExport.GameRoot;
                var previousEngine = Environment.GetEnvironmentVariable("SIMS_ENGINE");
                try
                {
                    if (nativeEngine)
                    {
                        Environment.SetEnvironmentVariable("SIMS_ENGINE", "native");
                    }

                    SimulationHost.PrepareNeighborhood(content.Root, storageRoot, profileId, neighborhood);
                    var languageCode = (byte)content.Language.Code;
                    if (!string.IsNullOrEmpty(edit))
                    {
                        global::NeighborhoodEdits.Apply(edit, content.Root, profile, languageCode, mode);
                    }

                    global::NeighborhoodExport.Export(
                        session,
                        mode,
                        profile,
                        languageCode,
                        storageRoot,
                        mount,
                        includeRenderScenes: includeWebScenes);
                }
                finally
                {
                    global::Neighborhoods.Current = previousNeighborhood;
                    global::NeighborhoodExport.GameRoot = previousGameRoot;
                    if (nativeEngine)
                    {
                        Environment.SetEnvironmentVariable("SIMS_ENGINE", previousEngine);
                    }
                }
            }
        }
        catch (ContentError)
        {
            throw;
        }
        catch (Exception error)
        {
            WriteFailureLog(session, error);
            throw new ContentError($"Neighborhood export failed: {error.Message}");
        }
        finally
        {
            // The export only reads the mount; its images and manifest live beside it in the session.
            if (discardsContent && ownsMount)
            {
                SessionSweeper.DeleteEntrySafely(mount);
            }
        }

        var manifest = Path.Combine(session, "neighborhood.json");
        if (!File.Exists(manifest))
        {
            throw new ContentError("Neighborhood export did not write its manifest");
        }

        // The fingerprint is taken after the export, so files the export itself writes into the profile do not miss the next lookup.
        if (key is not null)
        {
            Store(key, new Cached(session, manifest, Fingerprint(profile)));
        }

        return (session, manifest);
    }

    /// <summary>
    /// Looks up an earlier neighborhood export whose profile files have not changed.
    /// </summary>
    public static (string Session, string Manifest)? LookupCached(
        GameContent content,
        string workspace,
        string? dataRoot = null,
        int mode = 1,
        string profileId = "player",
        int neighborhood = 1,
        bool includeWebScenes = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(workspace);

        var storageRoot = dataRoot ?? workspace;
        var key = CacheKey(content, workspace, storageRoot, mode, profileId, neighborhood, includeWebScenes);
        var profile = Path.Combine(storageRoot, "runtime", "profiles", profileId);
        if (!TryGetCached(key, out var hit) ||
            !hit.Fingerprint.SequenceEqual(Fingerprint(profile)) ||
            !File.Exists(hit.Manifest))
        {
            return null;
        }

        return (hit.Session, hit.Manifest);
    }

    /// <summary>
    /// Stores a neighborhood export alongside the profile's post-export file fingerprint.
    /// </summary>
    public static void StoreCached(
        GameContent content,
        string workspace,
        string? dataRoot = null,
        int mode = 1,
        string profileId = "player",
        int neighborhood = 1,
        bool includeWebScenes = false,
        string session = "",
        string manifest = "")
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(workspace);
        ArgumentException.ThrowIfNullOrEmpty(session);
        ArgumentException.ThrowIfNullOrEmpty(manifest);

        var storageRoot = dataRoot ?? workspace;
        var key = CacheKey(content, workspace, storageRoot, mode, profileId, neighborhood, includeWebScenes);
        var profile = Path.Combine(storageRoot, "runtime", "profiles", profileId);
        Store(key, new Cached(session, manifest, Fingerprint(profile)));
    }

    /// <summary>
    /// Forgets every earlier export, for callers that change what the export reads outside the profile folder.
    /// </summary>
    public static void InvalidateCache()
    {
        lock (CacheLock)
        {
            Cache.Clear();
        }
    }

    private static bool TryGetCached(string key, out Cached entry)
    {
        lock (CacheLock)
        {
            return Cache.TryGetValue(key, out entry!);
        }
    }

    private static void Store(string key, Cached entry)
    {
        lock (CacheLock)
        {
            Cache[key] = entry;
        }
    }

    private static string CacheKey(
        GameContent content,
        string workspace,
        string storageRoot,
        int mode,
        string profileId,
        int neighborhood,
        bool includeWebScenes)
    {
        var packs = content.Packs.Ordered.Select(pack =>
        {
            string manifestJson;
            try
            {
                manifestJson = JsonSerializer.Serialize(pack.Manifest, SortedKeyJsonOptions);
            }
            catch
            {
                manifestJson = "";
            }

            return $"{pack.Directory}={manifestJson}";
        });

        var parts = new List<string>(8 + content.Packs.Ordered.Count)
        {
            content.Root,
            content.Language.Code.ToString(CultureInfo.InvariantCulture),
            workspace,
            storageRoot,
            mode.ToString(CultureInfo.InvariantCulture),
            profileId,
            neighborhood.ToString(CultureInfo.InvariantCulture),
            includeWebScenes ? "true" : "false",
        };
        parts.AddRange(packs);
        return string.Join('\n', parts);
    }

    /// <summary>
    /// Every file under the profile with its size and modification time, which change whenever a save or edit writes there.
    /// </summary>
    private static string[] Fingerprint(string profile)
    {
        if (!Directory.Exists(profile))
        {
            return [];
        }

        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var webPagesPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(profile, "Web Pages"))) +
                             Path.DirectorySeparatorChar;
        var entries = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories))
            {
                var fullPath = Path.GetFullPath(file);
                if (fullPath.StartsWith(webPagesPrefix, pathComparison))
                {
                    continue;
                }

                var info = new FileInfo(fullPath);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                entries.Add(
                    $"{fullPath} {info.Length.ToString(CultureInfo.InvariantCulture)} {info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}");
            }
        }
        catch
        {
            return [];
        }

        entries.Sort(StringComparer.Ordinal);
        return [.. entries];
    }

    private static void WriteFailureLog(string session, Exception error)
    {
        try
        {
            File.WriteAllText(Path.Combine(session, "neighborhood-export.log"), error.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Best-effort diagnostics log.
        }
    }
}
