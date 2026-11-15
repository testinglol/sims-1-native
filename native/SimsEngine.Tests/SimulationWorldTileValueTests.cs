using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using Xunit;

namespace SimsEngine.Tests;

public sealed class SimulationWorldTileValueTests
{
    private sealed class FamilyBudgetStub(SimulationWorld world, int funds) : IFamilyBudget
    {
        public int ReadFamilyBudget() => world[SimulationGlobal.SimlessBuildMode] == 1 ? 999999 : funds;
        public void AlterFamilyBudget(int expenseType, int amount) { }
    }

    private static XObject Object(short id, ObjectPosition position = default, short parent = 0)
    {
        var definition = new short[XObject.DefinitionCount];
        var item = new XObject(id, definition) { Position = position };
        item[ObjectData.AllowedHeightFlags] = 1;
        item[ObjectData.ParentId] = parent;
        return item;
    }

    [Theory]
    [InlineData(-1, 0, 1)]
    [InlineData(64, 0, 1)]
    [InlineData(0, -1, 1)]
    [InlineData(0, 64, 1)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 0, 4)]
    public void InvalidCellsAndLevelsAreEmpty(int x, int y, int level)
    {
        var world = new SimulationWorld();
        Assert.Equal(0, world.TileValueAt(x, y, level));
    }

    [Fact]
    public void TileLookupUsesTheObjectManagerKeyAndRequiresTheMatchingLevel()
    {
        var world = new SimulationWorld();
        var item = world.Add(Object(7, new ObjectPosition(0x23, 0x35, 2)));

        Assert.Equal(item.ClockManagerIndex, world.TileValueAt(2, 3, 2));
        Assert.Equal(0, world.TileValueAt(2, 3, 1));
        Assert.Equal(0, world.TileValueAt(3, 3, 2));
    }

    [Fact]
    public void ContainedObjectsDoNotWriteTheTileCellAndLaterRootsReplaceEarlierOnes()
    {
        var world = new SimulationWorld();
        var position = new ObjectPosition(0x20, 0x30, 1);
        var parent = world.Add(Object(3, position));
        var child = world.Add(Object(4, position, parent.ClockManagerIndex));
        var laterRoot = world.Add(Object(5, position));
        parent.Slots.Add(child.ClockManagerIndex);

        Assert.Equal(laterRoot.ClockManagerIndex, world.TileValueAt(2, 3, 1));

        world.Remove(laterRoot.Id);
        Assert.Equal(0, world.TileValueAt(2, 3, 1));
        Assert.Empty(world.ObjectsAtTile(2, 3, 1));
    }

    [Fact]
    public void PlacementMovesTheLiveRootAndRemovalClearsItsCell()
    {
        var world = new SimulationWorld();
        var first = new ObjectPosition(0x20, 0x30, 1);
        var second = new ObjectPosition(0x41, 0x52, 2);
        var root = world.Add(Object(8, first));

        world.PlaceObject(root, second);

        Assert.Equal(0, world.TileValueAt(2, 3, 1));
        Assert.Equal(root.ClockManagerIndex, world.TileValueAt(4, 5, 2));
        Assert.Equal([root], world.ObjectsAtTile(4, 5, 2));
        world.Remove(root.Id);
        Assert.Equal(0, world.TileValueAt(4, 5, 2));
    }

    [Fact]
    public void ThirdLevelPlacementHasNoTileRootBecauseTheSourceGridOnlyIndexesLevelsOneAndTwo()
    {
        var world = new SimulationWorld();
        var item = world.Add(Object(10, new ObjectPosition(0x20, 0x30, 3)));
        item[ObjectData.PlacementFlags] = 0x0200;
        var free = new ObjectPosition(0x40, 0x50, 3);
        world.Add(Object(11, new ObjectPosition(0x60, 0x70, 3)));

        Assert.Equal(0, world.TileValueAt(2, 3, 3));
        Assert.True(world.CanPlaceObject(item, item.Position));
        Assert.True(world.CanPlaceObject(item, new ObjectPosition(0x60, 0x70, 3)));
        Assert.True(world.CanPlaceCreatedObject(item, free));
        Assert.True(world.CanPlaceCreatedObject(item, new ObjectPosition(0x60, 0x70, 3)));
        Assert.True(world.CanPlaceObject(item, free));
        world.PlaceObject(item, free);
        Assert.Equal(0, world.TileValueAt(2, 3, 3));
        Assert.Equal(0, world.TileValueAt(4, 5, 3));
    }

    [Fact]
    public void BuildModeTypeSixPlacementSetsAndClearsItsSeparateLevelGridBit()
    {
        var world = new SimulationWorld();
        var item = Object(15, new ObjectPosition(0x20, 0x30, 1));
        item.Definition[(int)ObjdField.BuildModeType] = 6;
        world.Add(item);
        var oldLevelGrid = new byte[64 * 64];
        oldLevelGrid[2 * 64 + 3] = 0x18;
        world.BuildModeType6PlacementGrids[1] = oldLevelGrid;
        var managerGrid = new byte[64 * 64];
        managerGrid[4 * 64 + 5] = 0x01;
        world.BuildModeType6PlacementGrids[2] = managerGrid;

        world.PlaceObject(item, new ObjectPosition(0x40, 0x50, 2));
        Assert.Equal(0x10, world.BuildModeType6PlacementFlagsAt(2, 3, 1));
        Assert.Equal(0x09, world.BuildModeType6PlacementFlagsAt(4, 5, 2));

        world.PlaceObject(item, new ObjectPosition(0x60, 0x70, 2));
        Assert.Equal(0x01, world.BuildModeType6PlacementFlagsAt(4, 5, 2));
        Assert.Equal(0x08, world.BuildModeType6PlacementFlagsAt(6, 7, 2));

        world.BeginObjectRemoval(item, 1);
        Assert.Equal(0, world.BuildModeType6PlacementFlagsAt(6, 7, 2));
    }

    [Fact]
    public void PlacementEligibilityBitUsesLevelOneManagerSentinelOnlyWhenRequested()
    {
        var world = new SimulationWorld();
        var position = new ObjectPosition(0x28, 0x28, 1);
        world.LotRoomMaps[1] = RoomMapWithGroundClass(0x10);
        var item = world.Add(Object(16, position));
        item[ObjectData.PlacementFlags] = 0x0020;

        // FUN_00477a73 initializes +0x64 cells to 0xff, so FUN_004790bc rejects this gated cell.
        Assert.False(world.HasPlacementEligibilityAt(2, 2, 1));
        Assert.False(world.CanPlaceObject(item, position));

        world.SetPlacementEligibilityAt(2, 2, 0x00);
        Assert.True(world.HasPlacementEligibilityAt(2, 2, 1));
        Assert.True(world.CanPlaceObject(item, position));

        item[ObjectData.PlacementFlags] = 0;
        Assert.True(world.CanPlaceObject(item, position));
        Assert.False(world.HasPlacementEligibilityAt(2, 2, 2));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(63, 5)]
    [InlineData(5, 0)]
    [InlineData(5, 63)]
    public void BuildModeTypeSixMarkerIgnoresTheLotBorder(int tileX, int tileY)
    {
        var world = new SimulationWorld();
        var item = Object(17, new ObjectPosition(0x20, 0x30, 1));
        item.Definition[(int)ObjdField.BuildModeType] = 6;
        world.Add(item);

        world.PlaceObject(item, new ObjectPosition(tileX << 4, tileY << 4, 1));

        Assert.Equal(0, world.BuildModeType6PlacementFlagsAt(tileX, tileY, 1));
    }

    [Fact]
    public void PlaceObjectBroadcastsFlaggedNoticeAfterPositionAndRootUpdate()
    {
        var world = new SimulationWorld();
        var item = world.Add(Object(16, new ObjectPosition(0x20, 0x30, 1)));
        item[ObjectData.FlagField2] = 0x0400;
        var observed = new List<(int Message, int Value, ObjectPosition Position, int TileRoot)>();
        world.AddNoticeListener(new SimulationNoticeListener((message, value) =>
        {
            observed.Add((message, value, item.Position, world.TileValueAt(4, 5, 1)));
            return false;
        }));

        world.PlaceObject(item, new ObjectPosition(0x40, 0x50, 1));

        Assert.Equal([(0xf2, item.ClockManagerIndex, new ObjectPosition(0x40, 0x50, 1), (int)item.ClockManagerIndex)], observed);
    }

    [Fact]
    public void PlaceObjectRoomChangeNoticeObservesUpdatedTileRoots()
    {
        var wallBody = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(8).U32(0);
        for (var x = 0; x < 64; x++)
        for (var y = 0; y < 64; y++)
        {
            var wall = x switch
            {
                31 => (byte)0x04,
                32 => (byte)0x01,
                _ => (byte)0,
            };
            wallBody.Raw([wall, 0x0f, 1, 1, 0, 0, 0, 0]);
        }
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, wallBody.ToArray())), "placement-room-change.iff");
        var roomMap = RoomMap.Build(file, 1, 64, 64);
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = roomMap;
        var item = world.Add(Object(19, new ObjectPosition(20 * 16, 20 * 16, 1)));
        world.SetObjectPosition(item, item.Position);
        item.RoomChangeNoticeGateWord58 = 1;
        var observed = new List<(int Room, ObjectPosition Position, int OldRoot, int NewRoot)>();
        world.AddNoticeListener(new SimulationNoticeListener((message, room) =>
        {
            if (message == 0xf3)
                observed.Add((room, item.Position, world.TileValueAt(20, 20, 1), world.TileValueAt(40, 20, 1)));
            return false;
        }));

        world.PlaceObject(item, new ObjectPosition(40 * 16, 20 * 16, 1));

        Assert.Equal((short)0, observed[0].Room);
        Assert.Equal((short)1, observed[1].Room);
        Assert.All(observed, notice =>
        {
            Assert.Equal(new ObjectPosition(40 * 16, 20 * 16, 1), notice.Position);
            Assert.Equal(0, notice.OldRoot);
            Assert.Equal(item.ClockManagerIndex, notice.NewRoot);
        });
    }

    [Fact]
    public void MultitilePlacementMovesTheLeadAndEveryPartUsingSavedObjMOffsets()
    {
        var world = new SimulationWorld();
        var root = Object(17, new ObjectPosition(0x20, 0x30, 1));
        root.RuntimeClass = ObjectClass.MultiTileObject;
        root.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        world.Add(root);
        var part = Object(18, new ObjectPosition(0x40, 0x40, 2));
        part.RuntimeClass = ObjectClass.MultiTileObject;
        part.SavedMultiTileState = new MultiTileState(1, 2, 1, 0, 0, 0, root.Id, null);
        part.MultiTileMasterId = root.Id;
        world.Add(part);
        root.MultiTileNextId = part.Id;

        world.PlaceObject(part, new ObjectPosition(0xa0, 0x90, 2));

        Assert.Equal(new ObjectPosition(0x90, 0x70, 1), root.Position);
        Assert.Equal(new ObjectPosition(0xa0, 0x90, 2), part.Position);
        // A non-lead FUN_00421ea5 request delegates directly to the master's +0x58 group callback;
        // the root wrapper's cache writes are not executed by FUN_00422c62.
        Assert.Null(root.MultiTileMovePositionState);
        Assert.Null(part.MultiTileMovePositionState);
        Assert.Equal((short)0, root.MultiTileMoveFlag1B0);
        Assert.Equal((short)0, part.MultiTileMoveFlag1B0);
        Assert.Equal(0.0f, root.MultiTileMoveValue1C0);
        Assert.Equal(0.0f, part.MultiTileMoveValue1C0);
        Assert.Equal(0, world.TileValueAt(2, 3, 1));
        Assert.Equal(root.ClockManagerIndex, world.TileValueAt(9, 7, 1));
        Assert.Equal(part.ClockManagerIndex, world.TileValueAt(10, 9, 2));
        Assert.True(root.PositionRefreshPending);
        Assert.True(part.PositionRefreshPending);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(-2, 1)]
    [InlineData(1, -2)]
    public void RefreshingAMultiTileGroupAtItsOwnPositionDoesNotTransposeItsComponents(int x, int y)
    {
        var world = new SimulationWorld();
        var lead = Object(17, new ObjectPosition(0x88, 0x98, 1));
        lead.RuntimeClass = ObjectClass.MultiTileObject;
        lead.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        // FUN_0042339b computes +0x1a0 from the +0xf8 difference (ObjectHead.X), and +0x1a4 from
        // +0xf4 (ObjectHead.Y). An asymmetric saved footprint must be unchanged by a placement refresh.
        var original = new ObjectPosition(lead.Position.X + x * 16, lead.Position.Y + y * 16, 1);
        var part = Object(18, original);
        part.RuntimeClass = ObjectClass.MultiTileObject;
        part.MultiTileMasterId = lead.Id;
        part.SavedMultiTileState = new MultiTileState(x, y, 0, x, y, 0, lead.Id, null);
        lead.MultiTileNextId = part.Id;
        world.Add(lead);
        world.Add(part);

        world.PlaceObject(lead, lead.Position);

        Assert.Equal(new ObjectPosition(0x88, 0x98, 1), lead.Position);
        Assert.Equal(original, part.Position);
        Assert.Equal(part.ClockManagerIndex, world.TileValueAt(original.X >> 4, original.Y >> 4, 1));
    }

    [Fact]
    public void MultiTileRootRequestAppliesTheLeadOffsetOnlyOnce()
    {
        var world = new SimulationWorld();
        var root = Object(41, new ObjectPosition(0x20, 0x30, 1));
        root.RuntimeClass = ObjectClass.MultiTileObject;
        root.SavedMultiTileState = new MultiTileState(1, 2, 1, 0, 0, 0, 0, null);
        world.Add(root);
        var part = Object(42, new ObjectPosition(0x40, 0x40, 2));
        part.RuntimeClass = ObjectClass.MultiTileObject;
        part.SavedMultiTileState = new MultiTileState(3, 4, 1, 0, 0, 0, root.Id, null);
        part.MultiTileMasterId = root.Id;
        world.Add(part);
        root.MultiTileNextId = part.Id;

        var requestedRootPosition = new ObjectPosition(0xa0, 0x90, 2);
        world.PlaceObject(root, requestedRootPosition);

        Assert.Equal(requestedRootPosition, root.Position);
        Assert.Equal(new ObjectPosition(0xc0, 0xb0, 2), part.Position);
        Assert.Equal(requestedRootPosition, root.MultiTileMovePositionState);
        Assert.Equal(part.Position, part.MultiTileMovePositionState);
        Assert.Equal(root.ClockManagerIndex, world.TileValueAt(10, 9, 2));
        Assert.Equal(part.ClockManagerIndex, world.TileValueAt(12, 11, 2));
    }

    [Fact]
    public void MultiTileMoveFromAPartPreservesOffsetsAndRestoresStateWhenBlocked()
    {
        var world = new SimulationWorld();
        var root = Object(51, new ObjectPosition(0x20, 0x30, 1));
        root.RuntimeClass = ObjectClass.MultiTileObject;
        root.Definition[(int)ObjdField.MasterId] = 17;
        root[ObjectData.MovementFlags] = 2;
        root.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        world.Add(root);
        var part = Object(52, new ObjectPosition(0x30, 0x30, 1));
        part.RuntimeClass = ObjectClass.MultiTileObject;
        part.Definition[(int)ObjdField.MasterId] = 17;
        part[ObjectData.MovementFlags] = 2;
        part.SavedMultiTileState = new MultiTileState(1, 0, 0, 1, 0, 0, root.Id, null);
        part.MultiTileMasterId = root.Id;
        world.Add(part);
        root.MultiTileNextId = part.Id;

        Assert.False(world.MoveObject(part, new ObjectPosition(0, 0, 1), 2));
        Assert.Equal(new ObjectPosition(0x20, 0x30, 1), root.Position);
        Assert.Equal(new ObjectPosition(0x30, 0x30, 1), part.Position);
        Assert.Equal((short)0, root[ObjectData.Direction]);
        Assert.Equal((short)0, part[ObjectData.Direction]);
        Assert.Equal((short)1, part.SavedMultiTileState!.Value0x1A0);

        var destination = new ObjectPosition(0x50, 0x50, 1);
        Assert.True(world.MoveObject(part, destination, 0));
        Assert.Equal(new ObjectPosition(0x40, 0x50, 1), root.Position);
        Assert.Equal(destination, part.Position);
    }

    [Fact]
    public void SellingAnyMultiTilePartChecksAndRemovesTheWholeGroupWithOneRefund()
    {
        var world = new SimulationWorld();
        var root = Object(61, new ObjectPosition(0x20, 0x30, 1));
        root.RuntimeClass = ObjectClass.MultiTileObject;
        root.Definition[(int)ObjdField.MasterId] = 17;
        root[ObjectData.MovementFlags] = 10;
        root[ObjectData.CurrentValue] = 100;
        root.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        world.Add(root);
        var part = Object(62, new ObjectPosition(0x30, 0x30, 1));
        part.RuntimeClass = ObjectClass.MultiTileObject;
        part.Definition[(int)ObjdField.MasterId] = 17;
        part[ObjectData.MovementFlags] = 10;
        part[ObjectData.RepairState] = 1;
        part.SavedMultiTileState = new MultiTileState(0, 1, 0, 1, 0, 0, root.Id, null);
        part.MultiTileMasterId = root.Id;
        world.Add(part);
        root.MultiTileNextId = part.Id;
        var removalNotices = new List<int>();
        world.AddNoticeListener(new SimulationNoticeListener((message, value) =>
        {
            if (message == 0x88) removalNotices.Add(value);
            return false;
        }));

        part[ObjectData.MovementFlags] = 8;
        Assert.False(world.CanSellObject(root));
        part[ObjectData.MovementFlags] = 10;
        Assert.Equal(50, world.SellObject(part));
        Assert.Null(world.ObjectById(root.Id));
        Assert.Null(world.ObjectById(part.Id));
        Assert.Null(world.ClockManager.ObjectAt(root.ClockManagerIndex));
        Assert.Null(world.ClockManager.ObjectAt(part.ClockManagerIndex));
        Assert.Equal(new[] { (int)root.Id, part.Id }.Order(), removalNotices.Order());
    }

    [Fact]
    public void NonLeadPlacementValidationChecksTheTranslatedMasterChain()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithGroundClass(0x10);
        var root = Object(43, new ObjectPosition(0x20, 0x30, 1));
        root.RuntimeClass = ObjectClass.MultiTileObject;
        root.SavedMultiTileState = new MultiTileState(1, 2, 1, 0, 0, 0, 0, null);
        root[ObjectData.PlacementFlags] = 0x0800;
        world.Add(root);
        var part = Object(44, new ObjectPosition(0x40, 0x40, 1));
        part.RuntimeClass = ObjectClass.MultiTileObject;
        part.SavedMultiTileState = new MultiTileState(3, 4, 1, 0, 0, 0, root.Id, null);
        part.MultiTileMasterId = root.Id;
        part[ObjectData.PlacementFlags] = 0x0800;
        world.Add(part);
        root.MultiTileNextId = part.Id;

        var requestedPartPosition = new ObjectPosition(0xa0, 0x90, 1);
        Assert.True(world.CanPlaceObject(part, requestedPartPosition));
        world.Add(Object(45, new ObjectPosition(0x80, 0x70, 1)));

        Assert.False(world.CanPlaceObject(part, requestedPartPosition));
    }

    [Fact]
    public void MultiTileAssociationPointerAloneDoesNotSkipTheMovingGroup()
    {
        var world = new SimulationWorld();
        var owner = Object(19, new ObjectPosition(0x40, 0x40, 1));
        owner.RuntimeClass = ObjectClass.MultiTileObject;
        owner.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        var associated = Object(20, new ObjectPosition(0x50, 0x50, 1));
        associated.SavedMultiTileState = new MultiTileState(2, 1, 1, 0, 0, 0, 0, null);
        owner.MultiTileMoveAssociatedObject = associated;
        world.Add(owner);
        world.Add(associated);

        world.PlaceCreatedObject(owner, new ObjectPosition(0xa0, 0x90, 2));

        Assert.Equal(new ObjectPosition(0xa0, 0x90, 2), owner.Position);
        Assert.Equal((short)0, owner.MultiTileMoveFlag1B0);
        Assert.Equal(new ObjectPosition(0x50, 0x50, 1), associated.Position);
        Assert.Equal(owner.ClockManagerIndex, world.TileValueAt(10, 9, 2));
        Assert.Equal(associated.ClockManagerIndex, world.TileValueAt(5, 5, 1));
    }

    [Fact]
    public void AcceptedMultiTileAssociationRunsBeforeOwnerGroupAndRefreshesItsHelper()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithGroundClass(0x10);
        var owner = Object(22, new ObjectPosition(0x40, 0x40, 1));
        owner.RuntimeClass = ObjectClass.MultiTileObject;
        owner.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        var associated = Object(23, new ObjectPosition(0x50, 0x50, 1));
        associated.TypeRecord = new TypeRecord(23) { ExtensionFlagsCC = 1 };
        owner.MultiTileMoveAssociatedObject = associated;
        world.Add(owner);
        world.Add(associated);

        var destination = new ObjectPosition(0x18, 0x18, 1);
        Assert.True(world.CanPlaceObject(associated, destination));
        world.PlaceObject(owner, destination);

        Assert.Equal(destination, associated.Position);
        Assert.Equal(destination, owner.Position);
        Assert.Equal((short)1, owner.MultiTileMoveFlag1B0);
        Assert.Equal(destination, owner.MultiTileMovePositionState);
        Assert.Equal(destination, associated.AssociatedPlacementHelper?.Owner?.Position);
        Assert.True(associated.AssociatedPlacementHelper?.FlagD9);
        Assert.Equal(owner.ClockManagerIndex, world.TileValueAt(destination.X >> 4, destination.Y >> 4, destination.Height));
    }

    [Fact]
    public void AcceptedNonPersonAssociationUsesAndStoresItsResolvedReferenceSlot()
    {
        var world = new SimulationWorld();
        var owner = Object(60, new ObjectPosition(0x40, 0x40, 1));
        owner.RuntimeClass = ObjectClass.MultiTileObject;
        owner.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        var associated = Object(61, new ObjectPosition(0x50, 0x50, 1));
        var reference = Object(62, new ObjectPosition(0x30, 0x30, 1));
        reference[ObjectData.SupportStrength] = 10;
        reference.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
        var requested = new ObjectPosition(0xa0, 0x90, 1);
        owner.MultiTileMoveAssociatedObject = associated;
        world.Add(owner);
        world.Add(associated);
        world.Add(reference);
        owner.MultiTileMoveValue1C4 = reference.ClockManagerIndex;
        owner.MultiTileMoveValue1C6 = 0;
        owner.MultiTileMovePositionState = requested;

        world.PlaceObject(owner, requested);

        Assert.Equal(reference.Position, associated.Position);
        Assert.Equal(reference.ClockManagerIndex, associated[ObjectData.ParentId]);
        Assert.Equal((short)1, associated[ObjectData.SlotNumber]);
        Assert.Equal(associated.ClockManagerIndex, reference.Slots[1]);
        Assert.Equal(associated.ClockManagerIndex, reference.FootprintSlots[0].OccupantObjectId);
    }

    [Theory]
    [InlineData(0)] // unresolved definition slot
    [InlineData(1)] // competing occupant
    [InlineData(2)] // disallowed height
    [InlineData(3)] // insufficient support
    [InlineData(4)] // containment cycle
    public void NonPersonAssociationWithInvalidResolvedSlotDoesNotFallBackToOrdinaryPlacement(int rejection)
    {
        var world = new SimulationWorld();
        var owner = Object(63, new ObjectPosition(0x40, 0x40, 1));
        owner.RuntimeClass = ObjectClass.MultiTileObject;
        owner.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        var associated = Object(64, new ObjectPosition(0x50, 0x50, 1));
        if (rejection == 4) associated.Definition[(int)ObjdField.MasterId] = 1;
        var reference = Object(65, new ObjectPosition(0x30, 0x30, 1));
        reference[ObjectData.SupportStrength] = rejection == 3 ? (short)0 : (short)10;
        if (rejection != 0)
            reference.FootprintSlots.Add(new ObjectFootprintSlot
            {
                HeightCode = rejection == 2 ? (short)2 : (short)1,
                OccupantObjectId = rejection == 1 ? (short)99 : (short)0,
            });
        if (rejection == 3) associated[ObjectData.Weight] = 1;
        var requested = new ObjectPosition(0xa0, 0x90, 1);
        owner.MultiTileMoveAssociatedObject = associated;
        world.Add(owner);
        world.Add(associated);
        world.Add(reference);
        if (rejection == 4) reference[ObjectData.ParentId] = associated.ClockManagerIndex;
        owner.MultiTileMoveValue1C4 = reference.ClockManagerIndex;
        owner.MultiTileMoveValue1C6 = 0;
        owner.MultiTileMovePositionState = requested;

        world.PlaceObject(owner, requested);

        Assert.Equal(new ObjectPosition(0x50, 0x50, 1), associated.Position);
        Assert.Equal((short)0, associated[ObjectData.ParentId]);
        Assert.Empty(reference.Slots);
        if (rejection != 0)
            Assert.Equal(rejection == 1 ? (short)99 : (short)0, reference.FootprintSlots[0].OccupantObjectId);
    }

    [Theory]
    [InlineData((short)0, 50, 0x15, 0)]
    [InlineData((short)1, 50, 0, 1)]
    public void AssociatedMasterReportsErrorFifteenFromAlternativePriceButStillApplies(
        short mode50, int value90, int expectedError, short expectedMoveFlag)
    {
        var world = new SimulationWorld();
        world.FamilyBudget = new FamilyBudgetStub(world, value90);
        world[SimulationGlobal.SimlessBuildMode] = mode50;
        var owner = Object(46, new ObjectPosition(0x40, 0x40, 1));
        owner.RuntimeClass = ObjectClass.MultiTileObject;
        owner.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        var masterDefinition = new short[XObject.DefinitionCount];
        masterDefinition[(int)ObjdField.MasterId] = 1;
        var associated = new XObject(47, masterDefinition)
        {
            Position = new ObjectPosition(0x50, 0x50, 1),
            TypeRecord = new TypeRecord(47) { AlternativeObjectPrice = 100 },
        };
        associated[ObjectData.AllowedHeightFlags] = 1;
        owner.MultiTileMoveAssociatedObject = associated;
        world.Add(owner);
        world.Add(associated);

        var destination = new ObjectPosition(0x18, 0x18, 1);
        Assert.True(world.CanPlaceObject(associated, destination));
        world.PlaceObject(owner, destination, cursorGroup: world.CreatePlacementCursorGroup(owner));

        Assert.Equal(destination, associated.Position);
        Assert.Equal(destination, owner.Position);
        Assert.Equal(expectedMoveFlag, owner.MultiTileMoveFlag1B0);
        Assert.Equal(expectedError, world.LastPlacementFailureCode);
    }

    [Fact]
    public void AssociatedNonMasterReadsZeroFromTypeRecordAssociationValue()
    {
        var world = new SimulationWorld();
        world.FamilyBudget = new FamilyBudgetStub(world, 50);
        var owner = Object(48, new ObjectPosition(0x40, 0x40, 1));
        owner.RuntimeClass = ObjectClass.MultiTileObject;
        owner.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        var associated = Object(49, new ObjectPosition(0x50, 0x50, 1));
        associated.TypeRecord = new TypeRecord(49);
        owner.MultiTileMoveAssociatedObject = associated;
        world.Add(owner);
        world.Add(associated);

        var destination = new ObjectPosition(0x18, 0x18, 1);
        Assert.True(world.CanPlaceObject(associated, destination));
        world.PlaceObject(owner, destination, cursorGroup: world.CreatePlacementCursorGroup(owner));

        Assert.Equal(destination, associated.Position);
        Assert.Equal(destination, owner.Position);
        Assert.Equal(0, world.LastPlacementFailureCode);
        Assert.Equal((short)1, owner.MultiTileMoveFlag1B0);
    }

    [Fact]
    public void RefusedMultiTileAssociationLeavesTheAssociatedObjectAndStillMovesTheOwnerGroup()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithGroundClass(0x10);
        var destination = new ObjectPosition(0x18, 0x18, 1);
        var owner = Object(24, new ObjectPosition(0x40, 0x40, 1));
        owner.RuntimeClass = ObjectClass.MultiTileObject;
        owner.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        var associated = Object(25, new ObjectPosition(0x50, 0x50, 1));
        associated.TypeRecord = new TypeRecord(25) { ExtensionFlagsCC = 1 };
        owner.MultiTileMoveAssociatedObject = associated;
        world.Add(owner);
        world.Add(associated);
        world.Add(Object(26, destination));
        var helper = associated.AssociatedPlacementHelper;

        Assert.False(world.CanPlaceObject(associated, destination));
        world.PlaceObject(owner, destination);

        Assert.Equal(destination, owner.Position);
        Assert.Equal((short)0, owner.MultiTileMoveFlag1B0);
        Assert.Equal(new ObjectPosition(0x50, 0x50, 1), associated.Position);
        Assert.False(helper!.FlagD9);
    }

    [Fact]
    public void MultiTileAssociationUsesItsDirectClassCallbackWithoutReenteringItsWrapper()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithGroundClass(0x10);
        var owner = Object(27, new ObjectPosition(0x40, 0x40, 1));
        owner.RuntimeClass = ObjectClass.MultiTileObject;
        owner.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        var associated = Object(28, new ObjectPosition(0x50, 0x50, 1));
        associated.RuntimeClass = ObjectClass.MultiTileObject;
        associated.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        var nestedAssociation = Object(29, new ObjectPosition(0x60, 0x60, 1));
        associated.MultiTileMoveAssociatedObject = nestedAssociation;
        owner.MultiTileMoveAssociatedObject = associated;
        world.Add(owner);
        world.Add(associated);
        world.Add(nestedAssociation);

        var destination = new ObjectPosition(0x18, 0x18, 1);
        Assert.True(world.CanPlaceObject(associated, destination));
        world.PlaceObject(owner, destination);

        Assert.Equal(destination, owner.Position);
        Assert.Equal(destination, associated.Position);
        Assert.Equal(new ObjectPosition(0x60, 0x60, 1), nestedAssociation.Position);
        Assert.Equal(default, associated.MultiTileMovePositionState);
    }

    [Fact]
    public void ErrorElevenAssociationFallbackFindsCompatibleSlotAndUpdatesRelationships()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithGroundClass(0x10);
        var destination = new ObjectPosition(0x18, 0x18, 1);
        var owner = Object(30, new ObjectPosition(0x18, 0x28, 1));
        owner.RuntimeClass = ObjectClass.MultiTileObject;
        owner.SavedMultiTileState = new MultiTileState(0, 0, 0, 10, 20, 0, 0, null);
        owner[ObjectData.RotationNotches] = 1;
        world.InteractionRefreshTypeKey = unchecked((int)0xe9337020u);
        world.ActiveInteractionRefreshTarget = owner;
        world.InteractionRefreshPendingObjects.Add(owner);
        var personDefinition = new short[XObject.DefinitionCount];
        personDefinition[(int)ObjdField.Type] = XPerson.PersonType;
        var person = new XPerson(31, personDefinition)
        {
            Position = new ObjectPosition(0x50, 0x50, 1),
            TypeRecord = new TypeRecord(31) { FootprintMask = 0x1111 },
        };
        person[ObjectData.AllowedHeightFlags] = 1;
        person[ObjectData.PlacementFlags] = 1; // bypass the occupied root cell so the source footprint error 0x0b is exercised
        person[ObjectData.RotationNotches] = 1;
        person[ObjectData.Direction] = 2;
        person.MultiTileMoveValue1C4 = 44;
        person.MultiTileMoveValue1C6 = 5;
        var part = Object(36, new ObjectPosition(0x18, 0x18, 1));
        part.RuntimeClass = ObjectClass.MultiTileObject;
        part.SavedMultiTileState = new MultiTileState(-1, 0, 0, 9, 20, 0, owner.Id, null);
        part.MultiTileMasterId = owner.Id;
        owner.MultiTileNextId = part.Id;
        var slotObject = Object(32, destination);
        slotObject.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
        owner.MultiTileMoveAssociatedObject = person;
        world.Add(owner);
        world.Add(person);
        world.Add(part);
        world.Add(slotObject);

        Assert.False(world.CanPlaceObject(person, destination));
        Assert.Equal(0x0b, world.LastPlacementFailureCode);
        world.PlaceObject(owner, destination);

        Assert.Equal(destination, owner.Position);
        Assert.Equal(destination, person.Position);
        Assert.Equal(slotObject.ClockManagerIndex, person[ObjectData.ParentId]);
        Assert.Equal((short)1, person[ObjectData.SlotNumber]);
        Assert.Equal((short)44, person.MultiTileMoveValue1C4);
        Assert.Equal((short)5, person.MultiTileMoveValue1C6);
        Assert.Equal(person.ClockManagerIndex, slotObject.Slots[1]);
        Assert.Equal(person.ClockManagerIndex, slotObject.FootprintSlots[0].OccupantObjectId);
        Assert.Equal(slotObject.ClockManagerIndex, owner.MultiTileMoveValue1C4);
        Assert.Equal((short)0, owner.MultiTileMoveValue1C6);
        Assert.Equal((short)1, owner.MultiTileMoveFlag1B0);
        Assert.Equal((short)6, owner[ObjectData.Direction]);
        Assert.Equal(new ObjectPosition(0x18, 0x28, 1), part.Position);
        Assert.Equal(0, part.SavedMultiTileState?.Value0x1A0);
        Assert.Equal(1, part.SavedMultiTileState?.Value0x1A4);
        Assert.DoesNotContain(owner, world.InteractionRefreshPendingObjects);
        Assert.Contains(owner, world.InteractionRefreshDeferredObjects);
        Assert.True(world.InteractionContextsInvalidated);
        Assert.Equal(owner.ClockManagerIndex, world.TileValueAt(destination.X >> 4, destination.Y >> 4, destination.Height));
    }

    [Fact]
    public void RemovalInteractionRefreshUsesTheSecondPersonFootprintSlot()
    {
        var world = new SimulationWorld
        {
            InteractionRefreshTypeKey = 0x0baa71c5,
        };
        var definition = new short[XObject.DefinitionCount];
        definition[(int)ObjdField.Type] = XPerson.PersonType;
        var person = new XPerson(41, definition)
        {
            Position = new ObjectPosition(0x40, 0x40, 1),
            TypeRecord = new TypeRecord(41) { ObjectType = XPerson.PersonType },
        };
        var firstSlotObject = Object(42, new ObjectPosition(0x60, 0x60, 1));
        var secondSlotObject = Object(43, new ObjectPosition(0x70, 0x70, 1));
        world.Add(person);
        world.Add(firstSlotObject);
        world.Add(secondSlotObject);
        person.FootprintSlots.Add(new ObjectFootprintSlot { OccupantObjectId = firstSlotObject.ClockManagerIndex });
        person.FootprintSlots.Add(new ObjectFootprintSlot { OccupantObjectId = secondSlotObject.ClockManagerIndex });

        world.BeginObjectRemoval(person, 1);

        // FUN_00433ef5 (00433ef5.c:30-53) moves both +0x130 slot children with person, while FUN_0044822b
        // (0044822b.c:18,45) invokes +0x50(0) only on the second person footprint slot (+0x48).
        Assert.Equal((short)0, firstSlotObject.RemovalCallbackWord14);
        Assert.Equal((short)-1, secondSlotObject.RemovalCallbackWord14);
        Assert.Equal(new ObjectPosition(-16, -16, 1), secondSlotObject.Position);
    }

    [Fact]
    public void ExhaustedErrorElevenAssociationScanPreservesRefusalAndMovesOwnerGroup()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithGroundClass(0x10);
        var destination = new ObjectPosition(0x18, 0x18, 1);
        var owner = Object(33, new ObjectPosition(0x28, 0x28, 1));
        owner.RuntimeClass = ObjectClass.MultiTileObject;
        owner.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        var definition = new short[XObject.DefinitionCount];
        definition[(int)ObjdField.Type] = XPerson.PersonType;
        var person = new XPerson(34, definition)
        {
            Position = new ObjectPosition(0x50, 0x50, 1),
            TypeRecord = new TypeRecord(34) { FootprintMask = 0x1111 },
        };
        person[ObjectData.AllowedHeightFlags] = 1;
        person[ObjectData.PlacementFlags] = 1;
        var blocker = Object(35, destination);
        owner.MultiTileMoveAssociatedObject = person;
        world.Add(owner);
        world.Add(person);
        world.Add(blocker);

        world.PlaceObject(owner, destination);

        Assert.Equal(destination, owner.Position);
        Assert.Equal(new ObjectPosition(0x50, 0x50, 1), person.Position);
        Assert.Equal((short)0, owner.MultiTileMoveFlag1B0);
        Assert.Equal(0x0b, world.LastPlacementFailureCode);
    }

    [Fact]
    public void ExhaustedAssociatedSlotScanReportsSourceError24ForItsHeightMask()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithGroundClass(0x10);
        var destination = new ObjectPosition(0x18, 0x18, 1);
        var owner = Object(37, destination);
        owner.RuntimeClass = ObjectClass.MultiTileObject;
        owner.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        var definition = new short[XObject.DefinitionCount];
        definition[(int)ObjdField.Type] = XPerson.PersonType;
        var person = new XPerson(38, definition)
        {
            Position = new ObjectPosition(0x40, 0x40, 1),
            TypeRecord = new TypeRecord(38) { FootprintMask = 0x1111 },
        };
        person[ObjectData.AllowedHeightFlags] = 8;
        person.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 4 });
        owner.MultiTileMoveAssociatedObject = person;
        world.Add(owner);
        world.Add(person);
        owner.MultiTileMovePositionState = destination;
        owner.MultiTileMoveValue1C4 = person.ClockManagerIndex;

        world.PlaceObject(owner, destination);

        Assert.Equal(destination, owner.Position);
        Assert.Equal(new ObjectPosition(0x40, 0x40, 1), person.Position);
        Assert.Equal(0x24, world.LastPlacementFailureCode);
    }

    [Fact]
    public void AssociatedMemberFallbackSearchesOffsetsBeforeApplyingMoverRotation()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithGroundClass(0x10);
        var destination = new ObjectPosition(0x18, 0x18, 1);
        var owner = Object(39, new ObjectPosition(0x18, 0x28, 1));
        owner.RuntimeClass = ObjectClass.MultiTileObject;
        owner.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        owner[ObjectData.RotationNotches] = 1;
        var personDefinition = new short[XObject.DefinitionCount];
        personDefinition[(int)ObjdField.Type] = XPerson.PersonType;
        var person = new XPerson(40, personDefinition)
        {
            Position = new ObjectPosition(0x50, 0x50, 1),
            TypeRecord = new TypeRecord(40) { FootprintMask = 0x1111 },
        };
        person[ObjectData.AllowedHeightFlags] = 1;
        person[ObjectData.PlacementFlags] = 1;
        person[ObjectData.RotationNotches] = 1;
        var memberDefinition = new short[XObject.DefinitionCount];
        memberDefinition[(int)ObjdField.MasterId] = 1;
        var candidate = new XObject(41, memberDefinition) { Position = destination };
        candidate[ObjectData.AllowedHeightFlags] = 1;
        candidate.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
        owner.MultiTileMoveAssociatedObject = person;
        world.Add(owner);
        world.Add(person);
        world.Add(candidate);

        world.PlaceObject(owner, destination);

        Assert.Equal(destination, owner.Position);
        Assert.Equal((short)2, owner[ObjectData.Direction]);
        Assert.Equal(candidate.ClockManagerIndex, owner.MultiTileMoveValue1C4);
        Assert.Equal(candidate.ClockManagerIndex, person[ObjectData.ParentId]);
    }

    [Fact]
    public void MultiTileMoveRestoresCallbackSlotWhenPositionIsUnchangedAndClearsItWhenChanged()
    {
        var world = new SimulationWorld();
        var item = Object(21, new ObjectPosition(0x20, 0x30, 1));
        item.RuntimeClass = ObjectClass.MultiTileObject;
        item.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        world.Add(item);
        world.PlaceCreatedObject(item, item.Position);
        item.MultiTileMoveValue1C4 = 7;

        world.PlaceObject(item, item.Position, placementSlotNumber: 5);

        Assert.Equal((short)7, item.MultiTileMoveValue1C4);
        Assert.Equal((short)5, item.MultiTileMoveValue1C6);

        world.PlaceObject(item, new ObjectPosition(0x30, 0x30, 1), placementSlotNumber: 3);

        Assert.Equal((short)0, item.MultiTileMoveValue1C4);
        Assert.Equal((short)0, item.MultiTileMoveValue1C6);
    }

    [Fact]
    public void PlacementSetsPositionRefreshStateOnlyAfterCoordinatesChange()
    {
        var world = new SimulationWorld();
        var item = world.Add(Object(19, new ObjectPosition(0x20, 0x30, 1)));

        world.PlaceObject(item, item.Position);
        Assert.False(item.PositionRefreshPending);

        world.PlaceObject(item, new ObjectPosition(0x30, 0x30, 1));
        Assert.True(item.PositionRefreshPending);

        var levelOnly = world.Add(Object(20, new ObjectPosition(0x20, 0x40, 1)));
        world.PlaceObject(levelOnly, new ObjectPosition(0x20, 0x40, 2));
        Assert.False(levelOnly.PositionRefreshPending);
    }

    [Theory]
    [InlineData(2, 0x0000, false)]
    [InlineData(2, 0x0200, true)]
    [InlineData(3, 0x0000, false)]
    [InlineData(3, 0x0200, true)]
    public void UpperFloorPermissionAppliesWhenTheEntireLevelMapIsAbsent(int level, int flags, bool expected)
    {
        var world = new SimulationWorld();
        var item = world.Add(Object(30));
        var relativeTo = world.Add(Object(31));
        item[ObjectData.PlacementFlags] = (short)flags;
        var candidate = new ObjectPosition(0x20, 0x30, level);

        Assert.Equal(expected, world.CanPlaceObject(item, candidate));
        Assert.Equal(expected, world.CanPlaceCreatedObject(item, candidate));
        Assert.Equal(expected, world.CanPlaceCreatedObject(item, candidate, relativeTo));
    }

    [Theory]
    [InlineData(1, 1, 0, false)]
    [InlineData(1, 1, 4, true)]
    [InlineData(1, 2, 0, true)]
    [InlineData(1, 2, 4, false)]
    [InlineData(1, 3, 4, true)]
    [InlineData(2, 1, 0, false)]
    [InlineData(2, 1, 4, true)]
    [InlineData(2, 2, 0, true)]
    [InlineData(2, 2, 4, false)]
    [InlineData(3, 1, 0, false)]
    [InlineData(3, 1, 4, true)]
    [InlineData(3, 2, 0, true)]
    [InlineData(3, 2, 4, false)]
    public void RoomPlacementUsesBitTwoOfItsLevelManagerGrid(int level, short roomPlacement, byte gridFlags, bool expected)
    {
        var world = new SimulationWorld();
        var position = new ObjectPosition(5 * 16 + 8, 6 * 16 + 8, level);
        var item = world.Add(Object(32, position));
        item[ObjectData.RoomPlacement] = roomPlacement;
        if (level > 1) item[ObjectData.PlacementFlags] = 0x0200;
        if (gridFlags != 0)
        {
            world.BuildModeType6PlacementGrids[level] = new byte[64 * 64];
            world.BuildModeType6PlacementGrids[level][5 * 64 + 6] = gridFlags;
        }

        Assert.Equal(expected, world.CanPlaceObject(item, position));
    }

    [Fact]
    public void RelativePlacementSkipsRoomPlacementManagerGridCheck()
    {
        var world = new SimulationWorld();
        var position = new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1);
        var item = world.Add(Object(33, position));
        item[ObjectData.RoomPlacement] = 1;
        var reference = world.Add(Object(34, position));

        Assert.True(world.CanPlaceCreatedObject(item, position, reference));
        Assert.False(world.CanPlaceObject(item, position));
    }

    [Fact]
    public void PlacementMovesThePriorDestinationOccupantIntoTheMoverFirstSlot()
    {
        var world = new SimulationWorld();
        var oldPosition = new ObjectPosition(0x30, 0x40, 1);
        var destinationPosition = new ObjectPosition(0x50, 0x60, 2);
        var destination = world.Add(Object(35, destinationPosition));
        var mover = world.Add(Object(36, oldPosition));
        var displacedPosition = new ObjectPosition(destinationPosition.X + 5, destinationPosition.Y + 4, 1);
        var displaced = world.Add(Object(37, displacedPosition, destination.ClockManagerIndex));
        destination.Slots.Add(displaced.ClockManagerIndex);
        mover.Slots.Add(0);
        displaced[ObjectData.ContainerId] = destination.ClockManagerIndex;
        displaced[ObjectData.SlotNumber] = 0;
        displaced[ObjectData.SlotHierNumber] = 0;
        var originalContainer = displaced[ObjectData.ContainerId];
        var originalSlotNumber = displaced[ObjectData.SlotNumber];

        world.PlaceObject(mover, destinationPosition, placementContainer: destination, placementSlotNumber: 0);

        Assert.Equal(mover.ClockManagerIndex, destination.Slots[0]);
        Assert.Equal(displaced.ClockManagerIndex, mover.Slots[0]);
        Assert.Equal(destination.ClockManagerIndex, mover[ObjectData.ParentId]);
        Assert.Equal(mover.ClockManagerIndex, displaced[ObjectData.ParentId]);
        Assert.Equal((short)0, displaced[ObjectData.SlotHierNumber]);
        Assert.Equal(originalContainer, displaced[ObjectData.ContainerId]);
        Assert.Equal(originalSlotNumber, displaced[ObjectData.SlotNumber]);
        Assert.Equal(displacedPosition with { Height = destinationPosition.Height }, displaced.Position);
        Assert.Equal(destination.ClockManagerIndex, world.TileValueAt(destinationPosition.X >> 4, destinationPosition.Y >> 4, 2));
        Assert.Equal(0, world.TileValueAt(oldPosition.X >> 4, oldPosition.Y >> 4, 1));
    }

    [Fact]
    public void PlacementDisplacementResolvesTheOccupantFromTheFootprintRecord()
    {
        var world = new SimulationWorld();
        var destinationPosition = new ObjectPosition(0x50, 0x60, 2);
        var destination = world.Add(Object(38, destinationPosition));
        var mover = world.Add(Object(39, new ObjectPosition(0x30, 0x40, 1)));
        var displaced = world.Add(Object(40, destinationPosition with { Height = 1 }, destination.ClockManagerIndex));
        destination.Slots.AddRange([0, 0]); // The relation vector still reflects the pre-apply empty slot.
        destination.FootprintSlots.Add(new ObjectFootprintSlot { OccupantObjectId = displaced.ClockManagerIndex });
        mover.Slots.Add(0);
        displaced[ObjectData.ParentId] = destination.ClockManagerIndex;
        displaced[ObjectData.ContainerId] = destination.ClockManagerIndex;
        displaced[ObjectData.SlotNumber] = 1;
        displaced[ObjectData.SlotHierNumber] = 1;

        world.PlaceObject(mover, destinationPosition, placementContainer: destination, placementSlotNumber: 1);

        Assert.Equal(mover.ClockManagerIndex, destination.Slots[1]);
        Assert.Equal(mover.ClockManagerIndex, destination.FootprintSlots[0].OccupantObjectId);
        Assert.Equal(displaced.ClockManagerIndex, mover.Slots[0]);
        Assert.Equal(mover.ClockManagerIndex, displaced[ObjectData.ParentId]);
        Assert.Equal(destination.ClockManagerIndex, displaced[ObjectData.ContainerId]);
        Assert.Equal((short)1, displaced[ObjectData.SlotNumber]);
        Assert.Equal(destinationPosition, displaced.Position);
    }

    [Fact]
    public void OrdinaryPlacementDisplacesTheRequestedTileRootIntoMoverSlotZero()
    {
        var world = new SimulationWorld();
        var oldPosition = new ObjectPosition(0x30, 0x40, 1);
        var destination = new ObjectPosition(0x50, 0x60, 2);
        var mover = world.Add(Object(41, oldPosition));
        var displacedSource = new ObjectPosition(destination.X + 5, destination.Y + 4, destination.Height);
        var displaced = world.Add(Object(42, displacedSource));
        mover.Slots.Add(0);

        world.PlaceObject(mover, destination);

        Assert.Equal(mover.ClockManagerIndex, world.TileValueAt(destination.X >> 4, destination.Y >> 4, destination.Height));
        Assert.Equal(0, world.TileValueAt(oldPosition.X >> 4, oldPosition.Y >> 4, oldPosition.Height));
        Assert.Equal(displaced.ClockManagerIndex, mover.Slots[0]);
        Assert.Equal(mover.ClockManagerIndex, displaced[ObjectData.ParentId]);
        Assert.Equal((short)0, displaced[ObjectData.ContainerId]);
        Assert.Equal((short)0, displaced[ObjectData.SlotNumber]);
        Assert.Equal((short)0, displaced[ObjectData.SlotHierNumber]);
        Assert.Equal(displacedSource, displaced.Position);
        Assert.Equal(new[] { mover, displaced }, world.ObjectsAtTile(destination.X >> 4, destination.Y >> 4, destination.Height));
    }

    [Fact]
    public void OrdinaryPlacementDoesNotDisplaceItsOwnTileRoot()
    {
        var world = new SimulationWorld();
        var position = new ObjectPosition(0x50, 0x60, 1);
        var mover = world.Add(Object(43, position));
        mover.Slots.Add(0);

        world.PlaceObject(mover, position);

        Assert.Equal(mover.ClockManagerIndex, world.TileValueAt(position.X >> 4, position.Y >> 4, position.Height));
        Assert.Equal((short)0, mover.Slots[0]);
        Assert.Equal((short)0, mover[ObjectData.ParentId]);
        Assert.Equal((short)-1, mover[ObjectData.SlotHierNumber]);
    }

    [Theory]
    [InlineData(0x10, 0x20, 1, true)]
    [InlineData(0x3e0, 0x3e0, 3, true)]
    [InlineData(0x00, 0x20, 1, false)]
    [InlineData(0x3f0, 0x20, 1, false)]
    [InlineData(0x20, 0x00, 1, false)]
    [InlineData(0x20, 0x3f0, 1, false)]
    [InlineData(0x20, 0x20, 0, false)]
    [InlineData(0x20, 0x20, 4, false)]
    public void BothPlacementEntryPointsUseTheSourceCommonBoundsAndLevelGate(int x, int y, int level, bool expected)
    {
        var world = new SimulationWorld();
        var item = world.Add(Object(12, new ObjectPosition(0x80, 0x80, 1)));
        if (level > 1) item[ObjectData.PlacementFlags] = 0x0200;
        var candidate = new ObjectPosition(x, y, level);

        Assert.Equal(expected, world.CanPlaceObject(item, candidate));
        Assert.Equal(expected, world.CanPlaceCreatedObject(item, candidate));
    }

    [Theory]
    [InlineData(0x0000, 0x0f, false)]
    [InlineData(0x0800, 0x0f, true)]
    [InlineData(0x1000, 0x0f, false)]
    [InlineData(0x1800, 0x0f, true)]
    [InlineData(0x0000, 0x10, true)]
    [InlineData(0x0800, 0x10, true)]
    [InlineData(0x1000, 0x10, false)]
    [InlineData(0x1800, 0x10, false)]
    public void PlacementFlagsApplyTheSourceLevelOneGroundClassGate(int flags, int groundClass, bool expected)
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithGroundClass(groundClass);
        var definition = new short[XObject.DefinitionCount];
        var item = world.Add(new XObject(13, definition));
        var relativeTo = world.Add(Object(29));
        item[ObjectData.AllowedHeightFlags] = 1;
        item[ObjectData.PlacementFlags] = (short)flags;
        var candidate = new ObjectPosition(0x18, 0x18, 1);

        Assert.Equal(expected, world.CanPlaceObject(item, candidate));
        Assert.Equal(expected, world.CanPlaceCreatedObject(item, candidate));
        Assert.Equal(expected, world.CanPlaceCreatedObject(item, candidate, relativeTo));
    }

    [Theory]
    [InlineData(0x0000, true)]
    [InlineData(0x0800, true)]
    [InlineData(0x1000, false)]
    public void PlacementUsesTheNoPoolSentinelForAnOmittedGroundGrid(int flags, bool expected)
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithGroundClass(0, includeWaterLayer: false);
        var item = world.Add(Object(28));
        item[ObjectData.PlacementFlags] = (short)flags;

        Assert.Equal(expected, world.CanPlaceObject(item, new ObjectPosition(0x18, 0x18, 1)));
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(0x80, false, true)]
    [InlineData(0x40, false, false)]
    [InlineData(0xC0, false, false)]
    [InlineData(0, true, false)]
    [InlineData(0x80, true, true)]
    [InlineData(0xC0, true, true)]
    public void WallPlacementFlagsApplyTheSourceDiagonalWallGate(int wallFlags, bool diagonal, bool expected)
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithDiagonalWall(diagonal);
        var item = world.Add(Object(16));
        item[ObjectData.WallPlacementFlags] = (short)wallFlags;
        item[ObjectData.PlacementFlags] = 0x0400; // isolate FUN_004770d1 from FUN_00476f33's separate footprint-wall gate
        var candidate = new ObjectPosition(0x18, 0x18, 1);

        Assert.Equal(expected, world.CanPlaceObject(item, candidate));
        Assert.Equal(expected, world.CanPlaceCreatedObject(item, candidate));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(12, false)]
    [InlineData(13, false)]
    [InlineData(14, false)]
    public void RequiredCardinalWallCategoryIsCheckedWhenDiagonalSegmentIsPresent(int category, bool expected)
    {
        var world = new SimulationWorld();
        // FUN_004770d1 requires cardinal segment 2 and a diagonal segment. The category rejection is
        // for the required cardinal segment; diagonal segment presence is controlled by flags 0x40/0x80.
        world.LotRoomMaps[1] = RoomMapWithWallGeometry(0x12, (byte)category);
        var expanded = world.LotRoomMaps[1].ExpandedWallAt(1, 1)!.Value;
        Assert.Equal((uint)0x12, expanded.UInt32At(0));
        Assert.Equal((ushort)category, expanded.WallCategory(2));
        var item = world.Add(Object(19));
        item[ObjectData.WallPlacementFlags] = 0x0081; // require local segment 2 and permit a diagonal wall
        item[ObjectData.PlacementFlags] = 0x0400; // isolate FUN_004770d1 from the separate footprint-wall gate

        Assert.Equal(expected, world.CanPlaceObject(item, new ObjectPosition(0x18, 0x18, 1)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void DiagonalOnlyPlacementDoesNotApplyTheCardinalCategoryRejection(int diagonalCategory)
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithWallGeometry(0x10, (byte)diagonalCategory);
        var expanded = world.LotRoomMaps[1].ExpandedWallAt(1, 1)!.Value;
        Assert.Equal((ushort)diagonalCategory, expanded.WallCategory(0x10));
        var item = world.Add(Object(20));
        item[ObjectData.WallPlacementFlags] = 0x00c0; // require and permit a diagonal segment
        item[ObjectData.PlacementFlags] = 0x0400; // skip the separate footprint-wall gate

        Assert.True(world.CanPlaceObject(item, new ObjectPosition(0x18, 0x18, 1)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void LoadedDiagonalWallCategoryDoesNotTriggerTheCardinalCategoryRejection(int diagonalCategory)
    {
        const int width = 64;
        const int height = 64;
        var wallBytes = new byte[width * height * 8];
        var center = (1 * height + 1) * 8;
        wallBytes[center] = 0x10;
        wallBytes[center + 1] = 0x0f;
        wallBytes[center + 3] = (byte)diagonalCategory;
        var walls = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0).Raw(wallBytes);
        var floor = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(new byte[width * height]);
        var water = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(Enumerable.Repeat((byte)0x10, width * height).ToArray());
        var lot = new IffFile(ChunkBuilder.File(("Arry", 1, floor.ToArray()), ("Arry", 2, walls.ToArray()),
            ("Arry", 9, water.ToArray())), "diagonal-placement-input.iff");
        var world = new SimulationWorld();
        world.LotRoomMaps.Add(1, SavedLotObjectLoader.ReadRoomMaps(lot, 0x35)[1]);
        var expanded = world.LotRoomMaps[1].ExpandedWallAt(1, 1)!.Value;
        Assert.Equal((uint)0x10, expanded.UInt32At(0));
        Assert.Equal((ushort)diagonalCategory, expanded.WallCategory(0x10));

        var item = world.Add(Object(21));
        item[ObjectData.WallPlacementFlags] = 0x00c0; // require and permit the diagonal segment only
        item[ObjectData.PlacementFlags] = 0x0400; // skip FUN_00476f33's separate footprint-wall gate

        // FUN_004770d1 rejects forbidden categories only for required cardinal masks; diagonal bits
        // are tested for presence/absence, so these category ids do not reject this placement.
        Assert.True(world.CanPlaceObject(item, new ObjectPosition(0x18, 0x18, 1)));
    }

    [Fact]
    public void RelativeCreatePlacementSkipsTheSourceWallPlacementGate()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithDiagonalWall(true);
        var item = world.Add(Object(17));
        var relativeTo = world.Add(Object(18));
        var candidate = new ObjectPosition(0x18, 0x18, 1);

        Assert.False(world.CanPlaceCreatedObject(item, candidate));
        Assert.True(world.CanPlaceCreatedObject(item, candidate, relativeTo));
    }

    [Fact]
    public void PlacementRequiresFloorPermissionUnlessCreationIsRelative()
    {
        var world = new SimulationWorld();
        var item = world.Add(Object(25));
        var relativeTo = world.Add(Object(26));
        item[ObjectData.AllowedHeightFlags] = 0;
        var candidate = new ObjectPosition(0x18, 0x18, 1);

        Assert.False(world.CanPlaceObject(item, candidate));
        Assert.False(world.CanPlaceCreatedObject(item, candidate));
        Assert.True(world.CanPlaceCreatedObject(item, candidate, relativeTo));
    }

    [Fact]
    public void PlacementReadsAllowedHeightFlagsFromObjectDataNotTheObjdDefinition()
    {
        var world = new SimulationWorld();
        var item = world.Add(Object(71, new ObjectPosition(0x18, 0x18, 1)));

        // OBJD field 4 is NumGraphics; the placement gate reads cXObject +0x52, object-data field 4.
        item.Definition[(int)ObjdField.NumGraphics] = 1;
        item[ObjectData.AllowedHeightFlags] = 0;
        Assert.False(world.CanPlaceObject(item, item.Position));

        item[ObjectData.AllowedHeightFlags] = 1;
        Assert.True(world.CanPlaceObject(item, item.Position));
    }

    [Fact]
    public void PlacementRejectsIntersectingNonPersonFootprintsWhenNeitherObjectDefinesFunctionFive()
    {
        var world = new SimulationWorld();
        var candidate = world.Add(PlacementObject(27, 0x20));
        world.Add(PlacementObject(28, 0x30));

        Assert.False(world.CanPlaceObject(candidate, candidate.Position));
        Assert.False(world.CanPlaceCreatedObject(candidate, candidate.Position));
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(1, 0, true)]
    [InlineData(0, 1, true)]
    public void CenterRootExclusivePlacementFlagsRejectOnlyWhenBothObjectsSetBitZero(
        short candidateExclusiveFlags, short otherExclusiveFlags, bool expected)
    {
        var world = new SimulationWorld();
        var position = new ObjectPosition(0x38, 0x38, 1);
        var other = world.Add(Object(72, position));
        other[ObjectData.Flags] = 4;
        other[ObjectData.ExclusivePlacementFlags] = otherExclusiveFlags;
        var candidate = world.Add(Object(73, position));
        candidate[ObjectData.Flags] = 4;
        candidate[ObjectData.ExclusivePlacementFlags] = candidateExclusiveFlags;

        Assert.Equal(expected, world.CanPlaceObject(candidate, position));
    }

    [Theory]
    [InlineData(2, 2, 1, 1, 0, 0, false)]
    [InlineData(2, 2, 1, 4, 0, 0, true)]
    [InlineData(2, 0, 1, 1, 0, 0, true)]
    [InlineData(0, 0, 1, 1, 0, 0, false)]
    [InlineData(2, 2, 1, 1, 2, 0, true)]
    public void CenterRootFlagsBitTwoComparesSourceManagerStates(
        short candidateAlternativeFlag, short otherAlternativeFlag, short candidateObjectId, short otherObjectId,
        short candidateDirection, short otherDirection, bool expected)
    {
        var world = new SimulationWorld();
        var position = new ObjectPosition(0x38, 0x38, 1);
        var other = world.Add(Object(74, position));
        other[ObjectData.Flags] = 4;
        other[ObjectData.ObjectId] = otherObjectId;
        other[ObjectData.Direction] = otherDirection;
        other.TypeRecord = new TypeRecord(74) { AutonomyAlternativeTypeFlag = otherAlternativeFlag };
        var candidate = world.Add(Object(75, position));
        candidate[ObjectData.Flags] = 4;
        candidate[ObjectData.ObjectId] = candidateObjectId;
        candidate[ObjectData.Direction] = candidateDirection;
        candidate.TypeRecord = new TypeRecord(75) { AutonomyAlternativeTypeFlag = candidateAlternativeFlag };

        Assert.Equal(expected, world.CanPlaceObject(candidate, position));
    }

    [Fact]
    public void MultiTilePlacementValidatesEveryLinkedMemberAtItsTranslatedPosition()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithGroundClass(0x10);
        var root = Object(901, new ObjectPosition(0x40, 0x40, 1));
        root.RuntimeClass = ObjectClass.MultiTileObject;
        root.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        root[ObjectData.PlacementFlags] = 0x0800;
        world.Add(root);
        var part = Object(902, new ObjectPosition(0x30, 0x40, 1));
        part.RuntimeClass = ObjectClass.MultiTileObject;
        part.SavedMultiTileState = new MultiTileState(-1, 0, 0, 0, 0, 0, root.Id, null);
        part[ObjectData.PlacementFlags] = 0x0800;
        part.MultiTileMasterId = root.Id;
        root.MultiTileNextId = part.Id;
        world.Add(part);

        var requested = new ObjectPosition(0x40, 0x40, 1);
        Assert.True(world.CanPlaceObject(root, requested));

        var blocker = world.Add(Object(903, new ObjectPosition(0x30, 0x40, 1)));
        Assert.NotEqual(root.ClockManagerIndex, blocker.ClockManagerIndex);
        Assert.False(world.CanPlaceObject(root, requested));
    }

    [Fact]
    public void AssociatedObjectPointerDoesNotReplaceTheMovingGroupsPlacementValidation()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithGroundClass(0x10);
        var moving = Object(904, new ObjectPosition(0x40, 0x40, 1));
        moving.RuntimeClass = ObjectClass.MultiTileObject;
        moving.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        moving[ObjectData.PlacementFlags] = 0x0800;
        world.Add(moving);
        var movingPart = Object(905, new ObjectPosition(0x30, 0x40, 1));
        movingPart.RuntimeClass = ObjectClass.MultiTileObject;
        movingPart.SavedMultiTileState = new MultiTileState(-1, 0, 0, 0, 0, 0, moving.Id, null);
        movingPart[ObjectData.PlacementFlags] = 0x0800;
        movingPart.MultiTileMasterId = moving.Id;
        moving.MultiTileNextId = movingPart.Id;
        world.Add(movingPart);

        var associated = Object(906, new ObjectPosition(0x80, 0x80, 1));
        associated.RuntimeClass = ObjectClass.MultiTileObject;
        associated.SavedMultiTileState = new MultiTileState(0, 0, 0, 0, 0, 0, 0, null);
        world.Add(associated);
        moving.MultiTileMoveAssociatedObject = associated;

        var target = new ObjectPosition(0x40, 0x40, 1);
        Assert.True(world.CanPlaceObject(moving, target));
        world.Add(Object(907, new ObjectPosition(0x30, 0x40, 1)));

        Assert.False(world.CanPlaceObject(moving, target));
    }

    [Theory]
    [InlineData(ObjectClass.Object)]
    [InlineData(ObjectClass.MultiTileObject)]
    [InlineData(ObjectClass.Portal)]
    public void PlacementSkipsFootprintCollisionAgainstOtherMembersOfTheSameMultiTileChain(ObjectClass runtimeClass)
    {
        var world = new SimulationWorld();
        var master = Object(76, new ObjectPosition(0x80, 0x80, 1));
        master.RuntimeClass = runtimeClass;
        master[ObjectData.PlacementFlags] = 1; // Every validated member skips the synthetic shared root cell.
        world.Add(master);
        var position = new ObjectPosition(0x38, 0x38, 1);
        var sibling = Object(78, position);
        sibling.RuntimeClass = runtimeClass;
        sibling[ObjectData.PlacementFlags] = 1; // Source bit 0 bypasses the tile-root occupancy comparison.
        world.Add(sibling);
        var candidate = Object(77, position);
        candidate.RuntimeClass = runtimeClass;
        candidate[ObjectData.PlacementFlags] = 1;
        world.Add(candidate);

        master.MultiTileNextId = candidate.Id;
        candidate.MultiTileMasterId = master.Id;
        candidate.MultiTileNextId = sibling.Id;
        sibling.MultiTileMasterId = master.Id;

        Assert.True(world.CanPlaceObject(candidate, position));
    }

    [Fact]
    public void PersonPlacementCallbackDoesNotFilterOtherObjectsByLinkedChain()
    {
        var world = new SimulationWorld();
        var position = new ObjectPosition(0x38, 0x38, 1);
        var master = world.Add(Object(83, position));
        var definition = new short[XObject.DefinitionCount];
        definition[(int)ObjdField.Type] = XPerson.PersonType;
        var person = new XPerson(84, definition)
        {
            Position = position,
            TypeRecord = new TypeRecord(84),
            MultiTileMasterId = master.Id,
        };
        person[ObjectData.AllowedHeightFlags] = 1;
        world.Add(person);
        master.MultiTileNextId = person.Id;

        Assert.False(world.CanPlaceObject(person, position));
    }

    [Fact]
    public void PlacementStillChecksFootprintsAgainstUnlinkedObjects()
    {
        var world = new SimulationWorld();
        var position = new ObjectPosition(0x38, 0x38, 1);
        world.Add(Object(79, position));
        var candidate = Object(80, position);
        candidate.RuntimeClass = ObjectClass.MultiTileObject;
        world.Add(candidate);

        Assert.False(world.CanPlaceObject(candidate, position));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public void BaseObjectPlacementCallbackDoesNotUseTheExtensionBitAsANeighborFilter(short extensionFlags, bool expected)
    {
        var world = new SimulationWorld();
        var position = new ObjectPosition(0x38, 0x38, 1);
        world.Add(Object(81, position));
        var candidate = Object(82, position);
        candidate.Definition[0x66] = extensionFlags;
        world.Add(candidate);

        Assert.Equal(expected, world.CanPlaceObject(candidate, position));
    }

    [Fact]
    public void PlacementAllowsTouchingNonPersonFootprintBounds()
    {
        var world = new SimulationWorld();
        var candidate = world.Add(PlacementObject(29, 0x20));
        world.Add(PlacementObject(30, 0x40));

        Assert.True(world.CanPlaceObject(candidate, candidate.Position));
    }

    [Fact]
    public void OrdinaryPlacementChecksLivingPersonFootprintsButIgnoresGhostsAndSpecialFlags()
    {
        static (SimulationWorld World, XObject Candidate) Setup(short id, short flags = 0, bool ghost = false)
        {
            var world = new SimulationWorld();
            var candidate = PlacementObject(id, 0x30);
            candidate[ObjectData.Flags] = flags;
            world.Add(candidate);
            var definition = new short[XObject.DefinitionCount];
            definition[(int)ObjdField.Type] = XPerson.PersonType;
            var person = new XPerson((short)(id + 1), definition)
            {
                Position = new ObjectPosition(0x40, 0x30, 1),
                TypeRecord = new TypeRecord((short)(id + 1)),
            };
            person[ObjectData.AllowedHeightFlags] = 1;
            if (ghost) person[PersonData.IsGhost] = 1;
            world.Add(person);
            return (world, candidate);
        }

        var (livingWorld, livingCandidate) = Setup(34);
        Assert.False(livingWorld.CanPlaceObject(livingCandidate, livingCandidate.Position));

        var (ghostWorld, ghostCandidate) = Setup(36, ghost: true);
        Assert.True(ghostWorld.CanPlaceObject(ghostCandidate, ghostCandidate.Position));

        var (flagsWorld, flagsCandidate) = Setup(38, flags: 0x04);
        Assert.True(flagsWorld.CanPlaceObject(flagsCandidate, flagsCandidate.Position));
        (flagsWorld, flagsCandidate) = Setup(40, flags: 0x10);
        Assert.True(flagsWorld.CanPlaceObject(flagsCandidate, flagsCandidate.Position));
        (flagsWorld, flagsCandidate) = Setup(42, flags: 0x06);
        Assert.Equal((short)0x06, flagsCandidate[ObjectData.Flags]);
        var flagPerson = (XPerson)flagsWorld.Objects[43];
        flagPerson.RefreshFootprintBounds();
        Assert.Equal(new FootprintBounds(0x43, 0x33, 0x3d, 0x2d), flagPerson.FootprintBounds);
        Assert.False(flagsWorld.CanPlaceObject(flagsCandidate, flagsCandidate.Position));
    }

    [Fact]
    public void OrdinarySimPlacementUsesPersonFootprintAtTheProposedPosition()
    {
        var world = new SimulationWorld();
        var definition = new short[XObject.DefinitionCount];
        definition[(int)ObjdField.Type] = XPerson.PersonType;
        var person = new XPerson(44, definition)
        {
            Position = new ObjectPosition(0x30, 0x30, 1),
            TypeRecord = new TypeRecord(44),
        };
        person[ObjectData.AllowedHeightFlags] = 1;
        world.Add(person);
        world.Add(PlacementObject(45, 0x40));

        Assert.False(world.CanPlaceObject(person, person.Position));
    }

    [Fact]
    public void GhostPlacementSkipsFootprintScanOnlyWhenItHasNoParentOrSecondSlotObject()
    {
        static XPerson Ghost(SimulationWorld world, short id, short parent = 0, short slotNumber = 0)
        {
            var definition = new short[XObject.DefinitionCount];
            definition[(int)ObjdField.Type] = XPerson.PersonType;
            var person = new XPerson(id, definition)
            {
                Position = new ObjectPosition(0x30, 0x30, 1),
                TypeRecord = new TypeRecord(id),
            };
            person[ObjectData.AllowedHeightFlags] = 1;
            person[PersonData.IsGhost] = 1;
            person[PersonData.FootprintExtension] = unchecked((short)0xffff);
            person[ObjectData.ParentId] = parent;
            person[ObjectData.SlotHierNumber] = slotNumber;
            return world.Add(person);
        }

        var uncontainedWorld = new SimulationWorld();
        var uncontainedGhost = Ghost(uncontainedWorld, 48);
        uncontainedWorld.Add(PlacementObject(49, 0x40));
        Assert.True(uncontainedWorld.CanPlaceObject(uncontainedGhost, uncontainedGhost.Position));

        var danglingParentWorld = new SimulationWorld();
        var danglingParentGhost = Ghost(danglingParentWorld, 55, 123);
        danglingParentWorld.Add(PlacementObject(56, 0x40));
        Assert.True(danglingParentWorld.CanPlaceObject(danglingParentGhost, danglingParentGhost.Position));

        var slottedWorld = new SimulationWorld();
        var slottedGhost = Ghost(slottedWorld, 53);
        var slotObject = slottedWorld.Add(PlacementObject(54, 0x40));
        slottedGhost.Slots.Add(0);
        slottedGhost.Slots.Add(slotObject.ClockManagerIndex);
        Assert.False(slottedWorld.CanPlaceObject(slottedGhost, slottedGhost.Position));

        var rootAncestorWorld = new SimulationWorld();
        var rootAncestor = rootAncestorWorld.Add(PlacementObject(57, 0x20));
        var chainGhost = Ghost(rootAncestorWorld, 58, rootAncestor.ClockManagerIndex);
        rootAncestorWorld.Add(PlacementObject(59, 0x40));
        Assert.True(rootAncestorWorld.CanPlaceObject(chainGhost, chainGhost.Position));

        var containedWorld = new SimulationWorld();
        var parent = containedWorld.Add(PlacementObject(50, 0x20));
        var containedGhost = Ghost(containedWorld, 51, parent.ClockManagerIndex, slotNumber: 1);
        containedWorld.Add(PlacementObject(52, 0x40));
        Assert.False(containedWorld.CanPlaceObject(containedGhost, containedGhost.Position));
    }

    [Fact]
    public void PersonCandidateReadsTheNeighborsIntersectionFlagsEvenForLivingGhosts()
    {
        static (SimulationWorld World, XPerson Candidate) Setup(bool livingGhost)
        {
            var world = new SimulationWorld();
            var parent = world.Add(PlacementObject((short)(livingGhost ? 61 : 63), 0));
            var definition = new short[XObject.DefinitionCount];
            definition[(int)ObjdField.Type] = XPerson.PersonType;
            var candidate = new XPerson((short)(livingGhost ? 62 : 64), definition)
            {
                Position = new ObjectPosition(0x30, 0x30, 1),
                TypeRecord = new TypeRecord((short)(livingGhost ? 62 : 64)),
            };
            candidate[ObjectData.AllowedHeightFlags] = 1;
            candidate[ObjectData.Flags] = 0x10;
            candidate[ObjectData.ParentId] = parent.ClockManagerIndex;
            candidate[ObjectData.SlotHierNumber] = 1;
            candidate[PersonData.FootprintExtension] = unchecked((short)0xffff);
            if (livingGhost) candidate[PersonData.IsLivingGhost] = 1;
            world.Add(candidate);

            var neighborDefinition = new short[XObject.DefinitionCount];
            neighborDefinition[(int)ObjdField.Type] = XPerson.PersonType;
            var neighbor = world.Add(new XPerson((short)(livingGhost ? 65 : 66), neighborDefinition)
            {
                Position = new ObjectPosition(0x40, 0x30, 1),
                TypeRecord = new TypeRecord((short)(livingGhost ? 65 : 66)),
            });
            neighbor[ObjectData.AllowedHeightFlags] = 1;
            return (world, candidate);
        }

        var (ordinaryWorld, ordinaryCandidate) = Setup(livingGhost: false);
        Assert.False(ordinaryWorld.CanPlaceObject(ordinaryCandidate, ordinaryCandidate.Position));
        ordinaryWorld.ObjectById(66)![ObjectData.Flags] = 0x10;
        Assert.True(ordinaryWorld.CanPlaceObject(ordinaryCandidate, ordinaryCandidate.Position));

        var (livingGhostWorld, livingGhostCandidate) = Setup(livingGhost: true);
        Assert.False(livingGhostWorld.CanPlaceObject(livingGhostCandidate, livingGhostCandidate.Position));
        livingGhostWorld.ObjectById(65)![ObjectData.Flags] = 0x10;
        Assert.True(livingGhostWorld.CanPlaceObject(livingGhostCandidate, livingGhostCandidate.Position));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0x10, true)]
    [InlineData(4, true)]
    [InlineData(6, false)]
    public void PersonPlacementUsesTheDoorsIntersectionFlags(short flags, bool accepted)
    {
        var world = new SimulationWorld();
        var definition = new short[XObject.DefinitionCount];
        definition[(int)ObjdField.Type] = XPerson.PersonType;
        var person = world.Add(new XPerson(81, definition)
        {
            Position = new ObjectPosition(0x30, 0x30, 1),
            TypeRecord = new TypeRecord(81),
        });
        person[ObjectData.AllowedHeightFlags] = 1;
        person[PersonData.FootprintExtension] = unchecked((short)0xffff);
        var door = world.Add(PlacementObject(82, 0x40));
        door[ObjectData.Flags] = flags;
        Assert.Equal(accepted, world.CanPlaceObject(person, person.Position));
    }

    [Fact]
    public void PlacementRunsEachObjectsOwnFunctionFiveWithTheOtherObjectsKey()
    {
        var world = new SimulationWorld();
        var candidate = world.Add(PlacementObject(31, 0x20));
        var other = PlacementObject(32, 0x30);
        var functions = new ushort[6];
        functions[5] = 1;
        candidate.TypeRecord = new TypeRecord(31)
        {
            FunctionTable = functions,
            BehaviorScope = new TreeTable((1, PrimitiveTestWorld.Tree(0, (0, 0xFE, 0xFF, new byte[8])))),
        };
        world.Add(other);

        var stack = new PlacementCheckStack(other.Id);
        Assert.True(world.CanPlaceObject(candidate, candidate.Position, stack));
        Assert.Equal([other.Id], stack.StackObjects);

        stack = new PlacementCheckStack(candidate.Id);
        Assert.False(world.CanPlaceObject(candidate, candidate.Position, stack));
        Assert.Equal([other.Id], stack.StackObjects);

        stack = new PlacementCheckStack();
        Assert.False(world.CanPlaceObject(candidate, candidate.Position, stack));
        Assert.Equal([other.Id], stack.StackObjects);

        // The second source call changes ECX to the neighbor, so its own scope and
        // temporaries must be used. Re-running the candidate tree cannot approve this.
        other.TypeRecord = new TypeRecord(32)
        {
            FunctionTable = functions,
            BehaviorScope = new TreeTable((1, PrimitiveTestWorld.Tree(0,
                (2, 1, 0xFF, PrimitiveTestWorld.Expression(0, 77, 5)),
                (2, 0xFE, 0xFF, PrimitiveTestWorld.Expression(0, candidate.StackObjectKey, 2,
                    (byte)DataOwner.StackObjectId, 7))))),
        };
        stack = new PlacementCheckStack();
        Assert.True(world.CanPlaceObject(candidate, candidate.Position, stack));
        Assert.Equal([other.Id], stack.StackObjects);
        Assert.Equal(77, other.Temporaries[0]);
        Assert.Equal(0, candidate.Temporaries[0]);
    }

    private sealed class PlacementCheckStack(short trueFor = 0) : TreeStack
    {
        public List<short> StackObjects { get; } = [];

        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction)
        {
            StackObjects.Add(frame.StackObject);
            return frame.StackObject == trueFor ? PrimitiveResult.True : PrimitiveResult.False;
        }
    }

    private static XObject PlacementObject(short id, int x)
    {
        var item = Object(id, new ObjectPosition(x, 0x30, 1));
        item.Definition[(int)ObjdField.TileWidth] = 32;
        item.TypeRecord = new TypeRecord(id);
        return item;
    }

    [Fact]
    public void PlacementFootprintSpanUsesTheObjectDefinitionRatherThanMutableRoomPlacementData()
    {
        static (SimulationWorld World, XObject Candidate) Setup(short candidateId, short neighborId, bool definitionSpan)
        {
            var world = new SimulationWorld();
            var candidate = world.Add(Object(candidateId, new ObjectPosition(0x40, 0x30, 1)));
            candidate.Definition[(int)ObjdField.TileWidth] = definitionSpan ? (short)32 : (short)0;
            candidate[ObjectData.RoomPlacement] = definitionSpan ? (short)0 : (short)32;
            candidate[ObjectData.Flags] = 0x20;
            candidate.TypeRecord = new TypeRecord(candidateId);
            var neighbor = world.Add(Object(neighborId, new ObjectPosition(0x50, 0x30, 1)));
            neighbor.TypeRecord = new TypeRecord(neighborId);
            neighbor[ObjectData.Flags] = 0x30;
            return (world, candidate);
        }

        var (wideDefinitionWorld, wideDefinitionCandidate) = Setup(67, 68, definitionSpan: true);
        Assert.False(wideDefinitionWorld.CanPlaceObject(wideDefinitionCandidate, wideDefinitionCandidate.Position));

        var (wideRuntimeDataWorld, wideRuntimeDataCandidate) = Setup(69, 70, definitionSpan: false);
        Assert.True(wideRuntimeDataWorld.CanPlaceObject(wideRuntimeDataCandidate, wideRuntimeDataCandidate.Position));
    }

    [Theory]
    [InlineData(0, 0x02)]
    [InlineData(1, 0x02)]
    [InlineData(2, 0x04)]
    [InlineData(3, 0x04)]
    [InlineData(4, 0x08)]
    [InlineData(5, 0x08)]
    [InlineData(6, 0x01)]
    [InlineData(7, 0x01)]
    public void CardinalWallRequirementsRotateWithTheOriginalEightWayDirection(int direction, int segment)
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithWallGeometry((byte)segment, 0);
        var item = world.Add(Object(19));
        item[ObjectData.Direction] = (short)direction;
        item[ObjectData.WallPlacementFlags] = 0x0001;
        var candidate = new ObjectPosition(0x18, 0x18, 1);

        Assert.True(world.CanPlaceObject(item, candidate));
    }

    [Theory]
    [InlineData(1, 0, 1)] [InlineData(1, 1, 2)] [InlineData(1, 2, 4)] [InlineData(1, 3, 8)]
    [InlineData(2, 0, 2)] [InlineData(2, 1, 4)] [InlineData(2, 2, 8)] [InlineData(2, 3, 1)]
    [InlineData(4, 0, 4)] [InlineData(4, 1, 8)] [InlineData(4, 2, 1)] [InlineData(4, 3, 2)]
    [InlineData(8, 0, 8)] [InlineData(8, 1, 1)] [InlineData(8, 2, 2)] [InlineData(8, 3, 4)]
    public void SourceWallMaskTableRotatesEachCardinalBit(uint bit, int quarterTurns, uint expected) =>
        Assert.Equal(expected, SimulationWorld.RotatePlacementWallBit(bit, quarterTurns));

    [Theory]
    [InlineData(0, true)]
    [InlineData(0x02, false)]
    [InlineData(0x0c, false)]
    [InlineData(0x0d, false)]
    [InlineData(0x0e, false)]
    public void CardinalWallRequirementRejectsTheSourceExcludedCategories(int category, bool expected)
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithWallGeometry(0x02, (byte)category);
        var item = world.Add(Object(20));
        item[ObjectData.WallPlacementFlags] = 0x0001;

        Assert.Equal(expected, world.CanPlaceObject(item, new ObjectPosition(0x18, 0x18, 1)));
    }

    [Fact]
    public void CardinalWallRequirementFailsWhenTheRequestedSegmentIsAbsent()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithWallGeometry(0x01, 0);
        var item = world.Add(Object(21));
        item[ObjectData.WallPlacementFlags] = 0x0001;

        Assert.False(world.CanPlaceObject(item, new ObjectPosition(0x18, 0x18, 1)));
    }

    [Theory]
    [InlineData(0x0000, false)]
    [InlineData(0x0400, true)]
    public void PlacementRejectsFootprintsThatCrossAWallUnlessPlacementFlagTenIsSet(int placementFlags, bool expected)
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithWallGeometry(0x01, 0);
        var definition = new short[XObject.DefinitionCount];
        definition[(int)ObjdField.TileWidth] = 32;
        var item = world.Add(new XObject(25, definition));
        item[ObjectData.AllowedHeightFlags] = 1;
        item[ObjectData.PlacementFlags] = (short)placementFlags;

        Assert.Equal(expected, world.CanPlaceObject(item, new ObjectPosition(0x18, 0x18, 1)));
    }

    [Theory]
    [InlineData(0x0200, false)]
    [InlineData(0x0600, true)]
    public void LevelThreePlacementStillRunsTheFootprintWallGate(int placementFlags, bool expected)
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[3] = RoomMapWithWallGeometry(0x01, 0, level: 3);
        var definition = new short[XObject.DefinitionCount];
        definition[(int)ObjdField.TileWidth] = 32;
        var item = world.Add(new XObject(26, definition));
        item[ObjectData.AllowedHeightFlags] = 1;
        item[ObjectData.PlacementFlags] = (short)placementFlags;

        Assert.Equal(expected, world.CanPlaceObject(item, new ObjectPosition(0x18, 0x18, 3)));
    }

    [Theory]
    [InlineData(2, false, 0x0000, false)]
    [InlineData(2, true, 0x0000, true)]
    [InlineData(2, false, 0x1000, false)]
    [InlineData(3, false, 0x0000, false)]
    [InlineData(3, true, 0x0000, true)]
    [InlineData(3, false, 0x1000, false)]
    public void UpperLevelWallRequirementAndPlacementFlagsFollowTheSourceBranch(
        int level, bool hasWall, int placementFlags, bool expected)
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[level] = RoomMapWithWallGeometry(hasWall ? (byte)0x02 : (byte)0, 0, level, floorWord: 1);
        var item = world.Add(Object(24));
        item[ObjectData.WallPlacementFlags] = 0x0001;
        item[ObjectData.PlacementFlags] = (short)placementFlags;

        Assert.Equal(expected, world.CanPlaceObject(item, new ObjectPosition(0x18, 0x18, level)));
        Assert.Equal(expected, world.CanPlaceCreatedObject(item, new ObjectPosition(0x18, 0x18, level)));
    }

    [Theory]
    [InlineData(0x0100, 0x02, false)]
    [InlineData(0x0100, 0x01, true)]
    [InlineData(0x0800, 0x01, false)]
    [InlineData(0x0800, 0x02, true)]
    [InlineData(0x0200, 0x04, false)]
    [InlineData(0x0200, 0x02, true)]
    [InlineData(0x0400, 0x08, false)]
    [InlineData(0x0400, 0x02, true)]
    public void HighCardinalWallFlagsRequireTheCorrespondingSegmentToBeClear(int wallFlags, int segment, bool expected)
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithWallGeometry((byte)segment, 0);
        var item = world.Add(Object(22));
        item[ObjectData.WallPlacementFlags] = (short)wallFlags;

        Assert.Equal(expected, world.CanPlaceObject(item, new ObjectPosition(0x18, 0x18, 1)));
    }

    [Theory]
    [InlineData(0, 0x02)]
    [InlineData(1, 0x02)]
    [InlineData(2, 0x04)]
    [InlineData(3, 0x04)]
    [InlineData(4, 0x08)]
    [InlineData(5, 0x08)]
    [InlineData(6, 0x01)]
    [InlineData(7, 0x01)]
    public void HighCardinalWallFlagsRotateWithObjectDirection(int direction, int segment)
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomMapWithWallGeometry((byte)segment, 0);
        var item = world.Add(Object(23));
        item[ObjectData.Direction] = (short)direction;
        item[ObjectData.WallPlacementFlags] = 0x0100;

        Assert.False(world.CanPlaceObject(item, new ObjectPosition(0x18, 0x18, 1)));
    }

    [Theory]
    [InlineData(2, 0, 0x0000, false)]
    [InlineData(2, 1, 0x0000, true)]
    [InlineData(2, 0, 0x0001, false)]
    [InlineData(2, 0, 0x1200, false)]
    [InlineData(2, 0, 0x0200, true)]
    [InlineData(2, 0, 0x1000, false)]
    [InlineData(2, -1, 0x0000, false)]
    [InlineData(2, -1, 0x0200, true)]
    [InlineData(3, 0, 0x0000, false)]
    [InlineData(3, 1, 0x0000, true)]
    [InlineData(3, 0, 0x0001, false)]
    [InlineData(3, 0, 0x1200, false)]
    [InlineData(3, 0, 0x0200, true)]
    [InlineData(3, 0, 0x1000, false)]
    public void FindLocationPlacementRequiresAnUpperLevelFloorUnlessSourceFlagsBypassIt(
        int level, int floorWord, int flags, bool expected)
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[level] = floorWord < 0
            ? RoomMapWithUpperFloorWord(level, 0, includeFloorLayer: false)
            : RoomMapWithUpperFloorWord(level, (ushort)floorWord);
        var item = world.Add(Object(14));
        var relativeTo = world.Add(Object(15));
        item[ObjectData.PlacementFlags] = (short)flags;
        var candidate = new ObjectPosition(0x18, 0x18, level);

        Assert.Equal(expected, world.CanPlaceObject(item, candidate));
        Assert.Equal(expected, world.CanPlaceCreatedObject(item, candidate));
        Assert.Equal(expected, world.CanPlaceCreatedObject(item, candidate, relativeTo));
    }

    private static RoomMap RoomMapWithGroundClass(int groundClass, bool includeWaterLayer = true)
    {
        const int width = 4;
        const int height = 4;
        var walls = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0);
        for (var x = 0; x < width; x++)
        for (var y = 0; y < height; y++)
            walls.Raw([0, 0x0f, 0, 0, 0, 0, 0, 0]);
        var floor = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(new byte[width * height]);
        var water = new byte[width * height];
        Array.Fill(water, (byte)0x10);
        water[1 * height + 1] = (byte)groundClass;
        var waterLayer = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0).Raw(water);
        var chunks = new List<(string Type, ushort Id, byte[] Body)>
        {
            ("Arry", 1, floor.ToArray()), ("Arry", 2, walls.ToArray()),
        };
        if (includeWaterLayer) chunks.Add(("Arry", 9, waterLayer.ToArray()));
        var file = new IffFile(ChunkBuilder.File(chunks.ToArray()), "placement-ground-class.iff");
        return RoomMap.Build(file, 1, width, height);
    }

    private static RoomMap RoomMapWithDiagonalWall(bool diagonal) =>
        RoomMapWithWallGeometry(diagonal ? (byte)0x10 : (byte)0, 0);

    private static RoomMap RoomMapWithWallGeometry(byte segments, byte sideCategory, int level = 1, byte floorWord = 0)
    {
        const int width = 4;
        const int height = 4;
        var wallBytes = new byte[width * height * 8];
        for (var index = 0; index < width * height; index++)
        {
            wallBytes[index * 8] = index == width + 1 ? segments : (byte)0;
            wallBytes[index * 8 + 1] = 0x0f;
            wallBytes[index * 8 + 2] = sideCategory;
            wallBytes[index * 8 + 3] = sideCategory;
        }
        var walls = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0).Raw(wallBytes);
        var floor = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(Enumerable.Repeat(floorWord, width * height).ToArray());
        var water = Enumerable.Repeat((byte)0x10, width * height).ToArray();
        var waterLayer = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0).Raw(water);
        var levelOffset = (level - 1) * 100;
        var chunks = new List<(string Type, ushort Id, byte[] Body)>
        {
            ("Arry", (ushort)(levelOffset + 1), floor.ToArray()),
            ("Arry", (ushort)(levelOffset + 2), walls.ToArray()),
        };
        if (level == 1) chunks.Add(("Arry", 9, waterLayer.ToArray()));
        return RoomMap.Build(new IffFile(ChunkBuilder.File(chunks.ToArray()), "placement-diagonal-wall.iff"), level, width, height);
    }

    private static RoomMap RoomMapWithUpperFloorWord(int level, ushort floorWord, bool includeFloorLayer = true)
    {
        const int width = 4;
        const int height = 4;
        var walls = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0);
        for (var x = 0; x < width; x++)
        for (var y = 0; y < height; y++)
            walls.Raw([0, 0x0f, 0, 0, 0, 0, 0, 0]);
        var floor = new byte[width * height];
        for (var x = 0; x < width; x++)
        for (var y = 0; y < height; y++)
            floor[y * height + x] = x == 1 && y == 1 ? (byte)floorWord : (byte)0;
        var chunks = new List<(string Type, ushort Id, byte[] Body)>
        {
            ("Arry", (ushort)((level - 1) * 100 + 2), walls.ToArray()),
        };
        if (includeFloorLayer)
        {
            var floorLayer = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0).Raw(floor).ToArray();
            chunks.Add(("Arry", (ushort)((level - 1) * 100 + 1), floorLayer));
        }
        var file = new IffFile(ChunkBuilder.File(chunks.ToArray()), "upper-placement-floor.iff");
        return RoomMap.Build(file, level, width, height);
    }

    [Fact]
    public void RawPositionUpdateDoesNotInventAPlacementGridWrite()
    {
        var world = new SimulationWorld();
        var root = world.Add(Object(9, new ObjectPosition(0x20, 0x30, 1)));

        world.SetObjectPosition(root, new ObjectPosition(0x41, 0x52, 2));

        Assert.Equal(root.ClockManagerIndex, world.TileValueAt(2, 3, 1));
        Assert.Equal(0, world.TileValueAt(4, 5, 2));
    }

    [Fact]
    public void TileObjectTraversalFollowsContainedSlotsInDepthFirstOrder()
    {
        var world = new SimulationWorld();
        var position = new ObjectPosition(0x20, 0x30, 1);
        var root = world.Add(Object(1, position));
        var childA = world.Add(Object(2, position, root.ClockManagerIndex));
        var childB = world.Add(Object(3, position, root.ClockManagerIndex));
        var grandchild = world.Add(Object(4, position, childA.ClockManagerIndex));
        root.Slots.AddRange([0, childA.ClockManagerIndex, childB.ClockManagerIndex]);
        childA.Slots.Add(grandchild.ClockManagerIndex);
        childA[ObjectData.ParentId] = root.ClockManagerIndex;
        childA[ObjectData.SlotHierNumber] = 1;
        childB[ObjectData.ParentId] = root.ClockManagerIndex;
        childB[ObjectData.SlotHierNumber] = 2;
        grandchild[ObjectData.ParentId] = childA.ClockManagerIndex;
        grandchild[ObjectData.SlotHierNumber] = 0;

        Assert.Equal([root, childA, grandchild, childB], world.ObjectsAtTile(2, 3, 1));
    }
}
