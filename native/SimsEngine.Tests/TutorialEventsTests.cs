using SimsEngine.Simulation;
using SimsEngine.Content;
using SimsEngine.Formats;
using System.Text;
using Xunit;

namespace SimsEngine.Tests;

public sealed class TutorialEventsTests
{
    [Theory]
    [InlineData(1, true, null, true)]
    [InlineData(1, false, "motives", false)]
    [InlineData(2, true, null, false)]
    [InlineData(2, true, "motives", true)]
    [InlineData(9, true, null, false)]
    [InlineData(9, true, "rel", true)]
    [InlineData(9, true, "job", false)]
    [InlineData(11, true, null, false)]
    [InlineData(11, true, "job", true)]
    [InlineData(11, true, "rel", false)]
    [InlineData(13, true, "skill", true)]
    [InlineData(14, false, null, true)]
    [InlineData(14, true, "job", false)]
    [InlineData(14, true, "house", false)]
    [InlineData(15, true, "per.ity", true)]
    [InlineData(5, true, "motives", false)]
    [InlineData(0, true, null, false)]
    [InlineData(3, true, null, false)]
    [InlineData(10, true, "job", false)]
    [InlineData(12, true, "rel", false)]
    public void MatchesMacPopupCases(short kind, bool live, string? popup, bool expected) =>
        Assert.Equal(expected, TutorialEvents.PopupEventMatches(kind, live, popup));

    private static (TreeContext Context, XObject Owner) Fixture(short kind, short data)
    {
        static byte[] Assignment(short temporary, short value) =>
            new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
                .U16(2).U8(0xFE).U8(0xFF)
                .Raw([(byte)temporary, 0, (byte)value, (byte)(value >> 8), 0, 5, 8, 7]).ToArray();
        static byte[] Two(short kind, short data) =>
            new ChunkBuilder().U16(0x8002).S16(2).U8(0).U8(0).U8(0).U8(0).U32(0)
                .U16(2).U8(1).U8(0xFF).Raw([0, 0, (byte)kind, (byte)(kind >> 8), 0, 5, 8, 7])
                .U16(2).U8(0xFE).U8(0xFF).Raw([1, 0, (byte)data, (byte)(data >> 8), 0, 5, 8, 7]).ToArray();
        var first = Two(kind, data);
        var second = Assignment(2, 77);
        var bytes = ChunkBuilder.File(("BHAV", 4096, first), ("BHAV", 4097, second));
        Encoding.Latin1.GetBytes("query wait event").CopyTo(bytes, IffFile.HeaderSize + 12);
        Encoding.Latin1.GetBytes("got wait event").CopyTo(bytes, IffFile.HeaderSize + IffFile.ChunkHeaderSize + first.Length + 12);
        var file = new IffFile(bytes, "tutorial-fixture.iff");
        var (context, owner, _) = PrimitiveTestWorld.Create();
        owner.TypeRecord = new TypeRecord(1) { BehaviorScope = new BehaviorScope(file, file, null) };
        context.World.TutorialOwner = owner;
        return (context, owner);
    }

    [Fact]
    public void QueryAndDeliveryUseTheTutorialOwnersNamedTrees()
    {
        var (context, owner) = Fixture(2, 0);
        var events = new TutorialEvents();
        Assert.False(events.Check(context.World, new TutorialUiState { Live = true, Popup = "job" }));
        Assert.Equal(0, owner.Temporaries[2]);
        Assert.True(events.Check(context.World, new TutorialUiState { Live = true, Popup = "motives" }));
        Assert.Equal(77, owner.Temporaries[2]);
    }

    [Fact]
    public void ButtonPressMatchesTheLastButtonsImageAndSetsGlobal12()
    {
        var (context, owner) = Fixture(4, 4501);
        var events = new TutorialEvents();
        Assert.False(events.Check(context.World, new TutorialUiState { LastButtonImage = 2010 }));
        Assert.Equal(2010, context.World[SimulationGlobal.LastGZButtonID]);
        Assert.True(events.Check(context.World, new TutorialUiState { LastButtonImage = 4501 }));
        Assert.Equal(4501, context.World[SimulationGlobal.LastGZButtonID]);
        events.Check(context.World, new TutorialUiState());
        Assert.Equal(0, context.World[SimulationGlobal.LastGZButtonID]);
    }

    [Fact]
    public void LastButtonImageIsWrittenEvenWhenThereIsNoTutorialOwner()
    {
        var world = new SimulationWorld();
        var events = new TutorialEvents();

        Assert.False(events.Check(world, new TutorialUiState { LastButtonImage = 4501 }));
        Assert.Equal(4501, world[SimulationGlobal.LastGZButtonID]);
        Assert.False(events.Check(world, new TutorialUiState()));
        Assert.Equal(0, world[SimulationGlobal.LastGZButtonID]);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public void RotationAndScrollWaitForTheValueToChange(short kind)
    {
        var (context, owner) = Fixture(kind, 0);
        var events = new TutorialEvents();
        TutorialUiState State(int value) => kind == 6 ? new() { Rotation = value } : new() { Scrolls = value };
        Assert.False(events.Check(context.World, State(3)));
        Assert.Equal(3, events.Watched);
        Assert.False(events.Check(context.World, State(3)));
        Assert.True(events.Check(context.World, State(0)));
        Assert.Equal(-1, events.Watched);
        Assert.Equal(77, owner.Temporaries[2]);
    }

    [Fact]
    public void ButtonStateMatchesASetButtonWithTheImage()
    {
        var (context, _) = Fixture(8, 4500);
        var events = new TutorialEvents();
        Assert.False(events.Check(context.World, new TutorialUiState { SetButtons = [4504] }));
        Assert.True(events.Check(context.World, new TutorialUiState { SetButtons = [4500] }));
    }

    [Fact]
    public void PersonButtonMatchesTheNeighborIdentityRatherThanTheSharedSimDefinition()
    {
        var (context, owner) = Fixture(5, 17);
        var simDefinition = PrimitiveTestWorld.Definition(XPerson.PersonType);
        const int sharedPersonTypeKey = 0x1234;
        var clickedOther = context.World.Add(new XPerson(100, simDefinition) { TypeRecord = new TypeRecord(sharedPersonTypeKey) });
        clickedOther[PersonData.NeighborId] = 18;
        var clickedTarget = context.World.Add(new XPerson(101, simDefinition) { TypeRecord = new TypeRecord(sharedPersonTypeKey) });
        clickedTarget[PersonData.NeighborId] = 17;
        context.World.Neighbors.Add(17, new NeighborState(17, simDefinition) { TypeRecordKey = sharedPersonTypeKey });
        var events = new TutorialEvents();
        context.World[SimulationGlobal.LastGZButtonID] = 2010;

        Assert.False(events.Check(context.World, new TutorialUiState { LastButtonPerson = clickedOther.Id }));
        Assert.Equal(0, context.World[SimulationGlobal.LastGZButtonID]);
        Assert.True(events.Check(context.World, new TutorialUiState { LastButtonPerson = clickedTarget.Id }));
        Assert.Equal(77, owner.Temporaries[2]);
    }
}
