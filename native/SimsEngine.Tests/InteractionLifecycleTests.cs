using System.Buffers.Binary;
using System.Text;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class InteractionLifecycleTests
{
    [Fact]
    public void BusyAcquireStopsAtSameInstructionAndCancelledFifoWaiterDoesNotBlockNextAction()
    {
        var world = new SimulationWorld();
        var rink = world.Add(new XObject(10, PrimitiveTestWorld.Definition()));
        var first = world.Add(new XPerson(11, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        var cancelled = world.Add(new XPerson(12, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        var next = world.Add(new XPerson(13, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        const int firstAction = 101, cancelledAction = 102, nextAction = 103;
        first.CurrentActionRecord = Action(rink, firstAction);
        cancelled.CurrentActionRecord = Action(rink, cancelledAction);
        next.CurrentActionRecord = Action(rink, nextAction);
        var scope = Scope();

        var firstStack = Start(world, first, rink, scope);
        Assert.True(firstStack.Run()); // acquire + attach, then Sleep stops the current tick
        Assert.Equal(2, firstStack.Top!.Instruction);
        Assert.True(world.InteractionLifecycle.OwnsAction(rink, first, firstAction));
        Assert.Contains(InteractionLifecycle.SkateAttachment, first.InteractionAttachments);

        var cancelledStack = Start(world, cancelled, rink, scope);
        Assert.True(cancelledStack.Run()); // busy acquire yields at opcode F0
        Assert.Equal(0, cancelledStack.Top!.Instruction);
        Assert.Equal(1, world.InteractionLifecycle.WaiterCount(rink));

        var nextStack = Start(world, next, rink, scope);
        Assert.True(nextStack.Run());
        Assert.Equal(0, nextStack.Top!.Instruction);
        Assert.Equal(2, world.InteractionLifecycle.WaiterCount(rink));

        Assert.True(world.CancelInteractionAction(cancelled, cancelledAction));
        Assert.True(cancelledStack.Run()); // cancellation branches false and pops; FIFO waiter is pruned
        Assert.Empty(cancelledStack.Frames);
        Assert.Equal(1, world.InteractionLifecycle.WaiterCount(rink));

        world.ApplyStartedInteractionAction(first, ActionQueueSlot.EmptyRecord()); // normal action transition finalizes exact UID
        Assert.False(world.InteractionLifecycle.OwnsAction(rink, first, firstAction));
        Assert.DoesNotContain(InteractionLifecycle.SkateAttachment, first.InteractionAttachments);

        Assert.True(nextStack.Run()); // the next FIFO action acquires at its original instruction
        Assert.Equal(2, nextStack.Top!.Instruction);
        Assert.True(world.InteractionLifecycle.OwnsAction(rink, next, nextAction));
        Assert.Contains(InteractionLifecycle.SkateAttachment, next.InteractionAttachments);

        Assert.True(world.Remove(next.Id)); // actor removal also finalizes its lease and observed accessory
        Assert.Equal(0, world.InteractionLifecycle.LeaseCount(rink));
        Assert.DoesNotContain(InteractionLifecycle.SkateAttachment, next.InteractionAttachments);
    }

    [Fact]
    public void LaneLeasesAreIndependentAndRemovingRinkPrunesEveryOwner()
    {
        var world = new SimulationWorld();
        var rink = world.Add(new XObject(20, PrimitiveTestWorld.Definition()));
        var actor = world.Add(new XPerson(21, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        actor.CurrentActionRecord = Action(rink, 201);
        var second = world.Add(new XPerson(22, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        second.CurrentActionRecord = Action(rink, 202);

        // Exercise the same ownership service with two authored lanes and distinct action identities.
        Assert.Equal(PrimitiveResult.True, Acquire(world, rink, actor, 2, 0));
        Assert.Equal(PrimitiveResult.True, Acquire(world, rink, second, 2, 0));
        Assert.True(world.InteractionLifecycle.OwnsAction(rink, actor, 201));
        Assert.True(world.InteractionLifecycle.OwnsAction(rink, second, 202));
        Assert.True(world.InteractionLifecycle.TryRequestGracefulExit(actor, 201));
        Assert.True(world.InteractionLifecycle.InterruptRequested(actor, 201));
        Assert.False(world.InteractionLifecycle.InterruptRequested(second, 202));

        Assert.True(world.Remove(rink.Id));
        Assert.Equal(0, world.InteractionLifecycle.LeaseCount(rink));
        Assert.False(world.InteractionLifecycle.OwnsAction(rink, actor, 201));
        Assert.False(world.InteractionLifecycle.OwnsAction(rink, second, 202));
        Assert.DoesNotContain(InteractionLifecycle.SkateAttachment, actor.InteractionAttachments);
        Assert.DoesNotContain(InteractionLifecycle.SkateAttachment, second.InteractionAttachments);
    }

    [Fact]
    public void FinalizingReplacedActionCannotReleaseItsReplacementLease()
    {
        var world = new SimulationWorld();
        var rink = world.Add(new XObject(25, PrimitiveTestWorld.Definition()));
        var actor = world.Add(new XPerson(26, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        const int oldAction = 251;
        actor.CurrentActionRecord = Action(rink, oldAction);
        Assert.Equal(PrimitiveResult.True, Acquire(world, rink, actor, 2, 0));
        actor.InteractionAttachments.Add(InteractionLifecycle.SkateAttachment);

        world.ApplyStartedInteractionAction(actor, Action(rink, 252));
        var newAction = actor.CurrentActionRecord!.Sequence;
        Assert.NotEqual(oldAction, newAction);
        Assert.Equal(PrimitiveResult.True, Acquire(world, rink, actor, 2, 0));
        actor.InteractionAttachments.Add(InteractionLifecycle.SkateAttachment);
        Assert.True(world.InteractionLifecycle.OwnsAction(rink, actor, newAction));

        // A late old-action finalizer must be idempotent and action-scoped.
        world.InteractionLifecycle.FinalizeAction(actor, oldAction);
        Assert.True(world.InteractionLifecycle.OwnsAction(rink, actor, newAction));
        Assert.Contains(InteractionLifecycle.SkateAttachment, actor.InteractionAttachments);
        Assert.Equal(1, world.InteractionLifecycle.LeaseCount(rink));
    }

    [Fact]
    public void CancelOnEnteredLifecycleActionRequestsAuthoredExitWithoutHardInterrupt()
    {
        var world = new SimulationWorld();
        var rink = world.Add(new XObject(27, PrimitiveTestWorld.Definition()));
        var actor = world.Add(new XPerson(28, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        actor.CurrentActionRecord = Action(rink, 271);
        Assert.Equal(PrimitiveResult.True, Acquire(world, rink, actor, 2, 0));

        Assert.True(world.CancelInteractionAction(actor, 271));
        Assert.False(actor.ActiveActionCancelRequested);
        Assert.True(world.InteractionLifecycle.InterruptRequested(actor, 271));
        Assert.True(world.InteractionLifecycle.OwnsAction(rink, actor, 271));

        world.ApplyStartedInteractionAction(actor, ActionQueueSlot.EmptyRecord());
        Assert.False(world.InteractionLifecycle.InterruptRequested(actor, 271));
        Assert.Equal(0, world.InteractionLifecycle.LeaseCount(rink));
    }

    [Fact]
    public void UnsupportedCapabilityVersionsAndMalformedOperandsFailExplicitly()
    {
        var world = new SimulationWorld();
        var rink = world.Add(new XObject(30, PrimitiveTestWorld.Definition()));
        var actor = world.Add(new XPerson(31, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        actor.CurrentActionRecord = Action(rink, 301);

        var context = new TreeContext(world, actor,
            new StackFrame(rink.ClockManagerIndex, [], [0]) { Table = Scope(version: 2) });
        var acquire = new BhavInstruction(InteractionLifecycle.AcquireLaneOpcode, 0xFE, 0xFF,
            [1, 0, 0, 0, 0, 0, 0, 0], false);
        var versionError = Assert.Throws<SimulationException>(() => InteractionLifecycle.Execute(context, acquire));
        Assert.Equal(SimulationError.IllegalPrimitiveCode, versionError.Error);

        context = context with { Frame = new StackFrame(rink.ClockManagerIndex, [], [0]) { Table = Scope() } };
        var badOperand = acquire with { Operand = [0, 0, 1, 0, 0, 0, 0, 0] };
        var operandError = Assert.Throws<SimulationException>(() => InteractionLifecycle.Execute(context, badOperand));
        Assert.Equal(SimulationError.IllegalPrimitiveCode, operandError.Error);

        var unknownOpcode = acquire with { Opcode = 0xF4 };
        var operationError = Assert.Throws<SimulationException>(() => InteractionLifecycle.Execute(context, unknownOpcode));
        Assert.Equal(SimulationError.IllegalPrimitiveCode, operationError.Error);

        context = context with { Frame = new StackFrame(rink.ClockManagerIndex, [], [0]) { Table = Scope(mask: 0x80) } };
        var unknownCapabilityBit = Assert.Throws<SimulationException>(() => InteractionLifecycle.Execute(context, acquire));
        Assert.Equal(SimulationError.IllegalPrimitiveCode, unknownCapabilityBit.Error);

        context = context with { Frame = new StackFrame(rink.ClockManagerIndex, [], [0]) { Table = Scope(includeCapability: false) } };
        var missingCapability = Assert.Throws<SimulationException>(() => InteractionLifecycle.Execute(context, acquire));
        Assert.Equal(SimulationError.IllegalPrimitiveCode, missingCapability.Error);
    }

    [Theory]
    [InlineData(18, false)]
    [InlineData(10, true)]
    public void CompiledSkateRunsOneSourceCircuitAndRoutesOut(int age, bool child)
    {
        var iffPath = Environment.GetEnvironmentVariable("SIMS_ICE_RINK_IFF");
        if (string.IsNullOrWhiteSpace(iffPath)) return;
        var file = IffFile.Open(Path.GetFullPath(iffPath!, FindRepositoryRoot()));
        var scope = new BehaviorScope(file, file, null) { ObjectKey = unchecked((int)0xE904FA93), AnimationTableId = 1 };
        var (world, actor, rink) = RouteFixture();
        actor[PersonData.PersonAge] = checked((short)age);
        actor.CurrentActionRecord = Action(rink, 501);
        var tableId = child ? (ushort)130 : (ushort)129;
        world.ObjectAvatarAnimationTable[(unchecked((int)0xE904FA93), tableId, 41)] = new AvatarAnimationClip("skates-on", 30,
            [new AvatarAnimationEvent(566, 101)]);
        world.ObjectAvatarAnimationTable[(unchecked((int)0xE904FA93), tableId, 42)] = new AvatarAnimationClip("glide", 2, []);
        world.ObjectAvatarAnimationTable[(unchecked((int)0xE904FA93), tableId, 43)] = new AvatarAnimationClip("skates-off", 30,
            [new AvatarAnimationEvent(600, 102)]);

        var skateTree = scope.FindNamedTree("Skate");
        Assert.InRange(skateTree, 0x1000, 0x1FFF);
        var stack = TreeRunner.StartObjectTree(world, actor, scope, skateTree, rink.ClockManagerIndex, 0, [])!;
        // An interaction push records the rink in the person's +0xb00 list (FUN_0045e707); Animate Sim's
        // Object-scope lookup resolves the rink's skate clips through that entry (FUN_005893b7/FUN_005894e6).
        PersonInteractionStack.Register(actor, rink, stack.Frames.Count, flag: false);
        var sawSkateAttachmentState = false;
        var attachmentStateAtMs = -1;
        var skatesOnStartedAtMs = -1;
        var skatesOffStartedAtMs = -1;
        var skatesOffRemovedAtMs = -1;
        var exitRequested = false;
        var completeCircuitAtExitRequest = false;
        var routeSelectionsAtTrack = 0;
        var elapsedMs = 0;
        var waypointSamples = new List<string>();
        for (var tick = 0; tick < 180 && stack.Frames.Count > 0; tick++)
        {
            if (actor.AnimationPlayback?.Clip.Name == "skates-on" && skatesOnStartedAtMs < 0)
                skatesOnStartedAtMs = elapsedMs;
            if (!exitRequested && stack.Top is { TreeId: var tree, Instruction: 17 } && tree == skateTree)
            {
                // TreeRunner can execute through the following check before yielding, so
                // request the ported source interrupt as the third glide begins. The BHAV
                // consumes it at the post-glide boundary, after completing the circuit.
                routeSelectionsAtTrack = world.NativeRouteTrace.Count(entry => entry.Phase == "destination-selected");
                completeCircuitAtExitRequest = routeSelectionsAtTrack >= 4; // entry + source track successors 7/9/7 (or 8/10/8)
                Assert.True(world.RequestInteractionExit(actor, 501));
                exitRequested = true;
            }
            if (actor.AnimationPlayback?.Clip.Name == "skates-off" && skatesOffStartedAtMs < 0)
                skatesOffStartedAtMs = elapsedMs;
            world.ClockManager.SetWaitTicksAt(actor.ClockManagerIndex, -1);
            var routeBefore = actor.NativeRoute;
            var positionBefore = actor.Position;
            var frameBefore = stack.Top;
            var traceCountBefore = world.NativeRouteTrace.Count;
            stack.Run();
            // The world tick runs SAnimator::Tick after the object's stack; route root motion and the
            // practice clip both advance there (SAnimator.s 0x10350F4C–0x1035109C).
            world.TickAvatarAnimations(actor);
            if (routeBefore is not null && actor.Position != positionBefore)
            {
                SimulationNativeRouteTrace? lastTrace = world.NativeRouteTrace.Count > traceCountBefore
                    ? world.NativeRouteTrace[^1] : null;
                waypointSamples.Add($"BHAV={frameBefore?.TreeId}:{frameBefore?.Instruction}; lane={frameBefore?.Locals.ElementAtOrDefault(1)}; target={routeBefore.Destination}; before={positionBefore}; after={actor.Position}; trace={lastTrace?.Phase}");
                if (waypointSamples.Count > 30) waypointSamples.RemoveAt(0);
            }
            elapsedMs += 40;
            if (actor.InteractionAttachments.Contains(InteractionLifecycle.SkateAttachment))
            {
                sawSkateAttachmentState = true;
                if (attachmentStateAtMs < 0) attachmentStateAtMs = elapsedMs;
            }
            else if (skatesOffStartedAtMs >= 0 && skatesOffRemovedAtMs < 0)
            {
                skatesOffRemovedAtMs = elapsedMs;
            }
            routeSelectionsAtTrack = Math.Max(routeSelectionsAtTrack,
                world.NativeRouteTrace.Count(entry => entry.Phase == "destination-selected"));
        }

        Assert.True(sawSkateAttachmentState, "XEVT 101 did not set action-owned skate attachment state on the compiled adult/child Skate action.");
        Assert.InRange(attachmentStateAtMs - skatesOnStartedAtMs, 520, 760); // Source event is 566ms into the clip.
        Assert.True(exitRequested, "The test never issued the action-scoped graceful exit request.");
        Assert.True(completeCircuitAtExitRequest,
            $"Exit was requested before the source 7→9→7 (or 8→10→8) circuit completed; route selections={routeSelectionsAtTrack}.");
        Assert.True(skatesOffStartedAtMs >= 0, "The graceful exit did not start the source skatesOff animation.");
        Assert.InRange(skatesOffRemovedAtMs - skatesOffStartedAtMs, 560, 760); // Source event 102 is 600ms into the clip.
        Assert.True(routeSelectionsAtTrack >= 4,
            $"The compiled action made only {routeSelectionsAtTrack} routes; expected entry plus all three source track legs.");
        Assert.True(stack.Frames.Count == 0,
            $"Skate did not finish: tree={stack.Top?.TreeId}, instruction={stack.Top?.Instruction}, lane={stack.Top?.Locals.ElementAtOrDefault(1)}, position={actor.Position}, route={actor.NativeRoute}, lastWaypoints={string.Join(';', waypointSamples.TakeLast(3))}, lastPlans={string.Join(';', world.NativeRouteTrace.Where(item => item.Phase is "path-plan" or "waypoint-placement").TakeLast(9).Select(item => $"{item.Phase}:{item.Failure}"))}, lastSearch={world.RouteSearchTrace.LastOrDefault()}");
        Assert.Equal(0, world.InteractionLifecycle.LeaseCount(rink));
        Assert.DoesNotContain(InteractionLifecycle.SkateAttachment, actor.InteractionAttachments);
        Assert.Contains(world.NativeRouteTrace, entry => entry.Phase == "destination-selected" &&
            (entry.Destination.X >> 4) is >= 10 and <= 20);
    }

    [Fact]
    public void CompiledSkateRouteFailureAndForcedMidRouteCancelFinalizeTheSameActionLease()
    {
        var iffPath = Environment.GetEnvironmentVariable("SIMS_ICE_RINK_IFF");
        if (string.IsNullOrWhiteSpace(iffPath)) return;
        var file = IffFile.Open(Path.GetFullPath(iffPath!, FindRepositoryRoot()));
        var scope = new BehaviorScope(file, file, null) { ObjectKey = unchecked((int)0xE904FA93) };
        var skateTree = scope.FindNamedTree("Skate");

        var (failedWorld, failedActor, failedRink) = RouteFixture();
        failedActor.CurrentActionRecord = Action(failedRink, 601);
        failedWorld.RoutingEnabled = false;
        var failedStack = TreeRunner.StartObjectTree(failedWorld, failedActor, scope, skateTree,
            failedRink.ClockManagerIndex, 0, [])!;
        failedStack.Run();
        Assert.Empty(failedStack.Frames);
        Assert.Equal(0, failedWorld.InteractionLifecycle.LeaseCount(failedRink));
        Assert.DoesNotContain(InteractionLifecycle.SkateAttachment, failedActor.InteractionAttachments);

        var (cancelWorld, cancelActor, cancelRink) = RouteFixture();
        cancelActor.CurrentActionRecord = Action(cancelRink, 602);
        var cancelStack = TreeRunner.StartObjectTree(cancelWorld, cancelActor, scope, skateTree,
            cancelRink.ClockManagerIndex, 0, [])!;
        Assert.True(cancelStack.Run());
        Assert.NotNull(cancelActor.NativeRoute);
        // Exercise the forced interrupt path independently from Cancel Interaction, which
        // requests the authored track-boundary exit after a lane has been acquired.
        cancelActor.ActiveActionCancelRequested = true;
        // EndFollow defers while +0x180 is 1; SAnimator::Tick advances it to 3 and the next
        // callback takes the interrupted exit.
        for (var tick = 0; tick < 8 && cancelStack.Frames.Count > 0; tick++)
        {
            cancelStack.Run();
            cancelWorld.TickAvatarAnimations(cancelActor);
        }
        Assert.Empty(cancelStack.Frames);
        Assert.Null(cancelActor.NativeRoute);
        Assert.Equal(0, cancelWorld.InteractionLifecycle.LeaseCount(cancelRink));
        Assert.DoesNotContain(InteractionLifecycle.SkateAttachment, cancelActor.InteractionAttachments);
    }

    private static ActionQueueSlot Action(XObject rink, int sequence) => new() { Owner = rink, Sequence = sequence };

    private static (SimulationWorld World, XPerson Actor, XObject Rink) RouteFixture()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        var world = context.World;
        world.Host = null;
        world.RoutingEnabled = true;
        world.CaptureExecutionTrace = true;
        GoToRoutingSlotPrimitiveTests.AddWalkClips(world);
        // Quick walk clips so the 180-tick budget covers the whole circuit; the route executor reads only the root motion.
        var quick = Enumerable.Range(0, 31).Select(i => new System.Numerics.Vector3(i * 2f, 0f, 0f)).ToArray();
        for (var index = 11; index <= 20; index++)
            world.StandardWalkFallbackAnimationTable[(150, index)] = new AvatarAnimationClip($"walk{index}", quick.Length, [],
                null, 60f, new System.Numerics.Vector3(60f, 0f, 0f), quick, 1000f,
                // Tick's terminal fade reads StartVitaBoy's walk-loop (0x14) marker strides; without them the
                // final segment never arrives (SAnimator.s 0x103507B0–0x103509F0).
                LocomotionMarkerOne: index == 20 ? new AvatarAnimationMarker(0.25f, "lfoot") : null,
                LocomotionMarkerTwo: index == 20 ? new AvatarAnimationMarker(0.75f, "rfoot") : null);
        world.LotRoomMaps[1] = OpenRoom();
        var actor = Assert.IsType<XPerson>(context.Me);
        actor.TypeRecord = new TypeRecord(actor.Id);
        actor[ObjectData.AllowedHeightFlags] = 1;
        actor[ObjectData.PlacementFlags] = 0x0801;
        world.PlaceObject(actor, new ObjectPosition(8 * 16 + 8, 10 * 16 + 8, 1));
        var rink = world.Add(new XObject(2, PrimitiveTestWorld.Definition(0))
        {
            RuntimeClass = ObjectClass.MultiTileObject,
            TypeRecord = new TypeRecord(unchecked((int)0xE904FA93)),
        });
        foreach (var _ in Enumerable.Range(0, 30)) rink.RoutingSlots.Add(new RoutingSlotRequest());
        var routes = new Dictionary<int, (int X, int Y)>
        {
            [7] = (128, 64), [8] = (32, 64), [9] = (80, 104), [10] = (80, 24),
            [13] = (80, 88), [14] = (80, 40), [21] = (80, 136), [22] = (80, -8),
            [23] = (80, 120), [24] = (80, 8), [25] = (80, 8), [26] = (80, 120),
        };
        foreach (var (index, (x, y)) in routes)
            rink.RoutingSlots[index] = new RoutingSlotRequest { X = x, Y = y, Flags = 0 };
        var parts = new List<XObject> { rink };
        short nextId = 200;
        for (var y = 0; y < 9; y++)
        for (var x = 0; x < 11; x++)
        {
            if (x == 0 && y == 0) continue;
            var partId = nextId++;
            var part = world.Add(new XObject(partId, PrimitiveTestWorld.Definition(0))
            {
                RuntimeClass = ObjectClass.MultiTileObject,
                TypeRecord = new TypeRecord(partId),
                MultiTileMasterId = rink.Id,
                SavedMultiTileState = new MultiTileState(x, y, 0, 0, 0, 0, rink.Id, null),
            });
            part[ObjectData.Flags] = 4;
            parts.Add(part);
        }
        rink.MultiTileNextId = parts[1].Id;
        for (var index = 1; index < parts.Count; index++)
            parts[index].MultiTileNextId = index + 1 < parts.Count ? parts[index + 1].Id : (short)0;
        rink[ObjectData.Flags] = 4;
        world.PlaceObject(rink, new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1));
        return (world, actor, rink);
    }

    private static SimsEngine.World.RoomMap OpenRoom()
    {
        var body = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(8).U32(0);
        for (var tile = 0; tile < 64 * 64; tile++) body.Raw(new byte[8]);
        var terrain = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(1).U32(0);
        for (var tile = 0; tile < 64 * 64; tile++) terrain.U8(0x10);
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, body.ToArray()), ("Arry", 9, terrain.ToArray())),
            "ice-rink-generated-skate.iff");
        return SimsEngine.World.RoomMap.Build(file, 1, 64, 64);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not find the repository root from the test output directory.");
    }

    private static TreeStack Start(SimulationWorld world, XPerson actor, XObject rink, BehaviorScope scope)
    {
        var stack = TreeRunner.StartObjectTree(world, actor, scope, 0x1000, rink.ClockManagerIndex, 0, [1])!;
        world.ClockManager.SetWaitTicksAt(actor.ClockManagerIndex, 1);
        return stack;
    }

    private static PrimitiveResult Acquire(SimulationWorld world, XObject rink, XPerson actor, byte lanes, byte local)
    {
        var scope = Scope();
        var frame = new StackFrame(rink.ClockManagerIndex, [], [0]) { Table = scope };
        actor.CurrentActionRecord ??= Action(rink, 900 + actor.Id);
        var context = new TreeContext(world, actor, frame);
        return InteractionLifecycle.Execute(context,
            new BhavInstruction(InteractionLifecycle.AcquireLaneOpcode, 0xFE, 0xFF,
                [lanes, local, 0, 0, 0, 0, 0, 0], false));
    }

    private static BehaviorScope Scope(byte version = 1, byte mask = 15, bool includeCapability = true)
    {
        var bhav = new byte[13 + 4 * 12];
        BinaryPrimitives.WriteUInt16LittleEndian(bhav, 0x8003);
        bhav[2] = 0; // type
        bhav[3] = 1; // Sleep reads parameter 0; retain the test's supplied wait value.
        bhav[4] = 2; // locals: XEVT value and chosen lane
        BinaryPrimitives.WriteUInt32LittleEndian(bhav.AsSpan(9), 4);
        Instruction(bhav.AsSpan(13, 12), InteractionLifecycle.AcquireLaneOpcode, 1, 0xFF,
            [1, 1, 0, 0, 0, 0, 0, 0]);
        Instruction(bhav.AsSpan(25, 12), InteractionLifecycle.SetAttachmentOpcode, 2, 2,
            [1, 1, 0, 0, 0, 0, 0, 0]);
        Instruction(bhav.AsSpan(37, 12), 0, 3, 3, [0, 0, 0, 0, 0, 0, 0, 0]); // Sleep parameter 0
        Instruction(bhav.AsSpan(49, 12), InteractionLifecycle.ReleaseLaneOpcode, 0xFE, 0xFE, new byte[8]);
        var chunks = includeCapability
            ? new (string Type, ushort Id, byte[] Data)[]
            {
                ("NLCF", 0, new byte[] { 0x53, 0x31, 0x4c, 0x43, version, mask, 0, 0 }),
                ("BHAV", 0x1000, bhav),
            }
            : [("BHAV", (ushort)0x1000, bhav)];
        var bytes = Iff(chunks);
        var file = new IffFile(bytes, "test:rink");
        return new BehaviorScope(file, file, null);
    }

    private static void Instruction(Span<byte> target, ushort opcode, byte yes, byte no, byte[] operand)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target, opcode);
        target[2] = yes;
        target[3] = no;
        operand.CopyTo(target[4..]);
    }

    private static byte[] Iff((string Type, ushort Id, byte[] Data)[] chunks)
    {
        var output = new byte[64 + chunks.Sum(chunk => 76 + chunk.Data.Length)];
        Encoding.ASCII.GetBytes("IFF FILE 2.0").CopyTo(output, 0);
        var cursor = 64;
        foreach (var (type, id, data) in chunks)
        {
            var header = output.AsSpan(cursor, 76);
            BinaryPrimitives.WriteUInt32BigEndian(header, ResourceType.Parse(type).Code);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], checked((uint)(76 + data.Length)));
            BinaryPrimitives.WriteUInt16BigEndian(header[8..], id);
            BinaryPrimitives.WriteUInt16BigEndian(header[10..], 0x10);
            Encoding.ASCII.GetBytes("test").CopyTo(header[12..]);
            data.CopyTo(output, cursor + 76);
            cursor += 76 + data.Length;
        }
        return output;
    }
}
