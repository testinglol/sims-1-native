using SimsEngine.Simulation;
using SimsEngine.Formats;
using SimsEngine.World;
using Xunit;

namespace SimsEngine.Tests;

public sealed class GoToRelativePositionPrimitiveTests
{
    // GoToRelativePosition operand encodes the signed direction at byte 2, signed facing at byte 3, and
    // route flags at byte 6.
    private static byte[] Operand(sbyte direction, sbyte facing, byte flags = 0) =>
        [0xEE, 0xEE, (byte)direction, (byte)facing, 0xEE, 0xEE, flags, 0xEE];

    private static (TreeContext Context, PrimitiveTestHost Host) World()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        return (context, host);
    }

    [Fact]
    public void WaitingFrameContinuesItsRouteWithNoTarget()
    {
        var (context, host) = World();
        context.Frame.Waiting = true;
        host.RouteAnswer = PrimitiveResult.Again;
        Assert.Equal(PrimitiveResult.Again, GoToRelativePositionPrimitive.Execute(context, Operand(2, 3)));
        Assert.Equal(new[] { "Route(100,null)" }, host.Calls);
    }

    [Fact]
    public void BuildsTheSlotFromTheDefaultsWithDirectionTwoAndFacingThree()
    {
        var (context, host) = World();
        Assert.Equal(PrimitiveResult.True, GoToRelativePositionPrimitive.Execute(context, Operand(2, 3)));
        Assert.Equal(new[] { "Route(100,slot)" }, host.Calls);
        var slot = host.Routes[0]!;
        Assert.Equal(1, slot.Standing);
        Assert.Equal((16, 16, 16), (slot.MinProximity, slot.MaxProximity, slot.OptimalProximity));
        Assert.Equal(0x4004u, slot.Flags);
        Assert.Equal(3, slot.Facing);
        Assert.Equal(-1, slot.SnapTargetSlot);
        Assert.Equal(0.1875f, slot.Gradient);
        Assert.Equal(16, slot.Resolution);
    }

    [Theory]
    [InlineData(0, 0x01)]
    [InlineData(2, 0x04)]
    [InlineData(4, 0x10)]
    [InlineData(6, 0x40)]
    [InlineData(1, 0x05)]
    [InlineData(3, 0x14)]
    [InlineData(5, 0x50)]
    [InlineData(7, 0x41)]
    public void EachDirectionSetsItsBitOrTheTwoBitsEitherSide(sbyte direction, uint bits)
    {
        var (context, host) = World();
        GoToRelativePositionPrimitive.Execute(context, Operand(direction, 0));
        Assert.Equal(bits, host.Routes[0]!.Flags & 0xff);
    }

    [Fact]
    public void MinusTwoDirectionClearsTheDirectionBits()
    {
        var (context, host) = World();
        // Operand byte 6 maps bit 2 to route flag 0x8000 and an unset bit 1 to route flag 0x4000 while
        // clearing direction bits.
        GoToRelativePositionPrimitive.Execute(context, Operand(-2, 0, flags: 4));
        Assert.Equal(0xC000u, host.Routes[0]!.Flags);
    }

    [Fact]
    public void OtherDirectionSetsTheFourSidesAndTheDefaultMaximumProximity()
    {
        var (context, host) = World();
        GoToRelativePositionPrimitive.Execute(context, Operand(-1, 0));
        var slot = host.Routes[0]!;
        Assert.Equal(0x55u, slot.Flags & 0xff);
        Assert.Equal((16, 32, 16), (slot.MinProximity, slot.MaxProximity, slot.OptimalProximity));
    }

    [Theory]
    [InlineData(0, 0x4000u)]
    [InlineData(2, 0u)]
    [InlineData(4, 0xC000u)]
    [InlineData(6, 0x8000u)]
    public void FlagBitsOfByteSixSetTheTwoRouteFlags(byte flags, uint expected)
    {
        var (context, host) = World();
        // Specifying direction -2 clears directional bits from the route flags, leaving only mode flags
        // intact.
        GoToRelativePositionPrimitive.Execute(context, Operand(-2, 0, flags));
        Assert.Equal(expected, host.Routes[0]!.Flags);
    }

    [Theory]
    [InlineData(-2, -2)]
    [InlineData(-1, -3)]
    [InlineData(0, 0)]
    [InlineData(7, 7)]
    public void FacingMinusTwoAndMinusOneGiveTheSpecialFacings(sbyte facing, int expected)
    {
        var (context, host) = World();
        GoToRelativePositionPrimitive.Execute(context, Operand(2, facing));
        Assert.Equal(expected, host.Routes[0]!.Facing);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(-3)]
    public void FacingOutsideTheRangeAnswersFalseWithoutARoute(sbyte facing)
    {
        var (context, host) = World();
        Assert.Equal(PrimitiveResult.False, GoToRelativePositionPrimitive.Execute(context, Operand(2, facing)));
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(PrimitiveResult.Yield)]
    [InlineData(PrimitiveResult.Stop)]
    [InlineData(PrimitiveResult.Again)]
    public void TheRouteAnswerPassesThroughUnchanged(PrimitiveResult answer)
    {
        var (context, host) = World();
        host.RouteAnswer = answer;
        Assert.Equal(answer, GoToRelativePositionPrimitive.Execute(context, Operand(2, 3)));
    }

    [Fact]
    public void NativeRelativePositionRoutesFromTheStackObjectsFacingAndIgnoresItsAuthoredSlots()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        context.World.Host = null;
        GoToRoutingSlotPrimitiveTests.AddWalkClips(context.World);
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        person.TypeRecord = new TypeRecord(person.Id) { FootprintMask = 0x4444 };
        person[ObjectData.AllowedHeightFlags] = 1;
        var start = new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1);
        context.World.SetObjectPosition(person, start);
        var anchor = context.World.Add(new XObject(2, PrimitiveTestWorld.Definition()));
        var anchorPosition = new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1);
        context.World.SetObjectPosition(anchor, anchorPosition);
        anchor[ObjectData.Direction] = 2;
        anchor.RoutingSlots.Add(new RoutingSlotRequest { X = 32, Standing = 1 });

        var result = GoToRelativePositionPrimitive.Execute(context, Operand(2, 3));
        // Object callbacks and animation updates execute in separate scheduler passes.
        for (var tick = 0; tick < 1024 && result is not (PrimitiveResult.True or PrimitiveResult.False); tick++)
        {
            if (result == PrimitiveResult.Stop) context.World.TickAvatarAnimations(person);
            result = GoToRelativePositionPrimitive.Execute(context, Operand(2, 3));
        }

        var expected = anchorPosition with { Y = anchorPosition.Y + 16 };
        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal(expected, person.Position);
        Assert.Equal((short)0, person[ObjectData.ParentId]);
        Assert.Equal((short)0, person[ObjectData.SlotNumber]);
        Assert.Equal((short)-1, person[ObjectData.SlotHierNumber]);
        Assert.Empty(person.Slots);
    }

    [Fact]
    public void NativeRelativePositionReturnsFalseWhenStackObjectMissingOrRoutingDisabled()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 0);
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.False, GoToRelativePositionPrimitive.Execute(context, Operand(2, 3)));

        var (disabledContext, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        disabledContext.World.Host = null;
        disabledContext.World.Add(new XObject(2, PrimitiveTestWorld.Definition()));
        disabledContext.World.RoutingEnabled = false;
        Assert.Equal(PrimitiveResult.False, GoToRelativePositionPrimitive.Execute(disabledContext, Operand(2, 3)));
    }

    private sealed class RelativeRouteStack : TreeStack
    {
        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction) =>
            PrimitiveResult.True;
    }

    [Fact]
    public void SeatedSimPushesContainerStandTreeBeforeRelativePositionAndDirectPositionRoutes()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        var world = context.World;
        world.Host = null;
        GoToRoutingSlotPrimitiveTests.AddWalkClips(world);
        world.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        person.TypeRecord = new TypeRecord(person.Id) { FootprintMask = 0x4444 };
        person[ObjectData.AllowedHeightFlags] = 1;
        var start = new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1);
        world.SetObjectPosition(person, start);
        var anchor = world.Add(new XObject(2, PrimitiveTestWorld.Definition()));
        world.SetObjectPosition(anchor, new ObjectPosition(8 * 16 + 8, 5 * 16 + 8, 1));
        anchor[ObjectData.Direction] = 2;

        var standChunk = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(1).U8(0).U8(0).U32(0)
            .U16(0).U8(BhavInstruction.ReturnTrue).U8(BhavInstruction.ReturnFalse).Raw(new byte[8]).InFile("BHAV");
        var standTree = Bhav.Read(standChunk.File, standChunk.Chunk);
        var seatScope = new TreeTable((4097, standTree));
        var functions = new ushort[0x1c];
        functions[0x1a] = 4097;
        functions[0x1b] = 4097;
        var seat = world.Add(new XObject(3, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(3) { FunctionTable = functions, BehaviorScope = seatScope },
        });
        world.SetObjectPosition(seat, start);
        person[PersonData.Sitting] = 1;
        person[ObjectData.ContainerId] = seat.Id;

        var stack = new RelativeRouteStack();
        stack.Frames.Add(context.Frame);
        var treeContext = new TreeContext(world, person, context.Frame) { Stack = stack };

        var first = GoToRelativePositionPrimitive.Execute(treeContext, Operand(2, 3));
        Assert.Equal(PrimitiveResult.Again, first);
        Assert.Equal(2, stack.Frames.Count);
        Assert.Equal(4097, stack.Frames[^1].TreeId);
        Assert.Equal(person[PersonData.RouteEntryFlags], stack.Frames[^1].Parameters[0]);

        // Routing directly to a position pushes the Stand interaction with parameter 0 and clears route
        // entry flags to 0.
        person.NativeRoute = null;
        context.Frame.Waiting = false;
        stack.Frames.RemoveAt(stack.Frames.Count - 1);
        person[PersonData.RouteEntryFlags] = 16;

        var directFirst = RouteExecutor.StartDirectPosition(world, person, context.Frame,
            new ObjectPosition(8 * 16 + 8, 6 * 16 + 8, 1), stack);
        Assert.Equal(PrimitiveResult.Again, directFirst);
        Assert.Equal(2, stack.Frames.Count);
        Assert.Equal(4097, stack.Frames[^1].TreeId);
        Assert.Equal((short)0, person[PersonData.RouteEntryFlags]);
        Assert.Equal((short)0, stack.Frames[^1].Parameters[0]);
    }

    private static RoomMap OpenRoom()
    {
        const int size = 16;
        var walls = new ChunkBuilder().S16(0).S16(0).S32(size).S32(size).S32(8).U32(0);
        var terrain = new ChunkBuilder().S16(0).S16(0).S32(size).S32(size).S32(1).U32(0);
        for (var tile = 0; tile < size * size; tile++)
        {
            walls.Raw(new byte[8]);
            terrain.U8(0x10);
        }
        var lot = new IffFile(ChunkBuilder.File(("Arry", 2, walls.ToArray()), ("Arry", 9, terrain.ToArray())), "relative-position.iff");
        return RoomMap.Build(lot, 1, size, size);
    }
}
