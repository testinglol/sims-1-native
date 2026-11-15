using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class SetToNextPrimitiveTests
{
    // Operand: byte 4 is the mode (low seven bits) and the flag (bit 7), byte 5 the owner, byte 7 the data,
    // bytes 0 to 3 the GUID for mode 4, byte 6 the frame local for mode 9.
    private static byte[] Operand(byte mode, bool explicitTarget = false, byte owner = 0, byte data = 0, int guid = 0, byte local = 0) =>
        [(byte)guid, (byte)(guid >> 8), (byte)(guid >> 16), (byte)(guid >> 24),
            (byte)(mode | (explicitTarget ? 0x80 : 0)), owner, local, data];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    // The walk order is the person (id 100, added first by the helper), then objects 3, 1 and 2; mode 1 sees only
    // persons, while modes 0/2/6 walk the whole order.
    private static (TreeContext Context, XPerson Me) Lot(short stackObject = 0)
    {
        var (context, me, _) = PrimitiveTestWorld.Create(stackObject: stackObject);
        context.World.Add(new XObject(3, PrimitiveTestWorld.Definition()));
        context.World.Add(new XObject(1, PrimitiveTestWorld.Definition()));
        context.World.Add(new XObject(2, PrimitiveTestWorld.Definition()));
        return (context, me);
    }

    [Fact]
    public void WithoutTheFlagTheWalkMovesTheStackObject()
    {
        var (context, me) = Lot();
        // Stack object 0 is "no object yet", so the walk starts at the first object (the person).
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(1)));
        Assert.Equal(me.Id, context.Frame.StackObject);
    }

    [Fact]
    public void ObjectWalkDoesNotRequireUnrelatedHostServices()
    {
        var (context, me) = Lot();
        context.World.Host = null;

        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(1)));

        Assert.Equal(me.Id, context.Frame.StackObject);
    }

    [Fact]
    public void PersonWalkSkipsObjectsAndEndsAfterTheLastPerson()
    {
        var (context, me) = Lot();
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(1)));
        Assert.Equal(me.Id, context.Frame.StackObject);
        // Objects 3, 1 and 2 are not on the person walk; it ends and writes 0.
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(1)));
        Assert.Equal(0, context.Frame.StackObject);
        // From 0 the walk starts again at the first person.
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(1)));
        Assert.Equal(me.Id, context.Frame.StackObject);
    }

    [Fact]
    public void UnknownIdEndsTheWalk()
    {
        var (context, _) = Lot(stackObject: 9);
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(1)));
        Assert.Equal(0, context.Frame.StackObject);
    }

    [Fact]
    public void PersonWalkUsesTheListOrderAndANonPersonTargetEndsIt()
    {
        var (context, me) = Lot();
        var second = context.World.Add(new XPerson(4, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(1)));
        Assert.Equal(me.Id, context.Frame.StackObject);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(1)));
        Assert.Equal(second.Id, context.Frame.StackObject);

        // A target that is not on the person walk (objects 1 and 3 are not persons) writes 0 and answers false.
        context.Frame.StackObject = 3;
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(1)));
        Assert.Equal(0, context.Frame.StackObject);
    }

    [Fact]
    public void FlagTakesTheVariableNamedByTheOperand()
    {
        var (context, me) = Lot();
        var second = context.World.Add(new XPerson(4, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        // Temporary 2 (owner 8, data 2) holds the first person, which the walk advances past.
        me.Temporaries[2] = me.Id;
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(1, explicitTarget: true, owner: 8, data: 2)));
        Assert.Equal(second.Id, me.Temporaries[2]);
        Assert.Equal(0, context.Frame.StackObject);
    }

    [Fact]
    public void ReplacedObjectKeepsItsPlaceInTheWalk()
    {
        var (context, _) = Lot();
        var replacement = context.World.Add(new XObject(1, PrimitiveTestWorld.Definition()));
        var order = context.World.WalkOrder;
        Assert.Equal(4, order.Count);
        Assert.Equal(3, order[1].Id);
        Assert.Same(replacement, order[2]);
        Assert.Equal(2, order[3].Id);
    }

    [Fact]
    public void ReadOnlyOrMotiveTargetIsAnError()
    {
        var (context, _) = Lot();
        // A literal and a motive are not shorts, so the reference is refused before the mode.
        Assert.Equal(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea, Fails(() => SetToNextPrimitive.Execute(context, Operand(1, true, 7, 0))));
        Assert.Equal(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea, Fails(() => SetToNextPrimitive.Execute(context, Operand(1, true, 14, 0))));
    }

    [Fact]
    public void TargetOwnerIsReadWithItsErrors()
    {
        var (context, _) = Lot();
        Assert.Equal(SimulationError.IllegalOwnerFieldInDataReference, Fails(() => SetToNextPrimitive.Execute(context, Operand(1, true, 2, 0))));
    }

    [Fact]
    public void ReferenceErrorComesBeforeTheMode()
    {
        var (context, _) = Lot();
        // Mode 2 is not ported, but a read-only target is refused first.
        Assert.Equal(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea, Fails(() => SetToNextPrimitive.Execute(context, Operand(2, true, 7, 0))));
    }

    [Fact]
    public void Mode9_MissingTarget_TakesTheEastNeighbor()
    {
        var (context, host) = Mode9Lot(99, [5]);
        At(context.World, 5, 0x20, 0x30);
        var east = At(context.World, 7, 0x30, 0x30);
        context.World.ObjectById(5)!.RoomWord84 = 4;
        east.RoomWord84 = 4;
        host.ObjectTable[7] = east;
        context.Me.SetObjectTableEntry(east.ClockManagerIndex, east);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(9)));
        Assert.Equal(7, context.Frame.StackObject);
        Assert.DoesNotContain("LotSize", host.Calls);
    }

    [Fact]
    public void Mode9UsesTheSourceLotDimensionAtTheOuterTile()
    {
        var (context, host) = Mode9Lot(0, [5]);
        At(context.World, 5, 62 * 16, 10 * 16);
        var edge = At(context.World, 63, 63 * 16, 10 * 16);
        context.World.ObjectById(5)!.RoomWord84 = 4;
        edge.RoomWord84 = 4;
        context.Me.SetObjectTableEntry(edge.ClockManagerIndex, edge);

        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(9)));

        Assert.Equal((short)63, context.Frame.StackObject);
        Assert.DoesNotContain("LotSize", host.Calls);
    }

    [Fact]
    public void Mode9_FollowsTheDeltasNeighborOrder()
    {
        // Target tile (5, 3) against base (2, 3): the machine tries (0, +1), then (-1, 0), then (0, -1).
        var (context, host) = Mode9Lot(9, [5]);
        At(context.World, 5, 0x20, 0x30);
        At(context.World, 9, 0x50, 0x30);
        var match = At(context.World, 13, 0x10, 0x30);
        context.World.ObjectById(5)!.RoomWord84 = 4;
        match.RoomWord84 = 4;
        host.ObjectTable[13] = match;
        context.Me.SetObjectTableEntry(match.ClockManagerIndex, match);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(9)));
        Assert.Equal(13, context.Frame.StackObject);
    }

    [Fact]
    public void Mode9_MissesAnswerFalseAndKeepTheTarget()
    {
        var (context, host) = Mode9Lot(9, [5]);
        At(context.World, 5, 0x20, 0x30);
        At(context.World, 9, 0x50, 0x30);
        var stranger = At(context.World, 13, 0x10, 0x30);
        context.World.ObjectById(5)!.RoomWord84 = 4;
        stranger.RoomWord84 = 9;
        host.ObjectTable[13] = stranger;
        context.Me.SetObjectTableEntry(stranger.ClockManagerIndex, stranger);
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(9)));
        Assert.Equal(9, context.Frame.StackObject);
    }

    [Fact]
    public void Mode9UsesTheNativeObjectTableWhenHostEntryConflicts()
    {
        var (context, host) = Mode9Lot(9, [5]);
        At(context.World, 5, 0x20, 0x30);
        var nativeMatch = At(context.World, 13, 0x10, 0x30);
        var hostOnly = new XObject(91, PrimitiveTestWorld.Definition());
        context.World.ObjectById(5)!.RoomWord84 = 4;
        nativeMatch.RoomWord84 = 4;
        hostOnly.RoomWord84 = 4;
        context.Me.SetObjectTableEntry(nativeMatch.ClockManagerIndex, nativeMatch);
        host.ObjectTable[13] = hostOnly;
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(9)));
        Assert.Equal(13, context.Frame.StackObject);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("ObjectTableEntry", StringComparison.Ordinal));
    }

    [Fact]
    public void Mode9ComparesTheSourceRoomWordsWithoutAHostRoomCallback()
    {
        var (context, host) = Mode9Lot(0, [5]);
        var anchor = At(context.World, 5, 0x20, 0x30);
        var east = At(context.World, 13, 0x30, 0x30);
        anchor.RoomWord84 = 6;
        east.RoomWord84 = 6;
        context.Me.SetObjectTableEntry(east.ClockManagerIndex, east);

        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(9)));

        Assert.Equal((short)13, context.Frame.StackObject);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Mode9_OnTheTargetsOwnTile_AnswersFalseAtOnce()
    {
        var (context, host) = Mode9Lot(9, [5]);
        At(context.World, 5, 0x20, 0x30);
        At(context.World, 9, 0x20, 0x30);
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(9)));
        Assert.Equal(9, context.Frame.StackObject);
    }

    [Fact]
    public void Mode9_BadLocalIndex_RaisesError8()
    {
        var (context, _) = Mode9Lot(0, [5]);
        Assert.Equal(SimulationError.StackNumberOutOfRange, Fails(() => SetToNextPrimitive.Execute(context, Operand(9, local: 5))));
    }

    [Fact]
    public void Mode9_MissingLocalObject_RaisesError10()
    {
        var (context, _) = Mode9Lot(0, [77]);
        Assert.Equal(SimulationError.ReferenceToTreeParameterWhenNoParameterExists, Fails(() => SetToNextPrimitive.Execute(context, Operand(9))));
    }

    private static (TreeContext Context, PrimitiveTestHost Host) HostLot(short stackObject = 0)
    {
        var host = new PrimitiveTestHost();
        var world = new SimulationWorld { Host = host };
        var me = world.Add(new XPerson(100, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        var frame = new StackFrame(stackObject, [], [0, 0]);
        return (new TreeContext(world, me, frame), host);
    }

    private static (TreeContext Context, PrimitiveTestHost Host) Mode9Lot(short stackObject, short[] locals)
    {
        var host = new PrimitiveTestHost();
        var world = new SimulationWorld { Host = host };
        var me = world.Add(new XPerson(100, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        var frame = new StackFrame(stackObject, [], locals);
        return (new TreeContext(world, me, frame), host);
    }

    private static XObject At(SimulationWorld world, short id, int x, int y, int level = 1)
    {
        return world.Add(new XObject(id, PrimitiveTestWorld.Definition()) { Position = new ObjectPosition(x, y, level) });
    }

    [Fact]
    public void Mode3_FollowsNextLinkBeforeMasterLink()
    {
        var (context, _) = HostLot(stackObject: 5);
        var definition = PrimitiveTestWorld.Definition();
        definition[(int)SimsEngine.Formats.ObjdField.MasterId] = 1;
        context.World.Add(new XObject(5, definition) { MultiTileNextId = 6, MultiTileMasterId = 7 });
        context.World.Add(new XObject(6, PrimitiveTestWorld.Definition()));
        context.World.Add(new XObject(7, PrimitiveTestWorld.Definition()));
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(3)));
        Assert.Equal(6, context.Frame.StackObject);
    }

    [Fact]
    public void Mode3_WithoutAMasterId_AnswersFalse()
    {
        var (context, _) = HostLot(stackObject: 5);
        context.World.Add(new XObject(5, PrimitiveTestWorld.Definition()));
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(3)));
    }

    [Fact]
    public void Mode3_WithoutAnyLink_AnswersFalse()
    {
        var (context, _) = HostLot(stackObject: 7);
        var definition = PrimitiveTestWorld.Definition();
        definition[(int)SimsEngine.Formats.ObjdField.MasterId] = 1;
        context.World.Add(new XObject(7, definition));
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(3)));
    }

    [Fact]
    public void Mode3_MissingTarget_AnswersFalse()
    {
        var (context, _) = HostLot();
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(3)));
    }

    [Fact]
    public void Mode3_UsesMasterLinkWhenThereIsNoNextPart()
    {
        var (context, _) = HostLot(stackObject: 5);
        var definition = PrimitiveTestWorld.Definition();
        definition[(int)SimsEngine.Formats.ObjdField.MasterId] = 1;
        context.World.Add(new XObject(5, definition) { MultiTileMasterId = 6 });
        context.World.Add(new XObject(6, PrimitiveTestWorld.Definition()));
        context.World.Host = null;

        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(3)));
        Assert.Equal(6, context.Frame.StackObject);
    }

    [Fact]
    public void Mode4_WalksTheTypesObjectsInNativeManagerOrder()
    {
        var (context, host) = HostLot();
        var first = new XObject(7, PrimitiveTestWorld.Definition());
        var second = new XObject(8, PrimitiveTestWorld.Definition());
        first.TypeRecord = new TypeRecord(0x1234);
        second.TypeRecord = new TypeRecord(0x1234);
        var otherType = new XObject(9, PrimitiveTestWorld.Definition()) { TypeRecord = new TypeRecord(0x4321) };
        context.World.Add(first);
        context.World.Add(otherType);
        context.World.Add(second);
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(4, guid: 0x1234)));
        Assert.Equal(7, context.Frame.StackObject);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(4, guid: 0x1234)));
        Assert.Equal(8, context.Frame.StackObject);
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(4, guid: 0x1234)));
        Assert.Equal(0, context.Frame.StackObject);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Mode4_MissingTypeClearsTargetAndAnswersFalse()
    {
        var (context, _) = HostLot(stackObject: 7);
        context.World.Add(new XObject(7, PrimitiveTestWorld.Definition()) { TypeRecord = new TypeRecord(0x4321) });

        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(4, guid: 0x1234)));
        Assert.Equal(0, context.Frame.StackObject);
    }

    [Fact]
    public void Mode7_WalksNeighborSlotsForTheGuid()
    {
        var (context, _) = HostLot();
        context.World.NeighborWalkOrder.Add(new NeighborState(1, []) { TypeRecordKey = 9 });
        context.World.NeighborWalkOrder.Add(null);
        context.World.NeighborWalkOrder.Add(new NeighborState(3, []) { TypeRecordKey = 0x1234 });
        context.World.NeighborWalkOrder.Add(new NeighborState(4, []) { TypeRecordKey = 0x1234 });
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(7, guid: 0x1234)));
        Assert.Equal(3, context.Frame.StackObject);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(7, guid: 0x1234)));
        Assert.Equal(4, context.Frame.StackObject);
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(7, guid: 0x1234)));
        Assert.Equal(0, context.Frame.StackObject);
    }

    [Fact]
    public void Mode7_NoMatch_AnswersFalse()
    {
        var (context, _) = HostLot(stackObject: 1);
        context.World.NeighborWalkOrder.Add(new NeighborState(1, []) { TypeRecordKey = 9 });
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(7, guid: 0x1234)));
        Assert.Equal(0, context.Frame.StackObject);
    }

    [Fact]
    public void Mode8_WalksTheTargetsTile()
    {
        var (context, host) = HostLot(stackObject: 5);
        var target = At(context.World, 5, 64, 64);
        var nextItem = new XObject(6, PrimitiveTestWorld.Definition()) { Position = new ObjectPosition(65, 65, 1) };
        nextItem[ObjectData.ParentId] = target.ClockManagerIndex;
        var next = context.World.Add(nextItem);
        target.Slots.Add(next.ClockManagerIndex);
        _ = At(context.World, 7, 0, 0, 0);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(8)));
        Assert.Equal(6, context.Frame.StackObject);
        Assert.Empty(host.Calls);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(8)));
        Assert.Equal(5, context.Frame.StackObject);
    }

    [Fact]
    public void Mode8_WithoutTarget_AnswersFalse()
    {
        var (context, host) = HostLot();
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(8)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Mode8WalksTheNativeTileWithoutASimulationHost()
    {
        var (context, _) = HostLot(stackObject: 5);
        var target = At(context.World, 5, 64, 64);
        var nextItem = new XObject(6, PrimitiveTestWorld.Definition()) { Position = new ObjectPosition(65, 65, 1) };
        nextItem[ObjectData.ParentId] = target.ClockManagerIndex;
        var next = context.World.Add(nextItem);
        target.Slots.Add(next.ClockManagerIndex);
        context.World.Host = null;

        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(8)));
        Assert.Equal(6, context.Frame.StackObject);
    }

    [Fact]
    public void Mode5_AdvancesThroughOccupiedNeighborManagerSlots()
    {
        var (context, host) = HostLot();
        context.World.NeighborWalkOrder.Add(null);
        context.World.NeighborWalkOrder.Add(new NeighborState(9, []));
        context.World.NeighborWalkOrder.Add(null);
        context.World.NeighborWalkOrder.Add(new NeighborState(5, []));
        context.World.NeighborWalkOrder.Add(new NeighborState(7, []));
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(5)));
        Assert.Equal(2, context.Frame.StackObject);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(5)));
        Assert.Equal(4, context.Frame.StackObject);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(5)));
        Assert.Equal(5, context.Frame.StackObject);
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(5)));
        Assert.Equal(0, context.Frame.StackObject);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Mode5_NegativeSlotEndsTheWalk()
    {
        var (context, _) = HostLot(stackObject: -1);
        context.World.NeighborWalkOrder.Add(new NeighborState(9, []));

        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(5)));
        Assert.Equal(0, context.Frame.StackObject);
    }

    [Fact]
    public void Mode11_WalksTheGlobalIdTable()
    {
        var (context, host) = HostLot();
        host.IdTableEntries.AddRange([3, 7, 9]);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(11)));
        Assert.Equal(3, context.Frame.StackObject);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(11)));
        Assert.Equal(7, context.Frame.StackObject);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(11)));
        Assert.Equal(9, context.Frame.StackObject);
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(11)));
        Assert.Equal(9, context.Frame.StackObject);
        Assert.Equal(["GlobalIdTable", "GlobalIdTable", "GlobalIdTable", "GlobalIdTable"], host.Calls);
    }

    [Fact]
    public void Mode10_WalksTheCareerTable()
    {
        var (context, host) = HostLot();
        host.CareerEntries.AddRange([3, 7, 9]);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(10)));
        Assert.Equal(3, context.Frame.StackObject);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(10)));
        Assert.Equal(7, context.Frame.StackObject);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(10)));
        Assert.Equal(9, context.Frame.StackObject);
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(10)));
        Assert.Equal(9, context.Frame.StackObject);
        Assert.Equal(["CareerTable", "CareerTable", "CareerTable", "CareerTable"], host.Calls);
    }

    [Fact]
    public void Mode10_MissingEntry_TakesTheFirst()
    {
        var (context, host) = HostLot(stackObject: 5);
        host.CareerEntries.AddRange([3, 7]);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(10)));
        Assert.Equal(3, context.Frame.StackObject);
        Assert.Equal(["CareerTable"], host.Calls);
    }

    [Fact]
    public void Mode10_EmptyTable_AnswersFalse()
    {
        var (context, host) = HostLot(stackObject: 5);
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(10)));
        Assert.Equal(5, context.Frame.StackObject);
        Assert.Equal(["CareerTable"], host.Calls);
    }

    [Fact]
    public void Mode10_ComparesFullIdsAndTruncatesToShort()
    {
        // Entry ids are dwords: 5 does not match 0x10005, so the missing entry takes the first,
        // and the target takes its low word.
        var (context, host) = HostLot(stackObject: 5);
        host.CareerEntries.AddRange([0x10005, 9]);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(10)));
        Assert.Equal(5, context.Frame.StackObject);
        Assert.Equal(["CareerTable"], host.Calls);
    }

    [Fact]
    public void Mode11_MissingEntry_AnswersFalse()
    {
        var (context, host) = HostLot(stackObject: 5);
        host.IdTableEntries.AddRange([3, 7]);
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(11)));
        Assert.Equal(5, context.Frame.StackObject);
        Assert.Equal(["GlobalIdTable"], host.Calls);
    }

    [Fact]
    public void Mode12_WalksToTheNextFamilyMember()
    {
        var host = new PrimitiveTestHost();
        var world = new SimulationWorld { Host = host };
        var me = world.Add(new XPerson(100, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        me[PersonData.FamilyNumber] = 1;
        var stranger = world.Add(new XPerson(3, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        stranger[PersonData.FamilyNumber] = 2;
        var kin = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        kin[PersonData.FamilyNumber] = 1;
        var context = new TreeContext(world, me, new StackFrame(100, [], [0, 0]));
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(12)));
        Assert.Equal(1, context.Frame.StackObject);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(12)));
        Assert.Equal(100, context.Frame.StackObject);
        var missing = new TreeContext(world, me, new StackFrame(9, [], [0, 0]));
        var error = Assert.Throws<SimulationException>(() => SetToNextPrimitive.Execute(missing, Operand(12)));
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, error.Error);
    }

    [Fact]
    public void Mode12_ZeroTargetAnswersFalse()
    {
        var (context, _) = HostLot();
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(12)));
    }

    [Fact]
    public void Mode12_NonPersonTargetIsError28()
    {
        var (context, _) = HostLot(stackObject: 5);
        context.World.Add(new XObject(5, PrimitiveTestWorld.Definition()));
        var error = Assert.Throws<SimulationException>(() => SetToNextPrimitive.Execute(context, Operand(12)));
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, error.Error);
    }

    [Fact]
    public void DefaultDispatch_FirstEntrySkipsPeopleAndMatchesNativeCategoryWithoutAHost()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(100, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        me.Position = new ObjectPosition(0, 0, 0);
        var itemA = world.Add(new XObject(3, PrimitiveTestWorld.Definition()));
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        var itemB = world.Add(new XObject(2, PrimitiveTestWorld.Definition()));
        itemA[ObjectData.Category] = 4;
        person[ObjectData.Category] = 6;
        itemB[ObjectData.Category] = 5;
        TreeContext MakeContext(short stack) => new(world, me, new StackFrame(stack, [], [4]));
        var first = MakeContext(0);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(first, Operand(0)));
        Assert.Equal(100, first.Frame.StackObject);
        var skip = MakeContext(0);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(skip, Operand(2)));
        Assert.Equal(3, skip.Frame.StackObject);
        var match = MakeContext(0);
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(match, Operand(6)));
        Assert.Equal(3, match.Frame.StackObject);
        var missing = MakeContext(5);
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(missing, Operand(6)));
        Assert.Equal(5, missing.Frame.StackObject);
    }

    [Fact]
    public void DefaultDispatch_UnknownModeIsError30()
    {
        var (context, _) = Lot();
        var error = Assert.Throws<SimulationException>(() => SetToNextPrimitive.Execute(context, Operand(13)));
        Assert.Equal(SimulationError.SearchTypeIsInvalid, error.Error);
    }
}
