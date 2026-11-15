using SimsEngine.Formats;
using SimsEngine.World;
using System.Text.Json;
using SimsSimulation;

/// <summary>Checks original floor patterns and palette selection used by native lot scene export.</summary>
internal static class FloorArtworkChecks
{
    public static object Run(string gameRoot, string? scenePath = null)
    {
        var floors = IffFile.Open(Path.Combine(gameRoot, "GameData", "floors.iff"));
        // House 05 uses floor pattern 12. The original floors.iff stores its medium SPR2 at pattern + 256.
        var spriteChunk = floors.Find(ResourceType.Spr2, 268)
            ?? throw new InvalidDataException("Base-game floor pattern 12 has no SPR2 268");
        var sprite = DepthSprite.Read(floors, spriteChunk);
        var paletteChunk = floors.Find(ResourceType.Palt, checked((ushort)sprite.PaletteId))
            ?? throw new InvalidDataException($"Base-game floor SPR2 268 names missing PALT {sprite.PaletteId}");
        var palette = Palette.Read(floors, paletteChunk);
        var frame = sprite.Frames.FirstOrDefault()
            ?? throw new InvalidDataException("Base-game floor SPR2 268 has no frames");
        var visibleColors = frame.Indices.Where((_, index) => frame.Alpha[index] != 0)
            .Select(index => palette.Colors[index]).Distinct().Count();
        if (sprite.PaletteId != 12 || visibleColors < 2)
            throw new InvalidDataException("Base-game floor pattern 12 did not resolve to its colored SPR2-header palette");
        if (SceneExporter.HasFloorArtwork(0xff))
            throw new InvalidDataException("The special 0xff floor value was treated as a missing SPR2 pattern");
        if (!SceneExporter.HasFloorArtwork(12))
            throw new InvalidDataException("A painted base-game floor pattern was treated as empty");

        var dynamicChecked = 0;
        if (scenePath is not null)
        {
            using var document = JsonDocument.Parse(SimsSimulation.InMemorySessionAssets.ReadAllBytes(scenePath));
            var scene = document.RootElement;
            var resources = FloorCatalog.DynamicFiles(Path.Combine(gameRoot, "GameData")).ToDictionary();
            foreach (var mesh in scene.GetProperty("meshes").EnumerateArray())
            {
                if (!mesh.TryGetProperty("floor", out var surface)) continue;
                var pattern = surface.GetProperty("pattern").GetInt32();
                if (pattern is < 256 or >= 65534) continue;
                var resource = IffFile.Open(resources[pattern]);
                var art = DepthSprite.Read(resource, resource.Find(ResourceType.Spr2, 257)!);
                var source = art.Frames[0];
                var colors = Palette.Read(resource, resource.Find(ResourceType.Palt, checked((ushort)art.PaletteId))!).Colors;
                var texture = scene.GetProperty("textures")[mesh.GetProperty("texture").GetInt32()];
                if (texture.GetProperty("width").GetInt32() != source.Width || texture.GetProperty("height").GetInt32() != source.Height)
                    throw new InvalidDataException($"Floor {pattern} does not use its original medium dimensions");
                var bytes = SimsSimulation.InMemorySessionAssets.ReadAllBytes(Path.Combine(Path.GetDirectoryName(scenePath)!, texture.GetProperty("file").GetString()!));
                if (bytes.Length != source.Width * source.Height * 4)
                    throw new InvalidDataException($"Floor {pattern} has an invalid RGBA texture length");
                for (var i = 0; i < source.Indices.Length; i++)
                {
                    var color = colors[source.Indices[i]];
                    var expected = source.Alpha[i] == 0 ? new byte[4] : new byte[]
                    {
                        color.Red, color.Green, color.Blue, (byte)((source.Alpha[i] * 255 + 15) / 31),
                    };
                    if (!bytes.AsSpan(i * 4, 4).SequenceEqual(expected))
                        throw new InvalidDataException($"Floor {pattern} differs from its original resource at pixel {i}");
                }
                dynamicChecked++;
            }
            if (dynamicChecked == 0) throw new InvalidDataException("Scene contains no standalone floor surfaces to verify");
        }

        return new { result = "passed", pattern = 12, sprite = 268, palette = sprite.PaletteId, visibleColors, specialNoArtPattern = 255, dynamicChecked };
    }
}
