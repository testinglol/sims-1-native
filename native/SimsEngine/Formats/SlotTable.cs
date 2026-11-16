namespace SimsEngine.Formats;

/// <summary>Represents a single routing or container slot definition from a SLOT resource.</summary>
public sealed record Slot(
    ushort Type,
    float X,
    float Y,
    float Z,
    int Standing,
    int Sitting,
    int Ground,
    uint Flags,
    int SnapTargetSlot,
    int MinProximity,
    int MaxProximity,
    int OptimalProximity,
    int MaxSize,
    int Reserved14,
    float Gradient,
    int Height,
    int Facing,
    int Resolution);

/// <summary>Parses an object slot table (SLOT). Handles format evolution from version 3 through 10,
/// including legacy facing flags and pre-version-10 proximity and gradient coordinate scaling.</summary>
public sealed class SlotTable
{
    public int Version { get; }
    public IReadOnlyList<Slot> Slots { get; }

    private SlotTable(int version, IReadOnlyList<Slot> slots)
    {
        Version = version;
        Slots = slots;
    }

    public static SlotTable Read(IffFile file, IffChunk chunk)
    {
        var reader = file.Reader(chunk);
        reader.U32();
        var version = reader.S32();
        if (!reader.Bytes(4).SequenceEqual("TOLS"u8)) throw reader.Error("missing the TOLS tag");
        if (version < 3) throw reader.Error($"version {version} is not read");
        var count = reader.S32();
        // A version 3 entry, the smallest format variant, is 30 bytes.
        if (count < 0 || count > reader.Remaining / 30) throw reader.Error($"{count} slots do not fit the chunk");
        var slots = new Slot[count];
        for (var index = 0; index < count; index++) slots[index] = ReadSlot(ref reader, version);
        return new SlotTable(version, slots);
    }

    private static Slot ReadSlot(ref ResourceReader reader, int version)
    {
        var type = reader.U16();
        var x = Float(ref reader);
        var y = Float(ref reader);
        var z = Float(ref reader);
        var standing = reader.S32();
        var sitting = reader.S32();
        var ground = reader.S32();
        var flags = reader.U32();
        int snapTarget = -1, minProximity = 16, maxProximity = 16, optimalProximity = 16;
        int maxSize = 0, reserved14 = 0, height = 0, facing = -2, resolution = 16;
        var gradient = 0.1875f;
        if (version >= 4) snapTarget = reader.S32();
        if (version >= 5)
        {
            minProximity = reader.S32();
            maxProximity = reader.S32();
            optimalProximity = reader.S32();
            maxSize = reader.S32();
            reserved14 = reader.S32();
        }
        if (version >= 7) gradient = Float(ref reader);
        if (version >= 8) height = reader.S32();
        if (version >= 9) facing = reader.S32();
        else if ((flags & 0x200) != 0)
        {
            facing = -3;
            flags &= ~0x200u;
        }
        else if ((flags & 0x400) != 0)
        {
            facing = -1;
            flags &= ~0x400u;
        }
        if (version >= 10) resolution = reader.S32();
        else
        {
            minProximity <<= 4;
            maxProximity <<= 4;
            optimalProximity <<= 4;
            gradient *= 0.0625f;
        }
        return new Slot(type, x, y, z, standing, sitting, ground, flags, snapTarget, minProximity, maxProximity,
            optimalProximity, maxSize, reserved14, gradient, height, facing, resolution);
    }

    private static float Float(ref ResourceReader reader) => BitConverter.Int32BitsToSingle(reader.S32());
}
