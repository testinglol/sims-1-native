using System.Text.Json.Serialization;

namespace SimsSDL.Core;

public sealed class HouseholdCreationRequest : IEquatable<HouseholdCreationRequest>
{
    public enum TextField
    {
        FamilyName,
        FirstName,
        Biography,
    }

    private static readonly HashSet<Rune> FamilyNameFilteredCharacters =
    [
        new Rune('\\'), new Rune('/'), new Rune('|'), new Rune('*'), new Rune('?'),
        new Rune(':'), new Rune('<'), new Rune('>'), new Rune('"'), new Rune('\''),
        new Rune('%'), new Rune('('), new Rune(')'), new Rune('&'), new Rune(';'),
        new Rune('@'), new Rune('!'), new Rune('#'), new Rune(','), new Rune('.'),
    ];

    public static bool AllowsFamilyNameInput(string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (FamilyNameFilteredCharacters.Contains(rune))
            {
                return false;
            }
        }

        return true;
    }

    public static bool CanInsert(string inserted, string current, TextField field)
    {
        if (string.IsNullOrEmpty(inserted))
        {
            return false;
        }

        int limit;
        switch (field)
        {
            case TextField.FamilyName:
                limit = 24;
                if (!AllowsFamilyNameInput(inserted))
                {
                    return false;
                }

                break;
            case TextField.FirstName:
                limit = 25;
                break;
            case TextField.Biography:
                limit = 2048;
                break;
            default:
                return false;
        }

        foreach (Rune rune in inserted.EnumerateRunes())
        {
            if (Rune.IsControl(rune) && !(field == TextField.Biography && rune.Value == '\n'))
            {
                return false;
            }
        }

        return Encoding.UTF8.GetByteCount(current) + Encoding.UTF8.GetByteCount(inserted) <= limit;
    }

    public sealed class Member : IEquatable<Member>
    {
        [JsonPropertyName("name")]
        public string Name { get; }

        [JsonPropertyName("bio")]
        public string Bio { get; }

        [JsonPropertyName("code")]
        public string Code { get; }

        [JsonPropertyName("skin")]
        public string Skin { get; }

        [JsonPropertyName("personality")]
        public IReadOnlyList<int> Personality { get; }

        [JsonPropertyName("zodiac")]
        public int? Zodiac { get; }

        [JsonPropertyName("headChoice")]
        public string? HeadChoice { get; }

        [JsonPropertyName("bodyChoice")]
        public string? BodyChoice { get; }

        [JsonConstructor]
        public Member(
            string name,
            string bio,
            string code,
            string skin,
            IReadOnlyList<int> personality,
            string? headChoice = null,
            string? bodyChoice = null,
            int? zodiac = null)
        {
            Name = name ?? "";
            Bio = bio ?? "";
            Code = code ?? "";
            Skin = skin ?? "";
            Personality = personality is null ? Array.Empty<int>() : personality.ToArray();
            HeadChoice = headChoice;
            BodyChoice = bodyChoice;
            Zodiac = zodiac;
        }

        public Member(
            string name,
            string code,
            string skin,
            IReadOnlyList<int> personality,
            string? headChoice = null,
            string? bodyChoice = null,
            int? zodiac = null)
            : this(name, "", code, skin, personality, headChoice, bodyChoice, zodiac)
        {
        }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name) ||
                Encoding.UTF8.GetByteCount(Name) > 25 ||
                ContainsControlRune(Name, allowNewline: false) ||
                Encoding.UTF8.GetByteCount(Bio) > 2048 ||
                ContainsControlRune(Bio, allowNewline: true) ||
                !IsValidCode(Code) ||
                Skin is not ("lgt" or "med" or "drk") ||
                !IsValidChoice(HeadChoice) ||
                !IsValidChoice(BodyChoice) ||
                Personality.Count != 5 ||
                !IsValidPersonality(Personality) ||
                (Zodiac is { } z && (z < 1 || z > 12)))
            {
                throw new ContentError("Invalid household member data");
            }
        }

        private static bool ContainsControlRune(string value, bool allowNewline)
        {
            foreach (Rune rune in value.EnumerateRunes())
            {
                if (Rune.IsControl(rune) && !(allowNewline && rune.Value == '\n'))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsValidCode(string code)
        {
            if (code.Length != 5 || code[0] is not ('m' or 'f') || code[1] is not ('a' or 'c'))
            {
                return false;
            }

            for (int i = 2; i < 5; i++)
            {
                if (code[i] < 'a' || code[i] > 'z')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidChoice(string? choice) =>
            choice is null ||
            (choice.Length > 0 &&
             Encoding.UTF8.GetByteCount(choice) <= 128 &&
             !ContainsControlRune(choice, allowNewline: false));

        private static bool IsValidPersonality(IReadOnlyList<int> personality)
        {
            int sum = 0;
            for (int i = 0; i < personality.Count; i++)
            {
                int value = personality[i];
                if (value < 0 || value > 1000 || value % 100 != 0)
                {
                    return false;
                }

                sum += value;
            }

            return sum <= 2500;
        }

        public bool Equals(Member? other) =>
            other is not null &&
            Name == other.Name &&
            Bio == other.Bio &&
            Code == other.Code &&
            Skin == other.Skin &&
            Zodiac == other.Zodiac &&
            HeadChoice == other.HeadChoice &&
            BodyChoice == other.BodyChoice &&
            Personality.SequenceEqual(other.Personality);

        public override bool Equals(object? obj) => Equals(obj as Member);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Name);
            hash.Add(Bio);
            hash.Add(Code);
            hash.Add(Skin);
            hash.Add(Zodiac);
            hash.Add(HeadChoice);
            hash.Add(BodyChoice);
            foreach (int trait in Personality)
            {
                hash.Add(trait);
            }

            return hash.ToHashCode();
        }

        public static bool operator ==(Member? left, Member? right) => Equals(left, right);
        public static bool operator !=(Member? left, Member? right) => !Equals(left, right);
    }

    [JsonPropertyName("familyName")]
    public string FamilyName { get; }

    [JsonPropertyName("members")]
    public IReadOnlyList<Member> Members { get; }

    [JsonConstructor]
    public HouseholdCreationRequest(string familyName, IReadOnlyList<Member> members)
    {
        FamilyName = familyName ?? "";
        Members = members is null ? Array.Empty<Member>() : members.ToArray();
    }

    public void Validate()
    {
        bool hasControl = false;
        foreach (Rune rune in FamilyName.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                hasControl = true;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(FamilyName) ||
            Encoding.UTF8.GetByteCount(FamilyName) > 24 ||
            !AllowsFamilyNameInput(FamilyName) ||
            hasControl ||
            Members.Count < 1 || Members.Count > 8)
        {
            throw new ContentError("Invalid household name or member count");
        }

        bool hasAdult = false;
        foreach (Member member in Members)
        {
            member.Validate();
            if (member.Code.Length >= 2 && member.Code[1] == 'a')
            {
                hasAdult = true;
            }
        }

        if (!hasAdult)
        {
            throw new ContentError("A household must contain at least one adult Sim");
        }
    }

    public bool Equals(HouseholdCreationRequest? other) =>
        other is not null &&
        FamilyName == other.FamilyName &&
        Members.SequenceEqual(other.Members);

    public override bool Equals(object? obj) => Equals(obj as HouseholdCreationRequest);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(FamilyName);
        foreach (Member member in Members)
        {
            hash.Add(member);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(HouseholdCreationRequest? left, HouseholdCreationRequest? right) => Equals(left, right);
    public static bool operator !=(HouseholdCreationRequest? left, HouseholdCreationRequest? right) => !Equals(left, right);
}

public sealed class HouseholdAppearanceCatalog : IEquatable<HouseholdAppearanceCatalog>
{
    public sealed record Choice
    {
        [JsonPropertyName("id")]
        public string Id { get; }

        [JsonPropertyName("texture")]
        public string Texture { get; }

        [JsonPropertyName("bodyType")]
        public string BodyType { get; }

        [JsonConstructor]
        public Choice(string id, string texture, string? bodyType = "")
        {
            Id = id ?? "";
            Texture = texture ?? "";
            BodyType = bodyType ?? "";
        }
    }

    [JsonPropertyName("gender")]
    public string Gender { get; }

    [JsonPropertyName("age")]
    public string Age { get; }

    [JsonPropertyName("skin")]
    public string Skin { get; }

    [JsonPropertyName("heads")]
    public IReadOnlyList<Choice> Heads { get; }

    [JsonPropertyName("bodies")]
    public IReadOnlyList<Choice> Bodies { get; }

    [JsonConstructor]
    public HouseholdAppearanceCatalog(
        string gender,
        string age,
        string skin,
        IReadOnlyList<Choice> heads,
        IReadOnlyList<Choice> bodies)
    {
        Gender = gender ?? "";
        Age = age ?? "";
        Skin = skin ?? "";
        Heads = heads is null ? Array.Empty<Choice>() : heads.ToArray();
        Bodies = bodies is null ? Array.Empty<Choice>() : bodies.ToArray();
    }

    public bool Equals(HouseholdAppearanceCatalog? other) =>
        other is not null &&
        Gender == other.Gender &&
        Age == other.Age &&
        Skin == other.Skin &&
        Heads.SequenceEqual(other.Heads) &&
        Bodies.SequenceEqual(other.Bodies);

    public override bool Equals(object? obj) => Equals(obj as HouseholdAppearanceCatalog);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Gender);
        hash.Add(Age);
        hash.Add(Skin);
        foreach (Choice head in Heads)
        {
            hash.Add(head);
        }

        foreach (Choice body in Bodies)
        {
            hash.Add(body);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(HouseholdAppearanceCatalog? left, HouseholdAppearanceCatalog? right) => Equals(left, right);
    public static bool operator !=(HouseholdAppearanceCatalog? left, HouseholdAppearanceCatalog? right) => !Equals(left, right);
}

/// <summary>
/// The original character editor retains the selected suit indices for each gender/age pair. Skin changes reuse
/// the same pair, while age or gender changes load that pair's prior indices.
/// </summary>
public sealed class HouseholdAppearanceSelectionCache : IEquatable<HouseholdAppearanceSelectionCache>
{
    public record struct Indices(int Head, int Body);

    private readonly Dictionary<string, Indices> _values = [];

    public Indices? Selection(string gender, string age) =>
        _values.TryGetValue(Key(gender, age), out Indices indices) ? indices : null;

    public void Remember(Indices indices, string gender, string age) =>
        _values[Key(gender, age)] = indices;

    /// <summary>
    /// Mirrors <c>cWinDesignCharacter::SanifySuits</c>: negative indices become zero
    /// and oversized indices become last.
    /// </summary>
    public static int Sanitized(int index, int count) =>
        count > 0 ? Math.Min(Math.Max(0, index), count - 1) : -1;

    private static string Key(string gender, string age) => gender + "/" + age;

    public bool Equals(HouseholdAppearanceSelectionCache? other)
    {
        if (other is null || _values.Count != other._values.Count)
        {
            return false;
        }

        foreach ((string key, Indices value) in _values)
        {
            if (!other._values.TryGetValue(key, out Indices otherValue) || value != otherValue)
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as HouseholdAppearanceSelectionCache);

    public override int GetHashCode()
    {
        int hash = _values.Count;
        foreach ((string key, Indices value) in _values)
        {
            hash ^= HashCode.Combine(key, value);
        }

        return hash;
    }

    public static bool operator ==(HouseholdAppearanceSelectionCache? left, HouseholdAppearanceSelectionCache? right) =>
        Equals(left, right);

    public static bool operator !=(HouseholdAppearanceSelectionCache? left, HouseholdAppearanceSelectionCache? right) =>
        !Equals(left, right);
}

public static class HouseholdPersonalityEditor
{
    /// <summary>
    /// Applies one widget click: one 100-point step, with no redistribution to other traits.
    /// </summary>
    public static int[]? Step(IReadOnlyList<int> values, int trait, bool increasing)
    {
        if (values.Count != 5 || trait < 0 || trait >= values.Count)
        {
            return null;
        }

        int total = 0;
        for (int i = 0; i < values.Count; i++)
        {
            int v = values[i];
            if (v < 0 || v > 1000 || v % 100 != 0)
            {
                return null;
            }

            total += v;
        }

        if (total > 2500)
        {
            return null;
        }

        int[] result = [.. values];
        if (increasing)
        {
            if (result[trait] >= 1000 || total >= 2500)
            {
                return null;
            }

            result[trait] += 100;
        }
        else
        {
            if (result[trait] <= 0)
            {
                return null;
            }

            result[trait] -= 100;
        }

        return result;
    }
}

public sealed class HouseholdAppearancePreview
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; }

    [JsonPropertyName("width")]
    public float Width { get; }

    [JsonPropertyName("height")]
    public float Height { get; }

    [JsonPropertyName("textures")]
    public List<LotScene.Texture> Textures { get; }

    [JsonPropertyName("meshes")]
    public List<LotScene.Mesh> Meshes { get; }

    [JsonConstructor]
    public HouseholdAppearancePreview(
        int schemaVersion,
        float width,
        float height,
        List<LotScene.Texture> textures,
        List<LotScene.Mesh> meshes)
    {
        SchemaVersion = schemaVersion;
        Width = width;
        Height = height;
        Textures = textures ?? new List<LotScene.Texture>();
        Meshes = meshes ?? new List<LotScene.Mesh>();
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || Width != 1f || Height != 1f ||
            Textures.Count < 1 || Textures.Count > 32 ||
            Meshes.Count < 1 || Meshes.Count > 32)
        {
            throw new ContentError("Invalid household appearance preview header");
        }

        long textureBudget = 128L * 1024L * 1024L;
        foreach (LotScene.Texture texture in Textures)
        {
            if (!IsValidTexture(texture))
            {
                throw new ContentError("Invalid household preview texture");
            }

            textureBudget -= (long)texture.Width * texture.Height * 4;
            if (textureBudget < 0)
            {
                throw new ContentError("Household preview textures exceed the memory limit");
            }
        }

        long vertexCount = 0;
        foreach (LotScene.Mesh mesh in Meshes)
        {
            IReadOnlyList<LotScene.MeshVertex> vertices = mesh.Vertices;
            vertexCount += vertices.Count;
            if (mesh.Texture < 0 || mesh.Texture >= Textures.Count ||
                mesh.ObjectID != 0 ||
                !float.IsFinite(mesh.Depth) ||
                mesh.Layer != 1 ||
                mesh.Roof is not null ||
                mesh.Floor is not null ||
                mesh.Unlit == true ||
                vertices.Count == 0 ||
                vertices.Count % 3 != 0 ||
                vertexCount > 250_000)
            {
                throw new ContentError("Invalid household preview mesh");
            }

            foreach (LotScene.MeshVertex vertex in vertices)
            {
                if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y) || !float.IsFinite(vertex.Z) ||
                    !float.IsFinite(vertex.U) || !float.IsFinite(vertex.V) ||
                    !float.IsFinite(vertex.NX) || !float.IsFinite(vertex.NY) || !float.IsFinite(vertex.NZ) ||
                    !(vertex.X >= 0f && vertex.X <= 1f) ||
                    !(vertex.Y >= 0f && vertex.Y <= 1f) ||
                    !(vertex.Z >= 0f && vertex.Z <= 1f) ||
                    MathF.Abs(vertex.U) > 16f || MathF.Abs(vertex.V) > 16f ||
                    MathF.Abs(vertex.NX) > 1.01f || MathF.Abs(vertex.NY) > 1.01f || MathF.Abs(vertex.NZ) > 1.01f)
                {
                    throw new ContentError("Invalid household preview vertex");
                }
            }
        }
    }

    public static (HouseholdAppearancePreview Preview, List<byte[]> Pixels) Load(string path)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Household appearance preview file not found.", path);
        }

        if (fileInfo.Length > 16L * 1024L * 1024L)
        {
            throw new ContentError("Household appearance preview exceeds 16 MiB");
        }

        byte[] jsonBytes = File.ReadAllBytes(path);
        HouseholdAppearancePreview preview = JsonSerializer.Deserialize<HouseholdAppearancePreview>(jsonBytes)
            ?? throw new ContentError("Invalid household appearance preview header");
        preview.Validate();

        string fullPath = Path.GetFullPath(path);
        string parentDir = Path.GetDirectoryName(fullPath) ?? ".";
        string root = Directory.ResolveLinkTarget(parentDir, returnFinalTarget: true)?.FullName ?? parentDir;
        root = Path.GetFullPath(root);

        List<byte[]> pixels = [.. LotScene.ReadPixels(preview.Textures, root)];
        return (preview, pixels);
    }

    private static bool IsValidTexture(LotScene.Texture texture)
    {
        if (string.IsNullOrEmpty(texture.File) ||
            texture.File.StartsWith('/') ||
            texture.File.Contains('\\') ||
            texture.File.Length > 1024)
        {
            return false;
        }

        foreach (Rune rune in texture.File.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                return false;
            }
        }

        string[] components = texture.File.Split('/');
        if (components.Length == 0)
        {
            return false;
        }

        foreach (string component in components)
        {
            if (component.Length == 0 || component == "." || component == "..")
            {
                return false;
            }
        }

        return texture.Width > 0 && texture.Height > 0 &&
               texture.Width <= 4096 && texture.Height <= 4096 &&
               texture.Encoding is "bmp" or "tga";
    }
}
