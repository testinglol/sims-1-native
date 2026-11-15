namespace SimsEngine.Formats;

/// <summary>One palette entry.</summary>
public readonly record struct PaletteColor(byte Red, byte Green, byte Blue);

/// <summary>A sprite palette (PALT), read by Sims.exe FUN_004b9685 when FUN_004c915f loads a file's palettes: a u32
/// version, a u32 colour count, eight bytes Sims.exe does not read, then three bytes (red, green, blue) per colour.
/// Sims.exe trusts the count; here a count that runs past the data is an error.</summary>
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
