namespace SimsEngine.Simulation;

/// <summary>Manages family funds and expense categories. While Simless Build Mode is active, the budget
/// evaluates to 999,999 and balance modifications are ignored.</summary>
public interface IFamilyBudget
{
    int ReadFamilyBudget();

    /// <summary>Adjusts the family budget and tracks expenses by type, updating the budget display globals
    /// for modulo and division by 10,000.</summary>
    void AlterFamilyBudget(int expenseType, int amount);

    /// <summary>Advances the daily expense history tracking window when the simulation clock crosses
    /// midnight.</summary>
    void AdvanceBudgetDay() { }
}
