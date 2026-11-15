namespace SimsEngine.Simulation;

/// <summary>The Test Sim Interacting With primitive (opcode 0x25), after Sims.exe FUN_0045d9eb (labelled "Test Sim Interacting
/// With" in Behavior.iff STR# 139). Sims.exe reaches it through the person dispatcher FUN_0045cd1f, so the running object is a
/// person.
/// <para>Operand: byte 0, the mode. Mode 1 names another person: byte 1 is the owner of a variable and bytes 2 and 3 its data,
/// read with <see cref="Variables.Get"/> as the object id. Any other mode (0, 2 and up) asks the running person.</para>
/// <para>The answer is true when the stack object's id is the id of an object in the person's interacting list (the vector at
/// +0xb00, <see cref="XPerson.InteractingObjects"/>; the list's name is inferred), and false otherwise. It compares
/// the raw stack-object key with the referenced object's +0xf0 key. The key is read from the frame with no check: a
/// missing stack object is not an error, it just matches no entry.</para>
/// <para>Mode 1 looks the named object up (FUN_00407498) and casts it to a person (FUN_006359f2). An object that is not a
/// person, or no object, raises error 28 (<see cref="SimulationError.ObjectReferredToMustBeAPerson"/>). The running person's
/// list is not searched then.</para></summary>
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

    // Mode 1: the variable's value is the object id; the object must be a person (error 28 otherwise).
    private static List<XObject> OtherPerson(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var id = Variables.Get(context, (DataOwner)operand[1], (short)(operand[2] | operand[3] << 8));
        var person = (context.World.ObjectTableEntry(id) ?? context.World.ObjectByStackObject(context.Me, id)) as XPerson
            ?? throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
        return person.InteractingObjects;
    }

    // Routing guarantees a person (FUN_0045cd1f runs only for the person class).
    private static XPerson Person(in TreeContext context) =>
        context.Me as XPerson ?? throw new InvalidOperationException($"Object {context.Me.Id} is not a person; FUN_0045cd1f runs for persons only.");
}
