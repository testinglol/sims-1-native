using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class MoveOutOfWayTests
{
    [Fact]
    public void BlockedSimGetsTheSourceGoHereRequestAtTheRequestingActionsPriority()
    {
        var (context, blocker, _) = PrimitiveTestWorld.Create();
        var world = context.World;
        var position = new ObjectPosition(168, 184, 1);
        world.SetObjectPosition(blocker, position);
        var created = 0;
        world.ObjectFactory = guid =>
        {
            Assert.Equal(0x7c4, guid);
            created++;
            return new XObject(200, PrimitiveTestWorld.Definition())
            {
                TypeRecord = new TypeRecord(0x7c4) { Interactions = MoveAsideInteractions() },
            };
        };

        Assert.True(RouteExecutor.MoveOutOfWay(world, blocker, 37));

        var aside = Assert.Single(world.WalkOrder, candidate => candidate.Id == 200);
        Assert.Equal(position, aside.Position);
        var slot = Assert.Single(aside.RoutingSlots);
        Assert.Equal(0x2155u, slot.Flags);
        Assert.Equal((16, 80, 16), (slot.MinProximity, slot.MaxProximity, slot.OptimalProximity));
        Assert.Equal(1, slot.Standing);
        Assert.Equal(-3, slot.Facing);
        var action = blocker.ActionQueue[blocker.ActionQueueCurrent & 7];
        Assert.Equal(3, action.InteractionId);
        Assert.Equal((ushort)0x4567, action.ActionTree);
        Assert.Equal(37, action.Threshold);
        Assert.Same(aside, action.Owner);
        Assert.Equal(1, created);
    }

    [Fact]
    public void ExistingMoveAsideActionPreventsCreatingAnotherGoHereObject()
    {
        var (context, blocker, _) = PrimitiveTestWorld.Create();
        blocker.CurrentActionRecord = new ActionQueueSlot
        {
            Owner = new XObject(2, PrimitiveTestWorld.Definition()) { TypeRecord = new TypeRecord(0x7c4) },
            RawDword10 = 3,
        };
        var created = 0;
        context.World.ObjectFactory = _ =>
        {
            created++;
            return null;
        };

        Assert.False(RouteExecutor.MoveOutOfWay(context.World, blocker, 37));
        Assert.Equal(0, created);
    }

    private static InteractionTable MoveAsideInteractions()
    {
        var builder = new ChunkBuilder().S16(4).S16(6);
        for (var index = 0; index < 4; index++)
            builder.U16((ushort)(index == 3 ? 0x4567 : 0)).U16(0).S32(0).U32(0)
                .S32(index).S32(1).F32(0).S32(0).S32(0);
        var (file, chunk) = builder.InFile("TTAB");
        return InteractionTable.Read(file, chunk);
    }
}
