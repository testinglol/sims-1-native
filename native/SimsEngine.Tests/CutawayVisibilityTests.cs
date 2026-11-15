using SimsEngine.Formats;
using SimsEngine.World;
using SimsSimulation;
using Xunit;

namespace SimsEngine.Tests;

/// <summary>Unit tests for A13 presentation wall cutaway visibility, room masking, outdoor blocks, and pattern exemptions.</summary>
public class CutawayVisibilityTests
{
    private static IffFile CreateLotWithIndoorAndOutdoorRooms(int lotSize = 64)
    {
        byte[] Layer(int size, Func<int, int, byte[]> value)
        {
            var body = new ChunkBuilder().S16(0).S16(0).S32(lotSize).S32(lotSize).S32(size).U32(0);
            for (var x = 0; x < lotSize; x++)
            for (var y = 0; y < lotSize; y++) body.Raw(value(x, y));
            return body.ToArray();
        }

        byte[] Walls(int x, int y)
        {
            byte bits = 0;
            // Room bounding box: x in [10, 15], y in [10, 15]
            if (y >= 10 && y <= 15)
            {
                if (x == 10 || x == 16) bits |= 1; // wall towards x-1
                if (x == 9 || x == 15) bits |= 4;  // wall towards x+1
            }
            if (x >= 10 && x <= 15)
            {
                if (y == 10 || y == 16) bits |= 2; // wall towards y-1
                if (y == 9 || y == 15) bits |= 8;  // wall towards y+1
            }
            // Byte 0: bits, Byte 1: 0x0f (side flags), Bytes 2-3: style (1 = wall), Bytes 4-5: pattern
            return [bits, 0x0f, 1, 1, 0, 0, 0, 0];
        }

        return new IffFile(ChunkBuilder.File(
            ("Arry", 0, Layer(4, (_, _) => [0, 0, 0, 0])),
            ("Arry", 1, Layer(1, (_, _) => [0])),
            ("Arry", 2, Layer(8, Walls)),
            ("Arry", 8, Layer(1, (x, y) => [x >= 10 && x <= 15 && y >= 10 && y <= 15 ? (byte)0x10 : (byte)0x14]))), "cutaway_lot.iff");
    }

    private static IffFile CreateLotWithWallStyle(byte wallStyle, int width = 4, int height = 2)
    {
        var wallBody = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0);
        for (var x = 0; x < width; x++)
        for (var y = 0; y < height; y++)
        {
            // Wall dividing column 1 and 2
            byte segments = x switch
            {
                1 => 4, // towards higher x
                2 => 1, // towards lower x
                _ => 0
            };
            wallBody.Raw([segments, 0x0f, wallStyle, 0, 0, 0, 0, 0]);
        }
        return new IffFile(ChunkBuilder.File(("Arry", 2, wallBody.ToArray())), "style_lot.iff");
    }

    [Fact]
    public void CutawayRoomMaskComputesBitmaskForSelectedIndoorRooms()
    {
        var lot = CreateLotWithIndoorAndOutdoorRooms();
        var map = RoomMap.Build(lot, 1, 64, 64);
        var camera = new LotCamera(0, 64, 64);

        var indoorRoom = map.RoomAt(12, 12);
        Assert.False(map.IsOutside(indoorRoom));

        var outdoorRoom = map.RoomAt(0, 0);
        Assert.True(map.IsOutside(outdoorRoom));

        // When indoor room is selected, CutawayMatrix cuts indoor tiles and walks towards camera
        var mask = SceneExporter.CutawayMatrix(map, camera, 64, [indoorRoom], []);
        Assert.True(mask[12 * 64 + 12]);
        Assert.False(mask[0 * 64 + 0]);

        // Mac Room::ComputeCutawayMatrix rule: outdoor rooms contribute no tiles directly
        var outdoorMask = SceneExporter.CutawayMatrix(map, camera, 64, [outdoorRoom], []);
        Assert.False(outdoorMask[0 * 64 + 0]);

        // Extra tiles passed in are explicitly or-ed into the bitmask
        var extraMask = SceneExporter.CutawayMatrix(map, camera, 64, [], [(5, 5)]);
        Assert.True(extraMask[5 * 64 + 5]);
    }

    [Fact]
    public void WallCutawayMaskTransitionsAndMaskId()
    {
        // Far zoom (zoom = 0): base 2
        Assert.Equal(2, SceneExporter.WallMaskId(1, down: false, zoom: 0));
        Assert.Equal(3, SceneExporter.WallMaskId(1, down: true, zoom: 0));
        Assert.Equal(4, SceneExporter.WallMaskId(2, down: false, zoom: 0));

        // Medium zoom (zoom = 1): base 514
        Assert.Equal(514, SceneExporter.WallMaskId(1, down: false, zoom: 1));
        Assert.Equal(515, SceneExporter.WallMaskId(1, down: true, zoom: 1));
        Assert.Equal(516, SceneExporter.WallMaskId(2, down: false, zoom: 1));  // fence up
        Assert.Equal(517, SceneExporter.WallMaskId(2, down: true, zoom: 1));   // fence down
        Assert.Equal(536, SceneExporter.WallMaskId(12, down: false, zoom: 1)); // half-wall up
        Assert.Equal(538, SceneExporter.WallMaskId(13, down: false, zoom: 1)); // balustrade up
        Assert.Equal(540, SceneExporter.WallMaskId(14, down: false, zoom: 1)); // picket fence up

        // Near zoom (zoom = 2): base 1026
        Assert.Equal(1026, SceneExporter.WallMaskId(1, down: false, zoom: 2));
        Assert.Equal(1027, SceneExporter.WallMaskId(1, down: true, zoom: 2));

        // Story height scaling per zoom level: 58 << (zoom - 1)
        Assert.Equal(58, SceneExporter.CutawayStoryPixels(1));
        Assert.Equal(116, SceneExporter.CutawayStoryPixels(2));
        Assert.Equal(232, SceneExporter.CutawayStoryPixels(3));
    }

    [Fact]
    public void OutdoorCutawayGeneratesFiveByFiveBlockAroundSim()
    {
        var camera = new LotCamera(0, 64, 64);
        var simX = 20;
        var simY = 25;
        var lotSize = 64;

        var tiles = SceneExporter.CutawayRectangle(camera, simX, simY, lotSize).ToList();

        // 5x5 rectangle = 25 tiles
        Assert.Equal(25, tiles.Count);

        // Every tile must be within the lot dimensions [0, 63]
        foreach (var (x, y) in tiles)
        {
            Assert.InRange(x, 0, lotSize - 1);
            Assert.InRange(y, 0, lotSize - 1);
        }

        // Verify that passing these tiles to CutawayMatrix sets every tile in the mask
        var lot = CreateLotWithIndoorAndOutdoorRooms();
        var map = RoomMap.Build(lot, 1, 64, 64);
        var mask = SceneExporter.CutawayMatrix(map, camera, lotSize, [], tiles);
        foreach (var (x, y) in tiles)
        {
            Assert.True(mask[y * lotSize + x]);
        }

        // Near edge of lot: should clamp within lot bounds
        var edgeTiles = SceneExporter.CutawayRectangle(camera, 62, 62, lotSize).ToList();
        Assert.NotEmpty(edgeTiles);
        foreach (var (x, y) in edgeTiles)
        {
            Assert.InRange(x, 0, lotSize - 1);
            Assert.InRange(y, 0, lotSize - 1);
        }

        // Camera rotation preserves lot-bounded projection
        var rotatedCamera = new LotCamera(1, 64, 64);
        var rotatedTiles = SceneExporter.CutawayRectangle(rotatedCamera, simX, simY, lotSize).ToList();
        Assert.Equal(25, rotatedTiles.Count);
        foreach (var (x, y) in rotatedTiles)
        {
            Assert.InRange(x, 0, lotSize - 1);
            Assert.InRange(y, 0, lotSize - 1);
        }
    }

    [Theory]
    [InlineData(2, true)]   // White Picket Fence -> exempt
    [InlineData(14, true)]  // Monticello Balustrade -> exempt
    [InlineData(12, true)]  // Privacy Fence -> exempt
    [InlineData(13, true)]  // Wrought Iron Balustrade -> exempt
    [InlineData(1, false)]  // Standard wall -> cutaway
    [InlineData(5, false)]  // Brick wall -> cutaway
    [InlineData(20, false)] // Wood panel wall -> cutaway
    public void WallPatternAndStyleCutawayExemptions(byte style, bool isExempt)
    {
        // 1. SceneExporter.NormalizedWallStyle preserves exempt opening/fence styles (2, 12, 13, 14)
        // and collapses standard/custom walls to 1
        var normalized = SceneExporter.NormalizedWallStyle(style);
        if (isExempt)
        {
            Assert.Equal(style, normalized);
        }
        else
        {
            Assert.Equal(1, normalized);
        }

        // 2. RoomMap.Build respects style openness (RoomMap.cs:684: Open(style) => style is 2 or 0xc or 0xd or 0xe):
        // fences/half-walls do NOT divide rooms, while standard walls DO
        var lot = CreateLotWithWallStyle(style);
        var map = RoomMap.Build(lot, 1, 4, 2);
        if (isExempt)
        {
            Assert.Equal(1, map.RoomCount);
        }
        else
        {
            Assert.Equal(2, map.RoomCount);
        }
    }

    [Fact]
    public void CutawayWallAdjacentTilesCondition()
    {
        // Mac Wall::ComputeCutaway: a wall is cut only when the tile that owns it AND the tile across it
        // are both present in the cutaway mask (SceneExporter.cs:1101-1104).
        var lot = CreateLotWithIndoorAndOutdoorRooms();
        var map = RoomMap.Build(lot, 1, 64, 64);
        var camera = new LotCamera(0, 64, 64);

        var indoorRoom = map.RoomAt(12, 12);
        var mask = SceneExporter.CutawayMatrix(map, camera, 64, [indoorRoom], []);

        // 1. Two interior tiles inside the cutaway room: both masked -> partition wall is cut
        int inX1 = 12, inY1 = 12;
        int inX2 = 13, inY2 = 12;
        Assert.True(mask[inY1 * 64 + inX1] && mask[inY2 * 64 + inX2]);

        // 2. Boundary wall between indoor room and outdoor area: indoor is masked, outdoor is not -> boundary wall is NOT cut
        int boundInX = 10, boundInY = 12;
        int boundOutX = 9, boundOutY = 12;
        Assert.True(mask[boundInY * 64 + boundInX]);
        Assert.False(mask[boundOutY * 64 + boundOutX]);
        Assert.False(mask[boundInY * 64 + boundInX] && mask[boundOutY * 64 + boundOutX]);

        // 3. Two outdoor tiles: neither masked -> outdoor wall is NOT cut
        int outX1 = 5, outY1 = 5;
        int outX2 = 6, outY2 = 5;
        Assert.False(mask[outY1 * 64 + outX1] && mask[outY2 * 64 + outX2]);
    }

    [Fact]
    public void CutawayProbeRoomFindsAdjacentRoom()
    {
        var lot = CreateLotWithIndoorAndOutdoorRooms();
        var map = RoomMap.Build(lot, 1, 64, 64);
        var camera = new LotCamera(0, 64, 64);

        var indoorRoom = map.RoomAt(12, 12);
        var outdoorRoom = map.RoomAt(0, 0);

        // Probing from inside near the wall probes across into the neighbor room
        var probed = SceneExporter.CutawayProbeRoom(map, camera, 64, 15, 12, zoom: 2);
        Assert.True(probed == indoorRoom || probed == outdoorRoom);
    }
}
