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

    // Me is person 1 and the stack object is person 2 unless the test replaces them.
    private static (TreeContext Context, XPerson Me, XPerson Stack) World(short stackObject = 2)
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, Definition(XPerson.PersonType)));
        var stack = world.Add(new XPerson(2, Definition(XPerson.PersonType)));
        var frame = new StackFrame(stackObject, [], []);
        return (new TreeContext(world, me, frame), me, stack);
    }

    // Bytes 0 to 3 are unused. Subject at 4 and search type at 6.
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
        // Comfort 10, Bladder 20, Social 30, Hunger 50, Fun 60.
        Assert.Equal(new short[] { 6, 9, 14, 7, 15 }, me.Temporaries[..5]);
        Assert.Equal(0, me.Temporaries[5]);
        Assert.Equal(99, me.Temporaries[6]);
    }

    [Fact]
    public void EqualMotivesFollowTheTracedSortOrder()
    {
        // The sort moves the greatest of the rest to the end each pass, so equal motives come out as
        // Comfort, Hunger, Hygiene, Bladder, Room (the eight start as 5, 6, 7, 8, 9, 13, 14, 15).
        var (context, me, _) = World();
        SetAll(me, 50);
        Assert.Equal(PrimitiveResult.True, Find5WorstMotivesPrimitive.Execute(context, Operand(0, 0)));
        Assert.Equal(new short[] { 6, 7, 8, 9, 13 }, me.Temporaries[..5]);
    }

    [Fact]
    public void MotivesWithinTheToleranceCompareEqual()
    {
        // Comfort (10) and Bladder (10.005) are within 0.01, so the comparison does not order them.
        // The other six are 90. The traced sort gives Comfort, Bladder, Hygiene, Hunger, Room.
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

        // A stack object that is not a person (type 0) is refused the same way.
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
