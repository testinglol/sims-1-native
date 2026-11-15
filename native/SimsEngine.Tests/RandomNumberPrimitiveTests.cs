using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class RandomNumberPrimitiveTests
{
    // Operand: result data u16 at 0, result owner byte 2, range data u16 at 4, range owner byte 6.
    private static byte[] Operand(short resultData, byte resultOwner, short rangeData, byte rangeOwner) =>
        [(byte)resultData, (byte)((ushort)resultData >> 8), resultOwner, 0, (byte)rangeData, (byte)((ushort)rangeData >> 8), rangeOwner, 0];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Fact]
    public void DecodesTheResultAndTheRangeOperands()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 10;
        // Result: temporary 1 (owner 8). Range: temporary 0 (owner 8).
        Assert.Equal(PrimitiveResult.True, RandomNumberPrimitive.Execute(context, Operand(1, 8, 0, 8)));
        Assert.Equal(8, me.Temporaries[1]);
        Assert.Equal(0, host.RandomCalls);
    }

    [Fact]
    public void ShortResultKeepsTheLowSixteenBits()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        // With seed 1, FUN_0046f556 returns 0x41c6.
        RandomNumberPrimitive.Execute(context, DataOwner.Temporary, 0, DataOwner.Literal, -2);
        Assert.Equal(0x41c6, me.Temporaries[0]);
    }

    [Fact]
    public void PositiveRangeTakesTheRemainder()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        // The first source draw is 0x41c6, below this range.
        RandomNumberPrimitive.Execute(context, DataOwner.Temporary, 0, DataOwner.Literal, short.MaxValue);
        Assert.Equal(0x41c6, me.Temporaries[0]);
    }

    [Fact]
    public void ZeroRangeIsAnErrorBeforeTheRandomSourceIsAsked()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Equal(SimulationError.RandomNumberRangeEvaluatedToZero, Fails(() => RandomNumberPrimitive.Execute(context, Operand(0, 8, 0, 7))));
        Assert.Equal(0, host.RandomCalls);
    }

    [Fact]
    public void ReadOnlyResultIsAnErrorBeforeTheRandomSourceIsAsked()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        // A literal result cannot be written; the range (5) is fine.
        Assert.Equal(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea, Fails(() => RandomNumberPrimitive.Execute(context, Operand(0, 7, 5, 7))));
        Assert.Equal(0, host.RandomCalls);
    }

    [Fact]
    public void ZeroRangeIsCheckedBeforeTheReadOnlyResult()
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        // The range is read and checked before the write target is looked at (Sims.exe's order).
        Assert.Equal(SimulationError.RandomNumberRangeEvaluatedToZero, Fails(() => RandomNumberPrimitive.Execute(context, Operand(0, 7, 0, 7))));
    }

    [Fact]
    public void ResultIsReadBeforeTheRange()
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        // Owner 2 is "targ obj attr", which Sims.exe rejects; it fails before the zero range is seen.
        Assert.Equal(SimulationError.IllegalOwnerFieldInDataReference, Fails(() => RandomNumberPrimitive.Execute(context, Operand(0, 2, 0, 7))));
    }

    [Fact]
    public void MotiveTakesTheValueAsAnInt()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Motives[2] = 50f;
        // Result: my motive 2 (owner 14). Range: literal 4.
        RandomNumberPrimitive.Execute(context, DataOwner.MyMotive, 2, DataOwner.Literal, 4);
        Assert.Equal(2f, me.Motives[2]);
    }

    [Fact]
    public void NegativeRangeIsAnUnsignedDivisor()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        // (uint)(int)-5 is 0xFFFFFFFB, so a small random value is its own remainder.
        RandomNumberPrimitive.Execute(context, DataOwner.Temporary, 0, DataOwner.Literal, -5);
        Assert.Equal(0x41c6, me.Temporaries[0]);
    }

    [Fact]
    public void FloatResultStoresTheRandomDrawAsAnInteger()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        // The random helper result is converted to the motive's integer value.
        RandomNumberPrimitive.Execute(context, DataOwner.MyMotive, 0, DataOwner.Literal, -1);
        Assert.Equal(0x41c6, me.Motives[0]);
    }

    [Fact]
    public void AlwaysAnswersTrue()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        // Range: literal 3, so 0x41c6 % 3 = 2 is written to temporary 0 and the answer is true.
        Assert.Equal(PrimitiveResult.True, RandomNumberPrimitive.Execute(context, Operand(0, 8, 3, 7)));
        Assert.Equal(2, me.Temporaries[0]);
    }

    [Fact]
    public void AdvancesTheSourceLcgAndReturnsTheUpdatedHighWord()
    {
        var world = new SimulationWorld { RandomState = 1 };
        Assert.Equal(0x41c6u, world.NextRandom());
        Assert.Equal(0x41c67ea6u, world.RandomState);
        Assert.Equal(0x59d4967eu, world.NextRandom());
        Assert.Equal(0x967eb0e7u, world.RandomState);
    }

    [Fact]
    public void ReturnsTheFullShiftedProductForHighSeeds()
    {
        var world = new SimulationWorld { RandomState = 0x12345678 };
        Assert.Equal(0x64030b71u, world.NextRandom());
        Assert.Equal(0x0b719151u, world.RandomState);
    }

    [Fact]
    public void RunsWithoutASimulationHost()
    {
        var world = new SimulationWorld { RandomState = 1 };
        var me = new XObject(1, new short[XObject.DefinitionCount]);
        var context = new TreeContext(world, me, new StackFrame(0, [], []));
        Assert.Equal(PrimitiveResult.True,
            RandomNumberPrimitive.Execute(context, DataOwner.Temporary, 0, DataOwner.Literal, 3));
        Assert.Equal(2, me.Temporaries[0]);
    }
}
