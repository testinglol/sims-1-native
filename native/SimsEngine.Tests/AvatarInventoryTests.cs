using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class AvatarInventoryTests
{
    [Fact]
    public void Add_AppendsAndIncrementsWithCap()
    {
        var list = new AvatarInventory();
        Assert.True(list.Add(7, 0x300, 3));
        Assert.True(list.Add(7, 0x300, 2));
        Assert.Equal([(7, 0x300, (short)5)], list.Entries.Select(entry => (entry.Key1, entry.Key2, entry.Count)));
        list.Add(7, 0x300, short.MaxValue);
        list.Add(7, 0x300, short.MaxValue);
        Assert.Equal((short)0x7fff, list.Entries[0].Count);
    }

    [Fact]
    public void Add_ZeroCountChangesNothing()
    {
        var list = new AvatarInventory();
        Assert.True(list.Add(7, 1, 0));
        Assert.Empty(list.Entries);
    }

    [Fact]
    public void Remove_DecrementsOneAndDeletesAtZero()
    {
        var list = new AvatarInventory();
        list.Add(7, 1, 2);
        list.Add(8, 1, 3);
        Assert.False(list.Remove(9, 1, 1));
        Assert.True(list.Remove(7, 1, 0));
        Assert.True(list.Remove(7, 1, 1));
        Assert.Equal([(7, 1, (short)1), (8, 1, (short)3)], list.Entries.Select(entry => (entry.Key1, entry.Key2, entry.Count)));
        Assert.True(list.Remove(7, 1, 5));
        Assert.Equal([(8, 1, (short)3)], list.Entries.Select(entry => (entry.Key1, entry.Key2, entry.Count)));
    }

    [Fact]
    public void Remove_MinusOneDeletesWholeEntry()
    {
        var list = new AvatarInventory();
        list.Add(7, 1, 8);
        Assert.True(list.Remove(7, 1, -1));
        Assert.Empty(list.Entries);
    }

    [Fact]
    public void Adjust_SubtractsAndDropsAtZero()
    {
        var list = new AvatarInventory();
        list.Add(7, 1, 10);
        list.Add(8, 1, 2);
        Assert.True(list.Adjust(0, 5));
        Assert.Equal((short)5, list.Entries[0].Count);
        Assert.Equal(2, list.Entries.Count);
        Assert.True(list.Adjust(1, 2));
        Assert.Single(list.Entries);
    }

    [Fact]
    public void Adjust_ZeroCountAndNegativeCountAndOutOfRange()
    {
        var list = new AvatarInventory();
        list.Add(7, 1, 4);
        Assert.True(list.Adjust(5, 0));
        Assert.Equal((short)4, list.Entries[0].Count);
        Assert.False(list.Adjust(5, 1));
        Assert.False(list.Adjust(-1, 1));
        Assert.True(list.Adjust(0, -1));
        Assert.Empty(list.Entries);
    }

    [Fact]
    public void FindIndex_ReturnsCountAndIndex()
    {
        var list = new AvatarInventory();
        list.Add(7, 1, 4);
        Assert.Equal(4, list.FindIndex(7, 1, out var at));
        Assert.Equal(0, at);
        Assert.Equal(0, list.FindIndex(7, 2, out at));
        Assert.Equal(-1, at);
    }

    [Fact]
    public void FindNext_WalksForwardWithWildcard()
    {
        var list = new AvatarInventory();
        list.Add(7, 1, 4);
        list.Add(7, 2, 6);
        list.Add(8, 1, 9);
        short cursor = -1;
        Assert.Equal(4, list.FindNext(7, ref cursor, 0));
        Assert.Equal(0, cursor);
        Assert.Equal(6, list.FindNext(7, ref cursor, 0));
        Assert.Equal(1, cursor);
        Assert.Equal(0, list.FindNext(7, ref cursor, 0));
        Assert.Equal(-1, cursor);
        cursor = -1;
        Assert.Equal(6, list.FindNext(7, ref cursor, 2));
        Assert.Equal(1, cursor);
    }

    [Fact]
    public void FilterOut_RemovesEveryMatchingFirstKey()
    {
        var list = new AvatarInventory();
        list.Add(1, 9, 1);
        list.Add(3, 9, 2);
        list.Add(1, 10, 3);
        list.FilterOut(1);
        Assert.Equal([(3, 9, (short)2)], list.Entries.Select(entry => (entry.Key1, entry.Key2, entry.Count)));
    }
}
