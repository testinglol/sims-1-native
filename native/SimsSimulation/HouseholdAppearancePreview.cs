using System.Security.Cryptography;
using System.Text.Json;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;

internal static class HouseholdAppearancePreview
{
    private sealed record PreviewTexture(string File, int Width, int Height, string Encoding);
    private sealed record PreviewVertex(float X, float Y, float Z, float U, float V, float Nx, float Ny, float Nz);
    private sealed record PreviewMesh(int Texture, int ObjectID, float Depth, PreviewVertex[] Vertices, int Layer);
    private sealed record PreviewDocument(int SchemaVersion, float Width, float Height, PreviewTexture[] Textures, PreviewMesh[] Meshes);

    internal static XPerson CreatePreviewPerson(string game, ObjectRegistry registry, string gender, string age, string skin, string headChoice, string bodyChoice)
    {
        var catalog = HouseholdAppearance.Catalog(game, registry, gender, age, skin);
        var body = catalog.Bodies.SingleOrDefault(choice => string.Equals(choice.Id, bodyChoice, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Selected clothing is not available for this Sim's gender, age and skin");
        var head = catalog.Heads.SingleOrDefault(choice => string.Equals(choice.Id, headChoice, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Selected head is not available for this Sim's gender, age and skin");

        var headPipe = head.Id.IndexOf('|');
        var headItem = headPipe >= 0 ? head.Id[..headPipe] : head.Id;
        var headTexture = head.Texture;

        var bodyPipe = body.Id.IndexOf('|');
        var bodyItem = bodyPipe >= 0 ? body.Id[..bodyPipe] : body.Id;
        var bodyTexture = body.Texture;

        var child = age == "child";
        var code = child ? 'u' : gender.Length > 0 && (gender[0] == 'm' || gender[0] == 'M') ? 'm' : 'f';
        var handTexture = "huao" + skin;

        var parts = new (string, string)[]
        {
            (bodyItem.EndsWith(".apr", StringComparison.OrdinalIgnoreCase) ? bodyItem : bodyItem + ".apr", "BODY=" + bodyTexture),
            ("", ""),
            ($"h{code}lo.apr", handTexture),
            ($"h{code}ro.apr", handTexture),
            (headItem.EndsWith(".apr", StringComparison.OrdinalIgnoreCase) ? headItem : headItem + ".apr", "HEAD-HEAD=" + headTexture),
        };

        var character = new PersonCharacter(0, 0, parts, [], [string.Empty, string.Empty, string.Empty], 3, new float[9]);
        var person = new XPerson(1, new short[XObject.DefinitionCount])
        {
            SavedCharacter = character,
            Position = new ObjectPosition(64 * 16, 64 * 16, 1)
        };
        person[PersonData.PersonAge] = (short)(child ? 9 : 27);
        person[ObjectData.Direction] = 3;
        return person;
    }

    internal static object Export(string session, string game, ObjectRegistry registry, string gender, string age, string skin, string headChoice, string bodyChoice, bool portrait = false)
    {
        var person = CreatePreviewPerson(game, registry, gender, age, skin, headChoice, bodyChoice);
        var child = age == "child";
        var geometry = AvatarSceneGeometry.For(game, registry);
        if (portrait)
        {
            var headMeshes = geometry.BuildHeadMeshes(person, 1, 0, (_, _) => 0, new LotCamera(0, 128, 128));
            return WritePortrait(session, SimPortrait.FaceBitmap(headMeshes));
        }
        var nativeParts = geometry.Build(person, 1, 0, (_, _) => 0, new LotCamera(0, 128, 128));
        if (nativeParts.Count == 0 || nativeParts.Sum(part => part.Vertices.Length) > 250_000)
            throw new InvalidDataException("The selected Sim appearance produced invalid preview geometry");

        return Write(session, nativeParts, child);
    }

    /// <summary>Normalizes avatar geometry into a unit square and writes preview.json plus its textures.</summary>
    internal static object Write(string session, IReadOnlyList<AvatarSceneGeometry.Part> parts, bool child = false)
    {
        var minX = parts.SelectMany(part => part.Vertices).Min(vertex => vertex.X);
        var maxX = parts.SelectMany(part => part.Vertices).Max(vertex => vertex.X);
        var minY = parts.SelectMany(part => part.Vertices).Min(vertex => vertex.Y);
        var maxY = parts.SelectMany(part => part.Vertices).Max(vertex => vertex.Y);
        var rangeX = maxX - minX;
        var rangeY = maxY - minY;
        if (!float.IsFinite(rangeX) || !float.IsFinite(rangeY) || rangeX <= 0 || rangeY <= 0)
            throw new InvalidDataException("The selected Sim appearance has invalid preview bounds");
        var maxHeight = child ? 0.72f : 0.94f;
        var scale = MathF.Min(0.88f / rangeX, maxHeight / rangeY);
        var centerX = (minX + maxX) / 2;

        var directory = Path.Combine(session, "cas-preview");
        var textureDirectory = Path.Combine(directory, "textures");
        Directory.CreateDirectory(textureDirectory);
        var textures = new List<PreviewTexture>();
        var textureIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        var meshes = new List<PreviewMesh>();
        foreach (var part in parts)
        {
            var key = Convert.ToHexString(SHA256.HashData(part.Texture));
            if (!textureIndices.TryGetValue(key, out var textureIndex))
            {
                textureIndex = textures.Count;
                textureIndices.Add(key, textureIndex);
                var file = $"textures/{key}.{part.Encoding}";
                File.WriteAllBytes(Path.Combine(directory, file), part.Texture);
                textures.Add(new PreviewTexture(file, part.Width, part.Height, part.Encoding));
            }
            var vertices = part.Vertices.Select(vertex => new PreviewVertex(
                0.5f + (vertex.X - centerX) * scale,
                0.97f - (maxY - vertex.Y) * scale,
                vertex.Z,
                vertex.U,
                vertex.V,
                vertex.Nx,
                vertex.Ny,
                vertex.Nz)).ToArray();
            meshes.Add(new PreviewMesh(textureIndex, 0, 0.5f, vertices, 1));
        }

        var path = Path.Combine(directory, "preview.json");
        var temporary = path + ".tmp";
        var document = new PreviewDocument(1, 1, 1, textures.ToArray(), meshes.ToArray());
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return new { file = "cas-preview/preview.json", schemaVersion = 1, meshes = meshes.Count, textures = textures.Count };
    }

    private static object WritePortrait(string session, byte[] bitmap)
    {
        var directory = Path.Combine(session, "cas-preview");
        var textureDirectory = Path.Combine(directory, "textures");
        Directory.CreateDirectory(textureDirectory);
        var hash = Convert.ToHexString(SHA256.HashData(bitmap));
        var file = $"textures/{hash}.bmp";
        File.WriteAllBytes(Path.Combine(directory, file), bitmap);
        var texture = new PreviewTexture(file, 35, 41, "bmp");
        var vertices = new[]
        {
            new PreviewVertex(0.075f, 0.05f, 0.5f, 0, 0, 0, 0, 1),
            new PreviewVertex(0.925f, 0.05f, 0.5f, 1, 0, 0, 0, 1),
            new PreviewVertex(0.925f, 0.95f, 0.5f, 1, 1, 0, 0, 1),
            new PreviewVertex(0.075f, 0.05f, 0.5f, 0, 0, 0, 0, 1),
            new PreviewVertex(0.925f, 0.95f, 0.5f, 1, 1, 0, 0, 1),
            new PreviewVertex(0.075f, 0.95f, 0.5f, 0, 1, 0, 0, 1)
        };
        var mesh = new PreviewMesh(0, 0, 0.5f, vertices, 1);
        var path = Path.Combine(directory, "preview.json");
        var temporary = path + ".tmp";
        var document = new PreviewDocument(1, 1, 1, [texture], [mesh]);
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return new { file = "cas-preview/preview.json", schemaVersion = 1, meshes = 1, textures = 1 };
    }
}
