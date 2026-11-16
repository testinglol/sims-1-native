using System.Diagnostics;
using System.Numerics;
using SimsSDL;

namespace SimsSDL.UI;

/// <summary>Renders the credits roll, loading string blocks from <c>GameData/Credits.iff</c> in reverse
/// release order (newest expansion pack first) over a framed dialog background.</summary>
public sealed class OriginalCreditsOverlay
{
    public struct DrawItem
    {
        public OriginalGameplayOverlay.Image Image { get; init; }
        public CGRect Rect { get; init; }
        public Vector4 UV { get; set; }
        public float Opacity { get; set; }

        public DrawItem(OriginalGameplayOverlay.Image image, CGRect rect, Vector4 uv, float opacity = 1f)
        {
            Image = image;
            Rect = rect;
            UV = uv;
            Opacity = opacity;
        }
    }

    private readonly record struct CreditLinePair(OriginalGameplayOverlay.Image Base, OriginalGameplayOverlay.Image White);

    private readonly record struct CreditItem(IReadOnlyList<CreditLinePair> Sublines, double LineHeight);

    // Credits animation timing constants: 35 px/s scroll speed, 750ms item spacing, 1000ms initial delay,
    // and 400ms fade duration.
    public static readonly CGRect CloseRect = new(350, 547, 100, 33);
    public const double ScrollSpeed = 35.0;
    public const double LineInterval = 0.75;
    public const double StartDelay = 1.0;
    public const double FadeDuration = 0.4;
    public const double SpawnY = 400.0;
    public const double TopY = 100.0;
    public const string Music = "bkground_credits";

    private static readonly (byte R, byte G, byte B) BaseTextColor = (195, 205, 205);
    private static readonly (byte R, byte G, byte B) HighlightTextColor = (255, 255, 255);

    public static string MusicForNeighborhoodMode(int mode) =>
        OriginalCreditsLayout.Music(mode);

    public static IReadOnlyList<ushort> Blocks(IEnumerable<string> installedFolders) =>
        OriginalCreditsLayout.Blocks(installedFolders);

    public static IReadOnlyList<string> TokenizeEntry(string entry) =>
        OriginalCreditsLayout.TokenizeEntry(entry);

    private readonly OriginalGameplayOverlay.Image _outerFill;
    private readonly OriginalGameplayOverlay.Image _backgroundTile;
    private readonly IReadOnlyList<CreditItem> _items;
    private readonly OriginalGameplayOverlay.Image _systemButton;
    private readonly CGRect _okButtonRect;
    private readonly string _okLabel;
    private readonly OriginalGameplayOverlay.Image _okCaptionNormal;
    private readonly OriginalGameplayOverlay.Image _okCaptionHover;
    private readonly double _opened = MonotonicNow();

    public bool HoveredClose { get; private set; }

    public OriginalCreditsOverlay(GameContent content, SimsBitmapFont font, UIDevice device, OriginalUIStrings strings)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(strings);

        SimsBitmapFont LoadFont(string path)
        {
            try
            {
                return new SimsBitmapFont(content.Read(path));
            }
            catch
            {
                return font;
            }
        }

        OriginalGameplayOverlay.Image Render(string value, SimsBitmapFont targetFont, (byte R, byte G, byte B) color)
        {
            var raster = targetFont.Rasterize(value, color);
            return OriginalGameplayOverlay.CreateImage(device, raster.Width, raster.Height, raster.Rgba);
        }

        var creditsFont = LoadFont("UIGraphics/Fonts/variablesans_14.ffn");
        var buttonFont = LoadFont("UIGraphics/Fonts/variablesans_12.ffn");

        _outerFill = OriginalGameplayOverlay.CreateImage(device, 1, 1, [0, 0, 0, 255]);
        var bgBitmap = new SimsBitmap(content.Read("UIGraphics/cpanel/Backgrounds/PopupInfoTiles.bmp"));
        _backgroundTile = OriginalGameplayOverlay.CreateImage(device, bgBitmap.Width, bgBitmap.Height, bgBitmap.Rgba, magenta: true);

        var folders = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(content.Root))
            {
                string name = Path.GetFileName(entry);
                string lower = name.ToLowerInvariant();
                if (lower.StartsWith("expansionpack", StringComparison.Ordinal) ||
                    string.Equals(lower, "deluxe", StringComparison.Ordinal))
                {
                    folders.Add(name);
                }
            }
        }
        catch
        {
        }

        var tables = OriginalUIStrings.CreditBlocks(content);
        var textCache = new Dictionary<string, CreditLinePair>(StringComparer.Ordinal);
        CreditLinePair CachedImages(string line)
        {
            if (textCache.TryGetValue(line, out var existing))
            {
                return existing;
            }

            var pair = new CreditLinePair(
                Render(line, creditsFont, BaseTextColor),
                Render(line, creditsFont, HighlightTextColor));
            textCache[line] = pair;
            return pair;
        }

        const int wrapWidth = 768;
        double lineH = creditsFont.LineHeight;
        var builtItems = new List<CreditItem>();
        foreach (ushort id in Blocks(folders))
        {
            if (!tables.TryGetValue(id, out var entries))
            {
                continue;
            }

            foreach (string entry in entries)
            {
                foreach (string rawLine in TokenizeEntry(entry))
                {
                    if (string.IsNullOrWhiteSpace(rawLine))
                    {
                        builtItems.Add(new CreditItem([], lineH));
                    }
                    else
                    {
                        IReadOnlyList<string> wrapped;
                        try
                        {
                            wrapped = creditsFont.WrappedLines(rawLine, wrapWidth);
                        }
                        catch
                        {
                            wrapped = [rawLine];
                        }

                        var sublines = new List<CreditLinePair>();
                        foreach (string wrappedLine in wrapped)
                        {
                            if (!string.IsNullOrWhiteSpace(wrappedLine))
                            {
                                sublines.Add(CachedImages(wrappedLine));
                            }
                        }

                        builtItems.Add(new CreditItem(sublines, lineH));
                    }
                }
            }
        }
        _items = builtItems;

        // Configures the credits dismissal button using a 4-frame 65x33 button sheet and string table 152
        // entry 0, centered horizontally 20 pixels from the bottom.
        byte[]? sysData = null;
        try
        {
            sysData = content.Read("UIGraphics/Shared/Sys/WinBtn.bmp");
        }
        catch
        {
            sysData = null;
        }

        if (sysData is not null)
        {
            var sysBitmap = new SimsBitmap(sysData);
            _systemButton = OriginalGameplayOverlay.CreateImage(device, sysBitmap.Width, sysBitmap.Height, sysBitmap.Rgba, magenta: true);
        }
        else
        {
            var fallbackBitmap = new SimsBitmap(content.Read("UIGraphics/Nbhd/NbhdTileBtn.bmp"));
            _systemButton = OriginalGameplayOverlay.CreateImage(device, fallbackBitmap.Width, fallbackBitmap.Height, fallbackBitmap.Rgba, magenta: true);
        }

        string label = strings.OkButton();
        _okLabel = label;
        double btnWidth = Math.Max(100.0, buttonFont.TextWidth(label) + 44.0);
        double btnHeight = _systemButton.Size.Height;
        _okButtonRect = new CGRect((800.0 - btnWidth) / 2.0, 600.0 - btnHeight - 20.0, btnWidth, btnHeight);
        _okCaptionNormal = Render(label, buttonFont, BaseTextColor);
        _okCaptionHover = Render(label, buttonFont, HighlightTextColor);
    }

    private static (double Left, double Top, double Factor) Transform(CGSize canvas, float uiScale = 1f)
    {
        _ = uiScale;
        double factor = Math.Max(0.001, Math.Min(canvas.Width / 800.0, canvas.Height / 600.0));
        double left = (canvas.Width - 800.0 * factor) / 2.0;
        double top = (canvas.Height - 600.0 * factor) / 2.0;
        return (left, top, factor);
    }

    /// <summary>Evaluates credit line animation state at the given elapsed time: fades in while stationary
    /// at y=400, scrolls upward to y=100 while brightening to pure white at the vertical midpoint (y=250),
    /// and fades out while stationary at y=100.</summary>
    private static (double Y, float BaseAlpha, float Spotlight)? ItemState(int index, double elapsed)
    {
        double age = elapsed - StartDelay - index * LineInterval;
        if (age < 0.0)
        {
            return null;
        }
        if (age < FadeDuration)
        {
            return (SpawnY, (float)(age / FadeDuration), 0f);
        }

        double scrollAge = age - FadeDuration;
        const double distance = SpawnY - TopY;
        const double scrollDuration = distance / ScrollSpeed;
        if (scrollAge < scrollDuration)
        {
            double y = SpawnY - scrollAge * ScrollSpeed;
            float t = (float)((SpawnY - y) / distance);
            float spotlight = Math.Clamp(t <= 0.5f ? 2f * t : 2f - 2f * t, 0f, 1f);
            return (y, 1f, spotlight);
        }

        double fadeOutAge = scrollAge - scrollDuration;
        if (fadeOutAge >= FadeDuration)
        {
            return null;
        }
        return (TopY, (float)(1.0 - fadeOutAge / FadeDuration), 0f);
    }

    /// <summary>Indicates whether all credit items and trailing spacer items have finished scrolling and
    /// faded out.</summary>
    public bool IsFinished
    {
        get
        {
            if (_items.Count == 0)
            {
                return true;
            }
            double elapsed = MonotonicNow() - _opened;
            const double distance = SpawnY - TopY;
            const double totalItemLifetime = FadeDuration + distance / ScrollSpeed + FadeDuration;
            double finishTime = StartDelay + (_items.Count - 1) * LineInterval + totalItemLifetime;
            return elapsed >= finishTime;
        }
    }

    private static void AppendNineSlice(
        OriginalGameplayOverlay.Image image,
        CGRect rect,
        double requestedEdge,
        double sourceEdge,
        List<DrawItem> draws)
    {
        double edge = Math.Min(requestedEdge, Math.Min(rect.Width / 2.0, rect.Height / 2.0));
        float u = (float)Math.Min(1.0 / 3.0, sourceEdge / Math.Max(1.0, image.Size.Width));
        float v = (float)Math.Min(1.0 / 3.0, sourceEdge / Math.Max(1.0, image.Size.Height));
        double[] xs = [rect.MinX, rect.MinX + edge, rect.MaxX - edge, rect.MaxX];
        double[] ys = [rect.MinY, rect.MinY + edge, rect.MaxY - edge, rect.MaxY];
        float[] us = [0f, u, 1f - u, 1f];
        float[] vs = [0f, v, 1f - v, 1f];

        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                double cellW = xs[col + 1] - xs[col];
                double cellH = ys[row + 1] - ys[row];
                if (cellW <= 0.0 || cellH <= 0.0)
                {
                    continue;
                }

                draws.Add(new DrawItem(
                    image,
                    new CGRect(xs[col], ys[row], cellW, cellH),
                    new Vector4(us[col], vs[row], us[col + 1] - us[col], vs[row + 1] - vs[row]),
                    1f));
            }
        }
    }

    public IReadOnlyList<DrawItem> DrawItems(CGSize canvas, float uiScale = 1f)
    {
        var (left, top, factor) = Transform(canvas, uiScale);
        CGRect Screen(CGRect rect) =>
            new(left + rect.MinX * factor, top + rect.MinY * factor, rect.Width * factor, rect.Height * factor);

        var whole = new Vector4(0f, 0f, 1f, 1f);

        // Renders the 800x600 background frame using 12px borders derived from the 36x36 9-slice tiles over
        // letterboxed margins.
        var draws = new List<DrawItem>();
        if (left > 0.0 || top > 0.0)
        {
            draws.Add(new DrawItem(_outerFill, new CGRect(0, 0, canvas.Width, canvas.Height), whole, 1f));
        }

        AppendNineSlice(_backgroundTile, Screen(new CGRect(0, 0, 800, 600)), 12.0 * factor, 12.0, draws);

        double elapsed = MonotonicNow() - _opened;
        for (int index = 0; index < _items.Count; index++)
        {
            var item = _items[index];
            if (item.Sublines.Count == 0)
            {
                continue;
            }
            if (ItemState(index, elapsed) is not { } state || state.BaseAlpha <= 0.001f)
            {
                continue;
            }

            for (int subIndex = 0; subIndex < item.Sublines.Count; subIndex++)
            {
                var pair = item.Sublines[subIndex];
                double y = state.Y + subIndex * item.LineHeight - 3.0;
                if (y < 12.0 || y + pair.Base.Size.Height > 588.0)
                {
                    continue;
                }

                var rect = Screen(new CGRect(
                    400.0 - pair.Base.Size.Width / 2.0,
                    y,
                    pair.Base.Size.Width,
                    pair.Base.Size.Height));
                if (state.Spotlight >= 0.999f)
                {
                    draws.Add(new DrawItem(pair.White, rect, whole, state.BaseAlpha));
                }
                else
                {
                    draws.Add(new DrawItem(pair.Base, rect, whole, state.BaseAlpha));
                    if (state.Spotlight > 0.001f)
                    {
                        draws.Add(new DrawItem(pair.White, rect, whole, state.Spotlight * state.BaseAlpha));
                    }
                }
            }
        }

        var okRect = _okButtonRect;
        float frame = HoveredClose ? 2f : 0f;
        double frameWidth = _systemButton.Size.Width / 4.0;
        double rawCap = Math.Floor((frameWidth - 1.0) / 2.0);
        double cap = Math.Min(rawCap, okRect.Width / 2.0);
        float u = (float)(cap / _systemButton.Size.Width);
        const float quarter = 0.25f;

        (double X, double Width, float U0, float Du)[] slices =
        [
            (okRect.MinX, cap, frame * quarter, u),
            (okRect.MinX + cap, okRect.Width - cap * 2.0, frame * quarter + u, quarter - u * 2f),
            (okRect.MaxX - cap, cap, (frame + 1f) * quarter - u, u),
        ];

        foreach (var slice in slices)
        {
            if (slice.Width > 0.0)
            {
                draws.Add(new DrawItem(
                    _systemButton,
                    Screen(new CGRect(slice.X, okRect.MinY, slice.Width, okRect.Height)),
                    new Vector4(slice.U0, 0f, slice.Du, 1f),
                    1f));
            }
        }

        var caption = HoveredClose ? _okCaptionHover : _okCaptionNormal;
        draws.Add(new DrawItem(
            caption,
            Screen(new CGRect(
                okRect.MidX - caption.Size.Width / 2.0,
                okRect.MidY - caption.Size.Height / 2.0,
                caption.Size.Width,
                caption.Size.Height)),
            whole,
            1f));

        return draws;
    }

    public IReadOnlyList<(string Label, CGRect Rect)> Controls(CGSize canvas, float uiScale = 1f)
    {
        var (left, top, factor) = Transform(canvas, uiScale);
        var rect = _okButtonRect;
        return
        [
            (_okLabel, new CGRect(left + rect.MinX * factor, top + rect.MinY * factor, rect.Width * factor, rect.Height * factor))
        ];
    }

    public void Hover(CGPoint point, CGSize canvas, float uiScale = 1f)
    {
        HoveredClose = Contains(Controls(canvas, uiScale)[0].Rect, point);
    }

    public bool Click(CGPoint point, CGSize canvas, float uiScale = 1f) =>
        Contains(Controls(canvas, uiScale)[0].Rect, point);

    private static bool Contains(CGRect rect, CGPoint point) =>
        rect.Width > 0.0 && rect.Height > 0.0 &&
        point.X >= rect.MinX && point.X < rect.MaxX &&
        point.Y >= rect.MinY && point.Y < rect.MaxY;

    private static double MonotonicNow() =>
        Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
