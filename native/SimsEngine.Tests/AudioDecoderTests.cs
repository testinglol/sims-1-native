using System.Buffers.Binary;
using SimsEngine.Formats;

namespace SimsEngine.Tests;

public class AudioDecoderTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    [Fact]
    public void XaAudio_RecognizesMagicAndRejectsInvalid()
    {
        var validHeader = new byte[24];
        validHeader[0] = (byte)'X';
        validHeader[1] = (byte)'A';
        Assert.True(XaAudio.IsXa(validHeader));

        var invalidHeader = new byte[24];
        invalidHeader[0] = (byte)'R';
        invalidHeader[1] = (byte)'I';
        Assert.False(XaAudio.IsXa(invalidHeader));

        Assert.Throws<ArgumentException>(() => XaAudio.Decode(invalidHeader));
    }

    [Fact]
    public void XaAudio_DecodesRealGameSoundFromArchive()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir == null) return;

        var soundDataFar = Path.Combine(gameDir, "SoundData", "SoundData.far");
        if (!File.Exists(soundDataFar)) return;

        var far = FarArchive.Open(soundDataFar);
        var entry = far.Find("UI/UI_CLICK.XA");
        Assert.NotNull(entry);

        var data = far.Read(entry).Span;
        Assert.True(XaAudio.IsXa(data));

        var (wav, sampleRate, channels, totalSamples, duration) = XaAudio.Decode(data);
        Assert.Equal(22050, sampleRate);
        Assert.Equal(1, channels);
        Assert.Equal(468, totalSamples);
        Assert.True(duration > 0.02f && duration < 0.03f);

        Assert.True(wav.Length >= 44 + 468 * 2);
        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal((byte)'F', wav[2]);
        Assert.Equal((byte)'F', wav[3]);

        Assert.Equal((byte)'W', wav[8]);
        Assert.Equal((byte)'A', wav[9]);
        Assert.Equal((byte)'V', wav[10]);
        Assert.Equal((byte)'E', wav[11]);

        var pcmData = wav.AsSpan(44);
        Assert.Equal(totalSamples * 2, pcmData.Length);
    }

    [Fact]
    public void UtkAudio_RecognizesMagicAndRejectsInvalid()
    {
        var validHeader = new byte[32];
        validHeader[0] = (byte)'U';
        validHeader[1] = (byte)'T';
        validHeader[2] = (byte)'M';
        validHeader[3] = (byte)'0';
        Assert.True(UtkAudio.IsUtk(validHeader));

        var invalidHeader = new byte[32];
        Assert.False(UtkAudio.IsUtk(invalidHeader));
        Assert.Throws<ArgumentException>(() => UtkAudio.Decode(invalidHeader));
    }

    [Fact]
    public void NativeSoundBank_MockModeGeneratesValidFallback()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sims-soundbank-mock-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var bank = new SoundBank(null, tempDir);
            Assert.True(bank.IsMock);

            var res = bank.Resolve("test_sound_cue");
            Assert.NotNull(res);
            Assert.Equal("FX", res.Group);
            Assert.False(res.IsLoop);
            Assert.True(res.Duration > 0);

            var filePath = Path.Combine(tempDir, Path.GetFileName(res.RelativePath));
            Assert.True(File.Exists(filePath));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void NativeSoundBank_ProductionModeResolvesOriginalEvents()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir == null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), "sims-soundbank-prod-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var bank = new SoundBank(gameDir, tempDir);
            Assert.False(bank.IsMock);

            var click = bank.Resolve("ui_click");
            Assert.NotNull(click);
            Assert.Equal("FX", click.Group);
            Assert.False(click.IsLoop);
            Assert.True(click.Duration > 0.01f && click.Duration < 1.0f);
            Assert.True(File.Exists(Path.Combine(tempDir, Path.GetFileName(click.RelativePath))));

            var bdoze = bank.Resolve("ui_nhood_bdoze_loop");
            Assert.NotNull(bdoze);
            Assert.True(bdoze.IsLoop);

            var vox = bank.Resolve("vox_laugh_giggle");
            Assert.NotNull(vox);
            Assert.Equal("VOX", vox.Group);
            Assert.True(vox.Duration > 0.1f);

            var phone = bank.Resolve("phone_ring");
            Assert.NotNull(phone);
            Assert.True(phone.Duration > 0.1f);

            var music = bank.Resolve("bkground_buy1");
            Assert.NotNull(music);
            Assert.Equal("MUSIC", music.Group);
            Assert.True(music.IsLoop);
            Assert.EndsWith(".mp3", music.RelativePath);

            var dtn = bank.Resolve("station_dtnhood");
            Assert.NotNull(dtn);
            Assert.Equal("MUSIC", dtn.Group);

            var missing = bank.Resolve("native_missing_event");
            Assert.Null(missing);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void NativeAudio_RejectsMissingEventInProduction()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir == null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), "sims-audio-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var audio = new HitAudioRuntime(tempDir, "test", _ => { }, gameDir);
            Assert.True(audio.Play("ui_click"));
            Assert.False(audio.Play("native_missing_event"));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
