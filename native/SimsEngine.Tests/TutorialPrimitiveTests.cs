using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class TutorialPrimitiveTests
{
    // Operand: byte 0 is the mode.
    private static byte[] Operand(byte mode) => [mode, 0, 0, 0, 0, 0, 0, 0];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Fact]
    public void ClaimTakesAFreeTutorialAndSendsTheNotice()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        Assert.Equal(PrimitiveResult.True, TutorialPrimitive.Execute(context, Operand(0)));
        Assert.Same(me, context.World.TutorialOwner);
        Assert.Equal(["SendNotice(263,0)"], host.Calls);
    }

    [Fact]
    public void ClaimDispatchesItsNoticeWithoutAHost()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        context.World.Host = null;

        Assert.Equal(PrimitiveResult.True, TutorialPrimitive.Execute(context, Operand(0)));

        Assert.Same(me, context.World.TutorialOwner);
        Assert.Equal(["SendNotice(263,0)"], host.Calls);
        Assert.Null(context.World.Host);
    }

    [Fact]
    public void ClaimIsRefusedWhileAnySimHoldsTheTutorialIncludingTheRunningObject()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var other = context.World.Add(new XPerson(7, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        context.World.TutorialOwner = other;
        Assert.Equal(PrimitiveResult.False, TutorialPrimitive.Execute(context, Operand(0)));
        context.World.TutorialOwner = me;
        Assert.Equal(PrimitiveResult.False, TutorialPrimitive.Execute(context, Operand(0)));
        Assert.Same(me, context.World.TutorialOwner);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ReleaseClearsTheTutorialWhenTheRunningObjectHoldsIt()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        context.World.TutorialOwner = me;
        Assert.Equal(PrimitiveResult.True, TutorialPrimitive.Execute(context, Operand(1)));
        Assert.Null(context.World.TutorialOwner);
        // The holder's dialog records are checked first (FUN_00444c08's loop), then the notice goes out.
        Assert.Equal(["DialogRecordState(100)", "SendNotice(263,0)"], host.Calls);
    }

    [Fact]
    public void ReleaseByAnotherObjectOrOfAFreeTutorialIsFalseAndChangesNothing()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Equal(PrimitiveResult.False, TutorialPrimitive.Execute(context, Operand(1)));
        Assert.Null(context.World.TutorialOwner);
        var other = context.World.Add(new XPerson(7, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        context.World.TutorialOwner = other;
        Assert.Equal(PrimitiveResult.False, TutorialPrimitive.Execute(context, Operand(1)));
        Assert.Same(other, context.World.TutorialOwner);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void OtherModesAreTheInvalidTutorialActionError()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Equal(SimulationError.InvalidActionInTutorialPrimitive, Fails(() => TutorialPrimitive.Execute(context, Operand(2))));
        Assert.Equal(SimulationError.InvalidActionInTutorialPrimitive, Fails(() => TutorialPrimitive.Execute(context, Operand(0xff))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ReleasingAHolderWithADialogRecordDismissesItBeforeChangingOwner()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        context.World.TutorialOwner = me;
        host.DialogState = 1;
        Assert.Equal(PrimitiveResult.True, TutorialPrimitive.Execute(context, Operand(1)));
        Assert.Null(context.World.TutorialOwner);
        Assert.Equal(["DialogRecordState(100)", "DismissDialogRecords(100)", "SendNotice(263,0)"], host.Calls);
    }
}
