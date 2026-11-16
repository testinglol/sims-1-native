using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class RemoveObjectInstancePrimitiveTests
{
    private static short[] Definition(short type = 0)
    {
        var definition = new short[XObject.DefinitionCount];
        definition[9] = type;
        return definition;
    }

    private static (TreeContext Context, SimulationWorld World, XPerson Me, XObject Stack) World()
    {
        var world = new SimulationWorld();
        world.Host = new PrimitiveTestHost();
        var me = world.Add(new XPerson(1, Definition(XPerson.PersonType), attributes: 4));
        var stack = world.Add(new XObject(2, Definition()));
        return (new TreeContext(world, me, new StackFrame(2, [], [])), world, me, stack);
    }

    private static byte[] Operand(short selector, byte flags = 0) =>
        [(byte)selector, (byte)(selector >> 8), flags, 0, 0, 0, 0, 0];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Fact]
    public void RemovingTheRunningObjectStopsTheRun()
    {
        var (context, world, me, _) = World();
        me[PersonData.NeighborId] = 5;
        me.PersonData[2] = 37;
        world.Neighbors[5] = new NeighborState(5, Definition());
        Assert.Equal(PrimitiveResult.Stop, RemoveObjectInstancePrimitive.Execute(context, Operand(0)));
        Assert.Equal(new short[] { 1 }, world.PendingRemovals);
        Assert.Equal((short)37, world.Neighbors[5].PersonData[2]);
    }

    [Fact]
    public void BitZeroOfTheFlagsKeepsTheRunGoing()
    {
        var (context, world, _, _) = World();
        Assert.Equal(PrimitiveResult.True, RemoveObjectInstancePrimitive.Execute(context, Operand(0, flags: 1)));
        Assert.Equal(new short[] { 1 }, world.PendingRemovals);
    }

    [Fact]
    public void RemovingAHeldObjectClearsTheHoldersSlot()
    {
        var (context, world, me, stack) = World();
        while (me.Slots.Count < 2) me.Slots.Add(0);
        me.Slots[1] = stack.ClockManagerIndex;
        stack[ObjectData.ParentId] = me.ClockManagerIndex;
        stack[ObjectData.SlotHierNumber] = 1;
        Assert.Equal(PrimitiveResult.True, RemoveObjectInstancePrimitive.Execute(context, Operand(1)));
        Assert.Equal((short)0, me.Slots[1]);
        Assert.Equal((short)0, stack[ObjectData.ParentId]);
    }

    [Fact]
    public void RemovingAnotherObjectDoesNotStopTheRun()
    {
        var (context, world, _, _) = World();
        Assert.Equal(PrimitiveResult.True, RemoveObjectInstancePrimitive.Execute(context, Operand(1)));
        Assert.Equal(new short[] { 2 }, world.PendingRemovals);
    }

    [Fact]
    public void SelectorTwoNamesObjectZero()
    {
        var (context, world, _, _) = World();
        Assert.Equal(PrimitiveResult.False, RemoveObjectInstancePrimitive.Execute(context, Operand(2)));
        Assert.Empty(world.PendingRemovals);

        world.Add(new XObject(0, Definition()));
        Assert.Equal(PrimitiveResult.True, RemoveObjectInstancePrimitive.Execute(context, Operand(2)));
        Assert.Equal(new short[] { 0 }, world.PendingRemovals);
    }

    [Fact]
    public void AMissingObjectIsFalseAndNothingIsQueued()
    {
        var (context, world, _, _) = World();
        world.Objects.Remove(2);
        Assert.Equal(PrimitiveResult.False, RemoveObjectInstancePrimitive.Execute(context, Operand(1)));
        Assert.Empty(world.PendingRemovals);
    }

    [Fact]
    public void AnObjectIsQueuedOnlyOnce()
    {
        var (context, world, _, _) = World();
        RemoveObjectInstancePrimitive.Execute(context, Operand(1));
        RemoveObjectInstancePrimitive.Execute(context, Operand(1));
        Assert.Equal(new short[] { 2 }, world.PendingRemovals);
    }

    [Fact]
    public void OutOfLotRootSkipsTheEmptyRemovalGroupIterator()
    {
        var (context, world, _, stack) = World();
        stack.Position = new ObjectPosition(-16, -16, 1);
        stack.Data[2] = 42;
        stack.Data[8] = -1;

        Assert.Equal(PrimitiveResult.True, RemoveObjectInstancePrimitive.Execute(context, Operand(1)));

        Assert.Equal(new ObjectPosition(-16, -16, 1), stack.Position);
        Assert.Equal((short)-5, stack.RoomWord84);
        Assert.Equal((short)-1, stack.RemovalCallbackWord14);
        Assert.Equal((short)0, stack.Data[2]);
        Assert.Equal(unchecked((short)0xfeff), stack.Data[8]);
        Assert.True(world.DisplayRefreshRequested);
        Assert.Contains((short)2, world.PendingRemovals);
    }

    [Fact]
    public void PlainRootWhoseTileMapPointsToItHasNoOtherRemovalGroupCandidate()
    {
        var (context, world, _, stack) = World();
        world.PlaceObject(stack, new ObjectPosition(16, 16, 1));
        Assert.Equal((short)2, world.TileValueAt(1, 1, 1));
        Assert.Equal(PrimitiveResult.True, RemoveObjectInstancePrimitive.Execute(context, Operand(1)));

        Assert.Equal(new short[] { 2 }, world.PendingRemovals);
        Assert.Equal(0, world.TileValueAt(1, 1, 1));
    }

    [Fact]
    public void APendingParentBlocksRemoval()
    {
        var (context, world, _, stack) = World();
        var parent = world.Add(new XObject(4, Definition()));
        parent.Slots.Add(stack.ClockManagerIndex);
        stack[ObjectData.ParentId] = parent.ClockManagerIndex;
        stack[ObjectData.SlotHierNumber] = 0;
        world.PendingRemovals.Add(parent.Id);

        Assert.Equal(PrimitiveResult.True, RemoveObjectInstancePrimitive.Execute(context, Operand(1)));

        Assert.Equal(new short[] { 4 }, world.PendingRemovals);
    }

    [Fact]
    public void TheGlobalFlagDecidesWhetherRemovingMeStops()
    {
        var (context, world, _, _) = World();
        world.SelfRemovalEndsRun = false;
        Assert.Equal(PrimitiveResult.True, RemoveObjectInstancePrimitive.Execute(context, Operand(0)));
    }

    [Fact]
    public void AnOutOfRangeSelectorIsIllegal()
    {
        var (context, _, _, _) = World();
        Assert.Equal(SimulationError.IllegalOwnerFieldInDataReference, Fails(() => RemoveObjectInstancePrimitive.Execute(context, Operand(3))));
        Assert.Equal(SimulationError.IllegalOwnerFieldInDataReference, Fails(() => RemoveObjectInstancePrimitive.Execute(context, Operand(-1))));
    }

    [Fact]
    public void RemovingAMultiTilePartRedirectsToItsMasterOutsideSettingEightMode()
    {
        var (context, world, _, stack) = World();
        stack.Definition[(int)ObjdField.MasterId] = 1;
        stack.MultiTileMasterId = 3;
        var master = world.Add(new XObject(3, Definition()));
        Assert.Equal(PrimitiveResult.True, RemoveObjectInstancePrimitive.Execute(context, Operand(1, flags: 2)));

        Assert.Equal(new short[] { 3 }, world.PendingRemovals);
        Assert.Equal(0x1000u, master.Flags & 0x1000u);
        Assert.Equal(new ObjectPosition(-16, -16, 1), master.Position);
    }

    [Fact]
    public void FunctionEightOnThePartRemovesItDirectly()
    {
        var (context, world, _, stack) = World();
        stack.Definition[(int)ObjdField.MasterId] = 1;
        stack.MultiTileMasterId = 3;
        stack.TypeRecord = new TypeRecord(0x1234) { FunctionTable = [0, 0, 0, 0, 0, 0, 0, 0, 4115] };
        world.Add(new XObject(3, Definition()));

        Assert.Equal(PrimitiveResult.True, RemoveObjectInstancePrimitive.Execute(context, Operand(1)));

        Assert.Equal(new short[] { 2 }, world.PendingRemovals);
    }

    [Fact]
    public void APartWithoutALinkedMasterRemainsTheRemovalTarget()
    {
        var (context, world, _, stack) = World();
        stack.Definition[(int)ObjdField.MasterId] = 1;

        Assert.Equal(PrimitiveResult.True, RemoveObjectInstancePrimitive.Execute(context, Operand(1)));

        Assert.Equal(new short[] { 2 }, world.PendingRemovals);
    }

    [Fact]
    public void AClearRemovalFlagPreservesOtherObjectFlags()
    {
        var (context, world, _, stack) = World();
        stack.Flags = 0xB123;

        Assert.Equal(PrimitiveResult.True, RemoveObjectInstancePrimitive.Execute(context, Operand(1)));

        Assert.Equal(0xA123u, stack.Flags);
    }

    [Fact]
    public void AnAlreadyQueuedParentPreventsQueueingTheTarget()
    {
        var (context, world, _, stack) = World();
        stack.Definition[(int)ObjdField.MasterId] = 1;
        stack.MultiTileMasterId = 3;
        var master = world.Add(new XObject(3, Definition()));
        var parent = world.Add(new XObject(4, Definition()));
        parent.Slots.Add(master.ClockManagerIndex);
        master[ObjectData.ParentId] = parent.ClockManagerIndex;
        master[ObjectData.SlotHierNumber] = 0;
        world.PendingRemovals.Add(parent.Id);

        Assert.Equal(PrimitiveResult.True, RemoveObjectInstancePrimitive.Execute(context, Operand(1)));

        Assert.Equal(new short[] { 4 }, world.PendingRemovals);
        Assert.Equal(default, world.Objects[3].Position);
    }

    [Fact]
    public void AQueuedMasterIsNotPreparedOrQueuedAgain()
    {
        var (context, world, _, stack) = World();
        stack.Definition[(int)ObjdField.MasterId] = 1;
        stack.MultiTileMasterId = 3;
        world.Add(new XObject(3, Definition()));
        world.PendingRemovals.Add(3);
        Assert.Equal(PrimitiveResult.True, RemoveObjectInstancePrimitive.Execute(context, Operand(1)));

        Assert.Equal(new short[] { 3 }, world.PendingRemovals);
        Assert.Equal(default, world.Objects[3].Position);
    }
}
