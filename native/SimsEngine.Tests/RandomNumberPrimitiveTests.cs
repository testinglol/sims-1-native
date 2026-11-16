using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class RandomNumberPrimitiveTests
{
    private static byte[] Operand(short resultData, byte resultOwner, short rangeData, byte rangeOwner) =>
        [(byte)resultData, (byte)((ushort)resultData >> 8), resultOwner, 0, (byte)rangeData, (byte)((ushort)rangeData >> 8), rangeOwner, 0];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Fact]
    public void DecodesTheResultAndTheRangeOperands()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 10;
        Assert.Equal(PrimitiveResult.True, RandomNumberPrimitive.Execute(context, Operand(1, 8, 0, 8)));
        Assert.Equal(8, me.Temporaries[1]);
        Assert.Equal(0, host.RandomCalls);
    }

    [Fact]
    public void ShortResultKeepsTheLowSixteenBits()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        RandomNumberPrimitive.Execute(context, DataOwner.Temporary, 0, DataOwner.Literal, -2);
        Assert.Equal(0x41c6, me.Temporaries[0]);
    }

    [Fact]
    public void PositiveRangeTakesTheRemainder()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
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
        Assert.Equal(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea, Fails(() => RandomNumberPrimitive.Execute(context, Operand(0, 7, 5, 7))));
        Assert.Equal(0, host.RandomCalls);
    }

    [Fact]
    public void ZeroRangeIsCheckedBeforeTheReadOnlyResult()
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        Assert.Equal(SimulationError.RandomNumberRangeEvaluatedToZero, Fails(() => RandomNumberPrimitive.Execute(context, Operand(0, 7, 0, 7))));
    }

    [Fact]
    public void ResultIsReadBeforeTheRange()
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        Assert.Equal(SimulationError.IllegalOwnerFieldInDataReference, Fails(() => RandomNumberPrimitive.Execute(context, Operand(0, 2, 0, 7))));
    }

    [Fact]
    public void MotiveTakesTheValueAsAnInt()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Motives[2] = 50f;
        RandomNumberPrimitive.Execute(context, DataOwner.MyMotive, 2, DataOwner.Literal, 4);
        Assert.Equal(2f, me.Motives[2]);
    }

    [Fact]
    public void NegativeRangeIsAnUnsignedDivisor()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        RandomNumberPrimitive.Execute(context, DataOwner.Temporary, 0, DataOwner.Literal, -5);
        Assert.Equal(0x41c6, me.Temporaries[0]);
    }

    [Fact]
    public void FloatResultStoresTheRandomDrawAsAnInteger()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        RandomNumberPrimitive.Execute(context, DataOwner.MyMotive, 0, DataOwner.Literal, -1);
        Assert.Equal(0x41c6, me.Motives[0]);
    }

    [Fact]
    public void AlwaysAnswersTrue()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
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
