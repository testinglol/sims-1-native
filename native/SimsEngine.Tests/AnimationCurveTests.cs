using System.Buffers.Binary;
using SimsEngine.Formats;

namespace SimsEngine.Tests;

/// <summary>CFP decoding as Sims.exe FUN_005974ee does it, on hand-coded streams.</summary>
public class AnimationCurveTests
{
    private static byte[] Float(float value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Stream(params byte[][] parts) => [.. parts.SelectMany(part => part)];

    private static void AssertClose(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++) Assert.True(MathF.Abs(expected[index] - actual[index]) < 1e-5f, $"index {index}: {actual[index]} != {expected[index]}");
    }

    [Fact]
    public void AbsoluteFloatsAndDeltaCodesAccumulate()
    {
        // One frame of three translation channels: an absolute 1.0, then code 0xFC (the table's largest entry, +0.1), then
        // code 0x7E (the middle entry, x = 0, which adds nothing).
        var data = Stream([0xFF], Float(1f), [0xFC], [0x7E]);
        var curves = AnimationCurveFile.Read(data, "absolute.cfp", 1, 0);
        AssertClose([1f, 1.1f, 1.1f], curves.Translations);
        Assert.Empty(curves.Rotations);
    }

    [Fact]
    public void RepeatCodeHoldsTheValueWithoutReadingMoreCodes()
    {
        // Two frames of the three translation channels, read channel by channel. Channel x: 0xFE with count 1 gives frame 0 the
        // value 0 and holds it for frame 1, which reads no code. Channel y: an absolute 3.0, then a -0.1 step. Channel z: 5.0,
        // then 0xFE with count 0.
        var data = Stream([0xFE, 0x01, 0x00], [0xFF], Float(3f), [0x00], [0xFF], Float(5f), [0xFE, 0x00, 0x00]);
        var curves = AnimationCurveFile.Read(data, "repeat.cfp", 2, 0);
        AssertClose([0f, 3f, 5f, 0f, 2.9f, 5f], curves.Translations);
    }

    [Fact]
    public void EachRunStartsFromZeroAndChannelsShareTheValue()
    {
        // Translations, one frame: x is an absolute 2.0, y and z step by -0.1 (1.9, 1.8), so the value carries from x to y
        // to z. Rotations, one frame: the run starts again at zero, so x is 0 + 0.1 and y, z and w hold it with 0xFE count 0.
        var data = Stream([0xFF], Float(2f), [0x00], [0x00], [0xFC], [0xFE, 0x00, 0x00], [0xFE, 0x00, 0x00], [0xFE, 0x00, 0x00]);
        var curves = AnimationCurveFile.Read(data, "restart.cfp", 1, 1);
        AssertClose([2f, 1.9f, 1.8f], curves.Translations);
        AssertClose([0.1f, 0.1f, 0.1f, 0.1f], curves.Rotations);
    }

    [Fact]
    public void LeftoverBytesAreRejected()
    {
        var data = Stream([0xFF], Float(1f), [0x00], [0x00], [0x00]);
        var error = Assert.Throws<ResourceFormatException>(() => AnimationCurveFile.Read(data, "extra.cfp", 1, 0));
        Assert.Equal("extra.cfp", error.File);
        Assert.Equal(7, error.Offset);
    }

    [Fact]
    public void TruncatedAbsoluteFloatIsRejectedWithItsOffset()
    {
        var data = Stream([0xFF], [0x00, 0x00]);
        var error = Assert.Throws<ResourceFormatException>(() => AnimationCurveFile.Read(data, "short.cfp", 1, 0));
        Assert.Equal("short.cfp", error.File);
        Assert.Equal(1, error.Offset);
    }

    [Fact]
    public void FrameCountsAboveTheLimitAreRejected()
    {
        Assert.Throws<ResourceFormatException>(() => AnimationCurveFile.Read(new byte[] { 0 }, "big.cfp", AnimationCurveFile.MaximumFrames + 1, 0));
    }
}
