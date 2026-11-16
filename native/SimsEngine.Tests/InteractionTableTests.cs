using SimsEngine.Formats;

namespace SimsEngine.Tests;

public class InteractionTableTests
{
    /// <summary>Encodes table fields using a presence bit, a two-bit byte width selector, and the
    /// variable-length value.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bit;

        public BitWriter Value(int value, ReadOnlySpan<byte> widths)
        {
            if (value == 0) return Bits(0, 1);
            for (var index = 0; index < widths.Length; index++)
            {
                var width = widths[index];
                var fits = width == 32 || (value >= -(1 << (width - 1)) && value < 1 << (width - 1));
                if (!fits) continue;
                Bits(1, 1);
                Bits((uint)index, 2);
                return Bits((uint)value & (width == 32 ? uint.MaxValue : (1u << width) - 1), width);
            }
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        public BitWriter Short(int value) => Value(value, [5, 8, 13, 16]);
        public BitWriter Int(int value) => Value(value, [6, 11, 21, 32]);

        private BitWriter Bits(uint value, int count)
        {
            for (var index = count - 1; index >= 0; index--)
            {
                if (_bit == 0)
                {
                    _bytes.Add(0);
                    _bit = 0x80;
                }
                if ((value >> index & 1) != 0) _bytes[^1] |= (byte)_bit;
                _bit >>= 1;
            }
            return this;
        }

        public byte[] ToArray() => [.. _bytes];
    }

    [Fact]
    public void Version9FieldEncodingDecodesSignedValues()
    {
        var bits = new BitWriter()
            .Short(4100).Short(4101).Int(1).Int(0x10).Int(3).Int(2).Int(BitConverter.SingleToInt32Bits(0.5f)).Int(-30).Int(-1)
            .Short(10).Short(-20).Short(5);
        var chunk = new ChunkBuilder().S16(1).S16(9).U8(1).Raw(bits.ToArray());
        var (file, iff) = chunk.InFile("TTAB");
        var table = InteractionTable.Read(file, iff);
        Assert.True(table.FieldEncoded);
        var entry = table.Interactions.Single();
        Assert.Equal((4100, 4101, 0x10u, 3, 2u), (entry.ActionTree, entry.GuardTree, entry.Flags, entry.TtaIndex, entry.AttenuationCode));
        Assert.Equal(0.5f, entry.AttenuationValue);
        Assert.Equal(unchecked((uint)-30), entry.AutonomyThreshold);
        Assert.Equal(-1, entry.JoiningIndex);
        Assert.Equal(new MotiveAdvertisement(10, -20, 5), entry.Motives.Single());
    }

    [Fact]
    public void Version8IsPlain()
    {
        var chunk = new ChunkBuilder().S16(1).S16(8).U16(4100).U16(0).S32(1).U32(1).S32(0).U32(0).F32(0).U32(0).S32(-1)
            .S16(1).S16(2).S16(3);
        var (file, iff) = chunk.InFile("TTAB");
        var table = InteractionTable.Read(file, iff);
        Assert.False(table.FieldEncoded);
        Assert.Equal(new MotiveAdvertisement(1, 2, 3), table.Interactions.Single().Motives.Single());
    }

    [Fact]
    public void Version5AlignsItsIntegersAndConvertsMaximaToDeltas()
    {
        // Interaction table entry headers include two padding bytes after the action, guard, and motive
        // count fields.
        var chunk = new ChunkBuilder().S16(1).S16(5).U16(4100).U16(4101).S16(2).U16(0xFFFF)
            .U32(1).S32(7).F32(0.25f).U32(30).S32(-1).S16(40).S16(-10);
        var (file, iff) = chunk.InFile("TTAB");
        var entry = InteractionTable.Read(file, iff).Interactions.Single();
        Assert.Equal((1u, 7, 30u, -1), (entry.Flags, entry.TtaIndex, entry.AutonomyThreshold, entry.JoiningIndex));
        Assert.Equal([new MotiveAdvertisement(0, 40, 0), new MotiveAdvertisement(0, -10, 0)], entry.Motives);
    }

    [Fact]
    public void Version1StoresMotiveNumbers()
    {
        var chunk = new ChunkBuilder().S16(1).S16(1).U16(4100).U16(4101).S16(1).S16(3).S16(50);
        var (file, iff) = chunk.InFile("TTAB");
        var entry = InteractionTable.Read(file, iff).Interactions.Single();
        Assert.Equal(0, entry.TtaIndex);
        Assert.Equal(4, entry.Motives.Count);
        Assert.Equal(50, entry.Motives[3].Delta);
    }

    [Fact]
    public void EmptyChunkIsAnEmptyTable()
    {
        var (file, iff) = new ChunkBuilder().InFile("TTAB");
        Assert.Empty(InteractionTable.Read(file, iff).Interactions);
    }
}
