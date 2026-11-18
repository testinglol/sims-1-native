using System.Collections.Concurrent;

namespace SimsSDL.Core;

internal static class SimsModuleInit
{
    [ModuleInitializer]
    internal static void InitializeEncodings()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
}

public readonly record struct CGPoint(double X, double Y)
{
    public static CGPoint Zero => new(0, 0);
}

public readonly record struct CGSize(double Width, double Height)
{
    public static CGSize Zero => new(0, 0);
}

public readonly record struct CGRect(double X, double Y, double Width, double Height)
{
    public static CGRect Zero => new(0, 0, 0, 0);

    public CGRect(CGPoint origin, CGSize size) : this(origin.X, origin.Y, size.Width, size.Height) { }

    public CGPoint Origin => new(X, Y);
    public CGSize Size => new(Width, Height);

    public double MinX => Width >= 0 ? X : X + Width;
    public double MaxX => Width >= 0 ? X + Width : X;
    public double MidX => X + Width * 0.5;
    public double MinY => Height >= 0 ? Y : Y + Height;
    public double MaxY => Height >= 0 ? Y + Height : Y;
    public double MidY => Y + Height * 0.5;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(CGPoint point) =>
        point.X >= MinX && point.X < MaxX && point.Y >= MinY && point.Y < MaxY;

    public bool Intersects(CGRect other) =>
        MinX < other.MaxX && other.MinX < MaxX && MinY < other.MaxY && other.MinY < MaxY;

    public CGRect InsetBy(double dx, double dy) =>
        new(X + dx, Y + dy, Width - 2 * dx, Height - 2 * dy);

    public CGRect OffsetBy(double dx, double dy) =>
        new(X + dx, Y + dy, Width, Height);
}

public sealed class ContentError(string message) : Exception(message)
{
    public static ContentError InvalidChunk(string type, string reason) => new($"Invalid {type} chunk: {reason}");
    public static ContentError UnsupportedFormat(string detail) => new($"Unsupported format: {detail}");
}

public static class MainThreadDispatcher
{
    private static readonly ConcurrentQueue<Action> Queue = new();
    private static int _mainThreadId = Environment.CurrentManagedThreadId;

    public static void InitializeMainThread() => _mainThreadId = Environment.CurrentManagedThreadId;

    public static bool IsMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    public static void Post(Action action) => Queue.Enqueue(action);

    public static void Drain()
    {
        while (Queue.TryDequeue(out var action))
            action();
    }
}

public static class SimsAssetPaths
{
    public static string? FindShadersDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        ReadOnlySpan<string> candidates =
        [
            Path.Combine(baseDir, "Shaders"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "Resources", "Shaders")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "native", "SimsSDL", "Shaders")),
        ];
        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }
}

public static class JsonMapExtensions
{
    public static Dictionary<string, JsonElement> DecodeJsonLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.ValueKind == JsonValueKind.Object ? CloneObject(doc.RootElement) : [];
    }

    public static Dictionary<string, JsonElement> CloneObject(JsonElement element)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object) return dict;
        foreach (var prop in element.EnumerateObject())
            dict[prop.Name] = prop.Value.Clone();
        return dict;
    }

    public static string? JsonString(this IReadOnlyDictionary<string, JsonElement> map, string key) =>
        map.TryGetValue(key, out var val) && val.ValueKind == JsonValueKind.String ? val.GetString() : null;

    public static bool? JsonBool(this IReadOnlyDictionary<string, JsonElement> map, string key) =>
        map.TryGetValue(key, out var val) && val.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? val.GetBoolean()
            : null;

    public static int? JsonInt(this IReadOnlyDictionary<string, JsonElement> map, string key)
    {
        if (!map.TryGetValue(key, out var val)) return null;
        if (val.ValueKind == JsonValueKind.Number)
        {
            if (val.TryGetInt32(out var i)) return i;
            if (val.TryGetDouble(out var d)) return (int)d;
        }
        return null;
    }

    public static short? JsonInt16(this IReadOnlyDictionary<string, JsonElement> map, string key)
    {
        var i = map.JsonInt(key);
        return i is >= short.MinValue and <= short.MaxValue ? (short)i.Value : null;
    }

    public static byte? JsonByte(this IReadOnlyDictionary<string, JsonElement> map, string key)
    {
        var i = map.JsonInt(key);
        return i is >= byte.MinValue and <= byte.MaxValue ? (byte)i.Value : null;
    }

    public static long? JsonInt64(this IReadOnlyDictionary<string, JsonElement> map, string key)
    {
        if (!map.TryGetValue(key, out var val) || val.ValueKind != JsonValueKind.Number) return null;
        if (val.TryGetInt64(out var l)) return l;
        if (val.TryGetDouble(out var d)) return (long)d;
        return null;
    }

    public static double? JsonDouble(this IReadOnlyDictionary<string, JsonElement> map, string key) =>
        map.TryGetValue(key, out var val) && val.ValueKind == JsonValueKind.Number && val.TryGetDouble(out var d)
            ? d
            : null;

    public static Dictionary<string, JsonElement>? JsonDict(this IReadOnlyDictionary<string, JsonElement> map, string key) =>
        map.TryGetValue(key, out var val) && val.ValueKind == JsonValueKind.Object ? CloneObject(val) : null;

    public static List<Dictionary<string, JsonElement>>? JsonArray(this IReadOnlyDictionary<string, JsonElement> map, string key)
    {
        if (!map.TryGetValue(key, out var val) || val.ValueKind != JsonValueKind.Array) return null;
        var result = new List<Dictionary<string, JsonElement>>();
        foreach (var item in val.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
                result.Add(CloneObject(item));
        }
        return result;
    }

    public static List<string>? JsonStringArray(this IReadOnlyDictionary<string, JsonElement> map, string key)
    {
        if (!map.TryGetValue(key, out var val) || val.ValueKind != JsonValueKind.Array) return null;
        var result = new List<string>();
        foreach (var item in val.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                result.Add(s);
        }
        return result;
    }

    public static List<int>? JsonIntArray(this IReadOnlyDictionary<string, JsonElement> map, string key)
    {
        if (!map.TryGetValue(key, out var val) || val.ValueKind != JsonValueKind.Array) return null;
        var result = new List<int>();
        foreach (var item in val.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var i))
                result.Add(i);
        }
        return result;
    }
}
