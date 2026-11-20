using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLayer;
using SimsEngine.Formats;
using SimsSDL;
using SimsSDL.Core;
using SimsSimulation;

namespace SimsEngine.Tests.E2E;

[Collection("Neighborhoods")]
[Trait("Suite", "E2E")]
[Trait("Area", "Audio")]
public sealed class AudioE2ETests
{
    private static string BuildSyntheticHotGameDir(
        string root,
        Action<List<string>, Dictionary<int, byte[]>, List<string>, List<string>> configure)
    {
        var soundData = Path.Combine(root, "SoundData");
        Directory.CreateDirectory(soundData);

        var validWav1 = E2EHarness.BuildSyntheticWav(22050, 1, 1102);
        var validWav2 = E2EHarness.BuildSyntheticWav(22050, 1, 1500);
        File.WriteAllBytes(Path.Combine(soundData, "sfx_one.wav"), validWav1);
        File.WriteAllBytes(Path.Combine(soundData, "sfx_two.wav"), validWav2);

        var patches = new List<string>
        {
            "1=0, valid_cue_patch, \"SoundData\\sfx_one.wav\", 0",
            "2=0, secondary_cue_patch, \"SoundData\\sfx_two.wav\", 0"
        };
        var routines = new Dictionary<int, byte[]>();
        var tracks = new List<string>();
        var mappings = new List<string>();

        configure(patches, routines, tracks, mappings);

        var offset = 0;
        var hitBytes = new List<byte>();
        var tkdLines = new List<string>();
        var hsmLines = new List<string>();
        foreach (var (tkdId, code) in routines.OrderBy(kv => kv.Key))
        {
            tkdLines.Add($"{tkdId}={offset}");
            hsmLines.Add($"tkd_sub_{tkdId} {tkdId}");
            hsmLines.Add($"sub_{tkdId} {offset}");
            hitBytes.AddRange(code);
            offset += code.Length;
        }

        var hot = new StringBuilder();
        hot.AppendLine("[patch]");
        foreach (var p in patches) hot.AppendLine(p);
        hot.AppendLine("[trackdata]");
        foreach (var t in tkdLines) hot.AppendLine(t);
        hot.AppendLine("[track]");
        foreach (var trk in tracks) hot.AppendLine(trk);
        hot.AppendLine("[eventmapping]");
        foreach (var m in mappings) hot.AppendLine(m);

        File.WriteAllText(Path.Combine(soundData, "TestSynth.hot"), hot.ToString(), Encoding.Latin1);
        File.WriteAllLines(Path.Combine(soundData, "TestSynth.hsm"), hsmLines, Encoding.Latin1);
        File.WriteAllBytes(Path.Combine(soundData, "TestSynth.hit"), hitBytes.ToArray());
        return root;
    }

    private static string? FindAnyMp3(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.mp3", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            : null;

    // -------------------------------------------------------------------------
    // Feature F01: Stateful HIT Bytecode VM
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F01")]
    public void T1_F01_01_TrackDataLiteralJumpResolution_VoxLaughGiggle()
    {
        var gameDir = E2EHarness.RequireGameDir();

        using var audio = E2EHarness.CreateAudioSession(gameDir);
        var female = audio.Bank.ResolveVoice("vox_laugh_giggle", 'f', 1);
        Assert.NotNull(female);
        Assert.Equal("VOX", female.Group);
        Assert.Contains("gigglef", female.RelativePath, StringComparison.OrdinalIgnoreCase);

        var direct = audio.Bank.Resolve("vox_laugh_giggle");
        Assert.NotNull(direct);
        Assert.Equal("VOX", direct.Group);
        Assert.Contains("vox_laugh_giggle", direct.RelativePath, StringComparison.OrdinalIgnoreCase);

        audio.Runtime.SetSpeakerDemographic(10, 'f');
        Assert.True(audio.Runtime.PlayForSimulationObject("vox_laugh_giggle", 10, false, false, 'f'));
        var play = Assert.Single(audio.PlayEvents());
        Assert.Equal("VOX", play.GetProperty("group").GetString());
        Assert.Contains("gigglef", play.GetProperty("file").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F01")]
    public void T1_F01_02_RegisterIndirectSubroutineJump_ExecutesConditionalBranch()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-e2e-f01-02-" + Guid.NewGuid().ToString("N"));
        try
        {
            BuildSyntheticHotGameDir(tempRoot, (patches, routines, tracks, mappings) =>
            {
                patches.Add("10=0, indirect_target_bell, \"SoundData\\sfx_two.wav\", 0");
                // Entry routine 100: reg8 = 42; Test(reg8); JumpEq -> 200 (wrong branch); JumpGt -> 201 (right branch); End
                routines[100] =
                [
                    0x05, 8, 42, 0, 0, 0,       // Loadl reg8 = 42
                    0x0e, 8,                    // Test reg8 (zero=0, gt=1)
                    0x3e, 200, 0, 0, 0,         // JumpEq -> 200 (not taken)
                    0x40, 201, 0, 0, 0,         // JumpGt -> 201 (taken)
                    0x0c                        // End
                ];
                // False branch 200 sets reg 0x32 = 9999 (missing patch!) so static branch scanning fails
                routines[200] = [0x05, 0x32, 0x0F, 0x27, 0, 0, 0x02, 1, 0x0c];
                // Taken branch 201 sets reg 0x32 = 10 ("indirect_target_bell") and emits note_on
                routines[201] = [0x05, 0x32, 10, 0, 0, 0, 0x02, 10, 0x0c];

                tracks.Add("500=0, reg_jump_event, 0, kArgsNormal, 0, kGroupSfx, 0, @sub_100, 0");
                mappings.Add("reg_jump_event=kPlayTrack, 500");
            });

            using var audio = E2EHarness.CreateAudioSession(tempRoot);
            Assert.True(audio.Runtime.Play("reg_jump_event"), "HIT VM should follow JumpGt to taken branch and play indirect_target_bell.");
            var play = Assert.Single(audio.PlayEvents());
            Assert.Contains("indirect_target_bell", play.GetProperty("file").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            E2EHarness.DeleteTempTree(tempRoot);
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F01")]
    public void T1_F01_03_WaitTimerPacing_DefersSecondCueUntilDurationElapses()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-e2e-f01-03-" + Guid.NewGuid().ToString("N"));
        try
        {
            BuildSyntheticHotGameDir(tempRoot, (patches, routines, tracks, mappings) =>
            {
                // Track 501 (note 1) & 502 (note 2)
                routines[101] = [0x05, 0x32, 1, 0, 0, 0, 0x02, 1, 0x0c];
                routines[102] = [0x05, 0x32, 2, 0, 0, 0, 0x02, 2, 0x0c];
                // Main routine 100: PlayTrack(501 via reg5), Loadl reg9=20 ticks (100ms at 200Hz), Wait reg9, PlayTrack(502 via reg6), End
                routines[100] =
                [
                    0x05, 5, 0xF5, 0x01, 0, 0,  // Loadl reg5 = 501
                    0x32, 5,                    // PlayTrack reg5
                    0x05, 9, 20, 0, 0, 0,       // Loadl reg9 = 20 HIT ticks (~100ms)
                    0x09, 9,                    // Wait reg9
                    0x05, 6, 0xF6, 0x01, 0, 0,  // Loadl reg6 = 502
                    0x32, 6,                    // PlayTrack reg6
                    0x0c                        // End
                ];
                tracks.Add("501=0, note_one, 0, kArgsNormal, 0, kGroupSfx, 0, @sub_101, 1");
                tracks.Add("502=0, note_two, 0, kArgsNormal, 0, kGroupSfx, 0, @sub_102, 2");
                tracks.Add("500=0, wait_seq, 0, kArgsNormal, 0, kGroupSfx, 0, @sub_100, 0");
                mappings.Add("wait_seq=kPlayTrack, 500");
            });

            using var audio = E2EHarness.CreateAudioSession(tempRoot);
            Assert.True(audio.Runtime.Play("wait_seq"));
            Assert.Single(audio.PlayEvents()); // Immediate first note only

            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
            while (DateTime.UtcNow < deadline && audio.PlayEvents().Count < 2)
            {
                audio.Runtime.Tick();
                Thread.Sleep(20);
            }

            Assert.Equal(2, audio.PlayEvents().Count);
        }
        finally
        {
            E2EHarness.DeleteTempTree(tempRoot);
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F01")]
    public void T1_F01_04_DuckingAndFreshnessUpdate_AttenuatesMusicAndRotatesVariations()
    {
        var gameDir = E2EHarness.RequireGameDir();

        using var audio = E2EHarness.CreateAudioSession(gameDir);
        Assert.True(audio.Runtime.Play("bkground_buy1", -1));
        var initialPlay = Assert.Single(audio.PlayEvents());
        float initialMusicVolume = initialPlay.GetProperty("volume").GetSingle();
        long musicVoiceId = initialPlay.GetProperty("voice").GetInt64();

        audio.DrainAudioEvents();
        // sting_kiss_polite executes PlayStingWithDucking_* which sets MainDuckPri (0x7b) to duck active music.
        Assert.True(audio.Runtime.Play("sting_kiss_polite", -1));
        var stingPlay = audio.PlayEvents().FirstOrDefault(e => e.GetProperty("voice").GetInt64() != musicVoiceId);
        Assert.Equal(JsonValueKind.Object, stingPlay.ValueKind);
        long stingVoiceId = stingPlay.GetProperty("voice").GetInt64();

        var duckUpdates = audio.UpdateEvents()
            .Where(u => u.GetProperty("voice").GetInt64() == musicVoiceId)
            .Select(u => u.GetProperty("volume").GetSingle())
            .ToArray();
        Assert.NotEmpty(duckUpdates);
        Assert.True(duckUpdates.Any(v => v < initialMusicVolume),
            $"Expected bkground_buy1 volume ({initialMusicVolume}) to be attenuated while sting_kiss_polite plays.");

        // End the ducker sting and verify bkground_buy1 volume restores to initialMusicVolume
        audio.DrainAudioEvents();
        audio.Runtime.End(stingVoiceId);
        audio.Runtime.Tick();
        var restoreUpdates = audio.UpdateEvents()
            .Where(u => u.GetProperty("voice").GetInt64() == musicVoiceId)
            .Select(u => u.GetProperty("volume").GetSingle())
            .ToArray();
        Assert.NotEmpty(restoreUpdates);
        Assert.Contains(restoreUpdates, v => Math.Abs(v - initialMusicVolume) < 0.01f);

        // Also check variation rotation across 5 triggers
        var playedFiles = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            audio.DrainAudioEvents();
            Assert.True(audio.Runtime.PlayForSimulationObject("footstep_hard", (short)(20 + i), false, false));
            var play = Assert.Single(audio.PlayEvents());
            playedFiles.Add(play.GetProperty("file").GetString()!);
        }

        for (int i = 1; i < playedFiles.Count; i++)
            Assert.NotEqual(playedFiles[i - 1], playedFiles[i]);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F01")]
    public void T1_F01_05_RadioStationAutoPatch_PatchesStationsAndHandlesNextAndTurnOff()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());

        long activeVoice = 0;
        string? activeFile = null;
        var rockTracks = new[] { "rock1", "rock2", "rock3", "rock4", "rock5" };
        var stations = new (string Station, string[] Tracks)[]
        {
            ("stereo_classical", ["bbmaj", "cmaj", "ebmaj", "fmaj", "gmaj"]),
            ("stereo_country", ["beaumont", "devilsdr", "downroad", "downyon", "sallygoo", "splatter", "turkey"]),
            ("stereo_rock", rockTracks)
        };
        for (int i = 0; i < stations.Length; i++)
        {
            var (station, expectedTracks) = stations[i];
            var resp = server.HandleJson("audioEvent", new { name = station, ownerID = 50 });
            Assert.True(resp.GetProperty("ok").GetBoolean());
            Assert.True(resp.GetProperty("result").GetProperty("played").GetBoolean(), $"Expected {station} to play.");

            var play = server.LastEvents.FirstOrDefault(e =>
                e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
                e.TryGetProperty("command", out var c) && c.GetString() == "play" &&
                e.TryGetProperty("group", out var g) && g.GetString() == "MUSIC");
            Assert.Equal(JsonValueKind.Object, play.ValueKind);
            activeFile = play.GetProperty("file").GetString();
            activeVoice = play.GetProperty("voice").GetInt64();
            Assert.NotNull(activeFile);
            Assert.Contains(expectedTracks, trk => activeFile.Contains(trk, StringComparison.OrdinalIgnoreCase));
            Assert.EndsWith(".mp3", activeFile, StringComparison.OrdinalIgnoreCase);

            if (i < stations.Length - 1)
            {
                var switchOff = server.HandleJson("audioEvent", new { name = "stereo_turnoff", ownerID = 50 });
                Assert.True(switchOff.GetProperty("ok").GetBoolean());
                Assert.Contains(server.LastEvents, e =>
                    e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
                    e.TryGetProperty("command", out var c) && c.GetString() == "stop" &&
                    e.GetProperty("voice").GetInt64() == activeVoice);
            }
        }

        var nextResp = server.HandleJson("audioEvent", new { name = "radio_next_song", ownerID = 50 });
        Assert.True(nextResp.GetProperty("ok").GetBoolean());
        Assert.True(nextResp.GetProperty("result").GetProperty("played").GetBoolean(),
            "radio_next_song should advance/patch the active stereo station.");
        Assert.Contains(server.LastEvents, e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
            e.TryGetProperty("command", out var c) && c.GetString() == "stop" &&
            e.GetProperty("voice").GetInt64() == activeVoice);
        var nextPlay = server.LastEvents.FirstOrDefault(e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
            e.TryGetProperty("command", out var c) && c.GetString() == "play" &&
            e.TryGetProperty("group", out var g) && g.GetString() == "MUSIC");
        Assert.Equal(JsonValueKind.Object, nextPlay.ValueKind);
        var nextFile = nextPlay.GetProperty("file").GetString();
        var nextVoice = nextPlay.GetProperty("voice").GetInt64();
        Assert.NotNull(nextFile);
        Assert.Contains(rockTracks, trk => nextFile.Contains(trk, StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith(".mp3", nextFile, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(activeFile, nextFile);

        var offResp = server.HandleJson("audioEvent", new { name = "stereo_turnoff", ownerID = 50 });
        Assert.True(offResp.GetProperty("ok").GetBoolean());
        Assert.True(offResp.GetProperty("result").GetProperty("played").GetBoolean(),
            "stereo_turnoff should cleanly handle stop track 23 without failing.");
        Assert.Contains(server.LastEvents, e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
            e.TryGetProperty("command", out var c) && c.GetString() == "stop" &&
            e.GetProperty("voice").GetInt64() == nextVoice);
        Assert.Equal(0, server.HandleJson("audioState").GetProperty("result").GetProperty("voices").GetInt32());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F01")]
    public void T2_F01_01_IllegalHitOpcode_AbortsOffendingTrackCleanly()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-e2e-f01-t2-01-" + Guid.NewGuid().ToString("N"));
        try
        {
            BuildSyntheticHotGameDir(tempRoot, (_, routines, tracks, mappings) =>
            {
                routines[100] = [0xFE, 0x0C]; // Unmapped illegal opcode 0xFE
                routines[101] = [0x05, 0x32, 1, 0, 0, 0, 0x0c];
                tracks.Add("500=0, bad_opcode, 0, kArgsNormal, 0, kGroupSfx, 0, @sub_100, 0");
                tracks.Add("501=0, valid_cue, 0, kArgsNormal, 0, kGroupSfx, 0, @sub_101, 1");
                mappings.Add("bad_opcode=kPlayTrack, 500");
                mappings.Add("valid_cue=kPlayTrack, 501");
            });

            using var audio = E2EHarness.CreateAudioSession(tempRoot);
            Assert.True(audio.Runtime.Play("valid_cue"));
            Assert.False(audio.Runtime.Play("bad_opcode"));
            Assert.Equal(1, audio.Runtime.ActiveVoiceCount);
            Assert.True(audio.Runtime.Play("valid_cue"));
        }
        finally
        {
            E2EHarness.DeleteTempTree(tempRoot);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F01")]
    public void T2_F01_02_OutOfRangeRegisterIndex_FailsCleanlyWithoutCorruption()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-e2e-f01-t2-02-" + Guid.NewGuid().ToString("N"));
        try
        {
            BuildSyntheticHotGameDir(tempRoot, (_, routines, tracks, mappings) =>
            {
                // Append note_on (0x02, 2 -> secondary_cue_patch, since InitialPatchId = 0 requires noteArg > 1) after each invalid register instruction so the test fails if HitVm fails to abort.
                routines[100] = [0x05, 0x00, 1, 0, 0, 0, 0x02, 2, 0x0c]; // Reg 0 invalid
                routines[101] = [0x05, 0x80, 1, 0, 0, 0, 0x02, 2, 0x0c]; // Reg 128 invalid
                routines[102] = [0x09, 0x80, 0x02, 2, 0x0c];             // Wait Reg 128 invalid
                routines[103] = [0x05, 0x32, 1, 0, 0, 0, 0x02, 1, 0x0c];
                tracks.Add("500=0, bad_reg0, 0, kArgsNormal, 0, kGroupSfx, 0, @sub_100, 0");
                tracks.Add("501=0, bad_reg128, 0, kArgsNormal, 0, kGroupSfx, 0, @sub_101, 0");
                tracks.Add("502=0, bad_wait128, 0, kArgsNormal, 0, kGroupSfx, 0, @sub_102, 0");
                tracks.Add("503=0, valid_cue, 0, kArgsNormal, 0, kGroupSfx, 0, @sub_103, 1");
                mappings.Add("bad_reg0=kPlayTrack, 500");
                mappings.Add("bad_reg128=kPlayTrack, 501");
                mappings.Add("bad_wait128=kPlayTrack, 502");
                mappings.Add("valid_cue=kPlayTrack, 503");
            });

            using var audio = E2EHarness.CreateAudioSession(tempRoot);
            Assert.False(audio.Runtime.Play("bad_reg0"));
            Assert.False(audio.Runtime.Play("bad_reg128"));
            Assert.False(audio.Runtime.Play("bad_wait128"));
            Assert.Empty(audio.PlayEvents());
            Assert.True(audio.Runtime.Play("valid_cue"));
            Assert.Single(audio.PlayEvents());
        }
        finally
        {
            E2EHarness.DeleteTempTree(tempRoot);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F01")]
    public async Task T2_F01_03_ZeroWaitLoopGuard_CompletesWithinTimeout()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-e2e-f01-t2-03-" + Guid.NewGuid().ToString("N"));
        try
        {
            BuildSyntheticHotGameDir(tempRoot, (_, routines, tracks, mappings) =>
            {
                // Self-jumping zero-wait infinite loop: Jump -> 100
                routines[100] = [0x07, 100, 0, 0, 0];
                routines[101] = [0x05, 0x32, 1, 0, 0, 0, 0x0c];
                tracks.Add("500=0, zero_wait_loop, 0, kArgsNormal, 0, kGroupSfx, 0, @sub_100, 0");
                tracks.Add("501=0, valid_cue, 0, kArgsNormal, 0, kGroupSfx, 0, @sub_101, 1");
                mappings.Add("zero_wait_loop=kPlayTrack, 500");
                mappings.Add("valid_cue=kPlayTrack, 501");
            });

            using var audio = E2EHarness.CreateAudioSession(tempRoot);
            var loopTask = Task.Run(() =>
            {
                audio.Runtime.Play("zero_wait_loop");
                audio.Runtime.Tick();
            });
            var completed = await Task.WhenAny(loopTask, Task.Delay(TimeSpan.FromSeconds(2))) == loopTask;
            Assert.True(completed, "Zero-wait HIT loop hung instead of yielding/capping instructions.");
            await loopTask;
            Assert.True(audio.Runtime.Play("valid_cue"));
        }
        finally
        {
            E2EHarness.DeleteTempTree(tempRoot);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F01")]
    public void T2_F01_04_KillOrStopNonexistentOrEndedTrack_IsGracefulNoOp()
    {
        var gameDir = E2EHarness.RequireGameDir();

        using var audio = E2EHarness.CreateAudioSession(gameDir);
        Assert.True(audio.Runtime.PlayForSimulationObject("fire_burning_small", 88, true, true));
        int objectVoices = audio.Runtime.ActiveVoiceCount;
        Assert.True(objectVoices >= 1);

        Assert.True(audio.Runtime.Play("ui_click"));
        var clickVoice = audio.PlayEvents().Last().GetProperty("voice").GetInt64();
        Assert.Equal(objectVoices + 1, audio.Runtime.ActiveVoiceCount);

        audio.Runtime.End(clickVoice);
        audio.Runtime.End(clickVoice); // double end
        audio.Runtime.End(999_999_999L); // nonexistent voice
        audio.Runtime.StopResource(9999, "nonexistent_cue");
        audio.Runtime.RemoveOwner(9999);

        Assert.Equal(objectVoices, audio.Runtime.ActiveVoiceCount);

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir);
        using var server = E2EHarness.StartServer(profile);
        var endedResp = server.HandleJson("audioEnded", new { voice = 999_999_999L });
        Assert.True(endedResp.GetProperty("ok").GetBoolean());
        Assert.True(endedResp.GetProperty("result").GetProperty("accepted").GetBoolean());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F01")]
    public void T2_F01_05_NoteOnWithMissingSampleId_FailsGracefullyWithoutLeak()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-e2e-f01-t2-05-" + Guid.NewGuid().ToString("N"));
        try
        {
            BuildSyntheticHotGameDir(tempRoot, (_, routines, tracks, mappings) =>
            {
                routines[100] = [0x05, 0x32, 0x9F, 0x86, 0x01, 0, 0x0c]; // PatchId 99999 absent
                routines[101] = [0x05, 0x32, 1, 0, 0, 0, 0x0c];
                tracks.Add("500=0, missing_patch_track, 0, kArgsNormal, 0, kGroupSfx, 0, @sub_100, 99999");
                tracks.Add("501=0, valid_cue, 0, kArgsNormal, 0, kGroupSfx, 0, @sub_101, 1");
                mappings.Add("missing_patch_track=kPlayTrack, 500");
                mappings.Add("valid_cue=kPlayTrack, 501");
            });

            using var audio = E2EHarness.CreateAudioSession(tempRoot);
            Assert.False(audio.Runtime.Play("missing_patch_track"));
            Assert.Equal(0, audio.Runtime.ActiveVoiceCount);
            Assert.True(audio.Runtime.Play("valid_cue"));
            Assert.Equal(1, audio.Runtime.ActiveVoiceCount);
        }
        finally
        {
            E2EHarness.DeleteTempTree(tempRoot);
        }

        var gameDir = E2EHarness.RequireGameDir();
        using var realAudio = E2EHarness.CreateAudioSession(gameDir);
        Assert.Null(realAudio.Bank.Resolve("MissingEvent"));
    }

    // -------------------------------------------------------------------------
    // Feature F02: Music/Station Playlists & bkground_fade
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F02")]
    public void T1_F02_01_ModePlaylistSequentialAdvancement_IsFalseLoopAndAdvancesOnEnd()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        using var audio = E2EHarness.CreateAudioSession(gameDir);
        var buyTrack1 = audio.Bank.Resolve("bkground_buy1");
        Assert.NotNull(buyTrack1);
        Assert.Equal("MUSIC", buyTrack1.Group);
        Assert.False(buyTrack1.IsLoop, "Playlist-backed mode music must emit IsLoop = false so AudioMixer notifies end-of-track.");

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("modeMusic", new { mode = "buy" }).GetProperty("ok").GetBoolean());

        var firstPlay = server.LastEvents.FirstOrDefault(e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
            e.TryGetProperty("command", out var c) && c.GetString() == "play" &&
            e.TryGetProperty("group", out var g) && g.GetString() == "MUSIC");
        Assert.Equal(JsonValueKind.Object, firstPlay.ValueKind);
        Assert.False(firstPlay.GetProperty("loop").GetBoolean());

        var voice1 = firstPlay.GetProperty("voice").GetInt64();
        var file1 = firstPlay.GetProperty("file").GetString();

        var ended = server.HandleJson("audioEnded", new { voice = voice1 });
        Assert.True(ended.GetProperty("ok").GetBoolean());

        var nextPlay = server.LastEvents.FirstOrDefault(e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
            e.TryGetProperty("command", out var c) && c.GetString() == "play" &&
            e.TryGetProperty("group", out var g) && g.GetString() == "MUSIC");
        Assert.Equal(JsonValueKind.Object, nextPlay.ValueKind);
        Assert.NotEqual(file1, nextPlay.GetProperty("file").GetString());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F02")]
    public void T1_F02_02_Mode0BkgroundFadeAndLiveTransition_StopsActiveMusic()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        using var audio = E2EHarness.CreateAudioSession(gameDir);
        Assert.True(audio.Runtime.Play("bkground_buy1", -1));
        Assert.Equal(1, audio.Runtime.ActiveVoiceCount);

        Assert.True(audio.Runtime.Play("bkground_fade", -1),
            "bkground_fade (mode 9) must be recognized to stop/fade active background mode music.");
        Assert.Contains(audio.StopEvents(), _ => true);

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("modeMusic", new { mode = "buy" }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("modeMusic", new { mode = "live" }).GetProperty("ok").GetBoolean());

        var state = server.HandleJson("audioState").GetProperty("result");
        Assert.Equal(0, state.GetProperty("voices").GetInt32());
        Assert.Contains(server.AllEvents, e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
            e.TryGetProperty("command", out var c) && c.GetString() == "stop");
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F02")]
    public void T1_F02_03_CommunityLotBuyMode_SelectsExpansionStations()
    {
        var gameDir = E2EHarness.RequireGameDir();

        var cases = new (short House, string ExpectedMode)[]
        {
            (21, "station_dtbuy"),
            (40, "station_vacation"),
            (58, "station_unleashed"),
            (82, "station_superstar"),
            (93, "music_magictown")
        };

        foreach (var (house, expectedMode) in cases)
        {
            Assert.True(E2EHarness.HasHouses(gameDir, house), $"Missing House{house:D2}.iff");
            using var profile = E2EHarness.CreateIsolatedProfile(gameDir, house);
            using var server = E2EHarness.StartServer(profile, house: house);
            Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());

            var resp = server.HandleJson("modeMusic", new { mode = "buy" });
            Assert.True(resp.GetProperty("ok").GetBoolean());
            var result = resp.GetProperty("result");
            Assert.Equal(expectedMode, result.GetProperty("name").GetString());
            Assert.True(result.GetProperty("played").GetBoolean(), $"Expected {expectedMode} to play on house {house}.");
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F02")]
    public void T1_F02_04_MagicTownResidential_OverridesBuildAndBuyMusic()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 90));

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 90);
        using var server = E2EHarness.StartServer(profile, house: 90);
        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());

        var buildResp = server.HandleJson("modeMusic", new { mode = "build" }).GetProperty("result");
        Assert.Equal("music_magictown_build", buildResp.GetProperty("name").GetString());
        Assert.True(buildResp.GetProperty("played").GetBoolean());

        var buyResp = server.HandleJson("modeMusic", new { mode = "buy" }).GetProperty("result");
        Assert.Equal("music_magictown_buy", buyResp.GetProperty("name").GetString());
        Assert.True(buyResp.GetProperty("played").GetBoolean());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F02")]
    public async Task T1_F02_05_OptionsCameraModesAndStereoPlaylist_AdvancesWithoutLooping()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);

        // Exercise pre-lot FrontendAudioSession for neighborhood / options UI mode music
        await using (var frontend = await E2EHarness.StartFrontendAudioServerAsync(profile))
        {
            var enableResp = await frontend.SendAsync("audio", new { enabled = true });
            Assert.True(enableResp.GetProperty("ok").GetBoolean());

            var nhoodResp = await frontend.SendAsync("audioEvent", new { name = "bkground_nhood" });
            Assert.True(nhoodResp.GetProperty("ok").GetBoolean());
            Assert.True(nhoodResp.GetProperty("result").GetProperty("played").GetBoolean());
            var nhoodPlay = frontend.DrainEvents().FirstOrDefault(e =>
                e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
                e.TryGetProperty("command", out var c) && c.GetString() == "play" &&
                e.TryGetProperty("group", out var g) && g.GetString() == "MUSIC");
            Assert.Equal(JsonValueKind.Object, nhoodPlay.ValueKind);
            Assert.False(nhoodPlay.GetProperty("loop").GetBoolean());
            var nhoodVoice = nhoodPlay.GetProperty("voice").GetInt64();
            var faState1 = await frontend.SendAsync("audioState");
            Assert.Equal(1, faState1.GetProperty("result").GetProperty("voices").GetInt32());

            var optEventResp = await frontend.SendAsync("audioEvent", new { name = "bkground_option" });
            Assert.True(optEventResp.GetProperty("ok").GetBoolean());
            Assert.True(optEventResp.GetProperty("result").GetProperty("played").GetBoolean());
            Assert.Contains(frontend.DrainEvents(), e =>
                e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
                e.TryGetProperty("command", out var c) && c.GetString() == "stop" &&
                e.GetProperty("voice").GetInt64() == nhoodVoice);
            var faState2 = await frontend.SendAsync("audioState");
            Assert.Equal(0, faState2.GetProperty("result").GetProperty("voices").GetInt32());
        }

        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());

        var camResp = server.HandleJson("modeMusic", new { mode = "camera" }).GetProperty("result");
        Assert.Equal("bkground_camera", camResp.GetProperty("name").GetString());
        Assert.True(camResp.GetProperty("played").GetBoolean());

        var optResp = server.HandleJson("modeMusic", new { mode = "options" }).GetProperty("result");
        Assert.Equal("bkground_option", optResp.GetProperty("name").GetString());

        using var audio = E2EHarness.CreateAudioSession(gameDir);
        Assert.True(audio.Runtime.Play("radio_class", -1));
        var play1 = Assert.Single(audio.PlayEvents());
        Assert.False(play1.GetProperty("loop").GetBoolean(), "Radio station tracks must use IsLoop = false.");
        var voice1 = play1.GetProperty("voice").GetInt64();
        var file1 = play1.GetProperty("file").GetString();

        audio.DrainAudioEvents();
        audio.Runtime.End(voice1);
        var play2 = Assert.Single(audio.PlayEvents());
        Assert.NotEqual(file1, play2.GetProperty("file").GetString());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F02")]
    public void T2_F02_01_EmptyAndSingleTrackPlaylists_StaySilentOrRestartCleanly()
    {
        var gameDir = E2EHarness.RequireGameDir();
        var sampleMp3 = FindAnyMp3(Path.Combine(gameDir, "Music", "Modes", "Buy"));
        Assert.NotNull(sampleMp3);

        var emptyDir = Path.Combine(Path.GetTempPath(), "sims-e2e-f02-empty-" + Guid.NewGuid().ToString("N"));
        var singleDir = Path.Combine(Path.GetTempPath(), "sims-e2e-f02-single-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(emptyDir, "Music", "Modes", "Buy"));
            using (var emptyAudio = E2EHarness.CreateAudioSession(emptyDir))
            {
                Assert.Null(emptyAudio.Bank.Resolve("bkground_buy1"));
                Assert.False(emptyAudio.Runtime.Play("bkground_buy1", -1));
            }

            var buyDir = Path.Combine(singleDir, "Music", "Modes", "Buy");
            Directory.CreateDirectory(buyDir);
            File.Copy(sampleMp3, Path.Combine(buyDir, "only_track.mp3"));

            using (var singleAudio = E2EHarness.CreateAudioSession(singleDir))
            {
                Assert.True(singleAudio.Runtime.Play("bkground_buy1", -1));
                var play1 = Assert.Single(singleAudio.PlayEvents());
                Assert.False(play1.GetProperty("loop").GetBoolean());
                var voice1 = play1.GetProperty("voice").GetInt64();

                singleAudio.DrainAudioEvents();
                singleAudio.Runtime.End(voice1);
                var play2 = Assert.Single(singleAudio.PlayEvents());
                Assert.Contains("only_track", play2.GetProperty("file").GetString(), StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            E2EHarness.DeleteTempTree(emptyDir);
            E2EHarness.DeleteTempTree(singleDir);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F02")]
    public void T2_F02_02_RapidModeSwitching_LeavesOnlyFinalModeMusicActive()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());

        foreach (var mode in new[] { "live", "build", "buy", "options", "live", "buy" })
        {
            var resp = server.HandleJson("modeMusic", new { mode });
            Assert.True(resp.GetProperty("ok").GetBoolean());
        }

        var state = server.HandleJson("audioState").GetProperty("result");
        Assert.Equal(1, state.GetProperty("voices").GetInt32());

        var musicPlays = server.AllEvents.Count(e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
            e.TryGetProperty("command", out var c) && c.GetString() == "play" &&
            e.TryGetProperty("group", out var g) && g.GetString() == "MUSIC");
        var musicStops = server.AllEvents.Count(e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
            e.TryGetProperty("command", out var c) && c.GetString() == "stop");
        Assert.Equal(1, musicPlays - musicStops);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F02")]
    public void T2_F02_03_RadioPlaylistPauseAndUnpause_PreservesAndAdvancesOnEnd()
    {
        var gameDir = E2EHarness.RequireGameDir();

        using var audio = E2EHarness.CreateAudioSession(gameDir);
        Assert.True(audio.Runtime.PlayForSimulationObject("stereo_classical", 10, true, true));
        var play1 = Assert.Single(audio.PlayEvents());
        var voice1 = play1.GetProperty("voice").GetInt64();
        var file1 = play1.GetProperty("file").GetString();

        audio.DrainAudioEvents();
        audio.Runtime.SetPaused(true);
        Assert.Contains(audio.PauseEvents(), e => e.GetProperty("voice").GetInt64() == voice1);

        audio.Runtime.Tick();
        Assert.Empty(audio.PlayEvents());

        audio.DrainAudioEvents();
        audio.Runtime.SetPaused(false);
        Assert.Contains(audio.ResumeEvents(), e => e.GetProperty("voice").GetInt64() == voice1);

        audio.DrainAudioEvents();
        audio.Runtime.End(voice1);
        var play2 = Assert.Single(audio.PlayEvents());
        Assert.NotEqual(file1, play2.GetProperty("file").GetString());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F02")]
    public void T2_F02_04_MissingExpansionMusicAsset_FallsBackGracefully()
    {
        var baseOnlyDir = Path.Combine(Path.GetTempPath(), "sims-e2e-f02-baseonly-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(baseOnlyDir, "Music", "Modes", "Build"));
            using var audio = E2EHarness.CreateAudioSession(baseOnlyDir);

            Assert.Null(audio.Bank.Resolve("station_superstar"));
            Assert.Null(audio.Bank.Resolve("music_magictown"));
            Assert.False(audio.Runtime.Play("station_superstar", -1));
            Assert.False(audio.Runtime.Play("music_magictown", -1));
            Assert.Equal(0, audio.Runtime.ActiveVoiceCount);
        }
        finally
        {
            E2EHarness.DeleteTempTree(baseOnlyDir);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F02")]
    public void T2_F02_05_CorruptOrZeroByteMp3InPlaylist_SkipsToNextValidTrack()
    {
        var gameDir = E2EHarness.RequireGameDir();
        var validRockMp3 = FindAnyMp3(Path.Combine(gameDir, "Music", "Stations", "Rock"));
        Assert.NotNull(validRockMp3);

        var tempGame = Path.Combine(Path.GetTempPath(), "sims-e2e-f02-corrupt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rockDir = Path.Combine(tempGame, "Music", "Stations", "Rock");
            Directory.CreateDirectory(rockDir);
            File.WriteAllBytes(Path.Combine(rockDir, "01_corrupt.mp3"), []);
            File.Copy(validRockMp3, Path.Combine(rockDir, "02_valid.mp3"));

            using var audio = E2EHarness.CreateAudioSession(tempGame);
            Assert.True(audio.Runtime.Play("radio_rock", -1));
            var play = Assert.Single(audio.PlayEvents());
            var playedFile = play.GetProperty("file").GetString()!;
            Assert.Contains("02_valid", playedFile, StringComparison.OrdinalIgnoreCase);

            var diskPath = Path.Combine(audio.SessionAudioDirectory, Path.GetFileName(playedFile));
            using var mpeg = new MpegFile(diskPath);
            Assert.True(mpeg.Length > 0);
        }
        finally
        {
            E2EHarness.DeleteTempTree(tempGame);
        }
    }

    // -------------------------------------------------------------------------
    // Feature F03: Audio Mixer Pitch/Rate Resampling & VOX
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F03")]
    public void T1_F03_01_VariablePitchRateResampling_EmitsPitchScaleAndResamples()
    {
        var gameDir = E2EHarness.RequireGameDir();

        using var audio = E2EHarness.CreateAudioSession(gameDir);
        var fsc = audio.Bank.ResolveAmbienceSequence("setambience_backyard");
        Assert.NotNull(fsc);
        Assert.Contains(fsc.Notes, n => n.PitchL != 0 || n.PitchR != 0);

        Assert.True(audio.Runtime.Play("setambience_backyard", -1));
        // Deterministic HIT VM pitch modulation cue (tv_action_cheap sets pitch=2400 -> pitchScale=0.5f)
        Assert.True(audio.Runtime.PlayForSimulationObject("tv_action_cheap", ownerID: 99, pan: true, zoom: true));
        audio.Runtime.Tick();

        var pitchedPlays = audio.PlayEvents()
            .Where(p => p.TryGetProperty("pitchScale", out _) || p.TryGetProperty("pitch", out _))
            .ToArray();
        Assert.NotEmpty(pitchedPlays);
        Assert.Contains(pitchedPlays, p =>
        {
            var prop = p.TryGetProperty("pitchScale", out var ps) ? ps : p.GetProperty("pitch");
            return Math.Abs(prop.GetSingle() - 1.0f) > 0.001f;
        });

        // Also verify AudioMixer.Voice + MixAndPut resamples proportionally at 0.75f vs 1.35f
        var mixer = new AudioMixer();
        var pcm = new float[675 * 2];
        for (int i = 0; i < pcm.Length; i += 2)
        {
            pcm[i] = MathF.Sin(i * 0.05f);
            pcm[i + 1] = pcm[i];
        }

        mixer.Set(1, new AudioMixer.Voice(pcm) { PitchScale = 0.75f, Loop = false });
        mixer.Set(2, new AudioMixer.Voice(pcm) { PitchScale = 1.35f, Loop = false });
        mixer.MixAndPut(nint.Zero, 500);

        var ended = mixer.TakeEnded();
        Assert.Contains(2L, ended);       // 500 * 1.35 = 675 -> finished
        Assert.DoesNotContain(1L, ended); // 500 * 0.75 = 375 < 675 -> still playing

        double posSlow = 0.0;
        mixer.Update(1, (ref AudioMixer.Voice v) => posSlow = v.FramePosition);
        Assert.InRange(posSlow, 374.9, 375.1);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F03")]
    public void T1_F03_02_SimlishVoxDemographicSelection_MaleFemaleChild()
    {
        var gameDir = E2EHarness.RequireGameDir();

        using var audio = E2EHarness.CreateAudioSession(gameDir);
        var m = audio.Bank.ResolveVoice("vox_laugh_giggle", 'm', 1);
        var f = audio.Bank.ResolveVoice("vox_laugh_giggle", 'f', 1);
        var k = audio.Bank.ResolveVoice("vox_laugh_giggle", 'k', 1);

        Assert.NotNull(m);
        Assert.NotNull(f);
        Assert.NotNull(k);
        Assert.Equal("VOX", m.Group);
        Assert.Equal("VOX", f.Group);
        Assert.Equal("VOX", k.Group);
        Assert.Contains("gigglem", m.RelativePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gigglef", f.RelativePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gigglek", k.RelativePath, StringComparison.OrdinalIgnoreCase);

        audio.Runtime.SetSpeakerDemographic(101, 'm');
        audio.Runtime.SetSpeakerDemographic(102, 'f');
        audio.Runtime.SetSpeakerDemographic(103, 'k');
        Assert.True(audio.Runtime.PlayForSimulationObject("vox_laugh_giggle", 101, false, false));
        Assert.True(audio.Runtime.PlayForSimulationObject("vox_laugh_giggle", 102, false, false));
        Assert.True(audio.Runtime.PlayForSimulationObject("vox_laugh_giggle", 103, false, false));

        var plays = audio.PlayEvents();
        Assert.Equal(3, plays.Count);
        Assert.All(plays, p => Assert.Equal("VOX", p.GetProperty("group").GetString()));
        // Smartlists for vox_laugh_giggle select from gigglem/m, gigglef/f, gigglek/appreck/k patches
        Assert.Matches(new Regex(@"(?:giggle|laugh|apprec)[a-z_]*m\d*", RegexOptions.IgnoreCase), plays[0].GetProperty("file").GetString()!);
        Assert.Matches(new Regex(@"(?:giggle|laugh|apprec)[a-z_]*f\d*", RegexOptions.IgnoreCase), plays[1].GetProperty("file").GetString()!);
        Assert.Matches(new Regex(@"(?:giggle|laugh|apprec)[a-z_]*k\d*", RegexOptions.IgnoreCase), plays[2].GetProperty("file").GetString()!);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F03")]
    public void T1_F03_03_TvStationAudioPlayback_ResolvesAllNineChannelsAndSwitches()
    {
        var gameDir = E2EHarness.RequireGameDir();

        var channels = new[]
        {
            "tv_action_norm",
            "tv_cartoon_norm",
            "tv_romance_norm",
            "tv_mystery_norm",
            "tv_horror_norm",
            "tv_superstar_animal",
            "tv_superstar_entertainment",
            "tv_superstar_news",
            "tv_superstar_sports"
        };

        using var audio = E2EHarness.CreateAudioSession(gameDir);
        foreach (var ch in channels)
        {
            var res = audio.Bank.Resolve(ch);
            Assert.NotNull(res);
            Assert.Equal("FX", res.Group);
            Assert.True(res.Duration > 0f);
            Assert.True(audio.Runtime.PlayForSimulationObject(ch, 30, true, true));
        }

        Assert.Equal(channels.Length, audio.PlayEvents().Count);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F03")]
    public void T1_F03_04_FourChannelVolumeControl_AttenuatesGroupsIndependently()
    {
        var gameDir = E2EHarness.RequireGameDir();

        using var audio = E2EHarness.CreateAudioSession(gameDir);
        Assert.True(audio.Runtime.Play("ui_click"));
        Assert.True(audio.Runtime.Play("bkground_buy1", -1));
        Assert.True(audio.Runtime.PlayForSimulationObject("vox_laugh_giggle", 11, false, false, 'f'));
        Assert.True(audio.Runtime.Play("setambience_backyard", -1));

        var initialGroups = audio.PlayEvents()
            .Select(p => p.GetProperty("group").GetString()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("FX", initialGroups);
        Assert.Contains("MUSIC", initialGroups);
        Assert.Contains("VOX", initialGroups);
        Assert.Contains("AMBIENCE", initialGroups);

        // Verify each group's volume slider emits an update ONLY for that group (cross-channel isolation).
        foreach (var (targetGroup, targetVolume) in new (string Group, float Volume)[]
        {
            ("FX", 0.25f),
            ("MUSIC", 0.60f),
            ("VOX", 0.80f),
            ("AMBIENCE", 0.10f)
        })
        {
            audio.DrainAudioEvents();
            audio.Runtime.SetVolume(targetGroup, targetVolume);
            var updates = audio.UpdateEvents();
            Assert.NotEmpty(updates);
            Assert.All(updates, u => Assert.Equal(targetGroup, u.GetProperty("group").GetString()));
            Assert.Contains(updates, u =>
                u.GetProperty("group").GetString() == targetGroup &&
                u.GetProperty("volume").GetSingle() > 0f &&
                u.GetProperty("volume").GetSingle() <= targetVolume + 0.01f);
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F03")]
    public void T1_F03_05_FourChannelVolumePersistence_SurvivesRestart()
    {
        var gameDir = E2EHarness.RequireGameDir();

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir);

        using (var session1 = E2EHarness.StartServer(profile, house: 0))
        {
            Assert.True(session1.HandleJson("audioVolume", new { group = "FX", volume = 0.25f }).GetProperty("ok").GetBoolean());
            Assert.True(session1.HandleJson("audioVolume", new { group = "MUSIC", volume = 0.60f }).GetProperty("ok").GetBoolean());
            Assert.True(session1.HandleJson("audioVolume", new { group = "VOX", volume = 0.80f }).GetProperty("ok").GetBoolean());
            Assert.True(session1.HandleJson("audioVolume", new { group = "AMBIENCE", volume = 0.10f }).GetProperty("ok").GetBoolean());
            Assert.True(session1.HandleJson("quit").GetProperty("ok").GetBoolean());
        }

        using (var session2 = E2EHarness.StartServer(profile, house: 0))
        {
            var state = session2.HandleJson("audioState").GetProperty("result");
            var vols = state.GetProperty("volumes");
            Assert.InRange(vols.GetProperty("FX").GetSingle(), 0.24f, 0.26f);
            Assert.InRange(vols.GetProperty("MUSIC").GetSingle(), 0.59f, 0.61f);
            Assert.InRange(vols.GetProperty("VOX").GetSingle(), 0.79f, 0.81f);
            Assert.InRange(vols.GetProperty("AMBIENCE").GetSingle(), 0.09f, 0.11f);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F03")]
    public void T2_F03_01_ExtremePitchScaleBounds_ClampsSafelyWithoutNaN()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sims-e2e-f03-t2-01-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var soundData = Path.Combine(tempDir, "SoundData");
            Directory.CreateDirectory(soundData);
            var wavBytes = E2EHarness.BuildSyntheticWav();
            File.WriteAllBytes(Path.Combine(soundData, "amb_note.wav"), wavBytes);
            File.WriteAllText(Path.Combine(soundData, "AmbPitch.hot"),
                "[patch]\n1=0, amb_note, \"SoundData\\amb_note.wav\", 0\n" +
                "[track]\n500=0, amb_note, 0, kArgsNormal, 0, kGroupSfx, 0, , 1\n" +
                "[eventmapping]\namb_note=kPlayTrack, 500\n",
                Encoding.Latin1);

            // AudioEvent.Validate must reject zero/negative or out-of-range pitchScale and accept valid bounded pitchScale
            var jsonZero = """{"command":"play","voice":1,"file":"audio/amb_note.wav","duration":0.1,"group":"AMBIENCE","volume":1.0,"pan":0.0,"loop":false,"pitchScale":0.0}""";
            Assert.Throws<ContentError>(() => AudioEvent.FromJson(jsonZero).Validate());
            var jsonValid = """{"command":"play","voice":1,"file":"audio/amb_note.wav","duration":0.1,"group":"AMBIENCE","volume":1.0,"pan":0.0,"loop":false,"pitchScale":0.5}""";
            AudioEvent.FromJson(jsonValid).Validate();

            // Extreme FSC pitch bounds (-100, +100) fed through SoundBank -> HitAudioRuntime must clamp pitchScale cleanly
            var header = "hdr\t1024\t0\t1\t4\t1\t1\t120\t4\t0\t0\t0\t0\t0\t0";
            var noteRow = "0\t1024\t0\t10\t20\t0\t0\t0\t0\t0\t1\t1\t0\t20\t-100\t100\t0\t0\t0\tamb_note.wav";
            var fscBytes = Encoding.ASCII.GetBytes($"FSC1\n{header}\n{noteRow}\n");
            var fsc = FscSequence.Read(fscBytes, "extreme.fsc");
            Assert.Single(fsc.Notes);
            Assert.Equal(-100, fsc.Notes[0].PitchL);
            Assert.Equal(100, fsc.Notes[0].PitchR);

            File.WriteAllBytes(
                Path.Combine(soundData, "SoundData.far"),
                E2EHarness.BuildSyntheticFarArchive(("Outdoors/sim_amb.fsc", fscBytes), ("amb_note.wav", wavBytes)));

            using var audio = E2EHarness.CreateAudioSession(tempDir);
            Assert.True(audio.Runtime.Play("setambience_backyard", -1));
            var play = Assert.Single(audio.PlayEvents());
            Assert.Equal("AMBIENCE", play.GetProperty("group").GetString());
            Assert.True(play.TryGetProperty("pitchScale", out var ps),
                "Audio play events should expose clamped finite pitchScale contract.");
            float val = ps.GetSingle();
            Assert.True(float.IsFinite(val));
            Assert.InRange(val, 0.0625f, 4.0f);

            // AudioMixer.Voice + MixAndPut must clamp extreme PitchScale values safely without NaN or div-by-zero
            var mixer = new AudioMixer();
            var pcm = new float[2048 * 2];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = 0.25f;

            var extremeCases = new (long Id, float Pitch, double MinPos, double MaxPos)[]
            {
                (1, 0.0f, 255.9, 256.1),
                (2, -1.0f, 255.9, 256.1),
                (3, 0.05f, 15.9, 16.1),   // clamped to 0.0625 -> 256 * 0.0625 = 16
                (4, 8.0f, 1023.9, 1024.1), // clamped to 4.0 -> 256 * 4.0 = 1024
                (5, float.NaN, 255.9, 256.1)
            };
            foreach (var (id, pitch, _, _) in extremeCases)
            {
                mixer.Set(id, new AudioMixer.Voice(pcm) { PitchScale = pitch, Loop = true });
            }
            mixer.MixAndPut(nint.Zero, 256);

            foreach (var (id, _, minPos, maxPos) in extremeCases)
            {
                double pos = double.NaN;
                mixer.Update(id, (ref AudioMixer.Voice v) => pos = v.FramePosition);
                Assert.True(double.IsFinite(pos) && pos > 0.0, $"Voice {id} produced invalid FramePosition {pos}.");
                Assert.InRange(pos, minPos, maxPos);
            }
        }
        finally
        {
            E2EHarness.DeleteTempTree(tempDir);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F03")]
    public void T2_F03_02_ChildVoxFallback_WhenChildPatchAbsent()
    {
        var gameDir = E2EHarness.RequireGameDir();

        using var audio = E2EHarness.CreateAudioSession(gameDir);
        var fallback = audio.Bank.ResolveVoice("vox_flirtee_giggle", 'k', 1);
        Assert.NotNull(fallback);
        Assert.Equal("VOX", fallback.Group);
        Assert.True(audio.Runtime.PlayForSimulationObject("vox_flirtee_giggle", 15, false, false, 'k'));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F03")]
    public void T2_F03_03_VolumeSliderExtremesAndOutOfRangeClamping()
    {
        var gameDir = E2EHarness.RequireGameDir();

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir);
        using (var server = E2EHarness.StartServer(profile))
        {
            Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("audioEvent", new { name = "ui_click" }).GetProperty("ok").GetBoolean());

            var zeroResp = server.HandleJson("audioVolume", new { group = "FX", volume = 0.0f });
            Assert.True(zeroResp.GetProperty("ok").GetBoolean());
            Assert.Equal(0.0f, zeroResp.GetProperty("result").GetProperty("volumes").GetProperty("FX").GetSingle());

            // Verify an FX cue emitted while FX == 0.0f produces zero volume output
            Assert.True(server.HandleJson("audioEvent", new { name = "ui_click" }).GetProperty("ok").GetBoolean());
            var mutedPlay = server.LastEvents.LastOrDefault(e =>
                e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
                e.TryGetProperty("command", out var c) && c.GetString() == "play" &&
                e.TryGetProperty("group", out var g) && g.GetString() == "FX");
            Assert.Equal(JsonValueKind.Object, mutedPlay.ValueKind);
            Assert.Equal(0.0f, mutedPlay.GetProperty("volume").GetSingle());

            var negResp = server.HandleJson("audioVolume", new { group = "FX", volume = -0.5f });
            Assert.False(negResp.GetProperty("ok").GetBoolean());

            var highResp = server.HandleJson("audioVolume", new { group = "FX", volume = 2.5f });
            Assert.False(highResp.GetProperty("ok").GetBoolean());
        }

        // Canonical audio.json schema with out-of-range settings must clamp cleanly to [0.0, 1.0] on reload
        var settingsDir = Path.Combine(profile.Profile, "Settings");
        Directory.CreateDirectory(settingsDir);
        File.WriteAllText(Path.Combine(settingsDir, "audio.json"), """{"schemaVersion":1,"volumes":[-0.5,2.5,0.0,1.0]}""");

        using (var reloaded = E2EHarness.StartServer(profile))
        {
            var vols = reloaded.HandleJson("audioState").GetProperty("result").GetProperty("volumes");
            Assert.Equal(0.0f, vols.GetProperty("FX").GetSingle());
            Assert.Equal(1.0f, vols.GetProperty("MUSIC").GetSingle());
            Assert.Equal(0.0f, vols.GetProperty("VOX").GetSingle());
            Assert.Equal(1.0f, vols.GetProperty("AMBIENCE").GetSingle());

            Assert.True(reloaded.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());
            Assert.True(reloaded.HandleJson("audioEvent", new { name = "ui_click" }).GetProperty("ok").GetBoolean());
            var reloadedMutedPlay = reloaded.LastEvents.LastOrDefault(e =>
                e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
                e.TryGetProperty("command", out var c) && c.GetString() == "play" &&
                e.TryGetProperty("group", out var g) && g.GetString() == "FX");
            Assert.Equal(JsonValueKind.Object, reloadedMutedPlay.ValueKind);
            Assert.Equal(0.0f, reloadedMutedPlay.GetProperty("volume").GetSingle());
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F03")]
    public void T2_F03_04_TvTurnOffOrDeletedMidStream_StopsPlaybackImmediately()
    {
        var gameDir = E2EHarness.RequireGameDir();

        using var audio = E2EHarness.CreateAudioSession(gameDir);
        Assert.True(audio.Runtime.PlayForSimulationObject("tv_action_norm", 30, true, true));
        Assert.Equal(1, audio.Runtime.ActiveVoiceCount);

        audio.DrainAudioEvents();
        Assert.True(audio.Runtime.PlayForSimulationObject("tv_turn_off", 30, true, true),
            "tv_turn_off (track 23) should resolve and kill active TV playback cleanly.");
        Assert.Contains(audio.StopEvents(), _ => true);

        // Also verify RemoveOwner when object is deleted mid-stream
        Assert.True(audio.Runtime.PlayForSimulationObject("tv_romance_norm", 31, true, true));
        audio.DrainAudioEvents();
        audio.Runtime.RemoveOwner(31);
        Assert.NotEmpty(audio.StopEvents());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F03")]
    public void T2_F03_05_ConcurrentMultiSimConversationAtSpeed3_DoesNotSuppressOtherSim()
    {
        var gameDir = E2EHarness.RequireGameDir();

        using var audio = E2EHarness.CreateAudioSession(gameDir);
        audio.Runtime.SuppressActiveDuplicates = true;
        audio.Runtime.SetSimulationSpeed(3);
        audio.Runtime.SetSpeakerDemographic(71, 'f');
        audio.Runtime.SetSpeakerDemographic(72, 'm');

        Assert.True(audio.Runtime.PlayForSimulationObject("vox_laugh_giggle", 71, true, true));
        Assert.True(audio.Runtime.PlayForSimulationObject("vox_laugh_giggle", 72, true, true),
            "Speed 3 must not suppress VOX from a different speaker (Sim 72) while Sim 71 is speaking.");
        Assert.Equal(2, audio.PlayEvents().Count);
    }

    // -------------------------------------------------------------------------
    // Feature F04: Spatial Mixing & Codecs
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F04")]
    public void T1_F04_01_CodecDecoding_XaUtkMp3WavFsc_AllDecodeCleanly()
    {
        var gameDir = E2EHarness.RequireGameDir();

        // 1. XA from FAR
        var far = FarArchive.Open(Path.Combine(gameDir, "SoundData", "SoundData.far"));
        var xaEntry = far.Find("UI/UI_CLICK.XA");
        Assert.NotNull(xaEntry);
        var xaDecoded = XaAudio.Decode(far.Read(xaEntry).Span);
        Assert.True(xaDecoded.TotalSamples > 0 && xaDecoded.Duration > 0f);

        // 2. WAV via SoundBank
        using var audio = E2EHarness.CreateAudioSession(gameDir);
        var wavRes = audio.Bank.Resolve("Sting_Fire");
        Assert.NotNull(wavRes);
        Assert.True(wavRes.Duration > 0f);

        // 3. MP3 via NLayer
        var mp3Res = audio.Bank.Resolve("bkground_buy1");
        Assert.NotNull(mp3Res);
        using (var mpeg = new MpegFile(Path.Combine(audio.SessionAudioDirectory, Path.GetFileName(mp3Res.RelativePath))))
        {
            var buf = new float[1024];
            Assert.True(mpeg.ReadSamples(buf, 0, buf.Length) > 0);
        }

        // 4. FSC ambience sequence
        var fsc = audio.Bank.ResolveAmbienceSequence("setambience_backyard");
        Assert.NotNull(fsc);
        Assert.NotEmpty(fsc.Notes);

        // 5. UTK synthetic valid UTM0 packet (Complete Collection stores VOX as .xa)
        var utkDecoded = UtkAudio.Decode(E2EHarness.BuildValidSyntheticUtk(432));
        Assert.Equal(432, utkDecoded.TotalSamples);
        Assert.True(utkDecoded.WavBytes.Length >= 44 + 432 * 2);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F04")]
    public void T1_F04_02_StereoPanTracking_ShiftsLeftCenterRight()
    {
        var viewport = new Vector2(1280f, 720f);
        var (leftPan, _) = PositionalSound.Mix(new Vector2(0f, 360f), viewport, panned: true);
        var (centerPan, centerGain) = PositionalSound.Mix(new Vector2(640f, 360f), viewport, panned: true);
        var (rightPan, _) = PositionalSound.Mix(new Vector2(1280f, 360f), viewport, panned: true);

        Assert.Equal(-1f, leftPan, 3);
        Assert.Equal(0f, centerPan, 3);
        Assert.Equal(1f, rightPan, 3);
        Assert.Equal(1f, centerGain, 3);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F04")]
    public void T1_F04_03_CameraZoomAndDistanceAttenuation_DecreasesMonotonically()
    {
        float nearZoom = LotZoom.Near.SoundGain();
        float medZoom = LotZoom.Medium.SoundGain();
        float farZoom = LotZoom.Far.SoundGain();
        Assert.True(nearZoom > medZoom && medZoom > farZoom);

        float g2 = HitAudioRuntime.AttenuationGain(2f);
        float g5 = HitAudioRuntime.AttenuationGain(5f);
        float g15 = HitAudioRuntime.AttenuationGain(15f);
        float g40 = HitAudioRuntime.AttenuationGain(40f);
        Assert.Equal(1f, g2, 3);
        Assert.Equal(1f, g5, 3);
        Assert.True(g5 > g15 && g15 > g40);
        Assert.Equal(0f, g40, 3);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F04")]
    public void T1_F04_04_IndoorOutdoorAndFloorMuffle_EngagesLowPassAndFloorGain()
    {
        Assert.Equal(0.25f, PositionalSound.FloorGain(2, 1), 3);
        Assert.Equal(1.00f, PositionalSound.FloorGain(1, 1), 3);
        Assert.Equal(0.50f, PositionalSound.FloorGain(1, 2), 3);

        var mixer = new AudioMixer();
        mixer.SetMuffle(0.5f, 0.2f);

        var samples = new float[512 * 2];
        for (int i = 0; i < samples.Length; i += 2)
        {
            samples[i] = i % 4 == 0 ? 1f : -1f;
            samples[i + 1] = samples[i];
        }

        mixer.Set(1, new AudioMixer.Voice(samples) { Outdoors = true, Loop = true });
        mixer.MixAndPut(nint.Zero, 256);

        float lowLeftAfter = 0f;
        mixer.Update(1, (ref AudioMixer.Voice v) => lowLeftAfter = v.LowLeft);
        Assert.NotEqual(0f, lowLeftAfter);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F04")]
    public void T1_F04_05_VoiceStealingUnderPolyphonyPressure_RespectsMaxVoices()
    {
        var gameDir = E2EHarness.RequireGameDir();

        using var audio = E2EHarness.CreateAudioSession(gameDir);
        for (short i = 1; i <= 75; i++)
        {
            audio.Runtime.PlayForSimulationObject("ui_click", i, true, true);
        }

        Assert.InRange(audio.Runtime.ActiveVoiceCount, 1, HitAudioRuntime.MaxVoices);
        Assert.NotEmpty(audio.StopEvents());

        var mixer = new AudioMixer();
        var pcm = new float[256];
        for (long i = 1; i <= 64; i++)
            mixer.Set(i, new AudioMixer.Voice(pcm) { Loop = false });

        var evicted = mixer.EvictIfFull(64, protectedVoice: 1);
        Assert.NotNull(evicted);
        Assert.NotEqual(1, evicted.Value);
        Assert.Equal(63, mixer.Count);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F04")]
    public void T2_F04_01_MissingFarArchiveOrMissingSoundId_ReturnsGracefully()
    {
        var gameDir = E2EHarness.RequireGameDir();

        using var audio = E2EHarness.CreateAudioSession(gameDir);
        Assert.Null(audio.Bank.Resolve("nonexistent_asset_xyz_999"));
        Assert.Null(audio.Bank.ResolveVoice("nonexistent_vox_xyz_999", 'm'));
        Assert.False(audio.Runtime.Play("nonexistent_asset_xyz_999"));

        var far = FarArchive.Open(Path.Combine(gameDir, "SoundData", "SoundData.far"));
        Assert.Null(far.Find("MISSING/DOES_NOT_EXIST.XA"));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F04")]
    public async Task T2_F04_02_CorruptCodecHeaders_XaUtkFsc_AbortWithoutCrashOrHang()
    {
        // 1. Direct codec guards
        Assert.Throws<ArgumentException>(() => XaAudio.Decode([0x58, 0x41, 0x00, 0x01])); // truncated XA
        var badChannelsXa = new byte[54];
        badChannelsXa[0] = (byte)'X';
        badChannelsXa[1] = (byte)'A';
        BinaryPrimitives.WriteUInt16LittleEndian(badChannelsXa.AsSpan(10, 2), 99);
        Assert.Throws<NotSupportedException>(() => XaAudio.Decode(badChannelsXa));

        // 2. Truncated UTM0 must abort cleanly within 2s instead of infinite BitReader loop
        var truncatedUtm0 = new byte[32];
        truncatedUtm0[0] = (byte)'U';
        truncatedUtm0[1] = (byte)'T';
        truncatedUtm0[2] = (byte)'M';
        truncatedUtm0[3] = (byte)'0';
        BinaryPrimitives.WriteUInt32LittleEndian(truncatedUtm0.AsSpan(4, 4), 88200);
        BinaryPrimitives.WriteUInt16LittleEndian(truncatedUtm0.AsSpan(14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(truncatedUtm0.AsSpan(16, 4), 22050);

        Exception? utkException = null;
        int decodedUtkSamples = -1;
        var utkTask = Task.Run(() =>
        {
            try { decodedUtkSamples = UtkAudio.Decode(truncatedUtm0).TotalSamples; }
            catch (Exception ex) { utkException = ex; }
        });
        var utkFinished = await Task.WhenAny(utkTask, Task.Delay(TimeSpan.FromSeconds(2))) == utkTask;

        Assert.True(utkFinished, "UtkAudio.Decode hung on truncated UTM0 buffer (BitReader infinite loop).");
        await utkTask;
        Assert.True(utkException is not null || decodedUtkSamples == 0,
            $"Truncated UTM0 must abort cleanly (throw or decode 0 samples), not fabricate {decodedUtkSamples} samples from EOF zeros.");

        // 3. Malformed FSC
        Assert.Throws<ResourceFormatException>(() => FscSequence.Read("NOT_FSC\n"u8.ToArray(), "bad.fsc"));

        // 4. SoundBank.Resolve on corrupt XA file must return null rather than throwing out of SoundBank
        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-e2e-f04-t2-02-" + Guid.NewGuid().ToString("N"));
        try
        {
            var soundData = Path.Combine(tempRoot, "SoundData");
            Directory.CreateDirectory(soundData);
            File.WriteAllBytes(Path.Combine(soundData, "bad_xa.xa"), badChannelsXa);
            File.WriteAllText(Path.Combine(soundData, "Corrupt.hot"),
                "[patch]\n1=0, bad_xa_patch, \"SoundData\\bad_xa.xa\", 0\n" +
                "[track]\n500=0, bad_xa_event, 0, kArgsNormal, 0, kGroupSfx, 0, , 1\n" +
                "[eventmapping]\nbad_xa_event=kPlayTrack, 500\n",
                Encoding.Latin1);

            using var audio = E2EHarness.CreateAudioSession(tempRoot);
            Assert.Null(audio.Bank.Resolve("bad_xa_event"));
        }
        finally
        {
            E2EHarness.DeleteTempTree(tempRoot);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F04")]
    public void T2_F04_03_SoundEmitterFarOutsideLotBounds_ClampsPanAndZeroGain()
    {
        var (panLeft, gainLeft) = PositionalSound.Mix(new Vector2(-2000f, 360f), new Vector2(1280f, 720f), panned: true);
        var (panRight, gainRight) = PositionalSound.Mix(new Vector2(4000f, 360f), new Vector2(1280f, 720f), panned: true);

        Assert.Equal(-1f, panLeft, 3);
        Assert.Equal(1f, panRight, 3);
        Assert.Equal(0f, gainLeft, 3);
        Assert.Equal(0f, gainRight, 3);

        var atten = HitAudioRuntime.AttenuationGain(500f);
        Assert.True(float.IsFinite(atten));
        Assert.Equal(0f, atten, 3);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F04")]
    public void T2_F04_04_ProtectedUiAndMusicVoices_SurvivePolyphonyStealing()
    {
        var gameDir = E2EHarness.RequireGameDir();

        using var audio = E2EHarness.CreateAudioSession(gameDir);
        Assert.True(audio.Runtime.Play("bkground_buy1", -1));
        Assert.True(audio.Runtime.Play("ui_click"));

        var musicVoice = audio.PlayEvents().First(e => e.GetProperty("group").GetString() == "MUSIC").GetProperty("voice").GetInt64();
        var uiVoice = audio.PlayEvents().First(e => e.GetProperty("group").GetString() == "FX").GetProperty("voice").GetInt64();

        for (short i = 1; i <= 75; i++)
        {
            audio.Runtime.PlayForSimulationObject("footstep_hard", i, true, true);
        }

        var stoppedVoices = audio.StopEvents().Select(e => e.GetProperty("voice").GetInt64()).ToHashSet();
        Assert.DoesNotContain(musicVoice, stoppedVoices);
        Assert.DoesNotContain(uiVoice, stoppedVoices);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F04")]
    public void T2_F04_05_RapidCameraRotation_UpdatesStereoPanImmediately()
    {
        var sceneBase = new LotScene
        {
            SchemaVersion = 1,
            House = 5,
            Width = 2560,
            Height = 1920,
            Textures = [],
            Sprites = [],
            Limitations = [],
            LotWidth = 64,
            LotHeight = 64,
            OriginX = 0f,
            OriginY = 0f,
            CameraOrientation = 0
        };
        var source = new LotScene.SoundSource { ObjectID = 42, Level = 1, X = 10f, Y = 50f, Z = 0f };
        var viewport = new Vector2(1280f, 720f);

        var pans = new float[4];
        for (int rot = 0; rot < 4; rot++)
        {
            var scene = sceneBase with { CameraOrientation = rot };
            var pt = PositionalSound.ScenePoint(source, scene);
            Assert.NotNull(pt);
            // Center around origin for viewport screen coordinates
            var screen = new Vector2(viewport.X * 0.5f + pt.Value.X, viewport.Y * 0.5f + pt.Value.Y);
            pans[rot] = PositionalSound.Mix(screen, viewport, panned: true).Pan;
        }

        Assert.True(pans.Distinct().Count() >= 3, "Rotating camera 90 degrees should update stereo pan across orientations.");
        Assert.True(Math.Sign(pans[0]) != Math.Sign(pans[2]) || Math.Abs(pans[0] - pans[2]) > 0.1f);
    }
}
