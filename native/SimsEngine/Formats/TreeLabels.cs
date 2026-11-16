namespace SimsEngine.Formats;

/// <summary>Parses behaviour tree parameter and local variable names (TPRP), handling versioned headers,
/// parameter flags, and variable-length string encodings.</summary>
public sealed class TreeParameterNames
{
    public int Version { get; }
    public IReadOnlyList<string> ParameterNames { get; }
    public IReadOnlyList<string> LocalNames { get; }
    public bool Flag { get; }
    public IReadOnlyList<byte> ParameterFlags { get; }
    public uint Value1fc { get; }
    public uint Value08 { get; }

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

/// <summary>Represents a named constant in a TRCN chunk, including optional range validation bounds.</summary>
public sealed record ConstantLabel(uint Flags, uint Value, string Label, string Comment, bool RangeEnabled, short Low, short High);

/// <summary>Parses constant table labels (TRCN), decoding constant values, names, comments, and versioned
/// range limit fields.</summary>
public sealed class ConstantLabels
{
    public int Version { get; }
    public IReadOnlyList<ConstantLabel> Entries { get; }

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
