using System.Text.Json.Serialization;

namespace SimsSDL.Core;

public static class NeighborhoodWebExport
{
    public sealed record MemberProfile
    {
        [JsonPropertyName("gender")]
        public string? Gender { get; init; }

        [JsonPropertyName("species")]
        public string? Species { get; init; }

        [JsonPropertyName("age")]
        public string? Age { get; init; }

        [JsonPropertyName("neat")]
        public int? Neat { get; init; }

        [JsonPropertyName("outgoing")]
        public int? Outgoing { get; init; }

        [JsonPropertyName("active")]
        public int? Active { get; init; }

        [JsonPropertyName("playful")]
        public int? Playful { get; init; }

        [JsonPropertyName("nice")]
        public int? Nice { get; init; }

        [JsonPropertyName("cooking")]
        public int? Cooking { get; init; }

        [JsonPropertyName("mechanical")]
        public int? Mechanical { get; init; }

        [JsonPropertyName("charisma")]
        public int? Charisma { get; init; }

        [JsonPropertyName("body")]
        public int? Body { get; init; }

        [JsonPropertyName("logic")]
        public int? Logic { get; init; }

        [JsonPropertyName("creativity")]
        public int? Creativity { get; init; }

        [JsonPropertyName("career")]
        public string? Career { get; init; }

        [JsonPropertyName("job")]
        public string? Job { get; init; }

        [JsonPropertyName("performance")]
        public int? Performance { get; init; }

        [JsonPropertyName("performanceLabel")]
        public string? PerformanceLabel { get; init; }

        [JsonPropertyName("salary")]
        public int? Salary { get; init; }

        [JsonPropertyName("zodiac")]
        public int? Zodiac { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonConstructor]
        public MemberProfile() { }

        public MemberProfile(
            string? gender,
            string? species,
            string? age,
            int? neat,
            int? outgoing,
            int? active,
            int? playful,
            int? nice,
            int? cooking,
            int? mechanical,
            int? charisma,
            int? body,
            int? logic,
            int? creativity,
            string? career = null,
            string? job = null,
            int? performance = null,
            string? performanceLabel = null,
            int? salary = null,
            int? zodiac = null,
            string? description = null)
        {
            Gender = gender;
            Species = species;
            Age = age;
            Neat = neat;
            Outgoing = outgoing;
            Active = active;
            Playful = playful;
            Nice = nice;
            Cooking = cooking;
            Mechanical = mechanical;
            Charisma = charisma;
            Body = body;
            Logic = logic;
            Creativity = creativity;
            Career = career;
            Job = job;
            Performance = performance;
            PerformanceLabel = performanceLabel;
            Salary = salary;
            Zodiac = zodiac;
            Description = description;
        }
    }

    public sealed record ScrapbookPhoto(byte[]? Image, byte[]? Thumbnail, string Caption)
    {
        public bool Equals(ScrapbookPhoto? other) =>
            other is not null &&
            BytesEqual(Image, other.Image) &&
            BytesEqual(Thumbnail, other.Thumbnail) &&
            string.Equals(Caption, other.Caption, StringComparison.Ordinal);

        public override int GetHashCode()
        {
            var hc = new HashCode();
            AddBytes(ref hc, Image);
            AddBytes(ref hc, Thumbnail);
            hc.Add(Caption, StringComparer.Ordinal);
            return hc.ToHashCode();
        }
    }

    public sealed record HouseViews(
        byte[]? Thumbnail = null,
        byte[]? Exterior = null,
        byte[]? Floor1 = null,
        byte[]? Floor2 = null)
    {
        public bool Equals(HouseViews? other) =>
            other is not null &&
            BytesEqual(Thumbnail, other.Thumbnail) &&
            BytesEqual(Exterior, other.Exterior) &&
            BytesEqual(Floor1, other.Floor1) &&
            BytesEqual(Floor2, other.Floor2);

        public override int GetHashCode()
        {
            var hc = new HashCode();
            AddBytes(ref hc, Thumbnail);
            AddBytes(ref hc, Exterior);
            AddBytes(ref hc, Floor1);
            AddBytes(ref hc, Floor2);
            return hc.ToHashCode();
        }
    }

    public sealed class Family
    {
        public string Name { get; }
        public int Lot { get; }
        public int Street { get; }
        public int Members { get; }
        public int Friends { get; }
        public int? CashBalance { get; }
        public int? DaysExist { get; }
        public IReadOnlyList<ScrapbookPhoto> ScrapbookPhotos { get; }
        public HouseViews? HouseViews { get; }
        public IReadOnlyList<string> MemberNames { get; }
        public IReadOnlyList<byte[]?> MemberThumbnails { get; }
        public IReadOnlyList<MemberProfile?> MemberProfiles { get; }
        public int? HouseSquareFeet { get; }
        public int? HouseBathrooms { get; }
        public int? HouseBedrooms { get; }
        public int? HouseFloors { get; }
        public int? HouseValue { get; }
        public int? HouseSizeRating { get; }
        public int? HouseFurnishingsRating { get; }
        public int? HouseYardRating { get; }
        public int? HouseUpkeepRating { get; }
        public int? HouseLayoutRating { get; }

        public Family(
            string name,
            int lot,
            int street,
            int members,
            int friends,
            int? cashBalance = null,
            int? daysExist = null,
            IReadOnlyList<string>? memberNames = null,
            IReadOnlyList<byte[]?>? memberThumbnails = null,
            IReadOnlyList<MemberProfile?>? memberProfiles = null,
            IReadOnlyList<ScrapbookPhoto>? scrapbookPhotos = null,
            HouseViews? houseViews = null,
            int? houseSquareFeet = null,
            int? houseBathrooms = null,
            int? houseBedrooms = null,
            int? houseFloors = null,
            int? houseValue = null,
            int? houseSizeRating = null,
            int? houseFurnishingsRating = null,
            int? houseYardRating = null,
            int? houseUpkeepRating = null,
            int? houseLayoutRating = null)
        {
            Name = name;
            Lot = lot;
            Street = street;
            Members = members;
            Friends = friends;
            CashBalance = cashBalance;
            DaysExist = daysExist;
            ScrapbookPhotos = scrapbookPhotos ?? [];
            HouseViews = houseViews;
            MemberNames = memberNames ?? [];
            MemberThumbnails = memberThumbnails ?? [];
            MemberProfiles = memberProfiles ?? [];
            HouseSquareFeet = houseSquareFeet;
            HouseBathrooms = houseBathrooms;
            HouseBedrooms = houseBedrooms;
            HouseFloors = houseFloors;
            HouseValue = houseValue;
            HouseSizeRating = houseSizeRating;
            HouseFurnishingsRating = houseFurnishingsRating;
            HouseYardRating = houseYardRating;
            HouseUpkeepRating = houseUpkeepRating;
            HouseLayoutRating = houseLayoutRating;
        }
    }

    private static readonly string[] ZodiacSigns =
    [
        "Aries", "Taurus", "Gemini", "Cancer", "Leo", "Virgo",
        "Libra", "Scorpio", "Sagittarius", "Capricorn", "Aquarius", "Pisces",
    ];

    public static string Export(
        string templates,
        IReadOnlyList<Family> families,
        string destination,
        InstalledGameLanguage? language = null)
    {
        var resolvedLanguage = language ?? InstalledGameLanguage.EnglishUS;
        (string templateLanguage, string webLanguageCode) = resolvedLanguage.Code switch
        {
            2 => ("English", "en"),
            3 => ("French", "fr"),
            4 => ("German", "de"),
            5 => ("Italian", "it"),
            6 => ("Spanish", "es"),
            7 => ("Dutch", "nl"),
            8 => ("Danish", "da"),
            9 => ("Swedish", "sv"),
            10 => ("Norwegian", "no"),
            11 => ("Finnish", "fi"),
            12 => ("Hebrew", "he"),
            13 => ("Russian", "ru"),
            14 => ("Portuguese", "pt"),
            15 => ("Japanese", "ja"),
            16 => ("Polish", "pl"),
            17 => ("Chinese (Traditional)", "zh-TW"),
            18 => ("Chinese (Simplified)", "zh-CN"),
            19 => ("Thai", "th"),
            20 => ("Korean", "ko"),
            _ => ("English", "en"),
        };

        string? root = null;
        foreach (string folderName in new[] { "Web_Templates", "Web Templates" })
        {
            string candidate = Path.Combine(templates, "UserData", folderName, templateLanguage);
            if (Directory.Exists(candidate))
            {
                root = candidate;
                break;
            }
        }
        if (root is null)
            throw new DirectoryNotFoundException($"Web templates directory not found under {templates}");

        string fullDest = Path.GetFullPath(destination);
        string destPrefix = fullDest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var exportFamilies = new List<Family>(families.Count);
        for (int offset = 0; offset < families.Count; offset++)
        {
            Family family = families[offset];
            if (family.HouseViews is not null || family.Lot == 0)
            {
                exportFamilies.Add(family);
                continue;
            }

            string folder = $"{offset + 1}_{family.Name.Replace(" ", "")}_Sim_Lane";
            string graphics = Path.Combine(fullDest, folder, "FamilyGFX");

            byte[]? Cached(string fileName)
            {
                try
                {
                    string filePath = Path.GetFullPath(Path.Combine(graphics, fileName));
                    if (!filePath.StartsWith(destPrefix, StringComparison.Ordinal))
                        return null;
                    var info = new FileInfo(filePath);
                    if (!info.Exists ||
                        (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                        info.Length <= 0 ||
                        info.Length > 16 * 1024 * 1024)
                    {
                        return null;
                    }
                    if (ContainsSymlinkBetween(fullDest, filePath))
                        return null;
                    return File.ReadAllBytes(filePath);
                }
                catch
                {
                    return null;
                }
            }

            var views = new HouseViews(
                Thumbnail: Cached("house-thumb.jpg"),
                Exterior: Cached("house-exterior.jpg"),
                Floor1: Cached("house-floor1.jpg"),
                Floor2: Cached("house-floor2.jpg"));

            if (views.Thumbnail is null && views.Exterior is null && views.Floor1 is null && views.Floor2 is null)
            {
                exportFamilies.Add(family);
                continue;
            }

            exportFamilies.Add(new Family(
                name: family.Name,
                lot: family.Lot,
                street: family.Street,
                members: family.Members,
                friends: family.Friends,
                cashBalance: family.CashBalance,
                daysExist: family.DaysExist,
                memberNames: family.MemberNames,
                memberThumbnails: family.MemberThumbnails,
                memberProfiles: family.MemberProfiles,
                scrapbookPhotos: family.ScrapbookPhotos,
                houseViews: views,
                houseSquareFeet: family.HouseSquareFeet,
                houseBathrooms: family.HouseBathrooms,
                houseBedrooms: family.HouseBedrooms,
                houseFloors: family.HouseFloors,
                houseValue: family.HouseValue,
                houseSizeRating: family.HouseSizeRating,
                houseFurnishingsRating: family.HouseFurnishingsRating,
                houseYardRating: family.HouseYardRating,
                houseUpkeepRating: family.HouseUpkeepRating,
                houseLayoutRating: family.HouseLayoutRating));
        }

        try
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
        }
        catch
        {
        }
        Directory.CreateDirectory(destination);

        var common = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sims_web_character_set"] = "iso-8859-1",
            ["sims_web_language_code"] = webLanguageCode,
            // The SIMI resource stores the expansion pack bitmask in GameEdition.
            ["sims_game_edition"] = "255",
            ["sims_neighborhood_numfamilies"] = exportFamilies.Count.ToString(),
        };

        foreach (string item in Directory.EnumerateFileSystemEntries(root))
        {
            string name = Path.GetFileName(item);
            if (Directory.Exists(item))
            {
                if (name == "NeighborhoodGFX")
                    CopyDirectory(item, Path.Combine(destination, name));
            }
            else if (name != "familyX.html")
            {
                if (name.EndsWith(".html", StringComparison.Ordinal))
                    WriteAllTextAtomic(Path.Combine(destination, name), Fill(item, common), Encoding.Latin1);
                else
                    File.Copy(item, Path.Combine(destination, name), overwrite: true);
            }
        }

        string rowTemplate = Path.Combine(root, "familyX.html");
        for (int offset = 0; offset < exportFamilies.Count; offset++)
        {
            Family family = exportFamilies[offset];
            int index = offset + 1;
            bool housed = family.Lot != 0;
            string folder = housed ? $"{index}_{family.Name.Replace(" ", "")}_Sim_Lane" : "#";

            var values = new Dictionary<string, string>(common, StringComparer.Ordinal)
            {
                ["sims_neighborhood_familyX_id"] = index.ToString(),
                ["sims_neighborhood_familyX_lot"] = family.Lot.ToString(),
                ["sims_neighborhood_familyX_name"] = family.Name,
                ["sims_neighborhood_familyX_name_nospaces"] = family.Name.Replace(" ", ""),
                ["sims_neighborhood_familyX_nummembers"] = family.Members.ToString(),
                ["sims_neighborhood_familyX_targetURL"] = folder,
            };

            int rowMemberCount = Math.Min(8, family.MemberNames.Count);
            for (int member = 0; member < rowMemberCount; member++)
                values[$"sims_familymember{member + 1}_name"] = family.MemberNames[member];

            WriteAllTextAtomic(Path.Combine(destination, $"family{index}.html"), Fill(rowTemplate, values), Encoding.Latin1);
            if (!housed)
                continue;

            string directory = Path.Combine(destination, folder);
            string theme = Path.Combine(root, $"{Math.Min(Math.Max(family.Street, 0), 10)}_Sim_Lane");
            if (Directory.Exists(theme))
                CopyDirectory(theme, directory);
            else
                Directory.CreateDirectory(directory);

            string familyGfx = Path.Combine(directory, "FamilyGFX");
            WriteFamilyThumbnails(family.MemberThumbnails, familyGfx);
            WriteScrapbook(family.ScrapbookPhotos, familyGfx);
            WriteHouseViews(family.HouseViews, familyGfx);

            var lot = new Dictionary<string, string>(common, StringComparer.Ordinal)
            {
                ["sims_family_name"] = family.Name,
                ["sims_family_nummembers"] = family.Members.ToString(),
                ["sims_family_numfriends"] = family.Friends.ToString(),
                ["sims_family_daysexist"] = (family.DaysExist ?? 0).ToString(),
                ["sims_house_address"] = family.Lot.ToString(),
            };
            if (family.CashBalance is { } cashBalance) lot["sims_family_cashbalance"] = cashBalance.ToString();
            if (family.HouseSquareFeet is { } sqFt) lot["sims_house_squarefeet"] = sqFt.ToString();
            if (family.HouseBathrooms is { } baths) lot["sims_house_numbathrooms"] = baths.ToString();
            if (family.HouseBedrooms is { } beds) lot["sims_house_numbedrooms"] = beds.ToString();
            if (family.HouseFloors is { } floors) lot["sims_house_numfloors"] = floors.ToString();
            if (family.HouseValue is { } houseValue) lot["sims_house_value"] = houseValue.ToString();
            if (family.HouseSizeRating is { } sizeRating) lot["sims_house_ratings_size"] = sizeRating.ToString();
            if (family.HouseFurnishingsRating is { } furnishingsRating) lot["sims_house_ratings_furnishings"] = furnishingsRating.ToString();
            if (family.HouseYardRating is { } yardRating) lot["sims_house_ratings_yard"] = yardRating.ToString();
            if (family.HouseUpkeepRating is { } upkeepRating) lot["sims_house_ratings_upkeep"] = upkeepRating.ToString();
            if (family.HouseLayoutRating is { } layoutRating) lot["sims_house_ratings_layout"] = layoutRating.ToString();

            int lotMemberCount = Math.Min(8, family.MemberNames.Count);
            for (int member = 0; member < lotMemberCount; member++)
                lot[$"sims_familymember{member + 1}_name"] = family.MemberNames[member];

            try
            {
                foreach (string leftover in Directory.EnumerateFiles(directory, "*.html"))
                {
                    try { File.Delete(leftover); } catch { }
                }
            }
            catch
            {
            }

            foreach (string page in new[] { "familyhome.html", "house.html" })
            {
                string pageTemplate = Path.Combine(root, "LotTemplates", page);
                WriteAllTextAtomic(Path.Combine(directory, page), Fill(pageTemplate, lot), Encoding.Latin1);
            }

            string memberTemplate = Path.Combine(theme, "familymemberX.html");
            if (File.Exists(memberTemplate))
            {
                for (int member = 0; member < lotMemberCount; member++)
                {
                    var memberValues = new Dictionary<string, string>(lot, StringComparer.Ordinal)
                    {
                        ["sims_familymemberX_name"] = family.MemberNames[member],
                        ["sims_familymemberX_number"] = (member + 1).ToString(),
                    };
                    if (member < family.MemberProfiles.Count && family.MemberProfiles[member] is { } profile)
                    {
                        memberValues["sims_familymemberX_gender"] = profile.Gender ?? "";
                        memberValues["sims_familymemberX_species"] = profile.Species ?? "";
                        memberValues["sims_familymemberX_kidadult"] = profile.Age ?? "";
                        memberValues["sims_familymemberX_personality_neat"] = profile.Neat?.ToString() ?? "";
                        memberValues["sims_familymemberX_personality_outgoing"] = profile.Outgoing?.ToString() ?? "";
                        memberValues["sims_familymemberX_personality_active"] = profile.Active?.ToString() ?? "";
                        memberValues["sims_familymemberX_personality_playful"] = profile.Playful?.ToString() ?? "";
                        memberValues["sims_familymemberX_personality_nice"] = profile.Nice?.ToString() ?? "";
                        memberValues["sims_familymemberX_skill_cooking"] = profile.Cooking?.ToString() ?? "";
                        memberValues["sims_familymemberX_skill_mechanical"] = profile.Mechanical?.ToString() ?? "";
                        memberValues["sims_familymemberX_skill_charisma"] = profile.Charisma?.ToString() ?? "";
                        memberValues["sims_familymemberX_skill_body"] = profile.Body?.ToString() ?? "";
                        memberValues["sims_familymemberX_skill_logic"] = profile.Logic?.ToString() ?? "";
                        memberValues["sims_familymemberX_skill_creativity"] = profile.Creativity?.ToString() ?? "";
                        memberValues["sims_familymemberX_career"] = profile.Career ?? "";
                        memberValues["sims_familymemberX_job"] = profile.Job ?? "";
                        memberValues["sims_familymemberX_performance"] = profile.PerformanceLabel ?? profile.Performance?.ToString() ?? "";
                        memberValues["sims_familymemberX_salary"] = profile.Salary?.ToString() ?? "";
                        memberValues["sims_familymemberX_zodiac"] =
                            profile.Zodiac is >= 1 and <= 12 ? ZodiacSigns[profile.Zodiac.Value - 1] : "";
                        memberValues["sims_familymemberX_description"] = profile.Description ?? "";
                    }
                    string memberPage = Fill(memberTemplate, memberValues)
                        .Replace("familymemberX", $"familymember{member + 1}", StringComparison.Ordinal);
                    WriteAllTextAtomic(
                        Path.Combine(directory, $"familymember{member + 1}.html"),
                        memberPage,
                        Encoding.Latin1);
                }
            }

            if (family.ScrapbookPhotos.Count > 0)
            {
                int total = family.ScrapbookPhotos.Count;
                var scrapbookValues = new Dictionary<string, string>(lot, StringComparer.Ordinal)
                {
                    ["sims_scrapbook_numpages"] = total.ToString(),
                };
                for (int i = 0; i < total; i++)
                    scrapbookValues[$"sims_scrapbook{i + 1}_caption"] = family.ScrapbookPhotos[i].Caption;

                string scrapbookPage = Fill(Path.Combine(theme, "scrapbookX.html"), scrapbookValues)
                    .Replace("scrapbookX", "scrapbook1", StringComparison.Ordinal);
                WriteAllTextAtomic(Path.Combine(directory, "scrapbook1.html"), scrapbookPage, Encoding.Latin1);

                for (int i = 0; i < total; i++)
                {
                    int page = i + 1;
                    var popupValues = new Dictionary<string, string>(lot, StringComparer.Ordinal)
                    {
                        ["sims_scrapbook_numpages"] = total.ToString(),
                        ["sims_scrapbook_currpagenum"] = page.ToString(),
                        ["sims_scrapbookX_snapshot"] = $"scrapbook{page}.jpg",
                        ["sims_scrapbookX_caption"] = family.ScrapbookPhotos[i].Caption,
                    };
                    string popupPage = Fill(Path.Combine(theme, "scrapbook_popupX.html"), popupValues)
                        .Replace("scrapbook_popupX", $"scrapbook_popup{page}", StringComparison.Ordinal);
                    WriteAllTextAtomic(Path.Combine(directory, $"scrapbook_popup{page}.html"), popupPage, Encoding.Latin1);
                }
            }
        }

        return Path.Combine(destination, "neighborhood.html");
    }

    private static void WriteFamilyThumbnails(IReadOnlyList<byte[]?> thumbnails, string directory)
    {
        Directory.CreateDirectory(directory);
        var writer = new StbImageWriteSharp.ImageWriter();
        int count = Math.Min(8, thumbnails.Count);
        for (int index = 0; index < count; index++)
        {
            byte[]? data = thumbnails[index];
            if (data is null)
                continue;

            SimsBitmap bitmap;
            try
            {
                bitmap = new SimsBitmap(data);
            }
            catch
            {
                continue;
            }

            if (bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Rgba.Length < bitmap.Width * bitmap.Height * 4)
                continue;

            using var ms = new MemoryStream();
            writer.WriteJpg(
                bitmap.Rgba,
                bitmap.Width,
                bitmap.Height,
                StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha,
                ms,
                90);
            WriteAllBytesAtomic(Path.Combine(directory, $"family{index + 1}_face.jpg"), ms.ToArray());
        }
    }

    private static void WriteScrapbook(IReadOnlyList<ScrapbookPhoto> photos, string directory)
    {
        if (photos.Count == 0)
            return;

        Directory.CreateDirectory(directory);
        for (int offset = 0; offset < photos.Count; offset++)
        {
            ScrapbookPhoto photo = photos[offset];
            if (photo.Image is not null)
                WriteAllBytesAtomic(Path.Combine(directory, $"scrapbook{offset + 1}.jpg"), photo.Image);

            byte[]? thumbnail = photo.Thumbnail ?? (photo.Image is not null ? ScrapbookThumbnail(photo.Image) : null);
            if (thumbnail is not null)
                WriteAllBytesAtomic(Path.Combine(directory, $"scrapbook{offset + 1}_thumb.jpg"), thumbnail);
        }
    }

    private static void WriteHouseViews(HouseViews? views, string directory)
    {
        if (views is null)
            return;

        Directory.CreateDirectory(directory);
        (string Name, byte[]? Image)[] entries =
        [
            ("house-thumb.jpg", views.Thumbnail),
            ("house-exterior.jpg", views.Exterior),
            ("house-floor1.jpg", views.Floor1),
            ("house-floor2.jpg", views.Floor2),
        ];
        foreach ((string name, byte[]? image) in entries)
        {
            if (image is not null)
                WriteAllBytesAtomic(Path.Combine(directory, name), image);
        }
    }

    private static byte[]? ScrapbookThumbnail(byte[] data)
    {
        StbImageSharp.ImageResult decoded;
        try
        {
            decoded = StbImageSharp.ImageResult.FromMemory(data, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        }
        catch
        {
            return null;
        }

        int srcW = decoded.Width;
        int srcH = decoded.Height;
        if (srcW <= 0 || srcH <= 0 || decoded.Data.Length < srcW * srcH * 4)
            return null;

        int dstW = srcW;
        int dstH = srcH;
        int maxDim = Math.Max(srcW, srcH);
        if (maxDim > 100)
        {
            double scale = 100.0 / maxDim;
            dstW = Math.Max(1, (int)Math.Round(srcW * scale));
            dstH = Math.Max(1, (int)Math.Round(srcH * scale));
        }

        byte[] outputPixels;
        if (dstW == srcW && dstH == srcH)
        {
            outputPixels = decoded.Data;
        }
        else
        {
            outputPixels = new byte[dstW * dstH * 4];
            byte[] src = decoded.Data;
            for (int dy = 0; dy < dstH; dy++)
            {
                int sy0 = dy * srcH / dstH;
                int sy1 = Math.Max(sy0 + 1, (dy + 1) * srcH / dstH);
                if (sy1 > srcH) sy1 = srcH;

                for (int dx = 0; dx < dstW; dx++)
                {
                    int sx0 = dx * srcW / dstW;
                    int sx1 = Math.Max(sx0 + 1, (dx + 1) * srcW / dstW);
                    if (sx1 > srcW) sx1 = srcW;

                    int sumR = 0, sumG = 0, sumB = 0, sumA = 0, count = 0;
                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        int rowOffset = sy * srcW * 4;
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            int idx = rowOffset + sx * 4;
                            sumR += src[idx];
                            sumG += src[idx + 1];
                            sumB += src[idx + 2];
                            sumA += src[idx + 3];
                            count++;
                        }
                    }

                    int dstIdx = (dy * dstW + dx) * 4;
                    outputPixels[dstIdx] = (byte)(sumR / count);
                    outputPixels[dstIdx + 1] = (byte)(sumG / count);
                    outputPixels[dstIdx + 2] = (byte)(sumB / count);
                    outputPixels[dstIdx + 3] = (byte)(sumA / count);
                }
            }
        }

        using var ms = new MemoryStream();
        var writer = new StbImageWriteSharp.ImageWriter();
        writer.WriteJpg(
            outputPixels,
            dstW,
            dstH,
            StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha,
            ms,
            90);
        return ms.ToArray();
    }

    /// <summary>Replaces <c>^^^^tag^^^^</c> template placeholders, clearing any tags without matching
    /// values.</summary>
    public static string Fill(string template, IReadOnlyDictionary<string, string> values)
    {
        byte[] data = File.ReadAllBytes(template);
        string text = Encoding.Latin1.GetString(data);
        var result = new StringBuilder(text.Length);
        int pos = 0;
        while (pos < text.Length)
        {
            int start = text.IndexOf("^^^^", pos, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(text, pos, text.Length - pos);
                break;
            }
            result.Append(text, pos, start - pos);
            int keyStart = start + 4;
            int end = text.IndexOf("^^^^", keyStart, StringComparison.Ordinal);
            if (end < 0)
            {
                result.Append(text, start, text.Length - start);
                break;
            }
            string key = text[keyStart..end];
            if (values.TryGetValue(key, out string? value))
                result.Append(value);
            pos = end + 4;
        }
        return result.ToString();
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (string subDir in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectory(subDir, Path.Combine(destinationDir, Path.GetFileName(subDir)));
        }
    }

    private static void WriteAllTextAtomic(string path, string content, Encoding encoding) =>
        WriteAllBytesAtomic(path, encoding.GetBytes(content));

    private static void WriteAllBytesAtomic(string path, byte[] bytes)
    {
        string tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static bool ContainsSymlinkBetween(string rootDir, string targetFile)
    {
        string normalizedRoot = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? current = Path.GetDirectoryName(Path.GetFullPath(targetFile));
        while (!string.IsNullOrEmpty(current) && current.Length > normalizedRoot.Length)
        {
            var dirInfo = new DirectoryInfo(current);
            if (!dirInfo.Exists || (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    private static bool BytesEqual(byte[]? a, byte[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.AsSpan().SequenceEqual(b);
    }

    private static void AddBytes(ref HashCode hc, byte[]? bytes)
    {
        if (bytes is null)
        {
            hc.Add(-1);
            return;
        }
        hc.Add(bytes.Length);
        hc.AddBytes(bytes);
    }
}
