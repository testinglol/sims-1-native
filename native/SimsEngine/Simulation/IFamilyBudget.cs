namespace SimsEngine.Simulation;

/// <summary>The family budget: the money the Alter Budget primitive tests and charges, and the running total of each
/// expense type. Sims.exe keeps it in the simulation globals block (DAT_006e486c+0x1c): an int32 at +0x90, just after
/// the 64 globals at +0x10. Named after the two Sims.exe functions it stands for; the host implements it.
/// <para>Both methods share one rule that the implementation must keep: while "Simless Build Mode" (global 32) is 1,
/// the budget reads as 999999 and writes do nothing. The offset-to-global mapping is inferred from the field offsets;
/// the names are Behavior.iff's.</para></summary>
public interface IFamilyBudget
{
    /// <summary>FUN_0046d9e5: the budget the Alter Budget test compares a charge with.</summary>
    int ReadFamilyBudget();

    /// <summary>FUN_0046da38: subtracts <paramref name="amount"/> from the budget (a negative amount is income) and adds
    /// it to the running total of <paramref name="expenseType"/> (0 to 8). It then rewrites "Budget Mod 10000" (global
    /// 13, the budget modulo 10000) and "Budget Div 10000" (global 14, the budget divided by 10000) from the new budget.
    /// Sims.exe also calls FUN_0040766a(0xda, budget) (inferred: a notice to the budget display; not modelled).</summary>
    void AlterFamilyBudget(int expenseType, int amount);

    /// <summary>Called when the source simulation clock crosses midnight; implementations with SIMI history roll its day window.</summary>
    void AdvanceBudgetDay() { }
}
