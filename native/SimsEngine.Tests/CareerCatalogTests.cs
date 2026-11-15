using System.Buffers.Binary;
using System.Text;
using SimsEngine.Formats;

namespace SimsEngine.Tests;

public sealed class CareerCatalogTests
{
    [Fact]
    public void ReadsTheSourceCareerVectorAndJobFields()
    {
        var file = CareerFile(42, [2, 3, 4, 5, 6, 7, 8, 9, 10, 11], [0, -1, 2, -3, 4, -5, 6],
            salary: 120, start: 9, end: 17, car: 3);
        var careers = CareerCatalog.Read(file);

        Assert.Equal([42], careers.CareerTable());
        Assert.True(careers.CareerExists(42));
        Assert.False(careers.CareerExists(41));
        Assert.Equal((short?)1, careers.JobData(42, -1, 0));
        Assert.Equal((short?)120, careers.JobData(42, 0, 1));
        Assert.Equal((short?)2, careers.JobData(42, 0, 2));
        Assert.Equal((short?)11, careers.JobData(42, 0, 11));
        Assert.Equal((short?)9, careers.JobData(42, 0, 12));
        Assert.Equal((short?)17, careers.JobData(42, 0, 13));
        Assert.Equal((short?)-5, careers.JobData(42, 0, 19));
        Assert.Equal((short?)6, careers.JobData(42, 0, 20));
        Assert.Equal((short?)3, careers.JobData(42, 0, 21));
        // FUN_00403647 field 22 reports whether the level's male uniform mesh string is non-empty.
        Assert.Equal((short?)1, careers.JobData(42, 0, 22));
        Assert.Null(careers.JobData(42, 1, 12));
        Assert.Null(careers.JobData(41, 0, 12));
    }

    [Fact]
    public void JobTextReadsTheSourceStringLayoutAndPicksTheFemaleTitle()
    {
        var careers = CareerCatalog.Read(WithStrings(CareerFile(42, [2, 3, 4, 5, 6, 7, 8, 9, 10, 11], [0, -1, 2, -3, 4, -5, 6],
            salary: 120, start: 9, end: 17, car: 3), 42, ("Offer", 1), ("Offer (f)", 2), ("Clerk", 3), ("Clerk desc", 4), ("Clerkess", 23)));

        Assert.Equal("Clerk", careers.JobText(42, 0, JobTextKind.Title, female: false, 1));
        Assert.Equal("Clerkess", careers.JobText(42, 0, JobTextKind.Title, female: true, 1));
        Assert.Equal("Clerk desc", careers.JobText(42, 0, JobTextKind.Description, female: true, 1));
        Assert.Equal("Offer", careers.JobText(42, 0, JobTextKind.Offer, female: false, 1));
        Assert.Equal("Offer (f)", careers.JobText(42, 0, JobTextKind.Offer, female: true, 1));
        Assert.Null(careers.JobText(42, 1, JobTextKind.Title, female: false, 1));
        Assert.Null(careers.JobText(41, 0, JobTextKind.Title, female: false, 1));
    }

    /// <summary>Appends a format 0 STR# chunk of 24 entries, <paramref name="entries"/> placed at their zero-based index.</summary>
    private static IffFile WithStrings(IffFile career, ushort id, params (string Text, int Index)[] entries)
    {
        var strings = new string[24];
        Array.Fill(strings, string.Empty);
        foreach (var (text, index) in entries) strings[index] = text;
        var data = new List<byte>();
        data.AddRange(BitConverter.GetBytes((short)strings.Length));
        foreach (var value in strings) { var bytes = Encoding.Latin1.GetBytes(value); data.Add((byte)bytes.Length); data.AddRange(bytes); }
        return career.WithAddedChunk(ResourceType.Str, id, data.ToArray());
    }

    private static IffFile CareerFile(ushort id, int[] requirements, int[] motives, int salary, int start, int end, int car)
    {
        var body = new List<byte>(new byte[12]) { 1 };
        body.AddRange(Encoding.Latin1.GetBytes("Test\0")); // even-sized payload including terminator
        var bits = new CareerFieldWriter(body);
        bits.Write(1);
        foreach (var value in requirements) bits.Write(value);
        foreach (var value in motives) bits.Write(value);
        bits.Write(salary);
        bits.Write(start);
        bits.Write(end);
        bits.Write(car);
        bits.WriteString("Level One", moreFieldsFollow: false);
        bits.WriteString("male", moreFieldsFollow: false);
        bits.WriteString("female", moreFieldsFollow: false);
        bits.WriteString("skin", moreFieldsFollow: false);
        bits.WriteString("tail", moreFieldsFollow: false);
        var data = body.ToArray();

        var output = new byte[IffFile.HeaderSize + IffFile.ChunkHeaderSize + data.Length];
        "IFF FILE"u8.CopyTo(output);
        var header = output.AsSpan(IffFile.HeaderSize, IffFile.ChunkHeaderSize);
        "CARR"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)(IffFile.ChunkHeaderSize + data.Length));
        BinaryPrimitives.WriteUInt16BigEndian(header[8..], id);
        data.CopyTo(output, IffFile.HeaderSize + IffFile.ChunkHeaderSize);
        return new IffFile(output, "synthetic-work.iff");
    }

    private sealed class CareerFieldWriter(List<byte> output)
    {
        private int _current;
        private int _bits;

        public void Write(int value)
        {
            if (value == 0) { Bit(0); return; }
            var (selector, width) = value is >= -32 and <= 31 ? (0, 6)
                : value is >= -1024 and <= 1023 ? (1, 11)
                : value is >= -1_048_576 and <= 1_048_575 ? (2, 21)
                : (3, 32);
            Bit(1);
            Bits((uint)selector, 2);
            Bits(unchecked((uint)value) & (width == 32 ? uint.MaxValue : (1u << width) - 1), width);
        }

        public void WriteString(string value, bool moreFieldsFollow)
        {
            FlushBits();
            output.AddRange(Encoding.Latin1.GetBytes(value + "\0"));
            if ((output.Count & 1) != 0) output.Add(0);
            if (moreFieldsFollow) _bits = 0;
            else _bits = 8;
        }

        private void Bits(uint value, int count)
        {
            for (var bit = count - 1; bit >= 0; bit--) Bit((int)(value >> bit & 1));
        }

        private void Bit(int bit)
        {
            _current = (_current << 1) | bit;
            if (++_bits != 8) return;
            output.Add((byte)_current);
            _current = 0;
            _bits = 0;
        }

        private void FlushBits()
        {
            if (_bits is 0 or 8) return;
            output.Add((byte)(_current << (8 - _bits)));
            _current = 0;
            _bits = 0;
        }
    }
}
