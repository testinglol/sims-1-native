using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class DeleteCharacterTests
{
    private static NeighborState Neighbor(short id, int guid, uint? flags, short family = 0, short lingering = 0)
    {
        var neighbor = new NeighborState(id, []) { TypeRecordKey = guid, FriendEligibilityObjectFlagsB4 = flags };
        neighbor.PersonData[(int)PersonData.FamilyNumber] = family;
        neighbor.PersonData[(int)PersonData.LingeringHouseNumber] = lingering;
        return neighbor;
    }

    private static SimulationWorld World(params NeighborState?[] neighbors)
    {
        var world = new SimulationWorld();
        foreach (var neighbor in neighbors)
        {
            world.NeighborWalkOrder.Add(neighbor);
            if (neighbor is not null) world.Neighbors[neighbor.Id] = neighbor;
        }
        return world;
    }

    [Fact]
    public void SaveHouseSweepDeletesOnlyCharactersWithNoFamilyOrLingeringHouse()
    {
        var orphan = Neighbor(1, 101, 4);
        var housed = Neighbor(2, 102, 4, family: 3);
        var ghost = Neighbor(3, 103, 4, lingering: 7);
        var townie = Neighbor(4, 104, 0);
        housed.Relationships.Resize(1, 2);
        housed.Relationships.Resize(4, 1);
        var world = World(orphan, housed, null, ghost, townie);
        var inventory = world.NeighborInventory(1);
        inventory.Add(2, 55, 1);
        inventory.Add(12, 56, 1);

        var deleted = world.DeleteUnhousedCharacters(_ => false);

        Assert.Equal(new short[] { 1 }, deleted);
        Assert.Null(world.NeighborWalkOrder[0]);
        Assert.Equal(5, world.NeighborWalkOrder.Count);
        Assert.False(world.Neighbors.ContainsKey(1));
        Assert.Equal(0, housed.Relationships.Count(1));
        Assert.Equal(1, housed.Relationships.Count(4));
        Assert.Equal(new[] { 12 }, inventory.Entries.Select(entry => entry.Key1));
        Assert.True(world.RelationshipModuleDirtyFlag12);
    }

    [Fact]
    public void FamilyMemberIsRefusedAndEndsTheSweep()
    {
        var world = World(Neighbor(1, 101, 4), Neighbor(2, 102, 4));

        Assert.Empty(world.DeleteUnhousedCharacters(guid => guid == 101));
        Assert.Equal(2, world.Neighbors.Count);
        Assert.False(world.DeleteCharacter(Neighbor(5, 105, null), _ => false));
    }

    [Fact]
    public void DeletingCharacterKillsItsLotObjectThroughObjectModuleNoticePath()
    {
        var neighbor = Neighbor(1, 101, 4);
        var world = World(neighbor);
        var person = world.Add(new XObject(10, new short[XObject.DefinitionCount]) { TypeRecord = new TypeRecord(101) });
        var notices = new List<(int Message, int Value)>();
        world.AddNoticeListener(new SimulationNoticeListener((message, value) =>
        {
            notices.Add((message, value));
            return false;
        }));

        Assert.True(world.DeleteCharacter(neighbor, _ => false));
        Assert.Null(world.ObjectById(person.Id));
        Assert.Equal([(0x87, (int)person.Id), (0x88, (int)person.Id), (0xfb, (int)neighbor.Id)], notices);
    }
}
