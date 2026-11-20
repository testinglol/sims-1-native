using System.Collections.Concurrent;
using System.Text.Json;
using SimsSDL.Core;

namespace SimsSDL.UI;

public static partial class SimsVectorUiKit
{
    private static readonly ConcurrentDictionary<string, FittedPanel> FittedPanels = new(StringComparer.Ordinal);

    /// <summary>
    /// Draws a control-panel background from its vector model (VectorUI/Panels/&lt;name&gt;.json, produced by
    /// tools/ui/fit_layered_panel.py). Every layer adds a height profile to one shared surface, painted bottom to top,
    /// and that surface is lit once, so bevels where shapes meet merge instead of overlapping.
    /// </summary>
    public static VectorBannerRenderer.RasterImage RenderFittedPanel(string name, int scale = 4)
    {
        scale = Math.Clamp(scale, 1, 8);
        string key = $"panel_fit_v49:{name}:{scale}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var panel = FittedPanels.GetOrAdd(name, LoadFittedPanel);
        int pw = panel.Width * scale;
        int ph = panel.Height * scale;
        int n = pw * ph;
        int layerCount = panel.Layers.Length;
        float reach = panel.HeightStops[^1] + 1f;
        float inv = 1f / scale;

        var dists = new float[layerCount][];
        for (int li = 0; li < layerCount; li++)
            dists[li] = PanelDistanceField(panel.Layers[li].Contours, pw, ph, scale, reach);

        var bevelDists = new float[layerCount][];
        for (int li = 0; li < layerCount; li++)
        {
            float[] raw = dists[li];
            if (li == 0 && layerCount > 1)
            {
                raw = new float[n];
                float[] d0 = dists[0];
                float[] d1 = dists[1];
                for (int i = 0; i < n; i++)
                    raw[i] = MathF.Min(d0[i], -d1[i]);
            }
            else if (li == 1 && layerCount >= 5)
            {
                // Cut recessed bevel groove into field around housing (layer 2).
                raw = new float[n];
                float[] d1 = dists[1];
                float[] d2 = dists[2];
                for (int i = 0; i < n; i++)
                    raw[i] = MathF.Min(d1[i], -d2[i]);
            }
            bevelDists[li] = panel.CornerSmooth > 0.01f
                ? SepGaussianBlurSdf(raw, pw, ph, panel.CornerSmooth * scale, scale)
                : raw;
        }

        float[]? padRecessSdf = null;
        if (panel.PadRecess > 0.001f && layerCount >= 5)
        {
            float pSmooth = panel.PadRecessSmooth > 0f ? panel.PadRecessSmooth : panel.CornerSmooth;
            padRecessSdf = pSmooth > 0.01f
                ? SepGaussianBlurSdf(dists[4], pw, ph, pSmooth * scale, scale)
                : dists[4];
        }
        float padRecessW = panel.PadRecessWidth > 0.1f ? panel.PadRecessWidth : 4.5f;

        var weights = new float[layerCount][];
        var layerHeights = new float[layerCount][];
        for (int li = 0; li < layerCount; li++)
        {
            weights[li] = new float[n];
            layerHeights[li] = new float[n];
        }
        float[] compositeHeight = new float[n];
        Parallel.For(0, n, i =>
        {
            float rest = 1f;
            float padRecessDelta = 0f;
            if (padRecessSdf is not null)
            {
                float outD = MathF.Max(0f, -padRecessSdf[i]);
                if (outD < padRecessW)
                {
                    float g = 1f - outD / padRecessW;
                    padRecessDelta = panel.PadRecess * 5.5f * g * g * (3f - 2f * g);
                }
            }
            float wellsRecessDelta = 0f;
            if (panel.WellsRecess > 0.001f && layerCount >= 5)
            {
                float outD = MathF.Max(0f, -bevelDists[3][i]);
                if (outD < 4.5f)
                {
                    float g = 1f - outD / 4.5f;
                    wellsRecessDelta = panel.WellsRecess * 10f * g * g * (3f - 2f * g);
                }
            }

            for (int li = layerCount - 1; li >= 0; li--)
            {
                float d = dists[li][i];
                float c = Math.Clamp(0.5f + d * scale, 0f, 1f);
                float w = c * rest;
                weights[li][i] = w;
                rest *= 1f - c;

                float lh = Profile(panel.Layers[li].Profile, bevelDists[li][i]);
                if (li is 1 or 2)
                    lh -= padRecessDelta;
                if (li == 2)
                    lh -= wellsRecessDelta;
                layerHeights[li][i] = lh;
                compositeHeight[i] += w * lh;
            }
        });

        float effNormalBlur = MathF.Max(panel.NormalBlur, 0.35f) * scale;
        var layerSmooth = new float[layerCount][];
        var layerCreaseBase = new float[layerCount][];
        for (int li = 0; li < layerCount; li++)
        {
            layerSmooth[li] = SepGaussianBlur(layerHeights[li], pw, ph, effNormalBlur);
            layerCreaseBase[li] = SepGaussianBlur(layerHeights[li], pw, ph, MathF.Max(panel.CreaseBlur, 0.35f) * scale);
        }
        float[] cast = CastShadow(compositeHeight, pw, ph, panel.LightAngle, panel.CastReach, scale);

        float lx = MathF.Cos(panel.LightAltitude) * MathF.Cos(panel.LightAngle);
        float ly = MathF.Cos(panel.LightAltitude) * MathF.Sin(panel.LightAngle);
        float lz = MathF.Sin(panel.LightAltitude);
        float hz = 1f + lz;
        float hn = MathF.Sqrt(lx * lx + ly * ly + hz * hz);
        float flatSpec = MathF.Pow(hz / hn, panel.Gloss);
        float[][][] gains = panel.Layers
            .Select(l => new[] { l.Highlight, l.Shadow, l.Specular, l.Crease, l.Ridge, l.Cast })
            .ToArray();

        byte[] rgba = new byte[n * 4];
        Parallel.For(0, ph, py =>
        {
            Span<float> terms = stackalloc float[6];
            float y = (py + 0.5f) * inv;
            int yUp = Math.Max(py - 1, 0), yDown = Math.Min(py + 1, ph - 1);
            for (int px = 0; px < pw; px++)
            {
                int i = py * pw + px;
                int xLeft = Math.Max(px - 1, 0), xRight = Math.Min(px + 1, pw - 1);
                float x = (px + 0.5f) * inv;
                float r = 0f, g = 0f, b = 0f;
                for (int li = 0; li < layerCount; li++)
                {
                    float w = weights[li][i];
                    if (w <= 0f)
                        continue;

                    float[] sm = layerSmooth[li];
                    float[] lh = layerHeights[li];
                    float[] cb = layerCreaseBase[li];
                    float gx = (sm[py * pw + xRight] - sm[py * pw + xLeft]) / (xRight - xLeft) * scale;
                    float gy = (sm[yDown * pw + px] - sm[yUp * pw + px]) / (yDown - yUp) * scale;
                    float nz = 1f / MathF.Sqrt(1f + gx * gx + gy * gy);
                    float diffuse = (-gx * lx - gy * ly + lz) * nz - lz;
                    float ndh = (-gx * lx - gy * ly + hz) * nz / hn;
                    float crease = cb[i] - lh[i];
                    terms[0] = MathF.Max(0f, diffuse);
                    float rawShadow = MathF.Max(0f, -diffuse);
                    float dirShadow = rawShadow > 0f ? MathF.Pow(rawShadow, 0.55f) * 0.85f : 0f;
                    if (panel.ShadowWrap > 0.001f)
                    {
                        float slope = MathF.Sqrt(gx * gx + gy * gy) * nz;
                        float notLit = 1f - Math.Clamp(terms[0] * 1.8f, 0f, 1f);
                        float evenShadow = MathF.Min(1f, slope * 1.25f) * notLit * 0.85f;
                        terms[1] = Lerp(dirShadow, evenShadow, Math.Clamp(panel.ShadowWrap, 0f, 1f));
                    }
                    else
                    {
                        terms[1] = dirShadow;
                    }
                    terms[2] = MathF.Max(0f, MathF.Pow(MathF.Max(0f, ndh), panel.Gloss) - flatSpec);
                    terms[3] = MathF.Max(0f, crease);
                    terms[4] = MathF.Max(0f, -crease);
                    terms[5] = cast[i];

                    var layer = panel.Layers[li];
                    var c = StopShade(layer.Flat, panel.HeightStops, dists[li][i]);
                    for (int z = 0; z < panel.Zones.Length; z++)
                    {
                        float[] zone = panel.Zones[z];
                        float zw = Math.Clamp((zone[2] - MathF.Sqrt((x - zone[0]) * (x - zone[0]) + (y - zone[1]) * (y - zone[1]))) / zone[3] + 0.5f, 0f, 1f);
                        c = (c.R + zw * layer.ZoneFlat[z][0], c.G + zw * layer.ZoneFlat[z][1], c.B + zw * layer.ZoneFlat[z][2]);
                    }
                    float[][] lg = gains[li];
                    float specVal = terms[2];
                    if (layerCount >= 5 && li is 1 or 2)
                    {
                        float outPad = -dists[4][i];
                        if (li == 1)
                        {
                            float padMask = SmoothStep(4.0f, 7.0f, outPad);
                            float botMask = 1f - SmoothStep(panel.Height - 10.5f, panel.Height - 5.5f, y);
                            specVal *= padMask * botMask;
                        }
                        else
                        {
                            // Fade contact specular right against pad's overhanging rim and roll off into the bottom screen bevel.
                            float padContact = SmoothStep(0.4f, 2.4f, outPad);
                            float botCorner = x < 62f ? 1f - SmoothStep(panel.Height - 13.5f, panel.Height - 4.5f, y) : 1f;
                            specVal *= padContact * botCorner;
                        }
                    }
                    for (int t = 0; t < lg.Length; t++)
                    {
                        float tv = t == 2 ? specVal : terms[t];
                        c = (c.R + tv * lg[t][0], c.G + tv * lg[t][1], c.B + tv * lg[t][2]);
                    }
                    r += w * c.R;
                    g += w * c.G;
                    b += w * c.B;
                }

                float a = Math.Clamp(0.5f + dists[0][i] * scale, 0f, 1f);
                float ia = a > 1e-4f ? 1f / a : 0f;
                float pr = r * ia, pg = g * ia, pb = b * ia;
                if (panel.ShadowDepth > 0.001f)
                {
                    float castNorm = cast[i] / (cast[i] + 10f);
                    float sDark = Math.Clamp(panel.ShadowDepth * (0.30f * terms[1] + 0.40f * castNorm), 0f, 0.45f);
                    float sMul = 1f - sDark;
                    pr *= sMul;
                    pg *= sMul;
                    pb *= sMul;
                }
                // Crisp tight bottom/bottom-right rim highlight on recessed LCD wells (layer 3)
                // using the well contour's own inward normal so it always hugs the bottom lip.
                if (panel.RimBounce > 0.001f && layerCount >= 5 && weights[3][i] > 0.01f)
                {
                    float[] d3 = dists[3];
                    float rimEdge = Math.Clamp(1f - MathF.Max(0f, d3[i]) / 2.2f, 0f, 1f);
                    if (rimEdge > 0f)
                    {
                        float nx3 = (d3[py * pw + xRight] - d3[py * pw + xLeft]) / (xRight - xLeft);
                        float ny3 = (d3[yDown * pw + px] - d3[yUp * pw + px]) / (yDown - yUp);
                        float invLen = 1f / MathF.Max(1e-4f, MathF.Sqrt(nx3 * nx3 + ny3 * ny3));
                        // Inward normal (nx3, ny3) points UP on the bottom rim; dot with upper-left light (lx, ly).
                        float rimDot = MathF.Max(0f, (nx3 * lx + ny3 * ly) * invLen);
                        float rimSpec = MathF.Pow(rimDot, 1.8f) * rimEdge * rimEdge * panel.RimBounce * weights[3][i];
                        pr += rimSpec * 150f;
                        pg += rimSpec * 162f;
                        pb += rimSpec * 205f;
                    }
                }
                rgba[i * 4] = Byte(pr);
                rgba[i * 4 + 1] = Byte(pg);
                rgba[i * 4 + 2] = Byte(pb);
                rgba[i * 4 + 3] = Byte(a * 255f);
            }
        });

        var img = new VectorBannerRenderer.RasterImage(pw, ph, rgba, new CGSize(panel.Width, panel.Height));
        Cache[key] = img;
        return img;
    }

    /// <summary>Separable Gaussian blur for signed distance fields that extrapolates linearly
    /// off canvas edges (<c>d - overshoot / scale</c>) instead of clamping, so screen-edge bevels
    /// (left x=0, bottom y=height) keep their full slope and depth.</summary>
    private static float[] SepGaussianBlurSdf(float[] src, int w, int h, float sigma, int scale)
    {
        if (sigma <= 0.05f)
            return (float[])src.Clone();
        int radius = Math.Max(1, (int)MathF.Ceiling(sigma * 3f));
        float[] kernel = new float[radius * 2 + 1];
        float sum = 0f;
        float inv2s2 = 1f / (2f * sigma * sigma);
        for (int k = -radius; k <= radius; k++)
        {
            float v = MathF.Exp(-k * k * inv2s2);
            kernel[k + radius] = v;
            sum += v;
        }
        for (int k = 0; k < kernel.Length; k++)
            kernel[k] /= sum;

        float invScale = 1f / scale;
        float[] tmp = new float[w * h];
        float[] dst = new float[w * h];

        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float acc = 0f;
                for (int k = -radius; k <= radius; k++)
                {
                    int sx = x + k;
                    float sample = sx < 0
                        ? src[row] + sx * invScale
                        : sx >= w
                            ? src[row + w - 1] - (sx - (w - 1)) * invScale
                            : src[row + sx];
                    acc += sample * kernel[k + radius];
                }
                tmp[row + x] = acc;
            }
        });

        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float acc = 0f;
                for (int k = -radius; k <= radius; k++)
                {
                    int sy = y + k;
                    float sample = sy < 0
                        ? tmp[x] + sy * invScale
                        : sy >= h
                            ? tmp[(h - 1) * w + x] - (sy - (h - 1)) * invScale
                            : tmp[sy * w + x];
                    acc += sample * kernel[k + radius];
                }
                dst[row + x] = acc;
            }
        });

        return dst;
    }

    private static float[] CastShadow(float[] height, int pw, int ph, float angle, float reach, int scale)
    {
        float cosA = MathF.Cos(angle);
        float sinA = MathF.Sin(angle);
        float maxDist = reach * scale;
        int samples = Math.Max(8, (int)MathF.Ceiling(reach * 2f));
        float[] lift = new float[pw * ph];
        Parallel.For(0, ph, py =>
        {
            int row = py * pw;
            for (int px = 0; px < pw; px++)
            {
                float h0 = height[row + px];
                float acc = 0f;
                float wSum = 0f;
                for (int s = 1; s <= samples; s++)
                {
                    float distPx = maxDist * s / samples;
                    float fx = Math.Clamp(px + cosA * distPx, 0f, pw - 1.001f);
                    float fy = Math.Clamp(py + sinA * distPx, 0f, ph - 1.001f);
                    int x0 = (int)fx, y0 = (int)fy;
                    float tx = fx - x0, ty = fy - y0;
                    int r0 = y0 * pw, r1 = (y0 + 1) * pw;
                    float hs = (height[r0 + x0] * (1f - tx) + height[r0 + x0 + 1] * tx) * (1f - ty)
                             + (height[r1 + x0] * (1f - tx) + height[r1 + x0 + 1] * tx) * ty;
                    float w = 1f - 0.55f * (s - 1f) / samples;
                    float diff = hs - h0;
                    if (diff > 0f)
                        acc += diff * w;
                    wSum += w;
                }
                lift[row + px] = wSum > 0f ? acc / wSum : 0f;
            }
        });
        return SepGaussianBlur(lift, pw, ph, MathF.Max(0.8f, maxDist * 0.35f));
    }

    private static float Profile(float[] p, float d)
    {
        float width = MathF.Max(0.001f, p[2]);
        float power = p[3];
        // Extend t smoothly slightly below 0 (where CornerSmooth pulls convex corners inward)
        // so height, slope, and curvature remain C-infinity continuous across d = 0.
        float t = Math.Clamp(d / width, -0.45f, 1f);
        return p[0] + p[1] * (1f - MathF.Pow(1f - t, power));
    }

    private static (float R, float G, float B) StopShade(float[] rgb, float[] stops, float d)
    {
        // Clamp d >= 0.5 so anti-aliased edge pixels never sample unconstrained negative-distance
        // regression stops (d < 0) or the 0px anti-aliasing-edge spike (d = 0).
        d = MathF.Max(0.5f, d);
        int j = 0;
        while (j < stops.Length - 2 && d > stops[j + 1])
            j++;
        float fd = Math.Clamp((d - stops[j]) / (stops[j + 1] - stops[j]), 0f, 1f);
        return (Lerp(rgb[j * 3], rgb[j * 3 + 3], fd), Lerp(rgb[j * 3 + 1], rgb[j * 3 + 4], fd), Lerp(rgb[j * 3 + 2], rgb[j * 3 + 5], fd));
    }

    private static float[] PanelDistanceField(float[][] contours, int pw, int ph, int scale, float reach)
    {
        var segs = new List<(float X0, float Y0, float X1, float Y1)>();
        foreach (float[] c in contours)
        {
            int count = c.Length / 2;
            for (int k = 0; k < count; k++)
            {
                int k1 = (k + 1) % count;
                segs.Add((c[k * 2], c[k * 2 + 1], c[k1 * 2], c[k1 * 2 + 1]));
            }
        }

        float[] d = new float[pw * ph];
        float inv = 1f / scale;
        Parallel.For(0, ph, py =>
        {
            float y = (py + 0.5f) * inv;
            var near = new List<(float X0, float Y0, float X1, float Y1)>();
            var crossings = new List<float>();
            foreach (var s in segs)
            {
                if (MathF.Min(s.Y0, s.Y1) - reach <= y && MathF.Max(s.Y0, s.Y1) + reach >= y)
                    near.Add(s);
                if ((s.Y0 > y) != (s.Y1 > y))
                    crossings.Add(s.X0 + (y - s.Y0) * (s.X1 - s.X0) / (s.Y1 - s.Y0));
            }
            crossings.Sort();

            int crossed = 0;
            for (int px = 0; px < pw; px++)
            {
                float x = (px + 0.5f) * inv;
                while (crossed < crossings.Count && crossings[crossed] < x)
                    crossed++;
                float best = reach * reach;
                foreach (var s in near)
                {
                    if (MathF.Min(s.X0, s.X1) - reach > x || MathF.Max(s.X0, s.X1) + reach < x)
                        continue;
                    float vx = s.X1 - s.X0, vy = s.Y1 - s.Y0;
                    float t = Math.Clamp(((x - s.X0) * vx + (y - s.Y0) * vy) / (vx * vx + vy * vy + 1e-12f), 0f, 1f);
                    float cx = s.X0 + t * vx - x, cy = s.Y0 + t * vy - y;
                    best = MathF.Min(best, cx * cx + cy * cy);
                }
                float dist = MathF.Sqrt(best);
                d[py * pw + px] = (crossed & 1) == 1 ? dist : -dist;
            }
        });
        return d;
    }

    private static FittedPanel LoadFittedPanel(string name)
    {
        string suffix = $".VectorUI.Panels.{name}.json";
        var asm = typeof(SimsVectorUiKit).Assembly;
        string resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new ContentError($"Missing embedded panel model '{name}.json'");
        using Stream stream = asm.GetManifestResourceStream(resName)
            ?? throw new ContentError($"Could not open embedded panel model '{resName}'");
        return JsonSerializer.Deserialize<FittedPanel>(stream, FittedPanelJson)
            ?? throw new ContentError($"Empty panel model '{resName}'");
    }

    private static readonly JsonSerializerOptions FittedPanelJson = new() { PropertyNameCaseInsensitive = true };

    private sealed record FittedPanel(
        int Width,
        int Height,
        float NormalBlur,
        float[] HeightStops,
        float LightAngle,
        float LightAltitude,
        float Gloss,
        float CreaseBlur,
        float CastReach,
        float[][] Zones,
        FittedPanelLayer[] Layers,
        float CornerSmooth = 0f,
        float PadRecess = 0f,
        float WellsRecess = 0f,
        float ShadowDepth = 0f,
        float RimBounce = 0f,
        float ShadowWrap = 0f,
        float PadRecessWidth = 4.5f,
        float PadRecessSmooth = 2.5f);

    private sealed record FittedPanelLayer(
        string Name,
        float[][] Contours,
        float[] Profile,
        float[] Flat,
        float[][] ZoneFlat,
        float[] Highlight,
        float[] Shadow,
        float[] Specular,
        float[] Crease,
        float[] Ridge,
        float[] Cast);
}
