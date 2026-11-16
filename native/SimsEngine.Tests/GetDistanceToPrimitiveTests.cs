using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class GetDistanceToPrimitiveTests
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

    // GetDistanceTo operand encodes the destination temporary at bytes 0–1, flags at byte 2, and the target
    // object variable reference at bytes 3–5.
    private static byte[] Operand(short temporary, byte flags = 0, byte owner = 0, short data = 0) =>
        [(byte)temporary, (byte)(temporary >> 8), flags, owner, (byte)data, (byte)(data >> 8), 0, 0];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Fact]
    public void StoresTheDistanceFromMeToTheStackObject()
    {
        var (context, me, _, stack) = World();
        // Distance calculation divides horizontal squared displacement by 256, adds 20 squared per floor
        // level difference, and takes the square root.
        stack.Position = new ObjectPosition(768, 0, 1);
        Assert.Equal(PrimitiveResult.True, GetDistanceToPrimitive.Execute(context, Operand(2)));
        Assert.Equal(52, me.Temporaries[2]);
    }

    [Fact]
    public void TruncatesTheDistanceTowardZero()
    {
        var (context, me, _, stack) = World();
        stack.Position = new ObjectPosition(100, 0, 0);
        Assert.Equal(PrimitiveResult.True, GetDistanceToPrimitive.Execute(context, Operand(0)));
        Assert.Equal(6, me.Temporaries[0]);
    }

    [Fact]
    public void FollowsTheObjectNamedByAVariable()
    {
        var (context, me, chair, stack) = World();
        chair.Position = new ObjectPosition(0, 512, 0);
        Assert.Equal(PrimitiveResult.True, GetDistanceToPrimitive.Execute(context, Operand(0, flags: 1, owner: 7, data: 2)));
        Assert.Equal(32, me.Temporaries[0]);
    }

    [Fact]
    public void ADistanceToAType7ObjectIsZero()
    {
        var (context, me, _, stack) = World();
        stack.Position = new ObjectPosition(768, 0, 1);
        me.Temporaries[0] = 9;
        stack.Definition[9] = 7;
        Assert.Equal(PrimitiveResult.True, GetDistanceToPrimitive.Execute(context, Operand(0)));
        Assert.Equal(0, me.Temporaries[0]);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(-1)]
    public void RefusesATemporaryOutsideZeroToSeven(int temporary)
    {
        var (context, me, _, _) = World();
        Assert.Equal(SimulationError.TemporaryReferenceOutOfRange, Fails(() => GetDistanceToPrimitive.Execute(context, Operand((short)temporary))));
        Assert.All(me.Temporaries, value => Assert.Equal(0, value));
    }

    [Fact]
    public void MissingObjectsAreNotFound()
    {
        var (context, _, _, _) = World();
        Assert.Equal(SimulationError.CouldNotFindObjectReference, Fails(() => GetDistanceToPrimitive.Execute(context, Operand(0, flags: 1, owner: 7, data: 99))));

        var (noStack, _, _, _) = World(stackObject: 77);
        Assert.Equal(SimulationError.CouldNotFindObjectReference, Fails(() => GetDistanceToPrimitive.Execute(noStack, Operand(0))));
    }

    [Fact]
    public void AVariableErrorComesBeforeTheObjectChecks()
    {
        var (context, _, _, _) = World(stackObject: 77);
        Assert.Equal(SimulationError.IllegalOwnerFieldInDataReference, Fails(() => GetDistanceToPrimitive.Execute(context, Operand(0, flags: 1, owner: 0xEE, data: 0))));
    }

    [Fact]
    public void DistanceBetweenIsTheSquareRootOfTheParts()
    {
        var self = new XObject(1, Definition());
        var other = new XObject(2, Definition());
        other.Position = new ObjectPosition(768, 1024, 0);
        Assert.Equal(80, GetDistanceToPrimitive.DistanceBetween(self, other));

        other.Position = new ObjectPosition(0, 0, 1);
        Assert.Equal(20, GetDistanceToPrimitive.DistanceBetween(self, other));
    }

    [Fact]
    public void TypeSevenUsesTheExecutablesZeroDistanceConstant()
    {
        var self = new XObject(1, Definition());
        var typeSeven = new XObject(2, Definition());
        typeSeven.Definition[9] = 7;
        typeSeven.Position = new ObjectPosition(768, 1024, 3);

        Assert.Equal(0, GetDistanceToPrimitive.DistanceBetween(self, typeSeven));
    }
}
