using System.Globalization;

namespace SimsEngine.Formats;

/// <summary>The fame promotion tables loaded by Sims.exe from GameData/fame.iff (FUN_00404e49): STR# 5 contains
/// score thresholds, STR# 4 friend-power thresholds, and STR# 6 has one comma-separated row of six skill thresholds
/// per star level.</summary>
public sealed record FameTuning(
    IReadOnlyList<int> FameStarThresholds,
    IReadOnlyList<int> FameFriendThresholds,
    IReadOnlyList<IReadOnlyList<int>> FameSkillThresholds)
{
    public static FameTuning Read(string path) => Read(IffFile.Open(path));

    public static FameTuning Read(IffFile iff)
    {
        ArgumentNullException.ThrowIfNull(iff);
        var score = Integers(iff, 5);
        var friends = Integers(iff, 4);
        var skillRows = Strings(iff, 6).Select((value, index) => ParseSkillRow(value, index, iff.Source)).ToArray();
        if (score.Length != 11 || friends.Length != 11 || skillRows.Length != 11)
            throw new InvalidDataException($"{iff.Source}: fame.iff STR# 4, 5 and 6 must each contain 11 star levels.");

        var skills = Enumerable.Range(0, 6)
            .Select(skill => (IReadOnlyList<int>)skillRows.Select(row => row[skill]).ToArray())
            .ToArray();
        return new FameTuning(score, friends, skills);
    }

    private static int[] Integers(IffFile iff, ushort id) => Strings(iff, id)
        .Select((value, index) => int.Parse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture))
        .ToArray();

    private static IReadOnlyList<string> Strings(IffFile iff, ushort id)
    {
        var chunk = iff.Find(ResourceType.Str, id)
            ?? throw new InvalidDataException($"{iff.Source}: fame.iff is missing STR# {id}.");
        var table = StringTable.Read(iff, chunk);
        if (table.Truncated) throw new InvalidDataException($"{iff.Source}: fame.iff STR# {id} is truncated.");
        return table.ForLanguage(1);
    }

    private static int[] ParseSkillRow(string value, int row, string source)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 6)
            throw new InvalidDataException($"{source}: fame.iff STR# 6 row {row} must contain six skill thresholds.");
        return parts.Select(part => int.Parse(part, NumberStyles.Integer, CultureInfo.InvariantCulture)).ToArray();
    }
}
