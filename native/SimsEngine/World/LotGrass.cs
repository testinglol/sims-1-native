using System.Buffers.Binary;
using SimsEngine.Formats;

namespace SimsEngine.World;

/// <summary>The lot's grass layer (Arry 6, one signed byte per tile). TerrainComponent subtracts it from 127, so a tile's
/// grass state is 127 minus the stored change (0 is greenest, 255 is bare); a lot without the array is all 127.</summary>
public sealed class LotGrass
{
    private readonly LotArray? shape;
    private readonly byte[] raw = new byte[LotWalls.Size * LotWalls.Size];

    private LotGrass(LotArray? array)
    {
        shape = array;
        if (array is null) return;
        for (var x = 0; x < LotWalls.Size; x++)
        for (var y = 0; y < LotWalls.Size; y++)
            raw[x * LotWalls.Size + y] = array.Element(x, y)[0];
    }

    public static LotGrass Read(IffFile lot)
    {
        if (lot.Find(ResourceType.Parse("Arry"), 6) is not { } chunk) return new LotGrass(null);
        var array = LotArray.Read(lot, chunk);
        return array.Rows == LotWalls.Size && array.Width == LotWalls.Size && array.ElementSize >= 1 && !array.OldLayout ? new LotGrass(array) : new LotGrass(null);
    }

    public int this[int x, int y]
    {
        get => 127 - unchecked((sbyte)raw[x * LotWalls.Size + y]);
        set => raw[x * LotWalls.Size + y] = unchecked((byte)(sbyte)(127 - Math.Clamp(value, 0, 255)));
    }

    public IffFile WriteTo(IffFile lot)
    {
        var elementSize = shape?.ElementSize ?? 1;
        var data = new byte[raw.Length * elementSize];
        for (var index = 0; index < raw.Length; index++) data[index * elementSize] = raw[index];
        if (shape is not null && elementSize > 1)
            for (var index = 0; index < raw.Length; index++)
                shape.Element(index / LotWalls.Size, index % LotWalls.Size)[1..].CopyTo(data.AsSpan(index * elementSize + 1));
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
        return lot.Find(type, 6) is not null ? lot.WithChunkDataResized(type, 6, body) : lot.WithAddedChunk(type, 6, body);
    }
}
