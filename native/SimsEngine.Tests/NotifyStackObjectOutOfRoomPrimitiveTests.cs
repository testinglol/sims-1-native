using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class NotifyStackObjectOutOfRoomPrimitiveTests
{
    private static readonly byte[] NoOperand = new byte[8];

    private sealed class Table(Bhav tree) : ITreeTable
    {
        public Bhav? Find(short id) => id == 4096 ? tree : null;
    }

    private static (TreeContext Context, XObject StackObject, StackFrame Frame) Lot(
        short opcode, short counterIndex = 0, short[]? parameters = null)
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 5);
        var stackObject = context.World.Add(new XObject(5, PrimitiveTestWorld.Definition()));
        var body = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16((ushort)opcode).U8(0xFE).U8(0xFF)
            .Raw([(byte)counterIndex, (byte)(counterIndex >> 8), 0, 0, 0, 0, 0, 0]);
        var (file, chunk) = body.InFile("BHAV", 4096);
        var tree = Bhav.Read(file, chunk);
        stackObject.SavedStack.Add(new ObjectStackFrame(5, 4096, 0, parameters ?? [5], [], 0, 0));
        var stack = TreeRunner.RestoreSavedStack(context.World, stackObject, _ => new Table(tree));
        context.World.SavedExecutionStacks[stackObject.Id] = stack;
        return (context, stackObject, stack.Top!);
    }

    [Fact]
    public void AMissingStackObjectIsTheNoStackObjectError()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        var error = Assert.Throws<SimulationException>(() => NotifyStackObjectOutOfRoomPrimitive.Execute(context, NoOperand)).Error;
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, error);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void WithoutAWaitRecordNothingChanges()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        var stackObject = context.World.Add(new XObject(5, PrimitiveTestWorld.Definition()));
        Assert.Equal(PrimitiveResult.True, NotifyStackObjectOutOfRoomPrimitive.Execute(context, NoOperand));
        Assert.Empty(host.Calls);
        Assert.Equal((short)-1, context.World.ClockManager.WaitTicksAt(stackObject.ClockManagerIndex));
    }

    [Fact]
    public void SleepClearsTheCounterAndZeroesTheWaitCell()
    {
        var (context, stackObject, frame) = Lot(0, parameters: [5]);
        Assert.Equal(PrimitiveResult.True, NotifyStackObjectOutOfRoomPrimitive.Execute(context, NoOperand));
        Assert.Equal((short)0, frame.Parameters[0]);
        Assert.Equal((short)0, context.World.ClockManager.WaitTicksAt(stackObject.ClockManagerIndex));
    }

    [Fact]
    public void IdleForInputClearsTheCounterOnly()
    {
        var (context, stackObject, frame) = Lot(0x11, parameters: [5]);
        Assert.Equal(PrimitiveResult.True, NotifyStackObjectOutOfRoomPrimitive.Execute(context, NoOperand));
        Assert.Equal((short)0, frame.Parameters[0]);
        Assert.Equal((short)-1, context.World.ClockManager.WaitTicksAt(stackObject.ClockManagerIndex));
    }

    [Fact]
    public void OtherOpcodesChangeNothing()
    {
        var (context, stackObject, frame) = Lot(0x12, parameters: [5]);
        Assert.Equal(PrimitiveResult.True, NotifyStackObjectOutOfRoomPrimitive.Execute(context, NoOperand));
        Assert.Equal((short)5, frame.Parameters[0]);
        Assert.Equal((short)-1, context.World.ClockManager.WaitTicksAt(stackObject.ClockManagerIndex));
    }

    [Fact]
    public void TheCounterParameterComesFromTheWaitingInstructionsOperand()
    {
        var (context, _, frame) = Lot(0, counterIndex: 1, parameters: [7, 5]);
        Assert.Equal(PrimitiveResult.True, NotifyStackObjectOutOfRoomPrimitive.Execute(context, NoOperand));
        Assert.Equal((short)7, frame.Parameters[0]);
        Assert.Equal((short)0, frame.Parameters[1]);
    }
}
