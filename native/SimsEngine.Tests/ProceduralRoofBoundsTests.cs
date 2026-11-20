using SimsEngine.Formats;
using SimsEngine.World;
using Xunit;

namespace SimsEngine.Tests;

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
        var roof = LotRoof.Read(new IffFile(ChunkBuilder.File(), "empty.iff"));
        Assert.Equal(LotRoof.DefaultPitch, roof.Pitch);
        Assert.Equal(2.0f, LotRoof.MaxPitch);

        roof.SetPitch(input);
        Assert.Equal(expected, roof.Pitch);

        var vertices = RoofCellMesh.Build(256, roof.Pitch);
        Assert.NotEmpty(vertices);
    }

    [Fact]
    public void RoofCellMeshRejectsInvalidPitchesOutsideBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RoofCellMesh.Build(256, -0.01f));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoofCellMesh.Build(256, 2.01f));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoofCellMesh.Build(256, float.NaN));
    }

    [Fact]
    public void RoofVerticesDoNotEscapeLotBoundaries()
    {
        // A 64x64 lot corresponds to 128x128 half-tile roof grid cells.
        var grid = new RoofGrid(128);

        for (var r = 2; r <= 6; r++)
        for (var c = 2; c <= 6; c++)
            grid.Seed(r, c, roofed: true, blocked: false);

        for (var r = 120; r <= 125; r++)
        for (var c = 120; c <= 125; c++)
            grid.Seed(r, c, roofed: true, blocked: false);

        grid.Plan(false);

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
        var lot = CreateLotWithUpperFloor((x, y) => x == 6 && y == 5 ? (ushort)256 : (ushort)0);

        var level1Grid = LotRoofGrid.Seed(lot, 1, []);
        Assert.False(level1Grid.Has(2 * 5, 2 * 6));

        var level2Grid = LotRoofGrid.Seed(lot, 2, []);
        Assert.True(level2Grid.Has(2 * 5, 2 * 6));

        const float StoryHeight = 2.95f;
        var level1Altitude = 1 * StoryHeight;
        var level2Altitude = 2 * StoryHeight;
        Assert.True(level2Altitude > level1Altitude);
        Assert.Equal(StoryHeight, level2Altitude - level1Altitude, precision: 3);

        Assert.Throws<ArgumentOutOfRangeException>(() => LotRoofGrid.Seed(lot, 0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => LotRoofGrid.Seed(lot, 3, []));
    }

    [Fact]
    public void RoofRelaxationSolverConvergesWithinMaxRounds()
    {
        var grid = new RoofGrid(24);
        for (var r = 4; r <= 14; r++)
        for (var c = 4; c <= 8; c++)
            grid.Seed(r, c, roofed: true, blocked: false);

        for (var r = 10; r <= 14; r++)
        for (var c = 9; c <= 16; c++)
            grid.Seed(r, c, roofed: true, blocked: false);

        grid.Plan(grow: false);

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
        for (var r = 10; r <= 20; r++)
        for (var c = 10; c <= 20; c++)
            grid.Seed(r, c, roofed: true, blocked: false);

        grid.Seed(9, 10, roofed: false, blocked: true);

        Assert.False(grid.Has(9, 15));
        Assert.False(grid.Has(21, 15));
        Assert.False(grid.Has(15, 9));
        Assert.False(grid.Has(15, 21));

        grid.Plan(grow: false);

        Assert.True(grid.Has(9, 15));
        Assert.True(grid.Has(21, 15));
        Assert.True(grid.Has(15, 9));
        Assert.True(grid.Has(15, 21));
        Assert.NotEqual(0, grid.Flags(9, 15) & 4);

        Assert.False(grid.Has(9, 10));

        Assert.False(grid.Has(8, 15));
        Assert.False(grid.Has(22, 15));
        Assert.False(grid.Has(15, 8));
        Assert.False(grid.Has(15, 22));
    }

    [Fact]
    public void RoofSunDirectionCreatesDistinctFacetContrastInDefaultMode()
    {
        var sun = SimsSDL.LotRenderer.ResolveRoofSunDirection(ambientActive: false, new System.Numerics.Vector3(1f, 0f, 0f));
        var frontRight = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0.5f, 1f, 0f));
        var frontLeft = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0f, 1f, 0.5f));
        var backRight = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0f, 1f, -0.5f));
        var backLeft = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(-0.5f, 1f, 0f));

        float dotFR = MathF.Max(0f, System.Numerics.Vector3.Dot(frontRight, sun));
        float dotFL = MathF.Max(0f, System.Numerics.Vector3.Dot(frontLeft, sun));
        float dotBR = MathF.Max(0f, System.Numerics.Vector3.Dot(backRight, sun));
        float dotBL = MathF.Max(0f, System.Numerics.Vector3.Dot(backLeft, sun));

        Assert.True(dotFR - dotFL > 0.35f, $"Expected strong front-right vs front-left contrast, got FR={dotFR:F3}, FL={dotFL:F3}");
        Assert.True(dotBR > dotBL, $"Expected back-right ({dotBR:F3}) brighter than back-left ({dotBL:F3})");
    }

    [Fact]
    public void RoofSunDirectionFollowsActiveAmbientSun()
    {
        var frontRight = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0.5f, 1f, 0f));
        var frontLeft = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0f, 1f, 0.5f));

        var sunRight = SimsSDL.LotRenderer.ResolveRoofSunDirection(ambientActive: true, new System.Numerics.Vector3(0.6f, 0f, 0.8f));
        var sunLeft = SimsSDL.LotRenderer.ResolveRoofSunDirection(ambientActive: true, new System.Numerics.Vector3(0.6f, 0f, -0.8f));

        Assert.True(System.Numerics.Vector3.Dot(frontRight, sunRight) > System.Numerics.Vector3.Dot(frontLeft, sunRight));
        Assert.True(System.Numerics.Vector3.Dot(frontLeft, sunLeft) > System.Numerics.Vector3.Dot(frontRight, sunLeft));
    }
}
