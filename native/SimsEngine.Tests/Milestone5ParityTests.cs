using System.Text.Json;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.Tests.E2E;
using SimsEngine.World;
using SimsSDL.Core;
using SimsSimulation;
using Xunit;

namespace SimsEngine.Tests;

[Collection("Neighborhoods")]
public sealed class Milestone5ParityTests
{
    private sealed class TestBudget(int funds) : IFamilyBudget
    {
        public int Funds { get; private set; } = funds;
        public List<(int ExpenseType, int Amount)> Charges { get; } = [];
        public int ReadFamilyBudget() => Funds;
        public void AlterFamilyBudget(int expenseType, int amount)
        {
            Funds -= amount;
            Charges.Add((expenseType, amount));
        }
    }

    private static byte[] Operand(short call) =>
        [(byte)call, (byte)(call >> 8), 0, 0, 0, 0, 0, 0];

    private static byte[] ManageInventoryOperand(byte mode, byte key = 0, ushort flags = 0, uint typeField = 0) =>
    [
        mode,
        key,
        (byte)(flags & 0xff),
        (byte)(flags >> 8),
        (byte)(typeField & 0xff),
        (byte)((typeField >> 8) & 0xff),
        (byte)((typeField >> 16) & 0xff),
        (byte)((typeField >> 24) & 0xff),
    ];

    private static short[] PersonDef(short price = 0)
    {
        var def = new short[XObject.DefinitionCount];
        def[(int)ObjdField.Type] = XPerson.PersonType;
        def[(int)ObjdField.Price] = price;
        return def;
    }

    [Fact]
    public void HostlessGenericSimsCallInitialization_PersistsTutorialAndModeFlags()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, PersonDef()) { TypeRecord = new TypeRecord(0x1111) });
        world[SimulationGlobal.IsTutorialHouseNoYes] = 1;
        world.NeighborhoodData[2] = 9;

        var lotPersisted = 0;
        var nghPersisted = 0;
        world.PersistLotSimulationStateImmediately = () => lotPersisted++;
        world.PersistNeighborhoodStateImmediately = () => nghPersisted++;

        var ctx = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, Operand(0x00)));
        Assert.Equal((short)0, world[SimulationGlobal.IsTutorialHouseNoYes]);
        Assert.Equal((short)1, world.NeighborhoodData[1]);
        Assert.Equal((short)0, world.NeighborhoodData[2]);
        Assert.Equal(1, lotPersisted);
        Assert.Equal(1, nghPersisted);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, Operand(0x08)));
        Assert.Equal((short)2, world.NeighborhoodData[1]);
        Assert.Equal(1, lotPersisted);
        Assert.Equal(2, nghPersisted);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, Operand(0x09)));
        Assert.Equal((short)3, world.NeighborhoodData[1]);
        Assert.Equal(1, lotPersisted);
        Assert.Equal(3, nghPersisted);
    }

    [Fact]
    public void HostlessGenericSimsCall03_ClearsHandlesAndInvokesOptionalCallbacks()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, PersonDef()) { TypeRecord = new TypeRecord(0x1111) });
        world.GenericSimsCall03State.PrimaryHandle = 11;
        world.GenericSimsCall03State.SecondaryHandle = 22;
        world.GenericSimsCall03State.LinkedGlobalWord60 = 33;

        var releasedPrimary = 0;
        var releasedSecondary = 0;
        world.ReleaseGenericSimsCall03PrimaryHandle = h => releasedPrimary = h;
        world.ReleaseGenericSimsCall03SecondaryHandle = h => releasedSecondary = h;

        var ctx = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, Operand(0x03)));
        Assert.Equal(11, releasedPrimary);
        Assert.Equal(22, releasedSecondary);
        Assert.Equal(0, world.GenericSimsCall03State.PrimaryHandle);
        Assert.Equal(0, world.GenericSimsCall03State.SecondaryHandle);
        Assert.Equal((short)0, world.GenericSimsCall03State.LinkedGlobalWord60);
    }

    [Fact]
    public void HostlessGenericSimsCall03_DismissesVisibleDialogAndClearsLinkedWord60()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, PersonDef()) { TypeRecord = new TypeRecord(0x1111) });

        var dlg1 = world.CreateMessageDialog(me, null, "Title", "Body", "OK", null, null, blocking: true, awaitsResponse: true);
        Assert.NotNull(dlg1);
        Assert.True(world.HasVisibleDialog);
        Assert.NotEqual(0, world.GenericSimsCall03State.PrimaryHandle);
        Assert.Equal(0xf, world.GenericSimsCall03State.LinkedGlobalWord60);

        var ctx = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, Operand(0x03)));
        Assert.False(world.HasVisibleDialog);
        Assert.Equal(0, world.GenericSimsCall03State.PrimaryHandle);
        Assert.Equal(0, world.GenericSimsCall03State.LinkedGlobalWord60);

        var dlg2 = world.CreateMessageDialog(me, null, "Title 2", "Body 2", "OK", null, null, blocking: true, awaitsResponse: false);
        Assert.NotNull(dlg2);
        Assert.True(world.HasVisibleDialog);
        Assert.NotEqual(0, world.GenericSimsCall03State.PrimaryHandle);
        Assert.Equal(0xf, world.GenericSimsCall03State.LinkedGlobalWord60);

        world.RespondMessageDialog(dlg2!.Token, 0);
        Assert.False(world.HasVisibleDialog);
        Assert.Equal(0, world.GenericSimsCall03State.PrimaryHandle);
        Assert.Equal(0, world.GenericSimsCall03State.LinkedGlobalWord60);

        // Manually-assigned test PrimaryHandle values are never clobbered by dialog lifecycle sync.
        world.GenericSimsCall03State.PrimaryHandle = 99;
        world.GenericSimsCall03State.LinkedGlobalWord60 = 0x1234;
        var dlg3 = world.CreateMessageDialog(me, null, "Title 3", "Body 3", "OK", null, null, blocking: true, awaitsResponse: false);
        Assert.Equal(99, world.GenericSimsCall03State.PrimaryHandle);
        Assert.Equal(0x1234, world.GenericSimsCall03State.LinkedGlobalWord60);
        world.RespondMessageDialog(dlg3!.Token, 0);
        Assert.Equal(99, world.GenericSimsCall03State.PrimaryHandle);
        Assert.Equal(0x1234, world.GenericSimsCall03State.LinkedGlobalWord60);
    }

    [Fact]
    public void GenericSimsCall03_DialogPrimitiveAwaitingResponse_CompletesOnNextTick()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, PersonDef()) { TypeRecord = new TypeRecord(0x1111) });
        var dialogFrame = new StackFrame(0, [], [0, 0]);
        var dialogCtx = new TreeContext(world, me, dialogFrame);
        var dialogOperand = new byte[8];

        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(dialogCtx, dialogOperand));
        Assert.True(world.HasVisibleDialog);
        Assert.NotEqual(0, world.GenericSimsCall03State.PrimaryHandle);
        var record = Assert.Single(world.DialogRecords);
        Assert.True(record.AwaitsResponse);

        var callCtx = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(callCtx, Operand(0x03)));
        Assert.False(world.HasVisibleDialog);
        Assert.False(record.Visible);
        Assert.Equal(2, record.State);

        Assert.Equal(PrimitiveResult.True, DialogPrimitive.Execute(dialogCtx, dialogOperand));
        Assert.Empty(world.DialogRecords);
        Assert.False(world.HasVisibleDialog);
    }

    [Fact]
    public void GenericSimsCall03_QueuedDialogBehindNonAwaitedDialog_PromotesNextAndReArms03Handle()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, PersonDef()) { TypeRecord = new TypeRecord(0x1111) });

        var qna1 = world.CreateMessageDialog(me, null, "NonAwaited", "Body 1", "OK", null, null, blocking: true, awaitsResponse: false);
        var qna2 = world.CreateMessageDialog(me, null, "Awaited", "Body 2", "OK", null, null, blocking: true, awaitsResponse: true);
        Assert.NotNull(qna1);
        Assert.NotNull(qna2);
        Assert.True(qna1!.Visible);
        Assert.False(qna2!.Visible);
        Assert.Equal(2, world.DialogRecords.Count);
        var firstHandle = world.GenericSimsCall03State.PrimaryHandle;
        Assert.NotEqual(0, firstHandle);
        Assert.Equal((short)0xf, world.GenericSimsCall03State.LinkedGlobalWord60);

        var callCtx = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(callCtx, Operand(0x03)));
        Assert.Same(qna2, Assert.Single(world.DialogRecords));
        Assert.True(qna2.Visible);
        Assert.Equal(0, qna2.State);
        Assert.True(world.HasVisibleDialog);
        Assert.NotEqual(0, world.GenericSimsCall03State.PrimaryHandle);
        Assert.NotEqual(firstHandle, world.GenericSimsCall03State.PrimaryHandle);
        Assert.Equal((short)0xf, world.GenericSimsCall03State.LinkedGlobalWord60);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(callCtx, Operand(0x03)));
        Assert.False(qna2.Visible);
        Assert.Equal(2, qna2.State);
        Assert.False(world.HasVisibleDialog);
        Assert.Equal(0, world.GenericSimsCall03State.PrimaryHandle);
        Assert.Equal((short)0, world.GenericSimsCall03State.LinkedGlobalWord60);

        // Clean up qna2 via CompleteDialogRecord, then verify DialogPrimitive awaiting response with a queued dialog behind it.
        world.CompleteDialogRecord(qna2);
        Assert.Empty(world.DialogRecords);

        var sim2 = world.Add(new XPerson(2, PersonDef()) { TypeRecord = new TypeRecord(0x1112) });
        var dialogCtx = new TreeContext(world, sim2, new StackFrame(0, [], [0, 0]));
        var dialogOperand = new byte[8];
        Assert.Equal(PrimitiveResult.Stop, DialogPrimitive.Execute(dialogCtx, dialogOperand));
        var q1 = Assert.Single(world.DialogRecords);
        Assert.True(q1.AwaitsResponse);
        Assert.True(q1.Visible);
        var q2 = world.CreateMessageDialog(me, null, "Queued", "Body Q", "OK", null, null, blocking: true, awaitsResponse: false);
        Assert.NotNull(q2);
        Assert.False(q2!.Visible);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(callCtx, Operand(0x03)));
        Assert.False(q1.Visible);
        Assert.Equal(2, q1.State);
        Assert.True(q2.Visible);
        Assert.True(world.HasVisibleDialog);
        Assert.NotEqual(0, world.GenericSimsCall03State.PrimaryHandle);
        Assert.Equal((short)0xf, world.GenericSimsCall03State.LinkedGlobalWord60);

        Assert.Equal(PrimitiveResult.True, DialogPrimitive.Execute(dialogCtx, dialogOperand));
        Assert.Same(q2, Assert.Single(world.DialogRecords));
        Assert.True(q2.Visible);
        Assert.NotEqual(0, world.GenericSimsCall03State.PrimaryHandle);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(callCtx, Operand(0x03)));
        Assert.Empty(world.DialogRecords);
        Assert.False(world.HasVisibleDialog);
        Assert.Equal(0, world.GenericSimsCall03State.PrimaryHandle);
        Assert.Equal((short)0, world.GenericSimsCall03State.LinkedGlobalWord60);
    }

    [Fact]
    public void HostlessGenericSimsCall05_TransfersOffLotAndOnLotFamilyNetWorth()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, PersonDef()) { TypeRecord = new TypeRecord(0x1111) });
        var budget = new TestBudget(1000);
        world.FamilyBudget = budget;
        world.ActiveLotNumber = 5;
        world.ArchitectureValueMultiplier = 0.7f;
        var simiWriter = PersistWriter.Start(false, 0x25, "IMIS");
        for (var i = 0; i < 0x20; i++) simiWriter.Int16(0);
        simiWriter.Int32(0); simiWriter.Int16(0); simiWriter.Int32(0);
        simiWriter.Int32(0); simiWriter.Int32(0); simiWriter.Int32(0);
        simiWriter.Int32(500); simiWriter.Int32(0); simiWriter.Int32(0);
        var simiFile = new IffFile(ChunkBuilder.File(("SIMI", 1, simiWriter.ToArray())), "simi.iff");
        world.SavedSimulationState = SimulationState.Read(simiFile, simiFile.OfType(ResourceType.Parse("SIMI")).Single());
        world.LiveLotValuationProvider = () => (250, 101); // trunc(101 * 0.7f) = 70
        world.TryLookupFamilyValuation = (short familyNum, out int houseNum, out int famBudget, out int famValue) =>
        {
            if (familyNum == 2) { houseNum = 9; famBudget = 300; famValue = 400; return true; }
            if (familyNum == 5) { houseNum = 5; famBudget = 1000; famValue = 999; return true; }
            houseNum = 0; famBudget = 0; famValue = 0; return false;
        };

        var ctx = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));
        me.Temporaries[0] = 2; // Off-lot family: 300 + 400 = 700
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, Operand(0x05)));
        Assert.Equal(1700, budget.Funds);

        me.Temporaries[0] = 5; // On-lot family: 1700 (live budget) + 500 (lot) + 250 (furnishings) + 70 (trunc(101*0.7)) = 2520
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, Operand(0x05)));
        Assert.Equal(1700 + 2520, budget.Funds);

        me.Temporaries[0] = 99;
        Assert.Equal(PrimitiveResult.False, GenericSimsCallPrimitive.Execute(ctx, Operand(0x05)));
    }

    [Fact]
    public void HostlessGenericSimsCall1B_CountsFreeVacationHousesFromProvider()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, PersonDef()) { TypeRecord = new TypeRecord(0x1111) });
        world.OccupiedVacationHousesProvider = () => [40, 42, 42, 48, 99];

        var ctx = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, Operand(0x1b)));
        Assert.Equal((short)6, me.Temporaries[0]);
    }

    [Fact]
    public void HostlessGenericSimsCall2B_RaisesMagicMasterAttributesFromActiveFamilyUpkeep()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, PersonDef()) { TypeRecord = new TypeRecord(0x1111) });
        world.ActiveLotNumber = 5;
        world.StateClasses.Add(new StateClassEntry(5, 1));
        world.LotUpkeepManagerPresent = true;
        world.GlobalPerson.LotUpkeepMatchWord22 = 7;
        world.TryLookupActiveFamilyUpkeep = (out short familyNumber, out bool hasMembers, out IReadOnlyList<short> minResources) =>
        {
            familyNumber = 7;
            hasMembers = true;
            minResources = [10, 20, 30, 40, 50, 60];
            return true;
        };

        var magicMaster = world.Add(new XObject(2, new short[XObject.DefinitionCount], attributes: 6)
        {
            TypeRecord = new TypeRecord(unchecked((int)0xB6C90029)),
            Attributes = [5, 25, 10, 40, 0, 99],
        });

        var ctx = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, Operand(0x2b)));
        Assert.Equal([10, 25, 30, 40, 50, 99], magicMaster.Attributes);
    }

    [Fact]
    public void EvaluatePersonRecordSelection_DoesNotThrowOnAwayLotWithNullStoredSlotAndAliasesFamilyNumber()
    {
        var world = new SimulationWorld { PersonRecordManagerPresent = true, NeighborInteractionGlobalStateValue = 21 };
        world.StateClasses.Add(new StateClassEntry(21, 2));
        world.PersonRecordKeys.Add(0x1234);
        var person = world.Add(new XPerson(1, PersonDef()) { TypeRecord = new TypeRecord(0x1234) });
        person[PersonData.FamilyNumber] = 42;
        person.SelectionWord5CC = 1;

        Assert.Equal((short)42, person.SelectionWord606);
        var selection = world.EvaluatePersonRecordSelection(0);
        Assert.True(selection.HasValue);
        Assert.Same(person, selection.Value.Person);
    }

    [Fact]
    public void SpecialEffectPrimitive_SetsGlobalElevenInHeadlessMode()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, PersonDef()) { TypeRecord = new TypeRecord(0x1111) });
        var stackObj = world.Add(new XObject(2, new short[XObject.DefinitionCount]));
        var ctx = new TreeContext(world, me, new StackFrame(0, [], [0, 0]) { StackObject = 2 });

        Assert.Equal(PrimitiveResult.Stop, SpecialEffectPrimitive.Execute(ctx, [2, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Equal((short)1, world.Globals[11]);
        Assert.Equal((short)1, world.GlobalObjectTableEffectWords[11]);
    }

    [Fact]
    public void InteractionScopeTable_RemoveEntriesWithKeyPreservesZeroOneThree()
    {
        var table = new InteractionScopeTable([
            new InteractionScopeEntry(0, 100, 1),
            new InteractionScopeEntry(1, 200, 1),
            new InteractionScopeEntry(2, 0, 50),
            new InteractionScopeEntry(3, 300, 1),
        ]);

        table.RemoveEntriesWithKey(2);

        Assert.Equal(3, table.Count);
        Assert.Equal(0, table.CountValue(2));
        Assert.Equal(1, table.CountValue(0));
        Assert.Equal(1, table.CountValue(1));
        Assert.Equal(1, table.CountValue(3));
    }

    [Fact]
    public void ManageInventoryAndGenericSimsCall11_13_14_ShareNeighborInventoryAndSyncMotives()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, PersonDef()) { TypeRecord = new TypeRecord(0x1001) });
        me[PersonData.NeighborId] = 5;
        me[PersonData.FamilyNumber] = 3;
        me.SetObjectTableEntry(1, me);
        world.PersonRecordKeys.AddRange([0x1001, 0x2002, 0x3003]);
        world.Neighbors[5] = new NeighborState(5, PersonDef()) { TypeRecordKey = 0x1001 };

        var dateNeighbor = new NeighborState(6, PersonDef()) { TypeRecordKey = 0x2002 };
        var groupNeighbor = new NeighborState(7, PersonDef()) { TypeRecordKey = 0x3003 };
        world.Neighbors[6] = dateNeighbor;
        world.Neighbors[7] = groupNeighbor;
        world.NeighborWalkOrder.AddRange([world.Neighbors[5], dateNeighbor, groupNeighbor]);

        var dateSim = world.Add(new XPerson(2, PersonDef()) { TypeRecord = new TypeRecord(0x2002) });
        var groupSim = world.Add(new XPerson(3, PersonDef()) { TypeRecord = new TypeRecord(0x3003) });

        var ctx = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));

        // ManageInventory mode 5 -> key 1 (date companion index 2 => 0x2002)
        me.Temporaries[0] = 2;
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(ctx, ManageInventoryOperand(5)));
        // ManageInventory mode 6 -> key 3 (group companion index 3 => 0x3003)
        me.Temporaries[0] = 3;
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(ctx, ManageInventoryOperand(6)));

        // Set motive and fame/star power, run 0x11 (should preserve key=1 and key=3 while writing key=2)
        me.Motives[9] = 65f; // mode 0
        me.PersonData[80] = 4; // mode 10 (0x62c)
        me.PersonData[81] = 8; // mode 12 (0x62e)
        me.PersonData[82] = 5; // mode 11 (0x630)
        me.Temporaries[0] = 21;
        me.Temporaries[1] = 1;
        me.Temporaries[2] = 30;
        me.Temporaries[3] = 2;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, Operand(0x11)));

        // Verify 0x13 pops key=1 companion and 0x14 pops key=3 companion
        me.Temporaries[0] = 1;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, Operand(0x13)));
        Assert.Equal(dateSim.ClockManagerIndex, me.Temporaries[0]);

        me.Temporaries[0] = 1;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, Operand(0x14)));
        Assert.Equal(groupSim.ClockManagerIndex, me.Temporaries[0]);

        // Verify 0x17 pops arrival clock and 0x15 restores motives and PersonData[80..82] onto a fresh person
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, Operand(0x17)));
        me.Motives[9] = 0f;
        me.PersonData[80] = 0;
        me.PersonData[81] = 0;
        me.PersonData[82] = 0;
        me.Temporaries[0] = 1;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, Operand(0x15)));
        Assert.Equal(65f, me.Motives[9]);
        Assert.Equal((short)4, me.PersonData[80]);
        Assert.Equal((short)8, me.PersonData[81]);
        Assert.Equal((short)5, me.PersonData[82]);
    }

    [Fact]
    public void PersonRecordVersion10_RoundTripsMakinMagicFieldsThroughNeighborsChunk()
    {
        var world = new SimulationWorld();
        var neighbor = new NeighborState(4, PersonDef()) { TypeRecordKey = 0x4444, Name = "Witch" };
        world.Neighbors[4] = neighbor;
        world.NeighborWalkOrder.Add(neighbor);
        var savedNeighbor = new Neighbor(0, 9, "Witch", 0, new short[256], 4, 0x4444, new Dictionary<int, IReadOnlyList<int>>(), 10, 0);
        var nbrsBytes = new Neighbors(0x49, 1, [savedNeighbor]).WriteChunkData(swap: false);
        var ngh = new IffFile(ChunkBuilder.File(("NBRS", 1, nbrsBytes)), "Neighborhood.iff");
        world.SavedNeighbors = Neighbors.ReadRecoveringSupportedPrefix(ngh, ngh.OfType(ResourceType.Parse("NBRS")).Single());

        var person = world.Add(new XPerson(1, PersonDef()) { TypeRecord = new TypeRecord(0x4444) });
        person[PersonData.NeighborId] = 4;
        person.PersonData[54] = 12;
        person.PersonData[(int)PersonData.MagicTransgressions] = 3;
        person.PersonData[(int)PersonData.IsLivingGhost] = 1;
        person.PersonData[(int)PersonData.ScalingSim] = 2;
        person.PersonData[(int)PersonData.IsToad] = 1;

        world.SavePersonRecord(person);
        Assert.Equal(10, neighbor.PersonRecordVersion);

        var updatedNgh = world.WriteSavedNeighborRelationshipsTo(ngh);
        var reparsedNeighbors = Neighbors.ReadRecoveringSupportedPrefix(updatedNgh, updatedNgh.OfType(ResourceType.Parse("NBRS")).Single());
        var reloadedWorld = SavedLotObjectLoader.Restore([], [], _ => null, _ => null, savedNeighbors: reparsedNeighbors);
        var rebound = reloadedWorld.Add(new XPerson(2, PersonDef()) { TypeRecord = new TypeRecord(0x4444) });
        reloadedWorld.BindPersonToNeighborRecord(rebound);

        Assert.Equal((short)12, rebound.PersonData[54]);
        Assert.Equal((short)3, rebound.PersonData[(int)PersonData.MagicTransgressions]);
        Assert.Equal((short)1, rebound.PersonData[(int)PersonData.IsLivingGhost]);
        Assert.Equal((short)2, rebound.PersonData[(int)PersonData.ScalingSim]);
        Assert.Equal((short)1, rebound.PersonData[(int)PersonData.IsToad]);
    }

    [Fact]
    public void NeighborhoodExportAndLotFilter_SupportAllModesAndMagiCoinTotals()
    {
        Assert.Equal(1, NeighborhoodExport.SlotForMode(2));
        Assert.Equal(2, NeighborhoodExport.SlotForMode(3));
        Assert.Equal(3, NeighborhoodExport.SlotForMode(4));
        Assert.Equal(4, NeighborhoodExport.SlotForMode(5));
        Assert.Equal(5, NeighborhoodExport.SlotForMode(7));
        Assert.Equal(2, NeighborhoodExport.DefaultFallbackForMode(2));
        Assert.Equal(0x400, NeighborhoodExport.DefaultFallbackForMode(5));
        Assert.Equal(0x400, NeighborhoodExport.DefaultFallbackForMode(7));

        var family = Family.CreateNew(1, 1, [0x1111u]);
        var neighbors = new Dictionary<uint, Neighbor>
        {
            [0x1111u] = new(0, 10, "Sim", 0, new short[256], 5, 0x1111u, new Dictionary<int, IReadOnlyList<int>>(), 10, 0),
        };
        var ngbh = new NeighborhoodData(0x49, new short[16], new Dictionary<short, IReadOnlyList<NeighborhoodData.InventoryItem>>
        {
            [5] =
            [
                new(7, NeighborhoodExport.MagiCoinTokenGuid, 30_000),
                new(5, 0x1234u, 5_000),
            ],
        });
        Assert.Equal(35_000, NeighborhoodExport.SumFamilyMagiCoins(family, neighbors, ngbh));

        var stFilter = new NeighborhoodLotFilter([0x80, 0x400], mode: 5, savedMask: 0x02);
        Assert.Equal(9, stFilter.ModeMasks.Count);
        Assert.Equal(0x80, stFilter.SelectedMask);

        var mtFilter = new NeighborhoodLotFilter([0x800, 0x1000], mode: 7, savedMask: 0x04);
        Assert.Equal(0x1000, mtFilter.SelectedMask);
    }

    [Fact]
    public void SavedLotSerialization_PreservesNonSquareFootprintBoundsAndUpdatedObjt()
    {
        var game = Directory.CreateTempSubdirectory("m5-save-roundtrip").FullName;
        try
        {
            var typeGuid = 0x12345678u;
            var objdFields = new ushort[XObject.DefinitionCount];
            objdFields[(int)ObjdField.Guid1] = unchecked((ushort)typeGuid);
            objdFields[(int)ObjdField.Guid2] = (ushort)(typeGuid >> 16);
            objdFields[(int)ObjdField.Type] = 4;
            objdFields[(int)ObjdField.NumAttributes] = 2;
            var objdChunk = new ChunkBuilder();
            foreach (var f in objdFields) objdChunk.U16(f);
            var bhavChunk = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(1)
                .U16(0).U8(254).U8(255).Raw(new byte[8]).ToArray();

            var objectsDir = Directory.CreateDirectory(Path.Combine(game, "Objects")).FullName;
            File.WriteAllBytes(Path.Combine(objectsDir, "testobj.iff"), ChunkBuilder.File(
                ("OBJD", 1, objdChunk.ToArray()),
                ("BHAV", 4096, bhavChunk)));

            var registry = ObjectRegistry.Scan(game);
            var library = GlobalLibrary.Open(game);

            var objmEmpty = PersistWriter.Start(false, 0x45, "MjbO");
            objmEmpty.WriteCodingFlag(false);
            objmEmpty.Int16(0); objmEmpty.BeginBlock(); objmEmpty.Int16(0); objmEmpty.Int32(0); objmEmpty.String("");
            var objtEmpty = PersistWriter.Start(false, 3, "tjbo"); objtEmpty.Int32(0);
            var emptyLot = new IffFile(ChunkBuilder.File(("ObjM", 1, objmEmpty.ToArray()), ("objt", 0, objtEmpty.ToArray())), "lot.iff");

            var world = SavedLotObjectLoader.Load(emptyLot, registry, library);
            var created = world.ObjectFactory!(unchecked((int)typeGuid))!;
            world.Add(created);
            created.Position = new ObjectPosition(168, 328, 1);
            created.FootprintBounds = new FootprintBounds(MaxX: 20, MaxY: 40, MinX: 10, MinY: 30);

            var (savedLot, _) = world.WriteSavedLotObjectsTo(emptyLot);

            // Corrupt objt TreeValue0 on disk to simulate code update, then reload -> ResetObjectsWithChangedCode -> save -> reload
            var objtChunk = savedLot.OfType(ResourceType.Parse("objt")).Single();
            var objt = ObjectTypeTable.Read(savedLot, objtChunk);
            var staleEntries = objt.Entries.Select(e => e with { TreeValue0 = 0xDEADu, TreeValue1 = 0xBEEFu }).ToArray();
            var staleLot = savedLot.WithChunkDataResized(objtChunk.Type, objtChunk.Id, objt.Write(false, "tjbo", staleEntries));

            var reloaded = SavedLotObjectLoader.Load(staleLot, registry, library);
            Assert.Contains(typeGuid, reloaded.ChangedCodeTypeGuids);
            var reloadedObj = Assert.Single(reloaded.WalkOrder);
            Assert.Equal(new FootprintBounds(MaxX: 20, MaxY: 40, MinX: 10, MinY: 30), reloadedObj.FootprintBounds);

            reloaded.ResetObjectsWithChangedCode();
            reloadedObj.FootprintBounds = new FootprintBounds(MaxX: 25, MaxY: 45, MinX: 15, MinY: 35);
            var (resavedLot, _) = reloaded.WriteSavedLotObjectsTo(staleLot);
            var finalWorld = SavedLotObjectLoader.Load(resavedLot, registry, library);
            Assert.Empty(finalWorld.ChangedCodeTypeGuids);
            var finalObj = Assert.Single(finalWorld.WalkOrder);
            Assert.Equal(new FootprintBounds(MaxX: 25, MaxY: 45, MinX: 15, MinY: 35), finalObj.FootprintBounds);
        }
        finally
        {
            if (Directory.Exists(game)) Directory.Delete(game, recursive: true);
        }
    }

    [Fact]
    public void PoolTool_IgnoresHiddenAndCursorObjectsWhenRebuildingDeck()
    {
        byte[] BuildArry(int elementSize, byte[] rawData) => new ChunkBuilder()
            .S16(0).S16(0).S32(64).S32(64).S32(elementSize).S32(0).Raw(rawData).ToArray();
        var lot = new IffFile(ChunkBuilder.File(
            ("Arry", 0, BuildArry(4, new byte[64 * 64 * 4])),
            ("Arry", 2, BuildArry(8, new byte[64 * 64 * 8])),
            ("Arry", 8, BuildArry(1, Enumerable.Repeat((byte)1, 64 * 64).ToArray())),
            ("Arry", 11, BuildArry(2, new byte[64 * 64 * 2]))
        ), "lot.iff");
        var floorStrings = new ChunkBuilder().S16(-3).S16(3)
            .U8(1).CString("10").CString("")
            .U8(1).CString("Tile").CString("")
            .U8(1).CString("Floor").CString("");
        var waterStrings = new ChunkBuilder().S16(-3).S16(3)
            .U8(1).CString("50").CString("")
            .U8(1).CString("Pool").CString("")
            .U8(1).CString("Desc").CString("");
        var build = new IffFile(ChunkBuilder.File(
            ("STR#", 0x82, floorStrings.ToArray()),
            ("STR#", 0x85, waterStrings.ToArray())), "Build.iff");
        var catalog = FloorCatalog.Read(build);

        var world = new SimulationWorld();
        var hiddenDef = new short[XObject.DefinitionCount];
        hiddenDef[(int)ObjdField.Type] = 4;
        var hiddenObj = world.Add(new XObject(10, hiddenDef) { Position = new ObjectPosition(11 * 16 + 8, 10 * 16 + 8, 1) });
        hiddenObj[ObjectData.Hidden] = 1;
        world.SetObjectPosition(hiddenObj, hiddenObj.Position);

        var cursorDef = new short[XObject.DefinitionCount];
        cursorDef[(int)ObjdField.Type] = 9;
        var cursorObj = world.Add(new XObject(11, cursorDef) { Position = new ObjectPosition(9 * 16 + 8, 10 * 16 + 8, 1) });
        world.SetObjectPosition(cursorObj, cursorObj.Position);

        var pools = LotPool.Read(lot);
        var tool = new PoolTool(world, LotFloors.Read(lot, 1), pools, LotTiles.Read(lot),
            LotTerrain.Read(lot), LotWalls.Read(lot, 1), catalog, new TestBudget(5000));

        var result = tool.Rectangle(10, 10, 10, 10, erase: false);
        Assert.True(result.Applied);
        Assert.True(pools.IsDeck(11, 10, 1));
        Assert.True(pools.IsDeck(9, 10, 1));
    }

    [Fact]
    public void ObjectCatalog_FiltersExpansionSubsortFieldsWhilePreservingResidentialDefault()
    {
        ushort[] MakeFields(ushort roomSort, ushort funcFlags, ushort dtSubsort, ushort vacSubsort, ushort commSubsort)
        {
            var f = new ushort[105];
            f[(int)ObjdField.Type] = 4;
            f[(int)ObjdField.NumGraphics] = 1;
            f[(int)ObjdField.Price] = 100;
            f[(int)ObjdField.RoomSort] = roomSort;
            f[(int)ObjdField.FunctionSort] = funcFlags;
            f[95] = dtSubsort;
            f[97] = vacSubsort;
            f[99] = commSubsort;
            return f;
        }
        byte[] ToBytes(ushort[] fields)
        {
            var bytes = new byte[fields.Length * 2];
            for (var i = 0; i < fields.Length; i++)
            {
                bytes[i * 2] = (byte)(fields[i] & 0xff);
                bytes[i * 2 + 1] = (byte)(fields[i] >> 8);
            }
            return bytes;
        }

        var dtFields = MakeFields(0, 0, 0x04, 0, 0);
        dtFields[(int)ObjdField.Guid1] = 0x1111;
        var resFields = MakeFields(0x08, 0x01, 0, 0, 0);
        resFields[(int)ObjdField.Guid1] = 0x2222;
        var vacFields = MakeFields(0, 0, 0, 0x02, 0);
        vacFields[(int)ObjdField.Guid1] = 0x3333;

        var game = Directory.CreateTempSubdirectory("m5-cat-game").FullName;
        var session = Path.Combine(Path.GetTempPath(), "m5-cat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var objectsDir = Directory.CreateDirectory(Path.Combine(game, "Objects")).FullName;
            File.WriteAllBytes(Path.Combine(objectsDir, "catalog.iff"), ChunkBuilder.File(
                ("OBJD", 1, ToBytes(dtFields)),
                ("OBJD", 2, ToBytes(resFields)),
                ("OBJD", 3, ToBytes(vacFields))));
            var registry = ObjectRegistry.Scan(game);

            var dtEntryObj = registry.Find(0x1111u)!;
            var resEntryObj = registry.Find(0x2222u)!;
            var vacEntryObj = registry.Find(0x3333u)!;

            Assert.False(ObjectCatalog.TryGetEntry(dtEntryObj, 1, out _));
            Assert.True(ObjectCatalog.TryGetEntry(dtEntryObj, 1, CatalogLotType.Downtown, out var dtEntry));
            Assert.Equal(0x04, dtEntry!.DTSubsort);

            Assert.True(ObjectCatalog.TryGetEntry(resEntryObj, 1, out _));
            Assert.False(ObjectCatalog.TryGetEntry(resEntryObj, 1, CatalogLotType.Downtown, out _));
            Assert.True(ObjectCatalog.TryGetEntry(vacEntryObj, 1, CatalogLotType.Vacation, out var vacEntry));
            Assert.Equal(0x02, vacEntry!.VacationSubsort);

            var dtPage = JsonSerializer.SerializeToElement(
                ObjectCatalog.Page(registry, 1, category: -1, offset: 0, count: 10, session, lotType: CatalogLotType.Downtown));
            Assert.Equal(1, dtPage.GetProperty("total").GetInt32());
        }
        finally
        {
            if (Directory.Exists(game)) Directory.Delete(game, recursive: true);
            if (Directory.Exists(session)) Directory.Delete(session, recursive: true);
        }
    }

    [Fact]
    public void LiveServer_FootstepPriority_EmergencyLock_WallSellOpeningUndoRedo_AndProductionWiring()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        var world = server.World!;

        // 1. Verify production wiring fields populated natively without ISimulationHost
        Assert.Null(world.Host);
        Assert.NotNull(world.StoredSlotPersonId);
        Assert.True(world.LotUpkeepManagerPresent);
        Assert.NotNull(world.ArchitectureValueMultiplier);
        Assert.NotNull(world.PictureInPicture);

        // 2. Verify emergency lot list (0x0a / 0x0b) blocks and unblocks Buy/Build commands
        Assert.True(server.HandleJson("speed", new { value = 0 }).GetProperty("ok").GetBoolean());
        for (int quiet = 0, pass = 0; quiet < 4 && pass < 32; pass++)
        {
            world.WakeSimulateOnPauseObjects();
            world.AdvanceSimulationTick(new SimulationClockControl(0, 0, 0), 0, world.Clock.Ticks);
            if (world.DialogRecords.Any(d => d.Visible && d.State < 2))
            {
                quiet = 0;
                while (world.DialogRecords.FirstOrDefault(d => d.Visible && d.State < 2) is { } dlg)
                    world.RespondMessageDialog(dlg.Token, dlg.Kind is 1 or 2 or 14 ? (byte)1 : (byte)0);
            }
            else if (world.DialogRecords.Count == 0)
            {
                quiet++;
            }
            else
            {
                quiet = 0;
            }
        }
        var npc = world.Add(new XObject(9450, new short[XObject.DefinitionCount]));
        world.MaintainLotList(npc);
        var blockedResp = server.HandleJson("architecture", new { command = "WALL_LINE", level = 1, x = 10, y = 10, x2 = 12, y2 = 10, style = 1, pattern = 0 });
        Assert.False(blockedResp.GetProperty("ok").GetBoolean());
        Assert.Contains("emergency", blockedResp.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        world.RemoveFromLotList(npc);
        world.Remove(npc.Id);

        // 3. Verify WallTool SellOpening + Undo/Redo restores/re-demolishes door XObject cleanly
        var houseIff = IffFile.Open(Path.Combine(profile.UserData, "Houses", "House05.iff"));
        var walls = LotWalls.Read(houseIff, 1);
        var tiles = LotTiles.Read(houseIff);
        var terrain = LotTerrain.Read(houseIff);
        var pool = LotPool.Read(houseIff);
        var water = LotWater.Read(houseIff);
        (int X, int Y)? wallStart = null;
        for (var y = 10; y < 50 && wallStart is null; y++)
        for (var x = 10; x < 50; x++)
        {
            if (tiles.InsideLot(x, y) && tiles.Buildable(x, y, 1) &&
                tiles.InsideLot(x + 1, y) && tiles.Buildable(x + 1, y, 1) &&
                tiles.InsideLot(x, y - 1) && tiles.Buildable(x, y - 1, 1) &&
                !pool.IsPool(x, y, 1) && !water.IsWater(x, y, 1) &&
                walls[x, y][0] == 0 && walls[x + 1, y][0] == 0 && walls[x, y - 1][0] == 0 &&
                terrain.IsFlatAt(x, y, terrain.ReferenceHeight(x, y)) &&
                terrain.IsFlatAt(x + 1, y, terrain.ReferenceHeight(x, y)) &&
                terrain.IsFlatAt(x, y - 1, terrain.ReferenceHeight(x, y)) &&
                !world.ObjectsAtTile(x, y, 1).Any() && !world.ObjectsAtTile(x, y - 1, 1).Any())
            {
                wallStart = (x, y);
                break;
            }
        }
        Assert.NotNull(wallStart);
        var (wx, wy) = wallStart!.Value;

        var wallBuild = server.HandleJson("architecture", new { command = "WALL_LINE", level = 1, x = wx, y = wy, x2 = wx + 2, y2 = wy, style = 1, pattern = 0 });
        Assert.True(wallBuild.GetProperty("ok").GetBoolean(), wallBuild.ToString());
        Assert.True(wallBuild.GetProperty("result").GetProperty("changed").GetInt32() > 0, wallBuild.ToString());

        var doorGuid = ObjectRegistry.Scan(gameDir).Entries
            .Where(e => ObjectCatalog.TryGetEntry(e, 1, out var cat) &&
                        cat!.Categories == (1 << 8) &&
                        e.Definition[ObjdField.Price] <= 500)
            .OrderBy(e => e.Definition[ObjdField.Price])
            .Select(e => e.Guid)
            .FirstOrDefault();
        Assert.NotEqual(0u, doorGuid);
        JsonElement buyDoor = default;
        var placedDoor = false;
        foreach (var dir in new[] { 1, 4, 16, 64 })
        {
            buyDoor = server.HandleJson("buyObject", new { guid = doorGuid, x = wx * 16 + 8, y = wy * 16 + 8, level = 1, direction = dir });
            if (buyDoor.GetProperty("ok").GetBoolean())
            {
                placedDoor = true;
                break;
            }
        }
        Assert.True(placedDoor, buyDoor.ToString());
        var doorId = (short)buyDoor.GetProperty("result").GetProperty("objectID").GetInt32();
        var doorLead = world.ObjectById(doorId)!;
        var doorPartGuid = unchecked((uint)(doorLead.TypeRecord?.Key ?? 0));
        Assert.NotNull(doorLead);

        var eraseWall = server.HandleJson("architecture", new { command = "WALL_ERASE", level = 1, x = wx, y = wy, x2 = wx + 2, y2 = wy, style = 1, pattern = 0 });
        Assert.True(eraseWall.GetProperty("ok").GetBoolean(), eraseWall.ToString());
        Assert.Null(world.ObjectById(doorId));

        var undoResp = server.HandleJson("undo", new { });
        Assert.True(undoResp.GetProperty("ok").GetBoolean(), undoResp.ToString());
        Assert.Contains(world.WalkOrder, o => o.Definition[(int)ObjdField.Type] == 8 && unchecked((uint)(o.TypeRecord?.Key ?? 0)) == doorPartGuid && (o.Position.X >> 4) == wx && (o.Position.Y >> 4) == wy);

        var redoResp = server.HandleJson("redo", new { });
        Assert.True(redoResp.GetProperty("ok").GetBoolean(), redoResp.ToString());
        Assert.DoesNotContain(world.WalkOrder, o => o.Definition[(int)ObjdField.Type] == 8 && unchecked((uint)(o.TypeRecord?.Key ?? 0)) == doorPartGuid && (o.Position.X >> 4) == wx && (o.Position.Y >> 4) == wy);

        // 4. Verify multi-object surface priority (puddle > roach > ash > trash > plant)
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        var emitted = new List<string>();
        world.PlaySoundEvent = (cue, owner, pan, zoom) => { emitted.Add(cue); return true; };
        var walkClip = new AvatarAnimationClip("m5-step", 10, [], [new AvatarAnimationSoundEvent(0, "", "footstep", "1", false, false)], 333f);
        var tile = new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1);

        XObject SpawnSurface(short id, uint guid)
        {
            var o = world.Add(new XObject(id, new short[XObject.DefinitionCount]) { TypeRecord = new TypeRecord(unchecked((int)guid)) });
            world.SetObjectPosition(o, tile);
            return o;
        }
        string Step()
        {
            emitted.Clear();
            bella[PersonData.CurrentOutfit] = 0;
            bella[PersonData.IsSwimming] = 0;
            world.SetObjectPosition(bella, tile);
            bella.AnimationPlayback = new AvatarAnimationPlayback(walkClip, false, false, bella.AnimatorMilliseconds);
            world.TickAvatarAnimations(bella);
            return Assert.Single(emitted);
        }

        var plant = SpawnSurface(9460, 0x96E3398Du);
        var trash = SpawnSurface(9461, 0x7F907075u);
        var ash = SpawnSurface(9462, 0x63416BA1u);
        var roach = SpawnSurface(9463, 99999999u);
        var puddle = SpawnSurface(9464, 0x3E7470F6u);

        Assert.Equal("footstep_puddle", Step());
        world.Remove(puddle.Id);
        Assert.Equal("footstep_roach", Step());
        world.Remove(roach.Id);
        Assert.Equal("footstep_ash", Step());
        world.Remove(ash.Id);
        Assert.Equal("footstep_trash", Step());
        world.Remove(trash.Id);
        Assert.Equal("footstep_plant", Step());
        world.Remove(plant.Id);

        // 5. Run simulation ticks to confirm zero unhandled crashes
        server.BatchRun(5);
        var tickEvents = server.DrainEvents();
        Assert.DoesNotContain(tickEvents, e => e.TryGetProperty("type", out var t) && t.GetString() == "error");
    }

    [Fact]
    public void LoadNeighborhoodData_PreservesInteractionScopeTableBacking_AndPersists0x13_0x14_ConsumptionOnDisk()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        var world = server.World!;
        var sims = world.WalkOrder.OfType<XPerson>().Where(p => p[PersonData.NeighborId] > 0).ToArray();
        Assert.True(sims.Length >= 2);
        var simA = sims[0];
        var simB = sims[1];
        var comp1 = world.Neighbors[simB[PersonData.NeighborId]];
        var comp2 = world.Neighbors.Values.First(n =>
            n.Id != simA[PersonData.NeighborId] &&
            n.Id != comp1.Id &&
            n.TypeRecordKey != 0 &&
            world.PersonRecordKeys.Contains(n.TypeRecordKey));

        // Cache InteractionScopeTable on simA before ManageInventory triggers PersistNeighborhoodState -> LoadNeighborhoodData.
        var cachedScope = world.InteractionExecutorScope(simA);
        Assert.NotNull(cachedScope);

        // ManageInventory mode 5 (Add key1=1) then mode 6 (Add key1=3): each triggers LoadNeighborhoodData.
        var inv1Frame = new StackFrame(simA.Id, new short[8], [0, 0]);
        var inv1Ctx = new TreeContext(world, simA, inv1Frame);
        simA.Temporaries[0] = comp1.Id;
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(inv1Ctx, ManageInventoryOperand(5)));
        simA.Temporaries[0] = comp2.Id;
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(inv1Ctx, ManageInventoryOperand(6)));

        // Call 0x14 and 0x13 on simA; must resolve via the live refreshed inventory rather than returning -1.
        var call14Frame = new StackFrame(simA.Id, new short[8], [0, 0]);
        simA.Temporaries[0] = simA.Id;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(new TreeContext(world, simA, call14Frame), Operand(0x14)));
        Assert.True(simA.Temporaries[0] > 0);

        var call13Frame = new StackFrame(simA.Id, new short[8], [0, 0]);
        simA.Temporaries[0] = simA.Id;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(new TreeContext(world, simA, call13Frame), Operand(0x13)));
        Assert.Equal(simB.ClockManagerIndex, simA.Temporaries[0]);

        // Reload Neighborhood.iff NGBH from disk and verify both keys 1 and 3 are removed.
        var diskNgh = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
        var diskNgbh = NeighborhoodData.Read(diskNgh, diskNgh.Find(ResourceType.Parse("NGBH"), 1)!);
        var entries = diskNgbh.Inventories.GetValueOrDefault(simA[PersonData.NeighborId]) ?? [];
        Assert.DoesNotContain(entries, e => e.Type == 1 && e.GUID == unchecked((uint)comp1.TypeRecordKey));
        Assert.DoesNotContain(entries, e => e.Type == 3 && e.GUID == unchecked((uint)comp2.TypeRecordKey));
    }

    [Fact]
    public void TryLookupFamilyValuation_MatchesFamilyNumberExclusively_OnRealUserData()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        var world = server.World!;

        // In real UserData/Neighborhood.iff, chunk 5 has FamilyNumber=6 (House 5) and chunk 1 has FamilyNumber=5 (House 7).
        Assert.True(world.TryLookupFamilyValuation!(5, out var house5, out var budget5, out var value5));
        Assert.Equal(7, house5);
        Assert.True(budget5 >= 0 && value5 >= 0);

        Assert.True(world.TryLookupFamilyValuation!(6, out var house6, out _, out _));
        Assert.Equal(5, house6);
    }

    [Fact]
    public void ExecuteCallFifteenAndTwenty_AdvanceCursorPastArrivalTimeEntriesWithoutSkippingMotives()
    {
        var world = new SimulationWorld();
        var sim = world.Add(new XPerson(1, PersonDef()) { TypeRecord = new TypeRecord(0x4415) });
        var scope = world.InteractionExecutorScope(sim);
        // Place arrival time entries (2, 7) and (2, 8) BEFORE motive/career entries (2, 0) and (2, 10).
        scope.AddOrIncrement(2, 7, 14);
        scope.AddOrIncrement(2, 8, 30);
        scope.AddOrIncrement(2, 0, 77);
        scope.AddOrIncrement(2, 10, 6);

        var me = world.Add(new XObject(2, new short[XObject.DefinitionCount]));
        me.Temporaries[0] = sim.Id;
        var ctx15 = new TreeContext(world, me, new StackFrame(me.Id, new short[8], [0, 0]));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx15, Operand(0x15)));
        Assert.Equal(77f, sim.Motives[9]);
        Assert.Equal((short)6, sim.PersonData[80]);
        Assert.Equal(2, scope.CountValue(2)); // (2,7) and (2,8) remain intact

        // Also verify 0x20 fallback person motive/career transfer with preceding (2,7)/(2,8).
        var world2 = new SimulationWorld();
        var sim2 = new XPerson(3, PersonDef()) { TypeRecord = new TypeRecord(0x4420) };
        var neighbor10 = new NeighborState(10, PersonDef()) { TypeRecordKey = 0x4420 };
        world2.Neighbors[10] = neighbor10;
        world2.NeighborWalkOrder.Add(neighbor10);
        world2.ActivePersonManagerPresent = true;
        world2.ActivePersonRecordKeys.Add(0x4420);
        world2.PersonRecordKeys.Add(0x4420);
        world2.ObjectFactory = key => key == 0x4420 ? sim2 : null;
        var inv2 = world2.NeighborInventory(10);
        inv2.Add(2, 7, 9);
        inv2.Add(2, 8, 45);
        inv2.Add(2, 0, 55);
        inv2.Add(2, 10, 4);

        var caller = world2.Add(new XObject(4, new short[XObject.DefinitionCount]) { Position = new ObjectPosition(16 * 16, 16 * 16, 1) });
        var ctx20 = new TreeContext(world2, caller, new StackFrame(caller.Id, new short[8], [0, 0]));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx20, Operand(0x20)));
        Assert.Equal(55f, sim2.Motives[9]);
        Assert.Equal((short)4, sim2.PersonData[80]);
    }

    [Fact]
    public void ObjectCatalog_ExpansionLotTypesUseLotSubsortForCategoriesAndFunctionSubsortForSubsort_OnRealAssets()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;

        var registry = ObjectRegistry.Scan(gameDir);
        var session = Path.Combine(Path.GetTempPath(), "m5-realcat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dtSub4 = JsonSerializer.SerializeToElement(
                ObjectCatalog.Page(registry, 1, category: -1, offset: 0, count: 10, session, subsort: 4, lotType: CatalogLotType.Downtown));
            Assert.True(dtSub4.GetProperty("total").GetInt32() > 0);

            var mtSub4 = JsonSerializer.SerializeToElement(
                ObjectCatalog.Page(registry, 1, category: -1, offset: 0, count: 10, session, subsort: 4, lotType: CatalogLotType.MagicTown));
            Assert.True(mtSub4.GetProperty("total").GetInt32() > 0);

            var mtSub5 = JsonSerializer.SerializeToElement(
                ObjectCatalog.Page(registry, 1, category: -1, offset: 0, count: 10, session, subsort: 5, lotType: CatalogLotType.MagicTown));
            Assert.True(mtSub5.GetProperty("total").GetInt32() > 0);

            // Items with FunctionFlags == 0 and DTSubsort bit 0x01..0x08 must use DTSubsort categories, not Misc (1<<7).
            var dtOnlyEntry = registry.Entries.FirstOrDefault(e =>
                e.Definition[ObjdField.Disabled] == 0 &&
                e.Definition[ObjdField.NumGraphics] > 0 &&
                e.Definition[ObjdField.FunctionFlags] == 0 &&
                (e.Definition[(ObjdField)95] & 0x0f) != 0 &&
                ((e.Definition[ObjdField.MasterId] == 0 && e.Definition[ObjdField.SubIndex] == 0) ||
                 (e.Definition[ObjdField.MasterId] != 0 && e.Definition[ObjdField.SubIndex] == ushort.MaxValue)));
            Assert.NotNull(dtOnlyEntry);
            Assert.True(ObjectCatalog.TryGetEntry(dtOnlyEntry!, 1, CatalogLotType.Downtown, out var catItem));
            Assert.Equal(dtOnlyEntry!.Definition[(ObjdField)95] & 0x8f, catItem!.Categories);
        }
        finally
        {
            if (Directory.Exists(session)) Directory.Delete(session, recursive: true);
        }
    }

    [Fact]
    public void ResetObjectsWithChangedCode_StaleObjtAcrossRealHouse05_LoadsWithoutStepLimitCrash()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        var housePath = Path.Combine(profile.UserData, "Houses", "House05.iff");
        var houseIff = IffFile.Open(housePath);
        var objtType = ResourceType.Parse("objt");
        var objtChunk = houseIff.OfType(objtType).First();
        var originalTable = ObjectTypeTable.Read(houseIff, objtChunk);
        var staleEntries = originalTable.Entries
            .Select(e => e with { TreeValue0 = e.TreeValue0 ^ 0x1234u })
            .ToArray();
        var staleBytes = originalTable.Write(false, "tjbo", staleEntries);
        File.WriteAllBytes(housePath, houseIff.WithAddedOrReplacedChunk(objtType, objtChunk.Id, staleBytes).Bytes.ToArray());

        using var server = E2EHarness.StartServer(profile, house: 5);
        var world = server.World!;
        Assert.Empty(world.ChangedCodeTypeGuids);
        Assert.Contains(world.WalkOrder, o => unchecked((uint)(o.TypeRecord?.Key ?? 0)) == 0x11B197B4u);
    }

    [Fact]
    public void WallTool_Add_OverwritesZeroValueOpeningCleanly()
    {
        byte[] BuildArry(int elementSize, byte[] rawData) => new ChunkBuilder()
            .S16(0).S16(0).S32(64).S32(64).S32(elementSize).S32(0).Raw(rawData).ToArray();
        var lot = new IffFile(ChunkBuilder.File(
            ("Arry", 0, BuildArry(4, new byte[64 * 64 * 4])),
            ("Arry", 2, BuildArry(8, new byte[64 * 64 * 8])),
            ("Arry", 8, BuildArry(1, Enumerable.Repeat((byte)1, 64 * 64).ToArray()))
        ), "lot.iff");
        var wallStrings = new ChunkBuilder().S16(-3).S16(3)
            .U8(1).CString("10").CString("")
            .U8(1).CString("Wall").CString("")
            .U8(1).CString("Desc").CString("");
        var build = new IffFile(ChunkBuilder.File(("STR#", 0x81, wallStrings.ToArray())), "Build.iff");
        var wallCatalog = WallCatalog.Read(build);

        var world = new SimulationWorld();
        var doorDef = new short[XObject.DefinitionCount];
        doorDef[(int)ObjdField.Type] = 8;
        var door = world.Add(new XObject(20, doorDef) { Position = new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1) });
        door[ObjectData.WallPlacementFlags] = 1; // north local bit (2)
        door[ObjectData.MovementFlags] = 2 | 8; // pickable & sellable
        door[ObjectData.Direction] = 0;
        door[ObjectData.CurrentValue] = 0;
        world.SetObjectPosition(door, door.Position);

        var walls = LotWalls.Read(lot, 1);
        walls[10, 10][0] = 2;
        walls[10, 10][3] = 3; // opening style

        var tool = new WallTool(world, walls, LotTerrain.Read(lot), wallCatalog, new TestBudget(1000), tiles: LotTiles.Read(lot));
        var changed = tool.Line(10, 10, 11, 10, style: 1, pattern: 0, erase: false);
        Assert.Equal(1, changed);
        Assert.Equal(1, walls[10, 10][3]);
        Assert.Null(world.ObjectById(door.Id));
    }

    [Fact]
    public void NeighborhoodExport_UnhousedFamilies_IncludesImportedFlagsBit8AndEvictedWhileExcludingStrays()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"m5-bin-{Guid.NewGuid():N}");
        try
        {
            var gameDir = Path.Combine(tempRoot, "game");
            var userDir = Path.Combine(tempRoot, "profile", "UserData");
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(Path.Combine(userDir, "Characters"));

            const uint importedGuid = 0x20000001u;
            const uint evictedGuid = 0x20000002u;
            const uint strayFlaggedGuid = 0x20000003u;

            static Neighbor MakeNeighbor(int slot, string name, uint guid)
            {
                var pdata = new short[256];
                pdata[(int)PersonData.PersonAge] = 27;
                return new Neighbor(slot, 9, name, 0, pdata, checked((short)(slot + 1)), guid, new Dictionary<int, IReadOnlyList<int>>(), 10, 0);
            }

            var neighbors = new[]
            {
                MakeNeighbor(0, "ImportedSim", importedGuid),
                MakeNeighbor(1, "EvictedSim", evictedGuid),
                MakeNeighbor(2, "StrayHiddenSim", strayFlaggedGuid),
            };

            var importedFam = Family.CreateNew(1, 0, [importedGuid], 20_000).WithValue(0).WithFields(flags: 0x09u);
            var evictedFam = Family.CreateNew(2, 0, [evictedGuid], 25_000).WithValue(0).WithFields(flags: 0x05u);
            var hiddenStrayFam = Family.CreateNew(3, 0, [strayFlaggedGuid], 20_000).WithValue(0).WithFields(flags: 0x18u);

            var nghPath = Path.Combine(userDir, "Neighborhood.iff");
            var iff = new IffFile(ChunkBuilder.File(
                ("NBRS", 1, new Neighbors(73, neighbors.Length, neighbors).WriteChunkData(swap: false)),
                ("FAMI", 1, importedFam.Write(1)),
                ("FAMs", 1, StringTable.CreateLanguageValues(["Imported"], 1)),
                ("FAMI", 2, evictedFam.Write(2)),
                ("FAMs", 2, StringTable.CreateLanguageValues(["Evicted"], 1)),
                ("FAMI", 3, hiddenStrayFam.Write(3)),
                ("FAMs", 3, StringTable.CreateLanguageValues(["HiddenStray"], 1))
            ), nghPath);
            File.WriteAllBytes(nghPath, iff.Bytes.ToArray());

            var exported = NeighborhoodExport.UnhousedFamilies(userDir, gameDir, language: 1, existingRegistry: new ObjectRegistry());
            Assert.Equal(["Imported", "Evicted"], exported.Select(f => f.Name).ToArray());
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GenericSimsCall11_VacationAndMultiHop_PreservesMutatedMotivesAndPersonData80_82_OnReturnHome()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 40, 41))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 40, 41);
        using var server = E2EHarness.StartServer(profile, house: 5);
        var homeWorld = server.World!;
        var sim = homeWorld.WalkOrder.OfType<XPerson>()
            .First(p => p[PersonData.FamilyNumber] > 0 && (p.TypeRecord?.Key ?? 0) != 0);
        var simGuid = sim.TypeRecord!.Key;

        sim.Motives[9] = 30f;
        sim.PersonData[80] = 4;
        sim.PersonData[81] = 1;
        sim.PersonData[82] = 7;
        sim.Temporaries[0] = 40;
        sim.Temporaries[1] = 10;
        sim.Temporaries[2] = 30;
        sim.Temporaries[3] = 1;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(
            new TreeContext(homeWorld, sim, new StackFrame(sim.Id, new short[8], [0, 0])), Operand(0x11)));

        server.BatchRun(1);
        var vac1World = server.World!;
        Assert.Equal(40, vac1World.ActiveLotNumber);
        var vac1Sim = vac1World.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == simGuid);
        Assert.Equal(30f, vac1Sim.Motives[9]);
        Assert.Equal((short)1, vac1Sim.PersonData[81]);

        vac1Sim.Motives[9] = 72f;
        vac1Sim.PersonData[80] = 5;
        vac1Sim.PersonData[81] = 2;
        vac1Sim.PersonData[82] = 8;
        vac1Sim.Temporaries[0] = 41;
        vac1Sim.Temporaries[1] = 11;
        vac1Sim.Temporaries[2] = 15;
        vac1Sim.Temporaries[3] = 1;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(
            new TreeContext(vac1World, vac1Sim, new StackFrame(vac1Sim.Id, new short[8], [0, 0])), Operand(0x11)));

        server.BatchRun(1);
        var vac2World = server.World!;
        Assert.Equal(41, vac2World.ActiveLotNumber);
        var vac2Sim = vac2World.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == simGuid);
        Assert.Equal(72f, vac2Sim.Motives[9]);
        Assert.Equal((short)5, vac2Sim.PersonData[80]);
        Assert.Equal((short)2, vac2Sim.PersonData[81]);
        Assert.Equal((short)8, vac2Sim.PersonData[82]);

        vac2Sim.Motives[9] = 88f;
        vac2Sim.PersonData[80] = 6;
        vac2Sim.PersonData[81] = 3;
        vac2Sim.PersonData[82] = 9;
        vac2Sim.Temporaries[0] = -1;
        vac2Sim.Temporaries[1] = 2;
        vac2Sim.Temporaries[2] = 15;
        vac2Sim.Temporaries[3] = 1;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(
            new TreeContext(vac2World, vac2Sim, new StackFrame(vac2Sim.Id, new short[8], [0, 0])), Operand(0x11)));

        server.BatchRun(1);
        var returnedWorld = server.World!;
        Assert.Equal(5, returnedWorld.ActiveLotNumber);
        var homeSim = returnedWorld.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == simGuid);
        Assert.Equal(88f, homeSim.Motives[9]);
        Assert.Equal((short)6, homeSim.PersonData[80]);
        Assert.Equal((short)3, homeSim.PersonData[81]);
        Assert.Equal((short)9, homeSim.PersonData[82]);
    }

    [Fact]
    public void GenericSimsCall11_DowntownAndStudioTown_PreservesMutatedMotivesAndStarPower_OnReturnHome()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 21))
            return;

        // 1. Downtown round-trip (House05 -> House21 -> House05, temp3 = 0)
        using (var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 21))
        using (var server = E2EHarness.StartServer(profile, house: 5))
        {
            var homeWorld = server.World!;
            var sim = homeWorld.WalkOrder.OfType<XPerson>()
                .First(p => p[PersonData.FamilyNumber] > 0 && (p.TypeRecord?.Key ?? 0) != 0);
            var simGuid = sim.TypeRecord!.Key;

            sim.Motives[9] = 20f;
            sim.PersonData[80] = 2;
            sim.PersonData[81] = 1;
            sim.PersonData[82] = 3;
            sim.Temporaries[0] = 21;
            sim.Temporaries[1] = 0;
            sim.Temporaries[2] = 30;
            sim.Temporaries[3] = 0;
            Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(
                new TreeContext(homeWorld, sim, new StackFrame(sim.Id, new short[8], [0, 0])), Operand(0x11)));

            server.BatchRun(1);
            var dtWorld = server.World!;
            Assert.Equal(21, dtWorld.ActiveLotNumber);
            var dtSim = dtWorld.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == simGuid);
            dtSim.Motives[9] = 95f;
            dtSim.PersonData[80] = 7;
            dtSim.PersonData[81] = 4;
            dtSim.PersonData[82] = 6;
            dtSim.Temporaries[0] = -1;
            dtSim.Temporaries[1] = 0;
            dtSim.Temporaries[2] = 30;
            dtSim.Temporaries[3] = 0;
            Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(
                new TreeContext(dtWorld, dtSim, new StackFrame(dtSim.Id, new short[8], [0, 0])), Operand(0x11)));

            server.BatchRun(1);
            var returnedWorld = server.World!;
            Assert.Equal(5, returnedWorld.ActiveLotNumber);
            var homeSim = returnedWorld.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == simGuid);
            Assert.Equal(95f, homeSim.Motives[9]);
            Assert.Equal((short)7, homeSim.PersonData[80]);
            Assert.Equal((short)4, homeSim.PersonData[81]);
            Assert.Equal((short)6, homeSim.PersonData[82]);
        }

        // 2. Studio Town fame promotion via GenericSimsCall 0x22 persists across 0x11 return home.
        if (!E2EHarness.HasHouses(gameDir, 81))
            return;

        using (var stProfile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 81))
        using (var stServer = E2EHarness.StartServer(stProfile, house: 5))
        {
            var homeWorld = stServer.World!;
            var sim = homeWorld.WalkOrder.OfType<XPerson>()
                .First(p => p[PersonData.FamilyNumber] > 0 && (p.TypeRecord?.Key ?? 0) != 0);
            var simGuid = sim.TypeRecord!.Key;

            sim.PersonData[(int)PersonData.FameStarPower09] = 0;
            sim.PersonData[(int)PersonData.FameScore] = 500;
            sim.PersonData[10] = 1000;
            sim.PersonData[12] = 1000;
            sim.PersonData[11] = 1000;
            sim.PersonData[17] = 1000;
            sim.PersonData[18] = 1000;
            sim.PersonData[15] = 1000;
            sim.Temporaries[0] = 81;
            sim.Temporaries[1] = 0;
            sim.Temporaries[2] = 30;
            sim.Temporaries[3] = 0;
            Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(
                new TreeContext(homeWorld, sim, new StackFrame(sim.Id, new short[8], [0, 0])), Operand(0x11)));

            stServer.BatchRun(1);
            var stWorld = stServer.World!;
            Assert.Equal(81, stWorld.ActiveLotNumber);
            var stSim = stWorld.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == simGuid);
            stWorld.GenericSimsCallRecordCallbackTarget ??= new GenericSimsCallRecordTarget
            {
                FameStarThresholds = [50, 100],
                FameFriendThresholds = [0, 0],
                FameSkillThresholds = Enumerable.Repeat<IReadOnlyList<int>>([0, 0], 6).ToArray(),
            };
            stSim.SetObjectTableEntry(stSim.Id, stSim);
            stSim.PersonData[(int)PersonData.FameScore] = 200;
            Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(
                new TreeContext(stWorld, stSim, new StackFrame(stSim.Id, new short[8], [0, 0]) { StackObject = stSim.Id }),
                Operand(0x22)));
            Assert.Equal((short)1, stSim.PersonData[(int)PersonData.FameStarPower09]);

            stSim.Temporaries[0] = -1;
            stSim.Temporaries[1] = 0;
            stSim.Temporaries[2] = 30;
            stSim.Temporaries[3] = 0;
            Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(
                new TreeContext(stWorld, stSim, new StackFrame(stSim.Id, new short[8], [0, 0])), Operand(0x11)));

            stServer.BatchRun(1);
            var returnedWorld = stServer.World!;
            Assert.Equal(5, returnedWorld.ActiveLotNumber);
            var homeSim = returnedWorld.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == simGuid);
            Assert.Equal((short)1, homeSim.PersonData[(int)PersonData.FameStarPower09]);
        }
    }

    private sealed class CountingTreeTable(IReadOnlyDictionary<short, Action> counters) : ITreeTable
    {
        private readonly Bhav _tree = BuildReturnTrueTree();

        public Bhav? Find(short id)
        {
            if (!counters.TryGetValue(id, out var onRun)) return null;
            onRun();
            return _tree;
        }

        private static Bhav BuildReturnTrueTree()
        {
            var expression = PrimitiveTestWorld.Expression(0, 0, (byte)ExpressionOperator.EqualTo);
            var body = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
                .U16(2).U8(BhavInstruction.ReturnTrue).U8(BhavInstruction.ReturnFalse).Raw(expression);
            var (file, chunk) = body.InFile("BHAV", 4096);
            return Bhav.Read(file, chunk);
        }
    }

    [Theory]
    [InlineData("LeadTree3Zero_LeadBeforePart", true, true)]
    [InlineData("LeadTree3Zero_PartBeforeLead", true, false)]
    [InlineData("PartTree3Zero_LeadBeforePart", false, true)]
    public void ResetObjectsWithChangedCode_MixedTree3MultiTileGroup_ResetsEachGroupOnce(
        string configuration, bool leadTree3Zero, bool leadBeforePart)
    {
        _ = configuration;
        var world = new SimulationWorld();
        var leadInitRuns = 0;
        var leadTree3Runs = 0;
        var partInitRuns = 0;
        var partTree3Runs = 0;
        var leadTrees = new CountingTreeTable(new Dictionary<short, Action>
        {
            [100] = () => leadInitRuns++,
            [102] = () => leadTree3Runs++,
        });
        var partTrees = new CountingTreeTable(new Dictionary<short, Action>
        {
            [100] = () => partInitRuns++,
            [102] = () => partTree3Runs++,
        });

        var leadDef = new short[XObject.DefinitionCount];
        leadDef[(int)ObjdField.Type] = 4;
        leadDef[(int)ObjdField.MasterId] = 1;
        leadDef[(int)ObjdField.SubIndex] = 0;
        var lead = new XObject(1, leadDef)
        {
            MultiTileMasterId = 0,
            MultiTileNextId = 2,
            Position = new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1),
            TypeRecord = new TypeRecord(0x55550001)
            {
                FunctionTable = [100, 101, 0, leadTree3Zero ? (ushort)0 : (ushort)102],
                BehaviorScope = leadTrees,
            },
        };

        var partDef = new short[XObject.DefinitionCount];
        partDef[(int)ObjdField.Type] = 4;
        partDef[(int)ObjdField.MasterId] = 1;
        partDef[(int)ObjdField.SubIndex] = 1;
        var part = new XObject(2, partDef)
        {
            MultiTileMasterId = 1,
            MultiTileNextId = 0,
            Position = new ObjectPosition(11 * 16 + 8, 10 * 16 + 8, 1),
            TypeRecord = new TypeRecord(0x55550002)
            {
                FunctionTable = [100, 101, 0, leadTree3Zero ? (ushort)102 : (ushort)0],
                BehaviorScope = partTrees,
            },
        };

        if (leadBeforePart)
        {
            world.Add(lead);
            world.Add(part);
        }
        else
        {
            world.Add(part);
            world.Add(lead);
        }
        world.ChangedCodeTypeGuids.Add(0x55550001u);
        world.ChangedCodeTypeGuids.Add(0x55550002u);

        world.ResetObjectsWithChangedCode();

        // Any member with FunctionTreeId(3) == 0 triggers a single group-wide ResetObjectAfterScriptError
        // regardless of WalkOrder or which member lacks Tree 3 (init runs once per member, Tree 3 never runs).
        Assert.Equal(1, leadInitRuns);
        Assert.Equal(1, partInitRuns);
        Assert.Equal(0, leadTree3Runs);
        Assert.Equal(0, partTree3Runs);
    }

    [Fact]
    public void ResetObjectsWithChangedCode_MixedTree3_PartTree9ReadsMySlot_UsesMemberAsMe()
    {
        var world = new SimulationWorld();
        var initTree = PrimitiveTestWorld.Tree(0, (2, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse,
            PrimitiveTestWorld.Expression(0, 0, (byte)ExpressionOperator.EqualTo)));
        var partTree9 = PrimitiveTestWorld.Tree(0,
            (2, 1, BhavInstruction.ReturnFalse,
                PrimitiveTestWorld.Expression(1, 1, (byte)ExpressionOperator.AssignTo, (byte)DataOwner.Temporary, (byte)DataOwner.MySlot)),
            (2, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse,
                PrimitiveTestWorld.Expression(0, 42, (byte)ExpressionOperator.AssignTo, (byte)DataOwner.Temporary, (byte)DataOwner.Literal)));

        var leadDef = new short[XObject.DefinitionCount];
        leadDef[(int)ObjdField.Type] = 4;
        leadDef[(int)ObjdField.MasterId] = 1;
        leadDef[(int)ObjdField.SubIndex] = 0;
        var lead = new XObject(1, leadDef)
        {
            RuntimeClass = ObjectClass.MultiTileObject,
            MultiTileMasterId = 0,
            MultiTileNextId = 2,
            Position = new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1),
            TypeRecord = new TypeRecord(0x55550011)
            {
                FunctionTable = [100, 101, 0, 102],
                BehaviorScope = new TreeTable((100, initTree), (101, initTree), (102, initTree)),
            },
        };

        var partDef = new short[XObject.DefinitionCount];
        partDef[(int)ObjdField.Type] = 4;
        partDef[(int)ObjdField.MasterId] = 1;
        partDef[(int)ObjdField.SubIndex] = 1;
        var part = new XObject(2, partDef)
        {
            RuntimeClass = ObjectClass.MultiTileObject,
            MultiTileMasterId = 1,
            MultiTileNextId = 0,
            Position = new ObjectPosition(11 * 16 + 8, 10 * 16 + 8, 1),
            TypeRecord = new TypeRecord(0x55550012)
            {
                FunctionTable = [100, 101, 0, 0, 0, 0, 0, 0, 0, 109],
                BehaviorScope = new TreeTable((100, initTree), (101, initTree), (109, partTree9)),
            },
        };
        part.Slots.AddRange([0, 42, 0, 0]);

        world.Add(lead);
        world.Add(part);
        world.ChangedCodeTypeGuids.Add(0x55550011u);
        world.ChangedCodeTypeGuids.Add(0x55550012u);

        world.ResetObjectsWithChangedCode();

        Assert.Equal((short)42, part.Temporaries[0]);
        Assert.Equal((short)0, lead.Temporaries[0]);
    }

    [Fact]
    public void ResetObjectsWithChangedCode_DyingMixedTree3MultiTileGroup_UnplacesAllMembersWithoutReinit()
    {
        var world = new SimulationWorld();
        var leadInitRuns = 0;
        var leadTree3Runs = 0;
        var partInitRuns = 0;
        var partTree3Runs = 0;
        var leadTrees = new CountingTreeTable(new Dictionary<short, Action>
        {
            [100] = () => leadInitRuns++,
            [102] = () => leadTree3Runs++,
        });
        var partTrees = new CountingTreeTable(new Dictionary<short, Action>
        {
            [100] = () => partInitRuns++,
            [102] = () => partTree3Runs++,
        });

        var leadDef = new short[XObject.DefinitionCount];
        leadDef[(int)ObjdField.Type] = 4;
        leadDef[(int)ObjdField.MasterId] = 1;
        leadDef[(int)ObjdField.SubIndex] = 0;
        var lead = new XObject(1, leadDef)
        {
            MultiTileMasterId = 0,
            MultiTileNextId = 2,
            Position = new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1),
            TypeRecord = new TypeRecord(0x55550021)
            {
                FunctionTable = [100, 101, 0, 0],
                BehaviorScope = leadTrees,
            },
        };

        var partDef = new short[XObject.DefinitionCount];
        partDef[(int)ObjdField.Type] = 4;
        partDef[(int)ObjdField.MasterId] = 1;
        partDef[(int)ObjdField.SubIndex] = 1;
        var part = new XObject(2, partDef)
        {
            MultiTileMasterId = 1,
            MultiTileNextId = 0,
            Position = new ObjectPosition(11 * 16 + 8, 10 * 16 + 8, 1),
            Flags = 0x40u,
            TypeRecord = new TypeRecord(0x55550022)
            {
                FunctionTable = [100, 101, 0, 102],
                BehaviorScope = partTrees,
            },
        };

        world.Add(lead);
        world.Add(part);
        world.ChangedCodeTypeGuids.Add(0x55550021u);
        world.ChangedCodeTypeGuids.Add(0x55550022u);

        world.ResetObjectsWithChangedCode();

        Assert.Equal(0, leadInitRuns);
        Assert.Equal(0, partInitRuns);
        Assert.Equal(0, leadTree3Runs);
        Assert.Equal(0, partTree3Runs);
        Assert.Equal(new ObjectPosition(-16, -16, 1), lead.Position);
        Assert.Equal(new ObjectPosition(-16, -16, 1), part.Position);
    }

    [Theory]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(30)]
    public void SimulationServer_LoadHouse28Through30_SucceedsWithoutSlotNumberOutOfRange(short house)
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, house))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, house);
        using var server = E2EHarness.StartServer(profile, house: house);
        var world = server.World!;
        Assert.NotEmpty(world.WalkOrder);
        Assert.DoesNotContain(world.ThreadErrors, error => error.Message.Contains("SlotNumberOutOfRange", StringComparison.Ordinal));
        server.BatchRun(1);
        Assert.DoesNotContain(world.ThreadErrors, error => error.Message.Contains("SlotNumberOutOfRange", StringComparison.Ordinal));
    }

    [Fact]
    public void InteractionExecutorScope_PreBindReference_RemainsBackedAfterBindPersonToNeighborRecord()
    {
        var world = new SimulationWorld();
        const int simGuid = 0x77771234;
        var sim = world.Add(new XPerson(1, PersonDef()) { TypeRecord = new TypeRecord(simGuid) });
        Assert.Equal((short)0, sim[PersonData.NeighborId]);

        var preBind = world.InteractionExecutorScope(sim);
        preBind.AddOrIncrement(1, 0, 42);

        var neighbor = new NeighborState(7, PersonDef()) { TypeRecordKey = simGuid };
        world.Neighbors[7] = neighbor;
        world.NeighborWalkOrder.Add(neighbor);

        var bound = world.BindPersonToNeighborRecord(sim);
        Assert.Same(neighbor, bound);
        Assert.Equal((short)7, sim[PersonData.NeighborId]);

        var postBind = world.InteractionExecutorScope(sim);
        Assert.Same(preBind, postBind);
        Assert.True(preBind.IsBackedBy(world.NeighborInventory(7)));
        Assert.Equal(1, preBind.CountValue(1));

        // Mutating NeighborInventory directly (or reloading NGBH) is visible through the preBind reference.
        world.NeighborInventory(7).Add(3, 0, 99);
        Assert.Equal(1, preBind.CountValue(3));
    }

    [Fact]
    public void Architecture_MultiTilePortalPartialWallEraseAndOverwrite_ResetsSurvivingWallStyleAcrossUndoRedoAndSaveLoad()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        const uint windowWesternGuid = 0xD0A244E4u;
        var registry = ObjectRegistry.Scan(gameDir);
        if (registry.Find(windowWesternGuid) is null)
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        var world = server.World!;

        var lotPath = Path.Combine(profile.UserData, "Houses", "House05.iff");
        var lotIff = IffFile.Open(lotPath);
        var tiles = LotTiles.Read(lotIff);
        var terrain = LotTerrain.Read(lotIff);
        var pool = LotPool.Read(lotIff);
        var water = LotWater.Read(lotIff);

        // Find a flat, empty 6x3 strip on Level 1 to place a 3-tile horizontal window (Bit = 2) cleanly without overhanging wall ends.
        var walls1 = LotWalls.Read(lotIff, 1);
        (int X, int Y)? strip = null;
        for (var y = 10; y < LotWalls.Size - 10 && strip is null; y++)
        for (var x = 10; x < LotWalls.Size - 12; x++)
        {
            var ok = true;
            for (var dx = 0; dx < 6 && ok; dx++)
            for (var dy = -1; dy <= 1 && ok; dy++)
            {
                if (!tiles.InsideLot(x + dx, y + dy) || !tiles.Buildable(x + dx, y + dy, 1) ||
                    pool.IsPool(x + dx, y + dy, 1) || water.IsWater(x + dx, y + dy, 1) ||
                    walls1[x + dx, y + dy][0] != 0 ||
                    !terrain.IsFlatAt(x + dx, y + dy, terrain.ReferenceHeight(x, y)) ||
                    world.ObjectsAtTile(x + dx, y + dy, 1).Any())
                    ok = false;
            }
            if (ok) strip = (x, y);
        }
        Assert.NotNull(strip);
        var (wx, wy) = strip!.Value;

        var buildWall = server.HandleJson("architecture", new { command = "WALL_LINE", level = 1, x = wx, y = wy, x2 = wx + 5, y2 = wy, style = 1, pattern = 0 });
        Assert.True(buildWall.GetProperty("ok").GetBoolean(), buildWall.ToString());
        Assert.Equal(5, buildWall.GetProperty("result").GetProperty("changed").GetInt32());

        void DismissDialogs()
        {
            while (world.DialogRecords.FirstOrDefault(d => d.Visible && d.State < 2) is { } dlg)
                world.RespondMessageDialog(dlg.Token, dlg.Kind is 1 or 2 or 14 ? (byte)1 : (byte)0);
        }
        DismissDialogs();

        JsonElement buyWin = default;
        short winId = 0;
        var placed = false;
        foreach (var tx in new[] { wx + 2, wx + 1, wx + 3 })
        foreach (var dir in new[] { 1, 16, 4, 64 })
        {
            DismissDialogs();
            buyWin = server.HandleJson("buyObject", new { guid = windowWesternGuid, x = tx * 16 + 8, y = wy * 16 + 8, level = 1, direction = dir });
            if (buyWin.TryGetProperty("ok", out var okEl) && okEl.GetBoolean())
            {
                var leadId = (short)buyWin.GetProperty("result").GetProperty("objectID").GetInt32();
                var leadObj = world.ObjectById(leadId);
                if (leadObj is not null &&
                    world.MultiTileMembers(leadObj).Select(m => m.Position.X >> 4).Distinct().Count() == 3)
                {
                    winId = leadId;
                    placed = true;
                    break;
                }
                server.HandleJson("undo", new { });
            }
            if (placed) break;
        }
        Assert.True(placed, buyWin.ToString());
        var winLead = world.ObjectById(winId)!;
        var winPartGuid = unchecked((uint)(winLead.TypeRecord?.Key ?? 0));
        var winXs = world.MultiTileMembers(winLead)
            .Select(m => m.Position.X >> 4)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
        Assert.Equal(3, winXs.Length);
        var winX0 = winXs[0];
        var survivingXs = winXs[1..];

        // 1. Partial WALL_ERASE on only the first tile of the 3-tile Window - Western.
        var partialErase = server.HandleJson("architecture", new { command = "WALL_ERASE", level = 1, x = winX0, y = wy, x2 = winX0 + 1, y2 = wy, style = 1, pattern = 0 });
        Assert.True(partialErase.GetProperty("ok").GetBoolean(), partialErase.ToString());
        Assert.Equal(1, partialErase.GetProperty("result").GetProperty("changed").GetInt32());
        Assert.Null(world.ObjectById(winId));

        // Save immediately and verify disk LotWalls has WallStyle = 1 on all surviving sibling tiles.
        var saveAfterErase = server.HandleJson("save", new { slot = "m5-partial-erase", lot = true });
        Assert.True(saveAfterErase.GetProperty("ok").GetBoolean(), saveAfterErase.ToString());
        var savedErasePath = Path.Combine(profile.Profile, "native-saves", Neighborhoods.StorageSlot("m5-partial-erase", profile.NeighborhoodNumber) + ".iff");
        var savedEraseIff = IffFile.Open(savedErasePath);
        var savedEraseWalls = LotWalls.Read(savedEraseIff, 1);
        Assert.Equal(0, savedEraseWalls[winX0, wy][0] & 2);
        foreach (var sx in survivingXs)
        {
            Assert.NotEqual(0, savedEraseWalls[sx, wy][0] & 2);
            Assert.Equal(1, savedEraseWalls[sx, wy][3]);
        }

        // Verify SceneExporter in !hasLiveObjects mode sees plain wall (WallSide metadata present for style == 1) on surviving tile.
        var exportDir = Path.Combine(profile.Session, "m5-scene-check");
        Directory.CreateDirectory(exportDir);
        var exportedScenePath = SceneExporter.Export(savedEraseIff, new SimulationWorld(), registry, gameDir, exportDir, 5, 1, viewLevel: 1, wallMode: "up");
        using (var sceneDoc = JsonDocument.Parse(SimsSimulation.InMemorySessionAssets.ReadAllBytes(exportedScenePath)))
        {
            var lastSibX = survivingXs[^1];
            var hasPlainWallOnSibling = sceneDoc.RootElement.GetProperty("sprites").EnumerateArray().Any(s =>
                s.TryGetProperty("wall", out var w) && w.ValueKind == JsonValueKind.Object &&
                w.GetProperty("x").GetInt32() == lastSibX && w.GetProperty("y").GetInt32() == wy &&
                w.GetProperty("level").GetInt32() == 1);
            Assert.True(hasPlainWallOnSibling);
        }

        // 2. Undo -> Redo cycle restores Window - Western + style 22, then Redo restores WallStyle = 1 on surviving tiles.
        Assert.True(server.HandleJson("undo", new { }).GetProperty("ok").GetBoolean());
        Assert.Contains(world.WalkOrder, o => o.Definition[(int)ObjdField.Type] == 8 && unchecked((uint)(o.TypeRecord?.Key ?? 0)) == winPartGuid);
        Assert.True(server.HandleJson("redo", new { }).GetProperty("ok").GetBoolean());
        Assert.DoesNotContain(world.WalkOrder, o => o.Definition[(int)ObjdField.Type] == 8 && unchecked((uint)(o.TypeRecord?.Key ?? 0)) == winPartGuid);
        var saveAfterRedo = server.HandleJson("save", new { slot = "m5-partial-redo", lot = true });
        Assert.True(saveAfterRedo.GetProperty("ok").GetBoolean(), saveAfterRedo.ToString());
        var redoWalls = LotWalls.Read(IffFile.Open(Path.Combine(profile.Profile, "native-saves", Neighborhoods.StorageSlot("m5-partial-redo", profile.NeighborhoodNumber) + ".iff")), 1);
        foreach (var sx in survivingXs)
            Assert.Equal(1, redoWalls[sx, wy][3]);

        // 3. Undo back to intact window, then partial WALL_LINE overwrite across 1 tile, followed by WALL_LINE (changed==0) and WALL_ERASE on sibling.
        Assert.True(server.HandleJson("undo", new { }).GetProperty("ok").GetBoolean());
        DismissDialogs();
        var partialOverwrite = server.HandleJson("architecture", new { command = "WALL_LINE", level = 1, x = winX0, y = wy, x2 = winX0 + 1, y2 = wy, style = 1, pattern = 0 });
        Assert.True(partialOverwrite.GetProperty("ok").GetBoolean(), partialOverwrite.ToString());
        Assert.Equal(1, partialOverwrite.GetProperty("result").GetProperty("changed").GetInt32());
        Assert.DoesNotContain(world.WalkOrder, o => o.Definition[(int)ObjdField.Type] == 8 && unchecked((uint)(o.TypeRecord?.Key ?? 0)) == winPartGuid);

        DismissDialogs();
        var subsequentWallLine = server.HandleJson("architecture", new { command = "WALL_LINE", level = 1, x = survivingXs[0], y = wy, x2 = survivingXs[0] + 1, y2 = wy, style = 1, pattern = 0 });
        Assert.True(subsequentWallLine.GetProperty("ok").GetBoolean(), subsequentWallLine.ToString());
        Assert.Equal(0, subsequentWallLine.GetProperty("result").GetProperty("changed").GetInt32());

        DismissDialogs();
        var subsequentErase = server.HandleJson("architecture", new { command = "WALL_ERASE", level = 1, x = survivingXs[0], y = wy, x2 = survivingXs[0] + 1, y2 = wy, style = 1, pattern = 0 });
        Assert.True(subsequentErase.GetProperty("ok").GetBoolean(), subsequentErase.ToString());
        Assert.Equal(1, subsequentErase.GetProperty("result").GetProperty("changed").GetInt32());

        // 4. Reload saved slot into a fresh SimulationServer and confirm WallStyle == 1 persists cleanly on surviving tile.
        Assert.True(server.HandleJson("save", new { slot = "m5-final-persist", lot = true }).GetProperty("ok").GetBoolean());
        using var reloadedServer = E2EHarness.StartServer(profile, house: 5);
        Assert.True(reloadedServer.HandleJson("load", new { slot = "m5-final-persist" }).GetProperty("ok").GetBoolean());
        var finalPersistPath = Path.Combine(profile.Profile, "native-saves", Neighborhoods.StorageSlot("m5-final-persist", profile.NeighborhoodNumber) + ".iff");
        var reloadedWalls = LotWalls.Read(IffFile.Open(finalPersistPath), 1);
        Assert.Equal(1, reloadedWalls[survivingXs[^1], wy][3]);
    }

    [Fact]
    public void GenericSimsCall26_MagicMasterSpellsRealStrTables_ParsesRecipesAccurately()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;

        var farPath = Path.Combine(gameDir, "ExpansionPack7", "ExpansionPack7.far");
        if (!File.Exists(farPath))
            return;

        var far = FarArchive.Open(farPath);
        var entry = far.Entries.FirstOrDefault(e => e.Name.EndsWith("MagicMasterSpells.iff", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry);
        var spellsIff = new IffFile(far.Read(entry!).ToArray(), "MagicMasterSpells.iff");
        var library = GlobalLibrary.Open(gameDir);
        Assert.NotNull(library.Global);
        var scope = new BehaviorScope(library.Global!, spellsIff, null);

        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, PersonDef()) { TypeRecord = new TypeRecord(0x7726) });
        var frame = new StackFrame(me.Id, new short[8], [0, 0]) { Table = scope, TreeId = 4096 };
        var ctx = new TreeContext(world, me, frame);

        foreach (short tableId in new short[] { 3, 4, 5, 6 })
        {
            var table = StringTable.Read(spellsIff, spellsIff.Find(ResourceType.Str, (ushort)tableId)!);
            var strings = table.ForLanguage(1);
            for (var index = 1; index < strings.Count; index++)
            {
                var raw = strings[index];
                Array.Clear(me.Temporaries);
                me.Temporaries[0] = tableId;
                me.Temporaries[1] = (short)index;
                Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, Operand(0x26)));

                if (string.IsNullOrEmpty(raw))
                {
                    Assert.Equal((short)0, me.Temporaries[0]);
                }
                else
                {
                    var expectedParts = raw.Split(',').Select(short.Parse).ToArray();
                    Assert.Equal((short)expectedParts.Length, me.Temporaries[0]);
                    for (var i = 0; i < expectedParts.Length; i++)
                        Assert.Equal(expectedParts[i], me.Temporaries[i + 1]);
                }
            }
        }
    }

    [Fact]
    public void SimulationServer_GenericSimsCallPersonPicker_CompletesCall28RoundTrip()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        var world = server.World!;
        var sim = world.WalkOrder.OfType<XPerson>()
            .First(p => p[PersonData.FamilyNumber] > 0 && p.GenericSimsCallPersonTypeRecords.Count > 0);

        var frame = new StackFrame(sim.Id, new short[8], [0, 0]);
        var ctx = new TreeContext(world, sim, frame);
        sim.Temporaries[0] = sim.StackObjectKey;
        Assert.Equal(PrimitiveResult.Stop, GenericSimsCallPrimitive.Execute(ctx, Operand(0x28)));
        Assert.Equal((byte)1, frame.WaitState);

        var poll = server.HandleJson("genericSimsCallPersonPicker", new { centerX = 32, centerY = 32 });
        Assert.True(poll.GetProperty("ok").GetBoolean(), poll.ToString());
        var pollResult = poll.GetProperty("result");
        Assert.Equal(JsonValueKind.Object, pollResult.ValueKind);
        var requestId = pollResult.GetProperty("requestID").GetInt64();
        Assert.True(requestId > 0);
        var candidates = pollResult.GetProperty("candidates");
        Assert.True(candidates.GetArrayLength() > 0);

        var chosen = candidates[0];
        var personRecordIndex = chosen.GetProperty("personRecordIndex").GetInt32();
        var livePersonId = (short)chosen.GetProperty("livePersonId").GetInt32();
        var selectedPerson = Assert.IsType<XPerson>(world.ObjectById(livePersonId));

        var choose = server.HandleJson("genericSimsCallPersonPicker", new { requestID = requestId, personRecordIndex });
        Assert.True(choose.GetProperty("ok").GetBoolean(), choose.ToString());
        Assert.True(choose.GetProperty("result").GetProperty("accepted").GetBoolean(), choose.ToString());

        server.BatchRun(1);
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, Operand(0x28)));
        Assert.Equal((short)0, sim.Temporaries[0]);
        Assert.Equal(selectedPerson.StackObjectKey, sim.Temporaries[1]);
    }

    private static (int WallX, int WallY, short LeadId, uint PartGuid, int[] WindowXs) PlaceWindowWesternOnFlatWall(
        ServerSession server, IsolatedProfileScope profile)
    {
        const uint windowWesternGuid = 0xD0A244E4u;
        var world = server.World!;
        var lotPath = Path.Combine(profile.UserData, "Houses", "House05.iff");
        var lotIff = IffFile.Open(lotPath);
        var tiles = LotTiles.Read(lotIff);
        var terrain = LotTerrain.Read(lotIff);
        var pool = LotPool.Read(lotIff);
        var water = LotWater.Read(lotIff);
        var walls1 = LotWalls.Read(lotIff, 1);

        (int X, int Y)? strip = null;
        for (var y = 10; y < LotWalls.Size - 10 && strip is null; y++)
        for (var x = 10; x < LotWalls.Size - 12; x++)
        {
            var ok = true;
            for (var dx = 0; dx < 6 && ok; dx++)
            for (var dy = -1; dy <= 1 && ok; dy++)
            {
                if (!tiles.InsideLot(x + dx, y + dy) || !tiles.Buildable(x + dx, y + dy, 1) ||
                    pool.IsPool(x + dx, y + dy, 1) || water.IsWater(x + dx, y + dy, 1) ||
                    walls1[x + dx, y + dy][0] != 0 ||
                    !terrain.IsFlatAt(x + dx, y + dy, terrain.ReferenceHeight(x, y)) ||
                    world.ObjectsAtTile(x + dx, y + dy, 1).Any())
                    ok = false;
            }
            if (ok) strip = (x, y);
        }
        Assert.NotNull(strip);
        var (wx, wy) = strip!.Value;

        var buildWall = server.HandleJson("architecture", new { command = "WALL_LINE", level = 1, x = wx, y = wy, x2 = wx + 5, y2 = wy, style = 1, pattern = 0 });
        Assert.True(buildWall.GetProperty("ok").GetBoolean(), buildWall.ToString());
        Assert.Equal(5, buildWall.GetProperty("result").GetProperty("changed").GetInt32());

        void DismissDialogs()
        {
            while (world.DialogRecords.FirstOrDefault(d => d.Visible && d.State < 2) is { } dlg)
                world.RespondMessageDialog(dlg.Token, dlg.Kind is 1 or 2 or 14 ? (byte)1 : (byte)0);
        }
        DismissDialogs();

        JsonElement buyWin = default;
        short winId = 0;
        var placed = false;
        foreach (var tx in new[] { wx + 2, wx + 1, wx + 3 })
        foreach (var dir in new[] { 1, 16, 4, 64 })
        {
            DismissDialogs();
            buyWin = server.HandleJson("buyObject", new { guid = windowWesternGuid, x = tx * 16 + 8, y = wy * 16 + 8, level = 1, direction = dir });
            if (buyWin.TryGetProperty("ok", out var okEl) && okEl.GetBoolean())
            {
                var leadId = (short)buyWin.GetProperty("result").GetProperty("objectID").GetInt32();
                var leadObj = world.ObjectById(leadId);
                if (leadObj is not null &&
                    world.MultiTileMembers(leadObj).Select(m => m.Position.X >> 4).Distinct().Count() == 3)
                {
                    winId = leadId;
                    placed = true;
                    break;
                }
                server.HandleJson("undo", new { });
            }
            if (placed) break;
        }
        Assert.True(placed, buyWin.ToString());
        var winLead = world.ObjectById(winId)!;
        var winPartGuid = unchecked((uint)(winLead.TypeRecord?.Key ?? 0));
        var winXs = world.MultiTileMembers(winLead)
            .Select(m => m.Position.X >> 4)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
        Assert.Equal(3, winXs.Length);
        return (wx, wy, winId, winPartGuid, winXs);
    }

    [Fact]
    public void Architecture_BuyMultiTilePortal_SaveWhileLiveAndReload_PreservesPortalGroup()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        const uint windowWesternGuid = 0xD0A244E4u;
        var registry = ObjectRegistry.Scan(gameDir);
        if (registry.Find(windowWesternGuid) is null)
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        var world = server.World!;
        var (_, wy, winId, winPartGuid, winXs) = PlaceWindowWesternOnFlatWall(server, profile);

        var liveMembers = world.MultiTileMembers(world.ObjectById(winId)!);
        Assert.Equal(6, liveMembers.Count);
        Assert.All(liveMembers, member =>
        {
            Assert.Equal(ObjectClass.Portal, member.RuntimeClass);
            Assert.NotNull(member.SavedMultiTileState?.PortalValues);
        });

        var save = server.HandleJson("save", new { slot = "m5-buy-live-portal", lot = true });
        Assert.True(save.GetProperty("ok").GetBoolean(), save.ToString());

        using var reloadedServer = E2EHarness.StartServer(profile, house: 5);
        var load = reloadedServer.HandleJson("load", new { slot = "m5-buy-live-portal" });
        Assert.True(load.GetProperty("ok").GetBoolean(), load.ToString());

        var reloadedWorld = reloadedServer.World!;
        var reloadedLead = reloadedWorld.ObjectById(winId);
        Assert.NotNull(reloadedLead);
        Assert.Equal(winPartGuid, unchecked((uint)(reloadedLead!.TypeRecord?.Key ?? 0)));
        Assert.Equal(ObjectClass.Portal, reloadedLead.RuntimeClass);
        var reloadedMembers = reloadedWorld.MultiTileMembers(reloadedLead);
        Assert.Equal(liveMembers.Count, reloadedMembers.Count);
        var liveYs = liveMembers.Select(m => m.Position.Y >> 4).Distinct().OrderBy(y => y).ToArray();
        Assert.Equal(winXs, reloadedMembers.Select(m => m.Position.X >> 4).Distinct().OrderBy(x => x).ToArray());
        Assert.Equal(liveYs, reloadedMembers.Select(m => m.Position.Y >> 4).Distinct().OrderBy(y => y).ToArray());
        Assert.All(reloadedMembers, m =>
        {
            Assert.Contains(m.Position.Y >> 4, liveYs);
            Assert.Equal(ObjectClass.Portal, m.RuntimeClass);
            Assert.NotNull(m.SavedMultiTileState?.PortalValues);
        });
    }

    [Fact]
    public void Architecture_MultiTilePortalPartialEraseUndo_SaveWhileRestoredAndReload_PreservesPortalAndRedoResetsWalls()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        const uint windowWesternGuid = 0xD0A244E4u;
        var registry = ObjectRegistry.Scan(gameDir);
        if (registry.Find(windowWesternGuid) is null)
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        var world = server.World!;
        var (_, wy, winId, winPartGuid, winXs) = PlaceWindowWesternOnFlatWall(server, profile);
        var expectedMemberCount = world.MultiTileMembers(world.ObjectById(winId)!).Count;
        Assert.Equal(6, expectedMemberCount);
        var winX0 = winXs[0];
        var survivingXs = winXs[1..];

        var partialErase = server.HandleJson("architecture", new { command = "WALL_ERASE", level = 1, x = winX0, y = wy, x2 = winX0 + 1, y2 = wy, style = 1, pattern = 0 });
        Assert.True(partialErase.GetProperty("ok").GetBoolean(), partialErase.ToString());
        Assert.Equal(1, partialErase.GetProperty("result").GetProperty("changed").GetInt32());
        Assert.Null(world.ObjectById(winId));

        var undo = server.HandleJson("undo", new { });
        Assert.True(undo.GetProperty("ok").GetBoolean(), undo.ToString());
        var restoredLead = world.WalkOrder
            .FirstOrDefault(o => o.Definition[(int)ObjdField.Type] == 8 && unchecked((uint)(o.TypeRecord?.Key ?? 0)) == winPartGuid);
        Assert.NotNull(restoredLead);
        var restoredMembers = world.MultiTileMembers(restoredLead!);
        Assert.Equal(expectedMemberCount, restoredMembers.Count);
        Assert.Equal(winXs, restoredMembers.Select(m => m.Position.X >> 4).Distinct().OrderBy(x => x).ToArray());

        var saveAfterUndo = server.HandleJson("save", new { slot = "m5-undo-restored-portal", lot = true });
        Assert.True(saveAfterUndo.GetProperty("ok").GetBoolean(), saveAfterUndo.ToString());

        using (var reloadedAfterUndo = E2EHarness.StartServer(profile, house: 5))
        {
            var loadUndo = reloadedAfterUndo.HandleJson("load", new { slot = "m5-undo-restored-portal" });
            Assert.True(loadUndo.GetProperty("ok").GetBoolean(), loadUndo.ToString());
            var reloadedLead = reloadedAfterUndo.World!.WalkOrder
                .FirstOrDefault(o => o.Definition[(int)ObjdField.Type] == 8 && unchecked((uint)(o.TypeRecord?.Key ?? 0)) == winPartGuid);
            Assert.NotNull(reloadedLead);
            var reloadedMembers = reloadedAfterUndo.World!.MultiTileMembers(reloadedLead!);
            Assert.Equal(expectedMemberCount, reloadedMembers.Count);
            var restoredYs = restoredMembers.Select(m => m.Position.Y >> 4).Distinct().OrderBy(y => y).ToArray();
            Assert.Equal(winXs, reloadedMembers.Select(m => m.Position.X >> 4).Distinct().OrderBy(x => x).ToArray());
            Assert.Equal(restoredYs, reloadedMembers.Select(m => m.Position.Y >> 4).Distinct().OrderBy(y => y).ToArray());
            Assert.All(reloadedMembers, m =>
            {
                Assert.Contains(m.Position.Y >> 4, restoredYs);
                Assert.Equal(ObjectClass.Portal, m.RuntimeClass);
                Assert.NotNull(m.SavedMultiTileState?.PortalValues);
            });
        }

        var redo = server.HandleJson("redo", new { });
        Assert.True(redo.GetProperty("ok").GetBoolean(), redo.ToString());
        Assert.DoesNotContain(world.WalkOrder, o => o.Definition[(int)ObjdField.Type] == 8 && unchecked((uint)(o.TypeRecord?.Key ?? 0)) == winPartGuid);

        var saveAfterRedo = server.HandleJson("save", new { slot = "m5-redo-portal-demolished", lot = true });
        Assert.True(saveAfterRedo.GetProperty("ok").GetBoolean(), saveAfterRedo.ToString());

        using var reloadedAfterRedo = E2EHarness.StartServer(profile, house: 5);
        var loadRedo = reloadedAfterRedo.HandleJson("load", new { slot = "m5-redo-portal-demolished" });
        Assert.True(loadRedo.GetProperty("ok").GetBoolean(), loadRedo.ToString());
        Assert.DoesNotContain(reloadedAfterRedo.World!.WalkOrder,
            o => o.Definition[(int)ObjdField.Type] == 8 && unchecked((uint)(o.TypeRecord?.Key ?? 0)) == winPartGuid);
        var redoLotPath = Path.Combine(profile.Profile, "native-saves", Neighborhoods.StorageSlot("m5-redo-portal-demolished", profile.NeighborhoodNumber) + ".iff");
        var redoWalls = LotWalls.Read(IffFile.Open(redoLotPath), 1);
        Assert.Equal(0, redoWalls[winX0, wy][0] & 2);
        foreach (var sx in survivingXs)
        {
            Assert.NotEqual(0, redoWalls[sx, wy][0] & 2);
            Assert.Equal(1, redoWalls[sx, wy][3]);
        }
    }

    [Fact]
    public void WriteSavedLotObjectsTo_PortalWithNullPortalValues_NormalizesToEmptyArrayAndRoundTrips()
    {
        static byte[] PortalDef(uint guid, ushort subIndex)
        {
            var fields = new ushort[XObject.DefinitionCount];
            fields[(int)ObjdField.Guid1] = unchecked((ushort)guid);
            fields[(int)ObjdField.Guid2] = (ushort)(guid >> 16);
            fields[(int)ObjdField.Type] = 8;
            fields[(int)ObjdField.MasterId] = 29;
            fields[(int)ObjdField.SubIndex] = subIndex;
            fields[(int)ObjdField.NumAttributes] = 2;
            var builder = new ChunkBuilder();
            foreach (var field in fields) builder.U16(field);
            return builder.ToArray();
        }

        var game = Directory.CreateTempSubdirectory("m5-portal-null-values").FullName;
        try
        {
            var objectsDir = Directory.CreateDirectory(Path.Combine(game, "Objects")).FullName;
            var behavior = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(1)
                .U16(0).U8(254).U8(255).Raw(new byte[8]).ToArray();
            File.WriteAllBytes(Path.Combine(objectsDir, "portal.iff"), ChunkBuilder.File(
                ("OBJD", 1, PortalDef(0xCAFE0001u, ushort.MaxValue)),
                ("OBJD", 2, PortalDef(0xCAFE0002u, 0x0000)),
                ("OBJD", 3, PortalDef(0xCAFE0003u, 0x0100)),
                ("BHAV", 4096, behavior)));

            var objects = PersistWriter.Start(false, 0x49, "MjbO");
            objects.WriteCodingFlag(false);
            objects.Int16(0); objects.BeginBlock(); objects.Int16(0); objects.Int32(0); objects.String("");
            var types = PersistWriter.Start(false, 3, "tjbo"); types.Int32(0);
            var lot = new IffFile(ChunkBuilder.File(("ObjM", 1, objects.ToArray()), ("objt", 0, types.ToArray())), "portal-test.iff");

            var registry = ObjectRegistry.Scan(game);
            var library = GlobalLibrary.Open(game);
            var world = SavedLotObjectLoader.Load(lot, registry, library);

            var group = world.ObjectGroupFactory!(unchecked((int)0xCAFE0001u));
            Assert.NotNull(group);
            Assert.Equal(2, group!.Count);
            Assert.All(group, part =>
            {
                Assert.Equal(ObjectClass.Portal, part.RuntimeClass);
                Assert.NotNull(part.SavedMultiTileState?.PortalValues);
            });

            // Also verify WriteSavedLotObjectsTo defense-in-depth when a live portal has PortalValues == null.
            group[1].SavedMultiTileState = group[1].SavedMultiTileState! with { PortalValues = null };

            var (saved, written) = world.WriteSavedLotObjectsTo(lot);
            Assert.Equal(2, written);

            var reloaded = SavedLotObjectLoader.Load(saved, registry, library);
            var reloadedLead = reloaded.ObjectById(group[0].Id);
            var reloadedPart = reloaded.ObjectById(group[1].Id);
            Assert.NotNull(reloadedLead);
            Assert.NotNull(reloadedPart);
            Assert.Equal(ObjectClass.Portal, reloadedLead!.RuntimeClass);
            Assert.Equal(ObjectClass.Portal, reloadedPart!.RuntimeClass);
            Assert.Empty(reloadedLead.SavedMultiTileState!.PortalValues!);
            Assert.Empty(reloadedPart.SavedMultiTileState!.PortalValues!);
            Assert.Equal(reloadedLead.Id, reloadedPart.MultiTileMasterId);
            Assert.Equal(reloadedPart.Id, reloadedLead.MultiTileNextId);
        }
        finally
        {
            Directory.Delete(game, recursive: true);
        }
    }
}
