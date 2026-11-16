using System.Buffers.Binary;

namespace SimsEngine.Formats;

/// <summary>Represents an object saved within an ObjM chunk, containing its instance ID, catalog type
/// index, and serialized state payload.</summary>
public sealed record LotObject(short ObjectId, short TypeIndex, byte[] State, int StateOffset, ObjectState? Decoded = null,
    ObjectHead? Head = null)
{
    public int BlockEnd { get; init; }

    public byte[] StateBytes { get; init; } = [];
}

/// <summary>Represents the saved lot objects (ObjM) resource. In version 0x2f and later, the chunk stores
/// an object ID and type index table terminated by ID 0, followed by serialized object state blocks,
/// trailing manager flags (version 0x36+), and auxiliary object lists.</summary>
public sealed class LotObjects
{
    public int Version { get; }
    public bool FieldCoded { get; }
    public IReadOnlyList<LotObject> Objects { get; }
    public short Object0xA8 { get; }
    public IReadOnlyList<short> ObjectList0x38 { get; }
    public string? Name { get; }
    public int UnreadBytes { get; }

    private LotObjects(int version, bool coded, IReadOnlyList<LotObject> objects, short objectA8,
        IReadOnlyList<short> list38, string? name, int unread)
    {
        UnreadBytes = unread;
        Version = version;
        FieldCoded = coded;
        Objects = objects;
        Object0xA8 = objectA8;
        ObjectList0x38 = list38;
        Name = name;
    }

    /// <summary>Determines the runtime object class for a definition: standalone objects are persons (type
    /// 2) or standard objects, while objects with a master ID are portals (type 8) or multi-tile
    /// components.</summary>
    public static ObjectClass? ClassOf(Objd definition)
    {
        var type = definition[ObjdField.Type];
        if (definition[ObjdField.MasterId] != 0) return type == 8 ? ObjectClass.Portal : ObjectClass.MultiTileObject;
        return type switch { 2 => ObjectClass.Person, 8 or 9 => null, _ => ObjectClass.Object };
    }

    /// <summary>Determines whether a GUID represents a fallback placeholder definition used when an object
    /// or person definition is missing.</summary>
    public static bool IsPlaceholder(uint guid) => guid is 0x3C08965F or 0x7FD96B54 or 0x4A70DF92 or 0x7BEA0977;

    public static LotObjects Read(IffFile file, IffChunk chunk, ObjectTypeTable? types = null,
        Func<uint, Objd?>? definitions = null, bool decodeUnknownAsObject = false)
    {
        var classes = new Dictionary<short, (ObjectClass Class, bool Placeholder)>();
        if (types is not null && definitions is not null)
            foreach (var entry in types.Entries)
                if (definitions(entry.Guid) is { } definition && ClassOf(definition) is { } objectClass)
                    classes[entry.TypeIndex] = (objectClass, IsPlaceholder(entry.Guid));
        var stream = new PersistReader(file, chunk);
        var version = stream.Version;
        var objects = new List<LotObject>();
        if (version > 0x2e)
        {
            stream.ReadCodingFlag();
            var listed = new List<(short Id, short Type)>();
            for (var id = stream.Int16(); id != 0; id = stream.Int16()) listed.Add((id, stream.Int16()));
            stream.BeginBlock();
            foreach (var (id, type) in listed)
            {
                var start = stream.Position;
                var end = stream.BlockEnd;
                ObjectState? decoded = null;
                if (classes.TryGetValue(type, out var objectClass))
                {
                    var state = stream;
                    decoded = ObjectState.Read(ref state, version, objectClass.Class, objectClass.Placeholder);
                    if (state.Position != end) throw state.Error($"object {id} (type index {type}, {objectClass.Class}) state ends at {state.Position}, its block at {end} in ObjM version 0x{version:X} (field-coded: {stream.FieldCoded})");
                }
                else if (decodeUnknownAsObject)
                {
                    var probe = stream;
                    try
                    {
                        var candidate = ObjectState.Read(ref probe, version, ObjectClass.Object, false);
                        if (probe.Position == end) decoded = candidate;
                    }
                    catch (ResourceFormatException) { }
                }
                ObjectHead? head = null;
                try
                {
                    var headStream = stream;
                    head = ObjectHead.Read(ref headStream, version);
                    if (headStream.Position > end) head = null;
                }
                catch (ResourceFormatException) { }
                objects.Add(new LotObject(id, type, stream.BytesTo(end).ToArray(), start, decoded, head)
                {
                    BlockEnd = end,
                    StateBytes = chunk.Data.Span[start..end].ToArray(),
                });
                stream.EndBlock();
                stream.BeginBlock();
            }
        }
        var objectA8 = stream.Int16();
        short[] list38 = [];
        string? name = null;
        if (version > 0x33)
        {
            list38 = stream.Int16s(stream.Count(2));
            name = stream.String();
        }
        return new LotObjects(version, stream.FieldCoded, objects, objectA8, list38, name, stream.Remaining);
    }

    public static byte[] Write(LotObjects source, bool swap, string tag,
        Func<short, (ObjectClass Class, bool Placeholder)> classOfTypeIndex,
        IReadOnlyDictionary<short, ObjectState>? states = null, IReadOnlyList<LotObject>? objects = null)
    {
        var entries = objects ?? source.Objects;
        var writer = PersistWriter.Start(swap, source.Version, tag);
        writer.WriteCodingFlag(source.FieldCoded);
        foreach (var item in entries)
        {
            writer.Int16(item.ObjectId);
            writer.Int16(item.TypeIndex);
        }
        writer.Int16(0);
        writer.BeginBlock();
        foreach (var item in entries)
        {
            var state = states is not null && states.TryGetValue(item.ObjectId, out var replaced) ? replaced : item.Decoded;
            if (state is { } decoded)
            {
                var (objectClass, placeholder) = classOfTypeIndex(item.TypeIndex);
                ObjectState.Write(writer, decoded, source.Version, objectClass, placeholder);
            }
            else
            {
                writer.Raw(item.StateBytes);
            }
            writer.EndBlock();
            writer.BeginBlock();
        }
        writer.Int16(source.Object0xA8);
        if (source.Version > 0x33)
        {
            writer.Int32(source.ObjectList0x38.Count);
            foreach (var value in source.ObjectList0x38) writer.Int16(value);
            writer.String(source.Name ?? string.Empty);
        }
        return writer.ToArray();
    }

    /// <summary>Updates the per-object manager flag dwords directly within an uncompressed ObjM chunk body
    /// without reallocating stream blocks.</summary>
    public static byte[] WriteManagerFlags(IReadOnlyList<LotObject> objects, IffChunk chunk,
        IReadOnlyDictionary<short, uint> flagsByObjectId)
    {
        var body = chunk.Data.ToArray();
        if (body.Length > 12 && body[12] != 0)
            throw new NotSupportedException("A field-coded ObjM stream cannot be patched in place.");
        var swap = body.Length > 0 && body[0] != 0;
        foreach (var item in objects)
        {
            if (item.Decoded?.ManagerFlags is null || !flagsByObjectId.TryGetValue(item.ObjectId, out var flags)) continue;
            var at = item.BlockEnd - 4;
            if (at < 0 || at + 4 > body.Length)
                throw new InvalidDataException($"ObjM object {item.ObjectId}: manager flags at {at} do not fit the {body.Length}-byte body.");
            if (swap) BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(at, 4), flags);
            else BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at, 4), flags);
        }
        return body;
    }
}
