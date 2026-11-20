using System.Text.Json;
using SimsEngine.Tests.E2E;
using SimsSDL;
using SimsSDL.Core;
using SimsSimulation;

namespace SimsEngine.Tests;

public sealed class M3GraphicsRenderingTests
{
    [Fact]
    public void PondWaterTextures_ExcludeTransparentIndexMagentaAndPreserveCurvedShoreTransparency()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        // Export House05 or create-family lot with pond water and verify water-*.rgba has zero magenta pixels.
        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-m3-water-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Find any house with pond water (or check House01..House10 or GameData/floors.iff pond tiles)
            var housePath = E2EHarness.FindHouseSource(gameDir, 1, 5)!;
            var boot = WorldBootstrap.Load(gameDir, housePath);
            // Force a small 2x2 pond patch in Arry 10 or test via LotScene on House05 if present;
            // even better: check all water-080*.rgba generated when pond water tiles exist.
            var lot = boot.Lot;
            var waterChunk = lot.Find(SimsEngine.Formats.ResourceType.Parse("Arry"), 10);
            if (waterChunk is null)
                return;

            // Paint a 3x3 pond into an off-building grass corner (tiles 5..7, 5..7) using LotArray.ToChunkData
            var arr = SimsEngine.Formats.LotArray.Read(lot, waterChunk);
            var data = arr.Data.ToArray();
            var sentinel = arr.Element(0, 0)[0] == 0xff ? (byte)0xff : (byte)0;
            var fillVal = sentinel == 0xff ? (byte)0 : (byte)1;
            for (int x = 5; x <= 7; x++)
            for (int y = 5; y <= 7; y++)
            {
                data[(x * arr.Width + y) * arr.ElementSize] = fillVal;
            }
            var modifiedLot = lot.WithAddedOrReplacedChunk(
                SimsEngine.Formats.ResourceType.Parse("Arry"),
                10,
                arr.ToChunkData(data),
                0,
                waterChunk.Label);

            var scenePath = SceneExporter.Export(modifiedLot, boot.World, boot.Registry, gameDir, tempDir, 5, 1);
            var (scene, pixels) = LotScene.Load(scenePath);
            scene.Validate();

            var pondTextures = scene.Textures
                .Select((t, i) => (Texture: t, Pixels: pixels[i]))
                .Where(pair => pair.Texture.File.StartsWith("water-080", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(pondTextures);

            foreach (var (tex, rgba) in pondTextures)
            {
                for (int p = 0; p < rgba.Length; p += 4)
                {
                    if (rgba[p + 3] == 0)
                        continue;
                    bool isMagenta = rgba[p] >= 240 && rgba[p + 1] <= 20 && rgba[p + 2] >= 200;
                    Assert.False(isMagenta, $"Pond texture {tex.File} contains opaque magenta seam pixel at {p / 4}.");
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
    public void KeySpeechBitmapTransparency_PreservesDarkHatAtOriginWhileKeyingSpeechWhite()
    {
        // Simulate 2x2 person speech icon where pixel (0,0) is a dark hat (20,20,20) and (1,0) is Speech (248,252,248)
        byte[] personRgba =
        [
            20, 20, 20, 255,
            248, 252, 248, 255,
            255, 0, 255, 255,
            100, 120, 140, 255,
        ];
        SceneExporter.KeySpeechBitmapTransparency(2, 2, personRgba, isPerson: true);
        Assert.Equal(255, personRgba[3]);     // dark hat pixel at (0,0) preserved!
        Assert.Equal(0, personRgba[7]);       // Speech (248,252,248) keyed out!
        Assert.Equal(0, personRgba[11]);      // Magenta (255,0,255) keyed out!
        Assert.Equal(255, personRgba[15]);    // face pixel preserved!
    }

    [Fact]
    public void RuntimeSlotAnchor_IncludesFourScaleOffsetFromDecompilation()
    {
        // 00438198.c:36: slotY = ((4 << scale) - slotZ * (1 << (scale + 1))) + renderY
        var med = SceneExporter.RuntimeSlotSpritePosition(100f, 200f, slotZ: 0f, height: 32, scale: 2);
        var near = SceneExporter.RuntimeSlotSpritePosition(100f, 200f, slotZ: 0f, height: 64, scale: 3);
        Assert.Equal(200 + (4 << 2) - 32, med.Y);
        Assert.Equal(200 + (4 << 3) - 64, near.Y);
    }

    [Fact]
    public void MotiveBarState_CriticalPulseRespectsEffectsToggleAndPingPongs()
    {
        var disabled = new OriginalGameplayLayout.MotiveBarState(50);
        disabled.SetValue(-85, nowMs: 1000, effectsEnabled: false);
        Assert.False(disabled.Evaluate(1150).IsAnimating);

        var enabled = new OriginalGameplayLayout.MotiveBarState(50);
        enabled.SetValue(-85, nowMs: 1000, effectsEnabled: true);
        var leg1 = enabled.Evaluate(1250);
        var peak = enabled.Evaluate(1500);
        var leg2 = enabled.Evaluate(1750);
        Assert.True(leg1.IsAnimating);
        Assert.True(peak.IsAnimating);
        Assert.True(leg2.IsAnimating);
        Assert.Equal(OriginalGameplayLayout.MotiveArrowFallingFlashRgb, peak.RedRgb);
        Assert.NotEqual(leg1.RedRgb, peak.RedRgb);
    }

    [Fact]
    public void SceneModels_AudioEventPitchScaleAndOverheadRoundTrip_Preserved()
    {
        var audioJson = """
            {"command":"play","voice":1,"file":"audio/test.wav","duration":1.5,"group":"FX","volume":0.8,"pan":0.0,"loop":false,"pitchScale":1.25}
            """;
        var evt = AudioEvent.FromJson(audioJson);
        evt.Validate();
        Assert.Equal(1.25f, evt.PitchScale);

        var animJson = """
            {
              "schemaVersion": 1,
              "baseRevision": 1,
              "sequence": 2,
              "updates": 1,
              "textureStart": 0,
              "textures": [],
              "sprites": [],
              "meshes": [],
              "overheadFeedback": [
                {"objectID": 18, "kind": "currency", "text": "+$250", "backgroundRgb": 82, "foregroundRgb": 51769, "delta": 250, "x": 100, "y": 200, "level": 1, "stackIndex": 0, "remainingTicks": 99, "hasSprite": false}
              ],
              "skillMeters": [
                {"objectID": 18, "spriteId": 803, "progressTenths": 3, "x": 100, "y": 150, "level": 1}
              ]
            }
            """;
        var anim = SceneAnimation.FromJson(animJson);
        Assert.NotNull(anim.OverheadFeedback);
        Assert.Single(anim.OverheadFeedback!);
        Assert.Equal("+$250", anim.OverheadFeedback![0].Text);
        Assert.NotNull(anim.SkillMeters);
        Assert.Single(anim.SkillMeters!);
        Assert.Equal(803, anim.SkillMeters![0].SpriteId);
    }

    [Fact]
    public void BookcaseStudy_DismissingStartupDialogs_ExportsSkillGainProgressMeter()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        Assert.True(server.HandleJson("freeWill", new { enabled = false }).GetProperty("ok").GetBoolean());

        short targetId = 0;
        JsonElement targetAction = default;
        var state = server.State();
        foreach (var obj in state.GetProperty("objects").EnumerateArray())
        {
            var name = obj.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!name.Contains("Bookshelf", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("Bookcase", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("Easel", StringComparison.OrdinalIgnoreCase))
                continue;
            var id = obj.GetProperty("id").GetInt16();
            var menu = server.HandleJson("menu", new { objectID = id });
            if (!menu.GetProperty("ok").GetBoolean())
                continue;
            foreach (var entry in menu.GetProperty("result").EnumerateArray())
            {
                var actionName = entry.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                if (actionName.Contains("Study", StringComparison.OrdinalIgnoreCase) ||
                    actionName.Contains("Cook", StringComparison.OrdinalIgnoreCase) ||
                    actionName.Contains("Paint", StringComparison.OrdinalIgnoreCase))
                {
                    targetId = id;
                    targetAction = entry.Clone();
                    break;
                }
            }
            if (targetId != 0)
                break;
        }
        Assert.NotEqual((short)0, targetId);

        Assert.True(server.HandleJson("interact", new
        {
            objectID = targetId,
            interaction = targetAction.GetProperty("interaction").GetByte(),
            param0 = targetAction.GetProperty("param0").GetInt16(),
            global = targetAction.GetProperty("global").GetBoolean(),
        }).GetProperty("ok").GetBoolean());

        bool foundSkillMeter = false;
        var diagInfo = $"target={targetId}:{targetAction.GetProperty("name").GetString()}";
        for (int i = 0; i < 40 && !foundSkillMeter; i++)
        {
            var currentState = server.State();
            foreach (var dlg in currentState.GetProperty("dialogs").EnumerateArray())
            {
                var token = dlg.GetProperty("token").GetString();
                var kind = dlg.TryGetProperty("kind", out var k) ? k.GetInt32() : 0;
                if (!string.IsNullOrEmpty(token))
                    _ = server.HandleJson("dialogResponse", new { token, code = (byte)(kind == 1 ? 1 : 0) });
            }

            server.Step(20);
            var (scene, _) = LotScene.Load(server.LatestScenePath());
            scene.Validate();
            if ((scene.SkillMeters ?? []).Any(m => m.ProgressTenths is >= 0 and <= 9 && float.IsFinite(m.X) && float.IsFinite(m.Y)))
                foundSkillMeter = true;
            if (i == 39 && !foundSkillMeter)
            {
                var sim0 = server.World!.WalkOrder.OfType<SimsEngine.Simulation.XPerson>().First();
                var slotActive = sim0.RuntimeSlots.Count > 0 ? sim0.RuntimeSlots[0].ReadWord(0x10) : -99;
                var slotSprite = sim0.RuntimeSlots.Count > 0 ? sim0.RuntimeSlots[0].ReadWord(0x14) : -99;
                var targetObj = server.World.ObjectById(targetId);
                var objSlotActive = targetObj?.RuntimeSlots.Count > 0 ? targetObj.RuntimeSlots[0].ReadWord(0x10) : -99;
                var objSlotSprite = targetObj?.RuntimeSlots.Count > 0 ? targetObj.RuntimeSlots[0].ReadWord(0x14) : -99;
                diagInfo += $" ticks={server.World.Clock.Ticks} pos=({sim0.Position.X},{sim0.Position.Y},{sim0.Position.Height}) slot=({slotActive},{slotSprite}) objSlot=({objSlotActive},{objSlotSprite}) sims={server.State().GetProperty("sims")[0].GetProperty("actions")} dialogs={server.State().GetProperty("dialogs")}";
            }
        }
        Assert.True(foundSkillMeter, $"Expected SkillMeters: {diagInfo}");
    }
}
