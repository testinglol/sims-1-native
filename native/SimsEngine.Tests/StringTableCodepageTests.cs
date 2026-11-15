using SimsEngine.Formats;

namespace SimsEngine.Tests;

/// <summary>Unit tests for A17 non-Latin-1 codepage decoding (Cyrillic, Polish, Hebrew, CJK, Thai) and round-trip preservation.</summary>
public class StringTableCodepageTests
{
    [Fact]
    public void PreservesByteFidelityForCyrillicStrings()
    {
        // Russian (LanguageCode 13, Windows-1251)
        var encoding = ResourceReader.EncodingForLanguage(13);
        var originalText = "Привет, мир!"; // "Hello, world!" in Russian
        var encodedBytes = encoding.GetBytes(originalText);

        var builder = new ChunkBuilder().S16(-3).S16(1)
            .U8(13).Raw(encodedBytes).Raw([(byte)0]).CString("");
        var (file, iff) = builder.InFile("STR#");

        var table = StringTable.Read(file, iff);
        var strings = table.ForLanguage(13);

        Assert.Single(strings);
        Assert.Equal(originalText, strings[0]);
    }

    [Fact]
    public void PreservesByteFidelityForPolishCentralEuropeanStrings()
    {
        // Polish (LanguageCode 16, Windows-1250)
        var encoding = ResourceReader.EncodingForLanguage(16);
        var originalText = "Zażółć gęślą jaźń"; // Standard Polish pangram with diacritics
        var encodedBytes = encoding.GetBytes(originalText);

        var builder = new ChunkBuilder().S16(-3).S16(1)
            .U8(16).Raw(encodedBytes).Raw([(byte)0]).CString("");
        var (file, iff) = builder.InFile("STR#");

        var table = StringTable.Read(file, iff);
        var strings = table.ForLanguage(16);

        Assert.Single(strings);
        Assert.Equal(originalText, strings[0]);
    }

    [Fact]
    public void PreservesByteFidelityForJapaneseShiftJisStrings()
    {
        // Japanese (LanguageCode 15, CP932 / Shift-JIS)
        var encoding = ResourceReader.EncodingForLanguage(15);
        var originalText = "こんにちは"; // "Konnichiwa"
        var encodedBytes = encoding.GetBytes(originalText);

        var builder = new ChunkBuilder().S16(-3).S16(1)
            .U8(15).Raw(encodedBytes).Raw([(byte)0]).CString("");
        var (file, iff) = builder.InFile("STR#");

        var table = StringTable.Read(file, iff);
        var strings = table.ForLanguage(15);

        Assert.Single(strings);
        Assert.Equal(originalText, strings[0]);
    }

    [Fact]
    public void PreservesByteFidelityForTraditionalChineseStrings()
    {
        // Traditional Chinese (LanguageCode 17, CP950 / Big5)
        var encoding = ResourceReader.EncodingForLanguage(17);
        var originalText = "模擬市民"; // "The Sims" (Traditional Chinese canonical title)
        var encodedBytes = encoding.GetBytes(originalText);

        var builder = new ChunkBuilder().S16(-3).S16(1)
            .U8(17).Raw(encodedBytes).Raw([(byte)0]).CString("");
        var (file, iff) = builder.InFile("STR#");

        var table = StringTable.Read(file, iff);
        var strings = table.ForLanguage(17);

        Assert.Single(strings);
        Assert.Equal(originalText, strings[0]);
    }

    [Fact]
    public void PreservesByteFidelityForSimplifiedChineseStrings()
    {
        // Simplified Chinese (LanguageCode 18, CP936 / GB2312)
        var encoding = ResourceReader.EncodingForLanguage(18);
        var originalText = "模拟人生"; // "The Sims" (Simplified Chinese canonical title)
        var encodedBytes = encoding.GetBytes(originalText);

        var builder = new ChunkBuilder().S16(-3).S16(1)
            .U8(18).Raw(encodedBytes).Raw([(byte)0]).CString("");
        var (file, iff) = builder.InFile("STR#");

        var table = StringTable.Read(file, iff);
        var strings = table.ForLanguage(18);

        Assert.Single(strings);
        Assert.Equal(originalText, strings[0]);
    }

    [Fact]
    public void WriteWithValuePreservesMultiByteCharacterEncoding()
    {
        // Japanese (LanguageCode 15)
        var encoding = ResourceReader.EncodingForLanguage(15);
        var initialText = "初期テキスト";
        var updatedText = "更新されたテキスト";

        var builder = new ChunkBuilder().S16(-3).S16(1)
            .U8(15).Raw(encoding.GetBytes(initialText)).Raw([(byte)0]).CString("Comment");
        var (file, iff) = builder.InFile("STR#");

        var table = StringTable.Read(file, iff);
        var rewrittenBytes = table.WriteWithValue(1, updatedText, 15);

        // Read back the rewritten table
        var reloadedIff = new IffFile(ChunkBuilder.File(("STR#", 100, rewrittenBytes)), "reloaded.iff");
        var reloadedChunk = reloadedIff.Find(ResourceType.Parse("STR#"), 100)!;
        var reloadedTable = StringTable.Read(reloadedIff, reloadedChunk);

        var reloadedStrings = reloadedTable.ForLanguage(15);
        Assert.Single(reloadedStrings);
        Assert.Equal(updatedText, reloadedStrings[0]);
    }

    [Fact]
    public void DecodesWindows1252PunctuationAcrossFormats()
    {
        // Bytes 0x92 (right single quote ’), 0x93/0x94 (“ ”), 0x97 (em dash —), 0x99 (trademark ™)
        byte[] cp1252Bytes = [(byte)'B', (byte)'o', (byte)'b', 0x92, (byte)'s', (byte)' ', 0x93, (byte)'T', (byte)'V', 0x94, 0x97, 0x99];
        const string expected = "Bob\u2019s \u201CTV\u201D\u2014\u2122";

        var formatMinus1 = new ChunkBuilder().S16(-1).S16(1).Raw(cp1252Bytes).Raw([(byte)0]).InFile("STR#");
        Assert.Equal(expected, StringTable.Read(formatMinus1.File, formatMinus1.Chunk).ForLanguage(1).Single());

        var formatMinus3Lang0 = new ChunkBuilder().S16(-3).S16(1).U8(0).Raw(cp1252Bytes).Raw([(byte)0]).CString("").InFile("STR#");
        Assert.Equal(expected, StringTable.Read(formatMinus3Lang0.File, formatMinus3Lang0.Chunk).ForLanguage(1).Single());

        var formatCount = new ChunkBuilder().S16(1).U8((byte)cp1252Bytes.Length).Raw(cp1252Bytes).InFile("STR#");
        Assert.Equal(expected, StringTable.Read(formatCount.File, formatCount.Chunk).ForLanguage(1).Single());
    }
}
