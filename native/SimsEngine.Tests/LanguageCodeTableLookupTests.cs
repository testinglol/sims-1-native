using SimsEngine.Formats;

namespace SimsEngine.Tests;

/// <summary>Unit tests for A17 exhaustive LanguageCode 1..20 table lookup, English fallback, empty fallback, and repeated runs.</summary>
public class LanguageCodeTableLookupTests
{
    private static StringTable BuildAllTwentyLanguagesTable()
    {
        // Construct a format -3 STR# chunk with 20 entries, one for each LanguageCode 1..20
        var builder = new ChunkBuilder().S16(-3).S16(20);
        for (byte code = 1; code <= 20; code++)
        {
            builder.U8(code).CString($"Text_Lang_{code}").CString($"Comment_{code}");
        }
        var (file, iff) = builder.InFile("STR#");
        return StringTable.Read(file, iff);
    }

    [Theory]
    [InlineData(1, "Text_Lang_1")]   // US English
    [InlineData(2, "Text_Lang_2")]   // UK English
    [InlineData(3, "Text_Lang_3")]   // French
    [InlineData(4, "Text_Lang_4")]   // German
    [InlineData(5, "Text_Lang_5")]   // Italian
    [InlineData(6, "Text_Lang_6")]   // Spanish
    [InlineData(7, "Text_Lang_7")]   // Dutch
    [InlineData(8, "Text_Lang_8")]   // Danish
    [InlineData(9, "Text_Lang_9")]   // Swedish
    [InlineData(10, "Text_Lang_10")] // Norwegian
    [InlineData(11, "Text_Lang_11")] // Finnish
    [InlineData(12, "Text_Lang_12")] // Hebrew
    [InlineData(13, "Text_Lang_13")] // Russian
    [InlineData(14, "Text_Lang_14")] // Portuguese
    [InlineData(15, "Text_Lang_15")] // Japanese
    [InlineData(16, "Text_Lang_16")] // Polish
    [InlineData(17, "Text_Lang_17")] // Traditional Chinese
    [InlineData(18, "Text_Lang_18")] // Simplified Chinese
    [InlineData(19, "Text_Lang_19")] // Thai
    [InlineData(20, "Text_Lang_20")] // Korean
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
        // Table containing only LanguageCode 1 (US English)
        var chunk = new ChunkBuilder().S16(-3).S16(1)
            .U8(1).CString("English_Greeting").CString("");
        var (file, iff) = chunk.InFile("STR#");
        var table = StringTable.Read(file, iff);

        // LanguageCode 15 (Japanese) not in table -> falls back to English
        var strings = table.ForLanguage(15);
        Assert.Single(strings);
        Assert.Equal("English_Greeting", strings[0]);
        Assert.Equal("English_Greeting", table.GetString(0, 15));
    }

    [Fact]
    public void MissingStringIndexFallsBackToEnglishThenEmpty()
    {
        // English has 2 strings, Spanish has 1 string
        var chunk = new ChunkBuilder().S16(-3).S16(3)
            .U8(1).CString("Hello").CString("")
            .U8(1).CString("Goodbye").CString("")
            .U8(6).CString("Hola").CString("");
        var (file, iff) = chunk.InFile("STR#");
        var table = StringTable.Read(file, iff);

        // String 0 in Spanish
        Assert.Equal("Hola", table.GetString(0, 6));

        // String 1 missing in Spanish -> falls back to English "Goodbye"
        Assert.Equal("Goodbye", table.GetString(1, 6));

        // String 2 missing in both -> returns empty string ""
        Assert.Equal(string.Empty, table.GetString(2, 6));
    }

    [Fact]
    public void RepeatedLanguageRunsOverwriteOpeningStrings()
    {
        // Sims.exe FUN_0046ffa4: when the same language code appears in a second run,
        // its strings overwrite the opening indices of that language.
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
        // Stored language byte 0 means "the game's current language"
        var chunk = new ChunkBuilder().S16(-3).S16(1)
            .U8(0).CString("CurrentLangText").CString("");
        var (file, iff) = chunk.InFile("STR#");
        var table = StringTable.Read(file, iff);

        // Requesting language 3 (French) binds language 0 to French
        var french = table.ForLanguage(3);
        Assert.Single(french);
        Assert.Equal("CurrentLangText", french[0]);
    }
}
