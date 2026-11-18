using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;
using SimsSDL;

namespace SimsSDL.UI;

/// <summary>Renders the Select a Family dialog displayed when choosing or moving in an unhoused family.</summary>
public sealed class OriginalFamilyPickerOverlay
{
    public sealed record Family(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("budget")] int Budget,
        [property: JsonPropertyName("netWorth")] int NetWorth,
        [property: JsonPropertyName("friends")] int Friends,
        [property: JsonPropertyName("members")] IReadOnlyList<string> Members,
        [property: JsonPropertyName("memberProfiles")] IReadOnlyList<NeighborhoodWebExport.MemberProfile?>? MemberProfiles,
        [property: JsonPropertyName("memberThumbnails")] IReadOnlyList<byte[]?>? MemberThumbnails,
        [property: JsonPropertyName("magiCoins")] int? MagiCoins = null)
    {
        public bool Equals(Family? other)
        {
            if (other is null)
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return Id == other.Id &&
                   string.Equals(Name, other.Name, StringComparison.Ordinal) &&
                   Budget == other.Budget &&
                   NetWorth == other.NetWorth &&
                   Friends == other.Friends &&
                   MagiCoins == other.MagiCoins &&
                   Members.SequenceEqual(other.Members, StringComparer.Ordinal) &&
                   NullableSequenceEqual(MemberProfiles, other.MemberProfiles) &&
                   ByteArraySequenceEqual(MemberThumbnails, other.MemberThumbnails);
        }

        public override int GetHashCode()
        {
            var hc = new HashCode();
            hc.Add(Id);
            hc.Add(Name, StringComparer.Ordinal);
            hc.Add(Budget);
            hc.Add(NetWorth);
            hc.Add(Friends);
            hc.Add(MagiCoins);
            foreach (string m in Members) hc.Add(m, StringComparer.Ordinal);
            if (MemberProfiles is not null)
            {
                hc.Add(MemberProfiles.Count);
                foreach (var p in MemberProfiles) hc.Add(p);
            }

            if (MemberThumbnails is not null)
            {
                hc.Add(MemberThumbnails.Count);
                foreach (var bytes in MemberThumbnails)
                {
                    if (bytes is null)
                    {
                        hc.Add(-1);
                    }
                    else
                    {
                        hc.Add(bytes.Length);
                        foreach (byte b in bytes) hc.Add(b);
                    }
                }
            }

            return hc.ToHashCode();
        }

        private static bool NullableSequenceEqual<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
        {
            if (a is null && b is null) return true;
            if (a is null || b is null) return false;
            return a.SequenceEqual(b);
        }

        private static bool ByteArraySequenceEqual(IReadOnlyList<byte[]?>? a, IReadOnlyList<byte[]?>? b)
        {
            if (a is null && b is null) return true;
            if (a is null || b is null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                var x = a[i];
                var y = b[i];
                if (x is null && y is null) continue;
                if (x is null || y is null || !x.AsSpan().SequenceEqual(y.AsSpan())) return false;
            }

            return true;
        }
    }

    public readonly record struct DrawItem(OriginalGameplayOverlay.Image Image, CGRect Rect, Vector4 UV);

    public abstract record Action
    {
        private Action() { }
        public sealed record CreateFamily : Action;
        public sealed record MoveIn(Family Family) : Action;
        public sealed record Delete(Family Family) : Action;
        public sealed record Cancel : Action;
    }

    public enum Control
    {
        Add = 0,
        Delete = 1,
        MoveIn = 2,
        Cancel = 3
    }

    private enum TextState
    {
        Normal = 0,
        Hover = 1,
        Selected = 2
    }

    private readonly record struct StateImages(
        OriginalGameplayOverlay.Image Normal,
        OriginalGameplayOverlay.Image Hover,
        OriginalGameplayOverlay.Image Selected)
    {
        public OriginalGameplayOverlay.Image ForState(TextState state) => state switch
        {
            TextState.Normal => Normal,
            TextState.Hover => Hover,
            TextState.Selected => Selected,
            _ => Normal
        };
    }

    private readonly record struct RowAssets(
        StateImages Name,
        StateImages NetWorth,
        StateImages Friends,
        StateImages? MagiCoins,
        double FriendsBoxWidth,
        double MagiCoinsBoxWidth);

    private static readonly Control[] AllControls =
    [
        Control.Add,
        Control.Delete,
        Control.MoveIn,
        Control.Cancel
    ];

    public static CGPoint RowOrigin => OriginalFamilyPickerLayout.RowOrigin;
    public static CGSize RowSize => OriginalFamilyPickerLayout.RowSize;
    public static double RowPitch => OriginalFamilyPickerLayout.RowPitch;
    public static int RowsPerPage => OriginalFamilyPickerLayout.RowsPerPage;
    public static CGRect ScrollbarRect => OriginalFamilyPickerLayout.ScrollbarRect;
    public static double ScrollArrowHeight => OriginalFamilyPickerLayout.ScrollArrowHeight;
    public static double ScrollThumbHeight => OriginalFamilyPickerLayout.ScrollThumbHeight;

    public static IReadOnlyDictionary<Control, CGRect> ControlRects { get; } = new Dictionary<Control, CGRect>
    {
        [Control.Add] = OriginalFamilyPickerLayout.AddRect,
        [Control.Delete] = OriginalFamilyPickerLayout.DeleteRect,
        [Control.MoveIn] = OriginalFamilyPickerLayout.MoveInRect,
        [Control.Cancel] = new CGRect(143, 529, 114, 62)
    };

    private static readonly (byte R, byte G, byte B) NormalTextColor = (195, 205, 205);
    private static readonly (byte R, byte G, byte B) HoverTextColor = (255, 255, 255);
    private static readonly (byte R, byte G, byte B) SelectedTextColor = (0, 255, 255);

    private readonly UIDevice _device;
    private readonly bool _quickTips;
    private readonly OriginalGameplayOverlay.Image _outerFill;
    private readonly OriginalGameplayOverlay.Image _background;
    private readonly OriginalGameplayOverlay.Image _row;
    private readonly OriginalGameplayOverlay.Image _peopleTemplate;
    private readonly OriginalGameplayOverlay.Image? _scrollBar;
    private readonly Dictionary<Control, (OriginalGameplayOverlay.Image Image, int Frames)> _buttons;
    private readonly OriginalGameplayOverlay.Image _tileButton;
    private readonly CGRect _cancelRect;
    private readonly OriginalGameplayOverlay.Image _cancelCaptionNormal;
    private readonly OriginalGameplayOverlay.Image _cancelCaptionHover;
    private readonly OriginalGameplayOverlay.Image _title;
    private readonly RowAssets[] _rowAssets;
    private readonly double _netWorthBoxWidth;
    private readonly OriginalGameplayOverlay.Image?[][] _memberPortraits;
    private readonly Dictionary<Control, string> _labels;
    private readonly Dictionary<Control, string> _controlTips;
    private readonly List<(CGRect Rect, string Text)> _headerTips;
    private readonly string _emptyListTip;
    private readonly SimsBitmapFont _tooltipFont;
    private readonly Dictionary<string, OriginalGameplayOverlay.Image> _tooltipCache = new(StringComparer.Ordinal);
    private (string Text, CGPoint Anchor)? _activeTooltip;
    private double? _tooltipStartedAt;

    private (int Row, int Member)? _hoveredMember;
    private int? _hoveredScrollArrow;
    private int _scroll;

    public int House { get; }
    public IReadOnlyList<Family> Families { get; }
    public bool IsMagicTown { get; }
    public int? Selected { get; private set; }
    public int? HoveredRow { get; private set; }
    public Control? HoveredControl { get; private set; }

    public Family? SelectedFamily =>
        Selected is { } idx && idx >= 0 && idx < Families.Count ? Families[idx] : null;

    public OriginalFamilyPickerOverlay(
        int house,
        IReadOnlyList<Family> families,
        GameContent content,
        SimsBitmapFont font,
        UIDevice device,
        OriginalUIStrings strings,
        bool isMagicTown = false,
        bool quickTips = true)
    {
        ArgumentNullException.ThrowIfNull(families);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(strings);

        House = house;
        Families = families;
        IsMagicTown = isMagicTown;
        _device = device;
        _quickTips = quickTips;

        OriginalGameplayOverlay.Image Upload(string path)
        {
            var bitmap = new SimsBitmap(content.Read(path));
            return OriginalGameplayOverlay.CreateImage(device, bitmap.Width, bitmap.Height, bitmap.Rgba, magenta: true);
        }

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

        OriginalGameplayOverlay.Image Render(string value, SimsBitmapFont targetFont, (byte R, byte G, byte B) color) =>
            OriginalGameplayOverlay.CreateImage(device, targetFont.RasterizeHiDpi(value, color));

        StateImages RenderStates(string value, SimsBitmapFont targetFont) => new(
            Render(value, targetFont, NormalTextColor),
            Render(value, targetFont, HoverTextColor),
            Render(value, targetFont, SelectedTextColor));

        var font16 = LoadFont("UIGraphics/Fonts/variablesans_16.ffn");
        var font12 = LoadFont("UIGraphics/Fonts/variablesans_12.ffn");
        var font10 = LoadFont("UIGraphics/Fonts/variablesans_10.ffn");
        SimsBitmapFont tipFont = font10;
        try
        {
            tipFont = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_08.ffn"));
        }
        catch
        {
        }
        _tooltipFont = tipFont;

        // Fills the letterboxed margin outside the centered 800x600 dialog with dark green (RGB 0, 50, 0).
        _outerFill = OriginalGameplayOverlay.CreateImage(device, 1, 1, [0, 50, 0, 255]);
        if (isMagicTown)
        {
            try
            {
                _background = Upload("UIGraphics/Nbhd/PickBkgMagic.bmp");
            }
            catch
            {
                _background = Upload("UIGraphics/Nbhd/PickBkg.bmp");
            }
        }
        else
        {
            _background = Upload("UIGraphics/Nbhd/PickBkg.bmp");
        }

        _row = Upload("UIGraphics/Nbhd/FamilyPickItemBkg.bmp");
        _peopleTemplate = Upload("UIGraphics/cpanel/People/PeopleTemplate.bmp");
        try
        {
            _scrollBar = Upload("UIGraphics/Nbhd/ScrollBar.bmp");
        }
        catch
        {
            _scrollBar = null;
        }

        _buttons = new Dictionary<Control, (OriginalGameplayOverlay.Image Image, int Frames)>
        {
            [Control.Add] = (Upload("UIGraphics/Nbhd/PAFAddFamily.bmp"), 4),
            [Control.Delete] = (Upload("UIGraphics/Nbhd/PAFDeleteFamily.bmp"), 4),
            [Control.MoveIn] = (Upload("UIGraphics/Nbhd/PAFMoveIn.bmp"), 4)
        };
        _tileButton = Upload("UIGraphics/Nbhd/NbhdTileBtn.bmp");

        _title = Render(strings.PickFamily(8, "SELECT A FAMILY"), font16, NormalTextColor);

        // Sizes the net worth column to fit the maximum currency amount "$999,999,999" plus 4 pixels of
        // padding.
        _netWorthBoxWidth = font10.TextWidth("$999,999,999") + 4;
        int minFriendsBoxWidth = font10.TextWidth("12") + 4;
        int minMagiBoxWidth = font10.TextWidth("9999") + 4;

        _rowAssets = new RowAssets[families.Count];
        for (int i = 0; i < families.Count; i++)
        {
            var family = families[i];
            string netWorthStr = OriginalUIStrings.Simoleons(family.NetWorth);
            string friendsStr = family.Friends.ToString(CultureInfo.InvariantCulture);
            double friendsWidth = Math.Max(minFriendsBoxWidth, font10.TextWidth(friendsStr) + 4);
            int magiCoinsValue = family.MagiCoins ?? 0;
            string magiStr = magiCoinsValue.ToString(CultureInfo.InvariantCulture);
            double magiWidth = Math.Max(minMagiBoxWidth, font10.TextWidth(magiStr) + 4);
            StateImages? magiStates = isMagicTown ? RenderStates(magiStr, font10) : null;
            _rowAssets[i] = new RowAssets(
                RenderStates(family.Name, font12),
                RenderStates(netWorthStr, font10),
                RenderStates(friendsStr, font10),
                magiStates,
                friendsWidth,
                magiWidth);
        }

        _memberPortraits = new OriginalGameplayOverlay.Image?[families.Count][];
        for (int i = 0; i < families.Count; i++)
        {
            var thumbs = families[i].MemberThumbnails;
            if (thumbs is null || thumbs.Count == 0)
            {
                _memberPortraits[i] = [];
                continue;
            }

            var portraits = new OriginalGameplayOverlay.Image?[thumbs.Count];
            for (int m = 0; m < thumbs.Count; m++)
            {
                var data = thumbs[m];
                if (data is null)
                {
                    continue;
                }

                try
                {
                    var bitmap = new SimsBitmap(data);
                    portraits[m] = OriginalGameplayOverlay.CreateImage(device, bitmap.Width, bitmap.Height, bitmap.Rgba, magenta: true);
                }
                catch
                {
                    portraits[m] = null;
                }
            }

            _memberPortraits[i] = portraits;
        }

        string cancelLabel = strings.PickFamily(5, "Cancel");
        _labels = new Dictionary<Control, string>
        {
            [Control.Add] = strings.PickFamily(2, "Create New Family"),
            [Control.Delete] = strings.PickFamily(4, "Delete Family"),
            [Control.MoveIn] = strings.PickFamily(3, "Move In Family"),
            [Control.Cancel] = cancelLabel
        };

        // Overrides the cancel button tooltip with string table 128 entry 15 ("Back to Neighborhood").
        _controlTips = new Dictionary<Control, string>
        {
            [Control.Add] = strings.PickFamily(2, "Create New Family"),
            [Control.Delete] = strings.PickFamily(4, "Delete Family"),
            [Control.MoveIn] = strings.PickFamily(3, "Move In Family"),
            [Control.Cancel] = strings.PickFamily(15, "Back to Neighborhood")
        };

        // Sizes the neighborhood button with 35-pixel side margins (text width plus 70, minimum 91 pixels),
        // centering it horizontally at x=200.
        double cancelWidth = Math.Max(91.0, font12.TextWidth(cancelLabel) + 70.0);
        _cancelRect = new CGRect(Math.Floor(200.0 - cancelWidth / 2.0), 529.0, cancelWidth, 62.0);
        _cancelCaptionNormal = Render(cancelLabel, font12, NormalTextColor);
        _cancelCaptionHover = Render(cancelLabel, font12, HoverTextColor);

        var tips = new List<(CGRect Rect, string Text)>(3)
        {
            (new CGRect(isMagicTown ? 563 : 586, 42, 26, 23), strings.PickFamily(10, "Net Worth")),
            (new CGRect(isMagicTown ? 637 : 662, 42, 26, 23), strings.PickFamily(11, "Family Friends"))
        };
        if (isMagicTown)
        {
            tips.Add((new CGRect(690, 42, 26, 23), strings.PickFamily(17, "Family MagiCoins")));
        }

        _headerTips = tips;
        _emptyListTip = strings.PickFamily(16, "Click to create a new family");

        // Initializes with no family selected, keeping Delete and Move In buttons disabled until a row is
        // chosen.
        Selected = null;
    }

    public bool IsEnabled(Control control) => control switch
    {
        Control.Add or Control.Cancel => true,
        Control.MoveIn or Control.Delete => SelectedFamily is not null,
        _ => false
    };

    private CGRect RectFor(Control control) =>
        control == Control.Cancel ? _cancelRect : (ControlRects.TryGetValue(control, out var r) ? r : CGRect.Zero);

    private bool HasScrollbar => _scrollBar is not null && Families.Count > RowsPerPage;

    private static CGRect UpArrowRect =>
        new(ScrollbarRect.MinX, ScrollbarRect.MinY, ScrollbarRect.Width, ScrollArrowHeight);

    private static CGRect DownArrowRect =>
        new(ScrollbarRect.MinX, ScrollbarRect.MaxY - ScrollArrowHeight, ScrollbarRect.Width, ScrollArrowHeight);

    private CGRect ThumbRect
    {
        get
        {
            double minY = ScrollbarRect.MinY + ScrollArrowHeight;
            double maxY = ScrollbarRect.MaxY - ScrollArrowHeight - ScrollThumbHeight;
            int maxScroll = Math.Max(1, Families.Count - RowsPerPage);
            double fraction = _scroll / (double)maxScroll;
            double y = Math.Round(minY + fraction * (maxY - minY), MidpointRounding.AwayFromZero);
            return new CGRect(ScrollbarRect.MinX, y, ScrollbarRect.Width, ScrollThumbHeight);
        }
    }

    private static (double Left, double Top, double Factor) Transform(CGSize canvas, float uiScale = 1f)
    {
        _ = uiScale;
        double factor = Math.Max(0.001, Math.Min(canvas.Width / 800.0, canvas.Height / 600.0));
        double left = (canvas.Width - 800.0 * factor) / 2.0;
        double top = (canvas.Height - 600.0 * factor) / 2.0;
        return (left, top, factor);
    }

    private static CGPoint Logical(CGPoint point, CGSize canvas, float uiScale = 1f)
    {
        var (left, top, factor) = Transform(canvas, uiScale);
        return new CGPoint((point.X - left) / factor, (point.Y - top) / factor);
    }

    private static CGRect RowRect(int visibleIndex) =>
        new(RowOrigin.X, RowOrigin.Y + visibleIndex * RowPitch, RowSize.Width, RowSize.Height);

    private int MemberSlotCount(int index) =>
        Math.Min(8, Math.Max(Families[index].Members.Count, _memberPortraits[index].Length));

    private bool HasMemberSlot(int rowIndex, int member)
    {
        bool hasPortrait = member < _memberPortraits[rowIndex].Length && _memberPortraits[rowIndex][member] is not null;
        bool hasName = member < Families[rowIndex].Members.Count && !string.IsNullOrEmpty(Families[rowIndex].Members[member]);
        return hasPortrait || hasName;
    }

    private static CGRect MemberFrameRect(CGRect rowRect, int member) =>
        // Lays out 45x45 family member portraits horizontally at 48-pixel intervals starting at offset
        // (110, 20) within the row.
        new(rowRect.MinX + 110.0 + member * 48.0, rowRect.MinY + 20.0, 45.0, 45.0);

    private int VisibleEnd => Math.Min(Families.Count, _scroll + RowsPerPage);

    private void SetActiveTooltip((string Text, CGPoint Anchor)? next)
    {
        if (next is not { } tip || string.IsNullOrEmpty(tip.Text))
        {
            _activeTooltip = null;
            _tooltipStartedAt = null;
            return;
        }

        if (!string.Equals(_activeTooltip?.Text, tip.Text, StringComparison.Ordinal))
        {
            _tooltipStartedAt = null;
        }

        _activeTooltip = tip;
    }

    public void Hover(CGPoint point, CGSize canvas, float uiScale = 1f)
    {
        var local = Logical(point, canvas, uiScale);
        Control? hitControl = null;
        foreach (var control in AllControls)
        {
            if (ContainsRect(RectFor(control), local))
            {
                hitControl = control;
                break;
            }
        }

        HoveredControl = hitControl is { } hc && IsEnabled(hc) ? hc : null;

        int? hitRow = null;
        for (int index = _scroll; index < VisibleEnd; index++)
        {
            if (ContainsRect(RowRect(index - _scroll), local))
            {
                hitRow = index;
                break;
            }
        }

        HoveredRow = hitRow;

        if (HasScrollbar)
        {
            if (ContainsRect(UpArrowRect, local))
            {
                _hoveredScrollArrow = -1;
            }
            else if (ContainsRect(DownArrowRect, local))
            {
                _hoveredScrollArrow = 1;
            }
            else
            {
                _hoveredScrollArrow = null;
            }
        }
        else
        {
            _hoveredScrollArrow = null;
        }

        _hoveredMember = null;
        if (HoveredRow is { } rowIndex)
        {
            var rRect = RowRect(rowIndex - _scroll);
            int slotCount = MemberSlotCount(rowIndex);
            for (int member = 0; member < slotCount; member++)
            {
                if (!HasMemberSlot(rowIndex, member))
                {
                    continue;
                }

                if (ContainsRect(MemberFrameRect(rRect, member), local))
                {
                    _hoveredMember = (rowIndex, member);
                    break;
                }
            }
        }

        // Resolves tooltip priority in order: member portrait, bottom buttons, column header icons, and
        // empty list well.
        if (_hoveredMember is { } hm && hm.Member < Families[hm.Row].Members.Count)
        {
            string memberName = Families[hm.Row].Members[hm.Member];
            if (!string.IsNullOrEmpty(memberName))
            {
                var frame = MemberFrameRect(RowRect(hm.Row - _scroll), hm.Member);
                SetActiveTooltip((memberName, new CGPoint(frame.MidX, frame.MinY - 1.0)));
                return;
            }
        }

        if (hitControl is { } ctrl && _controlTips.TryGetValue(ctrl, out var tip))
        {
            var r = RectFor(ctrl);
            SetActiveTooltip((tip, new CGPoint(r.MidX, r.MinY - 1.0)));
            return;
        }

        foreach (var header in _headerTips)
        {
            if (ContainsRect(header.Rect, local))
            {
                SetActiveTooltip((header.Text, new CGPoint(header.Rect.MidX, header.Rect.MinY - 1.0)));
                return;
            }
        }

        int visibleCount = Math.Max(0, VisibleEnd - _scroll);
        if (visibleCount < RowsPerPage)
        {
            var emptyRect = new CGRect(
                RowOrigin.X,
                RowOrigin.Y + visibleCount * RowPitch,
                RowSize.Width,
                (RowsPerPage - visibleCount) * RowPitch);
            if (ContainsRect(emptyRect, local))
            {
                SetActiveTooltip((_emptyListTip, new CGPoint(local.X, local.Y - 4.0)));
                return;
            }
        }

        SetActiveTooltip(null);
    }

    public Action? Click(CGPoint point, CGSize canvas, float uiScale = 1f)
    {
        var local = Logical(point, canvas, uiScale);
        if (HasScrollbar && ContainsRect(ScrollbarRect, local))
        {
            if (ContainsRect(UpArrowRect, local))
            {
                Scroll(-1);
            }
            else if (ContainsRect(DownArrowRect, local))
            {
                Scroll(1);
            }
            else
            {
                var currentThumb = ThumbRect;
                if (local.Y < currentThumb.MinY)
                {
                    Scroll(-RowsPerPage);
                }
                else if (local.Y > currentThumb.MaxY)
                {
                    Scroll(RowsPerPage);
                }
                else
                {
                    double minY = ScrollbarRect.MinY + ScrollArrowHeight;
                    double maxY = ScrollbarRect.MaxY - ScrollArrowHeight - ScrollThumbHeight;
                    int maxScroll = Math.Max(0, Families.Count - RowsPerPage);
                    if (maxY > minY && maxScroll > 0)
                    {
                        double ratio = Math.Min(1.0, Math.Max(0.0, (local.Y - ScrollThumbHeight / 2.0 - minY) / (maxY - minY)));
                        _scroll = (int)Math.Round(ratio * maxScroll, MidpointRounding.AwayFromZero);
                    }
                }
            }

            return null;
        }

        for (int index = _scroll; index < VisibleEnd; index++)
        {
            if (ContainsRect(RowRect(index - _scroll), local))
            {
                Selected = index;
                return null;
            }
        }

        Control? hit = null;
        foreach (var control in AllControls)
        {
            if (ContainsRect(RectFor(control), local))
            {
                hit = control;
                break;
            }
        }

        if (hit is not { } clicked || !IsEnabled(clicked))
        {
            return null;
        }

        return clicked switch
        {
            Control.Add => new Action.CreateFamily(),
            Control.MoveIn => SelectedFamily is { } moveFamily ? new Action.MoveIn(moveFamily) : null,
            Control.Cancel => new Action.Cancel(),
            Control.Delete => SelectedFamily is { } deleteFamily ? new Action.Delete(deleteFamily) : null,
            _ => null
        };
    }

    public void Scroll(int delta)
    {
        _scroll = Math.Min(Math.Max(0, _scroll + delta), Math.Max(0, Families.Count - RowsPerPage));
    }

    public void MoveSelection(int delta)
    {
        if (Families.Count == 0)
        {
            return;
        }

        int next = Selected is { } current
            ? Math.Min(Math.Max(0, current + delta), Families.Count - 1)
            : 0;

        Selected = next;
        if (next < _scroll)
        {
            _scroll = next;
        }
        else if (next >= _scroll + RowsPerPage)
        {
            _scroll = next - RowsPerPage + 1;
        }
    }

    public IReadOnlyList<(string Label, CGRect Rect)> Controls(CGSize canvas, float uiScale = 1f)
    {
        var (left, top, factor) = Transform(canvas, uiScale);
        CGRect Screen(CGRect r) =>
            new(left + r.MinX * factor, top + r.MinY * factor, r.Width * factor, r.Height * factor);

        var result = new List<(string Label, CGRect Rect)>();
        for (int index = _scroll; index < VisibleEnd; index++)
        {
            var family = Families[index];
            string members = string.Join(", ", family.Members.Where(m => !string.IsNullOrEmpty(m)));
            result.Add(($"{family.Name}. {OriginalUIStrings.Simoleons(family.NetWorth)}. {family.Friends}. {members}",
                        Screen(RowRect(index - _scroll))));
        }

        foreach (var control in AllControls)
        {
            if (!IsEnabled(control))
            {
                continue;
            }

            string label = _labels.TryGetValue(control, out var s) ? s : string.Empty;
            result.Add((label, Screen(RectFor(control))));
        }

        if (HasScrollbar)
        {
            result.Add(("Scroll Up", Screen(UpArrowRect)));
            result.Add(("Scroll Down", Screen(DownArrowRect)));
        }

        return result;
    }

    public IReadOnlyList<DrawItem> DrawItems(CGSize canvas, float uiScale = 1f)
    {
        var (left, top, factor) = Transform(canvas, uiScale);
        CGRect Screen(CGRect r) =>
            new(left + r.MinX * factor, top + r.MinY * factor, r.Width * factor, r.Height * factor);

        var whole = new Vector4(0f, 0f, 1f, 1f);
        bool sharp = SimsBitmapFont.SharpTextEnabled;
        var bgImg = sharp
            ? SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderFamilyPickerBackground(IsMagicTown, scale: 3))
            : _background;
        var rowImg = sharp
            ? SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderListRowStrip(
                Math.Max(64, (int)Math.Round(RowSize.Width)),
                Math.Max(24, (int)Math.Round(RowSize.Height)),
                scale: 3))
            : _row;
        var peopleImg = sharp
            ? SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderPeopleTemplateStrip(45, scale: 4))
            : _peopleTemplate;

        var items = new List<DrawItem>
        {
            new(_outerFill, new CGRect(0.0, 0.0, canvas.Width, canvas.Height), whole),
            new(bgImg, Screen(new CGRect(0.0, 0.0, 800.0, 600.0)), whole)
        };

        // Centers the dialog title horizontally across the 800-pixel frame at y=6, compensating for the
        // 3-pixel font padding.
        items.Add(new DrawItem(
            _title,
            Screen(new CGRect((800.0 - _title.Size.Width) / 2.0, 6.0 - 3.0, _title.Size.Width, _title.Size.Height)),
            whole));

        for (int slot = 0; slot < RowsPerPage; slot++)
        {
            var rect = RowRect(slot);
            int index = _scroll + slot;
            if (index >= Families.Count)
            {
                items.Add(new DrawItem(rowImg, Screen(rect), new Vector4(0f, 0f, 1f, 1f / 3f)));
                continue;
            }

            bool isRowSelected = Selected == index;
            bool isRowHovered = HoveredRow == index;
            float frame = isRowSelected ? 2f : isRowHovered ? 1f : 0f;
            items.Add(new DrawItem(rowImg, Screen(rect), new Vector4(0f, frame / 3f, 1f, 1f / 3f)));

            var textState = isRowSelected ? TextState.Selected : isRowHovered ? TextState.Hover : TextState.Normal;
            var assets = _rowAssets[index];

            var nameImage = assets.Name.ForState(textState);
            items.Add(new DrawItem(
                nameImage,
                Screen(new CGRect(rect.MinX + 22.0 - 3.0, rect.MinY - 3.0, nameImage.Size.Width, nameImage.Size.Height)),
                whole));

            int slotCount = MemberSlotCount(index);
            for (int member = 0; member < slotCount; member++)
            {
                if (!HasMemberSlot(index, member))
                {
                    continue;
                }

                var frameRect = MemberFrameRect(rect, member);
                bool isMemberHovered = _hoveredMember?.Row == index && _hoveredMember?.Member == member;
                float templateFrame = isMemberHovered ? 2f : isRowSelected ? 1f : 0f;
                items.Add(new DrawItem(
                    peopleImg,
                    Screen(frameRect),
                    new Vector4(templateFrame * 0.25f, 0f, 0.25f, 1f)));

                if (member < _memberPortraits[index].Length && _memberPortraits[index][member] is { } portrait)
                {
                    double pw = portrait.Size.Width;
                    double ph = portrait.Size.Height;
                    var portraitRect = new CGRect(
                        frameRect.MinX + Math.Floor((frameRect.Width - pw) / 2.0),
                        frameRect.MinY + Math.Floor((frameRect.Height - ph) / 2.0),
                        pw,
                        ph);
                    items.Add(new DrawItem(portrait, Screen(portraitRect), whole));
                }
            }

            var netWorthImage = assets.NetWorth.ForState(textState);
            double netWorthLeft = rect.MinX + (IsMagicTown ? 494.0 : 526.0);
            double netWorthRight = netWorthLeft + _netWorthBoxWidth - 2.0;
            items.Add(new DrawItem(
                netWorthImage,
                Screen(new CGRect(netWorthRight - netWorthImage.Size.Width + 3.0, rect.MinY + 21.0 - 3.0,
                                  netWorthImage.Size.Width, netWorthImage.Size.Height)),
                whole));

            var friendsImage = assets.Friends.ForState(textState);
            double friendsLeft = rect.MinX + (IsMagicTown ? 606.0 : 620.0);
            double friendsRight = friendsLeft + assets.FriendsBoxWidth - 2.0;
            items.Add(new DrawItem(
                friendsImage,
                Screen(new CGRect(friendsRight - friendsImage.Size.Width + 3.0, rect.MinY + 21.0 - 3.0,
                                  friendsImage.Size.Width, friendsImage.Size.Height)),
                whole));

            if (IsMagicTown && assets.MagiCoins is { } magiStates)
            {
                var magiImage = magiStates.ForState(textState);
                double magiRight = rect.MinX + 649.0 + assets.MagiCoinsBoxWidth - 2.0;
                items.Add(new DrawItem(
                    magiImage,
                    Screen(new CGRect(magiRight - magiImage.Size.Width + 3.0, rect.MinY + 21.0 - 3.0,
                                      magiImage.Size.Width, magiImage.Size.Height)),
                    whole));
            }
        }

        if (HasScrollbar && sharp)
        {
            int sw = Math.Max(16, (int)Math.Round(ScrollbarRect.Width));
            int sh = Math.Max(20, (int)Math.Round(ScrollArrowHeight));
            var upImg = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderScrollbarCap(sw, sh, kind: 1, hovered: _hoveredScrollArrow == -1, scale: 4));
            var trackImg = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderScrollbarCap(sw, sh, kind: 3, scale: 4));
            var downImg = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderScrollbarCap(sw, sh, kind: 2, hovered: _hoveredScrollArrow == 1, scale: 4));
            var thumbRect = ThumbRect;
            int th = Math.Max(20, (int)Math.Round(thumbRect.Height));
            var thumbImg = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderScrollbarCap(sw, th, kind: 0, scale: 4));

            items.Add(new DrawItem(upImg, Screen(UpArrowRect), whole));
            double trackH = Math.Max(0.0, DownArrowRect.MinY - UpArrowRect.MaxY);
            if (trackH > 0.0)
                items.Add(new DrawItem(trackImg, Screen(new CGRect(ScrollbarRect.MinX, UpArrowRect.MaxY, ScrollbarRect.Width, trackH)), whole));
            items.Add(new DrawItem(downImg, Screen(DownArrowRect), whole));
            items.Add(new DrawItem(thumbImg, Screen(thumbRect), whole));
        }
        else if (HasScrollbar && _scrollBar is { } scrollBar)
        {
            const float tileU = 25f / 300f;
            float upFrame = _hoveredScrollArrow == -1 ? 1f : 0f;
            items.Add(new DrawItem(scrollBar, Screen(UpArrowRect), new Vector4(upFrame * tileU, 0f, tileU, 1f)));

            double trackY = UpArrowRect.MaxY;
            double trackEndY = DownArrowRect.MinY;
            while (trackY < trackEndY)
            {
                double segH = Math.Min(ScrollArrowHeight, trackEndY - trackY);
                float dv = (float)(segH / ScrollArrowHeight);
                items.Add(new DrawItem(
                    scrollBar,
                    Screen(new CGRect(ScrollbarRect.MinX, trackY, ScrollbarRect.Width, segH)),
                    new Vector4(3f * tileU, 0f, tileU, dv)));
                trackY += segH;
            }

            float downFrame = _hoveredScrollArrow == 1 ? 5f : 4f;
            items.Add(new DrawItem(scrollBar, Screen(DownArrowRect), new Vector4(downFrame * tileU, 0f, tileU, 1f)));

            items.Add(new DrawItem(scrollBar, Screen(ThumbRect), new Vector4(2f * tileU, 0f, tileU, 1f)));
        }

        foreach (var control in AllControls)
        {
            if (!_buttons.TryGetValue(control, out var sheet))
            {
                continue;
            }

            var cRect = RectFor(control);
            int frame = !IsEnabled(control) ? 3 : HoveredControl == control ? 2 : 0;
            items.Add(new DrawItem(
                sheet.Image,
                Screen(cRect),
                new Vector4(frame / (float)sheet.Frames, 0f, 1f / sheet.Frames, 1f)));
        }

        {
            var cRect = _cancelRect;
            bool isHovered = HoveredControl == Control.Cancel;
            float frame = isHovered ? 2f : 0f;
            if (sharp)
            {
                var vecCancel = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderSlantedButtonStrip(
                    Math.Max(32, (int)Math.Round(cRect.Width)),
                    Math.Max(24, (int)Math.Round(cRect.Height)),
                    scale: 4));
                items.Add(new DrawItem(
                    vecCancel,
                    Screen(cRect),
                    new Vector4(frame * 0.25f, 0f, 0.25f, 1f)));
            }
            else
            {
                double cap = Math.Min(45.0, cRect.Width / 2.0);
                float u = (float)(cap / _tileButton.Size.Width);
                const float quarter = 0.25f;
                (double X, double Width, float U0, float Du)[] slices =
                [
                    (cRect.MinX, cap, frame * quarter, u),
                    (cRect.MinX + cap, cRect.Width - cap * 2.0, frame * quarter + u, quarter - u * 2f),
                    (cRect.MaxX - cap, cap, (frame + 1f) * quarter - u, u)
                ];
                foreach (var (x, width, u0, du) in slices)
                {
                    if (width <= 0.0)
                    {
                        continue;
                    }

                    items.Add(new DrawItem(
                        _tileButton,
                        Screen(new CGRect(x, cRect.MinY, width, cRect.Height)),
                        new Vector4(u0, 0f, du, 1f)));
                }
            }

            var caption = isHovered ? _cancelCaptionHover : _cancelCaptionNormal;
            items.Add(new DrawItem(
                caption,
                Screen(new CGRect(
                    cRect.MidX - caption.Size.Width / 2.0,
                    cRect.MidY - caption.Size.Height / 2.0,
                    caption.Size.Width,
                    caption.Size.Height)),
                whole));
        }

        if (_activeTooltip is { } activeTip &&
            PlacementTooltip.Progress(ref _tooltipStartedAt, _quickTips) is { } progress)
        {
            OriginalGameplayOverlay.Image? tipImage;
            if (_tooltipCache.TryGetValue(activeTip.Text, out var cached))
            {
                tipImage = cached;
            }
            else
            {
                try
                {
                    var uploaded = OriginalGameplayOverlay.CreateImage(_device, PlacementTooltip.RasterizeHiDpi(activeTip.Text, _tooltipFont));
                    _tooltipCache[activeTip.Text] = uploaded;
                    tipImage = uploaded;
                }
                catch
                {
                    tipImage = null;
                }
            }

            if (tipImage is { } resolvedTip)
            {
                var scaledSize = new CGSize(resolvedTip.Size.Width * factor, resolvedTip.Size.Height * factor);
                var panelSize = new CGSize(800.0 * factor, 600.0 * factor);
                var panelAnchor = new CGPoint(activeTip.Anchor.X * factor, activeTip.Anchor.Y * factor);
                var fullRect = PlacementTooltip.Rect(scaledSize, panelAnchor, panelSize).OffsetBy(left, top);
                if (progress < 1.0)
                {
                    double h = Math.Max(1.0, Math.Round(fullRect.Height * progress, MidpointRounding.AwayFromZero));
                    float frac = (float)(h / fullRect.Height);
                    var grownRect = new CGRect(fullRect.MinX, fullRect.MaxY - h, fullRect.Width, h);
                    items.Add(new DrawItem(resolvedTip, grownRect, new Vector4(0f, 1f - frac, 1f, frac)));
                }
                else
                {
                    items.Add(new DrawItem(resolvedTip, fullRect, whole));
                }
            }
        }

        return items;
    }

    private static bool ContainsRect(CGRect rect, CGPoint point) =>
        rect.Width > 0.0 && rect.Height > 0.0 &&
        point.X >= rect.MinX && point.X < rect.MaxX &&
        point.Y >= rect.MinY && point.Y < rect.MaxY;
}
