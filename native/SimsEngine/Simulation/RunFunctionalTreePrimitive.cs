namespace SimsEngine.Simulation;

/// <summary>The Run Functional Tree primitive (opcode 0x14), after Sims.exe FUN_0044f6c1.
/// <para>Operand: the function selector (a short at byte 0). Selectors 0 to 14 are valid; selectors other than 8 map
/// through the jump table at 0x44f7aa to a function number (<see cref="FunctionNumbers"/>), and selector 8 has its own
/// person gate. Any other value answers false. Byte 2 bit 0 is the flag the push takes.</para>
/// <para>Behavior: the stack object is looked up first, and a missing one raises error 21 (No stack object present when
/// required). The object's type gives the tree id for the function (<see cref="SimulationWorld.FunctionTreeId"/>); none
/// answers false. Otherwise the tree is pushed on the stack for the stack object, and the primitive answers
/// <see cref="PrimitiveResult.Again"/>: the caller is not branched now, and when the pushed tree returns the caller
/// branches on its result through this instruction's true and false targets (the return path in
/// <see cref="TreeStack"/>).</para>
/// <para>Selector 8 requires the running object to be a person whose Gender data is at most 1, then looks up function
/// 0x1a on the stack object's type. A failed person cast or a larger gender value answers false. This follows the
/// signed comparison at Sims.exe 0x44f737-0x44f75e. All selectors use the same tree push; on a person, the vtable
/// +0x34 path records the pushed object in +0xb00 and increments its UseCount (FUN_0045e707). Operand byte 2 bit 0
/// controls the secondary-object flag. Child stack-object access resolves the raw key through the running object's
/// +0xdc table.
/// The tree table the push reads is the stack object's type record's
/// <see cref="TypeRecord.BehaviorScope"/> (inferred); with none, the primitive answers false.</para></summary>
public static class RunFunctionalTreePrimitive
{
    /// <summary>The function number for selectors 0 to 14 (the jump table at 0x44f7aa). Selector 8 is handled separately.</summary>
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
            // FUN_006359f2 at 0x44f737 casts the running object to cXPerson without throwing, then the signed
            // comparison at 0x44f752-0x44f75a rejects Gender > 1. The accepted path selects function 0x1a.
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
