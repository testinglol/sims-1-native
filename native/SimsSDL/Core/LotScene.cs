using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace SimsSDL.Core;

public sealed record LotScene
{
    public sealed record Texture
    {
        [JsonConstructor]
        public Texture() { }

        [SetsRequiredMembers]
        public Texture(string file, int width, int height, string? encoding = null)
        {
            File = file;
            Width = width;
            Height = height;
            Encoding = encoding;
        }

        [JsonPropertyName("file")]
        public required string File { get; init; }

        [JsonPropertyName("width")]
        public required int Width { get; init; }

        [JsonPropertyName("height")]
        public required int Height { get; init; }

        [JsonPropertyName("encoding")]
        public string? Encoding { get; init; }
    }

    /// <summary>Tracks a sound source's tile coordinate and altitude in pixels, allowing positional audio
    /// for off-screen sources without active sprites.</summary>
    public sealed record SoundSource
    {
        [JsonConstructor]
        public SoundSource() { }

        [SetsRequiredMembers]
        public SoundSource(int objectID, int level, float x, float y, float z)
        {
            ObjectID = objectID;
            Level = level;
            X = x;
            Y = y;
            Z = z;
        }

        [JsonPropertyName("objectID")]
        public required int ObjectID { get; init; }

        [JsonPropertyName("level")]
        public required int Level { get; init; }

        [JsonPropertyName("x")]
        public required float X { get; init; }

        [JsonPropertyName("y")]
        public required float Y { get; init; }

        [JsonPropertyName("z")]
        public required float Z { get; init; }
    }

    public sealed record WallSide
    {
        [JsonConstructor]
        public WallSide() { }

        [SetsRequiredMembers]
        public WallSide(int x, int y, int level, int direction, int? cut = null, int? edge = null, int? side = null)
        {
            Cut = cut;
            X = x;
            Y = y;
            Level = level;
            Direction = direction;
            Edge = edge;
            Side = side;
        }

        [JsonPropertyName("cut")]
        public int? Cut { get; init; }

        [JsonPropertyName("x")]
        public required int X { get; init; }

        [JsonPropertyName("y")]
        public required int Y { get; init; }

        [JsonPropertyName("level")]
        public required int Level { get; init; }

        [JsonPropertyName("direction")]
        public required int Direction { get; init; }

        [JsonPropertyName("edge")]
        public int? Edge { get; init; }

        [JsonPropertyName("side")]
        public int? Side { get; init; }

        /// <summary>Maps the scene picker direction to an <c>Arry</c> wall edge flag, representing cardinal
        /// directions (0–3) or diagonal boundaries (0x10 or 0x20).</summary>
        [JsonIgnore]
        public int? EdgeBit =>
            Edge is 1 or 2 or 4 or 8 or 16 or 32
                ? Edge
                : Direction is >= 0 and <= 3 ? 1 << Direction : null;
    }

    public sealed record WallDepth
    {
        [JsonConstructor]
        public WallDepth() { }

        [SetsRequiredMembers]
        public WallDepth(int x, int y, int level, int configuration)
        {
            X = x;
            Y = y;
            Level = level;
            Configuration = configuration;
        }

        [JsonPropertyName("x")]
        public required int X { get; init; }

        [JsonPropertyName("y")]
        public required int Y { get; init; }

        [JsonPropertyName("level")]
        public required int Level { get; init; }

        [JsonPropertyName("configuration")]
        public required int Configuration { get; init; }
    }

    public sealed record WallJunction
    {
        [JsonConstructor]
        public WallJunction() { }

        [SetsRequiredMembers]
        public WallJunction(int x, int y, int level, int flags, int frame, bool down)
        {
            X = x;
            Y = y;
            Level = level;
            Flags = flags;
            Frame = frame;
            Down = down;
        }

        [JsonPropertyName("x")]
        public required int X { get; init; }

        [JsonPropertyName("y")]
        public required int Y { get; init; }

        [JsonPropertyName("level")]
        public required int Level { get; init; }

        [JsonPropertyName("flags")]
        public required int Flags { get; init; }

        [JsonPropertyName("frame")]
        public required int Frame { get; init; }

        [JsonPropertyName("down")]
        public required bool Down { get; init; }
    }

    /// <summary>Represents alternate-zoom sprite offsets and dimensions in medium-zoom units, where near
    /// zoom provides two texels per unit and far zoom provides half a texel.</summary>
    public sealed record ZoomImage
    {
        [JsonConstructor]
        public ZoomImage() { }

        [SetsRequiredMembers]
        public ZoomImage(int texture, float x, float y, float width, float height, int? depthTexture = null)
        {
            Texture = texture;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            DepthTexture = depthTexture;
        }

        [JsonPropertyName("texture")]
        public required int Texture { get; init; }

        [JsonPropertyName("x")]
        public required float X { get; init; }

        [JsonPropertyName("y")]
        public required float Y { get; init; }

        [JsonPropertyName("width")]
        public required float Width { get; init; }

        [JsonPropertyName("height")]
        public required float Height { get; init; }

        [JsonPropertyName("depthTexture")]
        public int? DepthTexture { get; init; }
    }

    public sealed record Sprite
    {
        [JsonConstructor]
        public Sprite() { }

        [JsonPropertyName("texture")]
        public required int Texture { get; init; }

        [JsonPropertyName("x")]
        public required float X { get; init; }

        [JsonPropertyName("y")]
        public required float Y { get; init; }

        [JsonPropertyName("width")]
        public required float Width { get; init; }

        [JsonPropertyName("height")]
        public required float Height { get; init; }

        [JsonPropertyName("flip")]
        public required bool Flip { get; init; }

        [JsonPropertyName("objectID")]
        public int? ObjectID { get; init; }

        /// <summary>OBJD field 57, specifying the object's directional floor-shadow type index.</summary>
        [JsonPropertyName("shadow")]
        public int? Shadow { get; init; }

        /// <summary>Signed OBJD field 79, specifying a palette brightness adjustment for the object's
        /// shadow.</summary>
        [JsonPropertyName("shadowBrightness")]
        public int? ShadowBrightness { get; init; }

        /// <summary>Pre-rendered floor shadow surface loaded from <c>floors.iff</c> for shadow IDs 0 and
        /// 16; other IDs use dynamic lookup surfaces.</summary>
        [JsonPropertyName("floorShadow")]
        public ZoomImage? FloorShadow { get; init; }

        [JsonPropertyName("floorShadowNear")]
        public ZoomImage? FloorShadowNear { get; init; }

        [JsonPropertyName("floorShadowFar")]
        public ZoomImage? FloorShadowFar { get; init; }

        [JsonPropertyName("layer")]
        public int? Layer { get; init; }

        [JsonPropertyName("depth")]
        public float? Depth { get; init; }

        [JsonPropertyName("depthTexture")]
        public int? DepthTexture { get; init; }

        [JsonPropertyName("depthNear")]
        public float? DepthNear { get; init; }

        [JsonPropertyName("depthFar")]
        public float? DepthFar { get; init; }

        [JsonPropertyName("wall")]
        public WallSide? Wall { get; init; }

        [JsonPropertyName("junction")]
        public WallJunction? Junction { get; init; }

        [JsonPropertyName("architectureDepth")]
        public WallDepth? ArchitectureDepth { get; init; }

        [JsonPropertyName("room")]
        public int? Room { get; init; }

        [JsonPropertyName("level")]
        public int? Level { get; init; }

        [JsonPropertyName("near")]
        public ZoomImage? Near { get; init; }

        [JsonPropertyName("far")]
        public ZoomImage? Far { get; init; }

        [JsonPropertyName("hideAtFarZoom")]
        public bool? HideAtFarZoom { get; init; }

        public ZoomImage? Image(LotZoom zoom) => zoom switch
        {
            LotZoom.Near => Near,
            LotZoom.Far => Far,
            LotZoom.Medium => null,
            _ => null
        };

        public ZoomImage? FloorShadowImage(LotZoom zoom) => zoom switch
        {
            LotZoom.Near => FloorShadowNear ?? FloorShadow,
            LotZoom.Far => FloorShadowFar ?? FloorShadow,
            _ => FloorShadow
        };
    }

    public readonly record struct MeshVertex
    {
        [JsonConstructor]
        public MeshVertex() { }

        [SetsRequiredMembers]
        public MeshVertex(float x, float y, float z, float u, float v, float nx, float ny, float nz)
        {
            X = x;
            Y = y;
            Z = z;
            U = u;
            V = v;
            NX = nx;
            NY = ny;
            NZ = nz;
        }

        [JsonPropertyName("x")]
        public required float X { get; init; }

        [JsonPropertyName("y")]
        public required float Y { get; init; }

        [JsonPropertyName("z")]
        public required float Z { get; init; }

        [JsonPropertyName("u")]
        public required float U { get; init; }

        [JsonPropertyName("v")]
        public required float V { get; init; }

        [JsonPropertyName("nx")]
        public required float NX { get; init; }

        [JsonPropertyName("ny")]
        public required float NY { get; init; }

        [JsonPropertyName("nz")]
        public required float NZ { get; init; }
    }

    public sealed record WaterSurface
    {
        [JsonConstructor]
        public WaterSurface() { }

        [SetsRequiredMembers]
        public WaterSurface(int adjacency, ushort sprite, IReadOnlyList<ushort> corners)
        {
            Adjacency = adjacency;
            Sprite = sprite;
            Corners = corners;
        }

        [JsonPropertyName("adjacency")]
        public required int Adjacency { get; init; }

        [JsonPropertyName("sprite")]
        public required ushort Sprite { get; init; }

        [JsonPropertyName("corners")]
        public required IReadOnlyList<ushort> Corners { get; init; }

        public void Validate(ushort pattern)
        {
            if (Adjacency is < 0 or > 255 || pattern < 65534 || Corners is null)
                throw new ContentError("Invalid water surface topology");

            int variant = ((Adjacency & 1) != 0 ? 1 : 0)
                        | ((Adjacency & 64) != 0 ? 2 : 0)
                        | ((Adjacency & 16) != 0 ? 4 : 0)
                        | ((Adjacency & 4) != 0 ? 8 : 0);

            int[] pairs = [65, 80, 20, 5];
            int[] diagonal = [128, 32, 8, 2];
            var expected = pattern == 65535
                ? Enumerable.Range(0, 4)
                    .Where(i => (Adjacency & pairs[i]) == pairs[i] && (Adjacency & diagonal[i]) == 0)
                    .Select(i => (ushort)(0x434 + i))
                    .ToArray()
                : [];

            ushort expectedSprite = (ushort)((pattern == 65535 ? 0x410 : 0x800) + variant);
            if (Sprite != expectedSprite || !Corners.SequenceEqual(expected))
                throw new ContentError("Water surface sprites do not match adjacent tiles");
        }
    }

    public sealed record FloorSurface
    {
        [JsonConstructor]
        public FloorSurface() { }

        [SetsRequiredMembers]
        public FloorSurface(int x, int y, ushort pattern, int half, WaterSurface? water = null, int? level = null)
        {
            X = x;
            Y = y;
            Pattern = pattern;
            Half = half;
            Water = water;
            Level = level;
        }

        [JsonPropertyName("x")]
        public required int X { get; init; }

        [JsonPropertyName("y")]
        public required int Y { get; init; }

        [JsonPropertyName("pattern")]
        public required ushort Pattern { get; init; }

        [JsonPropertyName("half")]
        public required int Half { get; init; }

        [JsonPropertyName("water")]
        public WaterSurface? Water { get; init; }

        [JsonPropertyName("level")]
        public int? Level { get; init; }
    }

    public sealed record RoofSurface
    {
        [JsonConstructor]
        public RoofSurface() { }

        [SetsRequiredMembers]
        public RoofSurface(uint style, float pitch, string file, string? part = null)
        {
            Part = part;
            Style = style;
            Pitch = pitch;
            File = file;
        }

        [JsonPropertyName("part")]
        public string? Part { get; init; }

        [JsonPropertyName("style")]
        public required uint Style { get; init; }

        [JsonPropertyName("pitch")]
        public required float Pitch { get; init; }

        [JsonPropertyName("file")]
        public required string File { get; init; }
    }

    public sealed record Mesh
    {
        [JsonConstructor]
        public Mesh() { }

        [SetsRequiredMembers]
        public Mesh(
            int texture,
            int objectID,
            float depth,
            IReadOnlyList<MeshVertex> vertices,
            int? room = null,
            int? shadow = null,
            int? shadowBrightness = null,
            ZoomImage? floorShadow = null,
            ZoomImage? floorShadowNear = null,
            ZoomImage? floorShadowFar = null,
            RoofSurface? roof = null,
            FloorSurface? floor = null,
            int? level = null,
            int? layer = null,
            bool? unlit = null,
            int? nearTexture = null,
            int? farTexture = null)
        {
            Room = room;
            Shadow = shadow;
            ShadowBrightness = shadowBrightness;
            FloorShadow = floorShadow;
            FloorShadowNear = floorShadowNear;
            FloorShadowFar = floorShadowFar;
            Roof = roof;
            Floor = floor;
            Level = level;
            Layer = layer;
            Unlit = unlit;
            Texture = texture;
            ObjectID = objectID;
            Depth = depth;
            Vertices = vertices;
            NearTexture = nearTexture;
            FarTexture = farTexture;
        }

        [JsonPropertyName("room")]
        public int? Room { get; init; }

        /// <summary>OBJD field 57, specifying the directional floor-shadow type index for an avatar mesh.</summary>
        [JsonPropertyName("shadow")]
        public int? Shadow { get; init; }

        /// <summary>Signed OBJD field 79, specifying the shadow palette brightness adjustment for an avatar
        /// mesh.</summary>
        [JsonPropertyName("shadowBrightness")]
        public int? ShadowBrightness { get; init; }

        [JsonPropertyName("floorShadow")]
        public ZoomImage? FloorShadow { get; init; }

        [JsonPropertyName("floorShadowNear")]
        public ZoomImage? FloorShadowNear { get; init; }

        [JsonPropertyName("floorShadowFar")]
        public ZoomImage? FloorShadowFar { get; init; }

        [JsonPropertyName("roof")]
        public RoofSurface? Roof { get; init; }

        [JsonPropertyName("floor")]
        public FloorSurface? Floor { get; init; }

        [JsonPropertyName("level")]
        public int? Level { get; init; }

        [JsonPropertyName("layer")]
        public int? Layer { get; init; }

        [JsonPropertyName("unlit")]
        public bool? Unlit { get; init; }

        [JsonPropertyName("texture")]
        public required int Texture { get; init; }

        [JsonPropertyName("objectID")]
        public required int ObjectID { get; init; }

        [JsonPropertyName("depth")]
        public required float Depth { get; init; }

        [JsonPropertyName("vertices")]
        public required IReadOnlyList<MeshVertex> Vertices { get; init; }

        [JsonPropertyName("nearTexture")]
        public int? NearTexture { get; init; }

        [JsonPropertyName("farTexture")]
        public int? FarTexture { get; init; }

        [JsonPropertyName("tint")]
        public IReadOnlyList<float>? Tint { get; init; }

        /// <summary>Floor texture for the given zoom level; UV coordinates span exactly one tile at every
        /// zoom.</summary>
        public int TextureFor(LotZoom zoom) => zoom switch
        {
            LotZoom.Near => NearTexture ?? Texture,
            LotZoom.Far => FarTexture ?? Texture,
            _ => Texture
        };

        public ZoomImage? FloorShadowImage(LotZoom zoom) => zoom switch
        {
            LotZoom.Near => FloorShadowNear ?? FloorShadow,
            LotZoom.Far => FloorShadowFar ?? FloorShadow,
            _ => FloorShadow
        };
    }

    public readonly record struct TerrainVertex
    {
        [JsonConstructor]
        public TerrainVertex() { }

        [SetsRequiredMembers]
        public TerrainVertex(
            float x,
            float y,
            byte r,
            byte g,
            byte b,
            byte? dr = null,
            byte? dg = null,
            byte? db = null,
            byte? grass = null)
        {
            X = x;
            Y = y;
            R = r;
            G = g;
            B = b;
            DR = dr;
            DG = dg;
            DB = db;
            Grass = grass;
        }

        [JsonPropertyName("x")]
        public required float X { get; init; }

        [JsonPropertyName("y")]
        public required float Y { get; init; }

        [JsonPropertyName("r")]
        public required byte R { get; init; }

        [JsonPropertyName("g")]
        public required byte G { get; init; }

        [JsonPropertyName("b")]
        public required byte B { get; init; }

        /// <summary>Encodes the darkest shade of the terrain vertex grass palette and its wear state (0 for
        /// green, 255 for brown).</summary>
        [JsonPropertyName("dr")]
        public byte? DR { get; init; }

        [JsonPropertyName("dg")]
        public byte? DG { get; init; }

        [JsonPropertyName("db")]
        public byte? DB { get; init; }

        [JsonPropertyName("grass")]
        public byte? Grass { get; init; }
    }

    /// <summary>Overhead architectural map encoded in RGBA8 format: story coverage bitmask, high and low
    /// bytes of biased terrain altitude (in 1/256 tile units), and cell validity.</summary>
    public sealed record OverheadMapData
    {
        [JsonConstructor]
        public OverheadMapData() { }

        [SetsRequiredMembers]
        public OverheadMapData(int width, int height, int samplesPerTile, byte[] pixels)
        {
            Width = width;
            Height = height;
            SamplesPerTile = samplesPerTile;
            Pixels = pixels;
        }

        [JsonPropertyName("width")]
        public required int Width { get; init; }

        [JsonPropertyName("height")]
        public required int Height { get; init; }

        [JsonPropertyName("samplesPerTile")]
        public required int SamplesPerTile { get; init; }

        [JsonPropertyName("pixels")]
        public required byte[] Pixels { get; init; }
    }

    public readonly record struct CensorRect
    {
        [JsonConstructor]
        public CensorRect() { }

        [SetsRequiredMembers]
        public CensorRect(float x, float y, float width, float height, float depth)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Depth = depth;
        }

        [JsonPropertyName("x")]
        public required float X { get; init; }

        [JsonPropertyName("y")]
        public required float Y { get; init; }

        [JsonPropertyName("width")]
        public required float Width { get; init; }

        [JsonPropertyName("height")]
        public required float Height { get; init; }

        [JsonPropertyName("depth")]
        public required float Depth { get; init; }
    }

    /// <summary>Tracks validated counts of static architecture, textures, sprites, and meshes so animation
    /// frames only validate newly appended entries.</summary>
    internal readonly record struct ValidatedState(int Textures = 0, int Sprites = 0, int Meshes = 0, bool Architecture = false)
    {
        public static readonly ValidatedState None = new();
    }

    [JsonConstructor]
    public LotScene() { }

    [JsonPropertyName("overheadMap")]
    public OverheadMapData? OverheadMap { get; init; }

    [JsonPropertyName("censorRects")]
    public IReadOnlyList<CensorRect>? CensorRects { get; init; }

    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("lighting")]
    public SceneLighting? Lighting { get; init; }

    [JsonPropertyName("wallMode")]
    public string? WallMode { get; init; }

    [JsonPropertyName("cameraOrientation")]
    public int? CameraOrientation { get; init; }

    [JsonPropertyName("house")]
    public required int House { get; init; }

    [JsonPropertyName("width")]
    public required float Width { get; init; }

    [JsonPropertyName("height")]
    public required float Height { get; init; }

    [JsonPropertyName("lotWidth")]
    public int? LotWidth { get; init; }

    [JsonPropertyName("lotHeight")]
    public int? LotHeight { get; init; }

    [JsonPropertyName("floorDiagonals")]
    public IReadOnlyList<byte>? FloorDiagonals { get; init; }

    [JsonPropertyName("roofsVisible")]
    public bool? RoofsVisible { get; init; }

    [JsonPropertyName("viewLevel")]
    public int? ViewLevel { get; init; }

    /// <summary>Specifies the projection origin offset applied to scene sprites, used to align audio source
    /// positions.</summary>
    [JsonPropertyName("originX")]
    public float? OriginX { get; init; }

    [JsonPropertyName("originY")]
    public float? OriginY { get; init; }

    [JsonPropertyName("soundSources")]
    public IReadOnlyList<SoundSource>? SoundSources { get; init; }

    [JsonPropertyName("stories")]
    public int? Stories { get; init; }

    [JsonPropertyName("placementVertices")]
    public IReadOnlyList<TerrainVertex>? PlacementVertices { get; init; }

    [JsonIgnore]
    public int SelectedLevel => ViewLevel ?? 1;

    [JsonIgnore]
    public LotCamera Camera => CameraOrientation is int orientation && Enum.IsDefined(typeof(LotCamera), orientation)
        ? (LotCamera)orientation
        : LotCamera.TopLeft;

    [JsonIgnore]
    public IReadOnlyList<TerrainVertex>? ConstructionVertices => PlacementVertices ?? TerrainVertices;

    [JsonPropertyName("textures")]
    public required IReadOnlyList<Texture> Textures { get; init; }

    [JsonPropertyName("sprites")]
    public required IReadOnlyList<Sprite> Sprites { get; init; }

    [JsonPropertyName("terrainVertices")]
    public IReadOnlyList<TerrainVertex>? TerrainVertices { get; init; }

    [JsonPropertyName("terrainEditable")]
    public IReadOnlyList<bool>? TerrainEditable { get; init; }

    [JsonPropertyName("scenery")]
    public IReadOnlyList<IReadOnlyList<Sprite>>? Scenery { get; init; }

    [JsonPropertyName("terrainHeights")]
    public IReadOnlyList<short>? TerrainHeights { get; init; }

    [JsonPropertyName("meshes")]
    public IReadOnlyList<Mesh>? Meshes { get; init; }

    [JsonPropertyName("limitations")]
    public required IReadOnlyList<string> Limitations { get; init; }

    public static LotScene FromJson(string json) =>
        JsonSerializer.Deserialize<LotScene>(json) ?? throw new ContentError("Lot scene JSON decoded to null");

    public static LotScene FromJson(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<LotScene>(utf8Json) ?? throw new ContentError("Lot scene JSON decoded to null");

    public static (LotScene Scene, List<byte[]> Pixels) Load(string path)
    {
        if (SimsSimulation.InMemorySessionAssets.TryGetPayload<SimsSimulation.SceneExporter.ExportedLotScene>(path, out var exported)
            && exported is not null)
        {
            var directScene = FromExported(exported);
            directScene.Validate();
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))
                ?? throw new ContentError("Lot scene path has no parent directory");
            string memoryRoot = SimsSimulation.InMemorySessionAssets.Canonicalize(directory);
            return (directScene, ReadPixels(directScene.Textures, memoryRoot));
        }

        byte[] bytes;
        string root;
        if (SimsSimulation.InMemorySessionAssets.TryReadBytes(path, out var inMemoryBytes))
        {
            if (inMemoryBytes.Length > 64 * 1024 * 1024)
                throw new ContentError("Lot scene file exceeds 64 MiB");
            bytes = inMemoryBytes;
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))
                ?? throw new ContentError("Lot scene path has no parent directory");
            root = SimsSimulation.InMemorySessionAssets.Canonicalize(directory);
        }
        else
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
                throw new FileNotFoundException("Lot scene file not found", path);
            if (fileInfo.Length > 64 * 1024 * 1024)
                throw new ContentError("Lot scene file exceeds 64 MiB");

            bytes = File.ReadAllBytes(fileInfo.FullName);
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))
                ?? throw new ContentError("Lot scene path has no parent directory");
            root = ResolveRealPath(directory);
        }

        var scene = JsonSerializer.Deserialize<LotScene>(bytes)
            ?? throw new ContentError("Lot scene JSON decoded to null");
        scene.Validate();
        var pixels = ReadPixels(scene.Textures, root);
        return (scene, pixels);
    }

    internal static LotScene FromExported(SimsSimulation.SceneExporter.ExportedLotScene payload)
    {
        var diagonals = new byte[payload.FloorDiagonals.Length];
        for (int i = 0; i < payload.FloorDiagonals.Length; i++)
            diagonals[i] = (byte)payload.FloorDiagonals[i];

        var sounds = new SoundSource[payload.SoundSources.Length];
        for (int i = 0; i < payload.SoundSources.Length; i++)
        {
            var s = payload.SoundSources[i];
            sounds[i] = new SoundSource(s.ObjectID, s.Level, s.X, s.Y, s.Z);
        }

        int terrainCount = payload.Terrain.Length;
        var placementVertices = new TerrainVertex[terrainCount];
        var terrainVertices = new TerrainVertex[terrainCount];
        int lotSize = payload.LotSize;
        for (int i = 0; i < terrainCount; i++)
        {
            var vertex = payload.Terrain[i];
            placementVertices[i] = new TerrainVertex(
                vertex.X, vertex.Y - payload.PlacementLift,
                (byte)vertex.R, (byte)vertex.G, (byte)vertex.B,
                (byte)vertex.Dr, (byte)vertex.Dg, (byte)vertex.Db, (byte)vertex.Grass);

            int tileIndex = i / 6;
            int tx = tileIndex % lotSize;
            int ty = tileIndex / lotSize;
            var v = (tx == 0 || tx == lotSize - 1 || ty == 0 || ty == lotSize - 1)
                ? payload.Terrain[tileIndex * 6]
                : vertex;
            terrainVertices[i] = new TerrainVertex(
                v.X, v.Y,
                (byte)v.R, (byte)v.G, (byte)v.B,
                (byte)v.Dr, (byte)v.Dg, (byte)v.Db, (byte)v.Grass);
        }

        var meshes = new Mesh[payload.Meshes.Length];
        for (int i = 0; i < payload.Meshes.Length; i++)
        {
            var m = payload.Meshes[i];
            meshes[i] = new Mesh
            {
                Texture = m.Texture,
                ObjectID = m.ObjectID,
                Depth = m.Depth,
                Vertices = ToMeshVertices(m.Vertices),
                Level = m.Level,
                Room = m.Room,
                Unlit = m.Unlit,
                Floor = m.Floor is { } floor
                    ? new FloorSurface(
                        floor.X, floor.Y, floor.Pattern, floor.Half,
                        floor.Water is { } water
                            ? new WaterSurface(water.Adjacency, water.Sprite, water.Corners)
                            : null,
                        floor.Level)
                    : null,
                NearTexture = m.NearTexture,
                FarTexture = m.FarTexture,
                Layer = m.Layer,
                Roof = m.Roof is { } roof
                    ? new RoofSurface(roof.Style, roof.Pitch, roof.File, roof.Part)
                    : null,
                Shadow = m.Shadow,
                ShadowBrightness = m.ShadowBrightness,
                FloorShadow = ToZoomImage(m.FloorShadow),
                FloorShadowNear = ToZoomImage(m.FloorShadowNear),
                FloorShadowFar = ToZoomImage(m.FloorShadowFar),
                Tint = m.Tint
            };
        }

        return new LotScene
        {
            SchemaVersion = 1,
            House = payload.House,
            Width = payload.Width,
            Height = payload.Height,
            LotWidth = payload.LotSize,
            LotHeight = payload.LotSize,
            FloorDiagonals = diagonals,
            TerrainHeights = payload.TerrainHeights,
            CameraOrientation = payload.CameraOrientation,
            OriginX = payload.OriginX,
            OriginY = payload.OriginY,
            WallMode = payload.WallMode,
            RoofsVisible = payload.RoofsVisible,
            ViewLevel = payload.ViewLevel,
            Stories = payload.Stories,
            Lighting = ToLighting(payload.Lighting),
            Textures = ToTextures(payload.Textures),
            Sprites = ToSprites(payload.Sprites),
            SoundSources = sounds,
            PlacementVertices = placementVertices,
            TerrainVertices = terrainVertices,
            Meshes = meshes,
            CensorRects = ToCensorRects(payload.CensorRects),
            Limitations = payload.Limitations
        };
    }

    internal static SceneLighting? ToLighting(global::SceneLighting.State? state)
    {
        if (state is null)
            return null;

        var rooms = new SceneLighting.Room[state.Rooms.Length];
        for (int i = 0; i < state.Rooms.Length; i++)
        {
            var r = state.Rooms[i];
            rooms[i] = new SceneLighting.Room(r.Id, r.Outside, r.Ambient, r.Daylight, r.R, r.G, r.B);
        }

        SceneLighting.Light[]? lights = null;
        if (state.Lights is { } sourceLights)
        {
            lights = new SceneLighting.Light[sourceLights.Length];
            for (int i = 0; i < sourceLights.Length; i++)
            {
                var l = sourceLights[i];
                lights[i] = new SceneLighting.Light(l.X, l.Y, l.Toward, l.Strength, l.Level);
            }
        }

        SceneLighting.Window[]? windows = null;
        if (state.Windows is { } sourceWindows)
        {
            windows = new SceneLighting.Window[sourceWindows.Length];
            for (int i = 0; i < sourceWindows.Length; i++)
            {
                var w = sourceWindows[i];
                windows[i] = new SceneLighting.Window(w.X, w.Y, w.Level);
            }
        }

        return new SceneLighting(
            new SceneLighting.RGB(state.Outside.R, state.Outside.G, state.Outside.B),
            rooms,
            state.Minutes,
            state.Phase,
            lights,
            windows,
            new SceneLighting.RGB(state.Backdrop.R, state.Backdrop.G, state.Backdrop.B));
    }

    internal static Texture[] ToTextures(SimsSimulation.SceneExporter.Texture[] source)
    {
        if (source.Length == 0)
            return Array.Empty<Texture>();
        var result = new Texture[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            var t = source[i];
            result[i] = new Texture(t.File, t.Width, t.Height, t.Encoding);
        }
        return result;
    }

    internal static Sprite[] ToSprites(SimsSimulation.SceneExporter.SceneSprite[] source)
    {
        if (source.Length == 0)
            return Array.Empty<Sprite>();
        var result = new Sprite[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            var s = source[i];
            result[i] = new Sprite
            {
                Texture = s.Texture,
                X = s.X,
                Y = s.Y,
                Width = s.Width,
                Height = s.Height,
                Flip = s.Flip,
                ObjectID = s.ObjectID == 0 ? null : s.ObjectID,
                Depth = s.Depth,
                Layer = s.Layer,
                DepthTexture = s.DepthTexture,
                DepthNear = s.DepthNear,
                DepthFar = s.DepthFar,
                Wall = s.Wall is { } w
                    ? new WallSide(w.X, w.Y, w.Level, w.Direction, w.Cut, w.Edge, w.Side)
                    : null,
                Level = s.Level,
                Room = s.Room,
                Junction = s.Junction is { } j
                    ? new WallJunction(j.X, j.Y, j.Level, j.Flags, j.Frame, j.Down)
                    : null,
                ArchitectureDepth = s.ArchitectureDepth is { } d
                    ? new WallDepth(d.X, d.Y, d.Level, d.Configuration)
                    : null,
                Near = ToZoomImage(s.Near),
                Far = ToZoomImage(s.Far),
                HideAtFarZoom = s.HideAtFarZoom,
                Shadow = s.Shadow,
                ShadowBrightness = s.ShadowBrightness,
                FloorShadow = ToZoomImage(s.FloorShadow),
                FloorShadowNear = ToZoomImage(s.FloorShadowNear),
                FloorShadowFar = ToZoomImage(s.FloorShadowFar)
            };
        }
        return result;
    }

    internal static CensorRect[] ToCensorRects(SimsSimulation.SceneExporter.CensorRect[] source)
    {
        if (source.Length == 0)
            return Array.Empty<CensorRect>();
        var result = new CensorRect[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            var r = source[i];
            result[i] = new CensorRect(r.X, r.Y, r.Width, r.Height, r.Depth);
        }
        return result;
    }

    internal static MeshVertex[] ToMeshVertices(SimsSimulation.SceneExporter.Vertex[] source, float shiftX = 0f, float shiftY = 0f)
    {
        if (source.Length == 0)
            return Array.Empty<MeshVertex>();
        var result = new MeshVertex[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            var v = source[i];
            result[i] = new MeshVertex(v.X + shiftX, v.Y + shiftY, v.Z, v.U, v.V, v.Nx, v.Ny, v.Nz);
        }
        return result;
    }

    internal static ZoomImage? ToZoomImage(SimsSimulation.SceneExporter.ZoomImage? z) =>
        z is null ? null : new ZoomImage(z.Texture, z.X, z.Y, z.Width, z.Height, z.DepthTexture);

    public static List<byte[]> ReadPixels(IReadOnlyList<Texture> textures, string rootDirectory)
    {
        string resolvedRoot = Directory.Exists(rootDirectory)
            ? ResolveRealPath(rootDirectory)
            : SimsSimulation.InMemorySessionAssets.Canonicalize(rootDirectory);
        string rootPrefix = resolvedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedRoot
            : resolvedRoot + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        long budget = 512L * 1024 * 1024;
        var result = new List<byte[]>(textures.Count);

        foreach (var texture in textures)
        {
            if (texture.Width <= 0 || texture.Height <= 0 || texture.Width > 4096 || texture.Height > 4096)
                throw new ContentError("Invalid lot texture dimensions");

            _ = ResourceKey(texture.File);
            long expectedSize = (long)texture.Width * texture.Height * (texture.Encoding == "depth8" ? 1 : 4);
            budget -= expectedSize;
            if (budget < 0)
                throw new ContentError("Lot texture memory limit exceeded");

            string combined = Path.Combine(resolvedRoot, texture.File.Replace('\\', Path.DirectorySeparatorChar));
            byte[] data;
            if (SimsSimulation.InMemorySessionAssets.TryReadBytes(combined, out var inMemoryPixels))
            {
                string canonicalFile = SimsSimulation.InMemorySessionAssets.Canonicalize(combined);
                if (!canonicalFile.StartsWith(rootPrefix, pathComparison))
                    throw new ContentError("Lot texture escapes scene directory");
                data = inMemoryPixels;
            }
            else
            {
                string file = ResolveRealPath(combined);
                if (!file.StartsWith(rootPrefix, pathComparison))
                    throw new ContentError("Lot texture escapes scene directory");
                data = File.ReadAllBytes(file);
            }
            result.Add(DecodeTextureBytes(texture, data));
        }

        return result;
    }

    internal static byte[] DecodeTextureBytes(Texture texture, byte[] data)
    {
        long expectedSize = (long)texture.Width * texture.Height * (texture.Encoding == "depth8" ? 1 : 4);
        switch (texture.Encoding ?? "rgba")
        {
            case "rgba":
            case "depth8":
                if (data.Length != expectedSize)
                    throw new ContentError($"Truncated lot texture: {texture.File}");
                return data;
            case "png":
                return new PNGImage(data, texture.Width, texture.Height).Rgba;
            case "bmp":
            {
                var image = new SimsBitmap(data);
                if (image.Width != texture.Width || image.Height != texture.Height)
                    throw new ContentError("BMP dimensions differ from scene metadata");
                return image.Rgba;
            }
            case "tga":
            {
                var image = new SimsTarga(data);
                if (image.Width != texture.Width || image.Height != texture.Height)
                    throw new ContentError("TGA dimensions differ from scene metadata");
                return image.Rgba;
            }
            default:
                throw new ContentError("Unsupported lot texture encoding");
        }
    }

    public void Validate() => Validate(ValidatedState.None);

    public void ValidateWallSurface()
    {
        if (LotWidth is not int lw || LotHeight is not int lh
            || lw is < 1 or > 128 || lh is < 1 or > 128
            || TerrainVertices is not null || Meshes is not null
            || Sprites is null || Sprites.Count == 0
            || !Sprites.All(s => s.ArchitectureDepth is not null && (s.ObjectID ?? 0) == 0 && s.Layer == 1))
        {
            throw new ContentError("Invalid wallpaper surface preview");
        }

        Validate(ValidatedState.None, wallSurface: true);
    }

    internal void Validate(ValidatedState validated, bool wallSurface = false, bool allowSpriteOverhang = false)
    {
        if (Textures is null || Sprites is null || Limitations is null)
            throw new ContentError("Invalid lot scene collections");

        if (Scenery is not null && !validated.Architecture)
        {
            if (Scenery.Count > 16 || Scenery.Any(group => group is null || group.Count == 0 || group.Count > 64))
                throw new ContentError("Invalid scenery prototypes");

            foreach (var group in Scenery)
            {
                foreach (var sprite in group)
                {
                    if (!ValidTextureIndex(sprite.Texture)
                        || !float.IsFinite(sprite.X) || !float.IsFinite(sprite.Y)
                        || !float.IsFinite(sprite.Width) || !float.IsFinite(sprite.Height)
                        || sprite.Width <= 0 || sprite.Height <= 0
                        || (sprite.DepthTexture is int dt && !ValidTextureIndex(dt))
                        || (sprite.DepthNear is float dn && !float.IsFinite(dn))
                        || (sprite.DepthFar is float df && !float.IsFinite(df)))
                    {
                        throw new ContentError("Invalid scenery sprite");
                    }

                    ValidateZoomImageBounds(sprite.Near);
                    ValidateZoomImageBounds(sprite.Far);
                }
            }
        }

        if (CameraOrientation is int cam && cam is < 0 or > 3)
            throw new ContentError("Invalid lot camera orientation");

        if (WallMode is not null && WallMode is not ("up" or "down" or "cutaway"))
            throw new ContentError("Invalid wall mode");

        if (OverheadMap is { } map)
        {
            if (map.SamplesPerTile != 4
                || LotWidth is not int mw || LotHeight is not int mh
                || map.Width != mw * 4 || map.Height != mh * 4
                || map.Width <= 0 || map.Height <= 0 || map.Width > 2048 || map.Height > 2048
                || map.Pixels is null || map.Pixels.Length != map.Width * map.Height * 4)
            {
                throw new ContentError("Invalid overhead architecture map");
            }
        }

        Lighting?.Validate();
        int? roomLimit = Lighting?.Rooms.Count;

        foreach (var sprite in Sprites)
        {
            if (sprite.Room is int room)
            {
                if (room is < 0 or > 65535)
                    throw new ContentError("Invalid material room ID");
                if (roomLimit is int limit && room >= limit)
                    throw new ContentError("Sprite or mesh references a room outside the lighting table");
            }
        }

        if (Meshes is not null)
        {
            foreach (var mesh in Meshes)
            {
                if (mesh.Room is int room)
                {
                    if (room is < 0 or > 65535)
                        throw new ContentError("Invalid material room ID");
                    if (roomLimit is int limit && room >= limit)
                        throw new ContentError("Sprite or mesh references a room outside the lighting table");
                }
            }
        }

        if (SelectedLevel is < 1 or > 5
            || (Stories is int storiesVal && storiesVal is < 1 or > 5)
            || SelectedLevel > (Stories ?? 5))
        {
            throw new ContentError("Invalid selected story");
        }

        if (SoundSources is not null)
        {
            foreach (var source in SoundSources)
            {
                if (source.Level is < 1 or > 5
                    || !float.IsFinite(source.X) || !float.IsFinite(source.Y) || !float.IsFinite(source.Z))
                {
                    throw new ContentError("Invalid sound source");
                }
            }
        }

        if (CensorRects is not null)
        {
            foreach (var censor in CensorRects)
            {
                if (!float.IsFinite(censor.X) || !float.IsFinite(censor.Y)
                    || !float.IsFinite(censor.Width) || !float.IsFinite(censor.Height) || !float.IsFinite(censor.Depth)
                    || censor.Width <= 0 || censor.Height <= 0
                    || censor.Width > 4096 || censor.Height > 4096
                    || censor.Depth < 0 || censor.Depth > 1)
                {
                    throw new ContentError("Invalid censor rect");
                }
            }
        }

        if (SoundSources is not null || OriginX is not null || OriginY is not null)
        {
            if (OriginX is not float ox || OriginY is not float oy || !float.IsFinite(ox) || !float.IsFinite(oy))
                throw new ContentError("Invalid scene origin");
        }

        if (PlacementVertices is not null && !validated.Architecture)
        {
            if (LotWidth is not int lw || LotHeight is not int lh || PlacementVertices.Count != lw * lh * 6)
                throw new ContentError("Invalid construction grid");

            foreach (var vertex in PlacementVertices)
            {
                if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y)
                    || vertex.X < 0 || vertex.Y < 0
                    || vertex.X > Width + 1 || vertex.Y > Height + 1)
                {
                    throw new ContentError("Invalid construction vertex bounds");
                }
            }
        }

        if (!wallSurface && (LotWidth is not null || LotHeight is not null))
        {
            if (LotWidth is not int lw || LotHeight is not int lh
                || lw is < 1 or > 128 || lh is < 1 or > 128
                || TerrainVertices is null || TerrainVertices.Count != lw * lh * 6)
            {
                throw new ContentError("Invalid lot terrain grid");
            }
        }

        if (FloorDiagonals is not null && !validated.Architecture)
        {
            if (LotWidth is not int lw || LotHeight is not int lh
                || FloorDiagonals.Count != lw * lh
                || !FloorDiagonals.All(d => d is 0 or 16 or 32))
            {
                throw new ContentError("Invalid diagonal floor grid");
            }
        }

        long textureBytes = 0;
        for (int index = 0; index < Textures.Count; index++)
        {
            var texture = Textures[index];
            if (index < validated.Textures)
            {
                textureBytes += (long)texture.Width * texture.Height * (texture.Encoding == "depth8" ? 1 : 4);
                continue;
            }

            if (texture.Width <= 0 || texture.Height <= 0 || texture.Width > 4096 || texture.Height > 4096
                || (texture.Encoding ?? "rgba") is not ("rgba" or "bmp" or "tga" or "png" or "depth8"))
            {
                throw new ContentError("Invalid lot texture metadata");
            }

            _ = ResourceKey(texture.File);
            textureBytes += (long)texture.Width * texture.Height * (texture.Encoding == "depth8" ? 1 : 4);
            if (textureBytes > 512L * 1024 * 1024)
                throw new ContentError("Lot texture memory limit exceeded");
        }

        if (SchemaVersion != 1 || !float.IsFinite(Width) || !float.IsFinite(Height)
            || Width <= 0 || Height <= 0 || Width > 65536 || Height > 65536
            || Textures.Count > 10000 || Sprites.Count > 100000)
        {
            throw new ContentError("Invalid lot scene size or version");
        }

        int maxStories = Stories ?? 5;
        for (int i = validated.Sprites; i < Sprites.Count; i++)
        {
            var sprite = Sprites[i];
            int spriteLevel = sprite.Level ?? 1;
            if (spriteLevel < 1 || spriteLevel > maxStories || (ViewLevel is not null && spriteLevel > SelectedLevel))
                throw new ContentError("Sprite exceeds visible story");

            bool withinBounds = allowSpriteOverhang
                ? sprite.X >= -4096 && sprite.Y >= -4096 && sprite.X + sprite.Width <= Width + 4096 && sprite.Y + sprite.Height <= Height + 4096
                : sprite.X >= 0 && sprite.Y >= 0 && sprite.X + sprite.Width <= Width + 1 && sprite.Y + sprite.Height <= Height + 1;

            if (!ValidTextureIndex(sprite.Texture)
                || !float.IsFinite(sprite.X) || !float.IsFinite(sprite.Y)
                || !float.IsFinite(sprite.Width) || !float.IsFinite(sprite.Height)
                || sprite.Width <= 0 || sprite.Height <= 0 || !withinBounds)
            {
                throw new ContentError("Invalid lot sprite bounds");
            }

            if (sprite.Wall is { } wall)
            {
                if ((wall.Cut ?? 0) is < 0 or > 3)
                    throw new ContentError("Invalid wall cut shape");
                if (LotWidth is not int lw || LotHeight is not int lh
                    || wall.X < 0 || wall.X >= lw || wall.Y < 0 || wall.Y >= lh
                    || wall.Level < 1 || wall.Level > maxStories || wall.Level != spriteLevel
                    || wall.Direction is < 0 or > 3 || (sprite.ObjectID ?? 0) != 0 || sprite.Layer != 1)
                {
                    throw new ContentError("Invalid wall picking metadata");
                }
            }

            if (sprite.ArchitectureDepth is { } architecture)
            {
                if (LotWidth is not int lw || LotHeight is not int lh
                    || architecture.X < 0 || architecture.X >= lw || architecture.Y < 0 || architecture.Y >= lh
                    || architecture.Level != spriteLevel || architecture.Configuration is < 4 or > 7
                    || sprite.Junction is not null || (sprite.ObjectID ?? 0) != 0 || sprite.Layer != 1 || sprite.DepthTexture is null
                    || (sprite.Wall is not null && (sprite.Wall.X != architecture.X || sprite.Wall.Y != architecture.Y)))
                {
                    throw new ContentError("Invalid wall depth metadata");
                }
            }

            if (sprite.Junction is { } junction)
            {
                if (LotWidth is not int lw || LotHeight is not int lh
                    || junction.X < 0 || junction.X >= lw || junction.Y < 0 || junction.Y >= lh
                    || junction.Level != spriteLevel || junction.Flags is < 1 or > 255 || junction.Frame is < 0 or > 45
                    || sprite.Wall is not null || (sprite.ObjectID ?? 0) != 0 || sprite.Layer != 1 || sprite.DepthTexture is null)
                {
                    throw new ContentError("Invalid wall junction metadata");
                }
            }

            if (sprite.Depth is float sDepth && !float.IsFinite(sDepth))
                throw new ContentError("Invalid sprite draw depth");

            var color = Textures[sprite.Texture];
            if (color.Encoding == "depth8")
                throw new ContentError("Sprite color references a depth texture");

            if (sprite.DepthTexture is int depthTex)
            {
                if (!ValidTextureIndex(depthTex)
                    || sprite.DepthNear is not float near || sprite.DepthFar is not float far
                    || !float.IsFinite(near) || !float.IsFinite(far)
                    || near < 0 || far > 1 || near > far
                    || Textures[depthTex].Encoding != "depth8"
                    || Textures[depthTex].Width != color.Width || Textures[depthTex].Height != color.Height)
                {
                    throw new ContentError("Invalid sprite depth texture or interval");
                }
            }
            else if (sprite.DepthNear is not null || sprite.DepthFar is not null)
            {
                throw new ContentError("Sprite depth interval has no texture");
            }

            ValidateSpriteZoomImage(sprite.Near, sprite.DepthTexture is not null);
            ValidateSpriteZoomImage(sprite.Far, sprite.DepthTexture is not null);
        }

        if (TerrainVertices is not null && !validated.Architecture)
        {
            if (TerrainVertices.Count > 300000 || TerrainVertices.Count % 3 != 0)
                throw new ContentError("Invalid terrain triangle count");

            foreach (var vertex in TerrainVertices)
            {
                if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y)
                    || vertex.X < 0 || vertex.Y < 0
                    || vertex.X > Width + 1 || vertex.Y > Height + 1)
                {
                    throw new ContentError("Invalid terrain vertex bounds");
                }
            }
        }

        if (TerrainHeights is not null)
        {
            if (LotWidth is not int lw || LotHeight is not int lh || TerrainHeights.Count != lw * lh)
                throw new ContentError("Invalid terrain height grid");
        }

        if (Meshes is not null)
        {
            int meshVertexCount = 0;
            for (int index = 0; index < Meshes.Count; index++)
            {
                var mesh = Meshes[index];
                if (mesh.Vertices is null)
                    throw new ContentError("Invalid avatar mesh vertices");

                if (index < validated.Meshes)
                {
                    meshVertexCount += mesh.Vertices.Count;
                    continue;
                }

                int meshLevel = mesh.Level ?? 1;
                if (meshLevel < 1 || meshLevel > maxStories || (ViewLevel is not null && meshLevel > SelectedLevel))
                    throw new ContentError("Mesh exceeds visible story");

                if ((mesh.Layer is int layerVal && layerVal is < 0 or > 2)
                    || (mesh.Unlit == true && !(mesh.Layer == 0 && mesh.ObjectID == 0)))
                {
                    throw new ContentError("Invalid static mesh material or layer");
                }

                if ((mesh.Layer == 2) != (mesh.Roof is not null))
                    throw new ContentError("Roof layer has no material");

                if (mesh.Roof is { } roof)
                {
                    if (mesh.ObjectID != 0 || mesh.Floor is not null || mesh.Unlit != false
                        || (roof.Part ?? "surface") is not ("surface" or "edge")
                        || !float.IsFinite(roof.Pitch) || roof.Pitch < 0 || roof.Pitch > 2
                        || roof.File is null || roof.File.Length > 1024)
                    {
                        throw new ContentError("Invalid roof material");
                    }
                }

                if (mesh.Floor is { } floor)
                {
                    if (floor.Pattern >= 65534)
                    {
                        if (floor.Water is null)
                            throw new ContentError("Water surface has no topology");
                        floor.Water.Validate(floor.Pattern);
                    }
                    else if (floor.Water is not null)
                    {
                        throw new ContentError("Ordinary floor contains water metadata");
                    }

                    if ((floor.Level ?? 1) != meshLevel
                        || LotWidth is not int lw || LotHeight is not int lh
                        || floor.X < 0 || floor.X >= lw || floor.Y < 0 || floor.Y >= lh
                        || floor.Pattern == 0 || floor.Half is < 0 or > 4 || mesh.Unlit != true
                        || mesh.Vertices.Count != (floor.Half == 0 ? 6 : 3))
                    {
                        throw new ContentError("Invalid floor surface metadata");
                    }
                }

                meshVertexCount += mesh.Vertices.Count;
                if (meshVertexCount > 1_000_000 || mesh.Vertices.Count == 0 || mesh.Vertices.Count % 3 != 0
                    || !ValidTextureIndex(mesh.Texture) || !float.IsFinite(mesh.Depth)
                    || Textures[mesh.Texture].Encoding == "depth8")
                {
                    throw new ContentError("Invalid avatar mesh size or texture");
                }

                ValidateZoomFloorTexture(mesh.Floor is not null, mesh.NearTexture);
                ValidateZoomFloorTexture(mesh.Floor is not null, mesh.FarTexture);

                if (mesh.Tint is { } meshTint)
                {
                    if (meshTint.Count != 4 || meshTint.Any(c => !float.IsFinite(c) || c < 0f || c > 1f))
                        throw new ContentError("Invalid mesh material tint");
                }

                float uvLimit = mesh.Roof is null ? 16f : 4096f;
                foreach (var vertex in mesh.Vertices)
                {
                    bool xyValid = mesh.ObjectID > 0
                        ? vertex.X >= -4096 && vertex.Y >= -4096 && vertex.X <= Width + 4096 && vertex.Y <= Height + 4096
                        : vertex.X >= 0 && vertex.Y >= 0 && vertex.X <= Width + 1 && vertex.Y <= Height + 1;

                    if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y) || !float.IsFinite(vertex.Z)
                        || !float.IsFinite(vertex.U) || !float.IsFinite(vertex.V)
                        || !float.IsFinite(vertex.NX) || !float.IsFinite(vertex.NY) || !float.IsFinite(vertex.NZ)
                        || !xyValid || vertex.Z < 0 || vertex.Z > 1
                        || MathF.Abs(vertex.U) > uvLimit || MathF.Abs(vertex.V) > uvLimit
                        || MathF.Abs(vertex.NX) > 1.01f || MathF.Abs(vertex.NY) > 1.01f || MathF.Abs(vertex.NZ) > 1.01f)
                    {
                        throw new ContentError(
                            $"Invalid avatar mesh vertex (obj={mesh.ObjectID} floor={mesh.Floor is not null} roof={mesh.Roof is not null} v=({vertex.X},{vertex.Y},{vertex.Z}) uv=({vertex.U},{vertex.V}) n=({vertex.NX},{vertex.NY},{vertex.NZ}) size=({Width},{Height}))");
                    }
                }
            }
        }
    }

    private bool ValidTextureIndex(int index) => index >= 0 && index < Textures.Count;

    private void ValidateZoomImageBounds(ZoomImage? zoomed)
    {
        if (zoomed is null)
            return;

        if (!ValidTextureIndex(zoomed.Texture)
            || !float.IsFinite(zoomed.X) || !float.IsFinite(zoomed.Y)
            || !float.IsFinite(zoomed.Width) || !float.IsFinite(zoomed.Height)
            || zoomed.Width <= 0 || zoomed.Height <= 0
            || (zoomed.DepthTexture is int dt && !ValidTextureIndex(dt)))
        {
            throw new ContentError("Invalid scenery sprite");
        }
    }

    private void ValidateSpriteZoomImage(ZoomImage? zoomed, bool hasSpriteDepthTexture)
    {
        if (zoomed is null)
            return;

        if (!ValidTextureIndex(zoomed.Texture) || Textures[zoomed.Texture].Encoding == "depth8"
            || !float.IsFinite(zoomed.X) || !float.IsFinite(zoomed.Y)
            || !float.IsFinite(zoomed.Width) || !float.IsFinite(zoomed.Height)
            || MathF.Abs(zoomed.X) > 1024 || MathF.Abs(zoomed.Y) > 1024
            || zoomed.Width <= 0 || zoomed.Height <= 0
            || zoomed.Width > 2048 || zoomed.Height > 2048
            || (zoomed.DepthTexture is not null) != hasSpriteDepthTexture)
        {
            throw new ContentError("Invalid zoomed sprite");
        }

        if (zoomed.DepthTexture is int zDepth)
        {
            var zoomColor = Textures[zoomed.Texture];
            if (!ValidTextureIndex(zDepth) || Textures[zDepth].Encoding != "depth8"
                || Textures[zDepth].Width != zoomColor.Width || Textures[zDepth].Height != zoomColor.Height)
            {
                throw new ContentError("Invalid zoomed sprite depth texture");
            }
        }
    }

    private void ValidateZoomFloorTexture(bool hasFloor, int? textureIndex)
    {
        if (textureIndex is not int zoomed)
            return;

        if (!hasFloor || !ValidTextureIndex(zoomed) || Textures[zoomed].Encoding == "depth8")
            throw new ContentError("Invalid zoomed floor texture");
    }

    internal static string ResourceKey(string? path)
    {
        if (string.IsNullOrEmpty(path))
            throw new ContentError("Invalid resource path");

        string normalized = path.Replace('\\', '/');
        string[] parts = normalized.Split('/');
        if (parts.Length == 0)
            throw new ContentError("Invalid resource path");

        foreach (string part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.Contains(':') || part.Contains('\0'))
                throw new ContentError($"Invalid resource path: {path}");
        }

        return string.Join('/', parts).ToLowerInvariant();
    }

    internal static string ResolveRealPath(string path)
    {
        string current = Path.GetFullPath(path);
        for (int hop = 0; hop < 16; hop++)
        {
            string trimmed = current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(trimmed))
                return Path.DirectorySeparatorChar.ToString();

            string root = Path.GetPathRoot(trimmed) ?? string.Empty;
            string acc = root;
            bool changed = false;
            string relative = trimmed[root.Length..];

            foreach (string segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                acc = Path.Combine(acc, segment);
                FileSystemInfo? target = null;
                if (File.Exists(acc))
                    target = File.ResolveLinkTarget(acc, returnFinalTarget: true);
                else if (Directory.Exists(acc))
                    target = Directory.ResolveLinkTarget(acc, returnFinalTarget: true);

                if (target is not null)
                {
                    string resolved = Path.GetFullPath(target.FullName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (!string.Equals(resolved, acc, StringComparison.Ordinal))
                    {
                        acc = resolved;
                        changed = true;
                    }
                }
            }

            current = acc;
            if (!changed)
                break;
        }

        return Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
