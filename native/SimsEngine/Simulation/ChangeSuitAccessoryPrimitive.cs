namespace SimsEngine.Simulation;

/// <summary>The Change Suit/Accessory primitive (opcode 6), after Sims.exe FUN_0045dace (labelled "Change Suit/Accessory"
/// in Behavior.iff STR# 139). Sims.exe reaches it through the person dispatcher FUN_0045cd1f (see <see cref="TreeRunner"/>),
/// so the running object is a person.
/// <para>Operand: byte 0, the index (unsigned). Byte 1, the mode: 0 and 2 change the suit from a bank, 1 chooses a random
/// outfit variant, and any other mode raises error 47 (<see cref="SimulationError.InvalidSuitLocation"/>). Byte 2, the flags:
/// bit 0 removes the selected appearance in modes 0 and 2, and makes mode 1 answer true without changing anything.
/// Bytes 3 to 7 are not read.</para>
/// <para>Modes 0 and 2: the bank is Global.iff or the code owner's object IFF (Sims.exe FUN_00401f73/FUN_00401f6f;
/// with the original Global/Object scopes), STR# 304. A missing bank raises error 47. Sims.exe passes index + 1
/// to its one-based string reader (FUN_00465b21); the native STR# parser is zero-based, so it reads at index. An empty
/// entry raises error 48 (<see cref="SimulationError.InvalidSuitIndex"]).
/// The entry names an appearance resource. Flag bit 0 removes it from the person's appearance bindings; with the bit
/// clear the appearance is bound (FUN_0045dace's +0xdc/+0xd8 callbacks; the Mac callbacks corroborate this
/// mapping). The native model stores these names in the saved character's extra-part list.</para>
/// <para>Mode 1 (inferred from the code, no bank): an index below 8 clears the chosen variant (person data 21; the label
/// table names that field "CostumeTrunkOffsetHouseParty", which does not fit the code, so the variant is inferred). Otherwise
/// the variant is drawn from the random source (<see cref="SimulationWorld.NextRandom"/>): a person aged 1 to 17 gets 0 or 1
/// (1 when the variant was 0 and the outfit is the same, else a random low bit), and a person of any other age, age 0
/// included, gets a random value from 0 to 9 (drawn again while it equals the chosen variant when the outfit is the same).
/// The index is stored as the current outfit (person data 8, "CurrentOutfit").</para>
/// <para>Not modelled: the avatar's rendered geometry update and the FUN_0046f9f6 call before it.</para></summary>
public static class ChangeSuitAccessoryPrimitive
{
    private const byte RandomVariantMode = 1;
    private const uint VariantCount = 10;
    private const int MaximumChildAge = 0x11;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var index = operand[0];
        var mode = operand[1];
        var flags = operand[2];
        if (mode == RandomVariantMode) return ChooseVariant(context, index, flags);
        if (mode is not (0 or 2)) throw new SimulationException(SimulationError.InvalidSuitLocation);

        // Object scope is the original frame's code-owner resource.
        // Shared/global trees can be on Frame.Table while the interaction's target remains the code owner.
        var scope = (mode == 2 ? context.Frame.Table as Content.BehaviorScope : null)
            ?? (mode == 2 ? context.StackObject?.TypeRecord?.BehaviorScope as Content.BehaviorScope : null)
            ?? context.Me.TypeRecord?.BehaviorScope as Content.BehaviorScope
            ?? context.Frame.Table as Content.BehaviorScope;
        if (scope is null) throw new SimulationException(SimulationError.InvalidSuitLocation);
        // FUN_0045dace calls the global-folder lookup for mode 0 and the code owner's
        // object-resource lookup for mode 2. The tree currently executing can be a
        // global BHAV (for example a shared book-reading helper), so selecting by its
        // tree id would incorrectly read Global.iff for Object scope.
        var file = mode == 0 ? scope.Global : scope.Private;
        var chunk = file.Find(SimsEngine.Formats.ResourceType.Str, 304);
        if (chunk is null) throw new SimulationException(SimulationError.InvalidSuitLocation);
        var strings = SimsEngine.Formats.StringTable.Read(file, chunk).ForLanguage(context.World.Language);
        // FUN_0045dace passes operand[0] + 1 to the original one-based STR reader. Our parser returns a zero-based
        // list, so the equivalent entry is exactly operand[0].
        var entry = (int)index;
        if (entry >= strings.Count || string.IsNullOrWhiteSpace(strings[entry]))
            throw new SimulationException(SimulationError.InvalidSuitIndex,
                $"STR# 304 entry {entry} is empty or missing in {file.Source} ({strings.Count} entries)");

        // Sims.exe hands the loaded string object to the person's avatar callbacks. STR# 304
        // entries are .apr appearances; the native world records the binding on the saved character state so actions
        // can add it and later remove it without a simulation host.
        var appearance = strings[entry].Trim();
        if (!appearance.EndsWith(".apr", StringComparison.OrdinalIgnoreCase)) appearance += ".apr";
        var person = Person(context);
        var character = person.SavedCharacter
            ?? throw new InvalidOperationException($"Person {person.Id} has no saved character state for a suit change.");
        var appearances = character.ExtraParts.ToList();
        var existing = appearances.FindIndex(part => NormalizeAppearance(part.Item1) == NormalizeAppearance(appearance));
        if ((flags & 1) != 0)
        {
            if (existing >= 0) appearances.RemoveAt(existing);
        }
        else if (existing < 0)
        {
            appearances.Add((appearance, ""));
        }
        person.SavedCharacter = character with { ExtraParts = appearances };
        if (person.PresentationCharacter is { } presentation)
            person.PresentationCharacter = presentation with { ExtraParts = appearances };
        return PrimitiveResult.True;
    }

    private static string NormalizeAppearance(string name) =>
        (name.EndsWith(".apr", StringComparison.OrdinalIgnoreCase) ? name : name + ".apr").ToLowerInvariant();

    // Mode 1: the variant and outfit fields of the person. Flag bit 0 answers true before anything is read or drawn.
    private static PrimitiveResult ChooseVariant(in TreeContext context, byte index, byte flags)
    {
        var person = Person(context);
        if ((flags & 1) != 0) return PrimitiveResult.True;
        if (index < 8)
        {
            person[PersonData.CostumeTrunkOffsetHouseParty] = 0;
        }
        else
        {
            var world = context.World;
            var previous = person[PersonData.CostumeTrunkOffsetHouseParty];
            var age = person[PersonData.PersonAge];
            var ageDrawsTen = age == 0 || age > MaximumChildAge;
            short variant;
            if (index == person[PersonData.CurrentOutfit])
            {
                variant = ageDrawsTen ? DrawDifferentVariant(world, previous) : (short)(previous == 0 ? 1 : 0);
            }
            else
            {
                variant = ageDrawsTen ? (short)(world.NextRandom() % VariantCount) : (short)(world.NextRandom() & 1);
            }
            person[PersonData.CostumeTrunkOffsetHouseParty] = variant;
        }
        person[PersonData.CurrentOutfit] = index;
        return PrimitiveResult.True;
    }

    // Draws again while the value equals the previous variant (the unsigned compare with (int)previous, as Sims.exe makes it).
    private static short DrawDifferentVariant(SimulationWorld world, short previous)
    {
        uint draw;
        do draw = world.NextRandom() % VariantCount;
        while (draw == (uint)(int)previous);
        return (short)draw;
    }

    // Routing guarantees a person (FUN_0045cd1f runs only for the person class).
    private static XPerson Person(in TreeContext context) =>
        context.Me as XPerson ?? throw new InvalidOperationException($"Object {context.Me.Id} is not a person; FUN_0045cd1f runs for persons only.");
}
