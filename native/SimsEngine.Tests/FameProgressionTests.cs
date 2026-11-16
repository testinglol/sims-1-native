using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public class FameProgressionTests
{
    [Fact]
    public void DefaultFamePromotionChecksHas11StarThresholds()
    {
        var checks = new FamePromotionChecks();
        Assert.Equal(11, checks.FameStarThresholds.Count);
        Assert.Equal(11, checks.FameFriendThresholds.Count);
        Assert.Equal(6, checks.FameSkillThresholds.Count);
        Assert.All(checks.FameSkillThresholds, s => Assert.Equal(11, s.Count));
    }

    [Fact]
    public void FullLadderProgressionFrom0To10Stars()
    {
        var world = new SimulationWorld();
        var target = new FamePromotionChecks();
        world.GenericSimsCallRecordCallbackTarget = target;

        var sim = CreateSimWithRecord(world, id: 101, recordIndex: 0);

        sim.PersonData[11] = 1000;
        sim.PersonData[17] = 1000;
        sim.PersonData[15] = 1000;

        AddMutualFamousFriends(world, subjectId: 101, totalStarPower: 50);

        for (short step = 0; step < 10; step++)
        {
            Assert.Equal(step, sim[PersonData.FameStarPower09]);
            Assert.Equal(step, sim[PersonData.FameStarHighWatermark]);

            sim[PersonData.FameScore] = (short)target.FameStarThresholds[step];

            var promoted = world.EvaluateFamePromotion(101);
            Assert.True(promoted, $"Promotion at step {step} failed");
            Assert.Equal((short)(step + 1), sim[PersonData.FameStarPower09]);
            Assert.Equal((short)(step + 1), sim[PersonData.FameStarHighWatermark]);
        }

        Assert.Equal((short)10, sim[PersonData.FameStarPower09]);
        var maxPromoted = world.EvaluateFamePromotion(101);
        Assert.False(maxPromoted);
        Assert.Equal((short)10, sim[PersonData.FameStarPower09]);
    }

    [Fact]
    public void FriendGateBlocksPromotionWhenInsufficient()
    {
        var world = new SimulationWorld();
        var target = new FamePromotionChecks();
        world.GenericSimsCallRecordCallbackTarget = target;

        var sim = CreateSimWithRecord(world, id: 102, recordIndex: 0);
        sim[PersonData.FameStarPower09] = 4;
        sim[PersonData.FameStarHighWatermark] = 4;
        sim[PersonData.FameScore] = (short)target.FameStarThresholds[4];
        sim.PersonData[11] = 1000;
        sim.PersonData[17] = 1000;
        sim.PersonData[15] = 1000;

        var promotedNoFriends = world.EvaluateFamePromotion(102);
        Assert.False(promotedNoFriends);
        Assert.Equal((short)4, sim[PersonData.FameStarPower09]);

        AddMutualFamousFriends(world, subjectId: 102, totalStarPower: 2);
        var promotedInsufficient = world.EvaluateFamePromotion(102);
        Assert.False(promotedInsufficient);

        AddMutualFamousFriends(world, subjectId: 102, totalStarPower: 4);
        var promotedSuccess = world.EvaluateFamePromotion(102);
        Assert.True(promotedSuccess);
        Assert.Equal((short)5, sim[PersonData.FameStarPower09]);
    }

    [Fact]
    public void SkillGatesBlockPromotionWhenInsufficient()
    {
        var world = new SimulationWorld();
        var target = new FamePromotionChecks();
        world.GenericSimsCallRecordCallbackTarget = target;

        var sim = CreateSimWithRecord(world, id: 103, recordIndex: 0);
        sim[PersonData.FameStarPower09] = 1;
        sim[PersonData.FameScore] = (short)target.FameStarThresholds[1];

        sim.PersonData[11] = 0;
        sim.PersonData[17] = 1000;
        sim.PersonData[15] = 1000;

        var promotedNoCharisma = world.EvaluateFamePromotion(103);
        Assert.False(promotedNoCharisma);

        sim.PersonData[11] = 200;
        var promotedWithCharisma = world.EvaluateFamePromotion(103);
        Assert.True(promotedWithCharisma);
        Assert.Equal((short)2, sim[PersonData.FameStarPower09]);
    }

    [Fact]
    public void DemotionDecrementsStarPowerAndPreservesWatermark()
    {
        var world = new SimulationWorld();
        var target = new FamePromotionChecks();
        world.GenericSimsCallRecordCallbackTarget = target;

        var sim = CreateSimWithRecord(world, id: 104, recordIndex: 0);
        sim[PersonData.FameStarPower09] = 3;
        sim[PersonData.FameStarHighWatermark] = 5;
        sim[PersonData.FameScore] = 5;

        var demoted = world.TryDecrementFameStarPower(104);
        Assert.True(demoted);
        Assert.Equal((short)2, sim[PersonData.FameStarPower09]);
        Assert.Equal((short)5, sim[PersonData.FameStarHighWatermark]);

        sim[PersonData.FameScore] = 1;
        var demoted2 = world.TryDecrementFameStarPower(104);
        Assert.True(demoted2);
        Assert.Equal((short)1, sim[PersonData.FameStarPower09]);
        Assert.Equal((short)5, sim[PersonData.FameStarHighWatermark]);

        sim[PersonData.FameScore] = 0;
        var demoted3 = world.TryDecrementFameStarPower(104);
        Assert.True(demoted3);
        Assert.Equal((short)0, sim[PersonData.FameStarPower09]);
        Assert.Equal((short)5, sim[PersonData.FameStarHighWatermark]);

        var demotedBelowZero = world.TryDecrementFameStarPower(104);
        Assert.False(demotedBelowZero);
        Assert.Equal((short)0, sim[PersonData.FameStarPower09]);
        Assert.Equal((short)5, sim[PersonData.FameStarHighWatermark]);
    }

    [Fact]
    public void OnStarPowerChangeRecordsUiMessageAndMarksDirty()
    {
        var world = new SimulationWorld();
        Assert.False(world.PersistentPersonDataDirty);

        world.OnStarPowerChange(105, oldStars: 0, newStars: 2);

        Assert.True(world.PersistentPersonDataDirty);
        Assert.Contains(world.UiMessages, m => m.Message == 0x110 && m.Text.Contains("Sim 105 fame star changed: 0.0* -> 1.0*"));
    }

    private static XPerson CreateSimWithRecord(SimulationWorld world, short id, int recordIndex)
    {
        var sim = new XPerson(id, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(id * 10)
        };
        sim[PersonData.NeighborId] = id;
        world.Add(sim);
        while (world.PersonRecordKeys.Count <= recordIndex) world.PersonRecordKeys.Add(0);
        while (world.PersonRecordNeighborIds.Count <= recordIndex) world.PersonRecordNeighborIds.Add(null);
        world.PersonRecordKeys[recordIndex] = id * 10;
        world.PersonRecordNeighborIds[recordIndex] = id;

        var neighbor = new NeighborState(id, []);
        neighbor.TypeRecordKey = id * 10;
        neighbor.PersonData[(int)PersonData.NeighborId] = id;
        world.Neighbors[id] = neighbor;
        world.NeighborWalkOrder.Add(neighbor);
        return sim;
    }

    private static void AddMutualFamousFriends(SimulationWorld world, short subjectId, int totalStarPower)
    {
        var friendId = (short)(subjectId + 100);
        var friend = new NeighborState(friendId, [])
        {
            FriendEligibilityObjectFlagsB4 = 4
        };
        friend.PersonData[(int)PersonData.NeighborId] = friendId;
        friend.PersonData[61] = friendId;
        friend.PersonData[81] = (short)totalStarPower;

        var subjectNeighbor = world.Neighbors[subjectId];
        subjectNeighbor.PersonData[61] = subjectId;

        friend.Relationships.Resize(subjectId, 1);
        subjectNeighbor.Relationships.Resize(friendId, 1);

        // Mutual friendship requires a daily relationship score of at least 50 in both directions.
        friend.Relationships.Set(subjectId, 0, 75);
        subjectNeighbor.Relationships.Set(friendId, 0, 75);

        world.Neighbors[friendId] = friend;
        world.NeighborWalkOrder.Add(friend);
    }
}
