namespace SimsEngine.Formats;

/// <summary>Represents the six layout history pairs stored in the HOUS resource, tracking five historical
/// value pairs initialized from tuning fill values and one active current pair.</summary>
public sealed class HouseLayoutHistory
{
    private readonly int[] _values;

    private HouseLayoutHistory(int version, int[] values)
    {
        Version = version;
        _values = values;
    }

    public int Version { get; }

    /// <summary>Calculates the layout history score by summing the first values into A and second values
    /// into B, yielding <c>clamp((A - 2 * B) / max(A, 1), 0, 1) * 100</c>.</summary>
    public int Score
    {
        get
        {
            var firstSum = 0;
            var secondSum = 0;
            for (var index = 0; index < 6; index++)
            {
                firstSum = unchecked(firstSum + _values[index * 2]);
                secondSum = unchecked(secondSum + _values[index * 2 + 1]);
            }

            var denominator = Math.Max(firstSum, 1);
            var numerator = unchecked(denominator - 2 * secondSum);
            var ratio = Math.Clamp(numerator / (float)denominator, 0f, 1f);
            return (int)Math.Round(ratio * 100f, MidpointRounding.ToEven);
        }
    }

    /// <summary>Reads the layout history stream from HOUS resource 1, populating lots older than version 38
    /// with five default fill pairs and a zeroed active pair.</summary>
    public static HouseLayoutHistory? Read(IffFile lot, int fillValue)
    {
        ArgumentNullException.ThrowIfNull(lot);
        var chunk = lot.Find(ResourceType.Parse("HOUS"), 1);
        if (chunk is null) return null;

        var reader = new PersistReader(lot, chunk);
        if (reader.Version > 0x12) _ = reader.String();

        var values = new int[12];
        if (reader.Version >= 0x26)
        {
            for (var index = 0; index < values.Length; index++) values[index] = reader.Int32();
            if (reader.Remaining != 0) throw reader.Error($"{reader.Remaining} trailing bytes after layout history");
        }
        else
        {
            for (var index = 0; index < 10; index++) values[index] = fillValue;
        }

        return new HouseLayoutHistory(reader.Version, values);
    }
}
