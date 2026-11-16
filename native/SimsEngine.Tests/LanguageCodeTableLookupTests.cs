using SimsEngine.Formats;

namespace SimsEngine.Tests;

public class LanguageCodeTableLookupTests
{
    private static StringTable BuildAllTwentyLanguagesTable()
    {
        var builder = new ChunkBuilder().S16(-3).S16(20);
        for (byte code = 1; code <= 20; code++)
        {
            builder.U8(code).CString($"Text_Lang_{code}").CString($"Comment_{code}");
        }
        var (file, iff) = builder.InFile("STR#");
        return StringTable.Read(file, iff);
    }

    [Theory]
    [InlineData(1, "Text_Lang_1")]
    [InlineData(2, "Text_Lang_2")]
    [InlineData(3, "Text_Lang_3")]
    [InlineData(4, "Text_Lang_4")]
    [InlineData(5, "Text_Lang_5")]
    [InlineData(6, "Text_Lang_6")]
    [InlineData(7, "Text_Lang_7")]
    [InlineData(8, "Text_Lang_8")]
    [InlineData(9, "Text_Lang_9")]
    [InlineData(10, "Text_Lang_10")]
    [InlineData(11, "Text_Lang_11")]
    [InlineData(12, "Text_Lang_12")]
    [InlineData(13, "Text_Lang_13")]
    [InlineData(14, "Text_Lang_14")]
    [InlineData(15, "Text_Lang_15")]
    [InlineData(16, "Text_Lang_16")]
    [InlineData(17, "Text_Lang_17")]
    [InlineData(18, "Text_Lang_18")]
    [InlineData(19, "Text_Lang_19")]
    [InlineData(20, "Text_Lang_20")]
    public void AllTwentyLanguageCodesExtractMatchingStrings(byte languageCode, string expectedText)
    {
        var table = BuildAllTwentyLanguagesTable();
        var strings = table.ForLanguage(languageCode);
        Assert.Single(strings);
        Assert.Equal(expectedText, strings[0]);
        Assert.Equal(expectedText, table.GetString(0, languageCode));
    }

    [Fact]
    public void MissingLanguageFallsBackToEnglishUS()
    {
        var chunk = new ChunkBuilder().S16(-3).S16(1)
            .U8(1).CString("English_Greeting").CString("");
        var (file, iff) = chunk.InFile("STR#");
        var table = StringTable.Read(file, iff);

        var strings = table.ForLanguage(15);
        Assert.Single(strings);
        Assert.Equal("English_Greeting", strings[0]);
        Assert.Equal("English_Greeting", table.GetString(0, 15));
    }

    [Fact]
    public void MissingStringIndexFallsBackToEnglishThenEmpty()
    {
        var chunk = new ChunkBuilder().S16(-3).S16(3)
            .U8(1).CString("Hello").CString("")
            .U8(1).CString("Goodbye").CString("")
            .U8(6).CString("Hola").CString("");
        var (file, iff) = chunk.InFile("STR#");
        var table = StringTable.Read(file, iff);

        Assert.Equal("Hola", table.GetString(0, 6));

        Assert.Equal("Goodbye", table.GetString(1, 6));

        Assert.Equal(string.Empty, table.GetString(2, 6));
    }

    [Fact]
    public void RepeatedLanguageRunsOverwriteOpeningStrings()
    {
        // When a language code appears again in a subsequent string block, its strings overwrite earlier
        // entries starting from index zero.
        var chunk = new ChunkBuilder().S16(-3).S16(4)
            .U8(1).CString("FirstRun_0").CString("")
            .U8(1).CString("FirstRun_1").CString("")
            .U8(6).CString("Spanish_Run").CString("")
            .U8(1).CString("SecondRun_Overwrites_0").CString("");
        var (file, iff) = chunk.InFile("STR#");
        var table = StringTable.Read(file, iff);

        var english = table.ForLanguage(1);
        Assert.Equal(2, english.Count);
        Assert.Equal("SecondRun_Overwrites_0", english[0]);
        Assert.Equal("FirstRun_1", english[1]);
    }

    [Fact]
    public void LanguageCodeZeroResolvesCurrentGameLanguage()
    {
        // Language code 0 in localized string tables represents the currently active language.
        var chunk = new ChunkBuilder().S16(-3).S16(1)
            .U8(0).CString("CurrentLangText").CString("");
        var (file, iff) = chunk.InFile("STR#");
        var table = StringTable.Read(file, iff);

        var french = table.ForLanguage(3);
        Assert.Single(french);
        Assert.Equal("CurrentLangText", french[0]);
    }
}
