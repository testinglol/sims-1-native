namespace SimsEngine.Formats;

public readonly record struct PaletteColor(byte Red, byte Green, byte Blue);

/// <summary>Represents a sprite palette (PALT) chunk, consisting of a version, color count, eight reserved
/// bytes, and RGB color triples.</summary>
public sealed class Palette
{
    public uint Version { get; }
    public IReadOnlyList<PaletteColor> Colors { get; }

    private Palette(uint version, IReadOnlyList<PaletteColor> colors)
    {
        Version = version;
        Colors = colors;
    }

    public static Palette Read(IffFile file, IffChunk chunk)
    {
        var reader = file.Reader(chunk);
        var version = reader.U32();
        var count = reader.U32();
        reader.Bytes(8);
        if (count > reader.Remaining / 3) throw reader.Error($"{count} colours do not fit the chunk");
        var colors = new PaletteColor[count];
        for (var index = 0; index < colors.Length; index++)
        {
            var rgb = reader.Bytes(3);
            colors[index] = new PaletteColor(rgb[0], rgb[1], rgb[2]);
        }
        return new Palette(version, colors);
    }
}
