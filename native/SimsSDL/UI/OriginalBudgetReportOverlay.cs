using System.Numerics;
using SimsSDL;

namespace SimsSDL.UI;

/// <summary>
/// The live-lot budget dialog (<c>cWinBudgetDlg</c>, <c>005020aa.c</c>, <c>00502c7a.c</c>, <c>00502eb1.c</c>,
/// <c>005032cb.c</c>, <c>0050337c.c</c>), backed by <c>cpanel\Backgrounds\budgetback.bmp</c>,
/// <c>cpanel\Buttons\budgetok.bmp</c>, and <c>UIText.iff</c> <c>STR# 146</c> (<c>0x92</c>).
/// </summary>
public sealed class OriginalBudgetReportOverlay
{
    public sealed record ReportValues(
        IReadOnlyList<int> Today,
        IReadOnlyList<int> ThreeDay,
        int DaysSinceMoveIn,
        int NetWorth);

    private struct RowData
    {
        public SimsBitmapFont Font;
        public int Indent;
        public bool Centered;
        public string Col0;
        public string Col1;
        public string Col2;
        public int Raw1;
        public int Raw2;
    }

    private readonly OriginalGameplayOverlay.Image _background;
    private readonly OriginalGameplayOverlay.Image _okButton;
    private readonly bool _usesTileButtonFallback;
    private readonly SimsBitmapFont _font10;
    private readonly SimsBitmapFont _font12;
    private readonly SimsBitmapFont _font18;
    private readonly SimsBitmapFont _font20;
    private readonly UIDevice _device;
    private readonly OriginalUIStrings _strings;
    private readonly double _marginLeft;
    private readonly double _marginTop;
    private readonly double _marginRight;
    private readonly double _marginBottom;
    private readonly Dictionary<int, string> _rowTooltips = new();

    private CGPoint? _hoverPoint;
    private CGRect _lastPanelRect = new(136, 15, 528, 396);
    private CGRect _lastOKRect = new(291, 345, 217, 52);
    private List<CGRect> _lastRowRects = [];

    /// <summary>
    /// DAT_00dc73d8 (<c>DAT_006cc300</c> in base <c>005020aa.c</c>): indentation level per row (<c>x = 30 + indent * 12</c>).
    /// </summary>
    private static readonly int[] RowIndents =
    [
        0, 0, 0, 1, 1, 0, 0, 1, 1, 1,
        1, 2, 2, 1, 0, 0, 0, 0, 0, 0
    ];

    public SimsBitmapFont Font9 { get; }
    public SimsBitmapFont SmallFont => Font9;
    public bool HoveredOK { get; private set; }
    public bool PressedOK { get; private set; }
    public int? HoveredRow { get; private set; }

    public OriginalBudgetReportOverlay(GameContent content, SimsBitmapFont font, UIDevice device, OriginalUIStrings strings)
    {
        _device = device;
        _strings = strings;

        // 004ff397.c:106 DAT_006f2a98 = UIGraphics/Fonts/variablesans_09.ffn (leading zero in UIGraphics.far).
        Font9 = TryLoadFont(content, "UIGraphics/Fonts/variablesans_09.ffn") ?? font;
        _font10 = font;
        _font12 = TryLoadFont(content, "UIGraphics/Fonts/variablesans_12.ffn") ?? font;
        _font18 = TryLoadFont(content, "UIGraphics/Fonts/variablesans_18.ffn") ?? _font12;
        _font20 = TryLoadFont(content, "UIGraphics/Fonts/variablesans_20.ffn") ?? _font18;

        var backData = TryRead(content, "UIGraphics/cpanel/Backgrounds/budgetback.bmp")
            ?? TryRead(content, "UIGraphics/CPanel/Backgrounds/budgetback.bmp");
        if (backData is not null)
        {
            var bmp = new SimsBitmap(backData);
            _background = OriginalGameplayOverlay.CreateImage(device, bmp.Width, bmp.Height, bmp.Rgba, magenta: true);
        }
        else
        {
            var bmp = new SimsBitmap(content.Read("UIGraphics/CPanel/Backgrounds/PopupInfoTiles.bmp"));
            _background = OriginalGameplayOverlay.CreateImage(device, bmp.Width, bmp.Height, bmp.Rgba, magenta: true);
        }

        var okData = TryRead(content, "UIGraphics/cpanel/Buttons/budgetok.bmp")
            ?? TryRead(content, "UIGraphics/CPanel/Buttons/budgetok.bmp");
        if (okData is not null)
        {
            var bmp = new SimsBitmap(okData);
            _okButton = OriginalGameplayOverlay.CreateImage(device, bmp.Width, bmp.Height, bmp.Rgba, magenta: true);
            _usesTileButtonFallback = false;
        }
        else if (TryRead(content, "UIGraphics/Shared/Sys/WinBtn.bmp") is { } winBtnData)
        {
            var bmp = new SimsBitmap(winBtnData);
            _okButton = OriginalGameplayOverlay.CreateImage(device, bmp.Width, bmp.Height, bmp.Rgba, magenta: true);
            _usesTileButtonFallback = true;
        }
        else
        {
            _okButton = _background;
            _usesTileButtonFallback = true;
        }

        var topMarginPair = ParsePoint(strings.BudgetLabel(0, "(40;25)"), 40, 25);
        var bottomMarginPair = ParsePoint(strings.BudgetLabel(1, "(40;100)"), 40, 100);
        _marginLeft = topMarginPair.X;
        _marginTop = topMarginPair.Y;
        _marginRight = bottomMarginPair.X;
        _marginBottom = bottomMarginPair.Y;

        var strIdx = 6;
        for (var row = 2; row < 16; row++)
        {
            if (row != 5 && row != 14)
            {
                var tip = strings.BudgetLabel(strIdx + 1, string.Empty);
                if (!string.IsNullOrEmpty(tip)) _rowTooltips[row] = tip;
                strIdx += 2;
            }
        }

        var cashFlowTip = strings.BudgetLabel(29, "The sum of income and expenses");
        if (!string.IsNullOrEmpty(cashFlowTip)) _rowTooltips[15] = cashFlowTip;
        var totalTip = strings.BudgetLabel(31, "Current funds available");
        if (!string.IsNullOrEmpty(totalTip)) _rowTooltips[16] = totalTip;
        var daysTip = strings.BudgetLabel(33, "Sim days family has lived here");
        if (!string.IsNullOrEmpty(daysTip)) _rowTooltips[17] = daysTip;
        var netWorthTip = strings.BudgetLabel(35, "Value of house and belongings");
        if (!string.IsNullOrEmpty(netWorthTip)) _rowTooltips[18] = netWorthTip;
    }

    private static byte[]? TryRead(GameContent content, string path)
    {
        try
        {
            return content.Read(path);
        }
        catch
        {
            return null;
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

    private static (int X, int Y) ParsePoint(string text, int defaultX, int defaultY)
    {
        var trimmed = text.Trim('(', ')', ' ');
        var parts = trimmed.Split(';');
        if (parts.Length == 2 &&
            int.TryParse(parts[0].Trim(), out var x) &&
            int.TryParse(parts[1].Trim(), out var y))
        {
            return (x, y);
        }
        return (defaultX, defaultY);
    }

    /// <summary>DAT_00dc7388 (<c>PTR_DAT_006cc2b0</c> in <c>005020aa.c</c>): font per row (0..19).</summary>
    private SimsBitmapFont RowFont(int row) => row switch
    {
        0 => _font18,
        2 or 6 or 15 or 16 => _font12,
        11 or 12 => Font9,
        _ => _font10
    };

    private static string FormatSimoleons(int amount) =>
        amount < 0
            ? "-" + OriginalUIStrings.Simoleons(Math.Abs(amount))
            : OriginalUIStrings.Simoleons(amount);

    public void ClearHover()
    {
        _hoverPoint = null;
        HoveredOK = false;
        PressedOK = false;
        HoveredRow = null;
    }

    public void Hover(CGPoint point)
    {
        _hoverPoint = point;
        HoveredOK = Contains(_lastOKRect, point);
        if (!HoveredOK) PressedOK = false;
        HoveredRow = FindHoveredRow(_lastRowRects, point);
    }

    public bool BeginPress(CGPoint point)
    {
        PressedOK = Contains(_lastOKRect, point);
        return PressedOK;
    }

    public void EndPress() => PressedOK = false;

    /// <summary>Handles Return / Escape dismissal for <c>cWinBudgetDlg</c> (<c>005020aa.c</c>).</summary>
    public bool Key(string key)
    {
        if (key is not ("\r" or "\n" or "\u001b")) return false;
        ClearHover();
        return true;
    }

    public bool Contains(CGPoint point) => Contains(_lastPanelRect, point);

    public bool IsOKButton(CGPoint point)
    {
        PressedOK = false;
        return Contains(_lastOKRect, point);
    }

    public List<(string Label, CGRect Rect)> Controls() =>
        [(_strings.BudgetLabel(2, "OK"), _lastOKRect)];

    public List<OriginalGameplayOverlay.Part> DrawItems(
        ReportValues values,
        int budget,
        CGSize canvas,
        bool quickTips = true)
    {
        var parts = new List<OriginalGameplayOverlay.Part>();

        // Build row contents matching FUN_005020aa lines 161-349:
        var rows = new RowData[20];
        for (var r = 0; r < 20; r++)
        {
            rows[r] = new RowData
            {
                Font = RowFont(r),
                Indent = RowIndents[r],
                Centered = r is 0 or 16 or 17 or 18,
                Col0 = string.Empty,
                Col1 = string.Empty,
                Col2 = string.Empty
            };
        }

        rows[0].Col0 = _strings.BudgetLabel(3, "Budget");
        rows[1].Col1 = _strings.BudgetLabel(4, "3 Days");
        rows[1].Col2 = _strings.BudgetLabel(5, "Today");

        var fallbacks = new Dictionary<int, string>
        {
            [6] = "Income:",
            [8] = "Job",
            [10] = "Miscellaneous",
            [12] = "Expenses:",
            [14] = "Bills Paid:",
            [16] = "Food:",
            [18] = "Repair/Cleaning/Gardening:",
            [20] = "New Purchases",
            [22] = "Household Items",
            [24] = "Architecture/Landscaping",
            [26] = "Miscellaneous Expenses"
        };

        var strIdx = 6;
        for (var r = 2; r < 16; r++)
        {
            if (r != 5 && r != 14)
            {
                rows[r].Col0 = _strings.BudgetLabel(strIdx, fallbacks.GetValueOrDefault(strIdx, string.Empty));
                strIdx += 2;
            }
        }
        rows[15].Col0 = _strings.BudgetLabel(28, "Cash Flow");

        // Populate expense/income columns (FUN_005020aa lines 293-325):
        // Note: FUN_005031e9 / FUN_0050322b store -value so expenses become negative and incomes remain positive!
        void SetAmounts(int row, int index)
        {
            var t3 = -(index >= 0 && index < values.ThreeDay.Count ? values.ThreeDay[index] : 0);
            var td = -(index >= 0 && index < values.Today.Count ? values.Today[index] : 0);
            rows[row].Raw1 = t3;
            rows[row].Raw2 = td;
            rows[row].Col1 = FormatSimoleons(t3);
            rows[row].Col2 = FormatSimoleons(td);
        }

        SetAmounts(3, 1);  // Job
        SetAmounts(4, 2);  // Miscellaneous Income
        SetAmounts(7, 4);  // Bills Paid
        SetAmounts(8, 3);  // Food
        SetAmounts(9, 5);  // Repair/Cleaning/Gardening
        SetAmounts(11, 6); // Household Items
        SetAmounts(12, 7); // Architecture/Landscaping
        SetAmounts(13, 0); // Miscellaneous Expenses

        var sumThreeDay = values.ThreeDay.Take(8).Sum();
        var sumToday = values.Today.Take(8).Sum();
        rows[15].Raw1 = -sumThreeDay;
        rows[15].Raw2 = -sumToday;
        rows[15].Col1 = FormatSimoleons(-sumThreeDay);
        rows[15].Col2 = FormatSimoleons(-sumToday);

        var fundsTemplate = _strings.BudgetLabel(30, "Household Account Total: %s");
        rows[16].Col0 = fundsTemplate.Replace("%s", FormatSimoleons(budget), StringComparison.Ordinal);

        var daysTemplate = _strings.BudgetLabel(32, "Days Since Move-In: %s");
        var daysString = values.DaysSinceMoveIn.ToString();
        rows[17].Col0 = daysTemplate
            .Replace("%s", daysString, StringComparison.Ordinal)
            .Replace("%d", daysString, StringComparison.Ordinal);

        var worthTemplate = _strings.BudgetLabel(34, "Household Net Worth: %s");
        rows[18].Col0 = worthTemplate.Replace("%s", FormatSimoleons(values.NetWorth), StringComparison.Ordinal);

        // FUN_005032cb & FUN_005020aa lines 350-380: compute max column widths across non-centered rows.
        double maxCol0 = 0;
        double maxCol1 = 0;
        double maxCol2 = 0;
        foreach (var row in rows)
        {
            if (row.Centered) continue;
            var w0 = string.IsNullOrEmpty(row.Col0) ? 0.0 : row.Font.TextWidth(row.Col0) + row.Indent * 12;
            var w1 = string.IsNullOrEmpty(row.Col1) ? 0.0 : row.Font.TextWidth(row.Col1);
            var w2 = string.IsNullOrEmpty(row.Col2) ? 0.0 : row.Font.TextWidth(row.Col2);
            maxCol0 = Math.Max(maxCol0, w0);
            maxCol1 = Math.Max(maxCol1, w1);
            maxCol2 = Math.Max(maxCol2, w2);
        }

        // FUN_0050337c: rowWidth = maxCol0 + maxCol1 + maxCol2 + 0x78 (120)
        var rowWidth = Math.Max(400, maxCol0 + maxCol1 + maxCol2 + 120);
        // FUN_005031b0 & FUN_005020aa lines 138-149, 381-382: each row's height is its own font's lineHeight,
        // stacked sequentially from marginTop to row[19].MaxY, plus marginBottom.
        double totalRowsHeight = 0;
        foreach (var row in rows) totalRowsHeight += row.Font.LineHeight;

        var dialogWidth = _marginLeft + rowWidth + _marginRight;
        var dialogHeight = _marginTop + totalRowsHeight + _marginBottom;

        // FUN_0053a478 lines 191-206: center dialog in the lot viewport above the 150px control panel (or full canvas).
        var viewportHeight = Math.Max(dialogHeight, canvas.Height - 100);
        var panelX = Math.Floor((canvas.Width - dialogWidth) / 2);
        var panelY = Math.Max(8, Math.Floor((viewportHeight - dialogHeight) / 2));
        var panel = new CGRect(panelX, panelY, dialogWidth, dialogHeight);
        _lastPanelRect = panel;

        // FUN_00502c7a lines 29-35: 9-slice draw of budgetback.bmp (cell size = width / 3, height / 3).
        var sliceW = Math.Floor(_background.Size.Width / 3);
        var sliceH = Math.Floor(_background.Size.Height / 3);
        var edgeW = Math.Min(sliceW, Math.Floor(panel.Width / 3));
        var edgeH = Math.Min(sliceH, Math.Floor(panel.Height / 3));
        double[] xs = [panel.MinX, panel.MinX + edgeW, panel.MaxX - edgeW, panel.MaxX];
        double[] ys = [panel.MinY, panel.MinY + edgeH, panel.MaxY - edgeH, panel.MaxY];
        float[] uxs = [0f, 1.0f / 3.0f, 2.0f / 3.0f, 1.0f];
        float[] uys = [0f, 1.0f / 3.0f, 2.0f / 3.0f, 1.0f];
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                var w = xs[c + 1] - xs[c];
                var h = ys[r + 1] - ys[r];
                if (w <= 0 || h <= 0) continue;
                parts.Add(new OriginalGameplayOverlay.Part(
                    _background,
                    new CGRect(xs[c], ys[r], w, h),
                    new Vector4(uxs[c], uys[r], uxs[c + 1] - uxs[c], uys[r + 1] - uys[r])));
            }
        }

        // FUN_004ff397 lines 26-30, 69-74: global font default color DAT_006f2a44 = RGB(195, 205, 205),
        // pressed DAT_006f2a4c = RGB(0, 255, 255), hover DAT_006f2a48 = RGB(255, 255, 255).
        (byte R, byte G, byte B) textColor = (195, 205, 205);
        (byte R, byte G, byte B) pressedTextColor = (0, 255, 255);
        (byte R, byte G, byte B) hoverTextColor = (255, 255, 255);
        // DAT_00dfa8e4 (`DAT_006f2a5c` in base `00502eb1.c`): negative currency color RGB(209, 0, 0).
        (byte R, byte G, byte B) negativeRed = (209, 0, 0);

        var rowRects = new List<CGRect>(20);
        var currentY = panel.MinY + _marginTop;
        foreach (var row in rows)
        {
            double h = row.Font.LineHeight;
            var rowRect = new CGRect(panel.MinX + _marginLeft, currentY, rowWidth, h);
            rowRects.Add(rowRect);
            var y = currentY;
            currentY += h;

            if (row.Centered)
            {
                if (string.IsNullOrEmpty(row.Col0)) continue;
                var img = OriginalGameplayOverlay.Text(row.Col0, row.Font, _device, textColor);
                var x = Math.Floor(rowRect.MinX + (rowRect.Width - img.Size.Width) / 2);
                parts.Add(new OriginalGameplayOverlay.Part(img, new CGRect(x, y, img.Size.Width, img.Size.Height)));
            }
            else
            {
                // FUN_00502eb1 lines 34-36:
                // col0X = rowRect.minX + 30 + indent * 12
                // col1X = rowRect.minX + maxCol0 + 60
                // col2X = rowRect.minX + maxCol0 + maxCol1 + 90
                if (!string.IsNullOrEmpty(row.Col0))
                {
                    var img = OriginalGameplayOverlay.Text(row.Col0, row.Font, _device, textColor);
                    var x = rowRect.MinX + 30 + row.Indent * 12;
                    parts.Add(new OriginalGameplayOverlay.Part(img, new CGRect(x, y, img.Size.Width, img.Size.Height)));
                }
                if (!string.IsNullOrEmpty(row.Col1))
                {
                    var color = row.Raw1 < 0 ? negativeRed : textColor;
                    var img = OriginalGameplayOverlay.Text(row.Col1, row.Font, _device, color);
                    var x = rowRect.MinX + maxCol0 + 60;
                    parts.Add(new OriginalGameplayOverlay.Part(img, new CGRect(x, y, img.Size.Width, img.Size.Height)));
                }
                if (!string.IsNullOrEmpty(row.Col2))
                {
                    var color = row.Raw2 < 0 ? negativeRed : textColor;
                    var img = OriginalGameplayOverlay.Text(row.Col2, row.Font, _device, color);
                    var x = rowRect.MinX + maxCol0 + maxCol1 + 90;
                    parts.Add(new OriginalGameplayOverlay.Part(img, new CGRect(x, y, img.Size.Width, img.Size.Height)));
                }
            }
        }
        _lastRowRects = rowRects;

        // FUN_005020aa lines 383-387: OK button (budgetok.bmp, 4-frame horizontal sheet, 217x52 per frame;
        // falls back to 3-sliced WinBtn.bmp >=100x33 if budgetok.bmp is absent).
        var okCaption = _strings.BudgetLabel(2, "OK");
        var captionFont = _usesTileButtonFallback ? _font12 : _font20;
        var btnW = _usesTileButtonFallback
            ? Math.Max(100, captionFont.TextWidth(okCaption) + 24)
            : Math.Floor(_okButton.Size.Width / 4);
        var btnH = _okButton.Size.Height;
        var btnX = Math.Floor(panel.MinX + (panel.Width - btnW) / 2);
        var btnY = Math.Floor(panel.MaxY - _marginBottom + (_marginBottom - btnH) / 2);
        var okRect = new CGRect(btnX, btnY, btnW, btnH);
        _lastOKRect = okRect;

        if (_hoverPoint is { } hoverPoint)
        {
            HoveredOK = Contains(okRect, hoverPoint);
            if (!HoveredOK) PressedOK = false;
            HoveredRow = FindHoveredRow(rowRects, hoverPoint);
        }

        var frameIndex = PressedOK ? 1.0f : (HoveredOK ? 2.0f : 0.0f);
        var uvBase = frameIndex * 0.25f;
        if (_usesTileButtonFallback)
        {
            var rawFrameW = Math.Max(4, Math.Floor(_okButton.Size.Width / 4));
            var cap = Math.Min(Math.Floor(okRect.Width / 2), Math.Max(1, Math.Floor((rawFrameW - 1) / 2)));
            var uCap = (float)(cap / _okButton.Size.Width);
            var uMid = Math.Max(0f, 0.25f - uCap * 2f);
            var midW = Math.Max(0, okRect.Width - cap * 2);
            parts.Add(new OriginalGameplayOverlay.Part(
                _okButton,
                new CGRect(okRect.MinX, okRect.MinY, cap, okRect.Height),
                new Vector4(uvBase, 0f, uCap, 1f)));
            if (midW > 0)
            {
                parts.Add(new OriginalGameplayOverlay.Part(
                    _okButton,
                    new CGRect(okRect.MinX + cap, okRect.MinY, midW, okRect.Height),
                    new Vector4(uvBase + uCap, 0f, uMid, 1f)));
            }
            parts.Add(new OriginalGameplayOverlay.Part(
                _okButton,
                new CGRect(okRect.MaxX - cap, okRect.MinY, cap, okRect.Height),
                new Vector4(uvBase + 0.25f - uCap, 0f, uCap, 1f)));
        }
        else
        {
            parts.Add(new OriginalGameplayOverlay.Part(
                _okButton,
                okRect,
                new Vector4(uvBase, 0f, 0.25f, 1f)));
        }

        // FUN_005020aa lines 31-32, 54-63, 150-159 & FUN_005e4104:
        // state 0 (normal) = (0, 0), state 1 (pressed) = (+2, +2), state 2 (hover) = (0, 0), state 3 (disabled) = (0, 0).
        if (!string.IsNullOrEmpty(okCaption))
        {
            var captionColor = PressedOK ? pressedTextColor : (HoveredOK ? hoverTextColor : textColor);
            var okText = OriginalGameplayOverlay.Text(okCaption, captionFont, _device, captionColor);
            var shift = PressedOK ? 2.0 : 0.0;
            var tx = Math.Floor(okRect.MinX + (okRect.Width - okText.Size.Width) / 2) + shift;
            var ty = Math.Floor(okRect.MinY + (okRect.Height - okText.Size.Height) / 2) + shift;
            parts.Add(new OriginalGameplayOverlay.Part(okText, new CGRect(tx, ty, okText.Size.Width, okText.Size.Height)));
        }

        // Row hover tooltip (FUN_005020aa lines 201-210, 216-224, 226-277)
        if (quickTips && HoveredRow is { } hoveredRow &&
            hoveredRow >= 0 && hoveredRow < rowRects.Count &&
            _rowTooltips.TryGetValue(hoveredRow, out var tip))
        {
            try
            {
                var anchor = rowRects[hoveredRow];
                var raster = PlacementTooltip.Rasterize(tip, Font9);
                var tipImage = OriginalGameplayOverlay.CreateImage(_device, raster.Width, raster.Height, raster.Rgba);
                var tipRect = PlacementTooltip.Rect(tipImage.Size, new CGPoint(anchor.MidX, anchor.MinY - 1), canvas);
                parts.Add(new OriginalGameplayOverlay.Part(tipImage, tipRect));
            }
            catch
            {
                // Ignore transient tooltip rasterization errors during hover.
            }
        }

        return parts;
    }

    private static int? FindHoveredRow(IReadOnlyList<CGRect> rowRects, CGPoint point)
    {
        for (var i = 0; i < rowRects.Count; i++)
        {
            if (Contains(rowRects[i], point)) return i;
        }
        return null;
    }

    private static bool Contains(CGRect rect, CGPoint point) =>
        point.X >= rect.X && point.X < rect.X + rect.Width &&
        point.Y >= rect.Y && point.Y < rect.Y + rect.Height;
}
