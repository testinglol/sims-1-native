namespace SimsEngine.Formats;

/// <summary>Represents a serialized execution stack frame, containing the target stack object ID, tree ID,
/// instruction node, arguments, locals, and behavior scope index.</summary>
public sealed record ObjectStackFrame(
    short StackObject,
    ushort Tree,
    short Node,
    short[] Parameters,
    short[] Locals,
    int Value0x08,
    short CodeOwner);

public enum ObjectClass
{
    Object,
    Person,
    MultiTileObject,
    Portal,
}

/// <summary>Contains serialized state for multi-tile object parts, including lead object linkage, placement
/// offsets, and portal routing data.</summary>
public sealed record MultiTileState(
    int Value0x1A0,
    int Value0x1A4,
    int? Value0x1A8,
    int Value0x194,
    int Value0x198,
    int Value0x19C,
    short LeadObject,
    float[]? PortalValues);

/// <summary>Represents a serialized relationship entry between the saved object and a target neighbor or
/// object.</summary>
public sealed record ObjectRelationship(int Value0x0C, int[] Values);

/// <summary>Header portion of a saved object's state written before class-specific fields, allowing common
/// properties to be read without object definitions.</summary>
public sealed record ObjectHead(
    IReadOnlyList<int> Values0x108,
    int Value0x110,
    int? Value0x114,
    short[] Attributes,
    short[] Temps,
    short[] Data)
{
    /// <summary>The object X position in sixteenths of a tile.</summary>
    public int X => Values0x108[4];
    /// <summary>The object Y position in sixteenths of a tile.</summary>
    public int Y => Values0x108[5];
    public int Level => Value0x110 & 0xff;

    internal static ObjectHead Read(ref PersistReader stream, int version)
    {
        if (version < 0x2f) throw stream.Error($"object states are not stored before version 0x2f (version {version})");
        int[] values = [stream.Int32(), stream.Int32(), stream.Int32(), stream.Int32(), stream.Int32(), stream.Int32()];
        var value110 = stream.Int32();
        stream.Int16();
        int? value114 = version == 0x37 ? stream.Int32() : null;
        var attributes = stream.Int16s(ObjectState.Count16(ref stream));
        var temps = stream.Int16s(8);
        var data = stream.Int16s(0x48);
        return new ObjectHead(values, value110, value114, attributes, temps, data);
    }
}

/// <summary>Serialized state for a lot object within an ObjM chunk. Contains world coordinates in
/// sixteenths of a tile, object data variables, temporary registers, call stack frames, relationship
/// tables, routing slots, and manager flags.</summary>
public sealed record ObjectState(
    IReadOnlyList<int> Values0x108,
    int Value0x110,
    int? Value0x114,
    short[] Attributes,
    short[] Temps,
    short[] Data,
    int StackSize,
    IReadOnlyList<ObjectStackFrame> Stack,
    IReadOnlyList<ObjectRelationship?> Relationships,
    short[] Slots,
    short[] Values0x34,
    int? ManagerFlags,
    PersonState? Person,
    MultiTileState? MultiTile)
{
    internal static ObjectState Read(ref PersistReader stream, int version, ObjectClass objectClass, bool placeholder = false)
    {
        var head = ObjectHead.Read(ref stream, version);
        stream.Int16();

        var frameCount = stream.Count(6);
        var stackSize = stream.Int32();
        var stack = new ObjectStackFrame[frameCount];
        for (var index = 0; index < frameCount; index++)
        {
            var stackObject = stream.Int16();
            var tree = (ushort)stream.Int16();
            var node = stream.Int16();
            var localCount = stream.Byte();
            var parameterCount = stream.Byte();
            var parameters = stream.Int16s(parameterCount);
            var locals = stream.Int16s(localCount);
            var value08 = stream.Int32();
            stack[index] = new ObjectStackFrame(stackObject, tree, node, parameters, locals, value08, stream.Int16());
        }

        // A count of -1 indicates a sparse table; non-negative is a legacy dense list.
        var relationshipCount = stream.Int32();
        var sparse = relationshipCount < 0;
        if (sparse) relationshipCount = stream.Count(4);
        else if (relationshipCount > (stream.FieldCoded ? stream.Remaining * 8 + 8 : stream.Remaining / 8)) throw stream.Error($"{relationshipCount} relationships do not fit the chunk");
        var relationships = new ObjectRelationship?[relationshipCount];
        for (var index = 0; index < relationshipCount; index++)
        {
            if (sparse && stream.Int32() == 0) continue;
            var value0C = stream.Int32();
            var relationValues = new int[stream.Count(4)];
            for (var value = 0; value < relationValues.Length; value++) relationValues[value] = stream.Int32();
            relationships[index] = new ObjectRelationship(value0C, relationValues);
        }

        var slots = new short[Count16(ref stream)];
        for (var index = 0; index < slots.Length; index++)
        {
            stream.Int16();
            slots[index] = stream.Int16();
        }
        var values34 = stream.Int16s(Count16(ref stream));

        PersonState? person = null;
        MultiTileState? multiTile = null;
        if (objectClass == ObjectClass.Person) person = PersonState.Read(ref stream, version);
        else if (objectClass != ObjectClass.Object)
        {
            var value1A0 = stream.Int32();
            var value1A4 = stream.Int32();
            int? value1A8 = version > 0x39 ? stream.Int32() : null;
            var value194 = stream.Int32();
            var value198 = stream.Int32();
            var value19C = stream.Int32();
            var lead = stream.Int16();
            float[]? portalValues = null;
            if (objectClass == ObjectClass.Portal)
            {
                portalValues = new float[stream.Count(1)];
                for (var index = 0; index < portalValues.Length; index++) portalValues[index] = stream.Single();
            }
            multiTile = new MultiTileState(value1A0, value1A4, value1A8, value194, value198, value19C, lead, portalValues);
        }
        int? managerFlags = !placeholder && version > 0x35 ? stream.Int32() : null;
        return new ObjectState(head.Values0x108, head.Value0x110, head.Value0x114, head.Attributes, head.Temps, head.Data, stackSize, stack, relationships,
            slots, values34, managerFlags, person, multiTile);
    }

    public static void Write(PersistWriter stream, ObjectState state, int version, ObjectClass objectClass, bool placeholder = false)
    {
        WriteHead(stream, state, version);
        stream.Int16(0);
        stream.Int32(state.Stack.Count);
        stream.Int32(state.StackSize);
        foreach (var frame in state.Stack)
        {
            stream.Int16(frame.StackObject);
            stream.Int16(unchecked((short)frame.Tree));
            stream.Int16(frame.Node);
            stream.Byte((byte)frame.Locals.Length);
            stream.Byte((byte)frame.Parameters.Length);
            stream.Int16s(frame.Parameters);
            stream.Int16s(frame.Locals);
            stream.Int32(frame.Value0x08);
            stream.Int16(frame.CodeOwner);
        }
        stream.Int32(-1);
        stream.Int32(state.Relationships.Count);
        foreach (var relationship in state.Relationships)
        {
            if (relationship is null)
            {
                stream.Int32(0);
                continue;
            }
            stream.Int32(1);
            stream.Int32(relationship.Value0x0C);
            stream.Int32(relationship.Values.Length);
            foreach (var value in relationship.Values) stream.Int32(value);
        }
        stream.Int16((short)state.Slots.Length);
        foreach (var value in state.Slots)
        {
            stream.Int16(0);
            stream.Int16(value);
        }
        stream.Int16((short)state.Values0x34.Length);
        stream.Int16s(state.Values0x34);
        if (objectClass == ObjectClass.Person)
            PersonState.Write(stream, state.Person ?? throw new InvalidDataException("A person state has no person fields."), version);
        else if (objectClass != ObjectClass.Object)
        {
            var multiTile = state.MultiTile ?? throw new InvalidDataException("A multi-tile state has no multi-tile fields.");
            stream.Int32(multiTile.Value0x1A0);
            stream.Int32(multiTile.Value0x1A4);
            if (version > 0x39) stream.Int32(multiTile.Value0x1A8 ?? throw new InvalidDataException("The multi-tile state has no +0x1a8 value to write."));
            stream.Int32(multiTile.Value0x194);
            stream.Int32(multiTile.Value0x198);
            stream.Int32(multiTile.Value0x19C);
            stream.Int16(multiTile.LeadObject);
            if (objectClass == ObjectClass.Portal)
            {
                var values = multiTile.PortalValues ?? throw new InvalidDataException("A portal state has no portal values.");
                stream.Int32(values.Length);
                foreach (var value in values) stream.Single(value);
            }
        }
        if (!placeholder && version > 0x35)
            stream.Int32(state.ManagerFlags ?? throw new InvalidDataException("The object state has no manager flags to write."));
    }

    private static void WriteHead(PersistWriter stream, ObjectState state, int version)
    {
        foreach (var value in state.Values0x108) stream.Int32(value);
        stream.Int32(state.Value0x110);
        stream.Int16(1);
        if (version == 0x37)
            stream.Int32(state.Value0x114 ?? throw new InvalidDataException("The object state has no +0x114 value to write for version 0x37."));
        stream.Int16((short)state.Attributes.Length);
        stream.Int16s(state.Attributes);
        stream.Int16s(state.Temps);
        stream.Int16s(state.Data);
    }

    internal static int Count16(ref PersistReader stream)
    {
        var count = stream.Int16();
        if (count < 0 || count > (stream.FieldCoded ? stream.Remaining * 8 + 8 : stream.Remaining / 2)) throw stream.Error($"{count} values do not fit the chunk");
        return count;
    }
}
