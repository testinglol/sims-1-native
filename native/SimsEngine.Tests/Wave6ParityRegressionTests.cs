using System.Text.Json;
using SimsEngine.Simulation;
using SimsSimulation;

namespace SimsEngine.Tests;

public sealed class Wave6ParityRegressionTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    [Fact]
    public void HandResaleValue_MatchesSimsExeGrabToolAndDisposePanelRounding()
    {
        Assert.Equal(127, SimulationWorld.HandResaleValue(127, 0));
        Assert.Equal(127, SimulationWorld.HandResaleValue(127, 7));
        Assert.Equal(0, SimulationWorld.HandResaleValue(127, 4));

        foreach (var buildType in new[] { 1, 2, 3, 5, 6 })
        {
            Assert.Equal(102, SimulationWorld.HandResaleValue(127, buildType));
            Assert.Equal(79, SimulationWorld.HandResaleValue(99, buildType));
        }
    }

    [Fact]
    public void IsInsideLotEdge_ExcludesOuterBorderTilesPerFun00433136()
    {
        var gameDir = LocateGameDirectory();
        var housePath = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(housePath))
            return;

        var bootstrap = WorldBootstrap.Load(gameDir, housePath!);
        var bella = Assert.IsType<XPerson>(bootstrap.World.ObjectById(18));
        Assert.True(SimulationWorld.IsInsideLotEdge(bella));

        var savedPos = bella.Position;
        try
        {
            bella.Position = new ObjectPosition(0, 32 << 4, 1);
            Assert.False(SimulationWorld.IsInsideLotEdge(bella));

            bella.Position = new ObjectPosition(63 << 4, 32 << 4, 1);
            Assert.False(SimulationWorld.IsInsideLotEdge(bella));

            bella.Position = new ObjectPosition(1 << 4, 62 << 4, 1);
            Assert.True(SimulationWorld.IsInsideLotEdge(bella));
        }
        finally
        {
            bella.Position = savedPos;
        }
    }

    [Fact]
    public void EvictAndWriteSavedSimulationState_PreserveVersion1EightCategoryBudgetDaysPerFun0046d38c()
    {
        var gameDir = LocateGameDirectory();
        var housePath = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        var nghPath = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Neighborhood.iff");
        if (gameDir is null || !File.Exists(housePath) || !File.Exists(nghPath))
            return;

        var tempProfile = Path.Combine(Path.GetTempPath(), $"sims-wave6-evict-{Guid.NewGuid():N}");
        try
        {
            var userPath = Path.Combine(tempProfile, "UserData");
            Directory.CreateDirectory(Path.Combine(userPath, "Houses"));
            Directory.CreateDirectory(Path.Combine(userPath, "Characters"));
            File.Copy(nghPath!, Path.Combine(userPath, "Neighborhood.iff"));
            File.Copy(housePath!, Path.Combine(userPath, "Houses", "House05.iff"));

            var lotZoningPath = Path.Combine(gameDir, "UserData", "LotZoning.iff");
            if (File.Exists(lotZoningPath))
                File.Copy(lotZoningPath, Path.Combine(userPath, "LotZoning.iff"));

            _ = NeighborhoodEdits.Evict(userPath, gameDir, 5);

            var evictedHouse = SimsEngine.Formats.IffFile.Open(Path.Combine(userPath, "Houses", "House05.iff"));
            var simiChunk = evictedHouse.Find(SimsEngine.Formats.ResourceType.Parse("SIMI"), 1);
            Assert.NotNull(simiChunk);
            var simi = SimsEngine.Formats.SimulationState.Read(evictedHouse, simiChunk!);
            Assert.Equal(6, simi.BudgetDays.Count);
            Assert.All(simi.BudgetDays, day =>
            {
                Assert.True(day.Valid);
                Assert.Equal(8, day.Values.Count);
                Assert.All(day.Values, v => Assert.Equal(0, v));
            });

            var unhoused = NeighborhoodExport.UnhousedFamilies(userPath, gameDir);
            var goth = Assert.Single(unhoused, f => f.Name == "Goth");
            Assert.Equal(3, goth.Members.Length);
            Assert.Equal(3, goth.MemberThumbnails.Length);

            var registry = SimsEngine.Content.ObjectRegistry.Scan(gameDir);
            var library = SimsEngine.Content.GlobalLibrary.Open(gameDir);
            var world = SavedLotObjectLoader.Load(evictedHouse, registry, library);
            world.ReplaceSavedBudgetDays(Enumerable.Repeat(new SimsEngine.Formats.BudgetDay(false, new int[7]), 6).ToArray());
            var legacyLot = world.WriteSavedSimulationStateTo(evictedHouse);
            world.ReplaceSavedBudgetDays(Enumerable.Repeat(new SimsEngine.Formats.BudgetDay(true, new int[8]), 6).ToArray());
            var upgradedLot = world.WriteSavedSimulationStateTo(legacyLot);
            var upgradedSimi = SimsEngine.Formats.SimulationState.Read(
                upgradedLot,
                upgradedLot.Find(SimsEngine.Formats.ResourceType.Parse("SIMI"), 1)!);
            Assert.All(upgradedSimi.BudgetDays, day => Assert.Equal(8, day.Values.Count));
        }
        finally
        {
            if (Directory.Exists(tempProfile))
                Directory.Delete(tempProfile, recursive: true);
        }
    }
}
