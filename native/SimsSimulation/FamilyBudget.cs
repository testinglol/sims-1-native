using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsSimulation;

/// <summary>Supplies the source-defined active-family budget to the native SimAntics world. Sims.exe
/// FUN_0046d9e5/FUN_0046da38 read and update the global budget, including the Simless Build Mode override.
/// The parsed Sims.exe FAMI record is written back to its original neighborhood chunk when the user saves.</summary>
internal sealed class FamilyBudget : IFamilyBudget
{
    private readonly int[] expenseTotals = new int[9];
    private readonly SimulationWorld world;
    private Family? family;
    private IReadOnlyList<int>? activePersonKeysOverride;
    public ushort? FamilyChunkId { get; }
    /// <summary>The selected FAMI record and its ordered member-key vector, which backs GenericSimsCall's active-person manager.</summary>
    public Family? ActiveFamily
    {
        get => family;
        internal set
        {
            family = value;
            SyncActivePersonKeys();
        }
    }

    internal void RestrictActivePersonKeys(IReadOnlyList<int>? keys)
    {
        activePersonKeysOverride = keys;
        SyncActivePersonKeys();
    }

    private void SyncActivePersonKeys()
    {
        world.ActivePersonRecordKeys.Clear();
        world.ActivePersonManagerPresent = family is not null;
        if (!world.ActivePersonManagerPresent) return;
        if (activePersonKeysOverride is { Count: > 0 })
            world.ActivePersonRecordKeys.AddRange(activePersonKeysOverride);
        else
            world.ActivePersonRecordKeys.AddRange(family!.Members.Select(member => unchecked((int)member)));
    }


    public FamilyBudget(SimulationWorld world, Family? family, ushort? familyChunkId)
    {
        this.world = world;
        FamilyChunkId = familyChunkId;
        ActiveFamily = family;
        if (family is not null)
        {
            world[SimulationGlobal.BudgetMod10000] = unchecked((short)(family.Budget % 10000));
            world[SimulationGlobal.BudgetDiv10000] = unchecked((short)(family.Budget / 10000));
        }
        if (world.SavedSimulationState is { BudgetDays.Count: > 0 } saved)
            for (var index = 0; index < Math.Min(8, saved.BudgetDays[0].Values.Count); index++)
                expenseTotals[index] = saved.BudgetDays[0].Values[index];
        if (world.SavedSimulationState is { Version: >= 0x26, BudgetDays.Count: not 6 })
            SaveCurrentBudgetDay();
    }

    public int ReadFamilyBudget() => world[SimulationGlobal.SimlessBuildMode] == 1
        ? 999999
        : family?.Budget ?? 0;

    public void AlterFamilyBudget(int expenseType, int amount)
    {
        if ((uint)expenseType >= expenseTotals.Length) throw new ArgumentOutOfRangeException(nameof(expenseType));
        if (world[SimulationGlobal.SimlessBuildMode] == 1 || family is null) return;

        family = family.WithBudget(unchecked(family.Budget - amount));
        expenseTotals[expenseType] = unchecked(expenseTotals[expenseType] + amount);
        SaveCurrentBudgetDay();
        world[SimulationGlobal.BudgetMod10000] = unchecked((short)(family.Budget % 10000));
        world[SimulationGlobal.BudgetDiv10000] = unchecked((short)(family.Budget / 10000));
        // cSimulator::Spend/SetFunds send GlobalDispatch(0xda, funds), which cSimsApp::DoCommand
        // uses to set its ResetTutorial save byte when the lot is interactive.
        world.DispatchNotice(0xda, family.Budget);
    }

    public void AdvanceBudgetDay()
    {
        if (world.SavedSimulationState is not { } saved) return;
        var days = new BudgetDay[6];
        days[0] = new BudgetDay(true, new int[8]);
        for (var index = 1; index < days.Length; index++)
        {
            days[index] = index - 1 < saved.BudgetDays.Count
                ? saved.BudgetDays[index - 1]
                : (index == 1 ? new BudgetDay(true, expenseTotals.Take(8).ToArray()) : new BudgetDay(true, new int[8]));
        }
        Array.Clear(expenseTotals);
        world.ReplaceSavedBudgetDays(days);
    }

    private void SaveCurrentBudgetDay()
    {
        if (world.SavedSimulationState is not { } saved) return;
        var days = new BudgetDay[6];
        days[0] = new BudgetDay(true, expenseTotals.Take(8).ToArray());
        for (var index = 1; index < days.Length; index++)
        {
            days[index] = index < saved.BudgetDays.Count
                ? saved.BudgetDays[index]
                : new BudgetDay(true, new int[8]);
        }
        world.ReplaceSavedBudgetDays(days);
    }
}
