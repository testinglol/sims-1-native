using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using Xunit;

namespace SimsEngine.Tests;

public sealed class WorldTests
{
    private static XObject CreateObject(short id, ObjectPosition position = default)
    {
        var definition = new short[XObject.DefinitionCount];
        var item = new XObject(id, definition)
        {
            Position = position,
            TypeRecord = new TypeRecord(id),
        };
        item[ObjectData.AllowedHeightFlags] = 1;
        item[ObjectData.MovementFlags] = 0x0a;
        return item;
    }

    [Fact]
    public void AdvanceClockMidnightRolloverAppliesInitialAndDailyDepreciationWithFloorAndSkipsSelfDepreciatingAndPeople()
    {
        var world = new SimulationWorld();
        world.Clock.Restore(23, 59, 59);

        var sofa = CreateObject(1, new ObjectPosition(0x18, 0x18, 1));
        sofa.Definition[(int)ObjdField.Price] = 500;
        sofa.Definition[(int)ObjdField.InitialDepreciation] = 100;
        sofa.Definition[(int)ObjdField.DailyDepreciation] = 40;
        sofa.Definition[(int)ObjdField.DepreciationLimit] = 350;
        sofa[ObjectData.CurrentValue] = 500;
        world.Add(sofa);

        var painting = CreateObject(2, new ObjectPosition(0x28, 0x18, 1));
        painting.Definition[(int)ObjdField.Price] = 400;
        painting.Definition[(int)ObjdField.InitialDepreciation] = 50;
        painting.Definition[(int)ObjdField.DailyDepreciation] = 20;
        painting.Definition[(int)ObjdField.DepreciationLimit] = 100;
        painting.Definition[(int)ObjdField.SelfDepreciating] = 1;
        painting[ObjectData.CurrentValue] = 400;
        world.Add(painting);

        var personDef = new short[XObject.DefinitionCount];
        personDef[(int)ObjdField.Type] = XPerson.PersonType;
        personDef[(int)ObjdField.Price] = 100;
        personDef[(int)ObjdField.InitialDepreciation] = 10;
        personDef[(int)ObjdField.DailyDepreciation] = 5;
        var person = new XPerson(3, personDef)
        {
            Position = new ObjectPosition(0x38, 0x18, 1),
            TypeRecord = new TypeRecord(3) { ObjectType = XPerson.PersonType },
        };
        person[ObjectData.CurrentValue] = 100;
        world.Add(person);

        world.AdvanceClock();
        Assert.Equal(400, sofa[ObjectData.CurrentValue]);
        Assert.Equal(400, painting[ObjectData.CurrentValue]);
        Assert.Equal(100, person[ObjectData.CurrentValue]);

        world.Clock.Restore(23, 59, 59);
        world.AdvanceClock();
        Assert.Equal(360, sofa[ObjectData.CurrentValue]);

        world.Clock.Restore(23, 59, 59);
        world.AdvanceClock();
        Assert.Equal(350, sofa[ObjectData.CurrentValue]);
    }

    [Fact]
    public void AdvanceClockMidnightRolloverOnlyDepreciatesMultiTileLeadObject()
    {
        var world = new SimulationWorld();
        world.Clock.Restore(23, 59, 59);

        var lead = CreateObject(10, new ObjectPosition(0x18, 0x18, 1));
        lead.RuntimeClass = ObjectClass.MultiTileObject;
        lead.Definition[(int)ObjdField.MasterId] = 7;
        lead.Definition[(int)ObjdField.Price] = 800;
        lead.Definition[(int)ObjdField.InitialDepreciation] = 120;
        lead.Definition[(int)ObjdField.DailyDepreciation] = 30;
        lead.Definition[(int)ObjdField.DepreciationLimit] = 200;
        lead[ObjectData.CurrentValue] = 800;

        var part = CreateObject(11, new ObjectPosition(0x28, 0x18, 1));
        part.RuntimeClass = ObjectClass.MultiTileObject;
        part.Definition[(int)ObjdField.MasterId] = 7;
        part.Definition[(int)ObjdField.Price] = 800;
        part.Definition[(int)ObjdField.InitialDepreciation] = 120;
        part.Definition[(int)ObjdField.DailyDepreciation] = 30;
        part.Definition[(int)ObjdField.DepreciationLimit] = 200;
        part[ObjectData.CurrentValue] = 800;

        lead.MultiTileMasterId = lead.Id;
        lead.MultiTileNextId = part.Id;
        part.MultiTileMasterId = lead.Id;
        world.Add(lead);
        world.Add(part);

        world.AdvanceClock();
        Assert.Equal(680, lead[ObjectData.CurrentValue]);
        Assert.Equal(800, part[ObjectData.CurrentValue]);
    }

    [Fact]
    public void CanPickUpObjectAndCanSellObjectRespectMoveObjectsAndBuildableLotBoundary()
    {
        const int size = 64;
        var world = new SimulationWorld();
        var tileBytes = new byte[size * size];
        tileBytes[2 * size + 3] = 0x20;
        var tiles = new ChunkBuilder().S16(0).S16(0).S32(size).S32(size).S32(1).U32(0).Raw(tileBytes).ToArray();
        world.LotTileFlags = LotTiles.Read(new IffFile(ChunkBuilder.File(("Arry", 8, tiles)), "tiles.iff"));

        var outsideItem = world.Add(CreateObject(20, new ObjectPosition(2 * 16 + 8, 3 * 16 + 8, 1)));
        Assert.False(world.CanPickUpObject(outsideItem));
        Assert.Equal(0x27, world.LastPlacementFailureCode);
        Assert.False(world.CanSellObject(outsideItem));

        var lockedItem = world.Add(CreateObject(21, new ObjectPosition(4 * 16 + 8, 4 * 16 + 8, 1)));
        lockedItem[ObjectData.MovementFlags] = 0;
        lockedItem[ObjectData.UseCount] = 1;
        Assert.False(world.CanPickUpObject(lockedItem));
        Assert.Equal(0x25, world.LastPlacementFailureCode);
        Assert.False(world.CanSellObject(lockedItem));

        world.MoveObjects = true;
        Assert.True(world.CanPickUpObject(outsideItem));
        Assert.True(world.CanSellObject(outsideItem));
        Assert.True(world.CanPickUpObject(lockedItem));
        Assert.True(world.CanSellObject(lockedItem));
    }

    [Fact]
    public void XObjectInitializesRotationNotchesToTwoAndRotateObjectForPlacementUsesRotationNotches()
    {
        var world = new SimulationWorld();
        var item = world.Add(CreateObject(30, new ObjectPosition(0x18, 0x18, 1)));
        Assert.Equal(2, item[ObjectData.RotationNotches]);

        world.RotateObjectForPlacement(item, 1);
        Assert.Equal(2, item[ObjectData.Direction]);

        item[ObjectData.RotationNotches] = 1;
        world.RotateObjectForPlacement(item, 1);
        Assert.Equal(3, item[ObjectData.Direction]);

        item[ObjectData.RotationNotches] = 0;
        world.RotateObjectForPlacement(item, 1);
        Assert.Equal(3, item[ObjectData.Direction]);
    }

    [Fact]
    public void PlaceCreatedObjectFromEditorRunsUserPlacementActionOnlyAndIgnoresCheckTree()
    {
        var world = new SimulationWorld();
        var item = world.Add(CreateObject(40, new ObjectPosition(0x18, 0x18, 1)));
        var actions = new ushort[12];
        var checks = new ushort[12];
        checks[11] = 4096;
        var tree = PrimitiveTestWorld.Tree(0, (2, 0xFE, 0xFF, PrimitiveTestWorld.Expression(0, 77, 5)));
        item.TypeRecord = new TypeRecord(40)
        {
            FunctionTable = actions,
            FunctionCheckTable = checks,
            BehaviorScope = new TreeTable((4096, tree)),
        };

        world.PlaceCreatedObjectFromEditor(item, new ObjectPosition(0x28, 0x28, 1));
        Assert.Equal(0, item.Temporaries[0]);

        actions[11] = 4096;
        item.TypeRecord = new TypeRecord(40)
        {
            FunctionTable = actions,
            FunctionCheckTable = checks,
            BehaviorScope = new TreeTable((4096, tree)),
        };
        world.PlaceCreatedObjectFromEditor(item, new ObjectPosition(0x38, 0x38, 1));
        Assert.Equal(77, item.Temporaries[0]);
    }

    [Fact]
    public void MoveObjectFromEditorRunsFunctionNineOnOldContainerWhenDetaching()
    {
        var world = new SimulationWorld();
        var containerPos = new ObjectPosition(0x18, 0x18, 1);
        var container = world.Add(CreateObject(50, containerPos));
        var containerFunctions = new ushort[10];
        containerFunctions[9] = 4096;
        var containerTree = PrimitiveTestWorld.Tree(0, (2, 0xFE, 0xFF, PrimitiveTestWorld.Expression(0, 91, 5)));
        container.TypeRecord = new TypeRecord(50)
        {
            FunctionTable = containerFunctions,
            BehaviorScope = new TreeTable((4096, containerTree)),
        };

        var child = world.Add(CreateObject(51, containerPos));
        container.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1, OccupantObjectId = child.ClockManagerIndex });
        container.Slots.AddRange([0, child.ClockManagerIndex]);
        child[ObjectData.ContainerId] = container.ClockManagerIndex;
        child[ObjectData.SlotNumber] = 1;
        child[ObjectData.ParentId] = container.ClockManagerIndex;
        child[ObjectData.SlotHierNumber] = 1;

        var checkStack = TreeRunner.CreateTransientStack(world, child);
        Assert.True(world.MoveObjectFromEditor(child, new ObjectPosition(0x38, 0x38, 1), 1, checkStack));
        Assert.Equal(91, container.Temporaries[0]);
        Assert.Equal(0, child[ObjectData.ContainerId]);
        Assert.Equal(0, child[ObjectData.ParentId]);
    }

    [Fact]
    public void PlaceObjectInContainerRunsMemberFunctionNineOnMemberAndContainerFunctionNineOnContainer()
    {
        var world = new SimulationWorld();
        var containerPos = new ObjectPosition(0x18, 0x18, 1);
        var container = world.Add(CreateObject(60, containerPos));
        container.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
        container.Slots.AddRange([0, 0]);
        var containerFunctions = new ushort[10];
        containerFunctions[9] = 4097;
        var containerTree = PrimitiveTestWorld.Tree(0, (2, 0xFE, 0xFF, PrimitiveTestWorld.Expression(0, 222, 5)));
        container.TypeRecord = new TypeRecord(60)
        {
            FunctionTable = containerFunctions,
            BehaviorScope = new TreeTable((4097, containerTree)),
        };

        var item = world.Add(CreateObject(61, new ObjectPosition(0x28, 0x28, 1)));
        var itemFunctions = new ushort[10];
        itemFunctions[9] = 4096;
        var itemTree = PrimitiveTestWorld.Tree(0, (2, 0xFE, 0xFF, PrimitiveTestWorld.Expression(0, 111, 5)));
        item.TypeRecord = new TypeRecord(61)
        {
            FunctionTable = itemFunctions,
            BehaviorScope = new TreeTable((4096, itemTree)),
        };

        var checkStack = TreeRunner.CreateTransientStack(world, item);
        world.PlaceObject(item, containerPos, checkStack, placementContainer: container, placementSlotNumber: 1);

        Assert.Equal(111, item.Temporaries[0]);
        Assert.Equal(222, container.Temporaries[0]);
    }

    [Fact]
    public void EnqueueInteractionActionFlag40InheritsFromCurrentActionOnlyOnHeadContinuationOrActiveImmediate()
    {
        var world = new SimulationWorld();
        var personDef = new short[XObject.DefinitionCount];
        personDef[(int)ObjdField.Type] = XPerson.PersonType;
        var person = world.Add(new XPerson(70, personDef));
        person.CurrentActionRecord = new ActionQueueSlot
        {
            Owner = person,
            ActionText = "Active Queue String",
            Flags = 0x80,
        };

        var normalCandidate = new ActionQueueSlot
        {
            Owner = person,
            Threshold = 50,
            Flags = 0x40,
        };
        Assert.True(world.EnqueueInteractionAction(person, normalCandidate, headContinuation: false));
        Assert.Null(person.ActionQueue[0].ActionText);
        Assert.Equal(0x80, person.CurrentActionRecord.Flags & 0x80);

        var headCandidate = new ActionQueueSlot
        {
            Owner = person,
            Threshold = 50,
            Flags = 0x42,
        };
        Assert.True(world.EnqueueInteractionAction(person, headCandidate, headContinuation: true));
        Assert.Equal("Active Queue String", person.ActionQueue[person.ActionQueueCurrent & 7].ActionText);
    }

    [Fact]
    public void WallMountedSingleTileObjectsAllowPerpendicularWallsOnSameCornerTileAndRejectSameWallEdge()
    {
        var world = new SimulationWorld();
        var corner = new ObjectPosition(0x18, 0x18, 1);

        var firstWindow = world.Add(CreateObject(80, corner));
        firstWindow[ObjectData.Flags] = 4;
        firstWindow[ObjectData.PlacementFlags] = 1;
        firstWindow[ObjectData.WallPlacementFlags] = 1;
        firstWindow[ObjectData.Direction] = 0;
        firstWindow.Definition[(int)ObjdField.BuildModeType] = 2;

        var secondWindow = world.Add(CreateObject(81, new ObjectPosition(0x28, 0x28, 1)));
        secondWindow[ObjectData.Flags] = 4;
        secondWindow[ObjectData.PlacementFlags] = 1;
        secondWindow[ObjectData.WallPlacementFlags] = 1;
        secondWindow[ObjectData.Direction] = 0;
        secondWindow.Definition[(int)ObjdField.BuildModeType] = 2;

        Assert.False(world.CanPlaceObject(secondWindow, corner));
        Assert.Equal(0x16, world.LastPlacementFailureCode);

        secondWindow[ObjectData.Direction] = 2;
        Assert.True(world.CanPlaceObject(secondWindow, corner));

        var painting = world.Add(CreateObject(82, new ObjectPosition(0x38, 0x38, 1)));
        painting[ObjectData.Flags] = 4;
        painting[ObjectData.PlacementFlags] = 1;
        painting[ObjectData.WallPlacementFlags] = 1;
        painting[ObjectData.Direction] = 0;
        painting.Definition[(int)ObjdField.BuildModeType] = 0;
        Assert.True(world.CanPlaceObject(painting, corner));
    }
}
