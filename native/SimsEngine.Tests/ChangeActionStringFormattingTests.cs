using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public class ChangeActionStringFormattingTests
{
    [Fact]
    public void ExpandsSimAnticsTokensAndPrintfSpecifiers()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Name = "Bob";
        context.World.ActionStringRecordWriteEnabled = true;

        var target = new XObject(200, PrimitiveTestWorld.Definition());
        target.Name = "Telescope";
        context.World.Add(target);
        context.Frame.StackObject = 200;
        me.SetObjectTableEntry(200, target);
        context.Frame.Locals[0] = 42;

        host.Strings[(100, 0)] = "Hello $Me, look at %s for %d minutes!";

        var operand = new byte[] { 100, 0, 0, 0, 1 };
        var result = ChangeActionStringPrimitive.Execute(context, operand);

        Assert.Equal(PrimitiveResult.True, result);
        Assert.NotNull(me.CurrentActionRecord);
        Assert.Equal("Hello Bob, look at Telescope for 42 minutes!", me.CurrentActionRecord.ActionText);
        Assert.Equal(0x80, me.CurrentActionRecord.Flags & 0x80);
    }

    [Fact]
    public void UnknownTokenIsPreservedVerbatim()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        context.World.ActionStringRecordWriteEnabled = true;

        host.Strings[(101, 0)] = "Testing $UnknownToken preserved!";

        var operand = new byte[] { 101, 0, 0, 0, 1 };
        var result = ChangeActionStringPrimitive.Execute(context, operand);

        Assert.Equal(PrimitiveResult.True, result);
        Assert.NotNull(me.CurrentActionRecord);
        Assert.Equal("Testing $UnknownToken preserved!", me.CurrentActionRecord.ActionText);
    }

    [Fact]
    public void RoutesToInteractionMenuWhenBuildingMenu()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var menu = new InteractionMenuContext(context.World);
        menu.Current = new ActionQueueSlot
        {
            Person = me,
            Owner = me,
            ActionTree = 1,
            Flags = 0
        };
        context.World.InteractionMenu = menu;

        host.Strings[(102, 0)] = "Custom Menu Option";

        var operand = new byte[] { 102, 0, 0, 0, 1 };
        var result = ChangeActionStringPrimitive.Execute(context, operand);

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Single(menu.Choices);
        Assert.Equal("Custom Menu Option", menu.Choices[0].ActionText);
    }

    [Fact]
    public void NestedSubroutineSuppressionLeavesRecordUntouched()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        context.World.ActionStringRecordWriteEnabled = false;

        host.Strings[(103, 0)] = "Nested text should not overwrite record";

        var operand = new byte[] { 103, 0, 0, 0, 1 };
        var result = ChangeActionStringPrimitive.Execute(context, operand);

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Null(me.CurrentActionRecord);
    }

    [Fact]
    public void ConcurrentExecutionUpdatesBothRecordAndHost()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        context.World.ActionStringRecordWriteEnabled = true;
        context.World.ActionStringDisplayActive = true;

        host.Strings[(104, 0)] = "Dual update message";

        var operand = new byte[] { 104, 0, 0, 0, 1 };
        var result = ChangeActionStringPrimitive.Execute(context, operand);

        Assert.Equal(PrimitiveResult.True, result);

        Assert.NotNull(me.CurrentActionRecord);
        Assert.Equal("Dual update message", me.CurrentActionRecord.ActionText);
        Assert.Equal(0x80, me.CurrentActionRecord.Flags & 0x80);

        Assert.Contains(host.Calls, c => c.StartsWith("ChangeActionString") && c.Contains("Dual update message"));
    }
}
