using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class InteractionScopeTableTests
{
    [Fact]
    public void PersonTypeRecordInitializationAssignsStableOneBasedScopeKeys()
    {
        var world = new SimulationWorld();
        var first = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(0x1234),
        });
        var second = world.Add(new XPerson(2, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(0x1234),
        });
        var third = world.Add(new XPerson(3, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(0x5678),
        });

        Assert.Equal((short)1, first.InteractionScopeKey5CA);
        Assert.Equal((short)1, second.InteractionScopeKey5CA);
        Assert.Equal((short)2, third.InteractionScopeKey5CA);
        Assert.Equal(2, world.PersonTypeRecordSlots.Count);
        Assert.Same(world.InteractionExecutorScope(first), world.InteractionExecutorScope(second));
        Assert.NotSame(world.InteractionExecutorScope(first), world.InteractionExecutorScope(third));
    }

    [Fact]
    public void WorldLazilyCreatesAndSharesScopesByTheObjectsRawKey()
    {
        var world = new SimulationWorld();
        var first = world.Add(new XObject(1, PrimitiveTestWorld.Definition()) { InteractionScopeKey5CA = 23 });
        var alias = world.Add(new XObject(2, PrimitiveTestWorld.Definition()) { InteractionScopeKey5CA = 23 });

        var firstScope = world.InteractionExecutorScope(first);
        var aliasScope = world.InteractionExecutorScope(alias);

        Assert.Same(firstScope, aliasScope);
        Assert.Equal(0, firstScope.Count);
        Assert.Single(world.InteractionScopes);
    }

    [Fact]
    public void CountValueCountsAllMatchingEntriesRegardlessOfMode()
    {
        var table = Table(
            new(2, 1, 11),
            new(2, 3, 12),
            new(7, 1, 13));

        Assert.Equal(2, table.CountValue(2));
        Assert.Equal(0, table.CountValue(5));
    }

    [Fact]
    public void FindNextUsesCursorAndSpecificMode()
    {
        var table = Table(
            new(2, 1, 0x1234),
            new(2, 3, 0x5678),
            new(2, 3, 0x9abc));
        short cursor = -1;

        Assert.True(table.TryFindNext(2, ref cursor, 3, out var result));
        Assert.Equal((short)1, cursor);
        Assert.Equal((ushort)0x5678, result);
        Assert.True(table.TryFindNext(2, ref cursor, 3, out result));
        Assert.Equal((short)2, cursor);
        Assert.Equal((ushort)0x9abc, result);
    }

    [Fact]
    public void ZeroModeMatchesAnyModeAndMissResetsCursor()
    {
        var table = Table(new(2, 4, 0x1234), new(9, 0, 0x5678));
        short cursor = -1;

        Assert.True(table.TryFindNext(2, ref cursor, 0, out var result));
        Assert.Equal((ushort)0x1234, result);
        Assert.False(table.TryFindNext(2, ref cursor, 0, out result));
        Assert.Equal((short)-1, cursor);
        Assert.Equal((ushort)0, result);
    }

    [Fact]
    public void GetRejectsNegativeAndPastEndIndices()
    {
        var expected = new InteractionScopeEntry(2, 4, 0x1234);
        var table = Table(expected);

        Assert.True(table.TryGet(0, out var actual));
        Assert.Equal(expected, actual);
        Assert.False(table.TryGet(-1, out _));
        Assert.False(table.TryGet(1, out _));
    }

    [Fact]
    public void AdvanceWithNegativeDeltaRemovesOnlySelectedEntry()
    {
        var first = new InteractionScopeEntry(2, 4, 0x1234);
        var removed = new InteractionScopeEntry(2, 9, 0x5678);
        var last = new InteractionScopeEntry(7, 1, 0x9abc);
        var table = Table(first, removed, last);

        Assert.True(table.TryAdvance(1, -1));

        Assert.Equal(2, table.Count);
        Assert.True(table.TryGet(0, out var actualFirst));
        Assert.Equal(first, actualFirst);
        Assert.True(table.TryGet(1, out var actualLast));
        Assert.Equal(last, actualLast);
        Assert.Equal(1, table.CountValue(2));
    }

    [Fact]
    public void AdvanceWithNonnegativeDeltaDecaysLowWordAndDropsExhaustedEntry()
    {
        var table = Table(
            new(2, 4, unchecked((int)0xabcd0007u)),
            new(2, 9, 0x12340002),
            new(2, 1, 0x56780002));

        Assert.True(table.TryAdvance(0, 3));
        Assert.True(table.TryAdvance(1, 2));

        Assert.Equal(2, table.Count);
        Assert.True(table.TryGet(0, out var decayed));
        Assert.Equal(unchecked((int)0xabcd0004u), decayed.Result);
        Assert.True(table.TryGet(1, out var untouched));
        Assert.Equal(0x56780002, untouched.Result);
    }

    [Fact]
    public void ZeroDeltaSucceedsWithoutValidatingIndex()
    {
        var entry = new InteractionScopeEntry(2, 4, 0x1234);
        var table = Table(entry);

        Assert.True(table.TryAdvance(-1, 0));
        Assert.Equal(1, table.Count);
        Assert.True(table.TryGet(0, out var actual));
        Assert.Equal(entry, actual);
    }

    private static InteractionScopeTable Table(params InteractionScopeEntry[] entries) => new(entries);
}
