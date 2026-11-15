using System.Globalization;
using SimsEngine.Formats;

/// <summary>
/// The destination pickers' remembered filter: STR# 6 of the neighborhood's LotLocations, one string per picker. Mac
/// cWinDowntown uses string 1, cWinVacation 2, cWinNeighborhoodUL 3, cWinStudiotown 4 and cWinMagicland 5 (the same
/// numbers as Sims.exe FUN_0054db45, FUN_0056c9f8, FUN_0055e143, FUN_00568306 and FUN_00555dd4).
/// </summary>
internal static class TravelFilter
{
    /// <summary>cWinNeighborhoodUL's string, used by the Unleashed (Old Town) destination picker.</summary>
    internal const int Unleashed = 3;

    private static readonly ResourceType Strings = ResourceType.Parse("STR#");

    /// <summary>LoadCurrentFilter: <paramref name="fallback"/> (2, or 0x400 for Studio Town and Makin' Magic) when the
    /// file or STR# 6 cannot be read; otherwise the string read as a C integer (atoi), so a missing string gives 0.</summary>
    internal static int Load(string userData, int number, byte language, int fallback = 2)
    {
        var path = Path.Combine(userData, "LotLocations.iff");
        if (!File.Exists(path)) return fallback;
        var file = IffFile.Open(path);
        if (file.Find(Strings, 6) is not { } chunk) return fallback;
        var strings = StringTable.Read(file, chunk).ForLanguage(language);
        return number <= strings.Count ? Atoi(strings[number - 1]) : 0;
    }

    /// <summary>SaveCurrentFilter, run when the picker shuts down: nothing for filter 0 or when STR# 6 cannot be
    /// read; otherwise the string becomes the filter formatted with "%d" and the table is saved.</summary>
    internal static void Save(string userData, int number, int filter, byte language)
    {
        if (filter == 0) return;
        var path = Path.Combine(userData, "LotLocations.iff");
        if (!File.Exists(path)) return;
        var file = IffFile.Open(path);
        if (file.Find(Strings, 6) is not { } chunk) return;
        var data = StringTable.Read(file, chunk).WriteWithValue(number, filter.ToString(CultureInfo.InvariantCulture), language);
        var temporary = path + ".save-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temporary, file.WithChunkDataResized(Strings, 6, data).Bytes.ToArray());
        File.Move(temporary, path, true);
    }

    private static int Atoi(string text)
    {
        var at = 0;
        while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
        var negative = at < text.Length && text[at] == '-';
        if (at < text.Length && text[at] is '-' or '+') at++;
        var value = 0;
        while (at < text.Length && text[at] is >= '0' and <= '9') value = unchecked(value * 10 + (text[at++] - '0'));
        return negative ? -value : value;
    }
}
