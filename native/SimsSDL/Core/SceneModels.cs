using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json.Serialization;

namespace SimsSDL.Core;

public enum LotCamera
{
    TopLeft = 0,
    TopRight = 1,
    BottomRight = 2,
    BottomLeft = 3
}

public static class LotCameraExtensions
{
    public static Vector2 CameraPoint(this LotCamera camera, Vector2 point, Vector2 lotSize) => camera switch
    {
        LotCamera.TopLeft => point,
        LotCamera.TopRight => new Vector2(lotSize.Y - point.Y, point.X),
        LotCamera.BottomRight => lotSize - point,
        LotCamera.BottomLeft => new Vector2(point.Y, lotSize.X - point.X),
        _ => point
    };

    public static Vector2 LotPoint(this LotCamera camera, Vector2 point, Vector2 lotSize) => camera switch
    {
        LotCamera.TopLeft => point,
        LotCamera.TopRight => new Vector2(point.Y, lotSize.Y - point.X),
        LotCamera.BottomRight => lotSize - point,
        LotCamera.BottomLeft => new Vector2(lotSize.X - point.Y, point.X),
        _ => point
    };

    public static Vector2 ProjectedPoint(this LotCamera camera, Vector2 point, Vector2 lotSize, float altitude = 0f)
    {
        Vector2 c = camera.CameraPoint(point, lotSize);
        return new Vector2((c.X - c.Y) * 32f + 34f, (c.X + c.Y) * 16f + 158f - altitude);
    }

    public static (int X, int Y) SourceTile(this LotCamera camera, (int X, int Y) tile, (int X, int Y) lotSize) => camera switch
    {
        LotCamera.TopLeft => tile,
        LotCamera.TopRight => (tile.Y, lotSize.Y - 1 - tile.X),
        LotCamera.BottomRight => (lotSize.X - 1 - tile.X, lotSize.Y - 1 - tile.Y),
        LotCamera.BottomLeft => (lotSize.X - 1 - tile.Y, tile.X),
        _ => tile
    };

    public static (int X, int Y) SourceTile(this LotCamera camera, int x, int y, int lotWidth, int lotHeight) =>
        camera.SourceTile((x, y), (lotWidth, lotHeight));
}

public sealed record SceneLighting
{
    public readonly record struct RGB
    {
        [JsonConstructor]
        public RGB() { }

        [SetsRequiredMembers]
        public RGB(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }

        [JsonPropertyName("r")]
        public required byte R { get; init; }

        [JsonPropertyName("g")]
        public required byte G { get; init; }

        [JsonPropertyName("b")]
        public required byte B { get; init; }

        [JsonIgnore]
        public Vector4 Multiplier => new(R / 255f, G / 255f, B / 255f, 1f);
    }

    public sealed record Room
    {
        [JsonConstructor]
        public Room() { }

        [SetsRequiredMembers]
        public Room(int id, bool outside, int ambient, int daylight, byte r, byte g, byte b)
        {
            Id = id;
            Outside = outside;
            Ambient = ambient;
            Daylight = daylight;
            R = r;
            G = g;
            B = b;
        }

        [JsonPropertyName("id")]
        public required int Id { get; init; }

        [JsonPropertyName("outside")]
        public required bool Outside { get; init; }

        [JsonPropertyName("ambient")]
        public required int Ambient { get; init; }

        [JsonPropertyName("daylight")]
        public required int Daylight { get; init; }

        [JsonPropertyName("r")]
        public required byte R { get; init; }

        [JsonPropertyName("g")]
        public required byte G { get; init; }

        [JsonPropertyName("b")]
        public required byte B { get; init; }

        [JsonIgnore]
        public Vector4 Multiplier => new(R / 255f, G / 255f, B / 255f, 1f);
    }

    /// <summary>
    /// A lit lamp: its scene position (medium-zoom pixels), its distance toward the camera in units of camera x + y,
    /// and its lighting contribution.
    /// </summary>
    public readonly record struct Light
    {
        [JsonConstructor]
        public Light() { }

        [SetsRequiredMembers]
        public Light(float x, float y, float toward, int strength, int level)
        {
            X = x;
            Y = y;
            Toward = toward;
            Strength = strength;
            Level = level;
        }

        [JsonPropertyName("x")]
        public required float X { get; init; }

        [JsonPropertyName("y")]
        public required float Y { get; init; }

        [JsonPropertyName("toward")]
        public required float Toward { get; init; }

        [JsonPropertyName("strength")]
        public required int Strength { get; init; }

        [JsonPropertyName("level")]
        public required int Level { get; init; }
    }

    /// <summary>
    /// A tile holding part of a window, in lot tiles: a window has a part on each side of its wall.
    /// </summary>
    public readonly record struct Window
    {
        [JsonConstructor]
        public Window() { }

        [SetsRequiredMembers]
        public Window(int x, int y, int level)
        {
            X = x;
            Y = y;
            Level = level;
        }

        [JsonPropertyName("x")]
        public required int X { get; init; }

        [JsonPropertyName("y")]
        public required int Y { get; init; }

        [JsonPropertyName("level")]
        public required int Level { get; init; }
    }

    [JsonConstructor]
    public SceneLighting() { }

    [SetsRequiredMembers]
    public SceneLighting(
        RGB outside,
        IReadOnlyList<Room> rooms,
        int? minutes = null,
        int? phase = null,
        IReadOnlyList<Light>? lights = null,
        IReadOnlyList<Window>? windows = null,
        RGB? backdrop = null)
    {
        Outside = outside;
        Rooms = rooms;
        Minutes = minutes;
        Phase = phase;
        Lights = lights;
        Windows = windows;
        Backdrop = backdrop;
    }

    [JsonPropertyName("outside")]
    public required RGB Outside { get; init; }

    [JsonPropertyName("rooms")]
    public required IReadOnlyList<Room> Rooms { get; init; }

    /// <summary>
    /// The game clock in minutes after midnight and the time of day (0 day, 1 dawn, 2 night, 3 dusk), when exported.
    /// </summary>
    [JsonPropertyName("minutes")]
    public int? Minutes { get; init; }

    [JsonPropertyName("phase")]
    public int? Phase { get; init; }

    [JsonPropertyName("lights")]
    public IReadOnlyList<Light>? Lights { get; init; }

    [JsonPropertyName("windows")]
    public IReadOnlyList<Window>? Windows { get; init; }

    /// <summary>
    /// The colour Sims.exe fills the screen with before drawing the lot (FUN_004aabcd), by time of day.
    /// </summary>
    [JsonPropertyName("backdrop")]
    public RGB? Backdrop { get; init; }

    public static SceneLighting FromJson(string json) =>
        JsonSerializer.Deserialize<SceneLighting>(json) ?? throw new ContentError("Scene lighting JSON decoded to null");

    public void Validate()
    {
        if (Rooms is null || Rooms.Count == 0 || Rooms.Count > 65535)
            throw new ContentError("Invalid room lighting table");

        for (int i = 0; i < Rooms.Count; i++)
        {
            var room = Rooms[i];
            if (room is null || room.Id != i || room.Ambient is < 0 or > 100 || room.Daylight is < 0 or > 100)
                throw new ContentError("Invalid room lighting table");
        }
    }

    public Vector4 Multiplier(int? room)
    {
        if (room is not int index || index == 65535 || index < 0 || index >= Rooms.Count)
            return Vector4.One;
        return Rooms[index].Multiplier;
    }

    internal void ValidateRoom(int? room)
    {
        // Sims.exe's room fill (FUN_00479f0c) uses 0xFF for tiles outside every room; the native scene exporter
        // preserves that value on outdoor sprites. 0xFFFF is also retained as the scene format's no-room sentinel.
        if (room is int index && index != 255 && index != 65535 && (index < 0 || index >= Rooms.Count))
            throw new ContentError("Material references missing room lighting");
    }
}

public sealed record SceneAnimation
{
    public sealed record Mesh
    {
        [JsonConstructor]
        public Mesh() { }

        [JsonPropertyName("texture")]
        public required int Texture { get; init; }

        [JsonPropertyName("objectID")]
        public required int ObjectID { get; init; }

        [JsonPropertyName("room")]
        public int? Room { get; init; }

        [JsonPropertyName("level")]
        public int? Level { get; init; }

        [JsonPropertyName("floorShadow")]
        public LotScene.ZoomImage? FloorShadow { get; init; }

        [JsonPropertyName("floorShadowNear")]
        public LotScene.ZoomImage? FloorShadowNear { get; init; }

        [JsonPropertyName("floorShadowFar")]
        public LotScene.ZoomImage? FloorShadowFar { get; init; }

        [JsonPropertyName("depth")]
        public required float Depth { get; init; }

        [JsonPropertyName("vertices")]
        public required byte[] Vertices { get; init; }

        internal LotScene.Mesh Unpack()
        {
            if (Vertices is null || Vertices.Length == 0 || Vertices.Length % 96 != 0 || Vertices.Length > 32_000_000)
                throw new ContentError("Invalid packed animation triangle count");

            int vertexCount = Vertices.Length / 32;
            var unpacked = new LotScene.MeshVertex[vertexCount];
            ReadOnlySpan<byte> bytes = Vertices;

            for (int i = 0; i < vertexCount; i++)
            {
                int offset = i * 32;
                unpacked[i] = new LotScene.MeshVertex(
                    BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset + 4, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset + 8, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset + 12, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset + 16, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset + 20, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset + 24, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset + 28, 4)));
            }

            return new LotScene.Mesh
            {
                Room = Room,
                FloorShadow = FloorShadow,
                FloorShadowNear = FloorShadowNear,
                FloorShadowFar = FloorShadowFar,
                Level = Level,
                Texture = Texture,
                ObjectID = ObjectID,
                Depth = Depth,
                Vertices = unpacked
            };
        }
    }

    [JsonConstructor]
    public SceneAnimation() { }

    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("lighting")]
    public SceneLighting? Lighting { get; init; }

    [JsonPropertyName("baseRevision")]
    public required long BaseRevision { get; init; }

    [JsonPropertyName("sequence")]
    public required long Sequence { get; init; }

    [JsonPropertyName("updates")]
    public required long Updates { get; init; }

    [JsonPropertyName("textureStart")]
    public required int TextureStart { get; init; }

    [JsonPropertyName("textures")]
    public required IReadOnlyList<LotScene.Texture> Textures { get; init; }

    [JsonPropertyName("sprites")]
    public required IReadOnlyList<LotScene.Sprite> Sprites { get; init; }

    [JsonPropertyName("meshes")]
    public required IReadOnlyList<Mesh> Meshes { get; init; }

    [JsonPropertyName("censorRects")]
    public IReadOnlyList<LotScene.CensorRect>? CensorRects { get; init; }

    public static SceneAnimation FromJson(string json) =>
        JsonSerializer.Deserialize<SceneAnimation>(json) ?? throw new ContentError("Scene animation JSON decoded to null");

    public static SceneAnimation FromJson(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<SceneAnimation>(utf8Json) ?? throw new ContentError("Scene animation JSON decoded to null");

    public LotScene Applying(LotScene scene, long revision, long after)
    {
        var retainedSprites = scene.Sprites.Where(s => (s.ObjectID ?? 0) == 0).ToList();
        var retainedMeshes = (scene.Meshes ?? []).Where(m => m.ObjectID == 0).ToList();
        return Applying(scene, retainedSprites, retainedMeshes, revision, after);
    }

    public LotScene Applying(
        LotScene scene,
        IReadOnlyList<LotScene.Sprite> retainedSprites,
        IReadOnlyList<LotScene.Mesh> retainedMeshes,
        long revision,
        long after)
    {
        if (SchemaVersion != 1 || BaseRevision != revision || Sequence <= after || Updates < 0
            || Textures is null || Sprites is null || Meshes is null
            || TextureStart != scene.Textures.Count || Textures.Count + TextureStart > 10000
            || Sprites.Count > 100000 || Meshes.Count > 10000
            || !Sprites.All(s => (s.ObjectID ?? 0) > 0)
            || !Meshes.All(m => m.ObjectID > 0))
        {
            throw new ContentError("Animation does not match the current scene");
        }

        long vertexBytes = 0;
        foreach (var mesh in Meshes)
        {
            if (mesh.Vertices is null)
                throw new ContentError("Invalid packed animation triangle count");
            vertexBytes += mesh.Vertices.Length;
            if (vertexBytes > 32_000_000)
                throw new ContentError("Animation vertex memory limit exceeded");
        }

        var updatedSprites = new List<LotScene.Sprite>(retainedSprites.Count + Sprites.Count);
        updatedSprites.AddRange(retainedSprites);
        updatedSprites.AddRange(Sprites);

        var updatedMeshes = new List<LotScene.Mesh>(retainedMeshes.Count + Meshes.Count);
        updatedMeshes.AddRange(retainedMeshes);
        foreach (var mesh in Meshes)
            updatedMeshes.Add(mesh.Unpack());

        IReadOnlyList<LotScene.Texture> updatedTextures;
        if (Textures.Count == 0)
        {
            updatedTextures = scene.Textures;
        }
        else
        {
            var combinedTextures = new List<LotScene.Texture>(scene.Textures.Count + Textures.Count);
            combinedTextures.AddRange(scene.Textures);
            combinedTextures.AddRange(Textures);
            updatedTextures = combinedTextures;
        }

        var result = scene with
        {
            CensorRects = CensorRects ?? scene.CensorRects,
            Lighting = Lighting ?? scene.Lighting,
            Textures = updatedTextures,
            Sprites = updatedSprites,
            Meshes = updatedMeshes
        };

        // The snapshot was validated when it loaded; its static sprites, meshes and textures are kept as they were.
        // Animated object sprites may extend past the static scene rectangle as their frame changes. Sims.exe clips
        // each SPR2 draw to the active viewport (FUN_004c828c -> FUN_004c8728), so the GPU viewport should clip these
        // too instead of rejecting the entire avatar/object frame.
        result.Validate(
            new LotScene.ValidatedState(scene.Textures.Count, retainedSprites.Count, retainedMeshes.Count, Architecture: true),
            allowSpriteOverhang: true);
        return result;
    }
}

public sealed record AudioEvent
{
    [JsonConverter(typeof(StrictCamelCaseEnumConverter<AudioCommand>))]
    public enum AudioCommand
    {
        Play,
        Update,
        Pause,
        Resume,
        Stop,
        Reset
    }

    [JsonConstructor]
    public AudioEvent() { }

    [JsonPropertyName("command")]
    public required AudioCommand Command { get; init; }

    [JsonPropertyName("voice")]
    public long? Voice { get; init; }

    [JsonPropertyName("file")]
    public string? File { get; init; }

    [JsonPropertyName("duration")]
    public float? Duration { get; init; }

    [JsonPropertyName("group")]
    public string? Group { get; init; }

    [JsonPropertyName("volume")]
    public float? Volume { get; init; }

    [JsonPropertyName("pan")]
    public float? Pan { get; init; }

    [JsonPropertyName("loop")]
    public bool? Loop { get; init; }

    [JsonPropertyName("ownerID")]
    public int? OwnerID { get; init; }

    [JsonPropertyName("spatial")]
    public bool? Spatial { get; init; }

    [JsonPropertyName("zoom")]
    public bool? Zoom { get; init; }

    public static AudioEvent FromJson(string json) =>
        JsonSerializer.Deserialize<AudioEvent>(json) ?? throw new ContentError("Audio event JSON decoded to null");

    public void Validate()
    {
        if (Command == AudioCommand.Reset)
            return;

        if (Voice is not long voice || voice <= 0)
            throw new ContentError("Invalid audio voice");

        if (Command is AudioCommand.Play or AudioCommand.Update)
        {
            if (File is null || Duration is not float duration || Group is null
                || Volume is not float volume || Pan is not float pan || Loop is null
                || !float.IsFinite(duration) || duration <= 0 || duration > 3600
                || !float.IsFinite(volume) || volume < 0 || volume > 1
                || !float.IsFinite(pan) || pan < -1 || pan > 1
                || Group is not ("FX" or "VOX" or "MUSIC" or "AMBIENCE")
                || (OwnerID ?? -1) < -32768 || (OwnerID ?? -1) > 32767)
            {
                throw new ContentError("Invalid audio playback parameters");
            }

            string key = LotScene.ResourceKey(File);
            int slash = key.LastIndexOf('/');
            string fileName = slash >= 0 ? key[(slash + 1)..] : key;
            int dot = fileName.LastIndexOf('.');
            string extension = dot > 0 ? fileName[(dot + 1)..] : string.Empty;

            if (!key.StartsWith("audio/", StringComparison.Ordinal)
                || extension is not ("wav" or "mp3")
                || Encoding.UTF8.GetByteCount(key) > 256)
            {
                throw new ContentError("Invalid audio resource path");
            }
        }
    }
}

/// <summary>
/// Positional sound maths shared by the clients: where a sound's owner sits in the view, how much a floor other than
/// the viewed one takes off it, and the pan and distance falloff from the owner's point on screen.
/// </summary>
public static class PositionalSound
{
    /// <summary>
    /// The gain for an owner on <paramref name="sourceLevel"/> while the view shows <paramref name="viewedLevel"/>:
    /// quarter attenuation from floors above the view and half from floors below.
    /// </summary>
    public static float FloorGain(int sourceLevel, int viewedLevel)
    {
        if (sourceLevel > viewedLevel)
            return 0.25f;
        return sourceLevel == viewedLevel ? 1f : 0.5f;
    }

    /// <summary>
    /// A sound owner's point in scene units (the frame of the sprites), projected with the scene's camera from the
    /// host's tile and altitude. Null when the scene carries no origin or lot size.
    /// </summary>
    public static Vector2? ScenePoint(LotScene.SoundSource source, LotScene scene)
    {
        if (scene.LotWidth is not int lotWidth || scene.LotHeight is not int lotHeight
            || scene.OriginX is not float originX || scene.OriginY is not float originY)
        {
            return null;
        }

        Vector2 projected = scene.Camera.ProjectedPoint(
            new Vector2(source.X, source.Y),
            new Vector2(lotWidth, lotHeight),
            source.Z);
        return projected - new Vector2(originX, originY);
    }

    /// <summary>
    /// The pan and falloff for a source at <paramref name="screen"/> (view pixels) in a view of <paramref name="size"/>.
    /// </summary>
    public static (float Pan, float Gain) Mix(Vector2 screen, Vector2 size, bool panned)
    {
        float width = MathF.Max(1f, size.X);
        float height = MathF.Max(1f, size.Y);
        Vector2 delta = (screen - size / 2f) / width;
        float pan = Math.Clamp(delta.X * 2f, -1f, 1f);
        if (!panned)
            return (pan, 1f);

        float marginX = width * 0.20f;
        float distX = screen.X < marginX
            ? marginX - screen.X
            : screen.X > width - marginX
                ? screen.X - (width - marginX)
                : 0f;

        float marginTopY = height * 0.30f;
        float distY = screen.Y > height
            ? screen.Y - height
            : screen.Y < marginTopY
                ? marginTopY - screen.Y
                : 0f;

        float borderDist = MathF.Max(distX, distY);
        float innerAttenuation = MathF.Min(0.15f, MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y) * 0.20f);
        if (borderDist <= 0f)
            return (pan, 1f - innerAttenuation);
        if (borderDist >= 600f)
            return (pan, 0f);

        float borderFactor = (600f - borderDist) / 600f;
        return (pan, MathF.Max(0f, (1f - innerAttenuation) * borderFactor));
    }
}

internal sealed class StrictCamelCaseEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    private static readonly Dictionary<string, TEnum> FromJsonMap = CreateFromMap();
    private static readonly Dictionary<TEnum, string> ToJsonMap = CreateToMap();

    private static Dictionary<string, TEnum> CreateFromMap()
    {
        var map = new Dictionary<string, TEnum>(StringComparer.Ordinal);
        foreach (TEnum value in Enum.GetValues<TEnum>())
        {
            string key = JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
            map[key] = value;
        }
        return map;
    }

    private static Dictionary<TEnum, string> CreateToMap()
    {
        var map = new Dictionary<TEnum, string>();
        foreach (TEnum value in Enum.GetValues<TEnum>())
            map[value] = JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
        return map;
    }

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Expected string token for {typeof(TEnum).Name}");

        string? raw = reader.GetString();
        if (raw is null || !FromJsonMap.TryGetValue(raw, out TEnum value))
            throw new JsonException($"Invalid {typeof(TEnum).Name} value '{raw}'");

        return value;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (!ToJsonMap.TryGetValue(value, out string? name))
            name = JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
        writer.WriteStringValue(name);
    }
}
