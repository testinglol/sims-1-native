namespace SimsSDL.Client;

public static class SimulationEngineSelection
{
    public static bool IsNativePreview(string? bundleIdentifier)
    {
        if (string.IsNullOrEmpty(bundleIdentifier))
        {
            return false;
        }

        return string.Equals(bundleIdentifier, "local.sims.native-preview", StringComparison.Ordinal) ||
               bundleIdentifier.StartsWith("local.sims.native-preview.", StringComparison.Ordinal);
    }

    public static bool UsesNativeEngine(
        string? bundleIdentifier = null,
        IReadOnlyDictionary<string, string>? environment = null,
        string? executablePath = null)
    {
        var mode = GetEnvironmentValue(environment, "SIMS_ENGINE")?.ToLowerInvariant();
        if (mode is "legacy")
        {
            return false;
        }
        if (mode is "native")
        {
            return true;
        }

        if (IsNativePreview(bundleIdentifier))
        {
            return true;
        }

        var exePath = executablePath ?? Environment.ProcessPath;
        var exeDir = ResolveDirectory(exePath);
        if (!string.IsNullOrEmpty(exeDir))
        {
            ReadOnlySpan<string> candidates =
            [
                Path.Combine(exeDir, "workspace", "native", "SimsSimulation", "bin", "Release", "net9.0"),
                Path.GetFullPath(Path.Combine(exeDir, "..", "Resources", "workspace", "native", "SimsSimulation", "bin", "Release", "net9.0")),
            ];
            ReadOnlySpan<string> hostLibs = ["libhostfxr.dylib", "libhostfxr.so", "hostfxr.dll"];
            foreach (var dir in candidates)
            {
                foreach (var lib in hostLibs)
                {
                    if (File.Exists(Path.Combine(dir, lib)))
                    {
                        return true;
                    }
                }
            }
        }

        return true;
    }

    private static string? GetEnvironmentValue(IReadOnlyDictionary<string, string>? environment, string key)
    {
        if (environment is not null)
        {
            return environment.TryGetValue(key, out var value) ? value : null;
        }

        return Environment.GetEnvironmentVariable(key);
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
}

public static class AwayLotLocks
{
    public static (bool BuyBuildLocked, bool? LotSaveDisabled) Compute(
        int house,
        int? homeHouse,
        bool simlessBuildMode,
        bool? awayFlag)
    {
        if (simlessBuildMode || house <= 0)
        {
            return (false, awayFlag == false ? false : null);
        }

        if (awayFlag is { } flag)
        {
            if (flag)
            {
                return (true, !(house >= 40 && house <= 48));
            }

            return (false, false);
        }

        var inAwayRange = (house >= 21 && house <= 31) ||
                          (house >= 40 && house <= 49) ||
                          (house >= 50 && house <= 99);
        var isAwayVisit = homeHouse is { } home && house != home && inAwayRange;
        return (isAwayVisit, isAwayVisit ? !(house >= 40 && house <= 48) : null);
    }
}
