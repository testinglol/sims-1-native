using System.Globalization;
using System.Text.Json.Serialization;

namespace SimsSDL.Core;

public sealed record PackAvatarResource(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("owner")] string Owner);

internal static class AvatarContainer
{
    private ref struct BinaryCursor
    {
        private readonly ReadOnlySpan<byte> _data;
        public int Offset;

        public BinaryCursor(ReadOnlySpan<byte> data)
        {
            _data = data;
            Offset = 0;
        }

        public ReadOnlySpan<byte> Remaining => _data[Offset..];

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || count > _data.Length - Offset)
            {
                throw new ContentError("Truncated BCF");
            }
            var slice = _data.Slice(Offset, count);
            Offset += count;
            return slice;
        }

        public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));
    }

    private ref struct Reader
    {
        private readonly bool _isBinary;
        private BinaryCursor _binary;
        private readonly string[] _lines;
        private int _line;
        private string[] _numbers;
        private int _number;
        private int _operations;

        public Reader(ReadOnlySpan<byte> data, bool text)
        {
            if (data.Length > 64 * 1024 * 1024)
            {
                throw new ContentError("Avatar container exceeds size limit");
            }

            _operations = 0;
            _number = 0;
            _numbers = [];
            _line = 0;

            if (text)
            {
                _isBinary = false;
                _binary = default;
                string content;
                try
                {
                    content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(data);
                }
                catch (DecoderFallbackException)
                {
                    throw new ContentError("CMX is not UTF-8 text");
                }

                if (content.StartsWith('\uFEFF'))
                {
                    content = content[1..];
                }

                _lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                var versionIndex = Array.FindIndex(_lines, static l => l.StartsWith("version ", StringComparison.Ordinal));
                if (versionIndex < 0 || _lines[versionIndex] != "version 300")
                {
                    throw new ContentError("Unsupported CMX version");
                }
                _line = versionIndex + 1;
            }
            else
            {
                _isBinary = true;
                _binary = new BinaryCursor(data);
                _lines = [];
            }
        }

        private void Consume()
        {
            _operations++;
            if (_operations > 4_000_000)
            {
                throw new ContentError("Avatar container complexity limit exceeded");
            }
        }

        private string Token()
        {
            if (_number == _numbers.Length)
            {
                if (_line >= _lines.Length)
                {
                    throw new ContentError("Truncated CMX");
                }
                _numbers = PackContentPaths.TrimHorizontalWhitespace(_lines[_line]).Split(' ', StringSplitOptions.None);
                _line++;
                _number = 0;
            }

            if (_number >= _numbers.Length)
            {
                throw new ContentError("Missing CMX numeric value");
            }

            return _numbers[_number++];
        }

        public int Integer(bool isShort = false)
        {
            Consume();
            if (_isBinary)
            {
                if (isShort)
                {
                    var bytes = _binary.ReadBytes(2);
                    return unchecked((short)(bytes[0] | (bytes[1] << 8)));
                }
                return unchecked((int)_binary.ReadUInt32());
            }

            var tok = Token();
            if (isShort)
            {
                if (!short.TryParse(tok, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsedShort))
                {
                    throw new ContentError("Invalid CMX integer");
                }
                return parsedShort;
            }

            if (!int.TryParse(tok, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsedInt))
            {
                throw new ContentError("Invalid CMX integer");
            }
            return parsedInt;
        }

        public int Count(bool isShort = false, int limit = 100_000)
        {
            var value = Integer(isShort);
            if (value < 0 || value > limit)
            {
                throw new ContentError("Invalid avatar container count");
            }
            return value;
        }

        public void Floats(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Consume();
                float value;
                if (_isBinary)
                {
                    value = BitConverter.Int32BitsToSingle(unchecked((int)_binary.ReadUInt32()));
                }
                else
                {
                    var tok = Token();
                    const NumberStyles FloatStyles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
                    if (!float.TryParse(tok, FloatStyles, CultureInfo.InvariantCulture, out value))
                    {
                        throw new ContentError("Invalid CMX float");
                    }
                }

                if (!float.IsFinite(value))
                {
                    throw new ContentError("Non-finite avatar value");
                }
            }
        }

        public string ReadString()
        {
            Consume();
            string value;
            if (_isBinary)
            {
                int count = _binary.ReadBytes(1)[0];
                var bytes = _binary.ReadBytes(count);
                for (var i = 0; i < bytes.Length; i++)
                {
                    if (bytes[i] >= 0x80)
                    {
                        throw new ContentError("Invalid BCF string");
                    }
                }
                value = Encoding.ASCII.GetString(bytes);
            }
            else
            {
                if (_number != _numbers.Length || _line >= _lines.Length)
                {
                    throw new ContentError("Invalid CMX string position");
                }
                value = _lines[_line];
                _line++;
            }

            if (Encoding.UTF8.GetByteCount(value) > 4096 || value.Contains('\0'))
            {
                throw new ContentError("Invalid avatar string");
            }

            return value;
        }

        public void Properties(bool grouped)
        {
            var groups = grouped ? Count() : 1;
            for (var g = 0; g < groups; g++)
            {
                var propCount = Count();
                for (var p = 0; p < propCount; p++)
                {
                    _ = ReadString();
                    _ = ReadString();
                }
            }
        }

        public void Finish()
        {
            if (_isBinary)
            {
                foreach (var b in _binary.Remaining)
                {
                    if (b != 0)
                    {
                        throw new ContentError("Trailing BCF content");
                    }
                }
            }
            else
            {
                if (_number != _numbers.Length)
                {
                    throw new ContentError("Trailing CMX content");
                }
                for (var i = _line; i < _lines.Length; i++)
                {
                    if (!PackContentPaths.IsHorizontalWhitespaceOnly(_lines[i]))
                    {
                        throw new ContentError("Trailing CMX content");
                    }
                }
            }
        }
    }

    internal static List<PackAvatarResource> Read(byte[] data, string path, string owner, bool allowDuplicates = false)
    {
        var reader = new Reader(data, text: path.EndsWith(".cmx", StringComparison.Ordinal));
        var resources = new List<PackAvatarResource>();
        var identities = new HashSet<string>(StringComparer.Ordinal);

        void Insert(string kind, string name)
        {
            if (name.Length == 0)
            {
                throw new ContentError($"Empty avatar resource name in {path}");
            }

            var normalized = name.ToLowerInvariant();
            if (!identities.Add(kind + ":" + normalized) && !allowDuplicates)
            {
                throw new ContentError($"Duplicate avatar {kind} {normalized} in {path}");
            }

            resources.Add(new PackAvatarResource(kind, normalized, path, owner));
        }

        var skeletonCount = reader.Count();
        for (var s = 0; s < skeletonCount; s++)
        {
            Insert("skeleton", reader.ReadString());
            var count = reader.Count(isShort: true, limit: 32767);
            var bones = 0;
            while (bones < count)
            {
                var name = reader.ReadString();
                _ = reader.ReadString();
                if (name.Length == 0)
                {
                    continue;
                }

                reader.Properties(grouped: true);
                reader.Floats(7);
                for (var i = 0; i < 3; i++)
                {
                    _ = reader.Integer();
                }
                reader.Floats(2);
                bones++;
            }
        }

        var appearanceCount = reader.Count();
        for (var a = 0; a < appearanceCount; a++)
        {
            Insert("appearance", reader.ReadString());
            _ = reader.Integer();
            _ = reader.Integer();
            var bindingCount = reader.Count();
            for (var b = 0; b < bindingCount; b++)
            {
                _ = reader.ReadString();
                _ = reader.ReadString();
                _ = reader.Integer();
                _ = reader.Integer();
            }
        }

        var animationCount = reader.Count();
        for (var a = 0; a < animationCount; a++)
        {
            Insert("animation", reader.ReadString());
            _ = reader.ReadString();
            reader.Floats(2);
            _ = reader.Integer();
            _ = reader.Count(limit: 10_000_000);
            _ = reader.Count(limit: 10_000_000);
            var motionCount = reader.Count();
            for (var m = 0; m < motionCount; m++)
            {
                _ = reader.ReadString();
                _ = reader.Count(limit: 10_000_000);
                reader.Floats(1);
                for (var i = 0; i < 4; i++)
                {
                    _ = reader.Integer();
                }

                var motionPropCount = reader.Count();
                for (var p = 0; p < motionPropCount; p++)
                {
                    reader.Properties(grouped: false);
                }

                var timePropListCount = reader.Count();
                for (var t = 0; t < timePropListCount; t++)
                {
                    var timePropEntryCount = reader.Count();
                    for (var e = 0; e < timePropEntryCount; e++)
                    {
                        _ = reader.Integer();
                        reader.Properties(grouped: false);
                    }
                }
            }
        }

        reader.Finish();
        return resources;
    }
}

public sealed partial class PackStack
{
    internal static string? AvatarContainerName(string path)
    {
        var parts = path.Split("::", StringSplitOptions.None);
        var lastPart = parts[^1];
        var slashIdx = lastPart.LastIndexOf('/');
        var name = slashIdx >= 0 ? lastPart[(slashIdx + 1)..] : lastPart;
        if (name.Length == 0 ||
            (!name.EndsWith(".cmx", StringComparison.Ordinal) &&
             !name.EndsWith(".bcf", StringComparison.Ordinal) &&
             !name.EndsWith(".cfp", StringComparison.Ordinal)))
        {
            return null;
        }

        if (parts.Length == 2)
        {
            return name;
        }

        if (name.EndsWith(".cmx", StringComparison.Ordinal) &&
            (path.StartsWith("gamedata/skins/", StringComparison.Ordinal) ||
             path.StartsWith("expansionshared/skinsbuy/", StringComparison.Ordinal)))
        {
            return name;
        }

        return path.StartsWith("downloads/", StringComparison.Ordinal) ? name : null;
    }

    internal static (List<PackAvatarResource> Definitions, Dictionary<string, string> Containers) CollectAvatarResources(
        IReadOnlyDictionary<string, string> assets,
        IReadOnlyDictionary<string, string> owners)
    {
        var definitions = new List<PackAvatarResource>();
        var containers = new Dictionary<string, string>(StringComparer.Ordinal);
        var symbols = new Dictionary<string, PackAvatarResource>(StringComparer.Ordinal);

        void Collect(byte[] data, string path, string owner)
        {
            if (!path.EndsWith(".cmx", StringComparison.Ordinal) &&
                !path.EndsWith(".bcf", StringComparison.Ordinal) &&
                !path.EndsWith(".cfp", StringComparison.Ordinal))
            {
                return;
            }

            if (AvatarContainerName(path) is not { } name || name.Contains("globals", StringComparison.Ordinal))
            {
                throw new ContentError(
                    $"Avatar container {path} is outside the engine lookup. Put CMX under GameData/Skins, ExpansionShared/SkinsBuy or Downloads; put loose BCF/CFP under Downloads; avoid filenames containing globals");
            }

            if (containers.TryGetValue(name, out var previousContainer))
            {
                throw new ContentError(
                    $"Avatar container filename {name} conflicts: {previousContainer} and {path}. Use a unique filename or override the same resource path");
            }

            containers[name] = path;
            if (name.EndsWith(".cfp", StringComparison.Ordinal))
            {
                return;
            }

            foreach (var definition in AvatarContainer.Read(data, path, owner))
            {
                var symbol = definition.Kind + ":" + definition.Name;
                if (symbols.TryGetValue(symbol, out var previousSymbol))
                {
                    throw new ContentError(
                        $"Avatar {symbol} conflicts: {previousSymbol.Owner}:{previousSymbol.Path} and {owner}:{path}. Use a unique internal name or replace the same resource container");
                }

                symbols[symbol] = definition;
                definitions.Add(definition);
            }
        }

        foreach (var (path, source) in assets.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            if (path.EndsWith(".far", StringComparison.Ordinal))
            {
                var archive = new FARArchive(source);
                foreach (var entry in archive.Entries.Keys.OrderBy(static k => k, StringComparer.Ordinal))
                {
                    if (entry.EndsWith(".cmx", StringComparison.Ordinal) ||
                        entry.EndsWith(".bcf", StringComparison.Ordinal) ||
                        entry.EndsWith(".cfp", StringComparison.Ordinal))
                    {
                        Collect(archive.Read(entry), path + "::" + entry, owners[path]);
                    }
                }
            }
            else if (path.EndsWith(".cmx", StringComparison.Ordinal) ||
                     path.EndsWith(".bcf", StringComparison.Ordinal) ||
                     path.EndsWith(".cfp", StringComparison.Ordinal))
            {
                Collect(File.ReadAllBytes(source), path, owners[path]);
            }
        }

        definitions.Sort(static (a, b) =>
        {
            var cmp = string.CompareOrdinal(a.Kind, b.Kind);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Name, b.Name);
        });

        return (definitions, containers);
    }

    internal void ValidateBaseAvatarResources(string baseDir)
    {
        if (AvatarContainers.Count == 0)
        {
            return;
        }

        var canonicalBase = PackContentPaths.ResolveSymlinksInPath(baseDir);
        var basePrefix = PackContentPaths.TrimTrailingSeparator(canonicalBase);
        var slashPrefix = basePrefix.EndsWith('/') ? basePrefix : basePrefix + "/";

        var symbols = new Dictionary<string, PackAvatarResource>(AvatarResources.Count, StringComparer.Ordinal);
        foreach (var resource in AvatarResources)
        {
            symbols.Add(resource.Kind + ":" + resource.Name, resource);
        }

        var declarations = new Dictionary<string, HashSet<string>>(Ordered.Count, StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pack in Ordered)
        {
            declarations[pack.Manifest.Id] = new HashSet<string>(
                pack.Manifest.Overrides.Select(PackContentPaths.ResourceKey),
                StringComparer.Ordinal);
            foreach (var assetKey in pack.Assets.Keys)
            {
                owners[assetKey] = pack.Manifest.Id;
            }
        }

        void Check(byte[] data, string path)
        {
            if (AvatarContainerName(path) is not { } name || name.Contains("globals", StringComparison.Ordinal))
            {
                return;
            }

            if (AvatarContainers.TryGetValue(name, out var replacement))
            {
                var parts = replacement.Split("::", StringSplitOptions.None);
                if (parts.Length == 1 && declarations[owners[replacement]].Contains(replacement))
                {
                    if (!path.Contains("::", StringComparison.Ordinal) && replacement != path)
                    {
                        throw new ContentError(
                            $"Avatar container filename {name} conflicts with loose base path {path}. Replace the original loose path instead of {replacement}");
                    }
                    return;
                }

                throw new ContentError(
                    $"Avatar container filename {name} conflicts: base:{path} and {replacement}. Declare an explicit loose replacement with the same filename or replace the original FAR container");
            }

            if (name.EndsWith(".cfp", StringComparison.Ordinal) || symbols.Count == 0)
            {
                return;
            }

            foreach (var original in AvatarContainer.Read(data, path, "base", allowDuplicates: true))
            {
                var symbol = original.Kind + ":" + original.Name;
                if (symbols.TryGetValue(symbol, out var existingReplacement))
                {
                    throw new ContentError(
                        $"Avatar {symbol} conflicts: base:{path} and {existingReplacement.Owner}:{existingReplacement.Path}. Use a unique internal name or explicitly replace the original resource container");
                }
            }
        }

        if (!Directory.Exists(canonicalBase))
        {
            throw new ContentError("Cannot enumerate base avatar resources");
        }

        foreach (var file in PackContentPaths.EnumerateFilesRecursively(canonicalBase))
        {
            var resolved = PackContentPaths.ResolveSymlinksInPath(file);
            if (!PackContentPaths.IsUnderCanonicalRoot(resolved, canonicalBase))
            {
                throw new ContentError("Base avatar resource escapes game directory");
            }

            var path = PackContentPaths.ResourceKey(PackContentPaths.NormalizeSeparators(resolved)[slashPrefix.Length..]);
            if (Assets.ContainsKey(path))
            {
                continue;
            }

            if (path.EndsWith(".far", StringComparison.Ordinal))
            {
                var archive = new FARArchive(file);
                foreach (var entry in archive.Entries.Keys.OrderBy(static k => k, StringComparer.Ordinal))
                {
                    if (entry.EndsWith(".cmx", StringComparison.Ordinal) ||
                        entry.EndsWith(".bcf", StringComparison.Ordinal) ||
                        entry.EndsWith(".cfp", StringComparison.Ordinal))
                    {
                        Check(archive.Read(entry), path + "::" + entry);
                    }
                }
            }
            else if (AvatarContainerName(path) is not null)
            {
                Check(File.ReadAllBytes(file), path);
            }
        }
    }
}
