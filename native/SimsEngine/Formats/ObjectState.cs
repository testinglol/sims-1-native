namespace SimsEngine.Formats;

/// <summary>One frame of a saved object's stack (FUN_00474801, from version 0x1a): the stack object id, the tree id
/// (0xffff for none), the node, the parameters and locals, the u32 Sims.exe keeps at +8 of the frame and the code-owner
/// index. From version 0x2f, `FUN_0043c0f9` writes the type record's +0xa4 index and resolves it back to that record's
/// behavior scope on load; older versions store the type-record key.</summary>
public sealed record ObjectStackFrame(
    short StackObject,
    ushort Tree,
    short Node,
    short[] Parameters,
    short[] Locals,
    int Value0x08,
    short CodeOwner);

/// <summary>The class Sims.exe builds a lot object as (FUN_004409db), which decides what its serializer (virtual slot
/// 0x7c) writes. Names are the Sims.exe class names (RTTI).</summary>
public enum ObjectClass
{
    /// <summary>cXObject (FUN_0043194c, serializer FUN_00434353): every object not listed below.</summary>
    Object,
    /// <summary>cXPerson (FUN_00459493, serializer FUN_0045d099): OBJD type 2 with no master id.</summary>
    Person,
    /// <summary>cXMTObject (FUN_004227e6, serializer FUN_00423256): a part of a multi-tile object (nonzero OBJD master
    /// id), other than a portal.</summary>
    MultiTileObject,
    /// <summary>cXPortal (FUN_004631a6, serializer FUN_004638b6): a multi-tile part of OBJD type 8.</summary>
    Portal,
}

/// <summary>What a multi-tile part writes after the object state (FUN_00423256): u32s kept at +0x1a0 and +0x1a4,
/// from version 0x3a a u32 at +0x1a8, from version 0x28 u32s at +0x194, +0x198 and +0x19c, and the id of the object
/// it keeps at +0x190 (inferred: the group's lead object). A portal adds (FUN_004638b6) a u32-counted list of floats
/// kept at +0x1ac.</summary>
public sealed record MultiTileState(
    int Value0x1A0,
    int Value0x1A4,
    int? Value0x1A8,
    int Value0x194,
    int Value0x198,
    int Value0x19C,
    short LeadObject,
    float[]? PortalValues);

/// <summary>One relationship a saved object keeps (FUN_004651dc and FUN_0046574a): the u32 at +0xc of the entry
/// (inferred: the other object or neighbour) and its u32 values.</summary>
public sealed record ObjectRelationship(int Value0x0C, int[] Values);

/// <summary>The part of a saved object's state that every class writes first, the same way (FUN_0043426c and the start
/// of FUN_00434353, see <see cref="ObjectState"/>), so it can be read without knowing the object's definition.</summary>
public sealed record ObjectHead(
    IReadOnlyList<int> Values0x108,
    int Value0x110,
    int? Value0x114,
    short[] Attributes,
    short[] Temps,
    short[] Data)
{
    /// <summary>The u32 kept at +0xf8 (also at +0xfc as a tile): inferred, the x position in sixteenths of a tile.</summary>
    public int X => Values0x108[4];
    /// <summary>The u32 kept at +0xf4 (also at +0xfd as a tile): inferred, the y position in sixteenths of a tile.</summary>
    public int Y => Values0x108[5];
    /// <summary>The low byte of +0x110, kept at +0xfe: inferred, the level.</summary>
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

/// <summary>The state an object's serializer (virtual slot 0x7c, FUN_00434353 for every object) writes into its ObjM
/// block, for the versions ObjM stores states with (0x2f and later).
/// <para>In stream order (FUN_0043426c first): six u32s kept at +0x108, +0x10c, +0x100, +0x104, +0xf8 and +0xf4 (the
/// last two also stored shifted right by 4 at +0xfc and +0xfd, so inferred: positions in sixteenths of a tile), the
/// u32 at +0x110 (its low byte kept at +0xfe), an s16 always written as 1, at version 0x37 only a u32 kept at +0x114;
/// then the attributes (an s16 count and the values, at +0x2c), 8 temps (+0x3a), 72 object data values (+0x4a), an s16
/// always written as 0; the stack (FUN_00475130: u32 frame count, u32 size Sims.exe allocates the stack with, the
/// frames); the relationships (FUN_004651dc writes a u32 -1 and then FUN_00465631's table: a u32 slot count and per
/// slot a u32 that is nonzero when the slot holds a relationship, followed by that relationship's u32 and u32-counted list
/// of u32s; empty slots are null here); the slots (FUN_00434578: s16 count,
/// then per slot an s16 written as 0 and the s16 kept at +0x10 of the slot's 0x24-byte entry); and an s16-counted list
/// of s16s kept at +0x34.</para>
/// <para>The serializers of the other classes write this first and then their own fields: a person
/// <see cref="Person"/>, a multi-tile part <see cref="MultiTile"/>. ObjM adds, from version 0x36, the u32
/// <see cref="ManagerFlags"/> after the state of every object whose definition is not a placeholder.
/// FUN_0043fe4a reads it into the object manager's parallel flag dword (low word: the wait cell FUN_00431e78
/// counts down; high bits: the eligibility flags FUN_0044582f sets), then applies its version rules.</para></summary>
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
    /// <summary>Reads the state one object of <paramref name="objectClass"/> wrote into its block, and the u32 ObjM
    /// adds unless the object's definition is a <paramref name="placeholder"/>.</summary>
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

        // FUN_004651dc: a count of -1 (what Sims.exe writes) is followed by FUN_00465631's table, whose slots may be
        // empty; a count of zero or more is an older dense list that the reader still accepts.
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

    /// <summary>Writes the state back in the reader's order, including the trailing manager dword the ObjM adds for a
    /// version-0x36+ non-placeholder definition, and the class-specific person or multi-tile fields.</summary>
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
