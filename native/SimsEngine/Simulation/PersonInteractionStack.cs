namespace SimsEngine.Simulation;

/// <summary>Tracks target objects pushed onto a Sim's interaction stack, incrementing the target's use
/// count and releasing it as the stack unwinds.</summary>
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

    /// <summary>Releases target object references whose call stack depth is greater than or equal to the
    /// current unwound depth.</summary>
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
