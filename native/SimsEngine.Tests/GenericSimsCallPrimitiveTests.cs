using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class GenericSimsCallPrimitiveTests
{
    private sealed class Budget : IFamilyBudget
    {
        public int Funds { get; private set; } = 1000;
        public List<(int ExpenseType, int Amount)> Charges { get; } = [];
        public int ReadFamilyBudget() => Funds;
        public void AlterFamilyBudget(int expenseType, int amount)
        {
            Charges.Add((expenseType, amount));
            Funds = unchecked(Funds - amount);
        }
    }

    private static void SetNotificationRecipients(SimulationWorld world, params XObject[] recipients)
    {
        world.WalkOrder.Clear();
        world.WalkOrder.AddRange(recipients);
    }

    // The generic call number is stored as a signed 16-bit integer at operand byte 0.
    private static byte[] Operand(short call) => [(byte)call, (byte)((ushort)call >> 8), 0, 0, 0, 0, 0, 0];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 1)]
    [InlineData(9, 2)]
    public void CallsZeroEightAndNineDispatchInitializationModes(short call, int mode)
    {
        var (context, _, host) = PrimitiveTestWorld.Create();

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(call)));
        Assert.Equal($"RunGenericSimsCallInitialization({mode})", Assert.Single(host.Calls));
    }

    [Fact]
    public void CallTwentySixZeroSelectorClearsCountAndSkipsStringLookup()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 0;
        me.Temporaries[1] = 12;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x26)));
        Assert.Equal((short)0, me.Temporaries[0]);
        Assert.Equal((short)12, me.Temporaries[1]);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallTwentySixParsesDecimalGroupsFromTheSelectedFrameString()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 5;
        me.Temporaries[1] = 2;
        host.GenericSimsCall26StringBytesAnswer = System.Text.Encoding.ASCII.GetBytes("12,-3;4x5");

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x26)));
        Assert.Equal((short)5, me.Temporaries[0]);
        Assert.Equal(new short[] { 5, 12, 0, 3, 4, 5, 0, 0 }, me.Temporaries);
        Assert.Equal(["GenericSimsCall26StringBytes(5,3)"], host.Calls);
    }

    [Fact]
    public void CallTwentySixMissingStringReturnsFalseAndWritesMinusOne()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 4;
        me.Temporaries[1] = 0;

        Assert.Equal(PrimitiveResult.False, GenericSimsCallPrimitive.Execute(context, Operand(0x26)));
        Assert.Equal((short)-1, me.Temporaries[0]);
        Assert.Equal(["GenericSimsCall26StringBytes(4,1)"], host.Calls);
    }

    [Fact]
    public void CallTwentySixStopsAfterSevenFields()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 2;
        host.GenericSimsCall26StringBytesAnswer = System.Text.Encoding.ASCII.GetBytes("1,2,3,4,5,6,7,8");

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x26)));
        Assert.Equal(new short[] { 7, 1, 2, 3, 4, 5, 6, 7 }, me.Temporaries);
    }

    [Theory]
    [InlineData(0x27, 0)]
    [InlineData(0x28, 1)]
    public void CallsTwentySevenAndTwentyEightStartTheirDistinctRequest(int call, int requestKind)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 42;

        Assert.Equal(PrimitiveResult.Stop, GenericSimsCallPrimitive.Execute(context, Operand((short)call)));
        Assert.Equal(1, context.Frame.WaitState);
        Assert.Equal(new GenericSimsCallPendingRequest(1, me.StackObjectKey, 42, 0,
            (GenericSimsCallRequestKind)requestKind, 0, 0), Assert.Single(context.World.GenericSimsCallRequests.Snapshot()));
    }

    [Fact]
    public void CallsTwentySevenAndTwentyEightStopWhileTheirRequestIsPending()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 17;
        me.Temporaries[1] = 9;

        Assert.Equal(PrimitiveResult.Stop, GenericSimsCallPrimitive.Execute(context, Operand(0x27)));
        Assert.Equal(PrimitiveResult.Stop, GenericSimsCallPrimitive.Execute(context, Operand(0x27)));
        Assert.Equal((short)17, me.Temporaries[0]);
        Assert.Equal((short)9, me.Temporaries[1]);
        Assert.Single(context.World.GenericSimsCallRequests.Snapshot());
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(2, 13, 14)]
    [InlineData(3, 13, 0)]
    public void CallsTwentySevenAndTwentyEightApplyCompletedRequestResult(int status, short temporary0, short temporary1)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 99;
        me.Temporaries[1] = 98;
        Assert.Equal(PrimitiveResult.Stop, GenericSimsCallPrimitive.Execute(context, Operand(0x28)));
        var request = Assert.Single(context.World.GenericSimsCallRequests.Snapshot()).Id;
        var requestStatus = status == 2 ? GenericSimsCallRequestStatus.Succeeded : GenericSimsCallRequestStatus.Failed;
        Assert.True(context.World.GenericSimsCallRequests.TryComplete(request, temporary0, 14, requestStatus));

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x28)));
        Assert.Equal(temporary0, me.Temporaries[0]);
        Assert.Equal(temporary1, me.Temporaries[1]);
        Assert.Empty(context.World.GenericSimsCallRequests.Snapshot());
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallOneResolvesBothRequiredObjectsAndPassesOptionalContainerToHost()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var stackObject = new XPerson(201, PrimitiveTestWorld.Definition(XPerson.PersonType));
        var runnerContainer = new XObject(202, PrimitiveTestWorld.Definition());
        var stackContainer = new XObject(203, PrimitiveTestWorld.Definition());
        context.Frame.StackObject = 1;
        me[ObjectData.ContainerId] = 2;
        stackObject[ObjectData.ContainerId] = 3;
        me.SetObjectTableEntry(1, stackObject);
        me.SetObjectTableEntry(2, runnerContainer);
        me.SetObjectTableEntry(3, stackContainer);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x01)));
        Assert.Equal([
            "RunGenericSimsCall01InteractionPhase(100,201,202,203)",
            "RunGenericSimsCall01RoutePhase(100,201,202,203;0,0)"], host.Calls);
    }

    [Fact]
    public void CallOneAllowsTheStackObjectsContainerEntryToBeMissing()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 1);
        var stackObject = new XPerson(205, PrimitiveTestWorld.Definition(XPerson.PersonType));
        var runnerContainer = new XObject(206, PrimitiveTestWorld.Definition());
        me[ObjectData.ContainerId] = 2;
        stackObject[ObjectData.ContainerId] = 3;
        me.SetObjectTableEntry(1, stackObject);
        me.SetObjectTableEntry(2, runnerContainer);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x01)));
        Assert.Equal([
            "RunGenericSimsCall01InteractionPhase(100,205,206,null)",
            "RunGenericSimsCall01RoutePhase(100,205,206,null;0,0)"], host.Calls);
    }

    [Fact]
    public void CallOneMissingFrameEntryRaisesStackObjectOutOfRange()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();

        Assert.Equal(SimulationError.ReferenceToStackObjectOutOfRange,
            Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(0x01))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallOneUsesTheNativeStackTableEvenWhenHostHasNoMatchingEntry()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 1);
        var stackObject = new XPerson(204, PrimitiveTestWorld.Definition(XPerson.PersonType));
        var runnerContainer = new XObject(205, PrimitiveTestWorld.Definition());
        me[ObjectData.ContainerId] = 2;
        me.SetObjectTableEntry(1, stackObject);
        me.SetObjectTableEntry(2, runnerContainer);
        host.ObjectTable[1] = new XObject(999, PrimitiveTestWorld.Definition());

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x01)));
        Assert.Equal([
            "RunGenericSimsCall01InteractionPhase(100,204,205,null)",
            "RunGenericSimsCall01RoutePhase(100,204,205,null;0,0)"], host.Calls);
    }

    [Fact]
    public void CallOneExchangesThePreInteractionRawWordsBeforeItsRoutePhase()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 1);
        var stackObject = new XPerson(207, PrimitiveTestWorld.Definition(XPerson.PersonType));
        stackObject.Data[1] = 34;
        var runnerContainer = new XObject(208, PrimitiveTestWorld.Definition());
        me[ObjectData.ContainerId] = 2;
        me.Data[1] = 12;
        me.SetObjectTableEntry(1, stackObject);
        me.SetObjectTableEntry(2, runnerContainer);
        host.GenericSimsCall01InteractionPhaseAction = (runner, stack) =>
        {
            runner.Data[1] = 56;
            stack.Data[1] = 78;
        };

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x01)));
        Assert.Equal((short)34, me.Data[1]);
        Assert.Equal((short)12, stackObject.Data[1]);
        Assert.Equal([
            "RunGenericSimsCall01InteractionPhase(100,207,208,null)",
            "RunGenericSimsCall01RoutePhase(100,207,208,null;34,12)"], host.Calls);
    }

    [Fact]
    public void CallOneRequiresTheRunnerContainerButAllowsMissingStackContainer()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var stackObject = new XPerson(203, PrimitiveTestWorld.Definition(XPerson.PersonType));
        context.Frame.StackObject = 1;
        me[ObjectData.ContainerId] = 2;
        stackObject[ObjectData.ContainerId] = 3;
        me.SetObjectTableEntry(1, stackObject);

        Assert.Equal(SimulationError.ReferenceToStackObjectOutOfRange,
            Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(0x01))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallOneRequiresBothRunnerAndStackObjectToBePersons()
    {
        var world = new SimulationWorld();
        var nonPersonRunner = world.Add(new XObject(100, PrimitiveTestWorld.Definition()));
        var nonPersonContext = new TreeContext(world, nonPersonRunner, new StackFrame(1, [], [0, 0]));
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson,
            Fails(() => GenericSimsCallPrimitive.Execute(nonPersonContext, Operand(0x01))));

        var (context, me, _) = PrimitiveTestWorld.Create(stackObject: 1);
        me.SetObjectTableEntry(1, new XObject(201, PrimitiveTestWorld.Definition()));
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson,
            Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(0x01))));
    }

    [Fact]
    public void CallTwentyTwoResolvesTheStackObjectBeforeCheckingItsOptionalCallback()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var item = new XPerson(220, PrimitiveTestWorld.Definition());
        context.Frame.StackObject = 100;
        me.SetObjectTableEntry(100, item);
        context.World.Host = null;

        Assert.Equal(PrimitiveResult.False, GenericSimsCallPrimitive.Execute(context, Operand(0x22)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallTwentyTwoMissingResolvedObjectRaisesErrorTwentyEightBeforeCallback()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        context.Frame.StackObject = 101;

        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson,
            Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(0x22))));
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(true, PrimitiveResult.True)]
    [InlineData(false, PrimitiveResult.False)]
    public void CallTwentyTwoAppliesNativeFameGatesThenFinalizes(bool checkAnswer, PrimitiveResult expected)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var item = new XPerson(221, PrimitiveTestWorld.Definition());
        item.TypeRecord = new TypeRecord(123);
        item[PersonData.FameScore] = 100;
        var target = new GenericSimsCallRecordTarget
        {
            FameStarThresholds = [100],
            FameSkillThresholds = Enumerable.Range(0, 6).Select(_ => (IReadOnlyList<int>)new int[11]).ToArray(),
            FameFriendThresholds = [5],
        };
        context.Frame.StackObject = 100;
        me.SetObjectTableEntry(100, item);
        context.World.Add(item);
        context.World.PersonRecordKeys.AddRange([999, 123]);
        context.World.GenericSimsCallRecordCallbackTarget = target;
        var subject = new NeighborState(1, []) { TypeRecordKey = 999 };
        var friend = new NeighborState(2, []) { FriendEligibilityObjectFlagsB4 = 4 };
        subject.PersonData[61] = 1;
        friend.PersonData[61] = 2;
        friend.PersonData[81] = (short)(checkAnswer ? 10 : 0);
        friend.Relationships.Resize(subject.Id, 1);
        subject.Relationships.Resize(friend.Id, 1);
        friend.Relationships.Set(subject.Id, 0, 50);
        subject.Relationships.Set(friend.Id, 0, 50);
        context.World.Neighbors[subject.Id] = subject;
        context.World.Neighbors[friend.Id] = friend;
        context.World.NeighborWalkOrder.AddRange([subject, friend]);
        context.World.PersonRecordNeighborIds.AddRange([null, subject.Id]);

        Assert.Equal(expected, GenericSimsCallPrimitive.Execute(context, Operand(0x22)));
        Assert.Equal([
            "GenericSimsCall22Finalize(221)"], host.Calls);
        Assert.Equal((short)(checkAnswer ? 1 : 0), item[PersonData.FameStarPower09]);
        Assert.Equal((short)(checkAnswer ? 1 : 0), item[PersonData.FameStarHighWatermark]);
    }

    [Theory]
    [InlineData(10, 5, true)]
    [InlineData(9, 5, false)]
    [InlineData(20, 11, false)]
    public void FameFriendGateUsesMutualRelationshipPowerAtHalfScale(int friendPower, int required, bool expected)
    {
        var world = new SimulationWorld();
        var (subject, friend) = AddMutualFriend(world, friendPower);
        world.PersonRecordKeys.Add(100);
        world.PersonRecordNeighborIds.Add(subject.Id);

        Assert.Equal(friendPower, world.FameFriendStarPower(0));
        Assert.Equal(expected, world.MeetsFameFriendPowerGate(0, required));
    }

    [Fact]
    public void FameFriendPowerCountsOnlyEligibleDifferentFamilyMutualNeighbors()
    {
        var world = new SimulationWorld { FriendshipThreshold = 50 };
        var subject = new NeighborState(1, []);
        subject.PersonData[61] = 1;
        world.Neighbors.Add(subject.Id, subject);
        world.NeighborWalkOrder.Add(subject);
        world.PersonRecordKeys.Add(100);
        world.PersonRecordNeighborIds.Add(subject.Id);

        void Add(short id, short family, short power, uint? objectFlags, int candidateToSubject, int subjectToCandidate)
        {
            var neighbor = new NeighborState(id, []) { FriendEligibilityObjectFlagsB4 = objectFlags };
            neighbor.PersonData[61] = family;
            neighbor.PersonData[81] = power;
            neighbor.Relationships.Resize(subject.Id, 1);
            neighbor.Relationships.Set(subject.Id, 0, candidateToSubject);
            subject.Relationships.Resize(id, 1);
            subject.Relationships.Set(id, 0, subjectToCandidate);
            world.Neighbors.Add(id, neighbor);
            world.NeighborWalkOrder.Add(neighbor);
        }

        Add(2, 2, 8, 4, 50, 50);
        Add(3, 2, 100, null, 100, 100);
        Add(4, 2, 100, 0xC, 100, 100);
        Add(5, 0, 100, 4, 100, 100);
        Add(6, 1, 100, 4, 100, 100);
        Add(7, 2, 100, 4, 49, 100);
        Add(8, 2, 100, 4, 100, 49);

        Assert.Equal(8, world.FameFriendStarPower(0));
        Assert.False(world.MeetsFameFriendPowerGate(0, 5));
    }

    [Fact]
    public void FameFriendPowerLazilyResolvesSubjectFromTypeRecordKeyInManagerOrder()
    {
        var world = new SimulationWorld();
        var subject = new NeighborState(3, []) { TypeRecordKey = 100 };
        var friend = new NeighborState(4, []) { FriendEligibilityObjectFlagsB4 = 4 };
        subject.PersonData[61] = 1;
        friend.PersonData[61] = 2;
        friend.PersonData[81] = 12;
        friend.Relationships.Resize(subject.Id, 1);
        subject.Relationships.Resize(friend.Id, 1);
        friend.Relationships.Set(subject.Id, 0, 50);
        subject.Relationships.Set(friend.Id, 0, 50);
        world.Neighbors.Add(subject.Id, subject);
        world.Neighbors.Add(friend.Id, friend);
        world.NeighborWalkOrder.AddRange([subject, friend]);
        world.PersonRecordKeys.Add(100);
        world.PersonRecordNeighborIds.Add(null);

        Assert.Equal(12, world.FameFriendStarPower(0));
        Assert.Equal(subject.Id, world.PersonRecordNeighborIds[0]);
    }

    [Fact]
    public void FameFriendPowerCachesMissingNeighborLookupAsZero()
    {
        var world = new SimulationWorld();
        world.PersonRecordKeys.Add(100);
        world.PersonRecordNeighborIds.Add(null);

        Assert.Equal(0, world.FameFriendStarPower(0));
        Assert.Equal((short)0, world.PersonRecordNeighborIds[0]);
        world.NeighborWalkOrder.Add(new NeighborState(3, []) { TypeRecordKey = 100 });
        Assert.Equal(0, world.FameFriendStarPower(0));
        Assert.Equal((short)0, world.PersonRecordNeighborIds[0]);
    }

    private static (NeighborState Subject, NeighborState Friend) AddMutualFriend(SimulationWorld world, int friendPower)
    {
        var subject = new NeighborState(1, []);
        var friend = new NeighborState(2, []) { FriendEligibilityObjectFlagsB4 = 4 };
        subject.PersonData[61] = 1;
        friend.PersonData[61] = 2;
        friend.PersonData[81] = (short)friendPower;
        friend.Relationships.Resize(subject.Id, 1);
        subject.Relationships.Resize(friend.Id, 1);
        friend.Relationships.Set(subject.Id, 0, 50);
        subject.Relationships.Set(friend.Id, 0, 50);
        world.Neighbors.Add(subject.Id, subject);
        world.Neighbors.Add(friend.Id, friend);
        world.NeighborWalkOrder.AddRange([subject, friend]);
        return (subject, friend);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(11)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(15)]
    public void CallTwentyTwoAppliesEachNativeSkillRequirementBeforeTheFriendGate(int skillField)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var item = new XPerson(222, PrimitiveTestWorld.Definition()) { TypeRecord = new TypeRecord(123) };
        item[PersonData.FameScore] = 100;
        var skillFields = new[] { 10, 12, 11, 17, 18, 15 };
        var skillThresholds = Enumerable.Range(0, 6)
            .Select(index => (IReadOnlyList<int>)(index == Array.IndexOf(skillFields, skillField) ? [1] : [0]))
            .ToArray();
        me.SetObjectTableEntry(100, item);
        context.Frame.StackObject = 100;
        context.World.Add(item);
        context.World.PersonRecordKeys.Add(123);
        context.World.GenericSimsCallRecordCallbackTarget = new GenericSimsCallRecordTarget
        {
            FameStarThresholds = [100],
            FameSkillThresholds = skillThresholds,
            FameFriendThresholds = [5],
        };

        Assert.Equal(PrimitiveResult.False, GenericSimsCallPrimitive.Execute(context, Operand(0x22)));
        Assert.Equal((short)0, item[PersonData.FameStarPower09]);
        Assert.Equal(["GenericSimsCall22Finalize(222)"], host.Calls);
    }

    [Fact]
    public void CallTwentyFourUsesTheSharedTargetMapperAndFinalizerWithItsDistinctMutation()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var item = new XPerson(241, PrimitiveTestWorld.Definition());
        item.TypeRecord = new TypeRecord(124);
        item[PersonData.FameScore] = 50;
        item[PersonData.FameStarPower09] = 2;
        var target = new GenericSimsCallRecordTarget { FameStarThresholds = [40, 80] };
        context.Frame.StackObject = 100;
        me.SetObjectTableEntry(100, item);
        context.World.Add(item);
        context.World.PersonRecordKeys.AddRange([998, 124]);
        context.World.GenericSimsCallRecordCallbackTarget = target;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x24)));
        Assert.Equal((short)1, item[PersonData.FameStarPower09]);
        Assert.Equal(["GenericSimsCall22Finalize(241)"], host.Calls);
    }

    [Theory]
    [InlineData(0, 50, false)]
    [InlineData(2, 80, false)]
    [InlineData(2, 90, false)]
    [InlineData(2, 50, true)]
    public void CallTwentyFourAppliesTheSourceFameThresholdGate(short level, short fameScore, bool expected)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var item = new XPerson(242, PrimitiveTestWorld.Definition()) { TypeRecord = new TypeRecord(124) };
        item[PersonData.FameScore] = fameScore;
        item[PersonData.FameStarPower09] = level;
        var target = new GenericSimsCallRecordTarget { FameStarThresholds = [40, 80] };
        context.Frame.StackObject = 100;
        me.SetObjectTableEntry(100, item);
        context.World.Add(item);
        context.World.PersonRecordKeys.Add(124);
        context.World.GenericSimsCallRecordCallbackTarget = target;

        var result = GenericSimsCallPrimitive.Execute(context, Operand(0x24));

        Assert.Equal(expected ? PrimitiveResult.True : PrimitiveResult.False, result);
        Assert.Equal((short)(expected ? level - 1 : level), item[PersonData.FameStarPower09]);
        Assert.Equal(["GenericSimsCall22Finalize(242)"], host.Calls);
    }

    [Fact]
    public void CallTwentyFourFallsBackToTheRecordAssociatedNeighborWhenPersonIsOffLot()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var item = new XPerson(244, PrimitiveTestWorld.Definition()) { TypeRecord = new TypeRecord(124) };
        var neighbor = new NeighborState(7, []);
        neighbor.PersonData[(int)PersonData.FameScore] = 50;
        neighbor.PersonData[(int)PersonData.FameStarPower09] = 2;
        var target = new GenericSimsCallRecordTarget { FameStarThresholds = [40, 80] };
        context.Frame.StackObject = 100;
        me.SetObjectTableEntry(100, item);
        context.World.PersonRecordKeys.Add(124);
        context.World.PersonRecordNeighborIds.Add(7);
        context.World.Neighbors.Add(7, neighbor);
        context.World.GenericSimsCallRecordCallbackTarget = target;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x24)));
        Assert.Equal((short)1, neighbor.PersonData[(int)PersonData.FameStarPower09]);
        Assert.Equal(["GenericSimsCall22Finalize(244)"], host.Calls);
    }

    [Fact]
    public void CallTwentyFourWithoutTheSharedGlobalTargetSkipsMutationAndFinalizer()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var item = new XPerson(243, PrimitiveTestWorld.Definition());
        context.Frame.StackObject = 100;
        me.SetObjectTableEntry(100, item);
        context.World.Host = null;

        Assert.Equal(PrimitiveResult.False, GenericSimsCallPrimitive.Execute(context, Operand(0x24)));
        Assert.Empty(host.Calls);
    }

    private static (TreeContext Context, XPerson Me, PrimitiveTestHost Host, XObject Item, InteractionScopeTable Scope)
        CallFifteen(params InteractionScopeEntry[] entries)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 1;
        var item = new XPerson(2, PrimitiveTestWorld.Definition(XPerson.PersonType));
        me.SetObjectTableEntry(1, item);
        var scope = new InteractionScopeTable(entries);
        context.World.InteractionScopes[item.InteractionScopeKey5CA] = scope;
        return (context, me, host, item, scope);
    }

    [Fact]
    public void CallFifteenUsesTheNativeObjectTableInsteadOfAConflictingHostLookup()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 1;
        var nativeItem = new XPerson(2, PrimitiveTestWorld.Definition(XPerson.PersonType));
        var hostItem = new XPerson(3, PrimitiveTestWorld.Definition(XPerson.PersonType));
        nativeItem.InteractionScopeKey5CA = 11;
        hostItem.InteractionScopeKey5CA = 12;
        me.SetObjectTableEntry(1, nativeItem);
        host.ObjectTable[1] = hostItem;
        context.World.InteractionScopes[nativeItem.InteractionScopeKey5CA] =
            new InteractionScopeTable([new InteractionScopeEntry(2, 0, 6)]);
        context.World.InteractionScopes[hostItem.InteractionScopeKey5CA] =
            new InteractionScopeTable([new InteractionScopeEntry(2, 1, 9)]);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x15)));

        Assert.Equal(6f, nativeItem.InteractionExecutorFloats[9]);
        Assert.Empty(hostItem.InteractionExecutorFloats);
        Assert.Empty(host.Calls);
    }

    private static (TreeContext Context, XPerson Me, PrimitiveTestHost Host, InteractionScopeTable Scope)
        CallSeventeen(params InteractionScopeEntry[] entries)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var item = me;
        var scope = new InteractionScopeTable(entries);
        context.World.InteractionScopes[item.InteractionScopeKey5CA] = scope;
        return (context, me, host, scope);
    }

    private static (TreeContext Context, XPerson Me, PrimitiveTestHost Host, InteractionScopeTable Scope)
        CallThirteenOrFourteen(params InteractionScopeEntry[] entries)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var item = new XPerson(300, PrimitiveTestWorld.Definition(XPerson.PersonType));
        me.Temporaries[0] = 1;
        me.SetObjectTableEntry(1, item);
        var scope = new InteractionScopeTable(entries);
        context.World.InteractionScopes[item.InteractionScopeKey5CA] = scope;
        return (context, me, host, scope);
    }

    private static (TreeContext Context, XPerson Me, PrimitiveTestHost Host, XObject Item) CallEighteen()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.TypeRecord = new TypeRecord(0x1234);
        return (context, me, host, me);
    }

    [Fact]
    public void CallTwentyNineAnswersTrue()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x29)));
    }

    [Fact]
    public void CallOneBCountsUnoccupiedValuesFortyThroughFortyEightOnceEach()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        me.Temporaries[0] = -1;
        me.Temporaries[1] = 123;
        context.World.InteractionLookupOrder.AddRange([
            new XObject(1, PrimitiveTestWorld.Definition()) { GenericSimsCall1BRawWord13C = 40 },
            new XObject(2, PrimitiveTestWorld.Definition()) { GenericSimsCall1BRawWord13C = 43 },
            new XObject(3, PrimitiveTestWorld.Definition()) { GenericSimsCall1BRawWord13C = 48 },
            new XObject(4, PrimitiveTestWorld.Definition()) { GenericSimsCall1BRawWord13C = 43 },
            new XObject(5, PrimitiveTestWorld.Definition()) { GenericSimsCall1BRawWord13C = 49 },
        ]);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x1b)));

        Assert.Equal((short)6, me.Temporaries[0]);
        Assert.Equal((short)123, me.Temporaries[1]);
    }

    [Fact]
    public void CallOneBReturnsNineWhenTheManagerVectorIsEmpty()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x1b)));

        Assert.Equal((short)9, me.Temporaries[0]);
    }

    [Fact]
    public void CallOneBReturnsZeroWhenEveryTestedValueIsPresent()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        context.World.InteractionLookupOrder.AddRange(Enumerable.Range(40, 9).Select((value, index) =>
            new XObject((short)(index + 1), PrimitiveTestWorld.Definition()) { GenericSimsCall1BRawWord13C = value }));

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x1b)));

        Assert.Equal((short)0, me.Temporaries[0]);
    }

    [Fact]
    public void CallTwentyWithNoLotPersonsReturnsTrueWithoutResolvingPeople()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        me.Temporaries[0] = 44;
        me.Temporaries[1] = -12;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x20)));

        Assert.Equal((short)44, me.Temporaries[0]);
        Assert.Equal((short)-12, me.Temporaries[1]);
        Assert.Null(context.World.Host);
    }

    [Fact]
    public void CallTwentySkipsAResolvedLotPersonBeforeItsRecordFallback()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        const int key = 0x12345678;
        var person = context.World.Add(new XPerson(501, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(key),
        });
        context.World.ActivePersonManagerPresent = true;
        context.World.ActivePersonRecordKeys.Add(key);
        context.World.PersonRecordKeys.Add(key);
        var scope = new InteractionScopeTable([new InteractionScopeEntry(2, 0, 5)]);
        context.World.InteractionScopes[person.InteractionScopeKey5CA] = scope;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x20)));

        Assert.DoesNotContain("GenericSimsCall20FallbackPerson(0)", host.Calls);
        Assert.DoesNotContain("GenericSimsCall20FinalizePerson(501)", host.Calls);
        Assert.Equal(1, scope.CountValue(2));
        Assert.Empty(person.InteractionExecutorFloats);
    }

    [Fact]
    public void CallTwentyIgnoresActivePersonEntriesWhenTheirManagerIsAbsent()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        context.World.ActivePersonRecordKeys.Add(0x12345678);
        context.World.PersonRecordKeys.Add(0x12345678);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x20)));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("GenericSimsCall20", StringComparison.Ordinal));
    }

    [Fact]
    public void CallTwentySkipsResolvedPersonEvenWhenItsInteractionScopeIsEmpty()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        const int key = 0x12345679;
        var person = context.World.Add(new XPerson(502, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(key),
        });
        context.World.ActivePersonManagerPresent = true;
        context.World.ActivePersonRecordKeys.Add(key);
        context.World.PersonRecordKeys.Add(key);
        context.World.InteractionScopes[person.InteractionScopeKey5CA] = new InteractionScopeTable([]);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x20)));

        Assert.DoesNotContain("GenericSimsCall20FinalizePerson(502)", host.Calls);
    }

    [Theory]
    [InlineData(0, 9, true)]
    [InlineData(1, 6, true)]
    [InlineData(2, 5, true)]
    [InlineData(3, 15, true)]
    [InlineData(4, 7, true)]
    [InlineData(5, 8, true)]
    [InlineData(6, 14, true)]
    [InlineData(7, -1, false)]
    [InlineData(8, -1, false)]
    [InlineData(9, -2, true)]
    [InlineData(10, 0x62c, true)]
    [InlineData(11, 0x630, true)]
    [InlineData(12, 0x62e, true)]
    [InlineData(13, -1, false)]
    public void CallTwentyAppliesInteractionScopeModesInSourceOrder(int mode, int selectorOrOffset, bool remove)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var person = new XPerson(500, PrimitiveTestWorld.Definition());
        var scope = new InteractionScopeTable([new InteractionScopeEntry(2, mode, 5)]);
        context.World.ActivePersonManagerPresent = true;
        context.World.ActivePersonRecordKeys.Add(0);
        context.World.PersonRecordKeys.Add(0);
        host.GenericSimsCall20FallbackPersons[0] = person;
        context.World.InteractionScopes[person.InteractionScopeKey5CA] = scope;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x20)));

        Assert.Contains("GenericSimsCall20FallbackPerson(0)", host.Calls);
        Assert.Contains("GenericSimsCall20FinalizePerson(500)", host.Calls);
        Assert.Equal(remove ? 0 : 1, scope.CountValue(2));
        if (mode <= 6)
            Assert.Equal(5f, person.InteractionExecutorFloats[selectorOrOffset]);
        else if (mode == 9)
            Assert.Equal((short)5, person.InteractionBehaviorSavedWord);
        else if (mode is >= 10 and <= 12)
            Assert.Equal((short)5, person.InteractionExecutorWords[selectorOrOffset]);
        else
        {
            Assert.Empty(person.InteractionExecutorFloats);
            Assert.Empty(person.InteractionExecutorWords);
        }
        Assert.Equal((short)0, me.Temporaries[0]);
    }

    [Fact]
    public void CallEighteenUsesItsStaticallyTypedRunningObject()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.TypeRecord = new TypeRecord(0x1234);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x18)));
        Assert.Contains("BeginInteractionBehaviorScope(4660,0)", host.Calls);
    }

    [Theory]
    [InlineData(0, 0, 0x02)]
    [InlineData(1, 1, 0x1f)]
    [InlineData(2, 2, 0x20)]
    [InlineData(3, 3, 0x21)]
    [InlineData(4, 4, 0x22)]
    [InlineData(5, 5, 0x23)]
    public void CallEighteenSearchesAndUpdatesTheSelectedActionGroup(int mode, int listIndex, int queryId)
    {
        var (context, me, host, item) = CallEighteen();
        me.Temporaries[0] = (short)mode;
        me.Temporaries[1] = 0;
        item.InteractionBehaviorSavedWord = 0x1234;
        var actions = new InteractionBehaviorActionList([
            new InteractionBehaviorAction(string.Empty, "other"),
            new InteractionBehaviorAction(string.Empty, "chair")]);
        host.InteractionBehaviorLists[listIndex] = actions;
        host.InteractionBehaviorQueryContextAnswer!.SetString(queryId, "CHAIR");

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x18)));

        Assert.Equal((short)1, me.Temporaries[1]);
        Assert.Null(host.LastInteractionBehaviorScope!.ActionList(listIndex));
        Assert.Null(host.LastInteractionBehaviorScope.TypeRecord);
        Assert.Null(host.LastInteractionBehaviorScope.QueryContext);
        Assert.Equal(0, host.LastInteractionBehaviorScope.RuntimeContext.StringCount());
        Assert.Equal((short)0x1234, item.InteractionBehaviorSavedWord);
        Assert.Same(item.TypeRecord, host.FinalizedInteractionBehaviorTypeRecord);
        Assert.Equal([ "BeginInteractionBehaviorScope(4660,0)", $"InteractionBehaviorActions({listIndex})",
            "EndInteractionBehaviorScope",
            "FinalizeInteractionBehaviorTypeRecord(4660)",
            "NotifyInteractionBehaviorGlobal(20000000)"], host.Calls);
        Assert.Equal(",other", host.InteractionBehaviorQueryContextAnswer!.StringAt(queryId));
    }

    [Theory]
    [InlineData(0, 0, 0x02)]
    [InlineData(1, 1, 0x1f)]
    [InlineData(2, 2, 0x20)]
    [InlineData(3, 3, 0x21)]
    [InlineData(4, 4, 0x22)]
    public void CallOneDSearchesAndUpdatesItsFiveActionGroups(int mode, int listIndex, int queryId)
    {
        var (context, me, host, item) = CallEighteen();
        me.Temporaries[0] = (short)mode;
        me.Temporaries[1] = 0;
        var actions = new InteractionBehaviorActionList([new(string.Empty, "chair")]);
        host.InteractionBehaviorLists[listIndex] = actions;
        host.InteractionBehaviorQueryContextAnswer!.SetString(queryId, "CHAIR");

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x1d)));

        Assert.Equal((short)0, me.Temporaries[1]);
        Assert.Equal(",chair", host.InteractionBehaviorQueryContextAnswer.StringAt(queryId));
        Assert.Same(item.TypeRecord, host.FinalizedInteractionBehaviorTypeRecord);
        Assert.Equal([ "BeginInteractionBehaviorScope(4660,0)", $"InteractionBehaviorActions({listIndex})",
            "EndInteractionBehaviorScope", "FinalizeInteractionBehaviorTypeRecord(4660)",
            "NotifyInteractionBehaviorGlobal(20000000)"], host.Calls);
    }

    [Fact]
    public void CallOneDSkipsTheSixthActionGroup()
    {
        var (context, me, host, _) = CallEighteen();
        me.Temporaries[0] = 5;
        me.Temporaries[1] = 0;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x1d)));

        Assert.Equal((short)0, me.Temporaries[1]);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("InteractionBehaviorActions(", StringComparison.Ordinal));
        Assert.Contains("EndInteractionBehaviorScope", host.Calls);
    }

    [Fact]
    public void CallOneDWithNegativeIndexRunsOnlyTheCommonTail()
    {
        var (context, me, host, _) = CallEighteen();
        me.Temporaries[0] = 0;
        me.Temporaries[1] = -1;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x1d)));

        Assert.Equal((short)-1, me.Temporaries[1]);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("InteractionBehaviorActions(", StringComparison.Ordinal));
        Assert.Equal("NotifyInteractionBehaviorGlobal(20000000)", host.Calls[^1]);
    }

    [Fact]
    public void CallOneDWithIndexPastTheListDoesNotSearchOrUpdate()
    {
        var (context, me, host, _) = CallEighteen();
        me.Temporaries[0] = 0;
        me.Temporaries[1] = 1;
        host.InteractionBehaviorLists[0] = new InteractionBehaviorActionList([new(string.Empty, "chair")]);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x1d)));

        Assert.Equal((short)1, me.Temporaries[1]);
        Assert.Equal(0, host.InteractionBehaviorQueryContextAnswer!.StringCount());
        Assert.Contains("InteractionBehaviorActions(0)", host.Calls);
        Assert.Equal("NotifyInteractionBehaviorGlobal(20000000)", host.Calls[^1]);
    }

    [Theory]
    [InlineData(0, 0, 0x02)]
    [InlineData(1, 1, 0x1f)]
    [InlineData(2, 2, 0x20)]
    [InlineData(3, 3, 0x21)]
    [InlineData(4, 4, 0x22)]
    public void CallOneEReturnsTheFirstMatchingAction(int mode, int listIndex, int queryId)
    {
        var (context, me, host, item) = CallEighteen();
        me.Temporaries[0] = (short)mode;
        me.Temporaries[1] = 99;
        item.InteractionBehaviorSavedWord = 0x1234;
        host.InteractionBehaviorLists[listIndex] = new InteractionBehaviorActionList([
            new(string.Empty, "chair"), new(string.Empty, "table")]);
        host.InteractionBehaviorQueryContextAnswer!.SetString(queryId, "TABLE");

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x1e)));

        Assert.Equal((short)1, me.Temporaries[1]);
        Assert.Equal("TABLE", host.InteractionBehaviorQueryContextAnswer.StringAt(queryId));
        Assert.Equal((short)0x1234, item.InteractionBehaviorSavedWord);
        Assert.True(host.LastInteractionBehaviorScopeCommit);
        Assert.Equal([ "BeginInteractionBehaviorScope(4660,0)", $"InteractionBehaviorActions({listIndex})",
            "EndInteractionBehaviorScope", "RefreshInteractionBehaviorTypeRecord(4660)",
            "NotifyInteractionBehaviorGlobal(20000000)"], host.Calls);
    }

    [Fact]
    public void CallOneEReturnsMinusOneWhenNoActionMatches()
    {
        var (context, me, host, _) = CallEighteen();
        me.Temporaries[0] = 0;
        host.InteractionBehaviorLists[0] = new InteractionBehaviorActionList([new(string.Empty, "chair")]);
        host.InteractionBehaviorQueryContextAnswer!.SetString(0x02, "table");

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x1e)));

        Assert.Equal((short)-1, me.Temporaries[1]);
        Assert.Equal("table", host.InteractionBehaviorQueryContextAnswer.StringAt(0x02));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    public void CallOneFReturnsActionGroupCount(int mode, int listIndex)
    {
        var (context, me, host, _) = CallEighteen();
        me.Temporaries[0] = (short)mode;
        me.Temporaries[1] = -99;
        host.InteractionBehaviorLists[listIndex] = new InteractionBehaviorActionList([
            new(string.Empty, "chair"), new(string.Empty, "table")]);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x1f)));

        Assert.Equal((short)2, me.Temporaries[1]);
        Assert.Equal("RefreshInteractionBehaviorTypeRecord(4660)", host.Calls[^2]);
        Assert.False(host.LastInteractionBehaviorScopeCommit);
    }

    [Fact]
    public void CallOneFReturnsZeroWhenTheSelectedListIsNull()
    {
        var (context, me, host, _) = CallEighteen();
        me.Temporaries[0] = 3;
        host.InteractionBehaviorLists[3] = null;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x1f)));

        Assert.Equal((short)0, me.Temporaries[1]);
        Assert.Contains("InteractionBehaviorActions(3)", host.Calls);
    }

    [Theory]
    [InlineData(0x1e)]
    [InlineData(0x1f)]
    public void CallOneEAndOneFReturnMinusOneForTheSixthGroup(short call)
    {
        var (context, me, host, _) = CallEighteen();
        me.Temporaries[0] = 5;
        me.Temporaries[1] = 77;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(call)));

        Assert.Equal((short)-1, me.Temporaries[1]);
        Assert.DoesNotContain(host.Calls, item => item.StartsWith("InteractionBehaviorActions(", StringComparison.Ordinal));
        Assert.Equal("NotifyInteractionBehaviorGlobal(20000000)", host.Calls[^1]);
    }

    [Fact]
    public void CallTenAdvancesPastTheMatchingActionAndCommitsTheScope()
    {
        var (context, _, host, item) = CallEighteen();
        item.InteractionBehaviorSavedWord = 0x1234;
        host.InteractionBehaviorLists[0] = new InteractionBehaviorActionList([
            new(string.Empty, "sofa"), new(string.Empty, "chair")]);
        host.InteractionBehaviorQueryContextAnswer!.SetString(0x02, "CHAIR");

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x10)));

        Assert.Equal(",sofa", host.InteractionBehaviorQueryContextAnswer.StringAt(0x02));
        Assert.True(host.LastInteractionBehaviorScopeCommit);
        Assert.Equal((short)0x1234, item.InteractionBehaviorSavedWord);
        Assert.Equal("RefreshInteractionBehaviorTypeRecord(4660)", host.Calls[^2]);
    }

    [Fact]
    public void CallTenUsesTheFirstActionWhenTheQueryDoesNotMatch()
    {
        var (context, _, host, _) = CallEighteen();
        host.InteractionBehaviorLists[0] = new InteractionBehaviorActionList([
            new(string.Empty, "sofa"), new(string.Empty, "chair")]);
        host.InteractionBehaviorQueryContextAnswer!.SetString(0x02, "bed");

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x10)));

        Assert.Equal(",sofa", host.InteractionBehaviorQueryContextAnswer.StringAt(0x02));
        Assert.True(host.LastInteractionBehaviorScopeCommit);
    }

    [Fact]
    public void CallTenWithEmptyListSkipsTheUpdateAndDoesNotCommit()
    {
        var (context, _, host, _) = CallEighteen();
        host.InteractionBehaviorLists[0] = new InteractionBehaviorActionList([]);
        host.InteractionBehaviorQueryContextAnswer!.SetString(0x02, "chair");

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x10)));

        Assert.Equal("chair", host.InteractionBehaviorQueryContextAnswer.StringAt(0x02));
        Assert.False(host.LastInteractionBehaviorScopeCommit);
        Assert.Equal("NotifyInteractionBehaviorGlobal(20000000)", host.Calls[^1]);
    }

    [Fact]
    public void CallEighteenRunsCommonTailWhenTemporaryOneIsNegative()
    {
        var (context, me, host, _) = CallEighteen();
        me.Temporaries[0] = 0;
        me.Temporaries[1] = -1;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x18)));

        Assert.Equal((short)-1, me.Temporaries[1]);
        Assert.Equal([ "BeginInteractionBehaviorScope(4660,0)",
            "EndInteractionBehaviorScope",
            "FinalizeInteractionBehaviorTypeRecord(4660)", "NotifyInteractionBehaviorGlobal(20000000)"], host.Calls);
    }

    [Fact]
    public void CallEighteenSkipsSearchAndUpdateWhenIndexIsPastTheSelectedList()
    {
        var (context, me, host, _) = CallEighteen();
        me.Temporaries[0] = 0;
        me.Temporaries[1] = 1;
        host.InteractionBehaviorLists[0] = new InteractionBehaviorActionList([new(string.Empty, "chair")]);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x18)));

        Assert.Equal((short)1, me.Temporaries[1]);
        Assert.Equal(0, host.InteractionBehaviorQueryContextAnswer!.StringCount());
        Assert.Contains("EndInteractionBehaviorScope", host.Calls);
    }

    [Fact]
    public void CallEighteenUpdatesRequestedEntryEvenWhenNoActionMatches()
    {
        var (context, me, host, _) = CallEighteen();
        me.Temporaries[0] = 0;
        me.Temporaries[1] = 0;
        host.InteractionBehaviorLists[0] = new InteractionBehaviorActionList([new(string.Empty, "table")]);
        host.InteractionBehaviorQueryContextAnswer!.SetString(0x02, "chair");

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x18)));

        Assert.Equal((short)-1, me.Temporaries[1]);
        Assert.Equal(",table", host.InteractionBehaviorQueryContextAnswer!.StringAt(0x02));
    }

    [Fact]
    public void CallEighteenUnknownActionModeRunsCommonTailWithoutListAccess()
    {
        var (context, me, host, _) = CallEighteen();
        me.Temporaries[0] = 6;
        me.Temporaries[1] = 0;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x18)));

        Assert.Equal((short)0, me.Temporaries[1]);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("InteractionBehaviorActions", StringComparison.Ordinal));
        Assert.Contains("EndInteractionBehaviorScope", host.Calls);
    }

    [Fact]
    public void CallFifteenWithMissingResolvedObjectRaisesErrorTwentyEight()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 1;

        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson,
            Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(0x15))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallFifteenWithNoMatchingScopeEntriesAnswersTrueWithoutDispatch()
    {
        var (context, _, host, item, scope) = CallFifteen();

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x15)));

        Assert.Equal(0, scope.Count);
        Assert.Empty(item.InteractionExecutorFloats);
        Assert.Empty(item.InteractionExecutorWords);
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(0, 9)]
    [InlineData(1, 6)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 7)]
    [InlineData(5, 8)]
    [InlineData(6, 14)]
    public void CallFifteenDispatchesExecutorKindsZeroThroughSix(int kind, int selector)
    {
        var (context, _, _, item, scope) = CallFifteen(new InteractionScopeEntry(2, kind, 0x1234fff4));

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x15)));

        Assert.Equal(-12f, item.InteractionExecutorFloats[selector]);
        Assert.Empty(item.InteractionExecutorWords);
        Assert.Equal(0, scope.Count);
    }

    [Theory]
    [InlineData(9, 0x59c)]
    [InlineData(10, 0x62c)]
    [InlineData(11, 0x630)]
    [InlineData(12, 0x62e)]
    public void CallFifteenWritesKindsNineThroughTwelve(int kind, int offset)
    {
        var (context, _, _, item, scope) = CallFifteen(new InteractionScopeEntry(2, kind, unchecked((int)0xabcdfff4u)));

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x15)));

        if (offset == 0x59c) Assert.Equal((short)-12, item.InteractionBehaviorSavedWord);
        else Assert.Equal((short)-12, item.InteractionExecutorWords[offset]);
        Assert.Empty(item.InteractionExecutorFloats);
        Assert.Equal(0, scope.Count);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(-1)]
    public void CallFifteenSkipsUnsupportedKindsWithoutUpdatingTheScope(int kind)
    {
        var (context, _, _, item, scope) = CallFifteen(new InteractionScopeEntry(2, kind, 0x12340004));

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x15)));

        Assert.Empty(item.InteractionExecutorFloats);
        Assert.Empty(item.InteractionExecutorWords);
        Assert.Equal(1, scope.Count);
    }

    [Fact]
    public void CallFifteenResetsCursorAndAppliesEachEntryAfterScopeCompaction()
    {
        var (context, _, _, item, scope) = CallFifteen(
            new InteractionScopeEntry(2, 9, 5),
            new InteractionScopeEntry(2, 10, 6));

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x15)));

        Assert.Equal((short)5, item.InteractionBehaviorSavedWord);
        Assert.Equal((short)6, item.InteractionExecutorWords[0x62c]);
        Assert.Equal(0, scope.Count);
    }

    [Fact]
    public void CallSeventeenUsesItsStaticallyTypedRunningObjectWithoutHostCalls()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        context.World.Host = null;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x17)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallSeventeenWithoutModeSevenLeavesTemporariesAndScopeUntouched()
    {
        var (context, me, host, scope) = CallSeventeen(
            new InteractionScopeEntry(2, 8, 11),
            new InteractionScopeEntry(3, 7, 12));
        me.Temporaries[0] = 4;
        me.Temporaries[1] = 5;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x17)));

        Assert.Equal((short)4, me.Temporaries[0]);
        Assert.Equal((short)5, me.Temporaries[1]);
        Assert.Equal(2, scope.Count);
        Assert.Equal([], host.Calls);
    }

    [Fact]
    public void CallSeventeenWritesAndRemovesModeSevenWhenModeEightIsMissing()
    {
        var (context, me, _, scope) = CallSeventeen(new InteractionScopeEntry(2, 7, 0x1234fff4));
        me.Temporaries[0] = 1;
        me.Temporaries[1] = 2;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x17)));

        Assert.Equal((short)-12, me.Temporaries[0]);
        Assert.Equal((short)2, me.Temporaries[1]);
        Assert.Equal(0, scope.Count);
    }

    [Fact]
    public void CallSeventeenWritesBothPayloadsWhenModeEightRemainsAfterTheResetCursor()
    {
        var (context, me, _, scope) = CallSeventeen(
            new InteractionScopeEntry(2, 7, 13),
            new InteractionScopeEntry(2, 3, 99),
            new InteractionScopeEntry(2, 8, 21));

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x17)));

        Assert.Equal((short)13, me.Temporaries[0]);
        Assert.Equal((short)21, me.Temporaries[1]);
        Assert.Equal(1, scope.Count);
        Assert.True(scope.TryGet(0, out var remaining));
        Assert.Equal(new InteractionScopeEntry(2, 3, 99), remaining);
    }

    [Fact]
    public void CallSeventeenFindsModeEightShiftedToIndexZeroAfterResettingCursor()
    {
        var (context, me, _, scope) = CallSeventeen(
            new InteractionScopeEntry(2, 7, 13),
            new InteractionScopeEntry(2, 8, 21));
        me.Temporaries[1] = 5;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x17)));

        Assert.Equal((short)13, me.Temporaries[0]);
        Assert.Equal((short)21, me.Temporaries[1]);
        Assert.Equal(0, scope.Count);
    }

    [Theory]
    [InlineData(0x13, 0)]
    [InlineData(0x14, 3)]
    public void CallThirteenAndFourteenOnScopeMissWriteMinusOneWithoutPersisting(int call, int mode)
    {
        var (context, me, host, scope) = CallThirteenOrFourteen(
            new InteractionScopeEntry(mode == 0 ? 3 : 1, 2, 4));

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand((short)call)));

        Assert.Equal((short)-1, me.Temporaries[0]);
        Assert.Equal(1, scope.Count);
        Assert.DoesNotContain(host.Calls, callName => callName.StartsWith("ScopedNeighborPersonRecordId", StringComparison.Ordinal));
        Assert.DoesNotContain("PersistNeighborhoodState", host.Calls);
    }

    [Fact]
    public void CallThirteenResolvesWildcardScopeEntryThenDecrementsAndPersists()
    {
        var (context, me, host, scope) = CallThirteenOrFourteen(new InteractionScopeEntry(1, 27, 2));
        host.ScopedNeighborPersonRecordIdAnswer = 41;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x13)));

        Assert.Equal((short)41, me.Temporaries[0]);
        Assert.Equal(1, scope.Count);
        Assert.True(scope.TryGet(0, out var remaining));
        Assert.Equal(new InteractionScopeEntry(1, 27, 1), remaining);
        Assert.Equal([
            "ScopedNeighborPersonRecordId(27)", "PersistNeighborhoodState"], host.Calls);
    }

    [Fact]
    public void CallThirteenUsesTheNativeObjectTableInsteadOfAConflictingHostLookup()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 1;
        var nativeItem = new XPerson(300, PrimitiveTestWorld.Definition(XPerson.PersonType)) { InteractionScopeKey5CA = 11 };
        var hostItem = new XPerson(301, PrimitiveTestWorld.Definition(XPerson.PersonType)) { InteractionScopeKey5CA = 12 };
        me.SetObjectTableEntry(1, nativeItem);
        host.ObjectTable[1] = hostItem;
        var nativeScope = new InteractionScopeTable([new InteractionScopeEntry(1, 27, 2)]);
        var hostScope = new InteractionScopeTable([new InteractionScopeEntry(1, 99, 2)]);
        context.World.InteractionScopes[11] = nativeScope;
        context.World.InteractionScopes[12] = hostScope;
        host.ScopedNeighborPersonRecordIdAnswer = 41;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x13)));

        Assert.Equal((short)41, me.Temporaries[0]);
        Assert.Equal(1, nativeScope.Count);
        Assert.True(nativeScope.TryGet(0, out var nativeRemaining));
        Assert.Equal(new InteractionScopeEntry(1, 27, 1), nativeRemaining);
        Assert.Equal(1, hostScope.Count);
        Assert.True(hostScope.TryGet(0, out var hostRemaining));
        Assert.Equal(new InteractionScopeEntry(1, 99, 2), hostRemaining);
        Assert.Equal(["ScopedNeighborPersonRecordId(27)", "PersistNeighborhoodState"], host.Calls);
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    public void CallsTenAndElevenRequireRunningObjectToBeAPerson(short call)
    {
        var world = new SimulationWorld();
        var me = world.Add(new XObject(100, PrimitiveTestWorld.Definition()));
        var context = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));

        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson,
            Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(call))));
    }

    [Theory]
    [InlineData(0x13)]
    [InlineData(0x14)]
    [InlineData(0x15)]
    public void CallsThirteenFourteenAndFifteenRequireResolvedTargetToBeAPerson(short call)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 1;
        me.SetObjectTableEntry(1, new XObject(300, PrimitiveTestWorld.Definition()));

        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson,
            Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(call))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallThirteenMissingNativeObjectTableEntryRaisesErrorTwentyEightWithoutHostLookup()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 1;
        host.ObjectTable[1] = new XObject(300, PrimitiveTestWorld.Definition());

        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson,
            Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(0x13))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallFourteenSelectsOnlyKeyThreeAndWritesMinusOneWhenRecordResolutionMisses()
    {
        var (context, me, host, scope) = CallThirteenOrFourteen(
            new InteractionScopeEntry(1, 2, 5),
            new InteractionScopeEntry(3, 3, 1));

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x14)));

        Assert.Equal((short)-1, me.Temporaries[0]);
        Assert.Equal(1, scope.Count);
        Assert.True(scope.TryGet(0, out var remaining));
        Assert.Equal(new InteractionScopeEntry(1, 2, 5), remaining);
        Assert.Equal([
            "ScopedNeighborPersonRecordId(3)", "PersistNeighborhoodState"], host.Calls);
    }

    [Fact]
    public void CallElevenFinalizesTheTypedRunningObjectWithoutAHostCast()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x11)));

        Assert.Equal(["GenericSimsCall11CallbackTarget", "RunGenericSimsCall11Finalize(100,0,0,0,0)"], host.Calls);
        Assert.Equal((byte)1, context.World.GlobalPerson.ByteE1);
    }

    [Fact]
    public void CallElevenSkipsMissingCallbackButAlwaysFinalizesAndSetsGlobalFlag()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 4;
        me.Temporaries[1] = -5;
        me.Temporaries[2] = 6;
        me.Temporaries[3] = 7;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x11)));

        Assert.Equal([ "GenericSimsCall11CallbackTarget",
            "RunGenericSimsCall11Finalize(100,4,-5,6,7)"], host.Calls);
        Assert.Equal((byte)1, context.World.GlobalPerson.ByteE1);
    }

    [Fact]
    public void CallElevenAppliesCallbackGateBeforeInvocationAndStillRunsFinalize()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var callbackTarget = new XObject(211, PrimitiveTestWorld.Definition());
        host.GenericSimsCall11CallbackTargetAnswer = callbackTarget;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x11)));

        Assert.Equal([ "GenericSimsCall11CallbackTarget", "PassesAutonomyCallbackGate(211)",
            "RunGenericSimsCall11Finalize(100,0,0,0,0)"], host.Calls);
        Assert.Equal((byte)1, context.World.GlobalPerson.ByteE1);
    }

    [Fact]
    public void CallElevenInvokesAllowedCallbackWithTheOriginalFourWordRequest()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        var callbackTarget = new XObject(211, PrimitiveTestWorld.Definition());
        host.GenericSimsCall11CallbackTargetAnswer = callbackTarget;
        host.GenericSimsCall11CallbackGateAnswer = true;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x11)));

        Assert.Equal([ "GenericSimsCall11CallbackTarget", "PassesAutonomyCallbackGate(211)",
            "InvokeGatedObjectCallback(211,3,16,0,0)", "RunGenericSimsCall11Finalize(100,0,0,0,0)"], host.Calls);
        Assert.Equal((byte)1, context.World.GlobalPerson.ByteE1);
    }

    private sealed class FakeFamilies : INeighborhoodFamilies
    {
        public Dictionary<short, List<int>> Members { get; } = [];
        public short Active { get; set; } = 3;
        public short ActiveFamilyNumber => Active;
        public bool Exists(short familyNumber) => Members.ContainsKey(familyNumber);
        public void AddMember(short familyNumber, int guid) => Members[familyNumber].Add(guid);
        public bool HasMember(short familyNumber, int guid) => Members.TryGetValue(familyNumber, out var list) && list.Contains(guid);
        public void RemoveMember(short familyNumber, int guid) => Members[familyNumber].Remove(guid);
    }

    private static (NeighborState Neighbor, FakeFamilies Families) FamilyNeighbor(SimulationWorld world, short family)
    {
        var neighbor = new NeighborState(7, PrimitiveTestWorld.Definition()) { TypeRecordKey = 0x5150 };
        neighbor.PersonData[(int)PersonData.FamilyNumber] = family;
        world.Neighbors[7] = neighbor;
        var families = new FakeFamilies();
        families.Members[3] = [0x5150, 0x6160];
        world.NeighborhoodFamilies = families;
        return (neighbor, families);
    }

    [Fact]
    public void CallSixRaisesNeighborNotFoundForAStackObjectThatIsNoNeighbor()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 7);
        context.World.Host = null;

        Assert.Equal(SimulationError.NeighborNotFound,
            Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(0x06))));

        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallSixRemovesTheNeighborFromItsFamilyAsMacRemoveFromFamily()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 7);
        me.TypeRecord = new TypeRecord(0x5150);
        var (neighbor, families) = FamilyNeighbor(context.World, 3);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x06)));

        Assert.Equal(["GenericSimsCall06CallbackTarget", "NotifyNeighborInteractionChanged"], host.Calls);
        Assert.Equal((short)0, neighbor.PersonData[(int)PersonData.FamilyNumber]);
        Assert.Equal((short)0, ((XPerson)me)[PersonData.FamilyNumber]);
        Assert.Equal([0x6160], families.Members[3]);
        Assert.Contains((short)3, context.World.DirtyRelationshipFamilies);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void CallSixAnswersFalseWhenRemoveFromFamilyFindsNoFamilyOrMember(short family)
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 7);
        var (neighbor, families) = FamilyNeighbor(context.World, family);

        Assert.Equal(PrimitiveResult.False, GenericSimsCallPrimitive.Execute(context, Operand(0x06)));

        Assert.Equal(["GenericSimsCall06CallbackTarget"], host.Calls);
        Assert.Equal(family, neighbor.PersonData[(int)PersonData.FamilyNumber]);
        Assert.Equal([0x5150, 0x6160], families.Members[3]);
    }

    [Fact]
    public void CallSixInvokesAllowedCallbackBeforeRemovingTheNeighbor()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 7);
        me.TypeRecord = new TypeRecord(me.Id);
        FamilyNeighbor(context.World, 3);
        host.GenericSimsCall06CallbackTargetAnswer = new XObject(213, PrimitiveTestWorld.Definition());
        host.GenericSimsCall11CallbackGateAnswer = true;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x06)));

        Assert.Equal([
            "GenericSimsCall06CallbackTarget", "PassesAutonomyCallbackGate(213)",
            "InvokeGatedObjectCallback(213,3,16,27,0)", "NotifyNeighborInteractionChanged"], host.Calls);
    }

    [Fact]
    public void CallThreeRunsTheGlobalStateResetAndAnswersTrue()
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x03)));
    }

    [Fact]
    public void CallThreeReleasesAndClearsThePrimaryHandleBeforeTheSecondaryHandle()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        var state = context.World.GenericSimsCall03State;
        state.PrimaryHandle = 0x1234;
        state.SecondaryHandle = 0x5678;
        state.LinkedGlobalWord60 = 9;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x03)));

        Assert.Equal([
            "ReleaseGenericSimsCall03PrimaryHandle(4660)",
            "ReleaseGenericSimsCall03SecondaryHandle(22136)"], host.Calls);
        Assert.Equal(0, state.PrimaryHandle);
        Assert.Equal(0, state.SecondaryHandle);
        Assert.Equal(0, state.LinkedGlobalWord60);
    }

    [Fact]
    public void CallThreeClearsTheLinkedWordOnlyWhenThePrimaryHandleExists()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        var state = context.World.GenericSimsCall03State;
        state.LinkedGlobalWord60 = 9;
        state.SecondaryHandle = 0x5678;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x03)));

        Assert.Equal(["ReleaseGenericSimsCall03SecondaryHandle(22136)"], host.Calls);
        Assert.Equal(0, state.PrimaryHandle);
        Assert.Equal(9, state.LinkedGlobalWord60);
    }

    [Fact]
    public void CallFourteenWritesTemporaryZeroToItsLotTableEffectWord31()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 5;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0e)));
        Assert.Equal((short)5, me.ObjectTableEffectWords[31]);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallFifteenStoresTheFootprintFields()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 5;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0f)));
        Assert.Equal((short)5, me[PersonData.RouteFootprintMask]);
        Assert.Equal((short)0, me[PersonData.FootprintExtension]);
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(0, 11, 20, 11, 18)]
    [InlineData(2, 12, 21, 10, 21)]
    [InlineData(4, 9, 22, 9, 20)]
    [InlineData(6, 10, 19, 8, 19)]
    public void CallFifteenRefreshesTheFootprintInFacingOrder(short facing, int maxX, int maxY, int minX, int minY)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Position = new ObjectPosition(10, 20, 0);
        me.TypeRecord = new TypeRecord(0) { FootprintMask = 0x4321 };
        me[ObjectData.Direction] = facing;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0f)));
        Assert.Equal(new FootprintBounds(maxX, maxY, minX, minY), me.FootprintBounds);
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallFifteenUsesTheOriginalZeroSpanGhostGate(bool livingGhost)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Position = new ObjectPosition(10, 20, 0);
        me.TypeRecord = new TypeRecord(0);
        if (livingGhost) me.FootprintFlags634 = 1;
        else me.FootprintFlags614 = 1;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0f)));

        Assert.Equal(new FootprintBounds(10, 20, 10, 20), me.FootprintBounds);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallFifteenNeedsAPerson()
    {
        var host = new PrimitiveTestHost();
        var world = new SimulationWorld { Host = host };
        var me = world.Add(new XObject(100, PrimitiveTestWorld.Definition()));
        var context = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(0x0f))));
    }

    [Fact]
    public void CallNineteenSavesThePersonsRecord()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me[PersonData.NeighborId] = 4;
        me.PersonData[2] = 46;
        var neighbor = new NeighborState(4, PrimitiveTestWorld.Definition());
        context.World.Neighbors[4] = neighbor;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x19)));

        Assert.Equal(10, neighbor.PersonRecordVersion);
        Assert.Equal((short)46, neighbor.PersonData[2]);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallNineteenNeedsAPerson()
    {
        var host = new PrimitiveTestHost();
        var world = new SimulationWorld { Host = host };
        var me = world.Add(new XObject(100, PrimitiveTestWorld.Definition()));
        var context = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));
        var error = Assert.Throws<SimulationException>(() => GenericSimsCallPrimitive.Execute(context, Operand(0x19)));
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, error.Error);
    }

    [Fact]
    public void CallTwentyAnswersTrueWhenTheTemporaryIsZero()
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x2a)));
    }

    [Fact]
    public void CallTwentyWithAMissingEntryAnswersTrue()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 1;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x2a)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallTwentyMissingEntryDoesNotRequireAHost()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XObject(100, PrimitiveTestWorld.Definition()));
        me.Temporaries[0] = 1;
        var context = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x2a)));
    }

    [Fact]
    public void CallTwentyWithANullResolutionAnswersTrue()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 5;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x2a)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallTwentyWithANonPersonEntrySkipsPersonRecordSelection()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var objectEntry = context.World.Add(new XObject(101, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(0x1234),
        });
        me.SetObjectTableEntry(5, objectEntry);
        var personDefinition = PrimitiveTestWorld.Definition();
        personDefinition[(int)ObjdField.Type] = XPerson.PersonType;
        host.ObjectTable[5] = context.World.Add(new XPerson(102, personDefinition)
        {
            TypeRecord = new TypeRecord(0x5678),
        });
        context.World.PersonRecordKeys.Add(0x5678);
        context.World.PersonRecordManagerPresent = true;
        context.World.PersonRecordKeys.Add(0x1234);
        context.World.PersonRecordManagerPresent = true;
        me.Temporaries[0] = 5;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x2a)));

        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallTwentySelectsTheResolvedPersonAndSendsItsRawKey()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.SetObjectTableEntry(5, me);
        me.TypeRecord = new TypeRecord(0x1234);
        context.World.PersonRecordKeys.Add(0x1234);
        context.World.PersonRecordManagerPresent = true;
        me.Temporaries[0] = 5;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x2a)));
        Assert.Equal([
            "RefreshDisplay", "RefreshDisplay", "SelectedPersonChanged(100)", "SendNotice(228,0)",
        ], host.Calls);
        Assert.Equal(2u, me.Flags & 0x1f);
    }

    [Fact]
    public void CallTwentyWithNoPersonRecordDoesNotChangeAnEmptySelection()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.SetObjectTableEntry(5, me);
        me.TypeRecord = new TypeRecord(0x1234);
        context.World.PersonRecordManagerPresent = true;
        me.Temporaries[0] = 5;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x2a)));

        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallTwentyDoesNotRepeatEffectsForTheAlreadySelectedPerson()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.SetObjectTableEntry(5, me);
        me.TypeRecord = new TypeRecord(0x1234);
        me.Flags = 2;
        context.World.PersonRecordKeys.Add(0x1234);
        context.World.PersonRecordManagerPresent = true;
        me.Temporaries[0] = 5;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x2a)));

        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallTwentySkipsSelectionWhenPersonRecordManagerIsMissing()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.SetObjectTableEntry(5, me);
        me.TypeRecord = new TypeRecord(0x1234);
        context.World.PersonRecordKeys.Add(0x1234);
        context.World.Add(new XObject(101, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(0x1234),
        });
        me.Temporaries[0] = 5;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x2a)));

        Assert.DoesNotContain(host.Calls, call => call.StartsWith("SetMultiTilePersonSelectionFlags(", StringComparison.Ordinal));
    }

    [Fact]
    public void CallTwentyAppliesSelectionGatesFromStateClassAndStoredSlot()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.SetObjectTableEntry(5, me);
        me.TypeRecord = new TypeRecord(0x1234);
        me.StateValue = 5;
        me.SelectionWord5CC = 2;
        me.SelectionWord606 = 12;
        context.World.PersonRecordKeys.Add(0x1234);
        context.World.StateClasses.Add(new StateClassEntry(5, 2));
        context.World.PersonRecordManagerPresent = true;
        context.World.StoredSlotPersonId = 12;
        context.World.Add(new XObject(101, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(0x1234),
            StateValue = 5,
            SelectionWord5CC = 2,
            SelectionWord606 = 12,
        });
        me.Temporaries[0] = 5;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x2a)));

        Assert.Equal("SendNotice(228,0)", host.Calls[^1]);
    }

    [Fact]
    public void CallTwentyDeselectionCompletesBeforeSelectingTheNewPerson()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.SetObjectTableEntry(5, me);
        me.TypeRecord = new TypeRecord(0x1234);
        me.Flags = 0x81;
        context.World.PersonRecordKeys.Add(0x1234);
        context.World.PersonRecordManagerPresent = true;
        var previous = new XObject(99, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(0x9876),
            Flags = 0x102,
        };
        context.World.WalkOrder.Insert(0, previous);
        me.Temporaries[0] = 5;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x2a)));

        Assert.Equal([
            "RefreshDisplay", "RefreshDisplay", "SelectedPersonChanged(99)",
            "RefreshDisplay", "RefreshDisplay", "SelectedPersonChanged(100)",
            "SendNotice(228,0)",
        ], host.Calls);
        Assert.Equal(0x100u, previous.Flags);
        Assert.Equal(0x83u, me.Flags);
    }

    [Fact]
    public void CallTwentyAppliesMultiTileFlagsInLeadToPartOrder()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.SetObjectTableEntry(5, me);
        me.Definition[(int)ObjdField.MasterId] = 1;
        me.TypeRecord = new TypeRecord(0x1234);
        me.Flags = 0x80;
        me.MultiTileMasterId = 101;
        me.MultiTileNextId = 102;
        context.World.PersonRecordKeys.Add(0x1234);
        context.World.PersonRecordManagerPresent = true;
        var master = context.World.Add(new XObject(101, PrimitiveTestWorld.Definition())
        {
            Flags = 0x100,
            MultiTileNextId = 100,
        });
        var part = context.World.Add(new XObject(102, PrimitiveTestWorld.Definition())
        {
            Flags = 0x40,
            MultiTileMasterId = 101,
        });
        me.Temporaries[0] = 5;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x2a)));

        Assert.Equal([
            "RefreshDisplay", "RefreshDisplay", "RefreshDisplay", "RefreshDisplay",
            "SelectedPersonChanged(100)", "SendNotice(228,0)",
        ], host.Calls);
        Assert.Equal(0x102u, master.Flags);
        Assert.Equal(0x82u, me.Flags);
        Assert.Equal(0x42u, part.Flags);
    }

    [Fact]
    public void CallTwentyFunctionEightUpdatesOnlyTheSelectedMultiTilePartNatively()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        me.SetObjectTableEntry(5, me);
        me.Definition[(int)ObjdField.MasterId] = 1;
        me.TypeRecord = new TypeRecord(0x1234) { FunctionTable = [0, 0, 0, 0, 0, 0, 0, 0, 4115] };
        me.Flags = 0x81;
        context.World.PersonRecordKeys.Add(0x1234);
        context.World.PersonRecordManagerPresent = true;
        me.Temporaries[0] = 5;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x2a)));

        var host = (PrimitiveTestHost)context.World.Host!;
        Assert.Equal([
            "RefreshDisplay", "RefreshDisplay", "SelectedPersonChanged(100)", "SendNotice(228,0)",
        ], host.Calls);
        Assert.Equal(0x83u, me.Flags);
    }

    [Fact]
    public void CallTwentySkipsRefreshForMultiTilePartsWhoseFlagsAlreadyMatch()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.SetObjectTableEntry(5, me);
        me.Definition[(int)ObjdField.MasterId] = 1;
        me.TypeRecord = new TypeRecord(0x1234);
        me.Flags = 2;
        me.MultiTileMasterId = 101;
        me.MultiTileNextId = 102;
        context.World.PersonRecordKeys.Add(0x1234);
        context.World.PersonRecordManagerPresent = true;
        var previous = new XObject(98, PrimitiveTestWorld.Definition())
        {
            Flags = 2,
            TypeRecord = new TypeRecord(0x9876),
        };
        context.World.WalkOrder.Insert(0, previous);
        context.World.Add(new XObject(101, PrimitiveTestWorld.Definition())
        {
            Flags = 2,
            MultiTileNextId = 100,
        });
        context.World.Add(new XObject(102, PrimitiveTestWorld.Definition())
        {
            Flags = 2,
            MultiTileMasterId = 101,
        });
        me.Temporaries[0] = 5;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x2a)));

        Assert.Equal([
            "RefreshDisplay", "RefreshDisplay", "SelectedPersonChanged(98)",
            "RefreshDisplay", "SelectedPersonChanged(100)", "SendNotice(228,0)",
        ], host.Calls);
    }

    [Fact]
    public void PersonRecordSelectionUsesStateClassOneAndGlobalOverride()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person.TypeRecord = new TypeRecord(0x1234);
        person.SelectionWord5CC = 1;
        context.World.PersonRecordKeys.Add(0x1234);
        context.World.PersonRecordManagerPresent = true;

        Assert.Null(context.World.EvaluatePersonRecordSelection(0)?.Person);

        context.World.PersonSelectionOverride = true;
        Assert.Same(person, context.World.EvaluatePersonRecordSelection(0)?.Person);
    }

    [Fact]
    public void PersonRecordSelectionUsesTheTwentyOneToThirtyStateRange()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person.TypeRecord = new TypeRecord(0x1234);
        person.StateValue = 21;
        person.SelectionWord5CC = 2;
        context.World.PersonRecordKeys.Add(0x1234);
        context.World.PersonRecordManagerPresent = true;

        Assert.Null(context.World.EvaluatePersonRecordSelection(0)?.Person);

        person.SelectionWord5CC = 1;
        Assert.Same(person, context.World.EvaluatePersonRecordSelection(0)?.Person);
    }

    [Fact]
    public void PersonRecordSelectionLeavesUnsupportedStatesUnchanged()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        person.TypeRecord = new TypeRecord(0x1234);
        person.StateValue = 50;
        context.World.PersonRecordKeys.Add(0x1234);
        context.World.PersonRecordManagerPresent = true;

        Assert.Null(context.World.EvaluatePersonRecordSelection(0));
    }

    [Fact]
    public void CallTwelveWithNoActivePersonLeavesTemporaryZero()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 5;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x12)));
        Assert.Equal((short)5, me.Temporaries[0]);
    }

    [Fact]
    public void CallTwelveWithNoMatchingTypeRecordWritesMinusOne()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.ActivePersonManagerPresent = true;
        context.World.ActivePersonRecordKeys.Add(100);
        context.World.PersonRecordKeys.Add(100);
        me.Temporaries[0] = 7;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x12)));
        Assert.Equal((short)-1, me.Temporaries[0]);
    }

    [Fact]
    public void CallTwelveWritesTheFirstLotObjectWithThePersonRecordTypeKey()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.ActivePersonManagerPresent = true;
        context.World.ActivePersonRecordKeys.Add(0x1234);
        context.World.PersonRecordKeys.Add(0x1234);
        context.World.WalkOrder.Add(new XPerson(42, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(0x1234),
        });
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x12)));
        Assert.Equal((short)42, me.Temporaries[0]);
    }

    [Fact]
    public void CallTwelveUsesActiveVectorRecordAndLotObjectOrder()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.ActivePersonManagerPresent = true;
        context.World.ActivePersonRecordKeys.AddRange([77, 100]);
        context.World.PersonRecordKeys.AddRange([100, 77]);
        context.World.WalkOrder.AddRange([
            new XPerson(31, PrimitiveTestWorld.Definition()) { TypeRecord = new TypeRecord(77) },
            new XPerson(32, PrimitiveTestWorld.Definition()) { TypeRecord = new TypeRecord(77) },
        ]);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x12)));

        Assert.Equal((short)31, me.Temporaries[0]);
    }

    [Fact]
    public void CallOneATwentySixWithNoActivePersonLeavesTemporaries()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 5;
        me.Temporaries[1] = 6;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x1a)));
        Assert.Equal((short)5, me.Temporaries[0]);
        Assert.Equal((short)6, me.Temporaries[1]);
    }

    [Fact]
    public void CallOneAWithoutStoredSlotResolvesTheActivePersonRecord()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.ActivePersonManagerPresent = true;
        context.World.ActivePersonRecordKeys.Add(9);
        context.World.PersonRecordKeys.Add(9);
        context.World.WalkOrder.Add(new XPerson(9, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(9),
        });
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x1a)));
        Assert.Equal((short)9, me.Temporaries[0]);
        Assert.Equal((short)0, me.Temporaries[1]);
    }

    [Fact]
    public void CallOneAWithoutStoredSlotAndWithoutTypeRecordWritesMinusOne()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.ActivePersonManagerPresent = true;
        context.World.ActivePersonRecordKeys.Add(9);
        context.World.PersonRecordKeys.Add(9);
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x1a)));
        Assert.Equal((short)-1, me.Temporaries[0]);
        Assert.Equal((short)0, me.Temporaries[1]);
    }

    [Fact]
    public void CallOneAWithStoredSlotWritesSlotAndOne()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.ActivePersonManagerPresent = true;
        context.World.ActivePersonRecordKeys.Add(9);
        context.World.PersonRecordKeys.Add(9);
        context.World.StoredSlotPersonId = 77;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x1a)));
        Assert.Equal((short)77, me.Temporaries[0]);
        Assert.Equal((short)1, me.Temporaries[1]);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(0x16)]
    [InlineData(0x2c)]
    [InlineData(-1)]
    public void UnlistedCallsRaiseInvalidGlobalSimCall(short call)
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        Assert.Equal(SimulationError.InvalidGlobalSimCall, Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(call))));
    }

    [Fact]
    public void CallTwoStoresTheEntryWhenTheWordIsSet()
    {
        var (context, person, host) = PrimitiveTestWorld.Create(stackObject: 3);
        var entry = context.World.Add(new XObject(7, PrimitiveTestWorld.Definition()));
        person.SetObjectTableEntry(3, entry);
        person.GenericSimsCall02GateWordA84 = 5;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x02)));
        Assert.Same(entry, person.GenericSimsCall02ObjectA54);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallThirteenClearsTheReferencePreviouslyStoredByCallTwo()
    {
        var (context, person, _) = PrimitiveTestWorld.Create(stackObject: 3);
        var subject = new XObject(7, PrimitiveTestWorld.Definition());
        var currentOwner = new XObject(8, PrimitiveTestWorld.Definition());
        person.SetObjectTableEntry(3, subject);
        person.CurrentActionRecord = new ActionQueueSlot { Owner = currentOwner };
        person.GenericSimsCall02GateWordA84 = 1;
        SetNotificationRecipients(context.World, person);
        person.ActionQueueCurrent = 0;
        person.ActionQueueEnd = 2;
        person.ActionQueue[0] = new ActionQueueSlot { Owner = subject };
        person.ActionQueue[1] = new ActionQueueSlot { Owner = currentOwner };

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x02)));
        Assert.Same(subject, person.GenericSimsCall02ObjectA54);
        Assert.Same(subject, person.CurrentActionRecord.Secondary);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0d)));

        Assert.Null(person.GenericSimsCall02ObjectA54);
        Assert.Null(person.CurrentActionRecord.Secondary);
        Assert.Equal(1, person.ActionQueueEnd - person.ActionQueueCurrent);
        Assert.Same(currentOwner, person.ActionQueue[person.ActionQueueCurrent & 7].Owner);
    }

    [Fact]
    public void CallTwoSkipsTheStoreWhenTheWordIsClear()
    {
        var (context, person, _) = PrimitiveTestWorld.Create(stackObject: 3);
        person.SetObjectTableEntry(3, context.World.Add(new XObject(7, PrimitiveTestWorld.Definition())));
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x02)));
        Assert.Null(person.GenericSimsCall02ObjectA54);
    }

    [Fact]
    public void CallTwoWithAMissingEntryRaisesError10()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 9);
        context.World.Host = null;
        Assert.Equal(SimulationError.ReferenceToTreeParameterWhenNoParameterExists, Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(0x02))));
    }

    [Fact]
    public void CallTwoWithAZeroIndexRaisesError10()
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        Assert.Equal(SimulationError.ReferenceToTreeParameterWhenNoParameterExists, Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(0x02))));
    }

    [Fact]
    public void CallTwoNeedsAPerson()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XObject(100, PrimitiveTestWorld.Definition()));
        me.SetObjectTableEntry(3, world.Add(new XObject(7, PrimitiveTestWorld.Definition())));
        var context = new TreeContext(world, me, new StackFrame(3, [], [0, 0]));
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(0x02))));
    }

    [Fact]
    public void CallTwelveWithAMissingEntryRaisesError10()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 9);
        Assert.Equal(SimulationError.ReferenceToTreeParameterWhenNoParameterExists, Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(0x0c))));
    }

    [Fact]
    public void CallTwelveWithAZeroIndexRaisesError10()
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        Assert.Equal(SimulationError.ReferenceToTreeParameterWhenNoParameterExists, Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(0x0c))));
    }

    [Fact]
    public void CallTwelveChecksTheNativeObjectTableBeforeRequiringTheTileHost()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XObject(100, PrimitiveTestWorld.Definition()));
        var context = new TreeContext(world, me, new StackFrame(9, [], [0, 0]));

        Assert.Equal(SimulationError.ReferenceToTreeParameterWhenNoParameterExists,
            Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(0x0c))));
    }

    [Fact]
    public void CallTwelveWithAZeroParameterNegatesTheTileSum()
    {
        var (context, me, _) = PrimitiveTestWorld.Create(stackObject: 3);
        var entry = context.World.Add(new XObject(7, PrimitiveTestWorld.Definition()));
        entry.Position = new ObjectPosition(0x20, 0x30, 0);
        me.SetObjectTableEntry(3, entry);
        Assert.Equal(0, context.World.LotTransformParameter);
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0c)));
        Assert.Equal(-5, me.Temporaries[0]);
    }

    [Fact]
    public void CallTwelveUsesNativeTransformForCoordinatesInsideTheLot()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 3);
        var entry = context.World.Add(new XObject(7, PrimitiveTestWorld.Definition()));
        entry.Position = new ObjectPosition(0x20, 0x30, 0);
        me.SetObjectTableEntry(3, entry);
        context.World.SetLotOrientation(1);
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0c)));
        Assert.Equal(-62, me.Temporaries[0]);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallTwelveUsesNativeWorldOrientationWithoutAHost()
    {
        var (context, me, _) = PrimitiveTestWorld.Create(stackObject: 3);
        var entry = context.World.Add(new XObject(7, PrimitiveTestWorld.Definition()));
        entry.Position = new ObjectPosition(0x20, 0x30, 0);
        me.SetObjectTableEntry(3, entry);
        context.World.SetLotOrientation(2);
        context.World.Host = null;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0c)));

        Assert.Equal(-121, me.Temporaries[0]);
    }

    [Fact]
    public void CallTwelvePreservesTheHostTablePathOutsideTheVerifiedLotDomain()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 3);
        var entry = context.World.Add(new XObject(7, PrimitiveTestWorld.Definition()));
        entry.Position = new ObjectPosition(-0x10, 0x30, 0);
        me.SetObjectTableEntry(3, entry);
        context.World.SetLotOrientation(1);
        host.TileTransformByteAnswer = index => index == -122 ? (sbyte)10 : (sbyte)20;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0c)));

        Assert.Equal(-30, me.Temporaries[0]);
        Assert.Equal(["TileTransformByte(-122)", "TileTransformByte(-121)"], host.Calls);
    }

    [Fact]
    public void CallTwelveUsesTheNativeObjectTableInsteadOfAConflictingHostEntry()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 3);
        var nativeEntry = context.World.Add(new XObject(7, PrimitiveTestWorld.Definition()));
        nativeEntry.Position = new ObjectPosition(0x20, 0x30, 0);
        me.SetObjectTableEntry(3, nativeEntry);
        var conflictingHostEntry = context.World.Add(new XObject(8, PrimitiveTestWorld.Definition()));
        conflictingHostEntry.Position = new ObjectPosition(0x70, 0x90, 0);
        host.ObjectTable[3] = conflictingHostEntry;
        context.World.SetLotOrientation(0);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0c)));

        Assert.Equal(-5, me.Temporaries[0]);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallThirtyFiveWithANonPersonTypeRaisesError28()
    {
        var host = new PrimitiveTestHost();
        var world = new SimulationWorld { Host = host };
        var me = world.Add(new XPerson(100, PrimitiveTestWorld.Definition()));
        var context = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(0x23))));
    }

    [Fact]
    public void CallThirtyFiveWithANonPersonRecordAnswersTrue()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XObject(100, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        var context = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x23)));
    }

    [Fact]
    public void CallThirtyFiveWithAMissingBaseAnswersTrue()
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x23)));
    }

    [Fact]
    public void CallThirtyFiveStoresThePersonInTheBase()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        var slotBase = context.World.Add(new XObject(8, PrimitiveTestWorld.Definition()));
        context.World.GenericSimsCall23PersonSlotBase = slotBase;
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x23)));
        Assert.Same(person, slotBase.StoredPersonPointer10C);
    }

    [Fact]
    public void CallThirtySevenWithAMissingEntryNeedsAPerson()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 9);
        context.World.Host = null;
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(0x25))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallThirtySevenWithANonPersonEntryNeedsAPerson()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 3);
        context.Me.SetObjectTableEntry(3, context.World.Add(new XObject(7, PrimitiveTestWorld.Definition())));
        context.World.Host = null;
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(0x25))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallThirtySevenWithAPersonEntryAnswersTrue()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 3);
        var person = context.World.Add(new XPerson(7, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        context.Me.SetObjectTableEntry(3, person);
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x25)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallTenMaintainsTheLotList()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0a)));
        Assert.Equal([me], me.LotListEntries);
        Assert.Equal(["SendNotice(264,0)"], host.Calls);
    }

    [Fact]
    public void CallTenAppendsDuplicateReferencesWithoutRepeatingTheBoundaryNotice()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.LotListEntries.Add(me);
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0a)));
        Assert.Equal([me, me], me.LotListEntries);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallElevenRemovesTheRunnerFromTheLotList()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var first = context.World.Add(new XObject(101, PrimitiveTestWorld.Definition()));
        var last = context.World.Add(new XObject(102, PrimitiveTestWorld.Definition()));
        me.LotListEntries.AddRange([first, me, last, me]);
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0b)));
        Assert.Equal([first, last, me], me.LotListEntries);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallElevenNotifiesOnlyWhenRemovalEmptiesTheLotList()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.LotListEntries.Add(me);
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0b)));
        Assert.Empty(me.LotListEntries);
        Assert.Equal(["SendNotice(264,0)"], host.Calls);
    }

    [Fact]
    public void CallElevenDoesNotNotifyWhenTheRunnerIsAbsent()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var other = context.World.Add(new XObject(101, PrimitiveTestWorld.Definition()));
        me.LotListEntries.Add(other);
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0b)));
        Assert.Equal([other], me.LotListEntries);
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(0x5a, 0, 0)]
    [InlineData(0x5c, 0, 0)]
    [InlineData(0x5d, 0, 1)]
    [InlineData(0x62, 0, 1)]
    [InlineData(10, 1, 0)]
    [InlineData(10, 2, 1)]
    [InlineData(10, 3, -1)]
    [InlineData(10, 0, -1)]
    [InlineData(0x63, 2, 1)]
    [InlineData(0x59, 1, 0)]
    public void CallTwentyEightClassifiesTemporaryZero(short initial, int stateClass, short expected)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = initial;
        if (stateClass != 0) context.World.StateClasses.Add(new StateClassEntry(initial, stateClass));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x1c)));
        Assert.Equal(expected, me.Temporaries[0]);
    }

    [Fact]
    public void CallFortyThreeSkipsTheUpkeepWhenTheGateIsShut()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        context.World.LotUpkeepManagerPresent = false;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x2b)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallFortyThreeRaisesMagicMasterResourcesToRunningObjectMinimums()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        context.World.LotUpkeepManagerPresent = true;
        me.TypeRecord = new TypeRecord(0x1234);
        me.LotUpkeepGateByte14C = true;
        me.TableWord10C = 17;
        me.LotUpkeepPointer128Present = true;
        me.StateValue = 77;
        context.World.StateClasses.Add(new StateClassEntry(77, 2));
        context.World.GlobalPerson.LotUpkeepMatchWord22 = 17;
        for (var i = 0; i < 6; i++) me.LotUpkeepMinimumResources[i] = (short)(10 + i);
        var resourceObject = new XObject(101, PrimitiveTestWorld.Definition(), attributes: 6)
        {
            TypeRecord = new TypeRecord(unchecked((int)0xB6C90029)),
        };
        resourceObject.Attributes = [2, 11, 3, 14, 0, 20];
        context.World.Add(resourceObject);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x2b)));
        Assert.Equal([2, 11, 3, 14, 0, 20], resourceObject.Attributes);
        context.World.StateClasses[0] = new StateClassEntry(77, 1);
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x2b)));
        Assert.Equal([10, 11, 12, 14, 14, 20], resourceObject.Attributes);
        Assert.Empty(host.Calls);
    }

    // Notification broadcasts validate the target stack object in the lot table before dispatching and
    // exclude the subject from receiving notifications.
    [Theory]
    [InlineData(short.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(short.MaxValue)]
    public void CallThirteenRejectsMissingTableEntriesBeforeNotifying(short index)
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: index);
        SetNotificationRecipients(context.World, me);
        Assert.Equal(SimulationError.ReferenceToTreeParameterWhenNoParameterExists,
            Fails(() => GenericSimsCallPrimitive.Execute(context, Operand(0x0d))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallThirteenWithNoRecipientsAnswersTrueAndKeepsState()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 3);
        context.World.WalkOrder.Clear();
        var subject = context.World.Add(new XObject(7, PrimitiveTestWorld.Definition()));
        me.SetObjectTableEntry(3, subject);
        me.Temporaries[0] = 42;
        var temporaries = me.Temporaries.ToArray();
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0d)));
        Assert.Empty(host.Calls);
        Assert.Equal(temporaries, me.Temporaries);
        Assert.Equal((short)3, context.Frame.StackObject);
        Assert.Same(subject, context.World.ObjectById(subject.Id));
    }

    [Fact]
    public void CallThirteenWithNoRecipientsDoesNotNeedAHost()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XObject(100, PrimitiveTestWorld.Definition()));
        me.SetObjectTableEntry(3, world.Add(new XObject(7, PrimitiveTestWorld.Definition())));
        var context = new TreeContext(world, me, new StackFrame(3, [], []));

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0d)));
    }

    [Fact]
    public void CallThirteenOrdinaryObjectRecipientsUseTheSourceNoOpWithoutAHost()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XObject(100, PrimitiveTestWorld.Definition()));
        var subject = world.Add(new XObject(7, PrimitiveTestWorld.Definition()));
        var ordinary = world.Add(new XObject(8, PrimitiveTestWorld.Definition()));
        me.SetObjectTableEntry(3, subject);
        SetNotificationRecipients(world, ordinary);
        var context = new TreeContext(world, me, new StackFrame(3, [], []));

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0d)));
    }

    [Fact]
    public void CallThirteenClearsMatchingRuntimeReferencesWhenSubjectIsPerson()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 3);
        var subject = new XPerson(7, PrimitiveTestWorld.Definition(XPerson.PersonType));
        var recipient = new XPerson(8, PrimitiveTestWorld.Definition(XPerson.PersonType));
        var other = context.World.Add(new XObject(9, PrimitiveTestWorld.Definition()));
        me.SetObjectTableEntry(3, subject);
        SetNotificationRecipients(context.World, recipient);
        var matching = new PersonRuntimeObjectReference(subject, 0, 42);
        var nonmatching = new PersonRuntimeObjectReference(other, 3, 99);
        recipient.RuntimeObjectReferences.AddRange([matching, nonmatching, new PersonRuntimeObjectReference(subject)]);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0d)));

        Assert.Null(matching.Target);
        Assert.Equal((byte)1, matching.State84);
        Assert.Equal(0, matching.RawWord90);
        Assert.Same(other, nonmatching.Target);
        Assert.Equal((byte)3, nonmatching.State84);
        Assert.Equal(99, nonmatching.RawWord90);
        Assert.Null(recipient.RuntimeObjectReferences[2].Target);
        Assert.Equal((byte)1, recipient.RuntimeObjectReferences[2].State84);
        Assert.Contains("NotifyPersonObjectReference(8,7)", host.Calls);
    }

    [Fact]
    public void CallThirteenLeavesRuntimeReferencesWhenSubjectIsNotPerson()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 3);
        var subject = context.World.Add(new XObject(7, PrimitiveTestWorld.Definition()));
        var recipient = context.World.Add(new XPerson(8, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        me.SetObjectTableEntry(3, subject);
        SetNotificationRecipients(context.World, recipient);
        var reference = new PersonRuntimeObjectReference(subject, 4, 88);
        recipient.RuntimeObjectReferences.Add(reference);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0d)));

        Assert.Same(subject, reference.Target);
        Assert.Equal((byte)4, reference.State84);
        Assert.Equal(88, reference.RawWord90);
        Assert.Contains("NotifyPersonObjectReference(8,7)", host.Calls);
    }

    [Fact]
    public void CallThirteenPersonRecipientsWithoutAHostRunOnlyTheNativeCleanup()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XObject(100, PrimitiveTestWorld.Definition()));
        var subject = world.Add(new XObject(7, PrimitiveTestWorld.Definition()));
        me.SetObjectTableEntry(3, subject);
        SetNotificationRecipients(world, new XPerson(8, PrimitiveTestWorld.Definition()));
        var context = new TreeContext(world, me, new StackFrame(3, [], []));

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0d)));
    }

    [Fact]
    public void CallThirteenNotifiesInOrderSkippingOnlyTheSubject()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 3);
        var subject = new XObject(7, PrimitiveTestWorld.Definition());
        var first = new XPerson(8, PrimitiveTestWorld.Definition());
        var sameIdDifferentObject = new XPerson(7, PrimitiveTestWorld.Definition());
        me.SetObjectTableEntry(3, subject);
        me.SetObjectTableEntry(4, first);
        me.Temporaries[0] = 4; // The subject is taken from the stack frame rather than temporary 0.
        SetNotificationRecipients(context.World, subject, first, me, subject, first, sameIdDifferentObject);
        var notified = new List<XObject>();
        host.OnPersonObjectReferenceNotification = (recipient, argument) =>
        {
            Assert.NotEqual(0u, recipient.Flags & 0x00000800u);
            Assert.Same(subject, argument);
            notified.Add(recipient);
        };
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0d)));
        Assert.Equal([first, me, first, sameIdDifferentObject], notified);
        Assert.Equal(["NotifyPersonObjectReference(8,7)", "NotifyPersonObjectReference(100,7)",
            "NotifyPersonObjectReference(8,7)",
            "NotifyPersonObjectReference(7,7)"], host.Calls);
        Assert.All(notified, recipient => Assert.Equal(0u, recipient.Flags & 0x00000800u));
    }

    [Fact]
    public void CallThirteenRechecksTheLiveRecipientEndAfterEachCallback()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 3);
        var subject = context.World.Add(new XObject(7, PrimitiveTestWorld.Definition()));
        var first = context.World.Add(new XPerson(8, PrimitiveTestWorld.Definition()));
        var appended = context.World.Add(new XPerson(9, PrimitiveTestWorld.Definition()));
        me.SetObjectTableEntry(3, subject);
        SetNotificationRecipients(context.World, first);
        host.OnPersonObjectReferenceNotification = (_, _) =>
        {
            if (context.World.WalkOrder.Count == 1) context.World.WalkOrder.Add(appended);
        };

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0d)));

        Assert.Equal(["NotifyPersonObjectReference(8,7)", "NotifyPersonObjectReference(9,7)"], host.Calls);
    }

    [Fact]
    public void CallThirteenAllowsANonPersonRunner()
    {
        var host = new PrimitiveTestHost();
        var world = new SimulationWorld { Host = host };
        var me = world.Add(new XObject(100, PrimitiveTestWorld.Definition()));
        var subject = new XObject(7, PrimitiveTestWorld.Definition());
        me.SetObjectTableEntry(3, subject);
        SetNotificationRecipients(world, me);
        var context = new TreeContext(world, me, new StackFrame(3, [], []));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0d)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallThirteenPropagatesReceiverFailureAndStopsTheBroadcast()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 3);
        me.SetObjectTableEntry(3, new XObject(7, PrimitiveTestWorld.Definition()));
        var person = new XPerson(8, PrimitiveTestWorld.Definition());
        SetNotificationRecipients(context.World, new XObject(9, PrimitiveTestWorld.Definition()), person);
        var failure = new InvalidOperationException("receiver failed");
        host.OnPersonObjectReferenceNotification = (_, _) => throw failure;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(
            () => GenericSimsCallPrimitive.Execute(context, Operand(0x0d))));
        Assert.Equal(["NotifyPersonObjectReference(8,7)"], host.Calls);
        Assert.Equal(0u, person.Flags & 0x00000800u);
    }

    [Fact]
    public void CallThirteenSkipsAReentrantPersonCallback()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 3);
        me.SetObjectTableEntry(3, new XObject(7, PrimitiveTestWorld.Definition()));
        var person = new XPerson(8, PrimitiveTestWorld.Definition()) { Flags = 0x00000800u };
        SetNotificationRecipients(context.World, person);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0d)));

        Assert.Empty(host.Calls);
        Assert.Equal(0x00000800u, person.Flags & 0x00000800u);
    }

    [Fact]
    public void CallThirteenRemovesQueuedActionsReferencingTheSubjectInRingOrder()
    {
        var (context, person, host) = PrimitiveTestWorld.Create(stackObject: 3);
        var subject = new XObject(7, PrimitiveTestWorld.Definition());
        var other = new XObject(8, PrimitiveTestWorld.Definition());
        var third = new XObject(9, PrimitiveTestWorld.Definition());
        person.SetObjectTableEntry(3, subject);
        SetNotificationRecipients(context.World, person);
        person.ActionQueueCurrent = 6;
        person.ActionQueueEnd = 11;
        person.ActionQueue[6] = new ActionQueueSlot { Owner = subject, Secondary = subject };
        person.ActionQueue[7] = new ActionQueueSlot { Owner = other, Secondary = other };
        person.ActionQueue[0] = new ActionQueueSlot { Owner = other, Secondary = subject };
        person.ActionQueue[1] = new ActionQueueSlot { Owner = other, Secondary = third };
        person.ActionQueue[2] = new ActionQueueSlot { Secondary = subject };
        host.OnPersonObjectReferenceNotification = (recipient, argument) =>
        {
            Assert.Same(person, recipient);
            Assert.Same(subject, argument);
            Assert.Equal(0, person.ActionQueueCurrent);
            Assert.Equal(2, person.ActionQueueEnd);
            Assert.Same(other, person.ActionQueue[0].Owner);
            Assert.Same(other, person.ActionQueue[0].Secondary);
            Assert.Same(other, person.ActionQueue[1].Owner);
            Assert.Same(third, person.ActionQueue[1].Secondary);
        };

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0d)));

        Assert.Equal([
            new ActionQueueRemovalNotice(person.RequestKey, 0),
            new ActionQueueRemovalNotice(person.RequestKey, 2),
        ], context.World.ActionQueueRemovalNotices);
    }

    [Fact]
    public void CallThirteenLeavesTheRingUntouchedWhenNoActionReferencesTheSubject()
    {
        var (context, person, host) = PrimitiveTestWorld.Create(stackObject: 3);
        var subject = new XObject(7, PrimitiveTestWorld.Definition());
        var other = new XObject(8, PrimitiveTestWorld.Definition());
        person.SetObjectTableEntry(3, subject);
        SetNotificationRecipients(context.World, person);
        person.ActionQueueCurrent = 5;
        person.ActionQueueEnd = 7;
        person.ActionQueue[5] = new ActionQueueSlot { Owner = other, Secondary = other };
        person.ActionQueue[6] = new ActionQueueSlot { Owner = other, Secondary = other };

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0d)));

        Assert.Equal(5, person.ActionQueueCurrent);
        Assert.Equal(7, person.ActionQueueEnd);
        Assert.Same(other, person.ActionQueue[5].Owner);
        Assert.Same(other, person.ActionQueue[6].Secondary);
        Assert.Empty(context.World.ActionQueueRemovalNotices);
        Assert.Single(host.Calls);
    }

    [Fact]
    public void CallThirteenFullResetClearsTheBackedUpSubjectOwner()
    {
        var (context, person, host) = PrimitiveTestWorld.Create(stackObject: 3);
        var subject = new XObject(7, PrimitiveTestWorld.Definition());
        var other = new XObject(8, PrimitiveTestWorld.Definition());
        person.SetObjectTableEntry(3, subject);
        SetNotificationRecipients(context.World, person);
        person.CurrentActionRecord = new ActionQueueSlot { Owner = other, Secondary = subject };
        person.ApplyStartedActionState(new ActionQueueSlot { Owner = subject, Secondary = subject });
        host.OnPersonObjectReferenceNotification = (recipient, _) =>
        {
            Assert.Null(recipient.PreviousActionRecord!.Owner);
            Assert.Null(recipient.CurrentActionRecord!.Owner);
            Assert.Null(recipient.CurrentActionRecord.Secondary);
        };

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0d)));

        Assert.Null(person.PreviousActionRecord!.Owner);
        Assert.Null(person.CurrentActionRecord!.Owner);
        Assert.Null(person.CurrentActionRecord.Secondary);
    }

    [Fact]
    public void CallThirteenFullResetBacksUpCurrentOwnerThenClearsItsSubjectSecondary()
    {
        var (context, person, _) = PrimitiveTestWorld.Create(stackObject: 3);
        var subject = new XObject(7, PrimitiveTestWorld.Definition());
        var other = new XObject(8, PrimitiveTestWorld.Definition());
        person.SetObjectTableEntry(3, subject);
        SetNotificationRecipients(context.World, person);
        person.CurrentActionRecord = new ActionQueueSlot { Owner = subject, Secondary = subject };
        person.ApplyStartedActionState(new ActionQueueSlot { Owner = other, Secondary = subject });

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0d)));

        Assert.NotNull(person.PreviousActionRecord);
        Assert.Same(other, person.PreviousActionRecord!.Owner);
        Assert.Null(person.PreviousActionRecord.Secondary);
        Assert.Null(person.CurrentActionRecord!.Secondary);
    }

    [Fact]
    public void CallThirteenRunsTheOriginalEmptyRecordTransitionWhenCurrentOwnerMatches()
    {
        var (context, person, host) = PrimitiveTestWorld.Create(stackObject: 3);
        var subject = new XObject(7, PrimitiveTestWorld.Definition());
        person.SetObjectTableEntry(3, subject);
        SetNotificationRecipients(context.World, person);
        person.CurrentActionRecord = new ActionQueueSlot { Owner = subject, Secondary = subject, Threshold = 4 };
        person.CurrentActionWord5D8 = 1;
        person.CurrentActionWord5DA = 2;
        person.PersonData[(int)PersonData.NonInterruptible] = 1;
        person.PersonData[(int)PersonData.RouteFootprintMask] = 3;
        person.MotiveChanges.Add(new MotiveChange(Motive.Hunger, 0, -1));
        host.OnPersonObjectReferenceNotification = (recipient, _) =>
        {
            Assert.Null(recipient.CurrentActionRecord!.Owner);
            Assert.Null(recipient.CurrentActionRecord.Secondary);
            Assert.Equal(-1, recipient.CurrentActionRecord.RawDword10);
            Assert.Same(subject, recipient.PreviousActionRecord!.Owner);
            Assert.Equal(0, recipient.ActivePriority);
            Assert.Equal(1, recipient.CurrentActionWord5D8);
            Assert.Equal(2, recipient.CurrentActionWord5DA);
            Assert.Equal(0, recipient[PersonData.NonInterruptible]);
            Assert.Equal(0, recipient[PersonData.RouteFootprintMask]);
            Assert.Empty(recipient.MotiveChanges);
        };

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x0d)));

        Assert.Equal(0, person.CurrentActionRecord!.Sequence);
        Assert.Equal(-1, person.CurrentActionRecord.RawDword10);
    }

    [Theory]
    [InlineData(0x04)]
    [InlineData(0x21)]
    public void AddToFamilyCallsRaiseNeighborNotFoundForAStackObjectThatIsNoNeighbor(int call)
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 7);
        context.World.Host = null;
        Assert.Equal(SimulationError.NeighborNotFound,
            Fails(() => GenericSimsCallPrimitive.Execute(context, Operand((byte)call))));
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(0x04)]
    [InlineData(0x21)]
    public void AddToFamilyCallsAnswerFalseWithoutTheirFamily(int call)
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 7);
        var (neighbor, families) = FamilyNeighbor(context.World, 0);
        families.Active = 0;
        me.Temporaries[0] = 9;

        Assert.Equal(PrimitiveResult.False, GenericSimsCallPrimitive.Execute(context, Operand((byte)call)));

        Assert.Empty(host.Calls);
        Assert.Equal((short)0, neighbor.PersonData[(int)PersonData.FamilyNumber]);
    }

    [Theory]
    [InlineData(0x04)]
    [InlineData(0x21)]
    public void AddToFamilyCallsMoveTheNeighborAsMacAddToFamily(int call)
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 7);
        var person = (XPerson)me;
        person.TypeRecord = new TypeRecord(0x5150);
        person[PersonData.LingeringHouseNumber] = 12;
        var (neighbor, families) = FamilyNeighbor(context.World, 3);
        neighbor.PersonData[(int)PersonData.LingeringHouseNumber] = 12;
        families.Members[5] = [0x7170];
        families.Active = 5;
        me.Temporaries[0] = 5;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand((byte)call)));

        Assert.Equal(["NotifyNeighborInteractionChanged", "NotifyNeighborInteractionChanged"], host.Calls);
        Assert.Equal([0x6160], families.Members[3]);
        Assert.Equal([0x7170, 0x5150], families.Members[5]);
        Assert.Equal((short)5, neighbor.PersonData[(int)PersonData.FamilyNumber]);
        Assert.Equal((short)0, neighbor.PersonData[(int)PersonData.LingeringHouseNumber]);
        Assert.Equal((short)5, person[PersonData.FamilyNumber]);
        Assert.Equal((short)0, person[PersonData.LingeringHouseNumber]);
        Assert.Contains((short)5, context.World.DirtyRelationshipFamilies);
    }

    [Fact]
    public void AddToFamilyAnswersFalseWhenTheFamilyAlreadyListsTheNeighbor()
    {
        var (context, me, host) = PrimitiveTestWorld.Create(stackObject: 7);
        var (neighbor, families) = FamilyNeighbor(context.World, 0);
        families.Members[5] = [0x5150];
        me.Temporaries[0] = 5;

        Assert.Equal(PrimitiveResult.False, GenericSimsCallPrimitive.Execute(context, Operand(0x21)));

        Assert.Empty(host.Calls);
        Assert.Equal((short)0, neighbor.PersonData[(int)PersonData.FamilyNumber]);
    }

    [Fact]
    public void NeighborTableUsesOneBasedOrderedEntriesWithNullGaps()
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        var third = new XObject(7, PrimitiveTestWorld.Definition());
        context.World.SetNeighborTableEntry(3, third);

        Assert.Null(context.World.NeighborTableEntry(0));
        Assert.Null(context.World.NeighborTableEntry(1));
        Assert.Null(context.World.NeighborTableEntry(2));
        Assert.Same(third, context.World.NeighborTableEntry(3));
        Assert.Null(context.World.NeighborTableEntry(4));
    }

    [Fact]
    public void CallFiveReturnsFalseWithoutValuingOrChargingWhenNoTableWordMatches()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 12;
        context.World.Host = null;
        var budget = new Budget();
        context.World.FamilyBudget = budget;
        Assert.Equal(PrimitiveResult.False, GenericSimsCallPrimitive.Execute(context, Operand(0x05)));
        Assert.Empty(host.Calls);
        Assert.Empty(budget.Charges);
        Assert.Equal(1000, budget.Funds);
    }

    [Fact]
    public void CallFiveUsesTheFirstMatchingObjectInManagerVectorOrder()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var first = new XObject(8, PrimitiveTestWorld.Definition()) { TableWord10C = 12, ObjectValueWord118 = 17 };
        var second = new XObject(9, PrimitiveTestWorld.Definition()) { TableWord10C = 12, ObjectValueWord118 = 25 };
        context.World.InteractionLookupOrder.AddRange([first, second]);
        me.Temporaries[0] = 12;
        var budget = new Budget();
        context.World.FamilyBudget = budget;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x05)));
        Assert.Equal([(2, -17)], budget.Charges);
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(250, -250)]
    [InlineData(-90, 90)]
    public void CallFiveChargesTheComputedAmountAsExpenseTypeTwo(int value, int expectedAmount)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var item = new XObject(8, PrimitiveTestWorld.Definition()) { TableWord10C = 12, ObjectValueWord118 = value };
        context.World.InteractionLookupOrder.Add(item);
        me.Temporaries[0] = 12;
        var budget = new Budget();
        context.World.FamilyBudget = budget;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x05)));
        Assert.Empty(host.Calls);
        Assert.Equal([(2, expectedAmount)], budget.Charges);
        Assert.Equal(1000 - expectedAmount, budget.Funds);
    }

    [Theory]
    [InlineData(0, 1021)]
    [InlineData(1, 1000020)]
    public void CallFiveUsesConfiguredSpecialArchitectureValuePath(int mode, int expectedAmount)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var item = new XObject(8, PrimitiveTestWorld.Definition())
        {
            TableWord10C = 12,
            ObjectValueWord118 = 100,
            ObjectValueWord11C = 200,
            Position = new ObjectPosition(0, 0, 7),
        };
        context.World.InteractionLookupOrder.Add(item);
        context.World.GenericSimsCall05ManagerWord164 = 7;
        context.World.ObjectValueGlobalWord50 = mode;
        context.World.ObjectValueGlobalWord90 = 1000;
        context.World.ObjectValueGlobalWord9C = 3;
        context.World.ObjectValueGlobalWordA0 = 2;
        context.World.ObjectValueGlobalWordA4 = 13;
        context.World.ArchitectureValueMultiplier = 1.25f;
        me.Temporaries[0] = 12;
        var budget = new Budget();
        context.World.FamilyBudget = budget;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x05)));
        Assert.Equal([(2, -expectedAmount)], budget.Charges);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CallFiveReportsMissingConfigurationOnSpecialArchitectureValuePath()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        var item = new XObject(8, PrimitiveTestWorld.Definition())
        {
            TableWord10C = 12,
            Position = new ObjectPosition(0, 0, 7),
        };
        context.World.InteractionLookupOrder.Add(item);
        context.World.GenericSimsCall05ManagerWord164 = 7;
        me.Temporaries[0] = 12;
        context.World.FamilyBudget = new Budget();

        var error = Assert.Throws<InvalidOperationException>(
            () => GenericSimsCallPrimitive.Execute(context, Operand(0x05)));
        Assert.Contains("ObjectValueGlobalWord50", error.Message);
    }

    [Fact]
    public void CallFiveTruncatesTheSpecialScaledValueUsingOriginalExtendedPrecision()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        var item = new XObject(8, PrimitiveTestWorld.Definition())
        {
            TableWord10C = 12,
            Position = new ObjectPosition(0, 0, 7),
        };
        context.World.InteractionLookupOrder.Add(item);
        context.World.GenericSimsCall05ManagerWord164 = 7;
        context.World.ObjectValueGlobalWord50 = 0;
        context.World.ObjectValueGlobalWord90 = 0;
        context.World.ObjectValueGlobalWord9C = 0;
        context.World.ObjectValueGlobalWordA0 = 0;
        context.World.ObjectValueGlobalWordA4 = 16_777_217;
        context.World.ArchitectureValueMultiplier = 1.00000011920928955078125f;
        me.Temporaries[0] = 12;
        var budget = new Budget();
        context.World.FamilyBudget = budget;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand(0x05)));
        Assert.Equal([(2, -16_777_219)], budget.Charges);
    }

    [Fact]
    public void CallFiveRequiresAFamilyBudgetOnlyAfterFindingTheObjectAndItsValue()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var item = new XObject(8, PrimitiveTestWorld.Definition()) { TableWord10C = 12, ObjectValueWord118 = 12 };
        context.World.InteractionLookupOrder.Add(item);
        me.Temporaries[0] = 12;
        Assert.Throws<InvalidOperationException>(() => GenericSimsCallPrimitive.Execute(context, Operand(0x05)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void CaseAddressesFollowTheJumpTable()
    {
        Assert.Equal(0x2c, GenericSimsCallPrimitive.CaseAddresses.Count);
        Assert.Equal(0x44f830u, GenericSimsCallPrimitive.CaseAddresses[0x29]);
        Assert.Equal(0x45145fu, GenericSimsCallPrimitive.CaseAddresses[7]);
        Assert.Equal(0x45145fu, GenericSimsCallPrimitive.CaseAddresses[0x16]);
    }
}
