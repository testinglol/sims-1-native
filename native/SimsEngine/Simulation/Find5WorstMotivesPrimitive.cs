namespace SimsEngine.Simulation;

/// <summary>Implements the Find 5 Worst Motives primitive (opcode 0x21). Sorts the eight primary motives
/// (Energy, Comfort, Hunger, Hygiene, Bladder, Room, Social, Fun) lowest first with an equality tolerance
/// of 0.01, storing the five lowest into temporaries 0 through 4.</summary>
public static class Find5WorstMotivesPrimitive
{
    private static readonly Motive[] Candidates =
        [Motive.Energy, Motive.Comfort, Motive.Hunger, Motive.Hygiene, Motive.Bladder, Motive.Room, Motive.Social, Motive.Fun];

    private const int TypeField = 9;
    private const int Found = 5;
    private const ushort MaximumSearchType = 2;

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

    private static XPerson Subject(in TreeContext context, short subject)
    {
        var item = subject switch
        {
            1 => context.World.ObjectByStackObject(context.Me, context.Frame.StackObject),
            2 => null,
            _ => context.Me,
        };
        if (item is null || item.Definition[TypeField] != XPerson.PersonType) throw new SimulationException(SimulationError.ReferenceToTreeParameterWhenNoParameterExists);
        return item as XPerson ?? throw new InvalidOperationException($"Object {item.Id} has the person type but no person data.");
    }

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

    private static int Compare(XPerson person, Motive first, Motive second)
    {
        var difference = (double)person[first] - person[second];
        if (difference < -Tolerance) return -1;
        return difference > Tolerance ? 1 : 0;
    }
}
