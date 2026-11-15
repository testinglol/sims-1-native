using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;

namespace SimsEngine.Tests;

public sealed class LotRoofGridTests
{
    private static IffFile Lot(bool diagonal = false, bool partition = false,
        Func<int, int, byte[]>? terrain = null, Func<int, int, ushort>? upperFloor = null)
    {
        byte[] Layer(int size, Func<int, int, byte[]> value)
        {
            var body = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(size).U32(0);
            for (var x = 0; x < 64; x++)
            for (var y = 0; y < 64; y++) body.Raw(value(x, y));
            return body.ToArray();
        }
        byte[] Walls(int x, int y)
        {
            byte bits = 0;
            if (partition)
            {
                if (x == 9) bits |= 4;
                if (x == 10) bits |= 1;
            }
            else
            {
                if (y >= 4 && y <= 8) { if (x == 4 || x == 9) bits |= 1; if (x == 3 || x == 8) bits |= 4; }
                if (x >= 4 && x <= 8) { if (y == 4 || y == 9) bits |= 2; if (y == 3 || y == 8) bits |= 8; }
                if (diagonal && x == 4 && y == 4) bits |= 0x10;
            }
            return [bits, 0x0f, 1, 1, 0, 0, 0, 0];
        }
        return new IffFile(ChunkBuilder.File(
            ("Arry", 0, Layer(4, terrain ?? ((_, _) => [0, 0, 0, 0]))),
            ("Arry", 1, Layer(1, (_, _) => [0])),
            ("Arry", 2, Layer(8, Walls)),
            ("Arry", 8, Layer(1, (x, y) => [x is >= 4 and <= 8 && y is >= 4 and <= 8 ? (byte)0x10 : (byte)0x14])),
            ("Arry", 101, Layer(1, (_, _) => [0])),
            ("Arry", 111, Layer(2, (x, y) => BitConverter.GetBytes(upperFloor?.Invoke(x, y) ?? 0))),
            ("Arry", 102, Layer(8, Walls))), "roof.iff");
    }

    [Fact]
    public void OutsideStateComesFromTileFlagBitTwoAtTheRoomsFirstTile()
    {
        var map = RoomMap.Build(Lot(), 1, 64, 64);

        Assert.False(map.IsOutside(map.RoomAt(6, 6)));
        Assert.True(map.IsOutside(map.RoomAt(0, 0)));
    }

    private static XObject Item(short id, int x, int y, int level, short alternateBuildType = 0)
        => new(id, new short[XObject.DefinitionCount])
        {
            Position = new ObjectPosition(x * 16, y * 16, level),
            TypeRecord = new TypeRecord(id) { AutonomyAlternativeTypeFlag = alternateBuildType },
        };

    private static void Tile(RoofGrid grid, int x, int y, bool roofed, bool blocked = false)
    {
        for (var dy = 0; dy < 2; dy++)
        for (var dx = 0; dx < 2; dx++)
        {
            Assert.Equal(roofed, grid.Has(2 * y + dy, 2 * x + dx));
            Assert.Equal(blocked, (grid.Flags(2 * y + dy, 2 * x + dx) & 8) != 0);
        }
    }

    [Fact]
    public void RoomSeedsFourCellsPerTileAndBuildAddsEaves()
    {
        var lot = Lot();
        var seed = LotRoofGrid.Seed(lot, 1, []);
        Tile(seed, 5, 6, true);
        Tile(seed, 6, 5, true);
        Tile(seed, 3, 6, false);
        var plan = LotRoofGrid.Build(lot, 1, []);
        Assert.True(plan.Has(12, 7));
        Assert.False(plan.Has(12, 6));
        Assert.True(plan.Height(12, 12) > 0);
    }

    [Fact]
    public void OneSlopedTileSuppressesTheWholeRoomButDifferentFlatTileHeightsDoNot()
    {
        var slope = Lot(terrain: (x, y) => x == 6 && y == 6 ? [1, 0, 0, 0] : [0, 0, 0, 0]);
        Tile(LotRoofGrid.Seed(slope, 1, []), 5, 5, false);
        Tile(LotRoofGrid.Build(slope, 1, []), 5, 5, false);
        var stepped = Lot(terrain: (x, y) => x == 6 && y == 6 ? [1, 1, 1, 1] : [0, 0, 0, 0]);
        Tile(LotRoofGrid.Seed(stepped, 1, []), 5, 5, true);
    }

    [Fact]
    public void EitherAdjacentRoomRoofsAllFourCellsOfADiagonalTile()
    {
        var lot = Lot(diagonal: true);
        var rooms = RoomMap.Build(lot, 1, 64, 64);
        Assert.Equal(RoomMap.NoRoom, rooms.RoomAt(4, 4));
        var pair = rooms.AdjacentRoomsAtTile(4, 4)!.Value;
        Assert.NotEqual(rooms.IsOutside(pair.First), rooms.IsOutside(pair.Second));
        Tile(LotRoofGrid.Seed(lot, 1, []), 4, 4, true);
    }

    [Fact]
    public void UpperFloorWordBlocksLowerRoofAndGrowthButNotTheUpperRoof()
    {
        var lot = Lot(upperFloor: (x, y) => x == 6 && y == 5 ? (ushort)256 : (ushort)0);
        Tile(LotRoofGrid.Seed(lot, 1, []), 6, 5, false, true);
        Tile(LotRoofGrid.Build(lot, 1, []), 6, 5, false, true);
        Tile(LotRoofGrid.Seed(lot, 1, []), 5, 6, true);
        Tile(LotRoofGrid.Seed(lot, 2, []), 6, 5, true);
    }

    [Fact]
    public void OmittedUpperFloorPlaneDoesNotBlockLowerRoof()
    {
        var chunks = Lot().Chunks.Where(chunk => chunk.Id is not (101 or 111))
            .Select(chunk => (chunk.Type.ToString(), chunk.Id, chunk.Data.ToArray())).ToArray();
        var lot = new IffFile(ChunkBuilder.File(chunks), "no-upper-floor.iff");
        Tile(LotRoofGrid.Seed(lot, 1, []), 6, 5, true);
    }

    [Fact]
    public void AlternateBuildTypeThreeRequiresUpperOccupancyAndUsesGroundObjects()
    {
        var lot = Lot();
        var lower = Item(1, 5, 6, 1, 3);
        var upper = Item(2, 5, 6, 2);
        Tile(LotRoofGrid.Seed(lot, 1, [lower]), 5, 6, true);
        Tile(LotRoofGrid.Seed(lot, 1, [upper]), 5, 6, true);
        Tile(LotRoofGrid.Seed(lot, 1, [lower, upper]), 5, 6, false, true);
        Tile(LotRoofGrid.Seed(lot, 2, [lower, upper]), 5, 6, true);
        upper.Position = new ObjectPosition(6 * 16, 5 * 16, 2);
        Tile(LotRoofGrid.Seed(lot, 1, [lower, upper]), 5, 6, true);
    }

    [Fact]
    public void VisibleCategoryEightOverridesOutsideForNonzeroRoomAndRebuildClearsIt()
    {
        var lot = Lot(partition: true);
        var rooms = RoomMap.Build(lot, 1, 64, 64);
        var room = rooms.RoomAt(12, 6);
        Assert.True(rooms.IsOutside(room));
        Assert.NotEqual(0, room & 0x3ff);
        var item = Item(1, 12, 6, 1);
        item.RoomWord84 = (short)room;
        item.Data[(int)ObjectData.Category] = 8;
        Tile(LotRoofGrid.Seed(lot, 1, [item]), 13, 7, true);
        item.Data[(int)ObjectData.Hidden] = 1;
        Tile(LotRoofGrid.Seed(lot, 1, [item]), 13, 7, false);
        item.Data[(int)ObjectData.Hidden] = 0;
        item.RoomWord84 = -5;
        Tile(LotRoofGrid.Seed(lot, 1, [item]), 13, 7, false);
        Tile(LotRoofGrid.Build(lot, 1, []), 13, 7, false);
        item.Position = new ObjectPosition(2 * 16, 6 * 16, 1);
        item.RoomWord84 = (short)rooms.RoomAt(2, 6);
        Tile(LotRoofGrid.Seed(lot, 1, [item]), 2, 6, false);
    }
}
