using System.Buffers.Binary;
using SimsEngine.Content;

namespace SimsEngine.Simulation;

/// <summary>Implements the Animate Sim primitive (opcode 0x2c), managing the primary play-and-wait channel
/// (mode 0), independent background channel (mode 1), carry channel (mode 2), and primary playback with
/// automatic carry suppression (mode 3).</summary>
public static class AnimateSimPrimitive
{
    private const byte StoreEventInLocal = 0x20;
    private const byte AnimationIdFromParameter = 0x04;
    private const byte PlayBackwardsFlag = 0x02;
    private const byte HurryFlag = 0x08;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var person = Person(context);
        if (person.AnimatorControlState is < 0 or > 3) return PrimitiveResult.Stop;
        if (operand.Length < 8) throw new ArgumentException("Animate Sim requires its eight-byte operand.", nameof(operand));
        var id = BinaryPrimitives.ReadInt16LittleEndian(operand);
        var localEventIndex = operand[2];
        var source = operand[4];
        var flags = operand[5];
        var expectedEvents = unchecked((sbyte)operand[6]);
        var mode = (flags & 1) | ((flags >> 3) & 2);

        var idFromParameter = (flags & AnimationIdFromParameter) != 0;
        if (idFromParameter)
        {
            if (id < 0 || id >= context.Frame.Parameters.Length)
                throw new SimulationException(SimulationError.StackNumberOutOfRange);
            id = context.Frame.Parameters[id];
        }
        var clearCarryWhenPrimaryStarts = mode == 3;
        if (mode == 3)
        {
            mode = 0;
        }
        if (mode == 2 && id == 0)
        {
            person.CarryAnimationPlayback = null;
            person.CarryPracticeMode = 0;
            person.CarryPracticeCachedState = -1;
            return PrimitiveResult.True;
        }
        if (mode == 1 && id == 0)
        {
            person.BackgroundAnimationPlayback = null;
            return PrimitiveResult.True;
        }
        if (id == 0)
        {
            context.World.StopCurrentAnimation(person);
            person.PrimaryAnimationWaiting = false;
            person.PrimaryAnimationEventsRun = 0;
            person.PrimaryAnimationBackwards = false;
            person[PersonData.UnusedAndDoNotUse] = 0;
            person.ReachAnimationActive = false;
            person.AnimatorControlState = person[PersonData.Sitting] switch
            {
                0 => 3,
                1 => 1,
                2 => 2,
                _ => person.AnimatorControlState,
            };
            return PrimitiveResult.True;
        }

        var backwards = (flags & PlayBackwardsFlag) != 0;
        if (mode == 2)
        {
            var carryClip = ResolveClip(context, person, source, id, idFromParameter, flags);
            if (carryClip is null)
            {
                RunMissingAnimationTree(context, person);
                if (person.CarryAnimationPlayback is not null)
                {
                    person.CarryPracticeMode = 6;
                    person.CarryPracticeCachedState = -1;
                }
                return PrimitiveResult.True;
            }
            person.CarryAnimationPlayback = new AvatarAnimationPlayback(carryClip, backwards, loop: true,
                startedAtMils: person.AnimatorMilliseconds);
            person.CarryPracticeMode = 6;
            person.CarryPracticeCachedState = -1;
            return PrimitiveResult.True;
        }

        if (mode == 1)
        {
            var backgroundClip = ResolveClip(context, person, source, id, idFromParameter, flags);
            if (backgroundClip is null)
            {
                RunMissingAnimationTree(context, person);
                return PrimitiveResult.True;
            }
            person.BackgroundAnimationPlayback = new AvatarAnimationPlayback(backgroundClip, backwards, loop: true,
                startedAtMils: person.AnimatorMilliseconds);
            return PrimitiveResult.True;
        }
        var playback = person.AnimationPlayback;
        // PrimaryAnimationWaiting (param_1[0x28]) prevents restarting if Primary is cleared externally mid-wait,
        // while allowing any non-waiting playback (e.g., RouteDriven or ReachOps) to be replaced cleanly.
        if (!person.PrimaryAnimationWaiting)
        {
            var primaryClip = ResolveClip(context, person, source, id, idFromParameter, flags);
            if (primaryClip is null)
            {
                RunMissingAnimationTree(context, person);
                primaryClip = RouteExecutor.ResolveWalkAnimation(context.World, person, 0x25);
                if (primaryClip is null)
                {
                    context.World.StopCurrentAnimation(person);
                    person.PendingAnimationEvents.Clear();
                    person.AnimatorCurrentSkill = -1;
                    person.AnimatorControlState = 0;
                    person.AnimatorPracticeScaleEnabled = (flags & 0x40) != 0;
                    if (clearCarryWhenPrimaryStarts) ClearCarryForPrimaryStart(person);
                    person.PrimaryAnimationWaiting = true;
                    person.PrimaryAnimationEventsRun = 0;
                    person.PrimaryAnimationBackwards = backwards;
                    playback = null;
                }
                else
                {
                    if (clearCarryWhenPrimaryStarts) ClearCarryForPrimaryStart(person);
                    var fallbackPlayback = new AvatarAnimationPlayback(primaryClip, backwards: false, loop: false,
                        startedAtMils: person.AnimatorMilliseconds)
                    {
                        Interruptible = (flags & HurryFlag) != 0,
                    };
                    context.World.BeginPrimaryAnimation(person, fallbackPlayback);
                    person.AnimatorPracticeScaleEnabled = (flags & 0x40) != 0;
                    person.AnimatorControlState = 0;
                    person.PrimaryAnimationWaiting = true;
                    person.PrimaryAnimationEventsRun = 0;
                    person.PrimaryAnimationBackwards = backwards;
                    return PrimitiveResult.Stop;
                }
            }
            else
            {
                if (clearCarryWhenPrimaryStarts) ClearCarryForPrimaryStart(person);
                var primaryPlayback = new AvatarAnimationPlayback(primaryClip, backwards, loop: false,
                    startedAtMils: person.AnimatorMilliseconds)
                {
                    Interruptible = (flags & HurryFlag) != 0,
                };
                context.World.BeginPrimaryAnimation(person, primaryPlayback);
                person.AnimatorPracticeScaleEnabled = (flags & 0x40) != 0;
                person.AnimatorControlState = 0;
                person.PrimaryAnimationWaiting = true;
                person.PrimaryAnimationEventsRun = 0;
                person.PrimaryAnimationBackwards = primaryPlayback.Backwards;
                return PrimitiveResult.Stop;
            }
        }

        if (person.TryDequeueAnimationEvent(out var value))
        {
            if (playback is not null)
            {
                if (!playback.TryConsumeExpectedEvent(value, expectedEvents, out value, person.PrimaryAnimationBackwards))
                    return PrimitiveResult.Stop;
                person.PrimaryAnimationEventsRun = playback.EventsRun;
            }
            else if (!TryConsumeNullPlaybackExpectedEvent(person, value, expectedEvents, out value))
            {
                return PrimitiveResult.Stop;
            }
            WriteEventResult(context, flags, localEventIndex, value);
            return PrimitiveResult.False;
        }
        if (playback is null || playback.EndReached || (playback.Interruptible && IdleForInputPrimitive.ShouldInterrupt(context.World, person)))
        {
            short? missingEvent;
            if (playback is not null)
            {
                missingEvent = playback.TakeMissingExpectedEvent(expectedEvents, person.PrimaryAnimationBackwards);
                person.PrimaryAnimationEventsRun = playback.EventsRun;
            }
            else
            {
                missingEvent = TakeNullPlaybackMissingExpectedEvent(person, expectedEvents);
            }
            if (missingEvent is { } expectedEvent)
            {
                WriteEventResult(context, flags, localEventIndex, expectedEvent);
                return PrimitiveResult.False;
            }
            person.PrimaryAnimationWaiting = false;
            person.PrimaryAnimationEventsRun = 0;
            person.PrimaryAnimationBackwards = false;
            context.World.StopReachAnimation(person);
            context.World.UpdateBackgroundPractice(person);
            person[PersonData.UnusedAndDoNotUse] = 0;
            return PrimitiveResult.True;
        }
        return PrimitiveResult.Stop;
    }

    private static void WriteEventResult(in TreeContext context, byte flags, byte localEventIndex, short value)
    {
        if ((flags & StoreEventInLocal) != 0)
        {
            if (localEventIndex >= context.Frame.Locals.Length)
                throw new InvalidOperationException($"Animate Sim local index {localEventIndex} is outside the frame's {context.Frame.Locals.Length} locals.");
            context.Frame.Locals[localEventIndex] = value;
        }
        else
        {
            if (context.Frame.Parameters.Length == 0)
                throw new InvalidOperationException("Animate Sim writes the next XEVT to parameter 0, but the frame has no parameters.");
            context.Frame.Parameters[0] = value;
        }
    }

    private static bool TryConsumeNullPlaybackExpectedEvent(XPerson person, short value, sbyte expectedCount, out short expectedValue)
    {
        expectedValue = value;
        if (value >= 100) return true;
        var expected = person.PrimaryAnimationBackwards ? expectedCount - person.PrimaryAnimationEventsRun - 1 : person.PrimaryAnimationEventsRun;
        if (expected < 0 || expected >= expectedCount || value != expected) return false;
        expectedValue = (short)expected;
        person.PrimaryAnimationEventsRun++;
        return true;
    }

    private static short? TakeNullPlaybackMissingExpectedEvent(XPerson person, sbyte expectedCount)
    {
        if (person.PrimaryAnimationEventsRun >= expectedCount) return null;
        var expected = person.PrimaryAnimationBackwards ? expectedCount - person.PrimaryAnimationEventsRun - 1 : person.PrimaryAnimationEventsRun;
        person.PrimaryAnimationEventsRun++;
        return (short)expected;
    }

    private static void ClearCarryForPrimaryStart(XPerson person)
    {
        person.CarryAnimationPlayback = null;
        person.CarryPracticeCachedState = -1;
        person.ReachAnimationActive = true;
    }

    private static AvatarAnimationClip? ResolveClip(in TreeContext context, XPerson person, byte source, short id,
        bool idFromParameter, byte flags)
    {
        var child = PersonAgeCategory.IsChild(person[PersonData.PersonAge]);
        if (source == 0)
        {
            var selector = idFromParameter
                ? context.StackObject
                : person.InteractingObjects.LastOrDefault() ?? person;
            if (selector is null)
                throw new SimulationException(SimulationError.CouldNotFindObjectReference);
            if (selector.TypeRecord?.Key is not { } objectGuid) return null;
            if (id < 0) return null;

            // Animation table selection defaults to the actor's age category, switching to the target Sim's
            // category only when the target owns the current action and flags 0 and 4 are clear.
            var targetChild = child;
            var actionOwnerMatches = ReferenceEquals(person.CurrentActionRecord?.Owner, selector);
            if ((flags & 0x11) == 0 && actionOwnerMatches && person.GenericSimsCall02ObjectA54 is XPerson secondary)
                targetChild = PersonAgeCategory.IsChild(secondary[PersonData.PersonAge]);
            var tableId = (child, targetChild) switch
            {
                (false, false) => (ushort)129,
                (true, true) => (ushort)130,
                (false, true) => (ushort)131,
                (true, false) => (ushort)132,
            };
            if (!context.World.ObjectAvatarAnimationTable.TryGetValue((unchecked((int)objectGuid), tableId, id), out var objectClip))
                return null;
            return objectClip;
        }
        if (id < 0) return null;
        return context.World.AvatarAnimationTable.TryGetValue((source, child, id), out var globalClip) ? globalClip : null;
    }

    private static void RunMissingAnimationTree(in TreeContext context, XPerson person)
    {
        if (context.Stack is not { } stack || person.TypeRecord?.BehaviorScope is not Content.BehaviorScope scope) return;
        var treeId = scope.FindNamedTree("missing_animation_tree");
        if (treeId == 0) return;

        var selfRemovalEndsRun = context.World.SelfRemovalEndsRun;
        context.World.SelfRemovalEndsRun = false;
        try { stack.RunToReturn(scope, [], useDefault: true, treeId: treeId, stackObject: person.Id); }
        finally { context.World.SelfRemovalEndsRun = selfRemovalEndsRun; }
    }

    private static XPerson Person(in TreeContext context) =>
        context.Me as XPerson ?? throw new InvalidOperationException($"Object {context.Me.Id} is not a person; FUN_0045cd1f dispatches Animate Sim only for persons.");
}
