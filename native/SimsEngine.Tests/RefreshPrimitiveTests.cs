using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class RefreshPrimitiveTests
{
    private static byte[] Operand(short mode, short selector) =>
        [(byte)mode, (byte)((ushort)mode >> 8), (byte)selector, (byte)((ushort)selector >> 8), 0, 0, 0, 0];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Fact]
    public void SelectorZeroRefreshesTheDisplay()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Equal(PrimitiveResult.True, RefreshPrimitive.Execute(context, Operand(0, 0)));
        Assert.Equal(new[] { "RefreshDisplay" }, host.Calls);
        Assert.True(context.World.DisplayRefreshRequested);
    }

    [Fact]
    public void SelectorZeroQueuesNativeDisplayRefreshWithoutAHost()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        context.World.Host = null;

        Assert.Equal(PrimitiveResult.True, RefreshPrimitive.Execute(context, Operand(0, 0)));

        Assert.True(context.World.DisplayRefreshRequested);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void SelectorOneSendsNoticeF2WithTheSubjectId()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        context.World.Add(new XObject(5, PrimitiveTestWorld.Definition()));
        Assert.Equal(PrimitiveResult.True, RefreshPrimitive.Execute(context, Operand(0, 1)));
        Assert.Equal(PrimitiveResult.True, RefreshPrimitive.Execute(context, Operand(1, 1)));
        Assert.Equal(new[] { "SendNotice(242,100)", "SendNotice(242,5)" }, host.Calls);
    }

    [Fact]
    public void SelectorOneDispatchesItsNoticeWithoutAHost()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        context.World.Host = null;

        Assert.Equal(PrimitiveResult.True, RefreshPrimitive.Execute(context, Operand(0, 1)));

        Assert.Equal(["SendNotice(242,100)"], host.Calls);
        Assert.Null(context.World.Host);
    }

    [Fact]
    public void SelectorTwoRefreshesTheSubjectsRoom()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me[ObjectData.Room] = 3;
        RefreshPrimitive.Execute(context, Operand(0, 2));
        Assert.Equal(new[] { "RefreshRoom(3)" }, host.Calls);
    }

    [Theory]
    [InlineData(0x0000, 1u)]
    [InlineData(0x0400, 2u)]
    [InlineData(0x0800, 0u)]
    [InlineData(0x0c00, 0u)]
    public void SelectorTwoRefreshesNativeRoomRecordAndManagerGroup(ushort roomId, uint managerFlags)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        me[ObjectData.Room] = unchecked((short)roomId);
        context.World.RoomManagerRecordsLoaded = true;
        context.World.PersonRoomMotiveEntries[roomId] = new PersonRoomMotiveEntry(12f, false);

        Assert.Equal(PrimitiveResult.True, RefreshPrimitive.Execute(context, Operand(0, 2)));

        var room = context.World.PersonRoomMotiveEntries[roomId];
        Assert.True(room.NeedsRefresh);
        Assert.Equal(managerFlags, context.World.RoomManagerRefreshFlags);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void SelectorTwoIsANoOpForAnAbsentRoomInALoadedManagerTree()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        context.World.RoomManagerRecordsLoaded = true;
        me[ObjectData.Room] = 77;

        Assert.Equal(PrimitiveResult.True, RefreshPrimitive.Execute(context, Operand(0, 2)));

        Assert.Equal(0u, context.World.RoomManagerRefreshFlags);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void SelectorTwoSkipsMinusFiveAsNoRoom()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me[ObjectData.Room] = -5;
        Assert.Equal(PrimitiveResult.True, RefreshPrimitive.Execute(context, Operand(0, 2)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void SelectorTwoPassesOtherNegativeRoomsAsTheirUnsignedValue()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me[ObjectData.Room] = -1;
        RefreshPrimitive.Execute(context, Operand(0, 2));
        Assert.Equal(new[] { "RefreshRoom(65535)" }, host.Calls);
    }

    [Fact]
    public void ModeOneReadsTheStackObjectForSelectorTwo()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 5);
        me[ObjectData.Room] = 1;
        var stack = context.World.Add(new XObject(5, PrimitiveTestWorld.Definition()));
        stack[ObjectData.Room] = 4;
        RefreshPrimitive.Execute(context, Operand(1, 2));
        Assert.Equal(new[] { "RefreshRoom(4)" }, host.Calls);
    }

    [Fact]
    public void MissingStackObjectRaisesNoStackObjectPresent()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 77);
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => RefreshPrimitive.Execute(context, Operand(1, 0))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ModeOtherThanZeroOrOneRaisesBadParameterBeforeTheSubjectIsLookedUp()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 77);
        Assert.Equal(SimulationError.BadParameterToUpdatePrimitive, Fails(() => RefreshPrimitive.Execute(context, Operand(2, 0))));
        Assert.Equal(SimulationError.BadParameterToUpdatePrimitive, Fails(() => RefreshPrimitive.Execute(context, Operand(-1, 0))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void MissingStackObjectIsCheckedBeforeTheSelector()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 77);
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => RefreshPrimitive.Execute(context, Operand(1, 3))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void SelectorOtherThanZeroToTwoRaisesBadParameter()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Equal(SimulationError.BadParameterToUpdatePrimitive, Fails(() => RefreshPrimitive.Execute(context, Operand(0, 3))));
        Assert.Equal(SimulationError.BadParameterToUpdatePrimitive, Fails(() => RefreshPrimitive.Execute(context, Operand(0, -1))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ModeZeroNeverReadsTheStackObject()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 77);
        Assert.Equal(PrimitiveResult.True, RefreshPrimitive.Execute(context, Operand(0, 1)));
        Assert.Equal(new[] { "SendNotice(242,100)" }, host.Calls);
    }
}
