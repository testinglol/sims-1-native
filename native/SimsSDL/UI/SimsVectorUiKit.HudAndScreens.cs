using SimsSDL.Core;

namespace SimsSDL.UI;

public static partial class SimsVectorUiKit
{
    /// <summary>
    /// Recreates PeopleTemplate.bmp (180x45 = 4 states of 45x45, 7.4/255 MAE against original):
    /// Frame 0 = Normal periwinkle [166,166,195], Frame 1 = Selected #00FFFF, Frame 2 = Hover #FFFFFF, Frame 3 = Disabled [89,89,142].
    /// </summary>
    public static VectorBannerRenderer.RasterImage RenderPeopleTemplateStrip(int size1x = 45, int scale = 4)
    {
        scale = Math.Clamp(scale, 1, 6);
        string key = $"people_tpl:{size1x}:{scale}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        int fw = size1x * scale;
        int fh = size1x * scale;
        int stripW = fw * 4;
        byte[] strip = new byte[stripW * fh * 4];
        float inv = 1f / scale;
        float c = size1x * 0.5f;
        float u = size1x / 45f;

        for (int state = 0; state < 4; state++)
        {
            (float R, float G, float B) rc = state switch
            {
                1 => (0f, 255f, 255f),
                2 => (255f, 255f, 255f),
                3 => (89f, 89f, 142f),
                _ => (166f, 166f, 195f),
            };

            int stateCapture = state;
            Parallel.For(0, fh, py =>
            {
                float y = (py + 0.5f) * inv;
                for (int px = 0; px < fw; px++)
                {
                    float x = (px + 0.5f) * inv;
                    float dOut = SdRoundedBox4(x, y, c, c, 22.1f * u, 22.1f * u, 6.2f * u, 6.2f * u, 6.2f * u, 6.2f * u);
                    float outCov = Math.Clamp(0.5f - dOut * scale, 0f, 1f);
                    if (outCov <= 0.001f)
                        continue;

                    float dIn = SdRoundedBox4(x, y, c, c, 20.6f * u, 20.6f * u, 4.8f * u, 4.8f * u, 4.8f * u, 4.8f * u);
                    float inCov = Math.Clamp(0.5f - dIn * scale, 0f, 1f);
                    float ringCov = outCov * (1f - inCov);
                    float totalCov = Math.Clamp(inCov + ringCov, 0f, 1f);
                    if (totalCov <= 0.001f)
                        continue;

                    float dMid = MathF.Abs((dOut + dIn) * 0.5f);
                    float ridge = Math.Clamp(1f - dMid / (0.85f * u), 0f, 1f);
                    float shade = 0.62f + 0.38f * ridge;

                    // Fill the interior with the classic portrait dark-navy (#000052) so 35x40 BMP#2002
                    // portraits sit seamlessly inside the 45x45 rounded frame with no side gaps.
                    float bgR = 0f, bgG = 0f, bgB = 82f;
                    float ringR = rc.R * shade;
                    float ringG = rc.G * shade;
                    float ringB = rc.B * shade;
                    float r = (bgR * inCov + ringR * ringCov) / totalCov;
                    float g = (bgG * inCov + ringG * ringCov) / totalCov;
                    float b = (bgB * inCov + ringB * ringCov) / totalCov;

                    int dst = (py * stripW + stateCapture * fw + px) * 4;
                    strip[dst] = Byte(r);
                    strip[dst + 1] = Byte(g);
                    strip[dst + 2] = Byte(b);
                    strip[dst + 3] = Byte(totalCov * 255f);
                }
            });
        }

        var img = new VectorBannerRenderer.RasterImage(stripW, fh, strip, new CGSize(size1x * 4, size1x));
        Cache[key] = img;
        return img;
    }

    /// <summary>
    /// Recreates cpanel/Catalog/ThumbTemplate.BMP (180x45 = 4 states of 45x45) at HiDPI resolution:
    /// State 0 = Normal muted steel-blue [107,165,189] with navy rim,
    /// State 1/3 = Selected bright ice-blue [156,198,214] with #00FFFF cyan rim,
    /// State 2 = Hover bright ice-blue [156,198,214] with navy rim.
    /// </summary>
    public static VectorBannerRenderer.RasterImage RenderThumbTemplateStrip(int size1x = 45, int scale = 4)
    {
        scale = Math.Clamp(scale, 1, 6);
        string key = $"thumb_tpl:{size1x}:{scale}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        int fw = size1x * scale;
        int fh = size1x * scale;
        int stripW = fw * 4;
        byte[] strip = new byte[stripW * fh * 4];
        float inv = 1f / scale;
        float c = size1x * 0.5f;
        float u = size1x / 45f;

        for (int state = 0; state < 4; state++)
        {
            bool selected = state is 1 or 3;
            bool bright = state is 1 or 2 or 3;
            (float bgR, float bgG, float bgB) = bright ? (156f, 198f, 214f) : (107f, 165f, 189f);
            (float rimR, float rimG, float rimB) = selected ? (0f, 255f, 255f) : (36f, 52f, 118f);
            (float edgeR, float edgeG, float edgeB) = selected ? (8f, 74f, 132f) : (8f, 16f, 90f);

            int stateCapture = state;
            Parallel.For(0, fh, py =>
            {
                float y = (py + 0.5f) * inv;
                for (int px = 0; px < fw; px++)
                {
                    float x = (px + 0.5f) * inv;
                    float dOut = SdRoundedBox4(x, y, c, c, 22.0f * u, 22.0f * u, 5.5f * u, 5.5f * u, 5.5f * u, 5.5f * u);
                    float outCov = Math.Clamp(0.5f - dOut * scale, 0f, 1f);
                    if (outCov <= 0.001f)
                        continue;

                    float dIn = -dOut; // distance inside from outer edge (0 at outer edge, >0 inside)
                    float r, g, b;
                    if (selected)
                    {
                        float rimT = SmoothStep(0.3f * u, 1.0f * u, dIn) * (1f - SmoothStep(1.6f * u, 2.7f * u, dIn));
                        float innerT = SmoothStep(1.4f * u, 2.8f * u, dIn);
                        r = Lerp(edgeR, bgR, innerT);
                        g = Lerp(edgeG, bgG, innerT);
                        b = Lerp(edgeB, bgB, innerT);
                        r = Lerp(r, rimR, rimT);
                        g = Lerp(g, rimG, rimT);
                        b = Lerp(b, rimB, rimT);
                    }
                    else
                    {
                        float innerT = SmoothStep(0.4f * u, 3.0f * u, dIn);
                        float midT = SmoothStep(0.2f * u, 1.2f * u, dIn) * (1f - innerT);
                        r = Lerp(edgeR, bgR, innerT) * (1f - midT * 0.35f) + rimR * (midT * 0.35f);
                        g = Lerp(edgeG, bgG, innerT) * (1f - midT * 0.35f) + rimG * (midT * 0.35f);
                        b = Lerp(edgeB, bgB, innerT) * (1f - midT * 0.35f) + rimB * (midT * 0.35f);
                    }

                    int dst = (py * stripW + stateCapture * fw + px) * 4;
                    strip[dst] = Byte(r);
                    strip[dst + 1] = Byte(g);
                    strip[dst + 2] = Byte(b);
                    strip[dst + 3] = Byte(outCov * 255f);
                }
            });
        }

        var img = new VectorBannerRenderer.RasterImage(stripW, fh, strip, new CGSize(size1x * 4, size1x));
        Cache[key] = img;
        return img;
    }

    /// <summary>
    /// Recreates PanelBack.bmp (3.47/255 MAE against original) at ANY widescreen width (zero stretched 1px columns).
    /// </summary>
    public static VectorBannerRenderer.RasterImage RenderLivePanelBack(int w1x, int h1x = 100, int scale = 4)
    {
        w1x = Math.Max(64, w1x);
        scale = Math.Clamp(scale, 1, 4);
        string key = $"panelback:{w1x}:{h1x}:{scale}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        int pw = w1x * scale;
        int ph = h1x * scale;
        byte[] rgba = new byte[pw * ph * 4];
        float inv = 1f / scale;
        float hw = w1x * 0.5f;
        float hh = h1x * 0.5f;

        Parallel.For(0, ph, py =>
        {
            float y = (py + 0.5f) * inv;
            float topSh = Math.Clamp(1f - y / 1.4f, 0f, 1f) * 0.55f;
            float botSh = Math.Clamp(1f - (h1x - y) / 2.6f, 0f, 1f) * 0.28f;
            float ySh = MathF.Max(topSh, botSh);
            int rowOff = py * pw;

            for (int px = 0; px < pw; px++)
            {
                float x = (px + 0.5f) * inv;
                float dBox = SdRoundedBox4(x, y, hw, hh, hw - 0.2f, hh - 0.2f, 6.5f, 2f, 2f, 6.5f);
                float cov = Math.Clamp(0.5f - dBox * scale, 0f, 1f);
                if (cov <= 0.001f)
                    continue;

                float tx = MathF.Pow(Math.Clamp((x - 6f) / 248f, 0f, 1f), 0.92f);
                float r = 126f * (1f - tx);
                float g = 126f * (1f - tx);
                float b = 166f * (1f - tx) + 82f * tx;

                float rSh = Math.Clamp(1f - (w1x - x) / 3f, 0f, 1f) * 0.42f;
                float sh = MathF.Max(ySh, rSh);
                r *= 1f - sh;
                g *= 1f - sh;
                b = Lerp(b, 46f, sh);

                int idx = (rowOff + px) * 4;
                rgba[idx] = Byte(r);
                rgba[idx + 1] = Byte(g);
                rgba[idx + 2] = Byte(b);
                rgba[idx + 3] = Byte(cov * 255f);
            }
        });

        var img = new VectorBannerRenderer.RasterImage(pw, ph, rgba, new CGSize(w1x, h1x));
        Cache[key] = img;
        return img;
    }

    /// <summary>
    /// Renders LiveGadget.bmp (108x100) from the real multi-layer Cubic-Bezier .svg asset
    /// (native/SimsSDL/VectorUI/CPanel/livegadget.svg) traced directly from the AI-upscaled reference.
    /// </summary>
    public static VectorBannerRenderer.RasterImage RenderLiveGadget(int scale = 8) =>
        SvgIconRenderer.RenderLiveGadgetSvg("CPanel.livegadget", 108, 100, scale);

    public static VectorBannerRenderer.RasterImage RenderLiveGadgetMoodBars(bool isGreen, int scale = 8) =>
        SvgIconRenderer.RenderLiveGadgetMoodBarsSvg("CPanel.livegadget", isGreen, scale);

    /// <summary>
    /// Renders Pause.bmp, Speed1.bmp, Speed2.bmp, and Speed3.bmp from real embedded .svg files in VectorUI/CPanel/.
    /// </summary>
    public static VectorBannerRenderer.RasterImage RenderSpeedControlStrip(int speed, int scale = 4) =>
        speed switch
        {
            0 => SvgIconRenderer.RenderStateStrip("CPanel.pause", SvgIconRenderer.IconTheme.SpeedControl, 15, 15, rows: 2, scale: scale),
            1 => SvgIconRenderer.RenderStateStrip("CPanel.speed1", SvgIconRenderer.IconTheme.SpeedControl, 11, 15, rows: 1, scale: scale),
            3 => SvgIconRenderer.RenderStateStrip("CPanel.speed2", SvgIconRenderer.IconTheme.SpeedControl, 20, 15, rows: 1, scale: scale),
            _ => SvgIconRenderer.RenderStateStrip("CPanel.speed3", SvgIconRenderer.IconTheme.SpeedControl, 32, 15, rows: 1, scale: scale),
        };

    /// <summary>
    /// Renders Left-Wheel controls from real embedded .svg files in VectorUI/CPanel/.
    /// Returns null for Live/Buy/Build (Wave 1 pictograms).
    /// </summary>
    public static VectorBannerRenderer.RasterImage? TryRenderUniversalGeometricControlStrip(
        OriginalUniversalControl.Action kind,
        int w1x,
        int h1x,
        int scale = 4) =>
        kind switch
        {
            OriginalUniversalControl.Action.Live =>
                SvgIconRenderer.RenderStateStrip("CPanel.people", SvgIconRenderer.IconTheme.ModeMedallion, w1x, h1x, scale: scale),
            OriginalUniversalControl.Action.Buy =>
                SvgIconRenderer.RenderStateStrip("CPanel.objects", SvgIconRenderer.IconTheme.ModeMedallion, w1x, h1x, scale: scale),
            OriginalUniversalControl.Action.Build =>
                SvgIconRenderer.RenderStateStrip("CPanel.arch", SvgIconRenderer.IconTheme.ModeMedallion, w1x, h1x, scale: scale),
            OriginalUniversalControl.Action.Options =>
                SvgIconRenderer.RenderStateStrip("CPanel.options", SvgIconRenderer.IconTheme.OptionsDisc, w1x, h1x, scale: scale),
            OriginalUniversalControl.Action.Help =>
                SvgIconRenderer.RenderStateStrip("CPanel.helpbutton", SvgIconRenderer.IconTheme.HelpTile, w1x, h1x, scale: scale),
            OriginalUniversalControl.Action.ZoomIn =>
                SvgIconRenderer.RenderStateStrip("CPanel.zoomin", SvgIconRenderer.IconTheme.WheelDisc, w1x, h1x, scale: scale),
            OriginalUniversalControl.Action.ZoomOut =>
                SvgIconRenderer.RenderStateStrip("CPanel.zoomout", SvgIconRenderer.IconTheme.WheelDisc, w1x, h1x, scale: scale),
            OriginalUniversalControl.Action.CameraLeft =>
                SvgIconRenderer.RenderStateStrip("CPanel.rotleft", SvgIconRenderer.IconTheme.WheelDisc, w1x, h1x, scale: scale),
            OriginalUniversalControl.Action.CameraRight =>
                SvgIconRenderer.RenderStateStrip("CPanel.rotright", SvgIconRenderer.IconTheme.WheelDisc, w1x, h1x, scale: scale),
            OriginalUniversalControl.Action.Story1 =>
                SvgIconRenderer.RenderStateStrip("CPanel.lev1", SvgIconRenderer.IconTheme.Architectural, w1x, h1x, scale: scale),
            OriginalUniversalControl.Action.Story2 =>
                SvgIconRenderer.RenderStateStrip("CPanel.lev2", SvgIconRenderer.IconTheme.Architectural, w1x, h1x, scale: scale),
            OriginalUniversalControl.Action.Roofs =>
                SvgIconRenderer.RenderStateStrip("CPanel.levroof", SvgIconRenderer.IconTheme.Architectural, w1x, h1x, scale: scale),
            OriginalUniversalControl.Action.WallsUp =>
                SvgIconRenderer.RenderStateStrip("CPanel.nocut", SvgIconRenderer.IconTheme.Architectural, w1x, h1x, scale: scale),
            OriginalUniversalControl.Action.WallsCutaway =>
                SvgIconRenderer.RenderStateStrip("CPanel.dynacut", SvgIconRenderer.IconTheme.Architectural, w1x, h1x, scale: scale),
            OriginalUniversalControl.Action.WallsDown =>
                SvgIconRenderer.RenderStateStrip("CPanel.nowall", SvgIconRenderer.IconTheme.Architectural, w1x, h1x, scale: scale),
            _ => null,
        };

    /// <summary>
    /// Renders MinimizeBox.bmp / CloseBox.bmp (48x11 = 4 states of 12x11) from real embedded .svg files in VectorUI/Shared/.
    /// </summary>
    public static VectorBannerRenderer.RasterImage RenderWindowSmallBoxStrip(bool isCloseX, int scale = 4) =>
        SvgIconRenderer.RenderStateStrip(
            isCloseX ? "Shared.closebox" : "Shared.minimizebox",
            SvgIconRenderer.IconTheme.WindowBox,
            12,
            11,
            scale: scale);

    /// <summary>
    /// Renders all 7 Live Mode subpanel tab strips from real Inkvec .svg files.
    /// </summary>
    public static VectorBannerRenderer.RasterImage? TryRenderSubpanelTabStrip(string assetStem, int w1x = 27, int h1x = 30, int scale = 4) =>
        assetStem.ToLowerInvariant() switch
        {
            "mood" => SvgIconRenderer.RenderStateStrip("CPanel.mood", SvgIconRenderer.IconTheme.SubpanelTab, w1x, h1x, scale: scale),
            "job" => SvgIconRenderer.RenderStateStrip("CPanel.job", SvgIconRenderer.IconTheme.SubpanelTab, w1x, h1x, scale: scale),
            "interest" => SvgIconRenderer.RenderStateStrip("CPanel.interest", SvgIconRenderer.IconTheme.SubpanelTab, w1x, h1x, scale: scale),
            "gift" => SvgIconRenderer.RenderStateStrip("CPanel.gift", SvgIconRenderer.IconTheme.SubpanelTab, w1x, h1x, scale: scale),
            "house" => SvgIconRenderer.RenderStateStrip("CPanel.house", SvgIconRenderer.IconTheme.SubpanelTab, w1x, h1x, scale: scale),
            "relationship" => SvgIconRenderer.RenderStateStrip("CPanel.relationship", SvgIconRenderer.IconTheme.SubpanelTab, w1x, h1x, scale: scale),
            "personality" => SvgIconRenderer.RenderStateStrip("CPanel.personality", SvgIconRenderer.IconTheme.SubpanelTab, w1x, h1x, scale: scale),
            _ => null,
        };

    /// <summary>
    /// Renders Options Mode main subpanel buttons (OptNbhd, OptSave, OptGraphics, OptSound, OptPlay) from real Inkvec .svg files.
    /// </summary>
    public static VectorBannerRenderer.RasterImage? TryRenderOptionsButtonStrip(
        OriginalOptionsPanelOverlay.Button button,
        int w1x,
        int h1x,
        int scale = 4) =>
        button switch
        {
            OriginalOptionsPanelOverlay.Button.Neighborhood =>
                SvgIconRenderer.RenderStateStrip("CPanel.optnbhd", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            OriginalOptionsPanelOverlay.Button.Save =>
                SvgIconRenderer.RenderStateStrip("CPanel.optsave", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            OriginalOptionsPanelOverlay.Button.Graphics =>
                SvgIconRenderer.RenderStateStrip("CPanel.optgraphics", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            OriginalOptionsPanelOverlay.Button.Sound =>
                SvgIconRenderer.RenderStateStrip("CPanel.optsound", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            OriginalOptionsPanelOverlay.Button.Play =>
                SvgIconRenderer.RenderStateStrip("CPanel.optplay", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            _ => null,
        };

    private static readonly string[] CareerRowStems =
    [
        "CPanel.Career.job_icon_01",
        "CPanel.Career.job_icon_02",
        "CPanel.Career.job_icon_03",
        "CPanel.Career.job_icon_04",
        "CPanel.Career.job_icon_05",
        "CPanel.Career.job_icon_06",
        "CPanel.Career.job_icon_07",
        "CPanel.Career.job_icon_08",
        "CPanel.Career.job_icon_09",
        "CPanel.Career.job_icon_10",
        "CPanel.Career.job_icon_11",
        "CPanel.Career.job_icon_12",
        "CPanel.Career.job_icon_13",
        "CPanel.Career.job_icon_14",
        "CPanel.Career.job_icon_15",
        "CPanel.Career.job_icon_16",
        "CPanel.Career.job_icon_17",
        "CPanel.Career.job_icon_18",
        "CPanel.Career.job_icon_19",
        "CPanel.Career.job_icon_20",
        "CPanel.Career.job_icon_21",
        "CPanel.Career.job_icon_22",
    ];

    public static VectorBannerRenderer.RasterImage RenderJobIconMultiButton(int scale = 4) =>
        SvgIconRenderer.RenderMultiRowStateStrip(CareerRowStems, SvgIconRenderer.IconTheme.OptionsButton, 28, 22, scale);

    public static VectorBannerRenderer.RasterImage RenderFameIconMultiButton(int scale = 4) =>
        SvgIconRenderer.RenderStateStrip("CPanel.Career.fame_star", SvgIconRenderer.IconTheme.OptionsButton, 28, 22, scale: scale);

    public static VectorBannerRenderer.RasterImage RenderSalaryButtonStrip(int scale = 4) =>
        SvgIconRenderer.RenderStateStrip("Nbhd.pick_simoleon", SvgIconRenderer.IconTheme.OptionsButton, 13, 11, scale: scale, fitInset1x: 0.8f);

    public static VectorBannerRenderer.RasterImage RenderFriendSmileyStrip(int w1x, int h1x, int scale = 4) =>
        SvgIconRenderer.RenderStateStrip("Nbhd.pick_smiley", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale, fitInset1x: 0.8f);

    public static VectorBannerRenderer.RasterImage RenderLcdGreenSeasonIcon(int seasonIndex, int size1x = 10, int scale = 4)
    {
        int idx = ((seasonIndex % 4) + 4) % 4;
        scale = Math.Clamp(scale, 1, 6);
        string key = $"lcd_season_v1:{idx}:{size1x}:{scale}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        int fw = size1x * scale;
        int fh = size1x * scale;
        byte[] rgba = new byte[fw * fh * 4];
        float aa = 1.4f / fw;

        static float SdSeg(float px, float py, float ax, float ay, float bx, float by)
        {
            float pax = px - ax, pay = py - ay;
            float bax = bx - ax, bay = by - ay;
            float h = Math.Clamp((pax * bax + pay * bay) / MathF.Max(1e-6f, bax * bax + bay * bay), 0f, 1f);
            float dx = pax - bax * h, dy = pay - bay * h;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        static float SdSun(float u, float v)
        {
            float r = MathF.Sqrt(u * u + v * v);
            float d = r - 0.42f;
            for (int k = 0; k < 8; k++)
            {
                float a = k * MathF.PI / 4f;
                float ca = MathF.Cos(a), sa = MathF.Sin(a);
                d = MathF.Min(d, SdSeg(u, v, ca * 0.58f, sa * 0.58f, ca * 0.88f, sa * 0.88f) - 0.085f);
            }
            return d;
        }

        static float SdBlossom(float u, float v)
        {
            float d = 1e6f;
            for (int k = 0; k < 5; k++)
            {
                float a = k * (2f * MathF.PI / 5f) - MathF.PI / 2f;
                float px = MathF.Cos(a) * 0.42f, py = MathF.Sin(a) * 0.42f;
                d = MathF.Min(d, MathF.Sqrt((u - px) * (u - px) + (v - py) * (v - py)) - 0.34f);
            }
            float hole = 0.21f - MathF.Sqrt(u * u + v * v);
            return MathF.Max(d, hole);
        }

        static float SdLeaf(float u, float v)
        {
            float ru = (u + v) * 0.7071f;
            float rv = (-u + v) * 0.7071f;
            float body = MathF.Max(
                MathF.Sqrt((ru - 0.38f) * (ru - 0.38f) + rv * rv) - 0.84f,
                MathF.Sqrt((ru + 0.38f) * (ru + 0.38f) + rv * rv) - 0.84f);
            float stem = SdSeg(u, v, -0.78f, 0.78f, -0.30f, 0.30f) - 0.085f;
            float vein = 0.055f - SdSeg(u, v, -0.32f, 0.32f, 0.36f, -0.36f);
            return MathF.Min(MathF.Max(body, vein), stem);
        }

        static float SdSnowflake(float u, float v)
        {
            float d = 1e6f;
            for (int k = 0; k < 3; k++)
            {
                float a = k * (MathF.PI / 3f);
                float ca = MathF.Cos(a), sa = MathF.Sin(a);
                d = MathF.Min(d, SdSeg(u, v, -ca * 0.86f, -sa * 0.86f, ca * 0.86f, sa * 0.86f) - 0.085f);
                for (int s = -1; s <= 1; s += 2)
                {
                    float bx = ca * 0.52f * s, by = sa * 0.52f * s;
                    float a1 = a + 0.72f, a2 = a - 0.72f;
                    d = MathF.Min(d, SdSeg(u, v, bx, by, bx + MathF.Cos(a1) * 0.28f * s, by + MathF.Sin(a1) * 0.28f * s) - 0.065f);
                    d = MathF.Min(d, SdSeg(u, v, bx, by, bx + MathF.Cos(a2) * 0.28f * s, by + MathF.Sin(a2) * 0.28f * s) - 0.065f);
                }
            }
            return d;
        }

        for (int py = 0; py < fh; py++)
        {
            float v = ((py + 0.5f) / fh) * 2f - 1f;
            for (int px = 0; px < fw; px++)
            {
                float u = ((px + 0.5f) / fw) * 2f - 1f;
                float sd = idx switch
                {
                    0 => SdBlossom(u, v),
                    1 => SdSun(u, v),
                    2 => SdLeaf(u, v),
                    _ => SdSnowflake(u, v),
                };
                float fill = SmoothStep(aa, -aa, sd);
                if (fill <= 0.001f) continue;
                int o = (py * fw + px) * 4;
                rgba[o] = 0x29;
                rgba[o + 1] = 0xE6;
                rgba[o + 2] = 0x5E;
                rgba[o + 3] = (byte)Math.Clamp((int)MathF.Round(fill * 255f), 0, 255);
            }
        }

        var result = new VectorBannerRenderer.RasterImage(fw, fh, rgba, new CGSize(size1x, size1x));
        Cache[key] = result;
        return result;
    }

    public static VectorBannerRenderer.RasterImage RenderLcdGreenSmiley(int size1x = 10, int scale = 4)
    {
        scale = Math.Clamp(scale, 1, 6);
        string key = $"lcd_smiley_v2:{size1x}:{scale}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        int fw = size1x * scale;
        int fh = size1x * scale;
        byte[] rgba = new byte[fw * fh * 4];
        float aa = 1.4f / fw;

        for (int py = 0; py < fh; py++)
        {
            float v = ((py + 0.5f) / fh) * 2f - 1f;
            for (int px = 0; px < fw; px++)
            {
                float u = ((px + 0.5f) / fw) * 2f - 1f;
                // Matches the exact 10x10 UniversalBack.tga LCD smiley geometry:
                // full flat circular disc, bold rounded-square eyes, wide semicircular open grin, no drop shadow.
                float dDisc = MathF.Sqrt(u * u + v * v) - 0.94f;
                float ex = MathF.Abs(u) - 0.40f;
                float ey = v + 0.34f;
                float dEyes = SdRoundedBox4(ex, ey, 0f, 0f, 0.17f, 0.20f, 0.05f, 0.05f, 0.05f, 0.05f);
                float mv = v - 0.12f;
                float dMouth = MathF.Max(-mv, MathF.Sqrt(u * u + mv * mv) - 0.66f);
                float dFace = MathF.Max(dDisc, -MathF.Min(dEyes, dMouth));

                float cov = Math.Clamp(0.5f - dFace / aa, 0f, 1f);
                if (cov <= 0.004f) continue;
                int dst = (py * fw + px) * 4;
                rgba[dst] = 0x29;
                rgba[dst + 1] = 0xe6;
                rgba[dst + 2] = 0x5e;
                rgba[dst + 3] = (byte)Math.Clamp((int)MathF.Round(cov * 255f), 0, 255);
            }
        }

        var img = new VectorBannerRenderer.RasterImage(fw, fh, rgba, new CGSize(size1x, size1x));
        Cache[key] = img;
        return img;
    }

    public static VectorBannerRenderer.RasterImage RenderRelationshipFilterStrip(int index, int scale = 4) =>
        index switch
        {
            0 => SvgIconRenderer.RenderStateStrip("NghUI.movein", SvgIconRenderer.IconTheme.SubpanelTab, 20, 20, scale: scale, fitInset1x: 3.1f),
            1 => SvgIconRenderer.RenderStateStrip("Nbhd.pick_smiley", SvgIconRenderer.IconTheme.SubpanelTab, 20, 20, scale: scale, fitInset1x: 3.4f),
            2 => SvgIconRenderer.RenderStateStrip("CPanel.Career.fame_star", SvgIconRenderer.IconTheme.SubpanelTab, 20, 20, scale: scale, fitInset1x: 3.3f),
            _ => SvgIconRenderer.RenderStateStrip("CPanel.relallsort_inf", SvgIconRenderer.IconTheme.SubpanelTab, 20, 20, scale: scale),
        };

    public static VectorBannerRenderer.RasterImage RenderInventorySortStrip(int index, int scale = 4) =>
        index switch
        {
            0 => SvgIconRenderer.RenderStateStrip("Nbhd.pick_magichat", SvgIconRenderer.IconTheme.SubpanelTab, 20, 20, scale: scale, fitInset1x: 2.8f),
            1 => SvgIconRenderer.RenderStateStrip("CPanel.Career.job_icon_10", SvgIconRenderer.IconTheme.SubpanelTab, 20, 20, scale: scale, fitInset1x: 2.9f),
            _ => SvgIconRenderer.RenderStateStrip("CPanel.inv_dots3", SvgIconRenderer.IconTheme.SubpanelTab, 20, 20, scale: scale),
        };

    public static VectorBannerRenderer.RasterImage RenderCameraModeButtonStrip(int w1x = 29, int h1x = 29, int scale = 4) =>
        SvgIconRenderer.RenderStateStrip("CPanel.camera", SvgIconRenderer.IconTheme.ModeMedallion, w1x, h1x, scale: scale);

    public static VectorBannerRenderer.RasterImage RenderCameraViewAlbumStrip(int w1x = 61, int h1x = 35, int scale = 4) =>
        SvgIconRenderer.RenderStateStrip("CPanel.camviewalbum", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale);

    public static VectorBannerRenderer.RasterImage? TryRenderBuyCategoryStrip(string key, int w1x, int h1x, int scale = 4) =>
        key.ToLowerInvariant() switch
        {
            "buyfseating" => SvgIconRenderer.RenderStateStrip("CPanel.Buy.buyf_seating", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "buyfsurfaces" => SvgIconRenderer.RenderStateStrip("CPanel.Buy.buyf_surfaces", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "buyfdecorative" => SvgIconRenderer.RenderStateStrip("CPanel.Buy.buyf_decorative", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "buyfelectronics" => SvgIconRenderer.RenderStateStrip("CPanel.Buy.buyf_electronics", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "buyfappliances" => SvgIconRenderer.RenderStateStrip("CPanel.Buy.buyf_appliances", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "buyfplumbing" => SvgIconRenderer.RenderStateStrip("CPanel.Buy.buyf_plumbing", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "buyflighting" => SvgIconRenderer.RenderStateStrip("CPanel.Buy.buyf_lighting", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "buyfmisc" => SvgIconRenderer.RenderStateStrip("CPanel.Buy.buyf_misc", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "buyrliving" => SvgIconRenderer.RenderStateStrip("CPanel.Buy.buyr_living", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "buyrdining" => SvgIconRenderer.RenderStateStrip("CPanel.Buy.buyr_dining", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "buyrbedroom" => SvgIconRenderer.RenderStateStrip("CPanel.Buy.buyr_bedroom", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "buyrstudy" => SvgIconRenderer.RenderStateStrip("CPanel.Buy.buyr_study", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "buyrkitchen" => SvgIconRenderer.RenderStateStrip("CPanel.Buy.buyr_kitchen", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "buyrbathroom" => SvgIconRenderer.RenderStateStrip("CPanel.Buy.buyr_bathroom", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "buyroutside" => SvgIconRenderer.RenderStateStrip("CPanel.Buy.buyr_outside", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "buyrmisc" => SvgIconRenderer.RenderStateStrip("CPanel.Buy.buyr_misc", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "buysubsortpets" => SvgIconRenderer.RenderStateStrip("CPanel.Buy.buysub_pets", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            _ => null,
        };

    public static VectorBannerRenderer.RasterImage? TryRenderBuildToolStrip(string key, int w1x, int h1x, int scale = 4) =>
        key.ToLowerInvariant() switch
        {
            "terrain" or "terraintool" => SvgIconRenderer.RenderStateStrip("CPanel.Build.tool_terrain", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "water" or "watertool" => SvgIconRenderer.RenderStateStrip("CPanel.Build.tool_water", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "wall" or "walltool" => SvgIconRenderer.RenderStateStrip("CPanel.Build.tool_wall", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "wallpaper" or "wallpapertool" => SvgIconRenderer.RenderStateStrip("CPanel.Build.tool_wallpaper", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "floor" or "floortool" => SvgIconRenderer.RenderStateStrip("CPanel.Build.tool_floor", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "door" or "doortool" => SvgIconRenderer.RenderStateStrip("CPanel.Build.tool_door", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "window" or "windowtool" => SvgIconRenderer.RenderStateStrip("CPanel.Build.tool_window", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "stairs" or "stairtool" => SvgIconRenderer.RenderStateStrip("CPanel.Build.tool_stairs", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "fireplace" or "fireplacetool" => SvgIconRenderer.RenderStateStrip("CPanel.Build.tool_fireplace", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "tree" or "treetool" => SvgIconRenderer.RenderStateStrip("CPanel.Build.tool_tree", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "roof" or "rooftool" => SvgIconRenderer.RenderStateStrip("CPanel.Build.tool_roof", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "hand" or "handtool" => SvgIconRenderer.RenderStateStrip("CPanel.Build.tool_hand", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale, fitInset1x: 2.0f),
            "undo" => SvgIconRenderer.RenderStateStrip("CPanel.Build.undo", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "redo" => SvgIconRenderer.RenderStateStrip("CPanel.Build.redo", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale),
            "terraindownicon" => SvgIconRenderer.RenderStateStrip("CPanel.Build.terrain_down", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale, fitInset1x: 2.2f),
            "terrainlevelicon" => SvgIconRenderer.RenderStateStrip("CPanel.Build.terrain_level", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale, fitInset1x: 2.2f),
            "terrainupicon" => SvgIconRenderer.RenderStateStrip("CPanel.Build.terrain_up", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale, fitInset1x: 2.2f),
            "terraingrassicon" => SvgIconRenderer.RenderStateStrip("CPanel.Build.terrain_grass", SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale, fitInset1x: 2.2f),
            _ => null,
        };

    public static VectorBannerRenderer.RasterImage? TryRenderBuildPopupPreview(string key, int w1x, int h1x, int scale = 4)
    {
        string? stem = key.ToLowerInvariant() switch
        {
            "popupterraindown" => "CPanel.Build.terrain_down",
            "popupterrainlevel" => "CPanel.Build.terrain_level",
            "popupterrainup" => "CPanel.Build.terrain_up",
            "popupgrass" => "CPanel.Build.terrain_grass",
            "popuproofpitch" => "CPanel.Build.roof_pitch",
            _ => null,
        };
        if (stem is null)
            return null;

        var strip = SvgIconRenderer.RenderStateStrip(stem, SvgIconRenderer.IconTheme.OptionsButton, w1x, h1x, scale: scale);
        int fw = w1x * scale;
        int fh = h1x * scale;
        int sw = strip.PixelWidth;
        byte[] rgba = new byte[fw * fh * 4];
        for (int py = 0; py < fh; py++)
            Buffer.BlockCopy(strip.Rgba, py * sw * 4, rgba, py * fw * 4, fw * 4);
        return new VectorBannerRenderer.RasterImage(fw, fh, rgba, new CGSize(w1x, h1x));
    }

    /// <summary>
    /// Recreates PickBkg.bmp / PickBkgMagic.bmp (800x600): PS 5.5 raised cobalt frame + sunken [0,0,59]
    /// Ring-Gloss well + real embedded .svg column header icons (pick_simoleon.svg, pick_smiley.svg, pick_magichat.svg).
    /// </summary>
    public static VectorBannerRenderer.RasterImage RenderFamilyPickerBackground(bool isMagicTown, int scale = 2)
    {
        scale = Math.Clamp(scale, 1, 4);
        string key = $"pickbkg:{(isMagicTown ? 1 : 0)}:{scale}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        const int w1x = 800;
        const int h1x = 600;
        int pw = w1x * scale;
        int ph = h1x * scale;

        var fullFrame = RenderPanelFrame(w1x, h1x, scale);
        byte[] rgba = new byte[pw * ph * 4];
        int padPx = 8 * scale;
        for (int py = 0; py < ph; py++)
        {
            Buffer.BlockCopy(
                fullFrame.Rgba,
                ((py + padPx) * fullFrame.PixelWidth + padPx) * 4,
                rgba,
                py * pw * 4,
                pw * 4);
        }

        var well = RenderRecessedWell(726, 446, cornerR: 7.5f, scale: scale);
        BlitOver(rgba, pw, ph, well.Rgba, well.PixelWidth, well.PixelHeight, 16 * scale, 63 * scale);

        int simX = (isMagicTown ? 563 : 586) - 2;
        int frdX = (isMagicTown ? 637 : 662) - 2;
        const int hdrY = 40;

        var simSvg = SvgIconRenderer.RenderSingle("Nbhd.pick_simoleon", 30, 26, scale);
        var smiSvg = SvgIconRenderer.RenderSingle("Nbhd.pick_smiley", 30, 26, scale);
        BlitOver(rgba, pw, ph, simSvg.Rgba, simSvg.PixelWidth, simSvg.PixelHeight, simX * scale, hdrY * scale);
        BlitOver(rgba, pw, ph, smiSvg.Rgba, smiSvg.PixelWidth, smiSvg.PixelHeight, frdX * scale, hdrY * scale);
        if (isMagicTown)
        {
            var hatSvg = SvgIconRenderer.RenderSingle("Nbhd.pick_magichat", 34, 26, scale);
            BlitOver(rgba, pw, ph, hatSvg.Rgba, hatSvg.PixelWidth, hatSvg.PixelHeight, 686 * scale, hdrY * scale);
        }

        var img = new VectorBannerRenderer.RasterImage(pw, ph, rgba, new CGSize(w1x, h1x));
        Cache[key] = img;
        return img;
    }

    private static void BlitOver(byte[] dst, int dw, int dh, byte[] src, int sw, int sh, int ox, int oy)
    {
        for (int sy = 0; sy < sh; sy++)
        {
            int dy = oy + sy;
            if ((uint)dy >= (uint)dh)
                continue;
            int sRow = sy * sw;
            int dRow = dy * dw;
            for (int sx = 0; sx < sw; sx++)
            {
                int dx = ox + sx;
                if ((uint)dx >= (uint)dw)
                    continue;
                int si = (sRow + sx) * 4;
                float sa = src[si + 3] / 255f;
                if (sa <= 0.001f)
                    continue;
                int di = (dRow + dx) * 4;
                float dr = dst[di], dg = dst[di + 1], db = dst[di + 2], da = dst[di + 3] / 255f;
                Blend(ref dr, ref dg, ref db, ref da, src[si], src[si + 1], src[si + 2], sa);
                dst[di] = Byte(dr);
                dst[di + 1] = Byte(dg);
                dst[di + 2] = Byte(db);
                dst[di + 3] = Byte(da * 255f);
            }
        }
    }
}
