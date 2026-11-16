using System.Buffers.Binary;
using SimsEngine.Formats;

namespace SimsEngine.World;

/// <summary>The lot pool grid stored in Arry 9 as one byte per tile, where 0xFF indicates no pool.
/// Ground-floor tiles with values below 0x10 represent pool water, while other levels cannot contain pools.
/// </summary>
public sealed class LotPool
{
    private const byte NoPool = 0xff;
    // Neighbour offsets ordered by cardinal directions (0..3) followed by diagonals (4..7).
    private static readonly (int X, int Y)[] DeckNeighbours =
        [(-1, 0), (0, -1), (1, 0), (0, 1), (-1, -1), (1, -1), (1, 1), (-1, 1)];
    private static readonly byte[] NeighbourEdgeFlags = [2, 0, 3, 1];
    private static readonly byte[] PoolEdgeFlags = [8, 4, 2, 1];
    private readonly LotArray? shape;
    private readonly byte[] tiles = Enumerable.Repeat(NoPool, LotWalls.Size * LotWalls.Size).ToArray();

    private LotPool(LotArray? array)
    {
        shape = array;
        if (array is null) return;
        for (var x = 0; x < LotWalls.Size; x++)
        for (var y = 0; y < LotWalls.Size; y++)
            tiles[x * LotWalls.Size + y] = array.Element(x, y)[0];
    }

    public static LotPool Read(IffFile lot)
    {
        if (lot.Find(ResourceType.Parse("Arry"), 9) is not { } chunk) return new LotPool(null);
        var array = LotArray.Read(lot, chunk);
        return array.Rows == LotWalls.Size && array.Width == LotWalls.Size && array.ElementSize == 1 ? new LotPool(array) : new LotPool(null);
    }

    public bool IsPool(int x, int y, int level) =>
        level == 1 && x is >= 0 and < LotWalls.Size && y is >= 0 and < LotWalls.Size && tiles[x * LotWalls.Size + y] < 0x10;

    public bool Occupies(int x, int y, int level) =>
        level == 1 && x is >= 0 and < LotWalls.Size && y is >= 0 and < LotWalls.Size && tiles[x * LotWalls.Size + y] != NoPool;

    /// <summary>Checks whether a ground-floor tile is marked as pool decking (Arry 9 value 0x10).</summary>
    public bool IsDeck(int x, int y, int level) =>
        level == 1 && InBounds(x, y) && tiles[x * LotWalls.Size + y] == 0x10;

    /// <summary>Clears a pool deck tile back to 0xFF when manual floor editing replaces the automatic
    /// decking.</summary>
    public void ClearDeck(int x, int y)
    {
        if (InBounds(x, y) && tiles[x * LotWalls.Size + y] == 0x10)
            tiles[x * LotWalls.Size + y] = NoPool;
    }

    public void AddPool(int x, int y)
    {
        var cell = x * LotWalls.Size + y;
        if (tiles[cell] >= 0x10) tiles[cell] = 0;
        for (var direction = 0; direction < 4; direction++)
        {
            var (dx, dy) = DeckNeighbours[direction];
            var nx = x + dx;
            var ny = y + dy;
            if (!InBounds(nx, ny)) continue;
            var neighbour = nx * LotWalls.Size + ny;
            if (tiles[neighbour] >= 0x10) continue;
            tiles[neighbour] |= NeighbourEdgeFlags[direction];
            tiles[cell] |= PoolEdgeFlags[(direction + 2) & 3];
        }
    }

    public void RemovePool(int x, int y)
    {
        var cell = x * LotWalls.Size + y;
        if (tiles[cell] >= 0x10) return;
        tiles[cell] = NoPool;
        for (var direction = 0; direction < 4; direction++)
        {
            var (dx, dy) = DeckNeighbours[direction];
            var nx = x + dx;
            var ny = y + dy;
            if (!InBounds(nx, ny)) continue;
            var neighbour = nx * LotWalls.Size + ny;
            if (tiles[neighbour] < 0x10)
                tiles[neighbour] = (byte)(tiles[neighbour] & ~PoolEdgeFlags[direction]);
        }
    }

    /// <summary>Rebuilds pool decking by clearing existing deck entries and marking valid non-pool neighbor
    /// tiles as decking (value 0x10).</summary>
    public void RebuildDeck(LotTiles lotTiles, LotTerrain terrain, LotWalls walls,
        Func<int, int, bool>? hasBlockingObject = null)
    {
        for (var i = 0; i < tiles.Length; i++)
            if (tiles[i] == 0x10) tiles[i] = NoPool;
        for (var x = 0; x < LotWalls.Size; x++)
        for (var y = 0; y < LotWalls.Size; y++)
        {
            if (!IsPool(x, y, 1)) continue;
            var supported = new bool[8];
            for (var direction = 0; direction < DeckNeighbours.Length; direction++)
            {
                var (dx, dy) = DeckNeighbours[direction];
                var nx = x + dx;
                var ny = y + dy;
                if (!InBounds(nx, ny)) continue;
                var neighbourIndex = nx * LotWalls.Size + ny;
                if (tiles[neighbourIndex] <= 0x10)
                {
                    supported[direction] = true;
                    continue;
                }
                if (CanSupportDeck(nx, ny, direction, supported, lotTiles, terrain, walls, hasBlockingObject))
                {
                    supported[direction] = true;
                    tiles[neighbourIndex] = 0x10;
                }
            }
        }
    }

    private static bool CanSupportDeck(int x, int y, int direction, bool[] supported, LotTiles lotTiles,
        LotTerrain terrain, LotWalls walls, Func<int, int, bool>? hasBlockingObject)
    {
        if (!lotTiles.InsideLot(x, y) || !lotTiles.Buildable(x, y, 1) || hasBlockingObject?.Invoke(x, y) == true)
            return false;
        var corners = terrain.Corners(x, y);
        if (corners[0] != corners[1] || corners[0] != corners[2] || corners[0] != corners[3]) return false;

        var wall = walls[x, y][0];
        if ((wall & 0x30) != 0) return false;
        var segment = direction switch
        {
            0 => 0x02, 1 => 0x08, 2 => 0x01, 3 => 0x04,
            4 or 5 => 0x20, 6 or 7 => 0x10,
            _ => 0
        };
        var opposite = segment switch { 0x10 => 0x20, 0x20 => 0x10, _ => 0 };
        if (opposite == 0x10)
        {
            return direction == 7
                ? supported[2] && supported[1]
                : supported[0] && supported[3];
        }
        if (opposite == 0x20)
        {
            return direction == 4
                ? supported[2] && supported[0]
                : supported[1] && supported[3];
        }
        return (wall & segment) == 0;
    }

    private static bool InBounds(int x, int y) => x is >= 0 and < LotWalls.Size && y is >= 0 and < LotWalls.Size;

    /// <summary>Writes the Arry 9 pool and deck grid, using a 64x64 unpooled grid if no data was loaded.</summary>
    public IffFile WriteTo(IffFile lot)
    {
        const int elementSize = 1;
        var data = new byte[tiles.Length];
        for (var x = 0; x < LotWalls.Size; x++)
        for (var y = 0; y < LotWalls.Size; y++)
            data[x * LotWalls.Size + y] = tiles[x * LotWalls.Size + y];
        byte[] body;
        if (shape is { OldLayout: false }) body = shape.ToChunkData(data, elementSize);
        else
        {
            body = new byte[20 + data.Length];
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), LotWalls.Size);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), LotWalls.Size);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(12), elementSize);
            data.CopyTo(body, 20);
        }
        var type = ResourceType.Parse("Arry");
        return lot.Find(type, 9) is not null
            ? lot.WithChunkDataResized(type, 9, body)
            : lot.WithAddedChunk(type, 9, body);
    }
}
