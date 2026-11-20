using System.Numerics;
using System.Text;
using System.Text.Json;
using SimsEngine.Character;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSDL;
using SimsSDL.Core;
using SimsSDL.UI;
using SimsSimulation;
using SimLotCamera = global::LotCamera;

namespace SimsEngine.Tests.E2E;

[Collection("Neighborhoods")]
[Trait("Suite", "E2E")]
[Trait("Tier", "Tier3")]
[Trait("Feature", "Multi")]
[Trait("Area", "CrossFeature")]
public sealed class CrossFeatureE2ETests
{
    private static bool IsHousehold(XPerson person) =>
        person[PersonData.FamilyNumber] > 0 && person.TypeRecord is not null;

    private static bool IsAdult(XPerson person) =>
        person[PersonData.PersonAge] >= 18;

    private static bool IsChild(XPerson person) =>
        person[PersonData.PersonAge] < 18;

    private static short[] DefaultPersonality() => [500, 500, 500, 500, 500];

    private static void PauseAndDrainDialogs(ServerSession server)
    {
        Assert.True(server.HandleJson("speed", new { value = 0 }).GetProperty("ok").GetBoolean());
        var world = server.World;
        if (world is null)
            return;

        for (int quiet = 0, pass = 0; quiet < 4 && pass < 32; pass++)
        {
            world.WakeSimulateOnPauseObjects();
            world.AdvanceSimulationTick(new SimulationClockControl(0, 0, 0), 0, world.Clock.Ticks);
            if (world.DialogRecords.Any(d => d.Visible && d.State < 2))
            {
                quiet = 0;
                while (world.DialogRecords.FirstOrDefault(d => d.Visible && d.State < 2) is { } dlg)
                {
                    byte responseCode = dlg.Kind is 1 or 2 or 14 ? (byte)1 : (byte)0;
                    world.RespondMessageDialog(dlg.Token, responseCode);
                }
            }
            else if (world.DialogRecords.Count == 0)
            {
                quiet++;
            }
            else
            {
                quiet = 0;
            }
        }
        server.DrainEvents();
    }

    private static List<(int X, int Y)> FindOpenFlatTiles(string userData, SimulationWorld world, short house, int level = 1)
    {
        var lotPath = Path.Combine(userData, "Houses", $"House{house:D2}.iff");
        var lot = IffFile.Open(lotPath);
        var floors = LotFloors.Read(lot, level);
        var pool = LotPool.Read(lot);
        var water = LotWater.Read(lot);
        var tiles = LotTiles.Read(lot);
        var walls = LotWalls.Read(lot, level);
        var terrain = LotTerrain.Read(lot);

        var open = new List<(int X, int Y)>();
        for (int y = 8; y < 56; y++)
        {
            for (int x = 8; x < 56; x++)
            {
                if (!tiles.InsideLot(x, y) || !tiles.Buildable(x, y, level))
                    continue;
                if (pool.IsPool(x, y, level) || water.IsWater(x, y, level) || floors[x, y] != 0 || walls[x, y][0] != 0)
                    continue;
                var corners = terrain.Corners(x, y);
                if (corners[0] != corners[1] || corners[0] != corners[2] || corners[0] != corners[3])
                    continue;
                if (world.ObjectsAtTile(x, y, level).Any())
                    continue;
                open.Add((x, y));
            }
        }
        return open;
    }

    private static (int X, int Y) FindOpenFlatPatch(string userData, SimulationWorld world, short house, int patchSize = 4)
    {
        var lotPath = Path.Combine(userData, "Houses", $"House{house:D2}.iff");
        var lot = IffFile.Open(lotPath);
        var terrain = LotTerrain.Read(lot);
        var walls = LotWalls.Read(lot, 1);
        var floors = LotFloors.Read(lot, 1);
        var tiles = LotTiles.Read(lot);
        var pool = LotPool.Read(lot);
        var water = LotWater.Read(lot);
        var roomMap = world.LotRoomMaps.GetValueOrDefault(1);

        for (int y = 10; y <= 54 - patchSize; y++)
        {
            for (int x = 10; x <= 54 - patchSize; x++)
            {
                var refH = terrain.ReferenceHeight(x, y);
                var ok = true;
                for (int dy = 0; dy < patchSize && ok; dy++)
                {
                    for (int dx = 0; dx < patchSize; dx++)
                    {
                        var nx = x + dx;
                        var ny = y + dy;
                        if (!tiles.InsideLot(nx, ny) || !tiles.Buildable(nx, ny, 1)) { ok = false; break; }
                        if (world.TileValueAt(nx, ny, 1) != 0 || world.ObjectsAtTile(nx, ny, 1).Any()) { ok = false; break; }
                        if (roomMap is not null && !roomMap.IsOutside(roomMap.RoomAt(nx, ny))) { ok = false; break; }
                        if (walls[nx, ny][0] != 0 || floors[nx, ny] != 0) { ok = false; break; }
                        if (pool.IsPool(nx, ny, 1) || water.IsWater(nx, ny, 1)) { ok = false; break; }
                        if (terrain.ReferenceHeight(nx, ny) != refH) { ok = false; break; }
                    }
                }
                if (ok)
                    return (x, y);
            }
        }

        throw new InvalidOperationException($"No {patchSize}x{patchSize} open flat patch found on House{house:D2}.iff.");
    }

    private static AvatarAnimationClip ResolveRealClip(string gameDir, ObjectRegistry? registry, string name)
    {
        var shared = AvatarAnimations.GetSharedIndex(gameDir, registry);
        Assert.True(shared.AnimationsByName.TryGetValue(name, out var list) && list.Count > 0,
            $"Missing real animation '{name}' in shared character index.");
        var anim = list[0];
        var events = new List<AvatarAnimationEvent>();
        var soundEvents = new List<AvatarAnimationSoundEvent>();
        var frameCount = Math.Max(1, anim.PlaybackFrameCount);
        var durationMs = frameCount * (1000f / 30f);

        foreach (var motion in anim.Motions)
        {
            foreach (var ev in motion.Events)
            {
                var timeMs = AvatarAnimationEvent.MotionFrameToMilliseconds(ev.Time, frameCount, durationMs);
                foreach (var prop in ev.Properties)
                {
                    if (prop.Name.Equals("xevt", StringComparison.OrdinalIgnoreCase) &&
                        short.TryParse(prop.Value, out var xevt))
                    {
                        events.Add(new AvatarAnimationEvent(timeMs, xevt));
                    }
                    else if (prop.Name is "sound" or "selectedsound" or "deselectedsound" or "footstep" or
                             "interruptable" or "interruptible" or "lefthand" or "righthand" or
                             "censor" or "dress" or "undress" or "anchor" or "discontinuity")
                    {
                        soundEvents.Add(new AvatarAnimationSoundEvent(timeMs, "", prop.Name, prop.Value, false, false, motion.Name));
                    }
                }
            }
        }

        events.Sort((a, b) => a.TimeMilliseconds.CompareTo(b.TimeMilliseconds));
        soundEvents.Sort((a, b) => a.TimeMilliseconds.CompareTo(b.TimeMilliseconds));
        return new AvatarAnimationClip(anim.Name, frameCount, events, soundEvents, DurationMilliseconds: durationMs);
    }

    private static byte[] AnimateSimOperand(short animationIndex, byte source = 1, byte flags = 0, sbyte expectedEvents = 0) =>
        [
            unchecked((byte)(animationIndex & 0xff)),
            unchecked((byte)((animationIndex >> 8) & 0xff)),
            0,
            source,
            flags,
            0,
            unchecked((byte)expectedEvents),
            0
        ];

    private static void AssertFiniteParts(IReadOnlyList<AvatarSceneGeometry.Part> parts)
    {
        Assert.NotEmpty(parts);
        foreach (var part in parts)
        {
            Assert.NotEmpty(part.Vertices);
            foreach (var v in part.Vertices)
            {
                Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z));
                Assert.True(float.IsFinite(v.Nx) && float.IsFinite(v.Ny) && float.IsFinite(v.Nz));
            }
        }
    }

    private static string CreateIsolatedGameDirWithDownloads(string sourceGameDir, Action<string> populateDownloads, out string tempRoot)
    {
        tempRoot = Path.Combine(Path.GetTempPath(), "sims-e2e-t3-gamedir-" + Guid.NewGuid().ToString("N"));
        var gameCopy = Path.Combine(tempRoot, "game");
        Directory.CreateDirectory(gameCopy);

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceGameDir))
        {
            var name = Path.GetFileName(entry);
            if (string.Equals(name, "Downloads", StringComparison.OrdinalIgnoreCase))
                continue;

            var dst = Path.Combine(gameCopy, name);
            if (Directory.Exists(entry))
            {
                try
                {
                    Directory.CreateSymbolicLink(dst, entry);
                }
                catch
                {
                    CopyDirRecursive(entry, dst);
                }
            }
            else if (File.Exists(entry))
            {
                File.Copy(entry, dst, overwrite: true);
            }
        }

        var downloads = Path.Combine(gameCopy, "Downloads");
        Directory.CreateDirectory(downloads);
        var sourceDownloads = Path.Combine(sourceGameDir, "Downloads");
        if (Directory.Exists(sourceDownloads))
        {
            foreach (var dlEntry in Directory.EnumerateFileSystemEntries(sourceDownloads))
            {
                var dlDst = Path.Combine(downloads, Path.GetFileName(dlEntry));
                if (Directory.Exists(dlEntry))
                {
                    try { Directory.CreateSymbolicLink(dlDst, dlEntry); }
                    catch { CopyDirRecursive(dlEntry, dlDst); }
                }
                else if (File.Exists(dlEntry))
                {
                    try { File.CreateSymbolicLink(dlDst, dlEntry); }
                    catch { File.Copy(dlEntry, dlDst, overwrite: true); }
                }
            }
        }
        populateDownloads(downloads);
        return gameCopy;
    }

    private static void CopyDirRecursive(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(src))
            CopyDirRecursive(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    private static void WriteSyntheticSkinPack(
        string folder,
        string bmpFileName,
        string cmxFileName,
        string sknFileName,
        string suitName,
        string meshName,
        string textureName)
    {
        Directory.CreateDirectory(folder);

        var pixels = new byte[4 * 4 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 180;
            pixels[i + 1] = 120;
            pixels[i + 2] = 90;
            pixels[i + 3] = 255;
        }
        File.WriteAllBytes(Path.Combine(folder, bmpFileName), BitmapCodec.Write(4, 4, pixels));

        var skn = new StringBuilder();
        skn.AppendLine(meshName);
        skn.AppendLine(textureName);
        skn.AppendLine("1");
        skn.AppendLine("PELVIS");
        skn.AppendLine("1");
        skn.AppendLine("0 1 2");
        skn.AppendLine("1");
        skn.AppendLine("0 0 3 0 0");
        skn.AppendLine("3");
        skn.AppendLine("0 0");
        skn.AppendLine("1 0");
        skn.AppendLine("0 1");
        skn.AppendLine("0");
        skn.AppendLine("3");
        skn.AppendLine("0 0 0 0 0 1");
        skn.AppendLine("1 0 0 0 0 1");
        skn.AppendLine("0 1 0 0 0 1");
        File.WriteAllText(Path.Combine(folder, sknFileName), skn.ToString(), Encoding.ASCII);

        var cmx = new StringBuilder();
        cmx.AppendLine("// Character File");
        cmx.AppendLine("version 300");
        cmx.AppendLine("0");
        cmx.AppendLine("1");
        cmx.AppendLine(suitName);
        cmx.AppendLine("0");
        cmx.AppendLine("0");
        cmx.AppendLine("1");
        cmx.AppendLine("BODY");
        cmx.AppendLine(meshName);
        cmx.AppendLine("0");
        cmx.AppendLine("0");
        cmx.AppendLine("0");
        File.WriteAllText(Path.Combine(folder, cmxFileName), cmx.ToString(), Encoding.Latin1);
    }

    // -------------------------------------------------------------------------
    // Cluster 1: Audio <-> Anim / GraphicsUI / CAS / Travel / SimAntics / BuildSave (T3-01 .. T3-08)
    // -------------------------------------------------------------------------

    [Fact]
    public void T3_01_AudioAndAnimations_BarefootCarpetTilePoolFootstepCues()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());

        var world = server.World!;
        var sim = world.WalkOrder.OfType<XPerson>().First(IsHousehold);

        var emittedCues = new List<string>();
        var origPlay = world.PlaySoundEvent;
        world.PlaySoundEvent = (cue, owner, pan, zoom) =>
        {
            emittedCues.Add(cue);
            return origPlay?.Invoke(cue, owner, pan, zoom) ?? true;
        };

        var bareTiles = FindOpenFlatTiles(profile.UserData, world, 5);
        Assert.True(bareTiles.Count >= 4);

        PauseAndDrainDialogs(server);
        var carpetResp = server.HandleJson("architecture", new { command = "FLOOR_RECT", level = 1, x = bareTiles[0].X, y = bareTiles[0].Y, x2 = 0, y2 = 0, pattern = 1 });
        var hardResp = server.HandleJson("architecture", new { command = "FLOOR_RECT", level = 1, x = bareTiles[1].X, y = bareTiles[1].Y, x2 = 0, y2 = 0, pattern = 5 });
        Assert.True(carpetResp.GetProperty("ok").GetBoolean() && carpetResp.GetProperty("result").GetProperty("applied").GetBoolean());
        Assert.True(hardResp.GetProperty("ok").GetBoolean() && hardResp.GetProperty("result").GetProperty("applied").GetBoolean());

        var walkClip = new AvatarAnimationClip(
            "t3-01-footstep",
            10,
            [],
            [new AvatarAnimationSoundEvent(0, "", "footstep", "1", false, false)],
            DurationMilliseconds: 333f);

        string TriggerStep(int tx, int ty, short outfit, bool swimming)
        {
            emittedCues.Clear();
            sim[PersonData.CurrentOutfit] = outfit;
            sim[PersonData.IsSwimming] = (short)(swimming ? 1 : 0);
            world.SetObjectPosition(sim, new ObjectPosition((short)(tx * 16 + 8), (short)(ty * 16 + 8), 1));
            sim.AnimationPlayback = new AvatarAnimationPlayback(walkClip, false, false, sim.AnimatorMilliseconds);
            world.TickAvatarAnimations(sim);
            return Assert.Single(emittedCues);
        }

        server.DrainEvents();
        var carpetCue = TriggerStep(bareTiles[0].X, bareTiles[0].Y, outfit: 1, swimming: false);
        Assert.Equal("footstep_soft_noshoe", carpetCue);

        var hardCue = TriggerStep(bareTiles[1].X, bareTiles[1].Y, outfit: 1, swimming: false);
        Assert.Equal("footstep_hard_noshoe", hardCue);

        var swimCue = TriggerStep(bareTiles[2].X, bareTiles[2].Y, outfit: 2, swimming: true);
        Assert.Equal("footstep_pool_swim_stroke", swimCue);

        using var audio = E2EHarness.CreateAudioSession(gameDir);
        foreach (var cue in new[] { carpetCue, hardCue, swimCue })
        {
            var resolved = audio.Bank.Resolve(cue);
            Assert.NotNull(resolved);
            Assert.Equal("FX", resolved.Group);
            Assert.True(audio.Runtime.PlayForSimulationObject(cue, sim.Id, true, true));
        }
        Assert.Equal(3, audio.PlayEvents().Count);
    }

    [Fact]
    public void T3_02_AudioAndAnimations_ThreeWayVoxDemographicsCarryAndHeadTracking()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());

        var world = server.World!;
        var household = world.WalkOrder.OfType<XPerson>().Where(IsHousehold).ToArray();
        var adultFemale = household.First(p => IsAdult(p) && p[PersonData.Gender] == 1);
        var adultMale = household.First(p => IsAdult(p) && p[PersonData.Gender] == 0);
        var child = household.First(IsChild);

        var carryClip = ResolveRealClip(gameDir, world.ObjectRegistry, "a2o-rarm-carry-loop");
        adultFemale.CarryAnimationPlayback = new AvatarAnimationPlayback(carryClip, false, true, adultFemale.AnimatorMilliseconds);

        var lookFrame = new StackFrame(adultMale.Id, [0], [0, 0]);
        var lookCtx = new TreeContext(world, adultFemale, lookFrame);
        Assert.Equal(PrimitiveResult.True, LookTowardsPrimitive.Execute(lookCtx, [0, 0]));
        Assert.Equal(adultMale.Id, adultFemale[PersonData.HeadSeekObject]);
        Assert.Equal((short)1, adultFemale[PersonData.HeadSeekState]);

        for (int i = 0; i < 6; i++)
            world.TickAvatarAnimations(adultFemale);

        var order = adultFemale.GetAnimationPracticeApplicationOrder();
        Assert.Contains(order, p => p.Clip.Name.Equals("a2o-rarm-carry-loop", StringComparison.OrdinalIgnoreCase));

        var geometry = AvatarSceneGeometry.For(gameDir, world.ObjectRegistry!);
        var parts = geometry.Build(adultFemale, 1, 0, (_, _) => 0f, new SimLotCamera(0, 64, 64));
        AssertFiniteParts(parts);

        // Verify VOX demographic routing ('f', 'm', 'k') + mixer PitchScale resampling
        using var audio = E2EHarness.CreateAudioSession(gameDir);
        var fVox = audio.Bank.ResolveVoice("vox_laugh_giggle", 'f', 1);
        var mVox = audio.Bank.ResolveVoice("vox_laugh_giggle", 'm', 1);
        var kVox = audio.Bank.ResolveVoice("vox_laugh_giggle", 'k', 1);
        Assert.NotNull(fVox);
        Assert.NotNull(mVox);
        Assert.NotNull(kVox);
        Assert.Contains("gigglef", fVox.RelativePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gigglem", mVox.RelativePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gigglek", kVox.RelativePath, StringComparison.OrdinalIgnoreCase);

        server.DrainEvents();
        var playHook = world.PlaySoundEvent;
        Assert.NotNull(playHook);
        Assert.True(playHook("vox_laugh_giggle", adultFemale.Id, false, false));
        Assert.True(playHook("vox_laugh_giggle", adultMale.Id, false, false));
        Assert.True(playHook("vox_laugh_giggle", child.Id, false, false));

        var voxPlays = server.DrainEvents()
            .Where(e => e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
                        e.TryGetProperty("command", out var c) && c.GetString() == "play" &&
                        e.TryGetProperty("group", out var g) && g.GetString() == "VOX")
            .ToArray();
        Assert.Equal(3, voxPlays.Length);
        Assert.Contains(voxPlays, p => p.GetProperty("file").GetString()!.Contains("gigglef", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(voxPlays, p => p.GetProperty("file").GetString()!.Contains("gigglem", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(voxPlays, p => System.Text.RegularExpressions.Regex.IsMatch(
            p.GetProperty("file").GetString()!, @"(?:giggle|laugh|apprec)[a-z_]*k\d*", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

        var mixer = new AudioMixer();
        var pcm = new float[2048 * 2];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = 0.2f;
        mixer.Set(1, new AudioMixer.Voice(pcm) { PitchScale = 0.85f, Loop = true });
        mixer.Set(2, new AudioMixer.Voice(pcm) { PitchScale = 1.25f, Loop = true });
        mixer.MixAndPut(nint.Zero, 256);
        double posSlow = 0, posFast = 0;
        mixer.Update(1, (ref AudioMixer.Voice v) => posSlow = v.FramePosition);
        mixer.Update(2, (ref AudioMixer.Voice v) => posFast = v.FramePosition);
        Assert.True(posFast > posSlow);
    }

    [Fact]
    public void T3_03_AudioAndGraphicsUi_SkillUpStingAndGoldenOverheadFloater()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());

        var world = server.World!;
        var sim = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var playedSounds = new List<string>();
        var origPlay = world.PlaySoundEvent;
        world.PlaySoundEvent = (sound, id, loop, auto) =>
        {
            playedSounds.Add(sound);
            return origPlay?.Invoke(sound, id, loop, auto) ?? true;
        };

        sim[PersonData.CookingHouseBrokenSkill] = 100;
        sim[PersonData.MoneyAmountOverHead] = -10001;
        server.DrainEvents();
        server.Step(1);

        Assert.Contains("sting_char_winner", playedSounds, StringComparer.OrdinalIgnoreCase);
        Assert.Equal((short)0, sim[PersonData.MoneyAmountOverHead]);

        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();
        Assert.Contains(scene.OverheadFeedback ?? [], f =>
            f.Kind == "skillUp" && f.BackgroundRgb == 0xFFCB17u && float.IsFinite(f.X) && float.IsFinite(f.Y));

        var audioState = server.HandleJson("audioState").GetProperty("result");
        Assert.True(audioState.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void T3_04_AudioAndGraphicsUi_PanelModeMusicSwitchingAndClickSfx()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        var liveBtn = OriginalUniversalControl.All.First(c => c.Kind == OriginalUniversalControl.Action.Live);
        var liveCenter = new CGPoint(
            liveBtn.Position.X + liveBtn.Size.Width / 2,
            600 - OriginalSpeedControl.PanelHeight + liveBtn.Position.Y + liveBtn.Size.Height / 2);
        Assert.Equal(OriginalUniversalControl.Action.Live, OriginalUniversalControl.Hit(liveCenter, new CGSize(800, 600), 1.0)?.Kind);

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());

        foreach (var (mode, expectedTrack) in new[]
        {
            ("live", "bkground_live"),
            ("build", "bkground_build"),
            ("buy", "bkground_buy1"),
            ("camera", "bkground_camera"),
            ("options", "bkground_option")
        })
        {
            Assert.True(server.HandleJson("audioEvent", new { name = "ui_click" }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("mode", new { mode }).GetProperty("ok").GetBoolean());
            var musicResp = server.HandleJson("modeMusic", new { mode });
            Assert.True(musicResp.GetProperty("ok").GetBoolean(), musicResp.ToString());
            Assert.Equal(expectedTrack, musicResp.GetProperty("result").GetProperty("name").GetString());
        }

        // Verify bkground_fade parity requirement directly against SoundBank / HitAudioRuntime
        using var audio = E2EHarness.CreateAudioSession(gameDir);
        Assert.True(audio.Runtime.Play("bkground_buy1", -1));
        Assert.True(audio.Runtime.Play("bkground_fade", -1),
            "bkground_fade (mode 9) must resolve and fade/stop active background mode music.");
        Assert.Contains(audio.StopEvents(), _ => true);
    }

    [Fact]
    public void T3_05_AudioAndCas_CreatedFemaleAndChildVoxDemographicRouting()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 9))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 9);
        using var server = E2EHarness.StartServer(profile, house: 0);
        PauseAndDrainDialogs(server);

        var createResp = server.HandleJson("createHousehold", new
        {
            familyName = "VoxPair",
            moveIn = true,
            house = (short)9,
            members = new[]
            {
                new { name = "Elena", code = "fafit", skin = "lgt", personality = DefaultPersonality() },
                new { name = "Timmy", code = "mcchd", skin = "lgt", personality = DefaultPersonality() }
            }
        });
        Assert.True(createResp.GetProperty("ok").GetBoolean(), createResp.ToString());

        Assert.True(server.HandleJson("openLot", new { house = 9 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());

        var world = server.World!;
        var createdSims = world.WalkOrder.OfType<XPerson>().Where(IsHousehold).ToArray();
        var femaleAdult = createdSims.First(p => IsAdult(p) && p[PersonData.Gender] == 1);
        var childSim = createdSims.First(IsChild);

        server.DrainEvents();
        var playHook = world.PlaySoundEvent;
        Assert.NotNull(playHook);
        Assert.True(playHook("vox_laugh_giggle", femaleAdult.Id, false, false));
        Assert.True(playHook("vox_laugh_giggle", childSim.Id, false, false));

        var voxPlays = server.DrainEvents()
            .Where(e => e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
                        e.TryGetProperty("command", out var c) && c.GetString() == "play" &&
                        e.TryGetProperty("group", out var g) && g.GetString() == "VOX")
            .ToArray();
        Assert.Equal(2, voxPlays.Length);
        Assert.Contains(voxPlays, p =>
            p.GetProperty("ownerID").GetInt32() == femaleAdult.Id &&
            p.GetProperty("file").GetString()!.Contains("gigglef", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(voxPlays, p =>
            p.GetProperty("ownerID").GetInt32() == childSim.Id &&
            System.Text.RegularExpressions.Regex.IsMatch(
                p.GetProperty("file").GetString()!, @"(?:giggle|laugh|apprec)[a-z_]*k\d*", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    [Fact]
    public void T3_06_AudioAndTravel_ExpansionSubWorldBuyMusicPlaylists()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 21, 40, 82, 93))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 21, 40, 82, 93);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());

        var traveller = server.World!.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var travellerGuid = traveller.TypeRecord!.Key;
        var famNum = traveller[PersonData.FamilyNumber];

        var destinations = new (short House, string ExpectedBuyTrack, int NghMode, string ExpectedNghMusic)[]
        {
            (21, "station_dtbuy", 2, "station_dtnhood"),
            (40, "station_vacation", 3, "station_vacation"),
            (82, "station_superstar", 5, "station_superstar"),
            (93, "music_magictown", 7, "music_magictown")
        };

        foreach (var (destHouse, expectedBuyTrack, nghMode, expectedNghMusic) in destinations)
        {
            server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(destHouse, travellerGuid, famNum);
            server.BatchRun(1);
            server.DrainEvents();
            Assert.Equal(destHouse, server.State().GetProperty("house").GetInt32());

            var buyResp = server.HandleJson("modeMusic", new { mode = "buy" });
            Assert.True(buyResp.GetProperty("ok").GetBoolean(), buyResp.ToString());
            var buyResult = buyResp.GetProperty("result");
            Assert.Equal(expectedBuyTrack, buyResult.GetProperty("name").GetString());
            Assert.True(buyResult.GetProperty("played").GetBoolean(), $"Expected {expectedBuyTrack} to play on lot {destHouse}.");

            var play1 = server.LastEvents.FirstOrDefault(e =>
                e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
                e.TryGetProperty("command", out var c) && c.GetString() == "play" &&
                e.TryGetProperty("group", out var g) && g.GetString() == "MUSIC");
            Assert.Equal(JsonValueKind.Object, play1.ValueKind);
            Assert.False(play1.GetProperty("loop").GetBoolean());

            var voice1 = play1.GetProperty("voice").GetInt64();
            var file1 = play1.GetProperty("file").GetString();
            Assert.True(server.HandleJson("audioEnded", new { voice = voice1 }).GetProperty("ok").GetBoolean());

            var play2 = server.LastEvents.FirstOrDefault(e =>
                e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
                e.TryGetProperty("command", out var c) && c.GetString() == "play" &&
                e.TryGetProperty("group", out var g) && g.GetString() == "MUSIC");
            Assert.Equal(JsonValueKind.Object, play2.ValueKind);
            Assert.NotEqual(file1, play2.GetProperty("file").GetString());

            var nghResp = server.HandleJson("neighborhood", new { mode = nghMode });
            Assert.True(nghResp.GetProperty("ok").GetBoolean());
            Assert.Equal(expectedNghMusic, nghResp.GetProperty("result").GetProperty("music").GetString());
        }
    }

    [Fact]
    public void T3_07_AudioAndSimAntics_IndoorFireSpatialPanAndFloorMuffle()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());

        var world = server.World!;
        var roomMap = world.LotRoomMaps.GetValueOrDefault(1);
        var indoorObject = world.WalkOrder.FirstOrDefault(o =>
            o is not XPerson &&
            o.Position.Height == 1 &&
            SimulationWorld.BurnSourceOnLot(o) &&
            (roomMap is null || !roomMap.IsOutside(roomMap.RoomAt(o.Position.X >> 4, o.Position.Y >> 4))));
        Assert.NotNull(indoorObject);

        var burnStack = TreeRunner.CreateTransientStack(world, indoorObject);
        var burnCtx = new TreeContext(world, indoorObject, new StackFrame(indoorObject.Id, [0], [0, 0])) { Stack = burnStack };
        // Mode 0, flags=1 (non-strict burn check) ignites the indoor object's tile cleanly
        Assert.Equal(PrimitiveResult.True, BurnPrimitive.Execute(burnCtx, [0, 1, 0, 0, 0, 0, 0, 0]));

        var fireObj = world.WalkOrder.FirstOrDefault(o => o.TypeRecord?.Key == BurnPrimitive.FireType);
        Assert.NotNull(fireObj);

        server.DrainEvents();
        Assert.True(server.HandleJson("audioEvent", new { name = "fire_burning_small", ownerID = (int)fireObj.Id }).GetProperty("ok").GetBoolean());
        var firePlay = server.LastEvents.FirstOrDefault(e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
            e.TryGetProperty("command", out var c) && c.GetString() == "play");
        Assert.Equal(JsonValueKind.Object, firePlay.ValueKind);
        Assert.Equal("FX", firePlay.GetProperty("group").GetString());

        // Verify spatial pan, floor attenuation, and low-pass muffle through PositionalSound + AudioMixer
        Assert.Equal(1.0f, PositionalSound.FloorGain(1, 1));
        Assert.Equal(0.5f, PositionalSound.FloorGain(1, 2));
        Assert.Equal(0.25f, PositionalSound.FloorGain(2, 1));

        var size = new Vector2(1280f, 720f);
        var (leftPan, leftGain) = PositionalSound.Mix(new Vector2(120f, 360f), size, panned: true);
        var (rightPan, rightGain) = PositionalSound.Mix(new Vector2(1160f, 360f), size, panned: true);
        Assert.True(leftPan < -0.5f && rightPan > 0.5f);
        Assert.True(leftGain > 0f && rightGain > 0f);

        var mixer = new AudioMixer();
        var pcm = new float[1024 * 2];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = 0.25f;
        mixer.SetMuffle(0.45f, 0.2f);
        mixer.Set(10, new AudioMixer.Voice(pcm) { Outdoors = true, Loop = true, Left = 0.5f, Right = 0.5f });
        mixer.MixAndPut(nint.Zero, 128);
        float lowLeft = 0f;
        mixer.Update(10, (ref AudioMixer.Voice v) => lowLeft = v.LowLeft);
        Assert.True(float.IsFinite(lowLeft) && lowLeft > 0f);
    }

    [Fact]
    public void T3_08_AudioAndBuildSave_FourChannelVolumeAndTvStereoPersistence()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        uint placedTvGuid;
        short placedTvId;

        using (var server1 = E2EHarness.StartServer(profile, house: 5))
        {
            Assert.True(server1.HandleJson("audioVolume", new { group = "FX", volume = 0.25f }).GetProperty("ok").GetBoolean());
            Assert.True(server1.HandleJson("audioVolume", new { group = "MUSIC", volume = 0.60f }).GetProperty("ok").GetBoolean());
            Assert.True(server1.HandleJson("audioVolume", new { group = "VOX", volume = 0.80f }).GetProperty("ok").GetBoolean());
            Assert.True(server1.HandleJson("audioVolume", new { group = "AMBIENCE", volume = 0.10f }).GetProperty("ok").GetBoolean());
            Assert.True(server1.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());

            PauseAndDrainDialogs(server1);
            var world = server1.World!;
            var (px, py) = FindOpenFlatPatch(profile.UserData, world, 5, patchSize: 3);

            var tvCandidates = world.ObjectRegistry!.Entries
                .Where(e => e.Definition[ObjdField.Disabled] == 0 &&
                            e.Definition[ObjdField.RoomSort] != 0 &&
                            e.Definition[ObjdField.BuildModeType] == 0 &&
                            e.Definition[ObjdField.MasterId] == 0 &&
                            ObjectCatalog.TryGetEntry(e, 1, out var cat) &&
                            cat is { Price: > 0 } &&
                            (cat.Name.Contains("Television", StringComparison.OrdinalIgnoreCase) ||
                             e.Chunk.Label.Contains("TV", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(e => e.Definition[ObjdField.Price])
                .ToList();
            Assert.NotEmpty(tvCandidates);

            placedTvGuid = 0;
            placedTvId = 0;
            foreach (var tv in tvCandidates)
            {
                var buyResp = server1.HandleJson("buyObject", new
                {
                    guid = tv.Guid,
                    x = (px + 1) * 16 + 8,
                    y = (py + 1) * 16 + 8,
                    level = 1,
                    direction = (byte)1
                });
                if (buyResp.GetProperty("ok").GetBoolean())
                {
                    placedTvGuid = tv.Guid;
                    placedTvId = buyResp.GetProperty("result").GetProperty("objectID").GetInt16();
                    break;
                }
            }
            Assert.True(placedTvId > 0, "Expected at least one catalog TV to place onto open flat patch.");

            Assert.True(server1.HandleJson("audioEvent", new { name = "tv_action_norm", ownerID = (int)placedTvId }).GetProperty("ok").GetBoolean());
            Assert.True(server1.HandleJson("audioEvent", new { name = "stereo_classical", ownerID = (int)placedTvId }).GetProperty("ok").GetBoolean());
            Assert.True(server1.HandleJson("save", new { slot = "t3-08", lot = true }).GetProperty("ok").GetBoolean());
            Assert.False(server1.Handle("""{"id":"q1","op":"quit"}"""));
        }

        using (var server2 = E2EHarness.StartServer(profile, house: 5))
        {
            var loadResp = server2.HandleJson("load", new { slot = "t3-08" });
            Assert.True(loadResp.GetProperty("ok").GetBoolean(), loadResp.ToString());

            var vols = server2.HandleJson("audioState").GetProperty("result").GetProperty("volumes");
            Assert.Equal(0.25f, vols.GetProperty("FX").GetSingle(), 3);
            Assert.Equal(0.60f, vols.GetProperty("MUSIC").GetSingle(), 3);
            Assert.Equal(0.80f, vols.GetProperty("VOX").GetSingle(), 3);
            Assert.Equal(0.10f, vols.GetProperty("AMBIENCE").GetSingle(), 3);

            var reloadedObjs = server2.State().GetProperty("objects").EnumerateArray().ToArray();
            Assert.Contains(reloadedObjs, o => o.GetProperty("guid").GetUInt32() == placedTvGuid);

            Assert.True(server2.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());
            var tvResp = server2.HandleJson("audioEvent", new { name = "tv_action_norm", ownerID = (int)placedTvId });
            var stResp = server2.HandleJson("audioEvent", new { name = "stereo_classical", ownerID = (int)placedTvId });
            Assert.True(tvResp.GetProperty("result").GetProperty("played").GetBoolean());
            Assert.True(stResp.GetProperty("result").GetProperty("played").GetBoolean());
        }
    }

    // -------------------------------------------------------------------------
    // Cluster 2: Anim <-> GraphicsUI / CAS / Travel / SimAntics / BuildSave (T3-09 .. T3-14)
    // -------------------------------------------------------------------------

    [Fact]
    public void T3_09_AnimAndGraphicsUi_CensorMosaicAndDressUndressCutaway()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        Assert.True(server.HandleJson("walls", new { mode = "cutaway", x = 32, y = 32 }).GetProperty("ok").GetBoolean());
        var world = server.World!;
        var sim = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var pb = new AvatarAnimationPlayback(new AvatarAnimationClip("t3-09-censor", 10, []), false, false, sim.AnimatorMilliseconds);

        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(sim, pb, new AvatarAnimationSoundEvent(0, "", "undress", "1", false, false)));
        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(sim, pb, new AvatarAnimationSoundEvent(0, "", "censor", "127", false, false)));
        Assert.Equal((short)127, sim[PersonData.CensorshipFlags]);

        Assert.True(server.HandleJson("scene").GetProperty("ok").GetBoolean());
        var (censoredScene, _) = LotScene.Load(server.LatestScenePath());
        censoredScene.Validate();
        Assert.Equal("cutaway", censoredScene.WallMode);
        Assert.NotNull(censoredScene.CensorRects);
        Assert.NotEmpty(censoredScene.CensorRects!);
        Assert.All(censoredScene.CensorRects!, r => Assert.True(r.Width > 0f && r.Height > 0f));

        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(sim, pb, new AvatarAnimationSoundEvent(0, "", "censor", "0", false, false)));
        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(sim, pb, new AvatarAnimationSoundEvent(0, "", "dress", "1", false, false)));
        Assert.Equal((short)0, sim[PersonData.CensorshipFlags]);

        Assert.True(server.HandleJson("scene").GetProperty("ok").GetBoolean());
        var (dressedScene, _) = LotScene.Load(server.LatestScenePath());
        dressedScene.Validate();
        Assert.Empty(dressedScene.CensorRects ?? []);
    }

    [Fact]
    public void T3_10_AnimAndGraphicsUi_CriticalMotivePulseAndLastFadeOut()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var household = world.WalkOrder.OfType<XPerson>().Where(IsHousehold).ToArray();
        Assert.True(household.Length >= 2);
        var simA = household[0];
        var simB = household[1];

        Assert.True(server.HandleJson("selectSim", new { objectID = simA.Id }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("setMotive", new { objectID = simA.Id, motive = "Energy", value = -90f }).GetProperty("ok").GetBoolean());

        var energyBar = new OriginalGameplayLayout.MotiveBarState(-90);
        energyBar.Reset(-90, nowMs: 1000, effectsEnabled: true);
        var pulseA = energyBar.Evaluate(1150);
        var pulseB = energyBar.Evaluate(1450);
        Assert.True(pulseA.IsAnimating);
        Assert.NotEqual(pulseA.RedRgb, pulseB.RedRgb);

        var cookClip = ResolveRealClip(gameDir, world.ObjectRegistry, "a2o-stove-cook");
        var eatClip = ResolveRealClip(gameDir, world.ObjectRegistry, "a2o-eatloop-stand");
        simA.AnimationPlayback = new AvatarAnimationPlayback(cookClip, false, false, simA.AnimatorMilliseconds);
        for (int i = 0; i < 6; i++)
            world.TickAvatarAnimations(simA);

        world.StopCurrentAnimation(simA);
        Assert.NotNull(simA.LastAnimationPlayback);
        simA.AnimationPlayback = new AvatarAnimationPlayback(eatClip, false, true, simA.AnimatorMilliseconds);
        world.TickAvatarAnimations(simA);

        var order = simA.GetAnimationPracticeApplicationOrder();
        Assert.Contains(order, p => p.Clip.Name.Equals("a2o-stove-cook", StringComparison.OrdinalIgnoreCase) && p.PracticeWeight > 0f);
        Assert.Contains(order, p => p.Clip.Name.Equals("a2o-eatloop-stand", StringComparison.OrdinalIgnoreCase));

        var geometry = AvatarSceneGeometry.For(gameDir, world.ObjectRegistry!);
        AssertFiniteParts(geometry.Build(simA, 1, 0, (_, _) => 0f, new SimLotCamera(0, 64, 64)));

        var selB = server.HandleJson("selectSim", new { objectID = simB.Id });
        Assert.True(selB.GetProperty("ok").GetBoolean());
        Assert.Equal(simB.Id, selB.GetProperty("result").GetProperty("selectedSim").GetInt16());
    }

    [Fact]
    public void T3_11_AnimAndCas_DownloadsSkinWardrobeChangePortraitAndCarryProp()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        string? tempRoot = null;
        try
        {
            var customGame = CreateIsolatedGameDirWithDownloads(gameDir, downloads =>
            {
                WriteSyntheticSkinPack(
                    Path.Combine(downloads, "CustomSkinOnly"),
                    "b904fafitlgt_t311.bmp",
                    "b904fafit_t311.cmx",
                    "xskin-b904fafit_t311-PELVIS-BODY.skn",
                    "b904fafit_t311",
                    "xskin-b904fafit_t311-PELVIS-BODY",
                    "b904fafitlgt_t311");
            }, out tempRoot);

            var skins = CharacterSearchPath.Skins(customGame, new ObjectRegistry());
            Assert.Contains(skins.Files, f => f.Name.Equals("b904fafit_t311.cmx", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (tempRoot is not null)
                E2EHarness.DeleteTempTree(tempRoot);
        }

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var registry = world.ObjectRegistry!;
        var bella = world.WalkOrder.OfType<XPerson>().First(p => IsHousehold(p) && IsAdult(p));
        var guid = unchecked((uint)bella.TypeRecord!.Key);
        var entry = registry.Find(guid)!;
        var beforeBmp = entry.File.Find(ResourceType.Parse("BMP_"), 2002)!.Data.ToArray();

        var bodyActions = WardrobeSuitService.GetActionList(world, bella, null, 0, headsList: false)!;
        var currentBody = bella.SavedCharacter?.Parts[0].Item1 ?? "";
        var altBody = bodyActions.Entries.First(e => !e.First.Equals(currentBody, StringComparison.OrdinalIgnoreCase));

        var scope = InteractionBehaviorScope.Initialize();
        var ctx = WardrobeSuitService.BeginPersonScope(world, bella, scope);
        ctx.SetString(0x02, InteractionBehaviorActionQueryBuilder.BuildSelectedActionUpdate(altBody));
        WardrobeSuitService.CommitContext(registry, entry, world.Language, ctx, bella);
        world.DispatchNotice(0x20000000, 0);
        server.DrainEvents();

        var afterBmp = registry.Find(guid)!.File.Find(ResourceType.Parse("BMP_"), 2002)!.Data.ToArray();
        Assert.False(beforeBmp.SequenceEqual(afterBmp));

        var carryClip = ResolveRealClip(gameDir, registry, "a2o-rarm-carry-loop");
        bella.CarryAnimationPlayback = new AvatarAnimationPlayback(carryClip, false, true, bella.AnimatorMilliseconds);
        world.TickAvatarAnimations(bella);

        var geometry = AvatarSceneGeometry.For(gameDir, registry);
        var parts = geometry.Build(bella, 1, 0, (_, _) => 0f, new SimLotCamera(0, 64, 64));
        AssertFiniteParts(parts);
        var hand = geometry.HandPosition(bella);
        Assert.True(hand is { } h && float.IsFinite(h.X) && float.IsFinite(h.Y) && float.IsFinite(h.Z));
    }

    [Fact]
    public void T3_12_AnimAndTravel_LastFadeOutAcrossDowntownTripAndSuspendedHome()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 21))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 21);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var homeWorld = server.World!;
        var traveller = homeWorld.WalkOrder.OfType<XPerson>().First(p => IsHousehold(p) && IsAdult(p));
        var travellerGuid = traveller.TypeRecord!.Key;
        var famNum = traveller[PersonData.FamilyNumber];

        var walkClip = ResolveRealClip(gameDir, homeWorld.ObjectRegistry, "a2o-walking-female-loop");
        traveller.AnimationPlayback = new AvatarAnimationPlayback(walkClip, false, true, traveller.AnimatorMilliseconds);
        for (int i = 0; i < 6; i++)
            homeWorld.TickAvatarAnimations(traveller);
        homeWorld.StopCurrentAnimation(traveller);
        Assert.NotNull(traveller.LastAnimationPlayback);

        // Travel to Downtown 21 while fade-out was active
        homeWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, travellerGuid, famNum);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(21, server.State().GetProperty("house").GetInt32());

        var awayTraveller = server.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == travellerGuid);
        var awayMilsBefore = awayTraveller.AnimatorMilliseconds;
        server.Step(5);
        Assert.True(awayTraveller.AnimatorMilliseconds > awayMilsBefore);
        AssertFiniteParts(AvatarSceneGeometry.For(gameDir, server.World.ObjectRegistry!)
            .Build(awayTraveller, 1, 0, (_, _) => 0f, new SimLotCamera(0, 64, 64)));

        // Return home -> SuspendedHome restores cleanly with non-frozen animations
        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, travellerGuid, famNum);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(5, server.State().GetProperty("house").GetInt32());

        var homeTraveller = server.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == travellerGuid);
        var homeMilsBefore = homeTraveller.AnimatorMilliseconds;
        server.Step(10);
        Assert.True(homeTraveller.AnimatorMilliseconds > homeMilsBefore);
        AssertFiniteParts(AvatarSceneGeometry.For(gameDir, server.World.ObjectRegistry!)
            .Build(homeTraveller, 1, 0, (_, _) => 0f, new SimLotCamera(0, 64, 64)));
    }

    [Fact]
    public void T3_13_AnimAndSimAntics_DeficientClipSubstituteEventsAndSpecialEffect()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var sim = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var getFood = ResolveRealClip(gameDir, world.ObjectRegistry, "a2o-fridge-getfood");
        world.AvatarAnimationTable[(1, false, 42)] = getFood;

        var stack = TreeRunner.CreateTransientStack(world, sim);
        var frame = new StackFrame(sim.Id, [0], [0, 0]);
        var ctx = new TreeContext(world, sim, frame) { Stack = stack };
        var operand = AnimateSimOperand(42, source: 1, flags: 0, expectedEvents: 3);

        var fired = new List<short>();
        var result = AnimateSimPrimitive.Execute(ctx, operand);
        for (int tick = 0; tick < 300 && result != PrimitiveResult.True; tick++)
        {
            world.TickAvatarAnimations(sim);
            result = AnimateSimPrimitive.Execute(ctx, operand);
            if (result == PrimitiveResult.False)
                fired.Add(frame.Parameters[0]);
        }

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal(new short[] { 0, 1, 2 }, fired);

        var fxFrame = new StackFrame(sim.Id, [0], [0, 0]);
        var fxCtx = new TreeContext(world, sim, fxFrame) { Stack = stack };
        var fxOp = new byte[] { 2, 0, 0, 0, 0, 0 };
        Assert.Equal(PrimitiveResult.Stop, SpecialEffectPrimitive.Execute(fxCtx, fxOp));
        Assert.Equal(PrimitiveResult.Stop, SpecialEffectPrimitive.Execute(fxCtx, fxOp));
        Assert.NotEmpty(world.HeadlessSpecialEffects.Dispatches);
    }

    [Fact]
    public void T3_14_AnimAndBuildSave_MidFadeAndCarrySaveReloadIntoBuyBuild()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var sim = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var cookClip = ResolveRealClip(gameDir, world.ObjectRegistry, "a2o-stove-cook");
        var carryClip = ResolveRealClip(gameDir, world.ObjectRegistry, "a2o-rarm-carry-loop");

        sim.AnimationPlayback = new AvatarAnimationPlayback(cookClip, false, false, sim.AnimatorMilliseconds);
        sim.CarryAnimationPlayback = new AvatarAnimationPlayback(carryClip, false, true, sim.AnimatorMilliseconds);
        for (int i = 0; i < 6; i++)
            world.TickAvatarAnimations(sim);
        world.StopCurrentAnimation(sim);
        Assert.NotNull(sim.LastAnimationPlayback);

        Assert.True(server.HandleJson("save", new { slot = "t3-14", lot = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("load", new { slot = "t3-14" }).GetProperty("ok").GetBoolean());

        PauseAndDrainDialogs(server);
        Assert.True(server.HandleJson("mode", new { mode = "build" }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("mode", new { mode = "buy" }).GetProperty("ok").GetBoolean());

        var reloadedWorld = server.World!;
        var (px, py) = FindOpenFlatPatch(profile.UserData, reloadedWorld, 5, patchSize: 3);
        var cheapObj = reloadedWorld.ObjectRegistry!.Entries
            .Where(e => e.Definition[ObjdField.Disabled] == 0 &&
                        e.Definition[ObjdField.RoomSort] != 0 &&
                        e.Definition[ObjdField.BuildModeType] == 0 &&
                        e.Definition[ObjdField.MasterId] == 0 &&
                        ObjectCatalog.TryGetEntry(e, 1, out var cat) && cat is { Price: > 0 and <= 500 })
            .OrderBy(e => e.Definition[ObjdField.Price])
            .First();

        var buyResp = server.HandleJson("buyObject", new
        {
            guid = cheapObj.Guid,
            x = (px + 1) * 16 + 8,
            y = (py + 1) * 16 + 8,
            level = 1,
            direction = (byte)1
        });
        Assert.True(buyResp.GetProperty("ok").GetBoolean(), buyResp.ToString());

        var reloadedSim = reloadedWorld.WalkOrder.OfType<XPerson>().First(IsHousehold);
        Assert.NotNull(reloadedSim.CarryAnimationPlayback);
        Assert.Equal(carryClip.Name, reloadedSim.CarryAnimationPlayback!.Clip.Name);

        Assert.True(server.HandleJson("mode", new { mode = "live" }).GetProperty("ok").GetBoolean());
        server.Step(5);

        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();
        Assert.NotNull(scene.Meshes);
        Assert.Contains(scene.Meshes!, m => m.ObjectID == reloadedSim.Id);

        var geometry = AvatarSceneGeometry.For(gameDir, reloadedWorld.ObjectRegistry!);
        var camera = new SimLotCamera(0, 64, 64);
        var carryParts = geometry.Build(reloadedSim, 1, 0, (_, _) => 0f, camera);
        AssertFiniteParts(carryParts);

        // Stop carry via real SimAntics AnimateSim (mode 2, id 0) rather than manual null assignment.
        var carryStopStack = TreeRunner.CreateTransientStack(reloadedWorld, reloadedSim);
        var carryStopFrame = new StackFrame(reloadedSim.Id, [0], [0, 0]);
        var carryStopCtx = new TreeContext(reloadedWorld, reloadedSim, carryStopFrame) { Stack = carryStopStack };
        var stopCarryOperand = new byte[] { 0, 0, 0, 0, 0, 0x10, 0, 0 }; // id=0, source=0, flags=0x10 (mode 2: stop carry)
        Assert.Equal(PrimitiveResult.True, AnimateSimPrimitive.Execute(carryStopCtx, stopCarryOperand));
        reloadedWorld.TickAvatarAnimations(reloadedSim);
        Assert.Null(reloadedSim.CarryAnimationPlayback);

        var postStopCarryParts = geometry.Build(reloadedSim, 1, 0, (_, _) => 0f, camera);
        AssertFiniteParts(postStopCarryParts);
        static float MaxVertexDelta(IReadOnlyList<AvatarSceneGeometry.Part> a, IReadOnlyList<AvatarSceneGeometry.Part> b)
        {
            float maxDelta = 0f;
            int count = Math.Min(a.Count, b.Count);
            for (int p = 0; p < count; p++)
            {
                var va = a[p].Vertices;
                var vb = b[p].Vertices;
                int vCount = Math.Min(va.Length, vb.Length);
                for (int i = 0; i < vCount; i++)
                {
                    float d = Vector3.Distance(
                        new Vector3(va[i].X, va[i].Y, va[i].Z),
                        new Vector3(vb[i].X, vb[i].Y, vb[i].Z));
                    if (d > maxDelta) maxDelta = d;
                }
            }
            return maxDelta;
        }
        Assert.True(MaxVertexDelta(carryParts, postStopCarryParts) > 0.001f);
    }

    // -------------------------------------------------------------------------
    // Cluster 3: GraphicsUI <-> CAS / Travel / SimAntics / BuildSave (T3-15 .. T3-18)
    // -------------------------------------------------------------------------

    [Fact]
    public void T3_15_GraphicsUiAndCas_CreatedFamilyCheatConsoleFundsAndMagiCoins()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 9))
            return;

        var content = new GameContent(gameDir);
        var font = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
        var console = new OriginalCheatConsoleOverlay(font, new UIDevice());

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 9);
        using var server = E2EHarness.StartServer(profile, house: 0);
        PauseAndDrainDialogs(server);

        var createResp = server.HandleJson("createHousehold", new
        {
            familyName = "CheatFam",
            moveIn = true,
            house = (short)9,
            members = new[]
            {
                new { name = "Victor", code = "mafit", skin = "med", personality = DefaultPersonality() }
            }
        });
        Assert.True(createResp.GetProperty("ok").GetBoolean(), createResp.ToString());
        Assert.True(server.HandleJson("openLot", new { house = 9 }).GetProperty("ok").GetBoolean());

        var initialBudget = server.State().GetProperty("budget").GetInt32();
        Assert.Equal(16500, initialBudget);

        console.Open(nowSeconds: 1.0);
        console.TextInput("rosebud;!;!;!");
        Assert.True(console.TryPrepareSegments(out var segments, out _, out _));
        Assert.Equal(["rosebud", "rosebud", "rosebud", "rosebud"], segments);

        var cheatResp = server.HandleJson("cheat", new { code = "rosebud;!;!;!" });
        Assert.True(cheatResp.GetProperty("ok").GetBoolean(), cheatResp.ToString());
        Assert.Equal(initialBudget + 4000, server.State().GetProperty("budget").GetInt32());

        var magicResp = server.HandleJson("cheat", new { code = "magic_tokens" });
        Assert.True(magicResp.GetProperty("ok").GetBoolean(), magicResp.ToString());

        var inv = server.State().GetProperty("sims")[0].GetProperty("inventory").EnumerateArray().ToArray();
        Assert.Contains(inv, item => item.GetProperty("guid").GetUInt32() == NeighborhoodExport.MagiCoinTokenGuid &&
                                     item.GetProperty("count").GetInt32() > 0);
    }

    [Fact]
    public void T3_16_GraphicsUiAndTravel_SnowyVacationEdgeScrollAutoCenterAndCamera()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 40))
            return;

        var playOptions = new OriginalOptionsPanelOverlay.PlayOptionsState
        {
            EdgeScrolling = true,
            AutoCenter = true
        };
        var roundTrip = JsonSerializer.Deserialize<OriginalOptionsPanelOverlay.PlayOptionsState>(
            JsonSerializer.Serialize(playOptions));
        Assert.True(roundTrip.EdgeScrolling && roundTrip.AutoCenter);

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 40);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var traveller = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        world.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(40, traveller.TypeRecord!.Key, traveller[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(40, server.State().GetProperty("house").GetInt32());

        var awaySims = server.State().GetProperty("sims").EnumerateArray()
            .Where(s => s.GetProperty("household").GetBoolean())
            .ToArray();
        Assert.Single(awaySims);

        Assert.True(server.HandleJson("cameraOrientation", new { value = 2 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("zoom", new { scale = 1 }).GetProperty("ok").GetBoolean());

        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();
        Assert.NotNull(scene.TerrainVertices);
        Assert.NotEmpty(scene.TerrainVertices!);
        Assert.NotNull(scene.Meshes);
        Assert.NotEmpty(scene.Meshes!);
    }

    [Fact]
    public void T3_17_GraphicsUiAndSimAntics_WagesRelationshipAndBalloonHeadline()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var household = world.WalkOrder.OfType<XPerson>().Where(IsHousehold).ToArray();
        Assert.True(household.Length >= 2);
        var simA = household[0];
        var simB = household[1];

        var budgetBefore = server.State().GetProperty("budget").GetInt32();
        world.FamilyBudget!.AlterFamilyBudget(0, -500);
        simA[PersonData.MoneyAmountOverHead] = 500;

        // Seed relationship baseline then apply +30 positive delta -> "++"
        var relFrame = new StackFrame(simA.Id, new short[8], [0, 1]) { StackObject = simB.Id };
        var relCtx = new TreeContext(world, simA, relFrame);
        var relOp = new byte[] { 0, 0, 4, 0, (byte)DataOwner.Temporary, 0, 0, 0 };
        simA.Temporaries[0] = 15;
        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(relCtx, relOp));
        world.ClearOverheadFeedback();
        simA.Temporaries[0] = 45;
        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(relCtx, relOp));

        // Display thought/speech balloon headline on simA
        var balloonCtx = new TreeContext(world, simA, new StackFrame(simA.Id, new short[8], [0, 1]));
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(balloonCtx, [0, 0, 1, 1, 60, 0, 2, 1]));

        server.Step(1);
        Assert.Equal(budgetBefore + 500, server.State().GetProperty("budget").GetInt32());

        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();
        var feedback = scene.OverheadFeedback ?? [];
        Assert.Contains(feedback, f => f.Kind == "currency" && f.Text == "+$500");
        Assert.Contains(feedback, f => f.Kind == "relationship" && f.Text == "++" && f.Delta > 0);
        Assert.Contains(scene.Sprites, s =>
            s.ObjectID == simA.Id &&
            scene.Textures[s.Texture].File.StartsWith("runtime-slot-", StringComparison.Ordinal));
    }

    [Fact]
    public void T3_18_GraphicsUiAndBuildSave_MoveObjectsCheatInUseMoveAndPersist()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        PauseAndDrainDialogs(server);

        var world = server.World!;
        var target = world.WalkOrder.FirstOrDefault(o =>
            o is not XPerson &&
            o.MultiTileMasterId == 0 &&
            o.Position.Height == 1 &&
            world.CanPickUpObject(o) &&
            o.TypeRecord is not null);
        Assert.NotNull(target);

        var (destX, destY) = FindOpenFlatPatch(profile.UserData, world, 5, patchSize: 3);
        var newX = (destX + 1) * 16 + 8;
        var newY = (destY + 1) * 16 + 8;

        // Mark object in use -> moveObject must be refused while move_objects is off
        target[ObjectData.UseCount] = 1;
        var blockedMove = server.HandleJson("moveObject", new { objectID = target.Id, x = newX, y = newY, level = 1, direction = (byte)1 });
        Assert.False(blockedMove.GetProperty("ok").GetBoolean());

        var onResp = server.HandleJson("cheat", new { code = "move_objects on" });
        Assert.True(onResp.GetProperty("ok").GetBoolean(), onResp.ToString());
        Assert.True(onResp.GetProperty("result").GetProperty("moveObjects").GetBoolean());
        Assert.True(world.MoveObjects);

        var allowedMove = server.HandleJson("moveObject", new { objectID = target.Id, x = newX, y = newY, level = 1, direction = (byte)1 });
        Assert.True(allowedMove.GetProperty("ok").GetBoolean(), allowedMove.ToString());
        target[ObjectData.UseCount] = 0;

        Assert.True(server.HandleJson("save", new { slot = "t3-18", lot = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("load", new { slot = "t3-18" }).GetProperty("ok").GetBoolean());

        var reloaded = server.World!.ObjectById(target.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(newX, reloaded.Position.X);
        Assert.Equal(newY, reloaded.Position.Y);

        var offResp = server.HandleJson("cheat", new { code = "move_objects off" });
        Assert.True(offResp.GetProperty("ok").GetBoolean());
        Assert.False(offResp.GetProperty("result").GetProperty("moveObjects").GetBoolean());
        Assert.False(server.World.MoveObjects);
    }

    // -------------------------------------------------------------------------
    // Cluster 4: CAS <-> Travel <-> SimAntics <-> BuildSave (T3-19 .. T3-24)
    // -------------------------------------------------------------------------

    [Fact]
    public void T3_19_CasAndTravel_CustomSkinWardrobePortraitAndSoloDowntownTrip()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 9, 21))
            return;

        string? tempRoot = null;
        try
        {
            var customGame = CreateIsolatedGameDirWithDownloads(gameDir, downloads =>
            {
                WriteSyntheticSkinPack(
                    Path.Combine(downloads, "TravelSkinPack"),
                    "b905fafitlgt_t319.bmp",
                    "b905fafit_t319.cmx",
                    "xskin-b905fafit_t319-PELVIS-BODY.skn",
                    "b905fafit_t319",
                    "xskin-b905fafit_t319-PELVIS-BODY",
                    "b905fafitlgt_t319");
            }, out tempRoot);

            var catalog = HouseholdAppearance.Catalog(customGame, new ObjectRegistry(), "f", "adult", "lgt");
            var customBody = catalog.Bodies.First(b => b.Id.Contains("b905fafit_t319", StringComparison.OrdinalIgnoreCase));

            using var profile = E2EHarness.CreateIsolatedProfile(customGame, 9, 21);
            using var server = E2EHarness.StartServer(profile, house: 0);
            PauseAndDrainDialogs(server);

            var createResp = server.HandleJson("createHousehold", new
            {
                familyName = "TravelPair",
                moveIn = true,
                house = (short)9,
                members = new object[]
                {
                    new
                    {
                        name = "Sienna",
                        code = "fafit",
                        skin = "lgt",
                        headChoice = catalog.Heads[0].Id,
                        bodyChoice = customBody.Id,
                        personality = DefaultPersonality()
                    },
                    new
                    {
                        name = "Marcus",
                        code = "mafit",
                        skin = "med",
                        personality = DefaultPersonality()
                    }
                }
            });
            Assert.True(createResp.GetProperty("ok").GetBoolean(), createResp.ToString());
            Assert.True(server.HandleJson("openLot", new { house = 9 }).GetProperty("ok").GetBoolean());

            var homeWorld = server.World!;
            var sims = homeWorld.WalkOrder.OfType<XPerson>().Where(IsHousehold).ToArray();
            var simA = sims.First(s => s[PersonData.Gender] == 1);
            var simB = sims.First(s => s[PersonData.Gender] == 0);
            var simAGuid = simA.TypeRecord!.Key;
            var simBGuid = simB.TypeRecord!.Key;
            var famNum = simA[PersonData.FamilyNumber];

            // In-lot head makeover refreshes portrait prior to solo travel
            var registry = homeWorld.ObjectRegistry!;
            var entryA = registry.Find(unchecked((uint)simAGuid))!;
            var beforePortrait = server.State().GetProperty("sims").EnumerateArray()
                .First(s => s.GetProperty("id").GetInt16() == simA.Id)
                .GetProperty("portrait").GetProperty("file").GetString();

            var headActions = WardrobeSuitService.GetActionList(homeWorld, simA, null, 0, headsList: true)!;
            var currentHead = simA.SavedCharacter?.Parts[4].Item1 ?? "";
            var currentHeadBase = currentHead.EndsWith(".apr", StringComparison.OrdinalIgnoreCase) ? currentHead[..^4] : currentHead;
            var currentHeadTex = simA.SavedCharacter?.Parts[4].Item2 ?? "";
            var altHead = headActions.Entries.First(e =>
                !e.First.Equals(currentHeadBase, StringComparison.OrdinalIgnoreCase) &&
                !e.First.Equals(currentHead, StringComparison.OrdinalIgnoreCase) &&
                !e.Second.Equals(currentHeadTex, StringComparison.OrdinalIgnoreCase));
            var scope = InteractionBehaviorScope.Initialize();
            var ctx = WardrobeSuitService.BeginPersonScope(homeWorld, simA, scope);
            ctx.SetString(0x03, InteractionBehaviorActionQueryBuilder.BuildSelectedActionUpdate(altHead));
            WardrobeSuitService.CommitContext(registry, entryA, homeWorld.Language, ctx, simA);
            homeWorld.DispatchNotice(0x20000000, 0);
            server.DrainEvents();

            var refreshedPortrait = server.State().GetProperty("sims").EnumerateArray()
                .First(s => s.GetProperty("id").GetInt16() == simA.Id)
                .GetProperty("portrait").GetProperty("file").GetString();
            Assert.NotEqual(beforePortrait, refreshedPortrait);

            // Solo trip to Downtown 21
            homeWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, simAGuid, famNum);
            server.BatchRun(1);
            server.DrainEvents();

            var awayWorld = server.World!;
            Assert.Equal([simAGuid], awayWorld.ActivePersonRecordKeys);
            Assert.DoesNotContain(awayWorld.WalkOrder.OfType<XPerson>(), p => p.TypeRecord?.Key == simBGuid);

            var awaySims = server.State().GetProperty("sims").EnumerateArray()
                .Where(s => s.GetProperty("household").GetBoolean())
                .ToArray();
            var awaySimA = Assert.Single(awaySims);
            Assert.Equal(refreshedPortrait, awaySimA.GetProperty("portrait").GetProperty("file").GetString());

            // Return home restores full household
            awayWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, simAGuid, famNum);
            server.BatchRun(1);
            server.DrainEvents();
            Assert.Contains(simAGuid, server.World!.ActivePersonRecordKeys);
            Assert.Contains(simBGuid, server.World.ActivePersonRecordKeys);
        }
        finally
        {
            if (tempRoot is not null)
                E2EHarness.DeleteTempTree(tempRoot);
        }
    }

    [Fact]
    public void T3_20_CasAndSimAntics_PersonalityTraitsZodiacAndAutonomousScoring()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 9))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 9);
        using var server = E2EHarness.StartServer(profile, house: 0);
        PauseAndDrainDialogs(server);

        var p1 = new[] { 1000, 200, 0, 500, 800 };
        var p2 = new[] { 0, 1000, 500, 1000, 0 };
        var createResp = server.HandleJson("createHousehold", new
        {
            familyName = "TraitsFam",
            moveIn = true,
            house = (short)9,
            members = new[]
            {
                new { name = "NeatSim", code = "fafit", skin = "lgt", personality = p1 },
                new { name = "PlaySim", code = "mafit", skin = "med", personality = p2 }
            }
        });
        Assert.True(createResp.GetProperty("ok").GetBoolean(), createResp.ToString());
        Assert.True(server.HandleJson("openLot", new { house = 9 }).GetProperty("ok").GetBoolean());
        PauseAndDrainDialogs(server);

        var sims = server.State().GetProperty("sims").EnumerateArray()
            .Where(s => s.GetProperty("household").GetBoolean())
            .ToArray();
        Assert.Equal(2, sims.Length);

        var neatJson = sims.First(s => s.GetProperty("name").GetString() == "NeatSim");
        var playJson = sims.First(s => s.GetProperty("name").GetString() == "PlaySim");
        Assert.Equal(1000, neatJson.GetProperty("personality").GetProperty("Neat").GetInt32());
        Assert.Equal(0, neatJson.GetProperty("personality").GetProperty("Active").GetInt32());
        Assert.Equal(1000, playJson.GetProperty("personality").GetProperty("Playful").GetInt32());
        Assert.Equal(1000, playJson.GetProperty("personality").GetProperty("Outgoing").GetInt32());

        var neatZodiac = neatJson.GetProperty("zodiac").GetInt32();
        var playZodiac = playJson.GetProperty("zodiac").GetInt32();
        var neatId = checked((short)neatJson.GetProperty("id").GetInt32());
        var playId = checked((short)playJson.GetProperty("id").GetInt32());

        var world = server.World!;
        var registry = world.ObjectRegistry!;
        var globalLib = GlobalLibrary.Open(gameDir);
        var neatSim = world.WalkOrder.OfType<XPerson>().First(p => p.Id == neatId);
        var playSim = world.WalkOrder.OfType<XPerson>().First(p => p.Id == playId);

        // Set motives to -50 so AdultScoreCurves (Global.iff STR# 501) are on active slopes above FamilyMinimum.
        foreach (var sim in new[] { neatSim, playSim })
        {
            foreach (var motiveName in new[] { "Hunger", "Comfort", "Hygiene", "Bladder", "Energy", "Fun", "Social" })
            {
                Assert.True(server.HandleJson("setMotive", new { objectID = sim.Id, motive = motiveName, value = -50f }).GetProperty("ok").GetBoolean());
            }
        }

        float PreviewScore(XPerson sim, Interaction interaction)
        {
            var motives = AutonomyMotiveCatalog.All;
            float baseline = 0f, advertised = 0f;
            foreach (var mIdx in motives)
            {
                var cur = sim.Motives[mIdx];
                baseline += AutonomyMotiveCatalog.EvaluateCurve(world.PersonMotiveDecayTuning, sim, mIdx, cur);
                float change = 0f;
                if (mIdx < interaction.Motives.Count)
                {
                    var m = interaction.Motives[mIdx];
                    if (m.Personality == 0)
                    {
                        change = m.Delta + m.Minimum;
                    }
                    else
                    {
                        var (field, invert) = m.Personality switch
                        {
                            1 => (2, false), 2 => (2, true),
                            3 => (3, false), 4 => (3, true),
                            5 => (4, false), 6 => (4, true),
                            7 => (5, false), 8 => (5, true),
                            9 => (6, false), 10 => (6, true),
                            11 => (7, false), 12 => (7, true),
                            _ => (0, false)
                        };
                        var traitVal = field == 0 ? 0 : (invert ? 1000 - sim.PersonData[field] : sim.PersonData[field]);
                        change = m.Delta * (traitVal / 1000f) + m.Minimum;
                    }
                }
                advertised += AutonomyMotiveCatalog.EvaluateCurve(world.PersonMotiveDecayTuning, sim, mIdx, change * 0.001f + cur);
            }
            return (advertised - baseline) / motives.Count;
        }

        static float TraitBias(IReadOnlyList<int> p5, Interaction interaction)
        {
            float sum = 0f;
            foreach (var mIdx in AutonomyMotiveCatalog.All)
            {
                if (mIdx >= interaction.Motives.Count)
                    continue;
                var m = interaction.Motives[mIdx];
                if (m.Personality == 0 || m.Delta == 0)
                    continue;
                float traitVal = m.Personality switch
                {
                    1 => p5[4],
                    2 => 1000 - p5[4],
                    3 => p5[2],
                    4 => 1000 - p5[2],
                    7 => p5[3],
                    8 => 1000 - p5[3],
                    9 => p5[1],
                    10 => 1000 - p5[1],
                    11 => p5[0],
                    12 => 1000 - p5[0],
                    _ => 0f
                };
                sum += m.Delta * (traitVal / 1000f);
            }
            return sum;
        }

        // Ensure sufficient funds for placing two contrasting trait-biased catalog objects on starter lot House09.
        for (int c = 0; c < 4 && server.State().GetProperty("budget").GetInt32() < 3000; c++)
            server.HandleJson("cheat", new { code = "rosebud" });

        var openTiles = FindOpenFlatTiles(profile.UserData, world, 9);
        Assert.True(openTiles.Count >= 2);
        var openSet = openTiles.ToHashSet();
        var centerTiles = openTiles
            .Where(t => Enumerable.Range(-1, 3).All(dx => Enumerable.Range(-1, 3).All(dy => openSet.Contains((t.X + dx, t.Y + dy)))))
            .ToList();
        var playTile = centerTiles.First();
        var neatTile = centerTiles.First(t => Math.Abs(t.X - playTile.X) >= 4 || Math.Abs(t.Y - playTile.Y) >= 4);

        var sortedEntries = registry.Entries
            .Select(entry => (Entry: entry, HasCat: ObjectCatalog.TryGetEntry(entry, 1, out var cat), Cat: cat))
            .Where(x => x.HasCat && x.Cat is { Price: > 0 and <= 2500 })
            .OrderBy(x => x.Cat!.Price)
            .ToList();

        short BuyTraitObject(bool favorPlaySim, int tileX, int tileY, out short actionIndex)
        {
            foreach (var (entry, _, _) in sortedEntries)
            {
                var isSingleOrMaster = entry.Definition[ObjdField.MasterId] == 0 ||
                                       unchecked((short)entry.Definition[ObjdField.SubIndex]) == -1;
                if (entry.Definition[ObjdField.Disabled] != 0 ||
                    !isSingleOrMaster ||
                    entry.Definition[ObjdField.BuildModeType] != 0)
                {
                    continue;
                }

                var ttab = entry.FindInteractionTable(globalLib.ScopeOf(entry), world.Language);
                if (ttab is null)
                    continue;

                var matchingList = ttab.Interactions.Where(i =>
                {
                    if ((i.Flags & 0xC0) != 0)
                        return false;
                    if (unchecked((short)i.AutonomyThreshold) is <= 0 or > 50)
                        return false;
                    var hasNonFlatCurve = AutonomyMotiveCatalog.All.Any(mIdx =>
                        mIdx < i.Motives.Count &&
                        i.Motives[mIdx] is { Personality: not 0, Delta: not 0 } &&
                        AutonomyMotiveCatalog.EvaluateCurve(world.PersonMotiveDecayTuning, neatSim, mIdx, -49.9f) >
                        AutonomyMotiveCatalog.EvaluateCurve(world.PersonMotiveDecayTuning, neatSim, mIdx, -50f));
                    if (!hasNonFlatCurve)
                        return false;
                    var neatBias = TraitBias(p1, i);
                    var playBias = TraitBias(p2, i);
                    if (!(favorPlaySim ? playBias > neatBias + 0.1f : neatBias > playBias + 0.1f))
                        return false;
                    var neatScore = PreviewScore(neatSim, i);
                    var playScore = PreviewScore(playSim, i);
                    return favorPlaySim
                        ? playScore > neatScore && playScore > 0f
                        : neatScore > playScore && neatScore > 0f;
                }).ToList();
                if (matchingList.Count == 0)
                    continue;

                var resp = server.HandleJson("buyObject", new
                {
                    guid = entry.Guid,
                    x = tileX * 16 + 8,
                    y = tileY * 16 + 8,
                    level = 1,
                    direction = (byte)1
                });
                if (!resp.GetProperty("ok").GetBoolean())
                    continue;

                var objId = checked((short)resp.GetProperty("result").GetProperty("objectID").GetInt32());
                Assert.True(server.HandleJson("selectSim", new { objectID = neatSim.Id }).GetProperty("ok").GetBoolean());
                var menuResp = server.HandleJson("menu", new { objectID = objId });
                var menuIndices = menuResp.GetProperty("ok").GetBoolean()
                    ? menuResp.GetProperty("result").EnumerateArray()
                        .Where(item => item.TryGetProperty("interaction", out _))
                        .Select(item => item.GetProperty("interaction").GetInt32())
                        .ToHashSet()
                    : [];
                var matching = matchingList.FirstOrDefault(m => menuIndices.Contains(m.TtaIndex));
                if (matching is null)
                {
                    server.HandleJson("sellObject", new { objectID = objId });
                    continue;
                }

                actionIndex = unchecked((short)matching.TtaIndex);
                return objId;
            }

            throw new InvalidOperationException($"No buyable object found with autonomous TTAB bias for favorPlaySim={favorPlaySim}.");
        }

        var playObjId = BuyTraitObject(favorPlaySim: true, playTile.X, playTile.Y, out var playActionIdx);
        PauseAndDrainDialogs(server);
        var neatObjId = BuyTraitObject(favorPlaySim: false, neatTile.X, neatTile.Y, out var neatActionIdx);

        var originalHost = world.AutonomyHost!;
        var recordedScores = new List<(short PersonId, short TargetId, short ActionIndex, string ActionName, float Score)>();
        var globalIff = globalLib.Global!;
        world.AutonomyHost = new AutonomyHost(
            world,
            globalIff,
            9,
            () => originalHost.AutonomyLotState().Paused,
            (person, target, actionIndex, actionName, score) =>
            {
                var delegated = originalHost.ScoreAutonomy(person, target, actionIndex, actionName, score);
                recordedScores.Add((person.Id, target.Id, actionIndex, actionName, delegated));
                return delegated;
            });

        try
        {
            world.CaptureExecutionTrace = true;
            Assert.True(server.HandleJson("freeWill", new { enabled = true }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("speed", new { value = 3 }).GetProperty("ok").GetBoolean());

            JsonElement stepped = default;
            for (var round = 0; round < 30; round++)
            {
                stepped = server.Step(40);
                Assert.Equal(JsonValueKind.Null, stepped.GetProperty("simulationError").ValueKind);
                var hasPlay = recordedScores.Any(s => s.PersonId == neatSim.Id && s.TargetId == playObjId && s.ActionIndex == playActionIdx) &&
                              recordedScores.Any(s => s.PersonId == playSim.Id && s.TargetId == playObjId && s.ActionIndex == playActionIdx);
                var hasNeat = recordedScores.Any(s => s.PersonId == neatSim.Id && s.TargetId == neatObjId && s.ActionIndex == neatActionIdx) &&
                              recordedScores.Any(s => s.PersonId == playSim.Id && s.TargetId == neatObjId && s.ActionIndex == neatActionIdx);
                if (hasPlay && hasNeat)
                    break;
            }

            var playSimOnPlayObjList = recordedScores
                .Where(s => s.PersonId == playSim.Id && s.TargetId == playObjId && s.ActionIndex == playActionIdx)
                .Select(s => s.Score)
                .ToList();
            Assert.True(playSimOnPlayObjList.Count > 0, $"Missing playSim score on playObj {playObjId}:{playActionIdx}.");
            var playSimOnPlayObj = playSimOnPlayObjList[0];

            var neatSimOnPlayObjList = recordedScores
                .Where(s => s.PersonId == neatSim.Id && s.TargetId == playObjId && s.ActionIndex == playActionIdx)
                .Select(s => s.Score)
                .ToList();
            Assert.True(neatSimOnPlayObjList.Count > 0, $"Missing neatSim score on playObj {playObjId}:{playActionIdx}.");
            var neatSimOnPlayObj = neatSimOnPlayObjList[0];
            Assert.True(playSimOnPlayObj > neatSimOnPlayObj,
                $"Playful Sim (Playful=1000) should score Playful TTAB higher than Neat Sim (Playful=500): {playSimOnPlayObj} vs {neatSimOnPlayObj}");

            var neatSimOnNeatObjList = recordedScores
                .Where(s => s.PersonId == neatSim.Id && s.TargetId == neatObjId && s.ActionIndex == neatActionIdx)
                .Select(s => s.Score)
                .ToList();
            Assert.True(neatSimOnNeatObjList.Count > 0, $"Missing neatSim score on neatObj {neatObjId}:{neatActionIdx}.");
            var neatSimOnNeatObj = neatSimOnNeatObjList[0];

            var playSimOnNeatObjList = recordedScores
                .Where(s => s.PersonId == playSim.Id && s.TargetId == neatObjId && s.ActionIndex == neatActionIdx)
                .Select(s => s.Score)
                .ToList();
            Assert.True(playSimOnNeatObjList.Count > 0, $"Missing playSim score on neatObj {neatObjId}:{neatActionIdx}.");
            var playSimOnNeatObj = playSimOnNeatObjList[0];
            Assert.True(neatSimOnNeatObj > playSimOnNeatObj,
                $"Neat Sim (Neat=1000) should score Neat TTAB higher than Playful Sim (Neat=0): {neatSimOnNeatObj} vs {playSimOnNeatObj}");

            Assert.Contains(world.AutonomyAttemptTrace, a => a.ChosenObjectId is not null);
        }
        finally
        {
            world.AutonomyHost = originalHost;
        }

        var neatDict = new Dictionary<string, int>
        {
            ["Neat"] = 1000, ["Outgoing"] = 200, ["Active"] = 0, ["Playful"] = 500, ["Nice"] = 800
        };
        var playDict = new Dictionary<string, int>
        {
            ["Neat"] = 0, ["Outgoing"] = 1000, ["Active"] = 500, ["Playful"] = 1000, ["Nice"] = 0
        };
        Assert.Equal(OriginalGameplayLayout.Zodiac(neatDict, 0), neatZodiac);
        Assert.Equal(OriginalGameplayLayout.Zodiac(playDict, 0), playZodiac);
        Assert.NotEqual(neatZodiac, playZodiac);
    }

    [Fact]
    public void T3_21_CasAndBuildSave_CreateBuildEvictFamImportAndMoveIn()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 4, 9))
            return;

        var famSrc = Path.Combine(gameDir, "TemplateFamilyUnleashed", "Burb_11.FAM");
        Assert.True(File.Exists(famSrc), $"Missing template family: {famSrc}");

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 4, 9);
        var importDir = Path.Combine(profile.UserData, "Import");
        Directory.CreateDirectory(importDir);

        var famIff = IffFile.Open(famSrc);
        var famiType = ResourceType.Parse("FAMI");
        var famiChunk = famIff.OfType(famiType).First(c => c.Id != 0);
        var famRecord = Family.Read(famIff, famiChunk);
        var unhoused = famRecord.WithHouseNumber(0).WithBudget(25000).WithValue(0);
        var patchedFam = famIff.WithAddedOrReplacedChunk(famiType, famiChunk.Id, unhoused.Write(famiChunk.Id));
        File.WriteAllBytes(Path.Combine(importDir, "Burb_11.FAM"), patchedFam.Bytes.ToArray());

        using var server = E2EHarness.StartServer(profile, house: 0);
        PauseAndDrainDialogs(server);

        var createResp = server.HandleJson("createHousehold", new
        {
            familyName = "BuildAndEvict",
            moveIn = true,
            house = (short)9,
            members = new[]
            {
                new { name = "Hannah", code = "fafit", skin = "lgt", personality = DefaultPersonality() }
            }
        });
        Assert.True(createResp.GetProperty("ok").GetBoolean(), createResp.ToString());
        Assert.True(server.HandleJson("openLot", new { house = 9 }).GetProperty("ok").GetBoolean());
        PauseAndDrainDialogs(server);

        var world = server.World!;
        var (wx, wy) = FindOpenFlatPatch(profile.UserData, world, 9, patchSize: 5);
        var wallResp = server.HandleJson("architecture", new
        {
            command = "WALL_RECT",
            level = 1,
            x = wx,
            y = wy,
            x2 = wx + 3,
            y2 = wy + 3,
            style = 1,
            pattern = 0
        });
        Assert.True(wallResp.GetProperty("ok").GetBoolean(), wallResp.ToString());

        Assert.True(server.HandleJson("save", new { slot = "t3-21", lot = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = true }).GetProperty("ok").GetBoolean());

        var evictResp = server.HandleJson("neighborhood", new { mode = 1, edit = "evict:9" });
        Assert.True(evictResp.GetProperty("ok").GetBoolean(), evictResp.ToString());
        Assert.Contains(NeighborhoodExport.UnhousedFamilies(profile.UserData, gameDir, 1),
            f => f.Name.Equals("BuildAndEvict", StringComparison.OrdinalIgnoreCase) && f.Budget > 0);

        var candidates = NeighborhoodEdits.EnumerateImportCandidates(profile.UserData, profile.Profile, gameDir, 1);
        var burbIdx = Array.FindIndex(candidates.Candidates, c => c.FamilyName.Contains("Burb", StringComparison.OrdinalIgnoreCase));
        Assert.True(burbIdx >= 0);

        var importResp = server.HandleJson("neighborhood", new { mode = 1, edit = $"import:{burbIdx}" });
        Assert.True(importResp.GetProperty("ok").GetBoolean(), importResp.ToString());
        var burbFam = NeighborhoodExport.UnhousedFamilies(profile.UserData, gameDir, 1)
            .First(f => f.Name.Contains("Burb", StringComparison.OrdinalIgnoreCase));

        Assert.True(server.HandleJson("openLot", new { house = 4 }).GetProperty("ok").GetBoolean());
        var moveInResp = server.HandleJson("moveInFamily", new { family = burbFam.Id });
        Assert.True(moveInResp.GetProperty("ok").GetBoolean(), moveInResp.ToString());
        Assert.Contains(server.State().GetProperty("sims").EnumerateArray(), s => s.GetProperty("household").GetBoolean());
    }

    [Fact]
    public void T3_22_TravelAndSimAntics_GenericSimsCallAcrossAwayAndSuspendedHome()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 21))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 21);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var homeTraveller = server.World!.WalkOrder.OfType<XPerson>().First(p => IsHousehold(p) && IsAdult(p));
        var travellerGuid = homeTraveller.TypeRecord!.Key;
        var famNum = homeTraveller[PersonData.FamilyNumber];

        static void ExerciseProductionCalls(SimulationWorld world, XPerson actor, short familyNumber, bool expectValuationCall)
        {
            Assert.Null(world.Host);
            var stack = TreeRunner.CreateTransientStack(world, actor);
            var frame = new StackFrame(actor.Id, [0], [0, 0]);
            var ctx = new TreeContext(world, actor, frame) { Stack = stack };

            Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, [0x00, 0x00]));
            Assert.Equal((short)1, world.NeighborhoodData[1]);
            Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, [0x08, 0x00]));
            Assert.Equal((short)2, world.NeighborhoodData[1]);
            Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, [0x09, 0x00]));
            Assert.Equal((short)3, world.NeighborhoodData[1]);

            Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, [0x1a, 0x00]));
            Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, [0x2b, 0x00]));

            actor.Temporaries[0] = actor.Id;
            Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, [0x2a, 0x00]));

            if (expectValuationCall)
            {
                var before = world.FamilyBudget!.ReadFamilyBudget();
                actor.Temporaries[0] = familyNumber;
                Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, [0x05, 0x00]));
                Assert.NotEqual(before, world.FamilyBudget.ReadFamilyBudget());
            }
        }

        // Travel to Downtown 21 and run production GenericSimsCall calls on away lot
        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, travellerGuid, famNum);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(21, server.State().GetProperty("house").GetInt32());

        var awayActor = server.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == travellerGuid);
        ExerciseProductionCalls(server.World, awayActor, famNum, expectValuationCall: false);

        // Return to SuspendedHome and run production GenericSimsCall calls (including 0x05 valuation)
        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, travellerGuid, famNum);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(5, server.State().GetProperty("house").GetInt32());

        var returnedActor = server.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == travellerGuid);
        ExerciseProductionCalls(server.World, returnedActor, famNum, expectValuationCall: true);
    }

    [Fact]
    public void T3_23_TravelAndBuildSave_HomeFurnishingsAndAwayItemsRoundTrip()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 21))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 21);
        using var server = E2EHarness.StartServer(profile, house: 5);
        PauseAndDrainDialogs(server);

        var homeWorld = server.World!;
        var (px, py) = FindOpenFlatPatch(profile.UserData, homeWorld, 5, patchSize: 3);
        var candidate = homeWorld.ObjectRegistry!.Entries
            .Where(e => e.Definition[ObjdField.Disabled] == 0 &&
                        e.Definition[ObjdField.RoomSort] != 0 &&
                        e.Definition[ObjdField.BuildModeType] == 0 &&
                        e.Definition[ObjdField.MasterId] == 0 &&
                        ObjectCatalog.TryGetEntry(e, 1, out var cat) && cat is { Price: > 0 and <= 500 })
            .OrderBy(e => e.Definition[ObjdField.Price])
            .First();

        var buyX = (px + 1) * 16 + 8;
        var buyY = (py + 1) * 16 + 8;
        var buyResp = server.HandleJson("buyObject", new { guid = candidate.Guid, x = buyX, y = buyY, level = 1, direction = (byte)1 });
        Assert.True(buyResp.GetProperty("ok").GetBoolean(), buyResp.ToString());
        var boughtId = buyResp.GetProperty("result").GetProperty("objectID").GetInt16();

        var traveller = homeWorld.WalkOrder.OfType<XPerson>().First(p => IsHousehold(p) && IsAdult(p));
        var travellerGuid = traveller.TypeRecord!.Key;
        var famNum = traveller[PersonData.FamilyNumber];

        homeWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, travellerGuid, famNum);
        server.BatchRun(1);
        server.DrainEvents();

        var awayWorld = server.World!;
        var awaySim = awayWorld.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == travellerGuid);
        var nid = awaySim[PersonData.NeighborId];
        awayWorld.NeighborInventory(nid).Add(7, unchecked((int)NeighborhoodExport.MagiCoinTokenGuid), 12);
        awayWorld.FamilyBudget!.AlterFamilyBudget(0, 50);
        var expectedBudget = awayWorld.FamilyBudget.ReadFamilyBudget();

        Assert.True(server.HandleJson("save", new { slot = "t3-23-away", lot = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("load", new { slot = "t3-23-away" }).GetProperty("ok").GetBoolean());
        Assert.Equal(21, server.State().GetProperty("house").GetInt32());

        server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, travellerGuid, famNum);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(5, server.State().GetProperty("house").GetInt32());

        // Home furnishing from SuspendedHome + away inventory & budget delta must both survive
        var restoredItem = server.World!.ObjectById(boughtId);
        Assert.NotNull(restoredItem);
        Assert.Equal(buyX, restoredItem.Position.X);
        Assert.Equal(buyY, restoredItem.Position.Y);
        Assert.Equal(expectedBudget, server.State().GetProperty("budget").GetInt32());

        Assert.True(server.HandleJson("save", new { slot = "t3-23-home", lot = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("load", new { slot = "t3-23-home" }).GetProperty("ok").GetBoolean());

        Assert.NotNull(server.World!.ObjectById(boughtId));
        var homeSimState = server.State().GetProperty("sims").EnumerateArray()
            .First(s => unchecked((int)s.GetProperty("guid").GetUInt32()) == travellerGuid);
        Assert.Contains(homeSimState.GetProperty("inventory").EnumerateArray(),
            item => item.GetProperty("guid").GetUInt32() == NeighborhoodExport.MagiCoinTokenGuid &&
                    item.GetProperty("count").GetInt32() >= 12);
    }

    [Fact]
    public void T3_24_SimAnticsAndBuildSave_MansionBuildBillsUpkeepRepoRemovalAndSave()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        PauseAndDrainDialogs(server);

        var world = server.World!;
        var budget = Assert.IsType<FamilyBudget>(world.FamilyBudget);
        budget.ActiveFamily = budget.ActiveFamily!.WithBudget(50_000);

        var reportBefore = server.State().GetProperty("budgetReport");
        var propBefore = reportBefore.GetProperty("propertyValue").GetInt32();
        var objsBefore = reportBefore.GetProperty("objects").GetInt32();

        var (wx, wy) = FindOpenFlatPatch(profile.UserData, world, 5, patchSize: 5);
        Assert.True(server.HandleJson("architecture", new
        {
            command = "WALL_RECT",
            level = 1,
            x = wx,
            y = wy,
            x2 = 3,
            y2 = 3,
            style = 1,
            pattern = 0
        }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("architecture", new
        {
            command = "FLOOR_RECT",
            level = 1,
            x = wx,
            y = wy,
            x2 = 3,
            y2 = 3,
            pattern = 1
        }).GetProperty("ok").GetBoolean());

        var expensiveCandidates = world.ObjectRegistry!.Entries
            .Where(e => e.Definition[ObjdField.Disabled] == 0 &&
                        e.Definition[ObjdField.RoomSort] != 0 &&
                        e.Definition[ObjdField.BuildModeType] == 0 &&
                        e.Definition[ObjdField.MasterId] == 0 &&
                        ObjectCatalog.TryGetEntry(e, 1, out var cat) && cat is { Price: >= 200 and <= 1500 })
            .OrderByDescending(e => e.Definition[ObjdField.Price])
            .Take(60)
            .ToArray();
        Assert.NotEmpty(expensiveCandidates);

        JsonElement buyResp = default;
        var bought = false;
        foreach (var cand in expensiveCandidates)
        {
            for (var dy = 0; dy <= 2 && !bought; dy++)
            for (var dx = 0; dx <= 2 && !bought; dx++)
            foreach (var dir in new byte[] { 1, 4, 16, 64 })
            {
                PauseAndDrainDialogs(server);
                buyResp = server.HandleJson("buyObject", new
                {
                    guid = cand.Guid,
                    x = (wx + dx) * 16 + 8,
                    y = (wy + dy) * 16 + 8,
                    level = 1,
                    direction = dir
                });
                if (buyResp.GetProperty("ok").GetBoolean())
                {
                    PauseAndDrainDialogs(server);
                    bought = true;
                    break;
                }
            }
            if (bought) break;
        }
        Assert.True(bought, buyResp.ValueKind != JsonValueKind.Undefined ? buyResp.ToString() : "No expensive candidate placed.");
        var boughtId = buyResp.GetProperty("result").GetProperty("objectID").GetInt16();

        var reportAfter = server.State().GetProperty("budgetReport");
        Assert.True(reportAfter.GetProperty("objects").GetInt32() > objsBefore);
        Assert.True(reportAfter.GetProperty("propertyValue").GetInt32() > propBefore);

        // Advance past midnight so budget day rotates, then execute GenericSimsCall 0x05 net-worth valuation and bill upkeep deduction
        var daysBefore = world[SimulationGlobal.DaysRunning];
        world.Clock.Restore(23, 59, 58);
        world.AdvanceClock(advanceGameTime: true, simulationTickCompleted: true);
        Assert.True(world[SimulationGlobal.DaysRunning] > daysBefore);

        var actor = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var stack = TreeRunner.CreateTransientStack(world, actor);
        var ctx = new TreeContext(world, actor, new StackFrame(actor.Id, [0], [0, 0])) { Stack = stack };
        var budgetBeforeCall = budget.ReadFamilyBudget();
        actor.Temporaries[0] = actor[PersonData.FamilyNumber];
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, [0x05, 0x00]));
        var budgetAfterValuation = budget.ReadFamilyBudget();
        Assert.True(budgetAfterValuation > budgetBeforeCall);
        Assert.Equal(PrimitiveResult.True, AlterBudgetPrimitive.Execute(ctx, [0, 0, 150, 0, 0, 0, 2, 0]));
        Assert.Equal(budgetAfterValuation - 150, budget.ReadFamilyBudget());

        // Simulate Repo Man repossession removal, save, close, and reload
        Assert.True(world.Remove(boughtId));
        Assert.True(server.HandleJson("save", new { slot = "t3-24", lot = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("load", new { slot = "t3-24" }).GetProperty("ok").GetBoolean());

        Assert.Null(server.World!.ObjectById(boughtId));
        Assert.DoesNotContain(server.State().GetProperty("objects").EnumerateArray(),
            o => o.GetProperty("id").GetInt16() == boughtId);
    }
}
