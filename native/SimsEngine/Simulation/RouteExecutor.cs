using System.Buffers;

namespace SimsEngine.Simulation;

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
    /// <summary>The waypoint path providing the active motion target, retained until the segment completes
    /// to prevent projection errors during replanning.</summary>
    internal IReadOnlyList<RouteExecutor.RouteWaypoint>? MotionPath { get; init; }
    /// <summary>The index of the motion path waypoint currently targeted by the route cursor.</summary>
    internal int MotionTargetPathIndex { get; init; } = -1;
    internal IReadOnlyList<RouteExecutor.RouteWaypoint>? FollowPath { get; init; }
    internal int FollowWaypointIndex { get; init; }
    internal bool LookTowardsFollow { get; init; }
    /// <summary>Continuous [0, 8) directional heading toward a look-towards target.</summary>
    internal float? LookTowardsHeading { get; init; }
    /// <summary>Indicates that route following has initialized and is awaiting its first movement tick.</summary>
    internal bool BeginFollowPendingTick { get; init; }
    internal bool AwaitingBeginFollow { get; init; }
    internal bool EndFollowOnArrival { get; init; }
    /// <summary>Indicates a deferred swimming pool exit sequence is pending completion.</summary>
    internal bool PoolSwimExitPending { get; init; }
    internal float? PoolSwimExitRootX { get; init; }
    internal float? PoolSwimExitRootY { get; init; }
    internal float? PoolSwimExitDirection { get; init; }
    internal float? StartRootOriginX { get; init; }
    internal float? StartRootOriginY { get; init; }
    internal float? StartRootTargetX { get; init; }
    internal float? StartRootTargetY { get; init; }
    internal float? TurnRootOriginX { get; init; }
    internal float? TurnRootOriginY { get; init; }
    internal float? TurnRootTargetX { get; init; }
    internal float? TurnRootTargetY { get; init; }
    internal float? TurnRootSavedDirection { get; init; }
    internal float? TurnRootPracticeScale { get; init; }
    internal float? TurnRootStartDirection { get; init; }
    internal float? TurnRootPracticeDirection { get; init; }
    internal float? TransitionTargetX { get; init; }
    internal float? TransitionTargetY { get; init; }
    /// <summary>Bitmask representing the slot-relative entry sector, recorded to route entry flags upon
    /// arrival or before standing from a chair.</summary>
    internal short EntryFlags { get; init; }
    internal short ChairObjectId { get; init; }
    internal int PendingSitDepth { get; init; } = -1;
}

internal static class RouteExecutor
{
    /// <summary>Represents an accepted route goal. <paramref name="EntryFlags"/> stores a one-hot bitmask
    /// for the local sector or chair-relative approach side, and <paramref name="ChairObjectId"/>
    /// identifies the target chair for sitting goals.</summary>
    internal readonly record struct SelectedRouteGoal(ObjectPosition Position, short SearchDirection,
        short EntryFlags = 0, short ChairObjectId = 0);
    private readonly record struct RouteGoalCandidate(ObjectPosition Position, double Cost, short SearchDirection,
        short EntryFlags = 0, short ChairObjectId = 0);
    internal readonly record struct RouteWaypoint(int TileX, int TileY, int X, int Y,
        int CellCenterX, int CellCenterY, bool IsDestination);

    internal static PrimitiveResult BeginLookTowardsFollow(SimulationWorld world, XPerson person, StackFrame frame,
        IReadOnlyList<ObjectPosition> points)
    {
        if (points.Count < 2) throw new ArgumentException("A follow path needs at least two points.", nameof(points));
        person.AnimatorControlState = 3;
        person.AnimatorRouteTransitionState = 0;
        person.AnimationPlayback = null;
        person.AnimatorCurrentSkill = -1;
        person.AnimatorRootX = person.Position.X;
        person.AnimatorRootY = person.Position.Y;
        person.AnimatorRootDirection = person[ObjectData.Direction];
        person.SetAnimatorDisplacements(0f, 0f, 0f);
        var path = points.Select((point, index) => new RouteWaypoint(
            point.X >> 4, point.Y >> 4, point.X, point.Y, point.X, point.Y, index == points.Count - 1)).ToArray();
        var here = points[0];
        var lookPoint = points[^1];
        var lookDx = lookPoint.X - here.X;
        var lookDy = lookPoint.Y - here.Y;
        var lookHeading = lookDx == 0 && lookDy == 0
            ? (float)(person[ObjectData.Direction] & 7)
            : SimsEngine.Character.AvatarDirection.Toward(lookDx, lookDy);
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

    private const float AnimationFramesPerTick = 1.2f;
    private const float AnimatorTickMilliseconds = 42f;
    private const float StartTransitionProgress = 0.95f;

    public static PrimitiveResult Start(SimulationWorld world, XObject me, StackFrame frame, XObject anchor, RoutingSlotRequest slot, TreeStack? stack)
    {
        if (me is not XPerson person)
            throw new NotSupportedException("Sims.exe dispatches Go To Routing Slot only for a person.");
        if (person.ActiveActionCancelRequested && person[PersonData.NonInterruptible] == 0)
            return FailRouteBeforeStart(person, anchor, slot, frame, stack, failureReason: 4);
        if ((person[PersonData.IsGhost] & 1) != 0 && (slot.Flags & 0x0800u) == 0)
        {
            // Ghosts set routing flag 0x0800 to bypass wall and room boundaries during pathfinding.
            slot = new RoutingSlotRequest
            {
                X = slot.X,
                Y = slot.Y,
                Z = slot.Z,
                Standing = slot.Standing,
                Sitting = slot.Sitting,
                Ground = slot.Ground,
                Flags = slot.Flags | 0x0800u,
                CheckCandidateFootprintAgainstWall = slot.CheckCandidateFootprintAgainstWall,
                SnapTargetSlot = slot.SnapTargetSlot,
                MinProximity = slot.MinProximity,
                MaxProximity = slot.MaxProximity,
                OptimalProximity = slot.OptimalProximity,
                Gradient = slot.Gradient,
                Facing = slot.Facing,
                Resolution = slot.Resolution,
            };
        }
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

    /// <summary>Starts an explicit-position route using standard destination placement, distinct from
    /// player-queued movement handled through destination object interactions.</summary>
    internal static PrimitiveResult StartDirectPosition(SimulationWorld world, XPerson person, StackFrame frame,
        ObjectPosition destination, TreeStack? stack)
    {
        if (person.ActiveActionCancelRequested && person[PersonData.NonInterruptible] == 0)
        {
            person[PersonData.RouteResult] = 4;
            return PrimitiveResult.False;
        }
        var slot = (person[PersonData.IsGhost] & 1) != 0 ? new RoutingSlotRequest { Flags = 0x0800u } : new RoutingSlotRequest();
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

    /// <summary>Clears an interruptible direct-position route before replacement. Routes owned by running
    /// interaction trees or nested inside active portal functions must be cancelled by their owning stack
    /// to release portal state.</summary>
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
        person.AnimatorPoolTile = world.IsPoolTile(person.Position);
        var openedPortalId = (short)0;
        if (person.NativeRoute is { } parent && !ReferenceEquals(parent.OwnerFrame, frame))
        {
            person.NativeRouteParents.Add(parent);
            // Nested routing requests within a portal function cross the active portal directly to avoid
            // recursively triggering its transition tree.
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
            // Route records are not persisted in saved execution stacks, so restored continuations without
            // active route state evaluate to false.
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
        // Waits for the pushed chair Sit interaction to finish, completing the route if seated in the
        // target chair or failing with route error code 5.
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
        // Orientation adjustments ignore interaction cancellation flags, unlike standard slot routing.
        if (actionCancelled && !route.LookTowardsFollow)
        {
            // Cancellation during arrival retries until follow termination finishes so that an interrupted
            // wait cannot be treated as a successful arrival.
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
        if (route.EndFollowPending && !route.EndFollowOnArrival)
        {
            if (route.AnimatorFollowState is not (1 or -1))
            {
                CompleteEndFollow(world, person, route);
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

        // If already seated in the target chair, routing succeeds immediately; otherwise, it pushes the
        // chair's stand function (entry 0x1b) with the entry direction, failing with reason 6 if still
        // seated upon return.
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

        // Portal functions must be invoked from the character's current position before advancing across a
        // room boundary, preventing movement onto a closed door tile that would block the portal
        // interaction.
        var currentRoom = unchecked((ushort)person.RoomWord84);
        var destinationRoom = route.LookTowardsFollow ? currentRoom : route.DirectPositionRequest
            ? RoomKeyAt(world, route.Destination)
            : PortalRoomKey(anchor);

        // A completed portal callback does not guarantee the room boundary was crossed; if still outside
        // the target room, routing must select the next portal.
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

        var isGhost = (person[PersonData.IsGhost] & 1) != 0;
        if (!isGhost && !route.LookTowardsFollow && currentRoom != destinationRoom && route.OpenedPortalId == 0)
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

        // Re-evaluating multi-candidate routing slots upon entering the destination room allows scoring and
        // path testing from the post-portal entry position rather than pre-portal coordinates.
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

        // When routing to a chair, copies the chair-relative entry bit into route entry flags and pushes
        // the chair's Sit interaction (entry 0x1a) before following the route.
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
                allowCrossRoom: isGhost || route.DirectPositionRequest || person.NativeRouteParents.Count != 0))
        {
            Trace(world, person, anchor.Id, route.Destination, "path-rejected", "no route path to selected destination");
            return EndFollowAndClearRoute(world, person, route, frame, stack, failureReason: 1);
        }
        IReadOnlyList<RouteWaypoint> path = followedPath ?? plannedPath!;

        if (route.AnimatorFollowState == 2)
        {
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
                // When already standing at the destination, an in-place turn transition aligns with the
                // target heading before committing route entry flags and resuming execution.
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
            // Routes starting already at the destination must terminate locomotion state so subsequent
            // animation primitives do not report busy.
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
        // Ghosts and routes nested inside portal functions bypass room-transition portal checks.
        if (!isGhost && !route.LookTowardsFollow && person.NativeRouteParents.Count == 0 && currentRoom != nextRoom && currentRoom != SimsEngine.World.RoomMap.NoRoom && nextRoom != SimsEngine.World.RoomMap.NoRoom)
        {
            InitializeRoutePortalCosts(world, person, route);
            var routeSourceRoom = unchecked((ushort)person.RoomWord84);
            var routeDestinationRoom = route.DirectPositionRequest
                ? RoomKeyAt(world, route.Destination)
                : PortalRoomKey(anchor);
            // Reuses the active portal at room boundaries to prevent re-ranking from selecting another tile
            // of the same multi-tile door before it is marked used.
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
                // Standing adjustment turns only trigger from a standstill; active locomotion follows the
                // route polyline directly.
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
        // Locomotion start animations transition to their matching loop at 95% progress (walk start 19 to
        // loop 20, run start 22 to loop 21).
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
            person.AnimatorGaitEntryState = 1;
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
        route = route with { MotionPlaybackScale = playbackScale };
        var effectivePlaybackScale = playbackScale;
        var oneShot = turnPractice || route.MotionAnimationIndex is >= 4 and <= 18;
        // Root motion advances every tick even when bone poses are skipped, so animation phase calculation
        // must measure elapsed time since the last full evaluation when full updates resume.
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
        var movedX = motionX;
        var movedY = motionY;
        // Route arrival occurs once the projected waypoint is placed, even if continuous root coordinates
        // extend slightly beyond it.
        var activeFollowPlacement = route.AnimatorFollowState == 2;
        var arrived = activeFollowPlacement && (targetDistanceNow <= 0.0001f ||
                      (person.Position.X == targetX && person.Position.Y == targetY));

        var movedXInteger = FTileIntegerCoordinate(movedX);
        var movedYInteger = FTileIntegerCoordinate(movedY);
        var nearestRoutePoint = FindNearestRoutePoint(path, movedXInteger, movedYInteger);
        var movedPosition = nearestRoutePoint is { } nearest
            ? new ObjectPosition(nearest.X, nearest.Y, person.Position.Height)
            : new ObjectPosition(movedXInteger, movedYInteger, person.Position.Height);
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
        // Ground elevation is represented as height 0 in world positions, but corresponds to floor level 1
        // in placement checks.
        var canPlacePosition = movedPosition with
        {
            Height = movedPosition.Height == 0 ? 1 : movedPosition.Height,
        };
        // Continuous root heading drives avatar animation, while object direction stores the quantized
        // eight-way placement facing.
        if (route.AnimatorFollowState == 2)
            person[ObjectData.Direction] = QuantizeRootDirection(
                person.AnimatorRootDirection ?? person[ObjectData.Direction]);
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

        if (!accepted)
        {
            if (!route.LookTowardsFollow)
                person[PersonData.RouteResult] = 1;
            route = route with { MotionX = movedX, MotionY = movedY, FailureReason = 1 };
            if (route.AnimatorFollowState is 1 or -1)
            {
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
            // Discrete placement facing is only committed after standing adjustment turns complete,
            // allowing the animation to drive orientation during the turn.
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
            person.AnimatorControlState = person[ObjectData.WalkStyle] == 1 ? 11 : 6;
            person.AnimatorGaitEntryState = 0;
            person.AnimatorPoolTransitionState = 0;
            var startClip = ResolveWalkAnimation(world, person, nextAnimationIndex);
            if (startClip is { StandardDistance: > 0f })
            {
                // Converts standard animation distance to tile units using a 16/3 scale factor, rotating
                // displacement by (direction + 6) * π/4.
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
            return PrimitiveResult.True;
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

    // Root-only animation updates hold the visible skeletal pose fixed while advancing root displacement
    // along the route.
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

        if (person.AnimatorPoolTile)
        {
            var poolTable = child ? (ushort)(158 + (person[PersonData.Gender] & 1)) : (ushort)160;
            if (world.StandardWalkFallbackAnimationTable.TryGetValue((poolTable, standardIndex), out var poolClip))
                return poolClip;
        }
        if (clip is not null) return clip;

        // Resolves walk animations using global STR# 151 for children, 152 for adult males, and 153 for
        // adult females, falling back to STR# 150 if empty.
        var fallbackTable = child ? (ushort)151 : (ushort)(152 + (person[PersonData.Gender] & 1));
        if (world.StandardWalkFallbackAnimationTable.TryGetValue((fallbackTable, standardIndex), out clip)) return clip;
        return world.StandardWalkFallbackAnimationTable.GetValueOrDefault(((ushort)150, standardIndex));
    }

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

    /// <summary>Calculates route step distance by scaling standard animation distance by 16/3 and advancing
    /// proportionally by elapsed time over scaled animation duration.</summary>
    internal static float RouteStepDistance(float standardDistance, float skillDurationMilliseconds, float practiceScale,
        float elapsedMilliseconds)
    {
        var practiceDuration = skillDurationMilliseconds / practiceScale;
        var ticksPerPractice = practiceDuration / elapsedMilliseconds;
        var skillDistancePerTile = standardDistance / 3f;
        return (16f * skillDistancePerTile) / ticksPerPractice;
    }

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
            var dx = start.X - end.X;
            var dy = start.Y - end.Y;
            var lengthSquared = MathF.FusedMultiplyAdd(dy, dy, (float)dx * dx);
            if (lengthSquared == 0f) continue;

            var numerator = MathF.FusedMultiplyAdd(y - end.Y, dy, (float)(x - end.X) * dx);
            var amount = Math.Clamp(numerator / lengthSquared, 0f, 1f);
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

    internal static int FTileIntegerCoordinate(float coordinate) => (int)(coordinate + 0.5f);

    internal static int TickRootIntegerCoordinate(float coordinate) => (int)coordinate;

    /// <summary>Interpolates root position from start origin to target during the standing-start animation
    /// phase.</summary>
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

    /// <summary>Computes turn root coordinates by subtracting the angle-rotated standard root motion offset
    /// from the target position.</summary>
    internal static (float X, float Y) TurnRootTarget(float targetX, float targetY, float angle,
        System.Numerics.Vector3 standardOffset)
    {
        var cosine = MathF.Cos(angle);
        var sine = MathF.Sin(angle);
        return (targetX - (standardOffset.X * cosine + standardOffset.Y * sine),
            targetY - (standardOffset.X * sine + standardOffset.Y * cosine));
    }

    private static void CommitArrivalHeading(XPerson person, RouteState route)
    {
        var heading = ArrivalHeading(route);
        person[ObjectData.Direction] = SimsEngine.Character.AvatarDirection.ToObjectDirection(heading);
        person.AnimatorRootDirection = heading;
    }

    /// <summary>Determines arrival heading for completed routes. Slot facing modes -1 and -2 face along
    /// specified octants, while mode -3 preserves travel heading along the final path segment, falling back
    /// to initial orientation if zero-length.</summary>
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

    /// <summary>Selects body-turn animations or standing-turn offset adjustments based on heading error and
    /// distance thresholds to the route target.</summary>
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
        // Heading error thresholds are tightened for special body types and scaled by walking speed.
        if (distance <= 0.000001f && MathF.Abs(headingError) <= 0.000001f) return false;
        var angleThreshold = (specialBody ? 0.0625f : 0.25f) / person.AnimatorWalkingScale;
        if (MathF.Abs(headingError) >= angleThreshold || (route.AnimatorFollowState != 1 && distance >= maximumDistance))
            return TryBeginRouteBodyTurn(world, person, route, rootX, rootY, targetDirection, currentDirection,
                headingError);
        // During active following within angle threshold, distant targets enter locomotion rather than
        // standing turns.
        if (distance >= maximumDistance)
            return false;

        if (route.AnimatorFollowState == -1 && route.MotionTargetIsDestination && distance < snapDistance)
        {
            person.AnimatorRootX = route.MotionTargetX;
            person.AnimatorRootY = route.MotionTargetY;
            person.AnimatorRootDirection = targetDirection;
            person[ObjectData.Direction] = SimsEngine.Character.AvatarDirection.ToObjectDirection(targetDirection);
            person.NativeRoute = route with { MotionX = route.MotionTargetX, MotionY = route.MotionTargetY };
            return false;
        }

        // Turn selection uses standard 22.5-degree octant boundaries rather than 2:1 slope thresholds.
        var skill = StandingTurnAnimationForVector(dx, dy);
        var clip = ResolveWalkAnimation(world, person, skill);
        if (clip?.StandardRootMotionSamples is not { Count: > 1 }) return false;

        if (distance < snapDistance)
        {
            rootX = route.MotionTargetX;
            rootY = route.MotionTargetY;
            person.AnimatorRootX = rootX;
            person.AnimatorRootY = rootY;
        }

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

    private static bool TryBeginRouteBodyTurn(SimulationWorld world, XPerson person, RouteState route,
        float rootX, float rootY, float targetDirection, float currentDirection, float headingError)
    {
        var (skill, correction) = BodyTurnSkillForHeadingError(headingError);

        var clip = ResolveWalkAnimation(world, person, skill);
        if (clip?.StandardRootMotionSamples is not { Count: > 1 } samples) return false;
        var practiceDirection = currentDirection + headingError - correction;
        // Body turns during active path following rotate in place rather than translating along the
        // segment, whereas direct transitions interpolate toward the target.
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

    /// <summary>Maps heading error to the appropriate standing body turn animation and heading correction
    /// angle.</summary>
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

    /// <summary>Interpolates root position from anchor to offset-adjusted turn target during turn animation
    /// playback.</summary>
    internal static void TickRouteTurnRootInterpolation(SimulationWorld world, XPerson person)
    {
        var controlState = person.AnimatorControlState;
        if (controlState is not (4 or 5) || person.NativeRoute is not { } route ||
            (controlState == 5 && (route.MotionAnimationIndex is < 11 or > 18)) ||
            route.TurnRootOriginX is not { } originX || route.TurnRootOriginY is not { } originY ||
            route.TurnRootTargetX is not { } targetX || route.TurnRootTargetY is not { } targetY)
            return;
        person.AnimatorGaitEntryState = 0;
        person.AnimatorPoolTransitionState = 0;
        var turnClip = ResolveWalkAnimation(world, person, route.MotionAnimationIndex);
        var sampleCount = turnClip?.StandardRootMotionSamples?.Count ?? 0;
        if (sampleCount <= 0) return;
        var arrivalTurn = route.AnimatorFollowState == -1;
        if (arrivalTurn && !world.AvatarAnimationRootOnly && turnClip!.DurationMilliseconds > 0f)
        {
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
                // Clearing the turn animation index upon stopping prevents 180-degree turn animations from
                // replaying over committed directions.
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

    /// <summary>Selects a route lookahead sample point along the path from the current root position,
    /// advancing across reached segment endpoints.</summary>
    internal static RouteLookahead ResolveRouteLookahead(SimulationWorld world, XPerson person, RouteState route,
        float rootX, float rootY)
    {
        var motionPath = route.MotionPath;
        var activeTargetIndex = route.MotionTargetPathIndex;
        if (motionPath is { Count: > 1 } && (activeTargetIndex < 1 || activeTargetIndex >= motionPath.Count))
        {
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

        // Samples along the active route segment until exceeding half the standard animation stride scaled
        // by 16/3.
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

    internal static void TickRouteRootMovement(SimulationWorld world, XPerson person, TreeStack? stack)
    {
        if (person.AnimatorControlState is not (3 or 9 or 10)) return;
        if (person.NativeRoute is not { MotionTargetX: not int.MinValue, MotionTargetY: not int.MinValue } route)
            return;
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
            person.AnimatorRouteTransitionState = 1;
            if (person.AnimatorPoolTransitionState == 0)
            {
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
            stepX = storedLength > 1e-6f ? storedX * step / storedLength : 0f;
            stepY = storedLength > 1e-6f ? storedY * step / storedLength : 0f;
            arrival = true;
        }
        else
        {
            var deltaX = targetX - rootX;
            var deltaY = targetY - rootY;
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
            person.NativeRoute = route with
            {
                AnimatorFollowState = 3,
                FollowPlacementRejected = true,
                MotionX = rootX,
                MotionY = rootY,
            };
            return;
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

    /// <summary>Evaluates final-approach arrival when route preference (person data 9) is non-zero,
    /// triggering within 3 tiles for walking or 4.5 tiles for running.</summary>
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
        // Applies strict and inclusive 2:1 slope boundaries to classify direction octants.
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

    /// <summary>Initializes routing costs and allocates a route ID before portal selection, resetting
    /// portal costs while preserving used portal costs across subsequent selections in the same route.
    /// </summary>
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

    /// <summary>Terminates route following on failure and clears the active route, allowing nested portal
    /// callers to resume their parent route.</summary>
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

    // When route slot flag 0x4000 is set, pushes 'Route failure feedback' with the failure reason and
    // blocker object ID.
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
        if (!route.LookTowardsFollow && !route.AwaitingBeginFollow)
            world.PlaySoundEvent?.Invoke("end_route", person.Id, false, false);
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
        // Resolves portal crossing tree 0x0f from the selected portal's definition rather than its linked
        // multi-tile parts.
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
            // Halts active locomotion before running portal functions so that portal animation primitives
            // do not stall on active gait or turn states.
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

    internal static void MarkPortalRouteCostUsed(SimulationWorld world, short portalId, short functionObjectId,
        short routeId)
    {
        if (routeId <= 0) return;
        world.ObjectById(portalId)?.SetPortalRouteCost(routeId, 1101f);
        world.ObjectById(functionObjectId)?.SetPortalRouteCost(routeId, 1101f);
    }

    // Resolves portal function 0x0f across linked multi-tile portal parts, targeting the root portal as the
    // stack object.
    internal static (XObject FunctionObject, ITreeTable? Scope, short TreeId)? PortalFunction(SimulationWorld world, XObject portal)
    {
        // For objects with BuildModeType 1, multi-tile chain traversal filters linked parts by matching
        // OBJD InteractionGroup.
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
            if (!matchInteractionGroup && current.Id == portal.Id) return null;
            var treeId = SimulationWorld.FunctionTreeId(current, 0x0f);
            if (treeId != 0)
                return (current, current.TypeRecord?.BehaviorScope, treeId);
            nextId = current.MultiTileNextId != 0 || matchInteractionGroup
                ? current.MultiTileNextId
                : current.MultiTileMasterId;
            if (nextId == firstId) return null;
        }
        return null;
    }

    /// <summary>Records first-use room ranking (1001..1100) or marks an already-ranked portal as used
    /// (1101) on the portal and its linked parts.</summary>
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

        // Uses the portal's cached room attribute, which can differ from dynamic room map queries following
        // wall modifications.
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
        // Portals without a defined crossing function (table entry 0x0f) cannot authorize room transitions.
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
            var portalRoom = portal.RoomWord84 != -5
                ? unchecked((ushort)portal.RoomWord84)
                : RoomKeyAt(world, portal.Position);
            if (!PortalIsEligibleFromCurrentRoom(portalRoom, sourceCurrentRoom)) return false;
            return verticalCrossing ? portal[ObjectData.Direction] % 4 == 0 : portal[ObjectData.Direction] % 4 == 2;
        });
    }

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

    /// <summary>Caches the shortest transition distances from an origin portal to every reachable portal,
    /// keyed by route ID.</summary>
    internal static float ProjectPortalRouteDistance(SimulationWorld world, XObject origin, XObject target)
    {
        ProjectPortalRoutes(world, origin);
        var routeId = origin[ObjectData.RouteId];
        return routeId > 0 ? target.PortalRouteCostAt(routeId) : XObject.UnallocatedPortalRouteCost;
    }

    // Relaxes portal route distances using transition distance plus a 1.0 penalty per hop.
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
                var projected = currentCost + PortalTransitionDistanceCost(current, candidate) + 1f;
                if (projected < priorCost)
                    candidate.SetPortalRouteCost(routeId, projected);
            }
        }
    }

    /// <summary>Selects optimal portals by summing character-to-portal distance, projected portal-to-portal
    /// distance to the target room, and portal-to-target distance.</summary>
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

    // Compares stored room attributes directly to prevent dynamic map queries from resolving sentinel room
    // values into valid rooms.
    private static ushort PortalRoomKey(XObject item) => unchecked((ushort)item.RoomWord84);

    // Excludes unreachable portals (cost 10000) and previously used portals (cost 1101) while preserving
    // evaluation order for unprojected candidates (cost 10001).
    internal static XObject? SelectPreRankedPortal(IReadOnlyList<XObject> candidates, short routeId)
    {
        if (candidates.Count == 0) return null;
        if (routeId == 0) return null;
        var eligible = candidates.Where(candidate => candidate.PortalRouteCostAt(routeId) is not (10000f or 1101f)).ToArray();
        if (eligible.Length == 0) return null;
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

    // Calculates arrival facing: explicit facings 0..7 evaluate as (anchorDirection + Facing) & 7, facing
    // -3 retains current direction, and facings -1/-2 face toward or away from the anchor.
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

    // When slot flag 0x10000 is set, calculates the anchor origin by averaging coordinates across linked
    // multi-tile parts and truncating toward zero.
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

    // When slot flag 0x1000 is set, wall-probe validation tests the first enabled approach direction
    // relative to the anchor instead of current character facing.
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
        // Routing slots with no directional flags or with active snap targets preserve the exact authored
        // position.
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

            // The candidate pool is capped at 48 entries (reduced to 8 in large rooms), evaluating all
            // authored points before downsampling.
            placeable.Add(candidate);
            TraceCandidate(world, person, anchor, candidate.Position, true, slot.Flags);
        }

        // Downsamples overfull candidate pools by selecting random indices and taking the first remaining
        // candidate at or after each index without wrapping.
        var routeCandidates = SampleRouteCandidates(world, placeable, capacity);
        RouteGoalCandidate? bestGoal = null;
        RouteGoalCandidate? bestCrossFloorGoal = null;
        var bestPathCost = double.PositiveInfinity;
        var bestCrossFloorRouteCost = double.PositiveInfinity;
        Dictionary<(ushort CurrentRoom, ushort DestinationRoom), (XObject? Portal, float Cost)>? portalCache = null;
        FloorPartition? floorPartition = null;
        var floorPartitionAttempted = false;
        foreach (var candidate in routeCandidates)
        {
            // Cross-floor goals rely on stair portal transitions rather than direct floor-level tile
            // pathfinding, scoring candidates across floors normally.
            if (candidate.Position.Height != person.Position.Height)
            {
                var currentRoom = unchecked((ushort)person.RoomWord84);
                // Portal ranking evaluates the target object's room and position rather than the specific
                // candidate arrival tile within that room.
                var destinationRoom = PortalRoomKey(anchor);
                if (currentRoom == SimsEngine.World.RoomMap.NoRoom ||
                    destinationRoom == SimsEngine.World.RoomMap.NoRoom) continue;
                portalCache ??= new Dictionary<(ushort, ushort), (XObject? Portal, float Cost)>();
                var roomPair = (currentRoom, destinationRoom);
                if (!portalCache.TryGetValue(roomPair, out var portalPreview))
                {
                    portalPreview = PreviewPortalForDestination(world, person, anchor, currentRoom, destinationRoom);
                    portalCache[roomPair] = portalPreview;
                }
                var (selectedPortal, portalRouteCost) = portalPreview;
                // Cross-floor goal candidates are rejected if no valid portal transition can be found to
                // the destination room.
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
            if (!floorPartitionAttempted)
            {
                floorPartitionAttempted = true;
                floorPartition = TryBuildFloorPartition(world, person, anchor, allowCrossRoom: true, stack: stack);
            }
            if (floorPartition is not null &&
                TryPathWithPartition(floorPartition, candidate.Position, out _, out var pathCost))
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

    /// <summary>Previews destination portal ranking for candidate scoring without committing route IDs or
    /// mutating cached routing distances.</summary>
    private static (XObject? Portal, float Cost) PreviewPortalForDestination(SimulationWorld world,
        XPerson person, XObject destination, ushort currentRoom, ushort destinationRoom)
    {
        var savedPersonRouteId = person[ObjectData.RouteId];
        var portals = Portals(world).ToArray();
        var savedPortalState = new (short RouteId, KeyValuePair<short, float>[] Costs)[portals.Length];
        for (var i = 0; i < portals.Length; i++)
        {
            var portal = portals[i];
            savedPortalState[i] = (portal[ObjectData.RouteId], portal.PortalRouteCosts.ToArray());
        }
        try
        {
            var routeId = person[ObjectData.RouteId];
            if (routeId == 0) routeId = world.InitializePortalRouteCosts(person);
            if (routeId <= 0) return (null, float.PositiveInfinity);

            foreach (var portal in portals)
                portal.SetPortalRouteCost(routeId, XObject.UninitializedPortalRouteCost);
            var selected = SelectPortalForDestination(world, person, destination, currentRoom,
                destinationRoom, recordTrace: false);
            return (selected, selected?.PortalRouteCostAt(routeId) ?? float.PositiveInfinity);
        }
        finally
        {
            person[ObjectData.RouteId] = savedPersonRouteId;
            for (var i = 0; i < portals.Length; i++)
            {
                var portal = portals[i];
                var (routeId, costs) = savedPortalState[i];
                portal[ObjectData.RouteId] = routeId;
                portal.PortalRouteCosts.Clear();
                foreach (var (id, cost) in costs) portal.PortalRouteCosts.Add(id, cost);
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
        // Reduces candidate pool capacity from 48 to 8 when room area exceeds 250, where normal tiles
        // contribute two units and diagonal walls contribute one.
        var position = person.Position;
        if (world.HasRoomRecord(person.RoomWord84) && world.LotRoomMaps.TryGetValue(position.Height, out var map))
        {
            var room = unchecked((ushort)person.RoomWord84);
            if (map.RoomHalfArea(room, world.RoomIsOutside(person.RoomWord84)) > 250) return 8;
        }
        return 48;
    }

    // Samples enabled local directions between minimum and maximum bounds by resolution, scanning each row
    // from y to -y and rotating by anchor facing plus sector.
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
        // Pets cannot sit in Sim chairs and fall back to standing when sitting is requested; human Sims
        // score sitting multiplied by 10.
        var sittingScore = !petActor && slot.Sitting > 0 ? slot.Sitting * 10d : 0d;
        var effectiveStanding = petActor && slot.Sitting > 0 && slot.Standing == 0 ? 1 : slot.Standing;
        var floorPreference = Math.Max(effectiveStanding, slot.Ground) * 10d;
        // Suppresses standing goals on sit-only SLOT definitions where Sitting is positive while Standing
        // and Ground are zero.
        var allowStanding = floorPreference > 0d || sittingScore <= 0d;
        var resolution = sittingScore > 0d ? 16 : slot.Resolution;

        var directionMask = slot.Flags & 0xff;
        // Widens the sampled proximity bounds by one tile when chair scoring is active so side and rear
        // approach positions are scanned.
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
            // Rotates world-space wall sectors back by the anchor orientation before comparing them against
            // the routing slot's local direction bits.
            var worldWallMask = RouteMaskAt(world, roomMap, anchor.Position.X, anchor.Position.Y);
            var rotation = orientation & 7;
            wallMask = rotation == 0
                ? worldWallMask
                : ((worldWallMask >> rotation) | (worldWallMask << (8 - rotation))) & 0xff;
        }

        double ScoreCandidate(long distanceSquared, double posturePreference, int wallSector, ObjectPosition bonusTarget)
        {
            var radius = Math.Sqrt(distanceSquared);
            var roundedRadius = Math.Round(radius, MidpointRounding.ToEven);
            var gradientPenalty = Math.Round(
                Math.Abs(roundedRadius - slot.OptimalProximity) * slot.Gradient,
                MidpointRounding.ToEven);
            var score = 100d + posturePreference - gradientPenalty;
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
                // Decrements the row upper bound when the adjacent sector is enabled so shared boundary
                // candidates are evaluated only once.
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
        // Pedestrian portal anchors bypass inner lot bounds and route edge checks to allow routing onto
        // playable perimeter sidewalk tiles.
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
        // Route edges between the anchor origin and candidate tile are only checked when targeting a
        // different tile in the same room without the bypass flag. Candidates on the anchor's own tile
        // bypass edge checks.
        var anchorOrigin = destinationSlot is not null ? RouteAnchorOrigin(world, anchor, destinationSlot) : anchor.Position;
        var anchorTileX = anchorOrigin.X >> 4;
        var anchorTileY = anchorOrigin.Y >> 4;
        var personPosition = person.Position;
        var currentTileX = personPosition.X >> 4;
        var currentTileY = personPosition.Y >> 4;
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
                        world.LastRouteCandidateFailureReason = world.CaptureExecutionTrace
                            ? $"same-room route edge blocked; from={RouteBlockDetails(world, map, currentTileX, currentTileY, direction, fromBlocked)}; to={RouteBlockDetails(world, map, x, y, reverseDirection, toBlocked)}"
                            : "same-room route edge blocked";
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
                    var direction = candidateSector ?? DirectionToWaypoint((anchorOrigin.X, anchorOrigin.Y), (position.X, position.Y));
                    var reverseDirection = (direction + 4) & 7;
                    var fromBlocked = !radialSlot && world.BlocksRouteDirectionAt(map, anchorTileX, anchorTileY, direction);
                    var toBlocked = world.BlocksRouteDirectionAt(map, x, y, reverseDirection);
                    // When moving diagonally, the step is rejected if either adjacent cardinal wall at the
                    // candidate tile is blocked.
                    var diagonalCornerBlocked = (direction & 1) != 0 &&
                        (world.BlocksRouteDirectionAt(map, x, y, (reverseDirection + 1) & 7) ||
                         world.BlocksRouteDirectionAt(map, x, y, (reverseDirection + 7) & 7));
                    if (fromBlocked || toBlocked || diagonalCornerBlocked)
                    {
                        world.LastRouteCandidateFailureReason = world.CaptureExecutionTrace
                            ? $"same-room route edge blocked; from={RouteBlockDetails(world, map, anchorTileX, anchorTileY, direction, fromBlocked)}; to={RouteBlockDetails(world, map, x, y, reverseDirection, toBlocked)}; diagonalCornerBlocked={diagonalCornerBlocked}"
                            : "same-room route edge blocked";
                        return false;
                    }
                }
            }
        }
        var placementAccepted = destinationSlot is { SnapTargetSlot: >= 0 } snappedSlot
            ? world.CanPlaceRouteCandidate(person, position, anchor, snappedSlot.SnapTargetSlot)
            // Intermediate movement validation omits the destination reference object because non-solid
            // destination markers would otherwise cause placement checks to fail.
            : world.CanPlaceObject(person, position, stack);
        if (!placementAccepted)
        {
            if (world.CaptureExecutionTrace)
            {
                var referenceSlot = destinationSlot?.SnapTargetSlot ?? -1;
                var referencedHeightCode = referenceSlot >= 0 && referenceSlot < anchor.FootprintSlots.Count
                    ? anchor.FootprintSlots[referenceSlot].HeightCode
                    : (byte)0;
                world.LastRouteCandidateFailureReason =
                    $"person placement callback rejected candidate (error=0x{world.LastPlacementFailureCode:X2}; referenceSlot={referenceSlot}; heightCode={referencedHeightCode}; allowedHeight=0x{unchecked((ushort)person[ObjectData.AllowedHeightFlags]):X4}; overlapObject={world.LastPlacementBlockingObjectId})";
            }
            else
            {
                world.LastRouteCandidateFailureReason = "person placement callback rejected candidate";
            }
            blockerId = world.LastPlacementBlockingObjectId != 0
                ? world.LastPlacementBlockingObjectId
                : BlockingRootObjectId(world, person, anchor, x, y, position.Height);
            return false;
        }

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
        var tableCandidate = occupant is > 0 and <= short.MaxValue ? world.ObjectTableEntry((short)occupant) : null;
        var blocker = tableCandidate is not null &&
            tableCandidate.Position.Height == height &&
            (tableCandidate.Position.X >> 4) == x &&
            (tableCandidate.Position.Y >> 4) == y &&
            (tableCandidate.HasRequestKey ? tableCandidate.RequestKey : tableCandidate.ClockManagerIndex) == occupant
            ? tableCandidate
            : world.WalkOrder.FirstOrDefault(candidate => candidate.Position.Height == height &&
                (candidate.Position.X >> 4) == x && (candidate.Position.Y >> 4) == y &&
                (candidate.HasRequestKey ? candidate.RequestKey : candidate.ClockManagerIndex) == occupant);
        if (blocker is null || blocker.RuntimeClass == SimsEngine.Formats.ObjectClass.Portal) return 0;
        return blocker.ClockManagerIndex;
    }

    // Creates a temporary destination marker, installs an evasive routing slot, and queues an interaction
    // asking blocking characters along the path to step aside.
    internal static void AskPeopleOnRouteToMove(SimulationWorld world, XPerson requester, XObject anchor,
        IReadOnlyList<RouteWaypoint> path)
    {
        if (requester[PersonData.Sitting] != 0) return;
        var map = world.LotRoomMaps.GetValueOrDefault(requester.Position.Height);
        if (map is null) return;

        var reqLevel = requester.Position.Height;
        var reqRoom = unchecked((ushort)requester.RoomWord84);
        List<(XPerson Other, short CrossingDirection)>? blockers = null;
        for (var i = 0; i < world.WalkOrder.Count; i++)
        {
            if (world.WalkOrder[i] is not XPerson other) continue;
            if (ReferenceEquals(other, requester) || ReferenceEquals(other, anchor) ||
                other[PersonData.Sitting] != 0 ||
                other.Position.Height != reqLevel ||
                unchecked((ushort)other.RoomWord84) != reqRoom ||
                other.NativeRoute is not null ||
                GetDistanceToPrimitive.DistanceBetween(requester, other) > 3.0 ||
                world.ResolveParentContainer(other) is not null ||
                HasMoveAsideAction(other))
                continue;

            if (RouteCrossingDirection(other, path) is not { } crossingDirection)
                continue;

            blockers ??= [];
            blockers.Add((other, crossingDirection));
        }

        if (blockers is null) return;

        foreach (var (other, crossingDirection) in blockers)
        {
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

                var direction = crossingDirection;
                var firstDirection = (direction + 2) & 7;
                var secondDirection = (direction + 6) & 7;
                var moveSlot = new RoutingSlotRequest
                {
                    Flags = 0x2100u | (1u << firstDirection) | (1u << secondDirection),
                    MinProximity = 16,
                    MaxProximity = 80,
                    OptimalProximity = 16,
                    Standing = 1,
                    Facing = -3,
                };
                aside.RoutingSlots.Insert(0, moveSlot);
                if (SelectDestination(world, other, aside, moveSlot) is null)
                {
                    // Retries the evasion route with the direct forward direction enabled when
                    // perpendicular routes have no valid path.
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
                added = false; // Ownership of the temporary destination object transfers to the queued action.
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException)
            {
                if (added) world.Remove(aside.Id);
            }
        }
    }

    // Asks a blocking character to step aside by creating a destination at their position with cardinal
    // evasion slots and queuing a move-aside interaction.
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

    internal static short? RouteCrossingDirection(XPerson person, IReadOnlyList<RouteWaypoint> path)
    {
        var bounds = person.FootprintBounds;
        (int X, int Y)? entry = null;
        for (var segmentIndex = 0; segmentIndex + 1 < path.Count; segmentIndex++)
        {
            var start = path[segmentIndex];
            var end = path[segmentIndex + 1];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var steps = Math.Max(1, Math.Max(Math.Abs(dx), Math.Abs(dy)));
            for (var sample = 0; sample <= steps; sample++)
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
        // Projects along the final path segment's forward vector when the route terminates inside the
        // footprint.
        if (entry is not { } only || path.Count <= 1)
            return null;
        var terminal = (path[^1].X, path[^1].Y);
        return only != terminal
            ? DirectionToWaypoint(only, terminal)
            : DirectionToWaypoint((path[^2].X, path[^2].Y), terminal);
    }

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
        if (slot.SnapTargetSlot >= 0)
            // Converts the zero-based slot definition index to a one-based container relationship index.
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

    // Stores the selected slot's local sector bitmask into the character's route entry flags for scripts
    // that branch on approach direction. Exact goals and direct movement leave flags cleared.
    private static void SetRouteEntryFlags(SimulationWorld world, XPerson person, XObject anchor, RouteState route)
    {
        if (route.DirectPositionRequest) return;
        person[PersonData.RouteEntryFlags] = route.EntryFlags;
    }

    private static short LocalSectorEntryFlags(short worldSector, short orientation) =>
        unchecked((short)(1 << ((worldSector - orientation) & 7)));

    /// <summary>When exiting a seating object, projects each relative side enabled in chair entry flags one
    /// tile in world direction. Unblocked candidates compete by squared distance to the route target, and
    /// the nearest side bitmask is returned for the stand script.</summary>
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
                (world.ObjectTableEntry(world.LastPlacementBlockingObjectId)
                    ?? world.WalkOrder.FirstOrDefault(item => item.ClockManagerIndex == world.LastPlacementBlockingObjectId))
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

    private sealed class FloorPartition(
        SimulationWorld world,
        XPerson person,
        XObject anchor,
        TreeStack? stack,
        SimsEngine.World.RoomMap map,
        (int X, int Y) startTile,
        int startPointX,
        int startPointY,
        ushort startRoom,
        int startId,
        bool allowCrossRoom,
        List<PartitionCell> cells,
        List<(int Id, int X, int Y)>[] neighbors)
    {
        public SimulationWorld World { get; } = world;
        public XPerson Person { get; } = person;
        public XObject Anchor { get; } = anchor;
        public TreeStack? Stack { get; } = stack;
        public SimsEngine.World.RoomMap Map { get; } = map;
        public (int X, int Y) StartTile { get; } = startTile;
        public int StartPointX { get; } = startPointX;
        public int StartPointY { get; } = startPointY;
        public ushort StartRoom { get; } = startRoom;
        public int StartId { get; } = startId;
        public bool AllowCrossRoom { get; } = allowCrossRoom;
        public List<PartitionCell> Cells { get; } = cells;
        public List<(int Id, int X, int Y)>[] Neighbors { get; } = neighbors;
        public Dictionary<int, bool> PlacementCache { get; } = [];
    }

    private static FloorPartition? TryBuildFloorPartition(SimulationWorld world, XPerson person, XObject anchor,
        bool allowCrossRoom = true, TreeStack? stack = null,
        (int X, int Y)? startTileOverride = null, (int X, int Y)? startCellCenterOverride = null)
    {
        if (!world.LotRoomMaps.TryGetValue(person.Position.Height, out var map))
            return null;
        var startPointX = person.Position.X;
        var startPointY = person.Position.Y;
        var startTile = startTileOverride ?? (X: startPointX >> 4, Y: startPointY >> 4);
        if (startTile.X < 0 || startTile.Y < 0 || startTile.X >= map.Width || startTile.Y >= map.Height)
            return null;

        var startRoom = map.RoomAt(startTile.X, startTile.Y);
        if (startRoom == SimsEngine.World.RoomMap.NoRoom) return null;

        // Splits room rectangles at penalty rectangle boundaries, allowing sub-tile pathing nodes while
        // retaining tile-level room and blocker maps.
        var penaltyRects = new List<(int MinX, int MinY, int MaxX, int MaxY, int Cost, ushort Room)>();
        const int wallPenaltyInflate = 8;
        const int roomWallClearanceInflate = 7;

        // Inflates blocking wall segments so partition cells and path smoothing keep waypoints clear of
        // walls and fence corners. Ghosts bypass wall blocking.
        var ghostIgnoresWalls = (person[PersonData.IsGhost] & 1) != 0;
        if (!ghostIgnoresWalls)
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

        if (startTileOverride is { } retained && startCellCenterOverride is null)
        {
            if (startPointX == (retained.X + 1) * 16) startPointX--;
            if (startPointY == (retained.Y + 1) * 16) startPointY--;
        }
        var startLookupX = startCellCenterOverride?.X ?? startPointX;
        var startLookupY = startCellCenterOverride?.Y ?? startPointY;
        var personIsGhost = (person.FootprintFlags614 & 1) != 0 || (person.FootprintFlags634 & 1) != 0;
        var personHalfSpan = personIsGhost ? 0 : 3;
        var actorBounds = person.FootprintBoundsAt(new ObjectPosition(startLookupX, startLookupY, person.Position.Height));

        // Snapshots the object list because routing check scripts can mutate the lot object collection
        // during evaluation.
        if (!personIsGhost)
        foreach (var item in world.WalkOrder.ToArray())
        {
            if (item.Id == person.Id || item.Position.Height != person.Position.Height) continue;
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
            FootprintBounds bounds;
            if (item is XPerson itemPerson)
            {
                itemPerson.RefreshFootprintBounds();
                bounds = itemPerson.FootprintBounds;
            }
            else
            {
                bounds = SimulationWorld.PlacementFootprintBounds(item, item.Position);
            }
            var itemRoom = unchecked((ushort)item.RoomWord84);
            var intersectsActor = bounds.MinX < actorBounds.MaxX && actorBounds.MinX < bounds.MaxX &&
                                  bounds.MinY < actorBounds.MaxY && actorBounds.MinY < bounds.MaxY;
            if (!intersectsActor)
            {
                var obstacleInflate = item.TypeRecord?.ObjectType == 2 || routeCost == int.MaxValue || routeCost > 7
                    ? personHalfSpan
                    : 0;
                AddRouteCostRect(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY, routeCost, itemRoom,
                    obstacleInflate, obstacleInflate);
            }
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

        var cells = new List<PartitionCell>(map.Width * map.Height);
        for (var tileY = 0; tileY < map.Height; tileY++)
        for (var tileX = 0; tileX < map.Width; tileX++)
        {
            var room = map.RoomAt(tileX, tileY);
            if (room == SimsEngine.World.RoomMap.NoRoom || (!allowCrossRoom && room != startRoom)) continue;
            var minX = tileX * 16; var minY = tileY * 16;
            var maxX = minX + 16; var maxY = minY + 16;
            cells.Add(new PartitionCell(minX, minY, maxX, maxY, minX + 8, minY + 8, room, 0));
        }

        // Expands only the partition nodes intersected by each penalty rectangle to avoid projecting
        // boundaries across unrelated rooms.
        if (penaltyRects.Count > 0)
        {
            var nextCells = new List<PartitionCell>(cells.Count + 64);
            Span<int> xValues = stackalloc int[4];
            Span<int> yValues = stackalloc int[4];
            foreach (var rect in penaltyRects)
            {
                nextCells.Clear();
                for (var i = 0; i < cells.Count; i++)
                {
                    var cell = cells[i];
                    if ((rect.Room != SimsEngine.World.RoomMap.NoRoom && cell.Room != rect.Room) ||
                        rect.MinX >= cell.MaxX || rect.MaxX <= cell.MinX ||
                        rect.MinY >= cell.MaxY || rect.MaxY <= cell.MinY)
                    {
                        nextCells.Add(cell);
                        continue;
                    }

                    var xCount = SortedUnique4(cell.MinX, cell.MaxX,
                        Math.Clamp(rect.MinX, cell.MinX, cell.MaxX),
                        Math.Clamp(rect.MaxX, cell.MinX, cell.MaxX), xValues);
                    var yCount = SortedUnique4(cell.MinY, cell.MaxY,
                        Math.Clamp(rect.MinY, cell.MinY, cell.MaxY),
                        Math.Clamp(rect.MaxY, cell.MinY, cell.MaxY), yValues);
                    for (var y = 0; y < yCount - 1; y++)
                    for (var x = 0; x < xCount - 1; x++)
                    {
                        var minX = xValues[x]; var maxX = xValues[x + 1];
                        var minY = yValues[y]; var maxY = yValues[y + 1];
                        var centerX = minX + (maxX - minX) / 2;
                        var centerY = minY + (maxY - minY) / 2;
                        var inside = centerX >= rect.MinX && centerX < rect.MaxX &&
                                     centerY >= rect.MinY && centerY < rect.MaxY;
                        nextCells.Add(new PartitionCell(minX, minY, maxX, maxY, centerX, centerY,
                            cell.Room, inside ? Math.Max(cell.Cost, rect.Cost) : cell.Cost));
                    }
                }
                (cells, nextCells) = (nextCells, cells);
            }
        }

        var startId = cells.FindIndex(cell => startLookupX >= cell.MinX && startLookupX < cell.MaxX &&
            startLookupY >= cell.MinY && startLookupY < cell.MaxY);
        if (startId < 0) return null;

        var neighbors = new List<(int Id, int X, int Y)>[cells.Count];
        for (var i = 0; i < cells.Count; i++) neighbors[i] = [];
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

        static int SortedUnique4(int a, int b, int c, int d, Span<int> dst)
        {
            Span<int> raw = stackalloc int[4] { a, b, c, d };
            for (var i = 1; i < 4; i++)
            {
                var key = raw[i];
                var j = i - 1;
                while (j >= 0 && raw[j] > key)
                {
                    raw[j + 1] = raw[j];
                    j--;
                }
                raw[j + 1] = key;
            }
            var count = 0;
            for (var i = 0; i < 4; i++)
                if (count == 0 || raw[i] != dst[count - 1])
                    dst[count++] = raw[i];
            return count;
        }

        return new FloorPartition(world, person, anchor, stack, map, startTile, startPointX, startPointY,
            startRoom, startId, allowCrossRoom, cells, neighbors);
    }

    internal static bool TryPath(SimulationWorld world, XPerson person, ObjectPosition destination, XObject anchor,
        out List<RouteWaypoint> path, out double pathCost, bool directPositionRequest = false,
        RoutingSlotRequest? directSlot = null, TreeStack? stack = null,
        (int X, int Y)? startTileOverride = null, (int X, int Y)? startCellCenterOverride = null,
        bool allowCrossRoom = true)
    {
        path = [];
        pathCost = double.PositiveInfinity;
        if (destination.Height != person.Position.Height ||
            !world.LotRoomMaps.TryGetValue(person.Position.Height, out var map))
            return false;
        var endTile = (X: destination.X >> 4, Y: destination.Y >> 4);
        if (endTile.X < 0 || endTile.Y < 0 || endTile.X >= map.Width || endTile.Y >= map.Height)
            return false;
        var endRoom = map.RoomAt(endTile.X, endTile.Y);
        if (endRoom == SimsEngine.World.RoomMap.NoRoom) return false;
        var startTile = startTileOverride ?? (X: person.Position.X >> 4, Y: person.Position.Y >> 4);
        if (startTile.X < 0 || startTile.Y < 0 || startTile.X >= map.Width || startTile.Y >= map.Height)
            return false;
        if (!allowCrossRoom && map.RoomAt(startTile.X, startTile.Y) != endRoom)
            return false;
        if (TryBuildFloorPartition(world, person, anchor, allowCrossRoom, stack, startTileOverride, startCellCenterOverride) is not { } partition)
            return false;
        return TryPathWithPartition(partition, destination, out path, out pathCost);
    }

    private static bool TryPathWithPartition(FloorPartition partition, ObjectPosition destination,
        out List<RouteWaypoint> path, out double pathCost)
    {
        path = [];
        pathCost = double.PositiveInfinity;
        var world = partition.World;
        var person = partition.Person;
        if (destination.Height != person.Position.Height) return false;
        var map = partition.Map;
        var endTile = (X: destination.X >> 4, Y: destination.Y >> 4);
        if (endTile.X < 0 || endTile.Y < 0 || endTile.X >= map.Width || endTile.Y >= map.Height)
            return false;
        var endRoom = map.RoomAt(endTile.X, endTile.Y);
        if (endRoom == SimsEngine.World.RoomMap.NoRoom) return false;
        if (!partition.AllowCrossRoom && partition.StartRoom != endRoom) return false;

        var cells = partition.Cells;
        var startId = partition.StartId;
        var endId = cells.FindIndex(cell => destination.X >= cell.MinX && destination.X < cell.MaxX &&
            destination.Y >= cell.MinY && destination.Y < cell.MaxY);
        if (endId < 0) return false;

        var neighbors = partition.Neighbors;
        var anchor = partition.Anchor;
        var stack = partition.Stack;
        var startTile = partition.StartTile;
        var startPointX = partition.StartPointX;
        var startPointY = partition.StartPointY;
        var startRoom = partition.StartRoom;

        var nodeCount = cells.Count;
        var queue = new PriorityQueue<int, (double Estimate, double Cost, long Sequence)>();
        var costs = ArrayPool<double>.Shared.Rent(nodeCount);
        var previous = ArrayPool<int>.Shared.Rent(nodeCount);
        var visited = ArrayPool<byte>.Shared.Rent(nodeCount);
        try
        {
            costs.AsSpan(0, nodeCount).Fill(double.PositiveInfinity);
            visited.AsSpan(0, nodeCount).Clear();
            costs[startId] = 0d;
            var visitedCount = 0;
            long sequence = 0;
            var rejectedPlacements = 0;
            var rejectedEdges = 0;
            var placementCache = partition.PlacementCache;
            var anchorRawBounds = anchor is XPerson anchorPerson
                ? anchorPerson.FootprintBoundsAt(anchor.Position)
                : SimulationWorld.PlacementFootprintBounds(anchor, anchor.Position);
            var anchorInflate = (person.FootprintFlags614 & 1) != 0 || (person.FootprintFlags634 & 1) != 0 ? 0 : 3;
            var ghostIgnoresWalls = (person[PersonData.IsGhost] & 1) != 0;
            var anchorInfMinX = anchorRawBounds.MinX - anchorInflate;
            var anchorInfMinY = anchorRawBounds.MinY - anchorInflate;
            var anchorInfMaxX = anchorRawBounds.MaxX + anchorInflate;
            var anchorInfMaxY = anchorRawBounds.MaxY + anchorInflate;
            var destinationInAnchorCollar = destination.X >= anchorInfMinX && destination.X < anchorInfMaxX &&
                                            destination.Y >= anchorInfMinY && destination.Y < anchorInfMaxY;
            bool IsAnchorCollarForGoal(int id)
            {
                if (!destinationInAnchorCollar) return false;
                var c = cells[id];
                return c.CenterX >= anchorInfMinX && c.CenterX < anchorInfMaxX &&
                       c.CenterY >= anchorInfMinY && c.CenterY < anchorInfMaxY;
            }
            bool CanVisit(int id)
            {
                if (id == startId || id == endId) return true;
                if (IsAnchorCollarForGoal(id))
                {
                    var collarCell = cells[id];
                    var collarCandidate = new ObjectPosition(collarCell.CenterX, collarCell.CenterY, person.Position.Height);
                    if (Walkable(world, person, anchor, collarCandidate, writeCandidateResults: false, stack: stack))
                        return true;
                    return world.LastPlacementFailureCode == 0x0b &&
                           world.LastPlacementBlockingObjectId == anchor.ClockManagerIndex;
                }
                if (placementCache.TryGetValue(id, out var allowed)) return allowed;
                var cell = cells[id];
                var candidate = new ObjectPosition(cell.CenterX, cell.CenterY, person.Position.Height);
                allowed = Walkable(world, person, anchor, candidate, writeCandidateResults: false, stack: stack);
                placementCache[id] = allowed;
                if (!allowed) rejectedPlacements++;
                return allowed;
            }
            queue.Enqueue(startId, (DistanceBetweenNodeCenters(cells[startId], cells[endId]), 0d, sequence++));

            while (queue.TryDequeue(out var currentId, out var queuedPriority))
            {
                if (queuedPriority.Cost != costs[currentId] || visited[currentId] != 0) continue;
                visited[currentId] = 1;
                visitedCount++;
                var currentCost = costs[currentId];
                if (currentId == endId)
                {
                    pathCost = currentCost;
                    break;
                }
                var current = cells[currentId];
                foreach (var (nextId, interfaceX, interfaceY) in neighbors[currentId])
                {
                    if (nextId < 0 || visited[nextId] != 0) continue;
                    var next = cells[nextId];
                    if (nextId != endId && !IsAnchorCollarForGoal(nextId) && next.Cost == int.MaxValue) { rejectedEdges++; continue; }
                    if (!CanVisit(nextId)) continue;
                    var fromTile = (X: current.CenterX >> 4, Y: current.CenterY >> 4);
                    var toTile = (X: next.CenterX >> 4, Y: next.CenterY >> 4);
                    if (!ghostIgnoresWalls)
                    {
                        if (fromTile == toTile)
                        {
                            if (current.Room != next.Room) { rejectedEdges++; continue; }
                        }
                        else if (!CanStep(world, map, fromTile, toTile))
                        {
                            rejectedEdges++;
                            continue;
                        }
                    }

                    var dx1 = interfaceX - current.CenterX;
                    var dy1 = interfaceY - current.CenterY;
                    var dx2 = next.CenterX - interfaceX;
                    var dy2 = next.CenterY - interfaceY;
                    var edgeDistance = Math.Sqrt(dx1 * dx1 + dy1 * dy1) + Math.Sqrt(dx2 * dx2 + dy2 * dy2);
                    var edgeCost = ((current.Cost + 2L + next.Cost) >> 1) * edgeDistance;
                    var nextCost = currentCost + edgeCost;
                    if (costs[nextId] <= nextCost) continue;
                    costs[nextId] = nextCost;
                    previous[nextId] = currentId;
                    queue.Enqueue(nextId, (nextCost + DistanceBetweenNodeCenters(next, cells[endId]), nextCost, sequence++));
                }
            }

            if (world.CaptureExecutionTrace)
            {
                var roomCounts = new Dictionary<ushort, int>();
                for (var id = 0; id < nodeCount; id++)
                {
                    if (visited[id] == 0) continue;
                    var room = cells[id].Room;
                    roomCounts[room] = roomCounts.TryGetValue(room, out var count) ? count + 1 : 1;
                }
                var visitedRooms = string.Join(",", roomCounts.OrderBy(group => group.Key).Select(group => $"{group.Key}:{group.Value}"));
                world.RouteSearchTrace.Add(new SimulationRouteSearchTrace(person.Id, destination, startTile, endTile,
                    startRoom, endRoom, visitedCount, rejectedPlacements, rejectedEdges, visitedRooms));
            }
            if (!double.IsFinite(pathCost)) return false;

            var reverse = new List<int> { endId };
            while (reverse[^1] != startId) reverse.Add(previous[reverse[^1]]);
            reverse.Reverse();
            var smoothedPoints = new List<(int CenterX, int CenterY)>(reverse.Count);
            for (var i = 0; i < reverse.Count; i++)
            {
                var cell = cells[reverse[i]];
                smoothedPoints.Add((cell.CenterX, cell.CenterY));
            }
            SmoothPartitionPath(cells, reverse, smoothedPoints);
            // Retains the exact starting coordinates at index zero, emits representative points for linked
            // partition cells, and appends the target cell midpoint as the route terminus.
            path.Add(new RouteWaypoint(startTile.X, startTile.Y, startPointX, startPointY,
                cells[startId].CenterX, cells[startId].CenterY, false));
            for (var i = 1; i < reverse.Count; i++)
            {
                var cell = cells[reverse[i]];
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
        }
        finally
        {
            ArrayPool<double>.Shared.Return(costs);
            ArrayPool<int>.Shared.Return(previous);
            ArrayPool<byte>.Shared.Return(visited);
        }

        RouteWaypoint ToWaypoint((int CenterX, int CenterY) point, PartitionCell cell) =>
            new(
                Math.Clamp(point.CenterX >> 4, cell.MinX >> 4, Math.Max(cell.MinX >> 4, (cell.MaxX - 1) >> 4)),
                Math.Clamp(point.CenterY >> 4, cell.MinY >> 4, Math.Max(cell.MinY >> 4, (cell.MaxY - 1) >> 4)),
                point.CenterX, point.CenterY, cell.CenterX, cell.CenterY, false);
    }

    private static void SmoothPartitionPath(IReadOnlyList<PartitionCell> cells, IReadOnlyList<int> nodeIds,
        IList<(int CenterX, int CenterY)> points)
    {
        if (nodeIds.Count < 2) return;

        // Smooths paths by testing four axis-aligned offsets within interface rectangles to minimize
        // distance to adjacent points. The search radius scales down each pass until positions converge.
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
            // Room transitions require routing through a portal because static wall blocker masks remain
            // active at door locations.
            return fromRoom != SimsEngine.World.RoomMap.NoRoom &&
                   toRoom != SimsEngine.World.RoomMap.NoRoom &&
                   HasPortalForStep(world, map.Level, fromRoom, toRoom, from, to);
        }
        if (world.BlocksRouteDirectionAt(map, from.X, from.Y, direction) ||
            world.BlocksRouteDirectionAt(map, to.X, to.Y, (direction + 4) & 7)) return false;

        // Avoids one-sided cardinal corner gating to prevent valid diagonal entries from becoming one-way
        // when the reverse side has a blocked orthogonal edge.
        return fromRoom != SimsEngine.World.RoomMap.NoRoom;
    }

    private static IEnumerable<XObject> Portals(SimulationWorld world) =>
        world.WalkOrder.Where(item => item.RuntimeClass == SimsEngine.Formats.ObjectClass.Portal &&
            SimulationWorld.FunctionTreeId(item, 0x0f) != 0);

}
