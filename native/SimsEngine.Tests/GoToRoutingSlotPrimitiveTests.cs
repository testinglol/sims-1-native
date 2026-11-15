using SimsEngine.Simulation;
using SimsEngine.Formats;
using SimsEngine.World;
using SimsEngine.Character;
using Xunit;

namespace SimsEngine.Tests;

public sealed class GoToRoutingSlotPrimitiveTests
{
    private sealed class RouteStack : TreeStack
    {
        public Func<StackFrame, BhavInstruction, PrimitiveResult>? Primitive { get; set; }
        public Action? OnPopped { get; set; }
        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction) =>
            Primitive?.Invoke(frame, instruction) ?? PrimitiveResult.False;
        protected override void Popped(StackFrame frame) => OnPopped?.Invoke();
    }

    // Bytes 0 and 1 are the index, bytes 2 and 3 the mode, byte 4 the flags. The other bytes are not read.
    private static byte[] Operand(short index, short mode, byte flags = 0) =>
        [(byte)index, (byte)(index >> 8), (byte)mode, (byte)(mode >> 8), flags, 0xEE, 0xEE, 0xEE];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Fact]
    public void PortalCandidatesMustBelongToTheRoutingSimsCurrentRoom()
    {
        Assert.True(RouteExecutor.PortalIsEligibleFromCurrentRoom(12, 12));
        Assert.False(RouteExecutor.PortalIsEligibleFromCurrentRoom(13, 12));
    }

    [Fact]
    public void RouteCandidateCapacityUsesTheSourceRoomAreaThreshold()
    {
        static int Capacity(int width, int height)
        {
            var world = new SimulationWorld();
            var map = OpenRoom(width, height);
            world.LoadRoomMaps(new Dictionary<int, RoomMap> { [1] = map });
            var person = world.Add(new XPerson(100, PrimitiveTestWorld.Definition(XPerson.PersonType)));
            world.SetObjectPosition(person, new ObjectPosition(width / 2 * 16 + 8, height / 2 * 16 + 8, 1));
            return RouteExecutor.AuthoredRouteCandidateCapacity(world, person);
        }

        Assert.Equal(48, Capacity(15, 16)); // +0x38 / 2 = 240.
        Assert.Equal(8, Capacity(16, 16)); // +0x38 / 2 = 256, over the source threshold 250.
    }

    [Fact]
    public void RoomTransitionUsesItsPortalEvenWhenTheSavedDoorWallBlocksTheGridEdge()
    {
        var world = new SimulationWorld();
        var map = TwoRoomsWithBlockedPortalEdge();
        world.LotRoomMaps[1] = map;
        var sourceRoom = map.RoomAt(2, 1);
        var destinationRoom = map.RoomAt(3, 1);
        Assert.NotEqual(sourceRoom, destinationRoom);

        var portal = world.Add(new XObject(3, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = new TypeRecord(3) { FunctionTable = PortalFunctionTable(4096) },
            RoomWord84 = unchecked((short)sourceRoom),
        });
        portal[ObjectData.Direction] = 2;
        world.SetObjectPosition(portal, new ObjectPosition(2 * 16 + 8, 1 * 16 + 8, 1));
        Assert.True(world.BlocksRouteDirectionAt(map, 2, 1, 2) ||
                    world.BlocksRouteDirectionAt(map, 3, 1, 6));

        Assert.True(RouteExecutor.CanStep(world, map, (2, 1), (3, 1)));
        Assert.False(RouteExecutor.CanStep(world, map, (2, 1), (3, 2)));

        // A door elsewhere in the same source room cannot authorize this wall edge.
        world.SetObjectPosition(portal, new ObjectPosition(1 * 16 + 8, 1 * 16 + 8, 1));
        Assert.False(RouteExecutor.CanStep(world, map, (2, 1), (3, 1)));
    }

    [Fact]
    public void PortalStepSelectionUsesStoredObjectRoomWordsAfterMapRoomChanges()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = OpenRoom();
        var sameRoom = world.Add(new XObject(1, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = new TypeRecord(3) { FunctionTable = PortalFunctionTable(4096) },
            Position = new ObjectPosition(168, 168, 1),
            RoomWord84 = 10,
        });
        sameRoom[ObjectData.Direction] = 2;
        var otherRoom = world.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = new TypeRecord(3) { FunctionTable = PortalFunctionTable(4096) },
            Position = new ObjectPosition(184, 168, 1),
            RoomWord84 = 11,
        });
        otherRoom[ObjectData.Direction] = 2;

        var candidates = RouteExecutor.PortalCandidatesForStep(world, 0, 1,
            (10, 10), (11, 10), currentRoomWord: 10).ToArray();

        Assert.Equal(new[] { sameRoom }, candidates);
    }

    [Fact]
    public void FullyRankedPortalCandidatesChooseTheLowestSourceCost()
    {
        var first = new XObject(1, PrimitiveTestWorld.Definition());
        var cheaper = new XObject(2, PrimitiveTestWorld.Definition());
        first.SetPortalRouteCost(4, 1008f);
        cheaper.SetPortalRouteCost(4, 1003f);

        Assert.Same(cheaper, RouteExecutor.SelectPreRankedPortal([first, cheaper], 4));
    }

    [Fact]
    public void EqualPortalCostsKeepTheFirstManagerEntry()
    {
        var first = new XObject(1, PrimitiveTestWorld.Definition());
        var second = new XObject(2, PrimitiveTestWorld.Definition());
        first.SetPortalRouteCost(4, 1003f);
        second.SetPortalRouteCost(4, 1003f);

        Assert.Same(first, RouteExecutor.SelectPreRankedPortal([first, second], 4));
    }

    [Fact]
    public void RemainingUnprojectedCostDoesNotHideALowerEstablishedRank()
    {
        var first = new XObject(1, PrimitiveTestWorld.Definition());
        var ranked = new XObject(2, PrimitiveTestWorld.Definition());
        first.SetPortalRouteCost(4, XObject.UninitializedPortalRouteCost);
        ranked.SetPortalRouteCost(4, 1003f);

        Assert.Same(ranked, RouteExecutor.SelectPreRankedPortal([first, ranked], 4));
    }

    [Fact]
    public void UsedAndProjectedFrontierPortalsAreExcludedButDistanceCapRemainsEligible()
    {
        var used = new XObject(1, PrimitiveTestWorld.Definition());
        var unprojected = new XObject(2, PrimitiveTestWorld.Definition());
        var projectedFrontier = new XObject(3, PrimitiveTestWorld.Definition());
        var distanceCap = new XObject(4, PrimitiveTestWorld.Definition());
        used.SetPortalRouteCost(4, 1101f);
        unprojected.SetPortalRouteCost(4, XObject.UninitializedPortalRouteCost);
        projectedFrontier.SetPortalRouteCost(4, 10000f);
        distanceCap.SetPortalRouteCost(4, 1000f);

        Assert.Same(unprojected, RouteExecutor.SelectPreRankedPortal([used, unprojected, projectedFrontier], 4));
        Assert.Same(distanceCap, RouteExecutor.SelectPreRankedPortal([used, projectedFrontier, distanceCap], 4));
        Assert.Null(RouteExecutor.SelectPreRankedPortal([used, projectedFrontier], 4));
    }

    [Fact]
    public void ZeroRouteIdSelectsNoPortal()
    {
        var portal = new XObject(1, PrimitiveTestWorld.Definition());
        portal.SetPortalRouteCost(1, 1002f);

        Assert.Null(RouteExecutor.SelectPreRankedPortal([portal], 0));
    }

    // Me is person 100 and the frame's stack object is object 2, with three routing slots; the lot's table has two.
    private static (TreeContext Context, PrimitiveTestHost Host) World(short[]? parameters = null, short stackObject = 2)
    {
        var (context, _, host) = PrimitiveTestWorld.Create(parameters, stackObject);
        context.Me.TypeRecord = new TypeRecord(context.Me.Id);
        context.Me[ObjectData.AllowedHeightFlags] = 1;
        context.Me[ObjectData.PlacementFlags] = 0x0800;
        context.World.Add(new XObject(2, PrimitiveTestWorld.Definition(0)));
        context.World.ObjectById(2)!.RoutingSlots.AddRange([new RoutingSlotRequest(), new RoutingSlotRequest(), new RoutingSlotRequest()]);
        host.LotRoutingSlots.AddRange([new RoutingSlotRequest(), new RoutingSlotRequest()]);
        AddWalkClips(context.World);
        return (context, host);
    }

    // The route executor moves a Sim by the root motion of Global.iff STR# 150's walk clips.
    internal static void AddWalkClips(SimulationWorld world)
    {
        var samples = Enumerable.Range(0, 31).Select(i => new System.Numerics.Vector3(i * 0.5f, 0f, 0f)).ToArray();
        for (var index = 11; index <= 20; index++)
            world.StandardWalkFallbackAnimationTable[(150, index)] = new AvatarAnimationClip($"walk{index}", samples.Length, [],
                null, 15f, new System.Numerics.Vector3(15f, 0f, 0f), samples, 1000f,
                LocomotionMarkerOne: index == 20 ? new AvatarAnimationMarker(0.25f, "lfoot") : null,
                LocomotionMarkerTwo: index == 20 ? new AvatarAnimationMarker(0.75f, "rfoot") : null);
        // Synthetic authored stride inputs for the source marker/fade completion path. These are fixture data,
        // not evidence for any installed animation's marker times or skeleton geometry.
        world.LocomotionStrideResolver = (_, _) => (40f, 40f);
    }

    private static PrimitiveResult RunRoute(TreeContext context, byte[] operand)
    {
        var result = ExecuteAndTick(context, operand);
        for (var callback = 0; callback < 1024 && result == PrimitiveResult.Stop; callback++)
            result = ExecuteAndTick(context, operand);
        return result;
    }

    private static PrimitiveResult ExecuteAndTick(TreeContext context, byte[] operand)
    {
        var result = GoToRoutingSlotPrimitive.Execute(context, operand);
        if (context.World.Host is null && context.Me is XPerson person && result == PrimitiveResult.Stop)
            context.World.TickAvatarAnimations(person);
        return result;
    }

    private static PrimitiveResult ContinueAndTick(SimulationWorld world, XPerson person, StackFrame frame,
        TreeStack? stack = null)
    {
        var result = RouteExecutor.Continue(world, person, frame, stack);
        if (result == PrimitiveResult.Stop) world.TickAvatarAnimations(person);
        return result;
    }

    private static ushort[] PortalFunctionTable(ushort tree)
    {
        var functions = new ushort[0x10];
        functions[0x0f] = tree;
        return functions;
    }

    private static void AddDefinitionBackedReferenceSlot(XObject reference)
    {
        if (reference.FootprintSlots.Count == 0) reference.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
        if (reference.Slots.Count == 0) reference.Slots.Add(0);
    }

    // FUN_004338a5's person-candidate scan skips an object neighbor when its +0x5a Flags has 0x10 set.
    // Keep route-anchor fixture overlap behavior explicit; solid blockers in these tests remain unchanged.
    private static void AllowPersonOverlapWithRouteAnchor(XObject anchor) => anchor[ObjectData.Flags] |= 0x10;

    [Fact]
    public void NonSolidRouteTargetDoesNotBecomeTheIntermediatePlacementReference()
    {
        // Destination.iff's initializer sets Flags bit 2. Route movement still validates
        // ordinary placement (FUN_00589d4b passes null reference/slot arguments).
        var (context, _) = World();
        var world = context.World;
        var person = (XPerson)context.Me;
        world.LotRoomMaps[1] = OpenRoom();
        person[ObjectData.PlacementFlags] |= 1;
        world.PlaceObject(person, new ObjectPosition(136, 136, 1));
        var target = world.ObjectById(2)!;
        target[ObjectData.Flags] = 4;
        world.PlaceObject(target, new ObjectPosition(200, 136, 1));

        var result = RouteExecutor.Start(world, person, context.Frame, target, new RoutingSlotRequest(), null);
        for (var step = 0; step < 128 && result == PrimitiveResult.Stop; step++)
            result = ContinueAndTick(world, person, context.Frame);

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal(target.Position, person.Position);
        Assert.Null(person.NativeRoute);
    }

    [Fact]
    public void RouteWaypointsRemoveOldTileRootsAndKeepCurrentOccupancy()
    {
        // FUN_00589d4b -> cXPerson +0x88 (0045e655) -> FUN_004333ef applies
        // every movement step through placement, not just the final destination.
        var (context, _) = World();
        var world = context.World;
        var person = (XPerson)context.Me;
        world.LotRoomMaps[1] = OpenRoom();
        world.PlaceObject(person, new ObjectPosition(136, 136, 1));
        var result = RouteExecutor.StartDirectPosition(world, person, context.Frame,
            new ObjectPosition(200, 136, 1), stack: null);

        var tileX = person.Position.X >> 4;
        for (var tick = 0; tick < 512 && result == PrimitiveResult.Stop; tick++)
        {
            result = ContinueAndTick(world, person, context.Frame);
            var currentX = person.Position.X >> 4;
            if (currentX != tileX)
            {
                Assert.Equal(0, world.TileValueAt(tileX, 8, 1));
                tileX = currentX;
            }
            Assert.Equal(person.ClockManagerIndex, world.TileValueAt(currentX, 8, 1));
            Assert.Same(person, Assert.Single(world.ObjectsAtTile(currentX, 8, 1)));
        }
        Assert.Equal(new ObjectPosition(200, 136, 1), person.Position);
        Assert.Equal(PrimitiveResult.True, result);
        Assert.Null(person.NativeRoute);
    }

    [Fact]
    public void DirectPositionRouteUsesOrdinaryPlacementAndWritesNoSlotRelationship()
    {
        // The explicit-position helper must arrive without binding a slot. User Go Here
        // commands use Destination.iff's queued BHAV rather than this helper.
        var (context, _) = World();
        var person = (XPerson)context.Me;
        context.World.LotRoomMaps[1] = OpenRoom();
        var start = new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1);
        var destination = new ObjectPosition(10 * 16 + 8, 8 * 16 + 8, 1);
        context.World.SetObjectPosition(person, start);
        person[ObjectData.SlotHierNumber] = -1;
        var priorSlots = person.Slots.ToArray();

        var result = RouteExecutor.StartDirectPosition(context.World, person, context.Frame,
            destination, stack: null);
        for (var continuation = 0; continuation < 16 && result == PrimitiveResult.Stop; continuation++)
            result = ContinueAndTick(context.World, person, context.Frame);

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal(destination, person.Position);
        Assert.Equal(3, person.AnimatorControlState);
        Assert.Equal((short)0, person[ObjectData.ParentId]);
        Assert.Equal((short)0, person[ObjectData.ContainerId]);
        Assert.Equal((short)-1, person[ObjectData.SlotHierNumber]);
        Assert.Equal(priorSlots, person.Slots);
    }

    [Fact]
    public void DirectPositionRouteRejectsAnOuterBorderTargetWithoutMovingThePerson()
    {
        var (context, _) = World();
        var person = (XPerson)context.Me;
        context.World.LotRoomMaps[1] = OpenRoom();
        var start = new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1);
        var destination = new ObjectPosition(8, 8 * 16 + 8, 1);
        context.World.SetObjectPosition(person, start);
        person[ObjectData.SlotHierNumber] = -1;

        var result = RouteExecutor.StartDirectPosition(context.World, person, context.Frame,
            destination, stack: null);

        Assert.Equal(PrimitiveResult.False, result);
        Assert.Equal(start, person.Position);
        Assert.Null(person.NativeRoute);
        Assert.Equal((short)0, person[ObjectData.ParentId]);
        Assert.Equal((short)-1, person[ObjectData.SlotHierNumber]);
    }

    [Fact]
    public void DirectPositionRouteUsesThePortalManagerForACrossRoomTarget()
    {
        var (baseContext, _) = World();
        var world = baseContext.World;
        world.Host = null;
        world.RoutingEnabled = true;
        world.LotRoomMaps[1] = TwoRoomsSplitAtXWithApproach();
        world.CaptureExecutionTrace = true;
        var person = (XPerson)baseContext.Me;
        var start = new ObjectPosition(24, 24, 1);
        var destination = new ObjectPosition(56, 24, 1);
        world.SetObjectPosition(person, start);
        Assert.True(world.CanPlaceObject(person, destination), $"placement-error=0x{world.LastPlacementFailureCode:X2}");
        var map = world.LotRoomMaps[1];
        var sourceRoom = map.RoomAt(1, 1);
        var destinationRoom = map.RoomAt(3, 1);
        Assert.NotEqual(sourceRoom, destinationRoom);
        person.RoomWord84 = unchecked((short)sourceRoom);

        var portalTree = PrimitiveTestWorld.Tree(0,
            (0x25, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse, new byte[8]));
        var portalScope = new TreeTable((4096, portalTree));
        var portal = world.Add(new XObject(3, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = new TypeRecord(3) { BehaviorScope = portalScope, FunctionTable = PortalFunctionTable(4096) },
            RoomWord84 = unchecked((short)sourceRoom),
        });
        world.SetObjectPosition(portal, new ObjectPosition(40, 24, 1));
        portal[ObjectData.Direction] = 2;
        var functionPart = world.Add(new XObject(4, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = new TypeRecord(4) { BehaviorScope = portalScope, FunctionTable = PortalFunctionTable(4097) },
            RoomWord84 = unchecked((short)destinationRoom),
        });
        world.SetObjectPosition(functionPart, new ObjectPosition(72, 24, 1));
        portal.MultiTileNextId = functionPart.Id;

        var stack = new RouteStack();
        stack.Frames.Add(baseContext.Frame);
        var result = RouteExecutor.StartDirectPosition(world, person, baseContext.Frame, destination, stack);

        Assert.True(result == PrimitiveResult.Again,
            $"result={result}; placementError=0x{world.LastPlacementFailureCode:X2}; reason={world.LastRouteCandidateFailureReason}; route={string.Join(';', world.NativeRouteTrace.Select(entry => $"{entry.Phase}:{entry.Failure}"))}; candidates={string.Join(';', world.RouteCandidateTrace.Select(entry => entry.FailureReason))}");
        Assert.Equal(start, person.Position);
        Assert.Contains(world.NativeRouteTrace, entry => entry.Phase == "portal-function-started");
        Assert.Equal((short)4096, stack.Top!.TreeId);
    }

    [Theory]
    [InlineData(0, 0, -16)]
    [InlineData(2, 16, 0)]
    [InlineData(4, 0, 16)]
    [InlineData(6, -16, 0)]
    public void RelativePositionDirectionalRegionUsesTheAuthoredRadiusAndAnchorFacing(
        short direction, int offsetX, int offsetY)
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        context.World.SetObjectPosition(context.Me, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        var anchor = context.StackObject!;
        var anchorPosition = new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1);
        context.World.SetObjectPosition(anchor, anchorPosition);
        anchor[ObjectData.Direction] = direction;
        var request = new RoutingSlotRequest
        {
            Standing = 1,
            Flags = 1,
            MinProximity = 16,
            MaxProximity = 16,
            OptimalProximity = 16,
            Resolution = 16,
        };

        var destination = RouteExecutor.SelectDestination(context.World, (XPerson)context.Me,
            anchor, request);

        Assert.Equal(anchorPosition with { X = anchorPosition.X + offsetX, Y = anchorPosition.Y + offsetY }, destination?.Position);
    }

    [Fact]
    public void RouteSlotFlagTwoHundredMakesDirectionalSamplingAbsolute()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        context.World.SetObjectPosition(person, new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1));
        var origin = new ObjectPosition(20 * 16 + 8, 20 * 16 + 8, 1);
        context.World.SetObjectPosition(anchor, origin);
        anchor[ObjectData.Direction] = 4;

        var destination = RouteExecutor.SelectDestination(context.World, person, anchor,
            new RoutingSlotRequest { Flags = 0x0201, MinProximity = 16, MaxProximity = 16,
                OptimalProximity = 16, Resolution = 16 });

        Assert.Equal(origin with { Y = origin.Y - 16 }, destination?.Position);
    }

    [Theory]
    [InlineData(0x0001, 1u)]
    [InlineData(0x2001, 0x41c67ea6u)]
    public void RouteSlotRandomScoreFlagControlsSharedRandomDraws(uint flags, uint expectedRandomState)
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        context.World.SetObjectPosition(person, new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1));
        context.World.SetObjectPosition(anchor, new ObjectPosition(20 * 16 + 8, 20 * 16 + 8, 1));

        var destination = RouteExecutor.SelectDestination(context.World, person, anchor,
            new RoutingSlotRequest { Flags = flags, MinProximity = 16, MaxProximity = 16,
                OptimalProximity = 16, Resolution = 16 });

        Assert.NotNull(destination);
        // This authored region contains exactly one point at radius 16. With flag 0x2000,
        // FUN_0046f556 advances state 1 once to 0x41c67ea6 for its score perturbation.
        Assert.Equal(expectedRandomState, context.World.RandomState);
    }

    [Fact]
    public void RouteSlotRandomScoreDrawOccursBeforePlacementRejectsTheEndpoint()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        context.World.SetObjectPosition(person, new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1));
        var origin = new ObjectPosition(20 * 16 + 8, 20 * 16 + 8, 1);
        context.World.SetObjectPosition(anchor, origin);
        var blocker = context.World.Add(new XObject(3, PrimitiveTestWorld.Definition()));
        context.World.PlaceCreatedObject(blocker, origin with { Y = origin.Y - 16 });

        var destination = RouteExecutor.SelectDestination(context.World, person, anchor,
            new RoutingSlotRequest { Flags = 0x2001, MinProximity = 16, MaxProximity = 16,
                OptimalProximity = 16, Resolution = 16 });

        Assert.Null(destination);
        // FUN_00483572 adjusts score before FUN_00481593 classifies the blocked endpoint.
        Assert.Equal(0x41c67ea6u, context.World.RandomState);
    }

    [Theory]
    [InlineData(0, 16, 16, 16)]
    [InlineData(16, 0, 16, 16)]
    [InlineData(32, 16, 16, 16)]
    [InlineData(16, 16, 32, 16)]
    [InlineData(16, 16, 16, 0)]
    public void DirectionalRouteSlotRejectsInvalidAuthoredSamplingFields(
        int minimum, int maximum, int optimal, int resolution)
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        context.World.SetObjectPosition(person, new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1));
        context.World.SetObjectPosition(anchor, new ObjectPosition(20 * 16 + 8, 20 * 16 + 8, 1));

        var destination = RouteExecutor.SelectDestination(context.World, person, anchor,
            new RoutingSlotRequest { Flags = 1, MinProximity = minimum, MaxProximity = maximum,
                OptimalProximity = optimal, Resolution = resolution });

        Assert.Null(destination);
        Assert.Contains("invalid authored route-slot", context.World.LastRouteCandidateFailureReason);
    }

    [Fact]
    public void DirectionalRouteSlotCanChooseAnotherReachablePointInsideItsAuthoredRange()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        var origin = new ObjectPosition(20 * 16 + 8, 20 * 16 + 8, 1);
        context.World.SetObjectPosition(person, new ObjectPosition(18 * 16 + 8, 18 * 16 + 8, 1));
        context.World.SetObjectPosition(anchor, origin);
        var blockedNearest = origin with { Y = origin.Y - 16 };
        var blocker = context.World.Add(new XObject(3, PrimitiveTestWorld.Definition()));
        context.World.PlaceCreatedObject(blocker, blockedNearest);
        Assert.Equal(blocker.ClockManagerIndex,
            context.World.TileValueAt(blockedNearest.X >> 4, blockedNearest.Y >> 4, 1));
        Assert.False(context.World.CanPlaceObject(person, blockedNearest));

        var destination = RouteExecutor.SelectDestination(context.World, person, anchor,
            new RoutingSlotRequest { Flags = 1, MinProximity = 16, MaxProximity = 32,
                OptimalProximity = 16, Resolution = 16 });

        Assert.NotNull(destination);
        var dx = destination!.Value.Position.X - origin.X;
        var dy = destination.Value.Position.Y - origin.Y;
        Assert.InRange(dx * dx + dy * dy, 16 * 16, 32 * 32);
        Assert.Equal(context.World.LotRoomMaps[1].RoomAt(origin.X >> 4, origin.Y >> 4),
            context.World.LotRoomMaps[1].RoomAt(destination.Value.Position.X >> 4, destination.Value.Position.Y >> 4));
        Assert.NotEqual(blockedNearest, destination!.Value.Position);
    }

    [Fact]
    public void DirectionalRouteSlotDoesNotFallBackOutsideItsRegionWhenWallBlocksTheOnlyPoint()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = RoomWithVerticalWall(8);
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        var origin = new ObjectPosition(10 * 16 + 8, 5 * 16 + 8, 1);
        context.World.SetObjectPosition(person, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        context.World.SetObjectPosition(anchor, origin);
        anchor[ObjectData.Direction] = 4;

        var destination = RouteExecutor.SelectDestination(context.World, person, anchor,
            new RoutingSlotRequest { Flags = 1u << 2, MinProximity = 16, MaxProximity = 16,
                OptimalProximity = 16, Resolution = 16 });

        Assert.Null(destination);
    }

    [Fact]
    public void WaitingFrameContinuesItsRouteWithNoTargetAndNoStackObject()
    {
        var (context, host) = World(stackObject: 9);
        context.Frame.Waiting = true;
        Assert.Equal(PrimitiveResult.True, GoToRoutingSlotPrimitive.Execute(context, Operand(0, 1)));
        Assert.Equal(new[] { "Route(100,null)" }, host.Calls);
    }

    [Fact]
    public void NativeWaitingFrameWithoutARouteRecordReturnsFalseLikeTheSourceManager()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.Frame.Waiting = true;

        var result = ExecuteAndTick(context, Operand(0, 2));

        Assert.Equal(PrimitiveResult.False, result);
        Assert.False(context.Frame.Waiting);
        Assert.Null(((XPerson)context.Me).NativeRoute);
    }

    [Fact]
    public void MissingStackObjectIsErrorFiftyTwo()
    {
        var (context, host) = World(stackObject: 9);
        Assert.Equal(SimulationError.ObjectForRoutingDoesNotExistOrIsOutOfBounds,
            Fails(() => GoToRoutingSlotPrimitive.Execute(context, Operand(0, 1))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void RoutingDisabledAnswersFalseBeforeTheModeIsRead()
    {
        var (context, host) = World();
        host.RoutingEnabledAnswer = false;
        // Mode 9 is not valid, but the routing check comes first.
        Assert.Equal(PrimitiveResult.False, GoToRoutingSlotPrimitive.Execute(context, Operand(0, 9)));
        Assert.Equal(new[] { "RoutingEnabled" }, host.Calls);
    }

    [Fact]
    public void ModeOneSelectsTheStackObjectsSlotAndRoutesWithIt()
    {
        var (context, host) = World();
        Assert.Equal(PrimitiveResult.True, GoToRoutingSlotPrimitive.Execute(context, Operand(1, 1)));
        Assert.Equal(new[] { "RoutingEnabled", "Route(100,slot)" }, host.Calls);
        Assert.Same(context.StackObject!.RoutingSlots[1], host.Routes[0]);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void ModeOneSlotOutsideTheVectorIsErrorThirtySeven(short index)
    {
        var (context, host) = World();
        Assert.Equal(SimulationError.InvalidRoutingSlotSpecified,
            Fails(() => GoToRoutingSlotPrimitive.Execute(context, Operand(index, 1))));
        Assert.Empty(host.Routes);
    }

    [Fact]
    public void ModeTwoSelectsALotSlot()
    {
        var (context, host) = World();
        Assert.Equal(PrimitiveResult.True, GoToRoutingSlotPrimitive.Execute(context, Operand(1, 2)));
        Assert.Equal(new[] { "RoutingEnabled", "LotRoutingSlotCount(100)", "LotRoutingSlotAt(100,1)", "Route(100,slot)" }, host.Calls);
        Assert.Same(host.LotRoutingSlots[1], host.Routes[0]);
    }

    [Fact]
    public void NativeModeTwoUsesDirectionalRouteSlotRegionAfterClearingRouteFlag()
    {
        var (context, _) = World(stackObject: 100);
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        context.World.SetObjectPosition(context.Me, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        context.World.SetObjectPosition(context.StackObject!, context.Me.Position);
        var slot = new RoutingSlotRequest { Flags = 0x4001 };
        context.World.LotRoutingSlots.Add(slot);

        Assert.Equal(PrimitiveResult.True, RunRoute(context, Operand(0, 2, 1)));

        Assert.Same(slot, context.World.LotRoutingSlots[0]);
        Assert.Equal(0x0001u, slot.Flags);
        Assert.Equal(new ObjectPosition(5 * 16 + 8, 4 * 16 + 8, 1), context.Me.Position);
    }

    [Fact]
    public void DirectionalZeroOffsetSlotApproachesAnOccupiedAnchorWithinItsAuthoredRange()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        context.World.SetObjectPosition(context.Me, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        var anchorPosition = new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1);
        context.World.SetObjectPosition(context.StackObject!, anchorPosition);
        context.Me[ObjectData.PrimitiveResult] = 1;
        var slot = RoutingSlotRequest.FromDefinition(new Slot(3, 0, 0, 0,
            0, 0, 0, 0xff, -1, 16, 16, 16, 0, 0, 0.1875f, 0, -2, 16));
        context.World.LotRoutingSlots.Add(slot);

        var result = ExecuteAndTick(context, Operand(0, 2));
        for (var step = 0; step < 128 && result == PrimitiveResult.Stop; step++)
            result = ExecuteAndTick(context, Operand(0, 2));

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Null(((XPerson)context.Me).NativeRoute);
        var dx = context.Me.Position.X - anchorPosition.X;
        var dy = context.Me.Position.Y - anchorPosition.Y;
        // The goal rounds to lot integers, so a diagonal approach lands just inside the authored radius.
        Assert.InRange(Math.Sqrt(dx * dx + dy * dy), 15.0, 17.0);
        Assert.Equal((short)0, context.Me[ObjectData.PrimitiveResult]);
    }

    [Fact]
    public void BlockedRouteCandidateStoresTheRootObjectId()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        context.World.SetObjectPosition(context.Me, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        context.World.SetObjectPosition(context.StackObject!, new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1));
        var blocker = context.World.Add(new XObject(3, PrimitiveTestWorld.Definition()));
        context.World.PlaceCreatedObject(blocker, new ObjectPosition(11 * 16 + 8, 10 * 16 + 8, 1));
        Assert.Equal(blocker.ClockManagerIndex, context.World.TileValueAt(11, 10, 1));
        context.Me[ObjectData.PrimitiveResultId] = 123;
        context.World.LotRoutingSlots.Add(new RoutingSlotRequest { X = 16 });

        var destination = RouteExecutor.SelectDestination(context.World, (XPerson)context.Me,
            context.StackObject!, context.World.LotRoutingSlots[0]);

        Assert.Null(destination);
        Assert.Equal(blocker.ClockManagerIndex, context.Me[ObjectData.PrimitiveResultId]);
        Assert.Equal((short)0, context.Me[ObjectData.PrimitiveResult]);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(63, false)]
    public void RouteSlotCandidateMustStayInsideTheLotBorder(int anchorTileX, bool expected)
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        context.World.SetObjectPosition(context.Me, new ObjectPosition(2 * 16 + 8, 2 * 16 + 8, 1));
        var anchorPosition = new ObjectPosition(anchorTileX * 16 + 8, 2 * 16 + 8, 1);
        context.World.SetObjectPosition(context.StackObject!, anchorPosition);
        AddDefinitionBackedReferenceSlot(context.StackObject!);
        context.World.LotRoutingSlots.Add(RoutingSlotRequest.FromDefinition(new Slot(3, 0, 0, 0,
            0, 0, 0, 0xff, 0, 16, 16, 16, 0, 0, 0.1875f, 0, -2, 16)));

        var destination = RouteExecutor.SelectDestination(context.World, (XPerson)context.Me,
            context.StackObject!, context.World.LotRoutingSlots[0]);

        Assert.Equal(expected ? anchorPosition : null, destination?.Position);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void RouteCandidateFootprintWallGateUsesTheRequestWallCheckFlag(bool checkWall, bool expected)
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = RoomWithRouteWall();
        context.World.SetObjectPosition(context.Me, new ObjectPosition(0x18, 0x18, 1));
        context.Me.TypeRecord = new TypeRecord(context.Me.Id);
        Assert.True(context.World.FootprintIntersectsWall(
            ((XPerson)context.Me).FootprintBoundsAt(new ObjectPosition(0x1e, 0x18, 1)), 1));
        var anchorPosition = new ObjectPosition(0x1e, 0x18, 1);
        context.World.SetObjectPosition(context.StackObject!, anchorPosition);
        AddDefinitionBackedReferenceSlot(context.StackObject!);
        var slot = RoutingSlotRequest.FromDefinition(new Slot(3, 0, 0, 0,
            0, 0, 0, 0, 0, 16, 16, 16, 0, 0, 0.1875f, 0, -2, 16));
        slot.CheckCandidateFootprintAgainstWall = checkWall;
        context.World.LotRoutingSlots.Add(slot);

        var destination = RouteExecutor.SelectDestination(context.World, (XPerson)context.Me,
            context.StackObject!, slot);

        // With the request's wall-check flag (operand byte 6 bit 2) set, the wall-crossing anchor point is rejected.
        Assert.Equal(expected ? anchorPosition : null, destination?.Position);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0x0004, 0)]
    [InlineData(0, 0x0004)]
    public void RelativeRoutePlacementAllowsPlacementFlagFourOnEitherObject(
        int personFlags, int anchorFlags)
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        context.World.SetObjectPosition(person, new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1));
        context.World.SetObjectPosition(anchor, new ObjectPosition(10 * 16 + 8, 8 * 16 + 8, 1));
        person[ObjectData.PlacementFlags] = (short)personFlags;
        anchor[ObjectData.PlacementFlags] = (short)anchorFlags;

        var accepted = context.World.CanPlaceRouteCandidate(person,
            new ObjectPosition(11 * 16 + 8, 8 * 16 + 8, 1), anchor);

        Assert.True(accepted);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0x0004, 0, false)]
    [InlineData(0, 0x0004, false)]
    public void RelativeRouteSlotRejectsRuntimeFlagsBitFourOnEitherObject(
        int personFlags, int anchorFlags, bool expected)
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        var candidate = new ObjectPosition(10 * 16 + 8, 8 * 16 + 8, 1);
        context.World.SetObjectPosition(person, new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1));
        context.World.SetObjectPosition(anchor, candidate);
        person[ObjectData.Flags] = (short)personFlags;
        anchor[ObjectData.Flags] = (short)anchorFlags;
        anchor.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });

        Assert.Equal(expected,
            context.World.CanPlaceRouteCandidate(person, candidate, anchor, referenceSlotIndex: 0));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void RelativeRouteSlotChecksTheNormalizedSlotHeightCode(int allowedHeightFlags, bool expected)
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        var candidate = new ObjectPosition(10 * 16 + 8, 8 * 16 + 8, 1);
        context.World.SetObjectPosition(person, new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1));
        context.World.SetObjectPosition(anchor, candidate);
        person[ObjectData.AllowedHeightFlags] = (short)allowedHeightFlags;
        anchor.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 2 });

        Assert.Equal(expected,
            context.World.CanPlaceRouteCandidate(person, candidate, anchor, referenceSlotIndex: 0));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(unchecked((short)0x8000), false)]
    public void RelativeRouteSlotHeightCodeZeroUsesAllowedHeightBitThirtyOne(short allowedHeightFlags,
        bool expected)
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        var candidate = new ObjectPosition(10 * 16 + 8, 8 * 16 + 8, 1);
        context.World.SetObjectPosition(person, new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1));
        context.World.SetObjectPosition(anchor, candidate);
        person[ObjectData.AllowedHeightFlags] = allowedHeightFlags;
        anchor.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 0 });

        Assert.Equal(expected,
            context.World.CanPlaceRouteCandidate(person, candidate, anchor, referenceSlotIndex: 0));
        Assert.Equal(0x18, context.World.LastPlacementFailureCode);
    }

    [Fact]
    public void RelativeUserSlotReportsSourceErrorForUnsupportedCenteredGroundCodes()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        var candidate = new ObjectPosition(10 * 16 + 8, 8 * 16 + 8, 1);
        context.World.SetObjectPosition(person, new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1));
        context.World.SetObjectPosition(anchor, candidate);
        person[ObjectData.AllowedHeightFlags] = 1 << 4;
        anchor.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 5 });

        Assert.True(context.World.CanPlaceRouteCandidate(person, candidate, anchor, referenceSlotIndex: 0));
        Assert.False(context.World.CanUserPlaceReferencedSlot(person, candidate, anchor, referenceSlotIndex: 0));
        Assert.Equal(0x0b, context.World.LastPlacementFailureCode);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void RelativeRouteSlotChecksMoverWeightAgainstReferenceSupport(int supportStrength, bool expected)
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        var candidate = new ObjectPosition(10 * 16 + 8, 8 * 16 + 8, 1);
        context.World.SetObjectPosition(person, new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1));
        context.World.SetObjectPosition(anchor, candidate);
        person[ObjectData.Weight] = 1;
        anchor[ObjectData.SupportStrength] = (short)supportStrength;
        anchor.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });

        Assert.Equal(expected,
            context.World.CanPlaceRouteCandidate(person, candidate, anchor, referenceSlotIndex: 0));
    }

    [Fact]
    public void RelativeRouteSlotRejectsAReferenceWhoseParentChainContainsTheMover()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        var candidate = new ObjectPosition(10 * 16 + 8, 8 * 16 + 8, 1);
        context.World.SetObjectPosition(person, new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1));
        context.World.SetObjectPosition(anchor, candidate);
        anchor.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
        anchor[ObjectData.ParentId] = person.ClockManagerIndex;
        anchor[ObjectData.SlotHierNumber] = 1;

        Assert.False(context.World.CanPlaceRouteCandidate(person, candidate, anchor, referenceSlotIndex: 0));
    }

    [Fact]
    public void RelativeRouteSlotValidationUsesDefinedSlotsRatherThanLiveOccupantCount()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        var candidate = new ObjectPosition(10 * 16 + 8, 8 * 16 + 8, 1);
        context.World.SetObjectPosition(person, new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1));
        context.World.SetObjectPosition(anchor, candidate);

        // FUN_00432a70 bounds the request index against cXObject +0x130, not its occupied-slot relation.
        anchor.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
        Assert.True(context.World.CanPlaceRouteCandidate(person, candidate, anchor, referenceSlotIndex: 0));

        anchor.FootprintSlots.Clear();
        anchor.Slots.Add(0);
        Assert.False(context.World.CanPlaceRouteCandidate(person, candidate, anchor, referenceSlotIndex: 0));
    }

    [Theory]
    [InlineData("height", 0x18)]
    [InlineData("weight", 0x0e)]
    [InlineData("cycle", 0x0b)]
    [InlineData("missing-slot", 0x0d)]
    public void InvalidRelativeSlotValidationLeavesBothObjectsAndTheirLinksUnchanged(string failure, int errorCode)
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        var personPosition = new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1);
        var anchorPosition = new ObjectPosition(10 * 16 + 8, 8 * 16 + 8, 1);
        context.World.SetObjectPosition(person, personPosition);
        context.World.SetObjectPosition(anchor, anchorPosition);
        person[ObjectData.AllowedHeightFlags] = 1;
        anchor[ObjectData.SupportStrength] = 1;
        anchor.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
        anchor.Slots.Add(0);

        switch (failure)
        {
            case "height":
                person[ObjectData.AllowedHeightFlags] = 0;
                break;
            case "weight":
                person[ObjectData.Weight] = 2;
                anchor[ObjectData.SupportStrength] = 1;
                break;
            case "cycle":
                anchor[ObjectData.ParentId] = person.ClockManagerIndex;
                anchor[ObjectData.SlotHierNumber] = 1;
                break;
            case "missing-slot":
                anchor.FootprintSlots.Clear();
                break;
        }

        var personParent = person[ObjectData.ParentId];
        var personSlot = person[ObjectData.SlotNumber];
        var personHierarchy = person[ObjectData.SlotHierNumber];
        var anchorParent = anchor[ObjectData.ParentId];
        var anchorSlot = anchor[ObjectData.SlotNumber];
        var anchorHierarchy = anchor[ObjectData.SlotHierNumber];
        var personSlots = person.Slots.ToArray();
        var anchorSlots = anchor.Slots.ToArray();
        var anchorFootprintOccupants = anchor.FootprintSlots.Select(slot => slot.OccupantObjectId).ToArray();

        Assert.False(context.World.CanPlaceRouteCandidate(person, anchorPosition, anchor, referenceSlotIndex: 0));

        Assert.Equal(errorCode, context.World.LastPlacementFailureCode);
        Assert.Equal(personPosition, person.Position);
        Assert.Equal(anchorPosition, anchor.Position);
        Assert.Equal(personParent, person[ObjectData.ParentId]);
        Assert.Equal(personSlot, person[ObjectData.SlotNumber]);
        Assert.Equal(personHierarchy, person[ObjectData.SlotHierNumber]);
        Assert.Equal(anchorParent, anchor[ObjectData.ParentId]);
        Assert.Equal(anchorSlot, anchor[ObjectData.SlotNumber]);
        Assert.Equal(anchorHierarchy, anchor[ObjectData.SlotHierNumber]);
        Assert.Equal(personSlots, person.Slots);
        Assert.Equal(anchorSlots, anchor.Slots);
        Assert.Equal(anchorFootprintOccupants,
            anchor.FootprintSlots.Select(slot => slot.OccupantObjectId).ToArray());
    }

    // FUN_00432a70 allows the selected +0x130 record's empty +0x10 occupant key or the mover's
    // own +0xf0 manager key, and rejects a different key before apply-mode displacement.
    [Theory]
    [InlineData(0, true)]
    [InlineData(3, false)]
    public void SnappedRouteValidationRejectsACompetingReferenceSlotOccupant(int slotOccupantId, bool expected)
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        context.World.SetObjectPosition(person, new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1));
        context.World.SetObjectPosition(anchor, new ObjectPosition(10 * 16 + 8, 8 * 16 + 8, 1));
        anchor.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1, OccupantObjectId = (short)slotOccupantId });
        anchor.Slots.Add(slotOccupantId == 0 ? (short)0 : (short)slotOccupantId);
        var slot = new RoutingSlotRequest { SnapTargetSlot = 0, X = 32 };

        var destination = RouteExecutor.SelectDestination(context.World, person, anchor, slot);

        if (expected) Assert.Equal(anchor.Position, destination?.Position);
        else Assert.Null(destination);
    }

    [Fact]
    public void SnappedRouteValidationAllowsTheMovingPersonAsTheReferenceSlotOccupant()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        var candidate = new ObjectPosition(10 * 16 + 8, 8 * 16 + 8, 1);
        context.World.SetObjectPosition(person, new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1));
        context.World.SetObjectPosition(anchor, candidate);
        anchor.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1, OccupantObjectId = person.ClockManagerIndex });

        Assert.True(context.World.CanPlaceRouteCandidate(person, candidate, anchor, referenceSlotIndex: 0));
    }

    [Fact]
    public void SnappedRouteArrivalWritesThePersonIntoTheReferenceSlot()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        AllowPersonOverlapWithRouteAnchor(anchor);
        anchor.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
        anchor.Slots.Clear();
        context.World.SetObjectPosition(person, new ObjectPosition(11 * 16 + 8, 10 * 16 + 8, 1));
        context.World.SetObjectPosition(anchor, new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1));
        context.World.LotRoutingSlots.Add(new RoutingSlotRequest { SnapTargetSlot = 0 });

        var result = RunRoute(context, Operand(0, 2));

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal(person.ClockManagerIndex, anchor.Slots[1]);
        Assert.Equal(anchor.ClockManagerIndex, person[ObjectData.ParentId]);
        Assert.Equal((short)1, person[ObjectData.SlotHierNumber]);
        Assert.Equal(person.ClockManagerIndex, anchor.FootprintSlots[0].OccupantObjectId);
        Assert.Equal(anchor.Position, person.Position);
    }

    [Fact]
    public void SnappedRouteArrivalMovesThePriorOccupantIntoThePersonsFirstSlot()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var anchor = context.StackObject!;
        AllowPersonOverlapWithRouteAnchor(anchor);
        var displaced = context.World.Add(new XObject(90, new short[XObject.DefinitionCount], anchor.ClockManagerIndex));
        anchor.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
        anchor.Slots.Clear();
        person.Slots.Add(0);
        context.World.SetObjectPosition(person, new ObjectPosition(5 * 16 + 8, 10 * 16 + 8, 1));
        context.World.SetObjectPosition(anchor, new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1));
        context.World.LotRoutingSlots.Add(new RoutingSlotRequest { SnapTargetSlot = 0 });

        var result = ExecuteAndTick(context, Operand(0, 2));
        Assert.Equal(PrimitiveResult.Stop, result);
        Assert.True(person.Position.X > 5 * 16 + 8);

        // The slot was empty when FUN_00432a70 validated the route. A competing object arrives
        // before apply mode, where the source detaches it and links it to person slot 0.
        anchor.Slots.AddRange([0, displaced.ClockManagerIndex]);
        anchor.FootprintSlots[0].OccupantObjectId = displaced.ClockManagerIndex;
        displaced[ObjectData.ParentId] = anchor.ClockManagerIndex;
        displaced[ObjectData.SlotHierNumber] = 1;
        for (var callback = 0; callback < 128 && result != PrimitiveResult.True; callback++)
            result = ExecuteAndTick(context, Operand(0, 2));

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal(person.ClockManagerIndex, anchor.Slots[1]);
        Assert.Equal(displaced.ClockManagerIndex, person.Slots[0]);
        Assert.Equal(person.ClockManagerIndex, displaced[ObjectData.ParentId]);
        Assert.Equal((short)0, displaced[ObjectData.SlotHierNumber]);
        Assert.Equal(person.ClockManagerIndex, anchor.FootprintSlots[0].OccupantObjectId);
    }

    [Theory]
    [InlineData(8, 0x0000, false)]
    [InlineData(9, 0x0000, false)]
    [InlineData(8, 0x0800, true)]
    public void SameRoomRouteCandidateChecksBothSidesOfItsWallEdge(int wallTileX, int slotFlags, bool expected)
    {
        var (context, _) = World();
        context.World.Host = null;
        var map = wallTileX == 9
            ? RoomWithSingleRouteSegment(9, 32, 0x01)
            : RoomWithNonDividingVerticalWall(wallTileX);
        Assert.Equal(1, map.RoomCount);
        context.World.LotRoomMaps[1] = map;
        context.World.SetObjectPosition(context.Me, new ObjectPosition(8 * 16 + 8, 32 * 16 + 8, 1));
        var anchor = context.StackObject!;
        context.World.SetObjectPosition(anchor, new ObjectPosition(8 * 16 + 8, 32 * 16 + 8, 1));
        var slot = RoutingSlotRequest.FromDefinition(new Slot(3, 16, 0, 0,
            0, 0, 0, (uint)slotFlags, -1, 16, 16, 16, 0, 0, 0.1875f, 0, -2, 16));

        var destination = RouteExecutor.SelectDestination(context.World, (XPerson)context.Me, anchor, slot);

        if (expected)
            Assert.Equal(new ObjectPosition(9 * 16 + 8, 32 * 16 + 8, 1), destination?.Position);
        else
            Assert.Null(destination);
    }

    [Fact]
    public void SameRoomRouteCandidateAtThePersonsPositionSkipsTheEndpointGate()
    {
        var (context, _) = World();
        context.World.Host = null;
        var map = RoomWithNonDividingVerticalWall(8);
        context.World.LotRoomMaps[1] = map;
        var position = new ObjectPosition(8 * 16 + 8, 32 * 16 + 8, 1);
        context.World.SetObjectPosition(context.Me, position);
        var anchor = context.StackObject!;
        context.World.SetObjectPosition(anchor, position);
        AddDefinitionBackedReferenceSlot(anchor);
        var slot = RoutingSlotRequest.FromDefinition(new Slot(3, 0, 0, 0,
            0, 0, 0, 0xff, 0, 16, 16, 16, 0, 0, 0.1875f, 0, -2, 16));

        var destination = RouteExecutor.SelectDestination(context.World, (XPerson)context.Me, anchor, slot);

        Assert.Equal(position, destination?.Position);
    }

    [Fact]
    public void NativeRouteAppliesTheSlotSelectedArrivalFacing()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        AllowPersonOverlapWithRouteAnchor(context.StackObject!);
        context.World.SetObjectPosition(context.Me, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        context.World.SetObjectPosition(context.StackObject!, new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1));
        AddDefinitionBackedReferenceSlot(context.StackObject!);
        context.StackObject![ObjectData.Direction] = 4;
        var slot = new RoutingSlotRequest
        {
            Flags = 0x1008,
            Facing = 3,
            SnapTargetSlot = 0,
            MinProximity = 16,
            MaxProximity = 16,
            OptimalProximity = 16,
        };
        context.World.LotRoutingSlots.Add(slot);

        var result = ExecuteAndTick(context, Operand(0, 2));
        for (var callback = 0; callback < 256 && result != PrimitiveResult.True; callback++)
            result = ExecuteAndTick(context, Operand(0, 2));

        Assert.Equal(PrimitiveResult.True, result);
        // The authored facing 3 is relative to anchor direction 4 and wraps to direction 7.
        Assert.Equal((short)7, context.Me[ObjectData.Direction]);
    }

    [Fact]
    public void NativeRouteAppliesFinalPlacementBeforeTheSlotSelectedFacing()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        var sameTile = new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1);
        context.World.SetObjectPosition(context.Me, sameTile);
        context.World.SetObjectPosition(context.StackObject!, sameTile);
        // FUN_00589d4b state 2 rejects the completed root through placement (+0x54); the shared tile needs the
        // anchor's person-overlap flag.
        AllowPersonOverlapWithRouteAnchor(context.StackObject!);
        AddDefinitionBackedReferenceSlot(context.StackObject!);
        context.Me.TypeRecord = new TypeRecord(context.Me.Id) { FootprintMask = 0x4444 };
        context.Me[ObjectData.Direction] = 1;
        ((XPerson)context.Me)[PersonData.FootprintExtension] = 0x1111;
        ((XPerson)context.Me).RefreshFootprintBounds();
        context.World.LotRoutingSlots.Add(new RoutingSlotRequest { Flags = 0x1001, Facing = 0, SnapTargetSlot = 0 });

        var result = RunRoute(context, Operand(0, 2));

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal((short)0, context.Me[ObjectData.Direction]);
        // Every segment of this same-tile route is degenerate, so the route-distance block reaches the terminal
        // facing entry in the first Tick (SAnimator.s 0x10351748, 0x10350BE4) and the root turns to the arrival
        // facing before state 2 places it. The final placement therefore records that heading.
        Assert.Equal((short)0, ((XPerson)context.Me).FootprintBoundsDirection);
    }

    [Fact]
    public void NativeRouteFacesBackAcrossTheAnchorWhenNoApproachDirectionIsEnabled()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        context.World.SetObjectPosition(context.Me, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        context.World.SetObjectPosition(context.StackObject!, new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1));
        context.StackObject![ObjectData.Direction] = 4;
        context.Me[ObjectData.Direction] = 2;
        var slot = new RoutingSlotRequest { Flags = 0x1000, X = 16 };
        context.World.LotRoutingSlots.Add(slot);

        var result = ExecuteAndTick(context, Operand(0, 2));
        for (var callback = 0; callback < 256 && result != PrimitiveResult.True; callback++)
            result = ExecuteAndTick(context, Operand(0, 2));

        Assert.Equal(PrimitiveResult.True, result);
        // With no authored approach bit, FUN_0045c4fa Facing -2 faces back toward the anchor origin from the slot offset.
        Assert.Equal((short)2, context.Me[ObjectData.Direction]);
    }

    [Fact]
    public void NativeRouteUsesTheRelativeObjectPlacementBranchForSlotCandidates()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        AllowPersonOverlapWithRouteAnchor(context.StackObject!);
        context.World.SetObjectPosition(context.Me, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        context.World.SetObjectPosition(context.StackObject!, new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1));
        context.StackObject!.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 2 });
        // Walking uses the floor bit as well as the destination's slot-height bit.
        context.Me[ObjectData.AllowedHeightFlags] = 3;
        context.World.LotRoutingSlots.Add(new RoutingSlotRequest { SnapTargetSlot = 0, X = 16 });

        var result = ExecuteAndTick(context, Operand(0, 2));
        for (var callback = 0; callback < 256 && result != PrimitiveResult.True; callback++)
            result = ExecuteAndTick(context, Operand(0, 2));

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal(context.StackObject!.Position, context.Me.Position);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void FreePositionRouteCandidateUsesOrdinaryAllowedHeightValidation(int allowedHeightFlags, bool expected)
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        context.World.SetObjectPosition(context.Me, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        context.World.SetObjectPosition(context.StackObject!, new ObjectPosition(10 * 16 + 8, 5 * 16 + 8, 1));
        context.Me[ObjectData.AllowedHeightFlags] = (short)allowedHeightFlags;
        var slot = new RoutingSlotRequest { X = 16 };
        context.World.LotRoutingSlots.Add(slot);

        var destination = RouteExecutor.SelectDestination(context.World, (XPerson)context.Me,
            context.StackObject!, slot);

        Assert.Equal(expected, destination is not null);
        if (expected)
        {
            Assert.Equal(new ObjectPosition(10 * 16 + 8 + 16, 5 * 16 + 8, 1), destination?.Position);
            var result = ExecuteAndTick(context, Operand(0, 2));
            for (var callback = 0; callback < 256 && result != PrimitiveResult.True; callback++)
                result = ExecuteAndTick(context, Operand(0, 2));
            Assert.Equal(PrimitiveResult.True, result);
            Assert.Equal(destination?.Position, context.Me.Position);
        }
    }

    [Fact]
    public void PendingFollowDoesNotRunPlacementOrReportArrivalFromTheBhavCallback()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.CaptureExecutionTrace = true;
        context.World.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var start = new ObjectPosition(88, 88, 1);
        context.World.SetObjectPosition(person, start);
        // A solid object at the root would reject ordinary placement if the pending callback invoked it.
        var blocker = context.World.Add(new XObject(500, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(500) { FootprintMask = 0x4444 },
        });
        context.World.SetObjectPosition(blocker, start);
        RouteExecutor.BeginDirectWalkFollow(context.World, person, context.Frame,
            [start, start with { X = 104 }]);

        Assert.Equal(PrimitiveResult.Stop, RouteExecutor.Continue(context.World, person, context.Frame, null));
        Assert.Equal(start, person.Position);
        Assert.Equal(1, person.NativeRoute!.AnimatorFollowState);
        Assert.False(person.NativeRoute.FollowPlacementRejected);
        Assert.DoesNotContain(context.World.NativeRouteTrace, row => row.Phase == "follow-placement-rejected");
    }

    [Fact]
    public void OrdinaryRouteUsesChooseStartingPointActorCoordinatesInsteadOfConstructorZeroes()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        AllowPersonOverlapWithRouteAnchor(context.StackObject!);
        var start = new ObjectPosition(88, 88, 1);
        context.World.SetObjectPosition(context.Me, start);
        context.World.SetObjectPosition(context.StackObject!, new ObjectPosition(168, 88, 1));
        AddDefinitionBackedReferenceSlot(context.StackObject!);
        context.World.LotRoutingSlots.Add(new RoutingSlotRequest
        {
            Flags = 0xff, SnapTargetSlot = 0, MinProximity = 16, MaxProximity = 16, OptimalProximity = 16,
        });

        Assert.Equal(PrimitiveResult.Stop, ExecuteAndTick(context, Operand(0, 2)));
        var person = Assert.IsType<XPerson>(context.Me);
        for (var tick = 0; tick < 32 && person.NativeRoute?.MotionPath is null; tick++)
        {
            ExecuteAndTick(context, Operand(0, 2));
        }
        var path = person.NativeRoute!.MotionPath!;
        Assert.NotEmpty(path);
        Assert.Equal((start.X, start.Y), (path[0].X, path[0].Y));
    }

    [Fact]
    public void CompletedFollowPlacesTheRootWithoutRequiringAnAnimationPractice()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        AllowPersonOverlapWithRouteAnchor(context.StackObject!);
        var person = Assert.IsType<XPerson>(context.Me);
        context.World.SetObjectPosition(person, new ObjectPosition(88, 88, 1));
        context.World.SetObjectPosition(context.StackObject!, new ObjectPosition(168, 88, 1));
        AddDefinitionBackedReferenceSlot(context.StackObject!);
        context.World.LotRoutingSlots.Add(new RoutingSlotRequest { Flags = 0xff, SnapTargetSlot = 0 });
        Assert.Equal(PrimitiveResult.Stop, GoToRoutingSlotPrimitive.Execute(context, Operand(0, 2)));
        person.NativeRoute = person.NativeRoute! with
        {
            AnimatorFollowState = 2, MotionAnimationIndex = -1, MotionX = 168, MotionY = 88,
        };
        person.AnimatorRootX = 168;
        person.AnimatorRootY = 88;
        person.AnimationPlayback = null;
        context.World.StandardWalkFallbackAnimationTable.Clear();

        // FUN_00589d4b state 2 only projects/checks/places the root, then callback state 9 ends follow.
        Assert.Equal(PrimitiveResult.True, RouteExecutor.Continue(context.World, person, context.Frame, null));
        Assert.Equal(new ObjectPosition(168, 88, 1), person.Position);
        Assert.Null(person.NativeRoute);
        Assert.False(context.Frame.Waiting);
    }

    [Fact]
    public void NativeRoutePacesWaypointsUntilArrival()
    {
        var (context, _) = World();
        context.World.CaptureExecutionTrace = true;
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        AllowPersonOverlapWithRouteAnchor(context.StackObject!);
        context.World.SetObjectPosition(context.Me, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        context.World.SetObjectPosition(context.StackObject!, new ObjectPosition(10 * 16 + 8, 5 * 16 + 8, 1));
        AddDefinitionBackedReferenceSlot(context.StackObject!);
        context.World.LotRoutingSlots.Add(new RoutingSlotRequest { Flags = 0xff, SnapTargetSlot = 0, MinProximity = 16, MaxProximity = 16, OptimalProximity = 16 });

        var result = ExecuteAndTick(context, Operand(0, 2));
        Assert.Equal(PrimitiveResult.Stop, result);
        // The first state-3 Tick redispatches into state 9, and the route-distance block skips the seed's
        // zero-length segment within that Tick (SAnimator.s 0x1034F4EC, 0x10351748, 0x10350BE4), so the
        // root leaves the start point before the next route callback.
        Assert.True(((XPerson)context.Me).AnimatorRootX > 5 * 16 + 8);
        Assert.Equal(5 * 16 + 8, context.Me.Position.Y);
        Assert.Equal(1, ((XPerson)context.Me).NativeRoute!.AnimatorFollowState);
        Assert.True(context.Frame.Waiting);

        for (var callback = 0; callback < 512 && result == PrimitiveResult.Stop; callback++)
            result = ExecuteAndTick(context, Operand(0, 2));

        Assert.True(result == PrimitiveResult.True,
            System.Text.Json.JsonSerializer.Serialize(context.World.NativeRouteTrace.TakeLast(10)));
        Assert.Equal(new ObjectPosition(10 * 16 + 8, 5 * 16 + 8, 1), context.Me.Position);
        Assert.False(context.Frame.Waiting);
        Assert.Null(((XPerson)context.Me).NativeRoute);
    }

    [Theory]
    [InlineData(0, -1, 0)]
    [InlineData(1, 0, 2)]
    [InlineData(0, 1, 4)]
    [InlineData(-1, 0, 6)]
    public void NativeRouteSetsFacingFromEachAcceptedCardinalWaypoint(int dx, int dy, short expectedDirection)
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        context.World.SetObjectPosition(context.Me, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        context.World.SetObjectPosition(context.StackObject!, new ObjectPosition((5 + dx * 4) * 16 + 8,
            (5 + dy * 4) * 16 + 8, 1));
        context.World.LotRoutingSlots.Add(new RoutingSlotRequest { X = (short)(-dx * 16), Y = (short)(-dy * 16) });

        var start = context.Me.Position;
        var result = ExecuteAndTick(context, Operand(0, 2));
        // The first callback enters follow state 1 at the start tile; tick until root motion leaves it.
        for (var callback = 0; callback < 64 && result == PrimitiveResult.Stop && context.Me.Position == start; callback++)
            result = ExecuteAndTick(context, Operand(0, 2));

        Assert.Equal(PrimitiveResult.Stop, result);
        Assert.Equal(dx > 0, context.Me.Position.X > 5 * 16 + 8);
        Assert.Equal(dx < 0, context.Me.Position.X < 5 * 16 + 8);
        Assert.Equal(dy > 0, context.Me.Position.Y > 5 * 16 + 8);
        Assert.Equal(dy < 0, context.Me.Position.Y < 5 * 16 + 8);
        // Without standing-turn clips the Sim enters gait unturned; the Mac follow/gait block (SAnimator.s
        // 0x10350BF8-0x10350E28) wraps and clamps each heading step toward the waypoint, so every gait tick
        // shortens the heading error until arrival applies the slot's facing.
        var person = (XPerson)context.Me;
        static float Error(float direction, short target)
        {
            var delta = MathF.Abs(((direction - target) % 8 + 8) % 8);
            return MathF.Min(delta, 8 - delta);
        }
        var previous = Error(person.AnimatorRootDirection!.Value, expectedDirection);
        for (var callback = 0; callback < 128 && result == PrimitiveResult.Stop && person.AnimatorControlState == 9; callback++)
        {
            result = ExecuteAndTick(context, Operand(0, 2));
            if (person.AnimatorControlState != 9) break;
            var error = Error(person.AnimatorRootDirection!.Value, expectedDirection);
            Assert.True(error < previous || error < 0.01f, $"heading {person.AnimatorRootDirection} moved away from {expectedDirection}");
            previous = error;
        }
    }

    [Theory]
    [InlineData(1, 3, 4)]
    [InlineData(1, 2, 3)]
    [InlineData(2, 1, 3)]
    [InlineData(3, 1, 2)]
    [InlineData(1, -2, 1)]
    [InlineData(-1, -2, 7)]
    [InlineData(-1, 2, 5)]
    [InlineData(-3, 1, 6)]
    public void WaypointFacingMatchesSimsOctantThresholds(int dx, int dy, short expectedDirection)
    {
        // FUN_00452129 uses strict absX * 2 < absY and inclusive absX <= absY * 2 comparisons.
        Assert.Equal(expectedDirection, RouteExecutor.DirectionToWaypoint((0, 0), (dx, dy)));
    }

    [Theory]
    [InlineData(0, 18)]
    [InlineData(1, 17)]
    [InlineData(2, 15)]
    [InlineData(3, 13)]
    [InlineData(4, 11)]
    [InlineData(5, 12)]
    [InlineData(6, 14)]
    [InlineData(7, 16)]
    public void RouteOctantsSelectMacStandingAdjustSkills(short direction, short skill)
    {
        Assert.Equal(skill, RouteExecutor.DirectionalWalkAnimation(direction));
    }

    [Theory]
    [InlineData(0, 6, 19)]
    [InlineData(1, 11, 22)]
    public void StandingAdjustCompletionEntersSourceGaitStateAndClearsTransitionFlags(
        short walkStyle, int controlState, short startSkill)
    {
        var (context, _) = World();
        var world = context.World;
        world.Host = null;
        world.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        person[ObjectData.WalkStyle] = walkStyle;
        if (walkStyle == 1)
        {
            var runStartSamples = Enumerable.Range(0, 31)
                .Select(i => new System.Numerics.Vector3(i * 0.5f, 0f, 0f)).ToArray();
            world.StandardWalkFallbackAnimationTable[(150, 22)] = new AvatarAnimationClip("run22",
                runStartSamples.Length, [], null, 15f, new System.Numerics.Vector3(15f, 0f, 0f),
                runStartSamples, 1000f);
        }
        var anchor = context.StackObject!;
        person[ObjectData.PlacementFlags] |= 1;
        world.PlaceObject(person, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        anchor[ObjectData.Flags] = 4;
        world.PlaceObject(anchor, new ObjectPosition(10 * 16 + 8, 5 * 16 + 8, 1));
        AddDefinitionBackedReferenceSlot(anchor);
        world.LotRoutingSlots.Add(new RoutingSlotRequest
            { Flags = 0xff, SnapTargetSlot = 0, MinProximity = 16, MaxProximity = 16, OptimalProximity = 16 });
        Assert.Equal(PrimitiveResult.Stop,
            RouteExecutor.Start(world, person, context.Frame, anchor, new RoutingSlotRequest(), null));

        var route = person.NativeRoute!;
        // The route vector repeats the seed when the Sim stands on its cell's representative point; Tick skips
        // that zero-length segment (SAnimator.s 0x10351748), so the walk heads for the first distinct point.
        var firstStep = route.MotionPath!.First(point => point.X != person.Position.X || point.Y != person.Position.Y);
        var direction = RouteExecutor.DirectionToWaypoint((person.Position.X, person.Position.Y),
            (firstStep.X, firstStep.Y));
        person.NativeRoute = route with
        {
            MotionAnimationIndex = 11,
            MotionDirection = direction,
            MotionFrame = 30f,
            MotionPracticeFrame = 30f,
        };
        person.AnimatorGaitEntryState = 1;
        person.AnimatorPoolTransitionState = 1;

        RouteExecutor.Continue(world, person, context.Frame, null);

        Assert.Equal(controlState, person.AnimatorControlState); // Mac states 6/11 start WalkingStart/RunningStart.
        Assert.Equal(0, person.AnimatorGaitEntryState); // +0x194
        Assert.Equal(0, person.AnimatorPoolTransitionState); // +0x288
        Assert.Equal(startSkill, person.NativeRoute!.MotionAnimationIndex);
        var startRoute = person.NativeRoute;
        Assert.Equal(startRoute.MotionX, startRoute.StartRootOriginX!.Value);
        Assert.Equal(startRoute.MotionY, startRoute.StartRootOriginY!.Value);
        Assert.Equal(80f, startRoute.StartRootTargetX!.Value - startRoute.StartRootOriginX.Value);
        Assert.InRange(MathF.Abs(startRoute.StartRootTargetY!.Value - startRoute.StartRootOriginY.Value), 0f, 0.001f);

        person.NativeRoute = startRoute with { MotionPracticeFrame = 15.5f };
        RouteExecutor.TickStandingStartRootInterpolation(world, person);
        Assert.Equal(startRoute.MotionX + 40f, person.AnimatorRootX);
        Assert.InRange(MathF.Abs(person.AnimatorRootY!.Value - startRoute.MotionY), 0f, 0.001f);
    }

    [Fact]
    public void StateFiveInterpolatesTurnRootAndRestoresItsSavedDirectionAtPracticeEnd()
    {
        var (context, _) = World();
        var person = (XPerson)context.Me;
        var world = context.World;
        var samples = Enumerable.Repeat(System.Numerics.Vector3.Zero, 4).ToArray();
        var clip = new AvatarAnimationClip("turn", samples.Length, [], StandardRootMotionSamples: samples);
        world.StandardWalkFallbackAnimationTable[(150, 11)] = clip;
        person.AnimatorControlState = 5;
        person.AnimatorGaitEntryState = 1;
        person.AnimatorPoolTransitionState = 1;
        person.AnimatorRootX = 20f;
        person.AnimatorRootY = 30f;
        person.AnimatorRootDirection = 2f;
        person.AnimationPlayback = new AvatarAnimationPlayback(clip, false, false);
        person.NativeRoute = new RouteState(0, default, new RoutingSlotRequest(),
            MotionAnimationIndex: 11, MotionPracticeFrame: 1f, MotionX: 20f, MotionY: 30f)
        {
            TurnRootOriginX = 20f,
            TurnRootOriginY = 30f,
            TurnRootTargetX = 28f,
            TurnRootTargetY = 38f,
            TurnRootSavedDirection = 5f,
        };

        world.TickAvatarAnimations(person);

        Assert.Equal(22f, person.AnimatorRootX);
        Assert.Equal(32f, person.AnimatorRootY);
        Assert.Equal(0, person.AnimatorGaitEntryState);
        Assert.Equal(0, person.AnimatorPoolTransitionState);

        person.NativeRoute = person.NativeRoute! with { MotionPracticeFrame = 4f };
        world.TickAvatarAnimations(person);

        Assert.Equal(3, person.AnimatorControlState);
        Assert.Equal(28f, person.AnimatorRootX);
        Assert.Equal(38f, person.AnimatorRootY);
        Assert.Equal(5f, person.AnimatorRootDirection);
        Assert.Null(person.AnimationPlayback);
        Assert.Null(person.NativeRoute!.TurnRootTargetX);
    }

    [Fact]
    public void StateThreeEntersStateFiveForAShortAlignedTurnUsingTheAuthoredOffset()
    {
        var (context, _) = World();
        var person = (XPerson)context.Me;
        var world = context.World;
        var samples = Enumerable.Repeat(System.Numerics.Vector3.Zero, 4).ToArray();
        var clip = new AvatarAnimationClip("turn-east", samples.Length, [],
            StandardOffset: new System.Numerics.Vector3(2f, 0f, 0f), StandardRootMotionSamples: samples);
        world.StandardWalkFallbackAnimationTable[(150, 15)] = clip;
        person.AnimatorControlState = 3;
        person.AnimatorRootX = 10f;
        person.AnimatorRootY = 10f;
        person.AnimatorRootDirection = 2f;
        person.AnimatorWalkingScale = 1f;
        person.NativeRoute = new RouteState(0, default, new RoutingSlotRequest(),
            MotionTargetX: 12, MotionTargetY: 10, MotionX: 10f, MotionY: 10f,
            MotionAnimationIndex: 15, MotionDirection: 2);

        world.TickAvatarAnimations(person);

        Assert.Equal(5, person.AnimatorControlState);
        Assert.Equal((float?)10f, person.NativeRoute!.TurnRootOriginX);
        Assert.Equal((float?)10f, person.NativeRoute.TurnRootOriginY);
        Assert.Equal((float?)10f, person.NativeRoute.TurnRootTargetX);
        Assert.Equal((float?)10f, person.NativeRoute.TurnRootTargetY);
        Assert.Equal((float?)1f, person.NativeRoute.TurnRootPracticeScale);
        Assert.Equal(15, person.NativeRoute.MotionAnimationIndex);
        Assert.True(person.AnimationPlayback is { RouteDriven: true, Frame: 0f });
    }

    [Fact]
    public void StateThreeSnapsToTheRoutePointBeforeEnteringStateFiveInsideTheMacSnapDistance()
    {
        var (context, _) = World();
        var person = (XPerson)context.Me;
        var world = context.World;
        var samples = Enumerable.Repeat(System.Numerics.Vector3.Zero, 4).ToArray();
        world.StandardWalkFallbackAnimationTable[(150, 15)] = new AvatarAnimationClip("turn-east", samples.Length, [],
            StandardOffset: new System.Numerics.Vector3(2f, 0f, 0f), StandardRootMotionSamples: samples);
        person.AnimatorControlState = 3;
        // 0.25 FTile short of the point: inside the 0.5 snap distance, but not the exact pose match that
        // redispatches follow completion instead (SAnimator.s 0x1034F438–0x1034F4F4).
        person.AnimatorRootX = 9.75f;
        person.AnimatorRootY = 10f;
        person.AnimatorRootDirection = 2f;
        person.AnimatorWalkingScale = 1f;
        person.NativeRoute = new RouteState(0, default, new RoutingSlotRequest(),
            MotionTargetX: 10, MotionTargetY: 10, MotionX: 9.75f, MotionY: 10f,
            MotionAnimationIndex: 15, MotionDirection: 2);

        world.TickAvatarAnimations(person);

        Assert.Equal(5, person.AnimatorControlState);
        Assert.Equal(10f, person.AnimatorRootX);
        Assert.Equal(10f, person.AnimatorRootY);
        Assert.Equal((float?)10f, person.NativeRoute!.TurnRootOriginX);
        Assert.Equal((float?)10f, person.NativeRoute.TurnRootOriginY);
        Assert.Equal((float?)8f, person.NativeRoute.TurnRootTargetX);
        Assert.Equal((float?)10f, person.NativeRoute.TurnRootTargetY);
    }

    [Fact]
    public void StateThreeStartsMacStateFourBodyTurnAndRestoresItsRouteHeadingAtPracticeEnd()
    {
        var (context, _) = World();
        var person = (XPerson)context.Me;
        var world = context.World;
        var samples = Enumerable.Repeat(System.Numerics.Vector3.Zero, 4).ToArray();
        world.StandardWalkFallbackAnimationTable[(150, 5)] = new AvatarAnimationClip("body-turn", samples.Length, [],
            StandardRootMotionSamples: samples);
        person.AnimatorControlState = 3;
        person.AnimatorRootX = 10f;
        person.AnimatorRootY = 10f;
        person.AnimatorRootDirection = 6f;
        person.AnimatorWalkingScale = 1f;
        person.AnimatorGaitEntryState = 1;
        person.AnimatorPoolTransitionState = 1;
        person.NativeRoute = new RouteState(0, default, new RoutingSlotRequest(),
            MotionTargetX: 14, MotionTargetY: 10, MotionX: 10f, MotionY: 10f,
            MotionAnimationIndex: 15, MotionDirection: 2);

        world.TickAvatarAnimations(person);

        Assert.Equal(4, person.AnimatorControlState);
        Assert.Equal(5, person.NativeRoute!.MotionAnimationIndex);
        Assert.Equal((float?)2f, person.NativeRoute.TurnRootSavedDirection);
        Assert.Equal((float?)5.8f, person.NativeRoute.TurnRootStartDirection);
        Assert.Equal((float?)6f, person.NativeRoute.TurnRootPracticeDirection);
        Assert.Equal(0, person.AnimatorGaitEntryState);
        Assert.Equal(0, person.AnimatorPoolTransitionState);
        Assert.True(person.AnimationPlayback is { RouteDriven: true });

        person.NativeRoute = person.NativeRoute with { MotionPracticeFrame = 2f };
        world.TickAvatarAnimations(person);
        Assert.Equal(12f, person.AnimatorRootX);
        Assert.Equal(10f, person.AnimatorRootY);
        Assert.Equal(5.9f, person.AnimatorRootDirection);

        person.NativeRoute = person.NativeRoute! with { MotionPracticeFrame = 4f };
        world.TickAvatarAnimations(person);
        Assert.Equal(3, person.AnimatorControlState);
        Assert.Equal(14f, person.AnimatorRootX);
        Assert.Equal(10f, person.AnimatorRootY);
        Assert.Equal(2f, person.AnimatorRootDirection);
        Assert.Null(person.AnimationPlayback);
        Assert.Null(person.NativeRoute!.TurnRootTargetX);
    }

    [Theory]
    [InlineData(-3.01f, 5, -4f)]
    [InlineData(-3f, 7, -2f)]
    [InlineData(-1.5f, 9, -1f)]
    [InlineData(-0.5f, 10, 0f)]
    [InlineData(0.5f, 10, 0f)]
    [InlineData(0.51f, 8, 1f)]
    [InlineData(1.5f, 8, 1f)]
    [InlineData(1.51f, 6, 2f)]
    [InlineData(3f, 6, 2f)]
    [InlineData(3.01f, 4, 4f)]
    public void StateFourBodyTurnUsesMacHeadingBuckets(float headingError, short expectedSkill, float expectedCorrection)
    {
        var (skill, correction) = RouteExecutor.BodyTurnSkillForHeadingError(headingError);

        Assert.Equal(expectedSkill, skill);
        Assert.Equal(expectedCorrection, correction);
    }

    [Theory]
    [InlineData(4, 10f, 2f, 0)]
    [InlineData(2, 10f, 1.7f, 0)]
    [InlineData(2, 10f, 2f, 2)]
    public void StateThreeRejectsTurnRootSetupOutsideTheMacDistanceAndHeadingGates(
        int deltaX, float targetY, float rootDirection, short bodyStringMode)
    {
        var (context, _) = World();
        var person = (XPerson)context.Me;
        var world = context.World;
        var samples = Enumerable.Repeat(System.Numerics.Vector3.Zero, 4).ToArray();
        world.StandardWalkFallbackAnimationTable[(150, 15)] = new AvatarAnimationClip("turn-east", samples.Length, [],
            StandardOffset: new System.Numerics.Vector3(2f, 0f, 0f), StandardRootMotionSamples: samples);
        person.AnimatorControlState = 3;
        person.AnimatorRootX = 10f;
        person.AnimatorRootY = 10f;
        person.AnimatorRootDirection = rootDirection;
        person.AnimatorWalkingScale = 1f;
        person.AnimatorBodyStringMode = bodyStringMode;
        person.NativeRoute = new RouteState(0, default, new RoutingSlotRequest(),
            MotionTargetX: 10 + deltaX, MotionTargetY: (int)targetY, MotionX: 10f, MotionY: 10f,
            MotionAnimationIndex: 15, MotionDirection: 4);

        Assert.False(RouteExecutor.TryBeginRouteTurnRoot(world, person));
        Assert.Equal(3, person.AnimatorControlState);
        Assert.Null(person.NativeRoute!.TurnRootOriginX);
    }

    [Fact]
    public void StandingTurnSkillUsesTheMacAtan2SectorBoundary()
    {
        var below = RouteExecutor.StandingTurnAnimationForVector(1f, 0.4142f);
        var above = RouteExecutor.StandingTurnAnimationForVector(1f, 0.4143f);

        Assert.Equal(15, below);
        Assert.Equal(13, above);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(2, 0, 0)]
    [InlineData(4, 0, 0)]
    [InlineData(8, 1, 1)]
    [InlineData(12, 0, 0)]
    public void AnimatorDispatchCasesApplyTheirMacTransitionFlagWrites(
        int controlState, int gaitEntryState, int poolTransitionState)
    {
        var (context, _) = World();
        var person = (XPerson)context.Me;
        person.AnimatorControlState = controlState;
        person.AnimatorGaitEntryState = -1;
        person.AnimatorPoolTransitionState = -1;

        context.World.TickAvatarAnimations(person);

        Assert.Equal(gaitEntryState, person.AnimatorGaitEntryState);
        Assert.Equal(poolTransitionState, person.AnimatorPoolTransitionState);
    }

    [Fact]
    public void StateFiveOffsetRotatesTheAuthoredRootTranslationBeforeSubtractingIt()
    {
        var offset = new System.Numerics.Vector3(1f, 2f, 3f);

        var noTurn = RouteExecutor.TurnRootTarget(10f, 20f, 0f, offset);
        var quarterTurn = RouteExecutor.TurnRootTarget(10f, 20f, MathF.PI / 2f, offset);

        Assert.Equal((9f, 18f), noTurn);
        Assert.InRange(MathF.Abs(quarterTurn.X - 8f), 0f, 0.00001f);
        Assert.InRange(MathF.Abs(quarterTurn.Y - 19f), 0f, 0.00001f);
    }

    [Fact]
    public void MovingPersonRefreshesSavedFootprintBoundsAtEachRouteStep()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.Me.TypeRecord = new TypeRecord(1) { FootprintMask = 0 };
        context.World.LotRoomMaps[1] = OpenRoom();
        context.World.SetObjectPosition(context.Me, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        context.World.SetObjectPosition(context.StackObject!, new ObjectPosition(10 * 16 + 8, 5 * 16 + 8, 1));
        AddDefinitionBackedReferenceSlot(context.StackObject!);
        context.World.LotRoutingSlots.Add(new RoutingSlotRequest
            { Flags = 0xff, SnapTargetSlot = 0, MinProximity = 16, MaxProximity = 16, OptimalProximity = 16 });

        var result = ExecuteAndTick(context, Operand(0, 2));

        Assert.Equal(PrimitiveResult.Stop, result);
        var position = context.Me.Position;
        Assert.NotEqual(new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1), position);
        var bounds = context.Me.FootprintBounds;
        Assert.Equal(position.X + 3, bounds.MaxX);
        Assert.Equal(position.Y + 3, bounds.MaxY);
        Assert.Equal(position.X - 3, bounds.MinX);
        Assert.Equal(position.Y - 3, bounds.MinY);
    }

    [Fact]
    public void MovingSavedObjectTranslatesItsSourceDerivedFootprintBounds()
    {
        var world = new SimulationWorld();
        var item = world.Add(new XObject(7, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(1) { FootprintMask = 0x3210 },
            Position = new ObjectPosition(100, 200, 1),
            FootprintBounds = new FootprintBounds(108, 211, 94, 196),
            FootprintBoundsDirection = 0,
        });

        world.SetObjectPosition(item, new ObjectPosition(132, 184, 1));

        // FUN_00433ef5 recomputes the saved object's bounds from its type extents after moving it by (32, -16).
        Assert.Equal(new FootprintBounds(139, 190, 127, 176), item.FootprintBounds);
    }

    [Fact]
    public void MovingSavedObjectRecomputesFootprintBoundsAfterDirectionChanges()
    {
        var world = new SimulationWorld();
        var item = world.Add(new XObject(7, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(1) { FootprintMask = 0x3210 },
            Position = new ObjectPosition(100, 200, 1),
            FootprintBounds = new FootprintBounds(108, 208, 92, 192),
            FootprintBoundsDirection = 0,
        });
        item[ObjectData.Direction] = 2;

        world.SetObjectPosition(item, new ObjectPosition(100, 200, 1));

        // FUN_00433dbb rotates the four type-record extent nibbles for the current direction.
        Assert.Equal(new FootprintBounds(108, 207, 94, 195), item.FootprintBounds);
        Assert.Equal((short)2, item.FootprintBoundsDirection);
    }

    [Fact]
    public void FootprintBoundsUseSourceExtentSignsAndTemporaryRouteExtension()
    {
        var person = new XPerson(100, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(1) { FootprintMask = 0x3210 },
            Position = new ObjectPosition(88, 88, 1),
        };

        person.RefreshFootprintBounds();
        Assert.Equal(new FootprintBounds(90, 89, 88, 85), person.FootprintBounds);

        person[PersonData.FootprintExtension] = 0x0010;
        person.RefreshFootprintBounds();
        Assert.Equal(new FootprintBounds(91, 89, 88, 85), person.FootprintBounds);

        person[ObjectData.Direction] = 1;
        person.RefreshFootprintBounds();
        Assert.Equal(new FootprintBounds(91, 90, 87, 88), person.FootprintBounds);
    }

    [Fact]
    public void ActiveCancellationKeepsDeferredFollowCleanupWhileThePracticeIsNotInterruptible()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        context.World.SetObjectPosition(context.Me, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        context.World.SetObjectPosition(context.StackObject!, new ObjectPosition(10 * 16 + 8, 5 * 16 + 8, 1));
        AddDefinitionBackedReferenceSlot(context.StackObject!);
        context.World.LotRoutingSlots.Add(new RoutingSlotRequest { Flags = 0xff, SnapTargetSlot = 0, MinProximity = 16, MaxProximity = 16, OptimalProximity = 16 });
        var person = (XPerson)context.Me;
        person.CurrentActionRecord = new ActionQueueSlot { Sequence = 77, Owner = context.StackObject };
        person.GenericSimsCall02GateWordA84 = 77;
        person.ActivePriority = 50;

        Assert.Equal(PrimitiveResult.Stop, GoToRoutingSlotPrimitive.Execute(context, Operand(0, 2)));
        person.AnimatorRouteTransitionState = 1; // Isolate deferred EndFollow after the route-entry dispatch has advanced.
        Assert.True(context.World.CancelInteractionAction(person, 77));
        var positionAtCancellation = person.Position;

        Assert.Equal(PrimitiveResult.Stop, GoToRoutingSlotPrimitive.Execute(context, Operand(0, 2)));
        // The next Tick enters gait; StartSkill would replace a standing-turn practice with a fresh, interruptible
        // one (Skill::NewPractice +0x68 = -1). Make the non-interruptible practice the gait skill it keeps.
        var gaitClip = RouteExecutor.ResolveWalkAnimation(context.World, person, 20)!;
        person.AnimatorCurrentSkill = 20;
        person.NativeRoute = person.NativeRoute! with { MotionAnimationIndex = 20 };
        person.AnimationPlayback = new AvatarAnimationPlayback(gaitClip, backwards: false, loop: true)
            { RouteDriven = true, Interruptible = false };
        Assert.True(person.NativeRoute?.EndFollowPending);
        Assert.True(context.Frame.Waiting);
        context.World.TickAvatarAnimations(person);
        Assert.Equal(1, person.NativeRoute?.AnimatorFollowState);
        Assert.True(person.NativeRoute?.EndFollowPending);

        Assert.Equal(PrimitiveResult.Stop, GoToRoutingSlotPrimitive.Execute(context, Operand(0, 2)));

        Assert.Equal((short)0, person.ActivePriority);
        Assert.True(positionAtCancellation != person.Position);
        Assert.True(person.ActiveActionCancelRequested);
        Assert.True(context.Frame.Waiting);
        Assert.True(person.NativeRoute?.EndFollowPending);
    }

    [Fact]
    public void ActiveCancellationConsumesDeferredFollowCleanupWhenThePracticeAllowsIt()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        context.World.SetObjectPosition(context.Me, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        context.World.SetObjectPosition(context.StackObject!, new ObjectPosition(10 * 16 + 8, 5 * 16 + 8, 1));
        AddDefinitionBackedReferenceSlot(context.StackObject!);
        context.World.LotRoutingSlots.Add(new RoutingSlotRequest { Flags = 0xff, SnapTargetSlot = 0, MinProximity = 16, MaxProximity = 16, OptimalProximity = 16 });
        var person = (XPerson)context.Me;
        person.CurrentActionRecord = new ActionQueueSlot { Sequence = 79, Owner = context.StackObject };
        person.GenericSimsCall02GateWordA84 = 79;
        person.ActivePriority = 50;

        Assert.Equal(PrimitiveResult.Stop, GoToRoutingSlotPrimitive.Execute(context, Operand(0, 2)));
        Assert.True(context.World.CancelInteractionAction(person, 79));

        Assert.Equal(PrimitiveResult.Stop, GoToRoutingSlotPrimitive.Execute(context, Operand(0, 2)));
        Assert.True(person.NativeRoute?.EndFollowPending);
        var rootAtEndFollow = (person.NativeRoute!.MotionX, person.NativeRoute.MotionY);
        Assert.Equal(1, person.NativeRoute.AnimatorFollowState);
        Assert.True(person.AnimationPlayback is { RouteDriven: true, Interruptible: true });
        person.AnimatorControlState = 7;
        context.World.AvatarAnimationRootOnly = true;
        context.World.TickAvatarAnimations(person);
        Assert.Equal(3, person.NativeRoute?.AnimatorFollowState);
        Assert.True(person.NativeRoute?.EndFollowPending);
        Assert.Equal(7, person.AnimatorControlState); // Non-pool Tick branch changes +0x180 only.
        Assert.Equal(PrimitiveResult.False, GoToRoutingSlotPrimitive.Execute(context, Operand(0, 2)));

        Assert.False(context.Frame.Waiting);
        Assert.Null(person.NativeRoute);
        Assert.Equal(rootAtEndFollow.MotionX, person.AnimatorRootX);
        Assert.Equal(rootAtEndFollow.MotionY, person.AnimatorRootY);
    }

    [Fact]
    public void DeferredPoolEndFollowUsesTheZeroTransitionControlStateBranch()
    {
        var (context, _) = World();
        var person = (XPerson)context.Me;
        context.World.LotRoomMaps[1] = TwoRoomsSplitAtX();
        context.World.SetObjectPosition(person, new ObjectPosition(24, 24, 1)); // Arry 9 byte 0x00: source pool flag.
        person.AnimatorPoolTile = true;
        person.NativeRoute = new RouteState(2, person.Position, new RoutingSlotRequest(),
            AnimatorFollowState: 1, EndFollowPending: true);
        person.AnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("interruptible", 4, []), false, true)
        {
            RouteDriven = true,
            Interruptible = true,
        };
        person.AnimatorControlState = 7;
        person.AnimatorPoolTransitionState = 0;

        context.World.TickAvatarAnimations(person);

        Assert.Equal(3, person.AnimatorControlState);
        Assert.Equal(3, person.NativeRoute?.AnimatorFollowState);
        Assert.True(person.NativeRoute?.EndFollowPending);
    }

    [Fact]
    public void AnimatorOutOfBoundsSkipsControlDispatchButStillTicksPractices()
    {
        var (context, _) = World();
        var person = (XPerson)context.Me;
        context.World.LotRoomMaps[1] = OpenRoom();
        person.Position = new ObjectPosition(8, 5 * 16 + 8, 1); // tile x = 0: SAnimator::OutOfBounds.
        var clip = new AvatarAnimationClip("oob-practice", 4, []);
        person.AnimationPlayback = new AvatarAnimationPlayback(clip, backwards: false, loop: true)
        {
            Interruptible = true,
        };
        person.NativeRoute = new RouteState(2, person.Position, new RoutingSlotRequest(),
            AnimatorFollowState: 1, EndFollowPending: true, MotionAnimationIndex: 11);
        person.AnimatorControlState = 7;
        person.AnimatorGaitEntryState = 1;
        person.AnimatorPoolTransitionState = 0;
        var frameBefore = person.AnimationPlayback.Frame;
        var millisecondsBefore = person.AnimatorMilliseconds;

        context.World.TickAvatarAnimations(person);

        Assert.Equal(7, person.AnimatorControlState);
        Assert.Equal(1, person.AnimatorGaitEntryState);
        Assert.Equal(0, person.AnimatorPoolTransitionState);
        Assert.Equal(1, person.NativeRoute?.AnimatorFollowState);
        Assert.True(person.NativeRoute?.EndFollowPending);
        Assert.Equal(millisecondsBefore + 42, person.AnimatorMilliseconds);
        Assert.NotEqual(frameBefore, person.AnimationPlayback?.Frame);
    }

    [Theory]
    [InlineData((short)0, (short)19, 81f, 6)]
    [InlineData((short)1, (short)22, 81f, 11)] // +0x1fc = StdAnimDist(0x16) * 16/3, without the stride half.
    public void AnimatorRouteEntryUsesCachedWalkAndRunStepThresholds(short walkStyle, short currentSkill,
        float projectedStep, int expectedControlState)
    {
        var (context, _) = World();
        var person = (XPerson)context.Me;
        var position = person.Position;
        person[ObjectData.WalkStyle] = walkStyle;
        person.AnimatorControlState = 3;
        person.AnimatorRootX = position.X;
        person.AnimatorRootY = position.Y;
        person.AnimatorRootDirection = 2f;
        person.NativeRoute = new RouteState(2, position, new RoutingSlotRequest(),
            MotionX: position.X, MotionY: position.Y,
            MotionTargetX: position.X + (int)projectedStep, MotionTargetY: position.Y,
            MotionAnimationIndex: currentSkill);
        // Without a TileList the lookahead sample is the motion target, so its offset is the gate's f25
        // (SAnimator.s 0x10350B98, 0x10350E78).
        context.World.StandardWalkFallbackAnimationTable[(150, currentSkill)] =
            new AvatarAnimationClip("route-entry-threshold", 2, [], StandardDistance: 15f);
        context.World.StandardWalkFallbackAnimationTable[(150, walkStyle == 1 ? 21 : 20)] =
            new AvatarAnimationClip("route-entry-loop", 2, [], StandardDistance: 15f,
                StandardRootMotionSamples: [System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitX]);

        context.World.TickAvatarAnimations(person);

        Assert.Equal(expectedControlState, person.AnimatorControlState);
    }

    [Theory]
    [InlineData(19, 6, 0)]
    [InlineData(20, 3, 1)]
    public void AnimatorRouteEntryReadsCurrentSkillSeparateFromTheRouteMotionIndex(
        int animatorCurrentSkill, int expectedControlState, int expectedGaitEntryState)
    {
        var (context, _) = World();
        var person = (XPerson)context.Me;
        var position = person.Position;
        person.AnimatorControlState = 3;
        person.AnimatorRouteTransitionState = 1;
        person.AnimatorRootX = position.X;
        person.AnimatorRootY = position.Y;
        person.AnimatorRootDirection = 2f; // Already facing the +X target, so the heading is not clamped.
        person.AnimatorCurrentSkill = animatorCurrentSkill;
        person.NativeRoute = new RouteState(2, position, new RoutingSlotRequest(),
            MotionX: position.X, MotionY: position.Y,
            MotionTargetX: position.X + 100, MotionTargetY: position.Y, // Lookahead offset above the walk threshold.
            MotionAnimationIndex: 20);
        context.World.StandardWalkFallbackAnimationTable[(150, 19)] =
            new AvatarAnimationClip("route-entry-threshold", 2, [], StandardDistance: 15f);

        context.World.TickAvatarAnimations(person);

        Assert.Equal(expectedControlState, person.AnimatorControlState);
        Assert.Equal(expectedGaitEntryState, person.AnimatorGaitEntryState);
        Assert.Equal((short)20, person.NativeRoute?.MotionAnimationIndex);
    }

    [Fact]
    public void AnimatorRouteEntryEntersGaitWhenTheHeadingTurnIsClamped()
    {
        var (context, _) = World();
        var person = (XPerson)context.Me;
        var position = person.Position;
        person.AnimatorControlState = 3;
        person.AnimatorRootX = position.X;
        person.AnimatorRootY = position.Y;
        person.AnimatorRootDirection = 2f;
        person.NativeRoute = new RouteState(2, position, new RoutingSlotRequest(),
            MotionX: position.X, MotionY: position.Y,
            MotionTargetX: position.X, MotionTargetY: position.Y + 100,
            MotionAnimationIndex: 19);
        // The 100-FTile lookahead offset is above the walk threshold; the clamped turn selects gait entry.
        context.World.StandardWalkFallbackAnimationTable[(150, 19)] =
            new AvatarAnimationClip("route-entry-threshold", 2, [], StandardDistance: 15f);
        context.World.StandardWalkFallbackAnimationTable[(150, 20)] =
            new AvatarAnimationClip("route-entry-loop", 2, [], StandardDistance: 15f,
                StandardRootMotionSamples: [System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitX]);

        context.World.TickAvatarAnimations(person);

        Assert.Equal(1, person.AnimatorGaitEntryState);
        Assert.Equal(9, person.AnimatorControlState); // The 100-Ftile remaining route selects state 9 before the gait gate.
        Assert.Equal((short)20, person.NativeRoute?.MotionAnimationIndex);
    }

    [Fact]
    public void AnimatorRouteEntrySelectsRunGaitOnAnEarlierSegmentWithoutADistanceTest()
    {
        var (context, _) = World();
        var person = (XPerson)context.Me;
        var position = person.Position;
        person[ObjectData.WalkStyle] = 1;
        person.AnimatorControlState = 3;
        person.AnimatorRootX = position.X;
        person.AnimatorRootY = position.Y;
        person.AnimatorRootDirection = SimsEngine.Character.AvatarDirection.Toward(1f, 0f);
        // The active point is 4 FTile away: past the 3.1 standing-adjust distance but below the 4.5 running
        // threshold. It is not the last positional point, so Tick stores +0x188=10 without a distance test
        // (SAnimator.s 0x10350A00–0x10350A30).
        person.NativeRoute = new RouteState(2, position, new RoutingSlotRequest(),
            MotionX: position.X, MotionY: position.Y,
            MotionTargetX: position.X + 4, MotionTargetY: position.Y,
            MotionAnimationIndex: 22)
        {
            MotionTargetPathIndex = 1,
            MotionPath = [new(0, 0, position.X, position.Y, position.X, position.Y, false),
                new(0, 0, position.X + 4, position.Y, position.X + 4, position.Y, false),
                new(0, 0, position.X + 40, position.Y, position.X + 40, position.Y, true)],
        };
        context.World.StandardWalkFallbackAnimationTable[(150, 22)] =
            new AvatarAnimationClip("route-entry-run-threshold", 2, [], StandardDistance: 15f);
        context.World.StandardWalkFallbackAnimationTable[(150, 21)] =
            new AvatarAnimationClip("route-entry-run-loop", 2, [], StandardDistance: 15f,
                StandardRootMotionSamples: [System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitX]);

        context.World.TickAvatarAnimations(person);

        Assert.Equal(10, person.AnimatorControlState);
        Assert.Equal((short)21, person.NativeRoute?.MotionAnimationIndex);
    }

    [Fact]
    public void AnimatorRouteEntryUsesTheWalkThresholdInclusiveBoundary()
    {
        var (context, _) = World();
        var person = (XPerson)context.Me;
        var position = person.Position;
        person.AnimatorControlState = 3;
        person.AnimatorRootX = position.X;
        person.AnimatorRootY = position.Y;
        person.AnimatorRootDirection = 2f;
        person.NativeRoute = new RouteState(2, position, new RoutingSlotRequest(),
            MotionX: position.X, MotionY: position.Y,
            MotionTargetX: position.X + 80, MotionTargetY: position.Y, // Offset = StdAnimDist(0x13) × 16/3 for distance 15.
            MotionAnimationIndex: 19);
        context.World.StandardWalkFallbackAnimationTable[(150, 19)] =
            new AvatarAnimationClip("route-entry-threshold", 2, [], StandardDistance: 15f);
        context.World.StandardWalkFallbackAnimationTable[(150, 20)] =
            new AvatarAnimationClip("route-entry-loop", 2, [], StandardDistance: 15f,
                StandardRootMotionSamples: [System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitX]);

        context.World.TickAvatarAnimations(person);

        Assert.Equal(1, person.AnimatorGaitEntryState);
        Assert.Equal(9, person.AnimatorControlState); // +0x188=9 is selected at distance >=3; +0x194 gates skill 20.
        Assert.Equal((short)20, person.NativeRoute?.MotionAnimationIndex);
    }

    [Fact]
    public void DeferredPoolEndFollowStartsStandardSkillThirteenWhenTransitionIsPending()
    {
        var (context, _) = World();
        var person = (XPerson)context.Me;
        context.World.LotRoomMaps[1] = TwoRoomsSplitAtX();
        context.World.SetObjectPosition(person, new ObjectPosition(24, 24, 1)); // Arry 9 byte 0x00: source pool flag.
        person.AnimatorPoolTile = true;
        person.TypeRecord = new TypeRecord(0x7ffe) { FootprintMask = 0 };
        var swimExit = new AvatarAnimationClip("pool-exit", 4, []);
        context.World.StandardWalkFallbackAnimationTable[(158, 0x13)] = swimExit;
        context.World.StandardWalkFallbackAnimationTable[(159, 0x13)] = swimExit;
        context.World.StandardWalkFallbackAnimationTable[(160, 0x13)] = swimExit;
        person.NativeRoute = new RouteState(2, person.Position, new RoutingSlotRequest(),
            AnimatorFollowState: 1, EndFollowPending: true);
        person.AnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("route", 4, []), false, true)
        {
            RouteDriven = true,
            Interruptible = true,
        };
        person.AnimatorPoolTransitionState = 1;
        context.World.AvatarAnimationRootOnly = true;

        context.World.TickAvatarAnimations(person);

        Assert.Equal(7, person.AnimatorControlState);
        Assert.Same(swimExit, person.AnimationPlayback?.Clip);
        Assert.True(person.AnimationPlayback?.Backwards);
        Assert.False(person.AnimationPlayback?.Loop);
        Assert.True(person.AnimationPlayback?.PoolSwimExit);
        Assert.Equal(1, person.NativeRoute?.AnimatorFollowState); // Mac keeps +0x180=1 while state 7 plays skill 0x13.
        Assert.False(person.NativeRoute?.EndFollowPending); // The deferred request was consumed; state 7 owns completion.
        person.AnimationPlayback = AvatarAnimationPlayback.RestoreState(person.AnimationPlayback!.SaveState());
        Assert.True(person.AnimationPlayback?.PoolSwimExit); // The one-shot channel marker survives native save state.

        context.World.AvatarAnimationRootOnly = true;
        context.World.TickAvatarAnimations(person); // State 7's next Tick sees +0x288 cleared and ends the follow.

        Assert.Equal(3, person.AnimatorControlState);
        Assert.Equal(3, person.NativeRoute?.AnimatorFollowState);
        Assert.Equal(swimExit.Name, person.AnimationPlayback?.Clip.Name); // Primary Practice 0x13 outlives the route state.

        context.World.AvatarAnimationRootOnly = false;
        for (var tick = 0; tick < 10 && person.AnimationPlayback is { EndReached: false }; tick++)
            context.World.TickAvatarAnimations(person);
        context.World.TickAvatarAnimations(person); // The completed one-shot is stopped on the following Tick.
        Assert.Null(person.AnimationPlayback);
    }

    [Fact]
    public void DeferredPoolEndFollowLeavesStateSevenEvenWhenStandardSkillThirteenIsUnavailable()
    {
        var (context, _) = World();
        var person = (XPerson)context.Me;
        person.AnimatorRootX = 12.75f;
        person.AnimatorRootY = 13.25f;
        person.AnimatorRootDirection = 7.7f;
        person[ObjectData.Direction] = 1;
        person.AnimatorPoolTile = true;
        person.AnimatorPoolTransitionState = 1;
        person.NativeRoute = new RouteState(2, person.Position, new RoutingSlotRequest(),
            AnimatorFollowState: 1, EndFollowPending: true);
        context.World.StandardWalkFallbackAnimationTable.Clear();
        context.World.AvatarAnimationRootOnly = true;

        context.World.TickAvatarAnimations(person);

        Assert.Equal(7, person.AnimatorControlState);
        Assert.Null(person.AnimationPlayback);
        Assert.Equal(1, person.NativeRoute?.AnimatorFollowState);
        Assert.True(person.NativeRoute?.PoolSwimExitPending);

        context.World.TickAvatarAnimations(person);

        Assert.Equal(3, person.AnimatorControlState);
        Assert.Equal(3, person.NativeRoute?.AnimatorFollowState);
        Assert.False(person.NativeRoute?.PoolSwimExitPending);
        Assert.Equal(12.75f, person.AnimatorRootX);
        Assert.Equal(13.25f, person.AnimatorRootY);
        Assert.Equal(7.7f, person.AnimatorRootDirection);
        Assert.Equal((short)0, person[ObjectData.Direction]); // fctiwz(7.7 + 0.5), then wrap 8 -> 0.
        Assert.Equal(1, person.AnimatorStateTailField198);
    }

    [Fact]
    public void ActiveCancellationDoesNotInterruptANonInterruptibleSim()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        context.World.SetObjectPosition(context.Me, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        context.World.SetObjectPosition(context.StackObject!, new ObjectPosition(10 * 16 + 8, 5 * 16 + 8, 1));
        AllowPersonOverlapWithRouteAnchor(context.StackObject!);
        AddDefinitionBackedReferenceSlot(context.StackObject!);
        context.World.LotRoutingSlots.Add(new RoutingSlotRequest { Flags = 0xff, SnapTargetSlot = 0, MinProximity = 16, MaxProximity = 16, OptimalProximity = 16 });
        var person = (XPerson)context.Me;
        context.World.LocomotionStrideResolver = null;
        person.CurrentActionRecord = new ActionQueueSlot { Sequence = 78, Owner = context.StackObject };
        person.GenericSimsCall02GateWordA84 = 78;
        person[PersonData.NonInterruptible] = 1;

        Assert.Equal(PrimitiveResult.Stop, ExecuteAndTick(context, Operand(0, 2)));
        Assert.True(context.World.CancelInteractionAction(person, 78));
        var positionAtCancellation = person.Position;

        for (var tick = 0; tick < 10; tick++)
            Assert.Equal(PrimitiveResult.Stop, ExecuteAndTick(context, Operand(0, 2)));

        Assert.NotEqual(positionAtCancellation, person.Position);
        Assert.True(context.Frame.Waiting);
        Assert.NotNull(person.NativeRoute);
    }

    [Fact]
    public void PendingActiveCancellationTakesFailureBranchWhenRouteStarts()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        context.World.SetObjectPosition(context.Me, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        context.World.SetObjectPosition(context.StackObject!, new ObjectPosition(10 * 16 + 8, 5 * 16 + 8, 1));
        context.World.LotRoutingSlots.Add(new RoutingSlotRequest { Flags = 0xff, MinProximity = 16, MaxProximity = 16, OptimalProximity = 16 });
        ((XPerson)context.Me).ActiveActionCancelRequested = true;

        Assert.Equal(PrimitiveResult.False, GoToRoutingSlotPrimitive.Execute(context, Operand(0, 2)));

        Assert.Equal(new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1), context.Me.Position);
        Assert.False(context.Frame.Waiting);
        Assert.Null(((XPerson)context.Me).NativeRoute);
    }

    [Fact]
    public void NativeRouteFailsAndClearsItsStateWhenAWallBlocksEveryPath()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = RoomWithVerticalWall(8);
        var start = new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1);
        context.World.SetObjectPosition(context.Me, start);
        context.World.SetObjectPosition(context.StackObject!, new ObjectPosition(10 * 16 + 8, 5 * 16 + 8, 1));
        AddDefinitionBackedReferenceSlot(context.StackObject!);
        context.World.LotRoutingSlots.Add(new RoutingSlotRequest
        {
            Flags = 0xff,
            SnapTargetSlot = 0,
            MinProximity = 16,
            MaxProximity = 16,
            OptimalProximity = 16,
        });

        Assert.Equal(PrimitiveResult.False, RunRoute(context, Operand(0, 2)));

        Assert.Equal(start, context.Me.Position);
        Assert.False(context.Frame.Waiting);
        Assert.Null(((XPerson)context.Me).NativeRoute);
    }

    [Fact]
    public void NativeRouteCanUseADiagonalWaypoint()
    {
        var (context, _) = World();
        context.World.Host = null;
        context.World.LotRoomMaps[1] = OpenRoom();
        AllowPersonOverlapWithRouteAnchor(context.StackObject!);
        var start = new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1);
        var destination = new ObjectPosition(6 * 16 + 8, 6 * 16 + 8, 1);
        context.World.SetObjectPosition(context.Me, start);
        context.World.SetObjectPosition(context.StackObject!, destination);
        AddDefinitionBackedReferenceSlot(context.StackObject!);
        context.World.LotRoutingSlots.Add(new RoutingSlotRequest
        {
            Flags = 0xff,
            SnapTargetSlot = 0,
            MinProximity = 16,
            MaxProximity = 16,
            OptimalProximity = 16,
        });

        var result = RunRoute(context, Operand(0, 2));

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal(destination, context.Me.Position);
    }

    [Fact]
    public void House05DiagonalRouteEdgeRemainsReversibleAlongAnAdjacentWall()
    {
        var world = new SimulationWorld();
        var map = House05Portal51DiagonalApproachWall();
        Assert.Equal(map.RoomAt(26, 25), map.RoomAt(27, 26));

        // House05 Arry2 at (26,25) contains segment 0x01 (-X edge), which yields route mask 0x40 (FUN_00435a8f).
        // The neighboring wall also blocks the orthogonal corner exit, but the diagonal
        // endpoints themselves are open in both directions, as FUN_004550e0 requires.
        Assert.Equal(0x40u, map.RouteBlockingMaskAt(26, 25));
        Assert.True(world.BlocksRouteDirectionAt(map, 26, 26, 6));
        Assert.False(world.BlocksRouteDirectionAt(map, 27, 26, 7));
        Assert.False(world.BlocksRouteDirectionAt(map, 26, 25, 3));

        Assert.True(RouteExecutor.CanStep(world, map, (27, 26), (26, 25)));
        Assert.True(RouteExecutor.CanStep(world, map, (26, 25), (27, 26)));
        Assert.False(RouteExecutor.CanStep(world, map, (26, 25), (25, 25)));
    }

    [Fact]
    public void NonRoomDividingWallStillBlocksItsLocalRouteEdge()
    {
        var world = new SimulationWorld();
        var map = RoomWithNonDividingVerticalWall(8);
        Assert.Equal(1, map.RoomCount);
        Assert.True(world.BlocksRouteDirectionAt(map, 8, 32, 2));
        Assert.False(world.BlocksRouteDirectionAt(map, 8, 32, 6));
    }

    [Fact]
    public void RouteWallMaskIncludesDirectionRotatedFlagsFromLiveTileRoots()
    {
        var world = new SimulationWorld();
        var map = OpenRoom();
        world.LotRoomMaps[1] = map;
        var obstacle = world.Add(new XObject(7, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1),
        });
        var contained = new XObject(8, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(-16, -16, 1),
        };
        contained[ObjectData.ParentId] = obstacle.Id;
        contained[ObjectData.Direction] = 4;
        contained[ObjectData.WallBlockFlags] = 1;
        world.Add(contained);
        obstacle.Slots.Add(contained.ClockManagerIndex);

        Assert.True(world.BlocksRouteDirectionAt(map, 8, 8, 4));
        Assert.False(world.BlocksRouteDirectionAt(map, 8, 8, 0));
    }

    [Fact]
    public void RouteWallMaskIgnoresStaticAndLiveBlockersOnTheLotBorder()
    {
        var world = new SimulationWorld();
        var map = RoomWithNonDividingVerticalWall(0);
        world.LotRoomMaps[1] = map;
        var obstacle = world.Add(new XObject(7, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(8, 8, 1),
        });
        var contained = new XObject(8, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(-16, -16, 1),
        };
        contained[ObjectData.ParentId] = obstacle.Id;
        contained[ObjectData.Direction] = 4;
        contained[ObjectData.WallBlockFlags] = 1;
        world.Add(contained);
        obstacle.Slots.Add(contained.ClockManagerIndex);

        Assert.True(map.BlocksRouteDirectionAt(0, 8, 2));
        Assert.False(world.BlocksRouteDirectionAt(map, 0, 8, 4));
    }

    [Fact]
    public void RouteStartsThePortalFunctionBeforeMovingTowardTheRoomBoundary()
    {
        var (baseContext, _) = World();
        var world = baseContext.World;
        world.Host = null;
        world.RoutingEnabled = true;
        world.LotRoomMaps[1] = TwoRoomsSplitAtXWithApproach();
        world.CaptureExecutionTrace = true;
        var person = (XPerson)baseContext.Me;
        world.SetObjectPosition(person, new ObjectPosition(24, 24, 1)); // source tile 1,1
        var anchor = baseContext.StackObject!;
        world.SetObjectPosition(anchor, new ObjectPosition(56, 24, 1)); // destination tile 3,1
        var map = world.LotRoomMaps[1];
        var sourceRoom = map.RoomAt(1, 1);
        var destinationRoom = map.RoomAt(3, 1);
        Assert.NotEqual(sourceRoom, destinationRoom);
        person.RoomWord84 = unchecked((short)sourceRoom);
        anchor.RoomWord84 = unchecked((short)destinationRoom);
        AddDefinitionBackedReferenceSlot(anchor);
        anchor.RoutingSlots[0] = new RoutingSlotRequest
        {
            Flags = 0xfb, SnapTargetSlot = 0, MinProximity = 0, MaxProximity = 0, OptimalProximity = 0,
        };

        var portalTree = PrimitiveTestWorld.Tree(0,
            (0x25, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse, new byte[8]));
        var portalScope = new TreeTable((4096, portalTree), (4097, portalTree));
        var portal = world.Add(new XObject(3, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = new TypeRecord(3) { BehaviorScope = portalScope, FunctionTable = PortalFunctionTable(4096) },
        });
        portal[ObjectData.Direction] = 2;
        portal.RoomWord84 = unchecked((short)sourceRoom);
        world.SetObjectPosition(portal, new ObjectPosition(40, 24, 1)); // source tile 2,1
        // FUN_0044350d does not register portal-class objects without function 0x0f in Sims.exe's
        // routing manager. Keep a nearer window-like object here to guard the engine/geometry distinction.
        var window = world.Add(new XObject(7, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = new TypeRecord(7) { FunctionTable = new ushort[0x10] },
            Position = person.Position,
            RoomWord84 = unchecked((short)sourceRoom),
        });
        window[ObjectData.Direction] = 2;
        var functionPart = world.Add(new XObject(4, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = new TypeRecord(4)
            {
                BehaviorScope = portalScope,
                FunctionTable = PortalFunctionTable(4097),
            },
        });
        functionPart.RoomWord84 = unchecked((short)destinationRoom);
        world.SetObjectPosition(functionPart, new ObjectPosition(56, 24, 1));
        portal.MultiTileNextId = functionPart.Id;

        var stack = new RouteStack();
        stack.Frames.Add(baseContext.Frame);
        var context = new TreeContext(world, person, baseContext.Frame) { Stack = stack };

        var result = GoToRoutingSlotPrimitive.Execute(context, Operand(0, 1));

        Assert.True(result == PrimitiveResult.Again,
            $"result={result}; route={string.Join(';', world.NativeRouteTrace.Select(entry => $"{entry.Phase}:{entry.Failure}"))}; candidates={string.Join(';', world.RouteCandidateTrace.Select(entry => entry.FailureReason))}");
        Assert.Equal(new ObjectPosition(24, 24, 1), person.Position);
        Assert.Equal((short)4096, stack.Top!.TreeId);
        Assert.Contains(world.NativeRouteTrace, entry => entry.Phase == "portal-function-started");
        Assert.DoesNotContain(world.NativeRouteTrace, entry => entry.Phase == "waypoint");
        Assert.Contains(world.NativeRouteTrace, entry => entry.Phase == "portal-ranking" &&
            entry.Failure?.Contains("selected=3;") == true && !entry.Failure.Contains("7:"));
    }

    [Theory]
    [InlineData((short)0, false)]
    [InlineData((short)1, false)]
    [InlineData((short)0, true)]
    [InlineData((short)1, true)]
    public void PortalManagerEntriesPushTheirInteractionUntilTheTreeReturns(short buildModeType, bool destinationAllowsPerson)
    {
        var (baseContext, _) = World();
        var world = baseContext.World;
        world.Host = null;
        world.CaptureExecutionTrace = true;
        world.RoutingEnabled = true;
        world.LotRoomMaps[1] = TwoRoomsSplitAtX();
        var person = (XPerson)baseContext.Me;
        baseContext.StackObject![ObjectData.RouteId] = 1;
        world.SetObjectPosition(person, new ObjectPosition(24, 24, 1));
        var anchor = world.ObjectById(2)!;
        world.SetObjectPosition(anchor, new ObjectPosition(40, 24, 1));
        // FollowOneStep/Tick use ordinary placement after the portal callback. A solid anchor
        // at the final root rejects that placement; referenced-slot validation does not bypass it.
        if (destinationAllowsPerson) AllowPersonOverlapWithRouteAnchor(anchor);
        var sourceRoom = world.RoomAtTile(1, 1, 1);
        var destinationRoom = world.RoomAtTile(2, 1, 1);
        person.RoomWord84 = sourceRoom;
        anchor.RoomWord84 = destinationRoom;
        AddDefinitionBackedReferenceSlot(anchor);
        anchor.RoutingSlots[0] = new RoutingSlotRequest { Flags = 0xfb, SnapTargetSlot = 0, MinProximity = 0, MaxProximity = 0, OptimalProximity = 0 };

        var doorTree = PrimitiveTestWorld.Tree(0,
            (0x2d, 1, BhavInstruction.ReturnFalse, Operand(0, 1)),
            (0x25, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse, [0, 0, 0, 0, 0, 0, 0, 0]));
        var portalScope = new TreeTable((4096, doorTree));
        var portalDefinition = PrimitiveTestWorld.Definition();
        portalDefinition[(int)ObjdField.BuildModeType] = buildModeType;
        portalDefinition[(int)ObjdField.InteractionGroup] = 7;
        var portal = world.Add(new XObject(3, portalDefinition)
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = new TypeRecord(3)
            {
                Name = "Archway route fixture",
                SubIndex = 3,
                BehaviorScope = portalScope,
                FunctionTable = PortalFunctionTable(4096),
            },
        });
        portal.RequestKey = 39;
        portal[ObjectData.RouteId] = 2;
        portal.RoutingSlots.Add(new RoutingSlotRequest { Flags = 0xff, MinProximity = 0, MaxProximity = 0, OptimalProximity = 0 });
        portal[ObjectData.Direction] = 2;
        // FUN_00463dd9 selects from the routing Sim's current-room portal list; the portal
        // occupies the source-side tile at the room boundary.
        world.SetObjectPosition(portal, new ObjectPosition(24, 24, 1));
        portal.RoomWord84 = sourceRoom;
        var portalPartDefinition = PrimitiveTestWorld.Definition();
        portalPartDefinition[(int)ObjdField.InteractionGroup] = 8;
        var portalPart = world.Add(new XObject(4, portalPartDefinition)
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = new TypeRecord(4) { Name = "Archway route fixture middle part", SubIndex = 3 },
        });
        var functionPartDefinition = PrimitiveTestWorld.Definition();
        functionPartDefinition[(int)ObjdField.InteractionGroup] = 7;
        var functionPart = world.Add(new XObject(5, functionPartDefinition)
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = new TypeRecord(5)
            {
                Name = "Archway route fixture function part",
                SubIndex = 9,
                BehaviorScope = portalScope,
                FunctionTable = Enumerable.Repeat((ushort)0, 0x10).Select((value, index) => index == 0x0f ? (ushort)4096 : value).ToArray(),
            },
        });
        world.SetObjectPosition(functionPart, new ObjectPosition(40, 24, 1));
        if (destinationAllowsPerson) AllowPersonOverlapWithRouteAnchor(functionPart);
        functionPart.RoomWord84 = destinationRoom;
        var portalMasterDefinition = PrimitiveTestWorld.Definition();
        portalMasterDefinition[(int)ObjdField.InteractionGroup] = 7;
        var portalMaster = world.Add(new XObject(6, portalMasterDefinition)
        {
            RuntimeClass = ObjectClass.Portal,
            Position = new ObjectPosition(-16, -16, 1),
            TypeRecord = new TypeRecord(6) { Name = "Archway route linked master", SubIndex = 1 },
        });
        portal.MultiTileNextId = portalPart.Id;
        portal.MultiTileMasterId = portalMaster.Id;
        portalMaster.MultiTileNextId = portal.Id;
        portalPart.MultiTileMasterId = portalMaster.Id;
        portalPart.MultiTileNextId = functionPart.Id;
        functionPart.MultiTileMasterId = portalMaster.Id;
        // This is a runtime-created portal and has no corresponding saved ObjM record.
        world.SavedLotObjects = null;
        world.SavedObjectClasses = new Dictionary<short, (ObjectClass, bool)>();

        var caller = new StackFrame(anchor.StackObjectKey, [], []) { Table = portalScope, Waiting = false };
        var stack = new RouteStack();
        stack.Primitive = (frame, instruction) => TestSimInteractingWithPrimitive.Execute(
            new TreeContext(world, person, frame), instruction.Operand);
        stack.OnPopped = () =>
        {
            PersonInteractionStack.Release(person, stack.Frames.Count);
            // This fixture's callback only checks interaction ownership. Model the successful door
            // crossing before the owner route resumes; a callback that leaves the Sim in this room
            // must instead make the route manager rank another portal.
            if (person.NativeRoute is { PendingPortalId: not 0 })
                world.SetObjectPosition(person, new ObjectPosition(40, 24, 1));
        };
        stack.Frames.Add(caller);
        var context = new TreeContext(world, person, caller) { Stack = stack };

        Assert.Equal(PrimitiveResult.Again, GoToRoutingSlotPrimitive.Execute(context, Operand(0, 1)));
        Assert.Equal((short)3, person[ObjectData.RouteId]);
        Assert.True(portal.PortalRouteCosts.ContainsKey(3));
        Assert.Equal(1001f, portal.PortalRouteCosts[3]);
        Assert.Equal(1001f, functionPart.PortalRouteCosts[3]);
        Assert.Equal(2, stack.Frames.Count);
        Assert.Equal((short)4096, stack.Top!.TreeId);
        Assert.Equal((short)39, stack.Top.StackObject);
        Assert.Same(portal, Assert.Single(person.InteractingObjects));
        Assert.Equal((short)1, portal[ObjectData.UseCount]);
        Assert.Equal(StepResult.Continue, stack.Step());
        Assert.Equal(StepResult.Continue, stack.Step());
        Assert.Equal(StepResult.Continue, stack.Step());
        Assert.Single(stack.Frames);
        Assert.True(stack.LastResult);
        Assert.Empty(person.InteractingObjects);
        Assert.Equal((short)0, portal[ObjectData.UseCount]);

        Assert.Equal(PrimitiveResult.Stop, GoToRoutingSlotPrimitive.Execute(context, Operand(0, 1)));
        Assert.False(person.NativeRoute!.FollowPlacementRejected);
        var completion = PrimitiveResult.Stop;
        for (var tick = 0; tick < 256 && completion == PrimitiveResult.Stop; tick++)
        {
            world.TickAvatarAnimations(person);
            completion = GoToRoutingSlotPrimitive.Execute(context, Operand(0, 1));
        }
        Assert.True(completion == (destinationAllowsPerson ? PrimitiveResult.True : PrimitiveResult.False),
            $"result={completion}; follow={person.NativeRoute?.AnimatorFollowState}; control={person.AnimatorControlState}; " +
            $"targetIndex={person.NativeRoute?.MotionTargetPathIndex}; destination={person.NativeRoute?.MotionTargetIsDestination}; " +
            $"root=({person.AnimatorRootX},{person.AnimatorRootY}); frame={person.NativeRoute?.MotionPracticeFrame}; " +
            System.Text.Json.JsonSerializer.Serialize(world.NativeRouteTrace.TakeLast(2)));
        Assert.Equal(new ObjectPosition(40, 24, 1), person.Position);
        Assert.Null(person.NativeRoute);
    }

    [Fact]
    public void ReturnedPortalCallbackMarksLinkedPartAndSelectsNextCandidateBeforeFailingExhaustion()
    {
        var (baseContext, _) = World();
        var world = baseContext.World;
        world.Host = null;
        world.LotRoomMaps[1] = TwoRoomsSplitAtXWithApproach();
        world.CaptureExecutionTrace = true;
        var person = (XPerson)baseContext.Me;
        var start = new ObjectPosition(24, 24, 1);
        var destination = new ObjectPosition(56, 24, 1);
        world.SetObjectPosition(person, start);
        var map = world.LotRoomMaps[1];
        var sourceRoom = map.RoomAt(1, 1);
        var destinationRoom = map.RoomAt(3, 1);
        person.RoomWord84 = unchecked((short)sourceRoom);
        Assert.NotEqual(sourceRoom, destinationRoom);

        var firstTree = PrimitiveTestWorld.Tree(0,
            (BhavInstruction.ReturnTrue, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse, new byte[8]));
        var secondTree = PrimitiveTestWorld.Tree(0,
            (BhavInstruction.ReturnTrue, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse, new byte[8]));
        var firstScope = new TreeTable((4096, firstTree));
        var secondScope = new TreeTable((4097, secondTree));
        var first = AddPortalWithFunctionPart(world, 3, 4, sourceRoom, destinationRoom, firstScope, 4096,
            new ObjectPosition(40, 24, 1));
        var second = AddPortalWithFunctionPart(world, 5, 6, sourceRoom, destinationRoom, secondScope, 4097,
            new ObjectPosition(40, 40, 1));

        var caller = baseContext.Frame;
        var stack = new RouteStack();
        stack.OnPopped = () => PersonInteractionStack.Release(person, stack.Frames.Count);
        stack.Frames.Add(caller);

        var initial = RouteExecutor.StartDirectPosition(world, person, caller, destination, stack);
        Assert.True(initial == PrimitiveResult.Again,
            $"result={initial}; room={person.RoomWord84}; destinationRoom={destinationRoom}; " +
            string.Join(';', world.NativeRouteTrace.Select(trace => $"{trace.Phase}:{trace.Failure}")));
        Assert.Equal(first.Id, person.NativeRoute!.PendingPortalId);
        Assert.Equal((short)4096, stack.Top!.TreeId);

        while (stack.Frames.Count > 1) Assert.Equal(StepResult.Continue, stack.Step());
        Assert.Equal(start, person.Position); // the callback returned without crossing rooms
        Assert.Equal(PrimitiveResult.Again, RouteExecutor.Continue(world, person, caller, stack));
        Assert.Equal(first.Id, person.NativeRoute!.UsedPortalIds![0]);
        Assert.Contains(first.Id, person.NativeRoute.UsedPortalIds);
        Assert.Contains((short)4, person.NativeRoute.UsedPortalIds); // linked F0-bearing part
        Assert.True(first.PortalRouteCostAt(person[ObjectData.RouteId]) == 1101f,
            $"route={person[ObjectData.RouteId]}; costs={string.Join(',', first.PortalRouteCosts.Select(entry => entry.Key + ":" + entry.Value))}");
        Assert.Equal(1101f, world.ObjectById(4)!.PortalRouteCostAt(person[ObjectData.RouteId]));
        Assert.Equal(second.Id, person.NativeRoute.PendingPortalId);
        Assert.Equal((short)4097, stack.Top!.TreeId);
        Assert.Equal(2, stack.Frames.Count); // first callback was popped, second was started once

        while (stack.Frames.Count > 1) Assert.Equal(StepResult.Continue, stack.Step());
        var completion = RouteExecutor.Continue(world, person, caller, stack);
        for (var tick = 0; tick < 64 && completion == PrimitiveResult.Stop; tick++)
        {
            world.TickAvatarAnimations(person);
            completion = RouteExecutor.Continue(world, person, caller, stack);
        }
        Assert.Equal(PrimitiveResult.False, completion);
        Assert.Null(person.NativeRoute); // exhausted route fails and releases the owner
        Assert.False(caller.Waiting);
        Assert.Empty(person.InteractingObjects);
        Assert.Single(stack.Frames);
        Assert.Equal(2, world.NativeRouteTrace.Count(trace => trace.Phase == "portal-function-started"));
    }

    private static XObject AddPortalWithFunctionPart(SimulationWorld world, short portalId, short functionPartId,
        ushort sourceRoom, ushort destinationRoom, ITreeTable scope, short treeId, ObjectPosition portalPosition)
    {
        var portal = world.Add(new XObject(portalId, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = new TypeRecord(portalId)
            {
                BehaviorScope = scope,
                FunctionTable = PortalFunctionTable((ushort)treeId),
            },
            RoomWord84 = unchecked((short)sourceRoom),
        });
        world.SetObjectPosition(portal, portalPosition);
        var functionPart = world.Add(new XObject(functionPartId, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = new TypeRecord(functionPartId)
            {
                BehaviorScope = scope,
                FunctionTable = PortalFunctionTable((ushort)treeId),
            },
            RoomWord84 = unchecked((short)destinationRoom),
        });
        world.SetObjectPosition(functionPart, new ObjectPosition(72, portalPosition.Y, 1));
        portal.MultiTileNextId = functionPart.Id;
        return portal;
    }

    [Fact]
    public void RouteInvokesTheSelectedPortalFunctionWhileCostLookupFollowsItsLinkedMaster()
    {
        var (baseContext, _) = World();
        var world = baseContext.World;
        world.Host = null;
        world.RoutingEnabled = true;
        world.LotRoomMaps[1] = TwoRoomsSplitAtX();
        var person = (XPerson)baseContext.Me;
        baseContext.StackObject![ObjectData.RouteId] = 1;
        world.SetObjectPosition(person, new ObjectPosition(24, 24, 1));
        var anchor = world.ObjectById(2)!;
        world.SetObjectPosition(anchor, new ObjectPosition(40, 24, 1));
        var sourceRoom = world.RoomAtTile(1, 1, 1);
        var destinationRoom = world.RoomAtTile(2, 1, 1);
        person.RoomWord84 = sourceRoom;
        anchor.RoomWord84 = destinationRoom;
        AddDefinitionBackedReferenceSlot(anchor);
        anchor.RoutingSlots[0] = new RoutingSlotRequest { Flags = 0xfb, SnapTargetSlot = 0, MinProximity = 0, MaxProximity = 0, OptimalProximity = 0 };

        var portalTree = PrimitiveTestWorld.Tree(0,
            (0x2d, 1, BhavInstruction.ReturnFalse, Operand(0, 1)),
            (0x25, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse, [0, 0, 0, 0, 0, 0, 0, 0]));
        var portalScope = new TreeTable((4096, portalTree), (4097, portalTree));
        var master = world.Add(new XObject(3, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            Position = new ObjectPosition(-16, -16, 1),
            TypeRecord = new TypeRecord(3)
            {
                Name = "Archway route master",
                BehaviorScope = portalScope,
                FunctionTable = Enumerable.Repeat((ushort)0, 0x10).Select((value, index) => index == 0x0f ? (ushort)4096 : value).ToArray(),
            },
        });
        world.SetObjectPosition(master, new ObjectPosition(40, 24, 1));
        master.RoomWord84 = destinationRoom;
        var portal = world.Add(new XObject(4, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = new TypeRecord(4)
            {
                Name = "Archway route part",
                BehaviorScope = portalScope,
                FunctionTable = PortalFunctionTable(4097),
            },
        });
        portal.RequestKey = 39;
        portal[ObjectData.RouteId] = 2;
        portal[ObjectData.Direction] = 2;
        world.SetObjectPosition(portal, new ObjectPosition(24, 24, 1));
        portal.RoomWord84 = sourceRoom;
        portal.MultiTileMasterId = master.Id;
        master.MultiTileNextId = portal.Id;

        Assert.Same(master, RouteExecutor.PortalFunction(world, portal)?.FunctionObject);

        var caller = new StackFrame(anchor.StackObjectKey, [], []) { Table = portalScope, Waiting = false };
        var stack = new RouteStack();
        stack.Primitive = (frame, instruction) => TestSimInteractingWithPrimitive.Execute(
            new TreeContext(world, person, frame), instruction.Operand);
        stack.OnPopped = () => PersonInteractionStack.Release(person, stack.Frames.Count);
        stack.Frames.Add(caller);
        var context = new TreeContext(world, person, caller) { Stack = stack };

        Assert.Equal(PrimitiveResult.Again, GoToRoutingSlotPrimitive.Execute(context, Operand(0, 1)));
        Assert.Equal((short)4097, stack.Top!.TreeId);
        Assert.Equal(portal.StackObjectKey, stack.Top.StackObject);
    }

    private static RoomMap OpenRoom(int width = 64, int height = 64)
    {
        var body = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0);
        for (var tile = 0; tile < width * height; tile++) body.Raw(new byte[8]);
        var terrain = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0);
        for (var tile = 0; tile < width * height; tile++) terrain.U8(0x10);
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, body.ToArray()), ("Arry", 9, terrain.ToArray())), "route-test.iff");
        return RoomMap.Build(file, 1, width, height);
    }

    private static RoomMap TwoRoomsSplitAtX()
    {
        var walls = new ChunkBuilder().S16(0).S16(0).S32(4).S32(3).S32(8).U32(0);
        var water = new ChunkBuilder().S16(0).S16(0).S32(4).S32(3).S32(1).U32(0);
        for (var index = 0; index < 12; index++)
        {
            walls.Raw([0, 0x0f, 0, 0, 0, 0, 0, 0]);
            water.U8(index % 4 < 2 ? (byte)0 : (byte)0x10);
        }
        return RoomMap.Build(new IffFile(ChunkBuilder.File(("Arry", 2, walls.ToArray()), ("Arry", 9, water.ToArray())), "portal-route.iff"), 1, 4, 3);
    }

    private static RoomMap TwoRoomsSplitAtXWithApproach()
    {
        var walls = new ChunkBuilder().S16(0).S16(0).S32(6).S32(3).S32(8).U32(0);
        var water = new ChunkBuilder().S16(0).S16(0).S32(6).S32(3).S32(1).U32(0);
        for (var x = 0; x < 6; x++)
        for (var y = 0; y < 3; y++)
        {
            walls.Raw([0, 0x0f, 0, 0, 0, 0, 0, 0]);
            water.U8(x < 3 ? (byte)0 : (byte)0x10);
        }
        return RoomMap.Build(new IffFile(ChunkBuilder.File(("Arry", 2, walls.ToArray()), ("Arry", 9, water.ToArray())),
            "portal-route-approach.iff"), 1, 6, 3);
    }

    private static RoomMap TwoRoomsWithBlockedPortalEdge()
    {
        var walls = new ChunkBuilder().S16(0).S16(0).S32(6).S32(3).S32(8).U32(0);
        var water = new ChunkBuilder().S16(0).S16(0).S32(6).S32(3).S32(1).U32(0);
        for (var x = 0; x < 6; x++)
        for (var y = 0; y < 3; y++)
        {
            var segments = x == 2 && y == 1 ? (byte)0x04 :
                x == 3 && y == 1 ? (byte)0x01 : (byte)0;
            walls.Raw([segments, 0x0f, 0, 0, 0, 0, 0, 0]);
            water.U8(x < 3 ? (byte)0 : (byte)0x10);
        }
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, walls.ToArray()), ("Arry", 9, water.ToArray())),
            "portal-door-route.iff");
        return RoomMap.Build(file, 1, 6, 3);
    }

    private static (IffFile File, IffChunk Chunk) PortalObjectManager(short objectId, short typeIndex)
    {
        var header = new ChunkBuilder().U8(0).U8(0).U8(0).U8(0).S32(0x45).Tag("MjOb").U8(0)
            .S16(objectId).S16(typeIndex).S16(0);
        var end = header.Length + 4 + 3;
        var (file, chunk) = header.U32((uint)(end - PersistReader.HeaderSize)).Raw([0xaa, 0xbb, 0xcc]).U32(0)
            .S16(0).U32(0).EvenString("").InFile("ObjM", 1);
        return (file, chunk);
    }

    private static RoomMap RoomWithVerticalWall(int x)
    {
        var records = new byte[64 * 64 * 8];
        // The barrier faces west from x and east from x-1; set both opposing wall segments in the ARRy records.
        for (var y = 0; y < 64; y++)
        {
            records[(7 * 64 + y) * 8] |= 4;
            records[(x * 64 + y) * 8] = 1;
            records[(x * 64 + y) * 8 + 2] = 1;
        }
        var body = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(8).U32(0).Raw(records);
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, body.ToArray())), "route-wall-test.iff");
        return RoomMap.Build(file, 1, 64, 64);
    }

    private static RoomMap RoomWithNonDividingVerticalWall(int x)
    {
        var records = new byte[64 * 64 * 8];
        for (var y = 0; y < 64; y++)
        {
            // FUN_00435a8f tests wall segments even when style 2 leaves room fill connected.
            // Segment 4 is a tile's east edge and segment 1 the next tile's west edge.
            records[(x * 64 + y) * 8] |= 0x04;
            records[(x * 64 + y) * 8 + 4] = 2;
            records[((x + 1) * 64 + y) * 8] |= 0x01;
            records[((x + 1) * 64 + y) * 8 + 2] = 2;
        }
        var body = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(8).U32(0).Raw(records);
        return RoomMap.Build(new IffFile(ChunkBuilder.File(("Arry", 2, body.ToArray())), "route-open-wall-test.iff"), 1, 64, 64);
    }

    private static RoomMap RoomWithSingleRouteSegment(int x, int y, byte segment)
    {
        var records = new byte[64 * 64 * 8];
        var index = (x * 64 + y) * 8;
        records[index] = segment;
        var categoryOffset = segment switch { 1 => 2, 2 => 3, 4 => 4, 8 => 5, _ => throw new ArgumentOutOfRangeException(nameof(segment)) };
        records[index + categoryOffset] = 2;
        var body = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(8).U32(0).Raw(records);
        return RoomMap.Build(new IffFile(ChunkBuilder.File(("Arry", 2, body.ToArray())), "route-single-wall-test.iff"), 1, 64, 64);
    }

    private static RoomMap House05Portal51DiagonalApproachWall()
    {
        var records = new byte[64 * 64 * 8];
        foreach (var (x, y) in new[] { (26, 25), (26, 26) })
        {
            var index = (x * 64 + y) * 8;
            records[index] = 0x01;
            records[index + 1] = 0x0f;
            records[index + 2] = 3;
            records[index + 3] = 1;
        }
        var body = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(8).U32(0).Raw(records);
        var terrain = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(1).U32(0);
        for (var tile = 0; tile < 64 * 64; tile++) terrain.U8(0x10);
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, body.ToArray()), ("Arry", 9, terrain.ToArray())),
            "house05-portal51-route-wall.iff");
        return RoomMap.Build(file, 1, 64, 64);
    }

    private static RoomMap RoomWithRouteWall()
    {
        const int width = 4;
        const int height = 4;
        var wallBytes = new byte[width * height * 8];
        // The west edge of tile (2, 1) crosses the arrival footprint without lying on the Sim's route.
        wallBytes[(2 * width + 1) * 8] = 0x01;
        for (var index = 0; index < width * height; index++)
        {
            wallBytes[index * 8 + 1] = 0x0f;
        }
        var walls = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0).Raw(wallBytes);
        var floor = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(Enumerable.Repeat((byte)1, width * height).ToArray());
        var water = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(Enumerable.Repeat((byte)0x10, width * height).ToArray());
        return RoomMap.Build(new IffFile(ChunkBuilder.File(
            ("Arry", 1, floor.ToArray()), ("Arry", 2, walls.ToArray()), ("Arry", 9, water.ToArray())), "route-wall-test.iff"), 1, width, height);
    }

    [Fact]
    public void ModeTwoSlotPastTheLotsTableIsErrorThirtySeven()
    {
        var (context, host) = World();
        Assert.Equal(SimulationError.InvalidRoutingSlotSpecified,
            Fails(() => GoToRoutingSlotPrimitive.Execute(context, Operand(2, 2))));
        Assert.Empty(host.Routes);
    }

    [Fact]
    public void ModeZeroReadsTheSlotIndexFromTheParameterAtTheIndex()
    {
        // Parameter 1 is 2, so the slot is the stack object's slot 2.
        var (context, host) = World(parameters: [0, 2]);
        Assert.Equal(PrimitiveResult.True, GoToRoutingSlotPrimitive.Execute(context, Operand(1, 0)));
        Assert.Equal(new[] { "RoutingEnabled", "Route(100,slot)" }, host.Calls);
        Assert.Same(context.StackObject!.RoutingSlots[2], host.Routes[0]);
    }

    [Fact]
    public void ModeZeroParameterNamingNoSlotIsErrorThirtySeven()
    {
        var (context, host) = World(parameters: [7]);
        Assert.Equal(SimulationError.InvalidRoutingSlotSpecified,
            Fails(() => GoToRoutingSlotPrimitive.Execute(context, Operand(0, 0))));
        Assert.Empty(host.Routes);
    }

    [Fact]
    public void ModeZeroIndexPastTheParametersIsErrorEightInThePort()
    {
        var (context, host) = World(parameters: [5]);
        Assert.Equal(SimulationError.StackNumberOutOfRange,
            Fails(() => GoToRoutingSlotPrimitive.Execute(context, Operand(1, 0))));
        Assert.Empty(host.Routes);
    }

    [Fact]
    public void AnyOtherModeIsErrorThirtySeven()
    {
        var (context, host) = World();
        Assert.Equal(SimulationError.InvalidRoutingSlotSpecified,
            Fails(() => GoToRoutingSlotPrimitive.Execute(context, Operand(0, 3))));
        Assert.Empty(host.Routes);
    }

    [Theory]
    [InlineData(0, 0x4001u)]
    [InlineData(1, 0x0001u)]
    public void FlagBitSetsOrClearsTheRouteFlagOfTheSlot(byte flags, uint expected)
    {
        var (context, host) = World();
        context.StackObject!.RoutingSlots[0].Flags = 0x1 | 0x4000;
        GoToRoutingSlotPrimitive.Execute(context, Operand(0, 1, flags));
        Assert.Equal(expected, context.StackObject.RoutingSlots[0].Flags);
    }

    [Theory]
    [InlineData(PrimitiveResult.Yield)]
    [InlineData(PrimitiveResult.Stop)]
    [InlineData(PrimitiveResult.Again)]
    public void TheRouteAnswerPassesThroughUnchanged(PrimitiveResult answer)
    {
        var (context, host) = World();
        host.RouteAnswer = answer;
        Assert.Equal(answer, GoToRoutingSlotPrimitive.Execute(context, Operand(0, 1)));
    }

    [Fact]
    public void StateFiveInterpolatesTurnRootAndHeadingEndpointsAcrossPracticePlayback()
    {
        var (context, _) = World();
        var person = (XPerson)context.Me;
        var world = context.World;
        var samples = Enumerable.Repeat(System.Numerics.Vector3.Zero, 4).ToArray();
        var clip = new AvatarAnimationClip("turn-aligned", samples.Length, [],
            StandardOffset: new System.Numerics.Vector3(2f, 0f, 0f), StandardRootMotionSamples: samples);
        world.StandardWalkFallbackAnimationTable[(150, 15)] = clip;
        person.AnimatorControlState = 3;
        person.AnimatorRootX = 10f;
        person.AnimatorRootY = 10f;
        person.AnimatorRootDirection = 1.85f; // within 0.25f threshold of +X target heading 2.0f
        person[ObjectData.Direction] = 2;
        person.AnimatorWalkingScale = 1f;
        person.NativeRoute = new RouteState(0, default, new RoutingSlotRequest(),
            MotionTargetX: 12, MotionTargetY: 10, MotionX: 10f, MotionY: 10f,
            MotionAnimationIndex: 15, MotionDirection: 2);

        // State 3 enters State 5 via TryBeginRouteTurnRoot
        Assert.True(RouteExecutor.TryBeginRouteTurnRoot(world, person));

        Assert.Equal(5, person.AnimatorControlState);
        Assert.Equal((float?)1.85f, person.NativeRoute!.TurnRootStartDirection);
        Assert.Equal((float?)2.0f, person.NativeRoute.TurnRootPracticeDirection);
        Assert.Equal((float?)2.0f, person.NativeRoute.TurnRootSavedDirection);
        Assert.Equal(1.85f, person.AnimatorRootDirection); // Must NOT snap immediately!

        // Mid-stride: advance to phase 0.5 (frame 2 of 4)
        person.NativeRoute = person.NativeRoute with { MotionPracticeFrame = 2f };
        world.TickAvatarAnimations(person);

        Assert.Equal(5, person.AnimatorControlState);
        Assert.Equal(1.925f, person.AnimatorRootDirection); // 1.85 + (2.0 - 1.85) * 0.5
        Assert.Equal((short)2, person[ObjectData.Direction]);

        // End-of-stride: advance to phase >= 1.0 (frame 4 of 4)
        person.NativeRoute = person.NativeRoute! with { MotionPracticeFrame = 4f };
        world.TickAvatarAnimations(person);

        Assert.Equal(3, person.AnimatorControlState);
        Assert.Equal(2.0f, person.AnimatorRootDirection);
        Assert.Equal((short)2, person[ObjectData.Direction]);
        Assert.Null(person.AnimationPlayback);
        Assert.Null(person.NativeRoute!.TurnRootTargetX);
    }

    [Theory]
    [InlineData(0f, 0f, -1f)]           // -LotY (FUN_00452129 dir 0)
    [InlineData(1f, 0.7071f, -0.7071f)] // +LotX, -LotY (dir 1)
    [InlineData(2f, 1f, 0f)]            // +LotX (dir 2)
    [InlineData(3f, 0.7071f, 0.7071f)]  // +LotX, +LotY (dir 3)
    [InlineData(4f, 0f, 1f)]            // +LotY (dir 4)
    [InlineData(5f, -0.7071f, 0.7071f)] // -LotX, +LotY (dir 5)
    [InlineData(6f, -1f, 0f)]           // -LotX (dir 6)
    [InlineData(7f, -0.7071f, -0.7071f)]// -LotX, -LotY (dir 7)
    public void FacingMatrixAlignsMeshForwardWithLotTravelDirection(float rootDir, float expectedLotDx, float expectedLotDy)
    {
        var facing = AvatarDirection.LotFacingAngle(rootDir);
        var facingMatrix = System.Numerics.Matrix4x4.CreateRotationY(facing);
        var meshForward = new System.Numerics.Vector3(0f, 0f, 1f); // local +Z bind pose forward
        var transformed = System.Numerics.Vector3.Transform(meshForward, facingMatrix);
        // Avatar world coordinates map matrix Z to LotX and matrix X to LotY
        var lotVector = new System.Numerics.Vector3(transformed.Z, transformed.X, transformed.Y);
        Assert.Equal(expectedLotDx, lotVector.X, precision: 4);
        Assert.Equal(expectedLotDy, lotVector.Y, precision: 4);
    }

    [Fact]
    public void MidRouteCancellationCompletesActiveFootstepAndCleansUpExitStateWithoutArrivalFacing()
    {
        var (context, _) = World();
        var world = context.World;
        world.Host = null;
        world.LotRoomMaps[1] = OpenRoom();
        world.SetObjectPosition(context.Me, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        world.SetObjectPosition(context.StackObject!, new ObjectPosition(10 * 16 + 8, 5 * 16 + 8, 1));
        context.StackObject![ObjectData.Direction] = 6;
        AddDefinitionBackedReferenceSlot(context.StackObject!);
        world.LotRoutingSlots.Add(new RoutingSlotRequest { Flags = 0xff, SnapTargetSlot = 0, MinProximity = 16, MaxProximity = 16, OptimalProximity = 16 });
        var person = (XPerson)context.Me;
        person.CurrentActionRecord = new ActionQueueSlot { Sequence = 88, Owner = context.StackObject };
        person.GenericSimsCall02GateWordA84 = 88;
        person.ActivePriority = 50;

        // Start routing
        Assert.Equal(PrimitiveResult.Stop, GoToRoutingSlotPrimitive.Execute(context, Operand(0, 2)));
        Assert.NotNull(person.NativeRoute);

        // Cancel interaction while moving
        Assert.True(world.CancelInteractionAction(person, 88));

        // When follow state is active (1), Continue yields Stop with Waiting=true instead of snapping
        person.NativeRoute = person.NativeRoute! with { AnimatorFollowState = 1, EndFollowPending = false };
        var result = GoToRoutingSlotPrimitive.Execute(context, Operand(0, 2));
        Assert.Equal(PrimitiveResult.Stop, result);
        Assert.True(context.Frame.Waiting);
        Assert.True(person.NativeRoute!.EndFollowPending);

        // Footstep completes in animator (status advances to 3)
        person.NativeRoute = person.NativeRoute with { AnimatorFollowState = 3 };
        var finalResult = GoToRoutingSlotPrimitive.Execute(context, Operand(0, 2));
        Assert.Equal(PrimitiveResult.False, finalResult);
        Assert.False(context.Frame.Waiting);
        Assert.Null(person.NativeRoute);
        // Discrete heading is committed cleanly on exit
        Assert.True(person[ObjectData.Direction] is >= 0 and <= 7);
        // Arrival facing of slot (6) must NOT have overwritten actor's facing on cancelled route
        Assert.NotEqual((short)6, person[ObjectData.Direction]);
    }

    [Fact]
    public void SeatedSimRoutingToCurrentSeatWithSitFunctionCompletesImmediately()
    {
        var (context, _) = World();
        var world = context.World;
        world.Host = null;
        world.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var seat = world.ObjectById(2)!;
        var functions = new ushort[0x1c];
        functions[0x1a] = 4096;
        functions[0x1b] = 4097;
        seat.TypeRecord = new TypeRecord(seat.Id) { FunctionTable = functions };
        var pos = new ObjectPosition(6 * 16 + 8, 6 * 16 + 8, 1);
        world.SetObjectPosition(seat, pos);
        world.SetObjectPosition(person, pos);
        person[PersonData.Sitting] = 1;
        person[ObjectData.ContainerId] = seat.ClockManagerIndex;

        var result = GoToRoutingSlotPrimitive.Execute(context, Operand(0, 1));

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal((short)0, person[PersonData.RouteResult]);
        Assert.Null(person.NativeRoute);
        Assert.False(context.Frame.Waiting);
    }

    [Fact]
    public void SeatedSimStandTreeFailureReturnsFalseWithRouteResultSix()
    {
        var (context, _) = World();
        var world = context.World;
        world.Host = null;
        world.LotRoomMaps[1] = OpenRoom();
        var person = (XPerson)context.Me;
        var start = new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1);
        world.SetObjectPosition(person, start);
        var target = world.ObjectById(2)!;
        AllowPersonOverlapWithRouteAnchor(target);
        world.SetObjectPosition(target, new ObjectPosition(8 * 16 + 8, 5 * 16 + 8, 1));

        var standTree = PrimitiveTestWorld.Tree(0, (0, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse, new byte[8]));
        var seatScope = new TreeTable((4097, standTree));
        var functions = new ushort[0x1c];
        functions[0x1a] = 4096;
        functions[0x1b] = 4097;
        var seat = world.Add(new XObject(3, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(3) { FunctionTable = functions, BehaviorScope = seatScope },
        });
        world.SetObjectPosition(seat, start);
        person[PersonData.Sitting] = 1;
        person[ObjectData.ContainerId] = seat.ClockManagerIndex;

        var stack = new RouteStack();
        stack.Frames.Add(context.Frame);
        var treeContext = new TreeContext(world, person, context.Frame) { Stack = stack };

        // First call pushes the seat's Stand tree (4097) and returns Again
        var first = GoToRoutingSlotPrimitive.Execute(treeContext, Operand(0, 1));
        Assert.Equal(PrimitiveResult.Again, first);
        Assert.Equal(2, stack.Frames.Count);
        Assert.Equal(4097, stack.Frames[^1].TreeId);

        // Stand tree finishes without clearing PersonData.Sitting
        stack.Frames.RemoveAt(stack.Frames.Count - 1);
        var resumed = GoToRoutingSlotPrimitive.Execute(treeContext, Operand(0, 1));
        Assert.Equal(PrimitiveResult.False, resumed);
        Assert.Equal((short)6, person[PersonData.RouteResult]);
        Assert.Null(person.NativeRoute);
        Assert.False(context.Frame.Waiting);
    }
}
