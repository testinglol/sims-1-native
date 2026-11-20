using System.Diagnostics;
using System.Numerics;

namespace SimsSDL.UI;

public sealed class OriginalCameraOverlay
{
    public abstract record Action
    {
        private protected Action() { }

        public sealed record Size(int Value) : Action;
        public sealed record CustomSize : Action
        {
            public static readonly CustomSize Instance = new();
        }
        public sealed record Quality(int Value) : Action;
        public sealed record Album : Action
        {
            public static readonly Album Instance = new();
        }
    }

    private enum PopupTopic
    {
        Size,
        Quality
    }

    private sealed record ButtonSpec(Action Action, OriginalGameplayOverlay.Image Image, CGPoint Origin, string Label);

    private readonly ButtonSpec[] _buttons;
    private readonly OriginalGameplayOverlay.Image _universal;
    private readonly OriginalGameplayOverlay.Image _vectorUniversal;
    private OriginalGameplayOverlay.Image ActiveUniversal => SimsBitmapFont.SharpTextEnabled ? _vectorUniversal : _universal;
    private readonly OriginalGameplayOverlay.Image _patch;
    private readonly OriginalGameplayOverlay.Image _toothpickLeft;
    private readonly OriginalGameplayOverlay.Image _toothpickRight;
    private readonly OriginalGameplayOverlay.Image _popupTiles;
    private readonly OriginalGameplayOverlay.Image _popupSizeDetail;
    private readonly OriginalGameplayOverlay.Image _popupQualityDetail;
    private readonly SimsBitmapFont _popupTitleFont;
    private readonly SimsBitmapFont _popupBodyFont;
    private readonly SimsBitmapFont _customSizeFont;
    private readonly OriginalGameplayOverlay.Image _popupSizeTitle;
    private readonly OriginalGameplayOverlay.Image[] _popupSizeBody;
    private readonly OriginalGameplayOverlay.Image _popupQualityTitle;
    private readonly OriginalGameplayOverlay.Image[] _popupQualityBody;
    private readonly UIDevice _device;
    private readonly SimsBitmapFont _tooltipFont;
    private (string Text, OriginalGameplayOverlay.Image Image)? _tooltipImage;
    private string? _tooltipText;
    private double? _tooltipStartedAt;
    private bool _tooltipQuickTips = true;
    private CGPoint _tooltipAnchor = new(0, 0);
    private Action? _suppressedTooltipAction;
    private Action? _hoveredAction;
    private bool _modeHovered;
    private PopupTopic? _popupTopic;
    private double? _popupOpenedAt;
    private (string Text, OriginalGameplayOverlay.Image Main, OriginalGameplayOverlay.Image Shadow)? _customLabelCache;
    private readonly OriginalGameplayOverlay.Image _white = new(
        new UITexture(1, 1, [255, 255, 255, 255]),
        new CGSize(1, 1));
    private bool _visible;
    private int _size = 1;
    private int _quality = 1;

    public OriginalGameplayOverlay.Image ModeButton { get; }

    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            if (!_visible)
            {
                ClearHover();
            }
        }
    }

    public int Size
    {
        get => _size;
        set
        {
            _size = value;
            DismissTooltipOnClick();
        }
    }

    public CGSize CustomDimensions { get; set; } = new(320, 240);

    public int Quality
    {
        get => _quality;
        set
        {
            _quality = value;
            DismissTooltipOnClick();
        }
    }

    public CGPoint? Pointer { get; set; }
    public CGRect? CaptureRect { get; private set; }
    public bool PendingWhoosh { get; private set; }

    public OriginalCameraOverlay(GameContent content, UIDevice device, OriginalUIStrings strings)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(strings);

        _device = device;
        SimsBitmapFont tipFont;
        try
        {
            tipFont = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_07.ffn"));
        }
        catch
        {
            tipFont = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_08.ffn"));
        }
        _tooltipFont = tipFont;
        var titleFont = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_12.ffn"));
        var bodyFont = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_09.ffn"));
        _popupTitleFont = titleFont;
        _popupBodyFont = bodyFont;
        _customSizeFont = bodyFont;

        var universalTarga = new SimsTarga(content.Read("UIGraphics/cpanel/Backgrounds/UniversalBack.tga"));
        _universal = OriginalGameplayOverlay.CreateImage(device, universalTarga.Width, universalTarga.Height, universalTarga.Rgba);
        try { _vectorUniversal = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderFittedPanel("universalback", scale: 8)); } catch { _vectorUniversal = _universal; }

        OriginalGameplayOverlay.Image UploadBackground(string name)
        {
            var bitmap = new SimsBitmap(content.Read($"UIGraphics/CPanel/Backgrounds/{name}.bmp"));
            return OriginalGameplayOverlay.CreateImage(device, bitmap.Width, bitmap.Height, bitmap.Rgba, magenta: true);
        }

        _patch = UploadBackground("CameraPatch");
        _toothpickLeft = UploadBackground("CameraToothpkLeft");
        _toothpickRight = UploadBackground("CameraToothpkRight");
        _popupTiles = UploadBackground("PopupInfoTiles");
        _popupSizeDetail = UploadBackground("CamPopupDetail");
        _popupQualityDetail = UploadBackground("CamPopupQualDetail");

        OriginalGameplayOverlay.Image UploadButton(string name)
        {
            var bitmap = new SimsBitmap(content.Read($"UIGraphics/CPanel/Buttons/{name}.bmp"));
            if (SimsBitmapFont.SharpTextEnabled)
            {
                int w1x = Math.Max(1, bitmap.Width / 4);
                int h1x = Math.Max(1, bitmap.Height);
                if (string.Equals(name, "Camera", StringComparison.OrdinalIgnoreCase))
                    return SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderCameraModeButtonStrip(w1x, h1x, scale: 4));
                if (string.Equals(name, "CamViewAlbum", StringComparison.OrdinalIgnoreCase))
                    return SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderCameraViewAlbumStrip(w1x, h1x, scale: 4));
            }
            return OriginalGameplayOverlay.CreateImage(device, bitmap.Width, bitmap.Height, bitmap.Rgba, magenta: true);
        }

        ModeButton = UploadButton("Camera");

        CGPoint CamPoint(int index, CGPoint fallback) =>
            OriginalUIStrings.Point(strings.Camera(index, string.Empty)) ?? fallback;

        (Action Action, string Asset, CGPoint Origin, string Label)[] specs =
        [
            (new Action.Size(0), "CamFrameSmall", CamPoint(0, new CGPoint(82, 70)), strings.Camera(1, "Small-Sized Snapshot")),
            (new Action.Size(1), "CamFrameMed", CamPoint(2, new CGPoint(82, 36)), strings.Camera(3, "Medium-Sized Snapshot")),
            (new Action.Size(2), "CamFrameLarge", CamPoint(4, new CGPoint(82, 4)), strings.Camera(5, "Large-Sized Snapshot")),
            (Action.CustomSize.Instance, "CamFrameCust", CamPoint(6, new CGPoint(11, 11)), strings.Camera(7, "Custom-Sized Snapshot")),
            (Action.Album.Instance, "CamViewAlbum", CamPoint(8, new CGPoint(226, 35)), strings.Camera(9, "View Photo Album")),
            (new Action.Quality(0), "CamQualLow", CamPoint(10, new CGPoint(154, 70)), strings.Camera(11, "Low Quality")),
            (new Action.Quality(1), "CamQualMed", CamPoint(12, new CGPoint(154, 36)), strings.Camera(13, "Medium Quality")),
            (new Action.Quality(2), "CamQualHigh", CamPoint(14, new CGPoint(154, 4)), strings.Camera(15, "High Quality"))
        ];

        _buttons = new ButtonSpec[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            _buttons[i] = new ButtonSpec(specs[i].Action, UploadButton(specs[i].Asset), specs[i].Origin, specs[i].Label);
        }

        OriginalGameplayOverlay.Image MakeText(string text, SimsBitmapFont font, (byte R, byte G, byte B) color) =>
            OriginalGameplayOverlay.CreateImage(device, font.RasterizeHiDpi(text, color));

        OriginalGameplayOverlay.Image[] MakeLines(string raw)
        {
            var normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var paragraphs = normalized.Split('\n', StringSplitOptions.None);
            var lines = new List<OriginalGameplayOverlay.Image>();
            foreach (var paragraph in paragraphs)
            {
                var wrapped = bodyFont.WrappedLines(paragraph, 357);
                if (wrapped.Count == 0)
                {
                    lines.Add(new OriginalGameplayOverlay.Image(new UITexture(1, 1, [0, 0, 0, 0]), new CGSize(0, 0)));
                    continue;
                }

                foreach (var line in wrapped)
                {
                    lines.Add(string.IsNullOrEmpty(line)
                        ? new OriginalGameplayOverlay.Image(new UITexture(1, 1, [0, 0, 0, 0]), new CGSize(0, 0))
                        : MakeText(line, bodyFont, (195, 205, 205)));
                }
            }

            return lines.ToArray();
        }

        _popupSizeTitle = MakeText(strings.Camera(16, "Snapshot Sizes"), titleFont, (255, 255, 255));
        _popupSizeBody = MakeLines(strings.Camera(17, "Snapshots come in three sizes; small, medium and large."));
        _popupQualityTitle = MakeText(strings.Camera(18, "Quality Settings"), titleFont, (255, 255, 255));
        _popupQualityBody = MakeLines(strings.Camera(19, "Choose low, medium, or high quality settings for your photo."));
    }

    public bool ConsumeWhoosh()
    {
        if (!PendingWhoosh)
        {
            return false;
        }

        PendingWhoosh = false;
        return true;
    }

    public void SelectPresetSize(int index)
    {
        Size = index;
        CustomDimensions = PresetSize(index);
    }

    /// <summary>Clamps the camera frame between 160x120 and 640x480 pixels, switching to custom frame mode
    /// if dimensions differ from standard presets.</summary>
    public void ResizeCustomFrame(double dx, double dy)
    {
        var frameBase = _size == 3 ? CustomDimensions : PresetSize(_size);
        var w = Math.Min(640.0, Math.Max(160.0, frameBase.Width + dx));
        var h = Math.Min(480.0, Math.Max(120.0, frameBase.Height + dy));
        CustomDimensions = new CGSize(w, h);
        if (w == 160.0 && h == 120.0)
        {
            Size = 0;
        }
        else if (w == 320.0 && h == 240.0)
        {
            Size = 1;
        }
        else if (w == 640.0 && h == 480.0)
        {
            Size = 2;
        }
        else
        {
            Size = 3;
        }
    }

    public CGSize CurrentFrameSize => _size == 3 ? CustomDimensions : PresetSize(_size);

    /// <summary>Calculates the camera mode button rectangle relative to the universal control panel anchor.</summary>
    public CGRect ModeRect(CGSize canvas) => new(175, canvas.Height - 183 + 95, 29, 29);

    public Action? Hit(CGPoint point, CGSize canvas)
    {
        foreach (var button in _buttons)
        {
            if (IsEnabled(button.Action) && Contains(ButtonRect(button, canvas), point))
            {
                return button.Action;
            }
        }

        return null;
    }

    public IReadOnlyList<(string Label, CGRect Rect)> Controls(CGSize canvas)
    {
        var result = new List<(string Label, CGRect Rect)>
        {
            ("Camera Mode", ModeRect(canvas))
        };

        if (_visible)
        {
            foreach (var button in _buttons)
            {
                if (IsEnabled(button.Action))
                {
                    result.Add((button.Label, ButtonRect(button, canvas)));
                }
            }
        }

        return result;
    }

    public void Hover(CGPoint point, CGSize canvas)
    {
        _modeHovered = Contains(ModeRect(canvas), point);
        ButtonSpec? control = null;
        if (_visible)
        {
            foreach (var button in _buttons)
            {
                if (IsEnabled(button.Action) && Contains(ButtonRect(button, canvas), point))
                {
                    control = button;
                    break;
                }
            }
        }

        if (control?.Action != _suppressedTooltipAction)
        {
            _suppressedTooltipAction = null;
        }

        _hoveredAction = control?.Action;
        if (control is { } activeControl)
        {
            PopupTopic? topic = activeControl.Action switch
            {
                Action.Quality => PopupTopic.Quality,
                Action.Size or Action.CustomSize => PopupTopic.Size,
                _ => null
            };

            if (topic is { } activeTopic)
            {
                if (_popupTopic is null)
                {
                    _popupOpenedAt = MonotonicNowSeconds();
                    PendingWhoosh = true;
                }

                _popupTopic = activeTopic;
            }
            else
            {
                if (_popupTopic is not null)
                {
                    PendingWhoosh = true;
                }

                _popupTopic = null;
                _popupOpenedAt = null;
            }
        }
        else if (point.Y < canvas.Height - 100)
        {
            if (_popupTopic is not null)
            {
                PendingWhoosh = true;
            }

            _popupTopic = null;
            _popupOpenedAt = null;
        }

        string? text = control is not null && control.Action != _suppressedTooltipAction ? control.Label : null;
        if (!string.Equals(text, _tooltipText, StringComparison.Ordinal))
        {
            _tooltipText = text;
            _tooltipStartedAt = null;
        }

        if (text is not null)
        {
            _tooltipAnchor = point;
        }
    }

    public void ClearHover()
    {
        _modeHovered = false;
        _hoveredAction = null;
        _suppressedTooltipAction = null;
        _popupTopic = null;
        _popupOpenedAt = null;
        _tooltipText = null;
        _tooltipStartedAt = null;
    }

    public IReadOnlyList<OriginalGameplayOverlay.Part> TooltipParts(CGSize canvas, bool quickTips)
    {
        if (_tooltipQuickTips != quickTips)
        {
            _tooltipStartedAt = null;
            _tooltipQuickTips = quickTips;
        }

        if (!_visible || _tooltipText is not { } text)
        {
            _tooltipStartedAt = null;
            return [];
        }

        if (PlacementTooltip.Progress(ref _tooltipStartedAt, quickTips) is not { } progress)
        {
            return [];
        }

        try
        {
            OriginalGameplayOverlay.Image image;
            if (_tooltipImage is { } cached && string.Equals(cached.Text, text, StringComparison.Ordinal))
            {
                image = cached.Image;
            }
            else
            {
                image = OriginalGameplayOverlay.CreateImage(_device, PlacementTooltip.RasterizeHiDpi(text, _tooltipFont));
                _tooltipImage = (text, image);
            }

            var rect = PlacementTooltip.Rect(image.Size, new CGPoint(_tooltipAnchor.X, _tooltipAnchor.Y - 1), canvas);
            var lift = (1.0 - progress) * 2.0;
            return
            [
                new OriginalGameplayOverlay.Part(
                    image,
                    new CGRect(rect.X, rect.Y + lift, rect.Width, rect.Height),
                    new Vector4(0f, 0f, 1f, 1f),
                    (float)Math.Clamp(progress, 0.0, 1.0))
            ];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Assembles the camera control panel background, compositing the universal backdrop with the
    /// camera mode patch offset at (165, 83).</summary>
    public IReadOnlyList<OriginalGameplayOverlay.Part> PanelParts(CGSize canvas)
    {
        var top = canvas.Height - 150;
        var parts = PopupParts(canvas);
        parts.Add(new OriginalGameplayOverlay.Part(ActiveUniversal, new CGRect(0, top - 33, _universal.Size.Width, _universal.Size.Height)));
        parts.Add(new OriginalGameplayOverlay.Part(_patch, new CGRect(165, top - 33 + 83, _patch.Size.Width, _patch.Size.Height)));
        parts.Add(new OriginalGameplayOverlay.Part(_toothpickLeft, new CGRect(220 + 133, canvas.Height - 100 + 6, _toothpickLeft.Size.Width, _toothpickLeft.Size.Height)));
        parts.Add(new OriginalGameplayOverlay.Part(_toothpickRight, new CGRect(220 + 206, canvas.Height - 100 + 6, _toothpickRight.Size.Width, _toothpickRight.Size.Height)));
        return parts;
    }

    public IReadOnlyList<OriginalGameplayOverlay.Part> Draws(CGSize canvas, bool albumVisible)
    {
        if (albumVisible)
        {
            ClearHover();
        }

        float modeUV = _visible ? 0.25f : _modeHovered ? 0.5f : 0f;
        var parts = new List<OriginalGameplayOverlay.Part>
        {
            new(ModeButton, ModeRect(canvas), new Vector4(modeUV, 0f, 0.25f, 1f))
        };

        CaptureRect = null;
        if (!_visible)
        {
            return parts;
        }

        foreach (var button in _buttons)
        {
            bool enabled = IsEnabled(button.Action);
            bool selected = button.Action switch
            {
                Action.Size s => s.Value == _size,
                Action.CustomSize => _size == 3,
                Action.Quality q => q.Value == _quality,
                Action.Album => albumVisible,
                _ => false
            };

            float uvX = !enabled ? 0.75f : selected ? 0.25f : _hoveredAction == button.Action ? 0.5f : 0f;
            var buttonRect = ButtonRect(button, canvas);
            parts.Add(new OriginalGameplayOverlay.Part(button.Image, buttonRect, new Vector4(uvX, 0f, 0.25f, 1f)));

            if (button.Action is Action.CustomSize && _size == 3)
            {
                var labelText = $"{(int)CustomDimensions.Width}x{(int)CustomDimensions.Height}";
                if (_customLabelCache is null || !string.Equals(_customLabelCache.Value.Text, labelText, StringComparison.Ordinal))
                {
                    try
                    {
                        var mainImg = OriginalGameplayOverlay.CreateImage(_device, _customSizeFont.RasterizeHiDpi(labelText, (195, 205, 205)));
                        var shadowImg = OriginalGameplayOverlay.CreateImage(_device, _customSizeFont.RasterizeHiDpi(labelText, (0, 0, 0)));
                        _customLabelCache = (labelText, mainImg, shadowImg);
                    }
                    catch
                    {
                    }
                }

                if (_customLabelCache is { } cached)
                {
                    var lx = Math.Floor(buttonRect.X + buttonRect.Width / 2.0 - cached.Main.Size.Width / 2.0);
                    var ly = buttonRect.Y + buttonRect.Height - cached.Main.Size.Height;
                    parts.Add(new OriginalGameplayOverlay.Part(cached.Shadow, new CGRect(lx + 1, ly + 1, cached.Shadow.Size.Width, cached.Shadow.Size.Height)));
                    parts.Add(new OriginalGameplayOverlay.Part(cached.Main, new CGRect(lx, ly, cached.Main.Size.Width, cached.Main.Size.Height)));
                }
            }
        }

        if (Pointer is { } pointer && !albumVisible && pointer.Y < canvas.Height - 183)
        {
            var requested = CurrentFrameSize;
            var width = Math.Min(requested.Width, canvas.Width);
            var height = Math.Min(requested.Height, canvas.Height - 183);
            var rect = new CGRect(
                Math.Min(Math.Max(0.0, pointer.X - width / 2.0), canvas.Width - width),
                Math.Min(Math.Max(0.0, pointer.Y - height / 2.0), canvas.Height - 183 - height),
                width,
                height);
            CaptureRect = rect;

            parts.Add(new OriginalGameplayOverlay.Part(_white, new CGRect(rect.X, rect.Y, rect.Width, 1)));
            parts.Add(new OriginalGameplayOverlay.Part(_white, new CGRect(rect.X, rect.Y + rect.Height - 1, rect.Width, 1)));
            parts.Add(new OriginalGameplayOverlay.Part(_white, new CGRect(rect.X, rect.Y, 1, rect.Height)));
            parts.Add(new OriginalGameplayOverlay.Part(_white, new CGRect(rect.X + rect.Width - 1, rect.Y, 1, rect.Height)));
        }

        return parts;
    }

    private List<OriginalGameplayOverlay.Part> PopupParts(CGSize canvas)
    {
        if (!_visible || _popupTopic is not { } topic)
        {
            return [];
        }

        var title = topic == PopupTopic.Quality ? _popupQualityTitle : _popupSizeTitle;
        var body = topic == PopupTopic.Quality ? _popupQualityBody : _popupSizeBody;
        var detail = topic == PopupTopic.Quality ? _popupQualityDetail : _popupSizeDetail;
        int textHeight = (int)title.Size.Height + body.Length * _popupBodyFont.LineHeight;
        double height = Math.Max(127, textHeight + 24);
        const double width = 540.0;
        double panelBottom = canvas.Height - 100;
        double elapsed = _popupOpenedAt is { } openedAt ? MonotonicNowSeconds() - openedAt : 0.25;
        double progress = Math.Min(1.0, Math.Max(0.0, elapsed / 0.25));
        double visibleHeight = Math.Round(height * progress, MidpointRounding.AwayFromZero);
        if (visibleHeight <= 0.0)
        {
            return [];
        }

        double x = Math.Min(Math.Max(8.0, 220.0 + 580.0 - width), canvas.Width - width);
        double y = panelBottom - visibleHeight;
        var parts = new List<OriginalGameplayOverlay.Part>();
        double tileW = _popupTiles.Size.Width / 3.0;
        double tileH = _popupTiles.Size.Height / 3.0;
        double[] xs = [x, x + tileW, x + width - tileW, x + width];
        double[] ys = [y, y + tileH, y + height - tileH, y + height];

        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                double cellX = xs[col];
                double cellY = ys[row];
                double cellW = xs[col + 1] - xs[col];
                double cellH = ys[row + 1] - ys[row];
                if (cellY >= panelBottom)
                {
                    continue;
                }

                double clippedH = Math.Min(cellY + cellH, panelBottom) - cellY;
                float vScale = (float)(clippedH / Math.Max(1.0, cellH)) / 3f;
                parts.Add(new OriginalGameplayOverlay.Part(
                    _popupTiles,
                    new CGRect(cellX, cellY, cellW, clippedH),
                    new Vector4(col / 3f, row / 3f, 1f / 3f, vScale)));
            }
        }

        double detailX = x + Math.Floor((168.0 - detail.Size.Width) / 2.0);
        double detailY = y + Math.Floor((height - detail.Size.Height) / 2.0);
        if (detailY < panelBottom)
        {
            double clippedH = Math.Min(detail.Size.Height, panelBottom - detailY);
            parts.Add(new OriginalGameplayOverlay.Part(
                detail,
                new CGRect(detailX, detailY, detail.Size.Width, clippedH),
                new Vector4(0f, 0f, 1f, (float)(clippedH / Math.Max(1.0, detail.Size.Height)))));
        }

        double titleY = y + 12.0;
        if (titleY + title.Size.Height <= panelBottom)
        {
            parts.Add(new OriginalGameplayOverlay.Part(title, new CGRect(x + 178.0, titleY, title.Size.Width, title.Size.Height)));
        }

        double lineY = titleY + _popupTitleFont.LineHeight;
        foreach (var line in body)
        {
            if (line.Size.Width > 0 && lineY + line.Size.Height <= panelBottom)
            {
                parts.Add(new OriginalGameplayOverlay.Part(line, new CGRect(x + 178.0, lineY, line.Size.Width, line.Size.Height)));
            }

            lineY += _popupBodyFont.LineHeight;
        }

        return parts;
    }

    private void DismissTooltipOnClick()
    {
        if (_hoveredAction is not null)
        {
            _suppressedTooltipAction = _hoveredAction;
        }

        _tooltipText = null;
        _tooltipStartedAt = null;
    }

    private bool IsEnabled(Action action) => action is not Action.CustomSize || _size == 3;

    private static CGRect ButtonRect(ButtonSpec button, CGSize canvas) =>
        new(220.0 + button.Origin.X, canvas.Height - 100.0 + button.Origin.Y, button.Image.Size.Width / 4.0, button.Image.Size.Height);

    private static CGSize PresetSize(int index) => Math.Max(0, Math.Min(2, index)) switch
    {
        0 => new CGSize(160, 120),
        1 => new CGSize(320, 240),
        _ => new CGSize(640, 480)
    };

    private static bool Contains(CGRect rect, CGPoint point) =>
        rect.Width > 0 && rect.Height > 0 &&
        point.X >= rect.X && point.X < rect.X + rect.Width &&
        point.Y >= rect.Y && point.Y < rect.Y + rect.Height;

    private static double MonotonicNowSeconds() =>
        Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
