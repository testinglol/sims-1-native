using System.Text.Json.Serialization;

namespace SimsSDL.Core;

public enum TerrainBrushMode
{
    Lower,
    Level,
    Raise,
    Grass,
}

public static class TerrainBrushModeExtensions
{
    public static string Label(this TerrainBrushMode mode) => mode switch
    {
        TerrainBrushMode.Lower => "Lower terrain",
        TerrainBrushMode.Level => "Level terrain",
        TerrainBrushMode.Raise => "Raise terrain",
        TerrainBrushMode.Grass => "Grass",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static string RawValue(this TerrainBrushMode mode) => mode switch
    {
        TerrainBrushMode.Lower => "lower",
        TerrainBrushMode.Level => "level",
        TerrainBrushMode.Raise => "raise",
        TerrainBrushMode.Grass => "grass",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}

public enum ConstructionHistoryAction
{
    Undo,
    Redo,
}

public static class ConstructionHistoryActionExtensions
{
    public static string RawValue(this ConstructionHistoryAction action) => action switch
    {
        ConstructionHistoryAction.Undo => "undo",
        ConstructionHistoryAction.Redo => "redo",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };
}

public sealed record ArchitectureCommandRequest(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("level")] int Level,
    [property: JsonPropertyName("x2")] int X2,
    [property: JsonPropertyName("y2")] int Y2,
    [property: JsonPropertyName("pattern")] ushort Pattern,
    [property: JsonPropertyName("style")] ushort Style)
{
    [JsonIgnore]
    public Dictionary<string, object?> Payload => new()
    {
        ["command"] = Command,
        ["x"] = X,
        ["y"] = Y,
        ["level"] = Level,
        ["x2"] = X2,
        ["y2"] = Y2,
        ["pattern"] = Pattern,
        ["style"] = Style,
    };
}

public static class TerrainHeightDrag
{
    public const double SceneUnitsPerHeightLevel = 0.75;

    public static short Delta(double startY, double endY, double viewportScale, TerrainBrushMode mode)
    {
        if (!double.IsFinite(startY) || !double.IsFinite(endY) ||
            !double.IsFinite(viewportScale) || viewportScale <= 0)
        {
            throw new ContentError("Terrain height drag requires finite screen positions and viewport scale");
        }

        double heightLevels = Math.Round(
            (startY - endY) / (viewportScale * SceneUnitsPerHeightLevel),
            MidpointRounding.AwayFromZero);
        if (!double.IsFinite(heightLevels))
        {
            throw new ContentError("Terrain height drag is outside the supported range");
        }

        double direction = mode switch
        {
            TerrainBrushMode.Raise => 1.0,
            TerrainBrushMode.Lower => -1.0,
            _ => throw new ContentError("Height delta is only valid for raise or lower gestures"),
        };

        return (short)Math.Min(short.MaxValue, Math.Max(short.MinValue, heightLevels * direction));
    }
}

public sealed class TerrainBrushGesture
{
    private readonly List<GroundTile> _samples = [];
    private readonly HashSet<GroundTile> _visited = [];
    private int _flushedGrassCount;

    public GroundTile? Start { get; private set; }
    public GroundTile? End { get; private set; }
    public IReadOnlyList<GroundTile> Samples => _samples;

    public void Begin(GroundTile tile)
    {
        Start = tile;
        End = tile;
        _samples.Clear();
        _samples.Add(tile);
        _visited.Clear();
        _visited.Add(tile);
        _flushedGrassCount = 0;
    }

    public void Move(GroundTile tile)
    {
        if (Start is null)
        {
            return;
        }

        End = tile;
        if (_samples.Count == 0)
        {
            return;
        }

        GroundTile previous = _samples[^1];
        if (previous == tile)
        {
            return;
        }

        List<GroundTile> points = Rasterize(previous, tile);
        for (int i = 1; i < points.Count; i++)
        {
            GroundTile point = points[i];
            if (_visited.Add(point))
            {
                _samples.Add(point);
            }
        }
    }

    /// <summary>Returns grass tiles visited since the last drain and advances the flushed cursor so live
    /// strokes are not double-charged on release.</summary>
    public List<GroundTile> DrainPendingGrassSamples()
    {
        if (Start is null)
        {
            return [];
        }

        List<GroundTile> pending = _samples.GetRange(_flushedGrassCount, _samples.Count - _flushedGrassCount);
        _flushedGrassCount = _samples.Count;
        return pending;
    }

    public void Cancel()
    {
        Start = null;
        End = null;
        _samples.Clear();
        _visited.Clear();
        _flushedGrassCount = 0;
    }

    public ArchitectureCommandRequest Finish(
        TerrainBrushMode mode,
        int level,
        LotScene scene,
        short? heightDelta = null,
        short grassStep = -32,
        bool smooth = false)
    {
        if (Start is not { } start)
        {
            throw new ContentError("Terrain gesture has no start tile");
        }

        GroundTile? end = End;
        Start = null;
        End = null;
        _flushedGrassCount = 0;
        return TerrainBrushRequest.Make(
            mode, start, end, level, scene,
            heightDelta: heightDelta, grassStep: grassStep, smooth: smooth);
    }

    public List<ArchitectureCommandRequest> FinishStroke(
        TerrainBrushMode mode,
        int level,
        LotScene scene,
        short? heightDelta = null,
        short grassStep = -32,
        bool smooth = false)
    {
        if (Start is not { } start)
        {
            throw new ContentError("Terrain gesture has no start tile");
        }

        GroundTile? end = End;
        List<GroundTile> samples = [.. _samples];
        int flushedGrassCount = _flushedGrassCount;
        Start = null;
        End = null;
        _samples.Clear();
        _visited.Clear();
        _flushedGrassCount = 0;

        if (mode == TerrainBrushMode.Grass && flushedGrassCount > 0)
        {
            if (flushedGrassCount >= samples.Count)
            {
                return [];
            }

            List<GroundTile> remaining = samples.GetRange(flushedGrassCount, samples.Count - flushedGrassCount);
            return TerrainBrushRequest.MakeStroke(
                TerrainBrushMode.Grass, start, end, remaining, level, scene,
                heightDelta: heightDelta, grassStep: grassStep, smooth: smooth);
        }

        return TerrainBrushRequest.MakeStroke(
            mode, start, end, samples, level, scene,
            heightDelta: heightDelta, grassStep: grassStep, smooth: smooth);
    }

    private static List<GroundTile> Rasterize(GroundTile start, GroundTile end)
    {
        int x = start.X;
        int y = start.Y;
        int dx = Math.Abs(end.X - start.X);
        int sx = start.X < end.X ? 1 : -1;
        int dy = -Math.Abs(end.Y - start.Y);
        int sy = start.Y < end.Y ? 1 : -1;
        int error = dx + dy;
        var result = new List<GroundTile> { start };

        while (x != end.X || y != end.Y)
        {
            int twice = 2 * error;
            if (twice >= dy)
            {
                error += dy;
                x += sx;
            }

            if (twice <= dx)
            {
                error += dx;
                y += sy;
            }

            result.Add(new GroundTile(x, y));
        }

        return result;
    }
}

public static class TerrainBrushRequest
{
    public static List<ArchitectureCommandRequest> MakeStroke(
        TerrainBrushMode mode,
        GroundTile start,
        GroundTile? end,
        IReadOnlyList<GroundTile> samples,
        int level,
        LotScene scene,
        short? heightDelta = null,
        short grassStep = -32,
        bool smooth = false)
    {
        switch (mode)
        {
            case TerrainBrushMode.Level:
                return [Make(mode, start, end, level, scene, heightDelta, grassStep, smooth)];

            case TerrainBrushMode.Raise:
            case TerrainBrushMode.Lower:
                if (heightDelta is null)
                {
                    throw new ContentError("Terrain raise or lower requires a vertical drag delta");
                }

                return [Make(mode, start, null, level, scene, heightDelta, grassStep, smooth)];

            case TerrainBrushMode.Grass:
                IReadOnlyList<GroundTile> points = samples.Count == 0 ? new[] { start } : samples;
                if (scene.LotWidth is not { } width || scene.LotHeight is not { } height ||
                    points.Count > width * height)
                {
                    throw new ContentError("Terrain stroke exceeds the lot tile count");
                }

                var requests = new List<ArchitectureCommandRequest>(points.Count);
                foreach (GroundTile point in points)
                {
                    requests.Add(Make(mode, point, null, level, scene, heightDelta, grassStep, smooth));
                }

                return requests;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    /// <summary>Builds a terrain raise command relative to the drag-start vertex height so streaming
    /// updates mid-drag do not compound.</summary>
    public static ArchitectureCommandRequest MakeLiveRaise(
        TerrainBrushMode mode,
        GroundTile vertex,
        byte baseHeight,
        int level,
        int lotWidth,
        int lotHeight,
        int stories,
        short heightDelta,
        bool smooth = false)
    {
        if (mode is not (TerrainBrushMode.Raise or TerrainBrushMode.Lower))
        {
            throw new ContentError("Live terrain raise requires raise or lower brush mode");
        }

        if (lotWidth <= 0 || lotHeight <= 0 || stories < 1 ||
            level < 1 || level > stories ||
            vertex.X < 0 || vertex.X >= lotWidth ||
            vertex.Y < 0 || vertex.Y >= lotHeight)
        {
            throw new ContentError("Terrain edit requires valid lot dimensions, level, and vertex");
        }

        return MakeRaiseCommand(vertex, baseHeight, level, heightDelta, smooth);
    }

    public static ArchitectureCommandRequest MakeLiveRaise(
        TerrainBrushMode mode,
        GroundTile vertex,
        byte baseHeight,
        int level,
        int lotWidth,
        int lotHeight,
        short heightDelta,
        bool smooth = false) =>
        MakeLiveRaise(mode, vertex, baseHeight, level, lotWidth, lotHeight, stories: 5, heightDelta, smooth);

    public static ArchitectureCommandRequest Make(
        TerrainBrushMode mode,
        GroundTile start,
        GroundTile? end,
        int level,
        LotScene scene,
        short? heightDelta = null,
        short grassStep = -32,
        bool smooth = false)
    {
        int maxStories = scene.Stories ?? 5;
        if (scene.LotWidth is not { } width || scene.LotHeight is not { } height ||
            width <= 0 || height <= 0 ||
            level < 1 || level > maxStories ||
            start.X < 0 || start.X >= width || start.Y < 0 || start.Y >= height ||
            scene.TerrainHeights is not { } heights || heights.Count != width * height)
        {
            throw new ContentError("Terrain edit requires a valid lot height grid and start tile");
        }

        int startIndex = start.Y * width + start.X;
        short currentHeight = heights[startIndex];
        ushort encodedPattern = smooth ? (ushort)1 : (ushort)0;

        switch (mode)
        {
            case TerrainBrushMode.Raise:
            case TerrainBrushMode.Lower:
                if (heightDelta is not { } delta)
                {
                    throw new ContentError("Terrain raise or lower requires a vertical drag delta");
                }

                return MakeRaiseCommand(start, currentHeight, level, delta, smooth);

            case TerrainBrushMode.Level:
                if (end is not { } endTile ||
                    endTile.X < 0 || endTile.X >= width ||
                    endTile.Y < 0 || endTile.Y >= height)
                {
                    throw new ContentError("Terrain flatten requires an end tile inside the lot");
                }

                int left = Math.Min(start.X, endTile.X);
                int top = Math.Min(start.Y, endTile.Y);
                int right = Math.Max(start.X, endTile.X);
                int bottom = Math.Max(start.Y, endTile.Y);
                return new ArchitectureCommandRequest(
                    "TERRAIN_FLATTEN",
                    left,
                    top,
                    level,
                    right - left,
                    bottom - top,
                    encodedPattern,
                    unchecked((ushort)currentHeight));

            case TerrainBrushMode.Grass:
                return new ArchitectureCommandRequest(
                    "GRASS_DOT",
                    start.X,
                    start.Y,
                    level,
                    0,
                    0,
                    unchecked((ushort)grassStep),
                    0);

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    private static ArchitectureCommandRequest MakeRaiseCommand(
        GroundTile vertex,
        int currentHeight,
        int level,
        short heightDelta,
        bool smooth)
    {
        int target = Math.Min(64, Math.Max(0, currentHeight + heightDelta));
        return new ArchitectureCommandRequest(
            "TERRAIN_RAISE",
            vertex.X,
            vertex.Y,
            level,
            0,
            0,
            smooth ? (ushort)1 : (ushort)0,
            unchecked((ushort)(short)target));
    }
}
