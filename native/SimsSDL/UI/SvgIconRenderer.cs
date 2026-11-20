using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using SimsSDL.Core;

namespace SimsSDL.UI;

/// <summary>
/// Multi-layer W3C SVG icon &amp; 4-state button strip rasterizer for CPanel, Shared, and Nbhd UI icons.
/// Keeps VectorBannerRenderer (Neighborhood top bar) 100% untouched while rendering real .svg assets
/// under VectorUI/{CPanel,Shared,Nbhd}/ at crisp Retina resolution.
/// </summary>
public static class SvgIconRenderer
{
    public enum IconTheme
    {
        StaticLiteral,
        SpeedControl,
        OptionsDisc,
        HelpTile,
        WheelDisc,
        Architectural,
        WindowBox,
        ModeMedallion,
        SubpanelTab,
        OptionsButton,
    }

    private sealed record SvgLayer(string Role, (byte R, byte G, byte B) Fill, IReadOnlyList<(float X, float Y)> PackedContours);
    private sealed record ParsedIconSvg(float ViewWidth, float ViewHeight, IReadOnlyList<SvgLayer> Layers);

    private static readonly ConcurrentDictionary<string, ParsedIconSvg> SvgCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, VectorBannerRenderer.RasterImage> StripCache = new(StringComparer.Ordinal);

    public static VectorBannerRenderer.RasterImage RenderSingle(
        string resourceStem,
        int w1x,
        int h1x,
        int scale = 4)
    {
        scale = Math.Clamp(scale, 1, 6);
        string key = $"single:{resourceStem}:{w1x}:{h1x}:{scale}";
        if (StripCache.TryGetValue(key, out var cached))
            return cached;

        var svg = LoadSvg(resourceStem);
        int pw = w1x * scale;
        int ph = h1x * scale;
        byte[] rgba = new byte[pw * ph * 4];

        foreach (var layer in svg.Layers)
        {
            float[] cov = RasterizeEvenOdd(layer.PackedContours, svg.ViewWidth, svg.ViewHeight, pw, ph);
            CompositeLayer(rgba, cov, layer.Fill.R, layer.Fill.G, layer.Fill.B, alphaMul: 1f);
        }

        var img = new VectorBannerRenderer.RasterImage(pw, ph, rgba, new CGSize(w1x, h1x));
        StripCache[key] = img;
        return img;
    }

    /// <summary>
    /// Renders the structural LiveGadget .svg (chassis, pod, cols, win_cut, slot_cuts, col_holes)
    /// extracted from the user's Illustrator shape using exact floating-point vector contour SDFs
    /// (zero binary-grid Edt2D staircasing) and calibrated 3D bevels/reflections/colors.
    /// </summary>
    public static VectorBannerRenderer.RasterImage RenderLiveGadgetSvg(
        string resourceStem,
        int w1x = 108,
        int h1x = 100,
        int scale = 8)
    {
        scale = Math.Clamp(scale, 1, 8);
        string key = $"livegadget_exact_vec:{resourceStem}:{w1x}:{h1x}:{scale}";
        if (StripCache.TryGetValue(key, out var cached))
            return cached;

        var svg = LoadSvg(resourceStem);
        int pw = w1x * scale;
        int ph = h1x * scale;
        int n = pw * ph;
        float inv = 1f / scale;

        float[]? covChassis = null, covPod = null, covCols = null, covWin = null, covSlots = null, covColHoles = null;
        IReadOnlyList<(float X, float Y)>? ptsChassis = null, ptsPod = null, ptsCols = null, ptsWin = null, ptsSlots = null, ptsColHoles = null;

        foreach (var layer in svg.Layers)
        {
            float[] cov = RasterizeEvenOdd(layer.PackedContours, svg.ViewWidth, svg.ViewHeight, pw, ph);
            switch (layer.Role)
            {
                case "chassis": covChassis = cov; ptsChassis = layer.PackedContours; break;
                case "pod": covPod = cov; ptsPod = layer.PackedContours; break;
                case "cols": covCols = cov; ptsCols = layer.PackedContours; break;
                case "win_cut": covWin = cov; ptsWin = layer.PackedContours; break;
                case "slot_cuts": covSlots = cov; ptsSlots = layer.PackedContours; break;
                case "col_holes": covColHoles = cov; ptsColHoles = layer.PackedContours; break;
            }
        }

        covChassis ??= new float[n];
        covPod ??= new float[n];
        covCols ??= new float[n];
        covWin ??= new float[n];
        covSlots ??= new float[n];
        covColHoles ??= new float[n];

        var (dOuterBuf, nxOutBuf, nyOutBuf) = ExactContourSdf1x(ptsChassis ?? [], covChassis, svg.ViewWidth, svg.ViewHeight, pw, ph, w1x, h1x, scale);
        var (dPodBuf, nxPodBuf, nyPodBuf) = ExactContourSdf1x(ptsPod ?? [], covPod, svg.ViewWidth, svg.ViewHeight, pw, ph, w1x, h1x, scale);
        var (dColsBuf, nxColBuf, nyColBuf) = ExactContourSdf1x(ptsCols ?? [], covCols, svg.ViewWidth, svg.ViewHeight, pw, ph, w1x, h1x, scale);
        var (dWinBuf, nxWinBuf, nyWinBuf) = ExactContourSdf1x(ptsWin ?? [], covWin, svg.ViewWidth, svg.ViewHeight, pw, ph, w1x, h1x, scale);
        var (dSlotsBuf, nxSlBuf, nySlBuf) = ExactContourSdf1x(ptsSlots ?? [], covSlots, svg.ViewWidth, svg.ViewHeight, pw, ph, w1x, h1x, scale);
        var (dColHolesBuf, nxChBuf, nyChBuf) = ExactContourSdf1x(ptsColHoles ?? [], covColHoles, svg.ViewWidth, svg.ViewHeight, pw, ph, w1x, h1x, scale);

        // Light 0.20px smoothing on normals ONLY so polyline vertex transitions have C2 specular continuity
        float nSig = 0.22f * scale;
        nxOutBuf = GaussianBlur(nxOutBuf, pw, ph, nSig); nyOutBuf = GaussianBlur(nyOutBuf, pw, ph, nSig);
        nxPodBuf = GaussianBlur(nxPodBuf, pw, ph, nSig); nyPodBuf = GaussianBlur(nyPodBuf, pw, ph, nSig);
        nxColBuf = GaussianBlur(nxColBuf, pw, ph, nSig); nyColBuf = GaussianBlur(nyColBuf, pw, ph, nSig);
        nxWinBuf = GaussianBlur(nxWinBuf, pw, ph, nSig); nyWinBuf = GaussianBlur(nyWinBuf, pw, ph, nSig);
        nxSlBuf = GaussianBlur(nxSlBuf, pw, ph, nSig); nySlBuf = GaussianBlur(nySlBuf, pw, ph, nSig);
        nxChBuf = GaussianBlur(nxChBuf, pw, ph, nSig); nyChBuf = GaussianBlur(nyChBuf, pw, ph, nSig);

        static float LerpF(float a, float b, float t) => a + (b - a) * t;
        static float Gauss(float d, float mu, float sig)
        {
            float z = (d - mu) / sig;
            return z is > -6f and < 6f ? MathF.Exp(-0.5f * z * z) : 0f;
        }

        byte[] rgba = new byte[n * 4];
        Parallel.For(0, ph, py =>
        {
            float y = (py + 0.5f) * inv;
            float absDy = MathF.Abs(y - 50.0f);
            // Crescent vertical shading: #575690 at mid (y=50), #4F4E84 around y=18/82, #616099 near y=2/98
            float crescDip = Gauss(absDy, 31f, 13f);
            float crescEnd = Math.Clamp((absDy - 41f) / 8f, 0f, 1f);
            float baseCrescR = 87f - 8f * crescDip + 10f * crescEnd;
            float baseCrescG = 86f - 8f * crescDip + 10f * crescEnd;
            float baseCrescB = 144f - 12f * crescDip + 10f * crescEnd;

            // Louvered grille band subtle tone inside speaker pod
            float slotBand = Math.Clamp(1f - MathF.Abs(absDy - 34f) / 16f, 0f, 1f) * Math.Clamp((absDy - 29f) / 3f, 0f, 1f);

            int rowOff = py * pw;
            for (int px = 0; px < pw; px++)
            {
                int idx = rowOff + px;
                float cutCov = MathF.Max(MathF.Max(covWin[idx], covSlots[idx]), covColHoles[idx]);
                float cov = covChassis[idx] * (1f - cutCov);
                if (cov <= 0.001f)
                    continue;

                float x = (px + 0.5f) * inv;
                float dOuter = dOuterBuf[idx];
                float dPod = dPodBuf[idx];
                float dCols = dColsBuf[idx];
                float dWin = dWinBuf[idx];
                float dSlots = dSlotsBuf[idx];
                float dColHoles = dColHolesBuf[idx];

                float nxOut = nxOutBuf[idx], nyOut = nyOutBuf[idx];
                float nxPod = nxPodBuf[idx], nyPod = nyPodBuf[idx];
                float nxCol = nxColBuf[idx], nyCol = nyColBuf[idx];
                float nxWin = nxWinBuf[idx], nyWin = nyWinBuf[idx];
                float nxSl = nxSlBuf[idx], nySl = nySlBuf[idx];
                float nxCh = nxChBuf[idx], nyCh = nyChBuf[idx];

                float inCols = covCols[idx];

                // Keep a smooth, continuous ~1.02px outer chassis rim + crease around the top/bottom pod apex
                float apexB = dOuter + 1.02f;
                float smH = Math.Clamp(0.5f + 0.5f * (apexB - dPod) / 0.36f, 0f, 1f);
                float dPodEff = LerpF(dPod, apexB, smH) + 0.36f * smH * (1f - smH);
                if (smH > 0.001f)
                {
                    float bx = LerpF(nxPod, nxOut, smH);
                    float by = LerpF(nyPod, nyOut, smH);
                    float bl = MathF.Sqrt(bx * bx + by * by);
                    if (bl > 1e-5f) { nxPod = bx / bl; nyPod = by / bl; }
                }

                float inPod = Math.Clamp(0.5f - dPodEff * scale * 0.85f, 0f, 1f) * covChassis[idx];
                float inChassisOnly = Math.Clamp(1f - inPod, 0f, 1f) * (1f - inCols);

                // --- ZONE 1: Chassis Base, Left Crescent, Outer Rim & Spandrel Valley ---
                // Smooth transition from left crescent (#575690) to dark frame channel (#373570)
                float rightChannel = Math.Clamp((x - 31.0f) / 11.0f, 0f, 1f);
                float r = LerpF(baseCrescR, 55f, rightChannel);
                float g = LerpF(baseCrescG, 53f, rightChannel);
                float b = LerpF(baseCrescB, 112f, rightChannel);

                // Continuous razor-sharp outer D-silhouette wire rim at dOuter = -0.44px (zero seam around 360 deg)
                float leftRimW = Math.Clamp((48f - x) / 26f, 0f, 1f);
                float bOuter = Gauss(dOuter, -0.44f, 0.20f) * inChassisOnly;
                float outHiR = LerpF(126f, 148f, leftRimW);
                float outHiG = LerpF(125f, 148f, leftRimW);
                float outHiB = LerpF(176f, 200f, leftRimW);
                r = LerpF(r, outHiR, bOuter * 0.95f);
                g = LerpF(g, outHiG, bOuter * 0.95f);
                b = LerpF(b, outHiB, bOuter * 0.95f);

                float outDarkEdge = Math.Clamp((dOuter + 0.18f) / 0.18f, 0f, 1f) * inChassisOnly;
                r = LerpF(r, 58f, outDarkEdge * 0.60f);
                g = LerpF(g, 55f, outDarkEdge * 0.60f);
                b = LerpF(b, 110f, outDarkEdge * 0.60f);

                // Deep recessed valley immediately outside Columns & right/apex side of Speaker Pod
                float podRightW = Math.Clamp((x - 29.5f) / 4.0f, 0f, 1f);
                float valleyCol = dCols > 0f ? Gauss(dCols, 0.34f, 0.34f) : 0f;
                float valleyPod = dPodEff > 0f ? Gauss(dPodEff, 0.32f, 0.30f) * podRightW : 0f;
                float valleyDark = MathF.Max(valleyCol, valleyPod) * inChassisOnly;
                r = LerpF(r, 48f, valleyDark * 0.86f);
                g = LerpF(g, 45f, valleyDark * 0.86f);
                b = LerpF(b, 102f, valleyDark * 0.86f);

                // Crisp top-left / bottom-left crescent separating crease where pod approaches outer arc
                float crescCrease = (dPodEff > 0f ? Gauss(dPodEff, 0.52f, 0.24f) : 0f) * inChassisOnly * Math.Clamp((absDy - 28f) / 10f, 0f, 1f) * (1f - rightChannel * 0.5f);
                r -= 16f * crescCrease; g -= 16f * crescCrease; b -= 11f * crescCrease;

                // Crisp diagonal specular crease in the top & bottom triangular spandrel pockets
                if (inChassisOnly > 0.01f && x > 34.0f && x < 42.5f && absDy > 30f && dCols > 0.45f && dOuter < -0.85f)
                {
                    float spPocket = Math.Clamp((dCols - 0.45f) / 0.65f, 0f, 1f)
                                   * Math.Clamp((-0.85f - dOuter) / 0.65f, 0f, 1f)
                                   * Math.Clamp((x - 34.0f) / 1.5f, 0f, 1f);
                    float spRidge = Gauss(dPodEff, 1.22f, 0.28f) * spPocket * inChassisOnly;
                    r += 46f * spRidge;
                    g += 46f * spRidge;
                    b += 50f * spRidge;
                }

                // --- ZONE 2: Two Subpanel Columns (Col 0 & Col 1) + 6 Recessed Button Steps ---
                if (inCols > 0.001f)
                {
                    float cr = 108f, cg = 106f, cb = 157f; // #6C6A9D base plateau

                    // Razor-sharp outer column chrome-lavender ridge (#C4C3EA on left/top, #AEADD8 on right/bottom)
                    float colDir = Math.Clamp(0.60f - 0.28f * nxCol - 0.20f * nyCol, 0.20f, 1f);
                    float bCol = Gauss(dCols, -0.44f, 0.17f);
                    cr = LerpF(cr, 166f + 30f * colDir, bCol * 0.97f);
                    cg = LerpF(cg, 165f + 30f * colDir, bCol * 0.97f);
                    cb = LerpF(cb, 208f + 28f * colDir, bCol * 0.97f);

                    float colOuterLip = Math.Clamp((dCols + 0.16f) / 0.16f, 0f, 1f);
                    cr = LerpF(cr, 52f, colOuterLip * 0.72f);
                    cg = LerpF(cg, 50f, colOuterLip * 0.72f);
                    cb = LerpF(cb, 106f, colOuterLip * 0.72f);

                    // 6 Button Cutout Recessed Steps (matching st1/st2 step at dColHoles ~ 0.66px + crisp inner lip at 0.22px)
                    if (dColHoles > 0f && dColHoles < 1.5f)
                    {
                        float stepGroove = Gauss(dColHoles, 0.66f, 0.18f);
                        cr -= 24f * stepGroove;
                        cg -= 24f * stepGroove;
                        cb -= 19f * stepGroove;

                        float lrWeight = MathF.Abs(nxCh);
                        float holeLip = Gauss(dColHoles, 0.22f, 0.13f);
                        cr += (9f + 16f * lrWeight) * holeLip;
                        cg += (9f + 16f * lrWeight) * holeLip;
                        cb += (18f + 16f * lrWeight) * holeLip;

                        float holeEdge = Math.Clamp((0.10f - dColHoles) / 0.10f, 0f, 1f);
                        cr = LerpF(cr, 52f, holeEdge * 0.60f);
                        cg = LerpF(cg, 51f, holeEdge * 0.60f);
                        cb = LerpF(cb, 114f, holeEdge * 0.60f);
                    }

                    r = LerpF(r, cr, inCols);
                    g = LerpF(g, cg, inCols);
                    b = LerpF(b, cb, inCols);
                }

                // --- ZONE 3: Speaker Pod 3D Dome + Raised Arch Rim + Cycle Window + 10 Speaker Slots ---
                if (inPod > 0.001f)
                {
                    // Smooth 3D dome gradient (#807FAF top-left -> #7D7BAC center -> #7675A6 bottom-right)
                    float domeShade = ((x - 22f) / 36f) * 0.6f + ((y - 50f) / 100f) * 0.4f;
                    float pr = 125f - 6f * domeShade - 5f * slotBand;
                    float pg = 123f - 6f * domeShade - 4f * slotBand;
                    float pb = 172f - 5f * domeShade - 3f * slotBand;

                    // Crisp Raised Speaker Pod Outer Rim (#C6C5EC on left arc, #9E9CCB on right vertical edge)
                    float podLeftFactor = Math.Clamp(0.52f - 0.48f * nxPod, 0f, 1f);
                    float bPod = Gauss(dPodEff, -0.40f, 0.20f);
                    pr = LerpF(pr, 158f + 42f * podLeftFactor, bPod * 0.97f);
                    pg = LerpF(pg, 156f + 43f * podLeftFactor, bPod * 0.97f);
                    pb = LerpF(pb, 203f + 35f * podLeftFactor, bPod * 0.97f);

                    float podOuterEdge = Math.Clamp((dPodEff + 0.15f) / 0.15f, 0f, 1f);
                    pr = LerpF(pr, 80f, podOuterEdge * 0.60f);
                    pg = LerpF(pg, 78f, podOuterEdge * 0.60f);
                    pb = LerpF(pb, 130f, podOuterEdge * 0.60f);

                    // Cycle Window Bevel: Crisp Bright #A8A9E0 on Left & Bottom, Dark #52518E on Top & Right
                    if (dWin > 0f && dWin < 1.5f)
                    {
                        float winLit = Math.Clamp(MathF.Max(0f, -nxWin) + MathF.Max(0f, nyWin) * 0.92f, 0f, 1f);
                        float winShd = Math.Clamp(MathF.Max(0f, nxWin) * 0.92f + MathF.Max(0f, -nyWin) * 0.82f, 0f, 1f);
                        float wLip = Gauss(dWin, 0.32f, 0.17f);
                        float wDark = Gauss(dWin, 0.22f, 0.16f);
                        pr += 44f * winLit * wLip - 34f * winShd * wDark;
                        pg += 45f * winLit * wLip - 33f * winShd * wDark;
                        pb += 52f * winLit * wLip - 24f * winShd * wDark;

                        float wEdge = Math.Clamp((0.12f - dWin) / 0.12f, 0f, 1f);
                        pr = LerpF(pr, 60f, wEdge * 0.68f);
                        pg = LerpF(pg, 61f, wEdge * 0.68f);
                        pb = LerpF(pb, 128f, wEdge * 0.68f);
                    }

                    // 10 Stamped Speaker Slots: Dark recessed top wall + Crisp bright bottom/cap lip + Distinct outer recessed groove ring
                    if (dSlots > 0f && dSlots < 1.35f)
                    {
                        float slTop = Math.Clamp(-nySl, 0f, 1f);
                        float slBotCap = Math.Clamp(nySl + 0.55f * MathF.Abs(nxSl), 0f, 1f);

                        float sInner = Gauss(dSlots, 0.21f, 0.13f);
                        pr += 34f * slBotCap * sInner - 42f * slTop * sInner;
                        pg += 34f * slBotCap * sInner - 42f * slTop * sInner;
                        pb += 40f * slBotCap * sInner - 30f * slTop * sInner;

                        float sOuterRing = Gauss(dSlots, 0.62f, 0.16f) * Math.Clamp(nySl + 0.65f * MathF.Abs(nxSl) + 0.15f, 0f, 1f);
                        float sOuterTop = Gauss(dSlots, 0.54f, 0.16f) * slTop;
                        pr += 10f * sOuterTop - 22f * sOuterRing;
                        pg += 10f * sOuterTop - 22f * sOuterRing;
                        pb += 12f * sOuterTop - 16f * sOuterRing;

                        float sEdge = Math.Clamp((0.10f - dSlots) / 0.10f, 0f, 1f);
                        pr = LerpF(pr, 46f, sEdge * 0.70f);
                        pg = LerpF(pg, 44f, sEdge * 0.70f);
                        pb = LerpF(pb, 110f, sEdge * 0.70f);
                    }

                    r = LerpF(r, pr, inPod);
                    g = LerpF(g, pg, inPod);
                    b = LerpF(b, pb, inPod);
                }

                int di = idx * 4;
                rgba[di] = ClampByte(r);
                rgba[di + 1] = ClampByte(g);
                rgba[di + 2] = ClampByte(b);
                rgba[di + 3] = ClampByte(cov * 255f);
            }
        });

        var outImg = new VectorBannerRenderer.RasterImage(pw, ph, rgba, new CGSize(w1x, h1x));
        StripCache[key] = outImg;
        return outImg;
    }

    /// <summary>
    /// Renders Greenbars.bmp (27x25 at LiveGadget offset x=[12..39], y=[3..28]) and
    /// Redbars.bmp (27x25 at LiveGadget offset x=[12..39], y=[73..98]) at 8x vector resolution,
    /// strictly masked to CPanel/livegadget.svg's `slot_cuts` contours with alpha=0 everywhere outside
    /// the speaker pills so zero 1x opaque grey background box ever overlaps the vector LiveGadget dome.
    /// </summary>
    public static VectorBannerRenderer.RasterImage RenderLiveGadgetMoodBarsSvg(
        string resourceStem,
        bool isGreen,
        int scale = 8)
    {
        scale = Math.Clamp(scale, 1, 8);
        string key = $"lg_moodbars_svg:{resourceStem}:{(isGreen ? 'G' : 'R')}:{scale}";
        if (StripCache.TryGetValue(key, out var cached))
            return cached;

        const int gadgetW1x = 108;
        const int gadgetH1x = 100;
        const int cropX1x = 12;
        int cropY1x = isGreen ? 3 : 72;
        const int cropW1x = 27;
        const int cropH1x = 26;

        int fullW = gadgetW1x * scale;
        int fullH = gadgetH1x * scale;
        var svg = LoadSvg(resourceStem);
        float[]? covSlotsFull = null;
        IReadOnlyList<(float X, float Y)>? ptsSlots = null;
        foreach (var layer in svg.Layers)
        {
            if (layer.Role == "slot_cuts")
            {
                covSlotsFull = RasterizeEvenOdd(layer.PackedContours, svg.ViewWidth, svg.ViewHeight, fullW, fullH);
                ptsSlots = layer.PackedContours;
                break;
            }
        }
        covSlotsFull ??= new float[fullW * fullH];
        var (dSlotsFull, _, nySlFull) = ExactContourSdf1x(
            ptsSlots ?? [],
            covSlotsFull,
            svg.ViewWidth,
            svg.ViewHeight,
            fullW,
            fullH,
            gadgetW1x,
            gadgetH1x,
            scale);

        int pw = cropW1x * scale;
        int ph = cropH1x * scale;
        int offX = cropX1x * scale;
        int offY = cropY1x * scale;
        float inv = 1f / scale;
        byte[] rgba = new byte[pw * ph * 4];
        static float LerpMood(float a, float b, float t) => a + (b - a) * t;

        Parallel.For(0, ph, py =>
        {
            int fy = offY + py;
            float yLg = (fy + 0.5f) * inv;
            for (int px = 0; px < pw; px++)
            {
                int fx = offX + px;
                int fIdx = fy * fullW + fx;
                float cov = covSlotsFull[fIdx];
                if (cov <= 0.001f)
                    continue;

                float dIn = MathF.Max(0f, -dSlotsFull[fIdx]);
                float ny = nySlFull[fIdx];

                float r, g, b;
                if (isGreen)
                {
                    float t = Math.Clamp((yLg - 5.2f) / 20.5f, 0f, 1f);
                    r = 0f;
                    g = LerpMood(232f, 102f, t);
                    b = LerpMood(50f, 10f, t);
                }
                else
                {
                    float t = Math.Clamp((yLg - 74.8f) / 20.2f, 0f, 1f);
                    r = LerpMood(84f, 232f, t);
                    g = LerpMood(3f, 0f, t);
                    b = LerpMood(3f, 0f, t);
                }

                float topRim = Math.Clamp(1f - dIn / 0.65f, 0f, 1f) * Math.Clamp(-ny, 0f, 1f);
                float coreGlow = Math.Clamp((dIn - 0.35f) / 0.85f, 0f, 1f);
                r = r * (1f - 0.32f * topRim) + (isGreen ? 14f : 18f) * coreGlow;
                g = g * (1f - 0.32f * topRim) + (isGreen ? 18f : 10f) * coreGlow;
                b = b * (1f - 0.32f * topRim) + (isGreen ? 14f : 10f) * coreGlow;

                int di = (py * pw + px) * 4;
                rgba[di] = ClampByte(r);
                rgba[di + 1] = ClampByte(g);
                rgba[di + 2] = ClampByte(b);
                rgba[di + 3] = ClampByte(cov * 255f);
            }
        });

        var outImg = new VectorBannerRenderer.RasterImage(pw, ph, rgba, new CGSize(cropW1x, cropH1x));
        StripCache[key] = outImg;
        return outImg;
    }

    private readonly record struct Seg1x(float X0, float Y0, float Dx, float Dy, float InvLen2, float MinX, float MaxX);

    private static (float[] Dist1x, float[] Nx, float[] Ny) ExactContourSdf1x(
        IReadOnlyList<(float X, float Y)> packedContours,
        float[] cov,
        float viewW,
        float viewH,
        int pw,
        int ph,
        int w1x,
        int h1x,
        int scale)
    {
        int n = pw * ph;
        float[] distBuf = new float[n];
        float[] nxBuf = new float[n];
        float[] nyBuf = new float[n];
        if (packedContours.Count == 0)
        {
            Array.Fill(distBuf, 99f);
            return (distBuf, nxBuf, nyBuf);
        }

        float sx = w1x / MathF.Max(1f, viewW);
        float sy = h1x / MathF.Max(1f, viewH);
        const float bandMargin = 4.2f;

        var allSegs = new List<(Seg1x Seg, float MinY, float MaxY)>(packedContours.Count);
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
                    float x0 = p0.X * sx, y0 = p0.Y * sy;
                    float x1 = p1.X * sx, y1 = p1.Y * sy;
                    float dx = x1 - x0, dy = y1 - y0;
                    float len2 = dx * dx + dy * dy;
                    if (len2 > 1e-8f)
                    {
                        allSegs.Add((
                            new Seg1x(x0, y0, dx, dy, 1f / len2, MathF.Min(x0, x1) - bandMargin, MathF.Max(x0, x1) + bandMargin),
                            MathF.Min(y0, y1) - bandMargin,
                            MathF.Max(y0, y1) + bandMargin));
                    }
                }
            }
            start = end + 1;
        }

        // Bucket segments by 1x integer row so each pixel only checks nearby segments within 4.2px!
        var buckets = new Seg1x[h1x][];
        for (int ry = 0; ry < h1x; ry++)
        {
            float yLo = ry, yHi = ry + 1f;
            var list = new List<Seg1x>();
            foreach (var item in allSegs)
            {
                if (item.MaxY >= yLo && item.MinY <= yHi)
                    list.Add(item.Seg);
            }
            buckets[ry] = list.ToArray();
        }

        float inv = 1f / scale;
        Parallel.For(0, ph, py =>
        {
            float y = (py + 0.5f) * inv;
            int ry = Math.Clamp((int)y, 0, h1x - 1);
            Seg1x[] segs = buckets[ry];
            int rowOff = py * pw;

            for (int px = 0; px < pw; px++)
            {
                int idx = rowOff + px;
                float sgn = cov[idx] >= 0.5f ? -1f : 1f;
                float x = (px + 0.5f) * inv;

                float bestD2 = bandMargin * bandMargin;
                float bestQx = x, bestQy = y;
                bool found = false;

                for (int s = 0; s < segs.Length; s++)
                {
                    ref readonly Seg1x seg = ref segs[s];
                    if (x < seg.MinX || x > seg.MaxX)
                        continue;
                    float wx = x - seg.X0;
                    float wy = y - seg.Y0;
                    float t = Math.Clamp((wx * seg.Dx + wy * seg.Dy) * seg.InvLen2, 0f, 1f);
                    float qx = seg.X0 + t * seg.Dx;
                    float qy = seg.Y0 + t * seg.Dy;
                    float ex = x - qx, ey = y - qy;
                    float d2 = ex * ex + ey * ey;
                    if (d2 < bestD2)
                    {
                        bestD2 = d2;
                        bestQx = qx;
                        bestQy = qy;
                        found = true;
                    }
                }

                if (!found)
                {
                    distBuf[idx] = sgn * bandMargin;
                    continue;
                }

                float d = MathF.Sqrt(bestD2);
                distBuf[idx] = sgn * d;
                if (d > 1e-4f)
                {
                    float invD = sgn / d;
                    nxBuf[idx] = (x - bestQx) * invD;
                    nyBuf[idx] = (y - bestQy) * invD;
                }
            }
        });

        return (distBuf, nxBuf, nyBuf);
    }

    private static float[] SignedEdt1x(float[] cov, int w, int h, int scale, bool negate = false)
    {
        const double Inf = 1e12;
        int n = w * h;
        double[] fOut = new double[n];
        double[] fIn = new double[n];
        for (int i = 0; i < n; i++)
        {
            bool inside = cov[i] >= 0.5f;
            fOut[i] = inside ? 0.0 : Inf;
            fIn[i] = inside ? Inf : 0.0;
        }

        double[] d2Out = Edt2D(fOut, w, h);
        double[] d2In = Edt2D(fIn, w, h);
        float inv = 1f / scale;
        float sgn = negate ? -1f : 1f;
        float[] res = new float[n];
        for (int i = 0; i < n; i++)
        {
            float c = cov[i];
            float dPx = c > 0.02f && c < 0.98f
                ? 0.5f - c
                : c >= 0.5f
                    ? -(float)(Math.Sqrt(d2In[i]) - 0.5)
                    : (float)(Math.Sqrt(d2Out[i]) - 0.5);
            res[i] = dPx * inv * sgn;
        }

        return res;
    }

    private static double[] Edt2D(double[] src, int w, int h)
    {
        double[] tmp = new double[w * h];
        double[] dst = new double[w * h];

        Parallel.For(0, h, y =>
        {
            int[] v = new int[w];
            double[] z = new double[w + 1];
            int row = y * w;
            int k = 0;
            v[0] = 0;
            z[0] = -1e20;
            z[1] = 1e20;
            for (int q = 1; q < w; q++)
            {
                double s = ((src[row + q] + (double)q * q) - (src[row + v[k]] + (double)v[k] * v[k])) / (2.0 * (q - v[k]));
                while (s <= z[k])
                {
                    k--;
                    s = ((src[row + q] + (double)q * q) - (src[row + v[k]] + (double)v[k] * v[k])) / (2.0 * (q - v[k]));
                }

                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = 1e20;
            }

            k = 0;
            for (int q = 0; q < w; q++)
            {
                while (z[k + 1] < q)
                    k++;
                double dx = q - v[k];
                tmp[row + q] = dx * dx + src[row + v[k]];
            }
        });

        Parallel.For(0, w, x =>
        {
            int[] v = new int[h];
            double[] z = new double[h + 1];
            int k = 0;
            v[0] = 0;
            z[0] = -1e20;
            z[1] = 1e20;
            for (int q = 1; q < h; q++)
            {
                double s = ((tmp[q * w + x] + (double)q * q) - (tmp[v[k] * w + x] + (double)v[k] * v[k])) / (2.0 * (q - v[k]));
                while (s <= z[k])
                {
                    k--;
                    s = ((tmp[q * w + x] + (double)q * q) - (tmp[v[k] * w + x] + (double)v[k] * v[k])) / (2.0 * (q - v[k]));
                }

                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = 1e20;
            }

            k = 0;
            for (int q = 0; q < h; q++)
            {
                while (z[k + 1] < q)
                    k++;
                double dy = q - v[k];
                dst[q * w + x] = dy * dy + tmp[v[k] * w + x];
            }
        });

        return dst;
    }

    public static VectorBannerRenderer.RasterImage RenderClippedLayeredSvg(
        string resourceStem,
        int w1x,
        int h1x,
        int scale = 6,
        float toneFeatherPx1x = 0f)
    {
        scale = Math.Clamp(scale, 1, 8);
        string key = $"clipped:{resourceStem}:{w1x}:{h1x}:{scale}:{toneFeatherPx1x:F2}";
        if (StripCache.TryGetValue(key, out var cached))
            return cached;

        var svg = LoadSvg(resourceStem);
        int pw = w1x * scale;
        int ph = h1x * scale;
        int n = pw * ph;

        float[]? clipCov = null, covCols = null, covPodCuts = null, covColHoles = null;
        float[] rBuf = new float[n];
        float[] gBuf = new float[n];
        float[] bBuf = new float[n];
        bool initBase = false;

        foreach (var layer in svg.Layers)
        {
            float[] cov = RasterizeEvenOdd(layer.PackedContours, svg.ViewWidth, svg.ViewHeight, pw, ph);
            if (layer.Role == "clip")
            {
                clipCov = cov;
                if (!initBase)
                {
                    Array.Fill(rBuf, layer.Fill.R);
                    Array.Fill(gBuf, layer.Fill.G);
                    Array.Fill(bBuf, layer.Fill.B);
                    initBase = true;
                }
                continue;
            }
            if (layer.Role == "cols") { covCols = cov; continue; }
            if (layer.Role == "pod_cuts") { covPodCuts = cov; continue; }
            if (layer.Role == "col_holes") { covColHoles = cov; continue; }

            if (!initBase)
            {
                Array.Fill(rBuf, layer.Fill.R);
                Array.Fill(gBuf, layer.Fill.G);
                Array.Fill(bBuf, layer.Fill.B);
                initBase = true;
            }

            for (int i = 0; i < n; i++)
            {
                float a = cov[i];
                if (a <= 0.001f)
                    continue;
                rBuf[i] += (layer.Fill.R - rBuf[i]) * a;
                gBuf[i] += (layer.Fill.G - gBuf[i]) * a;
                bBuf[i] += (layer.Fill.B - bBuf[i]) * a;
            }
        }

        if (toneFeatherPx1x > 0.01f)
        {
            float sig = toneFeatherPx1x * scale;
            rBuf = GaussianBlur(rBuf, pw, ph, sig);
            gBuf = GaussianBlur(gBuf, pw, ph, sig);
            bBuf = GaussianBlur(bBuf, pw, ph, sig);
        }

        byte[] rgba = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            float a = clipCov != null ? clipCov[i] : 1f;
            if (a <= 0.001f)
                continue;
            int idx = i * 4;
            rgba[idx] = ClampByte(rBuf[i]);
            rgba[idx + 1] = ClampByte(gBuf[i]);
            rgba[idx + 2] = ClampByte(bBuf[i]);
            rgba[idx + 3] = ClampByte(a * 255f);
        }

        var img = new VectorBannerRenderer.RasterImage(pw, ph, rgba, new CGSize(w1x, h1x));
        StripCache[key] = img;
        return img;
    }

    public static VectorBannerRenderer.RasterImage RenderMultiRowStateStrip(
        IReadOnlyList<string> rowResourceStems,
        IconTheme theme,
        int w1x,
        int h1x,
        int scale = 4)
    {
        scale = Math.Clamp(scale, 1, 6);
        int rowCount = Math.Max(1, rowResourceStems.Count);
        string key = $"multirow:{string.Join(',', rowResourceStems)}:{theme}:{w1x}:{h1x}:{scale}";
        if (StripCache.TryGetValue(key, out var cached))
            return cached;

        int stripW = w1x * scale * 4;
        int rowH = h1x * scale;
        int stripH = rowH * rowCount;
        byte[] strip = new byte[stripW * stripH * 4];
        int rowBytes = stripW * rowH * 4;

        for (int r = 0; r < rowCount; r++)
        {
            var singleRow = RenderStateStrip(rowResourceStems[r], theme, w1x, h1x, rows: 1, scale: scale);
            Buffer.BlockCopy(singleRow.Rgba, 0, strip, r * rowBytes, rowBytes);
        }

        var img = new VectorBannerRenderer.RasterImage(stripW, stripH, strip, new CGSize(w1x * 4, h1x * rowCount));
        StripCache[key] = img;
        return img;
    }

    public static VectorBannerRenderer.RasterImage RenderStateStrip(
        string resourceStem,
        IconTheme theme,
        int w1x,
        int h1x,
        int rows = 1,
        int scale = 4,
        float? fitInset1x = null)
    {
        scale = Math.Clamp(scale, 1, 6);
        rows = Math.Max(1, rows);
        string fitTag = fitInset1x.HasValue ? fitInset1x.Value.ToString("F2", CultureInfo.InvariantCulture) : "raw";
        string key = $"strip:{resourceStem}:{theme}:{w1x}:{h1x}:{rows}:{scale}:{fitTag}";
        if (StripCache.TryGetValue(key, out var cached))
            return cached;

        var svg = LoadSvg(resourceStem);
        int fw = w1x * scale;
        int fh = h1x * scale;
        int stripW = fw * 4;
        int stripH = fh * rows;
        byte[] strip = new byte[stripW * stripH * 4];

        IReadOnlyList<SvgLayer> layersToRasterize = fitInset1x.HasValue
            ? FitLayersUniform(svg.Layers, w1x * 10f, h1x * 10f, fitInset1x.Value * 10f)
            : svg.Layers;
        float effVw = fitInset1x.HasValue ? w1x * 10f : svg.ViewWidth;
        float effVh = fitInset1x.HasValue ? h1x * 10f : svg.ViewHeight;

        float[][] masks = new float[layersToRasterize.Count][];
        for (int i = 0; i < layersToRasterize.Count; i++)
            masks[i] = RasterizeEvenOdd(layersToRasterize[i].PackedContours, effVw, effVh, fw, fh);

        // Combine any painter's-order 'fg' + 'fg-cut' sequences into a single clean foreground mask
        var effectiveLayers = new List<(string Role, (byte R, byte G, byte B) Fill, float[] Mask)>();
        float[]? accumFg = null;
        (byte R, byte G, byte B) accumFill = (220, 228, 236);
        for (int i = 0; i < layersToRasterize.Count; i++)
        {
            var layer = layersToRasterize[i];
            if (layer.Role == "fg")
            {
                if (accumFg is null)
                {
                    accumFg = (float[])masks[i].Clone();
                    accumFill = layer.Fill;
                }
                else
                {
                    for (int p = 0; p < accumFg.Length; p++)
                        accumFg[p] = accumFg[p] + masks[i][p] * (1f - accumFg[p]);
                }
            }
            else if (layer.Role == "fg-cut")
            {
                if (accumFg is not null)
                {
                    for (int p = 0; p < accumFg.Length; p++)
                        accumFg[p] *= 1f - masks[i][p];
                }
            }
            else
            {
                if (accumFg is not null)
                {
                    effectiveLayers.Add(("fg", accumFill, accumFg));
                    accumFg = null;
                }
                effectiveLayers.Add((layer.Role, layer.Fill, masks[i]));
            }
        }
        if (accumFg is not null)
            effectiveLayers.Add(("fg", accumFill, accumFg));

        byte[] cell = new byte[fw * fh * 4];
        for (int row = 0; row < rows; row++)
        {
            for (int state = 0; state < 4; state++)
            {
                Array.Clear(cell);
                if (theme == IconTheme.ModeMedallion)
                    DrawModeMedallionBase(cell, fw, fh, w1x, h1x, scale, state);
                else if (theme == IconTheme.SubpanelTab)
                    DrawSubpanelTabBase(cell, fw, fh, w1x, h1x, scale, state);

                for (int l = 0; l < effectiveLayers.Count; l++)
                {
                    var layer = effectiveLayers[l];
                    if (layer.Role == "shadow" && theme is IconTheme.WheelDisc or IconTheme.OptionsDisc or IconTheme.Architectural or IconTheme.HelpTile or IconTheme.ModeMedallion or IconTheme.SubpanelTab or IconTheme.OptionsButton)
                        continue;

                    var (r, g, b, aMul) = ResolveRoleColor(theme, layer.Role, layer.Fill, state, row);
                    if (aMul <= 0.001f)
                        continue;

                    if (layer.Role is "fg" or "ring" && theme is IconTheme.WheelDisc or IconTheme.OptionsDisc or IconTheme.Architectural or IconTheme.HelpTile or IconTheme.ModeMedallion or IconTheme.SubpanelTab or IconTheme.OptionsButton)
                    {
                        var (shR, shG, shB, shOp) = ResolveCastShadowColor(theme, state);
                        CompositePhotoshopLayerStyle(cell, layer.Mask, fw, fh, scale, r, g, b, aMul, shR, shG, shB, shOp, flatFill: state == 3);
                    }
                    else if (layer.Role is "fg" or "ring" && theme != IconTheme.SpeedControl && state != 3)
                    {
                        CompositeBeveledForeground(cell, layer.Mask, fw, fh, scale, r, g, b, aMul);
                    }
                    else
                    {
                        CompositeLayer(cell, layer.Mask, r, g, b, aMul);
                    }
                }

                if (theme == IconTheme.ModeMedallion)
                    DrawModeMedallionRingOver(cell, fw, fh, w1x, h1x, scale, state);
                else if (theme == IconTheme.SubpanelTab)
                    DrawSubpanelTabBorderOver(cell, fw, fh, w1x, h1x, scale, state);

                for (int py = 0; py < fh; py++)
                {
                    Buffer.BlockCopy(
                        cell,
                        py * fw * 4,
                        strip,
                        ((row * fh + py) * stripW + state * fw) * 4,
                        fw * 4);
                }
            }
        }

        var img = new VectorBannerRenderer.RasterImage(stripW, stripH, strip, new CGSize(w1x * 4, h1x * rows));
        StripCache[key] = img;
        return img;
    }

    private static (byte R, byte G, byte B, float AlphaMul) ResolveRoleColor(
        IconTheme theme,
        string role,
        (byte R, byte G, byte B) literal,
        int state,
        int row)
    {
        switch (theme)
        {
            case IconTheme.SpeedControl:
                if (role == "shadow")
                    return state == 3 ? ((byte)0, (byte)0, (byte)0, 0f) : ((byte)0, (byte)17, (byte)86, 0.96f);
                if (row == 1 && state == 1)
                    return (255, 0, 0, 1f);
                return state switch
                {
                    1 => (0, 255, 255, 1f),
                    2 => (255, 255, 255, 1f),
                    3 => (64, 83, 162, 1f),
                    _ => (189, 193, 210, 1f),
                };

            case IconTheme.OptionsDisc:
                return role switch
                {
                    "shadow" => (0, 16, 112, 0.78f),
                    "well" => (0, 16, 144, 1f),
                    "ring" => state switch
                    {
                        1 or 2 => (0, 248, 248, 1f),
                        3 => (96, 104, 184, 1f),
                        _ => (224, 224, 224, 1f),
                    },
                    _ => state switch
                    {
                        1 => (0, 248, 248, 1f),
                        2 => (248, 248, 248, 1f),
                        3 => (96, 104, 184, 1f),
                        _ => (224, 224, 224, 1f),
                    },
                };

            case IconTheme.HelpTile:
                return role switch
                {
                    "tile" => (0, 64, 140, 1f),
                    "shadow" => (0, 44, 92, 0.95f),
                    _ => state switch
                    {
                        1 => (0, 248, 248, 1f),
                        2 => (248, 248, 248, 1f),
                        3 => (96, 136, 184, 1f),
                        _ => (184, 200, 214, 1f),
                    },
                };

            case IconTheme.WheelDisc:
                return role switch
                {
                    "shadow" => state == 3 ? ((byte)32, (byte)104, (byte)152, 0.75f) : ((byte)0, (byte)32, (byte)56, 0.90f),
                    "moat" => (24, 92, 148, 1f),
                    _ => state switch
                    {
                        1 => (0, 248, 248, 1f),
                        2 => (248, 248, 248, 1f),
                        3 => (64, 128, 168, 1f),
                        _ => (202, 212, 220, 1f),
                    },
                };

            case IconTheme.Architectural:
                return role switch
                {
                    "shadow" => state == 3 ? ((byte)16, (byte)80, (byte)152, 0.72f) : ((byte)0, (byte)24, (byte)56, 0.88f),
                    "moat" => (0, 64, 144, 1f),
                    "fg-shade" => state switch
                    {
                        1 => (0, 208, 224, 1f),
                        2 => (184, 208, 224, 1f),
                        3 => (48, 104, 168, 1f),
                        _ => (148, 172, 204, 1f),
                    },
                    _ => state switch
                    {
                        1 => (0, 248, 248, 1f),
                        2 => (248, 248, 248, 1f),
                        3 => (64, 120, 176, 1f),
                        _ => (202, 212, 222, 1f),
                    },
                };

            case IconTheme.WindowBox:
                return role switch
                {
                    "well" => (0, 0, 80, 1f),
                    "shadow" => (0, 0, 40, 1f),
                    "moat" => (24, 24, 112, 1f),
                    _ => state switch
                    {
                        1 => (0, 248, 248, 1f),
                        2 => (248, 248, 248, 1f),
                        3 => (88, 88, 136, 1f),
                        _ => (188, 192, 212, 1f),
                    },
                };

            case IconTheme.ModeMedallion:
                return state switch
                {
                    1 => (0, 252, 255, 1f),
                    2 => (252, 254, 255, 1f),
                    3 => (96, 107, 190, 1f),
                    _ => (230, 232, 240, 1f),
                };

            case IconTheme.SubpanelTab:
                return state switch
                {
                    1 => (0, 252, 255, 1f),
                    2 => (252, 254, 255, 1f),
                    3 => (108, 110, 156, 1f),
                    _ => (204, 210, 218, 1f),
                };

            case IconTheme.OptionsButton:
                return state switch
                {
                    1 => (0, 252, 255, 1f),
                    2 => (252, 254, 255, 1f),
                    3 => (148, 148, 182, 1f),
                    _ => (204, 210, 216, 1f),
                };

            default:
                return (literal.R, literal.G, literal.B, 1f);
        }
    }

    private static (byte R, byte G, byte B, float Opacity) ResolveCastShadowColor(IconTheme theme, int state)
    {
        if (state == 3)
            return theme == IconTheme.OptionsDisc ? ((byte)0, (byte)16, (byte)112, 0.50f) : ((byte)0, (byte)0, (byte)0, 0f);
        return theme switch
        {
            IconTheme.WheelDisc => (0, 20, 46, 0.85f),
            IconTheme.OptionsDisc => (0, 8, 56, 0.80f),
            IconTheme.HelpTile => (0, 24, 62, 0.88f),
            IconTheme.ModeMedallion => (0, 10, 62, 0.80f),
            IconTheme.SubpanelTab => (0, 4, 34, 0.82f),
            IconTheme.OptionsButton => (0, 8, 52, 0.85f),
            _ => (0, 18, 52, 0.82f),
        };
    }

    private static (float Cx, float Cy, float ROut, float HalfT) GetModeMedallionGeometry(int w1x, int h1x)
    {
        if (w1x >= 48)
            return (26.0f, 24.0f, 22.0f, 1.55f); // People.bmp (50x50)
        if (w1x >= 43)
            return (23.0f, h1x >= 49 ? 24.0f : 22.0f, 20.0f, 1.55f); // Objects.bmp (45x47)
        if (w1x >= 35)
            return (20.0f, 18.0f, 17.0f, 1.40f); // Arch.bmp (39x39)
        return (14.5f, 14.0f, 12.5f, 1.20f); // Camera.bmp (29x29)
    }

    private static void DrawModeMedallionBase(byte[] dst, int fw, int fh, int w1x, int h1x, int scale, int state)
    {
        var (cx, cy, rOut, halfT) = GetModeMedallionGeometry(w1x, h1x);
        float rWell = rOut - halfT * 1.48f;
        float inv = 1f / scale;

        (byte wR, byte wG, byte wB) = state switch
        {
            1 or 2 => ((byte)0, (byte)22, (byte)176),
            3 => ((byte)28, (byte)36, (byte)128),
            _ => ((byte)0, (byte)17, (byte)150),
        };

        for (int py = 0; py < fh; py++)
        {
            float y = (py + 0.5f) * inv;
            for (int px = 0; px < fw; px++)
            {
                float x = (px + 0.5f) * inv;
                // Soft outer drop shadow of medallion
                float ds = MathF.Sqrt((x - (cx + 0.7f)) * (x - (cx + 0.7f)) + (y - (cy + 0.8f)) * (y - (cy + 0.8f)));
                float shCov = Math.Clamp((rOut + 0.5f - ds) * scale, 0f, 1f) * (state == 3 ? 0.35f : 0.72f);

                float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                float wellCov = Math.Clamp((rWell + 0.4f - d) * scale, 0f, 1f);

                float outA = wellCov + shCov * (1f - wellCov);
                if (outA <= 0.001f)
                    continue;
                int idx = (py * fw + px) * 4;
                dst[idx] = ClampByte((wR * wellCov + 2f * shCov * (1f - wellCov)) / outA);
                dst[idx + 1] = ClampByte((wG * wellCov + 6f * shCov * (1f - wellCov)) / outA);
                dst[idx + 2] = ClampByte((wB * wellCov + 44f * shCov * (1f - wellCov)) / outA);
                dst[idx + 3] = ClampByte(outA * 255f);
            }
        }
    }

    private static void DrawModeMedallionRingOver(byte[] dst, int fw, int fh, int w1x, int h1x, int scale, int state)
    {
        var (cx, cy, rOut, halfT) = GetModeMedallionGeometry(w1x, h1x);
        float rMid = rOut - halfT;
        float inv = 1f / scale;

        // Note: In State 2 (Active Mode), Maxis keeps the outer ring CYAN while the inner pictogram turns WHITE!
        (float hiR, float hiG, float hiB, float loR, float loG, float loB) = state switch
        {
            1 or 2 => (120f, 255f, 255f, 0f, 196f, 212f),
            3 => (122f, 130f, 198f, 82f, 92f, 168f),
            _ => (246f, 248f, 255f, 178f, 184f, 204f),
        };

        for (int py = 0; py < fh; py++)
        {
            float y = (py + 0.5f) * inv;
            for (int px = 0; px < fw; px++)
            {
                float x = (px + 0.5f) * inv;
                float dx = x - cx;
                float dy = y - cy;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float ringDist = MathF.Abs(d - rMid) - halfT;
                float cov = Math.Clamp(0.5f - ringDist * scale, 0f, 1f);
                if (cov <= 0.001f)
                    continue;

                float t = Math.Clamp(((dx + dy) / (2f * rOut)) * 0.5f + 0.5f, 0f, 1f);
                float rr = hiR + (loR - hiR) * t;
                float rg = hiG + (loG - hiG) * t;
                float rb = hiB + (loB - hiB) * t;

                int idx = (py * fw + px) * 4;
                float dr = dst[idx], dg = dst[idx + 1], db = dst[idx + 2], da = dst[idx + 3] / 255f;
                float outA = cov + da * (1f - cov);
                dst[idx] = ClampByte((rr * cov + dr * da * (1f - cov)) / outA);
                dst[idx + 1] = ClampByte((rg * cov + dg * da * (1f - cov)) / outA);
                dst[idx + 2] = ClampByte((rb * cov + db * da * (1f - cov)) / outA);
                dst[idx + 3] = ClampByte(outA * 255f);
            }
        }
    }

    private static float SdSubpanelTabRoundRect(float x, float y, int w1x, int h1x, float r = 1.95f)
    {
        float hx = w1x * 0.5f - 0.52f - r;
        float hy = h1x * 0.5f - 0.52f - r;
        float qx = MathF.Abs(x - w1x * 0.5f) - hx;
        float qy = MathF.Abs(y - h1x * 0.5f) - hy;
        float outD = MathF.Sqrt(MathF.Max(qx, 0f) * MathF.Max(qx, 0f) + MathF.Max(qy, 0f) * MathF.Max(qy, 0f));
        return outD + MathF.Min(MathF.Max(qx, qy), 0f) - r;
    }

    private static void DrawSubpanelTabBase(byte[] dst, int fw, int fh, int w1x, int h1x, int scale, int state)
    {
        float inv = 1f / scale;
        (byte wR, byte wG, byte wB) = state == 3 ? ((byte)22, (byte)24, (byte)76) : ((byte)6, (byte)8, (byte)64);
        for (int py = 0; py < fh; py++)
        {
            float y = (py + 0.5f) * inv;
            int rowOff = py * fw;
            for (int px = 0; px < fw; px++)
            {
                float x = (px + 0.5f) * inv;
                float d = SdSubpanelTabRoundRect(x, y, w1x, h1x);
                float a = Math.Clamp(0.5f - d * scale, 0f, 1f);
                if (a <= 0.001f)
                    continue;
                int idx = (rowOff + px) * 4;
                dst[idx] = wR;
                dst[idx + 1] = wG;
                dst[idx + 2] = wB;
                dst[idx + 3] = ClampByte(a * 255f);
            }
        }
    }

    private static void DrawSubpanelTabBorderOver(byte[] dst, int fw, int fh, int w1x, int h1x, int scale, int state)
    {
        float inv = 1f / scale;
        (float bR, float bG, float bB) = state switch
        {
            1 => (0f, 248f, 255f),
            2 => (248f, 250f, 255f),
            3 => (124f, 126f, 168f),
            _ => (188f, 194f, 208f),
        };

        for (int py = 0; py < fh; py++)
        {
            float y = (py + 0.5f) * inv;
            int rowOff = py * fw;
            for (int px = 0; px < fw; px++)
            {
                float x = (px + 0.5f) * inv;
                float d = SdSubpanelTabRoundRect(x, y, w1x, h1x);
                float outerA = Math.Clamp(0.5f - d * scale, 0f, 1f);
                if (outerA <= 0.001f)
                    continue;
                float dIn = -d;
                float ringCov = Math.Clamp((1.05f - dIn) * scale, 0f, 1f);
                if (ringCov <= 0.001f)
                    continue;

                float outerBlend = Math.Clamp((0.40f - dIn) * scale, 0f, 1f) * 0.38f;
                float rr = bR * (1f - outerBlend) + 108f * outerBlend;
                float rg = bG * (1f - outerBlend) + 108f * outerBlend;
                float rb = bB * (1f - outerBlend) + 152f * outerBlend;

                int idx = (rowOff + px) * 4;
                dst[idx] = ClampByte(dst[idx] * (1f - ringCov) + rr * ringCov);
                dst[idx + 1] = ClampByte(dst[idx + 1] * (1f - ringCov) + rg * ringCov);
                dst[idx + 2] = ClampByte(dst[idx + 2] * (1f - ringCov) + rb * ringCov);
                dst[idx + 3] = ClampByte(outerA * 255f);
            }
        }
    }

    private static void CompositePhotoshopLayerStyle(
        byte[] dstRgba,
        float[] mask,
        int w,
        int h,
        int scale,
        byte sr,
        byte sg,
        byte sb,
        float alphaMul,
        byte shR,
        byte shG,
        byte shB,
        float shOpacity,
        bool flatFill = false)
    {
        if (shOpacity > 0.002f)
        {
            // 1. Cast Photoshop 5.5 Drop Shadow from foreground stencil into ALL empty spaces (including interior +, moat, windows!)
            float[] dropped = ShiftField(mask, w, h, 0.92f * scale, 1.05f * scale);
            float[] shBlur = GaussianBlur(dropped, w, h, 0.42f * scale);
            for (int i = 0; i < mask.Length; i++)
            {
                float sa = shBlur[i] * shOpacity * (1f - mask[i] * 0.92f);
                if (sa <= 0.002f)
                    continue;
                int idx = i * 4;
                float dr = dstRgba[idx];
                float dg = dstRgba[idx + 1];
                float db = dstRgba[idx + 2];
                float da = dstRgba[idx + 3] / 255f;
                float outA = sa + da * (1f - sa);
                if (outA <= 1e-5f)
                    continue;
                dstRgba[idx] = ClampByte((shR * sa + dr * da * (1f - sa)) / outA);
                dstRgba[idx + 1] = ClampByte((shG * sa + dg * da * (1f - sa)) / outA);
                dstRgba[idx + 2] = ClampByte((shB * sa + db * da * (1f - sa)) / outA);
                dstRgba[idx + 3] = ClampByte(outA * 255f);
            }
        }

        if (flatFill)
        {
            CompositeLayer(dstRgba, mask, sr, sg, sb, alphaMul);
            return;
        }

        // 2. Smooth Gaussian-softened Inner Bevel on foreground stencil (top-left highlight, bottom-right bevel shade)
        float bevShift = 0.38f * scale;
        float[] shiftBr = ShiftField(mask, w, h, bevShift, bevShift);
        float[] shiftTl = ShiftField(mask, w, h, -bevShift, -bevShift);
        float[] rawHi = new float[w * h];
        float[] rawLo = new float[w * h];
        for (int i = 0; i < rawHi.Length; i++)
        {
            rawHi[i] = Math.Clamp(mask[i] - shiftBr[i], 0f, 1f);
            rawLo[i] = Math.Clamp(mask[i] - shiftTl[i], 0f, 1f);
        }
        float[] hiTl = GaussianBlur(rawHi, w, h, 0.32f * scale);
        float[] loBr = GaussianBlur(rawLo, w, h, 0.32f * scale);

        for (int i = 0; i < mask.Length; i++)
        {
            float m = mask[i];
            float sa = m * alphaMul;
            if (sa <= 0.001f)
                continue;

            float fr = Math.Clamp(sr + hiTl[i] * (255f - sr) * 0.72f - loBr[i] * 38f, 0f, 255f);
            float fg = Math.Clamp(sg + hiTl[i] * (255f - sg) * 0.72f - loBr[i] * 34f, 0f, 255f);
            float fb = Math.Clamp(sb + hiTl[i] * (255f - sb) * 0.72f - loBr[i] * 26f, 0f, 255f);

            int idx = i * 4;
            float dr = dstRgba[idx];
            float dg = dstRgba[idx + 1];
            float db = dstRgba[idx + 2];
            float da = dstRgba[idx + 3] / 255f;
            float outA = sa + da * (1f - sa);
            if (outA <= 1e-5f)
                continue;
            dstRgba[idx] = ClampByte((fr * sa + dr * da * (1f - sa)) / outA);
            dstRgba[idx + 1] = ClampByte((fg * sa + dg * da * (1f - sa)) / outA);
            dstRgba[idx + 2] = ClampByte((fb * sa + db * da * (1f - sa)) / outA);
            dstRgba[idx + 3] = ClampByte(outA * 255f);
        }
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
                float v00 = (uint)x0 < (uint)w && (uint)y0 < (uint)h ? src[y0 * w + x0] : 0f;
                float v10 = (uint)(x0 + 1) < (uint)w && (uint)y0 < (uint)h ? src[y0 * w + x0 + 1] : 0f;
                float v01 = (uint)x0 < (uint)w && (uint)(y0 + 1) < (uint)h ? src[(y0 + 1) * w + x0] : 0f;
                float v11 = (uint)(x0 + 1) < (uint)w && (uint)(y0 + 1) < (uint)h ? src[(y0 + 1) * w + x0 + 1] : 0f;
                dst[y * w + x] = (v00 + (v10 - v00) * fx) * (1f - fy) + (v01 + (v11 - v01) * fx) * fy;
            }
        }
        return dst;
    }

    private static float[] GaussianBlur(float[] src, int w, int h, float sigma)
    {
        if (sigma <= 0.05f)
            return (float[])src.Clone();
        int radius = Math.Clamp((int)MathF.Ceiling(sigma * 3f), 1, 16);
        float[] kernel = new float[radius * 2 + 1];
        float sum = 0f;
        float inv2 = 1f / (2f * sigma * sigma);
        for (int i = -radius; i <= radius; i++)
        {
            float wt = MathF.Exp(-(i * i) * inv2);
            kernel[i + radius] = wt;
            sum += wt;
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

    private static void CompositeBeveledForeground(
        byte[] dstRgba,
        float[] cov,
        int w,
        int h,
        int scale,
        byte sr,
        byte sg,
        byte sb,
        float alphaMul)
    {
        int shift = Math.Max(1, (int)MathF.Round(0.32f * scale));
        for (int py = 0; py < h; py++)
        {
            int rowOff = py * w;
            int ym = Math.Max(0, py - shift) * w;
            int yp = Math.Min(h - 1, py + shift) * w;
            for (int px = 0; px < w; px++)
            {
                int i = rowOff + px;
                float m = cov[i];
                float sa = m * alphaMul;
                if (sa <= 0.001f)
                    continue;

                int xm = Math.Max(0, px - shift);
                int xp = Math.Min(w - 1, px + shift);
                // Top-left specular rim (where top-left neighbor drops to 0) & bottom-right bevel shade (where bottom-right neighbor drops to 0)
                float hi = Math.Clamp(m - cov[ym + xm], 0f, 1f);
                float lo = Math.Clamp(m - cov[yp + xp], 0f, 1f);

                float fr = Math.Clamp(sr + hi * (255f - sr) * 0.55f - lo * 34f, 0f, 255f);
                float fg = Math.Clamp(sg + hi * (255f - sg) * 0.55f - lo * 32f, 0f, 255f);
                float fb = Math.Clamp(sb + hi * (255f - sb) * 0.55f - lo * 24f, 0f, 255f);

                int idx = i * 4;
                float dr = dstRgba[idx];
                float dg = dstRgba[idx + 1];
                float db = dstRgba[idx + 2];
                float da = dstRgba[idx + 3] / 255f;

                float outA = sa + da * (1f - sa);
                if (outA <= 1e-5f)
                    continue;

                dstRgba[idx] = ClampByte((fr * sa + dr * da * (1f - sa)) / outA);
                dstRgba[idx + 1] = ClampByte((fg * sa + dg * da * (1f - sa)) / outA);
                dstRgba[idx + 2] = ClampByte((fb * sa + db * da * (1f - sa)) / outA);
                dstRgba[idx + 3] = ClampByte(outA * 255f);
            }
        }
    }

    private static void CompositeLayer(byte[] dstRgba, float[] cov, byte sr, byte sg, byte sb, float alphaMul)
    {
        int n = cov.Length;
        for (int i = 0; i < n; i++)
        {
            float sa = cov[i] * alphaMul;
            if (sa <= 0.001f)
                continue;
            int idx = i * 4;
            float dr = dstRgba[idx];
            float dg = dstRgba[idx + 1];
            float db = dstRgba[idx + 2];
            float da = dstRgba[idx + 3] / 255f;

            float outA = sa + da * (1f - sa);
            if (outA <= 1e-5f)
                continue;

            dstRgba[idx] = ClampByte((sr * sa + dr * da * (1f - sa)) / outA);
            dstRgba[idx + 1] = ClampByte((sg * sa + dg * da * (1f - sa)) / outA);
            dstRgba[idx + 2] = ClampByte((sb * sa + db * da * (1f - sa)) / outA);
            dstRgba[idx + 3] = ClampByte(outA * 255f);
        }
    }

    private static ParsedIconSvg LoadSvg(string resourceStem)
    {
        if (SvgCache.TryGetValue(resourceStem, out var cached))
            return cached;

        string suffix = $".VectorUI.{resourceStem}.svg";
        string? resName = null;
        foreach (string name in typeof(SvgIconRenderer).Assembly.GetManifestResourceNames())
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                resName = name;
                break;
            }
        }

        if (resName is null)
            throw new ContentError($"Missing embedded SVG icon resource '{resourceStem}.svg'");

        using Stream stream = typeof(SvgIconRenderer).Assembly.GetManifestResourceStream(resName)
            ?? throw new ContentError($"Could not open embedded SVG icon resource '{resName}'");

        var doc = XDocument.Load(stream);
        var root = doc.Root ?? throw new ContentError($"Empty SVG document '{resourceStem}.svg'");

        float vw = 100f, vh = 100f;
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

        var layers = new List<SvgLayer>();
        foreach (var pathEl in root.Descendants().Where(static e => e.Name.LocalName == "path"))
        {
            string? d = pathEl.Attribute("d")?.Value;
            if (string.IsNullOrWhiteSpace(d))
                continue;
            string role = pathEl.Attribute("data-role")?.Value
                ?? (string.Equals(pathEl.Attribute("data-op")?.Value, "cut", StringComparison.OrdinalIgnoreCase) ? "fg-cut" : "fg");
            var fill = ParseHexColor(pathEl.Attribute("fill")?.Value);
            var contours = ParseSvgPathContours(d);
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
                layers.Add(new SvgLayer(role, fill, packed));
        }

        var parsed = new ParsedIconSvg(vw, vh, layers);
        SvgCache[resourceStem] = parsed;
        return parsed;
    }

    private static (byte R, byte G, byte B) ParseHexColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return (206, 214, 228);
        ReadOnlySpan<char> s = hex.AsSpan().Trim();
        if (s.StartsWith("#"))
            s = s[1..];
        if (s.Length == 6
            && byte.TryParse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
            && byte.TryParse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
            && byte.TryParse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            return (r, g, b);
        }
        return (206, 214, 228);
    }

    private static IReadOnlyList<SvgLayer> FitLayersUniform(
        IReadOnlyList<SvgLayer> layers,
        float targetVw,
        float targetVh,
        float pad)
    {
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
        foreach (var layer in layers)
        {
            if (layer.Role is not ("fg" or "ring"))
                continue;
            foreach (var pt in layer.PackedContours)
            {
                if (float.IsNaN(pt.X))
                    continue;
                if (pt.X < minX) minX = pt.X;
                if (pt.Y < minY) minY = pt.Y;
                if (pt.X > maxX) maxX = pt.X;
                if (pt.Y > maxY) maxY = pt.Y;
            }
        }

        if (!float.IsFinite(minX) || maxX <= minX || maxY <= minY)
            return layers;

        float availW = MathF.Max(1f, targetVw - 2f * pad);
        float availH = MathF.Max(1f, targetVh - 2f * pad);
        float s = MathF.Min(availW / (maxX - minX), availH / (maxY - minY));
        float tx = targetVw * 0.5f - (minX + maxX) * 0.5f * s;
        float ty = targetVh * 0.5f - (minY + maxY) * 0.5f * s;

        var fitted = new List<SvgLayer>(layers.Count);
        foreach (var layer in layers)
        {
            var pts = new List<(float X, float Y)>(layer.PackedContours.Count);
            foreach (var pt in layer.PackedContours)
                pts.Add(float.IsNaN(pt.X) ? pt : (pt.X * s + tx, pt.Y * s + ty));
            fitted.Add(new SvgLayer(layer.Role, layer.Fill, pts));
        }
        return fitted;
    }

    private static float[] RasterizeEvenOdd(
        IReadOnlyList<(float X, float Y)> packedContours,
        float viewW,
        float viewH,
        int pw,
        int ph)
    {
        float sx = pw / MathF.Max(1f, viewW);
        float sy = ph / MathF.Max(1f, viewH);

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
                i++;
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
                break;
        }

        return start == i ? 0f : float.Parse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static byte ClampByte(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
}
