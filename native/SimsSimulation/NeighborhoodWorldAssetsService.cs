using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using SimsEngine.Content;
using SimsEngine.Formats;

namespace SimsSimulation;

/// <summary>
/// Extracts and composites authentic multi-tile Sims 1 <c>DGRP/SPR2</c> vehicles (<c>Car*.iff</c>,
/// <c>SchoolBus.iff</c>) and full 2x2 canopy trees (<c>Trees.iff</c>) into cached RGBA assets
/// for connected-neighborhood traffic and greenbelt rendering. Zero synthetic geometric fallbacks.
/// </summary>
internal static class NeighborhoodWorldAssetsService
{
    internal sealed record VehicleSpriteAsset(
        string Id,
        int Direction,
        string FilePath,
        int Width,
        int Height,
        float AnchorX,
        float AnchorY);

    internal sealed record TreeSpriteAsset(
        string Id,
        string FilePath,
        int Width,
        int Height,
        float AnchorX,
        float AnchorY);

    private static readonly string[] VehicleFileNames =
    [
        "CarStd.iff",
        "CarTown.iff",
        "CarSUV.iff",
        "CarBentley.iff",
        "CarJunk.iff",
        "CarLimo.iff",
        "CarTaxi.iff",
        "SchoolBus.iff",
    ];

    private static readonly int[] CardinalDirections = [0, 2, 4, 6];
    private static readonly float MediumUnitHeight = (float)(Math.Sqrt(64 * 64 / 2.0) * Math.Cos(Math.PI / 6));

    private static readonly ConcurrentDictionary<string, (IReadOnlyList<VehicleSpriteAsset> Vehicles, IReadOnlyList<TreeSpriteAsset> Trees)> AssetsCache = new();

    private readonly record struct SpriteLayer(
        int X,
        int Y,
        int Width,
        int Height,
        bool Flip,
        byte[] Rgba,
        float DepthOrder);

    internal static (IReadOnlyList<VehicleSpriteAsset> Vehicles, IReadOnlyList<TreeSpriteAsset> Trees) GetOrBuildAssets(
        ObjectRegistry registry,
        string sceneDirectory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (string.IsNullOrWhiteSpace(sceneDirectory))
            return ([], []);

        var assetsDir = Path.Combine(sceneDirectory, "world_assets");
        if (AssetsCache.TryGetValue(assetsDir, out var cached) &&
            cached.Vehicles.Count > 0 &&
            AssetsExist(cached.Vehicles, cached.Trees))
        {
            return cached;
        }

        bool inMemory = InMemorySessionAssets.IsInMemoryPath(assetsDir);
        if (!inMemory)
            Directory.CreateDirectory(assetsDir);

        var vehicles = ExtractVehicles(registry, assetsDir, inMemory);
        var trees = ExtractTrees(registry, assetsDir, inMemory);
        var result = ((IReadOnlyList<VehicleSpriteAsset>)vehicles, (IReadOnlyList<TreeSpriteAsset>)trees);
        if (vehicles.Count > 0 || trees.Count > 0)
            AssetsCache[assetsDir] = result;
        return result;
    }

    private static bool AssetsExist(IReadOnlyList<VehicleSpriteAsset> vehicles, IReadOnlyList<TreeSpriteAsset> trees)
    {
        for (int i = 0; i < vehicles.Count; i++)
        {
            var p = vehicles[i].FilePath;
            if (!InMemorySessionAssets.TryReadBytes(p, out _) && !File.Exists(p))
                return false;
        }
        for (int i = 0; i < trees.Count; i++)
        {
            var p = trees[i].FilePath;
            if (!InMemorySessionAssets.TryReadBytes(p, out _) && !File.Exists(p))
                return false;
        }
        return true;
    }

    private static List<VehicleSpriteAsset> ExtractVehicles(ObjectRegistry registry, string assetsDir, bool inMemory)
    {
        var assets = new List<VehicleSpriteAsset>(VehicleFileNames.Length * 4);
        var byFileName = GroupMultiTileGroupsByFileName(registry, new HashSet<string>(VehicleFileNames, StringComparer.OrdinalIgnoreCase));

        foreach (var fileName in VehicleFileNames)
        {
            if (!byFileName.TryGetValue(fileName, out var groups) || groups.Count == 0)
                continue;

            var group = groups[0];
            string id = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
            foreach (int dir in CardinalDirections)
            {
                var composite = CompositeMultiTileGroup(group.File, group.Parts, dir);
                if (composite is not { } img || img.Width < 16 || img.Height < 16)
                    continue;

                string filePath = Path.Combine(assetsDir, $"veh_{id}_d{dir}.rgba");
                WriteAssetBytes(filePath, img.Rgba, inMemory);
                assets.Add(new VehicleSpriteAsset(id, dir, filePath, img.Width, img.Height, img.AnchorX, img.AnchorY));
            }
        }

        return assets;
    }

    private static List<TreeSpriteAsset> ExtractTrees(ObjectRegistry registry, string assetsDir, bool inMemory)
    {
        var assets = new List<TreeSpriteAsset>(4);
        var byFileName = GroupMultiTileGroupsByFileName(registry, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Trees.iff" });
        if (!byFileName.TryGetValue("Trees.iff", out var groups) || groups.Count == 0)
            return assets;

        int treeIndex = 0;
        foreach (var group in groups)
        {
            if (group.Label.Contains("Apple", StringComparison.OrdinalIgnoreCase) ||
                group.Label.Contains("Dead", StringComparison.OrdinalIgnoreCase))
            {
                // Skip orchard apple trees / dead trees so hills get lush pine, birch, mulberry & willow foliage.
                if (groups.Count >= 4)
                    continue;
            }

            var composite = CompositeMultiTileGroup(group.File, group.Parts, dir: 0);
            if (composite is not { } img || img.Width < 40 || img.Height < 85)
                continue;

            string id = $"tree_{treeIndex}";
            string filePath = Path.Combine(assetsDir, $"{id}.rgba");
            WriteAssetBytes(filePath, img.Rgba, inMemory);
            assets.Add(new TreeSpriteAsset(id, filePath, img.Width, img.Height, img.AnchorX, img.AnchorY));
            treeIndex++;
            if (assets.Count >= 4)
                break;
        }

        return assets;
    }

    private static void WriteAssetBytes(string path, byte[] bytes, bool inMemory)
    {
        if (inMemory)
            InMemorySessionAssets.StoreFile(path, bytes);
        else
            File.WriteAllBytes(path, bytes);
    }

    private sealed record MultiTileObjectGroup(
        string Label,
        IffFile File,
        ushort MasterId,
        List<(short RawOx, short RawOy, Objd Definition)> Parts);

    private static Dictionary<string, List<MultiTileObjectGroup>> GroupMultiTileGroupsByFileName(
        ObjectRegistry registry,
        HashSet<string> wantedFileNames)
    {
        var entriesByFile = new Dictionary<(string FileName, IffFile File), List<ObjectEntry>>();
        foreach (var entry in registry.Entries)
        {
            if (entry.Definition[ObjdField.Disabled] != 0)
                continue;
            string sourcePath = entry.File.Source.Replace('\\', '/');
            int sep = sourcePath.LastIndexOf("::", StringComparison.Ordinal);
            string innerPath = sep >= 0 ? sourcePath[(sep + 2)..] : sourcePath;
            string fileName = Path.GetFileName(innerPath);
            if (string.IsNullOrEmpty(fileName) || !wantedFileNames.Contains(fileName))
                continue;
            var key = (fileName, entry.File);
            if (!entriesByFile.TryGetValue(key, out var list))
            {
                list = [];
                entriesByFile[key] = list;
            }
            list.Add(entry);
        }

        var result = new Dictionary<string, List<MultiTileObjectGroup>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ((fileName, file), fileEntries) in entriesByFile)
        {
            var masters = fileEntries
                .Where(e => e.Definition[ObjdField.MasterId] > 0 &&
                            unchecked((ushort)e.Definition[ObjdField.SubIndex]) == ushort.MaxValue)
                .OrderBy(e => e.Definition[ObjdField.MasterId])
                .ToList();

            var groups = new List<MultiTileObjectGroup>(masters.Count);
            foreach (var master in masters)
            {
                ushort mid = (ushort)master.Definition[ObjdField.MasterId];
                var parts = fileEntries
                    .Where(e => e.Definition[ObjdField.MasterId] == mid &&
                                unchecked((ushort)e.Definition[ObjdField.SubIndex]) != ushort.MaxValue &&
                                e.Definition[ObjdField.BaseGraphic] > 0)
                    .Select(e =>
                    {
                        ushort sub = unchecked((ushort)e.Definition[ObjdField.SubIndex]);
                        short ox = unchecked((sbyte)(sub >> 8));
                        short oy = (byte)(sub & 0xFF);
                        return (ox, oy, e.Definition);
                    })
                    .ToList();
                if (parts.Count > 0)
                    groups.Add(new MultiTileObjectGroup(master.Chunk.Label, file, mid, parts));
            }

            if (groups.Count == 0)
            {
                var singles = fileEntries
                    .Where(e => e.Definition[ObjdField.BaseGraphic] > 0)
                    .OrderBy(e => e.Chunk.Id)
                    .Select(e => new MultiTileObjectGroup(
                        e.Chunk.Label,
                        file,
                        (ushort)e.Definition[ObjdField.MasterId],
                        [(0, 0, e.Definition)]));
                groups.AddRange(singles);
            }

            if (!result.TryGetValue(fileName, out var existing))
            {
                existing = [];
                result[fileName] = existing;
            }
            existing.AddRange(groups);
        }

        return result;
    }

    private static (int Width, int Height, float AnchorX, float AnchorY, byte[] Rgba)? CompositeMultiTileGroup(
        IffFile file,
        List<(short RawOx, short RawOy, Objd Definition)> parts,
        int dir)
    {
        if (parts.Count == 0)
            return null;

        var camera = new LotCamera(0, 64, 64);
        var layers = new List<SpriteLayer>(parts.Count * 2);
        var spr2Cache = new Dictionary<ushort, DepthSprite>();
        var spr1Cache = new Dictionary<ushort, Sprite>();

        float sumX = 0f;
        float sumY = 0f;
        var rotatedParts = new (float PartX, float PartY, Objd Definition)[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            var (rx, ry) = RotateCardinalOffset(dir, parts[i].RawOx, parts[i].RawOy);
            rotatedParts[i] = (rx, ry, parts[i].Definition);
            sumX += rx;
            sumY += ry;
        }
        float centroidX = sumX / parts.Count;
        float centroidY = sumY / parts.Count;

        foreach (var (partX, partY, def) in rotatedParts)
        {
            ushort baseGraphic = (ushort)def[ObjdField.BaseGraphic];
            var dgrpChunk = file.Find(ResourceType.Dgrp, baseGraphic);
            if (dgrpChunk is null)
                continue;

            DrawGroup group;
            try { group = DrawGroup.Read(file, dgrpChunk); }
            catch { continue; }

            var image = SceneExporter.SelectedImage(group, dir, cameraOrientation: 0, zoom: 2);
            if (image is null)
                continue;

            ushort dynBase = (ushort)def[ObjdField.DynamicSpritesBaseId];
            ushort dynNum = (ushort)def[ObjdField.NumDynamicSprites];

            for (int si = 0; si < image.Sprites.Count; si++)
            {
                var sprite = image.Sprites[si];
                if (dynNum > 0 && sprite.SpriteId >= dynBase && sprite.SpriteId < dynBase + dynNum)
                    continue;

                byte[]? rgba = DecodePartRgba(file, sprite, spr2Cache, spr1Cache, out int w, out int h);
                if (rgba is null || w <= 0 || h <= 0)
                    continue;

                var (offX, offY) = RotateCardinalOffset(dir, sprite.Offset2, sprite.Offset3);
                float worldTileX = 32.5f + partX + offX / 16f;
                float worldTileY = 32.5f + partY + offY / 16f;
                Vector2 pt = camera.Project(worldTileX, worldTileY, 0f);
                int spriteX = (int)(pt.X + sprite.X);
                int spriteY = (int)(pt.Y - sprite.Offset1 / 5f * MediumUnitHeight - h + sprite.Y);
                float depthOrder = (partX + partY) + (offX + offY) / 16f + si * 0.0001f;
                layers.Add(new SpriteLayer(spriteX, spriteY, w, h, (sprite.Flags & 1) != 0, rgba, depthOrder));
            }
        }

        if (layers.Count == 0)
            return null;

        layers.Sort((a, b) => a.DepthOrder.CompareTo(b.DepthOrder));

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int i = 0; i < layers.Count; i++)
        {
            var l = layers[i];
            if (l.X < minX) minX = l.X;
            if (l.Y < minY) minY = l.Y;
            if (l.X + l.Width > maxX) maxX = l.X + l.Width;
            if (l.Y + l.Height > maxY) maxY = l.Y + l.Height;
        }

        int outW = maxX - minX;
        int outH = maxY - minY;
        if (outW <= 0 || outH <= 0 || outW > 2048 || outH > 2048)
            return null;

        var outBuffer = new byte[outW * outH * 4];
        for (int i = 0; i < layers.Count; i++)
        {
            var l = layers[i];
            int dstBaseX = l.X - minX;
            int dstBaseY = l.Y - minY;
            for (int sy = 0; sy < l.Height; sy++)
            {
                int dy = dstBaseY + sy;
                for (int sx = 0; sx < l.Width; sx++)
                {
                    int srcX = l.Flip ? (l.Width - 1 - sx) : sx;
                    int srcAt = (sy * l.Width + srcX) * 4;
                    byte sa = l.Rgba[srcAt + 3];
                    if (sa == 0)
                        continue;

                    int dstAt = (dy * outW + (dstBaseX + sx)) * 4;
                    if (sa == 255 || outBuffer[dstAt + 3] == 0)
                    {
                        outBuffer[dstAt] = l.Rgba[srcAt];
                        outBuffer[dstAt + 1] = l.Rgba[srcAt + 1];
                        outBuffer[dstAt + 2] = l.Rgba[srcAt + 2];
                        outBuffer[dstAt + 3] = sa;
                    }
                    else
                    {
                        int invA = 255 - sa;
                        outBuffer[dstAt] = (byte)((l.Rgba[srcAt] * sa + outBuffer[dstAt] * invA + 127) / 255);
                        outBuffer[dstAt + 1] = (byte)((l.Rgba[srcAt + 1] * sa + outBuffer[dstAt + 1] * invA + 127) / 255);
                        outBuffer[dstAt + 2] = (byte)((l.Rgba[srcAt + 2] * sa + outBuffer[dstAt + 2] * invA + 127) / 255);
                        outBuffer[dstAt + 3] = (byte)Math.Min(255, sa + (outBuffer[dstAt + 3] * invA + 127) / 255);
                    }
                }
            }
        }

        // Trim transparent padding cleanly around final composited sprite
        int cropMinX = outW, cropMinY = outH, cropMaxX = 0, cropMaxY = 0;
        for (int y = 0; y < outH; y++)
        {
            for (int x = 0; x < outW; x++)
            {
                if (outBuffer[(y * outW + x) * 4 + 3] == 0)
                    continue;
                if (x < cropMinX) cropMinX = x;
                if (y < cropMinY) cropMinY = y;
                if (x + 1 > cropMaxX) cropMaxX = x + 1;
                if (y + 1 > cropMaxY) cropMaxY = y + 1;
            }
        }
        if (cropMaxX <= cropMinX || cropMaxY <= cropMinY)
            return null;

        int finalW = cropMaxX - cropMinX;
        int finalH = cropMaxY - cropMinY;
        var cropped = new byte[finalW * finalH * 4];
        for (int y = 0; y < finalH; y++)
        {
            Buffer.BlockCopy(outBuffer, ((cropMinY + y) * outW + cropMinX) * 4, cropped, y * finalW * 4, finalW * 4);
        }

        Vector2 anchorPt = camera.Project(32.5f + centroidX, 32.5f + centroidY, 0f);
        float anchorX = anchorPt.X - (minX + cropMinX);
        float anchorY = anchorPt.Y - (minY + cropMinY);
        return (finalW, finalH, anchorX, anchorY, cropped);
    }

    private static byte[]? DecodePartRgba(
        IffFile file,
        DrawGroupSprite part,
        Dictionary<ushort, DepthSprite> spr2Cache,
        Dictionary<ushort, Sprite> spr1Cache,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        bool doubleAlpha = (part.Flags & 0x10) != 0;
        ushort sprId = (ushort)part.SpriteId;

        var spr2Chunk = file.Find(ResourceType.Spr2, sprId);
        if (spr2Chunk is not null)
        {
            if (!spr2Cache.TryGetValue(sprId, out var art))
            {
                art = DepthSprite.Read(file, spr2Chunk);
                spr2Cache[sprId] = art;
            }
            if (part.Frame < 0 || part.Frame >= art.Frames.Count)
                return null;
            var f = art.Frames[part.Frame];
            if (f.Width <= 0 || f.Height <= 0 || f.Indices.Length != f.Width * f.Height || f.Alpha.Length != f.Indices.Length)
                return null;
            var palette = SceneExporter.PaletteFor(file, art.PaletteId);
            if (palette is null)
                return null;

            width = f.Width;
            height = f.Height;
            var rgba = new byte[width * height * 4];
            for (int px = 0; px < f.Indices.Length; px++)
            {
                if (f.Alpha[px] < 8)
                    continue;
                var color = palette[f.Indices[px]];
                byte alpha = (byte)Math.Clamp((f.Alpha[px] * 255 + 15) / 31, 0, 255);
                if (doubleAlpha)
                    alpha = alpha < 0x80 ? (byte)(alpha << 1) : (byte)0xff;
                int at = px * 4;
                rgba[at] = color.Red;
                rgba[at + 1] = color.Green;
                rgba[at + 2] = color.Blue;
                rgba[at + 3] = alpha;
            }
            return rgba;
        }

        var spr1Chunk = file.Find(ResourceType.Spr1, sprId);
        if (spr1Chunk is null)
            return null;
        if (!spr1Cache.TryGetValue(sprId, out var legacy))
        {
            legacy = Sprite.Read(file, spr1Chunk);
            spr1Cache[sprId] = legacy;
        }
        if (part.Frame < 0 || part.Frame >= legacy.Frames.Count)
            return null;
        var lf = legacy.Frames[part.Frame];
        if (lf.Width <= 0 || lf.Height <= 0 || lf.Indices.Length != lf.Width * lf.Height || lf.Opaque.Length != lf.Indices.Length)
            return null;
        var legacyPalette = SceneExporter.PaletteFor(file, legacy.PaletteId);
        if (legacyPalette is null)
            return null;

        width = lf.Width;
        height = lf.Height;
        var legacyRgba = new byte[width * height * 4];
        for (int px = 0; px < lf.Indices.Length; px++)
        {
            if (!lf.Opaque[px])
                continue;
            var color = legacyPalette[lf.Indices[px]];
            int at = px * 4;
            legacyRgba[at] = color.Red;
            legacyRgba[at + 1] = color.Green;
            legacyRgba[at + 2] = color.Blue;
            legacyRgba[at + 3] = 255;
        }
        return legacyRgba;
    }

    private static (float X, float Y) RotateCardinalOffset(int direction, float dx, float dy) => (direction & 7) switch
    {
        0 => (dx, dy),
        2 => (-dy, dx),
        4 => (-dx, -dy),
        6 => (dy, -dx),
        _ => (0f, 0f),
    };
}
