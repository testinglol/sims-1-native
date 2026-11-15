using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class MakeNewCharacterPrimitiveTests
{
    // Operand: the skin local (byte 0), the gender local (byte 1), the B local (byte 2) and the mode (byte 3).
    private static byte[] Operand(byte skin = 0, byte gender = 1, byte b = 0, byte mode = 2) => [skin, gender, b, mode, 0, 0, 0, 0];

    private static (TreeContext Context, PrimitiveTestHost Host) Lot()
    {
        var host = new PrimitiveTestHost();
        var world = new SimulationWorld { Host = host };
        var me = world.Add(new XPerson(100, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        return (new TreeContext(world, me, new StackFrame(0, [], [1, 2])), host);
    }

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Theory]
    [InlineData(2, 0, 0)]
    [InlineData(0, 2, 0)]
    [InlineData(0, 0, 2)]
    public void AnyLocalOutOfRangeIsStackNumberError(byte skin, byte gender, byte b)
    {
        var (context, host) = Lot();
        Assert.Equal(SimulationError.StackNumberOutOfRange, Fails(() => MakeNewCharacterPrimitive.Execute(context, Operand(skin, gender, b))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void AFailedCreationAnswersFalse()
    {
        var (context, host) = Lot();
        Assert.Equal(PrimitiveResult.False, MakeNewCharacterPrimitive.Execute(context, Operand(mode: 2)));
        Assert.Equal(new[] { "CreateCharacter(2)" }, host.Calls);
    }

    [Fact]
    public void ACreatedCharacterBecomesTheStackObject()
    {
        var (context, host) = Lot();
        host.NewCharacter = new XPerson(9, PrimitiveTestWorld.Definition(XPerson.PersonType));
        Assert.Equal(PrimitiveResult.True, MakeNewCharacterPrimitive.Execute(context, Operand(mode: 1)));
        Assert.Equal(new[] { "CreateCharacter(1)" }, host.Calls);
        Assert.Equal(9, context.Frame.StackObject);
    }

    [Theory]
    [InlineData(0, 0x7fd96b54)]
    [InlineData(1, 0x4a70df92)]
    [InlineData(2, 0x7bea0977)]
    [InlineData(5, 0x7fd96b54)]
    public void WithoutAHostTheModePicksTheTemplate(byte mode, int template)
    {
        var (context, _) = Lot();
        context.World.Host = null;
        var asked = new List<int>();
        context.World.ObjectFactory = guid => { asked.Add(guid); return context.World.Add(new XPerson(9, PrimitiveTestWorld.Definition(XPerson.PersonType))); };
        Assert.Equal(PrimitiveResult.True, MakeNewCharacterPrimitive.Execute(context, Operand(mode: mode)));
        Assert.Equal([template], asked);
        Assert.Equal(9, context.Frame.StackObject);
    }
}
