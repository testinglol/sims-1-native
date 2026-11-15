namespace SimsEngine.Simulation;

/// <summary>The Gosub Found Action primitive (opcode 0x1e), after Sims.exe FUN_0045a9c5 (labelled "Gosub Found Action" in
/// Behavior.iff STR# 139). Sims.exe reaches it through the person dispatcher FUN_0045cd1f, so the running object is a person.
/// It takes no operand, so the frame alone decides the answer.
/// <para>Behavior, in Sims.exe's order:</para>
/// <list type="number">
/// <item>A stack object id of 0 raises error 21 (<see cref="SimulationError.NoStackObjectPresentWhenRequired"/>).</item>
/// <item>The stack object id is a 1-based index into the person's object table (<see cref="XObject.ObjectTableEntry"/>,
/// +0xdc). An index out of range, or an empty entry, answers false.</item>
/// <item>The selected target's found-action table (FUN_00457e1c) must exist, or the answer is false. Sims.exe calls it on the
/// target's behavior manager at +0x11c (FUN_0045aa20). Native reads it from the target's resolved TTAB. The entry whose key
/// is the person's Tree Table Entry (object data 20, the short at +0x72) is looked for, first
/// match in table order. Without one, the primitive raises error 22
/// (<see cref="SimulationError.ReferenceToTreeTableEntryThatDoesNotExist"/>).</item>
/// <item>The person's vtable +0x34 method (FUN_0045e707, flag 0) pushes the entry's action tree from the target's
/// behavior scope. A failed push answers yield (-1); a successful push records a pending action and increments target
/// UseCount. An accepted action then becomes the person's current action through FUN_0045e839 with a
/// priority-2 record built by FUN_0041f010 (it is not queued), and the answer is again (3).</item>
/// </list>
/// <para>Not modelled: the dword Sims.exe passes the test (vtable +0x34) carries the action in its low word and, in its high word,
/// the high half of the entry's address (only the low half of ECX is reloaded from the entry; FUN_0045a9c5 at 0x45aa9e). The port
/// passes the action alone, so the high word is not reproduced.</para></summary>
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
        // FUN_0045aa20 loads FUN_00457e1c on the selected target's +0x11c behavior manager, so the matching key
        // and ActionTree both come from the target TTAB. Using the person's table maps ordinary object keys to
        // unrelated person interactions (for example toilet key 0 to a PersonGlobals social tree).
        var table = target.TypeRecord?.Interactions?.Interactions.Select(FoundAction.FromTtab).ToArray();
        if (table is null) return PrimitiveResult.False;

        var key = me[ObjectData.TreeTableEntry];
        var found = FindKey(table, key) ?? throw new SimulationException(SimulationError.ReferenceToTreeTableEntryThatDoesNotExist);
        var stack = context.Stack ?? throw new InvalidOperationException(
            "Gosub Found Action must run on the person's native execution stack.");
        // Sims.exe's PERS vtable +0x34 is FUN_0045e707. Its FUN_00452bd8 call pushes the selected action
        // from the target's behavior scope; a failed push refuses the action. The remaining writes mirror
        // FUN_0045e707: record the post-push depth, then increment target object data +62 (UseCount).
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
        // FUN_0045a9c5 passes flag 0 to FUN_0045e707. The shared person-push path records the target in +0xb00,
        // increments UseCount and releases both when the pushed action returns.
        me.PendingActionRequests.Add(new PersonActionRequest(target, stack.EffectiveDepth, false));

        // FUN_0041f010 builds the record from the target's TTAB entry and FUN_0041f0db copies the action words; the
        // record goes to FUN_0045e839 (the accepted-start transition), not to the queue.
        world.StartFoundAction(me, target, key, FoundActionPriority);
        return PrimitiveResult.Again;
    }

    // The first entry whose key is the person's Tree Table Entry (an int compare, as Sims.exe makes it).
    private static FoundAction? FindKey(IReadOnlyList<FoundAction> table, short key)
    {
        foreach (var entry in table)
        {
            if (entry.Key == key) return entry;
        }
        return null;
    }

    // Routing guarantees a person (FUN_0045cd1f runs only for the person class).
    private static XPerson Person(in TreeContext context) =>
        context.Me as XPerson ?? throw new InvalidOperationException($"Object {context.Me.Id} is not a person; FUN_0045cd1f runs for persons only.");
}
