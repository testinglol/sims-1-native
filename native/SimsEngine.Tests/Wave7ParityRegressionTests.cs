using System.Text.Json;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSimulation;

namespace SimsEngine.Tests;

public sealed class Wave7ParityRegressionTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    [Fact]
    public void DialogWordsOffset0x100_InitializesToMinusOnePerMakeNewCharacter()
    {
        var words = new DialogWords();
        Assert.Equal(-1, words[0x100]);
    }

    [Fact]
    public void TwoFieldBodyStringQueryAndUpdate_PreserveAppearanceAndBodyTexture()
    {
        var query = InteractionBehaviorActionQueryBuilder.BuildBodyStringQuery("b002fafit_01,BODY=b002fafitlgt_01;comment");
        Assert.Equal("b002fafit_01", query.First);
        Assert.Equal("body=b002fafitlgt_01", query.Second);

        var updated = InteractionBehaviorActionQueryBuilder.BuildSelectedActionUpdate(
            new InteractionBehaviorAction("b003fafit_01", "BODY=b003fafitlgt_02"));
        Assert.Equal("b003fafit_01,BODY=b003fafitlgt_02", updated);
    }

    [Fact]
    public void ExpansionLots95And99_ExportSceneWithoutTerrainPaletteCrashAndUseCloudBackdropAndUnlitSprites()
    {
        var gameDir = LocateGameDirectory();
        var house95Path = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House95.iff");
        var house99Path = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House99.iff");
        if (gameDir is null || !File.Exists(house95Path) || !File.Exists(house99Path))
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-wave7-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var boot95 = WorldBootstrap.Load(gameDir, house95Path!);
            var scene95Path = SceneExporter.Export(boot95.Lot, boot95.World, boot95.Registry, gameDir, tempDir, 95, 1);
            using (var doc95 = JsonDocument.Parse(File.ReadAllBytes(scene95Path)))
            {
                Assert.True(doc95.RootElement.GetProperty("terrainVertices").GetArrayLength() > 0);
            }

            var boot99 = WorldBootstrap.Load(gameDir, house99Path!);
            var scene99Path = SceneExporter.Export(boot99.Lot, boot99.World, boot99.Registry, gameDir, tempDir, 99, 2);
            using var doc99 = JsonDocument.Parse(File.ReadAllBytes(scene99Path));
            var root99 = doc99.RootElement;
            Assert.True(root99.GetProperty("terrainVertices").GetArrayLength() > 0);

            if (root99.TryGetProperty("lighting", out var lighting) && lighting.ValueKind == JsonValueKind.Object)
            {
                var backdrop = lighting.GetProperty("backdrop");
                Assert.Equal(0x14, backdrop.GetProperty("r").GetInt32());
                Assert.Equal(0x14, backdrop.GetProperty("g").GetInt32());
                Assert.Equal(0x8c, backdrop.GetProperty("b").GetInt32());
            }

            foreach (var sprite in root99.GetProperty("sprites").EnumerateArray())
            {
                if (sprite.TryGetProperty("objectID", out _) && sprite.TryGetProperty("room", out var roomProp))
                {
                    Assert.Equal(JsonValueKind.Null, roomProp.ValueKind);
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SingleStoryNightLighting_IncludesNoRoom255AtNightClamp()
    {
        var gameDir = LocateGameDirectory();
        var house07Path = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House07.iff");
        if (gameDir is null || !File.Exists(house07Path))
            return;

        var boot = WorldBootstrap.Load(gameDir, house07Path!);
        boot.World.Clock.Restore(22, 0, 0);
        var roomMap = boot.World.LotRoomMaps.Values.FirstOrDefault();
        var lighting = SceneLighting.Build(boot.World, roomMap, 7);
        Assert.NotNull(lighting);
        Assert.True(lighting!.Rooms.Length > RoomMap.NoRoom,
            "Single-story lot lighting table must include RoomMap.NoRoom (255) so outdoor/no-room sprites do not fall back to 1.0 full-bright at night.");
        Assert.Equal(128, lighting.Rooms[RoomMap.NoRoom].R);
    }

    [Fact]
    public void HostlessWardrobeAndOutfitPicker_EnumeratesSwatchesPreviewsAndCyclesEverydaySuit()
    {
        var gameDir = LocateGameDirectory();
        var house05Path = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(house05Path))
            return;

        var boot = WorldBootstrap.Load(gameDir, house05Path!);
        var bella = Assert.IsType<XPerson>(boot.World.ObjectById(18));
        var outfits = new SimOutfits(gameDir, boot.Registry, boot.World.Language);

        var everydayContext = new SimOutfits.Context(bella, false, 0, "fafit", "lgt");
        var everydayChoices = outfits.Choices(everydayContext);
        Assert.True(everydayChoices.Length > 10, $"Expected >10 everyday adult female light-skin swatches, got {everydayChoices.Length}.");

        var swimContext = new SimOutfits.Context(bella, false, 2, "fafit", "lgt");
        var swimChoices = outfits.Choices(swimContext);
        Assert.NotEmpty(swimChoices);

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-wave7-outfit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            bella[PersonData.CurrentOutfit] = 2;
            var previewObj = outfits.Preview(tempDir, swimContext, 0);
            var previewJson = JsonSerializer.SerializeToElement(previewObj);
            Assert.True(previewJson.GetProperty("meshes").GetInt32() > 0);
            Assert.Equal(2, bella[PersonData.CurrentOutfit]);
            bella[PersonData.CurrentOutfit] = 0;

            var beforeSuit = bella.SavedCharacter?.Parts[0];
            Assert.NotNull(beforeSuit);
            var frame = new StackFrame(bella.Id, new short[8], new short[4]);
            var context = new TreeContext(boot.World, bella, frame);
            Span<byte> operand = [0x10, 0x00, 0, 0, 0, 0, 0, 0];
            var result = GenericSimsCallPrimitive.Execute(context, operand);
            Assert.Equal(PrimitiveResult.True, result);

            var afterSuit = bella.SavedCharacter?.Parts[0];
            Assert.NotNull(afterSuit);
            Assert.NotEqual(beforeSuit, afterSuit);
            var bellaGuid = unchecked((uint)bella.TypeRecord!.Key);
            Assert.Contains(bellaGuid, boot.Registry.DirtyPeopleGuids);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
