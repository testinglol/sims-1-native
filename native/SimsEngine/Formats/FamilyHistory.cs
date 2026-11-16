namespace SimsEngine.Formats;

public sealed record FamilyHistoryEntry(
    IReadOnlyList<short> Values0x00,
    IReadOnlyList<int> Values0x10,
    IReadOnlyList<short> Values0x18,
    IReadOnlyList<int> Values0x28,
    IReadOnlyList<int> Values0x40,
    IReadOnlyList<short> Values0x48);

/// <summary>Represents the neighborhood family history (FAMh) chunk, containing a field-coding flag, an
/// entry count, and sequential history records whose financial fields are widened to 32-bit in version 1
/// and later.</summary>
public sealed class FamilyHistory
{
    public int Version { get; }
    public IReadOnlyList<FamilyHistoryEntry> Entries { get; }

    private FamilyHistory(int version, IReadOnlyList<FamilyHistoryEntry> entries)
    {
        Version = version;
        Entries = entries;
    }

    public static FamilyHistory Read(IffFile file, IffChunk chunk)
    {
        var stream = new PersistReader(file, chunk);
        stream.ReadCodingFlag();
        var count = stream.Count(64);
        var entries = new FamilyHistoryEntry[count];
        for (var index = 0; index < count; index++)
        {
            entries[index] = new FamilyHistoryEntry(
                stream.Int16s(8),
                Wide(ref stream, 2),
                stream.Int16s(7),
                Wide(ref stream, 6),
                [stream.Int32(), stream.Int32()],
                stream.Int16s(5));
        }
        return new FamilyHistory(stream.Version, entries);
    }

    private static int[] Wide(ref PersistReader stream, int count)
    {
        var values = new int[count];
        for (var index = 0; index < count; index++) values[index] = stream.Version < 1 ? stream.Int16() : stream.Int32();
        return values;
    }
}
