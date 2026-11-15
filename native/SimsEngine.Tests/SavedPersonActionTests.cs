using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class SavedPersonActionTests
{
    private static PersonAction Record(int sequence = 100) =>
        new(2, sequence, 10, 20, 30, 513, [-1, 2, -3, 4], 50, unchecked((short)0xfedc), 1.25f, 0x65);

    private static PersonState State(PersonAction first, PersonAction second, IReadOnlyList<PersonAction> actions) =>
        new(new(0, 0, [], [], [], null, []), new float[16], new float[16], new short[256], 0,
            first, second, actions, [], []);

    [Fact]
    public void ActiveSequenceWordAliasesTheCurrentActionRecordSequence()
    {
        var person = new XPerson(10, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            CurrentActionRecord = new ActionQueueSlot { Sequence = 12 },
        };

        Assert.Equal(12, person.GenericSimsCall02GateWordA84);
        person.GenericSimsCall02GateWordA84 = 44;
        Assert.Equal(44, person.CurrentActionRecord!.Sequence);
    }

    [Fact]
    public void HydrationResolvesReferencesAndPreservesEverySerializedField()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(10, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        var owner = world.Add(new XObject(20, PrimitiveTestWorld.Definition()));
        var secondary = world.Add(new XObject(30, PrimitiveTestWorld.Definition()));
        person.RequestKey = 10; owner.RequestKey = 20; secondary.RequestKey = 30;
        var saved = Record();
        var action = ActionQueueSlot.FromSaved(saved, world);
        Assert.Same(person, action.Person);
        Assert.Same(owner, action.Owner);
        Assert.Same(secondary, action.Secondary);
        var written = action.ToSaved();
        Assert.Equal(saved with { Values0x14 = written.Values0x14 }, written);
        Assert.Equal(saved.Values0x14, written.Values0x14);
        var copied = action.Copy();
        copied.Parameters[0] = 999;
        Assert.Equal(-1, action.Parameters[0]);
        Assert.Equal(513, copied.RawDword10);
    }

    [Fact]
    public void DanglingOrNonPersonReferencesBecomeNullAsInTheOriginalLoader()
    {
        var world = new SimulationWorld();
        world.Add(new XObject(10, PrimitiveTestWorld.Definition()));
        var action = ActionQueueSlot.FromSaved(Record(), world);
        Assert.Null(action.Person);
        Assert.Null(action.Owner);
        Assert.Null(action.Secondary);
        Assert.Equal((short)0, action.ToSaved().Person);
    }

    [Fact]
    public void FixedRecordsAndWrappedRingSaveInLogicalOrderWithoutConsumedSlots()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(10, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        person.RestoreActionState(State(Record(100), Record(200), [Record(300), Record(400)]), world);
        Assert.Equal(100, person.CurrentActionRecord!.Sequence);
        Assert.Equal(100, person.GenericSimsCall02GateWordA84);
        Assert.Equal(200, person.PreviousActionRecord!.Sequence);
        Assert.Equal(new[] { 300, 400 }, person.WriteActionQueue().Select(action => action.Id));
        var first = person.ActionQueue[0];
        var second = person.ActionQueue[1];
        person.ActionQueueCurrent = 15; person.ActionQueueEnd = 17;
        person.ActionQueue[7] = first; person.ActionQueue[0] = second;
        Assert.True(SimulationWorld.TryDequeueInteractionAction(person, out var dequeued));
        Assert.Equal(300, dequeued!.Sequence);
        Assert.Equal(400, Assert.Single(person.WriteActionQueue()).Id);
        person.ClearPreviousActionRecord();
        Assert.Equal(-1, person.PreviousActionRecord!.ToSaved().Value0x10);
        person.ActionQueueEnd = person.ActionQueueCurrent;
        Assert.Empty(person.WriteActionQueue());
    }

    [Fact]
    public void LoadRaisesSequenceHighWaterMarkIncludingPreviousAction()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(10, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        var owner = world.Add(new XObject(20, PrimitiveTestWorld.Definition()));
        person.RestoreActionState(State(Record(100), Record(900), []), world);
        var interaction = new Interaction(4096, 4097, 0, 7, 0, 0, 0, 0, [], null);
        Assert.True(world.EnqueueInteractionAction(person, owner, interaction, 7, 50, null, false, 0));
        Assert.Equal(901, person.ActionQueue[0].Sequence);
        Assert.Equal(7, person.ActionQueue[0].ToSaved().Value0x10);
        Assert.Equal(4096, person.ActionQueue[0].ToSaved().Value0x20);
    }

    [Fact]
    public void QueuingClientInteractionPreservesTheActivePersonStack()
    {
        var world = new SimulationWorld();
        var table = new Table((4096, PrimitiveTestWorld.Tree(0,
            (0, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse, new byte[8]))));
        var person = world.Add(new XPerson(10, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(0x1234) { BehaviorScope = table },
        });
        var owner = world.Add(new XObject(20, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(0x5678) { BehaviorScope = table },
        });
        var stack = new PassiveStack();
        Assert.True(stack.Push(table, [], useDefault: false, 4096));
        world.SavedExecutionStacks[person.Id] = stack;
        var interaction = new Interaction(4096, 4097, 0, 7, 0, 0, 0, 0, [], null);

        Assert.True(world.EnqueueInteractionAction(person, owner, interaction, 7, 50, null, false, 0));

        Assert.Same(stack, world.SavedExecutionStacks[person.Id]);
        Assert.Single(stack.Frames);
        Assert.Equal((short)4096, stack.Top!.TreeId);
        Assert.Single(person.WriteActionQueue());
    }

    private sealed class Table(params (short Id, Bhav Tree)[] trees) : ITreeTable
    {
        public Bhav? Find(short treeId) => trees.FirstOrDefault(pair => pair.Id == treeId).Tree;
    }

    private sealed class PassiveStack : TreeStack
    {
        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction) => PrimitiveResult.Stop;
    }

}
