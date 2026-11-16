using System.Buffers.Binary;
using SimsEngine.Formats;
using SimsEngine.World;

namespace SimsEngine.Tests;

public sealed class LotFloorsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DynamicPatternPromotesWithoutTruncatingOtherCellsOrChangingLegacyPlane(int level)
    {
        var legacyId = (ushort)(level == 1 ? 1 : 101);
        var advancedId = (ushort)(legacyId + 10);
        var data = new byte[64 * 64];
        data[4 * 64 + 5] = 29;
        var original = new IffFile(ChunkBuilder.File(("Arry", legacyId, Array(1, data)), ("KEEP", 7, [42]),
            ("rsmp", 0, ResourceMap.Write([]))), "legacy-floor.iff");
        var floors = LotFloors.Read(original, level);
        floors[9, 12] = 256;
        floors[12, 9] = 65533;

        var saved = floors.WriteTo(original);
        var loaded = LotFloors.Read(new IffFile(saved.Bytes, "native-save.iff"), level);

        Assert.Equal((ushort)256, loaded[9, 12]);
        Assert.Equal((ushort)65533, loaded[12, 9]);
        Assert.Equal((ushort)29, loaded[4, 5]);
        Assert.Equal((ushort)0, loaded[5, 4]);
        Assert.Equal(original.Find(ResourceType.Parse("Arry"), legacyId)!.Data.ToArray(),
            saved.Find(ResourceType.Parse("Arry"), legacyId)!.Data.ToArray());
        Assert.Equal(new byte[] { 42 }, saved.Find(ResourceType.Parse("KEEP"), 7)!.Data.ToArray());
        var advanced = saved.Find(ResourceType.Parse("Arry"), advancedId)!;
        Assert.Equal(2, LotArray.Read(saved, advanced).ElementSize);
        var index = ResourceMap.Read(saved.Find(ResourceType.Rsmp, 0)!);
        var indexed = index.Single(group => group.Type == ResourceType.Parse("Arry")).Entries.Single(entry => entry.Id == advancedId);
        Assert.Equal(advanced.Offset, indexed.Offset);
        // Subsequent floor modifications must write to the expanded floor array rather than a legacy
        // single-byte plane.
        loaded[9, 12] = 1;
        var edited = loaded.WriteTo(saved);
        Assert.Equal((ushort)1, LotFloors.Read(edited, level)[9, 12]);
        Assert.Equal((ushort)65533, LotFloors.Read(edited, level)[12, 9]);
        Assert.Single(edited.OfType(ResourceType.Parse("Arry")), chunk => chunk.Id == advancedId);
    }

    [Fact]
    public void ByteOnlyEditsRetainTheirExistingPlane()
    {
        var original = new IffFile(ChunkBuilder.File(("Arry", 1, Array(1, new byte[64 * 64]))), "byte-floor.iff");
        var floors = LotFloors.Read(original, 1);
        floors[3, 8] = 29;
        var saved = floors.WriteTo(original);
        Assert.Null(saved.Find(ResourceType.Parse("Arry"), 11));
        Assert.Equal((ushort)29, LotFloors.Read(saved, 1)[3, 8]);
    }

    [Fact]
    public void UpperRoomClassificationUsesTheWholeFloorWord()
    {
        var words = new byte[64 * 64 * 2];
        BinaryPrimitives.WriteUInt16LittleEndian(words.AsSpan((3 * 64 + 4) * 2), 256);
        var file = new IffFile(ChunkBuilder.File(("Arry", 101, Array(1, new byte[64 * 64])),
            ("Arry", 111, Array(2, words)), ("Arry", 102, Array(8, new byte[64 * 64 * 8]))), "upper-floor.iff");
        var rooms = RoomMap.Build(file, 2, 8, 8);
        Assert.True(rooms.TryGetFloorGridWord(3, 4, out var pattern));
        Assert.Equal((ushort)256, pattern);
        Assert.NotEqual(rooms.RoomAt(3, 4), rooms.RoomAt(3, 5));
    }

    private static byte[] Array(int elementSize, byte[] data) => new ChunkBuilder()
        .S16(0).S16(0).S32(64).S32(64).S32(elementSize).S32(0).Raw(data).ToArray();
}
