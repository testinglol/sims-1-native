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

        // Max out skills: Charisma (11), Body (17), Creativity (15) (scaled by 100)
        sim.PersonData[11] = 1000;
        sim.PersonData[17] = 1000;
        sim.PersonData[15] = 1000;

        // Provide enough mutual friend star power to pass all friend gates
        AddMutualFamousFriends(world, subjectId: 101, totalStarPower: 50);

        // Progress step by step through all 10 promotion steps (0.0 to 5.0 stars)
        for (short step = 0; step < 10; step++)
        {
            Assert.Equal(step, sim[PersonData.FameStarPower09]);
            Assert.Equal(step, sim[PersonData.FameStarHighWatermark]);

            // Set score to required threshold for step
            sim[PersonData.FameScore] = (short)target.FameStarThresholds[step];

            var promoted = world.EvaluateFamePromotion(101);
            Assert.True(promoted, $"Promotion at step {step} failed");
            Assert.Equal((short)(step + 1), sim[PersonData.FameStarPower09]);
            Assert.Equal((short)(step + 1), sim[PersonData.FameStarHighWatermark]);
        }

        // At star 10 (5.0 stars), further promotion is clamped
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
        sim[PersonData.FameStarPower09] = 4; // 2.0 stars -> advancing to 2.5 stars requires friend threshold index 4 = 2
        sim[PersonData.FameStarHighWatermark] = 4;
        sim[PersonData.FameScore] = (short)target.FameStarThresholds[4]; // 52
        sim.PersonData[11] = 1000; // Charisma
        sim.PersonData[17] = 1000; // Body
        sim.PersonData[15] = 1000; // Creativity

        // Without friends, friend power is 0, required is 2 -> fails
        var promotedNoFriends = world.EvaluateFamePromotion(102);
        Assert.False(promotedNoFriends);
        Assert.Equal((short)4, sim[PersonData.FameStarPower09]);

        // Add 1 mutual friend with star power 2 -> at 0.5x scale, friend power is 1 -> still < 2 -> fails
        AddMutualFamousFriends(world, subjectId: 102, totalStarPower: 2);
        var promotedInsufficient = world.EvaluateFamePromotion(102);
        Assert.False(promotedInsufficient);

        // Add more mutual friend star power -> total star power 4 -> at 0.5x scale, friend power is 2 -> passes!
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
        sim[PersonData.FameStarPower09] = 1; // advancing to star 2 requires Charisma 2, Body 1, Creativity 2
        sim[PersonData.FameScore] = (short)target.FameStarThresholds[1]; // 9

        // Charisma is 0, Body is 1000, Creativity is 1000
        sim.PersonData[11] = 0;    // Charisma
        sim.PersonData[17] = 1000; // Body
        sim.PersonData[15] = 1000; // Creativity

        var promotedNoCharisma = world.EvaluateFamePromotion(103);
        Assert.False(promotedNoCharisma);

        // Set Charisma to 200 (level 2)
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
        sim[PersonData.FameStarPower09] = 3; // 1.5 stars
        sim[PersonData.FameStarHighWatermark] = 5; // Sim once reached 2.5 stars
        sim[PersonData.FameScore] = 5; // Lower than threshold for star 2 (which is 21)

        var demoted = world.TryDecrementFameStarPower(104);
        Assert.True(demoted);
        Assert.Equal((short)2, sim[PersonData.FameStarPower09]);
        Assert.Equal((short)5, sim[PersonData.FameStarHighWatermark]); // Watermark preserved!

        // Demote again down to 1
        sim[PersonData.FameScore] = 1;
        var demoted2 = world.TryDecrementFameStarPower(104);
        Assert.True(demoted2);
        Assert.Equal((short)1, sim[PersonData.FameStarPower09]);
        Assert.Equal((short)5, sim[PersonData.FameStarHighWatermark]);

        // Demote again down to 0
        sim[PersonData.FameScore] = 0;
        var demoted3 = world.TryDecrementFameStarPower(104);
        Assert.True(demoted3);
        Assert.Equal((short)0, sim[PersonData.FameStarPower09]);
        Assert.Equal((short)5, sim[PersonData.FameStarHighWatermark]);

        // Clamping at 0: cannot demote below 0
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
        friend.PersonData[81] = (short)totalStarPower; // FameStarPower09

        var subjectNeighbor = world.Neighbors[subjectId];
        subjectNeighbor.PersonData[61] = subjectId;

        friend.Relationships.Resize(subjectId, 1);
        subjectNeighbor.Relationships.Resize(friendId, 1);

        // Daily relationship >= 50 for mutual friendship
        friend.Relationships.Set(subjectId, 0, 75);
        subjectNeighbor.Relationships.Set(friendId, 0, 75);

        world.Neighbors[friendId] = friend;
        world.NeighborWalkOrder.Add(friend);
    }
}
