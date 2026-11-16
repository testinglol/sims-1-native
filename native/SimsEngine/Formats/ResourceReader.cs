using System.Buffers.Binary;
using System.Text;

namespace SimsEngine.Formats;

public sealed class ResourceFormatException(string source, string? chunk, int offset, string problem)
    : FormatException($"{source}{(chunk is null ? "" : $" {chunk}")} at byte {offset}: {problem}")
{
    public string File { get; } = source;
    public string? Chunk { get; } = chunk;
    public int Offset { get; } = offset;
}

/// <summary>A bounds-checked little-endian cursor for reading binary chunk data.</summary>
public ref struct ResourceReader(ReadOnlySpan<byte> data, string source, string? chunk = null)
{
    private readonly ReadOnlySpan<byte> _data = data;
    public int Position { get; set; }
    public readonly int Length => _data.Length;
    public readonly int Remaining => _data.Length - Position;

    public readonly ResourceFormatException Error(string problem) => new(source, chunk, Position, problem);

    public ReadOnlySpan<byte> Bytes(int count)
    {
        if (count < 0 || count > Remaining) throw Error($"needs {count} bytes, {Remaining} left");
        var span = _data.Slice(Position, count);
        Position += count;
        return span;
    }

    public byte U8() => Bytes(1)[0];
    public sbyte S8() => (sbyte)U8();
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(2));
    public short S16() => BinaryPrimitives.ReadInt16LittleEndian(Bytes(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
    public int S32() => BinaryPrimitives.ReadInt32LittleEndian(Bytes(4));
    public ushort U16BigEndian() => BinaryPrimitives.ReadUInt16BigEndian(Bytes(2));
    public uint U32BigEndian() => BinaryPrimitives.ReadUInt32BigEndian(Bytes(4));

    public string CString(byte language = 0)
    {
        var rest = _data[Position..];
        var end = rest.IndexOf((byte)0);
        var bytes = end < 0 ? rest : rest[..end];
        Position += end < 0 ? rest.Length : end + 1;
        return language == 0 ? Latin1(bytes) : Decode(bytes, language);
    }

    /// <summary>Reads a null-terminated string padded to an even byte offset from the start of the chunk.</summary>
    public string EvenPaddedString()
    {
        var text = CString();
        if ((Position & 1) != 0 && Position < Length) Position++;
        return text;
    }

    /// <summary>Reads a UTF-8 string prefixed by a 7-bit variable-length integer byte length.</summary>
    public string VariableLengthString()
    {
        int length = 0, shift = 0;
        byte part;
        do
        {
            part = U8();
            length |= (part & 0x7f) << shift;
            shift += 7;
        } while ((part & 0x80) != 0 && shift < 32);
        return Encoding.UTF8.GetString(Bytes(length));
    }

    public string PascalString(byte language = 0) => language == 0 ? Latin1(Bytes(U8())) : Decode(Bytes(U8()), language);

    public static string Latin1(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

    private static bool _codePagesRegistered;

    public static Encoding GetCodePageEncoding(int codepage)
    {
        if (!_codePagesRegistered)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _codePagesRegistered = true;
        }
        try
        {
            return Encoding.GetEncoding(codepage);
        }
        catch
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>Maps game language codes (1 through 20) to their corresponding Windows code page encodings.</summary>
    public static Encoding EncodingForLanguage(byte language) => language switch
    {
        12 => GetCodePageEncoding(1255),
        13 => GetCodePageEncoding(1251),
        15 => GetCodePageEncoding(932),
        16 => GetCodePageEncoding(1250),
        17 => GetCodePageEncoding(950),
        18 => GetCodePageEncoding(936),
        19 => GetCodePageEncoding(874),
        20 => GetCodePageEncoding(949),
        _ => GetCodePageEncoding(1252)
    };

    public static string Decode(ReadOnlySpan<byte> bytes, byte language = 0) =>
        EncodingForLanguage(language).GetString(bytes);
}
