using System.Globalization;
using SimsEngine.Formats;
using SimsEngine.Simulation;

/// <summary>Discovers and manages numbered neighborhood directories, treating unnumbered UserData folders
/// as neighborhood 1.</summary>
public static class Neighborhoods
{
    public const int Maximum = 99;

    public static int Current { get; set; } = 1;

    public static string DirectoryName(int number) =>
        number is < 1 or > Maximum ? throw new ArgumentOutOfRangeException(nameof(number), "Neighborhood numbers run from 1 through 99")
            : number == 1 ? "UserData" : "UserData" + number.ToString(CultureInfo.InvariantCulture);

    internal static string UserData(string profile)
    {
        var canonical = Path.Combine(profile, DirectoryName(Current));
        var numberedBase = Path.Combine(profile, "UserData1");
        return Current == 1 && !Directory.Exists(canonical) && Directory.Exists(numberedBase) ? numberedBase : canonical;
    }

    /// <summary>Loads persistent neighborhood script variables from the NGBH chunk, falling back to zero
    /// defaults if absent.</summary>
    internal static void LoadScriptVariables(SimulationWorld world, string? neighborhoodPath)
    {
        if (neighborhoodPath is null || !File.Exists(neighborhoodPath)) return;
        var file = IffFile.Open(neighborhoodPath);
        var chunk = file.Find(ResourceType.Parse("NGBH"), 1)
            ?? throw new InvalidDataException($"{neighborhoodPath} has no NGBH #1 resource.");
        world.LoadNeighborhoodData(NeighborhoodData.Read(file, chunk));
    }

    /// <summary>Generates storage slot filenames, prefixing slots for neighborhoods beyond neighborhood 1
    /// to avoid collision.</summary>
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

    internal static int[] Installed(string profile)
    {
        if (!Directory.Exists(profile)) return [];
        return Directory.GetDirectories(profile)
            .Select(path => Number(Path.GetFileName(path)))
            .Where(number => number.HasValue)
            .Select(number => number!.Value)
            .Distinct()
            .Order()
            .ToArray();
    }

    internal static int? Number(string? name)
    {
        if (name == null || !name.StartsWith("UserData", StringComparison.OrdinalIgnoreCase)) return null;
        var suffix = name["UserData".Length..];
        if (suffix.Length == 0) return 1;
        return suffix.All(char.IsAsciiDigit) && int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
               number is >= 1 and <= Maximum ? number : null;
    }

    /// <summary>Initializes lot zoning classes from LotZoning.iff, mapping residential tokens to class 1
    /// and community tokens to class 2.</summary>
    internal static IReadOnlyList<StateClassEntry> StateClasses(string userData)
    {
        var path = Path.Combine(userData, "LotZoning.iff");
        // Falls back to the nested UserData directory when locating LotZoning.iff in staged user profiles.
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

    /// <summary>Derives an avatar's zodiac sign by matching its five personality motive values against the
    /// twelve astrological profiles.</summary>
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
