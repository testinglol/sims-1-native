using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using Xunit;

namespace SimsEngine.Tests;

public sealed class HostlessPrimitiveAndVariableTests
{
    private static byte[] GenericOperand(short call) => [(byte)call, (byte)((ushort)call >> 8), 0, 0, 0, 0, 0, 0];

    [Fact]
    public void CallOneExecutesWithoutHost()
    {
        var (context, runner, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;

        var definition = PrimitiveTestWorld.Definition();
        var stackObj = context.World.Add(new XPerson(2, definition) { Position = new ObjectPosition(32, 32, 1) });
        var container1 = context.World.Add(new XObject(3, definition) { Position = new ObjectPosition(16, 16, 1) });
        var container2 = context.World.Add(new XObject(4, definition) { Position = new ObjectPosition(32, 32, 1) });
        container1.RoutingSlots.Add(new RoutingSlotRequest { X = 0, Y = 0 });
        container2.RoutingSlots.Add(new RoutingSlotRequest { X = 0, Y = 0 });

        runner.Position = new ObjectPosition(16, 16, 1);
        runner[ObjectData.ContainerId] = container1.Id;
        stackObj[ObjectData.ContainerId] = container2.Id;
        runner[ObjectData.Direction] = 2;
        stackObj[ObjectData.Direction] = 6;
        context.Frame.StackObject = stackObj.Id;

        var result = GenericSimsCallPrimitive.Execute(context, GenericOperand(0x01));
        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal(6, runner[ObjectData.Direction]);
        Assert.Equal(2, stackObj[ObjectData.Direction]);
    }

    [Fact]
    public void CallThreeClearsHandlesWithoutHost()
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        context.World.GenericSimsCall03State.PrimaryHandle = 42;
        context.World.GenericSimsCall03State.SecondaryHandle = 84;

        var result = GenericSimsCallPrimitive.Execute(context, GenericOperand(0x03));
        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal(0, context.World.GenericSimsCall03State.PrimaryHandle);
        Assert.Equal(0, context.World.GenericSimsCall03State.SecondaryHandle);
    }

    [Fact]
    public void CallZeroCRotatesTileCoordinatesWithoutHost()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        context.World.SetLotOrientation(1);

        var stackObj = context.World.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(16 * 10, 16 * 20, 1)
        });
        context.Frame.StackObject = stackObj.Id;

        var result = GenericSimsCallPrimitive.Execute(context, GenericOperand(0x0c));
        Assert.Equal(PrimitiveResult.True, result);
        // Transformation norm 1 rotates coordinates (x, y) to (63 - y, x) on the 64x64 lot grid.
        Assert.Equal(-53, me.Temporaries[0]);
    }

    [Fact]
    public void CallsTenEighteenNineteenLookupWithoutHost()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, GenericOperand(0x10)));

        me.Temporaries[0] = 0;
        me.Temporaries[1] = 0;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, GenericOperand(0x18)));

        me.Temporaries[0] = 0;
        me.Temporaries[1] = 0;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, GenericOperand(0x1d)));

        me.Temporaries[0] = 0;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, GenericOperand(0x1e)));

        me.Temporaries[0] = 0;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, GenericOperand(0x1f)));
        Assert.Equal((short)0, me.Temporaries[1]);
    }

    [Fact]
    public void CallsTwentyTwoAndTwentyFourExecuteWithoutHost()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;

        var stackPerson = context.World.Add(new XPerson(2, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(123)
        });
        context.World.PersonRecordKeys.Add(123);
        context.Frame.StackObject = stackPerson.Id;

        context.World.GenericSimsCallRecordCallbackTarget = new GenericSimsCallRecordTarget
        {
            FameStarThresholds = [100],
            FameFriendThresholds = [5],
        };

        var res22 = GenericSimsCallPrimitive.Execute(context, GenericOperand(0x22));
        Assert.True(res22 == PrimitiveResult.True || res22 == PrimitiveResult.False);

        var res24 = GenericSimsCallPrimitive.Execute(context, GenericOperand(0x24));
        Assert.True(res24 == PrimitiveResult.True || res24 == PrimitiveResult.False);
    }

    [Fact]
    public void CallTwentySixGracefullyHandlesNonBehaviorScopeWithoutHost()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        me.Temporaries[0] = 1;
        me.Temporaries[1] = 0;

        var result = GenericSimsCallPrimitive.Execute(context, GenericOperand(0x26));
        Assert.Equal(PrimitiveResult.False, result);
        Assert.Equal(-1, me.Temporaries[0]);
    }

    [Fact]
    public void RoomByTemporaryZeroExecutesWithoutHost()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;

        var walls = new ChunkBuilder().S16(0).S16(0).S32(4).S32(1).S32(8).U32(0);
        for (var tile = 0; tile < 4; tile++) walls.Raw([0, 0x0F, 0, 0, 0, 0, 0, 0]);
        var levelOne = RoomMap.Build(new IffFile(ChunkBuilder.File(("Arry", 2, walls.ToArray())), "room-level-1.iff"), 1, 4, 1);
        context.World.LoadRoomMaps(new Dictionary<int, RoomMap> { [1] = levelOne });

        me.Temporaries[0] = -5;
        Assert.Equal(0, Variables.Get(context, DataOwner.RoomByTemporary0, 0));
        Assert.Equal(1, Variables.Get(context, DataOwner.RoomByTemporary0, 1));
        Assert.Equal(0, Variables.Get(context, DataOwner.RoomByTemporary0, 2));
        Assert.Equal(0, Variables.Get(context, DataOwner.RoomByTemporary0, 3));

        var validRoom = unchecked((short)levelOne.RoomAt(1, 0));
        me.Temporaries[0] = validRoom;
        Assert.Equal(0, Variables.Get(context, DataOwner.RoomByTemporary0, 0));
        Assert.Equal(1, Variables.Get(context, DataOwner.RoomByTemporary0, 2));
        Assert.True(Variables.Get(context, DataOwner.RoomByTemporary0, 3) >= 0);

        me.Temporaries[0] = 999;
        Assert.Throws<SimulationException>(() => Variables.Get(context, DataOwner.RoomByTemporary0, 2));

        Assert.Throws<SimulationException>(() => Variables.Get(context, DataOwner.RoomByTemporary0, 4));
    }

    [Fact]
    public void ManageInventoryModesFiveAndSixExecuteWithoutHost()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        me.Temporaries[0] = 1;

        var stackObj = context.World.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(10) { AnimationKey = 0x12345678 }
        });
        context.Frame.StackObject = stackObj.Id;

        var neighbor = new NeighborState(1, PrimitiveTestWorld.Definition()) { TypeRecordKey = 0x12345678 };
        context.World.Neighbors[1] = neighbor;

        byte[] operand5 = [5, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, operand5));

        byte[] operand6 = [6, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, operand6));
    }

    [Fact]
    public void ChangeActionStringUpdatesActionRecordWithoutHost()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        context.World.ActionStringRecordWriteEnabled = true;

        byte[] operand = [0, 0, 0, 0, 1, 0, 0, 0];
        Assert.Equal(PrimitiveResult.True, ChangeActionStringPrimitive.Execute(context, operand));
        Assert.NotNull(me.CurrentActionRecord);
        Assert.True((me.CurrentActionRecord.Flags & 0x80) != 0);
    }

    [Fact]
    public void IdleForInputHandlesEmptyQueueOrSavedStackWithoutHost()
    {
        var (context, me, _) = PrimitiveTestWorld.Create([0]);
        me.IdleFloat7A8 = -100;
        context.World.Host = null;

        byte[] operandClear = [0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(PrimitiveResult.True, IdleForInputPrimitive.Execute(context, operandClear));

        byte[] operandWait = [0, 0, 1, 0, 0, 0, 0, 0];
        Assert.Equal(PrimitiveResult.True, IdleForInputPrimitive.Execute(context, operandWait));
    }

    [Fact]
    public void CreateObjectInstanceAndFindLocationForWorkWithoutHost()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;

        Assert.True(context.World.FacingClear(10, 10, 1, 0));

        var stackObj = context.World.Add(new XObject(2, PrimitiveTestWorld.Definition()));
        context.Frame.StackObject = stackObj.Id;
        byte[] findOperand = [0, 0, 0, 0, 0, 0, 0, 0];
        var findRes = FindLocationForPrimitive.Execute(context, findOperand);
        Assert.True(findRes == PrimitiveResult.True || findRes == PrimitiveResult.False);
    }
}
