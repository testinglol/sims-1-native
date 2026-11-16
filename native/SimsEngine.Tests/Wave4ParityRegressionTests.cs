using System.Text;
using System.Text.Json;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsSimulation;

namespace SimsEngine.Tests;

public sealed class Wave4ParityRegressionTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    [Fact]
    public void WaterAndFloorCatalog_ResolvesHighPaletteIdsAbove1000InsteadOfFallingBackToPaltZero()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var sessionDir = Path.Combine(Path.GetTempPath(), "sims-wave4-floor-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(sessionDir);
            var floorsPath = Path.Combine(gameDir, "GameData", "floors.iff");
            if (!File.Exists(floorsPath)) return;

            var floors = IffFile.Open(floorsPath);
            var poolSprChunk = floors.Find(ResourceType.Spr2, 0x420);
            Assert.NotNull(poolSprChunk);
            var poolSprite = DepthSprite.Read(floors, poolSprChunk!);
            Assert.True(poolSprite.PaletteId >= 1000, $"Expected pool SPR2 0x420 paletteId >= 1000, got {poolSprite.PaletteId}");
            var expectedPaletteChunk = floors.Find(ResourceType.Palt, checked((ushort)poolSprite.PaletteId));
            Assert.NotNull(expectedPaletteChunk);
            var expectedPalette = Palette.Read(floors, expectedPaletteChunk!).Colors;

            var waterPageJson = JsonSerializer.SerializeToElement(
                FloorCatalogPage.WaterPage(gameDir, new ObjectRegistry(), 1, 0, 16, sessionDir));
            var poolItem = waterPageJson.GetProperty("items").EnumerateArray()
                .First(item => item.GetProperty("id").GetInt32() == 65535);
            var previewFile = poolItem.GetProperty("preview").GetProperty("file").GetString();
            Assert.False(string.IsNullOrEmpty(previewFile));

            var bmpBytes = File.ReadAllBytes(Path.Combine(sessionDir, previewFile!));
            var frame = poolSprite.Frames[0];
            var samplePixel = Enumerable.Range(0, frame.Width * frame.Height)
                .First(i => frame.Alpha[i] > 0 && frame.Indices[i] < expectedPalette.Count);
            var expectedColor = expectedPalette[frame.Indices[samplePixel]];
            var bmpOffset = 54 + samplePixel * 4;
            Assert.Equal(expectedColor.Blue, bmpBytes[bmpOffset]);
            Assert.Equal(expectedColor.Green, bmpBytes[bmpOffset + 1]);
            Assert.Equal(expectedColor.Red, bmpBytes[bmpOffset + 2]);
        }
        finally
        {
            if (Directory.Exists(sessionDir)) Directory.Delete(sessionDir, recursive: true);
        }
    }

    [Fact]
    public void BuildToolsAndRoofs_SingleExportClearsDoorWindowCutawayPortalsAndShowsAllStoriesOnRoofs()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null || !File.Exists(Path.Combine(gameDir, "UserData", "Houses", "House05.iff")))
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-wave4-server-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = Path.Combine(tempRoot, "runtime", "profiles", "test");
            var userData = Path.Combine(profile, "UserData");
            Directory.CreateDirectory(Path.Combine(userData, "Houses"));
            File.Copy(Path.Combine(gameDir, "UserData", "Neighborhood.iff"), Path.Combine(userData, "Neighborhood.iff"));
            File.Copy(Path.Combine(gameDir, "UserData", "Houses", "House05.iff"), Path.Combine(userData, "Houses", "House05.iff"));

            var session = Path.Combine(tempRoot, "session");
            Directory.CreateDirectory(session);
            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 5, writer);

            JsonElement[] DrainEvents()
            {
                writer.Flush();
                var text = Encoding.UTF8.GetString(outputStream.GetBuffer(), 0, (int)outputStream.Length);
                outputStream.SetLength(0);
                outputStream.Position = 0;
                var lines = text
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return lines.Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
            }

            JsonDocument LoadLatestScene(JsonElement[] events)
            {
                var sceneEvent = events.Last(e => e.TryGetProperty("scene", out _));
                var scenePath = sceneEvent.GetProperty("scene").GetString()!;
                return JsonDocument.Parse(File.ReadAllText(scenePath));
            }

            Assert.True(server.Handle("""{"id":"1","op":"walls","mode":"down"}"""));
            Assert.True(server.Handle("""{"id":"2","op":"view","editing":true,"doorWindowTool":false}"""));
            var initialEvents = DrainEvents();
            using var baselineFloor1Scene = LoadLatestScene(initialEvents);

            int PortalObjectSpriteCount(JsonDocument sceneDoc) =>
                sceneDoc.RootElement.GetProperty("sprites").EnumerateArray().Count(s => s.GetProperty("objectID").GetInt32() > 0);

            Assert.True(server.Handle("""{"id":"3","op":"catalogPage","category":9,"offset":0,"count":4}"""));
            var windowToolEvents = DrainEvents();
            using var windowToolScene = LoadLatestScene(windowToolEvents);
            Assert.True(PortalObjectSpriteCount(windowToolScene) > PortalObjectSpriteCount(baselineFloor1Scene),
                "Expected Window Tool to expose lowered HideForCutaway door/window portals on floor 1.");

            Assert.True(server.Handle("""{"id":"4","op":"level","value":2,"roofs":false,"doorWindowTool":false}"""));
            var stairLevelEvents = DrainEvents();
            Assert.Equal(1, stairLevelEvents.Count(e => e.TryGetProperty("scene", out _)));
            using var stairScene = LoadLatestScene(stairLevelEvents);

            Assert.True(server.Handle("""{"id":"5","op":"catalogPage","category":10,"offset":0,"count":4}"""));
            var stairCatalogEvents = DrainEvents();
            Assert.Equal(0, stairCatalogEvents.Count(e => e.TryGetProperty("scene", out _)));

            Assert.True(server.Handle("""{"id":"6","op":"catalogPage","category":8,"offset":0,"count":4}"""));
            var doorToolEvents = DrainEvents();
            using var doorToolScene = LoadLatestScene(doorToolEvents);
            Assert.True(PortalObjectSpriteCount(doorToolScene) > PortalObjectSpriteCount(stairScene),
                "Expected Stair Tool single export to keep lowered 2nd-story portals hidden until Door Tool is selected.");

            Assert.True(server.Handle("""{"id":"7","op":"view","editing":true,"doorWindowTool":false}"""));
            var handToolEvents = DrainEvents();
            Assert.Equal(1, handToolEvents.Count(e => e.TryGetProperty("scene", out _)));
            using var handToolScene = LoadLatestScene(handToolEvents);

            Assert.Equal(PortalObjectSpriteCount(stairScene), PortalObjectSpriteCount(handToolScene));

            Assert.True(server.Handle("""{"id":"8","op":"level","value":1,"roofs":false}"""));
            _ = DrainEvents();
            Assert.True(server.Handle("""{"id":"9","op":"roofs","visible":true,"level":2,"doorWindowTool":false}"""));
            var roofEvents = DrainEvents();
            Assert.Equal(1, roofEvents.Count(e => e.TryGetProperty("scene", out _)));
            using var roofScene = LoadLatestScene(roofEvents);
            Assert.True(roofScene.RootElement.GetProperty("roofsVisible").GetBoolean());
            Assert.Equal(2, roofScene.RootElement.GetProperty("viewLevel").GetInt32());
            Assert.Contains(roofScene.RootElement.GetProperty("meshes").EnumerateArray(),
                m => m.TryGetProperty("roof", out var r) && r.ValueKind == JsonValueKind.Object && m.GetProperty("level").GetInt32() == 2);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }
}
