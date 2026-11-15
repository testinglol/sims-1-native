namespace SimsEngine.Simulation;

/// <summary>The lot table's dialog words, read and written by the Dialog primitive: the table's words at +0xea to +0xfc
/// (inferred name: the lot's dialog state). <see cref="this[int]"/> takes the byte offset in the table. The holders at
/// +0xf8, +0xfc and +0x104 are dword object pointers in Sims.exe; here they are object ids, 0 for none.</summary>
public sealed class DialogWords
{
    private readonly short[] _words = new short[0x90];

    public DialogWords()
    {
        // FUN_00415c5b initializes lot word +0x100 (pet adoption choice index) to -1 so MakeNewCharacter (FUN_0044ae48)
        // draws a random breed unless Dialog case 9 wrote a player-selected breed index.
        _words[0x100 >> 1] = -1;
    }

    /// <summary>The word at byte <paramref name="offset"/> of the lot table (an even offset below 0x120).</summary>
    public short this[int offset]
    {
        get => _words[offset >> 1];
        set => _words[offset >> 1] = value;
    }
}

/// <summary>The Dialog primitive (opcode 0x24), after Sims.exe 0x451592. The name is inferred: the handler works on the
/// lot's dialog words and on a dialog record the owner keeps in the lot table's dialog list (+0x64).
/// <para>Order, as the handler runs it:</para>
/// <list type="number">
/// <item>The owner's dialog record is looked up (FUN_0044439c, <see cref="ISimulationHost.DialogRecordState"/>). A record
/// in state 1 is dismissed through FUN_004370fd before processing the primitive operand.</item>
/// <item>Operand byte 7 bit 0 runs FUN_004443eb, which creates and registers a record of a type the byte 5 selects.</item>
/// <item>Otherwise operand byte 5 (masked with 0x7f) selects the case.</item>
/// </list>
/// <para>Cases ported here:</para>
/// <list type="bullet">
/// <item>5, 7, 8 and 0xb take a lock word of the lot table (+0xea, +0xec, +0xf2, +0xee). A zero word becomes -1 and the
/// primitive answers <see cref="PrimitiveResult.Stop"/>. Otherwise the running object's temporary 0 takes the word, the
/// word becomes 0 and the primitive answers true.</item>
/// <item>0xd takes the lock word +0xf0 the same way when it is not zero. With a zero word, the global byte at
/// DAT_006f2a08 +0xf8 determines whether the word becomes -1; the primitive stops either way.</item>
/// <item>6 and 0xe hold the dialog for the running object, a person (definition type 2; any other object raises error 28,
/// "Object referred to must be a person."). Holder +0xf8 (case 6) or +0xfc (case 0xe) is 0: the holder takes the running
/// object and the saved word (+0xf4 for case 6, +0xf6 for case 0xe) takes the temporary 0, and the primitive answers
/// <see cref="PrimitiveResult.Stop"/>. The running object already holds it: the temporary 0 takes +0xf6, the holder and the
/// saved word are cleared, and the primitive answers true when +0xf4 is -2 (0xfffe) and false otherwise. Another object
/// holds it: the holder and the saved word are cleared, the temporary 0 becomes 0, and the primitive answers false.</item>
/// <item>9 uses the stack object's object-table entry, which must be a person, as a second holder at +0x104. The first
/// caller reserves it and stops. The holder later releases it: temporary 0 receives +0x100, and +0x108 == -2 selects
/// true; otherwise +0x100 becomes -1. A different holder clears the reservation and answers false.</item>
/// <item>0xa, for a person, exchanges temporary 0 with the lot's +0xe8 word. A nonzero word is consumed (positive
/// values are returned in temporary 0 and true; negative values answer false without changing the temporary). An
/// empty word stores the negated neighbor id and stops.</item>
/// </list>
/// <para>Record kinds 0–4 and specialized kinds 5–11, 13, and 14 create UI records. The client dispatches travel,
/// wardrobe/pet selection, and phone-book records to their corresponding native screens. Cases 0xc and 0xf present
/// dialog styles 0 and 1 through the host, or create matching native book-dialog records without a host. Specialized
/// travel state changes outside the dialog manager remain separate from these records. Generic record creation and the
/// record-state branch (0x4519ee) run the same way with or without a host; the host's
/// <see cref="ISimulationHost.CreateDialogRecord"/> family of members replaces the world's own record list.</para>
/// <para>Inferred: the dword holders are kept as object ids, and the -2 mark is the one the handler tests (0xfffe).</para></summary>
public static class DialogPrimitive
{
    private const int DefinitionTypeField = 9;
    private const short ReleaseMark = -2;
    private const short Reserved = -1;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, _) = context;
        var host = world.Host;
        var state = host?.DialogRecordState(me) ?? world.DialogRecordState(me);
        if (state == 1)
        {
            if (host is not null) host.DismissDialogRecord(me);
            else world.DismissDialogRecord(me);
            // FUN_00451592 aborts a state-1 record, clears its local pointer, and
            // continues processing this same Dialog instruction. The native
            // record must be removed as well so a replacement record can be found
            // by owner on the next tick.
            if (host is null && world.DialogRecord(me) is { } dismissedAfterAbort) world.CompleteDialogRecord(dismissedAfterAbort);
            state = null;
        }
        else if (state == 5)
        {
            // State 5 is already dismissed (FUN_004370fd). The source clears its
            // current-record pointer and falls through to the instruction's
            // normal handling instead of treating dismissal as a response.
            if (host is null && world.DialogRecord(me) is { } terminalRecord) world.CompleteDialogRecord(terminalRecord);
            state = null;
        }
        if ((operand[7] & 1) != 0)
        {
            CreateNativeMessageDialog(context, operand, blocking: (operand[7] & 0x80) == 0, awaitsResponse: false);
            return PrimitiveResult.True;
        }

        var words = world.Dialog;
        switch (operand[5] & 0x7f)
        {
            case 5: return TakeOrReserve(context, words, 0xea, 5);
            case 7: return TakeOrReserve(context, words, 0xec, 7);
            case 8: return TakeOrReserve(context, words, 0xf2, 8);
            case 0xb: return TakeOrReserve(context, words, 0xee, 0xb);
            case 0xd:
                if (words[0xf0] == 0)
                {
                    if ((host?.DialogGlobalFlagF8() ?? 0) == 0)
                    {
                        words[0xf0] = Reserved;
                        QueueLotScreenDialog(context, 0xd);
                    }
                    return PrimitiveResult.Stop;
                }
                return TakeOrReserve(context, words, 0xf0, 0xd);
            case 6: return Holder(context, words, 0xf8, 0xf4, 6);
            case 0xe: return Holder(context, words, 0xfc, 0xf6, 0xe);
            case 9: return StackObjectHolder(context, words);
            case 0xa: return NeighborIdExchange(context, words);
            case 0xc: return ShowDialog(host, world, me, 0);
            case 0xf: return ShowDialog(host, world, me, 1);
            default: return NativeMessageDialog(context, operand, state);
        }
    }

    private static InvalidOperationException MissingHost(SimulationWorld world, string operation) => new(
        world.CurrentExecutionLocation is { } location
            ? $"This dialog operation needs a simulation host for {operation} ({location})."
            : $"This dialog operation needs a simulation host for {operation}.");

    /// <summary>Native implementation of Dialog records created by
/// FUN_004443eb/FUN_004360c1. Text IDs are one-based in the Dialog operand and resolved through STR# 301 in code-owner scope.</summary>
    private static PrimitiveResult NativeMessageDialog(in TreeContext context, ReadOnlySpan<byte> operand, int? state)
    {
        var (world, me, frame) = context;
        var host = world.Host;

        if (state is null)
        {
            if (host?.HasVisibleDialog(world) ?? world.HasVisibleBlockingDialog) return PrimitiveResult.Stop;
            CreateNativeMessageDialog(context, operand, blocking: (operand[7] & 0x80) == 0, awaitsResponse: true);
            return PrimitiveResult.Stop;
        }

        var record = host?.DialogRecordFor(world, me) ?? world.DialogRecord(me);
        if (record is null) return state is 0 or 1 ? PrimitiveResult.Stop : PrimitiveResult.True;
        switch (state.Value)
        {
            case 0:
            case 1: return PrimitiveResult.Stop;
            case 2:
                if (record.Kind == 3 && !string.IsNullOrEmpty(record.ResponseText))
                {
                    var target = frame.StackObject == 0 ? me : world.ObjectByStackObject(me, frame.StackObject) ?? me;
                    target.Name = record.ResponseText.Length <= 64 ? record.ResponseText : record.ResponseText[..64];
                    if (target is XPerson namedPerson && world.Neighbors.TryGetValue(namedPerson[PersonData.NeighborId], out var neighbor))
                        neighbor.Name = target.Name;
                }
                CompleteRecord(world, record);
                return PrimitiveResult.True;
            case 3:
            case 4:
                if (record.Kind == 2)
                {
                    var tempIndex = (operand[7] >> 4) & 7;
                    me.Temporaries[tempIndex] = state.Value == 3 ? (short)0 : (short)1;
                }
                // State 3 (declined) also resets the lot table's +0x100 release word to -1 before answering false.
                if (state.Value == 3) world.Dialog[0x100] = Reserved;
                CompleteRecord(world, record);
                return PrimitiveResult.False;
            default:
                CompleteRecord(world, record);
                return PrimitiveResult.True;
        }
    }

    private static void CompleteRecord(SimulationWorld world, SimulationDialogRecord record)
    {
        if (world.Host is { } host) host.CompleteDialogRecord(world, record);
        else world.CompleteDialogRecord(record);
    }

    private static SimulationDialogRecord? CreateNativeMessageDialog(in TreeContext context, ReadOnlySpan<byte> operand,
        bool blocking, bool awaitsResponse)
    {
        var (world, me, frame) = context;
        var kind = (byte)(operand[5] & 0x7f);
        // FUN_004443eb gives every kind except 5, 7, 8, 0xb and 0xd the generic message record, including 12, 15 and
        // the unused values above 15. Kinds 12 and 15 mean Spellbook/Cookbook screens only in the non-record
        // primitive cases handled by ShowDialog above.
        var stackIcon = world.ObjectByStackObject(me, frame.StackObject);
        if (kind is 5 or 7 or 8 or 0xb or 0xd)
        {
            // Those five kinds construct their own screen record (FUN_00549f7f, FUN_00568898, FUN_004425e8 or
            // FUN_005569af, FUN_00564664, FUN_00551513) with no constructor arguments: the operand's strings are never
            // read, so a missing STR# 301 entry cannot fail the instruction, and the record carries only its kind. The
            // screen reports through the per-kind word of the lot table (TakeOrReserve), not through a message answer.
            return world.Host is { } screenHost
                ? screenHost.CreateDialogRecord(world, me, stackIcon, "", "", null, null, null, kind, blocking, awaitsResponse)
                : world.CreateMessageDialog(me, stackIcon, "", "", null, null, null, kind, blocking, awaitsResponse);
        }
        // FUN_004360c1 (004360c1.c:87-149) seeds default button labels from UIText.iff STR# 142 (FUN_00435ff3:
        // 1="OK", 2="Cancel", 3="Yes", 4="No"), overrides with non-empty STR# 301 entries (FUN_00436a83), and
        // expands Yes, No, Cancel, Title, Message in order through FUN_0044bf5a, updating local_7c (auto-icon).
        var autoToken = AutoIconToken.None;
        var yes = NonEmptyOrNull(ReadDialogString(frame, world, stackIcon, me, operand[3], ref autoToken))
            ?? (kind is 1 or 2
                ? DefaultDialogButton(world, me, frame, 2, "Yes")
                : (blocking || !ReferenceEquals(me, world.TutorialOwner))
                    ? DefaultDialogButton(world, me, frame, 0, "OK")
                    : null);
        var no = kind is 1 or 2 or 10 or 14
            ? NonEmptyOrNull(ReadDialogString(frame, world, stackIcon, me, operand[4], ref autoToken))
                ?? (kind is 1 or 2 ? DefaultDialogButton(world, me, frame, 3, "No") : null)
            : null;
        var cancel = kind == 2
            ? NonEmptyOrNull(ReadDialogString(frame, world, stackIcon, me, operand[0], ref autoToken))
                ?? DefaultDialogButton(world, me, frame, 1, "Cancel")
            : null;
        var title = ReadDialogString(frame, world, stackIcon, me, operand[6], ref autoToken);
        if (string.IsNullOrEmpty(title))
            title = new InteractionMenuContext(world).Expand("$Me", me, frame);
        var message = ReadDialogString(frame, world, stackIcon, me, operand[2], ref autoToken) ?? "";

        var iconMode = (operand[7] >> 1) & 7;
        var icon = ResolveDialogIcon(world, me, frame, stackIcon, iconMode, autoToken, out var neighborSpec);
        var created = world.Host is { } host
            ? host.CreateDialogRecord(world, me, icon, title, message, yes, no, cancel, kind, blocking, awaitsResponse)
            : world.CreateMessageDialog(me, icon, title, message, yes, no, cancel, kind, blocking, awaitsResponse);
        if (created is not null)
        {
            ApplyDialogImageMetadata(created, world, me, frame, stackIcon, iconMode, operand[1], neighborSpec);
        }
        return created;
    }

    private enum AutoIconToken
    {
        None = 0,
        Object = 1,
        Neighbor = 2,
    }

    private static XObject? ResolveMasterObject(SimulationWorld world, XObject? obj) =>
        obj is { MultiTileMasterId: not 0 } && obj.MultiTileMasterId != obj.Id && world.ObjectById(obj.MultiTileMasterId) is { } master
            ? master
            : obj;

    private static XPerson? FindPersonForNeighbor(SimulationWorld world, short neighborId, NeighborState entry)
    {
        foreach (var candidate in world.Objects.Values.OfType<XPerson>())
        {
            if (candidate[PersonData.NeighborId] == neighborId ||
                (entry.TypeRecordKey != 0 && candidate.TypeRecord?.Key == entry.TypeRecordKey))
                return candidate;
        }
        return null;
    }

    private static NeighborState? ResolveNeighborByIdOrSlot(SimulationWorld world, short idOrSlot) =>
        world.NeighborManagerEntry(idOrSlot) ?? (world.Neighbors.TryGetValue(idOrSlot, out var byId) ? byId : null);

    /// <summary>FUN_004360c1 (004360c1.c:155-192): resolves the dialog icon selector for modes 0 (Auto), 1 (None),
    /// 2 (Neighbor / FUN_00425b48), 3 (Indexed), and 4 (Dynamic STR# 301).</summary>
    private static XObject? ResolveDialogIcon(SimulationWorld world, XObject me, StackFrame frame, XObject? stackIcon,
        int iconMode, AutoIconToken autoToken, out string? neighborSpec)
    {
        neighborSpec = null;
        switch (iconMode)
        {
            case 0: // Auto (004360c1.c:156-166)
                if (autoToken == AutoIconToken.Neighbor && ResolveNeighborByIdOrSlot(world, frame.StackObject) is { } autoNeighbor)
                {
                    neighborSpec = $"dialog-neighbor:{autoNeighbor.TypeRecordKey:X8}:{autoNeighbor.Name}";
                    return FindPersonForNeighbor(world, frame.StackObject, autoNeighbor);
                }
                return ResolveMasterObject(world, stackIcon);
            case 1: // None (004360c1.c:167-168)
            case 3: // Indexed (004360c1.c:175-182: local_98 remains 0)
            case 4: // Dynamic STR# 301 (004360c1.c:315-503: local_98 remains 0)
                return null;
            case 2: // Neighbor (004360c1.c:169-174: FUN_00425b48(frame.StackObject))
                if (ResolveNeighborByIdOrSlot(world, frame.StackObject) is { } neighbor)
                {
                    neighborSpec = $"dialog-neighbor:{neighbor.TypeRecordKey:X8}:{neighbor.Name}";
                    return FindPersonForNeighbor(world, frame.StackObject, neighbor) ?? ResolveMasterObject(world, stackIcon);
                }
                return ResolveMasterObject(world, stackIcon ?? me);
            default:
                return ResolveMasterObject(world, stackIcon);
        }
    }

    private static readonly byte[] EmptyIffHeader = MakeEmptyIffHeader();

    private static byte[] MakeEmptyIffHeader()
    {
        var bytes = new byte[Formats.IffFile.HeaderSize];
        "IFF FILE 2.5:TYPE FOLLOWED BY SIZE\0"u8.CopyTo(bytes);
        return bytes;
    }

    private static Formats.IffFile CreateSpecIff(string specSource) => new(EmptyIffHeader, specSource);

    /// <summary>FUN_004360c1 (004360c1.c:175-518): populates ImageFile and ImageBitmapId for mode 3 (Indexed 5000 + operand[1]),
    /// mode 4 (Dynamic STR# 301 command spec: my, gz, gzi, guid, rel, job), and off-lot neighbor portraits.</summary>
    private static void ApplyDialogImageMetadata(SimulationDialogRecord created, SimulationWorld world, XObject me,
        StackFrame frame, XObject? stackIcon, int iconMode, byte iconOperand, string? neighborSpec)
    {
        var imageScope = frame.Table as Content.BehaviorScope ?? me.TypeRecord?.BehaviorScope as Content.BehaviorScope;
        if (iconMode == 3)
        {
            if (imageScope is not null)
            {
                created.ImageFile = imageScope.Private;
                created.ImageBitmapId = (ushort)(5000 + iconOperand);
            }
            return;
        }
        if (iconMode == 4)
        {
            var dummyToken = AutoIconToken.None;
            var spec = ReadDialogString(frame, world, stackIcon, me, iconOperand, ref dummyToken)?.Trim();
            if (string.IsNullOrEmpty(spec)) return;
            var spaceIndex = spec.IndexOf(' ');
            var command = (spaceIndex < 0 ? spec : spec[..spaceIndex]).Trim();
            var arg = spaceIndex < 0 ? "" : spec[(spaceIndex + 1)..].Replace(" ", "", StringComparison.Ordinal).Trim();
            if (command.Equals("my", StringComparison.OrdinalIgnoreCase) && TryParseUshortSpec(arg, out var myBitmapId))
            {
                var meScope = me.TypeRecord?.BehaviorScope as Content.BehaviorScope ?? imageScope;
                if (meScope is not null)
                {
                    created.ImageFile = meScope.Private;
                    created.ImageBitmapId = myBitmapId;
                }
                return;
            }
            if (arg.Length == 0 && TryParseUshortSpec(command, out var directBitmapId))
            {
                if (imageScope is not null)
                {
                    created.ImageFile = imageScope.Private;
                    created.ImageBitmapId = directBitmapId;
                }
                return;
            }
            if (command.Equals("rel", StringComparison.OrdinalIgnoreCase))
            {
                uint relGuid = 0;
                if (ResolveNeighborByIdOrSlot(world, frame.StackObject) is { } relNeighbor)
                    relGuid = unchecked((uint)relNeighbor.TypeRecordKey);
                else if (stackIcon?.TypeRecord is { } stackType)
                    relGuid = unchecked((uint)stackType.Key);
                created.ImageFile = CreateSpecIff($"dialog-spec:rel 0x{relGuid:X8}");
                created.ImageBitmapId = 2003;
                return;
            }
            var normalizedSpec = arg.Length > 0 ? $"{command.ToLowerInvariant()} {arg}" : command.ToLowerInvariant();
            created.ImageFile = CreateSpecIff($"dialog-spec:{normalizedSpec}");
            created.ImageBitmapId = TryParseUshortSpec(arg, out var parsedUshort) ? parsedUshort
                : TryParseUintSpec(arg, out var parsedUint) ? unchecked((ushort)(parsedUint != 0 ? parsedUint : 1))
                : (ushort)1;
            return;
        }
        if (neighborSpec is not null)
        {
            var hasPersonPortrait = created.Icon is XPerson person &&
                (person.TypeRecord?.BehaviorScope as Content.BehaviorScope)?.Private.Find(Formats.ResourceType.Parse("BMP_"), 2002) is not null;
            if (!hasPersonPortrait)
            {
                created.ImageFile = CreateSpecIff(neighborSpec);
                created.ImageBitmapId = unchecked((ushort)frame.StackObject);
            }
        }
    }

    private static bool TryParseUshortSpec(string text, out ushort value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ushort.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
        return ushort.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseUintSpec(string text, out uint value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
        if (uint.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
            return true;
        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var signed))
        {
            value = unchecked((uint)signed);
            return true;
        }
        value = 0;
        return false;
    }

    private static string? NonEmptyOrNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string DefaultDialogButton(SimulationWorld world, XObject me, StackFrame frame, int zeroBasedIndex, string fallback)
    {
        var raw = world.UiTextString(142, zeroBasedIndex) ?? fallback;
        return new InteractionMenuContext(world).Expand(raw, me, frame);
    }

    private static string? ReadDialogString(StackFrame frame, SimulationWorld world, XObject? icon, XObject me,
        byte oneBasedIndex, ref AutoIconToken autoToken)
    {
        if (oneBasedIndex == 0) return null;
        // FUN_004360c1 obtains STR# 301 through the frame's code-owner resource
        // (Mac reference: the current stack frame's resource). A shared BHAV can run on a stack
        // whose object resource differs from the Sim that owns the current thread.
        string? value = null;
        if (frame.Table is Content.BehaviorScope scope)
        {
            var file = scope.Private;
            if (file.Find(Formats.ResourceType.Str, 301) is { } chunk)
            {
                var strings = Formats.StringTable.Read(file, chunk).ForLanguage(world.Language);
                var index = oneBasedIndex - 1;
                if (index < strings.Count) value = strings[index];
            }
        }
        else value = world.StringTableEntry(icon, me, 301, oneBasedIndex - 1);
        if (value is null) return null;
        TrackAutoIconTokens(value, world, me, frame, ref autoToken);
        // FUN_004360c1 runs every string through FUN_0044bf5a. $Me is the object's assigned name, then CTSS entry 0,
        // then the OBJD label; the other commands are the dynamic string table of that
        // routine. PersonCharacter.Names stores three appearance-related strings, not a Sim name.
        return new InteractionMenuContext(world).Expand(value, me, frame);
    }

    private static void TrackAutoIconTokens(string text, SimulationWorld world, XObject me, StackFrame frame,
        ref AutoIconToken autoToken)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '$') continue;
            var start = i + 1;
            var end = start;
            while (end < text.Length && char.IsLetter(text[end])) end++;
            var token = text[start..end];
            if (token.Equals("Object", StringComparison.OrdinalIgnoreCase))
            {
                if (world.ObjectByStackObject(me, frame.StackObject) is not null)
                    autoToken = AutoIconToken.Object;
            }
            else if (token.Equals("Neighbor", StringComparison.OrdinalIgnoreCase))
            {
                if (ResolveNeighborByIdOrSlot(world, frame.StackObject) is not null)
                    autoToken = AutoIconToken.Neighbor;
            }
            i = end - 1;
        }
    }

    /// <summary>Cases 0xc/0xf (0x451922/0x451986): Sims.exe resolves the running object as a person, constructs dialog
    /// style 0/1, presents it with FUN_004f6c41 and hands it to FUN_005da4c7. Both branches answer true.</summary>
    private static PrimitiveResult ShowDialog(ISimulationHost? host, SimulationWorld world, XObject me, byte style)
    {
        if (me is not XPerson person)
            throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
        if (host is not null) host.ShowDialog(person, style);
        else world.CreateBookDialog(person, style);
        return PrimitiveResult.True;
    }

    private static void QueueLotScreenDialog(in TreeContext context, byte kind, XObject? customIcon = null)
    {
        var (world, me, frame) = context;
        if (world.Host is not null) return;
        if (world.DialogRecords.Any(record => record.Kind == kind && record.State < 2)) return;
        var icon = customIcon ?? world.ObjectByStackObject(me, frame.StackObject);
        world.CreateMessageDialog(me, icon, "", "", null, null, null, kind, blocking: true, awaitsResponse: true);
    }

    private static bool IsLotScreenDialogPending(SimulationWorld world, byte kind) =>
        world.Host is null && world.DialogRecords.Any(record => record.Kind == kind && record.State < 2);

    /// <summary>Cases 5, 7, 8, 0xb and 0xd: a free lock is reserved (-1, Stop); a held lock gives its value to the
    /// temporary 0 and is freed (true).</summary>
    private static PrimitiveResult TakeOrReserve(in TreeContext context, DialogWords words, int offset, byte kind)
    {
        var (world, me, _) = context;
        if (words[offset] == 0)
        {
            words[offset] = Reserved;
            QueueLotScreenDialog(context, kind);
            return PrimitiveResult.Stop;
        }
        if (IsLotScreenDialogPending(world, kind)) return PrimitiveResult.Stop;
        me.Temporaries[0] = words[offset];
        words[offset] = 0;
        return PrimitiveResult.True;
    }

    /// <summary>Cases 6 and 0xe: <paramref name="holderOffset"/> is the holder and <paramref name="savedOffset"/> the word the
    /// set and release use (+0xf4 for case 6, +0xf6 for case 0xe). The release always reads the temporary from +0xf6 and
    /// tests +0xf4, as the handler does.</summary>
    private static PrimitiveResult Holder(in TreeContext context, DialogWords words, int holderOffset, int savedOffset, byte kind)
    {
        var (world, me, _) = context;
        if (me.Definition[DefinitionTypeField] != XPerson.PersonType) throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
        var holder = words[holderOffset];
        if (holder == 0)
        {
            words[savedOffset] = me.Temporaries[0];
            words[holderOffset] = me.Id;
            QueueLotScreenDialog(context, kind);
            return PrimitiveResult.Stop;
        }
        if (holder == me.Id)
        {
            if (IsLotScreenDialogPending(world, kind)) return PrimitiveResult.Stop;
            me.Temporaries[0] = words[0xf6];
            var released = words[0xf4] == ReleaseMark;
            words[savedOffset] = 0;
            words[holderOffset] = 0;
            return released ? PrimitiveResult.True : PrimitiveResult.False;
        }
        words[savedOffset] = 0;
        words[holderOffset] = 0;
        me.Temporaries[0] = 0;
        return PrimitiveResult.False;
    }

    /// <summary>Case 9 (0x451864): the +0x104 holder is a person selected by the running object's frame stack key.
    /// The +0x100 release mark is retained on success and reset to -1 on either failed release.</summary>
    private static PrimitiveResult StackObjectHolder(in TreeContext context, DialogWords words)
    {
        var selected = context.World.ObjectByStackObject(context.Me, context.Frame.StackObject);
        if (selected is not XPerson person)
            throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);

        var holder = words[0x104];
        if (holder == 0)
        {
            words[0x104] = person.Id;
            QueueLotScreenDialog(context, 9, person);
            return PrimitiveResult.Stop;
        }

        if (holder == person.Id)
        {
            if (IsLotScreenDialogPending(context.World, 9)) return PrimitiveResult.Stop;
            context.Me.Temporaries[0] = words[0x100];
            if (words[0x108] == ReleaseMark)
            {
                words[0x104] = 0;
                return PrimitiveResult.True;
            }
        }
        else
        {
            context.Me.Temporaries[0] = 0;
        }

        words[0x100] = Reserved;
        words[0x104] = 0;
        return PrimitiveResult.False;
    }

    /// <summary>Case 0xa (0x451800): the person posts its negative neighbor id into +0xe8, or consumes the posted
    /// word. Nonpositive consumed words do not overwrite temporary 0.</summary>
    private static PrimitiveResult NeighborIdExchange(in TreeContext context, DialogWords words)
    {
        var (world, me, _) = context;
        if (me is not XPerson person)
            throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);

        var value = words[0xe8];
        if (value != 0)
        {
            if (IsLotScreenDialogPending(world, 10)) return PrimitiveResult.Stop;
            if (value > 0) me.Temporaries[0] = value;
            words[0xe8] = 0;
            return value > 0 ? PrimitiveResult.True : PrimitiveResult.False;
        }

        words[0xe8] = unchecked((short)-person[PersonData.NeighborId]);
        QueueLotScreenDialog(context, 10);
        return PrimitiveResult.Stop;
    }
}
