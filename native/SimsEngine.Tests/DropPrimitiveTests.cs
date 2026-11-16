using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class DropPrimitiveTests
{
    private static (TreeContext Context, XPerson Me, PrimitiveTestHost Host) World()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Position = new ObjectPosition(50, 60, 2);
        me[ObjectData.Direction] = 2;
        context.World.Add(new XObject(2, PrimitiveTestWorld.Definition()));
        context.World.Add(new XObject(3, PrimitiveTestWorld.Definition()));
        return (context, me, host);
    }

    private static readonly byte[] NoOperand = new byte[8];

    [Fact]
    public void DropsTheFirstRealSlotAtTheFootprintInFrontOfTheRunningObject()
    {
        var (context, me, host) = World();
        me.Slots.AddRange([0, 2]);
        Assert.Equal(PrimitiveResult.True, DropPrimitive.Execute(context, NoOperand));
        Assert.Equal(
            new[] { "CanDrop(2,66,60,2,null,0)", "Drop(2,66,60,2,null,0)" },
            host.Calls);
    }

    [Fact]
    public void NativeWorldDropsSlotZeroThroughWorldPlacementWhenThereIsNoHost()
    {
        var (context, me, _) = World();
        var item = context.World.ObjectById(2)!;
        me.Position = new ObjectPosition(0x40, 0x50, 3);
        item[ObjectData.AllowedHeightFlags] = 1;
        item[ObjectData.PlacementFlags] = 0x0600;
        context.World.PlaceObject(item, new ObjectPosition(0x40, 0x60, 3));
        me.Slots.AddRange([0, 0]);
        context.World.Host = null;

        Assert.True(context.World.CanPlaceObject(item, new ObjectPosition(0x40, 0x60, 3)),
            $"Native placement rejected with code {context.World.LastPlacementFailureCode}/{context.World.LastPortalPlacementFailureCode}.");
        var overlap = context.World.ObjectById(3)!;
        me.Slots[0] = overlap.ClockManagerIndex;
        me.Slots[1] = 0;
        overlap[ObjectData.ContainerId] = 0;
        overlap[ObjectData.ParentId] = me.ClockManagerIndex;
        overlap[ObjectData.SlotNumber] = 0;
        overlap[ObjectData.SlotHierNumber] = 0;
        Assert.True(context.World.PlaceCreatedObjectInHand(me, item));
        Assert.Equal(PrimitiveResult.True, DropPrimitive.Execute(context, NoOperand));
        Assert.Equal(new ObjectPosition(0x50, 0x50, 3), item.Position);
        Assert.Equal((short)0, item[ObjectData.ParentId]);
        Assert.Equal((short)-1, item[ObjectData.SlotHierNumber]);
        Assert.Equal(overlap.ClockManagerIndex, me.Slots[0]);
        Assert.Equal((short)0, me.Slots[1]);
    }

    [Fact]
    public void OnlyTheFirstRealSlotIsDropped()
    {
        var (context, me, host) = World();
        me.Slots.AddRange([3, 2, 3, 3]);
        DropPrimitive.Execute(context, NoOperand);
        Assert.Contains("Drop(2,66,60,2,null,0)", host.Calls);
        Assert.DoesNotContain(host.Calls, call => call.Contains("(3,"));
    }

    [Fact]
    public void SlotZeroOverlapAloneIsNotDropped()
    {
        var (context, me, host) = World();
        me.Slots.AddRange([2, 0]);

        Assert.Equal(PrimitiveResult.False, DropPrimitive.Execute(context, NoOperand));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void DropResolvesTheFirstRealSlotByManagerKey()
    {
        var (context, me, host) = World();
        var item = context.World.ObjectById(2)!;
        item.ClockManagerIndex = 120;
        me.SetObjectTableEntry(120, item);
        me.Slots.AddRange([0, 120]);

        Assert.Equal(PrimitiveResult.True, DropPrimitive.Execute(context, NoOperand));

        Assert.Equal(new[] { "CanDrop(2,66,60,2,null,0)", "Drop(2,66,60,2,null,0)" }, host.Calls);
    }

    [Fact]
    public void NoSlotAnswersFalseWithoutAskingTheHost()
    {
        var (context, _, host) = World();
        Assert.Equal(PrimitiveResult.False, DropPrimitive.Execute(context, NoOperand));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void AHeldIdNotOnTheLotAnswersFalse()
    {
        var (context, me, host) = World();
        me.Slots.AddRange([0, 7]);
        Assert.Equal(PrimitiveResult.False, DropPrimitive.Execute(context, NoOperand));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ARefusedCheckAnswersFalseAndNothingIsDropped()
    {
        var (context, me, host) = World();
        me.Slots.AddRange([0, 2]);
        host.PlacementAnswer = false;
        Assert.Equal(PrimitiveResult.False, DropPrimitive.Execute(context, NoOperand));
        Assert.Equal(new[] { "CanDrop(2,66,60,2,null,0)" }, host.Calls);
    }
}
