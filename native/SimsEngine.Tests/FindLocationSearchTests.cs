using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using Xunit;

namespace SimsEngine.Tests;

public sealed class FindLocationSearchTests
{
    private static RoomMap WaterRoomMap((int X, int Y, byte Value)[] values, bool openDiagonalAtCandidate = false)
    {
        const int size = 64;
        var walls = new ChunkBuilder().S16(0).S16(0).S32(size).S32(size).S32(8).U32(0);
        var waterBytes = Enumerable.Repeat((byte)0x10, size * size).ToArray();
        for (var x = 0; x < size; x++)
        for (var y = 0; y < size; y++)
            walls.Raw(openDiagonalAtCandidate && x == 1 && y == 2
                ? [0x10, 0x0F, 0, 2, 0, 0, 0, 0]
                : [0, 0x0F, 0, 0, 0, 0, 0, 0]);
        // Lot arrays are stored and indexed as [x][y] in row-major layout, with x as the outer dimension.
        foreach (var (x, y, value) in values) waterBytes[x * size + y] = value;
        var water = new ChunkBuilder().S16(0).S16(0).S32(size).S32(size).S32(1).U32(0).Raw(waterBytes).ToArray();
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, walls.ToArray()), ("Arry", 9, water)), "water-search.iff");
        return RoomMap.Build(file, 1, size, size);
    }

    private static RoomMap SplitRoomMap()
    {
        var body = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(8).U32(0);
        for (var x = 0; x < 64; x++)
        for (var y = 0; y < 64; y++)
        {
            if (x == 31) body.Raw([4, 0x0f, 0, 0, 0, 0, 0, 0]);
            else if (x == 32) body.Raw([1, 0x0f, 1, 0, 0, 0, 0, 0]);
            else body.Raw([0, 0x0f, 0, 0, 0, 0, 0, 0]);
        }
        var waterBytes = Enumerable.Repeat((byte)0x10, 64 * 64).ToArray();
        var water = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(1).U32(0).Raw(waterBytes);
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, body.ToArray()), ("Arry", 9, water.ToArray())), "split-room.iff");
        return RoomMap.Build(file, 1, 64, 64);
    }

    private static XObject AddObject(SimulationWorld world, short id, int x, int y)
    {
        var item = world.Add(new XObject(id, PrimitiveTestWorld.Definition()));
        item[ObjectData.AllowedHeightFlags] = 1;
        item.Position = new ObjectPosition(x, y, 1);
        return item;
    }

    [Fact]
    public void DirectionMinusOneUsesTheOriginalSpiralOrder()
    {
        var world = new SimulationWorld();
        var stack = AddObject(world, 1, -16, -16);
        AddObject(world, 2, 2 * 16 + 8, 2 * 16 + 8);

        Assert.True(world.FindLocation(stack, new FindLocationParams
        {
            CenterX = 2 * 16 + 8,
            CenterY = 2 * 16 + 8,
            Height = 1,
            Direction = -1,
            FlagB = 0,
        }, out var found));

        Assert.Equal(new ObjectPosition(1 * 16 + 8, 2 * 16 + 8, 1), found);
    }

    [Fact]
    public void DirectionMinusOneContinuesAlongTheSpiralAfterTheFirstNeighborIsOccupied()
    {
        var world = new SimulationWorld();
        var stack = AddObject(world, 1, -16, -16);
        AddObject(world, 2, 2 * 16 + 8, 2 * 16 + 8);
        AddObject(world, 3, 1 * 16 + 8, 2 * 16 + 8);

        Assert.True(world.FindLocation(stack, new FindLocationParams
        {
            CenterX = 2 * 16 + 8,
            CenterY = 2 * 16 + 8,
            Height = 1,
            Direction = -1,
            FlagB = 0,
        }, out var found));

        Assert.Equal(new ObjectPosition(1 * 16 + 8, 3 * 16 + 8, 1), found);
    }

    [Fact]
    public void DirectionZeroAlternatesTheSearchAlongItsSourceDefinedAxis()
    {
        var world = new SimulationWorld();
        var stack = AddObject(world, 1, -16, -16);
        AddObject(world, 2, 2 * 16 + 8, 2 * 16 + 8);

        Assert.True(world.FindLocation(stack, new FindLocationParams
        {
            CenterX = 2 * 16 + 8,
            CenterY = 2 * 16 + 8,
            Height = 1,
            Direction = 0,
            FlagB = 0,
        }, out var found));

        Assert.Equal(new ObjectPosition(2 * 16 + 8, 3 * 16 + 8, 1), found);
    }

    [Fact]
    public void ContinueFlagKeepsCandidatesInTheCenterRoomOnTheFirstSearch()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = SplitRoomMap();
        var stack = AddObject(world, 1, -16, -16);
        AddObject(world, 2, 32 * 16 + 8, 32 * 16 + 8);

        Assert.True(world.FindLocation(stack, new FindLocationParams
        {
            CenterX = 32 * 16 + 8,
            CenterY = 32 * 16 + 8,
            Height = 1,
            Direction = -1,
            ContinueFlag = 1,
            FlagB = 0,
        }, out var found));

        Assert.Equal(new ObjectPosition(32 * 16 + 8, 33 * 16 + 8, 1), found);
    }

    [Fact]
    public void ClearingContinueFlagAllowsTheFirstValidCandidateAcrossRooms()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = SplitRoomMap();
        var stack = AddObject(world, 1, -16, -16);
        AddObject(world, 2, 32 * 16 + 8, 32 * 16 + 8);

        Assert.True(world.FindLocation(stack, new FindLocationParams
        {
            CenterX = 32 * 16 + 8,
            CenterY = 32 * 16 + 8,
            Height = 1,
            Direction = -1,
            ContinueFlag = 0,
            FlagB = 0,
        }, out var found));

        Assert.Equal(new ObjectPosition(31 * 16 + 8, 32 * 16 + 8, 1), found);
    }

    [Fact]
    public void LevelOneWaterTilesAreSkippedByTheOriginalWaterHelperGate()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = WaterRoomMap([(1, 2, 0x0f)]);
        var stack = AddObject(world, 1, -16, -16);
        AddObject(world, 2, 2 * 16 + 8, 2 * 16 + 8);

        Assert.True(world.FindLocation(stack, new FindLocationParams
        {
            CenterX = 2 * 16 + 8, CenterY = 2 * 16 + 8, Height = 1, Direction = -1,
            ContinueFlag = 0, FlagB = 0,
        }, out var found));

        Assert.Equal(new ObjectPosition(1 * 16 + 8, 3 * 16 + 8, 1), found);
    }

    [Fact]
    public void FlagCRequiresLotTileBit20ClearUnlessTheSourceGlobalOverrideIsSet()
    {
        const int size = 64;
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = WaterRoomMap([(1, 2, 0x30)]);
        var tileBytes = new byte[size * size];
        tileBytes[1 * size + 2] = 0x20;
        var tiles = new ChunkBuilder().S16(0).S16(0).S32(size).S32(size).S32(1).U32(0).Raw(tileBytes).ToArray();
        world.LotTileFlags = SimsEngine.World.LotTiles.Read(new IffFile(ChunkBuilder.File(("Arry", 8, tiles)), "tiles.iff"));
        var stack = AddObject(world, 1, -16, -16);
        AddObject(world, 2, 2 * 16 + 8, 2 * 16 + 8);
        var parameters = new FindLocationParams
        {
            CenterX = 2 * 16 + 8, CenterY = 2 * 16 + 8, Height = 1, Direction = -1,
            ContinueFlag = 0, FlagB = 0, FlagC = 1,
        };

        Assert.True(world.FindLocation(stack, parameters, out var blocked));
        Assert.Equal(new ObjectPosition(1 * 16 + 8, 3 * 16 + 8, 1), blocked);

        world.FindLocationWaterOverride = true;
        Assert.True(world.FindLocation(stack, parameters, out var allowed));
        Assert.Equal(new ObjectPosition(1 * 16 + 8, 2 * 16 + 8, 1), allowed);
    }

    [Fact]
    public void FlagBAcceptsOnlyRoomKeyZeroAsTheOriginalRoomLookupDoes()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = SplitRoomMap();
        var stack = AddObject(world, 1, -16, -16);
        AddObject(world, 2, 32 * 16 + 8, 32 * 16 + 8);

        Assert.True(world.FindLocation(stack, new FindLocationParams
        {
            CenterX = 32 * 16 + 8, CenterY = 32 * 16 + 8, Height = 1, Direction = -1,
            ContinueFlag = 0, FlagB = 1,
        }, out var found));
        Assert.Equal(new ObjectPosition(31 * 16 + 8, 32 * 16 + 8, 1), found);

        var single = new SimulationWorld();
        single.LotRoomMaps[1] = WaterRoomMap([]);
        var singleStack = AddObject(single, 1, -16, -16);
        Assert.True(single.FindLocation(singleStack, new FindLocationParams
        {
            CenterX = 2 * 16 + 8, CenterY = 2 * 16 + 8, Height = 1, Direction = -1,
            ContinueFlag = 0, FlagB = 1,
        }, out _));
    }

    [Fact]
    public void SearchRejectsOpenStyleDiagonalWallsThatDoNotSplitRooms()
    {
        var world = new SimulationWorld();
        var map = WaterRoomMap([], openDiagonalAtCandidate: true);
        world.LotRoomMaps[1] = map;
        var stack = AddObject(world, 1, -16, -16);
        AddObject(world, 2, 2 * 16 + 8, 2 * 16 + 8);

        Assert.False(map.HasRoomDividingDiagonal(1, 2));
        Assert.True(map.HasDiagonalWall(1, 2));
        Assert.True(world.FindLocation(stack, new FindLocationParams
        {
            CenterX = 2 * 16 + 8, CenterY = 2 * 16 + 8, Height = 1, Direction = -1,
            ContinueFlag = 0, FlagB = 0,
        }, out var found));

        Assert.Equal(new ObjectPosition(1 * 16 + 8, 3 * 16 + 8, 1), found);
    }
}
