using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class InteractionActionCancellationTests
{
    [Fact]
    public void CancellingQueuedSequenceRebuildsWrappedRingAndPreservesSurvivorOrder()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(10, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        person.ActionQueueCurrent = 6;
        person.ActionQueueEnd = 9;
        person.ActionQueue[6] = Action(31, 61);
        person.ActionQueue[7] = Action(32, 62);
        person.ActionQueue[0] = Action(33, 63);

        Assert.True(world.CancelInteractionAction(person, 62));

        Assert.Equal(0, person.ActionQueueCurrent);
        Assert.Equal(2, person.ActionQueueEnd);
        Assert.Equal(new[] { 61, 63 }, person.ActionQueue.Take(2).Select(item => item.Sequence));
        Assert.Equal(31, person.ActionQueue[0].Owner!.Id);
        Assert.Equal(33, person.ActionQueue[1].Owner!.Id);
    }

    [Fact]
    public void ActiveSequenceRequestsInterruptWithoutRemovingQueuedEntries()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(10, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        person.CurrentActionRecord = new ActionQueueSlot { Sequence = 70, Owner = world.Add(new XObject(70, PrimitiveTestWorld.Definition())) };
        person.GenericSimsCall02GateWordA84 = 70;
        person.ActionQueueEnd = 1;
        person.ActionQueue[0] = Action(31, 71);

        Assert.True(world.CancelInteractionAction(person, 70));
        Assert.False(world.CancelInteractionAction(person, 72));
        Assert.Equal((short)0, person.ActivePriority);
        Assert.True(person.ActiveActionCancelRequested);
        Assert.Equal(0, person.ActionQueueCurrent);
        Assert.Equal(1, person.ActionQueueEnd);
        Assert.Equal(71, person.ActionQueue[0].Sequence);
    }

    private static ActionQueueSlot Action(short targetId, int sequence) => new()
    {
        Owner = new XObject(targetId, PrimitiveTestWorld.Definition()),
        Sequence = sequence,
        RawDword10 = 1,
    };
}
