using System.Text.Json.Serialization;

namespace SimsSDL.Client;

/// <summary>
/// Discovers workspaces, per-user save/data roots, and installed <em>The Sims 1</em> content folders across macOS, Windows, and Linux.
/// </summary>
public static class InstallLocator
{
    private static readonly string InstallSettingsRelativePath = Path.Combine("Settings", "install.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class InstallSettings
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("gamePath")]
        public string GamePath { get; init; } = "";
    }

    public static string DefaultWorkspace(
        string? executablePath = null,
        string? currentDirectoryPath = null,
        string? resourcePath = null)
    {
        var resolvedCurrentDir = currentDirectoryPath ?? Directory.GetCurrentDirectory();
        var resolvedExePath = ResolveExecutablePath(executablePath);
        var exeDir = ResolveDirectory(resolvedExePath);

        var effectiveResourcePath = resourcePath ?? InferBundleResourcePath(exeDir);
        if (!string.IsNullOrEmpty(effectiveResourcePath))
        {
            var bundled = Path.Combine(effectiveResourcePath, "workspace");
            if (Directory.Exists(Path.Combine(bundled, "native", "SimsSimulation")))
            {
                return bundled;
            }
        }

        if (!string.IsNullOrEmpty(exeDir))
        {
            var portableWorkspace = Path.Combine(exeDir, "workspace");
            if (Directory.Exists(Path.Combine(portableWorkspace, "native", "SimsSimulation")))
            {
                return portableWorkspace;
            }
        }

        var starts = new List<string>(2);
        if (!string.IsNullOrEmpty(exeDir))
        {
            starts.Add(exeDir);
        }
        starts.Add(resolvedCurrentDir);

        foreach (var start in starts)
        {
            var directory = NormalizePath(start);
            while (true)
            {
                if (Directory.Exists(Path.Combine(directory, "native", "SimsSimulation")) ||
                    Directory.Exists(Path.Combine(directory, "native", "SimsSDL")))
                {
                    return directory;
                }

                var parent = Path.GetDirectoryName(directory);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, directory, StringComparison.Ordinal))
                {
                    break;
                }

                directory = parent;
            }
        }

        return !string.IsNullOrEmpty(exeDir) ? exeDir : resolvedCurrentDir;
    }

    public static bool IsStandaloneDistribution(
        string workspace,
        string? bundleIdentifier = null,
        string? executablePath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspace);
        if (SimulationEngineSelection.IsNativePreview(bundleIdentifier))
        {
            return true;
        }

        var normalizedWorkspace = NormalizePath(workspace).Replace('\\', '/');
        if (normalizedWorkspace.Contains(".app/Contents/Resources/workspace", StringComparison.Ordinal))
        {
            return true;
        }

        var hasRepoSources = File.Exists(Path.Combine(workspace, "native", "SimsSDL", "SimsSDL.csproj")) ||
                             File.Exists(Path.Combine(workspace, "native", "SimsEngine", "SimsEngine.csproj"));
        if (!hasRepoSources)
        {
            return true;
        }

        var resolvedExePath = ResolveExecutablePath(executablePath);
        var exeDir = ResolveDirectory(resolvedExePath);
        if (!string.IsNullOrEmpty(exeDir))
        {
            var exeWorkspace = NormalizePath(Path.Combine(exeDir, "workspace"));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(exeWorkspace, NormalizePath(workspace), comparison))
            {
                return true;
            }
        }

        return false;
    }

    public static string DefaultDataRoot(
        string workspace,
        bool? standalone = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspace);
        var useStandalone = standalone ?? IsStandaloneDistribution(workspace);
        if (!useStandalone)
        {
            return workspace;
        }

        var home = GetHomeDirectory(environment);
        if (OperatingSystem.IsWindows())
        {
            var baseDir = GetEnvironmentValue(environment, "LOCALAPPDATA") ??
                          GetEnvironmentValue(environment, "APPDATA");
            if (!string.IsNullOrEmpty(baseDir))
            {
                return Path.Combine(baseDir, "TheSimsNative");
            }

            return Path.Combine(home, "AppData", "Local", "TheSimsNative");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "TheSimsNative");
        }

        var xdg = GetEnvironmentValue(environment, "XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg))
        {
            return Path.Combine(xdg, "TheSimsNative");
        }

        return Path.Combine(home, ".local", "share", "TheSimsNative");
    }

    public static bool IsValidGameDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        var hasGameData = false;
        var hasUIGraphics = false;
        try
        {
            foreach (var entry in Directory.EnumerateDirectories(path))
            {
                switch (Path.GetFileName(entry).ToLowerInvariant())
                {
                    case "gamedata":
                        hasGameData = true;
                        break;
                    case "uigraphics":
                        hasUIGraphics = true;
                        break;
                }
            }
        }
        catch
        {
            return false;
        }

        return hasGameData && hasUIGraphics;
    }

    public static string? SavedInstallRoot(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);
        var file = Path.Combine(dataRoot, InstallSettingsRelativePath);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllBytes(file);
            var settings = JsonSerializer.Deserialize<InstallSettings>(json, JsonOptions);
            if (settings is null || settings.SchemaVersion != 1 || string.IsNullOrEmpty(settings.GamePath))
            {
                return null;
            }

            return IsValidGameDirectory(settings.GamePath) ? settings.GamePath : null;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveInstallRoot(string gamePath, string dataRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(gamePath);
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);
        var file = Path.Combine(dataRoot, InstallSettingsRelativePath);
        var parent = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(parent);

        var settings = new InstallSettings
        {
            SchemaVersion = 1,
            GamePath = NormalizePath(gamePath),
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        var temp = Path.Combine(parent, $".install.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(temp, payload);
        File.Move(temp, file, overwrite: true);
    }

    public static string? ResolveGameDirectory(
        string? explicitPath,
        string workspace,
        string dataRoot,
        IReadOnlyDictionary<string, string>? environment = null,
        string? executablePath = null,
        IEnumerable<string>? extraCandidates = null)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return explicitPath;
        }

        var envPath = GetEnvironmentValue(environment, "SIMS_GAME_DIR");
        if (!string.IsNullOrEmpty(envPath) && IsValidGameDirectory(envPath))
        {
            return envPath;
        }

        if (SavedInstallRoot(dataRoot) is { } saved)
        {
            return saved;
        }

        var candidates = new List<string>
        {
            Path.Combine(workspace, "runtime", "game"),
            Path.Combine(workspace, "The Sims 1"),
        };

        var resolvedExePath = ResolveExecutablePath(executablePath);
        var exeDir = ResolveDirectory(resolvedExePath);
        if (!string.IsNullOrEmpty(exeDir))
        {
            var normalizedExeDir = NormalizePath(exeDir).Replace('\\', '/');
            var bundleSiblingRoot = normalizedExeDir.EndsWith("/Contents/MacOS", StringComparison.Ordinal)
                ? Path.GetFullPath(Path.Combine(exeDir, "..", "..", ".."))
                : exeDir;

            ReadOnlySpan<string> siblingNames =
            [
                "The Sims",
                "The Sims 1",
                "The Sims Complete Collection",
                "The Sims Legacy Collection",
                "game",
            ];
            foreach (var name in siblingNames)
            {
                candidates.Add(Path.Combine(bundleSiblingRoot, name));
            }
        }

        candidates.AddRange(StandardInstallCandidates(environment));
        if (extraCandidates is not null)
        {
            candidates.AddRange(extraCandidates);
        }

        return candidates.FirstOrDefault(IsValidGameDirectory);
    }

    private static IEnumerable<string> StandardInstallCandidates(IReadOnlyDictionary<string, string>? environment)
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                @"C:\Program Files (x86)\Steam\steamapps\common\The Sims Legacy Collection",
                @"C:\Program Files\EA Games\The Sims Legacy Collection",
                @"C:\Program Files (x86)\Maxis\The Sims",
                @"C:\Program Files\Maxis\The Sims",
                @"D:\SteamLibrary\steamapps\common\The Sims Legacy Collection",
            ];
        }

        var home = GetHomeDirectory(environment);
        if (OperatingSystem.IsMacOS())
        {
            return
            [
                Path.Combine(home, "Library", "Application Support", "Steam", "steamapps", "common", "The Sims Legacy Collection"),
                "/Applications/The Sims",
                "/Applications/The Sims Complete Collection",
                "/Applications/The Sims.app/Contents/Resources",
            ];
        }

        return
        [
            Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "The Sims Legacy Collection"),
        ];
    }

    private static string? InferBundleResourcePath(string? exeDir)
    {
        if (string.IsNullOrEmpty(exeDir))
        {
            return null;
        }

        var normalized = NormalizePath(exeDir).Replace('\\', '/');
        if (normalized.EndsWith("/Contents/MacOS", StringComparison.Ordinal))
        {
            var resources = Path.GetFullPath(Path.Combine(exeDir, "..", "Resources"));
            if (Directory.Exists(resources))
            {
                return resources;
            }
        }

        return null;
    }

    private static string? ResolveExecutablePath(string? executablePath)
    {
        if (!string.IsNullOrEmpty(executablePath))
        {
            return executablePath;
        }

        if (!string.IsNullOrEmpty(Environment.ProcessPath))
        {
            return Environment.ProcessPath;
        }

        var args = Environment.GetCommandLineArgs();
        return args.Length > 0 && !string.IsNullOrEmpty(args[0]) ? Path.GetFullPath(args[0]) : null;
    }

    private static string? ResolveDirectory(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            var resolved = File.ResolveLinkTarget(filePath, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(filePath);
            return Path.GetDirectoryName(resolved);
        }
        catch
        {
            return Path.GetDirectoryName(Path.GetFullPath(filePath));
        }
    }

    private static string? GetEnvironmentValue(IReadOnlyDictionary<string, string>? environment, string key)
    {
        if (environment is not null)
        {
            return environment.TryGetValue(key, out var value) ? value : null;
        }

        return Environment.GetEnvironmentVariable(key);
    }

    private static string GetHomeDirectory(IReadOnlyDictionary<string, string>? environment)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            return userProfile;
        }

        return GetEnvironmentValue(environment, "HOME") ??
               GetEnvironmentValue(environment, "USERPROFILE") ??
               ".";
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
