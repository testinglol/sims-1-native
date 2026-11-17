using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace SimsSDL.Core;

public sealed record BuyCatalogPage
{
    public sealed record Item
    {
        [JsonConstructor]
        public Item() { }

        [JsonPropertyName("guid")]
        public required uint Guid { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("description")]
        public required string Description { get; init; }

        [JsonPropertyName("price")]
        public required int Price { get; init; }

        [JsonPropertyName("category")]
        public required int Category { get; init; }

        [JsonPropertyName("icon")]
        public GameplayPanelState.Image? Icon { get; init; }

        [JsonPropertyName("preview")]
        public GameplayPanelState.Image? Preview { get; init; }

        [JsonPropertyName("ratings")]
        public IReadOnlyList<string>? Ratings { get; init; }
    }

    [JsonConstructor]
    public BuyCatalogPage() { }

    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("category")]
    public required int Category { get; init; }

    [JsonPropertyName("room")]
    public int? Room { get; init; }

    /// <summary>Item counts per function category; sub-sort category buttons are enabled only when their
    /// respective count is greater than zero.</summary>
    [JsonPropertyName("functionCounts")]
    public IReadOnlyList<int>? FunctionCounts { get; init; }

    [JsonPropertyName("subsort")]
    public int? Subsort { get; init; }

    [JsonPropertyName("functionSubsortCounts")]
    public IReadOnlyList<int>? FunctionSubsortCounts { get; init; }

    [JsonPropertyName("offset")]
    public required int Offset { get; init; }

    [JsonPropertyName("total")]
    public required int Total { get; init; }

    [JsonPropertyName("items")]
    public required IReadOnlyList<Item> Items { get; init; }

    public static BuyCatalogPage FromJson(string json) =>
        JsonSerializer.Deserialize<BuyCatalogPage>(json) ?? throw new ContentError("Buy catalog JSON decoded to null");

    public int? NextOffset(int visibleCapacity)
    {
        int count = Math.Min(Items.Count, Math.Max(0, visibleCapacity));
        return count > 0 && Offset + count < Total ? Offset + count : null;
    }

    /// <summary>Orders catalog items primarily by price, breaking ties by name and resolving any remaining
    /// ties by GUID.</summary>
    private static bool Ordered(Item previous, Item item)
    {
        if (previous.Price != item.Price)
            return previous.Price < item.Price;
        if (!string.Equals(previous.Name, item.Name, StringComparison.Ordinal))
            return string.CompareOrdinal(previous.Name, item.Name) < 0;
        return previous.Guid < item.Guid;
    }

    public void Validate(bool buildObjects = false)
    {
        bool validCategory = buildObjects
            ? Category is >= 8 and <= 12
            : Room is null ? Category is >= 0 and <= 7 : Category is >= -1 and <= 7;

        if (SchemaVersion != 1 || !validCategory
            || (Room is int roomVal && roomVal is < 0 or > 7)
            || (Subsort is int subVal && (subVal is < 0 or > 7 || Room is not null || Category < 0))
            || (FunctionCounts is not null && (FunctionCounts.Count != 8 || FunctionCounts.Any(c => c < 0)))
            || (FunctionSubsortCounts is not null && (FunctionSubsortCounts.Count != 8 || FunctionSubsortCounts.Any(c => c < 0) || Room is not null || Category < 0))
            || Offset is < 0 or > 1_000_000
            || Total is < 0 or > 1_000_000
            || Items is null || Items.Count > 64
            || Items.Count > Math.Max(0, Total - Offset)
            || (Items.Count == 0 && Offset < Total))
        {
            throw new ContentError("Invalid buy catalog page");
        }

        var guids = new HashSet<uint>(Items.Count);
        foreach (var item in Items)
        {
            if (item is null || !guids.Add(item.Guid))
                throw new ContentError("Invalid buy catalog page");
        }

        Item? previous = null;
        foreach (var item in Items)
        {
            if (item.Guid == 0 || (Category != -1 && item.Category != Category) || item.Price < 0
                || item.Name is null || item.Name.Length > 512
                || item.Description is null || item.Description.Length > 4096)
            {
                throw new ContentError("Invalid buy catalog item");
            }

            if (previous is not null && !Ordered(previous, item))
                throw new ContentError("Buy catalog items are out of order");

            item.Icon?.Validate();
            item.Preview?.Validate();
            previous = item;
        }
    }
}

public sealed record BuildCatalogPage
{
    [JsonConverter(typeof(StrictCamelCaseEnumConverter<CatalogKind>))]
    public enum CatalogKind
    {
        Floor,
        Wall,
        WallStyle,
        Water,
        Roof
    }

    public readonly record struct Target
    {
        private Target(bool isObject, uint value)
        {
            IsObject = isObject;
            Value = value;
        }

        public bool IsObject { get; }
        public bool IsPattern => !IsObject;
        public uint Value { get; }
        public ushort PatternId => (ushort)Value;
        public uint ObjectGuid => Value;

        public static Target Pattern(ushort id) => new(false, id);
        public static Target Object(uint guid) => new(true, guid);
    }

    public sealed record Item
    {
        [JsonConstructor]
        public Item() { }

        [JsonPropertyName("id")]
        public required ushort Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("description")]
        public required string Description { get; init; }

        [JsonPropertyName("price")]
        public required int Price { get; init; }

        [JsonPropertyName("file")]
        public required string File { get; init; }

        [JsonPropertyName("pattern")]
        public ushort? Pattern { get; init; }

        [JsonPropertyName("guid")]
        public uint? Guid { get; init; }

        [JsonIgnore]
        public BuildCatalogPage.Target Target => Guid is uint guid
            ? BuildCatalogPage.Target.Object(guid)
            : BuildCatalogPage.Target.Pattern(Id);

        [JsonIgnore]
        public ulong SortKey => Guid is uint guid ? guid + 65536UL : Id;

        [JsonPropertyName("icon")]
        public GameplayPanelState.Image? Icon { get; init; }

        [JsonPropertyName("preview")]
        public GameplayPanelState.Image? Preview { get; init; }
    }

    [JsonConstructor]
    public BuildCatalogPage() { }

    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("kind")]
    public required CatalogKind Kind { get; init; }

    [JsonPropertyName("offset")]
    public required int Offset { get; init; }

    [JsonPropertyName("total")]
    public required int Total { get; init; }

    [JsonPropertyName("items")]
    public required IReadOnlyList<Item> Items { get; init; }

    public static BuildCatalogPage FromJson(string json) =>
        JsonSerializer.Deserialize<BuildCatalogPage>(json) ?? throw new ContentError("Build catalog JSON decoded to null");

    public int? NextOffset(int visibleCapacity)
    {
        int count = Math.Min(Items.Count, Math.Max(0, visibleCapacity));
        return count > 0 && Offset + count < Total ? Offset + count : null;
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || Offset is < 0 or > 1_000_000 || Total is < 0 or > 1_000_000
            || Items is null || Items.Count > 64 || Items.Count > Math.Max(0, Total - Offset)
            || (Items.Count == 0 && Offset < Total))
        {
            throw new ContentError("Invalid build catalog page");
        }

        var targets = new HashSet<Target>(Items.Count);
        foreach (var item in Items)
        {
            if (item is null || !targets.Add(item.Target))
                throw new ContentError("Invalid build catalog page");
        }

        Item? previous = null;
        foreach (var item in Items)
        {
            if (Kind == CatalogKind.WallStyle && item.Guid is null && (item.Pattern is null || item.Pattern.Value >= 65534))
                throw new ContentError("Wall style has no valid pattern");

            bool validIdentity = item.Guid is uint guid
                ? (Kind is CatalogKind.Water or CatalogKind.WallStyle) && item.Id == 0 && guid != 0
                : Kind switch
                {
                    CatalogKind.Water => item.Id >= 65534,
                    CatalogKind.Roof => item.Id <= 65533,
                    _ => item.Id is >= 1 and <= 65533
                };

            if (!validIdentity || item.Price < 0
                || item.Name is null || item.Name.Length > 512
                || item.Description is null || item.Description.Length > 4096
                || item.File is null || item.File.Length > 1024)
            {
                throw new ContentError("Invalid build catalog pattern");
            }

            if (previous is not null
                && (item.Price < previous.Price || (item.Price == previous.Price && item.SortKey <= previous.SortKey)))
            {
                throw new ContentError("Build catalog patterns are out of order");
            }

            item.Icon?.Validate();
            item.Preview?.Validate();
            previous = item;
        }
    }
}

public sealed record EditableLotObject
{
    [JsonConstructor]
    public EditableLotObject() { }

    [SetsRequiredMembers]
    public EditableLotObject(
        int id,
        uint guid,
        int level,
        int direction,
        bool movable,
        bool sellable,
        int? catalogCategory = null,
        int? multiTileMasterID = null)
    {
        Id = id;
        Guid = guid;
        Level = level;
        Direction = direction;
        Movable = movable;
        Sellable = sellable;
        CatalogCategory = catalogCategory;
        MultiTileMasterID = multiTileMasterID;
    }

    [JsonPropertyName("id")]
    public required int Id { get; init; }

    [JsonPropertyName("guid")]
    public required uint Guid { get; init; }

    [JsonPropertyName("level")]
    public required int Level { get; init; }

    [JsonPropertyName("direction")]
    public required int Direction { get; init; }

    [JsonPropertyName("movable")]
    public required bool Movable { get; init; }

    [JsonPropertyName("sellable")]
    public required bool Sellable { get; init; }

    /// <summary>Specifies the catalog category index (such as 8 for doors or 9 for windows), or <c>null</c>
    /// if not cataloged.</summary>
    [JsonPropertyName("catalogCategory")]
    public int? CatalogCategory { get; init; }

    [JsonPropertyName("multiTileMasterID")]
    public int? MultiTileMasterID { get; init; }
}

public sealed class BuySelection
{
    public readonly record struct SelectionTarget
    {
        private SelectionTarget(bool isPlaced, uint catalogGuid, EditableLotObject? placedObject)
        {
            IsPlaced = isPlaced;
            CatalogGuid = catalogGuid;
            PlacedObject = placedObject;
        }

        public bool IsPlaced { get; }
        public bool IsCatalog => !IsPlaced;
        public uint CatalogGuid { get; }
        public EditableLotObject? PlacedObject { get; }

        public static SelectionTarget Catalog(uint guid) => new(false, guid, null);
        public static SelectionTarget Placed(EditableLotObject obj) => new(true, obj.Guid, obj);
    }

    public SelectionTarget? Target { get; private set; }
    public int Direction { get; private set; } = 1;

    public void ChooseCatalog(uint guid)
    {
        if (guid == 0)
            return;
        Target = SelectionTarget.Catalog(guid);
        Direction = 1;
    }

    public void ChooseObject(EditableLotObject lotObject)
    {
        if (lotObject.Id <= 0 || lotObject.Guid == 0
            || lotObject.Level is < 1 or > 5
            || lotObject.Direction is not (1 or 4 or 16 or 64)
            || (!lotObject.Movable && !lotObject.Sellable))
        {
            return;
        }

        Target = SelectionTarget.Placed(lotObject);
        Direction = lotObject.Direction;
    }

    public void SetDirection(int direction)
    {
        if (Target is not { } target)
            return;
        if (target.IsPlaced && target.PlacedObject is { Movable: false })
            return;
        if (direction is not (1 or 4 or 16 or 64))
            return;

        Direction = direction;
    }

    public void Rotate(int steps = 1)
    {
        if (Target is not { } target)
            return;
        if (target.IsPlaced && target.PlacedObject is { Movable: false })
            return;

        int[] directions = [1, 4, 16, 64];
        int index = Array.IndexOf(directions, Direction);
        if (index < 0)
            index = 0;
        Direction = directions[((index + steps % 4) + 4) % 4];
    }

    /// <summary>Rotates the held object toward a tile offset using directional bitmasks: 1 for north (-y),
    /// 4 for east (+x), 16 for south (+y), and 64 for west (-x).</summary>
    public bool Face(int dx, int dy)
    {
        if (Target is not { } target || (dx == 0 && dy == 0))
            return false;
        if (target.IsPlaced && target.PlacedObject is { Movable: false })
            return false;

        int facing = Math.Abs((long)dx) > Math.Abs((long)dy) ? (dx > 0 ? 4 : 64) : (dy > 0 ? 16 : 1);
        bool changed = facing != Direction;
        Direction = facing;
        return changed;
    }

    public void Cancel() => Target = null;

    public void Reconcile(IReadOnlyList<EditableLotObject> objects)
    {
        if (Target is not { IsPlaced: true, PlacedObject: { } held })
            return;

        var current = objects.FirstOrDefault(o => o.Id == held.Id && o.Guid == held.Guid);
        if (current is null || current.Level is < 1 or > 5 || current.Level != held.Level
            || (!current.Movable && !current.Sellable))
        {
            Cancel();
            return;
        }

        Target = SelectionTarget.Placed(current);
    }
}

public sealed record PlacementPreview
{
    public readonly record struct Tile
    {
        [JsonConstructor]
        public Tile() { }

        [SetsRequiredMembers]
        public Tile(int x, int y, int level)
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

    public sealed record GhostData
    {
        [JsonConstructor]
        public GhostData() { }

        [SetsRequiredMembers]
        public GhostData(float x, float y, LotScene scene)
        {
            X = x;
            Y = y;
            Scene = scene;
        }

        [JsonPropertyName("x")]
        public required float X { get; init; }

        [JsonPropertyName("y")]
        public required float Y { get; init; }

        [JsonPropertyName("scene")]
        public required LotScene Scene { get; init; }

        public void Validate(bool wallSurface = false)
        {
            if (!float.IsFinite(X) || !float.IsFinite(Y)
                || MathF.Abs(X) > 65536f || MathF.Abs(Y) > 65536f
                || Scene is null || Scene.Sprites is null || Scene.Textures is null
                || Scene.Sprites.Count > 2048 || Scene.Textures.Count > 4096
                || Scene.Meshes is not null || Scene.TerrainVertices is not null)
            {
                throw new ContentError("Invalid placement ghost");
            }

            if (wallSurface)
                Scene.ValidateWallSurface();
            else
                Scene.Validate();
        }
    }

    [JsonConstructor]
    public PlacementPreview() { }

    [JsonPropertyName("ghost")]
    public GhostData? Ghost { get; init; }

    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("valid")]
    public required bool Valid { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("direction")]
    public int? Direction { get; init; }

    [JsonPropertyName("tiles")]
    public required IReadOnlyList<Tile> Tiles { get; init; }

    public static PlacementPreview FromJson(string json) =>
        JsonSerializer.Deserialize<PlacementPreview>(json) ?? throw new ContentError("Placement preview JSON decoded to null");

    public List<LotScene.TerrainVertex> Geometry(LotScene scene)
    {
        if (SchemaVersion != 1 || Status is null || Status.Length > 128
            || Tiles is null || Tiles.Count == 0 || Tiles.Count > 128
            || scene.LotWidth is not int width || scene.LotHeight is not int height
            || scene.ConstructionVertices is not { } terrain || terrain.Count != width * height * 6)
        {
            throw new ContentError("Invalid placement preview");
        }

        Ghost?.Validate();

        var result = new List<LotScene.TerrainVertex>(Tiles.Count * 6);
        foreach (var tile in Tiles)
        {
            if (tile.X is < -128 or > 255 || tile.Y is < -128 or > 255 || tile.Level is < 1 or > 5)
                throw new ContentError("Placement footprint exceeds limits");

            if (tile.Level == scene.SelectedLevel && tile.X >= 0 && tile.X < width && tile.Y >= 0 && tile.Y < height)
            {
                int start = (tile.Y * width + tile.X) * 6;
                for (int i = 0; i < 6; i++)
                    result.Add(terrain[start + i]);
            }
        }

        return result;
    }
}

public static class PlacementTooltip
{
    /// <summary>Returns the tooltip delay in seconds: 0.0 when quick tips are enabled, or 0.750 seconds
    /// (750 ms) when disabled.</summary>
    public static double Delay(bool quickTips) => quickTips ? 0.0 : 0.750;

    public static double? Progress(ref double? startedAt, bool quickTips, double? now = null)
    {
        double timestamp = now ?? (Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        startedAt ??= timestamp;
        double duration = quickTips ? 0.250 : 0.333;
        double elapsed = timestamp - startedAt.Value - Delay(quickTips);
        if (elapsed < 0.0)
            return null;
        return Math.Min(1.0, elapsed / duration);
    }

    public static (int Width, int Height, byte[] Rgba) Rasterize(string text, SimsBitmapFont font)
    {
        if (text.Length > 8192)
            throw new ContentError("Tooltip text exceeds size limits");

        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        string[] lines = normalized.Split('\n');
        if (lines.Length > 512)
            throw new ContentError("Tooltip text has too many lines");

        var labels = new (int Width, int Height, byte[] Rgba)[lines.Length];
        int maxLineWidth = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            labels[i] = font.Rasterize(lines[i], (0, 0, 0));
            maxLineWidth = Math.Max(maxLineWidth, font.TextWidth(lines[i]));
        }

        int width = Math.Max(6, maxLineWidth + 6);
        int height = Math.Max(2, lines.Length * font.LineHeight + 2);
        byte[] pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);

        for (int pixel = 0; pixel < width * height; pixel++)
            pixels[pixel * 4 + 2] = 224;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
                {
                    int offset = (y * width + x) * 4;
                    pixels[offset] = 0;
                    pixels[offset + 1] = 0;
                    pixels[offset + 2] = 0;
                }
            }
        }

        for (int index = 0; index < labels.Length; index++)
        {
            var label = labels[index];
            for (int y = 3; y < label.Height - 3; y++)
            {
                for (int x = 3; x < label.Width - 3; x++)
                {
                    int target = ((1 + index * font.LineHeight + y - 3) * width + x) * 4;
                    byte shade = (byte)(255 - label.Rgba[(y * label.Width + x) * 4 + 3]);
                    pixels[target] = shade;
                    pixels[target + 1] = shade;
                    pixels[target + 2] = shade;
                }
            }
        }

        return (width, height, pixels);
    }

    public static SimsBitmapFont.HiDpiRaster RasterizeHiDpi(string text, SimsBitmapFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (text.Length > 8192)
            throw new ContentError("Tooltip text exceeds size limits");

        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        string[] lines = normalized.Split('\n');
        if (lines.Length > 512)
            throw new ContentError("Tooltip text has too many lines");

        var labels = new SimsBitmapFont.HiDpiRaster[lines.Length];
        int maxLineWidth = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            labels[i] = font.RasterizeHiDpi(lines[i], (0, 0, 0), logicalMargin: 0);
            maxLineWidth = Math.Max(maxLineWidth, font.TextWidth(lines[i]));
        }

        return CompositeTrimmedBoxHiDpi(labels, font, maxLineWidth, padXLogical: 3, padYLogical: 2);
    }

    public static SimsBitmapFont.HiDpiRaster RasterizeBoxHiDpi(
        string label,
        SimsBitmapFont font,
        (byte R, byte G, byte B) textColor)
    {
        ArgumentNullException.ThrowIfNull(font);
        var glyphs = font.RasterizeHiDpi(label, textColor, logicalMargin: 0);
        return CompositeTrimmedBoxHiDpi([glyphs], font, font.TextWidth(label), padXLogical: 3, padYLogical: 2);
    }

    private static SimsBitmapFont.HiDpiRaster CompositeTrimmedBoxHiDpi(
        SimsBitmapFont.HiDpiRaster[] lines,
        SimsBitmapFont font,
        int maxLineWidth,
        int padXLogical,
        int padYLogical)
    {
        int s = lines.Length > 0 && lines[0].LogicalHeight > 0
            ? Math.Max(1, lines[0].PixelHeight / lines[0].LogicalHeight)
            : (SimsBitmapFont.SharpTextEnabled ? SimsBitmapFont.HiDpiScale : 1);

        int nominalTopPx = Math.Max(0, (int)MathF.Floor(font.CapTopPx * s));
        int nominalBotPx = Math.Min(
            font.LineHeight * s,
            (int)MathF.Ceiling((font.CapBottomPx + Math.Max(1.8f, font.CapInkSpanPx * 0.28f)) * s));
        if (nominalBotPx <= nominalTopPx)
        {
            nominalTopPx = 0;
            nominalBotPx = Math.Max(1, font.LineHeight * s);
        }

        int lineStridePx = Math.Max(s, nominalBotPx - nominalTopPx + 2 * s);
        int minInkY = nominalTopPx;
        int maxInkY = nominalBotPx - 1;
        for (int index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            for (int sy = 0; sy < line.PixelHeight; sy++)
            {
                bool rowHasInk = false;
                int rowBase = sy * line.PixelWidth * 4 + 3;
                for (int sx = 0; sx < line.PixelWidth; sx++)
                {
                    if (line.Rgba[rowBase + sx * 4] > 12)
                    {
                        rowHasInk = true;
                        break;
                    }
                }
                if (!rowHasInk)
                    continue;
                if (index == 0 && sy < minInkY)
                    minInkY = sy;
                if (index == lines.Length - 1 && sy > maxInkY)
                    maxInkY = sy;
            }
        }

        int contentPxH = Math.Max(s, (lines.Length - 1) * lineStridePx + (maxInkY - minInkY + 1));
        int contentLogicalH = Math.Max(2, (contentPxH + s - 1) / s);
        int logicalW = Math.Max(6, maxLineWidth + (padXLogical + 1) * 2);
        int logicalH = Math.Max(4, contentLogicalH + (padYLogical + 1) * 2);
        int pixelW = logicalW * s;
        int pixelH = logicalH * s;
        byte[] pixels = new byte[pixelW * pixelH * 4];
        for (int i = 0; i < pixelW * pixelH; i++)
        {
            int off = i * 4;
            pixels[off] = 255;
            pixels[off + 1] = 255;
            pixels[off + 2] = 224;
            pixels[off + 3] = 255;
        }

        for (int y = 0; y < pixelH; y++)
        {
            for (int x = 0; x < pixelW; x++)
            {
                if (x < s || y < s || x >= pixelW - s || y >= pixelH - s)
                {
                    int off = (y * pixelW + x) * 4;
                    pixels[off] = 0;
                    pixels[off + 1] = 0;
                    pixels[off + 2] = 0;
                }
            }
        }

        int dstX0 = (1 + padXLogical) * s;
        int innerH = pixelH - 2 * s;
        int startY = s + Math.Max(0, (innerH - contentPxH) / 2);
        for (int index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            int dstY0 = startY + index * lineStridePx - minInkY;
            for (int sy = 0; sy < line.PixelHeight; sy++)
            {
                int dy = dstY0 + sy;
                if (dy < s || dy >= pixelH - s)
                    continue;
                for (int sx = 0; sx < line.PixelWidth; sx++)
                {
                    int dx = dstX0 + sx;
                    if (dx < s || dx >= pixelW - s)
                        continue;

                    int src = (sy * line.PixelWidth + sx) * 4;
                    int alpha = line.Rgba[src + 3];
                    if (alpha <= 0)
                        continue;

                    int dst = (dy * pixelW + dx) * 4;
                    int inv = 255 - alpha;
                    pixels[dst] = (byte)((line.Rgba[src] * alpha + pixels[dst] * inv + 127) / 255);
                    pixels[dst + 1] = (byte)((line.Rgba[src + 1] * alpha + pixels[dst + 1] * inv + 127) / 255);
                    pixels[dst + 2] = (byte)((line.Rgba[src + 2] * alpha + pixels[dst + 2] * inv + 127) / 255);
                }
            }
        }

        return new SimsBitmapFont.HiDpiRaster(logicalW, logicalH, pixelW, pixelH, pixels);
    }

    public static CGRect Rect(CGSize size, CGPoint point, CGSize canvas) =>
        new(
            Math.Max(0.0, Math.Min(point.X, canvas.Width - size.Width)),
            Math.Max(0.0, point.Y - size.Height),
            size.Width,
            size.Height);
}

public sealed record PanelDescription
{
    public const int SupportedSchemaVersion = 1;
    public const int LogicalWidth = 800;
    public const int LogicalHeight = 600;
    public const int MaximumElements = 64;
    public const int TitleHeight = 36;
    public const int CloseBandHeight = 44;

    public readonly record struct Rect
    {
        [JsonConstructor]
        public Rect() { }

        [SetsRequiredMembers]
        public Rect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        [JsonPropertyName("x")]
        public required int X { get; init; }

        [JsonPropertyName("y")]
        public required int Y { get; init; }

        [JsonPropertyName("width")]
        public required int Width { get; init; }

        [JsonPropertyName("height")]
        public required int Height { get; init; }

        [JsonIgnore]
        public CGRect CGRect => new(X, Y, Width, Height);

        internal bool Contains(Rect other) =>
            other.X >= X && other.Y >= Y
            && other.X + other.Width <= X + Width
            && other.Y + other.Height <= Y + Height;
    }

    [JsonConverter(typeof(StrictCamelCaseEnumConverter<Kind>))]
    public enum Kind
    {
        Label,
        Button,
        Meter,
        Image,
        List
    }

    public sealed record Element
    {
        [JsonConstructor]
        public Element() { }

        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("kind")]
        public required PanelDescription.Kind Kind { get; init; }

        [JsonPropertyName("frame")]
        public required Rect Frame { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("binding")]
        public string? Binding { get; init; }

        [JsonPropertyName("action")]
        public string? Action { get; init; }

        [JsonPropertyName("resource")]
        public string? Resource { get; init; }

        [JsonPropertyName("minimum")]
        public double? Minimum { get; init; }

        [JsonPropertyName("maximum")]
        public double? Maximum { get; init; }

        [JsonPropertyName("accessibilityLabel")]
        public string? AccessibilityLabel { get; init; }
    }

    [JsonConstructor]
    public PanelDescription() { }

    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("frame")]
    public required Rect Frame { get; init; }

    [JsonPropertyName("framed")]
    public bool? Framed { get; init; }

    [JsonPropertyName("elements")]
    public required IReadOnlyList<Element> Elements { get; init; }

    public static PanelDescription FromJson(string json) =>
        JsonSerializer.Deserialize<PanelDescription>(json) ?? throw new ContentError("Panel description JSON decoded to null");

    public string ToJson() => JsonSerializer.Serialize(this);

    public void Validate()
    {
        if (SchemaVersion != SupportedSchemaVersion)
            throw new ContentError($"Panel {Id} uses unsupported schema version {SchemaVersion}");

        if (!IsIdentifier(Id))
            throw new ContentError($"Panel ID {Id} must be 1-64 lowercase letters, digits, dots, underscores or hyphens");

        if (string.IsNullOrEmpty(Title) || Title.Length > 128)
            throw new ContentError($"Panel {Id} title must be 1-128 characters");

        var screen = new Rect(0, 0, LogicalWidth, LogicalHeight);
        if (Frame.Width < 32 || Frame.Height < 32 || !screen.Contains(Frame))
            throw new ContentError($"Panel {Id} frame must fit the 800x600 logical interface");

        if (Elements is null || Elements.Count > MaximumElements)
            throw new ContentError($"Panel {Id} has more than {MaximumElements} elements");

        if (Frame.Width < 140 || Frame.Height < TitleHeight + CloseBandHeight + 8)
            throw new ContentError($"Panel {Id} must be at least 140x{TitleHeight + CloseBandHeight + 8}");

        var local = new Rect(0, TitleHeight, Frame.Width, Frame.Height - TitleHeight - CloseBandHeight);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in Elements)
        {
            if (element is null)
                throw new ContentError($"Panel {Id} contains null element");

            string name = $"Panel {Id} element {element.Id}";
            if (!IsIdentifier(element.Id) || !ids.Add(element.Id))
                throw new ContentError($"{name} needs a unique identifier");

            if (element.Frame.Width <= 0 || element.Frame.Height <= 0 || !local.Contains(element.Frame))
            {
                throw new ContentError(
                    $"{name} must lie inside the panel content area; element frames are relative to the panel, below its {TitleHeight}-point title band and above its {CloseBandHeight}-point close band");
            }

            if (element.Text is not null && element.Text.Length > 256)
                throw new ContentError($"{name} text exceeds 256 characters");
            if (element.AccessibilityLabel is not null && element.AccessibilityLabel.Length > 256)
                throw new ContentError($"{name} accessibility label exceeds 256 characters");
            if (element.Binding is not null && !IsIdentifier(element.Binding))
                throw new ContentError($"{name} has an invalid binding");

            switch (element.Kind)
            {
                case Kind.Label:
                case Kind.List:
                    if (element.Text is null && element.Binding is null)
                        throw new ContentError($"{name} needs text or a binding");
                    if (element.Action is not null || element.Resource is not null)
                        throw new ContentError($"{name} cannot have an action or resource");
                    break;

                case Kind.Button:
                    if (element.Action is null || !IsIdentifier(element.Action))
                        throw new ContentError($"{name} needs a valid action");
                    if (element.Text is null && element.Binding is null)
                        throw new ContentError($"{name} needs text or a binding");
                    break;

                case Kind.Meter:
                    if (element.Binding is null)
                        throw new ContentError($"{name} needs a binding");
                    double minimum = element.Minimum ?? 0.0;
                    double maximum = element.Maximum ?? 100.0;
                    if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum >= maximum)
                        throw new ContentError($"{name} needs minimum below maximum");
                    break;

                case Kind.Image:
                    if (element.Resource is null)
                        throw new ContentError($"{name} needs a UIGraphics resource");
                    string key = LotScene.ResourceKey(element.Resource);
                    int slash = key.LastIndexOf('/');
                    string fileName = slash >= 0 ? key[(slash + 1)..] : key;
                    int dot = fileName.LastIndexOf('.');
                    string extension = dot > 0 ? fileName[(dot + 1)..] : string.Empty;
                    if (!key.StartsWith("uigraphics/", StringComparison.Ordinal) || extension is not ("bmp" or "tga"))
                        throw new ContentError($"{name} resource must be a BMP or TGA under UIGraphics");
                    break;
            }
        }
    }

    public string DisplayText(Element element, IReadOnlyDictionary<string, string> values)
    {
        if (element.Binding is { } binding && values.TryGetValue(binding, out string? value))
            return value;
        return element.Text ?? string.Empty;
    }

    public double? MeterFraction(Element element, IReadOnlyDictionary<string, string> values)
    {
        if (element.Kind != Kind.Meter || element.Binding is not { } binding
            || !values.TryGetValue(binding, out string? raw)
            || !double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value))
        {
            return null;
        }

        double minimum = element.Minimum ?? 0.0;
        double maximum = element.Maximum ?? 100.0;
        return Math.Clamp((value - minimum) / (maximum - minimum), 0.0, 1.0);
    }

    internal static bool IsIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64)
            return false;

        foreach (char c in value)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c is not ('.' or '_' or '-'))
                return false;
        }

        return true;
    }
}
