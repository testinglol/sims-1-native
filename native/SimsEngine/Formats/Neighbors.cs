using System.Buffers.Binary;

namespace SimsEngine.Formats;

/// <summary>One neighbour: a person the neighbourhood remembers, with the relationship values it keeps toward others.
/// <see cref="Slot"/> is its position in the stored list (empty slots are skipped).</summary>
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

/// <summary>The neighbour list (NBRS), as Sims.exe FUN_00430b85 reads it: a u32 slot count, and per slot a u32 that is
/// nonzero when the slot holds a neighbour, followed by the neighbour (FUN_00423e3b):
/// <list type="bullet">
/// <item>a record version: from chunk version 73, format 10 is followed by the person-record version, while format 4
/// is used directly; from 31 through 72 the version is 4; before 31 it is 0;</item>
/// <item>from record version 2 the name string and a u32;</item>
/// <item>from record version 1 a u32 that is nonzero when person data follows: 64 s16s and one discarded s16 before
/// record version 3, 80 s16s before 8, 256 from 8;</item>
/// <item>the neighbour id (s16) and the person's GUID (u32);</item>
/// <item>the relationships (FUN_004651dc): an s32 that, when negative, is followed by a u32 slot count and per slot a
/// u32 presence flag, and otherwise is the entry count. Each entry is a key (u32, from chunk version 1; inferred to be the other
/// neighbour's id), a u32 value count and that many s32 values.</item>
/// </list>
/// Sims.exe writes the slot form (-1). In version-73 files both record formats occur in the same chunk; format 10's
/// nested person-record version and format 4's direct version are observed in the supplied saved neighborhood.</summary>
public sealed class Neighbors
{
    public int Version { get; }
    public int SlotCount { get; }
    /// <summary>True when every serialized slot was decoded. A recovered prefix must never replace the source chunk.</summary>
    public bool IsComplete { get; }
    /// <summary>Slots before this index were fully decoded; later slots are unknown, not empty.</summary>
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

    /// <summary>Reads the known-good prefix of a version-73 roster if a later slot has an unsupported record marker.
    /// Other malformed data remains a hard parse error. The caller must preserve the original chunk on save.</summary>
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
                    var count = (record >= 8 || storedFormat == 4 || (version > 0x48 && stream.Remaining >= 0x200)) ? 0x100 : 0x50;
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

    /// <summary>Writes the slot-form NBRS stream Sims.exe uses when saving a neighborhood.</summary>
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

            // The original writer stores relationship records as a vector with a presence word per slot.
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

/// <summary>The neighborhood record (NGBH), as Sims.exe FUN_00425540 reads it: one s16 before version 20, then 16
/// s16s of neighborhood data. Version 64 adds a vector of SimInventory records; later versions add further flags.
/// The meaning of the neighborhood data values and later flags is not traced.</summary>
public sealed class NeighborhoodData
{
    public int Version { get; }
    public IReadOnlyList<short> Data { get; }
    public bool HasUnparsedExtendedFields => Version >= 0x42;
    // Kept for callers that used the earlier name; this is the post-inventory NGBH extension area.
    public bool HasUnparsedExtendedObjectList => HasUnparsedExtendedFields;

    /// <summary>One 12-byte inventory record (type, GUID, count), the entries the Live panel's inventory strip lists.</summary>
    public readonly record struct InventoryItem(int Type, uint GUID, ushort Count)
    {
        public uint Guid => GUID;
    }

    /// <summary>The neighbors' inventories keyed by neighbor id: the SimInventory pointer vector added by
    /// Neighborhood::DoStream at version 0x40. Each vector slot has a presence flag, then (when present) a SimInventory
    /// record containing its neighbor id and 12-byte token records. Later NGBH fields are left unread.</summary>
    public IReadOnlyDictionary<short, IReadOnlyList<InventoryItem>> Inventories { get; }

    /// <summary>Any version-gated extension bytes following the SimInventory vector in the source NGBH stream.</summary>
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
        // Neighborhood::DoStream (Mac, FUN_100AC350) adds the SimInventory pointer vector only at 0x40.
        // Earlier versions may have other versioned fields after the sixteen neighborhood variables.
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

    /// <summary>Writes the NGBH resource stream as Sims.exe FUN_00425540 / FUN_00425316 serializes it.</summary>
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
            // Sims.exe FUN_00425540 (00425540.c:17-58) version-gated neighborhood flags.
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

    /// <summary>Returns the sixteen one-based neighborhood variable slots consumed by the VM. Sims.exe
    /// FUN_00425540 reads one stored value for NGBH versions before 20, leaving the remaining slots at their
    /// zero-initialized values; versions 20 through 63 store all sixteen.</summary>
    public short[] ToVariables()
    {
        var values = new short[16];
        for (var index = 0; index < Data.Count; index++) values[index] = Data[index];
        return values;
    }
}
