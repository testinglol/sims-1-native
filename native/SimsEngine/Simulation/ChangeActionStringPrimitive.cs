using System.Globalization;

namespace SimsEngine.Simulation;

/// <summary>Implements the Change Action String primitive (opcode 0x32). Resolves text from STR# resources
/// using a 1-based index, expands dynamic tokens, and routes the string to the active menu candidate choice
/// or the person's current action record.</summary>
public static class ChangeActionStringPrimitive
{
    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var table = (short)(operand[0] | operand[1] << 8);
        var index = operand[4] - 1;
        var scope = frame.Table as Content.BehaviorScope
            ?? me.TypeRecord?.BehaviorScope as Content.BehaviorScope
            ?? context.StackObject?.TypeRecord?.BehaviorScope as Content.BehaviorScope;
        var text = scope is not null
            ? InteractionMenus.String(scope.Private, unchecked((ushort)table), unchecked((ushort)index), world.Language)
            : world.Host?.StringTableEntry(table, index);

        if (world.InteractionMenu is { } menu)
        {
            var menuText = text is not null ? ExpandPrintfSpecifiers(text, in context) : string.Empty;
            menu.Add(menuText, frame.StackObject, frame);
            return PrimitiveResult.True;
        }

        var expandedText = text is not null
            ? ExpandActionText(text, in context)
            : null;

        if (world.ActionStringRecordWriteEnabled && me is XPerson person)
        {
            var action = person.CurrentActionRecord ??= new ActionQueueSlot();
            action.ActionText = expandedText;
            action.Flags |= 0x80;
        }

        if (world.ActionStringDisplayActive && world.Host is not null)
        {
            world.Host.ChangeActionString(me, frame.StackObject, expandedText);
        }

        return PrimitiveResult.True;
    }

    /// <summary>Expands SimAntics dynamic tokens (such as $Me, $Object, $Neighbor, $Local:n) and
    /// printf-style (%s, %d) format specifiers within action text.</summary>
    private static string ExpandActionText(string text, in TreeContext context)
    {
        var (world, me, frame) = context;

        var formatted = new InteractionMenuContext(world).Expand(text, me, frame);

        return ExpandPrintfSpecifiers(formatted, in context);
    }

    private static string ExpandPrintfSpecifiers(string formatted, in TreeContext context)
    {
        var (world, me, frame) = context;

        if (formatted.Contains("%s", StringComparison.Ordinal))
        {
            var targetObj = context.StackObject
                ?? (me is XPerson p ? world.ObjectByStackObject(p, frame.StackObject) : world.ObjectById(frame.StackObject));
            var targetName = targetObj?.TypeRecord?.CatalogName
                ?? targetObj?.Name
                ?? (targetObj is XPerson tp && world.Neighbors.TryGetValue(tp[PersonData.NeighborId], out var n) ? n.Name : null)
                ?? targetObj?.TypeRecord?.Name
                ?? string.Empty;
            formatted = formatted.Replace("%s", targetName);
        }

        if (formatted.Contains("%d", StringComparison.Ordinal))
        {
            var numVal = frame.Locals.Length > 0 ? frame.Locals[0] : me.Temporaries[0];
            formatted = formatted.Replace("%d", numVal.ToString(CultureInfo.InvariantCulture));
        }

        return formatted;
    }
}
