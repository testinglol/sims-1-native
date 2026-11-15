using System.Buffers.Binary;
using SimsEngine.Formats;
using Xunit;

namespace SimsEngine.Tests;

public sealed class IffFileTests
{
    [Fact]
    public void ReplacesSameLengthChunkWithoutMovingTheResourceMap()
    {
        byte[] simi = [1, 2, 3, 4];
        byte[] resources = [5, 6, 7, 8, 9];
        var bytes = ChunkBuilder.File(("SIMI", 1, simi), ("TEST", 2, [10, 11]), ("rsmp", 0, resources));
        var resourceMapOffset = IffFile.HeaderSize + IffFile.ChunkHeaderSize + simi.Length
            + IffFile.ChunkHeaderSize + 2;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(60), (uint)resourceMapOffset);
        var original = new IffFile(bytes, "replace-chunk.iff");

        var updated = original.WithChunkData(ResourceType.Parse("SIMI"), 1, new byte[] { 20, 21, 22, 23 });

        Assert.Equal(original.Bytes.Length, updated.Bytes.Length);
        Assert.Equal(original.Bytes.Span[..IffFile.HeaderSize].ToArray(), updated.Bytes.Span[..IffFile.HeaderSize].ToArray());
        Assert.Equal((uint)resourceMapOffset, BinaryPrimitives.ReadUInt32BigEndian(updated.Bytes.Span[60..]));
        Assert.Equal(new byte[] { 20, 21, 22, 23 }, updated.Find(ResourceType.Parse("SIMI"), 1)!.Data.ToArray());
        Assert.Equal(new byte[] { 10, 11 }, updated.Find(ResourceType.Parse("TEST"), 2)!.Data.ToArray());
        Assert.Equal(resources, updated.Find(ResourceType.Rsmp, 0)!.Data.ToArray());
        Assert.Equal(simi, original.Find(ResourceType.Parse("SIMI"), 1)!.Data.ToArray());
    }

    [Fact]
    public void RejectsReplacementThatWouldInvalidateResourceOffsets()
    {
        var file = new IffFile(ChunkBuilder.File(("SIMI", 1, new byte[] { 1, 2, 3 })), "fixed-size.iff");

        Assert.Throws<ArgumentException>(() => file.WithChunkData(ResourceType.Parse("SIMI"), 1, new byte[] { 4, 5 }));
    }

    [Fact]
    public void MissingChunkDoesNotProduceAnEditedFile()
    {
        var file = new IffFile(ChunkBuilder.File(("SIMI", 1, new byte[] { 1 })), "missing.iff");

        Assert.Throws<KeyNotFoundException>(() => file.WithChunkData(ResourceType.Parse("NGBH"), 1, new byte[] { 1 }));
    }
}
