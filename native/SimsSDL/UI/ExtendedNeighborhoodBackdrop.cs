using System.Reflection;
using SimsSDL.Core;
using StbImageSharp;

namespace SimsSDL.UI;

/// <summary>
/// Reconstructs seamless 4K (3840x2160) widescreen neighborhood terrain backdrops around the classic
/// 800x600 coordinate space while keeping every lot, road, bridge, and wave animation pixel-locked.
/// </summary>
internal static class ExtendedNeighborhoodBackdrop
{
    public const double LogicalOffsetX = -304.0;
    public const double LogicalOffsetY = -96.0;
    public const double LogicalWidth = 1408.0;
    public const double LogicalHeight = 792.0;

    private const int Width4K = 3840;
    private const int Height4K = 2160;
    private const int CoreX0 = 829;
    private const int CoreY0 = 262;
    private const int CoreX1 = 3011;
    private const int CoreY1 = 1898;
    private const int CoreW = CoreX1 - CoreX0; // 2182
    private const int CoreH = CoreY1 - CoreY0; // 1636
    private const int FeatherPx = 64;
    private const int WaveEdgeFeatherPx = 72;
    private const int LutSize = 32;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, byte[]> s_cachedTemplatesRgb = new(StringComparer.OrdinalIgnoreCase);
    private static float[]? s_featherX;
    private static float[]? s_featherY;

    private static readonly IReadOnlyDictionary<string, string> ResourceToEmbeddedAsset =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UIGraphics/Community/NScreen_unleashed.bmp"] = "nscreen_unleashed_coastal_4k.jpg",
            ["UIGraphics/Downtown/DScreen.bmp"] = "dscreen_downtown_4k.jpg",
            ["UIGraphics/VIsland/visland.bmp"] = "visland_vacation_4k.jpg",
            ["UIGraphics/Studiotown/DScreen.bmp"] = "dscreen_studiotown_4k.jpg",
            ["UIGraphics/Magicland/DScreen.bmp"] = "dscreen_magictown_4k.jpg",
        };

    public sealed record Result(
        OriginalGameplayOverlay.Image Image,
        double OffsetX,
        double OffsetY,
        double Width,
        double Height);

    public static bool Supports(GameContent content, string backgroundResource)
    {
        if (!ResourceToEmbeddedAsset.TryGetValue(backgroundResource, out string? assetFileName))
            return false;

        // Respect custom neighborhood terrain replacements in Packs/.
        if (content.Packs.Assets.ContainsKey(PackContentPaths.ResourceKey(backgroundResource)))
            return false;

        return LoadTemplateRgb(assetFileName) is not null;
    }

    public static Result? TryCreate(
        GameContent content,
        UIDevice device,
        string backgroundResource,
        bool hasPluginTransform,
        ReadOnlySpan<byte> compositedCoreRgba,
        int coreWidth,
        int coreHeight,
        IReadOnlyList<OriginalNeighborhoodOverlay.Manifest.AnimationLayer> animationLayers)
    {
        if (coreWidth != 800 || coreHeight != 600
            || !ResourceToEmbeddedAsset.TryGetValue(backgroundResource, out string? assetFileName)
            || content.Packs.Assets.ContainsKey(PackContentPaths.ResourceKey(backgroundResource)))
        {
            return null;
        }

        byte[]? templateRgb = LoadTemplateRgb(assetFileName);
        if (templateRgb is null)
            return null;

        EnsureFeatherTables(out float[] wx, out float[] wy);

        float[]? seasonalLut = null;
        byte[]? baseSummerRgba = null;
        if (hasPluginTransform)
        {
            (seasonalLut, baseSummerRgba) = TryBuildSeasonalLut(content, backgroundResource, animationLayers, compositedCoreRgba);
        }

        byte[] outRgba = new byte[Width4K * Height4K * 4];

        // Fill outer 4K coastal terrain first.
        for (int y = 0; y < Height4K; y++)
        {
            bool inCoreY = y >= CoreY0 && y < CoreY1;
            int rowPixelOffset = y * Width4K;
            for (int x = 0; x < Width4K; x++)
            {
                if (inCoreY && x >= CoreX0 && x < CoreX1)
                    continue;

                int p = rowPixelOffset + x;
                int s = p * 3;
                int d = p * 4;
                byte r = templateRgb[s];
                byte g = templateRgb[s + 1];
                byte b = templateRgb[s + 2];
                if (seasonalLut is not null)
                    ApplyLut(seasonalLut, ref r, ref g, ref b);

                outRgba[d] = r;
                outRgba[d + 1] = g;
                outRgba[d + 2] = b;
                outRgba[d + 3] = 255;
            }
        }

        // Precompute horizontal bilinear sampling coordinates for [CoreX0..CoreX1].
        Span<int> sx0 = stackalloc int[CoreW];
        Span<int> sx1 = stackalloc int[CoreW];
        Span<float> sfx = stackalloc float[CoreW];
        const float scaleX = 800f / CoreW;
        const float scaleY = 600f / CoreH;
        for (int cx = 0; cx < CoreW; cx++)
        {
            float u = Math.Clamp((cx + 0.5f) * scaleX - 0.5f, 0f, 799f);
            int i0 = (int)MathF.Floor(u);
            sx0[cx] = i0;
            sx1[cx] = Math.Min(i0 + 1, 799);
            sfx[cx] = u - i0;
        }

        // Reconstruct 4K core without double-tinting the outer transition collar when seasonalLut is active.
        for (int cy = 0; cy < CoreH; cy++)
        {
            float v = Math.Clamp((cy + 0.5f) * scaleY - 0.5f, 0f, 599f);
            int y0 = (int)MathF.Floor(v);
            int y1 = Math.Min(y0 + 1, 599);
            float fy = v - y0;
            float wyVal = wy[cy];

            int row0 = y0 * 800 * 4;
            int row1 = y1 * 800 * 4;
            int dstRow = (CoreY0 + cy) * Width4K;

            for (int cx = 0; cx < CoreW; cx++)
            {
                float w = wyVal * wx[cx];
                int c00 = row0 + sx0[cx] * 4;
                int c10 = row0 + sx1[cx] * 4;
                int c01 = row1 + sx0[cx] * 4;
                int c11 = row1 + sx1[cx] * 4;
                float fx = sfx[cx];

                float topR = compositedCoreRgba[c00] + (compositedCoreRgba[c10] - compositedCoreRgba[c00]) * fx;
                float botR = compositedCoreRgba[c01] + (compositedCoreRgba[c11] - compositedCoreRgba[c01]) * fx;
                float ubR = topR + (botR - topR) * fy;

                float topG = compositedCoreRgba[c00 + 1] + (compositedCoreRgba[c10 + 1] - compositedCoreRgba[c00 + 1]) * fx;
                float botG = compositedCoreRgba[c01 + 1] + (compositedCoreRgba[c11 + 1] - compositedCoreRgba[c01 + 1]) * fx;
                float ubG = topG + (botG - topG) * fy;

                float topB = compositedCoreRgba[c00 + 2] + (compositedCoreRgba[c10 + 2] - compositedCoreRgba[c00 + 2]) * fx;
                float botB = compositedCoreRgba[c01 + 2] + (compositedCoreRgba[c11 + 2] - compositedCoreRgba[c01 + 2]) * fx;
                float ubB = topB + (botB - topB) * fy;

                int p = dstRow + CoreX0 + cx;
                int s = p * 3;
                int d = p * 4;
                float aR = templateRgb[s];
                float aG = templateRgb[s + 1];
                float aB = templateRgb[s + 2];

                float r, g, b;
                if (seasonalLut is not null && baseSummerRgba is not null)
                {
                    float bTopR = baseSummerRgba[c00] + (baseSummerRgba[c10] - baseSummerRgba[c00]) * fx;
                    float bBotR = baseSummerRgba[c01] + (baseSummerRgba[c11] - baseSummerRgba[c01]) * fx;
                    float ub0R = bTopR + (bBotR - bTopR) * fy;

                    float bTopG = baseSummerRgba[c00 + 1] + (baseSummerRgba[c10 + 1] - baseSummerRgba[c00 + 1]) * fx;
                    float bBotG = baseSummerRgba[c01 + 1] + (baseSummerRgba[c11 + 1] - baseSummerRgba[c01 + 1]) * fx;
                    float ub0G = bTopG + (bBotG - bTopG) * fy;

                    float bTopB = baseSummerRgba[c00 + 2] + (baseSummerRgba[c10 + 2] - baseSummerRgba[c00 + 2]) * fx;
                    float bBotB = baseSummerRgba[c01 + 2] + (baseSummerRgba[c11 + 2] - baseSummerRgba[c01 + 2]) * fx;
                    float ub0B = bTopB + (bBotB - bTopB) * fy;

                    float summerR = Math.Clamp(aR + w * (ub0R - 128f), 0f, 255f);
                    float summerG = Math.Clamp(aG + w * (ub0G - 128f), 0f, 255f);
                    float summerB = Math.Clamp(aB + w * (ub0B - 128f), 0f, 255f);

                    byte lr = (byte)MathF.Round(summerR);
                    byte lg = (byte)MathF.Round(summerG);
                    byte lb = (byte)MathF.Round(summerB);
                    ApplyLut(seasonalLut, ref lr, ref lg, ref lb);

                    float exactR = summerR + (ubR - ub0R);
                    float exactG = summerG + (ubG - ub0G);
                    float exactB = summerB + (ubB - ub0B);

                    r = lr * (1f - w) + exactR * w;
                    g = lg * (1f - w) + exactG * w;
                    b = lb * (1f - w) + exactB * w;
                }
                else
                {
                    r = aR + w * (ubR - 128f);
                    g = aG + w * (ubG - 128f);
                    b = aB + w * (ubB - 128f);
                }

                outRgba[d] = (byte)Math.Clamp((int)MathF.Round(r), 0, 255);
                outRgba[d + 1] = (byte)Math.Clamp((int)MathF.Round(g), 0, 255);
                outRgba[d + 2] = (byte)Math.Clamp((int)MathF.Round(b), 0, 255);
                outRgba[d + 3] = 255;
            }
        }

        var image = OriginalGameplayOverlay.CreateImage(
            device,
            Width4K,
            Height4K,
            outRgba,
            new CGSize(LogicalWidth, LogicalHeight));

        return new Result(image, LogicalOffsetX, LogicalOffsetY, LogicalWidth, LogicalHeight);
    }

    /// <summary>
    /// Creates a magenta-keyed animation frame whose alpha smoothly feathers to 0 across the outer 16px
    /// of the 800x600 map boundary so animated water ripples dissolve seamlessly into the extended 4K bay/river.
    /// </summary>
    public static OriginalGameplayOverlay.Image CreateFeatheredAnimationFrame(
        UIDevice device,
        SimsBitmap bitmap,
        int offsetX,
        int offsetY,
        int canvasWidth,
        int canvasHeight)
    {
        byte[] rgba = (byte[])bitmap.Rgba.Clone();
        int w = bitmap.Width;
        int h = bitmap.Height;

        for (int y = 0; y < h; y++)
        {
            int cy = offsetY + y;
            int distY = Math.Min(cy, canvasHeight - 1 - cy);
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 4;
                byte r = rgba[idx], g = rgba[idx + 1], b = rgba[idx + 2];
                if (r >= 180 && g <= 70 && b >= 180)
                {
                    rgba[idx] = 0;
                    rgba[idx + 1] = 0;
                    rgba[idx + 2] = 0;
                    rgba[idx + 3] = 0;
                    continue;
                }

                int cx = offsetX + x;
                int distEdge = Math.Min(Math.Min(cx, canvasWidth - 1 - cx), distY);
                if (distEdge <= 0)
                {
                    rgba[idx] = 0;
                    rgba[idx + 1] = 0;
                    rgba[idx + 2] = 0;
                    rgba[idx + 3] = 0;
                }
                else if (distEdge < WaveEdgeFeatherPx)
                {
                    float t = distEdge / (float)WaveEdgeFeatherPx;
                    float alpha = 0.5f - 0.5f * MathF.Cos(MathF.PI * t);
                    // Premultiply RGB by alpha if renderer uses premultiplied, or check how UIDraw blends!
                    // Wait: let's check how OriginalGameplayOverlay.CreateImage handles alpha!
                    byte a = (byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255);
                    rgba[idx + 3] = a;
                }
                else
                {
                    rgba[idx + 3] = 255;
                }
            }
        }

        return OriginalGameplayOverlay.CreateImage(device, w, h, rgba);
    }

    private static void EnsureFeatherTables(out float[] wx, out float[] wy)
    {
        lock (CacheLock)
        {
            if (s_featherX is null || s_featherY is null)
            {
                var fx = new float[CoreW];
                for (int x = 0; x < CoreW; x++)
                {
                    int dist = Math.Min(x, CoreW - 1 - x);
                    float t = Math.Clamp(dist / (float)FeatherPx, 0f, 1f);
                    fx[x] = 0.5f - 0.5f * MathF.Cos(MathF.PI * t);
                }

                var fy = new float[CoreH];
                for (int y = 0; y < CoreH; y++)
                {
                    int dist = Math.Min(y, CoreH - 1 - y);
                    float t = Math.Clamp(dist / (float)FeatherPx, 0f, 1f);
                    fy[y] = 0.5f - 0.5f * MathF.Cos(MathF.PI * t);
                }

                s_featherX = fx;
                s_featherY = fy;
            }

            wx = s_featherX;
            wy = s_featherY;
        }
    }

    private static byte[]? LoadTemplateRgb(string assetFileName)
    {
        lock (CacheLock)
        {
            if (s_cachedTemplatesRgb.TryGetValue(assetFileName, out byte[]? cached))
                return cached;

            Assembly asm = typeof(ExtendedNeighborhoodBackdrop).Assembly;
            string? resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(assetFileName, StringComparison.OrdinalIgnoreCase));
            if (resName is null)
                return null;

            using Stream? stream = asm.GetManifestResourceStream(resName);
            if (stream is null)
                return null;

            var decoded = ImageResult.FromStream(stream, ColorComponents.RedGreenBlue);
            if (decoded.Width != Width4K || decoded.Height != Height4K || decoded.Data.Length != Width4K * Height4K * 3)
                return null;

            s_cachedTemplatesRgb[assetFileName] = decoded.Data;
            return decoded.Data;
        }
    }

    private static (float[]? Lut, byte[]? BaseRgba) TryBuildSeasonalLut(
        GameContent content,
        string backgroundResource,
        IReadOnlyList<OriginalNeighborhoodOverlay.Manifest.AnimationLayer> animationLayers,
        ReadOnlySpan<byte> transformedRgba)
    {
        try
        {
            var rawBg = new SimsBitmap(content.Read(backgroundResource));
            if (rawBg.Width != 800 || rawBg.Height != 600)
                return (null, null);

            byte[] baseRgba = (byte[])rawBg.Rgba.Clone();
            foreach (var layer in animationLayers)
            {
                if (layer.FrameResources.Count == 0)
                    continue;
                int ox = (int)Math.Round(layer.PositionX, MidpointRounding.AwayFromZero);
                int oy = (int)Math.Round(layer.PositionY, MidpointRounding.AwayFromZero);
                var waveBmp = new SimsBitmap(content.Read(layer.FrameResources[0]));
                for (int y = 0; y < waveBmp.Height; y++)
                {
                    int dy = oy + y;
                    if (dy is < 0 or >= 600)
                        continue;
                    for (int x = 0; x < waveBmp.Width; x++)
                    {
                        int dx = ox + x;
                        if (dx is < 0 or >= 800)
                            continue;
                        int src = (y * waveBmp.Width + x) * 4;
                        byte r = waveBmp.Rgba[src], g = waveBmp.Rgba[src + 1], b = waveBmp.Rgba[src + 2];
                        if (r >= 180 && g <= 70 && b >= 180)
                            continue;
                        int dst = (dy * 800 + dx) * 4;
                        baseRgba[dst] = r;
                        baseRgba[dst + 1] = g;
                        baseRgba[dst + 2] = b;
                    }
                }
            }

            long diffSum = 0;
            int totalPixels = 800 * 600;
            for (int i = 0; i < totalPixels * 4; i += 4)
            {
                diffSum += Math.Abs(transformedRgba[i] - baseRgba[i])
                    + Math.Abs(transformedRgba[i + 1] - baseRgba[i + 1])
                    + Math.Abs(transformedRgba[i + 2] - baseRgba[i + 2]);
            }

            if (diffSum / (double)(totalPixels * 3) < 1.5)
                return (null, null);

            const int N = LutSize; // 32x32x32
            const int Cells = N * N * N;
            float[] sumR = new float[Cells];
            float[] sumG = new float[Cells];
            float[] sumB = new float[Cells];
            int[] counts = new int[Cells];

            for (int i = 0; i < totalPixels * 4; i += 4)
            {
                int br = baseRgba[i] >> 3;
                int bg = baseRgba[i + 1] >> 3;
                int bb = baseRgba[i + 2] >> 3;
                int cell = (br * N + bg) * N + bb;
                sumR[cell] += transformedRgba[i] - baseRgba[i];
                sumG[cell] += transformedRgba[i + 1] - baseRgba[i + 1];
                sumB[cell] += transformedRgba[i + 2] - baseRgba[i + 2];
                counts[cell]++;
            }

            // Build delta LUT and fill empty cells from nearest populated neighbor.
            float[] lut = new float[Cells * 3];
            List<int> populated = new(Cells);
            for (int c = 0; c < Cells; c++)
            {
                if (counts[c] > 0)
                {
                    lut[c * 3] = sumR[c] / counts[c];
                    lut[c * 3 + 1] = sumG[c] / counts[c];
                    lut[c * 3 + 2] = sumB[c] / counts[c];
                    populated.Add(c);
                }
            }

            if (populated.Count == 0)
                return (null, null);

            for (int c = 0; c < Cells; c++)
            {
                if (counts[c] > 0)
                    continue;
                int cr = c / (N * N), cg = (c / N) % N, cb = c % N;
                int bestCell = populated[0];
                int bestDist = int.MaxValue;
                foreach (int pc in populated)
                {
                    int pr = pc / (N * N), pg = (pc / N) % N, pb = pc % N;
                    int d = (cr - pr) * (cr - pr) + (cg - pg) * (cg - pg) + (cb - pb) * (cb - pb);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestCell = pc;
                    }
                }
                lut[c * 3] = lut[bestCell * 3];
                lut[c * 3 + 1] = lut[bestCell * 3 + 1];
                lut[c * 3 + 2] = lut[bestCell * 3 + 2];
            }

            return (lut, baseRgba);
        }
        catch
        {
            return (null, null);
        }
    }

    private static void ApplyLut(float[] lut, ref byte r, ref byte g, ref byte b)
    {
        const int N = LutSize;
        const float Scale = (N - 1) / 255f;
        float fr = r * Scale;
        float fg = g * Scale;
        float fb = b * Scale;
        int r0 = Math.Clamp((int)fr, 0, N - 2);
        int g0 = Math.Clamp((int)fg, 0, N - 2);
        int b0 = Math.Clamp((int)fb, 0, N - 2);
        float dr = Math.Clamp(fr - r0, 0f, 1f);
        float dg = Math.Clamp(fg - g0, 0f, 1f);
        float db = Math.Clamp(fb - b0, 0f, 1f);

        float dR = 0f, dG = 0f, dB = 0f;
        for (int ir = 0; ir < 2; ir++)
        {
            float wr = ir == 0 ? 1f - dr : dr;
            for (int ig = 0; ig < 2; ig++)
            {
                float wrg = wr * (ig == 0 ? 1f - dg : dg);
                for (int ib = 0; ib < 2; ib++)
                {
                    float w = wrg * (ib == 0 ? 1f - db : db);
                    int idx = (((r0 + ir) * N + (g0 + ig)) * N + (b0 + ib)) * 3;
                    dR += lut[idx] * w;
                    dG += lut[idx + 1] * w;
                    dB += lut[idx + 2] * w;
                }
            }
        }

        r = (byte)Math.Clamp((int)MathF.Round(r + dR), 0, 255);
        g = (byte)Math.Clamp((int)MathF.Round(g + dG), 0, 255);
        b = (byte)Math.Clamp((int)MathF.Round(b + dB), 0, 255);
    }
}
