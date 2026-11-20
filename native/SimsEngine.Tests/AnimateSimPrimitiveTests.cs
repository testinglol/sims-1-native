using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using System.Text;
using Xunit;

namespace SimsEngine.Tests;

public sealed class AnimateSimPrimitiveTests
{
    [Fact]
    public void MotionEventFramesConvertUsingTheOwningMotionsFrameCountAndSkillDuration()
    {
        var firstMotion = AvatarAnimationEvent.FromMotionFrame(frame: 3, value: 11, motionFrameCount: 6,
            durationMilliseconds: 1200f);
        var secondMotion = AvatarAnimationEvent.FromMotionFrame(frame: 3, value: 12, motionFrameCount: 12,
            durationMilliseconds: 1200f);

        Assert.Equal(600, firstMotion.TimeMilliseconds);
        Assert.Equal(300, secondMotion.TimeMilliseconds);

        var playback = new AvatarAnimationPlayback(new AvatarAnimationClip("motion-timing", 12,
            [secondMotion, firstMotion], DurationMilliseconds: 1200f), backwards: false, loop: false, startedAtMils: 0);
        playback.Tick(animatorMils: 300);
        Assert.Equal(new short[] { 12 }, playback.EventQueue);
        playback.Tick(animatorMils: 600);
        Assert.Equal(new short[] { 12, 11 }, playback.EventQueue);
    }

    [Fact]
    public void PoseInterpolationStopsWhenAnUpdateSpansTwoFrameIntervals()
    {
        var playback = new AvatarAnimationPlayback(new AvatarAnimationClip("pose-interpolation", 3, [],
            DurationMilliseconds: 300f), backwards: false, loop: true, startedAtMils: 0);

        playback.Tick(animatorMils: 199);
        Assert.True(playback.InterpolatePose);
        playback.Tick(animatorMils: 399);
        Assert.False(playback.InterpolatePose);
        playback.Tick(animatorMils: 598);
        Assert.True(playback.InterpolatePose);

        var restored = AvatarAnimationPlayback.RestoreState(playback.SaveState());
        Assert.True(restored.InterpolatePose);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoopingPracticeWrapsAcrossMultipleCyclesWhileKeepingTheFractionalPhase(bool backwards)
    {
        var clip = new AvatarAnimationClip("short-loop", 4, [], DurationMilliseconds: 28f);
        var playback = new AvatarAnimationPlayback(clip, backwards, loop: true);

        playback.Tick();

        Assert.Equal(2f, playback.Frame);
        Assert.False(playback.EndReached);
    }

    [Fact]
    public void LoopingPracticeKeepsTheExactForwardEndpointUntilTheNextTick()
    {
        var clip = new AvatarAnimationClip("endpoint-loop", 4, [], DurationMilliseconds: 168f);
        var playback = new AvatarAnimationPlayback(clip, backwards: false, loop: true, startedAtMils: 0);

        playback.Tick(animatorMils: 168);
        Assert.Equal(4f, playback.Frame);
        Assert.False(playback.EndReached);

        playback.Tick(animatorMils: 210);
        Assert.Equal(1f, playback.Frame);
        Assert.False(playback.EndReached);
    }

    [Fact]
    public void LoopingPracticeProcessesTimedEventsAcrossTheWrapInOneTick()
    {
        var clip = new AvatarAnimationClip("event-wrap", 4,
            [new AvatarAnimationEvent(0, 1), new AvatarAnimationEvent(42, 2),
             new AvatarAnimationEvent(126, 3), new AvatarAnimationEvent(168, 4)],
            DurationMilliseconds: 168f);
        var playback = new AvatarAnimationPlayback(clip, backwards: false, loop: true, startedAtMils: 0);

        playback.Tick(animatorMils: 126);
        Assert.Equal(new short[] { 1, 2, 3 }, playback.EventQueue);
        playback.EventQueue.Clear();

        playback.Tick(animatorMils: 210);

        Assert.Equal(new short[] { 4, 1, 2 }, playback.EventQueue);
        Assert.Equal(1f, playback.Frame);
    }

    [Fact]
    public void BackwardsLoopingPracticeProcessesTimedEventsAcrossTheWrapInOneTick()
    {
        var clip = new AvatarAnimationClip("backwards-event-wrap", 4,
            [new AvatarAnimationEvent(0, 1), new AvatarAnimationEvent(42, 2),
             new AvatarAnimationEvent(126, 3), new AvatarAnimationEvent(168, 4)],
            DurationMilliseconds: 168f);
        var playback = new AvatarAnimationPlayback(clip, backwards: true, loop: true, startedAtMils: 0);

        playback.Tick(animatorMils: 126);
        Assert.Equal(new short[] { 4, 3, 2 }, playback.EventQueue);
        playback.EventQueue.Clear();

        playback.Tick(animatorMils: 210);

        Assert.Equal(new short[] { 1, 4, 3 }, playback.EventQueue);
        Assert.Equal(3f, playback.Frame);
    }

    [Fact]
    public void PracticeApplyCatchesUpElapsedMillisecondsAfterRootOnlyTicks()
    {
        var clip = new AvatarAnimationClip("elapsed-practice", 100, [], DurationMilliseconds: 1000f);
        var playback = new AvatarAnimationPlayback(clip, backwards: false, loop: true, startedAtMils: 0);

        playback.Tick(animatorMils: 42);
        var heldFrame = playback.Frame;
        playback.Tick(animatorMils: 210);

        Assert.Equal(4.2f, heldFrame, precision: 4);
        Assert.Equal(21f, playback.Frame, precision: 4);
        Assert.Equal(210, playback.SaveState().LastPracticeApplyMils);

        var restored = AvatarAnimationPlayback.RestoreState(playback.SaveState());
        restored.Tick(animatorMils: 252);
        Assert.Equal(25.2f, restored.Frame, precision: 4);
    }

    [Fact]
    public void OneShotPracticeClampsToTheEndpointBeforeProcessingTimedEvents()
    {
        var clip = new AvatarAnimationClip("one-shot", 2,
            [new AvatarAnimationEvent(42, 1), new AvatarAnimationEvent(50, 2)], DurationMilliseconds: 42f);
        var playback = new AvatarAnimationPlayback(clip, backwards: false, loop: false, startedAtMils: 0);

        playback.Tick(animatorMils: 84);

        Assert.Equal(2f, playback.Frame);
        Assert.True(playback.EndReached);
        Assert.Equal(new short[] { 1 }, playback.EventQueue);
    }

    [Fact]
    public void ZeroWeightPracticeDefersTimedEventsUntilItsWeightBecomesPositive()
    {
        var sound = new AvatarAnimationSoundEvent(30, "base", "sound.iff", "cue", false, false);
        var clip = new AvatarAnimationClip("faded-practice", 4,
            [new AvatarAnimationEvent(30, 7)], [sound], DurationMilliseconds: 168f);
        var playback = new AvatarAnimationPlayback(clip, backwards: false, loop: true, startedAtMils: 0);
        playback.FadePracticeWeight(startMils: 0, endMils: 42, startWeight: 1f, targetWeight: 0f);

        playback.Tick(animatorMils: 42);

        Assert.Equal(0f, playback.PracticeWeight);
        Assert.Empty(playback.EventQueue);
        Assert.Empty(playback.SoundEventQueue);

        playback.FadePracticeWeight(startMils: 42, endMils: 84, startWeight: 0f, targetWeight: 1f);
        playback.Tick(animatorMils: 84);

        Assert.Equal(1f, playback.PracticeWeight);
        Assert.Equal(new short[] { 7 }, playback.EventQueue);
        Assert.Equal([sound], playback.SoundEventQueue);
    }

    [Fact]
    public void PracticeWeightSurvivesPlaybackSnapshotRestore()
    {
        var playback = new AvatarAnimationPlayback(new AvatarAnimationClip("overlay", 2, []), false, true)
        {
            PracticeWeight = 0.25f,
        };

        var restored = AvatarAnimationPlayback.RestoreState(playback.SaveState());

        Assert.Equal(0.25f, restored.PracticeWeight);
    }

    [Fact]
    public void PracticeWeightFadeInterpolatesOverAnimatorMilliseconds()
    {
        var playback = new AvatarAnimationPlayback(new AvatarAnimationClip("overlay", 4, []), false, true)
        {
            PracticeWeight = 0.25f,
        };
        playback.FadePracticeWeight(startMils: 100, endMils: 200, startWeight: 0.25f, targetWeight: 0.75f);

        playback.Tick(animatorMils: 150);
        Assert.Equal(0.5f, playback.PracticeWeight);
        playback.Tick(animatorMils: 201);
        Assert.Equal(0.75f, playback.PracticeWeight);
        Assert.False(playback.SaveState().PracticeWeightFadeActive);
    }

    [Fact]
    public void PracticeWeightFadeRestoresItsMillisecondInterval()
    {
        var playback = new AvatarAnimationPlayback(new AvatarAnimationClip("overlay", 4, []), false, true)
        {
            PracticeWeight = 0.25f,
        };
        playback.FadePracticeWeight(startMils: 100, endMils: 200, startWeight: 0.25f, targetWeight: 0.75f);
        playback.Tick(animatorMils: 150);
        var restored = AvatarAnimationPlayback.RestoreState(playback.SaveState());

        restored.Tick(animatorMils: 175);

        Assert.Equal(0.625f, restored.PracticeWeight);
        Assert.True(restored.SaveState().PracticeWeightFadeActive);
    }

    [Fact]
    public void PersonUpdateSelectsAndStopsTheBackgroundPracticeFromSittingState()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person[PersonData.Gender] = 0;
        context.World.StandardWalkFallbackAnimationTable[(152, 1)] = new AvatarAnimationClip("sit-a", 4, []);
        context.World.StandardWalkFallbackAnimationTable[(152, 2)] = new AvatarAnimationClip("sit-b", 5, []);
        context.World.StandardWalkFallbackAnimationTable[(152, 3)] = new AvatarAnimationClip("sit-c", 6, []);

        person[PersonData.Sitting] = 1;
        context.World.UpdateBackgroundPractice(person);
        Assert.Equal("sit-a", person.BackgroundAnimationPlayback?.Clip.Name);
        Assert.Equal((short)1, person.BackgroundPracticePersonState);

        person[PersonData.Sitting] = 2;
        context.World.UpdateBackgroundPractice(person);
        Assert.Equal("sit-b", person.BackgroundAnimationPlayback?.Clip.Name);

        person[PersonData.Sitting] = 0;
        context.World.UpdateBackgroundPractice(person);
        Assert.Equal("sit-c", person.BackgroundAnimationPlayback?.Clip.Name);
        Assert.Equal((short)0, person.BackgroundPracticePersonState);

        person[PersonData.Sitting] = -1;
        context.World.UpdateBackgroundPractice(person);
        Assert.Null(person.BackgroundAnimationPlayback);
        Assert.Equal((short)-1, person.BackgroundPracticePersonState);
    }

    [Fact]
    public void UnchangedOrdinaryGenderBackgroundPracticeKeepsItsCurrentFrame()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person[PersonData.Gender] = 0;
        context.World.StandardWalkFallbackAnimationTable[(152, 1)] = new AvatarAnimationClip("sit-a", 4, []);
        person[PersonData.Sitting] = 1;
        context.World.UpdateBackgroundPractice(person);
        var playback = Assert.IsType<AvatarAnimationPlayback>(person.BackgroundAnimationPlayback);
        playback.SetFrame(2);

        context.World.UpdateBackgroundPractice(person);

        Assert.Same(playback, person.BackgroundAnimationPlayback);
        Assert.Equal(2, playback.Frame);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public void BackgroundPracticeUsesResetSuitsBodyStringMode(int bodyStringMode, bool refreshesPractice)
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person[PersonData.Gender] = 0;
        person.AnimatorBodyStringMode = (short)bodyStringMode;
        person[PersonData.Sitting] = 1;
        context.World.StandardWalkFallbackAnimationTable[(152, 1)] = new AvatarAnimationClip("sit-a", 4, []);
        context.World.UpdateBackgroundPractice(person);
        var playback = Assert.IsType<AvatarAnimationPlayback>(person.BackgroundAnimationPlayback);
        playback.SetFrame(2);

        context.World.UpdateBackgroundPractice(person);

        if (refreshesPractice)
        {
            Assert.NotSame(playback, person.BackgroundAnimationPlayback);
            Assert.Equal(0, person.BackgroundAnimationPlayback?.Frame);
        }
        else
        {
            Assert.Same(playback, person.BackgroundAnimationPlayback);
            Assert.Equal(2, person.BackgroundAnimationPlayback?.Frame);
        }
    }

    [Fact]
    public void FirstPersonUpdateProcessesTheAnimatorsMinusOneBackgroundState()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person.BackgroundAnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("mode-one", 4, []), false, true);

        context.World.UpdateBackgroundPractice(person);

        Assert.Null(person.BackgroundAnimationPlayback);
        Assert.Equal((short)0, person.BackgroundPracticePersonState);
    }

    [Fact]
    public void CarryPracticeTracksTheFirstHandSlotAndPausesInferenceDuringReach()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person[PersonData.Gender] = 0;
        context.World.StandardWalkFallbackAnimationTable[(152, 0x26)] = new AvatarAnimationClip("carry-practice", 8, []);
        person.FootprintSlots.Add(new ObjectFootprintSlot { OccupantObjectId = 42 });

        context.World.TickAvatarAnimations(person);

        var carry = Assert.IsType<AvatarAnimationPlayback>(person.CarryAnimationPlayback);
        Assert.Equal("carry-practice", carry.Clip.Name);
        Assert.True(carry.Loop);
        Assert.Equal(1, person.CarryPracticeMode);
        Assert.Equal(0, person.CarryPracticeCachedState);

        person.ReachAnimationActive = true;
        context.World.TickAvatarAnimations(person);
        Assert.Null(person.CarryAnimationPlayback);
        Assert.Equal(1, person.CarryPracticeMode);
        Assert.Equal(-1, person.CarryPracticeCachedState);

        person.ReachAnimationActive = false;
        context.World.TickAvatarAnimations(person);
        Assert.Equal("carry-practice", person.CarryAnimationPlayback?.Clip.Name);
        Assert.Equal(1, person.CarryPracticeMode);
        Assert.Equal(0, person.CarryPracticeCachedState);

        person.FootprintSlots[0].OccupantObjectId = 0;
        context.World.TickAvatarAnimations(person);
        Assert.Null(person.CarryAnimationPlayback);
        Assert.Equal(0, person.CarryPracticeMode);
        Assert.Equal(-1, person.CarryPracticeCachedState);
    }

    [Fact]
    public void AnimatorTickStartsCarryAndBackgroundAtTheCurrentPracticeTime()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person[PersonData.Gender] = 0;
        person[PersonData.Sitting] = 1;
        context.World.StandardWalkFallbackAnimationTable[(152, 0x26)] = new AvatarAnimationClip("carry-practice", 8, []);
        context.World.StandardWalkFallbackAnimationTable[(152, 1)] = new AvatarAnimationClip("background-practice", 8, []);
        person.FootprintSlots.Add(new ObjectFootprintSlot { OccupantObjectId = 42 });

        context.World.TickAvatarAnimations(person);

        Assert.Equal("carry-practice", person.CarryAnimationPlayback?.Clip.Name);
        Assert.Equal("background-practice", person.BackgroundAnimationPlayback?.Clip.Name);
        Assert.Equal(0f, person.CarryAnimationPlayback!.Frame);
        Assert.Equal(0f, person.BackgroundAnimationPlayback!.Frame);
    }

    [Fact]
    public void AutomaticCarryPracticeLayersBelowThePrimaryAnimation()
    {
        var (_, person, _) = PrimitiveTestWorld.Create();
        person.BackgroundAnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("background", 4, []), false, true);
        person.CarryAnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("automatic-carry", 4, []), false, true);
        person.AnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("primary", 4, []), false, true);
        person.CarryPracticeMode = 1;

        var order = person.GetAnimationPracticeApplicationOrder().Select(playback => playback.Clip.Name).ToArray();
        Assert.Contains("background", order);
        Assert.True(Array.IndexOf(order, "automatic-carry") > Array.IndexOf(order, "primary"));
    }

    [Fact]
    public void EqualPriorityBackgroundAndCarryPracticesKeepTheirStartOrderAfterSnapshotRestore()
    {
        var (_, person, _) = PrimitiveTestWorld.Create();
        var carry = new AvatarAnimationPlayback(new AvatarAnimationClip("automatic-carry", 4, []), false, true);
        var restoredCarry = AvatarAnimationPlayback.RestoreState(carry.SaveState());
        var background = new AvatarAnimationPlayback(new AvatarAnimationClip("background", 4, []), false, true);
        person.CarryAnimationPlayback = restoredCarry;
        person.CarryPracticeMode = 1;
        person.BackgroundAnimationPlayback = background;
        person.AnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("primary", 4, []), false, true);

        var order = person.GetAnimationPracticeApplicationOrder().Select(playback => playback.Clip.Name).ToArray();

        Assert.Equal(carry.PracticeStartOrder, restoredCarry.PracticeStartOrder);
        Assert.Equal(new[] { "background", "primary", "automatic-carry" }, order);
    }

    [Fact]
    public void ExplicitCarryPracticeLayersAboveThePrimaryAnimation()
    {
        var (_, person, _) = PrimitiveTestWorld.Create();
        person.BackgroundAnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("background", 4, []), false, true);
        person.CarryAnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("explicit-carry", 4, []), false, true);
        person.AnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("primary", 4, []), false, true);
        person.CarryPracticeMode = 6;

        var order = person.GetAnimationPracticeApplicationOrder().Select(playback => playback.Clip.Name).ToArray();
        Assert.Contains("background", order);
        Assert.True(Array.IndexOf(order, "explicit-carry") > Array.IndexOf(order, "primary"));
    }

    [Fact]
    public void VitaBoyFlagSkipsBackgroundPracticeAndLeavesTheCachedStateUnchanged()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person[PersonData.Gender] = 0;
        context.World.StandardWalkFallbackAnimationTable[(152, 1)] = new AvatarAnimationClip("sit-a", 4, []);
        person.BackgroundAnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("existing", 4, []), false, true);
        person.Definition[(int)ObjdField.VitaBoyFlags] = 4;
        person[PersonData.Sitting] = 1;

        context.World.UpdateBackgroundPractice(person);

        Assert.Null(person.BackgroundAnimationPlayback);
        Assert.Equal((short)-1, person.BackgroundPracticePersonState);

        person.Definition[(int)ObjdField.VitaBoyFlags] = 0;
        context.World.UpdateBackgroundPractice(person);

        Assert.Equal("sit-a", person.BackgroundAnimationPlayback?.Clip.Name);
        Assert.Equal((short)1, person.BackgroundPracticePersonState);
    }

    [Fact]
    public void VitaBoyFlagPreservesUnchangedBackgroundPractice()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person[PersonData.Gender] = 0;
        person[PersonData.Sitting] = 1;
        context.World.StandardWalkFallbackAnimationTable[(152, 1)] = new AvatarAnimationClip("sit-a", 4, []);
        context.World.UpdateBackgroundPractice(person);
        var playback = Assert.IsType<AvatarAnimationPlayback>(person.BackgroundAnimationPlayback);
        playback.SetFrame(2);
        person.Definition[(int)ObjdField.VitaBoyFlags] = 4;

        context.World.UpdateBackgroundPractice(person);

        Assert.Same(playback, person.BackgroundAnimationPlayback);
        Assert.Equal(2, playback.Frame);
        Assert.Equal((short)1, person.BackgroundPracticePersonState);
    }

    [Fact]
    public void RootOnlyAnimatorTickDoesNotAdvanceAnyAnimationChannel()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person.AnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("primary", 4, [new(0, 1)]), false, true);
        person.BackgroundAnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("background", 4, []), false, true);
        person.CarryAnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("carry", 4, []), false, true);
        person.BackgroundPracticePersonState = 0;

        context.World.AvatarAnimationRootOnly = true;
        context.World.TickAvatarAnimations(person);

        Assert.Equal(0, person.AnimationPlayback!.Frame);
        Assert.Equal(0, person.BackgroundAnimationPlayback!.Frame);
        Assert.Equal(0, person.CarryAnimationPlayback!.Frame);
        Assert.Empty(person.AnimationPlayback.EventQueue);

        context.World.AvatarAnimationRootOnly = false;
        context.World.TickAvatarAnimations(person);

        Assert.True(person.AnimationPlayback.Frame > 0);
        Assert.True(person.BackgroundAnimationPlayback.Frame > 0);
        Assert.True(person.CarryAnimationPlayback.Frame > 0);
        Assert.Empty(person.AnimationPlayback.EventQueue);
        Assert.Equal(new short[] { 1 }, person.PendingAnimationEvents);
    }

    [Fact]
    public void RootOnlyAnimatorTicksAdvanceClockButHoldPracticeFadeUntilFullTick()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        var playback = new AvatarAnimationPlayback(new AvatarAnimationClip("primary", 4, []), false, true);
        playback.FadePracticeWeight(startMils: 0, endMils: 100, startWeight: 1f, targetWeight: 0f);
        person.AnimationPlayback = playback;

        context.World.AvatarAnimationRootOnly = true;
        context.World.TickAvatarAnimations(person);
        Assert.Equal(42, person.AnimatorMilliseconds);
        Assert.Equal(1f, playback.PracticeWeight);

        context.World.AvatarAnimationRootOnly = false;
        context.World.TickAvatarAnimations(person);

        Assert.Equal(84, person.AnimatorMilliseconds);
        Assert.Equal(0.16f, playback.PracticeWeight, precision: 5);
    }

    [Fact]
    public void ActivePracticeXevtsEnterTheSharedAnimatorQueueInApplicationOrder()
    {
        var (context, person, _) = PrimitiveTestWorld.Create(parameters: [0]);
        person[PersonData.PersonAge] = 18;
        person[PersonData.Gender] = 0;
        person[PersonData.Sitting] = -1;
        person.BackgroundPracticePersonState = -1;
        person.BackgroundAnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("background", 4,
            [new AvatarAnimationEvent(0, 0)]), false, true);
        person.AnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("primary", 4,
            [new AvatarAnimationEvent(0, 1)]), false, false);
        person.CarryPracticeMode = 6;
        person.CarryAnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("carry", 4,
            [new AvatarAnimationEvent(0, 2)]), false, false);

        context.World.TickAvatarAnimations(person);

        Assert.Equal(new short[] { 0, 1, 2 }, person.PendingAnimationEvents);
        Assert.Empty(person.BackgroundAnimationPlayback!.EventQueue);
        Assert.Empty(person.AnimationPlayback.EventQueue);
        Assert.Empty(person.CarryAnimationPlayback!.EventQueue);

        byte[] waitForSharedEvents = [58, 0, 0, 0, 3, 8, 3, 0];
        Assert.Equal(PrimitiveResult.False, AnimateSimPrimitive.Execute(context, waitForSharedEvents));
        Assert.Equal((short)0, context.Frame.Parameters[0]);
        Assert.Equal(PrimitiveResult.False, AnimateSimPrimitive.Execute(context, waitForSharedEvents));
        Assert.Equal((short)1, context.Frame.Parameters[0]);
        Assert.Equal(PrimitiveResult.False, AnimateSimPrimitive.Execute(context, waitForSharedEvents));
        Assert.Equal((short)2, context.Frame.Parameters[0]);
    }

    [Fact]
    public void StartingANewPrimaryPracticeClearsTheSharedAnimatorEventQueue()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        context.World.AvatarAnimationTable[(3, false, 58)] = new AvatarAnimationClip("new-primary", 4, []);
        person.PendingAnimationEvents.AddRange([7, 8]);

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, [58, 0, 0, 0, 3, 0, 0, 0]));

        Assert.Empty(person.PendingAnimationEvents);
        Assert.Equal("new-primary", person.AnimationPlayback?.Clip.Name);
    }

    [Fact]
    public void RootOnlyAnimatorTickStillStartsChangedBackgroundPractice()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person[PersonData.Gender] = 0;
        person[PersonData.Sitting] = 1;
        context.World.StandardWalkFallbackAnimationTable[(152, 1)] = new AvatarAnimationClip("sit-a", 4, []);

        context.World.AvatarAnimationRootOnly = true;
        context.World.UpdateBackgroundPractice(person);
        context.World.TickAvatarAnimations(person);

        var playback = Assert.IsType<AvatarAnimationPlayback>(person.BackgroundAnimationPlayback);
        Assert.Equal("sit-a", playback.Clip.Name);
        Assert.Equal(0, playback.Frame);
        Assert.Equal((short)1, person.BackgroundPracticePersonState);
    }

    [Fact]
    public void MissingBackgroundAnimationRunsThePersonMissingAnimationTree()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        var treeBytes = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(0x0060).U8(BhavInstruction.ReturnTrue).U8(BhavInstruction.ReturnFalse).Raw(new byte[8]).ToArray();
        var privateBytes = ChunkBuilder.File(("BHAV", (ushort)4096, treeBytes));
        Encoding.Latin1.GetBytes("missing_animation_tree").CopyTo(privateBytes, IffFile.HeaderSize + 12);
        var scope = new BehaviorScope(new IffFile(ChunkBuilder.File(), "global.iff"), new IffFile(privateBytes, "person.iff"), null);
        person.TypeRecord = new TypeRecord(1) { BehaviorScope = scope };
        var stack = TreeRunner.CreateTransientStack(context.World, person);
        context = context with { Stack = stack };
        byte[] operand = [58, 0, 0, 0, 3, 1, 0, 0];

        var error = Assert.Throws<SimulationException>(() => AnimateSimPrimitive.Execute(context, operand));

        Assert.Equal(SimulationError.IllegalPrimitiveCode, error.Error);
    }

    [Fact]
    public void MissingPrimaryAnimationRunsThePersonTreeThenStartsTheStandardFallback()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person[PersonData.Gender] = 0;
        var treeBytes = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(7).U8(BhavInstruction.ReturnTrue).U8(BhavInstruction.ReturnFalse).Raw(new byte[8]).ToArray();
        var privateBytes = ChunkBuilder.File(("BHAV", (ushort)4096, treeBytes));
        Encoding.Latin1.GetBytes("missing_animation_tree").CopyTo(privateBytes, IffFile.HeaderSize + 12);
        var scope = new BehaviorScope(new IffFile(ChunkBuilder.File(), "global.iff"), new IffFile(privateBytes, "person.iff"), null);
        person.TypeRecord = new TypeRecord(1) { BehaviorScope = scope };
        context.World.StandardWalkFallbackAnimationTable[(152, 0x25)] = new AvatarAnimationClip("fallback", 4, []);
        context = context with { Stack = TreeRunner.CreateTransientStack(context.World, person) };
        byte[] operand = [58, 0, 0, 0, 3, 0, 0, 0];

        var result = AnimateSimPrimitive.Execute(context, operand);

        Assert.Equal(PrimitiveResult.Stop, result);
        Assert.Equal("fallback", person.AnimationPlayback?.Clip.Name);
        Assert.True(context.World.DisplayRefreshRequested);
    }

    [Fact]
    public void MiscPlayAndWaitStopsTheCurrentCallbackDeliversTimedEventsAndCompletesWithoutHost()
    {
        var (context, person, _) = PrimitiveTestWorld.Create(parameters: [0]);
        person[PersonData.PersonAge] = 18;
        context.World.AvatarAnimationTable[(3, false, 58)] = new AvatarAnimationClip("toilet", 4,
            [new AvatarAnimationEvent(0, 0), new AvatarAnimationEvent(33, 1)]);
        byte[] operand = [58, 0, 0, 0, 3, 8, 2, 0];

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, operand));
        var playback = Assert.IsType<AvatarAnimationPlayback>(person.AnimationPlayback);
        playback.Tick();
        Assert.Equal(PrimitiveResult.False, AnimateSimPrimitive.Execute(context, operand));
        Assert.Equal(0, context.Frame.Parameters[0]);
        Assert.Equal(PrimitiveResult.False, AnimateSimPrimitive.Execute(context, operand));
        Assert.Equal(1, context.Frame.Parameters[0]);

        while (!playback.EndReached) playback.Tick();
        Assert.Equal(PrimitiveResult.True, AnimateSimPrimitive.Execute(context, operand));
        Assert.Null(person.AnimationPlayback);
    }

    [Fact]
    public void ARouteOwnedWalkPracticeIsReplacedByAPortalFunctionsAnimation()
    {
        var (context, person, _) = PrimitiveTestWorld.Create(parameters: [0]);
        person[PersonData.PersonAge] = 18;
        context.World.AvatarAnimationTable[(3, false, 58)] = new AvatarAnimationClip("stair-climb", 4, []);
        person.AnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("walking-loop", 4, []), false, loop: false)
            { RouteDriven = true };

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, [58, 0, 0, 0, 3, 8, 0, 0]));
        var playback = Assert.IsType<AvatarAnimationPlayback>(person.AnimationPlayback);
        Assert.Equal("stair-climb", playback.Clip.Name);
        Assert.False(playback.RouteDriven);
    }

    [Fact]
    public void StoredXevtsDrainInOrderBeforeNormalAnimateCompletion()
    {
        var (context, person, _) = PrimitiveTestWorld.Create(parameters: [0]);
        person[PersonData.PersonAge] = 18;
        context.World.AvatarAnimationTable[(3, false, 58)] = new AvatarAnimationClip("snowman-build", 4,
            [new AvatarAnimationEvent(0, 201), new AvatarAnimationEvent(33, 202), new AvatarAnimationEvent(67, 999)]);
        context.Frame.Locals[0] = -1;
        byte[] operand = [58, 0, 0, 0, 3, 0x28, 0, 0];

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, operand));
        var playback = Assert.IsType<AvatarAnimationPlayback>(person.AnimationPlayback);
        foreach (var expected in new short[] { 201, 202, 999 })
        {
            playback.Tick();
            Assert.Equal(PrimitiveResult.False, AnimateSimPrimitive.Execute(context, operand));
            Assert.Equal(expected, context.Frame.Locals[0]);
        }

        while (!playback.EndReached) playback.Tick();
        Assert.Equal(PrimitiveResult.True, AnimateSimPrimitive.Execute(context, operand));
        Assert.Null(person.AnimationPlayback);
    }

    [Fact]
    public void UnexpectedNumberedXevtIsConsumedWithoutAdvancingOrWritingTheExpectedEvent()
    {
        var (context, person, _) = PrimitiveTestWorld.Create(parameters: [77]);
        person[PersonData.PersonAge] = 18;
        context.World.AvatarAnimationTable[(3, false, 58)] = new AvatarAnimationClip("unexpected-xevt", 3,
            [new AvatarAnimationEvent(0, 8), new AvatarAnimationEvent(42, 0)]);
        byte[] operand = [58, 0, 0, 0, 3, 8, 1, 0];

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, operand));
        var playback = Assert.IsType<AvatarAnimationPlayback>(person.AnimationPlayback);
        playback.Tick();

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, operand));
        Assert.Equal((short)77, context.Frame.Parameters[0]);
        Assert.Equal((byte)0, playback.EventsRun);

        playback.Tick();
        Assert.Equal(PrimitiveResult.False, AnimateSimPrimitive.Execute(context, operand));
        Assert.Equal((short)0, context.Frame.Parameters[0]);
        Assert.Equal((byte)1, playback.EventsRun);
    }

    [Fact]
    public void InterruptiblePrimaryAnimationCompletesWhenSourceShouldInterruptBecomesTrue()
    {
        var (context, person, _) = PrimitiveTestWorld.Create(parameters: [0]);
        person[PersonData.PersonAge] = 18;
        person.ActionQueueEnd = 1;
        person.ActionQueue[0].Flags = 2;
        context.World.AvatarAnimationTable[(3, false, 58)] = new AvatarAnimationClip("interruptible", 8, []);
        byte[] interruptible = [58, 0, 0, 0, 3, 0x08, 2, 0];

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, interruptible));
        var playback = Assert.IsType<AvatarAnimationPlayback>(person.AnimationPlayback);
        Assert.False(playback.EndReached);
        Assert.True(playback.Interruptible);

        Assert.Equal(PrimitiveResult.False, AnimateSimPrimitive.Execute(context, interruptible));
        Assert.Equal((short)0, context.Frame.Parameters[0]);
        Assert.Equal(PrimitiveResult.False, AnimateSimPrimitive.Execute(context, interruptible));
        Assert.Equal((short)1, context.Frame.Parameters[0]);
        Assert.Equal(PrimitiveResult.True, AnimateSimPrimitive.Execute(context, interruptible));
        Assert.Null(person.AnimationPlayback);
    }

    [Fact]
    public void NonInterruptiblePrimaryAnimationKeepsWaitingEvenWhenQueueRequestsInterrupt()
    {
        var (context, person, _) = PrimitiveTestWorld.Create(parameters: [0]);
        person[PersonData.PersonAge] = 18;
        person.ActionQueueEnd = 1;
        person.ActionQueue[0].Flags = 2;
        context.World.AvatarAnimationTable[(3, false, 58)] = new AvatarAnimationClip("non-interruptible", 8, []);
        byte[] nonInterruptible = [58, 0, 0, 0, 3, 0, 0, 0];

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, nonInterruptible));
        var playback = Assert.IsType<AvatarAnimationPlayback>(person.AnimationPlayback);

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, nonInterruptible));
        Assert.Same(playback, person.AnimationPlayback);
        Assert.False(playback.EndReached);
    }

    [Fact]
    public void ImportedAnimationSoundsQueueAtTheirAuthoredTimesAndHonorLoopStops()
    {
        var start = new AvatarAnimationSoundEvent(50, "local.testpack", "Downloads/local.testpack/audio/ambient_loop.wav", "ambient-loop", true, false);
        var stop = new AvatarAnimationSoundEvent(110, "local.testpack", "", "ambient-loop", false, true);
        var playback = new AvatarAnimationPlayback(new AvatarAnimationClip("ambient-interaction", 5, [], [start, stop]), false, false);

        playback.Tick();
        Assert.Empty(playback.SoundEventQueue);
        playback.Tick();
        Assert.Equal([start], playback.SoundEventQueue);
        playback.SoundEventQueue.Clear();
        playback.Tick();
        Assert.Equal([stop], playback.SoundEventQueue);
    }

    [Fact]
    public void MissingExpectedEventsAreGeneratedOnlyAfterTheAnimationEnds()
    {
        var (context, person, _) = PrimitiveTestWorld.Create(parameters: [0]);
        person[PersonData.PersonAge] = 18;
        context.World.AvatarAnimationTable[(3, false, 58)] = new AvatarAnimationClip("toilet", 1, []);
        byte[] operand = [58, 0, 0, 0, 3, 8, 2, 0];

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, operand));
        var playback = Assert.IsType<AvatarAnimationPlayback>(person.AnimationPlayback);
        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, operand));
        playback.Tick();
        Assert.Equal(PrimitiveResult.False, AnimateSimPrimitive.Execute(context, operand));
        Assert.Equal(0, context.Frame.Parameters[0]);
        Assert.Equal(PrimitiveResult.False, AnimateSimPrimitive.Execute(context, operand));
        Assert.Equal(1, context.Frame.Parameters[0]);
        Assert.Equal(PrimitiveResult.True, AnimateSimPrimitive.Execute(context, operand));
    }

    [Fact]
    public void MissingExpectedEventsAreGeneratedInReverseOrderForBackwardsPlayback()
    {
        var (context, person, _) = PrimitiveTestWorld.Create(parameters: [0]);
        person[PersonData.PersonAge] = 18;
        context.World.AvatarAnimationTable[(3, false, 58)] = new AvatarAnimationClip("toilet", 1, []);
        byte[] operand = [58, 0, 0, 0, 3, 0x0a, 2, 0];

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, operand));
        var playback = Assert.IsType<AvatarAnimationPlayback>(person.AnimationPlayback);
        playback.Tick();

        Assert.Equal(PrimitiveResult.False, AnimateSimPrimitive.Execute(context, operand));
        Assert.Equal(1, context.Frame.Parameters[0]);
        Assert.Equal(PrimitiveResult.False, AnimateSimPrimitive.Execute(context, operand));
        Assert.Equal(0, context.Frame.Parameters[0]);
        Assert.Equal(PrimitiveResult.True, AnimateSimPrimitive.Execute(context, operand));
    }

    [Fact]
    public void ExpectedEventCountUsesTheSignedOperandByte()
    {
        var (context, person, _) = PrimitiveTestWorld.Create(parameters: [0]);
        person[PersonData.PersonAge] = 18;
        context.World.AvatarAnimationTable[(3, false, 58)] = new AvatarAnimationClip("toilet", 1, []);
        byte[] operand = [58, 0, 0, 0, 3, 8, 0xff, 0]; // An expected event count of -1 generates no synthetic substitute events.

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, operand));
        var playback = Assert.IsType<AvatarAnimationPlayback>(person.AnimationPlayback);
        playback.Tick();

        Assert.Equal(PrimitiveResult.True, AnimateSimPrimitive.Execute(context, operand));
        Assert.Null(person.AnimationPlayback);
        Assert.Empty(playback.EventQueue);
        Assert.Equal((short)0, context.Frame.Parameters[0]);
    }

    [Fact]
    public void PlaybackStateRestoresAnimationFramePendingEventsAndRootMotion()
    {
        var sound = new AvatarAnimationSoundEvent(80, "pack", "sound.wav", "cue", true, false);
        var clip = new AvatarAnimationClip("saved-animation", 8,
            [new AvatarAnimationEvent(0, 11), new AvatarAnimationEvent(120, 12)], [sound], 2.5f,
            new System.Numerics.Vector3(1, 2, 3),
            [new System.Numerics.Vector3(4, 5, 6)], 336,
            new AvatarAnimationMarker(0.25f, "L_FOOT"), new AvatarAnimationMarker(0.75f, "R_FOOT"));
        var source = new AvatarAnimationPlayback(clip, backwards: false, loop: false)
        {
            RouteDriven = true,
            Interruptible = true,
        };
        source.FadePracticeWeight(startMils: 100, endMils: 200, startWeight: 1f, targetWeight: 0.5f);
        source.Tick();
        source.EventQueue.Add(77);
        source.SoundEventQueue.Add(sound);
        Assert.True(source.TryConsumeExpectedEvent(0, 2, out _));

        var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        var json = System.Text.Json.JsonSerializer.Serialize(source.SaveState(), options);
        var saved = System.Text.Json.JsonSerializer.Deserialize<AvatarAnimationPlaybackState>(json, options)
            ?? throw new InvalidDataException("Animation playback JSON did not restore a state.");
        var restored = AvatarAnimationPlayback.RestoreState(saved);

        Assert.Equal(source.Clip.Name, restored.Clip.Name);
        Assert.Equal(source.Clip.FrameCount, restored.Clip.FrameCount);
        Assert.Equal(source.Clip.Events, restored.Clip.Events);
        Assert.Equal(source.Clip.SoundEvents, restored.Clip.SoundEvents);
        Assert.Equal(source.Clip.StandardDistance, restored.Clip.StandardDistance);
        Assert.Equal(source.Clip.StandardOffset, restored.Clip.StandardOffset);
        Assert.Equal(source.Clip.StandardRootMotionSamples, restored.Clip.StandardRootMotionSamples);
        Assert.Equal(source.Clip.DurationMilliseconds, restored.Clip.DurationMilliseconds);
        Assert.Equal(source.Clip.LocomotionMarkerOne, restored.Clip.LocomotionMarkerOne);
        Assert.Equal(source.Clip.LocomotionMarkerTwo, restored.Clip.LocomotionMarkerTwo);
        Assert.Equal(source.Frame, restored.Frame);
        Assert.Equal(source.InterpolatePose, restored.InterpolatePose);
        Assert.Equal(source.Backwards, restored.Backwards);
        Assert.Equal(source.Loop, restored.Loop);
        Assert.Equal(source.EndReached, restored.EndReached);
        Assert.Equal(source.RouteDriven, restored.RouteDriven);
        Assert.Equal(source.Interruptible, restored.Interruptible);
        Assert.True(restored.SaveState().PracticeWeightFadeActive);
        Assert.Equal(source.EventsRun, restored.EventsRun);
        Assert.Equal((byte)1, restored.EventsRun);
        Assert.Equal(source.EventQueue, restored.EventQueue);
        Assert.Equal(source.SoundEventQueue, restored.SoundEventQueue);
        var frameBeforeRouteTick = restored.Frame;
        restored.Tick();
        Assert.Equal(frameBeforeRouteTick, restored.Frame);
    }

    [Fact]
    public void AnimatorStateOneStopsThePrimaryPracticeUnlessFrozenForIdle()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        var primary = new AvatarAnimationPlayback(new AvatarAnimationClip("primary", 4, []), false, false);
        person.AnimationPlayback = primary;
        person.AnimatorCurrentSkill = 15;
        person.AnimatorControlState = 1;
        person[PersonData.FreezeForIdleHotDate] = 1;

        context.World.TickAvatarAnimations(person);

        Assert.Same(primary, person.AnimationPlayback);
        Assert.Equal(15, person.AnimatorCurrentSkill);

        person[PersonData.FreezeForIdleHotDate] = 0;
        context.World.TickAvatarAnimations(person);

        Assert.Null(person.AnimationPlayback);
        Assert.Equal(-1, person.AnimatorCurrentSkill);
    }

    [Fact]
    public void AnimatorStateTwoStopsThePrimaryPracticeAndRefreshesTheBackgroundPractice()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person[PersonData.Gender] = 0;
        person[PersonData.Sitting] = 1;
        context.World.StandardWalkFallbackAnimationTable[(152, 1)] = new AvatarAnimationClip("sit-a", 4, []);
        context.World.UpdateBackgroundPractice(person);
        var previousBackground = Assert.IsType<AvatarAnimationPlayback>(person.BackgroundAnimationPlayback);
        person.AnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("primary", 4, []), false, false);
        person.AnimatorCurrentSkill = 15;
        person.AnimatorControlState = 2;
        person[PersonData.FreezeForIdleHotDate] = 1;

        context.World.TickAvatarAnimations(person);

        Assert.Null(person.AnimationPlayback);
        Assert.Equal(-1, person.AnimatorCurrentSkill);
        Assert.NotSame(previousBackground, person.BackgroundAnimationPlayback);
        Assert.Equal("sit-a", person.BackgroundAnimationPlayback?.Clip.Name);
        Assert.Equal((short)1, person.BackgroundPracticePersonState);
    }

    [Theory]
    [InlineData((short)0, 3)]
    [InlineData((short)1, 1)]
    [InlineData((short)2, 2)]
    [InlineData((short)-1, 0)]
    public void PrimaryZeroAnimationSelectsTheStandingControlStateFromSitting(short sitting, int expectedControlState)
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person.AnimatorControlState = 0;
        person.AnimatorCurrentSkill = 15;
        person.AnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("current", 4, []), false, false);
        person.ReachAnimationActive = true;
        person[PersonData.UnusedAndDoNotUse] = 5;
        person[PersonData.Sitting] = sitting;

        Assert.Equal(PrimitiveResult.True, AnimateSimPrimitive.Execute(context, new byte[8]));

        Assert.Equal(expectedControlState, person.AnimatorControlState);
        Assert.Null(person.AnimationPlayback);
        Assert.Equal(-1, person.AnimatorCurrentSkill);
        Assert.False(person.ReachAnimationActive);
        Assert.Equal(0, person[PersonData.UnusedAndDoNotUse]);
    }

    [Fact]
    public void ModeThreePrimaryStartSuppressesAutomaticCarryUntilThePrimaryStops()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        context.World.AvatarAnimationTable[(3, false, 58)] = new AvatarAnimationClip("primary", 4, []);
        person.CarryAnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("carry", 4, []), false, true);
        person.CarryPracticeMode = 1;
        person.CarryPracticeCachedState = 0;

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, [58, 0, 0, 0, 3, 0x11, 0, 0]));

        Assert.True(person.ReachAnimationActive);
        Assert.Null(person.CarryAnimationPlayback);
        Assert.Equal(1, person.CarryPracticeMode);
        Assert.Equal(-1, person.CarryPracticeCachedState);

        Assert.Equal(PrimitiveResult.True, AnimateSimPrimitive.Execute(context, [0, 0, 0, 0, 3, 0x11, 0, 0]));
        Assert.False(person.ReachAnimationActive);
    }

    [Fact]
    public void ResetClearsThePrimaryAnimation()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person.AnimationPlayback = new AvatarAnimationPlayback(new AvatarAnimationClip("current", 2, []), false, false);
        person.AnimatorCurrentSkill = 23;
        Assert.Equal(PrimitiveResult.True, AnimateSimPrimitive.Execute(context, new byte[8]));
        Assert.Null(person.AnimationPlayback);
        Assert.Equal(-1, person.AnimatorCurrentSkill);
    }

    [Fact]
    public void PrimaryAnimationCompletionRefreshesBackgroundAndClearsReachState()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person[PersonData.Gender] = 0;
        person[PersonData.Sitting] = 0;
        context.World.StandardWalkFallbackAnimationTable[(152, 1)] = new AvatarAnimationClip("sit-a", 1, []);
        context.World.UpdateBackgroundPractice(person);
        var previousBackground = person.BackgroundAnimationPlayback;
        person[PersonData.Sitting] = 1;
        person.ReachAnimationActive = true;
        person[PersonData.UnusedAndDoNotUse] = 5;
        var primary = new AvatarAnimationPlayback(new AvatarAnimationClip("primary", 1, [], DurationMilliseconds: 1), false, false);
        primary.Tick();
        person.AnimationPlayback = primary;

        Assert.True(primary.EndReached);
        Assert.Equal(PrimitiveResult.True, AnimateSimPrimitive.Execute(context, [1, 0, 0, 0, 3, 0, 0, 0]));

        Assert.Null(person.AnimationPlayback);
        Assert.NotSame(previousBackground, person.BackgroundAnimationPlayback);
        Assert.Equal("sit-a", person.BackgroundAnimationPlayback?.Clip.Name);
        Assert.Equal((short)1, person.BackgroundPracticePersonState);
        Assert.False(person.ReachAnimationActive);
        Assert.Equal((short)0, person[PersonData.UnusedAndDoNotUse]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(7)]
    public void AnimateSimIsRejectedOutsideAnimatorStatesZeroThroughThree(int controlState)
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        var primary = new AvatarAnimationPlayback(new AvatarAnimationClip("current", 2, []), false, false);
        var carry = new AvatarAnimationPlayback(new AvatarAnimationClip("carry", 2, []), false, true);
        person.AnimationPlayback = primary;
        person.CarryAnimationPlayback = carry;
        person.CarryPracticeMode = 6;
        person.AnimatorControlState = controlState;

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, [0, 0, 0, 0, 3, 0x10, 0, 0]));
        Assert.Same(primary, person.AnimationPlayback);
        Assert.Same(carry, person.CarryAnimationPlayback);
        Assert.Equal(6, person.CarryPracticeMode);
        Assert.Equal(controlState, person.AnimatorControlState);
    }

    [Fact]
    public void StartingPrimaryAnimationClearsAnimatorControlState()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person.AnimatorControlState = 3;
        person.AnimatorCurrentSkill = 15;
        context.World.AvatarAnimationTable[(3, false, 58)] = new AvatarAnimationClip("current", 2, []);

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, [58, 0, 0, 0, 3, 0, 0, 0]));

        Assert.Equal(0, person.AnimatorControlState);
        Assert.Equal(-1, person.AnimatorCurrentSkill);
    }

    [Fact]
    public void ModeThreeClearsCarryChannelWithoutRestartingPrimaryPlayback()
    {
        var (baseContext, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person.TypeRecord = new TypeRecord(77);
        var file = new IffFile(ChunkBuilder.File(("OBJD", 1, new byte[0x80])), "object.iff");
        var scope = new BehaviorScope(file, file, null) { ObjectKey = 77 };
        var context = new TreeContext(baseContext.World, person, new StackFrame(0, [], []) { Table = scope });
        var clip = new AvatarAnimationClip("fridge-close", 2, []);
        context.World.ObjectAvatarAnimationTable[(77, 129, 4)] = clip;
        byte[] operand = [4, 0, 0, 0, 0, 0x11, 0, 0];

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, operand));
        var playback = Assert.IsType<AvatarAnimationPlayback>(person.AnimationPlayback);
        playback.Tick();
        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, operand));
        Assert.Same(playback, person.AnimationPlayback);
        playback.Tick();
        Assert.Equal(PrimitiveResult.True, AnimateSimPrimitive.Execute(context, operand));
        Assert.Null(person.AnimationPlayback);
    }

    [Fact]
    public void ModeThreeClearsCarryEvenWhenRequestedAndFallbackAnimationsAreMissing()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person.AnimatorPracticeScaleEnabled = false;
        person.CarryAnimationPlayback = new AvatarAnimationPlayback(
            new AvatarAnimationClip("carry", 4, []), backwards: false, loop: true);
        person.CarryPracticeMode = 6;
        person.CarryPracticeCachedState = 3;

        var result = AnimateSimPrimitive.Execute(context, [58, 0, 0, 0, 3, 0x51, 0, 0]);

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Null(person.CarryAnimationPlayback);
        Assert.Equal(6, person.CarryPracticeMode);
        Assert.Equal(-1, person.CarryPracticeCachedState);
        Assert.False(person.ReachAnimationActive);
        Assert.True(person.AnimatorPracticeScaleEnabled);
    }

    [Fact]
    public void MissingPrimaryAndFallbackStillWritesThePrimaryPracticeScaleFlag()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;

        var result = AnimateSimPrimitive.Execute(context, [58, 0, 0, 0, 3, 0x40, 0, 0]);

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Null(person.AnimationPlayback);
        Assert.True(person.AnimatorPracticeScaleEnabled);
    }

    [Fact]
    public void BackgroundAnimationLeavesThePrimaryPracticeScaleFlagUnchanged()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person.AnimatorPracticeScaleEnabled = true;
        context.World.AvatarAnimationTable[(3, false, 58)] = new AvatarAnimationClip("background", 4, []);

        var result = AnimateSimPrimitive.Execute(context, [58, 0, 0, 0, 3, 0x01, 0, 0]);

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal("background", person.BackgroundAnimationPlayback?.Clip.Name);
        Assert.True(person.AnimatorPracticeScaleEnabled);
    }

    [Fact]
    public void ModeTwoStartsAndClearsTheSeparateCarryChannel()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person.AnimatorPracticeScaleEnabled = true;
        var primary = new AvatarAnimationPlayback(new AvatarAnimationClip("read", 20, []), false, true);
        person.AnimationPlayback = primary;
        var carry = new AvatarAnimationClip("suit-book101", 30, [new AvatarAnimationEvent(100, 4)]);
        context.World.AvatarAnimationTable[(3, false, 78)] = carry;
        byte[] start = [78, 0, 0, 0, 3, 0x10, 0, 0];

        Assert.Equal(PrimitiveResult.True, AnimateSimPrimitive.Execute(context, start));
        Assert.Same(primary, person.AnimationPlayback);
        Assert.Same(carry, person.CarryAnimationPlayback!.Clip);
        Assert.Equal(6, person.CarryPracticeMode);
        Assert.Equal(-1, person.CarryPracticeCachedState);
        Assert.True(person.AnimatorPracticeScaleEnabled);
        Assert.Equal(0, person.CarryAnimationPlayback.Frame);
        Assert.True(person.CarryAnimationPlayback.Loop);
        person.CarryAnimationPlayback.SetFrame(29.5f);
        person.CarryAnimationPlayback.Tick();
        Assert.Equal(0.7f, person.CarryAnimationPlayback.Frame, precision: 4);
        Assert.False(person.CarryAnimationPlayback.EndReached);

        byte[] clear = [0, 0, 0, 0, 3, 0x10, 0, 0];
        Assert.Equal(PrimitiveResult.True, AnimateSimPrimitive.Execute(context, clear));
        Assert.Null(person.CarryAnimationPlayback);
        Assert.Equal(0, person.CarryPracticeMode);
        Assert.Equal(-1, person.CarryPracticeCachedState);
        Assert.Same(primary, person.AnimationPlayback);
    }

    [Fact]
    public void AnimateSimFlagSixRescalesThePrimaryPracticeFromStateZero()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        var clip = new AvatarAnimationClip("scaled-primary", 8, [], DurationMilliseconds: 336f);
        context.World.AvatarAnimationTable[(3, false, 58)] = clip;
        byte[] operand = [58, 0, 0, 0, 3, 0x40, 0, 0];

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, operand));
        Assert.True(person.AnimatorPracticeScaleEnabled);
        context.World.TickAvatarAnimations(person);

        var playback = Assert.IsType<AvatarAnimationPlayback>(person.AnimationPlayback);
        Assert.Equal(1.2f, person.AnimatorWalkingScale);
        Assert.Equal(1.2f, playback.PracticeScale);
        Assert.Equal(1.2f, playback.Frame);

        var restored = AvatarAnimationPlayback.RestoreState(playback.SaveState());
        Assert.Equal(1.2f, restored.PracticeScale);
        restored.Tick(animatorMils: 84);
        Assert.Equal(2.4f, restored.Frame);
    }

    [Fact]
    public void AnimateSimWithoutFlagSixLeavesPrimaryPracticeAtItsAuthoredRate()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        context.World.AvatarAnimationTable[(3, false, 58)] =
            new AvatarAnimationClip("unscaled-primary", 8, [], DurationMilliseconds: 336f);
        byte[] operand = [58, 0, 0, 0, 3, 0, 0, 0];

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, operand));
        context.World.TickAvatarAnimations(person);

        Assert.False(person.AnimatorPracticeScaleEnabled);
        Assert.Equal(1f, person.AnimationPlayback!.PracticeScale);
        Assert.Equal(1f, person.AnimationPlayback.Frame);
    }

    [Fact]
    public void MissingExplicitCarryAnimationKeepsAnExistingCarryPracticeOwned()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        var existing = new AvatarAnimationPlayback(new AvatarAnimationClip("automatic-carry", 8, []), false, true);
        person.CarryAnimationPlayback = existing;
        byte[] missing = [78, 0, 0, 0, 3, 0x10, 0, 0];

        Assert.Equal(PrimitiveResult.True, AnimateSimPrimitive.Execute(context, missing));

        Assert.Same(existing, person.CarryAnimationPlayback);
        Assert.Equal(6, person.CarryPracticeMode);
        Assert.Equal(-1, person.CarryPracticeCachedState);
    }

    [Theory]
    [InlineData(0, 0, 151)]
    [InlineData(8, 0, 151)]
    [InlineData(18, 0, 152)]
    [InlineData(18, 1, 153)]
    public void RouteWalkUsesMacAgeAndGenderFallbackTable(int age, int gender, ushort tableId)
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = (short)age;
        person[PersonData.Gender] = (short)gender;
        var clip = new AvatarAnimationClip($"global-{tableId}", 4, []);
        context.World.StandardWalkFallbackAnimationTable[(tableId, 20)] = clip;

        Assert.Same(clip, RouteExecutor.ResolveWalkAnimation(context.World, person, 20));
    }

    [Fact]
    public void RouteWalkFallsBackToGlobalStandardTableWhenAgeGenderEntryIsEmpty()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person[PersonData.Gender] = 1;
        var clip = new AvatarAnimationClip("global-standard-fallback", 4, []);
        context.World.StandardWalkFallbackAnimationTable[(150, 20)] = clip;

        Assert.Same(clip, RouteExecutor.ResolveWalkAnimation(context.World, person, 20));
    }

    [Fact]
    public void RouteWalkUsesPersonSelectorOverrideBeforeGlobalFallbackTables()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person.TypeRecord = new TypeRecord(0x1234);
        var selectorClip = new AvatarAnimationClip("selector-walk", 4, []);
        var globalClip = new AvatarAnimationClip("global-walk", 4, []);
        context.World.CustomWalkAvatarAnimationTable[(0x1234, 20)] = selectorClip;
        context.World.StandardWalkFallbackAnimationTable[(152, 20)] = globalClip;

        Assert.Same(selectorClip, RouteExecutor.ResolveWalkAnimation(context.World, person, 20));
    }

    [Theory]
    [InlineData(8, 0, 158)]
    [InlineData(8, 1, 159)]
    [InlineData(18, 0, 160)]
    [InlineData(18, 1, 160)]
    public void RouteWalkUsesMacPoolTableBeforeSelectorAndAgeGenderTables(int age, int gender, ushort tableId)
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = (short)age;
        person[PersonData.Gender] = (short)gender;
        person.TypeRecord = new TypeRecord(0x1234);
        person.Position = new ObjectPosition(2 * 16 + 8, 2 * 16 + 8, 1);
        person.AnimatorPoolTile = true;
        context.World.LotRoomMaps[1] = PoolRoom(2, 2, poolByte: 0);

        var selectorClip = new AvatarAnimationClip("selector-walk", 4, []);
        var poolClip = new AvatarAnimationClip($"pool-{tableId}", 4, []);
        var regularClip = new AvatarAnimationClip("regular-walk", 4, []);
        context.World.CustomWalkAvatarAnimationTable[(0x1234, 20)] = selectorClip;
        context.World.StandardWalkFallbackAnimationTable[(tableId, 20)] = poolClip;
        context.World.StandardWalkFallbackAnimationTable[(age <= 17 ? (ushort)151 : (ushort)(152 + (gender & 1)), 20)] = regularClip;

        Assert.Same(poolClip, RouteExecutor.ResolveWalkAnimation(context.World, person, 20));
    }

    [Fact]
    public void RouteWalkUsesTheForceLocationPoolCacheInsteadOfRecheckingTheCurrentTile()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 8;
        person.Position = new ObjectPosition(2 * 16 + 8, 2 * 16 + 8, 1);
        context.World.LotRoomMaps[1] = PoolRoom(2, 2, poolByte: 0);
        var poolClip = new AvatarAnimationClip("pool-walk", 4, []);
        var childClip = new AvatarAnimationClip("child-walk", 4, []);
        context.World.StandardWalkFallbackAnimationTable[(158, 20)] = poolClip;
        context.World.StandardWalkFallbackAnimationTable[(151, 20)] = childClip;

        Assert.Same(childClip, RouteExecutor.ResolveWalkAnimation(context.World, person, 20));

        person.AnimatorPoolTile = true;
        Assert.Same(poolClip, RouteExecutor.ResolveWalkAnimation(context.World, person, 20));
    }

    [Fact]
    public void RouteWalkUsesSelectorThenAgeGenderWhenPoolTableIsEmpty()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 8;
        person.TypeRecord = new TypeRecord(0x1234);
        person.Position = new ObjectPosition(2 * 16 + 8, 2 * 16 + 8, 1);
        person.AnimatorPoolTile = true;
        context.World.LotRoomMaps[1] = PoolRoom(2, 2, poolByte: 0);
        var selectorClip = new AvatarAnimationClip("selector-walk", 4, []);
        context.World.CustomWalkAvatarAnimationTable[(0x1234, 20)] = selectorClip;
        context.World.StandardWalkFallbackAnimationTable[(151, 20)] = new AvatarAnimationClip("child-walk", 4, []);

        Assert.Same(selectorClip, RouteExecutor.ResolveWalkAnimation(context.World, person, 20));

        context.World.CustomWalkAvatarAnimationTable.Clear();
        Assert.Equal("child-walk", RouteExecutor.ResolveWalkAnimation(context.World, person, 20)?.Name);
    }

    [Fact]
    public void RouteWalkDoesNotUsePoolTableOutsideGroundWaterTileOrAtLotBorder()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 8;
        var poolClip = new AvatarAnimationClip("pool-walk", 4, []);
        var childClip = new AvatarAnimationClip("child-walk", 4, []);
        context.World.StandardWalkFallbackAnimationTable[(158, 20)] = poolClip;
        context.World.StandardWalkFallbackAnimationTable[(151, 20)] = childClip;
        context.World.LotRoomMaps[1] = PoolRoom(2, 2, poolByte: 0);

        person.Position = new ObjectPosition(2 * 16 + 8, 2 * 16 + 8, 2);
        Assert.Same(childClip, RouteExecutor.ResolveWalkAnimation(context.World, person, 20));
        person.Position = new ObjectPosition(8, 2 * 16 + 8, 1);
        Assert.Same(childClip, RouteExecutor.ResolveWalkAnimation(context.World, person, 20));
    }

    private static RoomMap PoolRoom(int poolX, int poolY, byte poolByte)
    {
        const int width = 4;
        const int height = 4;
        var walls = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0);
        var water = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            walls.Raw(new byte[8]);
            water.U8(x == poolX && y == poolY ? poolByte : (byte)0xff);
        }
        return RoomMap.Build(new IffFile(ChunkBuilder.File(("Arry", 2, walls.ToArray()), ("Arry", 9, water.ToArray())),
            "pool-walk-test.iff"), 1, width, height);
    }

    [Fact]
    public void ObjectScopeWithoutInteractionUsesTheRunnersObjectSelector()
    {
        var (baseContext, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person.TypeRecord = new TypeRecord(77);
        var file = new IffFile(ChunkBuilder.File(("OBJD", 1, new byte[0x80])), "object.iff");
        var scope = new BehaviorScope(file, file, null) { ObjectKey = 77 };
        var frame = new StackFrame(0, [], []) { Table = scope };
        var context = new TreeContext(baseContext.World, person, frame);
        var clip = new AvatarAnimationClip("object-use", 4, []);
        context.World.ObjectAvatarAnimationTable[(77, 129, 58)] = clip;
        byte[] operand = [58, 0, 0, 0, 0, 8, 0, 0];

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, operand));
        Assert.Same(clip, person.AnimationPlayback!.Clip);
    }

    [Fact]
    public void ParameterizedObjectScopeUsesTheStackObjectsAnimationTable()
    {
        var (baseContext, person, _) = PrimitiveTestWorld.Create(stackObject: 5, parameters: [58]);
        person[PersonData.PersonAge] = 18;
        var codeFile = new IffFile(ChunkBuilder.File(("OBJD", 1, new byte[0x80])), "code-owner.iff");
        var codeScope = new BehaviorScope(codeFile, codeFile, null) { ObjectKey = 77 };
        var target = baseContext.World.Add(new XObject(5, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(88),
        });
        person.SetObjectTableEntry(5, target);
        var frame = new StackFrame(5, [58], []) { Table = codeScope };
        var context = new TreeContext(baseContext.World, person, frame);
        var codeOwnerClip = new AvatarAnimationClip("code-owner", 4, []);
        var stackObjectClip = new AvatarAnimationClip("stack-object", 4, []);
        context.World.ObjectAvatarAnimationTable[(77, 129, 58)] = codeOwnerClip;
        context.World.ObjectAvatarAnimationTable[(88, 129, 58)] = stackObjectClip;
        byte[] operand = [0, 0, 0, 0, 0, 12, 0, 0];

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, operand));
        Assert.Same(stackObjectClip, person.AnimationPlayback!.Clip);
    }

    [Fact]
    public void ObjectScopeUsesCrossAgeSelectorTableForTheCurrentInteractionTarget()
    {
        var (baseContext, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        var selector = baseContext.World.Add(new XObject(8, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(88),
        });
        var child = baseContext.World.Add(new XPerson(9, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(99),
        });
        child[PersonData.PersonAge] = 10;
        person.InteractingObjects.Add(selector);
        person.CurrentActionRecord = new ActionQueueSlot { Owner = selector, Secondary = child };
        person.GenericSimsCall02ObjectA54 = child;
        var codeFile = new IffFile(ChunkBuilder.File(("OBJD", 1, new byte[0x80])), "code-owner.iff");
        var frame = new StackFrame(0, [], []) { Table = new BehaviorScope(codeFile, codeFile, null) };
        var context = new TreeContext(baseContext.World, person, frame);
        var sameAgeClip = new AvatarAnimationClip("same-age", 4, []);
        var crossAgeClip = new AvatarAnimationClip("cross-age", 4, []);
        context.World.ObjectAvatarAnimationTable[(88, 129, 58)] = sameAgeClip;
        context.World.ObjectAvatarAnimationTable[(88, 131, 58)] = crossAgeClip;

        Assert.Equal(PrimitiveResult.Stop,
            AnimateSimPrimitive.Execute(context, [58, 0, 0, 0, 0, 8, 0, 0]));
        Assert.Same(crossAgeClip, person.AnimationPlayback!.Clip);
    }

    [Theory]
    [InlineData(18, 18, 0x08, 129, false)]
    [InlineData(10, 10, 0x08, 130, false)]
    [InlineData(18, 10, 0x08, 131, false)]
    [InlineData(10, 18, 0x08, 132, false)]
    [InlineData(18, 10, 0x18, 129, true)]
    public void ObjectScopeSelectsWindowsAgeTableAndCrossAgeGate(int actorAge, int targetAge, byte flags,
        ushort selectedTable, bool carryChannel)
    {
        var (baseContext, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = checked((short)actorAge);
        var selector = baseContext.World.Add(new XObject(8, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(88),
        });
        var target = baseContext.World.Add(new XPerson(9, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(99),
        });
        target[PersonData.PersonAge] = checked((short)targetAge);
        person.InteractingObjects.Add(selector);
        person.CurrentActionRecord = new ActionQueueSlot { Owner = selector, Secondary = target };
        person.GenericSimsCall02ObjectA54 = target;
        var file = new IffFile(ChunkBuilder.File(("OBJD", 1, new byte[0x80])), "code-owner.iff");
        var context = new TreeContext(baseContext.World, person,
            new StackFrame(0, [], []) { Table = new BehaviorScope(file, file, null) });
        var selected = new AvatarAnimationClip("selected", 4, []);
        var otherTables = new ushort[] { 129, 130, 131, 132 }.Where(table => table != selectedTable);
        foreach (var table in otherTables)
            context.World.ObjectAvatarAnimationTable[(88, table, 58)] = new AvatarAnimationClip($"table-{table}", 4, []);
        context.World.ObjectAvatarAnimationTable[(88, selectedTable, 58)] = selected;

        var result = AnimateSimPrimitive.Execute(context, [58, 0, 0, 0, 0, flags, 0, 0]);

        Assert.Equal(carryChannel ? PrimitiveResult.True : PrimitiveResult.Stop, result);
        Assert.Same(selected, carryChannel ? person.CarryAnimationPlayback!.Clip : person.AnimationPlayback!.Clip);
    }

    [Fact]
    public void NegativeParameterAnimationIdFallsBackToMissingAnimationAndStandardFallback()
    {
        var (context, person, _) = PrimitiveTestWorld.Create(parameters: [-1]);
        person[PersonData.PersonAge] = 18;
        person[PersonData.Gender] = 0;
        context.World.StandardWalkFallbackAnimationTable[(152, 0x25)] = new AvatarAnimationClip("fallback", 4, []);
        byte[] operand = [0, 0, 0, 0, 3, 0x04, 0, 0];

        var result = AnimateSimPrimitive.Execute(context, operand);

        Assert.Equal(PrimitiveResult.Stop, result);
        Assert.Equal("fallback", person.AnimationPlayback?.Clip.Name);
    }

    [Fact]
    public void ParameterizedObjectScopeWithoutStackObjectThrowsCouldNotFindObjectReference()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 0, parameters: [1]);
        byte[] operand = [0, 0, 0, 0, 0, 0x04, 0, 0];

        var error = Assert.Throws<SimulationException>(() => AnimateSimPrimitive.Execute(context, operand));

        Assert.Equal(SimulationError.CouldNotFindObjectReference, error.Error);
    }

    [Fact]
    public void TimePropertiesMutateInterruptibleHandGesturesAndCensorshipFlags()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        var props = new[]
        {
            new AvatarAnimationSoundEvent(0, "", "interruptable", "1", false, false),
            new AvatarAnimationSoundEvent(0, "", "lefthand", "3", false, false),
            new AvatarAnimationSoundEvent(0, "", "righthand", "4", false, false),
            new AvatarAnimationSoundEvent(0, "", "censor", "7", false, false),
            new AvatarAnimationSoundEvent(84, "", "interruptible", "0", false, false),
        };
        person.AnimationPlayback = new AvatarAnimationPlayback(
            new AvatarAnimationClip("time-props", 4, [], props, DurationMilliseconds: 168f),
            backwards: false, loop: false, startedAtMils: 0);

        context.World.TickAvatarAnimations(person);

        Assert.True(person.AnimationPlayback.Interruptible);
        Assert.Equal((short)3, person[PersonData.HandGestureLeft]);
        Assert.Equal((short)4, person[PersonData.HandGestureRight]);
        Assert.Equal((short)7, person[PersonData.CensorshipFlags]);

        context.World.TickAvatarAnimations(person);

        Assert.False(person.AnimationPlayback.Interruptible);
    }

    [Fact]
    public void ModeThreeSuppressesAutomaticCarryAcrossTicksAndRestoresWhenPrimaryCompletes()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person[PersonData.PersonAge] = 18;
        person[PersonData.Gender] = 0;
        context.World.StandardWalkFallbackAnimationTable[(152, 0x26)] = new AvatarAnimationClip("carry-practice", 8, []);
        context.World.AvatarAnimationTable[(3, false, 58)] = new AvatarAnimationClip("mode3-primary", 2, []);
        person.FootprintSlots.Add(new ObjectFootprintSlot { OccupantObjectId = 42 });

        context.World.TickAvatarAnimations(person);
        Assert.Equal("carry-practice", person.CarryAnimationPlayback?.Clip.Name);
        Assert.Equal(1, person.CarryPracticeMode);
        Assert.Equal(0, person.CarryPracticeCachedState);

        byte[] operand = [58, 0, 0, 0, 3, 0x11, 0, 0];
        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, operand));
        Assert.Null(person.CarryAnimationPlayback);
        Assert.Equal(-1, person.CarryPracticeCachedState);
        Assert.True(person.ReachAnimationActive);

        // Automatic carry animation playback remains suppressed while a primary animation is actively
        // playing.
        context.World.TickAvatarAnimations(person);
        Assert.Null(person.CarryAnimationPlayback);
        Assert.Equal(-1, person.CarryPracticeCachedState);
        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(context, operand));

        // Completion of the primary animation restores automatic carry animation playback on the following
        // tick.
        context.World.TickAvatarAnimations(person);
        Assert.True(person.AnimationPlayback!.EndReached);
        Assert.Equal(PrimitiveResult.True, AnimateSimPrimitive.Execute(context, operand));
        Assert.False(person.ReachAnimationActive);

        context.World.TickAvatarAnimations(person);
        Assert.Equal("carry-practice", person.CarryAnimationPlayback?.Clip.Name);
        Assert.Equal(1, person.CarryPracticeMode);
        Assert.Equal(0, person.CarryPracticeCachedState);
    }

    [Fact]
    public void ANonPersonIsRejectedBecauseTheDispatcherRunsForPersonsOnly()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XObject(100, PrimitiveTestWorld.Definition(0)));
        var context = new TreeContext(world, me, new StackFrame(2, [], [0, 0]));
        Assert.Throws<InvalidOperationException>(() => AnimateSimPrimitive.Execute(context, new byte[8]));
    }
}
