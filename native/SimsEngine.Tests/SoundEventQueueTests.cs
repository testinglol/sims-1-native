using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public class SoundEventQueueTests
{
    private static byte[] Operand(short eventId, byte flags = 0xFF) =>
        [(byte)eventId, (byte)(eventId >> 8), 0, 0, flags, 0, 0, 0];

    private static BehaviorScope ScopeWithFwav(params (bool isPrivate, ushort id, string name)[] chunks)
    {
        var privateBuilder = new List<(string, ushort, byte[])>();
        var globalBuilder = new List<(string, ushort, byte[])>();
        foreach (var (isPriv, id, name) in chunks)
        {
            var chunkData = new ChunkBuilder().CString(name).ToArray();
            if (isPriv) privateBuilder.Add(("FWAV", id, chunkData));
            else globalBuilder.Add(("FWAV", id, chunkData));
        }
        var privateFile = new IffFile(ChunkBuilder.File(privateBuilder.ToArray()), "object.iff");
        var globalFile = new IffFile(ChunkBuilder.File(globalBuilder.ToArray()), "global.iff");
        return new BehaviorScope(globalFile, privateFile, null);
    }

    [Fact]
    public void NativeAudio_PriorityVoiceStealing_PreservesLoopsAndSacrificesOldestOneshot()
    {
        var stoppedVoices = new List<long>();
        var audio = new HitAudioRuntime(Path.Combine(Path.GetTempPath(), "sims-audio-queue-" + Guid.NewGuid().ToString("N")), "test", msg =>
        {
            var cmd = msg.GetType().GetProperty("command")?.GetValue(msg)?.ToString();
            if (cmd == "stop")
            {
                var v = (long)(msg.GetType().GetProperty("voice")?.GetValue(msg) ?? 0L);
                stoppedVoices.Add(v);
            }
        });

        for (short i = 1; i <= 32; i++)
        {
            var played = audio.PlayForSimulationObject($"ambient_fountain_loop_{i}", i, false, false);
            Assert.True(played);
        }

        for (short i = 33; i <= 64; i++)
        {
            var played = audio.PlayForSimulationObject($"object_impact_hit_{i}", i, false, false);
            Assert.True(played);
        }

        Assert.Equal(HitAudioRuntime.MaxVoices, audio.ActiveVoiceCount);
        Assert.Empty(stoppedVoices);

        // When voice capacity is exhausted, voice stealing prioritizes reclaiming non-looping voices by
        // earliest start time.
        var ok = audio.PlayForSimulationObject("ui_click_button", 65, false, false);
        Assert.True(ok);
        Assert.Equal(HitAudioRuntime.MaxVoices, audio.ActiveVoiceCount);

        Assert.Single(stoppedVoices);
        var stolenVoiceId = stoppedVoices[0];

        Assert.Equal(33L, stolenVoiceId);

        audio.Dispose();
    }

    [Fact]
    public void NativeAudio_SimultaneousSoundEvents_DispatchesAllWithoutDropping()
    {
        var dispatched = new List<string>();
        var audio = new HitAudioRuntime(Path.Combine(Path.GetTempPath(), "sims-audio-" + Guid.NewGuid().ToString("N")), "test", message =>
        {
            if (message is not null)
            {
                var type = message.GetType().GetProperty("command")?.GetValue(message)?.ToString();
                if (type == "play")
                {
                    var file = message.GetType().GetProperty("file")?.GetValue(message)?.ToString();
                    if (file is not null) dispatched.Add(file);
                }
            }
        });

        for (short i = 1; i <= 20; i++)
        {
            var played = audio.PlayForSimulationObject($"sfx_event_{i}", i, true, true);
            Assert.True(played);
        }

        Assert.Equal(20, dispatched.Count);
        Assert.Equal(20, audio.ActiveVoiceCount);
        audio.Dispose();
    }

    [Fact]
    public void NativeAudio_SoundEventDispatch_CategorizesVoxAndFxAndSetsParameters()
    {
        var messages = new List<object>();
        var audio = new HitAudioRuntime(Path.Combine(Path.GetTempPath(), "sims-audio-vox-" + Guid.NewGuid().ToString("N")), "test", msg =>
        {
            if (msg is not null) messages.Add(msg);
        });

        audio.PlayForSimulationObject("sim_greeting_talk_happy", 1, pan: true, zoom: false);
        audio.PlayForSimulationObject("appliance_blender_sfx", 2, pan: false, zoom: true);

        Assert.Equal(2, messages.Count);

        var voxMsg = messages[0];
        Assert.Equal("play", voxMsg.GetType().GetProperty("command")?.GetValue(voxMsg)?.ToString());
        Assert.Equal("VOX", voxMsg.GetType().GetProperty("group")?.GetValue(voxMsg)?.ToString());
        Assert.Equal(1, voxMsg.GetType().GetProperty("ownerID")?.GetValue(voxMsg));
        Assert.Equal(true, voxMsg.GetType().GetProperty("spatial")?.GetValue(voxMsg));
        Assert.Equal(false, voxMsg.GetType().GetProperty("zoom")?.GetValue(voxMsg));

        var fxMsg = messages[1];
        Assert.Equal("play", fxMsg.GetType().GetProperty("command")?.GetValue(fxMsg)?.ToString());
        Assert.Equal("FX", fxMsg.GetType().GetProperty("group")?.GetValue(fxMsg)?.ToString());
        Assert.Equal(2, fxMsg.GetType().GetProperty("ownerID")?.GetValue(fxMsg));
        Assert.Equal(false, fxMsg.GetType().GetProperty("spatial")?.GetValue(fxMsg));
        Assert.Equal(true, fxMsg.GetType().GetProperty("zoom")?.GetValue(fxMsg));

        audio.Dispose();
    }

    [Fact]
    public void SoundBankFallback_PrefersPrivateBankOverGlobalBank()
    {
        // Sound event playback queries the object's private resource bank (bank 0) first.
        var scope = ScopeWithFwav(
            (isPrivate: true, id: 100, name: "private_door_open"),
            (isPrivate: false, id: 100, name: "global_door_open"));

        var world = new SimulationWorld();
        string? playedSound = null;
        world.PlaySoundEvent = (name, owner, pan, zoom) =>
        {
            playedSound = name;
            return true;
        };

        var person = new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType));
        var context = new TreeContext(world, person, new StackFrame(0, [], [0, 0]) { Table = scope });

        var result = PlaySoundEventPrimitive.Execute(context, Operand(100));

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal("private_door_open", playedSound);
    }

    [Fact]
    public void SoundBankFallback_FallsBackToGlobalBankWhenPrivateMissing()
    {
        // When the private resource bank contains no matching sound event, playback falls back to the
        // global sound bank (bank 1).
        var scope = ScopeWithFwav(
            (isPrivate: true, id: 101, name: "other_private_sound"),
            (isPrivate: false, id: 200, name: "global_phone_ring"));

        var world = new SimulationWorld();
        string? playedSound = null;
        world.PlaySoundEvent = (name, owner, pan, zoom) =>
        {
            playedSound = name;
            return true;
        };

        var person = new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType));
        var context = new TreeContext(world, person, new StackFrame(0, [], [0, 0]) { Table = scope });

        var result = PlaySoundEventPrimitive.Execute(context, Operand(200));

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal("global_phone_ring", playedSound);
    }

    [Fact]
    public void SoundBankFallback_FailsSilentlyWhenBothBanksReject()
    {
        // When neither private nor global resource banks contain the sound event, playback succeeds
        // silently without dispatching audio.
        var scope = ScopeWithFwav(
            (isPrivate: true, id: 101, name: "unrelated_private"),
            (isPrivate: false, id: 102, name: "unrelated_global"));

        var world = new SimulationWorld();
        var wasDispatched = false;
        world.PlaySoundEvent = (name, owner, pan, zoom) =>
        {
            wasDispatched = true;
            return true;
        };

        var person = new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType));
        var context = new TreeContext(world, person, new StackFrame(0, [], [0, 0]) { Table = scope });

        var result = PlaySoundEventPrimitive.Execute(context, Operand(999));

        Assert.Equal(PrimitiveResult.True, result);
        Assert.False(wasDispatched);
    }

    [Fact]
    public void NativeAudio_RemoveOwner_TerminatesAllSoundsOwnedByObject()
    {
        var stoppedVoices = new List<long>();
        var audio = new HitAudioRuntime(Path.Combine(Path.GetTempPath(), "sims-audio-owner-" + Guid.NewGuid().ToString("N")), "test", msg =>
        {
            var cmd = msg.GetType().GetProperty("command")?.GetValue(msg)?.ToString();
            if (cmd == "stop")
            {
                var v = (long)(msg.GetType().GetProperty("voice")?.GetValue(msg) ?? 0L);
                stoppedVoices.Add(v);
            }
        });

        audio.PlayForSimulationObject("stereo_music_loop", 10, false, false);
        audio.PlayForSimulationObject("stereo_bass_hit", 10, false, false);
        audio.PlayForSimulationObject("stereo_vocal", 10, false, false);

        audio.PlayForSimulationObject("tv_sfx", 20, false, false);
        audio.PlayForSimulationObject("tv_talk", 20, false, false);

        Assert.Equal(5, audio.ActiveVoiceCount);

        audio.RemoveOwner(10);

        Assert.Equal(2, audio.ActiveVoiceCount);
        Assert.Equal(3, stoppedVoices.Count);

        audio.Dispose();
    }
}
