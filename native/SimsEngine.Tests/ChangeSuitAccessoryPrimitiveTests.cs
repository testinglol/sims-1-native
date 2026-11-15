using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class ChangeSuitAccessoryPrimitiveTests
{
    // Index, mode and flags. Bytes 3 to 7 are not read, so they hold a marker value.
    private static byte[] Operand(byte index, byte mode, byte flags) => [index, mode, flags, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    private static BehaviorScope SuitScope(string? privateEntry = "suit-book101", string? globalEntry = "global-suit")
    {
        static byte[] Table(string? entry)
        {
            var builder = new ChunkBuilder().S16(-1).S16((short)(entry is null ? 0 : 1));
            if (entry is not null) builder.CString(entry);
            return builder.ToArray();
        }
        var privateFile = new IffFile(ChunkBuilder.File(("STR#", (ushort)304, Table(privateEntry))), "object.iff");
        var globalFile = new IffFile(ChunkBuilder.File(("STR#", (ushort)304, Table(globalEntry))), "global.iff");
        return new BehaviorScope(globalFile, privateFile, null);
    }

    private static TreeContext WithSuitScope(TreeContext original, XPerson person, BehaviorScope? scope, short tree = 4096)
    {
        person.TypeRecord = new TypeRecord(1) { BehaviorScope = scope };
        return new TreeContext(original.World, person, new StackFrame(0, [], [0, 0]) { Table = scope, TreeId = tree });
    }

    private static PersonCharacter Character(params (string, string)[] extraParts) =>
        new(0, 0, Enumerable.Repeat(("", ""), 5).ToArray(), extraParts, [], null, []);

    private static (TreeContext Context, XObject Me) NonPerson()
    {
        var world = new SimulationWorld { Host = new PrimitiveTestHost() };
        var me = world.Add(new XObject(100, PrimitiveTestWorld.Definition(0)));
        return (new TreeContext(world, me, new StackFrame(0, [], [0, 0])), me);
    }

    [Fact]
    public void ObjectScopeBindsTheOneBasedAppearanceAndPreservesExistingParts()
    {
        var (original, person, host) = PrimitiveTestWorld.Create();
        person.SavedCharacter = Character(("other.apr", "keep"));
        var context = WithSuitScope(original, person, SuitScope());
        Assert.Equal(PrimitiveResult.True, ChangeSuitAccessoryPrimitive.Execute(context, Operand(0, 2, 0)));
        Assert.Equal(new[] { ("other.apr", "keep"), ("suit-book101.apr", "") }, person.SavedCharacter!.ExtraParts);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ObjectScopeUsesCodeOwnersPrivateFileEvenWhenRunningAGlobalTree()
    {
        var (original, person, host) = PrimitiveTestWorld.Create();
        person.SavedCharacter = Character();
        var context = WithSuitScope(original, person, SuitScope("object-suit", "global-suit"), tree: 4106);
        Assert.Equal(PrimitiveResult.True, ChangeSuitAccessoryPrimitive.Execute(context, Operand(0, 2, 0)));
        Assert.Equal(new[] { ("object-suit.apr", "") }, person.SavedCharacter!.ExtraParts);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void RemoveFlagRemovesOnlyTheMatchingAppearance()
    {
        var (original, person, host) = PrimitiveTestWorld.Create();
        person.SavedCharacter = Character(("suit-book101", "preserve second value"), ("other.apr", "keep"));
        var context = WithSuitScope(original, person, SuitScope());
        Assert.Equal(PrimitiveResult.True, ChangeSuitAccessoryPrimitive.Execute(context, Operand(0, 2, 1)));
        Assert.Equal(new[] { ("other.apr", "keep") }, person.SavedCharacter!.ExtraParts);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void GlobalScopeReadsTheGlobalSuitBank()
    {
        var (original, person, host) = PrimitiveTestWorld.Create();
        person.SavedCharacter = Character();
        var context = WithSuitScope(original, person, SuitScope("private-suit", "global-suit"));
        Assert.Equal(PrimitiveResult.True, ChangeSuitAccessoryPrimitive.Execute(context, Operand(0, 0, 0)));
        Assert.Equal(new[] { ("global-suit.apr", "") }, person.SavedCharacter!.ExtraParts);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void MissingSuitBankIsInvalidLocation()
    {
        var (original, person, _) = PrimitiveTestWorld.Create();
        person.SavedCharacter = Character();
        var context = WithSuitScope(original, person, null);
        Assert.Equal(SimulationError.InvalidSuitLocation, Fails(() => ChangeSuitAccessoryPrimitive.Execute(context, Operand(0, 0, 0))));
    }

    [Fact]
    public void EmptyEntryIsInvalidSuitIndexAndNothingIsApplied()
    {
        var (original, person, host) = PrimitiveTestWorld.Create();
        person.SavedCharacter = Character();
        var context = WithSuitScope(original, person, SuitScope());
        Assert.Equal(SimulationError.InvalidSuitIndex, Fails(() => ChangeSuitAccessoryPrimitive.Execute(context, Operand(7, 2, 0))));
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(0xFF)]
    public void OtherModesAreInvalidSuitLocation(int mode)
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Equal(SimulationError.InvalidSuitLocation, Fails(() => ChangeSuitAccessoryPrimitive.Execute(context, Operand(0, (byte)mode, 0))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ModeOneWithFlagBitZeroAnswersTrueAndChangesNothing()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me[PersonData.CurrentOutfit] = 5;
        me[PersonData.CostumeTrunkOffsetHouseParty] = 3;
        Assert.Equal(PrimitiveResult.True, ChangeSuitAccessoryPrimitive.Execute(context, Operand(9, 1, 1)));
        Assert.Equal(5, me[PersonData.CurrentOutfit]);
        Assert.Equal(3, me[PersonData.CostumeTrunkOffsetHouseParty]);
        Assert.Equal(0, host.RandomCalls);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void LowIndexClearsTheVariantAndSetsTheOutfitWithoutDrawing()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me[PersonData.CostumeTrunkOffsetHouseParty] = 4;
        Assert.Equal(PrimitiveResult.True, ChangeSuitAccessoryPrimitive.Execute(context, Operand(3, 1, 0)));
        Assert.Equal(0, me[PersonData.CostumeTrunkOffsetHouseParty]);
        Assert.Equal(3, me[PersonData.CurrentOutfit]);
        Assert.Equal(0, host.RandomCalls);
    }

    [Fact]
    public void ChildOfTheSameOutfitTogglesTheVariantWithoutDrawing()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me[PersonData.PersonAge] = 5;
        me[PersonData.CurrentOutfit] = 9;
        me[PersonData.CostumeTrunkOffsetHouseParty] = 0;
        Assert.Equal(PrimitiveResult.True, ChangeSuitAccessoryPrimitive.Execute(context, Operand(9, 1, 0)));
        Assert.Equal(1, me[PersonData.CostumeTrunkOffsetHouseParty]);

        me[PersonData.CostumeTrunkOffsetHouseParty] = 1;
        Assert.Equal(PrimitiveResult.True, ChangeSuitAccessoryPrimitive.Execute(context, Operand(9, 1, 0)));
        Assert.Equal(0, me[PersonData.CostumeTrunkOffsetHouseParty]);
        Assert.Equal(0, host.RandomCalls);
    }

    [Fact]
    public void ChildOfAnotherOutfitTakesTheLowBitOfTheRandomSource()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me[PersonData.PersonAge] = 5;
        me[PersonData.CurrentOutfit] = 2;
        Assert.Equal(PrimitiveResult.True, ChangeSuitAccessoryPrimitive.Execute(context, Operand(9, 1, 0)));
        Assert.Equal(0, me[PersonData.CostumeTrunkOffsetHouseParty]);
        Assert.Equal(9, me[PersonData.CurrentOutfit]);

        // The outfit is now 9, so the second draw needs another outfit to take the draw branch again.
        me[PersonData.CurrentOutfit] = 2;
        Assert.Equal(PrimitiveResult.True, ChangeSuitAccessoryPrimitive.Execute(context, Operand(9, 1, 0)));
        Assert.Equal(0, me[PersonData.CostumeTrunkOffsetHouseParty]);
        Assert.Equal(0, host.RandomCalls);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(0)]
    public void AdultOrUnknownAgeOfAnotherOutfitDrawsTenWays(int age)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me[PersonData.PersonAge] = (short)age;
        me[PersonData.CurrentOutfit] = 2;
        Assert.Equal(PrimitiveResult.True, ChangeSuitAccessoryPrimitive.Execute(context, Operand(9, 1, 0)));
        Assert.Equal(8, me[PersonData.CostumeTrunkOffsetHouseParty]);
        Assert.Equal(0, host.RandomCalls);
    }

    [Fact]
    public void AdultOfTheSameOutfitDrawsAgainWhileTheVariantRepeats()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me[PersonData.PersonAge] = 30;
        me[PersonData.CurrentOutfit] = 9;
        me[PersonData.CostumeTrunkOffsetHouseParty] = 4;
        Assert.Equal(PrimitiveResult.True, ChangeSuitAccessoryPrimitive.Execute(context, Operand(9, 1, 0)));
        Assert.Equal(8, me[PersonData.CostumeTrunkOffsetHouseParty]);
        Assert.Equal(0, host.RandomCalls);
    }

    [Fact]
    public void RandomVariantModeRunsWithoutASimulationHost()
    {
        var world = new SimulationWorld { RandomState = 1 };
        var person = new XPerson(1, new short[XObject.DefinitionCount]);
        person[PersonData.PersonAge] = 30;
        person[PersonData.CurrentOutfit] = 2;
        var context = new TreeContext(world, person, new StackFrame(0, [], []));
        Assert.Equal(PrimitiveResult.True, ChangeSuitAccessoryPrimitive.Execute(context, Operand(9, 1, 0)));
        Assert.Equal(8, person[PersonData.CostumeTrunkOffsetHouseParty]);
    }

    [Fact]
    public void PersonsRunTheSuitPrimitiveThroughTheTreeRunner()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person.SavedCharacter = Character();
        context = WithSuitScope(context, person, SuitScope());
        var tree = PrimitiveTestWorld.Tree(0, (6, 0xFE, 0xFF, Operand(0, 2, 0)));
        Assert.True(TreeRunner.Run(context, tree, table: SuitScope()));
        Assert.Equal(new[] { ("suit-book101.apr", "") }, person.SavedCharacter!.ExtraParts);
    }

    [Fact]
    public void NonPersonsRaiseIllegalPrimitiveCodeFromTheMainDispatcher()
    {
        var (context, _) = NonPerson();
        var tree = PrimitiveTestWorld.Tree(0, (6, 0xFE, 0xFF, Operand(0, 0, 0)));
        Assert.Equal(SimulationError.IllegalPrimitiveCode, Fails(() => TreeRunner.Run(context, tree)));
    }
}
