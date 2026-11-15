namespace SimsEngine.Simulation;

/// <summary>The six cached 0x98-byte action arrays held by an interaction behavior scope. Their record fields remain
/// opaque until the Sims.exe matching and update helpers are ported; keeping the complete bytes preserves them exactly.</summary>
public sealed class InteractionBehaviorActionList(IEnumerable<InteractionBehaviorAction> entries)
{
    public IReadOnlyList<InteractionBehaviorAction> Entries { get; } = entries is null
        ? throw new ArgumentNullException(nameof(entries))
        : Array.AsReadOnly(entries.ToArray());

    /// <summary>Returns the first record whose two runtime string fields match the requested strings, or -1.
    /// Sims.exe FUN_00473098 compares the first field and checks the second only after the first matches.</summary>
    public int FindIndex(string first, string second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        for (var index = 0; index < Entries.Count; index++)
        {
            var entry = Entries[index];
            if (AsciiEqualsIgnoreCase(entry.First, first) && AsciiEqualsIgnoreCase(entry.Second, second))
                return index;
        }
        return -1;
    }

    private static bool AsciiEqualsIgnoreCase(string left, string right)
    {
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
        {
            static char FoldAscii(char value) => value is >= 'A' and <= 'Z' ? (char)(value + ('a' - 'A')) : value;
            if (FoldAscii(left[index]) != FoldAscii(right[index])) return false;
        }
        return true;
    }
}

/// <summary>One cached action record in the scope's arrays. The original 0x98-byte record contains two 0x4c-byte
/// runtime string objects at offsets 0 and 0x4c; the managed model stores their text rather than native pointers and
/// vtables.</summary>
public sealed class InteractionBehaviorAction
{
    public const int Size = 0x98;

    public InteractionBehaviorAction(string first, string second)
    {
        First = first ?? throw new ArgumentNullException(nameof(first));
        Second = second ?? throw new ArgumentNullException(nameof(second));
    }

    public string First { get; }
    public string Second { get; }
}

/// <summary>The two query strings built by Sims.exe FUN_00472FCA/FUN_00455EDC for one action category.</summary>
public readonly record struct InteractionBehaviorActionQuery(string First, string Second);

/// <summary>FUN_00455EDC query-string transformation. The original uses 256-byte temporary buffers and a
/// 64-byte output string; output strings therefore retain at most 63 characters. Delimiter searches are case
/// sensitive, and only ASCII A-Z are lowercased.</summary>
public static class InteractionBehaviorActionQueryBuilder
{
    private const int TemporaryCapacity = 0x100;
    private const int OutputCapacity = 0x40;

    public static InteractionBehaviorActionQuery Build(string? source, bool afterSemicolon = false)
    {
        var input = CStringPrefix(source ?? string.Empty);
        var temporary = Truncate(input, TemporaryCapacity - 1);
        var semicolon = temporary.IndexOf(';');
        var section = semicolon < 0
            ? (afterSemicolon ? string.Empty : temporary)
            : afterSemicolon ? temporary[(semicolon + 1)..] : temporary[..semicolon];
        section = Truncate(section, TemporaryCapacity - 1);

        var comma = section.IndexOf(',');
        var second = section;
        if (comma >= 0)
        {
            var prefix = section[..comma];
            var suffix = section[(comma + 1)..];
            var equals = suffix.IndexOf('=');
            second = equals < 0 ? suffix : prefix + suffix[(equals + 1)..];
        }

        return new(string.Empty, AsciiLower(Truncate(second, OutputCapacity - 1)));
    }

    /// <summary>FUN_00455EDC two-field BodyString query split: extracts the appearance name before ',' into
    /// <see cref="InteractionBehaviorActionQuery.First"/> (ASCII-lowercased, 63-char cap) and the texture binding
    /// after ',' into <see cref="InteractionBehaviorActionQuery.Second"/> (lowercasing key= prefix only, 63-char cap).</summary>
    public static InteractionBehaviorActionQuery BuildBodyStringQuery(string? source, bool afterSemicolon = false)
    {
        var input = CStringPrefix(source ?? string.Empty);
        var temporary = Truncate(input, TemporaryCapacity - 1);
        var semicolon = temporary.IndexOf(';');
        var section = semicolon < 0
            ? (afterSemicolon ? string.Empty : temporary)
            : afterSemicolon ? temporary[(semicolon + 1)..] : temporary[..semicolon];
        section = Truncate(section, TemporaryCapacity - 1);

        var comma = section.IndexOf(',');
        if (comma < 0)
            return new(AsciiLower(Truncate(section, OutputCapacity - 1)), string.Empty);

        var first = AsciiLower(Truncate(section[..comma], OutputCapacity - 1));
        var suffix = section[(comma + 1)..];
        var equals = suffix.IndexOf('=');
        string second;
        if (equals < 0)
        {
            second = AsciiLower(Truncate(suffix, OutputCapacity - 1));
        }
        else
        {
            var keyPrefix = AsciiLower(Truncate(suffix[..(equals + 1)], OutputCapacity - 1));
            second = Truncate(keyPrefix + suffix[(equals + 1)..], OutputCapacity - 1);
        }

        return new(first, second);
    }

    /// <summary>Matches <paramref name="source"/> against <paramref name="list"/> using FUN_00455EDC's two-field
    /// BodyString split when the list holds appearance+texture pairs, or the single-field query when First is empty.</summary>
    public static int FindMatchingIndex(InteractionBehaviorActionList list, string? source, bool afterSemicolon = false)
    {
        ArgumentNullException.ThrowIfNull(list);
        var hasAppearanceNames = false;
        for (var i = 0; i < list.Entries.Count; i++)
        {
            if (list.Entries[i].First.Length > 0)
            {
                hasAppearanceNames = true;
                break;
            }
        }

        var query = hasAppearanceNames
            ? BuildBodyStringQuery(source, afterSemicolon)
            : Build(source, afterSemicolon);
        return list.FindIndex(query.First, query.Second);
    }

    /// <summary>FUN_00473047 builds a comma followed by the action's second string in a 256-byte temporary buffer.</summary>
    public static string BuildSelectedActionUpdate(string action) =>
        Truncate("," + CStringPrefix(action ?? throw new ArgumentNullException(nameof(action))), TemporaryCapacity - 1);

    /// <summary>FUN_00473047: concatenates <c>action.First + "," + action.Second</c> in a 256-byte temporary buffer.</summary>
    public static string BuildSelectedActionUpdate(InteractionBehaviorAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Truncate(CStringPrefix(action.First) + "," + CStringPrefix(action.Second), TemporaryCapacity - 1);
    }

    private static string CStringPrefix(string value)
    {
        var terminator = value.IndexOf('\0');
        return terminator < 0 ? value : value[..terminator];
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static string AsciiLower(string value)
    {
        var result = value.ToCharArray();
        for (var index = 0; index < result.Length; index++)
            if (result[index] is >= 'A' and <= 'Z')
                result[index] = (char)(result[index] + ('a' - 'A'));
        return new string(result);
    }
}

/// <summary>One of the six GenericSimsCall 0x18 action groups and its query id passed to FUN_00472FCA.</summary>
public readonly record struct InteractionBehaviorActionGroup(int ListIndex, int QueryId)
{
    public static bool TryGet(int mode, out InteractionBehaviorActionGroup group)
    {
        group = mode switch
        {
            0 => new(0, 0x02),
            1 => new(1, 0x1f),
            2 => new(2, 0x20),
            3 => new(3, 0x21),
            4 => new(4, 0x22),
            5 => new(5, 0x23),
            _ => default,
        };
        return (uint)mode < InteractionBehaviorScope.ActionArrayCount;
    }
}

/// <summary>
/// Native data model for the 0x98-byte local scope initialized by Sims.exe FUN_00408B9D. The type record, mode and
/// context pointer are the first three dwords; a nested 0x6c-byte runtime context starts at +0x0c, and six cached action
/// arrays occupy +0x78 through +0x94. FUN_00408B9D clears the first two dwords and the six array pointers; the nested
/// context constructor FUN_00587670 initializes its own state. These arrays are distinct from the 12-byte executor
/// entries modeled by <see cref="InteractionScopeTable"/>.
/// </summary>
public sealed class InteractionBehaviorScope
{
    public const int Size = 0x98;
    public const int RuntimeContextSize = 0x6c;
    public const int ActionArrayCount = 6;

    private readonly InteractionBehaviorActionList?[] _actionLists = new InteractionBehaviorActionList?[ActionArrayCount];

    private InteractionBehaviorScope()
    {
        RuntimeContext = new InteractionBehaviorRuntimeContext();
    }

    public TypeRecord? TypeRecord { get; internal set; }
    public int Mode { get; internal set; }
    /// <summary>The string context returned by FUN_004580CC and stored at +8 by FUN_00409377. It is distinct from
    /// the nested runtime object at +0x0c.</summary>
    public InteractionBehaviorRuntimeContext? QueryContext { get; internal set; }
    public InteractionBehaviorRuntimeContext RuntimeContext { get; }

    public InteractionBehaviorActionList? ActionList(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ActionArrayCount);
        return _actionLists[index];
    }

    public void SetActionList(int index, InteractionBehaviorActionList? list)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ActionArrayCount);
        _actionLists[index] = list;
    }

    /// <summary>Clears the references released when FUN_004094F4 closes this stack-local scope.</summary>
    internal void Close()
    {
        TypeRecord = null;
        QueryContext = null;
        Array.Clear(_actionLists);
        RuntimeContext.ClearStrings();
    }

    /// <summary>Constructs the zeroed scope and nested runtime state described by FUN_00408B9D/FUN_00587670.</summary>
    public static InteractionBehaviorScope Initialize() => new();
}

/// <summary>The known initialized data in a runtime context (FUN_00587670/FUN_0046FCA5/FUN_0047034E). Its original
/// vtable pointers are represented by this managed type. The string-vector pointers at +0x60/+0x64 and capacity word
/// at +0x68 are represented by a managed one-based list, not retained as native addresses. The 64-byte range at +0x14
/// is cleared by FUN_0046FCA5.</summary>
public sealed class InteractionBehaviorRuntimeContext
{
    public const int Size = InteractionBehaviorScope.RuntimeContextSize;
    private readonly List<string> _strings = [];

    public InteractionBehaviorRuntimeContext(sbyte mode = 0)
    {
        ClearedRange14 = new byte[0x40];
        Offset5D = mode;
    }

    public int Offset04 { get; internal set; }
    public byte[] ClearedRange14 { get; }
    public short Offset54 { get; internal set; }
    public int Offset58 { get; internal set; }
    public byte Offset5C { get; internal set; }
    public sbyte Offset5D { get; set; }
    /// <summary>The count and one-based string lookup of FUN_0046FBE5/FUN_004705F2. A mode other than -1 only
    /// exposes the list when it equals the context's +0x5d mode byte.</summary>
    public int StringCount(sbyte mode = -1) => mode == -1 || mode == Offset5D ? _strings.Count : 0;

    public string? StringAt(int index, sbyte mode = -1) =>
        index > 0 && index <= StringCount(mode) ? _strings[index - 1] : null;

    /// <summary>FUN_005876AC/FUN_004704A5: grow the one-based list with empty strings, then replace an entry.
    /// Non-positive indices are ignored by the original setter.</summary>
    public void SetString(int index, string? value)
    {
        if (index <= 0) return;
        while (_strings.Count < index) _strings.Add(string.Empty);
        _strings[index - 1] = value ?? string.Empty;
    }

    internal void ClearStrings() => _strings.Clear();
}
