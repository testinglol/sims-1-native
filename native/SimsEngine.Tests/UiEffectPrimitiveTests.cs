using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class UiEffectPrimitiveTests
{
    private static byte[] Operand(byte type, byte owner, short data, byte flags) =>
        [type, owner, (byte)data, (byte)((ushort)data >> 8), flags, 0, 0, 0];

    [Fact]
    public void AHostlessWorldShowsNothingAndStillAnswersTrue()
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.True, UiEffectPrimitive.Execute(context, Operand(0, (byte)DataOwner.Literal, 42, 1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void HostlessEffectsStillEvaluateTheirOperand(byte type)
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        Assert.Throws<SimulationException>(() => UiEffectPrimitive.Execute(context, Operand(type, 2, 0, 0)));
    }

    [Theory]
    [InlineData(0, "ImageButton(42,True)", "PersonButton(42,True)")]
    [InlineData(1, "PersonButton(42,True)", null)]
    [InlineData(2, "RelationshipButton(42,True)", null)]
    public void AHostlessWorldGivesTheMacTutorialFlashesToItsHook(byte type, string first, string? second)
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        var calls = new List<string>();
        context.World.TutorialFlash = (kind, value, on) => calls.Add($"{kind}({value},{on})");
        UiEffectPrimitive.Execute(context, Operand(type, (byte)DataOwner.Literal, 42, 1));
        Assert.Equal(second is null ? [first] : new[] { first, second }, calls);
    }

    [Fact]
    public void TypeZeroSetsThenShowsTheObjectEffectOnTheVariablesObject()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Equal(PrimitiveResult.True, UiEffectPrimitive.Execute(context, Operand(0, (byte)DataOwner.Literal, 42, 1)));
        Assert.Equal(new[] { "SetObjectUiEffect(42,True)", "ShowObjectUiEffect(42,True)" }, host.Calls);
    }

    [Fact]
    public void TypeOneShowsTheObjectEffectOnly()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Equal(PrimitiveResult.True, UiEffectPrimitive.Execute(context, Operand(1, (byte)DataOwner.Literal, 42, 0)));
        Assert.Equal(new[] { "ShowObjectUiEffect(42,False)" }, host.Calls);
    }

    [Fact]
    public void TypeTwoShowsTheEntryEffect()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Equal(PrimitiveResult.True, UiEffectPrimitive.Execute(context, Operand(2, (byte)DataOwner.Literal, 42, 0)));
        Assert.Equal(new[] { "ShowEntryUiEffect(42,False)" }, host.Calls);
    }

    [Fact]
    public void FlagIsBitZeroOfByteFour()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        UiEffectPrimitive.Execute(context, Operand(1, (byte)DataOwner.Literal, 1, 0xFE));
        UiEffectPrimitive.Execute(context, Operand(1, (byte)DataOwner.Literal, 2, 0x03));
        Assert.Equal(new[] { "ShowObjectUiEffect(1,False)", "ShowObjectUiEffect(2,True)" }, host.Calls);
    }

    [Fact]
    public void TheIdIsReadFromTheTemporaryNamedByTheOperand()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[3] = 77;
        UiEffectPrimitive.Execute(context, Operand(1, (byte)DataOwner.Temporary, 3, 0));
        Assert.Equal(new[] { "ShowObjectUiEffect(77,False)" }, host.Calls);
    }

    [Fact]
    public void OtherTypesDoNothingAndNeverReadTheVariable()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Equal(PrimitiveResult.True, UiEffectPrimitive.Execute(context, Operand(3, 2, 0, 1)));
        Assert.Equal(PrimitiveResult.True, UiEffectPrimitive.Execute(context, Operand(0xFF, 2, 0, 1)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void TheVariableIsReadBeforeTheEffect()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Throws<SimulationException>(() => UiEffectPrimitive.Execute(context, Operand(0, 2, 0, 0)));
        Assert.Throws<SimulationException>(() => UiEffectPrimitive.Execute(context, Operand(1, 2, 0, 0)));
        Assert.Throws<SimulationException>(() => UiEffectPrimitive.Execute(context, Operand(2, 2, 0, 0)));
        Assert.Empty(host.Calls);
    }
}
