using System.Globalization;

namespace SimsEngine.Simulation;

/// <summary>The Change Action String primitive (opcode 0x32), after Sims.exe 0x44cc47 (read from the decompilation, checked
/// against the disassembly).
/// <para>Operand: word 0 is the string table id and byte 4 the one-based entry index. The native path reads STR# from the
/// current behavior scope's private resource file; non-behavior test/host contexts use <see cref="ISimulationHost.StringTableEntry"/>.</para>
/// <para>Behavior: after looking up the text, Sims.exe unconditionally expands dynamic tokens (FUN_0044bf5a).
/// If a menu is in progress (FUN_004588d7), it routes the text directly to the candidate choice.
/// Otherwise, when DAT_006c4d5c (ActionStringRecordWriteEnabled) is non-zero, it writes the text into the person's
/// current action record (+0xa48) and sets flag bit 0x80 via FUN_0041f1b0.
/// If an active display or host is present, it also invokes the host callback.</para>
/// </summary>
public static class ChangeActionStringPrimitive
{
    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var table = (short)(operand[0] | operand[1] << 8);
        // FUN_004705f2 is one-based; the parsed string table and host API are zero-based.
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

    /// <summary>
    /// Expands SimAntics dynamic $Token commands (such as $Me, $Object, $Neighbor, $Local:n)
    /// and printf-style (%s, %d) format specifiers within the action text.
    /// </summary>
    private static string ExpandActionText(string text, in TreeContext context)
    {
        var (world, me, frame) = context;

        // 1. Expand the 18 SimAntics dynamic $Token commands via InteractionMenuContext
        var formatted = new InteractionMenuContext(world).Expand(text, me, frame);

        return ExpandPrintfSpecifiers(formatted, in context);
    }

    private static string ExpandPrintfSpecifiers(string formatted, in TreeContext context)
    {
        var (world, me, frame) = context;

        // 2. Expand printf-style %s with target / stack object name
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

        // 3. Expand printf-style %d with local 0 (or temporary 0)
        if (formatted.Contains("%d", StringComparison.Ordinal))
        {
            var numVal = frame.Locals.Length > 0 ? frame.Locals[0] : me.Temporaries[0];
            formatted = formatted.Replace("%d", numVal.ToString(CultureInfo.InvariantCulture));
        }

        return formatted;
    }
}
