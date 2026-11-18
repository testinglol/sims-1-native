using System.Numerics;

namespace SimsSDL.UI;

public sealed class OriginalPluginPanelOverlay
{
    public readonly record struct DrawItem(OriginalGameplayOverlay.Image Image, CGRect Rect, Vector4 UV);

    public abstract record Hit
    {
        private protected Hit() { }

        public sealed record Action(string Value) : Hit;
        public sealed record Close : Hit
        {
            public static readonly Close Instance = new();
        }
        public sealed record Inside : Hit
        {
            public static readonly Inside Instance = new();
        }
    }

    private static readonly (byte R, byte G, byte B) TitleTextColor = SimsVectorUiKit.TextTitle;
    private static readonly (byte R, byte G, byte B) NormalTextColor = SimsVectorUiKit.TextNormal;
    private static readonly (byte R, byte G, byte B) HoverTextColor = SimsVectorUiKit.TextHover;

    private readonly SimsBitmapFont _font;
    private readonly UIDevice _device;
    private readonly OriginalGameplayOverlay.Image _panelFrame;
    private readonly OriginalGameplayOverlay.Image _meter;
    private readonly OriginalGameplayOverlay.Image _title;
    private readonly string _closeLabel;
    private readonly OriginalGameplayOverlay.Image _close;
    private readonly OriginalGameplayOverlay.Image _closeHover;
    private readonly Dictionary<string, OriginalGameplayOverlay.Image> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<(int W, int H, int Scale), OriginalGameplayOverlay.Image> _wellCache = new();
    private readonly Dictionary<(int W, int H, int Scale), OriginalGameplayOverlay.Image> _buttonCache = new();
    private readonly Dictionary<string, OriginalGameplayOverlay.Image[]> _textCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (OriginalGameplayOverlay.Image Normal, OriginalGameplayOverlay.Image Hover)> _buttonLabelCache = new(StringComparer.Ordinal);
    private Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string PackId { get; }
    public PanelDescription Description { get; }
    public SimsBitmapFont TitleFont { get; }
    public SimsBitmapFont ButtonFont { get; }
    public IReadOnlyDictionary<string, string> Values => _values;
    public string? Hovered { get; private set; }
    public int? AnchorId { get; }
    public CGRect? Anchor { get; set; }
    public double? LotHeight { get; set; }

    public OriginalPluginPanelOverlay(
        string packId,
        PanelDescription description,
        GameContent content,
        SimsBitmapFont font,
        UIDevice device,
        string closeLabel,
        int? anchorId = null)
    {
        ArgumentNullException.ThrowIfNull(packId);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(closeLabel);

        description.Validate();
        PackId = packId;
        AnchorId = anchorId;
        Description = description;
        _font = font;
        var loadedButtonFont = TryLoadFont(content, "UIGraphics/Fonts/variablesans_12.ffn") ?? font;
        var loadedTitleFont = TryLoadFont(content, "UIGraphics/Fonts/variablesans_14.ffn") ?? loadedButtonFont;
        ButtonFont = loadedButtonFont;
        TitleFont = loadedTitleFont;
        _device = device;
        _closeLabel = closeLabel;

        OriginalGameplayOverlay.Image Upload(string path, bool transparent = false)
        {
            var bitmap = new SimsBitmap(content.Read(path));
            return OriginalGameplayOverlay.CreateImage(device, bitmap.Width, bitmap.Height, bitmap.Rgba, magenta: transparent);
        }

        int uiScale = SimsVectorUiKit.CurrentScale();
        var frameImg = SimsVectorUiKit.RenderPopupInfoFrame(
            Math.Max(16, (int)Math.Round((double)description.Frame.Width)),
            Math.Max(16, (int)Math.Round((double)description.Frame.Height)),
            uiScale);
        _panelFrame = OriginalGameplayOverlay.CreateImage(device, frameImg.PixelWidth, frameImg.PixelHeight, frameImg.Rgba, frameImg.LogicalSize);
        _meter = Upload("UIGraphics/cpanel/Buttons/optslideroff.bmp", transparent: true);
        _title = OriginalGameplayOverlay.Text(description.Title, loadedTitleFont, device, TitleTextColor);
        _close = OriginalGameplayOverlay.Text(closeLabel, loadedButtonFont, device, NormalTextColor);
        _closeHover = OriginalGameplayOverlay.Text(closeLabel, loadedButtonFont, device, HoverTextColor);

        foreach (var element in description.Elements)
        {
            if (element.Kind != PanelDescription.Kind.Image || element.Resource is not { } resource)
            {
                continue;
            }

            var data = content.Read(resource);
            int width;
            int height;
            byte[] rgba;
            if (resource.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
            {
                var image = new SimsTarga(data);
                width = image.Width;
                height = image.Height;
                rgba = image.Rgba;
            }
            else
            {
                var image = new SimsBitmap(data);
                width = image.Width;
                height = image.Height;
                rgba = image.Rgba;
            }

            _images[element.Id] = OriginalGameplayOverlay.CreateImage(device, width, height, rgba, magenta: true);
        }
    }

    public void Update(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (DictionariesEqual(_values, values))
        {
            return;
        }

        _values = new Dictionary<string, string>(values, StringComparer.Ordinal);
    }

    public CGRect PanelRect(CGSize canvas)
    {
        var point = Origin(canvas);
        return new CGRect(point.X, point.Y, Description.Frame.Width, Description.Frame.Height);
    }

    public Hit? HitTest(CGPoint point, CGSize canvas, double scale)
    {
        var logical = new CGSize(canvas.Width / scale, canvas.Height / scale);
        var local = new CGPoint(point.X / scale, point.Y / scale);
        if (Contains(CloseRect(logical), local))
        {
            return Hit.Close.Instance;
        }

        foreach (var element in Description.Elements)
        {
            if (element.Kind == PanelDescription.Kind.Button &&
                Contains(ElementRect(element, logical), local) &&
                element.Action is { } action)
            {
                return new Hit.Action(action);
            }
        }

        return Contains(PanelRect(logical), local) ? Hit.Inside.Instance : null;
    }

    public void Hover(CGPoint point, CGSize canvas, double scale)
    {
        var logical = new CGSize(canvas.Width / scale, canvas.Height / scale);
        var local = new CGPoint(point.X / scale, point.Y / scale);
        if (Contains(CloseRect(logical), local))
        {
            Hovered = "close";
            return;
        }

        string? hoveredElement = null;
        foreach (var element in Description.Elements)
        {
            if (element.Kind == PanelDescription.Kind.Button && Contains(ElementRect(element, logical), local))
            {
                hoveredElement = element.Id;
                break;
            }
        }

        Hovered = hoveredElement;
    }

    public IReadOnlyList<(string Label, CGRect Rect)> Controls(CGSize canvas)
    {
        var result = new List<(string Label, CGRect Rect)>();
        foreach (var element in Description.Elements)
        {
            var text = element.AccessibilityLabel ?? Description.DisplayText(element, _values);
            switch (element.Kind)
            {
                case PanelDescription.Kind.Button:
                    result.Add((text, ElementRect(element, canvas)));
                    break;
                case PanelDescription.Kind.Meter:
                {
                    var fractionText = Description.MeterFraction(element, _values) is { } fraction
                        ? $"{(int)Math.Round(fraction * 100.0, MidpointRounding.AwayFromZero)}%"
                        : string.Empty;
                    result.Add(($"{element.AccessibilityLabel ?? element.Id} {fractionText}", ElementRect(element, canvas)));
                    break;
                }
                case PanelDescription.Kind.Label:
                case PanelDescription.Kind.List:
                case PanelDescription.Kind.Image:
                    if (!string.IsNullOrEmpty(text))
                    {
                        result.Add((text, ElementRect(element, canvas)));
                    }
                    break;
            }
        }

        result.Add((_closeLabel, CloseRect(canvas)));
        return result;
    }

    public IReadOnlyList<DrawItem> DrawItems(CGSize canvas)
    {
        var panel = PanelRect(canvas);
        var items = new List<DrawItem>();
        var whole = new Vector4(0f, 0f, 1f, 1f);

        if (Description.Framed ?? true)
        {
            items.Add(new DrawItem(_panelFrame, panel, whole));

            items.Add(new DrawItem(
                _title,
                new CGRect(panel.X + panel.Width / 2.0 - _title.Size.Width / 2.0, panel.Y + 14.0, _title.Size.Width, _title.Size.Height),
                whole));
        }

        foreach (var element in Description.Elements)
        {
            var area = ElementRect(element, canvas);
            switch (element.Kind)
            {
                case PanelDescription.Kind.Label:
                {
                    var wrapped = Lines(Description.DisplayText(element, _values), (int)area.Width);
                    for (int index = 0; index < wrapped.Length; index++)
                    {
                        var line = wrapped[index];
                        double y = area.Y + index * _font.LineHeight;
                        // Rasterized font lines include three points of padding; clipping occurs on the
                        // glyph line itself.
                        if (y + _font.LineHeight > area.Y + area.Height + 3.0)
                        {
                            break;
                        }

                        items.Add(new DrawItem(
                            line,
                            new CGRect(area.X, y, Math.Min(line.Size.Width, area.Width), line.Size.Height),
                            new Vector4(0f, 0f, (float)Math.Min(1.0, area.Width / Math.Max(1.0, line.Size.Width)), 1f)));
                    }
                    break;
                }
                case PanelDescription.Kind.List:
                {
                    items.Add(new DrawItem(WellImage((int)Math.Round(area.Width), (int)Math.Round(area.Height)), area, whole));
                    var wrapped = Lines(Description.DisplayText(element, _values), (int)area.Width - 14);
                    for (int index = 0; index < wrapped.Length; index++)
                    {
                        var line = wrapped[index];
                        double y = area.Y + 6.0 + index * _font.LineHeight;
                        if (y + _font.LineHeight > area.Y + area.Height - 4.0)
                        {
                            break;
                        }

                        items.Add(new DrawItem(
                            line,
                            new CGRect(area.X + 7.0, y, Math.Min(line.Size.Width, area.Width - 14.0), line.Size.Height),
                            new Vector4(0f, 0f, (float)Math.Min(1.0, (area.Width - 14.0) / Math.Max(1.0, line.Size.Width)), 1f)));
                    }
                    break;
                }
                case PanelDescription.Kind.Button:
                {
                    bool isHovered = string.Equals(Hovered, element.Id, StringComparison.Ordinal);
                    AppendTileButton(area, isHovered ? 2 : 0, items);
                    if (ButtonLabels(Description.DisplayText(element, _values)) is { } pair)
                    {
                        var label = isHovered ? pair.Hover : pair.Normal;
                        items.Add(new DrawItem(
                            label,
                            new CGRect(
                                Math.Floor(area.X + area.Width / 2.0 - label.Size.Width / 2.0),
                                Math.Floor(area.Y + area.Height / 2.0 - label.Size.Height / 2.0),
                                label.Size.Width,
                                label.Size.Height),
                            whole));
                    }
                    break;
                }
                case PanelDescription.Kind.Meter:
                {
                    double segment = Math.Min(area.Width / 4.0, area.Height * 2.0);
                    items.Add(new DrawItem(_meter, new CGRect(area.X, area.Y, segment, area.Height), new Vector4(0f, 0f, 0.25f, 1f)));
                    items.Add(new DrawItem(_meter, new CGRect(area.X + segment, area.Y, area.Width - segment * 2.0, area.Height), new Vector4(0.25f, 0f, 0.25f, 1f)));
                    items.Add(new DrawItem(_meter, new CGRect(area.X + area.Width - segment, area.Y, segment, area.Height), new Vector4(0.5f, 0f, 0.25f, 1f)));
                    if (Description.MeterFraction(element, _values) is { } fraction)
                    {
                        items.Add(new DrawItem(
                            _meter,
                            new CGRect(area.X + (area.Width - segment) * fraction, area.Y, segment, area.Height),
                            new Vector4(0.75f, 0f, 0.25f, 1f)));
                    }
                    break;
                }
                case PanelDescription.Kind.Image:
                    if (_images.TryGetValue(element.Id, out var image))
                    {
                        items.Add(new DrawItem(image, area, whole));
                    }
                    break;
            }
        }

        var closeArea = CloseRect(canvas);
        bool isCloseHovered = string.Equals(Hovered, "close", StringComparison.Ordinal);
        AppendTileButton(closeArea, isCloseHovered ? 2 : 0, items);
        var activeClose = isCloseHovered ? _closeHover : _close;
        items.Add(new DrawItem(
            activeClose,
            new CGRect(
                Math.Floor(closeArea.X + closeArea.Width / 2.0 - activeClose.Size.Width / 2.0),
                Math.Floor(closeArea.Y + closeArea.Height / 2.0 - activeClose.Size.Height / 2.0),
                activeClose.Size.Width,
                activeClose.Size.Height),
            whole));
        return items;
    }

    private CGPoint Origin(CGSize canvas)
    {
        if (Anchor is { } anchor)
        {
            double width = Description.Frame.Width;
            double height = Description.Frame.Height;
            const double gap = 12.0;
            double x = anchor.X + anchor.Width + gap;
            if (x + width > canvas.Width)
            {
                x = anchor.X - gap - width;
            }

            double bottom = Math.Min(canvas.Height, LotHeight ?? canvas.Height);
            double y = anchor.Y + anchor.Height / 2.0 - height / 2.0;
            return new CGPoint(
                Math.Round(Math.Max(0.0, Math.Min(x, canvas.Width - width)), MidpointRounding.AwayFromZero),
                Math.Round(Math.Max(0.0, Math.Min(y, bottom - height)), MidpointRounding.AwayFromZero));
        }

        double left = (canvas.Width - PanelDescription.LogicalWidth) / 2.0;
        double top = (canvas.Height - PanelDescription.LogicalHeight) / 2.0;
        return new CGPoint(Math.Max(0.0, left) + Description.Frame.X, Math.Max(0.0, top) + Description.Frame.Y);
    }

    private CGRect ElementRect(PanelDescription.Element element, CGSize canvas)
    {
        var point = Origin(canvas);
        var rect = element.Frame.CGRect;
        return new CGRect(rect.X + point.X, rect.Y + point.Y, rect.Width, rect.Height);
    }

    private CGRect CloseRect(CGSize canvas)
    {
        var panel = PanelRect(canvas);
        double width = Math.Max(100.0, Math.Ceiling(_close.Size.Width) + 20.0);
        return new CGRect(Math.Floor(panel.X + panel.Width / 2.0 - width / 2.0), panel.Y + panel.Height - 41.0, width, 33.0);
    }

    private OriginalGameplayOverlay.Image[] Lines(string text, int width)
    {
        string key = $"{width}\0{text}";
        if (_textCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var paragraphs = text.Split('\n', StringSplitOptions.None);
        var wrapped = new List<string>();
        foreach (var line in paragraphs)
        {
            try
            {
                wrapped.AddRange(_font.WrappedLines(line, Math.Max(8, width)));
            }
            catch
            {
                wrapped.Add(line);
            }
        }

        var rendered = new List<OriginalGameplayOverlay.Image>(wrapped.Count);
        foreach (var line in wrapped)
        {
            try
            {
                rendered.Add(OriginalGameplayOverlay.Text(line, _font, _device, NormalTextColor));
            }
            catch
            {
            }
        }

        var array = rendered.ToArray();
        if (_textCache.Count > 256)
        {
            _textCache.Clear();
        }

        _textCache[key] = array;
        return array;
    }

    private (OriginalGameplayOverlay.Image Normal, OriginalGameplayOverlay.Image Hover)? ButtonLabels(string text)
    {
        if (_buttonLabelCache.TryGetValue(text, out var cached))
        {
            return cached;
        }

        string singleLine = text
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal);

        try
        {
            var normal = OriginalGameplayOverlay.Text(singleLine, ButtonFont, _device, NormalTextColor);
            var hover = OriginalGameplayOverlay.Text(singleLine, ButtonFont, _device, HoverTextColor);
            if (_buttonLabelCache.Count > 128)
            {
                _buttonLabelCache.Clear();
            }

            var pair = (normal, hover);
            _buttonLabelCache[text] = pair;
            return pair;
        }
        catch
        {
            return null;
        }
    }

    private OriginalGameplayOverlay.Image WellImage(int width, int height)
    {
        int w = Math.Max(16, width);
        int h = Math.Max(16, height);
        int scale = SimsVectorUiKit.CurrentScale();
        var key = (w, h, scale);
        if (_wellCache.TryGetValue(key, out var cached))
            return cached;

        var img = SimsVectorUiKit.RenderRecessedWell(w, h, cornerR: 6f, scale: scale);
        var created = OriginalGameplayOverlay.CreateImage(_device, img.PixelWidth, img.PixelHeight, img.Rgba, img.LogicalSize);
        if (_wellCache.Count > 64) _wellCache.Clear();
        _wellCache[key] = created;
        return created;
    }

    private void AppendTileButton(CGRect bounds, int frameIndex, List<DrawItem> items)
    {
        int w = Math.Max(24, (int)Math.Round(bounds.Width));
        int h = Math.Max(18, (int)Math.Round(bounds.Height));
        int scale = SimsVectorUiKit.CurrentScale();
        var key = (w, h, scale);
        if (!_buttonCache.TryGetValue(key, out var strip))
        {
            var rendered = SimsVectorUiKit.RenderSlantedButtonStrip(w, h, highlighted: false, scale: scale);
            strip = OriginalGameplayOverlay.CreateImage(_device, rendered.PixelWidth, rendered.PixelHeight, rendered.Rgba, rendered.LogicalSize);
            if (_buttonCache.Count > 64) _buttonCache.Clear();
            _buttonCache[key] = strip;
        }

        items.Add(new DrawItem(
            strip,
            bounds,
            new Vector4(Math.Clamp(frameIndex, 0, 3) * 0.25f, 0f, 0.25f, 1f)));
    }

    private static SimsBitmapFont? TryLoadFont(GameContent content, string path)
    {
        try
        {
            return new SimsBitmapFont(content.Read(path));
        }
        catch
        {
            return null;
        }
    }

    private static bool DictionariesEqual(Dictionary<string, string> lhs, IReadOnlyDictionary<string, string> rhs)
    {
        if (lhs.Count != rhs.Count)
        {
            return false;
        }

        foreach (var (key, value) in lhs)
        {
            if (!rhs.TryGetValue(key, out var other) || !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(CGRect rect, CGPoint point) =>
        rect.Width > 0 && rect.Height > 0 &&
        point.X >= rect.X && point.X < rect.X + rect.Width &&
        point.Y >= rect.Y && point.Y < rect.Y + rect.Height;
}
