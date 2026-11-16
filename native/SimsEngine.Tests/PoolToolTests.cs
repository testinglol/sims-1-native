using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;

namespace SimsEngine.Tests;

public sealed class PoolToolTests
{
    private sealed class Budget(int funds) : IFamilyBudget
    {
        public int Funds { get; private set; } = funds;
        public int ReadFamilyBudget() => Funds;
        public void AlterFamilyBudget(int expenseType, int amount) => Funds -= amount;
    }

    private static (IffFile Lot, FloorCatalog Catalog) CreateSetup()
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

        var floorStrings = new ChunkBuilder().S16(-3).S16(3)
            .U8(1).CString("10").CString("")
            .U8(1).CString("Tile").CString("")
            .U8(1).CString("Floor").CString("");
        var waterStrings = new ChunkBuilder().S16(-3).S16(6)
            .U8(1).CString("50").CString("")
            .U8(1).CString("Pool").CString("")
            .U8(1).CString("Pool desc").CString("")
            .U8(1).CString("15").CString("")
            .U8(1).CString("Water").CString("")
            .U8(1).CString("Water desc").CString("");
        var build = new IffFile(ChunkBuilder.File(
            ("STR#", 0x82, floorStrings.ToArray()),
            ("STR#", 0x85, waterStrings.ToArray())
        ), "Build.iff");
        return (lot, FloorCatalog.Read(build));
    }

    [Fact]
    public void PoolAllowsExistingFloorTilesAndPerimeterWallsButRejectsInteriorOrPoolSeparatingWalls()
    {
        var (lot, catalog) = CreateSetup();
        var floors = LotFloors.Read(lot, 1);
        var pools = LotPool.Read(lot);
        var tiles = LotTiles.Read(lot);
        var terrain = LotTerrain.Read(lot);
        var walls = LotWalls.Read(lot, 1);
        var budget = new Budget(5000);
        var tool = new PoolTool(new SimulationWorld(), floors, pools, tiles, terrain, walls, catalog, budget);

        floors[10, 10] = 1;
        walls[10, 10][0] = 1;
        walls[9, 10][0] = 4;

        var added = tool.Rectangle(10, 10, 10, 11, erase: false);
        Assert.True(added.Applied);
        Assert.Equal(2, added.Changed);
        Assert.Equal(65535, floors[10, 10]);
        Assert.True(pools.IsPool(10, 10, 1));

        var blockedAcrossExisting = tool.Rectangle(9, 10, 9, 10, erase: false);
        Assert.False(blockedAcrossExisting.Applied);
        Assert.Equal("blocked-by-wall", blockedAcrossExisting.Refused);

        walls[20, 20][0] = 1;
        walls[19, 20][0] = 4;
        var blockedInterior = tool.Rectangle(19, 20, 20, 20, erase: false);
        Assert.False(blockedInterior.Applied);
        Assert.Equal("blocked-by-wall", blockedInterior.Refused);
    }

    [Fact]
    public void PoolAndWaterAllowMixedEraseRectangleWhenAtLeastOneTileMatches()
    {
        var (lot, catalog) = CreateSetup();
        var floors = LotFloors.Read(lot, 1);
        var pools = LotPool.Read(lot);
        var water = LotWater.Read(lot);
        var tiles = LotTiles.Read(lot);
        var terrain = LotTerrain.Read(lot);
        var walls = LotWalls.Read(lot, 1);
        var budget = new Budget(5000);
        var world = new SimulationWorld();

        var poolTool = new PoolTool(world, floors, pools, tiles, terrain, walls, catalog, budget, water: water);
        Assert.True(poolTool.Rectangle(10, 10, 10, 10, erase: false).Applied);

        terrain.SetHeight(9, 10, 4);
        var erasedPool = poolTool.Rectangle(9, 10, 10, 10, erase: true);
        Assert.True(erasedPool.Applied);
        Assert.Equal(1, erasedPool.Changed);
        Assert.False(pools.IsPool(10, 10, 1));
        Assert.Equal(0, floors[10, 10]);

        var waterTool = new WaterTool(world, water, floors, pools, tiles, terrain, walls, catalog, budget);
        Assert.True(waterTool.Rectangle(15, 15, 15, 15, erase: false).Applied);
        var erasedWater = waterTool.Rectangle(14, 15, 16, 15, erase: true);
        Assert.True(erasedWater.Applied);
        Assert.Equal(1, erasedWater.Changed);
        Assert.False(water.IsWater(15, 15, 1));
    }
}
