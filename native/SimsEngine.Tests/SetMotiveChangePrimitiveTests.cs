using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class SetMotiveChangePrimitiveTests
{
    // Operand encodes rate owner in byte 0, target owner in byte 1, motive index in byte 2, flags in byte
    // 3, rate data in bytes 4-5, and target data in bytes 6-7.
    private static byte[] Operand(byte rateOwner, byte targetOwner, byte motive, byte flags, short rate, short target) =>
        [rateOwner, targetOwner, motive, flags, (byte)rate, (byte)(rate >> 8), (byte)target, (byte)(target >> 8)];

    private static byte[] Literal(byte motive, short rate, short target) => Operand((byte)DataOwner.Literal, (byte)DataOwner.Literal, motive, 0, rate, target);

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Fact]
    public void AddsAMotiveChangeWithTheScaledRateAndTheTarget()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        Assert.Equal(PrimitiveResult.True, SetMotiveChangePrimitive.Execute(context, Literal(5, 1800, 60)));
        var change = Assert.Single(me.MotiveChanges);
        Assert.Equal(Motive.Energy, change.Motive);
        Assert.Equal(1f, change.Rate, 4);
        Assert.Equal(60f, change.Target);
    }

    [Fact]
    public void ReadsTheRateAndTheTargetFromTheirOwners()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        me.Temporaries[3] = 900;
        me[Motive.Hunger] = 42.7f;
        Assert.Equal(PrimitiveResult.True, SetMotiveChangePrimitive.Execute(context, Operand(8, 14, 7, 0, 3, 7)));
        var change = Assert.Single(me.MotiveChanges);
        Assert.Equal(Motive.Hunger, change.Motive);
        Assert.Equal(0.5f, change.Rate, 4);
        Assert.Equal(42f, change.Target);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(14)]
    [InlineData(15)]
    public void EveryAllowedMotiveIsAccepted(int motive)
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        Assert.Equal(PrimitiveResult.True, SetMotiveChangePrimitive.Execute(context, Literal((byte)motive, 1800, 0)));
        Assert.Equal((Motive)motive, Assert.Single(me.MotiveChanges).Motive);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(0xFF)]
    public void RefusesAMotiveOutsideTheAllowedSetWithErrorTwentySix(int motive)
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        Assert.Equal(SimulationError.MotiveNumberOutOfRange, Fails(() => SetMotiveChangePrimitive.Execute(context, Literal((byte)motive, 1800, 0))));
        Assert.Empty(me.MotiveChanges);
    }

    [Fact]
    public void TheMotiveErrorComesBeforeTheVariablesAreRead()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        // Motive number range validation occurs before rate data owner validation.
        Assert.Equal(SimulationError.MotiveNumberOutOfRange, Fails(() => SetMotiveChangePrimitive.Execute(context, Operand(0xFF, 7, 13, 0, 0, 0))));
        Assert.Empty(me.MotiveChanges);
    }

    [Fact]
    public void AnIllegalVariableOwnerRaisesItsError()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        Assert.Equal(SimulationError.IllegalOwnerFieldInDataReference, Fails(() => SetMotiveChangePrimitive.Execute(context, Operand(0xFF, 7, 5, 0, 0, 0))));
        Assert.Empty(me.MotiveChanges);
    }

    [Fact]
    public void FlagBitZeroErasesEveryMotiveChangeWithoutCheckingTheMotive()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        SetMotiveChangePrimitive.Execute(context, Literal(5, 1800, 60));
        SetMotiveChangePrimitive.Execute(context, Literal(6, 1800, 60));
        Assert.Equal(2, me.MotiveChanges.Count);

        // The motive change clear operation bypasses motive range validation.
        Assert.Equal(PrimitiveResult.True, SetMotiveChangePrimitive.Execute(context, Operand(7, 7, 13, 1, 0, 0)));
        Assert.Empty(me.MotiveChanges);
    }

    [Fact]
    public void EntriesAreAddedInOrderWithoutMerging()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        SetMotiveChangePrimitive.Execute(context, Literal(5, 1800, 10));
        SetMotiveChangePrimitive.Execute(context, Literal(5, 1800, 20));
        SetMotiveChangePrimitive.Execute(context, Literal(6, 1800, 30));
        Assert.Equal(new[] { 10f, 20f, 30f }, me.MotiveChanges.Select(change => change.Target));
        Assert.Equal(new[] { Motive.Energy, Motive.Energy, Motive.Comfort }, me.MotiveChanges.Select(change => change.Motive));
    }

    [Fact]
    public void PersonsRunTheMotiveChangeThroughTheTreeRunner()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        var tree = PrimitiveTestWorld.Tree(0, (0x1d, 0xFE, 0xFF, Literal(8, 1800, 50)));
        Assert.True(TreeRunner.Run(context, tree));
        Assert.Equal(Motive.Hygiene, Assert.Single(me.MotiveChanges).Motive);
    }

    [Fact]
    public void NonPersonsRaiseIllegalPrimitiveCodeFromTheMainDispatcher()
    {
        var world = new SimulationWorld { Host = new PrimitiveTestHost() };
        var me = world.Add(new XObject(100, PrimitiveTestWorld.Definition(0)));
        var context = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));
        var tree = PrimitiveTestWorld.Tree(0, (0x1d, 0xFE, 0xFF, Literal(5, 1800, 0)));
        Assert.Equal(SimulationError.IllegalPrimitiveCode, Fails(() => TreeRunner.Run(context, tree)));
    }
}
