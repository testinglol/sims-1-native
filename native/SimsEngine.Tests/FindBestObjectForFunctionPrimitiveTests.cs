using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class FindBestObjectForFunctionPrimitiveTests
{
    private const short MeId = 100;
    private const short Function0 = 0x12;
    private const short Function11 = 0x1c;
    private const short FunctionSit = 0x1a;

    // Operand: the selector (word 0), the owner's data (word 2), the owner's scope (word 4) and the flags (byte 6).
    private static byte[] Operand(short selector, byte flags = 0, short data = 0, short owner = 0) =>
        [(byte)selector, (byte)(selector >> 8), (byte)data, (byte)(data >> 8), (byte)owner, (byte)(owner >> 8), flags, 0];

    /// <summary>A stack whose only primitive is the check tree's: it answers the given result for every opcode.</summary>
    private sealed class CheckStack(bool answer) : TreeStack
    {
        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction) =>
            answer ? PrimitiveResult.True : PrimitiveResult.False;
    }

    /// <summary>The running person 100 (no room yet) on the lot, with a frame whose stack object is 0.</summary>
    private static (TreeContext Context, XPerson Me, PrimitiveTestHost Host, SimulationWorld World) Lot(bool checkAnswer = true)
    {
        var host = new PrimitiveTestHost();
        var world = new SimulationWorld { Host = host };
        var me = world.Add(new XPerson(MeId, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        var context = new TreeContext(world, me, new StackFrame(0, [], [0, 0])) { Stack = new CheckStack(checkAnswer) };
        return (context, me, host, world);
    }

    /// <summary>A candidate with <paramref name="points"/> in data field <paramref name="field"/>, at X <paramref name="x"/>,
    /// with a function entry for <paramref name="function"/> that has no check tree.</summary>
    private static XObject Candidate(SimulationWorld world, short id, short field, short points, int x, short function = Function0)
    {
        var item = world.Add(new XObject(id, PrimitiveTestWorld.Definition()));
        item.Data[field] = points;
        item.Position = new ObjectPosition(x, 0, 0);
        item.TypeRecord = new TypeRecord(id) { FunctionTable = FunctionTable(function, 1), FunctionCheckTable = FunctionTable(function, 0) };
        return item;
    }

    private static ushort[] FunctionTable(short function, ushort tree)
    {
        var entries = new ushort[30];
        entries[function] = tree;
        return entries;
    }

    private static void AddRoomRecord(SimulationWorld world, short room, bool outside = false)
    {
        world.PersonRoomMotiveEntries[unchecked((ushort)room)] = new PersonRoomMotiveEntry(0, outside);
        world.RoomManagerRecordsLoaded = true;
    }

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Fact]
    public void TheHighestPointsCandidateWinsAndBecomesTheStackObject()
    {
        var (context, _, host, world) = Lot();
        Candidate(world, 2, 31, 40, 0);
        Candidate(world, 3, 31, 60, 0);
        Assert.Equal(PrimitiveResult.True, FindBestObjectForFunctionPrimitive.Execute(context, Operand(0)));
        Assert.Equal(3, context.Frame.StackObject);
    }

    [Fact]
    public void ACandidateWithAZeroScoreFieldIsSkipped()
    {
        var (context, _, host, world) = Lot();
        Candidate(world, 2, 31, 0, 0);
        Assert.Equal(PrimitiveResult.False, FindBestObjectForFunctionPrimitive.Execute(context, Operand(0)));
    }

    [Fact]
    public void TheDistanceTakesThreeTimesEachTileFromThePoints()
    {
        var (context, _, host, world) = Lot();
        // Candidate 2 is 16 units away (distance 1) with 50 points: 47. Candidate 3 is beside the running person: 45.
        Candidate(world, 2, 31, 50, 16);
        Candidate(world, 3, 31, 45, 0);
        FindBestObjectForFunctionPrimitive.Execute(context, Operand(0));
        Assert.Equal(2, context.Frame.StackObject);
    }

    [Fact]
    public void SelectorElevenHalvesTheDistanceAboveOne()
    {
        var (context, _, host, world) = Lot();
        // Candidate 2 is 4 tiles away with 40 points: halved to 2, so 34. Candidate 3 scores 30 beside the person.
        Candidate(world, 2, 39, 40, 64, Function11);
        Candidate(world, 3, 39, 30, 0, Function11);
        FindBestObjectForFunctionPrimitive.Execute(context, Operand(11));
        Assert.Equal(2, context.Frame.StackObject);
    }

    [Fact]
    public void AScoreBelowOneIsOne()
    {
        var (context, _, host, world) = Lot();
        Candidate(world, 2, 31, 2, 160);
        Assert.Equal(PrimitiveResult.True, FindBestObjectForFunctionPrimitive.Execute(context, Operand(0)));
        Assert.Equal(2, context.Frame.StackObject);
    }

    [Fact]
    public void ACheckTreeThatAnswersFalseSkipsTheCandidate()
    {
        var (context, _, host, world) = Lot(checkAnswer: false);
        var candidate = Candidate(world, 2, 31, 40, 0);
        candidate.TypeRecord = new TypeRecord(1)
        {
            FunctionTable = FunctionTable(Function0, 1),
            FunctionCheckTable = FunctionTable(Function0, 9),
            BehaviorScope = new TreeTable((9, PrimitiveTestWorld.Tree(0, (2, 0xFE, 0xFF, new byte[8])))),
        };
        Assert.Equal(PrimitiveResult.False, FindBestObjectForFunctionPrimitive.Execute(context, Operand(0)));
    }

    [Fact]
    public void ACheckTreeThatAnswersTrueKeepsTheCandidate()
    {
        var (context, _, host, world) = Lot(checkAnswer: true);
        var candidate = Candidate(world, 2, 31, 40, 0);
        candidate.TypeRecord = new TypeRecord(1)
        {
            FunctionTable = FunctionTable(Function0, 1),
            FunctionCheckTable = FunctionTable(Function0, 9),
            BehaviorScope = new TreeTable((9, PrimitiveTestWorld.Tree(0, (2, 0xFE, 0xFF, new byte[8])))),
        };
        Assert.Equal(PrimitiveResult.True, FindBestObjectForFunctionPrimitive.Execute(context, Operand(0)));
        Assert.Equal(2, context.Frame.StackObject);
    }

    [Fact]
    public void FlagOneSkipsObjectsInExistingRoomsThatAreNotOutside()
    {
        var (context, _, host, world) = Lot();
        var indoor = Candidate(world, 2, 31, 40, 0);
        Candidate(world, 3, 31, 30, 0);
        indoor[ObjectData.Room] = 4;
        AddRoomRecord(world, 4);
        FindBestObjectForFunctionPrimitive.Execute(context, Operand(0, flags: 1));
        Assert.Equal(3, context.Frame.StackObject);
    }

    [Fact]
    public void FlagTwoSkipsObjectsWithoutARoomAndInOutsideRooms()
    {
        var (context, _, _, world) = Lot();
        Candidate(world, 2, 31, 40, 0);
        var outside = Candidate(world, 3, 31, 30, 0);
        var inside = Candidate(world, 4, 31, 20, 0);
        outside[ObjectData.Room] = 3;
        inside[ObjectData.Room] = 4;
        AddRoomRecord(world, 3, outside: true);
        AddRoomRecord(world, 4);
        FindBestObjectForFunctionPrimitive.Execute(context, Operand(0, flags: 2));
        Assert.Equal(4, context.Frame.StackObject);
    }

    [Fact]
    public void FlagFourKeepsTheRunningObjectsRoom()
    {
        var (context, _, host, world) = Lot();
        var otherRoom = Candidate(world, 2, 31, 40, 0);
        var sameRoom = Candidate(world, 3, 31, 30, 0);
        context.World.ObjectById(MeId)![ObjectData.Room] = 4;
        otherRoom[ObjectData.Room] = 5;
        sameRoom[ObjectData.Room] = 4;
        FindBestObjectForFunctionPrimitive.Execute(context, Operand(0, flags: 4));
        Assert.Equal(3, context.Frame.StackObject);
    }

    [Fact]
    public void FlagEightTakesTheOwnersRoomForFlagFour()
    {
        var (context, _, host, world) = Lot();
        var owner = world.Add(new XObject(7, PrimitiveTestWorld.Definition()));
        var matching = Candidate(world, 2, 31, 40, 0);
        var nonmatching = Candidate(world, 3, 31, 30, 0);
        context.World.ObjectById(MeId)![ObjectData.Room] = 4;
        owner[ObjectData.Room] = 5;
        matching[ObjectData.Room] = 4;
        nonmatching[ObjectData.Room] = 5;
        // The owner is the Literal owner (7) with the value 7, in room 5; flag 4 then matches room 5, not the person's 4.
        Assert.Equal(PrimitiveResult.True, FindBestObjectForFunctionPrimitive.Execute(context, Operand(0, flags: 12, data: 7, owner: 7)));
        Assert.Equal(3, context.Frame.StackObject);
    }

    [Fact]
    public void AMissingOwnerIsNotAnObjectReference()
    {
        var (context, _, host, world) = Lot();
        Candidate(world, 2, 31, 40, 0);
        Assert.Equal(SimulationError.CouldNotFindObjectReference, Fails(() => FindBestObjectForFunctionPrimitive.Execute(context, Operand(0, flags: 8, data: 9, owner: 7))));
    }

    [Fact]
    public void SelectorFifteenKeepsOnlyClassFourAndScoresFifty()
    {
        var (context, _, host, world) = Lot();
        var categoryFour = Candidate(world, 2, 15, 0, 0, 0);
        var categoryThree = Candidate(world, 3, 15, 0, 0, 0);
        categoryThree[ObjectData.Category] = 3;
        categoryFour[ObjectData.Category] = 4;
        Assert.Equal(PrimitiveResult.True, FindBestObjectForFunctionPrimitive.Execute(context, Operand(15)));
        Assert.Equal(2, context.Frame.StackObject);
    }

    [Fact]
    public void SitNeedsAPersonWhoseFieldIsAtMostOne()
    {
        var (context, me, host, world) = Lot();
        Candidate(world, 2, 31, 40, 0, FunctionSit);
        me.PersonData[65] = 2;
        Assert.Equal(PrimitiveResult.False, FindBestObjectForFunctionPrimitive.Execute(context, Operand(8)));
        Assert.Empty(host.Calls);

        me.PersonData[65] = 1;
        Assert.Equal(PrimitiveResult.True, FindBestObjectForFunctionPrimitive.Execute(context, Operand(8)));
        Assert.Equal(2, context.Frame.StackObject);
    }

    [Fact]
    public void AnUnknownSelectorAnswersFalseWithoutSearching()
    {
        var (context, _, host, world) = Lot();
        Candidate(world, 2, 31, 40, 0);
        Assert.Equal(PrimitiveResult.False, FindBestObjectForFunctionPrimitive.Execute(context, Operand(4)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CandidateWithPositiveLockoutCountIsNotSearched()
    {
        var (context, _, host, world) = Lot();
        var busy = Candidate(world, 2, 31, 40, 0);
        Candidate(world, 3, 31, 30, 0);
        busy[ObjectData.LockoutCount] = 1;
        FindBestObjectForFunctionPrimitive.Execute(context, Operand(0));
        Assert.Equal(3, context.Frame.StackObject);
    }

    [Fact]
    public void PositiveUseCountDoesNotReplaceTheLockoutCountGate()
    {
        var (context, _, _, world) = Lot();
        var candidate = Candidate(world, 2, 31, 40, 0);
        candidate[ObjectData.UseCount] = 1;

        Assert.Equal(PrimitiveResult.True, FindBestObjectForFunctionPrimitive.Execute(context, Operand(0)));
        Assert.Equal(2, context.Frame.StackObject);
    }

    [Fact]
    public void CandidateWithObjectFlag200IsNotSearched()
    {
        var (context, _, host, world) = Lot();
        var flagged = Candidate(world, 2, 31, 40, 0);
        Candidate(world, 3, 31, 30, 0);
        flagged[ObjectData.Flags] = 0x200;

        Assert.Equal(PrimitiveResult.True, FindBestObjectForFunctionPrimitive.Execute(context, Operand(0)));
        Assert.Equal(3, context.Frame.StackObject);
    }
}
