using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class SpecialEffectPrimitiveTests
{
    private static byte[] Operand(short data, byte flags = 0, byte byte2 = 0, byte byte3 = 0, byte byte5 = 0) =>
        [(byte)data, (byte)(data >> 8), byte2, byte3, flags, byte5, 0, 0];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    private static (TreeContext Context, PrimitiveTestHost Host) Lot()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        context.World.Add(new XObject(5, PrimitiveTestWorld.Definition()));
        return (context, host);
    }

    [Fact]
    public void TheFirstCallSetsTheEffectWordAndStops()
    {
        var (context, host) = Lot();
        Assert.Equal(PrimitiveResult.Stop, SpecialEffectPrimitive.Execute(context, Operand(2)));
        Assert.Equal(["SetEffectWord(11,1)"], host.Calls);
        Assert.True(context.World.PrimitiveTicks.ContainsKey(context.Frame));
    }

    [Fact]
    public void AHeadlessWorldRecordsTheDecodedDispatchAndCompletesWhenPlaybackCannotStart()
    {
        var (context, _) = Lot();
        context.World.Host = null;

        Assert.Equal(PrimitiveResult.Stop, SpecialEffectPrimitive.Execute(context, Operand(2)));
        Assert.Equal((short)1, context.World.GlobalObjectTableEffectWords[11]);
        Assert.True(context.World.PrimitiveTicks.ContainsKey(context.Frame));

        Assert.Equal(PrimitiveResult.Stop,
            SpecialEffectPrimitive.Execute(context, Operand(2, byte2: 7, byte3: 8, byte5: 9)));
        Assert.True(context.World.PrimitiveTicks.ContainsKey(context.Frame));
        Assert.Equal((short)1, context.World.GlobalObjectTableEffectWords[11]);
        Assert.Equal([new HeadlessSpecialEffectDispatch(100, 5, new SpecialEffectRequest(2000, 7, 8, 0, 9))],
            context.World.HeadlessSpecialEffects.Dispatches);
        Assert.Equal(PrimitiveResult.True, SpecialEffectPrimitive.Execute(context, Operand(2)));
        Assert.False(context.World.PrimitiveTicks.ContainsKey(context.Frame));
        Assert.Single(context.World.HeadlessSpecialEffects.Dispatches);
    }

    [Fact]
    public void TheSecondCallPlaysTheLiteralValueAsMilliseconds()
    {
        var (context, host) = Lot();
        SpecialEffectPrimitive.Execute(context, Operand(2));
        Assert.Equal(PrimitiveResult.Stop, SpecialEffectPrimitive.Execute(context, Operand(2, byte2: 7, byte3: 8, byte5: 9)));
        Assert.Equal(["SetEffectWord(11,1)", "PlaySpecialEffect(100,5,2000,7,8,0,9)"], host.Calls);
    }

    [Fact]
    public void AValueFromTheLocalsIsReadWhenFlagBitFiveIsSet()
    {
        var (context, host) = Lot();
        context.Frame.Locals[1] = 7;
        SpecialEffectPrimitive.Execute(context, Operand(1, flags: 0x20));
        Assert.Equal(PrimitiveResult.Stop, SpecialEffectPrimitive.Execute(context, Operand(1, flags: 0x20)));
        Assert.Equal("PlaySpecialEffect(100,5,7000,0,0,32,0)", host.Calls[1]);
    }

    [Fact]
    public void ThirdCallAnswersTrueAndClearsTheState()
    {
        var (context, _) = Lot();
        SpecialEffectPrimitive.Execute(context, Operand(2));
        SpecialEffectPrimitive.Execute(context, Operand(2));
        Assert.Equal(PrimitiveResult.True, SpecialEffectPrimitive.Execute(context, Operand(2)));
        Assert.False(context.World.PrimitiveTicks.ContainsKey(context.Frame));
    }

    [Fact]
    public void AnEffectThatDoesNotStartAnswersTrueAtOnce()
    {
        var (context, host) = Lot();
        host.EffectStarts = false;
        SpecialEffectPrimitive.Execute(context, Operand(2));
        Assert.Equal(PrimitiveResult.True, SpecialEffectPrimitive.Execute(context, Operand(2)));
        Assert.False(context.World.PrimitiveTicks.ContainsKey(context.Frame));
    }

    [Fact]
    public void AMissingStackObjectOnTheSecondCallIsTheCouldNotFindObjectError()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        SpecialEffectPrimitive.Execute(context, Operand(2));
        Assert.Equal(SimulationError.CouldNotFindObjectReference, Fails(() => SpecialEffectPrimitive.Execute(context, Operand(2))));
        Assert.Equal(["SetEffectWord(11,1)"], host.Calls);
    }

    [Fact]
    public void AHeadlessMissingStackObjectStillRaisesTheNativeReferenceError()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 0);
        context.World.Host = null;
        SpecialEffectPrimitive.Execute(context, Operand(2));

        Assert.Equal(SimulationError.CouldNotFindObjectReference,
            Fails(() => SpecialEffectPrimitive.Execute(context, Operand(2))));
        Assert.Equal((short)1, context.World.GlobalObjectTableEffectWords[11]);
        Assert.Empty(context.World.HeadlessSpecialEffects.Dispatches);
    }

    [Fact]
    public void HeadlessDispatchEmitsNotice10CUnlessFlagBitSixIsSet()
    {
        var (context, _) = Lot();
        context.World.Host = null;
        var notices = 0;
        context.World.AddNoticeListener(new SimulationNoticeListener((message, value) =>
        {
            if (message != 0x10c) return false;
            Assert.Equal(0, value);
            notices++;
            return false;
        }));

        SpecialEffectPrimitive.Execute(context, Operand(2, flags: 0));
        SpecialEffectPrimitive.Execute(context, Operand(2, flags: 0));
        SpecialEffectPrimitive.Execute(context, Operand(2, flags: 0));
        Assert.Equal(1, notices);

        SpecialEffectPrimitive.Execute(context, Operand(2, flags: 0x40));
        SpecialEffectPrimitive.Execute(context, Operand(2, flags: 0x40));
        SpecialEffectPrimitive.Execute(context, Operand(2, flags: 0x40));
        Assert.Equal(1, notices);
    }

    [Fact]
    public void PictureInPictureCaptionPrefersCurrentStackFrameBehaviorScopeOverStackObjectAndMe()
    {
        var (context, _) = Lot();
        context.World.Host = null;
        context.World.PictureInPicture = new PictureInPictureOptions { Live = true };
        context.World.StateClasses.Add(new StateClassEntry(0, 1));

        var (frameFile, _) = new ChunkBuilder().S16(-1).S16(1).CString("Frame Caption").InFile("STR#", 0x131);
        var (stackFile, _) = new ChunkBuilder().S16(-1).S16(1).CString("Stack Caption").InFile("STR#", 0x131);
        var (emptyFile, _) = new ChunkBuilder().S16(0).InFile("STR#", 1);

        var stackObject = context.World.ObjectById(5)!;
        stackObject.TypeRecord = new TypeRecord(5)
        {
            BehaviorScope = new SimsEngine.Content.BehaviorScope(emptyFile, stackFile, null),
        };
        var frameScope = new SimsEngine.Content.BehaviorScope(emptyFile, frameFile, null);
        context = context with { Frame = new StackFrame(5, [], []) { Table = frameScope, TreeId = 4096 } };

        SpecialEffectPrimitive.Execute(context, Operand(2, flags: 1, byte5: 1));
        SpecialEffectPrimitive.Execute(context, Operand(2, flags: 1, byte5: 1));

        var pip = Assert.Single(context.World.PictureInPictureEvents);
        Assert.Equal("Frame Caption", pip.Text);
    }

    [Fact]
    public void AllowsPictureInPictureWhenCurrentFamilyIsZeroRequiresAutoFollowPersonData()
    {
        var (context, _) = Lot();
        var me = (XPerson)context.Me;
        context.World.Host = null;
        context.World.PictureInPicture = new PictureInPictureOptions { Live = true };
        context.World.ActiveLotNumber = 21;
        context.World[SimulationGlobal.CurrentFamily] = 0;

        me[PersonData.AutoFollowHotDate] = 0;
        SpecialEffectPrimitive.Execute(context, Operand(2, flags: 1));
        Assert.Equal(PrimitiveResult.True, SpecialEffectPrimitive.Execute(context, Operand(2, flags: 1)));
        Assert.Empty(context.World.PictureInPictureEvents);

        me[PersonData.AutoFollowHotDate] = 1;
        SpecialEffectPrimitive.Execute(context, Operand(2, flags: 1));
        Assert.Equal(PrimitiveResult.Stop, SpecialEffectPrimitive.Execute(context, Operand(2, flags: 1)));
        Assert.Single(context.World.PictureInPictureEvents);
    }
}
