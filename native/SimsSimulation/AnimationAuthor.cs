using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimsEngine.Formats;

internal static class AnimationAuthor
{
    internal const int MaximumVectors = 1_000_000;
    internal const float MaximumEncodingError = 0.0032126708614f / 2;
    private static readonly float[] DeltaTable = BuildDeltaTable();

    internal sealed record Document
    {
        public int SchemaVersion { get; init; } = 1;
        public required string PackID { get; init; }
        public string? Name { get; init; }
        public string Version { get; init; } = "0.1.0";
        public string Encoding { get; init; } = "bcf";
        public string? Skeleton { get; init; }
        public required Clip[] Animations { get; init; }
    }
    internal sealed record Clip
    {
        public required string Name { get; init; }
        public required int Frames { get; init; }
        public bool Moving { get; init; }
        public float Distance { get; init; }
        public required Track[] Tracks { get; init; }
    }
    internal sealed record Track
    {
        public required string Bone { get; init; }
        public float[][]? Translations { get; init; }
        public float[][]? Rotations { get; init; }
        public Dictionary<string, string>[] Properties { get; init; } = [];
        public Event[] Events { get; init; } = [];
    }
    internal sealed record Event
    {
        public required int Time { get; init; }
        public required Dictionary<string, string> Properties { get; init; }
    }
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    internal static object Compile(string source, string destination, string? game = null)
    {
        if (new FileInfo(source).Length > 32 * 1024 * 1024) throw new InvalidDataException("Animation source exceeds size limit");
        var input = File.ReadAllBytes(source);
        var document = JsonSerializer.Deserialize<Document>(input, Json) ?? throw new InvalidDataException("Invalid animation document");
        return Compile(document, destination, game);
    }

    /// <summary>
    /// Compiles the document. With the installed game folder, every clip's bones are checked against its installed
    /// skeletons: against the named <c>skeleton</c> when the document gives one, otherwise each clip must fit at least one
    /// skeleton. A bone name the skeleton lacks would otherwise be skipped silently when the clip plays.
    /// </summary>
    internal static object Compile(Document document, string destination, string? game = null)
    {
        if (document.Skeleton != null && game == null)
            throw new InvalidDataException("Checking bones against skeleton " + document.Skeleton + " needs the installed game folder");
        var skeletons = game == null ? null : InstalledSkeletons(game);
        Skeleton? target = null;
        if (skeletons != null && document.Skeleton != null &&
            !skeletons.TryGetValue(document.Skeleton, out target))
            throw new InvalidDataException($"Unknown skeleton {document.Skeleton}; installed skeletons are {string.Join(", ", skeletons.Keys.Order(StringComparer.Ordinal))}");
        var fitted = new List<string?>();
        var output = Path.GetFullPath(destination);
        if (Directory.Exists(output) || File.Exists(output)) throw new InvalidDataException("Animation output already exists");
        if (document.SchemaVersion != 1 || !Identifier(document.PackID, false) || string.IsNullOrWhiteSpace(document.Version) ||
            (document.Name != null && string.IsNullOrWhiteSpace(document.Name)) ||
            document.Encoding is not ("bcf" or "cmx") || document.Animations == null || document.Animations.Length is < 1 or > 256)
            throw new InvalidDataException("Invalid animation pack specification");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var compiled = new List<(Animation Animation, byte[] Curves, int Frames)>();
        foreach (var clip in document.Animations)
        {
            if (clip == null || !Identifier(clip.Name, true) || !names.Add(clip.Name) || clip.Frames is < 1 or > 10000 ||
                !float.IsFinite(clip.Distance) || clip.Tracks == null || clip.Tracks.Length is < 1 or > 512)
                throw new InvalidDataException("Invalid or duplicate animation clip");
            var translations = new List<Vector3>();
            var rotations = new List<Quaternion>();
            var motions = new List<Motion>();
            var bones = new HashSet<string>(StringComparer.Ordinal);
            var duration = clip.Frames * 1000f / 30;
            foreach (var track in clip.Tracks)
            {
                if (track == null || !bones.Add(track.Bone) || string.IsNullOrWhiteSpace(track.Bone)) throw new InvalidDataException("Invalid or duplicate motion bone");
                ValidateString(track.Bone, document.Encoding);
                var motionProperties = (track.Properties ?? throw new InvalidDataException("Null motion properties"))
                    .SelectMany(properties => Properties(properties, document.Encoding)).ToArray();
                if (motionProperties.Length > 1024) throw new InvalidDataException("Invalid animation properties");
                var motionEvents = (track.Events ?? throw new InvalidDataException("Null animation events")).Select(item =>
                {
                    if (item == null || item.Time < 0 || item.Time >= duration) throw new InvalidDataException("Animation event is outside clip duration");
                    return new MotionEvent(item.Time, Properties(item.Properties, document.Encoding));
                }).ToArray();
                var motion = new Motion(
                    track.Bone,
                    clip.Frames,
                    duration,
                    [
                        track.Translations != null ? 1 : 0,
                        track.Rotations != null ? 1 : 0,
                        // -1 marks a missing channel, as in every shipped motion.
                        track.Translations != null ? translations.Count : -1,
                        track.Rotations != null ? rotations.Count : -1
                    ],
                    motionProperties,
                    motionEvents);
                if (track.Translations != null)
                {
                    ValidateSamples(track.Translations, clip.Frames, 3);
                    translations.AddRange(track.Translations.Select(value => new Vector3(value[0], value[1], value[2])));
                }
                if (track.Rotations != null)
                {
                    ValidateSamples(track.Rotations, clip.Frames, 4);
                    foreach (var value in track.Rotations)
                    {
                        var rotation = new Quaternion(value[0], value[1], value[2], value[3]);
                        var length = rotation.LengthSquared();
                        if (!float.IsFinite(length) || Math.Abs(length - 1) > 0.01f) throw new InvalidDataException("Animation rotations must be unit quaternions");
                        rotations.Add(rotation);
                    }
                }
                if (translations.Count > MaximumVectors || rotations.Count > MaximumVectors) throw new InvalidDataException("Animation vector limit exceeded");
                motions.Add(motion);
            }
            fitted.Add(skeletons == null ? null : FitSkeleton(clip, target, skeletons));
            var clipName = clip.Name.ToLowerInvariant();
            var animation = new Animation(
                clipName,
                clipName,
                [duration, clip.Distance],
                clip.Moving ? 1 : 0,
                translations.Count,
                rotations.Count,
                motions.ToArray());
            var curves = CompressCurves(translations, rotations);
            compiled.Add((animation, curves, clip.Frames));
        }
        var parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".animation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var resources = Path.Combine(staging, "files", "Downloads");
            Directory.CreateDirectory(resources);
            var metadata = document.PackID.ToLowerInvariant() + "." + document.Encoding;
            using (var stream = File.Create(Path.Combine(resources, metadata)))
                WriteCharacterContainer(stream, [], [], compiled.Select(item => item.Animation).ToArray(), document.Encoding == "cmx");
            foreach (var item in compiled) File.WriteAllBytes(Path.Combine(resources, item.Animation.CurveName + ".cfp"), item.Curves);
            var manifest = new
            {
                schemaVersion = 1, id = document.PackID, name = document.Name ?? document.PackID, version = document.Version,
                baseGameVersion = "1.90", dependencies = Array.Empty<object>(), overrides = Array.Empty<string>()
            };
            File.WriteAllText(Path.Combine(staging, "pack.json"), JsonSerializer.Serialize(manifest, Json));
            Directory.Move(staging, output);
            return new { pack = output, metadata = "Downloads/" + metadata, animations = compiled.Select((item, index) => new
            {
                name = item.Animation.Name, skeleton = fitted[index], frames = item.Frames, duration = item.Animation.Floats[0],
                translations = (uint)item.Animation.TranslationCount, rotations = (uint)item.Animation.RotationCount, curveBytes = item.Curves.Length
            }).ToArray(), maximumChannelEncodingError = MaximumEncodingError };
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    internal static void WriteCharacterContainer(Stream stream, IReadOnlyList<Skeleton> skeletons, IReadOnlyList<Appearance> appearances,
        IReadOnlyList<Animation> animations, bool cmx)
    {
        if (cmx)
        {
            var text = new StringBuilder();
            text.Append("// Character File. Copyright 1997, Maxis Inc.\nversion 300\n");
            void Int(int value) => text.Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
            void Float(float value) => text.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            void Str(string value) => text.Append(value).Append('\n');
            void Props(IReadOnlyList<PropertyPair> pairs)
            {
                Int(pairs.Count);
                foreach (var pair in pairs) { Str(pair.Name); Str(pair.Value); }
            }
            Int(skeletons.Count);
            foreach (var skeleton in skeletons)
            {
                Str(skeleton.Name);
                Int(skeleton.Bones.Count);
                foreach (var bone in skeleton.Bones)
                {
                    Str(bone.Name);
                    Str(bone.Parent);
                    if (bone.Properties.Count == 0) Int(0);
                    else { Int(1); Props(bone.Properties); }
                    Float(bone.Translation.X); Float(bone.Translation.Y); Float(bone.Translation.Z);
                    Float(bone.Rotation.X); Float(bone.Rotation.Y); Float(bone.Rotation.Z); Float(bone.Rotation.W);
                    Int(bone.Integers[0]); Int(bone.Integers[1]); Int(bone.Integers[2]);
                    Float(bone.Floats[0]); Float(bone.Floats[1]);
                }
            }
            Int(appearances.Count);
            foreach (var appearance in appearances)
            {
                Str(appearance.Name);
                Int(appearance.Integers.Length > 0 ? appearance.Integers[0] : 0);
                Int(appearance.Integers.Length > 1 ? appearance.Integers[1] : 0);
                Int(appearance.Bindings.Count);
                foreach (var binding in appearance.Bindings)
                {
                    Str(binding.Bone);
                    Str(binding.Mesh);
                    Int(binding.Integers.Length > 0 ? binding.Integers[0] : 0);
                    Int(binding.Integers.Length > 1 ? binding.Integers[1] : 0);
                }
            }
            Int(animations.Count);
            foreach (var animation in animations)
            {
                Str(animation.Name);
                Str(animation.CurveName);
                Float(animation.Floats.Length > 0 ? animation.Floats[0] : 0f);
                Float(animation.Floats.Length > 1 ? animation.Floats[1] : 0f);
                Int(animation.Integer);
                Int(animation.TranslationCount);
                Int(animation.RotationCount);
                Int(animation.Motions.Count);
                foreach (var motion in animation.Motions)
                {
                    Str(motion.Name);
                    Int(motion.FrameCount);
                    Float(motion.Float);
                    Int(motion.Integers[0]);
                    Int(motion.Integers[1]);
                    Int(motion.Integers[2]);
                    Int(motion.Integers[3]);
                    if (motion.Properties.Count == 0) Int(0);
                    else { Int(1); Props(motion.Properties); }
                    if (motion.Events.Count == 0) Int(0);
                    else
                    {
                        Int(1);
                        Int(motion.Events.Count);
                        foreach (var evt in motion.Events) { Int(evt.Time); Props(evt.Properties); }
                    }
                }
            }
            var bytes = new UTF8Encoding(false).GetBytes(text.ToString());
            stream.Write(bytes, 0, bytes.Length);
            return;
        }

        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        void WriteStr(string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            if (bytes.Length < 0xFF) writer.Write((byte)bytes.Length);
            else { writer.Write((byte)0xFF); writer.Write(bytes.Length); }
            writer.Write(bytes);
        }
        void WriteProps(IReadOnlyList<PropertyPair> pairs)
        {
            writer.Write(pairs.Count);
            foreach (var pair in pairs) { WriteStr(pair.Name); WriteStr(pair.Value); }
        }
        writer.Write(skeletons.Count);
        foreach (var skeleton in skeletons)
        {
            WriteStr(skeleton.Name);
            writer.Write(skeleton.Bones.Count);
            foreach (var bone in skeleton.Bones)
            {
                WriteStr(bone.Name);
                WriteStr(bone.Parent);
                if (bone.Properties.Count == 0) writer.Write(0);
                else { writer.Write(1); WriteProps(bone.Properties); }
                writer.Write(bone.Translation.X); writer.Write(bone.Translation.Y); writer.Write(bone.Translation.Z);
                writer.Write(bone.Rotation.X); writer.Write(bone.Rotation.Y); writer.Write(bone.Rotation.Z); writer.Write(bone.Rotation.W);
                writer.Write(bone.Integers[0]); writer.Write(bone.Integers[1]); writer.Write(bone.Integers[2]);
                writer.Write(bone.Floats[0]); writer.Write(bone.Floats[1]);
            }
        }
        writer.Write(appearances.Count);
        foreach (var appearance in appearances)
        {
            WriteStr(appearance.Name);
            writer.Write(appearance.Integers.Length > 0 ? appearance.Integers[0] : 0);
            writer.Write(appearance.Integers.Length > 1 ? appearance.Integers[1] : 0);
            writer.Write(appearance.Bindings.Count);
            foreach (var binding in appearance.Bindings)
            {
                WriteStr(binding.Bone);
                WriteStr(binding.Mesh);
                writer.Write(binding.Integers.Length > 0 ? binding.Integers[0] : 0);
                writer.Write(binding.Integers.Length > 1 ? binding.Integers[1] : 0);
            }
        }
        writer.Write(animations.Count);
        foreach (var animation in animations)
        {
            WriteStr(animation.Name);
            WriteStr(animation.CurveName);
            writer.Write(animation.Floats.Length > 0 ? animation.Floats[0] : 0f);
            writer.Write(animation.Floats.Length > 1 ? animation.Floats[1] : 0f);
            writer.Write(animation.Integer);
            writer.Write(animation.TranslationCount);
            writer.Write(animation.RotationCount);
            writer.Write(animation.Motions.Count);
            foreach (var motion in animation.Motions)
            {
                WriteStr(motion.Name);
                writer.Write(motion.FrameCount);
                writer.Write(motion.Float);
                writer.Write(motion.Integers[0]);
                writer.Write(motion.Integers[1]);
                writer.Write(motion.Integers[2]);
                writer.Write(motion.Integers[3]);
                if (motion.Properties.Count == 0) writer.Write(0);
                else { writer.Write(1); WriteProps(motion.Properties); }
                if (motion.Events.Count == 0) writer.Write(0);
                else
                {
                    writer.Write(1);
                    writer.Write(motion.Events.Count);
                    foreach (var evt in motion.Events) { writer.Write(evt.Time); WriteProps(evt.Properties); }
                }
            }
        }
    }

    private static float[] BuildDeltaTable()
    {
        const float step = 1f / 252f;
        var table = new float[253];
        for (var index = 0; index < table.Length; index++)
        {
            var x = (float)index * step + (float)index * step - 1f;
            var sign = x >= 0f ? 1f : -1f;
            table[index] = sign * x * x * x * x * 0.1f;
        }
        return table;
    }

    private static byte FindBestDelta(float diff)
    {
        if (!float.IsFinite(diff)) throw new ArgumentException("CFP delta must be finite");
        if (diff == 0f) return 126;
        byte closestDelta = 0;
        var closestDeltaDiff = float.PositiveInfinity;
        for (var index = 0; index < DeltaTable.Length; index++)
        {
            var difference = MathF.Abs(DeltaTable[index] - diff);
            if (difference < closestDeltaDiff)
            {
                closestDeltaDiff = difference;
                closestDelta = (byte)index;
            }
        }
        return closestDelta;
    }

    private static byte[] CompressCurves(IReadOnlyList<Vector3> translations, IReadOnlyList<Quaternion> rotations)
    {
        if (translations.Count > MaximumVectors || rotations.Count > MaximumVectors)
            throw new InvalidDataException("CFP vector limit exceeded");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var lastValue = 0f;
        void WriteChannel(int length, Func<int, float> sample, bool resetRun)
        {
            if (resetRun) lastValue = 0f;
            var repeats = 0;
            void Flush()
            {
                if (repeats == 1) writer.Write((byte)126);
                else if (repeats > 1)
                {
                    writer.Write((byte)0xFE);
                    writer.Write((ushort)(repeats - 1));
                }
                repeats = 0;
            }
            for (var index = 0; index < length; index++)
            {
                var value = sample(index);
                if (!float.IsFinite(value)) throw new InvalidDataException("CFP value must be finite");
                if (index == 0 && !resetRun && lastValue != 0f)
                {
                    writer.Write((byte)0xFF);
                    writer.Write(value);
                    lastValue = value;
                    continue;
                }
                var difference = value - lastValue;
                var finite = float.IsFinite(difference);
                var code = finite ? FindBestDelta(difference) : (byte)0xFF;
                if (code == 126)
                {
                    repeats++;
                    if (repeats == 65536) Flush();
                    continue;
                }
                Flush();
                if (!finite || MathF.Abs(difference - DeltaTable[code]) > MaximumEncodingError)
                {
                    writer.Write((byte)0xFF);
                    writer.Write(value);
                    lastValue = value;
                }
                else
                {
                    writer.Write(code);
                    lastValue += DeltaTable[code];
                }
            }
            Flush();
        }
        WriteChannel(translations.Count, index => -translations[index].X, true);
        WriteChannel(translations.Count, index => translations[index].Y, false);
        WriteChannel(translations.Count, index => translations[index].Z, false);
        WriteChannel(rotations.Count, index => rotations[index].X, true);
        WriteChannel(rotations.Count, index => -rotations[index].Y, false);
        WriteChannel(rotations.Count, index => -rotations[index].Z, false);
        WriteChannel(rotations.Count, index => -rotations[index].W, false);
        return stream.ToArray();
    }

    private static bool Identifier(string value, bool underscore) => value != null && value.Length is >= 1 and <= 120 && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' || (underscore && character == '_')) && value is not ("." or "..");

    private static void ValidateSamples(float[][] samples, int frames, int dimensions)
    {
        if (samples.Length != frames || samples.Any(value => value == null || value.Length != dimensions || value.Any(number => !float.IsFinite(number))))
            throw new InvalidDataException("Animation sample dimensions or frame count differ");
    }

    private static void ValidateString(string value, string encoding)
    {
        if (value == null || value.Contains('\n') || value.Contains('\r') || value.Contains('\0') || value.Length > 4096 ||
            (encoding == "bcf" && (value.Length > 255 || value.Any(character => character > 127))))
            throw new InvalidDataException("Animation string cannot be represented by its container encoding");
    }

    private static PropertyPair[] Properties(Dictionary<string, string> values, string encoding)
    {
        if (values == null || values.Count > 1024) throw new InvalidDataException("Invalid animation properties");
        foreach (var pair in values) { ValidateString(pair.Key, encoding); ValidateString(pair.Value, encoding); }
        return values.Select(pair => new PropertyPair(pair.Key, pair.Value)).ToArray();
    }

    private static string FitSkeleton(Clip clip, Skeleton? target, Dictionary<string, Skeleton> skeletons)
    {
        static string[] Missing(Clip clip, Skeleton skeleton) =>
            clip.Tracks.Select(track => track.Bone).Where(bone => !skeleton.Bones.Any(item => item.Name == bone)).ToArray();
        if (target != null)
        {
            var missing = Missing(clip, target);
            if (missing.Length > 0)
                throw new InvalidDataException($"Clip {clip.Name} animates bones that skeleton {target.Name} lacks: {string.Join(", ", missing)}");
            return target.Name;
        }
        var fit = skeletons.Values.OrderBy(skeleton => skeleton.Name, StringComparer.Ordinal).FirstOrDefault(skeleton => Missing(clip, skeleton).Length == 0);
        if (fit != null) return fit.Name;
        var unknown = clip.Tracks.Select(track => track.Bone).Where(bone => !skeletons.Values.Any(skeleton => skeleton.Bones.Any(item => item.Name == bone))).Distinct().ToArray();
        throw new InvalidDataException(unknown.Length > 0
            ? $"Clip {clip.Name} animates bones that no installed skeleton has: {string.Join(", ", unknown)}"
            : $"Clip {clip.Name} animates bones from more than one skeleton; name its skeleton to see which bones it lacks");
    }

    /// <summary>The skeletons defined by the installed BCF and CMX files, loose or in FAR archives, by case-insensitive name.</summary>
    internal static Dictionary<string, Skeleton> InstalledSkeletons(string game)
    {
        var skeletons = new Dictionary<string, Skeleton>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, byte[] data)
        {
            var cmx = name.EndsWith(".cmx", StringComparison.OrdinalIgnoreCase);
            if (!cmx && !name.EndsWith(".bcf", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                var parsed = cmx ? CharacterData.ReadText(data, name) : CharacterData.ReadBinary(data, name);
                foreach (var skeleton in parsed.Skeletons)
                    if (!string.IsNullOrEmpty(skeleton.Name)) skeletons.TryAdd(skeleton.Name, skeleton);
            }
            catch (Exception error) when (error is ResourceFormatException or IOException or FormatException or ArgumentException or IndexOutOfRangeException or OverflowException or InvalidDataException)
            {
                // Files that are not readable animation containers define no skeletons.
            }
        }
        InstalledFiles.Visit(game, name => name.EndsWith(".cmx", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bcf", StringComparison.OrdinalIgnoreCase),
            entry => Add(entry.Name, entry.Read()));
        if (skeletons.Count == 0) throw new InvalidDataException($"No skeletons are installed under {game}");
        return skeletons;
    }
}
