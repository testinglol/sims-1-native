using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SimsEngine.Tests;

/// <summary>Writes character and mesh content in the text form (CMX, SKN: one record per line, strings as lines) or the binary
/// form (BCF, BMF: little-endian values and length-prefixed strings), so one test model can be checked in both forms.</summary>
internal sealed class CharacterWriter(bool text)
{
    private readonly List<byte> _bytes = [];
    private readonly StringBuilder _text = new();

    /// <summary>The header of a text CMX file: a comment line and the version line.</summary>
    public CharacterWriter CmxHeader()
    {
        if (text) _text.Append("// Character File. Copyright 1997, Maxis Inc.\r\nversion 300\r\n");
        return this;
    }

    public CharacterWriter Int(int value)
    {
        if (text) _text.Append(value.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        else _bytes.AddRange(BitConverter.GetBytes(value));
        return this;
    }

    /// <summary>Several integers: one line of text, or consecutive values in binary.</summary>
    public CharacterWriter Ints(params int[] values)
    {
        if (text) _text.Append(string.Join(' ', values.Select(value => value.ToString(CultureInfo.InvariantCulture)))).Append("\r\n");
        else foreach (var value in values) _bytes.AddRange(BitConverter.GetBytes(value));
        return this;
    }

    public CharacterWriter Float(float value)
    {
        if (text) _text.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
        else _bytes.AddRange(BitConverter.GetBytes(value));
        return this;
    }

    /// <summary>Several floats: one line of text, or consecutive values in binary.</summary>
    public CharacterWriter Floats(params float[] values)
    {
        if (text) _text.Append(string.Join(' ', values.Select(value => value.ToString("R", CultureInfo.InvariantCulture)))).Append("\r\n");
        else foreach (var value in values) _bytes.AddRange(BitConverter.GetBytes(value));
        return this;
    }

    /// <summary>A string: a line of text, or a one-byte length and the bytes (a 0xFF length is followed by a u32 length).</summary>
    public CharacterWriter Str(string value)
    {
        if (text)
        {
            _text.Append(value).Append("\r\n");
            return this;
        }
        var bytes = Encoding.Latin1.GetBytes(value);
        if (bytes.Length < 0xFF) _bytes.Add((byte)bytes.Length);
        else
        {
            _bytes.Add(0xFF);
            _bytes.AddRange(BitConverter.GetBytes(bytes.Length));
        }
        _bytes.AddRange(bytes);
        return this;
    }

    /// <summary>Raw bytes in binary form (for hand-made damage); text form appends them as Latin-1.</summary>
    public CharacterWriter Raw(params byte[] bytes)
    {
        if (text) _text.Append(Encoding.Latin1.GetString(bytes));
        else _bytes.AddRange(bytes);
        return this;
    }

    public byte[] ToArray() => text ? Encoding.Latin1.GetBytes(_text.ToString()) : [.. _bytes];

    /// <summary>A binary u32 at the given position, for building a count the test chooses.</summary>
    public static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }
}
