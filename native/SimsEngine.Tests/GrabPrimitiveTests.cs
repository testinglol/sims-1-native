using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class GrabPrimitiveTests
{
    // The running object is 100 at (0x40, 0x50, 1), inside the lot border; the stack object is 2.
    private static (TreeContext Context, PrimitiveTestHost Host) World(short stackObject = 2)
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: stackObject);
        me.Position = new ObjectPosition(0x40, 0x50, 1);
        me.Slots.AddRange([0, 0]);
        // FUN_00451e1a validates against the Sim's footprint slot 0 (the hand) using the held object's allowed heights.
        me.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
        context.World.Add(new XObject(2, PrimitiveTestWorld.Definition()))[ObjectData.AllowedHeightFlags] = 1;
        context.World.Host = null;
        return (context, host);
    }

    // Grab reads no operand; a zero operand is the one the tree stores.
    private static readonly byte[] NoOperand = new byte[8];

    [Fact]
    public void PlacesTheStackObjectInTheHandsAndMovesItWithTheSim()
    {
        var (context, host) = World();
        Assert.Equal(PrimitiveResult.True, GrabPrimitive.Execute(context, NoOperand));
        var item = context.World.ObjectById(2)!;
        Assert.Equal((short)0, context.Me.Slots[0]);
        Assert.Equal(item.ClockManagerIndex, context.Me.Slots[1]);
        Assert.Equal(context.Me.ClockManagerIndex, item[ObjectData.ContainerId]);
        Assert.Equal(context.Me.ClockManagerIndex, item[ObjectData.ParentId]);
        Assert.Equal((short)1, item[ObjectData.SlotNumber]);
        Assert.Equal((short)1, item[ObjectData.SlotHierNumber]);
        Assert.Equal(context.Me.Position, item.Position);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void AnOccupiedHandAnswersFalse()
    {
        var (context, host) = World();
        var occupant = context.World.Add(new XObject(3, PrimitiveTestWorld.Definition()));
        context.Me.Slots[1] = 3;
        ((XPerson)context.Me).FootprintSlots[0].OccupantObjectId = occupant.ClockManagerIndex;
        Assert.Equal(PrimitiveResult.False, GrabPrimitive.Execute(context, NoOperand));
        Assert.Equal((short)0, context.Me.Slots[0]);
        Assert.Equal((short)3, context.Me.Slots[1]);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void AMissingStackObjectAnswersFalse()
    {
        var (context, host) = World(stackObject: 9);
        Assert.Equal(PrimitiveResult.False, GrabPrimitive.Execute(context, NoOperand));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void TheOperandIsNotRead()
    {
        var (context, _) = World();
        Assert.Equal(PrimitiveResult.True, GrabPrimitive.Execute(context, [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff]));
    }

    [Fact]
    public void GrabbingTheObjectAlreadyInHandSucceedsWithoutChangingItsContainer()
    {
        var (context, host) = World();
        context.Me.Slots[1] = 2;
        var item = context.World.ObjectById(2)!;
        item[ObjectData.ContainerId] = context.Me.ClockManagerIndex;
        item[ObjectData.ParentId] = context.Me.ClockManagerIndex;
        item[ObjectData.SlotHierNumber] = 1;
        Assert.Equal(PrimitiveResult.True, GrabPrimitive.Execute(context, NoOperand));
        Assert.Equal((short)2, context.Me.Slots[1]);
        Assert.Equal(context.Me.ClockManagerIndex, item[ObjectData.ContainerId]);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void GrabbingFromAContainerClearsItsSlotAndUpdatesContainerFields()
    {
        var (context, host) = World();
        var fridge = context.World.Add(new XObject(3, PrimitiveTestWorld.Definition()));
        fridge.Slots.AddRange([0, 2]);
        var item = context.World.ObjectById(2)!;
        item[ObjectData.ContainerId] = fridge.ClockManagerIndex;
        item[ObjectData.ParentId] = fridge.ClockManagerIndex;
        item[ObjectData.SlotNumber] = 0;
        item[ObjectData.SlotHierNumber] = 1;

        Assert.Equal(PrimitiveResult.True, GrabPrimitive.Execute(context, NoOperand));
        Assert.Equal((short)0, fridge.Slots[0]);
        Assert.Equal((short)0, fridge.Slots[1]);
        Assert.Equal((short)2, context.Me.Slots[1]);
        Assert.Equal(context.Me.ClockManagerIndex, item[ObjectData.ContainerId]);
        Assert.Equal(context.Me.ClockManagerIndex, item[ObjectData.ParentId]);
        Assert.Equal((short)1, item[ObjectData.SlotHierNumber]);
        Assert.Equal(context.Me.Position, item.Position);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void GrabPreservesSlotZeroOverlapAndResolvesManagerKeys()
    {
        var (context, _) = World();
        var overlap = context.World.Add(new XObject(3, PrimitiveTestWorld.Definition()));
        var item = context.World.ObjectById(2)!;
        var holderKey = (short)120;
        context.Me.ClockManagerIndex = holderKey;
        context.Me.SetObjectTableEntry(holderKey, context.Me);
        context.Me.Slots[0] = overlap.ClockManagerIndex;
        overlap[ObjectData.ContainerId] = 0;
        overlap[ObjectData.ParentId] = holderKey;
        overlap[ObjectData.SlotHierNumber] = 0;
        context.Me.SetObjectTableEntry(item.ClockManagerIndex, item);

        Assert.Equal(PrimitiveResult.True, GrabPrimitive.Execute(context, NoOperand));

        Assert.Equal(overlap.ClockManagerIndex, context.Me.Slots[0]);
        Assert.Equal(item.ClockManagerIndex, context.Me.Slots[1]);
        Assert.Equal((short)0, overlap[ObjectData.SlotHierNumber]);
        Assert.Equal((short)1, item[ObjectData.SlotHierNumber]);
        Assert.Equal(holderKey, item[ObjectData.ParentId]);
    }

    [Fact]
    public void GrabbingSlotZeroOverlapChildDetachesItsParentBeforeUsingRealSlot()
    {
        var (context, _) = World();
        var item = context.World.ObjectById(2)!;
        context.Me.Slots[0] = item.ClockManagerIndex;
        item[ObjectData.ContainerId] = 0;
        item[ObjectData.ParentId] = context.Me.ClockManagerIndex;
        item[ObjectData.SlotHierNumber] = 0;

        Assert.Equal(PrimitiveResult.True, GrabPrimitive.Execute(context, NoOperand));

        Assert.Equal((short)0, context.Me.Slots[0]);
        Assert.Equal(item.ClockManagerIndex, context.Me.Slots[1]);
        Assert.Equal(context.Me.ClockManagerIndex, item[ObjectData.ParentId]);
        Assert.Equal((short)1, item[ObjectData.SlotHierNumber]);
    }
}
