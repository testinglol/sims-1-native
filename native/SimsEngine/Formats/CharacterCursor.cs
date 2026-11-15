using System.Buffers.Binary;
using System.Globalization;

namespace SimsEngine.Formats;

/// <summary>Reads the values of a character or mesh file in either of its two forms. The binary form (BCF, BMF) is
/// little-endian, with one-byte length-prefixed strings (a 0xFF length is followed by a u32, as Sims.exe
/// FUN_005b90c2 reads them). The text form (CMX, SKN) has numbers as space-separated tokens and each string as a whole
/// line, so a string always starts a new line. Every read is bounds-checked; errors name the file and the byte offset
/// (the start of the line, in text).</summary>
internal sealed class CharacterCursor
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly string _source;
    private readonly string[]? _lines;
    private readonly int[]? _lineOffsets;
    private string[] _tokens = [];
    private int _token;
    private int _line;
    private int _position;

    private CharacterCursor(ReadOnlyMemory<byte> data, string source, string[]? lines, int[]? lineOffsets, int position)
    {
        _data = data;
        _source = source;
        _lines = lines;
        _lineOffsets = lineOffsets;
        _position = position;
    }

    /// <summary>A cursor over binary data starting at <paramref name="start"/>.</summary>
    public static CharacterCursor Binary(ReadOnlyMemory<byte> data, string source, int start = 0) => new(data, source, null, null, start);

    /// <summary>A cursor over the lines of text data from byte <paramref name="start"/>. Line breaks may be CRLF or LF.</summary>
    public static CharacterCursor Text(ReadOnlyMemory<byte> data, string source, int start = 0)
    {
        var offsets = new List<int>();
        var lines = new List<string>();
        var span = data.Span;
        var begin = start;
        for (var index = start; index <= span.Length; index++)
        {
            if (index < span.Length && span[index] != '\n') continue;
            var end = index;
            if (end > begin && span[end - 1] == '\r') end--;
            if (index == span.Length && begin == span.Length) break;
            offsets.Add(begin);
            lines.Add(ResourceReader.Latin1(span[begin..end]));
            begin = index + 1;
        }
        return new CharacterCursor(data, source, lines.ToArray(), offsets.ToArray(), 0);
    }

    /// <summary>Whether this cursor reads the text form.</summary>
    public bool IsText => _lines is not null;

    /// <summary>The bytes or lines still unread, used to bound counts before anything is allocated.</summary>
    public int Remaining => _lines is null ? _data.Length - _position : _lines.Length - _line;

    /// <summary>The byte offset the next read starts at (the start of the current line, in text).</summary>
    public int Offset => _lines is null ? _position : _lineOffsets!.Length == 0 ? 0 : _lineOffsets[Math.Min(_line, _lineOffsets.Length - 1)];

    public ResourceFormatException Error(string problem) => new(_source, null, Offset, problem);

    /// <summary>A count of items, each at least <paramref name="minimumBytes"/> bytes long in binary (one line in text). A
    /// count that cannot fit in what is left is an error.</summary>
    public int Count(int minimumBytes)
    {
        var value = Int32();
        if (value < 0) throw Error($"negative count {value}");
        var room = _lines is null ? Remaining / minimumBytes : Remaining;
        if (value > room) throw Error($"count {value} cannot fit in the {Remaining} {(_lines is null ? "bytes" : "lines")} left");
        return value;
    }

    /// <summary>A signed 32-bit integer (a token in text).</summary>
    public int Int32()
    {
        if (_lines is null)
        {
            if (_data.Length - _position < 4) throw Error($"needs 4 bytes, {_data.Length - _position} left");
            var value = BinaryPrimitives.ReadInt32LittleEndian(_data.Span[_position..]);
            _position += 4;
            return value;
        }
        var token = NextToken();
        if (!int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) throw Error($"\"{token}\" is not an integer");
        return number;
    }

    /// <summary>A 32-bit float (the bits of a u32 in binary, a decimal token in text).</summary>
    public float Float32()
    {
        if (_lines is null)
        {
            if (_data.Length - _position < 4) throw Error($"needs 4 bytes, {_data.Length - _position} left");
            var value = BinaryPrimitives.ReadSingleLittleEndian(_data.Span[_position..]);
            _position += 4;
            return value;
        }
        var token = NextToken();
        if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) throw Error($"\"{token}\" is not a number");
        return number;
    }

    /// <summary>A string: length-prefixed in binary, a whole line in text.</summary>
    public string Text()
    {
        if (_lines is null)
        {
            int length = U8();
            if (length == 0xFF)
            {
                var extended = Int32();
                if (extended < 0 || extended > Remaining) throw Error($"string length {extended} runs past the data");
                length = extended;
            }
            if (length > Remaining) throw Error($"string of {length} bytes runs past the data");
            var text = ResourceReader.Latin1(_data.Span.Slice(_position, length));
            _position += length;
            return text;
        }
        if (_token != _tokens.Length) throw Error("a string must start a new line");
        if (_line >= _lines.Length) throw Error("the text ends where a string is expected");
        var value = _lines[_line++];
        _tokens = [];
        _token = 0;
        return value;
    }

    /// <summary>Skips the text up to and including the first line that starts with <paramref name="prefix"/>, and
    /// returns that line. The comment lines before a CMX's version line are read this way.</summary>
    public string ReadLineStartingWith(string prefix)
    {
        if (_lines is null) throw Error("only text has lines");
        while (_line < _lines.Length && !_lines[_line].StartsWith(prefix, StringComparison.Ordinal)) _line++;
        if (_line >= _lines.Length) throw Error($"no line starts with \"{prefix}\"");
        _tokens = [];
        _token = 0;
        return _lines[_line++];
    }

    /// <summary>Checks that nothing follows the last section: only zero bytes in binary, only blank lines in text.</summary>
    public void ExpectEnd()
    {
        if (_lines is null)
        {
            for (var index = _position; index < _data.Length; index++)
                if (_data.Span[index] != 0) throw new ResourceFormatException(_source, null, index, $"{_data.Length - index} bytes follow the last section");
            return;
        }
        if (_token < _tokens.Length) throw Error("numbers follow the last section");
        for (var index = _line; index < _lines.Length; index++)
            if (_lines[index].Trim().Length > 0) throw new ResourceFormatException(_source, null, _lineOffsets![index], "text follows the last section");
    }

    private byte U8()
    {
        if (_data.Length - _position < 1) throw Error("needs 1 byte, none left");
        return _data.Span[_position++];
    }

    private string NextToken()
    {
        while (_token == _tokens.Length)
        {
            if (_line >= _lines!.Length) throw Error("the text ends where a number is expected");
            _tokens = _lines[_line].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            _line++;
            _token = 0;
            if (_tokens.Length == 0) throw new ResourceFormatException(_source, null, _lineOffsets![_line - 1], "a blank line where a number is expected");
        }
        return _tokens[_token++];
    }
}
