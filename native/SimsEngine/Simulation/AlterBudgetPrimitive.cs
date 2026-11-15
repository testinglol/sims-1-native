namespace SimsEngine.Simulation;

/// <summary>The Alter Budget primitive (opcode 0x19), after Sims.exe FUN_0044eb1e. Traced from the disassembly.
/// <para>Operand: byte 0 is the scope of the amount: 0 a literal, 1 a parameter, 2 a local, and anything else takes the
/// owner from byte 1. The data is the short at 2. Byte 4 holds the flags: bit 0 is JustTest (test without spending),
/// and bit 1 negates the amount (income). Byte 6 is the expense type, 0 to 8.</para>
/// <para>The expense type is checked first (error 56), then the amount is read as <see cref="Variables.Get"/> does.
/// It is negated when bit 1 is set. A charge (amount 0 or more) that the family budget does not cover returns false.
/// Otherwise the budget is charged unless bit 0 is set, and the primitive returns true. A negative amount never fails.
/// </para></summary>
public static class AlterBudgetPrimitive
{
    private const byte JustTest = 1;
    private const byte Negate = 2;
    private const byte MaximumExpenseType = 8;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var owner = Owner(operand[0], operand[1]);
        var data = (short)(operand[2] | operand[3] << 8);
        var flags = operand[4];
        var expenseType = operand[6];
        if (expenseType > MaximumExpenseType) throw new SimulationException(SimulationError.BadExpenseTypeSpecified);
        var value = Variables.Get(context, owner, data);
        var amount = (flags & Negate) != 0 ? -(int)value : value;
        var budget = context.World.RequireFamilyBudget();
        var funds = budget.ReadFamilyBudget();
        if (amount >= 0 && funds < amount) return PrimitiveResult.False;
        if ((flags & JustTest) == 0) budget.AlterFamilyBudget(expenseType, amount);
        return PrimitiveResult.True;
    }

    // Scopes 0 to 2 are a literal (owner 7), a parameter (9) and a local (25). Any other scope takes byte 1 as the owner.
    private static DataOwner Owner(byte scope, byte owner) => scope switch
    {
        0 => DataOwner.Literal,
        1 => DataOwner.Parameter,
        2 => DataOwner.Local,
        _ => (DataOwner)owner,
    };
}
