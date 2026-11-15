using System.Buffers.Binary;

namespace SimsEngine.Formats;

/// <summary>Pure C# EA XA ADPCM audio decoder for Maxis / Electronic Arts audio assets (.xa).
/// Supports both mono (15-byte blocks yielding 28 16-bit samples) and stereo (30-byte blocks yielding 28 stereo frames),
/// decoding into standard 44-byte RIFF WAV output.</summary>
public static class XaAudio
{
    private static readonly int[] EaC1 = [0, 240, 460, 392];
    private static readonly int[] EaC2 = [0, 0, -208, -220];

    public static bool IsXa(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24) return false;
        return data[0] == (byte)'X' && data[1] == (byte)'A';
    }

    public static byte[] DecodeToWav(ReadOnlySpan<byte> data)
    {
        var decoded = Decode(data);
        return decoded.WavBytes;
    }

    public static (byte[] WavBytes, int SampleRate, int Channels, int TotalSamples, float Duration) Decode(ReadOnlySpan<byte> data)
    {
        if (!IsXa(data)) throw new ArgumentException("Invalid EA XA header magic");

        var decompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2));
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(10, 2));
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12, 4));
        var avgByteRate = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16, 4));
        var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(20, 2));
        var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(22, 2));

        if (channels is not (1 or 2)) throw new NotSupportedException($"Unsupported channel count: {channels}");
        if (sampleRate == 0) sampleRate = 22050;
        if (bitsPerSample == 0) bitsPerSample = 16;

        var payload = data[24..];
        int sampleCapacity = (int)Math.Max(decompressedSize / 2, (uint)(payload.Length * 2));
        var samples = new List<short>(sampleCapacity);

        if (channels == 1)
        {
            // Mono decoding: 15-byte blocks -> 28 samples
            int curSample = 0;
            int prevSample = 0;
            for (int offset = 0; offset + 15 <= payload.Length; offset += 15)
            {
                byte hdr = payload[offset];
                int hi = (hdr >> 4) & 0x03;
                int c1 = EaC1[hi];
                int c2 = EaC2[hi];
                int shift = (hdr & 0x0F) + 8;

                for (int i = 1; i < 15; i++)
                {
                    byte b = payload[offset + i];

                    // High nibble
                    int nibble = (b >> 4) & 0x0F;
                    int val = (nibble << 28) >> shift;
                    int predicted = (val + curSample * c1 + prevSample * c2 + 0x80) >> 8;
                    int s = Math.Clamp(predicted, -32768, 32767);
                    prevSample = curSample;
                    curSample = s;
                    samples.Add((short)s);

                    // Low nibble
                    nibble = b & 0x0F;
                    val = (nibble << 28) >> shift;
                    predicted = (val + curSample * c1 + prevSample * c2 + 0x80) >> 8;
                    s = Math.Clamp(predicted, -32768, 32767);
                    prevSample = curSample;
                    curSample = s;
                    samples.Add((short)s);
                }
            }
        }
        else
        {
            // Stereo decoding: 30-byte blocks -> 28 stereo frames (56 interleaved samples)
            int curLeft = 0, prevLeft = 0;
            int curRight = 0, prevRight = 0;
            for (int offset = 0; offset + 30 <= payload.Length; offset += 30)
            {
                byte hdrLeft = payload[offset];
                int hiLeft = (hdrLeft >> 4) & 0x03;
                int c1Left = EaC1[hiLeft];
                int c2Left = EaC2[hiLeft];
                int shiftLeft = (hdrLeft & 0x0F) + 8;

                byte hdrRight = payload[offset + 1];
                int hiRight = (hdrRight >> 4) & 0x03;
                int c1Right = EaC1[hiRight];
                int c2Right = EaC2[hiRight];
                int shiftRight = (hdrRight & 0x0F) + 8;

                for (int i = 2; i < 30; i += 2)
                {
                    byte bLeft = payload[offset + i];
                    byte bRight = payload[offset + i + 1];

                    // High nibble left
                    int nLeft = (bLeft >> 4) & 0x0F;
                    int valLeft = (nLeft << 28) >> shiftLeft;
                    int predLeft = (valLeft + curLeft * c1Left + prevLeft * c2Left + 0x80) >> 8;
                    int sLeft = Math.Clamp(predLeft, -32768, 32767);
                    prevLeft = curLeft;
                    curLeft = sLeft;

                    // High nibble right
                    int nRight = (bRight >> 4) & 0x0F;
                    int valRight = (nRight << 28) >> shiftRight;
                    int predRight = (valRight + curRight * c1Right + prevRight * c2Right + 0x80) >> 8;
                    int sRight = Math.Clamp(predRight, -32768, 32767);
                    prevRight = curRight;
                    curRight = sRight;

                    samples.Add((short)sLeft);
                    samples.Add((short)sRight);

                    // Low nibble left
                    nLeft = bLeft & 0x0F;
                    valLeft = (nLeft << 28) >> shiftLeft;
                    predLeft = (valLeft + curLeft * c1Left + prevLeft * c2Left + 0x80) >> 8;
                    sLeft = Math.Clamp(predLeft, -32768, 32767);
                    prevLeft = curLeft;
                    curLeft = sLeft;

                    // Low nibble right
                    nRight = bRight & 0x0F;
                    valRight = (nRight << 28) >> shiftRight;
                    predRight = (valRight + curRight * c1Right + prevRight * c2Right + 0x80) >> 8;
                    sRight = Math.Clamp(predRight, -32768, 32767);
                    prevRight = curRight;
                    curRight = sRight;

                    samples.Add((short)sLeft);
                    samples.Add((short)sRight);
                }
            }
        }

        // Trim to decompressed size if specified in header
        int targetSampleCount = decompressedSize > 0 ? (int)(decompressedSize / 2) : samples.Count;
        if (targetSampleCount > samples.Count) targetSampleCount = samples.Count;
        int frameCount = targetSampleCount / channels;
        int finalSampleCount = frameCount * channels;

        var pcm = new byte[finalSampleCount * 2];
        for (int i = 0; i < finalSampleCount; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), samples[i]);
        }

        var wav = CreateRiffWav((int)sampleRate, (short)channels, 16, pcm);
        float duration = (float)frameCount / sampleRate;
        return (wav, (int)sampleRate, channels, frameCount, duration);
    }

    public static byte[] CreateRiffWav(int sampleRate, short channels, short bitsPerSample, ReadOnlySpan<byte> pcmData)
    {
        var totalDataLen = pcmData.Length;
        var totalLength = totalDataLen + 36;
        var byteRate = sampleRate * channels * (bitsPerSample / 8);
        var blockAlign = (short)(channels * (bitsPerSample / 8));

        var header = new byte[44 + totalDataLen];
        header[0] = (byte)'R'; header[1] = (byte)'I'; header[2] = (byte)'F'; header[3] = (byte)'F';
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), totalLength);
        header[8] = (byte)'W'; header[9] = (byte)'A'; header[10] = (byte)'V'; header[11] = (byte)'E';
        header[12] = (byte)'f'; header[13] = (byte)'m'; header[14] = (byte)'t'; header[15] = (byte)' ';
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20, 2), 1); // PCM = 1
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, 4), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32, 2), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34, 2), bitsPerSample);
        header[36] = (byte)'d'; header[37] = (byte)'a'; header[38] = (byte)'t'; header[39] = (byte)'a';
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40, 4), totalDataLen);
        pcmData.CopyTo(header.AsSpan(44));
        return header;
    }
}
