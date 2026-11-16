using System.Buffers.Binary;
using SimsEngine.Formats;

namespace SimsEngine.World;

/// <summary>Represents the ground-floor water and pond grid stored in Arry 10 as one byte per tile (0xFF
/// for non-water). Water tiles store a 4-bit bitmask encoding connections to cardinal neighbor tiles.</summary>
public sealed class LotWater
{
    public const byte NoWater = 0xff;
    private readonly LotArray? shape;
    private readonly byte[] tiles = Enumerable.Repeat(NoWater, LotWalls.Size * LotWalls.Size).ToArray();

    private LotWater(LotArray? array)
    {
        shape = array;
        if (array is null) return;
        var emptyZero = array.Element(0, 0)[0] == 0x00;
        for (var x = 0; x < LotWalls.Size; x++)
        for (var y = 0; y < LotWalls.Size; y++)
        {
            var value = array.Element(x, y)[0];
            tiles[x * LotWalls.Size + y] = emptyZero && value == 0x00 ? NoWater : value;
        }
    }

    public static LotWater Read(IffFile lot)
    {
        if (lot.Find(ResourceType.Parse("Arry"), 10) is not { } chunk) return new LotWater(null);
        var array = LotArray.Read(lot, chunk);
        return array.Rows == LotWalls.Size && array.Width == LotWalls.Size && array.ElementSize == 1
            ? new LotWater(array)
            : new LotWater(null);
    }

    public int Width => LotWalls.Size;
    public int Height => LotWalls.Size;

    public byte this[int x, int y]
    {
        get => InBounds(x, y) ? tiles[x * LotWalls.Size + y] : NoWater;
        set
        {
            if (InBounds(x, y)) tiles[x * LotWalls.Size + y] = value;
        }
    }

    public bool IsWater(int x, int y, int level = 1) =>
        level == 1 && InBounds(x, y) && tiles[x * LotWalls.Size + y] != NoWater;

    /// <summary>Sets a tile to water and updates cardinal connection masks for the tile and its neighbors.</summary>
    public void SetWater(int x, int y)
    {
        if (!InBounds(x, y) || this[x, y] != NoWater) return;
        this[x, y] = 0;
        if (IsWater(x - 1, y))
        {
            this[x - 1, y] |= 8;
            this[x, y] |= 2;
        }
        if (IsWater(x, y - 1))
        {
            this[x, y - 1] |= 4;
            this[x, y] |= 1;
        }
        if (IsWater(x + 1, y))
        {
            this[x + 1, y] |= 2;
            this[x, y] |= 8;
        }
        if (IsWater(x, y + 1))
        {
            this[x, y + 1] |= 1;
            this[x, y] |= 4;
        }
    }

    /// <summary>Clears water from a tile to 0xFF and updates cardinal connection masks on neighboring
    /// tiles.</summary>
    public void ClearWater(int x, int y)
    {
        if (!IsWater(x, y)) return;
        this[x, y] = NoWater;
        if (IsWater(x - 1, y)) this[x - 1, y] = (byte)(this[x - 1, y] & ~8);
        if (IsWater(x, y - 1)) this[x, y - 1] = (byte)(this[x, y - 1] & ~4);
        if (IsWater(x + 1, y)) this[x + 1, y] = (byte)(this[x + 1, y] & ~2);
        if (IsWater(x, y + 1)) this[x, y + 1] = (byte)(this[x, y + 1] & ~1);
    }

    private static bool InBounds(int x, int y) => x is >= 0 and < LotWalls.Size && y is >= 0 and < LotWalls.Size;

    /// <summary>Writes the Arry 10 water grid, defaulting missing grids to a 64x64 plane initialized to
    /// 0xFF.</summary>
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
        return lot.Find(type, 10) is not null
            ? lot.WithChunkDataResized(type, 10, body)
            : lot.WithAddedChunk(type, 10, body);
    }
}
