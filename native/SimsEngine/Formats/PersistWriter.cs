using System.Buffers.Binary;
using System.Text;

namespace SimsEngine.Formats;

/// <summary>Writes structured persistence resource streams, supporting 12-byte headers, variable-width bit
/// field coding, block skip patching, and even-aligned string padding.</summary>
public sealed class PersistWriter
{
    private static ReadOnlySpan<byte> ByteWidths => [2, 4, 6, 8];
    private static ReadOnlySpan<byte> Int16Widths => [5, 8, 13, 16];
    private static ReadOnlySpan<byte> Int32Widths => [6, 11, 21, 32];

    private readonly MemoryStream _output = new();
    private readonly bool _swap;
    private readonly int _positionOffset;
    private bool _coded;
    private int _bitMask;
    private byte _bits;

    public const int HeaderSize = 12;

    private PersistWriter(bool swap, int positionOffset)
    {
        _swap = swap;
        _positionOffset = positionOffset;
    }

    /// <summary>Initializes a persistence stream with a 12-byte header containing the endian flag, version,
    /// and type tag.</summary>
    public static PersistWriter Start(bool swap, int version, string tag, int positionOffset = 0)
    {
        if (tag.Length != 4) throw new ArgumentException("The stored tag is four characters.", nameof(tag));
        var writer = new PersistWriter(swap, positionOffset);
        writer._output.WriteByte(swap ? (byte)1 : (byte)0);
        writer._output.Write([0, 0, 0]);
        writer.WriteRawInt32(version, false);
        writer._output.Write(Encoding.ASCII.GetBytes(tag));
        return writer;
    }

    public int Position => (int)_output.Position;

    public void WriteCodingFlag(bool coded)
    {
        WriteRawByte(coded ? (byte)1 : (byte)0);
        _coded = coded;
    }

    public bool FieldCoded => _coded;

    /// <summary>Writes a byte value. When field coding is active, values above 127 force the 8-bit width so
    /// signed extension preserves the unsigned byte value.</summary>
    public void Byte(byte value)
    {
        if (!_coded)
        {
            WriteRawByte(value);
            return;
        }
        if (value == 0)
        {
            Bits(0, 1);
            return;
        }
        Bits(1, 1);
        if (value >= 0x80)
        {
            Bits(3, 2);
            Bits(value, 8);
            return;
        }
        for (var index = 0; index < ByteWidths.Length; index++)
        {
            var width = ByteWidths[index];
            if (value > (1 << (width - 1)) - 1) continue;
            Bits((uint)index, 2);
            Bits(value, width);
            return;
        }
    }

    public void Bool(bool value) => Byte(value ? (byte)1 : (byte)0);

    public void Int16(short value)
    {
        if (_coded) Field(Int16Widths, value, 16);
        else WriteRawInt16(value);
    }

    public void Int16s(ReadOnlySpan<short> values)
    {
        foreach (var value in values) Int16(value);
    }

    public void Int32(int value)
    {
        if (_coded) Field(Int32Widths, value, 32);
        else WriteRawInt32(value, _swap);
    }

    public void UInt32(uint value) => Int32(unchecked((int)value));

    public void Single(float value) => Int32(BitConverter.SingleToInt32Bits(value));

    public void String(string value)
    {
        FlushBits();
        var bytes = Encoding.Latin1.GetBytes(value);
        _output.Write(bytes);
        _output.WriteByte(0);
        // Pads strings ending at an odd stream offset with 0xA3.
        if (((Position + _positionOffset) & 1) != 0) _output.WriteByte(0xA3);
    }

    public void Raw(ReadOnlySpan<byte> bytes)
    {
        FlushBits();
        _output.Write(bytes);
    }

    private int _blockStart;

    /// <summary>Emits a placeholder 32-bit offset for a record block to be patched by
    /// <see cref="EndBlock"/>.</summary>
    public void BeginBlock()
    {
        FlushBits();
        _blockStart = (int)_output.Position;
        WriteRawInt32(0, _swap);
    }

    public void EndBlock()
    {
        FlushBits();
        var end = Position - HeaderSize;
        var at = _blockStart;
        var bytes = _output.GetBuffer();
        if (_swap) BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(at, 4), end);
        else BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at, 4), end);
    }

    public byte[] ToArray()
    {
        FlushBits();
        return _output.ToArray();
    }

    /// <summary>Encodes a numeric value as a single zero bit for zero, or a set bit followed by a two-bit
    /// width selector and sign-extended data bits.</summary>
    private void Field(ReadOnlySpan<byte> widths, long value, int size)
    {
        if (value == 0)
        {
            Bits(0, 1);
            return;
        }
        Bits(1, 1);
        for (var index = 0; index < widths.Length; index++)
        {
            var width = widths[index];
            var minimum = width == 32 ? int.MinValue : -(1L << (width - 1));
            var maximum = width == 32 ? int.MaxValue : (1L << (width - 1)) - 1;
            if (value < minimum || value > maximum) continue;
            Bits((uint)index, 2);
            var mask = width == 32 ? uint.MaxValue : (1u << width) - 1;
            Bits((uint)value & mask, width);
            return;
        }
        throw new ArgumentOutOfRangeException(nameof(value), $"{value} does not fit a {size}-bit field.");
    }

    private void Bits(uint value, int count)
    {
        for (var bit = count - 1; bit >= 0; bit--)
        {
            if (_bitMask == 0)
            {
                _bits = 0;
                _bitMask = 0x80;
            }
            if ((value & (1u << bit)) != 0) _bits |= (byte)_bitMask;
            _bitMask >>= 1;
            if (_bitMask == 0) _output.WriteByte(_bits);
        }
    }

    private void FlushBits()
    {
        if (_bitMask != 0 && _bitMask != 0x80) _output.WriteByte(_bits);
        _bitMask = 0;
        _bits = 0;
    }

    private void WriteRawByte(byte value) => _output.WriteByte(value);

    private void WriteRawInt16(short value)
    {
        Span<byte> bytes = stackalloc byte[2];
        if (_swap) BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        else BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        _output.Write(bytes);
    }

    private void WriteRawInt32(int value, bool swap)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (swap) BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        else BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        _output.Write(bytes);
    }
}
