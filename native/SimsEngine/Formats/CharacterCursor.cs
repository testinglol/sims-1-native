using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;

namespace SimsEngine.Formats;

/// <summary>Parses character and mesh data across both binary (BCF, BMF) and text (CMX, SKN) formats.
/// Binary strings use one-byte length prefixes where 0xFF indicates an extended 32-bit length, while text
/// forms use whitespace-delimited tokens and line-delimited strings.</summary>
internal sealed class CharacterCursor
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly string _source;
    private readonly bool _isText;
    private readonly int _initialLineCount;
    private int _nextLineOffset;
    private int _remainingLines;
    private int _lastConsumedLineOffset;
    private int _tokenCursor = -1;
    private int _tokenLineEnd;
    private int _position;

    [ThreadStatic]
    private static string?[]? t_shortStringCache;

    private static string DecodeLatin1Dedup(ReadOnlySpan<byte> span)
    {
        if (span.Length == 0) return string.Empty;
        if (span.Length > 48) return ResourceReader.Latin1(span);

        var cache = t_shortStringCache ??= new string?[512];
        uint hash = 2166136261u;
        for (var i = 0; i < span.Length; i++)
        {
            hash = (hash ^ span[i]) * 16777619u;
        }
        var slot = (int)(hash & 511u);
        var cached = cache[slot];
        if (cached is not null && cached.Length == span.Length)
        {
            var match = true;
            for (var i = 0; i < span.Length; i++)
            {
                if (cached[i] != (char)span[i])
                {
                    match = false;
                    break;
                }
            }
            if (match) return cached;
        }

        var created = ResourceReader.Latin1(span);
        cache[slot] = created;
        return created;
    }

    private CharacterCursor(ReadOnlyMemory<byte> data, string source, bool isText, int initialLineCount, int start)
    {
        _data = data;
        _source = source;
        _isText = isText;
        _initialLineCount = initialLineCount;
        _nextLineOffset = start;
        _remainingLines = initialLineCount;
        _position = start;
    }

    public static CharacterCursor Binary(ReadOnlyMemory<byte> data, string source, int start = 0) => new(data, source, false, 0, start);

    public static CharacterCursor Text(ReadOnlyMemory<byte> data, string source, int start = 0)
    {
        var span = data.Span;
        var lineCount = 0;
        var pos = start;
        while (pos < span.Length)
        {
            var nl = span[pos..].IndexOf((byte)'\n');
            lineCount++;
            if (nl < 0) break;
            pos += nl + 1;
        }
        return new CharacterCursor(data, source, true, lineCount, start);
    }

    public bool IsText => _isText;

    public int Remaining => !_isText ? _data.Length - _position : _remainingLines;

    public int Offset => !_isText
        ? _position
        : _initialLineCount == 0
            ? 0
            : _remainingLines > 0
                ? _nextLineOffset
                : _lastConsumedLineOffset;

    public ResourceFormatException Error(string problem) => new(_source, null, Offset, problem);

    public int Count(int minimumBytes)
    {
        var value = Int32();
        if (value < 0) throw Error($"negative count {value}");
        var room = !_isText ? Remaining / minimumBytes : Remaining;
        if (value > room) throw Error($"count {value} cannot fit in the {Remaining} {(!_isText ? "bytes" : "lines")} left");
        return value;
    }

    public int Int32()
    {
        if (!_isText)
        {
            if (_data.Length - _position < 4) throw Error($"needs 4 bytes, {_data.Length - _position} left");
            var value = BinaryPrimitives.ReadInt32LittleEndian(_data.Span[_position..]);
            _position += 4;
            return value;
        }
        var token = NextTokenSpan();
        if (Utf8Parser.TryParse(token, out int number, out var consumed) && consumed == token.Length)
            return number;
        var text = ResourceReader.Latin1(token);
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number))
            throw Error($"\"{text}\" is not an integer");
        return number;
    }

    public float Float32()
    {
        if (!_isText)
        {
            if (_data.Length - _position < 4) throw Error($"needs 4 bytes, {_data.Length - _position} left");
            var value = BinaryPrimitives.ReadSingleLittleEndian(_data.Span[_position..]);
            _position += 4;
            return value;
        }
        var token = NextTokenSpan();
        if (Utf8Parser.TryParse(token, out float number, out var consumed) && consumed == token.Length && float.IsFinite(number))
            return number;
        var text = ResourceReader.Latin1(token);
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            throw Error($"\"{text}\" is not a number");
        return number;
    }

    public string Text()
    {
        if (!_isText)
        {
            int length = U8();
            if (length == 0xFF)
            {
                var extended = Int32();
                if (extended < 0 || extended > Remaining) throw Error($"string length {extended} runs past the data");
                length = extended;
            }
            if (length > Remaining) throw Error($"string of {length} bytes runs past the data");
            var text = DecodeLatin1Dedup(_data.Span.Slice(_position, length));
            _position += length;
            return text;
        }
        if (_tokenCursor >= 0) throw Error("a string must start a new line");
        if (!TryConsumeLine(out var line, out _)) throw Error("the text ends where a string is expected");
        return DecodeLatin1Dedup(line);
    }

    public string ReadLineStartingWith(string prefix)
    {
        if (!_isText) throw Error("only text has lines");
        while (TryConsumeLine(out var line, out _))
        {
            if (line.Length < prefix.Length) continue;
            var match = true;
            for (var i = 0; i < prefix.Length; i++)
            {
                if ((char)line[i] != prefix[i])
                {
                    match = false;
                    break;
                }
            }
            if (!match) continue;
            _tokenCursor = -1;
            return DecodeLatin1Dedup(line);
        }
        throw Error($"no line starts with \"{prefix}\"");
    }

    public void ExpectEnd()
    {
        if (!_isText)
        {
            for (var index = _position; index < _data.Length; index++)
                if (_data.Span[index] != 0) throw new ResourceFormatException(_source, null, index, $"{_data.Length - index} bytes follow the last section");
            return;
        }
        if (_tokenCursor >= 0) throw Error("numbers follow the last section");
        while (TryConsumeLine(out var line, out var lineOffset))
        {
            for (var i = 0; i < line.Length; i++)
                if (!char.IsWhiteSpace((char)line[i]))
                    throw new ResourceFormatException(_source, null, lineOffset, "text follows the last section");
        }
    }

    private byte U8()
    {
        if (_data.Length - _position < 1) throw Error("needs 1 byte, none left");
        return _data.Span[_position++];
    }

    private bool TryConsumeLine(out ReadOnlySpan<byte> line, out int lineOffset)
    {
        if (_remainingLines <= 0)
        {
            line = default;
            lineOffset = Offset;
            return false;
        }
        lineOffset = _nextLineOffset;
        var span = _data.Span;
        var rel = span[_nextLineOffset..].IndexOf((byte)'\n');
        int contentEnd;
        if (rel < 0)
        {
            contentEnd = _data.Length;
            _nextLineOffset = _data.Length;
        }
        else
        {
            contentEnd = _nextLineOffset + rel;
            _nextLineOffset += rel + 1;
        }
        if (contentEnd > lineOffset && span[contentEnd - 1] == '\r')
            contentEnd--;
        line = span[lineOffset..contentEnd];
        _lastConsumedLineOffset = lineOffset;
        _remainingLines--;
        return true;
    }

    private ReadOnlySpan<byte> NextTokenSpan()
    {
        var span = _data.Span;
        while (_tokenCursor < 0)
        {
            if (!TryConsumeLine(out var lineSpan, out var lineOffset))
                throw Error("the text ends where a number is expected");
            var p = 0;
            while (p < lineSpan.Length && (lineSpan[p] == (byte)' ' || lineSpan[p] == (byte)'\t')) p++;
            if (p == lineSpan.Length)
                throw new ResourceFormatException(_source, null, lineOffset, "a blank line where a number is expected");
            _tokenCursor = lineOffset + p;
            _tokenLineEnd = lineOffset + lineSpan.Length;
        }
        var start = _tokenCursor;
        var end = start;
        while (end < _tokenLineEnd && span[end] != (byte)' ' && span[end] != (byte)'\t') end++;
        var next = end;
        while (next < _tokenLineEnd && (span[next] == (byte)' ' || span[next] == (byte)'\t')) next++;
        _tokenCursor = next < _tokenLineEnd ? next : -1;
        return span[start..end];
    }
}
