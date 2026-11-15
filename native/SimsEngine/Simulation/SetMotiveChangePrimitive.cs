namespace SimsEngine.Simulation;

/// <summary>One motive change a person runs: the motive it moves, the change per tick (the rate, already scaled) and the
/// value it stops at. Sims.exe's entry is three dwords (motive, rate, target) in the person's motive-change vector at +0xad0
/// (the entries are added by FUN_0045f995 and erased by FUN_004609a6; FUN_0045cdf8 applies one rate step after a successful
/// person-object update).</summary>
public readonly record struct MotiveChange(Motive Motive, float Rate, float Target);

/// <summary>The Set Motive Change primitive (opcode 0x1d), after Sims.exe FUN_0045a2db (labelled "Set Motive Change" in
/// Behavior.iff STR# 139). Sims.exe reaches it through the person dispatcher FUN_0045cd1f, so the running object is a person.
/// <para>Operand: byte 0, the owner of the rate variable; byte 1, the owner of the target variable; byte 2, the motive; byte 3,
/// the flags; bytes 4 and 5, the rate's data (a short); bytes 6 and 7, the target's data (a short). Both variables are read
/// with <see cref="Variables.Get"/>, so a motive reads truncated toward zero.</para>
/// <para>Flag bit 0 clear adds an entry to the person's motive changes: the motive must be 5 to 9, 14 or 15, or the
/// primitive raises error 26 (<see cref="SimulationError.MotiveNumberOutOfRange"/>, checked before either variable is read).
/// The rate is the rate variable times 1/1800 (DAT_00667408, the unit is not traced), and the target is the target variable
/// as read. Entries are not merged: a second change of the same motive is a second entry, and they run in order. Flag bit
/// 0 set erases every motive change of the person (FUN_004609a6 over the whole vector) and does not check the motive.
/// Answers true in both cases.</para>
/// <para>The native person scheduler applies each entry after a successful object update, following FUN_0045cdf8. The
/// surrounding PERS-only callbacks in that wrapper remain host/runtime integration work.</para></summary>
public static class SetMotiveChangePrimitive
{
    // DAT_00667408: 0x3a11a2b4, about 1/1800. The unit it converts to is not traced.
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

    // Routing guarantees a person (FUN_0045cd1f runs only for the person class).
    private static XPerson Person(in TreeContext context) =>
        context.Me as XPerson ?? throw new InvalidOperationException($"Object {context.Me.Id} is not a person; FUN_0045cd1f runs for persons only.");
}
