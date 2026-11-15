namespace SimsEngine.Formats;

/// <summary>The six layout-history pairs persisted in the HOUS #1 resource. Sims.exe's FUN_0041655e stores an
/// auxiliary string from version 19, then five history pairs and the current pair from version 38. The first five
/// pairs are initialized by FUN_004165e9 with FCNS 2 "fill value for layout history"; the current pair starts at zero.</summary>
public sealed class HouseLayoutHistory
{
    private readonly int[] _values;

    private HouseLayoutHistory(int version, int[] values)
    {
        Version = version;
        _values = values;
    }

    public int Version { get; }

    /// <summary>FUN_00416618 sums the first item of each pair into A and the second into B, then evaluates
    /// clamp((A - 2*B) / max(A, 1), 0, 1) * 100 via x87 and __ftol.</summary>
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

    /// <summary>Reads the original House history stream (HOUS resource id 1). A lot predating version 38 has no
    /// persisted pairs, so FUN_0041655e restores the same five configured fill pairs and zero current pair.</summary>
    public static HouseLayoutHistory? Read(IffFile lot, int fillValue)
    {
        ArgumentNullException.ThrowIfNull(lot);
        var chunk = lot.Find(ResourceType.Parse("HOUS"), 1);
        if (chunk is null) return null;

        var reader = new PersistReader(lot, chunk);
        if (reader.Version > 0x12) _ = reader.String(); // FUN_00464c84 serializes the state string at +0x28.

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
