namespace SimsEngine.Formats;

/// <summary>The parameter and local variable names of a behaviour tree (TPRP), read as Sims.exe FUN_004742c6 does: a
/// u32 zero, a u32 version, the tag "PRPT", a u32 parameter count and a u32 local count, then the parameter names and
/// the local names as zero-terminated strings padded to an even offset. Version 1 adds a u32 flag, 2 one byte per
/// parameter (1 when absent, FUN_004744ca), 3 a u32 at structure offset 0x1fc and 4 a u32 at offset 8.
/// <para>Version 5 occurs only in The Sims Online objects, sometimes without the tag; Sims.exe does not read it
/// differently, but those files store the names as UTF-8 strings with a 7-bit variable-length size and the flag as one
/// byte (layout inferred from the files). It is decoded so converted objects keep their names, and
/// <see cref="ReadBySimsExe"/> is false.</para></summary>
public sealed class TreeParameterNames
{
    public int Version { get; }
    public IReadOnlyList<string> ParameterNames { get; }
    public IReadOnlyList<string> LocalNames { get; }
    public bool Flag { get; }
    public IReadOnlyList<byte> ParameterFlags { get; }
    public uint Value1fc { get; }
    public uint Value08 { get; }

    /// <summary>False for the version 5 layout of The Sims Online.</summary>
    public bool ReadBySimsExe => Version < 5;

    private TreeParameterNames(int version, string[] parameters, string[] locals, bool flag, byte[] parameterFlags,
        uint value1fc, uint value08)
    {
        Version = version;
        ParameterNames = parameters;
        LocalNames = locals;
        Flag = flag;
        ParameterFlags = parameterFlags;
        Value1fc = value1fc;
        Value08 = value08;
    }

    public static TreeParameterNames Read(IffFile file, IffChunk chunk)
    {
        var reader = file.Reader(chunk);
        reader.U32();
        var version = reader.S32();
        var tag = reader.Bytes(4);
        if (version < 5 && !tag.SequenceEqual("PRPT"u8)) throw reader.Error("missing the PRPT tag");
        var parameterCount = reader.S32();
        var localCount = reader.S32();
        if (parameterCount < 0 || localCount < 0 || parameterCount + localCount > reader.Remaining)
            throw reader.Error($"{parameterCount} parameter and {localCount} local names do not fit the chunk");
        var online = version >= 5;
        string Name(ref ResourceReader reader) => online ? reader.VariableLengthString() : reader.EvenPaddedString();
        var parameters = new string[parameterCount];
        for (var index = 0; index < parameterCount; index++) parameters[index] = Name(ref reader);
        var locals = new string[localCount];
        for (var index = 0; index < localCount; index++) locals[index] = Name(ref reader);
        var flag = version >= 1 && (online ? reader.U8() : reader.U32()) != 0;
        var parameterFlags = new byte[parameterCount];
        if (version >= 2) reader.Bytes(parameterCount).CopyTo(parameterFlags);
        else parameterFlags.AsSpan().Fill(1);
        var value1fc = version >= 3 ? reader.U32() : 0;
        var value08 = version >= 4 ? reader.U32() : 0;
        return new TreeParameterNames(version, parameters, locals, flag, parameterFlags, value1fc, value08);
    }
}

/// <summary>One named constant of a TRCN chunk. <see cref="Flags"/> and <see cref="Value"/> are the two u32s Sims.exe
/// stores at entry offsets +4 and +8; the range applies when <see cref="RangeEnabled"/> is set.</summary>
public sealed record ConstantLabel(uint Flags, uint Value, string Label, string Comment, bool RangeEnabled, short Low, short High);

/// <summary>The labels of a constant table (TRCN), read as Sims.exe FUN_00473b0f and FUN_00473984 do: a u32 zero, a
/// u32 version, the tag "NCRT" and a u32 count, then per entry two u32s and the label and comment as zero-terminated
/// strings padded to an even offset. Version 1 adds a range-enabled byte and a 16-bit low and high bound (0, 0 and 100
/// when absent).
/// <para>Version 2 occurs only in The Sims Online objects, which store the strings with a 7-bit variable-length size
/// and no padding (layout inferred from the files); <see cref="ReadBySimsExe"/> is false for it.</para></summary>
public sealed class ConstantLabels
{
    public int Version { get; }
    public IReadOnlyList<ConstantLabel> Entries { get; }

    /// <summary>False for the version 2 layout of The Sims Online.</summary>
    public bool ReadBySimsExe => Version < 2;

    private ConstantLabels(int version, ConstantLabel[] entries)
    {
        Version = version;
        Entries = entries;
    }

    public static ConstantLabels Read(IffFile file, IffChunk chunk)
    {
        var reader = file.Reader(chunk);
        reader.U32();
        var version = reader.S32();
        if (!reader.Bytes(4).SequenceEqual("NCRT"u8)) throw reader.Error("missing the NCRT tag");
        var count = reader.S32();
        if (count < 0 || count > reader.Remaining / 10) throw reader.Error($"{count} labels do not fit the chunk");
        var online = version >= 2;
        var entries = new ConstantLabel[count];
        for (var index = 0; index < count; index++)
        {
            var flags = reader.U32();
            var value = reader.U32();
            var label = online ? reader.VariableLengthString() : reader.EvenPaddedString();
            var comment = online ? reader.VariableLengthString() : reader.EvenPaddedString();
            bool rangeEnabled = false;
            short low = 0, high = 100;
            if (version >= 1)
            {
                rangeEnabled = reader.U8() != 0;
                low = reader.S16();
                high = reader.S16();
            }
            entries[index] = new ConstantLabel(flags, value, label, comment, rangeEnabled, low, high);
        }
        return new ConstantLabels(version, entries);
    }
}
