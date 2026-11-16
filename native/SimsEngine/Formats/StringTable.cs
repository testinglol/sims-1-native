namespace SimsEngine.Formats;

/// <summary>Represents an entry in a string table chunk, with language code 1 denoting English and 0
/// denoting language-neutral or default.</summary>
public sealed record StringEntry(string Value, string Comment, byte Language);

/// <summary>Parses string tables including STR#, CTSS, and TTAs chunks. Supports format variants for
/// strings only (-1), string and comment pairs (-2), multilingual entries with language codes (-3),
/// extended multilingual sets (-4), and legacy Pascal strings with non-negative format codes.</summary>
public sealed class StringTable
{
    public short Format { get; }
    public IReadOnlyList<StringEntry> Entries { get; }

    public bool ReadBySimsExe => Format != -4;

    /// <summary>Indicates whether a length-prefixed string table declares more entries than the chunk data
    /// actually contains.</summary>
    public bool Truncated { get; }

    private StringTable(short format, IReadOnlyList<StringEntry> entries, bool truncated)
    {
        Format = format;
        Entries = entries;
        Truncated = truncated;
    }

    public static StringTable Read(IffFile file, IffChunk chunk)
    {
        var reader = file.Reader(chunk);
        var entries = new List<StringEntry>();
        if (reader.Remaining < 2) return new StringTable(0, entries, false);
        var format = reader.S16();
        var truncated = false;
        switch (format)
        {
            case -1:
                for (int index = 0, count = reader.S16(); index < count; index++)
                    entries.Add(new StringEntry(reader.CString(1), "", 0));
                break;
            case -2:
                for (int index = 0, count = reader.S16(); index < count; index++)
                    entries.Add(new StringEntry(reader.CString(1), reader.CString(1), 0));
                break;
            case -3:
                for (int index = 0, count = reader.S16(); index < count; index++)
                {
                    var language = reader.U8();
                    var decodeLanguage = language == 0 ? (byte)1 : language;
                    entries.Add(new StringEntry(reader.CString(decodeLanguage), reader.CString(decodeLanguage), language));
                }
                break;
            case -4:
                // Format -4: language sets containing variable-length UTF-8 string pairs.
                for (int set = 0, sets = reader.U8(); set < sets; set++)
                    for (int index = 0, count = reader.U16(); index < count; index++)
                    {
                        reader.U8();
                        var value = reader.VariableLengthString();
                        var comment = reader.VariableLengthString();
                        entries.Add(new StringEntry(value, comment, (byte)(set + 1)));
                    }
                break;
            default:
                if (format < 0) break;
                for (var index = 0; index < format; index++)
                {
                    var length = reader.Remaining > 0 ? reader.U8() : -1;
                    if (length < 0 || length > reader.Remaining)
                    {
                        truncated = true;
                        break;
                    }
                    var bytes = reader.Bytes(length).ToArray();
                    for (var at = 0; at < bytes.Length; at++)
                        bytes[at] = bytes[at] switch { 0xA1 => (byte)'o', 0xD5 => (byte)'\'', var other => other };
                    entries.Add(new StringEntry(ResourceReader.Decode(bytes, 1), "", 0));
                }
                break;
        }
        return new StringTable(format, entries, truncated);
    }

    /// <summary>Returns all strings matching <paramref name="language"/>, falling back to English (1) if no
    /// strings exist for that language.</summary>
    public IReadOnlyList<string> ForLanguage(byte language)
    {
        if (Format != -3) return Entries.Select(entry => entry.Value).ToList();
        var chosen = Pick(language);
        return chosen.Count == 0 && language != 1 ? PickEnglishFallback() : chosen;
    }

    public string GetString(int index, byte language)
    {
        if (index < 0) return string.Empty;
        var list = ForLanguage(language);
        if (index < list.Count && !string.IsNullOrEmpty(list[index])) return list[index];
        if (language != 1)
        {
            var english = ForLanguage(1);
            if (index < english.Count && !string.IsNullOrEmpty(english[index])) return english[index];
        }
        return string.Empty;
    }

    /// <summary>Updates the 1-based string entry for <paramref name="language"/> and serializes the table
    /// in format -3.</summary>
    public byte[] WriteWithValue(int number, string value, byte language)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        var entries = Entries.ToList();
        var target = Entries.Any(entry => entry.Language == language) ? language : (byte)1;
        var seen = 0;
        var at = entries.FindIndex(entry => entry.Language == target && ++seen == number);
        if (at < 0) throw new ArgumentOutOfRangeException(nameof(number), "The table has no such string.");
        entries[at] = entries[at] with { Value = value };
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((short)-3);
        writer.Write(checked((short)entries.Count));
        foreach (var entry in entries)
        {
            var encoding = ResourceReader.EncodingForLanguage(entry.Language);
            writer.Write(entry.Language);
            writer.Write(encoding.GetBytes(entry.Value));
            writer.Write((byte)0);
            writer.Write(encoding.GetBytes(entry.Comment));
            writer.Write((byte)0);
        }
        writer.Flush();
        return output.ToArray();
    }

    /// <summary>Replaces or extends string entries for <paramref name="language"/> while preserving
    /// comments and other languages in format -3.</summary>
    public byte[] WriteLanguageValues(IReadOnlyList<string> values, byte language)
    {
        ArgumentNullException.ThrowIfNull(values);
        var entries = Entries.ToList();
        if (Format != -3)
        {
            var normalizedLang = language == 0 ? (byte)1 : language;
            var converted = new List<StringEntry>(Math.Max(entries.Count, values.Count));
            for (var i = 0; i < values.Count; i++)
            {
                var comment = i < entries.Count ? entries[i].Comment : string.Empty;
                converted.Add(new StringEntry(values[i] ?? string.Empty, comment, normalizedLang));
            }
            return EncodeFormatMinusThree(converted);
        }

        var target = entries.Any(entry => entry.Language == language)
            ? language
            : entries.Any(entry => entry.Language == 1)
                ? (byte)1
                : entries.Any(entry => entry.Language == 0)
                    ? (byte)0
                    : (language == 0 ? (byte)1 : language);

        var indices = new List<int>();
        for (var i = 0; i < entries.Count; i++)
            if (entries[i].Language == target)
                indices.Add(i);

        var common = Math.Min(indices.Count, values.Count);
        for (var i = 0; i < common; i++)
            entries[indices[i]] = entries[indices[i]] with { Value = values[i] ?? string.Empty };

        if (values.Count > indices.Count)
        {
            var insertAt = indices.Count > 0 ? indices[^1] + 1 : entries.Count;
            var emitLanguage = target == 0 ? (byte)1 : target;
            for (var i = indices.Count; i < values.Count; i++)
                entries.Insert(insertAt++, new StringEntry(values[i] ?? string.Empty, string.Empty, emitLanguage));
        }

        return EncodeFormatMinusThree(entries);
    }

    public static byte[] CreateLanguageValues(IReadOnlyList<string> values, byte language = 1)
    {
        ArgumentNullException.ThrowIfNull(values);
        var emitLanguage = language == 0 ? (byte)1 : language;
        var entries = values.Select(v => new StringEntry(v ?? string.Empty, string.Empty, emitLanguage)).ToList();
        return EncodeFormatMinusThree(entries);
    }

    private static byte[] EncodeFormatMinusThree(IReadOnlyList<StringEntry> entries)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((short)-3);
        writer.Write(checked((short)entries.Count));
        foreach (var entry in entries)
        {
            var encodeLang = entry.Language == 0 ? (byte)1 : entry.Language;
            var encoding = ResourceReader.EncodingForLanguage(encodeLang);
            writer.Write(entry.Language);
            writer.Write(encoding.GetBytes(entry.Value));
            writer.Write((byte)0);
            writer.Write(encoding.GetBytes(entry.Comment));
            writer.Write((byte)0);
        }
        writer.Flush();
        return output.ToArray();
    }

    private List<string> Pick(byte language)
    {
        var result = new List<string>();
        var previous = -1;
        var number = 0;
        foreach (var entry in Entries)
        {
            var entryLanguage = entry.Language == 0 ? language : entry.Language;
            if (entryLanguage != previous)
            {
                number = 0;
                previous = entryLanguage;
            }
            if (entryLanguage != language) continue;
            // Subsequent runs of the same language overwrite earlier entries by index.
            if (number < result.Count) result[number] = entry.Value;
            else result.Add(entry.Value);
            number++;
        }
        return result;
    }

    private List<string> PickEnglishFallback()
    {
        var result = new List<string>();
        var previous = -1;
        var number = 0;
        foreach (var entry in Entries)
        {
            var entryLanguage = entry.Language == 0 ? (byte)1 : entry.Language;
            if (entryLanguage != previous)
            {
                number = 0;
                previous = entryLanguage;
            }
            if (entryLanguage != 1) continue;
            if (number < result.Count) result[number] = entry.Value;
            else result.Add(entry.Value);
            number++;
        }
        return result;
    }
}
