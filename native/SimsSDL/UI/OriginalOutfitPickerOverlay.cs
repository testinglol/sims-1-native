using System.Numerics;
using SimsSDL;

namespace SimsSDL.UI;

public sealed class OriginalOutfitPickerOverlay
{
    public readonly record struct DrawItem(OriginalGameplayOverlay.Image Image, CGRect Rect, Vector4 UV);

    public abstract record Action
    {
        private Action() { }
        public sealed record Previous : Action;
        public sealed record Next : Action;
        public sealed record Buy(int Index) : Action;
        public sealed record Cancel : Action;
    }

    private enum Control
    {
        Previous = 0,
        Next = 1,
        Buy = 2,
        Cancel = 3
    }

    private static readonly Control[] AllControls =
    [
        Control.Previous,
        Control.Next,
        Control.Buy,
        Control.Cancel
    ];

    private static readonly (byte R, byte G, byte B) NormalTextColor = (195, 205, 205);
    private static readonly (byte R, byte G, byte B) PressedTextColor = (0, 255, 255);
    private static readonly (byte R, byte G, byte B) HoverTextColor = (255, 255, 255);

    private readonly SimsBitmapFont _titleFont;
    private readonly SimsBitmapFont _buttonFont;
    private readonly OriginalGameplayOverlay.Image _frame;
    private readonly CGSize _frameEdge;
    private readonly OriginalGameplayOverlay.Image _button;
    private readonly double _buttonCapWidth;
    private readonly CGSize _buyButtonSize;
    private readonly CGSize _cancelButtonSize;
    private readonly OriginalGameplayOverlay.Image _previousArrow;
    private readonly OriginalGameplayOverlay.Image _nextArrow;
    private readonly CGSize _arrowSize;
    private readonly OriginalGameplayOverlay.Image _title;
    private readonly Dictionary<Control, OriginalGameplayOverlay.Image> _normalLabels;
    private readonly Dictionary<Control, OriginalGameplayOverlay.Image> _hoverLabels;
    private readonly Dictionary<Control, OriginalGameplayOverlay.Image> _pressedLabels;
    private readonly Dictionary<Control, string> _labelStrings;

    public string Token { get; }
    public bool Pet { get; }
    public IReadOnlyList<string> Names { get; }
    public int Index { get; private set; }
    public int? Hovered { get; private set; }
    public int? Pressed { get; private set; }

    public OriginalOutfitPickerOverlay(
        string token,
        bool pet,
        IReadOnlyList<string> names,
        GameContent content,
        SimsBitmapFont font,
        UIDevice device,
        OriginalUIStrings strings)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(strings);
        if (names.Count == 0)
        {
            throw new ArgumentException("Outfit picker requires at least one name.", nameof(names));
        }

        Token = token;
        Pet = pet;
        Names = names;

        SimsBitmapFont loadedTitleFont;
        try
        {
            loadedTitleFont = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_12.ffn"));
        }
        catch
        {
            loadedTitleFont = font;
        }

        _titleFont = loadedTitleFont;
        _buttonFont = font;

        OriginalGameplayOverlay.Image Upload(string path, bool magenta = false)
        {
            var bitmap = new SimsBitmap(content.Read(path));
            return OriginalGameplayOverlay.CreateImage(device, bitmap.Width, bitmap.Height, bitmap.Rgba, magenta: magenta);
        }

        _frame = Upload("UIGraphics/Shared/Dlg/GenDlg.bmp", magenta: true);
        _frameEdge = new CGSize(Math.Floor(_frame.Size.Width / 3.0), Math.Floor(_frame.Size.Height / 3.0));
        _button = Upload("UIGraphics/Shared/Sys/WinBtn.bmp", magenta: true);

        // Calculates button end-cap width as ((frameWidth - 1) / 2) from a four-column sprite sheet.
        double rawButtonFrameWidth = Math.Max(4.0, Math.Floor(_button.Size.Width / 4.0));
        double capWidth = Math.Max(1.0, Math.Floor((rawButtonFrameWidth - 1.0) / 2.0));
        _buttonCapWidth = capWidth;

        _previousArrow = Upload("UIGraphics/Nbhd/DesignCharSkinsLeftBtn.bmp", magenta: true);
        _nextArrow = Upload("UIGraphics/Nbhd/DesignCharSkinsRightBtn.bmp", magenta: true);
        _arrowSize = new CGSize(Math.Floor(_previousArrow.Size.Width / 4.0), _previousArrow.Size.Height);

        _title = OriginalGameplayOverlay.Text(
            strings.OutfitText(pet, 4, pet ? "Please Select Your Pet" : "Please Select Outfit"),
            _titleFont,
            device,
            NormalTextColor);

        _labelStrings = new Dictionary<Control, string>
        {
            [Control.Next] = strings.OutfitText(pet, 0, pet ? "Next Pet" : "Next Outfit"),
            [Control.Previous] = strings.OutfitText(pet, 1, pet ? "Previous Pet" : "Previous Outfit"),
            [Control.Buy] = strings.OutfitText(pet, 2, pet ? "Buy Pet" : "Buy Outfit"),
            [Control.Cancel] = strings.OutfitText(pet, 3, "Cancel")
        };

        _normalLabels = new Dictionary<Control, OriginalGameplayOverlay.Image>();
        _hoverLabels = new Dictionary<Control, OriginalGameplayOverlay.Image>();
        _pressedLabels = new Dictionary<Control, OriginalGameplayOverlay.Image>();
        ReadOnlySpan<Control> buttonControls = [Control.Buy, Control.Cancel];
        foreach (var control in buttonControls)
        {
            string value = _labelStrings.TryGetValue(control, out var s) ? s : string.Empty;
            _normalLabels[control] = OriginalGameplayOverlay.Text(value, _buttonFont, device, NormalTextColor);
            _hoverLabels[control] = OriginalGameplayOverlay.Text(value, _buttonFont, device, HoverTextColor);
            _pressedLabels[control] = OriginalGameplayOverlay.Text(value, _buttonFont, device, PressedTextColor);
        }

        // Enforces a minimum button width of 100 pixels, adding side cap padding when localized text
        // exceeds that width.
        double minPadding = Math.Max(16.0, (capWidth - 10.0) * 2.0);
        double buyLabelWidth = _normalLabels.TryGetValue(Control.Buy, out var buyImg) ? buyImg.Size.Width : 0.0;
        double cancelLabelWidth = _normalLabels.TryGetValue(Control.Cancel, out var cancelImg) ? cancelImg.Size.Width : 0.0;
        _buyButtonSize = new CGSize(Math.Max(100.0, Math.Ceiling(buyLabelWidth + minPadding)), _button.Size.Height);
        _cancelButtonSize = new CGSize(Math.Max(100.0, Math.Ceiling(cancelLabelWidth + minPadding)), _button.Size.Height);
    }

    public void Move(int delta)
    {
        Index = ((Index + delta) % Names.Count + Names.Count) % Names.Count;
    }

    private CGRect Panel(CGSize canvas)
    {
        // Dialog base dimensions are 300x400 (or 300x300 for pets), widening when title or button widths
        // exceed 300 pixels.
        double minButtonsWidth = _buyButtonSize.Width + _cancelButtonSize.Width + 15.0;
        double minTitleWidth = _title.Size.Width + 5.0;
        double requiredInner = Math.Max(minTitleWidth, minButtonsWidth);
        double width = requiredInner > 300.0 ? requiredInner + 10.0 : 300.0;
        double height = Pet ? 300.0 : 400.0;
        return new CGRect(Math.Floor((canvas.Width - width) / 2.0), Math.Floor((canvas.Height - height) / 2.0), width, height);
    }

    public CGRect PreviewRect(CGSize canvas)
    {
        var area = Panel(canvas);
        double innerTop = area.MinY + _title.Size.Height + 18.0;
        double innerBottom = area.MaxY - _buyButtonSize.Height - (Pet ? 36.0 : 18.0);
        if (Pet)
        {
            double left = ControlRect(Control.Previous, canvas).MaxX + 9.0;
            double right = ControlRect(Control.Next, canvas).MinX - 9.0;
            return new CGRect(left, innerTop, Math.Max(60.0, right - left), Math.Max(60.0, innerBottom - innerTop));
        }

        return new CGRect(Math.Floor(area.MidX - 100.0), Math.Floor((innerTop + innerBottom - 300.0) / 2.0), 200.0, 300.0);
    }

    private CGRect ControlRect(Control control, CGSize canvas)
    {
        var area = Panel(canvas);
        double buttonY = area.MaxY - _buyButtonSize.Height - 9.0;
        double gap = Math.Floor((area.Width - _buyButtonSize.Width - _cancelButtonSize.Width) / 3.0);
        double innerTop = area.MinY + _title.Size.Height + 18.0;
        double innerBottom = area.MaxY - _buyButtonSize.Height - (Pet ? 36.0 : 18.0);
        double arrowY = Math.Floor((innerTop + innerBottom - _arrowSize.Height) / 2.0);
        return control switch
        {
            Control.Previous => new CGRect(
                Pet ? area.MinX + 9.0 : Math.Floor(area.MidX - 100.0 - 9.0 - _arrowSize.Width),
                arrowY,
                _arrowSize.Width,
                _arrowSize.Height),
            Control.Next => new CGRect(
                Pet ? area.MaxX - 9.0 - _arrowSize.Width : Math.Floor(area.MidX + 100.0 + 9.0),
                arrowY,
                _arrowSize.Width,
                _arrowSize.Height),
            Control.Buy => new CGRect(area.MinX + gap, buttonY, _buyButtonSize.Width, _buyButtonSize.Height),
            Control.Cancel => new CGRect(area.MinX + gap * 2.0 + _buyButtonSize.Width, buttonY, _cancelButtonSize.Width, _cancelButtonSize.Height),
            _ => CGRect.Zero
        };
    }

    public void Hover(CGPoint point, CGSize canvas)
    {
        Control? hit = null;
        foreach (var control in AllControls)
        {
            if (ContainsRect(ControlRect(control, canvas), point))
            {
                hit = control;
                break;
            }
        }

        Hovered = hit is { } c ? (int)c : null;
    }

    public void ClearHover()
    {
        Hovered = null;
        Pressed = null;
    }

    public bool BeginPress(CGPoint point, CGSize canvas)
    {
        Control? hit = null;
        foreach (var control in AllControls)
        {
            if (ContainsRect(ControlRect(control, canvas), point))
            {
                hit = control;
                break;
            }
        }

        Pressed = hit is { } c ? (int)c : null;
        return Pressed is not null;
    }

    public void EndPress()
    {
        Pressed = null;
    }

    public Action? Key(string key) => key switch
    {
        "\uf702" => new Action.Previous(),
        "\uf703" => new Action.Next(),
        "\r" or "\n" => new Action.Buy(Index),
        "\u001b" => new Action.Cancel(),
        _ => null
    };

    public (string Text, CGRect Rect)? Tooltip(CGSize canvas)
    {
        if (Hovered is not { } hoveredRaw || !Enum.IsDefined(typeof(Control), hoveredRaw))
        {
            return null;
        }

        var control = (Control)hoveredRaw;
        if (control is not (Control.Previous or Control.Next))
        {
            return null;
        }

        if (!_labelStrings.TryGetValue(control, out var text) || string.IsNullOrEmpty(text))
        {
            return null;
        }

        return (text, ControlRect(control, canvas));
    }

    public Action? Click(CGPoint point, CGSize canvas)
    {
        Pressed = null;
        Control? hit = null;
        foreach (var control in AllControls)
        {
            if (ContainsRect(ControlRect(control, canvas), point))
            {
                hit = control;
                break;
            }
        }

        return hit switch
        {
            Control.Previous => new Action.Previous(),
            Control.Next => new Action.Next(),
            Control.Buy => new Action.Buy(Index),
            Control.Cancel => new Action.Cancel(),
            _ => null
        };
    }

    public IReadOnlyList<(string Label, CGRect Rect)> Controls(CGSize canvas)
    {
        var result = new List<(string Label, CGRect Rect)>(AllControls.Length);
        foreach (var control in AllControls)
        {
            string baseLabel = _labelStrings.TryGetValue(control, out var s) ? s : string.Empty;
            string fullLabel = baseLabel + (control == Control.Buy ? ": " + Names[Index] : string.Empty);
            result.Add((fullLabel, ControlRect(control, canvas)));
        }

        return result;
    }

    public IReadOnlyList<DrawItem> DrawItems(CGSize canvas)
    {
        var area = Panel(canvas);
        var whole = new Vector4(0f, 0f, 1f, 1f);
        double ew = Math.Min(_frameEdge.Width, Math.Floor(area.Width / 3.0));
        double eh = Math.Min(_frameEdge.Height, Math.Floor(area.Height / 3.0));
        double[] xs = [area.MinX, area.MinX + ew, area.MaxX - ew, area.MaxX];
        double[] ys = [area.MinY, area.MinY + eh, area.MaxY - eh, area.MaxY];

        var items = new List<DrawItem>();
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                double w = xs[column + 1] - xs[column];
                double h = ys[row + 1] - ys[row];
                if (w <= 0.0 || h <= 0.0)
                {
                    continue;
                }

                items.Add(new DrawItem(
                    _frame,
                    new CGRect(xs[column], ys[row], w, h),
                    new Vector4(column / 3f, row / 3f, 1f / 3f, 1f / 3f)));
            }
        }

        items.Add(new DrawItem(
            _title,
            new CGRect(Math.Floor(area.MidX - _title.Size.Width / 2.0), area.MinY + 9.0, _title.Size.Width, _title.Size.Height),
            whole));

        ReadOnlySpan<Control> arrows = [Control.Previous, Control.Next];
        foreach (var control in arrows)
        {
            var box = ControlRect(control, canvas);
            var sheet = control == Control.Previous ? _previousArrow : _nextArrow;
            int raw = (int)control;
            float uvX = Pressed == raw ? 0.25f : (Hovered == raw ? 0.50f : 0f);
            items.Add(new DrawItem(sheet, box, new Vector4(uvX, 0f, 0.25f, 1f)));
        }

        const float uFrame = 0.25f;
        float uCap = (float)(_buttonCapWidth / _button.Size.Width);
        float uMid = Math.Max(0f, uFrame - uCap * 2f);
        ReadOnlySpan<Control> buttons = [Control.Buy, Control.Cancel];
        foreach (var control in buttons)
        {
            var box = ControlRect(control, canvas);
            int raw = (int)control;
            bool isPressed = Pressed == raw;
            bool isHovered = Hovered == raw;
            float uvBase = isPressed ? 0.25f : (isHovered ? 0.50f : 0f);
            double cap = Math.Min(_buttonCapWidth, Math.Floor(box.Width / 2.0));
            double midW = Math.Max(0.0, box.Width - cap * 2.0);

            items.Add(new DrawItem(_button, new CGRect(box.MinX, box.MinY, cap, box.Height), new Vector4(uvBase, 0f, uCap, 1f)));
            if (midW > 0.0)
            {
                items.Add(new DrawItem(_button, new CGRect(box.MinX + cap, box.MinY, midW, box.Height), new Vector4(uvBase + uCap, 0f, uMid, 1f)));
            }

            items.Add(new DrawItem(_button, new CGRect(box.MaxX - cap, box.MinY, cap, box.Height), new Vector4(uvBase + uFrame - uCap, 0f, uCap, 1f)));

            OriginalGameplayOverlay.Image? labelImage = null;
            if (isPressed && _pressedLabels.TryGetValue(control, out var pressedImg))
            {
                labelImage = pressedImg;
            }
            else if (isHovered && _hoverLabels.TryGetValue(control, out var hoverImg))
            {
                labelImage = hoverImg;
            }
            else if (_normalLabels.TryGetValue(control, out var normalImg))
            {
                labelImage = normalImg;
            }

            if (labelImage is { } label)
            {
                double shift = isPressed ? 2.0 : 0.0;
                items.Add(new DrawItem(
                    label,
                    new CGRect(
                        Math.Floor(box.MidX - label.Size.Width / 2.0) + shift,
                        Math.Floor(box.MidY - label.Size.Height / 2.0) + shift,
                        label.Size.Width,
                        label.Size.Height),
                    whole));
            }
        }

        return items;
    }

    private static bool ContainsRect(CGRect rect, CGPoint point) =>
        rect.Width > 0.0 && rect.Height > 0.0 &&
        point.X >= rect.MinX && point.X < rect.MaxX &&
        point.Y >= rect.MinY && point.Y < rect.MaxY;
}
