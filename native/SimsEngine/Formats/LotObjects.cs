using System.Buffers.Binary;

namespace SimsEngine.Formats;

/// <summary>One object saved in an ObjM chunk: its object id, the objt type index it refers to and its state, the
/// block that the object's own serializer (virtual slot 0x7c) wrote, and that block decoded when the object's class is
/// known (<see cref="ObjectState"/>). <see cref="Head"/>, the part every class writes the same way, is always read.</summary>
public sealed record LotObject(short ObjectId, short TypeIndex, byte[] State, int StateOffset, ObjectState? Decoded = null,
    ObjectHead? Head = null)
{
    /// <summary>The object's block end in the ObjM chunk body, after the block's stored offset header and its state.</summary>
    public int BlockEnd { get; init; }

    /// <summary>The state bytes as stored, from the block's state start to its end (the manager dword included).</summary>
    public byte[] StateBytes { get; init; } = [];
}

/// <summary>The objects of a lot (ObjM), as Sims.exe FUN_0043fe4a reads them from the resource stream.
/// <para>From version 0x2f: the field-coding flag; the object list, each an s16 object id followed by its s16 objt type
/// index (FUN_0043bfaf), ended by an id of 0; then one block per object (FUN_00464c0a / FUN_00464c6e) in list order,
/// holding the state the object's serializer writes (and, from version 0x36, for an object whose definition is not a
/// placeholder (<see cref="IsPlaceholder"/>), a u32 Sims.exe keeps per object id). Before version 0x2f none of this is stored. Then for
/// every version an s16 object id that Sims.exe keeps at +0xa8 (FUN_0043fd68, 0 for none), and from version 0x34 a u32
/// count of s16 object ids kept at +0x38 and a string.</para>
/// <para>This reader keeps each object's state as the raw block. Given the lot's object type table (objt) and a way to
/// find object definitions by GUID, it also decodes the state of every object whose definition is found, with the
/// class Sims.exe would build the object as (<see cref="ClassOf"/>); a decoded block must end exactly at its stored
/// end.
/// Inferred, not traced: what the +0xa8 object and the +0x38 list are used for.</para></summary>
public sealed class LotObjects
{
    public int Version { get; }
    public bool FieldCoded { get; }
    public IReadOnlyList<LotObject> Objects { get; }
    public short Object0xA8 { get; }
    public IReadOnlyList<short> ObjectList0x38 { get; }
    public string? Name { get; }
    /// <summary>Bytes left in the chunk after the last field.</summary>
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

    /// <summary>The class Sims.exe builds an object of <paramref name="definition"/> as (FUN_004409db): with no master
    /// id a person for OBJD type 2, nothing for types 8 and 9 and a cXObject otherwise; with a master id a portal for
    /// type 8 and a multi-tile object otherwise.</summary>
    public static ObjectClass? ClassOf(Objd definition)
    {
        var type = definition[ObjdField.Type];
        if (definition[ObjdField.MasterId] != 0) return type == 8 ? ObjectClass.Portal : ObjectClass.MultiTileObject;
        return type switch { 2 => ObjectClass.Person, 8 or 9 => null, _ => ObjectClass.Object };
    }

    /// <summary>The GUIDs of the definitions Sims.exe FUN_0043fe4a gives an object whose own definition is missing
    /// (FUN_0043e07d for any object, FUN_0043e088, FUN_0043e093 and FUN_0043e09e for a person, chosen by the person's
    /// type). An object of one of these writes no u32 after its state. Sims.exe uses the first one in place of the last
    /// two when bit 0x20 of a game setting (+0x66 of DAT_006e486c) is clear; inferred: the bit is set with the expansion
    /// packs that store such persons, so all four count here.</summary>
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
                    // Types 8 and 9 get no cXObject in Sims.exe, but their saved block still follows the plain object
                    // serializer in the installed lots. Probe a copy so a failure leaves the stream untouched.
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

    /// <summary>Re-encodes the whole ObjM body in the reader's order: the id/type list, a block per object (a decoded
    /// state through <see cref="ObjectState.Write"/>, otherwise its stored bytes), the trailing block offset, the +0xa8
    /// object id and the version-0x34+ list and name. The caller supplies each type index's class and placeholder flag.
    /// </summary>
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

    /// <summary>Rewrites the per-object manager flag dword (the trailing u32 of each decoded block, written for
    /// version 0x36+ non-placeholder definitions) in an ObjM chunk body of the same size. The stream's first byte
    /// selects big-endian storage, as <see cref="PersistReader"/> does. Only an uncompressed (coding-flag 0) stream can
    /// be patched this way; a field-coded stream bit-packs the dword, so a new value can change the block size and
    /// needs the full writer that is not implemented yet. Objects without the dword keep their bytes.</summary>
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
