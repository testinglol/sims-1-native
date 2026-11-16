using System.Text;
using SimsEngine.Formats;

namespace SimsEngine.World;

/// <summary>Maintains lot roof pitch and style settings saved in the house record from version 49 onwards.
/// Pitch is clamped to 0..2 and defaults to 0.5 with no style for older lots; style names correspond to
/// bitmap files under GameData/Roofs.</summary>
public sealed class LotRoof
{
    public const float DefaultPitch = 0.5f;
    public const float MaxPitch = 2f;
    private const float Epsilon = 9.999999974752427e-07f;

    private LotRoof(float pitch, string style, bool present)
    {
        Pitch = pitch;
        Style = style;
        Present = present;
    }

    public float Pitch { get; private set; }

    public string Style { get; private set; }

    /// <summary>Indicates whether the lot stored a roof layer, supported in house records of version 49 or
    /// later.</summary>
    public bool Present { get; private set; }

    private static IffChunk? HouseChunk(IffFile lot) =>
        lot.Find(ResourceType.Parse("HOUS"), 0)
        ?? lot.Chunks.FirstOrDefault(item => item.Type == ResourceType.Parse("HOUS") && item.Id != 1);

    public static LotRoof Read(IffFile lot)
    {
        var chunk = HouseChunk(lot);
        if (chunk is null) return new LotRoof(DefaultPitch, "", false);
        var house = House.Read(lot, chunk);
        return house.RoofPitch is { } pitch ? new LotRoof(pitch, house.RoofName ?? "", true) : new LotRoof(DefaultPitch, "", false);
    }

    public bool SetPitch(float pitch)
    {
        pitch = pitch > 0 ? Math.Min(pitch, MaxPitch) : 0;
        if (Present && Math.Abs(pitch - Pitch) <= Epsilon) return false;
        Pitch = pitch;
        Present = true;
        return true;
    }

    public bool SetStyle(string style)
    {
        if (Present && string.Equals(style, Style, StringComparison.Ordinal)) return false;
        Style = style;
        Present = true;
        return true;
    }

    /// <summary>Lists installed roof style names from bitmap file stems under GameData/Roofs in
    /// alphabetical order.</summary>
    public static IReadOnlyList<string> Styles(string game)
    {
        var data = Content.ObjectRegistry.Resolve(game, "GameData");
        var roofs = data is null ? null : Content.ObjectRegistry.Resolve(data, "Roofs");
        if (roofs is null) return [];
        return Directory.EnumerateFiles(roofs, "*.bmp").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string? MaterialPath(string game, string style)
    {
        var data = Content.ObjectRegistry.Resolve(game, "GameData");
        var roofs = data is null ? null : Content.ObjectRegistry.Resolve(data, "Roofs");
        return roofs is null ? null : Directory.EnumerateFiles(roofs, "*.bmp")
            .FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), style, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Writes roof pitch and style into the lot house record, upgrading records older than version
    /// 49.</summary>
    public IffFile WriteTo(IffFile lot)
    {
        var chunk = HouseChunk(lot)
            ?? throw new InvalidDataException("The lot has no house record to hold the roof");
        var house = House.Read(lot, chunk);
        var text = Encoding.Latin1.GetBytes(Style);
        var data = new List<byte>(chunk.Data.Span[..house.RoofOffset].ToArray());
        if (house.Version <= 0x30)
        {
            if (house.Version <= 8) data.AddRange([0, 0]);
            if (house.Version < 0x1d) data.AddRange(data[0] != 0 ? [(byte)0, (byte)1] : [(byte)1, (byte)0]);
            if (house.Version <= 0x29) data.AddRange([0, 0]);
            var v49 = BitConverter.GetBytes(0x31);
            for (var i = 0; i < 4; i++) data[4 + i] = v49[i];
        }
        var pitchBytes = BitConverter.GetBytes(Pitch);
        if (data.Count > 0 && data[0] != 0) Array.Reverse(pitchBytes);
        data.AddRange(pitchBytes);
        data.AddRange(text);
        data.Add(0);
        if ((data.Count & 1) != 0) data.Add(0);
        return lot.WithChunkDataResized(chunk.Type, chunk.Id, data.ToArray());
    }
}
