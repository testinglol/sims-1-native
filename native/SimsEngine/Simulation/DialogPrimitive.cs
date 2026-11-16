namespace SimsEngine.Simulation;

/// <summary>Tracks shared lot dialog synchronization words and object reservations accessed by the Dialog
/// primitive.</summary>
public sealed class DialogWords
{
    private readonly short[] _words = new short[0x90];

    public DialogWords()
    {
        // Initializes pet adoption choice to -1 so a random breed is selected unless chosen via dialog.
        _words[0x100 >> 1] = -1;
    }

    public short this[int offset]
    {
        get => _words[offset >> 1];
        set => _words[offset >> 1] = value;
    }
}

/// <summary>Implements the Dialog primitive (opcode 0x24), which creates modal UI dialog records (message,
/// travel, wardrobe, phone book) and manages lot-level synchronization lock words and person-to-person
/// dialog reservations.</summary>
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
            if (host is null && world.DialogRecord(me) is { } dismissedAfterAbort) world.CompleteDialogRecord(dismissedAfterAbort);
            state = null;
        }
        else if (state == 5)
        {
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
        var stackIcon = world.ObjectByStackObject(me, frame.StackObject);
        if (kind is 5 or 7 or 8 or 0xb or 0xd)
        {
            return world.Host is { } screenHost
                ? screenHost.CreateDialogRecord(world, me, stackIcon, "", "", null, null, null, kind, blocking, awaitsResponse)
                : world.CreateMessageDialog(me, stackIcon, "", "", null, null, null, kind, blocking, awaitsResponse);
        }
        // Seeds default button labels from UIText.iff STR# 142 (OK, Cancel, Yes, No) and overrides them
        // with any non-empty STR# 301 entries.
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

    /// <summary>Resolves the dialog icon selector for modes 0 (Auto), 1 (None), 2 (Neighbor), 3 (Indexed),
    /// and 4 (Dynamic STR# 301).</summary>
    private static XObject? ResolveDialogIcon(SimulationWorld world, XObject me, StackFrame frame, XObject? stackIcon,
        int iconMode, AutoIconToken autoToken, out string? neighborSpec)
    {
        neighborSpec = null;
        switch (iconMode)
        {
            case 0:
                if (autoToken == AutoIconToken.Neighbor && ResolveNeighborByIdOrSlot(world, frame.StackObject) is { } autoNeighbor)
                {
                    neighborSpec = $"dialog-neighbor:{autoNeighbor.TypeRecordKey:X8}:{autoNeighbor.Name}";
                    return FindPersonForNeighbor(world, frame.StackObject, autoNeighbor);
                }
                return ResolveMasterObject(world, stackIcon);
            case 1:
            case 3:
            case 4:
                return null;
            case 2:
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

    /// <summary>Populates dialog image metadata for indexed icons (5000 + operand offset), dynamic STR# 301
    /// command specs, and off-lot neighbor portraits.</summary>
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
        // Resolves STR# 301 through the frame's code-owner resource, which may differ from the Sim running
        // the thread in shared routines.
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
        // Expands $Me using the object's assigned name, falling back to CTSS entry 0, then the OBJD catalog
        // label.
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

    /// <summary>Acquires or releases a lot dialog lock word: an available lock is reserved (-1, yielding
    /// execution), while a held lock transfers its value to temporary 0 and is released.</summary>
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

    /// <summary>Manages neighbor ID exchange on the lot: posts the person's negated neighbor ID if empty,
    /// or consumes a posted positive ID into temporary 0.</summary>
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
