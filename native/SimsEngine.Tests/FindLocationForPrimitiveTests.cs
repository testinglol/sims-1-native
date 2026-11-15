using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using Xunit;

namespace SimsEngine.Tests;

public sealed class FindLocationForPrimitiveTests
{
    private sealed class Runner : TreeStack
    {
        public List<short> CompletedTrees { get; } = [];

        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction) => PrimitiveResult.False;
        protected override void Popped(StackFrame frame)
        {
            if (frame.TreeId > 0) CompletedTrees.Add(frame.TreeId);
        }
    }

    private static byte[] Operand(byte mode, byte local = 0, byte flags = 0) =>
        [mode, local, flags, 0, 0, 0, 0, 0];

    private static (TreeContext Context, PrimitiveTestHost Host, XObject Me, XObject Stack) Setup(
        short stackId = 5, short meId = 100, short meType = 0, int x = 32, int y = 48, int level = 0)
    {
        var host = new PrimitiveTestHost();
        var world = new SimulationWorld { Host = host };
        var me = world.Add(new XObject(meId, PrimitiveTestWorld.Definition(meType)));
        me.Position = new ObjectPosition(x, y, level);
        var stack = world.Add(new XObject(stackId, PrimitiveTestWorld.Definition()));
        var frame = new StackFrame(stackId, [], [0, 0]);
        return (new TreeContext(world, me, frame), host, me, stack);
    }

    private static void PlaceTarget(SimulationWorld world, short id, int x, int y, int level, short direction)
    {
        var target = world.Add(new XObject(id, PrimitiveTestWorld.Definition()));
        target.Position = new ObjectPosition(x, y, level);
        target[ObjectData.Direction] = direction;
    }

    [Fact]
    public void MissingStack_IsError15()
    {
        var host = new PrimitiveTestHost();
        var world = new SimulationWorld { Host = host };
        var me = world.Add(new XObject(100, PrimitiveTestWorld.Definition()));
        var context = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));
        var error = Assert.Throws<SimulationException>(() => FindLocationForPrimitive.Execute(context, Operand(1)));
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, error.Error);
    }

    [Fact]
    public void Bit0_LocalPastTheLocals_IsError51()
    {
        var (context, _, _, _) = Setup();
        var error = Assert.Throws<SimulationException>(() => FindLocationForPrimitive.Execute(context, Operand(2, local: 2, flags: 1)));
        Assert.Equal(SimulationError.LocalOutOfRange, error.Error);
    }

    [Fact]
    public void Bit0_MissingTarget_IsError23()
    {
        var (context, _, _, _) = Setup();
        var error = Assert.Throws<SimulationException>(() => FindLocationForPrimitive.Execute(context, Operand(2, flags: 1)));
        Assert.Equal(SimulationError.CouldNotFindObjectReference, error.Error);
    }

    [Fact]
    public void Mode1_PlacesUnconditionally()
    {
        var (context, host, _, _) = Setup();
        Assert.Equal(PrimitiveResult.True, FindLocationForPrimitive.Execute(context, Operand(1)));
        Assert.Equal(["FinalizePlacement(5)"], host.Calls);
    }

    [Fact]
    public void NativePlaceObjectRunsFunctionNineAfterMovingTheObject()
    {
        var (context, _, me, stackObject) = Setup(x: 32, y: 48, level: 1);
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        stackObject[ObjectData.AllowedHeightFlags] = 1;
        stackObject[ObjectData.PlacementFlags] = 0x0800;
        stackObject[ObjectData.Flags] = 4;
        stackObject[ObjectData.ContainerId] = 9;
        stackObject[ObjectData.SlotNumber] = 3;
        var functionTable = new ushort[10];
        functionTable[9] = 4096;
        stackObject.TypeRecord = new TypeRecord(stackObject.Id)
        {
            FunctionTable = functionTable,
            BehaviorScope = new TreeTable((4096, PrimitiveTestWorld.Tree(0,
                (BhavInstruction.ReturnFalse, 0xff, 0xff, new byte[8])))),
        };
        var runner = new Runner();
        runner.Frames.Add(context.Frame);
        context = context with { Stack = runner };
        Assert.Equal(PrimitiveResult.True, FindLocationForPrimitive.Execute(context, Operand(2)));

        Assert.Equal(me.Position, stackObject.Position);
        Assert.Equal((short)0, stackObject[ObjectData.ContainerId]);
        Assert.Equal((short)0, stackObject[ObjectData.SlotNumber]);
        Assert.Equal(BhavInstruction.ReturnFalse, runner.LastTarget);
        Assert.Single(runner.Frames);
    }

    [Fact]
    public void PlacementRunsFunctionNineOnItsContainerAfterThePlacedObject()
    {
        var (context, _, _, placed) = Setup(x: 32, y: 48, level: 1);
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        context.World.CaptureExecutionTrace = true;
        var container = context.World.Add(new XObject(6, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(0x38, 0x48, 1),
        });
        container.Slots.AddRange([0, 0, 0]);
        container.FootprintSlots.AddRange([new ObjectFootprintSlot(), new ObjectFootprintSlot()]);
        placed.TypeRecord = new TypeRecord(placed.Id) { BehaviorScope = new TreeTable() };
        var readContainerSlot = PrimitiveTestWorld.Tree(0,
            (2, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse,
                PrimitiveTestWorld.Expression(1, 0, (byte)ExpressionOperator.EqualTo, (byte)DataOwner.MySlot)));
        container.TypeRecord = new TypeRecord(container.Id)
        {
            FunctionTable = Enumerable.Range(0, 10).Select(index => index == 9 ? (ushort)4097 : (ushort)0).ToArray(),
            BehaviorScope = new TreeTable((4097, readContainerSlot)),
        };
        var runner = TreeRunner.CreateTransientStack(context.World, placed);

        context.World.PlaceObject(placed, new ObjectPosition(0x38, 0x48, 1), runner, container, 2);

        Assert.Equal(container.ClockManagerIndex, placed[ObjectData.ContainerId]);
        Assert.Equal((short)2, placed[ObjectData.SlotNumber]);
        Assert.Equal(container.ClockManagerIndex, placed[ObjectData.ParentId]);
        Assert.Equal((short)2, placed[ObjectData.SlotHierNumber]);
        Assert.Equal(placed.ClockManagerIndex, container.Slots[2]);
        Assert.Equal(placed.ClockManagerIndex, container.FootprintSlots[1].OccupantObjectId);
        Assert.Equal(container.ClockManagerIndex, context.World.TileValueAt(3, 4, 1));
        Assert.Contains(context.World.ExecutionTrace, entry => entry.ObjectId == container.Id && entry.Tree == 4097);
    }

    [Fact]
    public void PlacementMovesAnObjectBetweenSourceFootprintSlotLinks()
    {
        var world = new SimulationWorld();
        var oldContainer = world.Add(new XObject(1, PrimitiveTestWorld.Definition()));
        var newContainer = world.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(0x50, 0x50, 1),
        });
        var placed = world.Add(new XObject(3, PrimitiveTestWorld.Definition()));
        oldContainer.Slots.AddRange([0, placed.ClockManagerIndex]);
        oldContainer.FootprintSlots.Add(new ObjectFootprintSlot { OccupantObjectId = placed.ClockManagerIndex });
        newContainer.Slots.AddRange([0, 0, 0]);
        newContainer.FootprintSlots.AddRange([new ObjectFootprintSlot(), new ObjectFootprintSlot()]);
        placed[ObjectData.ParentId] = oldContainer.ClockManagerIndex;
        placed[ObjectData.SlotHierNumber] = 1;
        placed.Position = new ObjectPosition(0x30, 0x30, 1);

        world.PlaceObject(placed, new ObjectPosition(0x30, 0x30, 1),
            placementContainer: newContainer, placementSlotNumber: 2);

        Assert.Equal(0, oldContainer.Slots[1]);
        Assert.Equal(0, oldContainer.FootprintSlots[0].OccupantObjectId);
        Assert.Equal(placed.ClockManagerIndex, newContainer.Slots[2]);
        Assert.Equal(placed.ClockManagerIndex, newContainer.FootprintSlots[1].OccupantObjectId);
        Assert.Equal(newContainer.ClockManagerIndex, placed[ObjectData.ParentId]);
        Assert.Equal((short)2, placed[ObjectData.SlotHierNumber]);
        Assert.Equal(newContainer.Position, placed.Position);
        Assert.Equal(newContainer.ClockManagerIndex, world.TileValueAt(5, 5, 1));
    }

    [Fact]
    public void MultiTilePlacementRunsEachMemberCallbackAndThenEachMemberFunctionNine()
    {
        var (context, _, _, _) = Setup(x: 32, y: 48, level: 1);
        var root = context.World.Add(new XObject(5, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.MultiTileObject,
        });
        var partDefinition = PrimitiveTestWorld.Definition();
        partDefinition[(int)ObjdField.MasterId] = root.Id;
        var part = context.World.Add(new XObject(6, partDefinition)
        {
            RuntimeClass = ObjectClass.MultiTileObject,
            MultiTileMasterId = root.Id,
        });
        root.MultiTileNextId = part.Id;
        static TypeRecord WithFunctionNine(short key, short treeId) => new(key)
        {
            FunctionTable = Enumerable.Range(0, 10).Select(index => index == 9 ? (ushort)treeId : (ushort)0).ToArray(),
            BehaviorScope = new TreeTable((treeId, PrimitiveTestWorld.Tree(0,
                (2, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse,
                    PrimitiveTestWorld.Expression(1, 1, (byte)ExpressionOperator.EqualTo))))),
        };
        root.TypeRecord = WithFunctionNine(root.Id, 4096);
        part.TypeRecord = WithFunctionNine(part.Id, 4097);
        var container = context.World.Add(new XObject(7, PrimitiveTestWorld.Definition()));
        container.TypeRecord = WithFunctionNine(container.Id, 4099);
        var runner = new Runner();
        runner.Frames.Add(context.Frame);

        context.World.PlaceObject(root, new ObjectPosition(32, 48, 1), runner, container, 4);

        Assert.Equal(new short[] { 4096, 4096, 4097 }, runner.CompletedTrees);
        Assert.Equal((short)0, root[ObjectData.ContainerId]);
        Assert.Equal((short)0, part[ObjectData.ContainerId]);
        Assert.Equal((short)0, root[ObjectData.SlotNumber]);
        Assert.Equal((short)0, part[ObjectData.SlotNumber]);
        Assert.Equal(root.Position, part.Position);
    }

    [Fact]
    public void PortalPlacementRunsTheLinkedMemberCallbacksWithTheOriginalRequest()
    {
        var (context, _, _, _) = Setup(x: 32, y: 48, level: 1);
        context.World.CaptureExecutionTrace = true;
        var root = context.World.Add(new XObject(5, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
        });
        var partDefinition = PrimitiveTestWorld.Definition();
        partDefinition[(int)ObjdField.MasterId] = root.Id;
        var part = context.World.Add(new XObject(6, partDefinition)
        {
            RuntimeClass = ObjectClass.Portal,
            MultiTileMasterId = root.Id,
        });
        root.MultiTileNextId = part.Id;
        static TypeRecord WithFunctionNine(short key, short treeId) => new(key)
        {
            FunctionTable = Enumerable.Range(0, 10).Select(index => index == 9 ? (ushort)treeId : (ushort)0).ToArray(),
            BehaviorScope = new TreeTable((treeId, PrimitiveTestWorld.Tree(0,
                (2, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse,
                    PrimitiveTestWorld.Expression(1, 1, (byte)ExpressionOperator.EqualTo))))),
        };
        root.TypeRecord = WithFunctionNine(root.Id, 4096);
        part.TypeRecord = WithFunctionNine(part.Id, 4097);
        var container = context.World.Add(new XObject(7, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(32, 48, 1),
        });
        container.Slots.AddRange(new short[5]);
        container.FootprintSlots.AddRange(Enumerable.Range(0, 4).Select(_ => new ObjectFootprintSlot()));
        container.TypeRecord = WithFunctionNine(container.Id, 4098);
        root.SetPortalRouteCost(2, 1004f);
        part.SetPortalRouteCost(2, 1101f);
        var runner = new Runner();
        runner.Frames.Add(context.Frame);

        context.World.PlaceObject(root, new ObjectPosition(32, 48, 1), runner, container, 4);

        Assert.Equal(new short[] { 4096, 4097 }, runner.CompletedTrees);
        Assert.Contains(context.World.ExecutionTrace, entry => entry.ObjectId == container.Id && entry.Tree == 4096);
        Assert.Equal(2, context.World.ExecutionTrace.Count(entry => entry.ObjectId == container.Id && entry.Tree == 4098));
        Assert.Equal(container.ClockManagerIndex, root[ObjectData.ContainerId]);
        Assert.Equal(container.ClockManagerIndex, part[ObjectData.ContainerId]);
        Assert.Equal((short)4, root[ObjectData.SlotNumber]);
        Assert.Equal((short)4, part[ObjectData.SlotNumber]);
        Assert.Equal(part.ClockManagerIndex, container.Slots[4]);
        Assert.Equal(XObject.UninitializedPortalRouteCost, root.PortalRouteCostAt(2));
        Assert.Equal(XObject.UninitializedPortalRouteCost, part.PortalRouteCostAt(2));
    }

    [Fact]
    public void PortalPlacementRequiresCategoryOneAtStyledMemberWall()
    {
        var (context, _, _, _) = Setup(x: 32, y: 48, level: 1);
        var definition = PrimitiveTestWorld.Definition();
        definition[(int)ObjdField.WallStyle] = 1;
        var portal = context.World.Add(new XObject(5, definition) { RuntimeClass = ObjectClass.Portal });
        portal[ObjectData.AllowedHeightFlags] = 1;
        portal[ObjectData.PlacementFlags] = 0x0800;
        portal[ObjectData.WallPlacementFlags] = 1;
        context.World.LotRoomMaps[1] = RoomWithWallAt(3, 4, segment: 2, category: 3);

        Assert.False(context.World.CanPlaceObject(portal, new ObjectPosition(56, 72, 1)));
        Assert.Equal(0x16, context.World.LastPortalPlacementFailureCode);

        context.World.LotRoomMaps[1] = RoomWithWallAt(3, 4, segment: 2, category: 1);
        Assert.True(context.World.CanPlaceObject(portal, new ObjectPosition(56, 72, 1)));
        Assert.Equal(0, context.World.LastPortalPlacementFailureCode);
    }

    [Fact]
    public void PortalStyledWallCheckRejectsWhenTheLevelWallMapIsMissing()
    {
        var (context, _, _, _) = Setup(x: 32, y: 48, level: 1);
        var definition = PrimitiveTestWorld.Definition();
        definition[(int)ObjdField.WallStyle] = 1;
        var portal = context.World.Add(new XObject(5, definition) { RuntimeClass = ObjectClass.Portal });
        portal[ObjectData.AllowedHeightFlags] = 1;
        portal[ObjectData.PlacementFlags] = 0x0800;
        portal[ObjectData.WallPlacementFlags] = 1;

        Assert.False(context.World.CanPlaceObject(portal, new ObjectPosition(56, 72, 1)));
        Assert.Equal(0x16, context.World.LastPortalPlacementFailureCode);
    }

    [Fact]
    public void PortalPlacementChecksStyledLinkedMemberAtItsSavedOffset()
    {
        var (context, _, _, _) = Setup(x: 32, y: 48, level: 1);
        var root = context.World.Add(new XObject(5, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
        });
        root[ObjectData.AllowedHeightFlags] = 1;
        root[ObjectData.PlacementFlags] = 0x0800;
        var partDefinition = PrimitiveTestWorld.Definition();
        partDefinition[(int)ObjdField.MasterId] = root.Id;
        partDefinition[(int)ObjdField.WallStyle] = 1;
        var part = context.World.Add(new XObject(6, partDefinition)
        {
            RuntimeClass = ObjectClass.Portal,
            MultiTileMasterId = root.Id,
            SavedMultiTileState = new MultiTileState(2, 1, 0, 0, 0, 0, root.Id, null),
        });
        root.MultiTileNextId = part.Id;
        part[ObjectData.AllowedHeightFlags] = 1;
        part[ObjectData.PlacementFlags] = 0x0800;
        part[ObjectData.WallPlacementFlags] = 1;
        context.World.LotRoomMaps[1] = RoomWithWallAt(7, 6, segment: 2, category: 3);

        Assert.False(context.World.CanPlaceObject(root, new ObjectPosition(88, 88, 1)));

        context.World.LotRoomMaps[1] = RoomWithWallAt(7, 6, segment: 2, category: 1);
        Assert.True(context.World.CanPlaceObject(root, new ObjectPosition(88, 88, 1)));
    }

    [Fact]
    public void PortalPlacementRequiresTileCenterCoordinates()
    {
        var (context, _, _, _) = Setup(x: 32, y: 48, level: 1);
        var portal = context.World.Add(new XObject(5, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
        });
        portal[ObjectData.AllowedHeightFlags] = 1;
        portal[ObjectData.PlacementFlags] = 0x0800;
        context.World.LotRoomMaps[1] = OpenRoom();

        Assert.True(context.World.CanPlaceObject(portal, new ObjectPosition(56, 72, 1)));
        Assert.False(context.World.CanPlaceObject(portal, new ObjectPosition(55, 72, 1)));
        Assert.Equal(3, context.World.LastPortalPlacementFailureCode);
        Assert.False(context.World.CanPlaceObject(portal, new ObjectPosition(56, 73, 1)));
        Assert.Equal(3, context.World.LastPortalPlacementFailureCode);
    }

    [Fact]
    public void PlacementRefreshesWallAdjacencyAndRunsFunctionSixWhenItChanges()
    {
        var (context, _, _, placed) = Setup(x: 32, y: 48, level: 1);
        context.World.LotRoomMaps[1] = OpenRoom();
        placed[ObjectData.WallAdjacencyFlags] = 0x0001;
        placed.TypeRecord = new TypeRecord(placed.Id)
        {
            FunctionTable = Enumerable.Range(0, 10).Select(index => index switch
            {
                6 => (ushort)4098,
                9 => (ushort)4099,
                _ => (ushort)0,
            }).ToArray(),
            BehaviorScope = new TreeTable(
                (4098, PrimitiveTestWorld.Tree(0, (BhavInstruction.ReturnFalse, 0xff, 0xff, new byte[8]))),
                (4099, PrimitiveTestWorld.Tree(0, (BhavInstruction.ReturnFalse, 0xff, 0xff, new byte[8])))),
        };
        var runner = new Runner();
        runner.Frames.Add(context.Frame);

        context.World.PlaceObject(placed, new ObjectPosition(32, 48, 1), runner);

        Assert.Equal((short)0, placed[ObjectData.WallAdjacencyFlags]);
        Assert.Equal(new short[] { 4098, 4099 }, runner.CompletedTrees);
    }

    [Fact]
    public void PlacementSkipsWallAdjacencyRefreshWhenFunctionSixIsMissing()
    {
        var (context, _, _, placed) = Setup(x: 32, y: 48, level: 1);
        context.World.LotRoomMaps[1] = OpenRoom();
        placed[ObjectData.WallAdjacencyFlags] = 0x0001;
        placed.TypeRecord = new TypeRecord(placed.Id)
        {
            FunctionTable = Enumerable.Range(0, 10).Select(index => index == 9 ? (ushort)4099 : (ushort)0).ToArray(),
            BehaviorScope = new TreeTable(
                (4099, PrimitiveTestWorld.Tree(0, (BhavInstruction.ReturnFalse, 0xff, 0xff, new byte[8])))),
        };
        var runner = new Runner();
        runner.Frames.Add(context.Frame);

        context.World.PlaceObject(placed, new ObjectPosition(32, 48, 1), runner);

        Assert.Equal((short)1, placed[ObjectData.WallAdjacencyFlags]);
        Assert.Equal(new short[] { 4099 }, runner.CompletedTrees);
    }

    [Fact]
    public void PlacementWallAdjacencyUsesTheSourceCardinalRotationTable()
    {
        var (context, _, _, placed) = Setup(x: 32, y: 48, level: 1);
        context.World.LotRoomMaps[1] = RoomWithWallAt(2, 3, segment: 2, category: 1);
        var expandedWall = context.World.LotRoomMaps[1].ExpandedWallAt(2, 3)!.Value;
        Assert.Equal((uint)2, expandedWall.UInt32At(0));
        Assert.Equal((ushort)1, expandedWall.WallCategory(2));
        placed[ObjectData.Direction] = 2;
        placed.TypeRecord = new TypeRecord(placed.Id)
        {
            FunctionTable = Enumerable.Range(0, 10).Select(index => index == 6 ? (ushort)4098 : (ushort)0).ToArray(),
            BehaviorScope = new TreeTable((4098, PrimitiveTestWorld.Tree(0,
                (BhavInstruction.ReturnFalse, 0xff, 0xff, new byte[8])))),
        };
        var runner = new Runner();
        runner.Frames.Add(context.Frame);

        Assert.Equal((short)4098, SimulationWorld.FunctionTreeId(placed, 6));
        context.World.PlaceObject(placed, new ObjectPosition(32, 48, 1), runner);

        Assert.Equal((short)1, placed[ObjectData.WallAdjacencyFlags]);
        Assert.Equal(new short[] { 4098 }, runner.CompletedTrees);
    }

    [Theory]
    [InlineData(1, 0x10)]
    [InlineData(4, 0x20)]
    public void PlacementWallAdjacencyReadsTheNextLevelSideFlags(byte upperSegment, short expectedFlag)
    {
        var (context, _, _, placed) = Setup(x: 32, y: 48, level: 0);
        context.World.LotRoomMaps[0] = OpenRoom();
        context.World.LotRoomMaps[1] = RoomWithWallAt(2, 3, upperSegment, category: 1);
        placed.TypeRecord = new TypeRecord(placed.Id)
        {
            FunctionTable = Enumerable.Range(0, 10).Select(index => index == 6 ? (ushort)4098 : (ushort)0).ToArray(),
            BehaviorScope = new TreeTable((4098, PrimitiveTestWorld.Tree(0,
                (BhavInstruction.ReturnFalse, 0xff, 0xff, new byte[8])))),
        };
        var runner = new Runner();
        runner.Frames.Add(context.Frame);

        context.World.PlaceObject(placed, new ObjectPosition(32, 48, 0), runner);

        Assert.Equal(expectedFlag, placed[ObjectData.WallAdjacencyFlags]);
        Assert.Equal(new short[] { 4098 }, runner.CompletedTrees);
    }

    [Fact]
    public void PlacementWallAdjacencyDoesNotReadAboveTheUpperLevel()
    {
        var (context, _, _, placed) = Setup(x: 32, y: 48, level: 1);
        context.World.LotRoomMaps[1] = OpenRoom();
        context.World.LotRoomMaps[2] = RoomWithWallAt(2, 3, segment: 1, category: 1);
        placed.TypeRecord = new TypeRecord(placed.Id)
        {
            FunctionTable = Enumerable.Range(0, 10).Select(index => index == 6 ? (ushort)4098 : (ushort)0).ToArray(),
            BehaviorScope = new TreeTable((4098, PrimitiveTestWorld.Tree(0,
                (BhavInstruction.ReturnFalse, 0xff, 0xff, new byte[8])))),
        };
        var runner = new Runner();
        runner.Frames.Add(context.Frame);

        context.World.PlaceObject(placed, new ObjectPosition(32, 48, 1), runner);

        Assert.Equal((short)0, placed[ObjectData.WallAdjacencyFlags]);
        Assert.Empty(runner.CompletedTrees);
    }

    [Fact]
    public void PlacementWallAdjacencyReadsTheCachedWallGridAtTheObjectsHeight()
    {
        var (context, _, _, placed) = Setup(x: 32, y: 48, level: 1);
        context.World.LotRoomMaps[0] = OpenRoom();
        context.World.LotRoomMaps[1] = RoomWithWallAt(2, 3, segment: 1, category: 1);
        placed.TypeRecord = new TypeRecord(placed.Id)
        {
            FunctionTable = Enumerable.Range(0, 10).Select(index => index == 6 ? (ushort)4098 : (ushort)0).ToArray(),
            BehaviorScope = new TreeTable((4098, PrimitiveTestWorld.Tree(0,
                (BhavInstruction.ReturnFalse, 0xff, 0xff, new byte[8])))),
        };
        var runner = new Runner();
        runner.Frames.Add(context.Frame);

        context.World.PlaceObject(placed, new ObjectPosition(32, 48, 1), runner);

        Assert.Equal((short)1, placed[ObjectData.WallAdjacencyFlags]);
        Assert.Equal(new short[] { 4098 }, runner.CompletedTrees);
    }

    [Fact]
    public void NativeMode1RunsTheSourceRemovalCallback()
    {
        var (context, _, _, stack) = Setup();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        context.World.PlaceObject(stack, new ObjectPosition(32, 48, 1));
        stack[ObjectData.Flags] = 0x0101;
        stack[ObjectData.FlagField2] = 0x0400;
        var notices = new List<(int Message, int Value, ObjectPosition Position)>();
        context.World.AddNoticeListener(new SimulationNoticeListener((message, value) =>
        {
            notices.Add((message, value, stack.Position));
            return false;
        }));

        Assert.Equal(PrimitiveResult.True, FindLocationForPrimitive.Execute(context, Operand(1)));

        Assert.Equal([(0xf2, stack.ClockManagerIndex, new ObjectPosition(32, 48, 1))], notices);
        Assert.Equal(new ObjectPosition(-16, -16, 1), stack.Position);
        Assert.Equal(0, context.World.TileValueAt(2, 3, 1));
        Assert.Equal((short)-1, stack.RemovalCallbackWord14);
        Assert.Equal((short)0, stack[ObjectData.ContainerId]);
        Assert.Equal((short)0, stack[ObjectData.Flags] & 0x0100);
        Assert.True(context.World.DisplayRefreshRequested);
    }

    [Fact]
    public void NativeMode1DoesNotBroadcastRemovalNoticeWithoutFlagField2Bit10()
    {
        var (context, _, _, stack) = Setup();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        context.World.AddNoticeListener(new SimulationNoticeListener((_, _) =>
            throw new Xunit.Sdk.XunitException("Unflagged removal must not emit notice 0xf2.")));

        Assert.Equal(PrimitiveResult.True, FindLocationForPrimitive.Execute(context, Operand(1)));
        Assert.Equal(new ObjectPosition(-16, -16, 1), stack.Position);
    }

    [Fact]
    public void NativeMode1UnlinksASelectedMultitilePartBeforeRemoval()
    {
        var (context, _, me, stack) = Setup();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        me.TypeRecord = new TypeRecord(100) { FunctionTable = FunctionTable(8) };
        stack.Definition[(int)ObjdField.MasterId] = 1;
        stack.RuntimeClass = SimsEngine.Formats.ObjectClass.MultiTileObject;

        var root = context.World.Add(new XObject(6, PrimitiveTestWorld.Definition()) { MultiTileNextId = stack.Id });
        var tail = context.World.Add(new XObject(7, PrimitiveTestWorld.Definition())
        {
            MultiTileMasterId = root.Id,
        });
        stack.MultiTileMasterId = root.Id;
        stack.MultiTileNextId = tail.Id;

        Assert.Equal(PrimitiveResult.True, FindLocationForPrimitive.Execute(context, Operand(1)));

        Assert.Equal(tail.Id, root.MultiTileNextId);
        Assert.Equal(root.Id, tail.MultiTileMasterId);
        Assert.Equal((short)0, stack.MultiTileMasterId);
        Assert.Equal((short)0, stack.MultiTileNextId);
    }

    [Fact]
    public void NativeMode1PromotesTheFirstPartWhenRemovingAMultitileLead()
    {
        var (context, _, me, root) = Setup();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        me.TypeRecord = new TypeRecord(100) { FunctionTable = FunctionTable(8) };
        root.Definition[(int)ObjdField.MasterId] = 1;
        root.RuntimeClass = SimsEngine.Formats.ObjectClass.MultiTileObject;
        var first = context.World.Add(new XObject(6, PrimitiveTestWorld.Definition())
        {
            MultiTileMasterId = root.Id,
            MultiTileNextId = 7,
        });
        var second = context.World.Add(new XObject(7, PrimitiveTestWorld.Definition())
        {
            MultiTileMasterId = root.Id,
        });
        root.MultiTileNextId = first.Id;

        Assert.Equal(PrimitiveResult.True, FindLocationForPrimitive.Execute(context, Operand(1)));

        Assert.Equal((short)0, root.MultiTileMasterId);
        Assert.Equal((short)0, root.MultiTileNextId);
        Assert.Equal((short)0, first.MultiTileMasterId);
        Assert.Equal(second.Id, first.MultiTileNextId);
        Assert.Equal(first.Id, second.MultiTileMasterId);
    }

    [Fact]
    public void NativeMode1KeepsTheMultitileChainWhenFunction8IsAbsent()
    {
        var (context, _, _, stack) = Setup();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        stack.RuntimeClass = SimsEngine.Formats.ObjectClass.MultiTileObject;
        var root = context.World.Add(new XObject(6, PrimitiveTestWorld.Definition())
        {
            MultiTileNextId = stack.Id,
        });
        stack.MultiTileMasterId = root.Id;

        Assert.Equal(PrimitiveResult.True, FindLocationForPrimitive.Execute(context, Operand(1)));

        Assert.Equal(stack.Id, root.MultiTileNextId);
        Assert.Equal(root.Id, stack.MultiTileMasterId);
    }

    [Fact]
    public void Mode1_MultitileAlignsWithFunction8()
    {
        var (context, host, me, stack) = Setup();
        stack.Definition[(int)SimsEngine.Formats.ObjdField.MasterId] = 1;
        me.TypeRecord = new TypeRecord(1) { FunctionTable = FunctionTable(8) };
        Assert.Equal(PrimitiveResult.True, FindLocationForPrimitive.Execute(context, Operand(1)));
        Assert.Equal(["AlignMultitileParts(5)", "FinalizePlacement(5)"], host.Calls);
    }

    private static ushort[] FunctionTable(int function)
    {
        var table = new ushort[0x20];
        table[function] = 1;
        return table;
    }

    private static RoomMap OpenRoom()
    {
        var walls = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(8).U32(0);
        for (var tile = 0; tile < 64 * 64; tile++) walls.Raw(new byte[8]);
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, walls.ToArray())), "find-location-test.iff");
        return RoomMap.Build(file, 1, 64, 64);
    }

    private static RoomMap RoomWithWallAt(int tileX, int tileY, byte segment, byte category)
    {
        var walls = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(8).U32(0);
        var records = new byte[64 * 64 * 8];
        // Arry 2 is indexed [x][y], so the saved row-major array has x as its outer row.
        var index = (tileX * 64 + tileY) * 8;
        records[index] = segment;
        var categoryOffset = segment switch { 1 => 2, 2 => 3, 4 => 4, 8 => 5, _ => throw new ArgumentOutOfRangeException(nameof(segment)) };
        records[index + categoryOffset] = category;
        walls.Raw(records);
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, walls.ToArray())), "find-location-wall-test.iff");
        return RoomMap.Build(file, 1, 64, 64);
    }

    [Fact]
    public void Mode1_MultitileWithoutFunction8_SkipsAlign()
    {
        var (context, host, _, stack) = Setup();
        stack.Definition[(int)SimsEngine.Formats.ObjdField.MasterId] = 1;
        Assert.Equal(PrimitiveResult.True, FindLocationForPrimitive.Execute(context, Operand(1)));
        Assert.Equal(["FinalizePlacement(5)"], host.Calls);
    }

    [Fact]
    public void Mode2_RotatesAndPlacesAtMe()
    {
        var (context, host, me, stack) = Setup();
        me[ObjectData.Direction] = 2;
        stack[ObjectData.Direction] = 1;
        Assert.Equal(PrimitiveResult.True, FindLocationForPrimitive.Execute(context, Operand(2)));
        Assert.Equal(
            ["RotateForPlacement(5,3)", "CanPlaceAt(5,32,48,0)", "PlaceAt(5,32,48,0)", "FinalizePlacement(5)"],
            host.Calls);
    }

    [Fact]
    public void Mode2_Bit0Target_PlacesAtTheTarget()
    {
        var (context, host, _, stack) = Setup();
        PlaceTarget(context.World, 9, 64, 64, 1, 6);
        context.Frame.Locals[0] = 9;
        stack[ObjectData.Direction] = 1;
        Assert.Equal(PrimitiveResult.True, FindLocationForPrimitive.Execute(context, Operand(2, flags: 1)));
        Assert.Equal(
            ["RotateForPlacement(5,1)", "CanPlaceAt(5,64,64,1)", "PlaceAt(5,64,64,1)", "FinalizePlacement(5)"],
            host.Calls);
    }

    [Fact]
    public void Mode2_RefusedCheck_AnswersFalse()
    {
        var (context, host, _, _) = Setup();
        host.PlacementAnswer = false;
        Assert.Equal(PrimitiveResult.False, FindLocationForPrimitive.Execute(context, Operand(2)));
        Assert.Equal(["RotateForPlacement(5,2)", "CanPlaceAt(5,32,48,0)"], host.Calls);
    }

    [Fact]
    public void Mode3_SearchesInTheTargetsDirection()
    {
        var (context, host, _, _) = Setup();
        PlaceTarget(context.World, 9, 64, 64, 1, 2);
        context.Frame.Locals[0] = 9;
        host.FoundTile = new ObjectPosition(70, 70, 0);
        Assert.Equal(PrimitiveResult.True, FindLocationForPrimitive.Execute(context, Operand(3, flags: 1)));
        Assert.Equal(
            ["FindLocation(5,64,64,1,2,1,1,0)", "CanPlaceAt(5,70,70,1)", "PlaceAt(5,70,70,1)", "FinalizePlacement(5)"],
            host.Calls);
    }

    [Fact]
    public void Mode4_TurnsTheDirectionByTwo()
    {
        var (context, host, _, _) = Setup();
        PlaceTarget(context.World, 9, 64, 64, 1, 2);
        context.Frame.Locals[0] = 9;
        host.FoundTile = new ObjectPosition(70, 70, 0);
        Assert.Equal(PrimitiveResult.True, FindLocationForPrimitive.Execute(context, Operand(4, flags: 1)));
        Assert.Equal("FindLocation(5,64,64,1,4,1,1,0)", host.Calls[0]);
    }

    [Fact]
    public void Modes3And4_WithoutATarget_AreError61()
    {
        var (context, _, _, _) = Setup();
        foreach (var mode in new byte[] { 3, 4 })
        {
            var error = Assert.Throws<SimulationException>(() => FindLocationForPrimitive.Execute(context, Operand(mode)));
            Assert.Equal(SimulationError.StartObjectRequired, error.Error);
        }
    }

    [Fact]
    public void Mode3_FailedSearch_AnswersFalse()
    {
        var (context, host, _, _) = Setup();
        PlaceTarget(context.World, 9, 64, 64, 1, 2);
        context.Frame.Locals[0] = 9;
        host.FindLocationAnswer = false;
        Assert.Equal(PrimitiveResult.False, FindLocationForPrimitive.Execute(context, Operand(3, flags: 1)));
        Assert.Single(host.Calls);
    }

    [Fact]
    public void Mode5_RetriesTheSearchOnce()
    {
        var (context, host, _, _) = Setup();
        host.FindLocationAnswer = false;
        Assert.Equal(PrimitiveResult.False, FindLocationForPrimitive.Execute(context, Operand(5)));
        Assert.Equal(
            ["FindLocation(5,16,16,1,-1,1,1,0)", "FindLocation(5,16,16,1,-1,0,1,0)"],
            host.Calls);
    }

    [Fact]
    public void Mode5_SuccessSettlesTheSearch()
    {
        var (context, host, _, _) = Setup();
        host.FoundTile = new ObjectPosition(20, 20, 0);
        Assert.Equal(PrimitiveResult.True, FindLocationForPrimitive.Execute(context, Operand(5)));
        Assert.Equal(
            ["FindLocation(5,16,16,1,-1,1,1,0)", "CanPlaceAt(5,20,20,1)", "PlaceAt(5,20,20,1)", "FinishLocationSearch(5)", "FinalizePlacement(5)"],
            host.Calls);
    }

    [Fact]
    public void Mode5_FlagBitsPassThrough()
    {
        var (context, host, _, _) = Setup();
        PlaceTarget(context.World, 9, 64, 64, 1, 2);
        context.Frame.Locals[0] = 9;
        host.FoundTile = new ObjectPosition(20, 20, 0);
        Assert.Equal(PrimitiveResult.True, FindLocationForPrimitive.Execute(context, Operand(5, flags: 7)));
        Assert.Equal("FindLocation(5,64,64,1,-1,1,0,1)", host.Calls[0]);
    }

    [Fact]
    public void UnknownMode_SearchesLikeMode5()
    {
        var (context, host, _, _) = Setup();
        host.FoundTile = new ObjectPosition(20, 20, 0);
        Assert.Equal(PrimitiveResult.True, FindLocationForPrimitive.Execute(context, Operand(0)));
        Assert.Equal(5, host.Calls.Count);
        Assert.Equal("FindLocation(5,16,16,1,-1,1,1,0)", host.Calls[0]);
    }
}
