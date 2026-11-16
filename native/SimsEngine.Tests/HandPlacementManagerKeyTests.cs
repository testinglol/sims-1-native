using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class HandPlacementManagerKeyTests
{
    [Fact]
    public void CreatedHandObjectUsesItsManagerKeyWhenItsIdEqualsTheHoldersManagerKey()
    {
        var world = new SimulationWorld();
        var holder = world.Add(Object(-1, new ObjectPosition(168, 168, 1)));
        holder.Slots.AddRange([0, 0]);
        var created = Object(holder.ClockManagerIndex, new ObjectPosition(-16, -16, 1));
        created.ClockManagerIndex = 2;
        world.Objects.Add(created.Id, created);
        world.WalkOrder.Add(created);
        holder.SetObjectTableEntry(created.ClockManagerIndex, created);

        Assert.Equal(holder.ClockManagerIndex, created.Id);
        Assert.NotEqual(created.Id, created.ClockManagerIndex);
        Assert.True(world.PlaceCreatedObjectInHand(holder, created));

        var tileContents = world.ObjectsAtTile(10, 10, 1).ToArray();

        Assert.Equal((short)0, holder.Slots[0]);
        Assert.Equal(created.ClockManagerIndex, holder.Slots[1]);
        Assert.Equal(new[] { holder, created }, tileContents);
        Assert.Equal(holder.ClockManagerIndex, world.TileValueAt(10, 10, 1));
        Assert.Equal(holder.ClockManagerIndex, created[ObjectData.ContainerId]);
        Assert.Equal(holder.ClockManagerIndex, created[ObjectData.ParentId]);
        Assert.Equal((short)0, created[ObjectData.SlotNumber]);
        Assert.Equal((short)1, created[ObjectData.SlotHierNumber]);
        Assert.Equal(holder.Position, created.Position);
    }

    [Fact]
    public void FirstRealSlotCoexistsWithSlotZeroOverlapChain()
    {
        var world = new SimulationWorld();
        var holder = world.Add(Object(1, new ObjectPosition(168, 168, 1)));
        var overlap = world.Add(Object(2, new ObjectPosition(-16, -16, 1)));
        var held = world.Add(Object(3, new ObjectPosition(-16, -16, 1)));
        holder.Slots.Add(overlap.ClockManagerIndex);
        holder.Slots.Add(0);
        overlap[ObjectData.ContainerId] = 0;
        overlap[ObjectData.ParentId] = holder.ClockManagerIndex;
        overlap[ObjectData.SlotNumber] = 0;
        overlap[ObjectData.SlotHierNumber] = 0;

        Assert.True(world.PlaceCreatedObjectInHand(holder, held));

        Assert.Equal(overlap.ClockManagerIndex, holder.Slots[0]);
        Assert.Equal(held.ClockManagerIndex, holder.Slots[1]);
        Assert.Equal((short)0, overlap[ObjectData.SlotHierNumber]);
        Assert.Equal((short)1, held[ObjectData.SlotHierNumber]);
        Assert.Equal(new[] { holder, overlap, held }, world.ObjectsAtTile(10, 10, 1));
    }

    [Fact]
    public void OccupiedFirstRealSlotRejectsWithoutChangingEitherObject()
    {
        var world = new SimulationWorld();
        var holder = world.Add(Object(1, new ObjectPosition(168, 168, 1)));
        var occupant = world.Add(Object(2, new ObjectPosition(168, 168, 1)));
        var created = world.Add(Object(3, new ObjectPosition(-16, -16, 1)));
        holder.Slots.AddRange([0, occupant.ClockManagerIndex]);
        occupant[ObjectData.ContainerId] = holder.ClockManagerIndex;
        occupant[ObjectData.ParentId] = holder.ClockManagerIndex;
        occupant[ObjectData.SlotNumber] = 0;
        occupant[ObjectData.SlotHierNumber] = 1;

        Assert.False(world.PlaceCreatedObjectInHand(holder, created));

        Assert.Equal(new short[] { 0, occupant.ClockManagerIndex }, holder.Slots);
        Assert.Equal((short)0, created[ObjectData.ContainerId]);
        Assert.Equal((short)0, created[ObjectData.ParentId]);
        Assert.Equal(new ObjectPosition(-16, -16, 1), created.Position);
    }

    [Fact]
    public void ContainmentCycleRejectsWithoutAllocatingOrMutatingSlots()
    {
        var world = new SimulationWorld();
        var holder = world.Add(Object(1, new ObjectPosition(168, 168, 1)));
        var created = world.Add(Object(2, new ObjectPosition(-16, -16, 1)));
        created[ObjectData.ContainerId] = holder.ClockManagerIndex;
        created[ObjectData.ParentId] = holder.ClockManagerIndex;
        created[ObjectData.SlotHierNumber] = 1;
        holder.Slots.AddRange([0, created.ClockManagerIndex]);

        Assert.False(world.TryPlaceInFirstRealSlot(created, holder));

        Assert.Equal(new short[] { 0, created.ClockManagerIndex }, holder.Slots);
        Assert.Equal(holder.ClockManagerIndex, created[ObjectData.ContainerId]);
    }

    [Fact]
    public void SlotZeroAncestorWithNoCachedContainerRejectsCycleWithoutMutation()
    {
        var world = new SimulationWorld();
        var descendant = world.Add(Object(1, new ObjectPosition(168, 168, 1)));
        var holder = world.Add(Object(2, new ObjectPosition(-16, -16, 1)));
        descendant.Slots.AddRange([holder.ClockManagerIndex, 0]);
        holder[ObjectData.ParentId] = descendant.ClockManagerIndex;
        holder[ObjectData.SlotHierNumber] = 0;
        holder[ObjectData.ContainerId] = 0;

        Assert.False(world.TryPlaceInFirstRealSlot(holder, descendant));

        Assert.Equal(new short[] { holder.ClockManagerIndex, 0 }, descendant.Slots);
        Assert.Equal((short)0, holder[ObjectData.ContainerId]);
        Assert.Equal(descendant.ClockManagerIndex, holder[ObjectData.ParentId]);
    }

    [Fact]
    public void MissingAuthoredFirstRealSlotRejectsWithoutExpandingHolder()
    {
        var world = new SimulationWorld();
        var holder = world.Add(Object(1, new ObjectPosition(168, 168, 1)));
        var item = world.Add(Object(2, new ObjectPosition(-16, -16, 1)));

        Assert.False(world.TryPlaceInFirstRealSlot(holder, item));

        Assert.Empty(holder.Slots);
        Assert.Equal((short)0, item[ObjectData.ParentId]);
        Assert.Equal((short)0, item[ObjectData.ContainerId]);
    }

    private static XObject Object(short id, ObjectPosition position) =>
        new(id, PrimitiveTestWorld.Definition()) { Position = position };
}
