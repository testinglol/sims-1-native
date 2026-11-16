using SimsEngine.Formats;

namespace SimsEngine.World;

/// <summary>Stores floor patterns for a lot level. Level 1 reads 16-bit pattern data from Arry 11 when
/// present, falling back to 8-bit Arry 1; level 2 reads Arry 111 or Arry 101.</summary>
public sealed class LotFloors
{
    public const int Size = 64;
    private readonly ushort[] patterns = new ushort[Size * Size];
    private readonly ushort chunkId;
    private readonly LotArray shape;

    private LotFloors(ushort chunkId, LotArray shape)
    {
        this.chunkId = chunkId;
        this.shape = shape;
    }

    public int Level { get; private init; }

    public static LotFloors Read(IffFile lot, int level)
    {
        foreach (var id in level == 1 ? new ushort[] { 11, 1 } : new ushort[] { 111, 101 })
        {
            if (lot.Find(ResourceType.Parse("Arry"), id) is not { } chunk) continue;
            var array = LotArray.Read(lot, chunk);
            if (array.Rows != Size || array.Width != Size || array.ElementSize is not (1 or 2)) continue;
            var floors = new LotFloors(id, array) { Level = level };
            for (var x = 0; x < Size; x++)
            for (var y = 0; y < Size; y++)
            {
                var element = array.Element(x, y);
                floors.patterns[x * Size + y] = array.ElementSize == 2 ? BitConverter.ToUInt16(element) : element[0];
            }
            return floors;
        }
        throw new InvalidDataException($"The lot has no floor array for level {level}");
    }

    public ushort this[int x, int y]
    {
        get => patterns[x * Size + y];
        set => patterns[x * Size + y] = value;
    }

    public IffFile WriteTo(IffFile lot)
    {
        var elementSize = shape.ElementSize == 2 || patterns.Any(pattern => pattern > byte.MaxValue) ? 2 : 1;
        var targetId = elementSize == 2 ? (ushort)(Level == 1 ? 11 : 111) : chunkId;
        var data = new byte[Size * Size * elementSize];
        for (var index = 0; index < patterns.Length; index++)
        {
            if (elementSize == 2) BitConverter.TryWriteBytes(data.AsSpan(index * 2), patterns[index]);
            else data[index] = (byte)patterns[index];
        }
        var type = ResourceType.Parse("Arry");
        var body = shape.ToChunkData(data, elementSize);
        return lot.Find(type, targetId) is not null
            ? lot.WithChunkDataResized(type, targetId, body)
            : lot.WithAddedChunk(type, targetId, body);
    }
}
