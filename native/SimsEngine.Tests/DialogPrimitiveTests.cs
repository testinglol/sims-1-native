using SimsEngine.Simulation;
using SimsEngine.Content;
using SimsEngine.Formats;
using Xunit;

namespace SimsEngine.Tests;

public sealed class DialogPrimitiveTests
{
    // In the dialog primitive operand, byte 5 specifies the case number and byte 7 specifies flags, where
    // bit 0 triggers record creation.
    private static byte[] Operand(byte caseNumber, byte flags = 0) => [0, 0, 0, 0, 0, caseNumber, 0, flags];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Fact]
    public void NativeMessageDialogUsesCodeOwnerStringsAndResumesOnOk()
    {
        var (context, me, _) = PrimitiveTestWorld.Create(stackObject: 0);
        context.World.Host = null;
        var strings = new[] { "", "", "", "Note:", "There is nothing to bill for. ( No billable objects on the lot )" };
        var table = new ChunkBuilder().S16((short)strings.Length);
        foreach (var value in strings)
        {
            var bytes = System.Text.Encoding.Latin1.GetBytes(value);
            table.U8((byte)bytes.Length).Raw(bytes);
        }
        var (privateFile, _) = table.InFile("STR#", 301);
        var globalFile = new IffFile(ChunkBuilder.File(), "empty-global.iff");
        var wrongTable = new ChunkBuilder().S16((short)1).U8(5).Raw(System.Text.Encoding.Latin1.GetBytes("wrong"));
        var (wrongPrivateFile, _) = wrongTable.InFile("STR#", 301);
        me.TypeRecord = new TypeRecord(1) { BehaviorScope = new BehaviorScope(globalFile, wrongPrivateFile, null) };
        context = context with
        {
            Frame = new StackFrame(0, [], [0, 0])
            {
                Table = new BehaviorScope(globalFile, privateFile, null),
                TreeId = 4106,
            },
        };
        var operand = new byte[] { 0, 0, 5, 0, 0, 0, 4, 0 };

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, operand));
        var record = Assert.Single(context.World.DialogRecords);
        Assert.Equal("Note:", record.Title);
        Assert.Equal(strings[4], record.Message);
        Assert.True(record.Visible);

        context.World.RespondMessageDialog(record.Token, 0);
        Assert.Equal(PrimitiveResult.True, DialogPrimitive.Execute(context, operand));
        Assert.Empty(context.World.DialogRecords);
    }

    [Fact]
    public void NativeMessageDialogExpandsMeFromTheObjectNameNotAppearanceStrings()
    {
        var (context, me, _) = PrimitiveTestWorld.Create(stackObject: 0);
        context.World.Host = null;
        var table = new ChunkBuilder().S16((short)1).U8(8).Raw(System.Text.Encoding.Latin1.GetBytes("Hi, $Me!"));
        var (privateFile, _) = table.InFile("STR#", 301);
        var globalFile = new IffFile(ChunkBuilder.File(), "empty-global.iff");
        me.TypeRecord = new TypeRecord(1)
        {
            Name = "Bella",
            BehaviorScope = new BehaviorScope(globalFile, privateFile, null),
        };
        me.SavedCharacter = new SimsEngine.Formats.PersonCharacter(0, 0, [], [], ["", "", "wrong appearance field"], null, []);
        var operand = new byte[] { 0, 0, 1, 0, 0, 0, 0, 0 };

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, operand));
        var dialog = Assert.Single(context.World.DialogRecords);
        Assert.Equal("Hi, Bella!", dialog.Message);
        Assert.Equal("Bella", dialog.Title);
    }

    [Fact]
    public void NativeMessageDialogWithMissingMessageStringFallsBackToEmptyString()
    {
        var (context, me, _) = PrimitiveTestWorld.Create(stackObject: 0);
        context.World.Host = null;
        me.Name = "Mortimer";
        var operand = new byte[] { 0, 0, 99, 0, 0, 0, 0, 0 };

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, operand));
        var record = Assert.Single(context.World.DialogRecords);
        Assert.Equal("", record.Message);
        Assert.Equal("Mortimer", record.Title);
    }

    [Theory]
    [InlineData(0, 2, true)]
    [InlineData(1, 3, false)]
    public void GenericYesNoDialogKindResumesWithTheSelectedBranch(byte response, int expectedState, bool expectedResult)
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 0);
        context.World.Host = null;
        var strings = new[] { "Message", "Yes", "No", "Title" };
        var table = new ChunkBuilder().S16((short)strings.Length);
        foreach (var value in strings)
        {
            var bytes = System.Text.Encoding.Latin1.GetBytes(value);
            table.U8((byte)bytes.Length).Raw(bytes);
        }
        var (privateFile, _) = table.InFile("STR#", 301);
        var globalFile = new IffFile(ChunkBuilder.File(), "empty-global.iff");
        context = context with { Frame = new StackFrame(0, [], [0, 0]) { Table = new BehaviorScope(globalFile, privateFile, null) } };
        var operand = new byte[] { 0, 0, 1, 2, 3, 1, 4, 0 };

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, operand));
        var record = Assert.Single(context.World.DialogRecords);
        Assert.Equal((byte)1, record.Kind);
        Assert.Equal("Title", record.Title);
        Assert.Equal("Message", record.Message);
        Assert.Equal("Yes", record.Yes);
        Assert.Equal("No", record.No);
        Assert.Null(record.Cancel);
        context.World.RespondMessageDialog(record.Token, response);
        Assert.Equal(expectedState, record.State);
        Assert.Equal(expectedResult ? PrimitiveResult.True : PrimitiveResult.False, DialogPrimitive.Execute(context, operand));
        Assert.Empty(context.World.DialogRecords);
    }

    [Fact]
    public void TheOwnersDialogRecordIsLookedUpFirst()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, Operand(5)));
        Assert.Equal("DialogRecordState(100)", Assert.Single(host.Calls));
    }

    [Fact]
    public void StateOneDialogRecordIsDismissedBeforeTheOperandRuns()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        host.DialogState = 1;

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, Operand(5)));
        Assert.Equal(-1, context.World.Dialog[0xea]);
        Assert.Equal(["DialogRecordState(100)", "DismissDialogRecord(100)"], host.Calls);
    }

    [Fact]
    public void AFreeLockIsReservedAndStops()
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, Operand(5)));
        Assert.Equal(-1, context.World.Dialog[0xea]);
    }

    [Fact]
    public void AHeldLockGivesItsValueToTheTemporaryAndIsFreed()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.Dialog[0xea] = 7;
        Assert.Equal(PrimitiveResult.True, DialogPrimitive.Execute(context, Operand(5)));
        Assert.Equal(7, me.Temporaries[0]);
        Assert.Equal(0, context.World.Dialog[0xea]);
    }

    [Theory]
    [InlineData(7, 0xec)]
    [InlineData(8, 0xf2)]
    [InlineData(0xb, 0xee)]
    [InlineData(0xd, 0xf0)]
    public void EachLockCaseUsesItsOwnWord(byte caseNumber, int offset)
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.Dialog[offset] = 4;
        Assert.Equal(PrimitiveResult.True, DialogPrimitive.Execute(context, Operand(caseNumber)));
        Assert.Equal(4, me.Temporaries[0]);
        Assert.Equal(0, context.World.Dialog[offset]);
    }

    [Fact]
    public void CaseDWithFreeLockReservesWhenTheGlobalByteIsZero()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, Operand(0xd)));
        Assert.Equal(-1, context.World.Dialog[0xf0]);
        Assert.Equal(["DialogRecordState(100)", "DialogGlobalFlagF8"], host.Calls);
    }

    [Fact]
    public void CaseDWithFreeLockLeavesItFreeWhenTheGlobalByteIsNonzero()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        host.DialogGlobalFlagF8Answer = 1;

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, Operand(0xd)));
        Assert.Equal(0, context.World.Dialog[0xf0]);
        Assert.Equal(["DialogRecordState(100)", "DialogGlobalFlagF8"], host.Calls);
    }

    [Fact]
    public void HolderTakesTheDialogAndStopsWithTheTemporarySaved()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 9;
        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, Operand(6)));
        Assert.Equal(100, context.World.Dialog[0xf8]);
        Assert.Equal(9, context.World.Dialog[0xf4]);
    }

    [Fact]
    public void CaseEUsesItsOwnHolderAndSavedWord()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 9;
        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, Operand(0xe)));
        Assert.Equal(100, context.World.Dialog[0xfc]);
        Assert.Equal(9, context.World.Dialog[0xf6]);
        Assert.Equal(0, context.World.Dialog[0xf8]);
    }

    [Fact]
    public void TheHolderReleasesWithTheFlagAndAnswersTrue()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.Dialog[0xf8] = 100;
        context.World.Dialog[0xf4] = -2;
        context.World.Dialog[0xf6] = 5;
        Assert.Equal(PrimitiveResult.True, DialogPrimitive.Execute(context, Operand(6)));
        Assert.Equal(5, me.Temporaries[0]);
        Assert.Equal(0, context.World.Dialog[0xf8]);
        Assert.Equal(0, context.World.Dialog[0xf4]);
    }

    [Fact]
    public void TheHolderReleasesWithoutTheFlagAndAnswersFalse()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.Dialog[0xf8] = 100;
        context.World.Dialog[0xf6] = 5;
        Assert.Equal(PrimitiveResult.False, DialogPrimitive.Execute(context, Operand(6)));
        Assert.Equal(5, me.Temporaries[0]);
        Assert.Equal(0, context.World.Dialog[0xf8]);
    }

    [Fact]
    public void AnotherHolderIsClearedAndAnswersFalse()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 4;
        context.World.Dialog[0xf8] = 55;
        context.World.Dialog[0xf4] = 9;
        Assert.Equal(PrimitiveResult.False, DialogPrimitive.Execute(context, Operand(6)));
        Assert.Equal(0, me.Temporaries[0]);
        Assert.Equal(0, context.World.Dialog[0xf8]);
        Assert.Equal(0, context.World.Dialog[0xf4]);
    }

    [Fact]
    public void CaseNineReservesForThePersonInTheStackObjectsTableEntry()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        var selected = context.World.Add(new XPerson(201, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        context.Me.SetObjectTableEntry(2, selected);

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, Operand(9)));
        Assert.Equal(201, context.World.Dialog[0x104]);
    }

    [Fact]
    public void CaseNineReleaseReturnsTheSavedValueWhenReleaseMarkIsSet()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        var selected = context.World.Add(new XPerson(201, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        context.Me.SetObjectTableEntry(2, selected);
        context.World.Dialog[0x104] = selected.Id;
        context.World.Dialog[0x100] = 42;
        context.World.Dialog[0x108] = -2;

        Assert.Equal(PrimitiveResult.True, DialogPrimitive.Execute(context, Operand(9)));
        Assert.Equal(42, context.Me.Temporaries[0]);
        Assert.Equal(0, context.World.Dialog[0x104]);
        Assert.Equal(42, context.World.Dialog[0x100]);
    }

    [Fact]
    public void CaseNineUnmarkedReleaseResetsTheWordAndAnswersFalse()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        var selected = context.World.Add(new XPerson(201, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        context.Me.SetObjectTableEntry(2, selected);
        context.World.Dialog[0x104] = selected.Id;
        context.World.Dialog[0x100] = 42;

        Assert.Equal(PrimitiveResult.False, DialogPrimitive.Execute(context, Operand(9)));
        Assert.Equal(42, context.Me.Temporaries[0]);
        Assert.Equal(-1, context.World.Dialog[0x100]);
        Assert.Equal(0, context.World.Dialog[0x104]);
    }

    [Fact]
    public void CaseNineNeedsAStackObjectPerson()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        context.World.Add(new XObject(201, PrimitiveTestWorld.Definition()));
        context.Me.SetObjectTableEntry(2, context.World.Objects[201]);

        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, Fails(() => DialogPrimitive.Execute(context, Operand(9))));
    }

    [Fact]
    public void CaseATakesAndPublishesNegativeNeighborId()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        me[PersonData.NeighborId] = 37;

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, Operand(0xa)));
        Assert.Equal(-37, context.World.Dialog[0xe8]);
    }

    [Theory]
    [InlineData(12, true)]
    [InlineData(-12, false)]
    public void CaseAConsumesPostedWordAndOnlyCopiesPositiveValues(short posted, bool result)
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 7;
        context.World.Dialog[0xe8] = posted;

        Assert.Equal(result ? PrimitiveResult.True : PrimitiveResult.False, DialogPrimitive.Execute(context, Operand(0xa)));
        Assert.Equal(posted > 0 ? posted : (short)7, me.Temporaries[0]);
        Assert.Equal(0, context.World.Dialog[0xe8]);
    }

    [Fact]
    public void HoldingCasesNeedAPerson()
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        var plain = context.World.Add(new XObject(101, PrimitiveTestWorld.Definition(0)));
        var asPlain = new TreeContext(context.World, plain, context.Frame);
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, Fails(() => DialogPrimitive.Execute(asPlain, Operand(6))));
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, Fails(() => DialogPrimitive.Execute(asPlain, Operand(0xe))));
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, Fails(() => DialogPrimitive.Execute(asPlain, Operand(0xa))));
        Assert.Equal(0, context.World.Dialog[0xf8]);
    }

    private static TreeContext WithDialogStrings(TreeContext context, params string[] strings)
    {
        var table = new ChunkBuilder().S16((short)strings.Length);
        foreach (var value in strings)
        {
            var bytes = System.Text.Encoding.Latin1.GetBytes(value);
            table.U8((byte)bytes.Length).Raw(bytes);
        }
        var (privateFile, _) = table.InFile("STR#", 301);
        var globalFile = new IffFile(ChunkBuilder.File(), "empty-global.iff");
        return context with { Frame = new StackFrame(0, [], [7, 0]) { Table = new BehaviorScope(globalFile, privateFile, null) } };
    }

    [Fact]
    public void OperandBitZeroCreatesTheGenericRecordThroughAHost()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 0);
        context = WithDialogStrings(context, "Pay $Local:0 now");

        Assert.Equal(PrimitiveResult.True, DialogPrimitive.Execute(context, [0, 0, 1, 0, 0, 0, 0, 1]));
        Assert.Equal("Pay 7 now", Assert.Single(context.World.DialogRecords).Message);
        Assert.Equal("DialogRecordState(100)", Assert.Single(host.Calls));
    }

    [Fact]
    public void HostedRecordStateBranchCreatesThenResumesWithTheDeclinedAnswer()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 0);
        context = WithDialogStrings(context, "Message", "Yes", "No", "Title", "Cancel");
        var operand = new byte[] { 5, 0, 1, 2, 3, 2, 4, 0x20 };

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, operand));
        var record = Assert.Single(context.World.DialogRecords);
        Assert.Equal((byte)2, record.Kind);

        host.DialogState = 3;
        context.World.Dialog[0x100] = 4;
        me.Temporaries[2] = 9;
        Assert.Equal(PrimitiveResult.False, DialogPrimitive.Execute(context, operand));
        Assert.Equal(0, me.Temporaries[2]);
        Assert.Equal(-1, context.World.Dialog[0x100]);
        Assert.Empty(context.World.DialogRecords);
    }

    [Fact]
    public void HostedRecordStateBranchWaitsWhileTheRecordIsOpen()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 0);
        context = WithDialogStrings(context, "Message");
        var operand = new byte[] { 0, 0, 1, 0, 0, 0, 0, 0 };
        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, operand));

        host.DialogState = 0;
        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, operand));
        host.DialogState = 2;
        Assert.Equal(PrimitiveResult.True, DialogPrimitive.Execute(context, operand));
        Assert.Empty(context.World.DialogRecords);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(0xb)]
    [InlineData(0xd)]
    public void SpecializedKindsCreateTheirRecordWithoutReadingStrings(int kind)
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 0);

        Assert.Equal(PrimitiveResult.True, DialogPrimitive.Execute(context, [0, 0, 1, 0, 0, (byte)kind, 0, 1]));
        var record = Assert.Single(context.World.DialogRecords);
        Assert.Equal((byte)kind, record.Kind);
        Assert.Equal(string.Empty, record.Message);
    }

    [Fact]
    public void RecordKindsAbove15UseTheGenericRecord()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 0);
        context.World.Host = null;
        context = WithDialogStrings(context, "Message");

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, [0, 0, 1, 0, 0, 0x20, 0, 0]));
        Assert.Equal((byte)0x20, Assert.Single(context.World.DialogRecords).Kind);
    }

    [Theory]
    [InlineData(0xc, 0)]
    [InlineData(0xf, 1)]
    public void DialogStylesArePresentedByTheHost(byte caseNumber, byte style)
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Equal(PrimitiveResult.True, DialogPrimitive.Execute(context, Operand(caseNumber)));
        Assert.Equal(["DialogRecordState(100)", $"ShowDialog(100,{style})"], host.Calls);
    }

    [Theory]
    [InlineData(0xc)]
    [InlineData(0xf)]
    public void DialogStylesRequireARunningPerson(byte caseNumber)
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        var plain = context.World.Add(new XObject(101, PrimitiveTestWorld.Definition()));
        var asPlain = new TreeContext(context.World, plain, context.Frame);
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson,
            Fails(() => DialogPrimitive.Execute(asPlain, Operand(caseNumber))));
    }

    [Fact]
    public void AutoIconModeResolvesMultiTileMasterForStackObject()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 202);
        context.World.Host = null;
        var master = context.World.Add(new XObject(201, PrimitiveTestWorld.Definition()));
        var part = context.World.Add(new XObject(202, PrimitiveTestWorld.Definition()) { MultiTileMasterId = 201 });
        context = WithDialogStrings(context, "Broken $Object") with
        {
            Frame = new StackFrame(part.Id, [], [7, 0]) { Table = WithDialogStrings(context, "Broken $Object").Frame.Table },
        };

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, [0, 0, 1, 0, 0, 0, 0, 0]));
        var record = Assert.Single(context.World.DialogRecords);
        Assert.Same(master, record.Icon);
    }

    [Fact]
    public void AutoIconModeSelectsNeighborWhenNeighborTokenIsExpanded()
    {
        var (context, me, _) = PrimitiveTestWorld.Create(stackObject: 7);
        context.World.Host = null;
        me.TypeRecord = new TypeRecord(1);
        var bella = context.World.Add(new XPerson(205, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(0x12345678),
            Name = "Bella",
        });
        bella[PersonData.NeighborId] = 7;
        context.World.Neighbors[7] = new NeighborState(7, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            Name = "Bella",
            TypeRecordKey = 0x12345678,
        };
        var baseCtx = WithDialogStrings(context, "Hello $Neighbor!");
        context = baseCtx with { Frame = new StackFrame(7, [], [7, 0]) { Table = baseCtx.Frame.Table } };

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, [0, 0, 1, 0, 0, 0, 0, 0]));
        var record = Assert.Single(context.World.DialogRecords);
        Assert.Same(bella, record.Icon);
        Assert.Equal("Hello Bella!", record.Message);
    }

    [Fact]
    public void NoneIconModeClearsDialogIcon()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 201);
        context.World.Host = null;
        context.World.Add(new XObject(201, PrimitiveTestWorld.Definition()));
        var baseCtx = WithDialogStrings(context, "No icon");
        context = baseCtx with { Frame = new StackFrame(201, [], [7, 0]) { Table = baseCtx.Frame.Table } };

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, [0, 0, 1, 0, 0, 0, 0, 0x02]));
        var record = Assert.Single(context.World.DialogRecords);
        Assert.Null(record.Icon);
        Assert.Null(record.ImageFile);
    }

    [Fact]
    public void NeighborIconModeResolvesNeighborFromStackObjectId()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 9);
        context.World.Host = null;
        var mortimer = context.World.Add(new XPerson(206, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        mortimer[PersonData.NeighborId] = 9;
        context.World.Neighbors[9] = new NeighborState(9, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            Name = "Mortimer",
            TypeRecordKey = 0x23456789,
        };
        var baseCtx = WithDialogStrings(context, "Neighbor mode");
        context = baseCtx with { Frame = new StackFrame(9, [], [7, 0]) { Table = baseCtx.Frame.Table } };

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, [0, 0, 1, 0, 0, 0, 0, 0x04]));
        var record = Assert.Single(context.World.DialogRecords);
        Assert.Same(mortimer, record.Icon);
    }

    [Fact]
    public void IndexedIconModeUsesFiveThousandPlusOperandInCodeOwnerScope()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 0);
        context.World.Host = null;
        context = WithDialogStrings(context, "Indexed icon");

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, [0, 7, 1, 0, 0, 0, 0, 0x06]));
        var record = Assert.Single(context.World.DialogRecords);
        Assert.Null(record.Icon);
        Assert.Equal((ushort)5007, record.ImageBitmapId);
        Assert.NotNull(record.ImageFile);
    }

    [Fact]
    public void DynamicIconModeMyUsesCallerPrivateFileAndParsedBitmapId()
    {
        var (context, me, _) = PrimitiveTestWorld.Create(stackObject: 0);
        context.World.Host = null;
        var (mePrivate, _) = new ChunkBuilder().S16(0).InFile("STR#", 301);
        var globalFile = new IffFile(ChunkBuilder.File(), "empty-global.iff");
        me.TypeRecord = new TypeRecord(1) { BehaviorScope = new BehaviorScope(globalFile, mePrivate, null) };
        context = WithDialogStrings(context, "Message", "my 2002");

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, [0, 2, 1, 0, 0, 0, 0, 0x08]));
        var record = Assert.Single(context.World.DialogRecords);
        Assert.Same(mePrivate, record.ImageFile);
        Assert.Equal((ushort)2002, record.ImageBitmapId);
    }

    [Theory]
    [InlineData("gz 4500", "dialog-spec:gz 4500", 4500)]
    [InlineData("gzi 9005", "dialog-spec:gzi 9005", 9005)]
    [InlineData("guid 0x98E32357", "dialog-spec:guid 0x98E32357", 0x2357)]
    [InlineData("job 3", "dialog-spec:job 3", 3)]
    public void DynamicIconModeEncodesCommandSpecifications(string spec, string expectedFileName, ushort expectedBitmapId)
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 0);
        context.World.Host = null;
        context = WithDialogStrings(context, "Message", spec);

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, [0, 2, 1, 0, 0, 0, 0, 0x08]));
        var record = Assert.Single(context.World.DialogRecords);
        Assert.Equal(expectedFileName, record.ImageFile?.Source);
        Assert.Equal(expectedBitmapId, record.ImageBitmapId);
    }

    [Fact]
    public void DynamicIconModeRelEncodesNeighborGuidAndRelationshipStripId()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 5);
        context.World.Host = null;
        context.World.Neighbors[5] = new NeighborState(5, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            Name = "Cassandra",
            TypeRecordKey = unchecked((int)0xABCDEF01),
        };
        var baseCtx = WithDialogStrings(context, "Message", "rel");
        context = baseCtx with { Frame = new StackFrame(5, [], [7, 0]) { Table = baseCtx.Frame.Table } };

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(context, [0, 2, 1, 0, 0, 0, 0, 0x08]));
        var record = Assert.Single(context.World.DialogRecords);
        Assert.Equal("dialog-spec:rel 0xABCDEF01", record.ImageFile?.Source);
        Assert.Equal((ushort)2003, record.ImageBitmapId);
    }
}
