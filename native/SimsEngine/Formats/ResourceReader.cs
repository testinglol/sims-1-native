using System.Buffers.Binary;
using System.Text;

namespace SimsEngine.Formats;

/// <summary>Malformed game data. The message names the file, the chunk (when there is one) and the byte offset.</summary>
public sealed class ResourceFormatException(string source, string? chunk, int offset, string problem)
    : FormatException($"{source}{(chunk is null ? "" : $" {chunk}")} at byte {offset}: {problem}")
{
    public string File { get; } = source;
    public string? Chunk { get; } = chunk;
    public int Offset { get; } = offset;
}

/// <summary>A bounds-checked little-endian cursor over resource bytes. Every read past the end throws a
/// <see cref="ResourceFormatException"/> that names where the data came from.</summary>
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

    /// <summary>A zero-terminated string. Data that ends without the terminator ends the string.</summary>
    public string CString(byte language = 0)
    {
        var rest = _data[Position..];
        var end = rest.IndexOf((byte)0);
        var bytes = end < 0 ? rest : rest[..end];
        Position += end < 0 ? rest.Length : end + 1;
        return language == 0 ? Latin1(bytes) : Decode(bytes, language);
    }

    /// <summary>A zero-terminated string after which the stream skips to an even offset from the start of the chunk
    /// (the Sims.exe resource stream's string read, FUN_00464c84 and FUN_00464d2c).</summary>
    public string EvenPaddedString()
    {
        var text = CString();
        if ((Position & 1) != 0 && Position < Length) Position++;
        return text;
    }

    /// <summary>A UTF-8 string whose byte length is a 7-bit variable-length integer (The Sims Online formats; layout
    /// inferred from the TSO object files).</summary>
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

    /// <summary>A length-prefixed string (one length byte).</summary>
    public string PascalString(byte language = 0) => language == 0 ? Latin1(Bytes(U8())) : Decode(Bytes(U8()), language);

    /// <summary>The game's strings are single-byte Windows text; Latin-1 keeps every byte value.</summary>
    public static string Latin1(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

    private static bool _codePagesRegistered;

    /// <summary>Gets the encoding for a Windows codepage, registering CodePagesEncodingProvider if needed.</summary>
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

    /// <summary>Authoritative Sims 1 LanguageCode 1..20 mapping to Windows codepages (TSLanguage.s).</summary>
    public static Encoding EncodingForLanguage(byte language) => language switch
    {
        12 => GetCodePageEncoding(1255), // Hebrew
        13 => GetCodePageEncoding(1251), // Russian Cyrillic
        15 => GetCodePageEncoding(932),  // Japanese Shift-JIS
        16 => GetCodePageEncoding(1250), // Polish Eastern European
        17 => GetCodePageEncoding(950),  // Traditional Chinese Big5
        18 => GetCodePageEncoding(936),  // Simplified Chinese GB2312
        19 => GetCodePageEncoding(874),  // Thai
        20 => GetCodePageEncoding(949),  // Korean Unified Hangul
        _ => GetCodePageEncoding(1252)   // Codes 1..11, 14, default: Windows Latin-1 (CP 1252)
    };

    /// <summary>Decodes byte span using the appropriate encoding for the specified Sims language code.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, byte language = 0) =>
        EncodingForLanguage(language).GetString(bytes);
}
