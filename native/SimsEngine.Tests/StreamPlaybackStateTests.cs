using SimsSimulation;
using Xunit;

namespace SimsEngine.Tests;

public class StreamPlaybackStateTests
{
    [Theory]
    [InlineData("live", 5, "bkground_live")]
    [InlineData("buy", 5, "bkground_buy1")]
    [InlineData("build", 5, "bkground_build")]
    [InlineData("other", 5, "bkground_live")]
    [InlineData("buy", 25, "station_dtbuy")]
    [InlineData("build", 25, "")]
    [InlineData("live", 25, "bkground_live")]
    [InlineData("buy", 45, "station_vacation")]
    [InlineData("build", 45, "")]
    [InlineData("buy", 85, "station_superstar")]
    [InlineData("build", 85, "")]
    [InlineData("buy", 91, "music_magictown_buy")]
    [InlineData("build", 91, "music_magictown_build")]
    [InlineData("buy", 95, "music_magictown")]
    [InlineData("build", 95, "")]
    public void ModeMusicTransitions_SelectsCorrectTracksForNeighborhoodAndMode(string mode, short house, string expectedTrack)
    {
        var result = SimulationServer.ModeMusic(mode, house);
        Assert.Equal(expectedTrack, result);
    }

    [Theory]
    [InlineData(28, "setambience_beach")]
    [InlineData(29, "setambience_beach")]
    [InlineData(47, "setambience_beach")]
    [InlineData(21, "setambience_park")]
    [InlineData(25, "setambience_park")]
    [InlineData(44, "setambience_park")]
    [InlineData(90, "setambience_magictown")]
    [InlineData(92, "setambience_magictown")]
    [InlineData(1, "setambience_backyard")]
    [InlineData(10, "setambience_backyard")]
    public void LotAmbienceTransitions_SelectsCorrectAmbienceEventForHouse(short house, string expectedAmbience)
    {
        var ambienceEvent = HitAudioRuntime.LotAmbienceEvent(house);
        Assert.Equal(expectedAmbience, ambienceEvent);
    }

    [Fact]
    public void StreamPlayback_ClassifiesMusicAndAmbienceAsLoopingStreams()
    {
        var messages = new List<object>();
        var audio = new HitAudioRuntime(Path.Combine(Path.GetTempPath(), "sims-audio-stream-" + Guid.NewGuid().ToString("N")), "test", msg =>
        {
            if (msg is not null) messages.Add(msg);
        });

        var buyTrack = SimulationServer.ModeMusic("buy", 5);
        audio.Play(buyTrack);

        audio.PlayLotAmbience(28);

        Assert.Equal(2, messages.Count);

        var musicMsg = messages[0];
        Assert.Equal("play", musicMsg.GetType().GetProperty("command")?.GetValue(musicMsg)?.ToString());
        Assert.Equal("MUSIC", musicMsg.GetType().GetProperty("group")?.GetValue(musicMsg)?.ToString());
        Assert.Equal(true, musicMsg.GetType().GetProperty("loop")?.GetValue(musicMsg));
        Assert.Equal(30.0f, (float)(musicMsg.GetType().GetProperty("duration")?.GetValue(musicMsg) ?? 0f));
        Assert.Equal(HitAudioRuntime.NeighborhoodOwnerID, musicMsg.GetType().GetProperty("ownerID")?.GetValue(musicMsg));

        var ambMsg = messages[1];
        Assert.Equal("play", ambMsg.GetType().GetProperty("command")?.GetValue(ambMsg)?.ToString());
        Assert.Equal("AMBIENCE", ambMsg.GetType().GetProperty("group")?.GetValue(ambMsg)?.ToString());
        Assert.Equal(true, ambMsg.GetType().GetProperty("loop")?.GetValue(ambMsg));
        Assert.Equal(30.0f, (float)(ambMsg.GetType().GetProperty("duration")?.GetValue(ambMsg) ?? 0f));
        Assert.Equal(HitAudioRuntime.NeighborhoodOwnerID, ambMsg.GetType().GetProperty("ownerID")?.GetValue(ambMsg));

        audio.Dispose();
    }

    [Fact]
    public void StreamPauseResume_BroadcastsToAllActiveStreams()
    {
        var commands = new List<string>();
        var audio = new HitAudioRuntime(Path.Combine(Path.GetTempPath(), "sims-audio-" + Guid.NewGuid().ToString("N")), "test", msg =>
        {
            var cmd = msg.GetType().GetProperty("command")?.GetValue(msg)?.ToString();
            if (cmd is not null) commands.Add(cmd);
        });

        audio.Play("bkground_buy1");
        audio.PlayLotAmbience(5);

        Assert.Equal(2, audio.ActiveVoiceCount);
        Assert.Equal(2, commands.Count(c => c == "play"));

        audio.SetPaused(true);
        Assert.Equal(2, commands.Count(c => c == "pause"));

        audio.SetPaused(false);
        Assert.Equal(2, commands.Count(c => c == "resume"));

        audio.Dispose();
    }

    [Fact]
    public void StreamPlayback_VolumeAdjustment_UpdatesMasterBuses()
    {
        var updateCommands = new List<object>();
        var audio = new HitAudioRuntime(Path.Combine(Path.GetTempPath(), "sims-audio-vol-" + Guid.NewGuid().ToString("N")), "test", msg =>
        {
            var cmd = msg.GetType().GetProperty("command")?.GetValue(msg)?.ToString();
            if (cmd == "update") updateCommands.Add(msg);
        });

        audio.Play("bkground_live");

        audio.SetVolume("MUSIC", 0.5f);

        Assert.Single(updateCommands);
        var update = updateCommands[0];
        Assert.Equal("MUSIC", update.GetType().GetProperty("group")?.GetValue(update)?.ToString());
        Assert.Equal(0.5f, (float)(update.GetType().GetProperty("volume")?.GetValue(update) ?? 0f), 0.001f);

        audio.Dispose();
    }

    [Fact]
    public void StreamPlayback_Reset_ClearsAllActiveStreamsAndEmitsReset()
    {
        var commands = new List<string>();
        var audio = new HitAudioRuntime(Path.Combine(Path.GetTempPath(), "sims-audio-reset-" + Guid.NewGuid().ToString("N")), "test", msg =>
        {
            var cmd = msg.GetType().GetProperty("command")?.GetValue(msg)?.ToString();
            if (cmd is not null) commands.Add(cmd);
        });

        audio.Play("bkground_build");
        audio.PlayLotAmbience(95);

        Assert.Equal(2, audio.ActiveVoiceCount);

        audio.Reset();

        Assert.Equal(0, audio.ActiveVoiceCount);
        Assert.Contains("reset", commands);

        audio.Dispose();
    }
}
