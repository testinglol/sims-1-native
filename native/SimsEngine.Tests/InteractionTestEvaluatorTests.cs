using SimsEngine.Formats;
using SimsEngine.Simulation;
using FormatMotiveAdvertisement = SimsEngine.Formats.MotiveAdvertisement;

namespace SimsEngine.Tests;

public sealed class InteractionTestEvaluatorTests
{
    private static InteractionTable Table(uint flags = 0, ushort guard = 0, FormatMotiveAdvertisement? motive = null)
    {
        var builder = new ChunkBuilder().S16(1).S16(6).U16(4096).U16(guard).S32(motive is null ? 0 : 1)
            .U32(flags).S32(7).S32(0).F32(0).S32(0).S32(-1);
        if (motive is { } entry) builder.S16(entry.Minimum).S16(entry.Delta).S16(entry.Personality);
        var (file, chunk) = builder.InFile("TTAB");
        return InteractionTable.Read(file, chunk);
    }

    private static (SimulationWorld World, XPerson Person, XObject Target, ActionQueueSlot Action) Lot(uint flags = 0,
        ushort guard = 0, ITreeTable? scope = null, FormatMotiveAdvertisement? motive = null)
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(2)));
        var target = world.Add(new XObject(2, PrimitiveTestWorld.Definition())
        { TypeRecord = new TypeRecord(22) { Interactions = Table(flags, guard, motive), BehaviorScope = scope } });
        return (world, person, target, new ActionQueueSlot { Person = person, Owner = target, InteractionId = 7 });
    }

    private static Bhav Tree(params (ushort Opcode, byte True, byte False, byte[] Operand)[] code)
    {
        var builder = new ChunkBuilder().U16(0x8002).S16((short)code.Length).U8(0).U8(4).U8(0).U8(0).U32(0);
        foreach (var item in code) builder.U16(item.Opcode).U8(item.True).U8(item.False).Raw(item.Operand);
        var (file, chunk) = builder.InFile("BHAV"); return Bhav.Read(file, chunk);
    }

    private static bool Passed(ActionQueueSlot action) => (action.Flags & InteractionTestEvaluator.Passed) != 0;

    [Theory]
    [InlineData(0, 18, 0, true)]
    [InlineData(0x40, 18, 0, false)]
    [InlineData(0x10, 17, 0, false)]
    [InlineData(0, 17, 0, true)]
    [InlineData(0x10, 0, 0, true)]
    [InlineData(0x10, -1, 0, false)]
    [InlineData(0, 18, 2, false)]
    [InlineData(0x400, 18, 2, true)]
    [InlineData(0x200, 18, 2, false)]
    [InlineData(0x200, 18, 4, true)]
    [InlineData(0x400, 18, 4, false)]
    public void AgeAndPetFlagsFollowTheOriginalGate(uint flags, short age, short gender, bool expected)
    {
        var (world, person, _, action) = Lot(flags);
        person[PersonData.PersonAge] = age; person[PersonData.Gender] = gender;
        InteractionTestEvaluator.Evaluate(world, person, [action], false);
        Assert.Equal(expected, Passed(action));
        Assert.NotEqual(0, action.Flags & InteractionTestEvaluator.Evaluated);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void BusyOnlyBlocksAutonomyWhileTargetFlagBlocksBoth(bool autonomy, bool disabled, bool expected)
    {
        var (world, person, target, action) = Lot();
        target[ObjectData.LockoutCount] = 1;
        target[ObjectData.Flags] = disabled ? (short)0x200 : (short)0;
        InteractionTestEvaluator.Evaluate(world, person, [action], autonomy);
        Assert.Equal(expected, Passed(action));
    }

    [Fact]
    public void PositiveUseCountAloneDoesNotBlockAutonomyGuard()
    {
        var (world, person, target, action) = Lot();
        target[ObjectData.UseCount] = 1;

        InteractionTestEvaluator.Evaluate(world, person, [action], autonomy: true);

        Assert.True(Passed(action));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void VisitorsNeedTheDescriptorPermission(uint flags, bool expected)
    {
        var (world, person, _, action) = Lot(flags); person.SelectionWord5CC = 1;
        InteractionTestEvaluator.Evaluate(world, person, [action], false);
        Assert.Equal(expected, Passed(action));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void DescriptorBitFivePermitsVisitorOnlyOutsideStateClassOne(int stateClass, bool expected)
    {
        var (world, person, _, action) = Lot(0x20);
        person.SelectionWord5CC = 1; person.StateValue = 42;
        world.StateClasses.Add(new StateClassEntry(42, stateClass));
        InteractionTestEvaluator.Evaluate(world, person, [action], false);
        Assert.Equal(expected, Passed(action));
    }

    [Fact]
    public void PersonTypeAndPriorityUseTheOriginalPersonDataStorage()
    {
        var (_, person, _, _) = Lot();
        person[PersonData.PersonType] = 2;
        Assert.Equal(2, person.SelectionWord5CC);
        person.SelectionWord5CC = 3;
        Assert.Equal(3, person[PersonData.PersonType]);
        person[PersonData.Priority] = 50;
        Assert.Equal(50, person.ActivePriority);
        person.ActivePriority = 100;
        Assert.Equal(100, person[PersonData.Priority]);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    public void VisitorInventoryForCategoriesFourAndSevenRestoresEligibilityOnlyForAPositiveAvatarEntry(short category)
    {
        var (world, person, target, action) = Lot();
        person.SelectionWord5CC = 1;
        target.Data[59] = category;
        var key = 0x1234;
        var scope = world.InteractionExecutorScope(person);
        scope.AvatarObjects.Add(new TypeRecord(99) { ObjectType = 13, AnimationKey = key });

        InteractionTestEvaluator.Evaluate(world, person, [action], false);

        Assert.False(Passed(action));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    public void VisitorInventoryForCategoriesFourAndSevenPassesWithPositiveMatchingCount(short category)
    {
        var (world, person, target, action) = Lot();
        person.SelectionWord5CC = 1;
        target.Data[59] = category;
        const int key = 0x1234;
        var scope = new InteractionScopeTable([new InteractionScopeEntry(0, key, 1)]);
        scope.AvatarObjects.Add(new TypeRecord(99) { ObjectType = 13, AnimationKey = key });
        world.InteractionScopes[person.InteractionScopeKey5CA] = scope;

        InteractionTestEvaluator.Evaluate(world, person, [action], false);

        Assert.True(Passed(action));
    }

    [Fact]
    public void VisitorInventoryDoesNotRestoreEligibilityForOtherObjectCategories()
    {
        var (world, person, target, action) = Lot();
        person.SelectionWord5CC = 1;
        target.Data[59] = 3;
        var scope = new InteractionScopeTable([new InteractionScopeEntry(0, 0x1234, 1)]);
        scope.AvatarObjects.Add(new TypeRecord(99) { ObjectType = 13, AnimationKey = 0x1234 });
        world.InteractionScopes[person.InteractionScopeKey5CA] = scope;

        InteractionTestEvaluator.Evaluate(world, person, [action], false);

        Assert.False(Passed(action));
    }

    [Fact]
    public void VisitorInventoryUsesTheSourceZeroKeyFallbackAndSignedCount()
    {
        var (world, person, target, action) = Lot();
        person.SelectionWord5CC = 1;
        target.Data[59] = 4;
        world.InteractionScopes[person.InteractionScopeKey5CA] =
            new InteractionScopeTable([new InteractionScopeEntry(0, 0, 0xffff)]);

        InteractionTestEvaluator.Evaluate(world, person, [action], false);

        Assert.False(Passed(action));
    }

    [Fact]
    public void GuardUsesTargetScopeAndStackKeyWithTheOriginalFourArguments()
    {
        var code = Enumerable.Range(0, 4).Select(index => ((ushort)2, index == 3 ? (byte)254 : (byte)(index + 1), (byte)255,
            PrimitiveTestWorld.Expression((short)index, (short)index, (byte)ExpressionOperator.AssignTo,
                (byte)DataOwner.StackObjectTemporary, (byte)DataOwner.Parameter))).ToArray();
        var (world, person, target, _) = Lot(guard: 4100, scope: new TreeTable((4100, Tree(code))));
        var action = new ActionQueueSlot { Person = person, Owner = target, InteractionId = 7, RecordKind = 1,
            Parameters = [0, 0, 93, 0] };
        InteractionTestEvaluator.Evaluate(world, person, [action], true);
        Assert.True(Passed(action));
        Assert.Equal(new short[] { 1, 1, 93, 0 }, target.Temporaries.Take(4));
    }

    [Fact]
    public void ShortSavedActionParametersAreZeroFilledForGuardArguments()
    {
        var tree = Tree((2, 254, 255, PrimitiveTestWorld.Expression(2, 0,
            (byte)ExpressionOperator.EqualTo, (byte)DataOwner.Parameter, (byte)DataOwner.Literal)));
        var (world, person, target, _) = Lot(guard: 4100, scope: new TreeTable((4100, tree)));
        var action = new ActionQueueSlot
        {
            Person = person,
            Owner = target,
            InteractionId = 7,
            RecordKind = 1,
            Parameters = [],
        };

        InteractionTestEvaluator.Evaluate(world, person, [action], autonomy: true);

        Assert.True(Passed(action));
    }

    [Fact]
    public void GuardReadsTheCurrentInteractionMotiveAdvertisementInSourceFieldOrder()
    {
        var tree = Tree(
            (2, 1, 255, PrimitiveTestWorld.Expression(0, 3, (byte)ExpressionOperator.EqualTo,
                (byte)DataOwner.CheckTreeAdPersonalityVariable, (byte)DataOwner.Literal)),
            (2, 2, 255, PrimitiveTestWorld.Expression(0, 50, (byte)ExpressionOperator.EqualTo,
                (byte)DataOwner.CheckTreeAdMin, (byte)DataOwner.Literal)),
            (2, 254, 255, PrimitiveTestWorld.Expression(0, 7, (byte)ExpressionOperator.EqualTo,
                (byte)DataOwner.CheckTreeAdRange, (byte)DataOwner.Literal)));
        var motive = new FormatMotiveAdvertisement(Minimum: 50, Delta: 7, Personality: 3);
        var (world, person, _, action) = Lot(guard: 4100,
            scope: new TreeTable((4100, tree)), motive: motive);
        world.CheckTreeAds = [9, 8, 7];

        InteractionTestEvaluator.Evaluate(world, person, [action], autonomy: true);

        Assert.True(Passed(action));
        Assert.Equal(new short[] { 9, 8, 7 }, world.CheckTreeAds);
    }

    [Fact]
    public void RelativeRouteInAvailabilityGuardReturnsFalseWithoutRequiringALiveHost()
    {
        var routeOperand = new byte[8];
        var tree = Tree((0x1b, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse, routeOperand));
        var (world, person, target, action) = Lot(guard: 4100, scope: new TreeTable((4100, tree)));
        world.Host = null;

        InteractionTestEvaluator.Evaluate(world, person, [action], false);

        Assert.False(Passed(action));
        Assert.False(world.EvaluatingInteractionTest);
    }

    [Fact]
    public void BatchSharesThenRestoresTempsAndPropagatesHiddenFlag()
    {
        var write = PrimitiveTestWorld.Expression(0, 123, (byte)ExpressionOperator.AssignTo);
        var hide = PrimitiveTestWorld.Expression((short)ObjectData.HideInteraction, 1, (byte)ExpressionOperator.AssignTo,
            (byte)DataOwner.My, (byte)DataOwner.Literal);
        var tree = Tree((2, 1, 255, write), (2, 254, 255, hide));
        var (world, person, _, action) = Lot(guard: 4100, scope: new TreeTable((4100, tree)));
        person.Temporaries[0] = 42;
        var nextTree = Tree((2, 254, 255, PrimitiveTestWorld.Expression(0, 0, (byte)ExpressionOperator.AssignTo,
            (byte)DataOwner.StackObjectTemporary, (byte)DataOwner.Temporary)));
        var second = world.Add(new XObject(3, PrimitiveTestWorld.Definition()) { TypeRecord = new TypeRecord(33)
            { Interactions = Table(guard: 4101), BehaviorScope = new TreeTable((4101, nextTree)) } });
        var secondAction = new ActionQueueSlot { Person = person, Owner = second, InteractionId = 7 };
        InteractionTestEvaluator.Evaluate(world, person, [action, secondAction], false);
        Assert.Equal(123, second.Temporaries[0]); Assert.True(Passed(secondAction));
        Assert.Equal(0, secondAction.Flags & InteractionTestEvaluator.Hidden);
        Assert.True(Passed(action)); Assert.NotEqual(0, action.Flags & InteractionTestEvaluator.Hidden);
        Assert.Equal(42, person.Temporaries[0]);
    }

    [Fact]
    public void SuspendingGuardPropagatesErrorAndRestoresNestedRunGates()
    {
        var tree = Tree((2, 1, 255, PrimitiveTestWorld.Expression(0, 123, (byte)ExpressionOperator.AssignTo)),
            (0, 254, 255, new byte[8]));
        var (world, person, _, action) = Lot(guard: 4100, scope: new TreeTable((4100, tree)));
        person.Temporaries[0] = 42; world.SelfRemovalEndsRun = true; world.ActionStringRecordWriteEnabled = true;
        var error = Assert.Throws<TreeRunException>(() => InteractionTestEvaluator.Evaluate(world, person, [action], true));
        Assert.Contains("interaction guard BHAV 4100 for object 2, type", error.Message);
        Assert.Contains("TTAB entry 7", error.Message);
        Assert.Equal(42, person.Temporaries[0]); Assert.True(world.SelfRemovalEndsRun);
        Assert.True(world.ActionStringRecordWriteEnabled); Assert.False(Passed(action));
    }

    [Fact]
    public void TreeBreakRejectsOnlyThatInteractionCandidateAndRecordsTheSourceError()
    {
        var breakPoint = new byte[] { 1, 0, (byte)DataOwner.Literal, 0, 0, 0, 0, 0 };
        var tree = Tree((0x0f, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse, breakPoint));
        var (world, person, target, action) = Lot(guard: 4100, scope: new TreeTable((4100, tree)));
        person.Temporaries[0] = 42;

        InteractionTestEvaluator.Evaluate(world, person, [action], autonomy: true);

        Assert.False(Passed(action));
        Assert.NotEqual(0, action.Flags & InteractionTestEvaluator.Evaluated);
        Assert.Equal(42, person.Temporaries[0]);
        var error = Assert.Single(world.InteractionGuardErrors);
        Assert.Equal(person.Id, error.PersonId);
        Assert.Equal(target.Id, error.ObjectId);
        Assert.Equal(7, error.InteractionId);
        Assert.Equal(4100, error.GuardTree);
        Assert.Equal(SimulationError.TreeBreakEncountered, error.Error);
        Assert.Empty(world.ThreadErrors);
    }

    [Fact]
    public void StepLimitInGuardRejectsOnlyThatCandidateAndDoesNotCrashSimThread()
    {
        var tree = Tree((2, 0, 0, PrimitiveTestWorld.Expression(0, 0, (byte)ExpressionOperator.EqualTo)));
        var (world, person, target, action) = Lot(guard: 4107, scope: new TreeTable((4107, tree)));

        InteractionTestEvaluator.Evaluate(world, person, [action], autonomy: false);

        Assert.False(Passed(action));
        Assert.NotEqual(0, action.Flags & InteractionTestEvaluator.Evaluated);
        var error = Assert.Single(world.InteractionGuardErrors);
        Assert.Equal(person.Id, error.PersonId);
        Assert.Equal(target.Id, error.ObjectId);
        Assert.Equal(4107, error.GuardTree);
        Assert.Contains("StepLimit", error.Message);
        Assert.Empty(world.ThreadErrors);
    }
}
