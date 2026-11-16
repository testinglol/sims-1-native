using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using SimsSDL;

namespace SimsSDL.UI;

public sealed class OriginalSoundOptionsOverlay
{
    public struct DrawItem
    {
        public OriginalGameplayOverlay.Image Image { get; init; }
        public CGRect Rect { get; init; }
        public Vector4 UV { get; init; }
        public float Opacity { get; set; }

        public DrawItem(OriginalGameplayOverlay.Image image, CGRect rect, Vector4 uv, float opacity = 1f)
        {
            Image = image;
            Rect = rect;
            UV = uv;
            Opacity = opacity;
        }
    }

    public readonly record struct VolumeChange(string Group, float Volume, float PreviousVolume);

    private readonly struct Slider
    {
        public string Group { get; init; }
        public string Label { get; init; }
        public OriginalGameplayOverlay.Image LabelImage { get; init; }
        public OriginalGameplayOverlay.Image EmbeddedLabelImage { get; init; }
        public OriginalGameplayOverlay.Image EmbeddedHoverLabelImage { get; init; }
        public OriginalGameplayOverlay.Image Off { get; init; }
        public OriginalGameplayOverlay.Image On { get; init; }
    }

    private readonly OriginalGameplayOverlay.Image _frame;
    private readonly OriginalGameplayOverlay.Image _body;
    private readonly OriginalGameplayOverlay.Image _button;
    private readonly float _buttonSheetWidth;
    private readonly double _buttonCapWidth;
    private readonly List<Slider> _sliders;
    private readonly OriginalGameplayOverlay.Image _buttonLabel;
    private readonly OriginalGameplayOverlay.Image _buttonPressedLabel;
    private readonly OriginalGameplayOverlay.Image _buttonHoverLabel;
    private readonly CGSize _closeButtonSize;
    private readonly OriginalGameplayOverlay.Image _title;
    private readonly List<OriginalGameplayOverlay.Image> _valueLabels;
    private readonly string _closeLabel;
    private readonly double _embeddedSliderWidth;
    private readonly int _embeddedFontLineHeight;

    private readonly Dictionary<string, float> _values = new(StringComparer.Ordinal)
    {
        ["FX"] = 0.8f,
        ["MUSIC"] = 0.8f,
        ["VOX"] = 0.8f,
        ["AMBIENCE"] = 1f
    };

    private string? _draggingGroup;
    private double? _embeddedTransitionStarted;
    private double _embeddedFadeInDelay;
    private bool _interfaceEffectsEnabled = true;

    public bool IsVisible { get; private set; }
    public bool HoveredClose { get; private set; }
    public bool PressedClose { get; private set; }
    public int? HoveredIndex { get; private set; }

    public (CGPoint Origin, CGSize Size)? EmbeddedRows { get; private set; }

    public bool IsDismissingEmbedded { get; private set; }

    public bool InterfaceEffectsEnabled
    {
        get => _interfaceEffectsEnabled;
        set
        {
            _interfaceEffectsEnabled = value;
            if (value) return;
            if (IsDismissingEmbedded)
            {
                Dismiss();
            }
            else
            {
                _embeddedTransitionStarted = null;
                _embeddedFadeInDelay = 0;
            }
        }
    }

    public bool IsEmbedded => EmbeddedRows is not null;
    public bool IsDragging => _draggingGroup is not null;

    public bool IsTransitioning
    {
        get
        {
            if (!InterfaceEffectsEnabled || !IsEmbedded || _embeddedTransitionStarted is not { } started) return false;
            var totalDuration = IsDismissingEmbedded ? 0.333 : _embeddedFadeInDelay + 0.333;
            return NowSeconds() - started < totalDuration;
        }
    }

    public OriginalSoundOptionsOverlay(GameContent content, SimsBitmapFont font, UIDevice device, OriginalUIStrings strings)
    {
        var embeddedFont = TryLoadFont(content, "UIGraphics/Fonts/variablesans_09.ffn") ?? font;
        var titleFont = TryLoadFont(content, "UIGraphics/Fonts/variablesans_12.ffn") ?? font;
        _embeddedFontLineHeight = embeddedFont.LineHeight;

        OriginalGameplayOverlay.Image Upload(string path, bool transparent = false)
        {
            var bitmap = new SimsBitmap(content.Read(path));
            return OriginalGameplayOverlay.CreateImage(device, bitmap.Width, bitmap.Height, bitmap.Rgba, magenta: transparent);
        }

        OriginalGameplayOverlay.Image TextImg(string value, SimsBitmapFont? useFont = null, (byte R, byte G, byte B)? color = null)
        {
            var f = useFont ?? font;
            var c = color ?? ((byte)195, (byte)205, (byte)205);
            var raster = f.Rasterize(value, c);
            return OriginalGameplayOverlay.CreateImage(device, raster.Width, raster.Height, raster.Rgba);
        }

        _frame = Upload("UIGraphics/Shared/Dlg/GenDlg.bmp");
        _body = Upload("UIGraphics/Shared/Dlg/LBoxTile.bmp");
        _button = Upload("UIGraphics/Shared/Sys/WinBtn.bmp", transparent: true);
        _buttonSheetWidth = Math.Max(1f, (float)_button.Size.Width);
        _buttonCapWidth = Math.Max(1, ((int)_button.Size.Width / 4 - 1) / 2);

        (string Group, string Label)[] rows =
        [
            ("FX", strings.SoundEffectVolume()),
            ("MUSIC", strings.SoundMusicVolume()),
            ("VOX", strings.SoundVoiceVolume())
        ];

        var sliderOff = Upload("UIGraphics/cpanel/Buttons/optslideroff.bmp", transparent: true);
        var sliderOn = Upload("UIGraphics/cpanel/Buttons/optslideron.bmp", transparent: true);

        _sliders = new List<Slider>(rows.Length);
        foreach (var row in rows)
        {
            _sliders.Add(new Slider
            {
                Group = row.Group,
                Label = row.Label,
                LabelImage = TextImg(row.Label),
                EmbeddedLabelImage = TextImg(row.Label, embeddedFont, (195, 205, 205)),
                EmbeddedHoverLabelImage = TextImg(row.Label, embeddedFont, (255, 255, 255)),
                Off = sliderOff,
                On = sliderOn
            });
        }

        _embeddedSliderWidth = OriginalUIStrings.Point(strings.OptionString(53, "(130;0)"))?.X ?? 130;
        _title = TextImg(strings.SoundOptionsTitle(), titleFont, (195, 205, 205));
        _closeLabel = strings.OkButton();
        _buttonLabel = TextImg(_closeLabel, titleFont, (195, 205, 205));
        _buttonPressedLabel = TextImg(_closeLabel, titleFont, (0, 255, 255));
        _buttonHoverLabel = TextImg(_closeLabel, titleFont, (255, 255, 255));
        _closeButtonSize = new CGSize(Math.Max(100, Math.Ceiling(_buttonLabel.Size.Width) + 20), _button.Size.Height);

        _valueLabels = new List<OriginalGameplayOverlay.Image>(11);
        for (var i = 0; i <= 10; i++)
        {
            _valueLabels.Add(TextImg(i + "/10"));
        }
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

    private static double NowSeconds() =>
        Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public void Show() => IsVisible = true;

    public void ShowEmbedded((CGPoint Origin, CGSize Size) rows, bool afterFadeOut = false)
    {
        EmbeddedRows = (
            new CGPoint(
                OriginalOptionsPanelOverlay.SubpanelOrigin.X + rows.Origin.X,
                OriginalOptionsPanelOverlay.SubpanelOrigin.Y + rows.Origin.Y),
            rows.Size);
        IsVisible = true;
        IsDismissingEmbedded = false;
        _embeddedFadeInDelay = InterfaceEffectsEnabled && afterFadeOut ? 0.333 : 0.0;
        _embeddedTransitionStarted = InterfaceEffectsEnabled ? NowSeconds() : null;
    }

    public void HideEmbedded()
    {
        if (!IsEmbedded || IsDismissingEmbedded) return;
        _draggingGroup = null;
        HoveredClose = false;
        PressedClose = false;
        HoveredIndex = null;
        if (!InterfaceEffectsEnabled)
        {
            Dismiss();
            return;
        }
        _embeddedFadeInDelay = 0.0;
        IsDismissingEmbedded = true;
        _embeddedTransitionStarted = NowSeconds();
    }

    public void Dismiss()
    {
        IsVisible = false;
        EmbeddedRows = null;
        _embeddedTransitionStarted = null;
        _embeddedFadeInDelay = 0.0;
        IsDismissingEmbedded = false;
        _draggingGroup = null;
        HoveredClose = false;
        PressedClose = false;
        HoveredIndex = null;
    }

    public void UpdateVolumes(IReadOnlyDictionary<string, object?> raw)
    {
        foreach (var group in _values.Keys.ToList())
        {
            if (raw.TryGetValue(group, out var entry) && TryConvertFloat(entry, out var volume) && float.IsFinite(volume))
            {
                _values[group] = Math.Clamp(volume, 0f, 1f);
            }
        }
    }

    public void UpdateVolumes(IReadOnlyDictionary<string, float> raw)
    {
        foreach (var group in _values.Keys.ToList())
        {
            if (raw.TryGetValue(group, out var volume) && float.IsFinite(volume))
            {
                _values[group] = Math.Clamp(volume, 0f, 1f);
            }
        }
    }

    private static bool TryConvertFloat(object? value, out float result)
    {
        switch (value)
        {
            case float f:
                result = f;
                return true;
            case double d:
                result = (float)d;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case decimal m:
                result = (float)m;
                return true;
            case JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetSingle(out var jf):
                result = jf;
                return true;
            default:
                result = 0f;
                return false;
        }
    }

    public void Restore(VolumeChange change)
    {
        if (!_values.TryGetValue(change.Group, out var current) || current != change.Volume) return;
        _values[change.Group] = change.PreviousVolume;
    }

    public VolumeChange? PointerDown(CGPoint point, CGSize canvas, double scale)
    {
        if (!IsVisible || IsDismissingEmbedded) return null;
        var local = new CGPoint(point.X / scale, point.Y / scale);
        var logical = new CGSize(canvas.Width / scale, canvas.Height / scale);
        if (!IsEmbedded && Contains(CloseRect(logical), local))
        {
            PressedClose = true;
            HoveredClose = true;
            _draggingGroup = null;
            return null;
        }
        PressedClose = false;
        _draggingGroup = GroupAt(point, canvas, scale);
        return UpdatePointer(point, canvas, scale);
    }

    public VolumeChange? PointerDragged(CGPoint point, CGSize canvas, double scale)
    {
        if (!IsVisible || IsDismissingEmbedded) return null;
        if (PressedClose)
        {
            var local = new CGPoint(point.X / scale, point.Y / scale);
            var logical = new CGSize(canvas.Width / scale, canvas.Height / scale);
            HoveredClose = Contains(CloseRect(logical), local);
            return null;
        }
        return UpdatePointer(point, canvas, scale);
    }

    public VolumeChange? PointerUp(CGPoint point, CGSize canvas, double scale)
    {
        if (!IsVisible || IsDismissingEmbedded) return null;
        var local = new CGPoint(point.X / scale, point.Y / scale);
        var logical = new CGSize(canvas.Width / scale, canvas.Height / scale);
        if (PressedClose)
        {
            PressedClose = false;
            var inside = !IsEmbedded && Contains(CloseRect(logical), local);
            HoveredClose = inside;
            if (inside) Dismiss();
            return null;
        }
        var change = UpdatePointer(point, canvas, scale);
        _draggingGroup = null;
        return change;
    }

    public VolumeChange? Click(CGPoint point, CGSize canvas, double scale)
    {
        if (!IsVisible || IsDismissingEmbedded) return null;
        var local = new CGPoint(point.X / scale, point.Y / scale);
        var logicalCanvas = new CGSize(canvas.Width / scale, canvas.Height / scale);
        if (!IsEmbedded && Contains(CloseRect(logicalCanvas), local))
        {
            Dismiss();
            return null;
        }
        if (GroupAt(point, canvas, scale) is { } group)
        {
            _draggingGroup = group;
            try
            {
                return UpdatePointer(point, canvas, scale);
            }
            finally
            {
                _draggingGroup = null;
            }
        }
        return null;
    }

    public void Hover(CGPoint point, CGSize canvas, double scale)
    {
        if (!IsVisible || IsDismissingEmbedded)
        {
            HoveredIndex = null;
            return;
        }
        var local = new CGPoint(point.X / scale, point.Y / scale);
        var logical = new CGSize(canvas.Width / scale, canvas.Height / scale);
        HoveredClose = Contains(CloseRect(logical), local);
        HoveredIndex = RowIndex(local, logical);
    }

    public int? RowIndex(CGPoint local, CGSize canvas)
    {
        if (!IsVisible || IsDismissingEmbedded || !IsEmbedded) return null;
        var panel = PanelRect(canvas);
        for (var index = 0; index < _sliders.Count; index++)
        {
            var label = LabelRect(index, panel);
            var track = TrackRect(index, panel);
            var rowRect = Inset(Union(label, track), -4, -4);
            if (Contains(rowRect, local)) return index;
        }
        return null;
    }

    public bool Contains(CGPoint point, CGSize canvas, double scale)
    {
        if (!IsVisible || IsDismissingEmbedded) return false;
        var local = new CGPoint(point.X / scale, point.Y / scale);
        var logical = new CGSize(canvas.Width / scale, canvas.Height / scale);
        return Contains(PanelRect(logical), local);
    }

    public List<(string Label, CGRect Rect)> Controls(CGSize canvas)
    {
        if (!IsVisible || IsDismissingEmbedded) return [];
        var panel = PanelRect(canvas);
        var result = new List<(string Label, CGRect Rect)>(_sliders.Count * 12 + 1);
        for (var index = 0; index < _sliders.Count; index++)
        {
            result.Add((_sliders[index].Label, LabelRect(index, panel)));
        }
        for (var index = 0; index < _sliders.Count; index++)
        {
            var slider = _sliders[index];
            var track = TrackRect(index, panel);
            var thumbWidth = slider.Off.Size.Width / 4.0;
            var valueWidth = track.Width - thumbWidth;
            for (var step = 0; step <= 10; step++)
            {
                result.Add((
                    slider.Label + " volume " + step + " of 10",
                    new CGRect(track.MinX + step * valueWidth / 10.0 - 10, track.MinY - 8, 20, 30)));
            }
        }
        if (!IsEmbedded)
        {
            result.Add((_closeLabel, CloseRect(canvas)));
        }
        return result;
    }

    public List<DrawItem> DrawItems(CGSize canvas)
    {
        if (!IsVisible) return [];
        var embeddedOpacity = 1f;
        if (IsEmbedded && _embeddedTransitionStarted is { } started)
        {
            var elapsed = NowSeconds() - started;
            if (IsDismissingEmbedded)
            {
                var progress = Math.Clamp((float)(elapsed / 0.333), 0f, 1f);
                embeddedOpacity = 1f - progress;
                if (progress >= 1f)
                {
                    _embeddedTransitionStarted = null;
                    _embeddedFadeInDelay = 0.0;
                    IsVisible = false;
                    EmbeddedRows = null;
                    IsDismissingEmbedded = false;
                    return [];
                }
            }
            else
            {
                if (elapsed <= _embeddedFadeInDelay)
                {
                    return [];
                }
                var progress = Math.Clamp((float)((elapsed - _embeddedFadeInDelay) / 0.333), 0f, 1f);
                embeddedOpacity = progress;
                if (progress >= 1f)
                {
                    _embeddedTransitionStarted = null;
                    _embeddedFadeInDelay = 0.0;
                }
            }
        }

        var panel = PanelRect(canvas);
        double[] xs = [panel.MinX, panel.MinX + 10, panel.MaxX - 10, panel.MaxX];
        double[] ys = [panel.MinY, panel.MinY + 10, panel.MaxY - 10, panel.MaxY];
        var items = new List<DrawItem>();

        if (!IsEmbedded)
        {
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    items.Add(new DrawItem(
                        _frame,
                        new CGRect(xs[column], ys[row], xs[column + 1] - xs[column], ys[row + 1] - ys[row]),
                        new Vector4(column / 3.0f, row / 3.0f, 1.0f / 3.0f, 1.0f / 3.0f)));
                }
            }

            var contentRect = Inset(panel, 12, 12);
            items.Add(new DrawItem(_body, contentRect, new Vector4(0f, 0f, 1f, 1f)));
            items.Add(new DrawItem(
                _title,
                new CGRect(panel.MidX - _title.Size.Width / 2.0, panel.MinY + 22, _title.Size.Width, _title.Size.Height),
                new Vector4(0f, 0f, 1f, 1f)));
        }

        for (var index = 0; index < _sliders.Count; index++)
        {
            var slider = _sliders[index];
            var track = TrackRect(index, panel);
            var labelImg = IsEmbedded
                ? (HoveredIndex == index ? slider.EmbeddedHoverLabelImage : slider.EmbeddedLabelImage)
                : slider.LabelImage;
            items.Add(new DrawItem(labelImg, LabelRect(index, panel), new Vector4(0f, 0f, 1f, 1f)));

            var value = _values.TryGetValue(slider.Group, out var v) ? v : 1f;
            var texture = value > 0.0001f ? slider.On : slider.Off;
            // Draws slider end caps at their native 13-pixel width, stretching only the middle track slice.
            var segment = texture.Size.Width / 4.0;
            items.Add(new DrawItem(texture, new CGRect(track.MinX, track.MinY, segment, track.Height), new Vector4(0f, 0f, 0.25f, 1f)));
            items.Add(new DrawItem(texture, new CGRect(track.MinX + segment, track.MinY, track.Width - segment * 2.0, track.Height), new Vector4(0.25f, 0f, 0.25f, 1f)));
            items.Add(new DrawItem(texture, new CGRect(track.MaxX - segment, track.MinY, segment, track.Height), new Vector4(0.5f, 0f, 0.25f, 1f)));
            items.Add(new DrawItem(
                texture,
                new CGRect(
                    track.MinX + Math.Round((track.Width - segment) * value, MidpointRounding.AwayFromZero),
                    track.MinY,
                    segment,
                    track.Height),
                new Vector4(0.75f, 0f, 0.25f, 1f)));

            if (!IsEmbedded)
            {
                var valueIdx = Math.Clamp((int)Math.Round(value * 10.0, MidpointRounding.AwayFromZero), 0, 10);
                var valueImage = _valueLabels[valueIdx];
                items.Add(new DrawItem(
                    valueImage,
                    new CGRect(track.MaxX + 10, track.MidY - valueImage.Size.Height / 2.0, valueImage.Size.Width, valueImage.Size.Height),
                    new Vector4(0f, 0f, 1f, 1f)));
            }
        }

        if (IsEmbedded)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                items[i] = new DrawItem(item.Image, item.Rect, item.UV, embeddedOpacity);
            }
            return items;
        }

        var close = CloseRect(canvas);
        var closeState = (PressedClose && HoveredClose) ? 1 : HoveredClose ? 2 : 0;
        AppendTileButton(close, closeState, items);
        var closeText = closeState == 1 ? _buttonPressedLabel : closeState == 2 ? _buttonHoverLabel : _buttonLabel;
        items.Add(new DrawItem(
            closeText,
            new CGRect(
                Math.Floor(close.MidX - closeText.Size.Width / 2.0),
                Math.Floor(close.MidY - closeText.Size.Height / 2.0),
                closeText.Size.Width,
                closeText.Size.Height),
            new Vector4(0f, 0f, 1f, 1f)));
        return items;
    }

    private void AppendTileButton(CGRect bounds, int frameIndex, List<DrawItem> items)
    {
        var framePx = _buttonSheetWidth / 4.0f;
        var basePx = frameIndex * framePx;
        var capPx = (float)_buttonCapWidth;
        var capW = Math.Min(_buttonCapWidth, Math.Floor(bounds.Width / 2.0));
        var midW = Math.Max(0, bounds.Width - capW * 2.0);
        items.Add(new DrawItem(
            _button,
            new CGRect(bounds.MinX, bounds.MinY, capW, bounds.Height),
            new Vector4(basePx / _buttonSheetWidth, 0f, capPx / _buttonSheetWidth, 1f)));
        if (midW > 0)
        {
            items.Add(new DrawItem(
                _button,
                new CGRect(bounds.MinX + capW, bounds.MinY, midW, bounds.Height),
                new Vector4((basePx + capPx) / _buttonSheetWidth, 0f, Math.Max(1f, framePx - capPx * 2f) / _buttonSheetWidth, 1f)));
        }
        items.Add(new DrawItem(
            _button,
            new CGRect(bounds.MaxX - capW, bounds.MinY, capW, bounds.Height),
            new Vector4((basePx + framePx - capPx) / _buttonSheetWidth, 0f, capPx / _buttonSheetWidth, 1f)));
    }

    private VolumeChange? UpdatePointer(CGPoint point, CGSize canvas, double scale)
    {
        if (_draggingGroup is not { } group) return null;
        var local = new CGPoint(point.X / scale, point.Y / scale);
        var index = _sliders.FindIndex(s => string.Equals(s.Group, group, StringComparison.Ordinal));
        if (index < 0) return null;
        var track = TrackRect(index, PanelRect(new CGSize(canvas.Width / scale, canvas.Height / scale)));
        var thumbWidth = _sliders[index].Off.Size.Width / 4.0;
        var valueWidth = Math.Max(1, track.Width - thumbWidth);
        var step = Math.Clamp((int)Math.Round((local.X - track.MinX) / valueWidth * 10.0, MidpointRounding.AwayFromZero), 0, 10);
        var volume = step / 10.0f;
        var previousVolume = _values.TryGetValue(group, out var pv) ? pv : 1f;
        if (MathF.Abs(previousVolume - volume) <= 0.0001f) return null;
        _values[group] = volume;
        return new VolumeChange(group, volume, previousVolume);
    }

    private string? GroupAt(CGPoint point, CGSize canvas, double scale)
    {
        var logical = new CGSize(canvas.Width / scale, canvas.Height / scale);
        var local = new CGPoint(point.X / scale, point.Y / scale);
        var panel = PanelRect(logical);
        for (var i = 0; i < _sliders.Count; i++)
        {
            if (Contains(Inset(TrackRect(i, panel), 0, -12), local))
            {
                return _sliders[i].Group;
            }
        }
        return null;
    }

    private CGRect PanelRect(CGSize canvas)
    {
        if (EmbeddedRows is { } embeddedRows)
        {
            var top = canvas.Height - 150;
            double widest = 0;
            foreach (var slider in _sliders)
            {
                if (slider.EmbeddedLabelImage.Size.Width > widest) widest = slider.EmbeddedLabelImage.Size.Width;
            }
            return new CGRect(
                embeddedRows.Origin.X,
                top + embeddedRows.Origin.Y,
                widest + 15 + _embeddedSliderWidth,
                embeddedRows.Size.Height * _sliders.Count);
        }
        return new CGRect((canvas.Width - 440) / 2.0, (canvas.Height - 330) / 2.0, 440, 330);
    }

    private CGRect LabelRect(int index, CGRect panel)
    {
        var slider = _sliders[index];
        if (EmbeddedRows is { } embeddedRows)
        {
            var img = slider.EmbeddedLabelImage;
            return new CGRect(panel.MinX, panel.MinY + index * embeddedRows.Size.Height, img.Size.Width, img.Size.Height);
        }
        var track = TrackRect(index, panel);
        return new CGRect(
            panel.MinX + 28,
            track.MidY - slider.LabelImage.Size.Height / 2.0,
            slider.LabelImage.Size.Width,
            slider.LabelImage.Size.Height);
    }

    private CGRect TrackRect(int index, CGRect panel)
    {
        if (EmbeddedRows is { } embeddedRows)
        {
            var row = embeddedRows.Size.Height;
            double widest = 0;
            foreach (var slider in _sliders)
            {
                if (slider.EmbeddedLabelImage.Size.Width > widest) widest = slider.EmbeddedLabelImage.Size.Width;
            }
            double yOffset = (_embeddedFontLineHeight - 14) / 2;
            return new CGRect(panel.MinX + widest + 15, panel.MinY + index * row + yOffset, _embeddedSliderWidth, 14);
        }
        return new CGRect(panel.MinX + 138, panel.MinY + 92 + index * 47 - 7, 220, 14);
    }

    private CGRect CloseRect(CGSize canvas)
    {
        var panel = PanelRect(canvas);
        return new CGRect(
            Math.Floor(panel.MidX - _closeButtonSize.Width / 2.0),
            panel.MaxY - 49,
            _closeButtonSize.Width,
            _closeButtonSize.Height);
    }

    private static CGRect Union(CGRect a, CGRect b)
    {
        var minX = Math.Min(a.MinX, b.MinX);
        var minY = Math.Min(a.MinY, b.MinY);
        var maxX = Math.Max(a.MaxX, b.MaxX);
        var maxY = Math.Max(a.MaxY, b.MaxY);
        return new CGRect(minX, minY, maxX - minX, maxY - minY);
    }

    private static CGRect Inset(CGRect r, double dx, double dy) =>
        new(r.X + dx, r.Y + dy, r.Width - dx * 2, r.Height - dy * 2);

    private static bool Contains(CGRect rect, CGPoint point) =>
        point.X >= rect.X && point.X < rect.X + rect.Width &&
        point.Y >= rect.Y && point.Y < rect.Y + rect.Height;
}
