using System.Numerics;
using System.Text.Json.Serialization;
using SimsSDL;

namespace SimsSDL.UI;

public sealed class GenericSimsCallChooserOverlay
{
    public sealed record Candidate(
        [property: JsonPropertyName("personRecordIndex")] int PersonRecordIndex,
        [property: JsonPropertyName("livePersonId")] int? LivePersonId,
        [property: JsonPropertyName("neighborName")] string? NeighborName,
        [property: JsonPropertyName("catalogName")] string? CatalogName,
        [property: JsonPropertyName("typeName")] string TypeName)
    {
        [JsonIgnore]
        public string Title => NeighborName ?? CatalogName ?? TypeName;
    }

    public struct DrawItem
    {
        public OriginalGameplayOverlay.Image Image { get; init; }
        public CGRect Rect { get; init; }
        public Vector4 UV { get; set; }

        public DrawItem(OriginalGameplayOverlay.Image image, CGRect rect)
            : this(image, rect, new Vector4(0f, 0f, 1f, 1f))
        {
        }

        public DrawItem(OriginalGameplayOverlay.Image image, CGRect rect, Vector4 uv)
        {
            Image = image;
            Rect = rect;
            UV = uv;
        }
    }

    private readonly record struct RowAssets(
        OriginalGameplayOverlay.Image Normal,
        OriginalGameplayOverlay.Image Selected,
        OriginalGameplayOverlay.Image Hover,
        OriginalGameplayOverlay.Image Disabled,
        OriginalGameplayOverlay.Image? Portrait,
        int PortraitColumns,
        int PortraitRows);

    private readonly record struct ButtonLabels(
        OriginalGameplayOverlay.Image Normal,
        OriginalGameplayOverlay.Image Pressed,
        OriginalGameplayOverlay.Image Hover,
        OriginalGameplayOverlay.Image Disabled);

    private const int MaxVisibleRows = 6;
    private const double RowHeight = 49.0;
    private const double ScrollbarWidth = 17.0; // Scrollbar texture contains 12 horizontal cells of 17x20 pixels each.
    private const double ScrollbarButtonHeight = 20.0;
    private const double ScrollbarReservedWidth = 20.0;

    private readonly IReadOnlyList<Candidate> _candidates;
    private readonly OriginalGameplayOverlay.Image _frame;
    private readonly double _frameEdge;
    private readonly OriginalGameplayOverlay.Image? _peopleTemplate;
    private readonly OriginalGameplayOverlay.Image? _unknownFace;
    private readonly OriginalGameplayOverlay.Image? _scrollbar;
    private readonly OriginalGameplayOverlay.Image _button;
    private readonly float _buttonSheetWidth;
    private readonly double _buttonCapWidth;
    private readonly OriginalGameplayOverlay.Image _title;
    private readonly RowAssets[] _rows;
    private readonly string _okCaption;
    private readonly ButtonLabels _okLabels;
    private readonly CGSize _okSize;
    private readonly double _preferredListWidth;
    private int _scroll;
    private CGPoint? _hoveredPoint;

    public SimsBitmapFont TitleFont { get; }
    public SimsBitmapFont ButtonFont { get; }
    public int Selected { get; private set; }
    public int? Hovered { get; private set; }
    public bool OkHovered { get; private set; }
    public bool OkPressed { get; private set; }

    public int? SelectedPersonRecordIndex =>
        Selected >= 0 && Selected < _candidates.Count && _candidates[Selected].LivePersonId is not null
            ? _candidates[Selected].PersonRecordIndex
            : null;

    public GenericSimsCallChooserOverlay(
        IReadOnlyList<Candidate> candidates,
        SimsBitmapFont font,
        UIDevice device,
        GameContent? content = null,
        OriginalUIStrings? strings = null,
        IReadOnlyList<GameplayPanelState.Sim>? sims = null,
        string? sceneDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(font);
        _candidates = candidates;

        SimsBitmapFont loadedTitleFont = font;
        if (content is not null)
        {
            try
            {
                loadedTitleFont = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_12.ffn"));
            }
            catch
            {
                loadedTitleFont = font;
            }
        }

        ButtonFont = font;
        TitleFont = loadedTitleFont;

        (byte R, byte G, byte B) normalColor = (195, 205, 205);
        (byte R, byte G, byte B) selectedColor = (0, 255, 255);
        (byte R, byte G, byte B) hoverColor = (255, 255, 255);
        (byte R, byte G, byte B) disabledColor = (64, 93, 95);

        SimsBitmap? TryReadBitmap(string path)
        {
            if (content is null)
            {
                return null;
            }

            try
            {
                return new SimsBitmap(content.Read(path));
            }
            catch
            {
                return null;
            }
        }

        if (TryReadBitmap("UIGraphics/CPanel/Backgrounds/PopupInfoTiles.bmp") is { } popup)
        {
            _frame = OriginalGameplayOverlay.CreateImage(device, popup.Width, popup.Height, popup.Rgba, magenta: false);
            _frameEdge = Math.Max(1, popup.Width / 3);
        }
        else
        {
            var fallback = new byte[36];
            Array.Fill(fallback, (byte)48);
            _frame = OriginalGameplayOverlay.CreateImage(device, 3, 3, fallback);
            _frameEdge = 12.0;
        }

        _peopleTemplate = TryReadBitmap("UIGraphics/cpanel/People/PeopleTemplate.bmp") is { } peopleBmp
            ? OriginalGameplayOverlay.CreateImage(device, peopleBmp.Width, peopleBmp.Height, peopleBmp.Rgba, magenta: true)
            : null;

        _unknownFace = TryReadBitmap("UIGraphics/cpanel/Buttons/UnknownFace.bmp") is { } unknownBmp
            ? OriginalGameplayOverlay.CreateImage(device, unknownBmp.Width, unknownBmp.Height, unknownBmp.Rgba, magenta: true)
            : null;

        _scrollbar = TryReadBitmap("UIGraphics/Shared/Sys/WinScrol.bmp") is { } scrollBmp
            ? OriginalGameplayOverlay.CreateImage(device, scrollBmp.Width, scrollBmp.Height, scrollBmp.Rgba, magenta: false)
            : null;

        if (TryReadBitmap("UIGraphics/Shared/Sys/WinBtn.bmp") is { } btnBmp)
        {
            _button = OriginalGameplayOverlay.CreateImage(device, btnBmp.Width, btnBmp.Height, btnBmp.Rgba, magenta: true);
            _buttonSheetWidth = Math.Max(1, btnBmp.Width);
            _buttonCapWidth = Math.Max(1, (btnBmp.Width / 4 - 1) / 2);
        }
        else
        {
            var fallbackBtn = new byte[64 * 33 * 4];
            Array.Fill(fallbackBtn, (byte)80);
            _button = OriginalGameplayOverlay.CreateImage(device, 64, 33, fallbackBtn);
            _buttonSheetWidth = 64f;
            _buttonCapWidth = 7.0;
        }

        _title = OriginalGameplayOverlay.Text("Choose a Sim", loadedTitleFont, device, normalColor);

        var simPortraitsById = new Dictionary<int, (OriginalGameplayOverlay.Image Image, int Columns, int Rows)>();
        if (sims is not null)
        {
            foreach (var sim in sims)
            {
                if (sim.Portrait is not { } reference)
                {
                    continue;
                }

                try
                {
                    if (GameDialog.ReadBitmap(reference, sceneDirectory) is { } bmp)
                    {
                        var img = OriginalGameplayOverlay.CreateImage(device, bmp.Width, bmp.Height, bmp.Rgba, magenta: true);
                        simPortraitsById[sim.Id] = (img, Math.Max(1, reference.Columns), Math.Max(1, reference.Rows));
                    }
                }
                catch
                {
                }
            }
        }

        var loadedRows = new RowAssets[candidates.Count];
        double widestLabel = 0.0;
        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            string text = candidate.LivePersonId is null ? $"{candidate.Title}  (not available yet)" : candidate.Title;
            var normal = OriginalGameplayOverlay.Text(text, font, device, normalColor);
            var sel = OriginalGameplayOverlay.Text(text, font, device, selectedColor);
            var hov = OriginalGameplayOverlay.Text(text, font, device, hoverColor);
            var dis = OriginalGameplayOverlay.Text(text, font, device, disabledColor);
            widestLabel = Math.Max(widestLabel, Math.Max(normal.Size.Width, dis.Size.Width));

            if (candidate.LivePersonId is { } liveId && simPortraitsById.TryGetValue(liveId, out var loadedPortrait))
            {
                loadedRows[i] = new RowAssets(
                    normal,
                    sel,
                    hov,
                    dis,
                    loadedPortrait.Image,
                    loadedPortrait.Columns,
                    loadedPortrait.Rows);
            }
            else
            {
                loadedRows[i] = new RowAssets(normal, sel, hov, dis, null, 1, 1);
            }
        }

        _rows = loadedRows;

        _okCaption = strings?.OkButton() ?? "OK";
        _okLabels = new ButtonLabels(
            OriginalGameplayOverlay.Text(_okCaption, ButtonFont, device, normalColor),
            OriginalGameplayOverlay.Text(_okCaption, ButtonFont, device, selectedColor),
            OriginalGameplayOverlay.Text(_okCaption, ButtonFont, device, hoverColor),
            OriginalGameplayOverlay.Text(_okCaption, ButtonFont, device, disabledColor));
        _okSize = new CGSize(Math.Max(100.0, Math.Ceiling(_okLabels.Normal.Size.Width) + 20.0), _button.Size.Height);

        double effectiveWidest = candidates.Count > 0 ? widestLabel : 160.0;
        double reserve = candidates.Count > MaxVisibleRows ? ScrollbarReservedWidth + 8.0 : 12.0;
        _preferredListWidth = Math.Max(300.0, Math.Ceiling(effectiveWidest) + 56.0 + reserve);

        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].LivePersonId is not null)
            {
                Selected = i;
                break;
            }
        }
    }

    private int VisibleCount(CGSize canvas)
    {
        if (_candidates.Count == 0)
        {
            return 1;
        }

        int maxFit = canvas.Height > 0.0
            ? Math.Max(1, (int)Math.Floor((canvas.Height - 140.0) / RowHeight))
            : MaxVisibleRows;
        return Math.Min(_candidates.Count, Math.Min(MaxVisibleRows, maxFit));
    }

    private int MaxScroll(CGSize canvas) =>
        Math.Max(0, _candidates.Count - VisibleCount(canvas));

    private int ClampedScroll(CGSize canvas) =>
        Math.Min(Math.Max(0, _scroll), MaxScroll(canvas));

    private CGRect PanelRect(CGSize canvas)
    {
        int visible = VisibleCount(canvas);
        double listH = visible * RowHeight;
        double minW = Math.Max(
            _preferredListWidth + 40.0,
            Math.Max(Math.Ceiling(_title.Size.Width) + 48.0, _okSize.Width + 48.0));
        double width = canvas.Width > 0.0 ? Math.Min(minW, Math.Max(240.0, canvas.Width - 32.0)) : minW;
        double height = 44.0 + listH + 14.0 + _okSize.Height + 16.0;
        var origin = canvas.Width > 0.0 && canvas.Height > 0.0
            ? new CGPoint(Math.Floor((canvas.Width - width) / 2.0), Math.Floor((canvas.Height - height) / 2.0))
            : new CGPoint(80.0, 60.0);
        return new CGRect(origin, new CGSize(width, height));
    }

    private CGRect ListRect(CGSize canvas)
    {
        var panel = PanelRect(canvas);
        int visible = VisibleCount(canvas);
        return new CGRect(panel.MinX + 20.0, panel.MinY + 44.0, panel.Width - 40.0, visible * RowHeight);
    }

    private CGRect ScrollbarRect(CGSize canvas)
    {
        var list = ListRect(canvas);
        return new CGRect(list.MaxX - ScrollbarWidth, list.MinY, ScrollbarWidth, list.Height);
    }

    private CGRect OkRect(CGSize canvas)
    {
        var panel = PanelRect(canvas);
        return new CGRect(
            Math.Floor(panel.MidX - _okSize.Width / 2.0),
            panel.MaxY - _okSize.Height - 16.0,
            _okSize.Width,
            _okSize.Height);
    }

    private CGRect RowRect(int visibleOffset, CGSize canvas)
    {
        var list = ListRect(canvas);
        bool hasScroll = MaxScroll(canvas) > 0 && _scrollbar is not null;
        double rowW = hasScroll ? Math.Max(1.0, list.Width - ScrollbarReservedWidth) : list.Width;
        return new CGRect(list.MinX, list.MinY + visibleOffset * RowHeight, rowW, RowHeight);
    }

    public IReadOnlyList<DrawItem> DrawItems(CGSize canvas)
    {
        var items = new List<DrawItem>();
        var panel = PanelRect(canvas);
        double[] xs = [panel.MinX, panel.MinX + _frameEdge, panel.MaxX - _frameEdge, panel.MaxX];
        double[] ys = [panel.MinY, panel.MinY + _frameEdge, panel.MaxY - _frameEdge, panel.MaxY];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                items.Add(new DrawItem(
                    _frame,
                    new CGRect(xs[column], ys[row], xs[column + 1] - xs[column], ys[row + 1] - ys[row]),
                    new Vector4(column / 3f, row / 3f, 1f / 3f, 1f / 3f)));
            }
        }

        items.Add(new DrawItem(
            _title,
            new CGRect(
                Math.Floor(panel.MidX - _title.Size.Width / 2.0),
                panel.MinY + 14.0,
                _title.Size.Width,
                _title.Size.Height)));

        var list = ListRect(canvas);
        items.Add(new DrawItem(_frame, list, new Vector4(1f / 3f, 1f / 3f, 1f / 3f, 1f / 3f)));

        int currentScroll = ClampedScroll(canvas);
        int visible = VisibleCount(canvas);
        for (int offset = 0; offset < visible; offset++)
        {
            int index = currentScroll + offset;
            if (index < 0 || index >= _candidates.Count)
            {
                break;
            }

            var row = RowRect(offset, canvas);
            var candidate = _candidates[index];
            var asset = _rows[index];
            bool isDisabled = candidate.LivePersonId is null;
            bool isSelected = index == Selected && !isDisabled;
            bool isHovered = Hovered == index && !isDisabled;

            var thumbRect = new CGRect(row.MinX + 2.0, row.MinY + 2.0, 45.0, 45.0);
            if (_peopleTemplate is { } template)
            {
                float col = isDisabled ? 3f : isSelected ? 1f : isHovered ? 2f : 0f;
                items.Add(new DrawItem(template, thumbRect, new Vector4(col * 0.25f, 0f, 0.25f, 1f)));
            }

            if (asset.Portrait is { } portrait)
            {
                int cols = Math.Max(1, asset.PortraitColumns);
                int rws = Math.Max(1, asset.PortraitRows);
                double frameW = portrait.Size.Width / cols;
                double frameH = portrait.Size.Height / rws;
                var uv = new Vector4(0f, 0f, 1f / cols, 1f / rws);
                if (frameW <= 41.0 && frameH <= 43.0)
                {
                    var faceRect = new CGRect(
                        thumbRect.MinX + Math.Floor((45.0 - frameW) / 2.0),
                        thumbRect.MinY + Math.Floor((45.0 - frameH) / 2.0),
                        frameW,
                        frameH);
                    items.Add(new DrawItem(portrait, faceRect, uv));
                }
                else
                {
                    items.Add(new DrawItem(portrait, thumbRect, uv));
                }
            }
            else if (_unknownFace is { } unknown)
            {
                double faceW = unknown.Size.Width / 3.0;
                double faceH = unknown.Size.Height;
                var faceRect = new CGRect(
                    thumbRect.MinX + Math.Floor((45.0 - faceW) / 2.0),
                    thumbRect.MinY + Math.Floor((45.0 - faceH) / 2.0),
                    faceW,
                    faceH);
                items.Add(new DrawItem(unknown, faceRect, new Vector4(0f, 0f, 1f / 3f, 1f)));
            }

            var label = isDisabled ? asset.Disabled : isSelected ? asset.Selected : isHovered ? asset.Hover : asset.Normal;
            double maxTextW = Math.Max(0.0, row.MaxX - (thumbRect.MaxX + 8.0) - 4.0);
            double drawW = Math.Min(label.Size.Width, maxTextW);
            if (drawW > 0.0 && label.Size.Width > 0.0)
            {
                items.Add(new DrawItem(
                    label,
                    new CGRect(
                        thumbRect.MaxX + 8.0,
                        Math.Floor(row.MidY - label.Size.Height / 2.0),
                        drawW,
                        label.Size.Height),
                    new Vector4(0f, 0f, (float)(drawW / label.Size.Width), 1f)));
            }
        }

        int maxScr = MaxScroll(canvas);
        if (maxScr > 0 && _scrollbar is not null)
        {
            AppendVerticalScrollbar(ScrollbarRect(canvas), currentScroll, maxScr, items);
        }

        var ok = OkRect(canvas);
        bool okDisabled = SelectedPersonRecordIndex is null;
        int okState = okDisabled ? 3 : (OkPressed && OkHovered) ? 1 : OkHovered ? 2 : 0;
        AppendTileButton(ok, okState, items);
        var okLabel = okState == 3
            ? _okLabels.Disabled
            : okState == 1
                ? _okLabels.Pressed
                : okState == 2
                    ? _okLabels.Hover
                    : _okLabels.Normal;
        items.Add(new DrawItem(
            okLabel,
            new CGRect(
                Math.Floor(ok.MidX - okLabel.Size.Width / 2.0),
                Math.Floor(ok.MidY - okLabel.Size.Height / 2.0),
                okLabel.Size.Width,
                okLabel.Size.Height)));
        return items;
    }

    private void AppendTileButton(CGRect bounds, int frameIndex, List<DrawItem> items)
    {
        // Slices button textures horizontally using 7px end caps with a 2px stretchable center segment.
        float framePx = _buttonSheetWidth / 4f;
        float basePx = frameIndex * framePx;
        float capPx = (float)_buttonCapWidth;
        double capW = Math.Min(_buttonCapWidth, Math.Floor(bounds.Width / 2.0));
        double midW = Math.Max(0.0, bounds.Width - capW * 2.0);
        items.Add(new DrawItem(
            _button,
            new CGRect(bounds.MinX, bounds.MinY, capW, bounds.Height),
            new Vector4(basePx / _buttonSheetWidth, 0f, capPx / _buttonSheetWidth, 1f)));
        if (midW > 0.0)
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

    private static CGRect ScrollbarThumbRect(CGRect bar, int scroll, int maxScroll)
    {
        const double btnH = ScrollbarButtonHeight;
        double minY = bar.MinY + btnH;
        double maxY = Math.Max(minY, bar.MaxY - btnH * 2.0);
        double fraction = maxScroll > 0 ? Math.Min(Math.Max(0, scroll), maxScroll) / (double)maxScroll : 0.0;
        double thumbY = minY + Math.Round(fraction * (maxY - minY), MidpointRounding.AwayFromZero);
        return new CGRect(bar.MinX, thumbY, bar.Width, btnH);
    }

    private void AppendVerticalScrollbar(CGRect bar, int scroll, int maxScroll, List<DrawItem> items)
    {
        if (_scrollbar is not { } scrollbar)
        {
            return;
        }

        const float cellW = 1f / 12f;
        const double btnH = ScrollbarButtonHeight;
        var upRect = new CGRect(bar.MinX, bar.MinY, bar.Width, btnH);
        var downRect = new CGRect(bar.MinX, bar.MaxY - btnH, bar.Width, btnH);
        var trackRect = new CGRect(bar.MinX, bar.MinY + btnH, bar.Width, Math.Max(0.0, bar.Height - btnH * 2.0));
        bool upActive = scroll > 0 && _hoveredPoint is { } pUp && ContainsRect(upRect, pUp);
        bool downActive = scroll < maxScroll && _hoveredPoint is { } pDown && ContainsRect(downRect, pDown);
        items.Add(new DrawItem(scrollbar, upRect, new Vector4((upActive ? 1f : 0f) * cellW, 0f, cellW, 1f)));
        if (trackRect.Height > 0.0)
        {
            items.Add(new DrawItem(scrollbar, trackRect, new Vector4(3f * cellW, 0f, cellW, 1f)));
        }

        items.Add(new DrawItem(scrollbar, downRect, new Vector4((downActive ? 5f : 4f) * cellW, 0f, cellW, 1f)));
        if (maxScroll > 0 && trackRect.Height >= btnH)
        {
            var thumbRect = ScrollbarThumbRect(bar, scroll, maxScroll);
            items.Add(new DrawItem(scrollbar, thumbRect, new Vector4(2f * cellW, 0f, cellW, 1f)));
        }
    }

    public IReadOnlyList<(string Label, CGRect Rect)> Controls(CGSize canvas)
    {
        int currentScroll = ClampedScroll(canvas);
        int visible = VisibleCount(canvas);
        var result = new List<(string Label, CGRect Rect)>();
        for (int offset = 0; offset < visible; offset++)
        {
            int index = currentScroll + offset;
            if (index < 0 || index >= _candidates.Count)
            {
                break;
            }

            result.Add((_candidates[index].Title, RowRect(offset, canvas)));
        }

        if (MaxScroll(canvas) > 0 && _scrollbar is not null)
        {
            var bar = ScrollbarRect(canvas);
            const double btnH = ScrollbarButtonHeight;
            result.Add(("Scroll Up", new CGRect(bar.MinX, bar.MinY, bar.Width, btnH)));
            result.Add(("Scroll Down", new CGRect(bar.MinX, bar.MaxY - btnH, bar.Width, btnH)));
        }

        result.Add((_okCaption, OkRect(canvas)));
        return result;
    }

    public bool BeginOKPress(CGPoint point, CGSize canvas)
    {
        if (SelectedPersonRecordIndex is null || !ContainsRect(OkRect(canvas), point))
        {
            OkPressed = false;
            return false;
        }

        OkPressed = true;
        OkHovered = true;
        return true;
    }

    public bool EndOKPress()
    {
        bool wasPressed = OkPressed;
        OkPressed = false;
        return wasPressed;
    }

    public int? Click(CGPoint point, CGSize canvas)
    {
        _scroll = ClampedScroll(canvas);
        if (ContainsRect(OkRect(canvas), point))
        {
            return SelectedPersonRecordIndex;
        }

        int maxScr = MaxScroll(canvas);
        if (maxScr > 0 && _scrollbar is not null)
        {
            var bar = ScrollbarRect(canvas);
            if (ContainsRect(bar, point))
            {
                const double btnH = ScrollbarButtonHeight;
                int barVisible = VisibleCount(canvas);
                if (point.Y < bar.MinY + btnH)
                {
                    _scroll = Math.Max(0, _scroll - 1);
                }
                else if (point.Y >= bar.MaxY - btnH)
                {
                    _scroll = Math.Min(maxScr, _scroll + 1);
                }
                else
                {
                    var thumb = ScrollbarThumbRect(bar, _scroll, maxScr);
                    if (point.Y < thumb.MinY)
                    {
                        _scroll = Math.Max(0, _scroll - barVisible);
                    }
                    else if (point.Y >= thumb.MaxY)
                    {
                        _scroll = Math.Min(maxScr, _scroll + barVisible);
                    }
                }

                return null;
            }
        }

        int visible = VisibleCount(canvas);
        for (int offset = 0; offset < visible; offset++)
        {
            int index = _scroll + offset;
            if (index < 0 || index >= _candidates.Count)
            {
                break;
            }

            if (ContainsRect(RowRect(offset, canvas), point))
            {
                Selected = index;
                return _candidates[index].LivePersonId is null ? null : _candidates[index].PersonRecordIndex;
            }
        }

        return null;
    }

    public void Hover(CGPoint point, CGSize canvas)
    {
        _hoveredPoint = point;
        OkHovered = ContainsRect(OkRect(canvas), point);
        int currentScroll = ClampedScroll(canvas);
        int visible = VisibleCount(canvas);
        int? hitRow = null;
        for (int offset = 0; offset < visible; offset++)
        {
            int index = currentScroll + offset;
            if (index < 0 || index >= _candidates.Count)
            {
                break;
            }

            if (ContainsRect(RowRect(offset, canvas), point))
            {
                hitRow = index;
                break;
            }
        }

        Hovered = hitRow;
    }

    public void ClearHover()
    {
        _hoveredPoint = null;
        Hovered = null;
        OkHovered = false;
        if (!OkPressed)
        {
            OkPressed = false;
        }
    }

    public void Scroll(int delta) => Scroll(delta, CGSize.Zero);

    public void Scroll(int delta, CGSize canvas)
    {
        int maxScr = canvas.Width > 0.0
            ? MaxScroll(canvas)
            : Math.Max(0, _candidates.Count - MaxVisibleRows);
        _scroll = Math.Min(Math.Max(0, _scroll + delta), maxScr);
    }

    public void MoveSelection(int delta)
    {
        if (_candidates.Count == 0)
        {
            return;
        }

        Selected = Math.Min(Math.Max(0, Selected + delta), _candidates.Count - 1);
        if (Selected < _scroll)
        {
            _scroll = Selected;
        }
        else if (Selected >= _scroll + MaxVisibleRows)
        {
            _scroll = Selected - MaxVisibleRows + 1;
        }
    }

    private static bool ContainsRect(CGRect rect, CGPoint point) =>
        rect.Width > 0.0 && rect.Height > 0.0 &&
        point.X >= rect.MinX && point.X < rect.MaxX &&
        point.Y >= rect.MinY && point.Y < rect.MaxY;
}
