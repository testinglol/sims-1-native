using System.Text.Json;

internal static class ServingEntry
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Length > 0 && args[0] == "--compile-animation")
        {
            if (args.Length is not (3 or 4))
            {
                Console.Error.WriteLine("Usage: SimsSimulation --compile-animation INPUT_JSON NEW_PACK_DIRECTORY [GAME_DIRECTORY]");
                return 1;
            }
            try
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    AnimationAuthor.Compile(args[1], args[2], args.Length == 4 ? args[3] : null),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return 0;
            }
            catch (Exception error) when (error is IOException or JsonException or ArgumentException or InvalidDataException)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        if (args.Length > 0 && args[0] == "--tso-inventory")
        {
            if (args.Length != 3)
            {
                Console.Error.WriteLine("Usage: SimsSimulation --tso-inventory OBJIFF_FAR OUTPUT_TSV");
                return 1;
            }
            return TsoInventory.Run(args[1], args[2]);
        }

        if (args.Length > 0 && args[0] == "--tso-merge")
        {
            if (args.Length != 4)
            {
                Console.Error.WriteLine("Usage: SimsSimulation --tso-merge TSO_OBJECTS_DIRECTORY NAME OUTPUT_IFF");
                return 1;
            }
            return TsoInventory.Merge(args[1], args[2], args[3]);
        }

        if (args.Length > 0 && args[0] == "--compile-object")
        {
            if (args.Length != 4)
            {
                Console.Error.WriteLine("Usage: SimsSimulation --compile-object INPUT_JSON GAME_DIRECTORY NEW_PACK_DIRECTORY");
                return 1;
            }
            try
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    ObjectAuthor.Compile(args[1], args[2], args[3]),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return 0;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        if (args.Length > 0 && args[0] is "--compile-outfit" or "--export-outfit-textures" or "--export-outfit-mesh")
        {
            if (args.Length != 4)
            {
                Console.Error.WriteLine(args[0] == "--compile-outfit"
                    ? "Usage: SimsSimulation --compile-outfit INPUT_JSON GAME_DIRECTORY NEW_PACK_DIRECTORY"
                    : $"Usage: SimsSimulation {args[0]} TEMPLATE_OUTFIT GAME_DIRECTORY OUTPUT_DIRECTORY");
                return 1;
            }
            try
            {
                var outfitResult = args[0] switch
                {
                    "--compile-outfit" => OutfitAuthor.Compile(args[1], args[2], args[3]),
                    "--export-outfit-mesh" => OutfitAuthor.ExportMeshes(args[1], args[2], args[3]),
                    _ => OutfitAuthor.ExportTextures(args[1], args[2], args[3])
                };
                Console.WriteLine(JsonSerializer.Serialize(outfitResult, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return 0;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        if (args.Length > 0 && args[0] == "--export-object-sprites")
        {
            if (args.Length != 4)
            {
                Console.Error.WriteLine("Usage: SimsSimulation --export-object-sprites TEMPLATE_IFF GAME_DIRECTORY OUTPUT_DIRECTORY");
                return 1;
            }
            try
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    ObjectAuthor.ExportSprites(args[1], args[2], args[3]),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return 0;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        if (args.Length > 0 && args[0] == "--import-original-user-data")
        {
            if (args.Length != 5 && (args.Length != 7 || args[5] != "--data-root"))
            {
                Console.Error.WriteLine("Usage: SimsSimulation --import-original-user-data GAME WORKSPACE SOURCE_DIRECTORY PROFILE_ID [--data-root DIRECTORY]");
                return 1;
            }
            try
            {
                var importDataRoot = args.Length == 7 ? Path.GetFullPath(args[6]) : Path.GetFullPath(args[2]);
                var imported = ProfileBootstrap.ImportOriginalUserData(args[1], importDataRoot, args[3], args[4]);
                Console.WriteLine(JsonSerializer.Serialize(imported, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        if (args.Length is 1 or 2 && args[0] == "--check-plugins")
        {
            try
            {
                var pluginFolder = args.Length == 2 ? args[1] : Path.Combine("examples", "plugin-pack", "plugin");
                Console.WriteLine(JsonSerializer.Serialize(
                    PluginChecks.Run(pluginFolder),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return 0;
            }
            catch (Exception error) when (error is InvalidDataException or IOException)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        if (args.Length is 2 or 3 && args[0] == "--check-native-floor-art")
        {
            try
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    FloorArtworkChecks.Run(args[1], args.Length == 3 ? args[2] : null),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or SimsEngine.Formats.ResourceFormatException)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        if (args.Length == 1 && args[0] == "--check-profile-bootstrap")
        {
            try
            {
                Console.WriteLine(JsonSerializer.Serialize(ProfileBootstrap.Check()));
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        var batchRunOption = Array.IndexOf(args, "--batch-run");
        int batchTicks = 0;
        if (batchRunOption >= 0)
        {
            if (batchRunOption + 1 >= args.Length || !int.TryParse(args[batchRunOption + 1], out batchTicks) || batchTicks <= 0)
            {
                Console.Error.WriteLine("--batch-run requires a positive integer tick count");
                return 1;
            }
        }

        if (args.Length < 4 && batchTicks == 0)
        {
            Console.Error.WriteLine("Usage: SimsSimulation GAME WORKSPACE --serve SESSION_DIRECTORY [HOUSE]");
            return 1;
        }

        var installRootOption = Array.IndexOf(args, "--install-root");
        if (installRootOption >= 0 && (installRootOption + 1 >= args.Length || args[installRootOption + 1].StartsWith("--", StringComparison.Ordinal)))
        {
            Console.Error.WriteLine("--install-root requires an installed game directory");
            return 1;
        }

        string game;
        string workspace;
        if (args.Length >= 2 && !args[0].StartsWith("--", StringComparison.Ordinal) && !args[1].StartsWith("--", StringComparison.Ordinal))
        {
            game = Path.GetFullPath(args[0]);
            workspace = Path.GetFullPath(args[1]);
        }
        else
        {
            var defaultGame = Path.GetFullPath(Directory.Exists("The Sims 1") ? "The Sims 1" : (Directory.Exists("runtime/game") ? "runtime/game" : "."));
            game = installRootOption >= 0 ? Path.GetFullPath(args[installRootOption + 1]) : defaultGame;
            workspace = Directory.Exists("runtime/game") ? Path.GetFullPath("runtime/game") : Path.GetFullPath(".");
        }

        var session = batchTicks > 0
            ? Path.Combine(Path.GetTempPath(), "sims-batch-" + Guid.NewGuid().ToString("N"))
            : (args.Length > 3 && !args[3].StartsWith("--", StringComparison.Ordinal)
                ? Path.GetFullPath(args[3])
                : Path.Combine(workspace, "batch-session"));
        var house = args.Length > 4 && short.TryParse(args[4], out var h) && h >= 0 ? h : (short)5;
        var dataRootOption = Array.IndexOf(args, "--data-root");
        if (dataRootOption >= 0 && (dataRootOption + 1 >= args.Length || args[dataRootOption + 1].StartsWith("--", StringComparison.Ordinal)))
        {
            Console.Error.WriteLine("--data-root requires a directory path");
            return 1;
        }
        var dataRoot = dataRootOption >= 0 && dataRootOption + 1 < args.Length
            ? Path.GetFullPath(args[dataRootOption + 1])
            : workspace;
        var installRoot = installRootOption >= 0
            ? Path.GetFullPath(args[installRootOption + 1])
            : ResolveInstallRoot(game, workspace);
        var profileOption = Array.IndexOf(args, "--profile-id");
        var profileId = profileOption >= 0 && profileOption + 1 < args.Length ? args[profileOption + 1] : "player";
        try { ProfileBootstrap.ValidateProfileId(profileId); }
        catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 1; }

        var languageCode = (byte)1;
        var languageOption = Array.IndexOf(args, "--language");
        if (languageOption >= 0 && (languageOption + 1 >= args.Length ||
            !byte.TryParse(args[languageOption + 1], out languageCode) || languageCode is < 1 or > 20))
        {
            Console.Error.WriteLine("--language must be a Sims language code from 1 through 20");
            return 1;
        }

        var neighborhoodOption = Array.IndexOf(args, "--neighborhood");
        if (neighborhoodOption >= 0)
        {
            if (neighborhoodOption + 1 >= args.Length || !int.TryParse(args[neighborhoodOption + 1], out var neighborhood) ||
                neighborhood is < 1 or > Neighborhoods.Maximum)
            {
                Console.Error.WriteLine("--neighborhood must be a neighborhood number from 1 through 99");
                return 1;
            }
            Neighborhoods.Current = neighborhood;
        }

        var profile = Path.Combine(dataRoot, "runtime", "profiles", profileId);
        try
        {
            ProfileBootstrap.EnsureNeighborhoodImported(installRoot, profile, Neighborhoods.Current);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        var userData = Neighborhoods.UserData(profile);
        if (!File.Exists(Path.Combine(userData, "Neighborhood.iff")))
        {
            Console.Error.WriteLine($"Neighborhood {Neighborhoods.Current} is not installed in this profile");
            return 1;
        }

        if (args.Length > 2 && args[2] == "--export-neighborhood")
        {
            var exportSession = Path.GetFullPath(args[3]);
            var mode = args.Length > 4 && !args[4].StartsWith("--", StringComparison.Ordinal)
                ? short.Parse(args[4])
                : (short)1;
            var editOption = Array.IndexOf(args, "--edit");
            if (editOption >= 0 && editOption + 1 < args.Length)
            {
                NeighborhoodEdits.Apply(args[editOption + 1], installRoot, profile, languageCode, mode);
            }
            var includeWebScenes = Array.IndexOf(args, "--web-scenes") >= 0;
            NeighborhoodExport.Export(exportSession, mode, profile, languageCode, dataRoot, game, includeRenderScenes: includeWebScenes);
            return 0;
        }

        if (args.Length > 2 && args[2] == "--serve-audio")
        {
            var audioSession = Path.GetFullPath(args[3]);
            var audioProtocol = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(Console.Error);
            await new FrontendAudioServer(audioProtocol, audioSession, profile, installRoot, languageCode).Run();
            return 0;
        }

        if (args.Length > 2 && args[2] == "--appearance-catalog")
        {
            if (args.Length < 6)
            {
                Console.Error.WriteLine("Usage: SimsSimulation GAME WORKSPACE --appearance-catalog GENDER AGE SKIN");
                return 1;
            }
            var registry = SimsEngine.Content.ObjectRegistry.Scan(installRoot, Path.Combine(userData, "Characters"));
            var catalog = HouseholdAppearance.Catalog(installRoot, registry, args[3], args[4], args[5]);
            Console.WriteLine(JsonSerializer.Serialize(catalog));
            return 0;
        }

        if (args.Length > 2 && args[2] == "--appearance-preview")
        {
            if (args.Length < 9)
            {
                Console.Error.WriteLine("Usage: SimsSimulation GAME WORKSPACE --appearance-preview SESSION GENDER AGE SKIN HEAD BODY");
                return 1;
            }
            var registry = SimsEngine.Content.ObjectRegistry.Scan(installRoot, Path.Combine(userData, "Characters"));
            var previewSession = Path.GetFullPath(args[3]);
            var preview = HouseholdAppearancePreview.Export(
                previewSession, installRoot, registry, args[4], args[5], args[6], args[7], args[8]);
            Console.WriteLine(JsonSerializer.Serialize(preview));
            return 0;
        }

        if (batchTicks > 0)
        {
            try
            {
                var batchServer = SimsSimulation.SimulationServer.Load(installRoot, session, profile, house, StreamWriter.Null, languageCode);
                var completed = batchServer.BatchRun(batchTicks);
                Console.Error.WriteLine($"Headless batch run completed {completed}/{batchTicks} ticks.");
                return 0;
            }
            finally
            {
                if (Directory.Exists(session))
                {
                    try { Directory.Delete(session, true); } catch { }
                }
            }
        }

        var protocol = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(Console.Error);
        // The first argument is the host's extracted content cache. WorldBootstrap needs the installed
        // directory layout (GameData, UIGraphics, expansion packs), supplied separately by --install-root.
        var server = SimsSimulation.SimulationServer.Load(game, session, profile, house, protocol, languageCode);
        await server.Run();
        return 0;
    }

    private static string ResolveInstallRoot(string game, string workspace)
    {
        if (Directory.Exists(Path.Combine(game, "UserData"))) return game;
        var gameDataPath = Path.Combine(game, "GameData");
        if (Directory.Exists(gameDataPath) &&
            new DirectoryInfo(gameDataPath).ResolveLinkTarget(true)?.FullName is { } resolvedGameData &&
            Path.GetDirectoryName(resolvedGameData) is { } parent &&
            Directory.Exists(Path.Combine(parent, "UserData")))
            return parent;
        if (Environment.GetEnvironmentVariable("SIMS_GAME_DIR") is { Length: > 0 } envGame &&
            Directory.Exists(Path.Combine(envGame, "UserData")))
            return Path.GetFullPath(envGame);
        foreach (var candidate in new[] { Path.Combine(workspace, "runtime", "game"), Path.Combine(workspace, "The Sims 1") })
            if (Directory.Exists(Path.Combine(candidate, "UserData")))
                return Path.GetFullPath(candidate);
        return game;
    }
}
