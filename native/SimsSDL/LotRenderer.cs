using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using SimsSDL.UI;

namespace SimsSDL;

public sealed partial class LotRenderer : IDisposable
{
    public static readonly float OriginalUIBlue = (float)Math.Pow((82.0 / 255.0 + 0.055) / 1.055, 2.4);

    public const float TrailTexels = 32f;

    public const float TrailMargin = 1f;

    private const int MaxLamps = 16;

    private const int MaxPrints = 16;

    private const int MaxCachedDynamicTextures = 256;

    private readonly record struct OrderedDrawEntry(
        int Level,
        int Layer,
        float Depth,
        bool IsMesh,
        int Index,
        SceneDrawItem Item)
    {
        public static int Compare(in OrderedDrawEntry a, in OrderedDrawEntry b, int spriteCount)
        {
            int cmp = a.Level.CompareTo(b.Level);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = a.Layer.CompareTo(b.Layer);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = a.Depth.CompareTo(b.Depth);
            if (cmp != 0)
            {
                return cmp;
            }

            int lhsOrdinal = a.IsMesh ? spriteCount + a.Index : a.Index;
            int rhsOrdinal = b.IsMesh ? spriteCount + b.Index : b.Index;
            return lhsOrdinal.CompareTo(rhsOrdinal);
        }
    }

    private readonly struct OrderedDrawEntryComparer(int spriteCount) : IComparer<OrderedDrawEntry>
    {
        public int Compare(OrderedDrawEntry x, OrderedDrawEntry y) => OrderedDrawEntry.Compare(in x, in y, spriteCount);
    }

    private readonly record struct MeshSlice(GPUBuffer Buffer, uint FirstVertex, uint VertexCount);

    private sealed class PreparedScene
    {
        public required LotScene Scene { get; init; }
        public required PickerAlphaMask[] Masks { get; init; }
        public required Dictionary<int, (int Width, int Height, bool Depth8, byte[] Pixels)> PreparedUploads { get; init; }
        public required List<SceneDrawItem> DrawItems { get; init; }
        public required Vector4[] AllMeshBounds { get; init; }
        public required Vector4[] StaticMeshBounds { get; init; }
        public GroundTilePicker? GroundPicker { get; init; }
    }

    private sealed class PreviewMeshes
    {
        public required List<GPUTexture> Textures { get; init; }
        public required List<GPUBuffer?> Buffers { get; init; }
        public required List<int> VertexCounts { get; init; }
        public required CGRect Bounds { get; init; }
    }

    private sealed record CachedObjectHit(
        Vector2 Point,
        Vector2 Canvas,
        LotZoom Zoom,
        float VisualZoomFactor,
        Vector2 Pan,
        long Revision,
        long Sequence,
        int? Result);

    private struct PendingMeshBatch
    {
        public GPUContext.Program Program;
        public GPUContext.DepthMode Depth;
        public GPUTexture Texture;
        public nint Sampler;
        public GPUBuffer Buffer;
        public Vector4 Tint;
        public float UVRectX;
        public Vector3 SunDir;
        public uint FirstVertex;
        public uint VertexCount;
    }

    /// <summary>Resolves the camera-space 3D sun direction used to shade roof facets. Uses the live
    /// ambient sun when a lighting plugin supplies a directional sun, clamped so high-noon sun still
    /// preserves facet contrast; otherwise falls back to a classic diagonal isometric sun.</summary>
    internal static Vector3 ResolveRoofSunDirection(bool ambientActive, Vector3 ambientSun)
    {
        if (ambientActive)
        {
            var horiz = new Vector2(ambientSun.Y, ambientSun.Z);
            float hLen = horiz.Length();
            if (hLen > 0.05f)
            {
                const float invSqrt2 = 0.70710678f;
                float up = Math.Clamp(ambientSun.X, 0.25f, 0.78f);
                float targetHoriz = MathF.Sqrt(MathF.Max(0f, 1f - up * up));
                horiz *= targetHoriz / hLen;
                return Vector3.Normalize(new Vector3(
                    (horiz.X + horiz.Y) * invSqrt2,
                    up,
                    (horiz.X - horiz.Y) * invSqrt2));
            }
        }

        return Vector3.Normalize(new Vector3(0.75f, 0.55f, -0.35f));
    }

    private enum SpritePassMode
    {
        Single,
        OpaqueCore,
        EdgeFringe,
    }

    /// <summary>Parameters for depth-based ambient occlusion and sun shadows, specified in scene pixels
    /// where one tile width equals 64 pixels.</summary>
    public struct AmbientParameters
    {
        public float Radius;
        public float Occlusion;
        public float Shadow;
        public float Thickness;
        public float Reach;
        public float ContactRadius;
        public float Contact;
        public float HeightTexels;
        public float GroundOffset;
        public Vector3 Sun;

        public float LampReach;
        public float LampGain;
        public float Night;
        public float WindowSun;

        public float CloudCover;
        public float CloudShade;
        public Vector2 Wind;
        public float CloudHeight;
        public float? CloudTime;
        public float Rain;
        public float Wetness;
        public float Tint;
        public int SurfaceMode;
        public float SurfaceAmount;
        public float GrassQuality;
        public float SpringBloom;
        public float Snowfall;
        public float Fireflies;
        public float Hail;
        public float Frost;
        public float Thunder;

        public AmbientParameters()
        {
            HeightTexels = 16f;
            Sun = new Vector3(1f, 0f, 0f);
        }
    }

    public GPUContext GPU { get; }
    public LotScene? Scene { get; private set; }
    private List<PickerAlphaMask> _sceneMasks = [];
    private List<LotScene.Texture> _deferredPixelTextures = [];
    public string? SceneRoot { get; private set; }
    private long _sceneRevision;
    private long _animationSequence;
    private List<GPUTexture> _sceneTextures = [];
    public int BaseTextureCount { get; private set; }
    private Dictionary<string, int> _textureIndexByFile = new();
    private Dictionary<string, GPUTexture> _cachedTextures = new();
    private Dictionary<string, PickerAlphaMask> _cachedMasks = new();
    private readonly Dictionary<string, GPUTexture> _dynamicTextureCache = new();
    private readonly Dictionary<string, PickerAlphaMask> _dynamicMaskCache = new();
    private readonly List<string> _dynamicTextureOrder = [];
    private GPUBuffer? _terrainBuffer;
    private List<MeshSlice?> _meshSlices = [];
    private readonly GPUBuffer?[] _dynamicMeshRing = new GPUBuffer?[3];
    private int _dynamicMeshRingSlot;
    private AvatarVertex[] _dynamicVertexScratch = [];
    private Vector4[] _meshBounds = [];
    private List<LotScene.Sprite> _staticSprites = [];
    private List<LotScene.Mesh> _staticMeshes = [];
    private int _staticSpriteCount;
    private int _staticMeshCount;
    private List<MeshSlice?> _staticMeshSlices = [];
    private Vector4[] _staticMeshBounds = [];
    private List<OrderedDrawEntry> _staticDrawEntries = [];
    private int[] _staticShadowSpriteIndices = [];
    private List<int> _activeShadowSpriteIndices = [];
    private List<SceneDrawItem> _drawItems = [];
    private readonly List<LotScene.Mesh> _dynamicMeshesScratch = [];
    private readonly List<OrderedDrawEntry> _dynamicEntriesScratch = [];
    private readonly List<MeshSlice?> _dynamicSlicesScratch = [];

    private static bool HasFloorShadow(LotScene.Sprite sprite) =>
        sprite.Wall is null && (sprite.FloorShadow is not null || sprite.FloorShadowNear is not null || sprite.FloorShadowFar is not null);
    private ScenePicker? _picker;
    private bool _pickerDirty;
    private CachedObjectHit? _cachedObjectHit;
    private GroundTilePicker? _groundPicker;

    private TerrainVertex[] _placementVertices = [];
    private GPUBuffer? _placementBuffer;
    private bool _placementDirty;
    private AvatarVertex[] _floorBrushVertices = [];
    private GPUBuffer? _floorBrushBuffer;
    private bool _floorBrushDirty;
    private string? _floorBrushTextureKey;
    private (int Width, int Height, bool Depth8, byte[] Pixels)? _floorBrushPixels;
    private GPUTexture? _floorBrushTexture;
    private PlacementPreview.GhostData? _placementGhost;
    public bool? PlacementIsValid { get; private set; }
    private (int Width, int Height, bool Depth8, byte[] Pixels)[] _ghostPixels = [];
    private IReadOnlyList<GPUTexture> _ghostTextures = [];

    public PlacementPreview.GhostData? BuildCursor { get; private set; }
    private (int Width, int Height, bool Depth8, byte[] Pixels)[]? _buildCursorPixels;
    private IReadOnlyList<GPUTexture> _buildCursorTextures = [];
    public PlacementPreview.GhostData? WallPaintPreview { get; private set; }
    private (int Width, int Height, bool Depth8, byte[] Pixels)[]? _wallPaintPixels;
    private IReadOnlyList<GPUTexture> _wallPaintTextures = [];

    public bool ShowsPlacement { get; set; }
    public bool ShowsGhost { get; set; }
    public bool ShowsBuildCursor { get; set; }
    public bool LiveMode { get; set; }
    public bool SuppressControlPanelInset { get; set; }

    public int Speed { get; set; }
    public bool AmbientPaused { get; set; }
    public float UIScale { get; set; } = 2f;

    private LotZoom _zoom = LotZoom.Near;
    private float _visualZoomFactor = LotZoom.Near.Factor();

    public LotZoom Zoom
    {
        get => _zoom;
        set
        {
            _zoom = value;
            _visualZoomFactor = value.Factor();
        }
    }

    public float VisualZoomFactor => _visualZoomFactor;
    public bool IsZoomInterpolating => MathF.Abs(_visualZoomFactor - _zoom.Factor()) > 1e-4f;

    /// <summary>Selects discrete asset LOD at geometric scale midpoints (0.7071 and 1.4142) so zooming
    /// preserves higher-resolution art until halfway to the next tier.</summary>
    public LotZoom EffectiveVisualZoom => !DrawsLot
        ? Zoom.AssetZoom()
        : _visualZoomFactor < 0.70710678f
            ? LotZoom.Far
            : _visualZoomFactor < 1.41421356f
                ? LotZoom.Medium
                : LotZoom.Near;

    public void SnapVisualZoom(LotZoom zoom) => Zoom = zoom;

    internal (LotZoom Zoom, float VisualFactor) SaveVisualZoom() => (_zoom, _visualZoomFactor);

    internal void RestoreVisualZoom((LotZoom Zoom, float VisualFactor) saved)
    {
        _zoom = saved.Zoom;
        _visualZoomFactor = saved.VisualFactor;
    }

    public int TerrainDetail { get; set; } = 2;

    public int CharacterDetail { get; set; } = 2;

    public int PanAssignments { get; private set; }

    private Vector2 _pan = Vector2.Zero;
    public Vector2 Pan
    {
        get => _pan;
        set
        {
            PanAssignments = unchecked(PanAssignments + 1);
            _pan = Scene is not null
                ? SimsSDL.Core.SceneViewport.ClampedPan(value, Scene, MathF.Max(UIScale, 0.01f) * _visualZoomFactor, _neighborhoodPanTileBounds)
                : value;
        }
    }

    public bool LightingEnabled { get; set; } = true;
    public bool AntiAliasedSprites { get; set; } = true;

    public List<UIDraw> Overlay { get; set; } = [];

    public bool DrawsLot { get; set; } = true;

    public (int Index, Dictionary<int, Vector4> Rects)? PreviewPlacement { get; set; }

    private readonly Dictionary<int, PreviewMeshes> _previews = new();
    private Dictionary<UITexture, (UITexture Source, int Version, GPUTexture Texture)> _uiTextures =
        new(ReferenceEqualityComparer.Instance);
    private Dictionary<UITexture, (UITexture Source, int Version, GPUTexture Texture)> _uiTexturesAlt =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<UITexture> _uiUploadsScratch = [];
    private readonly HashSet<UITexture> _uiUploadSeenScratch = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<GPUTexture> _retainedUiScratch = new(ReferenceEqualityComparer.Instance);

    private int _sceneLoadsInFlight;
    private long _newestRequestedRevision;

    private List<SceneAnimation> _pendingFrames = [];

    public bool AmbientEnabled { get; set; } = true;
    public AmbientParameters Ambient = new();

    public bool ShadowsEnabled { get; set; } = true;
    public PluginEnvironment? Environment { get; private set; }
    private long _lastPrint;

    public int AmbientDebug { get; set; }

    private GPUTexture? _heightMapTexture;
    private float _heightMargin = 4f;
    private GPUTexture? _footMapTexture;
    private GPUTexture? _worldShadowColor;
    private GPUTexture? _worldShadowDepth;
    private LotScene? _worldShadowScene;
    private string _worldShadowKey = string.Empty;
    private long _worldShadowAnimSeq;
    private Vector4? _worldHeightFrame;
    private Vector4? _worldFootFrame;
    private SimsSDL.Core.SceneViewport? _worldShadowViewport;
    private GPUTexture? _overheadMap;
    private GPUTexture? _emptyOverhead;
    private GPUTexture? _indoorMask;
    private float _indoorBase;
    private GPUTexture? _emptyMask;
    private GPUTexture? _groundDepthTexture;
    private GPUTexture? _ambientTermTexture;
    private List<GPUTexture> _trailTextures = [];
    private int _trailIndex;
    private string _trailKey = string.Empty;
    private ulong? _ambientTimeTicks;
    private double _ambientTimeSeconds;
    private float _trailFill;
    private readonly List<Vector4> _pendingPrints = [];
    private GPUTexture? _surfaceLiftTexture;
    private GPUTexture? _emptyLiftTexture;
    private GPUTexture? _simMaskTexture;
    private GPUTexture? _emptySimMaskTexture;
    private GPUTexture? _surfaceSourceTexture;
    private GPUTexture? _seasonDepthTexture;

    private readonly Queue<(ulong Frame, IDisposable Resource)> _retiredGpu = new();
    private ulong _frameCounter;
    private bool _disposed;

    public LotRenderer(GPUContext gpu)
    {
        GPU = gpu ?? throw new ArgumentNullException(nameof(gpu));
    }

    public void BeginFrame()
    {
        _frameCounter++;
        DrainRetired(minAge: 3);
    }

    private void Retire(IDisposable? resource)
    {
        if (resource is null)
        {
            return;
        }

        if (_disposed)
        {
            resource.Dispose();
            return;
        }

        _retiredGpu.Enqueue((_frameCounter, resource));
    }

    private void DrainRetired(ulong minAge)
    {
        while (_retiredGpu.TryPeek(out var entry) && _frameCounter - entry.Frame >= minAge)
        {
            _retiredGpu.Dequeue();
            entry.Resource.Dispose();
        }
    }

    private void RetireDistinctBuffers(
        IReadOnlyList<MeshSlice?> slices,
        int startIndex = 0,
        IReadOnlyList<MeshSlice?>? excludeSlices = null)
    {
        if (startIndex >= slices.Count)
        {
            return;
        }

        HashSet<GPUBuffer>? excluded = null;
        if (excludeSlices is { Count: > 0 })
        {
            excluded = new HashSet<GPUBuffer>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < excludeSlices.Count; i++)
            {
                if (excludeSlices[i] is { Buffer: { } excludedBuffer })
                {
                    excluded.Add(excludedBuffer);
                }
            }
        }

        HashSet<GPUBuffer>? seen = null;
        for (int i = startIndex; i < slices.Count; i++)
        {
            if (slices[i] is not { Buffer: { } buffer })
            {
                continue;
            }

            if (_dynamicMeshRing.Contains(buffer, ReferenceEqualityComparer.Instance) ||
                (excluded is not null && excluded.Contains(buffer)))
            {
                continue;
            }

            seen ??= new HashSet<GPUBuffer>(ReferenceEqualityComparer.Instance);
            if (seen.Add(buffer))
            {
                Retire(buffer);
            }
        }
    }

    private void RetireDistinctTextures(IEnumerable<GPUTexture> textures)
    {
        HashSet<GPUTexture>? seen = null;
        foreach (GPUTexture texture in textures)
        {
            seen ??= new HashSet<GPUTexture>(ReferenceEqualityComparer.Instance);
            if (seen.Add(texture))
            {
                Retire(texture);
            }
        }
    }

    private void RetirePreview(PreviewMeshes preview)
    {
        var seen = new HashSet<IDisposable>(ReferenceEqualityComparer.Instance);
        foreach (GPUBuffer? buffer in preview.Buffers)
        {
            if (buffer is not null && seen.Add(buffer))
            {
                Retire(buffer);
            }
        }

        foreach (GPUTexture texture in preview.Textures)
        {
            if (seen.Add(texture))
            {
                Retire(texture);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearAdjacentNeighborhood();
        var all = new HashSet<IDisposable>(ReferenceEqualityComparer.Instance);
        while (_retiredGpu.TryDequeue(out var entry))
        {
            all.Add(entry.Resource);
        }

        static void Collect(HashSet<IDisposable> set, IDisposable? resource)
        {
            if (resource is not null)
            {
                set.Add(resource);
            }
        }

        Collect(all, _terrainBuffer);
        Collect(all, _placementBuffer);
        Collect(all, _floorBrushBuffer);
        Collect(all, _floorBrushTexture);
        Collect(all, _heightMapTexture);
        Collect(all, _footMapTexture);
        Collect(all, _worldShadowColor);
        Collect(all, _worldShadowDepth);
        Collect(all, _overheadMap);
        Collect(all, _emptyOverhead);
        Collect(all, _indoorMask);
        Collect(all, _emptyMask);
        Collect(all, _groundDepthTexture);
        Collect(all, _ambientTermTexture);
        Collect(all, _surfaceLiftTexture);
        Collect(all, _emptyLiftTexture);
        Collect(all, _simMaskTexture);
        Collect(all, _emptySimMaskTexture);
        Collect(all, _surfaceSourceTexture);
        Collect(all, _seasonDepthTexture);

        foreach (MeshSlice? slice in _meshSlices) Collect(all, slice?.Buffer);
        foreach (MeshSlice? slice in _staticMeshSlices) Collect(all, slice?.Buffer);
        foreach (GPUBuffer? ring in _dynamicMeshRing) Collect(all, ring);
        foreach (GPUTexture tex in _sceneTextures) all.Add(tex);
        foreach (GPUTexture tex in _cachedTextures.Values) all.Add(tex);
        foreach (GPUTexture tex in _dynamicTextureCache.Values) all.Add(tex);
        foreach (GPUTexture tex in _trailTextures) all.Add(tex);
        foreach (GPUTexture tex in _ghostTextures) all.Add(tex);
        foreach (GPUTexture tex in _buildCursorTextures) all.Add(tex);
        foreach (GPUTexture tex in _wallPaintTextures) all.Add(tex);
        foreach (var entry in _uiTextures.Values) all.Add(entry.Texture);
        foreach (PreviewMeshes preview in _previews.Values)
        {
            foreach (GPUBuffer? b in preview.Buffers) Collect(all, b);
            foreach (GPUTexture t in preview.Textures) all.Add(t);
        }

        _sceneTextures = [];
        _cachedTextures = [];
        _dynamicTextureCache.Clear();
        _uiTextures.Clear();
        _uiTexturesAlt.Clear();
        _previews.Clear();
        _trailTextures = [];
        _ghostTextures = [];
        _buildCursorTextures = [];
        _wallPaintTextures = [];
        _meshSlices = [];
        _staticMeshSlices = [];

        foreach (IDisposable resource in all)
        {
            resource.Dispose();
        }
    }

    public bool IsLoadingScene => _sceneLoadsInFlight > 0;
    public long SceneRevisionNumber => _sceneRevision;
    public long AnimationSequenceNumber => _animationSequence;
    public int ViewLevel => Scene?.SelectedLevel ?? 1;
    public bool RoofsVisible => Scene?.RoofsVisible ?? false;
    public int StoryCount => Scene?.Stories ?? 2;
    public SimsSDL.Core.LotCamera CameraOrientation => Scene?.Camera ?? SimsSDL.Core.LotCamera.TopLeft;

    public void UploadOverlay()
    {
        if (_placementDirty)
        {
            _placementDirty = false;
            Retire(_placementBuffer);
            _placementBuffer = GPU.MakeStorageBuffer(_placementVertices);
            RetireDistinctTextures(_ghostTextures);
            _ghostTextures = GPU.UploadTextures(_ghostPixels);
            _ghostPixels = [];
        }

        if (_floorBrushDirty)
        {
            _floorBrushDirty = false;
            Retire(_floorBrushBuffer);
            _floorBrushBuffer = GPU.MakeStorageBuffer(_floorBrushVertices);
            if (_floorBrushPixels is { } pixels)
            {
                _floorBrushPixels = null;
                Retire(_floorBrushTexture);
                ReadOnlySpan<bool> floorBrushMip = [true];
                var uploaded = GPU.UploadTextures([(pixels.Width, pixels.Height, pixels.Depth8, pixels.Pixels)], floorBrushMip);
                _floorBrushTexture = uploaded.Count > 0 ? uploaded[0] : null;
            }
            else if (_floorBrushTextureKey is null)
            {
                Retire(_floorBrushTexture);
                _floorBrushTexture = null;
            }
        }

        if (_buildCursorPixels is { } cursorPixels)
        {
            _buildCursorPixels = null;
            RetireDistinctTextures(_buildCursorTextures);
            _buildCursorTextures = GPU.UploadTextures(cursorPixels);
        }

        if (_wallPaintPixels is { } wallPixels)
        {
            _wallPaintPixels = null;
            RetireDistinctTextures(_wallPaintTextures);
            _wallPaintTextures = GPU.UploadTextures(wallPixels);
        }

        var next = _uiTexturesAlt;
        next.Clear();
        List<UITexture> uploads = _uiUploadsScratch;
        uploads.Clear();
        _uiUploadSeenScratch.Clear();

        for (int i = 0; i < Overlay.Count; i++)
        {
            UITexture texture = Overlay[i].Texture;
            if (next.ContainsKey(texture))
            {
                continue;
            }

            if (_uiTextures.TryGetValue(texture, out var cached) && cached.Version == texture.Version)
            {
                next[texture] = cached;
            }
            else if (_uiUploadSeenScratch.Add(texture))
            {
                uploads.Add(texture);
            }
        }

        if (uploads.Count > 0)
        {
            var imageBatch = new (int Width, int Height, bool Depth8, byte[] Pixels)[uploads.Count];
            var mipFlags = new bool[uploads.Count];
            for (int i = 0; i < uploads.Count; i++)
            {
                UITexture u = uploads[i];
                imageBatch[i] = (u.Width, u.Height, false, u.Pixels);
                mipFlags[i] = u.Width > 1 && u.Height > 1;
            }

            IReadOnlyList<GPUTexture> uploadedTextures = GPU.UploadTextures(imageBatch, mipFlags);
            for (int i = 0; i < uploads.Count; i++)
            {
                UITexture source = uploads[i];
                next[source] = (source, source.Version, uploadedTextures[i]);
            }
        }

        if (_uiTextures.Count > 0 && (uploads.Count > 0 || next.Count != _uiTextures.Count))
        {
            HashSet<GPUTexture> retainedUi = _retainedUiScratch;
            retainedUi.Clear();
            foreach (var entry in next.Values)
            {
                retainedUi.Add(entry.Texture);
            }

            foreach (var entry in _uiTextures.Values)
            {
                if (retainedUi.Add(entry.Texture))
                {
                    Retire(entry.Texture);
                }
            }
        }

        _uiTexturesAlt = _uiTextures;
        _uiTexturesAlt.Clear();
        _uiTextures = next;
    }

    private SimsSDL.Core.SceneLighting? ActiveLighting(LotScene scene) => LightingEnabled ? scene.Lighting : null;

    private static (int Width, int Height, bool Depth8, byte[] Pixels) PrepareUpload(LotScene.Texture image, byte[] data)
    {
        bool depth8 = string.Equals(image.Encoding, "depth8", StringComparison.Ordinal);
        return (
            image.Width,
            image.Height,
            depth8,
            depth8 ? data : SpritePixels.PremultipliedSRGB(data, decontaminateEdges: true, width: image.Width, height: image.Height));
    }

    private static string CacheTexturePath(string rootDir, string file) =>
        Path.GetFullPath(Path.Combine(rootDir, file.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));

    private static string ResolveRealPath(string path) => LotScene.ResolveRealPath(path);

    public void LoadScene(string path, long revision = 0)
    {
        (LotScene Scene, List<byte[]> Pixels) loaded = LotScene.Load(path);
        PreparedScene prepared = PrepareScene(loaded, path, SnapshotCachedTextureSpecs());
        GPU.BatchingUploads(() => Install(prepared, path, revision));
    }

    private Dictionary<string, (int Width, int Height, bool Depth8, PickerAlphaMask Mask)> SnapshotCachedTextureSpecs()
    {
        var specs = new Dictionary<string, (int Width, int Height, bool Depth8, PickerAlphaMask Mask)>(_cachedTextures.Count + _dynamicTextureCache.Count);
        foreach (var (key, tex) in _dynamicTextureCache)
        {
            if (_dynamicMaskCache.TryGetValue(key, out var mask))
            {
                specs[key] = (tex.Width, tex.Height, tex.Format == SDL_GPU_TEXTUREFORMAT_R8_UNORM, mask);
            }
        }

        foreach (var (key, tex) in _cachedTextures)
        {
            if (_cachedMasks.TryGetValue(key, out var mask))
            {
                specs[key] = (tex.Width, tex.Height, tex.Format == SDL_GPU_TEXTUREFORMAT_R8_UNORM, mask);
            }
        }

        return specs;
    }

    private static PreparedScene PrepareScene(
        (LotScene Scene, List<byte[]> Pixels) loaded,
        string path,
        Dictionary<string, (int Width, int Height, bool Depth8, PickerAlphaMask Mask)> cachedSpecs)
    {
        string rootDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var masks = new PickerAlphaMask[loaded.Scene.Textures.Count];
        List<int> workIndices = [];
        for (int index = 0; index < loaded.Scene.Textures.Count; index++)
        {
            LotScene.Texture image = loaded.Scene.Textures[index];
            string key = CacheTexturePath(rootDir, image.File);
            bool isDepth = string.Equals(image.Encoding, "depth8", StringComparison.Ordinal);
            if (cachedSpecs.TryGetValue(key, out var spec) &&
                spec.Width == image.Width &&
                spec.Height == image.Height &&
                spec.Depth8 == isDepth)
            {
                masks[index] = spec.Mask;
                continue;
            }

            workIndices.Add(index);
        }

        var preparedUploads = new Dictionary<int, (int Width, int Height, bool Depth8, byte[] Pixels)>(workIndices.Count);
        if (workIndices.Count > 0)
        {
            if (workIndices.Count >= 8)
            {
                var slots = new (int Width, int Height, bool Depth8, byte[] Pixels)[workIndices.Count];
                Exception? firstError = null;
                Parallel.For(0, workIndices.Count, i =>
                {
                    int texIndex = workIndices[i];
                    try
                    {
                        var info = loaded.Scene.Textures[texIndex];
                        var raw = loaded.Pixels[texIndex];
                        masks[texIndex] = PickerAlphaMask.FromTexture(info, raw);
                        slots[i] = PrepareUpload(info, raw);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref firstError, ex, null);
                    }
                });

                if (firstError is not null)
                {
                    ExceptionDispatchInfo.Capture(firstError).Throw();
                }

                for (int i = 0; i < workIndices.Count; i++)
                {
                    preparedUploads[workIndices[i]] = slots[i];
                }
            }
            else
            {
                foreach (int texIndex in workIndices)
                {
                    var info = loaded.Scene.Textures[texIndex];
                    var raw = loaded.Pixels[texIndex];
                    masks[texIndex] = PickerAlphaMask.FromTexture(info, raw);
                    preparedUploads[texIndex] = PrepareUpload(info, raw);
                }
            }
        }

        List<SceneDrawItem> drawItems = SceneDrawItem.Ordered(loaded.Scene);
        IReadOnlyList<LotScene.Mesh> allMeshes = loaded.Scene.Meshes ?? [];
        Vector4[] allMeshBounds = ScenePicker.ComputeMeshBounds(allMeshes);
        List<LotScene.Mesh> staticMeshes = allMeshes.Where(m => m.ObjectID == 0).ToList();
        Vector4[] staticMeshBounds = ScenePicker.ComputeMeshBounds(staticMeshes);
        GroundTilePicker? groundPicker = GroundTilePicker.Create(loaded.Scene);

        return new PreparedScene
        {
            Scene = loaded.Scene,
            Masks = masks,
            PreparedUploads = preparedUploads,
            DrawItems = drawItems,
            AllMeshBounds = allMeshBounds,
            StaticMeshBounds = staticMeshBounds,
            GroundPicker = groundPicker,
        };
    }

    public void LoadSceneInBackground(string path, long revision, Action<Exception?> loaded)
    {
        _newestRequestedRevision = Math.Max(_newestRequestedRevision, revision);
        _sceneLoadsInFlight += 1;
        var cachedSpecs = SnapshotCachedTextureSpecs();
        _ = Task.Run(() =>
        {
            PreparedScene? prepared = null;
            Exception? decodeError = null;
            try
            {
                var decoded = LotScene.Load(path);
                prepared = PrepareScene(decoded, path, cachedSpecs);
            }
            catch (Exception error)
            {
                decodeError = error;
            }

            MainThreadDispatcher.Post(() =>
            {
                _sceneLoadsInFlight -= 1;
                if (revision < _newestRequestedRevision)
                {
                    return;
                }

                try
                {
                    if (decodeError is not null)
                    {
                        loaded(decodeError);
                        return;
                    }

                    GPU.BatchingUploads(() => Install(prepared!, path, revision));
                    loaded(null);
                }
                catch (Exception error)
                {
                    loaded(error);
                }
            });
        });
    }

    private void CacheDynamicTexture(GPUTexture texture, PickerAlphaMask mask, string key)
    {
        if (!_dynamicTextureCache.ContainsKey(key))
        {
            _dynamicTextureOrder.Add(key);
            if (_dynamicTextureOrder.Count > MaxCachedDynamicTextures)
            {
                int overflow = _dynamicTextureOrder.Count - MaxCachedDynamicTextures;
                for (int i = 0; i < overflow; i++)
                {
                    string evicted = _dynamicTextureOrder[i];
                    if (_dynamicTextureCache.Remove(evicted, out var evictedTex) &&
                        !IsTextureReferencedOutsideDynamicCache(evictedTex))
                    {
                        Retire(evictedTex);
                    }
                    _dynamicMaskCache.Remove(evicted);
                }

                _dynamicTextureOrder.RemoveRange(0, overflow);
            }
        }

        _dynamicTextureCache[key] = texture;
        _dynamicMaskCache[key] = mask;
    }

    private bool IsTextureReferencedOutsideDynamicCache(GPUTexture target)
    {
        foreach (GPUTexture tex in _sceneTextures)
        {
            if (ReferenceEquals(tex, target)) return true;
        }

        foreach (GPUTexture tex in _cachedTextures.Values)
        {
            if (ReferenceEquals(tex, target)) return true;
        }

        return false;
    }

    private void Install(PreparedScene loaded, string path, long revision)
    {
        int textureCount = loaded.Scene.Textures.Count;
        var textures = new GPUTexture?[textureCount];
        List<(int Index, (int Width, int Height, bool Depth8, byte[] Pixels) Image, bool Mipmapped)> uploads = [];
        List<string> keys = new(textureCount);
        string rootDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";

        for (int index = 0; index < textureCount; index++)
        {
            LotScene.Texture image = loaded.Scene.Textures[index];
            string key = CacheTexturePath(rootDir, image.File);
            keys.Add(key);
            var format = string.Equals(image.Encoding, "depth8", StringComparison.Ordinal)
                ? SDL_GPU_TEXTUREFORMAT_R8_UNORM
                : GPUContext.ColorFormat;
            bool wantsMip = !string.Equals(image.Encoding, "depth8", StringComparison.Ordinal)
                && (image.Width > 1 || image.Height > 1);
            GPUTexture? cached = _cachedTextures.GetValueOrDefault(key) ?? _dynamicTextureCache.GetValueOrDefault(key);
            if (cached is not null && cached.Handle != nint.Zero && cached.Width == image.Width && cached.Height == image.Height && cached.Format == format
                && (!wantsMip || cached.NumLevels > 1))
            {
                textures[index] = cached;
            }
            else
            {
                if (!loaded.PreparedUploads.TryGetValue(index, out var preparedImage))
                {
                    byte[] raw = LotScene.ReadPixels([image], ResolveRealPath(rootDir))[0];
                    loaded.Masks[index] = PickerAlphaMask.FromTexture(image, raw);
                    preparedImage = PrepareUpload(image, raw);
                }

                uploads.Add((index, preparedImage, wantsMip));
            }
        }

        if (uploads.Count > 0)
        {
            var batch = new (int Width, int Height, bool Depth8, byte[] Pixels)[uploads.Count];
            var batchMips = new bool[uploads.Count];
            for (int i = 0; i < uploads.Count; i++)
            {
                batch[i] = uploads[i].Image;
                batchMips[i] = uploads[i].Mipmapped;
            }

            IReadOnlyList<GPUTexture> uploaded = GPU.UploadTextures(batch, batchMips);
            for (int i = 0; i < uploads.Count; i++)
            {
                textures[uploads[i].Index] = uploaded[i];
            }
        }

        List<GPUTexture> resolved = new(textureCount);
        for (int i = 0; i < textureCount; i++)
        {
            resolved.Add(textures[i]!);
        }

        var previousTextures = new HashSet<GPUTexture>(ReferenceEqualityComparer.Instance);
        foreach (GPUTexture oldTex in _sceneTextures) previousTextures.Add(oldTex);
        foreach (GPUTexture oldTex in _cachedTextures.Values) previousTextures.Add(oldTex);

        if (Scene is null || loaded.Scene.Camera != Scene.Camera || loaded.Scene.SelectedLevel != Scene.SelectedLevel)
        {
            WallPaintPreview = null;
            _wallPaintPixels = null;
            RetireDistinctTextures(_wallPaintTextures);
            _wallPaintTextures = [];
        }

        LotScene? previousScene = Scene;
        Scene = loaded.Scene;
        if (previousScene is not null && previousScene.House != loaded.Scene.House && _adjacentLots.Count > 0)
        {
            ApplySeamlessConnectedLotTransition(previousScene, loaded.Scene);
        }
        _sceneMasks = [.. loaded.Masks];
        _deferredPixelTextures = [];
        SceneRoot = rootDir;
        _sceneRevision = revision;
        _animationSequence = 0;
        Retire(_terrainBuffer);
        _terrainBuffer = MakeTerrain(loaded.Scene);

        IReadOnlyList<LotScene.Mesh> allMeshes = loaded.Scene.Meshes ?? [];
        List<LotScene.Sprite> keptSprites = new(loaded.Scene.Sprites.Count);
        foreach (LotScene.Sprite sprite in loaded.Scene.Sprites)
        {
            if ((sprite.ObjectID ?? 0) == 0)
            {
                keptSprites.Add(sprite);
            }
        }

        List<LotScene.Mesh> keptMeshes = new(allMeshes.Count);
        List<int> staticAllIndices = new(allMeshes.Count);
        List<LotScene.Mesh> dynamicMeshesInitial = [];
        List<int> dynamicAllIndices = [];
        for (int allIdx = 0; allIdx < allMeshes.Count; allIdx++)
        {
            LotScene.Mesh mesh = allMeshes[allIdx];
            if (mesh.ObjectID == 0)
            {
                keptMeshes.Add(mesh);
                staticAllIndices.Add(allIdx);
            }
            else
            {
                dynamicMeshesInitial.Add(mesh);
                dynamicAllIndices.Add(allIdx);
            }
        }

        _staticSprites = keptSprites;
        _staticMeshes = keptMeshes;
        _staticSpriteCount = keptSprites.Count;
        _staticMeshCount = keptMeshes.Count;
        _staticMeshBounds = loaded.StaticMeshBounds;

        List<int> staticShadowIndices = [];
        for (int index = 0; index < keptSprites.Count; index++)
        {
            if (HasFloorShadow(keptSprites[index]))
            {
                staticShadowIndices.Add(index);
            }
        }
        _staticShadowSpriteIndices = [.. staticShadowIndices];

        _activeShadowSpriteIndices.Clear();
        for (int index = 0; index < loaded.Scene.Sprites.Count; index++)
        {
            if (HasFloorShadow(loaded.Scene.Sprites[index]))
            {
                _activeShadowSpriteIndices.Add(index);
            }
        }

        List<OrderedDrawEntry> cachedStaticEntries = new(keptSprites.Count + keptMeshes.Count);
        for (int index = 0; index < keptSprites.Count; index++)
        {
            LotScene.Sprite sprite = keptSprites[index];
            cachedStaticEntries.Add(new OrderedDrawEntry(
                sprite.Level ?? 1,
                sprite.Layer ?? 0,
                sprite.Depth ?? index,
                false,
                index,
                SceneDrawItem.Sprite(index)));
        }

        for (int index = 0; index < keptMeshes.Count; index++)
        {
            LotScene.Mesh mesh = keptMeshes[index];
            cachedStaticEntries.Add(new OrderedDrawEntry(
                mesh.Level ?? 1,
                mesh.Layer ?? 1,
                mesh.Depth,
                true,
                index,
                SceneDrawItem.Mesh(index)));
        }

        int staticTotalSprites = keptSprites.Count;
        cachedStaticEntries.Sort((a, b) => OrderedDrawEntry.Compare(in a, in b, staticTotalSprites));
        _staticDrawEntries = cachedStaticEntries;

        List<int> staticMeshOrder = [];
        foreach (OrderedDrawEntry entry in cachedStaticEntries)
        {
            if (entry.IsMesh)
            {
                staticMeshOrder.Add(entry.Index);
            }
        }

        RetireDistinctBuffers(_meshSlices);
        List<MeshSlice?> builtStaticSlices = MakeCombinedMeshes(keptMeshes, staticMeshOrder);
        List<MeshSlice?> builtDynamicSlices = dynamicMeshesInitial.Count == 0 ? [] : MakeCombinedMeshes(dynamicMeshesInitial);
        _staticMeshSlices = builtStaticSlices;

        List<MeshSlice?> initialSlices = Enumerable.Repeat<MeshSlice?>(null, allMeshes.Count).ToList();
        for (int staticIdx = 0; staticIdx < staticAllIndices.Count; staticIdx++)
        {
            initialSlices[staticAllIndices[staticIdx]] = builtStaticSlices[staticIdx];
        }

        for (int dynIdx = 0; dynIdx < dynamicAllIndices.Count; dynIdx++)
        {
            initialSlices[dynamicAllIndices[dynIdx]] = builtDynamicSlices[dynIdx];
        }

        _meshSlices = initialSlices;
        _meshBounds = loaded.AllMeshBounds;

        _drawItems = loaded.DrawItems;
        _picker = null;
        _pickerDirty = true;
        _cachedObjectHit = null;
        _groundPicker = loaded.GroundPicker;
        _sceneTextures = resolved;
        BaseTextureCount = resolved.Count;

        var fileMap = new Dictionary<string, int>(textureCount);
        for (int index = 0; index < textureCount; index++)
        {
            fileMap[loaded.Scene.Textures[index].File] = index;
        }

        _textureIndexByFile = fileMap;
        Retire(_indoorMask);
        _indoorMask = MakeIndoorMask(loaded.Scene);
        Retire(_overheadMap);
        _overheadMap = MakeOverheadMap(loaded.Scene);

        var nextCachedTextures = new Dictionary<string, GPUTexture>(keys.Count);
        var nextCachedMasks = new Dictionary<string, PickerAlphaMask>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            nextCachedTextures[keys[i]] = resolved[i];
            nextCachedMasks[keys[i]] = loaded.Masks[i];
        }

        _cachedTextures = nextCachedTextures;
        _cachedMasks = nextCachedMasks;

        if (previousTextures.Count > 0)
        {
            foreach (GPUTexture keep in resolved) previousTextures.Remove(keep);
            foreach (GPUTexture keep in nextCachedTextures.Values) previousTextures.Remove(keep);
            foreach (GPUTexture keep in _dynamicTextureCache.Values) previousTextures.Remove(keep);
            foreach (GPUTexture dead in previousTextures) Retire(dead);
        }
    }

    public void QueueAnimation(SceneAnimation frame) => _pendingFrames.Add(frame);

    /// <summary>Applies queued animation frames matching the active scene revision immediately during
    /// background snapshot decoding, holding frames for newer revisions until their base snapshot installs.
    /// </summary>
    public void ApplyPendingAnimation(nint command = 0)
    {
        ApplyAdjacentPendingAnimations(command);
        if (_pendingFrames.Count == 0)
        {
            return;
        }

        List<SceneAnimation> future = [];
        List<SceneAnimation> matching = [];
        foreach (SceneAnimation frame in _pendingFrames)
        {
            if (frame.BaseRevision == _sceneRevision)
            {
                matching.Add(frame);
            }
            else if (frame.BaseRevision > _sceneRevision)
            {
                future.Add(frame);
            }
        }

        _pendingFrames = future;
        if (matching.Count == 0)
        {
            return;
        }
        _worldShadowAnimSeq++;

        int last = matching.Count - 1;
        GPU.BatchingUploads(command, () =>
        {
            ApplyAnimation(matching[last]);
        });
    }

    public void ApplyAnimation(SceneAnimation frame)
    {
        if (Scene is null || SceneRoot is null)
        {
            throw new ContentError("Animation requires a scene snapshot");
        }

        LotScene previousScene = Scene;
        LotScene updated = frame.Applying(previousScene, _staticSprites, _staticMeshes, _sceneRevision, _animationSequence);

        int keepCount = frame.TextureStart;
        var resolvedNew = frame.Textures.Count > 0 ? new GPUTexture?[frame.Textures.Count] : [];
        var resolvedMasks = frame.Textures.Count > 0 ? new PickerAlphaMask[frame.Textures.Count] : [];

        if (frame.Textures.Count > 0)
        {
            int count = frame.Textures.Count;
            List<int> missingIndices = [];
            List<LotScene.Texture> missingTextures = [];
            List<string> missingKeys = [];
            List<bool> missingMips = [];

            for (int offset = 0; offset < count; offset++)
            {
                LotScene.Texture image = frame.Textures[offset];
                var format = string.Equals(image.Encoding, "depth8", StringComparison.Ordinal)
                    ? SDL_GPU_TEXTUREFORMAT_R8_UNORM
                    : GPUContext.ColorFormat;
                bool wantsMip = !string.Equals(image.Encoding, "depth8", StringComparison.Ordinal)
                    && (image.Width > 1 || image.Height > 1);

                if (_textureIndexByFile.TryGetValue(image.File, out int existingIndex)
                    && existingIndex < _sceneTextures.Count
                    && existingIndex < previousScene.Textures.Count
                    && string.Equals(previousScene.Textures[existingIndex].File, image.File, StringComparison.Ordinal))
                {
                    GPUTexture existing = _sceneTextures[existingIndex];
                    if (existing.Handle != nint.Zero && existing.Width == image.Width && existing.Height == image.Height && existing.Format == format
                        && (!wantsMip || existing.NumLevels > 1))
                    {
                        resolvedNew[offset] = existing;
                        if (existingIndex < _sceneMasks.Count)
                        {
                            resolvedMasks[offset] = _sceneMasks[existingIndex];
                        }

                        continue;
                    }
                }

                string key = CacheTexturePath(SceneRoot, image.File);
                GPUTexture? cached = _cachedTextures.GetValueOrDefault(key) ?? _dynamicTextureCache.GetValueOrDefault(key);
                PickerAlphaMask? mask = _cachedMasks.TryGetValue(key, out var cachedMask)
                    ? cachedMask
                    : _dynamicMaskCache.TryGetValue(key, out var dynMask)
                        ? dynMask
                        : null;
                if (cached is not null && cached.Handle != nint.Zero && cached.Width == image.Width && cached.Height == image.Height && cached.Format == format
                    && mask is { } validMask && (!wantsMip || cached.NumLevels > 1))
                {
                    resolvedNew[offset] = cached;
                    resolvedMasks[offset] = validMask;
                }
                else
                {
                    missingIndices.Add(offset);
                    missingTextures.Add(image);
                    missingKeys.Add(key);
                    missingMips.Add(wantsMip);
                }
            }

            if (missingTextures.Count > 0)
            {
                string standardizedRoot = ResolveRealPath(SceneRoot);
                List<byte[]> rawData = LotScene.ReadPixels(missingTextures, standardizedRoot);
                var uploadBatch = new (int Width, int Height, bool Depth8, byte[] Pixels)[missingTextures.Count];
                for (int i = 0; i < missingTextures.Count; i++)
                {
                    resolvedMasks[missingIndices[i]] = PickerAlphaMask.FromTexture(missingTextures[i], rawData[i]);
                    uploadBatch[i] = PrepareUpload(missingTextures[i], rawData[i]);
                }

                IReadOnlyList<GPUTexture> uploaded = GPU.UploadTextures(uploadBatch, CollectionsMarshal.AsSpan(missingMips));
                for (int i = 0; i < uploaded.Count; i++)
                {
                    int idx = missingIndices[i];
                    resolvedNew[idx] = uploaded[i];
                }
            }
        }

        if (_sceneTextures.Count > keepCount)
        {
            _sceneTextures.RemoveRange(keepCount, _sceneTextures.Count - keepCount);
        }

        if (_sceneMasks.Count > keepCount)
        {
            _sceneMasks.RemoveRange(keepCount, _sceneMasks.Count - keepCount);
        }

        _deferredPixelTextures.Clear();

        if (frame.Textures.Count > 0)
        {
            bool anyMissingMask = false;
            for (int offset = 0; offset < frame.Textures.Count; offset++)
            {
                LotScene.Texture image = frame.Textures[offset];
                int index = _sceneTextures.Count;
                GPUTexture texture = resolvedNew[offset]!;
                _sceneTextures.Add(texture);
                _textureIndexByFile[image.File] = index;
                if (!anyMissingMask && _sceneMasks.Count == index)
                {
                    _sceneMasks.Add(resolvedMasks[offset]);
                }
                else
                {
                    anyMissingMask = true;
                }
            }

            // Cache dynamic textures only after all resolvedNew entries are in _sceneTextures so eviction never retires a live frame texture.
            for (int offset = 0; offset < frame.Textures.Count; offset++)
            {
                LotScene.Texture image = frame.Textures[offset];
                string key = CacheTexturePath(SceneRoot, image.File);
                if (!_cachedTextures.ContainsKey(key))
                {
                    CacheDynamicTexture(resolvedNew[offset]!, resolvedMasks[offset], key);
                }
            }

            if (anyMissingMask)
            {
                _deferredPixelTextures.AddRange(frame.Textures);
            }
        }

        RetireDistinctBuffers(_meshSlices, startIndex: 0, excludeSlices: _staticMeshSlices);
        IReadOnlyList<LotScene.Mesh> allUpdatedMeshes = updated.Meshes ?? [];
        List<LotScene.Mesh> dynamicMeshes = _dynamicMeshesScratch;
        dynamicMeshes.Clear();
        if (allUpdatedMeshes.Count > _staticMeshCount)
        {
            int dynCount = allUpdatedMeshes.Count - _staticMeshCount;
            if (dynamicMeshes.Capacity < dynCount)
                dynamicMeshes.Capacity = dynCount;
            for (int m = _staticMeshCount; m < allUpdatedMeshes.Count; m++)
            {
                dynamicMeshes.Add(allUpdatedMeshes[m]);
            }
        }

        List<MeshSlice?> dynamicSlices = _dynamicSlicesScratch;
        dynamicSlices.Clear();
        if (dynamicMeshes.Count > 0)
        {
            MakeCombinedDynamicMeshes(dynamicMeshes, dynamicSlices);
        }

        if (_meshSlices.Count > _staticMeshSlices.Count)
        {
            _meshSlices.RemoveRange(_staticMeshSlices.Count, _meshSlices.Count - _staticMeshSlices.Count);
        }
        else if (_meshSlices.Count < _staticMeshSlices.Count)
        {
            _meshSlices.Clear();
            _meshSlices.AddRange(_staticMeshSlices);
        }
        for (int i = 0; i < dynamicSlices.Count; i++)
        {
            _meshSlices.Add(dynamicSlices[i]);
        }

        if (dynamicMeshes.Count == 0)
        {
            _meshBounds = _staticMeshBounds;
        }
        else
        {
            int totalMeshCount = _staticMeshBounds.Length + dynamicMeshes.Count;
            if (ReferenceEquals(_meshBounds, _staticMeshBounds) || _meshBounds.Length != totalMeshCount)
            {
                _meshBounds = new Vector4[totalMeshCount];
                Array.Copy(_staticMeshBounds, _meshBounds, _staticMeshBounds.Length);
            }

            Vector4[] dynBounds = ScenePicker.ComputeMeshBounds(dynamicMeshes);
            Array.Copy(dynBounds, 0, _meshBounds, _staticMeshBounds.Length, dynBounds.Length);
        }

        _activeShadowSpriteIndices.Clear();
        for (int i = 0; i < _staticShadowSpriteIndices.Length; i++)
        {
            _activeShadowSpriteIndices.Add(_staticShadowSpriteIndices[i]);
        }

        int totalSprites = _staticSpriteCount + frame.Sprites.Count;
        List<OrderedDrawEntry> dynamicEntries = _dynamicEntriesScratch;
        dynamicEntries.Clear();
        int neededEntries = frame.Sprites.Count + dynamicMeshes.Count;
        if (dynamicEntries.Capacity < neededEntries)
        {
            dynamicEntries.Capacity = neededEntries;
        }

        for (int offset = 0; offset < frame.Sprites.Count; offset++)
        {
            LotScene.Sprite sprite = frame.Sprites[offset];
            int spriteIndex = _staticSpriteCount + offset;
            if (HasFloorShadow(sprite))
            {
                _activeShadowSpriteIndices.Add(spriteIndex);
            }
            dynamicEntries.Add(new OrderedDrawEntry(
                sprite.Level ?? 1,
                sprite.Layer ?? 0,
                sprite.Depth ?? spriteIndex,
                false,
                spriteIndex,
                SceneDrawItem.Sprite(spriteIndex)));
        }

        for (int offset = 0; offset < dynamicMeshes.Count; offset++)
        {
            LotScene.Mesh mesh = dynamicMeshes[offset];
            int meshIndex = _staticMeshCount + offset;
            dynamicEntries.Add(new OrderedDrawEntry(
                mesh.Level ?? 1,
                mesh.Layer ?? 1,
                mesh.Depth,
                true,
                meshIndex,
                SceneDrawItem.Mesh(meshIndex)));
        }

        CollectionsMarshal.AsSpan(dynamicEntries).Sort(new OrderedDrawEntryComparer(totalSprites));

        List<SceneDrawItem> merged = _drawItems;
        merged.Clear();
        int mergedCount = _staticDrawEntries.Count + dynamicEntries.Count;
        if (merged.Capacity < mergedCount)
        {
            merged.Capacity = mergedCount;
        }

        int s = 0;
        int d = 0;
        while (s < _staticDrawEntries.Count && d < dynamicEntries.Count)
        {
            OrderedDrawEntry staticEntry = _staticDrawEntries[s];
            OrderedDrawEntry dynEntry = dynamicEntries[d];
            if (OrderedDrawEntry.Compare(in staticEntry, in dynEntry, totalSprites) < 0)
            {
                merged.Add(staticEntry.Item);
                s += 1;
            }
            else
            {
                merged.Add(dynEntry.Item);
                d += 1;
            }
        }

        while (s < _staticDrawEntries.Count)
        {
            merged.Add(_staticDrawEntries[s].Item);
            s += 1;
        }

        while (d < dynamicEntries.Count)
        {
            merged.Add(dynamicEntries[d].Item);
            d += 1;
        }

        Scene = updated;
        _picker = null;
        _pickerDirty = true;
        _cachedObjectHit = null;
        _animationSequence = frame.Sequence;
    }

    private ScenePicker? EnsurePicker()
    {
        if (!_pickerDirty && _picker is not null)
        {
            return _picker;
        }

        if (Scene is null)
        {
            return null;
        }

        if (_deferredPixelTextures.Count > 0 && SceneRoot is not null)
        {
            string standardizedRoot = ResolveRealPath(SceneRoot);
            Dictionary<string, int> existingByFile = new(_sceneMasks.Count);
            int limit = Math.Min(_sceneMasks.Count, Scene.Textures.Count);
            for (int i = 0; i < limit; i++)
            {
                existingByFile.TryAdd(Scene.Textures[i].File, i);
            }

            List<LotScene.Texture> unread = [];
            Dictionary<string, PickerAlphaMask> unreadMap = new();
            foreach (LotScene.Texture image in _deferredPixelTextures)
            {
                if (!existingByFile.ContainsKey(image.File) && unreadMap.TryAdd(image.File, default))
                {
                    unread.Add(image);
                }
            }

            if (unread.Count > 0)
            {
                List<byte[]> loaded;
                try
                {
                    loaded = LotScene.ReadPixels(unread, standardizedRoot);
                }
                catch
                {
                    return null;
                }

                for (int i = 0; i < unread.Count; i++)
                {
                    unreadMap[unread[i].File] = PickerAlphaMask.FromTexture(unread[i], loaded[i]);
                }
            }

            foreach (LotScene.Texture image in _deferredPixelTextures)
            {
                if (existingByFile.TryGetValue(image.File, out int existingIndex))
                {
                    _sceneMasks.Add(_sceneMasks[existingIndex]);
                }
                else if (unreadMap.TryGetValue(image.File, out PickerAlphaMask mask))
                {
                    int newIndex = _sceneMasks.Count;
                    _sceneMasks.Add(mask);
                    existingByFile[image.File] = newIndex;
                }
                else
                {
                    return null;
                }
            }

            _deferredPixelTextures.Clear();
        }

        if (_sceneMasks.Count != Scene.Textures.Count)
        {
            return null;
        }

        IReadOnlyList<LotScene.Mesh> allMeshes = Scene.Meshes ?? [];
        Vector4[]? precomputedBounds = _meshBounds.Length == allMeshes.Count
            ? _meshBounds
            : null;

        var built = new ScenePicker(Scene, _sceneMasks, _drawItems, precomputedBounds);
        _picker = built;
        _pickerDirty = false;
        return built;
    }

    private Vector2? PickScenePoint(Vector2 point, Vector2 canvas)
    {
        if (point.X < 0f || point.Y < 0f || point.X >= canvas.X || point.Y >= canvas.Y ||
            Viewport(canvas) is not { } viewport)
        {
            return null;
        }

        return (point - viewport.Origin) / viewport.Scale;
    }

    private List<GPUBuffer?> MakeMeshes(IReadOnlyList<LotScene.Mesh> meshes)
    {
        List<GPUBuffer?> result = new(meshes.Count);
        foreach (LotScene.Mesh mesh in meshes)
        {
            var vertices = new AvatarVertex[mesh.Vertices.Count];
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                LotScene.MeshVertex v = mesh.Vertices[i];
                vertices[i] = new AvatarVertex
                {
                    Position = new Vector4(v.X, v.Y, v.Z, 1f),
                    Uv = new Vector2(v.U, v.V),
                    Normal = new Vector4(v.NX, v.NY, v.NZ, 0f),
                };
            }

            result.Add(GPU.MakeStorageBuffer(vertices));
        }

        return result;
    }

    private void MakeCombinedDynamicMeshes(IReadOnlyList<LotScene.Mesh> meshes, List<MeshSlice?> slices)
    {
        if (meshes.Count == 0)
        {
            return;
        }

        int totalVertices = 0;
        for (int i = 0; i < meshes.Count; i++)
        {
            totalVertices += meshes[i].Vertices.Count;
        }

        if (totalVertices == 0)
        {
            for (int i = 0; i < meshes.Count; i++)
            {
                slices.Add(null);
            }
            return;
        }

        if (_dynamicVertexScratch.Length < totalVertices)
        {
            _dynamicVertexScratch = new AvatarVertex[Math.Max(totalVertices * 2, 4096)];
        }

        if (slices.Capacity < meshes.Count)
        {
            slices.Capacity = meshes.Count;
        }

        int writePos = 0;
        int slot = _dynamicMeshRingSlot;
        _dynamicMeshRingSlot = (_dynamicMeshRingSlot + 1) % _dynamicMeshRing.Length;

        long neededBytes = (long)totalVertices * Marshal.SizeOf<AvatarVertex>();
        GPUBuffer? ring = _dynamicMeshRing[slot];
        if (ring is null || ring.ByteSize < neededBytes)
        {
            Retire(ring);
            ring = GPU.MakeStorageBufferCapacity<AvatarVertex>(Math.Max(totalVertices * 2, 4096));
            _dynamicMeshRing[slot] = ring;
        }

        for (int i = 0; i < meshes.Count; i++)
        {
            IReadOnlyList<LotScene.MeshVertex> verts = meshes[i].Vertices;
            if (verts.Count == 0)
            {
                slices.Add(null);
                continue;
            }

            uint first = (uint)writePos;
            for (int vi = 0; vi < verts.Count; vi++)
            {
                LotScene.MeshVertex v = verts[vi];
                _dynamicVertexScratch[writePos++] = new AvatarVertex
                {
                    Position = new Vector4(v.X, v.Y, v.Z, 1f),
                    Uv = new Vector2(v.U, v.V),
                    Normal = new Vector4(v.NX, v.NY, v.NZ, 0f),
                };
            }

            slices.Add(new MeshSlice(ring, first, (uint)verts.Count));
        }

        GPU.UploadIntoBuffer<AvatarVertex>(ring, _dynamicVertexScratch.AsSpan(0, totalVertices));
    }

    private List<MeshSlice?> MakeCombinedMeshes(IReadOnlyList<LotScene.Mesh> meshes, IReadOnlyList<int>? order = null)
    {
        if (meshes.Count == 0)
        {
            return [];
        }

        int indexCount = order?.Count ?? meshes.Count;
        int totalVertices = 0;
        for (int i = 0; i < indexCount; i++)
        {
            int idx = order is not null ? order[i] : i;
            totalVertices += meshes[idx].Vertices.Count;
        }

        if (totalVertices == 0)
        {
            return Enumerable.Repeat<MeshSlice?>(null, meshes.Count).ToList();
        }

        var combined = new AvatarVertex[totalVertices];
        var ranges = new (uint First, uint Count)?[meshes.Count];
        int writePos = 0;
        for (int i = 0; i < indexCount; i++)
        {
            int idx = order is not null ? order[i] : i;
            IReadOnlyList<LotScene.MeshVertex> verts = meshes[idx].Vertices;
            if (verts.Count == 0)
            {
                continue;
            }

            uint first = (uint)writePos;
            for (int vi = 0; vi < verts.Count; vi++)
            {
                LotScene.MeshVertex v = verts[vi];
                combined[writePos++] = new AvatarVertex
                {
                    Position = new Vector4(v.X, v.Y, v.Z, 1f),
                    Uv = new Vector2(v.U, v.V),
                    Normal = new Vector4(v.NX, v.NY, v.NZ, 0f),
                };
            }

            ranges[idx] = (first, (uint)verts.Count);
        }

        GPUBuffer? buffer = GPU.MakeStorageBuffer(combined);
        if (buffer is null)
        {
            return Enumerable.Repeat<MeshSlice?>(null, meshes.Count).ToList();
        }

        List<MeshSlice?> slices = new(meshes.Count);
        foreach (var range in ranges)
        {
            slices.Add(range is { } r ? new MeshSlice(buffer, r.First, r.Count) : null);
        }

        return slices;
    }

    private GPUBuffer? MakeTerrain(LotScene scene, IReadOnlyList<LotScene.TerrainVertex>? vertices = null, float[]? depths = null)
    {
        // Terrain shade ramps are blended in sRGB before converting to linear color.
        static float Unit(byte component) => component / 255f;

        IReadOnlyList<LotScene.TerrainVertex> source = vertices ?? scene.TerrainVertices ?? [];
        if (source.Count == 0)
        {
            return null;
        }

        float[]? groundDepth = depths ?? (vertices is null ? TerrainDepth(scene) : null);
        var buffer = new TerrainVertex[source.Count];
        for (int index = 0; index < source.Count; index++)
        {
            LotScene.TerrainVertex vertex = source[index];
            var light = new Vector4(Unit(vertex.R), Unit(vertex.G), Unit(vertex.B), 1f);
            Vector4 dark = vertex.DR is byte dr
                ? new Vector4(Unit(dr), Unit(vertex.DG ?? 0), Unit(vertex.DB ?? 0), 1f)
                : light * new Vector4(0.2f, 0.45f, 0.15f, 1f);
            float depth = groundDepth is not null && index < groundDepth.Length ? groundDepth[index] : 1f;
            buffer[index] = new TerrainVertex
            {
                Position = new Vector2(vertex.X, vertex.Y),
                Grass = new Vector2(Unit(vertex.Grass ?? 0), depth),
                Color = light,
                Dark = dark,
            };
        }

        return GPU.MakeStorageBuffer(buffer);
    }

    /// <summary>Calculates terrain vertex depth as 0.9 - 0.003 * (x + y) in camera tile coordinates,
    /// aligning ground depth with standing sprite bases.</summary>
    private static float[]? TerrainDepth(LotScene scene)
    {
        if (scene.LotWidth is not int width || scene.LotHeight is not int height ||
            scene.TerrainVertices is not { } vertices || vertices.Count != width * height * 6)
        {
            return null;
        }

        int orientation = ((scene.CameraOrientation ?? 0) % 4 + 4) % 4;
        float CameraSum(int x, int y) => orientation switch
        {
            0 => x + y,
            1 => height - y + x,
            2 => width - x + height - y,
            _ => y + width - x,
        };

        Span<(int DX, int DY)> corners = [(0, 0), (1, 0), (1, 1), (1, 1), (0, 1), (0, 0)];
        var depths = new float[vertices.Count];
        for (int index = 0; index < vertices.Count; index++)
        {
            int tile = index / 6;
            (int dx, int dy) = corners[index % 6];
            float sum = CameraSum(tile % width + dx, tile / width + dy);
            depths[index] = MathF.Min(0.999f, MathF.Max(0.001f, 0.9f - 0.003f * sum));
        }

        return depths;
    }

    public void SetZoom(LotZoom level, bool instant = false)
    {
        if (level == _zoom && (!instant || !IsZoomInterpolating))
        {
            return;
        }

        if (instant)
        {
            Vector2 rescaled = SimsSDL.Core.SceneViewport.RescaledPan(_pan, _visualZoomFactor, level.Factor());
            _zoom = level;
            _visualZoomFactor = level.Factor();
            Pan = rescaled;
            return;
        }

        _zoom = level;
    }

    public void StepSmoothZoom(double dtSeconds, bool instant = false)
    {
        float target = _zoom.Factor();
        if (MathF.Abs(target - _visualZoomFactor) <= 1e-5f)
        {
            return;
        }

        float prev = _visualZoomFactor;
        float next = instant
            ? target
            : prev + (target - prev) * (1f - MathF.Exp(-18f * (float)Math.Clamp(dtSeconds, 0.0, 0.1)));
        if (MathF.Abs(target - next) < 0.001f)
        {
            next = target;
        }

        _visualZoomFactor = next;
        Vector2 rescaled = SimsSDL.Core.SceneViewport.RescaledPan(_pan, prev, next);
        _pan = Scene is not null
            ? SimsSDL.Core.SceneViewport.ClampedPan(rescaled, Scene, MathF.Max(UIScale, 0.01f) * next, _neighborhoodPanTileBounds)
            : rescaled;
    }

    private void UpdateAmbientMaps(Vector2 canvas, nint command)
    {
        if (Scene is null || !DrawsLot || !AmbientEnabled || Environment is null)
        {
            return;
        }

        float scale = MathF.Max(0.01f, UIScale * Zoom.Factor());
        float coverage = MathF.Min(12f, MathF.Ceiling(canvas.X / (128f * scale) + canvas.Y / (64f * scale)) + 8f);
        if (coverage > _heightMargin)
        {
            _heightMargin = coverage;
            GPU.BatchingUploads(command, () =>
            {
                Retire(_indoorMask);
                _indoorMask = MakeIndoorMask(Scene);
                Retire(_overheadMap);
                _overheadMap = MakeOverheadMap(Scene);
            });
        }
    }

    private SimsSDL.Core.SceneViewport? Viewport(Vector2 canvas)
    {
        if (Scene is null)
        {
            return null;
        }

        return new SimsSDL.Core.SceneViewport(
            Scene,
            canvas,
            LiveMode && !SuppressControlPanelInset ? 100f * UIScale : 0f,
            _visualZoomFactor,
            UIScale,
            _pan,
            snapOriginToPixel: !IsZoomInterpolating,
            panTileBounds: _neighborhoodPanTileBounds);
    }

    public bool ContainsObject(int id) =>
        Scene is not null &&
        (Scene.Sprites.Any(s => s.ObjectID == id) || (Scene.Meshes?.Any(m => m.ObjectID == id) ?? false));

    public CGRect? ScreenBounds(int id, Vector2 canvas)
    {
        if (Scene is null || Viewport(canvas) is not { } viewport)
        {
            return null;
        }

        var low = new Vector2(float.PositiveInfinity);
        var high = new Vector2(float.NegativeInfinity);

        foreach (LotScene.Sprite sprite in Scene.Sprites)
        {
            if (sprite.ObjectID != id)
            {
                continue;
            }

            low = Vector2.Min(low, new Vector2(sprite.X, sprite.Y));
            high = Vector2.Max(high, new Vector2(sprite.X + sprite.Width, sprite.Y + sprite.Height));
        }

        if (Scene.Meshes is not null)
        {
            foreach (LotScene.Mesh mesh in Scene.Meshes)
            {
                if (mesh.ObjectID != id)
                {
                    continue;
                }

                foreach (LotScene.MeshVertex vertex in mesh.Vertices)
                {
                    var p = new Vector2(vertex.X, vertex.Y);
                    low = Vector2.Min(low, p);
                    high = Vector2.Max(high, p);
                }
            }
        }

        if (!(low.X <= high.X))
        {
            return null;
        }

        Vector2 origin = viewport.Origin + low * viewport.Scale;
        Vector2 size = (high - low) * viewport.Scale;
        return new CGRect(origin.X, origin.Y, size.X, size.Y);
    }

    public FloorTileHit? FloorHitAt(Vector2 point, Vector2 canvas)
    {
        if (PickScenePoint(point, canvas) is not { } location)
        {
            return null;
        }

        return _groundPicker?.Hit(location);
    }

    public SimsSDL.Core.GroundTile? GroundTileAt(Vector2 point, Vector2 canvas) => FloorHitAt(point, canvas)?.Tile;

    public Vector2? ScreenPoint(int x, int y, Vector2 canvas) =>
        ScreenPoint(x + 0.5f, y + 0.5f, canvas);

    public Vector2? ScreenPoint(float x, float y, Vector2 canvas)
    {
        if (Scene is null || Scene.LotWidth is not int width || Scene.LotHeight is not int height ||
            Viewport(canvas) is not { } viewport || !float.IsFinite(x) || !float.IsFinite(y))
        {
            return null;
        }

        var lotSize = new Vector2(width, height);
        Vector2 origin;
        if (Scene.OriginX is float originX && Scene.OriginY is float originY)
        {
            origin = new Vector2(originX, originY);
        }
        else if (Scene.TerrainVertices is { Count: > 0 } vertices)
        {
            LotScene.TerrainVertex first = vertices[0];
            Vector2 corner = Scene.Camera switch
            {
                SimsSDL.Core.LotCamera.TopLeft => Vector2.Zero,
                SimsSDL.Core.LotCamera.TopRight => new Vector2(height, 0f),
                SimsSDL.Core.LotCamera.BottomRight => new Vector2(width, height),
                _ => new Vector2(0f, width),
            };
            origin = Scene.Camera.ProjectedPoint(corner, lotSize) - new Vector2(first.X, first.Y);
        }
        else
        {
            return null;
        }

        // Float coordinates are in tile units, supporting 1/16th sub-tile positions.
        Vector2 projected = Scene.Camera.ProjectedPoint(new Vector2(x, y), lotSize);
        return viewport.Origin + (projected - origin) * viewport.Scale;
    }

    public Vector2? CentreGroundTile(int x, int y, Vector2 canvas)
    {
        if (ScreenPoint(x, y, canvas) is not { } point || Viewport(canvas) is not { } viewport)
        {
            return null;
        }

        Pan += viewport.Size / 2f - point;
        return ScreenPoint(x, y, canvas);
    }

    public string GroundProjectionInfo(Vector2 canvas)
    {
        if (Scene is null || Viewport(canvas) is not { } viewport)
        {
            return "scene or viewport missing";
        }

        string originX = Scene.OriginX?.ToString() ?? "nil";
        string originY = Scene.OriginY?.ToString() ?? "nil";
        return $"lot {Scene.LotWidth ?? -1}x{Scene.LotHeight ?? -1}, camera {(int)Scene.Camera}, origin {originX},{originY}, viewport {viewport.Size.X}x{viewport.Size.Y}";
    }

    public SimsSDL.Core.LotVertex? LotVertexAt(Vector2 point, Vector2 canvas)
    {
        if (Scene is null || LotVertexPicker.Create(Scene) is not { } picker ||
            PickScenePoint(point, canvas) is not { } location)
        {
            return null;
        }

        return picker.Vertex(location);
    }

    public SimsSDL.Core.SceneViewport? SceneViewport(Vector2 canvas) => Viewport(canvas);

    private void SetPlacement(
        TerrainVertex[] vertices,
        PlacementPreview.GhostData? ghost = null,
        (int Width, int Height, bool Depth8, byte[] Pixels)[]? pixels = null)
    {
        _placementVertices = vertices;
        _placementGhost = ghost;
        _ghostPixels = pixels ?? [];
        RetireDistinctTextures(_ghostTextures);
        _ghostTextures = [];
        Retire(_placementBuffer);
        _placementBuffer = null;
        _placementDirty = true;
        if (_floorBrushVertices.Length > 0 || _floorBrushBuffer is not null)
        {
            _floorBrushVertices = [];
            Retire(_floorBrushBuffer);
            _floorBrushBuffer = null;
            _floorBrushDirty = true;
        }
    }

    private static byte[] BleedFloorBitmap(SimsBitmap bitmap)
    {
        var bytes = (byte[])bitmap.Rgba.Clone();
        int width = bitmap.Width;
        int height = bitmap.Height;

        for (int i = 0; i + 3 < bytes.Length; i += 4)
        {
            if (bytes[i] == 255 && bytes[i + 1] == 0 && bytes[i + 2] == 255)
            {
                bytes[i + 3] = 0;
            }
        }

        int firstOpaqueRow = -1;
        int lastOpaqueRow = -1;
        for (int py = 0; py < height; py++)
        {
            int rowStart = py * width;
            int firstX = -1;
            int lastX = -1;
            for (int px = 0; px < width; px++)
            {
                if (bytes[(rowStart + px) * 4 + 3] == 0)
                {
                    continue;
                }

                if (firstX < 0)
                {
                    firstX = px;
                }

                lastX = px;
            }

            if (firstX < 0)
            {
                continue;
            }

            if (firstOpaqueRow < 0)
            {
                firstOpaqueRow = py;
            }

            lastOpaqueRow = py;

            int leftAt = (rowStart + firstX) * 4;
            for (int px = 0; px < firstX; px++)
            {
                int dst = (rowStart + px) * 4;
                bytes[dst] = bytes[leftAt];
                bytes[dst + 1] = bytes[leftAt + 1];
                bytes[dst + 2] = bytes[leftAt + 2];
                bytes[dst + 3] = 255;
            }

            int rightAt = (rowStart + lastX) * 4;
            for (int px = lastX + 1; px < width; px++)
            {
                int dst = (rowStart + px) * 4;
                bytes[dst] = bytes[rightAt];
                bytes[dst + 1] = bytes[rightAt + 1];
                bytes[dst + 2] = bytes[rightAt + 2];
                bytes[dst + 3] = 255;
            }
        }

        if (firstOpaqueRow > 0)
        {
            int srcStart = firstOpaqueRow * width * 4;
            int rowBytes = width * 4;
            for (int py = 0; py < firstOpaqueRow; py++)
            {
                Buffer.BlockCopy(bytes, srcStart, bytes, py * rowBytes, rowBytes);
            }
        }

        if (lastOpaqueRow >= 0 && lastOpaqueRow < height - 1)
        {
            int srcStart = lastOpaqueRow * width * 4;
            int rowBytes = width * 4;
            for (int py = lastOpaqueRow + 1; py < height; py++)
            {
                Buffer.BlockCopy(bytes, srcStart, bytes, py * rowBytes, rowBytes);
            }
        }

        return SpritePixels.PremultipliedSRGB(bytes);
    }

    public void UpdateWallBrush(WallBrushSelection? line)
    {
        if (line is null || Scene is null)
        {
            SetPlacement([]);
            return;
        }

        Vector4 color = line.Deleting ? new Vector4(0.35f, 0.12f, 0.02f, 0.4f) : new Vector4(0.06f, 0.12f, 0.32f, 0.4f);
        List<LotScene.TerrainVertex> geometry = line.Geometry(Scene);
        var vertices = new TerrainVertex[geometry.Count];
        for (int i = 0; i < geometry.Count; i++)
        {
            vertices[i] = new TerrainVertex
            {
                Position = new Vector2(geometry[i].X, geometry[i].Y),
                Color = color,
            };
        }

        SetPlacement(vertices);
    }

    public void UpdateFloorBrush(FloorBrushSelection? rectangle, bool deleting = false, string? textureFile = null)
    {
        if (rectangle is null || Scene is null)
        {
            SetPlacement([]);
            return;
        }

        if (!deleting && !rectangle.Water && textureFile is not null && SceneRoot is not null)
        {
            string root = ResolveRealPath(SceneRoot);
            string file = ResolveRealPath(Path.Combine(
                root,
                textureFile.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
            string? fileDir = Path.GetDirectoryName(file);
            StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (fileDir is not null && string.Equals(Path.TrimEndingDirectorySeparator(fileDir), root, cmp) && File.Exists(file))
            {
                List<(LotScene.TerrainVertex Vertex, Vector2 UV)> textured = rectangle.TexturedGeometry(Scene);
                SetPlacement([]);
                var brushVerts = new AvatarVertex[textured.Count];
                for (int i = 0; i < textured.Count; i++)
                {
                    var item = textured[i];
                    brushVerts[i] = new AvatarVertex
                    {
                        Position = new Vector4(item.Vertex.X, item.Vertex.Y, 1f, 1f),
                        Uv = item.UV,
                        Normal = new Vector4(0f, 1f, 0f, 0f),
                    };
                }

                _floorBrushVertices = brushVerts;
                if (!string.Equals(_floorBrushTextureKey, file, cmp) || (_floorBrushTexture is null && _floorBrushPixels is null))
                {
                    var bitmap = new SimsBitmap(File.ReadAllBytes(file));
                    _floorBrushPixels = (bitmap.Width, bitmap.Height, false, BleedFloorBitmap(bitmap));
                    _floorBrushTextureKey = file;
                }

                _floorBrushDirty = true;
                return;
            }
        }

        Vector4 color = deleting ? new Vector4(0.35f, 0.12f, 0.02f, 0.4f) : new Vector4(0.06f, 0.12f, 0.32f, 0.4f);
        List<LotScene.TerrainVertex> geometry = rectangle.Geometry(Scene);
        var vertices = new TerrainVertex[geometry.Count];
        for (int i = 0; i < geometry.Count; i++)
        {
            vertices[i] = new TerrainVertex
            {
                Position = new Vector2(geometry[i].X, geometry[i].Y),
                Color = color,
            };
        }

        SetPlacement(vertices);
    }

    public void UpdatePlacement(PlacementPreview? preview)
    {
        if (preview is null || Scene is null || SceneRoot is null)
        {
            PlacementIsValid = null;
            SetPlacement([]);
            return;
        }

        PlacementIsValid = preview.Valid;
        Vector4 color = preview.Valid ? new Vector4(0.04f, 0.35f, 0.04f, 0.4f) : new Vector4(0.4f, 0.02f, 0.02f, 0.4f);
        List<LotScene.TerrainVertex> geometry = preview.Geometry(Scene);
        var vertices = new TerrainVertex[geometry.Count];
        for (int i = 0; i < geometry.Count; i++)
        {
            vertices[i] = new TerrainVertex
            {
                Position = new Vector2(geometry[i].X, geometry[i].Y),
                Color = color,
            };
        }

        if (preview.Ghost is not { } ghost)
        {
            SetPlacement(vertices);
            return;
        }

        List<byte[]> data = LotScene.ReadPixels(ghost.Scene.Textures, ResolveRealPath(SceneRoot));
        var prepared = new (int Width, int Height, bool Depth8, byte[] Pixels)[ghost.Scene.Textures.Count];
        for (int i = 0; i < ghost.Scene.Textures.Count; i++)
        {
            prepared[i] = PrepareUpload(ghost.Scene.Textures[i], data[i]);
        }

        SetPlacement(vertices, ghost, prepared);
    }

    public void UpdateBuildCursor(PlacementPreview.GhostData? ghost)
    {
        if (ghost is null || SceneRoot is null)
        {
            BuildCursor = null;
            _buildCursorPixels = null;
            RetireDistinctTextures(_buildCursorTextures);
            _buildCursorTextures = [];
            return;
        }

        ghost.Validate();
        List<byte[]> data = LotScene.ReadPixels(ghost.Scene.Textures, ResolveRealPath(SceneRoot));
        var prepared = new (int Width, int Height, bool Depth8, byte[] Pixels)[ghost.Scene.Textures.Count];
        for (int i = 0; i < ghost.Scene.Textures.Count; i++)
        {
            prepared[i] = PrepareUpload(ghost.Scene.Textures[i], data[i]);
        }

        _buildCursorPixels = prepared;
        RetireDistinctTextures(_buildCursorTextures);
        _buildCursorTextures = [];
        BuildCursor = ghost;
    }

    public void UpdateWallPaintPreview(PlacementPreview.GhostData? ghost)
    {
        if (ghost is null || SceneRoot is null)
        {
            WallPaintPreview = null;
            _wallPaintPixels = null;
            RetireDistinctTextures(_wallPaintTextures);
            _wallPaintTextures = [];
            return;
        }

        ghost.Validate(wallSurface: true);
        List<byte[]> data = LotScene.ReadPixels(ghost.Scene.Textures, ResolveRealPath(SceneRoot));
        var prepared = new (int Width, int Height, bool Depth8, byte[] Pixels)[ghost.Scene.Textures.Count];
        for (int i = 0; i < ghost.Scene.Textures.Count; i++)
        {
            prepared[i] = PrepareUpload(ghost.Scene.Textures[i], data[i]);
        }

        _wallPaintPixels = prepared;
        RetireDistinctTextures(_wallPaintTextures);
        _wallPaintTextures = [];
        WallPaintPreview = ghost;
    }

    public int? ObjectAt(Vector2 point, Vector2 canvas)
    {
        LotZoom zoom = EffectiveVisualZoom;
        if (_cachedObjectHit is { } cached &&
            cached.Point == point &&
            cached.Canvas == canvas &&
            cached.Zoom == zoom &&
            cached.VisualZoomFactor == _visualZoomFactor &&
            cached.Pan == _pan &&
            cached.Revision == _sceneRevision &&
            cached.Sequence == _animationSequence)
        {
            return cached.Result;
        }

        if (PickScenePoint(point, canvas) is not { } location)
        {
            return null;
        }

        int? hit = EnsurePicker()?.ObjectAt(location, zoom);
        _cachedObjectHit = new CachedObjectHit(point, canvas, zoom, _visualZoomFactor, _pan, _sceneRevision, _animationSequence, hit);
        return hit;
    }

    public LotScene.WallSide? WallAt(Vector2 point, Vector2 canvas)
    {
        if (PickScenePoint(point, canvas) is not { } location)
        {
            return null;
        }

        return EnsurePicker()?.WallAt(location, EffectiveVisualZoom);
    }

    public void UpdateAppearancePreview(SimsSDL.Core.HouseholdAppearancePreview preview, IReadOnlyList<byte[]> pixels, int key = 0)
    {
        preview.Validate();
        if (pixels.Count != preview.Textures.Count)
        {
            throw new ContentError("Household preview texture data does not match its manifest");
        }

        var uploadBatch = new (int Width, int Height, bool Depth8, byte[] Pixels)[preview.Textures.Count];
        var mips = new bool[preview.Textures.Count];
        for (int i = 0; i < preview.Textures.Count; i++)
        {
            LotScene.Texture metadata = preview.Textures[i];
            uploadBatch[i] = (metadata.Width, metadata.Height, false, SpritePixels.PremultipliedSRGB(pixels[i]));
            mips[i] = true;
        }

        IReadOnlyList<GPUTexture> uploaded = GPU.UploadTextures(uploadBatch, mips);
        float minX = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float minY = float.PositiveInfinity;
        float maxY = float.NegativeInfinity;
        bool hasVertex = false;

        List<GPUTexture> meshTextures = new(preview.Meshes.Count);
        List<int> vertexCounts = new(preview.Meshes.Count);
        foreach (LotScene.Mesh mesh in preview.Meshes)
        {
            meshTextures.Add(uploaded[mesh.Texture]);
            vertexCounts.Add(mesh.Vertices.Count);
            foreach (LotScene.MeshVertex v in mesh.Vertices)
            {
                hasVertex = true;
                if (v.X < minX) minX = v.X;
                if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y;
                if (v.Y > maxY) maxY = v.Y;
            }
        }

        var bounds = hasVertex
            ? new CGRect(minX, minY, maxX - minX, maxY - minY)
            : new CGRect(0, 0, 1, 1);

        if (_previews.TryGetValue(key, out PreviewMeshes? existingPreview))
        {
            RetirePreview(existingPreview);
        }

        _previews[key] = new PreviewMeshes
        {
            Textures = meshTextures,
            Buffers = MakeMeshes(preview.Meshes),
            VertexCounts = vertexCounts,
            Bounds = bounds,
        };
    }

    public void ClearAppearancePreview(int? key = null)
    {
        if (key is int k)
        {
            if (_previews.Remove(k, out PreviewMeshes? removed))
            {
                RetirePreview(removed);
            }
        }
        else
        {
            foreach (PreviewMeshes preview in _previews.Values)
            {
                RetirePreview(preview);
            }

            _previews.Clear();
        }
    }

    /// <summary>Renders the lot followed by the overlay, splitting the overlay to preserve color while
    /// clearing depth for character preview meshes.</summary>
    public void Encode(
        nint command,
        GPUTexture color,
        GPUTexture depth,
        GPUTexture? msaaColor = null,
        GPUTexture? msaaDepth = null)
    {
        var canvas = new Vector2(color.Width, color.Height);
        UpdateAmbientMaps(canvas, command);

        bool previewing = PreviewPlacement is { } pp && pp.Rects.Keys.Any(k => _previews.ContainsKey(k));
        int split = previewing ? Math.Min(Overlay.Count, Math.Max(0, PreviewPlacement!.Value.Index + 1)) : Overlay.Count;
        bool needsAmbient = DrawsLot && !HasAdjacentNeighborhood && AmbientEnabled && Environment is not null && Viewport(canvas) is not null;
        bool needsCensor = DrawsLot && Scene?.CensorRects is { Count: > 0 } && Viewport(canvas) is not null;
        bool useMsaa = msaaColor is not null
            && msaaDepth is not null
            && msaaColor.Width == color.Width
            && msaaColor.Height == color.Height
            && msaaDepth.Width == depth.Width
            && msaaDepth.Height == depth.Height
            && msaaColor.SampleCount > SDL_GPU_SAMPLECOUNT_1
            && msaaColor.SampleCount == msaaDepth.SampleCount
            && !needsAmbient
            && !HasAdjacentNeighborhood;
        int passSamples = useMsaa ? msaaColor!.SampleCount : SDL_GPU_SAMPLECOUNT_1;
        int activePassSamples = passSamples;
        GPUTexture targetColor = useMsaa ? msaaColor! : color;
        GPUTexture targetDepth = useMsaa ? msaaDepth! : depth;
        bool resolveLotPassImmediately = useMsaa && (!previewing || needsCensor);
        GPUTexture? initialResolve = resolveLotPassImmediately ? color : null;

        nint pass = BeginPass(
            command,
            targetColor,
            targetDepth,
            clearColor: true,
            resolveColor: initialResolve,
            discardDepth: resolveLotPassImmediately);
        try
        {
            if (DrawsLot)
            {
                if (HasAdjacentNeighborhood)
                {
                    EncodeConnectedNeighborhood(
                        command,
                        canvas,
                        targetColor,
                        targetDepth,
                        initialResolve,
                        passSamples,
                        deferTranslucentAvatars: needsAmbient,
                        ref pass);
                }
                else
                {
                    EncodeLot(pass, command, canvas, passSamples, deferTranslucentAvatars: needsAmbient);
                }
            }

            if (needsAmbient && Viewport(canvas) is { } viewport)
            {
                SDL_EndGPURenderPass(pass);
                pass = 0;

                GPUTexture ground = GroundTexture(color.Width, color.Height);
                EncodeGroundDepth(command, ground, viewport, canvas);
                var (heightMap, footMap, worldDepth, worldShadowViewport) = EnsureWorldShadowMaps(command, depth, viewport, canvas);
                EncodeAmbient(command, color, depth, ground, heightMap, footMap.Texture, worldDepth, worldShadowViewport, viewport, canvas);
                if (HasTranslucentAvatars())
                {
                    pass = BeginPass(command, color, depth, clearColor: false, clearDepth: false);
                    EncodeTranslucentAvatars(pass, command, canvas, SDL_GPU_SAMPLECOUNT_1);
                    if (Scene?.CensorRects is { Count: > 0 })
                    {
                        SDL_EndGPURenderPass(pass);
                        pass = 0;
                    }
                    else
                    {
                        activePassSamples = SDL_GPU_SAMPLECOUNT_1;
                    }
                }

                if (Scene?.CensorRects is { Count: > 0 } censors)
                {
                    EncodeCensorRects(censors, command, color, depth, viewport, canvas);
                }

                if (pass == 0)
                {
                    pass = BeginPass(command, color, depth, clearColor: false);
                    activePassSamples = SDL_GPU_SAMPLECOUNT_1;
                }
            }
            else if (needsCensor && Scene?.CensorRects is { Count: > 0 } censors && Viewport(canvas) is { } censorViewport)
            {
                SDL_EndGPURenderPass(pass);
                pass = 0;

                if (useMsaa && ComputeCensorScissor(censors, censorViewport, color.Width, color.Height) is { } censorScissor)
                {
                    nint censorDepthPass = BeginPass(command, SurfaceTexture(color.Width, color.Height), depth, clearColor: true);
                    try
                    {
                        EncodeLot(censorDepthPass, command, canvas, SDL_GPU_SAMPLECOUNT_1, censorScissor);
                    }
                    finally
                    {
                        SDL_EndGPURenderPass(censorDepthPass);
                    }
                }

                EncodeCensorRects(censors, command, color, depth, censorViewport, canvas);
                pass = BeginPass(command, color, depth, clearColor: false);
                activePassSamples = SDL_GPU_SAMPLECOUNT_1;
            }

            EncodeOverlay(CollectionsMarshal.AsSpan(Overlay).Slice(0, split), pass, command, canvas, activePassSamples);
            if (previewing && PreviewPlacement is { } placement)
            {
                SDL_EndGPURenderPass(pass);
                pass = 0;

                bool previewMsaa = useMsaa && !needsCensor;
                int previewSamples = previewMsaa ? passSamples : SDL_GPU_SAMPLECOUNT_1;
                pass = BeginPass(
                    command,
                    previewMsaa ? targetColor : color,
                    previewMsaa ? targetDepth : depth,
                    clearColor: false,
                    resolveColor: previewMsaa ? color : null,
                    discardDepth: previewMsaa);
                foreach (var (key, rect) in placement.Rects.OrderBy(kv => kv.Key))
                {
                    if (_previews.TryGetValue(key, out PreviewMeshes? preview))
                    {
                        EncodePreview(preview, rect, pass, command, canvas, previewSamples);
                    }
                }

                EncodeOverlay(CollectionsMarshal.AsSpan(Overlay).Slice(split), pass, command, canvas, previewSamples);
            }
        }
        finally
        {
            if (pass != 0)
            {
                SDL_EndGPURenderPass(pass);
            }
        }
    }

    private unsafe nint BeginPass(
        nint command,
        GPUTexture color,
        GPUTexture depth,
        bool clearColor,
        GPUTexture? resolveColor = null,
        bool discardDepth = false,
        bool clearDepth = true)
    {
        var clear = new SDL_FColor { r = 0f, g = 0f, b = OriginalUIBlue, a = 1f };
        if (DrawsLot && Scene?.Lighting?.Backdrop is { } backdrop)
        {
            static float Linear(byte value) => (float)Math.Pow((value / 255.0 + 0.055) / 1.055, 2.4);
            clear = new SDL_FColor { r = Linear(backdrop.R), g = Linear(backdrop.G), b = Linear(backdrop.B), a = 1f };
        }

        var colorTarget = new SDL_GPUColorTargetInfo
        {
            texture = color.Handle,
            clear_color = clear,
            load_op = clearColor ? SDL_GPU_LOADOP_CLEAR : SDL_GPU_LOADOP_LOAD,
            store_op = resolveColor is not null ? SDL_GPU_STOREOP_RESOLVE : SDL_GPU_STOREOP_STORE,
            resolve_texture = resolveColor?.Handle ?? nint.Zero,
        };

        var depthTarget = new SDL_GPUDepthStencilTargetInfo
        {
            texture = depth.Handle,
            clear_depth = 1f,
            load_op = clearDepth ? SDL_GPU_LOADOP_CLEAR : SDL_GPU_LOADOP_LOAD,
            store_op = discardDepth ? SDL_GPU_STOREOP_DONT_CARE : SDL_GPU_STOREOP_STORE,
            stencil_load_op = SDL_GPU_LOADOP_DONT_CARE,
            stencil_store_op = SDL_GPU_STOREOP_DONT_CARE,
        };

        nint pass = SDL_BeginGPURenderPass(command, &colorTarget, 1, &depthTarget);
        if (pass == 0)
        {
            throw new SDLError("SDL_BeginGPURenderPass");
        }

        return pass;
    }

    public void ApplyEnvironment(PluginEnvironment? value)
    {
        if (!string.Equals(Environment?.Scope, value?.Scope, StringComparison.Ordinal) ||
            !string.Equals(Environment?.PackId, value?.PackId, StringComparison.Ordinal) ||
            !string.Equals(Environment?.AssetRoot, value?.AssetRoot, StringComparison.Ordinal))
        {
            _lastPrint = 0;
            _pendingPrints.Clear();
            _trailKey = string.Empty;
        }

        Environment = value;
        PluginEnvironment.EffectData? effect = value?.Effect;
        IReadOnlyDictionary<string, float>? parameters = effect?.Parameters;
        float Param(string key, float fallback = 0f) =>
            parameters is not null && parameters.TryGetValue(key, out float v) ? v : fallback;

        Ambient.Radius = Param("radius");
        Ambient.Occlusion = Param("occlusion");
        Ambient.Shadow = Param("shadow");
        Ambient.Thickness = Param("thickness");
        Ambient.Reach = Param("reach");
        Ambient.ContactRadius = Param("contactRadius");
        Ambient.Contact = Param("contact");
        Ambient.HeightTexels = Param("heightTexels", 16f);
        Ambient.GroundOffset = Param("groundOffset");
        Ambient.Sun = new Vector3(Param("sunUp", 1f), Param("sunToward"), Param("sunRight"));
        Ambient.LampReach = Param("lampReach");
        Ambient.LampGain = Param("lampGain");
        Ambient.WindowSun = Param("windowSun");
        Ambient.CloudShade = Param("cloudShade");
        Ambient.Wind = new Vector2(Param("windX"), Param("windY"));
        Ambient.CloudHeight = Param("cloudHeight");
        Ambient.Tint = Param("tint");
        Ambient.SurfaceMode = effect?.SurfaceMode ?? 0;
        Ambient.SurfaceAmount = effect?.SurfaceAmount ?? 0f;
        Ambient.CloudCover = effect?.CloudCover ?? 0f;
        Ambient.Rain = effect?.Precipitation ?? 0f;
        Ambient.Wetness = effect?.Wetness ?? 0f;
        Ambient.GrassQuality = Param("grassQuality", Param("grass"));
        Ambient.SpringBloom = Param("springBloom");
        Ambient.Snowfall = Param("snowfall");
        Ambient.Fireflies = Param("fireflies");
        Ambient.Hail = Param("hail");
        Ambient.Frost = Param("frost");
        Ambient.Thunder = Param("thunder");

        var nextShaders = new Dictionary<string, string>();
        if (value is not null && effect is not null)
        {
            foreach (var (key, shaderPath) in effect.Shaders)
            {
                string stage = key is "groundDepth" or "heightScatter" or "fullscreen" ? ".vert" : ".frag";
                string assetPath = value.Asset(shaderPath);
                if (!GPU.SupportsShaderAsset(assetPath))
                {
                    AppLog.Warn($"Skipping environment shader '{key + stage}' at '{assetPath}': no compiled bytecode matching active GPU backend ({GPU.DriverName}).");
                    nextShaders.Clear();
                    break;
                }
                nextShaders[key + stage] = assetPath;
            }
        }

        if (GPU.EffectShaders.Count != nextShaders.Count ||
            nextShaders.Any(kv => !GPU.EffectShaders.TryGetValue(kv.Key, out string? existing) || !string.Equals(existing, kv.Value, StringComparison.Ordinal)))
        {
            GPU.EffectShaders = nextShaders;
        }

        if (effect is not null)
        {
            foreach (PluginEnvironment.EffectData.Footprint mark in effect.Footprints)
            {
                if (mark.Sequence > _lastPrint)
                {
                    _pendingPrints.Add(new Vector4(mark.X, mark.Y, mark.Heading, mark.Foot));
                    _lastPrint = mark.Sequence;
                }
            }
        }

        if (_pendingPrints.Count > 128)
        {
            _pendingPrints.RemoveRange(0, _pendingPrints.Count - 128);
        }
    }

    private GPUTexture HeightTexture(int width, int height, bool feet = false)
    {
        GPUTexture? cached = feet ? _footMapTexture : _heightMapTexture;
        if (cached is not null && cached.Width == width && cached.Height == height)
        {
            return cached;
        }

        GPUTexture texture = GPU.MakeTexture(
            width,
            height,
            GPUContext.DepthFormat,
            SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER);
        Retire(cached);
        if (feet)
        {
            _footMapTexture = texture;
        }
        else
        {
            _heightMapTexture = texture;
        }

        return texture;
    }

    private GPUTexture? MakeOverheadMap(LotScene scene)
    {
        if (scene.OverheadMap is not { } map)
        {
            return null;
        }

        return GPU.UploadDataTexture(map.Width, map.Height, map.Pixels);
    }

    private GPUTexture EmptyOverheadMap()
    {
        if (_emptyOverhead is not null)
        {
            return _emptyOverhead;
        }

        GPUTexture texture = GPU.UploadDataTexture(1, 1, [0, 128, 0, 0]);
        _emptyOverhead = texture;
        return texture;
    }

    private GPUTexture EmptyIndoorMask()
    {
        if (_emptyMask is not null)
        {
            return _emptyMask;
        }

        GPUTexture texture = GPU.UploadTextures([(1, 6, true, new byte[] { 0, 0, 0, 0, 0, 0 })])[0];
        _emptyMask = texture;
        return texture;
    }

    /// <summary>Encodes indoor rooms and sunlight penetration into six texture bands covering two floors:
    /// room IDs and window flags, floor altitudes, and diagonal half-tile masks. Room bytes reserve bit 0
    /// for windows with room IDs in higher bits, altitude measures eighths of a tile, and half bytes
    /// identify which screen half is indoor.</summary>
    // Half-band bits 0x80 indicate cement and 0x40 indicate off-lot roads.
    private GPUTexture? MakeIndoorMask(LotScene scene)
    {
        if (scene.LotWidth is not int width || scene.LotHeight is not int height ||
            scene.Lighting is not { } lighting || LotFrame(scene) is not { } frame)
        {
            return null;
        }

        int orientation = ((scene.CameraOrientation ?? 0) % 4 + 4) % 4;
        int margin = (int)_heightMargin;
        int columns = (orientation % 2 == 0 ? width : height) + 2 * margin;
        int rows = (orientation % 2 == 0 ? height : width) + 2 * margin;
        var mask = new byte[columns * rows * 6];

        (int X, int Y, int Index)? TryCell(int x, int y, int level)
        {
            if (x < 0 || x >= width || y < 0 || y >= height || (level != 1 && level != 2))
            {
                return null;
            }

            (int cx, int cy) = orientation switch
            {
                0 => (x, y),
                1 => (height - 1 - y, x),
                2 => (width - 1 - x, height - 1 - y),
                _ => (y, width - 1 - x),
            };
            return (cx, cy, ((level - 1) * rows + cy + margin) * columns + cx + margin);
        }

        List<(int Index, float Altitude)> altitudes = [];
        HashSet<int> seenAltitudes = [];
        Dictionary<int, byte> halves = new();
        HashSet<int> whole = [];
        Dictionary<int, byte> outdoorHalves = new();
        HashSet<int> outdoorWhole = [];

        foreach (LotScene.Mesh mesh in scene.Meshes ?? [])
        {
            if (mesh.Floor is not { } floor || TryCell(floor.X, floor.Y, floor.Level ?? 1) is not { } at)
            {
                continue;
            }

            // Tile centers sit 16*(x+y+1) px below frame, minus 40 px per altitude tile.
            if (mesh.Vertices.Count > 0 && seenAltitudes.Add(at.Index + 2 * rows * columns))
            {
                LotScene.MeshVertex v0 = mesh.Vertices[0];
                altitudes.Add((at.Index + 2 * rows * columns, (frame.Y + 16f * (at.X + at.Y) + 32f * v0.V - v0.Y) / 40f));
            }

            if (mesh.Room is not int room || room < 0 || room >= lighting.Rooms.Count || lighting.Rooms[room].Outside)
            {
                if (floor.Pattern == 12)
                {
                    mask[at.Index + 4 * rows * columns] |= 0x80;
                }

                if (floor.Pattern is >= 9 and <= 11)
                {
                    mask[at.Index + 4 * rows * columns] |= 0x40;
                }

                if (floor.Pattern != 12 && floor.Pattern is not (>= 9 and <= 11))
                {
                    if (floor.Half == 0 || outdoorHalves.ContainsKey(at.Index))
                    {
                        outdoorWhole.Add(at.Index);
                    }
                    else if (floor.Half is >= 1 and <= 4)
                    {
                        outdoorHalves[at.Index] = (byte)floor.Half;
                    }
                }

                continue;
            }

            mask[at.Index] = (byte)(((room % 127 + 1) * 2) | (mask[at.Index] & 1));
            if (floor.Half == 0 || halves.ContainsKey(at.Index))
            {
                whole.Add(at.Index);
            }
            else
            {
                halves[at.Index] = (byte)floor.Half;
            }
        }

        foreach (var (index, half) in halves)
        {
            if (!whole.Contains(index))
            {
                mask[index + 4 * rows * columns] |= (byte)(half & 7);
            }
        }

        foreach (var (index, half) in outdoorHalves)
        {
            if (mask[index] < 2 && !outdoorWhole.Contains(index))
            {
                mask[index + 4 * rows * columns] |= (byte)(half & 7);
            }
        }

        bool anyIndoor = false;
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] >= 2)
            {
                anyIndoor = true;
                break;
            }
        }

        if (!anyIndoor)
        {
            return null;
        }

        if (lighting.Windows is not null)
        {
            foreach (SimsSDL.Core.SceneLighting.Window window in lighting.Windows)
            {
                if (TryCell(window.X, window.Y, window.Level) is { } at)
                {
                    mask[at.Index] |= 1;
                }
            }
        }

        _indoorBase = altitudes.Count > 0 ? altitudes.Min(a => a.Altitude) : 0f;
        foreach (var (index, altitude) in altitudes)
        {
            mask[index] = (byte)MathF.Max(1f, MathF.Min(255f, MathF.Round((altitude - _indoorBase) * 8f, MidpointRounding.AwayFromZero) + 1f));
        }

        IReadOnlyList<GPUTexture> uploaded = GPU.UploadTextures([(columns, rows * 6, true, mask)]);
        return uploaded.Count > 0 ? uploaded[0] : null;
    }

    private static Vector2? LotFrame(LotScene scene)
    {
        if (scene.OriginX is float x && scene.OriginY is float y)
        {
            return new Vector2(34f - x, 158f - y);
        }

        if (scene.LotWidth is not int width || scene.LotHeight is not int height ||
            scene.TerrainVertices is not { Count: > 0 } vertices)
        {
            return null;
        }

        LotScene.TerrainVertex first = vertices[0];
        Vector2 corner = (((scene.CameraOrientation ?? 0) % 4 + 4) % 4) switch
        {
            0 => new Vector2(0f, 0f),
            1 => new Vector2(height, 0f),
            2 => new Vector2(width, height),
            _ => new Vector2(0f, width),
        };
        return new Vector2(first.X - 32f * (corner.X - corner.Y), first.Y - 16f * (corner.X + corner.Y));
    }

    private unsafe ((GPUTexture Texture, Vector4? Frame) HeightMap, (GPUTexture Texture, Vector4? Frame) FootMap, GPUTexture WorldDepth, SimsSDL.Core.SceneViewport? WorldViewport) EnsureWorldShadowMaps(
        nint command,
        GPUTexture screenDepth,
        SimsSDL.Core.SceneViewport screenViewport,
        Vector2 screenCanvas)
    {
        if (Scene is not { } scene || LotFrame(scene) is not { } frame ||
            scene.LotWidth is not int width || scene.LotHeight is not int height)
        {
            var hm = EncodeHeightMap(command, screenDepth, screenViewport, screenCanvas);
            var fm = EncodeHeightMap(command, screenDepth, screenViewport, screenCanvas, feet: true);
            return (hm, fm, screenDepth, null);
        }

        const int shadowW = 2048;
        const int shadowH = 1280;
        var shadowCanvas = new Vector2(shadowW, shadowH);
        bool swapped = (scene.CameraOrientation ?? 0) % 2 != 0;
        float w = swapped ? height : width;
        float h = swapped ? width : height;
        float minX = frame.X - 32f * h - 128f;
        float spanX = 32f * (w + h) + 256f;
        float minY = frame.Y - 400f;
        float spanY = 16f * (w + h) + 528f;
        float shadowScale = MathF.Min(shadowW / spanX, shadowH / spanY);
        var shadowViewport = new SimsSDL.Core.SceneViewport(shadowCanvas, shadowScale, new Vector2(-minX * shadowScale, -minY * shadowScale));

        string key = $"{scene.House}:{_sceneRevision}:{scene.CameraOrientation}:{ViewLevel}:{RoofsVisible}:{_worldShadowAnimSeq}:{Ambient.HeightTexels:0.##}";
        if (_worldShadowDepth is not null
            && _heightMapTexture is not null
            && _footMapTexture is not null
            && ReferenceEquals(_worldShadowScene, scene)
            && string.Equals(_worldShadowKey, key, StringComparison.Ordinal))
        {
            return ((_heightMapTexture, _worldHeightFrame), (_footMapTexture, _worldFootFrame), _worldShadowDepth, _worldShadowViewport);
        }

        if (_worldShadowColor is null || _worldShadowColor.Width != shadowW || _worldShadowColor.Height != shadowH)
        {
            Retire(_worldShadowColor);
            _worldShadowColor = GPU.MakeTexture(shadowW, shadowH, GPUContext.ColorFormat, SDL_GPU_TEXTUREUSAGE_COLOR_TARGET);
        }
        if (_worldShadowDepth is null || _worldShadowDepth.Width != shadowW || _worldShadowDepth.Height != shadowH)
        {
            Retire(_worldShadowDepth);
            _worldShadowDepth = GPU.MakeTexture(shadowW, shadowH, GPUContext.DepthFormat, SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER);
        }

        var colorTarget = new SDL_GPUColorTargetInfo
        {
            texture = _worldShadowColor.Handle,
            load_op = SDL_GPU_LOADOP_DONT_CARE,
            store_op = SDL_GPU_STOREOP_DONT_CARE,
        };
        var depthTarget = new SDL_GPUDepthStencilTargetInfo
        {
            texture = _worldShadowDepth.Handle,
            clear_depth = 1f,
            load_op = SDL_GPU_LOADOP_CLEAR,
            store_op = SDL_GPU_STOREOP_STORE,
            stencil_load_op = SDL_GPU_LOADOP_DONT_CARE,
            stencil_store_op = SDL_GPU_STOREOP_DONT_CARE,
        };
        nint pass = SDL_BeginGPURenderPass(command, &colorTarget, 1, &depthTarget);
        if (pass == 0)
        {
            throw new SDLError("SDL_BeginGPURenderPass");
        }
        try
        {
            EncodeLot(pass, command, shadowCanvas, SDL_GPU_SAMPLECOUNT_1, deferTranslucentAvatars: false, viewportOverride: shadowViewport);
        }
        finally
        {
            SDL_EndGPURenderPass(pass);
        }

        var hmWorld = EncodeHeightMap(command, _worldShadowDepth, shadowViewport, shadowCanvas);
        var fmWorld = EncodeHeightMap(command, _worldShadowDepth, shadowViewport, shadowCanvas, feet: true);
        _worldShadowScene = scene;
        _worldShadowKey = key;
        _worldHeightFrame = hmWorld.Frame;
        _worldFootFrame = fmWorld.Frame;
        _worldShadowViewport = shadowViewport;
        return (hmWorld, fmWorld, _worldShadowDepth, shadowViewport);
    }

    private unsafe (GPUTexture Texture, Vector4? Frame) EncodeHeightMap(
        nint command,
        GPUTexture depth,
        SimsSDL.Core.SceneViewport viewport,
        Vector2 canvas,
        bool feet = false)
    {
        if (Scene is null || LotFrame(Scene) is not { } frame ||
            Scene.LotWidth is not int width || Scene.LotHeight is not int height)
        {
            return (HeightTexture(1, 1, feet), null);
        }

        bool swapped = (Scene.CameraOrientation ?? 0) % 2 != 0;
        float texels = Ambient.HeightTexels;
        float margin = _heightMargin;
        Vector2 grid = new Vector2(swapped ? height : width, swapped ? width : height) + new Vector2(2f * margin);
        GPUTexture texture = HeightTexture((int)(grid.X * texels), (int)(grid.Y * texels), feet);

        var depthTarget = new SDL_GPUDepthStencilTargetInfo
        {
            texture = texture.Handle,
            clear_depth = 1f,
            load_op = SDL_GPU_LOADOP_CLEAR,
            store_op = SDL_GPU_STOREOP_STORE,
            stencil_load_op = SDL_GPU_LOADOP_DONT_CARE,
            stencil_store_op = SDL_GPU_STOREOP_DONT_CARE,
        };

        nint pass = SDL_BeginGPURenderPass(command, null, 0, &depthTarget);
        if (pass == 0)
        {
            throw new SDLError("SDL_BeginGPURenderPass");
        }

        try
        {
            SDL_BindGPUGraphicsPipeline(pass, GPU.Pipeline(GPUContext.Program.HeightScatter, GPUContext.DepthMode.Flat));
            var binding = new SDL_GPUTextureSamplerBinding { texture = depth.Handle, sampler = GPU.PixelSampler };
            SDL_BindGPUVertexSamplers(pass, 0, &binding, 1);

            float stride = MathF.Max(1f, MathF.Floor(0.5f * viewport.Scale));
            float columns = MathF.Ceiling(canvas.X / stride);
            float rows = MathF.Ceiling(canvas.Y / stride);

            Vector4* uniforms = stackalloc Vector4[4];
            uniforms[0] = new Vector4(viewport.Origin.X, viewport.Origin.Y, viewport.Scale, stride);
            uniforms[1] = new Vector4(canvas.X, canvas.Y, columns, feet ? 1f : 0f);
            uniforms[2] = new Vector4(frame.X, frame.Y, texels, margin);
            uniforms[3] = new Vector4(texture.Width, texture.Height, 0f, 0f);
            SDL_PushGPUVertexUniformData(command, 0, (nint)uniforms, (uint)(4 * sizeof(Vector4)));
            SDL_DrawGPUPrimitives(pass, (uint)(columns * rows), 1, 0, 0);
        }
        finally
        {
            SDL_EndGPURenderPass(pass);
        }

        return (texture, new Vector4(frame.X, frame.Y, texels, margin));
    }

    private GPUTexture GroundTexture(int width, int height)
    {
        if (_groundDepthTexture is not null && _groundDepthTexture.Width == width && _groundDepthTexture.Height == height)
        {
            return _groundDepthTexture;
        }

        GPUTexture texture = GPU.MakeTexture(
            width,
            height,
            GPUContext.DepthFormat,
            SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER);
        Retire(_groundDepthTexture);
        _groundDepthTexture = texture;
        return texture;
    }

    private GPUTexture SeasonDepthTexture(int width, int height)
    {
        if (_seasonDepthTexture is not null && _seasonDepthTexture.Width == width && _seasonDepthTexture.Height == height)
        {
            return _seasonDepthTexture;
        }

        GPUTexture texture = GPU.MakeTexture(
            width,
            height,
            GPUContext.DepthFormat,
            SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER);
        Retire(_seasonDepthTexture);
        _seasonDepthTexture = texture;
        return texture;
    }

    private GPUTexture AmbientTexture(int width, int height)
    {
        if (_ambientTermTexture is not null && _ambientTermTexture.Width == width && _ambientTermTexture.Height == height)
        {
            return _ambientTermTexture;
        }

        GPUTexture texture = GPU.MakeTexture(
            width,
            height,
            GPUContext.ColorFormat,
            SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER);
        Retire(_ambientTermTexture);
        _ambientTermTexture = texture;
        return texture;
    }

    private GPUTexture LiftTexture(int width, int height)
    {
        if (_surfaceLiftTexture is not null && _surfaceLiftTexture.Width == width && _surfaceLiftTexture.Height == height)
        {
            return _surfaceLiftTexture;
        }

        GPUTexture texture = GPU.MakeTexture(
            width,
            height,
            GPUContext.LiftFormat,
            SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER);
        Retire(_surfaceLiftTexture);
        _surfaceLiftTexture = texture;
        return texture;
    }

    private GPUTexture EmptyLift()
    {
        if (_emptyLiftTexture is not null)
        {
            return _emptyLiftTexture;
        }

        GPUTexture texture = GPU.MakeTexture(
            1,
            1,
            GPUContext.LiftFormat,
            SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER);
        _emptyLiftTexture = texture;
        return texture;
    }

    /// <summary>Generates a character mask so weather passes exclude occupied ground from snow accumulation
    /// and fallen leaves.</summary>
    private unsafe GPUTexture EncodeSimMask(nint command, GPUTexture depth, SimsSDL.Core.SceneViewport viewport, Vector2 canvas)
    {
        uint usage = SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER;
        List<int> sims = [];
        if (Scene?.Meshes is { } meshes)
        {
            for (int i = 0; i < meshes.Count && i < _meshSlices.Count; i++)
            {
                if (meshes[i].Unlit != true && !IsTranslucentAvatarMesh(meshes[i]) && _meshSlices[i] is not null)
                {
                    sims.Add(i);
                }
            }
        }

        if (Scene is null || sims.Count == 0)
        {
            if (_emptySimMaskTexture is not null)
            {
                return _emptySimMaskTexture;
            }

            GPUTexture empty = GPU.MakeTexture(1, 1, GPUContext.MaskFormat, usage);
            _emptySimMaskTexture = empty;
            return empty;
        }

        GPUTexture mask;
        if (_simMaskTexture is not null && _simMaskTexture.Width == depth.Width && _simMaskTexture.Height == depth.Height)
        {
            mask = _simMaskTexture;
        }
        else
        {
            Retire(_simMaskTexture);
            mask = GPU.MakeTexture(depth.Width, depth.Height, GPUContext.MaskFormat, usage);
            _simMaskTexture = mask;
        }

        var colorTarget = new SDL_GPUColorTargetInfo
        {
            texture = mask.Handle,
            load_op = SDL_GPU_LOADOP_CLEAR,
            store_op = SDL_GPU_STOREOP_STORE,
        };

        var depthTarget = new SDL_GPUDepthStencilTargetInfo
        {
            texture = depth.Handle,
            load_op = SDL_GPU_LOADOP_LOAD,
            store_op = SDL_GPU_STOREOP_STORE,
            stencil_load_op = SDL_GPU_LOADOP_DONT_CARE,
            stencil_store_op = SDL_GPU_STOREOP_DONT_CARE,
        };

        nint pass = SDL_BeginGPURenderPass(command, &colorTarget, 1, &depthTarget);
        if (pass == 0)
        {
            throw new SDLError("SDL_BeginGPURenderPass");
        }

        try
        {
            var scissor = new SDL_Rect { x = 0, y = 0, w = (int)canvas.X, h = (int)canvas.Y };
            SDL_SetGPUScissor(pass, in scissor);
            SDL_BindGPUGraphicsPipeline(pass, GPU.Pipeline(GPUContext.Program.AvatarMask, GPUContext.DepthMode.Mesh));

            nint boundBufferHandle = 0;
            foreach (int index in sims)
            {
                LotScene.Mesh mesh = Scene.Meshes![index];
                if (_meshSlices[index] is not { } slice || slice.Buffer.Handle == nint.Zero)
                {
                    continue;
                }

                var binding = new SDL_GPUTextureSamplerBinding
                {
                    texture = _sceneTextures[mesh.TextureFor(EffectiveVisualZoom)].Handle,
                    sampler = GPU.Sampler,
                };
                SDL_BindGPUFragmentSamplers(pass, 0, &binding, 1);

                if (boundBufferHandle != slice.Buffer.Handle)
                {
                    boundBufferHandle = slice.Buffer.Handle;
                    nint handle = slice.Buffer.Handle;
                    SDL_BindGPUVertexStorageBuffers(pass, 0, &handle, 1);
                }

                var transform = new Quad(new Vector4(viewport.Origin.X, viewport.Origin.Y, viewport.Scale, viewport.Scale), canvas)
                {
                    DepthInterval = new Vector4(BitConverter.UInt32BitsToSingle(slice.FirstVertex), 0f, 0f, 0f),
                };
                if (CharacterDetail == 0)
                {
                    transform.UvRect = new Vector4(1f, transform.UvRect.Y, transform.UvRect.Z, transform.UvRect.W);
                }

                SDL_PushGPUVertexUniformData(command, 0, (nint)(&transform), (uint)sizeof(Quad));
                SDL_DrawGPUPrimitives(pass, slice.VertexCount, 1, 0, 0);
            }
        }
        finally
        {
            SDL_EndGPURenderPass(pass);
        }

        return mask;
    }

    private GPUTexture SurfaceTexture(int width, int height)
    {
        if (_surfaceSourceTexture is not null && _surfaceSourceTexture.Width == width && _surfaceSourceTexture.Height == height)
        {
            return _surfaceSourceTexture;
        }

        GPUTexture texture = GPU.MakeTexture(
            width,
            height,
            GPUContext.ColorFormat,
            SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER);
        Retire(_surfaceSourceTexture);
        _surfaceSourceTexture = texture;
        return texture;
    }

    /// <summary>Renders terrain depth into a separate target because base-level terrain and floors omit
    /// depth writes during the primary lot pass.</summary>
    private unsafe void EncodeGroundDepth(nint command, GPUTexture depth, SimsSDL.Core.SceneViewport viewport, Vector2 canvas)
    {
        if (_terrainBuffer is null || _terrainBuffer.Handle == nint.Zero)
        {
            return;
        }

        var depthTarget = new SDL_GPUDepthStencilTargetInfo
        {
            texture = depth.Handle,
            clear_depth = 1f,
            load_op = SDL_GPU_LOADOP_CLEAR,
            store_op = SDL_GPU_STOREOP_STORE,
            stencil_load_op = SDL_GPU_LOADOP_DONT_CARE,
            stencil_store_op = SDL_GPU_STOREOP_DONT_CARE,
        };

        nint pass = SDL_BeginGPURenderPass(command, null, 0, &depthTarget);
        if (pass == 0)
        {
            throw new SDLError("SDL_BeginGPURenderPass");
        }

        try
        {
            var scissor = new SDL_Rect { x = 0, y = 0, w = (int)canvas.X, h = (int)canvas.Y };
            SDL_SetGPUScissor(pass, in scissor);
            SDL_BindGPUGraphicsPipeline(pass, GPU.Pipeline(GPUContext.Program.GroundDepth, GPUContext.DepthMode.Flat));
            var transform = new Quad(new Vector4(viewport.Origin.X, viewport.Origin.Y, viewport.Scale, viewport.Scale), canvas);
            SDL_PushGPUVertexUniformData(command, 0, (nint)(&transform), (uint)sizeof(Quad));
            nint handle = _terrainBuffer.Handle;
            SDL_BindGPUVertexStorageBuffers(pass, 0, &handle, 1);
            SDL_DrawGPUPrimitives(pass, (uint)_terrainBuffer.Count, 1, 0, 0);
        }
        finally
        {
            SDL_EndGPURenderPass(pass);
        }
    }

    private unsafe void EncodeSeasonDepth(
        nint command,
        GPUTexture color,
        GPUTexture seasonDepth,
        SimsSDL.Core.SceneViewport viewport,
        Vector2 canvas,
        bool displaced)
    {
        if (Scene is not { } scene || _drawItems.Count == 0)
        {
            return;
        }

        var colorTarget = new SDL_GPUColorTargetInfo
        {
            texture = color.Handle,
            load_op = SDL_GPU_LOADOP_LOAD,
            store_op = SDL_GPU_STOREOP_STORE,
        };
        var depthTarget = new SDL_GPUDepthStencilTargetInfo
        {
            texture = seasonDepth.Handle,
            load_op = SDL_GPU_LOADOP_LOAD,
            store_op = SDL_GPU_STOREOP_STORE,
            stencil_load_op = SDL_GPU_LOADOP_DONT_CARE,
            stencil_store_op = SDL_GPU_STOREOP_DONT_CARE,
        };

        nint pass = SDL_BeginGPURenderPass(command, &colorTarget, 1, &depthTarget);
        if (pass == 0)
        {
            throw new SDLError("SDL_BeginGPURenderPass");
        }

        try
        {
            var scissor = new SDL_Rect { x = 0, y = 0, w = (int)canvas.X, h = (int)canvas.Y };
            SDL_SetGPUScissor(pass, in scissor);
            SDL_BindGPUGraphicsPipeline(pass, GPU.Pipeline(GPUContext.Program.SpriteDepth, GPUContext.DepthMode.MeshDepthPrepass));

            float scale = viewport.Scale;
            Vector2 origin = viewport.Origin;
            LotZoom zoom = EffectiveVisualZoom;
            float artTexelScale = scale / zoom.Factor();
            bool settledIntegerUi = MathF.Abs(artTexelScale - MathF.Round(artTexelScale, MidpointRounding.AwayFromZero)) <= 1e-4f
                && !IsZoomInterpolating;
            nint artSampler = settledIntegerUi ? GPU.PixelSampler : GPU.Sampler;
            nint spriteSampler = AntiAliasedSprites ? GPU.Sampler : artSampler;
            bool spriteMinified = scale < zoom.Factor() * MathF.Max(UIScale, 0.01f) - 0.001f;
            nint colorSpriteSampler = AntiAliasedSprites && spriteMinified ? GPU.AnisoSampler : spriteSampler;
            float alphaCutoff = displaced ? 0.68f : 0.18f;
            float modeFlag = AntiAliasedSprites ? 1f : 0f;
            (nint T0, nint T1)? lastBound = null;
            SDL_GPUTextureSamplerBinding* bindings = stackalloc SDL_GPUTextureSamplerBinding[2];

            for (int i = 0; i < _drawItems.Count; i++)
            {
                SceneDrawItem item = _drawItems[i];
                if (item.Kind != SceneDrawItem.ItemKind.Sprite)
                {
                    continue;
                }

                LotScene.Sprite sprite = scene.Sprites[item.Index];
                if (sprite.Wall is not null || (zoom == LotZoom.Far && sprite.HideAtFarZoom == true))
                {
                    continue;
                }

                int texture = sprite.Texture;
                int? depthTexture = sprite.DepthTexture;
                var rect = new Vector4(sprite.X, sprite.Y, sprite.Width, sprite.Height);
                if (sprite.Image(zoom) is { } image)
                {
                    texture = image.Texture;
                    depthTexture = image.DepthTexture;
                    rect = new Vector4(sprite.X + image.X, sprite.Y + image.Y, image.Width, image.Height);
                }

                if (depthTexture is not int dt)
                {
                    continue;
                }

                if (origin.X + (rect.X + rect.Z) * scale < 0f ||
                    origin.Y + (rect.Y + rect.W) * scale < 0f ||
                    origin.X + rect.X * scale >= canvas.X ||
                    origin.Y + rect.Y * scale >= canvas.Y)
                {
                    continue;
                }

                nint t0 = _sceneTextures[texture].Handle;
                nint t1 = _sceneTextures[dt].Handle;
                if (lastBound != (t0, t1))
                {
                    bindings[0] = new SDL_GPUTextureSamplerBinding { texture = t0, sampler = colorSpriteSampler };
                    bindings[1] = new SDL_GPUTextureSamplerBinding { texture = t1, sampler = GPU.PixelSampler };
                    SDL_BindGPUFragmentSamplers(pass, 0, bindings, 2);
                    lastBound = (t0, t1);
                }

                var quad = new Quad(
                    new Vector4(origin.X + rect.X * scale, origin.Y + rect.Y * scale, rect.Z * scale, rect.W * scale),
                    canvas,
                    sprite.Flip)
                {
                    DepthInterval = new Vector4(sprite.DepthNear ?? 0f, sprite.DepthFar ?? 0f, alphaCutoff, modeFlag),
                    Tint = Vector4.One,
                };
                SDL_PushGPUVertexUniformData(command, 0, (nint)(&quad), (uint)sizeof(Quad));
                SDL_DrawGPUPrimitives(pass, 6, 1, 0, 0);
            }
        }
        finally
        {
            SDL_EndGPURenderPass(pass);
        }
    }

    /// <summary>Calculates ambient lighting from scene depth, combining occlusion and sun shadows into an
    /// intermediate target that is blurred and multiplied into scene color.</summary>
    private unsafe void EncodeAmbient(
        nint command,
        GPUTexture color,
        GPUTexture depth,
        GPUTexture ground,
        (GPUTexture Texture, Vector4? Frame) heightMap,
        GPUTexture footMap,
        GPUTexture worldDepth,
        SimsSDL.Core.SceneViewport? worldShadowViewport,
        SimsSDL.Core.SceneViewport viewport,
        Vector2 canvas)
    {
        // View-space basis vectors for the isometric camera in scene pixels.
        var up = new Vector3(0f, -0.8660254f, 0.5f);
        var toward = new Vector3(0f, 0.5f, 0.8660254f);
        var right = new Vector3(1f, 0f, 0f);
        SimsSDL.Core.SceneLighting? lighting = Scene?.Lighting;
        Vector3 sunInLot = Ambient.Sun;
        float shadowStrength = ShadowsEnabled ? Ambient.Shadow : 0f;
        float reach = Ambient.Reach;
        float windowSun = Ambient.WindowSun;

        Vector3 sun = up * sunInLot.X + toward * sunInLot.Y + right * sunInLot.Z;
        sun /= MathF.Max(1e-6f, sun.Length());

        float invSqrt2 = 1f / MathF.Sqrt(2f);
        var sunTiles = new Vector3((sunInLot.Y + sunInLot.Z) * invSqrt2, (sunInLot.Y - sunInLot.Z) * invSqrt2, sunInLot.X);
        sunTiles /= MathF.Max(1e-6f, sunTiles.Length());

        var parameters = Environment?.Effect.Parameters;
        float baseLight = parameters is not null && parameters.TryGetValue("base", out float baseVal) ? baseVal : 1f;
        float glow = parameters is not null && parameters.TryGetValue("glow", out float glowVal) ? glowVal : 0f;
        int level = Scene?.ViewLevel ?? 1;
        float reachOnScreen = Ambient.LampReach * viewport.Scale;

        List<SimsSDL.Core.SceneLighting.Light> lamps = [];
        if (lighting?.Lights is not null)
        {
            foreach (SimsSDL.Core.SceneLighting.Light light in lighting.Lights)
            {
                if (light.Level != level)
                {
                    continue;
                }

                Vector2 screen = viewport.Origin + new Vector2(light.X, light.Y) * viewport.Scale;
                if (screen.X > -reachOnScreen && screen.Y > -reachOnScreen &&
                    screen.X < canvas.X + reachOnScreen && screen.Y < canvas.Y + reachOnScreen)
                {
                    lamps.Add(light);
                }
            }

            lamps.Sort((a, b) => b.Strength.CompareTo(a.Strength));
            if (lamps.Count > MaxLamps)
            {
                lamps.RemoveRange(MaxLamps, lamps.Count - MaxLamps);
            }
        }

        ulong nowTicks = SDL_GetTicks();
        double dt = _ambientTimeTicks is ulong prevTicks && nowTicks >= prevTicks
            ? Math.Min((nowTicks - prevTicks) / 1000.0, 0.25)
            : 0.0;
        _ambientTimeTicks = nowTicks;
        double speedScale = AmbientPaused || Speed <= 0 ? 0.0 : Math.Min(Speed, 10);
        double simElapsed = Ambient.CloudTime is null ? dt * speedScale : 0.0;
        if (Ambient.CloudTime is null)
        {
            _ambientTimeSeconds = (_ambientTimeSeconds + simElapsed) % 3600.0;
        }
        float seconds = Ambient.CloudTime ?? (float)_ambientTimeSeconds;
        Vector2 drift = Ambient.Wind * seconds;
        float orientation = ((Scene?.CameraOrientation ?? 0) % 4 + 4) % 4;
        var lotSize = new Vector2(Scene?.LotWidth ?? 0, Scene?.LotHeight ?? 0);

        Vector2 wind = orientation switch
        {
            1f => new Vector2(-Ambient.Wind.Y, Ambient.Wind.X),
            2f => -Ambient.Wind,
            3f => new Vector2(Ambient.Wind.Y, -Ambient.Wind.X),
            _ => Ambient.Wind,
        };
        float slant = MathF.Min(0.6f, MathF.Max(-0.6f, (wind.X - wind.Y) * 32f * 3f / 900f));
        float windStrength = MathF.Min(1f, Ambient.Wind.Length() / 1.5f);
        bool displaced = Environment?.Effect.DisplacedSurface == true && heightMap.Frame is not null && AmbientDebug == 0;
        float trailState = 0f;
        List<Vector4> prints = [];
        if (displaced)
        {
            _trailFill += (Environment?.Effect.TrailRecovery ?? 0f) * MathF.Min((float)simElapsed, 1f);
            string key = $"{Scene?.House ?? 0}-{lotSize.X}x{lotSize.Y}";
            if (!string.Equals(_trailKey, key, StringComparison.Ordinal))
            {
                RetireDistinctTextures(_trailTextures);
                Vector2 size = (lotSize + new Vector2(2f * TrailMargin)) * TrailTexels;
                _trailTextures =
                [
                    GPU.MakeTexture((int)size.X, (int)size.Y, GPUContext.ColorFormat, SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER),
                    GPU.MakeTexture((int)size.X, (int)size.Y, GPUContext.ColorFormat, SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER),
                ];
                _trailKey = key;
                trailState = -1f;
            }

            int takeCount = Math.Min(MaxPrints, _pendingPrints.Count);
            if (takeCount > 0)
            {
                prints = _pendingPrints.GetRange(0, takeCount);
                _pendingPrints.RemoveRange(0, takeCount);
            }
        }

        float fill = _trailFill >= 0.02f ? _trailFill : 0f;
        if (fill > 0f)
        {
            _trailFill = 0f;
        }

        const int uniformCount = 16 + MaxLamps + MaxPrints;
        Vector4* uniforms = stackalloc Vector4[uniformCount];
        uniforms[0] = new Vector4(viewport.Origin.X, viewport.Origin.Y, viewport.Scale, Ambient.GroundOffset);
        uniforms[1] = new Vector4(Ambient.Radius, Ambient.Occlusion, shadowStrength, Ambient.Thickness);
        uniforms[2] = new Vector4(sun, reach);
        uniforms[3] = new Vector4(canvas.X, canvas.Y, AmbientDebug, 0f);
        uniforms[4] = new Vector4(lamps.Count, Ambient.LampReach, Ambient.LampGain, baseLight);
        uniforms[5] = new Vector4(Ambient.ContactRadius, ShadowsEnabled ? Ambient.Contact : 0f, heightMap.Frame is null ? 0f : windowSun, _indoorBase);
        uniforms[6] = heightMap.Frame ?? new Vector4(0f, 0f, 1f, 0f);
        uniforms[7] = new Vector4(sunTiles, heightMap.Frame is null ? 0f : reach);
        uniforms[8] = new Vector4(Ambient.CloudCover, Ambient.CloudShade, orientation, Ambient.CloudHeight);
        uniforms[9] = new Vector4(drift.X, drift.Y, lotSize.X, lotSize.Y);
        uniforms[10] = new Vector4(Ambient.Rain, Ambient.Wetness, seconds, heightMap.Frame is null ? 0f : glow);
        uniforms[11] = new Vector4(Ambient.Tint, slant, windStrength, 0f);
        uniforms[12] = new Vector4(Ambient.SurfaceMode, Ambient.SurfaceAmount, fill, trailState);

        int offset = 13;
        for (int i = 0; i < lamps.Count; i++)
        {
            SimsSDL.Core.SceneLighting.Light lamp = lamps[i];
            uniforms[offset + i] = new Vector4(lamp.X, lamp.Y, lamp.Toward, MathF.Min(1.5f, MathF.Max(0.4f, lamp.Strength / 40f)));
        }
        offset += MaxLamps;
        for (int i = 0; i < prints.Count; i++)
        {
            uniforms[offset + i] = prints[i];
        }
        offset += MaxPrints;
        uniforms[offset] = new Vector4(Ambient.GrassQuality, Ambient.SpringBloom, Ambient.Snowfall, Ambient.Fireflies);
        uniforms[offset + 1] = new Vector4(Ambient.Hail, Ambient.Frost, Ambient.Thunder, 0f);
        uniforms[offset + 2] = worldShadowViewport is { } wsv
            ? new Vector4(wsv.Origin.X, wsv.Origin.Y, wsv.Scale, 1f)
            : Vector4.Zero;

        var defaultScissor = new SDL_Rect { x = 0, y = 0, w = (int)canvas.X, h = (int)canvas.Y };
        GPUTexture term = AmbientDebug > 0 ? color : AmbientTexture(color.Width, color.Height);

        GPUTexture lift = EmptyLift();
        if ((Ambient.SurfaceMode != 0 || Ambient.SpringBloom > 0.01f) && AmbientDebug == 0)
        {
            GPUTexture source = SurfaceTexture(color.Width, color.Height);
            GPUTexture seasonDepth = SeasonDepthTexture(depth.Width, depth.Height);
            nint copy = SDL_BeginGPUCopyPass(command);
            if (copy == 0)
            {
                throw new SDLError("SDL_BeginGPUCopyPass");
            }

            try
            {
                var from = new SDL_GPUTextureLocation { texture = color.Handle };
                var to = new SDL_GPUTextureLocation { texture = source.Handle };
                SDL_CopyGPUTextureToTexture(copy, in from, in to, (uint)color.Width, (uint)color.Height, 1, false);
                var fromDepth = new SDL_GPUTextureLocation { texture = depth.Handle };
                var toDepth = new SDL_GPUTextureLocation { texture = seasonDepth.Handle };
                SDL_CopyGPUTextureToTexture(copy, in fromDepth, in toDepth, (uint)depth.Width, (uint)depth.Height, 1, false);
            }
            finally
            {
                SDL_EndGPUCopyPass(copy);
            }

            EncodeSeasonDepth(command, color, seasonDepth, viewport, canvas, displaced);

            GPUTexture trail = EmptyIndoorMask();
            if (displaced && _trailTextures.Count == 2)
            {
                GPUTexture previous = _trailTextures[_trailIndex];
                _trailIndex = 1 - _trailIndex;
                trail = _trailTextures[_trailIndex];
                RunAmbientPass(command, in defaultScissor, uniforms, uniformCount, GPUContext.Program.Trail, trail, false, [previous]);
            }

            GPUTexture seasonLift = LiftTexture(color.Width, color.Height);
            if (displaced)
            {
                lift = seasonLift;
            }

            GPUTexture sims = EncodeSimMask(command, depth, viewport, canvas);
            RunAmbientPass(
                command,
                in defaultScissor,
                uniforms,
                uniformCount,
                GPUContext.Program.Season,
                color,
                true,
                [seasonDepth, ground, heightMap.Texture, _indoorMask ?? EmptyIndoorMask(), source, trail, _overheadMap ?? EmptyOverheadMap(), sims],
                seasonLift);
        }

        RunAmbientPass(
            command,
            in defaultScissor,
            uniforms,
            uniformCount,
            GPUContext.Program.Ambient,
            term,
            AmbientDebug > 0,
            [depth, ground, heightMap.Texture, _indoorMask ?? EmptyIndoorMask(), footMap, lift, _overheadMap ?? EmptyOverheadMap(), worldDepth]);

        if (AmbientDebug != 0)
        {
            return;
        }

        RunAmbientPass(command, in defaultScissor, uniforms, uniformCount, GPUContext.Program.AmbientBlur, color, true, [term, depth, ground, lift]);
        if (Environment?.Effect.Atmosphere == true)
        {
            RunAmbientPass(command, in defaultScissor, uniforms, uniformCount, GPUContext.Program.Atmosphere, color, true, [term, depth, ground, _overheadMap ?? EmptyOverheadMap()]);
        }
    }

    private unsafe void RunAmbientPass(
        nint command,
        in SDL_Rect defaultScissor,
        Vector4* uniforms,
        int uniformCount,
        GPUContext.Program program,
        GPUTexture target,
        bool load,
        ReadOnlySpan<GPUTexture> textures,
        GPUTexture? second = null)
    {
        SDL_Rect passScissor = program == GPUContext.Program.Trail
            ? new SDL_Rect { x = 0, y = 0, w = target.Width, h = target.Height }
            : defaultScissor;

        SDL_GPUColorTargetInfo* colorTargets = stackalloc SDL_GPUColorTargetInfo[second is not null ? 2 : 1];
        colorTargets[0] = new SDL_GPUColorTargetInfo
        {
            texture = target.Handle,
            load_op = load ? SDL_GPU_LOADOP_LOAD : SDL_GPU_LOADOP_DONT_CARE,
            store_op = SDL_GPU_STOREOP_STORE,
        };
        uint numColorTargets = 1;
        if (second is not null)
        {
            colorTargets[1] = new SDL_GPUColorTargetInfo
            {
                texture = second.Handle,
                load_op = SDL_GPU_LOADOP_CLEAR,
                store_op = SDL_GPU_STOREOP_STORE,
            };
            numColorTargets = 2;
        }

        nint pass = SDL_BeginGPURenderPass(command, colorTargets, numColorTargets, null);
        if (pass == 0)
        {
            throw new SDLError("SDL_BeginGPURenderPass");
        }

        try
        {
            SDL_SetGPUScissor(pass, in passScissor);
            SDL_BindGPUGraphicsPipeline(pass, GPU.Pipeline(program, GPUContext.DepthMode.Flat));

            SDL_GPUTextureSamplerBinding* bindings = stackalloc SDL_GPUTextureSamplerBinding[textures.Length];
            for (int i = 0; i < textures.Length; i++)
            {
                bindings[i] = new SDL_GPUTextureSamplerBinding { texture = textures[i].Handle, sampler = GPU.PixelSampler };
            }

            SDL_BindGPUFragmentSamplers(pass, 0, bindings, (uint)textures.Length);
            SDL_PushGPUFragmentUniformData(command, 0, (nint)uniforms, (uint)(uniformCount * sizeof(Vector4)));
            SDL_DrawGPUPrimitives(pass, 3, 1, 0, 0);
        }
        finally
        {
            SDL_EndGPURenderPass(pass);
        }
    }

    /// <summary>Pixelates censored regions in 4-scene-unit mosaic blocks while preserving the sharpness of
    /// foreground occluders with lower depth values.</summary>
    private unsafe void EncodeCensorRects(
        IReadOnlyList<LotScene.CensorRect> censors,
        nint command,
        GPUTexture color,
        GPUTexture depth,
        SimsSDL.Core.SceneViewport viewport,
        Vector2 canvas)
    {
        GPUTexture source = SurfaceTexture(color.Width, color.Height);
        nint copy = SDL_BeginGPUCopyPass(command);
        if (copy == 0)
        {
            throw new SDLError("SDL_BeginGPUCopyPass");
        }

        try
        {
            var from = new SDL_GPUTextureLocation { texture = color.Handle };
            var to = new SDL_GPUTextureLocation { texture = source.Handle };
            SDL_CopyGPUTextureToTexture(copy, in from, in to, (uint)color.Width, (uint)color.Height, 1, false);
        }
        finally
        {
            SDL_EndGPUCopyPass(copy);
        }

        var colorTarget = new SDL_GPUColorTargetInfo
        {
            texture = color.Handle,
            load_op = SDL_GPU_LOADOP_LOAD,
            store_op = SDL_GPU_STOREOP_STORE,
        };

        nint pass = SDL_BeginGPURenderPass(command, &colorTarget, 1, null);
        if (pass == 0)
        {
            throw new SDLError("SDL_BeginGPURenderPass");
        }

        try
        {
            var scissor = new SDL_Rect { x = 0, y = 0, w = (int)canvas.X, h = (int)canvas.Y };
            SDL_SetGPUScissor(pass, in scissor);
            SDL_BindGPUGraphicsPipeline(pass, GPU.Pipeline(GPUContext.Program.Censor, GPUContext.DepthMode.Flat));

            SDL_GPUTextureSamplerBinding* bindings = stackalloc SDL_GPUTextureSamplerBinding[2];
            bindings[0] = new SDL_GPUTextureSamplerBinding { texture = source.Handle, sampler = GPU.PixelSampler };
            bindings[1] = new SDL_GPUTextureSamplerBinding { texture = depth.Handle, sampler = GPU.PixelSampler };
            SDL_BindGPUFragmentSamplers(pass, 0, bindings, 2);

            float blockSize = MathF.Max(2f, MathF.Round(4f * viewport.Scale, MidpointRounding.AwayFromZero));
            foreach (LotScene.CensorRect censor in censors)
            {
                float rx = viewport.Origin.X + censor.X * viewport.Scale;
                float ry = viewport.Origin.Y + censor.Y * viewport.Scale;
                float rw = censor.Width * viewport.Scale;
                float rh = censor.Height * viewport.Scale;
                if (rx + rw < 0f || ry + rh < 0f || rx >= canvas.X || ry >= canvas.Y)
                {
                    continue;
                }

                var quad = new Quad(new Vector4(rx, ry, rw, rh), canvas)
                {
                    DepthInterval = new Vector4(censor.Depth, blockSize, 0f, 0f),
                };
                SDL_PushGPUVertexUniformData(command, 0, (nint)(&quad), (uint)sizeof(Quad));
                SDL_DrawGPUPrimitives(pass, 6, 1, 0, 0);
            }
        }
        finally
        {
            SDL_EndGPURenderPass(pass);
        }
    }

    private unsafe void EncodePreview(PreviewMeshes preview, Vector4 rect, nint pass, nint command, Vector2 canvas, int sampleCount = SDL_GPU_SAMPLECOUNT_1)
    {
        if (rect.Z <= 0f || rect.W <= 0f)
        {
            return;
        }

        var scissor = new SDL_Rect
        {
            x = (int)MathF.Max(0f, rect.X),
            y = (int)MathF.Max(0f, rect.Y),
            w = (int)rect.Z,
            h = (int)rect.W,
        };
        SDL_SetGPUScissor(pass, in scissor);
        SDL_BindGPUGraphicsPipeline(pass, GPU.Pipeline(GPUContext.Program.Avatar, GPUContext.DepthMode.Mesh, sampleCount));

        Vector4 drawRect;
        if (preview.Buffers.Count > 1)
        {
            float scale = MathF.Min(rect.Z / MathF.Max((float)preview.Bounds.Width, 0.001f), rect.W);
            drawRect = new Vector4(rect.X + rect.Z * 0.5f - scale * 0.5f, rect.Y + rect.W - scale, scale, scale);
        }
        else
        {
            drawRect = rect;
        }

        for (int index = 0; index < preview.Buffers.Count; index++)
        {
            if (preview.Buffers[index] is not { } buffer || buffer.Handle == nint.Zero || preview.Textures[index].Handle == nint.Zero)
            {
                continue;
            }

            var binding = new SDL_GPUTextureSamplerBinding
            {
                texture = preview.Textures[index].Handle,
                sampler = GPU.AnisoSampler,
            };
            SDL_BindGPUFragmentSamplers(pass, 0, &binding, 1);
            nint handle = buffer.Handle;
            SDL_BindGPUVertexStorageBuffers(pass, 0, &handle, 1);

            var transform = new Quad(drawRect, canvas);
            SDL_PushGPUVertexUniformData(command, 0, (nint)(&transform), (uint)sizeof(Quad));
            SDL_DrawGPUPrimitives(pass, (uint)preview.VertexCounts[index], 1, 0, 0);
        }
    }

    private unsafe void EncodeOverlay(ReadOnlySpan<UIDraw> items, nint pass, nint command, Vector2 canvas, int sampleCount = SDL_GPU_SAMPLECOUNT_1)
    {
        if (items.IsEmpty)
        {
            return;
        }

        var scissor = new SDL_Rect { x = 0, y = 0, w = (int)canvas.X, h = (int)canvas.Y };
        SDL_SetGPUScissor(pass, in scissor);
        SDL_BindGPUGraphicsPipeline(pass, GPU.Pipeline(GPUContext.Program.Sprite, GPUContext.DepthMode.Flat, sampleCount));
        nint overlaySampler = GPU.AnisoSampler;

        for (int i = 0; i < items.Length; i++)
        {
            UIDraw item = items[i];
            if (!_uiTextures.TryGetValue(item.Texture, out var entry))
            {
                continue;
            }

            var binding = new SDL_GPUTextureSamplerBinding { texture = entry.Texture.Handle, sampler = overlaySampler };
            SDL_BindGPUFragmentSamplers(pass, 0, &binding, 1);

            var quad = new Quad(item.Rect, canvas, false, item.Opacity)
            {
                UvRect = item.UV,
                Tint = new Vector4(item.Opacity),
                DepthInterval = new Vector4(0f, 0f, 1f, 1f),
            };
            SDL_PushGPUVertexUniformData(command, 0, (nint)(&quad), (uint)sizeof(Quad));
            SDL_DrawGPUPrimitives(pass, 6, 1, 0, 0);
        }
    }

    private static SDL_Rect? ComputeCensorScissor(
        IReadOnlyList<LotScene.CensorRect> censors,
        SimsSDL.Core.SceneViewport viewport,
        int maxWidth,
        int maxHeight)
    {
        float blockSize = MathF.Max(2f, MathF.Round(4f * viewport.Scale, MidpointRounding.AwayFromZero));
        int pad = (int)MathF.Ceiling(blockSize) + 2;
        int minX = maxWidth;
        int minY = maxHeight;
        int maxX = 0;
        int maxY = 0;
        foreach (LotScene.CensorRect censor in censors)
        {
            int x0 = (int)MathF.Floor(viewport.Origin.X + censor.X * viewport.Scale) - pad;
            int y0 = (int)MathF.Floor(viewport.Origin.Y + censor.Y * viewport.Scale) - pad;
            int x1 = (int)MathF.Ceiling(viewport.Origin.X + (censor.X + censor.Width) * viewport.Scale) + pad;
            int y1 = (int)MathF.Ceiling(viewport.Origin.Y + (censor.Y + censor.Height) * viewport.Scale) + pad;
            if (x1 <= 0 || y1 <= 0 || x0 >= maxWidth || y0 >= maxHeight) continue;
            minX = Math.Min(minX, Math.Max(0, x0));
            minY = Math.Min(minY, Math.Max(0, y0));
            maxX = Math.Max(maxX, Math.Min(maxWidth, x1));
            maxY = Math.Max(maxY, Math.Min(maxHeight, y1));
        }

        return maxX > minX && maxY > minY
            ? new SDL_Rect { x = minX, y = minY, w = maxX - minX, h = maxY - minY }
            : null;
    }

    private static bool IsTranslucentAvatarMesh(LotScene.Mesh mesh) =>
        mesh.ObjectID > 0 && mesh.Unlit != true && mesh.Roof is null &&
        mesh.Tint is { Count: 4 } tint && tint[3] < 0.999f;

    private static float SrgbChannelToLinear(float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    private bool HasTranslucentAvatars()
    {
        if (Scene?.Meshes is not { Count: > 0 } meshes)
        {
            return false;
        }

        for (int i = 0; i < meshes.Count; i++)
        {
            if (IsTranslucentAvatarMesh(meshes[i]))
            {
                return true;
            }
        }

        return false;
    }

    private unsafe void EncodeTranslucentAvatars(nint pass, nint command, Vector2 canvas, int sampleCount = SDL_GPU_SAMPLECOUNT_1, SDL_Rect? clipScissor = null)
    {
        if (Scene is not { Meshes.Count: > 0 } scene || Viewport(canvas) is not { } viewport || !HasTranslucentAvatars())
        {
            return;
        }

        float scale = viewport.Scale;
        Vector2 origin = viewport.Origin;
        LotZoom zoom = EffectiveVisualZoom;

        var scissor = new SDL_Rect { x = 0, y = 0, w = (int)canvas.X, h = (int)canvas.Y };
        if (clipScissor is { } clip)
        {
            int left = Math.Max(scissor.x, clip.x);
            int top = Math.Max(scissor.y, clip.y);
            int right = Math.Min(scissor.x + scissor.w, clip.x + clip.w);
            int bottom = Math.Min(scissor.y + scissor.h, clip.y + clip.h);
            scissor = new SDL_Rect { x = left, y = top, w = Math.Max(0, right - left), h = Math.Max(0, bottom - top) };
            if (scissor.w == 0 || scissor.h == 0)
            {
                return;
            }
        }

        float clipLeft = scissor.x;
        float clipTop = scissor.y;
        float clipRight = scissor.x + scissor.w;
        float clipBottom = scissor.y + scissor.h;
        SDL_SetGPUScissor(pass, in scissor);

        var groups = new List<(int ObjectId, List<int> MeshIndices)>();
        var groupIndexByObject = new Dictionary<int, int>();
        for (int i = 0; i < _drawItems.Count; i++)
        {
            SceneDrawItem item = _drawItems[i];
            if (item.IsSprite)
            {
                continue;
            }

            int index = item.Index;
            if (index >= scene.Meshes.Count || index >= _meshSlices.Count || _meshSlices[index] is null)
            {
                continue;
            }

            LotScene.Mesh mesh = scene.Meshes[index];
            if (!IsTranslucentAvatarMesh(mesh))
            {
                continue;
            }

            if (index < _meshBounds.Length)
            {
                Vector4 b = _meshBounds[index];
                if (origin.X + b.Z * scale < clipLeft ||
                    origin.Y + b.W * scale < clipTop ||
                    origin.X + b.X * scale >= clipRight ||
                    origin.Y + b.Y * scale >= clipBottom)
                {
                    continue;
                }
            }

            if (!groupIndexByObject.TryGetValue(mesh.ObjectID, out int groupIdx))
            {
                groupIdx = groups.Count;
                groupIndexByObject[mesh.ObjectID] = groupIdx;
                groups.Add((mesh.ObjectID, []));
            }

            groups[groupIdx].MeshIndices.Add(index);
        }

        if (groups.Count == 0)
        {
            return;
        }

        nint prepassPipeline = GPU.Pipeline(GPUContext.Program.Avatar, GPUContext.DepthMode.MeshDepthPrepass, sampleCount);
        nint colorPipeline = GPU.Pipeline(GPUContext.Program.Avatar, GPUContext.DepthMode.MeshEqual, sampleCount);

        void DrawGhostSlices(List<int> meshIndices, bool colorSubpass)
        {
            nint currentVertexBuffer = 0;
            nint currentTexHandle = 0;
            for (int k = 0; k < meshIndices.Count; k++)
            {
                int index = meshIndices[k];
                LotScene.Mesh mesh = scene.Meshes[index];
                MeshSlice slice = _meshSlices[index]!.Value;
                if (slice.Buffer.Handle == nint.Zero)
                {
                    continue;
                }
                GPUTexture tex = _sceneTextures[mesh.TextureFor(zoom)];
                if (tex.Handle == nint.Zero)
                {
                    continue;
                }
                if (tex.Handle != currentTexHandle)
                {
                    currentTexHandle = tex.Handle;
                    var binding = new SDL_GPUTextureSamplerBinding { texture = tex.Handle, sampler = GPU.AnisoSampler };
                    SDL_BindGPUFragmentSamplers(pass, 0, &binding, 1);
                }

                if (slice.Buffer.Handle != currentVertexBuffer)
                {
                    currentVertexBuffer = slice.Buffer.Handle;
                    nint handle = slice.Buffer.Handle;
                    SDL_BindGPUVertexStorageBuffers(pass, 0, &handle, 1);
                }

                Vector4 tint = Vector4.One;
                if (colorSubpass && mesh.Tint is { Count: 4 } meshTint)
                {
                    float alpha = MathF.Pow(Math.Clamp(meshTint[3], 0f, 1f), 1.6f);
                    tint = new Vector4(
                        SrgbChannelToLinear(MathF.Min(1f, meshTint[0] * 1.5f)),
                        SrgbChannelToLinear(meshTint[1]),
                        SrgbChannelToLinear(MathF.Min(1f, meshTint[2] * 1.06f)),
                        alpha);
                }

                var quad = new Quad(new Vector4(origin.X, origin.Y, scale, scale), canvas)
                {
                    Tint = tint,
                    UvRect = new Vector4(1f, 0f, 1f, 1f),
                    DepthInterval = new Vector4(BitConverter.UInt32BitsToSingle(slice.FirstVertex), 0f, 0f, 0f),
                };
                SDL_PushGPUVertexUniformData(command, 0, (nint)(&quad), (uint)sizeof(Quad));
                SDL_DrawGPUPrimitives(pass, slice.VertexCount, 1, 0, 0);
            }
        }

        // Render each translucent ghost with a frontmost-surface depth prepass followed by an
        // EQUAL color pass so interior limbs never double-blend or punch holes in the torso.
        for (int g = 0; g < groups.Count; g++)
        {
            List<int> meshIndices = groups[g].MeshIndices;
            SDL_BindGPUGraphicsPipeline(pass, prepassPipeline);
            DrawGhostSlices(meshIndices, colorSubpass: false);
            SDL_BindGPUGraphicsPipeline(pass, colorPipeline);
            DrawGhostSlices(meshIndices, colorSubpass: true);
        }
    }

    private unsafe void EncodeLot(
        nint pass,
        nint command,
        Vector2 canvas,
        int sampleCount = SDL_GPU_SAMPLECOUNT_1,
        SDL_Rect? clipScissor = null,
        bool deferTranslucentAvatars = false,
        Vector2? originOverride = null,
        float? scaleOverride = null,
        LotZoom? zoomOverride = null,
        bool skipTerrainAndRoads = false,
        SimsSDL.Core.SceneViewport? viewportOverride = null)
    {
        if (Scene is not { } scene || (viewportOverride ?? Viewport(canvas)) is not { } viewport)
        {
            return;
        }

        float scale = scaleOverride ?? viewport.Scale;
        Vector2 origin = originOverride ?? viewport.Origin;

        // Anisotropic filtering when minified; analytical subpixel filters at 1:1+.
        LotZoom zoom = zoomOverride ?? EffectiveVisualZoom;
        float artTexelScale = scale / zoom.Factor();
        bool settledIntegerUi = MathF.Abs(artTexelScale - MathF.Round(artTexelScale, MidpointRounding.AwayFromZero)) <= 1e-4f
            && (scaleOverride is not null || !IsZoomInterpolating);
        nint artSampler = settledIntegerUi ? GPU.PixelSampler : GPU.Sampler;
        nint spriteSampler = AntiAliasedSprites ? GPU.Sampler : artSampler;
        bool spriteMinified = scale < zoom.Factor() * MathF.Max(UIScale, 0.01f) - 0.001f;
        nint colorSpriteSampler = AntiAliasedSprites && spriteMinified ? GPU.AnisoSampler : spriteSampler;
        SimsSDL.Core.SceneLighting? lighting = ActiveLighting(scene);

        var scissor = new SDL_Rect { x = 0, y = 0, w = (int)canvas.X, h = (int)canvas.Y };
        if (clipScissor is { } clip)
        {
            int left = Math.Max(scissor.x, clip.x);
            int top = Math.Max(scissor.y, clip.y);
            int right = Math.Min(scissor.x + scissor.w, clip.x + clip.w);
            int bottom = Math.Min(scissor.y + scissor.h, clip.y + clip.h);
            scissor = new SDL_Rect { x = left, y = top, w = Math.Max(0, right - left), h = Math.Max(0, bottom - top) };
            if (scissor.w == 0 || scissor.h == 0)
            {
                return;
            }
        }
        float clipLeft = scissor.x;
        float clipTop = scissor.y;
        float clipRight = scissor.x + scissor.w;
        float clipBottom = scissor.y + scissor.h;
        SDL_SetGPUScissor(pass, in scissor);

        (GPUContext.Program Program, GPUContext.DepthMode Depth)? currentPipeline = null;
        (nint Tex, nint Sampler)? currentSingleSampler = null;
        (nint T0, nint S0, nint T1, nint S1)? currentDualSamplers = null;
        nint currentVertexBuffer = 0;

        void Bind(GPUContext.Program program, GPUContext.DepthMode depth)
        {
            if (currentPipeline is { } current && current.Program == program && current.Depth == depth)
            {
                return;
            }

            nint resolved = sampleCount == SDL_GPU_SAMPLECOUNT_1
                ? (program, depth) switch
                {
                    (GPUContext.Program.SpriteDepth, GPUContext.DepthMode.Mesh) => GPU.SpriteDepthPipeline,
                    (GPUContext.Program.SpriteDepth, GPUContext.DepthMode.MeshEdge) => GPU.SpriteDepthEdgePipeline,
                    _ => GPU.Pipeline(program, depth, sampleCount),
                }
                : GPU.Pipeline(program, depth, sampleCount);
            SDL_BindGPUGraphicsPipeline(pass, resolved);
            currentPipeline = (program, depth);
            currentSingleSampler = null;
            currentDualSamplers = null;
        }

        bool BindTexture(GPUTexture texture, nint sampler)
        {
            nint th = texture.Handle;
            if (th == nint.Zero)
            {
                return false;
            }

            if (currentSingleSampler is { } cur && cur.Tex == th && cur.Sampler == sampler)
            {
                return true;
            }

            var binding = new SDL_GPUTextureSamplerBinding { texture = th, sampler = sampler };
            SDL_BindGPUFragmentSamplers(pass, 0, &binding, 1);
            currentSingleSampler = (th, sampler);
            currentDualSamplers = null;
            return true;
        }

        bool BindTextures(GPUTexture first, nint firstSampler, GPUTexture second, nint secondSampler)
        {
            nint t0 = first.Handle;
            nint t1 = second.Handle;
            if (t0 == nint.Zero || t1 == nint.Zero)
            {
                return false;
            }

            if (currentDualSamplers is { } cur && cur.T0 == t0 && cur.S0 == firstSampler && cur.T1 == t1 && cur.S1 == secondSampler)
            {
                return true;
            }

            SDL_GPUTextureSamplerBinding* bindings = stackalloc SDL_GPUTextureSamplerBinding[2];
            bindings[0] = new SDL_GPUTextureSamplerBinding { texture = t0, sampler = firstSampler };
            bindings[1] = new SDL_GPUTextureSamplerBinding { texture = t1, sampler = secondSampler };
            SDL_BindGPUFragmentSamplers(pass, 0, bindings, 2);
            currentDualSamplers = (t0, firstSampler, t1, secondSampler);
            currentSingleSampler = null;
            return true;
        }

        bool BindVertices(GPUBuffer buffer)
        {
            nint handle = buffer.Handle;
            if (handle == nint.Zero)
            {
                return false;
            }

            if (currentVertexBuffer == handle)
            {
                return true;
            }

            currentVertexBuffer = handle;
            SDL_BindGPUVertexStorageBuffers(pass, 0, &handle, 1);
            return true;
        }

        void Push(in Quad quad)
        {
            Quad copy = quad;
            SDL_PushGPUVertexUniformData(command, 0, (nint)(&copy), (uint)sizeof(Quad));
        }

        if (!skipTerrainAndRoads && _terrainBuffer is not null)
        {
            Bind(GPUContext.Program.Terrain, GPUContext.DepthMode.Flat);
            if (BindVertices(_terrainBuffer))
            {
                var transform = new Quad(new Vector4(origin.X, origin.Y, scale, scale), canvas)
                {
                    Tint = lighting?.Outside.Multiplier ?? Vector4.One,
                    UvRect = new Vector4(TerrainDetail, (float)zoom, 0f, 0f),
                };
                Push(in transform);
                SDL_DrawGPUPrimitives(pass, (uint)_terrainBuffer.Count, 1, 0, 0);
            }
        }

        void DrawFloorShadow(LotScene.Sprite sprite)
        {
            if (!ShadowsEnabled || !LiveMode || sprite.Wall is not null ||
                sprite.FloorShadowImage(zoom) is not { } shadow)
            {
                return;
            }

            var shadowRect = new Vector4(sprite.X + shadow.X, sprite.Y + shadow.Y, shadow.Width, shadow.Height);
            if (origin.X + (shadowRect.X + shadowRect.Z) * scale < clipLeft ||
                origin.Y + (shadowRect.Y + shadowRect.W) * scale < clipTop ||
                origin.X + shadowRect.X * scale >= clipRight ||
                origin.Y + shadowRect.Y * scale >= clipBottom)
            {
                return;
            }

            Bind(GPUContext.Program.Sprite, GPUContext.DepthMode.Flat);
            if (!BindTexture(_sceneTextures[shadow.Texture], colorSpriteSampler))
            {
                return;
            }
            var shadowQuad = new Quad(
                new Vector4(origin.X + shadowRect.X * scale, origin.Y + shadowRect.Y * scale, shadowRect.Z * scale, shadowRect.W * scale),
                canvas)
            {
                DepthInterval = new Vector4(0f, 0f, 0f, 1f),
            };
            Push(in shadowQuad);
            SDL_DrawGPUPrimitives(pass, 6, 1, 0, 0);
        }

        void DrawSprite(LotScene.Sprite sourceSprite, SpritePassMode passMode)
        {
            if (zoom == LotZoom.Far && sourceSprite.HideAtFarZoom == true)
            {
                return;
            }

            if (passMode == SpritePassMode.EdgeFringe &&
                sourceSprite.DepthTexture is null &&
                !(ShowsPlacement && sourceSprite.Wall is not null && WallPaintPreview is not null))
            {
                return;
            }

            bool useWallPaint = false;
            Vector2 offset = Vector2.Zero;
            LotScene.Sprite sprite = sourceSprite;
            if (ShowsPlacement &&
                sourceSprite.Wall is { } wall &&
                WallPaintPreview is { } preview &&
                _wallPaintTextures.Count == preview.Scene.Textures.Count)
            {
                foreach (LotScene.Sprite candidate in preview.Scene.Sprites)
                {
                    if (Equals(candidate.Wall, wall))
                    {
                        sprite = candidate;
                        useWallPaint = true;
                        offset = new Vector2(preview.X, preview.Y);
                        break;
                    }
                }
            }

            int texture = sprite.Texture;
            int? depthTexture = sprite.DepthTexture;
            var rect = new Vector4(sprite.X + offset.X, sprite.Y + offset.Y, sprite.Width, sprite.Height);
            if (sprite.Image(zoom) is { } image)
            {
                texture = image.Texture;
                depthTexture = image.DepthTexture;
                rect = new Vector4(sprite.X + offset.X + image.X, sprite.Y + offset.Y + image.Y, image.Width, image.Height);
            }

            if (passMode == SpritePassMode.EdgeFringe && depthTexture is null)
            {
                return;
            }

            if (origin.X + (rect.X + rect.Z) * scale < clipLeft ||
                origin.Y + (rect.Y + rect.W) * scale < clipTop ||
                origin.X + rect.X * scale >= clipRight ||
                origin.Y + rect.Y * scale >= clipBottom)
            {
                return;
            }

            GPUTexture colorTex = useWallPaint ? _wallPaintTextures[texture] : _sceneTextures[texture];
            var quad = new Quad(
                new Vector4(origin.X + rect.X * scale, origin.Y + rect.Y * scale, rect.Z * scale, rect.W * scale),
                canvas,
                sprite.Flip);

            if (depthTexture is int dt)
            {
                Bind(GPUContext.Program.SpriteDepth, passMode == SpritePassMode.EdgeFringe ? GPUContext.DepthMode.MeshEdge : GPUContext.DepthMode.Mesh);
                GPUTexture depthTex = useWallPaint ? _wallPaintTextures[dt] : _sceneTextures[dt];
                if (!BindTextures(colorTex, colorSpriteSampler, depthTex, GPU.PixelSampler))
                {
                    return;
                }
                float depthNear = sprite.DepthNear ?? 0f;
                float depthFar = sprite.DepthFar ?? 0f;
                quad.DepthInterval = passMode switch
                {
                    // SPR2 rendering splits opaque cores from premultiplied translucent fringes.
                    SpritePassMode.Single => new Vector4(depthNear, depthFar, 0.985f, 0f),
                    SpritePassMode.OpaqueCore => AntiAliasedSprites
                        ? new Vector4(depthNear, depthFar, 0.985f, 1f)
                        : new Vector4(depthNear, depthFar, 0.985f, 0f),
                    SpritePassMode.EdgeFringe => AntiAliasedSprites
                        ? new Vector4(depthNear, depthFar, 0.985f, 2f)
                        : new Vector4(depthNear, depthFar, 0.985f, -1f),
                    _ => default,
                };
            }
            else
            {
                Bind(GPUContext.Program.Sprite, GPUContext.DepthMode.Flat);
                if (!BindTexture(colorTex, colorSpriteSampler))
                {
                    return;
                }
                quad.DepthInterval = new Vector4(0f, 0f, 8.0f / 31.0f, AntiAliasedSprites ? 1f : 0f);
            }

            quad.Tint = lighting?.Multiplier(sprite.Room) ?? Vector4.One;
            Push(in quad);
            SDL_DrawGPUPrimitives(pass, 6, 1, 0, 0);
        }

        SpritePassMode primaryPass = SpritePassMode.OpaqueCore;
        int lastShadowLevel = 0;
        bool drewFloorBrush = false;

        void DrawFloorBrushIfNeeded()
        {
            if (!ShowsPlacement || drewFloorBrush || _floorBrushBuffer is null || _floorBrushTexture is null)
            {
                return;
            }

            drewFloorBrush = true;
            Bind(GPUContext.Program.Surface, GPUContext.DepthMode.Flat);
            if (!BindTexture(_floorBrushTexture, artSampler) || !BindVertices(_floorBrushBuffer))
            {
                return;
            }
            var transform = new Quad(new Vector4(origin.X, origin.Y, scale, scale), canvas)
            {
                Tint = Vector4.One,
            };
            Push(in transform);
            SDL_DrawGPUPrimitives(pass, (uint)_floorBrushBuffer.Count, 1, 0, 0);
        }

        (int Level, int Layer) ItemGroup(SceneDrawItem item) => item.Kind switch
        {
            SceneDrawItem.ItemKind.Sprite => (scene.Sprites[item.Index].Level ?? 1, scene.Sprites[item.Index].Layer ?? 0),
            _ => (scene.Meshes![item.Index].Level ?? 1, scene.Meshes![item.Index].Layer ?? 1),
        };

        int sliceStart = 0;
        while (sliceStart < _drawItems.Count)
        {
            (int itemLevel, int itemLayer) = ItemGroup(_drawItems[sliceStart]);
            int sliceEnd = sliceStart + 1;
            while (sliceEnd < _drawItems.Count)
            {
                (int nextLevel, int nextLayer) = ItemGroup(_drawItems[sliceEnd]);
                if (nextLevel != itemLevel || nextLayer != itemLayer)
                {
                    break;
                }

                sliceEnd += 1;
            }

            if (itemLevel > scene.SelectedLevel || (itemLevel == scene.SelectedLevel && itemLayer >= 1))
            {
                DrawFloorBrushIfNeeded();
            }

            if (itemLayer >= 1 && lastShadowLevel < itemLevel)
            {
                lastShadowLevel = itemLevel;
                for (int s = 0; s < _activeShadowSpriteIndices.Count; s++)
                {
                    LotScene.Sprite sprite = scene.Sprites[_activeShadowSpriteIndices[s]];
                    if ((sprite.Level ?? 1) == itemLevel)
                    {
                        DrawFloorShadow(sprite);
                    }
                }

                if (scene.Meshes is not null)
                {
                    foreach (LotScene.Mesh mesh in scene.Meshes)
                    {
                        if ((mesh.Level ?? 1) != itemLevel ||
                            !ShadowsEnabled || !LiveMode ||
                            mesh.FloorShadowImage(zoom) is not { } shadow)
                        {
                            continue;
                        }

                        var shadowRect = new Vector4(shadow.X, shadow.Y, shadow.Width, shadow.Height);
                        if (origin.X + (shadowRect.X + shadowRect.Z) * scale < clipLeft ||
                            origin.Y + (shadowRect.Y + shadowRect.W) * scale < clipTop ||
                            origin.X + shadowRect.X * scale >= clipRight ||
                            origin.Y + shadowRect.Y * scale >= clipBottom)
                        {
                            continue;
                        }

                        Bind(GPUContext.Program.Sprite, GPUContext.DepthMode.Flat);
                        if (!BindTexture(_sceneTextures[shadow.Texture], colorSpriteSampler))
                        {
                            continue;
                        }
                        var shadowQuad = new Quad(
                            new Vector4(origin.X + shadowRect.X * scale, origin.Y + shadowRect.Y * scale, shadowRect.Z * scale, shadowRect.W * scale),
                            canvas)
                        {
                            DepthInterval = new Vector4(0f, 0f, 0f, 1f),
                        };
                        Push(in shadowQuad);
                        SDL_DrawGPUPrimitives(pass, 6, 1, 0, 0);
                    }
                }
            }

            PendingMeshBatch? pendingMesh = null;
            void FlushPendingMesh()
            {
                if (pendingMesh is not { } batch)
                {
                    return;
                }

                pendingMesh = null;
                Bind(batch.Program, batch.Depth);
                if (!BindTexture(batch.Texture, batch.Sampler) || !BindVertices(batch.Buffer))
                {
                    return;
                }
                var transform = new Quad(new Vector4(origin.X, origin.Y, scale, scale), canvas)
                {
                    Tint = batch.Tint,
                    DepthInterval = new Vector4(BitConverter.UInt32BitsToSingle(batch.FirstVertex), 0f, 0f, 0f),
                };
                transform.UvRect = new Vector4(batch.UVRectX, batch.SunDir.X, batch.SunDir.Y, batch.SunDir.Z);
                Push(in transform);
                SDL_DrawGPUPrimitives(pass, batch.VertexCount, 1, 0, 0);
            }

            Vector3 roofSunDir = ResolveRoofSunDirection(AmbientEnabled && Environment is not null, Ambient.Sun);

            bool hasDepthSprites = false;
            for (int i = sliceStart; i < sliceEnd; i++)
            {
                SceneDrawItem item = _drawItems[i];
                if (item.IsSprite)
                {
                    if (pendingMesh is not null)
                    {
                        FlushPendingMesh();
                    }

                    LotScene.Sprite sprite = scene.Sprites[item.Index];
                    if (sprite.DepthTexture is not null)
                    {
                        hasDepthSprites = true;
                    }

                    DrawSprite(sprite, primaryPass);
                }
                else
                {
                    int index = item.Index;
                    if (index < _meshBounds.Length)
                    {
                        Vector4 b = _meshBounds[index];
                        if (origin.X + b.Z * scale < clipLeft ||
                            origin.Y + b.W * scale < clipTop ||
                            origin.X + b.X * scale >= clipRight ||
                            origin.Y + b.Y * scale >= clipBottom)
                        {
                            continue;
                        }
                    }

                    if (scene.Meshes is null || index >= scene.Meshes.Count || index >= _meshSlices.Count || _meshSlices[index] is not { } slice)
                    {
                        continue;
                    }

                    LotScene.Mesh mesh = scene.Meshes[index];
                    if (skipTerrainAndRoads && mesh.Floor is { Pattern: 9 or 10 or 11 or 12 } && (mesh.Level ?? 1) == 1)
                    {
                        continue;
                    }
                    if (IsTranslucentAvatarMesh(mesh))
                    {
                        continue;
                    }

                    bool unlit = mesh.Unlit == true;
                    GPUContext.Program prog = unlit ? GPUContext.Program.Surface : GPUContext.Program.Avatar;
                    GPUContext.DepthMode depthMode = unlit && mesh.Floor?.Water is null && (mesh.Level ?? 1) == 1
                        ? GPUContext.DepthMode.Flat
                        : GPUContext.DepthMode.Mesh;
                    GPUTexture tex = _sceneTextures[mesh.TextureFor(zoom)];
                    nint samp = mesh.Roof is not null
                        ? GPU.RoofSampler
                        : mesh.Floor is not null
                            ? (settledIntegerUi ? artSampler : GPU.AnisoSampler)
                            : GPU.AnisoSampler;
                    Vector4 tint = lighting?.Multiplier(mesh.Room) ?? Vector4.One;
                    if (mesh.Tint is { Count: 4 } meshTint)
                    {
                        tint *= new Vector4(meshTint[0], meshTint[1], meshTint[2], meshTint[3]);
                    }
                    float uvX = !unlit && mesh.Roof is not null
                        ? 2f
                        : (!unlit && mesh.Roof is null && CharacterDetail == 0) ? 1f : 0f;
                    Vector3 sunDir = !unlit && mesh.Roof is not null ? roofSunDir : Vector3.Zero;

                    if (pendingMesh is { } existing &&
                        existing.Program == prog &&
                        existing.Depth == depthMode &&
                        existing.Texture.Handle == tex.Handle &&
                        existing.Sampler == samp &&
                        existing.Buffer.Handle == slice.Buffer.Handle &&
                        existing.Tint == tint &&
                        existing.UVRectX == uvX &&
                        existing.SunDir == sunDir &&
                        existing.FirstVertex + existing.VertexCount == slice.FirstVertex)
                    {
                        existing.VertexCount += slice.VertexCount;
                        pendingMesh = existing;
                    }
                    else
                    {
                        if (pendingMesh is not null)
                        {
                            FlushPendingMesh();
                        }

                        pendingMesh = new PendingMeshBatch
                        {
                            Program = prog,
                            Depth = depthMode,
                            Texture = tex,
                            Sampler = samp,
                            Buffer = slice.Buffer,
                            Tint = tint,
                            UVRectX = uvX,
                            SunDir = sunDir,
                            FirstVertex = slice.FirstVertex,
                            VertexCount = slice.VertexCount,
                        };
                    }
                }
            }

            if (pendingMesh is not null)
            {
                FlushPendingMesh();
            }

            if (hasDepthSprites)
            {
                for (int i = sliceStart; i < sliceEnd; i++)
                {
                    SceneDrawItem item = _drawItems[i];
                    if (item.IsSprite)
                    {
                        DrawSprite(scene.Sprites[item.Index], SpritePassMode.EdgeFringe);
                    }
                }
            }

            sliceStart = sliceEnd;
        }

        DrawFloorBrushIfNeeded();

        if (!deferTranslucentAvatars)
        {
            EncodeTranslucentAvatars(pass, command, canvas, sampleCount, clipScissor);
            currentPipeline = null;
            currentSingleSampler = null;
            currentDualSamplers = null;
            currentVertexBuffer = 0;
        }

        if (ShowsPlacement && _placementBuffer is not null)
        {
            Bind(GPUContext.Program.Placement, GPUContext.DepthMode.Flat);
            if (BindVertices(_placementBuffer))
            {
                Push(new Quad(new Vector4(origin.X, origin.Y, scale, scale), canvas));
                SDL_DrawGPUPrimitives(pass, (uint)_placementBuffer.Count, 1, 0, 0);
            }
        }

        void DrawGhost(PlacementPreview.GhostData ghost, IReadOnlyList<GPUTexture> textures, float opacity)
        {
            foreach (LotScene.Sprite sprite in ghost.Scene.Sprites)
            {
                int texture = sprite.Texture;
                var rect = new Vector4(sprite.X, sprite.Y, sprite.Width, sprite.Height);
                if (sprite.Image(zoom) is { } image)
                {
                    texture = image.Texture;
                    rect = new Vector4(sprite.X + image.X, sprite.Y + image.Y, image.Width, image.Height);
                }

                if (!BindTexture(textures[texture], artSampler))
                {
                    continue;
                }
                var quad = new Quad(
                    new Vector4(origin.X + (ghost.X + rect.X) * scale, origin.Y + (ghost.Y + rect.Y) * scale, rect.Z * scale, rect.W * scale),
                    canvas,
                    sprite.Flip,
                    opacity)
                {
                    DepthInterval = new Vector4(0f, 0f, 0f, AntiAliasedSprites ? 1f : 0f),
                };
                Push(in quad);
                SDL_DrawGPUPrimitives(pass, 6, 1, 0, 0);
            }
        }

        if (ShowsGhost && _placementGhost is { } placementGhost && _ghostTextures.Count == placementGhost.Scene.Textures.Count)
        {
            Bind(GPUContext.Program.Ghost, GPUContext.DepthMode.Flat);
            DrawGhost(placementGhost, _ghostTextures, 0.6f);
        }

        if (ShowsBuildCursor && BuildCursor is { } cursorGhost && _buildCursorTextures.Count == cursorGhost.Scene.Textures.Count)
        {
            Bind(GPUContext.Program.Ghost, GPUContext.DepthMode.Flat);
            DrawGhost(cursorGhost, _buildCursorTextures, 1f);
        }
    }
}

public struct UIDraw
{
    public UITexture Texture { get; set; }
    public Vector4 Rect { get; set; }
    public Vector4 UV { get; set; }
    public float Opacity { get; set; }

    public UIDraw()
    {
        Texture = default!;
        Rect = Vector4.Zero;
        UV = new Vector4(0f, 0f, 1f, 1f);
        Opacity = 1f;
    }

    public UIDraw(UITexture texture, Vector4 rect, Vector4? uv = null, float opacity = 1f)
    {
        Texture = texture;
        Rect = rect;
        UV = uv ?? new Vector4(0f, 0f, 1f, 1f);
        Opacity = opacity;
    }
}
