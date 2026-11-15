using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsNative.Plugins;
using SimsSimulation;

internal static class NeighborhoodExport
{
    private sealed record AnimationLayer(float PositionX, float PositionY, string[] FrameResources);
    private sealed record Mode(string BackgroundResource, float Scale, bool Pulsate, int FrameDuration,
        string? Music, string? AmbientSound, AnimationLayer[] AnimationLayers);
    private sealed record ScrapbookPhoto(string Image, string? Thumbnail, string Caption);
    private sealed record HouseRenderScenes(string Thumbnail, string Exterior, string Floor1, string? Floor2);
    internal sealed record MemberProfile(string Name, string? Gender, string? Species, string? Age,
        int? Neat, int? Outgoing, int? Active,
        int? Playful, int? Nice, int? Cooking, int? Mechanical, int? Charisma, int? Body, int? Logic, int? Creativity,
        string? Career, string? Job, int? Performance, int? Salary, int? Zodiac, string? Description, string? PerformanceLabel = null);
    private sealed record Entry(int Ordinal, int House, int X, int Y, string ClosedImagePath, string OpenImagePath,
        int ClosedImageWidth, int ClosedImageHeight, int OpenImageWidth, int OpenImageHeight,
        int ThumbWidth, int ThumbHeight, int XOff, int BaseYOff, int AddYOff, string ClosedImageSource,
        string OpenImageSource, string OffsetsSource, bool Occupied, bool Residential, bool AvailableForCreation,
        int Price, bool Community, int FilterFlags, string? HouseName, string? HouseDescription, int? Street, string? StreetTemplate,
        string? FamilyName, int? FamilyNetWorth, int? FamilyCashBalance, int? FamilyFriends, int? FamilyDaysExist, bool Bulldozed, string?[] MemberImages,
        string[] MemberNames, MemberProfile[] MemberProfiles, int? HouseSquareFeet, int? HouseBathrooms,
        int? HouseBedrooms, int? HouseFloors, int? HouseValue, int? HouseSizeRating,
        int? HouseFurnishingsRating, int? HouseYardRating, int? HouseUpkeepRating, int? HouseLayoutRating,
        ScrapbookPhoto[] ScrapbookPhotos, HouseRenderScenes? RenderScenes, bool OnVacation,
        bool TutorialHouse, bool LotHasHouse, string? PopupName, string? PopupDescription);

    private sealed record HouseRoomStats(int SquareFeet, int Bathrooms, int Bedrooms, int Floors, int SizeRating,
        int? FurnishingsRating, int? YardRating, int? UpkeepRating, int? LayoutRating, int? DaysRunning);

    /// <summary>The installed game folder, set by the host so the export can tell which packs are installed.</summary>
    internal static string? GameRoot { get; set; }

    private static readonly IReadOnlyDictionary<int, Mode> Modes = new Dictionary<int, Mode>
    {
        [1] = new("UIGraphics/Nbhd/NScreen.BMP", 1f, true, 15, "bkground_nhood1", null,
            [Layer("Nbhd/DiffN1-N2_8.bmp", "Nbhd/DiffN1-N3_8.bmp", "Nbhd/DiffN1-N4_8.bmp")]),
        [2] = new("UIGraphics/Downtown/DScreen.bmp", 1f, true, 15, "station_dtnhood", "setambience_downtown",
            [Layer("Downtown/dscreen00.bmp", "Downtown/dscreen01.bmp", "Downtown/dscreen02.bmp", "Downtown/dscreen03.bmp", "Downtown/dscreen04.bmp")]),
        [3] = new("UIGraphics/VIsland/visland.bmp", 1f, true, 15, "station_vacation", "setambience_beach",
            [Layer("VIsland/visland_waves001.bmp", "VIsland/visland_waves002.bmp", "VIsland/visland_waves003.bmp", "VIsland/visland_waves004.bmp", "VIsland/visland_waves005.bmp"),
             Layer("VIsland/visland_port.bmp"), Layer("VIsland/visland_trees.bmp")]),
        [4] = new("UIGraphics/Community/NScreen_unleashed.bmp", 2f, false, 15, "station_unleashed", "river_loop",
            [Layer("Community/NScreen_unleashed_waves001.bmp", "Community/NScreen_unleashed_waves002.bmp", "Community/NScreen_unleashed_waves003.bmp", "Community/NScreen_unleashed_waves004.bmp", "Community/NScreen_unleashed_waves005.bmp", "Community/NScreen_unleashed_waves006.bmp")]),
        [5] = new("UIGraphics/Studiotown/DScreen.bmp", 1f, true, 15, "station_superstar", null,
            [Layer("Studiotown/DScreen_top_layer.bmp")]),
        [7] = new("UIGraphics/Magicland/DScreen.bmp", 1f, false, 15, "music_magictown", "mt_river_loop",
            [LayerAt(0, 62, "Magicland/DScreen_waves1.bmp", "Magicland/DScreen_waves2.bmp", "Magicland/DScreen_waves3.bmp", "Magicland/DScreen_waves4.bmp", "Magicland/DScreen_waves5.bmp", "Magicland/DScreen_waves6.bmp")])
    };

    private static readonly Mode UnleashedMain = Modes[4] with { Music = "bkground_nhood1" };

    internal static bool UnleashedInstalled => GameRoot != null && Directory.Exists(Path.Combine(GameRoot, "ExpansionPack5"));

    private static Mode Configure(int mode, out string variant)
    {
        if (!Modes.TryGetValue(mode, out var config)) throw new ArgumentOutOfRangeException(nameof(mode), "Unsupported neighborhood mode");
        variant = "base";
        if (mode == 1 && UnleashedInstalled)
        {
            variant = "unleashed";
            return UnleashedMain;
        }
        return config;
    }

    private static AnimationLayer Layer(params string[] frames) => LayerAt(0, 0, frames);
    private static AnimationLayer LayerAt(float x, float y, params string[] frames) =>
        new(x, y, frames.Select(frame => "UIGraphics/" + frame).ToArray());

    internal static int? ComputeExportZodiac(IReadOnlyList<short>? data) =>
        Neighborhoods.ComputeExportZodiac(data);

    private static (int First, int Last)? TownRange(int mode) => mode switch
    {
        2 => (21, 30),
        3 => (40, 49),
        5 => (81, 89),
        7 => (90, 99),
        _ => null
    };

    internal static string? ResolveUserFile(string userPath, string? gameRoot, string relativePath)
    {
        var direct = Path.Combine(userPath, relativePath);
        if (File.Exists(direct)) return direct;
        var nested = Path.Combine(userPath, "UserData", relativePath);
        if (File.Exists(nested)) return nested;
        if (gameRoot is not null)
        {
            var gameUser = Path.Combine(gameRoot, "UserData", relativePath);
            if (File.Exists(gameUser)) return gameUser;
            var templateUser = Path.Combine(gameRoot, "TemplateUserData", "UserData", relativePath);
            if (File.Exists(templateUser)) return templateUser;
        }
        return null;
    }

    internal static string? ResolveHouseDescriptionFile(string userPath, string? gameRoot, int house)
    {
        var primary = house switch
        {
            >= 21 and <= 31 => "DTDesc.iff",
            >= 40 and <= 49 => "VIDesc.iff",
            >= 81 and <= 89 => "STDesc.iff",
            >= 90 and <= 99 => "MTDesc.iff",
            _ => "Desc.iff"
        };
        var candidates = primary == "Desc.iff"
            ? new[] { "NeighborhoodDesc.iff", "Desc.iff" }
            : new[] { primary, "NeighborhoodDesc.iff", "Desc.iff" };
        foreach (var file in candidates)
        {
            foreach (var rel in new[] { Path.Combine("Houses", file), file })
            {
                if (ResolveUserFile(userPath, gameRoot, rel) is { } resolved)
                {
                    try
                    {
                        var iff = IffFile.Open(resolved);
                        if (iff.Find(ResourceType.Str, checked((ushort)(house + 2000))) is not null)
                            return resolved;
                    }
                    catch { }
                }
            }
        }
        return ResolveUserFile(userPath, gameRoot, Path.Combine("Houses", primary))
            ?? ResolveUserFile(userPath, gameRoot, Path.Combine("Houses", "NeighborhoodDesc.iff"))
            ?? ResolveUserFile(userPath, gameRoot, Path.Combine("Houses", "Desc.iff"));
    }

    internal static IReadOnlyList<string>? ReadHouseDescriptionStrings(string userPath, string? gameRoot, int house, byte language)
    {
        if (ResolveHouseDescriptionFile(userPath, gameRoot, house) is not { } descPath || !File.Exists(descPath))
            return null;
        try
        {
            var iff = IffFile.Open(descPath);
            if (iff.Find(ResourceType.Str, checked((ushort)(house + 2000))) is { } chunk)
                return StringTable.Read(iff, chunk).ForLanguage(language);
        }
        catch { }
        return null;
    }

    internal static Dictionary<short, short> ReadZoningDictionary(string userPath, string? gameRoot)
    {
        var result = new Dictionary<short, short>();
        var path = ResolveUserFile(userPath, gameRoot, "LotZoning.iff");
        if (path is null || !File.Exists(path)) return result;
        var file = IffFile.Open(path);
        if (file.Find(ResourceType.Str, 1) is not { } chunk) return result;
        var entries = StringTable.Read(file, chunk).ForLanguage(1);
        for (var i = 0; i < entries.Count; i++)
        {
            var parts = entries[i].Split(',', 2);
            if (parts.Length != 2 || !short.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var house))
                continue;
            var zoning = parts[1].Trim();
            if (zoning.Equals("community", StringComparison.OrdinalIgnoreCase))
                result[house] = 1;
            else if (zoning.Equals("residential", StringComparison.OrdinalIgnoreCase))
                result[house] = 0;
        }
        return result;
    }

    private static int HouseSizeScore(int squareFeet, int householdSize)
    {
        if (householdSize <= 0) return 0;
        var areaPerMember = (double)squareFeet / householdSize;
        if (areaPerMember <= 0) return 0;
        if (areaPerMember >= 1200) return 100;
        var (start, end, baseScore) = areaPerMember switch
        {
            < 150 => (0d, 150d, 0d),
            < 300 => (150d, 300d, 25d),
            < 600 => (300d, 600d, 50d),
            _ => (600d, 1200d, 75d),
        };
        return (int)Math.Round(baseScore + (areaPerMember - start) / (end - start) * 25d, MidpointRounding.ToEven);
    }

    private static int? HouseFurnishingsScore(SimulationState? saved, IffFile? global, byte language)
    {
        if (saved is not { Version: >= 0x25 } || global?.Find(ResourceType.Str, 505) is not { } chunk)
            return null;
        var curves = StringTable.Read(global, chunk).ForLanguage(language);
        if (curves.Count < 2) return null;
        var points = new List<(float X, float Y)>();
        foreach (var token in curves[1].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = token.Trim('(', ')').Split(';');
            if (pair.Length != 2 ||
                !float.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                return null;
            points.Add((x, y));
        }
        points.Sort((left, right) => left.X.CompareTo(right.X));
        if (points.Count == 0) return null;
        var ratio = saved.Field0xA0 == 0 ? 100f : (float)saved.Field0xA4 / saved.Field0xA0;
        if (ratio < points[0].X) return (int)Math.Round(Math.Clamp(points[0].Y, 0f, 100f), MidpointRounding.ToEven);
        if (ratio >= points[^1].X) return (int)Math.Round(Math.Clamp(points[^1].Y, 0f, 100f), MidpointRounding.ToEven);
        for (var index = points.Count - 2; index >= 0; index--)
        {
            var start = points[index];
            var end = points[index + 1];
            if (ratio < start.X) continue;
            var fraction = (ratio - start.X) / (end.X - start.X);
            var curve = (1 - fraction) * start.Y + fraction * end.Y;
            return (int)Math.Round(Math.Clamp(curve, 0f, 100f), MidpointRounding.ToEven);
        }
        return (int)Math.Round(Math.Clamp(points[0].Y, 0f, 100f), MidpointRounding.ToEven);
    }

    private static HouseRoomStats? ReadHouseRoomStats(IffFile lot, SimulationWorld world, GlobalLibrary library,
        int familyMembers, byte language)
    {
        var squareFeet = 0;
        var bathrooms = 0;
        var bedrooms = 0;
        foreach (var roomMap in world.LotRoomMaps.Values)
        foreach (var roomId in roomMap.RoomIds)
        {
            if (roomMap.IsOutside(roomId)) continue;
            squareFeet += roomMap.RoomHalfArea(roomId, roomBranchFlag: false) * 9;
            if (!world.PersonRoomMotiveEntries.TryGetValue(roomId, out var room)) continue;
            if (room.HouseCategory5To7ObjectCount5c > 0) bathrooms++;
            if (room.HouseCategory4ObjectCount58 > 0) bedrooms++;
        }
        var sizeRating = HouseSizeScore(squareFeet, familyMembers);
        var furnishingsRating = HouseFurnishingsScore(world.SavedSimulationState, library.Global, language);
        int? yardRating = null;
        if (world.PersonRoomMotiveEntries.Values.All(room => !room.NeedsRefresh) &&
            world.LotRoomMaps.Count != 0 && world.LotRoomMaps.Values.All(map => map.HasAuthoritativeOutsideRooms))
        {
            var rawYard = world.PersonRoomMotiveEntries.Values.Where(room => room.IsOutside)
                .Sum(room => room.InteriorValue) * world.HouseYardScoreMultiplier;
            if (float.IsFinite(rawYard)) yardRating = (int)Math.Clamp(rawYard, 0f, 100f);
        }

        var upkeepCount = 0;
        var upkeepPenalty = 0;
        foreach (var item in world.WalkOrder)
        {
            if (item.Definition[(int)ObjdField.RoomSort] == 0 &&
                item.Definition[(int)ObjdField.FunctionSort] == 0 &&
                item.Definition[(int)ObjdField.BuildModeType] == 0) continue;
            upkeepCount++;
            if (item[ObjectData.RepairState] != 0)
                upkeepPenalty += world.HouseUpkeepPenaltyPerBrokenObject;
            else if (item[ObjectData.DirtyLevel] > 0)
                upkeepPenalty += world.HouseUpkeepPenaltyPerDirtyObject;
        }
        int? upkeepRating = upkeepCount == 0 ? 0 :
            (int)(Math.Clamp((upkeepCount - upkeepPenalty) / (float)upkeepCount, 0f, 1f) * 100f);
        var layoutRating = HouseLayoutHistory.Read(lot, world.HouseLayoutHistoryFillValue)?.Score;
        int? daysRunning = world.SavedSimulationState is { } simulation && simulation.Globals.Count > (int)SimulationGlobal.DaysRunning
            ? simulation.Globals[(int)SimulationGlobal.DaysRunning]
            : null;
        return new HouseRoomStats(squareFeet, bathrooms, bedrooms, world.LotRoomMaps.Count, sizeRating,
            furnishingsRating, yardRating, upkeepRating, layoutRating, daysRunning);
    }

    private static HouseRenderScenes? ExportHouseRenderScenes(IffFile lot, SimulationWorld world, ObjectRegistry registry,
        string gameRoot, string session, short house)
    {
        var relative = Path.Combine("neighborhood", $"house{house:D2}-web-scenes");
        var directory = Path.Combine(session, relative);
        Directory.CreateDirectory(directory);
        string Scene(string name, int level, bool roof) =>
            SceneExporter.Export(lot, world, registry, gameRoot, Path.Combine(directory, name), house,
                revision: 0, viewLevel: level, wallMode: "down", cameraOrientation: 0, roofsVisible: roof);
        var stories = SceneExporter.Stories(lot);
        var thumbnail = Scene("thumbnail", stories, true);
        var exterior = Scene("exterior", stories, false);
        var floor1 = Scene("floor1", 1, false);
        var floor2 = stories > 1 ? Scene("floor2", 2, false) : null;
        return new HouseRenderScenes(Path.GetRelativePath(session, thumbnail), Path.GetRelativePath(session, exterior),
            Path.GetRelativePath(session, floor1), floor2 == null ? null : Path.GetRelativePath(session, floor2));
    }

    private static MemberProfile MakeMemberProfile(string name, IReadOnlyList<short>? data, byte language,
        ObjectRegistry? registry, CareerCatalog? careerCatalog, string? gameRoot, uint guid)
    {
        static int? Rating(IReadOnlyList<short>? values, int index) =>
            values != null && index < values.Count ? values[index] / 100 : null;
        string? gender = data is { Count: > 65 } ? ((data[65] & 1) == 0 ? "Male" : "Female") : null;
        var speciesFlags = data is { Count: > 65 } ? data[65] : (short)0;
        string? species = data is { Count: > 65 }
            ? (speciesFlags & 2) != 0 ? "dog" : (speciesFlags & 4) != 0 ? "cat" : "human"
            : null;
        string? age = data is { Count: > 58 } ? (data[58] == 18 ? "Adult" : "Child") : null;
        string? career = null;
        string? job = null;
        int? salary = null;
        string? performanceLabel = null;
        var female = data is { Count: > 65 } && (data[65] & 1) != 0;
        if (data is { Count: > 57 } && data[56] > 0 && careerCatalog is not null && careerCatalog.CareerExists(data[56]))
        {
            var jobType = data[56];
            var maxLevels = careerCatalog.JobData(jobType, 0, 0) ?? 1;
            var level = (short)Math.Clamp((int)data[57], 0, Math.Max(0, maxLevels - 1));
            career = careerCatalog.TrackName(jobType);
            job = careerCatalog.JobText(jobType, level, JobTextKind.Title, female, language) ?? careerCatalog.JobTitle(jobType, level);
            salary = careerCatalog.JobData(jobType, level, 1);
        }
        else if (data is { Count: > 81 } && data[58] == 18 && data[80] > 0 && gameRoot is not null && HasSuperstarPack(gameRoot))
        {
            var famePath = Path.Combine(gameRoot, "GameData", "fame.iff");
            if (File.Exists(famePath))
            {
                var fameFile = new IffFile(File.ReadAllBytes(famePath), famePath);
                if (fameFile.Find(ResourceType.Str, 1) is { } fameTitles)
                {
                    var titles = StringTable.Read(fameFile, fameTitles).ForLanguage(language);
                    career = titles.Count > 11 ? titles[11] : "Superstar";
                    var fameLevel = Math.Clamp((int)data[81], 0, 10);
                    job = fameLevel < titles.Count ? titles[fameLevel] : "?";
                    salary = 0;
                    performanceLabel = careerCatalog?.GradeString(data[63], language) ?? "?";
                }
            }
        }
        var description = registry?.Find(guid)?.CatalogString(1, language);
        return new MemberProfile(name, gender, species, age,
            Rating(data, 7), Rating(data, 6), Rating(data, 3), Rating(data, 5), Rating(data, 2),
            Rating(data, 10), Rating(data, 12), Rating(data, 11), Rating(data, 17), Rating(data, 18), Rating(data, 15),
            career, job, data is { Count: > 63 } ? data[63] : null, salary, ComputeExportZodiac(data),
            string.IsNullOrEmpty(description) ? null : description, performanceLabel);
    }

    private static bool HasSuperstarPack(string gamePath)
    {
        var folder = Path.Combine(gamePath, "ExpansionPack6");
        return Directory.Exists(folder) || File.Exists(folder + ".far");
    }

    private static ScrapbookPhoto[] CopyScrapbookPhotos(string userPath, string familyName, int familyNumber,
        int house, string outputDirectory)
    {
        var albumRoot = Path.GetFullPath(Path.Combine(userPath, "PhotoAlbum"));
        var sourceDirectory = Path.GetFullPath(Path.Combine(albumRoot,
            familyName + familyNumber.ToString(CultureInfo.InvariantCulture)));
        if (!sourceDirectory.StartsWith(albumRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return [];
        if (!Directory.Exists(sourceDirectory)) return [];
        var captions = Directory.EnumerateFiles(sourceDirectory, "*.txt", SearchOption.TopDirectoryOnly)
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .Select(path => (Path: path, Number: TrailingNumber(Path.GetFileNameWithoutExtension(path))))
            .Where(item => item.Number >= 0)
            .GroupBy(item => item.Number).ToDictionary(group => group.Key, group => group.First().Path);
        var images = Directory.EnumerateFiles(sourceDirectory, "*.jpg", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith("_thumb", StringComparison.OrdinalIgnoreCase))
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .Select(path => (Path: path, Stem: Path.GetFileNameWithoutExtension(path), Number: TrailingNumber(Path.GetFileNameWithoutExtension(path))))
            .Where(item => item.Number >= 0 && captions.ContainsKey(item.Number))
            .OrderBy(item => item.Number).ThenBy(item => item.Stem, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = new List<ScrapbookPhoto>(images.Length);
        for (var index = 0; index < images.Length; index++)
        {
            var image = images[index];
            var thumbnailPath = Path.Combine(sourceDirectory, image.Stem + "_thumb.jpg");
            var captionPath = captions[image.Number];
            if (!File.Exists(captionPath) || (File.GetAttributes(captionPath) & FileAttributes.ReparsePoint) != 0) continue;
            var fileStem = $"house{house:D2}-scrapbook{index + 1}";
            var imageName = fileStem + ".jpg";
            var thumbName = fileStem + "_thumb.jpg";
            File.Copy(image.Path, Path.Combine(outputDirectory, imageName), true);
            string? thumbnail = null;
            if (File.Exists(thumbnailPath) && (File.GetAttributes(thumbnailPath) & FileAttributes.ReparsePoint) == 0)
            {
                File.Copy(thumbnailPath, Path.Combine(outputDirectory, thumbName), true);
                thumbnail = Path.Combine("neighborhood", thumbName);
            }
            var caption = TruncateScrapbookCaption(Encoding.Latin1.GetString(File.ReadAllBytes(captionPath)));
            result.Add(new ScrapbookPhoto(Path.Combine("neighborhood", imageName), thumbnail, caption));
        }
        return result.ToArray();

        static int TrailingNumber(string value)
        {
            var start = value.Length;
            while (start > 0 && char.IsAsciiDigit(value[start - 1])) start--;
            return start < value.Length && int.TryParse(value[start..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number : -1;
        }

        static string TruncateScrapbookCaption(string value)
        {
            var length = Math.Min(value.Length, 50);
            var truncated = value.Length > 50;
            if (truncated) length = 47;
            var result = new StringBuilder(length + (truncated ? 3 : 0));
            for (var index = 0; index < length; index++)
            {
                var character = value[index];
                if (character == '\n') result.Append(' ');
                else if (character == '"') result.Append("&quot;");
                else result.Append(character);
            }
            if (truncated) result.Append("...");
            return result.ToString();
        }
    }

    internal static int RecountFamilyFriends(Family family, IReadOnlyList<Neighbor> neighbors, int threshold = 50)
    {
        var memberGuids = family.Members.ToHashSet();
        var members = neighbors.Where(n => memberGuids.Contains(n.Guid) &&
            (n.PersonData is null || n.PersonData.Count <= 65 || (n.PersonData[65] & 6) == 0)).ToArray();
        var byId = neighbors.GroupBy(n => n.NeighborId).ToDictionary(g => g.Key, g => g.First());
        var friends = new HashSet<short>();
        foreach (var member in members)
        foreach (var (targetKey, values) in member.Relationships)
        {
            if (targetKey is <= 0 or > short.MaxValue || values.Count == 0) continue;
            var targetId = (short)targetKey;
            if (!byId.TryGetValue(targetId, out var target) || memberGuids.Contains(target.Guid)) continue;
            if (target.PersonData is { Count: > 65 } && (target.PersonData[65] & 6) != 0) continue;
            if (values[0] >= threshold &&
                target.Relationships.TryGetValue(member.NeighborId, out var reverse) &&
                reverse.Count > 0 && reverse[0] >= threshold)
            {
                friends.Add(targetId);
            }
        }
        return friends.Count;
    }

    internal const uint MagiCoinTokenGuid = 0x99E81BECu;

    internal static bool IsMagiCoinItem(NeighborhoodData.InventoryItem item) =>
        (item.Type == 7 && item.GUID == MagiCoinTokenGuid) || item.Type == 5;

    internal static int GetMagicoinsForFamily(Family family, IReadOnlyList<Neighbor> neighbors, NeighborhoodData? ngbhData)
    {
        if (ngbhData is null) return 0;
        var byGuid = neighbors.GroupBy(n => n.Guid).ToDictionary(g => g.Key, g => g.First());
        var total = 0;
        foreach (var guid in family.Members)
        {
            if (!byGuid.TryGetValue(guid, out var neighbor)) continue;
            if (!ngbhData.Inventories.TryGetValue(neighbor.NeighborId, out var items)) continue;
            foreach (var item in items)
            {
                // Sims.exe FUN_00410315 (00410315.c:23) queries SimInventory token type 7, GUID 0x99e81bec.
                if (IsMagiCoinItem(item)) total += item.Count;
            }
        }
        return total;
    }

    internal static bool IsPetsOnly(Family family, IReadOnlyDictionary<uint, Neighbor> neighborsByGuid)
    {
        if (family.Members.Count == 0) return false;
        return family.Members.All(guid =>
            neighborsByGuid.TryGetValue(guid, out var neighbor) &&
            neighbor.PersonData is { Count: > 65 } &&
            (neighbor.PersonData[65] & 6) != 0);
    }

    internal static object Export(
        string session,
        int mode,
        string profile,
        byte language = 1,
        string? dataRoot = null,
        string? gameRoot = null,
        bool includeRenderScenes = false,
        ObjectRegistry? existingRegistry = null)
    {
        gameRoot ??= GameRoot ?? Path.GetFullPath(Directory.Exists("The Sims 1") ? "The Sims 1" : ".");
        GameRoot = gameRoot;
        var userPath = Neighborhoods.UserData(profile);
        NeighborhoodEdits.ImportTutorialIfPresent(userPath, gameRoot, language);
        NeighborhoodEdits.SeedStraysIfNeeded(userPath, gameRoot, language);
        var config = Configure(mode, out var variant);
        if (mode == 1 && Neighborhoods.Current > 1)
        {
            var art = Directory.Exists(userPath)
                ? Directory.EnumerateFiles(userPath, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(file => Path.GetRelativePath(userPath, file).Replace('\\', '/').Equals("Map/NScreen.bmp", StringComparison.OrdinalIgnoreCase))
                : null;
            if (art != null)
                config = config with { BackgroundResource = Neighborhoods.DirectoryName(Neighborhoods.Current) + "/Map/NScreen.bmp", AnimationLayers = [] };
            var townFile = Directory.Exists(userPath)
                ? Directory.EnumerateFiles(userPath, "*.json", SearchOption.TopDirectoryOnly).FirstOrDefault(file => Path.GetFileName(file).Equals("Town.json", StringComparison.OrdinalIgnoreCase))
                : null;
            if (townFile != null)
            {
                using var town = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(townFile));
                if (town.RootElement.TryGetProperty("music", out var music) && music.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var name = music.GetString() ?? "";
                    if (name.Length is > 0 and <= 64 && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
                        config = config with { Music = name };
                }
            }
        }

        var locationsId = (ushort)(variant == "unleashed" ? 4 : mode);
        var locationsPath = ResolveUserFile(userPath, gameRoot, "LotLocations.iff")
            ?? throw new FileNotFoundException($"LotLocations.iff not found in {userPath}");
        var locationsFile = IffFile.Open(locationsPath);
        var locationsChunk = locationsFile.Find(ResourceType.Str, locationsId)
            ?? throw new InvalidDataException($"LotLocations.iff has no STR chunk {locationsId}");
        var locations = StringTable.Read(locationsFile, locationsChunk).ForLanguage(1);

        var nghPath = ResolveUserFile(userPath, gameRoot, "Neighborhood.iff")
            ?? throw new FileNotFoundException($"Neighborhood.iff not found in {userPath}");
        var nghFile = IffFile.Open(nghPath);
        var families = nghFile.OfType(ResourceType.Parse("FAMI"))
            .Where(c => c.Id != 0)
            .Select(c => (ChunkId: c.Id, Family: Family.Read(nghFile, c)))
            .ToArray();
        var familyStrings = nghFile.OfType(ResourceType.Parse("FAMs"))
            .ToDictionary(c => c.Id, c => StringTable.Read(nghFile, c).GetString(0, language));
        var nbrsChunk = nghFile.OfType(ResourceType.Parse("NBRS")).FirstOrDefault();
        var neighbors = nbrsChunk is not null ? Neighbors.ReadRecoveringSupportedPrefix(nghFile, nbrsChunk).Entries : [];
        var neighborsByGuid = neighbors.GroupBy(n => n.Guid).ToDictionary(g => g.Key, g => g.First());
        var ngbhChunk = nghFile.Find(ResourceType.Parse("NGBH"), 1);
        var ngbhData = ngbhChunk is not null ? NeighborhoodData.Read(nghFile, ngbhChunk) : null;

        var zoningDictionary = ReadZoningDictionary(userPath, gameRoot);
        var streetNamesPath = ResolveUserFile(userPath, gameRoot, "StreetNames.iff");
        var streetNamesFile = streetNamesPath is not null && File.Exists(streetNamesPath) ? IffFile.Open(streetNamesPath) : null;
        var streets2001 = streetNamesFile?.Find(ResourceType.Str, 2001) is { } s2001
            ? StringTable.Read(streetNamesFile, s2001).ForLanguage(1) : null;
        var streets2000 = streetNamesFile?.Find(ResourceType.Str, 2000) is { } s2000
            ? StringTable.Read(streetNamesFile, s2000).ForLanguage(language) : null;

        var outputDirectory = Path.Combine(Path.GetFullPath(session), "neighborhood");
        Directory.CreateDirectory(outputDirectory);
        var entries = new List<Entry>(locations.Count);
        var charactersPath = Path.Combine(userPath, "Characters");
        var registry = existingRegistry is not null
            ? existingRegistry.WithRefreshedPeople(charactersPath)
            : includeRenderScenes
                ? ObjectRegistry.Scan(gameRoot, charactersPath)
                : ObjectRegistry.ScanPeopleOnly(gameRoot, charactersPath);
        var library = includeRenderScenes ? WorldBootstrap.GetOrLoadGlobalLibrary(gameRoot) : null;
        var careerCatalog = WorldBootstrap.GetOrLoadCareers(gameRoot);

        using var effects = dataRoot != null ? PluginHost.Create(gameRoot, dataRoot, _ => { }, () => 0, _ => null!,
            userData: userPath, environmentOnly: true) : null;
        var environment = effects?.Environment(0, mode);
        string? backgroundImagePath = null;
        if (environment != null)
        {
            var bitmap = ReadBackground(config.BackgroundResource, userPath, gameRoot);
            var changed = effects!.TransformNeighborhoodImage(new("background", config.BackgroundResource, bitmap, null, 0, 0, 1), environment);
            backgroundImagePath = Path.Combine("neighborhood", "background.bmp");
            File.WriteAllBytes(Path.Combine(session, backgroundImagePath), EncodePluginImage(changed));
        }

        for (var index = 0; index < locations.Count; index++)
        {
            var fields = locations[index].Split(',');
            if (fields.Length < 3 || !int.TryParse(fields[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var house) ||
                !int.TryParse(fields[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
                !int.TryParse(fields[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            {
                if (index > 0) break;
                throw new InvalidDataException($"Invalid LotLocations entry {index} in STR {locationsId}");
            }
            if (TownRange(mode) is (int first, int last) && (house < first || house > last)) continue;

            var houseName = $"House{house:D2}.iff";
            var roomPath = ResolveUserFile(userPath, gameRoot, Path.Combine("Houses", houseName))
                ?? throw new FileNotFoundException($"Missing {houseName} in {userPath}");
            var houseFile = IffFile.Open(roomPath);
            var familyRecord = families.FirstOrDefault(f => f.Family.HouseNumber == house && (f.Family.Flags & 1) != 0);
            if (familyRecord.Family is null)
                familyRecord = families.FirstOrDefault(f => f.Family.HouseNumber == house);
            var family = familyRecord.Family;
            var familyChunkId = familyRecord.ChunkId;
            var occupied = family != null;
            var residential = zoningDictionary.TryGetValue((short)house, out var zoning) && zoning == 0;
            var loadedHouseWorld = includeRenderScenes && occupied && residential && library is not null
                ? SavedLotObjectLoader.Load(houseFile, registry, library, language)
                : null;
            var houseStats = loadedHouseWorld is not null
                ? ReadHouseRoomStats(houseFile, loadedHouseWorld, library!, family!.Members.Count, language)
                : null;

            var closedChunk = houseFile.Find(ResourceType.Parse("BMP_"), 513) ?? houseFile.Find(ResourceType.Parse("BMP "), 513)
                ?? throw new InvalidDataException($"{houseName} is missing BMP chunk 513");
            var openChunk = houseFile.Find(ResourceType.Parse("BMP_"), 512) ?? houseFile.Find(ResourceType.Parse("BMP "), 512)
                ?? throw new InvalidDataException($"{houseName} is missing BMP chunk 512");
            var thmbChunk = houseFile.Find(ResourceType.Parse("THMB"), 512)
                ?? throw new InvalidDataException($"{houseName} is missing THMB chunk 512");
            if (thmbChunk.Data.Length < 20)
                throw new InvalidDataException($"{houseName} THMB chunk 512 is truncated ({thmbChunk.Data.Length} bytes)");
            var thmbSpan = thmbChunk.Data.Span;
            var thumbWidth = BinaryPrimitives.ReadInt32LittleEndian(thmbSpan[0..4]);
            var thumbHeight = BinaryPrimitives.ReadInt32LittleEndian(thmbSpan[4..8]);
            var baseYOff = BinaryPrimitives.ReadInt32LittleEndian(thmbSpan[8..12]);
            var xOff = BinaryPrimitives.ReadInt32LittleEndian(thmbSpan[12..16]);
            var addYOff = BinaryPrimitives.ReadInt32LittleEndian(thmbSpan[16..20]);

            var familyName = family == null ? null : familyStrings.GetValueOrDefault(familyChunkId);
            var description = ReadHouseDescriptionStrings(userPath, gameRoot, house, language);
            var lotName = description is { Count: > 0 } ? description[0].Trim() : null;
            var houseDescription = description is { Count: > 1 } ? description[1].Trim() : null;
            var popupName = description is { Count: > 0 } ? description[0] : null;
            var popupDescription = description is { Count: > 1 } ? description[1] : null;

            int? street = streets2001 != null && house - 1 >= 0 && house - 1 < streets2001.Count &&
                int.TryParse(streets2001[house - 1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var streetValue)
                ? streetValue : null;
            var streetTemplate = house is >= 21 and <= 31 or >= 40 and <= 49
                ? PackStreetTemplate(userPath, house, language)
                : street is int streetIndex && streets2000 != null && streetIndex >= 1 && streetIndex <= streets2000.Count
                    ? streets2000[streetIndex - 1] : null;

            var price = 0;
            var filterFlags = 0;
            var tutorialHouse = false;
            var lotHasHouse = false;
            int? simiDaysRunning = null;
            if (houseFile.Find(ResourceType.Parse("SIMI"), 1) is { } simiChunk)
            {
                var simi = SimulationState.Read(houseFile, simiChunk);
                price = Math.Max(0, simi.Field0x9C + simi.Field0xA0 + (int)(simi.Field0xA4 * 0.7f));
                var globals = simi.Globals;
                short Global(SimulationGlobal global) => globals.Count > (int)global ? globals[(int)global] : (short)0;
                filterFlags = Global(SimulationGlobal.FilterFlagsForTransitScreen);
                tutorialHouse = Global(SimulationGlobal.IsTutorialHouseNoYes) != 0;
                lotHasHouse = Global(SimulationGlobal.LotHasHouse) != 0;
                if (globals.Count > (int)SimulationGlobal.DaysRunning)
                    simiDaysRunning = globals[(int)SimulationGlobal.DaysRunning];
            }

            var closedData = closedChunk.Data.ToArray();
            var openData = openChunk.Data.ToArray();
            var closedDimensions = ReadBitmapDimensions(closedData, houseName, 513);
            var openDimensions = ReadBitmapDimensions(openData, houseName, 512);
            var closedFilename = $"house{house:D2}-closed.bmp";
            var openFilename = $"house{house:D2}-open.bmp";
            var closedPath = Path.Combine(outputDirectory, closedFilename);
            var openPath = Path.Combine(outputDirectory, openFilename);
            if (environment != null)
            {
                var closedImage = DecodePluginImage(closedData);
                var openImage = DecodePluginImage(openData);
                var step = 1 / config.Scale;
                var left = x + (xOff / 2f - closedImage.Width / 2f) * step;
                var top = y + (baseYOff / 2f - closedImage.Height / 2f) * step;
                closedData = EncodePluginImage(effects!.TransformNeighborhoodImage(new("closedLot", "", closedImage, openImage, left, top, step), environment));
                openData = EncodePluginImage(effects.TransformNeighborhoodImage(new("openLot", "", openImage, null, left, top - addYOff * step, step), environment));
            }
            File.WriteAllBytes(closedPath, closedData);
            File.WriteAllBytes(openPath, openData);
            var source = $"UserData/Houses/{houseName}";

            var memberImages = (family?.Members ?? []).Select((guid, member) =>
            {
                var charEntry = registry.Find(guid);
                var thumbChunk = charEntry?.File.Find(ResourceType.Parse("BMP_"), 2004) ?? charEntry?.File.Find(ResourceType.Parse("BMP "), 2004);
                var thumbnail = thumbChunk?.Data.ToArray();
                if (thumbnail == null || thumbnail.Length < 26 || thumbnail[0] != (byte)'B' || thumbnail[1] != (byte)'M') return null;
                var filename = $"house{house:D2}-member{member}.bmp";
                File.WriteAllBytes(Path.Combine(outputDirectory, filename), thumbnail);
                return Path.Combine("neighborhood", filename);
            }).ToArray();

            var memberNames = (family?.Members ?? []).Select(guid =>
                registry.Find(guid)?.CatalogString(0, language) is { Length: > 0 } cName ? cName
                : neighborsByGuid.TryGetValue(guid, out var neighbor) ? neighbor.Name ?? "" : "").ToArray();

            var memberProfiles = (family?.Members ?? []).Select(guid =>
            {
                neighborsByGuid.TryGetValue(guid, out var neighbor);
                var displayName = registry.Find(guid)?.CatalogString(0, language) is { Length: > 0 } cName ? cName : neighbor?.Name ?? "";
                return MakeMemberProfile(displayName, neighbor?.PersonData, language, registry, careerCatalog, gameRoot, guid);
            }).ToArray();

            var scrapbookPhotos = family != null && familyName != null
                ? CopyScrapbookPhotos(userPath, familyName, family.FamilyNumber, house, outputDirectory)
                : [];
            var renderScenes = loadedHouseWorld != null
                ? ExportHouseRenderScenes(houseFile, loadedHouseWorld, registry, gameRoot, session, checked((short)house))
                : null;

            entries.Add(new Entry(index, house, x, y, Path.Combine("neighborhood", closedFilename), Path.Combine("neighborhood", openFilename),
                closedDimensions.Width, closedDimensions.Height, openDimensions.Width, openDimensions.Height,
                thumbWidth, thumbHeight, xOff, baseYOff, addYOff,
                $"{source}#BMP:513", $"{source}#BMP:512", $"{source}#THMB:512", occupied, residential,
                !occupied && residential && price is >= 0 and <= 20_000,
                price, zoningDictionary.TryGetValue((short)house, out var zone) && zone == 1, filterFlags,
                string.IsNullOrEmpty(lotName) ? null : lotName, string.IsNullOrEmpty(houseDescription) ? null : houseDescription, street,
                string.IsNullOrEmpty(streetTemplate) ? null : streetTemplate,
                familyName, family == null ? null : family.Budget + family.Value,
                family == null ? null : GetMagicoinsForFamily(family, neighbors, ngbhData),
                family == null ? null : RecountFamilyFriends(family, neighbors),
                family == null ? null : houseStats?.DaysRunning ?? simiDaysRunning,
                !occupied && NeighborhoodEdits.IsBulldozed(userPath, house, gameRoot), memberImages, memberNames, memberProfiles,
                houseStats?.SquareFeet, houseStats?.Bathrooms, houseStats?.Bedrooms, houseStats?.Floors,
                family?.Value, houseStats?.SizeRating, houseStats?.FurnishingsRating, houseStats?.YardRating,
                houseStats?.UpkeepRating, houseStats?.LayoutRating,
                scrapbookPhotos, renderScenes,
                family != null && family.Field0x13C != 0 ||
                mode == 3 && families.Any(other => other.Family.HouseNumber == house || other.Family.Field0x13C == house),
                tutorialHouse, lotHasHouse, popupName, popupDescription));
        }

        var (imports, importEnabled) = ReadImports(userPath, profile, gameRoot, language);
        var manifest = new
        {
            schemaVersion = 2,
            mode,
            variant,
            neighborhood = Neighborhoods.Current,
            neighborhoods = Neighborhoods.Installed(profile),
            neighborhoodNames = TownNames(profile),
            backgroundResource = config.BackgroundResource,
            backgroundSource = config.BackgroundResource.StartsWith("UIGraphics/", StringComparison.Ordinal)
                ? "UIGraphics/UIGraphics.far#" + config.BackgroundResource["UIGraphics/".Length..]
                : config.BackgroundResource,
            canvasWidth = 800,
            canvasHeight = 600,
            scale = config.Scale,
            pulsate = config.Pulsate,
            frameDuration = config.FrameDuration,
            music = config.Music,
            ambientSound = config.AmbientSound,
            animationLayers = config.AnimationLayers,
            locationsSource = $"UserData/LotLocations.iff#STR:{locationsId}",
            travelFilter = mode == 4 ? TravelFilter.Load(userPath, TravelFilter.Unleashed, language) : (int?)null,
            entries,
            families = mode is 1 or 7 ? UnhousedFamilies(userPath, gameRoot, language, registry, careerCatalog) : [],
            imports,
            importEnabled,
            backgroundImagePath,
            environment
        };
        File.WriteAllText(Path.Combine(Path.GetFullPath(session), "neighborhood.json"),
            System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, WriteIndented = true }));
        effects?.CommitNeighborhood();
        return manifest;
    }

    private static PluginImage DecodePluginImage(byte[] bytes)
    {
        var (w, h, rgba) = BitmapCodec.Read(bytes);
        return new PluginImage(w, h, rgba);
    }

    private static byte[] EncodePluginImage(PluginImage image) =>
        BitmapCodec.Write(image.Width, image.Height, image.Rgba);

    private static PluginImage ReadBackground(string resource, string userPath, string gameRoot)
    {
        var loose = Path.Combine(gameRoot, resource.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(loose)) return DecodePluginImage(File.ReadAllBytes(loose));
        if (!resource.StartsWith("UIGraphics/", StringComparison.OrdinalIgnoreCase))
            return DecodePluginImage(File.ReadAllBytes(Path.Combine(userPath, "Map", "NScreen.bmp")));
        var archive = FarArchive.Open(Path.Combine(gameRoot, "UIGraphics", "UIGraphics.far"));
        var relative = resource["UIGraphics/".Length..];
        var entry = archive.Entries.Single(item => item.Name.Replace('\\', '/').Equals(relative, StringComparison.OrdinalIgnoreCase));
        return DecodePluginImage(archive.Read(entry).ToArray());
    }

    private static string? PackStreetTemplate(string userPath, int house, byte language)
    {
        var name = house <= 31 ? "Downtown.iff" : "Vacation.iff";
        var file = Directory.Exists(userPath)
            ? Directory.EnumerateFiles(userPath, "*.iff", SearchOption.TopDirectoryOnly).FirstOrDefault(candidate => Path.GetFileName(candidate).Equals(name, StringComparison.OrdinalIgnoreCase))
            : null;
        if (file == null || new FileInfo(file).Length > 4 * 1024 * 1024) return null;
        try
        {
            var iff = IffFile.Open(file);
            if (iff.Find(ResourceType.Str, 128) is not { } chunk) return null;
            var strings = StringTable.Read(iff, chunk).ForLanguage(language);
            var index = house is <= 23 or >= 40 and <= 42 ? 0 : 1;
            var street = index < strings.Count ? strings[index] : null;
            return string.IsNullOrEmpty(street) ? null : "%s " + street;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Dictionary<string, string> TownNames(string profile)
    {
        var names = new Dictionary<string, string>();
        foreach (var number in Neighborhoods.Installed(profile))
        {
            if (number == 1) continue;
            var dir = Path.Combine(profile, Neighborhoods.DirectoryName(number));
            if (!Directory.Exists(dir)) continue;
            var file = Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(candidate => Path.GetFileName(candidate).Equals("Town.json", StringComparison.OrdinalIgnoreCase));
            if (file == null) continue;
            try
            {
                using var town = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(file));
                if (town.RootElement.TryGetProperty("name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String &&
                    name.GetString() is { Length: > 0 and <= 40 } text && text.All(character => !char.IsControl(character)))
                    names[number.ToString(CultureInfo.InvariantCulture)] = text;
            }
            catch (System.Text.Json.JsonException) { }
        }
        return names;
    }

    internal sealed record ImportEntry(string File, string FamilyName, int House, string[] Members);

    private static (ImportEntry[] Imports, bool Enabled) ReadImports(string userPath, string profile, string gameRoot, byte language)
    {
        var (candidates, enabled) = NeighborhoodEdits.EnumerateImportCandidates(userPath, profile, gameRoot, language);
        var imports = candidates
            .Select(c => new ImportEntry(c.File, c.FamilyName, c.House, c.Members))
            .ToArray();
        return (imports, enabled);
    }

    internal sealed record FamilyChoice(int Id, string Name, int Budget, int NetWorth, int Friends, string[] Members,
        MemberProfile[] MemberProfiles, byte[]?[] MemberThumbnails, int MagiCoins = 0);

    /// <summary>Sums Family MagiCoins across member SimInventory records per Sims.exe FUN_00410315 (00410315.c:16-30):
    /// inventory token type 7, GUID 0x99e81bec, truncated to int16.</summary>
    internal static int SumFamilyMagiCoins(
        Family family,
        IReadOnlyDictionary<uint, Neighbor> neighborsByGuid,
        NeighborhoodData? ngbhData)
    {
        if (ngbhData is null) return 0;
        var total = 0;
        foreach (var guid in family.Members)
        {
            if (!neighborsByGuid.TryGetValue(guid, out var neighbor)) continue;
            if (!ngbhData.Inventories.TryGetValue(neighbor.NeighborId, out var items)) continue;
            foreach (var item in items)
            {
                if (item.Type == 7 && item.GUID == 0x99e81becu)
                    total += item.Count;
            }
        }
        return (short)total;
    }

    internal static FamilyChoice[] UnhousedFamilies(string userPath, string gameRoot, byte language = 1,
        ObjectRegistry? existingRegistry = null, CareerCatalog? existingCareers = null)
    {
        var nghPath = ResolveUserFile(userPath, gameRoot, "Neighborhood.iff");
        if (nghPath is null || !File.Exists(nghPath)) return [];
        var nghFile = IffFile.Open(nghPath);
        var nbrsChunk = nghFile.OfType(ResourceType.Parse("NBRS")).FirstOrDefault();
        var neighbors = nbrsChunk is not null ? Neighbors.ReadRecoveringSupportedPrefix(nghFile, nbrsChunk).Entries : [];
        var neighborsByGuid = neighbors.GroupBy(n => n.Guid).ToDictionary(g => g.Key, g => g.First());
        var ngbhChunk = nghFile.Find(ResourceType.Parse("NGBH"), 1);
        var ngbhData = ngbhChunk is not null ? NeighborhoodData.Read(nghFile, ngbhChunk) : null;
        var familyStrings = nghFile.OfType(ResourceType.Parse("FAMs"))
            .ToDictionary(c => c.Id, c => StringTable.Read(nghFile, c).GetString(0, language));
        var registry = existingRegistry ?? ObjectRegistry.ScanPeopleOnly(gameRoot, Path.Combine(userPath, "Characters"));
        var careerCatalog = existingCareers ?? WorldBootstrap.GetOrLoadCareers(gameRoot);

        return nghFile.OfType(ResourceType.Parse("FAMI"))
            .Where(c => c.Id != 0)
            .Select(c => (ChunkId: c.Id, Family: Family.Read(nghFile, c)))
            // Sims.exe 004e2f00.c:14 excludes families with bit 3 (0x08) set (Strays = 0x18), keeping Maxis & user bin families (Flags == 0).
            .Where(f => f.Family.HouseNumber == 0 && f.Family.Members.Count > 0 && (f.Family.Flags & 8) == 0 && !IsPetsOnly(f.Family, neighborsByGuid))
            .OrderBy(f => f.ChunkId)
            .Select(f =>
            {
                var netWorth = f.Family.HouseNumber == 0 && f.Family.Flags == 0 && f.Family.Value == f.Family.Budget
                    ? f.Family.Budget
                    : f.Family.Budget + f.Family.Value;
                return new FamilyChoice(
                    f.ChunkId,
                    familyStrings.GetValueOrDefault(f.ChunkId) ?? "",
                    f.Family.Budget,
                    netWorth,
                RecountFamilyFriends(f.Family, neighbors),
                f.Family.Members.Select(guid =>
                    registry.Find(guid)?.CatalogString(0, language) is { Length: > 0 } cName ? cName
                    : neighborsByGuid.TryGetValue(guid, out var n) ? n.Name ?? "" : "").ToArray(),
                f.Family.Members.Select(guid =>
                {
                    neighborsByGuid.TryGetValue(guid, out var neighbor);
                    var displayName = registry.Find(guid)?.CatalogString(0, language) is { Length: > 0 } cName ? cName : neighbor?.Name ?? "";
                    return MakeMemberProfile(displayName, neighbor?.PersonData, language, registry, careerCatalog, gameRoot, guid);
                }).ToArray(),
                f.Family.Members.Select(guid =>
                {
                    var charEntry = registry.Find(guid);
                    var bmp = charEntry?.File.Find(ResourceType.Parse("BMP_"), 2004) ?? charEntry?.File.Find(ResourceType.Parse("BMP "), 2004);
                    return bmp?.Data.ToArray();
                }).ToArray(),
                SumFamilyMagiCoins(f.Family, neighborsByGuid, ngbhData));
            })
            .ToArray();
    }

    internal sealed record RolloverHouseInfo(
        int HouseNumber,
        bool TutorialHouse,
        bool LotHasHouse,
        int Price,
        int FamilyId = -1,
        bool IsCommunity = false,
        int ArchitectureValue = 0);

    /// <summary>
    /// Ports Sims.exe cWinLotPopup::GetPopupIndex (00551013.c:17-85) and Magic Town residential lot
    /// MagiCoin affordability (00556352.c:26-31, 77-85).
    /// </summary>
    internal static int RolloverStatusCode(
        RolloverHouseInfo house,
        int mode,
        int familyBudget = 0,
        int familyMagiCoins = 0)
        => RolloverStatusCode(
            mode,
            house.IsCommunity ? 2 : 1,
            house.TutorialHouse,
            house.FamilyId,
            house.LotHasHouse,
            house.Price,
            familyBudget,
            house.HouseNumber,
            familyMagiCoins);

    internal static int RolloverStatusCode(
        int mode,
        int zoning,
        bool tutorialHouse,
        int familyId,
        bool lotHasHouse,
        int price,
        int familyBudget = 0,
        int houseNumber = 0,
        int familyMagiCoins = 0)
    {
        var isCommunity = zoning == 2;
        if (mode == 4)
        {
            if (isCommunity) return 21;
            return familyId < 0 ? 20 : 0;
        }

        if (tutorialHouse)
        {
            return mode switch
            {
                1 => 8,
                2 => 9,
                _ => 6
            };
        }

        if (familyId < 0)
        {
            switch (mode)
            {
                case 1:
                {
                    if (isCommunity) return 18;
                    var canAfford = familyBudget >= price;
                    if (canAfford && houseNumber is 90 or 91 or 92)
                    {
                        var requiredMagiCoins = houseNumber == 91 ? 150 : 100;
                        canAfford = familyMagiCoins >= requiredMagiCoins;
                    }
                    if (!lotHasHouse)
                        return canAfford ? 5 : 4;
                    return canAfford ? 3 : 2;
                }
                case 2:
                    return lotHasHouse ? 10 : 11;
                case 3:
                    return 19;
                default:
                    return zoning == 1 ? 1 : 17;
            }
        }

        if (mode is 2 or 3)
            return 9;

        return 0;
    }

    private static (int Width, int Height) ReadBitmapDimensions(byte[] data, string filename, ushort chunkID)
    {
        if (data.Length < 26 || data[0] != (byte)'B' || data[1] != (byte)'M')
            throw new InvalidDataException($"{filename} BMP chunk {chunkID} has an invalid bitmap header");
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(14, 4));
        int width;
        int height;
        if (headerSize == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(18, 2));
            height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(20, 2));
        }
        else if (headerSize >= 40 && headerSize <= data.Length - 14)
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18, 4));
            var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22, 4));
            height = checked((int)Math.Abs((long)signedHeight));
        }
        else
        {
            throw new InvalidDataException($"{filename} BMP chunk {chunkID} has an unsupported bitmap header");
        }
        if (width <= 0 || height <= 0) throw new InvalidDataException($"{filename} BMP chunk {chunkID} has invalid dimensions");
        return (width, height);
    }
}
