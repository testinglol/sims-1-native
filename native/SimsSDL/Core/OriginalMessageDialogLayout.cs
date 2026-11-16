namespace SimsSDL.Core;

public static class OriginalMessageDialogLayout
{
    public const double Border = 12;
    public const double Margin = 9;
    public const double Pad = Border + Margin;
    public const double ImageGap = 18;
    public const double TitleGap = 18;
    public const double ButtonHeight = 33;
    public const double MinButtonWidth = 100;
    public const double ButtonLabelPadding = 20;
    public const double ButtonReserveSpacing = 30;
    public const double InputHeight = 28;
    public const int ScrollbarReservedWidth = 20;
    public const double TargetAspect = 2.0;

    public readonly record struct WrappedLine(string Text, bool Side);

    public sealed class Input
    {
        public CGSize Viewport { get; set; }
        public string TitleText { get; set; }
        public CGSize TitleSize { get; set; }
        public int TitleLineHeight { get; set; }
        public string MessageText { get; set; }
        public SimsBitmapFont BodyFont { get; set; }
        public CGSize ImageFrame { get; set; }
        public IReadOnlyList<double> ButtonLabelWidths { get; set; }
        public bool HasTextInput { get; set; }
        public bool HasTutorialCloseBox { get; set; }
        public CGSize TutorialCloseBoxSize { get; set; }

        public Input(
            CGSize viewport,
            string titleText,
            CGSize titleSize,
            int titleLineHeight,
            string messageText,
            SimsBitmapFont bodyFont,
            CGSize? imageFrame = null,
            IReadOnlyList<double>? buttonLabelWidths = null,
            bool hasTextInput = false,
            bool hasTutorialCloseBox = false,
            CGSize? tutorialCloseBoxSize = null)
        {
            Viewport = viewport;
            TitleText = titleText;
            TitleSize = titleSize;
            TitleLineHeight = titleLineHeight;
            MessageText = messageText;
            BodyFont = bodyFont;
            ImageFrame = imageFrame ?? CGSize.Zero;
            ButtonLabelWidths = buttonLabelWidths ?? [];
            HasTextInput = hasTextInput;
            HasTutorialCloseBox = hasTutorialCloseBox;
            TutorialCloseBoxSize = tutorialCloseBoxSize ?? new CGSize(12, 11);
        }
    }

    public sealed record Result(
        CGRect Rect,
        double BodySpan,
        double MinBodySpan,
        double MaxBodySpan,
        bool HasImage,
        double ImageColumn,
        CGRect ImageRect,
        CGPoint TitleOrigin,
        double BodyTopInset,
        CGRect BodyRect,
        int SideLineLimit,
        bool NeedsScrollbar,
        int EffectiveFullWrap,
        int EffectiveSideWrap,
        int EffectiveSideLimit,
        IReadOnlyList<WrappedLine> Lines,
        CGRect InputRect,
        IReadOnlyList<CGRect> ButtonRects,
        CGRect TutorialCloseBoxRect)
    {
        public bool Equals(Result? other) =>
            other is not null &&
            Rect == other.Rect &&
            BodySpan == other.BodySpan &&
            MinBodySpan == other.MinBodySpan &&
            MaxBodySpan == other.MaxBodySpan &&
            HasImage == other.HasImage &&
            ImageColumn == other.ImageColumn &&
            ImageRect == other.ImageRect &&
            TitleOrigin == other.TitleOrigin &&
            BodyTopInset == other.BodyTopInset &&
            BodyRect == other.BodyRect &&
            SideLineLimit == other.SideLineLimit &&
            NeedsScrollbar == other.NeedsScrollbar &&
            EffectiveFullWrap == other.EffectiveFullWrap &&
            EffectiveSideWrap == other.EffectiveSideWrap &&
            EffectiveSideLimit == other.EffectiveSideLimit &&
            Lines.SequenceEqual(other.Lines) &&
            InputRect == other.InputRect &&
            ButtonRects.SequenceEqual(other.ButtonRects) &&
            TutorialCloseBoxRect == other.TutorialCloseBoxRect;

        public override int GetHashCode()
        {
            var hc = new HashCode();
            hc.Add(Rect);
            hc.Add(BodySpan);
            hc.Add(MinBodySpan);
            hc.Add(MaxBodySpan);
            hc.Add(HasImage);
            hc.Add(ImageColumn);
            hc.Add(ImageRect);
            hc.Add(TitleOrigin);
            hc.Add(BodyTopInset);
            hc.Add(BodyRect);
            hc.Add(SideLineLimit);
            hc.Add(NeedsScrollbar);
            hc.Add(EffectiveFullWrap);
            hc.Add(EffectiveSideWrap);
            hc.Add(EffectiveSideLimit);
            foreach (var line in Lines) hc.Add(line);
            hc.Add(InputRect);
            foreach (var btn in ButtonRects) hc.Add(btn);
            hc.Add(TutorialCloseBoxRect);
            return hc.ToHashCode();
        }
    }

    public static int NearestExclusionLines(int pixels, int lineHeight)
    {
        if (pixels <= 0 || lineHeight <= 0)
            return 0;
        int lines = pixels / lineHeight;
        if ((lines + 1) * lineHeight - pixels < pixels - lines * lineHeight)
            lines++;
        return lines;
    }

    /// <summary>Wraps text around an exclusion zone using a side width for initial lines and full width
    /// below. All lines remain in the side column if the entire text fits within the limit or if a single
    /// trailing line fits within the side width.</summary>
    public static IReadOnlyList<WrappedLine> FlowWrappedLines(
        string text,
        SimsBitmapFont font,
        int sideWidth,
        int fullWidth,
        int sideLineLimit)
    {
        int clampedFull = Math.Max(1, fullWidth);
        int clampedSide = Math.Max(1, sideWidth);
        if (sideLineLimit <= 0 || clampedSide >= clampedFull)
        {
            IReadOnlyList<string> fullOnly = font.WrappedLines(text, clampedFull);
            var fullResult = new WrappedLine[fullOnly.Count];
            bool sideFlag = sideLineLimit > 0;
            for (int i = 0; i < fullOnly.Count; i++)
                fullResult[i] = new WrappedLine(fullOnly[i], sideFlag);
            return fullResult;
        }

        if (sideLineLimit < int.MaxValue)
        {
            try
            {
                IReadOnlyList<string> sideOnly = font.WrappedLines(text, clampedSide);
                if (sideOnly.Count <= sideLineLimit)
                {
                    var sideResult = new WrappedLine[sideOnly.Count];
                    for (int i = 0; i < sideOnly.Count; i++)
                        sideResult[i] = new WrappedLine(sideOnly[i], true);
                    return sideResult;
                }
            }
            catch (ContentError)
            {
            }
        }

        if (text.Length > 8192)
            throw new ContentError("Invalid bitmap font paragraph");

        string normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = new List<WrappedLine>();
        foreach (string paragraph in normalized.Split('\n'))
        {
            string line = string.Empty;
            bool currentSide = lines.Count < sideLineLimit;
            int currentWidth = currentSide ? clampedSide : clampedFull;

            foreach (string word in paragraph.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = line.Length == 0 ? word : $"{line} {word}";
                if (font.TextWidth(candidate) <= currentWidth && candidate.Length <= 512)
                {
                    line = candidate;
                    continue;
                }

                if (line.Length > 0)
                {
                    lines.Add(new WrappedLine(line, currentSide));
                    line = string.Empty;
                    currentSide = lines.Count < sideLineLimit;
                    currentWidth = currentSide ? clampedSide : clampedFull;
                }

                foreach (Rune rune in word.EnumerateRunes())
                {
                    string ch = rune.ToString();
                    string charCandidate = line + ch;
                    if (line.Length > 0 && (font.TextWidth(charCandidate) > currentWidth || charCandidate.Length > 512))
                    {
                        lines.Add(new WrappedLine(line, currentSide));
                        line = string.Empty;
                        currentSide = lines.Count < sideLineLimit;
                        currentWidth = currentSide ? clampedSide : clampedFull;
                    }
                    line += ch;
                }
            }

            lines.Add(new WrappedLine(line, currentSide));
        }

        if (lines.Count > 512)
            throw new ContentError("Bitmap font paragraph has too many lines");

        if (sideLineLimit < int.MaxValue &&
            lines.Count == sideLineLimit + 1 &&
            font.TextWidth(lines[sideLineLimit].Text) <= clampedSide)
        {
            lines[sideLineLimit] = new WrappedLine(lines[sideLineLimit].Text, true);
        }

        return lines;
    }

    public static Result Solve(Input input)
    {
        double maxIconW = Math.Max(0, Math.Floor((input.Viewport.Width - 112) / 3.0));
        double imageWidth = input.ImageFrame.Width > 0 ? Math.Min(input.ImageFrame.Width, maxIconW) : 0;
        double imageHeight = input.ImageFrame.Width > 0 && imageWidth > 0
            ? input.ImageFrame.Height * imageWidth / input.ImageFrame.Width
            : 0;
        bool hasImage = imageWidth > 0 && imageHeight > 0;
        double imageColumn = hasImage ? imageWidth + ImageGap : 0;

        bool hasTitle = input.TitleText.Length > 0;
        double titleHeight = hasTitle ? Math.Max(1, input.TitleLineHeight) : 0;
        double rawBodyTop = hasTitle ? Pad + titleHeight + TitleGap : Pad;
        int bodyLineHeight = Math.Max(1, input.BodyFont.LineHeight);

        double iconOverlapBelowTitle = hasImage ? (Pad + imageHeight) - rawBodyTop : 0;
        int sideLineLimit;
        double bodyTopInset;
        if (!hasImage)
        {
            sideLineLimit = 0;
            bodyTopInset = rawBodyTop;
        }
        else if (iconOverlapBelowTitle < bodyLineHeight)
        {
            sideLineLimit = 0;
            bodyTopInset = Math.Max(rawBodyTop, Pad + imageHeight + Margin);
        }
        else
        {
            int exclusionPixels = (int)Math.Ceiling(iconOverlapBelowTitle + ImageGap);
            sideLineLimit = Math.Max(2, NearestExclusionLines(exclusionPixels, bodyLineHeight));
            bodyTopInset = rawBodyTop;
        }

        var buttonWidths = new double[input.ButtonLabelWidths.Count];
        double sumOfButtonWidths = 0;
        for (int i = 0; i < input.ButtonLabelWidths.Count; i++)
        {
            double w = Math.Max(MinButtonWidth, Math.Ceiling(input.ButtonLabelWidths[i]) + ButtonLabelPadding);
            buttonWidths[i] = w;
            sumOfButtonWidths += w;
        }
        bool hasButtons = buttonWidths.Length > 0;
        double bottomExtent;
        if (input.HasTextInput)
        {
            bottomExtent = hasButtons ? (12 + InputHeight + 18 + ButtonHeight + Pad) : (12 + InputHeight + Pad);
        }
        else
        {
            bottomExtent = hasButtons ? (18 + ButtonHeight + Pad) : (18 + Border);
        }

        double minButtonSpan = hasButtons
            ? sumOfButtonWidths + buttonWidths.Length * ButtonReserveSpacing
            : 0;
        double closeReserve = input.HasTutorialCloseBox ? Math.Ceiling(input.TutorialCloseBoxSize.Width) + 10 : 0;
        double minHeaderSpan;
        if (hasTitle && hasImage)
            minHeaderSpan = imageColumn + Math.Ceiling(input.TitleSize.Width) + closeReserve;
        else if (hasTitle)
            minHeaderSpan = Math.Ceiling(input.TitleSize.Width) + ImageGap + closeReserve * 2;
        else if (hasImage)
            minHeaderSpan = Math.Max(100, imageColumn + 60) + closeReserve;
        else
            minHeaderSpan = 100 + closeReserve;

        double maxBodySpan = Math.Max(100, Math.Floor(input.Viewport.Width - 32) - Pad * 2);
        double minBodySpan = Math.Min(maxBodySpan, Math.Ceiling(Math.Max(minHeaderSpan, minButtonSpan)));
        double maxSolverSpan = Math.Min(maxBodySpan, Math.Max(minBodySpan, 718));

        double maxWindowHeight = Math.Max(bodyTopInset + bodyLineHeight + bottomExtent, Math.Floor(input.Viewport.Height - 32));
        double maxBodyHeight = Math.Max(bodyLineHeight, maxWindowHeight - bodyTopInset - bottomExtent);
        int maxViewportLines = Math.Max(1, (int)Math.Floor(maxBodyHeight) / bodyLineHeight);
        int sideColumnPixels = (int)Math.Ceiling(imageColumn);

        (double Height, double Aspect) Evaluate(int span)
        {
            int fullW = Math.Max(40, span);
            int sideW = Math.Max(40, span - sideColumnPixels);
            int count;
            try
            {
                count = Math.Max(1, FlowWrappedLines(input.MessageText, input.BodyFont, sideW, fullW, sideLineLimit).Count);
            }
            catch (ContentError)
            {
                count = 1;
            }
            int visible = Math.Min(maxViewportLines, count);
            double textEnd = bodyTopInset + visible * bodyLineHeight;
            double contentEnd = Math.Max(hasImage ? Pad + imageHeight : 0, textEnd);
            double h = Math.Min(maxWindowHeight, contentEnd + bottomExtent);
            double w = span + Pad * 2;
            return (h, w / Math.Max(1, h));
        }

        int lowSpan = (int)minBodySpan;
        int highSpan = (int)maxSolverSpan;
        int chosenIntSpan;
        if (lowSpan >= highSpan)
        {
            chosenIntSpan = lowSpan;
        }
        else
        {
            var lowEval = Evaluate(lowSpan);
            var highEval = Evaluate(highSpan);
            if (lowEval.Aspect >= TargetAspect)
            {
                chosenIntSpan = lowSpan;
            }
            else if (highEval.Aspect <= TargetAspect)
            {
                chosenIntSpan = highSpan;
            }
            else
            {
                int lo = lowSpan;
                int hi = highSpan;
                while (hi - lo > 1)
                {
                    int mid = (lo + hi) / 2;
                    if (Evaluate(mid).Aspect < TargetAspect)
                        lo = mid;
                    else
                        hi = mid;
                }
                int scanStart = Math.Max(lowSpan, lo - 4);
                int scanEnd = Math.Min(highSpan, hi + 4);
                int bestSpan = lo;
                double bestErr = double.MaxValue;
                for (int candidate = scanStart; candidate <= scanEnd; candidate++)
                {
                    double err = Math.Abs(Evaluate(candidate).Aspect - TargetAspect);
                    if (err < bestErr - 1e-6)
                    {
                        bestErr = err;
                        bestSpan = candidate;
                    }
                }
                chosenIntSpan = bestSpan;
            }
        }

        double bodySpan = chosenIntSpan;
        double width = bodySpan + Pad * 2;
        int baseFullWrap = Math.Max(40, chosenIntSpan);
        int baseSideWrap = Math.Max(40, chosenIntSpan - sideColumnPixels);
        IReadOnlyList<WrappedLine> probeLines = FlowWrappedLines(
            input.MessageText,
            input.BodyFont,
            baseSideWrap,
            baseFullWrap,
            sideLineLimit);
        bool needsScrollbar = probeLines.Count > maxViewportLines;

        int effectiveFullWrap;
        int effectiveSideWrap;
        int effectiveSideLimit;
        IReadOnlyList<WrappedLine> lines;
        if (needsScrollbar)
        {
            if (sideLineLimit > 0)
            {
                effectiveSideWrap = Math.Max(40, baseSideWrap - ScrollbarReservedWidth);
                effectiveFullWrap = effectiveSideWrap;
                effectiveSideLimit = int.MaxValue;
            }
            else
            {
                effectiveFullWrap = Math.Max(40, baseFullWrap - ScrollbarReservedWidth);
                effectiveSideWrap = Math.Max(40, baseSideWrap - ScrollbarReservedWidth);
                effectiveSideLimit = 0;
            }
            lines = FlowWrappedLines(
                input.MessageText,
                input.BodyFont,
                effectiveSideWrap,
                effectiveFullWrap,
                effectiveSideLimit);
        }
        else
        {
            effectiveFullWrap = baseFullWrap;
            effectiveSideWrap = baseSideWrap;
            effectiveSideLimit = sideLineLimit;
            lines = probeLines;
        }

        int visibleLineCount = Math.Min(maxViewportLines, Math.Max(1, lines.Count));
        double textBottom = bodyTopInset + visibleLineCount * bodyLineHeight;
        double contentBottom = Math.Max(hasImage ? Pad + imageHeight : 0, textBottom);
        double height = Math.Min(maxWindowHeight, contentBottom + bottomExtent);
        var rect = new CGRect(
            Math.Floor((input.Viewport.Width - width) / 2.0),
            Math.Floor((input.Viewport.Height - height) / 2.0),
            width,
            height);

        CGPoint titleOrigin;
        if (hasTitle)
        {
            if (hasImage)
            {
                double rightColX = rect.MinX + Pad + imageColumn;
                double rightColW = Math.Max(0, bodySpan - imageColumn);
                double titleX = Math.Max(rightColX, rightColX + Math.Floor((rightColW - input.TitleSize.Width) / 2.0));
                double titleY = sideLineLimit == 0 && imageHeight > titleHeight
                    ? rect.MinY + Pad + Math.Floor((imageHeight - titleHeight) / 2.0)
                    : rect.MinY + Pad;
                titleOrigin = new CGPoint(titleX, titleY);
            }
            else
            {
                titleOrigin = new CGPoint(Math.Floor(rect.MidX - input.TitleSize.Width / 2.0), rect.MinY + Pad);
            }
        }
        else
        {
            titleOrigin = CGPoint.Zero;
        }

        double availBodyHeight = Math.Max(bodyLineHeight, height - bodyTopInset - bottomExtent);
        var bodyRect = new CGRect(
            sideLineLimit > 0 ? rect.MinX + Pad + imageColumn : rect.MinX + Pad,
            rect.MinY + bodyTopInset,
            sideLineLimit > 0 ? Math.Max(1, bodySpan - imageColumn) : Math.Max(1, bodySpan),
            availBodyHeight);

        double maxImgHeight = Math.Max(1, height - Pad - bottomExtent);
        double fit = hasImage ? Math.Min(1, maxImgHeight / Math.Max(1, imageHeight)) : 1;
        CGRect imageRect = hasImage
            ? new CGRect(rect.MinX + Pad, rect.MinY + Pad, imageWidth * fit, imageHeight * fit)
            : CGRect.Zero;

        double spacing = Math.Floor((bodySpan - sumOfButtonWidths) / (buttonWidths.Length + 1));
        if (spacing < 0) spacing = 9;
        double currentX = rect.MinX + Pad + spacing;
        double buttonY = rect.MaxY - Pad - ButtonHeight;
        var buttonRects = new CGRect[buttonWidths.Length];
        for (int i = 0; i < buttonWidths.Length; i++)
        {
            double buttonWidth = buttonWidths[i];
            buttonRects[i] = new CGRect(currentX, buttonY, buttonWidth, ButtonHeight);
            currentX += buttonWidth + spacing;
        }

        CGRect inputRect;
        if (input.HasTextInput)
        {
            double bottomOffset = hasButtons ? (Pad + ButtonHeight + 18 + InputHeight) : (Pad + InputHeight);
            inputRect = new CGRect(rect.MinX + Pad, rect.MaxY - bottomOffset, Math.Max(40, bodySpan), InputHeight);
        }
        else
        {
            inputRect = CGRect.Zero;
        }

        CGRect tutorialCloseBoxRect = input.HasTutorialCloseBox
            ? new CGRect(
                rect.MaxX - input.TutorialCloseBoxSize.Width - 5,
                rect.MinY + 5,
                input.TutorialCloseBoxSize.Width,
                input.TutorialCloseBoxSize.Height)
            : CGRect.Zero;

        return new Result(
            Rect: rect,
            BodySpan: bodySpan,
            MinBodySpan: minBodySpan,
            MaxBodySpan: maxBodySpan,
            HasImage: hasImage,
            ImageColumn: imageColumn,
            ImageRect: imageRect,
            TitleOrigin: titleOrigin,
            BodyTopInset: bodyTopInset,
            BodyRect: bodyRect,
            SideLineLimit: sideLineLimit,
            NeedsScrollbar: needsScrollbar,
            EffectiveFullWrap: effectiveFullWrap,
            EffectiveSideWrap: effectiveSideWrap,
            EffectiveSideLimit: effectiveSideLimit,
            Lines: lines,
            InputRect: inputRect,
            ButtonRects: buttonRects,
            TutorialCloseBoxRect: tutorialCloseBoxRect);
    }
}
