using System.Text;
using SimsEngine.Content;
using Xunit;

namespace SimsEngine.Tests;

public sealed class CharacterSearchPathTests
{
    private static byte[] Far(params (string Name, string Text)[] entries)
    {
        var data = new MemoryStream();
        var writer = new BinaryWriter(data);
        writer.Write("FAR!byAZ"u8);
        writer.Write(1u);
        writer.Write(0u);
        var offsets = new List<int>();
        foreach (var (_, text) in entries) { offsets.Add((int)data.Position); writer.Write(Encoding.Latin1.GetBytes(text)); }
        var manifest = (int)data.Position;
        writer.Write((uint)entries.Length);
        for (var index = 0; index < entries.Length; index++)
        {
            writer.Write((uint)entries[index].Text.Length);
            writer.Write((uint)entries[index].Text.Length);
            writer.Write((uint)offsets[index]);
            writer.Write((uint)entries[index].Name.Length);
            writer.Write(Encoding.Latin1.GetBytes(entries[index].Name));
        }
        data.Position = 12;
        writer.Write((uint)manifest);
        return data.ToArray();
    }

    private static void Write(string game, string path, string text)
    {
        var full = Path.Combine([game, .. path.Split('\\')]);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    // FUN_00413d0e sets the House Party edition bit from these two files; the pack folders are searched only when it is set.
    private static void MarkHousePartyInstalled(string game)
    {
        Write(game, @"ExpansionShared\Sound\houseparty.hot", "");
        Write(game, @"GameData\Ranger2.iff", "");
    }

    private static string Text(CharacterFile file) => Encoding.Latin1.GetString(file.Read().Span);

    [Fact]
    public void SkinsPathFollowsTheOriginalFolderOrderAndTheFirstFileWins()
    {
        var game = Directory.CreateTempSubdirectory("skins").FullName;
        try
        {
            Write(game, @"GameData\Skins\B001.cmx", "base");
            Write(game, @"GameData\cmx2\B002.cmx", "cmx2");
            Write(game, @"GameData\cmxpreview\B003.cmx", "preview");
            // House Party keeps its folders as entry folders of the pack's .far, and one loose Skins folder.
            MarkHousePartyInstalled(game);
            Directory.CreateDirectory(Path.Combine(game, "ExpansionPack2"));
            File.WriteAllBytes(Path.Combine(game, "ExpansionPack2", "ExpansionPack2.far"),
                Far(("Skins/B001.cmx", "pack"), ("Skins/B004.cmx", "pack far"), ("Animations/A1.cfp", "anim"), ("Other/X.cmx", "other")));
            Write(game, @"ExpansionPack2\Skins\ct-cost.cmx", "loose");

            var path = CharacterSearchPath.Skins(game);
            Assert.Equal([@"GameData\Skins", @"GameData\cmx2", @"GameData\cmxpreview", @"ExpansionPack2\Skins", @"ExpansionPack2\Animations"],
                path.Folders.Select(folder => folder.Folder));
            Assert.Equal("base", Text(path.Find("b001.CMX")!));
            Assert.Equal("pack", Text(Assert.Single(path.Shadowed)));
            Assert.Equal("pack far", Text(path.Find("B004.cmx")!));
            Assert.Equal("loose", Text(path.Find("ct-cost.cmx")!));
            Assert.Null(path.Find("X.cmx"));
            Assert.Equal(5, path.OfExtension(".cmx").Count());
            // The Complete Collection executable always sets edition bit 0x80, so ExpansionShared is on the path.
            Write(game, @"ExpansionShared\Skins\S.cmx", "shared");
            Assert.Equal(@"ExpansionShared\Skins", CharacterSearchPath.Skins(game).Find("S.cmx")!.Folder);
        }
        finally { Directory.Delete(game, true); }
    }

    [Fact]
    public void TexturePathPutsPackAccessoriesBeforeSkinsAndTexturesLast()
    {
        var game = Directory.CreateTempSubdirectory("textures").FullName;
        try
        {
            Write(game, @"GameData\Skins\T.bmp", "skins");
            Write(game, @"GameData\Textures\T.bmp", "textures");
            Write(game, @"GameData\Textures\U.bmp", "textures");
            MarkHousePartyInstalled(game);
            Write(game, @"ExpansionPack2\Accessories\T.bmp", "accessories");
            Write(game, @"ExpansionPack2\Skins\T.bmp", "pack skins");

            var path = CharacterSearchPath.Textures(game);
            Assert.Equal([@"ExpansionPack2\Accessories", @"ExpansionPack2\Skins", @"GameData\Skins", @"GameData\Textures"],
                path.Folders.Select(folder => folder.Folder));
            Assert.Equal("accessories", Text(path.Find("T.bmp")!));
            Assert.Equal(@"GameData\Textures", path.Find("U.bmp")!.Folder);
            Assert.Equal(3, path.Shadowed.Count);
        }
        finally { Directory.Delete(game, true); }
    }
}
