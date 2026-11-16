namespace SimsEngine.Formats;

/// <summary>Represents a motive advertisement. In format version 6 and later, values are stored as minimum,
/// delta, and personality; earlier versions store the maximum instead of delta, computed as <c>delta =
/// stored - minimum</c>.</summary>
public readonly record struct MotiveAdvertisement(short Minimum, short Delta, short Personality);

public sealed record Interaction(
    ushort ActionTree,
    ushort GuardTree,
    uint Flags,
    int TtaIndex,
    uint AttenuationCode,
    float AttenuationValue,
    uint AutonomyThreshold,
    int JoiningIndex,
    IReadOnlyList<MotiveAdvertisement> Motives,
    string? Name);

/// <summary>Represents an object interaction table (TTAB). Versions 8 and below use plain binary streams
/// with aligned fields, while version 9 and later use bit-packed variable-width field encoding.</summary>
public sealed class InteractionTable
{
    public short Version { get; }
    public bool FieldEncoded { get; }
    public IReadOnlyList<Interaction> Interactions { get; }

    private InteractionTable(short version, bool fieldEncoded, IReadOnlyList<Interaction> interactions)
    {
        Version = version;
        FieldEncoded = fieldEncoded;
        Interactions = interactions;
    }

    public InteractionTable WithNames(Func<Interaction, string?> resolve) =>
        new(Version, FieldEncoded, Interactions.Select(entry => entry with { Name = resolve(entry) }).ToArray());

    public static InteractionTable Read(IffFile file, IffChunk chunk)
    {
        var bytes = file.Reader(chunk);
        if (bytes.Remaining < 4) return new InteractionTable(0, false, []);
        var count = bytes.S16();
        var version = bytes.S16();
        var encoded = version > 8 && bytes.Remaining > 0 && bytes.U8() != 0;
        var reader = new FieldReader(chunk.Data.Span, bytes.Position, encoded, file.Source, chunk.ToString());
        var interactions = new Interaction[Math.Max((int)count, 0)];
        for (var index = 0; index < interactions.Length; index++)
        {
            var entry = version < 6 ? ReadOld(ref reader, version, index) : ReadCurrent(ref reader, version, index);
            interactions[index] = entry;
        }
        return new InteractionTable(version, encoded, interactions);
    }

    private static Interaction ReadCurrent(ref FieldReader reader, short version, int index)
    {
        var action = (ushort)reader.Int16();
        var guard = (ushort)reader.Int16();
        var motiveCount = reader.Int32();
        if (motiveCount < 0 || motiveCount > 64) throw reader.Error($"interaction {index} has {motiveCount} motives");
        var flags = (uint)reader.Int32();
        var tta = reader.Int32();
        var attenuationCode = (uint)reader.Int32();
        var attenuationValue = BitConverter.Int32BitsToSingle(reader.Int32());
        var threshold = (uint)reader.Int32();
        var joining = reader.Int32();
        var motives = new MotiveAdvertisement[motiveCount];
        for (var motive = 0; motive < motiveCount; motive++)
            motives[motive] = new MotiveAdvertisement(reader.Int16(), reader.Int16(), reader.Int16());
        return new Interaction(action, guard, flags, tta, attenuationCode, attenuationValue, threshold, joining, motives, null);
    }

    /// <summary>Reads legacy interaction entries where versions 0 and 1 store explicit (motive, maximum)
    /// pairs while versions 2 through 5 number motives implicitly by position.</summary>
    private static Interaction ReadOld(ref FieldReader reader, short version, int index)
    {
        var action = (ushort)reader.Int16();
        var guard = version == 0 ? (ushort)0 : (ushort)reader.Int16();
        var count = reader.Int16();
        uint flags = 0, threshold = 0;
        int tta = index, joining = 0;
        float attenuation = 0;
        switch (version)
        {
            case 2:
                attenuation = BitConverter.Int32BitsToSingle(reader.AlignedInt32());
                break;
            case 3:
                flags = (uint)reader.Int16();
                attenuation = BitConverter.Int32BitsToSingle(reader.AlignedInt32());
                break;
            case 4 or 5:
                flags = (uint)reader.AlignedInt32();
                tta = reader.AlignedInt32();
                attenuation = BitConverter.Int32BitsToSingle(reader.AlignedInt32());
                threshold = (uint)reader.AlignedInt32();
                if (version == 5) joining = reader.AlignedInt32();
                break;
        }
        var maxima = new SortedDictionary<int, short>();
        for (var motive = 0; motive < count; motive++)
        {
            if (version <= 1)
            {
                var number = reader.Int16();
                maxima[number] = reader.Int16();
            }
            else maxima[motive] = reader.Int16();
        }
        var motives = new MotiveAdvertisement[maxima.Count == 0 ? 0 : maxima.Keys.Max() + 1];
        foreach (var (number, maximum) in maxima)
            if (number >= 0) motives[number] = new MotiveAdvertisement(0, maximum, 0);
        return new Interaction(action, guard, flags, tta, 0, attenuation, threshold, joining, motives, null);
    }
}

/// <summary>Reads bit-packed variable-width field encoded streams: a presence bit indicates zero, followed
/// by a two-bit selector mapping to bit widths (5, 8, 13, 16 for 16-bit fields; 6, 11, 21, 32 for 32-bit
/// fields) stored MSB-first and sign-extended.</summary>
public ref struct FieldReader
{
    private static ReadOnlySpan<byte> ShortWidths => [5, 8, 13, 16];
    private static ReadOnlySpan<byte> IntWidths => [6, 11, 21, 32];

    private readonly ReadOnlySpan<byte> _data;
    private readonly bool _encoded;
    private readonly string _source;
    private readonly string _chunk;
    private int _position;
    private int _bit;

    public FieldReader(ReadOnlySpan<byte> data, int position, bool encoded, string source, string chunk)
    {
        _data = data;
        _position = position;
        _encoded = encoded;
        _source = source;
        _chunk = chunk;
    }

    public readonly int Position => _position;

    public readonly ResourceFormatException Error(string problem) => new(_source, _chunk, _position, problem);

    public short Int16() => _encoded ? (short)Encoded(ShortWidths) : (short)(Byte() | Byte() << 8);

    public int Int32() => _encoded ? Encoded(IntWidths) : Byte() | Byte() << 8 | Byte() << 16 | Byte() << 24;

    public int AlignedInt32()
    {
        _position = (_position + 3) & ~3;
        return Int32();
    }

    private int Byte()
    {
        if (_position >= _data.Length) throw Error("unexpected end of data");
        return _data[_position++];
    }

    private int Encoded(ReadOnlySpan<byte> widths)
    {
        if (Bits(1) == 0) return 0;
        var width = widths[(int)Bits(2)];
        var value = Bits(width);
        return width == 32 ? (int)value : (int)(value << (32 - width)) >> (32 - width);
    }

    private uint Bits(int count)
    {
        uint value = 0;
        for (var index = 0; index < count; index++)
        {
            if (_bit == 0)
            {
                if (_position >= _data.Length) throw Error("unexpected end of field-encoded data");
                _position++;
                _bit = 0x80;
            }
            if ((_data[_position - 1] & _bit) != 0) value |= 1u << (count - 1 - index);
            _bit >>= 1;
        }
        return value;
    }
}
