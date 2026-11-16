using System.Buffers.Binary;
namespace SimsEngine.Formats;

/// <summary>Represents a stored budget day, containing eight expense values when valid and seven otherwise.</summary>
public sealed record BudgetDay(bool Valid, IReadOnlyList<int> Values);

/// <summary>Represents a lot simulation record (SIMI), storing simulation globals, lot and architecture
/// valuations, and daily expense histories across chunk versions.</summary>
public sealed class SimulationState
{
    public int Version { get; }
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

    public SimulationState WithBudgetDays(IReadOnlyList<BudgetDay> budgetDays) => new(Version, Swap, Globals,
        StoredGlobal16, Field0x98, Field0x1A8, Field0x90, Field0xAC, StoredTime, Field0x94, Field0x9C,
        Field0xA0, Field0xA4, budgetDays);

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

    /// <summary>Serializes the SIMI chunk data, optionally updating the stored RNG state and simulation
    /// globals while preserving format version and byte order.</summary>
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
        WriteLittleInt32(output, Version); // The resource stream header version is always stored little-endian.
        output.Write("IMIS"u8);

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
