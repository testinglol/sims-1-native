using System.Buffers.Binary;

namespace SimsEngine.Formats;

/// <summary>The resource stream Sims.exe saves families, neighbours, houses and lot state with (FUN_00464faf sets it
/// up, FUN_00464a5e, FUN_00464973, FUN_00464895 and FUN_00464d2c move the values). A chunk starts with a 12-byte
/// header: a byte that, when nonzero, makes every value big-endian, three unused bytes, a u32 version (read as stored)
/// and the type tag reversed, which Sims.exe does not check. The values follow without alignment, except that a string
/// is zero-terminated and padded to an even offset. The same functions write the chunk, so the layout of each chunk
/// type is its serializer read in stream order, with the version deciding which fields are present.
/// <para>A serializer can switch the stream to field coding (<see cref="BeginFieldCoding"/>, FUN_004645e3), usually
/// after a stored byte that says whether the rest is coded. Each number is then a bit field read most significant bit
/// first (FUN_004647a1): one bit, zero meaning the value 0; otherwise two bits that pick one of four widths, then the
/// value in that many bits, sign-extended from its top bit (FUN_00464696). The widths are 2, 4, 6 or 8 bits for bytes,
/// 5, 8, 13 or 16 for s16s and 6, 11, 21 or 32 for u32s and floats (set up by FUN_004645e8, FUN_0046464e and
/// FUN_00464666). A string starts at the next whole byte and is stored as without coding.</para>
/// <para>A serializer can also frame a variable-size record as a block (<see cref="BeginBlock"/>): a stored offset of
/// the next block, so that a reader can move past the record (<see cref="EndBlock"/>) without decoding it.</para></summary>
public ref struct PersistReader
{
    private static ReadOnlySpan<byte> ByteWidths => [2, 4, 6, 8];
    private static ReadOnlySpan<byte> Int16Widths => [5, 8, 13, 16];
    private static ReadOnlySpan<byte> Int32Widths => [6, 11, 21, 32];

    private ResourceReader _reader;
    private readonly bool _swap;
    private bool _coded;
    private int _bitMask;
    private byte _bits;
    private int _nextBlock;

    /// <summary>Size of the header; stored stream offsets count from its end.</summary>
    public const int HeaderSize = 12;

    public int Version { get; }
    public readonly int Position => _reader.Position;
    public readonly int Remaining => _reader.Remaining;

    public PersistReader(IffFile file, IffChunk chunk)
    {
        _reader = file.Reader(chunk);
        _swap = _reader.U8() != 0;
        _reader.Bytes(3);
        Version = _reader.S32();
        _reader.Bytes(4);
    }

    public readonly ResourceFormatException Error(string problem) => _reader.Error(problem);

    /// <summary>Whether <see cref="BeginFieldCoding"/> has been called.</summary>
    public readonly bool FieldCoded => _coded;

    /// <summary>Reads every later number as a bit field (FUN_004645e3).</summary>
    public void BeginFieldCoding()
    {
        _coded = true;
        _bitMask = 0;
    }

    /// <summary>The stored flag that serializers read before switching to field coding, then the switch when it is
    /// set (for example FUN_0041144b).</summary>
    public void ReadCodingFlag()
    {
        if (Bool()) BeginFieldCoding();
    }

    private uint Bits(int count)
    {
        var value = 0u;
        for (var bit = count - 1; bit >= 0; bit--)
        {
            if (_bitMask == 0)
            {
                _bits = _reader.U8();
                _bitMask = 0x80;
            }
            if ((_bits & _bitMask) != 0) value |= 1u << bit;
            _bitMask >>= 1;
        }
        return value;
    }

    private uint Field(ReadOnlySpan<byte> widths)
    {
        if (Bits(1) == 0) return 0;
        var width = widths[(int)Bits(2)];
        var value = Bits(width);
        var sign = uint.MaxValue << (width - 1);
        return (value & sign) != 0 ? value | sign : value;
    }

    public int Int32()
    {
        if (_coded) return (int)Field(Int32Widths);
        var bytes = _reader.Bytes(4);
        return _swap ? BinaryPrimitives.ReadInt32BigEndian(bytes) : BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    /// <summary>A 32-bit float, swapped like a u32 (FUN_00464b48).</summary>
    public float Single() => BitConverter.Int32BitsToSingle(Int32());

    public short Int16()
    {
        if (_coded) return (short)Field(Int16Widths);
        var bytes = _reader.Bytes(2);
        return _swap ? BinaryPrimitives.ReadInt16BigEndian(bytes) : BinaryPrimitives.ReadInt16LittleEndian(bytes);
    }

    public short[] Int16s(int count)
    {
        var values = new short[count];
        for (var index = 0; index < count; index++) values[index] = Int16();
        return values;
    }

    public byte Byte() => _coded ? (byte)Field(ByteWidths) : _reader.U8();

    /// <summary>One byte, nonzero meaning true (FUN_0046494e).</summary>
    public bool Bool() => Byte() != 0;

    public string String()
    {
        _bitMask = 0;
        return _reader.EvenPaddedString();
    }

    /// <summary>Starts a block (FUN_00464c0a): the next whole byte holds a u32, swapped but never coded, giving the
    /// stream offset (from the end of the header) where the next block starts.</summary>
    public void BeginBlock()
    {
        _bitMask = 0;
        var bytes = _reader.Bytes(4);
        _nextBlock = _swap ? BinaryPrimitives.ReadInt32BigEndian(bytes) : BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    /// <summary>The stream position where the block started by <see cref="BeginBlock"/> ends.</summary>
    public readonly int BlockEnd => _nextBlock + HeaderSize;

    /// <summary>Moves to the end of the current block (FUN_00464c6e), past whatever the block holds.</summary>
    public void EndBlock()
    {
        if (_nextBlock < Position - HeaderSize || BlockEnd > _reader.Length) throw Error($"block end {_nextBlock} is outside the chunk");
        _bitMask = 0;
        _reader.Position = BlockEnd;
    }

    /// <summary>The raw bytes from the current position to <paramref name="end"/>.</summary>
    public ReadOnlySpan<byte> BytesTo(int end)
    {
        if (end < Position || end > _reader.Length) throw Error($"offset {end} is outside the chunk");
        return _reader.Bytes(end - Position);
    }

    /// <summary>A u32 element count, checked against the bytes left for elements of at least
    /// <paramref name="minimumSize"/> bytes.</summary>
    public int Count(int minimumSize)
    {
        var count = Int32();
        // A coded element can take a single bit.
        if (count < 0 || count > (_coded ? Remaining * 8 + 8 : Remaining / minimumSize)) throw Error($"{count} entries do not fit the chunk");
        return count;
    }
}
