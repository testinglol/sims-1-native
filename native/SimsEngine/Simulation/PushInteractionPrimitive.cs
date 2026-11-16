namespace SimsEngine.Simulation;

/// <summary>Executes the Push Interaction primitive (opcode 0x0d) to queue an interaction on a target Sim
/// with a specified priority mode, running immediate interactions synchronously.</summary>
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
        var interactions = world.EffectiveInteractions(owner);
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
            // Temporarily applies the interaction priority while executing the immediate action tree,
            // restoring the previous priority on completion.
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
