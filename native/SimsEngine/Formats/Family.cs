namespace SimsEngine.Formats;

/// <summary>Represents a family record (FAMI) resource. Layout across versions 1 through 9 includes the
/// house number, family number, household budget, lot net worth, status flags, and member person GUIDs,
/// followed by version 9 trailing attributes.</summary>
public sealed class Family
{
    public int Version { get; }
    public int HouseNumber { get; }
    public string? Name { get; }
    public int FamilyNumber { get; }
    public int Budget { get; }
    public int Value { get; }
    public int Field0x120 { get; }
    public uint Flags { get; }
    public IReadOnlyList<uint> Members { get; }
    public int Field0x13C { get; }
    public IReadOnlyList<short> Field0x14E { get; }

    private Family(int version, int house, string? name, int number, int budget, int value, int field120, uint flags,
        IReadOnlyList<uint> members, int field13C, IReadOnlyList<short> field14E)
    {
        Version = version;
        HouseNumber = house;
        Name = name;
        FamilyNumber = number;
        Budget = budget;
        Value = value;
        Field0x120 = field120;
        Flags = flags;
        Members = members;
        Field0x13C = field13C;
        Field0x14E = field14E;
    }

    public static Family Read(IffFile file, IffChunk chunk)
    {
        var stream = new PersistReader(file, chunk);
        var version = stream.Version;
        var house = stream.Int32();
        var name = version < 5 ? stream.String() : null;
        var number = version > 0 ? stream.Int32() : 0;
        int budget = 0, value = 0, field120 = 0;
        if (version > 1)
        {
            budget = stream.Int32();
            value = stream.Int32();
            field120 = stream.Int32();
        }
        uint flags;
        if (version >= 7) flags = (uint)stream.Int32();
        else
        {
            flags = version < 3 || stream.Bool() ? 1u : 0u;
            if (version > 3 && stream.Bool()) flags |= 2;
            if (version > 5) flags = (flags & ~2u) | (stream.Bool() ? 2u : 0u);
        }
        var members = Array.Empty<uint>();
        if (chunk.Id != 0)
        {
            members = new uint[stream.Count(4)];
            for (var index = 0; index < members.Length; index++) members[index] = (uint)stream.Int32();
        }
        var field13C = version > 7 ? stream.Int32() : 0;
        var field14E = version > 8 ? stream.Int16s(6) : [];
        return new Family(version, house, name, number, budget, value, field120, flags, members, field13C, field14E);
    }

    /// <summary>Serializes the family as a version 9 FAMI chunk. Resource ID 0 acts as a neighborhood
    /// sentinel and omits member count and GUIDs, whereas normal family chunks write the member list.</summary>
    public byte[] Write(ushort chunkId, bool swap = false)
    {
        if (Field0x14E.Count is not 0 and not 6)
            throw new InvalidDataException($"FAMI version 9 requires six 0x14e values, found {Field0x14E.Count}.");
        if (chunkId == 0 && Members.Count != 0)
            throw new InvalidDataException("FAMI chunk 0 is the neighborhood sentinel and cannot contain members.");

        var writer = PersistWriter.Start(swap, 9, "IMAF");
        writer.Int32(HouseNumber);
        writer.Int32(FamilyNumber);
        writer.Int32(Budget);
        writer.Int32(Value);
        writer.Int32(Field0x120);
        writer.UInt32(Flags);
        if (chunkId != 0)
        {
            writer.Int32(Members.Count);
            foreach (var member in Members) writer.UInt32(member);
        }
        writer.Int32(Field0x13C);
        if (Field0x14E.Count == 6) writer.Int16s(Field0x14E.ToArray());
        else writer.Int16s(new short[6]);
        return writer.ToArray();
    }

    public Family WithMembership(int familyNumber, IReadOnlyList<uint> members) => new(9, HouseNumber, null,
        familyNumber, Budget, Value, Field0x120, Flags, members.ToArray(), Field0x13C,
        Field0x14E.Count == 6 ? Field0x14E.ToArray() : new short[6]);

    public Family WithBudget(int budget) => new(9, HouseNumber, null, FamilyNumber, budget, Value, Field0x120, Flags,
        Members.ToArray(), Field0x13C, Field0x14E.Count == 6 ? Field0x14E.ToArray() : new short[6]);

    /// <summary>Returns a copy of the family with the specified person GUID added and flag bit 3 cleared.</summary>
    public Family WithMember(uint guid) => new(9, HouseNumber, null, FamilyNumber, Budget, Value, Field0x120, Flags & ~8u,
        Members.Contains(guid) ? Members.ToArray() : [.. Members, guid], Field0x13C,
        Field0x14E.Count == 6 ? Field0x14E.ToArray() : new short[6]);

    /// <summary>Returns a copy of the family with the specified person GUID removed and flag bit 3 cleared.</summary>
    public Family WithoutMember(uint guid) => new(9, HouseNumber, null, FamilyNumber, Budget, Value, Field0x120, Flags & ~8u,
        Members.Where(member => member != guid).ToArray(), Field0x13C, Field0x14E.Count == 6 ? Field0x14E.ToArray() : new short[6]);

    public Family WithValue(int value) => new(9, HouseNumber, null, FamilyNumber, Budget, value, Field0x120, Flags,
        Members.ToArray(), Field0x13C, Field0x14E.Count == 6 ? Field0x14E.ToArray() : new short[6]);

    public Family WithHouseNumber(int house) => new(9, house, null, FamilyNumber, Budget, Value, Field0x120, Flags,
        Members.ToArray(), Field0x13C, Field0x14E.Count == 6 ? Field0x14E.ToArray() : new short[6]);

    public Family WithFields(int? familyNumber = null, int? value = null, int? field0x120 = null, uint? flags = null, int? field0x13C = null) =>
        new(9, HouseNumber, null, familyNumber ?? FamilyNumber, Budget, value ?? Value, field0x120 ?? Field0x120,
            flags ?? Flags, Members.ToArray(), field0x13C ?? Field0x13C, Field0x14E.Count == 6 ? Field0x14E.ToArray() : new short[6]);

    public static Family CreateNew(int familyNumber, int houseNumber, IReadOnlyList<uint> members, int budget = 20_000, int value = 0, uint flags = 0) =>
        new(9, houseNumber, null, familyNumber, budget, value, 0, flags, members.ToArray(), 0, new short[6]);
}
