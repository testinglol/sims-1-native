using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class SimulationClockControlTests
{
    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(1, 0, 1, false)]
    [InlineData(0, 1, 0, false)]
    [InlineData(1, 1, 0, false)]
    [InlineData(1, 0, 0, true)]
    [InlineData(0, 0, 1, true)]
    [InlineData(0, 1, 1, false)]
    public void GlobalHoldMatchesOriginalThreeFieldPredicate(short word24, short word32, short word36, bool expected)
    {
        var control = new SimulationClockControl(word24, word32, word36);

        Assert.Equal(expected, control.GlobalTimeHold);
    }
}
