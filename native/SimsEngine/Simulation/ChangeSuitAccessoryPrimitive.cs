namespace SimsEngine.Simulation;

/// <summary>Implements the Change Suit/Accessory primitive (opcode 0x06). Modes 0 and 2 bind or remove
/// appearance resources (.apr) defined in STR# 304 from Global or Object scope; mode 1 selects random
/// outfit variants according to the Sim's age category and updates the current outfit.</summary>
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

        // Resolves Object scope to the executing frame's code owner, falling back to the stack object's
        // scope for shared routines.
        var scope = (mode == 2 ? context.Frame.Table as Content.BehaviorScope : null)
            ?? (mode == 2 ? context.StackObject?.TypeRecord?.BehaviorScope as Content.BehaviorScope : null)
            ?? context.Me.TypeRecord?.BehaviorScope as Content.BehaviorScope
            ?? context.Frame.Table as Content.BehaviorScope;
        if (scope is null) throw new SimulationException(SimulationError.InvalidSuitLocation);
        var file = mode == 0 ? scope.Global : scope.Private;
        var chunk = file.Find(SimsEngine.Formats.ResourceType.Str, 304);
        if (chunk is null) throw new SimulationException(SimulationError.InvalidSuitLocation);
        var strings = SimsEngine.Formats.StringTable.Read(file, chunk).ForLanguage(context.World.Language);
        var entry = (int)index;
        if (entry >= strings.Count || string.IsNullOrWhiteSpace(strings[entry]))
            throw new SimulationException(SimulationError.InvalidSuitIndex,
                $"STR# 304 entry {entry} is empty or missing in {file.Source} ({strings.Count} entries)");

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

    private static short DrawDifferentVariant(SimulationWorld world, short previous)
    {
        uint draw;
        do draw = world.NextRandom() % VariantCount;
        while (draw == (uint)(int)previous);
        return (short)draw;
    }

    private static XPerson Person(in TreeContext context) =>
        context.Me as XPerson ?? throw new InvalidOperationException($"Object {context.Me.Id} is not a person; FUN_0045cd1f runs for persons only.");
}
