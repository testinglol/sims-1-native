using System.Numerics;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class LookTowardsPrimitiveTests
{
    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(11)]
    public void AnimatorGaitStatesClearTransitionFlags(int controlState)
    {
        var (context, person, _) = World();
        person.AnimatorControlState = controlState;
        person.AnimatorGaitEntryState = 1;
        person.AnimatorPoolTransitionState = 1;

        context.World.TickAvatarAnimations(person);

        Assert.Equal(0, person.AnimatorGaitEntryState);
        Assert.Equal(0, person.AnimatorPoolTransitionState);
    }

    [Fact]
    public void HeadSeekToMissingObjectClearsTargetAndEntersTerminalState()
    {
        var (context, person, _) = World();
        person.AnimatorHeadSeekRegistrationsAvailable = true;
        person[PersonData.HeadSeekObject] = 77;
        person[PersonData.HeadSeekState] = 1;

        context.World.TickAvatarAnimations(person);

        Assert.Equal(0, person[PersonData.HeadSeekObject]);
        Assert.Equal(0x10, person[PersonData.HeadSeekState]);
    }

    [Fact]
    public void HeadSeekDoesNotResolveTargetWhenRequiredRegistrationsAreAbsent()
    {
        var (context, person, _) = World();
        person.AnimatorHeadSeekRegistrationsAvailable = false;
        person[PersonData.HeadSeekObject] = 77;
        person[PersonData.HeadSeekState] = 1;

        context.World.TickAvatarAnimations(person);

        Assert.Equal(77, person[PersonData.HeadSeekObject]);
        Assert.Equal(1, person[PersonData.HeadSeekState]);
    }

    [Fact]
    public void RootOnlyTickDoesNotResolveHeadSeekTarget()
    {
        var (context, person, _) = World();
        person[PersonData.HeadSeekObject] = 77;
        person[PersonData.HeadSeekState] = 1;
        context.World.AvatarAnimationRootOnly = true;

        context.World.TickAvatarAnimations(person);

        Assert.Equal(77, person[PersonData.HeadSeekObject]);
        Assert.Equal(1, person[PersonData.HeadSeekState]);
    }

    [Fact]
    public void HeadSeekToDifferentHeightClearsTargetAndEntersTerminalState()
    {
        var (context, person, _) = World();
        person.AnimatorHeadSeekRegistrationsAvailable = true;
        context.World.ObjectById(2)!.Position = new ObjectPosition(0, 0, 1);
        person[PersonData.HeadSeekObject] = 2;
        person[PersonData.HeadSeekState] = 1;

        context.World.TickAvatarAnimations(person);

        Assert.Equal(0, person[PersonData.HeadSeekObject]);
        Assert.Equal(0x10, person[PersonData.HeadSeekState]);
    }

    [Fact]
    public void HeadSeekToSameHeightKeepsActiveTargetRegardlessOfTypeRecord()
    {
        var (context, person, _) = World();
        person.AnimatorHeadSeekRegistrationsAvailable = true;
        context.World.ObjectById(2)!.TypeRecord = new TypeRecord(0x5678);
        person[PersonData.HeadSeekObject] = 2;
        person[PersonData.HeadSeekState] = 1;

        context.World.TickAvatarAnimations(person);

        Assert.Equal(2, person[PersonData.HeadSeekObject]);
        Assert.Equal(1, person[PersonData.HeadSeekState]);
    }

    [Fact]
    public void HeadSeekResolvesWhenRegistrationCacheHasNotBeenBuiltYet()
    {
        var (context, person, _) = World();
        context.World.ObjectById(2)!.TypeRecord = new TypeRecord(person.TypeRecord!.Key);
        person[PersonData.HeadSeekObject] = 2;
        person[PersonData.HeadSeekState] = 1;
        var resolverCalled = false;
        context.World.HeadSeekGeometryResolver = (_, _) =>
        {
            resolverCalled = true;
            return new HeadSeekGeometry(new Vector2(-1f, 0f), new Vector2(0f, 0f), new Vector2(0f, 1f));
        };

        context.World.TickAvatarAnimations(person);

        Assert.True(resolverCalled);
        Assert.Equal(2, person[PersonData.HeadSeekObject]);
        Assert.Equal(4f, person.AnimatorHeadBaselineDirection, 5);
        Assert.Equal(3f, person.AnimatorHeadTargetDirection, 5);
    }

    [Fact]
    public void UnknownRegistrationCacheWithoutResolverDoesNotConsumeHeadSeekTarget()
    {
        var (context, person, _) = World();
        person[PersonData.HeadSeekObject] = 77;
        person[PersonData.HeadSeekState] = 1;

        context.World.TickAvatarAnimations(person);

        Assert.Null(person.AnimatorHeadSeekRegistrationsAvailable);
        Assert.Equal(77, person[PersonData.HeadSeekObject]);
        Assert.Equal(1, person[PersonData.HeadSeekState]);
    }

    [Fact]
    public void HeadSeekRefreshesTargetDirectionOnEveryFullTick()
    {
        var (context, person, _) = World();
        context.World.ObjectById(2)!.TypeRecord = new TypeRecord(person.TypeRecord!.Key);
        person.AnimatorHeadSeekRegistrationsAvailable = true;
        person[PersonData.HeadSeekObject] = 2;
        person[PersonData.HeadSeekState] = 1;
        var resolveCount = 0;
        context.World.HeadSeekGeometryResolver = (_, _) =>
        {
            resolveCount++;
            return new HeadSeekGeometry(new Vector2(-1f, 0f), new Vector2(0f, 0f),
                resolveCount == 1 ? new Vector2(0f, 1f) : new Vector2(-2f, 0f));
        };

        context.World.TickAvatarAnimations(person);
        Assert.Equal(3f, person.AnimatorHeadTargetDirection, 5);
        context.World.TickAvatarAnimations(person);

        Assert.Equal(2, resolveCount);
        Assert.Equal(0f, person.AnimatorHeadTargetDirection, 5);
        Assert.Equal(2, person[PersonData.HeadSeekObject]);
    }

    [Fact]
    public void FullAvatarTicksResolveHeadDirectionsThenSmoothOnTheFollowingTick()
    {
        var (context, person, _) = World();
        context.World.ObjectById(2)!.TypeRecord = new TypeRecord(person.TypeRecord!.Key);
        person.AnimatorHeadSeekRegistrationsAvailable = true;
        person[PersonData.HeadSeekObject] = 2;
        person[PersonData.HeadSeekState] = 1;
        context.World.HeadSeekGeometryResolver = (_, _) => new HeadSeekGeometry(
            new Vector2(-1f, 0f), new Vector2(0f, 0f), new Vector2(0f, 1f));

        context.World.TickAvatarAnimations(person);
        Assert.Equal(0f, person.AnimatorHeadAdjustment);
        Assert.Equal(4f, person.AnimatorHeadBaselineDirection, 5);
        Assert.Equal(3f, person.AnimatorHeadTargetDirection, 5);

        context.World.TickAvatarAnimations(person);
        Assert.Equal(0.2f, person.AnimatorHeadAdjustment, 5);
    }

    [Fact]
    public void HeadAdjustmentLimitActionUsesSourcePersonState()
    {
        var (context, person, _) = World();
        person.AnimatorHeadSeekRegistrationsAvailable = true;
        person.AnimatorHeadAdjustment = 1.45f;
        person.AnimatorHeadBaselineDirection = 0.2f;
        person[PersonData.HeadSeekState] = 1;
        person[PersonData.HeadSeekLimitAction] = 1;

        context.World.TickAvatarAnimations(person);

        Assert.Equal(1.5f, person.AnimatorHeadAdjustment, 5);
        Assert.Equal(4, person[PersonData.HeadSeekState]);
    }

    [Fact]
    public void IntermediateRoutePointDoesNotScheduleTheTerminalMarkerFade()
    {
        var (context, person, _) = World();
        var clip = new AvatarAnimationClip("walking-loop", 10, [], DurationMilliseconds: 1000f,
            LocomotionMarkerOne: new AvatarAnimationMarker(0.25f, "lfoot"),
            LocomotionMarkerTwo: new AvatarAnimationMarker(0.75f, "rfoot"));
        context.World.StandardWalkFallbackAnimationTable[(150, 20)] = clip;
        context.World.LocomotionStrideResolver = (_, _) => (100f, 80f);
        person.AnimationPlayback = new AvatarAnimationPlayback(clip, false, true) { RouteDriven = true };
        person.AnimatorControlState = 9;
        person.AnimatorRootX = 170;
        person.AnimatorRootY = 180;
        person.NativeRoute = new RouteState(person.Id, new ObjectPosition(200, 180, 0), new RoutingSlotRequest(),
            MotionTargetX: 180, MotionTargetY: 180, MotionX: 170, MotionY: 180, MotionAnimationIndex: 20)
        {
            MotionTargetPathIndex = 1,
            MotionPath = [new(10, 11, 170, 180, 170, 180, false),
                new(11, 11, 180, 180, 180, 180, false), new(12, 11, 200, 180, 200, 180, true)],
        };

        context.World.TickAvatarAnimations(person);

        Assert.Equal(0, person.NativeRoute!.LocomotionFadeEndMils);
        Assert.Equal(0, person.AnimatorLocomotionFadeGate);
        Assert.False(person.AnimationPlayback!.SaveState().PracticeWeightFadeActive);
    }

    [Fact]
    public void NearWaypointSchedulesSourceMarkerFadeFromSelectedSkeletonStride()
    {
        var (context, person, _) = World();
        var markerClip = new AvatarAnimationClip("walking-loop-markers", 10, [], DurationMilliseconds: 1000f,
            LocomotionMarkerOne: new AvatarAnimationMarker(0.25f, "lfoot"),
            LocomotionMarkerTwo: new AvatarAnimationMarker(0.75f, "rfoot"));
        context.World.StandardWalkFallbackAnimationTable.Clear();
        context.World.StandardWalkFallbackAnimationTable[(150, 20)] = markerClip;
        var clip = new AvatarAnimationClip("current-practice-without-tags", 10, [], DurationMilliseconds: 1000f);
        var playback = new AvatarAnimationPlayback(clip, backwards: false, loop: true) { RouteDriven = true };
        playback.SetFrame(2f);
        person.AnimationPlayback = playback;
        person.AnimatorRootX = 170f;
        person.AnimatorRootY = 180f;
        person.NativeRoute = new RouteState(person.Id, person.Position, new RoutingSlotRequest(),
            MotionTargetX: 220, MotionTargetY: 180, MotionX: 170f, MotionY: 180f,
            MotionAnimationIndex: 20);
        context.World.LocomotionStrideResolver = (_, name) => name == markerClip.Name ? (100f, 80f) : null;

        context.World.TickAvatarAnimations(person);

        Assert.Equal(1, person.AnimatorLocomotionFadeGate);
        Assert.Equal(1, person.AnimatorRouteTransitionState); // Mac enters +0x280=1 before scheduling +0x1b8.
        Assert.Equal(1, person.AnimatorPoolTransitionState); // Mac's same fade branch also writes +0x288=1.
        Assert.True(person.NativeRoute!.LocomotionFadeEndMils > person.AnimatorMilliseconds);
        var fade = playback.SaveState();
        Assert.True(fade.PracticeWeightFadeActive);
        Assert.Equal(0.5f, fade.PracticeWeightFadeTarget);

        person.AnimatorRootX = 219.5f;
        person.AnimatorPoolTransitionState = 1;
        person.AnimatorMilliseconds = person.NativeRoute.LocomotionFadeEndMils - 84;
        context.World.TickAvatarAnimations(person);

        Assert.Equal(1, person.AnimatorLocomotionFadeGate);
        Assert.Equal(person.AnimatorMilliseconds + 42, person.NativeRoute!.LocomotionFadeEndMils);
        person.AnimatorRootX = 170f;
        person.AnimatorMilliseconds = person.NativeRoute.LocomotionFadeEndMils - 42;
        context.World.TickAvatarAnimations(person);

        Assert.Equal(0, person.NativeRoute!.LocomotionFadeEndMils);
        Assert.Equal(2, person.NativeRoute.AnimatorFollowState); // Tick redispatches the matching terminal pose in the same pass.
        Assert.Equal(-1, person.NativeRoute.MotionAnimationIndex);
        Assert.Equal(3, person.AnimatorControlState);
        Assert.Null(person.AnimationPlayback);
        Assert.Equal(0, person.AnimatorLocomotionFadeGate);
        Assert.Equal(0, person.AnimatorPoolTransitionState);
        Assert.Equal(220f, person.AnimatorRootX);
        Assert.Equal(180f, person.AnimatorRootY);
    }

    [Fact]
    public void RoutePreferenceArrivalRepeatsStoredStepThenCompletesAtTransitionTarget()
    {
        var (context, person, _) = World();
        var clip = new AvatarAnimationClip("walking-loop", 10, [], StandardDistance: 60f,
            StandardRootMotionSamples: [new(0f, 0f, 0f), new(60f, 0f, 0f)], DurationMilliseconds: 1000f,
            LocomotionMarkerOne: new AvatarAnimationMarker(0.25f, "lfoot"),
            LocomotionMarkerTwo: new AvatarAnimationMarker(0.75f, "rfoot"));
        context.World.StandardWalkFallbackAnimationTable[(150, 20)] = clip;
        context.World.LocomotionStrideResolver = (_, _) => (100f, 80f);
        person[ObjectData.RoutePreference] = 1; // Door portal functions set person data 9 around the route.
        person.AnimationPlayback = new AvatarAnimationPlayback(clip, false, true) { RouteDriven = true };
        person.AnimatorControlState = 9;
        person.AnimatorRootX = 170f;
        person.AnimatorRootY = 180f;
        person.AnimatorProjectedRootStepX = 0f;
        person.AnimatorProjectedRootStepY = 2f;
        person.NativeRoute = new RouteState(person.Id, person.Position, new RoutingSlotRequest(),
            MotionTargetX: 172, MotionTargetY: 180, MotionX: 170f, MotionY: 180f, MotionAnimationIndex: 20);

        context.World.TickAvatarAnimations(person);

        var route = person.NativeRoute!;
        Assert.Equal(0, route.LocomotionFadeEndMils); // No marker fade on this branch.
        Assert.Equal(-1, route.AnimatorFollowState);
        Assert.Equal(170f, route.TransitionTargetX);
        Assert.Equal(180f, route.TransitionTargetY);
        Assert.Equal(3, person.AnimatorControlState);
        Assert.Equal(1, person.AnimatorRouteTransitionState);
        Assert.Equal(1, person.AnimatorLocomotionFadeGate);
        Assert.Equal(1, person.AnimatorPoolTransitionState);
        Assert.NotNull(person.AnimationPlayback); // The arrival keeps the walk practice.
        // The stored +0x28c/+0x290 direction, not the target direction, sets the repeated step.
        Assert.Equal(170f, person.AnimatorRootX!.Value, 4);
        Assert.True(person.AnimatorRootY > 180f);
        Assert.Equal(0f, person.AnimatorProjectedRootStepX, 4);
        Assert.True(person.AnimatorProjectedRootStepY > 0f);

        context.World.TickAvatarAnimations(person);

        Assert.Equal(170f, person.AnimatorRootX);
        Assert.Equal(180f, person.AnimatorRootY);
        Assert.Null(person.NativeRoute!.TransitionTargetX);
        Assert.NotEqual(-1, person.NativeRoute.AnimatorFollowState);
    }

    [Fact]
    public void PoolRoutePreferenceArrivalWithoutPendingTransitionSnapsAtTheCurrentRoot()
    {
        var (context, person, _) = World();
        var clip = new AvatarAnimationClip("swim-loop", 10, [], StandardDistance: 60f, DurationMilliseconds: 1000f);
        context.World.StandardWalkFallbackAnimationTable[(150, 20)] = clip;
        person[ObjectData.RoutePreference] = 1;
        person.AnimatorPoolTile = true;
        person.AnimationPlayback = new AvatarAnimationPlayback(clip, false, true) { RouteDriven = true };
        person.AnimatorControlState = 9;
        person.AnimatorRootX = 170f;
        person.AnimatorRootY = 180f;
        person.AnimatorProjectedRootStepX = 0f;
        person.AnimatorProjectedRootStepY = 2f;
        person.NativeRoute = new RouteState(person.Id, person.Position, new RoutingSlotRequest(),
            MotionTargetX: 172, MotionTargetY: 180, MotionX: 170f, MotionY: 180f, MotionAnimationIndex: 20);

        RouteExecutor.TickRouteRootMovement(context.World, person, null);

        // Zero +0x288: +0x188=3, +0x180=-1, +0x1bc/+0x1c0 = the current root, and no step (0x1035115C–0x103511E8).
        var route = person.NativeRoute!;
        Assert.Equal(3, person.AnimatorControlState);
        Assert.Equal(1, person.AnimatorRouteTransitionState);
        Assert.Equal(-1, route.AnimatorFollowState);
        Assert.Equal(170f, route.TransitionTargetX);
        Assert.Equal(180f, route.TransitionTargetY);
        Assert.Equal(170f, person.AnimatorRootX);
        Assert.Equal(180f, person.AnimatorRootY);
        Assert.Same(clip, person.AnimationPlayback?.Clip);
    }

    [Fact]
    public void PoolRoutePreferenceArrivalWithPendingTransitionPlaysSkillThirteenThenFollowsTransitionState()
    {
        var (context, person, _) = World();
        var swimExit = new AvatarAnimationClip("pool-exit", 4, []);
        for (ushort index = 150; index <= 160; index++)
            context.World.StandardWalkFallbackAnimationTable[(index, 0x13)] = swimExit;
        person[ObjectData.RoutePreference] = 1;
        person.AnimatorPoolTile = true;
        person.AnimatorPoolTransitionState = 1;
        var swim = new AvatarAnimationClip("swim-loop", 10, [], StandardDistance: 60f, DurationMilliseconds: 1000f);
        context.World.StandardWalkFallbackAnimationTable[(150, 20)] = swim;
        person.AnimationPlayback = new AvatarAnimationPlayback(swim, false, true) { RouteDriven = true };
        person.AnimatorControlState = 9;
        person.AnimatorRootX = 170f;
        person.AnimatorRootY = 180f;
        person.NativeRoute = new RouteState(person.Id, person.Position, new RoutingSlotRequest(),
            MotionTargetX: 172, MotionTargetY: 180, MotionX: 170f, MotionY: 180f, MotionAnimationIndex: 20,
            ArrivalFacing: 2);

        RouteExecutor.TickRouteRootMovement(context.World, person, null);

        // Nonzero +0x288: +0x188=7, skill 0x13 backward; +0x1d4/+0x1d8 hold the terminal point (0x103511EC).
        var route = person.NativeRoute!;
        Assert.Equal(7, person.AnimatorControlState);
        Assert.Equal(1, person.AnimatorRouteTransitionState);
        Assert.Equal(0x13, person.AnimatorCurrentSkill);
        Assert.Same(swimExit, person.AnimationPlayback?.Clip);
        Assert.True(person.AnimationPlayback?.Backwards);
        Assert.True(route.PoolSwimExitPending);
        Assert.Equal(172f, route.PoolSwimExitRootX);
        Assert.Equal(180f, route.PoolSwimExitRootY);
        Assert.Equal(1, route.AnimatorFollowState); // +0x180 is unchanged while state 7 plays.

        person.AnimationPlayback = null; // State 7 substitutes phase 1 when the primary practice is gone.
        context.World.TickAvatarAnimations(person);

        // The terminal branch restores +0x1d4/+0x1d8 and maps +0x280=1 to +0x180=-1 (0x1035025C–0x103502A4).
        Assert.Equal(3, person.AnimatorControlState);
        Assert.Equal(172f, person.AnimatorRootX);
        Assert.Equal(180f, person.AnimatorRootY);
        Assert.False(person.NativeRoute!.PoolSwimExitPending);
        Assert.Equal(-1, person.NativeRoute.AnimatorFollowState);
    }

    private static PrimitiveResult RunLookTowardsStep(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var result = LookTowardsPrimitive.Execute(context, operand);
        // Native tests step the source object callback and its later SAnimator::Tick separately.
        if (context.World.Host is null && context.Me is XPerson person)
            context.World.TickAvatarAnimations(person);
        return result;
    }

    private static PrimitiveResult StartDirectWalkFollow(in TreeContext context, short targetObjectId)
    {
        var person = (XPerson)context.Me;
        var point = context.World.ObjectById(targetObjectId)!.Position with { Height = person.Position.Height };
        return RouteExecutor.BeginDirectWalkFollow(context.World, person, context.Frame,
            [person.Position, point, point]);
    }

    private static PrimitiveResult RunDirectWalkFollowStep(in TreeContext context, short targetObjectId)
    {
        var person = (XPerson)context.Me;
        var result = person.NativeRoute is null
            ? StartDirectWalkFollow(context, targetObjectId)
            : RouteExecutor.Continue(context.World, person, context.Frame, context.Stack);
        context.World.TickAvatarAnimations(person);
        return result;
    }

    // The mode is at byte 0; the target comes from the executing frame.
    private static byte[] Operand(int mode, short target = unchecked((short)0xEEEE)) =>
        [(byte)mode, (byte)(mode >> 8), 0xEE, 0xEE, (byte)target, (byte)(target >> 8), 0xEE, 0xEE];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    // Me is person 100 at (170, 180). The stack object is object 2 at (190, 200) unless the test removes it.
    private static (TreeContext Context, XPerson Me, PrimitiveTestHost Host) World(short stackObject = 2)
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: stackObject);
        me.Position = new ObjectPosition(170, 180, 0);
        // The source person OBJD permits placement on ground level; the minimal primitive fixture leaves this data
        // word zero unless a test sets it explicitly.
        me[ObjectData.AllowedHeightFlags] = 1;
        // The synthetic lot has no terrain data; allow the source placement validator's empty ground class.
        me[ObjectData.PlacementFlags] = 0x0800;
        me.TypeRecord = new TypeRecord(0x1234) { FootprintMask = 0 };
        context.World.Add(new XObject(2, PrimitiveTestWorld.Definition(0)) { Position = new ObjectPosition(190, 200, 0) });
        context.World.Add(new XObject(3, PrimitiveTestWorld.Definition(0)) { Position = new ObjectPosition(210, 220, 0) });
        return (context, me, host);
    }

    private static (TreeContext Context, XObject Me) NonPerson()
    {
        var world = new SimulationWorld { Host = new PrimitiveTestHost() };
        var me = world.Add(new XObject(100, PrimitiveTestWorld.Definition(0)));
        return (new TreeContext(world, me, new StackFrame(2, [], [0, 0])), me);
    }

    // Shares the route fixture's walk clips, including the synthetic locomotion markers and stride inputs that
    // activate SAnimator::Tick's terminal fade; without them the follow root never settles on its endpoint.
    private static void AddWalkClips(SimulationWorld world) => GoToRoutingSlotPrimitiveTests.AddWalkClips(world);

    [Fact]
    public void ModeZeroUsesTheStackObjectInsteadOfOperandBytes()
    {
        var (context, person, host) = World();
        Assert.Equal(PrimitiveResult.True, RunLookTowardsStep(context, Operand(0, 7)));
        Assert.Empty(host.Calls);
        Assert.Equal(2, person[PersonData.HeadSeekObject]);
        Assert.Equal(1, person[PersonData.HeadSeekState]);
        Assert.Equal(0, person[PersonData.HeadSeekFinishAction]);
        Assert.Equal(1, person[PersonData.HeadSeekLimitAction]);
        Assert.Equal(0, person[PersonData.HeadSeekTimeout]);
        Assert.False(context.Frame.Waiting);
    }

    [Fact]
    public void ModeZeroWithNoStackObjectClearsTheTarget()
    {
        var (context, person, host) = World(stackObject: 0);
        Assert.Equal(PrimitiveResult.True, RunLookTowardsStep(context, Operand(0, 7)));
        Assert.Empty(host.Calls);
        Assert.Equal(0, person[PersonData.HeadSeekObject]);
        Assert.Equal(0, person[PersonData.HeadSeekState]);
    }

    [Fact]
    public void ModeTwoResolvesTheFrameStackObjectAndStartsTheTurn()
    {
        var (context, _, host) = World(stackObject: 3);
        host.TurnStatusAnswer = 0;
        Assert.Equal(PrimitiveResult.True, RunLookTowardsStep(context, Operand(2, 3)));
        Assert.Equal(new[] { "TurnTowards(100,210,220)", "TurnStatus(100)", "TryStartTurn(100)" }, host.Calls);
        Assert.True(context.Frame.Waiting);
    }

    [Fact]
    public void ModeThreeTurnsAwayByReflectingTheTargetThroughThePerson()
    {
        var (context, _, host) = World(stackObject: 3);
        host.TurnStatusAnswer = 0;
        Assert.Equal(PrimitiveResult.True, RunLookTowardsStep(context, Operand(3, 3)));
        // 2 * (170, 180) - (210, 220) = (130, 140).
        Assert.Equal("TurnTowards(100,130,140)", host.Calls[0]);
    }

    [Theory]
    [InlineData(2, 210, 220, 3)]
    [InlineData(3, 130, 140, 7)]
    public void NativeHostlessTurnStartsTheSourceDefinedFollowPath(int mode, int x, int y, short expectedDirection)
    {
        var (context, person, _) = World(stackObject: 3);
        context.World.Host = null;
        AddWalkClips(context.World);

        Assert.Equal(PrimitiveResult.Stop, RunLookTowardsStep(context, Operand(mode, 3)));

        Assert.Equal(new ObjectPosition(x, y, 0), person.LastLookTowardsPoint);
        Assert.True(context.Frame.Waiting);
        Assert.Null(person.AnimationPlayback);
        var path = person.NativeRoute!.FollowPath!;
        Assert.Equal(new ObjectPosition(170, 180, 0), new ObjectPosition(path[0].X, path[0].Y, 0));
        Assert.Equal(new ObjectPosition(170, 180, 0), new ObjectPosition(path[1].X, path[1].Y, 0));
        Assert.Equal(new ObjectPosition(x, y, 0), new ObjectPosition(path[^1].X, path[^1].Y, 0));

        var result = PrimitiveResult.Stop;
        for (var tick = 0; tick < 64 && result == PrimitiveResult.Stop; tick++)
            result = RunLookTowardsStep(context, Operand(mode, 3));
        Assert.True(result == PrimitiveResult.True,
            $"result={result}; position={person.Position}; root=({person.AnimatorRootX},{person.AnimatorRootY}); " +
            $"route={person.NativeRoute}");
        Assert.Null(person.NativeRoute);
        Assert.Equal(new ObjectPosition(170, 180, 0), person.Position);
        Assert.Equal(expectedDirection, person[ObjectData.Direction]);
    }

    [Fact]
    public void FollowOneStepUsesTheScaleFromThePreviousAnimatorTick()
    {
        var (context, person, _) = World();
        context.World.Host = null;
        AddWalkClips(context.World);

        Assert.Equal(PrimitiveResult.Stop, RunDirectWalkFollowStep(context, 3));
        Assert.Equal(1.2f, person.AnimatorWalkingScale);
        Assert.Equal(PrimitiveResult.Stop, RunDirectWalkFollowStep(context, 3));

        var route = Assert.IsType<RouteState>(person.NativeRoute);
        Assert.Equal(31f * 42f / 1000f * 1.2f, route.MotionFrame, 3); // Practice::Apply: elapsed / (skill duration / scale).
        Assert.Equal(1.4f, person.AnimatorWalkingScale, 3); // FollowOneStep reads the prior 1.2 before Tick eases again.
        Assert.Equal(person.AnimatorWalkingScale, route.MotionPlaybackScale, 3);
    }

    [Theory]
    [InlineData(15f, 1000f, 1.2f, 42f, 4.032f)]
    [InlineData(0f, 1000f, 1.2f, 42f, 0f)]
    public void RouteStepUsesTheSourceSkillDistanceAndScaledPracticeDuration(float distance, float duration,
        float scale, float elapsed, float expected)
    {
        Assert.Equal(expected, RouteExecutor.RouteStepDistance(distance, duration, scale, elapsed), 3);
    }

    [Fact]
    public void FollowOneStepAddsTheSourceBiasBeforeFtileTruncation()
    {
        Assert.Equal(12, RouteExecutor.FTileIntegerCoordinate(12.499f));
        Assert.Equal(13, RouteExecutor.FTileIntegerCoordinate(12.5f));
        Assert.Equal(13, RouteExecutor.FTileIntegerCoordinate(12.999f));
    }

    [Fact]
    public void AnimatorTickTruncatesItsPredictedRootWithoutTheFollowOneStepBias()
    {
        Assert.Equal(12, RouteExecutor.TickRootIntegerCoordinate(12.499f));
        Assert.Equal(12, RouteExecutor.TickRootIntegerCoordinate(12.5f));
        Assert.Equal(12, RouteExecutor.TickRootIntegerCoordinate(12.999f));
    }

    [Fact]
    public void TileListNearestPointTruncatesItsProjectedCoordinatesWithoutTheFtileBias()
    {
        var path = new[]
        {
            new RouteExecutor.RouteWaypoint(0, 0, 0, 0, 0, 0, false),
            new RouteExecutor.RouteWaypoint(0, 0, 3, 2, 3, 2, true),
        };

        Assert.Equal((1, 0), RouteExecutor.FindNearestRoutePoint(path, 1, 1));
    }

    [Fact]
    public void FollowOneStepRunsPersonPlacementWhenFtileRoundsToCurrentPosition()
    {
        var (context, person, _) = World();
        context.World.Host = null;
        AddWalkClips(context.World);
        var functions = new ushort[10];
        functions[9] = 1;
        person.TypeRecord = new TypeRecord(1) { FunctionTable = functions };

        Assert.Equal(PrimitiveResult.Stop, StartDirectWalkFollow(context, 3));

        // The first root-motion increment is less than one FTile unit. Mac still calls cXPerson::Place,
        // which enters cXObject::Place's function-9 path for this unchanged discrete position.
        // SAnimator::Tick owns root motion, so the placement runs from the animator tick.
        Assert.Throws<NotSupportedException>(() => context.World.TickAvatarAnimations(person));
    }

    [Fact]
    public void RejectedFollowPlacementRestoresPositionAndDoesNotAdvanceRootMotion()
    {
        var (context, person, _) = World();
        context.World.Host = null;
        AddWalkClips(context.World);

        Assert.Equal(PrimitiveResult.Stop, RunDirectWalkFollowStep(context, 3));
        var originalPosition = person.Position;
        var originalRootX = person.AnimatorRootX;
        var originalRootY = person.AnimatorRootY;
        person[ObjectData.AllowedHeightFlags] = 0; // cXObject::CanPlace rejects the next ground-level step.

        Assert.Equal(PrimitiveResult.Stop, RunDirectWalkFollowStep(context, 3));

        // A rejected CanPlace sets +0x180=3 and leaves the route block without storing +0x80/+0x84
        // (SAnimator.s 0x10351060–0x10351070); the gait practice itself still ticks. The next follow
        // callback sees +0x180=3 and ends the follow.
        Assert.Equal(originalPosition, person.Position);
        Assert.Equal(originalRootX, person.AnimatorRootX);
        Assert.Equal(originalRootY, person.AnimatorRootY);
        Assert.True(person.NativeRoute!.FollowPlacementRejected);
        Assert.Equal(3, person.NativeRoute.AnimatorFollowState);
        Assert.Equal(person.NativeRoute.MotionX, person.AnimatorRootX);
        Assert.Equal(person.NativeRoute.MotionY, person.AnimatorRootY);

        Assert.Equal(PrimitiveResult.True, RunDirectWalkFollowStep(context, 3));
        Assert.Null(person.NativeRoute);
        Assert.Equal(originalPosition, person.Position);
    }

    [Fact]
    public void WalkingScaleSurvivesFollowTeardownAndSeedsTheNextFollow()
    {
        var (context, person, _) = World();
        context.World.Host = null;
        AddWalkClips(context.World);

        Assert.Equal(PrimitiveResult.Stop, RunDirectWalkFollowStep(context, 3));
        var result = PrimitiveResult.Stop;
        for (var tick = 0; tick < 512 && result == PrimitiveResult.Stop; tick++)
            result = RunDirectWalkFollowStep(context, 3);

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Null(person.NativeRoute);
        Assert.Equal(1.6f, person.AnimatorWalkingScale);

        context.World.ObjectById(3)!.Position = new ObjectPosition(70, 80, 0);
        Assert.Equal(PrimitiveResult.Stop, RunDirectWalkFollowStep(context, 3));
        Assert.Equal(1.6f, person.NativeRoute!.MotionPlaybackScale);
    }

    // The route-entry gate takes the standing-adjust start clip only when the lookahead offset
    // (StdAnimDist(loop) * 8/3) exceeds StdAnimDist(start) * 16/3 (SAnimator.s 0x10350B98, 0x10350E28–0x10350E7C),
    // so these fixtures give the start clips a shorter stride than the loops.
    private const float StartClipDistance = 0.25f;

    [Fact]
    public void WalkStyleOneUsesRunStartThenSwitchesToRunLoopAtNinetyFivePercent()
    {
        var (context, person, _) = World();
        context.World.Host = null;
        person[ObjectData.WalkStyle] = 1;
        person.TypeRecord = new TypeRecord(0x1234) { FootprintMask = 0 };
        AddWalkClips(context.World);
        var samples = Enumerable.Range(0, 31).Select(i => new System.Numerics.Vector3(i / 30f, 0f, 0f)).ToArray();
        for (var index = 11; index <= 22; index++)
            context.World.StandardWalkFallbackAnimationTable[(150, index)] = new AvatarAnimationClip(
                $"walk-{index}", samples.Length, [], null, index is 19 or 22 ? StartClipDistance : 1f,
                new System.Numerics.Vector3(1f, 0f, 0f), samples, 1000f);

        Assert.Equal(PrimitiveResult.Stop, StartDirectWalkFollow(context, 2));
        person.AnimatorRouteTransitionState = 1; // Exercise start-to-loop behavior after the +0x280 route-entry branch.
        context.World.TickAvatarAnimations(person);
        var result = PrimitiveResult.Stop;
        for (var tick = 0; tick < 128 && person.NativeRoute?.MotionAnimationIndex != 22; tick++)
            result = RunDirectWalkFollowStep(context, 2);
        Assert.Equal((short?)22, person.NativeRoute?.MotionAnimationIndex);
        result = RunDirectWalkFollowStep(context, 2);
        Assert.Equal("walk-22", person.AnimationPlayback?.Clip.Name);

        for (var tick = 0; tick < 32 && person.NativeRoute is { MotionAnimationIndex: 22 } route &&
             route.MotionPracticeFrame < samples.Length * 0.95f; tick++)
            result = RunDirectWalkFollowStep(context, 2);
        Assert.Equal((short?)22, person.NativeRoute?.MotionAnimationIndex);
        Assert.InRange(person.NativeRoute!.MotionPracticeFrame, samples.Length * 0.95f, samples.Length);
        Assert.Equal(PrimitiveResult.Stop, RunDirectWalkFollowStep(context, 2));
        Assert.Equal((short?)21, person.NativeRoute?.MotionAnimationIndex);
        Assert.Equal(10, person.AnimatorControlState);
        Assert.Equal(1, person.AnimatorPoolTransitionState);
        Assert.Equal(1, person.AnimatorGaitEntryState);
        Assert.Equal("walk-21", person.AnimationPlayback?.Clip.Name);

        RunDirectWalkFollowStep(context, 2);
        Assert.Equal(1, person.AnimatorGaitEntryState);
    }

    [Fact]
    public void WalkStyleZeroUsesWalkStartThenSwitchesToWalkLoopAtNinetyFivePercent()
    {
        var (context, person, _) = World();
        context.World.Host = null;
        person[ObjectData.WalkStyle] = 0;
        person.TypeRecord = new TypeRecord(0x1234) { FootprintMask = 0 };
        AddWalkClips(context.World);
        var samples = Enumerable.Range(0, 31).Select(i => new System.Numerics.Vector3(i / 30f, 0f, 0f)).ToArray();
        for (var index = 11; index <= 22; index++)
            context.World.StandardWalkFallbackAnimationTable[(150, index)] = new AvatarAnimationClip(
                $"walk-{index}", samples.Length, [], null, index is 19 or 22 ? StartClipDistance : 1f,
                new System.Numerics.Vector3(1f, 0f, 0f), samples, 1000f);

        Assert.Equal(PrimitiveResult.Stop, StartDirectWalkFollow(context, 3));
        person.AnimatorRouteTransitionState = 1; // Exercise start-to-loop behavior after the +0x280 route-entry branch.
        context.World.TickAvatarAnimations(person);
        var result = PrimitiveResult.Stop;
        for (var tick = 0; tick < 128 && person.NativeRoute?.MotionAnimationIndex != 19; tick++)
            result = RunDirectWalkFollowStep(context, 3);
        Assert.Equal((short?)19, person.NativeRoute?.MotionAnimationIndex);
        result = RunDirectWalkFollowStep(context, 3);
        Assert.Equal("walk-19", person.AnimationPlayback?.Clip.Name);

        for (var tick = 0; tick < 32 && person.NativeRoute is { MotionAnimationIndex: 19 } route &&
             route.MotionPracticeFrame < samples.Length * 0.95f; tick++)
            result = RunDirectWalkFollowStep(context, 2);
        Assert.Equal((short?)19, person.NativeRoute?.MotionAnimationIndex);
        Assert.InRange(person.NativeRoute!.MotionPracticeFrame, samples.Length * 0.95f, samples.Length);
        Assert.Equal(PrimitiveResult.Stop, RunDirectWalkFollowStep(context, 2));
        Assert.Equal((short?)20, person.NativeRoute?.MotionAnimationIndex);
        Assert.Equal(9, person.AnimatorControlState);
        Assert.Equal(1, person.AnimatorPoolTransitionState);
        Assert.Equal(1, person.AnimatorGaitEntryState);
        Assert.Equal("walk-20", person.AnimationPlayback?.Clip.Name);

        RunDirectWalkFollowStep(context, 2);
        Assert.Equal(1, person.AnimatorGaitEntryState);
    }

    [Fact]
    public void RootOnlyRouteTickMovesTheSimButHoldsWalkPracticePhase()
    {
        var (context, person, _) = World(stackObject: 3);
        context.World.Host = null;
        person[ObjectData.WalkStyle] = 0;
        person.TypeRecord = new TypeRecord(0x1234) { FootprintMask = 0 };
        var samples = Enumerable.Range(0, 121).Select(i => new System.Numerics.Vector3(i / 120f, 0f, 0f)).ToArray();
        for (var index = 11; index <= 22; index++)
            context.World.StandardWalkFallbackAnimationTable[(150, index)] = new AvatarAnimationClip(
                $"walk-{index}", samples.Length, [], null, 15f, new System.Numerics.Vector3(15f, 0f, 0f), samples, 1000f);

        context.World.AvatarAnimationRootOnly = true;
        Assert.Equal(PrimitiveResult.Stop, RunDirectWalkFollowStep(context, 3));
        var initialPosition = person.Position;
        var result = PrimitiveResult.Stop;
        for (var tick = 0; tick < 4 && result == PrimitiveResult.Stop; tick++)
            result = RunDirectWalkFollowStep(context, 3);
        Assert.Equal(PrimitiveResult.Stop, result);
        var route = Assert.IsType<RouteState>(person.NativeRoute);
        Assert.NotEqual(0f, route.MotionFrame);
        Assert.NotEqual((float)person.Position.X, route.MotionX);
        Assert.NotEqual((float)person.Position.Y, route.MotionY);
        Assert.Equal(0f, route.MotionPracticeFrame);
        Assert.Equal(1.6f, person.AnimatorWalkingScale);
        Assert.Equal(route.MotionPlaybackScale, person.AnimatorWalkingScale);
        var practiceFrame = route.MotionPracticeFrame;
        var poseFrame = person.AnimationPlayback!.Frame;
        Assert.Equal(0f, poseFrame);
        Assert.Equal(poseFrame, person.AnimationPlayback!.Frame);
        Assert.NotEqual(initialPosition, person.Position);

        context.World.AvatarAnimationRootOnly = false;
        result = RunDirectWalkFollowStep(context, 3);
        Assert.Equal(PrimitiveResult.Stop, result);
        Assert.True(person.NativeRoute!.MotionPracticeFrame > practiceFrame);
        Assert.True(person.NativeRoute.MotionPracticeFrame >
                    samples.Length * 42f * person.NativeRoute.MotionPlaybackScale / 1000f);
        Assert.True(person.AnimationPlayback!.Frame > poseFrame);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ChangingStandingAdjustSkillRestartsItsPracticePhase(int walkStyle)
    {
        var (context, person, _) = World(stackObject: 3);
        context.World.Host = null;
        person[ObjectData.WalkStyle] = (short)walkStyle;
        person.TypeRecord = new TypeRecord(0x1234) { FootprintMask = 0 };
        var samples = Enumerable.Range(0, 121).Select(i => new System.Numerics.Vector3(i / 120f, 0f, 0f)).ToArray();
        for (var index = 11; index <= 22; index++)
            context.World.StandardWalkFallbackAnimationTable[(150, index)] = new AvatarAnimationClip(
                $"walk-{index}", samples.Length, [], null, index is 19 or 22 ? 3f : 15f,
                new System.Numerics.Vector3(15f, 0f, 0f), samples, 1000f);

        Assert.Equal(PrimitiveResult.Stop, StartDirectWalkFollow(context, 3));
        person.AnimatorRouteTransitionState = 1; // Hold the source state beyond the initial route-entry dispatch.
        Assert.Equal(0, person.AnimatorPoolTransitionState);
        context.World.TickAvatarAnimations(person);
        Assert.Equal(1, person.AnimatorPoolTransitionState);
        Assert.Equal(PrimitiveResult.Stop, RunDirectWalkFollowStep(context, 3));
        var route = Assert.IsType<RouteState>(person.NativeRoute);
        Assert.InRange(route.MotionAnimationIndex, 11, 18);
        Assert.Equal(1, person.AnimatorPoolTransitionState);
        var oldDirection = (short)((route.MotionDirection + 4) & 7);
        person.NativeRoute = route with
        {
            MotionDirection = oldDirection,
            MotionAnimationIndex = RouteExecutor.DirectionalWalkAnimation(oldDirection),
            MotionTargetX = int.MinValue,
            MotionTargetY = int.MinValue,
            MotionPath = null,
            MotionFrame = 10f,
            MotionPracticeFrame = 17f,
        };

        var practiceScaleFromPreviousTick = person.AnimatorWalkingScale;
        Assert.Equal(PrimitiveResult.Stop, RunDirectWalkFollowStep(context, 3));

        Assert.NotEqual(oldDirection, person.NativeRoute!.MotionDirection);
        var expectedPracticeStep = samples.Length * 42f / 1000f * practiceScaleFromPreviousTick;
        Assert.Equal(expectedPracticeStep, person.NativeRoute.MotionFrame, 3);
        Assert.Equal(expectedPracticeStep, person.NativeRoute.MotionPracticeFrame, 3);
        Assert.Equal(RouteExecutor.DirectionalWalkAnimation(person.NativeRoute.MotionDirection), person.NativeRoute.MotionAnimationIndex);
        Assert.Equal(1, person.AnimatorPoolTransitionState);
        Assert.Equal($"walk-{person.NativeRoute.MotionAnimationIndex}", person.AnimationPlayback!.Clip.Name);
    }

    [Fact]
    public void HostlessFrontModeUsesTheRotatedSixteenUnitPath()
    {
        var (context, person, _) = World();
        context.World.Host = null;
        AddWalkClips(context.World);

        Assert.Equal(PrimitiveResult.Stop, RunLookTowardsStep(context, Operand(1)));

        var path = person.NativeRoute!.FollowPath!;
        Assert.Equal(new ObjectPosition(170, 180, 0), new ObjectPosition(path[0].X, path[0].Y, 0));
        Assert.Equal(new ObjectPosition(170, 180, 0), new ObjectPosition(path[1].X, path[1].Y, 0));
        Assert.Equal(new ObjectPosition(186, 196, 0), new ObjectPosition(path[2].X, path[2].Y, 0));
        Assert.Null(person.AnimationPlayback);

        var result = PrimitiveResult.Stop;
        for (var tick = 0; tick < 64 && result == PrimitiveResult.Stop; tick++)
            result = RunLookTowardsStep(context, Operand(1));
        Assert.Equal(PrimitiveResult.True, result);
        Assert.Null(person.NativeRoute);
        Assert.Equal(new ObjectPosition(170, 180, 0), person.Position);
        Assert.Equal((short)3, person[ObjectData.Direction]);
    }

    [Theory]
    [InlineData(0, 186, 196)]
    [InlineData(1, 186, 164)]
    [InlineData(2, 154, 164)]
    [InlineData(3, 154, 196)]
    public void FrontPathRotatesTheSourceOffsetWithTheLot(int orientation, int expectedX, int expectedY)
    {
        var (context, person, _) = World();
        context.World.Host = null;
        context.World.SetLotOrientation(orientation);

        Assert.Equal(PrimitiveResult.Stop, RunLookTowardsStep(context, Operand(1)));

        var front = person.NativeRoute!.FollowPath![^1];
        Assert.Equal((expectedX, expectedY), (front.X, front.Y));
    }

    [Fact]
    public void LookTowardsDoesNotCancelFollowWhenInteractionCancellationIsPending()
    {
        var (context, person, _) = World();
        context.World.Host = null;
        AddWalkClips(context.World);
        Assert.Equal(PrimitiveResult.Stop, RunLookTowardsStep(context, Operand(2)));
        person.ActiveActionCancelRequested = true;
        person.NativeRoute = person.NativeRoute! with { AnimatorFollowState = 3 };

        Assert.NotEqual(PrimitiveResult.False,
            RouteExecutor.Continue(context.World, person, context.Frame, stack: null));
    }

    [Fact]
    public void ModeOneTurnsToTheFrontAndNeedsNoStackObject()
    {
        var (context, _, host) = World();
        host.TurnStatusAnswer = 0;
        Assert.Equal(PrimitiveResult.True, RunLookTowardsStep(context, Operand(1)));
        Assert.Equal("TurnTowards(100,null)", host.Calls[0]);
    }

    [Fact]
    public void ASittingPersonAnswersFalseOnTheFirstTickWithoutTurning()
    {
        var (context, me, host) = World();
        me[PersonData.Sitting] = 1;
        Assert.Equal(PrimitiveResult.False, RunLookTowardsStep(context, Operand(2)));
        Assert.Empty(host.Calls);
        Assert.False(context.Frame.Waiting);
    }

    [Fact]
    public void ASittingPersonDoesNotStopATurnThatIsRunning()
    {
        var (context, me, host) = World();
        me[PersonData.Sitting] = 1;
        context.Frame.Waiting = true;
        host.TurnStatusAnswer = 0;
        Assert.Equal(PrimitiveResult.True, RunLookTowardsStep(context, Operand(2, 2)));
        Assert.Equal(new[] { "TurnStatus(100)", "TryStartTurn(100)" }, host.Calls);
    }

    [Fact]
    public void TurningTowardsNeedsTheFrameStackObject()
    {
        var (context, _, host) = World(stackObject: 77);
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => RunLookTowardsStep(context, Operand(2, 77))));
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => RunLookTowardsStep(context, Operand(3, 77))));
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(-1)]
    [InlineData(0x100)]
    public void ModesOutsideZeroToThreeAreBadParameters(int mode)
    {
        var (context, _, host) = World();
        Assert.Equal(SimulationError.BadParameterInLookTowardsPrimitive, Fails(() => RunLookTowardsStep(context, Operand(mode))));
        // The range check comes before the started flag, so a turn that is running is refused too.
        context.Frame.Waiting = true;
        Assert.Equal(SimulationError.BadParameterInLookTowardsPrimitive, Fails(() => RunLookTowardsStep(context, Operand(mode))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void BusyTurnStopsAndTheNextTickOnlyWaits()
    {
        var (context, _, host) = World();
        host.TurnStatusAnswer = 2;
        Assert.Equal(PrimitiveResult.Stop, RunLookTowardsStep(context, Operand(2, 2)));
        Assert.Equal(new[] { "TurnTowards(100,190,200)", "TurnStatus(100)" }, host.Calls);
        Assert.True(context.Frame.Waiting);

        // The started flag is set, so the second tick does not turn again and does not try to start a turn.
        host.Calls.Clear();
        Assert.Equal(PrimitiveResult.Stop, RunLookTowardsStep(context, Operand(2, 2)));
        Assert.Equal(new[] { "TurnStatus(100)" }, host.Calls);

        host.Calls.Clear();
        host.TurnStatusAnswer = 0;
        Assert.Equal(PrimitiveResult.True, RunLookTowardsStep(context, Operand(2, 2)));
        Assert.Equal(new[] { "TurnStatus(100)", "TryStartTurn(100)" }, host.Calls);
    }

    [Fact]
    public void ARefusedStartWaitsForTheTurnToEnd()
    {
        var (context, _, host) = World();
        host.TurnStatusAnswer = 1;
        host.TryStartAnswer = false;
        Assert.Equal(PrimitiveResult.Stop, RunLookTowardsStep(context, Operand(1)));
        Assert.Equal(new[] { "TurnTowards(100,null)", "TurnStatus(100)", "TryStartTurn(100)" }, host.Calls);
    }

    [Fact]
    public void StatusOneStartsTheTurnAndAnswersTrue()
    {
        var (context, _, host) = World();
        host.TurnStatusAnswer = 1;
        host.TryStartAnswer = true;
        Assert.Equal(PrimitiveResult.True, RunLookTowardsStep(context, Operand(1)));
        Assert.Equal(new[] { "TurnTowards(100,null)", "TurnStatus(100)", "TryStartTurn(100)" }, host.Calls);
    }

    [Fact]
    public void PersonsRunLookTowardsThroughTheTreeRunner()
    {
        var (context, _, host) = World();
        host.TurnStatusAnswer = 0;
        var tree = PrimitiveTestWorld.Tree(0, (0x16, 0xFE, 0xFF, Operand(2, 2)));
        Assert.True(TreeRunner.Run(context, tree));
        Assert.Equal("TurnTowards(100,190,200)", host.Calls[0]);
    }

    [Fact]
    public void NonPersonsRaiseIllegalPrimitiveCodeFromTheMainDispatcher()
    {
        var (context, _) = NonPerson();
        var tree = PrimitiveTestWorld.Tree(0, (0x16, 0xFE, 0xFF, Operand(0)));
        Assert.Equal(SimulationError.IllegalPrimitiveCode, Fails(() => TreeRunner.Run(context, tree)));
    }

    [Theory]
    [InlineData("male", 0)]
    [InlineData("female", 1)]
    [InlineData("dogmale", 2)]
    [InlineData("dogfemale", 3)]
    [InlineData("catmale", 4)]
    [InlineData("catfemale", 5)]
    // No compareNoCase match falls through to SAnimator.s 0x10358818 without writing +0x60e.
    [InlineData("unknown", null)]
    public void ResetSuitsBodyStringEntryThirteenSelectsTheMacAnimatorMode(string value, int? expected)
    {
        Assert.Equal((short?)expected, SavedLotObjectLoader.MapAnimatorBodyString13(value));
    }

    [Theory]
    [InlineData(0, 0, 0, 1.6f)]
    [InlineData(0, 9, 2, 1.6f)]
    [InlineData(1, 0, 0, 4.4f)]
    [InlineData(1, 0, 2, 4.4f)]
    [InlineData(1, 3, 0, 1.6f)]
    [InlineData(1, 4, 0, 4.4f)]
    [InlineData(1, 5, 1, 4.4f)]
    [InlineData(1, 6, 0, 4.4f)]
    [InlineData(1, 9, 1, 4.4f)]
    [InlineData(1, 4, 2, 1.2f)]
    [InlineData(1, 5, 3, 1.2f)]
    [InlineData(1, 6, 4, 1.2f)]
    [InlineData(1, 9, 5, 1.2f)]
    [InlineData(1, 10, 0, 1.6f)]
    [InlineData(1, 11, 2, 1.6f)]
    public void WalkingScaleMatchesMacWalkStyleControlStateAndBodyMode(int walkStyle, int controlState,
        short bodyStringMode, float expectedScale)
    {
        var (context, person, _) = World();
        context.World.Host = null;
        person[ObjectData.WalkStyle] = (short)walkStyle;
        person.AnimatorControlState = controlState;
        person.AnimatorBodyStringMode = bodyStringMode;
        person.AnimatorWalkingScale = 1f;
        for (var tick = 0; tick < 20; tick++) context.World.TickAvatarAnimations(person);
        Assert.Equal(expectedScale, person.AnimatorWalkingScale, 3);
    }
}
