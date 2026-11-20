using System.Numerics;

namespace SimsSDL.Core;

public readonly record struct GroundTile(int X, int Y)
{
    public GroundTile(LotVertex vertex) : this(vertex.X, vertex.Y) { }

    public int PlacementX => X * 16 + 8;
    public int PlacementY => Y * 16 + 8;
}

public readonly record struct FloorTileHit(GroundTile Tile, int Half)
{
    public ushort Style => Half == 1 ? (ushort)1 : Half == 4 ? (ushort)2 : (ushort)0;
}

public sealed class GroundTilePicker
{
    private readonly int _width;
    private readonly IReadOnlyList<LotScene.TerrainVertex> _vertices;
    private readonly IReadOnlyList<byte> _diagonals;

    private GroundTilePicker(int width, IReadOnlyList<LotScene.TerrainVertex> vertices, IReadOnlyList<byte> diagonals)
    {
        _width = width;
        _vertices = vertices;
        _diagonals = diagonals;
    }

    public static GroundTilePicker? Create(LotScene scene)
    {
        if (scene.LotWidth is not { } width || scene.LotHeight is not { } height ||
            width < 1 || width > 128 || height < 1 || height > 128)
        {
            return null;
        }

        IReadOnlyList<LotScene.TerrainVertex>? vertices = scene.ConstructionVertices;
        if (vertices is null || vertices.Count != width * height * 6)
        {
            return null;
        }

        IReadOnlyList<byte> diagonals = (IReadOnlyList<byte>?)scene.FloorDiagonals ?? new byte[width * height];
        if (diagonals.Count != width * height)
        {
            return null;
        }

        for (int i = 0; i < diagonals.Count; i++)
        {
            byte d = diagonals[i];
            if (d is not (0 or 16 or 32))
            {
                return null;
            }
        }

        return new GroundTilePicker(width, vertices, diagonals);
    }

    public GroundTile? Tile(Vector2 point) => Hit(point)?.Tile;

    public FloorTileHit? Hit(Vector2 point)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
        {
            return null;
        }

        static Vector2? Coordinates(
            LotScene.TerrainVertex a,
            LotScene.TerrainVertex b,
            LotScene.TerrainVertex c,
            Vector2 p)
        {
            var ab = new Vector2(b.X - a.X, b.Y - a.Y);
            var ac = new Vector2(c.X - a.X, c.Y - a.Y);
            var ap = p - new Vector2(a.X, a.Y);
            float determinant = ab.X * ac.Y - ab.Y * ac.X;
            if (!float.IsFinite(determinant) || MathF.Abs(determinant) <= 0.000001f)
            {
                return null;
            }

            float u = (ap.X * ac.Y - ap.Y * ac.X) / determinant;
            float v = (ab.X * ap.Y - ab.Y * ap.X) / determinant;
            if (!(u >= -0.00001f && v >= -0.00001f && u + v <= 1.00001f))
            {
                return null;
            }

            return new Vector2(u, v);
        }

        for (int index = (_vertices.Count / 6) - 1; index >= 0; index--)
        {
            int start = index * 6;
            Vector2 local;
            if (Coordinates(_vertices[start], _vertices[start + 1], _vertices[start + 2], point) is { } w1)
            {
                local = new Vector2(w1.X + w1.Y, w1.Y);
            }
            else if (Coordinates(_vertices[start + 3], _vertices[start + 4], _vertices[start + 5], point) is { } w2)
            {
                local = new Vector2(1f - w2.X - w2.Y, 1f - w2.Y);
            }
            else
            {
                continue;
            }

            byte diag = _diagonals[index];
            int half = diag == 32
                ? (local.X >= local.Y ? 1 : 2)
                : diag == 16
                    ? (local.X + local.Y <= 1f ? 3 : 4)
                    : 0;
            return new FloorTileHit(new GroundTile(index % _width, index / _width), half);
        }

        return null;
    }
}

/// <summary>Defines the buildable tile mask for the current level. A vertex is buildable only when all
/// adjacent tiles surrounding it are buildable.</summary>
public sealed class BuildableArea : IEquatable<BuildableArea>
{
    private readonly bool[] _tiles;

    public int Width { get; }
    public int Height { get; }

    private BuildableArea(int width, int height, bool[] tiles)
    {
        Width = width;
        Height = height;
        _tiles = tiles;
    }

    public static BuildableArea? Create(int width, int height, IReadOnlyList<bool> tiles)
    {
        if (width <= 0 || height <= 0 || tiles.Count != width * height)
        {
            return null;
        }

        return new BuildableArea(width, height, [.. tiles]);
    }

    public static BuildableArea? Create(IReadOnlyDictionary<string, JsonElement> state, string key = "buildable")
    {
        if (!state.TryGetValue("width", out JsonElement widthEl) ||
            widthEl.ValueKind != JsonValueKind.Number ||
            !widthEl.TryGetInt32(out int width) ||
            !state.TryGetValue("height", out JsonElement heightEl) ||
            heightEl.ValueKind != JsonValueKind.Number ||
            !heightEl.TryGetInt32(out int height) ||
            !state.TryGetValue(key, out JsonElement buildableEl) ||
            buildableEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var tiles = new List<bool>(buildableEl.GetArrayLength());
        foreach (JsonElement item in buildableEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.True)
            {
                tiles.Add(true);
            }
            else if (item.ValueKind == JsonValueKind.False)
            {
                tiles.Add(false);
            }
            else
            {
                return null;
            }
        }

        return Create(width, height, tiles);
    }

    public static BuildableArea? Create(IReadOnlyDictionary<string, object?> state, string key = "buildable")
    {
        if (!TryGetInt(state, "width", out int width) || !TryGetInt(state, "height", out int height))
        {
            return null;
        }

        if (!state.TryGetValue(key, out object? rawTiles))
        {
            return null;
        }

        if (rawTiles is IReadOnlyList<bool> boolList)
        {
            return Create(width, height, boolList);
        }

        if (rawTiles is IEnumerable<bool> boolSeq)
        {
            return Create(width, height, boolSeq.ToArray());
        }

        if (rawTiles is JsonElement el && el.ValueKind == JsonValueKind.Array)
        {
            var tiles = new List<bool>(el.GetArrayLength());
            foreach (JsonElement item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.True)
                {
                    tiles.Add(true);
                }
                else if (item.ValueKind == JsonValueKind.False)
                {
                    tiles.Add(false);
                }
                else
                {
                    return null;
                }
            }

            return Create(width, height, tiles);
        }

        return null;
    }

    private static bool TryGetInt(IReadOnlyDictionary<string, object?> state, string key, out int value)
    {
        if (state.TryGetValue(key, out object? raw))
        {
            if (raw is int i)
            {
                value = i;
                return true;
            }

            if (raw is JsonElement el && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int je))
            {
                value = je;
                return true;
            }
        }

        value = 0;
        return false;
    }

    public bool Contains(int tileX, int y) =>
        tileX >= 0 && y >= 0 && tileX < Width && y < Height && _tiles[y * Width + tileX];

    public bool Contains(GroundTile tile) => Contains(tile.X, tile.Y);

    public bool Contains(LotVertex vertex) =>
        Contains(vertex.X, vertex.Y) &&
        Contains(vertex.X - 1, vertex.Y) &&
        Contains(vertex.X, vertex.Y - 1) &&
        Contains(vertex.X - 1, vertex.Y - 1);

    public bool Equals(BuildableArea? other) =>
        other is not null &&
        Width == other.Width &&
        Height == other.Height &&
        _tiles.AsSpan().SequenceEqual(other._tiles);

    public override bool Equals(object? obj) => Equals(obj as BuildableArea);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Width);
        hash.Add(Height);
        foreach (bool tile in _tiles)
        {
            hash.Add(tile);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(BuildableArea? left, BuildableArea? right) => Equals(left, right);
    public static bool operator !=(BuildableArea? left, BuildableArea? right) => !Equals(left, right);
}

public sealed record FloorBrushRectangle
{
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    public int TileCount => (Width + 1) * (Height + 1);

    private FloorBrushRectangle(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public static FloorBrushRectangle? Create(GroundTile start, GroundTile end) =>
        Create(start.X, start.Y, end.X, end.Y);

    public static FloorBrushRectangle? Create(int startX, int startY, int endX, int endY)
    {
        if ((uint)startX > 127 || (uint)startY > 127 || (uint)endX > 127 || (uint)endY > 127)
        {
            return null;
        }

        return new FloorBrushRectangle(
            Math.Min(startX, endX),
            Math.Min(startY, endY),
            Math.Abs(endX - startX),
            Math.Abs(endY - startY));
    }

    public static Vector2 CornerUV(int corner, int orientation)
    {
        Vector2 baseUv = corner switch
        {
            0 or 5 => new Vector2(0.5f, 0.0f),
            1 => new Vector2(1.0f, 0.5f),
            2 or 3 => new Vector2(0.5f, 1.0f),
            _ => new Vector2(0.0f, 0.5f),
        };

        return (((orientation % 4) + 4) % 4) switch
        {
            0 => baseUv,
            1 => new Vector2(baseUv.Y, 1f - baseUv.X),
            2 => new Vector2(1f - baseUv.X, 1f - baseUv.Y),
            _ => new Vector2(1f - baseUv.Y, baseUv.X),
        };
    }

    public List<LotScene.TerrainVertex> Geometry(LotScene scene, int half = 0, bool water = false) =>
        TexturedGeometry(scene, half, water).ConvertAll(static item => item.Vertex);

    public List<(LotScene.TerrainVertex Vertex, Vector2 UV)> TexturedGeometry(
        LotScene scene,
        int half = 0,
        bool water = false)
    {
        if (scene.LotWidth is not { } columns || scene.LotHeight is not { } rows ||
            columns < 1 || columns > 128 || rows < 1 || rows > 128 ||
            X + Width >= columns || Y + Height >= rows ||
            scene.ConstructionVertices is not { } vertices || vertices.Count != columns * rows * 6)
        {
            throw new ContentError("Floor brush exceeds the terrain grid");
        }

        IReadOnlyList<byte>? diagonals = scene.FloorDiagonals;
        if (diagonals is not null)
        {
            if (diagonals.Count != columns * rows)
            {
                throw new ContentError("Invalid diagonal floor grid");
            }

            for (int i = 0; i < diagonals.Count; i++)
            {
                byte d = diagonals[i];
                if (d is not (0 or 16 or 32))
                {
                    throw new ContentError("Invalid diagonal floor grid");
                }
            }
        }

        if (half < 0 || half > 4)
        {
            throw new ContentError("Invalid floor brush half");
        }

        int orientation = scene.CameraOrientation ?? 0;
        var result = new List<(LotScene.TerrainVertex Vertex, Vector2 UV)>(TileCount * 6);
        for (int row = Y; row <= Y + Height; row++)
        {
            for (int column = X; column <= X + Width; column++)
            {
                int start = (row * columns + column) * 6;
                byte diagonal = diagonals is not null ? diagonals[row * columns + column] : (byte)0;
                if (diagonal != 0 && (water ? TileCount == 1 : TileCount > 1))
                {
                    continue;
                }

                int[] corners;
                if (TileCount == 1 && diagonal != 0 && !water)
                {
                    if (!((diagonal == 32 && (half == 1 || half == 2)) ||
                          (diagonal == 16 && (half == 3 || half == 4))))
                    {
                        throw new ContentError("Floor brush half does not match diagonal");
                    }

                    corners = half switch
                    {
                        1 => new[] { 0, 1, 2 },
                        2 => new[] { 3, 4, 5 },
                        3 => new[] { 0, 1, 4 },
                        _ => new[] { 1, 2, 4 },
                    };
                }
                else
                {
                    corners = new[] { 0, 1, 2, 3, 4, 5 };
                }

                foreach (int corner in corners)
                {
                    result.Add((vertices[start + corner], CornerUV(corner, orientation)));
                }
            }
        }

        return result;
    }
}

public sealed record FloorBrushSelection
{
    private readonly record struct RoomNode(int X, int Y, int Half);

    public FloorBrushRectangle Rectangle { get; }
    public int Half { get; }
    public bool Water { get; }
    public bool RoomFill { get; }

    public ushort Style => Half == 1 ? (ushort)1 : Half == 4 ? (ushort)2 : (ushort)0;

    private FloorBrushSelection(FloorBrushRectangle rectangle, int half, bool water, bool roomFill)
    {
        Rectangle = rectangle;
        Half = half;
        Water = water;
        RoomFill = roomFill;
    }

    public static FloorBrushSelection? Create(
        FloorBrushRectangle rectangle,
        int half,
        bool water = false,
        bool roomFill = false)
    {
        if (half < 0 || half > 4)
        {
            return null;
        }

        bool resolvedRoomFill = roomFill && rectangle.TileCount == 1 && !water;
        int resolvedHalf = rectangle.TileCount == 1 && !water ? half : 0;
        return new FloorBrushSelection(rectangle, resolvedHalf, water, resolvedRoomFill);
    }

    public List<LotScene.TerrainVertex> Geometry(LotScene scene) =>
        TexturedGeometry(scene).ConvertAll(static item => item.Vertex);

    public List<(LotScene.TerrainVertex Vertex, Vector2 UV)> TexturedGeometry(LotScene scene)
    {
        if (RoomFill)
        {
            return RoomGeometry(scene, Rectangle.X, Rectangle.Y, Half);
        }

        return Rectangle.TexturedGeometry(scene, Half, Water);
    }

    private static List<(LotScene.TerrainVertex Vertex, Vector2 UV)> RoomGeometry(
        LotScene scene,
        int startX,
        int startY,
        int startHalf)
    {
        if (scene.LotWidth is not { } columns || scene.LotHeight is not { } rows ||
            columns < 1 || columns > 128 || rows < 1 || rows > 128 ||
            startX < 1 || startY < 1 || startX >= columns - 1 || startY >= rows - 1 ||
            scene.ConstructionVertices is not { } vertices || vertices.Count != columns * rows * 6)
        {
            return FloorBrushRectangle.Create(startX, startY, startX, startY)
                ?.TexturedGeometry(scene, startHalf, water: false)
                ?? new List<(LotScene.TerrainVertex Vertex, Vector2 UV)>();
        }

        int level = scene.SelectedLevel;
        IReadOnlyList<byte>? floorDiagonals = scene.FloorDiagonals;

        byte Diagonal(int x, int y)
        {
            if (floorDiagonals is null)
            {
                return 0;
            }

            int idx = y * columns + x;
            return (uint)idx < (uint)floorDiagonals.Count ? floorDiagonals[idx] : (byte)0;
        }

        byte startDiag = Diagonal(startX, startY);
        int resolvedStartHalf = startDiag == 32
            ? (startHalf is 1 or 2 ? startHalf : 2)
            : startDiag == 16
                ? (startHalf is 3 or 4 ? startHalf : 3)
                : 0;

        static int[] Corners(int half) => half switch
        {
            1 => new[] { 0, 1, 2 },
            2 => new[] { 3, 4, 5 },
            3 => new[] { 0, 1, 4 },
            4 => new[] { 1, 2, 4 },
            _ => new[] { 0, 1, 2, 3, 4, 5 },
        };

        var roomNodes = new List<RoomNode>();
        if (scene.Meshes is { } meshes)
        {
            int? targetRoom = null;
            foreach (LotScene.Mesh mesh in meshes)
            {
                if (mesh.Floor is not { } floor ||
                    (floor.Level ?? mesh.Level ?? 1) != level ||
                    floor.X != startX || floor.Y != startY)
                {
                    continue;
                }

                if (resolvedStartHalf == 0 || floor.Half == resolvedStartHalf || floor.Half == 0)
                {
                    targetRoom = mesh.Room;
                    break;
                }
            }

            if (targetRoom is { } room && room != 0xFF && room != 0xFFFF)
            {
                var seen = new HashSet<RoomNode>();
                foreach (LotScene.Mesh mesh in meshes)
                {
                    if (mesh.Floor is not { } floor ||
                        (floor.Level ?? mesh.Level ?? 1) != level ||
                        mesh.Room != room ||
                        floor.X < 1 || floor.Y < 1 || floor.X >= columns - 1 || floor.Y >= rows - 1)
                    {
                        continue;
                    }

                    var node = new RoomNode(floor.X, floor.Y, floor.Half);
                    if (seen.Add(node))
                    {
                        roomNodes.Add(node);
                    }
                }
            }
        }

        byte[] blocked = new byte[columns * rows];
        IReadOnlyList<LotScene.Sprite> sprites = scene.Sprites;
        foreach (LotScene.Sprite sprite in sprites)
        {
            if (sprite.Wall is not { } wall || wall.Level != level || wall.Direction < 0 || wall.Direction > 3)
            {
                continue;
            }

            if (wall.X >= 0 && wall.X < columns && wall.Y >= 0 && wall.Y < rows)
            {
                blocked[wall.Y * columns + wall.X] |= (byte)(1 << wall.Direction);
            }

            int nx = wall.X + (wall.Direction == 0 ? -1 : wall.Direction == 2 ? 1 : 0);
            int ny = wall.Y + (wall.Direction == 1 ? -1 : wall.Direction == 3 ? 1 : 0);
            int opp = (wall.Direction + 2) % 4;
            if (nx >= 0 && nx < columns && ny >= 0 && ny < rows)
            {
                blocked[ny * columns + nx] |= (byte)(1 << opp);
            }
        }

        static ReadOnlySpan<int> AllowedDirections(byte diagonal, int half)
        {
            switch (diagonal, half)
            {
                case (32, 1): return [1, 2];
                case (32, 2): return [0, 3];
                case (16, 3): return [0, 1];
                case (16, 4): return [2, 3];
                case (0, _): return [0, 1, 2, 3];
                default: return [];
            }
        }

        static int HalfForIncoming(byte diagonal, int incomingDirection) => diagonal switch
        {
            32 => (incomingDirection == 1 || incomingDirection == 2) ? 1 : 2,
            16 => (incomingDirection == 0 || incomingDirection == 1) ? 3 : 4,
            _ => 0,
        };

        var visited = new HashSet<RoomNode>();
        var startNode = new RoomNode(startX, startY, resolvedStartHalf);
        var queue = new List<RoomNode> { startNode };
        visited.Add(startNode);
        int head = 0;
        bool touchedPerimeter = false;
        ReadOnlySpan<(int Dx, int Dy)> offsets = [(-1, 0), (0, -1), (1, 0), (0, 1)];

        while (head < queue.Count)
        {
            RoomNode current = queue[head++];
            if (current.X == 1 || current.Y == 1 || current.X == columns - 2 || current.Y == rows - 2)
            {
                touchedPerimeter = true;
            }

            byte diag = Diagonal(current.X, current.Y);
            byte cellBlocked = blocked[current.Y * columns + current.X];
            foreach (int dir in AllowedDirections(diag, current.Half))
            {
                if ((cellBlocked & (byte)(1 << dir)) != 0)
                {
                    continue;
                }

                int nx = current.X + offsets[dir].Dx;
                int ny = current.Y + offsets[dir].Dy;
                if (nx < 1 || ny < 1 || nx >= columns - 1 || ny >= rows - 1)
                {
                    continue;
                }

                int opp = (dir + 2) % 4;
                if ((blocked[ny * columns + nx] & (byte)(1 << opp)) != 0)
                {
                    continue;
                }

                byte nDiag = Diagonal(nx, ny);
                int nHalf = HalfForIncoming(nDiag, opp);
                var next = new RoomNode(nx, ny, nHalf);
                if (visited.Add(next))
                {
                    queue.Add(next);
                }
            }
        }

        List<RoomNode> finalNodes;
        if (roomNodes.Count > 0 && touchedPerimeter)
        {
            finalNodes = roomNodes;
        }
        else if (roomNodes.Count > 0)
        {
            foreach (RoomNode node in roomNodes)
            {
                if (visited.Add(node))
                {
                    queue.Add(node);
                }
            }

            finalNodes = queue;
        }
        else
        {
            finalNodes = queue;
        }

        int orientation = scene.CameraOrientation ?? 0;
        var result = new List<(LotScene.TerrainVertex Vertex, Vector2 UV)>(finalNodes.Count * 6);
        foreach (RoomNode node in finalNodes)
        {
            int start = (node.Y * columns + node.X) * 6;
            foreach (int corner in Corners(node.Half))
            {
                result.Add((vertices[start + corner], FloorBrushRectangle.CornerUV(corner, orientation)));
            }
        }

        return result;
    }
}

public readonly record struct LotVertex(int X, int Y);

public sealed record WallBrushLine
{
    private static readonly (int Dx, int Dy)[] Steps =
    [
        (1, 0), (1, 1), (0, 1), (-1, 1),
        (-1, 0), (-1, -1), (0, -1), (1, -1),
    ];

    public LotVertex Start { get; }
    public LotVertex End { get; }
    public int Length { get; }
    public int Direction { get; }

    private WallBrushLine(LotVertex start, LotVertex end, int length, int direction)
    {
        Start = start;
        End = end;
        Length = length;
        Direction = direction;
    }

    public static WallBrushLine? Create(LotVertex start, LotVertex end)
    {
        if ((uint)start.X > 128 || (uint)start.Y > 128 || (uint)end.X > 128 || (uint)end.Y > 128)
        {
            return null;
        }

        int dx = end.X - start.X;
        int dy = end.Y - start.Y;
        if (dx == 0 && dy == 0)
        {
            return null;
        }

        int direction = ((int)Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4.0), MidpointRounding.AwayFromZero) + 8) % 8;
        int length = Math.Max(Math.Abs(dx), Math.Abs(dy));
        (int stepX, int stepY) = Steps[direction];
        var snappedEnd = new LotVertex(start.X + stepX * length, start.Y + stepY * length);
        if ((uint)snappedEnd.X > 128 || (uint)snappedEnd.Y > 128)
        {
            return null;
        }

        return new WallBrushLine(start, snappedEnd, length, direction);
    }

    public static Vector2? Point(LotVertex vertex, LotScene scene)
    {
        if (scene.LotWidth is not { } width || scene.LotHeight is not { } height ||
            width < 1 || width > 128 || height < 1 || height > 128 ||
            vertex.X < 0 || vertex.X > width || vertex.Y < 0 || vertex.Y > height ||
            scene.ConstructionVertices is not { } vertices || vertices.Count != width * height * 6)
        {
            return null;
        }

        int x = Math.Min(vertex.X, width - 1);
        int y = Math.Min(vertex.Y, height - 1);
        int corner = vertex.X == x ? (vertex.Y == y ? 0 : 4) : (vertex.Y == y ? 1 : 2);
        LotScene.TerrainVertex value = vertices[(y * width + x) * 6 + corner];
        return new Vector2(value.X, value.Y);
    }

    public List<LotScene.TerrainVertex> Geometry(LotScene scene)
    {
        (int stepX, int stepY) = Steps[Direction];
        var result = new List<LotScene.TerrainVertex>(Length * 6);
        var lift = new Vector2(0, 112);

        for (int offset = 0; offset < Length; offset++)
        {
            var a = new LotVertex(Start.X + stepX * offset, Start.Y + stepY * offset);
            var b = new LotVertex(a.X + stepX, a.Y + stepY);
            if (Point(a, scene) is not { } first || Point(b, scene) is not { } last)
            {
                throw new ContentError("Wall brush exceeds terrain grid");
            }

            Vector2 liftedLast = last - lift;
            Vector2 liftedFirst = first - lift;
            result.Add(new LotScene.TerrainVertex(first.X, first.Y, 0, 0, 0));
            result.Add(new LotScene.TerrainVertex(last.X, last.Y, 0, 0, 0));
            result.Add(new LotScene.TerrainVertex(liftedLast.X, liftedLast.Y, 0, 0, 0));
            result.Add(new LotScene.TerrainVertex(liftedLast.X, liftedLast.Y, 0, 0, 0));
            result.Add(new LotScene.TerrainVertex(liftedFirst.X, liftedFirst.Y, 0, 0, 0));
            result.Add(new LotScene.TerrainVertex(first.X, first.Y, 0, 0, 0));
        }

        return result;
    }
}

public sealed class LotVertexPicker
{
    private readonly LotScene _scene;
    private readonly GroundTilePicker _tiles;

    private LotVertexPicker(LotScene scene, GroundTilePicker tiles)
    {
        _scene = scene;
        _tiles = tiles;
    }

    public static LotVertexPicker? Create(LotScene scene)
    {
        if (GroundTilePicker.Create(scene) is not { } tiles)
        {
            return null;
        }

        return new LotVertexPicker(scene, tiles);
    }

    public LotVertex? Vertex(Vector2 point)
    {
        if (_tiles.Tile(point) is not { } tile)
        {
            return null;
        }

        ReadOnlySpan<LotVertex> candidates =
        [
            new LotVertex(tile.X, tile.Y),
            new LotVertex(tile.X + 1, tile.Y),
            new LotVertex(tile.X + 1, tile.Y + 1),
            new LotVertex(tile.X, tile.Y + 1),
        ];

        LotVertex? best = null;
        float bestDistSq = float.PositiveInfinity;

        foreach (LotVertex candidate in candidates)
        {
            if (WallBrushLine.Point(candidate, _scene) is not { } candidatePoint)
            {
                continue;
            }

            Vector2 delta = candidatePoint - point;
            float distSq = delta.X * delta.X + delta.Y * delta.Y;
            if (best is null || distSq < bestDistSq)
            {
                best = candidate;
                bestDistSq = distSq;
            }
        }

        return best;
    }
}

public sealed record WallBrushRectangle
{
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    public int SegmentCount => 2 * (Width + Height);

    private WallBrushRectangle(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public static WallBrushRectangle? Create(LotVertex start, LotVertex end)
    {
        if ((uint)start.X > 128 || (uint)start.Y > 128 || (uint)end.X > 128 || (uint)end.Y > 128 ||
            start.X == end.X || start.Y == end.Y)
        {
            return null;
        }

        return new WallBrushRectangle(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X),
            Math.Abs(end.Y - start.Y));
    }

    public List<WallBrushLine> Lines
    {
        get
        {
            (LotVertex A, LotVertex B)[] pairs =
            [
                (new LotVertex(X, Y), new LotVertex(X + Width, Y)),
                (new LotVertex(X, Y), new LotVertex(X, Y + Height)),
                (new LotVertex(X, Y + Height), new LotVertex(X + Width, Y + Height)),
                (new LotVertex(X + Width, Y), new LotVertex(X + Width, Y + Height)),
            ];

            var lines = new List<WallBrushLine>(4);
            foreach ((LotVertex a, LotVertex b) in pairs)
            {
                if (WallBrushLine.Create(a, b) is { } line)
                {
                    lines.Add(line);
                }
            }

            return lines;
        }
    }

    public List<LotScene.TerrainVertex> Geometry(LotScene scene)
    {
        var result = new List<LotScene.TerrainVertex>();
        foreach (WallBrushLine line in Lines)
        {
            result.AddRange(line.Geometry(scene));
        }

        return result;
    }
}

public sealed record WallBrushSelection
{
    public WallBrushLine? Line { get; }
    public WallBrushRectangle? Rectangle { get; }
    public bool Deleting { get; }

    public bool IsRectangle => Rectangle is not null;

    private WallBrushSelection(WallBrushLine? line, WallBrushRectangle? rectangle, bool deleting)
    {
        Line = line;
        Rectangle = rectangle;
        Deleting = deleting;
    }

    public static WallBrushSelection? Create(LotVertex start, LotVertex end, bool rectangle, bool deleting)
    {
        if (rectangle && !deleting)
        {
            if (WallBrushRectangle.Create(start, end) is not { } area)
            {
                return null;
            }

            return new WallBrushSelection(null, area, false);
        }

        if (WallBrushLine.Create(start, end) is not { } line)
        {
            return null;
        }

        return new WallBrushSelection(line, null, deleting);
    }

    public string Command => Rectangle is not null ? "WALL_RECT" : (Deleting ? "WALL_ERASE" : "WALL_LINE");

    public (int X, int Y, int X2, int Y2) Arguments => Line is { } line
        ? (line.Start.X, line.Start.Y, line.End.X, line.End.Y)
        : Rectangle is { } area
            ? (area.X, area.Y, area.Width, area.Height)
            : throw new InvalidOperationException();

    public List<LotScene.TerrainVertex> Geometry(LotScene scene) => Line is { } line
        ? line.Geometry(scene)
        : Rectangle is { } area
            ? area.Geometry(scene)
            : throw new InvalidOperationException();
}

public sealed record BuildRoomFill
{
    public string Command { get; }
    public int X { get; }
    public int Y { get; }
    public int Level { get; }
    public ushort Pattern { get; }

    /// <summary>Specifies the target half-tile quadrant for diagonal floor fills, where zero indicates the
    /// entire tile center.</summary>
    public int Half { get; }

    private BuildRoomFill(string command, int x, int y, int level, ushort pattern, int half)
    {
        Command = command;
        X = x;
        Y = y;
        Level = level;
        Pattern = pattern;
        Half = half;
    }

    public static BuildRoomFill? Create(
        BuildCatalogPage.CatalogKind kind,
        ushort pattern,
        GroundTile? tile,
        LotScene.WallSide? wall,
        int half = 0,
        bool removing = false,
        int selectedLevel = 1)
    {
        if ((kind != BuildCatalogPage.CatalogKind.Floor && kind != BuildCatalogPage.CatalogKind.Wall) ||
            (!removing && pattern == 0) ||
            pattern >= 65534)
        {
            return null;
        }

        int x;
        int y;
        int level;
        if (kind == BuildCatalogPage.CatalogKind.Wall && wall is { } w)
        {
            x = w.X;
            y = w.Y;
            level = w.Level;
        }
        else if (tile is { } t)
        {
            x = t.X;
            y = t.Y;
            level = selectedLevel;
        }
        else
        {
            return null;
        }

        if ((uint)x > 127 || (uint)y > 127 || level < 1 || level > 5)
        {
            return null;
        }

        ushort resolvedPattern = removing ? (ushort)0 : pattern;
        int resolvedHalf = kind == BuildCatalogPage.CatalogKind.Floor ? half : (wall?.Side ?? 0);
        string command = kind == BuildCatalogPage.CatalogKind.Floor
            ? (removing ? "FLOOR_FILL_ERASE" : "FLOOR_FILL")
            : (removing ? "PATTERN_FILL_ERASE" : "PATTERN_FILL");
        return new BuildRoomFill(command, x, y, level, resolvedPattern, resolvedHalf);
    }
}
