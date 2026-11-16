using System.Buffers.Binary;

namespace SimsEngine.Formats;

/// <summary>Reads structured persistence resource streams. Chunks begin with a 12-byte header containing an
/// endian flag, version, and type tag, followed by unaligned values, optional variable-width bit field
/// coding, and block skip offsets.</summary>
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

    /// <summary>Header size in bytes; stored stream offsets are relative to the end of the header.</summary>
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

    public readonly bool FieldCoded => _coded;

    /// <summary>Switches the stream to variable-width bit field decoding for subsequent numeric values.</summary>
    public void BeginFieldCoding()
    {
        _coded = true;
        _bitMask = 0;
    }

    /// <summary>Reads the single-byte flag indicating whether the following stream data uses variable-width
    /// field coding.</summary>
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

    public bool Bool() => Byte() != 0;

    public string String()
    {
        _bitMask = 0;
        return _reader.EvenPaddedString();
    }

    /// <summary>Begins a framed record block by reading the uncoded byte offset to the next block.</summary>
    public void BeginBlock()
    {
        _bitMask = 0;
        var bytes = _reader.Bytes(4);
        _nextBlock = _swap ? BinaryPrimitives.ReadInt32BigEndian(bytes) : BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    public readonly int BlockEnd => _nextBlock + HeaderSize;

    /// <summary>Advances the stream cursor to the end of the current block, skipping any remaining unread
    /// data.</summary>
    public void EndBlock()
    {
        if (_nextBlock < Position - HeaderSize || BlockEnd > _reader.Length) throw Error($"block end {_nextBlock} is outside the chunk");
        _bitMask = 0;
        _reader.Position = BlockEnd;
    }

    public ReadOnlySpan<byte> BytesTo(int end)
    {
        if (end < Position || end > _reader.Length) throw Error($"offset {end} is outside the chunk");
        return _reader.Bytes(end - Position);
    }

    public int Count(int minimumSize)
    {
        var count = Int32();
        // When field coded, a zero value occupies only a single bit.
        if (count < 0 || count > (_coded ? Remaining * 8 + 8 : Remaining / minimumSize)) throw Error($"{count} entries do not fit the chunk");
        return count;
    }
}
