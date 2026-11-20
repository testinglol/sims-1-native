using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;

namespace SimsEngine.Tests;

public sealed class WallToolTests
{
    private sealed class Budget(int funds) : IFamilyBudget
    {
        public int Funds { get; private set; } = funds;
        public int ReadFamilyBudget() => Funds;
        public void AlterFamilyBudget(int expenseType, int amount) => Funds -= amount;
    }

    private static (IffFile Lot, WallCatalog Catalog) CreateSetup()
    {
        byte[] BuildArry(int elementSize, byte[] rawData) => new ChunkBuilder()
            .S16(0).S16(0).S32(64).S32(64).S32(elementSize).S32(0).Raw(rawData).ToArray();

        var terrainData = new byte[64 * 64 * 4];
        var wallData = new byte[64 * 64 * 8];
        var floorData = new byte[64 * 64 * 2];
        var tileFlags = Enumerable.Repeat((byte)1, 64 * 64).ToArray();

        var lot = new IffFile(ChunkBuilder.File(
            ("Arry", 0, BuildArry(4, terrainData)),
            ("Arry", 2, BuildArry(8, wallData)),
            ("Arry", 8, BuildArry(1, tileFlags)),
            ("Arry", 11, BuildArry(2, floorData))
        ), "lot.iff");

        var strings = new ChunkBuilder().S16(-3).S16(6)
            .U8(1).CString("70").CString("")
            .U8(1).CString("Wall").CString("")
            .U8(1).CString("Standard wall").CString("")
            .U8(1).CString("10").CString("")
            .U8(1).CString("Fence").CString("")
            .U8(1).CString("Picket fence").CString("");
        var build = new IffFile(ChunkBuilder.File(("STR#", 0x81, strings.ToArray())), "Build.iff");
        return (lot, WallCatalog.Read(build));
    }

    [Fact]
    public void LineRefusesSegmentWhenSimOrInUseObjectOccupiesIntermediateOrNeighbourTile()
    {
        var (lot, catalog) = CreateSetup();
        var world = new SimulationWorld();
        var walls = LotWalls.Read(lot, 1);
        var terrain = LotTerrain.Read(lot);
        var budget = new Budget(5000);
        var tool = new WallTool(world, walls, terrain, catalog, budget, LotPool.Read(lot), LotTiles.Read(lot), LotWater.Read(lot));

        var simDef = new short[XObject.DefinitionCount];
        simDef[(int)ObjdField.Type] = XPerson.PersonType;
        var sim = world.Add(new XPerson(10, simDef) { Position = new ObjectPosition(11 * 16 + 8, 9 * 16 + 8, 1) });

        var changed = tool.Line(10, 10, 13, 10, style: 1, pattern: 0, erase: false);
        Assert.Equal(2, changed);
        Assert.Equal(1, tool.Refused);
        Assert.Equal(0xb, tool.Error);
        Assert.Equal(2, walls[10, 10][0] & 2);
        Assert.Equal(0, walls[11, 10][0] & 2);
        Assert.Equal(2, walls[12, 10][0] & 2);
    }

    [Fact]
    public void EraseRefusesUnsellableDoorAndWallMountedObjectAndRestoresDiagonalSplitFloor()
    {
        var (lot, catalog) = CreateSetup();
        var world = new SimulationWorld();
        var walls = LotWalls.Read(lot, 1);
        var floors = LotFloors.Read(lot, 1);
        var terrain = LotTerrain.Read(lot);
        var budget = new Budget(5000);
        var tool = new WallTool(world, walls, terrain, catalog, budget, LotPool.Read(lot), LotTiles.Read(lot), LotWater.Read(lot), floors);

        Assert.Equal(1, tool.Line(10, 10, 10, 11, style: 1, pattern: 0, erase: false));
        var sconceDef = new short[XObject.DefinitionCount];
        sconceDef[(int)ObjdField.Type] = 4;
        var sconce = world.Add(new XObject(20, sconceDef) { Position = new ObjectPosition(9 * 16 + 8, 10 * 16 + 8, 1) });
        sconce[ObjectData.Direction] = 0;
        sconce[ObjectData.WallPlacementFlags] = 2;

        Assert.Equal(0, tool.Line(10, 10, 10, 11, style: 1, pattern: 0, erase: true));
        Assert.Equal(0xb, tool.Error);
        Assert.NotEqual(0, walls[10, 10][0] & 1);

        var doorTool = new WallTool(world, walls, terrain, catalog, budget, LotPool.Read(lot), LotTiles.Read(lot), LotWater.Read(lot), floors);
        Assert.Equal(1, doorTool.Line(12, 10, 12, 11, style: 1, pattern: 0, erase: false));
        walls[12, 10][2] = 3;
        var doorDef = new short[XObject.DefinitionCount];
        doorDef[(int)ObjdField.Type] = 8;
        var door = world.Add(new XObject(21, doorDef) { Position = new ObjectPosition(12 * 16 + 8, 10 * 16 + 8, 1) });
        door[ObjectData.MovementFlags] = 0xa;
        door[ObjectData.UseCount] = 1;
        door[ObjectData.WallPlacementFlags] = 8;
        Assert.Equal(0, doorTool.Line(12, 10, 12, 11, style: 1, pattern: 0, erase: true));
        Assert.Equal(0x23, doorTool.Error);
        Assert.NotEqual(0, walls[12, 10][0] & 1);

        // Erasing a diagonal wall on a split floor (pattern 0xFF) restores the underlying floor pattern
        // preserved in record index 4.
        Assert.Equal(1, tool.Line(15, 15, 16, 16, style: 1, pattern: 0, erase: false));
        walls[15, 15][2] = 3;
        walls[15, 15][4] = 7;
        floors[15, 15] = byte.MaxValue;

        Assert.Equal(1, tool.Line(15, 15, 16, 16, style: 1, pattern: 0, erase: true));
        Assert.Equal(7, floors[15, 15]);
    }

    [Theory]
    [InlineData(1, 2, 100)]
    [InlineData(2, 2, 0)]
    [InlineData(1, 1, 100)]
    [InlineData(2, 1, 0)]
    public void PartialWallEraseAndOverwriteOnMultiTilePortalResetsAllSurvivingSiblingWallStyles(
        int level, int wallBit, short currentValue)
    {
        byte[] BuildArry(int elementSize, byte[] rawData) => new ChunkBuilder()
            .S16(0).S16(0).S32(64).S32(64).S32(elementSize).S32(0).Raw(rawData).ToArray();
        var wallChunkId = (ushort)(level == 1 ? 2 : 102);
        var lot = new IffFile(ChunkBuilder.File(
            ("Arry", 0, BuildArry(4, new byte[64 * 64 * 4])),
            ("Arry", wallChunkId, BuildArry(8, new byte[64 * 64 * 8])),
            ("Arry", 8, BuildArry(1, Enumerable.Repeat((byte)1, 64 * 64).ToArray())),
            ("Arry", 11, BuildArry(2, new byte[64 * 64 * 2]))
        ), "lot.iff");
        var (_, catalog) = CreateSetup();

        static XObject AddThreeTilePortal(SimulationWorld world, LotWalls walls, int level, int bit, short value)
        {
            var def = new short[XObject.DefinitionCount];
            def[(int)ObjdField.Type] = 8;
            def[(int)ObjdField.MasterId] = 1;
            def[(int)ObjdField.WallStyle] = 22;
            def[(int)ObjdField.Price] = value;

            var parts = new XObject[3];
            for (var i = 0; i < 3; i++)
            {
                var px = bit == 2 ? 20 + i : 20;
                var py = bit == 2 ? 12 : 12 + i;
                var part = new XObject((short)(30 + i), def)
                {
                    Position = new ObjectPosition(px * 16 + 8, py * 16 + 8, level),
                };
                part[ObjectData.WallPlacementFlags] = (short)(bit == 2 ? 1 : 4);
                part[ObjectData.Direction] = (short)(bit == 2 ? 0 : 2);
                part[ObjectData.MovementFlags] = 0x2 | 0x8;
                part[ObjectData.CurrentValue] = i == 0 ? value : (short)0;
                world.Add(part);
                world.SetObjectPosition(part, part.Position);
                walls[px, py][bit == 1 ? 2 : 3] = 22;
                parts[i] = part;
            }
            parts[0].MultiTileNextId = parts[1].Id;
            parts[1].MultiTileMasterId = parts[0].Id;
            parts[1].MultiTileNextId = parts[2].Id;
            parts[2].MultiTileMasterId = parts[0].Id;
            return parts[0];
        }

        // 1. Partial erase of tile 0 resets surviving tiles 1 and 2 to WallStyle = 1, allowing subsequent erase with full wall refund.
        var world1 = new SimulationWorld();
        var walls1 = LotWalls.Read(lot, level);
        var terrain1 = LotTerrain.Read(lot);
        var budget1 = new Budget(5000);
        var tool1 = new WallTool(world1, walls1, terrain1, catalog, budget1, tiles: LotTiles.Read(lot));
        var (fromX, fromY, toX, toY) = wallBit == 2 ? (20, 12, 23, 12) : (20, 12, 20, 15);
        Assert.Equal(3, tool1.Line(fromX, fromY, toX, toY, style: 1, pattern: 0, erase: false));
        tool1.Commit();

        var portal1 = AddThreeTilePortal(world1, walls1, level, wallBit, currentValue);
        var styleSlot = wallBit == 1 ? 2 : 3;
        var (seg1EndX, seg1EndY) = wallBit == 2 ? (21, 12) : (20, 13);
        Assert.Equal(1, tool1.Line(20, 12, seg1EndX, seg1EndY, style: 1, pattern: 0, erase: true));
        Assert.Null(world1.ObjectById(portal1.Id));
        Assert.Null(world1.ObjectById((short)(portal1.Id + 1)));
        Assert.Null(world1.ObjectById((short)(portal1.Id + 2)));
        Assert.Equal(0, walls1[20, 12][0] & wallBit);

        var (sib1X, sib1Y) = wallBit == 2 ? (21, 12) : (20, 13);
        var (sib2X, sib2Y) = wallBit == 2 ? (22, 12) : (20, 14);
        Assert.NotEqual(0, walls1[sib1X, sib1Y][0] & wallBit);
        Assert.NotEqual(0, walls1[sib2X, sib2Y][0] & wallBit);
        Assert.Equal(1, walls1[sib1X, sib1Y][styleSlot]);
        Assert.Equal(1, walls1[sib2X, sib2Y][styleSlot]);

        // Subsequent erase on surviving sibling tile refunds 80% of wall price (70 * 0.8 = 56).
        tool1.Commit();
        var (sib1EndX, sib1EndY) = wallBit == 2 ? (22, 12) : (20, 14);
        Assert.Equal(1, tool1.Line(sib1X, sib1Y, sib1EndX, sib1EndY, style: 1, pattern: 0, erase: true));
        Assert.Equal(-56, tool1.Commit());

        // 2. Partial WALL_LINE overwrite (including multi-segment drag across 2 of 3 tiles) demolishes portal and resets surviving tile.
        var world2 = new SimulationWorld();
        var walls2 = LotWalls.Read(lot, level);
        var tool2 = new WallTool(world2, walls2, LotTerrain.Read(lot), catalog, new Budget(5000), tiles: LotTiles.Read(lot));
        Assert.Equal(3, tool2.Line(fromX, fromY, toX, toY, style: 1, pattern: 0, erase: false));
        tool2.Commit();

        var portal2 = AddThreeTilePortal(world2, walls2, level, wallBit, currentValue);
        var (dragEndX, dragEndY) = wallBit == 2 ? (22, 12) : (20, 14);
        Assert.Equal(1, tool2.Line(20, 12, dragEndX, dragEndY, style: 1, pattern: 0, erase: false));
        Assert.Null(world2.ObjectById(portal2.Id));
        Assert.Equal(1, walls2[20, 12][styleSlot]);
        Assert.Equal(1, walls2[sib1X, sib1Y][styleSlot]);
        Assert.Equal(1, walls2[sib2X, sib2Y][styleSlot]);
    }

    [Fact]
    public void EraseAndTrySellOpening_HandleOppositeSidePortalAcrossWallEdge()
    {
        var (lot, catalog) = CreateSetup();
        var world = new SimulationWorld();
        var walls = LotWalls.Read(lot, 1);
        var tool = new WallTool(world, walls, LotTerrain.Read(lot), catalog, new Budget(5000), tiles: LotTiles.Read(lot));

        Assert.Equal(2, tool.Line(20, 10, 22, 10, style: 1, pattern: 0, erase: false));
        walls[20, 10][3] = 22;
        walls[21, 10][3] = 22;

        // Place a 2-tile portal on the opposite side (y = 9) facing south (WallPlacementFlags = 4 -> bit 8 -> owner (x, 10, bit 2)).
        var def = new short[XObject.DefinitionCount];
        def[(int)ObjdField.Type] = 8;
        def[(int)ObjdField.MasterId] = 1;
        def[(int)ObjdField.WallStyle] = 22;
        var p0 = world.Add(new XObject(40, def) { Position = new ObjectPosition(20 * 16 + 8, 9 * 16 + 8, 1) });
        var p1 = world.Add(new XObject(41, def) { Position = new ObjectPosition(21 * 16 + 8, 9 * 16 + 8, 1) });
        p0.MultiTileNextId = p1.Id;
        p1.MultiTileMasterId = p0.Id;
        foreach (var p in new[] { p0, p1 })
        {
            p[ObjectData.WallPlacementFlags] = 4;
            p[ObjectData.MovementFlags] = 0xa;
            p[ObjectData.UseCount] = 1;
            world.SetObjectPosition(p, p.Position);
        }

        // Unsellable opposite-side portal must fail with 0x23.
        Assert.Equal(0, tool.Line(20, 10, 21, 10, style: 1, pattern: 0, erase: true));
        Assert.Equal(0x23, tool.Error);

        // Clearing UseCount makes it sellable; erasing tile 20 resets sibling tile 21 to WallStyle = 1.
        p0[ObjectData.UseCount] = 0;
        p1[ObjectData.UseCount] = 0;
        Assert.Equal(1, tool.Line(20, 10, 21, 10, style: 1, pattern: 0, erase: true));
        Assert.Null(world.ObjectById(p0.Id));
        Assert.Null(world.ObjectById(p1.Id));
        Assert.Equal(1, walls[21, 10][3]);
    }
}
