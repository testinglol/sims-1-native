using SimsEngine.Formats;

namespace SimsEngine.Tests;

public class TreeResourceTests
{
    private static ChunkBuilder SlotHeader(int version, int count) =>
        new ChunkBuilder().U32(0).S32(version).Tag("TOLS").S32(count);

    private static ChunkBuilder SlotBase(ChunkBuilder chunk, uint flags) =>
        chunk.U16(3).F32(1.5f).F32(-2f).F32(0.25f).S32(1).S32(2).S32(3).U32(flags);

    [Fact]
    public void SlotVersion10ReadsEveryField()
    {
        var chunk = SlotBase(SlotHeader(10, 1), 0x10)
            .S32(7).S32(1).S32(2).S32(3).S32(40).S32(9).F32(0.5f).S32(4).S32(2).S32(32);
        var (file, iff) = chunk.InFile("SLOT");
        var slot = SlotTable.Read(file, iff).Slots.Single();
        Assert.Equal(new Slot(3, 1.5f, -2f, 0.25f, 1, 2, 3, 0x10, 7, 1, 2, 3, 40, 9, 0.5f, 4, 2, 32), slot);
    }

    [Fact]
    public void SlotBelowVersion9TakesFacingFromFlagsAndScalesProximities()
    {
        var chunk = SlotBase(SlotHeader(6, 1), 0x201).S32(-1).S32(1).S32(2).S32(3).S32(0).S32(0);
        var (file, iff) = chunk.InFile("SLOT");
        var slot = SlotTable.Read(file, iff).Slots.Single();
        Assert.Equal(-3, slot.Facing);
        Assert.Equal(1u, slot.Flags);
        Assert.Equal((16, 32, 48), (slot.MinProximity, slot.MaxProximity, slot.OptimalProximity));
        Assert.Equal(0.1875f * 0.0625f, slot.Gradient);
        Assert.Equal(16, slot.Resolution);
    }

    [Fact]
    public void SlotVersion3UsesScaledDefaults()
    {
        var (file, iff) = SlotBase(SlotHeader(3, 1), 0x400).InFile("SLOT");
        var slot = SlotTable.Read(file, iff).Slots.Single();
        Assert.Equal((-1, -1, 0u), (slot.SnapTargetSlot, slot.Facing, slot.Flags));
        Assert.Equal((256, 256, 256), (slot.MinProximity, slot.MaxProximity, slot.OptimalProximity));
    }

    [Fact]
    public void SlotCountBeyondTheDataIsAnError()
    {
        var (file, iff) = SlotHeader(10, 5).InFile("SLOT");
        Assert.Throws<ResourceFormatException>(() => SlotTable.Read(file, iff));
    }

    [Fact]
    public void TprpVersion4SkipsEvenPadding()
    {
        var chunk = new ChunkBuilder().U32(0).S32(4).Tag("PRPT").S32(2).S32(1)
            .EvenString("Random").EvenString("param 1", 0).EvenString("Temp")
            .U32(1).U8(1).U8(0).U32(5).U32(6);
        var (file, iff) = chunk.InFile("TPRP");
        var names = TreeParameterNames.Read(file, iff);
        Assert.Equal(["Random", "param 1"], names.ParameterNames);
        Assert.Equal(["Temp"], names.LocalNames);
        Assert.True(names.Flag);
        Assert.Equal([1, 0], names.ParameterFlags);
        Assert.Equal((5u, 6u), (names.Value1fc, names.Value08));
        Assert.True(names.ReadBySimsExe);
    }

    [Fact]
    public void TprpVersion1DefaultsParameterFlagsToOne()
    {
        var chunk = new ChunkBuilder().U32(0).S32(1).Tag("PRPT").S32(2).S32(0).EvenString("a").EvenString("b").U32(0);
        var (file, iff) = chunk.InFile("TPRP");
        var names = TreeParameterNames.Read(file, iff);
        Assert.False(names.Flag);
        Assert.Equal([1, 1], names.ParameterFlags);
    }

    [Fact]
    public void TprpVersion5UsesTheOnlineLayoutWithoutTag()
    {
        var chunk = new ChunkBuilder().U32(0).S32(5).U32(0).S32(1).S32(1)
            .VarString("param 0").VarString("Größe").U8(1).U8(1).U32(5).U32(0);
        var (file, iff) = chunk.InFile("TPRP");
        var names = TreeParameterNames.Read(file, iff);
        Assert.Equal(["param 0"], names.ParameterNames);
        Assert.Equal(["Größe"], names.LocalNames);
        Assert.True(names.Flag);
        Assert.False(names.ReadBySimsExe);
    }

    [Fact]
    public void TrcnVersion0UsesTheDefaultRange()
    {
        var chunk = new ChunkBuilder().U32(0).S32(0).Tag("NCRT").S32(1).U32(1).U32(2).EvenString("standing").EvenString("");
        var (file, iff) = chunk.InFile("TRCN");
        Assert.Equal(new ConstantLabel(1, 2, "standing", "", false, 0, 100), ConstantLabels.Read(file, iff).Entries.Single());
    }

    [Fact]
    public void TrcnVersion1ReadsTheRange()
    {
        var chunk = new ChunkBuilder().U32(0).S32(1).Tag("NCRT").S32(1).U32(1).U32(0x4b2)
            .EvenString("Fine Amount").EvenString("").U8(1).S16(-5).S16(250);
        var (file, iff) = chunk.InFile("TRCN");
        var labels = ConstantLabels.Read(file, iff);
        Assert.Equal(new ConstantLabel(1, 0x4b2, "Fine Amount", "", true, -5, 250), labels.Entries.Single());
        Assert.True(labels.ReadBySimsExe);
    }

    [Fact]
    public void TrcnVersion2UsesTheOnlineLayout()
    {
        var chunk = new ChunkBuilder().U32(0).S32(2).Tag("NCRT").S32(1).U32(1).U32(0x4b2)
            .VarString("Floral").VarString("").U8(0).S16(0).S16(100);
        var (file, iff) = chunk.InFile("TRCN");
        var labels = ConstantLabels.Read(file, iff);
        Assert.Equal("Floral", labels.Entries.Single().Label);
        Assert.False(labels.ReadBySimsExe);
    }
}
