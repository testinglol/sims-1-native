using SimsEngine.World;
using Xunit;

namespace SimsEngine.Tests;

public sealed class RoofGridTests
{
    private static RoofGrid Square(int size, int from, int to)
    {
        var grid = new RoofGrid(size);
        for (var row = from; row <= to; row++)
        for (var column = from; column <= to; column++) grid.Seed(row, column, true, false);
        return grid;
    }

    [Fact]
    public void HeightIsTheNumberOfFullyRoofedRingsAroundACell()
    {
        var grid = Square(16, 4, 10);
        grid.Plan(false);
        Assert.Equal(4, grid.Height(7, 7));
        Assert.Equal(1, grid.Height(4, 4));
        Assert.Equal(2, grid.Height(5, 5));
        Assert.Equal(3, grid.Height(6, 6));
    }

    [Fact]
    public void AnEmptyLotDoesNotGrowARoof()
    {
        var grid = new RoofGrid(16);
        grid.Plan(false);
        Assert.False(grid.Has(5, 5), "growth requires an original roof neighbour");
        Assert.False(grid.Has(0, 0), "the border row is never planned");
    }

    [Fact]
    public void GrowthAddsOnlyOneRingAndDoesNotPropagateFromGrownCells()
    {
        var grid = Square(16, 4, 10);
        grid.Plan(false);
        Assert.True(grid.Has(3, 7));
        Assert.False(grid.Has(2, 7));
        Assert.True(grid.Has(11, 7));
        Assert.False(grid.Has(12, 7));
    }

    [Fact]
    public void ABlockedCellStaysOpen()
    {
        var grid = Square(16, 4, 10);
        grid.Seed(3, 8, false, true);
        grid.Plan(false);
        Assert.False(grid.Has(3, 8));
    }

    [Fact]
    public void TheApexOfASquareRoofHasNoEqualHeightNeighboursAndUsesResource0x100()
    {
        var grid = Square(16, 4, 10);
        grid.Plan(false);
        Assert.Equal(0, grid.Shape(7, 7));
        Assert.Equal((0x100, 0), grid.MeshResource(7, 7));
    }
}
