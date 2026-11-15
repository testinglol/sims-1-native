namespace SimsEngine.Formats;

/// <summary>The stored attributes of every object type on a lot (TATT), as Sims.exe FUN_0043e1d5 reads them from the
/// resource stream: the field-coding flag, a u32 type count, then per type its GUID, a u32 attribute count and that
/// many s16 attributes.
/// <para>Sims.exe copies the values into the loaded type with that GUID (or into a 128-value scratch buffer when no
/// such type is loaded), clears the attributes the chunk does not cover, and stops reading the chunk when the stored
/// count is larger than the type's own. This reader keeps every stored value.</para></summary>
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

    /// <summary>Writes the TATT resource stream as Sims.exe FUN_0043e1a3 / FUN_0043e1d5 serializes it.</summary>
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
