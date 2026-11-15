using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class UiEffectPrimitiveTests
{
    // Operand: effect type byte 0, owner byte 1, data word at 2, flags byte 4 (bit 0 is the flag). Bytes 5 to 7 unused.
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
        // Mac ObjectSim.s 0x100E75D0: type 0 is Tut_FlashButtonByImageID then Tut_FlashPersonPanelButton, type 1 the
        // person panel button and type 2 Tut_FlashRelButton.
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
        // Literal owner 7: the data is the value, 42.
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
        // Owner 2 is rejected by the variable read, so a read here would raise an error; the other types never read it.
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Equal(PrimitiveResult.True, UiEffectPrimitive.Execute(context, Operand(3, 2, 0, 1)));
        Assert.Equal(PrimitiveResult.True, UiEffectPrimitive.Execute(context, Operand(0xFF, 2, 0, 1)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void TheVariableIsReadBeforeTheEffect()
    {
        // Owner 2 is rejected by the variable read, so the read happens before any effect call.
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Throws<SimulationException>(() => UiEffectPrimitive.Execute(context, Operand(0, 2, 0, 0)));
        Assert.Throws<SimulationException>(() => UiEffectPrimitive.Execute(context, Operand(1, 2, 0, 0)));
        Assert.Throws<SimulationException>(() => UiEffectPrimitive.Execute(context, Operand(2, 2, 0, 0)));
        Assert.Empty(host.Calls);
    }
}
