using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;

namespace SimsEngine.Tests;

public sealed class TerrainToolTests
{
    private sealed class Budget(int funds) : IFamilyBudget
    {
        public int Funds { get; private set; } = funds;
        public int ReadFamilyBudget() => Funds;
        public void AlterFamilyBudget(int expenseType, int amount) => Funds -= amount;
    }

    private static LotTerrain FlatTerrain(byte height = 0)
    {
        var data = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(4).U32(0);
        for (var index = 0; index < 64 * 64; index++) data.Raw([height, height, height, height]);
        return LotTerrain.Read(new IffFile(ChunkBuilder.File(("Arry", 0, data.ToArray())), "lot.iff"));
    }

    [Fact]
    public void RaisingAVertexChargesPerUnitAndKeepsTheTilesCornersInAgreement()
    {
        var terrain = FlatTerrain();
        var tool = new TerrainTool(new SimulationWorld(), terrain, new Budget(1000), 2);
        Assert.Equal(1, tool.Raise(10, 10, 3));
        Assert.Equal(6, tool.Pending);
        Assert.Equal(3, terrain.Height(10, 10));
        Assert.Equal(3, terrain.Corners(9, 10)[2]);
        Assert.Equal(3, terrain.Corners(10, 9)[0]);
        Assert.Equal(3, terrain.Corners(9, 9)[3]);
    }

    [Fact]
    public void ASteepChangePullsNeighboursToWithinFiveAndChargesTheExtra()
    {
        var terrain = FlatTerrain();
        var tool = new TerrainTool(new SimulationWorld(), terrain, new Budget(1000), 1);
        Assert.Equal(5, tool.Raise(10, 10, 10));
        Assert.Equal(5, terrain.Height(9, 10));
        Assert.Equal(5, terrain.Height(11, 10));
        Assert.Equal(5, terrain.Height(10, 9));
        Assert.Equal(5, terrain.Height(10, 11));
        Assert.Equal(10 + 4 * 5, tool.Pending);
    }

    [Fact]
    public void HeightsAreClampedToTheMaximum()
    {
        var terrain = FlatTerrain(0x3d);
        var tool = new TerrainTool(new SimulationWorld(), terrain, new Budget(1000), 1);
        tool.Raise(10, 10, 200);
        Assert.Equal(TerrainTool.MaximumHeight, terrain.Height(10, 10));

        var lowTerrain = FlatTerrain(3);
        var lowTool = new TerrainTool(new SimulationWorld(), lowTerrain, new Budget(1000), 1);
        lowTool.Raise(10, 10, -50);
        Assert.Equal(0, lowTerrain.Height(10, 10));
    }

    [Fact]
    public void AnUnaffordableEditChangesNothingAndSetsInsufficientFunds()
    {
        var terrain = FlatTerrain();
        var tool = new TerrainTool(new SimulationWorld(), terrain, new Budget(5), 2);
        Assert.Equal(0, tool.Raise(10, 10, 3));
        Assert.True(tool.InsufficientFunds);
        Assert.Equal(0, terrain.Height(10, 10));
        Assert.Equal(0, tool.Pending);
    }

    [Fact]
    public void VerticesOutsideTheEditableBorderAreRefused()
    {
        var terrain = FlatTerrain();
        var tool = new TerrainTool(new SimulationWorld(), terrain, new Budget(1000), 1);
        Assert.Equal(0, tool.Raise(1, 10, 3));
        Assert.True(tool.Refused);
        Assert.Equal(0, terrain.Height(1, 10));
    }

    [Fact]
    public void LevelBringsTheRectangleToTheHeightAndCommitChargesIt()
    {
        var terrain = FlatTerrain();
        var budget = new Budget(1000);
        var tool = new TerrainTool(new SimulationWorld(), terrain, budget, 1);
        Assert.Equal(4, tool.Level(10, 10, 11, 11, 4));
        Assert.All(new[] { (10, 10), (11, 10), (10, 11), (11, 11) }, vertex => Assert.Equal(4, terrain.Height(vertex.Item1, vertex.Item2)));
        Assert.Equal(16, tool.Commit());
        Assert.Equal(984, budget.Funds);
        Assert.Equal(0, tool.Pending);
    }
}
