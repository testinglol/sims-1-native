namespace SimsEngine.Tests;

/// <summary>Unit tests for A15 audio channel limits, voice stealing, volume group gain attenuation, and 3D falloff.</summary>
public class AudioChannelAllocationTests
{
    [Fact]
    public void ChannelAllocationStealsOldestVoiceWhenFull()
    {
        var stoppedCount = 0;
        var audio = new HitAudioRuntime(Path.Combine(Path.GetTempPath(), "sims-audio-" + Guid.NewGuid().ToString("N")), "test", msg =>
        {
            var cmd = msg.GetType().GetProperty("command")?.GetValue(msg)?.ToString();
            if (cmd == "stop") stoppedCount++;
        });

        // Allocate up to MaxVoices (64)
        for (short i = 1; i <= HitAudioRuntime.MaxVoices; i++)
        {
            audio.PlayForSimulationObject($"sound_{i}", i, false, false);
        }
        Assert.Equal(HitAudioRuntime.MaxVoices, audio.ActiveVoiceCount);
        Assert.Equal(0, stoppedCount);

        // Allocate 5 more voices: should steal 5 oldest voices and keep active count <= MaxVoices
        for (short i = 100; i < 105; i++)
        {
            audio.PlayForSimulationObject($"extra_sound_{i}", i, false, false);
        }

        Assert.Equal(5, stoppedCount);
        Assert.Equal(HitAudioRuntime.MaxVoices, audio.ActiveVoiceCount);

        audio.Dispose();
    }

    [Fact]
    public void VolumeGroupAttenuationScalesIndependentBuses()
    {
        var audio = new HitAudioRuntime(Path.Combine(Path.GetTempPath(), "sims-audio-" + Guid.NewGuid().ToString("N")), "test", _ => { });

        // Set FX to 0.5 (50%), VOX to 0.2 (20%), MUSIC to 0.7 (70%), AMBIENCE to 1.0 (100%)
        audio.SetVolume("FX", 0.5f);
        audio.SetVolume("VOX", 0.2f);
        audio.SetVolume("MUSIC", 0.7f);
        audio.SetVolume("AMBIENCE", 1.0f);

        dynamic state = audio.State();
        var vols = (Dictionary<string, float>)state.volumes;

        Assert.Equal(0.5f, vols["FX"]);
        Assert.Equal(0.2f, vols["VOX"]);
        Assert.Equal(0.7f, vols["MUSIC"]);
        Assert.Equal(1.0f, vols["AMBIENCE"]);

        // Volume inputs outside [0, 1] or unknown groups throw ArgumentException
        Assert.Throws<ArgumentException>(() => audio.SetVolume("FX", 80f));
        Assert.Throws<ArgumentException>(() => audio.SetVolume("FX", -0.1f));
        Assert.Throws<ArgumentException>(() => audio.SetVolume("MUSIC", 1.1f));
        Assert.Throws<ArgumentException>(() => audio.SetVolume("unknown", 0.5f));

        audio.SetVolume("FX", 0.8f);
        state = audio.State();
        vols = (Dictionary<string, float>)state.volumes;
        Assert.Equal(0.8f, vols["FX"], 0.001f);

        audio.Dispose();
    }

    [Theory]
    [InlineData(2.0f, 1.0f)]   // Within minDist (5) -> full volume 1.0
    [InlineData(5.0f, 1.0f)]   // At minDist (5) -> full volume 1.0
    [InlineData(10.0f, 0.5f)]  // At dist 10: 5 / (5 + 1.0 * (10 - 5)) = 5 / 10 = 0.5
    [InlineData(15.0f, 0.333f)]// At dist 15: 5 / (5 + 1.0 * (15 - 5)) = 5 / 15 = 0.333
    [InlineData(40.0f, 0.0f)]  // At maxDist (40) -> 0.0
    [InlineData(50.0f, 0.0f)]  // Beyond maxDist -> 0.0
    public void InverseDistanceAttenuationModel(float distance, float expectedGain)
    {
        var gain = HitAudioRuntime.AttenuationGain(distance, minDist: 5.0f, rolloff: 1.0f, maxDist: 40.0f);
        Assert.Equal(expectedGain, gain, 0.01f);
    }
}
