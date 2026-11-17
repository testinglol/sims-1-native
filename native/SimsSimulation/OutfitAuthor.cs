using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimsEngine.Formats;

/// <summary>Compiles outfits into content packs. Outfit naming follows a strict pattern combining
/// collection, three-digit index, body type, and suffix, with body textures referenced by collection,
/// index, body type, and skin tone prefix.</summary>
internal static class OutfitAuthor
{
    internal sealed record Document
    {
        public int SchemaVersion { get; init; } = 1;
        public required string PackID { get; init; }
        public string? Name { get; init; }
        public string Version { get; init; } = "0.1.0";
        public required Outfit[] Outfits { get; init; }
    }

    internal sealed record Outfit
    {
        public required string Template { get; init; }
        public required int Number { get; init; }
        public required string Suffix { get; init; }
        /// <summary>Specifies the clothing collection prefix: 'b' for everyday, 'f' for formal, 's' for
        /// swimwear, 'l' for sleepwear, 'w' for winterwear, or 'h' for high fashion.</summary>
        public string? Collection { get; init; }
        public ObjectAuthor.Recolor? Recolor { get; init; }
        /// <summary>Maps skin tone codes ('lgt', 'med', 'drk') to replacement body texture image paths.</summary>
        public Dictionary<string, string> Textures { get; init; } = [];
        public Dictionary<string, string> Meshes { get; init; } = [];
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private static readonly string[] Collections = ["b", "f", "s", "l", "w", "h"];
    private static readonly string[] Tones = ["lgt", "med", "drk"];
    private static readonly string[] HandKinds = ["huao", "huap", "huac"];

    private sealed class Installed
    {
        public readonly Dictionary<string, Appearance> Appearances = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, (string Extension, byte[]? Data)> Textures = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, (string Extension, byte[]? Data)> Meshes = new(StringComparer.OrdinalIgnoreCase);

        public Installed(string game, IReadOnlyCollection<string> keep)
        {
            InstalledFiles.Visit(game, name => Extension(name) is ".cmx" or ".bcf" or ".bmp" or ".tga" or ".skn" or ".bmf", entry =>
            {
                var extension = Extension(entry.Name);
                var name = entry.Name[..^extension.Length].ToLowerInvariant();
                if (extension is ".bmp" or ".tga" or ".skn" or ".bmf")
                {
                    var table = extension is ".skn" or ".bmf" ? Meshes : Textures;
                    if (table.ContainsKey(name)) return;
                    table[name] = (extension, keep.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)) ? entry.Read() : null);
                    return;
                }
                try
                {
                    var data = entry.Read();
                    var parsed = extension == ".cmx"
                        ? CharacterData.ReadText(data, entry.Name)
                        : CharacterData.ReadBinary(data, entry.Name);
                    foreach (var appearance in parsed.Appearances)
                        if (!string.IsNullOrEmpty(appearance.Name)) Appearances.TryAdd(appearance.Name, appearance);
                }
                catch (Exception error) when (error is ResourceFormatException or IOException or FormatException or ArgumentException or IndexOutOfRangeException or OverflowException or InvalidDataException or NullReferenceException)
                {
                }
            });
        }

        private static string Extension(string name) => Path.GetExtension(name).ToLowerInvariant();
    }

    private sealed record OutfitName(string Collection, string Number, string BodyType, string Suffix)
    {
        public string Id => Collection + Number + BodyType;
        public string Name => Id + Suffix;

        public static OutfitName? Parse(string name)
        {
            name = name.ToLowerInvariant();
            if (name.Length < 5 || !char.IsAsciiLetter(name[0]) || !name[1..4].All(char.IsAsciiDigit)) return null;
            var underscore = name.IndexOf('_');
            if (underscore < 0) underscore = name.Length;
            if (underscore <= 4) return null;
            var type = name[4..underscore];
            if (Tones.Any(type.EndsWith)) type = type[..^3];
            if (type.Length == 0) return null;
            return new OutfitName(name[..1], name[1..4], type, name[underscore..]);
        }
    }

    internal static object Compile(string source, string game, string destination)
    {
        if (new FileInfo(source).Length > 1024 * 1024) throw new InvalidDataException("Outfit source exceeds size limit");
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(source), Json) ?? throw new InvalidDataException("Invalid outfit document");
        var root = Path.GetDirectoryName(Path.GetFullPath(source))!;
        var output = Path.GetFullPath(destination);
        if (Directory.Exists(output) || File.Exists(output)) throw new InvalidDataException("Outfit output already exists");
        if (document.SchemaVersion != 1 || !PackIdentifier(document.PackID) || string.IsNullOrWhiteSpace(document.Version) ||
            (document.Name != null && string.IsNullOrWhiteSpace(document.Name)) || document.Outfits == null || document.Outfits.Length is < 1 or > 256)
            throw new InvalidDataException("Invalid outfit pack specification");

        var templates = new List<(Outfit Spec, OutfitName Template, OutfitName Target)>();
        foreach (var spec in document.Outfits)
        {
            if (spec == null) throw new InvalidDataException("Invalid outfit");
            var template = OutfitName.Parse(spec.Template ?? "") ?? throw new InvalidDataException($"Template {spec.Template} is not an outfit name such as b002mafit_01");
            var collection = (spec.Collection ?? template.Collection).ToLowerInvariant();
            if (!Collections.Contains(collection)) throw new InvalidDataException($"Outfit {spec.Template}: collection must be one of {string.Join(", ", Collections)}");
            if (spec.Number is < 0 or > 999) throw new InvalidDataException($"Outfit {spec.Template}: number must be 0..999");
            if (string.IsNullOrEmpty(spec.Suffix) || spec.Suffix.Length > 32 || !spec.Suffix.All(char.IsAsciiLetterOrDigit))
                throw new InvalidDataException($"Outfit {spec.Template}: suffix must be 1..32 letters or digits");
            if (spec.Recolor == null && spec.Textures.Count == 0 && spec.Meshes.Count == 0)
                throw new InvalidDataException($"Outfit {spec.Template}: give a recolor, painted textures, meshes or a combination");
            if (spec.Recolor != null) ObjectAuthor.ValidateRecolor(spec.Recolor);
            foreach (var tone in spec.Textures.Keys)
                if (!Tones.Contains(tone)) throw new InvalidDataException($"Outfit {spec.Template}: texture tones are {string.Join(", ", Tones)}, not {tone}");
            var target = new OutfitName(collection, spec.Number.ToString("D3"), template.BodyType, "_" + spec.Suffix.ToLowerInvariant());
            if (templates.Any(item => item.Target.Id == target.Id))
                throw new InvalidDataException($"Two outfits are both {target.Id}; outfits of one collection and body type need different numbers");
            templates.Add((spec, template, target));
        }

        var installed = new Installed(game, templates.Select(item => item.Template.Id)
            .Concat(templates.Select(item => MeshPrefix + item.Template.Name))
            .Concat(templates.SelectMany(item => HandKinds.Select(kind => kind + item.Template.Suffix))).Distinct().ToArray());
        Dictionary<string, Skeleton>? skeletons = null;
        var resources = new List<(string Path, byte[] Data)>();
        var summary = new List<object>();
        foreach (var (spec, template, target) in templates)
        {
            if (!installed.Appearances.TryGetValue(template.Name, out var appearance))
                throw new InvalidDataException($"Template {spec.Template} is not an installed outfit");
            // Outfit textures are resolved by prefix matching, requiring new outfit identifiers to be
            // unique against all existing appearance and texture names.
            var clash = installed.Appearances.Keys.Concat(installed.Textures.Keys)
                .FirstOrDefault(name => name.StartsWith(target.Id, StringComparison.OrdinalIgnoreCase))
                ?? installed.Meshes.Keys.FirstOrDefault(name => name.StartsWith(MeshPrefix + target.Id, StringComparison.OrdinalIgnoreCase));
            if (clash != null)
                throw new InvalidDataException($"Outfit number {target.Number} is taken for {target.Collection} {target.BodyType}: {clash} is installed");

            var bindings = appearance.Bindings.Select(binding => new Binding(binding.Bone, binding.Mesh, [.. binding.Integers])).ToArray();
            var meshes = new List<object>();
            var unknownMesh = spec.Meshes.Keys.FirstOrDefault(key => !bindings.Any(binding => string.Equals(binding.Mesh, key, StringComparison.OrdinalIgnoreCase)));
            if (unknownMesh != null)
                throw new InvalidDataException($"Outfit {spec.Template}: {unknownMesh} is not one of its meshes ({string.Join(", ", bindings.Select(binding => binding.Mesh))})");
            for (var index = 0; index < bindings.Length; index++)
            {
                var binding = bindings[index];
                var obj = spec.Meshes.FirstOrDefault(item => string.Equals(item.Key, binding.Mesh, StringComparison.OrdinalIgnoreCase)).Value;
                if (obj == null) continue;
                var templateMesh = LoadMesh(installed, binding.Mesh);
                skeletons ??= AnimationAuthor.InstalledSkeletons(game);
                var skeleton = SkeletonFor(skeletons, template);
                var meshName = binding.Mesh.Contains(template.Name, StringComparison.OrdinalIgnoreCase)
                    ? MeshPrefix + target.Name + binding.Mesh[(binding.Mesh.IndexOf(template.Name, StringComparison.OrdinalIgnoreCase) + template.Name.Length)..]
                    : $"{MeshPrefix}{target.Name}-{index}";
                var (mesh, farthest) = OutfitMesh.Import(Encoding.UTF8.GetString(Load(root, obj, $"Outfit {spec.Template} mesh")), obj, templateMesh, skeleton,
                                                               meshName, target.Id + "lgt" + target.Suffix);
                if (farthest > MaximumReach)
                    throw new InvalidDataException($"Mesh {obj} reaches {farthest:0.##} units from the original body; keep it in the exported rest pose and scale");
                resources.Add(($"GameData/Skins/{meshName}.skn", OutfitMesh.Write(mesh)));
                bindings[index] = binding with { Mesh = meshName };
                meshes.Add(new { replaces = templateMesh.Name, mesh = meshName, vertices = mesh.TextureCoordinates.Count, triangles = mesh.Faces.Count,
                                 blended = mesh.Blends.Count, farthest = MathF.Round(farthest, 4) });
            }
            var clone = new Appearance(target.Name, [.. appearance.Integers], bindings);
            using (var stream = new MemoryStream())
            {
                AnimationAuthor.WriteCharacterContainer(stream, [], [clone], [], true);
                resources.Add(($"GameData/Skins/{target.Name}.cmx", stream.ToArray()));
            }

            var tones = new List<object>();
            foreach (var tone in Tones)
            {
                var name = target.Id + tone + target.Suffix;
                if (spec.Textures.TryGetValue(tone, out var painted))
                {
                    var (width, height, rgba) = SpriteArt.ReadPng(Load(root, painted, $"Outfit {spec.Template} {tone} texture"), painted);
                    if (width is < 1 or > 1024 || height is < 1 or > 1024) throw new InvalidDataException($"Painted texture {painted} must be at most 1024x1024");
                    resources.Add(($"GameData/Skins/{name}.bmp", WriteBitmap(width, height, rgba)));
                    tones.Add(new { tone, texture = name, source = "painted", width, height });
                    continue;
                }
                var original = TemplateTexture(installed, template, tone);
                if (original == null) continue;
                if (spec.Recolor == null) continue;
                if (original.Value.Extension != ".bmp")
                    throw new InvalidDataException($"Template texture {original.Value.Name} is not a BMP and cannot be recolored; paint the {tone} tone instead");
                var data = (byte[])original.Value.Data.Clone();
                var colors = ObjectAuthor.RecolorBitmap(spec.Recolor, data, false);
                if (colors == 0) throw new InvalidDataException($"Recolor changed no colors of {original.Value.Name}");
                resources.Add(($"GameData/Skins/{name}.bmp", data));
                tones.Add(new { tone, texture = name, source = original.Value.Name, colors });
            }
            if (tones.Count == 0 && spec.Meshes.Count > 0)
            {
                foreach (var tone in Tones)
                    if (TemplateTexture(installed, template, tone) is { } original)
                    {
                        resources.Add(($"GameData/Skins/{target.Id}{tone}{target.Suffix}{original.Extension}", original.Data));
                        tones.Add(new { tone, texture = target.Id + tone + target.Suffix, source = original.Name });
                    }
            }
            if (tones.Count == 0) throw new InvalidDataException($"Template {spec.Template} has no skin-tone textures to recolor; paint one");

            // Hand and accessory texture bindings share the suffix of their parent outfit.
            var hands = new List<string>();
            foreach (var kind in HandKinds)
                if (installed.Textures.TryGetValue(kind + template.Suffix, out var hand) && hand.Data != null)
                {
                    resources.Add(($"GameData/Skins/{kind}{target.Suffix}{hand.Extension}", hand.Data));
                    hands.Add(kind + target.Suffix);
                }
            summary.Add(new { name = target.Name, template = template.Name, collection = target.Collection, bodyType = target.BodyType,
                              meshes = clone.Bindings.Select(binding => binding.Mesh).ToArray(), reshaped = meshes, tones, hands });
        }

        var duplicate = resources.GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null) throw new InvalidDataException($"Two outfits write {duplicate.Key}");
        var parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".outfit-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (path, data) in resources)
            {
                var file = Path.Combine(staging, "files", path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, data);
            }
            var manifest = new
            {
                schemaVersion = 1, id = document.PackID, name = document.Name ?? document.PackID, version = document.Version,
                baseGameVersion = "1.90", dependencies = Array.Empty<object>(), overrides = Array.Empty<string>()
            };
            File.WriteAllText(Path.Combine(staging, "pack.json"), JsonSerializer.Serialize(manifest, Json));
            Directory.Move(staging, output);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        return new { pack = output, outfits = summary, files = resources.Select(item => item.Path).ToArray() };
    }

    private const string MeshPrefix = "xskin-";
    /// <summary>Defines the maximum vertex distance threshold for mesh retargeting, where an adult Sim
    /// model stands approximately three units tall.</summary>
    private const float MaximumReach = 1.5f;

    private static Mesh LoadMesh(Installed installed, string name)
    {
        if (!installed.Meshes.TryGetValue(name, out var file) || file.Data == null) throw new InvalidDataException($"Mesh {name} is not installed");
        if (file.Extension == ".skn") return OutfitMesh.Read(file.Data);
        try
        {
            var mesh = Mesh.ReadBinary(file.Data, name + ".bmf");
            if (mesh.Bindings.Count == 0 || mesh.TextureCoordinates.Count == 0 || mesh.Vertices.Count == 0)
                throw new InvalidDataException("The mesh has no vertices");
            return mesh;
        }
        catch (ResourceFormatException error)
        {
            throw new InvalidDataException(error.Message, error);
        }
    }

    /// <summary>Resolves the skeleton for a body type, mapping adult bodies to adult skeletons and child
    /// bodies to child skeletons.</summary>
    private static Skeleton SkeletonFor(Dictionary<string, Skeleton> skeletons, OutfitName outfit)
    {
        var name = outfit.BodyType.Length >= 2 && outfit.BodyType[1] == 'c' ? "child" : outfit.BodyType.Length >= 2 && outfit.BodyType[1] == 'a' ? "adult" : null;
        if (name == null || !skeletons.TryGetValue(name, out var skeleton))
            throw new InvalidDataException($"Outfit {outfit.Name} is not for an adult or child Sim, so its mesh cannot be reshaped");
        return skeleton;
    }

    internal static object ExportMeshes(string template, string game, string output)
    {
        var name = OutfitName.Parse(template) ?? throw new InvalidDataException($"Template {template} is not an outfit name such as b002mafit_01");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new InvalidDataException("Export directory is not empty");
        var installed = new Installed(game, [MeshPrefix + name.Name]);
        if (!installed.Appearances.TryGetValue(name.Name, out var appearance)) throw new InvalidDataException($"Template {template} is not an installed outfit");
        var skeleton = SkeletonFor(AnimationAuthor.InstalledSkeletons(game), name);
        Directory.CreateDirectory(output);
        var meshes = new List<object>();
        foreach (var binding in appearance.Bindings)
        {
            var mesh = LoadMesh(installed, binding.Mesh);
            var file = binding.Mesh + ".obj";
            File.WriteAllText(Path.Combine(output, file), OutfitMesh.ExportObj(mesh, skeleton, binding.Mesh));
            meshes.Add(new { mesh = binding.Mesh, file, vertices = mesh.TextureCoordinates.Count, triangles = mesh.Faces.Count,
                             bones = mesh.Bindings.Select(item => mesh.Bones[item.Bone]).ToArray() });
        }
        return new { directory = Path.GetFullPath(output), template = name.Name, skeleton = skeleton.Name, meshes };
    }

    internal static object ExportTextures(string template, string game, string output)
    {
        var name = OutfitName.Parse(template) ?? throw new InvalidDataException($"Template {template} is not an outfit name such as b002mafit_01");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new InvalidDataException("Export directory is not empty");
        var installed = new Installed(game, [name.Id]);
        if (!installed.Appearances.ContainsKey(name.Name)) throw new InvalidDataException($"Template {template} is not an installed outfit");
        Directory.CreateDirectory(output);
        var textures = new Dictionary<string, string>();
        var tones = new List<object>();
        foreach (var tone in Tones)
        {
            var texture = TemplateTexture(installed, name, tone);
            if (texture == null) continue;
            var (width, height, rgba) = texture.Value.Extension == ".bmp" ? ReadBitmap(texture.Value.Data)
                : throw new InvalidDataException($"Template texture {texture.Value.Name} is not a BMP");
            File.WriteAllBytes(Path.Combine(output, tone + ".png"), SpriteArt.WritePng(width, height, rgba, false));
            textures[tone] = tone + ".png";
            tones.Add(new { tone, texture = texture.Value.Name, width, height });
        }
        if (tones.Count == 0) throw new InvalidDataException($"Template {template} has no skin-tone textures");
        var example = new
        {
            schemaVersion = 1, packID = "local.my-outfits", outfits = new[] { new { template = name.Name, number = 900, suffix = "mine", textures } }
        };
        File.WriteAllText(Path.Combine(output, "outfit.json"), JsonSerializer.Serialize(example, Json));
        return new { directory = Path.GetFullPath(output), template = name.Name, tones };
    }

    private static (string Name, string Extension, byte[] Data)? TemplateTexture(Installed installed, OutfitName template, string tone)
    {
        // Texture lookup selects the first texture matching the outfit ID and skin tone prefix, preferring
        // an exact match for the outfit suffix.
        var prefix = template.Id + tone;
        var name = installed.Textures.ContainsKey(prefix + template.Suffix) ? prefix + template.Suffix
            : installed.Textures.Keys.Where(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).FirstOrDefault();
        if (name == null) return null;
        var texture = installed.Textures[name];
        return texture.Data == null ? null : (name, texture.Extension, texture.Data);
    }

    private static byte[] Load(string root, string relative, string what)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException($"{what} must be a path relative to the document");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException($"{what} {relative} is outside the document directory");
        if (!File.Exists(path)) throw new InvalidDataException($"{what} {relative} does not exist");
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException($"{what} {relative} exceeds size limit");
        return File.ReadAllBytes(path);
    }

    private static bool PackIdentifier(string value) => value != null && value.Length is >= 1 and <= 120 && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.');

    /// <summary>Encodes skin texture data as an uncompressed bottom-up 24-bit bitmap without transparency.</summary>
    internal static byte[] WriteBitmap(int width, int height, byte[] rgba)
    {
        var stride = (width * 3 + 3) & ~3;
        var data = new byte[54 + stride * height];
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        BitConverter.GetBytes(data.Length).CopyTo(data, 2);
        BitConverter.GetBytes(54).CopyTo(data, 10);
        BitConverter.GetBytes(40).CopyTo(data, 14);
        BitConverter.GetBytes(width).CopyTo(data, 18);
        BitConverter.GetBytes(height).CopyTo(data, 22);
        BitConverter.GetBytes((ushort)1).CopyTo(data, 26);
        BitConverter.GetBytes((ushort)24).CopyTo(data, 28);
        BitConverter.GetBytes(stride * height).CopyTo(data, 34);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var source = (y * width + x) * 4;
            var target = 54 + (height - 1 - y) * stride + x * 3;
            data[target] = rgba[source + 2];
            data[target + 1] = rgba[source + 1];
            data[target + 2] = rgba[source];
        }
        return data;
    }

    /// <summary>Decodes skin bitmaps supporting 8-bit uncompressed, 8-bit RLE, 24-bit, and 32-bit encodings
    /// into top-down RGBA pixel buffers.</summary>
    internal static (int Width, int Height, byte[] Rgba) ReadBitmap(byte[] data)
    {
        if (data.Length < 54 || data[0] != 'B' || data[1] != 'M') throw new InvalidDataException("Not a BMP");
        var pixels = BitConverter.ToInt32(data, 10);
        var header = BitConverter.ToInt32(data, 14);
        var width = BitConverter.ToInt32(data, 18);
        var rawHeight = BitConverter.ToInt32(data, 22);
        var height = Math.Abs(rawHeight);
        var bits = BitConverter.ToUInt16(data, 28);
        var compression = BitConverter.ToUInt32(data, 30);
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || pixels < 0 || pixels > data.Length) throw new InvalidDataException("BMP dimensions are invalid");
        var rgba = new byte[width * height * 4];
        int Row(int y) => rawHeight > 0 ? height - 1 - y : y;
        void Put(int x, int y, byte r, byte g, byte b)
        {
            if (x >= width || y >= height) return;
            var offset = (Row(y) * width + x) * 4;
            (rgba[offset], rgba[offset + 1], rgba[offset + 2], rgba[offset + 3]) = (r, g, b, 255);
        }
        if (bits == 8)
        {
            var used = BitConverter.ToInt32(data, 46);
            var palette = new byte[256][];
            for (var index = 0; index < 256; index++)
            {
                var offset = 14 + header + index * 4;
                palette[index] = index < (used > 0 ? used : 256) && offset + 3 <= data.Length ? [data[offset + 2], data[offset + 1], data[offset]] : [0, 0, 0];
            }
            void Index(int x, int y, byte value) => Put(x, y, palette[value][0], palette[value][1], palette[value][2]);
            if (compression == 0)
            {
                var stride = (width + 3) & ~3;
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    if (pixels + y * stride + x < data.Length) Index(x, y, data[pixels + y * stride + x]);
            }
            else if (compression == 1)
            {
                int position = pixels, column = 0, line = 0;
                while (position + 1 < data.Length && line < height)
                {
                    var count = data[position++];
                    var value = data[position++];
                    if (count > 0) { for (var index = 0; index < count; index++) Index(column++, line, value); continue; }
                    if (value == 0) { column = 0; line++; }
                    else if (value == 1) break;
                    else if (value == 2) { if (position + 1 >= data.Length) break; column += data[position++]; line += data[position++]; }
                    else
                    {
                        for (var index = 0; index < value && position < data.Length; index++) Index(column++, line, data[position++]);
                        if ((value & 1) == 1) position++;
                    }
                }
            }
            else throw new InvalidDataException("Unsupported 8-bit BMP compression");
        }
        else if ((bits == 24 || bits == 32) && compression is 0 or 3)
        {
            var size = bits / 8;
            var stride = (width * size + 3) & ~3;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = pixels + y * stride + x * size;
                if (offset + 3 <= data.Length) Put(x, y, data[offset + 2], data[offset + 1], data[offset]);
            }
        }
        else throw new InvalidDataException($"Unsupported {bits}-bit BMP");
        return (width, height, rgba);
    }
}
