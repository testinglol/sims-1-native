using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class SimulationClockManagerTests
{
    [Fact]
    public void AddedObjectsShareTheLotObjectTableAndRemovalLeavesItsSlotEmpty()
    {
        var world = new SimulationWorld();
        var first = world.Add(new XPerson(3, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        var second = world.Add(new XObject(7, PrimitiveTestWorld.Definition()));

        Assert.Same(first.ObjectTableEntries, second.ObjectTableEntries);
        Assert.Same(first, first.ObjectTableEntry(3));
        Assert.Same(second, first.ObjectTableEntry(7));
        Assert.Same(second, second.ObjectTableEntry(7));

        Assert.True(world.Remove(7));
        Assert.Null(first.ObjectTableEntry(7));
        Assert.Null(second.ObjectTableEntry(7));
    }

    private static XObject Object(short id) => new(id, new short[XObject.DefinitionCount]);

    [Fact]
    public void ParallelFlagWritesFollowFUN0044582fAndPreserveLowWaitWord()
    {
        var manager = new SimulationClockManager();
        manager.Register(Object(1));

        manager.SetFlag(1, 3, enabled: true);
        Assert.Equal(0x0008ffffu, manager.FlagsAt(1));
        manager.SetFlag(1, 3, enabled: false);
        Assert.Equal(0x0000ffffu, manager.FlagsAt(1));
        Assert.Equal(0u, manager.FlagsAt(2));
    }

    [Fact]
    public void EligibleObjectWaitDecrementsLowWordAndPreservesFlags()
    {
        var manager = new SimulationClockManager();
        var entry = new SimulationClockManagerEntry(Object(1), 0x00010003u);
        manager.Entries.Add(entry);

        var result = manager.Evaluate(new SimulationClockControl(1, 1, 1), 0, 24, (_, _) => throw new Xunit.Sdk.XunitException("waited object must not be ticked"));

        Assert.Equal(SimulationClockManagerResult.PassComplete, result);
        Assert.Equal((short)2, entry.WaitTicks);
        Assert.Equal(0x00010002u, entry.Flags);
    }

    [Fact]
    public void HeldClockSkipsObjectsUnlessTheirEntryAllowsWorkWhileHeld()
    {
        var manager = new SimulationClockManager();
        var entry = new SimulationClockManagerEntry(Object(1), 0x00030000u);
        manager.Entries.Add(entry);
        var calls = 0;

        var result = manager.Evaluate(new SimulationClockControl(0, 0, 1), 0, 5, (_, _) => { calls++; return true; });

        Assert.Equal(SimulationClockManagerResult.PassComplete, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RejectedObjectRetainsCursorForTheNextManagerPass()
    {
        var manager = new SimulationClockManager();
        var first = new SimulationClockManagerEntry(Object(1), 0x00010000u);
        var second = new SimulationClockManagerEntry(Object(2), 0x00010000u);
        manager.Entries.AddRange([first, second]);
        var seen = new List<short>();

        var rejected = manager.Evaluate(new SimulationClockControl(1, 1, 1), 0, 7, (item, _) =>
        {
            seen.Add(item.Id);
            return false;
        });
        var resumed = manager.Evaluate(new SimulationClockControl(1, 1, 1), 0, 8, (item, _) =>
        {
            seen.Add(item.Id);
            return true;
        });

        Assert.Equal(SimulationClockManagerResult.ObjectRejected, rejected);
        Assert.Equal(SimulationClockManagerResult.PassComplete, resumed);
        Assert.Equal(new short[] { 1, 1, 2 }, seen);
    }

    [Fact]
    public void SpecialUpdatePathDoesNotPretendTheManagerPassCompleted()
    {
        var manager = new SimulationClockManager();
        var item = Object(1);
        item.Flags = 0x00010100u;
        var entry = new SimulationClockManagerEntry(item, 0x00050000u);
        manager.Entries.Add(entry);

        var result = manager.Evaluate(new SimulationClockControl(1, 1, 1), 1, 9, (_, _) => true);

        Assert.Equal(SimulationClockManagerResult.SpecialUpdateRequired, result);
        Assert.Equal(0x00050000u, entry.Flags);
        Assert.Equal(0x00010000u, item.Flags);
    }

    [Fact]
    public void SpecialUpdateLeavesObjectFlagsAloneWhenSourceGateBitIsClear()
    {
        var item = Object(1);
        item.Flags = 0x00000100u;

        Assert.False(item.PrepareClockSpecialUpdate());

        Assert.Equal(0x00000100u, item.Flags);
    }

    [Fact]
    public void ClockStateZeroClearsSpecialFlagBeforeNormalWaitProcessing()
    {
        var manager = new SimulationClockManager();
        var entry = new SimulationClockManagerEntry(Object(1), 0x00050000u);
        manager.Entries.Add(entry);

        var result = manager.Evaluate(new SimulationClockControl(1, 1, 1), 0, 9, (_, _) => true);

        Assert.Equal(SimulationClockManagerResult.PassComplete, result);
        Assert.Equal(0x00010000u, entry.Flags);
    }

    [Fact]
    public void SimulationWorldAdvancesClockOnlyAfterAnAllowedManagerPass()
    {
        var world = new SimulationWorld();
        world.ClockManager.Entries.Add(new SimulationClockManagerEntry(null, 0));

        world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 1, (_, _) => true);

        Assert.Equal(1, world.Clock.Ticks);
        Assert.Equal(2, world.Clock.Second);
    }

    [Fact]
    public void SimulationWorldKeepsClockHeldWhenTheGlobalPredicateIsHeld()
    {
        var world = new SimulationWorld();

        world.AdvanceSimulationTick(new SimulationClockControl(0, 0, 1), 0, 1, (_, _) => true);

        // Simulation ticks increment on every completed object-manager pass, even when the calendar clock
        // is paused.
        Assert.Equal(1, world.Clock.Ticks);
        Assert.Equal(0, world.Clock.Second);
    }

    [Fact]
    public void ObjectManagerCallbacksUseAndPersistTheirOwnRandomStream()
    {
        const uint ambientSeed = 0x87654321;
        var world = new SimulationWorld { RandomState = ambientSeed, ObjectManagerRandomState = 1 };
        world.ClockManager.Entries.Add(new SimulationClockManagerEntry(Object(1), 0x00010000u));

        var result = world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 1,
            (_, _) => { world.NextRandom(); return true; });

        Assert.Equal(1, result);
        Assert.Equal(0x41c67ea6u, world.ObjectManagerRandomState);
        Assert.Equal(ambientSeed, world.RandomState);
        world.NextRandom();
        Assert.Equal(unchecked(ambientSeed * 0x41c64e6du + 0x3039u), world.RandomState);
        Assert.Equal(0x41c67ea6u, world.ObjectManagerRandomState);
    }

    [Fact]
    public void SuccessfulPersonManagerUpdateAppliesMotiveChangesInOrderAndClampsAtTargets()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(3, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        person[Motive.Energy] = 9.5f;
        person[Motive.Comfort] = -4.5f;
        person.MotiveChanges.Add(new MotiveChange(Motive.Energy, 1f, 10f));
        person.MotiveChanges.Add(new MotiveChange(Motive.Energy, -2f, 6f));
        person.MotiveChanges.Add(new MotiveChange(Motive.Comfort, -1f, -5f));
        person.MotiveChanges.Add(new MotiveChange(Motive.Hunger, 1f, 20f));
        person[Motive.Hunger] = 20f;
        world.ClockManager.SetTickEnabled(person, enabled: true);

        var callbacks = world.AdvanceSimulationTick(new SimulationClockControl(1, 1, 1), 0, 0);

        Assert.Equal(1, callbacks);
        Assert.Equal(8f, person[Motive.Energy]);
        Assert.Equal(-5f, person[Motive.Comfort]);
        Assert.Equal(20f, person[Motive.Hunger]);
    }

    [Fact]
    public void PersonBodyScaleUsesTheSourceTenthTickBoundsAndTransientGate()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(3, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        person[PersonData.ScalingSim] = 200;
        world.ClockManager.SetTickEnabled(person, enabled: true);
        var control = new SimulationClockControl(1, 1, 1);

        world.AdvanceSimulationTick(control, 0, 9);
        Assert.Equal(1f, person.AnimatorBodyScale);
        world.AdvanceSimulationTick(control, 0, 10);
        Assert.Equal(2f, person.AnimatorBodyScale);

        person[PersonData.ScalingSim] = 40;
        world.AdvanceSimulationTick(control, 0, 20);
        Assert.Equal(2f, person.AnimatorBodyScale);

        person[PersonData.ScalingSim] = 300;
        person.AnimatorScaleUpdateSuppressed = true;
        world.AdvanceSimulationTick(control, 0, 30);
        Assert.Equal(2f, person.AnimatorBodyScale);

        person.AnimatorScaleUpdateSuppressed = false;
        person.PersonTickGateWord8E = 1;
        world.AdvanceSimulationTick(control, 0, 40);
        Assert.Equal(2f, person.AnimatorBodyScale);

        person.PersonTickGateWord8E = 0;
        world.AdvanceSimulationTick(control, 0, 50);
        Assert.Equal(3f, person.AnimatorBodyScale);
    }
}
