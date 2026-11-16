using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class DropOntoPrimitiveTests
{
    private static byte[] Operand(short slotSelector, short slot, short flagSelector, short flag) =>
        [(byte)slotSelector, (byte)((ushort)slotSelector >> 8), (byte)slot, (byte)((ushort)slot >> 8),
         (byte)flagSelector, (byte)((ushort)flagSelector >> 8), (byte)flag, (byte)((ushort)flag >> 8)];

    private static (TreeContext Context, XPerson Me, PrimitiveTestHost Host) World(short stackObject = 5)
    {
        var (context, me, host) = PrimitiveTestWorld.Create([1, 2, 3], stackObject);
        me.Slots.AddRange([0, 2, 3]);
        context.World.Add(new XObject(2, PrimitiveTestWorld.Definition()));
        context.World.Add(new XObject(3, PrimitiveTestWorld.Definition()));
        context.World.Add(new XObject(5, PrimitiveTestWorld.Definition()));
        return (context, me, host);
    }

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Fact]
    public void LiteralSlotAndFlagChecksThenDropsOntoTheSlotObjectAtMinusSixteen()
    {
        var (context, _, host) = World();
        Assert.Equal(PrimitiveResult.True, DropOntoPrimitive.Execute(context, Operand(0, 0, 0, 7)));
        Assert.Equal(
            new[] { "CanDropOnto(2,-16,-16,1,5,7)", "DropOnto(2,-16,-16,1,5,7)" },
            host.Calls);
    }

    [Fact]
    public void ASelectorReadsTheValueFromTheFrameParameters()
    {
        var (context, _, host) = World();
        Assert.Equal(PrimitiveResult.True, DropOntoPrimitive.Execute(context, Operand(1, 0, 1, 2)));
        Assert.Equal(new[] { "CanDropOnto(3,-16,-16,1,5,3)", "DropOnto(3,-16,-16,1,5,3)" }, host.Calls);
    }

    [Fact]
    public void AParameterIndexOutsideTheFrameIsStackNumberError()
    {
        var (context, _, host) = World();
        Assert.Equal(SimulationError.StackNumberOutOfRange, Fails(() => DropOntoPrimitive.Execute(context, Operand(1, 3, 0, 0))));
        Assert.Equal(SimulationError.StackNumberOutOfRange, Fails(() => DropOntoPrimitive.Execute(context, Operand(0, 0, 1, -1))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ASlotOutsideTheSlotsIsSlotError()
    {
        var (context, _, host) = World();
        Assert.Equal(SimulationError.SlotNumberOutOfRange, Fails(() => DropOntoPrimitive.Execute(context, Operand(0, 2, 0, 0))));
        Assert.Equal(SimulationError.SlotNumberOutOfRange, Fails(() => DropOntoPrimitive.Execute(context, Operand(0, -1, 0, 0))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void TheSlotIsCheckedBeforeTheStackObject()
    {
        var (context, _, _) = World(stackObject: 9);
        Assert.Equal(SimulationError.SlotNumberOutOfRange, Fails(() => DropOntoPrimitive.Execute(context, Operand(0, 5, 0, 0))));
    }

    [Fact]
    public void AMissingStackObjectIsNoStackObjectError()
    {
        var (context, _, host) = World(stackObject: 9);
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => DropOntoPrimitive.Execute(context, Operand(0, 0, 0, 0))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void AnEmptySlotAnswersFalse()
    {
        var (context, me, host) = World();
        me.Slots[1] = 0;
        Assert.Equal(PrimitiveResult.False, DropOntoPrimitive.Execute(context, Operand(0, 0, 0, 0)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ARefusedCheckAnswersFalse()
    {
        var (context, _, host) = World();
        host.PlacementAnswer = false;
        Assert.Equal(PrimitiveResult.False, DropOntoPrimitive.Execute(context, Operand(0, 0, 0, 4)));
        Assert.Equal(new[] { "CanDropOnto(2,-16,-16,1,5,4)" }, host.Calls);
    }

    [Fact]
    public void ResolvesTheRealSlotByManagerKeyRatherThanSavedId()
    {
        var (context, me, host) = World();
        var target = context.World.ObjectById(2)!;
        me.SetObjectTableEntry(5, context.World.ObjectById(5));
        target.ClockManagerIndex = 120;
        me.SetObjectTableEntry(120, target);
        me.Slots[1] = 120;

        Assert.Equal(PrimitiveResult.True, DropOntoPrimitive.Execute(context, Operand(0, 0, 0, 3)));

        Assert.Equal(new[] { "CanDropOnto(2,-16,-16,1,5,3)", "DropOnto(2,-16,-16,1,5,3)" }, host.Calls);
    }

    [Fact]
    public void NativeDropOntoUsesTheReferencedObjectsPositionForTheRelativePlacementSentinel()
    {
        var (context, me, _) = World();
        context.World.Host = null;
        var target = context.World.ObjectById(2)!;
        var held = context.World.ObjectById(5)!;
        context.World.SetObjectPosition(target, new ObjectPosition(0x40, 0x50, 1));
        context.World.SetObjectPosition(held, new ObjectPosition(0x60, 0x50, 1));
        target[ObjectData.AllowedHeightFlags] = 1;
        held.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
        me.Slots[1] = target.ClockManagerIndex;

        Assert.Equal(PrimitiveResult.True, DropOntoPrimitive.Execute(context, Operand(0, 0, 0, 0)));

        Assert.Equal(held.Position, target.Position);
        Assert.Equal(held.ClockManagerIndex, target[ObjectData.ContainerId]);
        Assert.Equal((short)1, target[ObjectData.SlotHierNumber]);
        Assert.Equal(target.ClockManagerIndex, held.Slots[1]);
        Assert.Equal(target.ClockManagerIndex, held.FootprintSlots[0].OccupantObjectId);
    }
}
