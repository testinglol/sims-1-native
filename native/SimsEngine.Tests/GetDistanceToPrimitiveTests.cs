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

    // Me is person 1 at the origin, the chair is object 2 and the stack object is object 3. Me's object id is set
    // as the game sets it, since "my object id" is what the primitive reads without a variable.
    private static (TreeContext Context, XPerson Me, XObject Chair, XObject Stack) World(short stackObject = 3)
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, Definition(XPerson.PersonType)));
        me[ObjectData.ObjectId] = 1;
        var chair = world.Add(new XObject(2, Definition()));
        var stack = world.Add(new XObject(3, Definition()));
        return (new TreeContext(world, me, new StackFrame(stackObject, [], [])), me, chair, stack);
    }

    // Temporary at 0, flags at 2, owner byte at 3 and data short at 4.
    private static byte[] Operand(short temporary, byte flags = 0, byte owner = 0, short data = 0) =>
        [(byte)temporary, (byte)(temporary >> 8), flags, owner, (byte)data, (byte)(data >> 8), 0, 0];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Fact]
    public void StoresTheDistanceFromMeToTheStackObject()
    {
        var (context, me, _, stack) = World();
        // (768, 0) and height 1: sqrt(20² + 768² / 256) = sqrt(400 + 2304) = 52.
        stack.Position = new ObjectPosition(768, 0, 1);
        Assert.Equal(PrimitiveResult.True, GetDistanceToPrimitive.Execute(context, Operand(2)));
        Assert.Equal(52, me.Temporaries[2]);
    }

    [Fact]
    public void TruncatesTheDistanceTowardZero()
    {
        // sqrt(100² / 256) = 6.25, stored as 6.
        var (context, me, _, stack) = World();
        stack.Position = new ObjectPosition(100, 0, 0);
        Assert.Equal(PrimitiveResult.True, GetDistanceToPrimitive.Execute(context, Operand(0)));
        Assert.Equal(6, me.Temporaries[0]);
    }

    [Fact]
    public void FollowsTheObjectNamedByAVariable()
    {
        // Flag bit 0 with a literal owner (7) and data 2 names the chair. Me is at the stack object, so me would give 0.
        var (context, me, chair, stack) = World();
        chair.Position = new ObjectPosition(0, 512, 0);
        Assert.Equal(PrimitiveResult.True, GetDistanceToPrimitive.Execute(context, Operand(0, flags: 1, owner: 7, data: 2)));
        // sqrt(512² / 256) = sqrt(1024) = 32.
        Assert.Equal(32, me.Temporaries[0]);
    }

    [Fact]
    public void ADistanceToAType7ObjectIsZero()
    {
        var (context, me, _, stack) = World();
        stack.Position = new ObjectPosition(768, 0, 1);
        me.Temporaries[0] = 9;
        // The stack object's definition field 9 (type) is 7.
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
        // Owner 0xEE is not an owner. The stack object is missing too, but the variable is read first.
        var (context, _, _, _) = World(stackObject: 77);
        Assert.Equal(SimulationError.IllegalOwnerFieldInDataReference, Fails(() => GetDistanceToPrimitive.Execute(context, Operand(0, flags: 1, owner: 0xEE, data: 0))));
    }

    [Fact]
    public void DistanceBetweenIsTheSquareRootOfTheParts()
    {
        // dx 768 and dy 1024: (768² + 1024²) / 256 = 6400, and sqrt(6400) = 80.
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
