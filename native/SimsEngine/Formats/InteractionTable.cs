namespace SimsEngine.Formats;

/// <summary>One motive advertisement of an interaction. In versions 6 and later the three values are stored in the
/// order minimum, delta, personality (Sims.exe FUN_004760ae reads entry offsets +2, +4, +0); versions 0 to 5 store
/// the maximum instead of the delta, and FUN_004761b4 converts it (delta = stored - minimum). The meaning of the
/// three values is inferred from their use in autonomy scoring (FUN_004214d4).</summary>
public readonly record struct MotiveAdvertisement(short Minimum, short Delta, short Personality);

/// <summary>One pie-menu interaction of an object (a TTAB entry).</summary>
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

/// <summary>An object's interaction table (TTAB), read as Sims.exe FUN_00475e2d does: an int16 entry count and an
/// int16 version; versions above 8 add a byte that switches the rest of the chunk to the bit-packed field encoding
/// (<see cref="FieldReader"/>). Entries use FUN_004761b4 for versions 0 to 5 and FUN_004760ae for 6 and later.
/// In versions 0 to 3 the TTAs string is the entry's own index.
/// <para>Where the shipped files disagree with the decompiled readers, the files win (inferred): the 32-bit values of
/// versions 2 to 5 sit on 4-byte boundaries, with two padding bytes after the 16-bit motive count (the layout of a
/// stored C structure), and the two version 7 tables carry no inline names although FUN_004760ae would read one
/// through FUN_00464c84 (their names come from the TTAs chunk). An empty chunk is an empty table, and a table that
/// ends after its header is read plainly.</para></summary>
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

    /// <summary>Versions 0 to 5 (FUN_004761b4), always stored plainly. Motives are numbered by position, except in
    /// versions 0 and 1, which store (motive, maximum) pairs.</summary>
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

/// <summary>The Sims.exe resource stream (FUN_00464973, FUN_00464a5e, FUN_00464b48, FUN_00464c84): little-endian
/// values, or, once a chunk switches it on, the field encoding. An encoded value is one bit (0 means the value is
/// 0), then two bits choosing a width, then the value in that many bits, most significant first and sign-extended
/// (FUN_00464696, FUN_004647a1). The width tables live in zero-initialised data that Sims.exe fills at start-up, so
/// they are not in the executable file; the widths here (5, 8, 13, 16 for 16-bit fields and 6, 11, 21, 32 for
/// 32-bit fields) are inferred: with them all 1399 non-empty version 9 TTAB chunks in the game decode to exactly
/// their last byte.</summary>
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

    /// <summary>The offset of the next unread byte.</summary>
    public readonly int Position => _position;

    public readonly ResourceFormatException Error(string problem) => new(_source, _chunk, _position, problem);

    public short Int16() => _encoded ? (short)Encoded(ShortWidths) : (short)(Byte() | Byte() << 8);

    public int Int32() => _encoded ? Encoded(IntWidths) : Byte() | Byte() << 8 | Byte() << 16 | Byte() << 24;

    /// <summary>A plain 32-bit value at the next 4-byte boundary of the chunk (old TTAB versions; inferred from the
    /// files).</summary>
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
        // Sign-extend from the top bit of the field.
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
