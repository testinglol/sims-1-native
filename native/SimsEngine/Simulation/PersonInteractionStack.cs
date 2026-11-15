namespace SimsEngine.Simulation;

/// <summary>cXPerson::FUN_0045e707 is the person's vtable +0x34 push path. After FUN_00452bd8 successfully pushes
/// a target tree, it appends (target, stack depth, flag) to the +0xb00 reference vector and increments the target's
/// +0xc6 UseCount. FUN_0045e769 releases that entry as the interpreter unwinds the corresponding stack depth.</summary>
internal static class PersonInteractionStack
{
    public static bool Push(XPerson person, XObject target, TreeStack stack, ITreeTable table,
        short treeId, short stackObjectKey, bool flag = false)
    {
        if (!stack.PushFor(table, stackObjectKey, treeId)) return false;
        Register(person, target, stack.EffectiveDepth, flag);
        return true;
    }

    public static bool Push(XPerson person, XObject target, TreeStack stack, ITreeTable table,
        short treeId, short stackObjectKey, ReadOnlySpan<short> parameters, bool flag = false)
    {
        if (!stack.PushFor(table, stackObjectKey, treeId, parameters)) return false;
        Register(person, target, stack.EffectiveDepth, flag);
        return true;
    }

    public static void Register(XPerson person, XObject target, int depth, bool flag)
    {
        person.SavedPersonObjectReferences.Add(new PersonObjectReference(target.StackObjectKey, target, depth, flag));
        person.InteractingObjects.Add(target);
        target[ObjectData.UseCount] = unchecked((short)(target[ObjectData.UseCount] + 1));
        if (flag) person.SetCurrentActionSecondaryReference(target);
    }

    /// <summary>FUN_0045e769 tests the post-pop depth against the last +0xb00 entry. The stack is LIFO, so it can
    /// release multiple stale entries when a caller abandons nested frames after an error or cancellation.</summary>
    public static void Release(XPerson person, int currentDepth)
    {
        while (person.SavedPersonObjectReferences.Count > 0)
        {
            var lastIndex = person.SavedPersonObjectReferences.Count - 1;
            var reference = person.SavedPersonObjectReferences[lastIndex];
            if (currentDepth != reference.Value - 1 && currentDepth >= reference.Value) break;

            if (reference.Target is { } target)
            {
                target[ObjectData.UseCount] = unchecked((short)(target[ObjectData.UseCount] - 1));
                for (var index = person.InteractingObjects.Count - 1; index >= 0; index--)
                {
                    if (!ReferenceEquals(person.InteractingObjects[index], target)) continue;
                    person.InteractingObjects.RemoveAt(index);
                    break;
                }
            }
            person.SavedPersonObjectReferences.RemoveAt(lastIndex);

            if (currentDepth == reference.Value - 1)
            {
                for (var index = 0; index < person.SavedPersonObjectReferences.Count; index++)
                {
                    var surviving = person.SavedPersonObjectReferences[index];
                    if (!surviving.Flag || surviving.Target is not { } survivingTarget) continue;
                    person.SetCurrentActionSecondaryReference(survivingTarget);
                }
            }
        }
    }
}
