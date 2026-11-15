using System.Numerics;

namespace SimsSDL.Core;

public readonly record struct SceneDrawItem
{
    public enum ItemKind : byte
    {
        Sprite,
        Mesh
    }

    public SceneDrawItem(ItemKind kind, int index)
    {
        Kind = kind;
        Index = index;
    }

    public ItemKind Kind { get; }
    public int Index { get; }

    public bool IsSprite => Kind == ItemKind.Sprite;
    public bool IsMesh => Kind == ItemKind.Mesh;

    public static SceneDrawItem Sprite(int index) => new(ItemKind.Sprite, index);
    public static SceneDrawItem Mesh(int index) => new(ItemKind.Mesh, index);

    public static List<SceneDrawItem> Ordered(LotScene scene)
    {
        int spriteCount = scene.Sprites.Count;
        int meshCount = scene.Meshes?.Count ?? 0;
        var entries = new List<(int Level, int Layer, float Depth, int Ordinal, SceneDrawItem Item)>(spriteCount + meshCount);

        for (int i = 0; i < spriteCount; i++)
        {
            var s = scene.Sprites[i];
            entries.Add((s.Level ?? 1, s.Layer ?? 0, s.Depth ?? i, i, Sprite(i)));
        }

        if (scene.Meshes is { } meshes)
        {
            for (int i = 0; i < meshes.Count; i++)
            {
                var m = meshes[i];
                entries.Add((m.Level ?? 1, m.Layer ?? 1, m.Depth, spriteCount + i, Mesh(i)));
            }
        }

        entries.Sort(static (a, b) =>
        {
            int cmp = a.Level.CompareTo(b.Level);
            if (cmp != 0) return cmp;
            cmp = a.Layer.CompareTo(b.Layer);
            if (cmp != 0) return cmp;
            cmp = a.Depth.CompareTo(b.Depth);
            if (cmp != 0) return cmp;
            return a.Ordinal.CompareTo(b.Ordinal);
        });

        var result = new List<SceneDrawItem>(entries.Count);
        foreach (var entry in entries)
            result.Add(entry.Item);
        return result;
    }
}

/// <summary>
/// The three zoom levels of the original lot view. Scene units are medium-zoom pixels, so near draws every scene unit
/// as two screen pixels and far as half of one; the art for each level is drawn at its own resolution, which keeps
/// sprites sharp instead of magnifying the medium art.
/// </summary>
public enum LotZoom
{
    Far = 1,
    Medium = 2,
    Near = 3
}

public static class LotZoomExtensions
{
    /// <summary>
    /// CPState::__ct__ initializes the original control panel's zoom field to 1 (far).
    /// </summary>
    public const LotZoom GameplayDefault = LotZoom.Far;

    /// <summary>
    /// Screen pixels per scene unit at a UI scale of 1.
    /// </summary>
    public static float Factor(this LotZoom zoom) => zoom switch
    {
        LotZoom.Far => 0.5f,
        LotZoom.Medium => 1f,
        LotZoom.Near => 2f,
        _ => 1f
    };

    /// <summary>
    /// Zoom-aware lot sounds divide volume by 4 - zoom: near 1, medium 1/2, far 1/3.
    /// </summary>
    public static float SoundGain(this LotZoom zoom) => 1f / (4 - (int)zoom);

    /// <summary>
    /// One step closer (positive) or farther (negative), stopping at near and far.
    /// </summary>
    public static LotZoom Stepped(this LotZoom zoom, int steps) =>
        (LotZoom)Math.Clamp((int)zoom + steps, (int)LotZoom.Far, (int)LotZoom.Near);
}

public readonly record struct SceneViewport
{
    public Vector2 Size { get; }
    public float Scale { get; }
    public Vector2 Origin { get; }

    /// <summary>
    /// Lays the scene out at an exact zoom factor with a whole-pixel origin, so at near and medium every texel lands on
    /// whole screen pixels and nearest sampling stays crisp. <paramref name="pan"/> moves the scene centre away from
    /// the view centre.
    /// </summary>
    public SceneViewport(LotScene scene, Vector2 canvas, float panelHeight, LotZoom zoom, float uiScale, Vector2 pan)
    {
        Size = new Vector2(canvas.X, MathF.Max(1f, canvas.Y - panelHeight));
        Scale = MathF.Max(uiScale, 0.01f) * zoom.Factor();
        Vector2 clamped = ClampedPan(pan, scene, Scale);
        Vector2 rawOrigin = (Size - new Vector2(scene.Width, scene.Height) * Scale) / 2f + clamped;
        Origin = new Vector2(
            MathF.Round(rawOrigin.X, MidpointRounding.ToEven),
            MathF.Round(rawOrigin.Y, MidpointRounding.ToEven));
    }

    /// <summary>
    /// Keeps the view centre inside the lot.
    /// </summary>
    public static Vector2 ClampedPan(Vector2 pan, LotScene scene, float scale)
    {
        if (scene.LotWidth is int width && scene.LotHeight is int height && width > 2 && height > 2
            && scene.OriginX is float x && scene.OriginY is float y && scale > 0f)
        {
            Vector2 size = new(width, height);
            Vector2 origin = new(x, y);
            Vector2 centre = new Vector2(scene.Width, scene.Height) / 2f;
            Vector2 projected = centre - pan / scale + origin - new Vector2(34f, 158f);
            Vector2 camera = new(projected.Y / 32f + projected.X / 64f, projected.Y / 32f - projected.X / 64f);
            Vector2 source = Vector2.Clamp(scene.Camera.LotPoint(camera, size), Vector2.One, size - Vector2.One);
            return (centre - (scene.Camera.ProjectedPoint(source, size) - origin)) * scale;
        }

        Vector2 limit = new Vector2(scene.Width, scene.Height) * scale / 2f;
        return Vector2.Clamp(pan, -limit, limit);
    }

    /// <summary>
    /// The pan that keeps the same scene point at the view centre after the scale changes.
    /// </summary>
    public static Vector2 RescaledPan(Vector2 pan, float oldScale, float newScale)
    {
        if (oldScale <= 0f || newScale <= 0f)
            return pan;
        return pan * (newScale / oldScale);
    }

    public Vector2? ScenePoint(Vector2 point)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)
            || point.X < 0f || point.Y < 0f || point.X >= Size.X || point.Y >= Size.Y
            || !float.IsFinite(Scale) || Scale <= 0f)
        {
            return null;
        }

        return (point - Origin) / Scale;
    }
}

public sealed class ScenePicker
{
    private readonly LotScene _scene;
    private readonly IReadOnlyList<byte[]> _pixels;
    private readonly IReadOnlyList<SceneDrawItem> _items;
    private readonly IReadOnlyList<Vector4> _meshBounds;

    public static Vector4[] ComputeMeshBounds(IReadOnlyList<LotScene.Mesh> meshes)
    {
        if (meshes.Count == 0)
            return [];

        var bounds = new Vector4[meshes.Count];
        for (int i = 0; i < meshes.Count; i++)
        {
            var vertices = meshes[i].Vertices;
            if (vertices.Count == 0)
            {
                bounds[i] = new Vector4(float.MaxValue, float.MaxValue, -float.MaxValue, -float.MaxValue);
                continue;
            }

            float minX = vertices[0].X;
            float minY = vertices[0].Y;
            float maxX = minX;
            float maxY = minY;

            for (int v = 1; v < vertices.Count; v++)
            {
                float vx = vertices[v].X;
                float vy = vertices[v].Y;
                if (vx < minX) minX = vx;
                else if (vx > maxX) maxX = vx;
                if (vy < minY) minY = vy;
                else if (vy > maxY) maxY = vy;
            }

            bounds[i] = new Vector4(minX, minY, maxX, maxY);
        }

        return bounds;
    }

    public ScenePicker(
        LotScene scene,
        IReadOnlyList<byte[]> pixels,
        IReadOnlyList<SceneDrawItem>? items = null,
        IReadOnlyList<Vector4>? precomputedMeshBounds = null)
    {
        _scene = scene;
        _pixels = pixels;
        _items = items ?? SceneDrawItem.Ordered(scene);
        if (precomputedMeshBounds is not null)
            _meshBounds = precomputedMeshBounds;
        else if (scene.Meshes is { Count: > 0 } meshes)
            _meshBounds = ComputeMeshBounds(meshes);
        else
            _meshBounds = [];
    }

    public LotCamera Camera => _scene.Camera;

    private float Alpha(int texture, Vector2 uv, bool repeating = false)
    {
        var info = _scene.Textures[texture];
        byte[] data = _pixels[texture];
        Vector2 coordinates = repeating
            ? new Vector2(uv.X - MathF.Floor(uv.X), uv.Y - MathF.Floor(uv.Y))
            : new Vector2(Math.Clamp(uv.X, 0f, 1f), Math.Clamp(uv.Y, 0f, 1f));

        Vector2 p = new(coordinates.X * info.Width - 0.5f, coordinates.Y * info.Height - 0.5f);
        int x = (int)MathF.Floor(p.X);
        int y = (int)MathF.Floor(p.Y);
        Vector2 fraction = p - new Vector2(x, y);

        float Sample(int sx, int sy)
        {
            int column = repeating ? (sx % info.Width + info.Width) % info.Width : Math.Clamp(sx, 0, info.Width - 1);
            int row = repeating ? (sy % info.Height + info.Height) % info.Height : Math.Clamp(sy, 0, info.Height - 1);
            int index = (row * info.Width + column) * 4 + 3;
            return data[index] / 255f;
        }

        float top = Sample(x, y) * (1f - fraction.X) + Sample(x + 1, y) * fraction.X;
        float bottom = Sample(x, y + 1) * (1f - fraction.X) + Sample(x + 1, y + 1) * fraction.X;
        return top * (1f - fraction.Y) + bottom * fraction.Y;
    }

    private bool IsInsideWallSpan(int texture, Vector2 uv)
    {
        var info = _scene.Textures[texture];
        if (info.Width < 2 || info.Height < 2)
            return false;

        byte[] data = _pixels[texture];

        (float Top, float Bottom)? ColumnSpan(int col)
        {
            int first = -1;
            int last = -1;
            for (int row = 0; row < info.Height; row++)
            {
                if (data[(row * info.Width + col) * 4 + 3] >= 64)
                {
                    if (first < 0)
                        first = row;
                    last = row;
                }
            }

            return first >= 0 ? (first, last + 1) : null;
        }

        if (ColumnSpan(Math.Min(1, info.Width - 1)) is not { } left
            || ColumnSpan(Math.Max(0, info.Width - 2)) is not { } right)
        {
            return false;
        }

        float t = Math.Clamp(uv.X, 0f, 1f);
        float top = left.Top * (1f - t) + right.Top * t;
        float bottom = left.Bottom * (1f - t) + right.Bottom * t;
        float py = uv.Y * info.Height;
        return py >= top && py <= bottom;
    }

    private readonly record struct HitResult(bool IsWall, int ObjectID, LotScene.WallSide? WallSide)
    {
        public static HitResult Object(int id) => new(false, id, null);
        public static HitResult Wall(LotScene.WallSide wall) => new(true, 0, wall);
    }

    public int? ObjectAt(Vector2 point, LotZoom zoom = LotZoom.Medium) =>
        Hit(point, zoom) is { IsWall: false } hit ? hit.ObjectID : null;

    public LotScene.WallSide? WallAt(Vector2 point, LotZoom zoom = LotZoom.Medium)
    {
        if (Hit(point, zoom) is { IsWall: true, WallSide: { } hitWall })
            return hitWall;

        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)
            || point.X < 0f || point.Y < 0f
            || point.X >= _scene.Width || point.Y >= _scene.Height)
        {
            return null;
        }

        float storedDepth = 1f;
        LotScene.WallSide? visibleWall = null;

        foreach (var item in _items)
        {
            if (!item.IsSprite)
                continue;

            var sprite = _scene.Sprites[item.Index];
            if (sprite.Wall is not { } wall)
                continue;

            var image = sprite.Image(zoom);
            int? depthTexture = image is null ? sprite.DepthTexture : image.DepthTexture;
            if (depthTexture is not int texture || sprite.DepthNear is not float near || sprite.DepthFar is not float far)
                continue;

            Vector2 origin = new(sprite.X + (image?.X ?? 0f), sprite.Y + (image?.Y ?? 0f));
            Vector2 size = new(image?.Width ?? sprite.Width, image?.Height ?? sprite.Height);
            int colorTexture = image?.Texture ?? sprite.Texture;
            Vector2 uv = (point - origin) / size;
            if (uv.X < 0f || uv.Y < 0f || uv.X >= 1f || uv.Y >= 1f)
                continue;

            if (sprite.Flip)
                uv.X = 1f - uv.X;

            float opacity = Alpha(colorTexture, uv);
            if (opacity < 0.25f && !IsInsideWallSpan(colorTexture, uv))
                continue;

            var info = _scene.Textures[texture];
            int x = Math.Min(info.Width - 1, (int)(uv.X * info.Width));
            int y = Math.Min(info.Height - 1, (int)(uv.Y * info.Height));
            byte raw = _pixels[texture][y * info.Width + x];
            float depth = raw != 255
                ? near + (far - near) * raw / 254f
                : near + (far - near) * uv.X;

            if (depth > storedDepth)
                continue;

            storedDepth = depth;
            visibleWall = wall;
        }

        return visibleWall;
    }

    private HitResult? Hit(Vector2 point, LotZoom zoom)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)
            || point.X < 0f || point.Y < 0f
            || point.X >= _scene.Width || point.Y >= _scene.Height)
        {
            return null;
        }

        float storedDepth = 1f;
        HitResult? visibleHit = null;

        foreach (var item in _items)
        {
            if (item.IsSprite)
            {
                var sprite = _scene.Sprites[item.Index];
                var image = sprite.Image(zoom);
                Vector2 origin = new(sprite.X + (image?.X ?? 0f), sprite.Y + (image?.Y ?? 0f));
                Vector2 size = new(image?.Width ?? sprite.Width, image?.Height ?? sprite.Height);
                int colorTexture = image?.Texture ?? sprite.Texture;
                int? depthTexture = image is null ? sprite.DepthTexture : image.DepthTexture;
                Vector2 uv = (point - origin) / size;
                if (uv.X < 0f || uv.Y < 0f || uv.X >= 1f || uv.Y >= 1f)
                    continue;

                if (sprite.Flip)
                    uv.X = 1f - uv.X;

                float opacity = Alpha(colorTexture, uv);
                if (depthTexture is int texture && sprite.DepthNear is float near && sprite.DepthFar is float far)
                {
                    if (opacity < 0.25f)
                        continue;

                    var info = _scene.Textures[texture];
                    int x = Math.Min(info.Width - 1, (int)(uv.X * info.Width));
                    int y = Math.Min(info.Height - 1, (int)(uv.Y * info.Height));
                    byte raw = _pixels[texture][y * info.Width + x];
                    if (raw == 255)
                        continue;

                    float depth = near + (far - near) * raw / 254f;
                    if (depth > storedDepth)
                        continue;

                    storedDepth = depth;
                }
                else if (opacity < 0.01f)
                {
                    continue;
                }

                if (sprite.Wall is { } wall)
                    visibleHit = HitResult.Wall(wall);
                else
                {
                    int id = sprite.ObjectID ?? 0;
                    visibleHit = id == 0 ? null : HitResult.Object(id);
                }
            }
            else
            {
                if (_scene.Meshes is null || item.Index >= _scene.Meshes.Count)
                    continue;

                var mesh = _scene.Meshes[item.Index];
                if (item.Index < _meshBounds.Count)
                {
                    Vector4 bounds = _meshBounds[item.Index];
                    if (point.X < bounds.X || point.Y < bounds.Y || point.X > bounds.Z || point.Y > bounds.W)
                        continue;
                }

                var vertices = mesh.Vertices;
                for (int offset = 0; offset + 2 < vertices.Count; offset += 3)
                {
                    var a = vertices[offset];
                    var b = vertices[offset + 1];
                    var c = vertices[offset + 2];
                    Vector2 ab = new(b.X - a.X, b.Y - a.Y);
                    Vector2 ac = new(c.X - a.X, c.Y - a.Y);
                    Vector2 relative = point - new Vector2(a.X, a.Y);
                    float determinant = ab.X * ac.Y - ab.Y * ac.X;
                    if (MathF.Abs(determinant) <= 0.000001f)
                        continue;

                    float weightB = (relative.X * ac.Y - relative.Y * ac.X) / determinant;
                    float weightC = (ab.X * relative.Y - ab.Y * relative.X) / determinant;
                    float weightA = 1f - weightB - weightC;
                    if (weightA < 0f || weightB < 0f || weightC < 0f)
                        continue;

                    float depth = a.Z * weightA + b.Z * weightB + c.Z * weightC;
                    bool usesDepth = mesh.Unlit != true || mesh.Floor?.Water is not null || (mesh.Level ?? 1) > 1;
                    if (usesDepth && depth > storedDepth)
                        continue;

                    Vector2 uv = new Vector2(a.U, a.V) * weightA + new Vector2(b.U, b.V) * weightB + new Vector2(c.U, c.V) * weightC;
                    if (Alpha(mesh.TextureFor(zoom), uv, repeating: mesh.Roof is not null) < 0.01f)
                        continue;

                    if (usesDepth)
                        storedDepth = depth;
                    visibleHit = mesh.ObjectID == 0 ? null : HitResult.Object(mesh.ObjectID);
                }
            }
        }

        return visibleHit;
    }
}
