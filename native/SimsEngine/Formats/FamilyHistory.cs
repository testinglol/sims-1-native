namespace SimsEngine.Formats;

/// <summary>One 0x54-byte FAMh entry in stream order (FUN_00411484): s16s at +0x00 to +0x0e, u32s at +0x10 and +0x14,
/// s16s at +0x18 to +0x24, u32s at +0x28 to +0x3c, u32s at +0x40 and +0x44 and s16s at +0x48 to +0x50.</summary>
public sealed record FamilyHistoryEntry(
    IReadOnlyList<short> Values0x00,
    IReadOnlyList<int> Values0x10,
    IReadOnlyList<short> Values0x18,
    IReadOnlyList<int> Values0x28,
    IReadOnlyList<int> Values0x40,
    IReadOnlyList<short> Values0x48);

/// <summary>The FAMh chunk of a neighbourhood file, as Sims.exe FUN_0041144b and FUN_0041187d read it from the
/// resource stream: the field-coding flag, a u32 entry count, then the entries.
/// <para>Before version 1 the values at +0x10, +0x14 and +0x28 to +0x3c are stored as s16s and widened. Inferred, not
/// traced: that the entries are per-family history records; the tag is all this reader relies on.</para></summary>
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
