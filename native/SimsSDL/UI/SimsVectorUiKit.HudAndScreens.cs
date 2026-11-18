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

                    float dIn = SdRoundedBox4(x, y, c, c, 19.8f * u, 19.8f * u, 4.2f * u, 4.2f * u, 4.2f * u, 4.2f * u);
                    float inCov = Math.Clamp(0.5f - dIn * scale, 0f, 1f);
                    float ringCov = outCov * (1f - inCov);
                    if (ringCov <= 0.001f)
                        continue;

                    float dMid = MathF.Abs((dOut + dIn) * 0.5f);
                    float ridge = Math.Clamp(1f - dMid / (1.3f * u), 0f, 1f);
                    float shade = 0.65f + 0.35f * ridge;

                    int dst = (py * stripW + stateCapture * fw + px) * 4;
                    strip[dst] = Byte(rc.R * shade);
                    strip[dst + 1] = Byte(rc.G * shade);
                    strip[dst + 2] = Byte(rc.B * shade);
                    strip[dst + 3] = Byte(ringCov * 255f);
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
    /// Renders Live Mode subpanel tab strips (Relationship.bmp, Personality.bmp) from real Inkvec .svg files.
    /// </summary>
    public static VectorBannerRenderer.RasterImage? TryRenderSubpanelTabStrip(string assetStem, int w1x = 27, int h1x = 30, int scale = 4) =>
        assetStem.ToLowerInvariant() switch
        {
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

        var well = RenderRecessedWell(726, 472, cornerR: 7.5f, scale: scale);
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
