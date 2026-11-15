namespace SimsSDL.Core;

public static partial class OriginalGameplayLayout
{
    /// <summary>
    /// cWinPeople::Init (005186da.c:117-159, 237-249) allocates 8 45x45 PeopleTemplate.bmp buttons in a 4x2 grid
    /// at (229, 55) / (229, 100) (5px top/bottom margin inside the 100px-tall PanelBack.bmp), and
    /// cWinPeople::UpdateViewFromCPState (0051ceee.c:45-47) clamps visible household portraits to min(8, count).
    /// </summary>
    public const int MaxHouseholdPortraits = 8;

    /// <summary>Needs subpanel dark navy background RGB (PanelBack.bmp / 0050c694.c:19).</summary>
    public const uint MotiveArrowBackgroundRgb = 0x000052u;
    /// <summary>Initial 500ms flash peak for rising motive chevrons (0050c729.c:39, 0050c3b4.c:53).</summary>
    public const uint MotiveArrowRisingFlashRgb = 0x00ff00u;
    /// <summary>Steady green after the second 500ms ramp (0050c3b4.c:55).</summary>
    public const uint MotiveArrowRisingSteadyRgb = 0x00ca39u;
    /// <summary>Initial 500ms flash peak for falling motive chevrons (0050c729.c:39, 0050c3b4.c:46).</summary>
    public const uint MotiveArrowFallingFlashRgb = 0xff0000u;
    /// <summary>Steady red after the second 500ms ramp (0050c3b4.c:48).</summary>
    public const uint MotiveArrowFallingSteadyRgb = 0xd10000u;
    /// <summary>Duration of each cColorInterp ramp in cWinMotiveArrows and cWinMotive (0050c729.c:44, 0050c3b4.c:50).</summary>
    public const uint MotiveArrowFadeDurationMs = 500u;

    /// <summary>Individual chevron glyph bounds (0050c3b4.c:25-26, 0050c8d2.c:13-38).</summary>
    public static readonly CGSize MotiveArrowSize = new(3, 5);

    /// <summary>Combined 5-chevron strip bounds (4 px pitch across 5 glyphs of width 3: 4 * 4 + 3 = 19 px).</summary>
    public static readonly CGSize MotiveArrowStripSize = new(19, 5);

    /// <summary>
    /// cWinMotive::Init (00513322.c:55-61) passes left (DAT_006f38b8 - 20, DAT_006f38bc) = (0, 12) and right
    /// (DAT_006f38b8 + 61, DAT_006f38bc) = (81, 12) to cWinMotiveArrows::SetPositions (0050c387.c).
    /// cWinMotiveArrows::Draw (0050c3b4.c:22-38) steps right-pointing (&gt;) arrows from 81 by +4
    /// (81, 85, 89, 93, 97) and left-pointing (&lt;) arrows from 0 + 16 = 16 by -4 (16, 12, 8, 4, 0).
    /// </summary>
    public static CGPoint MotiveArrowOrigin(CGRect needRect, bool rising, int index)
    {
        int clamped = Math.Min(4, Math.Max(0, index));
        double offsetX = rising ? (81 + clamped * 4) : (16 - clamped * 4);
        return new CGPoint(needRect.MinX + offsetX, needRect.MinY + 12);
    }

    public static CGRect MotiveArrowRect(CGRect needRect, bool rising, int index) =>
        new(MotiveArrowOrigin(needRect, rising, index), MotiveArrowSize);

    public static CGRect MotiveArrowStripRect(CGRect needRect, bool rising) =>
        new(rising ? needRect.MinX + 81 : needRect.MinX, needRect.MinY + 12,
            MotiveArrowStripSize.Width, MotiveArrowStripSize.Height);

    /// <summary>
    /// Exact 3x5 chevron pixel masks from FUN_0050c8d2 (0050c8d2.c:13-38).
    /// rising == true (param_3 == 0) draws right-pointing &gt;; rising == false (param_3 != 0) draws left-pointing &lt;.
    /// </summary>
    public static bool[][] MotiveArrowMask(bool rising) =>
        rising
            ?
            [
                [true, false, false],
                [true, true, false],
                [true, true, true],
                [true, true, false],
                [true, false, false],
            ]
            :
            [
                [false, false, true],
                [false, true, true],
                [true, true, true],
                [false, true, true],
                [false, false, true],
            ];

    public static bool MotiveArrowMaskPixel(bool rising, int x, int y)
    {
        if (x is < 0 or >= 3 || y is < 0 or >= 5)
            return false;
        int distanceFromMid = Math.Abs(y - 2);
        return rising ? (x <= 2 - distanceFromMid) : (x >= distanceFromMid);
    }

    /// <summary>
    /// cRateOfChange (004cb630.c, 004cb715.c, 004cb761.c, 004cb7c0.c; x86 0x406540..0x406acd).
    /// Constructed per household Sim per motive with capacity = 10 (+0x20) and maxRate = 5 (+0x24).
    /// </summary>
    public struct MotiveRateTracker : IEquatable<MotiveRateTracker>
    {
        private readonly int _capacity;
        private readonly int _maxRate;
        private List<int>? _samples;
        private List<int>? _averages;

        public readonly int Capacity => _capacity > 0 ? _capacity : 10;
        public readonly int MaxRate => _maxRate > 0 ? _maxRate : 5;
        public readonly IReadOnlyList<int> Samples => _samples ?? (IReadOnlyList<int>)Array.Empty<int>();
        public readonly IReadOnlyList<int> Averages => _averages ?? (IReadOnlyList<int>)Array.Empty<int>();
        public int RunningSum { get; private set; }

        public MotiveRateTracker(int capacity = 10, int maxRate = 5)
        {
            _capacity = Math.Max(1, capacity);
            _maxRate = Math.Max(1, maxRate);
            _samples = new List<int>(_capacity);
            _averages = new List<int>(_capacity);
            RunningSum = 0;
        }

        /// <summary>
        /// Matches FUN_004cb715 (0x406a32..0x406a54): x &lt; 0 ? cvttss2si(x - 0.5f) : cvttss2si(x + 0.5f).
        /// </summary>
        public static int RoundHalfAwayFromZero(float value) =>
            value < 0 ? (int)(value - 0.5f) : (int)(value + 0.5f);

        /// <summary>FUN_004cb715: rounded mean of the current samples window.</summary>
        public readonly int CurrentAverage
        {
            get
            {
                if (_samples is null || _samples.Count == 0)
                    return 0;
                return RoundHalfAwayFromZero((float)RunningSum / _samples.Count);
            }
        }

        /// <summary>
        /// FUN_004cb761: appends a motive sample (-100...100), evicting the oldest sample &amp; average at capacity,
        /// then records the updated rounded running average into averages.
        /// </summary>
        public void Record(int sample)
        {
            int cap = Capacity;
            _samples ??= new List<int>(cap);
            _averages ??= new List<int>(cap);

            if (_samples.Count >= cap)
            {
                RunningSum -= _samples[0];
                _samples.RemoveAt(0);
                if (_averages.Count > 0)
                    _averages.RemoveAt(0);
            }
            _samples.Add(sample);
            RunningSum += sample;
            _averages.Add(CurrentAverage);
        }

        /// <summary>FUN_004cb7c0 (0x406a60..0x406aa3): averages.last - averages.first, clamped to -maxRate...maxRate.</summary>
        public readonly int Rate
        {
            get
            {
                if (_averages is null || _averages.Count == 0)
                    return 0;
                int delta = _averages[^1] - _averages[0];
                int max = MaxRate;
                return Math.Min(max, Math.Max(-max, delta));
            }
        }

        /// <summary>cRateOfChange::Clear (0x406ab0..0x406acd).</summary>
        public void Reset()
        {
            _samples?.Clear();
            _averages?.Clear();
            RunningSum = 0;
        }

        public readonly bool Equals(MotiveRateTracker other) =>
            Capacity == other.Capacity &&
            MaxRate == other.MaxRate &&
            RunningSum == other.RunningSum &&
            Samples.SequenceEqual(other.Samples) &&
            Averages.SequenceEqual(other.Averages);

        public override readonly bool Equals(object? obj) =>
            obj is MotiveRateTracker other && Equals(other);

        public override readonly int GetHashCode()
        {
            var hc = new HashCode();
            hc.Add(Capacity);
            hc.Add(MaxRate);
            hc.Add(RunningSum);
            foreach (int s in Samples) hc.Add(s);
            foreach (int a in Averages) hc.Add(a);
            return hc.ToHashCode();
        }

        public static bool operator ==(MotiveRateTracker left, MotiveRateTracker right) => left.Equals(right);
        public static bool operator !=(MotiveRateTracker left, MotiveRateTracker right) => !left.Equals(right);
    }

    /// <summary>
    /// cColorInterp (0050c54c.c, 0050c626.c, 004734bd.c, 00473496.c).
    /// </summary>
    public struct MotiveColorRamp : IEquatable<MotiveColorRamp>
    {
        public float StartR { get; private set; }
        public float StartG { get; private set; }
        public float StartB { get; private set; }
        public float DeltaR { get; private set; }
        public float DeltaG { get; private set; }
        public float DeltaB { get; private set; }
        public ulong StartTimeMs { get; private set; }
        public ulong EndTimeMs { get; private set; }
        public uint DurationMs { get; private set; }

        public MotiveColorRamp(uint rgb = 0)
        {
            StartR = (rgb >> 16) & 0xff;
            StartG = (rgb >> 8) & 0xff;
            StartB = rgb & 0xff;
        }

        public static uint PackRgb((byte X, byte Y, byte Z) color) =>
            ((uint)color.X << 16) | ((uint)color.Y << 8) | color.Z;

        public static (byte X, byte Y, byte Z) UnpackRgb(uint rgb) =>
            ((byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff));

        /// <summary>FUN_0050c54c + FUN_004734bd: configures linear per-channel RGB interpolation over durationMs.</summary>
        public void Set(uint from, uint to, uint durationMs, ulong nowMs)
        {
            float r0 = (from >> 16) & 0xff;
            float g0 = (from >> 8) & 0xff;
            float b0 = from & 0xff;
            float r1 = (to >> 16) & 0xff;
            float g1 = (to >> 8) & 0xff;
            float b1 = to & 0xff;
            StartR = r0;
            StartG = g0;
            StartB = b0;
            DeltaR = r1 - r0;
            DeltaG = g1 - g0;
            DeltaB = b1 - b0;
            StartTimeMs = nowMs;
            DurationMs = durationMs;
            EndTimeMs = unchecked(nowMs + durationMs);
        }

        /// <summary>FUN_00473496: normalized [0, 1] ramp progress at nowMs.</summary>
        public readonly float Progress(ulong nowMs)
        {
            if (DurationMs == 0 || nowMs >= EndTimeMs)
                return 1f;
            if (nowMs <= StartTimeMs)
                return 0f;
            return MathF.Min(1f, MathF.Max(0f, (float)(nowMs - StartTimeMs) / DurationMs));
        }

        /// <summary>FUN_0050c626 (0x49e986..0x49e9ea): evaluates cvttsd2si(double(start + delta * t) + 0.5) per channel.</summary>
        public readonly uint Color(ulong nowMs)
        {
            float t = Progress(nowMs);
            uint r = (uint)Math.Clamp((int)((double)(StartR + DeltaR * t) + 0.5), 0, 255);
            uint g = (uint)Math.Clamp((int)((double)(StartG + DeltaG * t) + 0.5), 0, 255);
            uint b = (uint)Math.Clamp((int)((double)(StartB + DeltaB * t) + 0.5), 0, 255);
            return (r << 16) | (g << 8) | b;
        }

        public readonly bool IsFinished(ulong nowMs) => nowMs >= EndTimeMs;

        public readonly bool Equals(MotiveColorRamp other) =>
            StartR == other.StartR && StartG == other.StartG && StartB == other.StartB &&
            DeltaR == other.DeltaR && DeltaG == other.DeltaG && DeltaB == other.DeltaB &&
            StartTimeMs == other.StartTimeMs && EndTimeMs == other.EndTimeMs && DurationMs == other.DurationMs;

        public override readonly bool Equals(object? obj) =>
            obj is MotiveColorRamp other && Equals(other);

        public override readonly int GetHashCode() =>
            HashCode.Combine(
                HashCode.Combine(StartR, StartG, StartB, DeltaR, DeltaG, DeltaB),
                StartTimeMs, EndTimeMs, DurationMs);

        public static bool operator ==(MotiveColorRamp left, MotiveColorRamp right) => left.Equals(right);
        public static bool operator !=(MotiveColorRamp left, MotiveColorRamp right) => !left.Equals(right);
    }

    /// <summary>
    /// cWinMotiveArrows (0050c27b.c, 0050c3b4.c, 0050c694.c, 0050c729.c).
    /// </summary>
    public struct MotiveArrowsState : IEquatable<MotiveArrowsState>
    {
        public readonly struct Evaluation : IEquatable<Evaluation>
        {
            public bool Visible { get; }
            public bool Rising { get; }
            public IReadOnlyList<uint> Colors { get; }
            public bool IsAnimating { get; }

            public Evaluation(bool visible, bool rising, IReadOnlyList<uint> colors, bool isAnimating)
            {
                Visible = visible;
                Rising = rising;
                Colors = colors;
                IsAnimating = isAnimating;
            }

            public bool Equals(Evaluation other) =>
                Visible == other.Visible &&
                Rising == other.Rising &&
                IsAnimating == other.IsAnimating &&
                Colors.SequenceEqual(other.Colors);

            public override bool Equals(object? obj) =>
                obj is Evaluation other && Equals(other);

            public override int GetHashCode()
            {
                var hc = new HashCode();
                hc.Add(Visible);
                hc.Add(Rising);
                hc.Add(IsAnimating);
                foreach (uint c in Colors) hc.Add(c);
                return hc.ToHashCode();
            }

            public static bool operator ==(Evaluation left, Evaluation right) => left.Equals(right);
            public static bool operator !=(Evaluation left, Evaluation right) => !left.Equals(right);
        }

        private MotiveColorRamp[]? _ramps;

        public IReadOnlyList<MotiveColorRamp> Ramps => EnsureRamps();
        public bool Visible { get; private set; }
        public bool Stopping { get; private set; }
        public int LastRate { get; private set; }
        public readonly bool IsRising => LastRate >= 0;

        public MotiveArrowsState()
        {
            _ramps = CreateDefaultRamps();
            Visible = false;
            Stopping = false;
            LastRate = 0;
        }

        private static MotiveColorRamp[] CreateDefaultRamps()
        {
            var ramps = new MotiveColorRamp[5];
            for (int i = 0; i < 5; i++)
                ramps[i] = new MotiveColorRamp(MotiveArrowBackgroundRgb);
            return ramps;
        }

        private MotiveColorRamp[] EnsureRamps() => _ramps ??= CreateDefaultRamps();

        /// <summary>FUN_0050c694: resets all 5 chevron slots to 0x000052 when switching Sims or clearing a tracker.</summary>
        public void Reset(bool hasTracker = true, ulong nowMs = 0)
        {
            MotiveColorRamp[] ramps = EnsureRamps();
            Visible = false;
            Stopping = !hasTracker;
            LastRate = 0;
            for (int index = 0; index < 5; index++)
            {
                ramps[index].Set(MotiveArrowBackgroundRgb, MotiveArrowBackgroundRgb, 0, nowMs);
            }
        }

        /// <summary>FUN_0050c729: updates the 5 chevron color ramps from the new -5...5 rate of change.</summary>
        public void Update(int rate, ulong nowMs, bool effectsEnabled = true)
        {
            MotiveColorRamp[] ramps = EnsureRamps();
            if (Stopping)
            {
                bool allFinished = true;
                for (int i = 0; i < 5; i++)
                {
                    if (!ramps[i].IsFinished(nowMs))
                    {
                        allFinished = false;
                        break;
                    }
                }
                if (allFinished)
                {
                    Visible = false;
                    LastRate = 0;
                    Stopping = false;
                }
            }

            int newRate = Math.Min(5, Math.Max(-5, rate));
            int oldRate = LastRate;
            if (newRate == 0)
            {
                Stopping = true;
            }
            else
            {
                Visible = true;
                Stopping = false;
                LastRate = newRate;
            }

            int oldAbs = Math.Abs(oldRate);
            int newAbs = Math.Abs(newRate);
            bool signChanged = newRate == 0 || oldRate == 0 || oldRate * newRate < 0;
            if (newAbs == oldAbs && !signChanged)
                return;

            if (!effectsEnabled)
            {
                if (newRate == 0)
                {
                    Reset(hasTracker: true, nowMs: nowMs);
                }
                else
                {
                    uint steady = LastRate >= 0 ? MotiveArrowRisingSteadyRgb : MotiveArrowFallingSteadyRgb;
                    for (int index = 0; index < 5; index++)
                    {
                        uint color = index < newAbs ? steady : MotiveArrowBackgroundRgb;
                        ramps[index].Set(color, color, 0, nowMs);
                    }
                }
                return;
            }

            uint flash = LastRate >= 0 ? MotiveArrowRisingFlashRgb : MotiveArrowFallingFlashRgb;
            const uint fadeMs = MotiveArrowFadeDurationMs;

            if (signChanged)
            {
                for (int index = 0; index < newAbs; index++)
                    ramps[index].Set(MotiveArrowBackgroundRgb, flash, fadeMs, nowMs);
                for (int index = newAbs; index < 5; index++)
                    ramps[index].Set(MotiveArrowBackgroundRgb, MotiveArrowBackgroundRgb, 0, nowMs);
            }
            else if (newAbs < oldAbs)
            {
                for (int index = newAbs; index < oldAbs; index++)
                {
                    uint current = ramps[index].Color(nowMs);
                    ramps[index].Set(current, MotiveArrowBackgroundRgb, fadeMs, nowMs);
                }
            }
            else
            {
                int startIndex = Math.Max(0, oldAbs - 1);
                for (int index = startIndex; index < newAbs; index++)
                {
                    uint current = ramps[index].Color(nowMs);
                    ramps[index].Set(current, flash, fadeMs, nowMs);
                }
            }
        }

        /// <summary>
        /// FUN_0050c3b4: evaluates the 5 chevron colors at nowMs, advancing 0x00ff00 -&gt; 0x00ca39 and
        /// 0xff0000 -&gt; 0xd10000 over 500ms upon reaching peak flash, and hiding once stopping finishes.
        /// </summary>
        public Evaluation Evaluate(ulong nowMs)
        {
            if (!Visible)
            {
                return new Evaluation(
                    visible: false,
                    rising: IsRising,
                    colors: [MotiveArrowBackgroundRgb, MotiveArrowBackgroundRgb, MotiveArrowBackgroundRgb, MotiveArrowBackgroundRgb, MotiveArrowBackgroundRgb],
                    isAnimating: false);
            }

            MotiveColorRamp[] ramps = EnsureRamps();
            bool rising = IsRising;
            const uint fadeMs = MotiveArrowFadeDurationMs;
            var colors = new uint[5];
            int finishedCount = 0;

            for (int index = 0; index < 5; index++)
            {
                uint current = ramps[index].Color(nowMs);
                if (current == MotiveArrowFallingFlashRgb)
                {
                    ramps[index].Set(MotiveArrowFallingFlashRgb, MotiveArrowFallingSteadyRgb, fadeMs, nowMs);
                }
                else if (current == MotiveArrowRisingFlashRgb)
                {
                    ramps[index].Set(MotiveArrowRisingFlashRgb, MotiveArrowRisingSteadyRgb, fadeMs, nowMs);
                }
                colors[index] = current;
                if (ramps[index].IsFinished(nowMs))
                    finishedCount++;
            }

            if (finishedCount == 5 && Stopping)
            {
                Visible = false;
                LastRate = 0;
                Stopping = false;
            }

            return new Evaluation(
                visible: Visible,
                rising: rising,
                colors: colors,
                isAnimating: Visible && finishedCount < 5);
        }

        public bool Equals(MotiveArrowsState other) =>
            Visible == other.Visible &&
            Stopping == other.Stopping &&
            LastRate == other.LastRate &&
            Ramps.SequenceEqual(other.Ramps);

        public override bool Equals(object? obj) =>
            obj is MotiveArrowsState other && Equals(other);

        public override int GetHashCode()
        {
            var hc = new HashCode();
            hc.Add(Visible);
            hc.Add(Stopping);
            hc.Add(LastRate);
            foreach (var r in Ramps) hc.Add(r);
            return hc.ToHashCode();
        }

        public static bool operator ==(MotiveArrowsState left, MotiveArrowsState right) => left.Equals(right);
        public static bool operator !=(MotiveArrowsState left, MotiveArrowsState right) => !left.Equals(right);
    }

    /// <summary>
    /// cWinMotive bar color state (00513534.c:68-97, 00513917.c, 00513a51.c).
    /// </summary>
    public struct MotiveBarState : IEquatable<MotiveBarState>
    {
        public readonly record struct Evaluation(uint GreenRgb, uint RedRgb, bool IsAnimating);

        public int Value { get; private set; }
        public MotiveColorRamp GreenRamp { get; private set; }
        public MotiveColorRamp RedRamp { get; private set; }

        public MotiveBarState(int value = 0)
        {
            int clamped = Math.Min(100, Math.Max(-100, value));
            Value = clamped;
            var steady = BarColors(clamped);
            GreenRamp = new MotiveColorRamp(MotiveColorRamp.PackRgb(steady.Green));
            RedRamp = new MotiveColorRamp(MotiveColorRamp.PackRgb(steady.Red));
        }

        /// <summary>FUN_00513a51: snaps both filled (green) and unfilled (red) cColorInterp channels to steady colors (0ms).</summary>
        public void Reset(int value, ulong nowMs = 0)
        {
            int clamped = Math.Min(100, Math.Max(-100, value));
            Value = clamped;
            var steady = BarColors(clamped);
            uint greenRgb = MotiveColorRamp.PackRgb(steady.Green);
            uint redRgb = MotiveColorRamp.PackRgb(steady.Red);
            var red = RedRamp;
            red.Set(redRgb, redRgb, 0, nowMs);
            RedRamp = red;
            var green = GreenRamp;
            green.Set(greenRgb, greenRgb, 0, nowMs);
            GreenRamp = green;
        }

        /// <summary>
        /// FUN_00513917 (00513917.c:13-48, 0x49e5b0..0x49e675): updates value when changed and sets both
        /// greenRamp and redRamp to the new steady barColors(value) with durationMs = 0.
        /// </summary>
        public void SetValue(int value, ulong nowMs = 0)
        {
            int clamped = Math.Min(100, Math.Max(-100, value));
            if (clamped == Value)
                return;
            Reset(clamped, nowMs);
        }

        /// <summary>
        /// FUN_00513534 (00513534.c:68-97): evaluates redRamp and greenRamp, including the post-peak
        /// 0xff0000 -&gt; steadyRed (0ms) and 0x00ff00 -&gt; steadyGreen (500ms) handlers.
        /// </summary>
        public Evaluation Evaluate(ulong nowMs)
        {
            var steady = BarColors(Value);
            uint steadyGreen = MotiveColorRamp.PackRgb(steady.Green);
            uint steadyRed = MotiveColorRamp.PackRgb(steady.Red);

            var red = RedRamp;
            uint currentRed = red.Color(nowMs);
            if (red.IsFinished(nowMs) && currentRed == MotiveArrowFallingFlashRgb)
            {
                red.Set(steadyRed, steadyRed, 0, nowMs);
                RedRamp = red;
            }

            var green = GreenRamp;
            uint currentGreen = green.Color(nowMs);
            if (green.IsFinished(nowMs) && currentGreen == MotiveArrowRisingFlashRgb)
            {
                green.Set(MotiveArrowRisingFlashRgb, steadyGreen, MotiveArrowFadeDurationMs, nowMs);
                GreenRamp = green;
            }

            bool animating = !RedRamp.IsFinished(nowMs) || !GreenRamp.IsFinished(nowMs);
            return new Evaluation(currentGreen, currentRed, animating);
        }

        public readonly bool Equals(MotiveBarState other) =>
            Value == other.Value && GreenRamp.Equals(other.GreenRamp) && RedRamp.Equals(other.RedRamp);

        public override readonly bool Equals(object? obj) =>
            obj is MotiveBarState other && Equals(other);

        public override readonly int GetHashCode() =>
            HashCode.Combine(Value, GreenRamp, RedRamp);

        public static bool operator ==(MotiveBarState left, MotiveBarState right) => left.Equals(right);
        public static bool operator !=(MotiveBarState left, MotiveBarState right) => !left.Equals(right);
    }

    // MARK: - Centered Widescreen Classic Subpanel Bay

    public readonly record struct HorizontalSpan(double MinX, double MaxX)
    {
        public double Width => Math.Max(0, MaxX - MinX);
    }

    public readonly record struct CenteredSubpanelBay(HorizontalSpan Bay, double? DividerLeftX, double? DividerRightX)
    {
        public const double ClassicWidth = 304;
        public bool IsFramed => DividerLeftX.HasValue && DividerRightX.HasValue;
    }

    public readonly record struct SkillGridSlot(CGRect Label, CGRect Bar)
    {
        public CGRect HitRect
        {
            get
            {
                double minX = Math.Min(Label.MinX, Bar.MinX);
                double minY = Math.Min(Label.MinY, Bar.MinY);
                double maxX = Math.Max(Label.MaxX, Bar.MaxX);
                double maxY = Math.Max(Label.MaxY, Bar.MaxY);
                return new CGRect(minX, minY, maxX - minX, maxY - minY);
            }
        }
    }

    public sealed record TraitGridLayout(
        double HeaderX,
        IReadOnlyList<CGRect> TraitRects,
        CGPoint ZodiacOrigin,
        bool UsesBottomAlignedZodiac)
    {
        public bool Equals(TraitGridLayout? other) =>
            other is not null &&
            HeaderX == other.HeaderX &&
            TraitRects.SequenceEqual(other.TraitRects) &&
            ZodiacOrigin == other.ZodiacOrigin &&
            UsesBottomAlignedZodiac == other.UsesBottomAlignedZodiac;

        public override int GetHashCode()
        {
            var hc = new HashCode();
            hc.Add(HeaderX);
            foreach (var r in TraitRects) hc.Add(r);
            hc.Add(ZodiacOrigin);
            hc.Add(UsesBottomAlignedZodiac);
            return hc.ToHashCode();
        }
    }

    public sealed record CareerSubpanelLayout(
        CGPoint HeaderOrigin,
        CGPoint JobIconOrigin,
        CGRect JobSummaryRect,
        CGRect SalaryRect,
        CGRect PerformanceRect,
        CGRect FriendsRect,
        CGRect ReportCardRect,
        double? SkillsHeaderX,
        IReadOnlyList<SkillGridSlot> Skills)
    {
        public bool Equals(CareerSubpanelLayout? other) =>
            other is not null &&
            HeaderOrigin == other.HeaderOrigin &&
            JobIconOrigin == other.JobIconOrigin &&
            JobSummaryRect == other.JobSummaryRect &&
            SalaryRect == other.SalaryRect &&
            PerformanceRect == other.PerformanceRect &&
            FriendsRect == other.FriendsRect &&
            ReportCardRect == other.ReportCardRect &&
            SkillsHeaderX == other.SkillsHeaderX &&
            Skills.SequenceEqual(other.Skills);

        public override int GetHashCode()
        {
            var hc = new HashCode();
            hc.Add(HeaderOrigin);
            hc.Add(JobIconOrigin);
            hc.Add(JobSummaryRect);
            hc.Add(SalaryRect);
            hc.Add(PerformanceRect);
            hc.Add(FriendsRect);
            hc.Add(ReportCardRect);
            hc.Add(SkillsHeaderX);
            foreach (var s in Skills) hc.Add(s);
            return hc.ToHashCode();
        }
    }

    public sealed record FameSubpanelLayout(
        CGPoint HeaderOrigin,
        CGRect StarBar,
        CGRect TitleRect,
        CGRect FriendsRect,
        double? SkillsHeaderX,
        IReadOnlyList<SkillGridSlot> Skills)
    {
        public bool Equals(FameSubpanelLayout? other) =>
            other is not null &&
            HeaderOrigin == other.HeaderOrigin &&
            StarBar == other.StarBar &&
            TitleRect == other.TitleRect &&
            FriendsRect == other.FriendsRect &&
            SkillsHeaderX == other.SkillsHeaderX &&
            Skills.SequenceEqual(other.Skills);

        public override int GetHashCode()
        {
            var hc = new HashCode();
            hc.Add(HeaderOrigin);
            hc.Add(StarBar);
            hc.Add(TitleRect);
            hc.Add(FriendsRect);
            hc.Add(SkillsHeaderX);
            foreach (var s in Skills) hc.Add(s);
            return hc.ToHashCode();
        }
    }

    public sealed record HouseSubpanelLayout(
        CGPoint HeaderOrigin,
        IReadOnlyList<CGRect> InfoRects,
        double? RatingsHeaderX,
        IReadOnlyList<CGRect> ScoreBars,
        IReadOnlyList<double> ScoreLabelRightXs,
        IReadOnlyList<double> ScoreLabelYs)
    {
        public bool Equals(HouseSubpanelLayout? other) =>
            other is not null &&
            HeaderOrigin == other.HeaderOrigin &&
            InfoRects.SequenceEqual(other.InfoRects) &&
            RatingsHeaderX == other.RatingsHeaderX &&
            ScoreBars.SequenceEqual(other.ScoreBars) &&
            ScoreLabelRightXs.SequenceEqual(other.ScoreLabelRightXs) &&
            ScoreLabelYs.SequenceEqual(other.ScoreLabelYs);

        public override int GetHashCode()
        {
            var hc = new HashCode();
            hc.Add(HeaderOrigin);
            foreach (var r in InfoRects) hc.Add(r);
            hc.Add(RatingsHeaderX);
            foreach (var b in ScoreBars) hc.Add(b);
            foreach (var x in ScoreLabelRightXs) hc.Add(x);
            foreach (var y in ScoreLabelYs) hc.Add(y);
            return hc.ToHashCode();
        }
    }

    /// <summary>
    /// Matches Sims.exe (1024x768) and Legacy Collection widescreen layout: fixed Live subpanels (Needs, Personality,
    /// Career, House, Fame, PetSkills, ReportCard) stay anchored at their classic left-aligned coordinates (x = 520..800)
    /// with no extra dividers while PanelBack.bmp extends seamlessly across the full bottom width (x = 220..canvasWidth).
    /// </summary>
    public static CenteredSubpanelBay CenteredBay(double canvasWidth)
    {
        _ = canvasWidth;
        return new CenteredSubpanelBay(new HorizontalSpan(520, 800), null, null);
    }

    public static double ClassicStripOffsetX(double canvasWidth)
    {
        _ = canvasWidth;
        return 0;
    }

    public static CGRect NeedRect(int index, double canvasWidth)
    {
        var framed = CenteredBay(canvasWidth);
        if (!framed.IsFramed)
            return Need(index);
        const double colGap = 12;
        const double totalW = 200 + colGap;
        double startX = framed.Bay.MinX + Math.Floor((framed.Bay.Width - totalW) / 2.0);
        return new CGRect(
            startX + (index % 2) * (100 + colGap),
            71 + (index / 2) * 19,
            100,
            20);
    }

    public static CGPoint NeedsHeaderOrigin(double canvasWidth, bool asian = false)
    {
        var framed = CenteredBay(canvasWidth);
        if (!framed.IsFramed)
            return InventoryTitle(asian);
        var first = NeedRect(0, canvasWidth);
        return new CGPoint(first.MinX + 5, asian ? 5 : 50);
    }

    public static TraitGridLayout PersonalitySubpanelLayout(double canvasWidth, int labelWidth, bool asian = false)
    {
        var framed = CenteredBay(canvasWidth);
        if (!framed.IsFramed)
        {
            var origin = InventoryTitle(asian);
            return new TraitGridLayout(
                origin.X,
                Enumerable.Range(0, 5).Select(i => Personality(i, labelWidth)).ToArray(),
                new CGPoint(525, 145),
                true);
        }

        const double contentW = 208;
        double leftX = framed.Bay.MinX + Math.Floor((framed.Bay.Width - contentW) / 2.0);
        double rightBarMaxX = leftX + contentW;
        double traitWidth = labelWidth + 49;
        return new TraitGridLayout(
            leftX,
            Enumerable.Range(0, 5)
                .Select(i => new CGRect(rightBarMaxX - traitWidth, 58 + i * 18, traitWidth, 14))
                .ToArray(),
            new CGPoint(leftX, 145),
            true);
    }

    public static CareerSubpanelLayout GetCareerSubpanelLayout(double canvasWidth, bool asian = false)
    {
        var framed = CenteredBay(canvasWidth);
        if (!framed.IsFramed)
        {
            return new CareerSubpanelLayout(
                asian ? new CGPoint(533, 58) : new CGPoint(555, 53),
                new CGPoint(523, 54),
                new CGRect(523, 54, 95, 41),
                new CGRect(523, 95, 95, 15),
                new CGRect(523, 115, 95, 15),
                new CGRect(523, 135, 95, 15),
                new CGRect(523, 134, 100, 16),
                null,
                Enumerable.Range(0, 6).Select(i => new SkillGridSlot(SkillLabel(i), SkillBar(i))).ToArray());
        }

        const double contentW = 216;
        double sx = framed.Bay.MinX + Math.Floor((framed.Bay.Width - contentW) / 2.0);
        const double summaryW = 104;
        return new CareerSubpanelLayout(
            asian ? new CGPoint(sx + 10, 58) : new CGPoint(sx + 32, 53),
            new CGPoint(sx, 54),
            new CGRect(sx, 54, summaryW, 41),
            new CGRect(sx, 95, summaryW, 15),
            new CGRect(sx, 115, summaryW, 15),
            new CGRect(sx, 135, summaryW, 15),
            new CGRect(sx, 134, summaryW, 16),
            null,
            Enumerable.Range(0, 6).Select(i => new SkillGridSlot(
                new CGRect(sx + 112, 56 + i * 15, 62, 16),
                new CGRect(sx + 176, 57 + i * 15, 40, 11))).ToArray());
    }

    public static FameSubpanelLayout GetFameSubpanelLayout(double canvasWidth, bool asian = false)
    {
        var framed = CenteredBay(canvasWidth);
        if (!framed.IsFramed)
        {
            return new FameSubpanelLayout(
                asian ? new CGPoint(533, 58) : new CGPoint(525, 50),
                FameStarBar,
                FameTitleLabel,
                FameFriendsButton,
                null,
                Enumerable.Range(0, 6).Select(i => new SkillGridSlot(FameSkillLabel(i), FameSkillBar(i))).ToArray());
        }

        const double contentW = 247; // classic 523..770
        double dx = framed.Bay.MinX + Math.Floor((framed.Bay.Width - contentW) / 2.0) - 523;
        return new FameSubpanelLayout(
            asian ? new CGPoint(533 + dx, 58) : new CGPoint(525 + dx, 50),
            FameStarBar.OffsetBy(dx, 0),
            FameTitleLabel.OffsetBy(dx, 0),
            FameFriendsButton.OffsetBy(dx, 0),
            null,
            Enumerable.Range(0, 6).Select(i => new SkillGridSlot(
                FameSkillLabel(i).OffsetBy(dx, 0),
                FameSkillBar(i).OffsetBy(dx, 0))).ToArray());
    }

    public static (CGPoint HeaderOrigin, IReadOnlyList<SkillGridSlot> Skills) PetSkillsSubpanelLayout(double canvasWidth)
    {
        var framed = CenteredBay(canvasWidth);
        if (!framed.IsFramed)
        {
            return (
                new CGPoint(533, 58),
                Enumerable.Range(0, 3).Select(i => new SkillGridSlot(PetSkillLabel(i), PetSkillBar(i))).ToArray());
        }

        const double contentW = 234; // classic 533..767
        double dx = framed.Bay.MinX + Math.Floor((framed.Bay.Width - contentW) / 2.0) - 533;
        return (
            new CGPoint(533 + dx, 58),
            Enumerable.Range(0, 3).Select(i => new SkillGridSlot(
                PetSkillLabel(i).OffsetBy(dx, 0),
                PetSkillBar(i).OffsetBy(dx, 0))).ToArray());
    }

    public static HouseSubpanelLayout GetHouseSubpanelLayout(double canvasWidth, bool asian = false)
    {
        var framed = CenteredBay(canvasWidth);
        if (!framed.IsFramed)
        {
            return new HouseSubpanelLayout(
                asian ? new CGPoint(533, 58) : new CGPoint(525, 50),
                Enumerable.Range(0, 4).Select(i => new CGRect(520, HouseInfoLabelY(i), 100, 15)).ToArray(),
                null,
                Enumerable.Range(0, 5).Select(HouseBar).ToArray(),
                [674, 674, 674, 674, 674],
                Enumerable.Range(0, 5).Select(HouseScoreLabelY).ToArray());
        }

        const double contentW = 208;
        double ix = framed.Bay.MinX + Math.Floor((framed.Bay.Width - contentW) / 2.0);
        double barX = ix + contentW - 40;
        return new HouseSubpanelLayout(
            asian ? new CGPoint(ix + 13, 58) : new CGPoint(ix + 5, 50),
            Enumerable.Range(0, 4).Select(i => new CGRect(ix, HouseInfoLabelY(i), 108, 15)).ToArray(),
            null,
            Enumerable.Range(0, 5).Select(i => new CGRect(barX, HouseBar(i).MinY, 40, 16)).ToArray(),
            [barX - 3, barX - 3, barX - 3, barX - 3, barX - 3],
            Enumerable.Range(0, 5).Select(HouseScoreLabelY).ToArray());
    }
}
