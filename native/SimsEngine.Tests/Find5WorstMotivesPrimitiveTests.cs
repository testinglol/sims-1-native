using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class Find5WorstMotivesPrimitiveTests
{
    private static short[] Definition(short type = 0)
    {
        var definition = new short[XObject.DefinitionCount];
        definition[9] = type;
        return definition;
    }

    private static (TreeContext Context, XPerson Me, XPerson Stack) World(short stackObject = 2)
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, Definition(XPerson.PersonType)));
        var stack = world.Add(new XPerson(2, Definition(XPerson.PersonType)));
        var frame = new StackFrame(stackObject, [], []);
        return (new TreeContext(world, me, frame), me, stack);
    }

    // In the find worst motives operand, bytes 0 through 3 are unused, byte 4 specifies the subject, and
    // byte 6 specifies the search type.
    private static byte[] Operand(short subject, ushort searchType) =>
        [0, 0, 0, 0, (byte)subject, (byte)(subject >> 8), (byte)searchType, (byte)(searchType >> 8)];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    private static void SetAll(XPerson person, float value)
    {
        foreach (var motive in new[] { Motive.Energy, Motive.Comfort, Motive.Hunger, Motive.Hygiene, Motive.Bladder, Motive.Room, Motive.Social, Motive.Fun })
            person[motive] = value;
    }

    [Fact]
    public void StoresTheFiveWorstMotivesLowestFirst()
    {
        var (context, me, _) = World();
        me[Motive.Energy] = 90;
        me[Motive.Comfort] = 10;
        me[Motive.Hunger] = 50;
        me[Motive.Hygiene] = 70;
        me[Motive.Bladder] = 20;
        me[Motive.Room] = 80;
        me[Motive.Social] = 30;
        me[Motive.Fun] = 60;
        me.Temporaries[6] = 99;

        Assert.Equal(PrimitiveResult.True, Find5WorstMotivesPrimitive.Execute(context, Operand(0, 0)));
        Assert.Equal(new short[] { 6, 9, 14, 7, 15 }, me.Temporaries[..5]);
        Assert.Equal(0, me.Temporaries[5]);
        Assert.Equal(99, me.Temporaries[6]);
    }

    [Fact]
    public void EqualMotivesFollowTheTracedSortOrder()
    {
        // When motives have equal values, the selection sort moves the maximum remaining value to the end
        // each pass, resolving ties in initial motive index order.
        var (context, me, _) = World();
        SetAll(me, 50);
        Assert.Equal(PrimitiveResult.True, Find5WorstMotivesPrimitive.Execute(context, Operand(0, 0)));
        Assert.Equal(new short[] { 6, 7, 8, 9, 13 }, me.Temporaries[..5]);
    }

    [Fact]
    public void MotivesWithinTheToleranceCompareEqual()
    {
        var (context, me, _) = World();
        SetAll(me, 90);
        me[Motive.Comfort] = 10;
        me[Motive.Bladder] = 10.005f;
        Assert.Equal(PrimitiveResult.True, Find5WorstMotivesPrimitive.Execute(context, Operand(0, 0)));
        Assert.Equal(new short[] { 6, 9, 8, 7, 13 }, me.Temporaries[..5]);
    }

    [Fact]
    public void EveryLegalSearchTypeIsAccepted()
    {
        var (context, me, _) = World();
        SetAll(me, 50);
        me[Motive.Energy] = 1;
        foreach (ushort searchType in new ushort[] { 0, 1, 2 })
        {
            Assert.Equal(PrimitiveResult.True, Find5WorstMotivesPrimitive.Execute(context, Operand(0, searchType)));
            Assert.Equal(5, me.Temporaries[0]);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(0xFFFF)]
    public void RefusesASearchTypeOutsideZeroToTwo(int searchType)
    {
        var (context, me, _) = World();
        Assert.Equal(SimulationError.SearchTypeIsInvalid, Fails(() => Find5WorstMotivesPrimitive.Execute(context, Operand(0, (ushort)searchType))));
        Assert.Equal(0, me.Temporaries[0]);
    }

    [Fact]
    public void ReadsTheStackObjectWhenTheSubjectIsOne()
    {
        var (context, me, stack) = World();
        SetAll(me, 50);
        me[Motive.Energy] = 1;
        SetAll(stack, 50);
        stack[Motive.Fun] = 1;
        Assert.Equal(PrimitiveResult.True, Find5WorstMotivesPrimitive.Execute(context, Operand(1, 0)));
        Assert.Equal(15, me.Temporaries[0]);

        Assert.Equal(PrimitiveResult.True, Find5WorstMotivesPrimitive.Execute(context, Operand(0, 0)));
        Assert.Equal(5, me.Temporaries[0]);
    }

    [Fact]
    public void RefusesSubjectTwoAndMissingOrNonPersonSubjects()
    {
        var (context, me, _) = World();
        Assert.Equal(SimulationError.ReferenceToTreeParameterWhenNoParameterExists, Fails(() => Find5WorstMotivesPrimitive.Execute(context, Operand(2, 0))));

        var (missing, _, _) = World(stackObject: 77);
        Assert.Equal(SimulationError.ReferenceToTreeParameterWhenNoParameterExists, Fails(() => Find5WorstMotivesPrimitive.Execute(missing, Operand(1, 0))));

        var (chairContext, _, _) = World(stackObject: 3);
        chairContext.World.Add(new XObject(3, Definition(0)));
        Assert.Equal(SimulationError.ReferenceToTreeParameterWhenNoParameterExists, Fails(() => Find5WorstMotivesPrimitive.Execute(chairContext, Operand(1, 0))));
    }

    [Fact]
    public void ChecksThePersonBeforeTheSearchType()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XObject(1, Definition(0)));
        var context = new TreeContext(world, me, new StackFrame(0, [], []));
        Assert.Equal(SimulationError.ReferenceToTreeParameterWhenNoParameterExists, Fails(() => Find5WorstMotivesPrimitive.Execute(context, Operand(0, 5))));
    }
}
