using System.Diagnostics;
using System.Numerics;
using SimsSDL.Core;

namespace SimsSDL.UI;

/// <summary>Classic top-left cheat console input bar matching Sims.original.exe (200x21 at (20, 20),
/// #405D5F fill, #FFFFFF text/caret, 5px left inset, 255-character cap, ; chaining, and ! repeat).</summary>
public sealed class OriginalCheatConsoleOverlay
{
    public static readonly CGRect LogicalRect = new(20, 20, 200, 21);
    public const int MaxLength = 255;
    public const int LeftTextPadding = 5;
    public const int RightTextPadding = 5;
    public const double CaretBlinkSeconds = 0.5;
    public static readonly (byte R, byte G, byte B, byte A) BackgroundRgba = (0x40, 0x5D, 0x5F, 0xFF);
    public static readonly (byte R, byte G, byte B) ForegroundRgb = (255, 255, 255);
    public static readonly (byte R, byte G, byte B, byte A) SelectionRgba = (0x24, 0x3E, 0x42, 0xFF);

    private readonly SimsBitmapFont _font;
    private readonly UIDevice _device;
    private readonly UITexture _backgroundTexture;
    private readonly UITexture _caretTexture;
    private readonly UITexture _selectionTexture;
    private readonly List<string> _commandHistory = [];
    private int _historyIndex = -1;
    private string _historyDraft = string.Empty;
    private string[]? _lastExecutedSegments;

    private string _text = string.Empty;
    private string? _cachedText;
    private OriginalGameplayOverlay.Image? _cachedTextImage;
    private double _openedAtSeconds;
    private bool _hasManualClock;

    public OriginalCheatConsoleOverlay(SimsBitmapFont font, UIDevice device)
    {
        _font = font ?? throw new ArgumentNullException(nameof(font));
        _device = device;
        _backgroundTexture = MakeSolidTexture(BackgroundRgba);
        _caretTexture = MakeSolidTexture((ForegroundRgb.R, ForegroundRgb.G, ForegroundRgb.B, 0xFF));
        _selectionTexture = MakeSolidTexture(SelectionRgba);
    }

    public bool Visible { get; private set; }
    public string Text => _text;
    public int CursorIndex { get; private set; }
    public int? SelectionAnchor { get; private set; }
    public double ScrollOffsetPx { get; private set; }
    public IReadOnlyList<string>? LastExecutedSegments => _lastExecutedSegments;
    public IReadOnlyList<string> CommandHistory => _commandHistory;

    private static double NowSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static UITexture MakeSolidTexture((byte R, byte G, byte B, byte A) rgba)
    {
        var pixels = SpritePixels.PremultipliedSRGB([rgba.R, rgba.G, rgba.B, rgba.A], removeMagenta: false);
        return new UITexture(1, 1, pixels);
    }

    public void Open(double? nowSeconds = null)
    {
        Visible = true;
        SetTextInternal(string.Empty);
        CursorIndex = 0;
        SelectionAnchor = null;
        ScrollOffsetPx = 0.0;
        _historyIndex = -1;
        _historyDraft = string.Empty;
        _hasManualClock = nowSeconds.HasValue;
        _openedAtSeconds = nowSeconds ?? NowSeconds();
    }

    public void Close()
    {
        Visible = false;
        SetTextInternal(string.Empty);
        CursorIndex = 0;
        SelectionAnchor = null;
        ScrollOffsetPx = 0.0;
        _historyIndex = -1;
        _historyDraft = string.Empty;
    }

    public void ClearTextCaches()
    {
        _cachedText = null;
        _cachedTextImage = null;
    }

    public void TextInput(string input)
    {
        if (!Visible || string.IsNullOrEmpty(input))
            return;

        var filtered = new System.Text.StringBuilder(input.Length);
        foreach (char c in input)
        {
            if (c >= ' ' && c != '\x7f')
                filtered.Append(c);
        }

        if (filtered.Length == 0)
            return;

        DeleteSelectionIfAny();
        int available = MaxLength - _text.Length;
        if (available <= 0)
            return;

        string insert = filtered.Length > available
            ? filtered.ToString(0, available)
            : filtered.ToString();

        SetTextInternal(_text.Insert(CursorIndex, insert));
        CursorIndex += insert.Length;
        _historyIndex = -1;
        EnsureCursorVisible();
        ResetBlinkPhase();
    }

    public void Backspace()
    {
        if (!Visible)
            return;

        if (DeleteSelectionIfAny())
        {
            EnsureCursorVisible();
            ResetBlinkPhase();
            return;
        }

        if (CursorIndex <= 0 || _text.Length == 0)
            return;

        int removeAt = CursorIndex - 1;
        SetTextInternal(_text.Remove(removeAt, 1));
        CursorIndex = removeAt;
        _historyIndex = -1;
        EnsureCursorVisible();
        ResetBlinkPhase();
    }

    public void Delete()
    {
        if (!Visible)
            return;

        if (DeleteSelectionIfAny())
        {
            EnsureCursorVisible();
            ResetBlinkPhase();
            return;
        }

        if (CursorIndex >= _text.Length)
            return;

        SetTextInternal(_text.Remove(CursorIndex, 1));
        _historyIndex = -1;
        EnsureCursorVisible();
        ResetBlinkPhase();
    }

    public void MoveLeft(bool word = false, bool select = false)
    {
        if (!Visible)
            return;

        int target = word ? PreviousWordBoundary(CursorIndex) : Math.Max(0, CursorIndex - 1);
        MoveCaret(target, select);
    }

    public void MoveRight(bool word = false, bool select = false)
    {
        if (!Visible)
            return;

        int target = word ? NextWordBoundary(CursorIndex) : Math.Min(_text.Length, CursorIndex + 1);
        MoveCaret(target, select);
    }

    public void MoveHome(bool select = false)
    {
        if (!Visible)
            return;

        MoveCaret(0, select);
    }

    public void MoveEnd(bool select = false)
    {
        if (!Visible)
            return;

        MoveCaret(_text.Length, select);
    }

    public void SelectAll()
    {
        if (!Visible || _text.Length == 0)
            return;

        SelectionAnchor = 0;
        CursorIndex = _text.Length;
        EnsureCursorVisible();
        ResetBlinkPhase();
    }

    public void HistoryUp()
    {
        if (!Visible || _commandHistory.Count == 0)
            return;

        if (_historyIndex < 0)
        {
            _historyDraft = _text;
            _historyIndex = _commandHistory.Count - 1;
        }
        else if (_historyIndex > 0)
        {
            _historyIndex--;
        }

        ApplyHistoryEntry(_commandHistory[_historyIndex]);
    }

    public void HistoryDown()
    {
        if (!Visible || _historyIndex < 0)
            return;

        if (_historyIndex < _commandHistory.Count - 1)
        {
            _historyIndex++;
            ApplyHistoryEntry(_commandHistory[_historyIndex]);
        }
        else
        {
            _historyIndex = -1;
            ApplyHistoryEntry(_historyDraft);
        }
    }

    public bool TryPrepareSegments(out List<string> segments, out string? errorTitle, out string? errorMessage)
    {
        segments = [];
        errorTitle = null;
        errorMessage = null;

        string[]? runningLast = _lastExecutedSegments;
        string[] rawParts = _text.Split(';', StringSplitOptions.TrimEntries);
        foreach (string rawPart in rawParts)
        {
            if (string.IsNullOrWhiteSpace(rawPart))
                continue;

            string[] tokens = rawPart.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;

            if (string.Equals(tokens[0], "!", StringComparison.Ordinal))
            {
                if (runningLast is null || runningLast.Length == 0)
                {
                    segments.Clear();
                    errorTitle = "Sorry";
                    errorMessage = "No previous cheat.";
                    return false;
                }

                if (tokens.Length == 1)
                {
                    segments.AddRange(runningLast);
                }
                else
                {
                    string? baseVerb = runningLast[0]
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault();
                    if (string.IsNullOrEmpty(baseVerb))
                    {
                        segments.Clear();
                        errorTitle = "Sorry";
                        errorMessage = "No previous cheat.";
                        return false;
                    }

                    string expanded = string.Concat(baseVerb, " ", string.Join(' ', tokens, 1, tokens.Length - 1));
                    segments.Add(expanded);
                    runningLast = [expanded];
                }
            }
            else
            {
                string normalized = string.Join(' ', tokens);
                segments.Add(normalized);
                runningLast = [normalized];
            }
        }

        return true;
    }

    public void RecordExecutedCommand(string normalizedCommand)
    {
        if (string.IsNullOrWhiteSpace(normalizedCommand))
            return;

        string trimmed = normalizedCommand.Trim();
        _lastExecutedSegments = [trimmed];
        if (_commandHistory.Count == 0 || !string.Equals(_commandHistory[^1], trimmed, StringComparison.Ordinal))
        {
            _commandHistory.Add(trimmed);
            if (_commandHistory.Count > 32)
                _commandHistory.RemoveAt(0);
        }
    }

    public void RecordExecutedBatch(IReadOnlyList<string> executedSegments)
    {
        if (executedSegments.Count == 0)
            return;

        _lastExecutedSegments = [.. executedSegments];
        string combined = string.Join("; ", executedSegments);
        if (_commandHistory.Count == 0 || !string.Equals(_commandHistory[^1], combined, StringComparison.Ordinal))
        {
            _commandHistory.Add(combined);
            if (_commandHistory.Count > 32)
                _commandHistory.RemoveAt(0);
        }
    }

    public IReadOnlyList<UIDraw> DrawItems(float scale, double? nowSeconds = null)
    {
        if (!Visible)
            return Array.Empty<UIDraw>();

        var draws = new List<UIDraw>(4);
        float boxX = (float)LogicalRect.X * scale;
        float boxY = (float)LogicalRect.Y * scale;
        float boxW = (float)LogicalRect.Width * scale;
        float boxH = (float)LogicalRect.Height * scale;
        draws.Add(new UIDraw(_backgroundTexture, new Vector4(boxX, boxY, boxW, boxH)));

        double innerWidth = LogicalRect.Width - LeftTextPadding - RightTextPadding;
        double textBaseX = LogicalRect.X + LeftTextPadding - ScrollOffsetPx;
        double clipLeft = LogicalRect.X + LeftTextPadding;
        double clipRight = LogicalRect.MaxX - RightTextPadding;

        if (SelectionAnchor is int anchor && anchor != CursorIndex && _text.Length > 0)
        {
            int selStart = Math.Min(anchor, CursorIndex);
            int selEnd = Math.Max(anchor, CursorIndex);
            double x0 = textBaseX + _font.TextWidth(_text[..selStart]);
            double x1 = textBaseX + _font.TextWidth(_text[..selEnd]);
            double clampedX0 = Math.Max(clipLeft, x0);
            double clampedX1 = Math.Min(clipRight, x1);
            if (clampedX1 > clampedX0)
            {
                double selY = LogicalRect.Y + 2.0;
                double selH = Math.Max(1.0, LogicalRect.Height - 4.0);
                draws.Add(new UIDraw(
                    _selectionTexture,
                    new Vector4((float)(clampedX0 * scale), (float)(selY * scale), (float)((clampedX1 - clampedX0) * scale), (float)(selH * scale))));
            }
        }

        if (_text.Length > 0 && EnsureTextImage() is { } textImage && textImage.Size.Width > 0 && textImage.Size.Height > 0)
        {
            double texW = textImage.Size.Width;
            double texH = textImage.Size.Height;
            double visX0 = Math.Max(clipLeft, textBaseX);
            double visX1 = Math.Min(clipRight, textBaseX + texW);
            if (visX1 > visX0)
            {
                double textY = LogicalRect.Y + Math.Floor((LogicalRect.Height - _font.LineHeight) / 2.0);
                float u0 = (float)((visX0 - textBaseX) / texW);
                float u1 = (float)((visX1 - textBaseX) / texW);
                draws.Add(new UIDraw(
                    textImage.Texture,
                    new Vector4((float)(visX0 * scale), (float)(textY * scale), (float)((visX1 - visX0) * scale), (float)(texH * scale)),
                    new Vector4(u0, 0f, u1 - u0, 1f)));
            }
        }

        double elapsed = Math.Max(0.0, (nowSeconds ?? NowSeconds()) - _openedAtSeconds);
        bool caretVisible = ((int)Math.Floor(elapsed / CaretBlinkSeconds) % 2) == 0;
        if (caretVisible)
        {
            double prefixWidth = CursorIndex > 0 ? _font.TextWidth(_text[..CursorIndex]) : 0.0;
            double caretX = Math.Clamp(textBaseX + prefixWidth, clipLeft, clipLeft + innerWidth);
            double caretHeight = Math.Min(17.0, Math.Max(10.0, _font.LineHeight));
            double caretY = LogicalRect.Y + Math.Floor((LogicalRect.Height - caretHeight) / 2.0);
            float caretWidthPx = MathF.Max(1f, scale);
            draws.Add(new UIDraw(
                _caretTexture,
                new Vector4((float)(caretX * scale), (float)(caretY * scale), caretWidthPx, (float)(caretHeight * scale))));
        }

        return draws;
    }

    private OriginalGameplayOverlay.Image? EnsureTextImage()
    {
        if (_text.Length == 0)
            return null;

        if (string.Equals(_cachedText, _text, StringComparison.Ordinal) && _cachedTextImage is { } cached)
            return cached;

        try
        {
            var raster = _font.RasterizeHiDpi(_text, ForegroundRgb, logicalMargin: 0);
            var image = OriginalGameplayOverlay.CreateImage(_device, raster);
            _cachedText = _text;
            _cachedTextImage = image;
            return image;
        }
        catch
        {
            return _cachedTextImage;
        }
    }

    private void SetTextInternal(string value)
    {
        if (string.Equals(_text, value, StringComparison.Ordinal))
            return;
        _text = value;
        _cachedText = null;
        _cachedTextImage = null;
    }

    private void ApplyHistoryEntry(string entry)
    {
        string clamped = entry.Length > MaxLength ? entry[..MaxLength] : entry;
        SetTextInternal(clamped);
        CursorIndex = clamped.Length;
        SelectionAnchor = null;
        EnsureCursorVisible();
        ResetBlinkPhase();
    }

    private bool DeleteSelectionIfAny()
    {
        if (SelectionAnchor is not int anchor || anchor == CursorIndex)
        {
            SelectionAnchor = null;
            return false;
        }

        int start = Math.Min(anchor, CursorIndex);
        int end = Math.Max(anchor, CursorIndex);
        SetTextInternal(_text.Remove(start, end - start));
        CursorIndex = start;
        SelectionAnchor = null;
        _historyIndex = -1;
        return true;
    }

    private void MoveCaret(int target, bool select)
    {
        if (select)
            SelectionAnchor ??= CursorIndex;
        else
            SelectionAnchor = null;

        CursorIndex = Math.Clamp(target, 0, _text.Length);
        EnsureCursorVisible();
        ResetBlinkPhase();
    }

    private void EnsureCursorVisible()
    {
        double maxTextWidth = LogicalRect.Width - LeftTextPadding - RightTextPadding;
        double caretPx = CursorIndex > 0 ? _font.TextWidth(_text[..CursorIndex]) : 0.0;
        if (caretPx < ScrollOffsetPx)
            ScrollOffsetPx = caretPx;
        else if (caretPx > ScrollOffsetPx + maxTextWidth)
            ScrollOffsetPx = caretPx - maxTextWidth;

        if (ScrollOffsetPx < 0.0)
            ScrollOffsetPx = 0.0;
    }

    private void ResetBlinkPhase()
    {
        if (!_hasManualClock)
            _openedAtSeconds = NowSeconds();
    }

    private int PreviousWordBoundary(int index)
    {
        int i = Math.Clamp(index, 0, _text.Length);
        while (i > 0 && char.IsWhiteSpace(_text[i - 1]))
            i--;
        while (i > 0 && !char.IsWhiteSpace(_text[i - 1]))
            i--;
        return i;
    }

    private int NextWordBoundary(int index)
    {
        int i = Math.Clamp(index, 0, _text.Length);
        while (i < _text.Length && !char.IsWhiteSpace(_text[i]))
            i++;
        while (i < _text.Length && char.IsWhiteSpace(_text[i]))
            i++;
        return i;
    }
}
