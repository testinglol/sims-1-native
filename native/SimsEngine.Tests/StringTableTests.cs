using SimsEngine.Formats;

namespace SimsEngine.Tests;

public class StringTableTests
{
    [Fact]
    public void LanguageFormatPicksTheCurrentLanguageAndFallsBackToEnglish()
    {
        var chunk = new ChunkBuilder().S16(-3).S16(3)
            .U8(1).CString("Eat").CString("")
            .U8(1).CString("Sleep").CString("")
            .U8(6).CString("Essen").CString("");
        var (file, iff) = chunk.InFile("STR#");
        var table = StringTable.Read(file, iff);
        Assert.Equal(["Eat", "Sleep"], table.ForLanguage(1));
        Assert.Equal(["Essen"], table.ForLanguage(6));
        Assert.Equal(["Eat", "Sleep"], table.ForLanguage(9));
    }

    [Fact]
    public void CountFormatReplacesMacCharacters()
    {
        var chunk = new ChunkBuilder().S16(1).U8(4).Raw([(byte)'d', 0xD5, (byte)'n', 0xA1]);
        var (file, iff) = chunk.InFile("STR#");
        Assert.Equal(["d'no"], StringTable.Read(file, iff).ForLanguage(1));
    }

    [Fact]
    public void CountFormatPastTheDataIsTruncated()
    {
        var chunk = new ChunkBuilder().S16(3).U8(2).Raw("ok"u8).U8(9).Raw("x"u8);
        var (file, iff) = chunk.InFile("STR#");
        var table = StringTable.Read(file, iff);
        Assert.True(table.Truncated);
        Assert.Equal(["ok"], table.ForLanguage(1));
    }

    [Fact]
    public void OnlineFormatIsDecodedButNotReadBySimsExe()
    {
        var chunk = new ChunkBuilder().S16(-4).U8(1).U16(1).U8(0).VarString("Schneemann").VarString("note");
        var (file, iff) = chunk.InFile("STR#");
        var table = StringTable.Read(file, iff);
        Assert.False(table.ReadBySimsExe);
        Assert.Equal(new StringEntry("Schneemann", "note", 1), table.Entries.Single());
    }

    [Fact]
    public void VariableLengthSizeSpansTwoBytes()
    {
        var text = new string('a', 200);
        var chunk = new ChunkBuilder().S16(-4).U8(1).U16(1).U8(0).VarString(text).VarString("");
        Assert.Equal(new byte[] { 0xC8, 0x01 }, chunk.ToArray()[6..8]);
        var (file, iff) = chunk.InFile("STR#");
        Assert.Equal(text, StringTable.Read(file, iff).Entries.Single().Value);
    }
}
