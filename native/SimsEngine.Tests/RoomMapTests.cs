using System.Buffers.Binary;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using Xunit;

namespace SimsEngine.Tests;

/// <summary>Room fill on hand-built walls layers (Sims.exe FUN_00479f0c, edge test FUN_004351e4): rooms are numbered
/// from 0 in seed order.</summary>
public class RoomMapTests
{
    [Fact]
    public void RoomScoreUsesSourceCountersClampsAndLightingState()
    {
        var tuning = new SimsEngine.World.RoomScoreTuning();
        var litInput = new SimsEngine.World.RoomScoreInput(
            Field38: 20, Field3C: 10, Field44: 300, Field48: 200, Field50: 4, Field54: 1,
            Field60: 100, Field64: 50,
            Daylight: 0.5f, Outdoor: false, LightsEnabledFlag73: 1, LightingObjectCount140: 1,
            PersonObjectCount142: 1, DaylightCeiling: 0.8f);

        var result = tuning.Calculate(litInput);

        Assert.Equal(1f, result.Light);
        Assert.Equal(1f, result.TileLight);
        Assert.Equal(-36f, result.RoomScore);

        var unlitInput = litInput with { Field44 = 0, Field48 = 0, Field50 = -100, Field3C = 0,
            Field64 = 0, LightsEnabledFlag73 = 0, LightingObjectCount140 = 0,
            PersonObjectCount142 = 1, DaylightCeiling = 0.8f };
        var unlit = tuning.Calculate(unlitInput);
        Assert.Equal(0f, unlit.Light);
        Assert.Equal(0f, unlit.TileLight);
        Assert.Equal(-100f, unlit.RoomScore);

        var autoCeiling = tuning.Calculate(unlitInput with { LightsEnabledFlag73 = 1 });
        Assert.Equal(0f, autoCeiling.Light);
        Assert.Equal(0.8f, autoCeiling.TileLight);

        var outdoor = tuning.Calculate(litInput with { Outdoor = true, Field50 = 2, Field54 = 3,
            Daylight = 0.5f });
        Assert.Equal(0.5f, outdoor.Light);
        Assert.Equal(0.5f, outdoor.TileLight);
        Assert.Equal(7f, outdoor.RoomScore);
    }

    /// <summary>A walls layer (chunk 2, 8-byte elements, indexed [x][y]) whose record for each tile comes from
    /// <paramref name="wall"/> as (segments, style of side 1, style of side 2).</summary>
    private static IffFile Walls(int width, int height, Func<int, int, (byte segments, byte side1, byte side2)> wall,
        byte sideFlags = 0x0f)
    {
        var body = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0);
        for (int x = 0; x < width; x++)
        for (int y = 0; y < height; y++)
        {
            var (segments, side1, side2) = wall(x, y);
            body.Raw([segments, sideFlags, side1, side2, 0, 0, 0, 0]);
        }
        return new IffFile(ChunkBuilder.File(("Arry", 2, body.ToArray())), "test.iff");
    }

    private static IffFile WallsWithWater(int width, int height, byte[] water)
    {
        var wallBody = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0);
        for (var i = 0; i < width * height; i++) wallBody.Raw([0, 0x0F, 0, 0, 0, 0, 0, 0]);
        var waterBody = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0).Raw(water).ToArray();
        var floorBody = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(new byte[width * height]).ToArray();
        return new IffFile(ChunkBuilder.File(("Arry", 1, floorBody), ("Arry", 2, wallBody.ToArray()), ("Arry", 9, waterBody)), "water.iff");
    }

    private const byte TowardsLowerX = 0x01, TowardsLowerY = 0x02, TowardsHigherX = 0x04, TowardsHigherY = 0x08;
    private const byte Diagonal = 0x10;
    private const byte Wall = 1, Fence = 2;
    private static readonly (byte, byte, byte) Empty = (0, 0, 0);

    [Fact]
    public void PoolClassificationUsesArryNineEvenWhenTileFlagsSaySomethingElse()
    {
        static byte[] Plane(byte[] values) => new ChunkBuilder().S16(0).S16(0).S32(2).S32(1).S32(1).U32(0).Raw(values).ToArray();
        var walls = Walls(2, 1, (_, _) => Empty);
        var wallChunk = walls.Find(ResourceType.Parse("Arry"), 2)!;
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, wallChunk.Data.ToArray()),
            ("Arry", 8, Plane([7, 7])), ("Arry", 9, Plane([0xff, 0x0f]))), "pool-vs-tile-flags.iff");

        var map = RoomMap.Build(file, 1, 2, 1);

        Assert.True(map.TryGetWaterGridByte(0, 0, out var dry));
        Assert.Equal((byte)0xff, dry);
        Assert.True(map.TryGetWaterGridByte(1, 0, out var pool));
        Assert.Equal((byte)0x0f, pool);
        Assert.NotEqual(map.RoomAt(0, 0), map.RoomAt(1, 0));
    }

    [Fact]
    public void MissingPoolPlaneKeepsTheOriginalConstructorNoPoolSentinel()
    {
        var map = RoomMap.Build(Walls(2, 1, (_, _) => Empty), 1, 2, 1);
        Assert.Equal((byte)0xff, map.WaterGridByteAt(0, 0));
        Assert.False(map.LightControlFlag75(0));
    }

    [Fact]
    public void OpenLotIsOneRoom()
    {
        var map = RoomMap.Build(Walls(3, 3, (_, _) => Empty), 1, 3, 3);
        Assert.Equal(1, map.RoomCount);
        Assert.All(Enumerable.Range(0, 9), i => Assert.Equal(0, map.RoomAt(i % 3, i / 3)));
    }

    [Fact]
    public void RuntimeWallGridLoadsEachLevelFromItsSourceArryLayer()
    {
        static byte[] WallLayer(byte segments, byte second, byte[] words)
        {
            var body = new ChunkBuilder().S16(0).S16(0).S32(1).S32(1).S32(8).U32(0)
                .Raw([segments, second, words[0], words[1], words[2], words[3], words[4], words[5]]);
            return body.ToArray();
        }

        var file = new IffFile(ChunkBuilder.File(
            ("Arry", 2, WallLayer(0x01, 0x11, [1, 2, 3, 4, 5, 6])),
            ("Arry", 102, WallLayer(0x20, 0x22, [7, 8, 9, 10, 11, 12]))), "per-level-runtime-walls.iff");
        var ground = RoomMap.Build(file, 1, 1, 1);
        var upper = RoomMap.Build(file, 2, 1, 1);

        Assert.Equal(new RoomRuntimeWallCell(0x01, 0x11, 1, 2, 3, 4, 5, 6), ground.RuntimeWallAt(0, 0));
        Assert.Equal(new RoomRuntimeWallCell(0x20, 0x22, 7, 8, 9, 10, 11, 12), upper.RuntimeWallAt(0, 0));
        Assert.Equal(0x01u, ground.ExpandedWallAt(0, 0)!.Value.UInt32At(0));
        Assert.Equal(0x20u, upper.ExpandedWallAt(0, 0)!.Value.UInt32At(0));
    }

    [Fact]
    public void RoomKeyWallAdjacencyMapsTheSourceTwoByTwoSegmentWindow()
    {
        var map = RoomMap.Build(Walls(4, 4, (x, y) => (x, y) switch
        {
            (1, 1) => ((byte)0x28, (byte)0, (byte)0),
            (2, 1) => ((byte)0x11, (byte)0, (byte)0),
            (1, 2) => ((byte)0x14, (byte)0, (byte)0),
            (2, 2) => ((byte)0x22, (byte)0, (byte)0),
            _ => Empty,
        }), 1, 4, 4);

        Assert.Equal((byte)0xff, map.RoomKeyWallAdjacencyAt(2, 2));
        Assert.Equal((byte)0x40, map.RoomKeyWallAdjacencyAt(1, 1));
    }

    [Fact]
    public void PortalWallRefreshRecomputesTheFourSourceRoomKeyBytes()
    {
        var map = RoomMap.Build(Walls(4, 4, (_, _) => Empty), 1, 4, 4);
        var wallBytes = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(wallBytes, 0x04);
        var wall = new RoomExpandedWallRecord(wallBytes);

        Assert.Equal((byte)0, map.RoomKeyWallAdjacencyGridByteAt(2, 1));
        Assert.True(map.ApplyWallStyleOverlay(1, 1, 5, 0x04, wall));

        Assert.Equal((byte)0x04, map.RoomKeyWallAdjacencyGridByteAt(2, 1));
        Assert.True(map.ApplyWallStyleOverlay(1, 1, 5, 0x04, new RoomExpandedWallRecord(new byte[44])));
        Assert.Equal((byte)0, map.RoomKeyWallAdjacencyGridByteAt(2, 1));
    }

    [Fact]
    public void RoomTileProjectionPreservesRoomFillTraversalOrder()
    {
        var map = RoomMap.Build(Walls(4, 1, (_, _) => Empty), 1, 4, 1);

        Assert.Equal(new[] { (0, 0), (1, 0), (2, 0), (3, 0) }, map.TilesInRoom(map.RoomAt(0, 0)));
        Assert.Empty(map.TilesInRoom(RoomMap.NoRoom));
        Assert.Empty(map.TilesInRoom(0x1234));
    }

    [Fact]
    public void RoomTileProjectionUsesSourceFifoAndNeighborSideOrder()
    {
        var map = RoomMap.Build(Walls(3, 3, (_, _) => Empty), 1, 3, 3);

        Assert.Equal(new[]
        {
            (0, 0), (0, 1), (1, 0), (0, 2), (1, 1), (2, 0), (1, 2), (2, 1), (2, 2),
        }, map.TilesInRoom(map.RoomAt(0, 0)));
    }

    [Fact]
    public void WallTileProjectionPreservesOriginalSegmentsAndStyleBytes()
    {
        var body = new ChunkBuilder().S16(0).S16(0).S32(1).S32(1).S32(8).U32(0)
            .Raw([TowardsHigherX, 0xa1, 7, 9, 4, 5, 6, 7]).ToArray();
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, body)), "wall-record.iff");
        var map = RoomMap.Build(file, 1, 1, 1);

        Assert.Equal(new RoomWallTile(TowardsHigherX, 0xa1, 7, 9, 4, 5, 6, 7), map.WallAt(0, 0));
        Assert.Equal(new RoomRuntimeWallCell(TowardsHigherX, 0xa1, 7, 9, 4, 5, 6, 7), map.RuntimeWallAt(0, 0));
        Assert.Null(map.WallAt(-1, 0));
        Assert.Null(map.RuntimeWallAt(1, 0));
    }

    [Fact]
    public void ExpandedWallRecordUsesRuntimeWordsAndCardinalDiagonalFlags()
    {
        var body = new ChunkBuilder().S16(0).S16(0).S32(3).S32(3).S32(8).U32(0);
        for (var x = 0; x < 3; x++)
        for (var y = 0; y < 3; y++)
        {
            var segments = x == 1 && y == 1 ? (byte)0x30 :
                x == 0 && y == 1 || x == 2 && y == 1 || x == 1 && y == 0 || x == 1 && y == 2 ? (byte)0x30 : (byte)0;
            body.Raw([segments, 0x0f, 1, 2, 4, 5, 6, 7]);
        }
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, body.ToArray())), "expanded-wall.iff");
        var map = RoomMap.Build(file, 1, 3, 3);

        var expanded = map.ExpandedWallAt(1, 1)!.Value;
        Assert.Equal(44, expanded.Bytes.Length);
        Assert.Equal(0x30u, expanded.UInt32At(0));
        Assert.Equal(0x0fu, expanded.UInt32At(4));
        Assert.Equal((ushort)1, expanded.UInt16At(8));
        Assert.Equal((ushort)2, expanded.UInt16At(10));
        Assert.Equal((ushort)0, expanded.UInt16At(12));
        Assert.Equal((ushort)0, expanded.UInt16At(14));
        Assert.Equal((ushort)2, expanded.UInt16At(16));
        Assert.Equal((ushort)2, expanded.UInt16At(18));
        Assert.Equal((ushort)4, expanded.UInt16At(20));
        Assert.Equal((ushort)5, expanded.UInt16At(22));
        Assert.Equal((ushort)7, expanded.UInt16At(24));
        Assert.Equal((ushort)6, expanded.UInt16At(26));
        Assert.Equal((ushort)6, expanded.UInt16At(28));
        Assert.Equal((ushort)7, expanded.UInt16At(30));
        Assert.Equal((ushort)6, expanded.UInt16At(32));
        Assert.Equal((ushort)7, expanded.UInt16At(34));
        Assert.Equal(0x1fu, expanded.UInt32At(40));
        Assert.Null(map.ExpandedWallAt(-1, 0));
    }

    [Fact]
    public void ExpandedWallRecordAppliesBelowAndRightWallOverlays()
    {
        var body = new ChunkBuilder().S16(0).S16(0).S32(2).S32(2).S32(8).U32(0)
            .Raw([0, 0x0f, 0, 0, 0, 0, 0, 0])
            .Raw([2, 12, 0, 9, 0, 0, 0, 0])
            .Raw([1, 3, 7, 0, 0, 0, 0, 0])
            .Raw([0, 0, 0, 0, 0, 0, 0, 0]).ToArray();
        var map = RoomMap.Build(new IffFile(ChunkBuilder.File(("Arry", 2, body)), "wall-overlays.iff"), 1, 2, 2);

        var expanded = map.ExpandedWallAt(0, 0)!.Value;
        Assert.Equal((ushort)7, expanded.UInt16At(12));
        Assert.Equal((ushort)9, expanded.UInt16At(14));
        Assert.Equal(0xffu, expanded.UInt32At(4));
    }

    [Fact]
    public void RoomStatisticsInputExposesKnownGroundGridAndExpandedWallSources()
    {
        var map = RoomMap.Build(WallsWithWater(4, 1, [0, 15, 16, 255]), 1, 4, 1);

        var dry = map.StatisticsInputAt(1, 0)!.Value;
        Assert.Equal((byte)15, dry.RawGroundWaterGridByte);
        Assert.Equal((ushort)0, dry.FloorGridWord);
        Assert.Equal((ushort)0, dry.Walls.UInt16At(8));

        var water = map.StatisticsInputAt(2, 0)!.Value;
        Assert.Equal((byte)16, water.RawGroundWaterGridByte);
        Assert.Null(map.StatisticsInputAt(4, 0));
    }

    [Fact]
    public void RoomStatisticsInputReadsGroundGridOnUpperLevelsLikeTheExe()
    {
        var wall = new ChunkBuilder().S16(0).S16(0).S32(1).S32(1).S32(8).U32(0)
            .Raw([0, 0, 0, 0, 0, 0, 0, 0]).ToArray();
        var floor = new ChunkBuilder().S16(0).S16(0).S32(1).S32(1).S32(1).U32(0).U8(1).ToArray();
        var water = new ChunkBuilder().S16(0).S16(0).S32(1).S32(1).S32(1).U32(0).U8(0x20).ToArray();
        var file = new IffFile(ChunkBuilder.File(("Arry", 102, wall), ("Arry", 101, floor), ("Arry", 9, water)), "upper-water.iff");
        var map = RoomMap.Build(file, 2, 1, 1);

        Assert.False(map.TryGetWaterGridByte(0, 0, out _));
        var input = map.StatisticsInputAt(0, 0)!.Value;
        Assert.Equal((byte)0x20, input.RawGroundWaterGridByte);
        Assert.Equal((ushort)1, input.FloorGridWord);
    }

    [Fact]
    public void ExpandedWallCategoryLookupsFollowSimsExecutableIndexing()
    {
        var bytes = new byte[44];
        // FUN_00467ece reads offset 8 + 2 * trailingZeroCount(mask).
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14, 2), 7); // mask 0x08
        // FUN_00467e89 reads offset 20 + the same index, with FUN_00435221's side adjustment.
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), 23); // index 4
        var walls = new RoomExpandedWallRecord(bytes);

        Assert.Equal(7, walls.WallCategory(0x08));
        Assert.Equal(23, walls.AdjacentWallCategory(0x08, sideSelector: 1));
        Assert.True(RoomExpandedWallRecord.IsRoomWallCategory(23));
        Assert.False(RoomExpandedWallRecord.IsRoomWallCategory(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => walls.WallCategory(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => walls.WallCategory(0x0c));
    }

    [Fact]
    public void PortalWallOverlayWritesSelectedCategoryWordsAndPreservesTheInputRecord()
    {
        var bytes = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x28, 4), 0x02);
        var walls = new RoomExpandedWallRecord(bytes);

        var overlaid = walls.WithWallStyleOverlay(13, 0x28);

        Assert.Equal((ushort)13, overlaid.UInt16At(14));
        Assert.Equal((ushort)13, overlaid.UInt16At(18));
        Assert.Equal((ushort)0, overlaid.UInt16At(16));
        Assert.Equal(0x12u, overlaid.UInt32At(0x28));
        Assert.Equal((ushort)0, walls.UInt16At(14));
        Assert.Equal(0x02u, walls.UInt32At(0x28));
    }

    [Fact]
    public void WallSideOrientation_UsesPackedTwoBitCardinalFields()
    {
        var bytes = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 0b11_10_01_00);
        var record = new RoomExpandedWallRecord(bytes);

        Assert.Equal(0, record.WallSideOrientation(1));
        Assert.Equal(1, record.WallSideOrientation(2));
        Assert.Equal(2, record.WallSideOrientation(4));
        Assert.Equal(3, record.WallSideOrientation(8));
        Assert.Equal(3, record.WallSideOrientation(0x10));

        var updated = record.WithWallSideOrientation(4, 1);
        Assert.Equal(0b11_01_01_00u, updated.UInt32At(4));
        Assert.Equal(0b11_10_01_00u, record.UInt32At(4));
        Assert.Equal(record.UInt32At(4), record.WithWallSideOrientation(0x10, 0).UInt32At(4));
    }

    [Theory]
    [InlineData(0, 0x01u)]
    [InlineData(1, 0x02u)]
    [InlineData(2, 0x04u)]
    [InlineData(3, 0x08u)]
    public void ExpandedWallRotationMovesCardinalCategoriesAndOrientations(int turns, uint expectedSegment)
    {
        var bytes = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x01);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 0b01_10_11_10);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10, 2), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12, 2), 6);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14, 2), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 5);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(24, 2), 7);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26, 2), 9);
        var original = new RoomExpandedWallRecord(bytes);

        var rotated = original.WithRotatedSides(turns);

        Assert.Equal(expectedSegment, rotated.UInt32At(0) & 0x0f);
        Assert.Equal((ushort)2, rotated.WallCategory(expectedSegment));
        Assert.Equal((byte)2, rotated.WallSideOrientation(expectedSegment));
        Assert.Equal((ushort)3, rotated.AdjacentWallCategory(expectedSegment));
        Assert.Equal(0x10u, rotated.UInt32At(40) & 0x10);
        Assert.Equal(0x01u, original.UInt32At(0));
    }

    [Theory]
    [InlineData(0, 0x10u, 0x10u, 15)]
    [InlineData(1, 0x10u, 0x20u, 15)]
    [InlineData(2, 0x10u, 0x10u, 15)]
    [InlineData(1, 0x20u, 0x10u, 23)]
    public void ExpandedWallRotationSwapsDiagonalBitsAndCategoriesByParity(int turns, uint sourceBit,
        uint expectedBit, ushort category)
    {
        var bytes = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), sourceBit);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16, 2), 15);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18, 2), 23);
        var original = new RoomExpandedWallRecord(bytes);

        var rotated = original.WithRotatedSides(turns);

        Assert.Equal(expectedBit, rotated.UInt32At(0) & 0x30);
        Assert.Equal(category, rotated.WallCategory(expectedBit));
        Assert.Equal(sourceBit, original.UInt32At(0));
    }

    [Theory]
    [InlineData(0, 101, 102, 103, 104)]
    [InlineData(1, 103, 104, 102, 101)]
    [InlineData(2, 102, 101, 104, 103)]
    [InlineData(3, 104, 103, 101, 102)]
    public void ExpandedWallRotationRemapsAllDiagonalAdjacentCategories(int turns, ushort first, ushort second,
        ushort third, ushort fourth)
    {
        var bytes = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x30);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), 101);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(30, 2), 102);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 103);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), 104);
        var original = new RoomExpandedWallRecord(bytes);

        var rotated = original.WithRotatedSides(turns);

        Assert.Equal(first, rotated.UInt16At(28));
        Assert.Equal(second, rotated.UInt16At(30));
        Assert.Equal(third, rotated.UInt16At(32));
        Assert.Equal(fourth, rotated.UInt16At(34));
        Assert.Equal((ushort)101, original.UInt16At(28));
    }

    [Fact]
    public void PortalWallRefreshAddsReciprocalBitsToAdjacentRuntimeCells()
    {
        var map = RoomMap.Build(Walls(4, 4, (_, _) => (0, 0, 0)), 1, 4, 4);
        var bytes = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x0f);
        for (var index = 0; index < 4; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8 + index * 2, 2), 1);
        var refreshed = new RoomExpandedWallRecord(bytes).WithWallStyleOverlay(5, 0x02);

        Assert.True(map.ApplyWallStyleOverlay(1, 1, 5, 0x02, refreshed));

        Assert.Equal(0x0fu, map.ExpandedWallAt(1, 1)!.Value.UInt32At(0) & 0x0f);
        Assert.Equal(0x04u, map.ExpandedWallAt(0, 1)!.Value.UInt32At(0) & 0x04);
        Assert.Equal(0x08u, map.ExpandedWallAt(1, 0)!.Value.UInt32At(0) & 0x08);
        Assert.Equal(0x01u, map.ExpandedWallAt(2, 1)!.Value.UInt32At(0) & 0x01);
        Assert.Equal(0x02u, map.ExpandedWallAt(1, 2)!.Value.UInt32At(0) & 0x02);
        Assert.Equal((ushort)1, map.ExpandedWallAt(2, 1)!.Value.WallCategory(0x01));
        Assert.Equal((ushort)1, map.ExpandedWallAt(1, 2)!.Value.WallCategory(0x02));
        Assert.Equal((ushort)5, map.ExpandedWallAt(1, 1)!.Value.WallCategory(0x02));
    }

    [Fact]
    public void PortalWallRefreshCopiesPackedOrientationsToReciprocalCells()
    {
        var map = RoomMap.Build(Walls(3, 3, (_, _) => (0, 0, 0), sideFlags: 0), 1, 3, 3);
        var bytes = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x0f);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 0b10_01_11_00);
        var expanded = new RoomExpandedWallRecord(bytes);

        Assert.True(map.ApplyWallStyleOverlay(1, 1, 5, 0x0f, expanded));

        Assert.Equal(0b00, map.RuntimeWallAt(0, 1)!.Value.Byte1 & 0b11);
        Assert.Equal(0b11, (map.RuntimeWallAt(1, 1)!.Value.Byte1 >> 2) & 0b11);
        Assert.Equal(0b01, map.RuntimeWallAt(2, 1)!.Value.Byte1 & 0b11);
        Assert.Equal(0b10, (map.RuntimeWallAt(1, 2)!.Value.Byte1 >> 2) & 0b11);
    }

    [Fact]
    public void PortalWallRefreshClearsReciprocalSegmentsAbsentFromTheExpandedRecord()
    {
        var map = RoomMap.Build(Walls(3, 3, (x, y) =>
            x == 1 && y == 1 ? ((byte)0x04, (byte)3, (byte)0) :
            x == 2 && y == 1 ? ((byte)0x01, (byte)7, (byte)0) : Empty), 1, 3, 3);
        var empty = new RoomExpandedWallRecord(new byte[44]);
        Assert.Equal(0x01u, map.ExpandedWallAt(2, 1)!.Value.UInt32At(0) & 0x01);
        var currentOrientation = map.RuntimeWallAt(1, 1)!.Value.Byte1;
        var reciprocalOrientation = map.RuntimeWallAt(2, 1)!.Value.Byte1;

        Assert.True(map.ApplyWallStyleOverlay(1, 1, 5, 0x04, empty));

        Assert.Equal(0u, map.ExpandedWallAt(1, 1)!.Value.UInt32At(0) & 0x04);
        Assert.Equal(0u, map.ExpandedWallAt(2, 1)!.Value.UInt32At(0) & 0x01);
        Assert.Equal((ushort)0, map.RuntimeWallAt(2, 1)!.Value.Word6);
        // FUN_00479954 clears the segment and reciprocal category words without changing packed side orientation.
        Assert.Equal(currentOrientation, map.RuntimeWallAt(1, 1)!.Value.Byte1);
        Assert.Equal(reciprocalOrientation, map.RuntimeWallAt(2, 1)!.Value.Byte1);
    }

    [Fact]
    public void PortalWallRefreshSynchronizesDiagonalSegmentBitsBeforeCardinalSides()
    {
        var map = RoomMap.Build(Walls(3, 3, (x, y) =>
            x == 1 && y == 1 ? ((byte)0x30, (byte)0, (byte)0) : Empty), 1, 3, 3);
        var diagonal = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(diagonal, 0x31);

        Assert.True(map.ApplyWallStyleOverlay(1, 1, 5, 0, new RoomExpandedWallRecord(diagonal)));

        // FUN_00479954 assigns 0x10, then 0x20, before OR-ing cardinal sides; when both diagonal bits are present,
        // the latter assignment leaves only 0x20. This preserves the source routine's byte-write order.
        Assert.Equal(0x21u, map.RuntimeWallAt(1, 1)!.Value.Segments);
        var empty = new RoomExpandedWallRecord(new byte[44]);
        Assert.True(map.ApplyWallStyleOverlay(1, 1, 5, 0, empty));
        Assert.Equal(0, map.RuntimeWallAt(1, 1)!.Value.Segments & 0x30);
    }

    [Theory]
    [InlineData(0x10, 200, 0, 204, 205)]
    [InlineData(0x20, 0, 200, 206, 207)]
    public void PortalWallRefreshWritesDiagonalCategoryAndAdjacentWordsFromExpandedRecord(
        int diagonalBit, int expectedWord6, int expectedWord8, int expectedWord10, int expectedWord12)
    {
        const ushort wallStyle = 0x345;
        var map = RoomMap.Build(Walls(3, 3, (_, _) => Empty), 1, 3, 3);
        var bytes = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), (uint)diagonalBit);
        for (var index = 0; index < 6; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8 + index * 2, 2), (ushort)(100 + index));
        for (var index = 0; index < 8; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20 + index * 2, 2), (ushort)(200 + index));
        var refreshed = new RoomExpandedWallRecord(bytes).WithWallStyleOverlay(wallStyle, (uint)diagonalBit);

        Assert.True(map.ApplyWallStyleOverlay(1, 1, wallStyle, (uint)diagonalBit, refreshed));

        var cell = map.RuntimeWallAt(1, 1)!.Value;
        Assert.Equal((ushort)100, cell.Word2);
        Assert.Equal(wallStyle, cell.Word4);
        Assert.Equal((ushort)expectedWord6, cell.Word6);
        Assert.Equal((ushort)expectedWord8, cell.Word8);
        Assert.Equal((ushort)expectedWord10, cell.Word10);
        Assert.Equal((ushort)expectedWord12, cell.Word12);
    }

    [Fact]
    public void RemovingDiagonalWallPreservesItsCachedCategoryWords()
    {
        var map = RoomMap.Build(Walls(3, 3, (_, _) => Empty), 1, 3, 3);
        var bytes = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x10);
        for (var index = 0; index < 6; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8 + index * 2, 2), (ushort)(100 + index));
        for (var index = 0; index < 8; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20 + index * 2, 2), (ushort)(200 + index));

        Assert.True(map.ApplyWallStyleOverlay(1, 1, 0, 0, new RoomExpandedWallRecord(bytes)));
        var beforeRemoval = map.RuntimeWallAt(1, 1)!.Value;
        Assert.True(map.ApplyWallStyleOverlay(1, 1, 0, 0, new RoomExpandedWallRecord(new byte[44])));

        var afterRemoval = map.RuntimeWallAt(1, 1)!.Value;
        Assert.Equal((byte)0, afterRemoval.Segments & 0x10);
        Assert.Equal(beforeRemoval.Word2, afterRemoval.Word2);
        Assert.Equal(beforeRemoval.Word4, afterRemoval.Word4);
        Assert.Equal(beforeRemoval.Word6, afterRemoval.Word6);
        Assert.Equal(beforeRemoval.Word10, afterRemoval.Word10);
        Assert.Equal(beforeRemoval.Word12, afterRemoval.Word12);
    }

    [Fact]
    public void RoomTileStatisticsAccumulatesSourceDefinedNoDiagonalBranches()
    {
        var bytes = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 9);
        var input = new RoomTileStatisticsInput(15, 1, new RoomExpandedWallRecord(bytes));

        Assert.Equal(new RoomTileStatisticsContribution(2, 2, 1, 1, 1), RoomTileStatistics.AccumulateKnown(input, roomBranchFlag: false));
        Assert.Equal(new RoomTileStatisticsContribution(2, 0, 1, 0, 0), RoomTileStatistics.AccumulateKnown(input, roomBranchFlag: true));
        var diagonal = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(diagonal, 0x10);
        Assert.Equal(new RoomTileStatisticsContribution(1, 0, 1, 0, 0), RoomTileStatistics.AccumulateKnown(
            input with { Walls = new RoomExpandedWallRecord(diagonal), FloorGridWord = null }, roomBranchFlag: false));
    }

    [Theory]
    [InlineData(0x10, 8, 20, 28, 30)]
    [InlineData(0x20, 8, 20, 32, 34)]
    public void RoomTileStatisticsUsesOriginalDiagonalSelectorRecords(
        int diagonalBit, int selectedOffset10, int selectedOffset20, int firstAdjacentOffset, int secondAdjacentOffset)
    {
        var bytes = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)diagonalBit);
        var baseWallIndex = diagonalBit == 0x10 ? 4 : 5;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8 + baseWallIndex * 2), 5);
        // The fixed sVar3 read is at expanded-record offset 20 for 0x10 and 8 for 0x20.
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(diagonalBit == 0x10 ? selectedOffset20 : selectedOffset10), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(firstAdjacentOffset), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(secondAdjacentOffset), 1);
        var input = new RoomTileStatisticsInput(0, null, new RoomExpandedWallRecord(bytes));

        Assert.Equal(new RoomTileStatisticsContribution(1, 1, 1, 1, 1),
            RoomTileStatistics.AccumulateKnown(input, roomBranchFlag: false));
    }

    [Fact]
    public void RoomTileStatisticsPrefersSelectorRecord10WhenBothDiagonalsAreSet()
    {
        var bytes = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x30);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8 + 4 * 2), 5);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20 + 4 * 2), 1);

        Assert.Equal(new RoomTileStatisticsContribution(1, 1, 1, 1, 1), RoomTileStatistics.AccumulateKnown(
            new RoomTileStatisticsInput(0, null, new RoomExpandedWallRecord(bytes)), roomBranchFlag: false));
    }

    [Fact]
    public void RoomObjectStatisticsAppliesSourceEligibilityAndKnownObjectCounters()
    {
        XObject Make(short id, short room, short category, bool hidden = false, short type = 0, short roomImpact = 0,
            bool lighting = false, short lightContribution = 0)
        {
            var definition = new short[XObject.DefinitionCount];
            definition[(int)ObjdField.Type] = type;
            var item = new XObject(id, definition) { RoomWord84 = room };
            item[ObjectData.Category] = category;
            item[(ObjectData)34] = hidden ? (short)1 : (short)0;
            item[ObjectData.RoomImpact] = roomImpact;
            item[ObjectData.FlagField2] = lighting ? (short)0x400 : (short)0;
            item[ObjectData.LightingContribution0100] = lightContribution;
            return item;
        }

        var objects = new[]
        {
            Make(1, 2, 4, roomImpact: 3, lighting: true, lightContribution: 10),
            Make(2, 2, 6, type: 8, lighting: true, lightContribution: 3),
            Make(3, 2, 8, roomImpact: -2, lighting: true), Make(4, -5, 4),
            Make(5, 2, 5, hidden: true, roomImpact: 10), Make(6, 2, 1, type: XPerson.PersonType),
        };

        Assert.Equal(new RoomObjectStatisticsContribution(4, 1, 1, true, 1, 1, 1, 3, 2, 3, 10, 1),
            RoomObjectStatistics.AccumulateKnown(objects, statisticsWord0: 1, roomBranchFlag: false, lightingGate: _ => true));
        Assert.Equal(new RoomObjectStatisticsContribution(4, 1, 1, false, 1, 1, -2, 3, 2, 3, 10, 1),
            RoomObjectStatistics.AccumulateKnown(objects, statisticsWord0: 0, roomBranchFlag: true, lightingGate: _ => true));
        Assert.Equal(new RoomObjectStatisticsContribution(4, 1, 1, true, 1, 1, 1, 3, 0, 0, 0, 0),
            RoomObjectStatistics.AccumulateKnown(objects, statisticsWord0: 1, roomBranchFlag: false, lightingGate: _ => false));
    }

    [Fact]
    public void RoomObjectStatisticsAppliesSourceLightGridRemoveAndAddCallbacks()
    {
        XObject Make(short id, short contribution)
        {
            var definition = new short[XObject.DefinitionCount];
            definition[(int)ObjdField.Type] = 4;
            var item = new XObject(id, definition) { RoomWord84 = 2, Position = new(64, 80, 1) };
            item[ObjectData.Category] = 4;
            item[ObjectData.FlagField2] = 0x400;
            item[ObjectData.LightingContribution0100] = contribution;
            return item;
        }

        var grid = new RoomLightCoordinateGrid();
        var context = new RoomLightGridCallbackContext(grid, LightingScalar: 3f, LevelAt: item => item.Position.Height,
            RoomAt: (_, _, _) => 2);
        var contribution = RoomObjectStatistics.AccumulateKnown([Make(1, 10)], statisticsWord0: 0,
            roomBranchFlag: false, lightingGate: _ => true, lightGrid: context);

        Assert.Equal(10, contribution.OtherLightContribution24);
        // Object position is (x=4,y=5); source vectors store Y first and X second.
        Assert.Equal(new RoomLightCoordinateCell(-1, -1, RoomLightCoordinateCell.EmptyCoordinate,
            RoomLightCoordinateCell.EmptyCoordinate), grid.At(4, 3, 1));
        Assert.Equal(RoomLightCoordinateCell.Empty, grid.At(4, 3, 2));

        var zeroContribution = RoomObjectStatistics.AccumulateKnown([Make(2, 0)], statisticsWord0: 0,
            roomBranchFlag: false, lightingGate: _ => true, lightGrid: context);
        Assert.Equal(1, zeroContribution.ZeroContributionLightCallbacks);
        Assert.Equal(RoomLightCoordinateCell.Empty, grid.At(4, 3, 1));
    }

    [Fact]
    public void RoomRuntimeStatisticsAggregatesTileAndObjectFieldsInSourceOrder()
    {
        var stats = new RoomRuntimeStatistics();
        stats.Add(new RoomTileStatisticsContribution(2, 3, 2, 4, 5));
        stats.Add(new RoomObjectStatisticsContribution(2, 1, 2, true, 1, 6, 7, 1, 3, 8, 9, 1));
        stats.Add(new RoomObjectStatisticsContribution(1, 0, 1, false, 0, -2, -3, 0, 1, 0, 0, 2));

        Assert.Equal(2, stats.Counter38);
        Assert.Equal(3, stats.Counter3C);
        Assert.Equal(2, stats.Counter54);
        Assert.Equal(4, stats.Counter60);
        Assert.Equal(5, stats.Counter64);
        Assert.Equal(3, stats.EligibleObjects20);
        Assert.Equal(1, stats.Category4Objects2C);
        Assert.Equal(3, stats.Category5To7Objects2E);
        Assert.False(stats.Category8Flag3A); // FUN_00467ee7 overwrites this field as objects are visited.
        Assert.Equal(1, stats.PersonObjects142);
        Assert.Equal(4, stats.RoomImpactSum26);
        Assert.Equal(4, stats.RoomImpactSum28);
        Assert.Equal(1, stats.NonnegativeImpactCount2A);
        Assert.Equal(4, stats.LightingObjects140);
        Assert.Equal(8, stats.Type8LightContribution22);
        Assert.Equal(9, stats.OtherLightContribution24);
        Assert.Equal(3, stats.ZeroContributionLightCallbacks);

        Assert.Equal(new SimsEngine.World.RoomScoreInput(
            Field38: 2, Field3C: 3, Field44: 8, Field48: 9, Field50: 4, Field54: 3,
            Field60: 4, Field64: 5, Daylight: 0.75f, Outdoor: false,
            LightOverrideFlag75: 1, LightsEnabledFlag73: 1,
            LightingObjectCount140: 4, PersonObjectCount142: 1, DaylightCeiling: 0.9f),
            stats.ToScoreInput(daylight: 0.75f, outdoor: false, lightOverrideFlag75: 1,
                lightsEnabledFlag73: 1, daylightCeiling: 0.9f));
    }

    [Fact]
    public void RoomLightCoordinateCellKeepsTwoNearestCandidatesAndPreservesTies()
    {
        var cell = RoomLightCoordinateCell.Empty
            .AddCandidate(4, 0)
            .AddCandidate(0, 4);

        Assert.Equal(2, cell.Count);
        Assert.Equal(new RoomLightCoordinateCell(4, 0, 0, 4), cell);
        Assert.Equal(cell, cell.AddCandidate(-4, 0)); // ties the second candidate; source keeps existing entries
        Assert.Equal(new RoomLightCoordinateCell(2, 0, 0, 4), cell.AddCandidate(2, 0));
        Assert.Equal(new RoomLightCoordinateCell(1, 0, 2, 0), RoomLightCoordinateCell.Empty
            .AddCandidate(5, 0).AddCandidate(2, 0).AddCandidate(1, 0));
    }

    [Fact]
    public void RoomLightCoordinateCellRemovesAndCompactsExactCandidates()
    {
        var cell = RoomLightCoordinateCell.Empty.AddCandidate(-2, 3).AddCandidate(4, -1);

        Assert.Equal(new RoomLightCoordinateCell(4, -1, RoomLightCoordinateCell.EmptyCoordinate,
            RoomLightCoordinateCell.EmptyCoordinate), cell.RemoveCandidate(-2, 3));
        Assert.Equal(new RoomLightCoordinateCell(-2, 3, RoomLightCoordinateCell.EmptyCoordinate,
            RoomLightCoordinateCell.EmptyCoordinate), cell.RemoveCandidate(4, -1));
        Assert.Equal(cell, cell.RemoveCandidate(0, 0));
        Assert.Equal(RoomLightCoordinateCell.Empty, RoomLightCoordinateCell.Empty.AddCandidate(1, 2)
            .RemoveCandidate(1, 2));
    }

    [Fact]
    public void RoomLightGridAddsAndRemovesSourceAcrossSameRoomSquare()
    {
        var grid = new RoomLightCoordinateGrid();
        Assert.Equal(0, RoomLightCoordinateGrid.RadiusFromLightingScalar(2.99f));
        Assert.Equal(0, RoomLightCoordinateGrid.RadiusFromLightingScalar(
            RoomLightCoordinateGrid.SimsExecutableInitialLightingScalar));
        Assert.Equal(1, RoomLightCoordinateGrid.RadiusFromLightingScalar(3f));
        Assert.Equal(1, RoomLightCoordinateGrid.RadiusFromLightingScalar(5.99f));
        Assert.Equal(1, grid.AddSource(10, 20, 1, lightingScalar: 0f, (_, _, _) => 9));
        Assert.Equal(1, grid.At(10, 20, 1).Count);
        Assert.Equal(0, grid.At(20, 10, 1).Count); // Source linear index is second * 64 + first.

        Assert.Equal(9, grid.AddSource(3, 3, 1, lightingScalar: 3f, (_, _, _) => 7));
        Assert.Equal(new RoomLightCoordinateCell(-1, -1, RoomLightCoordinateCell.EmptyCoordinate,
            RoomLightCoordinateCell.EmptyCoordinate), grid.At(2, 2, 1));
        Assert.Equal(new RoomLightCoordinateCell(0, 0, RoomLightCoordinateCell.EmptyCoordinate,
            RoomLightCoordinateCell.EmptyCoordinate), grid.At(3, 3, 1));
        Assert.Equal(RoomLightCoordinateCell.Empty, grid.At(3, 3, 2));

        Assert.Equal(9, grid.RemoveSource(3, 3, 1, lightingScalar: 3f));
        Assert.Equal(RoomLightCoordinateCell.Empty, grid.At(2, 2, 1));
        Assert.Equal(RoomLightCoordinateCell.Empty, grid.At(3, 3, 1));
    }

    [Fact]
    public void NativeRoomRuntimeScalarsStartAtTheirExecutableImageValues()
    {
        var world = new SimulationWorld();

        Assert.Equal(0f, world.RoomLightRadiusScalar);
        Assert.Equal(0f, world.RoomDaylightScalar);
        world.RoomLightRadiusScalar = 6f;
        world.RoomDaylightScalar = 0.5f;
        Assert.Equal(6f, world.RoomLightRadiusScalar);
        Assert.Equal(0.5f, world.RoomDaylightScalar);
        Assert.Equal(0.5f, RoomRuntimeScalarDefaults.DaylightForTimePhase(0, world.RoomDaylightScalar));
        Assert.Equal(0.25f, RoomRuntimeScalarDefaults.DaylightForTimePhase(1, world.RoomDaylightScalar));
        Assert.Equal(0f, RoomRuntimeScalarDefaults.DaylightForTimePhase(2, world.RoomDaylightScalar));
        Assert.Equal(0.25f, RoomRuntimeScalarDefaults.DaylightForTimePhase(3, world.RoomDaylightScalar));
    }

    [Fact]
    public void RoomLightGridSkipsDifferentRoomsAndPortsUnassignedAdjacentRoomException()
    {
        var grid = new RoomLightCoordinateGrid();
        short RoomAt(int x, int y, int _) => (x, y) switch
        {
            (3, 3) => 7,
            (2, 2) => -5,
            (4, 4) => 8,
            _ => 7,
        };

        Assert.Equal(8, grid.AddSource(3, 3, 1, 3f, RoomAt,
            adjacentRooms: (x, y, _) => (x == 2 && y == 2 ? (short?)7 : null, null),
            bridgeUnassignedRoomCells: true));
        Assert.Equal(new RoomLightCoordinateCell(-1, -1, RoomLightCoordinateCell.EmptyCoordinate,
            RoomLightCoordinateCell.EmptyCoordinate), grid.At(2, 2, 1));
        Assert.Equal(RoomLightCoordinateCell.Empty, grid.At(4, 4, 1));
    }

    [Fact]
    public void RoomObjectStatisticsAccumulatesTileRootAndContainedPerson()
    {
        var world = new SimulationWorld();
        var position = new ObjectPosition(0x20, 0x30, 1);
        var root = world.Add(new XObject(1, new short[XObject.DefinitionCount]) { Position = position });
        var personDefinition = new short[XObject.DefinitionCount];
        personDefinition[(int)ObjdField.Type] = XPerson.PersonType;
        var person = new XObject(2, personDefinition) { Position = position };
        person[ObjectData.ParentId] = root.ClockManagerIndex;
        world.Add(person);
        root.RoomWord84 = person.RoomWord84 = 1;
        root[ObjectData.Category] = 4;
        root[ObjectData.RoomImpact] = 1;
        person[ObjectData.Category] = 8;
        person[ObjectData.RoomImpact] = -1;
        root.Slots.Add(person.ClockManagerIndex);
        person[ObjectData.SlotHierNumber] = 0;

        Assert.Equal(new RoomObjectStatisticsContribution(2, 1, 0, false, 1, 0, 0, 1, 0, 0, 0, 0),
            RoomObjectStatistics.AccumulateTile(world, 2, 3, 1, statisticsWord0: 0,
                roomBranchFlag: false, lightingGate: _ => false));
    }

    [Fact]
    public void LevelOneWaterClassificationSplitsTheRoomFill()
    {
        var map = RoomMap.Build(WallsWithWater(4, 1, [0, 0, 0x10, 0x10]), 1, 4, 1);
        Assert.Equal(2, map.RoomCount);
        Assert.Equal(map.RoomAt(0, 0), map.RoomAt(1, 0));
        Assert.Equal(map.RoomAt(2, 0), map.RoomAt(3, 0));
        Assert.NotEqual(map.RoomAt(0, 0), map.RoomAt(2, 0));
    }

    [Fact]
    public void UpperLevelFloorPresenceSplitsTheRoomFill()
    {
        var wallBody = new ChunkBuilder().S16(0).S16(0).S32(4).S32(1).S32(8).U32(0);
        for (var i = 0; i < 4; i++) wallBody.Raw([0, 0x0F, 0, 0, 0, 0, 0, 0]);
        var floorBody = new ChunkBuilder().S16(0).S16(0).S32(4).S32(1).S32(1).U32(0)
            .U8(0).U8(255).U8(13).U8(13).ToArray();
        var file = new IffFile(ChunkBuilder.File(("Arry", 102, wallBody.ToArray()), ("Arry", 101, floorBody)), "upper-floor.iff");

        var map = RoomMap.Build(file, 2, 4, 1);
        Assert.Equal(2, map.RoomCount);
        Assert.Equal((ushort)0x400, map.RoomAt(0, 0));
        Assert.Equal((ushort)0x401, map.RoomAt(1, 0));
        Assert.Equal(map.RoomAt(1, 0), map.RoomAt(2, 0));
        Assert.Equal(map.RoomAt(2, 0), map.RoomAt(3, 0));
        Assert.NotEqual(map.RoomAt(0, 0), map.RoomAt(1, 0));
    }

    [Fact]
    public void NativePositionUpdateRefreshesRoomWordAndOffLotSentinel()
    {
        var map = RoomMap.Build(Walls(64, 64, (x, _) => x switch
        {
            31 => (TowardsHigherX, 0, 0),
            32 => (TowardsLowerX, Wall, 0),
            _ => Empty,
        }), 1, 64, 64);
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = map;
        var item = world.Add(new XObject(1, PrimitiveTestWorld.Definition()));
        var related = world.Add(new XObject(2, PrimitiveTestWorld.Definition()));
        item.Slots.AddRange([0, 2]);
        var notices = new List<(int Message, int Value)>();
        world.AddNoticeListener(new SimulationNoticeListener((message, value) =>
        {
            notices.Add((message, value));
            return false;
        }));
        item.RoomChangeNoticeGateWord58 = 1;

        world.SetObjectPosition(item, new ObjectPosition(20 * 16, 20 * 16, 1));
        Assert.Equal((short)0, item.RoomWord84);
        Assert.Equal(item.RoomWord84, item.Data[(int)ObjectData.Room]);
        Assert.Equal(new ObjectPosition(20 * 16, 20 * 16, 1), related.Position);
        Assert.Equal((short)0, related.RoomWord84);
        world.SetObjectPosition(item, new ObjectPosition(40 * 16, 20 * 16, 1));
        Assert.Equal((short)1, item.RoomWord84);
        Assert.Equal(item.RoomWord84, item.Data[(int)ObjectData.Room]);
        Assert.Equal(new ObjectPosition(40 * 16, 20 * 16, 1), related.Position);
        Assert.Equal((short)1, related.RoomWord84);
        world.SetObjectPosition(item, new ObjectPosition(-16, -16, 1));
        Assert.Equal((short)-5, item.RoomWord84);
        Assert.Equal([(0xf3, -5), (0xf3, 0), (0xf3, 0), (0xf3, 1), (0xf3, 1), (0xf3, -5)], notices);
        item.RoomChangeNoticeGateWord58 = 0;
        world.SetObjectPosition(item, new ObjectPosition(20 * 16, 20 * 16, 1));
        Assert.Equal(6, notices.Count);
    }

    [Fact]
    public void ClosedSquareIsItsOwnRoomSeededAfterTheOutside()
    {
        // A wall is recorded on the tiles at both of its sides. Sides 4 and 8 take their style from the side 1 or 2
        // of the next tile.
        var map = RoomMap.Build(Walls(3, 3, (x, y) => (x, y) switch
        {
            (1, 1) => ((byte)(TowardsLowerX | TowardsLowerY | TowardsHigherX | TowardsHigherY), Wall, Wall),
            (0, 1) => (TowardsHigherX, 0, 0),
            (1, 0) => (TowardsHigherY, 0, 0),
            (2, 1) => (TowardsLowerX, Wall, 0),
            (1, 2) => (TowardsLowerY, 0, Wall),
            _ => Empty,
        }), 1, 3, 3);
        Assert.Equal(2, map.RoomCount);
        Assert.Equal(1, map.RoomAt(1, 1));
        Assert.Equal(0, map.RoomAt(0, 0));
        Assert.Equal(0, map.RoomAt(2, 2));
        // The unresolved FUN_00468851 room-grid bit is approximated from lot-edge reachability.
        Assert.True(map.IsOutside(0));
        Assert.False(map.IsOutside(1));
    }

    [Fact]
    public void RoomLightControlFlag75UsesFirstTileGroundGridByteThreshold()
    {
        var map = RoomMap.Build(WallsWithWater(4, 1, [0x0f, 0x0f, 0x10, 0x10]), 1, 4, 1);

        Assert.Equal(2, map.RoomCount);
        Assert.True(map.LightControlFlag75(map.RoomAt(0, 0)));
        Assert.False(map.LightControlFlag75(map.RoomAt(2, 0)));
    }

    [Fact]
    public void PersonEnteringIndoorRoomEnablesLightsAndWritesRoomTiles()
    {
        var map = EnclosedRoomMap();
        var world = new SimulationWorld();
        world.LoadRoomMaps(new Dictionary<int, RoomMap> { [1] = map });
        var roomId = map.RoomAt(1, 1);
        var room = world.PersonRoomMotiveEntries[roomId];
        room.LightLevel6c = 0.5f;
        world.PersonRoomMotiveEntries[roomId] = room;
        var notices = new List<(int Message, int Value)>();
        world.AddNoticeListener(new SimulationNoticeListener((message, value) =>
        {
            notices.Add((message, value));
            return false;
        }));
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            RoomWord84 = unchecked((short)roomId),
            LastObservedRoomIdAFC = 0x2222,
        });

        world.UpdatePersonRoomMotive(person);

        Assert.Equal((byte)1, world.PersonRoomMotiveEntries[roomId].LightsEnabledFlag73);
        Assert.Equal([(0xf1, (int)roomId)], notices);
        var lightMap = world.LotRoomLightMaps[1];
        foreach (var (x, y) in map.TilesInRoom(roomId))
            Assert.Equal((byte)1, lightMap[x * RoomLightCoordinateGrid.GridSize + y]);
    }

    [Fact]
    public void RoomLightToggleRespectsManualOverrideAndLitObjectCount()
    {
        var map = EnclosedRoomMap();
        var world = new SimulationWorld();
        world.LoadRoomMaps(new Dictionary<int, RoomMap> { [1] = map });
        var roomId = map.RoomAt(1, 1);
        var room = world.PersonRoomMotiveEntries[roomId];
        room.LightLevel6c = 0.25f;
        room.LightOverrideFlag75 = 1;
        world.PersonRoomMotiveEntries[roomId] = room;
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            RoomWord84 = unchecked((short)roomId), LastObservedRoomIdAFC = 0x2222,
        });

        world.UpdatePersonRoomMotive(person);

        Assert.Equal((byte)0, world.PersonRoomMotiveEntries[roomId].LightsEnabledFlag73);
        Assert.DoesNotContain(world.LotRoomLightMaps[1], value => value != 0);

        room = world.PersonRoomMotiveEntries[roomId];
        room.LightOverrideFlag75 = 0;
        room.LightingObjectCount140 = 1;
        world.PersonRoomMotiveEntries[roomId] = room;
        person.LastObservedRoomIdAFC = 0x2222;

        world.UpdatePersonRoomMotive(person);

        Assert.Equal((byte)1, world.PersonRoomMotiveEntries[roomId].LightsEnabledFlag73);
        Assert.DoesNotContain(world.LotRoomLightMaps[1], value => value != 0);
    }

    [Fact]
    public void RoomChangeNoticeSchedulesAndTickRefreshesDirtyRooms()
    {
        var map = RoomMap.Build(WallsWithWater(4, 4,
            [0, 0, 0, 0, 0, 0, 0, 0, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10]), 1, 4, 4);
        Assert.Equal(2, map.RoomCount);
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            Position = new ObjectPosition(16, 16, 1),
        });
        world.LoadRoomMaps(new Dictionary<int, RoomMap> { [1] = map });
        var firstRoom = unchecked((ushort)person.RoomWord84);
        var secondRoom = map.RoomAt(2, 1);
        Assert.NotEqual(firstRoom, secondRoom);

        world.DispatchNotice(0xf3, firstRoom);
        world.DispatchNotice(0xf3, secondRoom);

        Assert.Equal(1u, world.RoomManagerRefreshFlags);
        Assert.True(world.PersonRoomMotiveEntries[firstRoom].NeedsRefresh);
        Assert.True(world.PersonRoomMotiveEntries[secondRoom].NeedsRefresh);
        world.AdvanceSimulationTick();

        Assert.Equal(0u, world.RoomManagerRefreshFlags);
        Assert.False(world.PersonRoomMotiveEntries[firstRoom].NeedsRefresh);
        Assert.False(world.PersonRoomMotiveEntries[secondRoom].NeedsRefresh);
    }

    [Fact]
    public void OnlyTheTilesOwnSidesBlock()
    {
        // A wall on side 1 of (2, 0) blocks the step from (2, 0) to (1, 0), but (1, 0) has no side 4, so the fill,
        // seeded at (0, 0), still steps from (1, 0) into (2, 0).
        var map = RoomMap.Build(Walls(3, 1, (x, _) => x == 2 ? (TowardsLowerX, Wall, (byte)0) : Empty), 1, 3, 1);
        Assert.Equal(1, map.RoomCount);
    }

    [Fact]
    public void TwoRoomsSplitByAWall()
    {
        // Column 1 has side 4 and column 2 side 1, both walls: a 4 x 2 lot splits in two.
        var map = RoomMap.Build(Walls(4, 2, (x, _) => x switch
        {
            1 => (TowardsHigherX, 0, 0),
            2 => (TowardsLowerX, Wall, 0),
            _ => Empty,
        }), 1, 4, 2);
        Assert.Equal(2, map.RoomCount);
        Assert.Equal(0, map.RoomAt(0, 1));
        Assert.Equal(0, map.RoomAt(1, 0));
        Assert.Equal(1, map.RoomAt(2, 0));
        Assert.Equal(1, map.RoomAt(3, 1));
    }

    [Fact]
    public void FencesDoNotDivideRooms()
    {
        var map = RoomMap.Build(Walls(4, 1, (x, _) => x switch
        {
            1 => (TowardsHigherX, 0, 0),
            2 => (TowardsLowerX, Fence, 0),
            _ => Empty,
        }), 1, 4, 1);
        Assert.Equal(1, map.RoomCount);
    }

    [Fact]
    public void DoorIsAGapInTheWall()
    {
        // Only row 0 is walled between columns 1 and 2: the two halves join through row 1.
        var map = RoomMap.Build(Walls(4, 2, (x, y) => (x, y) switch
        {
            (1, 0) => (TowardsHigherX, 0, 0),
            (2, 0) => (TowardsLowerX, Wall, 0),
            _ => Empty,
        }), 1, 4, 2);
        Assert.Equal(1, map.RoomCount);
    }

    [Fact]
    public void DiagonalWallTileBelongsToNoRoom()
    {
        var map = RoomMap.Build(Walls(3, 1, (x, _) => x == 1 ? (Diagonal, (byte)0, Wall) : Empty), 1, 3, 1);
        Assert.Equal(2, map.RoomCount);
        Assert.Equal(RoomMap.NoRoom, map.RoomAt(1, 0));
        Assert.Equal(0, map.RoomAt(0, 0));
        Assert.Equal(1, map.RoomAt(2, 0));
    }

    [Fact]
    public void DiagonalWallPositionResolvesTheCorrectHalfRoom()
    {
        // Enclosed room at (2..3, 2..3) with a 0x10 diagonal corner at (2, 2):
        // (-X, -Y) triangle joins outdoor (1, 2)/(2, 1)/(1, 1); (+X, +Y) triangle joins indoor (3, 2)/(2, 3)/(3, 3).
        var map = RoomMap.Build(Walls(5, 5, (x, y) =>
        {
            byte segments = 0;
            if (x == 2 && y == 3) segments |= TowardsLowerX;
            if (x == 1 && y == 3) segments |= TowardsHigherX;
            if (x == 3 && y == 2) segments |= TowardsLowerY;
            if (x == 3 && y == 1) segments |= TowardsHigherY;
            if (x is 2 or 3 && y == 3) segments |= TowardsHigherY;
            if (x is 2 or 3 && y == 4) segments |= TowardsLowerY;
            if (x == 3 && y is 2 or 3) segments |= TowardsHigherX;
            if (x == 4 && y is 2 or 3) segments |= TowardsLowerX;
            if (x == 2 && y == 2) segments |= Diagonal;
            return (segments,
                (byte)((segments & TowardsLowerX) != 0 ? Wall : 0),
                (byte)((segments & (TowardsLowerY | Diagonal)) != 0 ? Wall : 0));
        }), 1, 5, 5);

        Assert.Equal(RoomMap.NoRoom, map.RoomAt(2, 2));
        Assert.NotEqual(map.RoomAt(1, 1), map.RoomAt(3, 3));
        Assert.Equal(map.RoomAt(1, 1), map.RoomAtPosition(2 * 16, 2 * 16));
        Assert.Equal(map.RoomAt(1, 1), map.RoomAtPosition(2 * 16 + 4, 2 * 16 + 4));
        Assert.Equal(map.RoomAt(3, 3), map.RoomAtPosition(2 * 16 + 12, 2 * 16 + 12));
        Assert.Equal(map.RoomAt(3, 3), map.RoomAtPosition(2 * 16 + 15, 2 * 16 + 15));
        var adjacent = map.AdjacentRoomsAtTile(2, 2)!.Value;
        Assert.Equal(map.RoomAt(1, 1), adjacent.First);
        Assert.Equal(map.RoomAt(3, 3), adjacent.Second);
        Assert.Equal(map.RoomAt(3, 3), map.RoomAtPosition(2 * 16 + 8, 2 * 16 + 8));
    }

    [Fact]
    public void OtherDiagonalOrientationResolvesItsTwoHalfRooms()
    {
        // Enclosed room at (2..3, 1..2) with a 0x20 diagonal corner at (2, 2) (matching House 05 Floor 2 (23, 31)):
        // fractionX < fractionY joins outdoor (1, 2)/(2, 3)/(1, 3); fractionX >= fractionY joins indoor (3, 2)/(2, 1)/(3, 1).
        var map = RoomMap.Build(Walls(5, 5, (x, y) =>
        {
            byte segments = 0;
            if (x == 2 && y == 1) segments |= TowardsLowerX;
            if (x == 1 && y == 1) segments |= TowardsHigherX;
            if (x == 3 && y == 2) segments |= TowardsHigherY;
            if (x == 3 && y == 3) segments |= TowardsLowerY;
            if (x is 2 or 3 && y == 1) segments |= TowardsLowerY;
            if (x is 2 or 3 && y == 0) segments |= TowardsHigherY;
            if (x == 3 && y is 1 or 2) segments |= TowardsHigherX;
            if (x == 4 && y is 1 or 2) segments |= TowardsLowerX;
            if (x == 2 && y == 2) segments |= 0x20;
            return (segments,
                (byte)((segments & (TowardsLowerX | 0x20)) != 0 ? Wall : 0),
                (byte)((segments & TowardsLowerY) != 0 ? Wall : 0));
        }), 1, 5, 5);

        Assert.Equal(RoomMap.NoRoom, map.RoomAt(2, 2));
        Assert.NotEqual(map.RoomAt(3, 1), map.RoomAt(1, 3));
        Assert.Equal(map.RoomAt(1, 3), map.RoomAtPosition(2 * 16, 2 * 16 + 15));
        Assert.Equal(map.RoomAt(1, 3), map.RoomAtPosition(2 * 16 + 4, 2 * 16 + 12));
        Assert.Equal(map.RoomAt(3, 1), map.RoomAtPosition(2 * 16 + 12, 2 * 16 + 4));
        Assert.Equal(map.RoomAt(3, 1), map.RoomAtPosition(2 * 16 + 15, 2 * 16));
        var adjacent = map.AdjacentRoomsAtTile(2, 2)!.Value;
        Assert.Equal(map.RoomAt(3, 1), adjacent.First);
        Assert.Equal(map.RoomAt(1, 3), adjacent.Second);
        Assert.Equal(map.RoomAt(3, 1), map.RoomAtPosition(2 * 16 + 8, 2 * 16 + 8));
    }

    [Fact]
    public void DiagonalWallPositionProbesXOrthogonalBeforeYOrthogonalWithoutCornerLeak()
    {
        // Sims.exe FUN_00433c6a (00433c6a.c:68-85) checks RoomAt(adjacentX, y) first and falls back to
        // RoomAt(x, adjacentY) only when RoomAt(adjacentX, y) == NoRoom (-5); it never reads RoomAt(adjacentX, adjacentY).
        // Case 1: (1, 2) is also a diagonal wall (NoRoom), so (2, 2)'s (-X, +Y) half falls back to RoomAt(2, 3).
        var chained = RoomMap.Build(Walls(5, 5, (x, y) =>
        {
            if ((x == 1 && y == 2) || (x == 2 && y == 2)) return (0x20, Wall, (byte)0);
            return Empty;
        }), 1, 5, 5);
        Assert.Equal(RoomMap.NoRoom, chained.RoomAt(1, 2));
        Assert.NotEqual(RoomMap.NoRoom, chained.RoomAt(2, 3));
        Assert.Equal(chained.RoomAt(2, 3), chained.RoomAtPosition(2 * 16 + 4, 2 * 16 + 12));

        // Case 2: (1, 1) is walled off into its own corner room while (1, 2) and (2, 1) are partitioned from each other;
        // (2, 2)'s (-X, -Y) half on a 0x10 diagonal must pick RoomAt(1, 2) (not corner RoomAt(1, 1) and not RoomAt(2, 1)).
        var split = RoomMap.Build(Walls(5, 5, (x, y) =>
        {
            byte segments = 0;
            // Enclose (1, 1) completely on its +X and +Y edges and lot borders.
            if (x == 1 && y is 0 or 1) segments |= TowardsHigherX;
            if (x == 2 && y is 0 or 1) segments |= TowardsLowerX;
            if (x is 0 or 1 && y == 1) segments |= TowardsHigherY;
            if (x is 0 or 1 && y == 2) segments |= TowardsLowerY;
            // Also enclose (2, 1) separately from (1, 2).
            if (x == 2 && y is 0 or 1) segments |= TowardsHigherX;
            if (x == 3 && y is 0 or 1) segments |= TowardsLowerX;
            if (x == 2 && y == 2) segments |= Diagonal;
            return (segments,
                (byte)((segments & TowardsLowerX) != 0 ? Wall : 0),
                (byte)((segments & (TowardsLowerY | Diagonal)) != 0 ? Wall : 0));
        }), 1, 5, 5);
        Assert.NotEqual(split.RoomAt(1, 1), split.RoomAt(1, 2));
        Assert.NotEqual(split.RoomAt(2, 1), split.RoomAt(1, 2));
        Assert.Equal(split.RoomAt(1, 2), split.RoomAtPosition(2 * 16 + 4, 2 * 16 + 4));
    }

    [Fact]
    public void DiagonalFenceTileJoinsItsNeighbours()
    {
        var map = RoomMap.Build(Walls(3, 1, (x, _) => x == 1 ? (Diagonal, (byte)0, Fence) : Empty), 1, 3, 1);
        Assert.Equal(1, map.RoomCount);
    }

    [Fact]
    public void SavedLotRoomLoaderBuildsEveryAvailableLevelFromItsWallsLayer()
    {
        var groundLayer = Walls(64, 64, (_, _) => ((byte)0x01, (byte)0x11, (byte)0x21))
            .Find(ResourceType.Parse("Arry"), 2)!.Data.ToArray();
        var upperLayer = Walls(64, 64, (_, _) => ((byte)0x02, (byte)0x22, (byte)0x32))
            .Find(ResourceType.Parse("Arry"), 2)!.Data.ToArray();
        var lot = new IffFile(ChunkBuilder.File(("Arry", 2, groundLayer), ("Arry", 102, upperLayer)), "rooms.iff");

        var legacyRooms = SavedLotObjectLoader.ReadRoomMaps(lot, 0x16);
        Assert.Equal([1, 2], legacyRooms.Keys.Order());
        Assert.Equal(new RoomWallTile(0x02, 0x0f, 0x22, 0x32, 0, 0, 0, 0), legacyRooms[2].WallAt(0, 0));
        Assert.Equal(new RoomRuntimeWallCell(0, 0, 0, 0, 0, 0, 0, 0), legacyRooms[2].RuntimeWallAt(0, 0));
        Assert.Equal((ushort)0x400, legacyRooms[2].RoomAt(0, 0));

        var rooms = SavedLotObjectLoader.ReadRoomMaps(lot, 0x17);

        Assert.Equal([1, 2], rooms.Keys.Order());
        Assert.All(rooms.Values, room => Assert.Equal(1, room.RoomCount));
        Assert.Equal(64, rooms[1].Width);
        Assert.Equal(64, rooms[2].Height);
        Assert.Equal((ushort)0, rooms[1].RoomAt(0, 0));
        Assert.Equal((ushort)0x400, rooms[2].RoomAt(0, 0));
        Assert.Equal(new RoomRuntimeWallCell(0x02, 0x0f, 0x22, 0x32, 0, 0, 0, 0), rooms[2].RuntimeWallAt(0, 0));
    }

    [Fact]
    public void SavedLotEligibilityGridLoadsArryTenOnlyForCurrentSimulationVersions()
    {
        var eligibility = Enumerable.Repeat((byte)0xff, 64 * 64).ToArray();
        eligibility[2 * 64 + 3] = 0;
        var chunk = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(1).U32(0).Raw(eligibility).ToArray();
        var lot = new IffFile(ChunkBuilder.File(("Arry", 10, chunk)), "eligibility.iff");

        var current = SavedLotObjectLoader.ReadPlacementEligibilityGrid(lot, 0x35);
        var legacy = SavedLotObjectLoader.ReadPlacementEligibilityGrid(lot, 0x34);
        var missing = SavedLotObjectLoader.ReadPlacementEligibilityGrid(
            new IffFile(ChunkBuilder.File(), "missing-eligibility.iff"), 0x35);

        Assert.Equal((byte)0, current[2 * 64 + 3]);
        Assert.Equal((byte)0xff, current[3 * 64 + 2]);
        Assert.All(legacy, value => Assert.Equal((byte)0xff, value));
        Assert.All(missing, value => Assert.Equal((byte)0xff, value));
    }

    [Fact]
    public void SavedLotEligibilityGridRejectsMalformedArryTenDimensions()
    {
        var chunk = new ChunkBuilder().S16(0).S16(0).S32(63).S32(64).S32(1).U32(0)
            .Raw(new byte[63 * 64]).ToArray();
        var lot = new IffFile(ChunkBuilder.File(("Arry", 10, chunk)), "eligibility.iff");

        Assert.Throws<InvalidDataException>(() => SavedLotObjectLoader.ReadPlacementEligibilityGrid(lot, 0x35));
    }

    [Fact]
    public void LoadingRoomMapsCreatesRuntimeRecordsForEachRoomKey()
    {
        var oneRoomLayer = Walls(64, 64, (_, _) => Empty).Find(ResourceType.Parse("Arry"), 2)!.Data.ToArray();
        var lot = new IffFile(ChunkBuilder.File(("Arry", 2, oneRoomLayer), ("Arry", 102, oneRoomLayer)), "two-story.iff");
        var world = new SimulationWorld();
        var groundPerson = world.Add(new XPerson(10, new short[XObject.DefinitionCount])
            { Position = new ObjectPosition(20 * 16, 20 * 16, 0) });
        var secondFloorObject = world.Add(new XObject(11, new short[XObject.DefinitionCount])
            { Position = new ObjectPosition(21 * 16, 20 * 16, 2) });
        world.LoadRoomMaps(SavedLotObjectLoader.ReadRoomMaps(lot, 0x17));

        Assert.True(world.RoomManagerRecordsLoaded);
        Assert.Equal([0, 0x400], world.PersonRoomMotiveEntries.Keys.Order().Select(key => (int)key));
        Assert.Equal((short)1, groundPerson.Position.Height);
        Assert.Equal((short)0, groundPerson.RoomWord84);
        Assert.Equal((ushort)0, groundPerson.LastObservedRoomIdAFC);
        Assert.Equal((short)0x400, secondFloorObject.RoomWord84);
        Assert.All(world.PersonRoomMotiveEntries.Values, room =>
        {
            Assert.True(float.IsFinite(room.InteriorValue));
            Assert.True(room.IsOutside); // inferred edge room; FUN_00468851's manager-grid bit is unresolved
            Assert.False(room.NeedsRefresh);
        });
    }

    private static RoomMap EnclosedRoomMap() => RoomMap.Build(Walls(3, 3, (x, y) => (x, y) switch
        {
            (1, 1) => ((byte)(TowardsLowerX | TowardsLowerY | TowardsHigherX | TowardsHigherY), Wall, Wall),
            (0, 1) => (TowardsHigherX, 0, 0),
            (1, 0) => (TowardsHigherY, 0, 0),
            (2, 1) => (TowardsLowerX, Wall, 0),
            (1, 2) => (TowardsLowerY, 0, Wall),
            _ => Empty,
        }), 1, 3, 3);

    [Fact]
    public void UpperStoryRoomKeyFlowsIntoObjectRoomWord()
    {
        var oneRoomLayer = Walls(64, 64, (_, _) => Empty).Find(ResourceType.Parse("Arry"), 2)!.Data.ToArray();
        var lot = new IffFile(ChunkBuilder.File(("Arry", 2, oneRoomLayer), ("Arry", 102, oneRoomLayer)), "two-story.iff");
        var world = new SimulationWorld();
        foreach (var (level, map) in SavedLotObjectLoader.ReadRoomMaps(lot, 0x17)) world.LotRoomMaps.Add(level, map);
        var item = world.Add(new XObject(1, PrimitiveTestWorld.Definition()));

        world.SetObjectPosition(item, new ObjectPosition(20 * 16, 20 * 16, 2));

        Assert.Equal((short)0x400, item.RoomWord84);
        Assert.Equal(item.RoomWord84, item.Data[(int)ObjectData.Room]);
    }
}
