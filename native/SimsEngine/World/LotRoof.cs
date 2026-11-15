using System.Text;
using SimsEngine.Formats;

namespace SimsEngine.World;

/// <summary>The lot's roof settings, the pitch and the style name Sims.exe keeps in the roof layer (DAT_006f627c) and saves in
/// the house record from version 49. FUN_0056f23d sets the pitch (clamped to 0..2, a change of 1e-6 or less is ignored) and
/// FUN_0056f32e the style name (a different name replaces the layer); both rebuild the roof, and the roof panel's undo
/// (cRoofUndoable, FUN_004906b1) swaps them back. A lot without a roof layer reads as pitch 0.5 and no style. The
/// styles are the bitmaps of GameData/Roofs, named by file stem.</summary>
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

    /// <summary>Whether the lot stored a roof layer (a house record of version 49 or later).</summary>
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

    /// <summary>FUN_0056f23d: returns whether the pitch changed.</summary>
    public bool SetPitch(float pitch)
    {
        pitch = pitch > 0 ? Math.Min(pitch, MaxPitch) : 0;
        if (Present && Math.Abs(pitch - Pitch) <= Epsilon) return false;
        Pitch = pitch;
        Present = true;
        return true;
    }

    /// <summary>FUN_0056f32e: returns whether the style changed.</summary>
    public bool SetStyle(string style)
    {
        if (Present && string.Equals(style, Style, StringComparison.Ordinal)) return false;
        Style = style;
        Present = true;
        return true;
    }

    /// <summary>The roof styles installed under GameData/Roofs, as file stems in name order.</summary>
    public static IReadOnlyList<string> Styles(string game)
    {
        var data = Content.ObjectRegistry.Resolve(game, "GameData");
        var roofs = data is null ? null : Content.ObjectRegistry.Resolve(data, "Roofs");
        if (roofs is null) return [];
        return Directory.EnumerateFiles(roofs, "*.bmp").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>The installed style bitmap used by the procedural roof meshes. Enumerating stems also prevents a
    /// saved style name from escaping GameData/Roofs.</summary>
    public static string? MaterialPath(string game, string style)
    {
        var data = Content.ObjectRegistry.Resolve(game, "GameData");
        var roofs = data is null ? null : Content.ObjectRegistry.Resolve(data, "Roofs");
        return roofs is null ? null : Directory.EnumerateFiles(roofs, "*.bmp")
            .FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), style, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Writes the pitch and style into the lot's house record (upgrading pre-version-49 records to version 49), after the fields before them.</summary>
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
