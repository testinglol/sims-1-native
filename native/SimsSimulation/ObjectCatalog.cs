using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsNative.Plugins;

namespace SimsSimulation;

internal enum CatalogLotType
{
    Residential = 0,
    Downtown = 1,
    Vacation = 2,
    Community = 3,
    StudioTown = 4,
    MagicTown = 5,
}

internal sealed record ObjectCatalogEntry(ObjectEntry Source, uint Guid, int Categories, int Price,
    string Name, string Description, int RoomSort = 0, int FunctionSubsort = 0,
    int DTSubsort = 0, int VacationSubsort = 0, int CommunitySubsort = 0,
    int STSubsort = 0, int MTSubsort = 0);

/// <summary>Catalog filtering evaluates object definition function flags, room flags, and residential or
/// community/expansion lot subsorts. Multi-tile groups are cataloged via their root definition (having a
/// nonzero master ID and sub-index -1) while physical child tiles are omitted.</summary>
internal static class ObjectCatalog
{
    private const ObjdField DTSubsortField = (ObjdField)95;
    private const ObjdField VacationSubsortField = (ObjdField)97;
    private const ObjdField CommunitySubsortField = (ObjdField)99;
    private const ObjdField STSubsortField = (ObjdField)103;
    private const ObjdField MTSubsortField = (ObjdField)104;

    public static bool TryGetEntry(ObjectEntry source, byte language, out ObjectCatalogEntry? item) =>
        TryGetEntry(source, language, CatalogLotType.Residential, out item);

    public static bool TryGetEntry(ObjectEntry source, byte language, CatalogLotType lotType, out ObjectCatalogEntry? item)
    {
        var definition = source.Definition;
        var flags = definition[ObjdField.FunctionFlags];
        var master = definition[ObjdField.MasterId];
        var subIndex = definition[ObjdField.SubIndex];
        var objectClass = LotObjects.ClassOf(definition);
        var buildMode = definition[ObjdField.RoomSort] == 0 ? definition[ObjdField.BuildModeType] : 0;
        // When an object has a room sort value of 0, build tools categorize portals, multi-tile roots,
        // columns, and pool accessories by BuildModeType (1 through 7), mapping to catalog categories 8
        // through 14.
        var buildTool = buildMode is >= 1 and <= 7;
        var dtSubsort = definition[DTSubsortField];
        var vacationSubsort = definition[VacationSubsortField];
        var communitySubsort = definition[CommunitySubsortField];
        var stSubsort = definition[STSubsortField];
        var mtSubsort = definition[MTSubsortField];

        var lotSubsort = lotType switch
        {
            CatalogLotType.Downtown => dtSubsort,
            CatalogLotType.Vacation => vacationSubsort,
            CatalogLotType.Community => communitySubsort,
            CatalogLotType.StudioTown => stSubsort,
            CatalogLotType.MagicTown => mtSubsort,
            _ => 0,
        };

        var validSort = lotType == CatalogLotType.Residential
            ? (definition[ObjdField.RoomSort] != 0 && (flags & 0xff) != 0)
            : (lotSubsort & 0x8f) != 0;

        if (definition[ObjdField.Disabled] != 0 ||
            objectClass is not (ObjectClass.Object or ObjectClass.MultiTileObject or ObjectClass.Portal) ||
            objectClass == ObjectClass.Portal && !buildTool ||
            (master != 0 && subIndex != ushort.MaxValue) || (master == 0 && subIndex != 0) ||
            (definition[ObjdField.RoomSort] == 0 && definition[ObjdField.BuildModeType] != 0 && !buildTool) ||
            !buildTool && definition[ObjdField.NumGraphics] == 0 ||
            !buildTool && !validSort)
        {
            item = null;
            return false;
        }

        string? catName;
        string? catDesc;
        try
        {
            catName = source.CatalogString(0, language);
            catDesc = source.CatalogString(1, language);
        }
        catch (ResourceFormatException)
        {
            catName = null;
            catDesc = null;
        }
        var categories = buildTool
            ? 1 << (buildMode + 7)
            : lotType == CatalogLotType.Residential
                ? ((flags & 0xff) != 0 ? (int)(flags & 0xff) : (1 << 7))
                : (lotSubsort & 0x8f);
        item = new ObjectCatalogEntry(source, source.Guid, categories,
            definition[ObjdField.Price], catName ?? source.Chunk.Label,
            catDesc ?? "", buildTool ? 0 : definition[ObjdField.RoomSort], definition[ObjdField.FunctionSubsort],
            dtSubsort, vacationSubsort, communitySubsort, stSubsort, mtSubsort);
        return true;
    }

    public static object[] All(ObjectRegistry registry, byte language)
    {
        return registry.ByGuid.Values
            .Select(entry => TryGetEntry(entry, language, out var item) ? item : null)
            .Where(item => item is not null)
            .Cast<ObjectCatalogEntry>()
            .OrderBy(item => item.Guid)
            .Select(item =>
            {
                var category = BitOperations.TrailingZeroCount((uint)item.Categories);
                return (object)new
                {
                    guid = item.Guid,
                    name = item.Source.Chunk.Label,
                    catalogName = item.Name,
                    price = item.Price,
                    category,
                    soldHere = true,
                };
            })
            .ToArray();
    }

    /// <summary>Maps catalog room filter buttons (Living, Dining, Bedroom, Study, Kitchen, Bathroom,
    /// Outside, Misc) to their corresponding object definition RoomSort bitmasks.</summary>
    private static readonly int[] RoomMasks = [0x08, 0x20, 0x02, 0x80, 0x01, 0x04, 0x10, 0x40];
    // Maps sub-sort buttons to object FunctionSubsort masks, where 0x90 represents Other and 0xFF
    // represents All (including items with no subsort).
    private static readonly int[] FunctionSubsortMasks = [0x01, 0x02, 0x04, 0x08, 0x20, 0x40, 0x90, 0xff];

    public static IReadOnlyList<PluginCatalogObject> BuildPluginCatalogObjects(
        ObjectRegistry registry,
        byte language,
        CatalogLotType lotType = CatalogLotType.Residential,
        string? gameRoot = null)
    {
        var normalizedRoot = string.IsNullOrEmpty(gameRoot)
            ? null
            : Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var results = new List<PluginCatalogObject>();
        foreach (var entry in registry.ByGuid.Values)
        {
            if (!TryGetEntry(entry, language, lotType, out var item) || item is null)
                continue;
            // Only Buy Mode objects (categories 0..7) participate in Buy Mode plugin views.
            if ((item.Categories & 0xff) == 0)
                continue;
            var source = entry.Source ?? string.Empty;
            var relative = normalizedRoot is not null && source.StartsWith(normalizedRoot, pathComparison)
                ? source[normalizedRoot.Length..]
                : source;
            relative = relative.Replace('\\', '/');
            var isCustom = entry.Scan is ObjectScan.Downloads or ObjectScan.UserObjects
                && !relative.Contains(".far#", StringComparison.OrdinalIgnoreCase);
            results.Add(new PluginCatalogObject(
                item.Guid,
                item.Name,
                relative,
                item.Price,
                item.Categories,
                item.RoomSort,
                isCustom));
        }
        return results;
    }

    public static object Page(ObjectRegistry registry, byte language, int category, int offset, int count, string session,
        int? room = null, int? subsort = null, string? game = null, CatalogLotType lotType = CatalogLotType.Residential,
        IReadOnlySet<uint>? hiddenGuids = null)
    {
        var isResidential = lotType == CatalogLotType.Residential;
        if (category is < -1 or > 12 ||
            (isResidential && category == -1 && room is null) ||
            room is < 0 or > 7 ||
            subsort is < 0 or > 7 ||
            (subsort is not null && (room is not null || (isResidential && category is < 0 or > 7))) ||
            category > 7 && room is not null ||
            offset is < 0 or > 1_000_000 || count is < 1 or > 64)
            throw new ArgumentException("Invalid native residential catalog page");
        var roomMask = room is { } roomIndex ? RoomMasks[roomIndex] : 0;
        var subsortMask = subsort is { } subsortIndex ? FunctionSubsortMasks[subsortIndex] : 0;
        static bool MatchesSubsort(ObjectCatalogEntry item, int mask)
        {
            var value = item.FunctionSubsort;
            return (value & mask) != 0 || mask == 0xff && value == 0;
        }
        bool IsVisible(ObjectCatalogEntry item) =>
            hiddenGuids is null || category > 7 || !hiddenGuids.Contains(item.Guid);

        var items = registry.ByGuid.Values
            .Select(entry => TryGetEntry(entry, language, lotType, out var item) ? item : null)
            .Where(item => item is not null && IsVisible(item) && (room is null
                ? (category == -1 || (item.Categories & (1 << category)) != 0) &&
                  (subsort is null || MatchesSubsort(item, subsortMask))
                : (item.RoomSort & roomMask) != 0 && (category == -1 || (item.Categories & (1 << category)) != 0)))
            .Cast<ObjectCatalogEntry>()
            .OrderBy(item => item.Price)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Guid)
            .ToArray();
        // A catalog function button within room view is enabled only when the selected room contains items
        // matching that function.
        var functionCounts = room is null ? null : new int[8];
        var functionSubsortCounts = room is not null || category is < 0 or > 7 ? null : new int[8];
        if (functionCounts is not null)
        {
            var inRoom = registry.ByGuid.Values
                .Select(entry => TryGetEntry(entry, language, lotType, out var item) ? item : null)
                .Where(item => item is not null && IsVisible(item) && (item.RoomSort & roomMask) != 0);
            foreach (var item in inRoom)
                for (var function = 0; function < 8; function++)
                    if ((item!.Categories & (1 << function)) != 0) functionCounts[function]++;
        }
        if (functionSubsortCounts is not null)
        {
            var inFunction = registry.ByGuid.Values
                .Select(entry => TryGetEntry(entry, language, lotType, out var item) ? item : null)
                .Where(item => item is not null && IsVisible(item) && (item.Categories & (1 << category)) != 0)
                .Cast<ObjectCatalogEntry>()
                .ToArray();
            for (var index = 0; index < FunctionSubsortMasks.Length; index++)
                functionSubsortCounts[index] = inFunction.Count(item => MatchesSubsort(item, FunctionSubsortMasks[index]));
        }
        var ratingStrings = LoadCatalogRatingStrings(game, language);
        var globals = string.IsNullOrEmpty(game) ? null : GlobalLibrary.Open(game);
        return new
        {
            schemaVersion = 1,
            category,
            room,
            functionCounts,
            subsort,
            functionSubsortCounts,
            offset,
            total = items.Length,
            items = items.Skip(offset).Take(count).Select(item => new
            {
                guid = item.Guid,
                name = item.Name,
                description = item.Description,
                price = item.Price,
                category = category == -1 ? BitOperations.TrailingZeroCount((uint)item.Categories) : category,
                icon = SafeCatalogImage(() => CatalogIcon(item.Source, session)),
                preview = SafeCatalogImage(() => CatalogPreview(item.Source, session)),
                ratings = CatalogRatings(item.Source, ratingStrings, language, globals?.ScopeOf(item.Source),
                    registry.FindAlternativeType(item.Source)),
            }).ToArray(),
        };
    }

    public static object PageByGuids(ObjectRegistry registry, byte language, IReadOnlyList<uint> guids, int offset, int count,
        string session, string? game = null, CatalogLotType lotType = CatalogLotType.Residential)
    {
        if (offset is < 0 or > 1_000_000 || count is < 1 or > 64)
            throw new ArgumentException("Invalid plugin catalog page bounds");
        var guidSet = new HashSet<uint>(guids);
        var items = registry.ByGuid.Values
            .Where(entry => guidSet.Contains(entry.Guid))
            .Select(entry => TryGetEntry(entry, language, lotType, out var item) ? item : null)
            .Where(item => item is not null)
            .Cast<ObjectCatalogEntry>()
            .OrderBy(item => item.Price)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Guid)
            .ToArray();
        var ratingStrings = LoadCatalogRatingStrings(game, language);
        var globals = string.IsNullOrEmpty(game) ? null : GlobalLibrary.Open(game);
        return new
        {
            schemaVersion = 1,
            category = -1,
            room = (int?)null,
            functionCounts = (int[]?)null,
            subsort = (int?)null,
            functionSubsortCounts = (int[]?)null,
            offset,
            total = items.Length,
            items = items.Skip(offset).Take(count).Select(item => new
            {
                guid = item.Guid,
                name = item.Name,
                description = item.Description,
                price = item.Price,
                category = BitOperations.TrailingZeroCount((uint)item.Categories),
                icon = SafeCatalogImage(() => CatalogIcon(item.Source, session)),
                preview = SafeCatalogImage(() => CatalogPreview(item.Source, session)),
                ratings = CatalogRatings(item.Source, ratingStrings, language, globals?.ScopeOf(item.Source),
                    registry.FindAlternativeType(item.Source)),
            }).ToArray(),
        };
    }

    private static object? SafeCatalogImage(Func<object?> loader)
    {
        try { return loader(); }
        catch (Exception error) when (error is ResourceFormatException or InvalidDataException or ArgumentException or OverflowException) { return null; }
    }

    private static IReadOnlyList<string>? LoadCatalogRatingStrings(string? game, byte language)
    {
        if (string.IsNullOrEmpty(game)) return null;
        foreach (var folder in new[] { Path.Combine(game, "GameData"), game })
        {
            if (folder is null || !Directory.Exists(folder)) continue;
            var path = Directory.EnumerateFiles(folder).FirstOrDefault(file =>
                string.Equals(Path.GetFileName(file), "UIText.iff", StringComparison.OrdinalIgnoreCase));
            if (path is null) continue;
            try
            {
                var iff = IffFile.Open(path);
                var chunk = iff.Find(ResourceType.Str, 0xa0);
                if (chunk is not null) return StringTable.Read(iff, chunk).ForLanguage(language);
            }
            catch (Exception error) when (error is IOException or ResourceFormatException) { }
        }
        return null;
    }

    /// <summary>Generates catalog motive and skill rating lines using text strings from resource 160,
    /// evaluated against object definition motive offsets, skill bits, and interaction table flags.</summary>
    internal static string[] CatalogRatings(ObjectEntry item, IReadOnlyList<string>? strings, byte language = 1,
        BehaviorScope? scope = null, ObjectEntry? masterFallback = null)
    {
        if (strings is null || strings.Count < 14) return [];
        var fields = item.Definition.Fields;
        var lines = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            var wordIndex = 0x52 + i;
            var rating = wordIndex < fields.Count ? unchecked((short)fields[wordIndex]) : (short)0;
            if (rating != 0 && i < strings.Count && strings[i].Length > 0)
                lines.Add(strings[i].Replace("%d", rating.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        }
        var skillFlags = 0x59 < fields.Count ? (fields[0x59] & 0xff) : 0;
        for (var bit = 0; bit < 7; bit++)
        {
            var stringIndex = 7 + bit;
            if ((skillFlags & (1 << bit)) != 0 && stringIndex < strings.Count && strings[stringIndex].Length > 0)
                lines.Add(strings[stringIndex]);
        }
        try
        {
            if (!item.HasNoMasterInteractionTable &&
                item.FindInteractionTable(scope, language, masterFallback) is { } table)
            {
                var nonDebug = table.Interactions.Where(entry => (entry.Flags & 0x80) == 0).ToArray();
                if (nonDebug.Length > 0 && strings.Count >= 20)
                {
                    // String table 160 indices specify target demographics: 14 for adults, 15 for children,
                    // 16 for group activities, 17 for pets, 18 for dogs, and 19 for cats.
                    if (nonDebug.All(e => (e.Flags & 0x10) != 0 && (e.Flags & 0x200) == 0 && (e.Flags & 0x400) == 0))
                        lines.Add(strings[14]);
                    else if (nonDebug.All(e => (e.Flags & 0x40) != 0 && (e.Flags & 0x200) == 0 && (e.Flags & 0x400) == 0))
                        lines.Add(strings[15]);
                    else if (nonDebug.All(e => (e.Flags & 0x40) != 0 && (e.Flags & 0x10) != 0 && (e.Flags & 0x200) == 0))
                        lines.Add(strings[18]);
                    else if (nonDebug.All(e => (e.Flags & 0x40) != 0 && (e.Flags & 0x10) != 0 && (e.Flags & 0x400) == 0))
                        lines.Add(strings[19]);
                    else if (nonDebug.All(e => (e.Flags & 0x40) != 0 && (e.Flags & 0x10) != 0))
                        lines.Add(strings[17]);
                }
                if (strings.Count > 16 && nonDebug.Any(e => e.JoiningIndex >= 0 && table.Interactions.Any(c => c.TtaIndex == e.JoiningIndex)))
                    lines.Add(strings[16]);
            }
        }
        catch (Exception error) when (error is NotSupportedException or ResourceFormatException) { }
        return lines.Where(line => line.Length > 0).ToArray();
    }

    /// <summary>Resolves an object's catalog icon from the bitmap resource sharing the object definition's
    /// CatalogStringsID, applying magenta color-key transparency.</summary>
    internal static object? CatalogIcon(ObjectEntry item, string session)
    {
        var masterDef = ResolveMasterDefinition(item.File, item.Definition);
        var masterCatalogId = (ushort)masterDef[ObjdField.CatalogStringsId];
        var itemCatalogId = (ushort)item.Definition[ObjdField.CatalogStringsId];
        var candidates = new List<ushort>(3);
        if (masterCatalogId != 0) candidates.Add(masterCatalogId);
        if (itemCatalogId != 0 && itemCatalogId != masterCatalogId) candidates.Add(itemCatalogId);
        if (candidates.Count == 0) candidates.Add(2000);
        foreach (var candidateId in candidates)
        {
            var bytes = item.File.Find(ResourceType.Parse("BMP_"), candidateId)?.Data;
            if (bytes is { Length: >= 54 and <= 4 * 1024 * 1024 } && bytes.Value.Span[0] == (byte)'B' && bytes.Value.Span[1] == (byte)'M')
            {
                var data = bytes.Value.Span;
                var width = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
                var height = Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(data[22..]));
                if (width is > 0 and <= 512 && height is > 0 and <= 512)
                    return StoreImage(data, width == height * 2 ? 2 : 1, session);
            }
        }
        var catalogBmps = item.File.OfType(ResourceType.Parse("BMP_"))
            .Where(chunk => chunk.Id is >= 2000 and < 3000)
            .ToArray();
        if (catalogBmps.Length == 1)
        {
            var rootObjds = item.File.OfType(ResourceType.Objd)
                .Select(chunk => { try { return Objd.Read(item.File, chunk); } catch (ResourceFormatException) { return null; } })
                .Count(def => def is not null && ((def[ObjdField.MasterId] == 0 && def[ObjdField.SubIndex] == 0) ||
                    (def[ObjdField.MasterId] != 0 && def[ObjdField.SubIndex] == ushort.MaxValue)));
            if (rootObjds <= 1 && catalogBmps[0].Data is { Length: >= 54 and <= 4 * 1024 * 1024 } singleBytes &&
                singleBytes.Span[0] == (byte)'B' && singleBytes.Span[1] == (byte)'M')
            {
                var data = singleBytes.Span;
                var width = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
                var height = Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(data[22..]));
                if (width is > 0 and <= 512 && height is > 0 and <= 512)
                    return StoreImage(data, width == height * 2 ? 2 : 1, session);
            }
        }
        var rendered = RenderedCatalogIcon(item);
        return rendered is null ? null : StoreImage(rendered, 2, session);
    }

    private static Objd ResolveMasterDefinition(IffFile file, Objd definition)
    {
        var master = definition[ObjdField.MasterId];
        if (master != 0 && definition[ObjdField.SubIndex] != ushort.MaxValue)
        {
            foreach (var chunk in file.OfType(ResourceType.Objd))
            {
                try
                {
                    var candidate = Objd.Read(file, chunk);
                    if (candidate[ObjdField.MasterId] == master && candidate[ObjdField.SubIndex] == ushort.MaxValue)
                        return candidate;
                }
                catch (ResourceFormatException) { }
            }
        }
        return definition;
    }

    /// <summary>Renders the catalog popup preview image by selecting the highest zoom level (from 3 down to
    /// 1) whose draw group bounding box fits within the 160x119 inner box.</summary>
    internal static object? CatalogPreview(ObjectEntry item, string session)
    {
        var rendered = RenderedPreview(item);
        return rendered is null ? CatalogIcon(item, session) : StoreImage(rendered, 1, session);
    }

    /// <summary>Generates an object speech balloon icon by rendering its thumbnail draw group at
    /// front-direction plus four rotation steps, scaled to the target speech icon dimensions.</summary>
    internal static (int Width, int Height, byte[] Rgba)? GenerateProductSpeechIcon(ObjectEntry item, int size = 16)
    {
        if (item.Definition[ObjdField.Type] != 1 || size is < 1 or > 128) return null;
        var bitmap = RenderedIcon(item);
        if (bitmap is null) return null;
        try
        {
            var (width, height, rgba) = BitmapCodec.Read(bitmap);
            return FitSpeechImage(width, height, rgba, size);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    internal static (int Width, int Height, byte[] Rgba) FitSpeechImage(int width, int height, byte[] rgba, int size)
    {
        if (width <= 0 || height <= 0 || size is < 1 or > 128 || rgba.Length != checked(width * height * 4))
            throw new ArgumentException("Invalid speech image dimensions or pixels");

        // Speech icons preserve color-key transparency during scaling so that background padding and
        // transparent pixels retain zero alpha.
        var scale = Math.Min(size / (float)width, size / (float)height);
        var fittedWidth = Math.Clamp((int)MathF.Round(width * scale), 1, size);
        var fittedHeight = Math.Clamp((int)MathF.Round(height * scale), 1, size);
        var left = (size - fittedWidth) / 2;
        var top = (size - fittedHeight) / 2;
        var output = new byte[size * size * 4];

        for (var y = 0; y < fittedHeight; y++)
        for (var x = 0; x < fittedWidth; x++)
        {
            var sourceX = (x + 0.5f) * width / fittedWidth - 0.5f;
            var sourceY = (y + 0.5f) * height / fittedHeight - 0.5f;
            var x0 = Math.Clamp((int)MathF.Floor(sourceX), 0, width - 1);
            var y0 = Math.Clamp((int)MathF.Floor(sourceY), 0, height - 1);
            var x1 = Math.Min(x0 + 1, width - 1);
            var y1 = Math.Min(y0 + 1, height - 1);
            var fx = Math.Clamp(sourceX - MathF.Floor(sourceX), 0, 1);
            var fy = Math.Clamp(sourceY - MathF.Floor(sourceY), 0, 1);
            static (float Pr, float Pg, float Pb, float A) Read(byte[] pixels, int at)
            {
                var alphaByte = pixels[at + 3];
                if (alphaByte == 0 || (pixels[at] == 255 && pixels[at + 1] == 0 && pixels[at + 2] == 255))
                    return (0f, 0f, 0f, 0f);
                var alpha = alphaByte / 255f;
                return (pixels[at] * alpha, pixels[at + 1] * alpha, pixels[at + 2] * alpha, alphaByte);
            }
            var a = Read(rgba, (y0 * width + x0) * 4);
            var b = Read(rgba, (y0 * width + x1) * 4);
            var c = Read(rgba, (y1 * width + x0) * 4);
            var d = Read(rgba, (y1 * width + x1) * 4);
            static float Lerp2(float p, float q, float r, float s, float fx, float fy) =>
                (p + (q - p) * fx) * (1 - fy) + (r + (s - r) * fx) * fy;
            var blendedA = Lerp2(a.A, b.A, c.A, d.A, fx, fy);
            var outA = Math.Clamp((int)MathF.Round(blendedA), 0, 255);
            if (outA <= 0) continue;
            var alphaNorm = blendedA / 255f;
            var target = ((top + y) * size + left + x) * 4;
            output[target] = (byte)Math.Clamp((int)MathF.Round(Lerp2(a.Pr, b.Pr, c.Pr, d.Pr, fx, fy) / alphaNorm), 0, 255);
            output[target + 1] = (byte)Math.Clamp((int)MathF.Round(Lerp2(a.Pg, b.Pg, c.Pg, d.Pg, fx, fy) / alphaNorm), 0, 255);
            output[target + 2] = (byte)Math.Clamp((int)MathF.Round(Lerp2(a.Pb, b.Pb, c.Pb, d.Pb, fx, fy) / alphaNorm), 0, 255);
            output[target + 3] = (byte)outA;
        }
        return (size, size, output);
    }

    private sealed record IconLayer(
        int X,
        int Y,
        int Width,
        int Height,
        bool Flip,
        float Order,
        int Index,
        byte[] Rgba,
        byte[]? Depth,
        float DepthOffset);

    internal static byte[]? RenderedIcon(ObjectEntry item) => RenderObjectFitting(item, 37, 37, fixedZoom: 3);

    /// <summary>Renders a 74x37 two-frame catalog thumbnail containing normal and highlighted states at
    /// zoom 3, filtered into the 37x37 frame of the thumbnail template.</summary>
    private static byte[]? RenderedCatalogIcon(ObjectEntry item) => RenderObjectFitting(item, 37, 37, fixedZoom: 3, twoStateCatalogSheet: true);

    /// <summary>Renders an object preview from the highest available zoom level (preferring zoom 3),
    /// smoothly area-downsampled into the 160x119 preview box with preserved 8-bit alpha.</summary>
    private static byte[]? RenderedPreview(ObjectEntry item) => RenderObjectFitting(item, 160, 119, fixedZoom: 3);

    internal static byte[]? RenderedDialogPreview(ObjectEntry item) => RenderObjectFitting(item, 127, 97, fixedZoom: null);

    private static byte[]? RenderObjectFitting(ObjectEntry item, int maxWidth, int maxHeight, int? fixedZoom, bool twoStateCatalogSheet = false)
    {
        var masterDef = ResolveMasterDefinition(item.File, item.Definition);
        var directionIndex = masterDef.ThumbnailDirectionIndex;
        var masterGraphicIndex = masterDef.ThumbnailGraphicIndex;
        var parts = new List<(Objd Def, int Rx, int Ry)>();
        var master = masterDef[ObjdField.MasterId];
        if (master != 0)
        {
            // Gathers all child object definitions sharing the master ID and rotates their tile offset
            // coordinates (packed in the sub-index field) according to the target direction.
            foreach (var chunk in item.File.OfType(ResourceType.Objd))
            {
                Objd subDef;
                try { subDef = Objd.Read(item.File, chunk); }
                catch (ResourceFormatException) { continue; }
                var sub = subDef[ObjdField.SubIndex];
                if (subDef[ObjdField.MasterId] != master || sub == ushort.MaxValue) continue;
                int subX = unchecked((short)sub) >> 8;
                int subY = sub & 0xff;
                var (rx, ry) = (directionIndex & 7) switch
                {
                    0 => (subX, subY),
                    2 => (-subY, subX),
                    4 => (-subX, -subY),
                    6 => (subY, -subX),
                    _ => (subX, subY),
                };
                parts.Add((subDef, rx, ry));
            }
            parts.Sort((a, b) => (a.Rx + a.Ry).CompareTo(b.Rx + b.Ry));
        }
        if (parts.Count == 0)
        {
            parts.Add((masterDef, 0, 0));
        }

        (int Width, int Height, byte[] Canvas)? chosen = null;
        if (fixedZoom is { } z)
        {
            chosen = RenderObjectZoom(item.File, parts, directionIndex, masterGraphicIndex, z);
        }
        if (chosen is null)
        {
            for (var zoom = 3; zoom >= 1; zoom--)
            {
                var candidate = RenderObjectZoom(item.File, parts, directionIndex, masterGraphicIndex, zoom);
                if (candidate is null) continue;
                chosen = candidate;
                if ((maxWidth - candidate.Value.Width) / 2 > 2 && (maxHeight - candidate.Value.Height) / 2 > 2) break;
            }
        }
        if (chosen is not { } result) return null;
        var (widthOut, heightOut, canvas) = result;
        var scale = Math.Min(1f, Math.Min(maxWidth / (float)widthOut, maxHeight / (float)heightOut));
        var outWidth = Math.Max(1, (int)Math.Round(widthOut * scale));
        var outHeight = Math.Max(1, (int)Math.Round(heightOut * scale));
        if (twoStateCatalogSheet)
        {
            var padX = (maxWidth - outWidth) / 2;
            var padY = (maxHeight - outHeight) / 2;
            return WriteIconBitmap(maxWidth * 2, maxHeight, (x, y) =>
            {
                var highlighted = x >= maxWidth;
                var localX = (highlighted ? x - maxWidth : x) - padX;
                var localY = y - padY;
                if (localX < 0 || localX >= outWidth || localY < 0 || localY >= outHeight)
                    return ((byte)255, (byte)0, (byte)255, (byte)255);
                var bg = highlighted ? ((byte)152, (byte)196, (byte)208) : ((byte)104, (byte)164, (byte)184);
                return DownsampleBlended(canvas, widthOut, heightOut,
                    localX / (float)outWidth, localY / (float)outHeight, 1f / outWidth, 1f / outHeight, bg);
            });
        }
        var pad = maxWidth >= 3 && maxHeight >= 3 ? 1 : 0;
        var innerMaxW = maxWidth - pad * 2;
        var innerMaxH = maxHeight - pad * 2;
        var innerScale = Math.Min(1f, Math.Min(innerMaxW / (float)widthOut, innerMaxH / (float)heightOut));
        var innerWidth = Math.Max(1, (int)Math.Round(widthOut * innerScale));
        var innerHeight = Math.Max(1, (int)Math.Round(heightOut * innerScale));
        return WriteIconBitmap(innerWidth + pad * 2, innerHeight + pad * 2, (x, y) =>
        {
            var localX = x - pad;
            var localY = y - pad;
            if ((uint)localX >= (uint)innerWidth || (uint)localY >= (uint)innerHeight)
                return ((byte)0, (byte)0, (byte)0, (byte)0);
            return DownsampleAlpha(canvas, widthOut, heightOut, localX, localY, innerWidth, innerHeight);
        });
    }

    private static (int Width, int Height, byte[] Canvas)? RenderObjectZoom(
        IffFile file, IReadOnlyList<(Objd Def, int Rx, int Ry)> parts, int directionIndex, short masterGraphicIndex, int zoom)
    {
        var thumbnailDirectionMask = 1 << directionIndex;
        var layers = new List<IconLayer>();
        var tileHalfW = 8 << zoom;
        var tileHalfH = 4 << zoom;
        var layerSequence = 0;

        foreach (var (def, rx, ry) in parts)
        {
            // The thumbnail graphic index is taken from the master object definition. If this index exceeds
            // a tile part's available graphics count, it falls back to index 0 instead of skipping the
            // tile.
            int graphicIndex = masterGraphicIndex != 0 ? masterGraphicIndex : def.ThumbnailGraphicIndex;
            var graphicCount = def[ObjdField.NumGraphics];
            if (graphicIndex < 0 || graphicIndex >= graphicCount) graphicIndex = 0;
            IffChunk? groupChunk = null;
            if (graphicCount > 0)
            {
                var groupId = def[ObjdField.BaseGraphic] + graphicIndex;
                if (groupId is >= 0 and <= ushort.MaxValue)
                    groupChunk = file.Find(ResourceType.Dgrp, (ushort)groupId);
                if (groupChunk is null && graphicIndex != 0)
                    groupChunk = file.Find(ResourceType.Dgrp, def[ObjdField.BaseGraphic]);
            }
            else if (parts.Count == 1)
            {
                var baseId = def[ObjdField.BaseGraphic] > 0 ? (ushort)def[ObjdField.BaseGraphic] : (ushort)100;
                groupChunk = file.Find(ResourceType.Dgrp, baseId) ?? file.Find(ResourceType.Dgrp, 100) ?? file.Find(ResourceType.Dgrp, 200);
            }
            if (groupChunk is null) continue;
            DrawGroup group;
            try { group = DrawGroup.Read(file, groupChunk); }
            catch (ResourceFormatException) { continue; }
            var image = group.Images.FirstOrDefault(candidate => (candidate.Direction & thumbnailDirectionMask) != 0 && candidate.Zoom == zoom)
                ?? group.Images.FirstOrDefault(candidate => candidate.Zoom == zoom);
            if (image is null) continue;

            var tilePixelX = (rx - ry) * tileHalfW;
            var tilePixelY = (rx + ry) * tileHalfH;
            for (var index = 0; index < image.Sprites.Count; index++)
            {
                var sprite = image.Sprites[index];
                int width, height;
                byte[] rgba;
                byte[]? depth = null;
                int paletteId;
                int boundsX = 0;
                bool flip = (sprite.Flags & 1) != 0;
                try
                {
                    if (file.Find(ResourceType.Spr2, (ushort)sprite.SpriteId) is { } depthChunk)
                    {
                        var art = DepthSprite.Read(file, depthChunk);
                        if ((uint)sprite.Frame >= art.Frames.Count) continue;
                        var frame = art.Frames[sprite.Frame];
                        width = frame.Width; height = frame.Height; paletteId = art.PaletteId;
                        depth = frame.Depth;
                        rgba = Colorize(file, paletteId, frame.Width, frame.Height, pixel =>
                        {
                            var alpha = frame.Alpha[pixel];
                            return alpha == 0 ? (byte)0 : (byte)Math.Clamp((alpha * 255 + 15) / 31, 0, 255);
                        }, pixel => frame.Indices[pixel]);
                    }
                    else if (file.Find(ResourceType.Spr1, (ushort)sprite.SpriteId) is { } legacyChunk)
                    {
                        var art = Sprite.Read(file, legacyChunk);
                        if ((uint)sprite.Frame >= art.Frames.Count) continue;
                        var frame = art.Frames[sprite.Frame];
                        width = frame.Width; height = frame.Height; boundsX = frame.BoundsTop; paletteId = art.PaletteId;
                        rgba = Colorize(file, paletteId, width, height, pixel => frame.Opaque[pixel] ? (byte)255 : (byte)0,
                            pixel => frame.Indices[pixel]);
                    }
                    else continue;
                }
                catch (ResourceFormatException) { continue; }
                if (width is < 1 or > 2048 || height is < 1 or > 2048 || rgba.Length != width * height * 4) continue;
                var x = -sprite.Offset2 / 16f;
                var y = -sprite.Offset3 / 16f;
                // Sprite frames are positioned using draw group offsets and elevation scaled by zoom level;
                // sprite frame header coordinates represent world-space depth anchors rather than 2D screen
                // offsets.
                var left = tilePixelX + sprite.X + boundsX;
                var top = tilePixelY + sprite.Y - height - (sprite.Offset1 != 0f ? (int)(sprite.Offset1 * (1 << (zoom + 1))) : 0);
                // Match SceneExporter.SpriteDepth / SceneDepth.Project: 20*sqrt(6) depth units per tile along (rx+ry).
                const float DepthPerTile = 48.989795f;
                var depthOffset = (rx + ry + x + y + (sprite.Offset1 / 5f) * 0.81649658f) * DepthPerTile;
                layers.Add(new IconLayer(left, top, width, height, flip, (rx + ry) * 16f + x + y, layerSequence++, rgba, depth, depthOffset));
            }
        }
        if (layers.Count == 0) return null;
        var minX = layers.Min(layer => layer.X); var minY = layers.Min(layer => layer.Y);
        var widthOut = layers.Max(layer => layer.X + layer.Width) - minX;
        var heightOut = layers.Max(layer => layer.Y + layer.Height) - minY;
        if (widthOut is < 1 or > 4096 || heightOut is < 1 or > 4096 || (long)widthOut * heightOut > 8_000_000) return null;
        var canvas = new byte[widthOut * heightOut * 4];
        var zBuffer = new float[widthOut * heightOut];
        Array.Fill(zBuffer, float.PositiveInfinity);
        foreach (var layer in layers.OrderBy(layer => layer.Order).ThenBy(layer => layer.Index))
        for (var sy = 0; sy < layer.Height; sy++)
        for (var sx = 0; sx < layer.Width; sx++)
        {
            var srcPixel = sy * layer.Width + (layer.Flip ? layer.Width - 1 - sx : sx);
            var source = srcPixel * 4;
            var alpha = layer.Rgba[source + 3]; if (alpha == 0) continue;
            var dstPixel = (layer.Y - minY + sy) * widthOut + layer.X - minX + sx;
            float z = (layer.Depth is { } layerDepth ? layerDepth[srcPixel] : 128f) - layer.DepthOffset;
            if (z > zBuffer[dstPixel] + 0.01f) continue;
            var target = dstPixel * 4;
            if (alpha == 255) layer.Rgba.AsSpan(source, 4).CopyTo(canvas.AsSpan(target, 4));
            else
            {
                var oldAlpha = canvas[target + 3] / 255f;
                var nextAlpha = alpha / 255f;
                var outputAlpha = nextAlpha + oldAlpha * (1 - nextAlpha);
                for (var channel = 0; channel < 3; channel++)
                    canvas[target + channel] = (byte)((layer.Rgba[source + channel] * nextAlpha + canvas[target + channel] * oldAlpha * (1 - nextAlpha)) / outputAlpha);
                canvas[target + 3] = (byte)(outputAlpha * 255);
            }
            if (alpha >= 128) zBuffer[dstPixel] = z;
        }
        return (widthOut, heightOut, canvas);
    }

    private static byte[] Colorize(IffFile file, int paletteId, int width, int height,
        Func<int, byte> alpha, Func<int, byte> colorIndex)
    {
        var id = paletteId is > 0 and < 1000 ? (ushort)paletteId : (ushort)0;
        var paletteChunk = file.Find(ResourceType.Palt, id) ?? file.OfType(ResourceType.Palt).FirstOrDefault();
        if (paletteChunk is null) return [];
        var colors = Palette.Read(file, paletteChunk).Colors;
        var output = new byte[width * height * 4];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var a = alpha(pixel); var index = colorIndex(pixel);
            if (a == 0 || index >= colors.Count) continue;
            var color = colors[index]; var at = pixel * 4;
            output[at] = color.Red; output[at + 1] = color.Green; output[at + 2] = color.Blue; output[at + 3] = a;
        }
        return output;
    }

    private static (byte Red, byte Green, byte Blue, byte Alpha) DownsampleAlpha(byte[] canvas, int width, int height,
        int dstX, int dstY, int outWidth, int outHeight)
    {
        if (outWidth == width && outHeight == height)
        {
            var at = (dstY * width + dstX) * 4;
            var a = canvas[at + 3];
            return a == 0 ? ((byte)0, (byte)0, (byte)0, (byte)0) : (canvas[at], canvas[at + 1], canvas[at + 2], a);
        }

        float scaleX = width / (float)outWidth;
        float scaleY = height / (float)outHeight;
        float fx0 = dstX * scaleX;
        float fx1 = (dstX + 1) * scaleX;
        float fy0 = dstY * scaleY;
        float fy1 = (dstY + 1) * scaleY;
        int x0 = Math.Clamp((int)MathF.Floor(fx0), 0, width - 1);
        int x1 = Math.Clamp((int)MathF.Ceiling(fx1), x0 + 1, width);
        int y0 = Math.Clamp((int)MathF.Floor(fy0), 0, height - 1);
        int y1 = Math.Clamp((int)MathF.Ceiling(fy1), y0 + 1, height);

        float sumR = 0f, sumG = 0f, sumB = 0f, sumAlpha = 0f, totalWeight = 0f;
        for (var y = y0; y < y1; y++)
        {
            float wy = MathF.Min(fy1, y + 1) - MathF.Max(fy0, y);
            if (wy <= 0f) continue;
            for (var x = x0; x < x1; x++)
            {
                float wx = MathF.Min(fx1, x + 1) - MathF.Max(fx0, x);
                if (wx <= 0f) continue;
                float coverage = wx * wy;
                totalWeight += coverage;
                var at = (y * width + x) * 4;
                var a = canvas[at + 3];
                if (a == 0) continue;
                float alphaWeight = coverage * (a / 255f);
                sumR += canvas[at] * alphaWeight;
                sumG += canvas[at + 1] * alphaWeight;
                sumB += canvas[at + 2] * alphaWeight;
                sumAlpha += alphaWeight;
            }
        }

        if (sumAlpha <= 1e-6f || totalWeight <= 0f)
            return ((byte)0, (byte)0, (byte)0, (byte)0);

        int outAlpha = Math.Clamp((int)MathF.Round((sumAlpha / totalWeight) * 255f), 0, 255);
        if (outAlpha == 0)
            return ((byte)0, (byte)0, (byte)0, (byte)0);

        return (
            (byte)Math.Clamp((int)MathF.Round(sumR / sumAlpha), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(sumG / sumAlpha), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(sumB / sumAlpha), 0, 255),
            (byte)outAlpha
        );
    }

    private static (byte Red, byte Green, byte Blue, byte Alpha) DownsampleBlended(byte[] canvas, int width, int height,
        float u, float v, float du, float dv, (byte R, byte G, byte B) bg)
    {
        var x0 = (int)(u * width); var y0 = (int)(v * height);
        var x1 = Math.Max(x0 + 1, (int)((u + du) * width)); var y1 = Math.Max(y0 + 1, (int)((v + dv) * height));
        int r = 0, g = 0, b = 0, alphaSum = 0, total = 0;
        for (var y = y0; y < Math.Min(y1, height); y++)
        for (var x = x0; x < Math.Min(x1, width); x++)
        {
            total++;
            var at = (y * width + x) * 4;
            var a = canvas[at + 3];
            if (a == 0) continue;
            r += canvas[at] * a;
            g += canvas[at + 1] * a;
            b += canvas[at + 2] * a;
            alphaSum += a;
        }
        if (alphaSum == 0 || total == 0) return ((byte)255, (byte)0, (byte)255, (byte)255);
        var maxAlpha = total * 255;
        var bgAlpha = maxAlpha - alphaSum;
        return (
            (byte)Math.Clamp((r + bg.R * bgAlpha + maxAlpha / 2) / maxAlpha, 0, 255),
            (byte)Math.Clamp((g + bg.G * bgAlpha + maxAlpha / 2) / maxAlpha, 0, 255),
            (byte)Math.Clamp((b + bg.B * bgAlpha + maxAlpha / 2) / maxAlpha, 0, 255),
            (byte)255
        );
    }

    private static byte[] WriteIconBitmap(int width, int height, Func<int, int, (byte Red, byte Green, byte Blue, byte Alpha)> pixelAt)
    {
        using var data = new MemoryStream(); using var writer = new BinaryWriter(data);
        writer.Write((ushort)0x4d42); writer.Write(54 + width * height * 4); writer.Write(0); writer.Write(54); writer.Write(40);
        writer.Write(width); writer.Write(-height); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(0);
        writer.Write(width * height * 4); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var pixel = pixelAt(x, y);
            if (pixel.Alpha == 0)
            {
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((byte)0);
            }
            else
            {
                writer.Write(pixel.Blue);
                writer.Write(pixel.Green);
                writer.Write(pixel.Red);
                writer.Write(pixel.Alpha);
            }
        }
        writer.Flush(); return data.ToArray();
    }

    private static object StoreImage(ReadOnlySpan<byte> data, int columns, string session)
    {
        var file = "ui-" + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() + ".bmp";
        Directory.CreateDirectory(session);
        var path = Path.Combine(session, file);
        if (!File.Exists(path)) File.WriteAllBytes(path, data.ToArray());
        return new { file, columns, rows = 1 };
    }
}
