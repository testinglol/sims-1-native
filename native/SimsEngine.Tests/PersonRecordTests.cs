using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class PersonRecordTests
{
    private static readonly int[] SavedFields =
    [
        2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 16, 20, 15, 26, 17, 18, 46, 47, 48, 49, 50, 51, 52, 53,
        13, 14, 58, 60, 61, 56, 57, 63, 65, 67, 68, 69, 74, 54, 55, 75, 76, 80, 81, 82, 83, 84, 85, 86,
    ];

    private static short[] Definition()
    {
        var definition = new short[XObject.DefinitionCount];
        definition[9] = XPerson.PersonType;
        return definition;
    }

    [Fact]
    public void SavesOnlyTheVersionTenPersonFieldsToTheMatchingNeighbor()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(7, Definition()));
        for (var field = 0; field < person.PersonData.Length; field++) person.PersonData[field] = (short)(field + 100);
        person[PersonData.NeighborId] = 4;
        var neighbor = new NeighborState(4, Definition());
        Array.Fill(neighbor.PersonData, (short)-1);
        world.Neighbors[4] = neighbor;

        world.SavePersonRecord(person);

        Assert.Equal(10, neighbor.PersonRecordVersion);
        var saved = SavedFields.ToHashSet();
        for (var field = 0; field < neighbor.PersonData.Length; field++)
            Assert.Equal(saved.Contains(field) ? person.PersonData[field] : (short)-1, neighbor.PersonData[field]);
    }

    [Fact]
    public void MissingNeighborRecordLeavesThePersonAndWorldUnchanged()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(7, Definition()));
        person.PersonData[2] = 91;
        var invalidZero = new NeighborState(0, Definition());
        world.Neighbors[0] = invalidZero;

        world.SavePersonRecord(person);

        Assert.Equal(0, invalidZero.PersonRecordVersion);
        Assert.All(invalidZero.PersonData, value => Assert.Equal((short)0, value));
        Assert.Equal((short)91, person.PersonData[2]);
    }

    [Fact]
    public void WritingUninstantiatedNeighborDataMarksItForNeighborhoodSave()
    {
        var world = new SimulationWorld();
        var neighbor = new NeighborState(4, Definition());
        world.Neighbors[4] = neighbor;

        world.SetPersistentNeighborPersonData(neighbor, PersonData.Zodiac, 0);
        Assert.False(world.PersistentPersonDataDirty);
        world.SetPersistentNeighborPersonData(neighbor, PersonData.Zodiac, 11);
        Assert.Equal(11, neighbor.PersonData[(int)PersonData.Zodiac]);
        Assert.True(world.PersistentPersonDataDirty);
    }

    [Fact]
    public void RelationshipZodiacUsesLivePersonFirstAndOnlyInitializesZero()
    {
        var world = new SimulationWorld();
        var neighbor = new NeighborState(4, Definition());
        world.Neighbors[4] = neighbor;
        var person = world.Add(new XPerson(7, Definition()));
        person[PersonData.NeighborId] = 4;
        neighbor.PersonData[(int)PersonData.Zodiac] = 3;
        Assert.Equal(11, world.InitializeRelationshipZodiac(neighbor, true, _ => 11));
        Assert.Equal(11, person[PersonData.Zodiac]);
        Assert.Equal(3, neighbor.PersonData[(int)PersonData.Zodiac]);
        Assert.False(world.PersistentPersonDataDirty);
        Assert.Equal(11, world.InitializeRelationshipZodiac(neighbor, true, _ => 8));
    }

    [Fact]
    public void RelationshipZodiacWritesAnUninstantiatedNamedNeighbor()
    {
        var world = new SimulationWorld();
        var neighbor = new NeighborState(4, Definition());
        world.Neighbors[4] = neighbor;
        Assert.Equal(0, world.InitializeRelationshipZodiac(neighbor, false, _ => 11));
        Assert.False(world.PersistentPersonDataDirty);
        Assert.Equal(11, world.InitializeRelationshipZodiac(neighbor, true, _ => 11));
        Assert.Equal(11, neighbor.PersonData[(int)PersonData.Zodiac]);
        Assert.True(world.PersistentPersonDataDirty);
    }

    [Fact]
    public void PersonRecordDataLazilyResolvesAndCachesTheNeighborRecord()
    {
        var world = new SimulationWorld();
        world.PersonRecordKeys.Add(1234);
        world.PersonRecordNeighborIds.Add(null);
        var neighbor = new NeighborState(4, Definition()) { TypeRecordKey = 1234 };
        neighbor.PersonData[(int)PersonData.FameStarPower09] = 6;
        world.Neighbors[neighbor.Id] = neighbor;
        world.NeighborWalkOrder.Add(neighbor);

        Assert.Same(neighbor.PersonData, world.PersonRecordData(0));
        Assert.Equal((short)4, world.PersonRecordNeighborIds[0]);
        Assert.Equal((short)6, world.PersonRecordData(0)![(int)PersonData.FameStarPower09]);
    }
}
