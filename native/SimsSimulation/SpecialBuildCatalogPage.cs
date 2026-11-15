using System.Security.Cryptography;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.World;

namespace SimsSimulation;

/// <summary>Catalog entries whose native placement tools consume a style or roof pattern rather than a floor/wall
/// pattern. Mac Catalog.s creates these as special catalog products: wall styles dispatch to the wall placer with
/// (pattern, style) and roof entries dispatch to the roofer with the roof pattern id.</summary>
internal static class SpecialBuildCatalogPage
{
    // Mac Catalog.s UICatalog wall-style ordering and pattern IDs (the same five style resources installed by the game).
    private static readonly int[] WallStyleIds = [1, 2, 13, 12, 14];
    private static readonly int[] WallStylePatterns = [0, 248, 250, 249, 251];

    public static object WallStyles(string game, ObjectRegistry registry, byte language, int offset, int count, string session)
    {
        Validate(offset, count);
        var gameData = Path.Combine(game, "GameData");
        if (!Directory.Exists(gameData)) gameData = game;
        var buildPath = Directory.EnumerateFiles(gameData).FirstOrDefault(path => Path.GetFileName(path).Equals("Build.iff", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("Build.iff was not found", gameData);
        var wallPath = Directory.EnumerateFiles(gameData).FirstOrDefault(path => Path.GetFileName(path).Equals("walls.iff", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("walls.iff was not found", gameData);

        var build = IffFile.Open(buildPath);
        var walls = IffFile.Open(wallPath);
        var stringsChunk = build.Find(ResourceType.Str, 129) ?? throw new InvalidDataException("Build.iff has no wall STR# 129 (0x81)");
        var strings = StringTable.Read(build, stringsChunk).ForLanguage(language).ToArray();
        var wallCatalog = WallCatalog.Open(game, language);

        var items = WallStyleIds.Select((id, index) =>
        {
            var pattern = WallStylePatterns[index];
            var at = index * 3;
            var name = at + 1 < strings.Length ? strings[at + 1] : "";
            var description = at + 2 < strings.Length ? strings[at + 2] : "";
            var price = wallCatalog.Price(id);
            var chunk = walls.Find(ResourceType.Parse("BMP_"), (ushort)id);
            var icon = Store(chunk?.Data.ToArray(), 2, session);
            var preview = WallCatalogPage.WallSegmentPreview(walls, walls, (ushort)id, checked((ushort)(1792 + pattern)), session);
            return new
            {
                id,
                pattern = (int?)pattern,
                guid = (uint?)null,
                name,
                description,
                price,
                file = "wallstyles/" + id,
                icon,
                preview,
            };
        }).ToList();

        foreach (var source in registry.ByGuid.Values)
        {
            var definition = source.Definition;
            var master = definition[ObjdField.MasterId];
            var subIndex = definition[ObjdField.SubIndex];
            if (definition[ObjdField.BuildModeType] != 6 || definition[ObjdField.Disabled] != 0 || definition[ObjdField.Price] < 0 ||
                (master != 0 && subIndex != ushort.MaxValue) || (master == 0 && subIndex != 0) ||
                (definition[ObjdField.NumGraphics] == 0 && master == 0)) continue;
            items.Add(new
            {
                id = 0,
                pattern = (int?)null,
                guid = (uint?)source.Guid,
                name = source.CatalogString(0, language) ?? source.Chunk.Label,
                description = source.CatalogString(1, language) ?? "",
                price = (int)definition[ObjdField.Price],
                file = "objects/" + source.Guid.ToString("X8"),
                icon = ObjectCatalog.CatalogIcon(source, session),
                preview = ObjectCatalog.CatalogPreview(source, session),
            });
        }

        var ordered = items
            .OrderBy(item => item.price)
            .ThenBy(item => item.guid.HasValue)
            .ThenBy(item => item.guid ?? (uint)item.id)
            .ToArray();

        return new
        {
            schemaVersion = 1,
            kind = "wallStyle",
            offset,
            total = ordered.Length,
            items = ordered.Skip(offset).Take(count).ToArray(),
        };
    }

    public static object Roofs(string game, int offset, int count, string session)
    {
        Validate(offset, count);
        var styles = LotRoof.Styles(game);
        var items = styles.Select((style, id) =>
        {
            var path = LotRoof.MaterialPath(game, style);
            byte[]? data = path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
            var icon = Store(data, 1, session);
            var preview = RoofPatternPreview(data, session);
            return new { id, name = style, description = "", price = 0, file = style, icon, preview };
        }).ToArray();
        return new
        {
            schemaVersion = 1,
            kind = "roof",
            offset,
            total = items.Length,
            items = items.Skip(offset).Take(count).ToArray(),
        };
    }

    // Sims.exe FUN_00529cbc + FUN_004cbd1c (case 5, tiled=true): tiles the roof bitmap across the 168x127 preview box inset by 16px on all 4 sides (136x95).
    private static object? RoofPatternPreview(byte[]? bmp, string session)
    {
        if (bmp is not { Length: >= 54 } || bmp[0] != 'B' || bmp[1] != 'M') return null;
        int dataOffset = BitConverter.ToInt32(bmp, 10);
        int headerSize = BitConverter.ToInt32(bmp, 14);
        int srcW = BitConverter.ToInt32(bmp, 18);
        int srcH = BitConverter.ToInt32(bmp, 22);
        bool topDown = srcH < 0;
        srcH = Math.Abs(srcH);
        ushort bpp = BitConverter.ToUInt16(bmp, 28);
        uint compression = BitConverter.ToUInt32(bmp, 30);
        if (srcW <= 0 || srcH <= 0 || srcW > 512 || srcH > 512 || compression != 0 || (bpp != 8 && bpp != 24 && bpp != 32))
            return null;

        int paletteOffset = 14 + headerSize;
        int srcStride = ((srcW * bpp + 31) / 32) * 4;
        if (dataOffset < 0 || dataOffset + srcStride * srcH > bmp.Length) return null;

        const int outW = 136;
        const int outH = 95;
        var outBytes = new byte[54 + outW * outH * 4];
        outBytes[0] = (byte)'B';
        outBytes[1] = (byte)'M';
        BitConverter.TryWriteBytes(outBytes.AsSpan(2, 4), outBytes.Length);
        BitConverter.TryWriteBytes(outBytes.AsSpan(10, 4), 54);
        BitConverter.TryWriteBytes(outBytes.AsSpan(14, 4), 40);
        BitConverter.TryWriteBytes(outBytes.AsSpan(18, 4), outW);
        BitConverter.TryWriteBytes(outBytes.AsSpan(22, 4), outH);
        BitConverter.TryWriteBytes(outBytes.AsSpan(26, 2), (ushort)1);
        BitConverter.TryWriteBytes(outBytes.AsSpan(28, 2), (ushort)32);
        BitConverter.TryWriteBytes(outBytes.AsSpan(34, 4), outW * outH * 4);

        for (var y = 0; y < outH; y++)
        {
            var sy = y % srcH;
            var srcRow = topDown ? sy : (srcH - 1 - sy);
            var rowBase = dataOffset + srcRow * srcStride;
            for (var x = 0; x < outW; x++)
            {
                var sx = x % srcW;
                byte b, g, r;
                if (bpp == 8)
                {
                    var idx = bmp[rowBase + sx];
                    var pal = paletteOffset + idx * 4;
                    if (pal + 3 > bmp.Length) { b = 0; g = 0; r = 0; }
                    else { b = bmp[pal]; g = bmp[pal + 1]; r = bmp[pal + 2]; }
                }
                else if (bpp == 24)
                {
                    var p = rowBase + sx * 3;
                    b = bmp[p]; g = bmp[p + 1]; r = bmp[p + 2];
                }
                else
                {
                    var p = rowBase + sx * 4;
                    b = bmp[p]; g = bmp[p + 1]; r = bmp[p + 2];
                }
                var dst = 54 + ((outH - 1 - y) * outW + x) * 4;
                outBytes[dst] = b;
                outBytes[dst + 1] = g;
                outBytes[dst + 2] = r;
                outBytes[dst + 3] = 255;
            }
        }
        return Store(outBytes, 1, session);
    }

    private static void Validate(int offset, int count)
    {
        if (offset is < 0 or > 1_000_000 || count is < 1 or > 64)
            throw new ArgumentException("Invalid native build catalog page");
    }

    private static object? Store(byte[]? data, int columns, string session)
    {
        if (data is not { Length: >= 54 and <= 4 * 1024 * 1024 } || data[0] != 'B' || data[1] != 'M') return null;
        var file = "ui-" + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() + ".bmp";
        Directory.CreateDirectory(session);
        var path = Path.Combine(session, file);
        if (!File.Exists(path)) File.WriteAllBytes(path, data);
        return new { file, columns, rows = 1 };
    }
}
