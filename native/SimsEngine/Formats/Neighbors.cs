using System.Buffers.Binary;

namespace SimsEngine.Formats;

public sealed record Neighbor(
    int Slot,
    int RecordVersion,
    string? Name,
    int Field0x1C,
    IReadOnlyList<short>? PersonData,
    short NeighborId,
    uint Guid,
    IReadOnlyDictionary<int, IReadOnlyList<int>> Relationships,
    int StoredFormat = 0,
    short PersonDataTrailingWord = 0);

/// <summary>Represents the neighborhood roster (NBRS) resource. Stores an array of neighbor slots
/// containing versioned person records (allocating 64, 80, or 256 person data words by version) and
/// relationship arrays stored either as dense entry lists or sparse slot vectors.</summary>
public sealed class Neighbors
{
    public int Version { get; }
    public int SlotCount { get; }
    public bool IsComplete { get; }
    public int CompleteSlotPrefix { get; }
    public IReadOnlyList<Neighbor> Entries { get; }

    public Neighbors(int version, int slotCount, IReadOnlyList<Neighbor> entries, bool isComplete = true, int? completeSlotPrefix = null)
    {
        Version = version;
        SlotCount = slotCount;
        IsComplete = isComplete;
        CompleteSlotPrefix = completeSlotPrefix ?? slotCount;
        Entries = entries;
    }

    public static Neighbors Read(IffFile file, IffChunk chunk) => Read(file, chunk, recoverSupportedPrefix: false);

    /// <summary>Parses supported entries from a version-73 roster up to the first unrecognized record
    /// marker, preserving valid prefix data.</summary>
    public static Neighbors ReadRecoveringSupportedPrefix(IffFile file, IffChunk chunk) =>
        Read(file, chunk, recoverSupportedPrefix: true);

    private static Neighbors Read(IffFile file, IffChunk chunk, bool recoverSupportedPrefix)
    {
        var stream = new PersistReader(file, chunk);
        var version = stream.Version;
        var slots = stream.Count(4);
        var entries = new List<Neighbor>();
        for (var slot = 0; slot < slots; slot++)
        {
            if (stream.Int32() == 0) continue;
            var storedFormat = 0;
            var record = 0;
            if (version > 0x48)
            {
                storedFormat = stream.Int32();
                if (storedFormat is not (10 or 4))
                {
                    if (recoverSupportedPrefix)
                        return new Neighbors(version, slots, entries, isComplete: false, completeSlotPrefix: slot);
                    throw stream.Error($"unsupported version-73 neighbour record format {storedFormat}");
                }
                record = storedFormat == 10 ? stream.Int32() : 4;
            }
            else if (version > 0x1e) record = stream.Int32();
            string? name = null;
            var field1C = 0;
            if (record > 1)
            {
                name = stream.String();
                field1C = stream.Int32();
            }
            short[]? personData = null;
            short personDataTrailingWord = 0;
            if (record >= 1 && stream.Int32() != 0)
            {
                if (record < 3)
                {
                    personData = stream.Int16s(0x40);
                    personDataTrailingWord = stream.Int16();
                }
                else
                {
                    var count = (record >= 8 || storedFormat == 4) ? 0x100 : 0x50;
                    personData = stream.Int16s(count);
                }
            }
            var id = stream.Int16();
            var guid = (uint)stream.Int32();
            entries.Add(new Neighbor(slot, record, name, field1C, personData, id, guid,
                ReadRelationships(ref stream, version), storedFormat, personDataTrailingWord));
        }
        return new Neighbors(version, slots, entries);
    }

    public Neighbors WithEntries(IReadOnlyList<Neighbor> entries) => new(Version, SlotCount, entries, IsComplete, CompleteSlotPrefix);

    public byte[] WriteChunkData(bool swap)
    {
        var writer = PersistWriter.Start(swap, Version, "SRBN");
        writer.Int32(SlotCount);
        var entriesBySlot = Entries.ToDictionary(entry => entry.Slot);
        for (var slot = 0; slot < SlotCount; slot++)
        {
            if (!entriesBySlot.TryGetValue(slot, out var entry))
            {
                writer.Int32(0);
                continue;
            }

            writer.Int32(1);
            if (Version > 0x48)
            {
                var format = entry.StoredFormat is 4 or 10 ? entry.StoredFormat : 10;
                writer.Int32(format);
                if (format == 10) writer.Int32(entry.RecordVersion);
            }
            else if (Version > 0x1e) writer.Int32(entry.RecordVersion);

            if (entry.RecordVersion > 1)
            {
                writer.String(entry.Name ?? string.Empty);
                writer.Int32(entry.Field0x1C);
            }
            if (entry.RecordVersion >= 1)
            {
                writer.Int32(entry.PersonData is null ? 0 : 1);
                if (entry.PersonData is { } personData)
                {
                    var count = entry.RecordVersion < 3 ? 0x40 : (entry.StoredFormat == 4 || entry.RecordVersion >= 8) ? 0x100 : 0x50;
                    for (var index = 0; index < count; index++)
                        writer.Int16(index < personData.Count ? personData[index] : (short)0);
                    if (entry.RecordVersion < 3) writer.Int16(entry.PersonDataTrailingWord);
                }
            }
            writer.Int16(entry.NeighborId);
            writer.UInt32(entry.Guid);

            // A negative entry count indicates a sparse slot vector with presence flags.
            writer.Int32(-1);
            writer.Int32(entry.Relationships.Count);
            foreach (var (target, values) in entry.Relationships)
            {
                writer.Int32(1);
                if (Version > 0) writer.Int32(target);
                writer.Int32(values.Count);
                foreach (var value in values) writer.Int32(value);
            }
        }
        return writer.ToArray();
    }

    private static Dictionary<int, IReadOnlyList<int>> ReadRelationships(ref PersistReader stream, int version)
    {
        var relationships = new Dictionary<int, IReadOnlyList<int>>();
        var marker = stream.Int32();
        var slotted = marker < 0;
        var count = slotted ? stream.Count(4) : marker;
        if (!slotted && count > stream.Remaining / 4) throw stream.Error($"{count} relationships do not fit the chunk");
        for (var index = 0; index < count; index++)
        {
            if (slotted && stream.Int32() == 0) continue;
            var other = version > 0 ? stream.Int32() : 0;
            var values = new int[stream.Count(4)];
            for (var value = 0; value < values.Length; value++) values[value] = stream.Int32();
            relationships[other] = values;
        }
        return relationships;
    }
}

/// <summary>Represents the neighborhood data (NGBH) chunk, containing 16 global neighborhood variables and,
/// from version 64 (0x40), a vector of Sim inventory records.</summary>
public sealed class NeighborhoodData
{
    public int Version { get; }
    public IReadOnlyList<short> Data { get; }
    public bool HasUnparsedExtendedFields => Version >= 0x42;
    public bool HasUnparsedExtendedObjectList => HasUnparsedExtendedFields;

    /// <summary>Represents a 12-byte Sim inventory entry consisting of an item type, object GUID, and stack
    /// count.</summary>
    public readonly record struct InventoryItem(int Type, uint GUID, ushort Count)
    {
        public uint Guid => GUID;
    }

    /// <summary>Maps neighbor IDs to their persistent inventories, decoded from the sparse inventory vector
    /// introduced in format version 0x40.</summary>
    public IReadOnlyDictionary<short, IReadOnlyList<InventoryItem>> Inventories { get; }

    public ReadOnlyMemory<byte> TrailingBytes { get; }

    public NeighborhoodData(
        int version,
        IReadOnlyList<short> data,
        IReadOnlyDictionary<short, IReadOnlyList<InventoryItem>>? inventories = null,
        ReadOnlyMemory<byte> trailingBytes = default)
    {
        Version = version;
        Data = data;
        Inventories = inventories ?? new Dictionary<short, IReadOnlyList<InventoryItem>>();
        TrailingBytes = trailingBytes;
    }

    public static NeighborhoodData Read(IffFile file, IffChunk chunk)
    {
        var stream = new PersistReader(file, chunk);
        var data = stream.Int16s(stream.Version < 0x14 ? 1 : 16);
        var inventories = new Dictionary<short, IReadOnlyList<InventoryItem>>();
        if (stream.Version >= 0x40)
        {
            var entries = stream.Count(4);
            for (var entry = 0; entry < entries; entry++)
            {
                var present = stream.Int32();
                if (present == 0) continue;
                var neighbor = stream.Int16();
                var count = stream.Count(10);
                var items = new List<InventoryItem>();
                for (var item = 0; item < count; item++)
                    items.Add(new InventoryItem(stream.Int32(), unchecked((uint)stream.Int32()), unchecked((ushort)stream.Int16())));
                inventories[neighbor] = items;
            }
        }
        var trailing = stream.Remaining > 0
            ? stream.BytesTo(stream.Position + stream.Remaining).ToArray()
            : Array.Empty<byte>();
        return new NeighborhoodData(stream.Version, data, inventories, trailing);
    }

    public static NeighborhoodData Read(IffChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var file = WrapSingleChunk(chunk);
        return Read(file, file.Chunks[0]);
    }

    public static NeighborhoodData Read(ReadOnlyMemory<byte> chunkData) =>
        Read(new IffChunk(ResourceType.Parse("NGBH"), 1, 0, string.Empty, 0, chunkData));

    public NeighborhoodData WithData(IReadOnlyList<short> data) =>
        new(Version, data, Inventories, TrailingBytes);

    public NeighborhoodData WithInventories(IReadOnlyDictionary<short, IReadOnlyList<InventoryItem>> inventories) =>
        new(Version, Data, inventories, TrailingBytes);

    public byte[] Write(bool swap = false) =>
        Write(Data, Inventories, Version, swap, TrailingBytes.Span);

    public byte[] WriteChunkData(bool swap = false) => Write(swap);

    public static byte[] Write(NeighborhoodData data, bool swap = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.Write(swap);
    }

    public static byte[] Write(
        IReadOnlyList<short> variables,
        IReadOnlyDictionary<short, IReadOnlyList<InventoryItem>>? inventories = null,
        int version = 0x49,
        bool swap = false,
        ReadOnlySpan<byte> trailingBytes = default)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var writer = PersistWriter.Start(swap, version, "HBGN");
        var count = version < 0x14 ? 1 : 16;
        for (var index = 0; index < count; index++)
            writer.Int16(index < variables.Count ? variables[index] : (short)0);

        if (version >= 0x40)
        {
            if (inventories is null || inventories.Count == 0)
            {
                writer.Int32(0);
            }
            else
            {
                writer.Int32(inventories.Count);
                foreach (var (neighborId, items) in inventories)
                {
                    writer.Int32(1);
                    writer.Int16(neighborId);
                    writer.Int32(items.Count);
                    foreach (var item in items)
                    {
                        writer.Int32(item.Type);
                        writer.UInt32(item.GUID);
                        writer.Int16(unchecked((short)item.Count));
                    }
                }
            }
        }

        if (!trailingBytes.IsEmpty)
        {
            writer.Raw(trailingBytes);
        }
        else
        {
            if (version >= 0x42)
            {
                writer.Byte(0);
                writer.Byte(1);
            }
            if (version >= 0x44)
            {
                writer.Byte(0);
                writer.Byte(1);
            }
            if (version >= 0x45)
            {
                writer.Byte(1);
            }
            if (version >= 0x46)
            {
                writer.Byte(0);
                writer.Byte(1);
                writer.Byte(0);
                writer.Byte(1);
            }
            if (version >= 0x49)
            {
                writer.Byte(0);
                writer.Byte(1);
            }
        }

        return writer.ToArray();
    }

    internal static IffFile WrapSingleChunk(IffChunk chunk)
    {
        var body = chunk.Data.Span;
        var totalChunkSize = IffFile.ChunkHeaderSize + body.Length;
        var bytes = new byte[IffFile.HeaderSize + totalChunkSize];
        "IFF FILE 2.5:TYPE FOLLOWED BY SIZE\0"u8.CopyTo(bytes);
        var header = bytes.AsSpan(IffFile.HeaderSize, IffFile.ChunkHeaderSize);
        BinaryPrimitives.WriteUInt32BigEndian(header, chunk.Type.Code);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)totalChunkSize);
        BinaryPrimitives.WriteUInt16BigEndian(header[8..], chunk.Id);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], chunk.Flags);
        body.CopyTo(bytes.AsSpan(IffFile.HeaderSize + IffFile.ChunkHeaderSize));
        return new IffFile(bytes, chunk.Type.ToString());
    }

    /// <summary>Returns the sixteen neighborhood variable slots, populating one variable for formats prior
    /// to version 20 and all sixteen for version 20 and later.</summary>
    public short[] ToVariables()
    {
        var values = new short[16];
        for (var index = 0; index < Data.Count; index++) values[index] = Data[index];
        return values;
    }
}
