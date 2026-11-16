using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class ChangeActionStringPrimitiveTests
{
    private static byte[] Operand(short table, byte index) => [(byte)table, (byte)(table >> 8), 0, 0, index, 0, 0, 0];

    [Fact]
    public void TheEntrysTextBecomesTheActionWithTheStackObjectAsTarget()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        context.World.ActionStringDisplayActive = true;
        host.Strings[(3, 8)] = "Eating";
        Assert.Equal(PrimitiveResult.True, ChangeActionStringPrimitive.Execute(context, Operand(3, 9)));
        var request = Assert.Single(host.StringRequests);
        Assert.Equal((short)3, request.Table);
        Assert.Equal(8, request.Index);
        Assert.Equal(["ChangeActionString(100,5,Eating)"], host.Calls);
    }

    [Fact]
    public void AMissingEntryPassesNullToTheActiveDisplay()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        context.World.ActionStringDisplayActive = true;
        Assert.Equal(PrimitiveResult.True, ChangeActionStringPrimitive.Execute(context, Operand(3, 9)));
        Assert.Equal(["ChangeActionString(100,5,null)"], host.Calls);
    }

    [Fact]
    public void WithNoDisplayItStoresTextOnTheCurrentPersonActionWhenEnabled()
    {
        var (context, person, host) = PrimitiveTestWorld.Create(stackObject: 5);
        host.Strings[(3, 8)] = "Eating";
        context.World.ActionStringRecordWriteEnabled = true;

        Assert.Equal(PrimitiveResult.True, ChangeActionStringPrimitive.Execute(context, Operand(3, 9)));

        Assert.Equal("Eating", person.CurrentActionRecord?.ActionText);
        Assert.Equal(0x80, person.CurrentActionRecord?.Flags);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void WithNeitherOriginalGateItOnlyLooksUpTheText()
    {
        var (context, person, host) = PrimitiveTestWorld.Create(stackObject: 5);
        host.Strings[(3, 8)] = "Eating";
        context.World.ActionStringRecordWriteEnabled = false;

        Assert.Equal(PrimitiveResult.True, ChangeActionStringPrimitive.Execute(context, Operand(3, 9)));

        Assert.Null(person.CurrentActionRecord);
        Assert.Empty(host.Calls);
        Assert.Equal([( (short)3, 8 )], host.StringRequests);
    }
}
