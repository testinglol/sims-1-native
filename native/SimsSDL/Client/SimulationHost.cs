namespace SimsSDL.Client;

/// <summary>
/// Provides in-process simulation host resolution and shared profile/session/mode helpers.
/// <para>
/// In the unified .NET 9 <c>SimsSDL</c> binary, <c>SimsSimulation</c> and <c>SimsEngine</c> run directly in-process
/// via <see cref="SimsSimulation.SimulationServer"/> and <see cref="global::FrontendAudioServer"/>, eliminating external child-process lookups.
/// </para>
/// </summary>
public static class SimulationHost
{
    public sealed record Launch(string Executable, IReadOnlyList<string> LeadingArguments);

    /// <summary>
    /// Guards process-wide static state inside <c>SimsSimulation</c> (<see cref="global::Neighborhoods.Current"/>,
    /// <see cref="global::NeighborhoodExport.GameRoot"/>, and profile neighborhood bootstrapping) during concurrent calls.
    /// </summary>
    internal static readonly Lock SimulationStateLock = new();

    public static bool IsInProcess => true;

    public static Launch Resolve(string workspace, string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspace);
        ArgumentException.ThrowIfNullOrEmpty(purpose);

        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            executable = AppContext.BaseDirectory;
        }

        return new Launch(executable, []);
    }

    public static string RuntimeDirectory(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);
        return Path.Combine(dataRoot, "runtime");
    }

    public static string ProfilesDirectory(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);
        return Path.Combine(dataRoot, "runtime", "profiles");
    }

    public static string ProfileDirectory(string dataRoot, string profileId = "player")
    {
        global::ProfileBootstrap.ValidateProfileId(profileId);
        return Path.Combine(ProfilesDirectory(dataRoot), profileId);
    }

    public static string SessionsDirectory(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);
        return Path.Combine(dataRoot, "runtime", "sessions");
    }

    public static string NewSessionDirectory(string dataRoot, string? prefix = null)
    {
        var name = string.IsNullOrEmpty(prefix)
            ? Guid.NewGuid().ToString("D")
            : $"{prefix}-{Guid.NewGuid():D}";
        return Path.Combine(SessionsDirectory(dataRoot), name);
    }

    public static void ValidateMode(int mode)
    {
        if (mode < 1 || mode > 7)
        {
            throw new ContentError("Neighborhood mode must be 1..7");
        }
    }

    public static void ValidateNeighborhood(int neighborhood)
    {
        if (neighborhood < 1 || neighborhood > global::Neighborhoods.Maximum)
        {
            throw new ContentError($"Neighborhood number must be 1..{(global::Neighborhoods.Maximum)}");
        }
    }

    public static void ValidateHouse(int house, bool allowDaemonZero = false)
    {
        var min = allowDaemonZero ? 0 : 1;
        if (house < min || house > 100)
        {
            throw new ContentError(allowDaemonZero ? "House must be 0..100" : "House must be 1..100");
        }
    }

    /// <summary>
    /// Ensures the profile directory and requested neighborhood exist on disk, returning the resolved profile path
    /// and neighborhood <c>UserData</c> directory. Must be called under <see cref="SimulationStateLock"/> when mutating
    /// shared <see cref="global::Neighborhoods.Current"/> state alongside other calls.
    /// </summary>
    internal static (string ProfilePath, string UserDataPath) PrepareNeighborhood(
        string installRoot,
        string dataRoot,
        string profileId,
        int neighborhood)
    {
        ValidateNeighborhood(neighborhood);
        var profilePath = ProfileDirectory(dataRoot, profileId);
        global::Neighborhoods.Current = neighborhood;
        global::ProfileBootstrap.EnsureNeighborhoodImported(installRoot, profilePath, neighborhood);
        var userDataPath = global::Neighborhoods.UserData(profilePath);
        if (!File.Exists(Path.Combine(userDataPath, "Neighborhood.iff")))
        {
            throw new ContentError($"Neighborhood {neighborhood} is not installed in this profile");
        }

        return (profilePath, userDataPath);
    }

    internal static void ApplyNativeEngineEnvironmentIfSelected(bool nativeEngine)
    {
        if (nativeEngine || SimulationEngineSelection.UsesNativeEngine())
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("SIMS_ENGINE"), "legacy", StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("SIMS_ENGINE", "native");
            }
        }
    }
}
