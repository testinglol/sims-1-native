using SimsEngine.Formats;

namespace SimsEngine.World;

/// <summary>The lot's terrain grid (Arry 0): four corner heights per tile, as FUN_004796d9 returns them.</summary>
public sealed class LotTerrain
{
    private readonly LotArray array;
    private byte[] data;

    private LotTerrain(LotArray array)
    {
        this.array = array;
        data = array.Data.ToArray();
    }

    public static LotTerrain Read(IffFile lot)
    {
        var chunk = lot.Find(ResourceType.Parse("Arry"), 0) ?? throw new InvalidDataException("The lot has no terrain array");
        var array = LotArray.Read(lot, chunk);
        if (array.Rows != LotWalls.Size || array.Width != LotWalls.Size || array.ElementSize != 4)
            throw new InvalidDataException($"Terrain array is {array.Rows}x{array.Width} of {array.ElementSize}-byte tiles");
        return new LotTerrain(array);
    }

    public ReadOnlySpan<byte> Corners(int x, int y) => data.AsSpan((x * array.Width + y) * 4, 4);

    /// <summary>The corner byte FUN_00496868 stores as the drag's reference height (tool +0xe02f).</summary>
    public byte ReferenceHeight(int x, int y) => Corners(x, y)[1];

    /// <summary>FUN_004927ac: interior lot tiles editable by build-mode terrain/grass tools (1..62 inclusive).</summary>
    public bool EditableTile(int x, int y) =>
        x is >= 1 and <= LotWalls.Size - 2 && y is >= 1 and <= LotWalls.Size - 2;

    /// <summary>FUN_004950b8: all four corners equal the reference height.</summary>
    public bool IsFlatAt(int x, int y, byte reference)
    {
        foreach (var corner in Corners(x, y)) if (corner != reference) return false;
        return true;
    }

    /// <summary>The vertex height at (x, y): byte 1 of its element (FUN_004796d9's second byte).</summary>
    public byte Height(int x, int y) => Corners(x, y)[1];

    /// <summary>FUN_004909e0 / FUN_004794f7: stores the vertex height in its own element and in the matching corner of the
    /// three tiles that share it, so every tile's four corners keep agreeing.</summary>
    public void SetHeight(int x, int y, byte height)
    {
        void Put(int tileX, int tileY, int corner)
        {
            if ((uint)tileX < (uint)array.Rows && (uint)tileY < (uint)array.Width) data[(tileX * array.Width + tileY) * 4 + corner] = height;
        }
        Put(x, y, 1);
        Put(x - 1, y, 2);
        Put(x, y - 1, 0);
        Put(x - 1, y - 1, 3);
    }

    /// <summary>The heights, for a transaction that restores them when an edit is refused.</summary>
    public byte[] Snapshot() => data.ToArray();

    public void Restore(byte[] snapshot) => data = snapshot.ToArray();

    public IffFile WriteTo(IffFile lot)
    {
        var type = ResourceType.Parse("Arry");
        return lot.WithChunkDataResized(type, 0, array.ToChunkData(data));
    }
}
