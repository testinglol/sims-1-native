namespace SimsEngine.Simulation;

/// <summary>The Idle For Input primitive (opcode 0x11), after Sims.exe FUN_0045dd00 (labelled "Idle For Input" in Behavior.iff
/// STR# 139). Sims.exe reaches it through the person dispatcher FUN_0045cd1f, so the running object is a person.
/// <para>Operand: the parameter index at bytes 0 and 1 (a short), the flag at bytes 2 and 3 (a short). Bytes 4 to 7 are not
/// read. An index outside the frame's parameters raises error 8 (<see cref="SimulationError.StackNumberOutOfRange"/>), after
/// the idle predicate has run (it has no effects on the lot).</para>
/// <para>The idle predicate, FUN_0045d92c (<see cref="IsIdle"/>), picks the branch. When the person is not idle, the parameter
/// is a countdown: a nonzero value is decremented and the answer is stop (2); at zero the person's input flag
/// (<see cref="XPerson.InputFlags"/>, bit <see cref="XPerson.IdleForInputFlag"/>) is cleared and the answer is true.</para>
/// <para>When the person is idle, the flag bit is set. A zero flag operand answers true. Otherwise a queued action starts when
/// the action queue is not empty and either the frame is not waiting or the queue's current slot has flag bit 1. The start is
/// <see cref="ISimulationHost.StartQueuedAction"/>: no object (or a refusal) answers stop (2). An accepted action clears the
/// parameter, makes the frame waiting (<see cref="StackFrame.WaitState"/> 1) with the action's object as the stack object, and
/// answers again (3). Without a queued action to start, the answer is true.</para>
/// <para>The predicate (<see cref="IsIdle"/>) answers false when the person is non-interruptible (person data 71). Otherwise it
/// answers true when the queue's current slot interrupts (its threshold is above the person's Priority, or its flag bit 1 is
/// set). Failing that, it answers false unless the person's +0x24 state value is class 1 (`FUN_004074bc` via
/// `FUN_0042ba25`) and the person type (person data 32) is below 2. Then it answers false when the floats at +0x7a8 and +0x7b0 are both at least -99 and either
/// the float at +0x7a0 is at least -99 or the gender (person data 65) is above 1; otherwise true.</para>
/// <para>The native runner rechecks the queued action, pushes its action BHAV onto this person's stack in the target's
/// behavior scope (FUN_00452bd8/FUN_004749b7), and tracks the target-use request until the matching frame pop
/// (FUN_0045e707/FUN_0045e769). When called without a runner stack, the legacy host callback remains available to
/// focused primitive harnesses.</para></summary>
public static class IdleForInputPrimitive
{
    private const int QueueMask = 7;
    private const int BusyFlag = 2;

    // FUN_0045d92c compares the three floats with DAT_00667428.
    private const float Floor = -99f;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, _, frame) = context;
        var me = Person(context);
        var idle = ShouldInterrupt(world, me);

        var index = (short)(operand[0] | operand[1] << 8);
        var flag = (short)(operand[2] | operand[3] << 8);
        if (index < 0 || index >= frame.Parameters.Length) throw new SimulationException(SimulationError.StackNumberOutOfRange);

        if (!idle)
        {
            if (frame.Parameters[index] != 0)
            {
                frame.Parameters[index]--;
                return PrimitiveResult.Stop;
            }
            me.InputFlags &= unchecked((byte)~XPerson.IdleForInputFlag);
            return PrimitiveResult.True;
        }

        me.InputFlags |= XPerson.IdleForInputFlag;
        if (flag == 0 || !QueueHasAction(me, frame)) return PrimitiveResult.True;

        if (!SimulationWorld.TryDequeueInteractionAction(me, out var action)) return PrimitiveResult.Stop;
        var target = action!.Owner;
        var stack = context.Stack ?? (world.SavedExecutionStacks.TryGetValue(me.Id, out var s) ? s : null);
        if (stack is not null)
        {
            if (target is null || (target.Flags & 0x40u) != 0 ||
                !ReferenceEquals(world.ObjectById(target.Id), target) ||
                target.TypeRecord?.BehaviorScope is not { } scope)
            {
                TraceQueuedStart(world, me, target, action, false, false, false);
                return PrimitiveResult.Stop;
            }
            InteractionTestEvaluator.Evaluate(world, me, [action], autonomy: false);
            var passed = (action.Flags & InteractionTestEvaluator.Passed) != 0;
            if (!passed || action.ActionTree == 0)
            {
                TraceQueuedStart(world, me, target, action, true, false, false);
                world.RunFunctionToCompletion(target, 4, me.StackObjectKey);
                return PrimitiveResult.Stop;
            }
            var pushed = PersonInteractionStack.Push(me, target, stack, scope, unchecked((short)action.ActionTree),
                target.StackObjectKey, action.Parameters);
            TraceQueuedStart(world, me, target, action, true, true, pushed);
            if (!pushed)
            {
                world.RunFunctionToCompletion(target, 4, me.StackObjectKey);
                return PrimitiveResult.Stop;
            }
            me.PendingActionRequests.Add(new PersonActionRequest(target, stack.EffectiveDepth, false));
            world.ApplyStartedInteractionAction(me, action);
            frame.Parameters[index] = 0;
            frame.WaitState = 1;
            stack.Frames[^1].StackObject = target.StackObjectKey;
            return PrimitiveResult.Again;
        }

        if (world.Host is not null)
        {
            target = world.Host.StartQueuedAction(me, action);
            if (target is null || (target.Flags & 0x40u) != 0 ||
                !ReferenceEquals(world.ObjectById(target.Id), target))
                return PrimitiveResult.Stop;
            context.World.ApplyStartedInteractionAction(me, action);
            frame.Parameters[index] = 0;
            frame.WaitState = 1;
            frame.StackObject = target.Id;
            return PrimitiveResult.Again;
        }

        return PrimitiveResult.Stop;
    }

    // FUN_0045d92c: shared by Idle For Input and SAnimator::TryAnimate's early completion gate.
    internal static bool ShouldInterrupt(SimulationWorld world, XPerson me)
    {
        if (me[PersonData.NonInterruptible] != 0) return false;
        var queueInterrupts = me.ActionQueueEnd != me.ActionQueueCurrent && QueueInterrupts(me);
        if (queueInterrupts) return true;
        if (world.StateClass(me.StateValue) != 1 || me[PersonData.PersonType] >= 2) return false;
        if (me.IdleFloat7A8 >= Floor && me.IdleFloat7B0 >= Floor)
        {
            if (me.IdleFloat7A0 >= Floor) return false;
            if (me[PersonData.Gender] > 1) return false;
        }
        return true;
    }

    // The queue is not empty and its current slot's threshold is above the person's Priority, or the slot has flag bit 1.
    private static bool QueueInterrupts(XPerson me)
    {
        var slot = me.ActionQueue[me.ActionQueueCurrent & QueueMask];
        return me[PersonData.Priority] < slot.Threshold || (slot.Flags & BusyFlag) != 0;
    }

    // The nested block's test in FUN_0045dd00: the queue is not empty, and the frame is not waiting or the slot's flag bit 1 is set.
    private static bool QueueHasAction(XPerson me, StackFrame frame)
    {
        if (me.ActionQueueEnd == me.ActionQueueCurrent) return false;
        return frame.WaitState != 1 || (me.ActionQueue[me.ActionQueueCurrent & QueueMask].Flags & BusyFlag) != 0;
    }

    // Routing guarantees a person (FUN_0045cd1f runs only for the person class).
    private static XPerson Person(in TreeContext context) =>
        context.Me as XPerson ?? throw new InvalidOperationException($"Object {context.Me.Id} is not a person; FUN_0045cd1f runs for persons only.");

    private static void TraceQueuedStart(SimulationWorld world, XPerson person, XObject? target, ActionQueueSlot action,
        bool targetScopeFound, bool passed, bool pushed)
    {
        var interaction = target?.TypeRecord?.Interactions?.Interactions.FirstOrDefault(entry => entry.TtaIndex == action.RawDword10);
        world.QueuedInteractionStartTrace.Add(new QueuedInteractionStartTrace(person.Id, target?.Id,
            unchecked((short)action.ActionTree), targetScopeFound, passed, pushed, interaction?.GuardTree ?? 0,
            person.SelectionWord5CC, person[PersonData.PersonAge], person[PersonData.Gender],
            world.StateClass(person.StateValue), target?[ObjectData.Flags] ?? 0));
    }
}
