using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class TestObjectTypePrimitiveTests
{
    private static byte[] Operand(int type, short data, byte owner) =>
        [(byte)type, (byte)(type >> 8), (byte)(type >> 16), (byte)(type >> 24), (byte)data, (byte)((ushort)data >> 8), owner, 0];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    private static (TreeContext Context, XObject Chair, PrimitiveTestHost Host) Lot()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        context.Me.Temporaries[0] = 2;
        var chair = context.World.Add(new XObject(2, PrimitiveTestWorld.Definition()));
        chair.TypeRecord = new TypeRecord(0x1234);
        return (context, chair, host);
    }

    [Fact]
    public void DecodesTheTypeDwordAndTheIdOperand()
    {
        var (context, _, _) = Lot();
        Assert.Equal(PrimitiveResult.True, TestObjectTypePrimitive.Execute(context, Operand(0x1234, 0, 8)));
        Assert.Equal(PrimitiveResult.False, TestObjectTypePrimitive.Execute(context, Operand(0x1235, 0, 8)));
    }

    [Fact]
    public void TheTypeIsADwordNotAShort()
    {
        var (context, chair, _) = Lot();
        chair.TypeRecord = new TypeRecord(0x12345678);
        Assert.Equal(PrimitiveResult.True, TestObjectTypePrimitive.Execute(context, Operand(0x12345678, 0, 8)));
        Assert.Equal(PrimitiveResult.False, TestObjectTypePrimitive.Execute(context, Operand(0x1234, 0, 8)));
    }

    [Fact]
    public void CachedNativeAlternativeTypeCanMatchWithoutHostLookup()
    {
        var (context, chair, host) = Lot();
        chair.TypeRecord = new TypeRecord(0x1234) { AlternativeTypeKey = 0x9999 };
        Assert.Equal(PrimitiveResult.True, TestObjectTypePrimitive.Execute(context, Operand(0x9999, 0, 8)));
        Assert.Equal(PrimitiveResult.False, TestObjectTypePrimitive.Execute(context, Operand(0x5555, 0, 8)));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("AlternativeObjectType", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingAlternativeAnswersFalse()
    {
        var (context, _, _) = Lot();
        Assert.Equal(PrimitiveResult.False, TestObjectTypePrimitive.Execute(context, Operand(0x9999, 0, 8)));
    }

    [Fact]
    public void ObjectWithoutTypeRecordAnswersFalse()
    {
        var (context, chair, host) = Lot();
        chair.TypeRecord = null;
        host.Alternatives[new TypeRecord(0x1234)] = new TypeRecord(0x1234);
        Assert.Equal(PrimitiveResult.False, TestObjectTypePrimitive.Execute(context, Operand(0x1234, 0, 8)));
    }

    [Fact]
    public void MissingObjectIsErrorTwentyThree()
    {
        var (context, _, _) = Lot();
        context.Me.Temporaries[0] = 42;
        Assert.Equal(SimulationError.CouldNotFindObjectReference, Fails(() => TestObjectTypePrimitive.Execute(context, Operand(0x1234, 0, 8))));
    }

    [Fact]
    public void IdOperandIsReadBeforeTheObjectIsLookedUp()
    {
        var (context, _, _) = Lot();
        Assert.Equal(SimulationError.IllegalOwnerFieldInDataReference, Fails(() => TestObjectTypePrimitive.Execute(context, Operand(0x1234, 0, 2))));
    }

    [Fact]
    public void StackObjectIdOwnerReadsTheFrame()
    {
        var (context, _, _) = Lot();
        var frame = new StackFrame(2, [], [0, 0]);
        var stackContext = context with { Frame = frame };
        Assert.Equal(PrimitiveResult.True, TestObjectTypePrimitive.Execute(stackContext, Operand(0x1234, 0, 10)));
    }
}
