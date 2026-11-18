using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using SimsSDL.Core;

namespace SimsSDL.UI;

/// <summary>
/// Pure runtime vector renderer for Neighborhood banner chrome and embedded Cubic-Bezier SVG stencils.
/// Eliminates static PNG packs by rasterizing vector glyphs and analytical SDF medallions at target DPI.
/// </summary>
public static class VectorBannerRenderer
{
    public enum ButtonKind
    {
        Medallion53,
        SmallArrow26,
        InsetRing39,
    }

    public readonly record struct RasterImage(int PixelWidth, int PixelHeight, byte[] Rgba, CGSize LogicalSize);

    private readonly record struct MedallionTheme(
        (byte R, byte G, byte B) WellBg,
        (byte R, byte G, byte B) WellShadow,
        (byte R, byte G, byte B) RingHi,
        (byte R, byte G, byte B) RingMid,
        (byte R, byte G, byte B) RingLo,
        (byte R, byte G, byte B) RingRim,
        (byte R, byte G, byte B) Groove,
        float GrooveOpacity);

    private readonly record struct GlyphStyle(
        (byte R, byte G, byte B) BaseRgb,
        (byte R, byte G, byte B) HiRgb,
        float LoDrop,
        (byte R, byte G, byte B) ShadowRgb,
        float ShadowOpacity);

    private sealed record ParsedSvg(float ViewWidth, float ViewHeight, IReadOnlyList<(bool IsCut, IReadOnlyList<(float X, float Y)> Contours)> Layers);

    private static readonly ConcurrentDictionary<string, ParsedSvg> SvgCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, RasterImage> RasterCache = new(StringComparer.Ordinal);

    private static readonly MedallionTheme[] MedallionThemes =
    [
        // 0: Normal
        new((0, 16, 150), (6, 10, 60), (236, 239, 248), (212, 218, 232), (171, 179, 200), (34, 39, 82), (18, 22, 58), 0.72f),
        // 1: Hover
        new((0, 22, 181), (2, 40, 84), (120, 255, 255), (1, 255, 255), (2, 164, 181), (5, 59, 84), (5, 56, 80), 0.72f),
        // 2: Pressed
        new((0, 24, 190), (2, 40, 84), (120, 255, 255), (1, 255, 255), (2, 164, 181), (5, 59, 84), (5, 56, 80), 0.76f),
        // 3: Disabled
        new((43, 50, 140), (32, 37, 104), (164, 168, 204), (146, 150, 190), (114, 118, 158), (62, 66, 110), (54, 58, 102), 0.56f),
    ];

    private static readonly GlyphStyle[] GlyphStyles =
    [
        // 0: Normal
        new((224, 229, 240), (255, 255, 255), 38f, (8, 12, 52), 0.74f),
        // 1: Hover
        new((2, 252, 255), (155, 255, 255), 46f, (2, 26, 74), 0.72f),
        // 2: Pressed
        new((252, 254, 255), (255, 255, 255), 24f, (2, 26, 74), 0.74f),
        // 3: Disabled
        new((146, 150, 192), (175, 180, 215), 22f, (32, 38, 96), 0.48f),
    ];

    public static int RecommendedScale(float uiScale = 2f) =>
        Math.Clamp((int)MathF.Ceiling(Math.Max(2f, uiScale) * 2f), 4, 6);

    public static bool HasEmbeddedSvg(string stem) =>
        FindResourceName(stem) is not null;

    public static RasterImage RenderBannerBar(ReadOnlySpan<float> collarCentersX1x, int scale = 4)
    {
        scale = Math.Clamp(scale, 1, 8);
        string collarKey = string.Join(",", collarCentersX1x.ToArray().Select(static c => c.ToString("F2", CultureInfo.InvariantCulture)));
        string cacheKey = $"banner:{scale}:{collarKey}";
        if (RasterCache.TryGetValue(cacheKey, out var cached))
            return cached;

        const int w1x = 800;
        const int h1x = 52;
        int pw = w1x * scale;
        int ph = h1x * scale;
        byte[] rgba = new byte[pw * ph * 4];
        float[] collars = collarCentersX1x.ToArray();
        float invScale = 1f / scale;

        for (int py = 0; py < ph; py++)
        {
            float y1x = (py + 0.5f) * invScale;
            for (int px = 0; px < pw; px++)
            {
                float x1x = (px + 0.5f) * invScale;

                // Capsule bar [0..800, 0..42] with R=21.0 rounded left/right caps at x=21 and x=779.
                float capCx = Math.Clamp(x1x, 21.0f, 779.0f);
                float dCap = MathF.Sqrt((x1x - capCx) * (x1x - capCx) + (y1x - 21.0f) * (y1x - 21.0f)) - 21.0f;
                float dBar = MathF.Max(MathF.Max(-y1x, y1x - 42.0f), dCap);

                // Smoothly union circular lower button collars (r = 26.55, cy = 24.5).
                float dUnion = dBar;
                for (int i = 0; i < collars.Length; i++)
                {
                    float dx = x1x - collars[i];
                    if (MathF.Abs(dx) > 30f)
                        continue;
                    float dy = y1x - 24.5f;
                    float dCol = MathF.Sqrt(dx * dx + dy * dy) - 26.55f;
                    dUnion = SmoothMin(dUnion, dCol, 0.75f);
                }

                float cov = Math.Clamp(0.5f - dUnion * scale, 0f, 1f);
                if (cov <= 0.001f)
                    continue;

                // Base horizontal purple-to-navy gradient matching original Maxis NghUI banner.
                float tGrad = Math.Clamp((x1x - 25f) / 750f, 0f, 1f);
                float r = Lerp(99f, 5f, tGrad);
                float g = Lerp(99f, 5f, tGrad);
                float b = Lerp(146f, 79f, tGrad);

                // Upper bevel specular highlight along the top edge and upper cap curvature.
                float dTop = y1x;
                if (x1x < 21f || x1x > 779f)
                {
                    float ecx = x1x < 21f ? 21f : 779f;
                    dTop = 21.0f - MathF.Sqrt((x1x - ecx) * (x1x - ecx) + (y1x - 21.0f) * (y1x - 21.0f));
                }

                if (y1x < 21f && dTop >= 0f && dTop < 5.0f)
                {
                    float ridge = MathF.Exp(-((dTop - 1.55f) * (dTop - 1.55f)) / (2f * 0.78f * 0.78f));
                    float rimDark = dTop < 0.75f ? Lerp(0.72f, 1f, dTop / 0.75f) : 1f;
                    r = r * rimDark + ridge * 30f;
                    g = g * rimDark + ridge * 30f;
                    b = b * rimDark + ridge * 32f;
                }

                // Lower & right outer-boundary 3D bevel shadow taper.
                float dIn = MathF.Max(0f, -dUnion);
                if (y1x > 18f && dIn < 4.2f)
                {
                    float shade = Lerp(0.48f, 1.0f, SmoothStep(0f, 4.2f, dIn));
                    r *= shade;
                    g *= shade;
                    b *= shade;
                }

                // Subtle lower-left white specular crescent on each protruding collar bulge.
                if (y1x > 36f)
                {
                    for (int i = 0; i < collars.Length; i++)
                    {
                        float dx = x1x - collars[i];
                        if (dx > 2f || dx < -27f)
                            continue;
                        float dy = y1x - 25.25f;
                        float rad = MathF.Sqrt(dx * dx + dy * dy);
                        if (rad < 22.8f || rad > 26.2f)
                            continue;
                        float ang = MathF.Atan2(dy, dx); // lower-left quadrant is ~[2.05 .. 2.55] rad
                        if (ang > 1.96f && ang < 2.62f)
                        {
                            float aFade = MathF.Sin((ang - 1.96f) / (2.62f - 1.96f) * MathF.PI);
                            float rFade = MathF.Exp(-((rad - 24.75f) * (rad - 24.75f)) / (2f * 0.58f * 0.58f));
                            float cresc = aFade * rFade * 0.70f;
                            r = Lerp(r, 245f, cresc);
                            g = Lerp(g, 247f, cresc);
                            b = Lerp(b, 255f, cresc);
                        }
                    }
                }

                // Lit-proportional fine matte surface micro-grain (stronger on lighter purple left, subtler in dark navy).
                float lum = 0.299f * r + 0.587f * g + 0.114f * b;
                float litFactor = MathF.Pow(Math.Clamp((lum - 24f) / 90f, 0f, 1f), 1.40f);
                if (litFactor > 0.001f)
                {
                    float grain = BilinearSurfaceNoise(x1x * 2.2f, y1x * 2.2f) * 5.0f * litFactor;
                    r += grain;
                    g += grain;
                    b += grain;
                }

                int idx = (py * pw + px) * 4;
                rgba[idx] = ClampByte(r);
                rgba[idx + 1] = ClampByte(g);
                rgba[idx + 2] = ClampByte(b);
                rgba[idx + 3] = ClampByte(cov * 255f);
            }
        }

        var result = new RasterImage(pw, ph, rgba, new CGSize(w1x, h1x));
        RasterCache[cacheKey] = result;
        return result;
    }

    public static RasterImage RenderSimsLogo(int scale = 4)
    {
        scale = Math.Clamp(scale, 1, 8);
        string cacheKey = $"logo:{scale}";
        if (RasterCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var svg = LoadSvg("thesimslogo");
        const int w1x = 70;
        const int h1x = 52;
        int pw = w1x * scale;
        int ph = h1x * scale;

        float[] face = RasterizeContoursCombined(svg, pw, ph);

        // Continuous swept 3D block extrusion + soft ambient shadow matching original Maxis TheSimsLogo.
        float[] ext = new float[pw * ph];
        const int sweepSteps = 5;
        for (int step = 1; step <= sweepSteps; step++)
        {
            float t = step / (float)sweepSteps;
            float[] shifted = ShiftField(face, pw, ph, 1.75f * t * scale, 2.05f * t * scale);
            for (int i = 0; i < ext.Length; i++)
                ext[i] = MathF.Max(ext[i], shifted[i]);
        }
        float[] soft = GaussianBlur(ShiftField(face, pw, ph, 2.1f * scale, 2.4f * scale), pw, ph, 0.65f * scale);

        byte[] rgba = new byte[pw * ph * 4];
        for (int i = 0; i < pw * ph; i++)
        {
            float f = face[i];
            float e = Math.Clamp(ext[i] * 1.15f, 0f, 1f);
            float s = Math.Clamp(soft[i] * 0.72f, 0f, 1f);

            float underA = MathF.Max(e * 0.98f, s);
            float outA = f + underA * (1f - f);
            if (outA <= 0.002f)
                continue;

            float uWeight = underA * (1f - f);
            float r = (255f * f + 4f * uWeight) / outA;
            float g = (255f * f + 6f * uWeight) / outA;
            float b = (255f * f + 28f * uWeight) / outA;

            int dst = i * 4;
            rgba[dst] = ClampByte(r);
            rgba[dst + 1] = ClampByte(g);
            rgba[dst + 2] = ClampByte(b);
            rgba[dst + 3] = ClampByte(outA * 255f);
        }

        var result = new RasterImage(pw, ph, rgba, new CGSize(w1x, h1x));
        RasterCache[cacheKey] = result;
        return result;
    }

    public static RasterImage RenderButtonStrip(
        string svgStem,
        ButtonKind kind,
        float ringCx1x,
        int scale = 4)
    {
        scale = Math.Clamp(scale, 1, 8);
        string cacheKey = $"btn:{svgStem}:{kind}:{ringCx1x.ToString("F2", CultureInfo.InvariantCulture)}:{scale}";
        if (RasterCache.TryGetValue(cacheKey, out var cached))
            return cached;

        int fw1x = kind switch
        {
            ButtonKind.SmallArrow26 => 26,
            ButtonKind.InsetRing39 => 39,
            _ => 53,
        };
        const int fh1x = 52;
        float cy1x, rc1x, tr1x;
        switch (kind)
        {
            case ButtonKind.SmallArrow26:
                cy1x = 19.30f;
                rc1x = 8.12f;
                tr1x = 0.76f;
                break;
            case ButtonKind.InsetRing39:
                cy1x = 20.34f;
                rc1x = 14.22f;
                tr1x = 1.15f;
                break;
            default:
                cy1x = 25.08f;
                rc1x = 18.46f;
                tr1x = 1.38f;
                break;
        }

        int frameW = fw1x * scale;
        int frameH = fh1x * scale;
        int stripW = frameW * 4;
        byte[] stripRgba = new byte[stripW * frameH * 4];

        var svg = LoadSvg(svgStem);
        float[] glyphMask = RasterizeContoursCombined(svg, frameW, frameH);
        float wellClipR1x = rc1x - tr1x + 0.15f;
        for (int py = 0; py < frameH; py++)
        {
            float y1x = (py + 0.5f) / scale;
            for (int px = 0; px < frameW; px++)
            {
                float x1x = (px + 0.5f) / scale;
                float dist = MathF.Sqrt((x1x - ringCx1x) * (x1x - ringCx1x) + (y1x - cy1x) * (y1x - cy1x));
                float clip = Math.Clamp((wellClipR1x - dist) * scale, 0f, 1f);
                glyphMask[py * frameW + px] *= clip;
            }
        }

        for (int state = 0; state < 4; state++)
        {
            byte[] frameRgba = RenderMedallionBaseFrame(frameW, frameH, scale, kind, ringCx1x, cy1x, rc1x, tr1x, state);
            byte[] styledGlyph = ApplyMaxisLayerStyle(glyphMask, frameW, frameH, scale, state, pressedShift: false);
            AlphaOverInPlace(frameRgba, styledGlyph, frameW, frameH, ringCx1x, cy1x, rc1x - tr1x + 0.35f, scale);

            // Copy frame into 4-state horizontal strip.
            int xOffsetBytes = state * frameW * 4;
            int rowBytes = frameW * 4;
            int stripRowBytes = stripW * 4;
            for (int py = 0; py < frameH; py++)
            {
                Buffer.BlockCopy(frameRgba, py * rowBytes, stripRgba, py * stripRowBytes + xOffsetBytes, rowBytes);
            }
        }

        var result = new RasterImage(stripW, frameH, stripRgba, new CGSize(fw1x * 4, fh1x));
        RasterCache[cacheKey] = result;
        return result;
    }

    private static byte[] RenderMedallionBaseFrame(
        int pw,
        int ph,
        int scale,
        ButtonKind kind,
        float cx1x,
        float cy1x,
        float rc1x,
        float tr1x,
        int state)
    {
        var th = MedallionThemes[state];
        byte[] rgba = new byte[pw * ph * 4];
        float invScale = 1f / scale;
        float rIn = rc1x - tr1x;
        float rOut = rc1x + tr1x;

        for (int py = 0; py < ph; py++)
        {
            float y1x = (py + 0.5f) * invScale;
            for (int px = 0; px < pw; px++)
            {
                float x1x = (px + 0.5f) * invScale;

                // 1. Outer recessed groove shadow (shifted +0.25, +0.30 in 1x).
                float gdx = x1x - (cx1x + 0.25f);
                float gdy = y1x - (cy1x + 0.30f);
                float gDist = MathF.Sqrt(gdx * gdx + gdy * gdy);
                float grooveCov = Math.Clamp(((rOut + 0.42f) - gDist) * scale, 0f, 1f);
                var grooveCol = (kind == ButtonKind.InsetRing39 && state == 0) ? ((byte)1, (byte)3, (byte)38) : th.Groove;
                float outR = grooveCol.Item1;
                float outG = grooveCol.Item2;
                float outB = grooveCol.Item3;
                float outA = grooveCov * th.GrooveOpacity;

                // 2. Recessed blue well + top-left inner shadow crescent.
                float dx = x1x - cx1x;
                float dy = y1x - cy1x;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                float wellCov = Math.Clamp(((rIn + 0.15f) - dist) * scale, 0f, 1f);
                if (wellCov > 0.001f)
                {
                    float wr = th.WellBg.R;
                    float wg = th.WellBg.G;
                    float wb = th.WellBg.B;

                    // Inner shadow inside upper-left wall of well.
                    float sdx = x1x - (cx1x + 0.75f);
                    float sdy = y1x - (cy1x + 0.85f);
                    float sDist = MathF.Sqrt(sdx * sdx + sdy * sdy);
                    float innerSh = Math.Clamp((sDist - (rIn - 0.65f)) / 1.35f, 0f, 1f) * 0.65f;
                    wr = Lerp(wr, th.WellShadow.R, innerSh);
                    wg = Lerp(wg, th.WellShadow.G, innerSh);
                    wb = Lerp(wb, th.WellShadow.B, innerSh);

                    BlendPixel(ref outR, ref outG, ref outB, ref outA, wr, wg, wb, wellCov);
                }

                // 3. Dark ring rim border + 3D directional metallic/cyan torus ring.
                float rimDist = MathF.Abs(dist - rc1x) - (tr1x + 0.12f);
                float rimCov = Math.Clamp(0.5f - rimDist * scale, 0f, 1f);
                if (rimCov > 0.001f)
                {
                    BlendPixel(ref outR, ref outG, ref outB, ref outA, th.RingRim.R, th.RingRim.G, th.RingRim.B, rimCov);
                }

                float bodyHalfW = MathF.Max(0.28f, tr1x - 0.11f);
                float ringDist = MathF.Abs(dist - rc1x) - bodyHalfW;
                float ringCov = Math.Clamp(0.5f - ringDist * scale, 0f, 1f);
                if (ringCov > 0.001f)
                {
                    // Diagonal gradient along (0.64, 0.76) across the medallion ring.
                    float diag = ((dx / MathF.Max(1f, rOut)) * 0.64f + (dy / MathF.Max(1f, rOut)) * 0.76f) * 0.5f + 0.5f;
                    diag = Math.Clamp(diag, 0f, 1f);
                    float rr, rg, rb;
                    if (diag < 0.55f)
                    {
                        float lt = diag / 0.55f;
                        rr = Lerp(th.RingHi.R, th.RingMid.R, lt);
                        rg = Lerp(th.RingHi.G, th.RingMid.G, lt);
                        rb = Lerp(th.RingHi.B, th.RingMid.B, lt);
                    }
                    else
                    {
                        float lt = (diag - 0.55f) / 0.45f;
                        rr = Lerp(th.RingMid.R, th.RingLo.R, lt);
                        rg = Lerp(th.RingMid.G, th.RingLo.G, lt);
                        rb = Lerp(th.RingMid.B, th.RingLo.B, lt);
                    }

                    // Cross-sectional torus highlight ridge along the middle of the ring tube.
                    float tubeT = 1f - Math.Clamp(MathF.Abs(dist - rc1x) / bodyHalfW, 0f, 1f);
                    float spec = MathF.Pow(tubeT, 1.8f) * 14f;
                    BlendPixel(
                        ref outR,
                        ref outG,
                        ref outB,
                        ref outA,
                        Math.Clamp(rr + spec, 0f, 255f),
                        Math.Clamp(rg + spec, 0f, 255f),
                        Math.Clamp(rb + spec, 0f, 255f),
                        ringCov);
                }

                if (outA <= 0.001f)
                    continue;

                int idx = (py * pw + px) * 4;
                rgba[idx] = ClampByte(outR);
                rgba[idx + 1] = ClampByte(outG);
                rgba[idx + 2] = ClampByte(outB);
                rgba[idx + 3] = ClampByte(outA * 255f);
            }
        }

        return rgba;
    }

    private static byte[] ApplyMaxisLayerStyle(
        float[] maskIn,
        int w,
        int h,
        int scale,
        int state,
        bool pressedShift)
    {
        var cfg = GlyphStyles[state];
        float sRatio = scale / 8f; // Original PS 5.5 constants were tuned in 8x pixel space.
        float[] mask = pressedShift ? ShiftField(maskIn, w, h, 0.22f * scale, 0.22f * scale) : maskIn;

        float[] dropped = ShiftField(mask, w, h, 5.2f * sRatio, 6.2f * sRatio);
        float[] sh = GaussianBlur(dropped, w, h, 2.1f * sRatio);
        float[] dil = GaussianBlur(mask, w, h, 1.25f * sRatio);

        float bevShift = 2.0f * sRatio;
        float[] shiftBr = ShiftField(mask, w, h, bevShift, bevShift);
        float[] shiftTl = ShiftField(mask, w, h, -bevShift, -bevShift);

        float[] diffHi = new float[w * h];
        float[] diffLo = new float[w * h];
        for (int i = 0; i < w * h; i++)
        {
            diffHi[i] = Math.Clamp(mask[i] - shiftBr[i], 0f, 1f);
            diffLo[i] = Math.Clamp(mask[i] - shiftTl[i], 0f, 1f);
        }

        float[] hiTl = GaussianBlur(diffHi, w, h, 0.75f * sRatio);
        float[] loBr = GaussianBlur(diffLo, w, h, 0.75f * sRatio);

        byte[] outRgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            float m = mask[i];
            float shVal = sh[i] * cfg.ShadowOpacity;
            float rimVal = Math.Clamp(dil[i] * 2.3f, 0f, 1f) * (cfg.ShadowOpacity * 0.85f);
            float underA = MathF.Max(shVal, rimVal) * (1f - m * 0.96f);
            float totA = m + underA * (1f - m);
            if (totA <= 0.001f)
                continue;

            float fgR = Math.Clamp(cfg.BaseRgb.R + hiTl[i] * (cfg.HiRgb.R - cfg.BaseRgb.R) * 0.90f - loBr[i] * cfg.LoDrop, 0f, 255f);
            float fgG = Math.Clamp(cfg.BaseRgb.G + hiTl[i] * (cfg.HiRgb.G - cfg.BaseRgb.G) * 0.90f - loBr[i] * cfg.LoDrop, 0f, 255f);
            float fgB = Math.Clamp(cfg.BaseRgb.B + hiTl[i] * (cfg.HiRgb.B - cfg.BaseRgb.B) * 0.90f - loBr[i] * cfg.LoDrop, 0f, 255f);

            float shWeight = underA * (1f - m);
            float outR = (fgR * m + cfg.ShadowRgb.R * shWeight) / totA;
            float outG = (fgG * m + cfg.ShadowRgb.G * shWeight) / totA;
            float outB = (fgB * m + cfg.ShadowRgb.B * shWeight) / totA;

            int dst = i * 4;
            outRgba[dst] = ClampByte(outR);
            outRgba[dst + 1] = ClampByte(outG);
            outRgba[dst + 2] = ClampByte(outB);
            outRgba[dst + 3] = ClampByte(totA * 255f);
        }

        return outRgba;
    }

    private static void AlphaOverInPlace(
        byte[] dstRgba,
        byte[] srcRgba,
        int w,
        int h,
        float clipCx1x,
        float clipCy1x,
        float clipR1x,
        int scale)
    {
        for (int py = 0; py < h; py++)
        {
            float y1x = (py + 0.5f) / scale;
            for (int px = 0; px < w; px++)
            {
                int idx = (py * w + px) * 4;
                float sa = srcRgba[idx + 3] / 255f;
                if (sa <= 0.001f)
                    continue;

                float x1x = (px + 0.5f) / scale;
                float dist = MathF.Sqrt((x1x - clipCx1x) * (x1x - clipCx1x) + (y1x - clipCy1x) * (y1x - clipCy1x));
                float wellMask = Math.Clamp((clipR1x - dist) * scale, 0f, 1f);
                sa *= wellMask;
                if (sa <= 0.001f)
                    continue;

                float dr = dstRgba[idx];
                float dg = dstRgba[idx + 1];
                float db = dstRgba[idx + 2];
                float da = dstRgba[idx + 3] / 255f;

                BlendPixel(ref dr, ref dg, ref db, ref da, srcRgba[idx], srcRgba[idx + 1], srcRgba[idx + 2], sa);
                dstRgba[idx] = ClampByte(dr);
                dstRgba[idx + 1] = ClampByte(dg);
                dstRgba[idx + 2] = ClampByte(db);
                dstRgba[idx + 3] = ClampByte(da * 255f);
            }
        }
    }

    private static void BlendPixel(
        ref float dstR,
        ref float dstG,
        ref float dstB,
        ref float dstA,
        float srcR,
        float srcG,
        float srcB,
        float srcA)
    {
        float outA = srcA + dstA * (1f - srcA);
        if (outA <= 1e-5f)
        {
            dstA = 0f;
            return;
        }

        dstR = (srcR * srcA + dstR * dstA * (1f - srcA)) / outA;
        dstG = (srcG * srcA + dstG * dstA * (1f - srcA)) / outA;
        dstB = (srcB * srcA + dstB * dstA * (1f - srcA)) / outA;
        dstA = outA;
    }

    private static float[] ShiftField(float[] src, int w, int h, float dx, float dy)
    {
        float[] dst = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            float sy = y - dy;
            int y0 = (int)MathF.Floor(sy);
            float fy = sy - y0;
            for (int x = 0; x < w; x++)
            {
                float sx = x - dx;
                int x0 = (int)MathF.Floor(sx);
                float fx = sx - x0;

                float v00 = SampleZeroBorder(src, w, h, x0, y0);
                float v10 = SampleZeroBorder(src, w, h, x0 + 1, y0);
                float v01 = SampleZeroBorder(src, w, h, x0, y0 + 1);
                float v11 = SampleZeroBorder(src, w, h, x0 + 1, y0 + 1);

                float top = v00 + (v10 - v00) * fx;
                float bot = v01 + (v11 - v01) * fx;
                dst[y * w + x] = top + (bot - top) * fy;
            }
        }
        return dst;
    }

    private static float SampleZeroBorder(float[] src, int w, int h, int x, int y) =>
        (uint)x < (uint)w && (uint)y < (uint)h ? src[y * w + x] : 0f;

    private static float[] GaussianBlur(float[] src, int w, int h, float sigma)
    {
        if (sigma <= 0.05f)
            return (float[])src.Clone();

        int radius = Math.Clamp((int)MathF.Ceiling(sigma * 3f), 1, 24);
        float[] kernel = new float[radius * 2 + 1];
        float sum = 0f;
        float inv2Sigma2 = 1f / (2f * sigma * sigma);
        for (int i = -radius; i <= radius; i++)
        {
            float weight = MathF.Exp(-(i * i) * inv2Sigma2);
            kernel[i + radius] = weight;
            sum += weight;
        }
        for (int i = 0; i < kernel.Length; i++)
            kernel[i] /= sum;

        float[] tmp = new float[w * h];
        float[] dst = new float[w * h];

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float acc = 0f;
                for (int k = -radius; k <= radius; k++)
                {
                    int sx = x + k;
                    if ((uint)sx < (uint)w)
                        acc += src[row + sx] * kernel[k + radius];
                }
                tmp[row + x] = acc;
            }
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float acc = 0f;
                for (int k = -radius; k <= radius; k++)
                {
                    int sy = y + k;
                    if ((uint)sy < (uint)h)
                        acc += tmp[sy * w + x] * kernel[k + radius];
                }
                dst[y * w + x] = acc;
            }
        }

        return dst;
    }

    private static ParsedSvg LoadSvg(string stem)
    {
        if (SvgCache.TryGetValue(stem, out var cached))
            return cached;

        string resName = FindResourceName(stem)
            ?? throw new ContentError($"Missing embedded vector SVG resource '{stem}.svg'");
        using Stream stream = typeof(VectorBannerRenderer).Assembly.GetManifestResourceStream(resName)
            ?? throw new ContentError($"Could not open embedded vector SVG resource '{resName}'");

        var doc = XDocument.Load(stream);
        var root = doc.Root ?? throw new ContentError($"Empty SVG document '{stem}.svg'");

        float vw = 530f, vh = 520f;
        if (root.Attribute("viewBox")?.Value is { } vb)
        {
            string[] parts = vb.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4
                && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedW)
                && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedH))
            {
                vw = parsedW;
                vh = parsedH;
            }
        }

        var layers = new List<(bool IsCut, IReadOnlyList<(float X, float Y)> Contours)>();
        foreach (var pathEl in root.Descendants().Where(static e => e.Name.LocalName == "path"))
        {
            string? d = pathEl.Attribute("d")?.Value;
            if (string.IsNullOrWhiteSpace(d))
                continue;
            bool isCut = string.Equals(pathEl.Attribute("data-op")?.Value, "cut", StringComparison.OrdinalIgnoreCase);
            var contours = ParseSvgPathContours(d);
            if (contours.Count > 0)
            {
                // Pack closed contours inside a layer using (NaN, NaN) sentinel separators.
                var packed = new List<(float X, float Y)>();
                foreach (var contour in contours)
                {
                    if (contour.Count < 3)
                        continue;
                    if (packed.Count > 0)
                        packed.Add((float.NaN, float.NaN));
                    packed.AddRange(contour);
                }
                if (packed.Count > 0)
                    layers.Add((isCut, packed));
            }
        }

        var parsed = new ParsedSvg(vw, vh, layers);
        SvgCache[stem] = parsed;
        return parsed;
    }

    private static string? FindResourceName(string stem)
    {
        string suffix = $".{stem}.svg";
        foreach (string name in typeof(VectorBannerRenderer).Assembly.GetManifestResourceNames())
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return null;
    }

    private static float[] RasterizeContoursCombined(ParsedSvg svg, int pw, int ph)
    {
        float[] combined = new float[pw * ph];
        foreach (var (isCut, contours) in svg.Layers)
        {
            float[] cov = RasterizeContourSet(contours, svg.ViewWidth, svg.ViewHeight, pw, ph);
            if (isCut)
            {
                for (int i = 0; i < combined.Length; i++)
                    combined[i] *= 1f - cov[i];
            }
            else
            {
                for (int i = 0; i < combined.Length; i++)
                    combined[i] = combined[i] + cov[i] * (1f - combined[i]);
            }
        }
        return combined;
    }

    private static float[] RasterizeContourSet(
        IReadOnlyList<(float X, float Y)> packedContours,
        float viewW,
        float viewH,
        int pw,
        int ph)
    {
        float sx = pw / MathF.Max(1f, viewW);
        float sy = ph / MathF.Max(1f, viewH);

        // Extract scaled segments (x0, y0, x1, y1).
        var segments = new List<(float X0, float Y0, float X1, float Y1)>(packedContours.Count);
        int start = 0;
        while (start < packedContours.Count)
        {
            int end = start;
            while (end < packedContours.Count && !float.IsNaN(packedContours[end].X))
                end++;

            int count = end - start;
            if (count >= 3)
            {
                for (int i = 0; i < count; i++)
                {
                    var p0 = packedContours[start + i];
                    var p1 = packedContours[start + (i + 1) % count];
                    float y0 = p0.Y * sy;
                    float y1 = p1.Y * sy;
                    if (MathF.Abs(y1 - y0) > 1e-5f)
                        segments.Add((p0.X * sx, y0, p1.X * sx, y1));
                }
            }
            start = end + 1;
        }

        float[] coverage = new float[pw * ph];
        if (segments.Count == 0)
            return coverage;

        const int subRows = 4;
        var xHits = new List<float>(32);

        for (int py = 0; py < ph; py++)
        {
            int rowOffset = py * pw;
            for (int sr = 0; sr < subRows; sr++)
            {
                float scanY = py + (sr + 0.5f) / subRows;
                xHits.Clear();

                for (int s = 0; s < segments.Count; s++)
                {
                    var seg = segments[s];
                    if ((seg.Y0 <= scanY && seg.Y1 > scanY) || (seg.Y1 <= scanY && seg.Y0 > scanY))
                    {
                        float t = (scanY - seg.Y0) / (seg.Y1 - seg.Y0);
                        xHits.Add(seg.X0 + t * (seg.X1 - seg.X0));
                    }
                }

                if (xHits.Count < 2)
                    continue;

                xHits.Sort();
                for (int k = 0; k + 1 < xHits.Count; k += 2)
                {
                    float xLeft = Math.Clamp(xHits[k], 0f, pw);
                    float xRight = Math.Clamp(xHits[k + 1], 0f, pw);
                    if (xRight <= xLeft)
                        continue;

                    int ix0 = Math.Clamp((int)MathF.Floor(xLeft), 0, pw - 1);
                    int ix1 = Math.Clamp((int)MathF.Floor(xRight), 0, pw - 1);
                    if (ix0 == ix1)
                    {
                        coverage[rowOffset + ix0] += (xRight - xLeft) / subRows;
                    }
                    else
                    {
                        coverage[rowOffset + ix0] += (ix0 + 1 - xLeft) / subRows;
                        for (int ix = ix0 + 1; ix < ix1; ix++)
                            coverage[rowOffset + ix] += 1f / subRows;
                        if (xRight > ix1)
                            coverage[rowOffset + ix1] += (xRight - ix1) / subRows;
                    }
                }
            }

            for (int px = 0; px < pw; px++)
                coverage[rowOffset + px] = Math.Clamp(coverage[rowOffset + px], 0f, 1f);
        }

        return coverage;
    }

    private static List<List<(float X, float Y)>> ParseSvgPathContours(string d)
    {
        var contours = new List<List<(float X, float Y)>>();
        List<(float X, float Y)>? current = null;
        int i = 0;
        char cmd = '\0';
        float cx = 0f, cy = 0f;
        float startX = 0f, startY = 0f;

        void EnsureContour()
        {
            if (current is null)
            {
                current = [];
                contours.Add(current);
            }
        }

        while (i < d.Length)
        {
            SkipSeparators(d, ref i);
            if (i >= d.Length)
                break;

            if (char.IsLetter(d[i]))
            {
                cmd = d[i++];
                if (cmd is 'Z' or 'z')
                {
                    if (current is { Count: > 0 })
                    {
                        cx = startX;
                        cy = startY;
                        current = null;
                    }
                    continue;
                }
            }

            bool rel = char.IsLower(cmd);
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                {
                    float x = ReadFloat(d, ref i) + (rel ? cx : 0f);
                    float y = ReadFloat(d, ref i) + (rel ? cy : 0f);
                    current = [(x, y)];
                    contours.Add(current);
                    cx = startX = x;
                    cy = startY = y;
                    cmd = rel ? 'l' : 'L';
                    break;
                }
                case 'L':
                {
                    EnsureContour();
                    float x = ReadFloat(d, ref i) + (rel ? cx : 0f);
                    float y = ReadFloat(d, ref i) + (rel ? cy : 0f);
                    current!.Add((x, y));
                    cx = x;
                    cy = y;
                    break;
                }
                case 'H':
                {
                    EnsureContour();
                    float x = ReadFloat(d, ref i) + (rel ? cx : 0f);
                    current!.Add((x, cy));
                    cx = x;
                    break;
                }
                case 'V':
                {
                    EnsureContour();
                    float y = ReadFloat(d, ref i) + (rel ? cy : 0f);
                    current!.Add((cx, y));
                    cy = y;
                    break;
                }
                case 'C':
                {
                    EnsureContour();
                    float x1 = ReadFloat(d, ref i) + (rel ? cx : 0f);
                    float y1 = ReadFloat(d, ref i) + (rel ? cy : 0f);
                    float x2 = ReadFloat(d, ref i) + (rel ? cx : 0f);
                    float y2 = ReadFloat(d, ref i) + (rel ? cy : 0f);
                    float x3 = ReadFloat(d, ref i) + (rel ? cx : 0f);
                    float y3 = ReadFloat(d, ref i) + (rel ? cy : 0f);
                    FlattenCubic(cx, cy, x1, y1, x2, y2, x3, y3, current!, depth: 0);
                    cx = x3;
                    cy = y3;
                    break;
                }
                default:
                    i++;
                    break;
            }
        }

        return contours;
    }

    private static void FlattenCubic(
        float x0, float y0,
        float x1, float y1,
        float x2, float y2,
        float x3, float y3,
        List<(float X, float Y)> pts,
        int depth)
    {
        if (depth < 8)
        {
            float dx = x3 - x0;
            float dy = y3 - y0;
            float d1 = MathF.Abs((x1 - x3) * dy - (y1 - y3) * dx);
            float d2 = MathF.Abs((x2 - x3) * dy - (y2 - y3) * dx);
            float lenSq = dx * dx + dy * dy + 1e-4f;
            if ((d1 + d2) * (d1 + d2) > 0.08f * lenSq)
            {
                float x01 = 0.5f * (x0 + x1), y01 = 0.5f * (y0 + y1);
                float x12 = 0.5f * (x1 + x2), y12 = 0.5f * (y1 + y2);
                float x23 = 0.5f * (x2 + x3), y23 = 0.5f * (y2 + y3);
                float x012 = 0.5f * (x01 + x12), y012 = 0.5f * (y01 + y12);
                float x123 = 0.5f * (x12 + x23), y123 = 0.5f * (y12 + y23);
                float xm = 0.5f * (x012 + x123), ym = 0.5f * (y012 + y123);
                FlattenCubic(x0, y0, x01, y01, x012, y012, xm, ym, pts, depth + 1);
                FlattenCubic(xm, ym, x123, y123, x23, y23, x3, y3, pts, depth + 1);
                return;
            }
        }

        pts.Add((x3, y3));
    }

    private static void SkipSeparators(string s, ref int i)
    {
        while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ','))
            i++;
    }

    private static float ReadFloat(string s, ref int i)
    {
        SkipSeparators(s, ref i);
        int start = i;
        if (i < s.Length && (s[i] == '-' || s[i] == '+'))
            i++;
        bool sawDot = false;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsAsciiDigit(c))
            {
                i++;
            }
            else if (c == '.' && !sawDot)
            {
                sawDot = true;
                i++;
            }
            else if ((c == 'e' || c == 'E') && i > start)
            {
                i++;
                if (i < s.Length && (s[i] == '-' || s[i] == '+'))
                    i++;
            }
            else
            {
                break;
            }
        }

        if (start == i)
            return 0f;

        return float.Parse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static float BilinearSurfaceNoise(float gx, float gy)
    {
        int x0 = (int)MathF.Floor(gx);
        int y0 = (int)MathF.Floor(gy);
        float fx = gx - x0;
        float fy = gy - y0;
        float u = fx * fx * (3f - 2f * fx);
        float v = fy * fy * (3f - 2f * fy);

        float n00 = HashSigned(x0, y0);
        float n10 = HashSigned(x0 + 1, y0);
        float n01 = HashSigned(x0, y0 + 1);
        float n11 = HashSigned(x0 + 1, y0 + 1);

        float top = n00 + (n10 - n00) * u;
        float bot = n01 + (n11 - n01) * u;
        return top + (bot - top) * v;
    }

    private static float HashSigned(int x, int y)
    {
        uint h = (uint)(x * 374761393 + y * 668265263) ^ 0x9E3779B9u;
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return ((h & 0xFFFFu) / 32767.5f) - 1f;
    }

    private static float SmoothMin(float a, float b, float k)
    {
        float h = Math.Clamp(0.5f + 0.5f * (b - a) / k, 0f, 1f);
        return b * (1f - h) + a * h - k * h * (1f - h);
    }

    public static RasterImage RenderSculptedModalWindow(int w1x, int h1x, int headerH1x = 46, int scale = 4)
    {
        scale = Math.Clamp(scale, 1, 6);
        string cacheKey = $"modalwin:{w1x}:{h1x}:{headerH1x}:{scale}";
        if (RasterCache.TryGetValue(cacheKey, out var cached))
            return cached;

        const int pad1x = 8; // outer drop-shadow margin
        int totalW1x = w1x + pad1x * 2;
        int totalH1x = h1x + pad1x * 2;
        int pw = totalW1x * scale;
        int ph = totalH1x * scale;
        byte[] rgba = new byte[pw * ph * 4];
        float invScale = 1f / scale;

        float halfW = w1x * 0.5f;
        float halfH = h1x * 0.5f;
        float cx = pad1x + halfW;
        float cy = pad1x + halfH;
        const float cornerR = 14f;

        for (int py = 0; py < ph; py++)
        {
            float y1x = (py + 0.5f) * invScale;
            float localY = y1x - pad1x;
            for (int px = 0; px < pw; px++)
            {
                float x1x = (px + 0.5f) * invScale;
                float localX = x1x - pad1x;

                // Soft external drop shadow shifted (+2.0, +3.5).
                float shSdf = RoundedRectSdf(x1x - (cx + 2.0f), y1x - (cy + 3.5f), halfW, halfH, cornerR);
                float shAlpha = Math.Clamp(1f - SmoothStep(-1f, 6.5f, shSdf), 0f, 1f) * 0.62f;

                float dWin = RoundedRectSdf(x1x - cx, y1x - cy, halfW, halfH, cornerR);
                float winCov = Math.Clamp(0.5f - dWin * scale, 0f, 1f);

                float outR = 0f, outG = 2f, outB = 18f, outA = shAlpha;
                if (winCov > 0.001f)
                {
                    float r, g, b;
                    float tHoriz = Math.Clamp(localX / MathF.Max(1f, w1x), 0f, 1f);

                    if (localY <= headerH1x)
                    {
                        // Sculpted Maxis Neighborhood Banner header gradient + fine surface grain!
                        r = Lerp(96f, 12f, tHoriz);
                        g = Lerp(96f, 14f, tHoriz);
                        b = Lerp(146f, 84f, tHoriz);

                        // Header bottom groove separator
                        if (localY > headerH1x - 2.2f)
                        {
                            float sepT = SmoothStep(headerH1x - 2.2f, headerH1x - 0.8f, localY);
                            r = Lerp(r, 4f, sepT * 0.82f);
                            g = Lerp(g, 8f, sepT * 0.82f);
                            b = Lerp(b, 38f, sepT * 0.82f);
                        }
                    }
                    else
                    {
                        // Crisp deep indigo-slate modal body with subtle vertical depth gradient.
                        float tVert = Math.Clamp((localY - headerH1x) / MathF.Max(1f, h1x - headerH1x), 0f, 1f);
                        r = Lerp(22f, 11f, tVert) + (1f - tHoriz) * 6f;
                        g = Lerp(28f, 16f, tVert) + (1f - tHoriz) * 6f;
                        b = Lerp(78f, 54f, tVert) + (1f - tHoriz) * 8f;

                        if (localY < headerH1x + 1.4f)
                        {
                            // Crisp bevel highlight right below header groove
                            r += 22f;
                            g += 26f;
                            b += 38f;
                        }
                    }

                    // Lit-proportional fine matte grain matching the Neighborhood banner
                    float lum = 0.299f * r + 0.587f * g + 0.114f * b;
                    float litFactor = MathF.Pow(Math.Clamp((lum - 20f) / 92f, 0f, 1f), 1.35f);
                    if (litFactor > 0.001f)
                    {
                        float grain = BilinearSurfaceNoise(localX * 2.2f, localY * 2.2f) * 4.2f * litFactor;
                        r += grain;
                        g += grain;
                        b += grain;
                    }

                    // Crisp 3D Maxis silver-slate outer bevel rim (zero blurry corner fade!)
                    float dIn = -dWin;
                    if (dIn < 2.4f)
                    {
                        float diag = ((localX / halfW - 1f) * 0.55f + (localY / halfH - 1f) * 0.80f) * 0.5f + 0.5f;
                        diag = Math.Clamp(diag, 0f, 1f);
                        float rimR = Lerp(218f, 62f, diag);
                        float rimG = Lerp(224f, 72f, diag);
                        float rimB = Lerp(244f, 126f, diag);
                        if (dIn < 0.85f)
                        {
                            rimR *= 0.42f;
                            rimG *= 0.45f;
                            rimB *= 0.58f;
                        }
                        float rimMix = 1f - SmoothStep(1.1f, 2.4f, dIn);
                        r = Lerp(r, rimR, rimMix * 0.88f);
                        g = Lerp(g, rimG, rimMix * 0.88f);
                        b = Lerp(b, rimB, rimMix * 0.88f);
                    }

                    BlendPixel(ref outR, ref outG, ref outB, ref outA, r, g, b, winCov);
                }

                if (outA <= 0.001f)
                    continue;
                int idx = (py * pw + px) * 4;
                rgba[idx] = ClampByte(outR);
                rgba[idx + 1] = ClampByte(outG);
                rgba[idx + 2] = ClampByte(outB);
                rgba[idx + 3] = ClampByte(outA * 255f);
            }
        }

        var result = new RasterImage(pw, ph, rgba, new CGSize(totalW1x, totalH1x));
        RasterCache[cacheKey] = result;
        return result;
    }

    public static RasterImage RenderRecessedWell(int w1x, int h1x, float cornerR = 8f, int scale = 4)
    {
        scale = Math.Clamp(scale, 1, 6);
        string cacheKey = $"well:{w1x}:{h1x}:{cornerR:F1}:{scale}";
        if (RasterCache.TryGetValue(cacheKey, out var cached))
            return cached;

        int pw = w1x * scale;
        int ph = h1x * scale;
        byte[] rgba = new byte[pw * ph * 4];
        float invScale = 1f / scale;
        float halfW = w1x * 0.5f;
        float halfH = h1x * 0.5f;

        for (int py = 0; py < ph; py++)
        {
            float y1x = (py + 0.5f) * invScale;
            for (int px = 0; px < pw; px++)
            {
                float x1x = (px + 0.5f) * invScale;
                float d = RoundedRectSdf(x1x - halfW, y1x - halfH, halfW - 0.5f, halfH - 0.5f, cornerR);
                float cov = Math.Clamp(0.5f - d * scale, 0f, 1f);
                if (cov <= 0.001f)
                    continue;

                float r = 7f, g = 12f, b = 42f;
                // Top-left sunken inner shadow
                float shD = RoundedRectSdf(x1x - (halfW + 1.2f), y1x - (halfH + 1.4f), halfW - 1.5f, halfH - 1.5f, MathF.Max(2f, cornerR - 1f));
                if (shD > -1.8f)
                {
                    float shMix = SmoothStep(-1.8f, 0.6f, shD) * 0.72f;
                    r = Lerp(r, 2f, shMix);
                    g = Lerp(g, 4f, shMix);
                    b = Lerp(b, 20f, shMix);
                }

                // Crisp 1.2px sunken rim (dark top-left, subtle slate-blue bottom-right)
                float dIn = -d;
                if (dIn < 1.5f)
                {
                    float diag = ((x1x / halfW - 1f) * 0.6f + (y1x / halfH - 1f) * 0.8f) * 0.5f + 0.5f;
                    float rimMix = 1f - SmoothStep(0.6f, 1.5f, dIn);
                    r = Lerp(r, Lerp(4f, 68f, diag), rimMix);
                    g = Lerp(g, Lerp(8f, 86f, diag), rimMix);
                    b = Lerp(b, Lerp(30f, 156f, diag), rimMix);
                }

                int idx = (py * pw + px) * 4;
                rgba[idx] = ClampByte(r);
                rgba[idx + 1] = ClampByte(g);
                rgba[idx + 2] = ClampByte(b);
                rgba[idx + 3] = ClampByte(cov * 255f);
            }
        }

        var result = new RasterImage(pw, ph, rgba, new CGSize(w1x, h1x));
        RasterCache[cacheKey] = result;
        return result;
    }

    public static RasterImage RenderCapsuleButtonStrip(int w1x, int h1x, bool primaryAccent = false, int scale = 4)
    {
        scale = Math.Clamp(scale, 1, 6);
        string cacheKey = $"capbtn:{w1x}:{h1x}:{(primaryAccent ? 1 : 0)}:{scale}";
        if (RasterCache.TryGetValue(cacheKey, out var cached))
            return cached;

        int fw = w1x * scale;
        int fh = h1x * scale;
        int stripW = fw * 4;
        byte[] strip = new byte[stripW * fh * 4];
        float invScale = 1f / scale;
        float halfW = w1x * 0.5f;
        float halfH = h1x * 0.5f;
        float rCorner = MathF.Min(halfH - 0.5f, 8.5f);

        for (int state = 0; state < 4; state++)
        {
            var th = MedallionThemes[state];
            for (int py = 0; py < fh; py++)
            {
                float y1x = (py + 0.5f) * invScale;
                float tVert = Math.Clamp(y1x / h1x, 0f, 1f);
                for (int px = 0; px < fw; px++)
                {
                    float x1x = (px + 0.5f) * invScale;
                    float d = RoundedRectSdf(x1x - halfW, y1x - halfH, halfW - 0.6f, halfH - 0.6f, rCorner);
                    float cov = Math.Clamp(0.5f - d * scale, 0f, 1f);
                    if (cov <= 0.001f)
                        continue;

                    // Button body gradient (Maxis beveled sapphire/teal, brighter when primaryAccent or hovered)
                    float r, g, b;
                    if (state == 3)
                    {
                        r = Lerp(42f, 28f, tVert);
                        g = Lerp(48f, 34f, tVert);
                        b = Lerp(96f, 72f, tVert);
                    }
                    else if (state is 1 or 2)
                    {
                        r = Lerp(18f, 4f, tVert);
                        g = Lerp(116f, 54f, tVert);
                        b = Lerp(202f, 138f, tVert);
                    }
                    else if (primaryAccent)
                    {
                        r = Lerp(16f, 6f, tVert);
                        g = Lerp(106f, 52f, tVert);
                        b = Lerp(168f, 112f, tVert);
                    }
                    else
                    {
                        r = Lerp(26f, 10f, tVert);
                        g = Lerp(58f, 28f, tVert);
                        b = Lerp(142f, 94f, tVert);
                    }

                    // Top specular highlight inside the button
                    float dIn = -d;
                    if (y1x < halfH && dIn >= 1.5f && dIn < 3.6f)
                    {
                        float spec = MathF.Exp(-((dIn - 2.3f) * (dIn - 2.3f)) / 0.65f) * (state == 3 ? 8f : 26f);
                        r += spec;
                        g += spec;
                        b += spec;
                    }

                    // Crisp metallic/cyan ring rim matching the Neighborhood banner medallions
                    if (dIn < 1.85f)
                    {
                        float diag = tVert * 0.7f + (x1x / w1x) * 0.3f;
                        float rr = Lerp(th.RingHi.R, th.RingLo.R, diag);
                        float rg = Lerp(th.RingHi.G, th.RingLo.G, diag);
                        float rb = Lerp(th.RingHi.B, th.RingLo.B, diag);
                        if (dIn < 0.65f)
                        {
                            rr = Lerp(rr, th.RingRim.R, 0.65f);
                            rg = Lerp(rg, th.RingRim.G, 0.65f);
                            rb = Lerp(rb, th.RingRim.B, 0.65f);
                        }
                        float rimMix = 1f - SmoothStep(1.25f, 1.85f, dIn);
                        r = Lerp(r, rr, rimMix);
                        g = Lerp(g, rg, rimMix);
                        b = Lerp(b, rb, rimMix);
                    }

                    int dst = (py * stripW + state * fw + px) * 4;
                    strip[dst] = ClampByte(r);
                    strip[dst + 1] = ClampByte(g);
                    strip[dst + 2] = ClampByte(b);
                    strip[dst + 3] = ClampByte(cov * 255f);
                }
            }
        }

        var result = new RasterImage(stripW, fh, strip, new CGSize(w1x * 4, h1x));
        RasterCache[cacheKey] = result;
        return result;
    }

    public static RasterImage RenderToggleSwitchStrip(int w1x = 36, int h1x = 18, int scale = 4)
    {
        scale = Math.Clamp(scale, 1, 6);
        string cacheKey = $"toggle:{w1x}:{h1x}:{scale}";
        if (RasterCache.TryGetValue(cacheKey, out var cached))
            return cached;

        int fw = w1x * scale;
        int fh = h1x * scale;
        int stripW = fw * 2; // frame 0 = OFF, frame 1 = ON
        byte[] strip = new byte[stripW * fh * 4];
        float invScale = 1f / scale;
        float halfW = w1x * 0.5f;
        float halfH = h1x * 0.5f;
        float rTrack = halfH - 0.8f;

        for (int state = 0; state < 2; state++)
        {
            bool isOn = state == 1;
            float knobCx = isOn ? (w1x - halfH) : halfH;
            float knobR = halfH - 2.4f;

            for (int py = 0; py < fh; py++)
            {
                float y1x = (py + 0.5f) * invScale;
                for (int px = 0; px < fw; px++)
                {
                    float x1x = (px + 0.5f) * invScale;
                    float dTrack = RoundedRectSdf(x1x - halfW, y1x - halfH, halfW - 0.8f, halfH - 0.8f, rTrack);
                    float cov = Math.Clamp(0.5f - dTrack * scale, 0f, 1f);
                    if (cov <= 0.001f)
                        continue;

                    float tV = y1x / h1x;
                    float r = isOn ? Lerp(8f, 14f, tV) : Lerp(22f, 32f, tV);
                    float g = isOn ? Lerp(138f, 178f, tV) : Lerp(28f, 38f, tV);
                    float b = isOn ? Lerp(142f, 168f, tV) : Lerp(66f, 84f, tV);

                    float dIn = -dTrack;
                    if (dIn < 1.3f)
                    {
                        float rimMix = 1f - SmoothStep(0.7f, 1.3f, dIn);
                        r = Lerp(r, isOn ? 90f : 86f, rimMix);
                        g = Lerp(g, isOn ? 245f : 96f, rimMix);
                        b = Lerp(b, isOn ? 235f : 142f, rimMix);
                    }

                    // 3D metallic knob
                    float kDist = MathF.Sqrt((x1x - knobCx) * (x1x - knobCx) + (y1x - halfH) * (y1x - halfH));
                    float shDist = MathF.Sqrt((x1x - (knobCx + 0.6f)) * (x1x - (knobCx + 0.6f)) + (y1x - (halfH + 0.8f)) * (y1x - (halfH + 0.8f)));
                    float shCov = Math.Clamp((knobR + 0.8f - shDist) * scale, 0f, 1f) * 0.45f;
                    r *= 1f - shCov;
                    g *= 1f - shCov;
                    b *= 1f - shCov;

                    float knobCov = Math.Clamp((knobR - kDist) * scale, 0f, 1f);
                    if (knobCov > 0.001f)
                    {
                        float kt = Math.Clamp((y1x - (halfH - knobR)) / (knobR * 2f), 0f, 1f);
                        float kr = Lerp(252f, isOn ? 215f : 175f, kt);
                        float kg = Lerp(254f, isOn ? 248f : 184f, kt);
                        float kb = Lerp(255f, isOn ? 252f : 212f, kt);
                        r = Lerp(r, kr, knobCov);
                        g = Lerp(g, kg, knobCov);
                        b = Lerp(b, kb, knobCov);
                    }

                    int dst = (py * stripW + state * fw + px) * 4;
                    strip[dst] = ClampByte(r);
                    strip[dst + 1] = ClampByte(g);
                    strip[dst + 2] = ClampByte(b);
                    strip[dst + 3] = ClampByte(cov * 255f);
                }
            }
        }

        var result = new RasterImage(stripW, fh, strip, new CGSize(w1x * 2, h1x));
        RasterCache[cacheKey] = result;
        return result;
    }

    private static float RoundedRectSdf(float dx, float dy, float halfW, float halfH, float r)
    {
        float qx = MathF.Abs(dx) - (halfW - r);
        float qy = MathF.Abs(dy) - (halfH - r);
        float outDist = MathF.Sqrt(MathF.Max(qx, 0f) * MathF.Max(qx, 0f) + MathF.Max(qy, 0f) * MathF.Max(qy, 0f));
        float inDist = MathF.Min(MathF.Max(qx, qy), 0f);
        return outDist + inDist - r;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static byte ClampByte(float v) =>
        (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
}
