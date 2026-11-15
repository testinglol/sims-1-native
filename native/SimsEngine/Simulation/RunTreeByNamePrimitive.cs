namespace SimsEngine.Simulation;

/// <summary>The Run Tree By Name primitive (opcode 0x1c), after Sims.exe FUN_0044ba66.
/// <para>Operand: the string table id (a short at byte 0), the string-scope flag (byte 2 bit 0), the one-based string number (an unsigned byte at
/// byte 4) and the mode (byte 5). Sims.exe FUN_004705f2 treats string number 1 as the first entry; the port's string table API is zero-based, so the number is decremented for lookup. FUN_0044ba66 reads local strings from the current code owner's private file, or its semi-global file for semi-global code, and reads global strings when the flag is set. The lookup tree itself comes from the stack object's behavior scope.</para>
/// <para>Behavior, in the handler's order:</para>
/// <list type="number">
/// <item>A frame that is waiting (<see cref="StackFrame.Waiting"/>, set by a mode 2 call that is still running) answers
/// the result of the tree it started (<see cref="TreeStack.LastResult"/>), without decoding the operand.</item>
/// <item>An empty or missing name answers false.</item>
/// <item>A mode other than 0, 1 or 2 raises error 44 (Bad parameter to call named tree).</item>
/// <item>The stack object must exist (error 21 otherwise).</item>
/// <item>The name is looked up as a tree id (FUN_00402246: private, semi-global, global; then a second flagged pass). An id of 0 answers false.</item>
/// <item>Mode 0 and 1 run the tree to its return on the stack (FUN_00474b22) with the stack object's type scope and answer
/// the tree's result. Mode 2 pushes the tree (vtable +0x34 with flag 0), marks the frame waiting and answers
/// <see cref="PrimitiveResult.Again"/>; when the tree returns, the waiting frame reads the result in step 1.</item>
/// </list>
/// <para>Modes 0 and 1 differ only in the owner the handler checks, and both run the same nested tree, so the port treats
/// them alike (inferred). The nested run clears <see cref="SimulationWorld.SelfRemovalEndsRun"/> the way FUN_00474b22
/// does and restores it afterwards.</para>
/// <para>The second label matching pass is inferred as case-insensitive; the final lookup flag's matching rule was
/// not recoverable from the decompiled source. The type scope is the stack object's type record's
/// <see cref="TypeRecord.BehaviorScope"/> (inferred); with none, the primitive answers false.</para></summary>
public static class RunTreeByNamePrimitive
{
    private const int ModeStackObject = 1;
    private const int ModePush = 2;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var stack = context.Stack ?? throw new InvalidOperationException("Run Tree By Name needs the tree stack it runs on.");
        if (frame.Waiting) return stack.LastResult ? PrimitiveResult.True : PrimitiveResult.False;

        var table = (short)(operand[0] | operand[1] << 8);
        var stringScopeFlag = operand[2] & 1;
        var stringNumber = operand[4];
        // The stack object is resolved without its error so a missing one still answers false for an empty name, as
        // the source reads the name first.
        var lookupObject = world.ObjectByStackObject(me, frame.StackObject);
        var name = stringNumber == 0 ? null :
            StringTableEntry(frame, world, lookupObject, me, table, stringScopeFlag, stringNumber - 1)
            ?? world.Host?.StringTableEntry(table, stringNumber - 1);
        if (world.CaptureExecutionTrace)
            world.NamedTreeLookupTrace.Add(new SimulationNamedTreeLookupTrace(me.Id, lookupObject?.Id ?? 0,
                frame.TreeId, frame.Instruction, table, stringNumber, name, lookupObject?.TypeRecord?.Key, 0));
        if (string.IsNullOrEmpty(name)) return PrimitiveResult.False;

        var mode = operand[5];
        if (mode > ModePush) throw new SimulationException(SimulationError.BadParameterToCallNamedTree);

        var stackObject = world.ObjectByStackObject(me, frame.StackObject) ?? throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
        var scope = stackObject.TypeRecord?.BehaviorScope;
        if (scope is not Content.BehaviorScope behaviorScope) return PrimitiveResult.False;
        var treeId = behaviorScope.FindNamedTree(name);
        if (world.CaptureExecutionTrace)
            world.NamedTreeLookupTrace[^1] = world.NamedTreeLookupTrace[^1] with
            { ScopeKey = stackObject.TypeRecord?.Key, ResolvedTree = treeId };
        if (treeId == 0) return PrimitiveResult.False;

        if (mode == ModePush)
        {
            var pushed = me is XPerson person
                ? PersonInteractionStack.Push(person, stackObject, stack, scope, treeId, stackObject.StackObjectKey)
                : stack.PushFor(scope, stackObject.StackObjectKey, treeId);
            if (!pushed) return PrimitiveResult.False;
            frame.Waiting = true;
            return PrimitiveResult.Again;
        }

        // FUN_0044ba66 (0044ba66.c:64-86, 125-128): Mode 0 runs FUN_00474b22 on `this` (`me`), whereas Mode 1 sets the
        // target runner (`*(int *)(unaff_EBP + 0xc)`) to `stackObject` and runs FUN_00474b22 with `stackObject` as `this`.
        if (mode == ModeStackObject)
        {
            return TreeRunner.RunWithParameters(world, stackObject, scope, treeId, stackObject.StackObjectKey, stackObject.Temporaries)
                ? PrimitiveResult.True
                : PrimitiveResult.False;
        }

        var saved = world.SelfRemovalEndsRun;
        world.SelfRemovalEndsRun = false;
        try
        {
            return stack.RunToReturn(scope, [], true, treeId, stackObject.StackObjectKey) ? PrimitiveResult.True : PrimitiveResult.False;
        }
        finally
        {
            world.SelfRemovalEndsRun = saved;
        }
    }

    private static string? StringTableEntry(StackFrame frame, SimulationWorld world, XObject? stackObject,
        XObject me, short table, int stringScopeFlag, int index)
    {
        if (frame.Table is not Content.BehaviorScope scope)
            return world.StringTableEntry(stackObject, me, table, index);

        var file = stringScopeFlag != 0
            ? scope.Global
            : frame.TreeId >= 8192 && scope.SemiGlobal is not null ? scope.SemiGlobal : scope.Private;
        var chunk = file.Find(SimsEngine.Formats.ResourceType.Str, unchecked((ushort)table));
        if (chunk is null) return null;
        var strings = SimsEngine.Formats.StringTable.Read(file, chunk).ForLanguage(world.Language);
        return index >= 0 && index < strings.Count ? strings[index] : null;
    }

}
