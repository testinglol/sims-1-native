using System.Buffers.Binary;
using SimsEngine.Formats;

namespace SimsEngine.World;

/// <summary>The lot's pond/water grid (Arry 10, one byte per tile, 0xff for none). FUN_0047870b loads it into the
/// object at lot manager +0x64 when the lot version is above 0x34; FUN_004790bc calls a ground-floor tile a water
/// tile when its byte is not 0xff and treats every other level as having none. FUN_0047740f and FUN_0047759d maintain
/// the 4-bit cardinal neighbour bitmask (0..15) on each water tile.</summary>
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

    /// <summary>FUN_004790bc: ground-floor water check.</summary>
    public bool IsWater(int x, int y, int level = 1) =>
        level == 1 && InBounds(x, y) && tiles[x * LotWalls.Size + y] != NoWater;

    /// <summary>FUN_0047740f: sets a tile to water (0) and updates the 4-bit cardinal neighbour masks.</summary>
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

    /// <summary>FUN_0047759d: clears water from a tile (0xff) and clears its cardinal neighbour mask bits.</summary>
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

    /// <summary>Writes the Arry 10 water plane. Missing planes use the original 64x64 0xff no-water default.</summary>
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
