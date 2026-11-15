using SimsEngine.Formats;

namespace SimsEngine.World;

/// <summary>One level's wall grid (Arry 2, or 102 upstairs) as the lot stores it: an 8-byte record per tile. Byte 0 holds
/// the segment bits (1 the edge towards the -x neighbour, 2 the edge towards -y, 0x10 and 0x20 the diagonals), bytes 2
/// and 3 the styles of those two edges, bytes 4 and 5 their patterns, byte 7 a diagonal's pattern.</summary>
public sealed class LotWalls
{
    public const int Size = 64;
    public const int RecordSize = 8;
    private readonly byte[] records = new byte[Size * Size * RecordSize];
    private readonly ushort chunkId;
    private readonly LotArray shape;

    private LotWalls(ushort chunkId, LotArray shape)
    {
        this.chunkId = chunkId;
        this.shape = shape;
    }

    public int Level { get; private init; }

    public static LotWalls Read(IffFile lot, int level)
    {
        var id = (ushort)(level == 1 ? 2 : 102);
        var chunk = lot.Find(ResourceType.Parse("Arry"), id) ?? throw new InvalidDataException($"The lot has no wall array for level {level}");
        var array = LotArray.Read(lot, chunk);
        if (array.Rows != Size || array.Width != Size || array.ElementSize != RecordSize)
            throw new InvalidDataException($"Wall array {id} is {array.Rows}x{array.Width} of {array.ElementSize}-byte records");
        var walls = new LotWalls(id, array) { Level = level };
        for (var x = 0; x < Size; x++)
        for (var y = 0; y < Size; y++)
            array.Element(x, y).CopyTo(walls.records.AsSpan((x * Size + y) * RecordSize, RecordSize));
        return walls;
    }

    public Span<byte> this[int x, int y] => records.AsSpan((x * Size + y) * RecordSize, RecordSize);

    public IffFile WriteTo(IffFile lot) =>
        lot.WithChunkDataResized(ResourceType.Parse("Arry"), chunkId, shape.ToChunkData(records));
}
