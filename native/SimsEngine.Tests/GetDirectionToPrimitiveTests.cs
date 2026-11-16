using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class GetDirectionToPrimitiveTests
{
    private static short[] Definition(short type = 0)
    {
        var definition = new short[XObject.DefinitionCount];
        definition[9] = type;
        return definition;
    }

    private static (TreeContext Context, XPerson Me, XObject Chair, XObject Stack) World(short stackObject = 3)
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, Definition(XPerson.PersonType)));
        me[ObjectData.ObjectId] = 1;
        var chair = world.Add(new XObject(2, Definition()));
        var stack = world.Add(new XObject(3, Definition()));
        return (new TreeContext(world, me, new StackFrame(stackObject, [], [])), me, chair, stack);
    }

    // GetDirectionTo operand encodes the output variable reference at bytes 0–3, flags at byte 4, and the
    // target object variable reference at bytes 5–7.
    private static byte[] Operand(byte flags = 0, byte owner = 0, short data = 0) =>
        [3, 0, 8, 0, flags, owner, (byte)data, (byte)(data >> 8)];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Theory]
    [InlineData(0, -256, 0)]
    [InlineData(100, -100, 1)]
    [InlineData(256, 0, 2)]
    [InlineData(100, 100, 3)]
    [InlineData(0, 256, 4)]
    [InlineData(-100, 100, 5)]
    [InlineData(-256, 0, 6)]
    [InlineData(-100, -100, 7)]
    [InlineData(0, 0, 3)]
    public void StoresTheDirectionOfEachOctant(int dx, int dy, int expected)
    {
        var (context, me, _, stack) = World();
        stack.Position = new ObjectPosition(dx, dy, 0);
        Assert.Equal(PrimitiveResult.True, GetDirectionToPrimitive.Execute(context, Operand()));
        Assert.Equal(expected, me.Temporaries[3]);
    }

    [Theory]
    [InlineData(100, 200, 3)]
    [InlineData(100, 201, 4)]
    [InlineData(-100, -200, 7)]
    public void TheEdgeBetweenTwoOctantsFollowsTheComparison(int dx, int dy, int expected)
    {
        // Direction quantization compares coordinate deltas against two-to-one slope thresholds, mapping
        // steep slopes to direction 4, intermediate diagonal slopes to direction 3, and shallow slopes to
        // direction 2.
        var (context, me, _, stack) = World();
        stack.Position = new ObjectPosition(dx, dy, 0);
        Assert.Equal(PrimitiveResult.True, GetDirectionToPrimitive.Execute(context, Operand()));
        Assert.Equal(expected, me.Temporaries[3]);
    }

    [Fact]
    public void ReadsTheObjectNamedByAVariable()
    {
        var (context, me, chair, stack) = World();
        chair.Position = new ObjectPosition(-256, 0, 0);
        stack.Position = new ObjectPosition(0, 0, 0);
        Assert.Equal(PrimitiveResult.True, GetDirectionToPrimitive.Execute(context, Operand(flags: 1, owner: 7, data: 2)));
        Assert.Equal(2, me.Temporaries[3]);
    }

    [Fact]
    public void RefusesAnOutputThatIsNotAShortVariable()
    {
        var (context, me, _, stack) = World();
        stack.Position = new ObjectPosition(256, 0, 0);
        byte[] operand = [3, 0, 7, 0, 0, 0, 0, 0];
        Assert.Equal(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea, Fails(() => GetDirectionToPrimitive.Execute(context, operand)));
        Assert.Equal(0, me.Temporaries[3]);
    }

    [Fact]
    public void MissingObjectsAreNotFoundOrHaveNoStackObject()
    {
        var (context, _, _, _) = World();
        Assert.Equal(SimulationError.CouldNotFindObjectReference, Fails(() => GetDirectionToPrimitive.Execute(context, Operand(flags: 1, owner: 7, data: 99))));

        var (noStack, _, _, _) = World(stackObject: 77);
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => GetDirectionToPrimitive.Execute(noStack, Operand())));
    }

    [Fact]
    public void TheOutputIsResolvedBeforeTheObjects()
    {
        // Destination variable references are resolved and validated before checking the target object.
        var (context, _, _, _) = World(stackObject: 77);
        byte[] operand = [3, 0, 0xEE, 0, 0, 0, 0, 0];
        Assert.Equal(SimulationError.IllegalOwnerFieldInDataReference, Fails(() => GetDirectionToPrimitive.Execute(context, operand)));
    }

    [Fact]
    public void DirectionBetweenIsFromSelfToOther()
    {
        // World direction orientation maps positive X displacements to direction 2 and negative X
        // displacements to direction 6.
        var self = new ObjectPosition(500, 500, 0);
        Assert.Equal(2, GetDirectionToPrimitive.DirectionBetween(self, new ObjectPosition(900, 500, 0)));
        Assert.Equal(6, GetDirectionToPrimitive.DirectionBetween(self, new ObjectPosition(100, 500, 0)));
    }
}
