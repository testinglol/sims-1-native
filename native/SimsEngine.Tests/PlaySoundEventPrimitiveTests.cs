using SimsEngine.Simulation;

namespace SimsEngine.Tests;

/// <summary>Play Sound Event (0x17), after FUN_00452862: the event id, the owner from the flags, the two banks and the
/// positional part, and the result, which is always true.</summary>
public class PlaySoundEventPrimitiveTests
{
    private const short StackObjectId = 5;

    /// <summary>The operand: the event id in word 0 and the flags in byte 4.</summary>
    private static byte[] Operand(short eventId, byte flags = 0) =>
        [(byte)eventId, (byte)(eventId >> 8), 0, 0, flags, 0, 0, 0];

    private static (TreeContext Context, PrimitiveTestHost Host, XPerson Me) Lot()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: StackObjectId);
        context.World.Add(new XObject(StackObjectId, PrimitiveTestWorld.Definition()));
        return (context, host, me);
    }

    [Fact]
    public void AHostlessWorldPlaysNothingAndStillAnswersTrue()
    {
        var (context, _, _) = Lot();
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.True, PlaySoundEventPrimitive.Execute(context, Operand(7)));
    }

    [Fact]
    public void BankZeroAcceptingTheEventSkipsBankOneAndPlaysForMe()
    {
        var (context, host, me) = Lot();
        host.AcceptingBanks.Add(0);
        Assert.Equal(PrimitiveResult.True, PlaySoundEventPrimitive.Execute(context, Operand(0x1234)));
        Assert.Equal(new[] { (0, (short)0x1234) }, host.SoundRequests);
        Assert.Equal(new[] { me.Id }, host.PositionalSounds);
    }

    [Fact]
    public void BankOneIsTriedWhenBankZeroRefuses()
    {
        var (context, host, me) = Lot();
        host.AcceptingBanks.Add(1);
        PlaySoundEventPrimitive.Execute(context, Operand(7));
        Assert.Equal(new[] { (0, (short)7), (1, (short)7) }, host.SoundRequests);
        Assert.Equal(new[] { me.Id }, host.PositionalSounds);
    }

    [Fact]
    public void NoPositionalSoundWhenNoBankAccepts()
    {
        var (context, host, _) = Lot();
        Assert.Equal(PrimitiveResult.True, PlaySoundEventPrimitive.Execute(context, Operand(7)));
        Assert.Equal(new[] { (0, (short)7), (1, (short)7) }, host.SoundRequests);
        Assert.Empty(host.PositionalSounds);
    }

    [Fact]
    public void FlagBitOneMakesTheStackObjectTheOwner()
    {
        var (context, host, _) = Lot();
        host.AcceptingBanks.Add(0);
        PlaySoundEventPrimitive.Execute(context, Operand(7, flags: 2));
        PlaySoundEventPrimitive.Execute(context, Operand(7, flags: 3));
        Assert.Equal(new short[] { StackObjectId, StackObjectId }, host.PositionalSounds);
    }

    [Fact]
    public void FlagByteFfMakesMeTheOwnerEvenWithTheBitSet()
    {
        var (context, host, me) = Lot();
        host.AcceptingBanks.Add(0);
        PlaySoundEventPrimitive.Execute(context, Operand(7, flags: 0xFF));
        Assert.Equal(new[] { me.Id }, host.PositionalSounds);
    }

    [Fact]
    public void FlagsWithBitOneClearMakeMeTheOwner()
    {
        var (context, host, me) = Lot();
        host.AcceptingBanks.Add(0);
        PlaySoundEventPrimitive.Execute(context, Operand(7, flags: 1));
        Assert.Equal(new[] { me.Id }, host.PositionalSounds);
    }

    [Fact]
    public void EventIdIsASignedShort()
    {
        var (context, host, _) = Lot();
        PlaySoundEventPrimitive.Execute(context, Operand(-2));
        Assert.Equal((short)-2, host.SoundRequests[0].Event);
    }

    [Fact]
    public void PositionalRefusalDoesNotChangeTheResult()
    {
        var (context, host, _) = Lot();
        host.AcceptingBanks.Add(1);
        host.PositionalPlays = false;
        Assert.Equal(PrimitiveResult.True, PlaySoundEventPrimitive.Execute(context, Operand(7)));
    }

    [Fact]
    public void HostlessScopeResolvesSemiGlobalFwavBeforeGlobalAndHonorsAnyOwnerPanZoom()
    {
        var privateFile = new SimsEngine.Formats.IffFile(ChunkBuilder.File(), "private.iff");
        var semiGlobalFile = new SimsEngine.Formats.IffFile(
            ChunkBuilder.File(("FWAV", 10, new ChunkBuilder().CString("semi_sound").ToArray())), "semiglobal.iff");
        var globalFile = new SimsEngine.Formats.IffFile(
            ChunkBuilder.File(("FWAV", 10, new ChunkBuilder().CString("global_sound").ToArray())), "global.iff");
        var scope = new SimsEngine.Content.BehaviorScope(globalFile, privateFile, semiGlobalFile);

        var (baseContext, _, me) = Lot();
        baseContext.World.Host = null;
        var calls = new List<(string Name, short Owner, bool Pan, bool Zoom)>();
        baseContext.World.PlaySoundEvent = (name, owner, pan, zoom) =>
        {
            calls.Add((name, owner, pan, zoom));
            return true;
        };
        var context = baseContext with { Frame = new StackFrame(StackObjectId, [], []) { Table = scope } };

        Assert.Equal(PrimitiveResult.True, PlaySoundEventPrimitive.Execute(context, Operand(10, flags: 0xFF)));
        Assert.Equal(PrimitiveResult.True, PlaySoundEventPrimitive.Execute(context, Operand(10, flags: 0x0C)));

        Assert.Equal(new[]
        {
            ("semi_sound", me.Id, true, true),
            ("semi_sound", me.Id, false, false),
        }, calls);
    }
}
