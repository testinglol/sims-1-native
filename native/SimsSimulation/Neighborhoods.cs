using System.Globalization;
using SimsEngine.Formats;
using SimsEngine.Simulation;

/// <summary>
/// The Complete Collection's numbered neighborhoods. The original scans for <c>UserData</c> folders, reads the number
/// after the name (plain <c>UserData</c> is neighborhood 1), keeps them in a sorted map, remembers the current one in
/// <c>SIMS_CURRENT_NEIGHBORHOOD_NUM</c>, and the banner's Previous/Next buttons step through them with wrap-around
/// (<c>0x0042a53a</c>, <c>0x0042a368</c>, <c>0x0042a3ca</c>, <c>0x0042a3f4</c>).
/// </summary>
public static class Neighborhoods
{
    public const int Maximum = 99;

    /// <summary>The neighborhood the host was started with; neighborhood 1 unless <c>--neighborhood</c> chose another.</summary>
    public static int Current { get; set; } = 1;

    public static string DirectoryName(int number) =>
        number is < 1 or > Maximum ? throw new ArgumentOutOfRangeException(nameof(number), "Neighborhood numbers run from 1 through 99")
            : number == 1 ? "UserData" : "UserData" + number.ToString(CultureInfo.InvariantCulture);


    /// <summary>The current neighborhood's folder inside a native profile.</summary>
    internal static string UserData(string profile)
    {
        var canonical = Path.Combine(profile, DirectoryName(Current));
        // Sims.exe keeps the scanned directory path for numeric UserData1, even though UserData is the
        // usual spelling for neighborhood 1. Imported profiles normalize that alias; direct profiles need its path.
        var numberedBase = Path.Combine(profile, "UserData1");
        return Current == 1 && !Directory.Exists(canonical) && Directory.Exists(numberedBase) ? numberedBase : canonical;
    }

    /// <summary>Loads the source-backed neighborhood variable block from NGBH #1. Sims.exe FUN_00429CC7
    /// reads that block during Generic Sims Call 0/8/9 initialization; FUN_00425540 supplies the versioned
    /// defaults and values. Profiles that do not contain a neighborhood file retain the world's zero defaults.</summary>
    internal static void LoadScriptVariables(SimulationWorld world, string? neighborhoodPath)
    {
        if (neighborhoodPath is null || !File.Exists(neighborhoodPath)) return;
        var file = IffFile.Open(neighborhoodPath);
        var chunk = file.Find(ResourceType.Parse("NGBH"), 1)
            ?? throw new InvalidDataException($"{neighborhoodPath} has no NGBH #1 resource.");
        world.LoadNeighborhoodData(NeighborhoodData.Read(file, chunk));
    }

    /// <summary>
    /// The file name a save slot is stored under. Neighborhood 1 keeps its existing slot names; other neighborhoods
    /// prefix theirs so slots never collide. Slot names that already look prefixed are reserved for that reason.
    /// </summary>
    public static string StorageSlot(string slot, int number)
    {
        if (IsReservedSlot(slot)) throw new ArgumentException("Save slot names starting with \"neighborhood<number>-\" are reserved");
        DirectoryName(number);
        return number == 1 ? slot : $"neighborhood{number.ToString(CultureInfo.InvariantCulture)}-{slot}";
    }

    public static bool IsReservedSlot(string slot)

    {
        if (!slot.StartsWith("neighborhood", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = slot["neighborhood".Length..];
        var digits = rest.TakeWhile(char.IsAsciiDigit).Count();
        return digits > 0 && rest.Length > digits && rest[digits] == '-';
    }

    /// <summary>Installed neighborhoods in a profile, in the original's ascending order.</summary>
    internal static int[] Installed(string profile)
    {
        if (!Directory.Exists(profile)) return [];
        return Directory.GetDirectories(profile)
            .Select(path => Number(Path.GetFileName(path)))
            // FUN_0042a53a / Neighborhood::CheckForNewUserDataDirectories enumerate UserData directories
            // and build the neighborhood map from their numeric suffix. They do not require Neighborhood.iff
            // to exist before listing a directory; switching subsequently calls UnlockUserData.
            .Where(number => number.HasValue)
            .Select(number => number!.Value)
            .Distinct()
            .Order()
            .ToArray();
    }

    /// <summary>The neighborhood number of a folder name, or null when it is not a neighborhood folder.</summary>
    internal static int? Number(string? name)
    {
        if (name == null || !name.StartsWith("UserData", StringComparison.OrdinalIgnoreCase)) return null;
        var suffix = name["UserData".Length..];
        if (suffix.Length == 0) return 1;
        // CheckForNewUserDataDirectories parses every numeric suffix and accepts 1..99. UserData is the
        // canonical spelling for 1, but the original directory scan also recognizes UserData1.
        return suffix.All(char.IsAsciiDigit) && int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
               number is >= 1 and <= Maximum ? number : null;
    }

    /// <summary>Ports the state-class initialization in Sims.exe FUN_00424850. It rebuilds the ordered
    /// state-to-class vector from LotZoning.iff: residential maps to class 1 and community to class 2.
    /// The English table is used because the zoning tokens are data, not localized display strings.</summary>
    internal static IReadOnlyList<StateClassEntry> StateClasses(string userData)
    {
        var path = Path.Combine(userData, "LotZoning.iff");
        // Imported Complete Collection profiles preserve the original UserData folder inside the profile's UserData
        // root while their active Houses/ and Characters/ directories are also staged at that root. Sims.exe reads
        // LotZoning from the selected neighborhood root (FUN_00424850); accept that preserved nested root when the
        // staged profile does not have the resource beside Houses/.
        if (!File.Exists(path)) path = Path.Combine(userData, "UserData", "LotZoning.iff");
        if (!File.Exists(path)) return [];
        var file = IffFile.Open(path);
        var zoningChunk = file.Find(ResourceType.Str, 1)
            ?? throw new InvalidDataException($"{path} has no STR# 1 zoning table.");
        var zoning = StringTable.Read(file, zoningChunk).ForLanguage(1);
        var result = new List<StateClassEntry>();
        for (var index = 0; index < zoning.Count; index++)
        {
            var fields = zoning[index].Split(',', 2);
            if (fields.Length != 2 || !int.TryParse(fields[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var state)) continue;
            var zoningType = fields[1].Trim();
            if (zoningType.Equals("community", StringComparison.OrdinalIgnoreCase))
                result.Add(new StateClassEntry(state, 2));
            else if (zoningType.Equals("residential", StringComparison.OrdinalIgnoreCase))
                result.Add(new StateClassEntry(state, 1));
        }
        return result;
    }

    internal static bool IsCommunityLot(string userData, short house) =>
        StateClasses(userData).Any(entry => entry.State == house && entry.Class == 2);

    private static readonly float[][] ZodiacPersonalityProfiles =
    [
        [8, 3, 3, 5, 6], [5, 4, 8, 5, 3], [7, 3, 3, 4, 8], [3, 6, 4, 6, 6],
        [10, 3, 4, 4, 4], [2, 5, 3, 9, 6], [8, 7, 6, 2, 2], [5, 3, 3, 6, 8],
        [3, 4, 7, 2, 9], [4, 5, 8, 7, 1], [4, 6, 7, 4, 4], [3, 7, 3, 5, 7]
    ];

    /// <summary>Ports Mac ComputeZodiacSign for native family export: compare the five stored personality
    /// dimensions with the twelve sign profiles. The export derives the sign rather than using TS1Zodiac.</summary>
    internal static int? ComputeExportZodiac(IReadOnlyList<short>? data)
    {
        if (data is not { Count: > 7 }) return null;
        var traitIndices = new[] { 6, 2, 5, 7, 3 };
        var traits = traitIndices.Select(index => data[index] * 0.01f).ToArray();
        var bestSign = 1;
        var bestDistance = float.PositiveInfinity;
        for (var sign = 0; sign < ZodiacPersonalityProfiles.Length; sign++)
        {
            var distance = 0f;
            for (var trait = 0; trait < traits.Length; trait++)
            {
                var delta = traits[trait] - ZodiacPersonalityProfiles[sign][trait];
                distance += delta * delta;
            }
            if (distance < bestDistance) { bestDistance = distance; bestSign = sign + 1; }
        }
        return bestSign;
    }
}
