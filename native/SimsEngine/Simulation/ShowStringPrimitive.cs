using SimsEngine.Content;
using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>The Show String primitive (opcode 0x15), after Sims.exe FUN_00451af7. The Labels table names it "Show String
/// ( UNUSED )", but the main dispatcher still has its case.
/// <para>Operand: the string table id (a short at byte 0) and a one-based string number (a signed short at byte 2). The text is
/// number <c>index</c> of table <c>table</c>.</para>
/// <para>Behavior: when the text is missing or empty nothing happens. Otherwise the message is the running object's type
/// record name, then " : ", then the text (format "%s : %s" at 0x6c4120), and it goes to the listeners as message 0xeb
/// (<see cref="ISimulationHost.BroadcastString"/>). The primitive always answers true.</para>
/// <para>Inferred: the type record's name is the object's name (the field at +0x18). A running object with no type record
/// raises <see cref="InvalidOperationException"/>, because the original reads the record without a check. A null name
/// prints as empty.</para></summary>
public static class ShowStringPrimitive
{
    /// <summary>The message FUN_00451af7 sends (0xeb).</summary>
    public const int UiMessage = 0xeb;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var table = (short)(operand[0] | operand[1] << 8);
        var index = (short)(operand[2] | operand[3] << 8);
        var lookupObject = world.ObjectByStackObject(me, frame.StackObject);
        var stringIndex = index > 0 ? index - 1 : -1;
        var text = stringIndex < 0 ? null : StringTableEntry(frame, world, lookupObject, me, table, stringIndex)
            ?? world.Host?.StringTableEntry(table, stringIndex);
        if (!string.IsNullOrEmpty(text))
        {
            var record = me.TypeRecord ?? throw new InvalidOperationException($"Object {me.Id} has no type record to name the message.");
            var message = $"{record.Name ?? string.Empty} : {text}";
            world.RecordUiMessage(UiMessage, message);
            world.Host?.BroadcastString(UiMessage, message);
        }
        return PrimitiveResult.True;
    }

    private static string? StringTableEntry(StackFrame frame, SimulationWorld world, XObject? stackObject,
        XObject me, short table, int index)
    {
        if (frame.Table is not BehaviorScope scope) return world.StringTableEntry(stackObject, me, table, index);

        // FUN_00451af7 calls FUN_00470383 with string-scope flag 0: use the running BHAV's local scope.
        // The object routine loader uses the semi-global resource for global-tree ids, otherwise its private resource.
        var file = frame.TreeId >= 8192 && scope.SemiGlobal is not null ? scope.SemiGlobal : scope.Private;
        var chunk = file.Find(ResourceType.Str, unchecked((ushort)table));
        if (chunk is null) return null;
        var strings = StringTable.Read(file, chunk).ForLanguage(world.Language);
        return index >= 0 && index < strings.Count ? strings[index] : null;
    }
}
