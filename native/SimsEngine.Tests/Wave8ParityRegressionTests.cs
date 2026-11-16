using System.Numerics;
using System.Text.Json;
using SimsEngine.Character;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsSimulation;

namespace SimsEngine.Tests;

public sealed class Wave8ParityRegressionTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    [Fact]
    public void SimDialogPortrait_Crops3x1StripWithoutNeighborHairBleedAndPreservesPeopleTemplateBevel()
    {
        Assert.Equal((3, 1), PanelImages.PersonFaceSheetGrid(105, 41));
        Assert.Equal((3, 1), PanelImages.PersonFaceSheetGrid(108, 41));
        Assert.Equal((4, 3), PanelImages.PersonFaceSheetGrid(140, 123));
        Assert.Equal((4, 3), PanelImages.PersonFaceSheetGrid(180, 135));
        Assert.Equal((1, 1), PanelImages.PersonFaceSheetGrid(32, 32));

        var stripRgba = new byte[105 * 41 * 4];
        for (var y = 0; y < 41; y++)
        {
            for (var x = 0; x < 105; x++)
            {
                var at = (y * 105 + x) * 4;
                stripRgba[at] = x < 35 ? (byte)10 : (byte)240;
                stripRgba[at + 1] = x < 35 ? (byte)200 : (byte)10;
                stripRgba[at + 2] = x < 35 ? (byte)20 : (byte)10;
                stripRgba[at + 3] = 255;
            }
        }

        var templateRgba = new byte[45 * 45 * 4];
        for (var i = 0; i < 45 * 45; i++)
        {
            templateRgba[i * 4] = 0;
            templateRgba[i * 4 + 1] = 30;
            templateRgba[i * 4 + 2] = 180;
            templateRgba[i * 4 + 3] = 255;
        }
        var templateBmp = BitmapCodec.Write(45, 45, templateRgba);

        var (outW, outH, outRgba) = PanelImages.CompositePersonDialogThumbnail(105, 41, stripRgba, templateBmp);
        Assert.Equal(45, outW);
        Assert.Equal(45, outH);

        for (var y = 0; y < 45; y++)
        {
            for (var x = 0; x < 45; x++)
            {
                var at = (y * 45 + x) * 4;
                Assert.False(outRgba[at] == 240 && outRgba[at + 1] == 10,
                    $"Neighbor column bled into composited portrait at ({x}, {y}).");
                if (x < 2 || x >= 43 || y < 2 || y >= 43)
                {
                    Assert.Equal(0, outRgba[at]);
                    Assert.Equal(30, outRgba[at + 1]);
                    Assert.Equal(180, outRgba[at + 2]);
                }
            }
        }

        var gameDir = LocateGameDirectory();
        var house05Path = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(house05Path))
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-wave8-portrait-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var boot = WorldBootstrap.Load(gameDir, house05Path!);
            var bella = Assert.IsType<XPerson>(boot.World.ObjectById(18));
            var images = new PanelImages(tempDir, boot.Registry);
            var record = new SimulationDialogRecord("test-token", bella, bella, 0, "Title", "Message", "OK", null, null);
            var dialogImg = images.DialogImage(record);
            Assert.NotNull(dialogImg);
            var imgJson = JsonSerializer.SerializeToElement(dialogImg);
            Assert.Equal(1, imgJson.GetProperty("columns").GetInt32());
            Assert.Equal(1, imgJson.GetProperty("rows").GetInt32());
            var bmpFile = Path.Combine(tempDir, imgJson.GetProperty("file").GetString()!);
            var (bmpW, bmpH, _) = BitmapCodec.Read(File.ReadAllBytes(bmpFile));
            Assert.Equal(45, bmpW);
            Assert.Equal(45, bmpH);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void PersonBalloonAndFireSpeechIcon_FloatAboveHeadAndColorKeyGraySquareUnlit()
    {
        var gameDir = LocateGameDirectory();
        var house05Path = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(house05Path))
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-wave8-balloon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var boot = WorldBootstrap.Load(gameDir, house05Path!);
            var bella = Assert.IsType<XPerson>(boot.World.ObjectById(18));
            var fireEntry = boot.Registry.Entries.FirstOrDefault(e =>
                e.Source.Contains("Fire.iff", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(fireEntry);

            Assert.NotEmpty(bella.RuntimeSlots);
            var slot = bella.RuntimeSlots[0];
            Assert.Equal(2.0f, BitConverter.Int32BitsToSingle(slot.ReadWord(0x08)));
            slot.SetSelectorSprite(unchecked((int)fireEntry!.Guid));
            slot.WriteWord(0x2c, 104);
            slot.WriteWord(0x38, 1);
            slot.ActivateForTicks(120);

            var scenePath = SceneExporter.Export(boot.Lot, boot.World, boot.Registry, gameDir, tempDir, 5, 1);
            using var doc = JsonDocument.Parse(File.ReadAllBytes(scenePath));
            var root = doc.RootElement;
            var textures = root.GetProperty("textures").EnumerateArray().ToArray();
            var bellaSprites = root.GetProperty("sprites").EnumerateArray()
                .Where(s => s.TryGetProperty("objectID", out var oid) && oid.GetInt32() == bella.Id)
                .ToArray();
            Assert.True(bellaSprites.Length >= 2, $"Expected scream balloon + Fire speech icon on Sim {bella.Id}, got {bellaSprites.Length}.");

            var bellaMinMeshY = root.GetProperty("meshes").EnumerateArray()
                .Where(m => m.TryGetProperty("objectID", out var oid) && oid.GetInt32() == bella.Id)
                .SelectMany(m => m.GetProperty("vertices").EnumerateArray())
                .Min(v => v.GetProperty("y").GetSingle());

            var speechSprite = bellaSprites.First(s =>
                textures[s.GetProperty("texture").GetInt32()].GetProperty("file").GetString()!.Contains("runtime-slot-speech-", StringComparison.Ordinal));
            var balloonSprite = bellaSprites.First(s =>
                textures[s.GetProperty("texture").GetInt32()].GetProperty("file").GetString()!.Contains("runtime-slot-", StringComparison.Ordinal) &&
                !textures[s.GetProperty("texture").GetInt32()].GetProperty("file").GetString()!.Contains("speech", StringComparison.Ordinal));

            Assert.True(!balloonSprite.TryGetProperty("room", out var balloonRoom) || balloonRoom.ValueKind == JsonValueKind.Null);
            Assert.True(!speechSprite.TryGetProperty("room", out var speechRoom) || speechRoom.ValueKind == JsonValueKind.Null);
            Assert.Equal(JsonValueKind.Object, balloonSprite.GetProperty("near").ValueKind);
            Assert.Equal(JsonValueKind.Object, speechSprite.GetProperty("near").ValueKind);

            var speechCenterY = speechSprite.GetProperty("y").GetSingle() + speechSprite.GetProperty("height").GetSingle() * 0.5f;
            Assert.True(speechCenterY < bellaMinMeshY,
                $"Balloon speech icon center Y ({speechCenterY}) must float above Sim's top head vertex Y ({bellaMinMeshY}).");

            var speechTexFile = textures[speechSprite.GetProperty("texture").GetInt32()].GetProperty("file").GetString()!;
            var speechRgba = File.ReadAllBytes(Path.Combine(tempDir, speechTexFile));
            Assert.Equal(0, speechRgba[3]);
            Assert.Contains(Enumerable.Range(0, speechRgba.Length / 4), px => speechRgba[px * 4 + 3] == 255);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void FireAudio_ResolvesAllFireCuesAndStingFireSurvivesStopOwnerSounds()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-wave8-audio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var bank = new SoundBank(gameDir, tempDir);
            foreach (var cue in new[] { "fire_start", "fire_burning_small", "fire_burning_medium", "fire_burning_big", "fire_extinguish", "sting_fire" })
            {
                var resolved = bank.Resolve(cue);
                Assert.NotNull(resolved);
                Assert.NotEmpty(resolved!.RelativePath);
            }

            var vox = bank.ResolveVoice("fire_panic_vox", 'f', 1);
            Assert.NotNull(vox);

            var stingRes = bank.Resolve("sting_fire")!;
            var burnRes = bank.Resolve("fire_burning_small")!;
            Assert.False(stingRes.IsPositionalObjectTrack);
            Assert.True(burnRes.IsPositionalObjectTrack);

            var events = new List<JsonElement>();
            using var router = new HitAudioRuntime(tempDir, "test", payload =>
            {
                if (payload is not null) events.Add(JsonSerializer.SerializeToElement(payload));
            }, game: gameDir);
            const short fireId = 77;
            Assert.True(router.PlayForSimulationObject("sting_fire", fireId, pan: true, zoom: true));
            Assert.True(router.PlayForSimulationObject("fire_burning_small", fireId, pan: true, zoom: true));

            var stingPlay = events.First(e => e.GetProperty("command").GetString() == "play" && e.GetProperty("group").GetString() == "MUSIC");
            var burnPlay = events.First(e => e.GetProperty("command").GetString() == "play" && e.GetProperty("group").GetString() == "FX");
            var stingVoice = stingPlay.GetProperty("voice").GetInt64();
            var burnVoice = burnPlay.GetProperty("voice").GetInt64();
            Assert.Equal(HitAudioRuntime.NeighborhoodOwnerID, stingPlay.GetProperty("ownerID").GetInt32());
            Assert.False(stingPlay.GetProperty("spatial").GetBoolean());
            Assert.Equal(fireId, burnPlay.GetProperty("ownerID").GetInt32());
            Assert.True(burnPlay.GetProperty("spatial").GetBoolean());

            events.Clear();
            router.RemoveOwner(fireId);
            var stoppedVoices = events
                .Where(e => e.GetProperty("command").GetString() == "stop")
                .Select(e => e.GetProperty("voice").GetInt64())
                .ToHashSet();
            Assert.Contains(burnVoice, stoppedVoices);
            Assert.DoesNotContain(stingVoice, stoppedVoices);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void VitaboyDressUndressAndPerMotionFrameClamp_AttachesHandheldPropsAndSamplesMixedLengthTracks()
    {
        var skeleton = new Skeleton("adult", [
            new Bone("ROOT", "NULL", [], Vector3.Zero, Quaternion.Identity, [0, 0, 0], [0, 0]),
            new Bone("R_HAND", "ROOT", [], new Vector3(1, 0, 0), Quaternion.Identity, [0, 0, 1], [0, 0]),
        ]);
        var anim = new Animation("mixed", "adult", [0f, 0f], 0, 11, 11, [
            new Motion("ROOT", 10, 333f, [0, 0, 0, 0], [], []),
            new Motion("R_HAND", 1, 33f, [0, 0, 10, 10], [], []),
        ]);
        var translations = new float[11 * 3];
        var rotations = new float[11 * 4];
        for (var i = 0; i < 11; i++)
        {
            translations[i * 3] = i;
            rotations[i * 4 + 3] = 1f;
        }
        translations[10 * 3 + 1] = 4.5f;
        var sampled = Pose.Sample(skeleton, anim, new AnimationCurves(translations, rotations), frame: 7.5);
        Assert.Equal(7.5f, sampled[0].LocalTranslation.X, 3);
        Assert.Equal(4.5f, sampled[1].LocalTranslation.Y, 3);

        var gameDir = LocateGameDirectory();
        var house05Path = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(house05Path))
            return;

        var boot = WorldBootstrap.Load(gameDir, house05Path!);
        var bella = Assert.IsType<XPerson>(boot.World.ObjectById(18));
        var geometry = AvatarSceneGeometry.For(gameDir, boot.Registry);
        var camera = new LotCamera(0, 64, 64);
        var basePartCount = geometry.Build(bella, 1, 0, (_, _) => 0f, camera).Count;

        var playback = new AvatarAnimationPlayback(new AvatarAnimationClip("test", 10, []), backwards: false, loop: false);
        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(
            bella, playback, new AvatarAnimationSoundEvent(0, "", "dress", "coffeecupbigfull", false, false)));
        Assert.Contains(bella.TransientExtraParts, p => p.Item1.Equals("coffeecupbigfull", StringComparison.OrdinalIgnoreCase));
        var dressedPartCount = geometry.Build(bella, 1, 0, (_, _) => 0f, camera).Count;
        Assert.True(dressedPartCount > basePartCount,
            $"Expected handheld prop coffeecupbigfull to add mesh part(s): base={basePartCount}, dressed={dressedPartCount}.");

        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(
            bella, playback, new AvatarAnimationSoundEvent(0, "", "undress", "COFFEECUPBIGFULL.APR", false, false)));
        Assert.Empty(bella.TransientExtraParts);

        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(
            bella, playback, new AvatarAnimationSoundEvent(0, "", "dress", "accessory-paparazzicamera.cmx", false, false)));
        Assert.True(geometry.Build(bella, 1, 0, (_, _) => 0f, camera).Count > basePartCount);
        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(
            bella, playback, new AvatarAnimationSoundEvent(0, "", "undress", "accessory-paparazzicamera.cmx", false, false)));
        Assert.Empty(bella.TransientExtraParts);
    }
}
