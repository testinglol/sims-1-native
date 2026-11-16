using System.Diagnostics;
using System.Globalization;

namespace SimsSDL;

public sealed class FrameProfiler
{
    public bool Enabled { get; } = Environment.GetEnvironmentVariable("SIMS_PROFILE") is not null;

    private readonly Dictionary<string, long> _totals = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private long _last;
    private long _windowStart;
    private bool _hasWindowStart;
    private int _frames;

    public void Begin()
    {
        if (!Enabled)
            return;
        _last = Stopwatch.GetTimestamp();
        if (!_hasWindowStart)
        {
            _windowStart = _last;
            _hasWindowStart = true;
        }
    }

    public void Mark(string phase)
    {
        if (!Enabled)
            return;
        long now = Stopwatch.GetTimestamp();
        if (!_totals.TryGetValue(phase, out long existing))
            _order.Add(phase);
        _totals[phase] = existing + (now - _last);
        _last = now;
    }

    public void EndFrame()
    {
        if (!Enabled)
            return;
        _frames++;
        if (_frames < 120)
            return;

        long now = Stopwatch.GetTimestamp();
        double elapsedSeconds = (now - _windowStart) / (double)Stopwatch.Frequency;
        var phaseParts = new List<string>(_order.Count);
        foreach (string phase in _order)
        {
            long ticks = _totals.GetValueOrDefault(phase);
            double ms = ticks * 1000.0 / Stopwatch.Frequency / _frames;
            phaseParts.Add($"{phase} {ms.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        string phases = string.Join(", ", phaseParts);
        double fps = _frames / Math.Max(elapsedSeconds, 1e-9);
        EventProfiler.Flush();
        Console.Error.WriteLine(
            $"SimsSDL: profile: {fps.ToString("F1", CultureInfo.InvariantCulture)} fps, ms/frame: {phases}, rss {ResidentMegabytes()} MB");

        _totals.Clear();
        _order.Clear();
        _frames = 0;
        _windowStart = Stopwatch.GetTimestamp();
    }

    private static int ResidentMegabytes()
    {
        try
        {
            if (File.Exists("/proc/self/statm"))
            {
                string statm = File.ReadAllText("/proc/self/statm");
                string[] parts = statm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long pages))
                    return (int)(pages * Environment.SystemPageSize / 1_048_576L);
            }
        }
        catch
        {
        }

        return (int)(Environment.WorkingSet / 1_048_576L);
    }
}

public static class EventProfiler
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("SIMS_PROFILE") is not null;

    private static readonly Dictionary<string, (int Count, long Ticks)> Totals = new(StringComparer.Ordinal);

    public static void Measure(string type, Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!Enabled)
        {
            body();
            return;
        }

        long start = Stopwatch.GetTimestamp();
        try
        {
            body();
        }
        finally
        {
            Record(type, Stopwatch.GetTimestamp() - start);
        }
    }

    public static T Measure<T>(string type, Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!Enabled)
            return body();

        long start = Stopwatch.GetTimestamp();
        try
        {
            return body();
        }
        finally
        {
            Record(type, Stopwatch.GetTimestamp() - start);
        }
    }

    public static void Flush()
    {
        if (!Enabled || Totals.Count == 0)
            return;

        string summary = string.Join(
            ", ",
            Totals
                .OrderByDescending(static kv => kv.Value.Ticks)
                .Select(static kv =>
                {
                    double ms = kv.Value.Ticks * 1000.0 / Stopwatch.Frequency;
                    return $"{kv.Key} {kv.Value.Count}/{ms.ToString("F0", CultureInfo.InvariantCulture)}ms";
                }));
        Console.Error.WriteLine($"SimsSDL: events: {summary}");
        Totals.Clear();
    }

    private static void Record(string type, long elapsedTicks)
    {
        Totals.TryGetValue(type, out var entry);
        Totals[type] = (entry.Count + 1, entry.Ticks + elapsedTicks);
    }
}
