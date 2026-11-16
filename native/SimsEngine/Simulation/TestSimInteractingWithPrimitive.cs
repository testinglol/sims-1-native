namespace SimsEngine.Simulation;

public static class TestSimInteractingWithPrimitive
{
    private const byte OtherPersonMode = 1;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var stackObject = context.Frame.StackObject;
        var me = Person(context);
        var list = operand[0] == OtherPersonMode ? OtherPerson(context, operand) : me.InteractingObjects;
        var result = list.Any(interacting => interacting.StackObjectKey == stackObject);
        if (context.World.CaptureExecutionTrace)
            context.World.InteractionTestTrace.Add(new SimulationInteractionTestTrace(me.Id, context.Frame.TreeId,
                context.Frame.Instruction, operand[0], stackObject, list.Select(item => item.Id).ToArray(),
                list.Select(item => item.StackObjectKey).ToArray(), result));
        return result ? PrimitiveResult.True : PrimitiveResult.False;
    }

    private static List<XObject> OtherPerson(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var id = Variables.Get(context, (DataOwner)operand[1], (short)(operand[2] | operand[3] << 8));
        var person = (context.World.ObjectTableEntry(id) ?? context.World.ObjectByStackObject(context.Me, id)) as XPerson
            ?? throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
        return person.InteractingObjects;
    }

    private static XPerson Person(in TreeContext context) =>
        context.Me as XPerson ?? throw new InvalidOperationException($"Object {context.Me.Id} is not a person; FUN_0045cd1f runs for persons only.");
}
