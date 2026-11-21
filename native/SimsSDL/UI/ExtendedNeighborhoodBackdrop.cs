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
        double Height,
        byte[] SummerBackdrop1408Rgb,
        byte[] FinalBackdrop2816Rgb,
        byte[] SummerBackdrop2816Rgb);

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
        byte[]? summerRgba4K = seasonalLut is not null ? new byte[Width4K * Height4K * 4] : null;

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
                if (summerRgba4K is not null)
                {
                    summerRgba4K[d] = r;
                    summerRgba4K[d + 1] = g;
                    summerRgba4K[d + 2] = b;
                    summerRgba4K[d + 3] = 255;
                }
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

                    if (summerRgba4K is not null)
                    {
                        summerRgba4K[d] = (byte)MathF.Round(summerR);
                        summerRgba4K[d + 1] = (byte)MathF.Round(summerG);
                        summerRgba4K[d + 2] = (byte)MathF.Round(summerB);
                        summerRgba4K[d + 3] = 255;
                    }

                    byte lr = (byte)MathF.Round(summerR);
                    byte lg = (byte)MathF.Round(summerG);
                    byte lb = (byte)MathF.Round(summerB);
                    ApplyLut(seasonalLut, ref lr, ref lg, ref lb);

                    float exactR = summerR + (ubR - ub0R);
                    float exactG = summerG + (ubG - ub0G);
                    float exactB = summerB + (ubB - ub0B);

                    r = lr + w * (exactR - lr);
                    g = lg + w * (exactG - lg);
                    b = lb + w * (exactB - lb);
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

        if (seasonalLut is not null)
        {
            HarmonizeSeasonalOuterPerimeter(outRgba);
        }

        byte[] summerSource4K = summerRgba4K ?? outRgba;
        byte[] summer1408 = DownsampleRgbaToRgb(summerSource4K, 1408, 792);
        byte[] final2816 = DownsampleRgbaToRgb(outRgba, 2816, 1584);
        byte[] summer2816 = summerRgba4K is not null ? DownsampleRgbaToRgb(summerRgba4K, 2816, 1584) : final2816;

        var image = OriginalGameplayOverlay.CreateImage(
            device,
            Width4K,
            Height4K,
            outRgba,
            new CGSize(LogicalWidth, LogicalHeight));

        return new Result(image, LogicalOffsetX, LogicalOffsetY, LogicalWidth, LogicalHeight, summer1408, final2816, summer2816);
    }

    private static void HarmonizeSeasonalOuterPerimeter(byte[] outRgba)
    {
        float[] leftRes = new float[CoreH * 3];
        float[] rightRes = new float[CoreH * 3];
        for (int cy = 0; cy < CoreH; cy++)
        {
            int y = CoreY0 + cy;
            float inLR = 0f, inLG = 0f, inLB = 0f, outLR = 0f, outLG = 0f, outLB = 0f;
            float inRR = 0f, inRG = 0f, inRB = 0f, outRR = 0f, outRG = 0f, outRB = 0f;
            const int Band = 8;
            for (int k = 0; k < Band; k++)
            {
                int il = (y * Width4K + (CoreX0 + k)) * 4;
                int ol = (y * Width4K + (CoreX0 - 1 - k)) * 4;
                inLR += outRgba[il]; inLG += outRgba[il + 1]; inLB += outRgba[il + 2];
                outLR += outRgba[ol]; outLG += outRgba[ol + 1]; outLB += outRgba[ol + 2];

                int ir = (y * Width4K + (CoreX1 - 1 - k)) * 4;
                int or_ = (y * Width4K + (CoreX1 + k)) * 4;
                inRR += outRgba[ir]; inRG += outRgba[ir + 1]; inRB += outRgba[ir + 2];
                outRR += outRgba[or_]; outRG += outRgba[or_ + 1]; outRB += outRgba[or_ + 2];
            }
            float dlR = (inLR - outLR) / Band, dlG = (inLG - outLG) / Band, dlB = (inLB - outLB) / Band;
            if (MathF.Max(MathF.Abs(dlR), MathF.Max(MathF.Abs(dlG), MathF.Abs(dlB))) < 24f)
            {
                leftRes[cy * 3] = dlR; leftRes[cy * 3 + 1] = dlG; leftRes[cy * 3 + 2] = dlB;
            }
            float drR = (inRR - outRR) / Band, drG = (inRG - outRG) / Band, drB = (inRB - outRB) / Band;
            if (MathF.Max(MathF.Abs(drR), MathF.Max(MathF.Abs(drG), MathF.Abs(drB))) < 24f)
            {
                rightRes[cy * 3] = drR; rightRes[cy * 3 + 1] = drG; rightRes[cy * 3 + 2] = drB;
            }
        }

        float[] topRes = new float[CoreW * 3];
        float[] botRes = new float[CoreW * 3];
        for (int cx = 0; cx < CoreW; cx++)
        {
            int x = CoreX0 + cx;
            float inTR = 0f, inTG = 0f, inTB = 0f, outTR = 0f, outTG = 0f, outTB = 0f;
            float inBR = 0f, inBG = 0f, inBB = 0f, outBR = 0f, outBG = 0f, outBB = 0f;
            const int Band = 8;
            for (int k = 0; k < Band; k++)
            {
                int it = ((CoreY0 + k) * Width4K + x) * 4;
                int ot = ((CoreY0 - 1 - k) * Width4K + x) * 4;
                inTR += outRgba[it]; inTG += outRgba[it + 1]; inTB += outRgba[it + 2];
                outTR += outRgba[ot]; outTG += outRgba[ot + 1]; outTB += outRgba[ot + 2];

                int ib = ((CoreY1 - 1 - k) * Width4K + x) * 4;
                int ob = ((CoreY1 + k) * Width4K + x) * 4;
                inBR += outRgba[ib]; inBG += outRgba[ib + 1]; inBB += outRgba[ib + 2];
                outBR += outRgba[ob]; outBG += outRgba[ob + 1]; outBB += outRgba[ob + 2];
            }
            float dtR = (inTR - outTR) / Band, dtG = (inTG - outTG) / Band, dtB = (inTB - outTB) / Band;
            if (MathF.Max(MathF.Abs(dtR), MathF.Max(MathF.Abs(dtG), MathF.Abs(dtB))) < 24f)
            {
                topRes[cx * 3] = dtR; topRes[cx * 3 + 1] = dtG; topRes[cx * 3 + 2] = dtB;
            }
            float dbR = (inBR - outBR) / Band, dbG = (inBG - outBG) / Band, dbB = (inBB - outBB) / Band;
            if (MathF.Max(MathF.Abs(dbR), MathF.Max(MathF.Abs(dbG), MathF.Abs(dbB))) < 24f)
            {
                botRes[cx * 3] = dbR; botRes[cx * 3 + 1] = dbG; botRes[cx * 3 + 2] = dbB;
            }
        }

        Smooth1D3(leftRes, CoreH, radius: 36);
        Smooth1D3(rightRes, CoreH, radius: 36);
        Smooth1D3(topRes, CoreW, radius: 36);
        Smooth1D3(botRes, CoreW, radius: 36);

        for (int y = 0; y < Height4K; y++)
        {
            bool inCoreY = y >= CoreY0 && y < CoreY1;
            int cyClamp = Math.Clamp(y - CoreY0, 0, CoreH - 1);
            int dyOut = y < CoreY0 ? CoreY0 - y : (y >= CoreY1 ? y - CoreY1 + 1 : 0);

            for (int x = 0; x < Width4K; x++)
            {
                if (inCoreY && x >= CoreX0 && x < CoreX1)
                    continue;

                int cxClamp = Math.Clamp(x - CoreX0, 0, CoreW - 1);
                int dxOut = x < CoreX0 ? CoreX0 - x : (x >= CoreX1 ? x - CoreX1 + 1 : 0);
                float dist = MathF.Sqrt(dxOut * dxOut + dyOut * dyOut);
                float decay = MathF.Exp(-dist / 240f);
                if (decay < 0.01f)
                    continue;

                float sR = 0f, sG = 0f, sB = 0f, wSum = 0f;
                if (x < CoreX0)
                {
                    float wx = 1f / (dxOut + 8f);
                    sR += leftRes[cyClamp * 3] * wx;
                    sG += leftRes[cyClamp * 3 + 1] * wx;
                    sB += leftRes[cyClamp * 3 + 2] * wx;
                    wSum += wx;
                }
                else if (x >= CoreX1)
                {
                    float wx = 1f / (dxOut + 8f);
                    sR += rightRes[cyClamp * 3] * wx;
                    sG += rightRes[cyClamp * 3 + 1] * wx;
                    sB += rightRes[cyClamp * 3 + 2] * wx;
                    wSum += wx;
                }

                if (y < CoreY0)
                {
                    float wy = 1f / (dyOut + 8f);
                    sR += topRes[cxClamp * 3] * wy;
                    sG += topRes[cxClamp * 3 + 1] * wy;
                    sB += topRes[cxClamp * 3 + 2] * wy;
                    wSum += wy;
                }
                else if (y >= CoreY1)
                {
                    float wy = 1f / (dyOut + 8f);
                    sR += botRes[cxClamp * 3] * wy;
                    sG += botRes[cxClamp * 3 + 1] * wy;
                    sB += botRes[cxClamp * 3 + 2] * wy;
                    wSum += wy;
                }

                if (wSum <= 0f)
                    continue;

                float scale = decay / wSum;
                int d = (y * Width4K + x) * 4;
                outRgba[d] = (byte)Math.Clamp((int)MathF.Round(outRgba[d] + sR * scale), 0, 255);
                outRgba[d + 1] = (byte)Math.Clamp((int)MathF.Round(outRgba[d + 1] + sG * scale), 0, 255);
                outRgba[d + 2] = (byte)Math.Clamp((int)MathF.Round(outRgba[d + 2] + sB * scale), 0, 255);
            }
        }
    }

    private static void Smooth1D3(float[] data, int len, int radius)
    {
        float[] copy = (float[])data.Clone();
        for (int i = 0; i < len; i++)
        {
            int a = Math.Max(0, i - radius);
            int b = Math.Min(len - 1, i + radius);
            float sr = 0f, sg = 0f, sb = 0f;
            int cnt = b - a + 1;
            for (int j = a; j <= b; j++)
            {
                sr += copy[j * 3];
                sg += copy[j * 3 + 1];
                sb += copy[j * 3 + 2];
            }
            data[i * 3] = sr / cnt;
            data[i * 3 + 1] = sg / cnt;
            data[i * 3 + 2] = sb / cnt;
        }
    }

    private static byte[] DownsampleRgbaToRgb(byte[] srcRgba, int dstW, int dstH)
    {
        byte[] dst = new byte[dstW * dstH * 3];
        float scaleX = Width4K / (float)dstW;
        float scaleY = Height4K / (float)dstH;

        Span<int> sx0 = stackalloc int[dstW];
        Span<int> sx1 = stackalloc int[dstW];
        Span<float> sfx = stackalloc float[dstW];
        for (int x = 0; x < dstW; x++)
        {
            float u = Math.Clamp((x + 0.5f) * scaleX - 0.5f, 0f, Width4K - 1f);
            int i0 = (int)MathF.Floor(u);
            sx0[x] = i0;
            sx1[x] = Math.Min(i0 + 1, Width4K - 1);
            sfx[x] = u - i0;
        }

        for (int y = 0; y < dstH; y++)
        {
            float v = Math.Clamp((y + 0.5f) * scaleY - 0.5f, 0f, Height4K - 1f);
            int y0 = (int)MathF.Floor(v);
            int y1 = Math.Min(y0 + 1, Height4K - 1);
            float fy = v - y0;
            int row0 = y0 * Width4K * 4;
            int row1 = y1 * Width4K * 4;
            int dstRow = y * dstW * 3;

            for (int x = 0; x < dstW; x++)
            {
                int c00 = row0 + sx0[x] * 4;
                int c10 = row0 + sx1[x] * 4;
                int c01 = row1 + sx0[x] * 4;
                int c11 = row1 + sx1[x] * 4;
                float fx = sfx[x];

                float r = (srcRgba[c00] + (srcRgba[c10] - srcRgba[c00]) * fx) * (1f - fy)
                    + (srcRgba[c01] + (srcRgba[c11] - srcRgba[c01]) * fx) * fy;
                float g = (srcRgba[c00 + 1] + (srcRgba[c10 + 1] - srcRgba[c00 + 1]) * fx) * (1f - fy)
                    + (srcRgba[c01 + 1] + (srcRgba[c11 + 1] - srcRgba[c01 + 1]) * fx) * fy;
                float b = (srcRgba[c00 + 2] + (srcRgba[c10 + 2] - srcRgba[c00 + 2]) * fx) * (1f - fy)
                    + (srcRgba[c01 + 2] + (srcRgba[c11 + 2] - srcRgba[c01 + 2]) * fx) * fy;

                int d = dstRow + x * 3;
                dst[d] = (byte)Math.Clamp((int)MathF.Round(r), 0, 255);
                dst[d + 1] = (byte)Math.Clamp((int)MathF.Round(g), 0, 255);
                dst[d + 2] = (byte)Math.Clamp((int)MathF.Round(b), 0, 255);
            }
        }

        return dst;
    }

    /// <summary>
    /// Builds 2x HiDPI (2816x1584 logical 1408x792 at (-304,-96)) water animation frames that apply exact 1.0x
    /// Maxis RGB wave deltas with alpha=255 (avoiding sRGB premultiplication gamma blow-up) inside the classic
    /// wave rect and gently tile calm-water ripples onto flood-fill-connected extended rivers, bays, and oceans.
    /// </summary>
    public static OriginalGameplayOverlay.Image[]? TryCreateExtendedWaterFrames(
        UIDevice device,
        Result backdrop,
        SimsBitmap rawBaseBitmap,
        IReadOnlyList<SimsBitmap> waveBitmaps,
        int offsetX,
        int offsetY)
    {
        if (waveBitmaps.Count <= 1 || rawBaseBitmap.Width != 800 || rawBaseBitmap.Height != 600)
            return null;

        const int ExtW = 1408;
        const int ExtH = 792;
        const int CoreOx = 304;
        const int CoreOy = 96;
        const int OutW = ExtW * 2; // 2816
        const int OutH = ExtH * 2; // 1584

        // 1. Build 800x600 reference = rawBaseBitmap + waveBitmaps[0] (exact Frame-0 baked into the 4K backdrop).
        byte[] ref800 = (byte[])rawBaseBitmap.Rgba.Clone();
        var f0Bmp = waveBitmaps[0];
        int waveW = f0Bmp.Width;
        int waveH = f0Bmp.Height;
        for (int y = 0; y < waveH; y++)
        {
            int dy = offsetY + y;
            if (dy is < 0 or >= 600) continue;
            for (int x = 0; x < waveW; x++)
            {
                int dx = offsetX + x;
                if (dx is < 0 or >= 800) continue;
                int src = (y * waveW + x) * 4;
                byte r = f0Bmp.Rgba[src], g = f0Bmp.Rgba[src + 1], b = f0Bmp.Rgba[src + 2];
                if (r >= 180 && g <= 70 && b >= 180) continue;
                int dst = (dy * 800 + dx) * 4;
                ref800[dst] = r; ref800[dst + 1] = g; ref800[dst + 2] = b;
            }
        }

        int frameCount = waveBitmaps.Count;
        short[][] coreDeltasRgb = new short[frameCount][];
        coreDeltasRgb[0] = [];
        int[] maxAbsDelta = new int[800 * 600];

        for (int f = 1; f < frameCount; f++)
        {
            short[] delta = new short[800 * 600 * 3];
            var bmp = waveBitmaps[f];
            for (int y = 0; y < bmp.Height; y++)
            {
                int dy = offsetY + y;
                if (dy is < 0 or >= 600) continue;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int dx = offsetX + x;
                    if (dx is < 0 or >= 800) continue;
                    int src = (y * bmp.Width + x) * 4;
                    byte r = bmp.Rgba[src], g = bmp.Rgba[src + 1], b = bmp.Rgba[src + 2];
                    if (r >= 180 && g <= 70 && b >= 180) continue;
                    int p = dy * 800 + dx;
                    int dr = r - ref800[p * 4];
                    int dg = g - ref800[p * 4 + 1];
                    int db = b - ref800[p * 4 + 2];
                    delta[p * 3] = (short)dr;
                    delta[p * 3 + 1] = (short)dg;
                    delta[p * 3 + 2] = (short)db;
                    int m = Math.Max(Math.Abs(dr), Math.Max(Math.Abs(dg), Math.Abs(db)));
                    if (m > maxAbsDelta[p])
                        maxAbsDelta[p] = m;
                }
            }
            coreDeltasRgb[f] = delta;
        }

        // 2. Build per-map 32x32x32 water color LUT from calm animated core water pixels, then flood-fill from
        //    active core water so isolated inland features (e.g. Magic Town circus tent, Vacation snow) are excluded.
        byte[] summer1408 = backdrop.SummerBackdrop1408Rgb;
        bool[] waterColorLut = new bool[32 * 32 * 32];
        bool[] activeCore = new bool[800 * 600];
        bool[] calmCore = new bool[800 * 600];
        int activeCoreCount = 0;

        for (int cy = 0; cy < 600; cy++)
        {
            for (int cx = 0; cx < 800; cx++)
            {
                int p = cy * 800 + cx;
                int m = maxAbsDelta[p];
                if (m < 2)
                    continue;

                activeCore[p] = true;
                activeCoreCount++;

                int s = ((CoreOy + cy) * ExtW + (CoreOx + cx)) * 3;
                int r0 = summer1408[s], g0 = summer1408[s + 1], b0 = summer1408[s + 2];
                if (m <= 16 && r0 <= 110)
                {
                    calmCore[p] = true;
                    if (b0 >= r0 + 8)
                    {
                        int cr = r0 >> 3, cg = g0 >> 3, cb = b0 >> 3;
                        for (int ir = Math.Max(0, cr - 2); ir <= Math.Min(31, cr + 2); ir++)
                        {
                            for (int ig = Math.Max(0, cg - 2); ig <= Math.Min(31, cg + 2); ig++)
                            {
                                int baseIdx = (ir * 32 + ig) * 32;
                                for (int ib = Math.Max(0, cb - 2); ib <= Math.Min(31, cb + 2); ib++)
                                    waterColorLut[baseIdx + ib] = true;
                            }
                        }
                    }
                }
            }
        }

        if (activeCoreCount == 0)
            return null;

        bool[] candidateWater = new bool[ExtW * ExtH];
        int coreCandidateCount = 0;
        for (int ly = 0; ly < ExtH; ly++)
        {
            bool inCoreY = ly >= CoreOy && ly < CoreOy + 600;
            for (int lx = 0; lx < ExtW; lx++)
            {
                int i = ly * ExtW + lx;
                int s = i * 3;
                int r = summer1408[s], g = summer1408[s + 1], b = summer1408[s + 2];
                if (r <= 110 && b >= r + 10 && waterColorLut[((r >> 3) * 32 + (g >> 3)) * 32 + (b >> 3)])
                {
                    candidateWater[i] = true;
                    if (inCoreY && lx >= CoreOx && lx < CoreOx + 800)
                        coreCandidateCount++;
                }
            }
        }

        bool[] visitedWater = new bool[ExtW * ExtH];
        int[] queue = new int[ExtW * ExtH];
        int qHead = 0, qTail = 0;
        for (int cy = 0; cy < 600; cy++)
        {
            for (int cx = 0; cx < 800; cx++)
            {
                if (!activeCore[cy * 800 + cx])
                    continue;
                int idx = (CoreOy + cy) * ExtW + (CoreOx + cx);
                visitedWater[idx] = true;
                queue[qTail++] = idx;
            }
        }

        ReadOnlySpan<(int Dy, int Dx)> bfsSteps =
        [
            (-1, 0), (1, 0), (0, -1), (0, 1),
            (-5, 0), (5, 0), (0, -5), (0, 5),
            (-10, -10), (10, -10), (-10, 10), (10, 10)
        ];
        while (qHead < qTail)
        {
            int cur = queue[qHead++];
            int y = cur / ExtW;
            int x = cur - y * ExtW;
            foreach (var (dy, dx) in bfsSteps)
            {
                int ny = y + dy, nx = x + dx;
                if ((uint)ny >= ExtH || (uint)nx >= ExtW)
                    continue;
                int ni = ny * ExtW + nx;
                if (!visitedWater[ni] && candidateWater[ni])
                {
                    visitedWater[ni] = true;
                    queue[qTail++] = ni;
                }
            }
        }

        // On shoreline-surf-only maps (Vacation Island: covRatio ~ 0.34, open ocean calm inside 800x600),
        // restrict extended animation to nearshore surf within 12px of land so open ocean stays uniformly calm.
        float covRatio = activeCoreCount / (float)Math.Max(1, coreCandidateCount);
        bool surfOnly = covRatio < 0.55f;

        float[] waterMask = new float[ExtW * ExtH];
        for (int ly = 1; ly < ExtH - 1; ly++)
        {
            int row = ly * ExtW;
            for (int lx = 1; lx < ExtW - 1; lx++)
            {
                int i = row + lx;
                if (!visitedWater[i]
                    || !visitedWater[i - 1] || !visitedWater[i + 1]
                    || !visitedWater[i - ExtW] || !visitedWater[i + ExtW])
                {
                    continue;
                }

                if (surfOnly)
                {
                    bool nearShore = false;
                    const int SurfBand = 12;
                    for (int sy = Math.Max(0, ly - SurfBand); sy <= Math.Min(ExtH - 1, ly + SurfBand) && !nearShore; sy += 3)
                    {
                        int sRow = sy * ExtW;
                        for (int sx = Math.Max(0, lx - SurfBand); sx <= Math.Min(ExtW - 1, lx + SurfBand); sx += 3)
                        {
                            if (!visitedWater[sRow + sx])
                            {
                                nearShore = true;
                                break;
                            }
                        }
                    }
                    if (!nearShore)
                        continue;
                }

                waterMask[i] = 1f;
            }
        }

        // Smooth shoreline mask by 2px so waves taper gently before touching sand/rocks/piers.
        float[] tmpMask = new float[ExtW * ExtH];
        const int BlurR = 2;
        for (int ly = 0; ly < ExtH; ly++)
        {
            int row = ly * ExtW;
            for (int lx = 0; lx < ExtW; lx++)
            {
                int a = Math.Max(0, lx - BlurR), b = Math.Min(ExtW - 1, lx + BlurR);
                float sum = 0f;
                for (int x = a; x <= b; x++) sum += waterMask[row + x];
                tmpMask[row + lx] = sum / (b - a + 1);
            }
        }
        for (int ly = 0; ly < ExtH; ly++)
        {
            int a = Math.Max(0, ly - BlurR), b = Math.Min(ExtH - 1, ly + BlurR);
            int cnt = b - a + 1;
            for (int lx = 0; lx < ExtW; lx++)
            {
                float sum = 0f;
                for (int y = a; y <= b; y++) sum += tmpMask[y * ExtW + lx];
                waterMask[ly * ExtW + lx] = sum / cnt;
            }
        }

        // 3. Locate richest 48x48 calm-water ripple patch inside 800x600 and build seamless toroidal 48x48 RGB delta tiles.
        const int TileN = 48;
        const int HalfN = TileN / 2;
        int bestPatchX = 136, bestPatchY = 468, bestValid = -1;
        for (int py = 8; py <= 600 - TileN - 8; py += 4)
        {
            for (int px = 8; px <= 800 - TileN - 8; px += 4)
            {
                int valid = 0;
                for (int yy = 0; yy < TileN; yy += 2)
                {
                    int rOff = (py + yy) * 800 + px;
                    for (int xx = 0; xx < TileN; xx += 2)
                    {
                        if (calmCore[rOff + xx])
                            valid++;
                    }
                }
                if (valid > bestValid)
                {
                    bestValid = valid;
                    bestPatchX = px;
                    bestPatchY = py;
                }
            }
        }

        int[] patchSrcIdx = new int[TileN * TileN];
        List<(int Tx, int Ty)> validCoords = new(TileN * TileN);
        for (int ty = 0; ty < TileN; ty++)
        {
            for (int tx = 0; tx < TileN; tx++)
            {
                if (calmCore[(bestPatchY + ty) * 800 + (bestPatchX + tx)])
                    validCoords.Add((tx, ty));
            }
        }

        for (int ty = 0; ty < TileN; ty++)
        {
            for (int tx = 0; tx < TileN; tx++)
            {
                if (calmCore[(bestPatchY + ty) * 800 + (bestPatchX + tx)] || validCoords.Count == 0)
                {
                    patchSrcIdx[ty * TileN + tx] = (bestPatchY + ty) * 800 + (bestPatchX + tx);
                }
                else
                {
                    int bestD2 = int.MaxValue;
                    int nearTx = validCoords[0].Tx, nearTy = validCoords[0].Ty;
                    foreach (var (vx, vy) in validCoords)
                    {
                        int d2 = (vx - tx) * (vx - tx) + (vy - ty) * (vy - ty);
                        if (d2 < bestD2)
                        {
                            bestD2 = d2;
                            nearTx = vx;
                            nearTy = vy;
                        }
                    }
                    patchSrcIdx[ty * TileN + tx] = (bestPatchY + nearTy) * 800 + (bestPatchX + nearTx);
                }
            }
        }

        float[] win = new float[TileN];
        for (int i = 0; i < TileN; i++)
        {
            float s = MathF.Sin(MathF.PI * (i + 0.5f) / TileN);
            win[i] = s * s;
        }

        float[][] tileDeltasRgb = new float[frameCount][];
        tileDeltasRgb[0] = [];
        float extScale = surfOnly ? 0.55f : 0.85f;
        for (int f = 1; f < frameCount; f++)
        {
            float[] td = new float[TileN * TileN * 3];
            short[] cd = coreDeltasRgb[f];
            for (int ty = 0; ty < TileN; ty++)
            {
                float wy0 = win[ty];
                float wy1 = 1f - wy0;
                int ty1 = (ty + HalfN) % TileN;
                for (int tx = 0; tx < TileN; tx++)
                {
                    float wx0 = win[tx];
                    float wx1 = 1f - wx0;
                    int tx1 = (tx + HalfN) % TileN;

                    int p00 = patchSrcIdx[ty * TileN + tx] * 3;
                    int p10 = patchSrcIdx[ty * TileN + tx1] * 3;
                    int p01 = patchSrcIdx[ty1 * TileN + tx] * 3;
                    int p11 = patchSrcIdx[ty1 * TileN + tx1] * 3;

                    int dIdx = (ty * TileN + tx) * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        float v00 = Math.Clamp((int)cd[p00 + c], -12, 12);
                        float v10 = Math.Clamp((int)cd[p10 + c], -12, 12);
                        float v01 = Math.Clamp((int)cd[p01 + c], -12, 12);
                        float v11 = Math.Clamp((int)cd[p11 + c], -12, 12);
                        td[dIdx + c] = extScale * (
                            v00 * (wx0 * wy0)
                            + v10 * (wx1 * wy0)
                            + v01 * (wx0 * wy1)
                            + v11 * (wx1 * wy1));
                    }
                }
            }
            tileDeltasRgb[f] = td;
        }

        // 4. Render 2x HiDPI (2816x1584, LogicalSize 1408x792) frames with alpha=255 on modified wave pixels only.
        var logicalSize = new CGSize(LogicalWidth, LogicalHeight);
        var result = new OriginalGameplayOverlay.Image[frameCount];
        result[0] = OriginalGameplayOverlay.CreateImage(device, 2, 2, new byte[2 * 2 * 4], logicalSize);

        int waveMinLx = CoreOx + Math.Max(0, offsetX);
        int waveMaxLx = CoreOx + Math.Min(800, offsetX + waveW);
        int waveMinLy = CoreOy + Math.Max(0, offsetY);
        int waveMaxLy = CoreOy + Math.Min(600, offsetY + waveH);
        byte[] bg2816 = backdrop.FinalBackdrop2816Rgb;
        byte[] sum2816 = backdrop.SummerBackdrop2816Rgb;
        bool checkWinterFreeze = !ReferenceEquals(bg2816, sum2816);

        Parallel.For(1, frameCount, f =>
        {
            short[] cd = coreDeltasRgb[f];
            float[] td = tileDeltasRgb[f];

            float[] delta1408 = new float[ExtW * ExtH * 3];
            bool[] nonZero1408 = new bool[ExtW * ExtH];

            for (int ly = 0; ly < ExtH; ly++)
            {
                int row = ly * ExtW;
                bool inWaveY = ly >= waveMinLy && ly < waveMaxLy;
                int tRow = (ly % TileN) * TileN;

                for (int lx = 0; lx < ExtW; lx++)
                {
                    int i = row + lx;
                    float wm = waterMask[i];
                    bool inWave = inWaveY && lx >= waveMinLx && lx < waveMaxLx;
                    if (!inWave && wm <= 0.02f)
                        continue;

                    int tIdx = (tRow + (lx % TileN)) * 3;
                    float extR = wm * td[tIdx];
                    float extG = wm * td[tIdx + 1];
                    float extB = wm * td[tIdx + 2];

                    float dR, dG, dB;
                    if (inWave)
                    {
                        int cx = lx - CoreOx;
                        int cy = ly - CoreOy;
                        int cIdx = (cy * 800 + cx) * 3;
                        int edgeDist = Math.Min(
                            Math.Min(lx - waveMinLx, waveMaxLx - 1 - lx),
                            Math.Min(ly - waveMinLy, waveMaxLy - 1 - ly));
                        float wCore = edgeDist >= 10
                            ? 1f
                            : 0.5f - 0.5f * MathF.Cos(MathF.PI * edgeDist / 10f);
                        dR = wCore * cd[cIdx] + (1f - wCore) * extR;
                        dG = wCore * cd[cIdx + 1] + (1f - wCore) * extG;
                        dB = wCore * cd[cIdx + 2] + (1f - wCore) * extB;
                    }
                    else
                    {
                        dR = extR;
                        dG = extG;
                        dB = extB;
                    }

                    if (MathF.Abs(dR) >= 0.6f || MathF.Abs(dG) >= 0.6f || MathF.Abs(dB) >= 0.6f)
                    {
                        int d3 = i * 3;
                        delta1408[d3] = dR;
                        delta1408[d3 + 1] = dG;
                        delta1408[d3 + 2] = dB;
                        nonZero1408[i] = true;
                    }
                }
            }

            byte[] rgba = new byte[OutW * OutH * 4];
            for (int hy = 0; hy < OutH; hy++)
            {
                float lyF = Math.Clamp((hy + 0.5f) * 0.5f - 0.5f, 0f, ExtH - 1f);
                int y0 = (int)MathF.Floor(lyF);
                int y1 = Math.Min(y0 + 1, ExtH - 1);
                float fy = lyF - y0;
                int r0 = y0 * ExtW;
                int r1 = y1 * ExtW;
                int outRow = hy * OutW;

                for (int hx = 0; hx < OutW; hx++)
                {
                    int x0 = Math.Clamp((int)MathF.Floor((hx + 0.5f) * 0.5f - 0.5f), 0, ExtW - 1);
                    int x1 = Math.Min(x0 + 1, ExtW - 1);
                    if (!nonZero1408[r0 + x0] && !nonZero1408[r0 + x1]
                        && !nonZero1408[r1 + x0] && !nonZero1408[r1 + x1])
                    {
                        continue;
                    }

                    float lxF = Math.Clamp((hx + 0.5f) * 0.5f - 0.5f, 0f, ExtW - 1f);
                    float fx = lxF - x0;
                    int p00 = (r0 + x0) * 3, p10 = (r0 + x1) * 3;
                    int p01 = (r1 + x0) * 3, p11 = (r1 + x1) * 3;

                    float dR = (delta1408[p00] + (delta1408[p10] - delta1408[p00]) * fx) * (1f - fy)
                        + (delta1408[p01] + (delta1408[p11] - delta1408[p01]) * fx) * fy;
                    float dG = (delta1408[p00 + 1] + (delta1408[p10 + 1] - delta1408[p00 + 1]) * fx) * (1f - fy)
                        + (delta1408[p01 + 1] + (delta1408[p11 + 1] - delta1408[p01 + 1]) * fx) * fy;
                    float dB = (delta1408[p00 + 2] + (delta1408[p10 + 2] - delta1408[p00 + 2]) * fx) * (1f - fy)
                        + (delta1408[p01 + 2] + (delta1408[p11 + 2] - delta1408[p01 + 2]) * fx) * fy;

                    int pOut = outRow + hx;
                    int sBg = pOut * 3;
                    if (checkWinterFreeze)
                    {
                        int lumaJump = ((bg2816[sBg] - sum2816[sBg]) + (bg2816[sBg + 1] - sum2816[sBg + 1])) >> 1;
                        if (lumaJump > 32)
                        {
                            float damp = Math.Clamp(1f - (lumaJump - 32f) / 40f, 0f, 1f);
                            dR *= damp;
                            dG *= damp;
                            dB *= damp;
                        }
                    }

                    int idR = (int)MathF.Round(dR);
                    int idG = (int)MathF.Round(dG);
                    int idB = (int)MathF.Round(dB);
                    if (idR == 0 && idG == 0 && idB == 0)
                        continue;

                    int dst = pOut * 4;
                    rgba[dst] = (byte)Math.Clamp(bg2816[sBg] + idR, 0, 255);
                    rgba[dst + 1] = (byte)Math.Clamp(bg2816[sBg + 1] + idG, 0, 255);
                    rgba[dst + 2] = (byte)Math.Clamp(bg2816[sBg + 2] + idB, 0, 255);
                    rgba[dst + 3] = 255;
                }
            }

            result[f] = OriginalGameplayOverlay.CreateImage(device, OutW, OutH, rgba, logicalSize);
        });

        return result;
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
