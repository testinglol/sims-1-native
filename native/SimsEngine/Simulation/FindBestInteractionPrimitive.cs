using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>One motive's entry in an advertisement: six bytes at the motive's index in Sims.exe's table, read by
/// FUN_004214d4 as a kind, a base and a scale. Kind 0 is a fixed change of base + scale; kinds 1 to 4 scale the scale by a
/// personality field (see <see cref="FindBestInteractionPrimitive"/>).</summary>
public readonly record struct MotiveAdvertisement(short Kind, short Base, short Scale);

/// <summary>The nine motive IDs Sims.exe initializes from DAT_006c23f4 for the per-person score table in FUN_004213c0.
/// The order is source data order, not numeric order. FUN_004213c0 starts each row as an identity curve from the
/// executable endpoints -100 and +100; the Mac MotiveCurveSet::LoadFromFile then replaces the rows from Global STR# 0x1f5
/// (adult) or 0x1f7 (child), which <see cref="PersonMotiveDecayTuning.ScoreCurve"/> serves.
/// The unused Sleep State motive is not part of FUN_004215b6's nine-row score average.</summary>
public static class AutonomyMotiveCatalog
{
    private static readonly short[] MotiveIds = [5, 6, 7, 8, 9, 3, 13, 14, 15];

    public static IReadOnlyList<short> All => MotiveIds;

    /// <summary>FUN_004213c0 builds each row's two-point curve from (-100,-100) and (100,100), using
    /// DAT_00666a60=-100, DAT_0066674c=100 and DAT_00666748=1. FUN_004167d4 therefore returns the input inside
    /// that interval and the nearest endpoint outside it.</summary>
    public static float EvaluateCurve(short motiveId, float value) => EvaluateCurve(null, null, motiveId, value);

    /// <summary>FUN_004167d4 over the person's MotiveEffects row for the motive: the STR# 0x1f5/0x1f7 curve once the
    /// tuning is loaded, the constructor's identity curve before that.</summary>
    public static float EvaluateCurve(PersonMotiveDecayTuning? tuning, XPerson? person, short motiveId, float value)
    {
        if (!MotiveIds.Contains(motiveId))
            throw new ArgumentOutOfRangeException(nameof(motiveId), motiveId,
            "The Sims 1 autonomy score averages the nine source-selected motive IDs in DAT_006c23f4.");
        if (tuning is not null && person is not null && tuning.ScoreCurve(person, Array.IndexOf(MotiveIds, motiveId)) is { } curve)
            return curve.Evaluate(value);
        return Math.Clamp(value, -100f, 100f);
    }
}

/// <summary>One interaction record of a candidate: a 0x44-byte entry of Sims.exe's candidate list (DAT_006e50e8). The flags
/// are the dword at the record's +0x40: <see cref="AutoFirst"/> is bit 0 (descriptor flag 0x100, set by FUN_0041f010) and
/// <see cref="Passed"/> is bit 3, which FUN_00458485 sets when the record's tests pass. Only a passed record is scored, and it
/// then has an advertisement. The attenuation is the descriptor's code and custom value (FUN_0041f010 and FUN_0047603d), and
/// <see cref="Action"/> is the low word of the TTAB row's +0x18 field (TtaIndex), which Sims.exe writes to the person's
/// +0x72 word and matches against the selected target's TTAB in Gosub Found Action. It is not the TTAB ActionTree or AutonomyThreshold.</summary>
/// <param name="Advertisement">The sixteen motive entries, indexed by motive (FUN_004214d4 reads the entry at motive * 6).</param>
public sealed record InteractionRecord(bool AutoFirst, bool Passed, short AttenuationCode, float CustomAttenuation, MotiveAdvertisement[] Advertisement, short Action)
{
    /// <summary>Maps a source TTAB row to the record used by the autonomy chooser. Sims.exe stores the TTAB index at +0x18 in
    /// the person's +0x72 word; despite the legacy property name, this is neither ActionTree nor AutonomyThreshold.</summary>
    public static InteractionRecord FromTtab(Interaction interaction, bool passed, MotiveAdvertisement[] advertisement)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(advertisement);
        return new InteractionRecord((interaction.Flags & 0x100) != 0, passed,
            unchecked((short)interaction.AttenuationCode), interaction.AttenuationValue, advertisement,
            unchecked((short)interaction.TtaIndex));
    }

    /// <summary>Builds the scoring advertisement from a native TTAB row. Sims.exe's FUN_004760ae reads each
    /// stored motive as personality/kind, minimum/base, delta/scale; the stream order is minimum, delta, personality.</summary>
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

/// <summary>The lot-level values Find Best Interaction reads (FUN_0045dea5 lines 30 to 45, 69 to 70, 93 and 186). The names
/// are inferred from how the handler uses them.
/// <para><see cref="Forced"/> is DAT_006c2970 (the body runs without the person gate). <see cref="Paused"/> is DAT_006f2a04
/// (no selection). <see cref="FlagB"/> is DAT_006e4f18 (the gate's second flag). <see cref="LotKey"/> is the int at +0x10c of the
/// record at the sim globals' +0x10 (inferred: the lot's key, compared with the person's family number). <see cref="LotId"/>
/// is the short at +0x22 of the lot record DAT_006e486c[7] (inferred: the household number of the lot). <see cref="Rotation"/>
/// is the int at +0x98 of the same record (inferred: the lot's facing). <see cref="ActivePerson"/> is the person FUN_004e9744 finds
/// for the selection (FUN_004e9918(0)), or null.</para></summary>
public readonly record struct AutonomyLot(bool Forced, bool Paused, bool FlagB, int LotKey, short LotId, int Rotation, XObject? ActivePerson);

/// <summary>The tuning Find Best Interaction reads, after Sims.exe FUN_0045928c. That function loads each value from the FCNS 2
/// block of GameData/Global/Global.far, and the file overrides the Sims.exe defaults given here (the defaults are the ROM
/// constants FUN_0045928c passes to its loader: DAT_00666a28, DAT_00667370, DAT_0066736c, DAT_00667368, DAT_00666a38 and
/// DAT_00667364). Each value is named with the global it is stored in. The fallback attenuation and the facing limit are not
/// tuned by the file.</summary>
public sealed record FindBestInteractionTuning
{
    /// <summary>"min autonomy score for family" (DAT_006c4374; the ROM default is 0.2).</summary>
    public float FamilyMinimum { get; init; } = 0.2f;
    /// <summary>"min autonomy score for visitors" (DAT_006c4378; the ROM default is 0.05).</summary>
    public float VisitorMinimum { get; init; } = 0.05f;
    /// <summary>"min autonomy score for sitting" (DAT_006c437c; the ROM default is 0.2). It applies when the person is sitting.</summary>
    public float SittingMinimum { get; init; } = 0.2f;
    /// <summary>"random selection count" (DAT_006c4380; the ROM default is 10). The game stores it as an int (__ftol).</summary>
    public int RandomSelectionCount { get; init; } = 10;
    /// <summary>"friendship threshold" (DAT_006c4388; the ROM default is 25).</summary>
    public int FriendshipThreshold { get; init; } = 25;
    /// <summary>"low attenuation" for families (DAT_006c4d70; the ROM default is 0.002).</summary>
    public float FamilyLowAttenuation { get; init; } = 0.002f;
    /// <summary>"moderate attenuation" for families (DAT_006c4d74; the ROM default is 0.02).</summary>
    public float FamilyModerateAttenuation { get; init; } = 0.02f;
    /// <summary>"high attenuation" for families (DAT_006c4d78; the ROM default is 0.1).</summary>
    public float FamilyHighAttenuation { get; init; } = 0.1f;
    /// <summary>"visitor low attenuation" (DAT_006c4d7c; the ROM default is 0.002).</summary>
    public float VisitorLowAttenuation { get; init; } = 0.002f;
    /// <summary>"visitor moderate attenuation" (DAT_006c4d80; the ROM default is 0.02).</summary>
    public float VisitorModerateAttenuation { get; init; } = 0.02f;
    /// <summary>"visitor high attenuation" (DAT_006c4d84; the ROM default is 0.1).</summary>
    public float VisitorHighAttenuation { get; init; } = 0.1f;
    /// <summary>The attenuation FUN_0047603d returns for a code outside 0 to 4 (DAT_00667368, the ROM constant; not tuned).</summary>
    public float FallbackAttenuation { get; init; } = 0.02f;
    /// <summary>The distance limit of facing mode 1 (DAT_00666a50 = 7; not tuned).</summary>
    public float FacingDistanceLimit { get; init; } = 7f;

    /// <summary>Builds native autonomy and relationship tuning from the entries loaded by FUN_0045928c.</summary>
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

/// <summary>The Find Best Interaction primitive (opcode 0x03), after Sims.exe FUN_0045dea5 (labelled "Find Best Interaction" in
/// Behavior.iff STR# 139). Sims.exe reaches it through the person dispatcher FUN_0045cd1f with only the frame pushed, so the
/// running object is a person and the operand is not read.
/// <para>Result: true when a candidate interaction was chosen. The chosen candidate's id goes to the frame's stack object, and
/// its action goes to the person's +0x72 word. Otherwise false, and
/// nothing is written.</para>
/// <para>The gate (lines 30 to 45): a person who is not forced passes only when the body's test allows it. A family member
/// passes with a pet (Gender bits 1 or 2, the dog and cat tables) and the gate's second flag clear. A
/// visitor passes when the lot's key differs from the person's family number or the Gender is above 1. The body then runs when
/// the lot key is not zero or the active person is not this person, and the pause flag is clear.</para>
/// <para>The candidates (lines 86 to 176) run over the native object table from 1 to its count. A candidate is skipped when it is
/// missing, its definition's field 7 is 0, or its parallel object-manager flag word (+0x24) lacks bit 19. While the person is in transit (the state range 0x28
/// to 0x31, or its native state class is 2), a candidate whose alternative-type flag is 4 or 5 is skipped.
/// The person as a candidate needs Gender 2 or more. The candidate is then considered only when its lockout count (object data 25) is below 1 and, while the class-46
/// flag is set (an object of class 0x2e exists, the state is in the range, and the family number differs from the lot id), its
/// room key is -5, its room is missing or outside, or its class is not 5 to 7. Facing (lines 116 to 127) is mode (lot rotation +
/// the person's id) &amp; 3, forced to 0 when the state table answers non-zero: mode 1 keeps a candidate at most 7 away, and
/// modes 2 and 3 keep the candidates whose id has bit 0 clear or set (the id is the object's +0xf0, inferred).</para>
/// <para>Each candidate's passed records are scored (lines 128 to 173). The distance is <see cref="GetDistanceToPrimitive.DistanceBetween"/>.
/// A record's weight is 1 / (attenuation * distance + 1), where the attenuation is the record's code (0 custom, 1 none, 2 to 4
/// low to high, for families or visitors, and the fallback for other codes). The advertisement's mean (FUN_004214d4) is taken
/// over the autonomy motives, and a mean of 0 skips the record. The score is (mean - baseline) * weight, where the baseline is
/// the mean curve value of the person's motives (FUN_00421481). A score at or above the family or visitor minimum is kept, with
/// the candidate's id, the action, the score and bit 0 of the record's flags.</para>
/// <para>The scored entries are sorted best first (inferred: the comparator at 0x45E467 is not decoded from the on-disk Sims.exe,
/// and the top entry is the one the selection reads first). The choice (lines 185 to 219): the pool is the random selection count
/// less the logic skill divided by 2000, at least 1. A family's top entry that is not AutoFirst, or any pick while the state
/// table's class is not 1, takes a uniform index below min(count, pool) from the game's random source. Otherwise the top entry
/// is taken. The index is used only when it is in range, and while the person is sitting (person data 0 is not 0) the chosen
/// score must reach the sitting minimum.</para>
/// <para>Candidate TTABs and their guards are evaluated natively through <see cref="InteractionTestEvaluator"/>
/// (FUN_00458485). A missing TTAB contributes no records, as FUN_00457e1c does when neither resource scope resolves
/// the table. Not ported: list reset FUN_004583ff and per-candidate setup FUN_0045846c. Errors:
/// the dynamic cast FUN_006359f2 that the handler checks for the person cannot fail here, because the dispatcher runs the handler
/// for persons only, so the error 0x1c path is not raised. An advertisement kind outside 1 to 22 has no personality entry in the
/// traced table (see <see cref="Personality"/>).</para></summary>
public static class FindBestInteractionPrimitive
{
    // DAT_00666b7c and DAT_00666a24 (both 0.001): the scale of an advertisement's change and of a personality change.
    private const float AdvertisementScale = 0.001f;
    private const float PersonalityScale = 0.001f;
    // DAT_00666748 (1.0): the numerator of the distance weight.
    private const float One = 1f;
    // An inverted personality field is read as 1000 minus the field.
    private const int InvertedPersonality = 1000;
    // Definition field 7 (+0xe of the definition): nonzero for an object with interactions (inferred name).
    private const int InteractionsField = 7;
    // The object table's flag word: bit 19 marks a candidate the loop may take (inferred name).
    private const int EligibleBit = 19;
    // The object class FUN_0045dea5 looks for in the pre-scan (+0xc0 == 0x2e).
    private const short ClassWithUsesRoom = 0x2e;
    // cXObject +0x84 is object-data field 29, Room (Behavior.iff STR# 141).
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
        // Sims.exe FUN_0045dea5 returns before consulting the lot/autonomy managers when Free Will is off and
        // an unselected family human fails its pet-only preliminary gate. This path is fully determined by native
        // PersonData and DAT_006c2970; do not require host data that the source never reads on this branch.
        if (context.World.AutonomyForced == false && person.SelectionWord5CC == 0 &&
            (person[PersonData.Gender] & 6) == 0)
            return Finish(PrimitiveResult.False);
        var host = context.World.RequireAutonomyHost();
        var tuning = context.World.SimulatorTuning;
        var lot = host.AutonomyLotState();
        if (context.World.AutonomyForced is { } autonomyForced) lot = lot with { Forced = autonomyForced };

        // FUN_0045dea5 lines 30 to 45: the gate, then the body's test.
        forced = lot.Forced;
        if (!lot.Forced && !PassesGate(person, lot)) return Finish(PrimitiveResult.False);
        gatePassed = true;
        if (lot.Paused || (lot.LotKey == 0 && ReferenceEquals(lot.ActivePerson, person))) return Finish(PrimitiveResult.False);

        var visitor = person[PersonData.PersonType] != 0;
        var state = person.StateValue;
        var motives = AutonomyMotiveCatalog.All;
        var baseline = Baseline(world, person, motives);
        var threshold = visitor ? tuning.VisitorMinimum : tuning.FamilyMinimum;
        var count = context.World.ObjectTableEntries.Count;
        var inTransit = context.World.StateClass(state) == 2 || InTransitRange(state);
        var classFlag = count > 0 && InTransitRange(state) && person[PersonData.FamilyNumber] != lot.LotId && HasClassWithUses(person, count);
        // FUN_0045dea5 lines 63 to 66: the facing mode, forced to 0 when the state table answers non-zero (FUN_0045e42a).
        var facing = (lot.Rotation + (ushort)person.Id) & 3;
        if (context.World.StateClass(state) != 0) facing = 0;

        var candidateRecords = new List<(XObject Candidate, float Distance, List<InteractionRecord> Records, List<short> TargetIds)>();
        var pendingGuards = new List<(ActionQueueSlot Action, List<InteractionRecord> Records, int Index)>();
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

            // FUN_0045846c / FUN_0043532b: resolve multi-tile sub-objects to their interaction lead and deduplicate.
            var candidate = ResolveInteractionLead(context.World, rawCandidate);
            if (!evaluatedCandidates.Add(candidate.Id)) continue;
            distance = (float)GetDistanceToPrimitive.DistanceBetween(person, candidate);

            // FUN_00458992: OBJD.Type == 7 candidates are only considered by pets (Gender bits 1 or 2).
            var candidateType = candidate.TypeRecord is { ObjectType: > 0 } typeRec
                ? typeRec.ObjectType
                : candidate.Definition[(int)ObjdField.Type];
            if (candidateType == 7 && (person[PersonData.Gender] & 6) == 0) continue;

            var records = new List<InteractionRecord>();
            var targetIds = new List<short>();
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

                    // FUN_00458992:53-71: while in the transit state range (0x28..0x31), category 4/7 objects require
                    // a positive visitor-inventory token in the person's scope and only check pet flags (0x400/0x200).
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
                        // FUN_00458992:92-96: ungreeted visitor pets cannot autonomously target indoor objects on residential lots.
                        if (stateClass == 1 && isVisitor && (isDog || isCat) && !isGreeted &&
                            candidate[RoomKeyField] != 0)
                            continue;
                    }

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
                }
            }

            // FUN_00458992 / FUN_0041ee9b: when scanning a target Sim whose current action is Joinable ((Flags & 2) != 0),
            // evaluate joining that interaction on its target object.
            if (candidate is XPerson targetPerson && !ReferenceEquals(targetPerson, person) &&
                targetPerson.CurrentActionRecord is { Owner: { } activeOwner } currentAction)
            {
                var joinOwner = ResolveInteractionLead(context.World, activeOwner);
                var joinInteractions = joinOwner.TypeRecord?.Interactions?.Interactions;
                var activeInteraction = joinInteractions?.FirstOrDefault(entry => entry.TtaIndex == currentAction.RawDword10);
                if (activeInteraction is not null && (activeInteraction.Flags & 2) != 0)
                {
                    var joinInteraction = activeInteraction.JoiningIndex != -1
                        ? joinInteractions!.FirstOrDefault(entry => entry.TtaIndex == activeInteraction.JoiningIndex)
                        : null;
                    if (joinInteraction is not null)
                    {
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
                    }
                }
            }

            candidateRecords.Add((candidate, distance, records, targetIds));
        }

        // FUN_0045dea5 initializes one shared check context before iterating candidates and restores the person's
        // temporary values once at the end (FUN_004583ff/FUN_00458449). Evaluate the ordered native guards as one batch.
        if (pendingGuards.Count != 0)
        {
            InteractionTestEvaluator.Evaluate(world, person, pendingGuards.Select(guard => guard.Action).ToArray(), autonomy: true);
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
                    var interactionName = targetObject.TypeRecord?.Interactions?.Interactions
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
                    scored.Add(new ScoredInteraction(targetObjectId, record.Action, score, record.AutoFirst));
                }
                else if (captureDiagnostics) belowThresholdRecords++;
            }
        }

        if (scored.Count == 0) return Finish(PrimitiveResult.False);
        var ranked = scored.OrderByDescending(entry => entry.Score).ToList();
        var top = ranked[0];

        // FUN_0045dea5 lines 185 to 219: the random choice among the top candidates, then the sitting minimum.
        var pool = Math.Max(1, tuning.RandomSelectionCount - person[PersonData.LogicSkill] / 2000);
        // FUN_0045dea5 branches on person +0x5cc (SelectionWord5CC), not PersonType:
        // unselected Sims randomize unless the top action is Auto First.
        var random = (person.SelectionWord5CC == 0 && !top.AutoFirst) || context.World.StateClass(state) != 1;
        var chosenIndex = random ? (int)(context.World.NextRandom() % (uint)Math.Min(ranked.Count, pool)) : 0;
        if (chosenIndex >= ranked.Count) return Finish(PrimitiveResult.False);
        var chosen = ranked[chosenIndex];
        if (person[PersonData.Sitting] != 0 && chosen.Score < tuning.SittingMinimum) return Finish(PrimitiveResult.False);

        person.InteractionActionWord72 = chosen.Action;
        context.Frame.StackObject = chosen.Candidate;
        return Finish(PrimitiveResult.True, chosen.Candidate, chosen.Action);
    }

    /// <summary>FUN_0045846c / FUN_0043532b: resolves a multi-tile part to its interaction-group lead. Only a part whose
    /// OBJD MasterId (definition +0x14) is nonzero and whose OBJD InteractionGroup (definition +0x10) is positive is
    /// resolved: the walk starts at the part's +0x190 lead (the part itself when that is 0), follows the +0x18c chain,
    /// and answers the first member whose InteractionGroup is the negated group. Every other object, and a walk that
    /// reaches the end of the chain, answers the candidate itself. Sims.exe never consults the OBJD SubIndex here and
    /// never falls back to the multi-tile lead or the chain's last member: a single bed's middle part (group -2) must
    /// stay the interaction object, since its Sleep tree (BHAV 4134) reads the stack object's slot 0 and only that part
    /// has slots.</summary>
    internal static XObject ResolveInteractionLead(SimulationWorld world, XObject candidate)
    {
        if (candidate.Definition is not { Length: > (int)ObjdField.MasterId } definition ||
            definition[(int)ObjdField.MasterId] == 0)
            return candidate;
        var group = definition[(int)ObjdField.InteractionGroup];
        if (group <= 0) return candidate;
        var current = candidate.MultiTileMasterId != 0 ? world.ObjectById(candidate.MultiTileMasterId) : candidate;
        var visited = new HashSet<short>();
        while (current is not null && visited.Add(current.Id))
        {
            if (current.Definition is { Length: > (int)ObjdField.InteractionGroup } partDefinition &&
                partDefinition[(int)ObjdField.InteractionGroup] == -group)
                return current;
            current = current.MultiTileNextId != 0 ? world.ObjectById(current.MultiTileNextId) : null;
        }
        return candidate;
    }

    // FUN_0045dea5 lines 30 to 39: the gate of a person who is not forced. A family member passes with a pet and the second
    // flag clear; a visitor passes when the lot key differs from the family number or the Gender is above 1.
    private static bool PassesGate(XPerson person, AutonomyLot lot) =>
        person[PersonData.PersonType] == 0
            ? (person[PersonData.Gender] & 6) != 0 && !lot.FlagB
            : lot.LotKey != person[PersonData.FamilyNumber] || person[PersonData.Gender] > 1;

    // FUN_0045dea5 reads the category short at cXObject +0xc0: a candidate with no uses is considered, and while the class-46
    // flag is set only when its room key is -5, its room is missing or outside, or its category is not 5 to 7.
    private static bool ConsideredForScore(SimulationWorld world, XObject candidate, bool classFlag)
    {
        if (candidate[ObjectData.LockoutCount] >= 1) return false;
        if (!classFlag) return true;
        var key = candidate[RoomKeyField];
        if (key == -5 || !world.HasRoomRecord(key) || world.RoomIsOutside(key)) return true;
        return candidate[ObjectData.Category] is not (5 or 6 or 7);
    }

    // FUN_0045dea5 lines 69 to 83: whether an object with category 0x2e is in the native object table.
    private static bool HasClassWithUses(XPerson person, int count)
    {
        for (var position = 1; position <= count; position++)
        {
            var item = person.ObjectTableEntry((short)position);
            if (item is not null && item[ObjectData.Category] == ClassWithUsesRoom) return true;
        }
        return false;
    }

    // FUN_0045dea5 lines 116 to 127: mode 0 takes every candidate, mode 1 keeps those at most the facing limit away, and modes 2
    // and 3 keep the candidates whose raw +0xf0 word has bit 0 clear or set.
    private static bool FacingAllows(int facing, XObject candidate, float distance, FindBestInteractionTuning tuning) => facing switch
    {
        0 => true,
        1 => distance <= tuning.FacingDistanceLimit,
        2 => (candidate.RequestKey & 1) == 0,
        _ => (candidate.RequestKey & 1) != 0,
    };

    // FUN_0047603d: the attenuation of a record. Code 0 is the record's custom value, 1 is none, 2 to 4 are low, moderate and
    // high (families or visitors), and other codes give the ROM fallback.
    private static float Attenuation(InteractionRecord record, FindBestInteractionTuning tuning, bool visitor) => record.AttenuationCode switch
    {
        0 => record.CustomAttenuation,
        1 => 0f,
        2 => visitor ? tuning.VisitorLowAttenuation : tuning.FamilyLowAttenuation,
        3 => visitor ? tuning.VisitorModerateAttenuation : tuning.FamilyModerateAttenuation,
        4 => visitor ? tuning.VisitorHighAttenuation : tuning.FamilyHighAttenuation,
        _ => tuning.FallbackAttenuation,
    };

    // FUN_00421481: the mean curve value of the person's motives, over the autonomy motives.
    private static float Baseline(SimulationWorld world, XPerson person, IReadOnlyList<short> motives)
    {
        var sum = 0f;
        foreach (var motive in motives) sum += AutonomyMotiveCatalog.EvaluateCurve(world.PersonMotiveDecayTuning, person, motive, person.Motives[motive]);
        return motives.Count == 0 ? 0 : sum / motives.Count;
    }

    // FUN_004214d4: the mean over the autonomy motives of the curve at the motive's value plus the advertisement's change.
    // The change is base + scale for kind 0, and base + scale * personality * 0.001 for kinds 1 to 4 (the personality field is
    // inverted for the even kinds). The change is scaled by 0.001 before it is added to the motive.
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

    // FUN_004214d4 indexes DAT_00666a6c in 12-byte rows. The complete mapping below comes from the original
    // Sims.exe .rdata table at VA 0x666a6c; its first two dwords select the PersonData field and inversion.
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

    // FUN_00407525: the state range 0x28 to 0x31 (the state is above 0x27 and below 0x32).
    private static bool InTransitRange(short state) => state > 0x27 && state < 0x32;

    // Routing guarantees a person (FUN_0045cd1f runs only for the person class).
    private static XPerson Person(in TreeContext context) =>
        context.Me as XPerson ?? throw new InvalidOperationException($"Object {context.Me.Id} is not a person; FUN_0045cd1f runs for persons only.");

    // A candidate in the scored list: its id (+0xf0 of the object, the value written to the stack object), the action, the score
    // and the record's AutoFirst flag.
    private readonly record struct ScoredInteraction(short Candidate, short Action, float Score, bool AutoFirst);
}
