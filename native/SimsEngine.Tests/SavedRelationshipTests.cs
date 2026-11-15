using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class SavedRelationshipTests
{
    [Fact]
    public void SaveUsesLiveValuesAndLengthsWhileRetainingSparseSlots()
    {
        ObjectRelationship?[] original = [null, new(22, [4, 8]), null, new(33, [9])];
        var table = new RelationshipTable();
        table.Resize(22, 3);
        table.Set(22, 0, -123456);
        table.Set(22, 2, int.MaxValue);
        table.Resize(33, 0);
        table.Resize(44, 1);
        table.Set(44, 0, int.MinValue);

        var saved = table.WriteSavedEntries(original);
        Assert.Equal(5, saved.Count);
        Assert.Null(saved[0]);
        Assert.Null(saved[2]);
        Assert.Equal(22, saved[1]!.Value0x0C);
        Assert.Equal(new[] { -123456, 0, int.MaxValue }, saved[1]!.Values);
        Assert.Empty(saved[3]!.Values);
        Assert.Equal(44, saved[4]!.Value0x0C);
        Assert.Equal(new[] { int.MinValue }, saved[4]!.Values);
        table.Set(22, 0, 1);
        Assert.Equal(-123456, saved[1]!.Values[0]);
        Assert.Equal(new[] { 4, 8 }, original[1]!.Values);
    }
}
