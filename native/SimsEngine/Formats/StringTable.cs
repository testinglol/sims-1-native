namespace SimsEngine.Formats;

/// <summary>One string of a STR#, CTSS or TTAs chunk. <see cref="Language"/> is the stored language byte: 1 is
/// English, 0 means "the game's current language", and formats without languages store 0.</summary>
public sealed record StringEntry(string Value, string Comment, byte Language);

/// <summary>A string table: STR#, and the CTSS (catalog text) and TTAs (interaction names) chunks that share its
/// layout. The first int16 selects the format, as in Sims.exe FUN_0046ffa4:
/// <list type="bullet">
/// <item>-1: int16 count, zero-terminated strings.</item>
/// <item>-2: int16 count, pairs of zero-terminated value and comment.</item>
/// <item>-3: int16 count, entries of a language byte, value and comment. Sims.exe keeps the strings of the
/// current language and numbers them from 1 again whenever the language changes; when the current language has
/// none it loads the English (1) ones.</item>
/// <item>0 or more: the value itself is the count, followed by length-prefixed strings. Sims.exe replaces bytes
/// 0xA1 with 'o' and 0xD5 with '\'' in these (Mac text).</item>
/// <item>-4: not read by Sims.exe (it falls into the count branch with a negative count, so the table is empty).
/// Only The Sims Online objects use it; it is decoded here (layout inferred from those files) so converted
/// objects keep their text, and <see cref="ReadBySimsExe"/> is false.</item>
/// </list></summary>
public sealed class StringTable
{
    public short Format { get; }
    public IReadOnlyList<StringEntry> Entries { get; }

    /// <summary>False for format -4, which Sims.exe reads as an empty table.</summary>
    public bool ReadBySimsExe => Format != -4;

    /// <summary>True when a length-prefixed table declares more strings than its data holds. Sims.exe would read
    /// past the chunk; the strings that fit are kept.</summary>
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
                // Inferred from the TSO object files: a language-set count, then per set a u16 count and entries of a
                // language byte with two UTF-8 strings (The Sims Online text) whose lengths are 7-bit variable-length integers.
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

    /// <summary>The strings the game shows in <paramref name="language"/> (1 is English), numbered from 0, by the
    /// rules of Sims.exe FUN_0046ffa4. Tables without languages return every string.</summary>
    public IReadOnlyList<string> ForLanguage(byte language)
    {
        if (Format != -3) return Entries.Select(entry => entry.Value).ToList();
        var chosen = Pick(language);
        return chosen.Count == 0 && language != 1 ? PickEnglishFallback() : chosen;
    }

    /// <summary>Looks up a string by 0-based index for the requested language, falling back to US English (1)
    /// and then empty string "" if missing.</summary>
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

    /// <summary>Mac StringSetBase::SetString followed by StringSetBase::Save: the value of string
    /// <paramref name="number"/> (counted from 1) in <paramref name="language"/>'s strings, or in the English ones when
    /// that language has none, becomes <paramref name="value"/>, and the whole table is written back as format -3 (each
    /// string a language byte, its value and its comment).</summary>
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

    /// <summary>Replaces or extends the 0-based string sequence for <paramref name="language"/> (falling back to
    /// language 1 when <paramref name="language"/> has no entries) while preserving comments and other languages,
    /// encoding the table in format -3 as Sims.exe FUN_00587bed writes character STR# resources.</summary>
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
            // The original stores each string at its number in the current run, so a second run of the same
            // language overwrites the first one's opening strings.
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
