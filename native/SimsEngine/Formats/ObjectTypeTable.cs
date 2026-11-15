namespace SimsEngine.Formats;

/// <summary>One object type a lot was saved with (FUN_0043a5fb).
/// <para><see cref="TreeValue0"/> and <see cref="TreeValue1"/> are what FUN_00457759 and FUN_0045778a return for the
/// type when it is saved (values Sims.exe computes from FUN_004561a0(0) and FUN_004561a0(1)); on load Sims.exe marks a
/// type whose current values differ (flag 2 at +0xb4). <see cref="TypeIndex"/> is the number the lot's objects refer to
/// the type by (kept at +0xa4). <see cref="ObjectType"/> is the OBJD object type (OBJD word 9; 2 is a person), -1
/// before version 2. <see cref="Field0x8C"/> is the value the type keeps at +0x8c of its definition, 0 before
/// version 3. Inferred, not traced: that the tree values are checksums of the type's trees.</para></summary>
public sealed record ObjectTypeEntry(
    uint Guid,
    uint TreeValue0,
    uint TreeValue1,
    short TypeIndex,
    short ObjectType,
    string Name,
    int Field0x8C);

/// <summary>The object type table (objt) of a lot file, as Sims.exe FUN_0043a5fb reads it from the resource stream
/// (FUN_0043de5b loads it, FUN_0043df8d writes version 3): a list of types, each starting with its GUID and ended by a
/// GUID of 0. Each type stores, from version 1, two u32 tree values, then the s16 type index, from version 2 the s16
/// object type, the name and from version 3 a u32.</summary>
public sealed class ObjectTypeTable
{
    private static readonly ResourceType GuidTableType = ResourceType.Parse("Gtab");

    public int Version { get; }
    public IReadOnlyList<ObjectTypeEntry> Entries { get; }

    private ObjectTypeTable(int version, IReadOnlyList<ObjectTypeEntry> entries)
    {
        Version = version;
        Entries = entries;
    }

    /// <summary>FUN_00415784 / FUN_0041589a: reads the optional Gtab (0x47746162, id 1) GUID translation table
    /// written by FUN_00428f4f when a .FAM lot is imported with newly allocated character GUIDs.</summary>
    public static IReadOnlyList<(uint OldGuid, uint NewGuid)> ReadGuidTable(IffFile file)
    {
        if (file.Find(GuidTableType, 1) is not { } chunk) return [];
        var stream = new PersistReader(file, chunk);
        var count = stream.Int32();
        if (count <= 0) return [];
        var entries = new List<(uint OldGuid, uint NewGuid)>(count);
        for (var i = 0; i < count; i++)
        {
            var oldGuid = unchecked((uint)stream.Int32());
            var newGuid = unchecked((uint)stream.Int32());
            entries.Add((oldGuid, newGuid));
        }
        return entries;
    }

    /// <summary>FUN_004157ac / FUN_0041589a: writes the Gtab (0x47746162, id 1) GUID translation table.</summary>
    public static byte[] WriteGuidTable(bool swap, IReadOnlyList<(uint OldGuid, uint NewGuid)> entries)
    {
        var stream = PersistWriter.Start(swap, 0, "batG");
        stream.Int32(entries.Count);
        foreach (var (oldGuid, newGuid) in entries)
        {
            stream.Int32(unchecked((int)oldGuid));
            stream.Int32(unchecked((int)newGuid));
        }
        return stream.ToArray();
    }

    /// <summary>FUN_00415747: walks the Gtab chain (marking visited entries so cycles terminate) to translate
    /// a saved OBJT GUID to its imported character GUID.</summary>
    public static uint TranslateGuid(uint guid, IReadOnlyList<(uint OldGuid, uint NewGuid)> guidTable)
    {
        if (guidTable.Count == 0) return guid;
        Span<bool> used = guidTable.Count <= 64 ? stackalloc bool[guidTable.Count] : new bool[guidTable.Count];
        while (true)
        {
            var matched = false;
            for (var i = 0; i < guidTable.Count; i++)
            {
                if (guidTable[i].OldGuid == guid && !used[i])
                {
                    guid = guidTable[i].NewGuid;
                    used[i] = true;
                    matched = true;
                    break;
                }
            }
            if (!matched) return guid;
        }
    }

    /// <summary>FUN_0043a5fb's versioned GUID-terminated type list.</summary>
    public byte[] Write(bool swap, string tag, IReadOnlyList<ObjectTypeEntry>? entries = null)
    {
        var stream = PersistWriter.Start(swap, Version, tag);
        foreach (var entry in entries ?? Entries)
        {
            stream.Int32(unchecked((int)entry.Guid));
            if (Version > 0)
            {
                stream.Int32(unchecked((int)entry.TreeValue0));
                stream.Int32(unchecked((int)entry.TreeValue1));
            }
            stream.Int16(entry.TypeIndex);
            if (Version >= 2) stream.Int16(entry.ObjectType);
            stream.String(entry.Name);
            if (Version >= 3) stream.Int32(entry.Field0x8C);
        }
        stream.Int32(0);
        return stream.ToArray();
    }

    public static ObjectTypeTable Read(IffFile file, IffChunk chunk)
    {
        var guidTable = ReadGuidTable(file);
        var stream = new PersistReader(file, chunk);
        var version = stream.Version;
        var entries = new List<ObjectTypeEntry>();
        for (var guid = (uint)stream.Int32(); guid != 0; guid = (uint)stream.Int32())
        {
            uint tree0 = 0, tree1 = 0;
            if (version > 0)
            {
                tree0 = (uint)stream.Int32();
                tree1 = (uint)stream.Int32();
            }
            var typeIndex = stream.Int16();
            var objectType = version >= 2 ? stream.Int16() : (short)-1;
            var name = stream.String();
            var field8C = version >= 3 ? stream.Int32() : 0;
            guid = TranslateGuid(guid, guidTable);
            entries.Add(new ObjectTypeEntry(guid, tree0, tree1, typeIndex, objectType, name, field8C));
        }
        return new ObjectTypeTable(version, entries);
    }
}
