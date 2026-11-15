using System.Text;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.Tests;

/// <summary>Run Tree By Name (0x1c), after FUN_0044ba66: the waiting frame's result, the string and mode decoding, the
/// stack object check and its errors. Named-tree resolution is covered by GlobalLibraryTests.</summary>
public class RunTreeByNamePrimitiveTests
{
    private const short StackObjectId = 5;
    private const short TableWord = 0x0102;

    /// <summary>The operand: the string table in word 0, the flags in byte 2, the string index in byte 4 and the mode in
    /// byte 5.</summary>
    private static byte[] Operand(short table = TableWord, byte index = 1, byte mode = 0, byte flags = 0) =>
        [(byte)table, (byte)(table >> 8), flags, 0, index, mode, 0, 0];

    private static Bhav Caller(byte mode = 0, byte index = 1) =>
        PrimitiveTestWorld.Tree(0, (0x1c, 0xFE, 0xFF, Operand(mode: mode, index: index)));

    /// <summary>A stack that runs the tests' primitives: an Expression and Run Tree By Name, with the frame stack.</summary>
    private sealed class PrimitiveStack(SimulationWorld world, XObject me) : TreeStack
    {
        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction)
        {
            var context = new TreeContext(world, me, frame) { Stack = this };
            return instruction.Opcode switch
            {
                2 => ExpressionPrimitive.Execute(context, instruction.Operand) ? PrimitiveResult.True : PrimitiveResult.False,
                0x1c => RunTreeByNamePrimitive.Execute(context, instruction.Operand),
                _ => throw new InvalidOperationException($"The test stack does not run opcode {instruction.Opcode}."),
            };
        }
    }

    private static SimulationError Fails(Action run) => Assert.Throws<SimulationException>(run).Error;

    private static (TreeContext Context, PrimitiveTestHost Host) Lot(bool withStackObject = true)
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: StackObjectId);
        if (withStackObject) context.World.Add(new XObject(StackObjectId, PrimitiveTestWorld.Definition()));
        return (context, host);
    }

    private static BehaviorScope NamedScope(string name, ushort id)
    {
        var expression = PrimitiveTestWorld.Expression(0, 0, (byte)ExpressionOperator.EqualTo);
        var body = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(2).U8(BhavInstruction.ReturnTrue).U8(BhavInstruction.ReturnFalse).Raw(expression).ToArray();
        var bytes = ChunkBuilder.File(("BHAV", id, body));
        Encoding.Latin1.GetBytes(name).CopyTo(bytes, IffFile.HeaderSize + 12);
        var file = new IffFile(bytes, "named.iff");
        return new BehaviorScope(file, file, null);
    }

    private static BehaviorScope StringScope(string value, string? globalValue = null, byte flags = 0)
    {
        var strings = new ChunkBuilder().S16(-1).S16(1).CString(value);
        var caller = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(0x1c).U8(0xFE).U8(0xFF).Raw(Operand(flags: flags)).ToArray();
        var file = new IffFile(ChunkBuilder.File(("STR#", unchecked((ushort)TableWord), strings.ToArray()),
            ("BHAV", 4096, caller)), "caller.iff");
        var global = globalValue is null ? file : new IffFile(ChunkBuilder.File(("STR#", unchecked((ushort)TableWord),
            new ChunkBuilder().S16(-1).S16(1).CString(globalValue).ToArray())), "global.iff");
        return new BehaviorScope(global, file, null);
    }

    private static void AttachNamedScope(TreeContext context, string name = "Greet") =>
        context.World.ObjectById(StackObjectId)!.TypeRecord = new TypeRecord(1) { BehaviorScope = NamedScope(name, 4096) };

    [Fact]
    public void TheNameIsReadFromTheOperandsTableAndIndex()
    {
        var (context, host) = Lot();
        host.Strings[(TableWord, 0)] = "Greet";
        // The name and stack object exist, but no behavior scope is attached to the stack object's type record.
        Assert.False(TreeRunner.Run(context, Caller(), 0, new TreeTable()));
        Assert.Equal(new[] { (TableWord, 0) }, host.StringRequests);
    }

    [Fact]
    public void TheStringIndexIsAnUnsignedByte()
    {
        var (context, host) = Lot();
        host.Strings[(TableWord, 0xFD)] = "";
        Assert.False(TreeRunner.Run(context, Caller(index: 0xFE), 0, new TreeTable()));
        Assert.Equal(new[] { (TableWord, 0xFD) }, host.StringRequests);
    }

    [Fact]
    public void EmptyNameIsFalseBeforeTheModeIsChecked()
    {
        var (context, host) = Lot();
        host.Strings[(TableWord, 3)] = "";
        // Mode 3 is bad, but the empty name answers first (the handler reads the string before the mode).
        Assert.False(TreeRunner.Run(context, Caller(mode: 3), 0, new TreeTable()));
    }

    [Fact]
    public void MissingNameIsFalse()
    {
        var (context, _) = Lot();
        Assert.False(TreeRunner.Run(context, Caller(), 0, new TreeTable()));
    }

    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)0xFF)]
    public void ModeOtherThanZeroToTwoIsErrorFortyFour(byte mode)
    {
        var (context, host) = Lot();
        host.Strings[(TableWord, 0)] = "Greet";
        Assert.Equal(SimulationError.BadParameterToCallNamedTree, Fails(() => { TreeRunner.Run(context, Caller(mode), 0, new TreeTable()); }));
    }

    [Fact]
    public void MissingStackObjectIsErrorTwentyOne()
    {
        var (context, host) = Lot(withStackObject: false);
        host.Strings[(TableWord, 0)] = "Greet";
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => { TreeRunner.Run(context, Caller(), 0, new TreeTable()); }));
    }

    [Fact]
    public void FoundNameWithoutATypeBehaviorScopeAnswersFalse()
    {
        var (context, host) = Lot();
        host.Strings[(TableWord, 0)] = "Greet";
        Assert.False(TreeRunner.Run(context, Caller(mode: 2), 0, new TreeTable()));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    public void NamedTreeRunsSynchronouslyOrPushesAndReturnsItsResult(byte mode)
    {
        var (context, host) = Lot();
        host.Strings[(TableWord, 0)] = "Greet";
        AttachNamedScope(context);

        Assert.True(TreeRunner.Run(context, Caller(mode), 0, new TreeTable()));
    }

    [Fact]
    public void LocalNameComesFromTheCallingBehaviorScopeWhileTreeComesFromTheStackObject()
    {
        var (context, _) = Lot();
        context.World.ObjectById(StackObjectId)!.TypeRecord = new TypeRecord(2)
        { BehaviorScope = NamedScope("Greet", 4096) };
        var callerScope = StringScope("Greet");
        context.World.CaptureExecutionTrace = true;

        var result = TreeRunner.RunWithParameters(context.World, context.Me, callerScope, 4096, StackObjectId, []);
        Assert.True(result, string.Join("; ", context.World.NamedTreeLookupTrace));
    }

    [Fact]
    public void GlobalFlagReadsTheGlobalStringTable()
    {
        var (context, _) = Lot();
        context.World.ObjectById(StackObjectId)!.TypeRecord = new TypeRecord(2)
        { BehaviorScope = NamedScope("Greet", 4096) };
        var callerScope = StringScope("Different", "Greet", flags: 1);

        Assert.True(TreeRunner.RunWithParameters(context.World, context.Me, callerScope, 4096, StackObjectId, []));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WaitingFrameAnswersTheResultOfTheTreeItStarted(bool result)
    {
        // Frames: a marker (waiting), the running tree (waiting, as a mode 2 call leaves it) and the tree it started,
        // which returns the result. The waiting frame reads LastResult and branches to its true or false target.
        var (context, _) = Lot();
        var table = new TreeTable();
        var stack = new PrimitiveStack(context.World, context.Me);
        var running = PrimitiveTestWorld.Tree(0, (0x1c, 0xFE, 0xFF, Operand()));
        var started = PrimitiveTestWorld.Tree(0, (2, 0xFE, 0xFF, PrimitiveTestWorld.Expression(0, result ? (short)0 : (short)1, 2)));
        stack.Frames.Add(new StackFrame(0, [], []) { TreeId = -1, Waiting = true });
        stack.Frames.Add(new StackFrame(StackObjectId, [], [0, 0]) { Tree = running, TreeId = 0x1000, Table = table, Waiting = true });
        stack.Frames.Add(new StackFrame(StackObjectId, [], []) { Tree = started, TreeId = 6, Table = table });
        while (stack.Top!.TreeId != -1) stack.Step();
        Assert.Equal(result ? (int)BhavInstruction.ReturnTrue : (int)BhavInstruction.ReturnFalse, stack.LastTarget);
    }

    [Fact]
    public void StringTableLookupReadsTheStackObjectsScope()
    {
        var strings = new ChunkBuilder().S16(-1).S16(1).CString("Greet");
        var file = new IffFile(ChunkBuilder.File(("STR#", (ushort)TableWord, strings.ToArray())), "scope.iff");
        var scope = new BehaviorScope(file, file, null);
        var world = new SimulationWorld();
        var item = world.Add(new XObject(1, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(1) { BehaviorScope = scope },
        });

        Assert.Equal("Greet", world.StringTableEntry(item, item, TableWord, 0));
        Assert.Null(world.StringTableEntry(item, item, TableWord, 1));
        Assert.Null(world.StringTableEntry(null, item, unchecked((short)(TableWord + 1)), 0));
    }

    [Fact]
    public void ExecuteNeedsTheStackItRunsOn()
    {
        var (context, host) = Lot();
        host.Strings[(TableWord, 3)] = "Greet";
        Assert.Throws<InvalidOperationException>(() => RunTreeByNamePrimitive.Execute(context, Operand()));
    }
}
