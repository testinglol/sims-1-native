using System.Numerics;
using SimsSDL;

namespace SimsSDL.UI;

/// <summary>
/// Sims.exe cWinHelp (FUN_0050ea9b): modal centered on screen with a STR# 166 topic list
/// (width = max title width + 25, height = 12 * (helpFont.LineHeight + 3)),
/// a 340px detail view to its right, and a centered OK WinBtn below.
/// </summary>
public sealed class OriginalHelpOverlay
{
    public struct DrawItem
    {
        public OriginalGameplayOverlay.Image Image { get; init; }
        public CGRect Rect { get; init; }
        public Vector4 UV { get; set; }

        public DrawItem(OriginalGameplayOverlay.Image image, CGRect rect)
        {
            Image = image;
            Rect = rect;
            UV = new Vector4(0f, 0f, 1f, 1f);
        }

        public DrawItem(OriginalGameplayOverlay.Image image, CGRect rect, Vector4 uv)
        {
            Image = image;
            Rect = rect;
            UV = uv;
        }
    }

    private const double DetailWidth = 340;
    private const int VisibleRows = 12; // cWinHelp sizes the list to twelve font lines (0050ea9b.c:67).
    private const int PageStep = 12; // cWinHelp also initializes the widget's page-step field to 12.
    private const double ScrollbarWidth = 17; // WinScrol.bmp is 204x20 across 12 cells (005f3a06.c:199).
    private const double ScrollbarButtonHeight = 20;
    private const double ScrollbarReservedWidth = 20; // cWinListBox reserves bmpHeight (20px) on the right (005f4e36.c:37, 005f51e0.c:32).

    // SMCtrlMgr color sets (004ff397.c:26-74): DAT_006f2a44 normal, DAT_006f2a48 hover, DAT_006f2a4c selected.
    private static readonly (byte R, byte G, byte B) NormalColor = (195, 205, 205);
    private static readonly (byte R, byte G, byte B) HoveredColor = (255, 255, 255);
    private static readonly (byte R, byte G, byte B) SelectedColor = (0, 255, 255);

    private readonly IReadOnlyList<(string Title, string Text)> _topics;
    private readonly List<OriginalGameplayOverlay.Image> _topicLabels;
    private readonly List<OriginalGameplayOverlay.Image> _topicSelectedLabels;
    private readonly List<OriginalGameplayOverlay.Image> _topicHoveredLabels;
    private readonly OriginalGameplayOverlay.Image _frame;
    private readonly double _frameEdge;
    private readonly OriginalGameplayOverlay.Image _button;
    private readonly double _buttonCapWidth;
    private readonly float _buttonSheetWidth;
    private readonly OriginalGameplayOverlay.Image _scrollbar;
    private readonly OriginalGameplayOverlay.Image _closeLabel;
    private readonly OriginalGameplayOverlay.Image _closeHoveredLabel;
    private readonly CGSize _closeSize;
    private readonly string _closeCaption;
    private readonly double _listWidth;
    private readonly UIDevice _device;

    private List<OriginalGameplayOverlay.Image> _detailLines = [];
    private int _scroll;
    private int _detailScroll;
    private CGPoint? _hoveredPoint;
    private CGSize _lastCanvas = CGSize.Zero;

    public SimsBitmapFont HelpFont { get; }
    public int Selected { get; private set; }
    public bool CloseHovered { get; private set; }

    private double RowHeight => HelpFont.LineHeight + 3;
    private double ListHeight => VisibleRows * RowHeight;
    private int VisibleDetailRows => Math.Max(1, (int)ListHeight / Math.Max(1, HelpFont.LineHeight));
    private bool HasTopicScrollbar => _topics.Count > VisibleRows;
    private bool HasDetailScrollbar => _detailLines.Count > VisibleDetailRows;
    private int MaxTopicScroll => Math.Max(0, _topics.Count - VisibleRows);
    private int MaxDetailScroll => Math.Max(0, _detailLines.Count - VisibleDetailRows);

    private CGSize PanelSize => new(
        20 + _listWidth + 20 + DetailWidth + 20,
        20 + ListHeight + _closeSize.Height * 2);

    public OriginalHelpOverlay(GameContent content, SimsBitmapFont font, UIDevice device, OriginalUIStrings strings)
    {
        // cWinHelp (0053a478.c:292 -> 0050e9b6.c:17 -> 0050ea9b.c) passes DAT_006f2aa4 = variablesans_12.ffn.
        SimsBitmapFont loadedHelpFont;
        try
        {
            loadedHelpFont = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_12.ffn"));
        }
        catch
        {
            loadedHelpFont = font;
        }

        HelpFont = loadedHelpFont;
        _device = device;
        _topics = strings.HelpTopics;
        _topicLabels = _topics
            .Select(t => OriginalGameplayOverlay.Text(t.Title, loadedHelpFont, device, NormalColor))
            .ToList();
        _topicSelectedLabels = _topics
            .Select(t => OriginalGameplayOverlay.Text(t.Title, loadedHelpFont, device, SelectedColor))
            .ToList();
        _topicHoveredLabels = _topics
            .Select(t => OriginalGameplayOverlay.Text(t.Title, loadedHelpFont, device, HoveredColor))
            .ToList();

        double maxLabelWidth = 0;
        foreach (var label in _topicLabels)
        {
            if (label.Size.Width > maxLabelWidth) maxLabelWidth = label.Size.Width;
        }
        _listWidth = Math.Max(200, maxLabelWidth + 25);

        // cWinHelp loads resource 0xBC0, PopupInfoTiles.bmp, and TSPaint draws its nine-slice frame (0050ea9b.c:19, 0050efc7.c).
        var popup = new SimsBitmap(content.Read("UIGraphics/CPanel/Backgrounds/PopupInfoTiles.bmp"));
        _frame = OriginalGameplayOverlay.CreateImage(device, popup.Width, popup.Height, popup.Rgba, magenta: false);
        _frameEdge = popup.Width / 3;

        // SMCtrlMgr::CreateButton (005e7b7d.c:33-35) creates a 3-slice cWinTileBtn (005f730f.c) of width >= 100 from WinBtn.bmp.
        var buttonBitmap = new SimsBitmap(content.Read("UIGraphics/Shared/Sys/WinBtn.bmp"));
        _button = OriginalGameplayOverlay.CreateImage(device, buttonBitmap.Width, buttonBitmap.Height, buttonBitmap.Rgba, magenta: true);
        _buttonSheetWidth = Math.Max(1, buttonBitmap.Width);
        _buttonCapWidth = (buttonBitmap.Width / 4 - 1) / 2;

        // cWinListBox / cWinScrollbar use kSMSystemScrollbar ("UIGraphics/Shared/Sys/WinScrol.bmp", 005f4e36.c:35, 005f6677.c).
        var scrollBitmap = new SimsBitmap(content.Read("UIGraphics/Shared/Sys/WinScrol.bmp"));
        _scrollbar = OriginalGameplayOverlay.CreateImage(device, scrollBitmap.Width, scrollBitmap.Height, scrollBitmap.Rgba, magenta: false);

        _closeCaption = strings.OkButton();
        _closeLabel = OriginalGameplayOverlay.Text(_closeCaption, loadedHelpFont, device, NormalColor);
        _closeHoveredLabel = OriginalGameplayOverlay.Text(_closeCaption, loadedHelpFont, device, HoveredColor);
        _closeSize = new CGSize(Math.Max(100, Math.Ceiling(_closeLabel.Size.Width) + 20), buttonBitmap.Height);

        Select(0);
    }

    private CGRect PanelRect(CGSize canvas)
    {
        var size = PanelSize;
        var origin = canvas.Width > 0 && canvas.Height > 0
            ? new CGPoint(Math.Floor((canvas.Width - size.Width) / 2), Math.Floor((canvas.Height - size.Height) / 2))
            : new CGPoint(50, 50);
        return new CGRect(origin.X, origin.Y, size.Width, size.Height);
    }

    private CGRect ListRect(CGSize canvas)
    {
        var panel = PanelRect(canvas);
        return new CGRect(panel.MinX + 20, panel.MinY + 20, _listWidth, ListHeight);
    }

    private double TopicRowWidth(CGRect list) =>
        HasTopicScrollbar ? Math.Max(1, list.Width - ScrollbarReservedWidth) : list.Width;

    private CGRect TopicScrollbarRect(CGSize canvas)
    {
        var list = ListRect(canvas);
        return new CGRect(list.MaxX - ScrollbarWidth, list.MinY, ScrollbarWidth, list.Height);
    }

    private CGRect DetailRect(CGSize canvas)
    {
        var list = ListRect(canvas);
        return new CGRect(list.MaxX + 20, list.MinY, DetailWidth, ListHeight);
    }

    private CGRect DetailScrollbarRect(CGSize canvas)
    {
        var detail = DetailRect(canvas);
        return new CGRect(detail.MaxX - ScrollbarWidth, detail.MinY, ScrollbarWidth, detail.Height);
    }

    private CGRect CloseRect(CGSize canvas)
    {
        var panel = PanelRect(canvas);
        return new CGRect(
            panel.MinX + Math.Floor((panel.Width - _closeSize.Width) / 2),
            panel.MinY + 20 + ListHeight + Math.Floor(_closeSize.Height / 2),
            _closeSize.Width,
            _closeSize.Height);
    }

    private static CGRect ScrollbarThumbRect(CGRect bar, int scroll, int maxScroll)
    {
        var btnH = ScrollbarButtonHeight;
        var minY = bar.MinY + btnH;
        var maxY = Math.Max(minY, bar.MaxY - btnH * 2);
        var fraction = maxScroll > 0 ? Math.Clamp(scroll, 0, maxScroll) / (double)maxScroll : 0.0;
        var thumbY = minY + Math.Round(fraction * (maxY - minY));
        return new CGRect(bar.MinX, thumbY, bar.Width, btnH);
    }

    private static bool ApplyScrollbarClick(CGPoint point, CGRect bar, ref int scroll, int maxScroll, int pageStep)
    {
        if (maxScroll <= 0 || !Contains(bar, point)) return false;
        var btnH = ScrollbarButtonHeight;
        if (point.Y < bar.MinY + btnH)
        {
            scroll = Math.Max(0, scroll - 1);
            return true;
        }
        if (point.Y >= bar.MaxY - btnH)
        {
            scroll = Math.Min(maxScroll, scroll + 1);
            return true;
        }
        var thumb = ScrollbarThumbRect(bar, scroll, maxScroll);
        if (point.Y < thumb.MinY)
        {
            scroll = Math.Max(0, scroll - pageStep);
        }
        else if (point.Y >= thumb.MaxY)
        {
            scroll = Math.Min(maxScroll, scroll + pageStep);
        }
        return true;
    }

    private void AppendVerticalScrollbar(CGRect bar, int scroll, int maxScroll, List<DrawItem> result)
    {
        const float cellW = 1.0f / 12.0f;
        var btnH = ScrollbarButtonHeight;
        var upRect = new CGRect(bar.MinX, bar.MinY, bar.Width, btnH);
        var downRect = new CGRect(bar.MinX, bar.MaxY - btnH, bar.Width, btnH);
        var trackRect = new CGRect(bar.MinX, bar.MinY + btnH, bar.Width, Math.Max(0, bar.Height - btnH * 2));
        var upActive = scroll > 0 && _hoveredPoint is { } hpUp && Contains(upRect, hpUp);
        var downActive = scroll < maxScroll && _hoveredPoint is { } hpDown && Contains(downRect, hpDown);

        // FUN_005f6677 (lines 70-113): cell 0/1 = Up normal/pressed, cell 3 = stretched Track, cell 4/5 = Down normal/pressed, cell 2 = Thumb.
        result.Add(new DrawItem(_scrollbar, upRect, new Vector4((upActive ? 1f : 0f) * cellW, 0f, cellW, 1f)));
        if (trackRect.Height > 0)
        {
            result.Add(new DrawItem(_scrollbar, trackRect, new Vector4(3f * cellW, 0f, cellW, 1f)));
        }
        result.Add(new DrawItem(_scrollbar, downRect, new Vector4((downActive ? 5f : 4f) * cellW, 0f, cellW, 1f)));
        if (maxScroll > 0 && trackRect.Height >= btnH)
        {
            var thumbRect = ScrollbarThumbRect(bar, scroll, maxScroll);
            result.Add(new DrawItem(_scrollbar, thumbRect, new Vector4(2f * cellW, 0f, cellW, 1f)));
        }
    }

    private void AppendTileButton(CGRect bounds, int frameIndex, List<DrawItem> result)
    {
        // FUN_005f730f (cWinTileBtn::OnPaint): 3-slice horizontal button from WinBtn.bmp (left cap, stretched center, right cap).
        var framePx = _buttonSheetWidth / 4.0f;
        var basePx = frameIndex * framePx;
        var capPx = (float)_buttonCapWidth;
        var capW = Math.Min(_buttonCapWidth, Math.Floor(bounds.Width / 2));
        var midW = Math.Max(0, bounds.Width - capW * 2);

        result.Add(new DrawItem(
            _button,
            new CGRect(bounds.MinX, bounds.MinY, capW, bounds.Height),
            new Vector4(basePx / _buttonSheetWidth, 0f, capPx / _buttonSheetWidth, 1f)));

        if (midW > 0)
        {
            result.Add(new DrawItem(
                _button,
                new CGRect(bounds.MinX + capW, bounds.MinY, midW, bounds.Height),
                new Vector4((basePx + capPx) / _buttonSheetWidth, 0f, Math.Max(1f, framePx - capPx * 2f) / _buttonSheetWidth, 1f)));
        }

        result.Add(new DrawItem(
            _button,
            new CGRect(bounds.MaxX - capW, bounds.MinY, capW, bounds.Height),
            new Vector4((basePx + framePx - capPx) / _buttonSheetWidth, 0f, capPx / _buttonSheetWidth, 1f)));
    }

    private void Select(int index)
    {
        if (_topics.Count == 0)
        {
            Selected = 0;
            _detailLines = [];
            _detailScroll = 0;
            return;
        }

        Selected = Math.Clamp(index, 0, _topics.Count - 1);
        _detailScroll = 0;

        // cWinText (005e9913.c) applies a (2, 2) text margin; reserve 20px on the right if detail lines overflow and require a scrollbar.
        var fullWrapWidth = Math.Max(40, (int)DetailWidth - 4);
        List<string> lines;
        try
        {
            lines = HelpFont.WrappedLines(_topics[Selected].Text, fullWrapWidth);
        }
        catch
        {
            lines = [];
        }

        if (lines.Count > VisibleDetailRows)
        {
            var scrollWrapWidth = Math.Max(40, (int)(DetailWidth - ScrollbarReservedWidth) - 4);
            try
            {
                lines = HelpFont.WrappedLines(_topics[Selected].Text, scrollWrapWidth);
            }
            catch
            {
                // Keep initial wrapped lines on fallback.
            }
        }

        _detailLines = new List<OriginalGameplayOverlay.Image>(lines.Count);
        foreach (var line in lines)
        {
            try
            {
                _detailLines.Add(OriginalGameplayOverlay.Text(line, HelpFont, _device, NormalColor));
            }
            catch
            {
                // Skip unrasterizable line.
            }
        }

        if (Selected < _scroll) _scroll = Selected;
        if (Selected >= _scroll + VisibleRows) _scroll = Selected - VisibleRows + 1;
    }

    public void MoveSelection(int offset) => Select(Selected + offset);
    public void SelectFirst() => Select(0);
    public void SelectLast() => Select(_topics.Count - 1);

    public void MovePage(int direction) =>
        Select(Selected + (direction < 0 ? -PageStep : PageStep));

    public void Scroll(int offset)
    {
        if (HasDetailScrollbar && _lastCanvas.Width > 0 &&
            _hoveredPoint is { } point && Contains(DetailRect(_lastCanvas), point))
        {
            _detailScroll = Math.Clamp(_detailScroll + offset, 0, MaxDetailScroll);
            return;
        }
        _scroll = Math.Clamp(_scroll + offset, 0, MaxTopicScroll);
    }

    public void Hover(CGPoint? point, CGSize canvas = default)
    {
        if (canvas.Width > 0 && canvas.Height > 0) _lastCanvas = canvas;
        _hoveredPoint = point;
        var effectiveCanvas = canvas.Width > 0 && canvas.Height > 0 ? canvas : _lastCanvas;
        CloseHovered = point is { } p && Contains(CloseRect(effectiveCanvas), p);
    }

    public List<(string Label, CGRect Rect)> Controls(CGSize canvas = default)
    {
        if (canvas.Width > 0 && canvas.Height > 0) _lastCanvas = canvas;
        var list = ListRect(canvas);
        var rowW = TopicRowWidth(list);
        var items = new List<(string Label, CGRect Rect)>();
        var end = Math.Min(_topics.Count, _scroll + VisibleRows);
        for (var index = _scroll; index < end; index++)
        {
            items.Add((
                _topics[index].Title,
                new CGRect(list.MinX, list.MinY + (index - _scroll) * RowHeight, rowW, RowHeight)));
        }

        if (HasTopicScrollbar)
        {
            var bar = TopicScrollbarRect(canvas);
            items.Add(("Scroll Topics Up", new CGRect(bar.MinX, bar.MinY, bar.Width, ScrollbarButtonHeight)));
            items.Add(("Scroll Topics Down", new CGRect(bar.MinX, bar.MaxY - ScrollbarButtonHeight, bar.Width, ScrollbarButtonHeight)));
        }
        if (HasDetailScrollbar)
        {
            var bar = DetailScrollbarRect(canvas);
            items.Add(("Scroll Help Up", new CGRect(bar.MinX, bar.MinY, bar.Width, ScrollbarButtonHeight)));
            items.Add(("Scroll Help Down", new CGRect(bar.MinX, bar.MaxY - ScrollbarButtonHeight, bar.Width, ScrollbarButtonHeight)));
        }
        items.Add((_closeCaption, CloseRect(canvas)));
        return items;
    }

    public List<DrawItem> DrawItems(CGSize canvas = default)
    {
        if (canvas.Width > 0 && canvas.Height > 0) _lastCanvas = canvas;
        var result = new List<DrawItem>();
        var panel = PanelRect(canvas);
        var list = ListRect(canvas);
        var rowW = TopicRowWidth(list);
        var detail = DetailRect(canvas);
        var close = CloseRect(canvas);

        double[] xs = [panel.MinX, panel.MinX + _frameEdge, panel.MaxX - _frameEdge, panel.MaxX];
        double[] ys = [panel.MinY, panel.MinY + _frameEdge, panel.MaxY - _frameEdge, panel.MaxY];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                result.Add(new DrawItem(
                    _frame,
                    new CGRect(xs[column], ys[row], xs[column + 1] - xs[column], ys[row + 1] - ys[row]),
                    new Vector4(column / 3.0f, row / 3.0f, 1.0f / 3.0f, 1.0f / 3.0f)));
            }
        }

        // cWinListBox::OnPaint (005f5039.c:31) fills listRect with PopupInfoTiles' center pixel (0050ea9b.c:78-79).
        result.Add(new DrawItem(_frame, list, new Vector4(1.0f / 3.0f, 1.0f / 3.0f, 1.0f / 3.0f, 1.0f / 3.0f)));

        var end = Math.Min(_topics.Count, _scroll + VisibleRows);
        for (var index = _scroll; index < end; index++)
        {
            var rowRect = new CGRect(list.MinX, list.MinY + (index - _scroll) * RowHeight, rowW, RowHeight);
            var isHovered = _hoveredPoint is { } hp && Contains(rowRect, hp);
            var label = index == Selected
                ? _topicSelectedLabels[index]
                : isHovered
                    ? _topicHoveredLabels[index]
                    : _topicLabels[index];

            // cWinListBox::AddItem (005f51e0.c:27) sets item button margins to (left: 4, top: 1), vertically centered.
            var labelWidth = Math.Min(label.Size.Width, Math.Max(0, rowRect.MaxX - (rowRect.MinX + 4)));
            if (labelWidth <= 0) continue;
            result.Add(new DrawItem(
                label,
                new CGRect(
                    rowRect.MinX + 4,
                    Math.Floor(rowRect.MidY - label.Size.Height / 2),
                    labelWidth,
                    label.Size.Height),
                new Vector4(0f, 0f, (float)(labelWidth / label.Size.Width), 1f)));
        }

        if (HasTopicScrollbar)
        {
            AppendVerticalScrollbar(TopicScrollbarRect(canvas), _scroll, MaxTopicScroll, result);
        }

        var maxDetailRight = HasDetailScrollbar ? detail.MaxX - ScrollbarReservedWidth : detail.MaxX;
        var detailEnd = Math.Min(_detailLines.Count, _detailScroll + VisibleDetailRows);
        for (var i = _detailScroll; i < detailEnd; i++)
        {
            var offset = i - _detailScroll;
            var line = _detailLines[i];
            var lineWidth = Math.Min(line.Size.Width, Math.Max(0, maxDetailRight - (detail.MinX + 2)));
            if (lineWidth <= 0) continue;
            var rowRect = new CGRect(detail.MinX + 2, detail.MinY + offset * HelpFont.LineHeight, lineWidth, line.Size.Height);
            if (rowRect.MaxY > detail.MaxY + 2) break;
            result.Add(new DrawItem(
                line,
                rowRect,
                new Vector4(0f, 0f, (float)(lineWidth / line.Size.Width), 1f)));
        }

        if (HasDetailScrollbar)
        {
            AppendVerticalScrollbar(DetailScrollbarRect(canvas), _detailScroll, MaxDetailScroll, result);
        }

        var isCloseHovered = _hoveredPoint is { } chp && Contains(close, chp);
        CloseHovered = isCloseHovered;
        AppendTileButton(close, isCloseHovered ? 2 : 0, result);
        var activeCloseLabel = isCloseHovered ? _closeHoveredLabel : _closeLabel;
        result.Add(new DrawItem(
            activeCloseLabel,
            new CGRect(
                Math.Floor(close.MidX - activeCloseLabel.Size.Width / 2),
                Math.Floor(close.MidY - activeCloseLabel.Size.Height / 2),
                activeCloseLabel.Size.Width,
                activeCloseLabel.Size.Height)));

        return result;
    }

    /// <summary>Returns true when the help window should close.</summary>
    public bool Click(CGPoint point, CGSize canvas = default)
    {
        if (canvas.Width > 0 && canvas.Height > 0) _lastCanvas = canvas;
        if (Contains(CloseRect(canvas), point)) return true;
        if (HasTopicScrollbar)
        {
            var bar = TopicScrollbarRect(canvas);
            if (ApplyScrollbarClick(point, bar, ref _scroll, MaxTopicScroll, PageStep))
            {
                return false;
            }
        }
        if (HasDetailScrollbar)
        {
            var bar = DetailScrollbarRect(canvas);
            if (ApplyScrollbarClick(point, bar, ref _detailScroll, MaxDetailScroll, VisibleDetailRows))
            {
                return false;
            }
        }

        var list = ListRect(canvas);
        var rowW = TopicRowWidth(list);
        var end = Math.Min(_topics.Count, _scroll + VisibleRows);
        for (var index = _scroll; index < end; index++)
        {
            var rowRect = new CGRect(list.MinX, list.MinY + (index - _scroll) * RowHeight, rowW, RowHeight);
            if (Contains(rowRect, point))
            {
                Select(index);
                break;
            }
        }
        return false;
    }

    private static bool Contains(CGRect rect, CGPoint point) =>
        point.X >= rect.X && point.X < rect.X + rect.Width &&
        point.Y >= rect.Y && point.Y < rect.Y + rect.Height;
}
