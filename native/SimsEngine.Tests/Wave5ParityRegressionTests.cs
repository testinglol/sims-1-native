using System.Numerics;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsSimulation;

namespace SimsEngine.Tests;

public sealed class Wave5ParityRegressionTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    private static (int MinX, int MaxX, int MinY, int MaxY) ForegroundBounds(
        byte[] bmpBytes, int frameWidth, int frameHeight, (byte R, byte G, byte B) background, int border = 0)
    {
        var (_, _, rgba) = BitmapCodec.Read(bmpBytes);
        var fullWidth = BitConverter.ToInt32(bmpBytes, 18);
        int minX = frameWidth, maxX = -1, minY = frameHeight, maxY = -1;
        for (var y = border; y < frameHeight - border; y++)
        for (var x = border; x < frameWidth - border; x++)
        {
            var offset = (y * fullWidth + x) * 4;
            if (rgba[offset] == background.R && rgba[offset + 1] == background.G && rgba[offset + 2] == background.B)
                continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        return (minX, maxX, minY, maxY);
    }

    [Fact]
    public void PetAndHumanHeadPortraits_ExcludePelvisBodyMeshAndUseUprightFixedHeadBoneWindow()
    {
        var gameDir = LocateGameDirectory();
        var housePath = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        var ep5Path = gameDir is null ? null : Path.Combine(gameDir, "ExpansionPack5", "ExpansionPack5.far");
        if (gameDir is null || !File.Exists(housePath) || !File.Exists(ep5Path))
            return;

        var bootstrap = WorldBootstrap.Load(gameDir, housePath!);
        var avatarGeometry = AvatarSceneGeometry.For(gameDir, bootstrap.Registry);
        var camera = new LotCamera(0, 64, 64);

        var bella = Assert.IsType<XPerson>(bootstrap.World.ObjectById(18));
        var bellaMeshes = avatarGeometry.BuildHeadMeshes(bella, 1, 0, (_, _) => 0f, camera);
        Assert.NotEmpty(bellaMeshes);
        Assert.NotNull(bellaMeshes[0].HeadOrigin);

        var bellaFace = SimPortrait.FaceBitmap(bellaMeshes);
        var bellaBounds = ForegroundBounds(bellaFace, 35, 41, (0, 0, 82));
        Assert.InRange(bellaBounds.MinY, 0, 5);
        Assert.InRange(bellaBounds.MaxY, 35, 40);

        bella.AnimatorBodyStringMode = 2;
        bella.PresentationCharacter = bella.SavedCharacter! with
        {
            Parts =
            [
                ("b005cat_ragdoll", "B005Cat_Ragdoll"),
                ("NONE", ""),
                ("NONE", ""),
                ("NONE", ""),
                ("NONE", ""),
            ],
            ExtraParts = [],
        };
        var catMeshes = avatarGeometry.BuildHeadMeshes(bella, 1, 0, (_, _) => 0f, camera);
        Assert.Single(catMeshes);
        Assert.All(catMeshes[0].OnHead, onHead => Assert.True(onHead));

        var catFace = SimPortrait.FaceBitmap(catMeshes);
        var catBounds = ForegroundBounds(catFace, 35, 41, (0, 0, 82));
        Assert.InRange(catBounds.MinY, 4, 15);
        Assert.InRange(catBounds.MaxY, 28, 39);
        Assert.True(catBounds.MaxY - catBounds.MinY < bellaBounds.MaxY - bellaBounds.MinY,
            "Cat head portrait must render at natural MakePersonHeadImage scale rather than stretching to human head height.");

        var templateCharIff = IffFile.Open(Path.Combine(gameDir, "UserData", "Characters", "User00000.iff"));
        var catIff = SimPortrait.WithPortraits(templateCharIff, catMeshes);
        var catThumb = catIff.Find(ResourceType.Parse("BMP_"), 2004);
        Assert.NotNull(catThumb);
        var catThumbBounds = ForegroundBounds(catThumb!.Data.ToArray(), 25, 25, (0, 0, 0), border: 1);
        Assert.InRange(catThumbBounds.MinY, 3, 10);
        Assert.InRange(catThumbBounds.MaxY, 17, 23);

        bella.PresentationCharacter = bella.SavedCharacter! with
        {
            Parts =
            [
                ("b010dog_rott", "B010Dog_Rott"),
                ("NONE", ""),
                ("NONE", ""),
                ("NONE", ""),
                ("NONE", ""),
            ],
            ExtraParts = [],
        };
        var dogMeshes = avatarGeometry.BuildHeadMeshes(bella, 1, 0, (_, _) => 0f, camera);
        Assert.Single(dogMeshes);
        Assert.All(dogMeshes[0].OnHead, onHead => Assert.True(onHead));
        var dogFace = SimPortrait.FaceBitmap(dogMeshes);
        var dogBounds = ForegroundBounds(dogFace, 35, 41, (0, 0, 82));
        Assert.InRange(dogBounds.MinY, 4, 16);
        Assert.InRange(dogBounds.MaxY, 28, 39);
    }
}
