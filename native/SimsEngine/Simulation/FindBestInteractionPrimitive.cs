using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>Represents a motive advertisement entry (kind, base, and scale). Kind 0 is a fixed delta of
/// base plus scale, while kinds 1 through 4 scale the delta by personality traits.</summary>
public readonly record struct MotiveAdvertisement(short Kind, short Base, short Scale);

/// <summary>Catalog of motives scored during autonomy evaluation. Curves default to identity mappings
/// across the -100 to 100 range and are overridden by Global STR# 501 (adults) or STR# 503 (children).
/// </summary>
public static class AutonomyMotiveCatalog
{
    private static readonly short[] MotiveIds = [5, 6, 7, 8, 9, 3, 13, 14, 15];

    public static IReadOnlyList<short> All => MotiveIds;

    public static float EvaluateCurve(short motiveId, float value) => EvaluateCurve(null, null, motiveId, value);

    public static float EvaluateCurve(PersonMotiveDecayTuning? tuning, XPerson? person, short motiveId, float value)
    {
        var index = motiveId switch
        {
            5 => 0,
            6 => 1,
            7 => 2,
            8 => 3,
            9 => 4,
            3 => 5,
            13 => 6,
            14 => 7,
            15 => 8,
            _ => throw new ArgumentOutOfRangeException(nameof(motiveId), motiveId,
                "The Sims 1 autonomy score averages the nine source-selected motive IDs in DAT_006c23f4."),
        };
        if (tuning is not null && person is not null && tuning.ScoreCurve(person, index) is { } curve)
            return curve.Evaluate(value);
        return Math.Clamp(value, -100f, 100f);
    }
}

/// <summary>Represents an autonomy candidate interaction record. Contains test-pass status, AutoFirst flag,
/// distance attenuation settings, the motive advertisement array, and the interaction's TTAB index matched
/// by Gosub Found Action.</summary>
public sealed record InteractionRecord(bool AutoFirst, bool Passed, short AttenuationCode, float CustomAttenuation, MotiveAdvertisement[] Advertisement, short Action)
{
    /// <summary>Maps an interaction table row to an autonomy candidate record storing the interaction table
    /// index.</summary>
    public static InteractionRecord FromTtab(Interaction interaction, bool passed, MotiveAdvertisement[] advertisement)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(advertisement);
        return new InteractionRecord((interaction.Flags & 0x100) != 0, passed,
            unchecked((short)interaction.AttenuationCode), interaction.AttenuationValue, advertisement,
            unchecked((short)interaction.TtaIndex));
    }

    /// <summary>Builds the scoring advertisement from an interaction table row, reading each motive as
    /// minimum (base), delta (scale), and personality modifier (kind).</summary>
    public static InteractionRecord FromTtab(Interaction interaction, bool passed)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        var advertisement = new MotiveAdvertisement[16];
        for (var index = 0; index < Math.Min(interaction.Motives.Count, advertisement.Length); index++)
        {
            var motive = interaction.Motives[index];
            advertisement[index] = new MotiveAdvertisement(motive.Personality, motive.Minimum, motive.Delta);
        }
        return FromTtab(interaction, passed, advertisement);
    }
}

/// <summary>Represents lot-level state consumed during autonomy evaluation, including pause status, forced
/// autonomy, lot rotation, household identification, and active selected Sim.</summary>
public readonly record struct AutonomyLot(bool Forced, bool Paused, bool FlagB, int LotKey, short LotId, int Rotation, XObject? ActivePerson);

/// <summary>Holds autonomy scoring thresholds and distance attenuation factors loaded from the FCNS 2
/// resource in Global.far.</summary>
public sealed record FindBestInteractionTuning
{
    public float FamilyMinimum { get; init; } = 0.2f;
    public float VisitorMinimum { get; init; } = 0.05f;
    public float SittingMinimum { get; init; } = 0.2f;
    public int RandomSelectionCount { get; init; } = 10;
    public int FriendshipThreshold { get; init; } = 25;
    public float FamilyLowAttenuation { get; init; } = 0.002f;
    public float FamilyModerateAttenuation { get; init; } = 0.02f;
    public float FamilyHighAttenuation { get; init; } = 0.1f;
    public float VisitorLowAttenuation { get; init; } = 0.002f;
    public float VisitorModerateAttenuation { get; init; } = 0.02f;
    public float VisitorHighAttenuation { get; init; } = 0.1f;
    public float FallbackAttenuation { get; init; } = 0.02f;
    public float FacingDistanceLimit { get; init; } = 7f;

    public static FindBestInteractionTuning FromSimulatorConstants(SimulatorConstants constants)
    {
        ArgumentNullException.ThrowIfNull(constants);
        return new FindBestInteractionTuning
        {
            FamilyMinimum = constants.Get("min autonomy score for family", 0.2f),
            VisitorMinimum = constants.Get("min autonomy score for visitors", 0.05f),
            SittingMinimum = constants.Get("min autonomy score for sitting", 0.2f),
            RandomSelectionCount = unchecked((int)constants.Get("random selection count", 10f)),
            FriendshipThreshold = unchecked((int)constants.Get("friendship threshold", 25f)),
            FamilyLowAttenuation = constants.Get("low attenuation", 0.002f),
            FamilyModerateAttenuation = constants.Get("moderate attenuation", 0.02f),
            FamilyHighAttenuation = constants.Get("high attenuation", 0.1f),
            VisitorLowAttenuation = constants.Get("visitor low attenuation", 0.002f),
            VisitorModerateAttenuation = constants.Get("visitor moderate attenuation", 0.02f),
            VisitorHighAttenuation = constants.Get("visitor high attenuation", 0.1f),
        };
    }
}

/// <summary>Implements the Find Best Interaction primitive (opcode 0x03), which evaluates autonomous
/// candidate interactions across lot objects. Filters candidates by eligibility, room, and facing, scores
/// valid interactions using distance-attenuated motive advertisements against current motive baselines, and
/// selects a winning interaction weighted by the Sim's logic skill.</summary>
public static class FindBestInteractionPrimitive
{
    private const float AdvertisementScale = 0.001f;
    private const float PersonalityScale = 0.001f;
    private const float One = 1f;
    // Inverted personality traits are calculated as 1000 minus the trait value.
    private const int InvertedPersonality = 1000;
    // Object definition field 7 indicates whether an object has interactions.
    private const int InteractionsField = 7;
    // Bit 19 in an object's flag word marks it as eligible for autonomous interaction.
    private const int EligibleBit = 19;
    private const short ClassWithUsesRoom = 0x2e;
    private const ObjectData RoomKeyField = ObjectData.Room;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var person = Person(context);
        var world = context.World;
        var captureDiagnostics = world.CaptureExecutionTrace;
        var candidatesVisited = 0;
        var interactionRecords = 0;
        var passedRecords = 0;
        var zeroAdvertisementRecords = 0;
        var belowThresholdRecords = 0;
        var scoredRecords = 0;
        float? maximumScore = null;
        float? maximumScoreBaseline = null;
        float? maximumScoreAdvertisement = null;
        short? maximumScoreObjectId = null;
        short? maximumScoreAction = null;
        MotiveAdvertisement[]? maximumScoreMotives = null;
        float? maximumAbsoluteMotiveDelta = null;
        short? maximumDeltaObjectId = null;
        short? maximumDeltaAction = null;
        MotiveAdvertisement[]? maximumDeltaMotives = null;
        int? maximumRawAdvertisement = null;
        short? maximumRawAdvertisementObjectId = null;
        short? maximumRawAdvertisementAction = null;
        bool forced = context.World.AutonomyForced ?? false;
        bool gatePassed = false;
        PrimitiveResult Finish(PrimitiveResult result, short? chosenObject = null, short? action = null)
        {
            if (captureDiagnostics)
                world.AutonomyAttemptTrace.Add(new AutonomyAttemptDiagnostic(person.Id, forced, gatePassed,
                    candidatesVisited, interactionRecords, passedRecords, zeroAdvertisementRecords, belowThresholdRecords,
                    scoredRecords, maximumScore, maximumScoreBaseline, maximumScoreAdvertisement, maximumScoreObjectId,
                    maximumScoreAction, maximumScoreMotives, maximumAbsoluteMotiveDelta, maximumDeltaObjectId,
                    maximumDeltaAction, maximumDeltaMotives, maximumRawAdvertisement, maximumRawAdvertisementObjectId,
                    maximumRawAdvertisementAction, chosenObject, action));
            return result;
        }
        // When free will is disabled, unselected human family members bypass autonomy evaluation; only pets
        // pass the preliminary gate.
        if (context.World.AutonomyForced == false && person.SelectionWord5CC == 0 &&
            (person[PersonData.Gender] & 6) == 0)
            return Finish(PrimitiveResult.False);
        var host = context.World.RequireAutonomyHost();
        var tuning = context.World.SimulatorTuning;
        var lot = host.AutonomyLotState();
        if (context.World.AutonomyForced is { } autonomyForced) lot = lot with { Forced = autonomyForced };

        forced = lot.Forced;
        if (!lot.Forced && !PassesGate(person, lot)) return Finish(PrimitiveResult.False);
        gatePassed = true;
        if (lot.Paused || (lot.LotKey == 0 && ReferenceEquals(lot.ActivePerson, person))) return Finish(PrimitiveResult.False);

        var visitor = person.SelectionWord5CC != 0;
        var state = person.StateValue;
        var motives = AutonomyMotiveCatalog.All;
        var baseline = Baseline(world, person, motives);
        var threshold = visitor ? tuning.VisitorMinimum : tuning.FamilyMinimum;
        var count = context.World.ObjectTableEntries.Count;
        var inTransit = context.World.StateClass(state) == 2 || InTransitRange(state);
        var classFlag = count > 0 && InTransitRange(state) && person[PersonData.FamilyNumber] != lot.LotId && HasClassWithUses(person, count);
        var facing = (lot.Rotation + (ushort)person.Id) & 3;
        if (context.World.StateClass(state) != 0) facing = 0;

        var candidateRecords = new List<(XObject Candidate, float Distance, List<InteractionRecord> Records, List<short> TargetIds)>();
        var pendingGuards = new List<(ActionQueueSlot Action, List<InteractionRecord> Records, int Index)>();
        var pendingActions = new List<ActionQueueSlot>();
        var evaluatedCandidates = new HashSet<short>();
        for (var position = 1; position <= count; position++)
        {
            if (captureDiagnostics) candidatesVisited++;
            var rawCandidate = person.ObjectTableEntry((short)position);
            if (rawCandidate is null || rawCandidate.Definition[InteractionsField] == 0) continue;
            if (((context.World.ClockManager.FlagsAt(position) >> EligibleBit) & 1) == 0) continue;
            if (inTransit && rawCandidate.TypeRecord?.AutonomyAlternativeTypeFlag is 4 or 5) continue;
            if (ReferenceEquals(rawCandidate, person) && person[PersonData.Gender] < 2) continue;
            if (!ConsideredForScore(context.World, rawCandidate, classFlag)) continue;

            var distance = (float)GetDistanceToPrimitive.DistanceBetween(person, rawCandidate);
            if (!FacingAllows(facing, rawCandidate, distance, tuning)) continue;

            var candidate = ResolveInteractionLead(context.World, rawCandidate);
            if (!evaluatedCandidates.Add(candidate.Id)) continue;
            distance = (float)GetDistanceToPrimitive.DistanceBetween(person, candidate);

            // Object type 7 candidate objects are only considered for autonomous interactions by pets.
            var candidateType = candidate.TypeRecord is { ObjectType: > 0 } typeRec
                ? typeRec.ObjectType
                : candidate.Definition[(int)ObjdField.Type];
            if (candidateType == 7 && (person[PersonData.Gender] & 6) == 0) continue;

            List<InteractionRecord>? records = null;
            List<short>? targetIds = null;
            if (candidate.TypeRecord?.Interactions is { } interactionTable)
            {
                var personAge = person[PersonData.PersonAge];
                var personGender = person[PersonData.Gender];
                var isChild = personAge > 0 && personAge <= 17;
                var isDog = (personGender & 2) != 0;
                var isCat = (personGender & 4) != 0;
                var isVisitor = person.SelectionWord5CC != 0;
                var isGreeted = person[PersonData.GreetStatus] != 0;
                var stateClass = context.World.StateClass(state);
                var inTransitState = InTransitRange(state);

                foreach (var interaction in interactionTable.Interactions)
                {
                    var autonomyThreshold = unchecked((short)interaction.AutonomyThreshold);
                    if (autonomyThreshold <= 0) continue;

                    // While in transit states, category 4 and 7 objects require a positive
                    // visitor-inventory token in the Sim's scope and only check pet flags.
                    if (inTransitState && candidate[ObjectData.Category] is 4 or 7)
                    {
                        if (!context.World.HasVisitorInventoryEntry(person)) continue;
                        if (isDog && (interaction.Flags & 0x400) == 0) continue;
                        if (isCat && (interaction.Flags & 0x200) == 0) continue;
                    }
                    else
                    {
                        if (isDog && (interaction.Flags & 0x400) == 0) continue;
                        if (isCat && (interaction.Flags & 0x200) == 0) continue;
                        if (isChild)
                        {
                            if ((interaction.Flags & 0x10) != 0) continue;
                        }
                        else if (!isDog && !isCat && (interaction.Flags & 0x40) != 0)
                        {
                            continue;
                        }
                        if (person[PersonData.AutonomyLevel] < autonomyThreshold) continue;
                        if ((interaction.Flags & 0x80) != 0) continue;
                        if ((stateClass == 1 || (interaction.Flags & 0x20) == 0) &&
                            isVisitor && (interaction.Flags & 1) == 0)
                            continue;
                        // Ungreeted visitor pets cannot autonomously target indoor objects on residential
                        // lots.
                        if (stateClass == 1 && isVisitor && (isDog || isCat) && !isGreeted &&
                            candidate[RoomKeyField] != 0)
                            continue;
                    }

                    records ??= [];
                    targetIds ??= [];
                    var action = new ActionQueueSlot
                    {
                        Person = person,
                        Owner = candidate,
                        Secondary = candidate,
                        RawDword10 = interaction.TtaIndex,
                    };
                    var recordIndex = records.Count;
                    var record = InteractionRecord.FromTtab(interaction, passed: false);
                    if (candidate[RoomKeyField] != person[RoomKeyField])
                        record.Advertisement[13] = record.Advertisement[13] with { Scale = unchecked((short)-record.Advertisement[13].Base) };
                    records.Add(record);
                    targetIds.Add(candidate.Id);
                    pendingGuards.Add((action, records, recordIndex));
                    pendingActions.Add(action);
                }
            }

            // When scanning another Sim whose active interaction is joinable, evaluates joining that
            // interaction on its target object.
            if (candidate is XPerson targetPerson && !ReferenceEquals(targetPerson, person) &&
                targetPerson.CurrentActionRecord is { Owner: { } activeOwner } currentAction)
            {
                var joinOwner = ResolveInteractionLead(context.World, activeOwner);
                var joinInteractions = context.World.EffectiveInteractions(joinOwner)?.Interactions;
                var activeInteraction = joinInteractions?.FirstOrDefault(entry => entry.TtaIndex == currentAction.RawDword10);
                if (activeInteraction is not null && (activeInteraction.Flags & 2) != 0)
                {
                    var joinInteraction = activeInteraction.JoiningIndex != -1
                        ? joinInteractions!.FirstOrDefault(entry => entry.TtaIndex == activeInteraction.JoiningIndex)
                        : null;
                    if (joinInteraction is not null)
                    {
                        records ??= [];
                        targetIds ??= [];
                        var joinAction = new ActionQueueSlot
                        {
                            RecordKind = 1,
                            Person = person,
                            Owner = joinOwner,
                            Secondary = currentAction.Secondary ?? joinOwner,
                            RawDword10 = joinInteraction.TtaIndex,
                            Threshold = 50,
                            ActionTree = joinInteraction.ActionTree,
                            Attenuation = context.World.InteractionAttenuation(person, joinInteraction),
                            Flags = (joinInteraction.Flags & 0x100) != 0 ? 1 : 0,
                        };
                        var recordIndex = records.Count;
                        var record = InteractionRecord.FromTtab(joinInteraction, passed: false);
                        if (joinOwner[RoomKeyField] != person[RoomKeyField])
                            record.Advertisement[13] = record.Advertisement[13] with { Scale = unchecked((short)-record.Advertisement[13].Base) };
                        records.Add(record);
                        targetIds.Add(joinOwner.Id);
                        pendingGuards.Add((joinAction, records, recordIndex));
                        pendingActions.Add(joinAction);
                    }
                }
            }

            if (records is not null)
                candidateRecords.Add((candidate, distance, records, targetIds!));
        }

        if (pendingGuards.Count != 0)
        {
            InteractionTestEvaluator.Evaluate(world, person, pendingActions, autonomy: true);
            foreach (var (action, records, index) in pendingGuards)
                records[index] = records[index] with
                {
                    Passed = (action.Flags & InteractionTestEvaluator.Passed) != 0,
                    Advertisement = action.EvaluatedAdvertisements ?? records[index].Advertisement,
                };
        }

        var scored = new List<ScoredInteraction>();
        foreach (var (candidate, distance, records, targetIds) in candidateRecords)
        {
            for (var recordIndex = 0; recordIndex < records.Count; recordIndex++)
            {
                var record = records[recordIndex];
                var targetObjectId = targetIds[recordIndex];
                if (captureDiagnostics) interactionRecords++;
                if (!record.Passed) continue;
                if (captureDiagnostics) passedRecords++;
                var weight = One / (Attenuation(record, tuning, visitor) * distance + One);
                if (captureDiagnostics)
                {
                    var rawAdvertisement = 0;
                    foreach (var motive in motives)
                    {
                        var entry = record.Advertisement[motive];
                        rawAdvertisement = Math.Max(rawAdvertisement,
                            Math.Max(Math.Abs((int)entry.Base), Math.Abs((int)entry.Scale)));
                    }
                    if (maximumRawAdvertisement is null || rawAdvertisement > maximumRawAdvertisement.Value)
                    {
                        maximumRawAdvertisement = rawAdvertisement;
                        maximumRawAdvertisementObjectId = targetObjectId;
                        maximumRawAdvertisementAction = record.Action;
                    }
                }
                var advertised = AdvertisementMean(world, person, motives, record.Advertisement);
                if (advertised == 0)
                {
                    if (captureDiagnostics) zeroAdvertisementRecords++;
                    continue;
                }
                var score = (advertised - baseline) * weight;
                if (world.AutonomyHost is { } autonomyHost)
                {
                    var targetObject = world.ObjectById(targetObjectId) ?? candidate;
                    var interactionName = world.EffectiveInteractions(targetObject)?.Interactions
                        .FirstOrDefault(entry => entry.TtaIndex == record.Action)?.Name ?? "";
                    score = autonomyHost.ScoreAutonomy(person, targetObject, record.Action, interactionName, score);
                    if (score <= 0f) continue;
                }
                if (captureDiagnostics)
                {
                    var absoluteMotiveDelta = Math.Abs(advertised - baseline);
                    if (maximumAbsoluteMotiveDelta is null || absoluteMotiveDelta > maximumAbsoluteMotiveDelta.Value)
                    {
                        maximumAbsoluteMotiveDelta = absoluteMotiveDelta;
                        maximumDeltaObjectId = targetObjectId;
                        maximumDeltaAction = record.Action;
                        maximumDeltaMotives = record.Advertisement;
                    }
                    if (maximumScore is null || score > maximumScore.Value)
                    {
                        maximumScore = score;
                        maximumScoreBaseline = baseline;
                        maximumScoreAdvertisement = advertised;
                        maximumScoreObjectId = targetObjectId;
                        maximumScoreAction = record.Action;
                        maximumScoreMotives = record.Advertisement;
                    }
                }
                if (threshold <= score)
                {
                    if (captureDiagnostics) scoredRecords++;
                    scored.Add(new ScoredInteraction(targetObjectId, record.Action, score, record.AutoFirst, scored.Count));
                }
                else if (captureDiagnostics) belowThresholdRecords++;
            }
        }

        if (scored.Count == 0) return Finish(PrimitiveResult.False);
        scored.Sort(static (a, b) =>
        {
            var cmp = b.Score.CompareTo(a.Score);
            return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
        });
        var top = scored[0];

        var pool = Math.Max(1, tuning.RandomSelectionCount - person[PersonData.LogicSkill] / 2000);
        // Unselected Sims choose randomly among top-scoring candidates unless the highest-ranked
        // interaction specifies Auto First.
        var random = (person.SelectionWord5CC == 0 && !top.AutoFirst) || context.World.StateClass(state) != 1;
        var chosenIndex = random ? (int)(context.World.NextRandom() % (uint)Math.Min(scored.Count, pool)) : 0;
        if (chosenIndex >= scored.Count) return Finish(PrimitiveResult.False);
        var chosen = scored[chosenIndex];
        if (person[PersonData.Sitting] != 0 && chosen.Score < tuning.SittingMinimum) return Finish(PrimitiveResult.False);

        person.InteractionActionWord72 = chosen.Action;
        context.Frame.StackObject = chosen.Candidate;
        return Finish(PrimitiveResult.True, chosen.Candidate, chosen.Action);
    }

    /// <summary>Resolves a multi-tile object part to its interaction group lead object when MasterId is
    /// nonzero and InteractionGroup is positive.</summary>
    internal static XObject ResolveInteractionLead(SimulationWorld world, XObject candidate) =>
        InteractionMenus.Target(world, candidate);

    // Determines autonomy eligibility: family members require an accompanying pet with secondary flags
    // clear, while visitors pass if their family number differs from the lot or Gender indicates a pet.
    private static bool PassesGate(XPerson person, AutonomyLot lot) =>
        person.SelectionWord5CC == 0
            ? (person[PersonData.Gender] & 6) != 0 && !lot.FlagB
            : lot.LotKey != person[PersonData.FamilyNumber] || person[PersonData.Gender] > 1;

    // Candidates with no active users are eligible for scoring; when the class flag is set, objects must be
    // outdoors, without a room, or outside categories 5 through 7.
    private static bool ConsideredForScore(SimulationWorld world, XObject candidate, bool classFlag)
    {
        if (candidate[ObjectData.LockoutCount] >= 1) return false;
        if (!classFlag) return true;
        var key = candidate[RoomKeyField];
        if (key == -5 || !world.HasRoomRecord(key) || world.RoomIsOutside(key)) return true;
        return candidate[ObjectData.Category] is not (5 or 6 or 7);
    }

    private static bool HasClassWithUses(XPerson person, int count)
    {
        for (var position = 1; position <= count; position++)
        {
            var item = person.ObjectTableEntry((short)position);
            if (item is not null && item[ObjectData.Category] == ClassWithUsesRoom) return true;
        }
        return false;
    }

    // Facing mode 0 accepts all candidates, mode 1 enforces a distance threshold, and modes 2 and 3 filter
    // for even or odd candidate object IDs.
    private static bool FacingAllows(int facing, XObject candidate, float distance, FindBestInteractionTuning tuning) => facing switch
    {
        0 => true,
        1 => distance <= tuning.FacingDistanceLimit,
        2 => (candidate.RequestKey & 1) == 0,
        _ => (candidate.RequestKey & 1) != 0,
    };

    // Determines motive advertisement attenuation: code 0 uses the interaction's custom value, 1 disables
    // falloff, and 2 through 4 represent low, moderate, and high attenuation.
    private static float Attenuation(InteractionRecord record, FindBestInteractionTuning tuning, bool visitor) => record.AttenuationCode switch
    {
        0 => record.CustomAttenuation,
        1 => 0f,
        2 => visitor ? tuning.VisitorLowAttenuation : tuning.FamilyLowAttenuation,
        3 => visitor ? tuning.VisitorModerateAttenuation : tuning.FamilyModerateAttenuation,
        4 => visitor ? tuning.VisitorHighAttenuation : tuning.FamilyHighAttenuation,
        _ => tuning.FallbackAttenuation,
    };

    private static float Baseline(SimulationWorld world, XPerson person, IReadOnlyList<short> motives)
    {
        var sum = 0f;
        foreach (var motive in motives) sum += AutonomyMotiveCatalog.EvaluateCurve(world.PersonMotiveDecayTuning, person, motive, person.Motives[motive]);
        return motives.Count == 0 ? 0 : sum / motives.Count;
    }

    // Computes the mean motive curve after applying advertised changes. Kind 0 adds base and scale
    // directly, while kinds 1 through 4 scale by personality (inverted for even kinds), with delta values
    // scaled by 0.001 before being added to motives.
    private static float AdvertisementMean(SimulationWorld world, XPerson person, IReadOnlyList<short> motives, MotiveAdvertisement[] advertisement)
    {
        var sum = 0f;
        foreach (var motive in motives)
        {
            var entry = advertisement[motive];
            float change;
            if (entry.Kind == 0)
            {
                change = entry.Scale + entry.Base;
            }
            else
            {
                var (field, invert) = Personality(entry.Kind);
                var value = (int)person.PersonData[field];
                if (invert) value = InvertedPersonality - value;
                change = entry.Scale * value * PersonalityScale + entry.Base;
            }
            sum += AutonomyMotiveCatalog.EvaluateCurve(world.PersonMotiveDecayTuning, person, motive, change * AdvertisementScale + person.Motives[motive]);
        }
        return motives.Count == 0 ? 0 : sum / motives.Count;
    }

    private static (int Field, bool Invert) Personality(short kind) => kind switch
    {
        1 => (2, false),
        2 => (2, true),
        3 => (3, false),
        4 => (3, true),
        5 => (4, false),
        6 => (4, true),
        7 => (5, false),
        8 => (5, true),
        9 => (6, false),
        10 => (6, true),
        11 => (7, false),
        12 => (7, true),
        13 => (9, false),
        14 => (10, false),
        15 => (11, false),
        16 => (12, false),
        17 => (55, false),
        18 => (55, false),
        19 => (15, false),
        20 => (55, false),
        21 => (17, false),
        22 => (18, false),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "FUN_004214d4 only has personality table rows for kinds 1 through 22."),
    };

    private static bool InTransitRange(short state) => state > 0x27 && state < 0x32;

    private static XPerson Person(in TreeContext context) =>
        context.Me as XPerson ?? throw new InvalidOperationException($"Object {context.Me.Id} is not a person; FUN_0045cd1f runs for persons only.");

    private readonly record struct ScoredInteraction(short Candidate, short Action, float Score, bool AutoFirst, int Order);
}
