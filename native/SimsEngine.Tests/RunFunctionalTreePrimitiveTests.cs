using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public class RunFunctionalTreePrimitiveTests
{
    private sealed class ProbeStack : TreeStack
    {
        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction) =>
            throw new NotSupportedException("The stack is inspected before running its pushed tree.");
    }

    private const short StackObjectId = 5;
    private const short FunctionTree = 6;

    private static Bhav Caller(short selector, byte flags = 0) =>
        PrimitiveTestWorld.Tree(0, (0x14, 0xFE, 0xFF, new[] { (byte)selector, (byte)(selector >> 8), flags, (byte)0, (byte)0, (byte)0, (byte)0, (byte)0 }));

    private static Bhav Returns(bool result) =>
        PrimitiveTestWorld.Tree(0, (2, 0xFE, 0xFF, PrimitiveTestWorld.Expression(0, result ? (short)0 : (short)1, 2)));

    private static (TreeContext Context, PrimitiveTestHost Host, TreeTable Table) Lot(TreeTable? table = null)
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: StackObjectId);
        var scope = table ?? new TreeTable();
        context.World.Add(new XObject(StackObjectId, PrimitiveTestWorld.Definition()) { TypeRecord = new TypeRecord(1)
        {
            BehaviorScope = scope,
            FunctionTable = new ushort[0x20],
        } });
        return (context, host, scope);
    }

    private static void SetFunctionTree(TreeContext context, short function, short treeId)
    {
        var table = (ushort[])context.World.ObjectById(StackObjectId)!.TypeRecord!.FunctionTable!;
        table[function] = unchecked((ushort)treeId);
    }

    private static SimulationError Fails(Action run) => Assert.Throws<SimulationException>(run).Error;

    [Fact]
    public void SelectorTwoLooksUpFunctionFourteenAndTheCalleeReturnsTrue()
    {
        var (context, _, table) = Lot(new TreeTable((FunctionTree, Returns(true))));
        SetFunctionTree(context, 0x14, FunctionTree);
        Assert.True(TreeRunner.Run(context, Caller(2), 0, table));
    }

    [Fact]
    public void TheCalleesResultIsTheCallersBranch()
    {
        var (context, _, table) = Lot(new TreeTable((FunctionTree, Returns(false))));
        SetFunctionTree(context, 0x12, FunctionTree);
        Assert.False(TreeRunner.Run(context, Caller(0), 0, table));
    }

    [Fact]
    public void EverySelectorButEightMapsToItsFunctionNumber()
    {
        short[] expected = [0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1b, 0x0e, 0x1c, 0x10, 0x11, 0x1d];
        for (short selector = 0; selector <= 14; selector++)
        {
            if (selector == 8) continue;
            var functionIndex = selector < 8 ? selector : selector - 1;
            var treeId = (short)(FunctionTree + selector);
            var scope = new TreeTable((treeId, Returns(true)));
            var (context, _, _) = Lot(scope);
            SetFunctionTree(context, expected[functionIndex], treeId);
            Assert.True(TreeRunner.Run(context, Caller(selector), 0, scope));
        }
    }

    [Fact]
    public void FunctionTreeIdUsesTheLoadedActionTableAndChecksBounds()
    {
        var (context, _, _) = Lot();
        var item = context.World.ObjectById(StackObjectId)!;
        SetFunctionTree(context, 0x14, FunctionTree);
        Assert.Equal(FunctionTree, SimulationWorld.FunctionTreeId(item, 0x14));
        Assert.Equal((short)0, SimulationWorld.FunctionTreeId(item, -1));
        Assert.Equal((short)0, SimulationWorld.FunctionTreeId(item, 0x20));
    }

    [Fact]
    public void SelectorOutsideZeroToFourteenIsFalseWithoutALookup()
    {
        var (context, _, table) = Lot();
        Assert.False(TreeRunner.Run(context, Caller(15), 0, table));
        Assert.False(TreeRunner.Run(context, Caller(-1), 0, table));
    }

    [Fact]
    public void ObjectWithoutThatFunctionIsFalse()
    {
        var (context, _, table) = Lot(new TreeTable((FunctionTree, Returns(true))));
        Assert.False(TreeRunner.Run(context, Caller(0), 0, table));
    }

    [Fact]
    public void MissingStackObjectIsErrorTwentyOne()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: StackObjectId);
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => { TreeRunner.Run(context, Caller(0), 0, new TreeTable()); }));
    }

    [Fact]
    public void SelectorEightRequiresSupportedRunningPersonAndLooksUpFunctionOneA()
    {
        foreach (short gender in new short[] { -1, 0, 1 })
        {
            var (context, _, table) = Lot(new TreeTable((FunctionTree, Returns(true))));
            ((XPerson)context.Me)[PersonData.Gender] = gender;
            SetFunctionTree(context, 0x1a, FunctionTree);
            Assert.True(TreeRunner.Run(context, Caller(8), 0, table));
        }
    }

    [Fact]
    public void SelectorEightReturnsFalseForGenderAboveOneWithoutLookingUpFunction()
    {
        var (context, _, table) = Lot();
        ((XPerson)context.Me)[PersonData.Gender] = 2;
        Assert.False(TreeRunner.Run(context, Caller(8), 0, table));
    }

    [Fact]
    public void SelectorEightReturnsFalseWhenRunningObjectIsNotPerson()
    {
        var (context, _, table) = Lot();
        var me = new XObject(100, PrimitiveTestWorld.Definition());
        Assert.False(TreeRunner.Run(context with { Me = me }, Caller(8), 0, table));
    }

    [Fact]
    public void FlagBitZeroDoesNotChangeTheFunctionalTreePush()
    {
        var (context, _, table) = Lot(new TreeTable((FunctionTree, Returns(true))));
        SetFunctionTree(context, 0x12, FunctionTree);
        Assert.True(TreeRunner.Run(context, Caller(0, flags: 1), 0, table));
    }

    [Fact]
    public void FunctionalTreePushKeepsTheRawObjectKeyAndResolvesItBackToThePushedObject()
    {
        var (context, _, table) = Lot(new TreeTable((FunctionTree, Returns(true))));
        var pushed = context.World.ObjectById(StackObjectId)!;
        pushed.RequestKey = 42;
        pushed.Attributes = new short[2];
        pushed.Attributes[1] = 1;
        context.Me.SetObjectTableEntry(9, pushed);
        context.Me.SetObjectTableEntry(42, pushed);
        var wrongObject = context.World.Add(new XObject(77, PrimitiveTestWorld.Definition()) { Attributes = new short[] { 0, 0 } });
        context.Me.SetObjectTableEntry(5, wrongObject);
        SetFunctionTree(context, 0x12, FunctionTree);
        var stack = new ProbeStack();
        var frame = new StackFrame(9, [], []);
        var callContext = new TreeContext(context.World, context.Me, frame) { Stack = stack };

        Assert.Equal(PrimitiveResult.Again, RunFunctionalTreePrimitive.Execute(callContext, Caller(0).Instructions[0].Operand));
        Assert.Equal((short)42, stack.Top!.StackObject);
        Assert.Same(pushed, callContext.World.ObjectByStackObject(callContext.Me, stack.Top.StackObject));
        Assert.Equal((short)1, Variables.Get(callContext with { Frame = stack.Top }, DataOwner.StackObjectAttribute, 1));
    }

    [Fact]
    public void ObjectReferenceTableUsesOneBasedBoundsAndLoadedMissesStayMissing()
    {
        var world = new SimulationWorld();
        var owner = world.Add(new XObject(100, PrimitiveTestWorld.Definition()) { RequestKey = 100 });
        var item = world.Add(new XObject(5, PrimitiveTestWorld.Definition()) { RequestKey = 5 });
        owner.SetObjectTableEntry(2, item);

        Assert.Null(owner.ObjectTableEntry(0));
        Assert.Same(item, owner.ObjectTableEntry(2));
        Assert.Null(owner.ObjectTableEntry(3));
        Assert.Same(item, world.ObjectByStackObject(owner, item.Id));
        Assert.Null(world.ObjectByStackObject(owner, 3));
    }

    [Fact]
    public void AHostCannotFillAMissingNativeStackObjectTableEntry()
    {
        var world = new SimulationWorld();
        var target = world.Add(new XObject(7, PrimitiveTestWorld.Definition()) { RequestKey = 5 });
        var owner = new XObject(100, PrimitiveTestWorld.Definition()) { RequestKey = 100 };
        var host = new PrimitiveTestHost { ObjectTable = { [5] = target } };
        world.Host = host;

        Assert.Null(world.ObjectByStackObject(owner, 5));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("ObjectTableEntry", StringComparison.Ordinal));
    }

    [Fact]
    public void OtherFlagBitsAreNotReadByThisPrimitive()
    {
        var (context, _, table) = Lot(new TreeTable((FunctionTree, Returns(true))));
        SetFunctionTree(context, 0x12, FunctionTree);
        Assert.True(TreeRunner.Run(context, Caller(0, flags: 2), 0, table));
    }

    [Fact]
    public void TypeWithoutBehaviorScopeIsFalse()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: StackObjectId);
        var functions = new ushort[0x20];
        functions[0x12] = unchecked((ushort)FunctionTree);
        context.World.Add(new XObject(StackObjectId, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(1) { FunctionTable = functions },
        });
        Assert.False(TreeRunner.Run(context, Caller(0), 0, new TreeTable((FunctionTree, Returns(true)))));
    }

    [Fact]
    public void TheCalleeThatIsMissingFromTheTableIsFalse()
    {
        var (context, _, table) = Lot();
        SetFunctionTree(context, 0x12, FunctionTree);
        Assert.False(TreeRunner.Run(context, Caller(0), 0, table));
    }

    [Fact]
    public void ExecuteNeedsTheStackItRunsOn()
    {
        var (context, _, _) = Lot(new TreeTable((FunctionTree, Returns(true))));
        SetFunctionTree(context, 0x12, FunctionTree);
        Assert.Throws<InvalidOperationException>(() => RunFunctionalTreePrimitive.Execute(context, new byte[8]));
    }
}
