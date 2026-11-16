namespace SimsEngine.Formats;

/// <summary>Represents an object type entry in a lot's type table, recording its GUID, tree checksums,
/// lot-scoped type index, and object classification.</summary>
public sealed record ObjectTypeEntry(
    uint Guid,
    uint TreeValue0,
    uint TreeValue1,
    short TypeIndex,
    short ObjectType,
    string Name,
    int Field0x8C);

/// <summary>Parses and serializes the lot object type table (objt), a null-GUID-terminated list mapping
/// GUIDs to type indices and tree checksums across chunk versions.</summary>
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

    /// <summary>Reads the optional Gtab GUID translation table used to map saved object GUIDs to newly
    /// allocated character GUIDs during family import.</summary>
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

    /// <summary>Resolves a saved GUID through the translation table chain, cycle-checking visited entries
    /// until reaching the final imported GUID.</summary>
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
