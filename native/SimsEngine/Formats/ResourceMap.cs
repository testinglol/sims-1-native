using System.Buffers.Binary;
using System.Text;

namespace SimsEngine.Formats;

/// <summary>One chunk's row in an IFF resource map (rsmp): the chunk id, its flags, its file offset and its label.</summary>
public sealed record ResourceMapEntry(ushort Id, ushort Flags, int Offset, string Label);

/// <summary>One chunk type's rows in an IFF resource map.</summary>
public sealed record ResourceMapGroup(ResourceType Type, IReadOnlyList<ResourceMapEntry> Entries);

/// <summary>The IFF resource map (the rsmp chunk): a 20-byte header (two zero u32s, the reversed "pmsr" tag, a zero
/// u32 and a u32 group count), then per chunk type the reversed 4cc type, a u32 entry count and per entry the chunk's
/// u32 offset, u16 id, u16 flags and its zero-terminated label padded to an even length. Sims.exe's loader
/// (FUN_00416fc3) scans the sequential chunk headers instead and never reads this index; it is written for tools that
/// look chunks up through it.</summary>
public static class ResourceMap
{
    public static IReadOnlyList<ResourceMapGroup> Read(IffChunk chunk)
    {
        var data = chunk.Data.Span;
        if (data.Length < 20 || !data[8..12].SequenceEqual("pmsr"u8))
            throw new ResourceFormatException(chunk.ToString(), null, 8, "the resource map has no pmsr tag");
        var groups = new List<ResourceMapGroup>();
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[16..20]);
        var at = 20;
        for (var group = 0; group < count; group++)
        {
            if (at + 8 > data.Length) throw new ResourceFormatException(chunk.ToString(), null, at, "the resource map ends inside a group");
            var type = new ResourceType(BinaryPrimitives.ReadUInt32LittleEndian(data[at..(at + 4)]));
            var entries = new List<ResourceMapEntry>();
            var entriesCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..(at + 8)]);
            at += 8;
            for (var index = 0; index < entriesCount; index++)
            {
                if (at + 8 > data.Length) throw new ResourceFormatException(chunk.ToString(), null, at, "the resource map ends inside an entry");
                var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[at..(at + 4)]);
                var id = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 4)..(at + 6)]);
                var flags = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 6)..(at + 8)]);
                at += 8;
                var end = at;
                while (end < data.Length && data[end] != 0) end++;
                if (end >= data.Length) throw new ResourceFormatException(chunk.ToString(), null, at, "the resource map label is not terminated");
                var label = ResourceReader.Latin1(data[at..end]);
                at = end + 1;
                if ((at & 1) != 0) at++;
                entries.Add(new ResourceMapEntry(id, flags, offset, label));
            }
            groups.Add(new ResourceMapGroup(type, entries));
        }
        return groups;
    }

    /// <summary>Writes the index for <paramref name="chunks"/> (the rsmp chunk itself excluded) with their new file
    /// offsets.</summary>
    public static byte[] Write(IReadOnlyList<(ResourceType Type, ushort Id, ushort Flags, string Label, int Offset)> chunks)
    {
        using var output = new MemoryStream();
        output.Write(new byte[8]);
        output.Write("pmsr"u8);
        output.Write(new byte[4]);
        var groups = chunks.GroupBy(chunk => chunk.Type).ToList();
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)groups.Count);
        output.Write(word);
        Span<byte> half = stackalloc byte[2];
        foreach (var group in groups)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(word, group.Key.Code);
            output.Write(word);
            BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)group.Count());
            output.Write(word);
            foreach (var entry in group)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)entry.Offset);
                output.Write(word);
                BinaryPrimitives.WriteUInt16LittleEndian(half, entry.Id);
                output.Write(half);
                BinaryPrimitives.WriteUInt16LittleEndian(half, entry.Flags);
                output.Write(half);
                var label = Encoding.Latin1.GetBytes(entry.Label);
                output.Write(label);
                output.WriteByte(0);
                // The map pads an odd label with the same 0xA3 byte the streams use.
                if ((output.Position & 1) != 0) output.WriteByte(0xA3);
            }
        }
        return output.ToArray();
    }
}
