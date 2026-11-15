using System.Buffers.Binary;
using System.Security.Cryptography;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.World;

namespace SimsSimulation;

/// <summary>Wallpaper catalog projection for the native runtime. Build.iff STR# 0x83 supplies base price, name
/// and description triples; standalone .wll files in GameData/Walls supply dynamic wallpapers starting at ID 256
/// (FUN_004c5715, FUN_004c5da6). Base thumbnails use walls.iff BMP_ 512 + pattern (FUN_004cbf52); popup previews
/// render the isometric wall segment at medium zoom (FUN_004e5493, FUN_00540c42).</summary>
internal static class WallCatalogPage
{
    private sealed record Item(int Id, string Name, string Description, int Price, string File, IffFile ArtFile, ushort PatternSpriteId, ushort? BmpChunkId);

    public static object Page(string game, byte language, int offset, int count, string session)
    {
        if (offset is < 0 or > 1_000_000 || count is < 1 or > 64) throw new ArgumentException("Invalid native wallpaper catalog page");
        var gameData = Path.Combine(game, "GameData");
        if (!Directory.Exists(gameData)) gameData = game;
        var buildPath = Directory.EnumerateFiles(gameData).FirstOrDefault(path => Path.GetFileName(path).Equals("Build.iff", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("Build.iff was not found", gameData);
        var wallPath = Directory.EnumerateFiles(gameData).FirstOrDefault(path => Path.GetFileName(path).Equals("walls.iff", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("walls.iff was not found", gameData);
        var build = IffFile.Open(buildPath);
        var walls = IffFile.Open(wallPath);
        var stringsChunk = build.Find(ResourceType.Str, 0x83) ?? throw new InvalidDataException("Build.iff has no wallpaper STR# 0x83");
        var strings = StringTable.Read(build, stringsChunk).ForLanguage(language).ToArray();
        var items = new List<Item>();
        var patterns = Math.Min(30, strings.Length / 3);
        for (var pattern = 1; pattern <= patterns; pattern++)
        {
            var spriteId = checked((ushort)(1792 + pattern));
            if (walls.Find(ResourceType.Spr1, spriteId) is null) continue;
            var at = (pattern - 1) * 3;
            var price = int.TryParse(strings[at].Trim(), out var parsed) ? parsed : 0;
            items.Add(new Item(pattern, strings[at + 1], strings[at + 2], price, "global", walls, spriteId, checked((ushort)(512 + pattern))));
        }
        foreach (var (id, path) in WallpaperCatalog.DynamicFiles(gameData))
        {
            var iff = IffFile.Open(path);
            if (iff.Find(ResourceType.Spr1, 1793) is null) continue;
            var table = iff.Find(ResourceType.Str, 0);
            var values = table is null ? Array.Empty<string>() : StringTable.Read(iff, table).ForLanguage(language).ToArray();
            var name = values.ElementAtOrDefault(0) ?? Path.GetFileNameWithoutExtension(path);
            var price = int.TryParse(values.ElementAtOrDefault(1)?.Trim(), out var parsed) ? parsed : 0;
            var description = values.ElementAtOrDefault(2) ?? "";
            items.Add(new Item(id, name, description, price, Path.GetFileName(path), iff, 1793, null));
        }
        var ordered = items.OrderBy(item => item.Price).ThenBy(item => item.Id).ToArray();
        return new
        {
            schemaVersion = 1, kind = "wall", offset, total = ordered.Length,
            items = ordered.Skip(offset).Take(count).Select(item => new
            {
                id = item.Id, name = item.Name, description = item.Description, price = item.Price,
                file = item.File,
                icon = (item.BmpChunkId is { } bmpId && walls.Find(ResourceType.Parse("BMP_"), bmpId) is { } bmpChunk
                    ? StoreBmp(bmpChunk.Data.ToArray(), 2, session)
                    : null) ?? WallThumbnailIcon(walls, item.ArtFile, item.PatternSpriteId, session),
                preview = WallSegmentPreview(walls, item.ArtFile, 1, item.PatternSpriteId, session),
            }).ToArray(),
        };
    }

    public static object[] All(string game, byte language)
    {
        var gameData = Path.Combine(game, "GameData");
        if (!Directory.Exists(gameData)) gameData = game;
        var buildPath = Directory.EnumerateFiles(gameData).FirstOrDefault(path => Path.GetFileName(path).Equals("Build.iff", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("Build.iff was not found", gameData);
        var wallPath = Directory.EnumerateFiles(gameData).FirstOrDefault(path => Path.GetFileName(path).Equals("walls.iff", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("walls.iff was not found", gameData);
        var build = IffFile.Open(buildPath);
        var walls = IffFile.Open(wallPath);
        var stringsChunk = build.Find(ResourceType.Str, 0x83) ?? throw new InvalidDataException("Build.iff has no wallpaper STR# 0x83");
        var strings = StringTable.Read(build, stringsChunk).ForLanguage(language).ToArray();
        var items = new List<(int Id, string File, string Name, int Price)>();
        var patterns = Math.Min(30, strings.Length / 3);
        for (var pattern = 1; pattern <= patterns; pattern++)
        {
            var spriteId = checked((ushort)(1792 + pattern));
            if (walls.Find(ResourceType.Spr1, spriteId) is null) continue;
            var at = (pattern - 1) * 3;
            var price = int.TryParse(strings[at].Trim(), out var parsed) ? parsed : 0;
            items.Add((pattern, "global", strings[at + 1], price));
        }
        foreach (var (id, path) in WallpaperCatalog.DynamicFiles(gameData))
        {
            var iff = IffFile.Open(path);
            if (iff.Find(ResourceType.Spr1, 1793) is null) continue;
            var table = iff.Find(ResourceType.Str, 0);
            var values = table is null ? Array.Empty<string>() : StringTable.Read(iff, table).ForLanguage(language).ToArray();
            var name = values.ElementAtOrDefault(0) ?? Path.GetFileNameWithoutExtension(path);
            var price = int.TryParse(values.ElementAtOrDefault(1)?.Trim(), out var parsed) ? parsed : 0;
            items.Add((id, Path.GetFileName(path), name, price));
        }
        return items.OrderBy(item => item.Id).Select(item => (object)new { id = item.Id, file = item.File, name = item.Name, price = item.Price }).ToArray();
    }

    internal static object? StoreBmp(byte[]? data, int columns, string session)
    {
        if (data is not { Length: >= 54 and <= 4 * 1024 * 1024 } || data[0] != 'B' || data[1] != 'M') return null;
        var file = "ui-" + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() + ".bmp";
        Directory.CreateDirectory(session);
        var path = Path.Combine(session, file);
        if (!File.Exists(path)) File.WriteAllBytes(path, data);
        return new { file, columns, rows = 1 };
    }

    /// <summary>FUN_004e58e7 / FUN_004e5c2f: renders a 37x37 thumbnail for dynamic .wll files that lack a pre-baked BMP_ chunk.</summary>
    private static object? WallThumbnailIcon(IffFile walls, IffFile patternFile, ushort patternSpriteId, string session)
    {
        try
        {
            var patternChunk = patternFile.Find(ResourceType.Spr1, patternSpriteId);
            var maskChunk = walls.Find(ResourceType.Spr1, 514);
            if (patternChunk is null || maskChunk is null) return null;
            var patternSprite = Sprite.Read(patternFile, patternChunk);
            var maskSprite = Sprite.Read(walls, maskChunk);
            var paletteChunk = patternFile.Find(ResourceType.Palt,
                checked((ushort)(patternSprite.PaletteId is >= 0 and <= ushort.MaxValue ? patternSprite.PaletteId : 0)))
                ?? patternFile.OfType(ResourceType.Palt).FirstOrDefault();
            if (paletteChunk is null || patternSprite.Frames.Count < 2 || maskSprite.Frames.Count < 2) return null;
            var colors = Palette.Read(patternFile, paletteChunk).Colors;
            var faceArt = patternSprite.Frames[1];
            var faceMask = maskSprite.Frames[1];
            var capArt = patternSprite.Frames[0];
            var capMask = maskSprite.Frames[0];

            const int boxSize = 37;
            const int sheetW = boxSize * 2;
            const int sheetH = boxSize;
            var bytes = new byte[54 + sheetW * sheetH * 4];
            bytes[0] = (byte)'B'; bytes[1] = (byte)'M'; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), sheetW); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), -sheetH);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 32);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), sheetW * sheetH * 4);
            for (var i = 0; i < sheetW * sheetH; i++)
            {
                var t = 54 + i * 4;
                bytes[t] = 255; bytes[t + 2] = 255; bytes[t + 3] = 255;
            }
            // Box-average the 38px wide (6px left cap + 32px face) x 118px visible height wall segment into x=8..28 (21px) x y=0..36 (37px).
            for (var dstY = 0; dstY < boxSize; dstY++)
            {
                var y0 = 17 + dstY * 118 / boxSize;
                var y1 = Math.Max(y0 + 1, 17 + (dstY + 1) * 118 / boxSize);
                for (var dstX = 8; dstX <= 28; dstX++)
                {
                    var x0 = (dstX - 8) * 38 / 21;
                    var x1 = Math.Max(x0 + 1, (dstX - 7) * 38 / 21);
                    int r = 0, g = 0, b = 0, opaque = 0, total = 0;
                    for (var srcWallY = y0; srcWallY < y1; srcWallY++)
                    for (var srcWallX = x0; srcWallX < x1; srcWallX++)
                    {
                        total++;
                        IndexedFrame art;
                        IndexedFrame mask;
                        int px, py;
                        if (srcWallX < 6)
                        {
                            art = capArt;
                            mask = capMask;
                            px = srcWallX;
                            py = Math.Clamp(srcWallY - 1, 0, art.Height - 1);
                        }
                        else
                        {
                            art = faceArt;
                            mask = faceMask;
                            px = Math.Clamp(srcWallX - 6, 0, art.Width - 1);
                            py = Math.Clamp(srcWallY, 0, art.Height - 1);
                        }
                        var idx = py * art.Width + px;
                        var mIdx = (py * mask.Height / art.Height) * mask.Width + (px * mask.Width / art.Width);
                        if (!art.Opaque[idx] || !mask.Opaque[mIdx] || art.Indices[idx] >= colors.Count) continue;
                        var c = colors[art.Indices[idx]];
                        r += c.Red; g += c.Green; b += c.Blue; opaque++;
                    }
                    if (opaque == 0 || total == 0) continue;
                    var bgCount = total - opaque;
                    for (var col = 0; col < 2; col++)
                    {
                        var bg = col == 0 ? (R: 104, G: 164, B: 184) : (R: 152, G: 196, B: 208);
                        var target = 54 + (dstY * sheetW + col * boxSize + dstX) * 4;
                        bytes[target] = (byte)Math.Clamp((b + bg.B * bgCount + total / 2) / total, 0, 255);
                        bytes[target + 1] = (byte)Math.Clamp((g + bg.G * bgCount + total / 2) / total, 0, 255);
                        bytes[target + 2] = (byte)Math.Clamp((r + bg.R * bgCount + total / 2) / total, 0, 255);
                        bytes[target + 3] = 255;
                    }
                }
            }
            return StoreBmp(bytes, 2, session);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ResourceFormatException or OverflowException)
        { return null; }
    }

    /// <summary>FUN_004e5493 & FUN_00540c42: renders the 160x119 isometric wall/fence segment preview for cWinCatalogPopup.</summary>
    internal static object? WallSegmentPreview(IffFile walls, IffFile patternFile, ushort style, ushort patternSpriteId, string session)
    {
        try
        {
            var patternChunk = patternFile.Find(ResourceType.Spr1, patternSpriteId);
            var maskId = SceneExporter.WallMaskId(style, down: false, zoom: 1);
            var maskChunk = walls.Find(ResourceType.Spr1, maskId);
            if (patternChunk is null || maskChunk is null) return null;
            var patternSprite = Sprite.Read(patternFile, patternChunk);
            var maskSprite = Sprite.Read(walls, maskChunk);
            var paletteChunk = patternFile.Find(ResourceType.Palt,
                checked((ushort)(patternSprite.PaletteId is >= 0 and <= ushort.MaxValue ? patternSprite.PaletteId : 0)))
                ?? patternFile.OfType(ResourceType.Palt).FirstOrDefault();
            if (paletteChunk is null || patternSprite.Frames.Count < 2 || maskSprite.Frames.Count < 2) return null;
            var colors = Palette.Read(patternFile, paletteChunk).Colors;

            const int boxW = 160;
            const int boxH = 119;
            // FUN_004e5493 lines 15-23:
            // iVar4 = 80, iVar2 = (119 * 3) / 4 = 89.
            // iVar3 = iVar2 + 14 (103) for style 1; iVar2 - 16 (73) for style 2, 13, 14; iVar2 - 2 (87) for style 12.
            var anchorX = boxW / 2;
            var baseY = (boxH * 3) / 4;
            var anchorY = style switch
            {
                2 or 13 or 14 => baseY - 16,
                12 => baseY - 2,
                _ => baseY + 14,
            };
            // FUN_00540c42 lines 96-103: at zoom 2, half-width is 16 and bottom is anchorY + 16.
            var bottomY = anchorY + 16;
            var faceLeftX = anchorX - 16;

            var bytes = new byte[54 + boxW * boxH * 4];
            bytes[0] = (byte)'B'; bytes[1] = (byte)'M'; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), boxW); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), -boxH);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 32);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), boxW * boxH * 4);
            for (var i = 0; i < boxW * boxH; i++)
            {
                var t = 54 + i * 4;
                bytes[t] = 255; bytes[t + 2] = 255; bytes[t + 3] = 255;
            }

            void BlitMaskedFrame(IndexedFrame art, IndexedFrame mask, int dstX0, int dstY0, int clipX0, int clipWidth)
            {
                for (var sy = 0; sy < art.Height; sy++)
                {
                    var dy = dstY0 + sy;
                    if (dy < 0 || dy >= boxH) continue;
                    var my = sy * mask.Height / art.Height;
                    for (var sx = clipX0; sx < clipX0 + clipWidth && sx < art.Width; sx++)
                    {
                        var dx = dstX0 + (sx - clipX0);
                        if (dx < 0 || dx >= boxW) continue;
                        var idx = sy * art.Width + sx;
                        var mx = sx * mask.Width / art.Width;
                        if (!art.Opaque[idx] || !mask.Opaque[my * mask.Width + mx] || art.Indices[idx] >= colors.Count) continue;
                        var color = colors[art.Indices[idx]];
                        var target = 54 + (dy * boxW + dx) * 4;
                        bytes[target] = color.Blue; bytes[target + 1] = color.Green; bytes[target + 2] = color.Red; bytes[target + 3] = 255;
                    }
                }
            }

            var faceArt = patternSprite.Frames[1];
            var faceMask = maskSprite.Frames[1];
            var faceTopY = bottomY - faceArt.Height;
            if (style == 1)
            {
                // Draw 6px left end cap from frame 0.
                var capArt = patternSprite.Frames[0];
                var capMask = maskSprite.Frames[0];
                BlitMaskedFrame(capArt, capMask, faceLeftX - 6, bottomY - capArt.Height, 0, 6);
            }
            BlitMaskedFrame(faceArt, faceMask, faceLeftX, faceTopY, 0, faceArt.Width);

            return StoreBmp(bytes, 1, session);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ResourceFormatException or OverflowException)
        { return null; }
    }
}
