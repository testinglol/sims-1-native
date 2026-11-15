using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class InteractionBehaviorScopeTests
{
    [Fact]
    public void InitializeCreatesTheZeroedScopeAndSixEmptyActionLists()
    {
        var scope = InteractionBehaviorScope.Initialize();

        Assert.Null(scope.TypeRecord);
        Assert.Equal(0, scope.Mode);
        Assert.Null(scope.QueryContext);
        Assert.NotNull(scope.RuntimeContext);
        Assert.Equal(InteractionBehaviorScope.Size, 0x98);
        Assert.Equal(InteractionBehaviorScope.RuntimeContextSize, InteractionBehaviorRuntimeContext.Size);
        Assert.Equal(InteractionBehaviorScope.ActionArrayCount, 6);
        for (var index = 0; index < InteractionBehaviorScope.ActionArrayCount; index++)
            Assert.Null(scope.ActionList(index));
    }

    [Fact]
    public void RuntimeContextStartsWithTheFieldsClearedByTheOriginalInitializers()
    {
        var context = InteractionBehaviorScope.Initialize().RuntimeContext;

        Assert.Equal(0, context.Offset04);
        Assert.Equal(new byte[0x40], context.ClearedRange14);
        Assert.Equal((short)0, context.Offset54);
        Assert.Equal(0, context.Offset58);
        Assert.Equal((byte)0, context.Offset5C);
        Assert.Equal((sbyte)0, context.Offset5D);
        Assert.Equal(0, context.StringCount());
    }

    [Fact]
    public void ActionRecordsMatchBothStringsWithTheOriginalAsciiCaseRules()
    {
        var first = new InteractionBehaviorAction("CHAIR", "Sit");
        var second = new InteractionBehaviorAction("TABLE", "Eat");
        var list = new InteractionBehaviorActionList([first, second]);
        var scope = InteractionBehaviorScope.Initialize();

        scope.SetActionList(4, list);
        Assert.Same(list, scope.ActionList(4));
        Assert.Equal(0, list.FindIndex("chair", "SIT"));
        Assert.Equal(1, list.FindIndex("table", "eat"));
        Assert.Equal(-1, list.FindIndex("table", "sit"));
        Assert.Equal(-1, list.FindIndex("ChäIR", "sit"));
        Assert.Collection(list.Entries,
            entry => Assert.Same(first, entry),
            entry => Assert.Same(second, entry));
    }

    [Fact]
    public void ActionListAccessRejectsIndicesOutsideTheSixOriginalSlots()
    {
        var scope = InteractionBehaviorScope.Initialize();

        Assert.Throws<ArgumentOutOfRangeException>(() => scope.ActionList(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => scope.ActionList(6));
        Assert.Throws<ArgumentOutOfRangeException>(() => scope.SetActionList(-1, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => scope.SetActionList(6, null));
    }

    [Theory]
    [InlineData("CHAIR,Name=SIT;ignored", false, "chairsit")]
    [InlineData("CHAIR,Name;ignored", false, "name")]
    [InlineData("CHAIR;ignored", false, "chair")]
    [InlineData("ignored;CHAIR,Name=SIT", true, "chairsit")]
    [InlineData("CHAIR", true, "")]
    public void QueryBuilderAppliesTheOriginalDelimiterAndLowercaseRules(string text, bool afterSemicolon, string expected)
    {
        var query = InteractionBehaviorActionQueryBuilder.Build(text, afterSemicolon);

        Assert.Equal(string.Empty, query.First);
        Assert.Equal(expected, query.Second);
    }

    [Fact]
    public void QueryBuilderTreatsMissingAndNullTerminatedStringsLikeCStringBuffers()
    {
        Assert.Equal(new InteractionBehaviorActionQuery(string.Empty, string.Empty),
            InteractionBehaviorActionQueryBuilder.Build(null));
        Assert.Equal("chair", InteractionBehaviorActionQueryBuilder.Build("CHAIR\0ignored").Second);
    }

    [Fact]
    public void QueryBuilderRespectsTheOriginalSixtyThreeCharacterOutputCapacity()
    {
        var query = InteractionBehaviorActionQueryBuilder.Build(new string('A', 300));

        Assert.Equal(new string('a', 63), query.Second);
    }

    [Fact]
    public void SelectedActionUpdatePreservesTheCommaAndOriginalTemporaryCapacity()
    {
        var update = InteractionBehaviorActionQueryBuilder.BuildSelectedActionUpdate(new string('A', 300));

        Assert.Equal(255, update.Length);
        Assert.Equal(',', update[0]);
        Assert.Equal(new string('A', 254), update[1..]);
    }

    [Fact]
    public void RuntimeStringContextUsesOneBasedIndicesAndGrowsWithEmptyEntries()
    {
        var context = new InteractionBehaviorRuntimeContext();

        Assert.Equal(0, context.StringCount());
        Assert.Null(context.StringAt(1));
        Assert.Equal(0, context.StringCount(1));
        context.SetString(3, "third");

        Assert.Equal(3, context.StringCount());
        Assert.Equal(string.Empty, context.StringAt(1));
        Assert.Equal(string.Empty, context.StringAt(2));
        Assert.Equal("third", context.StringAt(3));
        context.SetString(2, null);
        Assert.Equal(string.Empty, context.StringAt(2));
        context.SetString(0, "ignored");
        Assert.Equal(3, context.StringCount());
    }

    [Fact]
    public void RuntimeStringContextModeFiltersNonWildcardReads()
    {
        var context = new InteractionBehaviorRuntimeContext();
        context.Offset5D = 2;
        context.SetString(1, "value");

        Assert.Null(context.StringAt(1, 1));
        Assert.Equal("value", context.StringAt(1, 2));
        Assert.Equal("value", context.StringAt(1, -1));
    }
}
