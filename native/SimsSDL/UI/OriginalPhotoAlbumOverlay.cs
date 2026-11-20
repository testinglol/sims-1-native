using System.Numerics;
using SimsSDL;

namespace SimsSDL.UI;

public sealed class OriginalPhotoAlbumOverlay
{
    public enum Action
    {
        Done,
        Delete,
        Previous,
        Next,
        First,
        Last
    }

    private readonly struct ButtonText
    {
        public OriginalGameplayOverlay.Image Normal { get; init; }
        public OriginalGameplayOverlay.Image Hover { get; init; }
        public OriginalGameplayOverlay.Image Disabled { get; init; }
        public OriginalGameplayOverlay.Image Shadow { get; init; }
    }

    private readonly record struct ButtonSpec(
        Action Action,
        OriginalGameplayOverlay.Image Image,
        CGPoint Origin,
        string Label,
        string Tooltip);

    private readonly OriginalGameplayOverlay.Image _background;
    private readonly List<ButtonSpec> _buttons;
    private readonly Dictionary<Action, ButtonText> _buttonLabels;
    private readonly SimsBitmapFont _font;
    private readonly OriginalGameplayOverlay.Image _emptyLabel;
    private readonly UIDevice _device;
    private readonly OriginalUIStrings _strings;
    private readonly CGRect _captionRect;

    private PhotoAlbum? _album;
    private OriginalGameplayOverlay.Image? _photo;
    private List<OriginalGameplayOverlay.Image> _text = [];
    private string _captionText = string.Empty;
    private bool _captionDirty;
    private int _index;
    private string? _imageFailure;
    private Action? _hoveredAction;
    private Action? _suppressedTooltipAction;
    private (string Text, OriginalGameplayOverlay.Image Image)? _tooltipImage;
    private string? _tooltipText;
    private double? _tooltipStartedAt;
    private bool _tooltipQuickTips = true;
    private CGPoint _tooltipAnchor = CGPoint.Zero;

    public string CurrentCaption => _captionText;
    public bool EditingCaption { get; private set; }
    public bool Visible { get; private set; }
    public int Count => _album?.Photos.Count ?? 0;

    public OriginalPhotoAlbumOverlay(GameContent content, SimsBitmapFont font, UIDevice device, OriginalUIStrings strings)
    {
        _font = font;
        _device = device;
        _strings = strings;

        SimsBitmapFont btnFont;
        try
        {
            btnFont = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
        }
        catch
        {
            btnFont = font;
        }

        SimsBitmapFont emptyFont;
        try
        {
            emptyFont = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_12.ffn"));
        }
        catch
        {
            emptyFont = font;
        }

        _emptyLabel = OriginalGameplayOverlay.Text(
            strings.Album(22, "Empty Photo Album"),
            emptyFont,
            device,
            (0, 0, 0));

        OriginalGameplayOverlay.Image Upload(string name)
        {
            var bitmap = new SimsBitmap(content.Read("UIGraphics/CPanel/" + name + ".bmp"));
            return OriginalGameplayOverlay.CreateImage(device, bitmap.Width, bitmap.Height, bitmap.Rgba, magenta: true);
        }

        ButtonText MakeBtnText(string value)
        {
            OriginalGameplayOverlay.Image Raster((byte R, byte G, byte B) color) =>
                OriginalGameplayOverlay.Text(value, btnFont, device, color);

            return new ButtonText
            {
                Normal = Raster((195, 205, 205)),
                Hover = Raster((255, 255, 255)),
                Disabled = Raster((64, 93, 95)),
                Shadow = Raster((0, 0, 0))
            };
        }

        _background = Upload("Backgrounds/ScrapBack");
        var captionSize = OriginalUIStrings.Point(strings.Album(14, "(520;72)")) ?? new CGPoint(520, 72);
        var captionOrigin = OriginalUIStrings.Point(strings.Album(15, "(90;435)")) ?? new CGPoint(90, 435);
        _captionRect = new CGRect(captionOrigin.X, captionOrigin.Y, Math.Max(1, captionSize.X), Math.Max(1, captionSize.Y));

        CGPoint AlbumPoint(int idx, CGPoint fallback) =>
            OriginalUIStrings.Point(strings.Album(idx, string.Empty)) ?? fallback;

        (Action Action, string SpriteName, CGPoint Origin, string Label, string Tooltip)[] specs =
        [
            (Action.Done, "ScrapDone", AlbumPoint(0, new CGPoint(129, 512)), strings.Album(1, "Done"), strings.Album(2, "Close Photo Album")),
            (Action.Delete, "ScrapDelete", AlbumPoint(3, new CGPoint(442, 511)), strings.Album(4, "Delete"), strings.Album(5, "Delete this photo")),
            (Action.Previous, "ScrapLeft", AlbumPoint(6, new CGPoint(58, 442)), strings.Album(7, "Previous Photo"), strings.Album(7, "Previous Photo")),
            (Action.Next, "ScrapRight", AlbumPoint(8, new CGPoint(614, 442)), strings.Album(9, "Next Photo"), strings.Album(9, "Next Photo")),
            (Action.First, "ScrapHome", AlbumPoint(10, new CGPoint(19, 442)), strings.Album(11, "First Photo"), strings.Album(11, "First Photo")),
            (Action.Last, "ScrapEnd", AlbumPoint(12, new CGPoint(643, 442)), strings.Album(13, "Last Photo"), strings.Album(13, "Last Photo"))
        ];

        _buttons = new List<ButtonSpec>(specs.Length);
        var labels = new Dictionary<Action, ButtonText>();
        foreach (var spec in specs)
        {
            _buttons.Add(new ButtonSpec(spec.Action, Upload("Buttons/" + spec.SpriteName), spec.Origin, spec.Label, spec.Tooltip));
            if (spec.Action is Action.Done or Action.Delete)
            {
                labels[spec.Action] = MakeBtnText(spec.Label);
            }
        }
        _buttonLabels = labels;
    }

    public void Open(string directory, bool newest = false)
    {
        _album = new PhotoAlbum(directory);
        _index = newest ? Math.Max(0, Count - 1) : 0;
        Visible = true;
        EditingCaption = false;
        _captionDirty = false;
        _hoveredAction = null;
        _suppressedTooltipAction = null;
        _tooltipText = null;
        _tooltipStartedAt = null;
        Refresh();
    }

    public void Close()
    {
        CommitCaptionIfNeeded();
        Visible = false;
        EditingCaption = false;
        _captionDirty = false;
        _hoveredAction = null;
        _suppressedTooltipAction = null;
        _tooltipText = null;
        _tooltipStartedAt = null;
        _photo = null;
        _album = null;
        _text = [];
    }

    private void CommitCaptionIfNeeded()
    {
        if (!_captionDirty || _album is null || _index < 0 || _index >= _album.Photos.Count) return;
        _captionDirty = false;
        try
        {
            _album.SetCaption(_captionText, _index);
        }
        catch
        {
        }
    }

    private OriginalGameplayOverlay.Image Label(string value) =>
        OriginalGameplayOverlay.CreateImage(_device, _font.RasterizeHiDpi(value, (255, 255, 255)));

    private void RebuildCaptionImages()
    {
        if (_album is null || _index < 0 || _index >= _album.Photos.Count)
        {
            _text = [];
            return;
        }

        var display = _imageFailure
            ?? (EditingCaption
                ? (_captionText + "|")
                : (string.IsNullOrEmpty(_captionText) ? _strings.AlbumDescription() : _captionText));
        var maxLines = Math.Max(1, (int)_captionRect.Height / Math.Max(1, _font.LineHeight));
        var wrapped = _font.WrappedLines(display, (int)_captionRect.Width);
        var visibleLines = EditingCaption
            ? wrapped.TakeLast(maxLines)
            : wrapped.Take(maxLines);
        _text = visibleLines.Select(Label).ToList();
    }

    private void Refresh()
    {
        _photo = null;
        _imageFailure = null;
        if (_album is not null && _index >= 0 && _index < _album.Photos.Count)
        {
            try
            {
                var image = _album.ImageAt(_index);
                _photo = OriginalGameplayOverlay.CreateImage(_device, image.Width, image.Height, image.Rgba);
            }
            catch
            {
                _imageFailure = "Cannot read this photo";
            }

            var caption = _album.Photos[_index].Caption;
            _captionText = string.IsNullOrEmpty(caption) ? _strings.AlbumDescription() : caption;
            _captionDirty = false;
            RebuildCaptionImages();
        }
        else
        {
            _captionText = string.Empty;
            _captionDirty = false;
            EditingCaption = false;
            _text = [];
        }

        if (_hoveredAction is { } current && !Enabled(current))
        {
            _hoveredAction = null;
            _suppressedTooltipAction = null;
            _tooltipText = null;
            _tooltipStartedAt = null;
        }
    }

    public void UpdateCaption(string newCaption)
    {
        if (Count <= 0 || !EditingCaption) return;
        _captionText = newCaption.Length > 512 ? newCaption[..512] : newCaption;
        _captionDirty = true;
        RebuildCaptionImages();
    }

    public void Perform(Action action)
    {
        if (!Enabled(action)) return;
        _suppressedTooltipAction = action;
        _tooltipText = null;
        _tooltipStartedAt = null;
        CommitCaptionIfNeeded();
        EditingCaption = false;
        switch (action)
        {
            case Action.Done:
                Close();
                return;
            case Action.Delete:
                _album?.Delete(_index);
                _index = Math.Min(_index, Math.Max(0, Count - 1));
                break;
            case Action.Previous:
                _index = Math.Max(0, _index - 1);
                break;
            case Action.Next:
                _index = Math.Min(Math.Max(0, Count - 1), _index + 1);
                break;
            case Action.First:
                _index = 0;
                break;
            case Action.Last:
                _index = Math.Max(0, Count - 1);
                break;
        }
        Refresh();
    }

    private bool Enabled(Action action) => action switch
    {
        Action.Done => true,
        Action.Delete => Count > 0,
        Action.Previous or Action.First => Count > 0 && _index > 0,
        Action.Next or Action.Last => Count > 0 && _index + 1 < Count,
        _ => false
    };

    private (double Scale, CGPoint Origin) Placement(CGSize canvas)
    {
        var scale = Math.Min(1.0, Math.Min((canvas.Width - 16) / 700.0, (canvas.Height - 16) / 560.0));
        return (scale, new CGPoint((canvas.Width - 700 * scale) / 2.0, (canvas.Height - 560 * scale) / 2.0));
    }

    private CGRect Mapped(CGRect rect, CGSize canvas)
    {
        var (scale, origin) = Placement(canvas);
        return new CGRect(
            origin.X + rect.MinX * scale,
            origin.Y + rect.MinY * scale,
            rect.Width * scale,
            rect.Height * scale);
    }

    private static CGRect Bounds(ButtonSpec button) =>
        new(button.Origin.X, button.Origin.Y, button.Image.Size.Width / 4.0, button.Image.Size.Height);

    public bool ClickCaption(CGPoint point, CGSize canvas)
    {
        if (Count <= 0)
        {
            EditingCaption = false;
            return false;
        }

        var captionBox = Mapped(_captionRect, canvas);
        var inside = Contains(captionBox, point);
        if (inside != EditingCaption)
        {
            if (!inside) CommitCaptionIfNeeded();
            EditingCaption = inside;
            try
            {
                RebuildCaptionImages();
            }
            catch
            {
            }
        }
        return inside;
    }

    public Action? Hit(CGPoint point, CGSize canvas)
    {
        if (!Visible) return null;
        foreach (var button in _buttons)
        {
            if (Enabled(button.Action) && Contains(Mapped(Bounds(button), canvas), point))
            {
                return button.Action;
            }
        }
        return null;
    }

    public void Hover(CGPoint point, CGSize canvas)
    {
        ButtonSpec? control = null;
        if (Visible)
        {
            foreach (var button in _buttons)
            {
                if (Enabled(button.Action) && Contains(Mapped(Bounds(button), canvas), point))
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
        var tip = control is { } hit && hit.Action != _suppressedTooltipAction ? hit.Tooltip : null;
        if (!string.Equals(tip, _tooltipText, StringComparison.Ordinal))
        {
            _tooltipText = tip;
            _tooltipStartedAt = null;
        }
        if (tip is not null) _tooltipAnchor = point;
    }

    public void ClearHover()
    {
        _hoveredAction = null;
        _suppressedTooltipAction = null;
        _tooltipText = null;
        _tooltipStartedAt = null;
    }

    public List<OriginalGameplayOverlay.Part> TooltipParts(CGSize canvas, bool quickTips)
    {
        if (_tooltipQuickTips != quickTips)
        {
            _tooltipStartedAt = null;
            _tooltipQuickTips = quickTips;
        }
        if (!Visible || _tooltipText is null)
        {
            _tooltipStartedAt = null;
            return [];
        }

        var progress = PlacementTooltip.Progress(ref _tooltipStartedAt, quickTips);
        if (progress is null) return [];

        try
        {
            OriginalGameplayOverlay.Image image;
            if (_tooltipImage is { } cached && string.Equals(cached.Text, _tooltipText, StringComparison.Ordinal))
            {
                image = cached.Image;
            }
            else
            {
                image = OriginalGameplayOverlay.CreateImage(_device, PlacementTooltip.RasterizeHiDpi(_tooltipText, _font));
                _tooltipImage = (_tooltipText, image);
            }

            var rect = PlacementTooltip.Rect(image.Size, new CGPoint(_tooltipAnchor.X, _tooltipAnchor.Y - 1), canvas);
            if (rect.Height <= 0) return [];
            var lift = (1.0 - progress.Value) * 2.0;
            return
            [
                new OriginalGameplayOverlay.Part(
                    image,
                    new CGRect(rect.MinX, rect.MinY + lift, rect.Width, rect.Height),
                    new Vector4(0f, 0f, 1f, 1f),
                    (float)Math.Clamp(progress.Value, 0.0, 1.0))
            ];
        }
        catch
        {
            return [];
        }
    }

    public List<(string Label, CGRect Rect)> Controls(CGSize canvas)
    {
        if (!Visible) return [];
        var result = new List<(string Label, CGRect Rect)>();
        foreach (var button in _buttons)
        {
            if (Enabled(button.Action))
            {
                result.Add((button.Label, Mapped(Bounds(button), canvas)));
            }
        }
        return result;
    }

    public List<OriginalGameplayOverlay.Part> Draws(CGSize canvas)
    {
        var parts = new List<OriginalGameplayOverlay.Part>
        {
            new(_background, Mapped(new CGRect(0, 0, 700, 560), canvas))
        };

        if (_photo is { } photo)
        {
            var fit = Math.Min(1.0, Math.Min(600.0 / photo.Size.Width, 400.0 / photo.Size.Height));
            parts.Add(new OriginalGameplayOverlay.Part(
                photo,
                Mapped(
                    new CGRect(
                        350 - photo.Size.Width * fit / 2.0,
                        220 - photo.Size.Height * fit / 2.0,
                        photo.Size.Width * fit,
                        photo.Size.Height * fit),
                    canvas)));
        }
        else if (Count == 0)
        {
            parts.Add(new OriginalGameplayOverlay.Part(
                _emptyLabel,
                Mapped(
                    new CGRect(
                        Math.Floor(350 - _emptyLabel.Size.Width / 2.0),
                        Math.Floor(220 - _emptyLabel.Size.Height / 2.0),
                        _emptyLabel.Size.Width,
                        _emptyLabel.Size.Height),
                    canvas)));
        }

        if (Count > 0)
        {
            for (var row = 0; row < _text.Count; row++)
            {
                var line = _text[row];
                parts.Add(new OriginalGameplayOverlay.Part(
                    line,
                    Mapped(
                        new CGRect(_captionRect.MinX, _captionRect.MinY + row * _font.LineHeight, line.Size.Width, line.Size.Height),
                        canvas)));
            }
        }

        foreach (var button in _buttons)
        {
            var isEnabled = Enabled(button.Action);
            var isHovered = isEnabled && _hoveredAction == button.Action;
            var uvX = !isEnabled ? 0.75f : isHovered ? 0.5f : 0f;
            var rect = Bounds(button);
            parts.Add(new OriginalGameplayOverlay.Part(button.Image, Mapped(rect, canvas), new Vector4(uvX, 0f, 0.25f, 1f)));
            if (_buttonLabels.TryGetValue(button.Action, out var textImages))
            {
                var main = !isEnabled ? textImages.Disabled : isHovered ? textImages.Hover : textImages.Normal;
                var shadow = textImages.Shadow;
                var lx = Math.Floor(rect.MidX - main.Size.Width / 2.0);
                var ly = Math.Floor(rect.MidY - main.Size.Height / 2.0);
                parts.Add(new OriginalGameplayOverlay.Part(
                    shadow,
                    Mapped(new CGRect(lx + 1, ly + 1, shadow.Size.Width, shadow.Size.Height), canvas)));
                parts.Add(new OriginalGameplayOverlay.Part(
                    main,
                    Mapped(new CGRect(lx, ly, main.Size.Width, main.Size.Height), canvas)));
            }
        }

        return parts;
    }

    private static bool Contains(CGRect rect, CGPoint point) =>
        point.X >= rect.X && point.X < rect.X + rect.Width &&
        point.Y >= rect.Y && point.Y < rect.Y + rect.Height;
}
