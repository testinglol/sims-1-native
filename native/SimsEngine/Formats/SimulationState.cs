using System.Buffers.Binary;
namespace SimsEngine.Formats;

/// <summary>One stored budget day (FUN_0046d38c): eight values when <see cref="Valid"/>, seven otherwise.</summary>
public sealed record BudgetDay(bool Valid, IReadOnlyList<int> Values);

/// <summary>The simulation record (SIMI) of a lot file, as Sims.exe FUN_0046d0cf reads it from the resource stream.
/// <para>Fields in stream order: the simulation globals as s16s (16 before version 18, 32 before 65, then 64; before
/// version 18 a u32 follows that Sims.exe keeps as global 16); a u32 kept at +0x98; an s16 kept at +0x1a8; from
/// version 2 a u32 kept at +0x90; from version 14 two u32s (+0xac and a time value Sims.exe replaces with the current
/// time unless FUN_004074bc holds); from version 15 a u32 that Sims.exe adds to +0x90 and clears; from version 37 three
/// u32s at +0x9c, +0xa0 and +0xa4 (4500, 0 and 0 before); from version 38 six budget days (FUN_0046d38c: a u32 that
/// selects eight values when nonzero and seven when zero, then the values), the first kept at +0x184 and the other
/// five from +0xe4.</para>
/// <para>Sims.exe also rewrites some globals after reading old versions (below 6, 17 and 21); this reader keeps the
/// stored values. Inferred, not traced: that +0x9c, +0xa0 and +0xa4 are the lot, furnishings and architecture
/// values.</para></summary>
public sealed class SimulationState
{
    public int Version { get; }
    /// <summary>Whether the resource stream stores its numeric fields big-endian.</summary>
    public bool Swap { get; }
    public IReadOnlyList<short> Globals { get; }
    public int? StoredGlobal16 { get; }
    public int Field0x98 { get; }
    public short Field0x1A8 { get; }
    public int Field0x90 { get; }
    public int Field0xAC { get; }
    public int StoredTime { get; }
    public int Field0x94 { get; }
    public int Field0x9C { get; }
    public int Field0xA0 { get; }
    public int Field0xA4 { get; }
    public IReadOnlyList<BudgetDay> BudgetDays { get; }

    /// <summary>Returns this SIMI record with updated daily expense history, preserving all other source fields.</summary>
    public SimulationState WithBudgetDays(IReadOnlyList<BudgetDay> budgetDays) => new(Version, Swap, Globals,
        StoredGlobal16, Field0x98, Field0x1A8, Field0x90, Field0xAC, StoredTime, Field0x94, Field0x9C,
        Field0xA0, Field0xA4, budgetDays);

    /// <summary>Returns this SIMI record with updated furnishings (+0xA0) and architecture (+0xA4) valuation fields.</summary>
    public SimulationState WithValuation(int fieldA0, int fieldA4) => new(Version, Swap, Globals,
        StoredGlobal16, Field0x98, Field0x1A8, Field0x90, Field0xAC, StoredTime, Field0x94, Field0x9C,
        fieldA0, fieldA4, BudgetDays);

    private SimulationState(int version, bool swap, IReadOnlyList<short> globals, int? global16, int field98, short field1A8,
        int field90, int fieldAC, int time, int field94, int field9C, int fieldA0, int fieldA4,
        IReadOnlyList<BudgetDay> budgetDays)
    {
        Version = version;
        Swap = swap;
        Globals = globals;
        StoredGlobal16 = global16;
        Field0x98 = field98;
        Field0x1A8 = field1A8;
        Field0x90 = field90;
        Field0xAC = fieldAC;
        StoredTime = time;
        Field0x94 = field94;
        Field0x9C = field9C;
        Field0xA0 = fieldA0;
        Field0xA4 = fieldA4;
        BudgetDays = budgetDays;
    }

    public static SimulationState Read(IffFile file, IffChunk chunk)
    {
        var stream = new PersistReader(file, chunk);
        var swap = chunk.Data.Span[0] != 0;
        var version = stream.Version;
        var globals = stream.Int16s(version < 0x12 ? 0x10 : version < 0x41 ? 0x20 : 0x40);
        int? global16 = version < 0x12 ? stream.Int32() : null;
        var field98 = stream.Int32();
        var field1A8 = stream.Int16();
        var field90 = version > 1 ? stream.Int32() : 0;
        int fieldAC = 0, time = 0;
        if (version > 0xd)
        {
            fieldAC = stream.Int32();
            time = stream.Int32();
        }
        var field94 = version >= 0xf ? stream.Int32() : 0;
        int field9C = 0x1194, fieldA0 = 0, fieldA4 = 0;
        if (version >= 0x25)
        {
            field9C = stream.Int32();
            fieldA0 = stream.Int32();
            fieldA4 = stream.Int32();
        }
        var days = new List<BudgetDay>();
        if (version >= 0x26)
        {
            for (var day = 0; day < 6; day++)
            {
                var valid = stream.Int32() != 0;
                var values = new int[valid ? 8 : 7];
                for (var index = 0; index < values.Length; index++) values[index] = stream.Int32();
                days.Add(new BudgetDay(valid, values));
            }
        }
        return new SimulationState(version, swap, globals, global16, field98, field1A8, field90, fieldAC, time, field94,
            field9C, fieldA0, fieldA4, days);
    }

    /// <summary>Serializes this SIMI resource stream as Sims.exe's FUN_0046d0cf field order, preserving its byte order
    /// and version. <paramref name="randomState"/> replaces the stored +0xac value when a native simulation tick has
    /// advanced it, and <paramref name="globals"/> replaces the stored global table (the first version-sized values
    /// are written). FUN_0046d0cf uses the same resource-stream fields to restore and save the record.</summary>
    public byte[] WriteChunkData(uint? randomState = null, ReadOnlySpan<short> globals = default)
    {
        if (Version < 0) throw new InvalidDataException($"Invalid SIMI version {Version}.");
        var globalCount = Version < 0x12 ? 0x10 : Version < 0x41 ? 0x20 : 0x40;
        if (Globals.Count != globalCount)
            throw new InvalidDataException($"SIMI version {Version} needs {globalCount} globals, found {Globals.Count}.");
        if (!globals.IsEmpty && globals.Length < globalCount)
            throw new InvalidDataException($"SIMI version {Version} needs {globalCount} globals, the replacement has {globals.Length}.");
        if (Version < 0x12 && StoredGlobal16 is null)
            throw new InvalidDataException($"SIMI version {Version} needs its legacy global 16 value.");
        if (Version >= 0x26 && BudgetDays.Count != 6)
            throw new InvalidDataException($"SIMI version {Version} needs six budget days, found {BudgetDays.Count}.");

        using var output = new MemoryStream();
        output.WriteByte(Swap ? (byte)1 : (byte)0);
        output.Write([0, 0, 0]);
        WriteLittleInt32(output, Version); // The resource-stream header version is always little-endian.
        output.Write("IMIS"u8); // Persist-stream type tag, reversed as FUN_00464faf stores it.

        for (var index = 0; index < globalCount; index++)
            WriteInt16(output, globals.IsEmpty ? Globals[index] : globals[index]);
        if (Version < 0x12) WriteInt32(output, StoredGlobal16!.Value);
        WriteInt32(output, Field0x98);
        WriteInt16(output, Field0x1A8);
        if (Version > 1) WriteInt32(output, Field0x90);
        if (Version > 0x0d)
        {
            WriteInt32(output, unchecked((int)(randomState ?? unchecked((uint)Field0xAC))));
            WriteInt32(output, StoredTime);
        }
        if (Version >= 0x0f) WriteInt32(output, Field0x94);
        if (Version >= 0x25)
        {
            WriteInt32(output, Field0x9C);
            WriteInt32(output, Field0xA0);
            WriteInt32(output, Field0xA4);
        }
        if (Version >= 0x26)
        {
            foreach (var day in BudgetDays)
            {
                var expected = day.Valid ? 8 : 7;
                if (day.Values.Count != expected)
                    throw new InvalidDataException($"SIMI budget day needs {expected} values, found {day.Values.Count}.");
                WriteInt32(output, day.Valid ? 1 : 0);
                foreach (var value in day.Values) WriteInt32(output, value);
            }
        }
        return output.ToArray();
    }

    private static void WriteLittleInt32(Stream output, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private void WriteInt16(Stream output, short value)
    {
        Span<byte> bytes = stackalloc byte[2];
        if (Swap) BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        else BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private void WriteInt32(Stream output, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (Swap) BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        else BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }
}
