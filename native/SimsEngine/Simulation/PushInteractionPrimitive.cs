namespace SimsEngine.Simulation;

/// <summary>The Push Interaction primitive (opcode 0x0d), after Sims.exe 0x44cd6b. It pushes one of an object's interactions
/// onto the queue of the stack object, a person, at a priority.
/// <para>Operand: byte 0 is the interaction id (the entry's id in the owner's interaction table), byte 1 the index of the
/// owner in the frame, byte 2 the priority mode, byte 3 the flags and byte 4 the local index of the secondary object. Byte
/// 3 bit 1 makes byte 1 a local index; otherwise it is a parameter index. Bit 0 takes the secondary object, bit 2 sets
/// the record flag 0x2 and bit 3 the record flag 0x40.</para>
/// <para>Order: the owner is the object table entry (1-based, <see cref="XObject.ObjectTableEntry"/>) of the frame's
/// value at byte 1; an index out of range raises "Stack number out of range." (8) for a parameter or "Local out of
/// range." (51) for a local, and a missing object raises "Could not find object reference." (23). The owner's interaction
/// table must exist (the owner's native <see cref="TypeRecord.Interactions"/>; "Tree table not found", 31) and hold the id ("Reference to tree table entry that does not exist.",
/// 22). The stack object must exist ("No stack object present when required.", 21) and be a person ("Object referred to
/// must be a person.", 28). The priority mode then gives the priority: 0 inherits the running object's
/// <see cref="XPerson.ActivePriority"/> when the running object is a person (1 otherwise, with no floor), 1 is 100, 2 is 2,
/// 3 is 50, and any other mode raises "Unrecognized priority value." (45). The secondary object is read from the locals
/// (error 51 when byte 4 is out of range; a missing object is null). The ordinary queued path uses priority insertion;
/// record flag 0x2 prepends the action and retains up to seven queued entries. TTAB Run Immediately entries are
/// guard-checked and executed synchronously through their native behavior scope, following the `FUN_0045d377` branch
/// through `FUN_00474c68`/`FUN_00474b22`; they do not enter the ordinary action ring.</para>
/// <para>Inferred: the record's secondary object is read only when byte 3 bit 0 is set, and the record flags are only the two
/// bits above. Sims.exe reads the parameter index against the count with a strict bound, so an index equal to the count is
/// the next entry in memory there; the port raises the error for it instead.</para></summary>
public static class PushInteractionPrimitive
{
    private const int DefinitionTypeField = 9;
    private const int InheritedPriorityMode = 0;
    private const int SecondaryFlag = 1;
    private const int LocalsFlag = 2;
    private const int RecordFlagTwo = 0x2;
    private const int RecordFlagSixtyFour = 0x40;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var interactionId = operand[0];
        var flags = operand[3];

        var owner = me.ObjectTableEntry(OwnerIndex(frame, operand[1], flags))
            ?? throw new SimulationException(SimulationError.CouldNotFindObjectReference);
        var interactions = owner.TypeRecord?.Interactions;
        if (interactions is null) throw new SimulationException(SimulationError.TreeTableNotFound);
        var interaction = interactions.Interactions.FirstOrDefault(interaction => interaction.TtaIndex == interactionId);
        if (interaction is null)
            throw new SimulationException(SimulationError.ReferenceToTreeTableEntryThatDoesNotExist);

        var stackObject = me.ObjectTableEntry(frame.StackObject)
            ?? throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
        if (stackObject is not XPerson person || stackObject.Definition[DefinitionTypeField] != XPerson.PersonType)
            throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);

        var priority = Priority(me, operand[2]);
        XObject? secondary = null;
        if ((flags & SecondaryFlag) != 0)
        {
            if (operand[4] >= frame.Locals.Length) throw new SimulationException(SimulationError.LocalOutOfRange);
            secondary = me.ObjectTableEntry(frame.Locals[operand[4]]);
        }

        var recordFlags = ((flags & 4) != 0 ? RecordFlagTwo : 0) | ((flags & 8) != 0 ? RecordFlagSixtyFour : 0);
        if ((interaction.Flags & 4) == 0)
            return world.EnqueueInteractionAction(person, owner, interaction, interactionId, priority, secondary,
                (flags & SecondaryFlag) != 0, recordFlags)
                ? PrimitiveResult.True
                : PrimitiveResult.False;

        var immediate = new ActionQueueSlot
        {
            Person = person,
            Owner = owner,
            Secondary = (flags & SecondaryFlag) != 0 ? secondary : owner,
            InteractionId = interactionId,
            ActionTree = interaction.ActionTree,
            Threshold = priority,
            Flags = (interaction.Flags & 0x100) != 0 ? 1 : 0,
        };
        InteractionTestEvaluator.Evaluate(world, person, [immediate], autonomy: false);
        if ((immediate.Flags & InteractionTestEvaluator.Passed) == 0) return PrimitiveResult.False;
        if (world.ImmediateInteractionRunning) return PrimitiveResult.False;

        var scope = owner.TypeRecord?.BehaviorScope
            ?? throw new InvalidOperationException($"Object {owner.Id}: Run Immediately interaction has no behavior scope.");
        var previousPriority = person.ActivePriority;
        var previousImmediate = world.ActiveImmediateAction;
        var previousImmediatePerson = world.ActiveImmediatePerson;
        world.ImmediateInteractionRunning = true;
        world.ActiveImmediateAction = immediate;
        world.ActiveImmediatePerson = person;
        person.ActivePriority = unchecked((short)priority);
        try
        {
            // FUN_0045d377 temporarily installs the interaction priority and calls the tree runner with
            // this interaction's target, tree and parameter block, then restores the previous priority.
            TreeRunner.RunWithParameters(world, person, scope, unchecked((short)interaction.ActionTree),
                owner.StackObjectKey, immediate.Parameters);
            return PrimitiveResult.True;
        }
        finally
        {
            person.ActivePriority = previousPriority;
            world.ImmediateInteractionRunning = false;
            world.ActiveImmediateAction = previousImmediate;
            world.ActiveImmediatePerson = previousImmediatePerson;
        }
    }

    /// <summary>The frame value that names the owner: a local when byte 3 bit 1 is set, otherwise a parameter.</summary>
    private static short OwnerIndex(StackFrame frame, byte index, byte flags)
    {
        if ((flags & LocalsFlag) != 0)
        {
            if (index >= frame.Locals.Length) throw new SimulationException(SimulationError.LocalOutOfRange);
            return frame.Locals[index];
        }
        if (index >= frame.Parameters.Length) throw new SimulationException(SimulationError.StackNumberOutOfRange);
        return frame.Parameters[index];
    }

    /// <summary>The priority the mode gives (Sims.exe 0x44cd6b, the chain after the stack person check).</summary>
    private static int Priority(XObject me, byte mode) => mode switch
    {
        InheritedPriorityMode => me is XPerson executing && executing.Definition[DefinitionTypeField] == XPerson.PersonType
            ? executing.ActivePriority
            : 1,
        1 => 100,
        2 => 2,
        3 => 50,
        _ => throw new SimulationException(SimulationError.UnrecognizedPriorityValue),
    };
}
