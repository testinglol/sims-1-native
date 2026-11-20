using System.Buffers.Binary;

namespace SimsEngine.Formats;

/// <summary>Decodes MicroTalk (UTK/UTM0) linear-predictive speech packets into 16-bit PCM audio.</summary>
public static class UtkAudio
{
    private static readonly float[] RcTable =
    [
        +0.000000f, -0.996776f, -0.990327f, -0.983879f,
        -0.977431f, -0.970982f, -0.964534f, -0.958085f,
        -0.951637f, -0.930754f, -0.904960f, -0.879167f,
        -0.853373f, -0.827579f, -0.801786f, -0.775992f,
        -0.750198f, -0.724405f, -0.698611f, -0.670635f,
        -0.619048f, -0.567460f, -0.515873f, -0.464286f,
        -0.412698f, -0.361111f, -0.309524f, -0.257937f,
        -0.206349f, -0.154762f, -0.103175f, -0.051587f,
        +0.000000f, +0.051587f, +0.103175f, +0.154762f,
        +0.206349f, +0.257937f, +0.309524f, +0.361111f,
        +0.412698f, +0.464286f, +0.515873f, +0.567460f,
        +0.619048f, +0.670635f, +0.698611f, +0.724405f,
        +0.750198f, +0.775992f, +0.801786f, +0.827579f,
        +0.853373f, +0.879167f, +0.904960f, +0.930754f,
        +0.951637f, +0.958085f, +0.964534f, +0.970982f,
        +0.977431f, +0.983879f, +0.990327f, +0.996776f
    ];

    private static readonly byte[][] Codebooks =
    [
        [
            4,  6,  5,  9,  4,  6,  5, 13,  4,  6,  5, 10,  4,  6,  5, 17,
            4,  6,  5,  9,  4,  6,  5, 14,  4,  6,  5, 10,  4,  6,  5, 21,
            4,  6,  5,  9,  4,  6,  5, 13,  4,  6,  5, 10,  4,  6,  5, 18,
            4,  6,  5,  9,  4,  6,  5, 14,  4,  6,  5, 10,  4,  6,  5, 25,
            4,  6,  5,  9,  4,  6,  5, 13,  4,  6,  5, 10,  4,  6,  5, 17,
            4,  6,  5,  9,  4,  6,  5, 14,  4,  6,  5, 10,  4,  6,  5, 22,
            4,  6,  5,  9,  4,  6,  5, 13,  4,  6,  5, 10,  4,  6,  5, 18,
            4,  6,  5,  9,  4,  6,  5, 14,  4,  6,  5, 10,  4,  6,  5,  0,
            4,  6,  5,  9,  4,  6,  5, 13,  4,  6,  5, 10,  4,  6,  5, 17,
            4,  6,  5,  9,  4,  6,  5, 14,  4,  6,  5, 10,  4,  6,  5, 21,
            4,  6,  5,  9,  4,  6,  5, 13,  4,  6,  5, 10,  4,  6,  5, 18,
            4,  6,  5,  9,  4,  6,  5, 14,  4,  6,  5, 10,  4,  6,  5, 26,
            4,  6,  5,  9,  4,  6,  5, 13,  4,  6,  5, 10,  4,  6,  5, 17,
            4,  6,  5,  9,  4,  6,  5, 14,  4,  6,  5, 10,  4,  6,  5, 22,
            4,  6,  5,  9,  4,  6,  5, 13,  4,  6,  5, 10,  4,  6,  5, 18,
            4,  6,  5,  9,  4,  6,  5, 14,  4,  6,  5, 10,  4,  6,  5,  2
        ],
        [
            4, 11,  7, 15,  4, 12,  8, 19,  4, 11,  7, 16,  4, 12,  8, 23,
            4, 11,  7, 15,  4, 12,  8, 20,  4, 11,  7, 16,  4, 12,  8, 27,
            4, 11,  7, 15,  4, 12,  8, 19,  4, 11,  7, 16,  4, 12,  8, 24,
            4, 11,  7, 15,  4, 12,  8, 20,  4, 11,  7, 16,  4, 12,  8,  1,
            4, 11,  7, 15,  4, 12,  8, 19,  4, 11,  7, 16,  4, 12,  8, 23,
            4, 11,  7, 15,  4, 12,  8, 20,  4, 11,  7, 16,  4, 12,  8, 28,
            4, 11,  7, 15,  4, 12,  8, 19,  4, 11,  7, 16,  4, 12,  8, 24,
            4, 11,  7, 15,  4, 12,  8, 20,  4, 11,  7, 16,  4, 12,  8,  3,
            4, 11,  7, 15,  4, 12,  8, 19,  4, 11,  7, 16,  4, 12,  8, 23,
            4, 11,  7, 15,  4, 12,  8, 20,  4, 11,  7, 16,  4, 12,  8, 27,
            4, 11,  7, 15,  4, 12,  8, 19,  4, 11,  7, 16,  4, 12,  8, 24,
            4, 11,  7, 15,  4, 12,  8, 20,  4, 11,  7, 16,  4, 12,  8,  1,
            4, 11,  7, 15,  4, 12,  8, 19,  4, 11,  7, 16,  4, 12,  8, 23,
            4, 11,  7, 15,  4, 12,  8, 20,  4, 11,  7, 16,  4, 12,  8, 28,
            4, 11,  7, 15,  4, 12,  8, 19,  4, 11,  7, 16,  4, 12,  8, 24,
            4, 11,  7, 15,  4, 12,  8, 20,  4, 11,  7, 16,  4, 12,  8,  3
        ]
    ];

    private readonly struct Command
    {
        public readonly int NextModel;
        public readonly int CodeSize;
        public readonly float PulseValue;

        public Command(int nextModel, int codeSize, float pulseValue)
        {
            NextModel = nextModel;
            CodeSize = codeSize;
            PulseValue = pulseValue;
        }
    }

    private static readonly Command[] Commands =
    [
        new(1, 8,  0.0f),
        new(1, 7,  0.0f),
        new(0, 8,  0.0f),
        new(0, 7,  0.0f),
        new(0, 2,  0.0f),
        new(0, 2, -1.0f),
        new(0, 2, +1.0f),
        new(0, 3, -1.0f),
        new(0, 3, +1.0f),
        new(1, 4, -2.0f),
        new(1, 4, +2.0f),
        new(1, 3, -2.0f),
        new(1, 3, +2.0f),
        new(1, 5, -3.0f),
        new(1, 5, +3.0f),
        new(1, 4, -3.0f),
        new(1, 4, +3.0f),
        new(1, 6, -4.0f),
        new(1, 6, +4.0f),
        new(1, 5, -4.0f),
        new(1, 5, +4.0f),
        new(1, 7, -5.0f),
        new(1, 7, +5.0f),
        new(1, 6, -5.0f),
        new(1, 6, +5.0f),
        new(1, 8, -6.0f),
        new(1, 8, +6.0f),
        new(1, 7, -6.0f),
        new(1, 7, +6.0f)
    ];

    private static readonly byte[] Bitmasks = [0x01, 0x03, 0x07, 0x0F, 0x1F, 0x3F, 0x7F, 0xFF];

    private sealed class BitReader(byte[] buffer, int offset)
    {
        private int pos = offset;
        private uint bitsValue;
        private int bitsCount;

        public void InitBits()
        {
            if (bitsCount == 0)
            {
                if (pos >= buffer.Length)
                    throw new EndOfStreamException("Unexpected end of UTK bitstream");
                bitsValue = buffer[pos++];
                bitsCount = 8;
            }
        }

        public byte PeekBits(int count)
        {
            byte mask = Bitmasks[count - 1];
            return (byte)(bitsValue & mask);
        }

        public byte ReadBits(int count)
        {
            if (bitsCount < count && pos < buffer.Length)
            {
                bitsValue |= (uint)buffer[pos++] << bitsCount;
                bitsCount += 8;
            }
            if (bitsCount < count)
                throw new EndOfStreamException("Unexpected end of UTK bitstream");

            byte mask = Bitmasks[count - 1];
            byte ret = (byte)(bitsValue & mask);
            bitsValue >>= count;
            bitsCount -= count;

            if (bitsCount < 8 && pos < buffer.Length)
            {
                bitsValue |= (uint)buffer[pos++] << bitsCount;
                bitsCount += 8;
            }

            return ret;
        }

        public void ConsumeBits(int count) => ReadBits(count);

        public bool HasMoreBytes => pos < buffer.Length || bitsCount > 0;
    }

    public static bool IsUtk(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32) return false;
        return data[0] == (byte)'U' && data[1] == (byte)'T' && data[2] == (byte)'M' && data[3] == (byte)'0';
    }

    public static byte[] DecodeToWav(ReadOnlySpan<byte> data) => Decode(data).WavBytes;

    public static (byte[] WavBytes, int SampleRate, int Channels, int TotalSamples, float Duration) Decode(ReadOnlySpan<byte> data)
    {
        if (!IsUtk(data)) throw new ArgumentException("Invalid UTK / UTM0 header magic");
        if (data.Length <= 0x20) throw new ArgumentException("Truncated UTK / UTM0 payload");

        var pcmBytes = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(14, 2));
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16, 4));

        if (sampleRate == 0) sampleRate = 22050;
        if (channels == 0) channels = 1;

        var br = new BitReader(data.ToArray(), 0x20);
        br.InitBits();

        bool reducedBandwidth = br.ReadBits(1) == 1;
        int baseThre = br.ReadBits(4);
        int baseGain = br.ReadBits(4);
        int baseMult = br.ReadBits(6);

        int multipulseThreshold = 32 - baseThre;
        float[] fixedGains = new float[64];
        fixedGains[0] = 8.0f * (1 + baseGain);
        float multiplier = 1.04f + baseMult * 0.001f;
        for (int i = 1; i < 64; i++) fixedGains[i] = fixedGains[i - 1] * multiplier;

        float[] rcData = new float[12];
        float[] synthHistory = new float[12];
        float[] subframes = new float[324 + 432]; // Indices 0..323 store the adaptive codebook; 324..755 store speech samples.

        int targetSamples = pcmBytes > 0 ? (int)Math.Min(pcmBytes / 2, int.MaxValue / 2) : 22050;
        var pcmSamples = new List<short>(Math.Min(targetSamples, 65536));

        while (pcmSamples.Count < targetSamples && br.HasMoreBytes)
        {
            bool useMultipulse = false;
            float[] excitation = new float[5 + 108 + 5];
            float[] rcDelta = new float[12];

            for (int i = 0; i < 12; i++)
            {
                int idx;
                if (i == 0)
                {
                    idx = br.ReadBits(6);
                    if (idx < multipulseThreshold) useMultipulse = true;
                }
                else if (i < 4)
                {
                    idx = br.ReadBits(6);
                }
                else
                {
                    idx = 16 + br.ReadBits(5);
                }
                rcDelta[i] = (RcTable[idx] - rcData[i]) * 0.25f;
            }

            for (int sub = 0; sub < 4; sub++)
            {
                int pitchLag = br.ReadBits(8);
                int pitchVal = br.ReadBits(4);
                int gainIdx = br.ReadBits(6);

                float pitchGain = (float)pitchVal / 15.0f;
                float fixedGain = fixedGains[gainIdx];

                if (!reducedBandwidth)
                {
                    DecodeExcitation(br, useMultipulse, excitation, 5, 1);
                }
                else
                {
                    int align = br.ReadBits(1);
                    int zeroFlag = br.ReadBits(1);
                    DecodeExcitation(br, useMultipulse, excitation, 5 + align, 2);

                    if (zeroFlag != 0)
                    {
                        for (int j = 0; j < 54; j++) excitation[5 + (1 - align) + 2 * j] = 0.0f;
                    }
                    else
                    {
                        Array.Clear(excitation, 0, 5);
                        Array.Clear(excitation, 5 + 108, 5);
                        InterpolateRest(excitation, 5 + (1 - align));
                        fixedGain *= 0.5f;
                    }
                }

                for (int j = 0; j < 108; j++)
                {
                    int idx = 108 * sub + 216 - pitchLag + j;
                    if (idx < 0) idx = 0;
                    float tmp1 = fixedGain * excitation[5 + j];
                    float tmp2 = pitchGain * subframes[idx];
                    subframes[324 + 108 * sub + j] = tmp1 + tmp2;
                }
            }

            for (int i = 0; i < 324; i++) subframes[i] = subframes[324 + 108 + i];

            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 12; j++) rcData[j] += rcDelta[j];
                int blocks = i < 3 ? 1 : 33;
                LpSynthesisFilter(subframes, 324 + 12 * i, blocks, rcData, synthHistory);
            }

            for (int i = 0; i < 432 && pcmSamples.Count < targetSamples; i++)
            {
                float s = subframes[324 + i];
                short sample = (short)Math.Clamp((int)Math.Round(s), -32768, 32767);
                pcmSamples.Add(sample);
            }
        }

        var pcmBuffer = new byte[pcmSamples.Count * 2];
        for (int i = 0; i < pcmSamples.Count; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcmBuffer.AsSpan(i * 2, 2), pcmSamples[i]);
        }

        var wav = XaAudio.CreateRiffWav((int)sampleRate, (short)channels, 16, pcmBuffer);
        float duration = (float)pcmSamples.Count / sampleRate;
        return (wav, (int)sampleRate, (int)channels, pcmSamples.Count, duration);
    }

    private static void DecodeExcitation(BitReader br, bool useMultipulse, float[] outBuf, int start, int stride)
    {
        int i = 0;
        if (useMultipulse)
        {
            int model = 0;
            while (i < 108)
            {
                int huffmanCode = br.PeekBits(8);
                int cmd = Codebooks[model][huffmanCode];
                model = Commands[cmd].NextModel;
                br.ConsumeBits(Commands[cmd].CodeSize);

                if (cmd > 3)
                {
                    outBuf[start + i] = Commands[cmd].PulseValue;
                    i += stride;
                }
                else if (cmd > 1)
                {
                    int count = 7 + br.ReadBits(6);
                    if (i + count * stride > 108) count = (108 - i) / stride;
                    while (count > 0)
                    {
                        outBuf[start + i] = 0.0f;
                        i += stride;
                        count--;
                    }
                }
                else
                {
                    int x = 7;
                    while (br.ReadBits(1) != 0) x++;
                    if (br.ReadBits(1) == 0) x *= -1;
                    outBuf[start + i] = x;
                    i += stride;
                }
            }
        }
        else
        {
            while (i < 108)
            {
                int huffmanCode = br.PeekBits(2);
                float val = 0.0f;
                int bits = 1;
                switch (huffmanCode)
                {
                    case 0:
                    case 2:
                        val = 0.0f;
                        bits = 1;
                        break;
                    case 1:
                        val = -2.0f;
                        bits = 2;
                        break;
                    case 3:
                        val = 2.0f;
                        bits = 2;
                        break;
                }
                br.ConsumeBits(bits);
                outBuf[start + i] = val;
                i += stride;
            }
        }
    }

    private static void InterpolateRest(float[] excitation, int start)
    {
        for (int i = 0; i < 108; i += 2)
        {
            int idx = start + i;
            float tmp1 = (excitation[idx - 5] + excitation[idx + 5]) * 0.01803268f;
            float tmp2 = (excitation[idx - 3] + excitation[idx + 3]) * 0.11459156f;
            float tmp3 = (excitation[idx - 1] + excitation[idx + 1]) * 0.59738597f;
            excitation[idx] = tmp1 - tmp2 + tmp3;
        }
    }

    private static void RcToLpc(float[] rcData, float[] lpc)
    {
        float[] tmp1 = new float[12];
        float[] tmp2 = new float[12];

        for (int i = 10; i >= 0; i--) tmp2[i + 1] = rcData[i];
        tmp2[0] = 1.0f;

        for (int i = 0; i < 12; i++)
        {
            float x = -(rcData[11] * tmp2[11]);
            for (int j = 10; j >= 0; j--)
            {
                x -= rcData[j] * tmp2[j];
                tmp2[j + 1] = x * rcData[j] + tmp2[j];
            }
            tmp2[0] = x;
            tmp1[i] = x;

            for (int j = 0; j < i; j++)
            {
                x -= tmp1[i - 1 - j] * lpc[j];
            }
            lpc[i] = x;
        }
    }

    private static void LpSynthesisFilter(float[] samples, int offset, int blocks, float[] rcData, float[] synthHistory)
    {
        float[] lpc = new float[12];
        RcToLpc(rcData, lpc);

        int ptr = offset;
        for (int i = 0; i < blocks; i++)
        {
            for (int j = 0; j < 12; j++)
            {
                float x = samples[ptr];
                int k;
                for (k = 0; k < j; k++) x += lpc[k] * synthHistory[k - j + 12];
                for (; k < 12; k++) x += lpc[k] * synthHistory[k - j + 0];

                synthHistory[11 - j] = x;
                samples[ptr++] = x;
            }
        }
    }
}
