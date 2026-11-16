using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;

namespace SimsSimulation;

/// <summary>Builds the lot scene from world state and ARRY layers, exporting terrain triangles, floor
/// diamonds, object sprites, and avatar meshes projected into camera space.</summary>
internal static class SceneExporter
{
    internal const int Size = 64;

    private static readonly ConcurrentDictionary<string, bool> speechBuffersCache = new(StringComparer.Ordinal);

    /// <summary>Loads the global speech balloon background bitmaps (resources 0x2328 and 0x2329) required
    /// for compositing medium and large speech icons.</summary>
    private static bool HasProductSpeechBuffers(string gameRoot) =>
        speechBuffersCache.GetOrAdd(gameRoot, LoadProductSpeechBuffers);

    private static bool LoadProductSpeechBuffers(string gameRoot)
    {
        var path = Path.Combine(gameRoot, "UIGraphics", "UIGraphics.far");
        if (!File.Exists(path)) return false;
        try
        {
            var archive = FarArchive.Open(path);
            var medium = archive.Find("Other/SpeechMedium.bmp");
            var large = archive.Find("Other/SpeechLarge.bmp");
            if (medium is null || large is null) return false;
            _ = BitmapCodec.Read(archive.Read(medium).ToArray());
            _ = BitmapCodec.Read(archive.Read(large).ToArray());
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    internal static object? PlacementGhost(string scenePath, short objectId, string? referenceScenePath = null)
        => PlacementGhost(scenePath, new HashSet<short> { objectId }, referenceScenePath);

    internal static object? PlacementGhost(string scenePath, IReadOnlySet<short> objectIds, string? referenceScenePath = null)
    {
        using var document = JsonDocument.Parse(SimsSimulation.InMemorySessionAssets.ReadAllBytes(scenePath));
        var source = document.RootElement;
        var sourceSprites = source.GetProperty("sprites").EnumerateArray()
            .Where(sprite => sprite.TryGetProperty("objectID", out var id) && objectIds.Contains(checked((short)id.GetInt32())))
            .OrderBy(sprite => sprite.TryGetProperty("depth", out var depth) ? depth.GetSingle() : 0)
            .ToArray();
        if (sourceSprites.Length == 0) return null;

        var requiredTextures = new SortedSet<int>();
        foreach (var sprite in sourceSprites)
        {
            AddTexture(sprite, "texture");
            AddTexture(sprite, "depthTexture");
            AddZoom(sprite, "near");
            AddZoom(sprite, "far");
        }
        var textureMap = requiredTextures.Select((texture, index) => (texture, index))
            .ToDictionary(pair => pair.texture, pair => pair.index);
        var sourceTextures = source.GetProperty("textures").EnumerateArray().ToArray();
        var textures = new JsonArray(requiredTextures.Select(index => JsonNode.Parse(sourceTextures[index].GetRawText())!).ToArray());
        var minX = sourceSprites.Min(sprite => sprite.GetProperty("x").GetSingle());
        var minY = sourceSprites.Min(sprite => sprite.GetProperty("y").GetSingle());
        var maxX = sourceSprites.Max(sprite => sprite.GetProperty("x").GetSingle() + sprite.GetProperty("width").GetSingle());
        var maxY = sourceSprites.Max(sprite => sprite.GetProperty("y").GetSingle() + sprite.GetProperty("height").GetSingle());
        var alignX = 0f;
        var alignY = 0f;
        if (referenceScenePath is not null && SimsSimulation.InMemorySessionAssets.FileExists(referenceScenePath))
        {
            using var reference = JsonDocument.Parse(SimsSimulation.InMemorySessionAssets.ReadAllBytes(referenceScenePath));
            if (source.GetProperty("terrainVertices").GetArrayLength() > 0 &&
                reference.RootElement.GetProperty("terrainVertices").GetArrayLength() > 0)
            {
                var currentOrigin = source.GetProperty("terrainVertices")[0];
                var referenceOrigin = reference.RootElement.GetProperty("terrainVertices")[0];
                alignX = referenceOrigin.GetProperty("x").GetSingle() - currentOrigin.GetProperty("x").GetSingle();
                alignY = referenceOrigin.GetProperty("y").GetSingle() - currentOrigin.GetProperty("y").GetSingle();
            }
        }
        var sprites = new JsonArray();
        foreach (var sourceSprite in sourceSprites)
        {
            var sprite = JsonNode.Parse(sourceSprite.GetRawText())!.AsObject();
            RemapTexture(sprite, "texture");
            RemapTexture(sprite, "depthTexture");
            RemapZoom(sprite, "near");
            RemapZoom(sprite, "far");
            sprite["x"] = sourceSprite.GetProperty("x").GetSingle() - minX;
            sprite["y"] = sourceSprite.GetProperty("y").GetSingle() - minY;
            sprites.Add(sprite);
        }
        var ghostScene = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["house"] = 0,
            ["width"] = maxX - minX,
            ["height"] = maxY - minY,
            ["cameraOrientation"] = source.TryGetProperty("cameraOrientation", out var orientation) ? orientation.GetInt32() : 0,
            ["textures"] = textures,
            ["sprites"] = sprites,
            ["limitations"] = new JsonArray(),
        };
        return new JsonObject { ["x"] = minX + alignX, ["y"] = minY + alignY, ["scene"] = ghostScene };

        void AddTexture(JsonElement sprite, string field)
        {
            if (sprite.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number)
                requiredTextures.Add(value.GetInt32());
        }
        void AddZoom(JsonElement sprite, string field)
        {
            if (!sprite.TryGetProperty(field, out var zoom) || zoom.ValueKind != JsonValueKind.Object) return;
            AddTexture(zoom, "texture");
            AddTexture(zoom, "depthTexture");
        }
        void RemapTexture(JsonObject sprite, string field)
        {
            if (sprite[field] is JsonValue value && value.TryGetValue<int>(out var index)) sprite[field] = textureMap[index];
        }
        void RemapZoom(JsonObject sprite, string field)
        {
            if (sprite[field] is not JsonObject zoom) return;
            RemapTexture(zoom, "texture");
            RemapTexture(zoom, "depthTexture");
        }
    }

    internal static object? WallPaintGhost(
        string scenePath,
        IReadOnlySet<(int X, int Y, int Level, int Direction)> changedWalls,
        string? referenceScenePath = null)
    {
        if (changedWalls.Count == 0) return null;
        using var document = JsonDocument.Parse(SimsSimulation.InMemorySessionAssets.ReadAllBytes(scenePath));
        var source = document.RootElement;
        var sourceSprites = source.GetProperty("sprites").EnumerateArray()
            .Where(sprite =>
                sprite.TryGetProperty("wall", out var wall) &&
                wall.ValueKind == JsonValueKind.Object &&
                changedWalls.Contains((
                    wall.GetProperty("x").GetInt32(),
                    wall.GetProperty("y").GetInt32(),
                    wall.GetProperty("level").GetInt32(),
                    wall.GetProperty("direction").GetInt32())))
            .ToArray();
        if (sourceSprites.Length == 0) return null;

        var requiredTextures = new SortedSet<int>();
        foreach (var sprite in sourceSprites)
        {
            AddTexture(sprite, "texture");
            AddTexture(sprite, "depthTexture");
            AddZoom(sprite, "near");
            AddZoom(sprite, "far");
        }
        var textureMap = requiredTextures.Select((texture, index) => (texture, index))
            .ToDictionary(pair => pair.texture, pair => pair.index);
        var sourceTextures = source.GetProperty("textures").EnumerateArray().ToArray();
        var textures = new JsonArray(requiredTextures.Select(index => JsonNode.Parse(sourceTextures[index].GetRawText())!).ToArray());
        var minX = sourceSprites.Min(sprite => sprite.GetProperty("x").GetSingle());
        var minY = sourceSprites.Min(sprite => sprite.GetProperty("y").GetSingle());
        var maxX = sourceSprites.Max(sprite => sprite.GetProperty("x").GetSingle() + sprite.GetProperty("width").GetSingle());
        var maxY = sourceSprites.Max(sprite => sprite.GetProperty("y").GetSingle() + sprite.GetProperty("height").GetSingle());
        var alignX = 0f;
        var alignY = 0f;
        if (referenceScenePath is not null && SimsSimulation.InMemorySessionAssets.FileExists(referenceScenePath))
        {
            using var reference = JsonDocument.Parse(SimsSimulation.InMemorySessionAssets.ReadAllBytes(referenceScenePath));
            if (source.GetProperty("terrainVertices").GetArrayLength() > 0 &&
                reference.RootElement.GetProperty("terrainVertices").GetArrayLength() > 0)
            {
                var currentOrigin = source.GetProperty("terrainVertices")[0];
                var referenceOrigin = reference.RootElement.GetProperty("terrainVertices")[0];
                alignX = referenceOrigin.GetProperty("x").GetSingle() - currentOrigin.GetProperty("x").GetSingle();
                alignY = referenceOrigin.GetProperty("y").GetSingle() - currentOrigin.GetProperty("y").GetSingle();
            }
        }
        var sprites = new JsonArray();
        foreach (var sourceSprite in sourceSprites)
        {
            var sprite = JsonNode.Parse(sourceSprite.GetRawText())!.AsObject();
            RemapTexture(sprite, "texture");
            RemapTexture(sprite, "depthTexture");
            RemapZoom(sprite, "near");
            RemapZoom(sprite, "far");
            sprite["x"] = sourceSprite.GetProperty("x").GetSingle() - minX;
            sprite["y"] = sourceSprite.GetProperty("y").GetSingle() - minY;
            sprites.Add(sprite);
        }
        var ghostScene = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["house"] = 0,
            ["width"] = maxX - minX,
            ["height"] = maxY - minY,
            ["lotWidth"] = source.TryGetProperty("lotWidth", out var lw) ? lw.GetInt32() : Size,
            ["lotHeight"] = source.TryGetProperty("lotHeight", out var lh) ? lh.GetInt32() : Size,
            ["viewLevel"] = source.TryGetProperty("viewLevel", out var vl) ? vl.GetInt32() : 1,
            ["stories"] = source.TryGetProperty("stories", out var st) ? st.GetInt32() : 2,
            ["cameraOrientation"] = source.TryGetProperty("cameraOrientation", out var orientation) ? orientation.GetInt32() : 0,
            ["textures"] = textures,
            ["sprites"] = sprites,
            ["limitations"] = new JsonArray(),
        };
        return new JsonObject { ["x"] = minX + alignX, ["y"] = minY + alignY, ["scene"] = ghostScene };

        void AddTexture(JsonElement sprite, string field)
        {
            if (sprite.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number)
                requiredTextures.Add(value.GetInt32());
        }
        void AddZoom(JsonElement sprite, string field)
        {
            if (!sprite.TryGetProperty(field, out var zoom) || zoom.ValueKind != JsonValueKind.Object) return;
            AddTexture(zoom, "texture");
            AddTexture(zoom, "depthTexture");
        }
        void RemapTexture(JsonObject sprite, string field)
        {
            if (sprite[field] is JsonValue value && value.TryGetValue<int>(out var index)) sprite[field] = textureMap[index];
        }
        void RemapZoom(JsonObject sprite, string field)
        {
            if (sprite[field] is not JsonObject zoom) return;
            RemapTexture(zoom, "texture");
            RemapTexture(zoom, "depthTexture");
        }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal sealed record Vertex(float X, float Y, float Z, float U, float V, float Nx, float Ny, float Nz);
    internal sealed record Texture(string File, int Width, int Height, string Encoding);
    internal sealed record WallSide(int X, int Y, int Level, int Direction, int Cut = 0, int? Edge = null, int? Side = null);
    internal sealed record WallDepth(int X, int Y, int Level, int Configuration);
    internal sealed record WallJunction(int X, int Y, int Level, int Flags, int Frame, bool Down);
    internal sealed record WaterSurface(int Adjacency, ushort Sprite, ushort[] Corners);
    internal sealed record FloorSurface(int X, int Y, ushort Pattern, int Half, int Level = 1, WaterSurface? Water = null);
    internal sealed record ZoomImage(int Texture, float X, float Y, float Width, float Height,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DepthTexture = null);
    internal sealed record RoofSurface(string Part, uint Style, float Pitch, string File);
    internal sealed record Mesh(int Texture, int ObjectID, float Depth, Vertex[] Vertices, int Level,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Room = null,
        bool Unlit = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FloorSurface? Floor = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NearTexture = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FarTexture = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Layer = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RoofSurface? Roof = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Shadow = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ShadowBrightness = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ZoomImage? FloorShadow = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ZoomImage? FloorShadowNear = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ZoomImage? FloorShadowFar = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float[]? Tint = null);
    internal sealed record TerrainVertex(float X, float Y, int R, int G, int B, int Dr, int Dg, int Db, int Grass);
    /// <summary>Represents mask art for a wall style, referencing either a base style in walls.iff or six
    /// SPR# frames defined within a door or window object file.</summary>
    internal sealed record WallStyleArt(IffFile? File, ushort SpriteBase, ushort BaseStyle)
    {
        public static WallStyleArt Base(ushort style) => new(null, 0, style);
    }

    internal sealed record CensorRect(float X, float Y, float Width, float Height, float Depth);
    internal sealed record SceneSprite(int Texture, float X, float Y, int Width, int Height, bool Flip, short ObjectID, float Depth, int Layer,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DepthTexture = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? DepthNear = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? DepthFar = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WallSide? Wall = null,
        int Level = 1,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Room = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WallJunction? Junction = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WallDepth? ArchitectureDepth = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ZoomImage? Near = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ZoomImage? Far = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Shadow = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ShadowBrightness = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ZoomImage? FloorShadow = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ZoomImage? FloorShadowNear = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ZoomImage? FloorShadowFar = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HideAtFarZoom = null);

    internal sealed record SoundSourceRecord(short ObjectID, int Level, float X, float Y, float Z);

    internal sealed record ExportedLotScene(
        short House,
        float Width,
        float Height,
        int LotSize,
        int[] FloorDiagonals,
        short[] TerrainHeights,
        int CameraOrientation,
        float OriginX,
        float OriginY,
        string WallMode,
        bool RoofsVisible,
        int ViewLevel,
        int Stories,
        SceneLighting.State? Lighting,
        Texture[] Textures,
        SceneSprite[] Sprites,
        SoundSourceRecord[] SoundSources,
        TerrainVertex[] Terrain,
        float PlacementLift,
        Mesh[] Meshes,
        CensorRect[] CensorRects,
        string[] Limitations,
        string[] MissingResources);

    internal sealed record DirectAnimationMesh(
        int Texture,
        int ObjectID,
        float Depth,
        int Level,
        int? Room,
        ZoomImage? FloorShadow,
        ZoomImage? FloorShadowNear,
        ZoomImage? FloorShadowFar,
        Vertex[] Vertices,
        float ShiftX,
        float ShiftY,
        float[]? Tint = null);

    internal sealed record DirectAnimationFrame(
        long BaseRevision,
        long Sequence,
        long Updates,
        int TextureStart,
        SceneLighting.State? Lighting,
        Texture[] Textures,
        SceneSprite[] Sprites,
        DirectAnimationMesh[] Meshes,
        CensorRect[] CensorRects);
    private static readonly (int X, int Y, int Width, int Height)[] WallRectangles = [(2, 39, 32, 136), (34, 39, 32, 136), (2, 55, 64, 120), (30, 73, 8, 116)];
    private static readonly (int X, int Y, int Width, int Height)[] NearWallRectangles = [(4, 77, 64, 271), (68, 77, 64, 271), (4, 109, 128, 240), (60, 145, 16, 232)];
    private static readonly (int X, int Y, int Width, int Height)[] FarWallRectangles = [(1, 20, 16, 68), (17, 20, 16, 68), (1, 28, 32, 60), (15, 37, 4, 58)];
    private static readonly (int X, int Y) JunctionOrigin = (2, 159);
    private const float FlatDepthIncrement = 1.525f;
    // Defines wall, floor, and junction depth ramp configurations across zoom levels, specifying
    // dimensions, base intensity, and depth gradients.
    private static readonly float[][] WallDepthConfig =
    [
        [64, 271, 74, 1, 0.5f], [64, 271, 135, -1, 0.5f], [128, 240, 89.5f, 0, 0.5f], [16, 232, 45, 0, 0.5f],
        [32, 135, 74, 2, 1f], [32, 135, 135, -2, 1f], [64, 120, 89.5f, 0, 1f], [8, 116, 45, 0, 1f],
        [16, 67, 74, 4, 2f], [16, 67, 135, -4, 2f], [32, 60, 89.5f, 0, 2f], [4, 58, 45, 0, 2f],
        [128, 64, 255, 0, -FlatDepthIncrement], [64, 32, 255, 0, -FlatDepthIncrement * 2], [32, 16, 255, 0, -FlatDepthIncrement * 4],
        [128, 64, 153, 0, FlatDepthIncrement], [64, 32, 153, 0, FlatDepthIncrement * 2], [32, 16, 153, 0, FlatDepthIncrement * 4],
        [128, 64, 235, 0, -FlatDepthIncrement], [64, 32, 235, 0, -FlatDepthIncrement * 2], [32, 16, 235, 0, -FlatDepthIncrement * 4],
    ];

    // Maps an 8-bit camera-rotated wall junction mask to a 1-based SPR# frame index, where 0 or 255
    // indicates no junction.
    internal static ReadOnlySpan<byte> JunctionFrames =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 255, 255, 255, 17, 255, 255, 255, 18, 255, 255, 255, 19, 255, 255, 255,
        20, 21, 255, 255, 255, 255, 255, 255, 22, 23, 255, 255, 255, 255, 255, 255,
        24, 255, 255, 255, 255, 255, 255, 255, 25, 255, 255, 255, 255, 255, 255, 255,
        26, 27, 28, 29, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        30, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        31, 32, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        33, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        34, 255, 35, 255, 36, 255, 37, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        38, 255, 255, 255, 39, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        40, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        41, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        42, 255, 43, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        44, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        45, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        46, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255
    ];

    internal static ushort SelectedGraphic(ushort baseGraphic, short graphicOffset) =>
        unchecked((ushort)(baseGraphic + graphicOffset));

    /// <summary>Converts a three-bit compass direction into the single-bit facing mask used by DGRP
    /// resources, shifted by camera rotation.</summary>
    internal static int FacingFlag(int direction) => 1 << (direction & 7);

    internal static DrawGroupImage? SelectedImage(DrawGroup group, int direction, int cameraOrientation, int zoom = 2)
    {
        var rotatedDirection = (uint)FacingFlag(direction) << (cameraOrientation * 2);
        var wanted = (rotatedDirection & 0xff) | (rotatedDirection >> 8);
        var images = group.Images;
        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            if (image.Direction == wanted && image.Zoom == zoom) return image;
        }
        return null;
    }

    internal static bool FrameHasPixels(int width, int height) => width > 0 && height > 0;

    internal static bool HasFloorArtwork(ushort pattern) =>
        pattern is not (0 or 0xff);

    internal static ushort WallMaskId(ushort wallStyle, bool down, int zoom = 1)
    {
        // Global wall mask SPR# indices start at 2 for far, 514 for medium, and 1026 for near zoom,
        // allocating two frames per style.
        var style = wallStyle is >= 1 and <= 21 ? wallStyle : (ushort)1;
        var origin = zoom switch { 0 => 2, 2 => 1026, _ => 514 };
        return (ushort)(origin + (style - 1) * 2 + (down ? 1 : 0));
    }

    /// <summary>Normalizes stored wall styles to standard style 1 while preserving opening cutout styles 2,
    /// 12, 13, and 14 before object-supplied styles are applied.</summary>
    internal static ushort NormalizedWallStyle(byte style) => style is 1 or 2 or 12 or 13 or 14 ? style : (ushort)1;

    /// <summary>Wall top junction cap pieces (SPR# 4096..4098) are rendered only when walls are fully up on
    /// the active story, or on stories below the active view level.</summary>
    internal static bool ShouldEmitWallJunction(int wallLevel, int viewLevel, string wallMode) =>
        wallLevel < viewLevel || wallMode == "up";

    internal static WallStyleArt ResolveWallStyle(ushort style, WallStyleArt? objectStyle) =>
        style == 1 && objectStyle is not null ? objectStyle : WallStyleArt.Base(style);

    /// <summary>Computes the mask SPR# ID for a wall style, offsetting object-supplied door and window
    /// masks by +3 when walls are lowered to select the doorway cutout.</summary>
    internal static ushort ResolveWallMaskId(WallStyleArt styleArt, bool down, int zoom = 1) =>
        styleArt.File is not null
            ? (ushort)(styleArt.SpriteBase + (down ? 3 : 0) + (zoom switch { 0 => 0, 2 => 2, _ => 1 }))
            : WallMaskId(styleArt.BaseStyle, down, zoom);

    internal static void PopulateTileJunctionFlags(
        int[] junctions,
        int lotSize,
        int x,
        int y,
        byte segments,
        byte styleSide0,
        byte styleSide1,
        byte? diagonalStyle = null,
        int stride = Size,
        bool[]? cutawayMask = null)
    {
        if (segments == 0) return;
        var diagStyle = diagonalStyle ?? styleSide1;
        var s = stride > 0 ? stride : (junctions.Length >= lotSize * lotSize ? lotSize : Size);

        bool CutawayEdge(int otherX, int otherY) =>
            cutawayMask is not null &&
            x >= 0 && y >= 0 && x < lotSize && y < lotSize &&
            otherX >= 0 && otherY >= 0 && otherX < lotSize && otherY < lotSize &&
            cutawayMask[y * lotSize + x] && cutawayMask[otherY * lotSize + otherX];

        bool Thick(int bit, byte rawStyle, int otherX, int otherY)
        {
            if ((segments & bit) == 0) return false;
            if (CutawayEdge(otherX, otherY)) return false;
            var style = rawStyle is 2 or 12 or 13 or 14 ? rawStyle : (byte)1;
            return style == 1;
        }

        // Segment bit 1 (top-left edge) sets junction bit 4 at (x, y) and bit 1 at (x, y + 1); segment bit
        // 2 (top-right edge) sets bit 8 at (x, y) and bit 2 at (x + 1, y).
        if (Thick(1, styleSide0, x - 1, y))
        {
            junctions[y * s + x] |= 4;
            if (y + 1 < lotSize) junctions[(y + 1) * s + x] |= 1;
        }
        if (Thick(2, styleSide1, x, y - 1))
        {
            junctions[y * s + x] |= 8;
            if (x + 1 < lotSize) junctions[y * s + x + 1] |= 2;
        }
        var diagThick = diagStyle is not (2 or 12 or 13 or 14) &&
                        (cutawayMask is null || !cutawayMask[y * lotSize + x]);
        if ((segments & 0x30) == 0x20 && diagThick)
        {
            // Diagonal wall junction arms pair 0x10 and 0x40 at line endpoints; walls.iff junction art maps
            // 0x40 to the downward arm and 0x10 to the upward arm.
            junctions[y * s + x] |= 64;
            if (x + 1 < lotSize && y + 1 < lotSize) junctions[(y + 1) * s + x + 1] |= 16;
        }
        else if ((segments & 0x30) == 0x10 && diagThick)
        {
            if (x + 1 < lotSize) junctions[y * s + x + 1] |= 32;
            if (y + 1 < lotSize) junctions[(y + 1) * s + x] |= 128;
        }
    }

    /// <summary>Calculates the wallpaper pattern SPR# resource ID in walls.iff by adding the pattern index
    /// to 1536 for far, 1792 for medium, and 2048 for near zoom.</summary>
    internal static ushort WallPatternId(ushort pattern, int zoom) =>
        (ushort)((zoom switch { 0 => 1536, 2 => 2048, _ => 1792 }) + pattern);

    internal static int WallPaletteId(int paletteId) =>
        paletteId is >= 0 and <= ushort.MaxValue ? paletteId : 0;

    internal static int RuntimeSlotSpriteFrame(int scale, int animationFrame) => 3 - scale + animationFrame * 3;

    internal static int RuntimeSlotOverlayFrame(int scale) => 3 - scale;

    /// <summary>Calculates speech balloon screen coordinates by centering the image horizontally on the
    /// runtime slot anchor and applying scale-adjusted vertical offsets.</summary>
    internal static (int X, int Y) RuntimeSlotSpeechImagePosition(float renderX, float renderY, float slotZ,
        int width, int height, int scale = 2)
    {
        var slotX = renderX - (8 << scale) / 2f;
        var slotY = renderY - slotZ * (1 << (scale + 1));
        var imageOffsetX = scale == 2 ? 0x20 / 2 : 0x20;
        var imageOffsetY = scale == 2 ? -36 / 2 : -36;
        return ((int)(slotX + imageOffsetX - width / 2f), (int)(slotY + imageOffsetY - height / 2f));
    }

    /// <summary>Adjusts runtime slot sprite screen position by subtracting shape bounds height and applying
    /// SPR# bounding box offsets.</summary>
    internal static (int X, int Y) RuntimeSlotSpritePosition(float renderX, float renderY, float slotZ,
        int height, short boundsLeft = 0, short boundsTop = 0, int scale = 2) =>
        ((int)(renderX - (8 << scale) / 2f + boundsLeft),
            (int)(renderY - slotZ * (1 << (scale + 1)) - height + boundsTop));

    /// <summary>Applies transparency to composited speech balloon bitmaps using the 16-bit RGB565 color key
    /// sampled from the top-left corner pixel.</summary>
    internal static void KeySpeechBitmapTransparency(int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0 || rgba.Length < 4) return;
        var keyR = rgba[0] >> 3;
        var keyG = rgba[1] >> 2;
        var keyB = rgba[2] >> 3;
        for (var at = 0; at + 3 < rgba.Length; at += 4)
        {
            var r5 = rgba[at] >> 3;
            var g6 = rgba[at + 1] >> 2;
            var b5 = rgba[at + 2] >> 3;
            if ((r5 == keyR && g6 == keyG && b5 == keyB) || (r5 == 31 && g6 == 0 && b5 == 31))
            {
                rgba[at] = 0;
                rgba[at + 1] = 0;
                rgba[at + 2] = 0;
                rgba[at + 3] = 0;
            }
        }
    }

    /// <summary>Determines an object's shadow ID, using OBJD.Shadow for single-tile objects and building an
    /// eight-neighbor footprint mask from non-elevated sibling parts for multi-tile objects.</summary>
    internal static int? ShadowId(ObjectEntry entry, IReadOnlyDictionary<(IffFile File, ushort MasterId, ushort SubIndex), ObjectEntry> parts)
    {
        var definition = entry.Definition;
        var shadow = definition[ObjdField.Shadow];
        if (shadow == ushort.MaxValue) return null;
        var masterId = definition[ObjdField.MasterId];
        if (masterId == 0) return shadow;

        // SubIndex packs the signed tile X offset in the high byte and unsigned Y offset in the low byte.
        var subIndex = definition[ObjdField.SubIndex];
        var originX = unchecked((sbyte)(subIndex >> 8));
        var originY = (byte)subIndex;
        ReadOnlySpan<(sbyte X, sbyte Y)> neighbours =
        [
            (0, -1), (-1, 0), (0, 1), (1, 0),
            (-1, -1), (-1, 1), (1, 1), (1, -1),
        ];
        var mask = 0;
        for (var bit = 0; bit < neighbours.Length; bit++)
        {
            var x = unchecked((byte)(originX + neighbours[bit].X));
            var y = unchecked((byte)(originY + neighbours[bit].Y));
            var target = (ushort)((x << 8) | y);
            parts.TryGetValue((entry.File, masterId, target), out var part);
            if (part is not null && part.Definition[ObjdField.Shadow] != ushort.MaxValue)
                mask |= 1 << bit;
        }

        return ShadowMaskIsValid(mask) ? mask : 0;
    }

    private static readonly bool[] validShadowMasks = BuildValidShadowMasks();

    private static bool[] BuildValidShadowMasks()
    {
        var valid = new bool[256];
        for (var mask = 0; mask < valid.Length; mask++)
        {
            var corners = ShadowLookupValues(mask);
            valid[mask] = corners[0] is not (2 or 4 or 6) &&
                          corners[1] is not (2 or 4 or 6) &&
                          corners[2] is not (2 or 4 or 6) &&
                          corners[3] is not (2 or 4 or 6);
        }
        return valid;
    }

    /// <summary>Validates a multi-tile shadow mask by ensuring none of its four corner neighbor
    /// configurations equal 2, 4, or 6.</summary>
    internal static bool ShadowMaskIsValid(int mask)
    {
        if ((uint)mask < (uint)validShadowMasks.Length) return validShadowMasks[mask];
        var corners = ShadowLookupValues(mask);
        return !corners.Any(corner => corner is 2 or 4 or 6);
    }

    /// <summary>Packs each tile edge's cardinal neighbor bit (weight 1) and two flanking diagonal neighbor
    /// bits (weights 2 and 4) into a 0..7 profile curve index for all four camera-space edges.</summary>
    internal static int[] ShadowLookupValues(int mask) =>
    [
        (mask & 1) + 2 * ((mask >> 7) & 1) + 4 * ((mask >> 4) & 1),
        ((mask >> 1) & 1) + 2 * ((mask >> 5) & 1) + 4 * ((mask >> 4) & 1),
        ((mask >> 2) & 1) + 2 * ((mask >> 6) & 1) + 4 * ((mask >> 5) & 1),
        ((mask >> 3) & 1) + 2 * ((mask >> 6) & 1) + 4 * ((mask >> 7) & 1),
    ];

    /// <summary>Rotates a shadow ID per quarter-turn: single-tile shadow 16 remains invariant, while
    /// multi-tile masks rotate their cardinal low nibble and diagonal high nibble independently.</summary>
    internal static int RotateShadowId(int shadowId, int quarterTurns)
    {
        var turns = ((quarterTurns % 4) + 4) % 4;
        if (turns == 0 || shadowId == 16) return shadowId & 0xff;
        var cardinal = shadowId & 0x0f;
        var diagonal = (shadowId >> 4) & 0x0f;
        for (var step = 0; step < turns; step++)
        {
            cardinal = ((cardinal >> 1) | ((cardinal & 1) << 3)) & 0x0f;
            diagonal = ((diagonal >> 1) | ((diagonal & 1) << 3)) & 0x0f;
        }
        return cardinal | (diagonal << 4);
    }

    /// <summary>Evaluates edge profile curve functions for floor shadow blending, mapping curve types to
    /// sine power functions or constant bounds.</summary>
    internal static float ShadowLookup(int type, float value) => type switch
    {
        0 => 0f,
        1 => MathF.Pow(MathF.Max(0f, MathF.Sin(value * MathF.PI)), 0.6f),
        3 => MathF.Pow(MathF.Max(0f, MathF.Sin(value * MathF.PI * 0.5f)), 0.6f),
        5 => MathF.Pow(MathF.Max(0f, MathF.Sin((1f - value) * MathF.PI * 0.5f)), 0.6f),
        7 => 1f,
        _ => 0f,
    };

    /// <summary>Bilinearly blends the four edge profile curves across camera-tile coordinates (u, v) in [0,
    /// 1]² and scales the result by 0.5.</summary>
    internal static float EvaluateFloorShadowShade(ReadOnlySpan<int> lookupTypes, float u, float v) =>
        0.5f * ((1f - v) * ShadowLookup(lookupTypes[0], u)
            + (1f - u) * ShadowLookup(lookupTypes[1], v)
            + v * ShadowLookup(lookupTypes[2], u)
            + u * ShadowLookup(lookupTypes[3], v));

    /// <summary>Calculates a block of up to 5x5 source tiles cut toward the camera from an outdoor room
    /// tile, stepping back toward the lot bounds.</summary>
    internal static IEnumerable<(int X, int Y)> CutawayRectangle(LotCamera camera, int sourceX, int sourceY, int lotSize)
    {
        var origin = camera.CameraPoint(sourceX + 0.5f, sourceY + 0.5f);
        var cameraX = (int)MathF.Floor(origin.X);
        var cameraY = (int)MathF.Floor(origin.Y);
        var extent = 4;
        while (extent > 0 && (cameraX + extent >= lotSize || cameraY + extent >= lotSize)) extent--;
        for (var y = 0; y <= extent; y++)
        for (var x = 0; x <= extent; x++)
        {
            var tile = camera.SourceTile(cameraX + x, cameraY + y);
            if (tile.X >= 0 && tile.Y >= 0 && tile.X < lotSize && tile.Y < lotSize) yield return tile;
        }
    }

    /// <summary>Returns the story height in screen pixels for a given zoom level: 58 for far, 116 for
    /// medium, and 232 for near.</summary>
    internal static int CutawayStoryPixels(int zoom) => 58 << (Math.Clamp(zoom, 1, 3) - 1);

    /// <summary>Computes the cutaway visibility matrix by cutting indoor room tiles and ray-walking toward
    /// the camera in an alternating (0,1) and (1,-1) step pattern to cut overlapping wall columns.</summary>
    internal static bool[] CutawayMatrix(RoomMap rooms, LotCamera camera, int lotSize, IEnumerable<ushort> selectedRooms,
        IEnumerable<(int X, int Y)> extraTiles, int zoom = 2, Func<int, int, float>? altitudePixels = null)
    {
        zoom = Math.Clamp(zoom, 1, 3);
        var mask = new bool[lotSize * lotSize];
        var selected = new HashSet<ushort>(selectedRooms);
        foreach (var (x, y) in extraTiles) if (x >= 0 && y >= 0 && x < lotSize && y < lotSize) mask[y * lotSize + x] = true;
        if (selected.Count == 0) return mask;
        int unit = 8 << zoom, half = unit / 2, story = CutawayStoryPixels(zoom), boxX = unit - 2;
        var altitudeScale = MathF.Pow(2, zoom - 2);
        float Altitude(int sx, int sy) => altitudePixels is null ? 0 : altitudePixels(sx, sy) * altitudeScale;
        bool InLot(int cameraX, int cameraY) => cameraX >= 1 && cameraY >= 1 && cameraX <= lotSize - 2 && cameraY <= lotSize - 2;
        for (var y = 0; y < lotSize && y < rooms.Height; y++)
        for (var x = 0; x < lotSize && x < rooms.Width; x++)
        {
            var room = rooms.RoomAt(x, y);
            if (selected.Contains(room) && !rooms.IsOutside(room)) Walk(x, y, 0, 0);
            else if (room == RoomMap.NoRoom && rooms.AdjacentRoomsAtTile(x, y) is { } pair)
            {
                // Halves a diagonal wall tile's screen bounds during cutaway testing, keeping the left half
                // for side 1 or the right half for side 3.
                var diagonal = rooms.HasRoomDividingDiagonal(x, y);
                if (!diagonal) continue;
                var vertical = (rooms.DiagonalKind(x, y) & 0x20) != 0;
                var points = vertical ? new[] { (14, 1), (1, 14) } : new[] { (1, 1), (14, 14) };
                var rooms2 = new[] { pair.First, pair.Second };
                var centre = camera.CameraPoint(x + 0.5f, y + 0.5f);
                for (var i = 0; i < 2; i++)
                {
                    if (!selected.Contains(rooms2[i]) || rooms.IsOutside(rooms2[i])) continue;
                    var p = camera.CameraPoint(x + points[i].Item1 / 16f, y + points[i].Item2 / 16f);
                    var left = (p.X - p.Y) < (centre.X - centre.Y);
                    Walk(x, y, left ? 0 : 1, left ? 1 : 0);
                }
            }
        }

        void Walk(int x, int y, int raiseMin, int cutMax)
        {
            mask[y * lotSize + x] = true;
            var origin = camera.CameraPoint(x + 0.5f, y + 0.5f);
            int originX = (int)MathF.Floor(origin.X), originY = (int)MathF.Floor(origin.Y);
            var originAltitude = Altitude(x, y);
            int minDelta = 3 - 2 * unit + raiseMin * unit, maxDelta = 2 * unit - 2 - cutMax * unit;
            int walkX = originX, walkY = originY + 1;
            for (var guard = 0; guard < 4 * lotSize; guard++)
            {
                var outside = 0;
                for (var check = 0; check < 3; check++)
                {
                    if (!InLot(walkX, walkY)) outside++;
                    else
                    {
                        var (sx, sy) = camera.SourceTile(walkX, walkY);
                        if (sx >= 0 && sy >= 0 && sx < lotSize && sy < lotSize && !mask[sy * lotSize + sx])
                        {
                            int dx = walkX - originX, dy = walkY - originY;
                            var screenX = unit * (dx - dy);
                            var screenY = half * (dx + dy) - (Altitude(sx, sy) - originAltitude);
                            if (screenX <= maxDelta && screenX >= minDelta && screenY <= story + boxX - 1 && screenY >= 2 - unit)
                                mask[sy * lotSize + sx] = true;
                        }
                    }
                    if (check == 0) { walkX++; walkY--; }
                    else if (check == 1) walkY++;
                }
                if (outside >= 3) break;
                walkY++;
            }
        }
        return mask;
    }

    /// <summary>Probes screen space to the right of the hovered tile in quarter-tile increments up to half
    /// a story height, returning the first distinct room encountered.</summary>
    internal static ushort CutawayProbeRoom(RoomMap rooms, LotCamera camera, int lotSize, int tileX, int tileY, int zoom = 2)
    {
        zoom = Math.Clamp(zoom, 1, 3);
        var first = rooms.RoomAt(tileX, tileY);
        var probe = first;
        int unit = 8 << zoom, half = unit / 2;
        var origin = camera.CameraPoint(tileX + 0.5f, tileY + 0.5f);
        var screenX = unit * (origin.X - origin.Y);
        var screenY = half * (origin.X + origin.Y);
        for (var step = unit / 2; step < CutawayStoryPixels(zoom) / 2; step += unit / 2)
        {
            var x = (screenX + step) / unit;
            var y = screenY / half;
            int cameraX = (int)MathF.Floor((y + x) / 2), cameraY = (int)MathF.Floor((y - x) / 2);
            if (cameraX >= 1 && cameraY >= 1 && cameraX <= lotSize - 2 && cameraY <= lotSize - 2)
            {
                var (sx, sy) = camera.SourceTile(cameraX, cameraY);
                if (sx >= 0 && sy >= 0 && sx < rooms.Width && sy < rooms.Height) probe = rooms.RoomAt(sx, sy);
            }
            if (probe != first) break;
        }
        return probe;
    }

    /// <summary>Determines the tile and slot that store a wall edge's pattern and style, where top-left and
    /// top-right slots hold edges towards -x and -y neighbors respectively.</summary>
    internal static (int X, int Y, int Side) EdgeOwner(int x, int y, int neighbourX, int neighbourY)
    {
        var dx = neighbourX - x;
        var dy = neighbourY - y;
        if (dx < 0) return (x, y, 0);
        if (dx > 0) return (neighbourX, neighbourY, 0);
        if (dy < 0) return (x, y, 1);
        if (dy > 0) return (neighbourX, neighbourY, 1);
        return (-1, -1, 0);
    }

    internal static bool DynamicSpriteVisible(int spriteId, int dynamicBase, int dynamicCount, short[]? flags)
    {
        var index = spriteId - dynamicBase;
        if (index < 0 || index >= dynamicCount) return true;
        return flags is not null && index < flags.Length && flags[index] != 0;
    }

    public static int Stories(IffFile lot) => lot.Find(ResourceType.Parse("Arry"), 101) is not null ? 2 : 1;

    /// <summary>Maps a lot number to its corresponding terrain color palette index (0..7).</summary>
    private static int TerrainType(short house) => house switch
    {
        28 or 29 or 46 or 47 or 48 => 1,
        40 or 41 or 42 => 3,
        >= 90 and <= 94 => 5,
        95 or 96 => 6,
        99 => 7,
        _ => 0,
    };

    private static readonly ConditionalWeakTable<ObjectRegistry, IReadOnlyDictionary<(IffFile File, ushort MasterId, ushort SubIndex), ObjectEntry>> shadowPartsCache = new();

    private static IReadOnlyDictionary<(IffFile File, ushort MasterId, ushort SubIndex), ObjectEntry> GetShadowParts(ObjectRegistry registry) =>
        shadowPartsCache.GetValue(registry, static reg =>
        {
            var parts = new Dictionary<(IffFile File, ushort MasterId, ushort SubIndex), ObjectEntry>();
            foreach (var part in reg.Entries)
            {
                if (part.Definition[ObjdField.MasterId] is not 0 && (short)part.Definition[ObjdField.LevelOffset] == 0)
                    parts.TryAdd((part.File, part.Definition[ObjdField.MasterId], part.Definition[ObjdField.SubIndex]), part);
            }
            return parts;
        });

    private readonly record struct SavedDoor(
        ObjectEntry Entry,
        uint Guid,
        short Id,
        float X,
        float Y,
        int TileX,
        int TileY,
        int Level,
        int Direction,
        ushort Flags1,
        ushort Flags2,
        short WallPlacement,
        short Graphic,
        short[] DynamicSpriteFlags);

    public sealed class AnimationState
    {
        internal long Revision;
        internal object? Kept;
    }

    private readonly record struct ObjectSpriteCacheKey(
        uint Guid,
        float OriginX,
        float OriginY,
        float OriginZ,
        int Level,
        int Direction,
        short Graphic,
        bool Contained,
        bool ShadowHidden,
        bool Lit,
        int SubtileRoom,
        int RoomLightByte,
        int CameraOrientation);

    private sealed record CachedObjectSprites(ObjectSpriteCacheKey Key, short[]? DynamicFlags, SceneSprite[] Sprites);

    private static bool DynamicFlagsSequenceEqual(short[]? left, short[]? right)
    {
        var leftEmpty = left is null || left.Length == 0;
        var rightEmpty = right is null || right.Length == 0;
        if (leftEmpty && rightEmpty) return true;
        if (leftEmpty || rightEmpty) return false;
        return left.AsSpan().SequenceEqual(right);
    }

    private sealed class Kept
    {
        public List<Texture> Textures = [];
        public int BaseTextureCount;
        public Dictionary<(uint Guid, int SpriteId, int Frame), int> SpriteTextures = [];
        public Dictionary<(uint Guid, int BitmapId), (int Texture, int Width, int Height)> SpeechImageTextures = [];
        public Dictionary<(uint Guid, int SpriteId, int Frame), int> SpriteDepthTextures = [];
        public Dictionary<(int Shadow, int Attenuation, int Zoom), int?> FloorShadowTextures = [];
        public Dictionary<string, int> AvatarTextures = new(StringComparer.Ordinal);
        public Dictionary<short, CachedObjectSprites> ObjectSprites = [];
        public HashSet<string> Written = new(StringComparer.Ordinal);
        public float[] Heights = [];
        public float BaseHeight;
        public bool[]? CutawayMask;
        public bool[]? WallAdjacentTiles;
        public int ViewLevel;
        public string WallMode = "down";
        public int CameraOrientation;
        public (int X, int Y)? CutawayTile;
        public ushort[] CutawayRooms = [];
        public (int X, int Y)? CutawaySim;
        public int CutawayZoom = 2;
        public HashSet<short> WallObjectIds = [];
        public SavedDoor[] SavedDoors = [];
        public float ShiftX, ShiftY;
        public SceneLighting.State? CachedLighting;
        public ulong CachedLightingXor;
        public ulong CachedLightingSum;
        public int CachedLightingRoomCount = -1;
        public int CachedLightingMapRooms = -1;
        public int CachedLightingMinute = -1;
        public int CachedLightingPhase = -1;
        public short CachedLightingHouse = -1;
        public long LastAnimatedUpdates = -1;
        public SceneSprite[]? CachedNonCarriedObjectSprites;
        public SceneLighting.Light[]? CachedSubTickLights;
        public SceneLighting.Window[]? CachedSubTickWindows;
    }

    private static float HeightAtArray(float[] heights, int x, int y)
    {
        x = Math.Clamp(x, 0, Size - 1);
        y = Math.Clamp(y, 0, Size - 1);
        return heights[x * Size + y];
    }

    private static float ElevationAt(float[] heights, float baseHeight, float x, float y)
    {
        var left = Math.Clamp((int)x, 0, Size - 1);
        var top = Math.Clamp((int)y, 0, Size - 1);
        var right = Math.Min(left + 1, Size - 1);
        var bottom = Math.Min(top + 1, Size - 1);
        var fx = Math.Clamp(x - left, 0, 1);
        var fy = Math.Clamp(y - top, 0, 1);
        var upper = HeightAtArray(heights, left, top) * (1 - fx) + HeightAtArray(heights, right, top) * fx;
        var lower = HeightAtArray(heights, left, bottom) * (1 - fx) + HeightAtArray(heights, right, bottom) * fx;
        return (upper * (1 - fy) + lower * fy - baseHeight) * 0.75f;
    }

    private static bool CutawayRoomsSequenceEqual(ushort[] left, IReadOnlyList<ushort>? right)
    {
        var rightCount = right?.Count ?? 0;
        if (left.Length != rightCount) return false;
        for (var i = 0; i < left.Length; i++)
            if (left[i] != right![i]) return false;
        return true;
    }

    // Combines cutaway rooms and the selected avatar's room, adding a 5x5 tile block toward the camera
    // around the avatar and cursor when outdoors.
    private static bool[]? ComputeCutawayMask(
        RoomMap? rooms,
        LotCamera camera,
        int lotSize,
        string wallMode,
        (int X, int Y)? cutawayTile,
        IReadOnlyList<ushort>? cutawayRooms,
        (int X, int Y)? cutawaySim,
        int cutawayZoom,
        float[] heights,
        float baseHeight)
    {
        if (wallMode != "cutaway" || rooms is null) return null;
        var selectedCutawayRooms = new HashSet<ushort>();
        var cutawayRectangleTiles = new HashSet<(int X, int Y)>();
        var touchedRooms = new HashSet<ushort>(cutawayRooms ?? Array.Empty<ushort>());
        if (cutawayTile is { } hovered && hovered.X >= 0 && hovered.Y >= 0 && hovered.X < rooms.Width && hovered.Y < rooms.Height &&
            rooms.RoomAt(hovered.X, hovered.Y) is var hoveredRoom && hoveredRoom != RoomMap.NoRoom)
            touchedRooms.Add(hoveredRoom);
        var simOutside = false;
        if (cutawaySim is { } sim && sim.X >= 0 && sim.Y >= 0 && sim.X < rooms.Width && sim.Y < rooms.Height &&
            rooms.RoomAt(sim.X, sim.Y) is var simRoom && simRoom != RoomMap.NoRoom)
        {
            touchedRooms.Add(simRoom);
            if (rooms.IsOutside(simRoom))
            {
                simOutside = true;
                foreach (var tile in CutawayRectangle(camera, sim.X, sim.Y, lotSize)) cutawayRectangleTiles.Add(tile);
            }
        }
        var anyOutside = simOutside;
        foreach (var room in touchedRooms)
        {
            if (rooms.IsOutside(room)) anyOutside = true;
            else selectedCutawayRooms.Add(room);
        }
        if (anyOutside && cutawayTile is { } cursor &&
            cursor.X >= 0 && cursor.Y >= 0 && cursor.X < lotSize && cursor.Y < lotSize)
        {
            foreach (var tile in CutawayRectangle(camera, cursor.X, cursor.Y, lotSize)) cutawayRectangleTiles.Add(tile);
        }
        return CutawayMatrix(rooms, camera, lotSize, selectedCutawayRooms, cutawayRectangleTiles, cutawayZoom,
            (x, y) => ElevationAt(heights, baseHeight, x + 0.5f, y + 0.5f));
    }

    internal static bool CutawayMaskMatches(
        SimulationWorld world,
        AnimationState state,
        long revision,
        int viewLevel,
        string wallMode,
        int cameraOrientation,
        (int X, int Y)? cutawayTile,
        IReadOnlyList<ushort>? cutawayRooms,
        (int X, int Y)? cutawaySim,
        int cutawayZoom)
    {
        if (state.Revision != revision || state.Kept is not Kept kept || kept.Heights.Length < Size * Size) return false;
        if (kept.ViewLevel != viewLevel || kept.WallMode != wallMode || kept.CameraOrientation != cameraOrientation)
            return false;
        if (kept.CutawayTile == cutawayTile &&
            kept.CutawaySim == cutawaySim &&
            kept.CutawayZoom == cutawayZoom &&
            CutawayRoomsSequenceEqual(kept.CutawayRooms, cutawayRooms))
            return true;

        var lotSize = world.Globals.Length > 23 && world.Globals[23] is > 0 and <= Size ? world.Globals[23] : Size;
        var rooms = world.LotRoomMaps.TryGetValue(viewLevel, out var roomMap) ? roomMap : null;
        var camera = new LotCamera(cameraOrientation, lotSize, lotSize);
        var nextMask = ComputeCutawayMask(rooms, camera, lotSize, wallMode, cutawayTile, cutawayRooms, cutawaySim, cutawayZoom, kept.Heights, kept.BaseHeight);
        var matches = ReferenceEquals(nextMask, kept.CutawayMask) ||
                      (nextMask is not null && kept.CutawayMask is not null &&
                       (nextMask.AsSpan().SequenceEqual(kept.CutawayMask) ||
                        CutawayChangesIgnoreWallsAndPortals(world, viewLevel, lotSize, kept.CutawayMask, nextMask, kept.WallAdjacentTiles)));
        if (matches)
        {
            kept.CutawayMask = nextMask;
            kept.CutawayTile = cutawayTile;
            kept.CutawaySim = cutawaySim;
            kept.CutawayZoom = cutawayZoom;
            kept.CutawayRooms = cutawayRooms is null or { Count: 0 } ? [] : [.. cutawayRooms];
        }
        return matches;
    }

    private static bool CutawayChangesIgnoreWallsAndPortals(
        SimulationWorld world,
        int viewLevel,
        int lotSize,
        bool[] previousMask,
        bool[] nextMask,
        bool[]? wallAdjacentTiles)
    {
        if (wallAdjacentTiles is null || previousMask.Length != nextMask.Length || wallAdjacentTiles.Length != nextMask.Length)
            return false;

        for (var i = 0; i < nextMask.Length; i++)
        {
            if (previousMask[i] == nextMask[i]) continue;
            if (wallAdjacentTiles[i]) return false;
        }

        foreach (var obj in world.WalkOrder)
        {
            if (obj.Position.Height != viewLevel || (obj[ObjectData.Flags] & 0x400) == 0) continue;
            var tx = obj.Position.X >> 4;
            var ty = obj.Position.Y >> 4;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var nx = tx + dx;
                var ny = ty + dy;
                if ((uint)nx >= (uint)lotSize || (uint)ny >= (uint)lotSize) continue;
                var idx = ny * lotSize + nx;
                if (previousMask[idx] != nextMask[idx]) return false;
            }
        }
        return true;
    }

    public static string Export(IffFile lot, SimulationWorld world, ObjectRegistry registry, string gameRoot, string directory, short house, long revision, int viewLevel = 1, string wallMode = "down", int cameraOrientation = 0, (int X, int Y)? cutawayTile = null, bool roofsVisible = false, AnimationState? state = null, IReadOnlyList<ushort>? cutawayRooms = null, (int X, int Y)? cutawaySim = null, int cutawayZoom = 2, bool showLoweredPortals = false) =>
        (string)Build(lot, world, registry, gameRoot, directory, house, revision, viewLevel, wallMode, cameraOrientation, cutawayTile, roofsVisible, state, false, 0, 0, cutawayRooms, cutawaySim, cutawayZoom, showLoweredPortals, 0f);

    public static object? Animation(IffFile lot, SimulationWorld world, ObjectRegistry registry, string gameRoot, string directory, short house, long revision, AnimationState state, long sequence, long updates, int viewLevel = 1, string wallMode = "down", int cameraOrientation = 0, (int X, int Y)? cutawayTile = null, bool roofsVisible = false, IReadOnlyList<ushort>? cutawayRooms = null, (int X, int Y)? cutawaySim = null, int cutawayZoom = 2, bool showLoweredPortals = false, float subTickAlpha = 0f) =>
        state.Kept is Kept && state.Revision == revision
            ? Build(lot, world, registry, gameRoot, directory, house, revision, viewLevel, wallMode, cameraOrientation, cutawayTile, roofsVisible, state, true, sequence, updates, cutawayRooms, cutawaySim, cutawayZoom, showLoweredPortals, subTickAlpha, directAnimation: false)
            : null;

    internal static DirectAnimationFrame? AnimationDirect(IffFile lot, SimulationWorld world, ObjectRegistry registry, string gameRoot, string directory, short house, long revision, AnimationState state, long sequence, long updates, int viewLevel = 1, string wallMode = "down", int cameraOrientation = 0, (int X, int Y)? cutawayTile = null, bool roofsVisible = false, IReadOnlyList<ushort>? cutawayRooms = null, (int X, int Y)? cutawaySim = null, int cutawayZoom = 2, bool showLoweredPortals = false, float subTickAlpha = 0f) =>
        state.Kept is Kept && state.Revision == revision
            ? (DirectAnimationFrame)Build(lot, world, registry, gameRoot, directory, house, revision, viewLevel, wallMode, cameraOrientation, cutawayTile, roofsVisible, state, true, sequence, updates, cutawayRooms, cutawaySim, cutawayZoom, showLoweredPortals, subTickAlpha, directAnimation: true)
            : null;

    private static ulong MixLightingTerm(ulong value)
    {
        value += 0x9e3779b97f4a7c15UL;
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }

    private static (ulong Xor, ulong Sum, int RoomCount, int MapRooms, int Minute, int Phase) LightingStamp(
        SimulationWorld world)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (var pair in world.PersonRoomMotiveEntries)
        {
            var term = MixLightingTerm(
                ((ulong)pair.Key << 48)
                ^ ((ulong)(pair.Value.IsOutside ? 1 : 0) << 40)
                ^ ((ulong)pair.Value.LightsEnabledFlag73 << 41)
                ^ ((ulong)pair.Value.LightOverrideFlag75 << 42)
                ^ ((ulong)(uint)BitConverter.SingleToInt32Bits(pair.Value.LightLevel6c) << 8)
                ^ ((ulong)(ushort)pair.Value.LightingObjectCount140 << 24)
                ^ (ushort)pair.Value.PersonObjectCount142);
            xor ^= term;
            sum = unchecked(sum + term);
        }

        var mapRooms = 0;
        foreach (var map in world.LotRoomMaps.Values)
            mapRooms += map.RoomIds.Count;

        return (xor, sum, world.PersonRoomMotiveEntries.Count, mapRooms,
            world.Clock.Hour * 60 + world.Clock.Minute, world.Clock.TimeOfDay);
    }

    private static object Build(IffFile lot, SimulationWorld world, ObjectRegistry registry, string gameRoot, string directory, short house, long revision, int viewLevel, string wallMode, int cameraOrientation, (int X, int Y)? cutawayTile, bool roofsVisible, AnimationState? retain, bool animating, long sequence, long updates, IReadOnlyList<ushort>? cutawayRooms, (int X, int Y)? cutawaySim, int cutawayZoom, bool showLoweredPortals = false, float subTickAlpha = 0f, bool directAnimation = false)
    {
        var shadowParts = GetShadowParts(registry);
        // SIMI global 23 defines the active lot size, while saved ARRY layers retain a fixed 64-cell
        // storage stride.
        var lotSize = world.Globals.Length > 23 && world.Globals[23] is > 0 and <= Size ? world.Globals[23] : Size;
        var lapWatch = Stopwatch.StartNew();
        var lapText = new StringBuilder();
        void Lap(string name) { lapText.Append($" {name}={lapWatch.ElapsedMilliseconds}"); lapWatch.Restart(); }
        var kept = animating ? (Kept)retain!.Kept! : new Kept();
        var heights = animating ? kept.Heights : ReadHeights(lot);
        var baseHeight = animating ? kept.BaseHeight : heights[0];
        var rooms = world.LotRoomMaps.TryGetValue(viewLevel, out var roomMap) ? roomMap : null;
        var camera = new LotCamera(cameraOrientation, lotSize, lotSize);
        var stamp = LightingStamp(world);
        SceneLighting.State? baseLighting;
        if (animating &&
            kept.CachedLightingHouse == house &&
            kept.CachedLightingXor == stamp.Xor &&
            kept.CachedLightingSum == stamp.Sum &&
            kept.CachedLightingRoomCount == stamp.RoomCount &&
            kept.CachedLightingMapRooms == stamp.MapRooms &&
            kept.CachedLightingMinute == stamp.Minute &&
            kept.CachedLightingPhase == stamp.Phase)
        {
            baseLighting = kept.CachedLighting;
        }
        else
        {
            baseLighting = SceneLighting.Build(world, rooms, house);
            kept.CachedLighting = baseLighting;
            kept.CachedLightingHouse = house;
            kept.CachedLightingXor = stamp.Xor;
            kept.CachedLightingSum = stamp.Sum;
            kept.CachedLightingRoomCount = stamp.RoomCount;
            kept.CachedLightingMapRooms = stamp.MapRooms;
            kept.CachedLightingMinute = stamp.Minute;
            kept.CachedLightingPhase = stamp.Phase;
        }

        int TileSpriteLightByte(int? subtileRoom)
        {
            if (house == 99 || baseLighting is null) return 255;
            if (subtileRoom is { } roomId && roomId >= 0 && roomId < baseLighting.Rooms.Length)
                return baseLighting.Rooms[roomId].R;
            return baseLighting.Outside.R;
        }

        float HeightAt(int x, int y) => HeightAtArray(heights, x, y);
        float Elevation(float x, float y) => ElevationAt(heights, baseHeight, x, y);

        var terrain = animating ? null! : new List<TerrainVertex>();
        var meshVertices = animating ? null! : new List<(Vertex Vertex, int TileX, int TileY)>();
        var textures = animating ? kept.Textures : new List<Texture>();
        var animationStart = animating ? textures.Count : 0;
        var files = new Dictionary<string, byte[]>();
        var meshes = new List<Mesh>();
        var sprites = new List<SceneSprite>();
        var missingResources = new HashSet<string>(StringComparer.Ordinal);
        var floorArtwork = TryOpenOptionalArtwork(Path.Combine(gameRoot, "GameData", "floors.iff"));
        var floorShadowTextures = animating
            ? kept.FloorShadowTextures
            : new Dictionary<(int Shadow, int Attenuation, int Zoom), int?>();
        var spriteTextures = animating ? kept.SpriteTextures : new Dictionary<(uint Guid, int SpriteId, int Frame), int>();
        var speechImageTextures = animating ? kept.SpeechImageTextures : new Dictionary<(uint Guid, int BitmapId), (int Texture, int Width, int Height)>();
        var spriteDepthTextures = animating ? kept.SpriteDepthTextures : new Dictionary<(uint Guid, int SpriteId, int Frame), int>();

        int? FloorShadowTexture(int shadowId, int attenuation, int zoom)
        {
            // Preloads floor shadow surfaces from floors.iff SPR2 resources (0x300-0x350) for shadow IDs 0
            // and 16, generating other masks dynamically from base diamond sprites.
            if (floorArtwork is null || shadowId is < 0 or > 255 || attenuation >= 0xf8 || zoom is < 1 or > 3) return null;
            var key = (shadowId, attenuation, zoom);
            if (floorShadowTextures.TryGetValue(key, out var cached)) return cached;
            if (floorShadowBytesCache.Count > 1024) floorShadowBytesCache.Clear();
            var cachedPixels = floorShadowBytesCache.GetOrAdd((floorArtwork, shadowId, attenuation, zoom), static k =>
            {
                var (artwork, sId, atten, z) = k;
                var spriteId = sId is 0 or 16
                    ? (ushort)(0x300 + (z - 1) * 0x20 + (sId == 16 ? 0x10 : 0))
                    : (ushort)((z - 1) * 0x100);
                var chunk = artwork.Find(ResourceType.Spr2, spriteId);
                if (chunk is null) return default;
                var art = CachedDepthSprite(artwork, chunk);
                if (art.Frames.Count == 0) return default;
                var frame = art.Frames[0];
                var generated = sId is not (0 or 16);
                var lookupTypes = generated ? ShadowLookupValues(sId) : [];
                if (generated && lookupTypes.Any(type => type is 2 or 4 or 6)) return default;
                var buffer = new byte[frame.Width * frame.Height * 4];
                var generatedWidth = frame.Width + (frame.Width & 1);
                var scale = generatedWidth > 0 ? 2f / generatedWidth : 0f;
                var anyVisible = false;
                for (var pixel = 0; pixel < frame.Width * frame.Height; pixel++)
                {
                    if (frame.Alpha[pixel] == 0) continue;
                    int shadeByte;
                    if (generated)
                    {
                        // Maps raster pixels to normalized tile coordinates (u, v) to evaluate edge curves,
                        // writing inverted shade values scaled to 255.
                        var row = pixel / frame.Width;
                        var column = pixel - row * frame.Width;
                        var halfX = (column - generatedWidth * 0.5f) * 0.5f;
                        var halfY = row - frame.Height * 0.5f;
                        var u = Math.Clamp(0.5f + scale * (halfY + halfX), 0f, 1f);
                        var v = Math.Clamp(0.5f + scale * (halfY - halfX), 0f, 1f);
                        var shade = EvaluateFloorShadowShade(lookupTypes, u, v);
                        shadeByte = Math.Clamp((int)(Math.Clamp(1f - shade, 0f, 1f) * 255f + 0.5f), 0, 255);
                    }
                    else
                    {
                        shadeByte = frame.Indices[pixel];
                    }
                    // Applies shadow attenuation to the shade value, skipping blending when the adjusted
                    // value reaches 0xF8 or higher.
                    var adjusted = shadeByte + atten;
                    if (adjusted >= 0xf8) continue;
                    var opacity = (byte)(Math.Clamp(255 - adjusted, 0, 255) * frame.Alpha[pixel] / 31);
                    if (opacity == 0) continue;
                    buffer[pixel * 4 + 3] = opacity;
                    anyVisible = true;
                }
                return anyVisible ? (frame.Width, frame.Height, buffer) : default;
            });
            if (cachedPixels.Bytes is not { } bytes)
            {
                floorShadowTextures[key] = null;
                return null;
            }
            var attenuationTag = attenuation < 0 ? $"m{-attenuation}" : $"{attenuation}";
            var name = $"floor-shadow-{shadowId}-{attenuationTag}-{zoom}.rgba";
            files[name] = bytes;
            var texture = textures.Count;
            textures.Add(new Texture(name, cachedPixels.Width, cachedPixels.Height, "rgba"));
            floorShadowTextures[key] = texture;
            return texture;
        }
        ZoomImage? FloorShadowAt(int? shadowId, int shadowBrightness, int roomLightByte, int zoom, float x, float y, float altitude = 0)
        {
            if (shadowId is null or < 0 or > 255) return null;
            // Calculates shadow attenuation by offsetting shadow brightness by room lighting and baseline
            // intensity.
            var attenuation = Math.Clamp(shadowBrightness - roomLightByte + 0xff, -255, 0xf8);
            var textureIndex = FloorShadowTexture(shadowId.Value, attenuation, zoom);
            if (textureIndex is null) return null;
            var point = camera.Project(x, y, altitude * 40f);
            var dimensions = textures[textureIndex.Value];
            var factor = zoom switch { 1 => 0.5f, 3 => 2f, _ => 1f };
            // Centers the 63x32 medium-zoom tile diamond shadow raster on the projected tile center in
            // canvas space.
            var shadowWidth = dimensions.Width / factor;
            var shadowHeight = dimensions.Height / factor;
            return new ZoomImage(textureIndex.Value, point.X - shadowWidth / 2, point.Y - shadowHeight / 2,
                shadowWidth, shadowHeight);
        }
        var cutawayMask = animating &&
            kept.ViewLevel == viewLevel &&
            kept.WallMode == wallMode &&
            kept.CameraOrientation == cameraOrientation &&
            kept.CutawayTile == cutawayTile &&
            kept.CutawaySim == cutawaySim &&
            kept.CutawayZoom == cutawayZoom &&
            CutawayRoomsSequenceEqual(kept.CutawayRooms, cutawayRooms)
            ? kept.CutawayMask
            : ComputeCutawayMask(rooms, camera, lotSize, wallMode, cutawayTile, cutawayRooms, cutawaySim, cutawayZoom, heights, baseHeight);

        bool IsCutawayPair(int x1, int y1, int x2, int y2) =>
            cutawayMask is not null &&
            x1 >= 0 && y1 >= 0 && x1 < lotSize && y1 < lotSize &&
            x2 >= 0 && y2 >= 0 && x2 < lotSize && y2 < lotSize &&
            cutawayMask[y1 * lotSize + x1] && cutawayMask[y2 * lotSize + x2];

        bool IsWallObjectLowered(int level, int tileX, int tileY, int flags1, int flags2, int wallPlacement, int direction,
            bool unresetPortalFallback = false)
        {
            if (roofsVisible || level != viewLevel || showLoweredPortals) return false;
            // Only objects with flag 11 (bit 0x400, hide for cutaway) are hidden on lowered walls;
            // wall-mounted objects such as paintings, lamps, and phones intentionally omit this flag to
            // remain visible.
            var hideForCutaway = (flags1 & 0x400) != 0 || (unresetPortalFallback && (flags2 & 0xc000) != 0);
            if (!hideForCutaway) return false;
            if (wallMode == "down") return true;
            if (wallMode != "cutaway" || cutawayMask is null) return false;
            if (tileX < 0 || tileY < 0 || tileX >= lotSize || tileY >= lotSize) return false;
            if (!cutawayMask[tileY * lotSize + tileX]) return false;
            var maskedPlacement = wallPlacement & 5;
            if (maskedPlacement is not (1 or 4)) return true;
            var dir = (direction & 7) >> 1;
            var side = (dir + (maskedPlacement == 1 ? 0 : 2)) & 3;
            var (nx, ny) = side switch
            {
                0 => (tileX, tileY - 1),
                1 => (tileX + 1, tileY),
                2 => (tileX, tileY + 1),
                _ => (tileX - 1, tileY)
            };
            return IsCutawayPair(tileX, tileY, nx, ny);
        }

        var hasLiveObjects = false;
        foreach (var walkItem in world.WalkOrder)
        {
            if (walkItem is not XPerson) { hasLiveObjects = true; break; }
        }
        var wallObjectIds = animating ? kept.WallObjectIds : new HashSet<short>();
        var savedDoors = animating ? kept.SavedDoors : Array.Empty<SavedDoor>();
        var wallAdjacentTiles = animating ? kept.WallAdjacentTiles : new bool[lotSize * lotSize];

        void MarkCutawaySensitiveNeighborhood(int centerX, int centerY)
        {
            if (wallAdjacentTiles is null) return;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var nx = centerX + dx;
                var ny = centerY + dy;
                if ((uint)nx < (uint)lotSize && (uint)ny < (uint)lotSize)
                    wallAdjacentTiles[ny * lotSize + nx] = true;
            }
        }

        if (!animating)
        {
        var grass = ReadGrass(lot);
        var wallArtwork = TryOpenOptionalArtwork(Path.Combine(gameRoot, "GameData", "walls.iff"));
        var gameDataPath = Path.Combine(gameRoot, "GameData");
        var dynamicFloorFiles = DynamicFloorFiles(gameDataPath);
        var dynamicWallFiles = DynamicWallFiles(gameDataPath);
        var floorTextures = new Dictionary<(ushort Pattern, int Zoom), int>();
        var waterTextures = new Dictionary<(ushort Sprite, int CornerMask, int Zoom), int>();
        var wallSprites = new Dictionary<ushort, Sprite>();
        var objectWallSprites = new Dictionary<(IffFile File, ushort Id), Sprite>();
        var wallPalettes = new Dictionary<(IffFile File, int Id), PaletteColor[]>();
        var wallTextures = new Dictionary<(ushort Pattern, IffFile? File, ushort SpriteBase, ushort BaseStyle, int Frame, bool Down, int Zoom), int>();
        var wallJunctionTextures = new Dictionary<(int Zoom, int Frame, bool LoweredSheet), int>();
        var wallDepthTextures = new Dictionary<(int Configuration, int Width, int Height), int>();
        var wallStripTextures = new Dictionary<(int Texture, int Width, bool Right), int>();

        int WallStrip(int texture, int width, bool right)
        {
            var key = (texture, width, right);
            if (wallStripTextures.TryGetValue(key, out var cached)) return cached;
            var source = textures[texture];
            width = Math.Min(width, source.Width);
            var channels = source.Encoding == "depth8" ? 1 : 4;
            var bytes = new byte[width * source.Height * channels];
            var sourceBytes = files[source.File];
            var sourceX = right ? source.Width - width : 0;
            for (var row = 0; row < source.Height; row++)
                Array.Copy(sourceBytes, (row * source.Width + sourceX) * channels, bytes,
                    row * width * channels, width * channels);
            var name = $"cap-{Path.GetFileNameWithoutExtension(source.File)}-{(right ? "right" : "left")}-{width}.{source.Encoding}";
            files[name] = bytes;
            var result = textures.Count;
            textures.Add(new Texture(name, width, source.Height, source.Encoding));
            wallStripTextures[key] = result;
            return result;
        }

        // Synthesizes per-pixel depth ramps for wall faces to depth-test walls against intersecting and
        // occluded objects.
        int ArchitectureDepth(int configuration, int colorTexture)
        {
            var dimensions = textures[colorTexture];
            var key = (configuration, dimensions.Width, dimensions.Height);
            if (wallDepthTextures.TryGetValue(key, out var cached)) return cached;
            if (archDepthBytesCache.Count > 512) archDepthBytesCache.Clear();
            var depths = archDepthBytesCache.GetOrAdd(key, static k =>
            {
                var (cfg, w, h) = k;
                var config = WallDepthConfig[cfg];
                var buffer = new byte[w * h];
                for (var row = 0; row < h; row++)
                for (var column = 0; column < w; column++)
                {
                    var sourceX = Math.Min((int)config[0] - 1, column * (int)config[0] / w);
                    var sourceY = Math.Min((int)config[1] - 1, row * (int)config[1] / h);
                    buffer[row * w + column] =
                        (byte)Math.Clamp(Math.Round(config[2] + config[3] * sourceX + config[4] * sourceY), 0, 254);
                }
                return buffer;
            });
            var name = $"wall-depth-{configuration}-{dimensions.Width}x{dimensions.Height}.depth8";
            files[name] = depths;
            var result = textures.Count;
            textures.Add(new Texture(name, dimensions.Width, dimensions.Height, "depth8"));
            wallDepthTextures[key] = result;
            return result;
        }
        PaletteColor[] Colors(IffFile sourceFile, Sprite sprite)
        {
            var paletteId = WallPaletteId(sprite.PaletteId);
            if (wallPalettes.TryGetValue((sourceFile, paletteId), out var colors)) return colors;
            var palette = sourceFile.Find(ResourceType.Palt, (ushort)paletteId)
                ?? sourceFile.OfType(ResourceType.Palt).FirstOrDefault();
            if (palette is null) return wallPalettes[(sourceFile, paletteId)] = [];
            return wallPalettes[(sourceFile, paletteId)] = Palette.Read(sourceFile, palette).Colors.ToArray();
        }
        // Wall junction caps use walls.iff SPR# resources (4096..4098 upper, 5120..5122 lowered),
        // indexing individual frames by junction bitmask.
        int JunctionTexture(int zoom, int frame, bool loweredSheet = false)
        {
            if (wallArtwork is null) { missingResources.Add("GameData/walls.iff"); return -1; }
            var key = (zoom, frame, loweredSheet);
            if (wallJunctionTextures.TryGetValue(key, out var cached)) return cached;
            var upperId = (ushort)(zoom switch { 0 => 4096, 2 => 4098, _ => 4097 });
            var loweredId = (ushort)(zoom switch { 0 => 5120, 2 => 5122, _ => 5121 });
            var chunk = loweredSheet ? wallArtwork.Find(ResourceType.Spr1, loweredId) : null;
            var artworkId = chunk is not null ? loweredId : upperId;
            chunk ??= wallArtwork.Find(ResourceType.Spr1, upperId);
            if (chunk is null) { missingResources.Add($"wall-junction:{artworkId}"); return -1; }
            if (!wallSprites.TryGetValue(chunk.Id, out var sprite))
                wallSprites[chunk.Id] = sprite = Sprite.Read(wallArtwork, chunk);
            if (frame < 0 || frame >= sprite.Frames.Count)
            {
                missingResources.Add($"wall-junction:{artworkId}:frame:{frame}");
                return -1;
            }
            var art = sprite.Frames[frame];
            var colors = Colors(wallArtwork, sprite);
            if (colors.Length == 0) { missingResources.Add($"wall-junction-palette:{artworkId}"); return -1; }
            var bytes = new byte[art.Width * art.Height * 4];
            for (var pixel = 0; pixel < art.Indices.Length; pixel++)
            {
                if (!art.Opaque[pixel]) continue;
                var color = colors[art.Indices[pixel]];
                var at = pixel * 4;
                bytes[at] = color.Red;
                bytes[at + 1] = color.Green;
                bytes[at + 2] = color.Blue;
                bytes[at + 3] = 255;
            }
            var name = $"wall-junction-{(loweredSheet ? "down-" : "")}{zoom}-{frame}.rgba";
            files[name] = bytes;
            var result = textures.Count;
            textures.Add(new Texture(name, art.Width, art.Height, "rgba"));
            wallJunctionTextures[key] = result;
            return result;
        }
        var terrainType = TerrainType(house);
        // Applies terrain directional lighting using a fixed sun latitude of 0.314 radians, camera-aligned
        // sun longitude, and a jitter envelope of 3.
        const float sunLatitude = 0.31415927f;
        var sunLongitude = (camera.Orientation & 3) switch
        {
            0 => MathF.PI * 0.25f,
            2 => MathF.PI * 0.75f,
            _ => MathF.PI * 1.25f,
        };
        var sunZ = MathF.Cos(sunLatitude);
        var sunX = MathF.Sin(sunLongitude) * MathF.Sin(sunLatitude);
        var sunY = -MathF.Cos(sunLongitude) * MathF.Sin(sunLatitude);
        float SlopeDotSq(int dx, int dy)
        {
            dx = Math.Clamp(dx, -5, 5);
            dy = Math.Clamp(dy, -5, 5);
            var dot = (2.82842712f * sunZ + dx * sunX + dy * sunY) / MathF.Sqrt(8f + dx * dx + dy * dy);
            return dot * dot;
        }
        int RawHeightStep(int tx, int ty) => (int)MathF.Round(HeightAt(tx, ty) / 10f);
        float TileTri0(int tx, int ty)
        {
            var h00 = RawHeightStep(tx, ty);
            var h01 = RawHeightStep(tx, ty + 1);
            var h10 = RawHeightStep(tx + 1, ty);
            return SlopeDotSq(h00 - h10, h01 - h00);
        }
        float TileTri1(int tx, int ty)
        {
            var h01 = RawHeightStep(tx, ty + 1);
            var h11 = RawHeightStep(tx + 1, ty + 1);
            var h10 = RawHeightStep(tx + 1, ty);
            return SlopeDotSq(h01 - h11, h10 - h11);
        }
        int GrassCell(int tx, int ty) =>
            grass[Math.Clamp(tx, 1, lotSize - 1) * Size + Math.Clamp(ty, 1, lotSize - 1)];
        var terrainPoint = new Func<float, float, TerrainVertex>((x, y) =>
        {
            var cellX = Math.Clamp((int)x, 1, lotSize - 1);
            var cellY = Math.Clamp((int)y, 1, lotSize - 1);
            // Averages grass wear across four tiles sharing each terrain vertex, while forcing the outer
            // two-tile border to sky blue on cloud lots.
            var grassState = terrainType == 7 && (x <= 2 || y <= 2 || x >= lotSize - 2 || y >= lotSize - 2)
                ? (byte)255
                : (byte)((GrassCell(cellX, cellY) + GrassCell(cellX - 1, cellY)
                    + GrassCell(cellX - 1, cellY - 1) + GrassCell(cellX, cellY - 1)) / 4);
            var blend = grassState / 255f;
            var light = TerrainColors.LightGreen[terrainType];
            var lightBrown = TerrainColors.LightBrown[terrainType];
            var lightColor = Microsoft.Xna.Framework.Color.Lerp(light, lightBrown, blend);
            var darkGreen = TerrainColors.DarkGreen[terrainType];
            var darkBrown = TerrainColors.DarkBrown[terrainType];
            var darkColor = Microsoft.Xna.Framework.Color.Lerp(darkGreen, darkBrown, blend);
            // Averages the normals of the six terrain triangles meeting at each vertex, scales the slope
            // lighting into [0, 127], and adds deterministic jitter.
            var avgDotSq = (TileTri0(cellX - 1, cellY - 1) + TileTri1(cellX - 1, cellY - 1)
                + TileTri0(cellX, cellY - 1) + TileTri1(cellX - 1, cellY)
                + TileTri0(cellX, cellY) + TileTri1(cellX, cellY)) / 6f;
            var hash = unchecked((uint)(cellX * 0x40de9 + cellY * 0x10dcd) ^ 0x40de9u);
            hash ^= hash >> 16;
            hash *= 0x7feb352du;
            hash ^= hash >> 15;
            var jitter = (int)Math.Min(hash % 3u, (hash >> 8) % 3u);
            if ((hash & 0x10000u) != 0) jitter = -jitter;
            var vertexShade = Math.Clamp((int)((1f - avgDotSq) * 127f) + jitter, 0, 127);
            lightColor = Microsoft.Xna.Framework.Color.Lerp(lightColor, darkColor, vertexShade / 128f);
            var point = camera.Project(x, y, Elevation(x, y));
            return new TerrainVertex(point.X, point.Y, lightColor.R, lightColor.G, lightColor.B,
                darkColor.R, darkColor.G, darkColor.B, grassState);
        });
        Lap("terrain");
        for (var y = 0; y < lotSize; y++)
        for (var x = 0; x < lotSize; x++)
        {
            var top = terrainPoint(x, y);
            var right = terrainPoint(x + 1, y);
            var bottom = terrainPoint(x + 1, y + 1);
            var left = terrainPoint(x, y + 1);
            terrain.AddRange([top, right, bottom, bottom, left, top]);
        }

        // Retains lower-story floors when viewing upper stories so surfaces remain visible through floor
        // openings and provide correct depth underneath objects.
        Lap("preFloors");
        for (var floorLevel = 1; floorLevel <= viewLevel; floorLevel++)
        {
            var floors = ReadFloors(lot, floorLevel);
            var floorWalls = ReadWalls(lot, floorLevel);
            var floorRooms = world.LotRoomMaps.TryGetValue(floorLevel, out var floorRoomMap) ? floorRoomMap : null;
            var levelOffset = (floorLevel - 1) * EntityTransform.StoryHeight * 40;
            Vertex Corner(float cornerX, float cornerY, float u, float v, ushort pattern)
            {
                var elevation = Elevation(cornerX, cornerY) + levelOffset;
                var point = camera.Project(cornerX, cornerY, elevation);
                var depth = floorLevel == 1 && pattern < 65534 ? 1 : camera.Depth(cornerX, cornerY, elevation / 40);
                var rotatedUv = camera.TexturePoint(u, v);
                if (pattern is >= 9 and <= 11)
                    rotatedUv = camera.Orientation % 2 == 0 ? new(u, v) : new(1 - v, 1 - u);
                else if (pattern >= 65534)
                    rotatedUv = camera.Orientation switch
                    {
                        0 => new(u, v),
                        1 => new(1 - v, u),
                        2 => new(1 - u, 1 - v),
                        _ => new(v, 1 - u)
                    };
                return new Vertex(point.X, point.Y, depth, rotatedUv.X, rotatedUv.Y, 0, 1, 0);
            }
            void EmitFloor(int tileX, int tileY, ushort pattern, int half, (int Dx, int Dy, float U, float V)[] corners, int? room)
            {
                if (!HasFloorArtwork(pattern)) return;
                int texture;
                int nearTexture;
                int farTexture;
                WaterSurface? water = null;
                if (pattern is 65534 or 65535)
                {
                    ReadOnlySpan<(int Dx, int Dy)> neighbourOffsets =
                        [(0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1)];
                    var adjacency = 0;
                    for (var i = 0; i < neighbourOffsets.Length; i++)
                    {
                        var offset = camera.SourceVector(neighbourOffsets[i].Dx, neighbourOffsets[i].Dy);
                        var nx = tileX + (int)MathF.Round(offset.X);
                        var ny = tileY + (int)MathF.Round(offset.Y);
                        if (nx >= 0 && nx < Size && ny >= 0 && ny < Size && floors[nx * Size + ny] == pattern)
                            adjacency |= 1 << i;
                    }
                    var variant = ((adjacency & 1) != 0 ? 1 : 0) |
                                  ((adjacency & 64) != 0 ? 2 : 0) |
                                  ((adjacency & 16) != 0 ? 4 : 0) |
                                  ((adjacency & 4) != 0 ? 8 : 0);
                    var sprite = (ushort)((pattern == 65535 ? 0x410 : 0x800) + variant);
                    ushort[] cornerSprites = [];
                    var cornerMask = 0;
                    if (pattern == 65535)
                    {
                        ReadOnlySpan<int> pairs = [65, 80, 20, 5];
                        ReadOnlySpan<int> diagonals = [128, 32, 8, 2];
                        var list = new List<ushort>(4);
                        for (var i = 0; i < 4; i++)
                        {
                            if ((adjacency & pairs[i]) == pairs[i] && (adjacency & diagonals[i]) == 0)
                            {
                                list.Add((ushort)(0x434 + i));
                                cornerMask |= 1 << i;
                            }
                        }
                        cornerSprites = [.. list];
                    }
                    water = new WaterSurface(adjacency, sprite, cornerSprites);
                    texture = WaterTexture(pattern, variant, cornerMask, 1);
                    if (texture < 0) return;
                    nearTexture = WaterTexture(pattern, variant, cornerMask, 2);
                    farTexture = WaterTexture(pattern, variant, cornerMask, 0);
                }
                else
                {
                    texture = FloorTexture(pattern, 1);
                    if (texture < 0) return;
                    nearTexture = FloorTexture(pattern, 2);
                    farTexture = FloorTexture(pattern, 0);
                }
                var vertices = corners.Select(c => Corner(tileX + c.Dx, tileY + c.Dy, c.U, c.V, pattern)).ToArray();
                var depth = camera.DepthOrder(tileX + 0.5f, tileY + 0.5f);
                meshes.Add(new Mesh(texture, 0, depth, vertices, floorLevel, room, Unlit: true,
                    Floor: new FloorSurface(tileX, tileY, pattern, half, floorLevel, water),
                    NearTexture: nearTexture >= 0 ? nearTexture : null,
                    FarTexture: farTexture >= 0 ? farTexture : null,
                    Layer: 0));
                foreach (var vertex in vertices) meshVertices.Add((vertex, tileX, tileY));
            }
            for (var y = 0; y < lotSize; y++)
            for (var x = 0; x < lotSize; x++)
            {
                var c0 = (Dx: 0, Dy: 0, U: 0.5f, V: 0f);
                var c1 = (Dx: 1, Dy: 0, U: 1f, V: 0.5f);
                var c2 = (Dx: 1, Dy: 1, U: 0.5f, V: 1f);
                var c3 = (Dx: 0, Dy: 1, U: 0f, V: 0.5f);
                var wallDiag = floorWalls is not null && x < floorWalls.Rows && y < floorWalls.Width
                    ? floorWalls.Element(x, y)[0] & 0x30
                    : 0;
                if (wallDiag == 0x20)
                {
                    var wallTile = floorWalls!.Element(x, y);
                    // Falls back to the underlying tile floor pattern when both diagonal slots are zero,
                    // unless marked by the 0xFF diagonal sentinel.
                    var basePattern = floors[x * Size + y] == byte.MaxValue ? (ushort)0 : floors[x * Size + y];
                    var pat1 = wallTile[2] == 0 && wallTile[4] == 0 ? basePattern : wallTile[4];
                    var pat2 = wallTile[2] == 0 && wallTile[4] == 0 ? basePattern : wallTile[2];
                    EmitFloor(x, y, pat1, 1, [c0, c1, c2], floorRooms?.RoomAtPosition(x * 16 + 12, y * 16 + 4));
                    EmitFloor(x, y, pat2, 2, [c2, c3, c0], floorRooms?.RoomAtPosition(x * 16 + 4, y * 16 + 12));
                }
                else if (wallDiag == 0x10)
                {
                    var wallTile = floorWalls!.Element(x, y);
                    var basePattern = floors[x * Size + y] == byte.MaxValue ? (ushort)0 : floors[x * Size + y];
                    var pat3 = wallTile[2] == 0 && wallTile[4] == 0 ? basePattern : wallTile[2];
                    var pat4 = wallTile[2] == 0 && wallTile[4] == 0 ? basePattern : wallTile[4];
                    EmitFloor(x, y, pat3, 3, [c0, c1, c3], floorRooms?.RoomAtPosition(x * 16 + 4, y * 16 + 4));
                    EmitFloor(x, y, pat4, 4, [c1, c2, c3], floorRooms?.RoomAtPosition(x * 16 + 12, y * 16 + 12));
                }
                else
                {
                    var pattern = floors[x * Size + y];
                    // Floor pattern value 0xFF acts as a transparent architecture sentinel leaving
                    // underlying terrain visible rather than indexing a floor pattern.
                    EmitFloor(x, y, pattern, 0, [c0, c1, c2, c2, c3, c0], floorRooms?.RoomAt(x, y));
                }
            }
        }

        int FloorTexture(ushort pattern, int zoom = 1)
        {
            var key = (pattern, zoom);
            if (floorTextures.TryGetValue(key, out var cached)) return cached;
            // Floor files store local SPR2 artwork with far, medium, and near zoom levels offset by 0, 256,
            // and 512 from the base sprite ID.
            var art = floorArtwork;
            var spriteId = unchecked((ushort)(pattern + zoom * 256));
            if (pattern >= 256)
            {
                if (!dynamicFloorFiles.TryGetValue(pattern, out var path))
                {
                    if (zoom == 1) missingResources.Add($"floor:{pattern}:resource");
                    return -1;
                }
                art = OpenArtwork(path);
                spriteId = (ushort)(1 + zoom * 256);
            }
            if (art is null) { if (zoom == 1) missingResources.Add("GameData/floors.iff"); return -1; }
            var chunk = art.Find(ResourceType.Spr2, spriteId);
            if (chunk is null)
            {
                if (zoom == 1) missingResources.Add($"floor:{pattern}");
                return -1;
            }
            var sprite = DepthSprite.Read(art, chunk);
            if (sprite.Frames.Count == 0)
            {
                if (zoom == 1) missingResources.Add($"floor:{pattern}:frame:0");
                return -1;
            }
            var frame = sprite.Frames[0];
            if (frame.Width <= 0 || frame.Height <= 0 || frame.Indices.Length != frame.Width * frame.Height ||
                frame.Alpha.Length != frame.Indices.Length)
            {
                if (zoom == 1) missingResources.Add($"floor:{pattern}:frame:0");
                return -1;
            }
            // Selects the palette defined in the SPR2 chunk header, ignoring individual frame palette
            // fields.
            var paletteId = sprite.PaletteId;
            var colors = PaletteFor(art, paletteId);
            if (colors is null)
            {
                if (zoom == 1) missingResources.Add($"floor-palette:{pattern}");
                return -1;
            }
            var bytes = new byte[frame.Width * frame.Height * 4];
            for (var pixel = 0; pixel < frame.Indices.Length; pixel++)
            {
                if (frame.Alpha[pixel] == 0) continue;
                if (frame.Indices[pixel] >= colors.Length)
                {
                    if (zoom == 1) missingResources.Add($"floor-palette:{pattern}");
                    return -1;
                }
                var color = colors[frame.Indices[pixel]];
                var at = pixel * 4;
                bytes[at] = color.Red;
                bytes[at + 1] = color.Green;
                bytes[at + 2] = color.Blue;
                bytes[at + 3] = (byte)Math.Clamp((frame.Alpha[pixel] * 255 + 15) / 31, 0, 255);
                // Tints bright pixels in road floor patterns yellow to render yellow road dividing lines
                // from white texture artwork.
                if (pattern is 10 or 11 && Math.Min(color.Red, Math.Min(color.Green, color.Blue)) > 64)
                {
                    bytes[at + 1] = (byte)(color.Green * 0.8f);
                    bytes[at + 2] = (byte)(color.Blue * 0.12f);
                }
            }
            // Bleeds edge texels into transparent border pixels of floor diamonds to prevent texture
            // filtering seams along tile edges.
            var firstOpaqueRow = -1;
            var lastOpaqueRow = -1;
            for (var py = 0; py < frame.Height; py++)
            {
                var rowStart = py * frame.Width;
                var firstX = -1;
                var lastX = -1;
                for (var px = 0; px < frame.Width; px++)
                {
                    if (bytes[(rowStart + px) * 4 + 3] == 0) continue;
                    if (firstX < 0) firstX = px;
                    lastX = px;
                }
                if (firstX < 0) continue;
                if (firstOpaqueRow < 0) firstOpaqueRow = py;
                lastOpaqueRow = py;
                var leftAt = (rowStart + firstX) * 4;
                for (var px = 0; px < firstX; px++)
                {
                    var dst = (rowStart + px) * 4;
                    bytes[dst] = bytes[leftAt];
                    bytes[dst + 1] = bytes[leftAt + 1];
                    bytes[dst + 2] = bytes[leftAt + 2];
                    bytes[dst + 3] = 255;
                }
                var rightAt = (rowStart + lastX) * 4;
                for (var px = lastX + 1; px < frame.Width; px++)
                {
                    var dst = (rowStart + px) * 4;
                    bytes[dst] = bytes[rightAt];
                    bytes[dst + 1] = bytes[rightAt + 1];
                    bytes[dst + 2] = bytes[rightAt + 2];
                    bytes[dst + 3] = 255;
                }
            }
            if (firstOpaqueRow > 0)
            {
                for (var py = 0; py < firstOpaqueRow; py++)
                    Array.Copy(bytes, firstOpaqueRow * frame.Width * 4, bytes, py * frame.Width * 4, frame.Width * 4);
            }
            if (lastOpaqueRow >= 0 && lastOpaqueRow < frame.Height - 1)
            {
                for (var py = lastOpaqueRow + 1; py < frame.Height; py++)
                    Array.Copy(bytes, lastOpaqueRow * frame.Width * 4, bytes, py * frame.Width * 4, frame.Width * 4);
            }
            var name = zoom == 1 ? $"floor-{pattern}.rgba" : $"floor-{pattern}-z{zoom}.rgba";
            files[name] = bytes;
            var result = textures.Count;
            textures.Add(new Texture(name, frame.Width, frame.Height, "rgba"));
            floorTextures.Add(key, result);
            return result;
        }

        int WaterTexture(ushort pattern, int variant, int cornerMask, int zoom = 1)
        {
            var mediumSprite = (ushort)((pattern == 65535 ? 0x410 : 0x800) + variant);
            var key = (mediumSprite, cornerMask, zoom);
            if (waterTextures.TryGetValue(key, out var cached)) return cached;
            var art = floorArtwork;
            if (art is null) { if (zoom == 1) missingResources.Add("GameData/floors.iff"); return -1; }
            var spriteId = pattern == 65535
                ? (ushort)(0x400 + zoom * 0x10 + variant)
                : (ushort)(0x800 + variant);
            var frameIndex = pattern == 65535 ? 0 : 2 - zoom;

            (int Width, int Height, int OffsetX, int OffsetY, byte[] Rgba)? ReadWaterSprite(ushort id, int frameIdx)
            {
                var spr2Chunk = art.Find(ResourceType.Spr2, id);
                if (spr2Chunk is not null)
                {
                    var spr2 = DepthSprite.Read(art, spr2Chunk);
                    if (frameIdx < 0 || frameIdx >= spr2.Frames.Count) return null;
                    var f = spr2.Frames[frameIdx];
                    if (f.Width <= 0 || f.Height <= 0 || f.Indices.Length != f.Width * f.Height || f.Alpha.Length != f.Indices.Length)
                        return null;
                    var colors = PaletteFor(art, spr2.PaletteId);
                    if (colors is null) return null;
                    var rgba = new byte[f.Width * f.Height * 4];
                    for (var p = 0; p < f.Indices.Length; p++)
                    {
                        if (f.Alpha[p] == 0 || f.Indices[p] >= colors.Length) continue;
                        var c = colors[f.Indices[p]];
                        var at = p * 4;
                        rgba[at] = c.Red;
                        rgba[at + 1] = c.Green;
                        rgba[at + 2] = c.Blue;
                        rgba[at + 3] = (byte)Math.Clamp((f.Alpha[p] * 255 + 15) / 31, 0, 255);
                    }
                    return (f.Width, f.Height, f.X, f.Y, rgba);
                }
                var spr1Chunk = art.Find(ResourceType.Spr1, id);
                if (spr1Chunk is not null)
                {
                    var spr1 = Sprite.Read(art, spr1Chunk);
                    if (frameIdx < 0 || frameIdx >= spr1.Frames.Count) return null;
                    var f = spr1.Frames[frameIdx];
                    if (f.Width <= 0 || f.Height <= 0 || f.Indices.Length != f.Width * f.Height || f.Opaque.Length != f.Indices.Length)
                        return null;
                    var colors = PaletteFor(art, spr1.PaletteId);
                    if (colors is null) return null;
                    var rgba = new byte[f.Width * f.Height * 4];
                    for (var p = 0; p < f.Indices.Length; p++)
                    {
                        if (!f.Opaque[p] || f.Indices[p] >= colors.Length) continue;
                        var c = colors[f.Indices[p]];
                        var at = p * 4;
                        rgba[at] = c.Red;
                        rgba[at + 1] = c.Green;
                        rgba[at + 2] = c.Blue;
                        rgba[at + 3] = 255;
                    }
                    return (f.Width, f.Height, f.BoundsLeft, f.BoundsTop, rgba);
                }
                return null;
            }

            var baseArt = ReadWaterSprite(spriteId, frameIndex);
            if (baseArt is not { } loaded)
            {
                if (zoom == 1) missingResources.Add($"water:{spriteId}:frame:{frameIndex}");
                return -1;
            }
            var bytes = loaded.Rgba;
            if (pattern == 65535 && cornerMask != 0)
            {
                for (var cornerIndex = 0; cornerIndex < 4; cornerIndex++)
                {
                    if ((cornerMask & (1 << cornerIndex)) == 0) continue;
                    var cornerSpriteId = (ushort)(0x430 + zoom * 4 + cornerIndex);
                    if (ReadWaterSprite(cornerSpriteId, 0) is not { } cornerArt) continue;
                    var dx = cornerArt.OffsetX - loaded.OffsetX;
                    var dy = cornerArt.OffsetY - loaded.OffsetY;
                    for (var cy = 0; cy < cornerArt.Height; cy++)
                    for (var cx = 0; cx < cornerArt.Width; cx++)
                    {
                        var srcAt = (cy * cornerArt.Width + cx) * 4;
                        var alpha = cornerArt.Rgba[srcAt + 3];
                        if (alpha == 0) continue;
                        var bx = cx + dx;
                        var by = cy + dy;
                        if (bx < 0 || bx >= loaded.Width || by < 0 || by >= loaded.Height) continue;
                        var dstAt = (by * loaded.Width + bx) * 4;
                        bytes[dstAt] = (byte)((cornerArt.Rgba[srcAt] * alpha + bytes[dstAt] * (255 - alpha) + 127) / 255);
                        bytes[dstAt + 1] = (byte)((cornerArt.Rgba[srcAt + 1] * alpha + bytes[dstAt + 1] * (255 - alpha) + 127) / 255);
                        bytes[dstAt + 2] = (byte)((cornerArt.Rgba[srcAt + 2] * alpha + bytes[dstAt + 2] * (255 - alpha) + 127) / 255);
                        bytes[dstAt + 3] = (byte)Math.Max(bytes[dstAt + 3], alpha);
                    }
                }
            }
            var firstOpaqueRow = -1;
            var lastOpaqueRow = -1;
            for (var py = 0; py < loaded.Height; py++)
            {
                var rowStart = py * loaded.Width;
                var firstX = -1;
                var lastX = -1;
                for (var px = 0; px < loaded.Width; px++)
                {
                    if (bytes[(rowStart + px) * 4 + 3] == 0) continue;
                    if (firstX < 0) firstX = px;
                    lastX = px;
                }
                if (firstX < 0) continue;
                if (firstOpaqueRow < 0) firstOpaqueRow = py;
                lastOpaqueRow = py;
                var leftAt = (rowStart + firstX) * 4;
                for (var px = 0; px < firstX; px++)
                {
                    var dst = (rowStart + px) * 4;
                    bytes[dst] = bytes[leftAt];
                    bytes[dst + 1] = bytes[leftAt + 1];
                    bytes[dst + 2] = bytes[leftAt + 2];
                    bytes[dst + 3] = 255;
                }
                var rightAt = (rowStart + lastX) * 4;
                for (var px = lastX + 1; px < loaded.Width; px++)
                {
                    var dst = (rowStart + px) * 4;
                    bytes[dst] = bytes[rightAt];
                    bytes[dst + 1] = bytes[rightAt + 1];
                    bytes[dst + 2] = bytes[rightAt + 2];
                    bytes[dst + 3] = 255;
                }
            }
            if (firstOpaqueRow > 0)
            {
                for (var py = 0; py < firstOpaqueRow; py++)
                    Array.Copy(bytes, firstOpaqueRow * loaded.Width * 4, bytes, py * loaded.Width * 4, loaded.Width * 4);
            }
            if (lastOpaqueRow >= 0 && lastOpaqueRow < loaded.Height - 1)
            {
                for (var py = lastOpaqueRow + 1; py < loaded.Height; py++)
                    Array.Copy(bytes, lastOpaqueRow * loaded.Width * 4, bytes, py * loaded.Width * 4, loaded.Width * 4);
            }
            var suffix = cornerMask == 0 ? "" : $"-c{cornerMask:x}";
            var waterName = zoom == 1
                ? $"water-{mediumSprite:x4}{suffix}.rgba"
                : $"water-{mediumSprite:x4}{suffix}-z{zoom}.rgba";
            files[waterName] = bytes;
            var waterResult = textures.Count;
            textures.Add(new Texture(waterName, loaded.Width, loaded.Height, "rgba"));
            waterTextures.Add(key, waterResult);
            return waterResult;
        }

        var typeTable = ObjectTypeTable.Read(lot, lot.OfType(ResourceType.Parse("objt")).First());
        var savedDoorsList = new List<SavedDoor>();
        Lap("floors");
        if (!hasLiveObjects && world.SavedLotObjects is { } savedObjects)
        foreach (var saved in savedObjects.Objects)
        {
            if (saved.Decoded is not { } state) continue;
            var type = typeTable.Entries.FirstOrDefault(entry => entry.TypeIndex == saved.TypeIndex);
            if (type is null) continue;
            var entry = registry.Find(type.Guid);
            if (entry is null) continue;
            if (entry.Definition[ObjdField.Type] is not (8 or 9)) continue;
            if ((state.Value0x110 & 0xff) < 1 || (state.Value0x110 & 0xff) > viewLevel) continue;
            // Converts stored world coordinates from sixteenths of a tile into floating-point tile units.
            var tileX = state.Values0x108[4] / 16f;
            var tileY = state.Values0x108[5] / 16f;
            if (world.ObjectById(saved.ObjectId) is not null) continue;
            savedDoorsList.Add(new SavedDoor(entry, type.Guid, saved.ObjectId, tileX, tileY, state.Values0x108[4] >> 4, state.Values0x108[5] >> 4,
                state.Value0x110 & 0xff, state.Data[(int)ObjectData.Direction],
                unchecked((ushort)state.Data[(int)ObjectData.Flags]), unchecked((ushort)state.Data[(int)ObjectData.FlagField2]),
                state.Data[(int)ObjectData.WallPlacementFlags], state.Data[(int)ObjectData.Graphic], state.Values0x34));
        }
        savedDoors = savedDoorsList.Count == 0 ? Array.Empty<SavedDoor>() : savedDoorsList.ToArray();
        var objectStyles = new Dictionary<(int Level, int X, int Y, int Side), WallStyleArt>();
        var wallObjectKinds = new Dictionary<(int Level, int X, int Y, int Side), int>();
        void AddObjectStyle(int level, int tileX, int tileY, int flags2, int wallPlacement, int direction, int wallStyle, IffFile file, Objd definition)
        {
            if ((flags2 & 0xc000) == 0 || wallStyle == 0) return;
            var kind = (flags2 & 0x8000) != 0 ? 1 : (flags2 & 0x4000) != 0 ? 2 : 0;
            WallStyleArt art;
            if (wallStyle is > 21 and < 256)
            {
                // Door and window objects define six wall mask SPR# resources covering up and down states
                // across three zoom levels, mapped to wall styles at 256 and above.
                if (definition.Fields.Count <= 81) return;
                var spriteBase = definition.Fields[81];
                if (spriteBase == 0) return;
                art = new WallStyleArt(file, spriteBase, 0);
            }
            else art = WallStyleArt.Base((ushort)wallStyle);
            var dir = (direction & 7) >> 1;
            void Apply(int side)
            {
                side &= 3;
                // Wall style placement uses only top-right (side 0) and top-left (side 3) slots, allowing
                // multi-tile wall objects to anchor to a single wall face.
                if (side == 0)
                {
                    objectStyles[(level, tileX, tileY, 1)] = art;
                    if (kind != 0) wallObjectKinds[(level, tileX, tileY, 1)] = kind;
                }
                else if (side == 3)
                {
                    objectStyles[(level, tileX, tileY, 0)] = art;
                    if (kind != 0) wallObjectKinds[(level, tileX, tileY, 0)] = kind;
                }
            }
            if ((wallPlacement & 1) != 0) Apply(dir);
            if ((wallPlacement & 2) != 0) Apply(dir + 1);
            if ((wallPlacement & 4) != 0) Apply(dir + 2);
            if ((wallPlacement & 8) != 0) Apply(dir + 3);
        }
        Lap("savedObjects");
        foreach (var item in world.WalkOrder)
        {
            if (item is XPerson || item.Position.Height < 1 || item.Position.Height > viewLevel) continue;
            var itemFlags1 = unchecked((ushort)item[ObjectData.Flags]);
            var itemFlags = unchecked((ushort)item[ObjectData.FlagField2]);
            var itemStyle = item.Definition[(int)ObjdField.WallStyle];
            var itemGuid = (uint)(item.TypeRecord?.Key ?? 0);
            var itemEntry = registry.Find(itemGuid);
            var itemTileX = item.Position.X >> 4;
            var itemTileY = item.Position.Y >> 4;
            var itemWallPlacement = item[ObjectData.WallPlacementFlags];
            var itemDirection = item[ObjectData.Direction];
            if (itemEntry is not null)
                AddObjectStyle(item.Position.Height, itemTileX, itemTileY, itemFlags,
                    itemWallPlacement, itemDirection, itemStyle,
                    itemEntry.File, itemEntry.Definition);
            if (IsWallObjectLowered(item.Position.Height, itemTileX, itemTileY, itemFlags1, itemFlags, itemWallPlacement, itemDirection,
                unresetPortalFallback: world.ChangedCodeTypeGuids.Contains(itemGuid)))
                wallObjectIds.Add(item.Id);
        }
        if (!hasLiveObjects && world.SavedLotObjects is { } doorStates)
            foreach (var saved in doorStates.Objects)
            {
                if (saved.Decoded is not { } state || (state.Value0x110 & 0xff) < 1 || (state.Value0x110 & 0xff) > viewLevel) continue;
                if (world.ObjectById(saved.ObjectId) is not null) continue;
                var type = typeTable.Entries.FirstOrDefault(candidate => candidate.TypeIndex == saved.TypeIndex);
                var entry = type is null ? null : registry.Find(type.Guid);
                if (entry is null) continue;
                var savedLevel = state.Value0x110 & 0xff;
                var savedTileX = state.Values0x108[4] >> 4;
                var savedTileY = state.Values0x108[5] >> 4;
                if (savedLevel == viewLevel &&
                    ((state.Data[(int)ObjectData.Flags] & 0x400) != 0 || (state.Data[(int)ObjectData.FlagField2] & 0xc000) != 0))
                {
                    MarkCutawaySensitiveNeighborhood(savedTileX, savedTileY);
                }
                AddObjectStyle(savedLevel, savedTileX, savedTileY, state.Data[(int)ObjectData.FlagField2],
                    state.Data[(int)ObjectData.WallPlacementFlags], state.Data[(int)ObjectData.Direction],
                    entry.Definition[ObjdField.WallStyle], entry.File, entry.Definition);
            }
        Lap("walkorder");
        for (var wallLevel = 1; wallLevel <= viewLevel; wallLevel++)
        {
        var walls = ReadWalls(lot, wallLevel);
        var wallRooms = world.LotRoomMaps.TryGetValue(wallLevel, out var wallRoomMap) ? wallRoomMap : null;
        // Derives both standing and lowered wall corner junctions from thick segments and diagonals,
        // excluding lowered doorway openings so thresholds do not receive plaster strips.
        var upJunctions = new int[Size * Size];
        var downJunctions = new int[Size * Size];
        var levelForcedDown = wallMode == "down" && wallLevel == viewLevel;
        var levelCutaway = wallMode == "cutaway" && wallLevel == viewLevel;
        bool IsDoorEdge(int ownerX, int ownerY, int side, byte storedStyle) =>
            wallObjectKinds.GetValueOrDefault((wallLevel, ownerX, ownerY, side)) == 1 ||
            (!hasLiveObjects && storedStyle is 3 or 5 or 6 or 15 or 23);
        if (walls is not null)
        Lap("wallsLevel");
        for (var y = 0; y < lotSize; y++)
        for (var x = 0; x < lotSize; x++)
        {
            if (walls is null || x >= walls.Rows || y >= walls.Width) continue;
            var tile = walls.Element(x, y);
            var segments = tile[0];
            if (wallLevel == viewLevel && segments != 0)
                MarkCutawaySensitiveNeighborhood(x, y);
            if (segments == 0) continue;

            if ((segments & 1) != 0 && NormalizedWallStyle(tile[2]) == 1)
            {
                var segDown = levelForcedDown || (levelCutaway && IsCutawayPair(x, y, x - 1, y));
                if (!segDown || !IsDoorEdge(x, y, 0, tile[2]))
                {
                    var targetJunctions = segDown ? downJunctions : upJunctions;
                    targetJunctions[y * Size + x] |= 4;
                    if (y + 1 < lotSize) targetJunctions[(y + 1) * Size + x] |= 1;
                }
            }
            if ((segments & 2) != 0 && NormalizedWallStyle(tile[3]) == 1)
            {
                var segDown = levelForcedDown || (levelCutaway && IsCutawayPair(x, y, x, y - 1));
                if (!segDown || !IsDoorEdge(x, y, 1, tile[3]))
                {
                    var targetJunctions = segDown ? downJunctions : upJunctions;
                    targetJunctions[y * Size + x] |= 8;
                    if (x + 1 < lotSize) targetJunctions[y * Size + x + 1] |= 2;
                }
            }

            var diag = segments & 0x30;
            if (diag is 0x20 or 0x10 && tile[3] is not (2 or 12 or 13 or 14))
            {
                var diagDown = levelForcedDown || (levelCutaway && cutawayMask is not null && cutawayMask[y * lotSize + x]);
                if (!diagDown || !IsDoorEdge(x, y, 1, tile[3]))
                {
                    var targetJunctions = diagDown ? downJunctions : upJunctions;
                    if (diag == 0x20)
                    {
                        targetJunctions[y * Size + x] |= 64;
                        if (x + 1 < lotSize && y + 1 < lotSize) targetJunctions[(y + 1) * Size + x + 1] |= 16;
                    }
                    else
                    {
                        if (x + 1 < lotSize) targetJunctions[y * Size + x + 1] |= 32;
                        if (y + 1 < lotSize) targetJunctions[(y + 1) * Size + x] |= 128;
                    }
                }
            }
        }
        if (walls is not null)
        // Walks the camera-rotated grid where each camera tile owns the two visible wall faces, mapping
        // pattern and style from the owning source tile edge.
        Lap("wallTiles");
        for (var v = 0; v < lotSize; v++)
        for (var u = 0; u < lotSize; u++)
        {
            var (sourceX, sourceY) = camera.SourceTile(u, v);
            if (sourceX < 0 || sourceY < 0 || sourceX >= lotSize || sourceY >= lotSize) continue;
            // Cuts a wall segment only when both the owning tile and the opposing neighbor tile are
            // included in the cutaway mask.
            bool Cutaway(int otherX, int otherY) =>
                wallLevel == viewLevel && cutawayMask is not null && otherX >= 0 && otherY >= 0 && otherX < lotSize && otherY < lotSize &&
                cutawayMask[sourceY * lotSize + sourceX] && cutawayMask[otherY * lotSize + otherX];
            var down = wallMode == "down" && wallLevel == viewLevel;
            bool IsWindowEdge(int ownerX, int ownerY, int side, byte storedStyle) =>
                wallObjectKinds.GetValueOrDefault((wallLevel, ownerX, ownerY, side)) == 2 ||
                (!hasLiveObjects && storedStyle is 4 or 9 or 10 or 11 or (>= 16 and <= 22));
            bool ThickFace(int face, int cameraX, int cameraY, bool callerDown)
            {
                var (tileX, tileY) = camera.SourceTile(cameraX, cameraY);
                if (tileX < 0 || tileY < 0 || tileX >= lotSize || tileY >= lotSize) return false;
                var (otherX, otherY) = face == 0
                    ? camera.SourceTile(cameraX - 1, cameraY)
                    : camera.SourceTile(cameraX, cameraY - 1);
                var (ownerX, ownerY, side) = EdgeOwner(tileX, tileY, otherX, otherY);
                if (ownerX < 0 || ownerY < 0 || ownerX >= lotSize || ownerY >= lotSize) return false;
                var bit = side == 0 ? 1 : 2;
                var owner = walls!.Element(ownerX, ownerY);
                var stored = owner[side == 0 ? 2 : 3];
                if ((owner[0] & bit) == 0 || NormalizedWallStyle(stored) != 1) return false;
                var otherDown = down || (wallLevel == viewLevel && IsCutawayPair(tileX, tileY, otherX, otherY));
                if (otherDown && IsDoorEdge(ownerX, ownerY, side, stored)) return false;
                return callerDown || !otherDown;
            }
            bool DiagonalAt(int vertexX, int vertexY, bool callerDown)
            {
                for (var dy = -1; dy <= 0; dy++)
                for (var dx = -1; dx <= 0; dx++)
                {
                    var (sx, sy) = camera.SourceTile(vertexX + dx, vertexY + dy);
                    if (sx < 0 || sy < 0 || sx >= lotSize || sy >= lotSize) continue;
                    var tile = walls.Element(sx, sy);
                    var diag = tile[0] & 0x30;
                    if (diag is not (0x10 or 0x20) || NormalizedWallStyle(tile[3]) != 1) continue;
                    var diagDown = down || (wallLevel == viewLevel && cutawayMask is not null && cutawayMask[sy * lotSize + sx]);
                    if (diagDown && IsDoorEdge(sx, sy, 1, tile[3])) continue;
                    if (!callerDown && diagDown) continue;
                    var vertical = (diag == 0x20) != (camera.Orientation % 2 != 0);
                    if (vertical ? dx == dy : dx != dy) return true;
                }
                return false;
            }
            // Wall sprites scale per zoom level, with near art using two texels per scene unit and far art
            // using half a texel per unit.
            ZoomImage? Zoom(ushort pattern, WallStyleArt wallStyle, int zoom, float scale,
                (int X, int Y, int Width, int Height)[] rectangles, int configuration, int artFace, bool isDown)
            {
                var art = WallTexture(pattern, wallStyle, artFace, isDown, zoom);
                if (art < 0) return null;
                var medium = WallRectangles[artFace];
                var zoomed = rectangles[artFace];
                return new ZoomImage(art, zoomed.X / scale - medium.X, zoomed.Y / scale - medium.Y,
                    zoomed.Width / scale, zoomed.Height / scale, ArchitectureDepth(configuration, art));
            }
            void AddFace(int face, int neighbourX, int neighbourY)
            {
                // Separates wall face attributes: the owning tile stores the structural wall style, while
                // each adjacent tile stores the wallpaper pattern facing its interior.
                var (ownerX, ownerY, side) = EdgeOwner(sourceX, sourceY, neighbourX, neighbourY);
                if (ownerX < 0 || ownerY < 0 || ownerX >= lotSize || ownerY >= lotSize) return;
                var owner = walls!.Element(ownerX, ownerY);
                // Decodes ARRY wall cell data: byte 0 stores segment flags, bytes 2 and 3 store wall
                // styles, and bytes 4 through 7 store wallpaper patterns for each face.
                if ((owner[0] & (side == 0 ? 1 : 2)) == 0) return;
                var sourceTile = walls.Element(sourceX, sourceY);
                var patternSlot = neighbourX < sourceX ? 4 : neighbourX > sourceX ? 7 : neighbourY < sourceY ? 5 : 6;
                var pattern = sourceTile[patternSlot];
                var storedStyle = side == 0 ? owner[2] : owner[3];
                var style = NormalizedWallStyle(storedStyle);
                // Lowering walls or applying cutaway lowers solid walls, hides door segments completely to
                // create openings, and renders window segments as lowered wall stubs.
                var faceDown = down || (style == 1 && Cutaway(neighbourX, neighbourY));
                if (faceDown && IsDoorEdge(ownerX, ownerY, side, storedStyle)) return;
                var objectStyle = objectStyles.GetValueOrDefault((wallLevel, ownerX, ownerY, side));
                var wallStyle = faceDown && IsWindowEdge(ownerX, ownerY, side, storedStyle)
                    ? WallStyleArt.Base(1)
                    : ResolveWallStyle(style, objectStyle);
                var texture = WallTexture(pattern, wallStyle, face, faceDown, 1);
                if (texture < 0) return;
                var altitude = Elevation(sourceX, sourceY) / 40f + (wallLevel - 1) * EntityTransform.StoryHeight;
                var interval = camera.SpriteDepthCamera(u, v, altitude);
                var point = camera.ProjectCamera(u, v, altitude * 40);
                var rectangle = WallRectangles[face];
                var sourceDirection = camera.SourceWallDirection(face);
                var (faceSubX, faceSubY) = sourceDirection switch
                {
                    0 => (2, 8),
                    1 => (8, 2),
                    2 => (13, 8),
                    _ => (8, 13),
                };
                var faceRoom = wallRooms is not null && sourceX >= 0 && sourceY >= 0 && sourceX < wallRooms.Width && sourceY < wallRooms.Height
                    ? wallRooms.RoomAtPosition(sourceX * 16 + faceSubX, sourceY * 16 + faceSubY)
                    : wallRooms?.RoomAt(sourceX, sourceY);
                sprites.Add(new SceneSprite(texture, point.X - 34 + rectangle.X, point.Y - 158 + rectangle.Y,
                    rectangle.Width, rectangle.Height, false, 0, camera.DepthOrderCamera(u, v) - 0.5f, 1,
                    ArchitectureDepth(face + 4, texture), interval.Near, interval.Far,
                    Wall: style == 1 ? new WallSide(sourceX, sourceY, wallLevel, sourceDirection) : null,
                    Level: wallLevel, Room: faceRoom,
                    ArchitectureDepth: new WallDepth(sourceX, sourceY, wallLevel, face + 4),
                    Near: Zoom(pattern, wallStyle, 2, 2f, NearWallRectangles, face, face, faceDown),
                    Far: Zoom(pattern, wallStyle, 0, 0.5f, FarWallRectangles, face + 8, face, faceDown)));
                if (style != 1) return;
                // Clips wall end caps to a narrow vertical strip scaled by zoom level, emitting them only
                // at open ends rather than joined corners.
                AddEnd(false);
                AddEnd(true);

                void AddEnd(bool close)
                {
                    var vx = u + (face == 1 && close ? 1 : 0);
                    var vy = v + (face == 0 && close ? 1 : 0);
                    var connected = face == 0
                        ? ThickFace(0, u, v + (close ? 1 : -1), faceDown) || ThickFace(1, vx, vy, faceDown) || ThickFace(1, vx - 1, vy, faceDown)
                        : ThickFace(1, u + (close ? 1 : -1), v, faceDown) || ThickFace(0, vx, vy, faceDown) || ThickFace(0, vx, vy - 1, faceDown);
                    if (connected || DiagonalAt(vx, vy, faceDown)) return;
                    var artFace = close ? 1 - face : face;
                    var endU = face == 0 ? u - (close ? 1 : 0) : u + (close ? 1 : -1);
                    var endV = face == 0 ? v + (close ? 1 : -1) : v - (close ? 1 : 0);
                    var right = artFace == 1;
                    // Wall end cap strips render using the base wall style rather than door or window
                    // cutout masks.
                    var capStyle = WallStyleArt.Base(style);
                    var fullTexture = WallTexture(pattern, capStyle, artFace, faceDown, 1);
                    if (fullTexture < 0) return;
                    var endTexture = WallStrip(fullTexture, 6, right);
                    var depthTexture = WallStrip(ArchitectureDepth(artFace + 4, fullTexture), 6, right);
                    var endSource = camera.SourcePoint(endU, endV);
                    var endAltitude = Elevation(endSource.X, endSource.Y) / 40f + (wallLevel - 1) * EntityTransform.StoryHeight;
                    var endPoint = camera.ProjectCamera(endU, endV, endAltitude * 40);
                    var rect = WallRectangles[artFace];
                    var stripX = right ? rect.Width - 6 : 0;
                    var spriteX = endPoint.X - 34 + rect.X + stripX;
                    var spriteY = endPoint.Y - 158 + rect.Y;
                    var interval = camera.SpriteDepthCamera(endU, endV, endAltitude);
                    ZoomImage? CapZoom(int zoom, float scale, (int X, int Y, int Width, int Height)[] rects, int config)
                    {
                        var full = WallTexture(pattern, capStyle, artFace, faceDown, zoom);
                        if (full < 0) return null;
                        var width = (int)(6 * scale);
                        var r = rects[artFace];
                        var x = endPoint.X - 34 + (r.X + (right ? r.Width - width : 0)) / scale;
                        var y = endPoint.Y - 158 + r.Y / scale;
                        return new ZoomImage(WallStrip(full, width, right), x - spriteX, y - spriteY,
                            6, r.Height / scale, WallStrip(ArchitectureDepth(config, full), width, right));
                    }
                    sprites.Add(new SceneSprite(endTexture, spriteX, spriteY, 6, rect.Height, false, 0,
                        camera.DepthOrderCamera(endU, endV) - 0.5f, 1, depthTexture, interval.Near, interval.Far,
                        Level: wallLevel, Room: faceRoom,
                        ArchitectureDepth: new WallDepth(sourceX, sourceY, wallLevel, artFace + 4),
                        Near: CapZoom(2, 2, NearWallRectangles, artFace),
                        Far: CapZoom(0, 0.5f, FarWallRectangles, artFace + 8)));
                }
            }
            // Diagonal walls use segment bit 0x10 for horizontal or 0x20 for vertical diagonals; odd camera
            // rotations swap these bits, while rotations 1 and 2 swap the diagonal pattern slots.
            void AddDiagonal()
            {
                var owner = walls!.Element(sourceX, sourceY);
                var sourceDiag = owner[0] & 0x30;
                if (sourceDiag is not (0x10 or 0x20)) return;
                var cameraDiag = camera.Orientation % 2 == 0 ? sourceDiag : sourceDiag ^ 0x30;
                var face = cameraDiag == 0x10 ? 2 : 3;
                var swapSides = camera.Orientation is 1 or 2;
                var primaryPattern = swapSides ? owner[6] : owner[7];
                var secondaryPattern = swapSides ? owner[7] : owner[6];
                var pattern = primaryPattern != 0 ? primaryPattern : secondaryPattern;
                var storedStyle = owner[3];
                var style = (ushort)storedStyle;
                var diagDown = down || (wallLevel == viewLevel && style is not (2 or 12 or 13 or 14) && cutawayMask is not null && cutawayMask[sourceY * lotSize + sourceX]);
                if (diagDown && IsDoorEdge(sourceX, sourceY, 1, storedStyle)) return;
                // Diagonal wall tiles preserve unnormalized style indices, though object-provided styles
                // still override standard style 1.
                var objectStyle = objectStyles.GetValueOrDefault((wallLevel, sourceX, sourceY, 1));
                var wallStyle = diagDown && IsWindowEdge(sourceX, sourceY, 1, storedStyle)
                    ? WallStyleArt.Base(1)
                    : ResolveWallStyle(style, objectStyle);
                var texture = WallTexture(pattern, wallStyle, face, diagDown, 1);
                if (texture < 0) return;
                var altitude = Elevation(sourceX, sourceY) / 40f + (wallLevel - 1) * EntityTransform.StoryHeight;
                var interval = camera.SpriteDepthCamera(u, v, altitude);
                var point = camera.ProjectCamera(u, v, altitude * 40);
                var rectangle = WallRectangles[face];
                // Shades visible diagonal wall faces using the camera-facing room half, since diagonal
                // dividing tiles are marked as having no room.
                var (diagSubX, diagSubY) = sourceDiag == 0x10
                    ? (camera.Orientation is 1 or 2 ? (4, 4) : (12, 12))
                    : (camera.Orientation is 2 or 3 ? (4, 12) : (12, 4));
                var diagRoom = wallRooms is not null && sourceX >= 0 && sourceY >= 0 && sourceX < wallRooms.Width && sourceY < wallRooms.Height
                    ? wallRooms.RoomAtPosition(sourceX * 16 + diagSubX, sourceY * 16 + diagSubY)
                    : wallRooms?.RoomAt(sourceX, sourceY);
                sprites.Add(new SceneSprite(texture, point.X - 34 + rectangle.X, point.Y - 158 + rectangle.Y,
                    rectangle.Width, rectangle.Height, false, 0, camera.DepthOrderCamera(u, v) - 0.5f, 1,
                    ArchitectureDepth(face + 4, texture), interval.Near, interval.Far,
                    Wall: NormalizedWallStyle(storedStyle) == 1
                        ? new WallSide(sourceX, sourceY, wallLevel, camera.SourceWallDirection(face), Edge: sourceDiag,
                            Side: swapSides ? (sourceDiag == 0x10 ? 2 : 1) : (sourceDiag == 0x10 ? 4 : 3))
                        : null,
                    Level: wallLevel, Room: diagRoom,
                    ArchitectureDepth: new WallDepth(sourceX, sourceY, wallLevel, face + 4),
                    Near: Zoom(pattern, wallStyle, 2, 2f, NearWallRectangles, face, face, diagDown),
                    Far: Zoom(pattern, wallStyle, 0, 0.5f, FarWallRectangles, face + 8, face, diagDown)));
            }
            // Anchors wall corner junction sprites at the top of the story, or 0.3 height units above the
            // floor when walls are lowered.
            void AddJunction(int sourceFlags, bool isDown)
            {
                if (sourceFlags == 0) return;
                var flags = camera.CameraJunction(sourceFlags);
                var rawFrame = JunctionFrames[flags & 0xff];
                var frameIndex = rawFrame is 0 or byte.MaxValue ? -1 : rawFrame - 1;
                if (frameIndex < 0) return;
                var useDownSheet = isDown && (flags & 0x50) == 0;
                var texture = JunctionTexture(1, frameIndex, useDownSheet);
                if (texture < 0) return;
                var floorAltitude = Elevation(sourceX, sourceY) / 40f + (wallLevel - 1) * EntityTransform.StoryHeight;
                var altitude = floorAltitude + (isDown ? 0.3f : EntityTransform.StoryHeight);
                var corner = camera.CameraPoint(sourceX, sourceY);
                var depthBias = (flags & 0x40) != 0 ? 0.5f : -0.5f;
                var interval = camera.SpriteDepthCamera(corner.X + depthBias, corner.Y + depthBias, altitude);
                var point = camera.ProjectCamera(corner.X, corner.Y, altitude * 40);
                ZoomImage? JunctionZoom(int zoom, float scale, (int X, int Y) destination, int configuration)
                {
                    var art = JunctionTexture(zoom, frameIndex, useDownSheet);
                    if (art < 0) return null;
                    return new ZoomImage(art, destination.X / scale - JunctionOrigin.X,
                        destination.Y / scale - JunctionOrigin.Y, 64, 32, ArchitectureDepth(configuration, art));
                }
                // Aligns junction cap sprites to rest cleanly atop standing and lowered walls.
                var junctionRoom = wallRooms is not null && sourceX >= 0 && sourceY >= 0 && sourceX < wallRooms.Width && sourceY < wallRooms.Height
                    ? (int?)wallRooms.RoomAt(sourceX, sourceY)
                    : (wallRooms is not null ? RoomMap.NoRoom : 1);
                sprites.Add(new SceneSprite(texture, point.X - 32, point.Y - (isDown ? 15 : 13), 64, 32, false, 0,
                    camera.DepthOrderCamera(corner.X, corner.Y) - 0.5f, 1, ArchitectureDepth(19, texture),
                    interval.Near, interval.Far, Level: wallLevel, Room: junctionRoom,
                    Junction: new WallJunction(sourceX, sourceY, wallLevel, sourceFlags, frameIndex, isDown),
                    Near: JunctionZoom(2, 2f, (4, isDown ? 318 : 317), 18), Far: JunctionZoom(0, 0.5f, (1, 80), 20)));
            }
            // Maps camera-relative wall faces to grid neighbors: the top-left face looks toward the prior
            // column and the top-right face toward the prior row.
            var (leftX, leftY) = camera.SourceTile(u - 1, v);
            var (upX, upY) = camera.SourceTile(u, v - 1);
            AddFace(0, leftX, leftY);
            AddFace(1, upX, upY);
            AddDiagonal();
            AddJunction(downJunctions[sourceY * Size + sourceX], isDown: true);
            AddJunction(upJunctions[sourceY * Size + sourceX], isDown: false);

            int WallTexture(ushort pattern, WallStyleArt styleArt, int frame, bool down, int zoom)
            {
                if (wallArtwork is null) { missingResources.Add("GameData/walls.iff"); return -1; }
                var key = (pattern, styleArt.File, styleArt.SpriteBase, styleArt.BaseStyle, frame, down, zoom);
                if (wallTextures.TryGetValue(key, out var cached)) return cached;
                IffFile patternFile;
                ushort patternSpriteId;
                if (pattern is >= 256 and < 65534 && dynamicWallFiles.TryGetValue(pattern, out var dynamicWallPath))
                {
                    patternFile = OpenArtwork(dynamicWallPath);
                    // Standalone .wll files store wallpaper sprites at SPR# 1 for far, 1793 for medium, and
                    // 2049 for near zoom.
                    patternSpriteId = zoom switch { 0 => 1, 2 => 2049, _ => 1793 };
                }
                else
                {
                    patternFile = wallArtwork;
                    patternSpriteId = WallPatternId(pattern, zoom);
                }
                var patternChunk = patternFile.Find(ResourceType.Spr1, patternSpriteId);
                if (patternChunk is null)
                {
                    missingResources.Add($"wall:{pattern}:zoom:{zoom}");
                    return -1;
                }
                Sprite maskSprite;
                if (styleArt.File is { } objectFile)
                {
                    // Architectural object wall mask sprites are ordered as far, medium, and near for
                    // walls-up, followed by far, medium, and near for walls-down.
                    var maskId = ResolveWallMaskId(styleArt, down, zoom);
                    var maskChunk = objectFile.Find(ResourceType.Spr1, maskId);
                    if (maskChunk is null)
                    {
                        missingResources.Add($"wall:{pattern}:object-style:{styleArt.SpriteBase}:zoom:{zoom}:down:{down}");
                        return -1;
                    }
                    if (!objectWallSprites.TryGetValue((objectFile, maskId), out maskSprite!))
                        objectWallSprites[(objectFile, maskId)] = maskSprite = Sprite.Read(objectFile, maskChunk);
                }
                else
                {
                    var maskId = ResolveWallMaskId(styleArt, down, zoom);
                    var maskChunk = wallArtwork.Find(ResourceType.Spr1, maskId);
                    // Falls back to the walls-up mask artwork when an object style does not provide a
                    // lowered wall mask.
                    if (maskChunk is null && down)
                    {
                        maskId = WallMaskId(styleArt.BaseStyle, false, zoom);
                        maskChunk = wallArtwork.Find(ResourceType.Spr1, maskId);
                    }
                    if (maskChunk is null)
                    {
                        missingResources.Add($"wall:{pattern}:style:{styleArt.BaseStyle}:zoom:{zoom}");
                        return -1;
                    }
                    if (!wallSprites.TryGetValue(maskChunk.Id, out maskSprite!))
                        wallSprites[maskChunk.Id] = maskSprite = Sprite.Read(wallArtwork, maskChunk);
                }
                Sprite patternSprite;
                if (ReferenceEquals(patternFile, wallArtwork))
                {
                    if (!wallSprites.TryGetValue(patternChunk.Id, out patternSprite!))
                        wallSprites[patternChunk.Id] = patternSprite = Sprite.Read(wallArtwork, patternChunk);
                }
                else
                {
                    if (!objectWallSprites.TryGetValue((patternFile, patternChunk.Id), out patternSprite!))
                        objectWallSprites[(patternFile, patternChunk.Id)] = patternSprite = Sprite.Read(patternFile, patternChunk);
                }
                if (frame >= patternSprite.Frames.Count || frame >= maskSprite.Frames.Count)
                {
                    missingResources.Add($"wall:{pattern}:style:{styleArt.BaseStyle}:zoom:{zoom}:frame:{frame}");
                    return -1;
                }
                var art = patternSprite.Frames[frame];
                var mask = maskSprite.Frames[frame];
                var colors = Colors(patternFile, patternSprite);
                if (colors.Length == 0) { missingResources.Add($"wall-palette:{pattern}"); return -1; }
                var bytes = new byte[art.Width * art.Height * 4];
                for (var py = 0; py < art.Height; py++)
                for (var px = 0; px < art.Width; px++)
                {
                    var pixel = py * art.Width + px;
                    if (!art.Opaque[pixel]) continue;
                    var color = colors[art.Indices[pixel]];
                    var maskX = px * mask.Width / art.Width;
                    var maskY = py * mask.Height / art.Height;
                    var visible = mask.Opaque[maskY * mask.Width + maskX];
                    var at = pixel * 4;
                    bytes[at] = color.Red;
                    bytes[at + 1] = color.Green;
                    bytes[at + 2] = color.Blue;
                    bytes[at + 3] = visible ? (byte)255 : (byte)0;
                }
                var styleName = styleArt.File is null ? styleArt.BaseStyle.ToString() : $"obj{styleArt.SpriteBase}";
                var name = $"wall-{pattern}-{styleName}-{frame}-{(down ? "down" : "up")}-{zoom}.rgba";
                files[name] = bytes;
                var result = textures.Count;
                textures.Add(new Texture(name, art.Width, art.Height, "rgba"));
                wallTextures[key] = result;
                return result;
            }
        }

        }

        }

        var mediumUnitHeight = (float)(Math.Sqrt(64 * 64 / 2.0) * Math.Cos(Math.PI / 6));
        // Slot height offsets are 1-indexed by the slot Height field, where only codes 1, 2, 3, 4, and 8
        // override the authored Z coordinate.
        float[] slotHeightOffsets = [0f, 2.5f, 4f, 4.05f, 0f, 0f, 0f, 4f, 0f];
        var avatarGeometry = AvatarSceneGeometry.For(gameRoot, registry);
        var avatarParts = new Dictionary<short, IReadOnlyList<AvatarSceneGeometry.Part>>();
        var buildingAvatars = new HashSet<short>();
        XPerson? selectedPerson = null;
        if (world[SimulationGlobal.SimlessBuildMode] == 0)
        {
            if (world[SimulationGlobal.SelectedPersonID] is > 0 and var selectedId && world.ObjectById(selectedId) is XPerson byId)
            {
                selectedPerson = byId;
            }
            else
            {
                foreach (var candidate in world.WalkOrder)
                {
                    if (candidate is XPerson flagged && (flagged.Flags & 2u) != 0)
                    {
                        selectedPerson = flagged;
                        break;
                    }
                }
            }
        }
        // Evaluates parent avatar poses before attached child avatars so child attachment transforms
        // inherit updated parent bone positions.
        foreach (var walkEntry in world.WalkOrder)
        {
            if (walkEntry is not XPerson person) continue;
            var visibleOnStairs = person.Position.Height == viewLevel + 1 && person[PersonData.WalkingUpStairs] != 0;
            if (person.Position.X < 0 || person.Position.Y < 0 || person.Position.Height < 1 ||
                (person.Position.Height > viewLevel && !visibleOnStairs) || person[ObjectData.Hidden] != 0) continue;
            BuildAvatar(person);
        }

        void BuildAvatar(XPerson person, bool refreshPose = false)
        {
            if (!refreshPose && avatarParts.ContainsKey(person.Id)) return;
            var hadPreviousParts = avatarParts.TryGetValue(person.Id, out var previousParts);
            if (refreshPose) avatarParts.Remove(person.Id);
            if (!buildingAvatars.Add(person.Id))
            {
                if (refreshPose && hadPreviousParts) avatarParts[person.Id] = previousParts!;
                if (!refreshPose) avatarParts[person.Id] = Array.Empty<AvatarSceneGeometry.Part>();
                return;
            }
            try
            {
                System.Numerics.Vector3? attachmentRoot = null;
                float? inheritedRootDirection = null;
                var parent = ContainmentParent(world, person);
                // SlotHierNumber stores the 1-based hierarchy index, mapping index 2 to the logical HEAD
                // slot (index 1) to transform an attached child by the parent head bone.
                if (person[ObjectData.SlotHierNumber] == 2 && parent is XPerson parentPerson &&
                    HeadSlotTargets(world, parentPerson, person) &&
                    parentPerson.Position.Height >= 1 &&
                    (parentPerson.Position.Height <= viewLevel ||
                     (parentPerson.Position.Height == viewLevel + 1 && parentPerson[PersonData.WalkingUpStairs] != 0)))
                {
                    BuildAvatar(parentPerson, refreshPose);
                    if (avatarGeometry.HeadPosition(parentPerson) is { } headPosition)
                    {
                        attachmentRoot = headPosition;
                        inheritedRootDirection = avatarGeometry.RootDirection(parentPerson);
                    }
                }

                // Attaches the standard plumbbob to the selected avatar, the date plumbbob to the active
                // date target, and the star plumbbob to celebrities with sufficient fame, provided the head
                // slot is unoccupied.
                var headSlotOccupied = false;
                if (person.Slots.Count > 2 && person.Slots[2] != 0)
                {
                    foreach (var item in world.WalkOrder)
                    {
                        if (!ReferenceEquals(item, person) &&
                            item[ObjectData.SlotHierNumber] == 2 &&
                            ReferenceEquals(ContainmentParent(world, item), person) &&
                            HeadSlotTargets(world, person, item))
                        {
                            headSlotOccupied = true;
                            break;
                        }
                    }
                }
                var headIndicatorKind = headSlotOccupied
                    ? 0
                    : selectedPerson is not null && person.Id == selectedPerson.Id
                        ? 1
                        : selectedPerson is not null && selectedPerson[PersonData.AutoFollowHotDate] > 0 &&
                          selectedPerson[PersonData.AutoFollowHotDate] == person.Id
                            ? 2
                            : person[PersonData.FameStarPower09] >= 10
                                ? 3
                                : 0;

                avatarParts[person.Id] = avatarGeometry.Build(person, person.Position.Height, cameraOrientation, Elevation,
                    camera, attachmentRoot: attachmentRoot, inheritedRootDirection: inheritedRootDirection,
                    headIndicatorKind: headIndicatorKind, subTickAlpha: animating ? subTickAlpha : 0f, updates: updates,
                    careerHost: world.CareerHost);
            }
            catch
            {
                if (refreshPose && hadPreviousParts) avatarParts[person.Id] = previousParts!;
                throw;
            }
            finally { buildingAvatars.Remove(person.Id); }
        }
        var objectPositions = new Dictionary<XObject, Microsoft.Xna.Framework.Vector3>();
        var resolvingContainment = new HashSet<XObject>();
        var shadowedObjectTiles = new HashSet<(int Level, int TileX, int TileY)>();
        var traceSpriteGuid = uint.TryParse(Environment.GetEnvironmentVariable("SIMS_NATIVE_TRACE_SPRITE_GUID"),
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedTraceSpriteGuid) ? parsedTraceSpriteGuid : 0;
        var tracedSpriteResources = new HashSet<(short Id, ushort Graphic, int Direction)>();
        Microsoft.Xna.Framework.Vector3 ObjectPosition(XObject entity)
        {
            if (objectPositions.TryGetValue(entity, out var cached)) return cached;
            if (!resolvingContainment.Add(entity)) return RootPosition(entity.Position);
            try
            {
                var container = ContainmentParent(world, entity) ??
                    (entity[ObjectData.ContainerId] != 0 ? world.ObjectById(entity[ObjectData.ContainerId]) : null);
                var position = ResolveObjectPosition(entity, container,
                    RootPosition,
                    (person) => avatarParts.ContainsKey(person.Id)
                        ? avatarGeometry.HeadPosition(person) ?? SlotPosition(person, 1)
                        : SlotPosition(person, 1),
                    (parent, slotIndex) => SlotPosition(parent, slotIndex),
                    (person) => avatarParts.ContainsKey(person.Id)
                        ? avatarGeometry.HandPosition(person) ?? SlotPosition(person, 0)
                        : SlotPosition(person, 0),
                    (person) => avatarParts.ContainsKey(person.Id)
                        ? avatarGeometry.PelvisPosition(person) ?? SlotPosition(person, 2)
                        : SlotPosition(person, 2),
                    (person) => avatarParts.ContainsKey(person.Id)
                        ? avatarGeometry.HeadAttachmentPosition(person) ?? SlotPosition(person, 3)
                        : SlotPosition(person, 3));
                objectPositions.Add(entity, position);
                return position;
            }
            finally { resolvingContainment.Remove(entity); }
        }
        Microsoft.Xna.Framework.Vector3 RootPosition(ObjectPosition tile) =>
            new(tile.X / 16f, tile.Y / 16f,
                (tile.Height - 1) * EntityTransform.StoryHeight + Elevation(tile.X / 16f + 0.5f, tile.Y / 16f + 0.5f) / 40);
        Microsoft.Xna.Framework.Vector3 SlotPosition(XObject container, int index)
        {
            // Calculates container slot world positions by rotating slot offsets by container orientation
            // and applying SLOT height code table offsets.
            var origin = ObjectPosition(container);
            if (index < 0) return origin;
            var defSlots = container.DefinitionSlots;
            var matchedSlotIndex = -1;
            var matchIndex = 0;
            for (var s = 0; s < defSlots.Count; s++)
            {
                if (defSlots[s].Type != 0) continue;
                if (matchIndex == index) { matchedSlotIndex = s; break; }
                matchIndex++;
            }
            if (matchedSlotIndex < 0) return origin;
            var item = defSlots[matchedSlotIndex];
            var height = item.Height switch
            {
                1 or 2 or 3 or 4 or 8 => slotHeightOffsets[item.Height - 1],
                >= 0 and <= 9 => item.Z,
                _ => throw new InvalidDataException("Invalid object containment slot height"),
            };
            var (rotatedX, rotatedY) = RotateCardinalOffset(container[ObjectData.Direction], item.X / 16f, item.Y / 16f);
            return origin + new Microsoft.Xna.Framework.Vector3(rotatedX, rotatedY,
                container is XPerson ? 0 : height / 5);
        }
        world.HeadSeekGeometryResolver = (actor, target) =>
        {
            // Refreshes avatar poses when resolving bone attachment points to ensure offscreen avatars
            // reflect current animation transforms.
            if (actor.AnimatorHeadSeekRegistrationsAvailable == false) return null;
            try { BuildAvatar(actor, refreshPose: true); }
            catch (Exception error) when (error is InvalidDataException or ResourceFormatException or ArgumentException)
            {
                avatarGeometry.ClearHeadSeekRegistrations(actor);
                missingResources.Add($"sim:{actor.Id}:head-seek-pose");
                return null;
            }
            if (actor.AnimatorHeadSeekRegistrationsAvailable == false ||
                avatarGeometry.HeadSeekRegistration(actor) is not { } actorRegistration ||
                avatarGeometry.HeadFakeRegistration(actor) is not { } headFakeRegistration)
                return null;

            System.Numerics.Vector2 targetPoint;
            if (target is XPerson targetPerson)
            {
                if (!ReferenceEquals(actor, targetPerson))
                {
                    try { BuildAvatar(targetPerson, refreshPose: true); }
                    catch (Exception error) when (error is InvalidDataException or ResourceFormatException or ArgumentException)
                    {
                        avatarGeometry.ClearHeadSeekRegistrations(targetPerson);
                        missingResources.Add($"sim:{targetPerson.Id}:head-seek-pose");
                        return null;
                    }
                }
                if (avatarGeometry.HeadSeekRegistration(targetPerson) is not { } targetRegistration) return null;
                targetPoint = new System.Numerics.Vector2(targetRegistration.X, targetRegistration.Z);
            }
            else
            {
                // Maps avatar coordinate axes to tile space, where avatar bone space maps X to tile Y and Z
                // to tile X.
                objectPositions.Clear();
                resolvingContainment.Clear();
                var targetPosition = ObjectPosition(target);
                targetPoint = new System.Numerics.Vector2(targetPosition.Y, targetPosition.X);
            }

            return new HeadSeekGeometry(
                new System.Numerics.Vector2(actorRegistration.X, actorRegistration.Z),
                new System.Numerics.Vector2(headFakeRegistration.X, headFakeRegistration.Z), targetPoint);
        };
        void EmitObject(ObjectEntry entry, uint guid, short id, float tileX, float tileY, int level, int direction,
            short graphic = 0, short[]? dynamicSpriteFlags = null, XObject? source = null)
        {
            var origin = source is null
                ? new Microsoft.Xna.Framework.Vector3(tileX, tileY,
                    (level - 1) * EntityTransform.StoryHeight + Elevation(tileX + 0.5f, tileY + 0.5f) / 40)
                : ObjectPosition(source);
            var contained = IsHierarchyContained(source);
            var objectRooms = world.LotRoomMaps.GetValueOrDefault(level);
            var subtileRoom = ResolveSubtileRoom(objectRooms, origin.X, origin.Y, source);
            var shadowLightByte = TileSpriteLightByte(subtileRoom);
            var shadowHidden = source is not null && (source[ObjectData.Flags] & 4) != 0;
            var litLightSource = source is not null && source[ObjectData.LightingContribution0100] != 0;
            ObjectSpriteCacheKey cacheKey = default;
            var canCacheObject = source is not null && traceSpriteGuid == 0 && (animating || retain is not null);
            if (canCacheObject)
            {
                cacheKey = new ObjectSpriteCacheKey(guid, origin.X, origin.Y, origin.Z, level, direction, graphic,
                    contained, shadowHidden, litLightSource, subtileRoom ?? -1, shadowLightByte, camera.Orientation);
                if (animating && kept.ObjectSprites.TryGetValue(id, out var cachedObj) &&
                    cachedObj.Key == cacheKey &&
                    DynamicFlagsSequenceEqual(cachedObj.DynamicFlags, dynamicSpriteFlags))
                {
                    var cachedSprites = cachedObj.Sprites;
                    var hasCachedShadow = false;
                    for (var c = 0; c < cachedSprites.Length; c++)
                    {
                        if (cachedSprites[c].FloorShadow is not null) { hasCachedShadow = true; break; }
                    }
                    if (!hasCachedShadow || shadowedObjectTiles.Add((level, (int)MathF.Floor(origin.X), (int)MathF.Floor(origin.Y))))
                    {
                        sprites.AddRange(cachedSprites);
                    }
                    else
                    {
                        for (var c = 0; c < cachedSprites.Length; c++)
                        {
                            var cs = cachedSprites[c];
                            sprites.Add(cs.FloorShadow is not null
                                ? cs with { FloorShadow = null, FloorShadowNear = null, FloorShadowFar = null }
                                : cs);
                        }
                    }
                    return;
                }
            }
            var emitStart = sprites.Count;
            // Resolves an object's active DGRP resource by adding the Graphic offset to OBJD BaseGraphic.
            var baseGraphic = (ushort)entry.Definition[ObjdField.BaseGraphic];
            var selectedGraphic = SelectedGraphic(baseGraphic, graphic);
            var groupChunk = entry.File.Find(ResourceType.Dgrp, selectedGraphic);
            if (groupChunk is null)
            {
                missingResources.Add($"object:{id}:guid:{guid:x8}:dgrp:{selectedGraphic}");
                return;
            }
            DrawGroup group;
            try { group = CachedGroup(entry.File, groupChunk); }
            catch (ResourceFormatException)
            {
                missingResources.Add($"object:{id}:guid:{guid:x8}:dgrp-read:{selectedGraphic}");
                return;
            }
            // DGRP resources store images for cardinal facings (flags 1, 4, 16, 64), rotated by camera
            // orientation.
            var image = SelectedImage(group, direction, camera.Orientation);
            if (image is null)
            {
                missingResources.Add($"object:{id}:guid:{guid:x8}:image:{selectedGraphic}:direction:{direction}:rotation:{camera.Orientation}");
                return;
            }
            // Pairs zoomed DGRP sprites with medium-zoom sprites by part index across near (zoom 3) and far
            // (zoom 1) images.
            var nearImage = SelectedImage(group, direction, camera.Orientation, 3);
            var farImage = SelectedImage(group, direction, camera.Orientation, 1);
            // Decodes draw group sprite parts using SPR2 palette, depth, and alpha data, falling back to
            // legacy SPR# frames when SPR2 is absent.
            (DepthFrame? Frame, IndexedFrame? Legacy, int Width, int Height, int PaletteId) ReadPart(
                DrawGroupSprite part, bool report)
            {
                var sprChunk = entry.File.Find(ResourceType.Spr2, (ushort)part.SpriteId);
                if (sprChunk is not null)
                {
                    var art = CachedDepthSprite(entry.File, sprChunk);
                    if (part.Frame < 0 || part.Frame >= art.Frames.Count)
                    {
                        if (report) missingResources.Add($"object:{id}:sprite:{part.SpriteId}:frame:{part.Frame}:frames:{art.Frames.Count}");
                        return (null, null, 0, 0, -1);
                    }
                    var depthFrame = art.Frames[part.Frame];
                    return (depthFrame, null, depthFrame.Width, depthFrame.Height, art.PaletteId);
                }
                var legacyChunk = entry.File.Find(ResourceType.Spr1, (ushort)part.SpriteId);
                if (legacyChunk is null)
                {
                    if (report) missingResources.Add($"object:{id}:sprite:{part.SpriteId}:chunk-absent");
                    return (null, null, 0, 0, -1);
                }
                var legacyArt = CachedSprite(entry.File, legacyChunk);
                if (part.Frame < 0 || part.Frame >= legacyArt.Frames.Count)
                {
                    if (report) missingResources.Add($"object:{id}:sprite:{part.SpriteId}:frame:{part.Frame}:frames:{legacyArt.Frames.Count}");
                    return (null, null, 0, 0, -1);
                }
                var legacyFrame = legacyArt.Frames[part.Frame];
                return (null, legacyFrame, legacyFrame.Width, legacyFrame.Height, legacyArt.PaletteId);
            }
            int PartTexture(DrawGroupSprite part, DepthFrame? frame, IndexedFrame? legacy, int width, int height, int paletteId)
            {
                var doubleAlpha = (part.Flags & 0x10) != 0;
                var spriteKey = part.SpriteId | (doubleAlpha ? 0x1000000 : 0);
                var key = (guid, spriteKey, part.Frame);
                if (spriteTextures.TryGetValue(key, out var texture)) return texture;
                var palette = PaletteFor(entry.File, paletteId);
                if (palette is null) return -1;
                if (partRgbaBytesCache.Count > 4096) partRgbaBytesCache.Clear();
                var cacheKey = (entry.File, paletteId, spriteKey | (frame is null ? 0x2000000 : 0), part.Frame);
                var bytes = partRgbaBytesCache.GetOrAdd(cacheKey, _ =>
                {
                    var buffer = new byte[width * height * 4];
                    for (var pixel = 0; pixel < width * height; pixel++)
                    {
                        // Treats 5-bit SPR2 alpha values below 8 as fully transparent.
                        var visible = frame is not null ? frame.Alpha[pixel] >= 8 : legacy!.Opaque[pixel];
                        if (!visible) continue;
                        var color = palette[frame is not null ? frame.Indices[pixel] : legacy!.Indices[pixel]];
                        var at = pixel * 4;
                        buffer[at] = color.Red;
                        buffer[at + 1] = color.Green;
                        buffer[at + 2] = color.Blue;
                        var alpha = frame is null ? (byte)255 : (byte)Math.Clamp((frame.Alpha[pixel] * 255 + 15) / 31, 0, 255);
                        if (doubleAlpha) alpha = alpha < 0x80 ? (byte)(alpha << 1) : (byte)0xff;
                        buffer[at + 3] = alpha;
                    }
                    return buffer;
                });
                var baseName = $"sprite-{guid:x8}-{part.SpriteId}-{part.Frame}.rgba";
                var name = doubleAlpha && files.ContainsKey(baseName)
                    ? $"sprite-{guid:x8}-{part.SpriteId}-{part.Frame}-a2.rgba"
                    : baseName;
                files[name] = bytes;
                texture = textures.Count;
                textures.Add(new Texture(name, width, height, "rgba"));
                spriteTextures[key] = texture;
                return texture;
            }
            int? PartDepth(DrawGroupSprite part, DepthFrame? frame, int width, int height)
            {
                if (frame is null) return null;
                var key = (guid, part.SpriteId, part.Frame);
                if (spriteDepthTextures.TryGetValue(key, out var depthIndex)) return depthIndex;
                if (partDepthBytesCache.Count > 4096) partDepthBytesCache.Clear();
                var depthBytes = partDepthBytesCache.GetOrAdd((entry.File, part.SpriteId, part.Frame), _ =>
                {
                    var srcDepth = frame.Depth;
                    var srcAlpha = frame.Alpha;
                    var buffer = new byte[srcDepth.Length];
                    for (var pixel = 0; pixel < srcDepth.Length; pixel++)
                        buffer[pixel] = srcAlpha[pixel] < 8 ? (byte)255 : srcDepth[pixel];
                    return buffer;
                });
                var depthName = $"sprite-depth-{guid:x8}-{part.SpriteId}-{part.Frame}.depth8";
                files[depthName] = depthBytes;
                depthIndex = textures.Count;
                textures.Add(new Texture(depthName, width, height, "depth8"));
                spriteDepthTextures[key] = depthIndex;
                return depthIndex;
            }
            var emittedAnySprite = false;
            var objectEmittedShadow = false;
            for (var index = 0; index < image.Sprites.Count; index++)
            {
                var sprite = image.Sprites[index];
                if (!DynamicSpriteVisible(sprite.SpriteId, entry.Definition[ObjdField.DynamicSpritesBaseId],
                        entry.Definition[ObjdField.NumDynamicSprites], dynamicSpriteFlags))
                    continue;
                var (frame, legacy, width, height, paletteId) = ReadPart(sprite, report: true);
                if (frame is null && legacy is null) continue;
                if (!FrameHasPixels(width, height)) continue;
                var texture = PartTexture(sprite, frame, legacy, width, height, paletteId);
                if (texture < 0) continue;
                var depthTexture = PartDepth(sprite, frame, width, height);
                var (offsetX, offsetY) = RotateCardinalOffset(direction, sprite.Offset2, sprite.Offset3);
                var offset = new Microsoft.Xna.Framework.Vector3(offsetX, offsetY, sprite.Offset1);
                var point = camera.Project(origin.X + offset.X / 16, origin.Y + offset.Y / 16, origin.Z * 40);
                // Positions DGRP sprite parts by adding DGRP offsets to the projected tile-center anchor
                // minus frame height, omitting internal SPR2 frame offsets to prevent double-counting
                // pivots.
                var spriteX = (int)(point.X + sprite.X);
                var spriteY = (int)(point.Y - offset.Z / 5 * mediumUnitHeight - height + sprite.Y);
                // Scales sprite coordinates across zoom levels where near units are doubled and far units
                // halved, while sprite offsets remain in zoom-native pixels.
                ZoomImage? Zoom(DrawGroupImage? zoomed, float scale)
                {
                    if (zoomed is null || index >= zoomed.Sprites.Count) return null;
                    if (zoomed.Sprites.Count != image.Sprites.Count && index != 0) return null;
                    var other = zoomed.Sprites[index];
                    if ((other.Flags & 1) != (sprite.Flags & 1)) return null;
                    var (otherFrame, otherLegacy, otherWidth, otherHeight, otherPalette) = ReadPart(other, report: false);
                    if (otherFrame is null && otherLegacy is null) return null;
                    if ((otherFrame is not null) != (frame is not null)) return null;
                    if (!FrameHasPixels(otherWidth, otherHeight)) return null;
                    var otherTexture = PartTexture(other, otherFrame, otherLegacy, otherWidth, otherHeight, otherPalette);
                    if (otherTexture < 0) return null;
                    var otherDepth = PartDepth(other, otherFrame, otherWidth, otherHeight);
                    if (otherDepth.HasValue != depthTexture.HasValue) return null;
                    var zoomedX = (int)(scale * point.X + other.X) / scale;
                    var zoomedY = (int)(scale * (point.Y - offset.Z / 5 * mediumUnitHeight) - otherHeight + other.Y) / scale;
                    return new ZoomImage(otherTexture, zoomedX - spriteX, zoomedY - spriteY, otherWidth / scale,
                        otherHeight / scale, otherDepth);
                }
                var depth = depthTexture.HasValue ? SpriteDepth(camera, origin, offset) : (Near: 0f, Far: 0f);
                var sortDepth = camera.DepthOrder(origin.X + offset.X / 16,
                    origin.Y + offset.Y / 16) + 0.001f * origin.Z + (contained ? 0.01f : 0f);
                if (traceSpriteGuid != 0 && guid == traceSpriteGuid &&
                    tracedSpriteResources.Add((id, selectedGraphic, direction)))
                {
                    var sourcePosition = source?.Position;
                    var containerId = source is null ? 0 : source[ObjectData.ContainerId];
                    var slot = source is null ? -1 : source[ObjectData.SlotHierNumber];
                    var sourceParentKey = source is null ? (short)0 : source[ObjectData.ParentId];
                    var managerContainer = source is not null && containerId is > 0 and <= short.MaxValue
                        ? source.ObjectTableEntry((short)containerId)
                        : null;
                    var managerParent = source is not null && sourceParentKey > 0
                        ? source.ObjectTableEntry(sourceParentKey)
                        : null;
                    var idContainer = containerId is > 0 and <= short.MaxValue ? world.ObjectById((short)containerId) : null;
                    var resourceHash = Convert.ToHexString(SHA256.HashData(entry.File.Bytes.Span)).ToLowerInvariant();
                    Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"SPRITE_TRACE guid={guid:x8} id={id} graphic={selectedGraphic} dgrp={groupChunk.Id} resourceSha256={resourceHash} " +
                        $"sourceTile={(sourcePosition is null ? "none" : $"{sourcePosition.Value.X},{sourcePosition.Value.Y},{sourcePosition.Value.Height}")} " +
                        $"container={containerId} slot={slot} contained={contained} direction={direction} camera={camera.Orientation} " +
                        $"parentKey={sourceParentKey} managerContainer={(managerContainer is null ? "none" : $"{managerContainer.Id}/{managerContainer.ClockManagerIndex}/{managerContainer.TypeRecord?.Key:x8}")} " +
                        $"managerParent={(managerParent is null ? "none" : $"{managerParent.Id}/{managerParent.ClockManagerIndex}/{managerParent.TypeRecord?.Key:x8}")} " +
                        $"managerContainerClass={managerContainer?.GetType().Name ?? "none"} " +
                        $"managerContainerPos={(managerContainer is null ? "none" : $"{managerContainer.Position.X},{managerContainer.Position.Y},{managerContainer.Position.Height}")} " +
                        $"managerContainerSlot0={(managerContainer is null || managerContainer.Slots.Count == 0 ? "none" : managerContainer.Slots[0].ToString(CultureInfo.InvariantCulture))} " +
                        $"idContainer={(idContainer is null ? "none" : $"{idContainer.Id}/{idContainer.ClockManagerIndex}/{idContainer.TypeRecord?.Key:x8}")} " +
                        $"origin={origin.X:R},{origin.Y:R},{origin.Z:R} rawDgrpOffset={sprite.Offset2:R},{sprite.Offset3:R},{sprite.Offset1:R} " +
                        $"rotatedOffset={offset.X:R},{offset.Y:R},{offset.Z:R} sortDepth={sortDepth:R} " +
                        $"near={depth.Near:R} far={depth.Far:R}"));
                }
                // Renders object sprites unlit (full brightness) when DGRP flag 0x4 is set, on cloud lots,
                // for active non-portal light sources, or for pool attachments.
                var unlitSprite = (sprite.Flags & 4) != 0 || house == 99 ||
                    (litLightSource && entry.Definition[ObjdField.Type] != 8);
                var room = unlitSprite ? null : entry.Definition[ObjdField.BuildModeType] == 7 ? 65534 : subtileRoom;
                var shadowId = ShadowId(entry, shadowParts);
                if (shadowId is { } rawMask)
                {
                    var turns = (((direction & 7) / 2) + camera.Orientation) & 3;
                    shadowId = RotateShadowId(rawMask, turns);
                }
                var shadowBrightness = (short)entry.Definition[ObjdField.ShadowBrightness];
                var attachShadow = !emittedAnySprite;
                emittedAnySprite = true;
                ZoomImage? FloorShadow(int zoom)
                {
                    if (!attachShadow || shadowId is null || contained ||
                        shadowHidden ||
                        entry.Definition[ObjdField.Type] is 2 or 9)
                        return null;
                    var altitude = (level - 1) * EntityTransform.StoryHeight +
                                   Elevation(origin.X + 0.5f, origin.Y + 0.5f) / 40f;
                    var absolute = FloorShadowAt(shadowId, shadowBrightness, shadowLightByte, zoom,
                        origin.X, origin.Y, altitude);
                    return absolute is null ? null : absolute with { X = absolute.X - spriteX, Y = absolute.Y - spriteY };
                }
                var fsMed = FloorShadow(2);
                var fsNear = FloorShadow(3);
                var fsFar = FloorShadow(1);
                if (fsMed is not null) objectEmittedShadow = true;
                sprites.Add(new SceneSprite(texture, spriteX, spriteY, width, height, (sprite.Flags & 1) != 0, id,
                    sortDepth, 1,
                    depthTexture, depthTexture.HasValue ? depth.Near : null, depthTexture.HasValue ? depth.Far : null,
                    Level: level, Room: room, Near: Zoom(nearImage, 2f), Far: Zoom(farImage, 0.5f),
                    // Single-tile objects use OBJD.Shadow directly, whereas multi-tile objects compute a
                    // composite shadow ID from neighboring parts.
                    Shadow: shadowId,
                    ShadowBrightness: shadowBrightness,
                    FloorShadow: fsMed, FloorShadowNear: fsNear, FloorShadowFar: fsFar));
            }
            if (canCacheObject)
            {
                var emittedCount = sprites.Count - emitStart;
                var emitted = emittedCount == 0 ? Array.Empty<SceneSprite>() : sprites.GetRange(emitStart, emittedCount).ToArray();
                var flagsCopy = dynamicSpriteFlags is null or { Length: 0 } ? null : (short[])dynamicSpriteFlags.Clone();
                kept.ObjectSprites[id] = new CachedObjectSprites(cacheKey, flagsCopy, emitted);
            }
            // Limits floor shadow rendering to at most one object shadow per tile.
            if (objectEmittedShadow &&
                !shadowedObjectTiles.Add((level, (int)MathF.Floor(origin.X), (int)MathF.Floor(origin.Y))))
            {
                for (var k = emitStart; k < sprites.Count; k++)
                {
                    if (sprites[k].FloorShadow is not null)
                        sprites[k] = sprites[k] with { FloorShadow = null, FloorShadowNear = null, FloorShadowFar = null };
                }
            }
        }

        static int? ResolveSubtileRoom(RoomMap? map, float tileX, float tileY, XObject? entity = null)
        {
            if (map is null) return null;
            var room = map.RoomAtPosition(
                Math.Clamp((int)(tileX * 16f), 0, map.Width * 16 - 1),
                Math.Clamp((int)(tileY * 16f), 0, map.Height * 16 - 1));
            if (room == RoomMap.NoRoom && entity is not null && entity.RoomWord84 >= 0 && entity.RoomWord84 != RoomMap.NoRoom)
                return entity.RoomWord84;
            return room;
        }

        void EmitRuntimeSlotSprites(XObject owner, System.Numerics.Vector3? attachmentAnchor = null)
        {
            var source = world.GlobalSprites;
            if (source is null || owner.RuntimeSlots.Count == 0) return;
            var guid = unchecked((uint)(owner.TypeRecord?.Key ?? 0));
            var tileX = attachmentAnchor?.X ?? owner.Position.X / 16f;
            var tileY = attachmentAnchor?.Y ?? owner.Position.Y / 16f;
            var altitude = attachmentAnchor?.Z ?? ((owner.Position.Height - 1) * EntityTransform.StoryHeight +
                           Elevation(tileX + 0.5f, tileY + 0.5f) / 40);
            var point = camera.Project(tileX, tileY, altitude * 40);
            var roomMap = world.LotRoomMaps.GetValueOrDefault(owner.Position.Height);
            var room = house == 99 ? null : ResolveSubtileRoom(roomMap, tileX, tileY, owner);
            for (var slotIndex = 0; slotIndex < owner.RuntimeSlots.Count; slotIndex++)
            {
                var slot = owner.RuntimeSlots[slotIndex];
                if (slot.ReadWord(0x10) == 0) continue;
                if (owner is XPerson person)
                {
                    var isSelectedPerson = (person.Flags & 2u) != 0 || world[SimulationGlobal.SelectedPersonID] == person.Id;
                    if (slot.ReadWord(0x38) == 0 && !isSelectedPerson)
                        continue;
                }
                var spriteId = slot.ReadWord(0x14);
                var rawSlotZ = BitConverter.Int32BitsToSingle(slot.ReadWord(0x08));
                var slotZ = float.IsFinite(rawSlotZ) ? rawSlotZ : 0f;

                bool EmitSprite(int resourceId, int frameIndex, string kind, float depthLayer = 0f,
                    int? nearFrameIndex = null)
                {
                    if (resourceId <= 0 || resourceId > ushort.MaxValue) return false;
                    var chunk = source.Find(ResourceType.Spr1, (ushort)resourceId);
                    if (chunk is null)
                    {
                        missingResources.Add($"runtime-slot:{owner.Id}:{kind}:{resourceId}:chunk-absent");
                        return false;
                    }
                    Sprite art;
                    try { art = CachedSprite(source, chunk); }
                    catch (ResourceFormatException)
                    {
                        missingResources.Add($"runtime-slot:{owner.Id}:{kind}:{resourceId}:read");
                        return false;
                    }
                    if (frameIndex < 0 || frameIndex >= art.Frames.Count)
                    {
                        missingResources.Add($"runtime-slot:{owner.Id}:{kind}:{resourceId}:frame:{frameIndex}:frames:{art.Frames.Count}");
                        return false;
                    }
                    var frame = art.Frames[frameIndex];
                    if (!FrameHasPixels(frame.Width, frame.Height)) return false;
                    int UploadRuntimeSlotFrame(IndexedFrame targetFrame, int targetFrameIndex)
                    {
                        var key = (guid, resourceId, targetFrameIndex);
                        if (spriteTextures.TryGetValue(key, out var cachedTexture)) return cachedTexture;
                        var palette = PaletteFor(source, art.PaletteId);
                        if (palette is null)
                        {
                            missingResources.Add($"runtime-slot:{owner.Id}:{kind}:{resourceId}:palette:{art.PaletteId}");
                            return -1;
                        }
                        var rgba = new byte[targetFrame.Width * targetFrame.Height * 4];
                        for (var pixel = 0; pixel < targetFrame.Width * targetFrame.Height; pixel++)
                        {
                            if (!targetFrame.Opaque[pixel]) continue;
                            var color = palette[targetFrame.Indices[pixel]];
                            var at = pixel * 4;
                            rgba[at] = color.Red;
                            rgba[at + 1] = color.Green;
                            rgba[at + 2] = color.Blue;
                            rgba[at + 3] = 255;
                        }
                        var name = $"runtime-slot-{guid:x8}-{resourceId}-{targetFrameIndex}.rgba";
                        files[name] = rgba;
                        var uploaded = textures.Count;
                        textures.Add(new Texture(name, targetFrame.Width, targetFrame.Height, "rgba"));
                        spriteTextures[key] = uploaded;
                        return uploaded;
                    }
                    var texture = UploadRuntimeSlotFrame(frame, frameIndex);
                    if (texture < 0) return false;
                    // Draws runtime slot overlays in layered order: background balloon shape, followed by
                    // the slot or selector image, and finally the foreground label sprite.
                    var (x, y) = RuntimeSlotSpritePosition(point.X, point.Y, slotZ, frame.Height, frame.BoundsLeft, frame.BoundsTop);
                    ZoomImage? ZoomRuntimeSlotImage(int? zoomFrameIndex, int zoomScale, float zoomFactor)
                    {
                        if (zoomFrameIndex is not { } idx || idx < 0 || idx >= art.Frames.Count) return null;
                        var zoomFrame = art.Frames[idx];
                        if (!FrameHasPixels(zoomFrame.Width, zoomFrame.Height)) return null;
                        var zoomTexture = UploadRuntimeSlotFrame(zoomFrame, idx);
                        if (zoomTexture < 0) return null;
                        var (zx, zy) = RuntimeSlotSpritePosition(point.X * zoomFactor, point.Y * zoomFactor, slotZ,
                            zoomFrame.Height, zoomFrame.BoundsLeft, zoomFrame.BoundsTop, scale: zoomScale);
                        return new ZoomImage(zoomTexture, zx / zoomFactor - x, zy / zoomFactor - y,
                            zoomFrame.Width / zoomFactor, zoomFrame.Height / zoomFactor, null);
                    }
                    var nearImage = ZoomRuntimeSlotImage(nearFrameIndex, zoomScale: 3, zoomFactor: 2f);
                    var objectDepth = camera.DepthOrder(tileX, tileY);
                    sprites.Add(new SceneSprite(texture, x, y, frame.Width, frame.Height, false, owner.Id,
                        objectDepth + 1.5f + slotIndex * 0.0001f + depthLayer, 1, Level: Math.Min(owner.Position.Height, viewLevel),
                        Room: null, Near: nearImage, Far: null, HideAtFarZoom: true));
                    return true;
                }

                bool? productSpeechBuffersAvailable = null;
                (int Texture, int Width, int Height)? LoadSelectorSpeechImage(uint selectorGuid, ObjectEntry selector, bool isPerson, int scale, bool recordMissing)
                {
                    var bitmapId = isPerson
                        ? (scale == 2 ? 2005 : 2006)
                        : selector.Definition[ObjdField.CatalogStringsId] + (scale == 2 ? 2000 : 4000);
                    if (bitmapId is <= 0 or > ushort.MaxValue) return null;
                    var textureKey = (selectorGuid, bitmapId);
                    if (speechImageTextures.TryGetValue(textureKey, out var cachedImage))
                        return cachedImage;

                    var bitmap = selector.File.Find(ResourceType.Parse("BMP_"), (ushort)bitmapId);
                    int width;
                    int height;
                    byte[] rgba;
                    var generated = false;
                    var iconSize = scale == 2 ? 16 : 32;
                    if (bitmap is null && !isPerson &&
                        (productSpeechBuffersAvailable ??= HasProductSpeechBuffers(gameRoot)) &&
                        ObjectCatalog.GenerateProductSpeechIcon(selector, iconSize) is { } generatedIcon)
                    {
                        (width, height, rgba) = generatedIcon;
                        generated = true;
                    }
                    else if (bitmap is not null)
                    {
                        try { (width, height, rgba) = BitmapCodec.Read(bitmap.Data.ToArray()); }
                        catch (InvalidDataException)
                        {
                            if (recordMissing)
                                missingResources.Add($"runtime-slot:{owner.Id}:speech-image:{selectorGuid:x8}:bmp:{bitmapId}:read");
                            return null;
                        }
                        KeySpeechBitmapTransparency(width, height, rgba);
                    }
                    else
                    {
                        if (recordMissing)
                            missingResources.Add($"runtime-slot:{owner.Id}:speech-image:{selectorGuid:x8}:bmp:{bitmapId}:chunk-absent");
                        return null;
                    }

                    if (!FrameHasPixels(width, height)) return null;
                    var name = $"runtime-slot-speech-{selectorGuid:x8}-{bitmapId}{(generated ? "-generated" : "")}.rgba";
                    files[name] = rgba;
                    var texture = textures.Count;
                    textures.Add(new Texture(name, width, height, "rgba"));
                    var image = (texture, width, height);
                    speechImageTextures[textureKey] = image;
                    return image;
                }

                void EmitSelectorSpeechImage(ObjectRuntimeSlot runtimeSlot, int depthSlotIndex)
                {
                    if (runtimeSlot.SelectorTypeKey is not { } selectorKey) return;
                    var selectorGuid = unchecked((uint)selectorKey);
                    var selector = registry.Find(selectorGuid);
                    if (selector is null) return;

                    // Renders speech icons unlit, resolving BMP# 2005/2006 for avatars or CatalogStringsID
                    // + 2000/+4000 for objects at medium and near zoom.
                    var isPerson = selector.Definition[ObjdField.Type] == 2;
                    if (LoadSelectorSpeechImage(selectorGuid, selector, isPerson, scale: 2, recordMissing: true) is not { } image)
                        return;

                    var (x, y) = RuntimeSlotSpeechImagePosition(point.X, point.Y, slotZ, image.Width, image.Height);
                    ZoomImage? nearImage = null;
                    if (LoadSelectorSpeechImage(selectorGuid, selector, isPerson, scale: 3, recordMissing: false) is { } nearSpeech)
                    {
                        var (nearX, nearY) = RuntimeSlotSpeechImagePosition(point.X * 2f, point.Y * 2f, slotZ,
                            nearSpeech.Width, nearSpeech.Height, scale: 3);
                        nearImage = new ZoomImage(nearSpeech.Texture, nearX / 2f - x, nearY / 2f - y,
                            nearSpeech.Width / 2f, nearSpeech.Height / 2f, null);
                    }
                    var objectDepth = camera.DepthOrder(tileX, tileY);
                    sprites.Add(new SceneSprite(image.Texture, x, y, image.Width, image.Height, false, owner.Id,
                        objectDepth + 1.5f + depthSlotIndex * 0.0001f + 0.00002f, 1, Level: Math.Min(owner.Position.Height, viewLevel),
                        Room: null, Near: nearImage, HideAtFarZoom: true));
                }

                // Skips balloon background sprites at far zoom.
                EmitSprite(slot.ReadWord(0x2c), RuntimeSlotOverlayFrame(scale: 2), "balloon",
                    depthLayer: 0f, nearFrameIndex: RuntimeSlotOverlayFrame(scale: 3));

                // Selector-backed slots render dynamic speech icons, whereas standard sprite slots render
                // fixed SPR# frames indexed by animation frame.
                if (spriteId >= 0)
                    EmitSprite(spriteId, RuntimeSlotSpriteFrame(scale: 2, slot.ReadWord(0x20)), "sprite",
                        depthLayer: 0.00002f, nearFrameIndex: RuntimeSlotSpriteFrame(scale: 3, slot.ReadWord(0x20)));
                else
                    EmitSelectorSpeechImage(slot, slotIndex);

                EmitSprite(slot.ReadWord(0x30), RuntimeSlotOverlayFrame(scale: 2), "label",
                    depthLayer: 0.00004f, nearFrameIndex: RuntimeSlotOverlayFrame(scale: 3));
            }
        }

        Lap("meshes");
        void EmitDynamicWorldObject(XObject item, XPerson? carrier)
        {
            if (carrier is not null)
            {
                if (!avatarParts.ContainsKey(carrier.Id)) return;
                // Contained 2D props in avatar slots 1, 3, 4, and 5 render only when a bone registration
                // exists, while slot 2 is reserved for the 3D head indicator.
                var slotHier = item[ObjectData.SlotHierNumber];
                var hasRegistration = slotHier switch
                {
                    1 or 5 => avatarGeometry.HandPosition(carrier).HasValue,
                    3 => avatarGeometry.PelvisPosition(carrier).HasValue,
                    4 => avatarGeometry.HeadAttachmentPosition(carrier).HasValue,
                    _ => false,
                };
                if (!hasRegistration) return;
            }
            else
            {
                if (item.Position.Height < 1 || item.Position.Height > viewLevel) return;
                if (item.Position.X < 0 || item.Position.Y < 0) return;
            }
            // Service objects such as portals, controllers, and phone lines define draw groups without
            // visible artwork and are skipped when the Hidden flag is set.
            if (item[ObjectData.Hidden] != 0) return;
            if (item.Definition[(int)ObjdField.SubIndex] < 0 && item.Definition[(int)ObjdField.MasterId] != 0) return;
            var guid = (uint)(item.TypeRecord?.Key ?? 0);
            if (wallMode == "down" || wallMode == "cutaway")
            {
                var lowered = animating
                    ? item.Position.Height >= 1 && item.Position.Height <= viewLevel &&
                      IsWallObjectLowered(item.Position.Height, item.Position.X >> 4, item.Position.Y >> 4,
                          unchecked((ushort)item[ObjectData.Flags]), unchecked((ushort)item[ObjectData.FlagField2]),
                          item[ObjectData.WallPlacementFlags], item[ObjectData.Direction],
                          unresetPortalFallback: world.ChangedCodeTypeGuids.Contains(guid))
                    : wallObjectIds.Contains(item.Id);
                if (lowered) return;
            }
            if (guid == 2179383033u) return; // Skips the pedestrian portal, which has no visible artwork.
            var entry = registry.Find(guid);
            if (entry is null) return;
            var itemLevel = item.Position.Height >= 1 ? item.Position.Height : (carrier?.Position.Height ?? 1);
            EmitObject(entry, guid, item.Id, item.Position.X / 16f, item.Position.Y / 16f, itemLevel,
                item[ObjectData.Direction], item[ObjectData.Graphic], item.DynamicSpriteFlags, item);
            EmitRuntimeSlotSprites(item);
        }

        var useSubTickCache = animating && directAnimation && subTickAlpha > 0f &&
            updates == kept.LastAnimatedUpdates &&
            kept.CachedNonCarriedObjectSprites is not null &&
            kept.CachedSubTickLights is not null &&
            kept.CachedSubTickWindows is not null;

        if (useSubTickCache)
        {
            sprites.AddRange(kept.CachedNonCarriedObjectSprites!);
            foreach (var item in world.WalkOrder)
            {
                if (item is XPerson) continue;
                if (item[ObjectData.ContainerId] == 0 && item[ObjectData.ParentId] == 0) continue;
                var carrier = (ContainmentParent(world, item) ?? (item[ObjectData.ContainerId] != 0 ? world.ObjectById(item[ObjectData.ContainerId]) : null)) as XPerson;
                if (carrier is not null) EmitDynamicWorldObject(item, carrier);
            }
        }
        else
        {
            List<(XObject Item, XPerson Carrier)>? carriedObjects = null;
            foreach (var item in world.WalkOrder)
            {
                if (item is XPerson) continue;
                var carrier = (ContainmentParent(world, item) ?? (item[ObjectData.ContainerId] != 0 ? world.ObjectById(item[ObjectData.ContainerId]) : null)) as XPerson;
                if (carrier is not null)
                {
                    (carriedObjects ??= []).Add((item, carrier));
                    continue;
                }
                EmitDynamicWorldObject(item, null);
            }
            if (!hasLiveObjects)
            {
                foreach (var door in savedDoors)
                {
                    if (animating && world.ObjectById(door.Id) is not null) continue;
                    if ((wallMode == "down" || wallMode == "cutaway") &&
                        IsWallObjectLowered(door.Level, door.TileX, door.TileY, door.Flags1,
                            door.Flags2 != 0 ? door.Flags2 : 0xc000, door.WallPlacement, door.Direction, unresetPortalFallback: true))
                        continue;
                    EmitObject(door.Entry, door.Guid, door.Id, door.X, door.Y, door.Level, door.Direction,
                        door.Graphic, door.DynamicSpriteFlags);
                }
            }
            if (animating && directAnimation)
            {
                kept.CachedNonCarriedObjectSprites = sprites.Count == 0 ? Array.Empty<SceneSprite>() : sprites.ToArray();
            }
            if (carriedObjects is not null)
            {
                foreach (var (item, carrier) in carriedObjects)
                    EmitDynamicWorldObject(item, carrier);
            }
        }
        Lap("items");
        var censorRects = new List<CensorRect>();
        foreach (var walkEntry in world.WalkOrder)
        {
            if (walkEntry is not XPerson person) continue;
            var visibleOnStairs = person.Position.Height == viewLevel + 1 && person[PersonData.WalkingUpStairs] != 0;
            if (person.Position.X < 0 || person.Position.Y < 0 || person.Position.Height < 1 ||
                (person.Position.Height > viewLevel && !visibleOnStairs) || person[ObjectData.Hidden] != 0) continue;
            var personLevel = person.Position.Height;
            var avatarRoot = avatarGeometry.RootPosition(person);
            var avatarAnchor = avatarGeometry.PelvisPosition(person) ?? avatarRoot;
            var tileX = avatarAnchor.X;
            var tileY = avatarAnchor.Y;
            var personRooms = world.LotRoomMaps.GetValueOrDefault(personLevel);
            var parts = avatarParts.GetValueOrDefault(person.Id) ?? [];
            var isGhostOrLivingGhost = (person[PersonData.IsGhost] & 1) != 0 || (person[PersonData.IsLivingGhost] & 1) != 0;
            var displayFlags = person[PersonData.RenderDisplayFlags];
            // Applies avatar ghost tinting: ghosts use RGBA (17/255, 1, 241/255, 128/255), while display
            // flags bits 1 and 0 select cyan or yellow translucent tints.
            float[]? avatarTint = isGhostOrLivingGhost
                ? [17f / 255f, 1f, 241f / 255f, 128f / 255f]
                : (displayFlags & 2) != 0
                    ? [17f / 255f, 1f, 128f / 255f, 128f / 255f]
                    : (displayFlags & 1) != 0
                        ? [248f / 255f, 248f / 255f, 1f, 128f / 255f]
                        : null;
            ZoomImage? avatarShadow = null;
            ZoomImage? avatarShadowNear = null;
            ZoomImage? avatarShadowFar = null;
            // Suppresses avatar floor shadows for ghosts and living ghosts.
            if (house != 99 && parts.Count > 0 && person[PersonData.Sitting] == 0 && !person.AnimatorPoolTile &&
                (person[ObjectData.Flags] & 4) == 0 && !isGhostOrLivingGhost)
            {
                var groundAltitude = (personLevel - 1) * EntityTransform.StoryHeight + Elevation(tileX, tileY) / 40f;
                var personShadowLight = TileSpriteLightByte(ResolveSubtileRoom(personRooms, tileX, tileY, person));
                static ZoomImage? CompactShadow(ZoomImage? z) => z is null
                    ? null
                    : z with { X = z.X + z.Width * 0.26f, Y = z.Y + z.Height * 0.26f, Width = z.Width * 0.48f, Height = z.Height * 0.48f };
                avatarShadow = CompactShadow(FloorShadowAt(16, 65, personShadowLight, 2, tileX, tileY, groundAltitude));
                avatarShadowNear = CompactShadow(FloorShadowAt(16, 65, personShadowLight, 3, tileX, tileY, groundAltitude));
                avatarShadowFar = CompactShadow(FloorShadowAt(16, 65, personShadowLight, 1, tileX, tileY, groundAltitude));
            }
            var partIndex = 0;
            foreach (var part in parts)
            {
                var name = $"avatar-{person.Id}-{partIndex}-{part.TextureHash}.{part.Encoding}";
                if (!kept.AvatarTextures.TryGetValue(name, out var texture))
                {
                    files[name] = part.Texture;
                    texture = textures.Count;
                    textures.Add(new Texture(name, part.Width, part.Height, part.Encoding));
                    kept.AvatarTextures[name] = texture;
                }
                var sourceVertices = part.Vertices;
                var vertices = new Vertex[sourceVertices.Length];
                for (var vIdx = 0; vIdx < sourceVertices.Length; vIdx++)
                {
                    ref readonly var vertex = ref sourceVertices[vIdx];
                    vertices[vIdx] = new Vertex(vertex.X, vertex.Y, vertex.Z, vertex.U, vertex.V,
                        vertex.Nx, vertex.Ny, vertex.Nz);
                }
                var applyTint = avatarTint is not null && !part.IgnoreRoomLighting;
                var room = part.IgnoreRoomLighting || house == 99 || applyTint
                    ? null
                    : ResolveSubtileRoom(personRooms, tileX, tileY, person);
                meshes.Add(new Mesh(texture, person.Id, camera.DepthOrder(tileX, tileY), vertices,
                    Math.Min(personLevel, viewLevel), room,
                    FloorShadow: partIndex == 0 ? avatarShadow : null,
                    FloorShadowNear: partIndex == 0 ? avatarShadowNear : null,
                    FloorShadowFar: partIndex == 0 ? avatarShadowFar : null,
                    Tint: applyTint ? avatarTint : null));
                partIndex++;
            }
            if (avatarGeometry.Censor(person) is { } censor)
                censorRects.Add(new CensorRect(censor.X, censor.Y, censor.Width, censor.Height, censor.Depth));
            EmitRuntimeSlotSprites(person, avatarGeometry.HeadAttachmentPosition(person) ?? avatarRoot);
        }

        if (!animating && roofsVisible)
        {
            var settings = LotRoof.Read(lot);
            var styles = LotRoof.Styles(gameRoot);
            var styleIndex = styles.ToList().FindIndex(style => string.Equals(style, settings.Style, StringComparison.OrdinalIgnoreCase));
            if (styleIndex < 0 && styles.Count > 0) styleIndex = 0;
            var selectedStyle = styleIndex >= 0 && styleIndex < styles.Count ? styles[styleIndex] : settings.Style;
            var roofPath = LotRoof.MaterialPath(gameRoot, selectedStyle);
            if (styleIndex < 0 || roofPath is null) missingResources.Add($"roof-material:{settings.Style}");
            else
            {
                var bytes = File.ReadAllBytes(roofPath);
                if (bytes.Length < 54 || bytes[0] != 'B' || bytes[1] != 'M')
                    throw new InvalidDataException("Roof material is not a BMP");
                var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18));
                var height = Math.Abs((long)System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22)));
                if (width < 1 || width > 4096 || height < 1 || height > 4096)
                    throw new InvalidDataException("Invalid roof texture dimensions");
                var texture = textures.Count;
                var textureFile = $"roof-{styleIndex}.bmp";
                files[textureFile] = bytes;
                textures.Add(new Texture(textureFile, width, (int)height, "bmp"));
                var material = new RoofSurface("surface", (uint)styleIndex, settings.Pitch, Path.GetFileName(roofPath));
                var totalStories = Stories(lot);
                var roofGrids = new Dictionary<int, RoofGrid>();
                RoofGrid? upperRoof = null;
                for (var buildLevel = totalStories; buildLevel >= 1; buildLevel--)
                {
                    var builtGrid = LotRoofGrid.Build(lot, buildLevel, world.WalkOrder, upperRoof);
                    roofGrids[buildLevel] = builtGrid;
                    upperRoof = builtGrid;
                }
                for (var level = 1; level <= Math.Min(viewLevel, totalStories); level++)
                {
                    var grid = roofGrids[level];
                    var vertices = new List<Vertex>();
                    for (var row = 1; row < Math.Min(grid.Size - 1, lotSize * 2); row++)
                    for (var column = 1; column < Math.Min(grid.Size - 1, lotSize * 2); column++)
                    {
                        if (!grid.Has(row, column)) continue;
                        // Calculates roof vertex elevation using the base tile terrain elevation so
                        // overhanging eaves do not sample neighboring terrain heights.
                        var altitude = level * EntityTransform.StoryHeight + Elevation(column / 2, row / 2) / 40;
                        var placed = RoofCellMesh.Build(grid, row, column, settings.Pitch).Select(vertex =>
                            (Position: new Microsoft.Xna.Framework.Vector3(vertex.X, altitude + vertex.Height, vertex.Y),
                                vertex.U, vertex.V)).ToArray();
                        for (var index = 0; index < placed.Length; index += 3)
                        {
                            var normal = Microsoft.Xna.Framework.Vector3.Cross(placed[index + 1].Position - placed[index].Position,
                                placed[index + 2].Position - placed[index].Position);
                            if (normal.LengthSquared() > 0) normal.Normalize();
                            else normal = Microsoft.Xna.Framework.Vector3.UnitY;
                            normal = camera.CameraNormal(normal);
                            for (var corner = 0; corner < 3; corner++)
                            {
                                var vertex = placed[index + corner];
                                var point = camera.Project(vertex.Position.X, vertex.Position.Z, vertex.Position.Y * 40);
                                var output = new Vertex(point.X, point.Y, camera.Depth(vertex.Position.X, vertex.Position.Z, vertex.Position.Y),
                                    vertex.U, vertex.V, normal.X, normal.Y, normal.Z);
                                vertices.Add(output);
                                meshVertices.Add((output, column / 2, row / 2));
                            }
                        }
                    }
                    if (vertices.Count > 0) meshes.Add(new Mesh(texture, 0, 0, vertices.ToArray(), level,
                        Unlit: false, Layer: 2, Roof: material));
                }
            }
        }

        var lights = new List<SceneLighting.Light>();
        var windows = new List<SceneLighting.Window>();
        SceneLighting.Light[]? cachedLights = null;
        SceneLighting.Window[]? cachedWindows = null;
        Lap("avatars");
        if (useSubTickCache)
        {
            cachedLights = kept.CachedSubTickLights!;
            cachedWindows = kept.CachedSubTickWindows!;
        }
        else
        {
            foreach (var item in world.WalkOrder)
            {
                if (SceneLighting.IsWindow(item))
                    windows.Add(new SceneLighting.Window(item.Position.X >> 4, item.Position.Y >> 4, item.Position.Height));
                if (!SceneLighting.IsLightSource(item) || item.Position.Height > viewLevel) continue;
                var lightX = item.Position.X / 16f;
                var lightY = item.Position.Y / 16f;
                var altitude = (item.Position.Height - 1) * EntityTransform.StoryHeight + Elevation(lightX, lightY) / 40 +
                    SceneLighting.LightLift;
                var point = camera.Project(lightX, lightY, altitude * 40);
                var toward = camera.CameraPoint(lightX, lightY);
                lights.Add(new SceneLighting.Light(point.X, point.Y, toward.X + toward.Y + 0.8f * altitude,
                    item[ObjectData.LightingContribution0100], item.Position.Height));
            }
            if (animating && directAnimation)
            {
                kept.CachedSubTickLights = lights.Count == 0 ? Array.Empty<SceneLighting.Light>() : lights.ToArray();
                kept.CachedSubTickWindows = windows.Count == 0 ? Array.Empty<SceneLighting.Window>() : windows.ToArray();
                kept.LastAnimatedUpdates = updates;
            }
        }

        if (animating)
        {
            Lap("animation");
            if (Environment.GetEnvironmentVariable("SIMS_PROFILE") is not null && sequence % 60 == 0) Console.Error.WriteLine("Animation phases (ms):" + lapText);
            var animatingInMemory = SimsSimulation.InMemorySessionAssets.IsInMemoryPath(directory);
            foreach (var (name, bytes) in files)
            {
                if (!kept.Written.Add(name)) continue;
                var target = Path.Combine(directory, name);
                if (animatingInMemory) SimsSimulation.InMemorySessionAssets.StoreFile(target, bytes);
                else File.WriteAllBytes(target, bytes);
            }
            var dx = kept.ShiftX;
            var dy = kept.ShiftY;
            var lightCount = cachedLights is not null ? cachedLights.Length : lights.Count;
            SceneLighting.Light[] shiftedLights;
            if (lightCount == 0)
            {
                shiftedLights = Array.Empty<SceneLighting.Light>();
            }
            else
            {
                shiftedLights = new SceneLighting.Light[lightCount];
                for (var i = 0; i < lightCount; i++)
                {
                    var light = cachedLights is not null ? cachedLights[i] : lights[i];
                    shiftedLights[i] = light with { X = light.X + dx, Y = light.Y + dy };
                }
            }
            var animatedLighting = baseLighting is { } animatedBuilt
                ? animatedBuilt with
                {
                    Lights = shiftedLights,
                    Windows = cachedWindows is not null
                        ? cachedWindows
                        : windows.Count == 0 ? Array.Empty<SceneLighting.Window>() : windows.ToArray(),
                }
                : null;
            var baseTextureCount = kept.BaseTextureCount;
            var newTextureCount = textures.Count - baseTextureCount;
            Texture[] animatedTextures;
            if (newTextureCount <= 0)
            {
                animatedTextures = Array.Empty<Texture>();
            }
            else
            {
                animatedTextures = new Texture[newTextureCount];
                textures.CopyTo(baseTextureCount, animatedTextures, 0, newTextureCount);
            }

            SceneSprite[] animatedSprites;
            if (sprites.Count == 0)
            {
                animatedSprites = Array.Empty<SceneSprite>();
            }
            else
            {
                var order = new (float Depth, int Index)[sprites.Count];
                var count = 0;
                for (var i = 0; i < sprites.Count; i++)
                {
                    var candidate = sprites[i];
                    if (candidate.ObjectID <= 0) continue;
                    order[count++] = (candidate.Depth, i);
                }
                Array.Sort(order, 0, count);
                animatedSprites = new SceneSprite[count];
                for (var i = 0; i < count; i++)
                {
                    var sprite = sprites[order[i].Index];
                    animatedSprites[i] = sprite with { X = sprite.X + dx, Y = sprite.Y + dy };
                }
            }

            var dynamicMeshCount = 0;
            for (var i = 0; i < meshes.Count; i++)
                if (meshes[i].ObjectID > 0) dynamicMeshCount++;
            if (directAnimation)
            {
                var directMeshes = dynamicMeshCount == 0
                    ? Array.Empty<DirectAnimationMesh>()
                    : new DirectAnimationMesh[dynamicMeshCount];
                var directOut = 0;
                for (var i = 0; i < meshes.Count; i++)
                {
                    var mesh = meshes[i];
                    if (mesh.ObjectID <= 0) continue;
                    directMeshes[directOut++] = new DirectAnimationMesh(
                        mesh.Texture, mesh.ObjectID, mesh.Depth, mesh.Level, mesh.Room,
                        ShiftAnimationShadow(mesh.FloorShadow),
                        ShiftAnimationShadow(mesh.FloorShadowNear),
                        ShiftAnimationShadow(mesh.FloorShadowFar),
                        mesh.Vertices, dx, dy, mesh.Tint);
                }

                CensorRect[] directCensors;
                if (censorRects.Count == 0)
                {
                    directCensors = Array.Empty<CensorRect>();
                }
                else
                {
                    directCensors = new CensorRect[censorRects.Count];
                    for (var i = 0; i < censorRects.Count; i++)
                    {
                        var rect = censorRects[i];
                        directCensors[i] = rect with { X = rect.X + dx, Y = rect.Y + dy };
                    }
                }

                return new DirectAnimationFrame(
                    revision, sequence, updates, baseTextureCount, animatedLighting,
                    animatedTextures, animatedSprites, directMeshes, directCensors);
            }

            var animatedMeshes = new object[dynamicMeshCount];
            var meshOut = 0;
            for (var i = 0; i < meshes.Count; i++)
            {
                var mesh = meshes[i];
                if (mesh.ObjectID <= 0) continue;
                animatedMeshes[meshOut++] = new
                {
                    mesh.Texture, mesh.ObjectID, mesh.Depth, mesh.Level, mesh.Room,
                    FloorShadow = ShiftAnimationShadow(mesh.FloorShadow),
                    FloorShadowNear = ShiftAnimationShadow(mesh.FloorShadowNear),
                    FloorShadowFar = ShiftAnimationShadow(mesh.FloorShadowFar),
                    tint = mesh.Tint,
                    vertices = PackVertices(mesh.Vertices, dx, dy),
                };
            }

            CensorRect[] animatedCensorRects;
            if (censorRects.Count == 0)
            {
                animatedCensorRects = Array.Empty<CensorRect>();
            }
            else
            {
                animatedCensorRects = new CensorRect[censorRects.Count];
                for (var i = 0; i < censorRects.Count; i++)
                {
                    var rect = censorRects[i];
                    animatedCensorRects[i] = rect with { X = rect.X + dx, Y = rect.Y + dy };
                }
            }

            return new
            {
                schemaVersion = 1,
                baseRevision = revision,
                sequence,
                updates,
                textureStart = baseTextureCount,
                lighting = animatedLighting,
                missingResources = missingResources.Count == 0 ? Array.Empty<string>() : missingResources.Order().ToArray(),
                textures = animatedTextures,
                sprites = animatedSprites,
                meshes = animatedMeshes,
                censorRects = animatedCensorRects,
            };

            ZoomImage? ShiftAnimationShadow(ZoomImage? shadow) => shadow is null
                ? null
                : shadow with { X = shadow.X + dx, Y = shadow.Y + dy };
        }

        var placementLift = (viewLevel - 1) * EntityTransform.StoryHeight * 40;
        var meshShadows = meshes.SelectMany(mesh => new[] { mesh.FloorShadow, mesh.FloorShadowNear, mesh.FloorShadowFar })
            .OfType<ZoomImage>()
            .ToArray();
        var allX = terrain.Select(vertex => vertex.X)
            .Concat(meshVertices.Select(item => item.Vertex.X))
            .Concat(sprites.Select(sprite => sprite.X))
            .Concat(sprites.Select(sprite => sprite.X + sprite.Width))
            .ToArray();
        var allY = terrain.Select(vertex => vertex.Y)
            .Concat(terrain.Select(vertex => vertex.Y - placementLift))
            .Concat(meshVertices.Select(item => item.Vertex.Y))
            .Concat(sprites.Select(sprite => sprite.Y))
            .Concat(sprites.Select(sprite => sprite.Y + sprite.Height))
            .ToArray();
        var minX = allX.Min();
        var minY = allY.Min();
        var shiftX = 2 - minX;
        var shiftY = 2 - minY;
        var maxX = allX.Concat(meshes.SelectMany(mesh => mesh.Vertices).Select(vertex => vertex.X))
            .Concat(meshShadows.Select(shadow => shadow.X + shadow.Width))
            .Max();
        var maxY = allY.Concat(meshes.SelectMany(mesh => mesh.Vertices).Select(vertex => vertex.Y))
            .Concat(meshShadows.Select(shadow => shadow.Y + shadow.Height))
            .Max();
        var sceneWidth = maxX - minX + 4;
        var sceneHeight = maxY - minY + 4;
        terrain = terrain.Select(vertex => vertex with { X = vertex.X + shiftX, Y = vertex.Y + shiftY }).ToList();
        var viewWalls = ReadWalls(lot, viewLevel);
        var floorDiagonals = new int[lotSize * lotSize];
        if (viewWalls is not null)
        {
            for (var y = 0; y < lotSize; y++)
            for (var x = 0; x < lotSize; x++)
            {
                if (x < viewWalls.Rows && y < viewWalls.Width)
                    floorDiagonals[y * lotSize + x] = viewWalls.Element(x, y)[0] & 0x30;
            }
        }
        ZoomImage? ShiftShadow(ZoomImage? shadow) => shadow is null
            ? null
            : shadow with { X = shadow.X + shiftX, Y = shadow.Y + shiftY };
        meshes = meshes.Select(mesh => mesh with
        {
            Vertices = mesh.Vertices.Select(vertex => vertex with { X = vertex.X + shiftX, Y = vertex.Y + shiftY }).ToArray(),
            FloorShadow = ShiftShadow(mesh.FloorShadow),
            FloorShadowNear = ShiftShadow(mesh.FloorShadowNear),
            FloorShadowFar = ShiftShadow(mesh.FloorShadowFar),
        }).ToList();
        sprites = sprites.Select(sprite => sprite with { X = sprite.X + shiftX, Y = sprite.Y + shiftY }).ToList();
        censorRects = censorRects.Select(rect => rect with { X = rect.X + shiftX, Y = rect.Y + shiftY }).ToList();
        Lap("sprites");
        var lighting = baseLighting is { } built
            ? built with
            {
                Lights = lights.Select(light => light with { X = light.X + shiftX, Y = light.Y + shiftY }).ToArray(),
                Windows = windows.ToArray(),
            }
            : null;

        var inMemory = SimsSimulation.InMemorySessionAssets.IsInMemoryPath(directory);
        if (!inMemory) Directory.CreateDirectory(directory);
        Lap("lighting");
        var previousWritten = retain?.Kept is Kept prevKept ? prevKept.Written : null;
        var prunedOldRevisionFiles = false;
        if (inMemory)
        {
            if (previousWritten is not null && SimsSimulation.InMemorySessionAssets.TotalBytesUnder(directory) > 32L * 1024 * 1024)
            {
                foreach (var stale in previousWritten)
                {
                    if (!files.ContainsKey(stale))
                    {
                        SimsSimulation.InMemorySessionAssets.DeleteFile(Path.Combine(directory, stale));
                    }
                }
                prunedOldRevisionFiles = true;
            }
            foreach (var pair in files)
            {
                if (previousWritten is not null && previousWritten.Contains(pair.Key)) continue;
                SimsSimulation.InMemorySessionAssets.StoreFile(Path.Combine(directory, pair.Key), pair.Value);
            }
        }
        else
        {
            Parallel.ForEach(files, pair =>
            {
                if (previousWritten is not null && previousWritten.Contains(pair.Key)) return;
                var target = Path.Combine(directory, pair.Key);
                if (new FileInfo(target) is { Exists: true } existing && existing.Length == pair.Value.Length) return;
                File.WriteAllBytes(target, pair.Value);
            });
        }
        var path = Path.Combine(directory, $"scene-{revision}.json");
        var soundSources = world.PositionalSoundOwners
            .Select(id => world.ObjectById(id))
            .Where(item => item is not null && item.Position.Height >= 1)
            .Select(item =>
            {
                var source = item!;
                var tileX = source.Position.X / 16f + 0.5f;
                var tileY = source.Position.Y / 16f + 0.5f;
                var altitude = (source.Position.Height - 1) * EntityTransform.StoryHeight + Elevation(tileX, tileY) / 40f;
                return new SoundSourceRecord(source.Id, source.Position.Height, tileX, tileY, altitude * 40f);
            }).ToArray();
        var exported = new ExportedLotScene(
            house,
            sceneWidth,
            sceneHeight,
            lotSize,
            floorDiagonals,
            TerrainHeights(lot, lotSize),
            camera.Orientation,
            -shiftX,
            -shiftY,
            wallMode,
            roofsVisible,
            viewLevel,
            Stories(lot),
            lighting,
            textures.ToArray(),
            sprites.ToArray(),
            soundSources,
            terrain.ToArray(),
            placementLift,
            meshes.ToArray(),
            censorRects.ToArray(),
            ["native engine scene"],
            missingResources.Order().ToArray());
        static object BuildJsonScene(ExportedLotScene payload) => new
        {
            schemaVersion = 1,
            house = payload.House,
            width = payload.Width,
            height = payload.Height,
            lotWidth = payload.LotSize,
            lotHeight = payload.LotSize,
            floorDiagonals = payload.FloorDiagonals,
            terrainHeights = payload.TerrainHeights,
            cameraOrientation = payload.CameraOrientation,
            originX = payload.OriginX,
            originY = payload.OriginY,
            wallMode = payload.WallMode,
            wallsUp = payload.WallMode == "up",
            roofsVisible = payload.RoofsVisible,
            viewLevel = payload.ViewLevel,
            stories = payload.Stories,
            lighting = payload.Lighting,
            textures = payload.Textures,
            sprites = payload.Sprites,
            soundSources = payload.SoundSources.Select(s => new { objectID = s.ObjectID, level = s.Level, x = s.X, y = s.Y, z = s.Z }).ToArray(),
            placementVertices = payload.Terrain.Select(vertex => new
            {
                x = vertex.X, y = vertex.Y - payload.PlacementLift, r = vertex.R, g = vertex.G, b = vertex.B,
                dr = vertex.Dr, dg = vertex.Dg, db = vertex.Db, grass = vertex.Grass,
            }).ToArray(),
            terrainVertices = payload.Terrain.Select((vertex, index) =>
            {
                var tileIndex = index / 6;
                var tx = tileIndex % payload.LotSize;
                var ty = tileIndex / payload.LotSize;
                var v = (tx == 0 || tx == payload.LotSize - 1 || ty == 0 || ty == payload.LotSize - 1) ? payload.Terrain[tileIndex * 6] : vertex;
                return new
                {
                    x = v.X, y = v.Y, r = v.R, g = v.G, b = v.B,
                    dr = v.Dr, dg = v.Dg, db = v.Db, grass = v.Grass,
                };
            }).ToArray(),
            meshes = payload.Meshes,
            censorRects = payload.CensorRects,
            limitations = payload.Limitations,
            missingResources = payload.MissingResources,
        };
        Lap("write");
        if (retain is not null)
        {
            kept.Textures = textures;
            kept.BaseTextureCount = textures.Count;
            kept.SpriteTextures = spriteTextures;
            kept.SpeechImageTextures = speechImageTextures;
            kept.SpriteDepthTextures = spriteDepthTextures;
            kept.FloorShadowTextures = floorShadowTextures;
            var mergedWritten = previousWritten is not null && !prunedOldRevisionFiles
                ? new HashSet<string>(previousWritten, StringComparer.Ordinal)
                : new HashSet<string>(files.Count, StringComparer.Ordinal);
            foreach (var key in files.Keys) mergedWritten.Add(key);
            kept.Written = mergedWritten;
            kept.Heights = heights;
            kept.BaseHeight = baseHeight;
            kept.CutawayMask = cutawayMask;
            kept.WallAdjacentTiles = wallAdjacentTiles;
            kept.ViewLevel = viewLevel;
            kept.WallMode = wallMode;
            kept.CameraOrientation = cameraOrientation;
            kept.CutawayTile = cutawayTile;
            kept.CutawayRooms = cutawayRooms is null or { Count: 0 } ? [] : [.. cutawayRooms];
            kept.CutawaySim = cutawaySim;
            kept.CutawayZoom = cutawayZoom;
            kept.WallObjectIds = wallObjectIds;
            kept.SavedDoors = savedDoors;
            kept.ShiftX = shiftX;
            kept.ShiftY = shiftY;
            retain.Revision = revision;
            retain.Kept = kept;
        }
        if (inMemory)
        {
            SimsSimulation.InMemorySessionAssets.StoreScene(path, exported,
                () => JsonSerializer.SerializeToUtf8Bytes(BuildJsonScene(exported), Json));
            SimsSimulation.InMemorySessionAssets.PruneOldScenes(directory, revision - 3);
        }
        else
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 65536))
            {
                JsonSerializer.Serialize(stream, BuildJsonScene(exported), Json);
            }
        }
        Lap("json");
        if (Environment.GetEnvironmentVariable("SIMS_PROFILE") is not null) Console.Error.WriteLine("Export phases (ms):" + lapText);
        return path;
    }

    private static readonly ConcurrentDictionary<(IffFile File, ushort Id), DrawGroup> groupCache = new();
    private static readonly ConcurrentDictionary<(IffFile File, ushort Id), DepthSprite> depthSpriteCache = new();
    private static readonly ConcurrentDictionary<(IffFile File, ushort Id), Sprite> spriteCache = new();
    private static readonly ConcurrentDictionary<(IffFile File, int PaletteId, int SpriteKey, int Frame), byte[]> partRgbaBytesCache = new();
    private static readonly ConcurrentDictionary<(IffFile File, int SpriteId, int Frame), byte[]> partDepthBytesCache = new();
    private static readonly ConcurrentDictionary<(IffFile File, int Shadow, int Attenuation, int Zoom), (int Width, int Height, byte[]? Bytes)> floorShadowBytesCache = new();
    private static readonly ConcurrentDictionary<(int Configuration, int Width, int Height), byte[]> archDepthBytesCache = new();

    private static T Cached<T>(ConcurrentDictionary<(IffFile File, ushort Id), T> cache, IffFile file, IffChunk chunk, Func<T> read) =>
        cache.GetOrAdd((file, chunk.Id), _ => read());

    private static DrawGroup CachedGroup(IffFile file, IffChunk chunk) => Cached(groupCache, file, chunk, () => DrawGroup.Read(file, chunk));
    private static DepthSprite CachedDepthSprite(IffFile file, IffChunk chunk) => Cached(depthSpriteCache, file, chunk, () => DepthSprite.Read(file, chunk));
    private static Sprite CachedSprite(IffFile file, IffChunk chunk) => Cached(spriteCache, file, chunk, () => Sprite.Read(file, chunk));

    private static byte[] PackVertices(Vertex[] vertices, float shiftX, float shiftY)
    {
        var data = new byte[vertices.Length * 32];
        var span = data.AsSpan();
        var offset = 0;
        for (var i = 0; i < vertices.Length; i++)
        {
            ref readonly var vertex = ref vertices[i];
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset, 4), vertex.X + shiftX);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 4, 4), vertex.Y + shiftY);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 8, 4), vertex.Z);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 12, 4), vertex.U);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 16, 4), vertex.V);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 20, 4), vertex.Nx);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 24, 4), vertex.Ny);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 28, 4), vertex.Nz);
            offset += 32;
        }
        return data;
    }

    private static readonly ConcurrentDictionary<string, IffFile> artworkFiles = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, IffFile?> optionalArtworkFiles = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Dictionary<int, string>> dynamicFloors = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Dictionary<int, string>> dynamicWalls = new(StringComparer.Ordinal);

    private static IffFile OpenArtwork(string path) =>
        artworkFiles.GetOrAdd(path, static p => IffFile.Open(p));

    private static IffFile? TryOpenOptionalArtwork(string path) =>
        optionalArtworkFiles.GetOrAdd(path, static p => File.Exists(p) ? OpenArtwork(p) : null);

    private static Dictionary<int, string> DynamicFloorFiles(string gameData) =>
        dynamicFloors.GetOrAdd(gameData, static gd => FloorCatalog.DynamicFiles(gd).ToDictionary());

    private static Dictionary<int, string> DynamicWallFiles(string gameData) =>
        dynamicWalls.GetOrAdd(gameData, static gd => WallpaperCatalog.DynamicFiles(gd).ToDictionary());

    private static float[] ReadHeights(IffFile lot)
    {
        var heights = new float[Size * Size];
        var array = ReadArray(lot, 0);
        if (array is null) return heights;
        for (var x = 0; x < Size; x++)
        for (var y = 0; y < Size; y++)
            heights[x * Size + y] = array.Element(x, y)[1] * 10;
        return heights;
    }

    /// <summary>The raw vertex heights (0 to 64) of the lot's Arry 0 chunk, ordered row-major as y * size +
    /// x.</summary>
    private static short[] TerrainHeights(IffFile lot, int lotSize)
    {
        var result = new short[lotSize * lotSize];
        if (ReadArray(lot, 0) is not { } array) return result;
        for (var y = 0; y < lotSize; y++)
        for (var x = 0; x < lotSize; x++)
            result[y * lotSize + x] = array.Element(x, y)[1];
        return result;
    }

    private static byte[] ReadGrass(IffFile lot)
    {
        var grass = new byte[Size * Size];
        Array.Fill(grass, (byte)127);
        var array = ReadArray(lot, 6);
        if (array is null) return grass;
        for (var x = 0; x < Size; x++)
        for (var y = 0; y < Size; y++)
            // Arry 6 stores grass state as a signed delta subtracted from 127, where a raw byte of 255
            // represents grass state 128.
            grass[x * Size + y] = unchecked((byte)(127 - array.Element(x, y)[0]));
        return grass;
    }

    private static ushort[] ReadFloors(IffFile lot, int level)
    {
        var floors = new ushort[Size * Size];
        var advanced = ReadArray(lot, level == 1 ? 11 : 111);
        var standard = ReadArray(lot, level == 1 ? 1 : 101);
        if (advanced is not null && advanced.ElementSize >= 2)
        {
            for (var x = 0; x < Size; x++)
            for (var y = 0; y < Size; y++)
                floors[x * Size + y] = BitConverter.ToUInt16(advanced.Element(x, y)[..2]);
        }
        else if (standard is not null)
        {
            for (var x = 0; x < Size; x++)
            for (var y = 0; y < Size; y++)
                floors[x * Size + y] = standard.Element(x, y)[0];
        }
        if (level == 1)
        {
            var pool = ReadArray(lot, 9);
            var water = ReadArray(lot, 10);
            var waterSentinel = water is not null && water.Element(0, 0)[0] == 0xff ? (byte)0xff : (byte)0;
            for (var x = 0; x < Size; x++)
            for (var y = 0; y < Size; y++)
            {
                if (pool is not null && pool.Element(x, y)[0] < 0x10)
                    floors[x * Size + y] = 65535;
                else if (pool is not null && pool.Element(x, y)[0] == 0x10 && floors[x * Size + y] == 0)
                    floors[x * Size + y] = 12;
                else if (water is not null && water.Element(x, y)[0] != waterSentinel && floors[x * Size + y] == 0)
                    floors[x * Size + y] = 65534;
            }
        }
        return floors;
    }

    internal static XObject? ContainmentParent(SimulationWorld world, XObject entity)
    {
        var key = entity[ObjectData.ContainerId] > 0
            ? entity[ObjectData.ContainerId]
            : entity[ObjectData.SlotHierNumber] > 0 && entity[ObjectData.ParentId] > 0
                ? entity[ObjectData.ParentId]
                : (short)0;
        if (key <= 0) return null;
        // Container identifiers store object table slot keys rather than raw object IDs, requiring table
        // lookup rather than direct object ID resolution.
        return entity.ObjectTableEntry(key) ?? world.ObjectTableEntry(key);
    }

    /// <summary>Determines whether an attached object targets the head bone slot, resolving logical object
    /// slot 1 located at slot array index 2 due to an initial sentinel record.</summary>
    internal static bool HeadSlotTargets(SimulationWorld world, XPerson owner, XObject attached)
    {
        if (owner.Slots.Count <= 2) return false;
        var key = owner.Slots[2];
        return key != 0 && ReferenceEquals(owner.ObjectTableEntry(key) ?? world.ObjectTableEntry(key), attached);
    }

    internal static Microsoft.Xna.Framework.Vector3 ResolveObjectPosition(XObject entity, XObject? container,
        Func<ObjectPosition, Microsoft.Xna.Framework.Vector3> rootPosition,
        Func<XPerson, Microsoft.Xna.Framework.Vector3> headPosition,
        Func<XObject, int, Microsoft.Xna.Framework.Vector3> authoredSlotPosition,
        Func<XPerson, Microsoft.Xna.Framework.Vector3>? handPosition = null,
        Func<XPerson, Microsoft.Xna.Framework.Vector3>? pelvisPosition = null,
        Func<XPerson, Microsoft.Xna.Framework.Vector3>? headAttachmentPosition = null)
    {
        var hierarchySlot = entity[ObjectData.SlotHierNumber];
        // Contained objects map hierarchy slots to person bones: slots 1 and 5 to the right hand, slot 2 to
        // the head bone or authored slot 1, slot 3 to the pelvis, and slot 4 to head attachment
        // registration.
        if (hierarchySlot == 2 && container is XPerson person && entity is XPerson)
            return headPosition(person);
        if (hierarchySlot is 1 or 5 && container is XPerson handSim && entity is not XPerson && handPosition != null)
            return handPosition(handSim);
        if (hierarchySlot == 3 && container is XPerson pelvisSim && entity is not XPerson && pelvisPosition != null)
            return pelvisPosition(pelvisSim);
        if (hierarchySlot == 4 && container is XPerson headSim && entity is not XPerson && headAttachmentPosition != null)
            return headAttachmentPosition(headSim);
        if (hierarchySlot > 0 && container is not null)
            return authoredSlotPosition(container, hierarchySlot - 1);
        return rootPosition(entity.Position);
    }

    internal static bool IsHierarchyContained(XObject? entity) =>
        entity is not null && entity[ObjectData.SlotHierNumber] > 0 &&
        (entity[ObjectData.ContainerId] > 0 || entity[ObjectData.ParentId] > 0);

    private static (float X, float Y) RotateCardinalOffset(int direction, float dx, float dy) => (direction & 7) switch
    {
        0 => (dx, dy),
        2 => (-dy, dx),
        4 => (-dx, -dy),
        6 => (dy, -dx),
        _ => (0f, 0f),
    };

    private static (float Near, float Far) SpriteDepth(LotCamera camera, Microsoft.Xna.Framework.Vector3 centre,
        Microsoft.Xna.Framework.Vector3 offset)
    {
        const float HalfStep = 1f / 97.97959f; // 1 / (40 * sqrt(6))
        var anchor = camera.CameraPoint(centre.X + offset.X / 16f, centre.Y + offset.Y / 16f);
        var altitude = centre.Z + offset.Z / 5f;
        return (SceneDepth.Project(anchor.X + 200f * HalfStep, anchor.Y + 200f * HalfStep, altitude),
            SceneDepth.Project(anchor.X - 54f * HalfStep, anchor.Y - 54f * HalfStep, altitude));
    }

    private static readonly ConcurrentDictionary<(IffFile File, int PaletteId), PaletteColor[]?> objectPaletteCache = new();

    internal static PaletteColor[]? PaletteFor(IffFile file, int paletteId) =>
        objectPaletteCache.GetOrAdd((file, paletteId), static key =>
        {
            // SPR2 and SPR# headers store PALT resource chunk IDs rather than palette indices, and object
            // palettes frequently use chunk IDs above 1000.
            var chunk = key.PaletteId is >= 0 and <= ushort.MaxValue
                ? key.File.Find(ResourceType.Palt, (ushort)key.PaletteId)
                : null;
            chunk ??= key.File.OfType(ResourceType.Palt).FirstOrDefault();
            return chunk is null ? null : Palette.Read(key.File, chunk).Colors.ToArray();
        });

    private static LotArray? ReadWalls(IffFile lot, int level) => ReadArray(lot, level == 1 ? 2 : 102);

    private static LotArray? ReadArray(IffFile lot, int id)
    {
        var chunk = lot.Find(ResourceType.Parse("Arry"), (ushort)id);
        return chunk is null ? null : LotArray.Read(lot, chunk);
    }
}
