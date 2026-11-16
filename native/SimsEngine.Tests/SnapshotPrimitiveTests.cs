using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using Xunit;

namespace SimsEngine.Tests;

public sealed class SnapshotPrimitiveTests
{
    private static byte[] Operand(short mode, byte flags, short index = 0) =>
        [(byte)index, (byte)(index >> 8), (byte)mode, (byte)(mode >> 8), flags, 0, 0, 0];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    private static (TreeContext Context, XPerson Me, XObject Stack) NativeSlotLot(short occupant = 0, bool wallCheck = false,
        byte wallCategory = 0, bool snapTargetBypass = false, bool blockedRoute = false)
    {
        var (context, me, _) = PrimitiveTestWorld.Create(stackObject: 5);
        var world = context.World;
        world.Host = null;
        me.TypeRecord = new TypeRecord(me.Id);
        me[ObjectData.AllowedHeightFlags] = 1;
        var records = new byte[64 * 64 * 8];
        for (var i = 0; i < 64 * 64; i++)
        {
            var wall = wallCheck && (i == 10 * 64 + 20 || i == 20 * 64 + 10) ? (byte)1 : (byte)0;
            records[i * 8] = wall;
            records[i * 8 + 1] = 0x0f;
            records[i * 8 + 2] = wallCategory;
            records[i * 8 + 3] = wallCategory;
        }
        if (blockedRoute)
        {
            for (var y = 0; y < 64; y++)
            {
                records[(10 * 64 + y) * 8] |= 0x04;
                records[(11 * 64 + y) * 8] |= 0x01;
                records[(10 * 64 + y) * 8 + 3] = 2;
                records[(11 * 64 + y) * 8 + 3] = 2;
                if (y + 1 < 64) records[(10 * 64 + y + 1) * 8 + 3] = 2;
            }
        }
        var walls = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(8).U32(0).Raw(records);
        var lot = new IffFile(ChunkBuilder.File(("Arry", 2, walls.ToArray())), "snapshot.iff");
        world.LotRoomMaps[1] = RoomMap.Build(lot, 1, 64, 64);

        var stack = world.Add(new XObject(5, PrimitiveTestWorld.Definition()));
        world.SetObjectPosition(stack, new ObjectPosition(160, 320, 1));
        stack.Slots.AddRange([0, occupant]);
        stack.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1, OccupantObjectId = occupant });
        stack.RoutingSlots.Add(new RoutingSlotRequest { SnapTargetSlot = 0, Flags = snapTargetBypass ? 0x800u : 0 });
        world.SetObjectPosition(me, new ObjectPosition(200, 320, 1));
        var frame = new StackFrame(5, [0], []);
        return (context with { Frame = frame }, me, stack);
    }

    private static (TreeContext Context, XPerson Me, PrimitiveTestHost Host, XObject Stack) Lot(short direction = 0, short stackDirection = 0)
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 5);
        me[ObjectData.Direction] = direction;
        var stack = context.World.Add(new XObject(5, PrimitiveTestWorld.Definition()));
        stack.Position = new ObjectPosition(10, 20, 3);
        stack[ObjectData.Direction] = stackDirection;
        return (context, me, host, stack);
    }

    [Fact]
    public void AMissingStackObjectIsTheNoStackObjectError()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => SnapshotPrimitive.Execute(context, Operand(1, 0))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ModeOneUsesTheStackObjectsPositionAndTheRunningObjectsDirectionMask()
    {
        var (context, _, host, _) = Lot(direction: 6);
        Assert.Equal(PrimitiveResult.True, SnapshotPrimitive.Execute(context, Operand(1, 0)));
        Assert.Equal(["RunSnapshot(100,5,10,20,3,0,False,6,False,null)"], host.Calls);
    }

    [Fact]
    public void ModeTwoAddsTheFootprintOffsetOfTheStackObjectsDirection()
    {
        var (context, _, host, _) = Lot(stackDirection: 2);
        Assert.Equal(PrimitiveResult.True, SnapshotPrimitive.Execute(context, Operand(2, 0)));
        Assert.Equal(["RunSnapshot(100,null,26,20,3,-1,False,0,False,null)"], host.Calls);
    }

    [Fact]
    public void ModeTwoPreservesTheRunningObjectsDirectionIntoTheDifference()
    {
        var (context, me, host, _) = Lot(direction: 6, stackDirection: 2);
        me.Position = new ObjectPosition(30, 40, 0);

        Assert.Equal(PrimitiveResult.True, SnapshotPrimitive.Execute(context, Operand(2, 1)));

        Assert.Equal(["RunSnapshot(100,null,26,20,3,-1,False,6,False,20,4,0)"], host.Calls);
    }

    [Fact]
    public void FlagsPassTheDifferenceFromTheRunningObjectAndTheSlotFlag()
    {
        var (context, me, host, _) = Lot();
        me.Position = new ObjectPosition(30, 40, 0);
        Assert.Equal(PrimitiveResult.True, SnapshotPrimitive.Execute(context, Operand(1, 0b101)));
        Assert.Equal(["RunSnapshot(100,5,10,20,3,0,False,0,True,20,20,0)"], host.Calls);
    }

    [Fact]
    public void TheHostsAnswerIsThePrimitivesAnswer()
    {
        var (context, _, host, _) = Lot();
        host.SnapshotAnswer = PrimitiveResult.Stop;
        Assert.Equal(PrimitiveResult.Stop, SnapshotPrimitive.Execute(context, Operand(1, 0)));
    }

    [Fact]
    public void ModeZeroChecksParameterIndexBeforeTheSlotList()
    {
        var (context, _, host, _) = Lot();
        Assert.Equal(SimulationError.StackNumberOutOfRange, Fails(() => SnapshotPrimitive.Execute(context, Operand(0, 0))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ModeZeroChecksTheParameterValueAgainstTheStackObjectsSlotList()
    {
        var (context, _, host, _) = Lot();
        context = context with { Frame = new StackFrame(5, [1], []) };

        Assert.Equal(SimulationError.SlotNumberOutOfRange, Fails(() => SnapshotPrimitive.Execute(context, Operand(0, 0))));
        Assert.Empty(host.Calls);
        Assert.Empty(host.Routes);
    }

    [Fact]
    public void ModeThreeChecksItsDirectSlotIndex()
    {
        var (context, _, host, _) = Lot();
        Assert.Equal(SimulationError.SlotNumberOutOfRange, Fails(() => SnapshotPrimitive.Execute(context, Operand(3, 0, -1))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ModeFourChecksItsDirectLotSlotIndex()
    {
        var (context, _, host, _) = Lot();
        Assert.Equal(SimulationError.SlotNumberOutOfRange, Fails(() => SnapshotPrimitive.Execute(context, Operand(4, 0, -1))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ModeFourReadsTheLotSlotCountForNonnegativeIndices()
    {
        var (context, _, host, _) = Lot();
        Assert.Equal(SimulationError.SlotNumberOutOfRange, Fails(() => SnapshotPrimitive.Execute(context, Operand(4, 0))));
        Assert.Equal(["LotRoutingSlotCount(5)"], host.Calls);
    }

    [Fact]
    public void ModeZeroProjectsAParameterSelectedFreeSlotAroundTheStackObject()
    {
        var (context, me, host, stack) = Lot(stackDirection: 2);
        context = context with { Frame = new StackFrame(5, [0], []) };
        me[ObjectData.Direction] = 4;
        stack.RoutingSlots.Add(new RoutingSlotRequest { X = 16 });

        Assert.Equal(PrimitiveResult.True, SnapshotPrimitive.Execute(context, Operand(0, 0)));

        Assert.Equal(["RunSnapshot(100,null,10,36,3,-1,False,4,False,null)"], host.Calls);
    }

    [Fact]
    public void ModeThreeUsesTheFirstPermittedDirectionRelativeToTheStackObject()
    {
        var (context, _, host, stack) = Lot(stackDirection: 1);
        stack.RoutingSlots.Add(new RoutingSlotRequest { X = 16, Flags = 0x1008 });

        Assert.Equal(PrimitiveResult.True, SnapshotPrimitive.Execute(context, Operand(3, 0)));

        Assert.Equal(["RunSnapshot(100,null,21,31,3,-1,False,4,False,null)"], host.Calls);
    }

    [Fact]
    public void ModeThreeDifferenceIncludesTheChosenSlotDirection()
    {
        var (context, me, host, stack) = Lot(direction: 2, stackDirection: 1);
        me.Position = new ObjectPosition(50, 60, 0);
        stack.RoutingSlots.Add(new RoutingSlotRequest { X = 16, Flags = 0x1008 });

        SnapshotPrimitive.Execute(context, Operand(3, 1));

        Assert.Equal("RunSnapshot(100,null,21,31,3,-1,False,4,False,29,29,-2)", host.Calls[^1]);
    }

    [Fact]
    public void ModeFourProjectsTheLotSlotAtTheStackObjectsPosition()
    {
        var (context, _, host, stack) = Lot(stackDirection: 0);
        host.LotRoutingSlots.Add(new RoutingSlotRequest { X = 16, Y = 8 });

        Assert.Equal(PrimitiveResult.True, SnapshotPrimitive.Execute(context, Operand(4, 0)));

        Assert.Equal([
            "LotRoutingSlotCount(5)",
            "LotRoutingSlotAt(5,0)",
            "RunSnapshot(100,null,26,28,3,-1,False,0,False,null)",
        ], host.Calls);
    }

    [Fact]
    public void SnapTargetIndexAndSourceFlagArePassedToTheSearchHost()
    {
        var (context, _, host, stack) = Lot();
        stack.RoutingSlots.Add(new RoutingSlotRequest { Flags = 0x1804, SnapTargetSlot = 2 });

        SnapshotPrimitive.Execute(context, Operand(3, 0));

        Assert.Equal("RunSnapshot(100,5,10,20,3,2,True,2,False,null)", host.Calls[^1]);
    }

    [Fact]
    public void NativeSnapshotPlacesTheActorIntoTheSelectedFootprintSlot()
    {
        var (context, me, stack) = NativeSlotLot();

        Assert.Equal(PrimitiveResult.True, SnapshotPrimitive.Execute(context, Operand(0, 0)));

        Assert.Equal(stack.Position, me.Position);
        Assert.Equal((short)100, stack.Slots[1]);
        Assert.Equal((short)100, stack.FootprintSlots[0].OccupantObjectId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void SuccessfulNativeSnapshotClearsPrimitiveResult(int mode)
    {
        var (context, me, _) = NativeSlotLot();
        if (mode == 4)
            context.World.LotRoutingSlots.Add(new RoutingSlotRequest { X = 16, Y = 8, SnapTargetSlot = -1 });
        context = context with { Frame = new StackFrame(5, mode == 0 ? [0] : [], []) };
        me[ObjectData.PrimitiveResult] = 9;

        Assert.Equal(PrimitiveResult.True, SnapshotPrimitive.Execute(context, Operand((short)mode, 0)));

        Assert.Equal((short)0, me[ObjectData.PrimitiveResult]);
    }

    [Fact]
    public void NativeSnapshotRejectsAnOccupiedFootprintSlotWithoutMovingTheActor()
    {
        var (context, me, stack) = NativeSlotLot(101);
        var initial = me.Position;

        Assert.Equal(PrimitiveResult.False, SnapshotPrimitive.Execute(context, Operand(0, 0)));

        Assert.Equal(initial, me.Position);
        Assert.Equal((short)101, stack.Slots[1]);
        Assert.Equal((short)101, stack.FootprintSlots[0].OccupantObjectId);
    }

    [Fact]
    public void NativeSnapshotOperandFlagChecksSlotWallCollisionBeforeMovingTheActor()
    {
        var (context, me, stack) = NativeSlotLot(wallCheck: true, snapTargetBypass: true);
        me.TypeRecord = new TypeRecord(1);
        var initialPosition = me.Position;
        var initialDirection = me[ObjectData.Direction];

        Assert.Equal(PrimitiveResult.False, SnapshotPrimitive.Execute(context, Operand(0, 4)));

        Assert.Equal(initialPosition, me.Position);
        Assert.Equal(initialDirection, me[ObjectData.Direction]);
        Assert.Equal((short)0, stack.Slots[1]);
    }

    [Fact]
    public void NativeSnapshotSkipsFootprintWallCollisionWhenOperandFlagIsClear()
    {
        var (context, me, stack) = NativeSlotLot(wallCheck: true, snapTargetBypass: true);
        me.TypeRecord = new TypeRecord(1);

        Assert.Equal(PrimitiveResult.True, SnapshotPrimitive.Execute(context, Operand(0, 0)));
        Assert.Equal(stack.Position, me.Position);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(15)]
    [InlineData(23)]
    public void NativeSnapshotAllowsSourceExemptWallCategories(byte category)
    {
        var (context, me, stack) = NativeSlotLot(wallCheck: true, wallCategory: category, snapTargetBypass: true);
        me.TypeRecord = new TypeRecord(1);

        Assert.Equal(PrimitiveResult.True, SnapshotPrimitive.Execute(context, Operand(0, 4)));
        Assert.Equal(stack.Position, me.Position);
    }

    [Fact]
    public void NativeSnapshotSlotFlag0800SkipsTheEarlyRouteWallGate()
    {
        var (context, me, stack) = NativeSlotLot(blockedRoute: true, snapTargetBypass: true);
        me.TypeRecord = new TypeRecord(1);

        Assert.Equal(PrimitiveResult.True, SnapshotPrimitive.Execute(context, Operand(0, 0)));
        Assert.Equal(stack.Position, me.Position);
    }

    [Fact]
    public void NativeSnapshotRejectsBlockedRouteToProjectedSlotWhenSlotFlagIsClear()
    {
        var (context, me, _) = NativeSlotLot(blockedRoute: true);
        var map = context.World.LotRoomMaps[1];
        Assert.True(context.World.BlocksRouteDirectionAt(map, 10, 20, 2));
        me.TypeRecord = new TypeRecord(1);
        var initialPosition = me.Position;

        Assert.Equal(PrimitiveResult.False, SnapshotPrimitive.Execute(context, Operand(0, 0)));

        Assert.Equal(initialPosition, me.Position);
    }

    [Fact]
    public void NativeSnapshotRequiresTheProjectedRoomToMatchTheActorsCurrentRoom()
    {
        var (context, me, _) = NativeSlotLot();
        me[ObjectData.Room] = 1;
        var initialPosition = me.Position;

        Assert.Equal(PrimitiveResult.False, SnapshotPrimitive.Execute(context, Operand(0, 0)));

        Assert.Equal(initialPosition, me.Position);
    }

    [Fact]
    public void NativeSnapshotModeOnePlacesTheActorAtTheStackObjectsPosition()
    {
        var (context, me, stack) = NativeSlotLot();
        var frame = new StackFrame(5, [], []);
        context = context with { Frame = frame };

        Assert.Equal(PrimitiveResult.True, SnapshotPrimitive.Execute(context, Operand(1, 0)));

        Assert.Equal(stack.Position, me.Position);
        Assert.Equal([0, me.ClockManagerIndex], stack.Slots);
    }

    [Fact]
    public void NativeSnapshotModeOneRejectsAnUnrelatedOccupant()
    {
        var (context, me, stack) = NativeSlotLot();
        var blocker = context.World.Add(new XObject(6, PrimitiveTestWorld.Definition()));
        stack.FootprintSlots[0].OccupantObjectId = blocker.ClockManagerIndex;
        var initial = me.Position;
        context = context with { Frame = new StackFrame(5, [], []) };

        Assert.Equal(PrimitiveResult.False, SnapshotPrimitive.Execute(context, Operand(1, 0)));

        Assert.Equal(initial, me.Position);
    }

    [Fact]
    public void NativeSnapshotModeTwoPlacesTheActorAtTheAnchorFootprintOffset()
    {
        var (context, me, stack) = NativeSlotLot();
        context = context with { Frame = new StackFrame(5, [], []) };
        var offset = LotGeometry.FootprintOffset(stack[ObjectData.Direction]);
        var expected = stack.Position with { X = stack.Position.X + offset.X, Y = stack.Position.Y + offset.Y };

        var dbg = SnapshotPrimitive.Execute(context, Operand(2, 0));
        Assert.True(dbg == PrimitiveResult.True, $"code={context.World.LastPlacementFailureCode} me={me.Position} room={me[ObjectData.Room]} expected={expected} roomAt={context.World.LotRoomMaps[expected.Height].RoomAtPosition(expected.X, expected.Y)}");

        Assert.Equal(expected, me.Position);
    }

    [Fact]
    public void NativeSnapshotModeTwoRejectsAnOccupiedFrontPosition()
    {
        var (context, me, stack) = NativeSlotLot();
        context = context with { Frame = new StackFrame(5, [], []) };
        var offset = LotGeometry.FootprintOffset(stack[ObjectData.Direction]);
        var blocker = context.World.Add(new XObject(6, PrimitiveTestWorld.Definition()));
        context.World.PlaceObject(blocker,
            stack.Position with { X = stack.Position.X + offset.X, Y = stack.Position.Y + offset.Y });
        var initial = me.Position;

        Assert.Equal(PrimitiveResult.False, SnapshotPrimitive.Execute(context, Operand(2, 0)));

        Assert.Equal(initial, me.Position);
    }

    [Fact]
    public void NativeSnapshotModeFourUsesTheRestoredLotRoutingSlotTable()
    {
        var (context, me, stack) = NativeSlotLot();
        var slot = new RoutingSlotRequest { X = 16, Y = 8, SnapTargetSlot = -1 };
        context.World.LotRoutingSlots.Add(slot);
        context = context with { Frame = new StackFrame(5, [], []) };
        var expected = LotGeometry.SlotPosition(stack.Position, stack[ObjectData.Direction], slot.X, slot.Y);

        Assert.Equal(PrimitiveResult.True, SnapshotPrimitive.Execute(context, Operand(4, 0, index: 0)));

        Assert.Equal(expected, me.Position);
    }

    [Fact]
    public void NativeSnapshotModeThreePlacesAtAFreeObjectSlotPosition()
    {
        var (context, me, stack) = NativeSlotLot();
        var slot = new RoutingSlotRequest { X = 16, Y = 8, SnapTargetSlot = -1 };
        stack.RoutingSlots.Add(slot);
        var expected = LotGeometry.SlotPosition(stack.Position, stack[ObjectData.Direction], slot.X, slot.Y);

        Assert.Equal(PrimitiveResult.True, SnapshotPrimitive.Execute(context, Operand(3, 0, index: 1)));

        Assert.Equal(expected, me.Position);
    }

    [Fact]
    public void NativeSnapshotFreeLotSlotFlag0800BypassesTheEarlyRouteWallGate()
    {
        var (context, me, _) = NativeSlotLot(blockedRoute: true);
        var slot = new RoutingSlotRequest { X = 0, Y = 16, SnapTargetSlot = -1, Flags = 0x800 };
        context.World.LotRoutingSlots.Add(slot);
        var expected = LotGeometry.SlotPosition(context.StackObject!.Position,
            context.StackObject[ObjectData.Direction], slot.X, slot.Y);

        Assert.Equal(PrimitiveResult.True, SnapshotPrimitive.Execute(context, Operand(4, 0)));

        Assert.Equal(expected, me.Position);
    }

    [Fact]
    public void AnyOtherModeIsTheInvalidSnappingModeError()
    {
        var (context, _, host, _) = Lot();
        Assert.Equal(SimulationError.InvalidSnappingModeSpecified, Fails(() => SnapshotPrimitive.Execute(context, Operand(9, 0))));
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public void OutOfRangeSnapTargetSlotInModesZeroThreeAndFourIsInvalidSlotNumberError(short mode)
    {
        var (context, _, stack) = NativeSlotLot();
        var invalidSnapSlot = new RoutingSlotRequest { SnapTargetSlot = 5 };
        stack.RoutingSlots.Add(invalidSnapSlot);
        context.World.LotRoutingSlots.Add(invalidSnapSlot);
        short operandIndex = mode == 0 ? (short)0 : mode == 3 ? (short)1 : (short)0;
        if (mode == 0) context.Frame.Parameters[0] = 1;

        Assert.Equal(SimulationError.BadTargetSlotSpecifiedInRoutingSlot,
            Fails(() => SnapshotPrimitive.Execute(context, Operand(mode, 0, index: operandIndex))));
    }

    [Fact]
    public void NativeSnapshotModeOneSetsFailureCode0DWhenTargetHasNoFootprintSlots()
    {
        var (context, me, stack) = NativeSlotLot();
        stack.FootprintSlots.Clear();
        context = context with { Frame = new StackFrame(5, [], []) };

        Assert.Equal(PrimitiveResult.False, SnapshotPrimitive.Execute(context, Operand(1, 0)));
        Assert.Equal(0x0d, context.World.LastPlacementFailureCode);
        Assert.Equal((short)0, me[ObjectData.PrimitiveResult]);
    }

    [Fact]
    public void NativeSnapshotFootprintSlotOffLotTargetSetsFailureCodeOneAndResultTwo()
    {
        var (context, me, stack) = NativeSlotLot();
        stack.Position = new ObjectPosition(0, 16, 1);

        Assert.Equal(PrimitiveResult.False, SnapshotPrimitive.Execute(context, Operand(0, 0)));
        Assert.Equal(1, context.World.LastPlacementFailureCode);
        Assert.Equal((short)2, me[ObjectData.PrimitiveResult]);
    }

    [Fact]
    public void NativeSnapshotSkipsDirectionalWallGateWhenActorStartsOffLot()
    {
        var (context, me, stack) = NativeSlotLot(blockedRoute: true);
        me.Position = new ObjectPosition(-16, -16, 1);
        me[ObjectData.Room] = 0;

        Assert.Equal(PrimitiveResult.True, SnapshotPrimitive.Execute(context, Operand(0, 0)));
        Assert.Equal(stack.Position, me.Position);
    }

    [Fact]
    public void NativeSnapshotWallFootprintFailureSetsFailureCodeSevenAndClearsResultId()
    {
        var (context, me, _) = NativeSlotLot(wallCheck: true, snapTargetBypass: true);
        me.TypeRecord = new TypeRecord(1);
        me[ObjectData.PrimitiveResultId] = 99;

        Assert.Equal(PrimitiveResult.False, SnapshotPrimitive.Execute(context, Operand(0, 4)));
        Assert.Equal(7, context.World.LastPlacementFailureCode);
        Assert.Equal((short)0, me[ObjectData.PrimitiveResultId]);
    }

    [Fact]
    public void NativeSnapshotSuccessUpdatesPersonAnimatorDisplacementsAndInvalidatesAnimatorRootGrid()
    {
        var (context, me, stack) = NativeSlotLot();
        stack[ObjectData.Direction] = 2;
        stack.FootprintSlots[0] = new ObjectFootprintSlot
        {
            X = 3.9f,
            Y = -2.1f,
            Height = 0f,
            HeightCode = 1,
        };
        me.AnimatorRootX = 12f;
        me.AnimatorRootY = 34f;
        me.AnimatorRootDirection = 2f;
        var initialPosition = me.Position;
        var initialDirection = me[ObjectData.Direction];

        Assert.Equal(PrimitiveResult.True, SnapshotPrimitive.Execute(context, Operand(0, 1)));

        // Direction 2 applies a 90-degree coordinate rotation that maps offset (X, Y) to (-Y, X).
        Assert.Equal(initialPosition.X - stack.Position.X + 2, me.AnimatorDisplacementX);
        Assert.Equal(initialPosition.Y - stack.Position.Y + 3, me.AnimatorDisplacementY);
        Assert.Equal(initialDirection - me[ObjectData.Direction], me.AnimatorDisplacementDirection);
        Assert.Null(me.AnimatorRootX);
        Assert.Null(me.AnimatorRootY);
        Assert.Null(me.AnimatorRootDirection);
    }
}
