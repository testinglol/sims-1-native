using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class AlterBudgetPrimitiveTests
{
    private sealed class Budget(int funds) : IFamilyBudget
    {
        public int Funds { get; set; } = funds;
        public List<(int ExpenseType, int Amount)> Alterations { get; } = [];
        public int ReadFamilyBudget() => Funds;

        public void AlterFamilyBudget(int expenseType, int amount)
        {
            Alterations.Add((expenseType, amount));
            Funds -= amount;
        }
    }

    private static short[] Definition(short type = 0)
    {
        var definition = new short[XObject.DefinitionCount];
        definition[9] = type;
        return definition;
    }

    private static (TreeContext Context, XPerson Me, Budget Budget) World(int funds)
    {
        var budget = new Budget(funds);
        var world = new SimulationWorld { FamilyBudget = budget };
        var me = world.Add(new XPerson(1, Definition(XPerson.PersonType)));
        var frame = new StackFrame(0, [5, 1, -3], [0, 0, 7]);
        return (new TreeContext(world, me, frame), me, budget);
    }

    // Scope, owner byte, data, flags, expense type. Bytes 5 and 7 are unused.
    private static byte[] Operand(byte scope, byte owner, short data, byte flags, byte expenseType) =>
        [scope, owner, (byte)data, (byte)(data >> 8), flags, 0, expenseType, 0];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Fact]
    public void ChargesTheBudgetWhenItCoversTheAmount()
    {
        var (context, _, budget) = World(500);
        Assert.Equal(PrimitiveResult.True, AlterBudgetPrimitive.Execute(context, Operand(0, 0, 100, 0, 0)));
        Assert.Equal(400, budget.Funds);
        Assert.Equal(new[] { (0, 100) }, budget.Alterations.ToArray());
    }

    [Fact]
    public void AnAmountEqualToTheBudgetPasses()
    {
        var (context, _, budget) = World(100);
        Assert.Equal(PrimitiveResult.True, AlterBudgetPrimitive.Execute(context, Operand(0, 0, 100, 0, 0)));
        Assert.Equal(0, budget.Funds);
    }

    [Fact]
    public void FailsAChargeThatTheBudgetCannotCover()
    {
        // The original test: a charge fails whenever the budget is below it, not only when the budget is negative.
        var (context, _, budget) = World(10);
        Assert.Equal(PrimitiveResult.False, AlterBudgetPrimitive.Execute(context, Operand(0, 0, 500, 0, 0)));
        Assert.Equal(10, budget.Funds);
        Assert.Empty(budget.Alterations);
    }

    [Fact]
    public void JustTestAnswersWithoutSpending()
    {
        var (affordable, _, rich) = World(500);
        Assert.Equal(PrimitiveResult.True, AlterBudgetPrimitive.Execute(affordable, Operand(0, 0, 100, 1, 0)));
        Assert.Equal(500, rich.Funds);
        Assert.Empty(rich.Alterations);

        var (short_, _, poor) = World(10);
        Assert.Equal(PrimitiveResult.False, AlterBudgetPrimitive.Execute(short_, Operand(0, 0, 100, 1, 0)));
        Assert.Equal(10, poor.Funds);
    }

    [Fact]
    public void NegatedAmountIsIncomeAndNeverFails()
    {
        // Flag bit 1 negates the amount, so 100 becomes -100: the budget rises and nothing is tested.
        var (context, _, budget) = World(0);
        Assert.Equal(PrimitiveResult.True, AlterBudgetPrimitive.Execute(context, Operand(0, 0, 100, 2, 0)));
        Assert.Equal(100, budget.Funds);
        Assert.Equal(new[] { (0, -100) }, budget.Alterations.ToArray());
    }

    [Fact]
    public void ANegativeAmountNeverFails()
    {
        var (context, _, budget) = World(0);
        Assert.Equal(PrimitiveResult.True, AlterBudgetPrimitive.Execute(context, Operand(0, 0, -50, 0, 0)));
        Assert.Equal(50, budget.Funds);
    }

    [Fact]
    public void ReadsTheAmountFromItsScope()
    {
        // Scope 1 is parameter 1 (value 1), scope 2 is local 2 (value 7), and scope 3 takes owner 8 (temporary) with data 4.
        var (context, me, budget) = World(1000);
        me.Temporaries[4] = 33;
        Assert.Equal(PrimitiveResult.True, AlterBudgetPrimitive.Execute(context, Operand(1, 0, 1, 0, 0)));
        Assert.Equal(PrimitiveResult.True, AlterBudgetPrimitive.Execute(context, Operand(2, 0, 2, 0, 0)));
        Assert.Equal(PrimitiveResult.True, AlterBudgetPrimitive.Execute(context, Operand(3, 8, 4, 0, 8)));
        Assert.Equal(new[] { (0, 1), (0, 7), (8, 33) }, budget.Alterations.ToArray());
    }

    [Fact]
    public void ChecksTheExpenseTypeBeforeTheAmount()
    {
        var (context, _, _) = World(1000);
        // Owner 0xEE is not an owner, but the expense type (9) is refused first.
        Assert.Equal(SimulationError.BadExpenseTypeSpecified, Fails(() => AlterBudgetPrimitive.Execute(context, Operand(3, 0xEE, 0, 0, 9))));
        Assert.Equal(SimulationError.IllegalOwnerFieldInDataReference, Fails(() => AlterBudgetPrimitive.Execute(context, Operand(3, 0xEE, 0, 0, 8))));
    }

    [Fact]
    public void ExpenseTypeEightIsTheLastOne()
    {
        var (context, _, budget) = World(100);
        Assert.Equal(PrimitiveResult.True, AlterBudgetPrimitive.Execute(context, Operand(0, 0, 10, 0, 8)));
        Assert.Equal(new[] { (8, 10) }, budget.Alterations.ToArray());
    }

    [Fact]
    public void FailsWithoutAFamilyBudget()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(1, Definition(XPerson.PersonType)));
        var context = new TreeContext(world, me, new StackFrame(0, [], []));
        Assert.Throws<InvalidOperationException>(() => AlterBudgetPrimitive.Execute(context, Operand(0, 0, 10, 0, 0)));
    }
}
