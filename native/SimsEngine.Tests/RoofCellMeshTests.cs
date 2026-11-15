using SimsEngine.World;

namespace SimsEngine.Tests;

public sealed class RoofCellMeshTests
{
    [Theory]
    [InlineData(256, 4)]
    [InlineData(264, 5)]
    [InlineData(265, 4)]
    [InlineData(266, 6)]
    [InlineData(270, 6)]
    [InlineData(271, 8)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(9, 2)]
    [InlineData(13, 2)]
    public void ResourceBranchesHaveSourceTriangleCountsAndPitchOnlyChangesHeight(int resource, int triangles)
    {
        var flat = RoofCellMesh.Build(resource, 0);
        var sloped = RoofCellMesh.Build(resource, 2);
        Assert.Equal(triangles * 3, sloped.Count);
        Assert.All(flat, vertex => Assert.Equal(0, vertex.Height));
        Assert.Equal(resource < 256 ? 1 : 0.5f, sloped.Max(vertex => vertex.Height));
        Assert.Equal(flat, sloped.Select(vertex => vertex with { Height = 0 }));
        Assert.All(sloped, vertex =>
        {
            Assert.InRange(vertex.X, 0, 0.5f);
            Assert.InRange(vertex.Y, 0, 0.5f);
            Assert.InRange(vertex.U, 0, 1);
            Assert.InRange(vertex.V, 0, 1);
        });
    }

    [Fact]
    public void ApexResourceHasFourFacesMeetingAtTheSourceCentreAndSeparateTextureSeams()
    {
        var vertices = RoofCellMesh.Build(256, 1);
        Assert.Equal(4, vertices.Count(vertex => vertex == new RoofMeshVertex(0.25f, 0.25f, 0.25f, 0.5f, 0.5f)));
        Assert.Contains(new RoofMeshVertex(0, 0, 0, 1, 1), vertices);
        Assert.Contains(new RoofMeshVertex(0, 0, 0, 0, 1), vertices);
    }

    [Fact]
    public void SecondKindThreeIsTheSourcePlaneWithTwoHighCorners()
    {
        Assert.Equal(new RoofMeshVertex[]
        {
            new(0, 0, 0, 1, 1), new(0.5f, 0.5f, 0.5f, 0, 0), new(0.5f, 0.5f, 0, 1, 0),
            new(0, 0, 0, 1, 1), new(0, 0, 0.5f, 0, 1), new(0.5f, 0.5f, 0.5f, 0, 0),
        }, RoofCellMesh.Build(3, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AdjacentPlannedCellsMeetAtTheSameHeightOnTheirSharedEdges(int footprint)
    {
        var grid = new RoofGrid(16);
        for (var row = 4; row <= 10; row++)
        for (var column = 4; column <= 10; column++)
        {
            var roofed = footprint switch
            {
                1 => column < 10,
                2 => row <= 7 || column <= 7,
                3 => row != 7 || column != 7,
                _ => true,
            };
            grid.Seed(row, column, roofed, !roofed);
        }
        grid.Plan(false);
        for (var row = 2; row < 13; row++)
        for (var column = 2; column < 13; column++)
        {
            var cell = RoofCellMesh.Build(grid, row, column, 1);
            for (var i = 0; i < cell.Count; i += 3)
            {
                var a = cell[i]; var b = cell[i + 1]; var c = cell[i + 2];
                var upwardArea = (b.Y - a.Y) * (c.X - a.X) - (b.X - a.X) * (c.Y - a.Y);
                Assert.True(upwardArea >= 0, "Axis conversion must preserve upward-facing winding");
            }
            foreach (var (dy, dx) in new[] { (0, 1), (1, 0) })
            {
                var neighbour = RoofCellMesh.Build(grid, row + dy, column + dx, 1);
                if (cell.Count == 0 || neighbour.Count == 0) continue;
                var edge = (dx == 1 ? column + 1 : row + 1) * 0.5f;
                var first = cell.Where(vertex => (dx == 1 ? vertex.X : vertex.Y) == edge)
                    .Select(vertex => (Along: dx == 1 ? vertex.Y : vertex.X, vertex.Height)).Distinct().Order().ToArray();
                var second = neighbour.Where(vertex => (dx == 1 ? vertex.X : vertex.Y) == edge)
                    .Select(vertex => (Along: dx == 1 ? vertex.Y : vertex.X, vertex.Height)).Distinct().Order().ToArray();
                Assert.Equal(first, second);
            }
        }
    }

    [Fact]
    public void MissingResourceAndInvalidInputsDoNotInventGeometry()
    {
        Assert.Empty(RoofCellMesh.Build(255, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoofCellMesh.Build(257, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoofCellMesh.Build(256, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoofCellMesh.Build(256, -1));
    }
}
