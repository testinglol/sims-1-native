using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class LotGeometryTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 0)]
    [InlineData(-1, 3)]
    [InlineData(-4, 0)]
    public void NormalizeOrientationMatchesTheOriginalTwoBitMask(int input, int expected)
    {
        Assert.Equal(expected, LotGeometry.NormalizeOrientation(input));
    }

    [Theory]
    [InlineData(0, 64, 0, 63, 0, 63)]
    [InlineData(1, 64, 0, 63, 0, 63)]
    [InlineData(2, 64, 0, 63, 0, 63)]
    [InlineData(3, 64, 0, 63, 0, 63)]
    [InlineData(1, 32, 32, 63, 0, 31)]
    [InlineData(3, 32, 0, 31, 32, 63)]
    public void TransformLotBoundsMatchesTheExtremaOfTransformedCorners(int orientation, int dimension, int minX, int maxX, int minY, int maxY)
    {
        Assert.Equal(new LotCoordinateBounds(minX, maxX, minY, maxY), LotGeometry.TransformLotBounds(dimension, orientation));
    }

    [Fact]
    public void SimulationWorldOrientationUpdateStoresTheMaskedValueAndRecomputedBounds()
    {
        var world = new SimulationWorld();

        world.SetLotOrientation(-1);

        Assert.Equal(3, world.LotTransformParameter);
        Assert.Equal(new LotCoordinateBounds(0, 63, 0, 63), world.LotBounds);
    }

    [Theory]
    [InlineData(0, 20, 35, 20, 35)]
    [InlineData(1, 20, 35, 988, 20)]
    [InlineData(2, 20, 35, 1003, 988)]
    [InlineData(3, 20, 35, 35, 1003)]
    public void TransformSearchCenterMatchesTheOriginalLotOrientationTransform(
        int orientation, int x, int y, int expectedX, int expectedY)
    {
        var transformed = LotGeometry.TransformSearchCenter(new ObjectPosition(x, y, 1), orientation);
        Assert.Equal(new ObjectPosition(expectedX, expectedY, 1), transformed);
    }

    [Theory]
    [InlineData(1, 2, 3, 60, 2)]
    [InlineData(2, 2, 3, 61, 60)]
    [InlineData(3, 2, 3, 3, 61)]
    [InlineData(1, 0, 0, 63, 0)]
    [InlineData(2, 63, 63, 0, 0)]
    [InlineData(3, 0, 63, 63, 63)]
    public void TryTransformTileMatchesTheOriginal64By64Tables(int orientation, sbyte x, sbyte y, sbyte expectedX, sbyte expectedY)
    {
        Assert.True(LotGeometry.TryTransformTile(x, y, orientation, out var transformedX, out var transformedY));
        Assert.Equal(expectedX, transformedX);
        Assert.Equal(expectedY, transformedY);
    }

    [Theory]
    [InlineData(-1, 0, 1)]
    [InlineData(64, 0, 1)]
    [InlineData(0, 64, 1)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 0, 4)]
    public void TryTransformTileLeavesUnsupportedInputsForTheCaller(int x, int y, int orientation)
    {
        Assert.False(LotGeometry.TryTransformTile((sbyte)x, (sbyte)y, orientation, out var transformedX, out var transformedY));
        Assert.Equal((sbyte)x, transformedX);
        Assert.Equal((sbyte)y, transformedY);
    }

    [Theory]
    [InlineData(0, 0, -16)]
    [InlineData(1, 16, -16)]
    [InlineData(2, 16, 0)]
    [InlineData(3, 16, 16)]
    [InlineData(4, 0, 16)]
    [InlineData(5, -16, 16)]
    [InlineData(6, -16, 0)]
    [InlineData(7, -16, -16)]
    [InlineData(8, 0, -16)]
    [InlineData(-1, -16, -16)]
    public void FootprintOffset_MasksToTheOriginalEightDirectionTable(short direction, int x, int y)
    {
        Assert.Equal((x, y), LotGeometry.FootprintOffset(direction));
    }

    [Theory]
    [InlineData(0, 0.5f, 0, 101, 100)]
    [InlineData(0, -0.5f, 0, 99, 100)]
    [InlineData(4, 0.5f, 0, 99, 100)]
    [InlineData(2, 0.5f, 0, 100, 101)]
    public void SlotProjectionRoundsHalfUnitsAwayFromZero(
        short direction, float slotX, float slotY, int expectedX, int expectedY)
    {
        var result = LotGeometry.SlotPosition(new ObjectPosition(100, 100, 1), direction, slotX, slotY);

        Assert.Equal(new ObjectPosition(expectedX, expectedY, 1), result);
    }

    [Theory]
    [InlineData(0, 16, 0)]
    [InlineData(1, 11, 11)]
    [InlineData(2, 0, 16)]
    [InlineData(3, -11, 11)]
    [InlineData(4, -16, 0)]
    [InlineData(5, -11, -11)]
    [InlineData(6, 0, -16)]
    [InlineData(7, 11, -11)]
    public void SlotProjectionMatchesTheSourceAxisAndSignForEveryAnchorDirection(
        short direction, int expectedXOffset, int expectedYOffset)
    {
        var result = LotGeometry.SlotPosition(new ObjectPosition(100, 100, 1), direction, 16, 0);

        Assert.Equal(new ObjectPosition(100 + expectedXOffset, 100 + expectedYOffset, 1), result);
    }
}
