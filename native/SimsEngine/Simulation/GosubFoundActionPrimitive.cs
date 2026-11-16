namespace SimsEngine.Simulation;

/// <summary>Executes an action from the target object's interaction table matching the Sim's tree table
/// entry, pushing the action tree and tracking target usage.</summary>
public static class GosubFoundActionPrimitive
{
    private const int FoundActionPriority = 2;

    public static PrimitiveResult Execute(in TreeContext context)
    {
        var (world, _, frame) = context;
        var me = Person(context);

        var stackObject = frame.StackObject;
        if (stackObject == 0) throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
        var target = me.ObjectTableEntry(stackObject);
        if (target is null) return PrimitiveResult.False;
        // Resolves interaction keys and action trees from the target object's interaction table rather than
        // the Sim's table to avoid misrouting object keys to social interactions.
        var table = target.TypeRecord?.Interactions?.Interactions.Select(FoundAction.FromTtab).ToArray();
        if (table is null) return PrimitiveResult.False;

        var key = me[ObjectData.TreeTableEntry];
        var found = FindKey(table, key) ?? throw new SimulationException(SimulationError.ReferenceToTreeTableEntryThatDoesNotExist);
        var stack = context.Stack ?? throw new InvalidOperationException(
            "Gosub Found Action must run on the person's native execution stack.");
        var scope = target.TypeRecord?.BehaviorScope;
        if (scope is null)
        {
            world.FoundActionPushFailures.Add(new FoundActionPushFailure(me.Id, target.Id, key, found.Action,
                "target has no hydrated behavior scope"));
            return PrimitiveResult.Yield;
        }
        if (!PersonInteractionStack.Push(me, target, stack, scope, found.Action, target.StackObjectKey, [0, 0, 0, 0]))
        {
            world.FoundActionPushFailures.Add(new FoundActionPushFailure(me.Id, target.Id, key, found.Action,
                "target behavior scope has no enterable action tree"));
            return PrimitiveResult.Yield;
        }
        me.PendingActionRequests.Add(new PersonActionRequest(target, stack.EffectiveDepth, false));

        world.StartFoundAction(me, target, key, FoundActionPriority);
        return PrimitiveResult.Again;
    }

    private static FoundAction? FindKey(IReadOnlyList<FoundAction> table, short key)
    {
        foreach (var entry in table)
        {
            if (entry.Key == key) return entry;
        }
        return null;
    }

    private static XPerson Person(in TreeContext context) =>
        context.Me as XPerson ?? throw new InvalidOperationException($"Object {context.Me.Id} is not a person; FUN_0045cd1f runs for persons only.");
}
