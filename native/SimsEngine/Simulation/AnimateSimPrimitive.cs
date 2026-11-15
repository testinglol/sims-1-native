using System.Buffers.Binary;
using SimsEngine.Content;

namespace SimsEngine.Simulation;

/// <summary>Animate Sim (opcode 0x2c), dispatched by Sims.exe FUN_0045cd1f to avatar slot 0x28, FUN_0058950f.
/// This native slice implements the primary play-and-wait channel (mode 0), the independent background channel
/// (mode 1), the carry channel (mode 2), primary playback with automatic carry suppression (mode 3), and the
/// source's zero-animation resets. Clip names, XEVT times and frame counts come from original animation resources.</summary>
public static class AnimateSimPrimitive
{
    private const byte StoreEventInLocal = 0x20;
    private const byte AnimationIdFromParameter = 0x04;
    private const byte PlayBackwardsFlag = 0x02;
    private const byte HurryFlag = 0x08;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        // SAnimator::TryAnimate accepts only the idle/follow states 0-3. It returns 2 before inspecting
        // AnimateNewParam when +0x188 is outside that range (SAnimator.s 0x10356C64-0x10356C98).
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
            // FUN_0047eda6 derives mode 3 from flags 0 and 4. FUN_0058950f routes it through the primary
            // play-and-wait path and clears the carry channel only when it starts a new primary animation.
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
            // FUN_0058950f's primary zero-animation branch calls StopCurAnim, writes 0 to person data 19
            // (+0x5b2) and clears +0x19c, then selects the standing control state from PersonData[Sitting]
            // (Mac SAnimator.s 0x10356FB0–0x10357028).
            context.World.StopCurrentAnimation(person);
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
            // FUN_0058950f's mode-2 branch starts the independent carry channel and returns without waiting for
            // XEVTs on the primary channel. A later mode-2 call with id 0 clears this channel.
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
            // FUN_0058950f mode 1 drives avatar slot +0x14 (the background practice channel), independently
            // of the primary interaction animation at +0x0c. It returns immediately; the background practice
            // remains active until explicitly cleared or replaced by the animator's state update.
            person.BackgroundAnimationPlayback = new AvatarAnimationPlayback(backgroundClip, backwards, loop: true,
                startedAtMils: person.AnimatorMilliseconds);
            return PrimitiveResult.True;
        }
        var playback = person.AnimationPlayback;
        // A route-owned walk practice is not this call's animation: a portal function (stairs, doors) that animates
        // while the route waits replaces the primary practice instead of waiting on the walk loop forever.
        if (playback is null or { RouteDriven: true })
        {
            var primaryClip = ResolveClip(context, person, source, id, idFromParameter, flags);
            if (primaryClip is null)
            {
                RunMissingAnimationTree(context, person);
                primaryClip = RouteExecutor.ResolveWalkAnimation(context.World, person, 0x25);
                if (primaryClip is null)
                {
                    // FUN_0058950f clears the carry practice for mode 3 after the missing-animation
                    // tree and standard-animation substitution, even when that substitution is null. It
                    // also writes the primary practice-scale flag after the fallback attempt regardless
                    // of whether either skill was available (FUN_0058950f, 0x5897d8–0x589914).
                    person.AnimatorPracticeScaleEnabled = (flags & 0x40) != 0;
                    if (clearCarryWhenPrimaryStarts) ClearCarryForPrimaryStart(person);
                    return PrimitiveResult.True;
                }
                if (clearCarryWhenPrimaryStarts) ClearCarryForPrimaryStart(person);
                person.PendingAnimationEvents.Clear();
                person.AnimatorCurrentSkill = -1;
                person.AnimationPlayback = new AvatarAnimationPlayback(primaryClip, backwards: false, loop: false,
                    startedAtMils: person.AnimatorMilliseconds)
                {
                    Interruptible = (flags & HurryFlag) != 0,
                };
                person.AnimatorPracticeScaleEnabled = (flags & 0x40) != 0;
                person.AnimatorControlState = 0;
                return PrimitiveResult.Stop;
            }
            if (clearCarryWhenPrimaryStarts) ClearCarryForPrimaryStart(person);
            person.PendingAnimationEvents.Clear();
            person.AnimatorCurrentSkill = -1;
            person.AnimationPlayback = new AvatarAnimationPlayback(primaryClip, backwards, loop: false,
                startedAtMils: person.AnimatorMilliseconds)
            {
                Interruptible = (flags & HurryFlag) != 0,
            };
            person.AnimatorPracticeScaleEnabled = (flags & 0x40) != 0;
            // TryAnimate clears SAnimator +0x188 and +0x98 after taking ownership of the primary practice.
            person.AnimatorControlState = 0;
            // FUN_0058950f returns 2 while the animation is still running. That ends this object's current BHAV
            // update but reports a completed callback to FUN_0046d66f; returning -1 here would reject the callback
            // and pin the global object-manager cursor on this Sim.
            return PrimitiveResult.Stop;
        }

        if (person.TryDequeueAnimationEvent(out var value))
        {
            if (!playback.TryConsumeExpectedEvent(value, expectedEvents, out value))
                return PrimitiveResult.Stop;
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
            return PrimitiveResult.False;
        }
        if (playback.EndReached || (playback.Interruptible && IdleForInputPrimitive.ShouldInterrupt(context.World, person)))
        {
            var missingEvent = playback.TakeMissingExpectedEvent(expectedEvents);
            if (missingEvent is { } expectedEvent)
            {
                if ((flags & StoreEventInLocal) != 0)
                {
                    if (localEventIndex >= context.Frame.Locals.Length)
                        throw new InvalidOperationException($"Animate Sim local index {localEventIndex} is outside the frame's {context.Frame.Locals.Length} locals.");
                    context.Frame.Locals[localEventIndex] = expectedEvent;
                }
                else
                {
                    if (context.Frame.Parameters.Length == 0)
                        throw new InvalidOperationException("Animate Sim writes the next XEVT to parameter 0, but the frame has no parameters.");
                    context.Frame.Parameters[0] = expectedEvent;
                }
                return PrimitiveResult.False;
            }
            context.World.StopReachAnimation(person);
            context.World.UpdateBackgroundPractice(person);
            person[PersonData.UnusedAndDoNotUse] = 0;
            return PrimitiveResult.True;
        }
        return PrimitiveResult.Stop;
    }

    /// <summary>Mac SAnimator::TryAnimate's mode-3 primary path stops the explicit carry practice, resets its
    /// cached state +0x1a4 to -1, and sets +0x19c so UpdateCarryPractice leaves the channel alone
    /// (SAnimator.s 0x1035719C–0x103571C4).</summary>
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
            // FUN_005893b7 resolves ordinary Object scope to the runner's last +0xb00 interaction object
            // (FUN_005894e6), or to the runner if that list is empty. ID-from-parameter resolves the
            // frame's one-based stack-object entry (FUN_00407498) and throws CouldNotFindObjectReference (23)
            // when that target object is null (005893b7.c:33-38).
            var selector = idFromParameter
                ? context.StackObject
                : person.InteractingObjects.LastOrDefault() ?? person;
            if (selector is null)
                throw new SimulationException(SimulationError.CouldNotFindObjectReference);
            if (selector.TypeRecord?.Key is not { } objectGuid) return null;
            if (id < 0) return null;

            // FUN_0059dbf8 starts with the actor's age category. It switches to the secondary Sim's
            // category only when the current action owner is this selector and flags 0/4 are both clear
            // (FUN_0047eda6). The resulting selector table ids are 129..132.
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

        // Mac cXObject::RunTree executes this person-scope tree synchronously, using the person as stack object.
        var selfRemovalEndsRun = context.World.SelfRemovalEndsRun;
        context.World.SelfRemovalEndsRun = false;
        try { stack.RunToReturn(scope, [], useDefault: true, treeId: treeId, stackObject: person.Id); }
        finally { context.World.SelfRemovalEndsRun = selfRemovalEndsRun; }
    }

    private static XPerson Person(in TreeContext context) =>
        context.Me as XPerson ?? throw new InvalidOperationException($"Object {context.Me.Id} is not a person; FUN_0045cd1f dispatches Animate Sim only for persons.");
}
