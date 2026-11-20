using System.Globalization;
using SimsEngine.Formats;

namespace SimsEngine.Simulation.Neighborhood;

public sealed class InactiveSimState
{
    public short NeighborId { get; set; }
    public uint Guid { get; set; }
    public string Name { get; set; } = string.Empty;
    public short FamilyId { get; set; }
    public float[] Motives { get; set; } = new float[16];
    public float[] SavedValues7CC { get; set; } = new float[16];
    public short[] PersonData { get; set; } = new short[256];
    public Dictionary<int, int[]> Relationships { get; set; } = new();
    public bool IsAtWorkOrSchool { get; set; }
    public string ActivitySummary { get; set; } = "Idle";
    public bool DirtyPersonData { get; set; }
    public bool DirtyRelationships { get; set; }
    public bool DirtyPersonDataForNbrs { get; set; }
    public bool DirtyRelationshipsForNbrs { get; set; }
    public bool DirtyMotives { get; set; }

    public InactiveSimState Clone()
    {
        var relCopy = new Dictionary<int, int[]>(Relationships.Count);
        foreach (var (k, v) in Relationships)
            relCopy[k] = v.ToArray();
        return new InactiveSimState
        {
            NeighborId = NeighborId,
            Guid = Guid,
            Name = Name,
            FamilyId = FamilyId,
            Motives = Motives.ToArray(),
            SavedValues7CC = SavedValues7CC.ToArray(),
            PersonData = PersonData.ToArray(),
            Relationships = relCopy,
            IsAtWorkOrSchool = IsAtWorkOrSchool,
            ActivitySummary = ActivitySummary,
            DirtyPersonData = DirtyPersonData,
            DirtyRelationships = DirtyRelationships,
            DirtyPersonDataForNbrs = DirtyPersonDataForNbrs,
            DirtyRelationshipsForNbrs = DirtyRelationshipsForNbrs,
            DirtyMotives = DirtyMotives,
        };
    }
}

public sealed class InactiveLotState
{
    public short House { get; set; }
    public ushort FamilyChunkId { get; set; }
    public short FamilyNumber { get; set; }
    public bool IsActive { get; set; }
    public long PendingDecayTicks { get; set; }
    public long PendingAutonomyTicks { get; set; }
    public int LastEvaluatedHour { get; set; } = -1;
    public int BudgetDelta { get; set; }
    public List<InactiveSimState> Sims { get; set; } = [];

    public InactiveLotState Clone() => new()
    {
        House = House,
        FamilyChunkId = FamilyChunkId,
        FamilyNumber = FamilyNumber,
        IsActive = IsActive,
        PendingDecayTicks = PendingDecayTicks,
        PendingAutonomyTicks = PendingAutonomyTicks,
        LastEvaluatedHour = LastEvaluatedHour,
        BudgetDelta = BudgetDelta,
        Sims = Sims.Select(static s => s.Clone()).ToList(),
    };
}

public sealed class NeighborLotControllerSnapshot
{
    public int Version { get; set; } = 1;
    public long TotalTicksProcessed { get; set; }
    public List<InactiveLotState> Lots { get; set; } = [];
    public List<string> RecentEvents { get; set; } = [];
}

public sealed class NeighborLotController
{
    private static readonly ResourceType FamiType = ResourceType.Parse("FAMI");
    private static readonly ResourceType NbrsType = ResourceType.Parse("NBRS");
    private static readonly ResourceType ObjtType = ResourceType.Parse("objt");
    private static readonly ResourceType ObjmType = ResourceType.Parse("ObjM");
    private static readonly byte[] PersonRecordFieldsVersion10 =
    [
        2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 16, 20, 15, 26, 17, 18, 46, 47, 48, 49, 50, 51, 52, 53,
        13, 14, 58, 60, 61, 56, 57, 63, 65, 67, 68, 69, 74, 54, 55, 75, 76, 80, 81, 82, 83, 84, 85, 86,
    ];

    private static readonly PersonMotiveDecayTuning FallbackMotiveTuning = new(
        Adult: new PersonMotiveDecayProfile(
            EnergySpan: 100f, WakeHours: 16f, WakeHour: 7f, EnergyDrift: 0.01f,
            HungerToBladderMultiplier: 0.3f, ComfortDecrementActive: 0.4f, ComfortDecrementLazy: 0.6f,
            HungerDecrementRatio: 0.0021f, SocialDecrementBase: 0.055f, SocialDecrementMultiplier: 0.0001254f,
            FunDecrementAwake: 0.25f, FunMultiplierAsleep: 0.99f, HygieneDecrementAwake: 0.17f,
            HygieneDecrementAsleep: 0.08f, BladderDecrementAwake: 0.3f, BladderDecrementAsleep: 0.15f,
            IsPet: false),
        Dog: new PersonMotiveDecayProfile(
            EnergySpan: 100f, WakeHours: 40f, WakeHour: 7f, EnergyDrift: 0.01f,
            HungerToBladderMultiplier: 0.3f, ComfortDecrementActive: 0.3f, ComfortDecrementLazy: 0.4f,
            HungerDecrementRatio: 0.0021f, SocialDecrementBase: 0.055f, SocialDecrementMultiplier: 0.0001254f,
            FunDecrementAwake: 0.25f, FunMultiplierAsleep: 0.99f, HygieneDecrementAwake: 0.17f,
            HygieneDecrementAsleep: 0.08f, BladderDecrementAwake: 0.3f, BladderDecrementAsleep: 0.15f,
            IsPet: true),
        Cat: new PersonMotiveDecayProfile(
            EnergySpan: 100f, WakeHours: 40f, WakeHour: 7f, EnergyDrift: 0.01f,
            HungerToBladderMultiplier: 0.3f, ComfortDecrementActive: 0.3f, ComfortDecrementLazy: 0.4f,
            HungerDecrementRatio: 0.0021f, SocialDecrementBase: 0.055f, SocialDecrementMultiplier: 0.0001254f,
            FunDecrementAwake: 0.25f, FunMultiplierAsleep: 0.99f, HygieneDecrementAwake: 0.17f,
            HygieneDecrementAsleep: 0.08f, BladderDecrementAwake: 0.3f, BladderDecrementAsleep: 0.15f,
            IsPet: true));

    private readonly Dictionary<short, InactiveLotState> _lots = new();
    private readonly List<string> _recentEvents = [];
    private PersonMotiveDecayTuning? _motiveTuning;
    private ISimulationCareerHost? _careerHost;
    private long _totalTicksProcessed;

    public IReadOnlyList<string> RecentEvents => _recentEvents;
    public long TotalTicksProcessed => _totalTicksProcessed;

    public void Configure(PersonMotiveDecayTuning? motiveTuning, ISimulationCareerHost? careerHost)
    {
        if (motiveTuning is not null) _motiveTuning = motiveTuning;
        if (careerHost is not null) _careerHost = careerHost;
    }

    public void RegisterInactiveLot(short house, InactiveLotState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (house <= 0) return;
        var copy = state.Clone();
        copy.House = house;
        _lots[house] = copy;
    }

    public void UnregisterLot(short house) => _lots.Remove(house);

    public void SetLotActive(short house, bool isActive)
    {
        if (_lots.TryGetValue(house, out var lot))
            lot.IsActive = isActive;
    }

    public bool TryGetLot(short house, out InactiveLotState state) => _lots.TryGetValue(house, out state!);

    public void Clear()
    {
        _lots.Clear();
        _recentEvents.Clear();
        _totalTicksProcessed = 0;
    }

    public NeighborLotControllerSnapshot CaptureSnapshot() => new()
    {
        Version = 1,
        TotalTicksProcessed = _totalTicksProcessed,
        Lots = _lots.Values.OrderBy(static l => l.House).Select(static l => l.Clone()).ToList(),
        RecentEvents = _recentEvents.ToList(),
    };

    public void RestoreSnapshot(NeighborLotControllerSnapshot? snapshot)
    {
        _lots.Clear();
        _recentEvents.Clear();
        _totalTicksProcessed = 0;
        if (snapshot is null) return;

        _totalTicksProcessed = snapshot.TotalTicksProcessed;
        foreach (var lot in snapshot.Lots)
        {
            if (lot.House <= 0) continue;
            _lots[lot.House] = lot.Clone();
        }
        if (snapshot.RecentEvents is { Count: > 0 })
            _recentEvents.AddRange(snapshot.RecentEvents.TakeLast(32));
    }

    public void RegisterFromWorld(
        short house,
        SimulationWorld world,
        short familyChunkId,
        IReadOnlySet<uint>? excludeGuids = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (house <= 0) return;

        if (_motiveTuning is null && world.PersonMotiveDecayTuning is not null)
            _motiveTuning = world.PersonMotiveDecayTuning;
        if (_careerHost is null && world.CareerHost is not null)
            _careerHost = world.CareerHost;

        var activeFamilyMembers = world.NeighborhoodFamilies?.ActiveFamilyNumber is > 0
            ? world.NeighborInteractionManagerTypeKeys.ToHashSet()
            : null;
        var familyNumber = world.NeighborhoodFamilies?.ActiveFamilyNumber ?? (short)0;

        var sims = new List<InactiveSimState>();
        foreach (var person in world.WalkOrder.OfType<XPerson>())
        {
            var guid = unchecked((uint)(person.TypeRecord?.Key ?? 0));
            if (guid == 0) continue;
            if (excludeGuids is not null && excludeGuids.Contains(guid)) continue;

            var nid = person[SimsEngine.Simulation.PersonData.NeighborId];
            if (nid <= 0) continue;

            var pFam = person[SimsEngine.Simulation.PersonData.FamilyNumber];
            var pType = person[SimsEngine.Simulation.PersonData.PersonType];
            if (pType != 0 && pFam <= 0) continue;
            if (activeFamilyMembers is { Count: > 0 } && !activeFamilyMembers.Contains(unchecked((int)guid)))
                continue;
            if (pFam <= 0 && familyNumber <= 0) continue;

            var rels = new Dictionary<int, int[]>();
            if (world.Neighbors.TryGetValue(nid, out var neighborState))
            {
                foreach (var (target, vals) in neighborState.Relationships.Snapshot())
                    rels[target] = vals.ToArray();
            }
            else
            {
                foreach (var (target, vals) in person.Relationships.Snapshot())
                    rels[target] = vals.ToArray();
            }

            var motives = new float[16];
            Array.Copy(person.Motives, motives, Math.Min(motives.Length, person.Motives.Length));
            var saved7CC = new float[16];
            Array.Copy(person.SavedPersonValues7CC, saved7CC, Math.Min(saved7CC.Length, person.SavedPersonValues7CC.Length));
            var pdata = new short[256];
            Array.Copy(person.PersonData, pdata, Math.Min(pdata.Length, person.PersonData.Length));

            var isAtWorkOrSchool = person[SimsEngine.Simulation.PersonData.IsPersonAtWorkSchool] != 0;
            var activity = isAtWorkOrSchool
                ? (PersonAgeCategory.IsChild(pdata[(int)SimsEngine.Simulation.PersonData.PersonAge]) ? "At school" : "At work")
                : "Relaxing";

            var dirtyPd = world.DirtyPersistentPersonNeighborIds.Contains(nid);
            var dirtyRel = world.DirtyRelationshipNeighbors.Contains(nid);
            sims.Add(new InactiveSimState
            {
                NeighborId = nid,
                Guid = guid,
                Name = !string.IsNullOrWhiteSpace(person.Name)
                    ? person.Name
                    : world.Neighbors.TryGetValue(nid, out var n) && !string.IsNullOrWhiteSpace(n.Name)
                        ? n.Name
                        : $"Sim {nid}",
                FamilyId = pFam > 0 ? pFam : familyNumber,
                Motives = motives,
                SavedValues7CC = saved7CC,
                PersonData = pdata,
                Relationships = rels,
                IsAtWorkOrSchool = isAtWorkOrSchool,
                ActivitySummary = activity,
                DirtyPersonData = dirtyPd,
                DirtyRelationships = dirtyRel,
                DirtyPersonDataForNbrs = dirtyPd,
                DirtyRelationshipsForNbrs = dirtyRel,
                DirtyMotives = false,
            });
        }

        if (sims.Count == 0)
        {
            _lots.Remove(house);
            return;
        }

        var existingBudgetDelta = _lots.TryGetValue(house, out var existing) ? existing.BudgetDelta : 0;
        _lots[house] = new InactiveLotState
        {
            House = house,
            FamilyChunkId = unchecked((ushort)(familyChunkId > 0 ? familyChunkId : 0)),
            FamilyNumber = familyNumber,
            IsActive = false,
            PendingDecayTicks = 0,
            PendingAutonomyTicks = 0,
            LastEvaluatedHour = world.Clock.Hour,
            BudgetDelta = existingBudgetDelta,
            Sims = sims,
        };
    }

    public void EnsureSeededFromNeighborhood(
        IffFile neighborhood,
        string userDataPath,
        IReadOnlySet<int> activeLotIds,
        IReadOnlyDictionary<short, byte[]>? savedLotBytes = null,
        Func<uint, Objd?>? findDefinition = null,
        NeighborhoodManager? manager = null)
    {
        ArgumentNullException.ThrowIfNull(neighborhood);

        var zoningByHouse = ReadLotZoning(userDataPath);
        var neighborByGuid = new Dictionary<uint, Neighbor>();
        var neighborById = new Dictionary<short, Neighbor>();
        if (neighborhood.OfType(NbrsType).FirstOrDefault() is { } nbrsChunk)
        {
            var nbrs = Neighbors.ReadRecoveringSupportedPrefix(neighborhood, nbrsChunk);
            foreach (var entry in nbrs.Entries)
            {
                if (entry.NeighborId <= 0) continue;
                neighborById[entry.NeighborId] = entry;
                if (entry.Guid != 0)
                    neighborByGuid[entry.Guid] = entry;
            }
        }

        foreach (var famChunk in neighborhood.OfType(FamiType))
        {
            if (famChunk.Id == 0) continue;
            var family = Family.Read(neighborhood, famChunk);
            if (family.HouseNumber <= 0 || family.HouseNumber > short.MaxValue || family.Members.Count == 0)
                continue;

            var house = (short)family.HouseNumber;
            if (IsCommunityOrVacation(house, zoningByHouse))
                continue;

            if (_lots.TryGetValue(house, out var existingLot))
            {
                existingLot.IsActive = activeLotIds.Contains(house);
                continue;
            }

            if (activeLotIds.Contains(house))
                continue;

            IffFile? lotFile = null;
            if (savedLotBytes is not null && savedLotBytes.TryGetValue(house, out var rawBytes) && rawBytes.Length > 0)
            {
                try { lotFile = new IffFile(rawBytes, $"House{house:D2}.iff"); }
                catch { lotFile = null; }
            }

            if (lotFile is null && !string.IsNullOrWhiteSpace(userDataPath))
            {
                var housePath = Path.Combine(userDataPath, "Houses", $"House{house:D2}.iff");
                if (!File.Exists(housePath))
                    continue;
                try { lotFile = IffFile.Open(housePath); }
                catch { continue; }
            }

            if (lotFile is null)
                continue;

            var seededSims = SeedSimsFromLotAndNeighbors(house, lotFile, family, neighborByGuid, findDefinition, manager);
            if (seededSims.Count == 0)
                continue;

            var lotState = new InactiveLotState
            {
                House = house,
                FamilyChunkId = famChunk.Id,
                FamilyNumber = unchecked((short)family.FamilyNumber),
                IsActive = false,
                Sims = seededSims,
            };
            _lots[house] = lotState;

            if (manager is not null)
            {
                foreach (var sim in seededSims)
                {
                    if (manager.GetSimDetails(sim.NeighborId) is { } loc)
                    {
                        loc.IsAtWorkOrSchool = sim.IsAtWorkOrSchool;
                        if (!string.IsNullOrWhiteSpace(sim.ActivitySummary))
                            loc.ActivitySummary = sim.ActivitySummary;
                    }
                }
            }
        }
    }

    public void TickNeighborhood(
        int ticks,
        int currentHour,
        int currentMinute,
        int currentDay,
        NeighborhoodManager? manager = null)
    {
        if (ticks <= 0) return;

        var tuning = _motiveTuning ?? FallbackMotiveTuning;
        _totalTicksProcessed += ticks;

        foreach (var lot in _lots.Values)
        {
            if (lot.IsActive || lot.Sims.Count == 0)
                continue;

            lot.PendingDecayTicks += ticks;
            lot.PendingAutonomyTicks += ticks;

            while (lot.PendingDecayTicks >= 60)
            {
                lot.PendingDecayTicks -= 60;
                ApplyDecayAndScheduleStep(lot, tuning, currentHour, currentDay);
            }

            while (lot.PendingAutonomyTicks >= 300)
            {
                lot.PendingAutonomyTicks -= 300;
                ApplyAutonomyStep(lot);
            }

            lot.LastEvaluatedHour = currentHour;

            if (manager is not null)
            {
                foreach (var sim in lot.Sims)
                {
                    if (manager.GetSimDetails(sim.NeighborId) is { } loc && loc.CurrentLotId == lot.House)
                    {
                        loc.IsAtWorkOrSchool = sim.IsAtWorkOrSchool;
                        loc.IsAway = sim.IsAtWorkOrSchool;
                        loc.ActivitySummary = sim.ActivitySummary;
                    }
                }
            }
        }
    }

    public bool ApplyToWorld(SimulationWorld world, short house, NeighborhoodManager? manager = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (house <= 0 || !_lots.TryGetValue(house, out var lot))
            return false;

        lot.IsActive = true;
        var appliedAny = false;

        foreach (var sim in lot.Sims)
        {
            var person = world.WalkOrder.OfType<XPerson>()
                .FirstOrDefault(p => p[SimsEngine.Simulation.PersonData.NeighborId] == sim.NeighborId ||
                                     (sim.Guid != 0 && unchecked((uint)(p.TypeRecord?.Key ?? 0)) == sim.Guid));
            if (person is null) continue;

            if (sim.DirtyMotives)
            {
                Array.Copy(sim.Motives, person.Motives, Math.Min(person.Motives.Length, sim.Motives.Length));
                Array.Copy(sim.SavedValues7CC, person.SavedPersonValues7CC,
                    Math.Min(person.SavedPersonValues7CC.Length, sim.SavedValues7CC.Length));
                person.MotiveDecayLastTickCounter = world.Clock.Ticks;
                appliedAny = true;
            }

            if (sim.DirtyPersonData)
            {
                ApplyPersistentFields(sim.PersonData, person.PersonData);
                person[SimsEngine.Simulation.PersonData.JobData] = sim.PersonData[(int)SimsEngine.Simulation.PersonData.JobData];
                person[SimsEngine.Simulation.PersonData.IsPersonAtWorkSchool] =
                    sim.PersonData[(int)SimsEngine.Simulation.PersonData.IsPersonAtWorkSchool];
                world.SavePersonRecord(person);
                appliedAny = true;
            }
            else if (sim.IsAtWorkOrSchool != (person[SimsEngine.Simulation.PersonData.IsPersonAtWorkSchool] != 0))
            {
                person[SimsEngine.Simulation.PersonData.IsPersonAtWorkSchool] = (short)(sim.IsAtWorkOrSchool ? 1 : 0);
                person[SimsEngine.Simulation.PersonData.JobData] = (short)(sim.IsAtWorkOrSchool ? 1 : 0);
            }

            if (sim.DirtyRelationships && world.Neighbors.TryGetValue(sim.NeighborId, out var sourceNeighbor))
            {
                foreach (var (targetKey, values) in sim.Relationships)
                {
                    if (targetKey is < short.MinValue or > short.MaxValue) continue;
                    var targetId = (short)targetKey;
                    sourceNeighbor.Relationships.Resize(targetId, values.Length);
                    person.Relationships.Resize(targetId, values.Length);
                    for (var i = 0; i < values.Length; i++)
                    {
                        sourceNeighbor.Relationships.Set(targetId, i, values[i]);
                        person.Relationships.Set(targetId, i, values[i]);
                    }
                    if (world.Neighbors.TryGetValue(targetId, out var targetNeighbor))
                        world.MarkRelationshipsChanged(sourceNeighbor, targetNeighbor);
                }
                appliedAny = true;
            }

            if (manager is not null && manager.GetSimDetails(sim.NeighborId) is { } loc && loc.CurrentLotId == house)
            {
                loc.IsAtWorkOrSchool = sim.IsAtWorkOrSchool;
                loc.ActivitySummary = sim.ActivitySummary;
            }

            sim.DirtyMotives = false;
            sim.DirtyPersonData = false;
            sim.DirtyRelationships = false;
            sim.DirtyPersonDataForNbrs = false;
            sim.DirtyRelationshipsForNbrs = false;
        }

        if (lot.BudgetDelta != 0 && world.FamilyBudget is { } budget)
        {
            budget.AlterFamilyBudget(0, -lot.BudgetDelta);
            lot.BudgetDelta = 0;
            appliedAny = true;
        }

        return appliedAny;
    }

    public IffFile FlushToNeighborhood(IffFile neighborhood, out bool changed) =>
        FlushToNeighborhood(neighborhood, out changed, flushBudgets: false, out _);

    public IffFile FlushToNeighborhood(
        IffFile neighborhood,
        out bool changed,
        bool flushBudgets,
        out Dictionary<short, int> flushedBudgets)
    {
        ArgumentNullException.ThrowIfNull(neighborhood);
        changed = false;
        flushedBudgets = new Dictionary<short, int>();
        var updated = neighborhood;

        var dirtySimsByNid = new Dictionary<short, InactiveSimState>();
        foreach (var lot in _lots.Values)
        {
            if (lot.IsActive) continue;
            foreach (var sim in lot.Sims)
            {
                if (sim.DirtyPersonDataForNbrs || sim.DirtyRelationshipsForNbrs)
                    dirtySimsByNid[sim.NeighborId] = sim;
            }
        }

        if (dirtySimsByNid.Count > 0 &&
            updated.OfType(NbrsType).FirstOrDefault() is { } nbrsChunk)
        {
            var currentNbrs = Neighbors.ReadRecoveringSupportedPrefix(updated, nbrsChunk);
            if (currentNbrs.IsComplete)
            {
                var updatedEntries = new List<Neighbor>(currentNbrs.Entries.Count);
                var nbrsChanged = false;
                foreach (var existing in currentNbrs.Entries)
                {
                    if (!dirtySimsByNid.TryGetValue(existing.NeighborId, out var sim))
                    {
                        updatedEntries.Add(existing);
                        continue;
                    }

                    var nextPersonData = existing.PersonData?.ToArray() ?? new short[256];
                    if (sim.DirtyPersonDataForNbrs)
                    {
                        if (nextPersonData.Length < 256)
                            Array.Resize(ref nextPersonData, 256);
                        ApplyPersistentFields(sim.PersonData, nextPersonData);
                    }

                    var nextRelationships = existing.Relationships;
                    if (sim.DirtyRelationshipsForNbrs && sim.Relationships.Count > 0)
                    {
                        var merged = new Dictionary<int, IReadOnlyList<int>>(existing.Relationships);
                        foreach (var (targetKey, vals) in sim.Relationships)
                            merged[targetKey] = vals.ToArray();
                        nextRelationships = merged;
                    }

                    updatedEntries.Add(existing with
                    {
                        RecordVersion = existing.RecordVersion > 0 ? Math.Max(existing.RecordVersion, 10) : 10,
                        PersonData = nextPersonData,
                        Relationships = nextRelationships,
                    });
                    sim.DirtyPersonDataForNbrs = false;
                    sim.DirtyRelationshipsForNbrs = false;
                    nbrsChanged = true;
                    changed = true;
                }

                if (nbrsChanged)
                {
                    var rewritten = new Neighbors(
                        currentNbrs.Version,
                        currentNbrs.SlotCount,
                        updatedEntries,
                        currentNbrs.IsComplete,
                        currentNbrs.CompleteSlotPrefix);
                    var swap = nbrsChunk.Data.Span.Length > 0 && nbrsChunk.Data.Span[0] != 0;
                    updated = updated.WithChunkDataResized(nbrsChunk.Type, nbrsChunk.Id, rewritten.WriteChunkData(swap));
                }
            }
        }

        if (flushBudgets)
        {
            var budgetLots = _lots.Values
                .Where(static l => !l.IsActive && l.BudgetDelta != 0)
                .ToDictionary(static l => l.House);
            if (budgetLots.Count > 0)
            {
                foreach (var famChunk in updated.OfType(FamiType).ToArray())
                {
                    if (famChunk.Id == 0) continue;
                    var fam = Family.Read(updated, famChunk);
                    if (fam.HouseNumber <= 0 || fam.HouseNumber > short.MaxValue) continue;
                    var h = (short)fam.HouseNumber;
                    if (!budgetLots.TryGetValue(h, out var lot)) continue;
                    if (lot.FamilyChunkId != 0 && famChunk.Id != lot.FamilyChunkId) continue;

                    var delta = lot.BudgetDelta;
                    var updatedFam = fam.WithBudget(unchecked(fam.Budget + delta));
                    updated = updated.WithAddedOrReplacedChunk(
                        FamiType,
                        famChunk.Id,
                        updatedFam.Write(famChunk.Id),
                        famChunk.Flags,
                        famChunk.Label);
                    flushedBudgets[h] = flushedBudgets.GetValueOrDefault(h) + delta;
                    lot.BudgetDelta = 0;
                    budgetLots.Remove(h);
                    changed = true;
                }
            }
        }

        return updated;
    }

    private void ApplyDecayAndScheduleStep(InactiveLotState lot, PersonMotiveDecayTuning tuning, int currentHour, int currentDay)
    {
        foreach (var sim in lot.Sims)
        {
            var age = sim.PersonData[(int)SimsEngine.Simulation.PersonData.PersonAge];
            var gender = sim.PersonData[(int)SimsEngine.Simulation.PersonData.Gender];
            var isPet = (gender & 6) != 0;
            var isChild = PersonAgeCategory.IsChild(age);
            var jobType = sim.PersonData[(int)SimsEngine.Simulation.PersonData.JobType];
            var jobLevel = sim.PersonData[(int)SimsEngine.Simulation.PersonData.JobPromotionLevel];

            var scheduled = false;
            short salary = 0;
            if (!isPet && isChild)
            {
                scheduled = currentHour is >= 9 and < 15;
            }
            else if (!isPet && jobType > 0 && _careerHost is not null)
            {
                var startHour = _careerHost.JobData(jobType, jobLevel, 12);
                var endHour = _careerHost.JobData(jobType, jobLevel, 13);
                salary = _careerHost.JobData(jobType, jobLevel, 1) ?? 0;
                if (startHour is { } s && endHour is { } e && s != e)
                {
                    scheduled = s < e
                        ? currentHour >= s && currentHour < e
                        : currentHour >= s || currentHour < e;
                }
            }

            if (scheduled && !sim.IsAtWorkOrSchool)
            {
                sim.IsAtWorkOrSchool = true;
                sim.PersonData[(int)SimsEngine.Simulation.PersonData.IsPersonAtWorkSchool] = 1;
                sim.PersonData[(int)SimsEngine.Simulation.PersonData.JobData] = 1;
                sim.Motives[(int)Motive.SleepState] = 0f;
                sim.ActivitySummary = isChild ? "At school" : "At work";
                sim.DirtyPersonData = true;
                sim.DirtyPersonDataForNbrs = true;
                RecordEvent($"{sim.Name} left for {(isChild ? "school" : "work")} (lot {lot.House})");
            }
            else if (!scheduled && sim.IsAtWorkOrSchool)
            {
                sim.IsAtWorkOrSchool = false;
                sim.PersonData[(int)SimsEngine.Simulation.PersonData.IsPersonAtWorkSchool] = 0;
                sim.PersonData[(int)SimsEngine.Simulation.PersonData.JobData] = 0;
                var perf = sim.PersonData[(int)SimsEngine.Simulation.PersonData.JobPerformance];
                sim.PersonData[(int)SimsEngine.Simulation.PersonData.JobPerformance] = (short)Math.Clamp(perf + 2, -100, 100);
                sim.PersonData[(int)SimsEngine.Simulation.PersonData.LastDayAtWork] = unchecked((short)currentDay);
                if (!isChild && salary > 0)
                    lot.BudgetDelta += salary;
                sim.ActivitySummary = "Relaxing";
                sim.DirtyPersonData = true;
                sim.DirtyPersonDataForNbrs = true;
                RecordEvent($"{sim.Name} returned from {(isChild ? "school" : "work")} (lot {lot.House})");
            }

            tuning.Apply(sim.Motives, sim.SavedValues7CC, sim.PersonData, currentHour);

            if (sim.IsAtWorkOrSchool)
            {
                sim.Motives[(int)Motive.Energy] = Math.Max(sim.Motives[(int)Motive.Energy], -80f);
                if (!isChild && jobType > 0 && _careerHost is not null)
                {
                    // Gentle career motive adjustment scaled for 60-tick interval.
                    var hungerDelta = (_careerHost.JobData(jobType, jobLevel, 14) ?? 0) / 60f;
                    var socialDelta = (_careerHost.JobData(jobType, jobLevel, 19) ?? 0) / 60f;
                    sim.Motives[(int)Motive.Hunger] = Math.Clamp(sim.Motives[(int)Motive.Hunger] + hungerDelta, -100f, 100f);
                    sim.Motives[(int)Motive.Social] = Math.Clamp(sim.Motives[(int)Motive.Social] + socialDelta, -100f, 100f);
                }
            }

            sim.DirtyMotives = true;
        }
    }

    private void ApplyAutonomyStep(InactiveLotState lot)
    {
        foreach (var sim in lot.Sims)
        {
            if (sim.IsAtWorkOrSchool)
                continue;

            var gender = sim.PersonData[(int)SimsEngine.Simulation.PersonData.Gender];
            var isPet = (gender & 6) != 0;

            var asleep = sim.Motives[(int)Motive.SleepState] < 0f;
            if (asleep)
            {
                if (sim.Motives[(int)Motive.Energy] >= 85f)
                {
                    sim.Motives[(int)Motive.SleepState] = 0f;
                    sim.ActivitySummary = "Relaxing";
                }
                else
                {
                    sim.ActivitySummary = "Sleeping";
                }
                ClampAllMotives(sim);
                sim.DirtyMotives = true;
                continue;
            }

            if (sim.Motives[(int)Motive.Hunger] < -25f)
            {
                if (isPet)
                {
                    sim.ActivitySummary = "Eating";
                    sim.Motives[(int)Motive.Hunger] += 35f;
                    sim.Motives[(int)Motive.Bladder] -= 8f;
                }
                else
                {
                    sim.ActivitySummary = "Cooking";
                    sim.Motives[(int)Motive.Hunger] += 35f;
                    sim.Motives[(int)Motive.Bladder] -= 8f;
                    IncrementSkill(sim, SimsEngine.Simulation.PersonData.CookingHouseBrokenSkill, 2);
                }
            }
            else if (sim.Motives[(int)Motive.Bladder] < -35f)
            {
                sim.ActivitySummary = isPet ? "Relieving self" : "Using toilet";
                sim.Motives[(int)Motive.Bladder] += 55f;
            }
            else if (sim.Motives[(int)Motive.Hygiene] < -35f)
            {
                sim.ActivitySummary = isPet ? "Grooming" : "Showering";
                sim.Motives[(int)Motive.Hygiene] += 55f;
            }
            else if (sim.Motives[(int)Motive.Energy] < -40f)
            {
                sim.ActivitySummary = "Sleeping";
                sim.Motives[(int)Motive.SleepState] = -1f;
            }
            else if (lot.Sims.Count > 1 && sim.Motives[(int)Motive.Social] < -15f)
            {
                sim.ActivitySummary = "Socializing";
                sim.Motives[(int)Motive.Social] += 25f;
                ProgressHouseholdRelationship(lot, sim);
            }
            else if (isPet)
            {
                sim.ActivitySummary = "Playing";
                sim.Motives[(int)Motive.Fun] += 25f;
                if (lot.Sims.Count > 1)
                    ProgressHouseholdRelationship(lot, sim);
            }
            else
            {
                var active = sim.PersonData[(int)SimsEngine.Simulation.PersonData.ActivePlayfulPersonality];
                var playful = sim.PersonData[(int)SimsEngine.Simulation.PersonData.PlayfulSmartPersonality];
                var neat = sim.PersonData[(int)SimsEngine.Simulation.PersonData.NeatQuietPersonality];
                if (active >= 600)
                {
                    sim.ActivitySummary = "Exercising";
                    sim.Motives[(int)Motive.Fun] += 20f;
                    IncrementSkill(sim, SimsEngine.Simulation.PersonData.BodyFurnitureSkill, 3);
                }
                else if (playful >= 500)
                {
                    sim.ActivitySummary = "Playing chess";
                    sim.Motives[(int)Motive.Fun] += 25f;
                    IncrementSkill(sim, SimsEngine.Simulation.PersonData.LogicSkill, 3);
                }
                else if (neat >= 600)
                {
                    sim.ActivitySummary = "Cleaning";
                    sim.Motives[(int)Motive.Room] += 15f;
                    IncrementSkill(sim, SimsEngine.Simulation.PersonData.CleaningSkill, 3);
                }
                else
                {
                    sim.ActivitySummary = "Painting";
                    sim.Motives[(int)Motive.Fun] += 25f;
                    IncrementSkill(sim, SimsEngine.Simulation.PersonData.CreativitySkill, 3);
                }

                if (lot.Sims.Count > 1)
                    ProgressHouseholdRelationship(lot, sim);
            }

            ClampAllMotives(sim);
            sim.DirtyMotives = true;
        }
    }

    private static void ProgressHouseholdRelationship(InactiveLotState lot, InactiveSimState sim)
    {
        var partner = lot.Sims.FirstOrDefault(other => other.NeighborId != sim.NeighborId);
        if (partner is null) return;

        UpdateDirectedRelationship(sim, partner.NeighborId, dailyDelta: 3, lifetimeDelta: 1);
        UpdateDirectedRelationship(partner, sim.NeighborId, dailyDelta: 3, lifetimeDelta: 1);
    }

    private static void UpdateDirectedRelationship(InactiveSimState sim, short targetId, int dailyDelta, int lifetimeDelta)
    {
        if (!sim.Relationships.TryGetValue(targetId, out var values) || values.Length < 2)
        {
            var next = new int[Math.Max(2, values?.Length ?? 2)];
            if (values is not null)
                Array.Copy(values, next, values.Length);
            values = next;
            sim.Relationships[targetId] = values;
        }

        values[0] = Math.Clamp(values[0] + dailyDelta, -100, 100);
        values[1] = Math.Clamp(values[1] + lifetimeDelta, -100, 100);
        sim.DirtyRelationships = true;
        sim.DirtyRelationshipsForNbrs = true;
    }

    private static void IncrementSkill(InactiveSimState sim, SimsEngine.Simulation.PersonData skillField, short delta)
    {
        var idx = (int)skillField;
        sim.PersonData[idx] = (short)Math.Clamp(sim.PersonData[idx] + delta, 0, 1000);
        sim.DirtyPersonData = true;
        sim.DirtyPersonDataForNbrs = true;
    }

    private static void ClampAllMotives(InactiveSimState sim)
    {
        for (var i = 0; i < sim.Motives.Length; i++)
        {
            var val = float.IsFinite(sim.Motives[i]) ? sim.Motives[i] : 0f;
            sim.Motives[i] = Math.Clamp(val, -100f, 100f);
            if (i < sim.SavedValues7CC.Length)
                sim.SavedValues7CC[i] = sim.Motives[i];
        }
    }

    private void RecordEvent(string text)
    {
        if (_recentEvents.Count >= 32)
            _recentEvents.RemoveAt(0);
        _recentEvents.Add(text);
    }

    private static List<InactiveSimState> SeedSimsFromLotAndNeighbors(
        short house,
        IffFile lotFile,
        Family family,
        Dictionary<uint, Neighbor> neighborByGuid,
        Func<uint, Objd?>? findDefinition,
        NeighborhoodManager? manager)
    {
        var result = new List<InactiveSimState>();
        var seenGuids = new HashSet<uint>();
        var memberGuids = family.Members.Where(static g => g != 0).ToHashSet();

        if (lotFile.OfType(ObjtType).FirstOrDefault() is { } objtChunk &&
            lotFile.OfType(ObjmType).FirstOrDefault() is { } objmChunk)
        {
            try
            {
                var types = ObjectTypeTable.Read(lotFile, objtChunk);
                var guidByTypeIndex = types.Entries.ToDictionary(static e => e.TypeIndex, static e => e.Guid);
                var objects = LotObjects.Read(lotFile, objmChunk, types, findDefinition, decodeUnknownAsObject: true);

                foreach (var obj in objects.Objects)
                {
                    if (obj.Decoded?.Person is not { } personState) continue;
                    if (!guidByTypeIndex.TryGetValue(obj.TypeIndex, out var guid) || guid == 0) continue;
                    if (!memberGuids.Contains(guid) || !seenGuids.Add(guid)) continue;
                    if (manager?.GetSimLocationByGuid(guid) is { CurrentLotId: > 0 } tracked && tracked.CurrentLotId != house)
                        continue;

                    var pdata = new short[256];
                    Array.Copy(personState.PersonData, pdata, Math.Min(pdata.Length, personState.PersonData.Length));
                    var motives = new float[16];
                    Array.Copy(personState.Values0x78C, motives, Math.Min(motives.Length, personState.Values0x78C.Length));
                    var saved7CC = new float[16];
                    Array.Copy(personState.Values0x7CC, saved7CC, Math.Min(saved7CC.Length, personState.Values0x7CC.Length));

                    var rels = new Dictionary<int, int[]>();
                    foreach (var r in obj.Decoded.Relationships)
                    {
                        if (r is not null)
                            rels[r.Value0x0C] = r.Values.ToArray();
                    }

                    var nid = pdata[(int)SimsEngine.Simulation.PersonData.NeighborId];
                    var name = $"Sim {nid}";
                    if (neighborByGuid.TryGetValue(guid, out var nbr))
                    {
                        if (nid <= 0) nid = nbr.NeighborId;
                        if (!string.IsNullOrWhiteSpace(nbr.Name)) name = nbr.Name;
                        if (nbr.RecordVersion > 0 && nbr.PersonData is { Count: > 0 })
                            ApplyPersistentFields(nbr.PersonData, pdata);
                        if (nbr.Relationships.Count > 0)
                        {
                            foreach (var (targetKey, vals) in nbr.Relationships)
                                rels[targetKey] = vals.ToArray();
                        }
                    }

                    if (nid <= 0) continue;

                    var isAtWorkOrSchool = pdata[(int)SimsEngine.Simulation.PersonData.IsPersonAtWorkSchool] != 0;
                    result.Add(new InactiveSimState
                    {
                        NeighborId = nid,
                        Guid = guid,
                        Name = name,
                        FamilyId = unchecked((short)family.FamilyNumber),
                        Motives = motives,
                        SavedValues7CC = saved7CC,
                        PersonData = pdata,
                        Relationships = rels,
                        IsAtWorkOrSchool = isAtWorkOrSchool,
                        ActivitySummary = isAtWorkOrSchool
                            ? (PersonAgeCategory.IsChild(pdata[(int)SimsEngine.Simulation.PersonData.PersonAge]) ? "At school" : "At work")
                            : "Relaxing",
                    });
                }
            }
            catch
            {
                // Fall back to NBRS below if an ObjM chunk cannot be parsed cleanly.
            }
        }

        foreach (var guid in family.Members)
        {
            if (guid == 0 || !seenGuids.Add(guid)) continue;
            if (manager?.GetSimLocationByGuid(guid) is { CurrentLotId: > 0 } tracked && tracked.CurrentLotId != house)
                continue;
            if (!neighborByGuid.TryGetValue(guid, out var nbr) || nbr.NeighborId <= 0) continue;

            var pdata = new short[256];
            if (nbr.PersonData is { Count: > 0 })
            {
                var len = Math.Min(pdata.Length, nbr.PersonData.Count);
                for (var i = 0; i < len; i++)
                    pdata[i] = nbr.PersonData[i];
            }
            pdata[(int)SimsEngine.Simulation.PersonData.NeighborId] = nbr.NeighborId;
            pdata[(int)SimsEngine.Simulation.PersonData.FamilyNumber] = unchecked((short)family.FamilyNumber);

            var motives = new float[16];
            motives[(int)Motive.Energy] = 60f;
            motives[(int)Motive.Comfort] = 50f;
            motives[(int)Motive.Hunger] = 40f;
            motives[(int)Motive.Hygiene] = 50f;
            motives[(int)Motive.Bladder] = 50f;
            motives[(int)Motive.Social] = 40f;
            motives[(int)Motive.Fun] = 40f;
            motives[(int)Motive.Room] = 20f;
            motives[(int)Motive.Mood] = 45f;
            var saved7CC = motives.ToArray();

            var rels = new Dictionary<int, int[]>();
            foreach (var (targetKey, vals) in nbr.Relationships)
                rels[targetKey] = vals.ToArray();

            var isAtWorkOrSchool = pdata[(int)SimsEngine.Simulation.PersonData.IsPersonAtWorkSchool] != 0;
            result.Add(new InactiveSimState
            {
                NeighborId = nbr.NeighborId,
                Guid = guid,
                Name = !string.IsNullOrWhiteSpace(nbr.Name) ? nbr.Name : $"Sim {nbr.NeighborId}",
                FamilyId = unchecked((short)family.FamilyNumber),
                Motives = motives,
                SavedValues7CC = saved7CC,
                PersonData = pdata,
                Relationships = rels,
                IsAtWorkOrSchool = isAtWorkOrSchool,
                ActivitySummary = isAtWorkOrSchool
                    ? (PersonAgeCategory.IsChild(pdata[(int)SimsEngine.Simulation.PersonData.PersonAge]) ? "At school" : "At work")
                    : "Relaxing",
            });
        }

        return result;
    }

    private static void ApplyPersistentFields(IReadOnlyList<short> source, short[] destination)
    {
        foreach (var field in PersonRecordFieldsVersion10)
        {
            if (field < source.Count && field < destination.Length &&
                field != (int)SimsEngine.Simulation.PersonData.FamilyNumber)
            {
                destination[field] = source[field];
            }
        }
    }

    private static Dictionary<short, string> ReadLotZoning(string userDataPath)
    {
        var result = new Dictionary<short, string>();
        if (string.IsNullOrWhiteSpace(userDataPath)) return result;
        var path = Path.Combine(userDataPath, "LotZoning.iff");
        if (!File.Exists(path))
            path = Path.Combine(userDataPath, "UserData", "LotZoning.iff");
        if (!File.Exists(path)) return result;

        try
        {
            var iff = IffFile.Open(path);
            var chunk = iff.Find(ResourceType.Str, 1);
            if (chunk is null) return result;
            var rows = StringTable.Read(iff, chunk).ForLanguage(1);
            for (var i = 0; i < rows.Count; i++)
            {
                var parts = rows[i].Split(',', 2);
                if (parts.Length != 2) continue;
                if (!short.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) || h <= 0)
                    continue;
                result[h] = parts[1].Trim();
            }
        }
        catch
        {
        }
        return result;
    }

    private static bool IsCommunityOrVacation(short house, Dictionary<short, string> zoningByHouse)
    {
        if (house is (>= 20 and <= 31) or (>= 40 and <= 49) or (>= 81 and <= 89) or (>= 93 and <= 99))
            return true;
        return zoningByHouse.TryGetValue(house, out var zoning) &&
               zoning.Equals("community", StringComparison.OrdinalIgnoreCase);
    }
}
