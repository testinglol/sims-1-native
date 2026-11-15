using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public class TreeStackTests
{
    private const byte T = BhavInstruction.ReturnTrue, F = BhavInstruction.ReturnFalse;

    /// <summary>Primitive opcode 0 answers with its first operand byte (as a signed result code) and records the
    /// frame it ran in.</summary>
    private static (ushort Opcode, byte True, byte False, byte[] Operand) Prim(PrimitiveResult result, byte onTrue, byte onFalse) =>
        (0, onTrue, onFalse, [(byte)(sbyte)result, 0, 0, 0, 0, 0, 0, 0]);

    private static (ushort, byte, byte, byte[]) Gosub(short tree, byte onTrue, byte onFalse, params short[] parameters)
    {
        var operand = new byte[8];
        for (var index = 0; index < 4; index++) BitConverter.GetBytes(index < parameters.Length ? parameters[index] : (short)0).CopyTo(operand, index * 2);
        return ((ushort)tree, onTrue, onFalse, operand);
    }

    private static (ushort, byte, byte, byte[]) GosubDefault(short tree, byte onTrue, byte onFalse) =>
        ((ushort)tree, onTrue, onFalse, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);

    private static Bhav Tree(byte type, byte arguments, params (ushort Opcode, byte True, byte False, byte[] Operand)[] instructions)
    {
        var body = new ChunkBuilder().U16(0x8002).S16((short)instructions.Length).U8(type).U8(arguments).U8(0).U8(0).U32(0);
        foreach (var (opcode, onTrue, onFalse, operand) in instructions) body.U16(opcode).U8(onTrue).U8(onFalse).Raw(operand);
        var (file, chunk) = body.InFile("BHAV");
        return Bhav.Read(file, chunk);
    }

    private sealed class Table(Dictionary<short, Bhav> trees) : ITreeTable
    {
        public Bhav? Find(short id) => trees.GetValueOrDefault(id);
    }

    private sealed class Runner : TreeStack
    {
        public List<(short Tree, int Instruction, short[] Parameters, short StackObject)> Ran { get; } = [];

        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction)
        {
            Ran.Add((frame.TreeId, frame.Instruction, [.. frame.Parameters], frame.StackObject));
            return (PrimitiveResult)(sbyte)instruction.Operand[0];
        }
    }

    private static Table Trees(params (short Id, Bhav Tree)[] trees) => new(trees.ToDictionary(tree => tree.Id, tree => tree.Tree));

    [Fact]
    public void RunToReturnFollowsBranchesToTheReturnTarget()
    {
        var table = Trees((4096, Tree(0, 4, Prim(PrimitiveResult.True, 1, F), Prim(PrimitiveResult.False, F, T))));
        var runner = new Runner();
        Assert.True(runner.RunToReturn(table, [], false, 4096, 7));
        Assert.Equal([(4096, 0), (4096, 1)], runner.Ran.Select(run => (run.Tree, run.Instruction)));
        Assert.Empty(runner.Frames);
    }

    [Fact]
    public void GosubPassesItsOperandAndTheStackObjectAndBranchesTheCallerOnReturn()
    {
        var table = Trees(
            (4096, Tree(0, 4, Gosub(4097, F, 1, 5, 6, 7, 8), Prim(PrimitiveResult.True, T, F))),
            (4097, Tree(0, 4, Prim(PrimitiveResult.False, T, F))));
        var runner = new Runner();
        Assert.True(runner.RunToReturn(table, [], false, 4096, 42));
        Assert.Equal((short)4097, runner.Ran[0].Tree);
        Assert.Equal([5, 6, 7, 8], runner.Ran[0].Parameters);
        Assert.Equal(42, runner.Ran[0].StackObject);
        Assert.Equal(1, runner.Ran[1].Instruction);
    }

    [Fact]
    public void AnOperandOfAllFfBytesPassesTheDefaultParameters()
    {
        var table = Trees(
            (4096, Tree(0, 4, GosubDefault(4097, T, F))),
            (4097, Tree(0, 4, Prim(PrimitiveResult.True, T, F))));
        var runner = new Runner { DefaultParameters = [9, 8, 7, 6] };
        Assert.True(runner.RunToReturn(table, [], false, 4096, 0));
        Assert.Equal([9, 8, 7, 6], runner.Ran[0].Parameters);
    }

    [Fact]
    public void ATypeFourTreeStartsAtTheInstructionItsFirstParameterNamesClampedToTheLast()
    {
        var table = Trees((4096, Tree(4, 4, Prim(PrimitiveResult.True, T, F), Prim(PrimitiveResult.False, T, F), Prim(PrimitiveResult.True, T, F))));
        var runner = new Runner();
        Assert.False(runner.RunToReturn(table, [1], false, 4096, 0));
        Assert.True(runner.RunToReturn(table, [9], false, 4096, 0));
        Assert.Equal([1, 2], runner.Ran.Select(run => run.Instruction));
    }

    [Fact]
    public void BranchingToFdIsAnError()
    {
        var table = Trees((4096, Tree(0, 4, Prim(PrimitiveResult.True, BhavInstruction.Error, F))));
        var error = Assert.Throws<TreeRunException>(() => new Runner().RunToReturn(table, [], false, 4096, 0));
        Assert.Equal(TreeRunError.BadTarget, error.Error);
    }

    [Fact]
    public void AFalseChildReturnToAnFdCallTargetReportsTheChildReturnSentinel()
    {
        var table = Trees(
            (4096, Tree(0, 4, GosubDefault(4097, T, BhavInstruction.Error))),
            (4097, Tree(0, 4, Prim(PrimitiveResult.False, F, BhavInstruction.ReturnFalse))));

        var error = Assert.Throws<TreeRunException>(() => new Runner().RunToReturn(table, [], false, 4096, 0));

        Assert.Equal(TreeRunError.BadTarget, error.Error);
        Assert.Contains("returning from tree 4097 at return target 255 with result false", error.Message);
        Assert.Contains("into caller tree 4096 instruction 0 selected the error target", error.Message);
    }

    [Fact]
    public void AnIndexAboveFaThatIsNotAReturnIsAnError()
    {
        var table = Trees((4096, Tree(0, 4, Prim(PrimitiveResult.True, 0xFB, F))));
        var error = Assert.Throws<TreeRunException>(() => new Runner().RunToReturn(table, [], false, 4096, 0));
        Assert.Equal(TreeRunError.BadTarget, error.Error);
    }

    [Fact]
    public void ALoopRunsOutOfSteps()
    {
        var table = Trees((4096, Tree(0, 4, Prim(PrimitiveResult.True, 0, 0))));
        var runner = new Runner { StepLimit = 50 };
        var error = Assert.Throws<TreeRunException>(() => runner.RunToReturn(table, [], false, 4096, 0));
        Assert.Equal(TreeRunError.StepLimit, error.Error);
        Assert.Equal(49, runner.Ran.Count);
    }

    [Fact]
    public void AGosubToAMissingTreeIsAnError()
    {
        var table = Trees((4096, Tree(0, 4, Gosub(4100, T, F))));
        var error = Assert.Throws<TreeRunException>(() => new Runner().RunToReturn(table, [], false, 4096, 0));
        Assert.Equal(TreeRunError.TreeOrInstructionMissing, error.Error);
    }

    [Fact]
    public void AGosubToATreeWithNoInstructionsCannotBeEntered()
    {
        var table = Trees((4096, Tree(0, 4, Gosub(4097, T, F))), (4097, Tree(0, 4)));
        var error = Assert.Throws<TreeRunException>(() => new Runner().RunToReturn(table, [], false, 4096, 0));
        Assert.Equal(TreeRunError.TreeNotEntered, error.Error);
    }

    [Fact]
    public void RunStopsOnYieldWithoutMovingAndOnStop()
    {
        var table = Trees((4096, Tree(0, 4, Prim(PrimitiveResult.Again, T, F), Prim(PrimitiveResult.Yield, T, F))));
        var runner = new Runner { StepLimit = 10 };
        Assert.True(runner.Push(table, [], false, 4096));
        Assert.Equal(StepResult.Continue, runner.Step());
        Assert.Equal(0, runner.Top!.Instruction);
        runner.Top.Instruction = 1;
        Assert.False(runner.Run());
        Assert.Equal(1, runner.Top.Instruction);

        var stopping = Trees((4096, Tree(0, 4, Prim(PrimitiveResult.Stop, T, F))));
        var other = new Runner();
        other.Push(stopping, [], false, 4096);
        Assert.True(other.Run());
    }

    [Fact]
    public void ReturningFromTheOnlyFrameIsAnError()
    {
        var table = Trees((4096, Tree(0, 4, Prim(PrimitiveResult.True, T, F))));
        var runner = new Runner();
        runner.Push(table, [], false, 4096);
        Assert.Equal(StepResult.Continue, runner.Step());
        var error = Assert.Throws<TreeRunException>(() => runner.Step());
        Assert.Equal(TreeRunError.ReturnFromLastFrame, error.Error);
    }

    [Fact]
    public void ARunToReturnThatYieldsIsAnError()
    {
        var table = Trees((4096, Tree(0, 4, Prim(PrimitiveResult.Yield, T, F))));
        var error = Assert.Throws<TreeRunException>(() => new Runner().RunToReturn(table, [], false, 4096, 0));
        Assert.Equal(TreeRunError.NestedRunSuspended, error.Error);
    }

    [Fact]
    public void RestoreSavedStackRebindsTreesAndPreservesTheSerializedFrameState()
    {
        var ownerTable = Trees((4097, Tree(0, 4, Prim(PrimitiveResult.True, T, F))));
        var codeOwnerTable = Trees((4096, Tree(0, 4, Prim(PrimitiveResult.True, T, F))));
        var owner = new XObject(1, new short[XObject.DefinitionCount])
        {
            TypeRecord = new TypeRecord(1) { BehaviorScope = ownerTable },
        };
        owner.SavedStack.Add(new ObjectStackFrame(7, 0x9000, 3, [4, 5], [6], 0x12345678, 12));
        owner.SavedStackSize = 64;

        var runner = TreeRunner.RestoreSavedStack(new SimulationWorld(), owner,
            codeOwner => codeOwner == 12 ? codeOwnerTable : null);

        var frame = Assert.Single(runner.Frames);
        Assert.Equal(7, frame.StackObject);
        Assert.Equal((short)4096, frame.TreeId);
        Assert.True(frame.TreeBreakpoint);
        Assert.Equal(3, frame.Instruction);
        Assert.Equal([4, 5], frame.Parameters);
        Assert.Equal([6], frame.Locals);
        Assert.Equal(0x12345678, frame.WaitState);
        Assert.Equal(12, frame.CodeOwner);
        Assert.Same(codeOwnerTable, frame.Table);
    }

    [Fact]
    public void RestoreSavedStackKeepsTheNoTreeMarker()
    {
        var table = Trees((4096, Tree(0, 4, Prim(PrimitiveResult.True, T, F))));
        var owner = new XObject(1, new short[XObject.DefinitionCount])
        {
            TypeRecord = new TypeRecord(1) { BehaviorScope = table },
        };
        owner.SavedStack.Add(new ObjectStackFrame(0, ushort.MaxValue, 0, [], [], 0, 0));

        var runner = TreeRunner.RestoreSavedStack(new SimulationWorld(), owner,
            _ => throw new InvalidOperationException("A no-tree frame must not resolve a code owner."));

        var frame = Assert.Single(runner.Frames);
        Assert.Equal((short)-1, frame.TreeId);
        Assert.Null(frame.Tree);
        Assert.Null(frame.Table);
    }

    [Fact]
    public void RestoreSavedStackDoesNotSubstituteTheObjectsOwnScopeForAMissingCodeOwner()
    {
        var ownerScope = Trees((4096, Tree(0, 4, Prim(PrimitiveResult.True, T, F))));
        var owner = new XObject(1, new short[XObject.DefinitionCount])
        {
            TypeRecord = new TypeRecord(1) { BehaviorScope = ownerScope },
        };
        owner.SavedStack.Add(new ObjectStackFrame(0, 4096, 0, [], [], 0, 99));

        var runner = TreeRunner.RestoreSavedStack(new SimulationWorld(), owner, _ => null);

        var frame = Assert.Single(runner.Frames);
        Assert.Null(frame.Table);
        Assert.Null(frame.Tree);
        Assert.Equal((short)4096, frame.TreeId);
    }
}
