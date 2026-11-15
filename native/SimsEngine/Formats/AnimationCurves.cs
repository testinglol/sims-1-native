using System.Buffers.Binary;

namespace SimsEngine.Formats;

/// <summary>The decoded curves of one animation: three floats per translation frame (x, y, z) and four per rotation frame
/// (x, y, z, w), each array frame by frame.</summary>
public sealed record AnimationCurves(float[] Translations, float[] Rotations);

/// <summary>A CFP curve file: an animation's translations and rotations in the coded form Sims.exe decodes with FUN_005974ee.
/// The file has no header; its frame counts come from the animation's record (<see cref="Animation.TranslationCount"/> and
/// <see cref="Animation.RotationCount"/>). The stream holds the translation channels first (three channels of
/// translation frames) and then the rotation channels (four, X, Y, Z, W). Each of the two runs is one call of
/// FUN_005974ee, which starts its value and hold count at zero. Within a run the decoder reads each channel over all its
/// frames in turn, and the value and hold count carry over from one channel to the next.
/// Each code byte is one of:
/// <list type="bullet">
/// <item><description>0x00 to 0xFD: add the delta table's entry for the code to the value;</description></item>
/// <item><description>0xFF: the next four bytes are a float that replaces the value;</description></item>
/// <item><description>0xFE: the next two bytes are a u16 count; this frame takes the value and the next count frames hold it.</description></item>
/// </list>
/// Each frame records the value after its code has been read, or after its hold has been taken up. Bytes left over after the
/// last frame are an error.</summary>
public static class AnimationCurveFile
{
    /// <summary>The largest frame count accepted. A real animation is far below it; this only bounds the allocation.</summary>
    public const int MaximumFrames = 10_000_000;

    /// <summary>Sims.exe's delta table (FUN_005975f0): entry k is sign(x) * x^4 * 0.1 for x = 2k/252 - 1, k = 0 to 252.
    /// Entry 253 (code 0xFD) is never written by FUN_005975f0 and is read as 0 (Inferred: no installed curve uses it).</summary>
    private static readonly float[] DeltaTable = BuildDeltaTable();

    /// <summary>Decodes a CFP for an animation with <paramref name="translationFrames"/> translation frames and
    /// <paramref name="rotationFrames"/> rotation frames.</summary>
    public static AnimationCurves Read(ReadOnlyMemory<byte> data, string source, int translationFrames, int rotationFrames)
    {
        if (translationFrames < 0 || translationFrames > MaximumFrames || rotationFrames < 0 || rotationFrames > MaximumFrames)
            throw new ResourceFormatException(source, null, 0, $"frame counts {translationFrames} and {rotationFrames} are outside 0 to {MaximumFrames}");
        var reader = new ResourceReader(data.Span, source);
        var translations = DecodeChannels(ref reader, translationFrames, 3);
        var rotations = DecodeChannels(ref reader, rotationFrames, 4);
        if (reader.Remaining != 0)
            throw reader.Error($"{reader.Remaining} bytes are left after {translationFrames} translation and {rotationFrames} rotation frames");
        return new AnimationCurves(translations, rotations);
    }

    /// <summary>Builds the table with single-precision arithmetic in the order FUN_005975f0 evaluates it.</summary>
    private static float[] BuildDeltaTable()
    {
        const float step = 1f / 252f;
        var table = new float[254];
        for (var index = 0; index < 253; index++)
        {
            var x = (float)index * step + (float)index * step - 1f;
            var sign = x >= 0f ? 1f : -1f;
            table[index] = sign * x * x * x * x * 0.1f;
        }
        return table;
    }

    /// <summary>One call of FUN_005974ee: decodes <paramref name="channels"/> channels of <paramref name="frames"/> frames
    /// each, channel by channel, and returns them interleaved frame by frame (frame * channels + channel).</summary>
    private static float[] DecodeChannels(ref ResourceReader reader, int frames, int channels)
    {
        var state = new CurveState();
        var output = new float[frames * channels];
        for (var channel = 0; channel < channels; channel++)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                if (state.Hold > 0)
                {
                    state.Hold--;
                }
                else
                {
                    var code = reader.U8();
                    if (code == 0xFF) state.Value = BinaryPrimitives.ReadSingleLittleEndian(reader.Bytes(4));
                    else if (code == 0xFE) state.Hold = reader.U16();
                    else state.Value += DeltaTable[code];
                }
                output[frame * channels + channel] = state.Value;
            }
        }
        return output;
    }

    /// <summary>The value and the frames still to hold. FUN_005974ee keeps them from one channel to the next within a call.</summary>
    private struct CurveState
    {
        public float Value;
        public int Hold;
    }
}
