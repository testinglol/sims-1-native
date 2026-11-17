using System.Globalization;
using System.Numerics;
using SimsSDL;

namespace SimsSDL.UI;

public sealed class OriginalHouseholdCreationOverlay
{
    public readonly record struct DrawItem(OriginalGameplayOverlay.Image Image, CGRect Rect, Vector4 UV)
    {
        public DrawItem(OriginalGameplayOverlay.Image image, CGRect rect)
            : this(image, rect, new Vector4(0f, 0f, 1f, 1f))
        {
        }
    }

    private sealed class Draft
    {
        public string Name = "";
        public string Bio = "";
        public bool Female;
        public bool Child;
        public string Skin = "med";
        public string BodyType = "fit";
        public int HeadIndex;
        public int BodyIndex;
        public string? HeadChoice;
        public string? BodyChoice;
        public HouseholdAppearanceSelectionCache AppearanceSelections = new();
        public int[] Personality = [0, 0, 0, 0, 0];
        public int? Zodiac;

        public string Code => (Female ? "f" : "m") + (Child ? "c" : "a") + BodyType;

        public HouseholdCreationRequest.Member ToMember() =>
            new(Name, Bio, Code, Skin, Personality, HeadChoice, BodyChoice, Zodiac);

        public Draft Clone()
        {
            var selections = new HouseholdAppearanceSelectionCache();
            foreach (string gender in new[] { "m", "f" })
            {
                foreach (string age in new[] { "adult", "child" })
                {
                    if (AppearanceSelections.Selection(gender, age) is { } indices)
                    {
                        selections.Remember(indices, gender, age);
                    }
                }
            }

            return new Draft
            {
                Name = Name,
                Bio = Bio,
                Female = Female,
                Child = Child,
                Skin = Skin,
                BodyType = BodyType,
                HeadIndex = HeadIndex,
                BodyIndex = BodyIndex,
                HeadChoice = HeadChoice,
                BodyChoice = BodyChoice,
                AppearanceSelections = selections,
                Personality = [.. Personality],
                Zodiac = Zodiac,
            };
        }
    }

    private enum Page { Family, Sim }
    private enum Field { Family, Sim, Bio }

    private readonly record struct ButtonSheet(OriginalGameplayOverlay.Image Image, int Columns);

    private static readonly (byte R, byte G, byte B) DefaultTextColor = (195, 205, 205);
    private static readonly (byte R, byte G, byte B) HoverTextColor = (255, 255, 255);
    private static readonly (byte R, byte G, byte B) SelectedTextColor = (0, 255, 255);
    private static readonly (byte R, byte G, byte B) DisabledTextColor = (64, 93, 95);
    private static readonly (byte R, byte G, byte B) ErrorTextColor = (255, 150, 132);

    private enum FontSlot { F8, F9, F10, F12, F14, F16 }

    private readonly record struct SimButtonSpec(string Key, string Sheet, double X, double Y);

    private static readonly string[] TraitKeys = ["Neat", "Outgoing", "Active", "Playful", "Nice"];

    private static readonly SimButtonSpec[] SimButtons =
    [
        new("adult", "adult", 497, 101),
        new("child", "child", 406, 111),
        new("female", "female", 499, 274),
        new("male", "male", 400, 274),
        new("dark", "dark", 499, 202),
        new("medium", "medium", 452, 202),
        new("light", "light", 396, 202),
        new("previousBody", "skinsLeft", 580, 291),
        new("nextBody", "skinsRight", 729, 291),
        new("previousHead", "skinsLeft", 597, 160),
        new("nextHead", "skinsRight", 714, 160),
    ];

    private static readonly string[] SimButtonFallbacks =
    [
        "Adult", "Child", "Female", "Male", "Dark", "Medium", "Light",
        "Previous Clothes", "Next Clothes", "Previous Head", "Next Head",
    ];

    private static readonly CGRect NameRect = new(275, 52, 255, 25);
    private static readonly CGRect BioRect = new(22, 425, 761, 96);
    private static readonly CGRect ZodiacRect = new(129, 290, 120, 20);
    private static readonly CGRect FigureRect = new(618, 145, 100, 220);

    private readonly UIDevice _device;
    private readonly SimsBitmapFont _font;
    private readonly SimsBitmapFont _font8;
    private readonly SimsBitmapFont _font9;
    private readonly SimsBitmapFont _font12;
    private readonly SimsBitmapFont _font14;
    private readonly SimsBitmapFont _font16;
    private readonly FontSlot _traitSlot;
    private readonly SimsBitmapFont _tooltipFont;
    private readonly OriginalGameplayOverlay.Image _outerFill;
    private readonly OriginalGameplayOverlay.Image _familyBackground;
    private readonly OriginalGameplayOverlay.Image _simBackground;
    private readonly OriginalUIStrings _strings;
    private readonly Dictionary<string, ButtonSheet> _buttonSheets = new(StringComparer.Ordinal);
    private readonly OriginalGameplayOverlay.Image _tileButton;
    private readonly Dictionary<string, OriginalGameplayOverlay.Image> _textImages = new(StringComparer.Ordinal);
    private readonly List<Draft> _members = [];
    private readonly Dictionary<string, OriginalGameplayOverlay.Image> _rolloverTipCache = new(StringComparer.Ordinal);

    private Page _page = Page.Family;
    private Field _field = Field.Family;
    private string _familyName = "";
    private Draft _draft = new();
    private HouseholdAppearanceCatalog? _appearanceCatalog;
    private int? _editingMember;
    private int _selectedMember = -1;
    private int _caretOffset;
    private string _error = "";
    private string? _rolloverTipText;
    private CGPoint _rolloverTipAnchor = CGPoint.Zero;
    private double? _rolloverTipStartedAt;
    private bool _rolloverTipQuickTips = true;
    private string? _hoveredControl;
    private string? _pressedControl;
    private bool _quickTips = true;

    public bool Submitting { get; private set; }

    public void BeginSubmission() => Submitting = true;

    public Action<string>? PlaySound { get; set; }

    public Action<System.Action>? ConfirmUnspent { get; set; }

    public System.Action? ClearPreview { get; set; }

    public OriginalHouseholdCreationOverlay(GameContent content, UIDevice device)
    {
        ArgumentNullException.ThrowIfNull(content);

        _device = device;
        _strings = new OriginalUIStrings(content);
        SimsBitmapFont loaded10 = new(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
        SimsBitmapFont loaded8 = TryLoadFont(content, "UIGraphics/Fonts/variablesans_08.ffn") ?? loaded10;
        SimsBitmapFont loaded9 = TryLoadFont(content, "UIGraphics/Fonts/variablesans_09.ffn") ?? loaded8;
        SimsBitmapFont loaded12 = TryLoadFont(content, "UIGraphics/Fonts/variablesans_12.ffn") ?? loaded10;
        SimsBitmapFont loaded14 = TryLoadFont(content, "UIGraphics/Fonts/variablesans_14.ffn") ?? loaded12;
        SimsBitmapFont loaded16 = TryLoadFont(content, "UIGraphics/Fonts/variablesans_16.ffn") ?? loaded14;
        SimsBitmapFont loadedTooltip = TryLoadFont(content, "UIGraphics/Fonts/variablesans_07.ffn") ?? loaded9;
        _font = loaded10;
        _font8 = loaded8;
        _font9 = loaded9;
        _font12 = loaded12;
        _font14 = loaded14;
        _font16 = loaded16;
        _tooltipFont = loadedTooltip;

        // Trait labels use 12pt font instead of 14pt in French and German to accommodate longer localized
        // text strings.
        _traitSlot = (content.Language.Code == 3 || content.Language.Code == 4) ? FontSlot.F12 : FontSlot.F14;
        _outerFill = Upload(device, 1, 1, [0, 50, 0, 255]);

        SimsBitmap family = new(content.Read("UIGraphics/Nbhd/DsgnFamBkg.bmp"));
        SimsBitmap sim = new(content.Read("UIGraphics/Nbhd/CreateACharBack.bmp"));
        if (family.Width != 800 || family.Height != 600 || sim.Width != 800 || sim.Height != 600)
        {
            throw new ContentError("Original family creation backgrounds are not 800×600");
        }

        _familyBackground = Upload(device, family);
        _simBackground = Upload(device, sim);
        _tileButton = Upload(device, new SimsBitmap(content.Read("UIGraphics/Nbhd/NbhdTileBtn.bmp")), magenta: true);

        (string Name, string File, int Columns)[] assets =
        [
            ("add", "AddPersonBtn.bmp", 4),
            ("delete", "DeletePersonBtn.bmp", 4),
            ("edit", "EditPersonBtn.bmp", 4),
            ("adult", "DesignCharAdultBtn.bmp", 4),
            ("child", "DesignCharChildBtn.bmp", 4),
            ("skinsLeft", "DesignCharSkinsLeftBtn.bmp", 4),
            ("skinsRight", "DesignCharSkinsRightBtn.bmp", 4),
            ("female", "DesignCharFemaleBtn.bmp", 4),
            ("male", "DesignCharMaleBtn.bmp", 4),
            ("dark", "DesignCharDarkBtn.bmp", 4),
            ("medium", "DesignCharMediumBtn.bmp", 4),
            ("light", "DesignCharLightBtn.bmp", 4),
            ("led", "DesignCharPersLED.bmp", 1),
        ];

        foreach ((string name, string file, int columns) in assets)
        {
            SimsBitmap bitmap = new(content.Read("UIGraphics/Nbhd/" + file));
            if (bitmap.Width % columns != 0)
            {
                throw new ContentError($"Original household button sheet has invalid frame dimensions: {file}");
            }

            _buttonSheets[name] = new ButtonSheet(Upload(device, bitmap, magenta: true), columns);
        }

        // Composites a 35x41 face portrait at offset (5, 2) within each 45x45 template cell. Punching out
        // the face rectangle while preserving the four inner rounded bevel corners allows previews to show
        // through the frame.
        SimsBitmap peopleBitmap = new(content.Read("UIGraphics/cpanel/People/PeopleTemplate.bmp"));
        if (peopleBitmap.Width != 180 || peopleBitmap.Height != 45)
        {
            throw new ContentError("PeopleTemplate.bmp is not 180×45");
        }

        byte[] peoplePixels = (byte[])peopleBitmap.Rgba.Clone();
        for (int col = 0; col < 4; col++)
        {
            for (int fy = 0; fy < 41; fy++)
            {
                for (int fx = 0; fx < 35; fx++)
                {
                    if ((fx == 0 || fx == 34) && (fy == 0 || fy == 40))
                    {
                        continue;
                    }

                    int offset = ((2 + fy) * 180 + (col * 45 + 5 + fx)) * 4;
                    peoplePixels[offset] = 0;
                    peoplePixels[offset + 1] = 0;
                    peoplePixels[offset + 2] = 0;
                    peoplePixels[offset + 3] = 0;
                }
            }
        }

        _buttonSheets["memberFrame"] = new ButtonSheet(Upload(device, 180, 45, peoplePixels, magenta: true), 4);
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

    private SimsBitmapFont BitmapFont(FontSlot slot) => slot switch
    {
        FontSlot.F8 => _font8,
        FontSlot.F9 => _font9,
        FontSlot.F10 => _font,
        FontSlot.F12 => _font12,
        FontSlot.F14 => _font14,
        FontSlot.F16 => _font16,
        _ => _font,
    };

    private static OriginalGameplayOverlay.Image Upload(UIDevice device, SimsBitmap bitmap, bool magenta = false) =>
        Upload(device, bitmap.Width, bitmap.Height, bitmap.Rgba, magenta);

    private static OriginalGameplayOverlay.Image Upload(UIDevice device, int width, int height, byte[] pixels, bool magenta = false)
    {
        return OriginalGameplayOverlay.CreateImage(device, width, height, pixels, magenta: magenta);
    }

    private static (double Left, double Top, double Factor) Transform(CGSize canvas, float uiScale = 1f)
    {
        _ = uiScale;
        double factor = Math.Max(0.001, Math.Min(canvas.Width / 800.0, canvas.Height / 600.0));
        double left = (canvas.Width - 800.0 * factor) / 2.0;
        double top = (canvas.Height - 600.0 * factor) / 2.0;
        return (left, top, factor);
    }

    private DrawItem? Button(string name, int frame, double x, double y, double left, double top, double factor)
    {
        if (!_buttonSheets.TryGetValue(name, out ButtonSheet sheet) || frame < 0 || frame >= sheet.Columns)
        {
            return null;
        }

        double frameWidth = sheet.Image.Size.Width / sheet.Columns;
        return new DrawItem(
            sheet.Image,
            new CGRect(left + x * factor, top + y * factor, frameWidth * factor, sheet.Image.Size.Height * factor),
            new Vector4((float)frame / sheet.Columns, 0f, 1f / sheet.Columns, 1f));
    }

    /// <summary>Renders three-slice buttons using 45-pixel end caps across four state frames, stepping
    /// caption text down from 12pt to 10pt, 9pt, or 8pt if it exceeds the inner span.</summary>
    private void AppendTileItems(
        List<DrawItem> items,
        string caption,
        CGRect rect,
        double left,
        double top,
        double factor,
        bool enabled = true,
        bool pressed = false,
        bool hovered = false)
    {
        double frameWidth = Math.Max(4.0, Math.Floor(_tileButton.Size.Width / 4.0));
        double cap = Math.Min(Math.Floor(rect.Width / 2.0), Math.Max(1.0, Math.Floor((frameWidth - 1.0) / 2.0)));
        const float quarter = 0.25f;
        float u = (float)(cap / _tileButton.Size.Width);
        float start = !enabled ? quarter * 3f : (pressed ? quarter : (hovered ? quarter * 2f : 0f));
        (double X, double Width, float U0, float DU)[] slices =
        [
            (rect.MinX, cap, start, u),
            (rect.MinX + cap, Math.Max(0.0, rect.Width - cap * 2.0), start + u, Math.Max(0f, quarter - u * 2f)),
            (rect.MaxX - cap, cap, start + quarter - u, u),
        ];

        foreach ((double sx, double sw, float u0, float du) in slices)
        {
            items.Add(new DrawItem(
                _tileButton,
                new CGRect(left + sx * factor, top + rect.MinY * factor, sw * factor, rect.Height * factor),
                new Vector4(u0, 0f, du, 1f)));
        }

        int innerSpan = (int)Math.Max(40.0, rect.Width - (cap - 10.0) * 2.0);
        FontSlot chosenSlot = FontSlot.F12;
        FontSlot[] candidates = [FontSlot.F12, FontSlot.F10, FontSlot.F9, FontSlot.F8];
        foreach (FontSlot candidate in candidates)
        {
            chosenSlot = candidate;
            if (BitmapFont(candidate).TextWidth(caption) <= innerSpan)
            {
                break;
            }
        }

        var captionColor = !enabled ? DisabledTextColor : (pressed ? SelectedTextColor : (hovered ? HoverTextColor : DefaultTextColor));
        if (Label(caption, chosenSlot, captionColor) is { } textImage)
        {
            double shift = (enabled && pressed) ? 2.0 : 0.0;
            items.Add(new DrawItem(
                textImage,
                new CGRect(
                    left + (rect.MidX - textImage.Size.Width / 2.0 + shift) * factor,
                    top + (rect.MidY - textImage.Size.Height / 2.0 + shift) * factor,
                    textImage.Size.Width * factor,
                    textImage.Size.Height * factor)));
        }
    }

    private CGRect DoneRect => new(100, 529, 200, _tileButton.Size.Height);
    private CGRect CancelRect => new(500, 529, 200, _tileButton.Size.Height);

    private OriginalGameplayOverlay.Image? Label(string value, FontSlot slot = FontSlot.F10, (byte R, byte G, byte B)? color = null)
    {
        var resolved = color ?? DefaultTextColor;
        string key = $"{slot}:{resolved.R}-{resolved.G}-{resolved.B}:{value}";
        if (_textImages.TryGetValue(key, out OriginalGameplayOverlay.Image cached))
        {
            return cached;
        }

        SimsBitmapFont targetFont = BitmapFont(slot);
        try
        {
            OriginalGameplayOverlay.Image image = OriginalGameplayOverlay.CreateImage(_device, targetFont.RasterizeHiDpi(value, resolved));
            _textImages[key] = image;
            return image;
        }
        catch
        {
            return null;
        }
    }

    private bool HasFamilyName => !string.IsNullOrWhiteSpace(_familyName);
    private bool HasSimName => !string.IsNullOrWhiteSpace(_draft.Name);
    private bool CanFinishSim => HasSimName && _draft.HeadChoice is not null && _draft.BodyChoice is not null;
    private bool CanAddMember => _members.Count < 8 && HasFamilyName;
    private bool CanModifyMember => _selectedMember >= 0 && _selectedMember < _members.Count;
    private bool CanFinishFamily => _members.Count > 0 && HasFamilyName;

    public IReadOnlyList<DrawItem> DrawItems(CGSize canvas, float uiScale = 1f)
    {
        (double left, double top, double factor) = Transform(canvas, uiScale);
        double panelWidth = 800.0 * factor;
        double panelHeight = 600.0 * factor;
        OriginalGameplayOverlay.Image background = _page == Page.Family ? _familyBackground : _simBackground;

        // Keeps index 0 as the base panel background so portrait previews can be inserted directly above
        // it, then fills outer pillarbox and letterbox margins with dark green RGB(0, 50, 0).
        List<DrawItem> items = [new DrawItem(background, new CGRect(left, top, panelWidth, panelHeight))];

        double topH = top > 0 ? Math.Ceiling(top) : 0;
        double bottomY = Math.Floor(top + panelHeight);
        if (topH > 0)
        {
            items.Add(new DrawItem(_outerFill, new CGRect(0, 0, canvas.Width, topH)));
        }

        if (top > 0 && canvas.Height > bottomY)
        {
            items.Add(new DrawItem(_outerFill, new CGRect(0, bottomY, canvas.Width, Math.Ceiling(canvas.Height - bottomY))));
        }

        if (left > 0)
        {
            double leftW = Math.Ceiling(left);
            double rightX = Math.Floor(left + panelWidth);
            double midY = topH;
            double midH = Math.Max(0.0, (top > 0 ? bottomY : canvas.Height) - midY);
            if (midH > 0)
            {
                items.Add(new DrawItem(_outerFill, new CGRect(0, midY, leftW, midH)));
                if (canvas.Width > rightX)
                {
                    items.Add(new DrawItem(_outerFill, new CGRect(rightX, midY, Math.Ceiling(canvas.Width - rightX), midH)));
                }
            }
        }

        void AddText(string value, double x, double y, FontSlot slot = FontSlot.F10, (byte R, byte G, byte B)? color = null)
        {
            if (Label(value, slot, color) is not { } image)
            {
                return;
            }

            items.Add(new DrawItem(
                image,
                new CGRect(left + x * factor, top + y * factor, image.Size.Width * factor, image.Size.Height * factor)));
        }

        void Centred(string value, double midX, double y, FontSlot slot = FontSlot.F10, (byte R, byte G, byte B)? color = null)
        {
            if (Label(value, slot, color) is not { } image)
            {
                return;
            }

            AddText(value, midX - image.Size.Width / 2.0, y, slot, color);
        }

        void Trailing(string value, double maxX, double midY, FontSlot slot = FontSlot.F10, (byte R, byte G, byte B)? color = null)
        {
            if (Label(value, slot, color) is not { } image)
            {
                return;
            }

            AddText(value, maxX - image.Size.Width, midY - image.Size.Height / 2.0, slot, color);
        }

        if (_page == Page.Family)
        {
            int addFrame = CanAddMember ? (_pressedControl == "add" ? 1 : (_hoveredControl == "add" ? 2 : 0)) : 3;
            int deleteFrame = CanModifyMember ? (_pressedControl == "delete" ? 1 : (_hoveredControl == "delete" ? 2 : 0)) : 3;
            int editFrame = CanModifyMember ? (_pressedControl == "edit" ? 1 : (_hoveredControl == "edit" ? 2 : 0)) : 3;

            if (Button("add", addFrame, 593, 103, left, top, factor) is { } addBtn) items.Add(addBtn);
            if (Button("delete", deleteFrame, 594, 208, left, top, factor) is { } delBtn) items.Add(delBtn);
            if (Button("edit", editFrame, 594, 311, left, top, factor) is { } editBtn) items.Add(editBtn);

            AppendTileItems(
                items,
                _strings.Family(3, "Done"),
                DoneRect,
                left,
                top,
                factor,
                enabled: CanFinishFamily,
                pressed: CanFinishFamily && _pressedControl == "done",
                hovered: CanFinishFamily && _hoveredControl == "done");
            AppendTileItems(
                items,
                _strings.Family(4, "Cancel"),
                CancelRect,
                left,
                top,
                factor,
                enabled: true,
                pressed: _pressedControl == "cancel",
                hovered: _hoveredControl == "cancel");

            Centred(_strings.Family(9, "CREATE A FAMILY"), 400, 6, FontSlot.F16, DefaultTextColor);
            Trailing(_strings.Family(12, "Enter Last Name:"), NameRect.MinX - 20, NameRect.MidY, FontSlot.F12, DefaultTextColor);
            if (_field == Field.Family || _familyName.Length > 0)
            {
                string display = _field == Field.Family ? WithCaret(_familyName) : _familyName;
                if (Label(display, FontSlot.F10, HoverTextColor) is { } image)
                {
                    AddText(display, NameRect.MinX + 4, NameRect.MidY - image.Size.Height / 2.0, FontSlot.F10, HoverTextColor);
                }
            }

            for (int index = 0; index < _members.Count; index++)
            {
                CGRect slot = MemberSlot(index);
                bool memberHovered = _hoveredControl == $"member:{index}";
                int memberFrame = index == _selectedMember ? 1 : (memberHovered ? 2 : 0);
                if (Button("memberFrame", memberFrame, slot.MinX + 20, slot.MinY + 14, left, top, factor) is { } frameItem)
                {
                    items.Add(frameItem);
                }

                var textColor = index == _selectedMember ? SelectedTextColor : (memberHovered ? HoverTextColor : DefaultTextColor);
                IReadOnlyList<string> lines = TryWrappedLines(_font, _members[index].Name, (int)slot.Width) ?? [_members[index].Name];
                double textTop = slot.MaxY - (_font.LineHeight * 2 + 2);
                int maxNameLines = Math.Min(2, lines.Count);
                for (int lineIndex = 0; lineIndex < maxNameLines; lineIndex++)
                {
                    string line = lines[lineIndex];
                    if (line.Length > 0)
                    {
                        Centred(line, slot.MidX, textTop + lineIndex * _font.LineHeight, FontSlot.F10, textColor);
                    }
                }
            }

            if (_error.Length > 0)
            {
                AddText(_error, 92, 462, FontSlot.F10, ErrorTextColor);
            }
        }
        else
        {
            foreach (SimButtonSpec control in SimButtons)
            {
                if (Button(control.Sheet, SimButtonFrame(control.Key), control.X, control.Y, left, top, factor) is { } item)
                {
                    items.Add(item);
                }
            }

            AppendTileItems(
                items,
                _strings.Sim(11, "Done"),
                DoneRect,
                left,
                top,
                factor,
                enabled: CanFinishSim,
                pressed: CanFinishSim && _pressedControl == "done",
                hovered: CanFinishSim && _hoveredControl == "done");
            AppendTileItems(
                items,
                _strings.Sim(12, "Cancel"),
                CancelRect,
                left,
                top,
                factor,
                enabled: true,
                pressed: _pressedControl == "cancel",
                hovered: _hoveredControl == "cancel");

            Centred(_strings.Sim(15, "CREATE A SIM"), 400, 6, FontSlot.F16, DefaultTextColor);
            string personalityHeader = _strings.Sim(16, "PERSONALITY");
            if (Label(personalityHeader, FontSlot.F12, DefaultTextColor) is { } header)
            {
                AddText(personalityHeader, 198 - header.Size.Width / 2.0, 114 - header.Size.Height, FontSlot.F12, DefaultTextColor);
            }

            Trailing(_strings.Sim(26, "Enter First Name:"), NameRect.MinX - 20, NameRect.MidY, FontSlot.F12, DefaultTextColor);
            if (_field == Field.Sim || _draft.Name.Length > 0)
            {
                string display = _field == Field.Sim ? WithCaret(_draft.Name) : _draft.Name;
                if (Label(display, FontSlot.F10, HoverTextColor) is { } image)
                {
                    AddText(display, NameRect.MinX + 4, NameRect.MidY - image.Size.Height / 2.0, FontSlot.F10, HoverTextColor);
                }
            }

            _buttonSheets.TryGetValue("led", out ButtonSheet led);
            for (int index = 0; index < TraitKeys.Length; index++)
            {
                CGRect row = PersonalityRow(index);
                var traitColor = _hoveredControl == $"trait:{index}" ? HoverTextColor : DefaultTextColor;
                Trailing(_strings.Sim(17 + index, TraitKeys[index]), row.MinX - 12, row.MidY, _traitSlot, traitColor);
                double lit = (_draft.Personality[index] / 100) / 10.0;
                if (led.Columns > 0 && lit > 0)
                {
                    items.Add(new DrawItem(
                        led.Image,
                        new CGRect(left + row.MinX * factor, top + row.MinY * factor, row.Width * lit * factor, row.Height * factor),
                        new Vector4(0f, 0f, (float)lit, 1f)));
                }
            }

            // Lights six pixels of the points bar for each personality point remaining to be allocated.
            if (led.Columns > 0)
            {
                double step = led.Image.Size.Width / 10.0;
                int rem = RemainingPoints;
                for (int point = 0; point < rem; point++)
                {
                    items.Add(new DrawItem(
                        led.Image,
                        new CGRect(left + (117 + point * 6) * factor, top + 342 * factor, step * factor, led.Image.Size.Height * factor),
                        new Vector4(0f, 0f, (float)(step / led.Image.Size.Width), 1f)));
                }
            }

            // Hides the astrological sign label until personality points are allocated or a sign is
            // explicitly chosen, formatting it in parentheses.
            if (RemainingPoints < 25 || _draft.Zodiac is not null)
            {
                var personality = BuildTraitDictionary();
                int zodiac = OriginalGameplayLayout.Zodiac(personality, _draft.Zodiac ?? 0);
                string signText = "(" + _strings.Sign(zodiac - 1, OriginalGameplayLayout.Signs[zodiac - 1]) + ")";
                var signColor = _hoveredControl == "zodiac" ? HoverTextColor : DefaultTextColor;
                if (Label(signText, FontSlot.F12, signColor) is { } signImg)
                {
                    AddText(signText, ZodiacRect.MidX - signImg.Size.Width / 2.0, ZodiacRect.MidY - signImg.Size.Height / 2.0, FontSlot.F12, signColor);
                }
            }

            string fullBioName = string.IsNullOrWhiteSpace(_familyName)
                ? _draft.Name
                : $"{_draft.Name} {_familyName}";
            string biographyLabel = _draft.Name.Length == 0
                ? _strings.Sim(24, "BIO")
                : _strings.Sim(25, "Bio for %s").Replace("%s", fullBioName, StringComparison.Ordinal);
            if (Label(biographyLabel, FontSlot.F10, DefaultTextColor) is { } bioLabelImg)
            {
                AddText(biographyLabel, BioRect.MinX, BioRect.MinY - bioLabelImg.Size.Height - 3, FontSlot.F10, DefaultTextColor);
            }

            string biography = _field == Field.Bio ? WithCaret(_draft.Bio) : _draft.Bio;
            if (TryWrappedLines(_font, biography, 736) is { } bioLines)
            {
                int startIdx = (_field == Field.Bio && bioLines.Count > 4) ? bioLines.Count - 4 : 0;
                int count = Math.Min(4, bioLines.Count - startIdx);
                for (int i = 0; i < count; i++)
                {
                    AddText(bioLines[startIdx + i], BioRect.MinX + 2, BioRect.MinY + 2 + i * _font.LineHeight, FontSlot.F10, HoverTextColor);
                }
            }

            if (_error.Length > 0)
            {
                AddText(_error, 30, 398, FontSlot.F10, ErrorTextColor);
            }
        }

        if (RolloverTooltip(left, top, factor) is { } tip)
        {
            items.Add(tip);
        }

        return items;
    }

    private Dictionary<string, int> BuildTraitDictionary()
    {
        var dict = new Dictionary<string, int>(TraitKeys.Length, StringComparer.Ordinal);
        for (int i = 0; i < TraitKeys.Length; i++)
        {
            dict[TraitKeys[i]] = _draft.Personality[i];
        }

        return dict;
    }

    private static IReadOnlyList<string>? TryWrappedLines(SimsBitmapFont font, string text, int width)
    {
        try
        {
            return font.WrappedLines(text, width);
        }
        catch
        {
            return null;
        }
    }

    public void Hover(CGPoint point, CGSize canvas, bool quickTips, float uiScale = 1f)
    {
        (double left, double top, double factor) = Transform(canvas, uiScale);
        CGPoint logical = new((point.X - left) / factor, (point.Y - top) / factor);
        static CGPoint LogicalTopCenter(CGRect rect) => new(rect.MidX, rect.MinY - 1);

        string? nextTipText = null;
        CGPoint nextTipAnchor = new(logical.X, logical.Y - 1);

        if (DoneRect.Contains(logical))
        {
            _hoveredControl = (_page == Page.Family ? CanFinishFamily : CanFinishSim) ? "done" : null;
        }
        else if (CancelRect.Contains(logical))
        {
            _hoveredControl = "cancel";
        }
        else if (_page == Page.Family)
        {
            CGRect addRect = new(593, 103, 139, 103);
            CGRect deleteRect = new(594, 208, 149, 99);
            CGRect editRect = new(594, 311, 137, 99);
            if (addRect.Contains(logical))
            {
                _hoveredControl = CanAddMember ? "add" : null;
                nextTipText = _strings.Family(0, "Create a Sim");
                nextTipAnchor = LogicalTopCenter(addRect);
            }
            else if (deleteRect.Contains(logical))
            {
                _hoveredControl = CanModifyMember ? "delete" : null;
                nextTipText = _strings.Family(1, "Delete a Sim");
                nextTipAnchor = LogicalTopCenter(deleteRect);
            }
            else if (editRect.Contains(logical))
            {
                _hoveredControl = CanModifyMember ? "edit" : null;
                nextTipText = _strings.Family(2, "Edit a Sim");
                nextTipAnchor = LogicalTopCenter(editRect);
            }
            else
            {
                int hitIndex = -1;
                for (int i = 0; i < _members.Count; i++)
                {
                    if (MemberSlot(i).Contains(logical))
                    {
                        hitIndex = i;
                        break;
                    }
                }

                _hoveredControl = hitIndex >= 0 ? $"member:{hitIndex}" : null;
            }
        }
        else
        {
            int matchedButtonIndex = -1;
            CGRect matchedRect = CGRect.Zero;
            for (int i = 0; i < SimButtons.Length; i++)
            {
                if (SimButtonRect(SimButtons[i]) is { } rect && rect.Contains(logical))
                {
                    matchedButtonIndex = i;
                    matchedRect = rect;
                    break;
                }
            }

            if (matchedButtonIndex >= 0)
            {
                _hoveredControl = SimButtons[matchedButtonIndex].Key;
                nextTipText = _strings.Sim(matchedButtonIndex, SimButtonFallbacks[matchedButtonIndex]);
                nextTipAnchor = LogicalTopCenter(matchedRect);
            }
            else if (ZodiacRect.Contains(logical))
            {
                _hoveredControl = (RemainingPoints < 25 || _draft.Zodiac is not null) ? "zodiac" : null;
                nextTipText = _strings.Sim(27, "");
                nextTipAnchor = LogicalTopCenter(ZodiacRect);
            }
            else
            {
                int traitIndex = -1;
                for (int i = 0; i < 5; i++)
                {
                    if (PersonalityRow(i).InsetBy(0, -3).Contains(logical))
                    {
                        traitIndex = i;
                        break;
                    }
                }

                if (traitIndex >= 0)
                {
                    _hoveredControl = $"trait:{traitIndex}";
                    nextTipText = _strings.Sim(27, "");
                }
                else
                {
                    _hoveredControl = null;
                    if (OriginalHouseholdCreationLayout.Frames.TryGetValue("personality", out CGRect personalityFrame) &&
                        personalityFrame.Contains(logical))
                    {
                        nextTipText = _strings.Sim(27, "");
                    }
                }
            }
        }

        if (nextTipText is not null && string.IsNullOrWhiteSpace(nextTipText))
        {
            nextTipText = null;
        }

        if (!string.Equals(nextTipText, _rolloverTipText, StringComparison.Ordinal))
        {
            _rolloverTipText = nextTipText;
            _rolloverTipStartedAt = null;
        }

        if (nextTipText is not null)
        {
            _rolloverTipAnchor = nextTipAnchor;
        }

        if (quickTips != _rolloverTipQuickTips)
        {
            _rolloverTipStartedAt = null;
            _rolloverTipQuickTips = quickTips;
        }

        _quickTips = quickTips;
    }

    public void ClearHover()
    {
        _hoveredControl = null;
        _pressedControl = null;
        _rolloverTipText = null;
        _rolloverTipStartedAt = null;
    }

    private DrawItem? RolloverTooltip(double left, double top, double factor)
    {
        if (string.IsNullOrEmpty(_rolloverTipText) ||
            PlacementTooltip.Progress(ref _rolloverTipStartedAt, _quickTips) is not { } progress)
        {
            return null;
        }

        OriginalGameplayOverlay.Image image;
        if (_rolloverTipCache.TryGetValue(_rolloverTipText, out OriginalGameplayOverlay.Image cached))
        {
            image = cached;
        }
        else
        {
            try
            {
                image = OriginalGameplayOverlay.CreateImage(_device, PlacementTooltip.RasterizeHiDpi(_rolloverTipText, _tooltipFont));
                _rolloverTipCache[_rolloverTipText] = image;
            }
            catch
            {
                return null;
            }
        }

        CGSize scaledSize = new(image.Size.Width * factor, image.Size.Height * factor);
        CGSize panelSize = new(800.0 * factor, 600.0 * factor);
        CGPoint panelAnchor = new(_rolloverTipAnchor.X * factor, _rolloverTipAnchor.Y * factor);
        CGRect rect = PlacementTooltip.Rect(scaledSize, panelAnchor, panelSize).OffsetBy(left, top);
        if (progress >= 1.0)
        {
            return new DrawItem(image, rect);
        }

        double height = Math.Max(1.0, Math.Round(rect.Height * progress, MidpointRounding.AwayFromZero));
        float fraction = (float)Math.Clamp(height / rect.Height, 0.0, 1.0);
        return new DrawItem(
            image,
            new CGRect(rect.MinX, rect.MaxY - height, rect.Width, height),
            new Vector4(0f, 1f - fraction, 1f, fraction));
    }

    public IReadOnlyList<(string Label, CGRect Rect)> Controls(CGSize canvas, float uiScale = 1f)
    {
        (double left, double top, double factor) = Transform(canvas, uiScale);
        CGRect Scaled(CGRect value) =>
            new(left + value.MinX * factor, top + value.MinY * factor, value.Width * factor, value.Height * factor);

        List<(string Label, CGRect Rect)> items = [];
        if (_page == Page.Family)
        {
            items.Add((_strings.Control("familyName"), Scaled(new CGRect(260, 44, 282, 40))));
            items.Add((_strings.Control("addSim"), Scaled(new CGRect(593, 103, 139, 103))));
            items.Add((_strings.Control("deleteSim"), Scaled(new CGRect(594, 208, 149, 99))));
            items.Add((_strings.Control("editSim"), Scaled(new CGRect(594, 311, 137, 99))));
            items.Add((_strings.Control("familyDone"), Scaled(DoneRect)));
            items.Add((_strings.Control("familyCancel"), Scaled(CancelRect)));
            for (int index = 0; index < _members.Count; index++)
            {
                Draft member = _members[index];
                string age = member.Child ? _strings.Sim(1, "Child") : _strings.Sim(0, "Adult");
                string selected = index == _selectedMember ? "✓ " : "";
                items.Add(($"{selected}{member.Name} · {age}", Scaled(MemberSlot(index))));
            }

            return items;
        }

        items.Add((_strings.Control("simName"), Scaled(NameRect)));
        foreach (SimButtonSpec control in SimButtons)
        {
            if (SimButtonRect(control) is { } buttonRect)
            {
                items.Add((_strings.Control(control.Key), Scaled(buttonRect)));
            }
        }

        items.Add((_strings.Control("personality"), Scaled(new CGRect(80, 124, 220, 160))));
        items.Add((_strings.Control("zodiac"), Scaled(ZodiacRect)));
        items.Add((ControlLabel("biography"), Scaled(BioRect)));
        items.Add((_strings.Control("simDone"), Scaled(DoneRect)));
        items.Add((_strings.Control("simCancel"), Scaled(CancelRect)));
        return items;
    }

    public string ControlLabel(string key)
    {
        if (string.Equals(key, "biography", StringComparison.Ordinal))
        {
            return _draft.Name.Length == 0
                ? _strings.Sim(24, "BIO")
                : _strings.Sim(25, "Bio for %s").Replace("%s", _draft.Name, StringComparison.Ordinal);
        }

        return _strings.Control(key);
    }

    public static CGRect MemberSlot(int index) =>
        new(111 + (index % 4) * 120, 121 + (index / 4) * 140, 85, 105);

    private static CGRect PersonalityRow(int index) =>
        new(228, 131 + index * 32, 59, 25);

    private int RemainingPoints
    {
        get
        {
            int sum = 0;
            for (int i = 0; i < _draft.Personality.Length; i++)
            {
                sum += _draft.Personality[i];
            }

            return Math.Max(0, 25 - sum / 100);
        }
    }

    private CGRect? SimButtonRect(SimButtonSpec control)
    {
        if (!_buttonSheets.TryGetValue(control.Sheet, out ButtonSheet sheet))
        {
            return null;
        }

        return new CGRect(control.X, control.Y, sheet.Image.Size.Width / sheet.Columns, sheet.Image.Size.Height);
    }

    private int SimButtonFrame(string key)
    {
        int baseFrame = key switch
        {
            "adult" => _draft.Child ? 0 : 1,
            "child" => _draft.Child ? 1 : 0,
            "female" => _draft.Female ? 1 : 0,
            "male" => _draft.Female ? 0 : 1,
            "light" => _draft.Skin == "lgt" ? 1 : 0,
            "medium" => _draft.Skin == "med" ? 1 : 0,
            "dark" => _draft.Skin == "drk" ? 1 : 0,
            "previousHead" or "nextHead" => (_appearanceCatalog?.Heads.Count ?? 0) > 1 ? 0 : 3,
            _ => (_appearanceCatalog?.Bodies.Count ?? 0) > 1 ? 0 : 3,
        };

        if (baseFrame == 0)
        {
            if (string.Equals(_pressedControl, key, StringComparison.Ordinal)) return 1;
            if (string.Equals(_hoveredControl, key, StringComparison.Ordinal)) return 2;
        }

        return baseFrame;
    }

    /// <summary>Defines render bounds for character previews: index 0 represents the full-body Sim, while
    /// indices 1 through 8 map to household member portrait slots.</summary>
    public IReadOnlyDictionary<int, CGRect> PreviewRects(CGSize canvas, float uiScale = 1f)
    {
        (double left, double top, double factor) = Transform(canvas, uiScale);
        var rects = new Dictionary<int, CGRect>();
        if (_page == Page.Sim)
        {
            if (AppearancePreviewRect(canvas, uiScale) is { } rect)
            {
                rects[0] = rect;
            }

            return rects;
        }

        for (int index = 0; index < _members.Count; index++)
        {
            CGRect slot = MemberSlot(index);
            rects[index + 1] = new CGRect(
                left + (slot.MinX + 25) * factor,
                top + (slot.MinY + 16) * factor,
                35 * factor,
                41 * factor);
        }

        return rects;
    }

    public IReadOnlyDictionary<int, (string Gender, string Age, string Skin, string HeadChoice, string BodyChoice)> MemberPreviewSelections()
    {
        var result = new Dictionary<int, (string Gender, string Age, string Skin, string HeadChoice, string BodyChoice)>();
        for (int index = 0; index < _members.Count; index++)
        {
            Draft member = _members[index];
            if (member.HeadChoice is not { } head || member.BodyChoice is not { } body)
            {
                continue;
            }

            result[index + 1] = (member.Female ? "f" : "m", member.Child ? "child" : "adult", member.Skin, head, body);
        }

        return result;
    }

    public bool ShowsFamilyPage => _page == Page.Family;

    public CGRect? AppearancePreviewRect(CGSize canvas, float uiScale = 1f)
    {
        if (_page != Page.Sim)
        {
            return null;
        }

        (double left, double top, double factor) = Transform(canvas, uiScale);
        return new CGRect(
            left + FigureRect.MinX * factor,
            top + FigureRect.MinY * factor,
            FigureRect.Width * factor,
            FigureRect.Height * factor);
    }

    public (string Gender, string Age, string Skin, string HeadChoice, string BodyChoice)? AppearancePreviewSelection()
    {
        if (_appearanceCatalog is null || _draft.HeadChoice is not { } headChoice || _draft.BodyChoice is not { } bodyChoice)
        {
            return null;
        }

        return (_draft.Female ? "f" : "m", _draft.Child ? "child" : "adult", _draft.Skin, headChoice, bodyChoice);
    }

    public void Click(
        CGPoint point,
        CGSize canvas,
        Action<HouseholdCreationRequest> submit,
        System.Action? cancel = null,
        Action<int>? delete = null,
        Action<string, string, string>? appearanceCatalog = null,
        Action<string, string, string, string, string>? previewAppearance = null) =>
        Click(point, canvas, 1f, submit, cancel, delete, appearanceCatalog, previewAppearance);

    public void Click(
        CGPoint point,
        CGSize canvas,
        float uiScale,
        Action<HouseholdCreationRequest> submit,
        System.Action? cancel = null,
        Action<int>? delete = null,
        Action<string, string, string>? appearanceCatalog = null,
        Action<string, string, string, string, string>? previewAppearance = null)
    {
        ArgumentNullException.ThrowIfNull(submit);
        if (Submitting)
        {
            return;
        }

        (double left, double top, double factor) = Transform(canvas, uiScale);
        CGPoint logical = new((point.X - left) / factor, (point.Y - top) / factor);
        _error = "";

        if (_page == Page.Family)
        {
            if (new CGRect(260, 44, 282, 40).Contains(logical))
            {
                _field = Field.Family;
                _caretOffset = TextElementCount(_familyName);
                return;
            }

            for (int index = 0; index < _members.Count; index++)
            {
                if (MemberSlot(index).Contains(logical))
                {
                    _selectedMember = index;
                    PlaySound?.Invoke("ui_click");
                    return;
                }
            }

            if (new CGRect(593, 103, 139, 103).Contains(logical))
            {
                if (!CanAddMember) return;
                PlaySound?.Invoke("ui_click");
                _page = Page.Sim;
                _draft = new Draft();
                _editingMember = null;
                _field = Field.Sim;
                _caretOffset = 0;
                ResetAppearance(appearanceCatalog);
                return;
            }

            if (new CGRect(594, 311, 137, 99).Contains(logical))
            {
                if (!CanModifyMember) return;
                PlaySound?.Invoke("ui_click");
                _page = Page.Sim;
                _draft = _members[_selectedMember].Clone();
                _editingMember = _selectedMember;
                _field = Field.Sim;
                _caretOffset = TextElementCount(_draft.Name);
                RequestCurrentAppearance(appearanceCatalog);
                return;
            }

            if (new CGRect(594, 208, 149, 99).Contains(logical))
            {
                if (!CanModifyMember) return;
                PlaySound?.Invoke("ui_click");
                delete?.Invoke(_selectedMember);
                return;
            }

            if (CancelRect.Contains(logical))
            {
                PlaySound?.Invoke("ui_click");
                cancel?.Invoke();
                return;
            }

            if (DoneRect.Contains(logical))
            {
                if (!CanFinishFamily) return;
                PlaySound?.Invoke("ui_click");
                try
                {
                    var memberModels = new HouseholdCreationRequest.Member[_members.Count];
                    for (int i = 0; i < _members.Count; i++)
                    {
                        memberModels[i] = _members[i].ToMember();
                    }

                    var request = new HouseholdCreationRequest(_familyName, memberModels);
                    request.Validate();
                    submit(request);
                }
                catch (Exception ex)
                {
                    _error = Truncate(ex.Message, 100);
                }

                return;
            }
        }
        else
        {
            if (ZodiacRect.Contains(logical))
            {
                var traits = BuildTraitDictionary();
                int current = OriginalGameplayLayout.Zodiac(traits, _draft.Zodiac ?? 0);
                int selected = current % 12 + 1;
                if (OriginalGameplayLayout.ZodiacPersonality(selected) is { } profile)
                {
                    int[] updated = new int[TraitKeys.Length];
                    for (int i = 0; i < TraitKeys.Length; i++)
                    {
                        updated[i] = profile.TryGetValue(TraitKeys[i], out int val) ? val : 0;
                    }

                    _draft.Personality = updated;
                }

                _draft.Zodiac = selected;
                PlaySound?.Invoke("ui_cac_personpts");
                return;
            }

            if (NameRect.InsetBy(-8, -6).Contains(logical))
            {
                _field = Field.Sim;
                _caretOffset = TextElementCount(_draft.Name);
                return;
            }

            if (BioRect.Contains(logical))
            {
                _field = Field.Bio;
                _caretOffset = TextElementCount(_draft.Bio);
                return;
            }

            foreach (SimButtonSpec control in SimButtons)
            {
                if (SimButtonRect(control) is not { } rect || !rect.Contains(logical))
                {
                    continue;
                }

                switch (control.Key)
                {
                    case "light":
                    case "medium":
                    case "dark":
                        PlaySound?.Invoke("ui_click");
                        string skin = control.Key switch { "light" => "lgt", "medium" => "med", _ => "drk" };
                        if (!string.Equals(_draft.Skin, skin, StringComparison.Ordinal))
                        {
                            RememberCurrentAppearance();
                            _draft.Skin = skin;
                            ResetAppearance(appearanceCatalog);
                        }

                        break;
                    case "adult":
                    case "child":
                        PlaySound?.Invoke("ui_click");
                        if (_draft.Child != (control.Key == "child"))
                        {
                            RememberCurrentAppearance();
                            _draft.Child = !_draft.Child;
                            RestoreCachedAppearanceIndices();
                            ResetAppearance(appearanceCatalog);
                        }

                        break;
                    case "male":
                    case "female":
                        PlaySound?.Invoke("ui_click");
                        if (_draft.Female != (control.Key == "female"))
                        {
                            RememberCurrentAppearance();
                            _draft.Female = !_draft.Female;
                            RestoreCachedAppearanceIndices();
                            ResetAppearance(appearanceCatalog);
                        }

                        break;
                    default:
                        if (SimButtonFrame(control.Key) == 3)
                        {
                            return;
                        }

                        int offset = control.Key.StartsWith("previous", StringComparison.Ordinal) ? -1 : 1;
                        if (control.Key.EndsWith("Head", StringComparison.Ordinal))
                        {
                            CycleHead(offset);
                            PlaySound?.Invoke("ui_cac_cyclehead");
                        }
                        else
                        {
                            CycleBody(offset);
                            PlaySound?.Invoke("ui_cac_cycleparts");
                        }

                        RequestPreviewForCurrentSelection(previewAppearance);
                        break;
                }

                return;
            }

            for (int index = 0; index < 5; index++)
            {
                CGRect row = PersonalityRow(index);
                if (!row.InsetBy(0, -3).Contains(logical))
                {
                    continue;
                }

                // Increments or decrements a personality trait by exactly one point per click depending on
                // which side of the current level the pointer falls, rather than jumping directly to the
                // clicked pip.
                int current = _draft.Personality[index] / 100;
                double currentEnd = row.MinX + row.Width * current / 10.0;
                bool raising = logical.X > currentEnd;
                int[]? next = HouseholdPersonalityEditor.Step(_draft.Personality, index, raising);
                if (next is null || next.AsSpan().SequenceEqual(_draft.Personality))
                {
                    return;
                }

                _draft.Personality = next;
                // Clears manually selected astrological signs when modifying personality traits, as traits
                // automatically recompute the matching sign on edit.
                _draft.Zodiac = null;
                PlaySound?.Invoke("ui_cac_personpts");
                return;
            }

            if (CancelRect.Contains(logical))
            {
                PlaySound?.Invoke("ui_click");
                ClearPreview?.Invoke();
                _page = Page.Family;
                _field = Field.Family;
                _caretOffset = TextElementCount(_familyName);
                return;
            }

            if (DoneRect.Contains(logical))
            {
                if (!CanFinishSim) return;
                PlaySound?.Invoke("ui_click");
                void Finish()
                {
                    try
                    {
                        _draft.ToMember().Validate();
                        Draft saved = _draft.Clone();
                        if (_editingMember is { } editingIdx && editingIdx >= 0 && editingIdx < _members.Count)
                        {
                            _members[editingIdx] = saved;
                            _selectedMember = editingIdx;
                        }
                        else
                        {
                            _members.Add(saved);
                            _selectedMember = _members.Count - 1;
                        }

                        ClearPreview?.Invoke();
                        _page = Page.Family;
                        _field = Field.Family;
                        _caretOffset = TextElementCount(_familyName);
                    }
                    catch (Exception ex)
                    {
                        _error = Truncate(ex.Message, 100);
                    }
                }

                if (RemainingPoints > 0 && ConfirmUnspent is not null)
                {
                    ConfirmUnspent(Finish);
                }
                else
                {
                    Finish();
                }
            }
        }
    }

    public void DeleteMember(int index)
    {
        if (Submitting || index < 0 || index >= _members.Count)
        {
            return;
        }

        _members.RemoveAt(index);
        _selectedMember = -1;
    }

    public bool SubmitFamilyIfReady(Action<HouseholdCreationRequest> submit)
    {
        ArgumentNullException.ThrowIfNull(submit);
        if (_page != Page.Family || !CanFinishFamily)
        {
            return false;
        }

        Click(new CGPoint(DoneRect.MidX, DoneRect.MidY), new CGSize(800, 600), 1f, submit);
        return true;
    }

    public bool Key(string key, System.Action? cancel = null)
    {
        if (key == "\u001b")
        {
            if (_page == Page.Sim)
            {
                ClearPreview?.Invoke();
                _page = Page.Family;
                _field = Field.Family;
                _caretOffset = TextElementCount(_familyName);
            }
            else
            {
                cancel?.Invoke();
            }

            return true;
        }

        if (key == "\t")
        {
            if (_page == Page.Sim)
            {
                if (_field == Field.Bio)
                {
                    _field = Field.Sim;
                    _caretOffset = TextElementCount(_draft.Name);
                }
                else
                {
                    _field = Field.Bio;
                    _caretOffset = TextElementCount(_draft.Bio);
                }
            }

            return true;
        }

        if (key == "\r" || key == "\n")
        {
            if (_page == Page.Sim && _field == Field.Bio)
            {
                EditCurrentField("\n");
                return true;
            }

            if (_page == Page.Sim)
            {
                Click(new CGPoint(DoneRect.MidX, DoneRect.MidY), new CGSize(800, 600), 1f, _ => { });
            }

            return true;
        }

        if (key == "\uf702")
        {
            _caretOffset = Math.Max(0, _caretOffset - 1);
            return true;
        }

        if (key == "\uf703")
        {
            _caretOffset = Math.Min(TextElementCount(CurrentFieldText), _caretOffset + 1);
            return true;
        }

        if (key == "\uf729")
        {
            _caretOffset = 0;
            return true;
        }

        if (key == "\uf72b")
        {
            _caretOffset = TextElementCount(CurrentFieldText);
            return true;
        }

        EditCurrentField(key);
        return true;
    }

    private string CurrentFieldText => _field switch
    {
        Field.Family => _familyName,
        Field.Sim => _draft.Name,
        Field.Bio => _draft.Bio,
        _ => "",
    };

    private void SetCurrentFieldText(string value)
    {
        switch (_field)
        {
            case Field.Family:
                _familyName = value;
                break;
            case Field.Sim:
                _draft.Name = value;
                break;
            case Field.Bio:
                _draft.Bio = value;
                break;
        }
    }

    private static int TextElementCount(string value) =>
        value.Length == 0 ? 0 : StringInfo.ParseCombiningCharacters(value).Length;

    private static int CharIndexForElement(string value, int elementOffset, int[]? starts = null)
    {
        int[] boundaries = starts ?? (value.Length == 0 ? [] : StringInfo.ParseCombiningCharacters(value));
        if (elementOffset <= 0 || boundaries.Length == 0)
        {
            return 0;
        }

        return elementOffset >= boundaries.Length ? value.Length : boundaries[elementOffset];
    }

    private void EditCurrentField(string key)
    {
        string value = CurrentFieldText;
        HouseholdCreationRequest.TextField textField = _field switch
        {
            Field.Family => HouseholdCreationRequest.TextField.FamilyName,
            Field.Sim => HouseholdCreationRequest.TextField.FirstName,
            Field.Bio => HouseholdCreationRequest.TextField.Biography,
            _ => HouseholdCreationRequest.TextField.FamilyName,
        };

        int[] boundaries = value.Length == 0 ? [] : StringInfo.ParseCombiningCharacters(value);
        int elementCount = boundaries.Length;
        int boundedOffset = Math.Clamp(_caretOffset, 0, elementCount);

        if (key == "\u007f" || key == "\b")
        {
            if (boundedOffset <= 0)
            {
                return;
            }

            int start = boundaries[boundedOffset - 1];
            int end = boundedOffset < elementCount ? boundaries[boundedOffset] : value.Length;
            value = value.Remove(start, end - start);
            _caretOffset = boundedOffset - 1;
        }
        else if (key == "\uf728")
        {
            if (boundedOffset >= elementCount)
            {
                return;
            }

            int start = boundaries[boundedOffset];
            int end = (boundedOffset + 1) < elementCount ? boundaries[boundedOffset + 1] : value.Length;
            value = value.Remove(start, end - start);
            _caretOffset = boundedOffset;
        }
        else
        {
            if (!HouseholdCreationRequest.CanInsert(key, value, textField))
            {
                return;
            }

            int insertAt = CharIndexForElement(value, boundedOffset, boundaries);
            value = value.Insert(insertAt, key);
            _caretOffset = boundedOffset + TextElementCount(key);
        }

        SetCurrentFieldText(value);
    }

    private string WithCaret(string value)
    {
        int splitAt = CharIndexForElement(value, _caretOffset);
        return string.Concat(value.AsSpan(0, splitAt), "|", value.AsSpan(splitAt));
    }

    public void Failed(string message)
    {
        Submitting = false;
        _error = Truncate(message ?? "", 100);
    }

    public void PreviewFailed(string message) =>
        _error = Truncate(message ?? "", 100);

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];

    public void SetAppearanceCatalog(HouseholdAppearanceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        string expectedGender = _draft.Female ? "f" : "m";
        string expectedAge = _draft.Child ? "child" : "adult";
        if (!string.Equals(catalog.Gender, expectedGender, StringComparison.Ordinal) ||
            !string.Equals(catalog.Age, expectedAge, StringComparison.Ordinal) ||
            !string.Equals(catalog.Skin, _draft.Skin, StringComparison.Ordinal))
        {
            return;
        }

        _appearanceCatalog = catalog;
        var cached = _draft.AppearanceSelections.Selection(catalog.Gender, catalog.Age);
        int headIndex = HouseholdAppearanceSelectionCache.Sanitized(cached?.Head ?? _draft.HeadIndex, catalog.Heads.Count);
        int bodyIndex = HouseholdAppearanceSelectionCache.Sanitized(cached?.Body ?? _draft.BodyIndex, catalog.Bodies.Count);
        _draft.HeadIndex = Math.Max(0, headIndex);
        _draft.BodyIndex = Math.Max(0, bodyIndex);
        if (headIndex >= 0)
        {
            _draft.HeadChoice = catalog.Heads[headIndex].Id;
        }

        if (bodyIndex >= 0)
        {
            HouseholdAppearanceCatalog.Choice body = catalog.Bodies[bodyIndex];
            _draft.BodyChoice = body.Id;
            _draft.BodyType = body.BodyType;
        }

        RememberCurrentAppearance();
    }

    private void ResetAppearance(Action<string, string, string>? requestAppearance)
    {
        _appearanceCatalog = null;
        _draft.HeadChoice = null;
        _draft.BodyChoice = null;
        _draft.BodyType = "fit";
        ClearPreview?.Invoke();
        RequestCurrentAppearance(requestAppearance);
    }

    private void RequestCurrentAppearance(Action<string, string, string>? requestAppearance) =>
        requestAppearance?.Invoke(_draft.Female ? "f" : "m", _draft.Child ? "child" : "adult", _draft.Skin);

    private void RequestPreviewForCurrentSelection(Action<string, string, string, string, string>? requestPreview)
    {
        if (AppearancePreviewSelection() is not { } selection)
        {
            return;
        }

        requestPreview?.Invoke(selection.Gender, selection.Age, selection.Skin, selection.HeadChoice, selection.BodyChoice);
    }

    private void CycleHead(int offset)
    {
        IReadOnlyList<HouseholdAppearanceCatalog.Choice>? choices = _appearanceCatalog?.Heads;
        if (choices is null || choices.Count == 0)
        {
            return;
        }

        int current = _draft.HeadIndex;
        for (int i = 0; i < choices.Count; i++)
        {
            if (string.Equals(choices[i].Id, _draft.HeadChoice, StringComparison.Ordinal))
            {
                current = i;
                break;
            }
        }

        int next = (current + offset + choices.Count) % choices.Count;
        _draft.HeadIndex = next;
        _draft.HeadChoice = choices[next].Id;
        RememberCurrentAppearance();
    }

    private void CycleBody(int offset)
    {
        IReadOnlyList<HouseholdAppearanceCatalog.Choice>? choices = _appearanceCatalog?.Bodies;
        if (choices is null || choices.Count == 0)
        {
            return;
        }

        int current = _draft.BodyIndex;
        for (int i = 0; i < choices.Count; i++)
        {
            if (string.Equals(choices[i].Id, _draft.BodyChoice, StringComparison.Ordinal))
            {
                current = i;
                break;
            }
        }

        int next = (current + offset + choices.Count) % choices.Count;
        HouseholdAppearanceCatalog.Choice choice = choices[next];
        _draft.BodyIndex = next;
        _draft.BodyChoice = choice.Id;
        _draft.BodyType = choice.BodyType;
        RememberCurrentAppearance();
    }

    private void RememberCurrentAppearance()
    {
        string gender = _draft.Female ? "f" : "m";
        string age = _draft.Child ? "child" : "adult";
        if (_appearanceCatalog is not null &&
            string.Equals(_appearanceCatalog.Gender, gender, StringComparison.Ordinal) &&
            string.Equals(_appearanceCatalog.Age, age, StringComparison.Ordinal))
        {
            for (int i = 0; i < _appearanceCatalog.Heads.Count; i++)
            {
                if (string.Equals(_appearanceCatalog.Heads[i].Id, _draft.HeadChoice, StringComparison.Ordinal))
                {
                    _draft.HeadIndex = i;
                    break;
                }
            }

            for (int i = 0; i < _appearanceCatalog.Bodies.Count; i++)
            {
                if (string.Equals(_appearanceCatalog.Bodies[i].Id, _draft.BodyChoice, StringComparison.Ordinal))
                {
                    _draft.BodyIndex = i;
                    break;
                }
            }
        }

        _draft.AppearanceSelections.Remember(
            new HouseholdAppearanceSelectionCache.Indices(_draft.HeadIndex, _draft.BodyIndex),
            gender,
            age);
    }

    private void RestoreCachedAppearanceIndices()
    {
        string gender = _draft.Female ? "f" : "m";
        string age = _draft.Child ? "child" : "adult";
        var cached = _draft.AppearanceSelections.Selection(gender, age);
        _draft.HeadIndex = Math.Max(0, cached?.Head ?? 0);
        _draft.BodyIndex = Math.Max(0, cached?.Body ?? 0);
    }

    public void ShowSimPreview()
    {
        _page = Page.Sim;
        _field = Field.Sim;
        _draft = new Draft();
    }
}
