using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class TestSimInteractingWithPrimitiveTests
{
    // Byte 0 the mode, byte 1 the owner of the other person's id variable, bytes 2 and 3 its data. Bytes 4 to 7 are not read.
    private static byte[] Operand(byte mode, byte owner, short data) =>
        [mode, owner, (byte)data, (byte)(data >> 8), 0xEE, 0xEE, 0xEE, 0xEE];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    // Me is person 100 and the stack object is object 2 unless the test replaces it.
    private static (TreeContext Context, XPerson Me, XObject Stack) World(short stackObject = 2)
    {
        var (context, me, _) = PrimitiveTestWorld.Create(stackObject: stackObject);
        var stack = context.World.Add(new XObject(2, PrimitiveTestWorld.Definition(0)));
        return (context, me, stack);
    }

    [Fact]
    public void ModeZeroFindsTheStackObjectInMyInteractions()
    {
        var (context, me, stack) = World();
        me.InteractingObjects.Add(stack);
        Assert.Equal(PrimitiveResult.True, TestSimInteractingWithPrimitive.Execute(context, Operand(0, 0, 0)));

        me.InteractingObjects.Clear();
        Assert.Equal(PrimitiveResult.False, TestSimInteractingWithPrimitive.Execute(context, Operand(0, 0, 0)));
    }

    [Fact]
    public void ComparesTheObjectsSourceStackKeyRatherThanItsNativeId()
    {
        var (context, me, stack) = World(stackObject: 17);
        stack.RequestKey = 17;
        me.InteractingObjects.Add(stack);

        Assert.NotEqual(stack.Id, context.Frame.StackObject);
        Assert.Equal(PrimitiveResult.True, TestSimInteractingWithPrimitive.Execute(context, Operand(0, 0, 0)));

        stack.RequestKey = 18;
        Assert.Equal(PrimitiveResult.False, TestSimInteractingWithPrimitive.Execute(context, Operand(0, 0, 0)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0xFF)]
    public void ModesOtherThanOneAskTheRunningPerson(int mode)
    {
        var (context, me, stack) = World();
        me.InteractingObjects.Add(stack);
        Assert.Equal(PrimitiveResult.True, TestSimInteractingWithPrimitive.Execute(context, Operand((byte)mode, 0, 0)));
    }

    [Fact]
    public void ModeOneReadsTheOtherPersonsInteractionsAndNotMine()
    {
        var (context, me, stack) = World();
        var other = context.World.Add(new XPerson(5, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        other.InteractingObjects.Add(stack);
        // Named by a literal (owner 7): person 5 holds the stack object, so the answer is true.
        Assert.Equal(PrimitiveResult.True, TestSimInteractingWithPrimitive.Execute(context, Operand(1, 7, 5)));

        // My own list is not searched in mode 1: I hold the stack object but person 5 does not.
        other.InteractingObjects.Clear();
        me.InteractingObjects.Add(stack);
        Assert.Equal(PrimitiveResult.False, TestSimInteractingWithPrimitive.Execute(context, Operand(1, 7, 5)));
    }

    [Fact]
    public void ModeOneNamesThePersonWithAVariable()
    {
        var (context, _, stack) = World();
        var other = context.World.Add(new XPerson(6, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        other.InteractingObjects.Add(stack);
        context.Me.Temporaries[2] = 6;
        // Owner 8 is a temporary: temporary 2 holds person 6.
        Assert.Equal(PrimitiveResult.True, TestSimInteractingWithPrimitive.Execute(context, Operand(1, 8, 2)));
    }

    [Fact]
    public void ModeOneRefusesAnObjectThatIsNotAPersonOrIsMissing()
    {
        var (context, _, _) = World();
        context.World.Add(new XObject(6, PrimitiveTestWorld.Definition(0)));
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, Fails(() => TestSimInteractingWithPrimitive.Execute(context, Operand(1, 7, 6))));
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, Fails(() => TestSimInteractingWithPrimitive.Execute(context, Operand(1, 7, 9))));
    }

    [Fact]
    public void AMissingStackObjectMatchesNothingWithoutAnError()
    {
        var (context, me, stack) = World(stackObject: 77);
        me.InteractingObjects.Add(stack);
        Assert.Equal(PrimitiveResult.False, TestSimInteractingWithPrimitive.Execute(context, Operand(0, 0, 0)));
    }

    [Fact]
    public void PersonsRunTheTestThroughTheTreeRunner()
    {
        var (context, me, stack) = World();
        me.InteractingObjects.Add(stack);
        var tree = PrimitiveTestWorld.Tree(0, (0x25, 0xFE, 0xFF, Operand(0, 0, 0)));
        Assert.True(TreeRunner.Run(context, tree));
    }

    [Fact]
    public void NonPersonsRaiseIllegalPrimitiveCodeFromTheMainDispatcher()
    {
        var world = new SimulationWorld { Host = new PrimitiveTestHost() };
        var me = world.Add(new XObject(100, PrimitiveTestWorld.Definition(0)));
        var context = new TreeContext(world, me, new StackFrame(2, [], [0, 0]));
        var tree = PrimitiveTestWorld.Tree(0, (0x25, 0xFE, 0xFF, Operand(0, 0, 0)));
        Assert.Equal(SimulationError.IllegalPrimitiveCode, Fails(() => TreeRunner.Run(context, tree)));
    }
}
