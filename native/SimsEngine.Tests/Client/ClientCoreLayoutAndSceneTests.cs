using System.Numerics;
using System.Text;
using System.Text.Json;
using SimsSDL.Core;

namespace SimsEngine.Tests.Client;

public sealed class ClientCoreLayoutAndSceneTests
{
    [Fact]
    public void OriginalGameplayAndScalingLayouts_MatchExactBinaryMetrics()
    {
        var need = new CGRect(100, 200, 120, 16);
        Assert.Equal(new CGPoint(181, 212), OriginalGameplayLayout.MotiveArrowOrigin(need, rising: true, index: 0));
        Assert.Equal(new CGPoint(197, 212), OriginalGameplayLayout.MotiveArrowOrigin(need, rising: true, index: 4));
        Assert.Equal(new CGPoint(116, 212), OriginalGameplayLayout.MotiveArrowOrigin(need, rising: false, index: 0));
        Assert.Equal(new CGPoint(100, 212), OriginalGameplayLayout.MotiveArrowOrigin(need, rising: false, index: 4));

        Assert.True(OriginalGameplayLayout.MotiveArrowMaskPixel(rising: true, x: 0, y: 0));
        Assert.False(OriginalGameplayLayout.MotiveArrowMaskPixel(rising: true, x: 1, y: 0));
        Assert.True(OriginalGameplayLayout.MotiveArrowMaskPixel(rising: true, x: 2, y: 2));
        Assert.True(OriginalGameplayLayout.MotiveArrowMaskPixel(rising: false, x: 2, y: 0));
        Assert.False(OriginalGameplayLayout.MotiveArrowMaskPixel(rising: false, x: 1, y: 0));

        Assert.Equal(2.0f, OriginalUIScalingLayout.AutomaticUIScale(1600, 1200, 2.0f));
        Assert.Equal(1.15f, OriginalUIScalingLayout.AutomaticUIScale(1280, 960, 1.0f));
        Assert.Equal(0.5, OriginalUIScalingLayout.FullscreenOverlayScale(new CGSize(400, 300), 1.0f), 3);

        var popupOrigin = OriginalUIScalingLayout.LivePopupOriginX(anchorMidX: 300, canvasWidth: 1280);
        Assert.InRange(popupOrigin, 220, 1280 - 559 - 8);

        // Catalog popup anchors over the hovered thumbnail's horizontal center, clamped within [catalogLeft, canvasWidth - popupWidth - rightMargin].
        Assert.Equal(220, OriginalUIScalingLayout.CatalogPopupOriginX(1600, anchorMidX: 437.5));
        Assert.Equal(620, OriginalUIScalingLayout.CatalogPopupOriginX(1600, anchorMidX: 900.0));
        Assert.Equal(1600 - 559 - 8, OriginalUIScalingLayout.CatalogPopupOriginX(1600, anchorMidX: 1550.0));
        Assert.Equal(220, OriginalUIScalingLayout.CatalogPopupOriginX(800, anchorMidX: 437.5));
        Assert.Equal(233, OriginalUIScalingLayout.CatalogPopupOriginX(800, anchorMidX: 780.0));

        var liveBtn = OriginalUniversalControl.All.First(c => c.Kind == OriginalUniversalControl.Action.Live);
        var center = new CGPoint(
            liveBtn.Position.X + liveBtn.Size.Width / 2,
            600 - OriginalSpeedControl.PanelHeight + liveBtn.Position.Y + liveBtn.Size.Height / 2);
        var hit = OriginalUniversalControl.Hit(center, new CGSize(800, 600), scale: 1.0);
        Assert.NotNull(hit);
        Assert.Equal(OriginalUniversalControl.Action.Live, hit!.Kind);
    }

    [Fact]
    public void MessageDialogLayout_NearestExclusionLines_AndFontMetricsWhenGamePresent()
    {
        Assert.Equal(0, OriginalMessageDialogLayout.NearestExclusionLines(0, 16));
        Assert.Equal(3, OriginalMessageDialogLayout.NearestExclusionLines(45, 16));

        var gameDir = TestGameLocator.Locate();
        if (gameDir is null)
        {
            return;
        }

        var content = new GameContent(gameDir);
        Assert.False(string.IsNullOrWhiteSpace(content.Language.Name));

        var fontBytes = content.Read("UIGraphics/Fonts/variablesans_12.ffn");
        var font = new SimsBitmapFont(fontBytes);
        var result = OriginalMessageDialogLayout.Solve(new OriginalMessageDialogLayout.Input(
            viewport: new CGSize(800, 600),
            titleText: "Test Dialog",
            titleSize: new CGSize(100, font.LineHeight),
            titleLineHeight: font.LineHeight,
            messageText: "Hello from The Sims Native C# test suite.",
            bodyFont: font,
            buttonLabelWidths: [40, 50]));

        Assert.Equal(2, result.ButtonRects.Count);
        Assert.True(result.Rect.Width >= 200);
        Assert.True(result.Rect.Height >= 80);
    }

    [Fact]
    public void PngCodec_And_Sha256_RoundTripCorrectly()
    {
        byte[] pixels =
        [
            255, 0, 0, 255,   0, 255, 0, 128,
            0, 0, 255, 255,   255, 255, 255, 0,
        ];
        byte[] encoded = PNGEncoder.Encode(pixels, 2, 2);
        var decoded = new PNGImage(encoded, 2, 2);
        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(pixels, decoded.Rgba);

        Assert.Equal(
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            SHA256Digest.Hex(Encoding.UTF8.GetBytes("hello")));
    }

    [Fact]
    public void BuildBrushes_HouseholdCreation_And_SceneOrdering_BehaveDeterministically()
    {
        var rect = FloorBrushRectangle.Create(2, 5, 4, 3);
        Assert.NotNull(rect);
        Assert.Equal(2, rect!.X);
        Assert.Equal(3, rect.Y);
        Assert.Equal(2, rect.Width);
        Assert.Equal(2, rect.Height);
        Assert.Equal(9, rect.TileCount);
        Assert.Equal(new Vector2(0.5f, 0.0f), FloorBrushRectangle.CornerUV(0, 0));

        var buildableJson = JsonDocument.Parse("""{"width":2,"height":2,"buildable":[true,false,false,true],"buildableLevel2":[false,true,true,false]}""").RootElement;
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in buildableJson.EnumerateObject())
            map[p.Name] = p.Value.Clone();
        var area = BuildableArea.Create(map);
        Assert.NotNull(area);
        Assert.True(area!.Contains(new GroundTile(0, 0)));
        Assert.False(area.Contains(new GroundTile(1, 0)));
        var area2 = BuildableArea.Create(map, "buildableLevel2");
        Assert.NotNull(area2);
        Assert.False(area2!.Contains(new GroundTile(0, 0)));
        Assert.True(area2.Contains(new GroundTile(1, 0)));

        var terrainVerts = new[]
        {
            new LotScene.TerrainVertex(100, 100, 0, 0, 0, 0, 0, 0, 0),
            new LotScene.TerrainVertex(132, 116, 0, 0, 0, 0, 0, 0, 0),
            new LotScene.TerrainVertex(100, 132, 0, 0, 0, 0, 0, 0, 0),
            new LotScene.TerrainVertex(100, 132, 0, 0, 0, 0, 0, 0, 0),
            new LotScene.TerrainVertex(68, 116, 0, 0, 0, 0, 0, 0, 0),
            new LotScene.TerrainVertex(100, 100, 0, 0, 0, 0, 0, 0, 0),
        };
        var placementScene = new LotScene
        {
            SchemaVersion = 1,
            House = 1,
            Width = 640,
            Height = 480,
            LotWidth = 1,
            LotHeight = 1,
            ViewLevel = 1,
            Textures = [],
            Sprites = [],
            Limitations = [],
            TerrainVertices = terrainVerts,
        };
        var multiLevelPreview = new PlacementPreview
        {
            SchemaVersion = 1,
            Valid = true,
            Status = "Success",
            Tiles = [new PlacementPreview.Tile(0, 0, 1), new PlacementPreview.Tile(0, 0, 2)],
        };
        var diamonds = multiLevelPreview.TileDiamonds(placementScene);
        Assert.Equal(2, diamonds.Count);
        Assert.Equal(1, diamonds[0].Level);
        Assert.Equal(100f, diamonds[0].Top.Y);
        Assert.Equal(2, diamonds[1].Level);
        Assert.Equal(100f - PlacementPreview.StoryLiftPerLevel, diamonds[1].Top.Y);

        Assert.True(HouseholdCreationRequest.AllowsFamilyNameInput("Goth"));
        Assert.False(HouseholdCreationRequest.AllowsFamilyNameInput("Bad/Name"));
        Assert.True(HouseholdCreationRequest.CanInsert("Bella", "", HouseholdCreationRequest.TextField.FirstName));
        Assert.False(HouseholdCreationRequest.CanInsert("X", new string('A', 25), HouseholdCreationRequest.TextField.FirstName));

        var sceneJson = """
        {
          "schemaVersion": 1,
          "house": 5,
          "width": 640,
          "height": 480,
          "textures": [{"file": "t.rgba", "width": 64, "height": 64}],
          "sprites": [
            {"objectID": 10, "level": 1, "layer": 0, "depth": 5.0, "texture": 0, "x": 0, "y": 0, "width": 64, "height": 64, "flip": false},
            {"objectID": 11, "level": 1, "layer": 0, "depth": 2.0, "texture": 0, "x": 0, "y": 0, "width": 64, "height": 64, "flip": false}
          ],
          "limitations": []
        }
        """;
        var scene = JsonSerializer.Deserialize<LotScene>(sceneJson);
        Assert.NotNull(scene);
        var order = SceneDrawItem.Ordered(scene!);
        Assert.Equal(2, order.Count);
        Assert.Equal(1, order[0].Index);
        Assert.Equal(0, order[1].Index);

        Assert.True(PluginDeclaration.IsSupportedAPIVersion(1));
        Assert.False(PluginDeclaration.IsSupportedAPIVersion(99));
        Assert.Equal(1, InstalledGameLanguage.EnglishUS.Code);
    }

    [Fact]
    public void PhotoAlbum_HandlesMissingDirectorySafely()
    {
        var temp = Path.Combine(Path.GetTempPath(), "sims-album-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var album = new PhotoAlbum(temp);
            Assert.Empty(album.Photos);
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }
}
