using System.Buffers.Binary;

namespace SimsEngine.Formats;

/// <summary>Decoded animation curves containing interleaved translation channels (X, Y, Z) and rotation
/// channels (X, Y, Z, W) frame by frame.</summary>
public sealed record AnimationCurves(float[] Translations, float[] Rotations);

/// <summary>Decodes compressed CFP animation curves. Streams store translation channels followed by
/// rotation channels, encoding each frame using delta table lookups (0x00–0xFD), run-length holds (0xFE),
/// or literal 32-bit floats (0xFF).</summary>
public static class AnimationCurveFile
{
    public const int MaximumFrames = 10_000_000;

    /// <summary>Precomputed curve delta table where entry <c>k</c> equals <c>sign(x) * x^4 * 0.1</c> for
    /// <c>x = 2k / 252 - 1</c> across indices 0 to 252.</summary>
    private static readonly float[] DeltaTable = BuildDeltaTable();

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

    /// <summary>Decodes planar animation curve channels and interleaves their values frame by frame.</summary>
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

    /// <summary>Tracks the running curve value and remaining repeat hold count carried across channels.</summary>
    private struct CurveState
    {
        public float Value;
        public int Hold;
    }
}
