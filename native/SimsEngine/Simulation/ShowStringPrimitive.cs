using SimsEngine.Content;
using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>Formats and broadcasts a string from a string table prefixed with the active object's name to
/// UI listeners via message 0xEB.</summary>
public static class ShowStringPrimitive
{
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

        // Resolves strings from the semi-global resource for subroutine IDs 8192 and above, or from private
        // object resources otherwise.
        var file = frame.TreeId >= 8192 && scope.SemiGlobal is not null ? scope.SemiGlobal : scope.Private;
        var chunk = file.Find(ResourceType.Str, unchecked((ushort)table));
        if (chunk is null) return null;
        var strings = StringTable.Read(file, chunk).ForLanguage(world.Language);
        return index >= 0 && index < strings.Count ? strings[index] : null;
    }
}
