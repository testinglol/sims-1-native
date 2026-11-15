namespace SimsEngine.Tests;

internal static class TestGameLocator
{
    public static string? Locate()
    {
        var envGameDir = Environment.GetEnvironmentVariable("SIMS_GAME_DIR");
        if (!string.IsNullOrWhiteSpace(envGameDir) && Directory.Exists(envGameDir))
        {
            return envGameDir;
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var theSims1 = Path.Combine(dir.FullName, "The Sims 1");
                if (Directory.Exists(theSims1)) return theSims1;

                var runtimeGame = Path.Combine(dir.FullName, "runtime", "game");
                if (Directory.Exists(runtimeGame)) return runtimeGame;
            }
        }

        return null;
    }
}
