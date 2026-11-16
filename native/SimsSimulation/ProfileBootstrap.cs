using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SimsEngine.Formats;

internal static class ProfileBootstrap
{
    private static readonly string[] TemplateDirectories =
    ["TemplateCommunity", "TemplateDowntown", "TemplateVacation", "TemplateStudiotown", "TemplateMagicTown"];

    private static readonly HashSet<string> NeighborhoodFiles =
    ["LotLocations.iff", "LotZoning.iff", "StreetNames.iff"];

    private readonly record struct ResourceChunk(string Type, ushort Id, byte[] Bytes);
    private readonly record struct UserDataSource(string Name, string Path);

    internal static bool EnsureImported(string gameDirectory, string profileDirectory)
        => EnsureImported(gameDirectory, profileDirectory, onlySource: null);

    internal static bool EnsureNeighborhoodImported(string gameDirectory, string profileDirectory, int neighborhood)
    {
        if (neighborhood is < 1 or > Neighborhoods.Maximum)
            throw new ArgumentOutOfRangeException(nameof(neighborhood));
        return EnsureImported(gameDirectory, profileDirectory,
            onlySource: neighborhood == 1 ? "UserData" : $"UserData{neighborhood}");
    }

    private static bool EnsureImported(string gameDirectory, string profileDirectory, string? onlySource)
    {
        var game = Path.GetFullPath(gameDirectory);
        var profile = Path.GetFullPath(profileDirectory);
        var sources = FindUserDataDirectories(game)
            .Where(source => onlySource is null || source.Name.Equals(onlySource, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (sources.Length == 0) throw new DirectoryNotFoundException("The selected Sims installation has no UserData directories.");
        Directory.CreateDirectory(profile);
        var imported = false;
        foreach (var source in sources)
        {
            var destination = Path.Combine(profile, source.Name);
            if (Directory.Exists(destination))
            {
                EnsureNeighborhoodRoutes(game, destination);
                continue;
            }
            if (File.Exists(destination)) throw new InvalidDataException($"The native profile {source.Name} path is a file, not a directory.");

            var sourceFiles = EnumerateNeighborhoodFiles(source.Path).ToArray();
            if (sourceFiles.Length == 0) throw new InvalidDataException($"The selected Sims installation has an empty {source.Name} directory.");

            var staging = Path.Combine(profile, source.Name + ".importing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var file in sourceFiles)
                {
                    var relative = Path.GetRelativePath(source.Path, file);
                    var target = Path.GetFullPath(Path.Combine(staging, relative));
                    if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new InvalidDataException("An imported path escapes the native profile.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    CopyProfileFile(file, target);
                }

                MergeNeighborhoodTemplates(game, staging);
                if (source.Name == "UserData") OfferUnleashedFamilies(game, staging);

                try
                {
                    Directory.Move(staging, destination);
                    imported = true;
                }
                catch (IOException) when (Directory.Exists(destination))
                {
                    EnsureNeighborhoodRoutes(game, destination);
                }
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }
        return imported;
    }

    private static void CopyProfileFile(string source, string destination)
    {
        if (OperatingSystem.IsMacOS() &&
            NativeCopyFile(source, destination, IntPtr.Zero, CopyFileData | CopyFileClone) == 0)
            return;

        if (File.Exists(destination)) File.Delete(destination);
        File.Copy(source, destination, false);
    }

    private static IEnumerable<string> EnumerateNeighborhoodFiles(string source)
    {
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                    yield return file;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                if (directory == source && IsUserDataDirectoryName(Path.GetFileName(child))) continue;
                pending.Push(child);
            }
        }
    }

    private const uint CopyFileData = 0x00000008;
    private const uint CopyFileClone = 0x01000000;

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "copyfile", SetLastError = true)]
    private static extern int NativeCopyFile(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination,
        IntPtr state,
        uint flags);

    internal static object ImportOriginalUserData(string gameDirectory, string dataRootDirectory, string sourceDirectory, string profileId)
    {
        ValidateProfileId(profileId);

        var game = Path.GetFullPath(gameDirectory);
        var dataRoot = Path.GetFullPath(dataRootDirectory);
        var selectedSource = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(selectedSource)) throw new DirectoryNotFoundException("The selected original save folder does not exist.");
        var selectedName = Path.GetFileName(selectedSource.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var sourceRoot = IsUserDataDirectoryName(selectedName) ? Directory.GetParent(selectedSource)?.FullName ?? selectedSource : selectedSource;
        var userDataSources = FindUserDataDirectories(sourceRoot);
        if (userDataSources.Length == 0 && File.Exists(Path.Combine(selectedSource, "Neighborhood.iff")))
            userDataSources = [new UserDataSource("UserData", selectedSource)];
        if (!userDataSources.Any(source => source.Name.Equals("UserData", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The selected original save folder has no base UserData neighborhood.");

        var sourcesWithFiles = new List<(UserDataSource Source, string[] Files)>();
        var importedHouseCount = 0;
        var importedFileCount = 0;
        foreach (var source in userDataSources)
        {
            var sourceFiles = EnumerateNeighborhoodFiles(source.Path).ToArray();
            var neighborhoodFile = sourceFiles.FirstOrDefault(file => Path.GetRelativePath(source.Path, file).Equals("Neighborhood.iff", StringComparison.OrdinalIgnoreCase));
            if (neighborhoodFile == null)
                throw new InvalidDataException($"The selected original save's {source.Name} directory has no Neighborhood.iff.");
            ValidateOriginalIFF(neighborhoodFile);
            var importedHouses = sourceFiles.Where(file =>
            {
                var relative = Path.GetRelativePath(source.Path, file);
                return string.Equals(Path.GetDirectoryName(relative), "Houses", StringComparison.OrdinalIgnoreCase) &&
                       Path.GetFileName(relative).StartsWith("House", StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(Path.GetExtension(relative), ".iff", StringComparison.OrdinalIgnoreCase);
            }).ToArray();
            if (importedHouses.Length == 0) throw new InvalidDataException($"The selected original save's {source.Name} directory has no household lots under Houses/.");
            foreach (var house in importedHouses) ValidateOriginalIFF(house);
            sourcesWithFiles.Add((source, sourceFiles));
            importedHouseCount += importedHouses.Length;
            importedFileCount += sourceFiles.Length;
        }

        var profileDirectory = Path.Combine(dataRoot, "runtime", "profiles", profileId);
        if (Directory.Exists(profileDirectory) || File.Exists(profileDirectory))
            throw new IOException($"Profile '{profileId}' already exists; the importer never overwrites profiles.");
        var staging = profileDirectory + ".importing-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (source, sourceFiles) in sourcesWithFiles)
            {
                var stagingUserData = Path.Combine(staging, source.Name);
                Directory.CreateDirectory(stagingUserData);
                foreach (var file in sourceFiles)
                {
                    if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) continue;
                    var relative = Path.GetRelativePath(source.Path, file);
                    var target = Path.GetFullPath(Path.Combine(stagingUserData, relative));
                    if (!target.StartsWith(stagingUserData + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        throw new InvalidDataException("An imported path escapes the profile.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, false);
                }
                MergeNeighborhoodTemplates(game, stagingUserData);
            }

            Directory.Move(staging, profileDirectory);
            return new
            {
                result = "imported",
                profileId,
                sourceName = Path.GetFileName(sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                importedFiles = importedFileCount,
                importedHouseholds = importedHouseCount,
                importedNeighborhoods = userDataSources.Length
            };
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    internal static void ValidateProfileId(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) || profileId.Length > 64 ||
            profileId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("The profile ID may contain only ASCII letters, digits, hyphens and underscores.", nameof(profileId));
    }

    private static UserDataSource[] FindUserDataDirectories(string root)
    {
        if (!Directory.Exists(root)) return [];
        var sources = Directory.EnumerateDirectories(root)
            .Where(path => IsUserDataDirectoryName(Path.GetFileName(path)) &&
                           Directory.Exists(path))
            .Select(path => (Index: UserDataDirectoryIndex(Path.GetFileName(path)), Path: path))
            .OrderBy(source => source.Index)
            .ToArray();
        if (sources.Select(source => source.Index).Distinct().Count() != sources.Length)
            throw new InvalidDataException("The selected Sims installation has duplicate UserData directories.");
        return sources.Select(source => new UserDataSource(
            source.Index == 1 ? "UserData" : $"UserData{source.Index}", source.Path)).ToArray();
    }

    private static bool IsUserDataDirectoryName(string name)
    {
        const string prefix = "UserData";
        if (name.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = name.AsSpan(prefix.Length);
        return !suffix.IsEmpty && suffix.ToString().All(char.IsAsciiDigit) &&
               int.TryParse(suffix, out var index) && index is >= 1 and <= Neighborhoods.Maximum;
    }

    private static int UserDataDirectoryIndex(string name)
    {
        if (name.Equals("UserData", StringComparison.OrdinalIgnoreCase)) return 1;
        return int.TryParse(name.AsSpan("UserData".Length), out var index) ? index : int.MaxValue;
    }

    private static void ValidateOriginalIFF(string path)
    {
        try
        {
            var iff = IffFile.Open(path);
            if (iff.Chunks.Count == 0) throw new InvalidDataException("The resource contains no IFF chunks.");
        }
        catch (Exception error) { throw new InvalidDataException($"The selected original save contains an invalid IFF file: {Path.GetFileName(path)}.", error); }
    }

    private static void MergeNeighborhoodTemplates(string gameDirectory, string userData)
    {
        foreach (var template in TemplateDirectories)
        {
            var templateDirectory = Path.Combine(gameDirectory, template);
            if (!Directory.Exists(templateDirectory)) continue;
            foreach (var file in Directory.EnumerateFiles(templateDirectory, "*.iff"))
            {
                var filename = Path.GetFileName(file);
                var target = Path.Combine(userData, NeighborhoodFiles.Contains(filename) ? "" : "Houses", filename);
                if (!File.Exists(target)) File.Copy(file, target, false);
                else if (filename == "LotLocations.iff") MergeMissingStringChunks(target, file);
            }
        }
        UseLongestStreetNames(gameDirectory, userData);
        UpgradeBaseNeighborhoodLayout(gameDirectory, userData);
    }

    private static void OfferUnleashedFamilies(string gameDirectory, string userData)
    {
        var templates = Path.Combine(gameDirectory, "TemplateFamilyUnleashed");
        if (!Directory.Exists(Path.Combine(gameDirectory, "ExpansionPack5")) || !Directory.Exists(templates)) return;
        static int House(IffChunk family) => family.Data.Length >= 16
            ? BinaryPrimitives.ReadInt32LittleEndian(family.Data.Span[12..]) : 0;
        var neighborhood = Path.Combine(userData, "Neighborhood.iff");
        var occupied = File.Exists(neighborhood)
            ? IffFile.Open(neighborhood).OfType(ResourceType.Parse("FAMI")).Select(House).Where(house => house > 0).ToHashSet()
            : [];
        var import = Path.Combine(userData, "Import");
        foreach (var file in Directory.EnumerateFiles(templates, "*.FAM", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }))
        {
            try
            {
                var family = IffFile.Open(file).OfType(ResourceType.Parse("FAMI")).FirstOrDefault();
                if (family == null || House(family) <= 0 || occupied.Contains(House(family))) continue;
                Directory.CreateDirectory(import);
                var target = Path.Combine(import, Path.GetFileName(file));
                if (!File.Exists(target)) File.Copy(file, target, false);
            }
            catch (Exception error) when (error is InvalidDataException or IOException or EndOfStreamException or ArgumentException)
            {
                Console.Error.WriteLine($"SimsSimulation: skipped {Path.GetFileName(file)}: {error.Message}");
            }
        }
    }

    internal static object Check()
    {
        var root = Path.Combine(Path.GetTempPath(), "sims-profile-bootstrap-check-" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "game");
        var profile = Path.Combine(root, "profile");
        try
        {
            Write(game, "UserData/Houses/House01.iff", "original household");
            Write(game, "UserData/Houses/House51.iff", "player neighborhood override");
            var originalLocations = MakeIFF((5, "player route override"));
            WriteBytes(game, "UserData/LotLocations.iff", originalLocations);
            WriteBytes(game, "UserData2/Neighborhood.iff", MakeIFF((1, "second neighborhood")));
            WriteBytes(game, "UserData2/Houses/House01.iff", MakeIFF((1, "second neighborhood household")));
            var communityLocations = MakeIFF((1, "base neighborhood"), (2, "downtown"));
            var studioLocations = MakeIFF((5, "studio town"), (6, "unused mode"));
            var magicLocations = MakeIFF((5, "template conflict"), (7, "magic town"));
            WriteBytes(game, "TemplateCommunity/LotLocations.iff", communityLocations);
            WriteBytes(game, "TemplateStudiotown/LotLocations.iff", studioLocations);
            WriteBytes(game, "TemplateMagicTown/LotLocations.iff", magicLocations);
            Write(game, "TemplateCommunity/House52.iff", "community lot template");
            Write(game, "TemplateVacation/House70.iff", "vacation template");
            Write(game, "TemplateMagicTown/StreetNames.iff", "magic street names");
            Write(profile, "UserData.importing-interrupted/Houses/partial.iff", "partial");

            var imported = EnsureImported(game, profile);
            Require(imported, "A new profile was not imported");
            Require(Read(profile, "UserData/Houses/House01.iff") == "original household", "Original household data was not copied");
            Require(Read(profile, "UserData/Houses/House51.iff") == "player neighborhood override", "A player neighborhood file was replaced by a template");
            Require(Read(profile, "UserData/Houses/House52.iff") == "community lot template", "A community template was not merged");
            Require(File.ReadAllBytes(Path.Combine(profile, "UserData2", "Houses", "House01.iff")).SequenceEqual(MakeIFF((1, "second neighborhood household"))), "A numbered neighborhood was not copied into its profile directory");
            var importedLocations = File.ReadAllBytes(Path.Combine(profile, "UserData/LotLocations.iff"));
            var locationChunks = ReadIFFChunks(importedLocations).Where(chunk => chunk.Type == "STR#").ToArray();
            Require(locationChunks.Select(chunk => chunk.Id).Order().SequenceEqual(new ushort[] { 1, 2, 5, 6, 7 }), "Expansion neighborhood routes were not merged into LotLocations.iff");
            Require(locationChunks.Single(chunk => chunk.Id == 5).Bytes.SequenceEqual(ReadIFFChunks(originalLocations).Single(chunk => chunk.Id == 5).Bytes), "A player route was replaced by a template route");
            Require(locationChunks.Single(chunk => chunk.Id == 7).Bytes.SequenceEqual(ReadIFFChunks(magicLocations).Single(chunk => chunk.Id == 7).Bytes), "The Magic Town route was not merged");
            Require(Read(profile, "UserData/Houses/House70.iff") == "vacation template", "A vacation template was not merged");
            Require(Read(profile, "UserData/StreetNames.iff") == "magic street names", "A neighborhood metadata file was not merged");
            Require(Read(game, "UserData/Houses/House01.iff") == "original household", "The import changed the original installation");
            Require(File.ReadAllBytes(Path.Combine(game, "TemplateStudiotown/LotLocations.iff")).SequenceEqual(studioLocations), "The import changed the Studio Town source template");
            Require(!Directory.EnumerateDirectories(profile, "UserData*.importing-*", SearchOption.TopDirectoryOnly).Any(path => !Path.GetFileName(path).EndsWith("interrupted", StringComparison.Ordinal)), "A completed staging directory was left behind");

            Write(profile, "UserData/Houses/House01.iff", "profile progress");
            Require(!EnsureImported(game, profile), "An existing profile was imported a second time");
            Require(Read(profile, "UserData/Houses/House01.iff") == "profile progress", "Repeat startup overwrote profile progress");

            var legacyProfile = Path.Combine(root, "legacy-profile");
            WriteBytes(legacyProfile, "UserData/LotLocations.iff", MakeIFF((1, "legacy base route")));
            Require(EnsureImported(game, legacyProfile), "An existing profile did not receive its missing numbered neighborhood");
            Require(File.ReadAllBytes(Path.Combine(legacyProfile, "UserData2", "Houses", "House01.iff")).SequenceEqual(MakeIFF((1, "second neighborhood household"))), "A missing numbered neighborhood was not added to an existing profile");
            var legacyRouteChunks = ReadIFFChunks(File.ReadAllBytes(Path.Combine(legacyProfile, "UserData/LotLocations.iff"))).Where(chunk => chunk.Type == "STR#").ToArray();
            Require(legacyRouteChunks.Select(chunk => chunk.Id).Order().SequenceEqual(new ushort[] { 1, 2, 5, 6, 7 }), "An existing profile did not receive its missing expansion routes");
            Require(legacyRouteChunks.Single(chunk => chunk.Id == 1).Bytes.SequenceEqual(ReadIFFChunks(MakeIFF((1, "legacy base route"))).Single(chunk => chunk.Id == 1).Bytes), "Refreshing routes replaced an existing profile route");

            var originalSave = Path.Combine(root, "original-save");
            var sourceNeighborhood = MakeIFF((1, "neighborhood state"));
            var sourceHouse = MakeIFF((1, "house lot"));
            var sourceExpansionNeighborhood = MakeIFF((1, "second neighborhood state"));
            var sourceExpansionHouse = MakeIFF((1, "second neighborhood lot"));
            WriteBytes(originalSave, "UserData/Neighborhood.iff", sourceNeighborhood);
            WriteBytes(originalSave, "UserData/Houses/House05.iff", sourceHouse);
            Write(originalSave, "UserData/Options.iff", "player options");
            var originalSaveRoutes = MakeIFF((1, "saved base route"), (5, "saved studio route"));
            WriteBytes(originalSave, "UserData/LotLocations.iff", originalSaveRoutes);
            WriteBytes(originalSave, "UserData2/Neighborhood.iff", sourceExpansionNeighborhood);
            WriteBytes(originalSave, "UserData2/Houses/House05.iff", sourceExpansionHouse);
            var sourceSnapshot = Directory.EnumerateFiles(originalSave, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(originalSave, path), File.ReadAllBytes);
            var importResult = ImportOriginalUserData(game, root, originalSave, "imported-save");
            var importedProfile = Path.Combine(root, "runtime", "profiles", "imported-save");
            Require(File.ReadAllBytes(Path.Combine(importedProfile, "UserData/Houses/House05.iff")).SequenceEqual(sourceHouse), "Original save household data was not imported");
            Require(File.ReadAllBytes(Path.Combine(importedProfile, "UserData2/Houses/House05.iff")).SequenceEqual(sourceExpansionHouse), "A numbered original neighborhood was not imported");
            Require(File.ReadAllBytes(Path.Combine(importedProfile, "UserData/Neighborhood.iff")).SequenceEqual(sourceNeighborhood), "Original save neighborhood state was not imported");
            Require(Read(importedProfile, "UserData/Options.iff") == "player options", "Original save options were not imported");
            Require(ReadIFFChunks(File.ReadAllBytes(Path.Combine(importedProfile, "UserData/LotLocations.iff"))).Where(chunk => chunk.Type == "STR#").Select(chunk => chunk.Id).Order().SequenceEqual(new ushort[] { 1, 2, 5, 6, 7 }), "Original save route overrides were not retained while expansion routes were merged");
            Require(sourceSnapshot.All(pair => File.ReadAllBytes(Path.Combine(originalSave, pair.Key)).SequenceEqual(pair.Value)), "Importing an original save changed source files");
            Require(!Directory.EnumerateDirectories(Path.Combine(root, "runtime", "profiles"), "*.importing-*", SearchOption.TopDirectoryOnly).Any(), "A failed or completed original-save staging directory was left behind");
            var duplicateImportRejected = false;
            try { ImportOriginalUserData(game, root, Path.Combine(originalSave, "UserData"), "imported-save"); }
            catch (IOException) { duplicateImportRejected = true; }
            Require(duplicateImportRejected && File.ReadAllBytes(Path.Combine(importedProfile, "UserData/Houses/House05.iff")).SequenceEqual(sourceHouse), "A duplicate import overwrote an existing profile");
            var userDataImportResult = ImportOriginalUserData(game, root, Path.Combine(originalSave, "UserData"), "imported-user-data");
            var secondImportedHousehold = File.ReadAllBytes(Path.Combine(root, "runtime", "profiles", "imported-user-data", "UserData/Houses/House05.iff"));
            var secondExpansionHousehold = File.ReadAllBytes(Path.Combine(root, "runtime", "profiles", "imported-user-data", "UserData2/Houses/House05.iff"));
            Require(secondImportedHousehold.SequenceEqual(sourceHouse) && secondExpansionHousehold.SequenceEqual(sourceExpansionHouse) &&
                    JsonSerializer.Serialize(importResult).Contains("imported-save", StringComparison.Ordinal) &&
                    JsonSerializer.Serialize(userDataImportResult).Contains("imported-user-data", StringComparison.Ordinal),
                "The importer did not accept a game root and UserData folder with sibling neighborhood data");
            var numberedBase = Path.Combine(root, "numbered-base-save");
            WriteBytes(numberedBase, "UserData1/Neighborhood.iff", sourceNeighborhood);
            WriteBytes(numberedBase, "UserData1/Houses/House05.iff", sourceHouse);
            ImportOriginalUserData(game, root, numberedBase, "imported-numbered-base");
            Require(File.ReadAllBytes(Path.Combine(root, "runtime", "profiles", "imported-numbered-base", "UserData/Neighborhood.iff"))
                        .SequenceEqual(sourceNeighborhood),
                "The original numeric UserData1 alias was not imported as neighborhood 1");
            var invalidSource = Path.Combine(root, "invalid-original-save");
            Write(invalidSource, "Neighborhood.iff", "not an IFF resource");
            WriteBytes(invalidSource, "Houses/House01.iff", MakeIFF((1, "house lot")));
            var malformedImportRejected = false;
            try { ImportOriginalUserData(game, root, invalidSource, "malformed-save"); }
            catch (InvalidDataException) { malformedImportRejected = true; }
            Require(malformedImportRejected && !Directory.Exists(Path.Combine(root, "runtime", "profiles", "malformed-save")), "The importer accepted malformed original save data");
            var unsafeProfileIDRejected = false;
            try { ImportOriginalUserData(game, root, originalSave, "../escape"); }
            catch (ArgumentException) { unsafeProfileIDRejected = true; }
            Require(unsafeProfileIDRejected && !Directory.Exists(Path.Combine(root, "escape")), "An unsafe profile ID escaped the profile directory");
            return new { result = "passed", checks = 31, sourcePreserved = true, originalSaveImport = true, numberedNeighborhoodsImported = true, duplicateImportProtected = true, malformedSaveRejected = true, unsafeProfileIDRejected = true, expansionRoutesMerged = true, legacyProfileRepaired = true, stagingRecovery = true, repeatLaunchPreservesProfile = true };
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        static void Write(string root, string relativePath, string value)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value);
        }

        static void WriteBytes(string root, string relativePath, byte[] value)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, value);
        }

        static string Read(string root, string relativePath) => File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        static byte[] MakeIFF(params (ushort Id, string Value)[] chunks)
        {
            using var output = new MemoryStream();
            var header = new byte[64];
            var signature = Encoding.ASCII.GetBytes("IFF FILE 2.5:TYPE FOLLOWED BY SIZE JAMIE DOORNBOS & MAXIS 1");
            signature.AsSpan(0, Math.Min(60, signature.Length)).CopyTo(header);
            output.Write(header);
            foreach (var chunk in chunks)
            {
                var payload = new byte[] { 0xFD, 0xFF, 0, 0 };
                var record = new byte[76 + payload.Length];
                Encoding.ASCII.GetBytes("STR#").CopyTo(record, 0);
                BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(4, 4), (uint)record.Length);
                BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(8, 2), chunk.Id);
                BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(10, 2), 0x10);
                Encoding.ASCII.GetBytes(chunk.Value).CopyTo(record, 12);
                payload.CopyTo(record, 76);
                output.Write(record);
            }
            return output.ToArray();
        }

        static List<ResourceChunk> ReadIFFChunks(byte[] data)
        {
            if (data.Length < 64 || !Encoding.ASCII.GetString(data, 0, 60).StartsWith("IFF FILE 2.", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid IFF resource in profile import");
            var result = new List<ResourceChunk>();
            var offset = 64;
            while (offset < data.Length)
            {
                if (data.Length - offset < 76) throw new InvalidDataException("Truncated IFF resource chunk");
                var size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4, 4));
                if (size < 76 || size > data.Length - offset) throw new InvalidDataException("Invalid IFF resource chunk size");
                var type = Encoding.ASCII.GetString(data, offset, 4);
                var id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 8, 2));
                if (type == "STR#") result.Add(new ResourceChunk(type, id, data.AsSpan(offset, (int)size).ToArray()));
                offset += (int)size;
            }
            return result;
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
    }

    private static void MergeMissingStringChunks(string destination, string source)
    {
        var destinationBytes = File.ReadAllBytes(destination);
        var existing = ReadResourceStringChunks(destinationBytes, out _);
        var additions = ReadResourceStringChunks(File.ReadAllBytes(source)).Where(chunk => existing.Add(chunk.Id)).ToArray();
        if (additions.Length == 0) return;
        var temporary = destination + ".merge-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(destinationBytes);
                foreach (var chunk in additions) output.Write(chunk.Bytes);
                output.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void EnsureNeighborhoodRoutes(string gameDirectory, string userData)
    {
        var destination = Path.Combine(userData, "LotLocations.iff");
        foreach (var template in TemplateDirectories)
        {
            var source = Path.Combine(gameDirectory, template, "LotLocations.iff");
            if (!File.Exists(source)) continue;
            if (!File.Exists(destination)) File.Copy(source, destination, false);
            else MergeMissingStringChunks(destination, source);
        }
        UseLongestStreetNames(gameDirectory, userData);
        UpgradeBaseNeighborhoodLayout(gameDirectory, userData);
    }

    /// <summary>Selects the StreetNames.iff template with the longest lot table (STR# 2001), ensuring all
    /// expansion neighborhood lots and street names are preserved.</summary>
    private static void UseLongestStreetNames(string gameDirectory, string userData)
    {
        var destination = Path.Combine(userData, "StreetNames.iff");
        static int LotTableLength(string path)
        {
            try { return ReadStringChunk(path, 2001)?.Bytes.Length ?? 0; }
            catch (InvalidDataException) { return 0; }
        }
        var currentLength = LotTableLength(destination);
        string? best = null;
        foreach (var template in TemplateDirectories)
        {
            var source = Path.Combine(gameDirectory, template, "StreetNames.iff");
            var length = LotTableLength(source);
            if (length > currentLength) { best = source; currentLength = length; }
        }
        if (best != null) File.Copy(best, destination, true);
    }

    private static void UpgradeBaseNeighborhoodLayout(string gameDirectory, string userData)
    {
        var destination = Path.Combine(userData, "LotLocations.iff");
        var baseline = ReadStringChunk(Path.Combine(gameDirectory, "GameData", "LotLocations.iff"), 1);
        var current = ReadStringChunk(destination, 1);
        if (baseline == null || current == null || !current.Value.Bytes.SequenceEqual(baseline.Value.Bytes)) return;

        ResourceChunk? replacement = null;
        foreach (var source in new[] { "UserData/LotLocations.iff", "TemplateUserData/LotLocations.iff" })
        {
            var candidate = ReadStringChunk(Path.Combine(gameDirectory, source), 1);
            if (candidate != null && !candidate.Value.Bytes.SequenceEqual(baseline.Value.Bytes))
            {
                replacement = candidate;
                break;
            }
        }
        if (replacement == null) return;

        var original = File.ReadAllBytes(destination);
        var temporary = destination + ".layout-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(original, 0, 64);
                var offset = 64;
                var replaced = false;
                while (offset < original.Length)
                {
                    if (original.Length - offset < 76) throw new InvalidDataException("Truncated neighborhood resource while updating its base lot layout");
                    var size = BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(offset + 4, 4));
                    if (size < 76 || size > original.Length - offset) throw new InvalidDataException("Invalid neighborhood resource while updating its base lot layout");
                    var isBaseRoute = Encoding.ASCII.GetString(original, offset, 4) == "STR#" &&
                        BinaryPrimitives.ReadUInt16BigEndian(original.AsSpan(offset + 8, 2)) == 1;
                    if (isBaseRoute && !replaced)
                    {
                        output.Write(replacement.Value.Bytes);
                        replaced = true;
                    }
                    else output.Write(original, offset, (int)size);
                    offset += (int)size;
                }
                if (!replaced) throw new InvalidDataException("Neighborhood resource has no base lot layout to update");
                output.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static ResourceChunk? ReadStringChunk(string path, ushort id)
    {
        if (!File.Exists(path)) return null;
        var chunks = ReadResourceStringChunks(File.ReadAllBytes(path));
        var index = chunks.FindIndex(chunk => chunk.Id == id);
        return index < 0 ? null : chunks[index];
    }

    private static HashSet<ushort> ReadResourceStringChunks(byte[] data, out List<ResourceChunk> chunks)
    {
        if (data.Length < 64 || !Encoding.ASCII.GetString(data, 0, 60).StartsWith("IFF FILE 2.", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid IFF resource while merging neighborhood routes");
        var identifiers = new HashSet<ushort>();
        chunks = new List<ResourceChunk>();
        var offset = 64;
        while (offset < data.Length)
        {
            if (data.Length - offset < 76) throw new InvalidDataException("Truncated IFF resource while merging neighborhood routes");
            var size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4, 4));
            if (size < 76 || size > data.Length - offset) throw new InvalidDataException("Invalid IFF resource chunk while merging neighborhood routes");
            if (Encoding.ASCII.GetString(data, offset, 4) == "STR#")
            {
                var id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 8, 2));
                identifiers.Add(id);
                chunks.Add(new ResourceChunk("STR#", id, data.AsSpan(offset, (int)size).ToArray()));
            }
            offset += (int)size;
        }
        return identifiers;
    }

    private static List<ResourceChunk> ReadResourceStringChunks(byte[] data)
    {
        ReadResourceStringChunks(data, out var chunks);
        return chunks;
    }
}
