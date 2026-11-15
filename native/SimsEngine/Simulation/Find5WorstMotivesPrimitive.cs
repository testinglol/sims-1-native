namespace SimsEngine.Simulation;

/// <summary>The Find 5 Worst Motives primitive (opcode 0x21), after Sims.exe FUN_0044d0b7. Traced from the disassembly.
/// <para>Operand: the subject (short at 4) is 1 for the stack object, 2 is refused, and anything else is me. The subject
/// must be a person (definition type 2), or the primitive raises error 10. The search type (short at 6) must be 0 to 2,
/// or error 30. Sims.exe checks the search type but sorts the same eight motives whatever it is.</para>
/// <para>The motives are Energy, Comfort, Hunger, Hygiene, Bladder, Room, Social and Fun, sorted by the subject's motive
/// values, lowest first. The first five numbers are stored in my temporaries 0 to 4. Two motives within 0.01 of each
/// other compare equal (FUN_0044d07b). The sort is FUN_0063575e, which for eight elements moves the greatest remaining
/// element to the end in turn, keeping the first of equal maxima, with FUN_006357ac as the swap. Its tie order is
/// therefore the traced one, not a stable sort.</para></summary>
public static class Find5WorstMotivesPrimitive
{
    // The eight motive numbers FUN_0044d0b7 puts on its stack, in order: 5, 6, 7, 8, 9, 0xd, 0xe, 0xf.
    private static readonly Motive[] Candidates =
        [Motive.Energy, Motive.Comfort, Motive.Hunger, Motive.Hygiene, Motive.Bladder, Motive.Room, Motive.Social, Motive.Fun];

    private const int TypeField = 9;
    private const int Found = 5;
    private const ushort MaximumSearchType = 2;

    // DAT_00667260 and DAT_00667268: +0.01 and -0.01.
    private const double Tolerance = 0.01;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var subject = (short)(operand[4] | operand[5] << 8);
        var searchType = (ushort)(operand[6] | operand[7] << 8);
        var person = Subject(context, subject);
        if (searchType > MaximumSearchType) throw new SimulationException(SimulationError.SearchTypeIsInvalid);
        var worst = WorstFirst(person);
        for (var index = 0; index < Found; index++) context.Me.Temporaries[index] = (short)worst[index];
        return PrimitiveResult.True;
    }

    // FUN_0044d0b7 checks the subject's type (2) after finding it; a missing subject fails the same way.
    private static XPerson Subject(in TreeContext context, short subject)
    {
        var item = subject switch
        {
            1 => context.World.ObjectByStackObject(context.Me, context.Frame.StackObject),
            // Sims.exe refuses subject 2 with the same error as a missing or non-person subject.
            2 => null,
            _ => context.Me,
        };
        if (item is null || item.Definition[TypeField] != XPerson.PersonType) throw new SimulationException(SimulationError.ReferenceToTreeParameterWhenNoParameterExists);
        return item as XPerson ?? throw new InvalidOperationException($"Object {item.Id} has the person type but no person data.");
    }

    // FUN_0063575e on the candidates: each pass moves the greatest of the rest (the first of equal ones) to the end.
    private static Motive[] WorstFirst(XPerson person)
    {
        var list = (Motive[])Candidates.Clone();
        for (var end = list.Length - 1; end > 0; end--)
        {
            var best = 0;
            for (var current = 1; current <= end; current++)
                if (Compare(person, list[current], list[best]) > 0) best = current;
            (list[best], list[end]) = (list[end], list[best]);
        }
        return list;
    }

    // FUN_0044d07b: -1 when the first motive is below the second by more than 0.01, 1 when above by more, else 0.
    private static int Compare(XPerson person, Motive first, Motive second)
    {
        var difference = (double)person[first] - person[second];
        if (difference < -Tolerance) return -1;
        return difference > Tolerance ? 1 : 0;
    }
}
