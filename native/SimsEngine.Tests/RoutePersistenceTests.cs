using System.Text.Json;
using System.Text.Json.Serialization;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class RoutePersistenceTests
{
    private sealed class TestStack : TreeStack
    {
        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction) => PrimitiveResult.False;
    }

    [Fact]
    public void PerSimAnimationRuntimeMetadataRoundTripsPracticeScaleFlagAndAcceptsLegacyRecords()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        var runtime = new PersonAnimationRuntimeSnapshot(100, null, null, null, [], null,
            BackgroundPracticePersonState: -1, AnimatorWalkingScale: 1.6f,
            PendingAnimationEvents: [17, 23], AnimatorControlState: 7,
            AnimatorRootX: 17.25f, AnimatorRootY: 18.5f, AnimatorPoolTransitionState: 1,
            AnimatorGaitEntryState: 1, AnimatorMilliseconds: 1234, AnimatorPoolTile: true,
            AnimatorRootDirection: 5.25f, AnimatorDisplacementX: 0.25f, AnimatorDisplacementY: -0.5f,
            AnimatorDisplacementDirection: 0.125f, AnimatorRouteTransitionState: 3,
            AnimatorStateTailField198: 1, AnimatorCurrentSkill: 21,
            AnimatorPracticeScaleEnabled: true);
        var json = JsonSerializer.Serialize(runtime, options);
        var restored = JsonSerializer.Deserialize<PersonAnimationRuntimeSnapshot>(json, options);

        Assert.Equal(1.6f, restored!.AnimatorWalkingScale);
        Assert.Equal(7, restored.AnimatorControlState);
        Assert.Equal(17.25f, restored.AnimatorRootX);
        Assert.Equal(18.5f, restored.AnimatorRootY);
        Assert.Equal(1, restored.AnimatorPoolTransitionState);
        Assert.Equal(1, restored.AnimatorGaitEntryState);
        Assert.Equal(1234, restored.AnimatorMilliseconds);
        Assert.True(restored.AnimatorPoolTile);
        Assert.Equal(5.25f, restored.AnimatorRootDirection);
        Assert.Equal(0.25f, restored.AnimatorDisplacementX);
        Assert.Equal(-0.5f, restored.AnimatorDisplacementY);
        Assert.Equal(0.125f, restored.AnimatorDisplacementDirection);
        Assert.Equal(3, restored.AnimatorRouteTransitionState);
        Assert.Equal(1, restored.AnimatorStateTailField198);
        Assert.Equal(21, restored.AnimatorCurrentSkill);
        Assert.True(restored.AnimatorPracticeScaleEnabled);
        Assert.Equal(new short[] { 17, 23 }, restored.PendingAnimationEvents);
        Assert.Empty(restored.InteractionAttachments);

        const string legacyJson = "{\"personId\":100,\"interactionAttachments\":[]}";
        var legacy = JsonSerializer.Deserialize<PersonAnimationRuntimeSnapshot>(legacyJson, options);
        Assert.Null(legacy!.AnimatorWalkingScale);
        Assert.Null(legacy.AnimatorRootX);
        Assert.Null(legacy.AnimatorCurrentSkill);
        Assert.Null(legacy.AnimatorRootY);
        Assert.Null(legacy.AnimatorPoolTransitionState);
        Assert.Null(legacy.AnimatorGaitEntryState);
        Assert.Null(legacy.AnimatorRouteTransitionState);
        Assert.Null(legacy.AnimatorMilliseconds);
        Assert.Null(legacy.AnimatorPoolTile);
        Assert.Null(legacy.AnimatorRootDirection);
        Assert.Null(legacy.AnimatorDisplacementX);
        Assert.Null(legacy.AnimatorDisplacementY);
        Assert.Null(legacy.AnimatorDisplacementDirection);
        Assert.Null(legacy.AnimatorStateTailField198);
        Assert.Equal(-1, legacy.BackgroundPracticePersonState);

        var person = new XPerson(100, PrimitiveTestWorld.Definition(XPerson.PersonType));
        Assert.Equal(0, person.AnimatorPoolTransitionState);
        Assert.Equal(0, person.AnimatorGaitEntryState);
    }

    [Fact]
    public void LegacyPrimaryChannelEventsMigrateToTheSharedAnimatorQueue()
    {
        var person = new XPerson(100, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            AnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("legacy", 4, []), false, false),
        };
        person.AnimationPlayback.EventQueue.AddRange([4, 9]);

        RoutePersistence.RestorePendingAnimationEvents(person, null);

        Assert.Equal(new short[] { 4, 9 }, person.PendingAnimationEvents);
        Assert.Empty(person.AnimationPlayback.EventQueue);
    }

    [Fact]
    public void BeginFollowSeedsAnimatorDirectionFromThePersonFacing()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[ObjectData.Direction] = 5;
        person.SetAnimatorDisplacements(1f, 2f, 3f);

        RouteExecutor.BeginLookTowardsFollow(context.World, person, context.Frame,
        [
            new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1),
            new ObjectPosition(6 * 16 + 8, 5 * 16 + 8, 1),
        ]);

        Assert.Equal(5f, person.AnimatorRootDirection);
        Assert.Equal(0f, person.AnimatorDisplacementX);
        Assert.Equal(0f, person.AnimatorDisplacementY);
        Assert.Equal(0f, person.AnimatorDisplacementDirection);
    }

    [Fact]
    public void FirstFollowTickKeepsTheInitialRootHeading()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person.AnimatorRootDirection = 0f;
        person.AnimatorRootX = 80f;
        person.AnimatorRootY = 80f;
        person.NativeRoute = new RouteState(person.Id, new ObjectPosition(80, 112, 0), new RoutingSlotRequest(),
            MotionTargetX: 80, MotionTargetY: 112, MotionX: 80, MotionY: 80, MotionDirection: 2)
        { BeginFollowPendingTick = true };

        context.World.TickAvatarAnimations(person);

        Assert.Equal(0f, person.AnimatorRootDirection!.Value);
        Assert.False(person.NativeRoute!.BeginFollowPendingTick);
    }

    [Fact]
    public void FirstFollowTickEntersGaitBeforeTurningTowardTheNextPoint()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        var samples = new[] { System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitX };
        context.World.StandardWalkFallbackAnimationTable[(150, 20)] = new AvatarAnimationClip(
            "test-walk", 2, [], StandardDistance: 3f, StandardRootMotionSamples: samples,
            DurationMilliseconds: 1000f);
        context.World.StandardWalkFallbackAnimationTable[(150, 19)] = new AvatarAnimationClip(
            "test-walk-start", 2, [], StandardDistance: 3f, StandardRootMotionSamples: samples,
            DurationMilliseconds: 1000f);
        person.AnimatorControlState = 3;
        person.AnimatorRootX = 80;
        person.AnimatorRootY = 80;
        person.AnimatorRootDirection = 0;
        person.NativeRoute = new RouteState(person.Id, new ObjectPosition(96, 80, 1), new RoutingSlotRequest(),
            MotionTargetX: 96, MotionTargetY: 80, MotionX: 80, MotionY: 80, AnimatorFollowState: 1)
        { BeginFollowPendingTick = true };

        context.World.TickAvatarAnimations(person);

        Assert.Equal(9, person.AnimatorControlState);
        Assert.Equal(20, person.NativeRoute!.MotionAnimationIndex);
        Assert.Equal("test-walk", person.AnimationPlayback!.Clip.Name);
        Assert.Equal(0f, person.AnimatorRootDirection);
    }

    [Fact]
    public void AnimatorTickAdvancesRootDirectionTowardTheActiveFollowTarget()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person.AnimatorRootDirection = 0f;
        person.AnimatorRootX = 80f;
        person.AnimatorRootY = 80f;
        person.NativeRoute = new RouteState(person.Id, new ObjectPosition(112, 80, 0), new RoutingSlotRequest(),
            MotionTargetX: 112, MotionTargetY: 80, MotionX: 80, MotionY: 80, MotionDirection: 2);

        context.World.TickAvatarAnimations(person);

        Assert.Equal(0.2f, person.AnimatorRootDirection!.Value);
    }

    [Fact]
    public void AnimatorTickUsesBodyModeTurnLimitAndPracticeScale()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person.AnimatorRootDirection = 0f;
        person.AnimatorRootX = 80f;
        person.AnimatorRootY = 80f;
        person.AnimatorBodyStringMode = 2;
        person.AnimatorWalkingScale = 0.6f;
        person.NativeRoute = new RouteState(person.Id, new ObjectPosition(112, 80, 0), new RoutingSlotRequest(),
            MotionTargetX: 112, MotionTargetY: 80, MotionX: 80, MotionY: 80, MotionDirection: 2);

        context.World.TickAvatarAnimations(person);

        Assert.Equal(0.07f, person.AnimatorRootDirection!.Value, precision: 5);
    }

    [Fact]
    public void AnimatorTickDoesNotInterpolateRouteDirectionOutsideControlStateThree()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person.AnimatorRootDirection = 0f;
        person.AnimatorRootX = 80f;
        person.AnimatorRootY = 80f;
        person.AnimatorControlState = 7;
        person.NativeRoute = new RouteState(person.Id, new ObjectPosition(80, 112, 0), new RoutingSlotRequest(),
            MotionTargetX: 80, MotionTargetY: 112, MotionX: 80, MotionY: 80, MotionDirection: 2);

        context.World.TickAvatarAnimations(person);

        Assert.Equal(0f, person.AnimatorRootDirection!.Value);
    }

    [Fact]
    public void ActiveLookTowardsFollowPathRoundTripsAndRebindsItsOwnerFrame()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        var stack = new TestStack();
        stack.Frames.Add(new StackFrame(9, [1, 2], [3, 4]) { Instruction = 7 });
        stack.Frames.Add(context.Frame);
        var route = new RouteState(100, new ObjectPosition(80, 96, 0), new RoutingSlotRequest
        {
            X = 2.5f, Y = -1.25f, Flags = 0x40, Facing = 5, MinProximity = 4,
        }, OwnerFrame: context.Frame, DirectPositionRequest: true,
            MotionTargetTileX: 4, MotionTargetTileY: 6, MotionTargetX: 72, MotionTargetY: 96,
            MotionTargetIsDestination: true, MotionFrame: 13.25f, MotionPracticeFrame: 7.5f,
            MotionX: float.NaN, MotionY: float.NaN,
            MotionPlaybackScale: 1.4f, MotionAnimationIndex: 17, MotionDirection: 3,
            WaypointCooldownTicks: 2, WaypointTileX: 4, WaypointTileY: 6,
            WaypointX: 72, WaypointY: 96, WaypointCellCenterX: 72, WaypointCellCenterY: 104,
            EndFollowPending: true, FollowPlacementRejected: true, LocomotionFadeEndMils: 1234)
        {
            MotionPath = [new(3, 5, 56, 80, 56, 88, false), new(4, 6, 72, 96, 72, 104, true)],
            FollowPath = [new(0, 0, 8, 16, 8, 24, false), new(4, 6, 72, 96, 72, 104, true)],
            FollowWaypointIndex = 1,
            LookTowardsFollow = true,
            StartRootOriginX = 70.5f,
            StartRootOriginY = 95.25f,
            StartRootTargetX = 86.5f,
            StartRootTargetY = 95.25f,
            TurnRootOriginX = 71.25f,
            TurnRootOriginY = 96.5f,
            TurnRootTargetX = 86.25f,
            TurnRootTargetY = 100.5f,
            TurnRootSavedDirection = 3.75f,
            TurnRootPracticeScale = 0.65f,
            TurnRootStartDirection = 2.25f,
            TurnRootPracticeDirection = 4.5f,
            PoolSwimExitPending = true,
            BeginFollowPendingTick = true,
            AwaitingBeginFollow = true,
            EndFollowOnArrival = true,
            PoolSwimExitRootX = 71.25f,
            PoolSwimExitRootY = 96.5f,
            PoolSwimExitDirection = 3.75f,
        };
        person.NativeRoute = route;
        person.NativeRouteParents.Add(route with { AnchorObjectId = 11, OwnerFrame = stack.Frames[0] });

        var json = JsonSerializer.Serialize(RoutePersistence.Capture(person, stack), new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        });
        var snapshot = JsonSerializer.Deserialize<PersonRouteSnapshot>(json, new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        });
        Assert.Equal(1.4f, RoutePersistence.RestoreAnimatorWalkingScale(null, snapshot));
        Assert.Equal(1.6f, RoutePersistence.RestoreAnimatorWalkingScale(1.6f, snapshot));
        Assert.Equal(1.4f, RoutePersistence.RestoreAnimatorWalkingScale(null,
            new PersonRouteSnapshot(null, [snapshot!.Current!])));
        Assert.Equal(1f, RoutePersistence.RestoreAnimatorWalkingScale(null, null));

        var restoredPerson = new XPerson(100, PrimitiveTestWorld.Definition(XPerson.PersonType));
        var restoredStack = new TestStack();
        restoredStack.Frames.Add(new StackFrame(9, [1, 2], [3, 4]) { Instruction = 7 });
        restoredStack.Frames.Add(new StackFrame(context.Frame.StackObject, context.Frame.Parameters.ToArray(),
            context.Frame.Locals.ToArray()) { Instruction = context.Frame.Instruction });
        RoutePersistence.Restore(restoredPerson, restoredStack, snapshot);

        Assert.Single(restoredPerson.NativeRouteParents);
        var restored = Assert.IsType<RouteState>(restoredPerson.NativeRoute);
        Assert.Same(restoredStack.Frames[1], restored.OwnerFrame);
        Assert.True(restored.LookTowardsFollow);
        Assert.True(restored.EndFollowPending);
        Assert.True(restored.BeginFollowPendingTick);
        Assert.True(restored.AwaitingBeginFollow);
        Assert.True(restored.EndFollowOnArrival);
        Assert.True(restored.FollowPlacementRejected);
        Assert.Equal(1234, restored.LocomotionFadeEndMils);
        Assert.Equal(1, restoredPerson.AnimatorLocomotionFadeGate);
        Assert.Equal(1, restored.FollowWaypointIndex);
        Assert.Equal(route.Destination, restored.Destination);
        Assert.Equal(route.MotionFrame, restored.MotionFrame);
        Assert.Equal(route.MotionPracticeFrame, restored.MotionPracticeFrame);
        Assert.Equal(route.MotionPlaybackScale, restored.MotionPlaybackScale);
        Assert.True(float.IsNaN(restored.MotionX));
        Assert.True(float.IsNaN(restored.MotionY));
        Assert.Equal(route.MotionPath, restored.MotionPath);
        Assert.Equal(route.FollowPath, restored.FollowPath);
        Assert.Equal(route.StartRootOriginX, restored.StartRootOriginX);
        Assert.Equal(route.StartRootOriginY, restored.StartRootOriginY);
        Assert.Equal(route.StartRootTargetX, restored.StartRootTargetX);
        Assert.Equal(route.StartRootTargetY, restored.StartRootTargetY);
        Assert.Equal(route.TurnRootOriginX, restored.TurnRootOriginX);
        Assert.Equal(route.TurnRootOriginY, restored.TurnRootOriginY);
        Assert.Equal(route.TurnRootTargetX, restored.TurnRootTargetX);
        Assert.Equal(route.TurnRootTargetY, restored.TurnRootTargetY);
        Assert.Equal(route.TurnRootSavedDirection, restored.TurnRootSavedDirection);
        Assert.Equal(route.TurnRootPracticeScale, restored.TurnRootPracticeScale);
        Assert.Equal(route.TurnRootStartDirection, restored.TurnRootStartDirection);
        Assert.Equal(route.TurnRootPracticeDirection, restored.TurnRootPracticeDirection);
        Assert.True(restored.PoolSwimExitPending);
        Assert.Equal(route.PoolSwimExitRootX, restored.PoolSwimExitRootX);
        Assert.Equal(route.PoolSwimExitRootY, restored.PoolSwimExitRootY);
        Assert.Equal(route.PoolSwimExitDirection, restored.PoolSwimExitDirection);
        Assert.Equal(route.Slot.X, restored.Slot.X);
        Assert.Equal(route.Slot.Y, restored.Slot.Y);
        Assert.Equal(route.Slot.Flags, restored.Slot.Flags);
        Assert.Same(restoredStack.Frames[0], restoredPerson.NativeRouteParents[0].OwnerFrame);
    }

    [Fact]
    public void CaptureRejectsAnActiveRouteWhoseOwnerIsNotInTheSavedStack()
    {
        var (_, person, _) = PrimitiveTestWorld.Create();
        person.NativeRoute = new RouteState(100, new ObjectPosition(1, 2, 0), new RoutingSlotRequest(),
            OwnerFrame: new StackFrame(0, [], []));
        Assert.Throws<InvalidDataException>(() => RoutePersistence.Capture(person, new TestStack()));
    }
}
