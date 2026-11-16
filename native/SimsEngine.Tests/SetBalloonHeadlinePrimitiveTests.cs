using System.Buffers.Binary;
using System.Text;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class SetBalloonHeadlinePrimitiveTests
{
    // Operand encodes owner and local index in word 0, text or type selector in byte 2, string table in
    // byte 3, value in word 4, duration kind in byte 6, and control flags in byte 7.
    private static byte[] Operand(short word0, byte selector, byte table, short value, byte duration, byte flags) =>
        [(byte)word0, (byte)((ushort)word0 >> 8), selector, table, (byte)value, (byte)((ushort)value >> 8), duration, flags];

    private static IffFile SpriteFile(ushort spriteId, int frameCount)
    {
        const int iffHeader = 64;
        const int chunkHeader = 76;
        const int spriteHeader = 12;
        const int frameSize = 16;
        var spriteBodySize = spriteHeader + frameCount * 4 + frameCount * frameSize;
        var data = new byte[iffHeader + chunkHeader + spriteBodySize];
        Encoding.ASCII.GetBytes("IFF FILE").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(60), iffHeader);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(iffHeader), ResourceType.Spr1.Code);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(iffHeader + 4), (uint)(chunkHeader + spriteBodySize));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(iffHeader + 8), spriteId);
        Encoding.ASCII.GetBytes("fixture").CopyTo(data, iffHeader + 12);
        var body = data.AsSpan(iffHeader + chunkHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(body, 502);
        BinaryPrimitives.WriteUInt32LittleEndian(body[4..], (uint)frameCount);
        BinaryPrimitives.WriteUInt32LittleEndian(body[8..], uint.MaxValue);
        var firstFrame = spriteHeader + frameCount * 4;
        for (var index = 0; index < frameCount; index++)
        {
            var frameOffset = firstFrame + index * frameSize;
            BinaryPrimitives.WriteUInt32LittleEndian(body[(spriteHeader + index * 4)..], (uint)frameOffset);
            var frame = body[frameOffset..];
            BinaryPrimitives.WriteUInt16LittleEndian(frame[4..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(frame[6..], 1);
            new byte[] { 4, 1, 3, 1, 7, 0, 5, 0 }.CopyTo(frame[8..]);
        }
        return new IffFile(data, "fixture Sprites.iff");
    }

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    private static byte[] Text(byte flags = 0, short value = 3, byte table = 2) => Operand(0, 9, table, value, 1, flags);

    private static readonly string[] TextTail =
    [
        "HasBalloonEntries(100)", "ClaimBalloon(100,1)", "ShowBalloonText(2,9,0)", "BalloonDuration(1)",
        "SetBalloonFields(100,101,0,False)", "SetBalloonState(100,3)", "RefreshDisplay", "NotifyObjectTable(100)",
    ];

    private static void TypedObject(TreeContext context, PrimitiveTestHost host, short index)
    {
        var entry = new XObject(50, PrimitiveTestWorld.Definition()) { TypeRecord = new TypeRecord(77) };
        host.ObjectTable[index] = entry;
        context.Me.SetObjectTableEntry(index, entry);
        host.Alternatives[entry.TypeRecord!] = new TypeRecord(88);
    }

    [Fact]
    public void AHostlessWorldHasNoBalloonRecordsAndAnswersTrue()
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(context, Text()));
    }

    [Fact]
    public void HostlessClearUsesTheFirstRuntimeSpriteSlotAndHonorsPriority()
    {
        var (context, owner, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        var slot = new ObjectRuntimeSlot();
        slot.WriteWord(0x10, 12);
        slot.WriteWord(0x28, 7);
        slot.WriteWord(0x34, 2);
        owner.RuntimeSlots.Add(slot);

        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(context, Text(value: 0)));
        Assert.Equal(12, slot.ReadWord(0x10));
        Assert.Equal(7, slot.ReadWord(0x28));

        Assert.Equal(PrimitiveResult.True,
            SetBalloonHeadlinePrimitive.Execute(context, Text(value: 0, table: 4)));
        Assert.Equal(2, slot.ReadWord(0x34));
        Assert.Equal(0, slot.ReadWord(0x10));
        Assert.Equal(0, slot.ReadWord(0x28));
    }

    [Fact]
    public void HostlessTextBalloonAssignsTheIconGroupSpriteAndThreeZoomFrameCycle()
    {
        var (context, owner, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        context.World.GlobalSprites = SpriteFile(109, 3);
        Assert.NotNull(context.World.GlobalSprites.Find(ResourceType.Spr1, 109));
        Assert.Equal(109, IconGroupSpriteIds.GetSpriteId(1, 9));
        Assert.Equal(3, Sprite.Read(context.World.GlobalSprites, context.World.GlobalSprites.Find(ResourceType.Spr1, 109)!).Frames.Count);
        var slot = new ObjectRuntimeSlot();
        owner.RuntimeSlots.Add(slot);

        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(context, Text(table: 1)));

        Assert.Equal(109, slot.ReadWord(0x14)); // Balloon sprite ID is computed as IconGroup base 100 plus text ID.
        Assert.Equal(0, slot.ReadWord(0x18));
        Assert.Equal(1, slot.ReadWord(0x1c)); // Sprite frame count divided across the three zoom levels.
        Assert.Equal(0, slot.ReadWord(0x20));
        Assert.Equal(1, slot.ReadWord(0x24));
        Assert.Equal(101, slot.ReadWord(0x2c));
        Assert.Equal(0, slot.ReadWord(0x30));
        Assert.Equal(0, slot.ReadWord(0x38));
        Assert.Equal(1, slot.ReadWord(0x34));
        Assert.Equal(3, slot.ReadWord(0x10));
        Assert.True(context.World.DisplayRefreshRequested);
        Assert.Equal(-2, context.World.ClockManager.WaitTicksAt(owner.ClockManagerIndex));
    }

    [Fact]
    public void HostlessTextBalloonUsesLabelSpriteReversePhaseAndLoopDurationFromOperandBits()
    {
        var (context, owner, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        context.World.GlobalSprites = SpriteFile(109, 3);
        var slot = new ObjectRuntimeSlot();
        owner.RuntimeSlots.Add(slot);

        // Byte 7 flag 0x0E maps to runtime flags for label display (0x200), reverse animation phase
        // (0x400), and loop duration (0x800).
        Assert.Equal(PrimitiveResult.True,
            SetBalloonHeadlinePrimitive.Execute(context, Text(flags: 0x0e, value: 2, table: 1)));

        Assert.Equal(109, slot.ReadWord(0x14));
        Assert.Equal(0, slot.ReadWord(0x20)); // Reverse animation phase starts playback at frame count minus one.
        Assert.Equal(-1, slot.ReadWord(0x24));
        Assert.Equal(101, slot.ReadWord(0x2c)); // Balloon type 1 maps to thought balloon sprite ID 101.
        Assert.Equal(103, slot.ReadWord(0x30)); // IconGroup 1 base 100 offset by label icon index 3.
        Assert.Equal(24, slot.ReadWord(0x10)); // Balloon duration is calculated as loops * frames * 12 ticks.

        // Flag bit 0x02 enables the label sprite without reversing the animation phase.
        Assert.Equal(PrimitiveResult.True,
            SetBalloonHeadlinePrimitive.Execute(context, Text(flags: 0x02, value: 2, table: 1)));
        Assert.Equal(1, slot.ReadWord(0x24));
        Assert.Equal(103, slot.ReadWord(0x30));

        // Flag bit 0x04 reverses the animation playback phase without enabling a label sprite.
        Assert.Equal(PrimitiveResult.True,
            SetBalloonHeadlinePrimitive.Execute(context, Text(flags: 0x04, value: 2, table: 1)));
        Assert.Equal(-1, slot.ReadWord(0x24));
        Assert.Equal(0, slot.ReadWord(0x30));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void HostlessTypeBalloonUsesTheMasterSelectorAndCommonSlotTail(byte selector)
    {
        var (context, owner, _) = PrimitiveTestWorld.Create(stackObject: 5);
        context.World.Host = null;
        var selected = context.World.Add(new XObject(50, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(77) { AlternativeTypeKey = 88 },
        });
        context.World.Neighbors.Add(5, new NeighborState(5, PrimitiveTestWorld.Definition()) { TypeRecordKey = 77 });
        if (selector == 2)
        {
            context.Frame.Locals[1] = 6;
            owner.SetObjectTableEntry(6, selected);
        }
        else owner.SetObjectTableEntry(5, selected);
        var slot = new ObjectRuntimeSlot();
        owner.RuntimeSlots.Add(slot);

        Assert.Equal(PrimitiveResult.True,
            SetBalloonHeadlinePrimitive.Execute(context, Operand(selector == 2 ? (short)2 : (short)0, selector, 7, 3, 1, 0)));

        Assert.Equal(88, slot.SelectorTypeKey);
        Assert.Equal(-1, slot.ReadWord(0x14));
        Assert.Equal(0, slot.ReadWord(0x18));
        Assert.Equal(1, slot.ReadWord(0x1c));
        Assert.Equal(101, slot.ReadWord(0x2c));
        Assert.Equal(0, slot.ReadWord(0x30));
        Assert.Equal(3, slot.ReadWord(0x10));
        Assert.True(context.World.DisplayRefreshRequested);
        Assert.Equal(-2, context.World.ClockManager.WaitTicksAt(owner.ClockManagerIndex));
    }

    [Fact]
    public void TextBalloonRunsTheWholeTailInOrder()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(context, Text()));
        Assert.Equal(TextTail, host.Calls);
    }

    [Fact]
    public void OwnerBitOneUsesTheStackObject()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        context.World.Add(new XObject(5, PrimitiveTestWorld.Definition()));
        SetBalloonHeadlinePrimitive.Execute(context, Operand(1, 9, 2, 3, 1, 0));
        Assert.Equal(new[] { "HasBalloonEntries(5)", "ClaimBalloon(5,1)" }, host.Calls.GetRange(0, 2));
        Assert.Equal("NotifyObjectTable(5)", host.Calls[^1]);
    }

    [Fact]
    public void MissingStackOwnerRaisesNoStackObject()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => SetBalloonHeadlinePrimitive.Execute(context, Operand(1, 9, 2, 3, 1, 0))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void RunningOwnerIgnoresAMissingStackObject()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(context, Text()));
        Assert.Equal(TextTail, host.Calls);
    }

    [Fact]
    public void NoBalloonEntriesAnswersTrueAfterTheCheck()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        host.HasBalloons = false;
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(context, Text()));
        Assert.Equal(new[] { "HasBalloonEntries(100)" }, host.Calls);
    }

    [Fact]
    public void ARefusedClaimAnswersTrueWithoutShowing()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        host.ClaimAnswer = false;
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(context, Text()));
        Assert.Equal(new[] { "HasBalloonEntries(100)", "ClaimBalloon(100,1)" }, host.Calls);
    }

    [Fact]
    public void ARefusedClaimIsCheckedBeforeTheZeroValue()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        host.ClaimAnswer = false;
        SetBalloonHeadlinePrimitive.Execute(context, Text(value: 0));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("SetBalloonState"));
    }

    [Fact]
    public void TableFourClaimsPriorityTwo()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        SetBalloonHeadlinePrimitive.Execute(context, Text(table: 4));
        Assert.Equal("ClaimBalloon(100,2)", host.Calls[1]);
        Assert.Contains("ShowBalloonText(4,9,0)", host.Calls);
    }

    [Fact]
    public void ZeroValueClearsTheStateAndStops()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(context, Text(value: 0)));
        Assert.Equal(new[] { "HasBalloonEntries(100)", "ClaimBalloon(100,1)", "SetBalloonState(100,0)" }, host.Calls);
    }

    [Fact]
    public void PermanentValueIsNeverTimed()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        SetBalloonHeadlinePrimitive.Execute(context, Text(flags: 0x08, value: -1));
        Assert.Contains("SetBalloonState(100,-1)", host.Calls);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("SetBalloonStateTimed"));
    }

    [Fact]
    public void BitElevenMakesTheValueTimed()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        SetBalloonHeadlinePrimitive.Execute(context, Text(flags: 0x08, value: 4));
        Assert.Contains("SetBalloonStateTimed(100,4)", host.Calls);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("SetBalloonState(100,4)"));
    }

    [Fact]
    public void TextFlagsUseBitTenAndTheTemporaryOffsetAddsTemporaryZero()
    {
        // Flag 0x14 marks a text balloon and specifies that Temporary 0 is added to the text ID.
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 4;
        SetBalloonHeadlinePrimitive.Execute(context, Text(flags: 0x14));
        Assert.Contains("ShowBalloonText(2,13,1)", host.Calls);
    }

    [Fact]
    public void LabelBitAddsTheLabelBeforeTheFields()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        SetBalloonHeadlinePrimitive.Execute(context, Text(flags: 0x02));
        Assert.Equal(
            new[]
            {
                "HasBalloonEntries(100)", "ClaimBalloon(100,1)", "ShowBalloonText(2,9,0)", "BalloonDuration(1)",
                "BalloonString(1,3)", "SetBalloonFields(100,101,7,False)", "SetBalloonState(100,3)", "RefreshDisplay",
                "NotifyObjectTable(100)",
            },
            host.Calls);
    }

    [Fact]
    public void BitEightSetsTheFieldFlag()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        SetBalloonHeadlinePrimitive.Execute(context, Text(flags: 0x01));
        Assert.Contains("SetBalloonFields(100,101,0,True)", host.Calls);
    }

    [Fact]
    public void AMissingTextStopsBeforeTheTail()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        host.BalloonTextAnswer = false;
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(context, Text()));
        Assert.Equal(TextTail[..3], host.Calls);
    }

    [Fact]
    public void TypeBalloonSelectorZeroShowsTheAlternativeOfTheStackIndexedObject()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        TypedObject(context, host, 5);
        var hostOnly = new XObject(51, PrimitiveTestWorld.Definition()) { TypeRecord = new TypeRecord(99) };
        host.ObjectTable[5] = hostOnly;
        host.Alternatives[hostOnly.TypeRecord!] = new TypeRecord(100);
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(context, Operand(0, 0, 7, 3, 1, 0)));
        Assert.Equal(
            new[]
            {
                "HasBalloonEntries(100)", "ClaimBalloon(100,1)", "AlternativeObjectType(77)",
                "ShowBalloonForType(100,88)", "BalloonDuration(1)", "SetBalloonFields(100,101,0,False)",
                "SetBalloonState(100,3)", "RefreshDisplay", "NotifyObjectTable(100)",
            },
            host.Calls);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("ObjectTableEntry", StringComparison.Ordinal));
    }

    [Fact]
    public void TypeBalloonWithNoObjectStops()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(context, Operand(0, 0, 7, 3, 1, 0)));
        Assert.Equal(new[] { "HasBalloonEntries(100)", "ClaimBalloon(100,1)" }, host.Calls);
    }

    [Fact]
    public void TypeBalloonObjectWithoutATypeRecordStops()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        context.Me.SetObjectTableEntry(5, new XObject(50, PrimitiveTestWorld.Definition()));
        SetBalloonHeadlinePrimitive.Execute(context, Operand(0, 0, 7, 3, 1, 0));
        Assert.Equal(new[] { "HasBalloonEntries(100)", "ClaimBalloon(100,1)" }, host.Calls);
    }

    [Fact]
    public void TypeBalloonSelectorOneShowsTheListValue()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        host.ListValues[5] = 9;
        SetBalloonHeadlinePrimitive.Execute(context, Operand(0, 1, 7, 3, 1, 0));
        Assert.Equal(
            new[]
            {
                "HasBalloonEntries(100)", "ClaimBalloon(100,1)", "ListEntryValue(5)", "ShowBalloonForValue(100,9)",
                "BalloonDuration(1)", "SetBalloonFields(100,101,0,False)", "SetBalloonState(100,3)", "RefreshDisplay",
                "NotifyObjectTable(100)",
            },
            host.Calls);
    }

    [Fact]
    public void TypeBalloonSelectorOneWithNoValueStops()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        SetBalloonHeadlinePrimitive.Execute(context, Operand(0, 1, 7, 3, 1, 0));
        Assert.Equal(new[] { "HasBalloonEntries(100)", "ClaimBalloon(100,1)", "ListEntryValue(5)" }, host.Calls);
    }

    [Fact]
    public void TypeBalloonSelectorTwoReadsTheLocalAtWordZeroShiftedByOne()
    {
        // Word 0 stores the target local variable index in bits 1 through 15.
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        context.Frame.Locals[1] = 5;
        TypedObject(context, host, 5);
        SetBalloonHeadlinePrimitive.Execute(context, Operand(2, 2, 7, 3, 1, 0));
        Assert.Equal("AlternativeObjectType(77)", host.Calls[2]);
        Assert.Equal("ShowBalloonForType(100,88)", host.Calls[3]);
    }

    [Fact]
    public void TypeBalloonSelectorTwoRaisesLocalOutOfRangePastTheLocals()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        Assert.Equal(SimulationError.LocalOutOfRange, Fails(() => SetBalloonHeadlinePrimitive.Execute(context, Operand(4, 2, 7, 3, 1, 0))));
        Assert.Equal(new[] { "HasBalloonEntries(100)", "ClaimBalloon(100,1)" }, host.Calls);
    }

    [Fact]
    public void TypeBalloonSelectorTwoRaisesLocalOutOfRangeForANegativeIndex()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 5);
        Assert.Equal(SimulationError.LocalOutOfRange, Fails(() => SetBalloonHeadlinePrimitive.Execute(context, Operand(-2, 2, 7, 3, 1, 0))));
    }

    [Fact]
    public void TypeBalloonOtherSelectorsStopWithTrue()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(context, Operand(0, 3, 7, 3, 1, 0)));
        Assert.Equal(new[] { "HasBalloonEntries(100)", "ClaimBalloon(100,1)" }, host.Calls);
    }
}
