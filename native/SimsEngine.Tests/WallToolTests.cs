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
}
