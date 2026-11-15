namespace SimsEngine.Formats;

/// <summary>A family record (FAMI), as Sims.exe FUN_00410561 reads it from the resource stream (the save path,
/// FUN_00410729, writes version 9 with the same function).
/// <para>Fields in stream order: the house number; before version 5 a name string; from version 1 the family number;
/// from version 2 three u32s (the budget, the value Sims.exe keeps beside it, and a third that is 0 in every game
/// file); before version 7 one to three flag bytes (from version 3 bit 0, from version 4 and again from version 6 bit
/// 1, before version 3 bit 0 is set), from version 7 a u32 flag word; the member list (a u32 count and that many
/// person GUIDs) for every record except chunk 0; from version 8 one more u32; from version 9 six s16s.</para>
/// <para>Inferred, not traced in Sims.exe: that the second u32 is the budget (new families hold 20000) and the third
/// the household's value (it tracks lot and furnishings). The meaning of the flag bits and of the version 8 and 9
/// fields is not known. Chunk 0 in the game's neighbourhood files does store an empty member list; Sims.exe leaves
/// those four bytes unread.</para></summary>
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

    /// <summary>FUN_00410729 writes every family as FAMI version 9. Chunk 0 is the neighborhood sentinel and omits
    /// the member count entirely; all other chunks write a count followed by person GUIDs. The tag bytes are stored
    /// in reverse order, as in Sims.exe's resource stream.</summary>
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

    /// <summary>Copies a source family into the version-9 layout used when Sims.exe writes a new family record.</summary>
    public Family WithMembership(int familyNumber, IReadOnlyList<uint> members) => new(9, HouseNumber, null,
        familyNumber, Budget, Value, Field0x120, Flags, members.ToArray(), Field0x13C,
        Field0x14E.Count == 6 ? Field0x14E.ToArray() : new short[6]);

    /// <summary>Copies a family with the updated budget written by FUN_00410729.</summary>
    public Family WithBudget(int budget) => new(9, HouseNumber, null, FamilyNumber, budget, Value, Field0x120, Flags,
        Members.ToArray(), Field0x13C, Field0x14E.Count == 6 ? Field0x14E.ToArray() : new short[6]);

    /// <summary>Copies a family as Mac Neighborhood::AddToFamily leaves it: with <paramref name="guid"/> appended
    /// (Family::AddMember skips a listed GUID) and flag bit 3 (+0x138) clear.</summary>
    public Family WithMember(uint guid) => new(9, HouseNumber, null, FamilyNumber, Budget, Value, Field0x120, Flags & ~8u,
        Members.Contains(guid) ? Members.ToArray() : [.. Members, guid], Field0x13C,
        Field0x14E.Count == 6 ? Field0x14E.ToArray() : new short[6]);

    /// <summary>Copies a family as Mac Neighborhood::RemoveFromFamily leaves it: without <paramref name="guid"/>
    /// (Family::RemoveMember) and with flag bit 3 (+0x138) clear.</summary>
    public Family WithoutMember(uint guid) => new(9, HouseNumber, null, FamilyNumber, Budget, Value, Field0x120, Flags & ~8u,
        Members.Where(member => member != guid).ToArray(), Field0x13C, Field0x14E.Count == 6 ? Field0x14E.ToArray() : new short[6]);

    /// <summary>Copies a family with the value in architecture Neighborhood::Save stores for the active household.</summary>
    public Family WithValue(int value) => new(9, HouseNumber, null, FamilyNumber, Budget, value, Field0x120, Flags,
        Members.ToArray(), Field0x13C, Field0x14E.Count == 6 ? Field0x14E.ToArray() : new short[6]);

    /// <summary>Copies a family with the updated house number written by Neighborhood::SetFamilyForHouse.</summary>
    public Family WithHouseNumber(int house) => new(9, house, null, FamilyNumber, Budget, Value, Field0x120, Flags,
        Members.ToArray(), Field0x13C, Field0x14E.Count == 6 ? Field0x14E.ToArray() : new short[6]);

    /// <summary>Copies a family with updated fields for version-9 persistence.</summary>
    public Family WithFields(int? familyNumber = null, int? value = null, int? field0x120 = null, uint? flags = null, int? field0x13C = null) =>
        new(9, HouseNumber, null, familyNumber ?? FamilyNumber, Budget, value ?? Value, field0x120 ?? Field0x120,
            flags ?? Flags, Members.ToArray(), field0x13C ?? Field0x13C, Field0x14E.Count == 6 ? Field0x14E.ToArray() : new short[6]);

    /// <summary>Creates a new family for a newly created household, initialized with the standard 20,000 simoleons budget and zero initial house value.</summary>
    public static Family CreateNew(int familyNumber, int houseNumber, IReadOnlyList<uint> members, int budget = 20_000, int value = 0, uint flags = 0) =>
        new(9, houseNumber, null, familyNumber, budget, value, 0, flags, members.ToArray(), 0, new short[6]);
}
