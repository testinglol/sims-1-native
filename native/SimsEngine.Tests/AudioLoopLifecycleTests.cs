using SimsEngine.Simulation;

namespace SimsEngine.Tests;

/// <summary>Unit tests for A15 audio looping lifecycle, sustained playback, owner termination, and object removal cleanup.</summary>
public class AudioLoopLifecycleTests
{
    [Fact]
    public void LoopedSoundStartsAndMaintainsActiveStateAcrossTicks()
    {
        var messages = new List<string>();
        var audio = new HitAudioRuntime(Path.Combine(Path.GetTempPath(), "sims-audio-" + Guid.NewGuid().ToString("N")), "test", msg =>
        {
            var cmd = msg.GetType().GetProperty("command")?.GetValue(msg)?.ToString();
            if (cmd is not null) messages.Add(cmd);
        });

        // Start a looping sound for object 5
        var played = audio.PlayForSimulationObject("water_fountain_loop", 5, true, true);
        Assert.True(played);
        Assert.Contains("play", messages);
        Assert.Equal(1, audio.ActiveVoiceCount);

        // Tick several times: looping voice must not expire
        for (int i = 0; i < 10; i++)
        {
            audio.Tick();
        }
        Assert.Equal(1, audio.ActiveVoiceCount);

        audio.Dispose();
    }

    [Fact]
    public void StopAllSoundsTerminatesAllLoopsForOwner()
    {
        var stoppedVoices = new List<long>();
        var audio = new HitAudioRuntime(Path.Combine(Path.GetTempPath(), "sims-audio-" + Guid.NewGuid().ToString("N")), "test", msg =>
        {
            var cmd = msg.GetType().GetProperty("command")?.GetValue(msg)?.ToString();
            if (cmd == "stop")
            {
                var voice = (long)(msg.GetType().GetProperty("voice")?.GetValue(msg) ?? 0L);
                stoppedVoices.Add(voice);
            }
        });

        // Start 3 sounds for owner 10, and 1 for owner 20
        audio.PlayForSimulationObject("loop_hum_1", 10, true, true);
        audio.PlayForSimulationObject("loop_hum_2", 10, true, true);
        audio.PlayForSimulationObject("loop_hum_3", 10, true, true);
        audio.PlayForSimulationObject("other_sound", 20, true, true);

        Assert.Equal(4, audio.ActiveVoiceCount);

        // Stop all sounds for owner 10
        audio.RemoveOwner(10);

        Assert.Equal(3, stoppedVoices.Count);
        Assert.Equal(1, audio.ActiveVoiceCount);

        audio.Dispose();
    }

    [Fact]
    public void ObjectRemovalStopsAttachedSoundLoops()
    {
        var stopped = false;
        var audio = new HitAudioRuntime(Path.Combine(Path.GetTempPath(), "sims-audio-" + Guid.NewGuid().ToString("N")), "test", msg =>
        {
            var cmd = msg.GetType().GetProperty("command")?.GetValue(msg)?.ToString();
            if (cmd == "stop") stopped = true;
        });

        audio.PlayForSimulationObject("appliance_loop", 15, true, true);
        Assert.Equal(1, audio.ActiveVoiceCount);

        // In a simulation world where object 15 does not exist, Tick(world) must clean it up
        var world = new SimulationWorld();
        Assert.Null(world.ObjectById(15));

        audio.Tick(world);

        Assert.True(stopped);
        Assert.Equal(0, audio.ActiveVoiceCount);

        audio.Dispose();
    }
}
