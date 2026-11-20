using SimsSDL.Core;

namespace SimsSDL.UI;

public static partial class SimsVectorUiKit
{
    // Exact fitted WinBtn.bmp model produced by tools/ui/fit_winbtn.py (MAE 2.28 / 2.24 at 1x).
    private const float WinBtnRefW = 65f;
    private const float WinBtnRefH = 33f;
    private const int WinBtnKTheta = 24;
    private const float WinBtnNormalBlur = 1.0f;
    private const float WinBtnPressShift = 2.0f;
    private const float WinBtnPressA = 0.00f;
    private const float WinBtnPressB = 5.00f;

    private static readonly float[] WinBtnDStops =
    [
        -0.75f, -0.25f, 0.25f, 0.75f, 1.25f, 1.75f, 2.25f, 2.75f,
        3.25f, 3.75f, 4.5f, 5.5f, 7.0f, 9.0f, 12.0f, 17.0f,
    ];

    private static readonly float[] WinBtnOutline =
    [
        49.211f, 31.032f,
        41.777f, 31.032f,
        34.097f, 31.032f,
        26.575f, 31.032f,
        19.050f, 31.032f,
        11.122f, 31.032f,
        3.382f, 30.702f,
        -0.383f, 25.407f,
        1.555f, 18.056f,
        5.936f, 11.643f,
        10.982f, 5.963f,
        16.839f, 1.432f,
        23.871f, -0.143f,
        31.494f, -0.143f,
        39.058f, -0.143f,
        46.640f, -0.143f,
        54.558f, -0.143f,
        61.811f, 1.079f,
        65.077f, 7.023f,
        63.873f, 14.437f,
        60.290f, 21.112f,
        55.369f, 26.980f,
    ];

    private static readonly uint[] WinBtnFaceShade =
    [
        0x010151, 0x010152, 0x020252, 0x10105F, 0x151762, 0x0C0D58, 0x0F1F63, 0x003462, 0x003963, 0x013961, 0x003C66, 0x003E69, 0x004D83, 0x00528C, 0x00528A, 0x00528B,
        0x010152, 0x010153, 0x020253, 0x030355, 0x0C0C5D, 0x282776, 0x33327E, 0x141D66, 0x072663, 0x00365B, 0x00375C, 0x003A63, 0x00487A, 0x00538D, 0x005289, 0x00528B,
        0x010153, 0x010153, 0x010153, 0x040456, 0x09095B, 0x121263, 0x2E2E7E, 0x52519E, 0x41408E, 0x1C3177, 0x00355C, 0x00355A, 0x003F6B, 0x00528C, 0x00528A, 0x00528A,
        0x020253, 0x010153, 0x010153, 0x040456, 0x0B0B5C, 0x0E0E60, 0x1B1B69, 0x5353A1, 0x696BB5, 0x463A94, 0x0D3769, 0x003356, 0x003961, 0x004F87, 0x00528B, 0x00518A,
        0x030354, 0x010153, 0x010153, 0x080859, 0x141464, 0x1E1F6D, 0x4D4C94, 0x8888D3, 0x7270BF, 0x474F9C, 0x083C66, 0x003458, 0x003C66, 0x005189, 0x00528A, 0x00518A,
        0x030454, 0x010152, 0x1F1E6B, 0x1C1E6B, 0x6A60AA, 0x6162A6, 0x9388DD, 0x656AB7, 0x354989, 0x103B6C, 0x002D4C, 0x013052, 0x00426F, 0x00528C, 0x005289, 0x00518A,
        0x050555, 0x090959, 0x30337E, 0x403E8B, 0x3D4F93, 0x001D56, 0x10406F, 0x003050, 0x003A61, 0x003C68, 0x004372, 0x004E83, 0x00528B, 0x00528A, 0x00518A, 0x00518A,
        0x050555, 0x0B0A59, 0x060954, 0x0E0E57, 0x000040, 0x000D51, 0x003664, 0x003B64, 0x013F6C, 0x00406D, 0x00416F, 0x004677, 0x01528A, 0x00528A, 0x00518A, 0x00518A,
        0x020354, 0x070755, 0x04054D, 0x030349, 0x000043, 0x040549, 0x001553, 0x01396B, 0x014170, 0x004371, 0x004474, 0x004576, 0x035189, 0x00528A, 0x00528A, 0x00518A,
        0x010151, 0x020250, 0x010147, 0x000144, 0x000040, 0x010141, 0x000544, 0x002C62, 0x004374, 0x004676, 0x004576, 0x004577, 0x045187, 0x00528B, 0x00518A, 0x00518A,
        0x000051, 0x00004F, 0x000044, 0x00003E, 0x00003E, 0x00003D, 0x00003B, 0x001750, 0x003669, 0x00497A, 0x00487A, 0x00477A, 0x075388, 0x00528A, 0x00528A, 0x00518A,
        0x000052, 0x000050, 0x000046, 0x00003C, 0x00003A, 0x000038, 0x000039, 0x000A43, 0x002A5F, 0x004779, 0x00497C, 0x00477A, 0x0B598F, 0x01538C, 0x00528A, 0x00508A,
        0x000052, 0x000051, 0x00004A, 0x00003C, 0x000037, 0x000036, 0x000035, 0x00073E, 0x00275D, 0x00487C, 0x004A7F, 0x014C80, 0x146298, 0x00518A, 0x01528B, 0x005089,
        0x000052, 0x000052, 0x00004E, 0x00003F, 0x000035, 0x000134, 0x00002F, 0x000C40, 0x003469, 0x005086, 0x004B81, 0x004B7F, 0x1A69A0, 0x004E87, 0x01538B, 0x004F89,
        0x000052, 0x000052, 0x000050, 0x00003E, 0x000032, 0x000233, 0x00002B, 0x022559, 0x034F87, 0x05558D, 0x004B82, 0x4A99D6, 0x2E7EB4, 0x004882, 0x03558D, 0x004E88,
        0x000052, 0x000052, 0x000051, 0x00003C, 0x000033, 0x00002C, 0x000B3B, 0x07477D, 0x0C6099, 0x116197, 0x3081C2, 0xB0FFFF, 0x0B5C9A, 0x005088, 0x01538B, 0x004D88,
        0x000051, 0x000052, 0x000049, 0x000038, 0x000030, 0x00002F, 0x073164, 0x12659E, 0x1567A0, 0x196BA5, 0x5EB2E8, 0xDEFFFF, 0x035697, 0x005289, 0x00528A, 0x004F89,
        0x000051, 0x000050, 0x000045, 0x000135, 0x000030, 0x032054, 0x0A558C, 0x10629B, 0x1C6EA6, 0x2F81C2, 0xADF9FF, 0x87D2EE, 0x004585, 0x03558B, 0x00518A, 0x01518A,
        0x000050, 0x000050, 0x000055, 0x00004A, 0x00002B, 0x000337, 0x003268, 0x024076, 0x0E5D93, 0x106097, 0x52A1D9, 0x2676AE, 0x004D86, 0x01538B, 0x00508A, 0x01528B,
        0x000050, 0x000050, 0x000046, 0x000038, 0x000336, 0x002459, 0x01467B, 0x00487D, 0x014F85, 0x004A80, 0x146196, 0x0D5C92, 0x004F88, 0x01538B, 0x00528A, 0x00518A,
        0x000050, 0x00004B, 0x00003D, 0x000034, 0x000D44, 0x003267, 0x004779, 0x00497B, 0x01497C, 0x00487A, 0x024A7D, 0x024F83, 0x00518A, 0x00528A, 0x00528A, 0x00518A,
        0x00004E, 0x000046, 0x00003E, 0x000039, 0x000F49, 0x003C70, 0x004475, 0x004575, 0x004474, 0x004575, 0x004677, 0x00497C, 0x005189, 0x00528A, 0x00528A, 0x00518A,
        0x000050, 0x01014B, 0x000043, 0x010040, 0x000B4A, 0x003369, 0x004270, 0x00436E, 0x00416E, 0x00426F, 0x004372, 0x004575, 0x005087, 0x00528B, 0x00528A, 0x00528A,
        0x000050, 0x01014E, 0x01014B, 0x05054D, 0x060B50, 0x001758, 0x033167, 0x003E67, 0x003D68, 0x003D68, 0x003F6D, 0x00416F, 0x004E85, 0x00518A, 0x00528A, 0x00528A,
    ];

    private static readonly uint[] WinBtnPressedSocketShade =
    [
        0x010151, 0x010152, 0x020252, 0x10105F, 0x151762, 0x0C0A54, 0x0F2165, 0x003869, 0x004070, 0x004474, 0x014D82, 0x014F86, 0x01497F, 0x01447C, 0x014279, 0x014078,
        0x010152, 0x010153, 0x020253, 0x030355, 0x0C0C5D, 0x282876, 0x33337F, 0x141961, 0x072360, 0x00406A, 0x004B7F, 0x004F85, 0x00487E, 0x01447B, 0x014179, 0x014078,
        0x010153, 0x010153, 0x010153, 0x040456, 0x09095B, 0x121263, 0x2E2E7E, 0x52519F, 0x413F8D, 0x1C2D6F, 0x00406E, 0x004D80, 0x00477C, 0x00437A, 0x014178, 0x014078,
        0x020253, 0x010153, 0x010153, 0x040456, 0x0B0B5C, 0x0E0E60, 0x1B1B69, 0x5353A1, 0x696DB9, 0x44358A, 0x11366B, 0x004976, 0x00467A, 0x004279, 0x004078, 0x013F77,
        0x030354, 0x010153, 0x010153, 0x080859, 0x141464, 0x1E206D, 0x4D4C94, 0x8889D4, 0x7270BF, 0x464B94, 0x094171, 0x00497A, 0x004479, 0x004178, 0x003F77, 0x013E76,
        0x030454, 0x010152, 0x1F1E6B, 0x1C1E6A, 0x6A61AC, 0x6163A8, 0x9384D6, 0x6569B6, 0x354685, 0x103E71, 0x004471, 0x00497E, 0x004379, 0x003F77, 0x003E76, 0x003D75,
        0x050555, 0x090959, 0x30337E, 0x403E8B, 0x3D4E8E, 0x00174F, 0x104878, 0x00406F, 0x004371, 0x005693, 0x00518A, 0x00487F, 0x004077, 0x003D75, 0x003C74, 0x003B74,
        0x050555, 0x0B0A59, 0x060954, 0x0E0E58, 0x00003F, 0x000347, 0x003669, 0x002E64, 0x014576, 0x01477C, 0x004473, 0x00447A, 0x003D75, 0x003B73, 0x003A73, 0x013A73,
        0x020354, 0x070755, 0x04054D, 0x030349, 0x000044, 0x030045, 0x000949, 0x03084B, 0x021F58, 0x03215D, 0x00386E, 0x003E76, 0x003871, 0x003871, 0x013872, 0x013872,
        0x010151, 0x020250, 0x010147, 0x000044, 0x000041, 0x010041, 0x00003E, 0x01003F, 0x000643, 0x020E4B, 0x011A58, 0x002E69, 0x00326D, 0x00346F, 0x013670, 0x013771,
        0x000051, 0x00004F, 0x000044, 0x00003E, 0x00003E, 0x00003C, 0x00003C, 0x00003C, 0x00013C, 0x00013D, 0x00114F, 0x002260, 0x002C68, 0x01316D, 0x01346F, 0x013670,
        0x000052, 0x000050, 0x000046, 0x00003C, 0x00003A, 0x000038, 0x000038, 0x000038, 0x000039, 0x000037, 0x000846, 0x001959, 0x012764, 0x012F6B, 0x01336E, 0x013570,
        0x000052, 0x000051, 0x00004A, 0x00003B, 0x000037, 0x000036, 0x000035, 0x000035, 0x000036, 0x000036, 0x00013F, 0x001052, 0x012462, 0x012E6A, 0x01336E, 0x01356F,
        0x000052, 0x000052, 0x00004E, 0x00003F, 0x000035, 0x000034, 0x000033, 0x000032, 0x000034, 0x000035, 0x000039, 0x000C4F, 0x012362, 0x022E6A, 0x02336E, 0x023570,
        0x000052, 0x000052, 0x000050, 0x00003E, 0x000032, 0x000031, 0x000030, 0x000030, 0x000032, 0x000034, 0x000039, 0x010C51, 0x022564, 0x022F6C, 0x02336F, 0x023570,
        0x000052, 0x000052, 0x000051, 0x00003C, 0x000033, 0x000030, 0x00002E, 0x00002E, 0x00002F, 0x000234, 0x000031, 0x021759, 0x032A69, 0x03326E, 0x023570, 0x023671,
        0x000051, 0x000052, 0x000049, 0x000038, 0x000031, 0x00002F, 0x00002E, 0x00002E, 0x000131, 0x00002F, 0x000139, 0x063070, 0x043370, 0x033571, 0x023772, 0x023872,
        0x000051, 0x000050, 0x000045, 0x000034, 0x000031, 0x000030, 0x00002F, 0x000030, 0x000335, 0x000033, 0x093F79, 0x0B4F8A, 0x053C77, 0x033974, 0x033974, 0x023974,
        0x000050, 0x000050, 0x000055, 0x00004A, 0x000036, 0x000034, 0x000131, 0x000030, 0x000026, 0x000334, 0x003C74, 0x064680, 0x05407A, 0x033C77, 0x033B75, 0x023B75,
        0x000050, 0x000050, 0x000046, 0x000038, 0x000032, 0x000032, 0x000030, 0x00002E, 0x000638, 0x002C61, 0x024D86, 0x074E86, 0x04447D, 0x033F79, 0x033D77, 0x023C76,
        0x00004F, 0x00004B, 0x00003C, 0x000036, 0x000036, 0x000034, 0x000136, 0x001045, 0x002F65, 0x00437A, 0x035087, 0x054F86, 0x04467E, 0x03417A, 0x023F78, 0x023E77,
        0x00004E, 0x000047, 0x00003E, 0x00003B, 0x00013C, 0x00063F, 0x001D55, 0x002E65, 0x00477D, 0x004A80, 0x024E84, 0x044E85, 0x03477F, 0x03437B, 0x024079, 0x023F78,
        0x000050, 0x01014B, 0x000043, 0x010041, 0x000544, 0x001752, 0x004578, 0x004375, 0x004D80, 0x004A7E, 0x014D82, 0x034D84, 0x03487F, 0x02447C, 0x024179, 0x024078,
        0x000050, 0x01014E, 0x01014B, 0x05054D, 0x060C51, 0x001251, 0x033870, 0x004877, 0x004879, 0x00487A, 0x034D81, 0x034E84, 0x02487F, 0x02447C, 0x02417A, 0x024079,
    ];

    private static VectorBannerRenderer.RasterImage RenderFittedSlantedButtonStrip(int w1x, int h1x, bool highlighted, int scale)
    {
        scale = Math.Clamp(scale, 1, 8);
        string key = $"winbtn_fitted_v1:{w1x}:{h1x}:{(highlighted ? 1 : 0)}:{scale}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        int fw = w1x * scale;
        int fh = h1x * scale;
        int stripW = fw * 4;
        byte[] strip = new byte[stripW * fh * 4];
        float k = h1x / WinBtnRefH;

        var (d0, t0) = ComputeWinBtnFields(w1x, h1x, scale, k, shift: 0f);
        var (ds, ts) = ComputeWinBtnFields(w1x, h1x, scale, k, shift: WinBtnPressShift);

        int n = fw * fh;
        byte[] normalFrame = new byte[n * 4];
        byte[] pressedFrame = new byte[n * 4];

        Parallel.For(0, n, i =>
        {
            float cov = Math.Clamp(0.5f + d0[i] * k * scale, 0f, 1f);
            if (cov <= 0.002f)
                return;

            var (nr, ng, nb) = LookupWinBtnLut(WinBtnFaceShade, d0[i], t0[i]);
            if (highlighted)
            {
                nr = MathF.Min(255f, nr + 14f);
                ng = MathF.Min(255f, ng + 24f);
                nb = MathF.Min(255f, nb + 28f);
            }

            byte a = Byte(cov * 255f);
            int p = i * 4;
            normalFrame[p] = Byte(nr);
            normalFrame[p + 1] = Byte(ng);
            normalFrame[p + 2] = Byte(nb);
            normalFrame[p + 3] = a;

            float wf = SmoothStep(-0.5f, 0.5f, MathF.Min(ds[i] - WinBtnPressA, d0[i] - WinBtnPressB));
            var (sr, sg, sb) = LookupWinBtnLut(WinBtnPressedSocketShade, d0[i], t0[i]);
            var (fr, fg, fb) = LookupWinBtnLut(WinBtnFaceShade, ds[i], ts[i]);
            float pr = sr * (1f - wf) + fr * wf;
            float pg = sg * (1f - wf) + fg * wf;
            float pb = sb * (1f - wf) + fb * wf;
            if (highlighted)
            {
                pr = MathF.Min(255f, pr + 10f);
                pg = MathF.Min(255f, pg + 18f);
                pb = MathF.Min(255f, pb + 22f);
            }

            pressedFrame[p] = Byte(pr);
            pressedFrame[p + 1] = Byte(pg);
            pressedFrame[p + 2] = Byte(pb);
            pressedFrame[p + 3] = a;
        });

        for (int state = 0; state < 4; state++)
        {
            byte[] src = state == 1 ? pressedFrame : normalFrame;
            for (int py = 0; py < fh; py++)
                Buffer.BlockCopy(src, py * fw * 4, strip, (py * stripW + state * fw) * 4, fw * 4);
        }

        var img = new VectorBannerRenderer.RasterImage(stripW, fh, strip, new CGSize(w1x * 4, h1x));
        Cache[key] = img;
        return img;
    }

    private static (float[] D, float[] Theta) ComputeWinBtnFields(int w1x, int h1x, int scale, float k, float shift)
    {
        int nCtrl = WinBtnOutline.Length / 2;
        var ctrl = new (float X, float Y)[nCtrl];
        for (int i = 0; i < nCtrl; i++)
        {
            float cx = WinBtnOutline[i * 2];
            float cy = WinBtnOutline[i * 2 + 1];
            float sx = cx > WinBtnRefW * 0.5f ? w1x - (WinBtnRefW - cx) * k : cx * k;
            ctrl[i] = (sx + shift * k, cy * k + shift * k);
        }

        const int samplesPerSeg = 16;
        int polyCount = nCtrl * samplesPerSeg;
        var poly = new (float X, float Y)[polyCount];
        int idx = 0;
        for (int i = 0; i < nCtrl; i++)
        {
            var p0 = ctrl[(i - 1 + nCtrl) % nCtrl];
            var p1 = ctrl[i];
            var p2 = ctrl[(i + 1) % nCtrl];
            var p3 = ctrl[(i + 2) % nCtrl];
            for (int s = 0; s < samplesPerSeg; s++)
            {
                float t = s / (float)samplesPerSeg;
                float t2 = t * t;
                float t3 = t2 * t;
                float b0 = (1f - t) * (1f - t) * (1f - t) / 6f;
                float b1 = (3f * t3 - 6f * t2 + 4f) / 6f;
                float b2 = (-3f * t3 + 3f * t2 + 3f * t + 1f) / 6f;
                float b3 = t3 / 6f;
                poly[idx++] = (
                    b0 * p0.X + b1 * p1.X + b2 * p2.X + b3 * p3.X,
                    b0 * p0.Y + b1 * p1.Y + b2 * p2.Y + b3 * p3.Y);
            }
        }

        int fw = w1x * scale;
        int fh = h1x * scale;
        int total = fw * fh;
        float[] d = new float[total];
        float[] nx = new float[total];
        float[] ny = new float[total];
        float invScale = 1f / scale;
        float invK = 1f / k;

        Parallel.For(0, fh, py =>
        {
            float y = (py + 0.5f) * invScale;
            for (int px = 0; px < fw; px++)
            {
                float x = (px + 0.5f) * invScale;
                float bestD2 = float.MaxValue;
                float nearX = 0f, nearY = 0f;
                bool inside = false;
                for (int i = 0; i < polyCount; i++)
                {
                    var a = poly[i];
                    var b = poly[(i + 1) % polyCount];
                    float vx = b.X - a.X;
                    float vy = b.Y - a.Y;
                    float t = Math.Clamp(((x - a.X) * vx + (y - a.Y) * vy) / (vx * vx + vy * vy + 1e-12f), 0f, 1f);
                    float qx = a.X + t * vx;
                    float qy = a.Y + t * vy;
                    float dx = x - qx;
                    float dy = y - qy;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < bestD2)
                    {
                        bestD2 = d2;
                        nearX = qx;
                        nearY = qy;
                    }

                    if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y + 1e-12f) + a.X)
                        inside = !inside;
                }

                float dist = MathF.Sqrt(bestD2);
                float sgn = inside ? 1f : -1f;
                int pi = py * fw + px;
                d[pi] = sgn * dist * invK;
                float invDist = 1f / (dist + 1e-9f);
                nx[pi] = (x - nearX) * -sgn * invDist;
                ny[pi] = (y - nearY) * -sgn * invDist;
            }
        });

        float sigma = WinBtnNormalBlur * scale * k;
        float[] snx = SepGaussianBlur(nx, fw, fh, sigma);
        float[] sny = SepGaussianBlur(ny, fw, fh, sigma);
        float[] theta = new float[total];
        Parallel.For(0, total, i => theta[i] = MathF.Atan2(sny[i], snx[i]));
        return (d, theta);
    }

    private static (float R, float G, float B) LookupWinBtnLut(uint[] lut, float d, float theta)
    {
        const float twoPi = MathF.PI * 2f;
        float wrapped = theta % twoPi;
        if (wrapped < 0f) wrapped += twoPi;
        float tu = (wrapped / twoPi) * WinBtnKTheta;
        int i0 = ((int)MathF.Floor(tu)) % WinBtnKTheta;
        int i1 = (i0 + 1) % WinBtnKTheta;
        float ft = tu - MathF.Floor(tu);

        int nd = WinBtnDStops.Length;
        int dj = 0;
        while (dj < nd - 2 && d > WinBtnDStops[dj + 1])
            dj++;
        float fd = Math.Clamp((d - WinBtnDStops[dj]) / (WinBtnDStops[dj + 1] - WinBtnDStops[dj]), 0f, 1f);

        static (float R, float G, float B) Unpack(uint c) => ((c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF);
        var c00 = Unpack(lut[i0 * nd + dj]);
        var c01 = Unpack(lut[i0 * nd + dj + 1]);
        var c10 = Unpack(lut[i1 * nd + dj]);
        var c11 = Unpack(lut[i1 * nd + dj + 1]);

        float w00 = (1f - ft) * (1f - fd);
        float w01 = (1f - ft) * fd;
        float w10 = ft * (1f - fd);
        float w11 = ft * fd;
        return (
            c00.R * w00 + c01.R * w01 + c10.R * w10 + c11.R * w11,
            c00.G * w00 + c01.G * w01 + c10.G * w10 + c11.G * w11,
            c00.B * w00 + c01.B * w01 + c10.B * w10 + c11.B * w11);
    }

    // Exact fitted PopupInfoTiles.bmp frame model produced by tools/ui/fit_popup_frame.py (MAE 0.13 on 240x120).
    private static readonly uint[] PopupFrameTop = [0x6C6CA8, 0x7272AD, 0x7C7CB3, 0x7575AD, 0x383882, 0x111160, 0x050555, 0x000051, 0x000051];
    private static readonly uint[] PopupFrameLeft = [0x0C0C52, 0x0A0A51, 0x0D0D54, 0x11115A, 0x191960, 0x11115A, 0x050555, 0x000051, 0x000051];
    private static readonly uint[] PopupFrameRight = [0x00002D, 0x00002D, 0x00002D, 0x000031, 0x000035, 0x000045, 0x00004C, 0x000051, 0x000051];
    private static readonly uint[] PopupFrameBottom = [0x000024, 0x000023, 0x000024, 0x000029, 0x00002D, 0x000045, 0x00004C, 0x000051, 0x000051];

    private static VectorBannerRenderer.RasterImage RenderFittedPopupInfoFrame(int w1x, int h1x, int scale)
    {
        scale = Math.Clamp(scale, 1, 8);
        string key = $"popup_frame_fitted_v1:{w1x}:{h1x}:{scale}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        int pw = w1x * scale;
        int ph = h1x * scale;
        byte[] rgba = new byte[pw * ph * 4];
        float inv = 1f / scale;
        const float tau = 1.90f;
        const float invSqrt2 = 0.70710678f;
        const float intR = 0f, intG = 0f, intB = 0x51;

        static (float R, float G, float B) SampleProfile(uint[] p, float d)
        {
            float x = Math.Clamp(d - 0.5f, 0f, p.Length - 1);
            int i = Math.Min((int)MathF.Floor(x), p.Length - 2);
            float f = x - i;
            uint c0 = p[i], c1 = p[i + 1];
            return (
                Lerp((c0 >> 16) & 0xFF, (c1 >> 16) & 0xFF, f),
                Lerp((c0 >> 8) & 0xFF, (c1 >> 8) & 0xFF, f),
                Lerp(c0 & 0xFF, c1 & 0xFF, f));
        }

        Parallel.For(0, ph, py =>
        {
            float y = (py + 0.5f) * inv;
            float dT = y;
            float dB = h1x - y;
            float wT = MathF.Exp(-MathF.Min(dT, 40f) / tau);
            float wB = MathF.Exp(-MathF.Min(dB, 40f) / tau);
            var pT = SampleProfile(PopupFrameTop, dT);
            var pB = SampleProfile(PopupFrameBottom, dB);

            for (int px = 0; px < pw; px++)
            {
                float x = (px + 0.5f) * inv;
                float dL = x;
                float dR = w1x - x;
                float wL = MathF.Exp(-MathF.Min(dL, 40f) / tau);
                float wR = MathF.Exp(-MathF.Min(dR, 40f) / tau);
                var pL = SampleProfile(PopupFrameLeft, dL);
                var pR = SampleProfile(PopupFrameRight, dR);

                float wSum = wT + wB + wL + wR;
                float r = intR + (wT * (pT.R - intR) + wB * (pB.R - intR) + wL * (pL.R - intR) + wR * (pR.R - intR)) / wSum;
                float g = intG + (wT * (pT.G - intG) + wB * (pB.G - intG) + wL * (pL.G - intG) + wR * (pR.G - intG)) / wSum;
                float b = intB + (wT * (pT.B - intB) + wB * (pB.B - intB) + wL * (pL.B - intB) + wR * (pR.B - intB)) / wSum;

                if (x < 24f && y < 24f)
                {
                    float u = (x - y - 1.50f) * invSqrt2;
                    float v = (x + y) * invSqrt2 - 4.50f;
                    float glint = MathF.Exp(-0.5f * ((u / 2.00f) * (u / 2.00f) + (v / 3.00f) * (v / 3.00f)));
                    r += glint * 128.02f;
                    g += glint * 128.02f;
                    b += glint * 97.58f;
                }

                int dst = (py * pw + px) * 4;
                rgba[dst] = Byte(r);
                rgba[dst + 1] = Byte(g);
                rgba[dst + 2] = Byte(b);
                rgba[dst + 3] = 255;
            }
        });

        var img = new VectorBannerRenderer.RasterImage(pw, ph, rgba, new CGSize(w1x, h1x));
        Cache[key] = img;
        return img;
    }
}
