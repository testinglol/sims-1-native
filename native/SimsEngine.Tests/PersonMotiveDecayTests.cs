using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class PersonMotiveDecayTests
{
    [Fact]
    public void AdultAwakeUpdateUsesPriorHungerAndOriginalPersonalityRates()
    {
        var person = new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType));
        person[Motive.Energy] = 100;
        person[Motive.Hunger] = 20;
        person[Motive.Bladder] = 50;
        person[Motive.Comfort] = 50;
        person[Motive.Social] = 50;
        person[Motive.Fun] = 50;
        person[Motive.Hygiene] = 50;
        person.SavedPersonValues7CC[(int)Motive.Hunger] = 10;
        person[PersonData.ActivePlayfulPersonality] = 1000;
        person[PersonData.OutgoingFriendlyPersonality] = 1000;

        Tuning().Apply(person, gameHour: 12);

        Assert.Equal(99.625f, person[Motive.Energy], 5);
        Assert.Equal(19.748f, person[Motive.Hunger], 5);
        Assert.Equal(46.7f, person[Motive.Bladder], 5);
        Assert.Equal(49.6f, person[Motive.Comfort], 5);
        Assert.Equal(49.8196f, person[Motive.Social], 5);
        Assert.Equal(49.75f, person[Motive.Fun], 5);
        Assert.Equal(49.83f, person[Motive.Hygiene], 5);
        Assert.Equal(person[Motive.Hunger], person.SavedPersonValues7CC[(int)Motive.Hunger]);
    }

    [Fact]
    public void SleepingDogGetsTheDogEnergyRateAndDoesNotLoseSocial()
    {
        var person = new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType));
        person[PersonData.Gender] = 2;
        person[Motive.SleepState] = -1;
        person[Motive.Energy] = 0;
        person[Motive.Social] = 42;
        person[Motive.Hygiene] = 50;

        Tuning().Apply(person, gameHour: 12);

        Assert.Equal(40f / 30f - 0.01f, person[Motive.Energy], 5);
        Assert.Equal(42f, person[Motive.Social]);
        Assert.Equal(49.92f, person[Motive.Hygiene], 5);
    }

    [Fact]
    public void NativePersonTickAppliesDecayAtSixtyUpdatesAndHonorsTheSourceGate()
    {
        var world = new SimulationWorld { PersonMotiveDecayTuning = Tuning() };
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        person[Motive.Energy] = 100;
        person[PersonData.MotivesStatic] = 1;

        RunPersonUpdate(world, person, 60);
        Assert.Equal(100f, person[Motive.Energy]);
        Assert.Equal(60, person.MotiveDecayLastTickCounter);

        person[PersonData.MotivesStatic] = 0;
        RunPersonUpdate(world, person, 119);
        Assert.Equal(100f, person[Motive.Energy]);
        RunPersonUpdate(world, person, 120);

        Assert.Equal(99.625f, person[Motive.Energy], 5);
        Assert.Equal(120, person.MotiveDecayLastTickCounter);
    }

    [Fact]
    public void MoodUsesTheAgeSpecificWeightedCurvesAfterMotiveDecay()
    {
        var person = new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType));
        person[PersonData.PersonAge] = 10;
        person[Motive.Hunger] = 20;
        person[Motive.Comfort] = 40;
        person[Motive.Hygiene] = 60;
        person[Motive.Bladder] = 80;
        person[Motive.Fun] = 100;
        person[Motive.Room] = -20;
        person[Motive.Social] = 0;

        var adult = Enumerable.Repeat(Curve(1), 7).ToArray();
        var child = new[] { Curve(10) }.Concat(Enumerable.Repeat(Curve(1), 6)).ToArray();
        (ZeroTuning() with { AdultMoodCurves = adult, ChildMoodCurves = child }).Apply(person, gameHour: 12);

        Assert.Equal(28.75f, person[Motive.Mood], 5);
    }

    [Fact]
    public void MoodCurvesInterpolateBetweenTheirOriginalBreakpoints()
    {
        var curve = PersonMoodWeightCurve.Parse("test", "(-100;15) (-60;5) (-40;3) (0;1) (100;1)");

        Assert.Equal(4f, curve.Evaluate(-50), 5);
        Assert.Equal(1f, curve.Evaluate(100));
    }

    [Fact]
    public void PetMoodOmitsTheDefaultRoomSentinel()
    {
        var person = new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType));
        person[PersonData.Gender] = 2;
        person[Motive.Hunger] = 20;
        person[Motive.Comfort] = 40;
        person[Motive.Hygiene] = 60;
        person[Motive.Bladder] = 80;
        person[Motive.Fun] = 100;
        person[Motive.Room] = 13;
        person[Motive.Social] = 0;
        var curves = Enumerable.Repeat(Curve(1), 7).ToArray();

        (ZeroTuning() with { AdultMoodCurves = curves, ChildMoodCurves = curves }).Apply(person, gameHour: 12);

        Assert.Equal(50f, person[Motive.Mood]);
    }

    [Fact]
    public void SuccessfulPersonUpdateIncrementsTheSourceTickCounterAsASignedWord()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        person[PersonData.TickCounter] = short.MaxValue;

        RunPersonUpdate(world, person, tick: 1);

        Assert.Equal(short.MinValue, person[PersonData.TickCounter]);
    }

    [Fact]
    public void PersonUpdateRefreshesRoomMotiveFromInteriorOutsideAndMissingRoomData()
    {
        var world = new SimulationWorld { OutsidePersonRoomMotiveValue = 18f };
        world.PersonRoomMotiveEntries[4] = new PersonRoomMotiveEntry(-12f, IsOutside: false);
        world.PersonRoomMotiveEntries[5] = new PersonRoomMotiveEntry(999f, IsOutside: true);
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        person.RoomWord84 = 4;
        RunPersonUpdate(world, person, tick: 1);
        Assert.Equal(-12f, person[Motive.Room]);
        Assert.Equal((ushort)4, person.LastObservedRoomIdAFC);

        person.RoomWord84 = 5;
        RunPersonUpdate(world, person, tick: 2);
        Assert.Equal(18f, person[Motive.Room]);

        person.RoomWord84 = 6;
        RunPersonUpdate(world, person, tick: 3);
        Assert.Equal(0f, person[Motive.Room]);
    }

    [Fact]
    public void RoomZeroClampsNegativeMotiveOutsideStateClassOneAndSkipsOffLotSentinel()
    {
        var world = new SimulationWorld();
        world.PersonRoomMotiveEntries[0] = new PersonRoomMotiveEntry(-20f, IsOutside: false);
        world.NeighborInteractionGlobalStateValue = 7;
        world.StateClasses.Add(new StateClassEntry(7, 2));
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        person.RoomWord84 = 0;
        RunPersonUpdate(world, person, tick: 1);
        Assert.Equal(0f, person[Motive.Room]);

        person.RoomWord84 = -5;
        person[Motive.Room] = 42f;
        RunPersonUpdate(world, person, tick: 2);
        Assert.Equal(42f, person[Motive.Room]);
        Assert.Equal((ushort)0xfffb, person.LastObservedRoomIdAFC);
    }

    private static void RunPersonUpdate(SimulationWorld world, XPerson person, int tick)
    {
        var tree = Tree();
        var stack = new ScriptedStack();
        Assert.True(stack.Push(new Table(tree), [], false, 4096));
        world.SavedExecutionStacks[person.Id] = stack;
        Assert.Equal(1, world.TickSavedExecutionStacks(tick));
    }

    private static PersonMotiveDecayTuning Tuning() => new(
        new PersonMotiveDecayProfile(180, 16, 7, 0.01f, 0.3f, 0.4f, 0.6f, 0.0021f,
            0.055f, 0.0001254f, 0.25f, 1f, 0.17f, 0.08f, 0.3f, 0.15f, false),
        new PersonMotiveDecayProfile(20, 40, 10, 0.01f, 0.3f, 0.1f, 0.1f, 0.0041f,
            0.055f, 0.000125f, 0.1f, 1f, 0.09f, 0.08f, 0.3f, 0.15f, true),
        new PersonMotiveDecayProfile(25, 66, 7, 0, 0.5f, 0.1f, 0.3f, 0.0041f,
            0.055f, 0.0001f, 0.35f, 1f, 0.025f, 0.01f, 0.3f, 0.15f, true));

    private static PersonMotiveDecayTuning ZeroTuning()
    {
        var profile = new PersonMotiveDecayProfile(0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, false);
        return new PersonMotiveDecayTuning(profile, profile with { IsPet = true }, profile with { IsPet = true });
    }

    private static PersonMoodWeightCurve Curve(float weight) => new([(-100f, weight), (100f, weight)]);

    private sealed class Table(Bhav tree) : ITreeTable
    {
        public Bhav? Find(short id) => id == 4096 ? tree : null;
    }

    private sealed class ScriptedStack : TreeStack
    {
        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction) => PrimitiveResult.Stop;
    }

    private static Bhav Tree()
    {
        var body = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(0).U8(0xFE).U8(0xFF).Raw(new byte[8]);
        var (file, chunk) = body.InFile("BHAV", 4096);
        return Bhav.Read(file, chunk);
    }
}
