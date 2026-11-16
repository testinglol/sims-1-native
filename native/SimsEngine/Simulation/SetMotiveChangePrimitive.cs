namespace SimsEngine.Simulation;

/// <summary>Represents an active motive change rate applied to a character each tick until reaching a
/// target value.</summary>
public readonly record struct MotiveChange(Motive Motive, float Rate, float Target);

/// <summary>Sets or clears a continuous motive delta on a character. Flag bit 0 set clears all active
/// motive changes, while bit 0 clear registers a new motive change with rate scaled by 1/1800 per tick.
/// </summary>
public static class SetMotiveChangePrimitive
{
    private static readonly float RateScale = BitConverter.Int32BitsToSingle(0x3a11a2b4);

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var person = Person(context);
        var flags = operand[3];
        if ((flags & 1) != 0)
        {
            person.MotiveChanges.Clear();
            return PrimitiveResult.True;
        }

        var motive = operand[2];
        if (motive < 5 || (motive > 9 && (motive < 14 || motive > 15))) throw new SimulationException(SimulationError.MotiveNumberOutOfRange);
        var rate = Variables.Get(context, (DataOwner)operand[0], Data(operand, 4));
        var target = Variables.Get(context, (DataOwner)operand[1], Data(operand, 6));
        person.MotiveChanges.Add(new MotiveChange((Motive)motive, rate * RateScale, target));
        return PrimitiveResult.True;
    }

    private static short Data(ReadOnlySpan<byte> operand, int at) => (short)(operand[at] | operand[at + 1] << 8);

    private static XPerson Person(in TreeContext context) =>
        context.Me as XPerson ?? throw new InvalidOperationException($"Object {context.Me.Id} is not a person; FUN_0045cd1f runs for persons only.");
}
