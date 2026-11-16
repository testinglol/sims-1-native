using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace SimsSDL.Core;

public sealed record GameDialog
{
    public sealed record BookAvailabilityEntry
    {
        [JsonConstructor]
        public BookAvailabilityEntry() { }

        [SetsRequiredMembers]
        public BookAvailabilityEntry(int contextItemId, int itemId, bool made)
        {
            ContextItemId = contextItemId;
            ItemId = itemId;
            Made = made;
        }

        [JsonPropertyName("contextItemId")]
        public required int ContextItemId { get; init; }

        [JsonPropertyName("itemId")]
        public required int ItemId { get; init; }

        [JsonPropertyName("made")]
        public required bool Made { get; init; }
    }

    public sealed record PhoneBookFamily
    {
        [JsonConstructor]
        public PhoneBookFamily() { }

        [SetsRequiredMembers]
        public PhoneBookFamily(int familyId, string name, IReadOnlyList<PhoneBookContact> contacts)
        {
            FamilyId = familyId;
            Name = name;
            Contacts = contacts;
        }

        [JsonPropertyName("familyId")]
        public required int FamilyId { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("contacts")]
        public required IReadOnlyList<PhoneBookContact> Contacts { get; init; }
    }

    public sealed record PhoneBookContact
    {
        [JsonConstructor]
        public PhoneBookContact() { }

        [SetsRequiredMembers]
        public PhoneBookContact(int neighborId, string name, GameplayPanelState.Image? portrait = null)
        {
            NeighborId = neighborId;
            Name = name;
            Portrait = portrait;
        }

        [JsonPropertyName("neighborId")]
        public required int NeighborId { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("portrait")]
        public GameplayPanelState.Image? Portrait { get; init; }
    }

    public readonly record struct Button(string Label, byte Code);

    [JsonConstructor]
    public GameDialog() { }

    [JsonPropertyName("token")]
    public required string Token { get; init; }

    [JsonPropertyName("blocking")]
    public required bool Blocking { get; init; }

    [JsonPropertyName("caller")]
    public required int Caller { get; init; }

    [JsonPropertyName("tutorial")]
    public bool? Tutorial { get; init; }

    [JsonPropertyName("icon")]
    public required int Icon { get; init; }

    [JsonPropertyName("kind")]
    public required byte Kind { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("yes")]
    public string? Yes { get; init; }

    [JsonPropertyName("no")]
    public string? No { get; init; }

    [JsonPropertyName("cancel")]
    public string? Cancel { get; init; }

    [JsonPropertyName("image")]
    public GameplayPanelState.Image? Image { get; init; }

    [JsonPropertyName("phoneBook")]
    public IReadOnlyList<PhoneBookFamily>? PhoneBook { get; init; }

    [JsonPropertyName("bookMode")]
    public byte? BookMode { get; init; }

    [JsonPropertyName("bookAvailability")]
    public IReadOnlyList<BookAvailabilityEntry>? BookAvailability { get; init; }

    [JsonPropertyName("hideBookItem9")]
    public bool? HideBookItem9 { get; init; }

    [JsonPropertyName("hideBookItem4")]
    public bool? HideBookItem4 { get; init; }

    /// <summary>Identifies Spellbook (12) and Cookbook (15) screens, which are distinguished from generic
    /// dialogs by active book mode settings.</summary>
    [JsonIgnore]
    public bool IsBookScreen => (Kind == 12 || Kind == 15) && BookMode is not null;

    [JsonIgnore]
    public IReadOnlyList<Button> Buttons => Kind switch
    {
        3 => [new Button(Yes ?? "OK", 0)],
        0 or 4 => Blocking ? [new Button(Yes ?? "OK", 0)] : [],
        1 => [new Button(Yes ?? "Yes", 0), new Button(No ?? "No", 1)],
        2 => [new Button(Yes ?? "Yes", 0), new Button(No ?? "No", 1), new Button(Cancel ?? "Cancel", 2)],
        10 => [new Button(Yes ?? "Call", 1), new Button(No ?? "Cancel", 0)],
        // Fallback cancel button for wardrobe (6) and pet selection (9) dialogs.
        6 or 9 => [new Button(Cancel ?? No ?? "Cancel", 0)],
        // Transform dialog (14) returns code 0 to accept or code 1 to decline.
        14 => [new Button(Yes ?? "Yes", 0), new Button(No ?? "No", 1)],
        // Spellbook (12) and Cookbook (15) screens return code 0 when closed.
        12 or 15 => (IsBookScreen || Blocking) ? [new Button(Yes ?? "OK", 0)] : [],
        >= 16 => Blocking ? [new Button(Yes ?? "OK", 0)] : [],
        _ => []
    };

    public static GameDialog FromJson(string json) =>
        JsonSerializer.Deserialize<GameDialog>(json) ?? throw new ContentError("Game dialog JSON decoded to null");

    public static GameDialog LocalFeedback(string token, string title, string message, string ok = "OK") =>
        LocalPrompt(token, title, message, kind: 0, yes: ok, no: null, cancel: null);

    public static GameDialog LocalPrompt(
        string token,
        string title,
        string message,
        byte kind,
        string? yes,
        string? no,
        string? cancel) =>
        new()
        {
            Token = token,
            Blocking = true,
            Caller = 0,
            Tutorial = false,
            Icon = 0,
            Kind = kind,
            Title = title,
            Message = message,
            Yes = yes,
            No = no,
            Cancel = cancel,
            Image = null,
            PhoneBook = null,
            BookMode = null,
            BookAvailability = null,
            HideBookItem9 = null,
            HideBookItem4 = null
        };

    public SimsBitmap? ReadImage(string? directory) => ReadBitmap(Image, directory);

    public static SimsBitmap? ReadBitmap(GameplayPanelState.Image? image, string? directory)
    {
        if (image is null)
            return null;

        image.Validate();
        if (string.IsNullOrEmpty(directory))
            throw new ContentError("Missing dialog image directory");

        string root = LotScene.ResolveRealPath(directory);
        string file = LotScene.ResolveRealPath(Path.Combine(root, image.File));
        string? parent = Path.GetDirectoryName(file)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(parent) && file.StartsWith(Path.DirectorySeparatorChar))
            parent = Path.DirectorySeparatorChar.ToString();

        if (!string.Equals(parent, root, StringComparison.Ordinal))
            throw new ContentError("Invalid dialog image file");

        var info = new FileInfo(file);
        if (!info.Exists)
            throw new FileNotFoundException("Dialog image file not found", file);
        if (info.Length > 4 * 1024 * 1024)
            throw new ContentError("Invalid dialog image file");

        var bitmap = new SimsBitmap(File.ReadAllBytes(file));
        if (bitmap.Width > 512 || bitmap.Height > 512)
            throw new ContentError("Oversized dialog image");

        return bitmap;
    }

    public void Validate()
    {
        if (string.IsNullOrEmpty(Token) || Token.Length > 64
            || Title is null || Title.Length > 512
            || Message is null || Message.Length > 8192
            || Title.Contains('\0') || Message.Contains('\0')
            || Buttons.Any(b => b.Label.Length > 512))
        {
            throw new ContentError("Invalid game dialog");
        }

        Image?.Validate();
        if (Image is { } image)
        {
            if (image.Columns is < 1 or > 5 || image.Rows is < 1 or > 4)
                throw new ContentError("Invalid dialog image sheet");
        }

        if (Kind == 10)
        {
            if (PhoneBook is null || PhoneBook.Count > 512)
                throw new ContentError("Invalid phone-book dialog");

            var neighborIDs = new HashSet<int>();
            foreach (var family in PhoneBook)
            {
                if (family is null || family.FamilyId is < 1 or > 65535
                    || family.Name is null || family.Name.Length > 512 || family.Name.Contains('\0')
                    || family.Contacts is null || family.Contacts.Count > 512)
                {
                    throw new ContentError("Invalid phone-book family");
                }

                foreach (var contact in family.Contacts)
                {
                    if (contact is null || contact.NeighborId is < 1 or > 32767
                        || contact.Name is null || contact.Name.Length > 512 || contact.Name.Contains('\0')
                        || !neighborIDs.Add(contact.NeighborId))
                    {
                        throw new ContentError("Invalid phone-book contact");
                    }

                    contact.Portrait?.Validate();
                }
            }
        }
        else if (PhoneBook is not null)
        {
            throw new ContentError("Unexpected phone-book dialog data");
        }
    }
}

/// <summary>In <c>Live.iff</c>, each personality trait uses a seven-entry string layout containing a label,
/// three rating names, and three tier descriptions following the panel title.</summary>
public static class PersonalityPopup
{
    public static int? Selection(int? current, int clicked)
    {
        if (clicked is < 0 or >= 5)
            return current;
        return current == clicked ? null : clicked;
    }

    public const int Width = 559;
    public const int MinimumHeight = 127;
    public static readonly CGRect PortraitPane = new(0, 0, 168, 127);
    public const int TextLeft = 178;
    public const int TextRight = 545;

    public readonly record struct TraitText(string Name, string Description);

    public static TraitText? Text(int trait, int value, IReadOnlyList<string> strings)
    {
        if (trait is < 0 or >= 5 || strings is null)
            return null;

        int points = value / 100;
        int category = points < 3 ? 0 : points > 7 ? 2 : 1;
        int start = 1 + trait * 7;
        int nameIndex = start + 1 + category;
        int descriptionIndex = start + 4 + category;
        if (descriptionIndex < 0 || descriptionIndex >= strings.Count || nameIndex < 0 || nameIndex >= strings.Count)
            return null;

        return new TraitText(strings[nameIndex], strings[descriptionIndex]);
    }
}
