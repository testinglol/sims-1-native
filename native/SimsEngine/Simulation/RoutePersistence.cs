namespace SimsEngine.Simulation;

public readonly record struct RouteWaypointState(int TileX, int TileY, int X, int Y,
    int CellCenterX, int CellCenterY, bool IsDestination);

/// <summary>Saves active route state alongside lot data, allowing asynchronous movement callbacks to
/// reconnect to their restored execution frame.</summary>
public sealed record RouteStateSnapshot(
    short AnchorObjectId, ObjectPosition Destination, RoutingSlotRequest Slot,
    short PendingPortalId, int PortalCallerDepth, int OwnerFrameIndex, short OpenedPortalId,
    short ArrivalFacing, short PendingPortalFunctionObjectId, short OpenedPortalFunctionObjectId,
    short LastUsedPortalId, short LastUsedPortalFunctionObjectId, bool DirectPositionRequest,
    short[]? UsedPortalIds, int DiagnosticSnapshotCount, int WaypointCooldownTicks,
    int WaypointTileX, int WaypointTileY, int WaypointX, int WaypointY,
    int WaypointCellCenterX, int WaypointCellCenterY,
    int MotionTargetTileX, int MotionTargetTileY, int MotionTargetX, int MotionTargetY,
    bool MotionTargetIsDestination, float MotionFrame, float MotionPracticeFrame, float MotionX, float MotionY,
    float MotionPlaybackScale, short MotionAnimationIndex, short MotionDirection, bool StandChecked,
    int PendingStandDepth, RouteWaypointState[]? MotionPath, RouteWaypointState[]? FollowPath,
    int FollowWaypointIndex, bool LookTowardsFollow, int AnimatorFollowState = 1, bool EndFollowPending = false,
    bool FollowPlacementRejected = false, int LocomotionFadeEndMils = 0,
    float? StartRootOriginX = null, float? StartRootOriginY = null,
    float? StartRootTargetX = null, float? StartRootTargetY = null,
    float? TurnRootOriginX = null, float? TurnRootOriginY = null,
    float? TurnRootTargetX = null, float? TurnRootTargetY = null,
    float? TurnRootSavedDirection = null, float? TurnRootPracticeScale = null,
    float? TurnRootStartDirection = null, float? TurnRootPracticeDirection = null,
    bool PoolSwimExitPending = false, float? PoolSwimExitRootX = null, float? PoolSwimExitRootY = null,
    float? PoolSwimExitDirection = null, bool BeginFollowPendingTick = false, bool EndFollowOnArrival = false,
    int MotionTargetPathIndex = -1, bool AwaitingBeginFollow = false,
    float? TransitionTargetX = null, float? TransitionTargetY = null, float? LookTowardsHeading = null);

/// <summary>Captures the active route and any parent routes suspended while portal behavior executes nested
/// on the call stack.</summary>
public sealed record PersonRouteSnapshot(RouteStateSnapshot? Current,
    RouteStateSnapshot[] Parents);

public sealed record PersonAnimationRuntimeSnapshot(short PersonId,
    AvatarAnimationPlaybackState? Primary, AvatarAnimationPlaybackState? Background,
    AvatarAnimationPlaybackState? Carry, string[] InteractionAttachments,
    PersonRouteSnapshot? Route, short BackgroundPracticePersonState = -1,
    float? AnimatorWalkingScale = null, short[]? PendingAnimationEvents = null,
    int? AnimatorControlState = null, float? AnimatorRootX = null, float? AnimatorRootY = null,
    int? AnimatorPoolTransitionState = null, int? AnimatorGaitEntryState = null,
    int? AnimatorMilliseconds = null, bool? AnimatorPoolTile = null, float? AnimatorRootDirection = null,
    float? AnimatorDisplacementX = null, float? AnimatorDisplacementY = null,
    float? AnimatorDisplacementDirection = null, int? AnimatorRouteTransitionState = null,
    int? AnimatorStateTailField198 = null, int? AnimatorCurrentSkill = null,
    bool? AnimatorPracticeScaleEnabled = null);

public static class RoutePersistence
{
    public static void RestorePendingAnimationEvents(XPerson person, short[]? savedEvents)
    {
        ArgumentNullException.ThrowIfNull(person);
        person.PendingAnimationEvents.Clear();
        if (savedEvents is not null)
        {
            person.PendingAnimationEvents.AddRange(savedEvents);
            return;
        }
        if (person.AnimationPlayback is not { } legacyPrimary) return;
        person.PendingAnimationEvents.AddRange(legacyPrimary.EventQueue);
        legacyPrimary.EventQueue.Clear();
    }

    public static float RestoreAnimatorWalkingScale(float? savedScale, PersonRouteSnapshot? routes) =>
        savedScale ?? routes?.Current?.MotionPlaybackScale ?? routes?.Parents.LastOrDefault()?.MotionPlaybackScale ?? 1f;

    public static PersonRouteSnapshot? Capture(XPerson person, TreeStack? stack)
    {
        ArgumentNullException.ThrowIfNull(person);
        if (person.NativeRoute is null && person.NativeRouteParents.Count == 0) return null;
        return new PersonRouteSnapshot(
            person.NativeRoute is null ? null : CaptureRoute(person.NativeRoute, stack),
            person.NativeRouteParents.Select(route => CaptureRoute(route, stack)).ToArray());
    }

    public static void Restore(XPerson person, TreeStack? stack, PersonRouteSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(person);
        person.NativeRoute = snapshot?.Current is null ? null : RestoreRoute(snapshot.Current, stack);
        person.NativeRouteParents.Clear();
        person.AnimatorLocomotionFadeGate = person.NativeRoute?.LocomotionFadeEndMils > person.AnimatorMilliseconds ? 1 : 0;
        if (snapshot is null) return;
        foreach (var route in snapshot.Parents ?? []) person.NativeRouteParents.Add(RestoreRoute(route, stack));
    }

    private static RouteStateSnapshot CaptureRoute(RouteState route, TreeStack? stack)
    {
        var ownerFrameIndex = -1;
        if (route.OwnerFrame is { } ownerFrame)
        {
            ownerFrameIndex = stack?.Frames.FindIndex(frame => ReferenceEquals(frame, ownerFrame)) ?? -1;
            if (ownerFrameIndex < 0)
                throw new InvalidDataException("An active native route is not owned by a saved Sim behavior frame.");
        }
        return new RouteStateSnapshot(
            route.AnchorObjectId, route.Destination, CloneSlot(route.Slot), route.PendingPortalId,
            route.PortalCallerDepth, ownerFrameIndex, route.OpenedPortalId, route.ArrivalFacing,
            route.PendingPortalFunctionObjectId, route.OpenedPortalFunctionObjectId,
            route.LastUsedPortalId, route.LastUsedPortalFunctionObjectId, route.DirectPositionRequest,
            route.UsedPortalIds?.ToArray(), route.DiagnosticSnapshotCount, route.WaypointCooldownTicks,
            route.WaypointTileX, route.WaypointTileY, route.WaypointX, route.WaypointY,
            route.WaypointCellCenterX, route.WaypointCellCenterY,
            route.MotionTargetTileX, route.MotionTargetTileY, route.MotionTargetX, route.MotionTargetY,
            route.MotionTargetIsDestination, route.MotionFrame, route.MotionPracticeFrame, route.MotionX, route.MotionY,
            route.MotionPlaybackScale, route.MotionAnimationIndex, route.MotionDirection, route.StandChecked,
            route.PendingStandDepth, ToSavedWaypoints(route.MotionPath), ToSavedWaypoints(route.FollowPath),
            route.FollowWaypointIndex, route.LookTowardsFollow, route.AnimatorFollowState, route.EndFollowPending,
            route.FollowPlacementRejected, route.LocomotionFadeEndMils,
            route.StartRootOriginX, route.StartRootOriginY, route.StartRootTargetX, route.StartRootTargetY,
            route.TurnRootOriginX, route.TurnRootOriginY, route.TurnRootTargetX, route.TurnRootTargetY,
            route.TurnRootSavedDirection, route.TurnRootPracticeScale,
            route.TurnRootStartDirection, route.TurnRootPracticeDirection, route.PoolSwimExitPending,
            route.PoolSwimExitRootX, route.PoolSwimExitRootY, route.PoolSwimExitDirection, route.BeginFollowPendingTick,
            route.EndFollowOnArrival, route.MotionTargetPathIndex, route.AwaitingBeginFollow,
            route.TransitionTargetX, route.TransitionTargetY, route.LookTowardsHeading);
    }

    private static RouteState RestoreRoute(RouteStateSnapshot snapshot, TreeStack? stack)
    {
        StackFrame? ownerFrame = null;
        if (snapshot.OwnerFrameIndex >= 0)
        {
            if (stack is null || snapshot.OwnerFrameIndex >= stack.Frames.Count)
                throw new InvalidDataException("A saved native route references a missing Sim behavior frame.");
            ownerFrame = stack.Frames[snapshot.OwnerFrameIndex];
        }
        else if (snapshot.OwnerFrameIndex != -1)
        {
            throw new InvalidDataException("A saved native route has an invalid behavior-frame index.");
        }

        return new RouteState(snapshot.AnchorObjectId, snapshot.Destination, CloneSlot(snapshot.Slot),
            snapshot.PendingPortalId, snapshot.PortalCallerDepth, ownerFrame, snapshot.OpenedPortalId,
            snapshot.ArrivalFacing, snapshot.PendingPortalFunctionObjectId, snapshot.OpenedPortalFunctionObjectId,
            snapshot.LastUsedPortalId, snapshot.LastUsedPortalFunctionObjectId, snapshot.DirectPositionRequest,
            snapshot.UsedPortalIds?.ToArray(), snapshot.DiagnosticSnapshotCount, snapshot.WaypointCooldownTicks,
            snapshot.WaypointTileX, snapshot.WaypointTileY, snapshot.WaypointX, snapshot.WaypointY,
            snapshot.WaypointCellCenterX, snapshot.WaypointCellCenterY, snapshot.MotionTargetTileX,
            snapshot.MotionTargetTileY, snapshot.MotionTargetX, snapshot.MotionTargetY,
            snapshot.MotionTargetIsDestination, snapshot.MotionFrame, snapshot.MotionPracticeFrame, snapshot.MotionX, snapshot.MotionY,
            snapshot.MotionPlaybackScale, snapshot.MotionAnimationIndex, snapshot.MotionDirection,
            snapshot.StandChecked, snapshot.PendingStandDepth, snapshot.AnimatorFollowState, snapshot.EndFollowPending,
            snapshot.FollowPlacementRejected, snapshot.LocomotionFadeEndMils)
        {
            MotionPath = ToWaypoints(snapshot.MotionPath),
            MotionTargetPathIndex = snapshot.MotionTargetPathIndex,
            FollowPath = ToWaypoints(snapshot.FollowPath),
            FollowWaypointIndex = snapshot.FollowWaypointIndex,
            LookTowardsFollow = snapshot.LookTowardsFollow,
            BeginFollowPendingTick = snapshot.BeginFollowPendingTick,
            AwaitingBeginFollow = snapshot.AwaitingBeginFollow,
            EndFollowOnArrival = snapshot.EndFollowOnArrival,
            StartRootOriginX = snapshot.StartRootOriginX,
            StartRootOriginY = snapshot.StartRootOriginY,
            StartRootTargetX = snapshot.StartRootTargetX,
            StartRootTargetY = snapshot.StartRootTargetY,
            TurnRootOriginX = snapshot.TurnRootOriginX,
            TurnRootOriginY = snapshot.TurnRootOriginY,
            TurnRootTargetX = snapshot.TurnRootTargetX,
            TurnRootTargetY = snapshot.TurnRootTargetY,
            TurnRootSavedDirection = snapshot.TurnRootSavedDirection,
            TurnRootPracticeScale = snapshot.TurnRootPracticeScale,
            TurnRootStartDirection = snapshot.TurnRootStartDirection,
            TurnRootPracticeDirection = snapshot.TurnRootPracticeDirection,
            PoolSwimExitPending = snapshot.PoolSwimExitPending,
            PoolSwimExitRootX = snapshot.PoolSwimExitRootX,
            PoolSwimExitRootY = snapshot.PoolSwimExitRootY,
            PoolSwimExitDirection = snapshot.PoolSwimExitDirection,
            TransitionTargetX = snapshot.TransitionTargetX,
            TransitionTargetY = snapshot.TransitionTargetY,
            LookTowardsHeading = snapshot.LookTowardsHeading,
        };
    }

    private static RouteWaypointState[]? ToSavedWaypoints(IReadOnlyList<RouteExecutor.RouteWaypoint>? path) =>
        path?.Select(point => new RouteWaypointState(point.TileX, point.TileY, point.X, point.Y,
            point.CellCenterX, point.CellCenterY, point.IsDestination)).ToArray();

    private static IReadOnlyList<RouteExecutor.RouteWaypoint>? ToWaypoints(RouteWaypointState[]? path) =>
        path?.Select(point => new RouteExecutor.RouteWaypoint(point.TileX, point.TileY, point.X, point.Y,
            point.CellCenterX, point.CellCenterY, point.IsDestination)).ToArray();

    private static RoutingSlotRequest CloneSlot(RoutingSlotRequest source) => new()
    {
        X = source.X, Y = source.Y, Z = source.Z, Standing = source.Standing, Sitting = source.Sitting,
        Ground = source.Ground, Flags = source.Flags,
        CheckCandidateFootprintAgainstWall = source.CheckCandidateFootprintAgainstWall,
        SnapTargetSlot = source.SnapTargetSlot, MinProximity = source.MinProximity,
        MaxProximity = source.MaxProximity, OptimalProximity = source.OptimalProximity,
        Gradient = source.Gradient, Facing = source.Facing, Resolution = source.Resolution,
    };
}
