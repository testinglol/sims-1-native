using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class ActionRecordTransitionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IncomingOwnerControlsWordResetAndSequenceAssignment(bool oldOwner, bool newOwner)
    {
        var world = new SimulationWorld();
        var owner = new XObject(2, PrimitiveTestWorld.Definition());
        var person = new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            CurrentActionRecord = new ActionQueueSlot { Owner = oldOwner ? owner : null, Sequence = 81 },
            CurrentActionWord5D8 = 12,
            CurrentActionWord5DA = 34,
        };
        var incoming = new ActionQueueSlot { Owner = newOwner ? owner : null, Sequence = 0 };

        world.ApplyStartedInteractionAction(person, incoming);

        Assert.Equal(newOwner ? 0 : 12, person.CurrentActionWord5D8);
        Assert.Equal(newOwner ? 0 : 34, person.CurrentActionWord5DA);
        Assert.Equal(newOwner ? 1 : 0, person.CurrentActionRecord!.Sequence);
        Assert.Equal(0, incoming.Sequence);
        if (oldOwner) Assert.Equal(81, person.PreviousActionRecord!.Sequence);
        else Assert.Null(person.PreviousActionRecord);
    }
}
