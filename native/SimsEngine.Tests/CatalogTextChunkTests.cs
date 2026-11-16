using SimsEngine.Formats;

namespace SimsEngine.Tests;

public class CatalogTextChunkTests
{
    [Fact]
    public void CtssChunkDecodesTitleAndDescriptionPerLanguage()
    {
        // In format -3 CTSS chunks, entry index 0 contains the catalog title and entry index 1 contains the
        // catalog description.
        var builder = new ChunkBuilder().S16(-3).S16(4)
            .U8(1).CString("Comfortable Chair").CString("Title")
            .U8(1).CString("A very cozy wooden dining chair.").CString("Description")
            .U8(3).CString("Chaise Confortable").CString("Titre")
            .U8(3).CString("Une chaise de salle a manger tres confortable.").CString("Description");

        var (file, iff) = builder.InFile("CTSS");
        var ctss = StringTable.Read(file, iff);

        var enStrings = ctss.ForLanguage(1);
        Assert.Equal(2, enStrings.Count);
        Assert.Equal("Comfortable Chair", enStrings[0]);
        Assert.Equal("A very cozy wooden dining chair.", enStrings[1]);

        var frStrings = ctss.ForLanguage(3);
        Assert.Equal(2, frStrings.Count);
        Assert.Equal("Chaise Confortable", frStrings[0]);
        Assert.Equal("Une chaise de salle a manger tres confortable.", frStrings[1]);

        var deStrings = ctss.ForLanguage(4);
        Assert.Equal(2, deStrings.Count);
        Assert.Equal("Comfortable Chair", deStrings[0]);
        Assert.Equal("A very cozy wooden dining chair.", deStrings[1]);
    }

    [Fact]
    public void TtasChunkDecodesInteractionMenuStringsPerLanguage()
    {
        var builder = new ChunkBuilder().S16(-3).S16(4)
            .U8(1).CString("Sit").CString("")
            .U8(1).CString("Relax").CString("")
            .U8(6).CString("Sentarse").CString("")
            .U8(6).CString("Relajarse").CString("");

        var (file, iff) = builder.InFile("TTAs");
        var ttas = StringTable.Read(file, iff);

        var en = ttas.ForLanguage(1);
        Assert.Equal(["Sit", "Relax"], en);

        var es = ttas.ForLanguage(6);
        Assert.Equal(["Sentarse", "Relajarse"], es);

        var it = ttas.ForLanguage(5);
        Assert.Equal(["Sit", "Relax"], it);
    }
}
