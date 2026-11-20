namespace SimsSDL.UI;

public static class CycleIndicator
{
    public enum Hover
    {
        Weather,
        Season
    }

    public const int Width = 10;
    public const int Height = 10;
    public static readonly CGPoint GlyphOrigin = new(149, 88);
    public static CGRect GlyphRect => new(GlyphOrigin.X, GlyphOrigin.Y, Width, Height);
    public static readonly CGRect ReelRect = new(149, 88, Width, Height);
    public const int Cell = 12;
    public const int Symbol = 11;
    public const int SpinFrames = 72;

    public static int StripRow(double position, int count)
    {
        if (count <= 0)
            return 0;

        double countDouble = count;
        double turn = position % countDouble;
        double wrapped = turn < 0 ? turn + countDouble : turn;
        return (int)Math.Round((countDouble - wrapped) * Cell, MidpointRounding.AwayFromZero);
    }

    public static double Spin(double from, double to, double t)
    {
        double clampedT = Math.Min(1.0, Math.Max(0.0, t));
        double overshoot = to > from ? 0.15 : 0.0;
        if (clampedT < 0.8)
        {
            double u = 1.0 - clampedT / 0.8;
            return from + (to + overshoot - from) * (1.0 - u * u * u);
        }

        double u2 = (clampedT - 0.8) / 0.2;
        return to + overshoot * (1.0 - u2 * u2 * (3.0 - 2.0 * u2));
    }
}
