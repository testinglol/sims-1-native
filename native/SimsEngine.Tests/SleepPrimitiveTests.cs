using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class SleepPrimitiveTests
{
    private static byte[] Operand(short index) => [(byte)index, (byte)((ushort)index >> 8), 0, 0, 0, 0, 0, 0];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;
    private static void SetWaitCell(SimulationWorld world, XObject item, short value) =>
        world.ClockManager.SetWaitTicksAt(item.ClockManagerIndex, value);
    private static short WaitCell(SimulationWorld world, XObject item) =>
        world.ClockManager.WaitTicksAt(item.ClockManagerIndex);

    [Fact]
    public void ReadsTheParameterIndexFromTheOperand()
    {
        var (context, _, _) = PrimitiveTestWorld.Create([5, 3]);
        SetWaitCell(context.World, context.Me, 0);
        // When the wait timer expires, the sleep primitive clears the counter parameter and returns true
        // without modifying other parameters.
        Assert.Equal(PrimitiveResult.True, SleepPrimitive.Execute(context, Operand(1)));
        Assert.Equal(0, context.Frame.Parameters[1]);
        Assert.Equal(5, context.Frame.Parameters[0]);
    }

    [Fact]
    public void ParameterOutsideTheFrameIsStackNumberError()
    {
        var (context, _, _) = PrimitiveTestWorld.Create([5, 3]);
        Assert.Equal(SimulationError.StackNumberOutOfRange, Fails(() => SleepPrimitive.Execute(context, Operand(2))));
        Assert.Equal(SimulationError.StackNumberOutOfRange, Fails(() => SleepPrimitive.Execute(context, Operand(-1))));
        Assert.Equal(new short[] { 5, 3 }, context.Frame.Parameters);
    }

    [Fact]
    public void FirstRunClearsTheCounterAndMarksTheWaitCell()
    {
        var (context, me, _) = PrimitiveTestWorld.Create([4]);
        SetWaitCell(context.World, me, 0);
        Assert.Equal(PrimitiveResult.True, SleepPrimitive.Execute(context, Operand(0)));
        Assert.Equal(0, context.Frame.Parameters[0]);
        Assert.Equal(-1, WaitCell(context.World, me));
    }

    [Fact]
    public void RegisteredObjectStartsWithTheOriginalMinusOneWaitCell()
    {
        var (context, me, _) = PrimitiveTestWorld.Create([3]);
        me.Definition[(int)SimsEngine.Formats.ObjdField.Type] = 0;

        Assert.Equal((short)-1, WaitCell(context.World, me));
        Assert.Equal(PrimitiveResult.Stop, SleepPrimitive.Execute(context, Operand(0)));
        Assert.Equal(3, context.Frame.Parameters[0]);
        Assert.Equal(2, WaitCell(context.World, me));
    }

    [Fact]
    public void ZeroOrNegativeCounterAnswersTrueAndLeavesTheCell()
    {
        var (context, me, _) = PrimitiveTestWorld.Create([0, -3]);
        SetWaitCell(context.World, me, 7);
        Assert.Equal(PrimitiveResult.True, SleepPrimitive.Execute(context, Operand(0)));
        Assert.Equal(PrimitiveResult.True, SleepPrimitive.Execute(context, Operand(1)));
        Assert.Equal(0, context.Frame.Parameters[1]);
        Assert.Equal(7, WaitCell(context.World, me));
    }

    [Fact]
    public void PositiveCounterCountsDownOneTickAtATime()
    {
        var (context, me, _) = PrimitiveTestWorld.Create([3]);
        SetWaitCell(context.World, me, 7);
        Assert.Equal(PrimitiveResult.Stop, SleepPrimitive.Execute(context, Operand(0)));
        Assert.Equal(2, context.Frame.Parameters[0]);
        Assert.Equal(PrimitiveResult.Stop, SleepPrimitive.Execute(context, Operand(0)));
        Assert.Equal(PrimitiveResult.Stop, SleepPrimitive.Execute(context, Operand(0)));
        Assert.Equal(0, context.Frame.Parameters[0]);
        Assert.Equal(PrimitiveResult.True, SleepPrimitive.Execute(context, Operand(0)));
        Assert.Equal(7, WaitCell(context.World, me));
    }

    [Fact]
    public void WaitCellPathWritesTheCounterMinusOneAndKeepsTheCounter()
    {
        var (context, me, _) = PrimitiveTestWorld.Create([3]);
        me.Definition[(int)SimsEngine.Formats.ObjdField.Type] = 0;
        SetWaitCell(context.World, me, 7);
        Assert.Equal(PrimitiveResult.Stop, SleepPrimitive.Execute(context, Operand(0)));
        Assert.Equal(3, context.Frame.Parameters[0]);
        Assert.Equal(2, WaitCell(context.World, me));
    }

    [Fact]
    public void NativeWaitCellGateDoesNotRequireAHost()
    {
        var (context, me, _) = PrimitiveTestWorld.Create([3]);
        me.Definition[(int)SimsEngine.Formats.ObjdField.Type] = 0;
        SetWaitCell(context.World, me, 7);
        context.World.Host = null;

        Assert.Equal(PrimitiveResult.Stop, SleepPrimitive.Execute(context, Operand(0)));

        Assert.Equal(3, context.Frame.Parameters[0]);
        Assert.Equal(2, WaitCell(context.World, me));
    }

    [Fact]
    public void WaitCellsAreKeptPerObject()
    {
        var (context, me, _) = PrimitiveTestWorld.Create([2]);
        me.Definition[(int)SimsEngine.Formats.ObjdField.Type] = 0;
        var other = context.World.Add(new XObject(9, PrimitiveTestWorld.Definition()));
        SetWaitCell(context.World, me, 5);
        SetWaitCell(context.World, other, 4);
        Assert.Equal(PrimitiveResult.Stop, SleepPrimitive.Execute(context, Operand(0)));
        Assert.Equal(1, WaitCell(context.World, me));
        Assert.Equal(4, WaitCell(context.World, other));
    }

    [Fact]
    public void TypeTwoDoesNotWriteTheWaitCellEvenWhenTheRawGatesAreClear()
    {
        var (context, me, _) = PrimitiveTestWorld.Create([2]);
        SetWaitCell(context.World, me, 5);

        Assert.Equal(PrimitiveResult.Stop, SleepPrimitive.Execute(context, Operand(0)));

        Assert.Equal(1, context.Frame.Parameters[0]);
        Assert.Equal(5, WaitCell(context.World, me));
    }

    [Fact]
    public void FirstRuntimeSpriteSlotAndRawWordSevenCControlTheWaitCellGate()
    {
        var (context, me, _) = PrimitiveTestWorld.Create([2]);
        me.Definition[(int)SimsEngine.Formats.ObjdField.Type] = 0;
        SetWaitCell(context.World, me, 5);
        var spriteSlot = new ObjectRuntimeSlot();
        spriteSlot.WriteWord(0x10, 1);
        me.RuntimeSlots.Add(spriteSlot);

        Assert.Equal(PrimitiveResult.Stop, SleepPrimitive.Execute(context, Operand(0)));
        Assert.Equal(1, context.Frame.Parameters[0]);
        Assert.Equal(5, WaitCell(context.World, me));

        spriteSlot.WriteWord(0x10, 0);
        me.SleepGateWord7C = 1;
        Assert.Equal(1, me[ObjectData.LockoutCount]);
        Assert.Equal(PrimitiveResult.Stop, SleepPrimitive.Execute(context, Operand(0)));
        Assert.Equal(0, context.Frame.Parameters[0]);
        Assert.Equal(5, WaitCell(context.World, me));
    }
}
