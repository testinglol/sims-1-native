namespace SimsEngine.Simulation;

public sealed class InteractionBehaviorActionList(IEnumerable<InteractionBehaviorAction> entries)
{
    public IReadOnlyList<InteractionBehaviorAction> Entries { get; } = entries is null
        ? throw new ArgumentNullException(nameof(entries))
        : Array.AsReadOnly(entries.ToArray());

    /// <summary>Returns the index of the first record matching both query strings, comparing the second
    /// string only after the first matches.</summary>
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

public readonly record struct InteractionBehaviorActionQuery(string First, string Second);

/// <summary>Formats action query strings, truncating results to 63 characters with case-sensitive delimiter
/// parsing and ASCII-only lowercasing.</summary>
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

    /// <summary>Splits a body string into an appearance identifier and texture binding, lowercasing the
    /// appearance and the texture prefix with a 63-character limit.</summary>
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

    /// <summary>Finds the index of an action matching the body string appearance and texture, or falls back
    /// to single-field matching when no appearance is specified.</summary>
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

    public static string BuildSelectedActionUpdate(string action) =>
        Truncate("," + CStringPrefix(action ?? throw new ArgumentNullException(nameof(action))), TemporaryCapacity - 1);

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

    internal void Close()
    {
        TypeRecord = null;
        QueryContext = null;
        Array.Clear(_actionLists);
        RuntimeContext.ClearStrings();
    }

    public static InteractionBehaviorScope Initialize() => new();
}

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
    /// <summary>Returns the string count for the specified mode, exposing the list only when the mode
    /// matches or is -1.</summary>
    public int StringCount(sbyte mode = -1) => mode == -1 || mode == Offset5D ? _strings.Count : 0;

    public string? StringAt(int index, sbyte mode = -1) =>
        index > 0 && index <= StringCount(mode) ? _strings[index - 1] : null;

    /// <summary>Expands the one-based string table with empty entries as needed and assigns the value at
    /// the given index, ignoring non-positive indices.</summary>
    public void SetString(int index, string? value)
    {
        if (index <= 0) return;
        while (_strings.Count < index) _strings.Add(string.Empty);
        _strings[index - 1] = value ?? string.Empty;
    }

    internal void ClearStrings() => _strings.Clear();
}
