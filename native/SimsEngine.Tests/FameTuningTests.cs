using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class FameTuningTests
{
    [Fact]
    public void ReadsTheThreeOriginalStringTablesAndTransposesSkillRows()
    {
        var iff = FameIff(
            Enumerable.Range(0, 11).Select(value => value.ToString()).ToArray(),
            Enumerable.Range(100, 11).Select(value => value.ToString()).ToArray(),
            Enumerable.Range(0, 11).Select(star => string.Join(',', Enumerable.Range(0, 6).Select(skill => star * 10 + skill))).ToArray());

        var tuning = FameTuning.Read(iff);
        var target = GenericSimsCallRecordTarget.FromFameTuning(tuning);

        Assert.Equal(Enumerable.Range(0, 11), tuning.FameStarThresholds);
        Assert.Equal(Enumerable.Range(100, 11), tuning.FameFriendThresholds);
        Assert.Equal(0, tuning.FameSkillThresholds[0][0]);
        Assert.Equal(5, tuning.FameSkillThresholds[5][0]);
        Assert.Equal(100, tuning.FameSkillThresholds[0][10]);
        Assert.Equal(105, tuning.FameSkillThresholds[5][10]);
        Assert.Same(tuning.FameStarThresholds, target.FameStarThresholds);
        Assert.Same(tuning.FameSkillThresholds, target.FameSkillThresholds);
        Assert.Same(tuning.FameFriendThresholds, target.FameFriendThresholds);
    }

    [Fact]
    public void RejectsRowsThatDoNotContainAllSixSkillThresholds()
    {
        var iff = FameIff(
            Enumerable.Repeat("0", 11).ToArray(),
            Enumerable.Repeat("0", 11).ToArray(),
            Enumerable.Repeat("1,2,3,4,5", 11).ToArray());

        var error = Assert.Throws<InvalidDataException>(() => FameTuning.Read(iff));

        Assert.Contains("STR# 6 row 0", error.Message);
    }

    private static IffFile FameIff(string[] scores, string[] friends, string[] skills) => new(
        ChunkBuilder.File(
            ("STR#", 5, StringTable(scores)),
            ("STR#", 4, StringTable(friends)),
            ("STR#", 6, StringTable(skills))),
        "fame.iff");

    private static byte[] StringTable(IEnumerable<string> values)
    {
        var entries = values.ToArray();
        var chunk = new ChunkBuilder().S16(-1).S16(checked((short)entries.Length));
        foreach (var value in entries) chunk.CString(value);
        return chunk.ToArray();
    }
}
