using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SimsEngine.Formats;
using SimsSDL;
using SimsSDL.Core;
using SimsSDL.UI;
using Xunit;

namespace SimsEngine.Tests;

public class HitVmAndAudioParityTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    private static List<byte> BuildBytes(Action<List<byte>> emit)
    {
        var list = new List<byte>();
        emit(list);
        return list;
    }

    private static void EmitInt32(List<byte> buf, int val)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(tmp, val);
        buf.AddRange(tmp);
    }

    [Fact]
    public void HitVm_OpcodesTrackDataJumpsArithmeticWaitAndNoteOffTick_ExecuteGenuinely()
    {
        // Build a synthetic HIT module exercising:
        // 0x04 loadb, 0x05 loadl, 0x06 set, 0x07 call (4-byte TrackDataID), 0x0d jump (4-byte TrackDataID),
        // 0x10-0x19 arithmetic/cmp/less/greater/not/abs, 0x1f get_var, 0x28-0x30 logic/cap/floor/inc/dec,
        // 0x3e-0x43 conditional jumps, 0x48 getsrcdatafield, 0x01 note (noteOffTick), 0x08/0x0c return/end.
        var hit = new List<byte>();
        var tkdOffsets = new Dictionary<int, int>();

        // Subroutine 100 (offset 0): main entry
        tkdOffsets[100] = hit.Count;
        // getsrcdatafield v1, arg1, v8 (where v8 == 0)
        hit.AddRange([0x04, HitReg.V8, 0]);       // loadb v8, 0
        hit.AddRange([0x48, HitReg.V1, HitReg.Arg1, HitReg.V8]); // getsrcdatafield v1, arg1, v8 -> 2 (child)
        hit.AddRange([0x04, HitReg.V2, 2]);       // loadb v2, 2
        hit.AddRange([0x14, HitReg.V1, HitReg.V2]); // cmp v1, v2 (ZF=1)
        hit.Add(0x3e); EmitInt32(hit, 200);       // ifeq 200 (must branch to tkd 200!)
        hit.Add(0x1b);                            // error (must NOT execute)

        // Subroutine 200: arithmetic + call 300 + note with noteOffTick = 4 ticks
        tkdOffsets[200] = hit.Count;
        hit.AddRange([0x04, HitReg.V3, 10]);      // v3 = 10
        hit.AddRange([0x04, HitReg.V4, 3]);       // v4 = 3
        hit.AddRange([0x10, HitReg.V3, HitReg.V4]); // v3 = 13 (add)
        hit.AddRange([0x11, HitReg.V3, HitReg.V4]); // v3 = 10 (sub)
        hit.AddRange([0x13, HitReg.V3, HitReg.V4]); // v3 = 30 (mul)
        hit.AddRange([0x12, HitReg.V3, HitReg.V4]); // v3 = 10 (div)
        hit.AddRange([0x2f, HitReg.V3]);          // inc v3 -> 11
        hit.AddRange([0x30, HitReg.V3]);          // dec v3 -> 10
        hit.Add(0x2d); hit.Add(HitReg.V3); EmitInt32(hit, 8); // max (cap to min(10,8)) -> 8
        hit.Add(0x2e); hit.Add(HitReg.V3); EmitInt32(hit, 12); // min (floor to max(8,12)) -> 12
        hit.Add(0x07); EmitInt32(hit, 300);       // call 300 (sets pitch = 4800 -> +1200 cents -> 2.0x)
        hit.Add(0x05); hit.Add(HitReg.Patch); EmitInt32(hit, 50); // patch = 50
        hit.AddRange([0x04, HitReg.V5, 4]);       // v5 = 4 ticks duration
        hit.AddRange([0x01, HitReg.V5, HitReg.V5]); // note v5, v5 -> noteOffTick = now + 4
        hit.AddRange([0x04, HitReg.V6, 2]);
        hit.AddRange([0x09, HitReg.V6]);          // wait 2 ticks
        hit.Add(0x0b);                            // wait_samp
        hit.Add(0x0c);                            // end

        // Subroutine 300: pitch = 4800, return
        tkdOffsets[300] = hit.Count;
        hit.Add(0x05); hit.Add(HitReg.Pitch); EmitInt32(hit, 4800);
        hit.Add(0x08); // return

        var mod = new HitBankModule("test", hit.ToArray());
        foreach (var (k, v) in tkdOffsets) mod.TrackDataOffsets[k] = v;
        mod.Patches[50] = new HotPatchDef(50, "PchTest", "test.wav", false);
        var trk = new HotTrackDef(1, "TrkTest", "FX", 1024, 10, 20, 0, "@100", tkdOffsets[100], false, true, mod);
        mod.Tracks[1] = trk;

        var vm = new HitVm([mod], getSourceDataField: (obj, fld) => (obj == 77 && fld == 0) ? 2 : 0, rng: new Random(1));
        var thread = vm.StartTrack(trk, arg1: 77);
        Assert.NotNull(thread);
        Assert.Equal(12, thread.Registers[HitReg.V3]);
        Assert.Equal(4800, thread.Registers[HitReg.Pitch]);

        var cmds = vm.DrainCommands();
        var startCmd = Assert.Single(cmds.OfType<HitStartVoiceCommand>());
        Assert.Equal(50, startCmd.PatchId);
        Assert.Equal(2.0f, startCmd.PitchScale, 3);
        vm.BindVoice(thread.InstanceId, 901);

        // At tick 1+2, wait 2 expires -> hits wait_samp -> still playing.
        vm.StepTicks(2);
        Assert.Empty(vm.DrainCommands());
        Assert.NotNull(vm.GetThread(thread.InstanceId));

        // At tick 1+4 (2 more ticks), noteOffTick fires -> emits HitStopVoiceCommand!
        vm.StepTicks(2);
        var stopCmds = vm.DrainCommands();
        var stopCmd = Assert.Single(stopCmds.OfType<HitStopVoiceCommand>());
        Assert.Equal(901, stopCmd.VoiceId);
    }

    [Fact]
    public void HitSmartList_CrushAndFifoRecycling_FollowsDecompiledSemantics()
    {
        var list = new HitSmartList([10, 20, 30, 40]); // default crush = 4 >> 1 = 2
        Assert.Equal(2, list.Crush);
        Assert.False(list.HasExplicitCrush);

        var rng = new Random(42);
        var picks = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            picks.Add(list.Choose(rng)!.Value);
        }
        // With crush=2, no item can repeat within 2 consecutive picks.
        for (int i = 2; i < picks.Count; i++)
        {
            Assert.NotEqual(picks[i - 1], picks[i]);
            Assert.NotEqual(picks[i - 2], picks[i]);
        }

        // Explicit crush >= total size visits all items without repeating until exhausted, then recycles FIFO.
        var full = new HitSmartList([1, 2, 3], explicitCrush: 1_000_000);
        Assert.True(full.HasExplicitCrush);
        var round1 = new[] { full.Choose(rng)!.Value, full.Choose(rng)!.Value, full.Choose(rng)!.Value };
        Assert.Equal(3, round1.Distinct().Count());
        var round2 = new[] { full.Choose(rng)!.Value, full.Choose(rng)!.Value, full.Choose(rng)!.Value };
        Assert.Equal(round1, round2);
    }

    [Fact]
    public void HitVm_DuckingSeqGroupFadeAndTargetForwarding_ExecuteCorrectly()
    {
        var hit = new List<byte>();
        var tkdOffsets = new Dictionary<int, int>();

        // Track 1 (Music, duckPri = 10): note_on patch 10, wait_samp, loop
        tkdOffsets[1] = hit.Count;
        hit.Add(0x21); // set_loop
        hit.AddRange([0x02, 10]); // note_on 10
        hit.Add(0x0b); // wait_samp
        hit.Add(0x20); // loop

        // Track 2 (Sting, sets main_duckpri = 30, plays patch 20, wait 3 ticks, clears main_duckpri = 0, ends)
        tkdOffsets[2] = hit.Count;
        hit.AddRange([0x04, HitReg.V1, 30]);
        hit.AddRange([0x06, HitReg.MainDuckPri, HitReg.V1]);
        hit.AddRange([0x02, 20]);
        hit.AddRange([0x04, HitReg.V2, 3]);
        hit.AddRange([0x09, HitReg.V2]);
        hit.AddRange([0x04, HitReg.V1, 0]);
        hit.AddRange([0x06, HitReg.MainDuckPri, HitReg.V1]);
        hit.Add(0x0c);

        // Track 3 (Fader): targets Track 1, sets fade_dest=0, fade_var=vol(0x12), fade_speed=512, fade_on=1, waiteq fade_on, 0, kill_trk arg1
        tkdOffsets[3] = hit.Count;
        hit.AddRange([0x06, HitReg.Target, HitReg.Arg1]); // target = arg1 (Track 1)
        hit.Add(0x05); hit.Add(HitReg.FadeDest); EmitInt32(hit, 0);
        hit.AddRange([0x04, HitReg.FadeVar, HitReg.Vol]);
        hit.Add(0x05); hit.Add(HitReg.FadeSpeed); EmitInt32(hit, 512);
        hit.AddRange([0x04, HitReg.FadeOn, 1]);
        hit.AddRange([0x04, HitReg.V1, 0]);
        hit.AddRange([0x4d, HitReg.FadeOn, HitReg.V1]); // waiteq fade_on, v1(0) -> polls target's fade_on!
        hit.AddRange([0x4a, HitReg.V2, HitReg.Arg1]); // setll v2, arg1 (local)
        hit.AddRange([0x04, HitReg.Target, 0]); // clear target
        hit.AddRange([0x33, HitReg.V2]); // kill_trk v2
        hit.Add(0x0c);

        var mod = new HitBankModule("duckfade", hit.ToArray());
        foreach (var (k, v) in tkdOffsets) mod.TrackDataOffsets[k] = v;
        mod.Patches[10] = new HotPatchDef(10, "PchMusic", "music.wav", true);
        mod.Patches[20] = new HotPatchDef(20, "PchSting", "sting.wav", false);
        var trk1 = new HotTrackDef(1, "Music", "MUSIC", 1024, 10, 20, 10, "@1", tkdOffsets[1], true, false, mod);
        var trk2 = new HotTrackDef(2, "Sting", "FX", 1024, 30, 20, 20, "@2", tkdOffsets[2], false, false, mod);
        var trk3 = new HotTrackDef(3, "Fader", "FX", 1024, 10, 20, 0, "@3", tkdOffsets[3], false, false, mod);
        mod.Tracks[1] = trk1;
        mod.Tracks[2] = trk2;
        mod.Tracks[3] = trk3;

        var vm = new HitVm([mod], rng: new Random(1));
        var t1 = vm.StartTrack(trk1)!;
        var s1 = Assert.Single(vm.DrainCommands().OfType<HitStartVoiceCommand>());
        Assert.Equal(1.0f, s1.Gain, 3);
        vm.BindVoice(t1.InstanceId, 101);

        // Start ducker Track 2 -> Track 1 (duckpri 10 < 30) should halve to 0.5f.
        var t2 = vm.StartTrack(trk2)!;
        var cmds2 = vm.DrainCommands();
        Assert.Equal(30, vm.MainDuckPriority);
        var duckUpdate = Assert.Single(cmds2.OfType<HitUpdateVoiceCommand>(), u => u.VoiceId == 101);
        Assert.Equal(0.5f, duckUpdate.Gain, 3);
        vm.BindVoice(t2.InstanceId, 102);

        // After 3 ticks, Track 2 clears main_duckpri -> Track 1 restores to 1.0f.
        vm.StepTicks(3);
        var cmds3 = vm.DrainCommands();
        Assert.Equal(0, vm.MainDuckPriority);
        var unduckUpdate = Assert.Single(cmds3.OfType<HitUpdateVoiceCommand>(), u => u.VoiceId == 101);
        Assert.Equal(1.0f, unduckUpdate.Gain, 3);

        // Start Track 3 targeting Track 1 -> fades vol 1024 -> 512 -> 0 over 2 ticks, then kills Track 1!
        vm.StartTrack(trk3, arg1: 1);
        vm.DrainCommands();
        vm.StepTicks(6);
        var fadeCmds = vm.DrainCommands();
        Assert.Contains(fadeCmds.OfType<HitUpdateVoiceCommand>(), u => u.VoiceId == 101 && u.Gain < 0.6f);
        Assert.Contains(fadeCmds.OfType<HitStopVoiceCommand>(), s => s.VoiceId == 101);
        Assert.Null(vm.GetThread(t1.InstanceId));
    }

    [Fact]
    public void HitVm_SeqGroupKillAndReturn_ManageExclusiveGroupThreads()
    {
        var hit = new List<byte>();
        var tkdOffsets = new Dictionary<int, int>();

        // 1: seqgroup_kill arg1 -> note_on 10 -> wait_samp -> end
        tkdOffsets[1] = hit.Count;
        hit.AddRange([0x45, HitReg.Arg1]); // seqgroup_kill arg1
        hit.AddRange([0x49, HitReg.Arg1, HitReg.V1]); // seqgroup_trkid grpReg=arg1, dest=v1 -> records self trackId
        hit.AddRange([0x02, 10]);
        hit.Add(0x0b);
        hit.Add(0x0c);

        // 2: seqgroup_return arg1 -> if occupied by another thread, kills self immediately!
        tkdOffsets[2] = hit.Count;
        hit.AddRange([0x47, HitReg.Arg1]);
        hit.AddRange([0x02, 10]);
        hit.Add(0x0c);

        var mod = new HitBankModule("seq", hit.ToArray());
        foreach (var (k, v) in tkdOffsets) mod.TrackDataOffsets[k] = v;
        mod.Patches[10] = new HotPatchDef(10, "Pch", "a.wav", false);
        var trk1 = new HotTrackDef(11, "T1", "VOX", 1024, 10, 20, 10, "@1", tkdOffsets[1], false, true, mod);
        var trk2 = new HotTrackDef(12, "T2", "VOX", 1024, 10, 20, 10, "@2", tkdOffsets[2], false, true, mod);
        mod.Tracks[11] = trk1;
        mod.Tracks[12] = trk2;

        var vm = new HitVm([mod]);
        var t1 = vm.StartTrack(trk1, arg1: 55)!;
        Assert.Equal(11, t1.Registers[HitReg.V1]);
        Assert.Single(vm.DrainCommands().OfType<HitStartVoiceCommand>());
        vm.BindVoice(t1.InstanceId, 501);

        // seqgroup_return on busy group 55 kills t2 without emitting a voice.
        var t2 = vm.StartTrack(trk2, arg1: 55)!;
        Assert.Empty(vm.DrainCommands());
        Assert.Null(vm.GetThread(t2.InstanceId));

        // seqgroup_kill on group 55 starts fade-kill on t1 and takes ownership.
        var t3 = vm.StartTrack(trk1, arg1: 55)!;
        Assert.True(t1.FadeKillPending);
        Assert.Single(vm.DrainCommands().OfType<HitStartVoiceCommand>());
        vm.StepTicks(10); // extvol 1024 -> 0 at 200/tick in 6 ticks -> FadeKillPending kills t1
        Assert.Contains(vm.DrainCommands().OfType<HitStopVoiceCommand>(), s => s.VoiceId == 501);
        Assert.Null(vm.GetThread(t1.InstanceId));
        Assert.NotNull(vm.GetThread(t3.InstanceId));
    }

    [Fact]
    public void AudioEvent_PitchScale_SerializesAndValidates()
    {
        var valid = AudioEvent.FromJson("""
            {"command":"play","voice":1,"file":"audio/test.wav","duration":1.0,"group":"FX","volume":0.8,"pan":0.0,"loop":false,"pitchScale":0.5}
            """);
        valid.Validate();
        Assert.Equal(0.5f, valid.PitchScale);

        var badZero = valid with { PitchScale = 0f };
        Assert.Throws<ContentError>(() => badZero.Validate());

        var badHigh = valid with { PitchScale = 4.5f };
        Assert.Throws<ContentError>(() => badHigh.Validate());

        var badNaN = valid with { PitchScale = float.NaN };
        Assert.Throws<ContentError>(() => badNaN.Validate());
    }

    [Fact]
    public void AudioMixer_PitchScaleResampling_MuffleAndEndedCleanup_WorkAccurately()
    {
        // 1. PitchScale == 2.0f finishes a 100-frame voice in 50 output frames.
        var mixer = new AudioMixer();
        var samples = new float[200]; // 100 stereo frames
        for (int f = 0; f < 100; f++)
        {
            samples[f * 2] = f / 100f;
            samples[f * 2 + 1] = f / 100f;
        }

        var vFast = new AudioMixer.Voice(samples) { PitchScale = 2.0f };
        mixer.Set(1, vFast);
        mixer.MixAndPut(nint.Zero, 40);
        Assert.Empty(mixer.TakeEnded());
        mixer.MixAndPut(nint.Zero, 20);
        Assert.Equal([1L], mixer.TakeEnded());

        // 2. PitchScale == 0.5f preserves fractional FramePosition across chunks & loop wraps.
        var loopSamples = new float[8]; // 4 stereo frames
        var vSlow = new AudioMixer.Voice(loopSamples) { PitchScale = 0.5f, Loop = true };
        mixer.Set(2, vSlow);
        mixer.MixAndPut(nint.Zero, 3); // pos = 1.5
        mixer.Update(2, (ref AudioMixer.Voice v) =>
        {
            Assert.Equal(1.5, v.FramePosition, 6);
            v.PitchScale = 1.0f; // Update preserves FramePosition!
        });
        mixer.MixAndPut(nint.Zero, 3); // 1.5 + 3.0 = 4.5 -> wraps to 0.5
        mixer.Update(2, (ref AudioMixer.Voice v) =>
        {
            Assert.Equal(0.5, v.FramePosition, 6);
        });
        Assert.Empty(mixer.TakeEnded());

        // 3. Set(id, null) purges pending _ended, and zero-length non-looping voices end cleanly.
        mixer.Set(3, new AudioMixer.Voice([]) { Loop = false });
        mixer.MixAndPut(nint.Zero, 4);
        mixer.Set(3, null);
        Assert.Empty(mixer.TakeEnded());
    }

    [Fact]
    public void HitAudioRuntime_ModeTransitionsPlaylistsTvAndPersistence_RealAssets()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-m1-parity-{Guid.NewGuid():N}");
        var profileDir = Path.Combine(tempDir, "profile");
        Directory.CreateDirectory(profileDir);
        try
        {
            var events = new List<object>();
            using var audio = new HitAudioRuntime(Path.Combine(tempDir, "session"), profileDir, msg =>
            {
                if (msg is not null) events.Add(msg);
            }, gameDir);

            // 1. Mode 5 (bkground_load) resolves Load/loadloop.wav as looping.
            events.Clear();
            Assert.True(audio.Play("bkground_load"));
            var loadMsg = events[^1];
            Assert.Contains("loadloop", loadMsg.GetType().GetProperty("file")?.GetValue(loadMsg)?.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(true, loadMsg.GetType().GetProperty("loop")?.GetValue(loadMsg));

            // 2. Mode 1 (bkground_buy1) stops bkground_load, plays non-looped MP3, and End(voiceId) advances to next track!
            events.Clear();
            Assert.True(audio.Play("bkground_buy1"));
            Assert.Contains(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "stop");
            var play1 = events.Last(e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play");
            Assert.Equal(false, play1.GetType().GetProperty("loop")?.GetValue(play1));
            var buyVoice1 = (long)play1.GetType().GetProperty("voice")?.GetValue(play1)!;
            var buyFile1 = play1.GetType().GetProperty("file")?.GetValue(play1)?.ToString();

            events.Clear();
            audio.End(buyVoice1);
            var play2 = Assert.Single(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play");
            var buyVoice2 = (long)play2.GetType().GetProperty("voice")?.GetValue(play2)!;
            var buyFile2 = play2.GetType().GetProperty("file")?.GetValue(play2)?.ToString();
            Assert.NotEqual(buyFile1, buyFile2);

            // 3. In-lot stereo (OwnerId > 0) survives mode 0/7/8/9 (bkground_live/option/family/fade)!
            events.Clear();
            Assert.True(audio.PlayForSimulationObject("stereo_country", ownerID: 42, pan: true, zoom: true));
            var stereoPlay = events.Last(e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play");
            var stereoVoice = (long)stereoPlay.GetType().GetProperty("voice")?.GetValue(stereoPlay)!;
            var stereoFile1 = stereoPlay.GetType().GetProperty("file")?.GetValue(stereoPlay)?.ToString();

            // End(stereoVoice) advances HIT VM RadioStationAutoPatch playlist!
            events.Clear();
            audio.End(stereoVoice);
            var stereoPlay2 = Assert.Single(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play");
            var stereoVoice2 = (long)stereoPlay2.GetType().GetProperty("voice")?.GetValue(stereoPlay2)!;
            Assert.NotEqual(stereoFile1, stereoPlay2.GetType().GetProperty("file")?.GetValue(stereoPlay2)?.ToString());

            // radio_next_song (kSndobCancelNote=19) advances station immediately!
            events.Clear();
            Assert.True(audio.Play("radio_next_song", ownerID: 42));
            Assert.Contains(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "stop" && (long)e.GetType().GetProperty("voice")?.GetValue(e)! == stereoVoice2);
            var stereoPlay3 = Assert.Single(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play");
            var stereoVoice3 = (long)stereoPlay3.GetType().GetProperty("voice")?.GetValue(stereoPlay3)!;

            // bkground_fade stops mode music (buyVoice2) WITHOUT killing stereoVoice3!
            events.Clear();
            Assert.True(audio.Play("bkground_fade"));
            Assert.Contains(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "stop" && (long)e.GetType().GetProperty("voice")?.GetValue(e)! == buyVoice2);
            Assert.DoesNotContain(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "stop" && (long)e.GetType().GetProperty("voice")?.GetValue(e)! == stereoVoice3);

            // bkground_live (mode 0) & bkground_option (mode 7) toggle ambience and stop mode music cleanly.
            Assert.True(audio.PlayLotAmbience(5));
            events.Clear();
            Assert.True(audio.Play("bkground_option"));
            Assert.Contains(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "pause");
            events.Clear();
            Assert.True(audio.Play("bkground_live"));
            Assert.Contains(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "resume");

            // 4. TV stations: tv_action_cheap gets PitchScale = 0.5f (pitch=2400 in simssound.HIT) and tv_turn_off (kKillInstance=23) stops it!
            events.Clear();
            Assert.True(audio.PlayForSimulationObject("tv_action_cheap", ownerID: 99, pan: true, zoom: true));
            var tvPlay = events.Last(e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play");
            Assert.Equal(0.5f, (float)tvPlay.GetType().GetProperty("pitchScale")?.GetValue(tvPlay)!, 3);
            var tvVoice = (long)tvPlay.GetType().GetProperty("voice")?.GetValue(tvPlay)!;

            events.Clear();
            Assert.True(audio.Play("tv_turn_off", ownerID: 99));
            Assert.Contains(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "stop" && (long)e.GetType().GetProperty("voice")?.GetValue(e)! == tvVoice);

            // Late End() for manually stopped voice must NOT re-trigger!
            events.Clear();
            audio.End(tvVoice);
            Assert.Empty(events);

            // 5. 4-channel volume persistence to Settings/audio.json across restart.
            audio.SetVolume("FX", 0.3f);
            audio.SetVolume("MUSIC", 0.7f);
            audio.SetVolume("VOX", 0.2f);
            audio.SetVolume("AMBIENCE", 0.9f);

            var audioJsonPath = Path.Combine(profileDir, "Settings", "audio.json");
            Assert.True(File.Exists(audioJsonPath));
            using (var doc = JsonDocument.Parse(File.ReadAllText(audioJsonPath)))
            {
                Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
                var vols = doc.RootElement.GetProperty("volumes");
                Assert.Equal(0.3f, vols[0].GetSingle(), 3);
                Assert.Equal(0.7f, vols[1].GetSingle(), 3);
                Assert.Equal(0.2f, vols[2].GetSingle(), 3);
                Assert.Equal(0.9f, vols[3].GetSingle(), 3);
            }

            using var reloaded = new HitAudioRuntime(Path.Combine(tempDir, "session2"), profileDir, _ => { }, gameDir);
            dynamic state = reloaded.State();
            var reloadedVols = (Dictionary<string, float>)state.volumes;
            Assert.Equal(0.3f, reloadedVols["FX"], 3);
            Assert.Equal(0.7f, reloadedVols["MUSIC"], 3);
            Assert.Equal(0.2f, reloadedVols["VOX"], 3);
            Assert.Equal(0.9f, reloadedVols["AMBIENCE"], 3);

            // Guard: corrupt audio.json falls back cleanly; gameDir never gets Settings/audio.json written.
            File.WriteAllText(audioJsonPath, "{not valid json");
            using var fallback = new HitAudioRuntime(Path.Combine(tempDir, "session3"), profileDir, _ => { }, gameDir);
            dynamic fbState = fallback.State();
            var fbVols = (Dictionary<string, float>)fbState.volumes;
            Assert.Equal(0.8f, fbVols["FX"], 3);
            Assert.Equal(0.8f, fbVols["MUSIC"], 3);
            Assert.Equal(0.8f, fbVols["VOX"], 3);
            Assert.Equal(1.0f, fbVols["AMBIENCE"], 3);

            using var gameProfileGuard = new HitAudioRuntime(Path.Combine(tempDir, "session4"), gameDir, _ => { }, gameDir);
            gameProfileGuard.SetVolume("FX", 0.4f);
            Assert.False(File.Exists(Path.Combine(gameDir, "Settings", "audio.json")));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void SoundBank_TvSuperstarPrefixMatching_ResolvesNonLoopingPlaylists()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-tv-ss-{Guid.NewGuid():N}");
        try
        {
            using var bank = new SoundBank(gameDir, tempDir);
            foreach (var cue in new[] { "tv_superstar_animal", "tv_superstar_animal_norm", "tv_superstar_entertainment", "tv_superstar_news", "tv_superstar_sports" })
            {
                var res = bank.Resolve(cue);
                Assert.NotNull(res);
                Assert.Equal("FX", res.Group);
                Assert.False(res.IsLoop);
                Assert.True(res.IsPlaylist);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void HitVm_ModuleScoping_IsolatesCollidingPatchesAndGlobalHitLists_AndRejectsNullEntryOffset()
    {
        // Build two modules both defining GlobalHitList 500, Patch 6400, and Track 6403.
        byte[] BuildChooseAndPlayBytecode(int loops)
        {
            var buf = new List<byte>();
            if (loops > 0)
            {
                // Execute >100 instructions without wait to verify MaxInstructionsPerTick continuation:
                // v1 = 0; v2 = loops; loop: inc v1; cmp v1, v2; iflt 1
                buf.AddRange([0x04, HitReg.V1, 0]);
                buf.AddRange([0x04, HitReg.V2, (byte)loops]);
                int loopOffset = buf.Count;
                buf.AddRange([0x2f, HitReg.V1]);           // inc v1
                buf.AddRange([0x14, HitReg.V1, HitReg.V2]); // cmp v1, v2
                buf.Add(0x41); EmitInt32(buf, 2);           // iflt tkd 2 (loopOffset)
                // We store tkd 2 -> loopOffset below.
                _ = loopOffset;
            }
            buf.AddRange([0x05, HitReg.V3]); EmitInt32(buf, 500);
            buf.AddRange([0x44, HitReg.V3]);           // smart_setlist 500
            buf.AddRange([0x27, HitReg.Patch]);        // smart_choose patch
            buf.AddRange([0x02, 0]);                   // note_on 0
            buf.Add(0x0b);                             // wait_samp
            buf.Add(0x0c);                             // end
            return buf.ToArray();
        }

        var hitA = BuildChooseAndPlayBytecode(loops: 60); // 60 * 3 = 180 > MaxInstructionsPerTick (100)
        var modA = new HitBankModule("modA", hitA);
        modA.TrackDataOffsets[1] = 0;
        modA.TrackDataOffsets[2] = 6; // inc v1
        modA.Patches[6400] = new HotPatchDef(6400, "PatchA", "a_6400.wav", false);
        modA.GlobalHitLists[500] = [6400];
        var trkA = new HotTrackDef(6403, "TrackA", "FX", 1024, 10, 20, 6400, "@1", 0, false, true, modA, modA);
        modA.Tracks[6403] = trkA;

        var hitB = BuildChooseAndPlayBytecode(loops: 0);
        var modB = new HitBankModule("modB", hitB);
        modB.TrackDataOffsets[1] = 0;
        modB.Patches[6400] = new HotPatchDef(6400, "PatchB", "b_6400.wav", false);
        modB.GlobalHitLists[500] = [6400];
        var trkB = new HotTrackDef(6403, "TrackB", "FX", 1024, 10, 20, 6400, "@1", 0, false, true, modB, modB);
        modB.Tracks[6403] = trkB;

        var nullOffsetTrack = new HotTrackDef(9999, "NullTrack", "FX", 1024, 10, 20, 6400, "@missing", null, false, true, modA, modA);

        var vm = new HitVm([modA, modB]);

        // Null EntryOffset must be rejected instead of executing offset 0 ("HIT!").
        Assert.Null(vm.StartTrack(nullOffsetTrack, arg1: 1));
        Assert.Empty(vm.DrainCommands());

        // modA executes >100 instructions without wait, suspends at WaitUntilTick = currentTick + 1, and completes on StepTicks(1).
        var tA = vm.StartTrack(trkA, arg1: 10)!;
        Assert.NotNull(tA);
        Assert.Empty(vm.DrainCommands()); // paused due to MaxInstructionsPerTick budget
        Assert.True(tA.WaitUntilTick > vm.CurrentTick);
        vm.StepTicks(1);
        var startA = Assert.Single(vm.DrainCommands().OfType<HitStartVoiceCommand>());
        Assert.Equal(6400, startA.PatchId);
        Assert.Same(modA, startA.Bank);

        // modB uses its own moduleSmartLists and Patch 6400 scope.
        var tB = vm.StartTrack(trkB, arg1: 20)!;
        Assert.NotNull(tB);
        var startB = Assert.Single(vm.DrainCommands().OfType<HitStartVoiceCommand>());
        Assert.Equal(6400, startB.PatchId);
        Assert.Same(modB, startB.Bank);
    }

    [Fact]
    public void HitSmartList_Remove_DrainsUsedDownToCrush()
    {
        var list = new HitSmartList([1, 2, 3, 4]);
        Assert.Equal(2, list.Crush);
        var rng = new Random(123);

        int? c1 = list.Choose(rng);
        int? c2 = list.Choose(rng);
        Assert.NotNull(c1);
        Assert.NotNull(c2);
        Assert.Equal(2, list.Available.Count);
        Assert.Equal(2, list.Used.Count);

        foreach (int remaining in list.Available.ToArray())
        {
            list.Remove(remaining);
        }

        Assert.Equal(2, list.TotalCount);
        Assert.Equal(1, list.Crush);
        Assert.True(list.Used.Count <= list.Crush, $"Expected Used.Count ({list.Used.Count}) <= Crush ({list.Crush}).");
        Assert.Single(list.Used);
        Assert.Single(list.Available);
    }

    [Fact]
    public void SoundBank_RealAssets_IsolatesPlantCarnivoreVsSc4Drinks_VoxHitLists_AndCrossModuleGenericTracks()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-m1-r2-assets-{Guid.NewGuid():N}");
        var profileDir = Path.Combine(tempDir, "profile");
        Directory.CreateDirectory(profileDir);
        try
        {
            var events = new List<object>();
            using var bank = new SoundBank(gameDir, Path.Combine(tempDir, "session-bank"));
            using var audio = new HitAudioRuntime(Path.Combine(tempDir, "session-rt"), profileDir, msg =>
            {
                if (msg is not null) events.Add(msg);
            }, gameDir);

            // 1. Patch 6400 isolation: PlantCarnivore vs SC4Drinks
            var cplantTrack = bank.GetTrackForEvent("cplant");
            var sc4Track = bank.GetTrackForEvent("sc_wait_anx_vox");
            if (cplantTrack != null && sc4Track != null)
            {
                var cplantPatch = bank.ResolvePatchById(6400, "FX", false, true, trackId: cplantTrack.TrackId, bank: cplantTrack.Bank);
                Assert.NotNull(cplantPatch);
                Assert.Contains("cplant1", cplantPatch.RelativePath, StringComparison.OrdinalIgnoreCase);

                var sc4Patch = bank.ResolvePatchById(6400, "VOX", false, true, trackId: sc4Track.TrackId, bank: sc4Track.Bank);
                Assert.NotNull(sc4Patch);
                Assert.Contains("vox_confused_huhk17", sc4Patch.RelativePath, StringComparison.OrdinalIgnoreCase);
            }

            // 2. VOX tracks no longer collide with radio/TV autoHitLists
            (string Cue, char Demo)[] voxCases =
            [
                ("vox_complimentee_reject", 'k'),
                ("Vox_Dancee_Decline", 'm'),
                ("Vox_Dancee_Decline", 'f'),
                ("vox_entertainee_boo", 'm'),
                ("vox_entertainee_boo", 'f'),
                ("vox_entertainee_laugh", 'f'),
                ("vox_juggler_tada", 'm')
            ];
            short owner = 100;
            foreach (var (cue, demo) in voxCases)
            {
                events.Clear();
                owner++;
                Assert.True(audio.PlayForSimulationObject(cue, owner, pan: true, zoom: true, voiceTypeOverride: demo), $"Expected '{cue}' ({demo}) to play");
                var play = Assert.Single(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play");
                var file = play.GetType().GetProperty("file")?.GetValue(play)?.ToString();
                Assert.False(string.IsNullOrEmpty(file));
                audio.RemoveOwner(owner);
            }

            // Radio/TV playlists still work with real smart_setlist IDs
            events.Clear();
            Assert.True(audio.PlayForSimulationObject("stereo_rock", ownerID: 201, pan: true, zoom: true));
            Assert.Contains(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play");
            audio.RemoveOwner(201);

            events.Clear();
            Assert.True(audio.PlayForSimulationObject("tv_action_cheap", ownerID: 202, pan: true, zoom: true));
            Assert.Contains(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play");
            audio.RemoveOwner(202);

            // 3. Cross-module @tkd_Generic / @tkd_GenericInterrupt tracks resolve EntryOffset > 0 + CodeBank
            foreach (var evName in new[] { "santa_appear", "bull_start", "shd_Vn01" })
            {
                var trk = bank.GetTrackForEvent(evName);
                if (trk is null) continue;
                Assert.True(trk.EntryOffset is > 0, $"Expected {evName} EntryOffset > 0");
                Assert.NotNull(trk.CodeBank);

                events.Clear();
                Assert.True(audio.PlayForSimulationObject(evName, ownerID: 301, pan: true, zoom: true));
                Assert.Contains(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play");
                audio.RemoveOwner(301);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void HitAudioRuntime_DirectTurnOnTv_ModeDuckingOnStart_MultiSyllableVox_AndSpeed3Vox()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-m1-r2-rt-{Guid.NewGuid():N}");
        var profileDir = Path.Combine(tempDir, "profile");
        Directory.CreateDirectory(profileDir);
        try
        {
            var events = new List<object>();
            using var audio = new HitAudioRuntime(Path.Combine(tempDir, "session"), profileDir, msg =>
            {
                if (msg is not null) events.Add(msg);
            }, gameDir);

            // 1. Direct kTurnOnTv (30) switching without stereo_turnoff / tv_turn_off stops previous voice
            Assert.True(audio.PlayForSimulationObject("stereo_classical", ownerID: 42, pan: true, zoom: true));
            var stereo1 = (long)events.Last(e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play")
                .GetType().GetProperty("voice")?.GetValue(events.Last(e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play"))!;

            events.Clear();
            Assert.True(audio.PlayForSimulationObject("stereo_rock", ownerID: 42, pan: true, zoom: true));
            Assert.Contains(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "stop" && (long)e.GetType().GetProperty("voice")?.GetValue(e)! == stereo1);
            Assert.Contains(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play");
            audio.RemoveOwner(42);

            events.Clear();
            Assert.True(audio.PlayForSimulationObject("tv_action_cheap", ownerID: 43, pan: true, zoom: true));
            var tv1 = (long)events.Last(e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play")
                .GetType().GetProperty("voice")?.GetValue(events.Last(e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play"))!;

            events.Clear();
            Assert.True(audio.PlayForSimulationObject("tv_romance_cheap", ownerID: 43, pan: true, zoom: true));
            Assert.Contains(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "stop" && (long)e.GetType().GetProperty("voice")?.GetValue(e)! == tv1);
            Assert.Contains(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play");
            audio.RemoveOwner(43);

            // 2. Mode music started while MainDuckPriority > 10 immediately gets 0.5x gain
            events.Clear();
            Assert.True(audio.Play("sting_kiss_polite", -1));
            events.Clear();
            Assert.True(audio.Play("bkground_buy1"));
            var buyPlay = Assert.Single(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play");
            float buyVol = (float)buyPlay.GetType().GetProperty("volume")?.GetValue(buyPlay)!;
            Assert.Equal(0.4f, buyVol, 3); // default MUSIC 0.8 * 0.5 duck
            audio.Reset();

            // 3. Multi-syllable hottub_getin_vox ('f') retains female demographic across End() / Tick()
            events.Clear();
            Assert.True(audio.PlayForSimulationObject("hottub_getin_vox", ownerID: 55, pan: true, zoom: true, voiceTypeOverride: 'f'));
            var firstPlay = Assert.Single(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play");
            long vId = (long)firstPlay.GetType().GetProperty("voice")?.GetValue(firstPlay)!;
            string firstFile = firstPlay.GetType().GetProperty("file")?.GetValue(firstPlay)?.ToString()!;
            Assert.Contains("f", Path.GetFileNameWithoutExtension(firstFile), StringComparison.OrdinalIgnoreCase);

            events.Clear();
            audio.End(vId);
            for (int i = 0; i < 60 && !events.Any(e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play"); i++)
            {
                Thread.Sleep(25);
                audio.Tick();
            }
            var nextSyllable = events.LastOrDefault(e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play");
            Assert.NotNull(nextSyllable);
            string nextFile = nextSyllable.GetType().GetProperty("file")?.GetValue(nextSyllable)?.ToString()!;
            Assert.Contains("f", Path.GetFileNameWithoutExtension(nextFile), StringComparison.OrdinalIgnoreCase);
            audio.RemoveOwner(55);

            // 4. SimulationSpeed > 1 with SuppressActiveDuplicates = true allows distinct Sims to speak concurrently
            audio.SuppressActiveDuplicates = true;
            audio.SetSimulationSpeed(3);
            events.Clear();
            Assert.True(audio.PlayForSimulationObject("vox_laugh_giggle", ownerID: 71, pan: true, zoom: true, voiceTypeOverride: 'm'));
            Assert.True(audio.PlayForSimulationObject("vox_laugh_giggle", ownerID: 72, pan: true, zoom: true, voiceTypeOverride: 'f'));
            Assert.Equal(2, events.Count(e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play"));
            Assert.Equal(2, audio.ActiveVoiceCount);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void AudioMixer_Set_PurgesStaleEndedOnReuse_And_EvictIfFull_SkipsActiveOverwrite()
    {
        var mixer = new AudioMixer();
        // Queue id=10 in _ended via MixAndPut on a short non-looping voice, then reuse id=10 via Set(10, newVoice) before TakeEnded()
        mixer.Set(10, new AudioMixer.Voice(new float[8]) { Loop = false });
        mixer.MixAndPut(nint.Zero, 16);
        Assert.Equal(0, mixer.Count);

        // Reuse id=10 without calling TakeEnded() first -> Set must purge stale id=10 from _ended
        mixer.Set(10, new AudioMixer.Voice(new float[200]) { Loop = false });
        Assert.Empty(mixer.TakeEnded());
        Assert.Equal(1, mixer.Count);

        // Fill mixer to 256 voices and verify EvictIfFull skips eviction when incomingVoice is already active
        mixer.Clear();
        for (long i = 1; i <= 256; i++)
        {
            mixer.Set(i, new AudioMixer.Voice(new float[8]) { Loop = false });
        }
        Assert.Equal(256, mixer.Count);
        Assert.Null(mixer.EvictIfFull(256, protectedVoice: 1, incomingVoice: 42));
        Assert.Equal(256, mixer.Count);

        // For a genuinely new voice id=257, EvictIfFull evicts the oldest non-looping voice (id=1 is protected -> id=2)
        Assert.Equal(2L, mixer.EvictIfFull(256, protectedVoice: 1, incomingVoice: 257));
        Assert.Equal(255, mixer.Count);
    }

    [Fact]
    public void UtkAudio_TruncatedUtm0_ThrowsWithoutFabricatingZeros()
    {
        byte[] headerOnly = new byte[32];
        headerOnly[0] = (byte)'U';
        headerOnly[1] = (byte)'T';
        headerOnly[2] = (byte)'M';
        headerOnly[3] = (byte)'0';
        BinaryPrimitives.WriteUInt32LittleEndian(headerOnly.AsSpan(4, 4), 88200);
        BinaryPrimitives.WriteUInt16LittleEndian(headerOnly.AsSpan(14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(headerOnly.AsSpan(16, 4), 22050);

        Assert.Throws<ArgumentException>(() => UtkAudio.Decode(headerOnly));

        byte[] truncatedPayload = new byte[34];
        headerOnly.CopyTo(truncatedPayload, 0);
        truncatedPayload[32] = 0x55;
        truncatedPayload[33] = 0xAA;

        Assert.Throws<EndOfStreamException>(() => UtkAudio.Decode(truncatedPayload));
    }

    private static byte[] BuildSyntheticFarArchive(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        ms.Write("FAR!byAZ"u8);
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tmp, 1);
        ms.Write(tmp);
        ms.Write(stackalloc byte[4]); // placeholder manifest offset

        var offsets = new int[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            offsets[i] = (int)ms.Position;
            ms.Write(entries[i].Data);
        }

        uint manifestOffset = (uint)ms.Position;
        BinaryPrimitives.WriteUInt32LittleEndian(tmp, (uint)entries.Length);
        ms.Write(tmp);
        for (int i = 0; i < entries.Length; i++)
        {
            var nameBytes = Encoding.Latin1.GetBytes(entries[i].Name);
            BinaryPrimitives.WriteUInt32LittleEndian(tmp, (uint)entries[i].Data.Length);
            ms.Write(tmp);
            BinaryPrimitives.WriteUInt32LittleEndian(tmp, (uint)entries[i].Data.Length);
            ms.Write(tmp);
            BinaryPrimitives.WriteUInt32LittleEndian(tmp, (uint)offsets[i]);
            ms.Write(tmp);
            BinaryPrimitives.WriteUInt32LittleEndian(tmp, (uint)nameBytes.Length);
            ms.Write(tmp);
            ms.Write(nameBytes);
        }

        ms.Position = 12;
        BinaryPrimitives.WriteUInt32LittleEndian(tmp, manifestOffset);
        ms.Write(tmp);
        return ms.ToArray();
    }

    [Fact]
    public void HitVm_OutOfRangeRegisters_KillThreadWithoutExecutingTrailingNoteOn_AndAllowLiteralZeroOperands()
    {
        var invalidRegs = new byte[] { 0x00, 0x80 };
        var cases = new List<(string Label, byte[] Body)>();

        foreach (byte bad in invalidRegs)
        {
            cases.Add(($"loadl_0x{bad:X2}", BuildBytes(b =>
            {
                b.AddRange([0x05, bad]);
                EmitInt32(b, 1);
            }).ToArray()));
            cases.Add(($"set_dest_0x{bad:X2}", [0x06, bad, HitReg.V1]));
            cases.Add(($"set_src_0x{bad:X2}", [0x06, HitReg.V1, bad]));
            cases.Add(($"wait_0x{bad:X2}", [0x09, bad]));
            cases.Add(($"callback_r1_0x{bad:X2}", [0x22, bad, HitReg.V1]));
            cases.Add(($"callback_r2_0x{bad:X2}", [0x22, HitReg.V1, bad]));
            cases.Add(($"printreg_0x{bad:X2}", [0x31, bad]));
            cases.Add(($"get_var_dest_0x{bad:X2}", [0x1f, bad, HitReg.V1]));
            cases.Add(($"get_var_src_0x{bad:X2}", [0x1f, HitReg.V1, bad]));
            cases.Add(($"get_var_indirect_0x{bad:X2}", BuildBytes(b =>
            {
                b.AddRange([0x05, HitReg.V2]);
                EmitInt32(b, bad);
                b.AddRange([0x1f, HitReg.V1, HitReg.V2]);
            }).ToArray()));
        }

        foreach (var (label, body) in cases)
        {
            var hit = new List<byte>(body);
            hit.AddRange([0x02, 2, 0x0c]); // note_on 2; end (must NOT execute)

            var mod = new HitBankModule($"bad_{label}", hit.ToArray());
            mod.TrackDataOffsets[100] = 0;
            mod.Patches[2] = new HotPatchDef(2, "Pch2", "two.wav", false);
            var trk = new HotTrackDef(10, "TrkBad", "FX", 1024, 10, 20, 0, "@100", 0, false, true, mod);
            mod.Tracks[10] = trk;

            var vm = new HitVm([mod], rng: new Random(1));
            var thread = vm.StartTrack(trk, arg1: 1);
            Assert.NotNull(thread);
            Assert.True(thread.Killed, $"{label} should kill the thread.");
            Assert.Equal(0, vm.ActiveThreadCount);
            Assert.Null(vm.GetThread(thread.InstanceId));
            Assert.Empty(vm.DrainCommands());
        }

        // Literal 0 operands in note_on 0 (0x02 0x00) and loadb v1, 0 (0x04 HitReg.V1 0x00) remain valid
        var validHit = new List<byte>();
        validHit.AddRange([0x04, HitReg.V1, 0x00]); // loadb v1, 0 (valid!)
        validHit.Add(0x05); validHit.Add(HitReg.Patch); EmitInt32(validHit, 50);
        validHit.AddRange([0x02, 0x00]);           // note_on 0 (literal 0 -> uses HitReg.Patch = 50)
        validHit.Add(0x0c);                        // end

        var validMod = new HitBankModule("valid_lit_zero", validHit.ToArray());
        validMod.TrackDataOffsets[200] = 0;
        validMod.Patches[50] = new HotPatchDef(50, "Pch50", "fifty.wav", false);
        var validTrk = new HotTrackDef(20, "TrkValid", "FX", 1024, 10, 20, 0, "@200", 0, false, true, validMod);
        validMod.Tracks[20] = validTrk;

        var validVm = new HitVm([validMod], rng: new Random(1));
        var validThread = validVm.StartTrack(validTrk, arg1: 1);
        Assert.NotNull(validThread);
        Assert.Equal(0, validThread.Registers[HitReg.V1]);
        var start = Assert.Single(validVm.DrainCommands().OfType<HitStartVoiceCommand>());
        Assert.Equal(50, start.PatchId);
    }

    [Fact]
    public void HitVm_StepThreadFade_OutOfRangeFadeVar_KillsThreadCleanly()
    {
        foreach (byte badReg in new byte[] { 0x00, 0x80 })
        {
            var hit = BuildBytes(b =>
            {
                b.AddRange([0x05, HitReg.V1]); EmitInt32(b, badReg);
                b.AddRange([0x06, HitReg.FadeVar, HitReg.V1]);
                b.AddRange([0x04, HitReg.FadeSpeed, 10]);
                b.AddRange([0x04, HitReg.FadeDest, 50]);
                b.AddRange([0x04, HitReg.FadeOn, 1]);
                b.AddRange([0x04, HitReg.V2, 5]);
                b.AddRange([0x09, HitReg.V2]); // wait 5 ticks
                b.Add(0x0c);
            });

            var mod = new HitBankModule("fade_bad", hit.ToArray());
            mod.TrackDataOffsets[1] = 0;
            var trk = new HotTrackDef(1, "TrkFadeBad", "FX", 1024, 10, 20, 0, "@1", 0, false, true, mod);
            mod.Tracks[1] = trk;

            var vm = new HitVm([mod], rng: new Random(1));
            var thread = vm.StartTrack(trk);
            Assert.NotNull(thread);
            Assert.False(thread.Killed);

            vm.StepTicks(1);
            Assert.True(thread.Killed);
            Assert.Equal(0, vm.ActiveThreadCount);
        }
    }

    [Fact]
    public void AmbienceSequencer_FinalNonRandNote_PlaysAndWrapsOnNextBeat()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-m1-r3-fsc-{Guid.NewGuid():N}");
        var soundData = Path.Combine(tempDir, "SoundData");
        Directory.CreateDirectory(soundData);
        try
        {
            var wavBytes = HitAudioRuntime.CreateWav(22050, 1, 16, new byte[2205 * 2]);
            var fscText =
                "FSC V1.0\n" +
                "HEAD\t1024\t1\t0\t8\t1\t2\t120\t4\t0\t0\t0\t0\t0\t0\n" +
                "cells\n" +
                "0\t1024\t0\t512\t0\t0\t0\t0\t0\t0\t1\t1\t0\t20\t0\t0\t0\t0\t0\tamb_bed.wav\n" +
                "1\t1024\t0\t512\t0\t0\t0\t0\t0\t0\t1\t0\t0\t20\t0\t0\t0\t0\t0\tamb_final.wav\n";
            var fscBytes = Encoding.UTF8.GetBytes(fscText);

            File.WriteAllBytes(
                Path.Combine(soundData, "SoundData.far"),
                BuildSyntheticFarArchive(
                    ("Outdoors/sim_amb.fsc", fscBytes),
                    ("amb_bed.wav", wavBytes),
                    ("amb_final.wav", wavBytes)));

            var playedFiles = new List<string>();
            using var audio = new HitAudioRuntime(Path.Combine(tempDir, "session"), Path.Combine(tempDir, "profile"), msg =>
            {
                var cmd = msg.GetType().GetProperty("command")?.GetValue(msg)?.ToString();
                if (cmd == "play")
                {
                    playedFiles.Add(msg.GetType().GetProperty("file")?.GetValue(msg)?.ToString() ?? "");
                }
            }, tempDir);

            Assert.True(audio.Play("setambience_backyard", -1));
            Assert.Single(playedFiles);
            Assert.Contains("amb_bed", playedFiles[0], StringComparison.OrdinalIgnoreCase);

            var seqField = typeof(HitAudioRuntime).GetField("ambience", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var sequencer = (HitAudioRuntime.AmbienceSequencer)seqField.GetValue(audio)!;

            playedFiles.Clear();
            sequencer.NextBeat(); // index 0 (amb_bed, non-Rand)
            sequencer.NextBeat(); // index 1 (amb_final, final non-Rand note -> must play!)
            sequencer.NextBeat(); // wraps to index 0 (amb_bed)

            Assert.Equal(3, playedFiles.Count);
            Assert.Contains("amb_bed", playedFiles[0], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("amb_final", playedFiles[1], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("amb_bed", playedFiles[2], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void HitAudioRuntime_KillAll_ClearsOrphanedThreadStateAndDuckPriority()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-m1-r3-killall-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var events = new List<object>();
            using var audio = new HitAudioRuntime(Path.Combine(tempDir, "session"), Path.Combine(tempDir, "profile"), msg =>
            {
                if (msg is not null) events.Add(msg);
            }, gameDir);

            // Start hottub_getin_vox and end its 1st syllable so the thread sits in `wait` with no active voice
            Assert.True(audio.PlayForSimulationObject("hottub_getin_vox", ownerID: 55, pan: true, zoom: true, voiceTypeOverride: 'f'));
            var firstPlay = Assert.Single(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play");
            long vId = (long)firstPlay.GetType().GetProperty("voice")?.GetValue(firstPlay)!;
            audio.End(vId);

            // Raise MainDuckPriority via sting_kiss_polite (>10)
            Assert.True(audio.Play("sting_kiss_polite", -1));

            // KillAll must clear all voices, HIT threads, ducking state, and non-voiced thread dictionaries
            Assert.True(audio.Play("kill_all"));
            Assert.Equal(0, audio.ActiveVoiceCount);

            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var threadToVoice = (System.Collections.IDictionary)typeof(HitAudioRuntime).GetField("threadToVoice", flags)!.GetValue(audio)!;
            var voiceToThread = (System.Collections.IDictionary)typeof(HitAudioRuntime).GetField("voiceToThread", flags)!.GetValue(audio)!;
            var pendingCtx = (System.Collections.IDictionary)typeof(HitAudioRuntime).GetField("pendingThreadContext", flags)!.GetValue(audio)!;
            var demoOverrides = (System.Collections.IDictionary)typeof(HitAudioRuntime).GetField("threadDemographicOverrides", flags)!.GetValue(audio)!;
            int lastDuck = (int)typeof(HitAudioRuntime).GetField("lastVmDuckPriority", flags)!.GetValue(audio)!;

            Assert.Empty(threadToVoice);
            Assert.Empty(voiceToThread);
            Assert.Empty(pendingCtx);
            Assert.Empty(demoOverrides);
            Assert.Equal(0, lastDuck);

            // Verify mode music after KillAll starts unducked (0.8f instead of 0.4f)
            events.Clear();
            Assert.True(audio.Play("bkground_buy1"));
            var buyPlay = Assert.Single(events, e => e.GetType().GetProperty("command")?.GetValue(e)?.ToString() == "play");
            float buyVol = (float)buyPlay.GetType().GetProperty("volume")?.GetValue(buyPlay)!;
            Assert.Equal(0.8f, buyVol, 3);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
