namespace SimsEngine.Formats;

public sealed record SimulatorConstant(string Name, float Value, string Description);

/// <summary>Manages simulator tuning constants loaded from the Global.iff FCNS resource, supporting
/// case-insensitive lookups with fallback defaults.</summary>
public sealed class SimulatorConstants
{
    private readonly Dictionary<string, float> _values = new(StringComparer.OrdinalIgnoreCase);

    public static SimulatorConstants Empty { get; } = new([]);
    public IReadOnlyList<SimulatorConstant> Entries { get; }

    private SimulatorConstants(IReadOnlyList<SimulatorConstant> entries)
    {
        Entries = entries;
        foreach (var entry in entries)
            _values.TryAdd(entry.Name, entry.Value);
    }

    /// <summary>Reads the version 1 FCNS layout, consisting of an SNCF header, row count, and even-padded
    /// name, float value, and description fields.</summary>
    public static SimulatorConstants Read(IffFile file, IffChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Type != ResourceType.Fcns)
            throw new ArgumentException($"Expected an FCNS chunk, got {chunk.Type}.", nameof(chunk));

        var reader = file.Reader(chunk);
        var reserved = reader.U32();
        if (reserved != 0) throw reader.Error($"reserved header word is {reserved}, expected 0");
        var version = reader.S32();
        if (version != 1) throw reader.Error($"FCNS version {version} is not supported by the original Sims format reader");
        if (!reader.Bytes(4).SequenceEqual("SNCF"u8)) throw reader.Error("missing the SNCF FCNS signature");
        var count = reader.S32();
        if (count < 0 || count > reader.Remaining / 8)
            throw reader.Error($"FCNS entry count {count} cannot fit in {reader.Remaining} bytes");

        var entries = new SimulatorConstant[count];
        for (var index = 0; index < entries.Length; index++)
        {
            var name = reader.EvenPaddedString();
            var value = BitConverter.Int32BitsToSingle(reader.S32());
            var description = reader.EvenPaddedString();
            entries[index] = new SimulatorConstant(name, value, description);
        }
        if (reader.Remaining != 0) throw reader.Error($"{reader.Remaining} trailing bytes after {count} FCNS entries");
        return new SimulatorConstants(entries);
    }

    public float Get(string name, float defaultValue) => _values.GetValueOrDefault(name, defaultValue);
}
