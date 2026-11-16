namespace SimsEngine.Simulation;

/// <summary>Executes the functional script corresponding to the given selector on the stack object.
/// Selectors 0 through 14 map to predefined function indices, with selector 8 requiring a person actor with
/// gender at most 1 to invoke function 26.</summary>
public static class RunFunctionalTreePrimitive
{
    /// <summary>Maps functional tree selectors 0 through 14 to their respective function table indices.</summary>
    private static readonly short[] FunctionNumbers =[0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0, 0x1b, 0x0e, 0x1c, 0x10, 0x11, 0x1d];

    private const int LookupSelector = 8;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var stackObject = world.ObjectByStackObject(me, frame.StackObject) ?? throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
        var selector = (short)(operand[0] | operand[1] << 8);
        if (selector is < 0 or > 14) return PrimitiveResult.False;
        short function;
        if (selector == LookupSelector)
        {
            if (context.Me is not XPerson person || person[PersonData.Gender] > 1) return PrimitiveResult.False;
            function = 0x1a;
        }
        else function = FunctionNumbers[selector];
        var treeId = SimulationWorld.FunctionTreeId(stackObject, function);
        if (treeId == 0) return PrimitiveResult.False;
        var stack = context.Stack ?? throw new InvalidOperationException("Run Functional Tree needs the tree stack it runs on.");
        var table = stackObject.TypeRecord?.BehaviorScope;
        if (table is null) return PrimitiveResult.False;
        var pushed = me is XPerson pushedPerson
            ? PersonInteractionStack.Push(pushedPerson, stackObject, stack, table, treeId, stackObject.StackObjectKey,
                flag: (operand[2] & 1) != 0)
            : stack.PushFor(table, stackObject.StackObjectKey, treeId);
        if (!pushed) return PrimitiveResult.False;
        return PrimitiveResult.Again;
    }
}
