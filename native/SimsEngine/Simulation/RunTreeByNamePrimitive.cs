namespace SimsEngine.Simulation;

/// <summary>Executes a behavior script resolved by name from a string table. Modes 0 and 1 run the resolved
/// tree inline to completion, while mode 2 pushes the tree onto the execution stack asynchronously.</summary>
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
        // Resolves the stack object without throwing immediately so an empty name evaluates to false first.
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

        // Mode 0 runs the named tree with the caller as the active object, whereas mode 1 executes it with
        // the stack object as the active object.
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
