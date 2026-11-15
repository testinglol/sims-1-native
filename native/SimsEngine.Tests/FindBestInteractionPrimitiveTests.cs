using SimsEngine.Simulation;
using Interaction = SimsEngine.Formats.Interaction;
using InteractionTable = SimsEngine.Formats.InteractionTable;
using Bhav = SimsEngine.Formats.Bhav;
using Xunit;

namespace SimsEngine.Tests;

public sealed class FindBestInteractionPrimitiveTests
{
    private const string FixtureTypeRecordName = "FindBestInteractionPrimitiveTests native TTAB fixture";
    [Fact]
    public void AutonomyUsesTheNineSourceOrderedMotiveSlots()
    {
        Assert.Equal(new short[] { 5, 6, 7, 8, 9, 3, 13, 14, 15 }, AutonomyMotiveCatalog.All);
        Assert.Throws<ArgumentOutOfRangeException>(() => AutonomyMotiveCatalog.EvaluateCurve(0, 12));
    }

    [Theory]
    [InlineData(-120, -100)]
    [InlineData(-100, -100)]
    [InlineData(-63.5, -63.5)]
    [InlineData(0, 0)]
    [InlineData(78, 78)]
    [InlineData(100, 100)]
    [InlineData(120, 100)]
    public void AutonomyMotiveCurvesAreIdentityBetweenTheSourceEndpointsAndClampOutside(float input, float expected)
    {
        Assert.Equal(expected, AutonomyMotiveCatalog.EvaluateCurve(5, input));
    }

    [Fact]
    public void TtabIndexIsTheFoundActionKeyRatherThanTheActionTreeOrAutonomyThreshold()
    {
        // FUN_004760ae reads TtaIndex at +0x18; FUN_0045a9c5 matches that key in the person's table.
        var source = new Interaction(0x4567, 0x2345, 0x100, 9, 1, 0, 0x1234, 0, [], null);

        var record = InteractionRecord.FromTtab(source, passed: true, new MotiveAdvertisement[16]);

        Assert.Equal((short)9, record.Action);
        Assert.NotEqual((short)source.ActionTree, record.Action);
        Assert.NotEqual((short)source.AutonomyThreshold, record.Action);
    }

    [Fact]
    public void NativeTtabMotiveFieldsMapToAutonomyKindBaseAndScaleInSourceOrder()
    {
        var source = new Interaction(0x4567, 0, 0, 9, 1, 0, 0, 0,
            [new SimsEngine.Formats.MotiveAdvertisement(40, -20, 3)], null);

        var record = InteractionRecord.FromTtab(source, passed: true);

        Assert.Equal(new MotiveAdvertisement(3, 40, -20), record.Advertisement[0]);
        Assert.All(record.Advertisement.Skip(1), motive => Assert.Equal(default, motive));
    }

    // A candidate with an interaction table (definition field 7) at the origin.
    private static XObject Candidate(short id, short type = 0)
    {
        var definition = PrimitiveTestWorld.Definition(type);
        definition[7] = 1;
        return new XObject(id, definition);
    }

    // Give each source-selected motive the same fixture entry so the nine-row mean preserves the focused test score.
    private static MotiveAdvertisement[] Advertisement(short baseValue, short kind = 0, short scale = 0)
    {
        var table = new MotiveAdvertisement[16];
        foreach (var motive in AutonomyMotiveCatalog.All)
            table[motive] = new MotiveAdvertisement(kind, baseValue, scale);
        return table;
    }

    // A passed record: the default family attenuation code 1 is none, so the weight is 1 unless a test changes the code.
    private static InteractionRecord Record(short action, short baseValue = 300, bool autoFirst = false, short code = 1, short kind = 0, short scale = 0) =>
        new(autoFirst, true, code, 0, Advertisement(baseValue, kind, scale), action);

    // The person (id 100) and the candidates, in table order from 1. The lot is forced, so the gate is the body's.
    private static (TreeContext Context, XPerson Me, PrimitiveTestHost Host) Setup(params XObject[] candidates)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me[PersonData.AutonomyLevel] = 100;
        for (var i = 0; i < candidates.Length; i++)
        {
            var index = (short)(i + 1);
            host.ObjectTable[index] = candidates[i];
            context.World.Add(candidates[i]);
            context.World.ClockManager.SetFlag(candidates[i].ClockManagerIndex, 3, enabled: true);
        }
        return (context, me, host);
    }

    private static PrimitiveResult Run(TreeContext context)
    {
        if (context.World.Host is PrimitiveTestHost host)
        {
            foreach (var (objectId, fixtures) in host.Records)
            {
                var candidate = context.World.ObjectById(objectId);
                if (candidate is null || (candidate.TypeRecord?.Interactions is not null &&
                    candidate.TypeRecord.Name != FixtureTypeRecordName)) continue;
                var table = NativeTable(fixtures, out var scope);
                candidate.TypeRecord = new TypeRecord(candidate.TypeRecord?.Key ?? objectId)
                {
                    ObjectType = candidate.TypeRecord?.ObjectType ?? -1,
                    AutonomyAlternativeTypeFlag = candidate.TypeRecord?.AutonomyAlternativeTypeFlag ?? 0,
                    Name = FixtureTypeRecordName,
                    BehaviorScope = scope,
                    Interactions = table,
                };
            }
        }
        return FindBestInteractionPrimitive.Execute(context, []);
    }

    private static InteractionTable NativeTable(ushort guardTree, uint flags = 0x100, short minimum = 300)
    {
        var advertisement = Advertisement(minimum);
        var builder = new ChunkBuilder().S16(1).S16(6).U16(0x4567).U16(guardTree).S32(16)
            .U32(flags).S32(9).S32(1).F32(0).S32(50).S32(0);
        foreach (var motive in advertisement)
            builder.S16(motive.Base).S16(motive.Scale).S16(motive.Kind);
        var (file, chunk) = builder.InFile("TTAB");
        return InteractionTable.Read(file, chunk);
    }

    private static InteractionTable EmptyNativeTable()
    {
        var (file, chunk) = new ChunkBuilder().S16(0).S16(6).InFile("TTAB");
        return InteractionTable.Read(file, chunk);
    }

    private static InteractionTable NativeTable(IReadOnlyList<InteractionRecord> fixtures, out TreeTable scope)
    {
        var builder = new ChunkBuilder().S16((short)fixtures.Count).S16(6);
        var trees = new List<(short Id, Bhav Tree)>();
        for (var index = 0; index < fixtures.Count; index++)
        {
            var fixture = fixtures[index];
            var treeId = unchecked((ushort)(0x7000 + index));
            // These fixtures represent the already-passed action records used by the score tests. Set the TTAB
            // descriptor permissions needed for visitors, children, pets and female sims; guard outcomes remain explicit.
            builder.U16(unchecked((ushort)(4100 + index))).U16(treeId).S32(16)
                .U32(0x1 | 0x10 | 0x200 | 0x400 | (fixture.AutoFirst ? 0x100u : 0u)).S32(fixture.Action)
                .S32(fixture.AttenuationCode).F32(fixture.CustomAttenuation)
                .S32(50).S32(0);
            foreach (var motive in fixture.Advertisement)
                builder.S16(motive.Base).S16(motive.Scale).S16(motive.Kind);
            trees.Add((unchecked((short)treeId), Guard(fixture.Passed)));
        }
        var (file, chunk) = builder.InFile("TTAB");
        scope = new TreeTable(trees.ToArray());
        return InteractionTable.Read(file, chunk);
    }

    private static Bhav Guard(bool pass) => PrimitiveTestWorld.Tree(0,
        (2, 254, 255, PrimitiveTestWorld.Expression(0, pass ? (short)0 : (short)1,
            (byte)ExpressionOperator.EqualTo)));

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void HydratedTtabGuardDeterminesAutonomyEligibilityInsteadOfHostPassedFlag(bool guardPasses, bool chosen)
    {
        var candidate = Candidate(5);
        var (context, me, host) = Setup(candidate);
        var guard = Guard(guardPasses);
        candidate.TypeRecord = new TypeRecord(55)
        {
            Interactions = NativeTable(4100),
            BehaviorScope = new TreeTable((4100, guard)),
        };
        // The host deliberately disagrees; its legacy record must not decide a hydrated native TTAB candidate.
        host.Records[5] = [Record(action: 99, baseValue: 900, code: 1) with { Passed = !guardPasses }];

        Assert.Equal(chosen ? PrimitiveResult.True : PrimitiveResult.False, Run(context));
        Assert.Equal(chosen ? (short)5 : (short)0, context.Frame.StackObject);
        Assert.Equal(chosen ? (short)9 : (short)0, me.InteractionActionWord72);
    }

    [Fact]
    public void TheHighestScoredCandidateIsChosenWhenTheRandomIndexIsZero()
    {
        var (context, me, host) = Setup(Candidate(5), Candidate(6));
        host.Records[5] = [Record(action: 7, baseValue: 300)];
        host.Records[6] = [Record(action: 9, baseValue: 500)];

        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(6, context.Frame.StackObject);
        Assert.Equal((short)9, me.InteractionActionWord72);
        Assert.Equal(0, host.RandomCalls);
    }

    [Fact]
    public void FindBestInteractionWritesTheSelectedRecordKeyToThePersonWord()
    {
        // FUN_0045dea5 stores the TTAB row's +0x18 word; FUN_0045a9c5 later uses that key to find the person's row.
        var (context, me, host) = Setup(Candidate(5));
        host.Records[5] = [Record(action: 0x1234, baseValue: 500)];

        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal((short)0x1234, me.InteractionActionWord72);
    }

    [Fact]
    public void CandidateIterationUsesTheNativeObjectTableWhenHostEntryConflicts()
    {
        var nativeCandidate = Candidate(5);
        var hostOnlyCandidate = Candidate(6);
        var (context, me, host) = Setup(nativeCandidate);
        host.ObjectTable[1] = hostOnlyCandidate;
        host.Records[5] = [Record(action: 7, baseValue: 500)];
        host.Records[6] = [Record(action: 8, baseValue: 900)];

        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(5, context.Frame.StackObject);
        Assert.Equal((short)7, me.InteractionActionWord72);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("ObjectTableEntry", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("ObjectTableCount", StringComparison.Ordinal));
    }

    [Fact]
    public void ACandidateBelowTheFamilyMinimumIsNotChosenAndNothingIsWritten()
    {
        var (context, me, host) = Setup(Candidate(5));
        me.InteractionActionWord72 = 41;
        host.Records[5] = [Record(action: 7, baseValue: 100)];

        Assert.Equal(PrimitiveResult.False, Run(context));
        Assert.Equal(0, context.Frame.StackObject);
        Assert.Equal((short)41, me.InteractionActionWord72);
    }

    [Fact]
    public void TheBaselineOfTheMotiveCurveIsSubtractedFromTheAdvertisedMean()
    {
        // The person's motive 5 is 0.2. A base of 100 advertises 0.1 more to 0.3, so the score is 0.1 (below 0.2). A base of 300
        // advertises 0.3 more to 0.5, so the score is 0.3.
        var (context, me, host) = Setup(Candidate(5));
        me.Motives[5] = 0.2f;
        host.Records[5] = [Record(action: 7, baseValue: 100)];
        Assert.Equal(PrimitiveResult.False, Run(context));

        host.Records[5] = [Record(action: 7, baseValue: 300)];
        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(5, context.Frame.StackObject);
    }

    [Fact]
    public void TheRandomPickIsTheGameRandomModuloTheCandidatesInScoreOrder()
    {
        // Scores 0.5 (6), 0.4 (7) and 0.3 (5): seed 2 produces 0x838c, and 0x838c % 3 is 1.
        var (context, me, host) = Setup(Candidate(5), Candidate(6), Candidate(7));
        host.Records[5] = [Record(action: 1, baseValue: 300)];
        host.Records[6] = [Record(action: 2, baseValue: 500)];
        host.Records[7] = [Record(action: 3, baseValue: 400)];
        context.World.RandomState = 2;

        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(7, context.Frame.StackObject);
        Assert.Equal((short)3, me.InteractionActionWord72);
    }

    [Fact]
    public void TheLogicSkillReducesThePoolOfTheRandomChoice()
    {
        // The pool is 10 - 16000 / 2000 = 2, so the source draw from seed 1 selects the best (6).
        var (context, me, host) = Setup(Candidate(5), Candidate(6), Candidate(7));
        me[PersonData.LogicSkill] = 16000;
        host.Records[5] = [Record(action: 1, baseValue: 300)];
        host.Records[6] = [Record(action: 2, baseValue: 500)];
        host.Records[7] = [Record(action: 3, baseValue: 400)];

        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(6, context.Frame.StackObject);
    }

    [Fact]
    public void AnAutoFirstTopCandidateOfAFamilyIsTakenWithoutTheRandomSource()
    {
        // A nonzero state is classified through the manager table. Class 1 makes the family's top entry (AutoFirst) non-random.
        var (context, me, host) = Setup(Candidate(5), Candidate(6), Candidate(7));
        me.StateValue = 10;
        context.World.StateClasses.Add(new StateClassEntry(10, 1));
        host.Records[5] = [Record(action: 1, baseValue: 300)];
        host.Records[6] = [Record(action: 2, baseValue: 500, autoFirst: true)];
        host.Records[7] = [Record(action: 3, baseValue: 400)];

        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(6, context.Frame.StackObject);
        Assert.Equal(0, host.RandomCalls);
    }

    [Fact]
    public void AVisitorTakesTheTopEntryWhileTheStateClassIsOneAndUsesTheVisitorMinimum()
    {
        var (context, me, host) = Setup(Candidate(5));
        me[PersonData.PersonType] = 1;
        me.StateValue = 10;
        context.World.StateClasses.Add(new StateClassEntry(10, 1));
        host.Records[5] = [Record(action: 7, baseValue: 100)];

        // 0.1 is above the visitor minimum (0.05) and below the family one (0.2).
        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(5, context.Frame.StackObject);
        Assert.Equal(0, host.RandomCalls);
    }

    [Fact]
    public void WhileSittingTheChosenScoreMustReachTheSittingMinimum()
    {
        var (context, me, host) = Setup(Candidate(5));
        context.World.SimulatorTuning = new FindBestInteractionTuning { FamilyMinimum = 0.05f };
        host.Records[5] = [Record(action: 7, baseValue: 100)];

        // Not sitting: 0.1 is accepted above the family minimum. Sitting: 0.1 is below the sitting minimum (0.2).
        Assert.Equal(PrimitiveResult.True, Run(context));
        context.Frame.StackObject = 0;
        me[PersonData.Sitting] = 1;
        Assert.Equal(PrimitiveResult.False, Run(context));
        Assert.Equal(0, context.Frame.StackObject);

        host.Records[5] = [Record(action: 7, baseValue: 300)];
        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(5, context.Frame.StackObject);
    }

    [Fact]
    public void AttenuationWeightsTheScoreByTheDistanceUnlessTheCandidateIsTypeSeven()
    {
        // A candidate 16 tiles away (256 units along Y) has a distance of 16. Family low attenuation 0.002 gives a weight of
        // 1 / 1.032, so 0.3 becomes 0.2907, below the 0.295 minimum. Code 1 (none) keeps 0.3. A definition of type 7 is at distance 0.
        var far = Candidate(5);
        far.Position = new ObjectPosition(0, 256, 0);
        var (context, me, host) = Setup(far);
        context.World.SimulatorTuning = new FindBestInteractionTuning { FamilyMinimum = 0.295f };
        host.Records[5] = [Record(action: 7, baseValue: 300, code: 2)];
        Assert.Equal(PrimitiveResult.False, Run(context));

        host.Records[5] = [Record(action: 7, baseValue: 300, code: 1)];
        Assert.Equal(PrimitiveResult.True, Run(context));

        var typeSeven = Candidate(6, type: 7);
        typeSeven.Position = new ObjectPosition(0, 256, 0);
        context.World.Remove(5);
        context.World.Add(typeSeven);
        context.World.ClockManager.SetFlag(typeSeven.ClockManagerIndex, 3, enabled: true);
        host.Records[6] = [Record(action: 8, baseValue: 300, code: 2)];
        context.Frame.StackObject = 0;
        me[PersonData.Gender] = 2;
        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(6, context.Frame.StackObject);
    }

    [Fact]
    public void PersonalityKindsScaleTheChangeByTheInvertedFieldOnTheEvenKinds()
    {
        // Personality 2 is 300; kind 1 reads 300 (change 30, score 0.03) and kind 2 reads 1000 - 300 = 700 (change 70, score 0.07).
        var (context, me, host) = Setup(Candidate(5));
        context.World.SimulatorTuning = new FindBestInteractionTuning { FamilyMinimum = 0.065f };
        me[(PersonData)2] = 300;
        host.Records[5] = [Record(action: 7, baseValue: 0, kind: 1, scale: 100)];
        Assert.Equal(PrimitiveResult.False, Run(context));

        host.Records[5] = [Record(action: 7, baseValue: 0, kind: 2, scale: 100)];
        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(5, context.Frame.StackObject);
    }

    [Theory]
    [InlineData(1, 2, false)]
    [InlineData(2, 2, true)]
    [InlineData(3, 3, false)]
    [InlineData(4, 3, true)]
    [InlineData(5, 4, false)]
    [InlineData(6, 4, true)]
    [InlineData(7, 5, false)]
    [InlineData(8, 5, true)]
    [InlineData(9, 6, false)]
    [InlineData(10, 6, true)]
    [InlineData(11, 7, false)]
    [InlineData(12, 7, true)]
    [InlineData(13, 9, false)]
    [InlineData(14, 10, false)]
    [InlineData(15, 11, false)]
    [InlineData(16, 12, false)]
    [InlineData(17, 55, false)]
    [InlineData(18, 55, false)]
    [InlineData(19, 15, false)]
    [InlineData(20, 55, false)]
    [InlineData(21, 17, false)]
    [InlineData(22, 18, false)]
    public void PersonalityKindsOneThroughTwentyTwoUseTheOriginalTable(short kind, short field, bool invert)
    {
        var (context, me, host) = Setup(Candidate(5));
        context.World.SimulatorTuning = new FindBestInteractionTuning { FamilyMinimum = 0.065f };
        foreach (var personalityField in Enumerable.Range(2, 17).Append(55)) me[(PersonData)personalityField] = 1000;
        me[(PersonData)field] = 300;
        host.Records[5] = [Record(action: 7, baseValue: 0, kind: kind, scale: 100)];

        var result = Run(context);

        Assert.Equal(invert ? PrimitiveResult.True : PrimitiveResult.False, result);
        Assert.Equal(invert ? (short)5 : (short)0, context.Frame.StackObject);
    }

    [Fact]
    public void FacingModeTwoKeepsCandidatesWithAnEvenRawStackObjectKey()
    {
        // The rotation 2 plus the person's id 100 is 102, and 102 & 3 is mode 2: only even +0xf0 keys pass.
        var oddId = Candidate(5);
        oddId.RequestKey = 8;
        var evenId = Candidate(6);
        evenId.RequestKey = 9;
        var (context, me, host) = Setup(oddId, evenId);
        // State 0 is special-cased by FUN_0042ba25 as class 1, which forces facing 0.
        // Use a nonzero state with no manager entry to exercise the rotation-derived facing.
        me.StateValue = 10;
        host.Lot = host.Lot with { Rotation = 2 };
        host.Records[5] = [Record(action: 1, baseValue: 300)];
        host.Records[6] = [Record(action: 2, baseValue: 500)];

        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(5, context.Frame.StackObject);
        Assert.Equal((short)1, me.InteractionActionWord72);
    }

    [Fact]
    public void FacingModeOneRejectsCandidatesBeyondSevenUnits()
    {
        // Rotation 1 plus person id 100 selects mode 1. FUN_0045dea5 compares the original distance against
        // DAT_00666a50 (the shipped value is 7), before scoring any interaction records.
        var tooFar = Candidate(5);
        tooFar.Position = new ObjectPosition(0, 128, 0); // 8 units: 128 / 16
        var withinRange = Candidate(6);
        withinRange.Position = new ObjectPosition(0, 112, 0); // 7 units
        var (context, me, host) = Setup(tooFar, withinRange);
        me.StateValue = 10; // class 0; a nonzero state with no class row leaves the facing mode active.
        host.Lot = host.Lot with { Rotation = 1 };
        host.Records[5] = [Record(action: 1, baseValue: 500)];
        host.Records[6] = [Record(action: 2, baseValue: 300)];

        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(6, context.Frame.StackObject);
        Assert.Equal((short)2, me.InteractionActionWord72);
    }

    [Fact]
    public void FacingModeThreeKeepsOnlyOddRawStackObjectKeys()
    {
        // Rotation 3 plus person id 100 selects mode 3; the source tests bit 0 of the candidate's +0xf0 id.
        var evenId = Candidate(6);
        evenId.RequestKey = 8;
        var oddId = Candidate(7);
        oddId.RequestKey = 9;
        var (context, me, host) = Setup(evenId, oddId);
        me.StateValue = 10;
        host.Lot = host.Lot with { Rotation = 3 };
        host.Records[6] = [Record(action: 1, baseValue: 500)];
        host.Records[7] = [Record(action: 2, baseValue: 300)];

        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(7, context.Frame.StackObject);
        Assert.Equal((short)2, me.InteractionActionWord72);
    }

    [Fact]
    public void ACandidateWithPositiveLockoutCountIsNotConsidered()
    {
        var lockedOut = Candidate(5);
        lockedOut[ObjectData.LockoutCount] = 1;
        var (context, me, host) = Setup(lockedOut, Candidate(6));
        host.Records[5] = [Record(action: 1, baseValue: 500)];
        host.Records[6] = [Record(action: 2, baseValue: 300)];

        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(6, context.Frame.StackObject);
    }

    [Fact]
    public void PositiveUseCountAloneDoesNotExcludeCandidate()
    {
        var used = Candidate(5);
        used[ObjectData.UseCount] = 1;
        var (context, me, host) = Setup(used, Candidate(6));
        host.Records[5] = [Record(action: 1, baseValue: 500)];
        host.Records[6] = [Record(action: 2, baseValue: 300)];

        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(5, context.Frame.StackObject);
    }

    [Fact]
    public void AnObjectWithoutAnInteractionTableIsSkipped()
    {
        var (context, me, host) = Setup(new XObject(5, PrimitiveTestWorld.Definition()), Candidate(6));
        host.Records[5] = [Record(action: 1, baseValue: 500)];
        host.Records[6] = [Record(action: 2, baseValue: 300)];

        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(6, context.Frame.StackObject);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrEmptyNativeTtabContributesNoAutonomyRecords(bool emptyTable)
    {
        var candidate = Candidate(5);
        if (emptyTable)
            candidate.TypeRecord = new TypeRecord(55) { Interactions = EmptyNativeTable() };
        var (context, _, _) = Setup(candidate);

        Assert.Equal(PrimitiveResult.False, Run(context));
        Assert.Equal((short)0, context.Frame.StackObject);
    }

    [Fact]
    public void ACandidateWithoutTableFlagBitNineteenIsSkipped()
    {
        var (context, me, host) = Setup(Candidate(5), Candidate(6));
        context.World.ClockManager.SetFlag(context.World.ObjectById(5)!.ClockManagerIndex, 3, enabled: false);
        host.Records[5] = [Record(action: 1, baseValue: 500)];
        host.Records[6] = [Record(action: 2, baseValue: 300)];

        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(6, context.Frame.StackObject);
    }

    [Fact]
    public void ThePersonAsACandidateNeedsGenderTwoOrMore()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me[PersonData.AutonomyLevel] = 100;
        me.Definition[7] = 1;
        context.World.ClockManager.SetFlag(me.ClockManagerIndex, 3, enabled: true);
        host.Records[100] = [Record(action: 7, baseValue: 500)];

        me[PersonData.Gender] = 1;
        Assert.Equal(PrimitiveResult.False, Run(context));

        me[PersonData.Gender] = 2;
        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(100, context.Frame.StackObject);
        Assert.Equal((short)7, me.InteractionActionWord72);
    }

    [Fact]
    public void WhileAClassFortySixObjectIsOnTheLotOnlyRoomlessOrOtherClassCandidatesAreConsidered()
    {
        // Sims.exe reads categories from cXObject +0xc0 (Behavior.iff Object Data field 59), not from a host class lookup.
        // The state is in the range 0x28 to 0x31, the family number (0) differs from the lot id (6), and category 0x2e exists.
        // Candidate 5 (category 5, a room that exists and is inside) is not considered; candidate 6 (category 1) is.
        var (context, me, host) = Setup(Candidate(5), Candidate(6), Candidate(7));
        me.StateValue = 0x28;
        host.Lot = host.Lot with { LotId = 6 };
        context.World.ObjectById(5)![ObjectData.Category] = 5;
        context.World.ObjectById(6)![ObjectData.Category] = 1;
        context.World.ObjectById(7)![ObjectData.Category] = 0x2e;
        context.World.ObjectById(5)!.RoomWord84 = 4;
        context.World.ObjectById(6)!.RoomWord84 = 4;
        context.World.PersonRoomMotiveEntries[4] = new PersonRoomMotiveEntry(0f, IsOutside: false);
        context.World.RoomManagerRecordsLoaded = true;
        host.Records[5] = [Record(action: 1, baseValue: 500)];
        host.Records[6] = [Record(action: 2, baseValue: 300)];
        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(6, context.Frame.StackObject);

        // Without the class-46 object, candidate 5 is considered again and has the better score.
        var (without, withoutMe, withoutHost) = Setup(Candidate(5), Candidate(6));
        withoutMe.StateValue = 0x28;
        withoutHost.Lot = withoutHost.Lot with { LotId = 6 };
        without.World.ObjectById(5)![ObjectData.Category] = 5;
        without.World.ObjectById(6)![ObjectData.Category] = 1;
        without.World.ObjectById(5)!.RoomWord84 = 4;
        without.World.ObjectById(6)!.RoomWord84 = 4;
        without.World.PersonRoomMotiveEntries[4] = new PersonRoomMotiveEntry(0f, IsOutside: false);
        without.World.RoomManagerRecordsLoaded = true;
        withoutHost.Records[5] = [Record(action: 1, baseValue: 500)];
        withoutHost.Records[6] = [Record(action: 2, baseValue: 300)];
        Assert.Equal(PrimitiveResult.True, Run(without));
        Assert.Equal(5, without.Frame.StackObject);
    }

    [Fact]
    public void StateClassTwoUsesTheNativeAlternativeTypeExclusion()
    {
        var (context, me, host) = Setup(Candidate(5), Candidate(6));
        me.StateValue = 10;
        context.World.StateClasses.Add(new StateClassEntry(10, 2));
        context.World.ObjectById(5)!.TypeRecord = new TypeRecord(5) { AutonomyAlternativeTypeFlag = 4 };
        host.Records[5] = [Record(action: 1, baseValue: 500)];
        host.Records[6] = [Record(action: 2, baseValue: 300)];

        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(6, context.Frame.StackObject);
        Assert.Equal((short)2, me.InteractionActionWord72);
    }

    [Fact]
    public void AnUnforcedFamilyMemberWithoutAPetFailsTheGateWithoutAnyRead()
    {
        var (context, me, host) = Setup(Candidate(5));
        host.Lot = host.Lot with { Forced = false };
        host.Records[5] = [Record(action: 1, baseValue: 500)];

        Assert.Equal(PrimitiveResult.False, Run(context));
        Assert.Equal(0, host.RandomCalls);

        me[PersonData.Gender] = 2;
        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal(5, context.Frame.StackObject);
    }

    [Fact]
    public void NativeUnforcedFamilyHumanFailsTheSourceGateWithoutHostAutonomyData()
    {
        var (context, me, _) = Setup();
        context.World.Host = null;
        context.World.AutonomyForced = false;
        Assert.Equal((short)0, context.World.GlobalObjectTableEffectWords[0x1e]);
        Assert.Equal((short)0, context.World[SimulationGlobal.FreeWill]);
        me[PersonData.Gender] = 1;
        me.SelectionWord5CC = 0;

        Assert.Equal(PrimitiveResult.False, Run(context));
        Assert.Equal((short)0, me.InteractionActionWord72);
    }

    [Fact]
    public void NativeForcedAutonomyStillReportsTheMissingServiceInsteadOfReturningFalse()
    {
        var (context, _, _) = Setup();
        context.World.Host = null;
        context.World.AutonomyForced = true;
        Assert.Equal((short)1, context.World.GlobalObjectTableEffectWords[0x1e]);
        Assert.Equal((short)1, context.World[SimulationGlobal.FreeWill]);

        Assert.Throws<InvalidOperationException>(() => Run(context));
    }

    [Fact]
    public void AnEmptyObjectTableAnswersFalseWithoutAChoice()
    {
        var (context, me, host) = Setup();
        Assert.Equal(PrimitiveResult.False, Run(context));
        Assert.Equal(0, host.RandomCalls);
        Assert.Equal((short)0, me.InteractionActionWord72);
    }

    [Fact]
    public void AutonomyCandidateEvaluationSetsActionQueueSlotSecondaryToCandidate()
    {
        var candidate = Candidate(5);
        var (context, _, host) = Setup(candidate);
        host.Records[5] = [Record(action: 7, baseValue: 500) with { Passed = false }];
        var menu = new InteractionMenuContext(context.World);
        context.World.InteractionMenu = menu;

        Assert.Equal(PrimitiveResult.False, Run(context));

        Assert.Same(candidate, menu.Current?.Owner);
        Assert.Same(candidate, menu.Current?.Secondary);
    }

    [Fact]
    public void UngreetedVisitorPetCannotAutonomouslySelectIndoorObjectOnResidentialLot()
    {
        var candidate = Candidate(5);
        candidate[ObjectData.Room] = 3;
        var (context, me, host) = Setup(candidate);
        me.SelectionWord5CC = 1;
        me[PersonData.Gender] = 2;
        me[PersonData.GreetStatus] = 0;
        me.StateValue = 10;
        context.World.StateClasses.Add(new StateClassEntry(10, 1));
        host.Records[5] = [Record(action: 7, baseValue: 500)];

        Assert.Equal(PrimitiveResult.False, Run(context));

        me[PersonData.GreetStatus] = 1;
        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal((short)5, context.Frame.StackObject);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    public void InTransitCategoryFourAndSevenRequireVisitorInventoryTokenAndBypassOtherGates(short category)
    {
        var candidate = Candidate(5);
        candidate[ObjectData.Category] = category;
        var (context, me, _) = Setup(candidate);
        me.StateValue = 0x28;
        me.SelectionWord5CC = 1;
        me[PersonData.AutonomyLevel] = 1;
        // AutonomyThreshold = 50 (> AutonomyLevel 1), debug flag 0x80 set, visitor flags 0x01/0x20 clear:
        var builder = new ChunkBuilder().S16(1).S16(6).U16(0x4567).U16(0).S32(16)
            .U32(0x80u).S32(9).S32(1).F32(0).S32(50).S32(-1);
        foreach (var motive in Advertisement(500))
            builder.S16(motive.Base).S16(motive.Scale).S16(motive.Kind);
        var (file, chunk) = builder.InFile("TTAB");
        candidate.TypeRecord = new TypeRecord(55)
        {
            Interactions = InteractionTable.Read(file, chunk),
            BehaviorScope = new TreeTable(),
        };

        // Without visitor inventory token, rejected even in transit range:
        Assert.Equal(PrimitiveResult.False, Run(context));

        // With visitor inventory token, allowed in transit range despite AutonomyLevel < 50, 0x80 set, and 0x01/0x20 clear:
        const int key = 0x1234;
        var scope = new InteractionScopeTable([new InteractionScopeEntry(0, key, 1)]);
        scope.AvatarObjects.Add(new TypeRecord(99) { ObjectType = 13, AnimationKey = key });
        context.World.InteractionScopes[me.InteractionScopeKey5CA] = scope;

        Assert.Equal(PrimitiveResult.True, Run(context));
        Assert.Equal((short)5, context.Frame.StackObject);
        Assert.Equal((short)9, me.InteractionActionWord72);
    }

    [Fact]
    public void JoinableTargetInteractionWithMissingJoiningIndexIsRejectedInsteadOfFallingBack()
    {
        var joinOwner = Candidate(5);
        var targetPerson = new XPerson(6, PrimitiveTestWorld.Definition(XPerson.PersonType));
        targetPerson.Definition[7] = 1;
        var (context, me, _) = Setup(joinOwner, targetPerson);

        var builder = new ChunkBuilder().S16(1).S16(6).U16(0x4567).U16(0).S32(16)
            .U32(0x2u | 0x1u).S32(9).S32(1).F32(0).S32(0).S32(99);
        foreach (var motive in Advertisement(500))
            builder.S16(motive.Base).S16(motive.Scale).S16(motive.Kind);
        var (file, chunk) = builder.InFile("TTAB");
        joinOwner.TypeRecord = new TypeRecord(55)
        {
            Interactions = InteractionTable.Read(file, chunk),
            BehaviorScope = new TreeTable(),
        };
        targetPerson.CurrentActionRecord = new ActionQueueSlot
        {
            Person = targetPerson,
            Owner = joinOwner,
            Secondary = joinOwner,
            RawDword10 = 9,
        };

        Assert.Equal(PrimitiveResult.False, Run(context));
        Assert.Equal((short)0, context.Frame.StackObject);
    }
}
