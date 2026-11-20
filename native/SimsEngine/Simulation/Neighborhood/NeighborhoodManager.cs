using System.Globalization;
using System.Text.Json.Serialization;
using SimsEngine.Formats;

namespace SimsEngine.Simulation.Neighborhood;

public sealed class SimNeighborhoodLocation
{
    public short SimId { get; set; }
    public uint Guid { get; set; }
    public string Name { get; set; } = string.Empty;
    public short FamilyId { get; set; }
    public int HomeLotId { get; set; }
    public int CurrentLotId { get; set; }
    public short? LotObjectId { get; set; }
    public string ActivitySummary { get; set; } = "Idle";
    public bool IsAway { get; set; }
    public bool IsVisiting { get; set; }
    public bool IsAtWorkOrSchool { get; set; }

    [JsonIgnore]
    public int LotId => CurrentLotId;

    [JsonIgnore]
    public bool IsAtHome => CurrentLotId > 0 && CurrentLotId == HomeLotId && !IsAway;

    public SimNeighborhoodLocation Clone() => new()
    {
        SimId = SimId,
        Guid = Guid,
        Name = Name,
        FamilyId = FamilyId,
        HomeLotId = HomeLotId,
        CurrentLotId = CurrentLotId,
        LotObjectId = LotObjectId,
        ActivitySummary = ActivitySummary,
        IsAway = IsAway,
        IsVisiting = IsVisiting,
        IsAtWorkOrSchool = IsAtWorkOrSchool,
    };
}

public sealed class LotSummary
{
    public short House { get; set; }
    public short FamilyId { get; set; }
    public string? FamilyName { get; set; }
    public string? LotName { get; set; }
    public string Zoning { get; set; } = "residential";
    public List<short> ResidentSimIds { get; set; } = [];
    public List<short> VisitorSimIds { get; set; } = [];

    [JsonIgnore]
    public bool IsOccupied => FamilyId > 0 || ResidentSimIds.Count > 0;

    [JsonIgnore]
    public bool IsCommunity => string.Equals(Zoning, "community", StringComparison.OrdinalIgnoreCase);

    public LotSummary Clone() => new()
    {
        House = House,
        FamilyId = FamilyId,
        FamilyName = FamilyName,
        LotName = LotName,
        Zoning = Zoning,
        ResidentSimIds = [.. ResidentSimIds],
        VisitorSimIds = [.. VisitorSimIds],
    };
}

public sealed class NeighborhoodSnapshot
{
    public int Version { get; set; } = 1;
    public List<SimNeighborhoodLocation> Sims { get; set; } = [];
    public List<LotSummary> Lots { get; set; } = [];
}

public sealed class NeighborhoodManager
{
    private static readonly ResourceType FamiType = ResourceType.Parse("FAMI");
    private static readonly ResourceType FamsType = ResourceType.Parse("FAMs");
    private static readonly ResourceType NbrsType = ResourceType.Parse("NBRS");

    private readonly Dictionary<short, SimNeighborhoodLocation> _simLocations = new();
    private readonly Dictionary<uint, short> _guidToSimId = new();
    private readonly Dictionary<short, LotSummary> _lots = new();

    public bool TrackingEnabled { get; set; }

    public void UpdateSimLocation(short simId, int lotId)
    {
        if (simId <= 0 || lotId <= 0 || lotId > short.MaxValue) return;
        var house = (short)lotId;
        var lotSummary = EnsureLotSummary(house, defaultZoning: DefaultZoningForHouse(lotId));
        if (!_simLocations.TryGetValue(simId, out var entry))
        {
            entry = new SimNeighborhoodLocation
            {
                SimId = simId,
                Name = $"Sim {simId}",
                HomeLotId = lotSummary.IsCommunity ? 0 : lotId,
            };
            _simLocations[simId] = entry;
        }

        var oldLotId = entry.CurrentLotId;
        if (oldLotId != lotId)
            entry.LotObjectId = null;
        entry.CurrentLotId = lotId;
        RecomputeLocationFlags(entry);

        RebuildLotMembershipLists();
    }

    public int? GetSimLocation(short simId) =>
        _simLocations.TryGetValue(simId, out var entry) ? entry.CurrentLotId : null;

    public SimNeighborhoodLocation? GetSimDetails(short simId) =>
        _simLocations.TryGetValue(simId, out var entry) ? entry : null;

    public SimNeighborhoodLocation? GetSimLocationByGuid(uint guid) =>
        guid != 0 && _guidToSimId.TryGetValue(guid, out var simId) && _simLocations.TryGetValue(simId, out var entry)
            ? entry
            : null;

    public SimNeighborhoodLocation? GetSimLocationByLotObjectId(int lotId, short lotObjectId)
    {
        if (lotId <= 0 || lotObjectId <= 0) return null;
        foreach (var entry in _simLocations.Values)
        {
            if (entry.CurrentLotId == lotId && entry.LotObjectId == lotObjectId)
                return entry;
        }
        return null;
    }

    public IReadOnlyDictionary<short, SimNeighborhoodLocation> GetAllSimLocations() => _simLocations;

    public void RegisterLotMetadata(short house, LotSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (house <= 0) return;
        var copy = summary.Clone();
        copy.House = house;
        if (string.IsNullOrWhiteSpace(copy.Zoning))
            copy.Zoning = DefaultZoningForHouse(house);
        _lots[house] = copy;
        RebuildLotMembershipLists();
    }

    public LotSummary? GetLotSummary(short house) =>
        _lots.TryGetValue(house, out var summary) ? summary : null;

    public IReadOnlyDictionary<short, LotSummary> GetAllLotSummaries() => _lots;

    public void SyncFromUserData(string userDataPath, byte language = 1, bool resetLocations = false)
    {
        if (string.IsNullOrWhiteSpace(userDataPath) || !Directory.Exists(userDataPath))
            return;

        var nghPath = Path.Combine(userDataPath, "Neighborhood.iff");
        if (!File.Exists(nghPath))
        {
            var nested = Path.Combine(userDataPath, "UserData", "Neighborhood.iff");
            if (File.Exists(nested))
                nghPath = nested;
            else
                return;
        }

        var ngh = IffFile.Open(nghPath);
        var lotZoningPath = Path.Combine(userDataPath, "LotZoning.iff");
        if (!File.Exists(lotZoningPath))
            lotZoningPath = Path.Combine(userDataPath, "UserData", "LotZoning.iff");
        IffFile? lotZoning = File.Exists(lotZoningPath) ? IffFile.Open(lotZoningPath) : null;

        IffFile? lotDescriptions = null;
        var descCandidates = new[]
        {
            Path.Combine(userDataPath, "Houses", "NeighborhoodDesc.iff"),
            Path.Combine(userDataPath, "NeighborhoodDesc.iff"),
            Path.Combine(userDataPath, "Desc.iff"),
        };
        foreach (var candidate in descCandidates)
        {
            if (File.Exists(candidate))
            {
                lotDescriptions = IffFile.Open(candidate);
                break;
            }
        }

        SyncFromNeighborhood(ngh, lotZoning, lotDescriptions, language, resetLocations);

        // Ensure every existing HouseXX.iff on the unified Sim Lane + Old Town map (1..10, 50..80)
        // has a summary entry even if omitted from NeighborhoodDesc or LotZoning.
        var housesDir = Path.Combine(userDataPath, "Houses");
        if (!Directory.Exists(housesDir))
            housesDir = Path.Combine(userDataPath, "UserData", "Houses");
        if (Directory.Exists(housesDir))
        {
            foreach (var h in NeighborhoodWorldBlueprint.Default.Parcels.Keys)
            {
                if (_lots.ContainsKey(h)) continue;
                var houseFile = Path.Combine(housesDir, $"House{h:D2}.iff");
                if (File.Exists(houseFile))
                    EnsureLotSummary(h, DefaultZoningForHouse(h));
            }
        }
    }

    public void SyncFromNeighborhood(IffFile neighborhood, IffFile? lotZoning = null, IffFile? lotDescriptions = null,
        byte language = 1, bool resetLocations = false)
    {
        ArgumentNullException.ThrowIfNull(neighborhood);

        var zoningByHouse = ReadLotZoning(lotZoning);
        foreach (var (zonedHouse, zoning) in zoningByHouse)
        {
            var summary = EnsureLotSummary(zonedHouse, zoning);
            summary.Zoning = zoning;
        }

        if (lotDescriptions is not null)
        {
            foreach (var strChunk in lotDescriptions.OfType(ResourceType.Str))
            {
                if (strChunk.Id <= 2000) continue;
                var descHouse = checked((short)(strChunk.Id - 2000));
                if (descHouse <= 0) continue;
                var table = StringTable.Read(lotDescriptions, strChunk);
                var lotName = table.GetString(0, language);
                if (!string.IsNullOrWhiteSpace(lotName))
                {
                    var summary = EnsureLotSummary(descHouse, DefaultZoningForHouse(descHouse));
                    summary.LotName = lotName;
                }
            }
        }

        var familyNames = new Dictionary<ushort, string>();
        foreach (var chunk in neighborhood.OfType(FamsType))
        {
            var text = StringTable.Read(neighborhood, chunk).GetString(0, language);
            if (!string.IsNullOrWhiteSpace(text))
                familyNames[chunk.Id] = text;
        }

        var guidToFamily = new Dictionary<uint, (short FamilyId, int HomeLotId, string? FamilyName)>();
        var familyNumberToFamily = new Dictionary<int, (short FamilyId, int HomeLotId, string? FamilyName)>();

        foreach (var chunk in neighborhood.OfType(FamiType))
        {
            if (chunk.Id == 0) continue;
            var family = Family.Read(neighborhood, chunk);
            var familyId = unchecked((short)chunk.Id);
            var familyName = !string.IsNullOrWhiteSpace(family.Name)
                ? family.Name
                : familyNames.GetValueOrDefault(chunk.Id);

            if (family.FamilyNumber > 0)
                familyNumberToFamily[family.FamilyNumber] = (familyId, family.HouseNumber, familyName);

            if (family.HouseNumber > 0 && family.HouseNumber <= short.MaxValue)
            {
                var h = (short)family.HouseNumber;
                var zoning = zoningByHouse.GetValueOrDefault(h) ?? DefaultZoningForHouse(h);
                var summary = EnsureLotSummary(h, zoning);
                summary.FamilyId = familyId;
                if (!string.IsNullOrWhiteSpace(familyName))
                    summary.FamilyName = familyName;
            }

            foreach (var memberGuid in family.Members)
            {
                if (memberGuid != 0)
                    guidToFamily[memberGuid] = (familyId, family.HouseNumber, familyName);
            }
        }

        var seenNeighborIds = resetLocations ? new HashSet<short>() : null;
        if (neighborhood.OfType(NbrsType).FirstOrDefault() is { } nbrsChunk)
        {
            var neighbors = Neighbors.ReadRecoveringSupportedPrefix(neighborhood, nbrsChunk);
            foreach (var neighbor in neighbors.Entries)
            {
                if (neighbor.NeighborId <= 0) continue;
                seenNeighborIds?.Add(neighbor.NeighborId);

                (short FamilyId, int HomeLotId, string? FamilyName) famInfo = default;
                var hasFamily = neighbor.Guid != 0 && guidToFamily.TryGetValue(neighbor.Guid, out famInfo);
                if (!hasFamily && neighbor.PersonData is { Count: > (int)PersonData.FamilyNumber } pdata)
                {
                    var pFamilyNum = pdata[(int)PersonData.FamilyNumber];
                    if (pFamilyNum > 0 && familyNumberToFamily.TryGetValue(pFamilyNum, out famInfo))
                        hasFamily = true;
                }

                var familyId = hasFamily ? famInfo.FamilyId : (short)0;
                var homeLotId = hasFamily ? Math.Max(0, famInfo.HomeLotId) : 0;

                if (!_simLocations.TryGetValue(neighbor.NeighborId, out var entry))
                {
                    entry = new SimNeighborhoodLocation
                    {
                        SimId = neighbor.NeighborId,
                        CurrentLotId = homeLotId,
                    };
                    _simLocations[neighbor.NeighborId] = entry;
                }
                else if (resetLocations || entry.CurrentLotId <= 0)
                {
                    var oldLotId = entry.CurrentLotId;
                    entry.CurrentLotId = homeLotId;
                    if (resetLocations || oldLotId != entry.CurrentLotId)
                    {
                        entry.LotObjectId = null;
                        if (resetLocations || entry.ActivitySummary is "In transit" or "Visiting")
                            entry.ActivitySummary = "Idle";
                    }
                }

                entry.Guid = neighbor.Guid;
                if (neighbor.Guid != 0)
                    _guidToSimId[neighbor.Guid] = neighbor.NeighborId;

                if (!string.IsNullOrWhiteSpace(neighbor.Name) &&
                    (string.IsNullOrWhiteSpace(entry.Name) || !IsInternalUserLabel(neighbor.Name) || IsInternalUserLabel(entry.Name)))
                {
                    entry.Name = neighbor.Name;
                }
                else if (string.IsNullOrWhiteSpace(entry.Name))
                {
                    entry.Name = $"Sim {neighbor.NeighborId}";
                }

                entry.FamilyId = familyId;
                entry.HomeLotId = homeLotId;

                if (neighbor.PersonData is { Count: > (int)PersonData.IsPersonAtWorkSchool } pd)
                {
                    entry.IsAtWorkOrSchool = pd[(int)PersonData.IsPersonAtWorkSchool] != 0;
                    if (entry.IsAtWorkOrSchool && string.Equals(entry.ActivitySummary, "Idle", StringComparison.Ordinal))
                        entry.ActivitySummary = ResolveWorkOrSchoolLabel(pd[(int)PersonData.PersonAge]);
                }

                RecomputeLocationFlags(entry);
            }
        }

        if (resetLocations)
        {
            foreach (var entry in _simLocations.Values)
            {
                if (seenNeighborIds is not null && seenNeighborIds.Contains(entry.SimId))
                    continue;
                entry.LotObjectId = null;
                entry.CurrentLotId = entry.HomeLotId;
                if (entry.ActivitySummary is "In transit" or "Visiting")
                    entry.ActivitySummary = "Idle";
                RecomputeLocationFlags(entry);
            }
        }

        RebuildLotMembershipLists();
    }

    public void SyncFromWorld(
        SimulationWorld world,
        short house,
        bool preserveOtherLotLocations = false,
        IReadOnlySet<int>? activeLotIds = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (house <= 0) return;

        var zoning = world.StateClass(house) == 2 ? "community" : DefaultZoningForHouse(house);
        var lotSummary = EnsureLotSummary(house, zoning);
        lotSummary.Zoning = zoning;

        if (world.NeighborhoodFamilies is { ActiveFamilyNumber: > 0 } famAccess &&
            famAccess.HouseNumber(famAccess.ActiveFamilyNumber) == house &&
            lotSummary.FamilyId == 0)
        {
            lotSummary.FamilyId = famAccess.ActiveFamilyNumber;
        }

        var presentSimIds = new HashSet<short>();
        foreach (var person in world.WalkOrder.OfType<XPerson>())
        {
            var simId = person[PersonData.NeighborId];
            if (simId <= 0) continue;
            presentSimIds.Add(simId);

            if (_simLocations.TryGetValue(simId, out var existing) &&
                existing.CurrentLotId > 0 &&
                existing.CurrentLotId != house &&
                ShouldPreserveExistingLotLocation(existing, preserveOtherLotLocations, activeLotIds))
            {
                continue;
            }

            UpsertFromPerson(world, house, person);
        }

        foreach (var entry in _simLocations.Values)
        {
            if (entry.CurrentLotId != house || presentSimIds.Contains(entry.SimId))
                continue;

            entry.LotObjectId = null;
            if (entry.HomeLotId > 0 && entry.HomeLotId != house)
            {
                entry.CurrentLotId = entry.HomeLotId;
                entry.IsVisiting = false;
                entry.IsAway = entry.IsAtWorkOrSchool;
                if (entry.ActivitySummary is "In transit" or "Visiting")
                    entry.ActivitySummary = "Idle";
            }
            else if (entry.HomeLotId == 0)
            {
                entry.CurrentLotId = 0;
                entry.IsVisiting = false;
                entry.IsAway = false;
            }
            RecomputeLocationFlags(entry);
        }

        RebuildLotMembershipLists();
    }

    private static bool ShouldPreserveExistingLotLocation(
        SimNeighborhoodLocation existing,
        bool preserveOtherLotLocations,
        IReadOnlySet<int>? activeLotIds)
    {
        if (preserveOtherLotLocations)
            return true;

        if (activeLotIds is not null && activeLotIds.Contains(existing.CurrentLotId))
            return true;

        return existing.LotObjectId is not null ||
               existing.IsVisiting ||
               string.Equals(existing.ActivitySummary, "In transit", StringComparison.Ordinal);
    }

    public void OnPersonInstantiated(SimulationWorld world, XPerson person)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(person);

        var simId = person[PersonData.NeighborId];
        if (simId <= 0 || world.ActiveLotNumber <= 0)
            return;

        var house = checked((short)world.ActiveLotNumber);
        UpsertFromPerson(world, house, person);
        RebuildLotMembershipLists();
    }

    public void OnPersonRemoved(SimulationWorld world, XPerson person)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(person);

        var simId = person[PersonData.NeighborId];
        if (simId <= 0) return;

        if (!_simLocations.TryGetValue(simId, out var entry))
            return;

        if (world.ActiveLotNumber <= 0 || entry.CurrentLotId != world.ActiveLotNumber)
            return;

        entry.LotObjectId = null;
        if (entry.HomeLotId > 0 && entry.HomeLotId != world.ActiveLotNumber)
        {
            entry.CurrentLotId = entry.HomeLotId;
        }
        else if (entry.HomeLotId == 0)
        {
            entry.CurrentLotId = 0;
        }
        if (entry.ActivitySummary is "In transit" or "Visiting")
            entry.ActivitySummary = "Idle";
        RecomputeLocationFlags(entry);
        RebuildLotMembershipLists();
    }

    public void OnSimLotSwitched(short simId, short fromHouse, short toHouse, bool isVisiting, short? lotObjectId = null)
    {
        if (simId <= 0) return;
        if (!_simLocations.TryGetValue(simId, out var entry))
        {
            entry = new SimNeighborhoodLocation
            {
                SimId = simId,
                Name = $"Sim {simId}",
                HomeLotId = fromHouse > 0 ? fromHouse : 0,
            };
            _simLocations[simId] = entry;
        }

        if (entry.HomeLotId == 0 && fromHouse > 0 && isVisiting)
            entry.HomeLotId = fromHouse;

        entry.CurrentLotId = toHouse;
        entry.LotObjectId = lotObjectId;
        entry.IsVisiting = isVisiting;
        entry.IsAway = (entry.HomeLotId > 0 && toHouse != entry.HomeLotId) || isVisiting || entry.IsAtWorkOrSchool;
        entry.ActivitySummary = "In transit";

        if (fromHouse > 0 && !_lots.ContainsKey(fromHouse))
            EnsureLotSummary(fromHouse, DefaultZoningForHouse(fromHouse));
        if (toHouse > 0 && !_lots.ContainsKey(toHouse))
            EnsureLotSummary(toHouse, DefaultZoningForHouse(toHouse));

        RebuildLotMembershipLists();
    }

    public NeighborhoodSnapshot CaptureSnapshot() => new()
    {
        Version = 1,
        Sims = _simLocations.Values.OrderBy(s => s.SimId).Select(s => s.Clone()).ToList(),
        Lots = _lots.Values.OrderBy(l => l.House).Select(l => l.Clone()).ToList(),
    };

    public void RestoreSnapshot(NeighborhoodSnapshot? snapshot)
    {
        _simLocations.Clear();
        _guidToSimId.Clear();
        _lots.Clear();
        if (snapshot is null) return;

        foreach (var lot in snapshot.Lots)
        {
            if (lot.House <= 0) continue;
            _lots[lot.House] = lot.Clone();
        }

        foreach (var sim in snapshot.Sims)
        {
            if (sim.SimId <= 0) continue;
            var copy = sim.Clone();
            _simLocations[copy.SimId] = copy;
            if (copy.Guid != 0)
                _guidToSimId[copy.Guid] = copy.SimId;
        }

        RebuildLotMembershipLists();
    }

    private void UpsertFromPerson(SimulationWorld world, short house, XPerson person)
    {
        var simId = person[PersonData.NeighborId];
        if (simId <= 0) return;

        var guid = unchecked((uint)(person.TypeRecord?.Key ?? 0));
        if (guid == 0 && world.Neighbors.TryGetValue(simId, out var neighborRecord))
            guid = unchecked((uint)neighborRecord.TypeRecordKey);

        var rawName = !string.IsNullOrWhiteSpace(person.Name)
            ? person.Name
            : world.Neighbors.TryGetValue(simId, out var n) && !string.IsNullOrWhiteSpace(n.Name)
                ? n.Name
                : person.TypeRecord?.CatalogName;

        if (!_simLocations.TryGetValue(simId, out var entry))
        {
            entry = new SimNeighborhoodLocation
            {
                SimId = simId,
                HomeLotId = ResolveHomeLotFromWorld(world, house, person, guid),
            };
            _simLocations[simId] = entry;
        }

        if (guid != 0)
        {
            entry.Guid = guid;
            _guidToSimId[guid] = simId;
        }

        if (!string.IsNullOrWhiteSpace(rawName) &&
            (string.IsNullOrWhiteSpace(entry.Name) || !IsInternalUserLabel(rawName) || IsInternalUserLabel(entry.Name)))
        {
            entry.Name = rawName;
        }
        else if (string.IsNullOrWhiteSpace(entry.Name))
        {
            entry.Name = $"Sim {simId}";
        }

        if (entry.FamilyId == 0)
        {
            var pFam = person[PersonData.FamilyNumber];
            if (pFam > 0)
                entry.FamilyId = pFam;
        }
        if (entry.HomeLotId == 0)
            entry.HomeLotId = ResolveHomeLotFromWorld(world, house, person, guid);

        entry.CurrentLotId = house;
        entry.LotObjectId = person.Id;
        entry.IsAtWorkOrSchool = person[PersonData.IsPersonAtWorkSchool] != 0;
        entry.ActivitySummary = ResolvePersonActivity(person, entry.IsAtWorkOrSchool);
        RecomputeLocationFlags(entry);
        EnsureLotSummary(house, world.StateClass(house) == 2 ? "community" : DefaultZoningForHouse(house));
    }

    private static int ResolveHomeLotFromWorld(SimulationWorld world, short currentHouse, XPerson person, uint guid)
    {
        if (world.NeighborhoodFamilies is { } famAccess)
        {
            if (guid != 0)
            {
                var memberHouse = famAccess.HouseNumberForMember(unchecked((int)guid));
                if (memberHouse > 0)
                    return memberHouse;
            }
            var pFam = person[PersonData.FamilyNumber];
            if (pFam > 0)
            {
                var famHouse = famAccess.HouseNumber(pFam);
                if (famHouse > 0)
                    return famHouse;
            }
        }
        if (person[PersonData.FamilyNumber] > 0 && world.StateClass(currentHouse) != 2 && DefaultZoningForHouse(currentHouse) == "residential")
            return currentHouse;
        return 0;
    }

    private static string ResolvePersonActivity(XPerson person, bool isAtWorkOrSchool)
    {
        if (isAtWorkOrSchool)
            return ResolveWorkOrSchoolLabel(person[PersonData.PersonAge]);

        if (person.CurrentActionRecord is { } current)
        {
            if (!string.IsNullOrWhiteSpace(current.ActionText))
                return current.ActionText;
            var interactionName = current.Owner?.TypeRecord?.Interactions?.Interactions
                .FirstOrDefault(item => item.TtaIndex == current.RawDword10)?.Name;
            if (!string.IsNullOrWhiteSpace(interactionName))
                return interactionName;
        }

        return "Idle";
    }

    private static string ResolveWorkOrSchoolLabel(short personAge) =>
        PersonAgeCategory.IsChild(personAge) ? "At school" : "At work";

    private static void RecomputeLocationFlags(SimNeighborhoodLocation entry)
    {
        entry.IsVisiting = entry.CurrentLotId > 0 && entry.HomeLotId != entry.CurrentLotId && !entry.IsAtWorkOrSchool;
        entry.IsAway = entry.IsAtWorkOrSchool ||
                       (entry.CurrentLotId > 0 && entry.HomeLotId > 0 && entry.CurrentLotId != entry.HomeLotId);
    }

    private LotSummary EnsureLotSummary(short house, string defaultZoning)
    {
        if (!_lots.TryGetValue(house, out var summary))
        {
            summary = new LotSummary
            {
                House = house,
                Zoning = defaultZoning,
            };
            _lots[house] = summary;
        }
        else if (string.IsNullOrWhiteSpace(summary.Zoning))
        {
            summary.Zoning = defaultZoning;
        }
        return summary;
    }

    private void RebuildLotMembershipLists()
    {
        foreach (var lot in _lots.Values)
        {
            lot.ResidentSimIds.Clear();
            lot.VisitorSimIds.Clear();
        }

        foreach (var sim in _simLocations.Values)
        {
            if (sim.HomeLotId > 0 && sim.HomeLotId <= short.MaxValue &&
                _lots.TryGetValue((short)sim.HomeLotId, out var homeLot))
            {
                homeLot.ResidentSimIds.Add(sim.SimId);
            }

            if (sim.CurrentLotId > 0 && sim.CurrentLotId <= short.MaxValue &&
                sim.CurrentLotId != sim.HomeLotId &&
                _lots.TryGetValue((short)sim.CurrentLotId, out var currentLot))
            {
                currentLot.VisitorSimIds.Add(sim.SimId);
            }
        }

        foreach (var lot in _lots.Values)
        {
            lot.ResidentSimIds.Sort();
            lot.VisitorSimIds.Sort();
        }
    }

    private static Dictionary<short, string> ReadLotZoning(IffFile? lotZoning)
    {
        var result = new Dictionary<short, string>();
        if (lotZoning is null) return result;
        var chunk = lotZoning.Find(ResourceType.Str, 1);
        if (chunk is null) return result;

        var rows = StringTable.Read(lotZoning, chunk).ForLanguage(1);
        for (var i = 0; i < rows.Count; i++)
        {
            var parts = rows[i].Split(',', 2);
            if (parts.Length != 2) continue;
            if (!short.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var house) || house <= 0)
                continue;
            var zoning = parts[1].Trim();
            if (zoning.Equals("community", StringComparison.OrdinalIgnoreCase))
                result[house] = "community";
            else if (zoning.Equals("residential", StringComparison.OrdinalIgnoreCase))
                result[house] = "residential";
        }
        return result;
    }

    private static string DefaultZoningForHouse(int house) =>
        house is (>= 21 and <= 31) or (>= 40 and <= 49) or (>= 81 and <= 89) or (>= 93 and <= 99)
            ? "community"
            : "residential";

    private static bool IsInternalUserLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!name.StartsWith("User", StringComparison.OrdinalIgnoreCase) || name.Length <= 4)
            return false;
        for (var i = 4; i < name.Length; i++)
        {
            if (!char.IsAsciiDigit(name[i])) return false;
        }
        return true;
    }
}
