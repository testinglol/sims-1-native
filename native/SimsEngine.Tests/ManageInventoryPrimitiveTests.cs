using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class ManageInventoryPrimitiveTests
{
    private const int Type = 0x300;

    private static byte[] Operand(byte mode, byte key1 = 7, ushort flags = 0, int type = Type) =>
        [mode, key1, (byte)flags, (byte)(flags >> 8),
            (byte)type, (byte)(type >> 8), (byte)(type >> 16), (byte)(type >> 24)];

    private static (TreeContext Context, PrimitiveTestHost Host, XPerson Me) Setup(short stackObject = 0)
    {
        var host = new PrimitiveTestHost();
        var world = new SimulationWorld { Host = host };
        var me = world.Add(new XPerson(100, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        var frame = new StackFrame(stackObject, [], [0, 0]);
        return (new TreeContext(world, me, frame), host, me);
    }

    [Fact]
    public void NonPerson_RaisesInvalidOperation()
    {
        var host = new PrimitiveTestHost();
        var world = new SimulationWorld { Host = host };
        var me = world.Add(new XObject(100, PrimitiveTestWorld.Definition()));
        var context = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));
        var error = Assert.Throws<SimulationException>(() => ManageInventoryPrimitive.Execute(context, Operand(0)));
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, error.Error);
    }

    [Fact]
    public void Mode0_AddsAnEntryAndNotifies()
    {
        var (context, host, me) = Setup();
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, Operand(0)));
        Assert.Equal([(7, Type, (short)1)],
            context.World.NeighborInventory(me[PersonData.NeighborId]).Entries.Select(entry => (entry.Key1, entry.Key2, entry.Count)));
        Assert.Equal([], host.Calls);
    }

    [Fact]
    public void Mode0_CountComesFromTheFlaggedTemporary()
    {
        var (context, host, me) = Setup();
        me.Temporaries[2] = 5;
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, Operand(0, flags: 0x0a)));
        Assert.Equal((short)5, context.World.NeighborInventory(me[PersonData.NeighborId]).Entries[0].Count);
        Assert.Equal([], host.Calls);
    }

    [Fact]
    public void Mode1_RemovesOneTokenFromAnEntry()
    {
        var (context, host, me) = Setup();
        context.World.NeighborInventory(me[PersonData.NeighborId]).Add(7, Type, 2);
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, Operand(1)));
        Assert.Equal((short)1, Assert.Single(context.World.NeighborInventory(me[PersonData.NeighborId]).Entries).Count);
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, Operand(1)));
        Assert.Empty(context.World.NeighborInventory(me[PersonData.NeighborId]).Entries);
        Assert.Equal(PrimitiveResult.False, ManageInventoryPrimitive.Execute(context, Operand(1)));
        Assert.Equal([], host.Calls);
    }

    [Fact]
    public void Mode2_AdjustsByTemporaries()
    {
        var (context, _, me) = Setup();
        context.World.NeighborInventory(me[PersonData.NeighborId]).Add(7, Type, 10);
        context.World.NeighborInventory(me[PersonData.NeighborId]).Add(8, Type, 4);
        me.Temporaries[2] = 3;
        me.Temporaries[3] = 1;
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, Operand(2, flags: 0x030a)));
        Assert.Equal((short)10, context.World.NeighborInventory(me[PersonData.NeighborId]).Entries[0].Count);
        Assert.Equal((short)1, context.World.NeighborInventory(me[PersonData.NeighborId]).Entries[1].Count);
        me.Temporaries[2] = 1;
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, Operand(2, flags: 0x030a)));
        Assert.Single(context.World.NeighborInventory(me[PersonData.NeighborId]).Entries);
    }

    [Fact]
    public void Mode2_OutOfRangeIndex_AnswersFalse()
    {
        var (context, _, me) = Setup();
        context.World.NeighborInventory(me[PersonData.NeighborId]).Add(7, Type, 4);
        me.Temporaries[3] = 5;
        Assert.Equal(PrimitiveResult.False, ManageInventoryPrimitive.Execute(context, Operand(2, flags: 0x0300)));
    }

    [Fact]
    public void Mode3_FindsCountAndIndex()
    {
        var (context, host, me) = Setup();
        context.World.NeighborInventory(me[PersonData.NeighborId]).Add(7, Type, 4);
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, Operand(3, flags: 0x1020)));
        Assert.Equal((short)4, me.Temporaries[1]);
        Assert.Equal((short)0, me.Temporaries[2]);
        Assert.Equal([], host.Calls);
        Assert.Equal(PrimitiveResult.False, ManageInventoryPrimitive.Execute(context, Operand(3, key1: 9, flags: 0x1020)));
    }

    [Fact]
    public void Mode3_UpdatesInventoryWithoutAHost()
    {
        var (context, _, me) = Setup();
        context.World.Host = null;
        context.World.NeighborInventory(me[PersonData.NeighborId]).Add(7, Type, 4);

        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, Operand(3, flags: 0x1020)));

        Assert.True(context.World.NeighborInventory(me[PersonData.NeighborId]).Changed);
        Assert.Equal((short)4, me.Temporaries[1]);
    }

    [Fact]
    public void Mode4_WalksEntriesWithTheCursor()
    {
        var (context, _, me) = Setup();
        context.World.NeighborInventory(me[PersonData.NeighborId]).Add(7, 1, 5);
        context.World.NeighborInventory(me[PersonData.NeighborId]).Add(7, 2, 6);
        me.Temporaries[2] = -1;
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, Operand(4, flags: 0x1220)));
        Assert.Equal((short)5, me.Temporaries[1]);
        Assert.Equal((short)0, me.Temporaries[2]);
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, Operand(4, flags: 0x1220)));
        Assert.Equal((short)6, me.Temporaries[1]);
        Assert.Equal((short)1, me.Temporaries[2]);
        Assert.Equal(PrimitiveResult.False, ManageInventoryPrimitive.Execute(context, Operand(4, flags: 0x1220)));
        Assert.Equal((short)0, me.Temporaries[1]);
        Assert.Equal((short)-1, me.Temporaries[2]);
    }

    [Fact]
    public void Mode4_ExactSecondKey()
    {
        var (context, _, me) = Setup();
        context.World.NeighborInventory(me[PersonData.NeighborId]).Add(7, 9, 5);
        context.World.NeighborInventory(me[PersonData.NeighborId]).Add(7, Type, 8);
        me.Temporaries[0] = -1;
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, Operand(4, flags: 0x1021)));
        Assert.Equal((short)8, me.Temporaries[1]);
        Assert.Equal((short)1, me.Temporaries[2]);
        me.Temporaries[0] = 1;
        Assert.Equal(PrimitiveResult.False, ManageInventoryPrimitive.Execute(context, Operand(4, flags: 0x1021)));
    }

    [Fact]
    public void Mode5_ReplacesKeyOneEntriesWithTheTemporaryNeighborGuid()
    {
        var (context, host, me) = Setup();
        context.World.NeighborWalkOrder.Add(new NeighborState(1, PrimitiveTestWorld.Definition()) { TypeRecordKey = 0x1234 });
        me.Temporaries[0] = 1;
        context.World.NeighborInventory(me[PersonData.NeighborId]).Add(1, 99, 2);
        context.World.NeighborInventory(me[PersonData.NeighborId]).Add(3, 98, 1);
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, Operand(5)));
        Assert.Equal([(3, 98, (short)1), (1, 0x1234, (short)1)],
            context.World.NeighborInventory(me[PersonData.NeighborId]).Entries.Select(entry => (entry.Key1, entry.Key2, entry.Count)));
        Assert.Equal([], host.Calls);
    }

    [Fact]
    public void Mode6_UsesKey3()
    {
        var (context, host, me) = Setup();
        context.World.NeighborWalkOrder.Add(null);
        context.World.NeighborWalkOrder.Add(new NeighborState(2, PrimitiveTestWorld.Definition()) { TypeRecordKey = 77 });
        me.Temporaries[0] = 2;
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, Operand(6)));
        Assert.Equal([(3, 77, (short)1)],
            context.World.NeighborInventory(me[PersonData.NeighborId]).Entries.Select(entry => (entry.Key1, entry.Key2, entry.Count)));
        Assert.Equal([], host.Calls);
    }

    [Fact]
    public void Mode5_MissingNeighborAnswersFalse()
    {
        var (context, host, _) = Setup();
        Assert.Equal(PrimitiveResult.False, ManageInventoryPrimitive.Execute(context, Operand(5)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void UnknownMode_NotifiesAndAnswersTrue()
    {
        var (context, host, _) = Setup();
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, Operand(9)));
        Assert.Equal([], host.Calls);
    }

    [Fact]
    public void Flag13_UsesTheOtherPersonsInventory()
    {
        var (context, host, me) = Setup();
        var other = context.World.Add(new XPerson(101, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        other[PersonData.NeighborId] = 5;
        me.Temporaries[4] = 101;
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, Operand(0, flags: 0x2000)));
        Assert.Empty(context.World.NeighborInventory(me[PersonData.NeighborId]).Entries);
        Assert.Single(context.World.NeighborInventory(5).Entries);
        Assert.Equal([], host.Calls);
    }

    [Fact]
    public void Flag13_NonPerson_IsError28()
    {
        var (context, _, me) = Setup();
        context.World.Add(new XObject(101, PrimitiveTestWorld.Definition()));
        me.Temporaries[4] = 101;
        var error = Assert.Throws<SimulationException>(() => ManageInventoryPrimitive.Execute(context, Operand(0, flags: 0x2000)));
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, error.Error);
    }

    [Fact]
    public void ZeroType_UsesTheStackObjectsKey()
    {
        var (context, host, me) = Setup(stackObject: 5);
        var stack = new XObject(5, PrimitiveTestWorld.Definition()) { TypeRecord = new TypeRecord(9) { AnimationKey = 0x400 } };
        context.World.Add(stack);
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, Operand(0, type: 0)));
        Assert.Single(context.World.NeighborInventory(me[PersonData.NeighborId]).Entries);
        Assert.Equal([], host.Calls);
    }

    [Fact]
    public void ZeroType_FallsBackToStackObjectsTypeRecordKeyWhenAnimationKeyIsZero()
    {
        var (context, _, me) = Setup(stackObject: 5);
        var stack = new XObject(5, PrimitiveTestWorld.Definition()) { TypeRecord = new TypeRecord(0x12345678) };
        context.World.Add(stack);
        Assert.Equal(PrimitiveResult.True, ManageInventoryPrimitive.Execute(context, Operand(0, type: 0)));
        var entry = Assert.Single(context.World.NeighborInventory(me[PersonData.NeighborId]).Entries);
        Assert.Equal(0x12345678, entry.Key2);
    }

    [Fact]
    public void ZeroType_WithoutAStackObject_Throws()
    {
        var (context, _, _) = Setup();
        Assert.Throws<InvalidOperationException>(() => ManageInventoryPrimitive.Execute(context, Operand(0, type: 0)));
    }

    [Fact]
    public void ZeroType_WithoutATypeRecord_Throws()
    {
        var (context, _, _) = Setup(stackObject: 5);
        context.World.Add(new XObject(5, PrimitiveTestWorld.Definition()));
        Assert.Throws<InvalidOperationException>(() => ManageInventoryPrimitive.Execute(context, Operand(0, type: 0)));
    }

    [Theory]
    [InlineData((byte)5, 1)]
    [InlineData((byte)6, 3)]
    public void ManageInventoryModesFiveAndSix_WithZeroStackObject_SucceedsWithoutThrowing(byte mode, int expectedKey)
    {
        var (context, _, me) = Setup(stackObject: 0);
        context.World.NeighborWalkOrder.Add(new NeighborState(1, []) { TypeRecordKey = 0x55aa55aa });
        me.Temporaries[0] = 1;

        byte[] operand = [mode, 0, 0, 0, 0, 0, 0, 0];
        var result = ManageInventoryPrimitive.Execute(context, operand);

        Assert.Equal(PrimitiveResult.True, result);
        var entries = context.World.NeighborInventory(me[PersonData.NeighborId]).Entries;
        Assert.Single(entries);
        Assert.Equal(expectedKey, entries[0].Key1);
        Assert.Equal(0x55aa55aa, entries[0].Key2);
        Assert.Equal(1, entries[0].Count);
    }

    [Theory]
    [InlineData((byte)5)]
    [InlineData((byte)6)]
    public void ManageInventoryModesFiveAndSix_WithZeroStackObjectAndMissingNeighbor_ReturnsFalseWithoutThrowing(byte mode)
    {
        var (context, _, me) = Setup(stackObject: 0);
        me.Temporaries[0] = 999;

        byte[] operand = [mode, 0, 0, 0, 0, 0, 0, 0];
        var result = ManageInventoryPrimitive.Execute(context, operand);

        Assert.Equal(PrimitiveResult.False, result);
    }
}
