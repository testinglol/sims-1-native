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
}
