using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;

/// <summary>Provides purchasable clothing and pet outfit catalog data, delegating appearance and texture
/// enumeration to match character creation indices across CMX and BCF files.</summary>
internal sealed class SimOutfits
{
    internal sealed record Choice(string Appearance, string BodyTexture, string HandTexture);
    internal sealed record Context(XPerson Subject, bool Pet, short OutfitType, string SimType, string Skin);

    private static readonly string[] CollectionCodes = ["b", "f", "s", "l", "w", "h"];
    private readonly string game;
    private readonly ObjectRegistry registry;
    private readonly byte language;
    private readonly AvatarSceneGeometry geometry;

    internal SimOutfits(string game, ObjectRegistry registry, byte language)
    {
        this.game = game;
        this.registry = registry;
        this.language = language;
        geometry = AvatarSceneGeometry.For(game, registry);
    }

    internal Context Find(SimulationWorld world, SimulationDialogRecord? dialog = null)
    {
        dialog ??= world.DialogRecords.LastOrDefault(record => record.Visible && record.Kind is 6 or 9)
            ?? throw new InvalidOperationException("No wardrobe or pet-choice dialog is waiting");
        if (dialog.Kind == 9)
        {
            var pet = (dialog.Icon ?? dialog.Owner) as XPerson
                ?? throw new InvalidOperationException("Pet-choice dialog has no pet stack object");
            var genderMode = WardrobeSuitService.ResolvePersonGenderMode(registry, language, pet);
            short type;
            if (genderMode is 2 or 3) type = -1;
            else if (genderMode is 4 or 5) type = -2;
            else
            {
                var gender = pet[PersonData.Gender];
                type = (gender & 2) != 0 ? (short)-1 : (gender & 4) != 0 ? (short)-2
                    : throw new InvalidOperationException("Pet-choice dialog stack object is not a cat or dog");
            }
            return new Context(pet, true, type, type == -1 ? "dog" : "cat", string.Empty);
        }

        if (dialog.Owner is not XPerson person) throw new InvalidOperationException("Wardrobe dialog owner is not a Sim");
        var outfitType = person.Temporaries[0];
        if (outfitType < 0 || outfitType >= CollectionCodes.Length)
            throw new InvalidOperationException($"Wardrobe collection {outfitType} is outside the Sims.exe clothes table");
        var strings = BodyStrings(person);
        if (strings.Count <= 14 || strings[1].Length <= 4)
            throw new InvalidDataException("Sim object has incomplete wardrobe body strings");
        var encodedType = strings[1][4..];
        var separator = encodedType.IndexOf('_');
        var simType = separator >= 0 ? encodedType[..separator] : encodedType;
        var skin = strings[14].Trim();
        if (simType.Length == 0 || skin.Length == 0) throw new InvalidDataException("Sim object has incomplete wardrobe type or skin data");
        return new Context(person, false, outfitType, simType.ToLowerInvariant(), skin.ToLowerInvariant());
    }

    internal Choice[] Choices(Context context) =>
        WardrobeSuitService.GetChoices(registry, language, context.Subject, context.Pet, context.OutfitType, game)
            .Select(choice => new Choice(choice.Appearance, choice.BodyTexture, choice.HandTexture))
            .ToArray();

    internal object Preview(string session, Context context, int index)
    {
        var choices = Choices(context);
        if (index < 0 || index >= choices.Length) throw new ArgumentException("Outfit index is out of range");
        var choice = choices[index];
        var person = context.Subject;
        var oldPresentation = person.PresentationCharacter;
        var oldOutfit = person[PersonData.CurrentOutfit];
        var character = person.SavedCharacter ?? throw new InvalidDataException($"Sim {person.Id} has no saved character state");
        var parts = character.Parts.Count >= 5
            ? character.Parts.ToArray()
            : Enumerable.Repeat((string.Empty, string.Empty), 5).ToArray();
        parts[0] = (choice.Appearance + ".apr", "BODY=" + choice.BodyTexture);
        if (!context.Pet)
        {
            var genderMode = WardrobeSuitService.ResolvePersonGenderMode(registry, language, person);
            var isChild = person[PersonData.PersonAge] is > 0 and < 18;
            var code = isChild ? 'u' : genderMode == 0 ? 'm' : genderMode == 1 ? 'f'
                : (context.SimType.Length > 1 && context.SimType[1] == 'a' ? context.SimType[0] : 'u');
            var handTex = choice.HandTexture.Length > 0 ? "huao" + choice.HandTexture : "huao" + context.Skin;
            parts[2] = ($"h{code}lo.apr", "HAND=" + handTex);
            parts[3] = ($"h{code}ro.apr", "HAND=" + handTex);
        }
        person[PersonData.CurrentOutfit] = 0;
        person.PresentationCharacter = character with { Parts = parts };
        try
        {
            var nativeParts = geometry.Build(person, 1, 0, (_, _) => 0, new LotCamera(0, 128, 128));
            return HouseholdAppearancePreview.Write(session, nativeParts);
        }
        finally
        {
            person[PersonData.CurrentOutfit] = oldOutfit;
            person.PresentationCharacter = oldPresentation;
        }
    }

    private IReadOnlyList<string> BodyStrings(XPerson person)
    {
        if (person.TypeRecord is null || registry.Find(unchecked((uint)person.TypeRecord.Key)) is not { } entry)
            throw new InvalidDataException("Wardrobe Sim object definition is unavailable");
        var id = unchecked((ushort)entry.Definition[ObjdField.BodyStringsId]);
        var chunk = entry.File.Find(ResourceType.Str, id) ?? throw new InvalidDataException("Wardrobe Sim body strings are missing");
        return StringTable.Read(entry.File, chunk).ForLanguage(language);
    }
}
