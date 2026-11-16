namespace SimsEngine.Formats;

/// <summary>Represents the house record (HOUS) resource in a lot file. Stream fields include lot flags,
/// global lot identifiers, camera rotation index (modulo 4, version 9+), and roof pitch and style name
/// (version 49+, where pitch defaults to 0.5).</summary>
public sealed class House
{
    public int Version { get; }
    public uint StoredFlags { get; }
    public bool Flag0x48 => (StoredFlags & 0xffffff00) != 0;
    public bool Flag0x49 => !Flag0x48 && (StoredFlags & 0xff) != 0;
    public int Global6F0088 { get; }
    public int Global6F0080 { get; }
    public int Global6F0084 { get; }
    public short? CameraRotation { get; }
    public short Field0x18 { get; }
    public bool Flag0x51 { get; }
    public float? RoofPitch { get; }
    public string? RoofName { get; }

    public int RoofOffset { get; private init; }

    private House(int version, uint flags, int global88, int global80, int global84, short? rotation, short field18,
        bool flag51, float? roofPitch, string? roofName)
    {
        Version = version;
        StoredFlags = flags;
        Global6F0088 = global88;
        Global6F0080 = global80;
        Global6F0084 = global84;
        CameraRotation = rotation;
        Field0x18 = field18;
        Flag0x51 = flag51;
        RoofPitch = roofPitch;
        RoofName = roofName;
    }

    public static House Read(IffFile file, IffChunk chunk)
    {
        var stream = new PersistReader(file, chunk);
        var version = stream.Version;
        var flags = (uint)stream.Int32();
        var global88 = stream.Int32();
        var global80 = stream.Int32();
        var global84 = stream.Int32();
        short? rotation = version > 8 ? stream.Int16() : null;
        var field18 = version >= 0x1d ? stream.Int16() : (short)1;
        var flag51 = version > 0x29 && stream.Int16() != 0;
        float? pitch = null;
        string? roof = null;
        var roofOffset = stream.Position;
        if (version > 0x30)
        {
            pitch = stream.Single();
            roof = stream.String();
        }
        return new House(version, flags, global88, global80, global84, rotation, field18, flag51, pitch, roof) { RoofOffset = roofOffset };
    }
}
