using SimsEngine.Formats;

namespace SimsEngine.World;

/// <summary>The lot terrain grid stored in Arry 0, providing four corner vertex heights per tile.</summary>
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

    public byte ReferenceHeight(int x, int y) => Corners(x, y)[1];

    /// <summary>Determines whether a tile is an interior tile editable by build-mode terrain and grass
    /// tools (indices 1 through 62).</summary>
    public bool EditableTile(int x, int y) =>
        x is >= 1 and <= LotWalls.Size - 2 && y is >= 1 and <= LotWalls.Size - 2;

    public bool IsFlatAt(int x, int y, byte reference)
    {
        foreach (var corner in Corners(x, y)) if (corner != reference) return false;
        return true;
    }

    public byte Height(int x, int y) => Corners(x, y)[1];

    /// <summary>Updates the shared vertex height across the tile and the three adjacent tiles that share
    /// the vertex to maintain corner consistency.</summary>
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

    public byte[] Snapshot() => data.ToArray();

    public void Restore(byte[] snapshot) => data = snapshot.ToArray();

    public IffFile WriteTo(IffFile lot)
    {
        var type = ResourceType.Parse("Arry");
        return lot.WithChunkDataResized(type, 0, array.ToChunkData(data));
    }
}
