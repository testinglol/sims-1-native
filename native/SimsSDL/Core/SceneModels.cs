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

    /// <summary>Represents a light source, storing its position in medium-zoom pixels, depth in camera
    /// projection units, and lighting contribution.</summary>
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

    /// <summary>Represents a window tile in lot coordinates, accounting for wall components on both sides
    /// of the wall.</summary>
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

    [JsonPropertyName("minutes")]
    public int? Minutes { get; init; }

    [JsonPropertyName("phase")]
    public int? Phase { get; init; }

    [JsonPropertyName("lights")]
    public IReadOnlyList<Light>? Lights { get; init; }

    [JsonPropertyName("windows")]
    public IReadOnlyList<Window>? Windows { get; init; }

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

    internal const int PoolAttachmentRoom = 65534;

    public Vector4 Multiplier(int? room)
    {
        if (room is not int index || index == 65535)
            return Vector4.One;
        if (index == PoolAttachmentRoom)
            return (Phase ?? 0) == 0 ? Vector4.One : new Vector4(204f / 255f, 204f / 255f, 204f / 255f, 1f);
        if (index < 0 || index >= Rooms.Count)
            return Vector4.One;
        return Rooms[index].Multiplier;
    }

    internal void ValidateRoom(int? room)
    {
        // Outdoor tiles use index 255, no-room sentinels use 65535, and pools use 65534.
        if (room is int index && index != 255 && index != PoolAttachmentRoom && index != 65535 &&
            (index < 0 || index >= Rooms.Count))
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

        [JsonPropertyName("tint")]
        public IReadOnlyList<float>? Tint { get; init; }

        [JsonIgnore]
        internal LotScene.MeshVertex[]? DirectVertices { get; init; }

        internal LotScene.Mesh Unpack()
        {
            LotScene.MeshVertex[] unpacked;
            if (DirectVertices is { } directVertices)
            {
                if (directVertices.Length == 0 || directVertices.Length % 3 != 0)
                    throw new ContentError("Invalid direct animation triangle count");
                unpacked = directVertices;
            }
            else
            {
                if (Vertices is null || Vertices.Length == 0 || Vertices.Length % 96 != 0 || Vertices.Length > 32_000_000)
                    throw new ContentError("Invalid packed animation triangle count");

                int vertexCount = Vertices.Length / 32;
                unpacked = new LotScene.MeshVertex[vertexCount];
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
                Vertices = unpacked,
                Tint = Tint
            };
        }
    }

    public sealed record OverheadFeedbackDescriptor(
        [property: JsonPropertyName("objectID")] short ObjectID,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("backgroundRgb")] uint BackgroundRgb,
        [property: JsonPropertyName("foregroundRgb")] uint ForegroundRgb,
        [property: JsonPropertyName("delta")] int Delta,
        [property: JsonPropertyName("x")] float X,
        [property: JsonPropertyName("y")] float Y,
        [property: JsonPropertyName("level")] int Level,
        [property: JsonPropertyName("stackIndex")] int StackIndex,
        [property: JsonPropertyName("remainingTicks")] int RemainingTicks,
        [property: JsonPropertyName("hasSprite")] bool HasSprite);

    public sealed record SkillMeterDescriptor(
        [property: JsonPropertyName("objectID")] short ObjectID,
        [property: JsonPropertyName("spriteId")] int SpriteId,
        [property: JsonPropertyName("progressTenths")] int ProgressTenths,
        [property: JsonPropertyName("x")] float X,
        [property: JsonPropertyName("y")] float Y,
        [property: JsonPropertyName("level")] int Level);

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

    [JsonPropertyName("overheadFeedback")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<OverheadFeedbackDescriptor>? OverheadFeedback { get; init; }

    [JsonPropertyName("skillMeters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SkillMeterDescriptor>? SkillMeters { get; init; }

    [JsonIgnore]
    internal bool IsTrustedInProcess { get; init; }

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
        bool trusted = IsTrustedInProcess;
        if (SchemaVersion != 1 || BaseRevision != revision || Sequence <= after || Updates < 0
            || Textures is null || Sprites is null || Meshes is null
            || TextureStart < 0 || TextureStart > scene.Textures.Count
            || (!trusted && (Textures.Count + TextureStart > 10000
                || Sprites.Count > 100000 || Meshes.Count > 10000
                || !Sprites.All(s => (s.ObjectID ?? 0) > 0)
                || !Meshes.All(m => m.ObjectID > 0))))
        {
            throw new ContentError("Animation does not match the current scene");
        }

        if (!trusted)
        {
            long vertexBytes = 0;
            foreach (var mesh in Meshes)
            {
                if (mesh.Vertices is null && mesh.DirectVertices is null)
                    throw new ContentError("Invalid packed animation triangle count");
                vertexBytes += mesh.DirectVertices is { } direct ? direct.Length * 32L : mesh.Vertices!.Length;
                if (vertexBytes > 32_000_000)
                    throw new ContentError("Animation vertex memory limit exceeded");
            }
        }

        IReadOnlyList<LotScene.Sprite> updatedSprites = retainedSprites.Count == 0
            ? Sprites
            : Sprites.Count == 0
                ? retainedSprites
                : new ConcatReadOnlyList<LotScene.Sprite>(retainedSprites, Sprites);

        IReadOnlyList<LotScene.Mesh> updatedMeshes;
        if (Meshes.Count == 0)
        {
            updatedMeshes = retainedMeshes;
        }
        else
        {
            var dynamicMeshes = new LotScene.Mesh[Meshes.Count];
            for (int i = 0; i < Meshes.Count; i++)
                dynamicMeshes[i] = Meshes[i].Unpack();
            updatedMeshes = retainedMeshes.Count == 0
                ? dynamicMeshes
                : new ConcatReadOnlyList<LotScene.Mesh>(retainedMeshes, dynamicMeshes);
        }

        IReadOnlyList<LotScene.Texture> updatedTextures;
        if (Textures.Count == 0 && TextureStart == scene.Textures.Count)
        {
            updatedTextures = scene.Textures;
        }
        else
        {
            var combinedTextures = new List<LotScene.Texture>(TextureStart + Textures.Count);
            for (int i = 0; i < TextureStart; i++)
                combinedTextures.Add(scene.Textures[i]);
            combinedTextures.AddRange(Textures);
            updatedTextures = combinedTextures;
        }

        var result = scene with
        {
            CensorRects = CensorRects ?? scene.CensorRects,
            OverheadFeedback = OverheadFeedback,
            SkillMeters = SkillMeters,
            Lighting = Lighting ?? scene.Lighting,
            Textures = updatedTextures,
            Sprites = updatedSprites,
            Meshes = updatedMeshes
        };

        if (trusted)
            return result;

        // Viewport clipping handles animated sprites extending past static scene bounds.
        result.Validate(
            new LotScene.ValidatedState(TextureStart, retainedSprites.Count, retainedMeshes.Count, Architecture: true),
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

    [JsonPropertyName("pitchScale")]
    public float? PitchScale { get; init; }

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
                || (PitchScale is float pitchScale && (!float.IsFinite(pitchScale) || pitchScale <= 0f || pitchScale > 4f))
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

/// <summary>Calculates positional audio panning, distance falloff, and vertical floor obstruction.</summary>
public static class PositionalSound
{
    /// <summary>Calculates audio attenuation across stories, applying one-quarter gain for sources above
    /// the current view and one-half gain for sources below.</summary>
    public static float FloorGain(int sourceLevel, int viewedLevel)
    {
        if (sourceLevel > viewedLevel)
            return 0.25f;
        return sourceLevel == viewedLevel ? 1f : 0.5f;
    }

    /// <summary>Projects an audio source position from tile coordinates and altitude into medium-zoom scene
    /// units using the scene camera.</summary>
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

internal sealed class ConcatReadOnlyList<T>(IReadOnlyList<T> first, IReadOnlyList<T> second) : IReadOnlyList<T>
{
    public int Count => first.Count + second.Count;

    public T this[int index] => index < first.Count ? first[index] : second[index - first.Count];

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < first.Count; i++)
            yield return first[i];
        for (int i = 0; i < second.Count; i++)
            yield return second[i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
