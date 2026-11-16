using SimsEngine.Formats;
using Xunit;

namespace SimsEngine.Tests;

public sealed class PersistWriterTests
{
    private static PersistReader Reader(byte[] body)
    {
        var file = new IffFile(ChunkBuilder.File(("PST1", 1, body)), "persist.iff");
        return new PersistReader(file, file.Find(ResourceType.Parse("PST1"), 1)!);
    }

    [Fact]
    public void UnswappedUncodedValuesRoundTrip()
    {
        var writer = PersistWriter.Start(false, 0x3e, "Mjbo");
        writer.WriteCodingFlag(false);
        writer.Int16(-2);
        writer.Int32(0x12345678);
        writer.String("Hi");

        var reader = Reader(writer.ToArray());

        Assert.Equal(0x3e, reader.Version);
        reader.ReadCodingFlag();
        Assert.False(reader.FieldCoded);
        Assert.Equal((short)-2, reader.Int16());
        Assert.Equal(0x12345678, reader.Int32());
        Assert.Equal("Hi", reader.String());
    }

    [Fact]
    public void SwappedValuesAreBigEndian()
    {
        var writer = PersistWriter.Start(true, 0x3e, "Mjbo");
        writer.WriteCodingFlag(false);
        writer.Int16(0x1234);
        writer.Int32(0x01020304);

        var bytes = writer.ToArray();
        Assert.Equal([0x12, 0x34], bytes[13..15]);
        Assert.Equal([1, 2, 3, 4], bytes[15..19]);
        var reader = Reader(bytes);
        reader.ReadCodingFlag();
        Assert.Equal((short)0x1234, reader.Int16());
        Assert.Equal(0x01020304, reader.Int32());
    }

    [Fact]
    public void CodedValuesRoundTripAtEveryWidth()
    {
        short[] shorts = [0, 1, -1, 31, 127, -128, 128, -129, 32767, -32768];
        int[] ints = [0, 1, 100000, -1, 0x7fffff, 0x800000, int.MinValue, int.MaxValue];
        byte[] bytes = [0, 1, 63, 64, 200, 255];

        var writer = PersistWriter.Start(false, 0x3e, "Mjbo");
        writer.WriteCodingFlag(true);
        foreach (var value in shorts) writer.Int16(value);
        foreach (var value in ints) writer.Int32(value);
        foreach (var value in bytes) writer.Byte(value);
        writer.Single(1.5f);
        writer.Bool(true);
        writer.String("Hi");

        var reader = Reader(writer.ToArray());
        reader.ReadCodingFlag();

        Assert.True(reader.FieldCoded);
        foreach (var value in shorts) Assert.Equal(value, reader.Int16());
        foreach (var value in ints) Assert.Equal(value, reader.Int32());
        foreach (var value in bytes) Assert.Equal(value, reader.Byte());
        Assert.Equal(1.5f, reader.Single());
        Assert.True(reader.Bool());
        Assert.Equal("Hi", reader.String());
    }

    [Fact]
    public void BlocksStoreTheOffsetToTheNextBlock()
    {
        var writer = PersistWriter.Start(false, 0x3e, "Mjbo");
        writer.WriteCodingFlag(false);
        writer.BeginBlock();
        writer.Int32(5);
        writer.EndBlock();
        writer.BeginBlock();
        writer.Int32(6);
        writer.EndBlock();

        var reader = Reader(writer.ToArray());
        reader.ReadCodingFlag();
        reader.BeginBlock();
        var firstEnd = reader.BlockEnd;
        Assert.Equal(5, reader.Int32());
        reader.EndBlock();
        Assert.Equal(firstEnd, reader.Position);
        reader.BeginBlock();
        Assert.Equal(6, reader.Int32());
        reader.EndBlock();
    }

    [Fact]
    public void CodedValuesRoundTripInsideBlocks()
    {
        var writer = PersistWriter.Start(false, 0x3e, "Mjbo");
        writer.WriteCodingFlag(true);
        writer.BeginBlock();
        writer.Int16(1234);
        writer.EndBlock();
        writer.BeginBlock();
        writer.Int16(-1234);
        writer.EndBlock();

        var reader = Reader(writer.ToArray());
        reader.ReadCodingFlag();
        reader.BeginBlock();
        Assert.Equal((short)1234, reader.Int16());
        reader.EndBlock();
        reader.BeginBlock();
        Assert.Equal((short)-1234, reader.Int16());
        reader.EndBlock();
    }
}
