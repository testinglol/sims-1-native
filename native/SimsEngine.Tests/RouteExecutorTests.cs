using System.Text;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using Xunit;

namespace SimsEngine.Tests;

public sealed class RouteExecutorTests
{
    private sealed class RouteTestStack : TreeStack
    {
        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction) =>
            PrimitiveResult.True;
    }

    private static RoomMap OpenRoom(int width = 16, int height = 16)
    {
        var floor = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(Enumerable.Repeat((byte)1, width * height).ToArray());
        var walls = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0)
            .Raw(new byte[width * height * 8]);
        var water = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(Enumerable.Repeat((byte)0x10, width * height).ToArray());
        return RoomMap.Build(new IffFile(ChunkBuilder.File(
            ("Arry", 1, floor.ToArray()),
            ("Arry", 2, walls.ToArray()),
            ("Arry", 9, water.ToArray())), "route-test.iff"), 1, width, height);
    }

    private static RoomMap TwoDisconnectedRooms(int width = 16, int height = 16, int splitX = 8)
    {
        var wallBytes = new byte[width * height * 8];
        for (var y = 0; y < height; y++)
            wallBytes[(y * width + splitX) * 8] = 0x01;
        var floor = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(Enumerable.Repeat((byte)1, width * height).ToArray());
        var walls = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0)
            .Raw(wallBytes);
        var water = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(Enumerable.Repeat((byte)0x10, width * height).ToArray());
        return RoomMap.Build(new IffFile(ChunkBuilder.File(
            ("Arry", 1, floor.ToArray()),
            ("Arry", 2, walls.ToArray()),
            ("Arry", 9, water.ToArray())), "two-rooms-test.iff"), 1, width, height);
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

    private static XObject DestinationObject(short id)
    {
        var definition = PrimitiveTestWorld.Definition(0);
        definition[(int)ObjdField.Guid1] = 0x07c4;
        definition[(int)ObjdField.Guid2] = 0;
        return new XObject(id, definition)
        {
            TypeRecord = new TypeRecord(0x7c4) { Interactions = MoveAsideInteractions() },
        };
    }

    private static BehaviorScope ScopeWithFailureFeedbackTree(ushort feedbackTreeId)
    {
        var expression = PrimitiveTestWorld.Expression(0, 0, (byte)ExpressionOperator.EqualTo);
        var body = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(4).U8(0).U8(0).U32(0)
            .U16(2).U8(BhavInstruction.ReturnTrue).U8(BhavInstruction.ReturnFalse).Raw(expression).ToArray();
        var bytes = ChunkBuilder.File(("BHAV", feedbackTreeId, body));
        Encoding.Latin1.GetBytes("Route failure feedback").CopyTo(bytes, IffFile.HeaderSize + 12);
        var file = new IffFile(bytes, "feedback-test.iff");
        return new BehaviorScope(file, file, null);
    }

    [Fact]
    public void AskPeopleOnRouteToMoveRequiresSittingZeroAndUsesRequesterPriority()
    {
        // FUN_0045bb94 (0045bb94.c:41) requires PersonData.Sitting == 0, and FUN_0045c09a (0045c09a.c:180-181)
        // passes the routing Sim's priority when queueing MoveOutOfWay.
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        var world = context.World;
        world.Host = null;
        world.LotRoomMaps[1] = OpenRoom();
        GoToRoutingSlotPrimitiveTests.AddWalkClips(world);

        short nextDynamicId = 500;
        world.ObjectFactory = guid => guid == 0x7c4 ? DestinationObject(nextDynamicId++) : null;

        var mover = (XPerson)context.Me;
        mover.TypeRecord = new TypeRecord(1);
        mover[ObjectData.AllowedHeightFlags] = 1;
        mover[ObjectData.PlacementFlags] = 0x0800;
        mover.ActivePriority = 45;
        world.SetObjectPosition(mover, new ObjectPosition(4 * 16 + 8, 5 * 16 + 8, 1));

        var blocker = world.Add(new XPerson(101, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(1),
        });
        blocker[ObjectData.AllowedHeightFlags] = 1;
        blocker[ObjectData.PlacementFlags] = 0x0800;
        world.SetObjectPosition(blocker, new ObjectPosition(6 * 16 + 8, 5 * 16 + 8, 1));

        var anchor = world.Add(new XObject(2, PrimitiveTestWorld.Definition(0)));
        anchor[ObjectData.Flags] |= 0x10;
        world.SetObjectPosition(anchor, new ObjectPosition(8 * 16 + 8, 5 * 16 + 8, 1));

        var path = new[]
        {
            new RouteExecutor.RouteWaypoint(4, 5, 4 * 16 + 8, 5 * 16 + 8, 4 * 16 + 8, 5 * 16 + 8, false),
            new RouteExecutor.RouteWaypoint(8, 5, 8 * 16 + 8, 5 * 16 + 8, 8 * 16 + 8, 5 * 16 + 8, true),
        };

        // Seated blocker is skipped.
        blocker[PersonData.Sitting] = 1;
        RouteExecutor.AskPeopleOnRouteToMove(world, mover, anchor, path);
        Assert.Equal(blocker.ActionQueueCurrent, blocker.ActionQueueEnd);

        // Seated requester is also skipped.
        blocker[PersonData.Sitting] = 0;
        mover[PersonData.Sitting] = 1;
        RouteExecutor.AskPeopleOnRouteToMove(world, mover, anchor, path);
        Assert.Equal(blocker.ActionQueueCurrent, blocker.ActionQueueEnd);

        // Standing Sims queue MoveOutOfWay with the requester's priority.
        mover[PersonData.Sitting] = 0;
        RouteExecutor.AskPeopleOnRouteToMove(world, mover, anchor, path);
        Assert.NotEqual(blocker.ActionQueueCurrent, blocker.ActionQueueEnd);
        var queued = blocker.ActionQueue[blocker.ActionQueueCurrent & 7];
        Assert.NotNull(queued);
        Assert.Equal(3, queued!.RawDword10);
        Assert.Equal((short)45, queued.Threshold);
    }

    [Fact]
    public void MoveOutOfWaySkipsBlockerWithExistingActiveOrQueuedGoHereActionAtThresholdThree()
    {
        // FUN_0045c02d (0045c02d.c:9-21) & FUN_0045c06f return true when the blocker already has an active
        // or queued interaction on GUID 0x7c4 with threshold/index 3, preventing duplicate MoveOutOfWay pushes.
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = OpenRoom();
        short nextDynamicId = 600;
        world.ObjectFactory = guid => guid == 0x7c4 ? DestinationObject(nextDynamicId++) : null;

        var blocker = world.Add(new XPerson(100, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(1),
        });
        world.SetObjectPosition(blocker, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));

        var terrainDef = PrimitiveTestWorld.Definition(0);
        terrainDef[(int)ObjdField.Guid1] = 0x07c4;
        terrainDef[(int)ObjdField.Guid2] = 0;
        var existingGoHere = world.Add(new XObject(50, terrainDef));

        // Active action with GUID 0x7c4 and Threshold == 3 blocks duplicate MoveOutOfWay.
        blocker.CurrentActionRecord = new ActionQueueSlot
        {
            Owner = existingGoHere,
            Threshold = 3,
        };
        Assert.False(RouteExecutor.MoveOutOfWay(world, blocker, 20));
        Assert.Equal((short)600, nextDynamicId);

        // Queued action with GUID 0x7c4 and Threshold == 3 also blocks duplicate MoveOutOfWay.
        blocker.CurrentActionRecord = null;
        blocker.ActionQueue[0] = new ActionQueueSlot
        {
            Owner = existingGoHere,
            Threshold = 3,
        };
        blocker.ActionQueueCurrent = 0;
        blocker.ActionQueueEnd = 1;
        Assert.False(RouteExecutor.MoveOutOfWay(world, blocker, 20));
        Assert.Equal((short)600, nextDynamicId);
    }

    [Fact]
    public void LocomotionRouteEmitsBeginRouteAndEndRouteSoundEvents()
    {
        // FUN_0045aecf (0045aecf.c:283, 286) emits "begin_route" when entering state 0x0c (BeginFollow)
        // and "end_route" when EndFollow completes in states 9/10/11.
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        var world = context.World;
        world.Host = null;
        world.LotRoomMaps[1] = OpenRoom();
        GoToRoutingSlotPrimitiveTests.AddWalkClips(world);

        var person = (XPerson)context.Me;
        person.TypeRecord = new TypeRecord(1);
        person[ObjectData.AllowedHeightFlags] = 1;
        person[ObjectData.PlacementFlags] = 0x0801;
        world.PlaceObject(person, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));

        var target = world.Add(new XObject(2, PrimitiveTestWorld.Definition(0)));
        target[ObjectData.Flags] = 4;
        world.PlaceObject(target, new ObjectPosition(7 * 16 + 8, 5 * 16 + 8, 1));

        var soundEvents = new List<(string Name, short SourceId)>();
        world.PlaySoundEvent = (name, sourceId, _, _) =>
        {
            soundEvents.Add((name, sourceId));
            return true;
        };

        var result = RouteExecutor.Start(world, person, context.Frame, target, new RoutingSlotRequest(), null);
        Assert.Contains(("begin_route", person.Id), soundEvents);
        Assert.DoesNotContain(("end_route", person.Id), soundEvents);

        for (var step = 0; step < 128 && result == PrimitiveResult.Stop; step++)
        {
            result = RouteExecutor.Continue(world, person, context.Frame, null);
            if (result == PrimitiveResult.Stop)
                world.TickAvatarAnimations(person);
        }

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal((short)0, person[PersonData.RouteResult]);
        Assert.Equal(
            new[] { ("begin_route", person.Id), ("end_route", person.Id) },
            soundEvents);
    }

    [Fact]
    public void RouteFailureSetsPersonDataRouteResultAndOptionallyPushesRouteFailureFeedbackTree()
    {
        // FUN_0045aecf (0045aecf.c:377-406, 495-506) records the route failure reason in PersonData.RouteResult
        // (1 = blocked/no path, 2 = portal failure, 4 = interrupted) and, when slot flag 0x4000 is set,
        // pushes the "Route failure feedback" tree with parameters [failureReason, blockerId, 0, 0].
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        var world = context.World;
        world.Host = null;
        world.LotRoomMaps[1] = TwoDisconnectedRooms();
        GoToRoutingSlotPrimitiveTests.AddWalkClips(world);

        var person = (XPerson)context.Me;
        person.TypeRecord = new TypeRecord(1);
        person[ObjectData.AllowedHeightFlags] = 1;
        person[ObjectData.PlacementFlags] = 0x0800;
        person[ObjectData.PrimitiveResultId] = 77;
        world.SetObjectPosition(person, new ObjectPosition(3 * 16 + 8, 5 * 16 + 8, 1));

        var feedbackScope = ScopeWithFailureFeedbackTree(4100);
        var target = world.Add(new XObject(2, PrimitiveTestWorld.Definition(0))
        {
            TypeRecord = new TypeRecord(2) { BehaviorScope = feedbackScope },
        });
        // Place target out of bounds for candidate selection when testing no-candidate failure:
        world.SetObjectPosition(target, new ObjectPosition(3 * 16 + 8, 5 * 16 + 8, 1));

        var stack = new RouteTestStack();
        stack.Frames.Add(context.Frame);

        // 1. Without 0x4000 flag, failing destination selection sets RouteResult = 1 and returns False directly.
        var impossibleSlot = new RoutingSlotRequest
        {
            Flags = 0x0001,
            MinProximity = 500,
            MaxProximity = 500,
            OptimalProximity = 500,
        };
        var noFeedbackResult = RouteExecutor.Start(world, person, context.Frame, target, impossibleSlot, stack);
        Assert.Equal(PrimitiveResult.False, noFeedbackResult);
        Assert.Equal((short)1, person[PersonData.RouteResult]);
        Assert.Single(stack.Frames);

        // 2. With 0x4000 flag, failing destination selection pushes "Route failure feedback" with [1, blockerId, 0, 0]
        // and returns Again; once the feedback frame pops, Continue returns False with RouteResult = 1.
        person[PersonData.RouteResult] = 0;
        person[ObjectData.PrimitiveResultId] = 77;
        var feedbackSlot = new RoutingSlotRequest
        {
            Flags = 0x4001,
            MinProximity = 500,
            MaxProximity = 500,
            OptimalProximity = 500,
        };
        var pushResult = RouteExecutor.Start(world, person, context.Frame, target, feedbackSlot, stack);
        Assert.Equal(PrimitiveResult.Again, pushResult);
        Assert.Equal((short)1, person[PersonData.RouteResult]);
        Assert.Equal(2, stack.Frames.Count);
        var pushedFrame = stack.Frames[^1];
        Assert.Equal((short)4100, pushedFrame.TreeId);
        Assert.Equal(new short[] { 1, 77, 0, 0 }, pushedFrame.Parameters);

        // While feedback frame is still on stack, Continue returns Stop.
        Assert.Equal(PrimitiveResult.Stop, RouteExecutor.Continue(world, person, context.Frame, stack));

        // Pop feedback frame; Continue completes state 0x11 -> state 7 and returns False.
        stack.Frames.RemoveAt(stack.Frames.Count - 1);
        Assert.Equal(PrimitiveResult.False, RouteExecutor.Continue(world, person, context.Frame, stack));
        Assert.Equal((short)1, person[PersonData.RouteResult]);
        Assert.Null(person.NativeRoute);

        // 3. Interrupted route before start sets RouteResult = 4.
        person.ActiveActionCancelRequested = true;
        Assert.Equal(PrimitiveResult.False,
            RouteExecutor.Start(world, person, context.Frame, target, new RoutingSlotRequest(), stack));
        Assert.Equal((short)4, person[PersonData.RouteResult]);
        person.ActiveActionCancelRequested = false;

        // 4. Cross-room route with no connecting portal sets RouteResult = 2 (portal failure).
        target[ObjectData.Flags] |= 0x10;
        person.RoomWord84 = 1;
        target.RoomWord84 = 2;
        world.SetObjectPosition(target, new ObjectPosition(12 * 16 + 8, 5 * 16 + 8, 1));
        var portalFailResult = RouteExecutor.Start(world, person, context.Frame, target, new RoutingSlotRequest(), stack);
        for (var step = 0; step < 16 && portalFailResult == PrimitiveResult.Stop; step++)
        {
            world.TickAvatarAnimations(person);
            portalFailResult = RouteExecutor.Continue(world, person, context.Frame, stack);
        }
        Assert.Equal(PrimitiveResult.False, portalFailResult);
        Assert.Equal((short)2, person[PersonData.RouteResult]);
    }
}
