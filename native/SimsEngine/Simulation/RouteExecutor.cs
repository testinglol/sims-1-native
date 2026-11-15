namespace SimsEngine.Simulation;

/// <summary>Runtime state retained while Sims.exe's asynchronous route callback continues.</summary>
public sealed record RouteState(short AnchorObjectId, ObjectPosition Destination, RoutingSlotRequest Slot,
    short PendingPortalId = 0, int PortalCallerDepth = -1, StackFrame? OwnerFrame = null, short OpenedPortalId = 0,
    short ArrivalFacing = 0, short PendingPortalFunctionObjectId = 0, short OpenedPortalFunctionObjectId = 0,
    short LastUsedPortalId = 0, short LastUsedPortalFunctionObjectId = 0, bool DirectPositionRequest = false,
    short[]? UsedPortalIds = null, int DiagnosticSnapshotCount = 0, int WaypointCooldownTicks = 0,
    int WaypointTileX = -1, int WaypointTileY = -1, int WaypointX = int.MinValue, int WaypointY = int.MinValue,
    int WaypointCellCenterX = int.MinValue, int WaypointCellCenterY = int.MinValue,
    int MotionTargetTileX = -1, int MotionTargetTileY = -1, int MotionTargetX = int.MinValue,
    int MotionTargetY = int.MinValue, bool MotionTargetIsDestination = false,
    float MotionFrame = 0f, float MotionPracticeFrame = 0f, float MotionX = float.NaN, float MotionY = float.NaN,
    float MotionPlaybackScale = 1f,
    short MotionAnimationIndex = -1, short MotionDirection = -1, bool StandChecked = false, int PendingStandDepth = -1,
    int AnimatorFollowState = 1, bool EndFollowPending = false, bool FollowPlacementRejected = false,
    int LocomotionFadeEndMils = 0, int PendingFailureFeedbackDepth = -1, short FailureReason = 0)
{
    /// <summary>The polyline the pending motion target came from. Mac SAnimator::FollowOneStep keeps projecting onto the
    /// TileList it was started with until the step ends; a fresh plan from the Sim's mid-segment position can route
    /// elsewhere and leave the Sim projected onto a different line than the one its target belongs to.</summary>
    internal IReadOnlyList<RouteExecutor.RouteWaypoint>? MotionPath { get; init; }
    /// <summary>MotionPath point currently targeted by the route cursor. Mac BeginFollow sets +0x178=0,
    /// and SAnimator::Tick uses that cursor for segment [cursor,cursor+1]. This index is cursor+1.</summary>
    internal int MotionTargetPathIndex { get; init; } = -1;
    internal IReadOnlyList<RouteExecutor.RouteWaypoint>? FollowPath { get; init; }
    internal int FollowWaypointIndex { get; init; }
    internal bool LookTowardsFollow { get; init; }
    /// <summary>Continuous [0, 8) target heading computed from here toward LookTowards's target point.</summary>
    internal float? LookTowardsHeading { get; init; }
    /// <summary>BeginFollow initializes the animator target to its current root and heading until its first Tick.</summary>
    internal bool BeginFollowPendingTick { get; init; }
    internal bool AwaitingBeginFollow { get; init; }
    internal bool EndFollowOnArrival { get; init; }
    /// <summary>State 7 runs for a deferred pool exit even when GetStdSkill(0x13) returns no practice.</summary>
    internal bool PoolSwimExitPending { get; init; }
    /// <summary>Mac's deferred pool EndFollow copies animator root +0x80/+0x84/+0x88 to +0x1d4/+0x1d8/+0x1dc;
    /// state 7 restores those endpoints when its primary-practice phase is already complete.</summary>
    internal float? PoolSwimExitRootX { get; init; }
    internal float? PoolSwimExitRootY { get; init; }
    internal float? PoolSwimExitDirection { get; init; }
    /// <summary>Mac SAnimator states 6/11 retain the standing-start root lerp endpoints at +0x1c8/+0x1cc
    /// and +0x1bc/+0x1c0 while the primary Practice phase advances.</summary>
    internal float? StartRootOriginX { get; init; }
    internal float? StartRootOriginY { get; init; }
    internal float? StartRootTargetX { get; init; }
    internal float? StartRootTargetY { get; init; }
    /// <summary>Mac SAnimator state 5 retains current root and the route target adjusted by StdAnimOffset.</summary>
    internal float? TurnRootOriginX { get; init; }
    internal float? TurnRootOriginY { get; init; }
    internal float? TurnRootTargetX { get; init; }
    internal float? TurnRootTargetY { get; init; }
    internal float? TurnRootSavedDirection { get; init; }
    internal float? TurnRootPracticeScale { get; init; }
    internal float? TurnRootStartDirection { get; init; }
    internal float? TurnRootPracticeDirection { get; init; }
    /// <summary>Mac SAnimator::Tick's non-zero route-preference arrival writes the current root to the state-3
    /// transition target +0x1bc/+0x1c0 instead of committing the terminal point (SAnimator.s 0x10350750–0x10350774).</summary>
    internal float? TransitionTargetX { get; init; }
    internal float? TransitionTargetY { get; init; }
    /// <summary>The selected goal candidate's +0xe entry flags (one-hot slot-relative sector), copied to
    /// PersonData.RouteEntryFlags when the route arrives (FUN_0045c4fa) or before a seated stand (FUN_0045aecf).</summary>
    internal short EntryFlags { get; init; }
    /// <summary>Selected chair goal's object id (+0xc in FUN_00483572 / FUN_0045c4fa), or 0 for standing goals.</summary>
    internal short ChairObjectId { get; init; }
    /// <summary>Caller stack depth while waiting in route state 4 for a pushed chair Sit (OBJf 0x1a) tree.</summary>
    internal int PendingSitDepth { get; init; } = -1;
}

/// <summary>A native route slice. Sims.exe FUN_0045aecf owns the asynchronous route lifecycle;
/// Opcode 0x2d enters FUN_0045ad3a and its endpoint planner is FUN_00483572. The partition search permits room changes at saved
/// door portal positions. Sims.exe FUN_0045c92e pushes portal functions; its return value is the push result, not the
/// eventual BHAV result. Route-slot endpoint sampling follows FUN_00483572; movement curves remain inferred.</summary>
internal static class RouteExecutor
{
    /// <summary>An accepted route goal. <paramref name="EntryFlags"/> is the candidate record's +0xe word: FUN_00483572
    /// stores <c>1 &lt;&lt; localSector</c> (00483572.c:236-237, 294-295) or chair-relative <c>1 &lt;&lt; chairSide</c>
    /// (00483572.c:349-350), and FUN_0045c4fa copies it into the person's +0x5d6 route-entry flags (0045c4fa.c:40).
    /// <paramref name="ChairObjectId"/> is candidate +0xc (chair object id when routing to a sitting goal).</summary>
    internal readonly record struct SelectedRouteGoal(ObjectPosition Position, short SearchDirection,
        short EntryFlags = 0, short ChairObjectId = 0);
    private readonly record struct RouteGoalCandidate(ObjectPosition Position, double Cost, short SearchDirection,
        short EntryFlags = 0, short ChairObjectId = 0);
    internal readonly record struct RouteWaypoint(int TileX, int TileY, int X, int Y,
        int CellCenterX, int CellCenterY, bool IsDestination);

    /// <summary>Mac TryLookTowards replaces the remaining person TileList points and calls BeginFollow.
    /// The first FollowOneStep result is busy (SAnimator +0x180 is 1), so movement starts on the next callback.</summary>
    internal static PrimitiveResult BeginLookTowardsFollow(SimulationWorld world, XPerson person, StackFrame frame,
        IReadOnlyList<ObjectPosition> points)
    {
        if (points.Count < 2) throw new ArgumentException("A follow path needs at least two points.", nameof(points));
        person.AnimatorControlState = 3; // SAnimator::BeginFollow writes +0x188 = 3.
        person.AnimatorRouteTransitionState = 0; // SAnimator::BeginFollow clears +0x280.
        person.AnimationPlayback = null;
        person.AnimatorCurrentSkill = -1;
        person.AnimatorRootX = person.Position.X;
        person.AnimatorRootY = person.Position.Y;
        person.AnimatorRootDirection = person[ObjectData.Direction];
        person.SetAnimatorDisplacements(0f, 0f, 0f); // Mac BeginFollow clears +0x8c/+0x90/+0x94.
        var path = points.Select((point, index) => new RouteWaypoint(
            point.X >> 4, point.Y >> 4, point.X, point.Y, point.X, point.Y, index == points.Count - 1)).ToArray();
        var here = points[0];
        var lookPoint = points[^1];
        var lookDx = lookPoint.X - here.X;
        var lookDy = lookPoint.Y - here.Y;
        var lookHeading = lookDx == 0 && lookDy == 0
            ? (float)(person[ObjectData.Direction] & 7)
            : SimsEngine.Character.AvatarDirection.Toward(lookDx, lookDy);
        // Mac TryLookTowards pushes [me, me, lookPoint]: points[0..1] match me.Position, so SAnimator's
        // terminal look-at heading is towards points[^1] while staying on points[0] with AnimatorFollowState = -1.
        person.NativeRoute = new RouteState(person.Id, here, new RoutingSlotRequest(), OwnerFrame: frame,
            DirectPositionRequest: true, ArrivalFacing: SimsEngine.Character.AvatarDirection.ToObjectDirection(lookHeading),
            MotionTargetTileX: here.X >> 4, MotionTargetTileY: here.Y >> 4, MotionTargetX: here.X,
            MotionTargetY: here.Y, MotionTargetIsDestination: true,
            MotionX: here.X, MotionY: here.Y,
            MotionPlaybackScale: person.AnimatorWalkingScale,
            MotionAnimationIndex: -1,
            MotionDirection: (short)(person[ObjectData.Direction] & 7), StandChecked: true, AnimatorFollowState: -1)
        {
            MotionPath = path,
            MotionTargetPathIndex = 1,
            FollowPath = path,
            FollowWaypointIndex = 1,
            LookTowardsFollow = true,
            LookTowardsHeading = lookHeading,
            BeginFollowPendingTick = false,
        };
        frame.Waiting = true;
        return PrimitiveResult.Stop;
    }

    /// <summary>Seeds an active walking follow (AnimatorFollowState = 1) directly along <paramref name="points"/>
    /// for SAnimator polyline-locomotion unit tests without entering a slot route planner.</summary>
    internal static PrimitiveResult BeginDirectWalkFollow(SimulationWorld world, XPerson person, StackFrame frame,
        IReadOnlyList<ObjectPosition> points)
    {
        if (points.Count < 2) throw new ArgumentException("A follow path needs at least two points.", nameof(points));
        person.AnimatorControlState = 3;
        person.AnimatorRouteTransitionState = 0;
        person.AnimatorRootX = person.Position.X;
        person.AnimatorRootY = person.Position.Y;
        person.AnimatorRootDirection = person[ObjectData.Direction];
        person.SetAnimatorDisplacements(0f, 0f, 0f);
        var path = points.Select((point, index) => new RouteWaypoint(
            point.X >> 4, point.Y >> 4, point.X, point.Y, point.X, point.Y, index == points.Count - 1)).ToArray();
        var target = path[1];
        person.NativeRoute = new RouteState(person.Id, points[^1], new RoutingSlotRequest(), OwnerFrame: frame,
            DirectPositionRequest: true, ArrivalFacing: person[ObjectData.Direction],
            MotionTargetTileX: target.TileX, MotionTargetTileY: target.TileY, MotionTargetX: target.X,
            MotionTargetY: target.Y, MotionTargetIsDestination: target.IsDestination,
            MotionX: person.Position.X, MotionY: person.Position.Y,
            MotionPlaybackScale: person.AnimatorWalkingScale,
            MotionAnimationIndex: DirectionalWalkAnimation((short)(person[ObjectData.Direction] & 7)),
            MotionDirection: (short)(person[ObjectData.Direction] & 7), StandChecked: true, AnimatorFollowState: 1)
        {
            MotionPath = path,
            MotionTargetPathIndex = 1,
            FollowPath = path,
            FollowWaypointIndex = 1,
            LookTowardsFollow = true,
            BeginFollowPendingTick = true,
        };
        var clip = ResolveWalkAnimation(world, person, person.NativeRoute.MotionAnimationIndex);
        if (clip?.StandardRootMotionSamples is { Count: > 1 } samples)
            ShowWalkSkill(person, clip, 0f, samples.Count);
        frame.Waiting = true;
        return PrimitiveResult.Stop;
    }

    // Sims.exe's SAnimator tick advances Animator::GetMils by 42 ms. Practice::SetScale divides
    // Skill +0x1c duration by the active scale, and Practice::Apply advances +0x28 by elapsed / duration.
    private const float AnimationFramesPerTick = 1.2f;
    private const float AnimatorTickMilliseconds = 42f;
    private const float StartTransitionProgress = 0.95f;

    public static PrimitiveResult Start(SimulationWorld world, XObject me, StackFrame frame, XObject anchor, RoutingSlotRequest slot, TreeStack? stack)
    {
        if (me is not XPerson person)
            throw new NotSupportedException("Sims.exe dispatches Go To Routing Slot only for a person.");
        if (person.ActiveActionCancelRequested && person[PersonData.NonInterruptible] == 0)
            return FailRouteBeforeStart(person, anchor, slot, frame, stack, failureReason: 4);
        var goal = SelectDestination(world, person, anchor, slot, stack);
        if (goal is null && person[PersonData.Sitting] == 1 &&
            ResolveSeatedContainer(world, person) is { } currentSeat &&
            SimulationWorld.HasFunction(currentSeat, 0x1a))
        {
            if (ReferenceEquals(currentSeat, anchor))
                goal = new SelectedRouteGoal(person.Position, anchor[ObjectData.Direction]);
            else if (IsSeatedChairMatchingSlot(world, person, anchor, slot, currentSeat, out var chairSector))
                goal = new SelectedRouteGoal(person.Position, chairSector, ChairObjectId: currentSeat.Id);
        }
        if (goal is null)
        {
            Trace(world, person, anchor.Id, anchor.Position, "destination-rejected", "no candidate passed placement validation");
            return FailRouteBeforeStart(person, anchor, slot, frame, stack, failureReason: 1);
        }
        Trace(world, person, anchor.Id, goal.Value.Position, "destination-selected", null);
        return StartResolved(world, person, frame, anchor, slot, goal.Value.Position, stack,
            routeSearchDirection: goal.Value.SearchDirection, routeEntryFlags: goal.Value.EntryFlags,
            chairObjectId: goal.Value.ChairObjectId);
    }

    /// <summary>Explicit-position helper route with ordinary placement at the destination.
    /// User Go Here commands use Destination.iff's queued action instead.</summary>
    internal static PrimitiveResult StartDirectPosition(SimulationWorld world, XPerson person, StackFrame frame,
        ObjectPosition destination, TreeStack? stack)
    {
        if (person.ActiveActionCancelRequested && person[PersonData.NonInterruptible] == 0)
        {
            person[PersonData.RouteResult] = 4;
            return PrimitiveResult.False;
        }
        // The direct caller's +0x1c/+0x24 flags are zero. FUN_004550e0 checks the local route edge only
        // when the candidate remains in the current room; a room change is handled by the route manager.
        var slot = new RoutingSlotRequest();
        if (!Walkable(world, person, person, destination, slot, stack: stack))
        {
            Trace(world, person, person.Id, destination, "destination-rejected",
                $"direct target failed placement validation: {world.LastRouteCandidateFailureReason}");
            person[PersonData.RouteResult] = 1;
            return PrimitiveResult.False;
        }
        Trace(world, person, person.Id, destination, "destination-selected", null);
        return StartResolved(world, person, frame, person, slot, destination, stack, directPositionRequest: true);
    }

    /// <summary>Clears an interruptible explicit-position helper route before its replacement starts.
    /// Do not detach a BHAV-owned route or a route currently nested in a portal function: those need their owner stack to
    /// consume cancellation and release its portal state first.</summary>
    internal static bool CancelDirectPosition(SimulationWorld world, XPerson person, StackFrame ownerFrame)
    {
        var route = person.NativeRoute;
        if (route is null || !route.DirectPositionRequest || !ReferenceEquals(route.OwnerFrame, ownerFrame) ||
            person.NativeRouteParents.Count != 0 || route.PendingPortalId != 0 || route.PortalCallerDepth >= 0)
            return false;

        Trace(world, person, route.AnchorObjectId, route.Destination, "direct-position-replaced", null);
        CompleteEndFollow(world, person, route);
        person.NativeRoute = null;
        ownerFrame.Waiting = false;
        return true;
    }

    private static PrimitiveResult StartResolved(SimulationWorld world, XPerson person, StackFrame frame,
        XObject anchor, RoutingSlotRequest slot, ObjectPosition destination, TreeStack? stack,
        bool directPositionRequest = false, short? routeSearchDirection = null, short routeEntryFlags = 0,
        short chairObjectId = 0)
    {
        // cXPerson::TryGotoRoutingSlot calls ForceLocation immediately before route setup (person.s 0x10105CBC).
        person.AnimatorPoolTile = world.IsPoolTile(person.Position);
        var openedPortalId = (short)0;
        if (person.NativeRoute is { } parent && !ReferenceEquals(parent.OwnerFrame, frame))
        {
            person.NativeRouteParents.Add(parent);
            // FUN_0045aecf retains the portal in route-record +0x68 while its function tree runs. A nested
            // Go To Routing Slot from that portal function must cross the already-active portal without
            // recursively starting the same function again.
            if (parent.PendingPortalId == anchor.Id) openedPortalId = anchor.Id;
        }
        person.NativeRoute = new RouteState(anchor.Id, destination, slot,
            OwnerFrame: frame, OpenedPortalId: openedPortalId,
            ArrivalFacing: directPositionRequest
                ? person[ObjectData.Direction]
                : RelativeArrivalFacing(world, person, anchor, slot, destination, routeSearchDirection),
            MotionPlaybackScale: person.AnimatorWalkingScale,
            DirectPositionRequest: directPositionRequest, AnimatorFollowState: 1)
        {
            AwaitingBeginFollow = true,
            EntryFlags = directPositionRequest ? (short)0 : routeEntryFlags,
            ChairObjectId = directPositionRequest ? (short)0 : chairObjectId,
        };
        frame.Waiting = true;
        return Continue(world, person, frame, stack);
    }

    public static PrimitiveResult Continue(SimulationWorld world, XObject me, StackFrame frame, TreeStack? stack)
    {
        if (me is not XPerson person)
            throw new NotSupportedException("Sims.exe dispatches Go To Routing Slot only for a person.");
        var route = person.NativeRoute;
        if (route is null)
        {
            // FUN_0045ad3a resumes a nonzero frame through FUN_0045aecf with a null route-record
            // pointer. When FUN_0045c4b5 finds no active per-person route record, the manager returns 0.
            // Route records are not part of the saved BHAV stack, so a restored continuation can reach
            // this source-defined false result without any native route state to resume.
            frame.Waiting = false;
            return PrimitiveResult.False;
        }
        if (route.PendingFailureFeedbackDepth >= 0)
        {
            if (stack is not null && stack.Frames.Count > route.PendingFailureFeedbackDepth)
            {
                frame.Waiting = true;
                return PrimitiveResult.Stop;
            }
            person[PersonData.RouteResult] = route.FailureReason != 0 ? route.FailureReason : (short)1;
            ClearRoute(person);
            frame.Waiting = false;
            return PrimitiveResult.False;
        }
        // FUN_0045aecf state 4 (0045aecf.c:204-215): wait for the pushed chair Sit (OBJf 0x1a) tree to finish,
        // completing the route when Sitting != 0 in that chair or failing with RouteResult / reason 5.
        if (route.PendingSitDepth >= 0)
        {
            if (stack is not null && stack.Frames.Count > route.PendingSitDepth)
            {
                frame.Waiting = true;
                return PrimitiveResult.Stop;
            }
            var targetChair = route.ChairObjectId != 0 ? world.ObjectById(route.ChairObjectId) : null;
            if (targetChair is not null && person[PersonData.Sitting] != 0 &&
                ReferenceEquals(ResolveSeatedContainer(world, person), targetChair))
            {
                person[PersonData.RouteResult] = 0;
                ClearRoute(person);
                frame.Waiting = false;
                return PrimitiveResult.True;
            }
            var sitFailure = person[PersonData.RouteResult] != 0 ? person[PersonData.RouteResult] : (short)5;
            return FinishRouteFailure(world, person, route with { PendingSitDepth = -1 }, frame, stack,
                failureReason: sitFailure);
        }
        var actionCancelled = person.ActiveActionCancelRequested && person[PersonData.NonInterruptible] == 0;
        // Look Towards uses FUN_0045aafa's FollowOneStep/EndFollow path; unlike the routing-slot callback,
        // it does not branch on the active interaction's cancellation flag.
        if (actionCancelled && !route.LookTowardsFollow)
        {
            // Mac's person route callback checks ShouldInterrupt on every callback and retries EndFollow even
            // after its first deferred result. EndFollow sets SAnimator +0x27c and returns false while +0x180
            // is 1 or -1; once it can finish, the route takes its interrupted/failure exit. Keep retrying while
            // pending so a cancellation during an already-at-destination wait cannot become successful arrival.
            var cancelReason = route.FailureReason != 0 ? route.FailureReason : (short)4;
            person[PersonData.RouteResult] = cancelReason;
            route = route with { EndFollowPending = true, EndFollowOnArrival = false, FailureReason = cancelReason };
            person.NativeRoute = route;
            if (route.AnimatorFollowState is not (1 or -1))
            {
                CompleteEndFollow(world, person, route);
                return FinishRouteFailure(world, person, route, frame, stack, cancelReason);
            }
            frame.Waiting = true;
            return PrimitiveResult.Stop;
        }
        // A deferred EndFollow is consumed by SAnimator::Tick, not by the next behavior callback.
        // Tick advances +0x180 from 1 to 3 when the active practice permits it; this callback then
        // retries EndFollow and takes its immediate-success branch.
        if (route.EndFollowPending && !route.EndFollowOnArrival)
        {
            if (route.AnimatorFollowState is not (1 or -1))
            {
                CompleteEndFollow(world, person, route);
                // TryLookTowards converts FollowOneStep's 0 result to 1; the routed-movement callback's
                // state-0xA arm instead transitions to its failed-route state after EndFollow succeeds.
                if (route.FollowPlacementRejected)
                {
                    person.AnimatorRootX = null;
                    person.AnimatorRootY = null;
                }
                if (route.LookTowardsFollow)
                {
                    ClearRoute(person);
                    frame.Waiting = false;
                    return PrimitiveResult.True;
                }
                return FinishRouteFailure(world, person, route, frame, stack,
                    route.FailureReason != 0 ? route.FailureReason : (short)1);
            }
            frame.Waiting = true;
            return PrimitiveResult.Stop;
        }
        if (route.AnimatorFollowState == -1)
        {
            // Mac FollowOneStep returns status 2 for +0x180 == -1; TryGotoRoutingSlot propagates
            // that pending status without advancing the route or changing its BHAV continuation.
            frame.Waiting = true;
            return PrimitiveResult.Stop;
        }
        if (route.FollowPlacementRejected && route.AnimatorFollowState == 3)
        {
            CompleteEndFollow(world, person, route);
            person.AnimatorRootX = null;
            person.AnimatorRootY = null;
            if (route.LookTowardsFollow)
            {
                ClearRoute(person);
                frame.Waiting = false;
                return PrimitiveResult.True;
            }
            return FinishRouteFailure(world, person, route, frame, stack,
                route.FailureReason != 0 ? route.FailureReason : (short)1);
        }

        var portalCallbackCompleted = false;
        if (route.PendingPortalId != 0)
        {
            if (stack is null)
                throw new NotSupportedException("A route crossing a portal requires the active BHAV stack.");
            if (stack.Frames.Count > route.PortalCallerDepth) return PrimitiveResult.Stop;
            route = route with
            {
                PendingPortalId = 0,
                PortalCallerDepth = -1,
                OpenedPortalId = route.PendingPortalId,
                OpenedPortalFunctionObjectId = route.PendingPortalFunctionObjectId,
                PendingPortalFunctionObjectId = 0,
                AwaitingBeginFollow = true,
                MotionPath = null,
                MotionTargetX = int.MinValue,
                MotionTargetY = int.MinValue,
                MotionTargetPathIndex = -1,
            };
            portalCallbackCompleted = true;
            person.NativeRoute = route;
            route = TracePortalCallbackSnapshot(world, person, route);
        }

        var anchor = world.ObjectById(route.AnchorObjectId);
        if (anchor is null)
        {
            return EndFollowAndClearRoute(world, person, route, frame, stack, failureReason: 1);
        }

        // FUN_0045aecf state 1: a seated Sim first checks FUN_004818d9 to complete immediately when already seated
        // in the target seat; otherwise it runs that seat's function 0x1b (stand) as a pushed tree, passing the route
        // entry direction, and waits in state 5 until it returns (failing with reason 6 if Sitting remains nonzero).
        if (route.PendingStandDepth >= 0)
        {
            if (stack is null || stack.Frames.Count > route.PendingStandDepth) return PrimitiveResult.Stop;
            route = route with { PendingStandDepth = -1 };
            person.NativeRoute = route;
            if (person[PersonData.Sitting] != 0)
                return FinishRouteFailure(world, person, route, frame, stack, failureReason: 6);
        }
        else if (!route.StandChecked)
        {
            route = route with { StandChecked = true };
            person.NativeRoute = route;
            if (person[PersonData.Sitting] == 1)
            {
                var seat = ResolveSeatedContainer(world, person);
                if (!route.DirectPositionRequest && seat is not null && SimulationWorld.HasFunction(seat, 0x1a) &&
                    (ReferenceEquals(seat, anchor) ||
                     (route.ChairObjectId != 0 && seat.Id == route.ChairObjectId) ||
                     IsSeatedChairMatchingSlot(world, person, anchor, route.Slot, seat, out _)))
                {
                    person[PersonData.RouteResult] = 0;
                    ClearRoute(person);
                    frame.Waiting = false;
                    return PrimitiveResult.True;
                }
                if (TryPushSeatedStandTree(world, person, seat, anchor, route, stack, out var callerDepth))
                {
                    person.NativeRoute = route with { PendingStandDepth = callerDepth };
                    frame.Waiting = true;
                    return PrimitiveResult.Again;
                }
                return FinishRouteFailure(world, person, route, frame, stack, failureReason: 1);
            }
        }

        // FUN_0045aecf's route-record state 2 compares the Sim's current room with the final target room and
        // calls FUN_0045c92e before it advances the route waypoint queue. Let the portal function approach its
        // portal from the Sim's current position; waiting until the next path edge changes rooms can first move
        // the Sim onto a closed door tile, where that function's own Go To Relative request is blocked. The
        // portal callback also precedes our inferred path search: a closed doorway can make that search fail.
        var currentRoom = unchecked((ushort)person.RoomWord84);
        var destinationRoom = route.LookTowardsFollow ? currentRoom : route.DirectPositionRequest
            ? RoomKeyAt(world, route.Destination)
            : PortalRoomKey(anchor);

        // FUN_0045aecf route state 2 marks the previous portal and linked function part used,
        // then calls FUN_0045c92e again while the Sim remains outside the destination room.
        // A returned portal callback is not proof that its portal crossed the room boundary.
        if (portalCallbackCompleted && currentRoom != destinationRoom)
        {
            var usedPortalIds = route.UsedPortalIds?.ToHashSet() ?? [];
            usedPortalIds.Add(route.OpenedPortalId);
            if (route.OpenedPortalFunctionObjectId != 0)
                usedPortalIds.Add(route.OpenedPortalFunctionObjectId);
            MarkPortalRouteCostUsed(world, route.OpenedPortalId, route.OpenedPortalFunctionObjectId,
                person[ObjectData.RouteId]);
            route = route with
            {
                UsedPortalIds = usedPortalIds.ToArray(),
                OpenedPortalId = 0,
                OpenedPortalFunctionObjectId = 0,
            };
            person.NativeRoute = route;
        }

        if (!route.LookTowardsFollow && currentRoom != destinationRoom && route.OpenedPortalId == 0)
        {
            InitializeRoutePortalCosts(world, person, route);
            var routeSourceRoom = unchecked((ushort)person.RoomWord84);
            var portal = PortalForStep(world, person, anchor, routeSourceRoom, destinationRoom,
                route.UsedPortalIds?.ToHashSet(), route.DirectPositionRequest ? route.Destination : null);
            if (portal is null)
            {
                Trace(world, person, anchor.Id, route.Destination, "portal-rejected", "no portal connects the route rooms");
                return EndFollowAndClearRoute(world, person, route, frame, stack, failureReason: 2);
            }
            // FUN_0045aecf calls FUN_0045c92e whenever the route Sim is outside the target
            // room. FUN_0045c92e pushes the selected portal's function 0x0f without filtering
            // by the target's lot coordinates or by what that BHAV does to its caller.
            var failure = stack is null ? "active BHAV stack is missing" : null;
            if (stack is null || !StartPortalFunction(world, person, portal, stack, frame, route, out failure))
            {
                Trace(world, person, anchor.Id, route.Destination, "portal-function-rejected",
                    $"portal={portal.Id}; {failure}");
                return EndFollowAndClearRoute(world, person, route, frame, stack, failureReason: 2);
            }
            Trace(world, person, anchor.Id, route.Destination, "portal-function-started", null);
            return PrimitiveResult.Again;
        }

        // In Sims.exe (0045aecf.c:95-137), FUN_004824b8 (SelectDestination) runs in state 1 AFTER
        // portal callbacks bring the Sim into destinationRoom. Re-evaluating radial/multi-candidate
        // slots upon entering destinationRoom allows SelectDestination to score and path-test
        // candidates from the Sim's post-portal entry position rather than pre-portal coordinates.
        if (portalCallbackCompleted && currentRoom == destinationRoom &&
            !route.LookTowardsFollow && !route.DirectPositionRequest &&
            (route.Slot.Flags & 0xff) != 0 && route.Slot.SnapTargetSlot < 0)
        {
            var reselected = SelectDestination(world, person, anchor, route.Slot, stack);
            if (reselected is { } selectedDestination)
            {
                var arrivalFacing = RelativeArrivalFacing(world, person, anchor, route.Slot,
                    selectedDestination.Position, selectedDestination.SearchDirection);
                route = route with
                {
                    Destination = selectedDestination.Position,
                    ArrivalFacing = arrivalFacing,
                    EntryFlags = selectedDestination.EntryFlags,
                    ChairObjectId = selectedDestination.ChairObjectId,
                };
                person.NativeRoute = route;
            }
        }

        // FUN_0045aecf state 0x12 -> FUN_0045c4fa (0045c4fa.c:40-69): when the selected goal is a chair
        // (+0xc != 0), copy its chair-relative entry bit into PersonData.RouteEntryFlags and push that chair's
        // Sit tree (OBJf 0x1a) before BeginFollow, waiting in route state 4 until Sit finishes.
        if (!route.LookTowardsFollow && !route.DirectPositionRequest && route.ChairObjectId != 0)
        {
            person[PersonData.RouteEntryFlags] = route.EntryFlags;
            var chair = world.ObjectById(route.ChairObjectId);
            if (chair is not null && SimulationWorld.HasFunction(chair, 0x1a))
            {
                if (person[PersonData.Sitting] != 0 && ReferenceEquals(ResolveSeatedContainer(world, person), chair))
                {
                    person[PersonData.RouteResult] = 0;
                    ClearRoute(person);
                    frame.Waiting = false;
                    return PrimitiveResult.True;
                }
                if (person[PersonData.Gender] < 2 && stack is not null &&
                    SimulationWorld.FunctionTreeId(chair, 0x1a) is var sitTree && sitTree != 0 &&
                    chair.TypeRecord?.BehaviorScope is { } chairScope)
                {
                    var callerDepth = stack.Frames.Count;
                    if (PersonInteractionStack.Push(person, chair, stack, chairScope, sitTree, chair.StackObjectKey,
                            [0, 0, 0, 0]))
                    {
                        person.NativeRoute = route with { PendingSitDepth = callerDepth };
                        frame.Waiting = true;
                        return PrimitiveResult.Again;
                    }
                }
            }
            return FinishRouteFailure(world, person, route, frame, stack, failureReason: 5);
        }

        var pendingTarget = route.MotionTargetX != int.MinValue;
        IReadOnlyList<RouteWaypoint>? followedPath = route.LookTowardsFollow ? route.FollowPath : pendingTarget ? route.MotionPath : null;
        List<RouteWaypoint>? plannedPath = null;
        if (!route.LookTowardsFollow && followedPath is null && !TryPath(world, person, route.Destination, anchor, out plannedPath, out _,
                directPositionRequest: route.DirectPositionRequest, directSlot: route.Slot, stack: stack,
                startTileOverride: person.Position.X == route.WaypointX && person.Position.Y == route.WaypointY
                    ? (route.WaypointTileX, route.WaypointTileY)
                    : null,
                startCellCenterOverride: person.Position.X == route.WaypointX && person.Position.Y == route.WaypointY &&
                                         route.WaypointCellCenterX != int.MinValue
                    ? (route.WaypointCellCenterX, route.WaypointCellCenterY)
                    : null,
                allowCrossRoom: route.DirectPositionRequest || person.NativeRouteParents.Count != 0))
        {
            Trace(world, person, anchor.Id, route.Destination, "path-rejected", "no route path to selected destination");
            return EndFollowAndClearRoute(world, person, route, frame, stack, failureReason: 1);
        }
        IReadOnlyList<RouteWaypoint> path = followedPath ?? plannedPath!;

        if (route.AnimatorFollowState == 2)
        {
            // FUN_00589d4b does not read a skill: state 2 places the completed animator root
            // and returns 1. FUN_0045aecf state 3 then takes state 9 (EndFollow / successful route).
            // The practice may already have been stopped by the animator's terminal transition.
            var rootX = person.AnimatorRootX ?? route.MotionX;
            var rootY = person.AnimatorRootY ?? route.MotionY;
            var completedProjection = FindNearestRoutePoint(path, FTileIntegerCoordinate(rootX), FTileIntegerCoordinate(rootY));
            var position = completedProjection is { } point
                ? new ObjectPosition(point.X, point.Y, person.Position.Height)
                : new ObjectPosition(FTileIntegerCoordinate(rootX), FTileIntegerCoordinate(rootY), person.Position.Height);
            person[ObjectData.Direction] = QuantizeRootDirection(
                person.AnimatorRootDirection ?? person[ObjectData.Direction]);
            var placementAccepted = CanPlaceArrival(world, person, anchor, route.Slot, position, stack);
            if (!placementAccepted)
            {
                world.PlaceObject(person, person.Position, stack);
                world.RefreshPersonRoomStateAfterMovement(person);
            }
            else if (route.LookTowardsFollow)
            {
                world.PlaceObject(person, position, stack);
                world.RefreshPersonRoomStateAfterMovement(person);
            }
            CompleteEndFollow(world, person, route);
            if (placementAccepted && !route.LookTowardsFollow)
            {
                person[PersonData.RouteResult] = 0;
                ApplyRoutePlacement(world, person, anchor, route.Destination, route.Slot, stack);
                SetRouteEntryFlags(world, person, anchor, route);
                CommitArrivalHeading(person, route);
            }
            else if (placementAccepted && route.LookTowardsFollow)
            {
                CommitArrivalHeading(person, route);
            }
            Trace(world, person, anchor.Id, route.Destination,
                placementAccepted ? "follow-completed" : "follow-placement-rejected", null);
            if (!placementAccepted && !route.LookTowardsFollow)
                return FinishRouteFailure(world, person, route, frame, stack, failureReason: 1);
            ClearRoute(person);
            frame.Waiting = false;
            return PrimitiveResult.True;
        }

        if (route.AwaitingBeginFollow)
        {
            // FUN_0045aecf state 0x12 constructs the path before state 0x0c calls BeginFollow.
            // FUN_00589c89 and Mac SAnimator.s 0x10356B38–0x10356BD4 seed the root from the
            // current actor position, including a position changed by the completed portal callback.
            if (!route.LookTowardsFollow)
                world.PlaySoundEvent?.Invoke("begin_route", person.Id, false, false);
            person.AnimatorControlState = 3;
            person.AnimatorRouteTransitionState = 0;
            person.AnimatorGaitEntryState = 0;
            person.AnimatorPoolTransitionState = 0;
            person.AnimatorRootX = person.Position.X;
            person.AnimatorRootY = person.Position.Y;
            person.AnimatorRootDirection = person[ObjectData.Direction];
            person.SetAnimatorDisplacements(0f, 0f, 0f);
            route = route with
            {
                AwaitingBeginFollow = false,
                BeginFollowPendingTick = true,
                AnimatorFollowState = 1,
                MotionX = person.Position.X,
                MotionY = person.Position.Y,
                MotionAnimationIndex = -1,
                MotionFrame = 0,
                MotionPracticeFrame = 0,
                LocomotionFadeEndMils = 0,
            };
            person.NativeRoute = route;
        }

        if (!route.LookTowardsFollow && path.Count > 1)
            AskPeopleOnRouteToMove(world, person, anchor, path);

        if (world.CaptureExecutionTrace)
        {
            var nextTileDescription = path.Count > 1
                ? $"tile=({path[1].TileX},{path[1].TileY}) point=({path[1].X},{path[1].Y})"
                : "none";
            var owner = route.OwnerFrame is { } ownerFrame ? $"{ownerFrame.TreeId}:{ownerFrame.Instruction}" : "none";
            Trace(world, person, anchor.Id, route.Destination, "path-plan",
                $"owner={owner}; slotSnap={route.Slot.SnapTargetSlot}; slotFlags=0x{route.Slot.Flags:X}; direct={route.DirectPositionRequest}; " +
                $"before=({person.Position.X},{person.Position.Y},{person.Position.Height}); " +
                $"start=({path[0].X},{path[0].Y}); count={path.Count}; next={nextTileDescription}; " +
                $"end=({path[^1].X},{path[^1].Y})");
        }

        var hasPendingMotionTarget = route.MotionTargetX != int.MinValue;
        if (path.Count <= 1 && !hasPendingMotionTarget)
        {
            var atDestinationHeading = ArrivalHeading(route);
            var atDestinationError = atDestinationHeading - (person.AnimatorRootDirection ?? person[ObjectData.Direction]);
            while (atDestinationError >= 4f) atDestinationError -= 8f;
            while (atDestinationError < -4f) atDestinationError += 8f;
            if (route.AnimatorFollowState == 1 && !route.EndFollowPending && !route.LookTowardsFollow &&
                MathF.Abs(atDestinationError) > 0.000001f)
            {
                // FUN_0045c4fa still appends its look point when the Sim already stands on the goal, and returns 2
                // unless that point equals the final point (0045c4fa.c:122-132); route state 0x0c then BeginFollows
                // (FUN_00589c89) and the animator turns toward it. Native has no walking segment here, so enter
                // the same terminal transition the fade end produces (+0x1bc/+0x1c0 = current root, +0x180 = -1,
                // SAnimator.s 0x103509A0–0x103509CC): the state-3 gate in TryBeginRouteTurnRoot plays the turn,
                // TickRouteFollowTransition promotes follow state 2 once the heading matches, and the follow-2
                // branch above places the Sim and writes RouteEntryFlags before the BHAV resumes.
                var here = person.Position;
                person.AnimatorControlState = 3;
                person.AnimatorRootX = here.X;
                person.AnimatorRootY = here.Y;
                person.NativeRoute = route with
                {
                    AnimatorFollowState = -1,
                    BeginFollowPendingTick = false,
                    MotionPath = path,
                    MotionTargetPathIndex = path.Count - 1,
                    MotionTargetTileX = here.X >> 4,
                    MotionTargetTileY = here.Y >> 4,
                    MotionTargetX = here.X,
                    MotionTargetY = here.Y,
                    MotionTargetIsDestination = true,
                    MotionX = here.X,
                    MotionY = here.Y,
                    MotionAnimationIndex = -1,
                    MotionFrame = 0f,
                    MotionPracticeFrame = 0f,
                };
                frame.Waiting = true;
                return PrimitiveResult.Stop;
            }
            if (route.AnimatorFollowState is 1 or -1)
            {
                person.NativeRoute = route with { EndFollowPending = true, EndFollowOnArrival = true };
                frame.Waiting = true;
                return PrimitiveResult.Stop;
            }
            var destinationPosition = route.Destination with { Height = route.Destination.Height == 0 ? 1 : route.Destination.Height };
            var placementAccepted = CanPlaceArrival(world, person, anchor, route.Slot, destinationPosition, stack);
            if (!placementAccepted && !route.LookTowardsFollow)
            {
                CompleteEndFollow(world, person, route);
                return FinishRouteFailure(world, person, route, frame, stack, failureReason: 1);
            }
            if (!route.LookTowardsFollow)
                person[PersonData.RouteResult] = 0;
            CommitArrivalHeading(person, route);
            ApplyRoutePlacement(world, person, anchor, route.Destination, route.Slot, stack);
            SetRouteEntryFlags(world, person, anchor, route);
            // The already-at-destination path must finish SAnimator's follow state too. Otherwise
            // +0x188 remains Walking/Running and TryAnimate keeps returning busy after the route ends
            // (EndFollow__9SAnimatorFv 0x103568A4–0x103568E8; cXPerson routing 0x10106434–0x101064DC).
            CompleteEndFollow(world, person, route);
            ClearRoute(person);
            frame.Waiting = false;
            return PrimitiveResult.True;
        }

        var next = hasPendingMotionTarget
            ? new RouteWaypoint(route.MotionTargetTileX, route.MotionTargetTileY, route.MotionTargetX,
                route.MotionTargetY, route.MotionTargetX, route.MotionTargetY, route.MotionTargetIsDestination)
            : path[1];
        var nextPosition = new ObjectPosition(next.X, next.Y, person.Position.Height);
        var nextRoom = world.LotRoomMaps.TryGetValue(nextPosition.Height, out var routeMap) &&
                       (uint)next.TileX < routeMap.Width && (uint)next.TileY < routeMap.Height
            ? routeMap.RoomAt(next.TileX, next.TileY)
            : SimsEngine.World.RoomMap.NoRoom;
        // A route nested inside a portal function walks through that portal; it has no further room gate.
        if (!route.LookTowardsFollow && person.NativeRouteParents.Count == 0 && currentRoom != nextRoom && currentRoom != SimsEngine.World.RoomMap.NoRoom && nextRoom != SimsEngine.World.RoomMap.NoRoom)
        {
            // FUN_0045c92e initializes the route-cost state (including the person's unique Route ID) before
            // selecting the first portal for this route segment. A nested route on an already-open portal skips it.
            InitializeRoutePortalCosts(world, person, route);
            var routeSourceRoom = unchecked((ushort)person.RoomWord84);
            var routeDestinationRoom = route.DirectPositionRequest
                ? RoomKeyAt(world, route.Destination)
                : PortalRoomKey(anchor);
            // FUN_0045aecf retains the portal selected by FUN_0045c92e in route-record +0x68 while its
            // function tree runs. Reuse that portal at the room edge; re-ranking here can select a second
            // manager entry for the same multi-tile door before the retained route portal is marked used.
            var portal = route.OpenedPortalId != 0
                ? world.ObjectById(route.OpenedPortalId)
                : PortalForStep(world, person, anchor, routeSourceRoom, routeDestinationRoom,
                    route.UsedPortalIds?.ToHashSet(), route.DirectPositionRequest ? route.Destination : null);
            if (portal is null)
            {
                Trace(world, person, anchor.Id, route.Destination, "portal-rejected", "no portal connects the route rooms");
                return EndFollowAndClearRoute(world, person, route, frame, stack, failureReason: 2);
            }
            if (route.LastUsedPortalId != 0)
            {
                MarkPortalRouteCostUsed(world, route.LastUsedPortalId, route.LastUsedPortalFunctionObjectId,
                    person[ObjectData.RouteId]);
                route = route with { LastUsedPortalId = 0, LastUsedPortalFunctionObjectId = 0 };
                person.NativeRoute = route;
            }
            if (route.OpenedPortalId != portal.Id)
            {
                var failure = stack is null ? "active BHAV stack is missing" : null;
                if (stack is null || !StartPortalFunction(world, person, portal, stack, frame, route, out failure))
                {
                    Trace(world, person, anchor.Id, route.Destination, "portal-function-rejected",
                        $"portal={portal.Id}; {failure}");
                    return EndFollowAndClearRoute(world, person, route, frame, stack, failureReason: 2);
                }
                if (!route.AwaitingBeginFollow)
                    world.PlaySoundEvent?.Invoke("end_route", person.Id, false, false);
                Trace(world, person, anchor.Id, route.Destination, "portal-function-started", null);
                return PrimitiveResult.Again;
            }
            route = route with
            {
                OpenedPortalId = 0,
                LastUsedPortalId = portal.Id,
                LastUsedPortalFunctionObjectId = route.OpenedPortalFunctionObjectId,
                OpenedPortalFunctionObjectId = 0,
            };
            person.NativeRoute = route;
        }
        var targetX = hasPendingMotionTarget ? route.MotionTargetX : next.X;
        var targetY = hasPendingMotionTarget ? route.MotionTargetY : next.Y;
        var motionX = float.IsNaN(route.MotionX) ? person.Position.X : route.MotionX;
        var motionY = float.IsNaN(route.MotionY) ? person.Position.Y : route.MotionY;
        var desiredDirection = DirectionToWaypoint((person.Position.X, person.Position.Y), (targetX, targetY));
        if (!hasPendingMotionTarget)
        {
            if (route.MotionAnimationIndex < 0)
            {
                route = route with
                {
                    MotionDirection = desiredDirection,
                    MotionAnimationIndex = DirectionalWalkAnimation(desiredDirection),
                    MotionFrame = 0f,
                    MotionPracticeFrame = 0f,
                };
            }
            else if (route.MotionDirection != desiredDirection)
            {
                // Once the Sim is in walking-start or the loop, the animator follows the route polyline with the
                // same skill. The standing-adjust skills only turn a Sim that is standing at the route's start.
                var walking = route.MotionAnimationIndex >= 19;
                route = route with
                {
                    MotionDirection = desiredDirection,
                    MotionAnimationIndex = walking ? route.MotionAnimationIndex : DirectionalWalkAnimation(desiredDirection),
                    MotionFrame = walking ? route.MotionFrame : 0f,
                    MotionPracticeFrame = walking ? route.MotionPracticeFrame : 0f,
                };
            }
        }
        // Mac SAnimator transitions a start skill to its matching loop at practice progress 0.95.
        // The run path is skill 0x16 -> 0x15 for WalkStyle 1; the walk path is 0x13 -> 0x14.
        // See Mac SAnimator.s 0x103502AC–0x10350378 and Windows FUN_0058dc76.
        if (route.MotionAnimationIndex is 19 or 22 &&
            ResolveWalkAnimation(world, person, route.MotionAnimationIndex)?.StandardRootMotionSamples is { Count: > 1 } startSamples &&
            route.MotionPracticeFrame >= startSamples.Count * StartTransitionProgress)
        {
            route = route with
            {
                MotionAnimationIndex = person[ObjectData.WalkStyle] == 1 ? (short)21 : (short)20,
                MotionFrame = 0f,
                MotionPracticeFrame = 0f,
            };
            person.AnimatorControlState = person[ObjectData.WalkStyle] == 1 ? 10 : 9;
            // Mac's state-6/11 threshold paths write SAnimator +0x194 = 1 alongside +0x288
            // (0x1034FF44–0x1034FFC0, 0x10350378–0x103503F4).
            person.AnimatorGaitEntryState = 1;
            // SAnimator's state 9/10 transition writes +0x288 = 1 when the start skill reaches
            // its transition threshold (Mac SAnimator.s 0x1034FFB8–0x1034FFC0, 0x103503EC–0x103503F4).
            person.AnimatorPoolTransitionState = 1;
            person.NativeRoute = route;
        }

        var clip = ResolveWalkAnimation(world, person, route.MotionAnimationIndex);
        if (clip?.StandardRootMotionSamples is not { Count: > 1 } samples)
        {
            Trace(world, person, anchor.Id, route.Destination, "walk-skill-missing",
                $"standard animation {route.MotionAnimationIndex} has no root-motion samples");
            return EndFollowAndClearRoute(world, person, route, frame);
        }

        if (!hasPendingMotionTarget)
        {
            route = route with
            {
                MotionTargetPathIndex = 1,
                MotionTargetTileX = next.TileX,
                MotionTargetTileY = next.TileY,
                MotionTargetX = targetX,
                MotionTargetY = targetY,
                MotionTargetIsDestination = next.IsDestination,
                MotionX = motionX,
                MotionY = motionY,
                MotionPath = path,
            };
        }
        var turnPractice = person.AnimatorControlState is 4 or 5;
        var playbackScale = turnPractice && route.TurnRootPracticeScale is { } turnScale
            ? turnScale
            : person.AnimatorWalkingScale;
        // FollowOneStep reads the scale from the preceding SAnimator::Tick. This route callback executes before
        // the current person's animator tick advances +0x220.
        route = route with { MotionPlaybackScale = playbackScale };
        var effectivePlaybackScale = playbackScale;
        var oneShot = turnPractice || route.MotionAnimationIndex is >= 4 and <= 18;
        // Route root motion advances every SAnimator tick, including root-only camera ticks. The skeleton's
        // Practice::Apply is skipped by VitaBoy::AnimationTickRootOnly, so its phase must use the elapsed time
        // since the last full practice application when full ticks resume (Practice +0x18; skeleton.s
        // 0x10361960–0x103619BC). Root movement below continues to use one per-tick frameDelta.
        var practiceApplyMils = unchecked(person.AnimatorMilliseconds + AnimatorTickMilliseconds);
        var elapsedPracticeMils = person.AnimationPlayback?.LastPracticeApplyMils is { } lastApplyMils
            ? unchecked(practiceApplyMils - lastApplyMils)
            : AnimatorTickMilliseconds;
        var practiceFrameDelta = clip.DurationMilliseconds > 0f
            ? samples.Count * elapsedPracticeMils * effectivePlaybackScale / clip.DurationMilliseconds
            : AnimationFramesPerTick * (elapsedPracticeMils / (float)AnimatorTickMilliseconds) * effectivePlaybackScale;
        var frameDelta = oneShot
            ? MathF.Min(practiceFrameDelta, Math.Max(0f, samples.Count - route.MotionFrame))
            : practiceFrameDelta;
        var targetDxNow = targetX - motionX;
        var targetDyNow = targetY - motionY;
        var targetDistanceNow = MathF.Sqrt(targetDxNow * targetDxNow + targetDyNow * targetDyNow);
        // FollowOneStep tests the root left by the previous animator Tick. The next root step and its
        // second placement happen later in TickAvatarAnimations.
        var movedX = motionX;
        var movedY = motionY;
        // Tick keeps the floating animator root even when FindNearestPoint projects it onto the
        // end of the route segment. Once FollowOneStep has placed that endpoint, route progress
        // is at the waypoint even if the retained root is a fraction beyond it (SAnimator::Tick
        // stores the unprojected root at +0x80/+0x84 after placing the projected FTile).
        var activeFollowPlacement = route.AnimatorFollowState == 2;
        var arrived = activeFollowPlacement && (targetDistanceNow <= 0.0001f ||
                      (person.Position.X == targetX && person.Position.Y == targetY));

        // FollowOneStep adds 0.5 to the current root before fctiwz (Mac SAnimator.s
        // 0x103569F0–0x10356A20), then projects the resulting FTile onto this saved route.
        var movedXInteger = FTileIntegerCoordinate(movedX);
        var movedYInteger = FTileIntegerCoordinate(movedY);
        var nearestRoutePoint = FindNearestRoutePoint(path, movedXInteger, movedYInteger);
        var movedPosition = nearestRoutePoint is { } nearest
            ? new ObjectPosition(nearest.X, nearest.Y, person.Position.Height)
            : new ObjectPosition(movedXInteger, movedYInteger, person.Position.Height);
        // SAnimator::Tick keeps this projected per-step root vector at +0x28c/+0x290 and tests its
        // squared length in the route-entry gate (Mac SAnimator.s 0x10350F4C–0x10350FFC,
        // 0x10350798–0x103507A8).
        person.AnimatorProjectedRootStepX = movedX - motionX;
        person.AnimatorProjectedRootStepY = movedY - motionY;
        var positionBefore = person.Position;
        var captureWaypointSnapshot = CanCaptureRouteSnapshot(world, route);
        if (captureWaypointSnapshot)
        {
            route = route with { DiagnosticSnapshotCount = route.DiagnosticSnapshotCount + 1 };
            person.NativeRoute = route;
            Trace(world, person, anchor.Id, route.Destination, "waypoint-world-before",
                $"owner={route.OwnerFrame?.TreeId}:{route.OwnerFrame?.Instruction}; next=({next.TileX},{next.TileY})@({targetX},{targetY}); " +
                RouteWorldSnapshot(world, person, positionBefore, movedPosition));
        }
        // Mac FollowOneStep validates and places the current root here; Tick performs the predicted-root
        // placement later in the same Simulate pass (SAnimator.s 0x10351004–0x1035109C). Windows
        // FUN_00589d4b corroborates FollowOneStep's check/rollback/place order. cXPerson::CanPlace
        // (Windows FUN_004355c7) forwards null reference-object and slot arguments, so this is ordinary
        // placement rather than referenced-slot validation. ObjectPosition stores ground as height 0, while
        // FUN_00432a70's placement spec stores it as level 1.
        var canPlacePosition = movedPosition with
        {
            Height = movedPosition.Height == 0 ? 1 : movedPosition.Height,
        };
        // FollowOneStep writes the discrete person facing from SAnimator +0x88 before it
        // validates and places the current root (Mac SAnimator.s 0x10356978–0x103569E4).
        // Keep the continuous root heading for the skeleton; ObjectData.Direction is the
        // separately quantized eight-way placement facing.
        if (route.AnimatorFollowState == 2)
            person[ObjectData.Direction] = QuantizeRootDirection(
                person.AnimatorRootDirection ?? person[ObjectData.Direction]);
        // FUN_00589d4b returns pending for follow state 1 without invoking object callbacks.
        // Path/practice preparation still feeds the separately scheduled animator Tick.
        var accepted = !activeFollowPlacement || CanPlaceArrival(world, person, anchor, route.Slot, canPlacePosition, stack);
        if (!accepted && world.CaptureExecutionTrace)
            Trace(world, person, anchor.Id, route.Destination, "follow-placement-rejected",
                $"follow={route.AnimatorFollowState}; control={person.AnimatorControlState}; " +
                $"position=({canPlacePosition.X},{canPlacePosition.Y},{canPlacePosition.Height}); " +
                $"failure={world.LastPlacementFailureCode}; portalFailure={world.LastPortalPlacementFailureCode}");
        if (!accepted)
        {
            movedPosition = positionBefore;
            movedX = positionBefore.X;
            movedY = positionBefore.Y;
        }
        if (activeFollowPlacement)
        {
            world.PlaceObject(person, movedPosition, stack);
            world.RefreshPersonRoomStateAfterMovement(person);
        }
        if (world.CaptureExecutionTrace)
            Trace(world, person, anchor.Id, route.Destination,
                activeFollowPlacement ? "waypoint-placement" : "follow-pending",
                $"owner={route.OwnerFrame?.TreeId}:{route.OwnerFrame?.Instruction}; next=({next.TileX},{next.TileY})@({targetX},{targetY}); " +
                $"follow={route.AnimatorFollowState}; control={person.AnimatorControlState}; skill={route.MotionAnimationIndex}; fade={route.LocomotionFadeEndMils}; " +
                $"clip={clip.Name}; markers={clip.LocomotionMarkerOne}/{clip.LocomotionMarkerTwo}; " +
                $"before=({positionBefore.X},{positionBefore.Y},{positionBefore.Height}); " +
                $"after=({person.Position.X},{person.Position.Y},{person.Position.Height})");
        if (captureWaypointSnapshot)
            Trace(world, person, anchor.Id, route.Destination, "waypoint-world-after",
                $"owner={route.OwnerFrame?.TreeId}:{route.OwnerFrame?.Instruction}; next=({next.TileX},{next.TileY})@({targetX},{targetY}); " +
                RouteWorldSnapshot(world, person, positionBefore, person.Position));

        // FollowOneStep returns 0 after a rejected preflight and does not advance the animation step.
        if (!accepted)
        {
            if (!route.LookTowardsFollow)
                person[PersonData.RouteResult] = 1;
            route = route with { MotionX = movedX, MotionY = movedY, FailureReason = 1 };
            if (route.AnimatorFollowState is 1 or -1)
            {
                // TryLookTowards calls EndFollow after result 0; routed movement enters callback state 0xA,
                // which does the same. EndFollow defers while +0x180 is 1/-1.
                person.NativeRoute = route with { EndFollowPending = true, FollowPlacementRejected = true };
                return PrimitiveResult.Stop;
            }
            if (route.LookTowardsFollow)
            {
                route = route with { AnimatorFollowState = 3 };
                person.NativeRoute = route;
                CompleteEndFollow(world, person, route);
                ClearRoute(person);
                person.AnimatorRootX = null;
                person.AnimatorRootY = null;
                frame.Waiting = false;
                return PrimitiveResult.True;
            }
            route = route with { AnimatorFollowState = 3 };
            person.NativeRoute = route;
            CompleteEndFollow(world, person, route);
            person.AnimatorRootX = null;
            person.AnimatorRootY = null;
            return FinishRouteFailure(world, person, route, frame, stack, failureReason: 1);
        }

        var oneShotEnded = oneShot && route.MotionFrame + frameDelta >= samples.Count;
        var standingAdjustEnded = oneShotEnded && !turnPractice &&
                                  route.MotionAnimationIndex is >= 11 and <= 18;
        if (standingAdjustEnded)
        {
            // The route heading belongs to the animator while a standing-adjust skill runs. Commit
            // the person's discrete placement facing only after that turn skill has completed.
            person[ObjectData.Direction] = route.MotionDirection;
            person.AnimatorRootDirection = route.MotionDirection;
            person.RefreshFootprintBounds();
        }
        var nextAnimationIndex = turnPractice ? route.MotionAnimationIndex : route.MotionAnimationIndex switch
        {
            >= 4 and <= 18 => person[ObjectData.WalkStyle] == 1 ? (short)22 : (short)19,
            19 => (short)20,
            22 => (short)21,
            _ => route.MotionAnimationIndex,
        };
        var nextFrame = oneShotEnded ? 0f : oneShot
            ? route.MotionFrame + frameDelta
            : (route.MotionFrame + frameDelta) % samples.Count;
        var nextPracticeFrame = oneShotEnded && !turnPractice ? 0f : world.AvatarAnimationRootOnly
            ? route.MotionPracticeFrame
            : turnPractice || route.MotionAnimationIndex is 19 or 22
                ? Math.Min(samples.Count, route.MotionPracticeFrame + frameDelta)
                : (route.MotionPracticeFrame + frameDelta) % samples.Count;
        route = route with
        {
            MotionAnimationIndex = oneShotEnded && !turnPractice ? nextAnimationIndex : route.MotionAnimationIndex,
            MotionFrame = oneShotEnded && turnPractice ? route.MotionFrame + frameDelta : nextFrame,
            MotionPracticeFrame = nextPracticeFrame,
            MotionX = movedX,
            MotionY = movedY,
        };
        person.AnimatorRootX = movedX;
        person.AnimatorRootY = movedY;
        if (standingAdjustEnded)
        {
            // Mac's state 6/11 handlers clear +0x194 and +0x288 before starting WalkingStart or
            // RunningStart (SAnimator.s 0x1034FF44–0x1034FF54 and 0x10350378–0x10350388).
            // The route's standing-adjust one-shot has just completed, so enter that same state
            // before the start skill begins.
            person.AnimatorControlState = person[ObjectData.WalkStyle] == 1 ? 11 : 6;
            person.AnimatorGaitEntryState = 0;
            person.AnimatorPoolTransitionState = 0;
            var startClip = ResolveWalkAnimation(world, person, nextAnimationIndex);
            if (startClip is { StandardDistance: > 0f })
            {
                // State 6/11 setup calls StdAnimDist(start skill), converts its skill units to FTile
                // with 16/3, and rotates the displacement by (root direction + 6) * pi/4
                // (SAnimator.s 0x1034FEBC–0x1034FF40 and 0x10350300–0x10350374).
                var angle = ((person.AnimatorRootDirection ?? person[ObjectData.Direction]) + 6f) * (MathF.PI / 4f);
                var startDistance = startClip.StandardDistance * (16f / 3f);
                route = route with
                {
                    StartRootOriginX = movedX,
                    StartRootOriginY = movedY,
                    StartRootTargetX = movedX + MathF.Cos(angle) * startDistance,
                    StartRootTargetY = movedY + MathF.Sin(angle) * startDistance,
                };
            }
        }
        if (route.LookTowardsFollow && arrived && next.IsDestination)
        {
            route = route with { AnimatorFollowState = 3 };
            person.NativeRoute = route;
            CompleteEndFollow(world, person, route);
            ClearRoute(person);
            frame.Waiting = false;
            return PrimitiveResult.True; // FUN_0045aafa converts a completed FollowOneStep result of 0 to true.
        }
        if (arrived && next.IsDestination)
        {
            var wasEndFollowPending = route.EndFollowPending;
            route = route with { AnimatorFollowState = 3 };
            person.NativeRoute = route;
            CompleteEndFollow(world, person, route);
            if (!wasEndFollowPending)
            {
                person[PersonData.RouteResult] = 0;
                ApplyRoutePlacement(world, person, anchor, route.Destination, route.Slot, stack);
                SetRouteEntryFlags(world, person, anchor, route);
                CommitArrivalHeading(person, route);
            }
            Trace(world, person, anchor.Id, route.Destination, "waypoint", null);
            if (wasEndFollowPending)
                return FinishRouteFailure(world, person, route, frame, stack,
                    route.FailureReason != 0 ? route.FailureReason : (short)1);
            ClearRoute(person);
            frame.Waiting = false;
            return PrimitiveResult.True;
        }
        if (arrived)
        {
            if (route.LookTowardsFollow)
            {
                var nextIndex = route.FollowWaypointIndex + 1;
                if (route.FollowPath is not { } followPath || nextIndex >= followPath.Count)
                {
                    return EndFollowAndClearRoute(world, person, route, frame, stack);
                }
                var followTarget = followPath[nextIndex];
                route = route with
                {
                    FollowWaypointIndex = nextIndex,
                    MotionTargetPathIndex = nextIndex,
                    MotionTargetTileX = followTarget.TileX,
                    MotionTargetTileY = followTarget.TileY,
                    MotionTargetX = followTarget.X,
                    MotionTargetY = followTarget.Y,
                    MotionTargetIsDestination = followTarget.IsDestination,
                    MotionPath = followPath,
                    MotionX = targetX,
                    MotionY = targetY,
                };
            }
            else
            {
                // BeginFollow retains its TileList; Tick advances +0x178 rather than rebuilding the graph
                // after every partition point (Mac SAnimator.s 0x10350BB4–0x10350BF0).
                var nextIndex = Math.Max(1, route.MotionTargetPathIndex) + 1;
                if (nextIndex < path.Count)
                {
                    var following = path[nextIndex];
                    route = route with
                    {
                        MotionTargetPathIndex = nextIndex,
                        MotionTargetTileX = following.TileX,
                        MotionTargetTileY = following.TileY,
                        MotionTargetX = following.X,
                        MotionTargetY = following.Y,
                        MotionTargetIsDestination = following.IsDestination,
                        MotionPath = path,
                        MotionX = targetX,
                        MotionY = targetY,
                    };
                }
            }
            Trace(world, person, anchor.Id, route.Destination, "waypoint", null);
        }
        person.NativeRoute = route;
        var practiceClip = ResolveWalkAnimation(world, person, route.MotionAnimationIndex) ?? clip;
        var practiceSamples = practiceClip.StandardRootMotionSamples?.Count ?? samples.Count;
        ShowWalkSkill(person, practiceClip, nextPracticeFrame, practiceSamples);
        return PrimitiveResult.Stop;
    }

    // The practice phase selects the visible pose. Root-only VitaBoy ticks keep that phase fixed while route root
    // motion continues to move the Sim; full ticks advance both independently from the same skill.
    internal static void ShowWalkSkill(XPerson person, AvatarAnimationClip clip, float routeFrame, int sampleCount)
    {
        var loop = true;
        if (person.NativeRoute is { } route)
        {
            person.AnimatorCurrentSkill = route.MotionAnimationIndex;
            loop = route.MotionAnimationIndex is not (>= 4 and <= 18);
        }
        if (person.AnimationPlayback is not { RouteDriven: true } playback || playback.Clip.Name != clip.Name)
            person.AnimationPlayback = playback = new AvatarAnimationPlayback(clip, backwards: false, loop: loop,
                startedAtMils: person.AnimatorMilliseconds)
                // Skill::NewPractice initializes Practice +0x68 to -1, and Practice::Bind leaves it intact.
                // SAnimator::Tick treats any nonzero value as interruptible.
                { RouteDriven = true, Interruptible = true };
        playback.SetFrame(Math.Min(routeFrame * clip.FrameCount / Math.Max(sampleCount, 1), clip.FrameCount - 0.001f));
    }

    internal static AvatarAnimationClip? ResolveWalkAnimation(SimulationWorld world, XPerson person, int standardIndex)
    {
        var age = person[PersonData.PersonAge];
        var child = age <= 17;
        AvatarAnimationClip? clip = null;
        if (person.TypeRecord is { } typeRecord &&
            world.CustomWalkAvatarAnimationTable.TryGetValue((unchecked((uint)typeRecord.Key), standardIndex), out var customClip))
            clip = customClip;

        // GetStdSkill reads cXPerson's cached +0x60c after the ObjSelector entry; ForceLocation is the writer.
        if (person.AnimatorPoolTile)
        {
            var poolTable = child ? (ushort)(158 + (person[PersonData.Gender] & 1)) : (ushort)160;
            if (world.StandardWalkFallbackAnimationTable.TryGetValue((poolTable, standardIndex), out var poolClip))
                return poolClip;
        }
        if (clip is not null) return clip;

        // Mac SkillLookup.s::GetStdSkill uses Global STR# 151 for age <= 17, then 152/153 by gender.
        // It falls back to STR# 150 when the selected table entry is empty.
        var fallbackTable = child ? (ushort)151 : (ushort)(152 + (person[PersonData.Gender] & 1));
        if (world.StandardWalkFallbackAnimationTable.TryGetValue((fallbackTable, standardIndex), out clip)) return clip;
        return world.StandardWalkFallbackAnimationTable.GetValueOrDefault(((ushort)150, standardIndex));
    }

    // FUN_0045aecf stores the direction returned by FUN_00452129 for the accepted next route point.
    // Mac SAnimator::Tick normalizes atan2(y, x) into eight sectors by adding 1/16 turn, multiplying
    // by 8, and truncating after +0.5 (0x1034F69C-0x1034F790). Its sector table is
    // [15,13,11,12,14,16,18,17]; converting FUN_00452129's route octants yields this mapping.
    internal static short DirectionalWalkAnimation(short direction) => direction switch
    {
        0 => 18,
        1 => 17,
        2 => 15,
        3 => 13,
        4 => 11,
        5 => 12,
        6 => 14,
        7 => 16,
        _ => 15,
    };

    /// <summary>Matches the route step in Mac SAnimator::Tick. It divides Skill +0x20 by 3, multiplies by 16,
    /// and scales by elapsed milliseconds over Practice duration (Skill +0x1c / scale +0x24), at
    /// 0x10350F68–0x10350FB0. The constants are lbl_1059BBC4 +0x3c and +0x9c.</summary>
    internal static float RouteStepDistance(float standardDistance, float skillDurationMilliseconds, float practiceScale,
        float elapsedMilliseconds)
    {
        var practiceDuration = skillDurationMilliseconds / practiceScale;
        var ticksPerPractice = practiceDuration / elapsedMilliseconds;
        var skillDistancePerTile = standardDistance / 3f;
        return (16f * skillDistancePerTile) / ticksPerPractice;
    }

    // Mac TileList::FindNearestPoint(this, FTilePt*, -1), 0x10164010, scans segment
    // indices 0 through count-3. The original TileList includes a trailing entry that
    // this port's path does not, so its last segment is count-2 here.
    internal static (int X, int Y)? FindNearestRoutePoint(IReadOnlyList<RouteWaypoint> path, int x, int y)
    {
        var nearest = FindNearestRouteSegment(path, x, y);
        return nearest is { } point ? (point.X, point.Y) : null;
    }

    private static (int X, int Y, int SegmentIndex)? FindNearestRouteSegment(IReadOnlyList<RouteWaypoint> path,
        int x, int y, int firstSegment = 0, int? lastSegmentOverride = null)
    {
        var lastSegment = Math.Min(path.Count - 2, lastSegmentOverride ?? path.Count - 2);
        firstSegment = Math.Max(0, firstSegment);
        if (lastSegment < 0) return null;
        if (firstSegment > lastSegment) return null;

        var first = path[firstSegment];
        var bestX = first.X;
        var bestY = first.Y;
        var bestDx = bestX - x;
        var bestDy = bestY - y;
        var bestDistanceSquared = (long)bestDx * bestDx + (long)bestDy * bestDy;
        var bestSegmentIndex = -1;
        for (var index = firstSegment; index <= lastSegment; index++)
        {
            var start = path[index];
            var end = path[index + 1];
            // The Mac routine computes the segment vector from the next point back to
            // the previous point, then projects from the next point using that vector.
            var dx = start.X - end.X;
            var dy = start.Y - end.Y;
            var lengthSquared = MathF.FusedMultiplyAdd(dy, dy, (float)dx * dx);
            if (lengthSquared == 0f) continue;

            var numerator = MathF.FusedMultiplyAdd(y - end.Y, dy, (float)(x - end.X) * dx);
            var amount = Math.Clamp(numerator / lengthSquared, 0f, 1f);
            // Mac TileList::FindNearestPoint applies fctiwz directly to these projections
            // (XRouting.s 0x10164218–0x10164248); its only 0.5 bias is earlier in FollowOneStep.
            var projectedX = (int)MathF.FusedMultiplyAdd(amount, dx, end.X);
            var projectedY = (int)MathF.FusedMultiplyAdd(amount, dy, end.Y);
            var projectedDx = projectedX - x;
            var projectedDy = projectedY - y;
            var distanceSquared = (long)projectedDx * projectedDx + (long)projectedDy * projectedDy;
            if (distanceSquared >= bestDistanceSquared) continue;
            bestDistanceSquared = distanceSquared;
            bestX = projectedX;
            bestY = projectedY;
            bestSegmentIndex = index;
        }

        return (bestX, bestY, bestSegmentIndex);
    }

    /// <summary>Matches FollowOneStep's lbl_1059BBC4[0] bias before PowerPC fctiwz (Mac SAnimator.s
    /// 0x103569F0–0x10356A20): add 0.5, then truncate toward zero.</summary>
    internal static int FTileIntegerCoordinate(float coordinate) => (int)(coordinate + 0.5f);

    /// <summary>Matches SAnimator::Tick's fctiwz conversion for the newly computed root
    /// (Mac SAnimator.s 0x10351004–0x10351028); this conversion has no +0.5 bias.</summary>
    internal static int TickRootIntegerCoordinate(float coordinate) => (int)coordinate;

    /// <summary>Applies the source Practice-phase root interpolation in standing-start states 6/11.
    /// SAnimator.s writes root +0x80/+0x84 as origin (+0x1c8/+0x1cc) to target (+0x1bc/+0x1c0)
    /// while primary Practice +0x28 is below 1 (0x1034FF44–0x1034FFF8, 0x10350378–0x1035042C).</summary>
    internal static void TickStandingStartRootInterpolation(SimulationWorld world, XPerson person)
    {
        var startSkill = person.AnimatorControlState switch
        {
            6 => 19,
            11 => 22,
            _ => -1,
        };
        if (startSkill < 0 || person.NativeRoute is not { } route || route.MotionAnimationIndex != startSkill ||
            route.StartRootOriginX is not { } originX || route.StartRootOriginY is not { } originY ||
            route.StartRootTargetX is not { } targetX || route.StartRootTargetY is not { } targetY)
            return;
        var sampleCount = ResolveWalkAnimation(world, person, startSkill)?.StandardRootMotionSamples?.Count ?? 0;
        if (sampleCount <= 0) return;
        var phase = route.MotionPracticeFrame / sampleCount;
        if (phase >= 1f) return;
        var rootX = originX + (targetX - originX) * phase;
        var rootY = originY + (targetY - originY) * phase;
        person.AnimatorRootX = rootX;
        person.AnimatorRootY = rootY;
        person.NativeRoute = route with { MotionX = rootX, MotionY = rootY };
    }

    /// <summary>Builds state 5's destination from the route target and the authored StdAnimOffset translation.
    /// Mac obtains the final "ROOT" motion translation and subtracts its angle-rotated X/Y components from
    /// +0x1bc/+0x1c0 (SAnimator.s 0x1034F780–0x1034F8A0; Windows FUN_0058dc76).</summary>
    internal static (float X, float Y) TurnRootTarget(float targetX, float targetY, float angle,
        System.Numerics.Vector3 standardOffset)
    {
        var cosine = MathF.Cos(angle);
        var sine = MathF.Sin(angle);
        return (targetX - (standardOffset.X * cosine + standardOffset.Y * sine),
            targetY - (standardOffset.X * sine + standardOffset.Y * cosine));
    }

    /// <summary>Writes the arrival heading to the animator root (+0x88) and the person facing (+0x4c, SAnimator
    /// state 7's fctiwz(direction + 0.5) wrap). After the animated state-3 turn this matches the current heading;
    /// for octant facings it equals the previous ArrivalFacing write.</summary>
    private static void CommitArrivalHeading(XPerson person, RouteState route)
    {
        var heading = ArrivalHeading(route);
        person[ObjectData.Direction] = SimsEngine.Character.AvatarDirection.ToObjectDirection(heading);
        person.AnimatorRootDirection = heading;
    }

    /// <summary>The terminal heading Tick derives at the last walking point: atan2 from TileList[count-2] toward the
    /// look point FUN_0045c4fa appends with FUN_0045f7c5 (0045c4fa.c:70-132), stored in +0x1c4 at the fade end
    /// (SAnimator.s 0x10350598–0x10350690, 0x103509C4) and in +0x1dc for the pool exit. Slot facing modes -1/-2 and
    /// explicit facings build that point one FUN_004222bd tile along an octant, which native keeps as ArrivalFacing.
    /// Facing -3 (0045c4fa.c:80-92) instead continues the final path segment (last point minus the previous
    /// one), so the Sim keeps its travel heading; a zero-length final segment falls back to the person's +0x4c
    /// direction, the ArrivalFacing captured when the route started. Direct-position helper routes are treated
    /// the same way (their native ArrivalFacing is that same captured +0x4c). Look Towards keeps ArrivalFacing.</summary>
    internal static float ArrivalHeading(RouteState route)
    {
        if (route.LookTowardsFollow)
            return route.LookTowardsHeading ?? route.ArrivalFacing;
        if (!route.DirectPositionRequest && route.Slot.Facing != -3)
            return route.ArrivalFacing;
        if (route.MotionPath is { Count: >= 2 } path)
        {
            var last = path[^1];
            var previous = path[^2];
            var dx = last.X - previous.X;
            var dy = last.Y - previous.Y;
            if (dx != 0 || dy != 0) return SimsEngine.Character.AvatarDirection.Toward(dx, dy);
        }
        return route.ArrivalFacing;
    }

    /// <summary>Enters Mac SAnimator state 4 or 5 from state 3 according to the wrapped heading error and
    /// route-target distance gates (SAnimator.s 0x1034F438–0x1034F610). State 4 uses its body-turn skill
    /// buckets; state 5 uses the standing-turn skill's authored offset and scaled practice.</summary>
    internal static bool TryBeginRouteTurnRoot(SimulationWorld world, XPerson person)
    {
        if (person.AnimatorControlState != 3 || person.NativeRoute is not
            { MotionTargetX: not int.MinValue, MotionTargetY: not int.MinValue } route ||
            !float.IsFinite(person.AnimatorWalkingScale) || person.AnimatorWalkingScale <= 0f)
            return false;

        var rootX = person.AnimatorRootX ?? route.MotionX;
        var rootY = person.AnimatorRootY ?? route.MotionY;
        if (!float.IsFinite(rootX) || !float.IsFinite(rootY)) return false;
        if (route.TransitionTargetX is { } transitionX && route.TransitionTargetY is { } transitionY)
        {
            // The state-3 gate adjusts toward +0x1bc/+0x1c0 after a route-preference arrival. Within +0x90
            // (0.5, or 0.1 for special body modes) it snaps the root there (0x1034F438–0x1034F4F8); farther away
            // its state-5 turn also ends with the root on +0x1bc. The arrival's one repeated step is shorter
            // than +0x90 for authored walk clips, so native snaps in both cases. The heading target +0x1c4 is
            // not written by that arrival; keeping the current heading is a native inference.
            person.AnimatorRootX = transitionX;
            person.AnimatorRootY = transitionY;
            person.NativeRoute = route with { MotionX = transitionX, MotionY = transitionY };
            return false;
        }
        var stepTargetX = (float)route.MotionTargetX;
        var stepTargetY = (float)route.MotionTargetY;
        if (route.AnimatorFollowState == 1)
        {
            var lookahead = ResolveRouteLookahead(world, person, route, rootX, rootY);
            if (lookahead.AdvancedCursor)
            {
                route = lookahead.Route;
                person.NativeRoute = route;
            }
            stepTargetX = lookahead.TargetX;
            stepTargetY = lookahead.TargetY;
        }
        var dx = route.MotionTargetX - rootX;
        var dy = route.MotionTargetY - rootY;
        var distance = MathF.Sqrt(dx * dx + dy * dy);
        var aimDx = stepTargetX - rootX;
        var aimDy = stepTargetY - rootY;
        var aimDistance = MathF.Sqrt(aimDx * aimDx + aimDy * aimDy);
        var specialBody = person.AnimatorBodyStringMode > 1;
        var maximumDistance = specialBody ? 3.1f / 12f : 3.1f;
        var snapDistance = specialBody ? 0.5f / 5f : 0.5f;

        var targetDirection = aimDistance <= 0.000001f && route.MotionTargetIsDestination
            ? ArrivalHeading(route) : SimsEngine.Character.AvatarDirection.Toward(aimDx, aimDy);
        var currentDirection = person.AnimatorRootDirection ?? person[ObjectData.Direction];
        var headingError = targetDirection - currentDirection;
        while (headingError >= 4f) headingError -= 8f;
        while (headingError < -4f) headingError += 8f;
        // A matching terminal pose needs no standing-adjust practice. The original state-3
        // match gate redispatches follow/gait completion (0x1034F438–0x1034F4F4).
        if (distance <= 0.000001f && MathF.Abs(headingError) <= 0.000001f) return false;
        var angleThreshold = (specialBody ? 0.0625f : 0.25f) / person.AnimatorWalkingScale;
        if (MathF.Abs(headingError) >= angleThreshold || (route.AnimatorFollowState != 1 && distance >= maximumDistance))
            return TryBeginRouteBodyTurn(world, person, route, rootX, rootY, targetDirection, currentDirection,
                headingError);
        // During active follow (AnimatorFollowState == 1) once heading is within angleThreshold, a distant
        // target (>= maximumDistance) enters gait rather than a short state-5 standing-adjust step.
        if (distance >= maximumDistance)
            return false;

        // Within the angle limit and nearer than +0x90, the gate does not start a practice: it copies
        // +0x1bc/+0x1c0 to the root, calls SetDirection(+0x1c4), stores fctiwz(+0x88 + 0.5) wrapped to [0,7]
        // in cXObject +0x4c and redispatches state 3 (SAnimator.s 0x1034F60C–0x1034F698). Native applies this
        // only to the follow-state -1 arrival, where the destination point and ArrivalHeading are the
        // transition target the fade end stored (0x103509A0–0x103509CC).
        if (route.AnimatorFollowState == -1 && route.MotionTargetIsDestination && distance < snapDistance)
        {
            person.AnimatorRootX = route.MotionTargetX;
            person.AnimatorRootY = route.MotionTargetY;
            person.AnimatorRootDirection = targetDirection;
            person[ObjectData.Direction] = SimsEngine.Character.AvatarDirection.ToObjectDirection(targetDirection);
            person.NativeRoute = route with { MotionX = route.MotionTargetX, MotionY = route.MotionTargetY };
            return false;
        }

        // This turn selector uses the atan2 octant boundaries at 22.5 degrees, unlike the route
        // waypoint direction's 2:1 slope boundaries (SAnimator.s 0x1034F69C–0x1034F790).
        var skill = StandingTurnAnimationForVector(dx, dy);
        var clip = ResolveWalkAnimation(world, person, skill);
        if (clip?.StandardRootMotionSamples is not { Count: > 1 }) return false;

        // State 3 snaps +0x80/+0x84 to the route point before setting the state-5 interpolation
        // anchor when its distance is below +0x90 (0.5, or 0.1 for special body modes).
        // This is not the 0.1 threshold used below to choose Practice scale 0.5.
        if (distance < snapDistance)
        {
            rootX = route.MotionTargetX;
            rootY = route.MotionTargetY;
            person.AnimatorRootX = rootX;
            person.AnimatorRootY = rootY;
        }

        // SAnimator retains the start and target directions (+0x1d0 and +0x1c4/1dc),
        // interpolating heading across practice playback (SAnimator.s 0x1034FCF8–0x1034FD10).
        var practiceDirection = currentDirection + headingError;
        var adjustedTarget = TurnRootTarget(route.MotionTargetX, route.MotionTargetY,
            MathF.Atan2(dy, dx), clip.StandardOffset);
        var offsetLength = MathF.Sqrt(clip.StandardOffset.X * clip.StandardOffset.X +
                                      clip.StandardOffset.Y * clip.StandardOffset.Y);
        var practiceScale = distance < 0.1f ? 0.5f : distance >= 1f ? 1f : distance / offsetLength;
        practiceScale = MathF.Max(0.5f, practiceScale);
        route = route with
        {
            MotionAnimationIndex = skill,
            MotionDirection = QuantizeRootDirection(targetDirection),
            MotionFrame = 0f,
            MotionPracticeFrame = 0f,
            MotionPlaybackScale = practiceScale,
            MotionX = rootX,
            MotionY = rootY,
            TurnRootOriginX = rootX,
            TurnRootOriginY = rootY,
            TurnRootTargetX = adjustedTarget.X,
            TurnRootTargetY = adjustedTarget.Y,
            TurnRootSavedDirection = targetDirection,
            TurnRootPracticeScale = practiceScale,
            TurnRootStartDirection = currentDirection,
            TurnRootPracticeDirection = practiceDirection,
        };
        person.NativeRoute = route;
        person.AnimationPlayback = null;
        ShowWalkSkill(person, clip, 0f, clip.StandardRootMotionSamples.Count);
        person.AnimatorControlState = 5;
        return true;
    }

    /// <summary>Starts Mac state 4's body-turn practice when the state-3 direction or distance gate requires it.
    /// Skill buckets, heading correction, endpoint fields, and +0x220 practice scale follow SAnimator.s
    /// 0x1034F938–0x1034FAE8; state 4 interpolates those endpoints at 0x1034FB04–0x1034FBEC.</summary>
    private static bool TryBeginRouteBodyTurn(SimulationWorld world, XPerson person, RouteState route,
        float rootX, float rootY, float targetDirection, float currentDirection, float headingError)
    {
        var (skill, correction) = BodyTurnSkillForHeadingError(headingError);

        var clip = ResolveWalkAnimation(world, person, skill);
        if (clip?.StandardRootMotionSamples is not { Count: > 1 } samples) return false;
        var practiceDirection = currentDirection + headingError - correction;
        // SAnimator::Tick stores +0x1bc/+0x1c0 = +0x80/+0x84 before entering state 4 from active polyline follow
        // (0058dc76.c:902-903, 353-360), turning the Sim in place rather than sliding root across the segment;
        // direct state-3 transition targets without an active polyline interpolate toward MotionTarget.
        var turnInPlace = route.AnimatorFollowState == 1 && route.MotionPath is { Count: > 1 };
        var turnTargetX = turnInPlace ? rootX : (float)route.MotionTargetX;
        var turnTargetY = turnInPlace ? rootY : (float)route.MotionTargetY;
        route = route with
        {
            MotionAnimationIndex = skill,
            MotionDirection = QuantizeRootDirection(targetDirection),
            MotionFrame = 0f,
            MotionPracticeFrame = 0f,
            MotionPlaybackScale = person.AnimatorWalkingScale,
            MotionX = rootX,
            MotionY = rootY,
            TurnRootOriginX = rootX,
            TurnRootOriginY = rootY,
            TurnRootTargetX = turnTargetX,
            TurnRootTargetY = turnTargetY,
            TurnRootSavedDirection = targetDirection,
            TurnRootPracticeScale = person.AnimatorWalkingScale,
            TurnRootStartDirection = currentDirection,
            TurnRootPracticeDirection = practiceDirection,
        };
        person.NativeRoute = route;
        person.AnimationPlayback = null;
        ShowWalkSkill(person, clip, 0f, samples.Count);
        person.AnimatorControlState = 4;
        return true;
    }

    /// <summary>Maps state 4's wrapped route-heading error to the Mac standing-body turn skill and correction
    /// (SAnimator.s 0x1034F938–0x1034FA58).</summary>
    internal static (short Skill, float Correction) BodyTurnSkillForHeadingError(float headingError)
    {
        if (headingError < -3f) return (5, -4f);
        if (headingError > 3f) return (4, 4f);
        if (headingError >= -0.5f && headingError <= 0.5f) return (10, 0f);
        if (headingError > 0.5f && headingError <= 1.5f) return (8, 1f);
        if (headingError > 1.5f && headingError <= 3f) return (6, 2f);
        if (headingError < -0.5f && headingError >= -1.5f) return (9, -1f);
        if (headingError < -1.5f && headingError >= -3f) return (7, -2f);
        return (10, 0f);
    }

    internal static short StandingTurnAnimationForVector(float deltaX, float deltaY)
    {
        var sector = (int)MathF.Floor(MathF.Atan2(deltaY, deltaX) * (4f / MathF.PI) + 0.5f);
        sector = (sector % 8 + 8) % 8;
        return sector switch
        {
            0 => 15,
            1 => 13,
            2 => 11,
            3 => 12,
            4 => 14,
            5 => 16,
            6 => 18,
            _ => 17,
        };
    }

    /// <summary>Applies state 5's Practice phase interpolation from root anchor (+0x1c8/+0x1cc) to its
    /// StdAnimOffset-adjusted route target (+0x1bc/+0x1c0); Mac SAnimator.s 0x1034FCD0–0x1034FDB0.</summary>
    internal static void TickRouteTurnRootInterpolation(SimulationWorld world, XPerson person)
    {
        var controlState = person.AnimatorControlState;
        if (controlState is not (4 or 5) || person.NativeRoute is not { } route ||
            (controlState == 5 && (route.MotionAnimationIndex is < 11 or > 18)) ||
            route.TurnRootOriginX is not { } originX || route.TurnRootOriginY is not { } originY ||
            route.TurnRootTargetX is not { } targetX || route.TurnRootTargetY is not { } targetY)
            return;
        // States 4 and 5 clear +0x194 and +0x288 on every Tick, before checking primary Practice phase.
        person.AnimatorGaitEntryState = 0;
        person.AnimatorPoolTransitionState = 0;
        var turnClip = ResolveWalkAnimation(world, person, route.MotionAnimationIndex);
        var sampleCount = turnClip?.StandardRootMotionSamples?.Count ?? 0;
        if (sampleCount <= 0) return;
        var arrivalTurn = route.AnimatorFollowState == -1;
        if (arrivalTurn && !world.AvatarAnimationRootOnly && turnClip!.DurationMilliseconds > 0f)
        {
            // The arrival turn runs while follow state is -1, where FollowOneStep only reports busy (2), so the
            // route callback does not advance this practice. Mac advances the primary Practice from Tick
            // (Practice::Apply: +0x28 += elapsed / (Skill +0x1c / scale)); mirror that here using the same
            // elapsed-since-last-full-apply rule as the route callback.
            var elapsedMils = person.AnimationPlayback?.LastPracticeApplyMils is { } lastApplyMils
                ? unchecked(person.AnimatorMilliseconds - lastApplyMils)
                : AnimatorTickMilliseconds;
            var turnScale = route.TurnRootPracticeScale ?? person.AnimatorWalkingScale;
            var advanced = Math.Min(sampleCount,
                route.MotionPracticeFrame + sampleCount * elapsedMils * turnScale / turnClip.DurationMilliseconds);
            route = route with { MotionFrame = advanced, MotionPracticeFrame = advanced };
            person.NativeRoute = route;
            ShowWalkSkill(person, turnClip, advanced, sampleCount);
        }
        var phase = route.MotionPracticeFrame / sampleCount;
        if (phase >= 1f)
        {
            person.AnimatorRootX = targetX;
            person.AnimatorRootY = targetY;
            // State 5 calls StopCurAnim, restores the saved direction through SetDirection, then redispatches
            // the state-3 branch (SAnimator.s 0x1034FDB0–0x1034FE18).
            person.AnimationPlayback = null;
            person.AnimatorCurrentSkill = -1;
            person.AnimatorControlState = 3;
            if (route.TurnRootSavedDirection is { } savedDirection)
            {
                person.AnimatorRootDirection = savedDirection;
                person[ObjectData.Direction] = QuantizeRootDirection(savedDirection);
            }
            person.NativeRoute = route with
            {
                MotionX = targetX,
                MotionY = targetY,
                // StopCurAnim (0058dc76.c:490, 590) clears the turn skill on both active-follow and arrival turns
                // so Continue never replays standing-turn-180 on top of the committed TurnRootSavedDirection.
                MotionAnimationIndex = -1,
                MotionFrame = 0f,
                MotionPracticeFrame = 0f,
                TurnRootOriginX = null,
                TurnRootOriginY = null,
                TurnRootTargetX = null,
                TurnRootTargetY = null,
                TurnRootSavedDirection = null,
                TurnRootPracticeScale = null,
                TurnRootStartDirection = null,
                TurnRootPracticeDirection = null,
            };
            // States 4 and 5 break back into the state-3 dispatch loop in SAnimator::Tick (0058dc76.c:501, 610),
            // immediately entering gait when AnimatorFollowState is 1.
            if (route.AnimatorFollowState == 1)
                world.ApplyAnimatorRouteEntryGate(person, routeHeadingClamped: false);
            return;
        }
        var rootX = originX + (targetX - originX) * phase;
        var rootY = originY + (targetY - originY) * phase;
        person.AnimatorRootX = rootX;
        person.AnimatorRootY = rootY;
        if (controlState is 4 or 5 && route.TurnRootStartDirection is { } startDirection &&
            route.TurnRootPracticeDirection is { } practiceDirection)
        {
            var heading = startDirection + (practiceDirection - startDirection) * phase;
            person.AnimatorRootDirection = heading;
            person[ObjectData.Direction] = QuantizeRootDirection(heading);
        }
        person.NativeRoute = route with { MotionX = rootX, MotionY = rootY };
    }

    internal readonly record struct RouteLookahead(RouteState Route, int ActiveSegmentIndex, float TargetX,
        float TargetY, bool ReachedSegmentEnd, bool AdvancedCursor);

    /// <summary>Selects SAnimator::Tick's route lookahead sample for the given root, advancing the
    /// TileList cursor (+0x178) across reached segment ends (SAnimator.s 0x103504A8–0x10350BF4).</summary>
    internal static RouteLookahead ResolveRouteLookahead(SimulationWorld world, XPerson person, RouteState route,
        float rootX, float rootY)
    {
        var motionPath = route.MotionPath;
        var activeTargetIndex = route.MotionTargetPathIndex;
        if (motionPath is { Count: > 1 } && (activeTargetIndex < 1 || activeTargetIndex >= motionPath.Count))
        {
            // Recover older transient route states from their active target coordinates.
            for (var index = 1; index < motionPath.Count; index++)
            {
                if (motionPath[index].X != route.MotionTargetX || motionPath[index].Y != route.MotionTargetY) continue;
                activeTargetIndex = index;
                break;
            }
        }
        var activeSegmentIndex = motionPath is { Count: > 1 } && activeTargetIndex >= 1 &&
                                 activeTargetIndex < motionPath.Count
            ? activeTargetIndex - 1
            : -1;

        // Mac SAnimator::Tick projects the floating root onto its active TileList segment (+0x1e4),
        // then samples that segment in one-unit increments until the first rounded route point lies
        // outside the squared half-stride radius (+0x1f4/+0x1f8). StartVitaBoy derives both radii
        // from StdAnimDist(walk loop 0x14 for +0x1f8, run loop 0x15 for +0x1f4) * 0.5 * 16/3
        // (SAnimator.s 0x10358C24–0x10358C70; selected at 0x10350B70).
        // Recompute the projection from the saved root each tick; this keeps it correct across saves.
        var targetX = (float)route.MotionTargetX;
        var targetY = (float)route.MotionTargetY;
        var progressReachedSegmentEnd = false;
        var advancedProgressCursor = false;
        if (motionPath is { } progressPath && activeSegmentIndex >= 0)
        {
            var strideSkill = ResolveWalkAnimation(world, person, person[ObjectData.WalkStyle] == 1 ? 21 : 20);
            var halfStride = (strideSkill?.StandardDistance ?? 0f) * (8f / 3f);
            var progressTarget = FindRouteProgressTarget(progressPath[activeSegmentIndex], progressPath[activeTargetIndex],
                rootX, rootY, halfStride);
            while (progressTarget.ReachedSegmentEnd && activeTargetIndex + 1 < progressPath.Count)
            {
                // SAnimator advances +0x178 when its lookahead sample reaches the segment endpoint, then
                // branches back to lbl_103504A8 and samples the next segment in the same Tick
                // (SAnimator.s 0x10350BCC–0x10350BF4), before projecting the candidate placement.
                activeTargetIndex++;
                activeSegmentIndex++;
                advancedProgressCursor = true;
                var nextTarget = progressPath[activeTargetIndex];
                route = route with
                {
                    MotionTargetPathIndex = activeTargetIndex,
                    MotionTargetTileX = nextTarget.TileX,
                    MotionTargetTileY = nextTarget.TileY,
                    MotionTargetX = nextTarget.X,
                    MotionTargetY = nextTarget.Y,
                    MotionTargetIsDestination = nextTarget.IsDestination,
                };
                progressTarget = FindRouteProgressTarget(progressPath[activeSegmentIndex],
                    progressPath[activeTargetIndex], rootX, rootY, halfStride);
            }
            targetX = progressTarget.X;
            targetY = progressTarget.Y;
            progressReachedSegmentEnd = progressTarget.ReachedSegmentEnd;
        }

        return new RouteLookahead(route, activeSegmentIndex, targetX, targetY, progressReachedSegmentEnd,
            advancedProgressCursor);
    }

    /// <summary>Runs SAnimator::Tick's second route placement after FollowOneStep placed the current root.
    /// Mac computes the next root, truncates it with fctiwz, projects it through the route TileList, calls
    /// CanPlace/Place, and only then stores the floating root (+0x80/+0x84; 0x10350F4C–0x1035109C).</summary>
    internal static void TickRouteRootMovement(SimulationWorld world, XPerson person, TreeStack? stack)
    {
        // Mac reaches the route-distance block from the active follow state 3 and walk/run gait
        // states 9/10. Standing-adjust (6/11) and other states return through their own branches
        // (SAnimator.s control-state dispatch into 0x10350F4C).
        if (person.AnimatorControlState is not (3 or 9 or 10)) return;
        if (person.NativeRoute is not { MotionTargetX: not int.MinValue, MotionTargetY: not int.MinValue } route)
            return;
        // After the non-zero route-preference arrival, follow state -1 waits for the state-3 match gate,
        // whose follow state 2 is completed by FollowOneStep rather than another root step.
        if (route.TransitionTargetX is not null || route.AnimatorFollowState is -1 or 2) return;
        var rootX = person.AnimatorRootX ?? route.MotionX;
        var rootY = person.AnimatorRootY ?? route.MotionY;
        if (!float.IsFinite(rootX) || !float.IsFinite(rootY)) return;
        var clip = ResolveWalkAnimation(world, person, route.MotionAnimationIndex);
        if (clip is null || clip.DurationMilliseconds <= 0f || person.AnimatorWalkingScale <= 0f) return;

        var motionPath = route.MotionPath;
        var lookahead = ResolveRouteLookahead(world, person, route, rootX, rootY);
        route = lookahead.Route;
        var activeSegmentIndex = lookahead.ActiveSegmentIndex;
        var activeTargetIndex = activeSegmentIndex + 1;
        var targetX = lookahead.TargetX;
        var targetY = lookahead.TargetY;
        var progressReachedSegmentEnd = lookahead.ReachedSegmentEnd;
        var advancedProgressCursor = lookahead.AdvancedCursor;

        var step = RouteStepDistance(clip.StandardDistance, clip.DurationMilliseconds,
            person.AnimatorWalkingScale, AnimatorTickMilliseconds);
        float stepX, stepY;
        var arrival = false;
        if (person.AnimatorPoolTile && IsRoutePreferenceArrival(person, route, rootX, rootY))
        {
            // The pool variant (0x10350714–0x1035074C, then 0x1035115C) sets +0x280=1 and records the root and
            // heading at +0x1c8/+0x1cc/+0x1d0, and the terminal point at +0x1d4/+0x1bc and +0x1d8/+0x1c0.
            person.AnimatorRouteTransitionState = 1;
            if (person.AnimatorPoolTransitionState == 0)
            {
                // Zero +0x288: +0x188=3, +0x180=-1 (from +0x280=1), and +0x1bc/+0x1c0/+0x1c4 are rewritten to the
                // current root and heading before state 3 redispatches (0x10351168–0x103511E8). No step is taken.
                person.AnimatorControlState = 3;
                person.NativeRoute = route with
                {
                    AnimatorFollowState = -1,
                    TransitionTargetX = rootX,
                    TransitionTargetY = rootY,
                    MotionX = rootX,
                    MotionY = rootY,
                };
                return;
            }
            // Nonzero +0x288 starts skill 0x13 in state 7. Its terminal branch restores +0x1d4/+0x1d8/+0x1dc (the
            // terminal point, and the direction Tick computed toward the facing entry at 0x10350598–0x10350690)
            // and maps +0x280=1 to +0x180=-1; the state-3 gate then matches +0x1bc, the same point.
            world.StartPoolSwimExitPractice(person);
            person.NativeRoute = route with
            {
                PoolSwimExitPending = true,
                PoolSwimExitRootX = route.MotionTargetX,
                PoolSwimExitRootY = route.MotionTargetY,
                PoolSwimExitDirection = ArrivalHeading(route),
                TransitionTargetX = route.MotionTargetX,
                TransitionTargetY = route.MotionTargetY,
            };
            return;
        }
        if (IsRoutePreferenceArrival(person, route, rootX, rootY))
        {
            // SAnimator::Tick's non-zero route-preference arrival (0x10350750–0x103507AC): +0x280=1,
            // +0x1bc/+0x1c0 = current root, +0x188=3, +0x180=-1, +0x284=1. Without a primary practice it
            // redispatches state 3; otherwise it repeats the stored +0x28c/+0x290 step direction at this
            // Tick's speed (0x10350F4C–0x10350FFC) and runs the common placement test.
            person.AnimatorRouteTransitionState = 1;
            person.AnimatorLocomotionFadeGate = 1;
            person.AnimatorControlState = 3;
            route = route with
            {
                AnimatorFollowState = -1,
                TransitionTargetX = rootX,
                TransitionTargetY = rootY,
                MotionX = rootX,
                MotionY = rootY,
            };
            if (person.AnimationPlayback is null)
            {
                person.NativeRoute = route;
                return;
            }
            person.AnimatorPoolTransitionState = 1;
            var storedX = person.AnimatorProjectedRootStepX;
            var storedY = person.AnimatorProjectedRootStepY;
            var storedLength = MathF.Sqrt(storedX * storedX + storedY * storedY);
            // A zero stored step divides by the source's substitute constant, leaving no movement.
            stepX = storedLength > 1e-6f ? storedX * step / storedLength : 0f;
            stepY = storedLength > 1e-6f ? storedY * step / storedLength : 0f;
            arrival = true;
        }
        else
        {
            var deltaX = targetX - rootX;
            var deltaY = targetY - rootY;
            // A lookahead sample on the root itself ends the Tick before the step and placement test
            // (SAnimator.s 0x10350BF8–0x10350C14: |dx²+dy²| <= 1e-6 branches to lbl_10351244).
            if (MathF.Abs(deltaX * deltaX + deltaY * deltaY) <= 1e-6f)
            {
                person.NativeRoute = route;
                return;
            }
            var distance = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
            var finalDestinationSegment = route.MotionTargetIsDestination &&
                                          (motionPath is null || activeTargetIndex >= motionPath.Count - 1);
            var effectiveStep = finalDestinationSegment ? MathF.Min(step, distance) : step;
            stepX = distance > 0f ? deltaX * effectiveStep / distance : 0f;
            stepY = distance > 0f ? deltaY * effectiveStep / distance : 0f;
        }
        person.AnimatorProjectedRootStepX = stepX;
        person.AnimatorProjectedRootStepY = stepY;
        var nextRootX = rootX + stepX;
        var nextRootY = rootY + stepY;
        var tileX = TickRootIntegerCoordinate(nextRootX);
        var tileY = TickRootIntegerCoordinate(nextRootY);
        var nearest = motionPath is { } path && activeSegmentIndex >= 0
            ? FindNearestRouteSegment(path, tileX, tileY, activeSegmentIndex, activeSegmentIndex)
            : null;
        var nextPosition = nearest is { } point
            ? new ObjectPosition(point.X, point.Y, person.Position.Height)
            : new ObjectPosition(tileX, tileY, person.Position.Height);
        var canPlacePosition = nextPosition with { Height = nextPosition.Height == 0 ? 1 : nextPosition.Height };
        var anchor = route.AnchorObjectId != 0 ? world.ObjectById(route.AnchorObjectId) : null;
        var isSnapTile = route.Slot.SnapTargetSlot >= 0 && anchor is not null &&
            (canPlacePosition.X >> 4 == route.Destination.X >> 4 && canPlacePosition.Y >> 4 == route.Destination.Y >> 4);
        var stepAccepted = isSnapTile
            ? CanPlaceArrival(world, person, anchor, route.Slot, canPlacePosition, stack)
            : world.CanPlaceObject(person, canPlacePosition, stack);
        if (!stepAccepted)
        {
            if (!arrival && world.LastPlacementFailureCode == 0x0b &&
                person.Position.X != 0 && person.Position.Y != 0 &&
                world.CanPlaceObject(person, person.Position, stack))
            {
                nextPosition = person.Position;
            }
            else
            {
                person.NativeRoute = route with
                {
                    AnimatorFollowState = 3,
                    FollowPlacementRejected = true,
                    MotionX = rootX,
                    MotionY = rootY,
                };
                return;
            }
        }

        world.PlaceObject(person, nextPosition, stack);
        world.RefreshPersonRoomStateAfterMovement(person);
        person.AnimatorRootX = nextRootX;
        person.AnimatorRootY = nextRootY;
        var nextRoute = route with { MotionX = nextRootX, MotionY = nextRootY };
        if (arrival)
        {
            person.NativeRoute = nextRoute;
            return;
        }
        if (!advancedProgressCursor && motionPath is { } routePath && nearest is { SegmentIndex: >= 0 } &&
            activeTargetIndex >= 1 && activeTargetIndex < routePath.Count)
        {
            // Mac SAnimator's +0x178 cursor selects one TileList segment for its root projection and
            // advances by exactly one after the root passes that segment's endpoint
            // (0x103504A8–0x10350590, 0x10350AE4–0x10350BF0). Windows FUN_0045c4fa clears the actor
            // point vector; FUN_00482558 appends generated points; FUN_0046a1f7 emits retained node
            // points seed-to-target before a conditional midpoint from reverse-chain index 1. The
            // seed record's +4 is the last partition-cell index, so it retains the exact seed
            // coordinate; the target record's +4 is -1 and is filtered. The optional midpoint then
            // comes from the target side (SIMS.EXE 0x0045c4fa, 0x00482558, 0x0046a7c5, 0x0046a1f7).
            var start = routePath[activeTargetIndex - 1];
            var end = routePath[activeTargetIndex];
            var segmentX = end.X - start.X;
            var segmentY = end.Y - start.Y;
            var pastEndpoint = (nextRootX - end.X) * (double)segmentX +
                               (nextRootY - end.Y) * (double)segmentY >= 0d;
            var nextTargetIndex = activeTargetIndex + 1;
            if (pastEndpoint && nextTargetIndex < routePath.Count)
            {
                var target = routePath[nextTargetIndex];
                nextRoute = nextRoute with
                {
                    MotionTargetPathIndex = nextTargetIndex,
                    MotionTargetTileX = target.TileX,
                    MotionTargetTileY = target.TileY,
                    MotionTargetX = target.X,
                    MotionTargetY = target.Y,
                    MotionTargetIsDestination = target.IsDestination,
                };
            }
        }
        person.NativeRoute = nextRoute;
    }

    /// <summary>Tick's final-approach gate when person data 9 (route preference) is non-zero: it arrives once +0x280 is set or the terminal point is nearer than +0x68 times the walk/run
    /// multiplier, 3 FTile walking and 4.5 running (SAnimator.s 0x10350588–0x10350594, 0x10350694–0x10350710).
    /// Door portal functions and the pool idle set data 9 around their routes.</summary>
    internal static bool IsRoutePreferenceArrival(XPerson person, RouteState route, float rootX, float rootY)
    {
        if (person[ObjectData.RoutePreference] == 0) return false;
        if (route.MotionPath is { Count: > 1 } && !route.MotionTargetIsDestination) return false;
        if (person.AnimatorRouteTransitionState != 0) return true;
        var remaining = MathF.Sqrt(MathF.Pow(route.MotionTargetX - rootX, 2) + MathF.Pow(route.MotionTargetY - rootY, 2));
        return remaining < (person[ObjectData.WalkStyle] == 1 ? 4.5f : 3f);
    }

    private static (float X, float Y, bool ReachedSegmentEnd) FindRouteProgressTarget(RouteWaypoint start, RouteWaypoint end,
        float rootX, float rootY, float halfStride)
    {
        var segmentX = (double)end.X - start.X;
        var segmentY = (double)end.Y - start.Y;
        var lengthSquared = segmentX * segmentX + segmentY * segmentY;
        if (lengthSquared <= 0d) return (end.X, end.Y, true);

        var length = Math.Sqrt(lengthSquared);
        var progress = ((rootX - start.X) * segmentX + (rootY - start.Y) * segmentY) / lengthSquared;
        progress = Math.Clamp(progress, 0d, 1d);
        var sampleStep = 1d / length;
        var thresholdSquared = Math.Max(0f, halfStride) * Math.Max(0f, halfStride);
        var candidateX = (float)end.X;
        var candidateY = (float)end.Y;
        for (var sampleProgress = progress; ; sampleProgress = Math.Min(1d, sampleProgress + sampleStep))
        {
            candidateX = FTileIntegerCoordinate((float)(start.X + segmentX * sampleProgress));
            candidateY = FTileIntegerCoordinate((float)(start.Y + segmentY * sampleProgress));
            var offsetX = candidateX - rootX;
            var offsetY = candidateY - rootY;
            if (offsetX * offsetX + offsetY * offsetY >= thresholdSquared || sampleProgress >= 1d)
                return (candidateX, candidateY, sampleProgress >= 1d);
        }
    }

    internal static short DirectionToWaypoint((int X, int Y) from, (int X, int Y) to)
    {
        var deltaY = to.Y - from.Y;
        var deltaX = to.X - from.X;
        var absY = Math.Abs(deltaY);
        var absX = Math.Abs(deltaX);
        // Preserve FUN_00452129's strict/inclusive boundaries at both 2:1 slopes (cWorldPt[0] = Y, cWorldPt[1] = X).
        int direction;
        if (absX * 2 < absY)
        {
            direction = 4;
        }
        else
        {
            direction = absX <= absY * 2 ? 3 : 2;
        }
        if (deltaX < 0)
        {
            if (direction == 2) direction = 6;
            else if (direction == 3) direction = 5;
        }
        if (deltaY < 0)
        {
            if (direction == 3) direction = 1;
            else if (direction == 4) direction = 0;
            else if (direction == 5) direction = 7;
        }
        return (short)(direction & 7);
    }

    private static short QuantizeRootDirection(float direction) =>
        SimsEngine.Character.AvatarDirection.ToObjectDirection(direction);

    /// <summary>FUN_0045c92e runs FUN_00463a0f only while the route record holds no portal (+0x68 is 0): it keeps the
    /// Sim's Route ID, or allocates one, and resets every portal's cost for that ID. Later selections in the same route
    /// keep the used-portal costs.</summary>
    private static void InitializeRoutePortalCosts(SimulationWorld world, XPerson person, RouteState route)
    {
        if (route.OpenedPortalId != 0 || route.UsedPortalIds is { Length: > 0 }) return;
        world.InitializePortalRouteCosts(person);
    }

    private static void ClearRoute(XPerson person)
    {
        var completedRoute = person.NativeRoute;
        if (person.NativeRouteParents.Count == 0)
        {
            if (completedRoute is { LookTowardsFollow: false } && person[PersonData.MultipleRoutes] == 0)
                person[PersonData.RoutingComplete] = 1;
            person[ObjectData.RoutePreference] = 0;
            person.NativeRoute = null;
            return;
        }
        var last = person.NativeRouteParents.Count - 1;
        person.NativeRoute = person.NativeRouteParents[last];
        person.NativeRouteParents.RemoveAt(last);
    }

    /// <summary>Failure exits from the routing callback still pass through cXPerson's EndFollow path. EndFollow
    /// defers while SAnimator +0x180 is 1 or -1; the waiting behavior callback completes it after Tick advances the
    /// follow state. Once complete, drop only this route so a nested portal caller regains its parent route.</summary>
    private static PrimitiveResult EndFollowAndClearRoute(SimulationWorld world, XPerson person, RouteState route,
        StackFrame frame, TreeStack? stack = null, short failureReason = 1)
    {
        if (!route.LookTowardsFollow)
            person[PersonData.RouteResult] = failureReason;
        route = route with { EndFollowPending = true, EndFollowOnArrival = false, FailureReason = failureReason };
        person.NativeRoute = route;
        if (route.AnimatorFollowState is 1 or -1)
        {
            frame.Waiting = true;
            return PrimitiveResult.Stop;
        }

        CompleteEndFollow(world, person, route);
        // FUN_0045aafa returns true when FollowOneStep reports the completed result 0. The routed movement
        // callback propagates failure instead.
        if (route.LookTowardsFollow)
        {
            ClearRoute(person);
            frame.Waiting = false;
            return PrimitiveResult.True;
        }
        return FinishRouteFailure(world, person, route, frame, stack, failureReason);
    }

    private static PrimitiveResult FailRouteBeforeStart(XPerson person, XObject anchor, RoutingSlotRequest slot,
        StackFrame frame, TreeStack? stack, short failureReason)
    {
        person[PersonData.RouteResult] = failureReason;
        if ((slot.Flags & 0x4000) != 0 && stack is not null &&
            TryPushRouteFailureFeedback(person, anchor, frame, stack, failureReason, out var callerDepth))
        {
            if (person.NativeRoute is { } parent && !ReferenceEquals(parent.OwnerFrame, frame))
                person.NativeRouteParents.Add(parent);
            person.NativeRoute = new RouteState(anchor.Id, anchor.Position, slot,
                OwnerFrame: frame, AnimatorFollowState: 0,
                PendingFailureFeedbackDepth: callerDepth, FailureReason: failureReason)
            { AwaitingBeginFollow = true };
            frame.Waiting = true;
            return PrimitiveResult.Again;
        }
        return PrimitiveResult.False;
    }

    private static PrimitiveResult FinishRouteFailure(SimulationWorld world, XPerson person, RouteState route,
        StackFrame frame, TreeStack? stack, short failureReason)
    {
        person[PersonData.RouteResult] = failureReason;
        if (!route.LookTowardsFollow && !route.DirectPositionRequest && (route.Slot.Flags & 0x4000) != 0 &&
            stack is not null && world.ObjectById(route.AnchorObjectId) is { } anchor &&
            TryPushRouteFailureFeedback(person, anchor, frame, stack, failureReason, out var callerDepth))
        {
            person.NativeRoute = route with
            {
                PendingFailureFeedbackDepth = callerDepth,
                FailureReason = failureReason,
                EndFollowPending = false,
                AwaitingBeginFollow = true,
            };
            frame.Waiting = true;
            return PrimitiveResult.Again;
        }
        ClearRoute(person);
        frame.Waiting = false;
        return route.LookTowardsFollow ? PrimitiveResult.True : PrimitiveResult.False;
    }

    // FUN_0045aecf case 0x10 (0045aecf.c:383-399): when route slot flag 0x4000 is set, push the named tree
    // "Route failure feedback" with parameters [failureReason, blockerObjectId, 0, 0] and wait in state 0x11.
    private static bool TryPushRouteFailureFeedback(XPerson person, XObject anchor, StackFrame frame, TreeStack stack,
        short failureReason, out int callerDepth)
    {
        callerDepth = -1;
        ITreeTable? resolvedScope = null;
        short resolvedTreeId = 0;
        if (anchor.TypeRecord?.BehaviorScope is SimsEngine.Content.BehaviorScope anchorScope &&
            anchorScope.FindNamedTree("Route failure feedback") is var anchorTree && anchorTree != 0)
        {
            resolvedScope = anchorScope;
            resolvedTreeId = anchorTree;
        }
        else if (person.TypeRecord?.BehaviorScope is SimsEngine.Content.BehaviorScope personScope &&
                 personScope.FindNamedTree("Route failure feedback") is var personTree && personTree != 0)
        {
            resolvedScope = personScope;
            resolvedTreeId = personTree;
        }
        else if (frame.Table is SimsEngine.Content.BehaviorScope frameScope &&
                 frameScope.FindNamedTree("Route failure feedback") is var frameTree && frameTree != 0)
        {
            resolvedScope = frameScope;
            resolvedTreeId = frameTree;
        }

        if (resolvedScope is null || resolvedTreeId == 0)
            return false;

        callerDepth = stack.Frames.Count;
        var blockerId = person[ObjectData.PrimitiveResultId];
        return PersonInteractionStack.Push(person, anchor, stack, resolvedScope, resolvedTreeId, anchor.StackObjectKey,
            [failureReason, blockerId, 0, 0]);
    }

    private static void CompleteEndFollow(SimulationWorld world, XPerson person, RouteState route)
    {
        // FUN_0045aecf LAB_0045b4f7 (0045aecf.c:283): emit "end_route" when SAnimator::EndFollow completes
        // for a route that previously entered BeginFollow (state 0x0c).
        if (!route.LookTowardsFollow && !route.AwaitingBeginFollow)
            world.PlaySoundEvent?.Invoke("end_route", person.Id, false, false);
        // SAnimator::EndFollow's successful branch (Mac SAnimator.s lines 13887-13906):
        // clear follow state (0x180 = 0), reset waypoint cursor (0x178 = -1),
        // set control state +0x188 to 3, lock transition targets to current root,
        // and commit discrete heading to cXPerson + 0x4c.
        person.AnimatorControlState = 3;
        var rootX = float.IsFinite(route.MotionX) ? route.MotionX : person.Position.X;
        var rootY = float.IsFinite(route.MotionY) ? route.MotionY : person.Position.Y;
        var rootDir = person.AnimatorRootDirection ?? person[ObjectData.Direction];
        var discreteDirection = QuantizeRootDirection(rootDir);
        person[ObjectData.Direction] = discreteDirection;
        person.AnimatorRootDirection = discreteDirection;
        person.AnimatorRootX = rootX;
        person.AnimatorRootY = rootY;
        person.NativeRoute = route with
        {
            AnimatorFollowState = 0,
            EndFollowPending = false,
            MotionX = rootX,
            MotionY = rootY,
            TransitionTargetX = rootX,
            TransitionTargetY = rootY,
            MotionTargetPathIndex = -1,
        };
    }

    private static void Trace(SimulationWorld world, XPerson person, short anchorId, ObjectPosition destination,
        string phase, string? failure)
    {
        if (world.CaptureExecutionTrace)
            world.NativeRouteTrace.Add(new SimulationNativeRouteTrace(person.Id, anchorId, destination, phase, failure));
    }

    private static bool StartPortalFunction(SimulationWorld world, XPerson person, XObject portal, TreeStack stack,
        StackFrame caller, RouteState route, out string? failure)
    {
        // FUN_0045c92e initializes and reads function 0x0f from the selected portal's own type record.
        // FUN_00463804 follows linked members for route-cost propagation; that is a separate lookup.
        var treeId = SimulationWorld.FunctionTreeId(portal, 0x0f);
        if (treeId == 0)
        {
            failure = "selected portal has no function 0x0f";
            return false;
        }
        var scope = portal.TypeRecord?.BehaviorScope;
        if (scope is null)
        {
            failure = $"selected portal {portal.Id} has no behavior scope";
            return false;
        }
        var callerDepth = stack.Frames.Count;
        if (!PersonInteractionStack.Push(person, portal, stack, scope, treeId, portal.StackObjectKey))
        {
            failure = $"function tree {treeId} on selected portal {portal.Id} could not be pushed";
            return false;
        }
        failure = null;
        if (!route.AwaitingBeginFollow)
        {
            // End active locomotion before the portal function runs so portal BHAV AnimateSim primitives
            // (which require AnimatorControlState in 0..3; 0058950f.c) never stall on a gait/turn state.
            if (person.AnimationPlayback is { RouteDriven: true })
                world.StopCurrentAnimation(person);
            person.AnimatorControlState = 3;
            var rootX = float.IsFinite(route.MotionX) ? route.MotionX : person.Position.X;
            var rootY = float.IsFinite(route.MotionY) ? route.MotionY : person.Position.Y;
            var discreteDirection = QuantizeRootDirection(person.AnimatorRootDirection ?? person[ObjectData.Direction]);
            person[ObjectData.Direction] = discreteDirection;
            person.AnimatorRootDirection = discreteDirection;
            person.AnimatorRootX = rootX;
            person.AnimatorRootY = rootY;
            route = route with
            {
                AnimatorFollowState = 0,
                EndFollowPending = false,
                MotionAnimationIndex = -1,
                MotionFrame = 0f,
                MotionPracticeFrame = 0f,
                MotionX = rootX,
                MotionY = rootY,
            };
        }
        var routeCostFunctionObject = PortalFunction(world, portal)?.FunctionObject ?? portal;
        RecordPortalRouteCostAfterSelection(world, person, portal, routeCostFunctionObject);
        person.NativeRoute = route with
        {
            PendingPortalId = portal.Id,
            PendingPortalFunctionObjectId = routeCostFunctionObject.Id,
            PortalCallerDepth = callerDepth,
        };
        caller.Waiting = true;
        return true;
    }

    // Sims.exe FUN_0045aecf calls FUN_00463f5f on the previous route portal when the Sim reaches
    // another room transition; FUN_00463f5f writes the used-route state to it and FUN_00463804's part.
    internal static void MarkPortalRouteCostUsed(SimulationWorld world, short portalId, short functionObjectId,
        short routeId)
    {
        if (routeId <= 0) return;
        world.ObjectById(portalId)?.SetPortalRouteCost(routeId, 1101f);
        world.ObjectById(functionObjectId)?.SetPortalRouteCost(routeId, 1101f);
    }

    // FUN_00463804 resolves function 0x0f on cXPortal/cXMTObject chain members through +0x18c
    // before FUN_0045c92e dispatches the selected tree with the route portal as its stack object.
    internal static (XObject FunctionObject, ITreeTable? Scope, short TreeId)? PortalFunction(SimulationWorld world, XObject portal)
    {
        // FUN_00463804 checks linked members, not the selected manager-vector root itself. For
        // BuildModeType 1 it starts at +0x190 (or the root) and filters on OBJD +0x10 (InteractionGroup).
        var matchInteractionGroup = portal.Definition[(int)SimsEngine.Formats.ObjdField.BuildModeType] == 1;
        var visited = new HashSet<short>();
        var nextId = matchInteractionGroup
            ? portal.MultiTileMasterId == 0 ? portal.Id : portal.MultiTileMasterId
            : portal.MultiTileNextId != 0 ? portal.MultiTileNextId : portal.MultiTileMasterId;
        var firstId = nextId;
        while (nextId != 0 && visited.Add(nextId))
        {
            var current = world.ObjectById(nextId);
            if (current is null) return null;
            if (current.RuntimeClass is not (SimsEngine.Formats.ObjectClass.Portal or SimsEngine.Formats.ObjectClass.MultiTileObject))
                return null;
            if (matchInteractionGroup && (current.Id == portal.Id ||
                current.Definition[(int)SimsEngine.Formats.ObjdField.InteractionGroup] !=
                portal.Definition[(int)SimsEngine.Formats.ObjdField.InteractionGroup]))
            {
                nextId = current.MultiTileNextId;
                if (nextId == firstId) return null;
                continue;
            }
            // FUN_00463804 returns 0 once the walk reaches the portal itself.
            if (!matchInteractionGroup && current.Id == portal.Id) return null;
            var treeId = SimulationWorld.FunctionTreeId(current, 0x0f);
            if (treeId != 0)
                return (current, current.TypeRecord?.BehaviorScope, treeId);
            // Without the interaction-group filter each step takes +0x18c, falling back to +0x190 when that is empty.
            nextId = current.MultiTileNextId != 0 || matchInteractionGroup
                ? current.MultiTileNextId
                : current.MultiTileMasterId;
            if (nextId == firstId) return null;
        }
        return null;
    }

    /// <summary>FUN_00463f99 stores a first-use room rank (1001..1100) or marks an already-ranked portal used
    /// (1101), on the selected portal and its linked function-bearing part.</summary>
    internal static void RecordPortalRouteCostAfterSelection(SimulationWorld world, XPerson person, XObject portal,
        XObject functionObject)
    {
        var routeId = person[ObjectData.RouteId];
        if (routeId <= 0) return;
        var cost = portal.PortalRouteCostAt(routeId);
        if (cost is >= 1001f and <= 1100f)
        {
            portal.SetPortalRouteCost(routeId, 1101f);
            functionObject.SetPortalRouteCost(routeId, 1101f);
            return;
        }

        // FUN_00463f99 compares each portal's stored cXObject +0x84 room word. This can
        // differ from a room-map lookup after runtime wall changes.
        var room = portal.RoomWord84;
        var greatestRoomCost = 1001f;
        foreach (var candidate in Portals(world))
        {
            if (candidate.RoomWord84 != room) continue;
            var candidateCost = candidate.PortalRouteCostAt(routeId);
            if (candidateCost is >= 1001f and <= 1100f && greatestRoomCost <= candidateCost)
                greatestRoomCost = candidateCost;
        }
        if (cost != 1101f)
        {
            portal.SetPortalRouteCost(routeId, greatestRoomCost);
            functionObject.SetPortalRouteCost(routeId, greatestRoomCost);
        }
    }

    // FUN_00463dd9 ranks portal-manager entries whose room word (+0x84) matches the routing Sim's
    // current room. A portal recorded in the destination room is not a candidate for this crossing.
    internal static bool PortalIsEligibleFromCurrentRoom(ushort portalRoom, ushort currentRoom) =>
        portalRoom == currentRoom;

    private static XObject? PortalForStep(SimulationWorld world, XPerson person, XObject destination,
        ushort currentRoom, ushort destinationRoom, IReadOnlySet<short>? excludedPortalIds = null,
        ObjectPosition? destinationPosition = null) =>
        SelectPortalForDestination(world, person, destination, currentRoom, destinationRoom, excludedPortalIds,
            destinationPosition: destinationPosition);

    private static bool HasPortalForStep(SimulationWorld world, int level, ushort fromRoom, ushort toRoom,
        (int X, int Y) from, (int X, int Y) to) =>
        PortalCandidatesForStep(world, fromRoom, toRoom, from, to, level: level).Any();

    internal static IEnumerable<XObject> PortalCandidatesForStep(SimulationWorld world, ushort fromRoom,
        ushort toRoom, (int X, int Y) from, (int X, int Y) to, short? currentRoomWord = null, int? level = null)
    {
        // Sims.exe's live route portal vector only contains cXPortal instances with
        // function 0x0f (FUN_0044350d); a physical portal without that function cannot
        // authorize a room transition.
        if (Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y) != 1) return [];
        var verticalCrossing = from.X == to.X;
        var sourceCurrentRoom = currentRoomWord is { } roomWord && roomWord != -5
            ? unchecked((ushort)roomWord)
            : fromRoom;
        return Portals(world).Where(portal =>
        {
            if (level is { } requestedLevel && portal.Position.Height != requestedLevel) return false;
            var tile = (X: portal.Position.X >> 4, Y: portal.Position.Y >> 4);
            if (tile != from && tile != to) return false;
            // FUN_00463dd9 compares the portal's stored +0x84 room word with the routing Sim's +0x84 word.
            // Position-derived room lookup is only a fallback for hand-built objects before room reconciliation.
            var portalRoom = portal.RoomWord84 != -5
                ? unchecked((ushort)portal.RoomWord84)
                : RoomKeyAt(world, portal.Position);
            if (!PortalIsEligibleFromCurrentRoom(portalRoom, sourceCurrentRoom)) return false;
            return verticalCrossing ? portal[ObjectData.Direction] % 4 == 0 : portal[ObjectData.Direction] % 4 == 2;
        });
    }

    /// <summary>FUN_004341ed's portal/object transition cost. FUN_00433ef5 writes the position level to +0x110.</summary>
    internal static float PortalTransitionDistanceCost(XObject from, XObject to)
    {
        if (to.TypeRecord?.ObjectType == 7) return XObject.UnallocatedPortalRouteCost;
        return PortalTransitionDistanceCost(from, to.Position);
    }

    private static float PortalTransitionDistanceCost(XObject from, ObjectPosition to)
    {
        var dx = (float)from.Position.X - to.X;
        var dy = (float)from.Position.Y - to.Y;
        var dz = (to.Height - from.Position.Height) * 20f;
        return MathF.Sqrt((dx * dx + dy * dy) * (1f / 256f) + dz * dz);
    }

    /// <summary>FUN_00463a65 caches the shortest transition distances from a portal to every reachable portal,
    /// keyed by a Route ID stored on the source portal itself.</summary>
    internal static float ProjectPortalRouteDistance(SimulationWorld world, XObject origin, XObject target)
    {
        ProjectPortalRoutes(world, origin);
        var routeId = origin[ObjectData.RouteId];
        return routeId > 0 ? target.PortalRouteCostAt(routeId) : XObject.UnallocatedPortalRouteCost;
    }

    // FUN_00463a65 (this = origin, stack argument = target) relaxes the route heap with edge cost
    // FUN_004341ed(settled, candidate) + 1.0, then returns the target's cost at the origin's route id.
    private static void ProjectPortalRoutes(SimulationWorld world, XObject origin)
    {
        var routeId = origin[ObjectData.RouteId];
        if (routeId == 0) routeId = world.InitializePortalRouteCosts(origin);
        if (routeId <= 0) return;
        if (origin.PortalRouteCostAt(routeId) != XObject.UninitializedPortalRouteCost) return;

        var portals = Portals(world).ToArray();
        foreach (var portal in portals) portal.SetPortalRouteCost(routeId, 10000f);
        origin.SetPortalRouteCost(routeId, 0f);
        var settled = new HashSet<short>();
        while (true)
        {
            XObject? current = null;
            var currentCost = 10000f;
            foreach (var portal in portals)
            {
                if (settled.Contains(portal.Id)) continue;
                var cost = portal.PortalRouteCostAt(routeId);
                if (cost >= currentCost) continue;
                current = portal;
                currentCost = cost;
            }
            if (current is null) break;
            settled.Add(current.Id);
            var linkedFunction = PortalFunction(world, current)?.FunctionObject;
            foreach (var candidate in portals)
            {
                if (settled.Contains(candidate.Id) ||
                    (candidate.RoomWord84 != current.RoomWord84 && !ReferenceEquals(candidate, linkedFunction))) continue;
                var priorCost = candidate.PortalRouteCostAt(routeId);
                var projected = currentCost + PortalTransitionDistanceCost(current, candidate) + 1f; // FUN_00463a65 adds 1.0 per hop
                if (projected < priorCost)
                    candidate.SetPortalRouteCost(routeId, projected);
            }
        }
    }

    /// <summary>FUN_00463dd9 combines each current-room portal's Sim-to-portal distance, its per-origin portal
    /// projection to a portal in the destination room, and that destination portal's distance to the target.</summary>
    internal static XObject? SelectPortalForDestination(SimulationWorld world, XPerson person, XObject destination,
        ushort currentRoom, ushort destinationRoom, IReadOnlySet<short>? excludedPortalIds = null,
        bool recordTrace = true, ObjectPosition? destinationPosition = null)
    {
        var routeId = person[ObjectData.RouteId];
        if (routeId == 0) return null;
        var portals = Portals(world).ToArray();
        foreach (var sourcePortal in portals)
        {
            if (PortalRoomKey(sourcePortal) != currentRoom) continue;
            var bestCost = sourcePortal.PortalRouteCostAt(routeId);
            if (bestCost != XObject.UninitializedPortalRouteCost) continue;
            sourcePortal.SetPortalRouteCost(routeId, 10000f);
            var sourceDistance = PortalTransitionDistanceCost(person, sourcePortal);
            foreach (var destinationPortal in portals)
            {
                if (PortalRoomKey(destinationPortal) != destinationRoom) continue;
                // 0x463e8a/0x463ea4: FUN_00463a65 runs with the SOURCE portal as `this` and the destination portal as its
                // stack argument, returning the destination portal's cost; the second call runs from FUN_00463804(source).
                var projectedCost = ProjectPortalRouteDistance(world, sourcePortal, destinationPortal);
                if (projectedCost == 10000f) continue;
                var functionObject = PortalFunction(world, sourcePortal)?.FunctionObject ?? sourcePortal;
                if (ProjectPortalRouteDistance(world, functionObject, destinationPortal) > projectedCost) continue;
                var destinationDistance = destinationPosition is { } position
                    ? PortalTransitionDistanceCost(destinationPortal, position)
                    : PortalTransitionDistanceCost(destinationPortal, destination);
                var projected = MathF.Min(1000f, destinationDistance + projectedCost + sourceDistance);
                if (projected < sourcePortal.PortalRouteCostAt(routeId))
                    sourcePortal.SetPortalRouteCost(routeId, projected);
            }
        }

        var candidates = portals.Where(portal => PortalRoomKey(portal) == currentRoom &&
            (excludedPortalIds is null || !excludedPortalIds.Contains(portal.Id))).ToArray();
        var selected = SelectPreRankedPortal(candidates, routeId);
        if (recordTrace && world.CaptureExecutionTrace)
        {
            var details = string.Join(";", candidates.Select(portal =>
                $"{portal.Id}:{portal.TypeRecord?.Name ?? "?"}:room={PortalRoomKey(portal)}:cost={portal.PortalRouteCostAt(routeId)}:fn={PortalFunction(world, portal)?.FunctionObject.Id ?? 0}"));
            world.NativeRouteTrace.Add(new SimulationNativeRouteTrace(person.Id, destination.Id, destination.Position,
                "portal-ranking", $"route={routeId};source={currentRoom};destination={destinationRoom};selected={selected?.Id ?? 0};candidates={details}"));
        }
        return selected;
    }

    // FUN_00463dd9 compares the stored +0x84 words directly. A room-map lookup here would
    // turn the native -5 sentinel into a real room and admit portal candidates Sims.exe rejects.
    private static ushort PortalRoomKey(XObject item) => unchecked((ushort)item.RoomWord84);

    // FUN_00463dd9 excludes the projected 10000 and used-route 1101 costs. Its 10001 initialization
    // still requires the unresolved projection path, so preserve manager order for those candidates.
    internal static XObject? SelectPreRankedPortal(IReadOnlyList<XObject> candidates, short routeId)
    {
        if (candidates.Count == 0) return null;
        if (routeId == 0) return null;
        var eligible = candidates.Where(candidate => candidate.PortalRouteCostAt(routeId) is not (10000f or 1101f)).ToArray();
        if (eligible.Length == 0) return null;
        // FUN_00463dd9 excludes only 10000 and 1101, then replaces the current winner
        // only for a strictly lower cost. Unprojected 10001 values remain comparable
        // until FUN_00463a65 projection is implemented; equal costs keep first order.
        var selected = eligible[0];
        var selectedCost = selected.PortalRouteCostAt(routeId);
        for (var index = 1; index < eligible.Length; index++)
        {
            var candidate = eligible[index];
            var cost = candidate.PortalRouteCostAt(routeId);
            if (cost >= selectedCost) continue;
            selected = candidate;
            selectedCost = cost;
        }
        return selected;
    }

    // Sims.exe FUN_0045c4fa (0045c4fa.c:80-120) & Mac XRoute::SetDirectionForGoalSearch:
    // explicit Facing 0..7 always uses (anchorDirection + Facing) & 7 and Facing -3 preserves the person's
    // current direction, even when no radial direction bits (Flags & 0xff == 0) are set (e.g. GoToRelativePosition
    // exact-offset calls on stair.iff BHAV 4112 where Flags == 0x4000 and MinProximity == 16).
    // For Facing -1/-2 with (Flags & 0xff) == 0, FUN_0045c4fa (0045c4fa.c:94-105) faces toward/away from anchorOrigin
    // (or anchorDirection when destination == anchorOrigin, reversing for -1).
    internal static short RelativeArrivalFacing(SimulationWorld world, XPerson person, XObject anchor,
        RoutingSlotRequest slot, ObjectPosition destination, short? routeSearchDirection)
    {
        var anchorDirection = (slot.Flags & 0x200) != 0 ? (short)0 : anchor[ObjectData.Direction];
        if ((slot.Flags & 0xff) == 0)
        {
            if (slot.Facing is -1 or -2)
            {
                var exactAnchorOrigin = RouteAnchorOrigin(world, anchor, slot);
                var faceTowardAnchor = (destination.X == exactAnchorOrigin.X && destination.Y == exactAnchorOrigin.Y)
                    ? anchorDirection
                    : DirectionToWaypoint((destination.X, destination.Y), (exactAnchorOrigin.X, exactAnchorOrigin.Y));
                return slot.Facing == -1 ? (short)((faceTowardAnchor + 4) & 7) : faceTowardAnchor;
            }
            return slot.Facing switch
            {
                >= 0 and <= 7 => (short)((anchorDirection + slot.Facing) & 7),
                -3 => person[ObjectData.Direction],
                _ => (short)((anchorDirection + 4) & 7),
            };
        }

        var anchorOrigin = RouteAnchorOrigin(world, anchor, slot);
        var center = LotGeometry.SlotPosition(anchorOrigin, anchorDirection, slot.X, slot.Y);
        var side = DirectionToWaypoint((center.X, center.Y), (destination.X, destination.Y));
        var searchDirection = routeSearchDirection ?? side;

        return slot.Facing switch
        {
            -1 => searchDirection,
            -2 => (destination.X == anchorOrigin.X && destination.Y == anchorOrigin.Y)
                ? (short)((anchorDirection + 4) & 7)
                : DirectionToWaypoint((destination.X, destination.Y), (anchorOrigin.X, anchorOrigin.Y)),
            -3 => person[ObjectData.Direction],
            _ => (short)((anchorDirection + slot.Facing) & 7),
        };
    }

    // Mac XRoute::ConstructGoals uses GetAverageLocation when RoutingSlot flags contain 0x10000.
    // GetAverageLocation walks the multi-tile lead/next chain and truncates each coordinate's
    // integer average toward zero; ordinary objects without a linked chain keep their own location.
    private static ObjectPosition RouteAnchorOrigin(SimulationWorld world, XObject anchor, RoutingSlotRequest slot)
    {
        if ((slot.Flags & 0x00010000) == 0) return anchor.Position;

        var lead = anchor.MultiTileMasterId == 0
            ? anchor
            : world.ObjectById(anchor.MultiTileMasterId) ?? anchor;
        long sumX = 0;
        long sumY = 0;
        var count = 0;
        var visited = new HashSet<short>();
        for (var member = lead; member is not null && visited.Add(member.Id);
             member = member.MultiTileNextId == 0 ? null : world.ObjectById(member.MultiTileNextId))
        {
            sumX += member.Position.X;
            sumY += member.Position.Y;
            count++;
        }
        return count == 0
            ? anchor.Position
            : new ObjectPosition(unchecked((int)(sumX / count)), unchecked((int)(sumY / count)), anchor.Position.Height);
    }

    // FUN_004549b6 passes uVar14 to FUN_004550e0 as its temporary wall-probe direction.
    // Ordinarily it is the Sim's current direction; slots with flag 0x1000 instead select
    // the first authored approach bit (0 through 6) relative to the anchor's direction.
    private static short CandidateValidationDirection(XPerson person, XObject anchor, RoutingSlotRequest slot)
    {
        if ((slot.Flags & 0x1000) == 0) return person[ObjectData.Direction];
        var direction = 0;
        while (direction < 7 && (slot.Flags & (1u << direction)) == 0) direction++;
        return (short)((anchor[ObjectData.Direction] + direction) & 7);
    }

    internal static SelectedRouteGoal? SelectDestination(SimulationWorld world, XPerson person, XObject anchor,
        RoutingSlotRequest slot, TreeStack? stack = null)
    {
        var orientation = (slot.Flags & 0x0200) != 0 ? (short)0 : anchor[ObjectData.Direction];
        var center = LotGeometry.SlotPosition(RouteAnchorOrigin(world, anchor, slot), orientation, slot.X, slot.Y);
        // FUN_00483572's zero-direction branch preserves the authored point. Snap slots also
        // keep the reference object's position so their placement callback can bind the slot.
        if ((slot.Flags & 0xff) == 0 || slot.SnapTargetSlot >= 0)
        {
            var exact = slot.SnapTargetSlot >= 0 ? anchor.Position : center;
            var accepted = Walkable(world, person, anchor, exact, slot, stack: stack);
            TraceCandidate(world, person, anchor, exact, accepted, slot.Flags);
            return accepted ? new SelectedRouteGoal(exact, orientation) : null;
        }

        var candidates = AuthoredRouteSlotCandidates(world, person, anchor, center, slot, orientation);
        var capacity = AuthoredRouteCandidateCapacity(world, person);
        var placeable = new List<RouteGoalCandidate>(capacity);
        foreach (var candidate in candidates)
        {
            if (!Walkable(world, person, anchor, candidate.Position, slot, stack: stack,
                    candidateSector: candidate.SearchDirection))
            {
                TraceCandidate(world, person, anchor, candidate.Position, false, slot.Flags);
                continue;
            }

            // FUN_00483572 stores validated candidates in a bounded pool; FUN_004817ed
            // initializes that pool to 48 entries and FUN_004824b8 lowers it to 8 for
            // large current rooms. It evaluates the full authored candidate scan before
            // reducing an overfull pool, so do not stop at the first accepted points.
            placeable.Add(candidate);
            TraceCandidate(world, person, anchor, candidate.Position, true, slot.Flags);
        }

        // FUN_00483572 downsamples an overfull candidate pool with the Sims.exe RNG. It
        // draws an index into the original pool and takes the first still-present entry
        // at or after it (the source's scan does not wrap). Keep the draw order for stable
        // tie-breaking after the route planner evaluates the selected goals.
        var routeCandidates = SampleRouteCandidates(world, placeable, capacity);
        RouteGoalCandidate? bestGoal = null;
        RouteGoalCandidate? bestCrossFloorGoal = null;
        var bestPathCost = double.PositiveInfinity;
        var bestCrossFloorRouteCost = double.PositiveInfinity;
        foreach (var candidate in routeCandidates)
        {
            // FUN_0045aecf resolves room transitions through portal function 0x0f before
            // walking to the endpoint. A different floor has a different room-key band;
            // do not reject its endpoint by asking the current-floor tile pathfinder to
            // connect directly to it. The stair portal callback owns that transition. Keep
            // scanning the sampled pool: returning its first cross-floor point ignored the
            // score that FUN_00483572 calculated for each accepted endpoint.
            if (candidate.Position.Height != person.Position.Height)
            {
                var currentRoom = unchecked((ushort)person.RoomWord84);
                // FUN_0045c92e passes the route record's target object to FUN_00463dd9.
                // Portal ranking therefore uses the target object's room and position; the
                // sampled arrival point is a later walking goal within that target room.
                var destinationRoom = PortalRoomKey(anchor);
                if (currentRoom == SimsEngine.World.RoomMap.NoRoom ||
                    destinationRoom == SimsEngine.World.RoomMap.NoRoom) continue;
                var (selectedPortal, portalRouteCost) = PreviewPortalForDestination(world, person, anchor,
                    currentRoom, destinationRoom);
                // A placeable tile on another floor is not a routable goal when the native
                // portal manager cannot select a transition to its room. Do not retain an
                // infinite-cost candidate as a fallback; the live route manager would fail
                // before it could walk to that endpoint.
                if (selectedPortal is null) continue;
                if (portalRouteCost < bestCrossFloorRouteCost ||
                    (portalRouteCost == bestCrossFloorRouteCost &&
                     (bestCrossFloorGoal is null || candidate.Cost > bestCrossFloorGoal.Value.Cost)))
                {
                    bestCrossFloorGoal = candidate;
                    bestCrossFloorRouteCost = portalRouteCost;
                }
                continue;
            }
            if (TryPath(world, person, candidate.Position, anchor, out _, out var pathCost, directPositionRequest: false,
                    directSlot: slot, stack: stack))
            {
                if (pathCost < bestPathCost ||
                    (pathCost == bestPathCost && (bestGoal is null || candidate.Cost > bestGoal.Value.Cost)))
                {
                    bestGoal = candidate;
                    bestPathCost = pathCost;
                }
            }
        }

        if (bestGoal is { } selectedGoal)
            return new SelectedRouteGoal(selectedGoal.Position, selectedGoal.SearchDirection,
                selectedGoal.EntryFlags, selectedGoal.ChairObjectId);
        if (bestCrossFloorGoal is { } selectedCrossFloorGoal)
            return new SelectedRouteGoal(selectedCrossFloorGoal.Position, selectedCrossFloorGoal.SearchDirection,
                selectedCrossFloorGoal.EntryFlags, selectedCrossFloorGoal.ChairObjectId);
        if (placeable.Count != 0) world.LastRouteCandidateFailureReason = "authored candidates have no route path";
        return null;
    }

    /// <summary>Runs the portal manager's target-room ranking for candidate selection without committing its
    /// lazy Route IDs or distance caches. Sims.exe initializes those values later, when the live route manager
    /// enters state 2 and calls FUN_0045c92e.</summary>
    private static (XObject? Portal, float Cost) PreviewPortalForDestination(SimulationWorld world,
        XPerson person, XObject destination, ushort currentRoom, ushort destinationRoom)
    {
        var objects = world.WalkOrder.ToArray();
        var routeIds = objects.ToDictionary(item => item, item => item[ObjectData.RouteId]);
        var routeCosts = objects.ToDictionary(item => item, item => item.PortalRouteCosts.ToArray());
        try
        {
            var routeId = person[ObjectData.RouteId];
            if (routeId == 0) routeId = world.InitializePortalRouteCosts(person);
            if (routeId <= 0) return (null, float.PositiveInfinity);

            foreach (var portal in Portals(world))
                portal.SetPortalRouteCost(routeId, XObject.UninitializedPortalRouteCost);
            var selected = SelectPortalForDestination(world, person, destination, currentRoom,
                destinationRoom, recordTrace: false);
            return (selected, selected?.PortalRouteCostAt(routeId) ?? float.PositiveInfinity);
        }
        finally
        {
            foreach (var item in objects)
            {
                item[ObjectData.RouteId] = routeIds[item];
                item.PortalRouteCosts.Clear();
                foreach (var (routeId, cost) in routeCosts[item]) item.PortalRouteCosts.Add(routeId, cost);
            }
        }
    }

    private static IReadOnlyList<RouteGoalCandidate> SampleRouteCandidates(
        SimulationWorld world, IReadOnlyList<RouteGoalCandidate> candidates, int capacity)
    {
        if (candidates.Count <= capacity) return candidates;

        var selected = new List<RouteGoalCandidate>(capacity);
        var consumed = new bool[candidates.Count];
        for (var attempt = 0; attempt < capacity; attempt++)
        {
            var index = (int)(world.NextRandom() % (uint)candidates.Count);
            while (index < candidates.Count && consumed[index]) index++;
            if (index == candidates.Count) continue;
            consumed[index] = true;
            selected.Add(candidates[index]);
        }
        return selected;
    }

    internal static int AuthoredRouteCandidateCapacity(SimulationWorld world, XPerson person)
    {
        // FUN_004824b8 calls FUN_004681ec on the current room and switches from 48 to 8
        // when that room's +0x38-derived half-area exceeds 250. FUN_00467c93 contributes
        // two per ordinary tile and one for a diagonal-wall tile.
        var position = person.Position;
        if (world.HasRoomRecord(person.RoomWord84) && world.LotRoomMaps.TryGetValue(position.Height, out var map))
        {
            var room = unchecked((ushort)person.RoomWord84);
            if (map.RoomHalfArea(room, world.RoomIsOutside(person.RoomWord84)) > 250) return 8;
        }
        return 48;
    }

    // Source authority: analysis/Sims.original.exe (SHA-256 6e598c22512a24659cbd0e533306abf3129733d67b1d120916b1b6b1581e29f2),
    // opcode 0x2d dispatch FUN_0045cd1f -> FUN_0045ad3a -> FUN_0045aecf -> FUN_004824b8 -> FUN_00483572.
    // The planner samples each enabled local direction from the rounded -min/res bound
    // through -max/res, scans each x row from y to -y, rotates it by anchor facing + sector,
    // and scores accepted points in insertion order. Keeping this projection here prevents
    // an exact-center fallback from silently routing outside the authored SLOT region.
    private static List<RouteGoalCandidate> AuthoredRouteSlotCandidates(
        SimulationWorld world, XPerson person, XObject anchor, ObjectPosition center, RoutingSlotRequest slot,
        short orientation)
    {
        var results = new List<RouteGoalCandidate>();
        if (slot.MinProximity is < 1 or > 512 || slot.MaxProximity is < 1 or > 512 ||
            slot.MinProximity > slot.OptimalProximity || slot.OptimalProximity > slot.MaxProximity ||
            slot.Resolution <= 0)
        {
            world.LastRouteCandidateFailureReason = "invalid authored route-slot range or resolution";
            return results;
        }

        var petActor = person[PersonData.Gender] > 1;
        // FUN_004824b8 (004824b8.c:43-49) and FUN_0045c4fa (0045c4fa.c:29-31): pets (>1) cannot sit in Sim chairs
        // and fall back to Standing=1 when Sitting!=0; Sims (<2) evaluate Sitting*10 alongside max(Standing,Ground)*10.
        var sittingScore = !petActor && slot.Sitting > 0 ? slot.Sitting * 10d : 0d;
        var effectiveStanding = petActor && slot.Sitting > 0 && slot.Standing == 0 ? 1 : slot.Standing;
        var floorPreference = Math.Max(effectiveStanding, slot.Ground) * 10d;
        // Keep hand-constructed zero-posture test requests standing-eligible while suppressing standing goals on
        // real sit-only SLOT definitions (Sitting > 0 && Standing == 0 && Ground == 0, 00483572.c:225-234).
        var allowStanding = floorPreference > 0d || sittingScore <= 0d;
        var resolution = sittingScore > 0d ? 16 : slot.Resolution;

        var directionMask = slot.Flags & 0xff;
        // FUN_00483572 converts both negative proximity/resolution ratios with __ftol, then widens
        // [maxY..minY] by 1 tile when sittingScore > 0 (00483572.c:177-183) so side/back chair entries are scanned.
        var minY = (int)Math.Round(-(double)slot.MinProximity / resolution, MidpointRounding.ToEven);
        var maxY = (int)Math.Round(-(double)slot.MaxProximity / resolution, MidpointRounding.ToEven);
        if (sittingScore > 0d)
        {
            maxY -= 1;
            minY = Math.Min(-1, minY + 1);
        }
        var radiusMinimumSquared = (long)slot.MinProximity * slot.MinProximity;
        var radiusMaximumSquared = (long)slot.MaxProximity * slot.MaxProximity;
        var wallMask = 0u;
        if ((slot.Flags & 0x0800) == 0 && (slot.Flags & 0x0400) == 0 && world.LotRoomMaps.TryGetValue(center.Height, out var roomMap))
        {
            // RouteMaskAt returns world-direction bits. FUN_00435a8f rotates the wall
            // sectors back by the anchor orientation before FUN_00483572 compares them
            // with the slot's local direction bits.
            // FUN_00483572 obtains this mask from the referenced anchor's +0xfc route
            // mask field, before transforming the sampled SLOT offsets into world space.
            var worldWallMask = RouteMaskAt(world, roomMap, anchor.Position.X, anchor.Position.Y);
            var rotation = orientation & 7;
            wallMask = rotation == 0
                ? worldWallMask
                : ((worldWallMask >> rotation) | (worldWallMask << (8 - rotation))) & 0xff;
        }

        double ScoreCandidate(long distanceSquared, double posturePreference, int wallSector, ObjectPosition bonusTarget)
        {
            var radius = Math.Sqrt(distanceSquared);
            // FUN_00483572 converts both the sampled radius and its gradient penalty
            // through MSVC __ftol (x87 round-to-nearest, ties-to-even), not truncation.
            var roundedRadius = Math.Round(radius, MidpointRounding.ToEven);
            var gradientPenalty = Math.Round(
                Math.Abs(roundedRadius - slot.OptimalProximity) * slot.Gradient,
                MidpointRounding.ToEven);
            var score = 100d + posturePreference - gradientPenalty;
            // FUN_00483572 consumes the shared Sims.exe RNG for each in-range
            // endpoint when request flag 0x2000 is set, before placement filtering.
            if ((slot.Flags & 0x2000) != 0) score += -10 + (int)(world.NextRandom() % 20);
            var wallSectorAndNeighbors = (1u << wallSector) |
                (1u << ((wallSector + 1) & 7)) | (1u << ((wallSector + 7) & 7));
            if ((wallMask & wallSectorAndNeighbors) == 0) score += 25;
            if (bonusTarget.X == person.Position.X && bonusTarget.Y == person.Position.Y &&
                bonusTarget.Height == person.Position.Height) score += 20;
            return score;
        }

        for (var localSector = 0; localSector < 8; localSector++)
        {
            if ((directionMask & (1u << localSector)) == 0) continue;
            if ((wallMask & (1u << localSector)) != 0) continue;
            var worldSector = (orientation + localSector) & 7;
            var expectedChairDirection = (worldSector + 4) & 7;
            var candidateDirectionBit = 1u << ((localSector + 2) & 7);
            for (var localY = minY; localY >= maxY; localY--)
            {
                // FUN_00483572 starts each row at localY and derives its inclusive upper-X
                // bound from -localY. If the next sector is enabled, it decrements that upper
                // bound once so the shared boundary candidate belongs to only one sector.
                var startX = localY;
                var endX = -localY - ((directionMask & candidateDirectionBit) != 0 ? 1 : 0);
                for (var localX = startX; localX <= endX; localX++)
                {
                    var offsetX = localX * resolution;
                    var offsetY = localY * resolution;
                    var standPos = LotGeometry.SlotPosition(center, (short)worldSector, offsetX, offsetY);

                    bool TryEmitStanding()
                    {
                        if (!allowStanding) return false;
                        var radiusSquared = (long)offsetX * offsetX + (long)offsetY * offsetY;
                        if (radiusSquared < radiusMinimumSquared || radiusSquared > radiusMaximumSquared) return false;
                        var score = ScoreCandidate(radiusSquared, floorPreference, localSector, standPos);
                        results.Add(new RouteGoalCandidate(standPos, score, (short)worldSector,
                            LocalSectorEntryFlags((short)worldSector, orientation)));
                        return true;
                    }

                    bool TryEmitChair()
                    {
                        if (sittingScore <= 0d) return false;
                        for (var chairSide = 0; chairSide < 8; chairSide++)
                        {
                            var step = LotGeometry.FootprintOffset((short)chairSide);
                            var chairLocalX = offsetX + step.X;
                            var chairLocalY = offsetY + step.Y;
                            var chairTileLocalX = chairLocalX >> 4;
                            var chairTileLocalY = chairLocalY >> 4;
                            var inCone = chairTileLocalY == localY
                                ? localY <= chairTileLocalX && chairTileLocalX <= endX
                                : chairTileLocalY <= localY || (localY < chairTileLocalX && chairTileLocalX < endX);
                            if (!inCone) continue;

                            var chairPos = LotGeometry.SlotPosition(center, (short)worldSector, chairLocalX, chairLocalY);
                            var chairTileX = chairPos.X >> 4;
                            var chairTileY = chairPos.Y >> 4;
                            if (chairTileX <= 0 || chairTileY <= 0 ||
                                chairTileX >= LotGeometry.LotDimension - 1 || chairTileY >= LotGeometry.LotDimension - 1)
                                continue;

                            var chair = FindChairAtTile(world, chairTileX, chairTileY, anchor.Position.Height);
                            if (chair is null || (chair[ObjectData.Direction] & 7) != expectedChairDirection) continue;
                            var chairEntryMask = unchecked((ushort)chair.Definition[(int)Formats.ObjdField.ChairEntryFlags]);
                            var entryBit = (short)(1 << chairSide);
                            if ((chairEntryMask & (ushort)entryBit) == 0) continue;

                            if (chair.FootprintSlots.Count > 0)
                            {
                                var occupantKey = chair.FootprintSlots[0].OccupantObjectId;
                                if (occupantKey > 0 && occupantKey != person.ClockManagerIndex &&
                                    world.ObjectTableEntry(occupantKey) is { } occupant && !ReferenceEquals(occupant, person))
                                {
                                    person[ObjectData.PrimitiveResultId] = occupant.ClockManagerIndex;
                                    continue;
                                }
                            }

                            var chairRadiusSquared = (long)chairLocalX * chairLocalX + (long)chairLocalY * chairLocalY;
                            if (chairRadiusSquared < radiusMinimumSquared || chairRadiusSquared > radiusMaximumSquared)
                                continue;

                            var score = ScoreCandidate(chairRadiusSquared, sittingScore, localSector, chairPos);
                            results.Add(new RouteGoalCandidate(standPos, score, (short)worldSector,
                                EntryFlags: entryBit, ChairObjectId: chair.Id));
                            return true;
                        }
                        return false;
                    }

                    // FUN_00483572 (00483572.c:224-230, 301): standing precedes chairs when floorPreference >= sittingScore;
                    // chairs precede standing when floorPreference < sittingScore; the first match at (localX,localY) wins.
                    if (floorPreference >= sittingScore)
                    {
                        if (!TryEmitStanding()) TryEmitChair();
                    }
                    else
                    {
                        if (!TryEmitChair()) TryEmitStanding();
                    }
                }
            }
        }

        // FUN_00483572 appends valid candidates to the bounded vector in sector/row/column scan order.
        // FUN_00460a05 and the following reduction sample that vector without sorting it by score.
        // Preserve this order so the shared RNG selects the same candidate subset as Sims.exe.
        return results;
    }

    private static XObject? FindChairAtTile(SimulationWorld world, int tileX, int tileY, int level)
    {
        var effectiveLevel = level == 0 ? 1 : level;
        try
        {
            foreach (var candidate in world.ObjectsAtTile(tileX, tileY, effectiveLevel))
            {
                if (candidate is XPerson) continue;
                if (candidate[ObjectData.ParentId] != 0 && candidate[ObjectData.SlotHierNumber] != 0) continue;
                var flags = unchecked((ushort)candidate[ObjectData.Flags]);
                var skip = (flags & 4) == 0 ? (flags & 0x10) != 0 : (flags & 2) == 0;
                if (skip) continue;
                return SimulationWorld.HasFunction(candidate, 0x1a) ? candidate : null;
            }
        }
        catch (InvalidDataException)
        {
        }
        return null;
    }

    private static bool IsSeatedChairMatchingSlot(SimulationWorld world, XPerson person, XObject anchor,
        RoutingSlotRequest slot, XObject seat, out short matchedWorldSector)
    {
        matchedWorldSector = seat[ObjectData.Direction];
        if (slot.Sitting <= 0 || person[PersonData.Gender] >= 2 || (slot.Flags & 0xff) == 0 || slot.SnapTargetSlot >= 0)
            return false;
        if (slot.MinProximity is < 1 or > 512 || slot.MaxProximity is < 1 or > 512) return false;
        var orientation = (slot.Flags & 0x0200) != 0 ? (short)0 : anchor[ObjectData.Direction];
        var center = LotGeometry.SlotPosition(RouteAnchorOrigin(world, anchor, slot), orientation, slot.X, slot.Y);
        var minR2 = (long)slot.MinProximity * slot.MinProximity;
        var maxR2 = (long)slot.MaxProximity * slot.MaxProximity;
        var minY = Math.Min(-1, (int)Math.Round(-(double)slot.MinProximity / 16d, MidpointRounding.ToEven));
        var maxY = (int)Math.Round(-(double)slot.MaxProximity / 16d, MidpointRounding.ToEven);
        var directionMask = slot.Flags & 0xff;
        for (var localSector = 0; localSector < 8; localSector++)
        {
            if ((directionMask & (1u << localSector)) == 0) continue;
            var worldSector = (short)((orientation + localSector) & 7);
            if ((seat[ObjectData.Direction] & 7) != ((worldSector + 4) & 7)) continue;
            var candidateDirectionBit = 1u << ((localSector + 2) & 7);
            for (var localY = minY; localY >= maxY; localY--)
            {
                var endX = -localY - ((directionMask & candidateDirectionBit) != 0 ? 1 : 0);
                for (var localX = localY; localX <= endX; localX++)
                {
                    var ox = localX * 16;
                    var oy = localY * 16;
                    var r2 = (long)ox * ox + (long)oy * oy;
                    if (r2 < minR2 || r2 > maxR2) continue;
                    var projected = LotGeometry.SlotPosition(center, worldSector, ox, oy);
                    if ((projected.X >> 4) == (seat.Position.X >> 4) &&
                        (projected.Y >> 4) == (seat.Position.Y >> 4) &&
                        projected.Height == seat.Position.Height)
                    {
                        matchedWorldSector = worldSector;
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static uint RouteMaskAt(SimulationWorld world, SimsEngine.World.RoomMap map, int worldX, int worldY)
    {
        var x = worldX >> 4;
        var y = worldY >> 4;
        if ((uint)x >= map.Width || (uint)y >= map.Height) return 0;
        var mask = 0u;
        for (var direction = 0; direction < 8; direction++)
        {
            if (world.BlocksRouteDirectionAt(map, x, y, direction)) mask |= 1u << direction;
        }
        return mask;
    }

    private static void TraceCandidate(SimulationWorld world, XPerson person, XObject anchor,
        ObjectPosition candidate, bool accepted, uint slotFlags)
    {
        if (!world.CaptureExecutionTrace) return;
        world.RouteCandidateTrace.Add(new SimulationRouteCandidateTrace(person.Id, anchor.Id,
            candidate, RoomKeyAt(world, candidate), anchor[ObjectData.Direction], accepted,
            world.LastPlacementFailureCode, person[ObjectData.PrimitiveResultId], accepted,
            $"{world.LastRouteCandidateFailureReason}; slotFlags=0x{slotFlags:X}"));
    }

    private static bool Walkable(SimulationWorld world, XPerson person, XObject anchor, ObjectPosition position,
        RoutingSlotRequest? destinationSlot = null, bool writeCandidateResults = true, TreeStack? stack = null,
        short? candidateSector = null)
    {
        var accepted = IsWalkableCandidate(world, person, anchor, position, destinationSlot, stack, out var blockerId,
            candidateSector: candidateSector);
        if (writeCandidateResults)
        {
            person[ObjectData.PrimitiveResultId] = blockerId;
        // The primitive result fields expose each source candidate validator's final state,
        // whether the candidate was accepted or rejected.
            person[ObjectData.PrimitiveResult] = 0;
        }
        return accepted;
    }

    private static bool IsWalkableCandidate(SimulationWorld world, XPerson person, XObject anchor, ObjectPosition position,
        RoutingSlotRequest? destinationSlot, TreeStack? stack, out short blockerId, short? candidateSector = null)
    {
        world.LastRouteCandidateFailureReason = string.Empty;
        world.LastPlacementBlockingObjectId = 0;
        blockerId = 0;
        if (!world.LotRoomMaps.TryGetValue(position.Height, out var map))
        {
            world.LastRouteCandidateFailureReason = "missing route map";
            return false;
        }
        var x = position.X >> 4;
        var y = position.Y >> 4;
        // FUN_004550e0's nonzero +0x1c request argument jumps directly to the placement callback,
        // bypassing its local bounds, room, and route-edge checks. Pedestrian portal anchors also bypass
        // inner bounds to permit valid playable perimeter sidewalk tiles (e.g. House 5 sidewalk exit).
        var isPortalAnchor = unchecked((uint)(anchor.TypeRecord?.Key ?? 0)) == 0x81E6BEF9 || anchor.RuntimeClass == Formats.ObjectClass.Portal;
        var bypassRouteEdgeChecks = (destinationSlot is not null && (destinationSlot.Flags & 0x0800) != 0) || isPortalAnchor;
        if (!bypassRouteEdgeChecks && (x < 1 || y < 1 || x >= map.Width - 1 || y >= map.Height - 1))
        {
            world.LastRouteCandidateFailureReason = "outside routable tile bounds";
            return false;
        }
        if (x < 0 || y < 0 || x >= map.Width || y >= map.Height)
        {
            world.LastRouteCandidateFailureReason = "outside map tile bounds";
            return false;
        }
        var room = map.RoomAtPosition(position.X, position.Y);
        if (!bypassRouteEdgeChecks && room == SimsEngine.World.RoomMap.NoRoom)
        {
            world.LastRouteCandidateFailureReason = "no room at candidate";
            return false;
        }
        // FUN_00483572 / FUN_00481593 check the route edge between the anchor origin and the
        // candidate tile when the candidate is on a different tile in the same room and SLOT
        // flag 0x0800 is clear. When the candidate is on the anchor's own tile (e.g. a door
        // portal's zero-direction slot), FUN_00483572 passes direction -1 (0xffffffff) and
        // FUN_00481593 skips the edge check.
        var anchorOrigin = destinationSlot is not null ? RouteAnchorOrigin(world, anchor, destinationSlot) : anchor.Position;
        var anchorTileX = anchorOrigin.X >> 4;
        var anchorTileY = anchorOrigin.Y >> 4;
        var personPosition = person.Position;
        var currentTileX = personPosition.X >> 4;
        var currentTileY = personPosition.Y >> 4;
        // FUN_00481593 compares the candidate's mapped room with the Sim's stored +0x84 word
        // directly. In particular, its -5 sentinel does not get resolved through the room map.
        var currentRoom = unchecked((ushort)person.RoomWord84);
        if (destinationSlot is not null && (destinationSlot.Flags & 0x0800) == 0 && (destinationSlot.Flags & 0x0400) == 0)
        {
            if (ReferenceEquals(anchor, person))
            {
                if (room == currentRoom && (position.X != personPosition.X || position.Y != personPosition.Y))
                {
                    var direction = DirectionToWaypoint((personPosition.X, personPosition.Y), (position.X, position.Y));
                    var reverseDirection = (direction + 4) & 7;
                    var fromBlocked = world.BlocksRouteDirectionAt(map, currentTileX, currentTileY, direction);
                    var toBlocked = world.BlocksRouteDirectionAt(map, x, y, reverseDirection);
                    if (fromBlocked || toBlocked)
                    {
                        world.LastRouteCandidateFailureReason = $"same-room route edge blocked; from={RouteBlockDetails(world, map, currentTileX, currentTileY, direction, fromBlocked)}; to={RouteBlockDetails(world, map, x, y, reverseDirection, toBlocked)}";
                        return false;
                    }
                }
            }
            else
            {
                var anchorRoom = PortalRoomKey(anchor);
                var radialSlot = (destinationSlot.Flags & 0xff) != 0;
                if (radialSlot && !isPortalAnchor && anchor.TypeRecord?.ObjectType != 8 &&
                    anchorRoom != SimsEngine.World.RoomMap.NoRoom && anchorRoom != unchecked((ushort)-5) &&
                    room != anchorRoom)
                {
                    world.LastRouteCandidateFailureReason = $"candidate room {room} does not match target room {anchorRoom}";
                    return false;
                }
                if (room == anchorRoom && (x != anchorTileX || y != anchorTileY))
                {
                    // FUN_00483572 passes worldSector (-0x4c) for radial/chair slots (00483572.c:272) and
                    // DirectionToWaypoint(anchorOrigin, exactPos) for zero-direction slots (00483572.c:59,69).
                    var direction = candidateSector ?? DirectionToWaypoint((anchorOrigin.X, anchorOrigin.Y), (position.X, position.Y));
                    var reverseDirection = (direction + 4) & 7;
                    var fromBlocked = !radialSlot && world.BlocksRouteDirectionAt(map, anchorTileX, anchorTileY, direction);
                    var toBlocked = world.BlocksRouteDirectionAt(map, x, y, reverseDirection);
                    // FUN_00481593 (00481593.c:51): when the step from anchor to candidate is diagonal
                    // ((direction & 1) != 0), also reject when either adjacent cardinal wall bit at the
                    // candidate tile ((reverseDirection + 1) & 7 or (reverseDirection + 7) & 7, i.e. 0x82
                    // rotated by reverseDirection) is blocked.
                    var diagonalCornerBlocked = (direction & 1) != 0 &&
                        (world.BlocksRouteDirectionAt(map, x, y, (reverseDirection + 1) & 7) ||
                         world.BlocksRouteDirectionAt(map, x, y, (reverseDirection + 7) & 7));
                    if (fromBlocked || toBlocked || diagonalCornerBlocked)
                    {
                        world.LastRouteCandidateFailureReason = $"same-room route edge blocked; from={RouteBlockDetails(world, map, anchorTileX, anchorTileY, direction, fromBlocked)}; to={RouteBlockDetails(world, map, x, y, reverseDirection, toBlocked)}; diagonalCornerBlocked={diagonalCornerBlocked}";
                        return false;
                    }
                }
            }
        }
        // Route slots with SnapTargetSlot < 0 use ordinary person placement. Snap-target placement
        // uses the selected footprint slot and remains a separate placement branch.
        var placementAccepted = destinationSlot is { SnapTargetSlot: >= 0 } snappedSlot
            ? world.CanPlaceRouteCandidate(person, position, anchor, snappedSlot.SnapTargetSlot)
            // FUN_00589d4b validates intermediate movement with null reference/slot
            // arguments. Using the action target as a reference rejects every step for
            // Destination.iff, whose Flags bit 2 deliberately makes it non-solid.
            : world.CanPlaceObject(person, position, stack);
        if (!placementAccepted)
        {
            var referenceSlot = destinationSlot?.SnapTargetSlot ?? -1;
            var referencedHeightCode = referenceSlot >= 0 && referenceSlot < anchor.FootprintSlots.Count
                ? anchor.FootprintSlots[referenceSlot].HeightCode
                : (byte)0;
            world.LastRouteCandidateFailureReason =
                $"person placement callback rejected candidate (error=0x{world.LastPlacementFailureCode:X2}; referenceSlot={referenceSlot}; heightCode={referencedHeightCode}; allowedHeight=0x{unchecked((ushort)person[ObjectData.AllowedHeightFlags]):X4}; overlapObject={world.LastPlacementBlockingObjectId})";
            blockerId = world.LastPlacementBlockingObjectId != 0
                ? world.LastPlacementBlockingObjectId
                : BlockingRootObjectId(world, person, anchor, x, y, position.Height);
            return false;
        }

        // FUN_004550e0 next calls FUN_00476f33 only when the reference object and request flag bit 2 are set.
        // It temporarily applies the candidate direction while building the person's footprint, then restores it.
        if (destinationSlot is { CheckCandidateFootprintAgainstWall: true })
        {
            var originalDirection = person[ObjectData.Direction];
            person[ObjectData.Direction] = CandidateValidationDirection(person, anchor, destinationSlot);
            bool intersectsWall;
            try
            {
                intersectsWall = world.FootprintIntersectsWall(
                    person.FootprintBoundsAt(position), position.Height);
            }
            finally
            {
                person[ObjectData.Direction] = originalDirection;
            }
            if (intersectsWall)
            {
                world.LastRouteCandidateFailureReason = "arrival footprint intersects a wall";
                return false;
            }
        }

        // The source FUN_004550e0 candidate path ends with the cXPerson placement callback
        // (vtable +0x54); there is no later tile-root equality test. The callback has already
        // checked every intersecting object and its placement flags. A separate TileValueAt
        // check rejects valid slots on a pass-through child tile of multi-tile objects.
        return true;
    }

    private static string RouteBlockDetails(SimulationWorld world, SimsEngine.World.RoomMap map, int x, int y,
        int direction, bool blocked)
    {
        if (!blocked) return "open";
        uint objectMask;
        try
        {
            objectMask = world.ObjectsAtTile(x, y, map.Level).Aggregate(0u, (mask, item) =>
            {
                var flags = unchecked((uint)(ushort)item[ObjectData.WallBlockFlags]);
                var rotated = flags << (unchecked((byte)item[ObjectData.Direction]) & 0x1f);
                return mask | ((rotated & 0xff) | ((rotated >> 8) & 0xff));
            });
        }
        catch (InvalidDataException)
        {
            return $"direction={direction},static=0x{map.RouteBlockingMaskAt(x, y):X2},objects=cycle";
        }
        return $"direction={direction},static=0x{map.RouteBlockingMaskAt(x, y):X2},objects=0x{objectMask:X2}";
    }

    private static short BlockingRootObjectId(SimulationWorld world, XPerson person, XObject anchor, int x, int y,
        int height)
    {
        var occupant = world.TileValueAt(x, y, height);
        var personKey = person.HasRequestKey ? person.RequestKey : person.ClockManagerIndex;
        var anchorKey = anchor.HasRequestKey ? anchor.RequestKey : anchor.ClockManagerIndex;
        if (occupant == 0 || occupant == personKey || occupant == anchorKey) return 0;
        var blocker = world.WalkOrder.FirstOrDefault(candidate => candidate.Position.Height == height &&
            (candidate.Position.X >> 4) == x && (candidate.Position.Y >> 4) == y &&
            (candidate.HasRequestKey ? candidate.RequestKey : candidate.ClockManagerIndex) == occupant);
        if (blocker is null || blocker.RuntimeClass == SimsEngine.Formats.ObjectClass.Portal) return 0;
        return blocker.ClockManagerIndex;
    }

    // cXPerson::MoveOutOfWay (Mac 0x10104710) creates Destination (GUID 0x7c4), installs a temporary
    // routing slot, and queues interaction 3 when this route crosses another Sim. Its overload's guard
    // (Mac 0x10105000; Windows FUN_0045c06f/FUN_0045c02d) avoids duplicate active or queued requests.
    internal static void AskPeopleOnRouteToMove(SimulationWorld world, XPerson requester, XObject anchor,
        IReadOnlyList<RouteWaypoint> path)
    {
        if (requester[PersonData.Sitting] != 0) return;
        var map = world.LotRoomMaps.GetValueOrDefault(requester.Position.Height);
        if (map is null) return;

        foreach (var other in world.WalkOrder.OfType<XPerson>().ToList())
        {
            var crossingDirection = RouteCrossingDirection(other, path);
            if (ReferenceEquals(other, requester) || ReferenceEquals(other, anchor) ||
                world.ResolveParentContainer(other) is not null ||
                other[PersonData.Sitting] != 0 ||
                other.Position.Height != requester.Position.Height ||
                unchecked((ushort)other.RoomWord84) != unchecked((ushort)requester.RoomWord84) ||
                other.NativeRoute is not null ||
                crossingDirection is null ||
                GetDistanceToPrimitive.DistanceBetween(requester, other) > 3.0 ||
                HasMoveAsideAction(other))
                continue;

            var guid = 0x7c4;
            var aside = world.ObjectFactory?.Invoke(guid);
            if (aside is null) continue;

            var added = false;
            try
            {
                world.Add(aside);
                added = true;
                world.InitializeCreatedObject(aside);
                world.SetObjectPosition(aside, other.Position);

                var intersection = ClosestRoutePoint(other, path);
                var direction = crossingDirection ?? (intersection.Index > 0
                    ? DirectionToWaypoint((intersection.Before.X, intersection.Before.Y),
                        (intersection.Point.X, intersection.Point.Y))
                    : DirectionToWaypoint((other.Position.X, other.Position.Y),
                        (intersection.After.X, intersection.After.Y)));
                var firstDirection = (direction + 2) & 7;
                var secondDirection = (direction + 6) & 7;
                var moveSlot = new RoutingSlotRequest
                {
                    // FUN_0046df4f(16, 80, 16), AllowDirection(base+2/base+6),
                    // AllowAnyFacing, SetMultiplier(standing, 1), and route flag 0x2000.
                    Flags = 0x2100u | (1u << firstDirection) | (1u << secondDirection),
                    MinProximity = 16,
                    MaxProximity = 80,
                    OptimalProximity = 16,
                    Standing = 1,
                    Facing = -3,
                };
                aside.RoutingSlots.Insert(0, moveSlot);
                var asideDestination = SelectDestination(world, other, aside, moveSlot);
                if (asideDestination is null || !TryPath(world, other, asideDestination.Value.Position, aside,
                        out _, out _, directPositionRequest: false, directSlot: moveSlot))
                {
                    // Mac MoveOutOfWay retries its generated route with the direct direction added
                    // and an 80-unit optimal distance when the perpendicular route has no path.
                    moveSlot.Flags |= 1u << (direction & 7);
                    moveSlot.OptimalProximity = 80;
                }

                var interaction = aside.TypeRecord?.Interactions?.Interactions
                    .FirstOrDefault(candidate => candidate.TtaIndex == 3);
                var priority = requester.ActivePriority != 0
                    ? requester.ActivePriority
                    : requester.CurrentActionRecord?.Threshold ?? 0;
                if (interaction is null || !world.EnqueueInteractionAction(other, aside, interaction, 3,
                        priority, null, secondarySpecified: false, recordFlags: 0))
                {
                    world.Remove(aside.Id);
                    continue;
                }
                added = false; // The queued action now owns this temporary destination object.
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException)
            {
                if (added) world.Remove(aside.Id);
            }
        }
    }

    // cXPerson::MoveOutOfWay(int) (Mac 0x10104710 int overload; Windows FUN_0045beca): a blocked Snapshot asks the
    // Sim in the way to step aside. The Go Here object (GUID 0x7c4) takes the blocker's position and a routing slot
    // that allows the four cardinal directions 0, 2, 4 and 6 at distances 16, 80 and 16, any facing, standing
    // multiplier 1 and route flag 0x2000; interaction 3 is queued at the caller's priority. The request is dropped
    // when a move-aside action is already current or queued, and the object is removed when the queue refuses it.
    internal static bool MoveOutOfWay(SimulationWorld world, XPerson blocker, int priority)
    {
        if (HasMoveAsideAction(blocker)) return false;
        var aside = world.ObjectFactory?.Invoke(0x7c4);
        if (aside is null) return false;

        var added = false;
        try
        {
            world.Add(aside);
            added = true;
            world.InitializeCreatedObject(aside);
            world.SetObjectPosition(aside, blocker.Position);
            aside.RoutingSlots.Insert(0, new RoutingSlotRequest
            {
                Flags = 0x2100u | (1u << 0) | (1u << 2) | (1u << 4) | (1u << 6),
                MinProximity = 16,
                MaxProximity = 80,
                OptimalProximity = 16,
                Standing = 1,
                Facing = -3,
            });
            var interaction = aside.TypeRecord?.Interactions?.Interactions
                .FirstOrDefault(candidate => candidate.TtaIndex == 3);
            if (interaction is not null && world.EnqueueInteractionAction(blocker, aside, interaction, 3,
                    priority, null, secondarySpecified: false, recordFlags: 0))
                return true;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
        }
        if (added) world.Remove(aside.Id);
        return false;
    }

    private static bool HasMoveAsideAction(XPerson person)
    {
        static bool IsMoveAside(ActionQueueSlot? action) =>
            action?.Owner is { } owner &&
            (owner.TypeRecord?.Key == 0x7c4 ||
             ((ushort)owner.Definition[(int)SimsEngine.Formats.ObjdField.Guid1] |
              ((uint)(ushort)owner.Definition[(int)SimsEngine.Formats.ObjdField.Guid2] << 16)) == 0x7c4u) &&
            (action.RawDword10 == 3 || action.Threshold == 3);

        if (IsMoveAside(person.CurrentActionRecord)) return true;
        for (var cursor = person.ActionQueueCurrent; cursor != person.ActionQueueEnd; cursor++)
            if (IsMoveAside(person.ActionQueue[cursor & 7])) return true;
        return false;
    }

    private static short? RouteCrossingDirection(XPerson person, IReadOnlyList<RouteWaypoint> path)
    {
        var bounds = person.FootprintBounds;
        (int X, int Y)? entry = null;
        for (var segmentIndex = 0; segmentIndex + 1 < path.Count; segmentIndex++)
        {
            var start = path[segmentIndex];
            var end = path[segmentIndex + 1];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var steps = Math.Max(Math.Abs(dx), Math.Abs(dy)) + 1;
            for (var sample = 0; sample < steps; sample++)
            {
                var x = start.X + dx * (sample / (float)steps);
                var y = start.Y + dy * (sample / (float)steps);
                var sampleX = (int)x;
                var sampleY = (int)y;
                var inside = sampleX >= bounds.MinX && sampleX < bounds.MaxX &&
                             sampleY >= bounds.MinY && sampleY < bounds.MaxY;
                if (inside && entry is null)
                    entry = (sampleX, sampleY);
                else if (!inside && entry is { } first)
                    return DirectionToWaypoint(first, (sampleX, sampleY));
            }
        }
        // If the route remains inside the footprint at its end, Mac uses the start of
        // the final path segment as the second direction point.
        return entry is { } only && path.Count > 1
            ? DirectionToWaypoint(only, (path[^2].X, path[^2].Y))
            : null;
    }

    // Mac MoveOutOfWay also considers route geometry while a Sim is already moving.
    private static bool SharesActiveRoute(SimulationWorld world, XPerson person, IReadOnlyList<RouteWaypoint> path)
    {
        if (person.NativeRoute is not { } otherRoute ||
            world.ObjectById(otherRoute.AnchorObjectId) is not { } otherAnchor ||
            !TryPath(world, person, otherRoute.Destination, otherAnchor, out var otherPath, out _,
                directPositionRequest: otherRoute.DirectPositionRequest, directSlot: otherRoute.Slot,
                allowCrossRoom: otherRoute.DirectPositionRequest)) return false;
        var requestCells = path.Skip(1).Select(point => (point.TileX, point.TileY)).ToHashSet();
        return otherPath.Skip(1).Any(point => requestCells.Contains((point.TileX, point.TileY)));
    }

    private static (int Index, (int X, int Y) Before, (int X, int Y) Point, (int X, int Y) After)
        ClosestRoutePoint(XPerson person, IReadOnlyList<RouteWaypoint> path)
    {
        var index = 1;
        var minimum = double.PositiveInfinity;
        for (var candidate = 1; candidate < path.Count; candidate++)
        {
            var dx = (long)path[candidate].X - person.Position.X;
            var dy = (long)path[candidate].Y - person.Position.Y;
            var distance = dx * dx + dy * dy;
            if (distance >= minimum) continue;
            minimum = distance;
            index = candidate;
        }
        var previous = path[Math.Max(0, index - 1)];
        var current = path[index];
        var next = path[Math.Min(path.Count - 1, index + 1)];
        return (index, (previous.X, previous.Y), (current.X, current.Y), (next.X, next.Y));
    }

    private static bool CanPlaceArrival(SimulationWorld world, XPerson person, XObject? anchor,
        RoutingSlotRequest slot, ObjectPosition position, TreeStack? stack)
    {
        var targetPosition = position with { Height = position.Height == 0 ? 1 : position.Height };
        if (world.CanPlaceObject(person, targetPosition, stack))
            return true;

        if (slot.SnapTargetSlot >= 0 && anchor is not null)
        {
            if (!world.CanPlaceRouteCandidate(person, targetPosition, anchor, slot.SnapTargetSlot))
                return false;

            var originalFlags = anchor[ObjectData.Flags];
            anchor[ObjectData.Flags] = (short)(originalFlags | 0x10);
            try
            {
                return world.CanPlaceObject(person, targetPosition, stack);
            }
            finally
            {
                anchor[ObjectData.Flags] = originalFlags;
            }
        }

        return false;
    }

    private static void ApplyRoutePlacement(SimulationWorld world, XPerson person, XObject anchor,
        ObjectPosition destination, RoutingSlotRequest slot, TreeStack? stack)
    {
        // FUN_004550e0's +0x58 callback receives the reference object and runtime-slot index for
        // a snapped destination, causing FUN_004327d9 to establish the container relationship.
        if (slot.SnapTargetSlot >= 0)
            // FUN_00432771 converts the zero-based SLOT definition index to the one-based
            // relationship index stored in cXObject +0x66 before FUN_004327d9 writes it.
            world.PlaceObject(person, destination, stack, anchor, checked((short)(slot.SnapTargetSlot + 1)));
        else
            world.PlaceObject(person, destination, stack);
        world.RefreshPersonRoomStateAfterMovement(person);
    }

    private static XObject? ResolveSeatedContainer(SimulationWorld world, XPerson person)
    {
        var containerKey = person[ObjectData.ContainerId];
        if (containerKey > 0)
        {
            if (person.ObjectTableEntry(containerKey) is { } fromPersonTable && !ReferenceEquals(fromPersonTable, person))
                return fromPersonTable;
            if (world.ObjectTableEntry(containerKey) is { } fromWorldTable && !ReferenceEquals(fromWorldTable, person))
                return fromWorldTable;
            if (world.ObjectById(containerKey) is { } fromId && !ReferenceEquals(fromId, person))
                return fromId;
        }
        return world.ResolveParentContainer(person);
    }

    private static bool TryPushSeatedStandTree(SimulationWorld world, XPerson person, XObject? seat, XObject anchor,
        RouteState route, TreeStack? stack, out int callerDepth)
    {
        callerDepth = -1;
        if (stack is null || seat is null ||
            !SimulationWorld.HasFunction(seat, 0x1a) ||
            SimulationWorld.FunctionTreeId(seat, 0x1b) is not (var standTree and not 0) ||
            seat.TypeRecord?.BehaviorScope is not { } seatScope)
            return false;

        if (route.DirectPositionRequest)
            person[PersonData.RouteEntryFlags] = 0;
        else
            person[PersonData.RouteEntryFlags] = SeatExitEntryFlags(world, person, seat, anchor);

        var nextCallerDepth = stack.Frames.Count;
        if (!PersonInteractionStack.Push(person, seat, stack, seatScope, standTree, seat.StackObjectKey,
                [person[PersonData.RouteEntryFlags], 0, 0, 0]))
            return false;

        callerDepth = nextCallerDepth;
        return true;
    }

    // FUN_0045aecf/FUN_0045c4fa copy the selected candidate's +0xe word into PersonData.RouteEntryFlags (+0x5d6).
    // FUN_00483572 writes 1 << localSector there, where localSector is the authored SLOT sector relative to the
    // slot orientation (0 = in front, 2/4/6 = the object's other sides). Chair BHAV 4099 branches on 1, 4, 16 and 64.
    // Exact-point goals (zero direction mask or snap target) leave the word at zero. Direct Go Here routes have no
    // target SLOT entry flags.
    private static void SetRouteEntryFlags(SimulationWorld world, XPerson person, XObject anchor, RouteState route)
    {
        if (route.DirectPositionRequest) return;
        person[PersonData.RouteEntryFlags] = route.EntryFlags;
    }

    private static short LocalSectorEntryFlags(short worldSector, short orientation) =>
        unchecked((short)(1 << ((worldSector - orientation) & 7)));

    /// <summary>FUN_00483432 (called first by FUN_004824b8): when the person is held by a seat with function 0x1a, each
    /// seat-relative side c enabled in OBJD ChairEntryFlags (+0x84) is projected one tile from the person's position in
    /// world direction (seat facing + c) & 7 (FUN_004222bd). Sides FUN_00481593 accepts (6, or 5 when only a person
    /// blocks) compete by squared distance to the route target; the nearest one's 1 &lt;&lt; c is written to route
    /// record +0x74, which FUN_0045aecf state 1 copies into +0x5d6 and passes to the seat's stand function.</summary>
    private static short SeatExitEntryFlags(SimulationWorld world, XPerson person, XObject seat, XObject target)
    {
        if (!SimulationWorld.HasFunction(seat, 0x1a)) return 0;
        var entryMask = unchecked((ushort)seat.Definition[(int)Formats.ObjdField.ChairEntryFlags]);
        short best = 0;
        var bestDistance = long.MaxValue;
        for (var side = 0; side < 8; side++)
        {
            if ((entryMask & (1 << side)) == 0) continue;
            var worldDirection = (short)((seat[ObjectData.Direction] + side) & 7);
            var offset = LotGeometry.FootprintOffset(worldDirection);
            var candidate = person.Position with { X = person.Position.X + offset.X, Y = person.Position.Y + offset.Y };
            var accepted = Walkable(world, person, seat, candidate, writeCandidateResults: false);
            if (!accepted && world.LastPlacementBlockingObjectId != 0 &&
                world.WalkOrder.FirstOrDefault(item => item.ClockManagerIndex == world.LastPlacementBlockingObjectId)
                    is XPerson)
                accepted = true;
            if (!accepted) continue;
            long dx = target.Position.X - candidate.X;
            long dy = target.Position.Y - candidate.Y;
            var distance = dx * dx + dy * dy;
            if (best != 0 && distance >= bestDistance) continue;
            best = unchecked((short)(1 << side));
            bestDistance = distance;
        }
        return best;
    }

    private readonly record struct PartitionCell(int MinX, int MinY, int MaxX, int MaxY,
        int CenterX, int CenterY, ushort Room, int Cost);

    private static bool TryPath(SimulationWorld world, XPerson person, ObjectPosition destination, XObject anchor,
        out List<RouteWaypoint> path, out double pathCost, bool directPositionRequest = false,
        RoutingSlotRequest? directSlot = null, TreeStack? stack = null,
        (int X, int Y)? startTileOverride = null, (int X, int Y)? startCellCenterOverride = null,
        bool allowCrossRoom = true)
    {
        path = [];
        pathCost = double.PositiveInfinity;
        if (!world.LotRoomMaps.TryGetValue(person.Position.Height, out var map) || destination.Height != person.Position.Height)
            return false;
        // ChooseStartingPoint overwrites the constructor's zeroed +0x6c/+0x70 with actor position
        // before goal/path setup (Windows FUN_00483432 via FUN_004824b8; Mac XRouting.s 0x10163D64–0x10163D74).
        // Holder-specific starting-point candidates are a separate branch of that source routine.
        var startPointX = person.Position.X;
        var startPointY = person.Position.Y;
        var startTile = startTileOverride ?? (X: startPointX >> 4, Y: startPointY >> 4);
        var endTile = (X: destination.X >> 4, Y: destination.Y >> 4);
        if (startTile.X < 0 || startTile.Y < 0 || startTile.X >= map.Width || startTile.Y >= map.Height ||
            endTile.X < 0 || endTile.Y < 0 || endTile.X >= map.Width || endTile.Y >= map.Height)
            return false;

        var startRoom = map.RoomAt(startTile.X, startTile.Y);
        var endRoom = map.RoomAt(endTile.X, endTile.Y);
        if (startRoom == SimsEngine.World.RoomMap.NoRoom || endRoom == SimsEngine.World.RoomMap.NoRoom) return false;
        if (!allowCrossRoom && startRoom != endRoom) return false;

        // Mac SpacePartition starts from room rectangles and splits them at every
        // PenaltyRect boundary. Tile boundaries retain RoomMap and route-block
        // information; penalty boundaries add the sub-tile nodes missing from the
        // previous tile-center search.
        var penaltyRects = new List<(int MinX, int MinY, int MaxX, int MaxY, int Cost, ushort Room)>();
        const int wallPenaltyInflate = 8;
        const int roomWallClearanceInflate = 7;

        // FUN_00481a0d (00481a0d.c:181-275): emit blocking wall-segment lines and inflate by 7 FTile units so
        // partition cells and Path::DoOneSmooth (0046a497.c) keep waypoints clear of walls and fence corners.
        for (var tileY = 0; tileY < map.Height; tileY++)
        for (var tileX = 0; tileX < map.Width; tileX++)
        {
            var walls = map.ExpandedWallAt(tileX, tileY);
            if (walls is null) continue;
            var mask = walls.Value.UInt32At(0);
            if ((mask & 0x33) == 0) continue;
            if ((mask & 1) != 0 && walls.Value.WallCategory(1) is not (3 or 5 or 6 or 15 or 23))
                AddRouteCostRect(tileX * 16, tileY * 16, tileX * 16, (tileY + 1) * 16,
                    10_000, SimsEngine.World.RoomMap.NoRoom, roomWallClearanceInflate, roomWallClearanceInflate);
            if ((mask & 2) != 0 && walls.Value.WallCategory(2) is not (3 or 5 or 6 or 15 or 23))
                AddRouteCostRect(tileX * 16, tileY * 16, (tileX + 1) * 16, tileY * 16,
                    10_000, SimsEngine.World.RoomMap.NoRoom, roomWallClearanceInflate, roomWallClearanceInflate);
            if ((mask & 0x10) != 0 && walls.Value.WallCategory(0x10) is not (3 or 5 or 6 or 15 or 23))
            {
                AddRouteCostRect(tileX * 16 + 4, tileY * 16 + 12, tileX * 16 + 4, tileY * 16 + 12,
                    10_000, SimsEngine.World.RoomMap.NoRoom, roomWallClearanceInflate, roomWallClearanceInflate);
                AddRouteCostRect(tileX * 16 + 12, tileY * 16 + 4, tileX * 16 + 12, tileY * 16 + 4,
                    10_000, SimsEngine.World.RoomMap.NoRoom, roomWallClearanceInflate, roomWallClearanceInflate);
            }
            if ((mask & 0x20) != 0 && walls.Value.WallCategory(0x20) is not (3 or 5 or 6 or 15 or 23))
            {
                AddRouteCostRect(tileX * 16 + 4, tileY * 16 + 4, tileX * 16 + 4, tileY * 16 + 4,
                    10_000, SimsEngine.World.RoomMap.NoRoom, roomWallClearanceInflate, roomWallClearanceInflate);
                AddRouteCostRect(tileX * 16 + 12, tileY * 16 + 12, tileX * 16 + 12, tileY * 16 + 12,
                    10_000, SimsEngine.World.RoomMap.NoRoom, roomWallClearanceInflate, roomWallClearanceInflate);
            }
        }

        // The routing check tree below runs object BHAV code; it may change the lot object
        // list while this pass is collecting penalty rectangles.
        foreach (var item in world.WalkOrder.ToArray())
        {
            if (item.Id == person.Id || item.Position.Height != person.Position.Height) continue;
            // FUN_00481947 (00481947.c:14-15) calls FUN_0043270d -> FUN_00432664, walking past
            // SlotHierNumber == 0 tile-overlap links and skipping only objects enclosed in an authored slot.
            if (world.ResolveParentContainer(item) is not null)
                continue;

            var routePenalty = item[ObjectData.RoutePenalty];
            var objectFlags = unchecked((ushort)item[ObjectData.Flags]);
            var ignoreZeroPenalty = (objectFlags & 0x0004) != 0
                ? (objectFlags & 0x0002) == 0
                : (objectFlags & 0x0010) != 0;
            if (routePenalty == 0 && ignoreZeroPenalty) continue;

            if (item.TypeRecord?.ObjectType == 2)
            {
                if (item.Id == anchor.Id || GetDistanceToPrimitive.DistanceBetween(person, item) > 3.0)
                    continue;
            }
            else
            {
                var checkTree = SimulationWorld.FunctionTreeId(item, 5);
                if (checkTree != 0 && item.TypeRecord?.BehaviorScope is { } scope &&
                    TreeRunner.RunWithParameters(world, item, scope, checkTree,
                        person.StackObjectKey, []))
                    continue;
            }

            var routeCost = routePenalty == 0 ? int.MaxValue : 10;
            var bounds = SimulationWorld.PlacementFootprintBounds(item, item.Position);
            var itemRoom = unchecked((ushort)item.RoomWord84);
            AddRouteCostRect(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY, routeCost, itemRoom);
            if (item.TypeRecord?.ObjectType != 8) continue;
            var storedWallFlags = unchecked((uint)(ushort)item[ObjectData.WallBlockFlags]);
            var rotatedWallFlags = storedWallFlags << (unchecked((byte)item[ObjectData.Direction]) & 0x1f);
            var wallFlags = (rotatedWallFlags & 0xff) | ((rotatedWallFlags >> 8) & 0xff);
            if ((wallFlags & 0x0001) != 0)
                AddRouteCostRect(bounds.MinX, bounds.MinY, bounds.MaxX + 1,
                    bounds.MaxY, 10_000, itemRoom, wallPenaltyInflate, wallPenaltyInflate);
            if ((wallFlags & 0x0004) != 0)
                AddRouteCostRect(bounds.MinX, bounds.MaxY - 1, bounds.MaxX,
                    bounds.MaxY, 10_000, itemRoom, wallPenaltyInflate, wallPenaltyInflate);
            if ((wallFlags & 0x0010) != 0)
                AddRouteCostRect(bounds.MaxX - 1, bounds.MinY, bounds.MaxX,
                    bounds.MaxY, 10_000, itemRoom, wallPenaltyInflate, wallPenaltyInflate);
            if ((wallFlags & 0x0040) != 0)
                AddRouteCostRect(bounds.MinX, bounds.MinY, bounds.MaxX,
                    bounds.MinY + 1, 10_000, itemRoom, wallPenaltyInflate, wallPenaltyInflate);
        }

        void AddRouteCostRect(int minX, int minY, int maxX, int maxY, int cost,
            ushort room, int inflateX = 0, int inflateY = 0)
        {
            minX = Math.Max(0, minX - inflateX);
            minY = Math.Max(0, minY - inflateY);
            maxX = Math.Min(map.Width * 16, maxX + inflateX);
            maxY = Math.Min(map.Height * 16, maxY + inflateY);
            if (minX < maxX && minY < maxY)
                penaltyRects.Add((minX, minY, maxX, maxY, cost, room));
        }

        var cells = new List<PartitionCell>();
        for (var tileY = 0; tileY < map.Height; tileY++)
        for (var tileX = 0; tileX < map.Width; tileX++)
        {
            var room = map.RoomAt(tileX, tileY);
            if (room == SimsEngine.World.RoomMap.NoRoom || (!allowCrossRoom && room != startRoom)) continue;
            var minX = tileX * 16; var minY = tileY * 16;
            var maxX = minX + 16; var maxY = minY + 16;
            cells.Add(new PartitionCell(minX, minY, maxX, maxY, minX + 8, minY + 8, room, 0));
        }

        // Expand only the nodes intersected by each penalty rectangle, matching
        // SpacePartition::ExpandRect. This avoids projecting an object's boundaries
        // across unrelated rooms and empty parts of the lot.
        foreach (var rect in penaltyRects)
        {
            for (var i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                if ((rect.Room != SimsEngine.World.RoomMap.NoRoom && cell.Room != rect.Room) ||
                    rect.MinX >= cell.MaxX || rect.MaxX <= cell.MinX ||
                    rect.MinY >= cell.MaxY || rect.MaxY <= cell.MinY)
                    continue;

                var xCuts = new SortedSet<int> { cell.MinX, cell.MaxX,
                    Math.Clamp(rect.MinX, cell.MinX, cell.MaxX), Math.Clamp(rect.MaxX, cell.MinX, cell.MaxX) };
                var yCuts = new SortedSet<int> { cell.MinY, cell.MaxY,
                    Math.Clamp(rect.MinY, cell.MinY, cell.MaxY), Math.Clamp(rect.MaxY, cell.MinY, cell.MaxY) };
                var newCells = new List<PartitionCell>();
                var xValues = xCuts.ToArray();
                var yValues = yCuts.ToArray();
                for (var y = 0; y < yValues.Length - 1; y++)
                for (var x = 0; x < xValues.Length - 1; x++)
                {
                    var minX = xValues[x]; var maxX = xValues[x + 1];
                    var minY = yValues[y]; var maxY = yValues[y + 1];
                    var centerX = minX + (maxX - minX) / 2;
                    var centerY = minY + (maxY - minY) / 2;
                    var inside = centerX >= rect.MinX && centerX < rect.MaxX &&
                                 centerY >= rect.MinY && centerY < rect.MaxY;
                    newCells.Add(new PartitionCell(minX, minY, maxX, maxY, centerX, centerY,
                        cell.Room, inside ? Math.Max(cell.Cost, rect.Cost) : cell.Cost));
                }
                cells.RemoveAt(i);
                cells.InsertRange(i, newCells);
                i += newCells.Count - 1;
            }
        }

        if (startTileOverride is { } retained && startCellCenterOverride is null)
        {
            if (startPointX == (retained.X + 1) * 16) startPointX--;
            if (startPointY == (retained.Y + 1) * 16) startPointY--;
        }
        var startLookupX = startCellCenterOverride?.X ?? startPointX;
        var startLookupY = startCellCenterOverride?.Y ?? startPointY;
        var startId = cells.FindIndex(cell => startLookupX >= cell.MinX && startLookupX < cell.MaxX &&
            startLookupY >= cell.MinY && startLookupY < cell.MaxY);
        var endId = cells.FindIndex(cell => destination.X >= cell.MinX && destination.X < cell.MaxX &&
            destination.Y >= cell.MinY && destination.Y < cell.MaxY);
        if (startId < 0 || endId < 0) return false;

        var neighbors = Enumerable.Range(0, cells.Count)
            .Select(_ => new List<(int Id, int X, int Y)>()).ToArray();
        var minXEdges = new Dictionary<int, List<int>>();
        var maxXEdges = new Dictionary<int, List<int>>();
        var minYEdges = new Dictionary<int, List<int>>();
        var maxYEdges = new Dictionary<int, List<int>>();
        for (var id = 0; id < cells.Count; id++)
        {
            var cell = cells[id];
            AddEdge(minXEdges, cell.MinX, id); AddEdge(maxXEdges, cell.MaxX, id);
            AddEdge(minYEdges, cell.MinY, id); AddEdge(maxYEdges, cell.MaxY, id);
        }
        void Connect(int firstId, int secondId)
        {
            var point = FindPartitionInterface(cells[firstId], cells[secondId]);
            neighbors[firstId].Add((secondId, point.X, point.Y));
            neighbors[secondId].Add((firstId, point.X, point.Y));
        }
        foreach (var edge in maxXEdges)
        {
            if (!minXEdges.TryGetValue(edge.Key, out var rightCells)) continue;
            foreach (var leftId in edge.Value)
            foreach (var rightId in rightCells)
            {
                var left = cells[leftId]; var right = cells[rightId];
                if (Math.Max(left.MinY, right.MinY) < Math.Min(left.MaxY, right.MaxY))
                    Connect(leftId, rightId);
            }
        }
        foreach (var edge in maxYEdges)
        {
            if (!minYEdges.TryGetValue(edge.Key, out var lowerCells)) continue;
            foreach (var upperId in edge.Value)
            foreach (var lowerId in lowerCells)
            {
                var upper = cells[upperId]; var lower = cells[lowerId];
                if (Math.Max(upper.MinX, lower.MinX) < Math.Min(upper.MaxX, lower.MaxX))
                    Connect(upperId, lowerId);
            }
        }

        static void AddEdge(Dictionary<int, List<int>> edges, int coordinate, int id)
        {
            if (!edges.TryGetValue(coordinate, out var line)) edges[coordinate] = line = [];
            line.Add(id);
        }

        var queue = new PriorityQueue<int, (double Estimate, double Cost, long Sequence)>();
        var costs = new Dictionary<int, double> { [startId] = 0d };
        var previous = new Dictionary<int, int>();
        var visited = new HashSet<int>();
        long sequence = 0;
        var rejectedPlacements = 0;
        var rejectedEdges = 0;
        var placementCache = new Dictionary<int, bool> { [startId] = true, [endId] = true };
        bool CanVisit(int id)
        {
            if (placementCache.TryGetValue(id, out var allowed)) return allowed;
            var cell = cells[id];
            var candidate = new ObjectPosition(cell.CenterX, cell.CenterY, person.Position.Height);
            allowed = Walkable(world, person, anchor, candidate, writeCandidateResults: false, stack: stack);
            placementCache[id] = allowed;
            if (!allowed) rejectedPlacements++;
            return allowed;
        }
        queue.Enqueue(startId, (DistanceBetweenNodeCenters(cells[startId], cells[endId]), 0d, sequence++));
        var directions = new (int X, int Y)[] { (1, 0), (0, 1), (-1, 0), (0, -1) };

        while (queue.TryDequeue(out var currentId, out var queuedPriority))
        {
            if (!costs.TryGetValue(currentId, out var currentCost) || queuedPriority.Cost != currentCost ||
                !visited.Add(currentId)) continue;
            if (currentId == endId)
            {
                pathCost = currentCost;
                break;
            }
            var current = cells[currentId];
            foreach (var (nextId, interfaceX, interfaceY) in neighbors[currentId])
            {
                if (nextId < 0 || visited.Contains(nextId)) continue;
                if (!CanVisit(nextId)) continue;
                var next = cells[nextId];
                var fromTile = (X: current.CenterX >> 4, Y: current.CenterY >> 4);
                var toTile = (X: next.CenterX >> 4, Y: next.CenterY >> 4);
                if (fromTile == toTile)
                {
                    if (current.Room != next.Room) { rejectedEdges++; continue; }
                }
                else if (!CanStep(world, map, fromTile, toTile))
                {
                    rejectedEdges++;
                    continue;
                }

                var dx = interfaceX - current.CenterX;
                var dy = interfaceY - current.CenterY;
                var edgeDistance = Math.Sqrt(dx * dx + dy * dy);
                var edgeCost = ((current.Cost + 2L + next.Cost) >> 1) * edgeDistance;
                var nextCost = currentCost + edgeCost;
                if (costs.TryGetValue(nextId, out var priorCost) && priorCost <= nextCost) continue;
                costs[nextId] = nextCost;
                previous[nextId] = currentId;
                queue.Enqueue(nextId, (nextCost + DistanceBetweenNodeCenters(next, cells[endId]), nextCost, sequence++));
            }
        }

        if (world.CaptureExecutionTrace)
        {
            var visitedRooms = string.Join(",", visited.Select(id => cells[id].Room)
                .GroupBy(room => room).OrderBy(group => group.Key)
                .Select(group => $"{group.Key}:{group.Count()}"));
            world.RouteSearchTrace.Add(new SimulationRouteSearchTrace(person.Id, destination, startTile, endTile,
                startRoom, endRoom, visited.Count, rejectedPlacements, rejectedEdges, visitedRooms));
        }
        if (!double.IsFinite(pathCost)) return false;

        var reverse = new List<int> { endId };
        while (reverse[^1] != startId) reverse.Add(previous[reverse[^1]]);
        reverse.Reverse();
        var smoothedPoints = reverse.Select(id => (cells[id].CenterX, cells[id].CenterY)).ToList();
        SmoothPartitionPath(cells, reverse, smoothedPoints);
        // FUN_0046a7c5 gives the seed record a valid +4 partition-cell index and the target record
        // +4=-1; FUN_0046a16d writes the exact route seed coordinates to the seed record. Therefore
        // FUN_0046a1f7 retains the exact seed point, omits the exact target sentinel, and emits the
        // remaining partition-derived points in seed-to-target order before appending the midpoint
        // of the target-side node at reverse-chain index 1. Keep the source route-task seed at index
        // zero, emit search-cell representatives for all linked partition nodes, and append that
        // target-cell midpoint as the route terminal.
        path.Add(new RouteWaypoint(startTile.X, startTile.Y, startPointX, startPointY,
            cells[startId].CenterX, cells[startId].CenterY, false));
        // The Mac Path starts at the Sim's own point; the start cell's centre is only the search node, not a waypoint.
        for (var i = 1; i < reverse.Count; i++)
        {
            var cell = cells[reverse[i]];
            // Path::OpenANode first records each node's representative point;
            // DoOneSmooth later adjusts it within its interface rectangle.
            path.Add(ToWaypoint(smoothedPoints[i], cell));
        }
        if (path[^1].X == destination.X && path[^1].Y == destination.Y)
        {
            path[^1] = path[^1] with { IsDestination = true };
        }
        else
        {
            path.Add(new RouteWaypoint(endTile.X, endTile.Y, destination.X, destination.Y,
                cells[endId].CenterX, cells[endId].CenterY, true));
        }
        if (world.CaptureExecutionTrace)
        {
            var crossings = new List<string>();
            ushort previousRoom = SimsEngine.World.RoomMap.NoRoom;
            foreach (var point in path)
            {
                var room = map.RoomAt(point.TileX, point.TileY);
                if (crossings.Count == 0 || room != previousRoom)
                    crossings.Add($"{room}@{point.TileX},{point.TileY}");
                previousRoom = room;
            }
            world.NativeRouteTrace.Add(new SimulationNativeRouteTrace(person.Id, anchor.Id, destination,
                "path-rooms", string.Join("->", crossings)));
        }
        return true;

        RouteWaypoint ToWaypoint((int CenterX, int CenterY) point, PartitionCell cell) =>
            new(point.CenterX >> 4, point.CenterY >> 4, point.CenterX, point.CenterY,
                cell.CenterX, cell.CenterY, false);
    }

    private static void SmoothPartitionPath(IReadOnlyList<PartitionCell> cells, IReadOnlyList<int> nodeIds,
        IList<(int CenterX, int CenterY)> points)
    {
        if (nodeIds.Count < 2) return;

        // Mac Path::DoOneSmooth starts with a 100-unit search radius and reduces it
        // as 100/(pass+1)^2, with a floor of one. For each point after the first it
        // tests the four axis-aligned offsets that remain inside FindInterfaceRect,
        // minimizing the sum of distances to the adjacent path points. A pass that
        // moves anything is repeated at the same radius until no candidate improves.
        for (var pass = 0; ; pass++)
        {
            var divisor = (long)(pass + 1) * (pass + 1);
            var radius = (int)Math.Max(1, 100 / divisor);
            var moved = false;
            for (var index = 1; index < nodeIds.Count; index++)
            {
                var previousCell = cells[nodeIds[index - 1]];
                var currentCell = cells[nodeIds[index]];
                var left = Math.Max(previousCell.MinX, currentCell.MinX - 1);
                var top = Math.Max(previousCell.MinY, currentCell.MinY - 1);
                var right = Math.Min(previousCell.MaxX, currentCell.MaxX + 1);
                var bottom = Math.Min(previousCell.MaxY, currentCell.MaxY + 1);
                var point = points[index];
                var previous = points[index - 1];
                var next = index + 1 < points.Count ? points[index + 1] : point;
                var best = point;
                var bestCost = Distance(previous, point) + Distance(point, next);

                void Consider(int x, int y)
                {
                    var candidate = (CenterX: x, CenterY: y);
                    var cost = Distance(previous, candidate) + Distance(candidate, next);
                    if (cost >= bestCost) return;
                    bestCost = cost;
                    best = candidate;
                }

                if (right - left > 2 && point.CenterX < right - radius)
                    Consider(point.CenterX + radius, point.CenterY);
                if (right - left > 2 && point.CenterX > left + radius)
                    Consider(point.CenterX - radius, point.CenterY);
                if (bottom - top > 2 && point.CenterY < bottom - radius)
                    Consider(point.CenterX, point.CenterY + radius);
                if (bottom - top > 2 && point.CenterY > top + radius)
                    Consider(point.CenterX, point.CenterY - radius);

                if (best == point) continue;
                points[index] = best;
                moved = true;
            }

            if (moved) continue;
            if (radius > 1) continue;
            return;
        }

        // Mac Distance2 returns a single-precision square root (Routing.s 0x10126BD0–0x10126C48),
        // and EvalPoint adds those float distances before its strict less-than comparison.
        static float Distance((int CenterX, int CenterY) first, (int CenterX, int CenterY) second)
        {
            var dx = (float)(first.CenterX - second.CenterX);
            var dy = (float)(first.CenterY - second.CenterY);
            return MathF.Sqrt(dx * dx + dy * dy);
        }
    }

    private static (int X, int Y) FindPartitionInterface(PartitionCell first, PartitionCell second)
    {
        if (first.MaxX == second.MinX || second.MaxX == first.MinX)
        {
            var sharedX = first.MaxX == second.MinX ? first.MaxX : first.MinX;
            var minY = Math.Max(first.MinY - 1, second.MinY - 1);
            var maxY = Math.Min(first.MaxY + 1, second.MaxY + 1);
            return (sharedX, (minY + maxY) / 2);
        }
        var sharedY = first.MaxY == second.MinY ? first.MaxY : first.MinY;
        var minX = Math.Max(first.MinX - 1, second.MinX - 1);
        var maxX = Math.Min(first.MaxX + 1, second.MaxX + 1);
        return ((minX + maxX) / 2, sharedY);
    }

    private static double DistanceBetweenNodeCenters(PartitionCell first, PartitionCell second)
    {
        var deltaX = second.CenterX - first.CenterX;
        var deltaY = second.CenterY - first.CenterY;
        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }

    private static bool CanCaptureRouteSnapshot(SimulationWorld world, RouteState route) =>
        world.CaptureExecutionTrace && route.DiagnosticSnapshotCount < 3;

    private static RouteState TracePortalCallbackSnapshot(SimulationWorld world, XPerson person,
        RouteState route)
    {
        if (!CanCaptureRouteSnapshot(world, route)) return route;
        route = route with { DiagnosticSnapshotCount = route.DiagnosticSnapshotCount + 1 };
        person.NativeRoute = route;
        var currentRoom = RoomKeyAt(world, person.Position);
        var destinationRoom = RoomKeyAt(world, route.Destination);
        Trace(world, person, route.AnchorObjectId, route.Destination, "portal-callback-world",
            $"portal={route.OpenedPortalId}; function={route.OpenedPortalFunctionObjectId}; " +
            $"position=({person.Position.X},{person.Position.Y},{person.Position.Height}); roomWord={person.RoomWord84}; " +
            $"tileRoom={currentRoom}; destinationRoom={destinationRoom}; " +
            RouteWorldSnapshot(world, person, person.Position, route.Destination));
        return route;
    }

    private static string RouteWorldSnapshot(SimulationWorld world, XPerson person,
        ObjectPosition source, ObjectPosition destination)
    {
        var parent = person.ObjectTableEntry(person[ObjectData.ParentId]);
        return $"person={DescribeObject(person)} parent={DescribeObject(parent)}; " +
            $"sourceTile={TileRootDescription(world, source.X >> 4, source.Y >> 4, source.Height)}; " +
            $"destinationTile={TileRootDescription(world, destination.X >> 4, destination.Y >> 4, destination.Height)}";
    }

    private static string TileRootDescription(SimulationWorld world, int x, int y, int level)
    {
        var rootKey = world.TileValueAt(x, y, level);
        var root = rootKey is > 0 and <= short.MaxValue ? world.ObjectTableEntry((short)rootKey) : null;
        return $"({x},{y},{level})={rootKey}:{DescribeObject(root)}";
    }

    private static string DescribeObject(XObject? item)
    {
        if (item is null) return "null";
        var slots = string.Join(",", item.Slots.Take(4));
        return $"id={item.Id}/key={item.ClockManagerIndex}/parent={item[ObjectData.ParentId]}/" +
            $"container={item[ObjectData.ContainerId]}/slot={item[ObjectData.SlotHierNumber]}/slots=[{slots}]";
    }

    private static string RejectedEdgeDescription(SimulationWorld world, SimsEngine.World.RoomMap map,
        (int X, int Y) from, (int X, int Y) to)
    {
        var direction = DirectionToWaypoint(from, to);
        var fromRoom = map.RoomAt(from.X, from.Y);
        var toRoom = map.RoomAt(to.X, to.Y);
        var fromBlocked = world.BlocksRouteDirectionAt(map, from.X, from.Y, direction);
        var toDirection = (direction + 4) & 7;
        var toBlocked = world.BlocksRouteDirectionAt(map, to.X, to.Y, toDirection);
        var fromDetails = RouteBlockDetails(world, map, from.X, from.Y, direction, fromBlocked);
        var toDetails = RouteBlockDetails(world, map, to.X, to.Y, toDirection, toBlocked);
        var reason = fromRoom != toRoom
            ? "cross-room portal authorization rejected"
            : fromBlocked || toBlocked
                ? "endpoint route-block mask"
                : $"diagonal corner blocked; horizontal={CardinalEdgeDescription(world, map, from, (to.X, from.Y))}; " +
                  $"vertical={CardinalEdgeDescription(world, map, from, (from.X, to.Y))}";
        return $"edge reason={reason}; direction={direction}; from=({from.X},{from.Y}) room={fromRoom} {fromDetails} " +
            $"to=({to.X},{to.Y}) room={toRoom} {toDetails}; " +
            $"root={TileRootDescription(world, to.X, to.Y, map.Level)}";
    }

    private static string CardinalEdgeDescription(SimulationWorld world, SimsEngine.World.RoomMap map,
        (int X, int Y) from, (int X, int Y) to)
    {
        var direction = DirectionToWaypoint(from, to);
        var reverse = (direction + 4) & 7;
        var fromBlocked = world.BlocksRouteDirectionAt(map, from.X, from.Y, direction);
        var toBlocked = world.BlocksRouteDirectionAt(map, to.X, to.Y, reverse);
        return $"from=({from.X},{from.Y}) {RouteBlockDetails(world, map, from.X, from.Y, direction, fromBlocked)}; " +
            $"to=({to.X},{to.Y}) {RouteBlockDetails(world, map, to.X, to.Y, reverse, toBlocked)}";
    }

    private static ushort RoomKeyAt(SimulationWorld world, ObjectPosition position) =>
        world.LotRoomMaps.TryGetValue(position.Height, out var map) &&
        (uint)(position.X >> 4) < map.Width && (uint)(position.Y >> 4) < map.Height
            ? map.RoomAtPosition(position.X, position.Y)
            : SimsEngine.World.RoomMap.NoRoom;

    internal static bool CanStep(SimulationWorld world, SimsEngine.World.RoomMap map,
        (int X, int Y) from, (int X, int Y) to)
    {
        var direction = DirectionToWaypoint(from, to);
        var fromRoom = map.RoomAt(from.X, from.Y);
        var toRoom = map.RoomAt(to.X, to.Y);
        if (fromRoom != toRoom)
        {
            // A room boundary is crossed by routing through a portal function. The saved wall segment
            // remains present in the static blocker mask at closed/interactive doors, so the portal
            // manager's source-room selection must authorize this transition before applying that mask.
            return fromRoom != SimsEngine.World.RoomMap.NoRoom &&
                   toRoom != SimsEngine.World.RoomMap.NoRoom &&
                   HasPortalForStep(world, map.Level, fromRoom, toRoom, from, to);
        }
        if (world.BlocksRouteDirectionAt(map, from.X, from.Y, direction) ||
            world.BlocksRouteDirectionAt(map, to.X, to.Y, (direction + 4) & 7)) return false;

        // FUN_004550e0 checks the requested edge at both endpoints before placement validation.
        // Do not add a current-endpoint-only cardinal corner gate here: it makes a legal diagonal
        // entry one-way when the reverse side has a blocked orthogonal edge (House05 at 26,25).
        return fromRoom != SimsEngine.World.RoomMap.NoRoom;
    }

    // FUN_0044350d only adds cXPortal instances with a nonzero function 0x0f to the live portal-manager vector;
    // FUN_00463dd9 scans that vector. Runtime-created function portals participate alongside saved-lot portals.
    private static IEnumerable<XObject> Portals(SimulationWorld world) =>
        world.WalkOrder.Where(item => item.RuntimeClass == SimsEngine.Formats.ObjectClass.Portal &&
            SimulationWorld.FunctionTreeId(item, 0x0f) != 0);

}
