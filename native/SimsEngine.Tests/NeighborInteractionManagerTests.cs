using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class NeighborInteractionManagerTests
{
    [Fact]
    public void RefreshUsesNativeTypeRecordKeyVectorAndKeepsTheSourceSelectionWordRules()
    {
        var world = new SimulationWorld();
        world.NeighborInteractionManagerTypeKeys.Add(20);
        var unlisted = Add(world, 1, key: 10, selection: 0);
        var listed = Add(world, 2, key: 20, selection: 3);
        var retained = Add(world, 3, key: 30, selection: -2);

        world.RefreshNeighborInteractionManager();

        Assert.Equal((short)1, unlisted.SelectionWord5CC);
        Assert.Equal((short)0, listed.SelectionWord5CC);
        Assert.Equal((short)-2, retained.SelectionWord5CC);
    }

    [Fact]
    public void RefreshWalksOnlyThePersonList()
    {
        var world = new SimulationWorld();
        var item = world.Add(new XObject(1, new short[XObject.DefinitionCount]) { SelectionWord5CC = 0 });

        world.RefreshNeighborInteractionManager();

        Assert.Equal((short)0, item.SelectionWord5CC);
    }

    [Fact]
    public void NonClassOneGlobalStateRestoresTheListedObjectsSelectionWord()
    {
        var world = new SimulationWorld();
        world.NeighborInteractionManagerTypeKeys.Add(20);
        world.NeighborInteractionGlobalStateValue = 7;
        world.StateClasses.Add(new StateClassEntry(7, 2));
        var listed = Add(world, 1, key: 20, selection: 3);

        world.RefreshNeighborInteractionManager();

        Assert.Equal((short)1, listed.SelectionWord5CC);
    }

    private static XObject Add(SimulationWorld world, short id, int key, short selection)
    {
        var item = new XPerson(id, new short[XObject.DefinitionCount])
        {
            TypeRecord = new TypeRecord(key),
            SelectionWord5CC = selection,
        };
        return world.Add(item);
    }
}
