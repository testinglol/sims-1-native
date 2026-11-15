using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using SimsSDL.UI;

namespace SimsSDL;

/// <summary>
/// The lot pass of the Metal renderer on SDL_GPU: terrain, the depth-ordered sprites and meshes, lit by the scene's
/// lighting. It keeps the same scene state (textures, mesh buffers, draw order, picker) and the same viewport maths.
/// </summary>
public sealed class LotRenderer
{
    /// <summary>
    /// The original UI's blue (82/255) in linear light, the colour the Metal view clears to.
    /// </summary>
    public static readonly float OriginalUIBlue = (float)Math.Pow((82.0 / 255.0 + 0.055) / 1.055, 2.4);

    /// <summary>
    /// Trail texels per lot tile in the snow trail map.
    /// </summary>
    public const float TrailTexels = 32f;

    /// <summary>
    /// Margin in lot tiles around the lot for the snow trail map.
    /// </summary>
    public const float TrailMargin = 1f;

    /// <summary>
    /// `ambient.frag`'s uniform block (ambientUniforms.glsl): thirteen vectors, then `MaxLamps` lamps, then
    /// `MaxPrints` footprints for the trail pass.
    /// </summary>
    private const int MaxLamps = 16;

    /// <summary>
    /// The trail pass stamps at most this many prints a frame (ambientUniforms.glsl's MAX_PRINTS); more wait a frame.
    /// </summary>
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

    private readonly record struct MeshSlice(GPUBuffer Buffer, uint FirstVertex, uint VertexCount);

    private sealed class PreparedScene
    {
        public required LotScene Scene { get; init; }
        public required List<byte[]> Pixels { get; init; }
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
        public uint FirstVertex;
        public uint VertexCount;
    }

    private enum SpritePassMode
    {
        Single,
        OpaqueCore,
        EdgeFringe,
    }

    /// <summary>
    /// Ambient occlusion and sun shadows from the lot's depth (`ambient.frag`), multiplied into the lot's colour before
    /// the overlay is drawn. The parameters are in scene pixels (64 to a tile's width).
    /// </summary>
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

        public AmbientParameters()
        {
            HeightTexels = 16f;
            Sun = new Vector3(1f, 0f, 0f);
        }
    }

    public GPUContext GPU { get; }
    public LotScene? Scene { get; private set; }
    private List<byte[]> _scenePixels = [];
    private List<LotScene.Texture> _deferredPixelTextures = [];
    public string? SceneRoot { get; private set; }
    private long _sceneRevision;
    private long _animationSequence;
    private List<GPUTexture> _sceneTextures = [];
    public int BaseTextureCount { get; private set; }
    private Dictionary<string, int> _textureIndexByFile = new();
    private Dictionary<string, GPUTexture> _cachedTextures = new();
    private Dictionary<string, byte[]> _cachedPixels = new();
    private readonly Dictionary<string, GPUTexture> _dynamicTextureCache = new();
    private readonly Dictionary<string, byte[]> _dynamicPixelCache = new();
    private readonly List<string> _dynamicTextureOrder = [];
    private GPUBuffer? _terrainBuffer;
    private List<MeshSlice?> _meshSlices = [];
    private Vector4[] _meshBounds = [];
    private List<LotScene.Sprite> _staticSprites = [];
    private List<LotScene.Mesh> _staticMeshes = [];
    private int _staticSpriteCount;
    private int _staticMeshCount;
    private List<MeshSlice?> _staticMeshSlices = [];
    private Vector4[] _staticMeshBounds = [];
    private List<OrderedDrawEntry> _staticDrawEntries = [];
    private List<SceneDrawItem> _drawItems = [];
    private ScenePicker? _picker;
    private bool _pickerDirty;
    private CachedObjectHit? _cachedObjectHit;
    private GroundTilePicker? _groundPicker;

    /// <summary>
    /// The buy and build previews: a coloured footprint or brush over the ground and the held object's ghost. They are
    /// built on the CPU when they change and uploaded with the next frame's uploads.
    /// </summary>
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

    /// <summary>
    /// The original build cursor object (wall or terrain tool) drawn at the hovered vertex in build mode.
    /// </summary>
    public PlacementPreview.GhostData? BuildCursor { get; private set; }
    private (int Width, int Height, bool Depth8, byte[] Pixels)[]? _buildCursorPixels;
    private IReadOnlyList<GPUTexture> _buildCursorTextures = [];
    public PlacementPreview.GhostData? WallPaintPreview { get; private set; }
    private (int Width, int Height, bool Depth8, byte[] Pixels)[]? _wallPaintPixels;
    private IReadOnlyList<GPUTexture> _wallPaintTextures = [];

    /// <summary>
    /// Whether the footprint and the ghost are drawn this frame (buy or build mode, and holding an object).
    /// </summary>
    public bool ShowsPlacement { get; set; }
    public bool ShowsGhost { get; set; }
    public bool ShowsBuildCursor { get; set; }
    public bool LiveMode { get; set; }

    /// <summary>
    /// The live game speed the host last reported: 0 paused, 1, 3 or 10.
    /// </summary>
    public int Speed { get; set; }
    public float UIScale { get; set; } = 2f;
    public LotZoom Zoom { get; set; } = LotZoom.Near;

    /// <summary>
    /// Graphics Options Terrain Detail: 0 low (dither only), 1 medium and 2 high draw grass blades.
    /// </summary>
    public int TerrainDetail { get; set; } = 2;

    /// <summary>
    /// Character Detail: Low draws Sim meshes with their texture alone (FUN_004e61c6, FUN_005d337d).
    /// </summary>
    public int CharacterDetail { get; set; } = 2;

    /// <summary>
    /// How many times the pan was set, like the counter HouseViewer::SetOffsets increments (DAT_006e54a8); the
    /// tutorial's scroll event waits for it to change.
    /// </summary>
    public int PanAssignments { get; private set; }

    private Vector2 _pan = Vector2.Zero;
    public Vector2 Pan
    {
        get => _pan;
        set
        {
            PanAssignments = unchecked(PanAssignments + 1);
            _pan = Scene is not null
                ? SimsSDL.Core.SceneViewport.ClampedPan(value, Scene, MathF.Max(UIScale, 0.01f) * Zoom.Factor())
                : value;
        }
    }

    public bool LightingEnabled { get; set; } = true;
    public bool AntiAliasedSprites { get; set; } = true;

    /// <summary>
    /// The original UI drawn over the lot this frame, in order, in canvas pixels.
    /// </summary>
    public List<UIDraw> Overlay { get; set; } = [];

    /// <summary>
    /// Whether the lot is drawn under the overlay; the neighborhood and Create a Family screens show only UI.
    /// </summary>
    public bool DrawsLot { get; set; } = true;

    /// <summary>
    /// Sim previews drawn after overlay item `Index`, each in its rectangle, by preview key (0 is the main preview;
    /// Create a Family shows one per household member).
    /// </summary>
    public (int Index, Dictionary<int, Vector4> Rects)? PreviewPlacement { get; set; }

    private readonly Dictionary<int, PreviewMeshes> _previews = new();
    private Dictionary<UITexture, (UITexture Source, int Version, GPUTexture Texture)> _uiTextures =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Scene snapshots being decoded off the main thread; animation waits for them, since its frames follow the newest.
    /// </summary>
    private int _sceneLoadsInFlight;
    private long _newestRequestedRevision;

    /// <summary>
    /// Animation frames the host sent since the last drawn frame.
    /// </summary>
    private List<SceneAnimation> _pendingFrames = [];

    public bool AmbientEnabled { get; set; } = true;
    public AmbientParameters Ambient = new();

    /// <summary>
    /// The original Graphics Options shadow switch also gates the lot shadow pass.
    /// </summary>
    public bool ShadowsEnabled { get; set; } = true;
    public PluginEnvironment? Environment { get; private set; }
    private long _lastPrint;

    /// <summary>
    /// 1 shows the occlusion, 2 the sun shadow, 3 the normals, 4 depth contours, 5 the lot's depth against the
    /// terrain's, 6 the lamp light, 7 the contact shadows, 8 each pixel's lot point, 9 the height map under it, 10 the
    /// sun shadow's terms, 11 sun through windows and 12 the cloud shadows, unsmoothed, in place of the lot.
    /// </summary>
    public int AmbientDebug { get; set; }

    private GPUTexture? _heightMapTexture;
    private float _heightMargin = 4f;
    private GPUTexture? _footMapTexture;
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
    private ulong? _trailTicks;
    private float _trailFill;
    private readonly List<Vector4> _pendingPrints = [];
    private GPUTexture? _surfaceLiftTexture;
    private GPUTexture? _emptyLiftTexture;
    private GPUTexture? _simMaskTexture;
    private GPUTexture? _emptySimMaskTexture;
    private GPUTexture? _surfaceSourceTexture;

    public LotRenderer(GPUContext gpu)
    {
        GPU = gpu ?? throw new ArgumentNullException(nameof(gpu));
    }

    public bool IsLoadingScene => _sceneLoadsInFlight > 0;
    public long SceneRevisionNumber => _sceneRevision;
    public long AnimationSequenceNumber => _animationSequence;
    public int ViewLevel => Scene?.SelectedLevel ?? 1;
    public bool RoofsVisible => Scene?.RoofsVisible ?? false;
    public int StoryCount => Scene?.Stories ?? 2;
    public SimsSDL.Core.LotCamera CameraOrientation => Scene?.Camera ?? SimsSDL.Core.LotCamera.TopLeft;

    /// <summary>
    /// Uploads the overlay's new and changed UI textures and drops the ones it no longer draws. Uploads go into the
    /// current batch, so call it inside `GPU.BatchingUploads` ahead of `Encode`.
    /// </summary>
    public void UploadOverlay()
    {
        if (_placementDirty)
        {
            _placementDirty = false;
            _placementBuffer = GPU.MakeStorageBuffer(_placementVertices);
            _ghostTextures = GPU.UploadTextures(_ghostPixels);
            _ghostPixels = [];
        }

        if (_floorBrushDirty)
        {
            _floorBrushDirty = false;
            _floorBrushBuffer = GPU.MakeStorageBuffer(_floorBrushVertices);
            if (_floorBrushPixels is { } pixels)
            {
                _floorBrushPixels = null;
                var uploaded = GPU.UploadTextures([(pixels.Width, pixels.Height, pixels.Depth8, pixels.Pixels)]);
                _floorBrushTexture = uploaded.Count > 0 ? uploaded[0] : null;
            }
            else if (_floorBrushTextureKey is null)
            {
                _floorBrushTexture = null;
            }
        }

        if (_buildCursorPixels is { } cursorPixels)
        {
            _buildCursorPixels = null;
            _buildCursorTextures = GPU.UploadTextures(cursorPixels);
        }

        if (_wallPaintPixels is { } wallPixels)
        {
            _wallPaintPixels = null;
            _wallPaintTextures = GPU.UploadTextures(wallPixels);
        }

        var next = new Dictionary<UITexture, (UITexture Source, int Version, GPUTexture Texture)>(ReferenceEqualityComparer.Instance);
        List<UITexture> uploads = [];
        foreach (UIDraw item in Overlay)
        {
            UITexture texture = item.Texture;
            if (next.ContainsKey(texture))
            {
                continue;
            }

            if (_uiTextures.TryGetValue(texture, out var cached) && cached.Version == texture.Version)
            {
                next[texture] = cached;
            }
            else if (!uploads.Exists(u => ReferenceEquals(u, texture)))
            {
                uploads.Add(texture);
            }
        }

        if (uploads.Count > 0)
        {
            var imageBatch = new (int Width, int Height, bool Depth8, byte[] Pixels)[uploads.Count];
            for (int i = 0; i < uploads.Count; i++)
            {
                UITexture u = uploads[i];
                imageBatch[i] = (u.Width, u.Height, false, u.Pixels);
            }

            IReadOnlyList<GPUTexture> uploadedTextures = GPU.UploadTextures(imageBatch);
            for (int i = 0; i < uploads.Count; i++)
            {
                UITexture source = uploads[i];
                next[source] = (source, source.Version, uploadedTextures[i]);
            }
        }

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

    private static string ResolveRealPath(string path)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try
        {
            string? parent = Path.GetDirectoryName(fullPath);
            string leaf = Path.GetFileName(fullPath);
            if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(leaf))
            {
                fullPath = Path.Combine(ResolveRealPath(parent), leaf);
            }

            if (File.Exists(fullPath))
            {
                return Path.TrimEndingDirectorySeparator(
                    File.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName ?? fullPath);
            }

            if (Directory.Exists(fullPath))
            {
                return Path.TrimEndingDirectorySeparator(
                    Directory.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName ?? fullPath);
            }
        }
        catch
        {
            // Fall back to full path when link resolution fails.
        }

        return fullPath;
    }

    public void LoadScene(string path, long revision = 0)
    {
        (LotScene Scene, List<byte[]> Pixels) loaded = LotScene.Load(path);
        PreparedScene prepared = PrepareScene(loaded, path, SnapshotCachedTextureSpecs());
        GPU.BatchingUploads(() => Install(prepared, path, revision));
    }

    private Dictionary<string, (int Width, int Height, bool Depth8)> SnapshotCachedTextureSpecs()
    {
        var specs = new Dictionary<string, (int Width, int Height, bool Depth8)>(_cachedTextures.Count + _dynamicTextureCache.Count);
        foreach (var (key, tex) in _dynamicTextureCache)
        {
            specs[key] = (tex.Width, tex.Height, tex.Format == SDL_GPU_TEXTUREFORMAT_R8_UNORM);
        }

        foreach (var (key, tex) in _cachedTextures)
        {
            specs[key] = (tex.Width, tex.Height, tex.Format == SDL_GPU_TEXTUREFORMAT_R8_UNORM);
        }

        return specs;
    }

    private static PreparedScene PrepareScene(
        (LotScene Scene, List<byte[]> Pixels) loaded,
        string path,
        Dictionary<string, (int Width, int Height, bool Depth8)> cachedSpecs)
    {
        string rootDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
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
                        slots[i] = PrepareUpload(loaded.Scene.Textures[texIndex], loaded.Pixels[texIndex]);
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
                    preparedUploads[texIndex] = PrepareUpload(loaded.Scene.Textures[texIndex], loaded.Pixels[texIndex]);
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
            Pixels = loaded.Pixels,
            PreparedUploads = preparedUploads,
            DrawItems = drawItems,
            AllMeshBounds = allMeshBounds,
            StaticMeshBounds = staticMeshBounds,
            GroundPicker = groundPicker,
        };
    }

    /// <summary>
    /// Decodes a host scene snapshot on a background thread (a lot's is several megabytes of JSON), then installs it on
    /// the main thread and calls `loaded`, unless a newer snapshot was requested meanwhile.
    /// </summary>
    public void LoadSceneInBackground(string path, long revision, Action<Exception?> loaded)
    {
        _newestRequestedRevision = Math.Max(_newestRequestedRevision, revision);
        _sceneLoadsInFlight += 1;
        Dictionary<string, (int Width, int Height, bool Depth8)> cachedSpecs = SnapshotCachedTextureSpecs();
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

    private void CacheDynamicTexture(GPUTexture texture, byte[]? rawPixels, string key)
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
                    _dynamicTextureCache.Remove(evicted);
                    _dynamicPixelCache.Remove(evicted);
                }

                _dynamicTextureOrder.RemoveRange(0, overflow);
            }
        }

        _dynamicTextureCache[key] = texture;
        if (rawPixels is not null)
        {
            _dynamicPixelCache[key] = rawPixels;
        }
    }

    private void Install(PreparedScene loaded, string path, long revision)
    {
        int textureCount = loaded.Scene.Textures.Count;
        var textures = new GPUTexture?[textureCount];
        List<(int Index, (int Width, int Height, bool Depth8, byte[] Pixels) Image)> uploads = [];
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
            GPUTexture? cached = _cachedTextures.GetValueOrDefault(key) ?? _dynamicTextureCache.GetValueOrDefault(key);
            if (cached is not null && cached.Width == image.Width && cached.Height == image.Height && cached.Format == format)
            {
                textures[index] = cached;
            }
            else
            {
                var preparedImage = loaded.PreparedUploads.TryGetValue(index, out var precomputed)
                    ? precomputed
                    : PrepareUpload(image, loaded.Pixels[index]);
                uploads.Add((index, preparedImage));
            }
        }

        if (uploads.Count > 0)
        {
            var batch = new (int Width, int Height, bool Depth8, byte[] Pixels)[uploads.Count];
            for (int i = 0; i < uploads.Count; i++)
            {
                batch[i] = uploads[i].Image;
            }

            IReadOnlyList<GPUTexture> uploaded = GPU.UploadTextures(batch);
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

        if (Scene is null || loaded.Scene.Camera != Scene.Camera || loaded.Scene.SelectedLevel != Scene.SelectedLevel)
        {
            WallPaintPreview = null;
            _wallPaintPixels = null;
            _wallPaintTextures = [];
        }

        Scene = loaded.Scene;
        _scenePixels = [.. loaded.Pixels];
        _deferredPixelTextures = [];
        SceneRoot = rootDir;
        _sceneRevision = revision;
        _animationSequence = 0;
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
        _indoorMask = MakeIndoorMask(loaded.Scene);
        _overheadMap = MakeOverheadMap(loaded.Scene);

        var nextCachedTextures = new Dictionary<string, GPUTexture>(keys.Count);
        var nextCachedPixels = new Dictionary<string, byte[]>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            nextCachedTextures[keys[i]] = resolved[i];
            nextCachedPixels[keys[i]] = loaded.Pixels[i];
        }

        _cachedTextures = nextCachedTextures;
        _cachedPixels = nextCachedPixels;
    }

    public void QueueAnimation(SceneAnimation frame) => _pendingFrames.Add(frame);

    /// <summary>
    /// Applies the queued animation frames before a draw. Frames for the currently installed `SceneRevisionNumber` apply immediately
    /// (even while a newer background scene snapshot is still decoding off-thread, preventing 150-250 ms Sim freezes), while
    /// frames targeting a newer `BaseRevision > SceneRevisionNumber` remain queued until that snapshot finishes installing.
    /// </summary>
    public void ApplyPendingAnimation(nint command = 0)
    {
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

        int last = matching.Count - 1;
        GPU.BatchingUploads(command, () =>
        {
            for (int index = 0; index < matching.Count; index++)
            {
                SceneAnimation frame = matching[index];
                if (index < last)
                {
                    if (frame.Textures.Count == 0)
                    {
                        continue;
                    }

                    frame = frame with { Sprites = [], Meshes = [] };
                }

                ApplyAnimation(frame);
            }
        });
    }

    public void ApplyAnimation(SceneAnimation frame)
    {
        if (Scene is null || SceneRoot is null)
        {
            throw new ContentError("Animation requires a scene snapshot");
        }

        LotScene updated = frame.Applying(Scene, _staticSprites, _staticMeshes, _sceneRevision, _animationSequence);
        if (frame.Textures.Count > 0)
        {
            int count = frame.Textures.Count;
            var resolvedNew = new GPUTexture?[count];
            var resolvedPixels = new byte[]?[count];
            List<int> missingIndices = [];
            List<LotScene.Texture> missingTextures = [];
            List<string> missingKeys = [];

            for (int offset = 0; offset < count; offset++)
            {
                LotScene.Texture image = frame.Textures[offset];
                var format = string.Equals(image.Encoding, "depth8", StringComparison.Ordinal)
                    ? SDL_GPU_TEXTUREFORMAT_R8_UNORM
                    : GPUContext.ColorFormat;

                if (_textureIndexByFile.TryGetValue(image.File, out int existingIndex) && existingIndex < _sceneTextures.Count)
                {
                    GPUTexture existing = _sceneTextures[existingIndex];
                    if (existing.Width == image.Width && existing.Height == image.Height && existing.Format == format)
                    {
                        resolvedNew[offset] = existing;
                        if (existingIndex < _scenePixels.Count)
                        {
                            resolvedPixels[offset] = _scenePixels[existingIndex];
                        }

                        continue;
                    }
                }

                string key = CacheTexturePath(SceneRoot, image.File);
                GPUTexture? cached = _cachedTextures.GetValueOrDefault(key) ?? _dynamicTextureCache.GetValueOrDefault(key);
                byte[]? raw = _cachedPixels.GetValueOrDefault(key) ?? _dynamicPixelCache.GetValueOrDefault(key);
                if (cached is not null && cached.Width == image.Width && cached.Height == image.Height && cached.Format == format && raw is not null)
                {
                    resolvedNew[offset] = cached;
                    resolvedPixels[offset] = raw;
                }
                else
                {
                    missingIndices.Add(offset);
                    missingTextures.Add(image);
                    missingKeys.Add(key);
                }
            }

            if (missingTextures.Count > 0)
            {
                string standardizedRoot = ResolveRealPath(SceneRoot);
                List<byte[]> rawData = LotScene.ReadPixels(missingTextures, standardizedRoot);
                var uploadBatch = new (int Width, int Height, bool Depth8, byte[] Pixels)[missingTextures.Count];
                for (int i = 0; i < missingTextures.Count; i++)
                {
                    uploadBatch[i] = PrepareUpload(missingTextures[i], rawData[i]);
                }

                IReadOnlyList<GPUTexture> uploaded = GPU.UploadTextures(uploadBatch);
                for (int i = 0; i < uploaded.Count; i++)
                {
                    int idx = missingIndices[i];
                    GPUTexture texture = uploaded[i];
                    resolvedNew[idx] = texture;
                    resolvedPixels[idx] = rawData[i];
                    CacheDynamicTexture(texture, rawData[i], missingKeys[i]);
                }
            }

            bool anyMissingPixel = false;
            for (int offset = 0; offset < count; offset++)
            {
                LotScene.Texture image = frame.Textures[offset];
                int index = _sceneTextures.Count;
                _sceneTextures.Add(resolvedNew[offset]!);
                _textureIndexByFile[image.File] = index;
                if (!anyMissingPixel && _scenePixels.Count == index && resolvedPixels[offset] is { } raw)
                {
                    _scenePixels.Add(raw);
                }
                else
                {
                    anyMissingPixel = true;
                }
            }

            if (anyMissingPixel)
            {
                _deferredPixelTextures.AddRange(frame.Textures);
            }
        }

        IReadOnlyList<LotScene.Mesh> allUpdatedMeshes = updated.Meshes ?? [];
        List<LotScene.Mesh> dynamicMeshes = allUpdatedMeshes.Count > _staticMeshCount
            ? allUpdatedMeshes.Skip(_staticMeshCount).ToList()
            : [];
        List<MeshSlice?> dynamicSlices = dynamicMeshes.Count == 0 ? [] : MakeCombinedMeshes(dynamicMeshes);
        _meshSlices = [.. _staticMeshSlices, .. dynamicSlices];
        _meshBounds = dynamicMeshes.Count == 0
            ? _staticMeshBounds
            : [.. _staticMeshBounds, .. ScenePicker.ComputeMeshBounds(dynamicMeshes)];

        int totalSprites = _staticSpriteCount + frame.Sprites.Count;
        List<OrderedDrawEntry> dynamicEntries = new(frame.Sprites.Count + dynamicMeshes.Count);
        for (int offset = 0; offset < frame.Sprites.Count; offset++)
        {
            LotScene.Sprite sprite = frame.Sprites[offset];
            int spriteIndex = _staticSpriteCount + offset;
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

        dynamicEntries.Sort((a, b) => OrderedDrawEntry.Compare(in a, in b, totalSprites));

        List<SceneDrawItem> merged = new(_staticDrawEntries.Count + dynamicEntries.Count);
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
        _drawItems = merged;
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
            Dictionary<string, int> existingByFile = new(_scenePixels.Count);
            int limit = Math.Min(_scenePixels.Count, Scene.Textures.Count);
            for (int i = 0; i < limit; i++)
            {
                existingByFile.TryAdd(Scene.Textures[i].File, i);
            }

            List<LotScene.Texture> unread = [];
            Dictionary<string, byte[]> unreadMap = new();
            foreach (LotScene.Texture image in _deferredPixelTextures)
            {
                if (!existingByFile.ContainsKey(image.File) && unreadMap.TryAdd(image.File, []))
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
                    unreadMap[unread[i].File] = loaded[i];
                }
            }

            foreach (LotScene.Texture image in _deferredPixelTextures)
            {
                if (existingByFile.TryGetValue(image.File, out int existingIndex))
                {
                    _scenePixels.Add(_scenePixels[existingIndex]);
                }
                else if (unreadMap.TryGetValue(image.File, out byte[]? data))
                {
                    int newIndex = _scenePixels.Count;
                    _scenePixels.Add(data);
                    existingByFile[image.File] = newIndex;
                }
                else
                {
                    return null;
                }
            }

            _deferredPixelTextures.Clear();
        }

        if (_scenePixels.Count != Scene.Textures.Count)
        {
            return null;
        }

        IReadOnlyList<LotScene.Mesh> allMeshes = Scene.Meshes ?? [];
        Vector4[]? precomputedBounds;
        if (_staticMeshBounds.Length == _staticMeshCount && allMeshes.Count >= _staticMeshCount)
        {
            if (allMeshes.Count == _staticMeshCount)
            {
                precomputedBounds = _staticMeshBounds;
            }
            else
            {
                List<LotScene.Mesh> dynamicSlice = allMeshes.Skip(_staticMeshCount).ToList();
                precomputedBounds = [.. _staticMeshBounds, .. ScenePicker.ComputeMeshBounds(dynamicSlice)];
            }
        }
        else
        {
            precomputedBounds = null;
        }

        var built = new ScenePicker(Scene, _scenePixels, _drawItems, precomputedBounds);
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
        // sRGB colours: the terrain shader mixes the shade ramp in sRGB like the original 16-bit tables, then linearises.
        static float Unit(byte component) => component / 255f;

        IReadOnlyList<LotScene.TerrainVertex> source = vertices ?? scene.TerrainVertices ?? [];
        if (source.Count == 0)
        {
            return null;
        }

        float[]? groundDepth = depths ?? TerrainDepth(scene);
        var buffer = new TerrainVertex[source.Count];
        for (int index = 0; index < source.Count; index++)
        {
            LotScene.TerrainVertex vertex = source[index];
            var light = new Vector4(Unit(vertex.R), Unit(vertex.G), Unit(vertex.B), 1f);
            // Older scenes carry no dark shade; the native default grass ratio keeps blades visible there.
            Vector4 dark = vertex.DR is byte dr
                ? new Vector4(Unit(dr), Unit(vertex.DG ?? 0), Unit(vertex.DB ?? 0), 1f)
                : light * new Vector4(0.2f, 0.45f, 0.15f, 1f);
            float depth = groundDepth is not null ? groundDepth[index] : 1f;
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

    /// <summary>
    /// The depth key of each terrain vertex, 0.9 - 0.003 (x + y) in camera tile coordinates. Measured at the bases of
    /// sprites standing on it, this meets their depth on flat lots and within about one unit on hilly ones, nearer than
    /// adding the elevation term does; `AmbientParameters.GroundOffset` shifts it.
    /// </summary>
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

        // corners of each tile's six vertices: (x, y), (x+1, y), (x+1, y+1), (x+1, y+1), (x, y+1), (x, y)
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

    /// <summary>
    /// Moves to another zoom level, keeping the scene point at the view centre where it was.
    /// </summary>
    public void SetZoom(LotZoom level)
    {
        if (level == Zoom)
        {
            return;
        }

        Vector2 rescaled = SimsSDL.Core.SceneViewport.RescaledPan(_pan, Zoom.Factor(), level.Factor());
        Zoom = level;
        Pan = rescaled;
    }

    private void UpdateAmbientMaps(Vector2 canvas, nint command)
    {
        if (Scene is null || !DrawsLot || !AmbientEnabled || Environment is null)
        {
            return;
        }

        float scale = MathF.Max(0.01f, UIScale * Zoom.Factor());
        float coverage = MathF.Ceiling(canvas.X / (128f * scale) + canvas.Y / (64f * scale)) + 16f;
        if (coverage > _heightMargin)
        {
            _heightMargin = coverage;
            GPU.BatchingUploads(command, () =>
            {
                _indoorMask = MakeIndoorMask(Scene);
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

        // The lot runs down to the control panel's 100-pixel bar; the panel's taller parts overlap it, as in the original.
        return new SimsSDL.Core.SceneViewport(Scene, canvas, LiveMode ? 100f * UIScale : 0f, Zoom, UIScale, _pan);
    }

    public bool ContainsObject(int id) =>
        Scene is not null &&
        (Scene.Sprites.Any(s => s.ObjectID == id) || (Scene.Meshes?.Any(m => m.ObjectID == id) ?? false));

    /// <summary>
    /// The window rectangle an object's sprites (or a Sim's meshes) cover, for placing UI beside it; nil when it is not drawn.
    /// </summary>
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

    /// <summary>
    /// Projects a lot tile center through the same camera and viewport used for drawing and picking.
    /// </summary>
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
            // Older lot exports omit originX/Y. Match lotFrame's camera-corner fallback.
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

        // Float coordinates are already in tile units and can include a sub-tile offset (for example a Sim's
        // Position / 16). Integer tile callers use the overload above, which targets the tile center.
        Vector2 projected = Scene.Camera.ProjectedPoint(new Vector2(x, y), lotSize);
        return viewport.Origin + (projected - origin) * viewport.Scale;
    }

    /// <summary>
    /// Pans the lot view to keep a tile center in view, using the same clamped camera pan as object centering.
    /// </summary>
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

    /// <summary>
    /// The viewport's scale and origin, for placing screen-space feedback over scene points.
    /// </summary>
    public SimsSDL.Core.SceneViewport? SceneViewport(Vector2 canvas) => Viewport(canvas);

    private void SetPlacement(
        TerrainVertex[] vertices,
        PlacementPreview.GhostData? ghost = null,
        (int Width, int Height, bool Depth8, byte[] Pixels)[]? pixels = null)
    {
        _placementVertices = vertices;
        _placementGhost = ghost;
        _ghostPixels = pixels ?? [];
        _ghostTextures = [];
        _placementBuffer = null;
        _placementDirty = true;
        if (_floorBrushVertices.Length > 0 || _floorBrushBuffer is not null)
        {
            _floorBrushVertices = [];
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

    /// <summary>
    /// Shows the build cursor the host drew for the hovered vertex (its `buildCursor` reply), or hides it.
    /// </summary>
    public void UpdateBuildCursor(PlacementPreview.GhostData? ghost)
    {
        if (ghost is null || SceneRoot is null)
        {
            BuildCursor = null;
            _buildCursorPixels = null;
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
        _buildCursorTextures = [];
        BuildCursor = ghost;
    }

    /// <summary>
    /// Shows the wallpaper tool's live preview on the hovered wall or room (its `buildCursor` `wallPreview`), or hides it.
    /// </summary>
    public void UpdateWallPaintPreview(PlacementPreview.GhostData? ghost)
    {
        if (ghost is null || SceneRoot is null)
        {
            WallPaintPreview = null;
            _wallPaintPixels = null;
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
        _wallPaintTextures = [];
        WallPaintPreview = ghost;
    }

    public int? ObjectAt(Vector2 point, Vector2 canvas)
    {
        if (_cachedObjectHit is { } cached &&
            cached.Point == point &&
            cached.Canvas == canvas &&
            cached.Zoom == Zoom &&
            cached.Revision == _sceneRevision &&
            cached.Sequence == _animationSequence)
        {
            return cached.Result;
        }

        if (PickScenePoint(point, canvas) is not { } location)
        {
            return null;
        }

        int? hit = EnsurePicker()?.ObjectAt(location, Zoom);
        _cachedObjectHit = new CachedObjectHit(point, canvas, Zoom, _sceneRevision, _animationSequence, hit);
        return hit;
    }

    public LotScene.WallSide? WallAt(Vector2 point, Vector2 canvas)
    {
        if (PickScenePoint(point, canvas) is not { } location)
        {
            return null;
        }

        return EnsurePicker()?.WallAt(location, Zoom);
    }

    /// <summary>
    /// Shows a Sim preview (from `HouseholdAppearancePreview`) at `PreviewPlacement`, as the macOS renderer does.
    /// </summary>
    public void UpdateAppearancePreview(SimsSDL.Core.HouseholdAppearancePreview preview, IReadOnlyList<byte[]> pixels, int key = 0)
    {
        preview.Validate();
        if (pixels.Count != preview.Textures.Count)
        {
            throw new ContentError("Household preview texture data does not match its manifest");
        }

        var uploadBatch = new (int Width, int Height, bool Depth8, byte[] Pixels)[preview.Textures.Count];
        for (int i = 0; i < preview.Textures.Count; i++)
        {
            LotScene.Texture metadata = preview.Textures[i];
            uploadBatch[i] = (metadata.Width, metadata.Height, false, SpritePixels.PremultipliedSRGB(pixels[i]));
        }

        IReadOnlyList<GPUTexture> uploaded = GPU.UploadTextures(uploadBatch);
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

        _previews[key] = new PreviewMeshes
        {
            Textures = meshTextures,
            Buffers = MakeMeshes(preview.Meshes),
            VertexCounts = vertexCounts,
            Bounds = bounds,
        };
    }

    /// <summary>
    /// Drops one preview, or every preview when `key` is null.
    /// </summary>
    public void ClearAppearancePreview(int? key = null)
    {
        if (key is int k)
        {
            _previews.Remove(k);
        }
        else
        {
            _previews.Clear();
        }
    }

    /// <summary>
    /// Clears the targets and draws the lot into them, as the Metal view's lot pass does, then the overlay. A Sim
    /// preview splits the overlay: a second pass keeps the colour, clears the depth and draws the preview's meshes.
    /// </summary>
    public void Encode(nint command, GPUTexture color, GPUTexture depth)
    {
        var canvas = new Vector2(color.Width, color.Height);
        UpdateAmbientMaps(canvas, command);

        bool previewing = PreviewPlacement is { } pp && pp.Rects.Keys.Any(k => _previews.ContainsKey(k));
        int split = previewing ? Math.Min(Overlay.Count, Math.Max(0, PreviewPlacement!.Value.Index + 1)) : Overlay.Count;

        nint pass = BeginPass(command, color, depth, clearColor: true);
        try
        {
            if (DrawsLot)
            {
                EncodeLot(pass, command, canvas);
            }

            if (DrawsLot && AmbientEnabled && Environment is not null && Viewport(canvas) is { } viewport)
            {
                SDL_EndGPURenderPass(pass);
                pass = 0;

                GPUTexture ground = GroundTexture(color.Width, color.Height);
                EncodeGroundDepth(command, ground, viewport, canvas);
                (GPUTexture Texture, Vector4? Frame) heightMap = EncodeHeightMap(command, depth, viewport, canvas);
                (GPUTexture Texture, Vector4? Frame) footMap = EncodeHeightMap(command, depth, viewport, canvas, feet: true);
                EncodeAmbient(command, color, depth, ground, heightMap, footMap.Texture, viewport, canvas);
                if (Scene?.CensorRects is { Count: > 0 } censors)
                {
                    EncodeCensorRects(censors, command, color, depth, viewport, canvas);
                }

                pass = BeginPass(command, color, depth, clearColor: false);
            }
            else if (DrawsLot && Scene?.CensorRects is { Count: > 0 } censors && Viewport(canvas) is { } censorViewport)
            {
                SDL_EndGPURenderPass(pass);
                pass = 0;

                EncodeCensorRects(censors, command, color, depth, censorViewport, canvas);
                pass = BeginPass(command, color, depth, clearColor: false);
            }

            EncodeOverlay(CollectionsMarshal.AsSpan(Overlay).Slice(0, split), pass, command, canvas);
            if (previewing && PreviewPlacement is { } placement)
            {
                SDL_EndGPURenderPass(pass);
                pass = 0;

                pass = BeginPass(command, color, depth, clearColor: false);
                foreach (var (key, rect) in placement.Rects.OrderBy(kv => kv.Key))
                {
                    if (_previews.TryGetValue(key, out PreviewMeshes? preview))
                    {
                        EncodePreview(preview, rect, pass, command, canvas);
                    }
                }

                EncodeOverlay(CollectionsMarshal.AsSpan(Overlay).Slice(split), pass, command, canvas);
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

    private unsafe nint BeginPass(nint command, GPUTexture color, GPUTexture depth, bool clearColor)
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
            store_op = SDL_GPU_STOREOP_STORE,
        };

        var depthTarget = new SDL_GPUDepthStencilTargetInfo
        {
            texture = depth.Handle,
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

        var nextShaders = new Dictionary<string, string>();
        if (value is not null && effect is not null)
        {
            foreach (var (key, shaderPath) in effect.Shaders)
            {
                string stage = key is "groundDepth" or "heightScatter" or "fullscreen" ? ".vert" : ".frag";
                nextShaders[key + stage] = value.Asset(shaderPath);
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

    /// <summary>
    /// The lot's height map: a top-down depth target over the lot (with a margin of `_heightMargin` tiles), into which
    /// `heightScatter.vert` drops every sampled pixel of the lot's depth at its reconstructed point, keeping the highest.
    /// </summary>
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

    /// <summary>
    /// Which tiles are indoors and where windows let the sun in: bytes per camera tile laid out like the height map's
    /// grid (`_heightMargin` tiles around the lot, one texel per tile), in six bands: the first floor's rooms, the second
    /// floor's, then each floor's altitude, then each floor's diagonal halves. In the room bands bit 0 marks a window part, and the rest is 0 outside and
    /// otherwise a code for the indoor room (its index mod 127, plus one), so a change of code between neighbouring tiles
    /// is a wall. An altitude byte is 0 where there is no floor, else 1 + eighths of a tile above `_indoorBase`. A half
    /// byte is 0 where the room fills its tile, else the floor half (1 right, 2 left, 3 top, 4 bottom on screen) that
    /// lies indoors where a diagonal wall splits the tile and only one side is a room.
    /// </summary>
    // The pack shader ABI uses half-band bits 0x80 for Cement and 0x40 for roads outside the lot.
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

        // A source tile's cell in camera tiles (see LotFrame for where the source corner lands).
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
        Dictionary<int, byte> halves = new();
        HashSet<int> whole = [];

        foreach (LotScene.Mesh mesh in scene.Meshes ?? [])
        {
            if (mesh.Floor is not { } floor || TryCell(floor.X, floor.Y, floor.Level ?? 1) is not { } at)
            {
                continue;
            }

            // A whole tile's vertices centre on the tile's middle, which lies 16 (x + y + 1) scene pixels below the
            // frame less 40 per tile of altitude.
            if (floor.Half == 0 && mesh.Vertices.Count > 0)
            {
                float sumY = 0f;
                for (int i = 0; i < mesh.Vertices.Count; i++)
                {
                    sumY += mesh.Vertices[i].Y;
                }

                float y = sumY / mesh.Vertices.Count;
                altitudes.Add((at.Index + 2 * rows * columns, (frame.Y + 16f * (at.X + at.Y + 1) - y) / 40f));
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
                mask[index + 4 * rows * columns] = half;
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

    /// <summary>
    /// Fills the lot's height map (or with `feet` its foot map) from the lot's depth; returns the frame and texels per
    /// tile of the height map, which the ambient pass reads both with, or null when the scene has no terrain to place it by.
    /// </summary>
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
        _groundDepthTexture = texture;
        return texture;
    }

    /// <summary>
    /// The ambient pass's unsmoothed light, which `AmbientBlur` reads.
    /// </summary>
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

    /// <summary>
    /// Where Sims show on the lot (1) for the season pass to keep free of snow and turned leaves; a 1x1 target stands in
    /// when no Sim is drawn.
    /// </summary>
    private unsafe GPUTexture EncodeSimMask(nint command, GPUTexture depth, SimsSDL.Core.SceneViewport viewport, Vector2 canvas)
    {
        uint usage = SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER;
        List<int> sims = [];
        if (Scene?.Meshes is { } meshes)
        {
            for (int i = 0; i < meshes.Count && i < _meshSlices.Count; i++)
            {
                if (meshes[i].Unlit != true && _meshSlices[i] is not null)
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
            mask = GPU.MakeTexture(depth.Width, depth.Height, GPUContext.MaskFormat, usage);
            _simMaskTexture = mask;
        }

        var colorTarget = new SDL_GPUColorTargetInfo
        {
            texture = mask.Handle,
            load_op = SDL_GPU_LOADOP_CLEAR,
            store_op = SDL_GPU_STOREOP_STORE,
        };

        // The lot's depth hides the parts of a Sim behind walls and objects, as in the lot pass.
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
                if (_meshSlices[index] is not { } slice)
                {
                    continue;
                }

                var binding = new SDL_GPUTextureSamplerBinding
                {
                    texture = _sceneTextures[mesh.TextureFor(Zoom)].Handle,
                    sampler = GPU.Sampler,
                };
                SDL_BindGPUFragmentSamplers(pass, 0, &binding, 1);

                if (boundBufferHandle != slice.Buffer.Handle)
                {
                    boundBufferHandle = slice.Buffer.Handle;
                    nint handle = slice.Buffer.Handle;
                    SDL_BindGPUVertexStorageBuffers(pass, 0, &handle, 1);
                }

                var transform = new Quad(new Vector4(viewport.Origin.X, viewport.Origin.Y, viewport.Scale, viewport.Scale), canvas);
                if (CharacterDetail == 0)
                {
                    transform.UvRect = new Vector4(1f, transform.UvRect.Y, transform.UvRect.Z, transform.UvRect.W);
                }

                SDL_PushGPUVertexUniformData(command, 0, (nint)(&transform), (uint)sizeof(Quad));
                SDL_DrawGPUPrimitives(pass, slice.VertexCount, 1, slice.FirstVertex, 0);
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
        _surfaceSourceTexture = texture;
        return texture;
    }

    /// <summary>
    /// Draws the terrain's depth into its own target: level-1 floors and bare terrain draw without depth in the lot pass,
    /// and the ambient pass reads this where the lot's depth is clear.
    /// </summary>
    private unsafe void EncodeGroundDepth(nint command, GPUTexture depth, SimsSDL.Core.SceneViewport viewport, Vector2 canvas)
    {
        if (_terrainBuffer is null)
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

    /// <summary>
    /// Lights the lot from its depth: the ambient pass writes occlusion times sun shadow to a target of its own, and the
    /// blur pass smooths it and multiplies it into the lot's colour. With a debug view the pass writes over the lot.
    /// </summary>
    private unsafe void EncodeAmbient(
        nint command,
        GPUTexture color,
        GPUTexture depth,
        GPUTexture ground,
        (GPUTexture Texture, Vector4? Frame) heightMap,
        GPUTexture footMap,
        SimsSDL.Core.SceneViewport viewport,
        Vector2 canvas)
    {
        // The view-space basis of the lot camera (x right, y down the screen, z toward the camera), in scene pixels.
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

        // The clouds blow with the running clock (a still render holds `CloudTime`), on the lot's own tiles.
        float seconds = Ambient.CloudTime ?? (SDL_GetTicks() % 3_600_000UL) / 1000f;
        Vector2 drift = Ambient.Wind * seconds;
        float orientation = ((Scene?.CameraOrientation ?? 0) % 4 + 4) % 4;
        var lotSize = new Vector2(Scene?.LotWidth ?? 0, Scene?.LotHeight ?? 0);

        // The wind turned from the lot's tiles to the camera's, and the rain's slant on screen from it.
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
            ulong ticks = SDL_GetTicks();
            float elapsed = Ambient.CloudTime is null ? (ticks - (_trailTicks ?? ticks)) / 1000f : 0f;
            _trailTicks = ticks;
            _trailFill += (Environment?.Effect.TrailRecovery ?? 0f) * MathF.Min(elapsed, 1f);
            string key = $"{Scene?.House ?? 0}-{lotSize.X}x{lotSize.Y}";
            if (!string.Equals(_trailKey, key, StringComparison.Ordinal))
            {
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

        // A target holds eighths of a unit, so the fill waits until it amounts to a few of them.
        float fill = _trailFill >= 0.02f ? _trailFill : 0f;
        if (fill > 0f)
        {
            _trailFill = 0f;
        }

        const int uniformCount = 13 + MaxLamps + MaxPrints;
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
        // Remaining lamp and print slots stay zero-initialized by stackalloc.
        offset += MaxLamps;
        for (int i = 0; i < prints.Count; i++)
        {
            uniforms[offset + i] = prints[i];
        }

        var defaultScissor = new SDL_Rect { x = 0, y = 0, w = (int)canvas.X, h = (int)canvas.Y };
        GPUTexture term = AmbientDebug > 0 ? color : AmbientTexture(color.Width, color.Height);

        GPUTexture lift = EmptyLift();
        if (Ambient.SurfaceMode != 0 && AmbientDebug == 0)
        {
            // The season pass reads the lot's colour from a copy while it writes over it.
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

            GPUTexture trail = EmptyIndoorMask();
            if (displaced && _trailTextures.Count == 2)
            {
                GPUTexture previous = _trailTextures[_trailIndex];
                _trailIndex = 1 - _trailIndex;
                trail = _trailTextures[_trailIndex];
                RunAmbientPass(command, in defaultScissor, uniforms, uniformCount, GPUContext.Program.Trail, trail, false, [previous]);
            }

            if (displaced)
            {
                lift = LiftTexture(color.Width, color.Height);
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
                [depth, ground, heightMap.Texture, _indoorMask ?? EmptyIndoorMask(), source, trail, _overheadMap ?? EmptyOverheadMap(), sims],
                lift);
        }

        RunAmbientPass(
            command,
            in defaultScissor,
            uniforms,
            uniformCount,
            GPUContext.Program.Ambient,
            term,
            AmbientDebug > 0,
            [depth, ground, heightMap.Texture, _indoorMask ?? EmptyIndoorMask(), footMap, lift, _overheadMap ?? EmptyOverheadMap()]);

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

    /// <summary>
    /// Sims.exe cLotRenderer::DrawCensorRects (004ac916.c / 004ac510.c): pixelates censored Sim regions in 4-scene-unit
    /// mosaic blocks while leaving foreground walls, shower doors, and tub rims (depth &lt; censor.Depth) sharp.
    /// </summary>
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

    private unsafe void EncodePreview(PreviewMeshes preview, Vector4 rect, nint pass, nint command, Vector2 canvas)
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
        SDL_BindGPUGraphicsPipeline(pass, GPU.Pipeline(GPUContext.Program.Avatar, GPUContext.DepthMode.Mesh));

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
            if (preview.Buffers[index] is not { } buffer)
            {
                continue;
            }

            var binding = new SDL_GPUTextureSamplerBinding
            {
                texture = preview.Textures[index].Handle,
                sampler = GPU.Sampler,
            };
            SDL_BindGPUFragmentSamplers(pass, 0, &binding, 1);
            nint handle = buffer.Handle;
            SDL_BindGPUVertexStorageBuffers(pass, 0, &handle, 1);

            var transform = new Quad(drawRect, canvas);
            SDL_PushGPUVertexUniformData(command, 0, (nint)(&transform), (uint)sizeof(Quad));
            SDL_DrawGPUPrimitives(pass, (uint)preview.VertexCounts[index], 1, 0, 0);
        }
    }

    private unsafe void EncodeOverlay(ReadOnlySpan<UIDraw> items, nint pass, nint command, Vector2 canvas)
    {
        if (items.IsEmpty)
        {
            return;
        }

        var scissor = new SDL_Rect { x = 0, y = 0, w = (int)canvas.X, h = (int)canvas.Y };
        SDL_SetGPUScissor(pass, in scissor);
        SDL_BindGPUGraphicsPipeline(pass, GPU.Pipeline(GPUContext.Program.Sprite, GPUContext.DepthMode.Flat));
        nint overlaySampler = GPU.Sampler;

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

    private unsafe void EncodeLot(nint pass, nint command, Vector2 canvas)
    {
        if (Scene is not { } scene || Viewport(canvas) is not { } viewport)
        {
            return;
        }

        float scale = viewport.Scale;
        Vector2 origin = viewport.Origin;

        // Floor and architectural surface meshes stay crisp on whole UI scales; anti-aliased sprites use linear
        // hardware filtering when minified and analytical subpixel + diagonal reconstruction when magnified.
        nint artSampler = UIScale == MathF.Round(UIScale, MidpointRounding.AwayFromZero) ? GPU.PixelSampler : GPU.Sampler;
        nint spriteSampler = AntiAliasedSprites ? GPU.Sampler : artSampler;
        LotZoom zoom = Zoom;
        SimsSDL.Core.SceneLighting? lighting = ActiveLighting(scene);

        var scissor = new SDL_Rect { x = 0, y = 0, w = (int)canvas.X, h = (int)canvas.Y };
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

            nint resolved = (program, depth) switch
            {
                (GPUContext.Program.SpriteDepth, GPUContext.DepthMode.Mesh) => GPU.SpriteDepthPipeline,
                (GPUContext.Program.SpriteDepth, GPUContext.DepthMode.MeshEdge) => GPU.SpriteDepthEdgePipeline,
                _ => GPU.Pipeline(program, depth),
            };
            SDL_BindGPUGraphicsPipeline(pass, resolved);
            currentPipeline = (program, depth);
            currentSingleSampler = null;
            currentDualSamplers = null;
        }

        void BindTexture(GPUTexture texture, nint sampler)
        {
            nint th = texture.Handle;
            if (currentSingleSampler is { } cur && cur.Tex == th && cur.Sampler == sampler)
            {
                return;
            }

            var binding = new SDL_GPUTextureSamplerBinding { texture = th, sampler = sampler };
            SDL_BindGPUFragmentSamplers(pass, 0, &binding, 1);
            currentSingleSampler = (th, sampler);
            currentDualSamplers = null;
        }

        void BindTextures(GPUTexture first, nint firstSampler, GPUTexture second, nint secondSampler)
        {
            nint t0 = first.Handle;
            nint t1 = second.Handle;
            if (currentDualSamplers is { } cur && cur.T0 == t0 && cur.S0 == firstSampler && cur.T1 == t1 && cur.S1 == secondSampler)
            {
                return;
            }

            SDL_GPUTextureSamplerBinding* bindings = stackalloc SDL_GPUTextureSamplerBinding[2];
            bindings[0] = new SDL_GPUTextureSamplerBinding { texture = t0, sampler = firstSampler };
            bindings[1] = new SDL_GPUTextureSamplerBinding { texture = t1, sampler = secondSampler };
            SDL_BindGPUFragmentSamplers(pass, 0, bindings, 2);
            currentDualSamplers = (t0, firstSampler, t1, secondSampler);
            currentSingleSampler = null;
        }

        void BindVertices(GPUBuffer buffer)
        {
            if (currentVertexBuffer == buffer.Handle)
            {
                return;
            }

            currentVertexBuffer = buffer.Handle;
            nint handle = buffer.Handle;
            SDL_BindGPUVertexStorageBuffers(pass, 0, &handle, 1);
        }

        void Push(in Quad quad)
        {
            Quad copy = quad;
            SDL_PushGPUVertexUniformData(command, 0, (nint)(&copy), (uint)sizeof(Quad));
        }

        if (_terrainBuffer is not null)
        {
            Bind(GPUContext.Program.Terrain, GPUContext.DepthMode.Flat);
            BindVertices(_terrainBuffer);
            var transform = new Quad(new Vector4(origin.X, origin.Y, scale, scale), canvas)
            {
                Tint = lighting?.Outside.Multiplier ?? Vector4.One,
                UvRect = new Vector4(TerrainDetail, (float)zoom, 0f, 0f),
            };
            Push(in transform);
            SDL_DrawGPUPrimitives(pass, (uint)_terrainBuffer.Count, 1, 0, 0);
        }

        void DrawFloorShadow(LotScene.Sprite sprite)
        {
            if (!ShadowsEnabled || !LiveMode || sprite.Wall is not null ||
                sprite.FloorShadowImage(zoom) is not { } shadow)
            {
                return;
            }

            var shadowRect = new Vector4(sprite.X + shadow.X, sprite.Y + shadow.Y, shadow.Width, shadow.Height);
            if (origin.X + (shadowRect.X + shadowRect.Z) * scale < 0f ||
                origin.Y + (shadowRect.Y + shadowRect.W) * scale < 0f ||
                origin.X + shadowRect.X * scale >= canvas.X ||
                origin.Y + shadowRect.Y * scale >= canvas.Y)
            {
                return;
            }

            Bind(GPUContext.Program.Sprite, GPUContext.DepthMode.Flat);
            BindTexture(_sceneTextures[shadow.Texture], spriteSampler);
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

            if (origin.X + (rect.X + rect.Z) * scale < 0f ||
                origin.Y + (rect.Y + rect.W) * scale < 0f ||
                origin.X + rect.X * scale >= canvas.X ||
                origin.Y + rect.Y * scale >= canvas.Y)
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
                BindTextures(colorTex, spriteSampler, depthTex, GPU.PixelSampler);
                float depthNear = sprite.DepthNear ?? 0f;
                float depthFar = sprite.DepthFar ?? 0f;
                quad.DepthInterval = passMode switch
                {
                    // Non-AA SPR2 blitters (004bdbcc.c:95) skip raw 5-bit alpha < 8.
                    SpritePassMode.Single => new Vector4(depthNear, depthFar, 8.0f / 31.0f, 0f),
                    SpritePassMode.OpaqueCore => new Vector4(depthNear, depthFar, 0.985f, 1f),
                    SpritePassMode.EdgeFringe => new Vector4(depthNear, depthFar, 0.985f, 2f),
                    _ => default,
                };
            }
            else
            {
                Bind(GPUContext.Program.Sprite, GPUContext.DepthMode.Flat);
                BindTexture(colorTex, spriteSampler);
                quad.DepthInterval = new Vector4(0f, 0f, 8.0f / 31.0f, AntiAliasedSprites ? 1f : 0f);
            }

            quad.Tint = lighting?.Multiplier(sprite.Room) ?? Vector4.One;
            Push(in quad);
            SDL_DrawGPUPrimitives(pass, 6, 1, 0, 0);
        }

        SpritePassMode primaryPass = AntiAliasedSprites ? SpritePassMode.OpaqueCore : SpritePassMode.Single;
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
            BindTexture(_floorBrushTexture, artSampler);
            BindVertices(_floorBrushBuffer);
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
                foreach (LotScene.Sprite sprite in scene.Sprites)
                {
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
                        if (origin.X + (shadowRect.X + shadowRect.Z) * scale < 0f ||
                            origin.Y + (shadowRect.Y + shadowRect.W) * scale < 0f ||
                            origin.X + shadowRect.X * scale >= canvas.X ||
                            origin.Y + shadowRect.Y * scale >= canvas.Y)
                        {
                            continue;
                        }

                        Bind(GPUContext.Program.Sprite, GPUContext.DepthMode.Flat);
                        BindTexture(_sceneTextures[shadow.Texture], spriteSampler);
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
                BindTexture(batch.Texture, batch.Sampler);
                BindVertices(batch.Buffer);
                var transform = new Quad(new Vector4(origin.X, origin.Y, scale, scale), canvas)
                {
                    Tint = batch.Tint,
                };
                transform.UvRect = new Vector4(batch.UVRectX, transform.UvRect.Y, transform.UvRect.Z, transform.UvRect.W);
                Push(in transform);
                SDL_DrawGPUPrimitives(pass, batch.VertexCount, 1, batch.FirstVertex, 0);
            }

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
                        if (origin.X + b.Z * scale < 0f ||
                            origin.Y + b.W * scale < 0f ||
                            origin.X + b.X * scale >= canvas.X ||
                            origin.Y + b.Y * scale >= canvas.Y)
                        {
                            continue;
                        }
                    }

                    if (scene.Meshes is null || index >= scene.Meshes.Count || index >= _meshSlices.Count || _meshSlices[index] is not { } slice)
                    {
                        continue;
                    }

                    LotScene.Mesh mesh = scene.Meshes[index];
                    bool unlit = mesh.Unlit == true;
                    GPUContext.Program prog = unlit ? GPUContext.Program.Surface : GPUContext.Program.Avatar;
                    GPUContext.DepthMode depthMode = unlit && mesh.Floor?.Water is null && (mesh.Level ?? 1) == 1
                        ? GPUContext.DepthMode.Flat
                        : GPUContext.DepthMode.Mesh;
                    GPUTexture tex = _sceneTextures[mesh.TextureFor(zoom)];
                    nint samp = mesh.Roof is not null ? GPU.RoofSampler : mesh.Floor is not null ? artSampler : GPU.Sampler;
                    Vector4 tint = lighting?.Multiplier(mesh.Room) ?? Vector4.One;
                    float uvX = (!unlit && CharacterDetail == 0) ? 1f : 0f;

                    if (pendingMesh is { } existing &&
                        existing.Program == prog &&
                        existing.Depth == depthMode &&
                        existing.Texture.Handle == tex.Handle &&
                        existing.Sampler == samp &&
                        existing.Buffer.Handle == slice.Buffer.Handle &&
                        existing.Tint == tint &&
                        existing.UVRectX == uvX &&
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

            if (AntiAliasedSprites && hasDepthSprites)
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

        if (ShowsPlacement && _placementBuffer is not null)
        {
            Bind(GPUContext.Program.Placement, GPUContext.DepthMode.Flat);
            BindVertices(_placementBuffer);
            Push(new Quad(new Vector4(origin.X, origin.Y, scale, scale), canvas));
            SDL_DrawGPUPrimitives(pass, (uint)_placementBuffer.Count, 1, 0, 0);
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

                BindTexture(textures[texture], artSampler);
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

/// <summary>
/// One original-UI image drawn over the lot: a rectangle in canvas pixels and the part of the texture it shows.
/// </summary>
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
