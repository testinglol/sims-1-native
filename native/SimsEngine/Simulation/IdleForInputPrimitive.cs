namespace SimsEngine.Simulation;

/// <summary>Suspends execution while waiting for user input or queued actions, decrementing the idle
/// counter or yielding execution to higher-priority interruptible actions.</summary>
public static class IdleForInputPrimitive
{
    private const int QueueMask = 7;
    private const int BusyFlag = 2;

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

    // An action interrupts if slot priority exceeds Sim priority or has bit 1 set.
    private static bool QueueInterrupts(XPerson me)
    {
        var slot = me.ActionQueue[me.ActionQueueCurrent & QueueMask];
        return me[PersonData.Priority] < slot.Threshold || (slot.Flags & BusyFlag) != 0;
    }

    private static bool QueueHasAction(XPerson me, StackFrame frame)
    {
        if (me.ActionQueueEnd == me.ActionQueueCurrent) return false;
        return frame.WaitState != 1 || (me.ActionQueue[me.ActionQueueCurrent & QueueMask].Flags & BusyFlag) != 0;
    }

    private static XPerson Person(in TreeContext context) =>
        context.Me as XPerson ?? throw new InvalidOperationException($"Object {context.Me.Id} is not a person; FUN_0045cd1f runs for persons only.");

    private static void TraceQueuedStart(SimulationWorld world, XPerson person, XObject? target, ActionQueueSlot action,
        bool targetScopeFound, bool passed, bool pushed)
    {
        var interaction = world.EffectiveInteractions(target)?.Interactions.FirstOrDefault(entry => entry.TtaIndex == action.RawDword10);
        world.QueuedInteractionStartTrace.Add(new QueuedInteractionStartTrace(person.Id, target?.Id,
            unchecked((short)action.ActionTree), targetScopeFound, passed, pushed, interaction?.GuardTree ?? 0,
            person.SelectionWord5CC, person[PersonData.PersonAge], person[PersonData.Gender],
            world.StateClass(person.StateValue), target?[ObjectData.Flags] ?? 0));
    }
}
