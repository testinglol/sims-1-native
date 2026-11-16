using SimsEngine.Formats;

namespace SimsEngine.Tests;

public class StringTableCodepageTests
{
    [Fact]
    public void PreservesByteFidelityForCyrillicStrings()
    {
        var encoding = ResourceReader.EncodingForLanguage(13);
        var originalText = "Привет, мир!";
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
        var encoding = ResourceReader.EncodingForLanguage(16);
        var originalText = "Zażółć gęślą jaźń";
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
        var encoding = ResourceReader.EncodingForLanguage(15);
        var originalText = "こんにちは";
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
        var encoding = ResourceReader.EncodingForLanguage(17);
        var originalText = "模擬市民";
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
        var encoding = ResourceReader.EncodingForLanguage(18);
        var originalText = "模拟人生";
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
        var encoding = ResourceReader.EncodingForLanguage(15);
        var initialText = "初期テキスト";
        var updatedText = "更新されたテキスト";

        var builder = new ChunkBuilder().S16(-3).S16(1)
            .U8(15).Raw(encoding.GetBytes(initialText)).Raw([(byte)0]).CString("Comment");
        var (file, iff) = builder.InFile("STR#");

        var table = StringTable.Read(file, iff);
        var rewrittenBytes = table.WriteWithValue(1, updatedText, 15);

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
