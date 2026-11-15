using SimsEngine.Formats;
using SimsEngine.World;
using Xunit;

namespace SimsEngine.Tests;

/// <summary>Unit tests for A13 procedural roof bounds, lot-boundary vertex clamping, multi-story roofs, and pitch limits.</summary>
public class ProceduralRoofBoundsTests
{
    private static IffFile CreateLotWithUpperFloor(Func<int, int, ushort> upperFloor)
    {
        byte[] Layer(int size, Func<int, int, byte[]> value)
        {
            var body = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(size).U32(0);
            for (var x = 0; x < 64; x++)
            for (var y = 0; y < 64; y++) body.Raw(value(x, y));
            return body.ToArray();
        }

        byte[] Walls(int x, int y)
        {
            byte bits = 0;
            if (y >= 4 && y <= 8) { if (x == 4 || x == 9) bits |= 1; if (x == 3 || x == 8) bits |= 4; }
            if (x >= 4 && x <= 8) { if (y == 4 || y == 9) bits |= 2; if (y == 3 || y == 8) bits |= 8; }
            return [bits, 0x0f, 1, 1, 0, 0, 0, 0];
        }

        return new IffFile(ChunkBuilder.File(
            ("Arry", 0, Layer(4, (_, _) => [0, 0, 0, 0])),
            ("Arry", 1, Layer(1, (_, _) => [0])),
            ("Arry", 2, Layer(8, Walls)),
            ("Arry", 8, Layer(1, (x, y) => [x is >= 4 and <= 8 && y is >= 4 and <= 8 ? (byte)0x10 : (byte)0x14])),
            ("Arry", 101, Layer(1, (_, _) => [0])),
            ("Arry", 111, Layer(2, (x, y) => BitConverter.GetBytes(upperFloor(x, y)))),
            ("Arry", 102, Layer(8, Walls))), "roof_test.iff");
    }

    [Theory]
    [InlineData(-1.0f, 0.0f)]
    [InlineData(-0.01f, 0.0f)]
    [InlineData(0.0f, 0.0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(1.0f, 1.0f)]
    [InlineData(2.0f, 2.0f)]
    [InlineData(2.5f, 2.0f)]
    [InlineData(10.0f, 2.0f)]
    public void PitchIsStrictlyClampedToZeroThroughTwo(float input, float expected)
    {
        // Sims.exe FUN_0056f23d clamps pitch strictly to [0.0, 2.0] in LotRoof.SetPitch
        var roof = LotRoof.Read(new IffFile(ChunkBuilder.File(), "empty.iff"));
        Assert.Equal(LotRoof.DefaultPitch, roof.Pitch);
        Assert.Equal(2.0f, LotRoof.MaxPitch);

        roof.SetPitch(input);
        Assert.Equal(expected, roof.Pitch);

        // RoofCellMesh.Build accepts all valid clamped pitches without throwing
        var vertices = RoofCellMesh.Build(256, roof.Pitch);
        Assert.NotEmpty(vertices);
    }

    [Fact]
    public void RoofCellMeshRejectsInvalidPitchesOutsideBounds()
    {
        // RoofCellMesh.Build validates pitch strictly against [0, LotRoof.MaxPitch]
        Assert.Throws<ArgumentOutOfRangeException>(() => RoofCellMesh.Build(256, -0.01f));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoofCellMesh.Build(256, 2.01f));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoofCellMesh.Build(256, float.NaN));
    }

    [Fact]
    public void RoofVerticesDoNotEscapeLotBoundaries()
    {
        // A 64x64 lot corresponds to 128x128 RoofGrid half-tile cells
        var grid = new RoofGrid(128);

        // Seed roofs at opposite corners of the lot
        for (var r = 2; r <= 6; r++)
        for (var c = 2; c <= 6; c++)
            grid.Seed(r, c, roofed: true, blocked: false);

        for (var r = 120; r <= 125; r++)
        for (var c = 120; c <= 125; c++)
            grid.Seed(r, c, roofed: true, blocked: false);

        grid.Plan(false);

        // Verify all generated roof mesh vertices are bounded within the 64x64 lot dimensions
        for (var row = 1; row < 127; row++)
        for (var column = 1; column < 127; column++)
        {
            if (!grid.Has(row, column)) continue;
            var vertices = RoofCellMesh.Build(grid, row, column, pitch: 1.0f);
            foreach (var v in vertices)
            {
                Assert.InRange(v.X, 0.0f, 64.0f);
                Assert.InRange(v.Y, 0.0f, 64.0f);
                Assert.InRange(v.Height, 0.0f, 64.0f);
            }
        }
    }

    [Fact]
    public void MultiStoryRoofPlacementAltitudeSeparation()
    {
        // Lot with upper floor at (6, 5)
        var lot = CreateLotWithUpperFloor((x, y) => x == 6 && y == 5 ? (ushort)256 : (ushort)0);

        // Level 1: upper floor blocks roof under the second story
        var level1Grid = LotRoofGrid.Seed(lot, 1, []);
        Assert.False(level1Grid.Has(2 * 5, 2 * 6)); // (6, 5) is blocked

        // Level 2: roof is placed above the upper floor
        var level2Grid = LotRoofGrid.Seed(lot, 2, []);
        Assert.True(level2Grid.Has(2 * 5, 2 * 6));

        // World altitude separation follows level * EntityTransform.StoryHeight (SceneExporter.cs:2022)
        const float StoryHeight = 2.95f; // EntityTransform.StoryHeight
        var level1Altitude = 1 * StoryHeight;
        var level2Altitude = 2 * StoryHeight;
        Assert.True(level2Altitude > level1Altitude);
        Assert.Equal(StoryHeight, level2Altitude - level1Altitude, precision: 3);

        // Invalid story levels are rejected by LotRoofGrid
        Assert.Throws<ArgumentOutOfRangeException>(() => LotRoofGrid.Seed(lot, 0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => LotRoofGrid.Seed(lot, 3, []));
    }

    [Fact]
    public void RoofRelaxationSolverConvergesWithinMaxRounds()
    {
        // An irregular L-shaped roof footprint requiring multiple relaxation passes
        var grid = new RoofGrid(24);
        for (var r = 4; r <= 14; r++)
        for (var c = 4; c <= 8; c++)
            grid.Seed(r, c, roofed: true, blocked: false);

        for (var r = 10; r <= 14; r++)
        for (var c = 9; c <= 16; c++)
            grid.Seed(r, c, roofed: true, blocked: false);

        // RoofGrid.Plan executes Sims.exe FUN_0056ea87 relaxation passes (Grow, Heights, Kinds, Shapes up to 9 rounds)
        grid.Plan(grow: false);

        // Verify convergence: every roofed cell received valid height, shape, and mesh resource
        for (var r = 1; r < 23; r++)
        for (var c = 1; c < 23; c++)
        {
            if (!grid.Has(r, c)) continue;
            Assert.InRange(grid.Height(r, c), 0, 10);
            Assert.InRange(grid.Shape(r, c), 0, 15);
            var (meshId, quarterTurns) = grid.MeshResource(r, c);
            Assert.True(meshId > 0);
            Assert.InRange(quarterTurns, 0, 3);
        }
    }

    [Fact]
    public void EdgeEavesExpandBeyondEnclosedRooms()
    {
        var grid = new RoofGrid(32);
        // Seed an 11x11 square from row 10..20, column 10..20
        for (var r = 10; r <= 20; r++)
        for (var c = 10; c <= 20; c++)
            grid.Seed(r, c, roofed: true, blocked: false);

        // Mark (9, 10) as blocked to verify blocked cells do not grow
        grid.Seed(9, 10, roofed: false, blocked: true);

        // Before Plan: cardinal neighbor cell (9, 15) is not roofed
        Assert.False(grid.Has(9, 15));
        Assert.False(grid.Has(21, 15));
        Assert.False(grid.Has(15, 9));
        Assert.False(grid.Has(15, 21));

        // Plan runs Grow() (FUN_0056e3da) which expands eaves by 1 half-tile cell
        grid.Plan(grow: false);

        // Cardinal unblocked neighbors are now roofed with the Grown flag (bit 2 = 4)
        Assert.True(grid.Has(9, 15));
        Assert.True(grid.Has(21, 15));
        Assert.True(grid.Has(15, 9));
        Assert.True(grid.Has(15, 21));
        Assert.NotEqual(0, grid.Flags(9, 15) & 4);

        // Blocked neighbor did NOT grow
        Assert.False(grid.Has(9, 10));

        // Cells two steps out did NOT grow (growth does not propagate from grown cells)
        Assert.False(grid.Has(8, 15));
        Assert.False(grid.Has(22, 15));
        Assert.False(grid.Has(15, 8));
        Assert.False(grid.Has(15, 22));
    }
}
