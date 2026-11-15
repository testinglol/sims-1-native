using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class ReachOpsTests
{
    [Theory]
    [InlineData(-1f, false, 9)]
    [InlineData(0f, false, 1)]
    [InlineData(0.75f, false, 3)]
    [InlineData(2f, true, 6)]
    [InlineData(2.7f, false, 7)]
    [InlineData(4f, false, -1)]
    [InlineData(4f, true, -1)]
    public void ReachIndexFollowsTheDistanceBands(float position, bool sitting, int expected) =>
        Assert.Equal(expected, ReachOps.ReachIndex(position, sitting));

    [Theory]
    [InlineData((short)0, 3)]
    [InlineData((short)1, 1)]
    [InlineData((short)2, 2)]
    [InlineData((short)-1, 7)]
    public void FinishReachStopsThePrimaryAndSelectsControlStateFromSitting(short sitting, int expectedControlState)
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person.AnimatorControlState = 7;
        person.AnimatorCurrentSkill = 15;
        person.AnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("reach", 4, []), false, false);
        person.ReachAnimationActive = true;
        person.PendingAnimationEvents.AddRange([1, 2]);
        person[PersonData.Sitting] = sitting;
        var ops = new ReachOps(context.World, null);

        ops.FinishReach(person);

        Assert.Equal(expectedControlState, person.AnimatorControlState);
        Assert.Null(person.AnimationPlayback);
        Assert.Equal(-1, person.AnimatorCurrentSkill);
        Assert.False(person.ReachAnimationActive);
        Assert.Empty(person.PendingAnimationEvents);
    }

    [Fact]
    public void HostlessSlotCountAndPositionComeFromTheFootprintSlots()
    {
        var world = new SimulationWorld();
        var holder = world.Add(new XObject(7, PrimitiveTestWorld.Definition()));
        holder.FootprintSlots.Add(new ObjectFootprintSlot { Height = 1.5f });
        holder.FootprintSlots.Add(new ObjectFootprintSlot { Height = 3f });
        var ops = new ReachOps(world, null);

        Assert.Equal(2, ops.SlotCount(holder));
        Assert.Equal(3f * (float)(1.0 / (Math.Sqrt(6.0) + Math.Sqrt(6.0))), ops.SlotPosition(holder, 1));
        Assert.Equal(0f, ops.SlotPosition(holder, 2));
    }
}
