namespace SimsEngine.Formats;

/// <summary>Parses lot object type attributes (TATT), decoding stored attribute arrays for each object
/// GUID.</summary>
public sealed class TypeAttributes
{
    public int Version { get; }
    public IReadOnlyDictionary<uint, short[]> AttributesByGuid { get; }

    public TypeAttributes(int version, IReadOnlyDictionary<uint, short[]> attributes)
    {
        Version = version;
        AttributesByGuid = attributes;
    }

    public static TypeAttributes Read(IffFile file, IffChunk chunk)
    {
        var stream = new PersistReader(file, chunk);
        stream.ReadCodingFlag();
        var count = stream.Count(8);
        var attributes = new Dictionary<uint, short[]>(count);
        for (var index = 0; index < count; index++)
        {
            var guid = (uint)stream.Int32();
            var values = stream.Int16s(stream.Count(2));
            attributes[guid] = values;
        }
        return new TypeAttributes(stream.Version, attributes);
    }

    public static TypeAttributes Read(IffChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var file = NeighborhoodData.WrapSingleChunk(chunk);
        return Read(file, file.Chunks[0]);
    }

    public static TypeAttributes Read(ReadOnlyMemory<byte> chunkData) =>
        Read(new IffChunk(ResourceType.Parse("TATT"), 1, 0, string.Empty, 0, chunkData));

    public byte[] Write(bool coded = true, bool swap = false) =>
        Write(AttributesByGuid, Version, coded, swap);

    public static byte[] Write(TypeAttributes attributes, bool coded = true, bool swap = false)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        return attributes.Write(coded, swap);
    }

    public static byte[] Write(IReadOnlyDictionary<uint, short[]> Entries, int Version = 0x49) =>
        Write(Entries, Version, coded: true, swap: false);

    public static byte[] Write(IReadOnlyDictionary<uint, short[]> Entries, int Version, bool coded, bool swap = false)
    {
        ArgumentNullException.ThrowIfNull(Entries);
        var writer = PersistWriter.Start(swap, Version, "TTAT");
        writer.WriteCodingFlag(coded);
        writer.Int32(Entries.Count);
        foreach (var (guid, values) in Entries)
        {
            writer.UInt32(guid);
            writer.Int32(values.Length);
            writer.Int16s(values);
        }
        return writer.ToArray();
    }
}
