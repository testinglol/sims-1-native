using System.Globalization;
using SimsEngine.Formats;

/// <summary>Stores destination picker filter selections in STR# 6 of the neighborhood LotLocations file,
/// with string indices corresponding to each destination picker.</summary>
internal static class TravelFilter
{
    internal const int Unleashed = 3;

    private static readonly ResourceType Strings = ResourceType.Parse("STR#");

    /// <summary>Loads the saved travel filter from STR# 6, falling back to 2 (or 0x400 for Studio Town and
    /// Magic Town) if missing or unreadable.</summary>
    internal static int Load(string userData, int number, byte language, int fallback = 2)
    {
        var path = Path.Combine(userData, "LotLocations.iff");
        if (!File.Exists(path)) return fallback;
        var file = IffFile.Open(path);
        if (file.Find(Strings, 6) is not { } chunk) return fallback;
        var strings = StringTable.Read(file, chunk).ForLanguage(language);
        return number <= strings.Count ? Atoi(strings[number - 1]) : 0;
    }

    /// <summary>Saves the active travel filter formatted as a decimal string into STR# 6 upon picker
    /// shutdown, skipping filter 0.</summary>
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
