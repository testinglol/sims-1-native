using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSimulation;

namespace SimsEngine.Tests;

public sealed class WallpaperToolTests
{
    private sealed class Budget(int funds) : IFamilyBudget
    {
        public int Funds { get; private set; } = funds;
        public int ReadFamilyBudget() => Funds;
        public void AlterFamilyBudget(int expenseType, int amount) => Funds -= amount;
    }

    private static (IffFile Lot, WallpaperCatalog Catalog) Setup(byte diagonalStyle)
    {
        var walls = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(8).U32(0);
        for (var x = 0; x < 64; x++)
        for (var y = 0; y < 64; y++)
            walls.Raw(x == 10 && y == 10 ? [0x10, 0, 0, diagonalStyle, 0, 0, 0, 0] : new byte[8]);
        var lot = new IffFile(ChunkBuilder.File(("Arry", 2, walls.ToArray())), "lot.iff");
        var strings = new ChunkBuilder().S16(-1).S16(3).CString("10").CString("one").CString("one pattern");
        var build = new IffFile(ChunkBuilder.File(("STR#", (ushort)0x83, strings.ToArray())), "Build.iff");
        return (lot, WallpaperCatalog.Read(build));
    }

    [Fact]
    public void FillStopsWithFenceErrorAtADiagonalInTheRoom()
    {
        var (lot, catalog) = Setup(2);
        var tool = new WallpaperTool(LotWalls.Read(lot, 1), catalog, new Budget(1000));
        var changed = tool.Fill(RoomMap.Build(lot, 1, 64, 64), 5, 5, 1, erase: false);
        Assert.Equal(0, changed);
        Assert.Equal(WallpaperTool.FenceError, tool.Error);
    }

    [Fact]
    public void FillWithoutADiagonalHasNoError()
    {
        var (lot, catalog) = Setup(2);
        var walls = LotWalls.Read(lot, 1);
        walls[10, 10][0] = 0;
        var tool = new WallpaperTool(walls, catalog, new Budget(1000));
        tool.Fill(RoomMap.Build(lot, 1, 64, 64), 5, 5, 1, erase: false);
        Assert.Equal(0, tool.Error);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(15, true)]
    [InlineData(22, true)]
    [InlineData(23, true)]
    [InlineData(24, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(12, false)]
    [InlineData(13, false)]
    [InlineData(14, false)]
    public void CanChangeStyleAllowsOpeningStylesAndRejectsFences(byte style, bool expected)
    {
        Assert.Equal(expected, WallpaperTool.CanChangeStyle(style));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    public void DotAndFillPaintAndEraseWallWithDoorOrWindowCutoutStyle(byte openingStyle)
    {
        var (lot, catalog) = Setup(1);
        var walls = LotWalls.Read(lot, 1);
        walls[10, 10][0] = 1;
        walls[10, 10][2] = openingStyle;
        walls[9, 10][0] = 4;
        var budget = new Budget(1000);
        var tool = new WallpaperTool(walls, catalog, budget);

        Assert.True(tool.Dot(10, 10, 1, 0, 1, erase: false));
        Assert.Equal(0, tool.Error);
        Assert.Equal(1, walls[10, 10][4]);
        Assert.Equal(10, tool.Commit());

        Assert.True(tool.Dot(10, 10, 1, 0, 0, erase: true));
        Assert.Equal(0, walls[10, 10][4]);
        Assert.Equal(-8, tool.Commit());

        var updatedLot = walls.WriteTo(lot);
        var filled = tool.Fill(RoomMap.Build(updatedLot, 1, 64, 64), 5, 5, 1, erase: false);
        Assert.Equal(2, filled);
        Assert.Equal(1, walls[10, 10][4]);
        Assert.Equal(1, walls[9, 10][7]);
    }

    [Theory]
    [InlineData(0x10, 0, 7, 4)]
    [InlineData(0x10, 1, 6, 2)]
    [InlineData(0x10, 2, 6, 2)]
    [InlineData(0x10, 3, 7, 4)]
    [InlineData(0x20, 0, 7, 3)]
    [InlineData(0x20, 1, 7, 3)]
    [InlineData(0x20, 2, 6, 1)]
    [InlineData(0x20, 3, 6, 1)]
    public void DiagonalFacingSlotAndSideMatchesCameraFacingHalfAndWallpaperToolSlot(
        int sourceDiag, int orientation, int expectedSlot, int expectedSide)
    {
        var (slot, side) = SceneExporter.DiagonalFacingSlotAndSide(sourceDiag, orientation);
        Assert.Equal(expectedSlot, slot);
        Assert.Equal(expectedSide, side);

        // Verify WallpaperTool.Dot writes to the exact same slot for that (edge, side) pair.
        var (lot, catalog) = Setup(1);
        var walls = LotWalls.Read(lot, 1);
        walls[10, 10][0] = (byte)sourceDiag;
        var tool = new WallpaperTool(walls, catalog, new Budget(1000));
        Assert.True(tool.Dot(10, 10, sourceDiag, side, 1, erase: false));
        Assert.Equal(1, walls[10, 10][slot]);
        Assert.Equal(0, walls[10, 10][slot == 6 ? 7 : 6]);

        // For orientations where this diagonal is broadside to the camera (cameraDiag == 0x10),
        // confirm the selected half-tile has the larger camera depth (u + v), i.e. faces the camera.
        var cameraDiag = orientation % 2 == 0 ? sourceDiag : sourceDiag ^ 0x30;
        if (cameraDiag == 0x10)
        {
            var camera = new LotCamera(orientation, LotWalls.Size, LotWalls.Size);
            var (slot6X, slot6Y, slot7X, slot7Y) = sourceDiag == 0x10
                ? (10.25f, 10.25f, 10.75f, 10.75f)
                : (10.25f, 10.75f, 10.75f, 10.25f);
            var depth6 = camera.DepthOrder(slot6X, slot6Y);
            var depth7 = camera.DepthOrder(slot7X, slot7Y);
            Assert.Equal(depth6 > depth7 ? 6 : 7, slot);
        }
    }
}
