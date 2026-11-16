using System.Text.Json;
using SimsSDL.Client;
using SimsSDL.Core;

namespace SimsEngine.Tests.Client;

[Collection("Neighborhoods")]
public sealed class ClientInstallPacksAndInProcessBridgeTests
{
    [Fact]
    public void SimulationHost_RunsInProcessWithoutChildExecutables()
    {
        Assert.True(SimulationHost.IsInProcess);
        var launch = SimulationHost.Resolve(Directory.GetCurrentDirectory(), "live");
        Assert.False(string.IsNullOrWhiteSpace(launch.Executable));
        Assert.Empty(launch.LeadingArguments);
    }

    [Fact]
    public void InstallLocator_DetectsDevVsStandaloneAndPersistsGamePath()
    {
        var temp = Path.Combine(Path.GetTempPath(), "sims-install-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var devWorkspace = Path.Combine(temp, "repo");
            Directory.CreateDirectory(Path.Combine(devWorkspace, "native", "SimsSDL"));
            File.WriteAllText(Path.Combine(devWorkspace, "native", "SimsSDL", "SimsSDL.csproj"), "<Project />");

            var standaloneWorkspace = Path.Combine(temp, "standalone");
            Directory.CreateDirectory(standaloneWorkspace);

            Assert.False(InstallLocator.IsStandaloneDistribution(devWorkspace));
            Assert.True(InstallLocator.IsStandaloneDistribution(standaloneWorkspace));
            Assert.True(InstallLocator.IsStandaloneDistribution(
                Path.Combine(temp, "The Sims Native.app", "Contents", "Resources", "workspace")));

            Assert.Equal(devWorkspace, InstallLocator.DefaultDataRoot(devWorkspace, standalone: false));
            var standaloneData = InstallLocator.DefaultDataRoot(standaloneWorkspace, standalone: true);
            Assert.Contains("TheSimsNative", standaloneData, StringComparison.Ordinal);

            var fakeGame = Path.Combine(temp, "Game");
            Assert.False(InstallLocator.IsValidGameDirectory(fakeGame));
            Directory.CreateDirectory(Path.Combine(fakeGame, "GameData"));
            Assert.False(InstallLocator.IsValidGameDirectory(fakeGame));
            Directory.CreateDirectory(Path.Combine(fakeGame, "UIGraphics"));
            Assert.True(InstallLocator.IsValidGameDirectory(fakeGame));

            var dataRoot = Path.Combine(temp, "data");
            InstallLocator.SaveInstallRoot(fakeGame, dataRoot);
            Assert.Equal(Path.GetFullPath(fakeGame), InstallLocator.SavedInstallRoot(dataRoot));
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void SessionSweeper_RemovesUnownedOrDeadMountsAndKeepsOwnedOnes()
    {
        var temp = Path.Combine(Path.GetTempPath(), "sims-sweeper-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var deadSession = Path.Combine(temp, "runtime", "sessions", "dead-session");
            var deadMount = Path.Combine(deadSession, "content");
            Directory.CreateDirectory(deadMount);
            File.WriteAllText(Path.Combine(deadMount, "dummy.iff"), "data");
            File.WriteAllText(Path.Combine(deadSession, "simulation.log"), "keep me");
            File.WriteAllText(Path.Combine(deadSession, SessionSweeper.OwnerFile), "9999999");

            var liveSession = Path.Combine(temp, "runtime", "sessions", "live-session");
            var liveMount = Path.Combine(liveSession, "content");
            Directory.CreateDirectory(liveMount);
            File.WriteAllText(Path.Combine(liveMount, "dummy.iff"), "data");
            SessionSweeper.Claim(liveSession);

            var cleaned = SessionSweeper.Sweep(temp);
            Assert.Contains(deadSession, cleaned);
            Assert.False(Directory.Exists(deadMount));
            Assert.True(File.Exists(Path.Combine(deadSession, "simulation.log")));
            Assert.True(Directory.Exists(liveMount));
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void PackTrust_RecordsMatchesExactHashAndRevokesCleanly()
    {
        var temp = Path.Combine(Path.GetTempPath(), "sims-trust-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            var trust = new PluginTrustStore(temp);
            var report = new PackPluginReport(
                PackID: "example.pack",
                Version: "1.0.0",
                Sha256: "abc123",
                Assembly: "plugin/Example.dll",
                EntryType: "Example.Plugin",
                ApiVersion: 1,
                StateVersion: 1);

            Assert.False(trust.IsTrusted(report));
            trust.Trust(report);

            var reloaded = new PluginTrustStore(temp);
            Assert.True(reloaded.IsTrusted(report));
            Assert.False(reloaded.IsTrusted(report with { Sha256 = "different" }));
            Assert.True(reloaded.Revoke(report.PackID));
            Assert.False(reloaded.IsTrusted(report));
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task InProcessSimulationBridge_StartsDaemonExportsManifestAndClosesCleanlyWhenGamePresent()
    {
        var gameDir = TestGameLocator.Locate();
        if (gameDir is null)
        {
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), "sims-bridge-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var content = new GameContent(gameDir);
            var bridge = new SimulationBridge(temp, dataRoot: temp, profileId: "test", neighborhood: 1);
            var helloTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var responseTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? pendingId = null;
            bridge.OnEvent = ev =>
            {
                if (!ev.TryGetValue("type", out var kind) || kind.ValueKind != JsonValueKind.String)
                    return;

                var typeName = kind.GetString();
                if (typeName == "hello")
                {
                    helloTcs.TrySetResult(true);
                }
                else if (typeName == "response" &&
                         pendingId is not null &&
                         ev.TryGetValue("id", out var idEl) && idEl.ValueKind == JsonValueKind.String &&
                         idEl.GetString() == pendingId)
                {
                    var ok = ev.TryGetValue("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                    responseTcs.TrySetResult(ok);
                }
            };

            MainThreadDispatcher.InitializeMainThread();
            await bridge.StartDaemonAsync(content, nativeEngine: true);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!helloTcs.Task.IsCompleted && DateTime.UtcNow < deadline)
            {
                MainThreadDispatcher.Drain();
                await Task.Delay(25);
            }
            MainThreadDispatcher.Drain();
            Assert.True(helloTcs.Task.IsCompleted, "In-process SimulationBridge did not emit hello event in time.");

            pendingId = bridge.Send("audio", new Dictionary<string, object?> { ["enabled"] = false });
            while (!responseTcs.Task.IsCompleted && DateTime.UtcNow < deadline)
            {
                MainThreadDispatcher.Drain();
                await Task.Delay(25);
            }
            MainThreadDispatcher.Drain();
            Assert.True(responseTcs.Task.IsCompleted && await responseTcs.Task, "In-process SimulationBridge did not acknowledge audio command.");

            await bridge.CloseAsync(save: false, timeout: TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void InMemorySessionAssets_LoadsLotSceneWithoutDiskFilesAndReclaimsOnDisable()
    {
        var session = Path.Combine(Path.GetTempPath(), "sims-inmem-test-" + Guid.NewGuid().ToString("N"));
        var sceneDir = Path.Combine(session, "scene");
        var texPath = Path.Combine(sceneDir, "sprite-test.rgba");
        var scene1Path = Path.Combine(sceneDir, "scene-1.json");
        var scene5Path = Path.Combine(sceneDir, "scene-5.json");
        try
        {
            SimsSimulation.InMemorySessionAssets.EnableRoot(session);
            Assert.True(SimsSimulation.InMemorySessionAssets.IsInMemoryPath(sceneDir));

            byte[] rgba = [10, 20, 30, 255];
            SimsSimulation.InMemorySessionAssets.StoreFile(texPath, rgba);
            SimsSimulation.InMemorySessionAssets.StoreFile(scene1Path, "{}"u8.ToArray());

            var sceneJson = """
            {
              "schemaVersion": 1,
              "house": 5,
              "width": 640,
              "height": 480,
              "textures": [{"file": "sprite-test.rgba", "width": 1, "height": 1}],
              "sprites": [
                {"objectID": 10, "level": 1, "layer": 0, "depth": 1.0, "texture": 0, "x": 0, "y": 0, "width": 1, "height": 1, "flip": false}
              ],
              "limitations": []
            }
            """u8.ToArray();
            SimsSimulation.InMemorySessionAssets.StoreFile(scene5Path, sceneJson);

            SimsSimulation.InMemorySessionAssets.PruneOldScenes(sceneDir, keepFromRevision: 3);
            Assert.False(SimsSimulation.InMemorySessionAssets.TryReadBytes(scene1Path, out _));

            var (loadedScene, loadedPixels) = LotScene.Load(scene5Path);
            Assert.Equal(5, loadedScene.House);
            Assert.Single(loadedPixels);
            Assert.Equal(rgba, loadedPixels[0]);
            Assert.False(Directory.Exists(session));
        }
        finally
        {
            SimsSimulation.InMemorySessionAssets.DisableAndClearRoot(session);
            Assert.False(SimsSimulation.InMemorySessionAssets.IsInMemoryPath(sceneDir));
            Assert.False(SimsSimulation.InMemorySessionAssets.TryReadBytes(scene5Path, out _));
        }
    }

    [Fact]
    public void InMemorySessionAssets_StoreSceneLoadsPayloadDirectlyWithLazyJson_AndTrustedAnimationUnpacksDirectVertices()
    {
        var session = Path.Combine(Path.GetTempPath(), "sims-direct-scene-test-" + Guid.NewGuid().ToString("N"));
        var sceneDir = Path.Combine(session, "scene");
        var texPath = Path.Combine(sceneDir, "sprite-test.rgba");
        var scenePath = Path.Combine(sceneDir, "scene-1.json");
        try
        {
            SimsSimulation.InMemorySessionAssets.EnableRoot(session);
            byte[] rgba = [10, 20, 30, 255];
            SimsSimulation.InMemorySessionAssets.StoreFile(texPath, rgba);

            var terrain = Enumerable.Repeat(new SimsSimulation.SceneExporter.TerrainVertex(16, 16, 128, 128, 128, 0, 0, 0, 0), 6).ToArray();
            var exported = new SimsSimulation.SceneExporter.ExportedLotScene(
                House: 5,
                Width: 640,
                Height: 480,
                LotSize: 1,
                FloorDiagonals: [0],
                TerrainHeights: [0],
                CameraOrientation: 0,
                OriginX: 0,
                OriginY: 0,
                WallMode: "down",
                RoofsVisible: false,
                ViewLevel: 1,
                Stories: 2,
                Lighting: null,
                Textures: [new SimsSimulation.SceneExporter.Texture("sprite-test.rgba", 1, 1, "rgba")],
                Sprites: [new SimsSimulation.SceneExporter.SceneSprite(0, 1, 2, 1, 1, false, 0, 1.0f, 0)],
                SoundSources: [],
                Terrain: terrain,
                PlacementLift: 0f,
                Meshes: [],
                CensorRects: [],
                Limitations: [],
                MissingResources: []);

            byte[] utf8Json = """
            {
              "schemaVersion": 1,
              "house": 5,
              "width": 640,
              "height": 480,
              "textures": [{"file": "sprite-test.rgba", "width": 1, "height": 1}],
              "sprites": [{"objectID": 0, "level": 1, "layer": 0, "depth": 1.0, "texture": 0, "x": 1, "y": 2, "width": 1, "height": 1, "flip": false}],
              "limitations": []
            }
            """u8.ToArray();

            int jsonFactoryCalls = 0;
            SimsSimulation.InMemorySessionAssets.StoreScene(scenePath, exported, () =>
            {
                Interlocked.Increment(ref jsonFactoryCalls);
                return utf8Json;
            });

            var (loadedScene, loadedPixels) = LotScene.Load(scenePath);
            Assert.Equal(5, loadedScene.House);
            Assert.Single(loadedPixels);
            Assert.Equal(rgba, loadedPixels[0]);
            Assert.Equal(0, jsonFactoryCalls);

            Assert.True(SimsSimulation.InMemorySessionAssets.TryReadBytes(scenePath, out var bytes1));
            Assert.True(SimsSimulation.InMemorySessionAssets.TryReadBytes(scenePath, out var bytes2));
            Assert.Equal(utf8Json, bytes1);
            Assert.Same(bytes1, bytes2);
            Assert.Equal(1, jsonFactoryCalls);

            LotScene.MeshVertex[] directVertices =
            [
                new(0, 0, 0.5f, 0, 0, 0, 1, 0),
                new(1, 0, 0.5f, 1, 0, 0, 1, 0),
                new(0, 1, 0.5f, 0, 1, 0, 1, 0),
            ];
            var anim = new SceneAnimation
            {
                SchemaVersion = 1,
                BaseRevision = 1,
                Sequence = 1,
                Updates = 1,
                TextureStart = loadedScene.Textures.Count,
                Textures = [],
                Sprites =
                [
                    new LotScene.Sprite { Texture = 0, X = 10, Y = 12, Width = 1, Height = 1, Flip = false, ObjectID = 42, Layer = 1, Level = 1 }
                ],
                Meshes =
                [
                    new SceneAnimation.Mesh
                    {
                        Texture = 0,
                        ObjectID = 42,
                        Depth = 1.0f,
                        Level = 1,
                        Vertices = [],
                        DirectVertices = directVertices,
                    }
                ],
                IsTrustedInProcess = true,
            };

            var applied = anim.Applying(loadedScene, revision: 1, after: 0);
            Assert.Equal(2, applied.Sprites.Count);
            Assert.Equal(42, applied.Sprites[1].ObjectID);
            Assert.NotNull(applied.Meshes);
            var mesh = Assert.Single(applied.Meshes!);
            Assert.Equal(42, mesh.ObjectID);
            Assert.Equal(3, mesh.Vertices.Count);
        }
        finally
        {
            SimsSimulation.InMemorySessionAssets.DisableAndClearRoot(session);
        }
    }

    [Fact]
    public void MipmapAndSmoothZoomMath_PreserveGammaAndFracOriginDuringInterpolation()
    {
        Assert.Equal(1u, SimsSDL.GPUContext.CalcMipLevels(1, 1));
        Assert.Equal(4u, SimsSDL.GPUContext.CalcMipLevels(8, 4));
        Assert.Equal(9u, SimsSDL.GPUContext.CalcMipLevels(256, 256));

        // Averaging transparent black and opaque white in premultiplied sRGB yields 50% alpha (128) and
        // linear 0.5 (~188 in sRGB).
        byte[] src2x2 =
        [
            255, 255, 255, 255, 255, 255, 255, 255,
            0, 0, 0, 0, 0, 0, 0, 0,
        ];
        byte[] mip1 = SpritePixels.DownsamplePremultipliedSrgbBox(src2x2, 2, 2);
        Assert.Equal(4, mip1.Length);
        Assert.InRange(mip1[0], 185, 190);
        Assert.InRange(mip1[1], 185, 190);
        Assert.InRange(mip1[2], 185, 190);
        Assert.Equal(128, mip1[3]);

        var scene = new LotScene
        {
            SchemaVersion = 1,
            House = 5,
            Width = 300,
            Height = 200,
            Textures = [],
            Sprites = [],
            Limitations = [],
        };
        var unSnapped = new SimsSDL.Core.SceneViewport(scene, new System.Numerics.Vector2(1000, 700), 0f, 1.37f, 1f, System.Numerics.Vector2.Zero, snapOriginToPixel: false);
        var snapped = new SimsSDL.Core.SceneViewport(scene, new System.Numerics.Vector2(1000, 700), 0f, 1.37f, 1f, System.Numerics.Vector2.Zero, snapOriginToPixel: true);
        Assert.NotEqual(snapped.Origin.X, unSnapped.Origin.X);
    }

    [Fact]
    public void AppearancePreview_WritesAndLoadsInMemoryWithoutDiskFiles_AndAvatarDirectionWraps()
    {
        Assert.Equal(1.0f, SimsEngine.Character.AvatarDirection.ShortestDelta(7.5f, 0.5f), 4);
        Assert.Equal(-1.0f, SimsEngine.Character.AvatarDirection.ShortestDelta(0.5f, 7.5f), 4);
        Assert.Equal(0.5f, SimsEngine.Character.AvatarDirection.Wrap(8.5f), 4);
        Assert.Equal(7.5f, SimsEngine.Character.AvatarDirection.Wrap(-0.5f), 4);

        var session = Path.Combine(Path.GetTempPath(), "sims-inmem-cas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        try
        {
            SimsSimulation.InMemorySessionAssets.EnableRoot(session);

            var tga = new byte[18 + 2 * 2 * 4];
            tga[2] = 2;
            tga[12] = 2;
            tga[14] = 2;
            tga[16] = 32;
            tga[17] = 8;
            for (var p = 18; p < tga.Length; p += 4)
            {
                tga[p] = 100;
                tga[p + 1] = 150;
                tga[p + 2] = 200;
                tga[p + 3] = 255;
            }

            var verts = new AvatarSceneGeometry.Vertex[]
            {
                new(0f, 0f, 0.5f, 0f, 0f, 0f, 0f, 1f),
                new(1f, 0f, 0.5f, 1f, 0f, 0f, 0f, 1f),
                new(0.5f, 1f, 0.5f, 0.5f, 1f, 0f, 0f, 1f),
            };
            var parts = new[] { new AvatarSceneGeometry.Part(tga, "test", "tga", 2, 2, verts) };
            _ = global::HouseholdAppearancePreview.Write(session, parts);

            var previewDir = Path.Combine(session, "cas-preview");
            var previewJson = Path.Combine(previewDir, "preview.json");
            Assert.False(Directory.Exists(previewDir));
            Assert.False(File.Exists(previewJson));

            var (preview, pixels) = SimsSDL.Core.HouseholdAppearancePreview.Load(previewJson);
            Assert.Single(preview.Textures);
            Assert.Single(preview.Meshes);
            Assert.Equal(3, preview.Meshes[0].Vertices.Count);
            Assert.Single(pixels);
            Assert.Equal(2 * 2 * 4, pixels[0].Length);
        }
        finally
        {
            SimsSimulation.InMemorySessionAssets.DisableAndClearRoot(session);
            if (Directory.Exists(session))
                Directory.Delete(session, recursive: true);
        }
    }
}
