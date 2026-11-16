namespace SimsEngine.Tests;

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

        for (short i = 1; i <= HitAudioRuntime.MaxVoices; i++)
        {
            audio.PlayForSimulationObject($"sound_{i}", i, false, false);
        }
        Assert.Equal(HitAudioRuntime.MaxVoices, audio.ActiveVoiceCount);
        Assert.Equal(0, stoppedCount);

        // When voice allocation exceeds maximum capacity, the oldest active voices are stolen to enforce
        // the voice limit.
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
    [InlineData(2.0f, 1.0f)]
    [InlineData(5.0f, 1.0f)]
    [InlineData(10.0f, 0.5f)]
    [InlineData(15.0f, 0.333f)]
    [InlineData(40.0f, 0.0f)]
    [InlineData(50.0f, 0.0f)]
    public void InverseDistanceAttenuationModel(float distance, float expectedGain)
    {
        var gain = HitAudioRuntime.AttenuationGain(distance, minDist: 5.0f, rolloff: 1.0f, maxDist: 40.0f);
        Assert.Equal(expectedGain, gain, 0.01f);
    }
}
