using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.Simulation.Neighborhood;

namespace SimsSimulation;

internal sealed partial class SimulationServer
{
    internal sealed record ConnectedNeighborhoodSaveLot(
        short House,
        ushort? FamilyChunkId,
        string LotState,
        SaveRuntimeMetadata Runtime,
        int Speed,
        bool Away,
        short HomeHouse,
        IReadOnlyList<int>? RestrictedActivePersonKeys,
        long LastTick,
        short SelectedSim,
        bool SaveDirty = false,
        ushort? CommittedVacationFamilyChunkId = null,
        int? CommittedVacationHouse = null,
        int ViewLevel = 1,
        short ViewerMode = 0,
        bool RoofsVisible = false,
        bool EditingView = false,
        bool DoorWindowBuildToolActive = false,
        int? CutawayTileX = null,
        int? CutawayTileY = null,
        IReadOnlyList<ushort>? CutawayRooms = null,
        int LastRunningSpeed = 1,
        int ClockTicks = 0,
        int ClockDay = 0);

    internal sealed record ConnectedNeighborhoodSaveState(
        ConnectedNeighborhoodOptions Options,
        short FocusedHouse,
        short HomeHouse,
        Dictionary<short, ConnectedNeighborhoodSaveLot> Lots,
        NeighborhoodSnapshot? Neighborhood = null,
        bool? FocusedAway = null,
        NeighborLotControllerSnapshot? StoryProgression = null,
        IReadOnlyList<short>? RemovedNeighbors = null,
        Dictionary<short, uint>? RemovedNeighborGuids = null);

    private const byte ConnectedKnockDoorInteractionId = 254;
    private const uint ConnectedPendingRemovalFlag = 0x1000u;

    private static readonly string[] ConnectedNeighborhoodCommandNames =
    [
        "setConnectedNeighborhood",
        "focusLot",
        "visitLot",
        "returnHome",
        "adjacentScenes",
    ];

    private sealed record AdjacentSceneCacheEntry(
        short House,
        int CameraOrientation,
        string WallMode,
        int ViewLevel,
        bool RoofsVisible,
        bool HideInteriorPeople,
        long Revision,
        bool HasDoor,
        string ScenePath,
        string AdjDir,
        SceneExporter.AnimationState AnimationState);

    private sealed class AdjacentPreviewWorld(IffFile lot, SimulationWorld world, int roadEdge)
    {
        public IffFile Lot { get; } = lot;
        public SimulationWorld World { get; } = world;
        public int RoadEdge { get; } = roadEdge;
        public ushort? ActiveFamilyChunkId { get; init; }
        public int ActiveFamilyField0x13C { get; init; }
        public long Revision { get; set; } = 1;
        public bool TickingPaused { get; set; }
    }

    private readonly Dictionary<short, AdjacentSceneCacheEntry> adjacentSceneCache = new();
    private readonly Dictionary<short, AdjacentPreviewWorld> adjacentPreviewWorlds = new();
    private readonly HashSet<short> invitedInsideHouses = new();
    private NeighborhoodWorldBlueprint? cachedWorldBlueprint;
    private long adjacentRenderVersion = 1;
    private int adjacentTickCounter;
    private int adjacentAnimFrameCounter;
    private long adjacentAnimationSequence;
    private string lastFocusedVisitorSignature = string.Empty;
    private int lastSharedPaperDeliveryDay = -1;

    private ConnectedNeighborhoodOptions connectedOptions = new();
    private readonly LotSessionPool lotPool = new();
    private readonly NeighborhoodManager neighborhoodManager = new();
    private readonly NeighborLotController neighborLotController = new();
    private readonly Dictionary<short, CrossLotTransitState> activeCrossLotTransits = new();
    private readonly Dictionary<short, uint> globallyRemovedNeighbors = new();
    private readonly List<(short SourceHouse, short NeighborId, short HomeHouse)> pendingAutonomousReturnHomes = new();
    private bool neighborControllerSeedDirty = true;
    private bool isClearingRemovedNeighbor;
    private IffFile? lastSeededNeighborhood;
    private int lastSeededSavedLotCount = -1;
    private int lastSeededLiveLotCount = -1;

    internal bool ConnectedNeighborhoodEnabled => connectedOptions.Enabled;
    internal ConnectedNeighborhoodOptions ConnectedOptions => connectedOptions;
    internal NeighborhoodManager NeighborhoodState => neighborhoodManager;
    internal LotSessionPool SessionPool => lotPool;
    internal NeighborLotController StoryProgressionController => neighborLotController;
    internal IReadOnlyDictionary<short, CrossLotTransitState> ActiveCrossLotTransits => activeCrossLotTransits;

    private void InitializeConnectedNeighborhoodFromPacks()
    {
        var packEnabled = plugins?.MigratedPacks.Any(pack => SaveCompatibility.OptionalFeaturePackIds.Contains(pack.Id)) == true;
        if (!packEnabled) return;

        connectedOptions.Enabled = true;
        connectedOptions.Normalize();
        neighborhoodManager.TrackingEnabled = true;

        var userData = Neighborhoods.UserData(profile);
        neighborhoodManager.SyncFromUserData(userData, language);
        ReconcileSuspendedHomeLotIntoPool();

        if (world is not null && lot is not null && house > 0)
        {
            world.Neighborhood = neighborhoodManager;
            AttachConnectedNeighborhoodHooks(world);
            SyncWorldPeopleRelationshipsFromNeighbors(world);
            neighborhoodManager.SyncFromWorld(world, house, activeLotIds: CollectActivePoolLotIds());
            SeedFocusedSessionIntoPool();
            ConfigureNeighborLotControllerFromWorld(world);
        }
        neighborControllerSeedDirty = true;
        EnsureNeighborLotControllerSeeded();
        if (world is not null && lot is not null && house > 0 && connectedOptions.EnableAdjacentLotRendering)
            PrewarmAdjacentNeighborhoodParallel();
    }

    private bool TryHandleConnectedNeighborhoodCommand(JsonElement request, string op, out object? result, out string? error)
    {
        result = null;
        error = null;
        switch (op)
        {
            case "setConnectedNeighborhood":
            {
                if (!request.TryGetProperty("enabled", out var enabledValue) ||
                    enabledValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new ArgumentException("setConnectedNeighborhood requires a boolean enabled value.");
                }

                var enabled = enabledValue.GetBoolean();
                bool? adjacentRendering = request.TryGetProperty("adjacentRendering", out var adjVal) &&
                                          adjVal.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? adjVal.GetBoolean()
                    : null;
                bool? storyProgression = request.TryGetProperty("storyProgression", out var spVal) &&
                                         spVal.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? spVal.GetBoolean()
                    : null;
                int? maxLiveSplitLots = request.TryGetProperty("maxLiveSplitLots", out var maxVal) &&
                                        maxVal.TryGetInt32(out var parsedMax)
                    ? parsedMax
                    : null;

                result = SetConnectedNeighborhoodMode(enabled, adjacentRendering, storyProgression, maxLiveSplitLots);
                return true;
            }
            case "focusLot":
            {
                if (!request.TryGetProperty("house", out var houseProp) || !houseProp.TryGetInt16(out var targetHouse) || targetHouse <= 0)
                    throw new ArgumentException("focusLot requires a positive integer house.");

                result = FocusLotInternal(targetHouse);
                return true;
            }
            case "visitLot":
            {
                if (!request.TryGetProperty("house", out var houseProp) || !houseProp.TryGetInt16(out var targetHouse) || targetHouse <= 0)
                    throw new ArgumentException("visitLot requires a positive integer house.");

                short? simId = null;
                if (request.TryGetProperty("simId", out var simProp) && simProp.ValueKind != JsonValueKind.Null)
                {
                    if (!simProp.TryGetInt16(out var parsedSim))
                        throw new ArgumentException("visitLot simId must be a valid integer neighborID.");
                    simId = parsedSim;
                }

                short[]? companionIds = null;
                if (request.TryGetProperty("companionIds", out var compProp) && compProp.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<short>();
                    foreach (var elem in compProp.EnumerateArray())
                    {
                        if (!elem.TryGetInt16(out var parsedComp))
                            throw new ArgumentException("visitLot companionIds entries must be valid integer neighborIDs.");
                        list.Add(parsedComp);
                    }
                    companionIds = list.ToArray();
                }

                var knock = request.TryGetProperty("knock", out var knockProp) &&
                            knockProp.ValueKind == JsonValueKind.True;
                var walk = request.TryGetProperty("walk", out var walkProp) &&
                           walkProp.ValueKind == JsonValueKind.True;
                int? targetTileX = request.TryGetProperty("targetTileX", out var txProp) && txProp.TryGetInt32(out var ptx) ? ptx : null;
                int? targetTileY = request.TryGetProperty("targetTileY", out var tyProp) && tyProp.TryGetInt32(out var pty) ? pty : null;

                result = VisitLotInternal(targetHouse, simId, companionIds, knock, walk, targetTileX, targetTileY);
                return true;
            }
            case "returnHome":
            {
                short? simId = null;
                if (request.TryGetProperty("simId", out var simProp) && simProp.ValueKind != JsonValueKind.Null)
                {
                    if (!simProp.TryGetInt16(out var parsedSim))
                        throw new ArgumentException("returnHome simId must be a valid integer neighborID.");
                    simId = parsedSim;
                }

                var walk = request.TryGetProperty("walk", out var walkProp) &&
                           walkProp.ValueKind == JsonValueKind.True;

                result = ReturnHomeInternal(simId, walk);
                return true;
            }
            case "adjacentScenes":
            {
                var force = request.TryGetProperty("force", out var forceProp) &&
                            forceProp.ValueKind == JsonValueKind.True;
                int? cameraTileX = request.TryGetProperty("cameraTileX", out var ctxProp) && ctxProp.TryGetInt32(out var pctx)
                    ? pctx
                    : null;
                int? cameraTileY = request.TryGetProperty("cameraTileY", out var ctyProp) && ctyProp.TryGetInt32(out var pcty)
                    ? pcty
                    : null;
                result = ExportAdjacentLotScenesPayload(force, cameraTileX, cameraTileY);
                return true;
            }
            default:
                return false;
        }
    }

    private object SetConnectedNeighborhoodMode(bool enabled, bool? adjacentRendering = null,
        bool? storyProgression = null, int? maxLiveSplitLots = null)
    {
        var wasEnabled = connectedOptions.Enabled;
        var wasVisitingAway = wasEnabled && world is not null && house > 0 && IsVisitingAwayLotValue();
        var capturedHomeHouse = wasVisitingAway ? EffectiveHomeHouseValue() : (short)0;
        connectedOptions.Enabled = enabled;
        if (adjacentRendering.HasValue)
            connectedOptions.EnableAdjacentLotRendering = adjacentRendering.Value;
        if (storyProgression.HasValue)
            connectedOptions.EnableStoryProgression = storyProgression.Value;
        if (maxLiveSplitLots.HasValue)
            connectedOptions.MaxLiveSplitLots = maxLiveSplitLots.Value;
        connectedOptions.Normalize();

        neighborhoodManager.TrackingEnabled = connectedOptions.Enabled;

        if (connectedOptions.Enabled)
        {
            var userData = Neighborhoods.UserData(profile);
            if (!wasEnabled)
            {
                ReconcileSuspendedHomeLotIntoPool();
                neighborhoodManager.SyncFromUserData(userData, language, resetLocations: true);
            }
            else if (neighborhoodManager.GetAllSimLocations().Count == 0)
            {
                neighborhoodManager.SyncFromUserData(userData, language);
            }

            if (world is not null && lot is not null && house > 0)
            {
                world.Neighborhood = neighborhoodManager;
                AttachConnectedNeighborhoodHooks(world);
                SyncNeighborhoodRecordsIntoLiveWorld(world, neighborhood, globallyRemovedNeighbors);
                SyncWorldPeopleRelationshipsFromNeighbors(world);
                neighborhoodManager.SyncFromWorld(world, house, activeLotIds: CollectActivePoolLotIds());
                if (!lotPool.TryGetLive(house, out _))
                    SeedFocusedSessionIntoPool();
                else
                    FlushFocusedLotSessionToPool();
                lotPool.FocusedHouse = house;
                if (lotPool.HomeHouse == 0)
                    lotPool.HomeHouse = EffectiveHomeHouseValue();
                ConfigureNeighborLotControllerFromWorld(world);
            }
            neighborControllerSeedDirty = true;
            EnsureNeighborLotControllerSeeded();
            if (world is not null && lot is not null && house > 0 && connectedOptions.EnableAdjacentLotRendering)
                PrewarmAdjacentNeighborhoodParallel();
        }
        else
        {
            if (wasVisitingAway && capturedHomeHouse > 0 && capturedHomeHouse != house)
            {
                if (lotPool.TryGetLive(capturedHomeHouse, out var homeLive))
                {
                    var dehydrated = DehydrateSession(homeLive);
                    suspendedHomeLot = new SuspendedHomeLot(
                        capturedHomeHouse, dehydrated.FamilyChunkId, dehydrated.LotBytes, dehydrated.Runtime, dehydrated.Runtime.Speed);
                }
                else if (lotPool.TryGetSaved(capturedHomeHouse, out var homeSaved))
                {
                    suspendedHomeLot = new SuspendedHomeLot(
                        capturedHomeHouse, homeSaved.FamilyChunkId, homeSaved.LotBytes, homeSaved.Runtime, homeSaved.Runtime.Speed);
                }
            }
            if (world is not null)
            {
                world.Neighborhood.TrackingEnabled = false;
                world.TryInterceptConnectedVisitorRemoval = null;
                world.NotifyRemovedNeighborCleared = null;
                world.TransitVisualOverrides.Clear();
            }
            activeCrossLotTransits.Clear();
            pendingAutonomousReturnHomes.Clear();
            globallyRemovedNeighbors.Clear();
            neighborLotController.Clear();
            lotPool.Clear();
            neighborhoodManager.RestoreSnapshot(null);
            neighborControllerSeedDirty = true;
            lastSeededNeighborhood = null;
            lastSeededSavedLotCount = -1;
            lastSeededLiveLotCount = -1;
        }

        var state = State();
        Write(new { type = "state", state });
        return new
        {
            enabled = connectedOptions.Enabled,
            adjacentRendering = connectedOptions.EnableAdjacentLotRendering,
            storyProgression = connectedOptions.EnableStoryProgression,
            maxLiveSplitLots = connectedOptions.MaxLiveSplitLots,
            focusedHouse = lotPool.FocusedHouse,
            homeHouse = lotPool.HomeHouse,
            state,
        };
    }

    private static int NormalizeSaveSpeed(int value) => value switch
    {
        3 => 3,
        10 => 10,
        _ => 1,
    };

    private static bool IsCommunityOrExpansionAwayLot(string userData, short targetHouse) =>
        targetHouse is (>= 21 and <= 30) or (>= 40 and <= 49) or (>= 81 and <= 89) or (>= 93 and <= 99) ||
        (!string.IsNullOrEmpty(userData) && Neighborhoods.IsCommunityLot(userData, targetHouse));

    private HashSet<int> CollectActivePoolLotIds()
    {
        var ids = new HashSet<int>();
        if (house > 0) ids.Add(house);
        foreach (var liveHouse in lotPool.LiveSessions.Keys)
            if (liveHouse > 0) ids.Add(liveHouse);
        foreach (var savedHouse in lotPool.SavedLotStates.Keys)
            if (savedHouse > 0) ids.Add(savedHouse);
        return ids;
    }

    private void ReconcileSuspendedHomeLotIntoPool()
    {
        if (suspendedHomeLot is not { } suspended) return;
        if (lotPool.HomeHouse == 0 && suspended.HomeHouse > 0)
            lotPool.HomeHouse = suspended.HomeHouse;
        if (suspended.HomeHouse > 0 &&
            !lotPool.TryGetLive(suspended.HomeHouse, out _) &&
            !lotPool.TryGetSaved(suspended.HomeHouse, out _))
        {
            lotPool.PutSaved(new DehydratedLotSession(
                suspended.HomeHouse,
                suspended.HomeFamilyChunkId,
                suspended.LotBytes,
                suspended.Runtime,
                suspended.Speed,
                Away: false,
                HomeHouse: suspended.HomeHouse,
                RestrictedActivePersonKeys: null,
                LastTick: updates,
                SelectedSim: 0,
                SaveDirty: false,
                LastRunningSpeed: NormalizeSaveSpeed(suspended.Runtime.Speed > 0 ? suspended.Runtime.Speed : suspended.Speed),
                ClockTicks: suspended.Runtime.ClockTicks ?? 0,
                ClockDay: suspended.Runtime.ClockDay ?? 0));
        }
        suspendedHomeLot = null;
    }

    private static IffFile MergeWorldNeighborRecordsIntoNeighborhood(
        SimulationWorld targetWorld,
        IffFile targetNeighborhood,
        Dictionary<short, uint>? globallyRemovedNeighbors = null)
    {
        targetWorld.SaveAllPersistentPersonData();
        var updated = targetNeighborhood;
        var nbrsChunk = updated.OfType(ResourceType.Parse("NBRS")).FirstOrDefault();
        if (nbrsChunk is not null)
        {
            var currentNbrs = Neighbors.ReadRecoveringSupportedPrefix(updated, nbrsChunk);
            if (currentNbrs.IsComplete)
            {
                var dirtyPersistent = targetWorld.DirtyPersistentPersonNeighborIds;
                var dirtyRelationships = targetWorld.DirtyRelationshipNeighbors;
                var dirtyPairs = targetWorld.DirtyRelationshipPairs;
                var dirtyPairsBySource = new Dictionary<short, HashSet<short>>();
                foreach (var (sourceId, targetId) in dirtyPairs)
                {
                    if (!dirtyPairsBySource.TryGetValue(sourceId, out var targets))
                    {
                        targets = [];
                        dirtyPairsBySource[sourceId] = targets;
                    }
                    targets.Add(targetId);
                }
                var removed = new Dictionary<short, uint>();
                if (globallyRemovedNeighbors is not null)
                {
                    foreach (var (remId, remGuid) in globallyRemovedNeighbors)
                        removed[remId] = remGuid;
                }
                foreach (var remId in targetWorld.RemovedNeighbors)
                {
                    var remGuid = targetWorld.RemovedNeighborGuids.TryGetValue(remId, out var g) ? g : 0u;
                    if (!removed.TryGetValue(remId, out var existingGuid) || (existingGuid == 0 && remGuid != 0))
                        removed[remId] = remGuid;
                }

                // Reconcile slot reuse: if a freed NeighborId now holds a new Sim with a different non-zero GUID,
                // clear the stale removal marker before filtering entries or relationships.
                if (removed.Count > 0)
                {
                    foreach (var existing in currentNbrs.Entries)
                    {
                        if (removed.TryGetValue(existing.NeighborId, out var deletedGuid) &&
                            deletedGuid != 0 && existing.Guid != 0 && existing.Guid != deletedGuid)
                        {
                            removed.Remove(existing.NeighborId);
                            globallyRemovedNeighbors?.Remove(existing.NeighborId);
                            targetWorld.ClearRemovedNeighbor(existing.NeighborId);
                        }
                    }

                    foreach (var live in targetWorld.NeighborWalkOrder)
                    {
                        if (live is null) continue;
                        var liveGuid = unchecked((uint)live.TypeRecordKey);
                        if (removed.TryGetValue(live.Id, out var deletedGuid) &&
                            deletedGuid != 0 && liveGuid != 0 && liveGuid != deletedGuid)
                        {
                            removed.Remove(live.Id);
                            globallyRemovedNeighbors?.Remove(live.Id);
                            targetWorld.ClearRemovedNeighbor(live.Id);
                        }
                    }
                }

                static bool MatchesDeletedNeighbor(IReadOnlyDictionary<short, uint> removedMap, short nid, uint entryGuid)
                {
                    if (!removedMap.TryGetValue(nid, out var deletedGuid))
                        return false;
                    return deletedGuid == 0 || entryGuid == 0 || entryGuid == deletedGuid;
                }

                var nbrsChanged = false;
                var entries = new List<Neighbor>(currentNbrs.Entries.Count);
                var entriesById = new Dictionary<short, Neighbor>();
                foreach (var existing in currentNbrs.Entries)
                {
                    if (MatchesDeletedNeighbor(removed, existing.NeighborId, existing.Guid))
                    {
                        nbrsChanged = true;
                        continue;
                    }
                    var next = existing;
                    targetWorld.Neighbors.TryGetValue(existing.NeighborId, out var live);
                    var copyPersistent = live is not null && dirtyPersistent.Contains(existing.NeighborId) && live.PersonRecordVersion > 0;
                    var nextRelationships = existing.Relationships;
                    var relationshipsChanged = false;
                    if (live is not null && dirtyPairsBySource.TryGetValue(existing.NeighborId, out var dirtyTargets))
                    {
                        var merged = new Dictionary<int, IReadOnlyList<int>>(existing.Relationships);
                        var liveSnapshot = live.Relationships.Snapshot();
                        foreach (var targetId in dirtyTargets)
                        {
                            if (liveSnapshot.TryGetValue(targetId, out var values))
                                merged[targetId] = values;
                            else
                                merged.Remove(targetId);
                        }
                        nextRelationships = merged;
                        relationshipsChanged = true;
                    }
                    if (removed.Count > 0 && nextRelationships.Keys.Any(k => k is >= short.MinValue and <= short.MaxValue && removed.ContainsKey((short)k)))
                    {
                        var cleaned = new Dictionary<int, IReadOnlyList<int>>(nextRelationships);
                        foreach (var remId in removed.Keys)
                            cleaned.Remove(remId);
                        nextRelationships = cleaned;
                        relationshipsChanged = true;
                    }
                    if (copyPersistent || relationshipsChanged)
                    {
                        next = existing with
                        {
                            Name = copyPersistent ? live!.Name : existing.Name,
                            RecordVersion = copyPersistent ? live!.PersonRecordVersion : existing.RecordVersion,
                            PersonData = copyPersistent ? live!.PersonData.ToArray() : existing.PersonData,
                            Relationships = nextRelationships,
                        };
                        nbrsChanged = true;
                    }
                    entries.Add(next);
                    entriesById[next.NeighborId] = next;
                }

                var famiMemberGuids = targetNeighborhood.OfType(ResourceType.Parse("FAMI"))
                    .Where(static c => c.Id != 0)
                    .SelectMany(c => Family.Read(targetNeighborhood, c).Members)
                    .ToHashSet();
                var updatedSlotCount = currentNbrs.SlotCount;
                var existingSlots = new HashSet<int>(entries.Select(e => e.Slot));
                for (var slotIndex = 0; slotIndex < targetWorld.NeighborWalkOrder.Count; slotIndex++)
                {
                    var live = targetWorld.NeighborWalkOrder[slotIndex];
                    if (live is null || MatchesDeletedNeighbor(removed, live.Id, unchecked((uint)live.TypeRecordKey)) || entriesById.ContainsKey(live.Id))
                        continue;
                    if (live.PersonRecordVersion <= 0 && live.Relationships.Snapshot().Count == 0)
                        continue;
                    if (live.PersonData[(int)PersonData.FamilyNumber] == 0 &&
                        live.PersonData[(int)PersonData.LingeringHouseNumber] == 0 &&
                        live.Relationships.Snapshot().Count == 0 &&
                        !famiMemberGuids.Contains(unchecked((uint)live.TypeRecordKey)))
                    {
                        continue;
                    }

                    var targetSlot = slotIndex;
                    while (existingSlots.Contains(targetSlot)) targetSlot++;
                    existingSlots.Add(targetSlot);
                    if (targetSlot + 1 > updatedSlotCount) updatedSlotCount = targetSlot + 1;

                    var initialRelationships = new Dictionary<int, IReadOnlyList<int>>();
                    if (dirtyPairsBySource.TryGetValue(live.Id, out var newTargets))
                    {
                        var liveSnapshot = live.Relationships.Snapshot();
                        foreach (var targetId in newTargets)
                        {
                            if (liveSnapshot.TryGetValue(targetId, out var values))
                                initialRelationships[targetId] = values;
                        }
                    }

                    var versioned = live.PersonRecordVersion > 0;
                    var added = new Neighbor(
                        Slot: targetSlot,
                        RecordVersion: versioned ? live.PersonRecordVersion : (currentNbrs.Version > 0x48 ? 10 : 4),
                        Name: live.Name,
                        Field0x1C: 0,
                        PersonData: versioned ? live.PersonData.ToArray() : null,
                        NeighborId: live.Id,
                        Guid: unchecked((uint)live.TypeRecordKey),
                        Relationships: initialRelationships,
                        StoredFormat: currentNbrs.Version > 0x48 ? 10 : 0,
                        PersonDataTrailingWord: 0);
                    entries.Add(added);
                    entriesById[added.NeighborId] = added;
                    nbrsChanged = true;
                }

                if (nbrsChanged)
                {
                    var rewritten = new Neighbors(
                        currentNbrs.Version,
                        updatedSlotCount,
                        entries,
                        currentNbrs.IsComplete,
                        currentNbrs.CompleteSlotPrefix);
                    var swap = nbrsChunk.Data.Span.Length > 0 && nbrsChunk.Data.Span[0] != 0;
                    updated = updated.WithChunkDataResized(nbrsChunk.Type, nbrsChunk.Id, rewritten.WriteChunkData(swap));
                }
                dirtyPersistent.Clear();
                dirtyRelationships.Clear();
                dirtyPairs.Clear();
            }
        }

        var tattType = ResourceType.Parse("TATT");
        var existingTattChunk = updated.Find(tattType, 1);
        var existingTatt = existingTattChunk is not null ? TypeAttributes.Read(updated, existingTattChunk) : null;
        var tattMap = existingTatt is not null
            ? new Dictionary<uint, short[]>(existingTatt.AttributesByGuid)
            : new Dictionary<uint, short[]>();
        var tattChanged = false;
        foreach (var obj in targetWorld.Objects.Values)
        {
            var guid = unchecked((uint)(obj.TypeRecord?.Key ?? 0));
            if (guid != 0 && obj.TypeAttributes is { Length: > 0 })
            {
                tattMap[guid] = obj.TypeAttributes.ToArray();
                tattChanged = true;
            }
        }
        if (tattChanged && (existingTattChunk is not null || tattMap.Count > 0))
        {
            var tattVersion = existingTatt?.Version ?? 0x49;
            var tattSwap = existingTattChunk is not null && existingTattChunk.Data.Span.Length > 0 && existingTattChunk.Data.Span[0] != 0;
            var tattCoded = existingTattChunk is null || existingTattChunk.Data.Span.Length <= 12 || existingTattChunk.Data.Span[12] != 0;
            updated = updated.WithAddedOrReplacedChunk(tattType, 1, TypeAttributes.Write(tattMap, tattVersion, tattCoded, tattSwap));
        }

        return updated;
    }

    private Dictionary<short, uint> CollectGloballyRemovedNeighbors()
    {
        void MergeFromWorld(SimulationWorld targetWorld)
        {
            foreach (var remId in targetWorld.RemovedNeighbors)
            {
                var remGuid = targetWorld.RemovedNeighborGuids.TryGetValue(remId, out var g) ? g : 0u;
                if (!globallyRemovedNeighbors.TryGetValue(remId, out var existingGuid) ||
                    (existingGuid == 0 && remGuid != 0))
                {
                    globallyRemovedNeighbors[remId] = remGuid;
                }
            }
        }

        foreach (var live in lotPool.LiveSessions.Values)
            MergeFromWorld(live.World);
        if (world is not null)
            MergeFromWorld(world);
        return new Dictionary<short, uint>(globallyRemovedNeighbors);
    }

    private void SyncAllLiveWorldsFromNeighborhood(Dictionary<short, uint>? globallyRemoved = null)
    {
        if (neighborhood is null) return;
        foreach (var live in lotPool.LiveSessions.Values)
            SyncNeighborhoodRecordsIntoLiveWorld(live.World, neighborhood, globallyRemoved, registry);
        if (world is not null && (house <= 0 || !lotPool.TryGetLive(house, out _)))
            SyncNeighborhoodRecordsIntoLiveWorld(world, neighborhood, globallyRemoved, registry);
    }

    private void SyncLiveNeighborRecordsToNeighborhood()
    {
        if (neighborhood is null) return;
        var globallyRemoved = CollectGloballyRemovedNeighbors();

        foreach (var live in lotPool.LiveSessions.Values)
            neighborhood = MergeWorldNeighborRecordsIntoNeighborhood(live.World, neighborhood, globallyRemoved);
        if (world is not null && (house <= 0 || !lotPool.TryGetLive(house, out _)))
            neighborhood = MergeWorldNeighborRecordsIntoNeighborhood(world, neighborhood, globallyRemoved);
        foreach (var remId in globallyRemovedNeighbors.Keys.ToArray())
        {
            if (!globallyRemoved.ContainsKey(remId))
                globallyRemovedNeighbors.Remove(remId);
        }
        neighborhood = neighborLotController.FlushToNeighborhood(neighborhood, out _);
        SyncAllLiveWorldsFromNeighborhood(globallyRemoved);
    }

    private static void SyncNeighborhoodRecordsIntoLiveWorld(
        SimulationWorld targetWorld,
        IffFile? sourceNeighborhood,
        Dictionary<short, uint>? globallyRemovedNeighbors = null,
        ObjectRegistry? objectRegistry = null)
    {
        if (sourceNeighborhood is null) return;
        var nbrsChunk = sourceNeighborhood.OfType(ResourceType.Parse("NBRS")).FirstOrDefault();
        if (nbrsChunk is null) return;
        var currentNbrs = Neighbors.ReadRecoveringSupportedPrefix(sourceNeighborhood, nbrsChunk);
        if (!currentNbrs.IsComplete) return;

        var activePersonNeighborIds = new HashSet<short>();
        foreach (var person in targetWorld.WalkOrder.OfType<XPerson>())
        {
            var nid = person[PersonData.NeighborId];
            if (nid > 0) activePersonNeighborIds.Add(nid);
        }

        var effectiveRemoved = globallyRemovedNeighbors is not null
            ? new Dictionary<short, uint>(globallyRemovedNeighbors)
            : new Dictionary<short, uint>();
        if (effectiveRemoved.Count > 0)
        {
            foreach (var entry in currentNbrs.Entries)
            {
                if (effectiveRemoved.TryGetValue(entry.NeighborId, out var delGuid) &&
                    delGuid != 0 && entry.Guid != 0 && entry.Guid != delGuid)
                {
                    effectiveRemoved.Remove(entry.NeighborId);
                    globallyRemovedNeighbors?.Remove(entry.NeighborId);
                    targetWorld.ClearRemovedNeighbor(entry.NeighborId);
                }
            }
            foreach (var (nid, liveRecord) in targetWorld.Neighbors)
            {
                var liveGuid = unchecked((uint)liveRecord.TypeRecordKey);
                if (effectiveRemoved.TryGetValue(nid, out var delGuid) &&
                    delGuid != 0 && liveGuid != 0 && liveGuid != delGuid)
                {
                    effectiveRemoved.Remove(nid);
                    globallyRemovedNeighbors?.Remove(nid);
                    targetWorld.ClearRemovedNeighbor(nid);
                }
            }
        }

        // Caveat #1: Prune neighbors deleted on another live lot from targetWorld.
        var diskIds = currentNbrs.Entries.Select(static e => e.NeighborId).ToHashSet();
        foreach (var nid in targetWorld.Neighbors.Keys.ToArray())
        {
            var liveRecord = targetWorld.Neighbors[nid];
            var liveGuid = unchecked((uint)liveRecord.TypeRecordKey);
            var isGloballyRemoved = effectiveRemoved.TryGetValue(nid, out var deletedGuid) &&
                                    (deletedGuid == 0 || liveGuid == 0 || liveGuid == deletedGuid);
            if (!isGloballyRemoved &&
                (diskIds.Contains(nid) ||
                 targetWorld.DirtyPersistentPersonNeighborIds.Contains(nid) ||
                 activePersonNeighborIds.Contains(nid)))
            {
                continue;
            }
            if (isGloballyRemoved && activePersonNeighborIds.Contains(nid))
                continue;

            var previouslyPersisted = isGloballyRemoved ||
                                      liveRecord.PersonRecordVersion > 0 ||
                                      targetWorld.NeighborWalkOrder.Contains(liveRecord);
            if (!previouslyPersisted) continue;

            targetWorld.Neighbors.Remove(nid);
            var slotIdx = targetWorld.NeighborWalkOrder.IndexOf(liveRecord);
            if (slotIdx >= 0) targetWorld.NeighborWalkOrder[slotIdx] = null;
            targetWorld.DirtyPersistentPersonNeighborIds.Remove(nid);
            targetWorld.DirtyRelationshipNeighbors.Remove(nid);
            targetWorld.DirtyRelationshipPairs.RemoveWhere(p => p.SourceNeighborId == nid || p.TargetNeighborId == nid);
            foreach (var other in targetWorld.Neighbors.Values)
                other.Relationships.RemoveTarget(nid);
            foreach (var person in targetWorld.WalkOrder.OfType<XPerson>())
                person.Relationships.RemoveTarget(nid);
        }

        if (effectiveRemoved.Count > 0)
        {
            foreach (var (remId, deletedGuid) in effectiveRemoved)
            {
                if (activePersonNeighborIds.Contains(remId)) continue;
                for (var i = 0; i < targetWorld.NeighborWalkOrder.Count; i++)
                {
                    if (targetWorld.NeighborWalkOrder[i] is { } walkNbr && walkNbr.Id == remId)
                    {
                        var walkGuid = unchecked((uint)walkNbr.TypeRecordKey);
                        if (deletedGuid == 0 || walkGuid == 0 || walkGuid == deletedGuid)
                            targetWorld.NeighborWalkOrder[i] = null;
                    }
                }
                foreach (var other in targetWorld.Neighbors.Values)
                    other.Relationships.RemoveTarget(remId);
                foreach (var person in targetWorld.WalkOrder.OfType<XPerson>())
                    person.Relationships.RemoveTarget(remId);
            }
        }

        foreach (var entry in currentNbrs.Entries)
        {
            if (!targetWorld.Neighbors.TryGetValue(entry.NeighborId, out var live))
            {
                if (objectRegistry is not null &&
                    !effectiveRemoved.ContainsKey(entry.NeighborId) &&
                    !(targetWorld.RemovedNeighbors.Contains(entry.NeighborId) &&
                      (!targetWorld.RemovedNeighborGuids.TryGetValue(entry.NeighborId, out var worldDelGuid) ||
                       worldDelGuid == 0 || entry.Guid == 0 || worldDelGuid == entry.Guid)) &&
                    objectRegistry.Find(entry.Guid) is { } regEntry)
                {
                    var definition = regEntry.Definition.Fields.Select(static v => unchecked((short)v)).ToArray();
                    var addedLive = new NeighborState(entry.NeighborId, definition)
                    {
                        Name = entry.Name,
                        TypeRecordKey = unchecked((int)entry.Guid),
                        PersonRecordVersion = entry.PersonData is null ? 0 : entry.RecordVersion,
                        FriendEligibilityObjectFlagsB4 = regEntry.Scan == ObjectScan.People ? 4u : 0u,
                        RelationshipsChangedFlag24 = true,
                    };
                    if (entry.PersonData is { Count: > 0 } pData)
                        pData.Take(addedLive.PersonData.Length).ToArray().CopyTo(addedLive.PersonData, 0);
                    foreach (var (otherId, values) in entry.Relationships)
                    {
                        if (otherId is < short.MinValue or > short.MaxValue) continue;
                        var targetId = (short)otherId;
                        addedLive.Relationships.Resize(targetId, values.Count);
                        for (var i = 0; i < values.Count; i++)
                            addedLive.Relationships.Set(targetId, i, values[i]);
                    }
                    targetWorld.ClearRemovedNeighbor(entry.NeighborId);
                    targetWorld.Neighbors[entry.NeighborId] = addedLive;
                    if (entry.Slot >= 0)
                    {
                        while (targetWorld.NeighborWalkOrder.Count <= entry.Slot)
                            targetWorld.NeighborWalkOrder.Add(null);
                        if (targetWorld.NeighborWalkOrder[entry.Slot] is null)
                            targetWorld.NeighborWalkOrder[entry.Slot] = addedLive;
                    }
                }
                continue;
            }
            var slotReused = entry.Guid != 0 && live.TypeRecordKey != 0 &&
                             unchecked((uint)live.TypeRecordKey) != entry.Guid &&
                             !activePersonNeighborIds.Contains(entry.NeighborId);
            if (slotReused)
            {
                live.TypeRecordKey = unchecked((int)entry.Guid);
                live.Name = entry.Name;
                foreach (var existingTarget in live.Relationships.Snapshot().Keys.ToArray())
                {
                    if (existingTarget is >= short.MinValue and <= short.MaxValue)
                        live.Relationships.RemoveTarget((short)existingTarget);
                }
            }
            if (entry.RecordVersion > 0 &&
                entry.PersonData is { Count: > 0 } &&
                (slotReused ||
                 (!targetWorld.DirtyPersistentPersonNeighborIds.Contains(entry.NeighborId) &&
                  !activePersonNeighborIds.Contains(entry.NeighborId))))
            {
                var copyLen = Math.Min(live.PersonData.Length, entry.PersonData.Count);
                for (var i = 0; i < copyLen; i++)
                    live.PersonData[i] = entry.PersonData[i];
                live.PersonRecordVersion = slotReused ? entry.RecordVersion : Math.Max(live.PersonRecordVersion, entry.RecordVersion);
            }

            foreach (var existingTarget in live.Relationships.Snapshot().Keys.ToArray())
            {
                if (existingTarget is < short.MinValue or > short.MaxValue) continue;
                var targetId = (short)existingTarget;
                if (entry.Relationships.ContainsKey(existingTarget)) continue;
                if (targetWorld.DirtyRelationshipPairs.Contains((entry.NeighborId, targetId))) continue;
                live.Relationships.RemoveTarget(targetId);
            }

            foreach (var (targetKey, values) in entry.Relationships)
            {
                if (targetKey is < short.MinValue or > short.MaxValue) continue;
                var targetId = (short)targetKey;
                if (targetWorld.DirtyRelationshipPairs.Contains((entry.NeighborId, targetId)))
                    continue;
                live.Relationships.Resize(targetId, values.Count);
                for (var i = 0; i < values.Count; i++)
                    live.Relationships.Set(targetId, i, values[i]);
            }
        }
    }

    private static void SyncWorldPeopleRelationshipsFromNeighbors(SimulationWorld targetWorld)
    {
        foreach (var person in targetWorld.WalkOrder.OfType<XPerson>())
        {
            var nid = person[PersonData.NeighborId];
            if (nid <= 0 || targetWorld.DirtyRelationshipNeighbors.Contains(nid))
                continue;
            if (!targetWorld.Neighbors.TryGetValue(nid, out var neighbor))
                continue;
            foreach (var (otherId, values) in neighbor.Relationships.Snapshot())
            {
                if (otherId is < short.MinValue or > short.MaxValue) continue;
                var targetId = (short)otherId;
                if (targetWorld.DirtyRelationshipPairs.Contains((nid, targetId)))
                    continue;
                person.Relationships.Resize(targetId, values.Count);
                for (var index = 0; index < values.Count; index++)
                    person.Relationships.Set(targetId, index, values[index]);
            }
        }
    }

    private short EffectiveHomeHouseValue()
    {
        if (ConnectedNeighborhoodEnabled)
        {
            if (lotPool.TryGetLive(house, out var liveSession) && liveSession.HomeHouse > 0)
                return liveSession.HomeHouse;
            if (lotPool.HomeHouse > 0)
                return lotPool.HomeHouse;
        }

        return suspendedHomeLot?.HomeHouse ??
               (world is not null && house > 0 && world[SimulationGlobal.SimlessBuildMode] == 0 && !IsVisitingAwayLot ? house : (short)0);
    }

    private bool IsVisitingAwayLotValue()
    {
        if (world is null || house == 0) return false;
        if (ConnectedNeighborhoodEnabled && lotPool.TryGetLive(house, out var liveSession))
            return liveSession.Away;

        return (suspendedHomeLot is not null && suspendedHomeLot.HomeHouse != house) ||
               (world[SimulationGlobal.SimlessBuildMode] == 0 &&
                IsAwayLot(Path.GetDirectoryName(neighborhoodPath) ?? Neighborhoods.UserData(profile), house));
    }

    private bool IsAwayLotForPool(string userData, short targetHouse, short poolHomeHouse) =>
        (poolHomeHouse > 0 && poolHomeHouse != targetHouse) ||
        targetHouse is (>= 21 and <= 30) or (>= 40 and <= 49) or (>= 81 and <= 89) or (>= 93 and <= 99) ||
        (!string.IsNullOrEmpty(userData) && Neighborhoods.IsCommunityLot(userData, targetHouse));

    private void SeedFocusedSessionIntoPool(bool? awayOverride = null, short? homeHouseOverride = null)
    {
        if (world is null || lot is null || house <= 0) return;

        var userData = Path.GetDirectoryName(neighborhoodPath) ?? Neighborhoods.UserData(profile);
        var resolvedHome = homeHouseOverride is > 0
            ? homeHouseOverride.Value
            : lotPool.HomeHouse > 0
                ? lotPool.HomeHouse
                : (suspendedHomeLot?.HomeHouse ?? (world[SimulationGlobal.SimlessBuildMode] == 0 && !IsAwayLot(userData, house) ? house : (short)0));
        var isAway = awayOverride ?? IsAwayLotForPool(userData, house, resolvedHome);

        var session = new LotSession
        {
            House = house,
            World = world,
            Lot = lot,
            ActiveFamilyChunkId = activeFamilyChunkId,
            Away = isAway,
            HomeHouse = resolvedHome,
            RestrictedActivePersonKeys = (world.FamilyBudget as FamilyBudget)?.RestrictedActivePersonKeys?.ToArray(),
            LastTick = updates,
            CommittedVacationState = committedVacationState,
            SelectedSim = selectedSim,
            ViewLevel = viewLevel,
            ViewerMode = viewerMode,
            Speed = speed,
            LastRunningSpeed = lastRunningSpeed,
            AutoSpeedSaved = autoSpeedSaved,
            DialogResumeSpeed = dialogResumeSpeed,
            SaveDirty = saveDirty,
            EditingView = editingView,
            DoorWindowBuildToolActive = doorWindowBuildToolActive,
            RoofsVisible = roofsVisible,
            CutawayTile = cutawayTile,
            ExportedCutawaySimKey = exportedCutawaySimKey,
            InitialObjectValuation = initialObjectValuation,
            InitialValuationLot = initialValuationLot,
            LotValuationEdited = lotValuationEdited,
            LastStreamedObjectsSignature = lastStreamedObjectsSignature,
        };
        session.CutawayRooms.AddRange(cutawayRooms);
        foreach (var item in undoStack.Reverse()) session.UndoStack.Push(item);
        foreach (var item in redoStack.Reverse()) session.RedoStack.Push(item);
        session.AnimationState.Revision = animationState.Revision;
        session.AnimationState.Kept = animationState.Kept;

        lotPool.PutLive(session);
        lotPool.FocusedHouse = house;
        if (lotPool.HomeHouse == 0 && resolvedHome > 0)
            lotPool.HomeHouse = resolvedHome;
    }

    private void FlushFocusedLotSessionToPool()
    {
        if (world is null || lot is null || house <= 0) return;

        if (!lotPool.TryGetLive(house, out var session))
        {
            SeedFocusedSessionIntoPool();
            return;
        }

        session.World = world;
        session.Lot = lot;
        session.ActiveFamilyChunkId = activeFamilyChunkId;
        session.Away = IsVisitingAwayLotValue();
        if (session.HomeHouse == 0 && lotPool.HomeHouse > 0)
            session.HomeHouse = lotPool.HomeHouse;
        session.RestrictedActivePersonKeys = (world.FamilyBudget as FamilyBudget)?.RestrictedActivePersonKeys?.ToArray();
        session.LastTick = updates;
        session.CommittedVacationState = committedVacationState;
        session.SelectedSim = selectedSim;
        session.ViewLevel = viewLevel;
        session.ViewerMode = viewerMode;
        session.Speed = speed;
        session.LastRunningSpeed = lastRunningSpeed;
        session.AutoSpeedSaved = autoSpeedSaved;
        session.DialogResumeSpeed = dialogResumeSpeed;
        session.SaveDirty = saveDirty;
        session.EditingView = editingView;
        session.DoorWindowBuildToolActive = doorWindowBuildToolActive;
        session.RoofsVisible = roofsVisible;
        session.CutawayTile = cutawayTile;
        session.CutawayRooms.Clear();
        session.CutawayRooms.AddRange(cutawayRooms);
        session.ExportedCutawaySimKey = exportedCutawaySimKey;
        session.InitialObjectValuation = initialObjectValuation;
        session.InitialValuationLot = initialValuationLot;
        session.LotValuationEdited = lotValuationEdited;
        session.UndoStack.Clear();
        foreach (var item in undoStack.Reverse()) session.UndoStack.Push(item);
        session.RedoStack.Clear();
        foreach (var item in redoStack.Reverse()) session.RedoStack.Push(item);
        session.AnimationState.Revision = animationState.Revision;
        session.AnimationState.Kept = animationState.Kept;
        session.LastStreamedObjectsSignature = lastStreamedObjectsSignature;
        lotPool.PutLive(session);
    }

    private void ApplyLotSessionToServer(LotSession session)
    {
        house = session.House;
        world = session.World;
        lot = session.Lot;
        world.Neighborhood = neighborhoodManager;
        activeFamilyChunkId = session.ActiveFamilyChunkId;
        committedVacationState = session.CommittedVacationState;
        selectedSim = session.SelectedSim;
        if (world.ObjectById(selectedSim) is not XPerson)
            selectedSim = DefaultSelectedSimId();
        SyncSelectedSimFlags(world, selectedSim);
        viewLevel = session.ViewLevel;
        viewerMode = session.ViewerMode;
        world[SimulationGlobal.Mode] = viewerMode;
        if (viewerScale != 0) world[SimulationGlobal.ZoomLevelSML] = viewerScale;
        speed = session.Speed;
        lastRunningSpeed = NormalizeSaveSpeed(session.LastRunningSpeed);
        pieHead = null;
        SetSimulationSpeed(session.Speed, blockingDialog: HasBlockingDialog(world));
        autoSpeedSaved = session.AutoSpeedSaved;
        dialogResumeSpeed = session.DialogResumeSpeed;
        saveDirty = session.SaveDirty;
        editingView = session.EditingView;
        doorWindowBuildToolActive = session.DoorWindowBuildToolActive;
        roofsVisible = session.RoofsVisible;
        cutawayTile = session.CutawayTile;
        cutawayRooms.Clear();
        cutawayRooms.AddRange(session.CutawayRooms);
        exportedCutawaySimKey = session.ExportedCutawaySimKey;
        initialObjectValuation = session.InitialObjectValuation;
        initialValuationLot = session.InitialValuationLot;
        lotValuationEdited = session.LotValuationEdited;
        undoStack.Clear();
        foreach (var item in session.UndoStack.Reverse()) undoStack.Push(item);
        redoStack.Clear();
        foreach (var item in session.RedoStack.Reverse()) redoStack.Push(item);
        liveTokens.Clear();
        animationState.Revision = session.AnimationState.Revision;
        animationState.Kept = session.AnimationState.Kept;
        lastStreamedObjectsSignature = session.LastStreamedObjectsSignature;

        buildableLot = null;
        buildableMask = null;
        stateFloorsLot = null;
        stateFloorsCache = null;
        stateWallsLot = null;
        stateWallsCache = null;
        cachedRoofLot = null;
        simulationError = null;
    }

    private DehydratedLotSession DehydrateSession(LotSession session)
    {
        var sessionFamilyChunkId = session.ActiveFamilyChunkId ?? (session.World.FamilyBudget as FamilyBudget)?.FamilyChunkId;
        UpdatePreSaveState(session.World, session.Lot, sessionFamilyChunkId);
        if (session.World.SavedSimulationState is { } savedSimi)
        {
            var (furnishingsValue, architectureValue) = ComputeLotValuation(
                session.World,
                session.Lot,
                session.InitialObjectValuation,
                session.InitialValuationLot,
                session.LotValuationEdited,
                savedSimi);
            session.World.ReplaceSavedValuation(furnishingsValue, architectureValue);
            var userData = Neighborhoods.UserData(profile);
            if (!session.Away &&
                !IsCommunityOrExpansionAwayLot(userData, session.House) &&
                sessionFamilyChunkId is { } famChunkId &&
                session.World.FamilyBudget is FamilyBudget fb &&
                fb.ActiveFamily is { } activeFam &&
                neighborhood is not null)
            {
                var updatedValueFam = activeFam.WithValue(
                    savedSimi.Field0x9C + furnishingsValue + (int)(architectureValue * (session.World.ArchitectureValueMultiplier ?? 0.7f)));
                fb.ActiveFamily = updatedValueFam;
                var famiType = ResourceType.Parse("FAMI");
                var existingChunk = neighborhood.Find(famiType, famChunkId);
                neighborhood = neighborhood.WithAddedOrReplacedChunk(
                    famiType, famChunkId, updatedValueFam.Write(famChunkId), existingChunk?.Flags ?? 0, existingChunk?.Label ?? "");
            }
        }
        if (neighborhood is not null)
        {
            var globallyRemoved = CollectGloballyRemovedNeighbors();
            neighborhood = MergeWorldNeighborRecordsIntoNeighborhood(session.World, neighborhood, globallyRemoved);
            SyncNeighborhoodRecordsIntoLiveWorld(session.World, neighborhood, globallyRemoved);
            foreach (var live in lotPool.LiveSessions.Values)
            {
                if (!ReferenceEquals(live.World, session.World))
                    SyncNeighborhoodRecordsIntoLiveWorld(live.World, neighborhood, globallyRemoved);
            }
            if (world is not null && !ReferenceEquals(world, session.World) &&
                (house <= 0 || !lotPool.TryGetLive(house, out _)))
            {
                SyncNeighborhoodRecordsIntoLiveWorld(world, neighborhood, globallyRemoved);
            }
        }
        session.World.CancelSyntheticDirectPositionRoutes();
        var (withObjects, _) = session.World.WriteSavedLotObjectsTo(session.Lot);
        var saved = session.World.WriteSavedSimulationStateTo(withObjects);
        var saveSpeed = NormalizeSaveSpeed(session.Speed > 0 ? session.Speed : session.LastRunningSpeed);
        var runtime = CaptureRuntimeMetadata(session.World, saved.Bytes.Span, saveSpeed);
        session.Lot = saved;
        session.InitialObjectValuation = SumPlacedObjectValues(session.World);
        session.InitialValuationLot = saved;
        session.LotValuationEdited = false;
        return new DehydratedLotSession(
            session.House,
            sessionFamilyChunkId,
            saved.Bytes.ToArray(),
            runtime,
            session.Speed,
            session.Away,
            session.HomeHouse,
            session.RestrictedActivePersonKeys,
            session.LastTick,
            session.SelectedSim,
            SaveDirty: session.SaveDirty,
            CommittedVacationState: session.CommittedVacationState,
            ViewLevel: session.ViewLevel,
            ViewerMode: session.ViewerMode,
            RoofsVisible: session.RoofsVisible,
            EditingView: session.EditingView,
            DoorWindowBuildToolActive: session.DoorWindowBuildToolActive,
            CutawayTile: session.CutawayTile,
            CutawayRooms: session.CutawayRooms.ToArray(),
            LastRunningSpeed: NormalizeSaveSpeed(session.LastRunningSpeed),
            ClockTicks: session.World.Clock.Ticks,
            ClockDay: session.World.Clock.Day);
    }

    private LotSession HydrateSession(DehydratedLotSession saved, string userData)
    {
        var destPath = Path.Combine(userData, "Houses", $"House{saved.House:D2}.iff");
        registry = EnsureRegistry().WithRefreshedPeople(Path.Combine(userData, "Characters"));
        SimulationWorld? loadedWorldRef = null;
        var setup = WorldBootstrap.Load(
            game,
            destPath,
            language,
            () => ReferenceEquals(world, loadedWorldRef) ? speed == 0 : (lotPool.TryGetLive(saved.House, out var s) && s.Speed == 0),
            saved.House,
            userDataPath: userData,
            preloadedLot: new IffFile(saved.LotBytes, destPath),
            scoreAutonomy: ScoreAutonomyChoice,
            existingRegistry: registry,
            neighborhoodManager: neighborhoodManager,
            connectedNeighborhoodEnabled: true,
            preloadedNeighborhood: neighborhood);

        var loadedWorld = setup.World;
        loadedWorldRef = loadedWorld;
        if (ConnectedNeighborhoodEnabled && lotPool.HomeHouse > 0 && loadedWorld.ActiveLotNumber != lotPool.HomeHouse)
        {
            if (loadedWorld.NeighborhoodData.Length > 1 && loadedWorld.NeighborhoodData[1] < 3)
                loadedWorld.NeighborhoodData[1] = 3;
            loadedWorld[SimulationGlobal.IsTutorialHouseNoYes] = 0;
            loadedWorld.TutorialOwner = null;
            foreach (var dlg in loadedWorld.DialogRecords)
                if (dlg.State == 0) { dlg.State = 2; dlg.Visible = false; }
        }
        global ??= setup.Global;
        neighborhood ??= setup.Neighborhood;
        neighborhoodPath ??= setup.NeighborhoodPath;
        loadedWorld.AutonomyForced = world?.AutonomyForced;
        if (setup.Registry is not null)
        {
            registry = setup.Registry;
            loadedWorld.UiTextFile ??= registry.UiText;
        }

        HydrateTypeAttributesFromNeighborhood(loadedWorld, setup.Lot, setup.Neighborhood, overwriteExisting: true);
        var resolvedFamilyChunkId = saved.FamilyChunkId ?? setup.ActiveFamilyChunkId;
        if (resolvedFamilyChunkId is null && !loadedWorld.WalkOrder.OfType<XPerson>().Any(p => p[PersonData.FamilyNumber] > 0))
            loadedWorld[SimulationGlobal.SimlessBuildMode] = 1;
        else if (resolvedFamilyChunkId is not null)
            loadedWorld[SimulationGlobal.SimlessBuildMode] = 0;

        if (saved.Runtime.Budget is { } savedBudget &&
            loadedWorld.FamilyBudget is FamilyBudget restoredBudget &&
            restoredBudget.ActiveFamily is { } restoredFamily)
        {
            restoredBudget.ActiveFamily = restoredFamily.WithBudget(savedBudget);
            loadedWorld[SimulationGlobal.BudgetMod10000] = unchecked((short)(savedBudget % 10000));
            loadedWorld[SimulationGlobal.BudgetDiv10000] = unchecked((short)(savedBudget / 10000));
        }
        if (saved.RestrictedActivePersonKeys is { Count: > 0 } &&
            loadedWorld.FamilyBudget is FamilyBudget restrictedBudget)
        {
            restrictedBudget.RestrictActivePersonKeys(saved.RestrictedActivePersonKeys);
        }

        RestorePersonAnimationRuntime(loadedWorld, saved.Runtime.PersonAnimations);
        RestoreDialogRecords(loadedWorld, saved.Runtime.Dialogs);
        var clockTicks = saved.ClockTicks > 0 ? saved.ClockTicks : (saved.Runtime.ClockTicks ?? 0);
        var clockDay = saved.ClockDay > 0 ? saved.ClockDay : (saved.Runtime.ClockDay ?? 0);
        if (clockTicks > 0 || clockDay > 0)
            loadedWorld.Clock.RestoreProgress(clockTicks, clockDay);
        SyncNeighborhoodRecordsIntoLiveWorld(loadedWorld, neighborhood, CollectGloballyRemovedNeighbors());
        SyncWorldPeopleRelationshipsFromNeighbors(loadedWorld);
        ConfigureNeighborLotControllerFromWorld(loadedWorld);
        var catchUpApplied = neighborLotController.ApplyToWorld(loadedWorld, saved.House, neighborhoodManager);
        neighborLotController.SetLotActive(saved.House, true);
        neighborControllerSeedDirty = true;
        AttachTutorialFlash(loadedWorld);
        AttachNoticeListener(loadedWorld);
        InitializeFameCallbackTarget(loadedWorld);
        AttachConnectedNeighborhoodHooks(loadedWorld);

        if (loadedWorld.ActiveLotNumber == 0)
            loadedWorld.ActiveLotNumber = saved.House;
        AttachConnectedNeighborhoodHooks(loadedWorld);

        var session = new LotSession
        {
            House = saved.House,
            World = loadedWorld,
            Lot = setup.Lot,
            ActiveFamilyChunkId = resolvedFamilyChunkId,
            Away = saved.Away,
            HomeHouse = saved.HomeHouse,
            RestrictedActivePersonKeys = saved.RestrictedActivePersonKeys,
            LastTick = saved.LastTick,
            CommittedVacationState = saved.CommittedVacationState,
            SelectedSim = saved.SelectedSim,
            ViewLevel = saved.ViewLevel,
            ViewerMode = saved.ViewerMode,
            Speed = saved.Speed > 0 ? saved.Speed : 0,
            LastRunningSpeed = NormalizeSaveSpeed(saved.LastRunningSpeed > 0 ? saved.LastRunningSpeed : saved.Runtime.Speed),
            SaveDirty = saved.SaveDirty || catchUpApplied,
            EditingView = saved.EditingView,
            DoorWindowBuildToolActive = saved.DoorWindowBuildToolActive,
            RoofsVisible = saved.RoofsVisible,
            CutawayTile = saved.CutawayTile,
            InitialObjectValuation = SumPlacedObjectValues(loadedWorld),
            InitialValuationLot = setup.Lot,
            LotValuationEdited = false,
        };
        if (saved.CutawayRooms is { Count: > 0 })
            session.CutawayRooms.AddRange(saved.CutawayRooms);
        neighborhoodManager.SyncFromWorld(loadedWorld, saved.House, preserveOtherLotLocations: true, activeLotIds: CollectActivePoolLotIds());
        return session;
    }

    private LotSession LoadFreshSessionForPool(short targetHouse, string userData)
    {
        var path = Path.Combine(userData, "Houses", $"House{targetHouse:D2}.iff");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Lot {targetHouse} not found.");

        var runtimePath = CanonicalRuntimePath(path);
        if (!File.Exists(runtimePath) && adjacentPreviewWorlds.Remove(targetHouse, out var promotedPreview))
        {
            var promotedWorld = promotedPreview.World;
            if (ConnectedNeighborhoodEnabled && lotPool.HomeHouse > 0 && promotedWorld.ActiveLotNumber != lotPool.HomeHouse)
            {
                if (promotedWorld.NeighborhoodData.Length > 1 && promotedWorld.NeighborhoodData[1] < 3)
                    promotedWorld.NeighborhoodData[1] = 3;
                promotedWorld[SimulationGlobal.IsTutorialHouseNoYes] = 0;
                promotedWorld.TutorialOwner = null;
                foreach (var dlg in promotedWorld.DialogRecords)
                    if (dlg.State == 0) { dlg.State = 2; dlg.Visible = false; }
            }
            promotedWorld.Neighborhood = neighborhoodManager;
            promotedWorld.AutonomyForced = world?.AutonomyForced;
            if (global is not null)
            {
                promotedWorld.AutonomyHost = new AutonomyHost(
                    promotedWorld,
                    global,
                    targetHouse,
                    () => ReferenceEquals(world, promotedWorld) ? speed == 0 : (lotPool.TryGetLive(targetHouse, out var s) && s.Speed == 0),
                    ScoreAutonomyChoice);
            }
            HydrateTypeAttributesFromNeighborhood(promotedWorld, promotedPreview.Lot, neighborhood);
            if (promotedPreview.ActiveFamilyChunkId is null && !promotedWorld.WalkOrder.OfType<XPerson>().Any(p => p[PersonData.FamilyNumber] > 0))
                promotedWorld[SimulationGlobal.SimlessBuildMode] = 1;
            else if (promotedPreview.ActiveFamilyChunkId is not null)
                promotedWorld[SimulationGlobal.SimlessBuildMode] = 0;

            SyncNeighborhoodRecordsIntoLiveWorld(promotedWorld, neighborhood, CollectGloballyRemovedNeighbors());
            SyncWorldPeopleRelationshipsFromNeighbors(promotedWorld);
            ConfigureNeighborLotControllerFromWorld(promotedWorld);
            var promotedCatchUp = neighborLotController.ApplyToWorld(promotedWorld, targetHouse, neighborhoodManager);
            neighborLotController.SetLotActive(targetHouse, true);
            neighborControllerSeedDirty = true;
            AttachTutorialFlash(promotedWorld);
            AttachNoticeListener(promotedWorld);
            InitializeFameCallbackTarget(promotedWorld);
            AttachConnectedNeighborhoodHooks(promotedWorld);
            promotedWorld.RefreshNeighborInteractionManager();

            var promotedCommunityOrExpAway = IsCommunityOrExpansionAwayLot(userData, targetHouse);
            bool promotedIsAway = promotedCommunityOrExpAway && promotedWorld[SimulationGlobal.SimlessBuildMode] == 0;
            var promotedHome = lotPool.HomeHouse > 0
                ? lotPool.HomeHouse
                : (promotedPreview.ActiveFamilyChunkId is not null && !promotedCommunityOrExpAway && promotedWorld[SimulationGlobal.SimlessBuildMode] == 0
                    ? targetHouse
                    : (short)0);
            var promotedSession = new LotSession
            {
                House = targetHouse,
                Lot = promotedPreview.Lot,
                World = promotedWorld,
                ActiveFamilyChunkId = promotedPreview.ActiveFamilyChunkId,
                Away = promotedIsAway,
                HomeHouse = promotedHome,
                LastTick = updates,
                CommittedVacationState = promotedPreview.ActiveFamilyChunkId is { } promotedChunkId
                    ? (promotedChunkId, promotedPreview.ActiveFamilyField0x13C)
                    : null,
                LastRunningSpeed = 1,
                SaveDirty = promotedCatchUp,
                InitialObjectValuation = SumPlacedObjectValues(promotedWorld),
                InitialValuationLot = promotedPreview.Lot,
                LotValuationEdited = false,
            };
            neighborhoodManager.SyncFromWorld(promotedWorld, targetHouse, preserveOtherLotLocations: true, activeLotIds: CollectActivePoolLotIds());
            return promotedSession;
        }
        adjacentPreviewWorlds.Remove(targetHouse);

        var runtime = ReadSavedRuntimeMetadata(runtimePath, File.ReadAllBytes(path));
        registry = EnsureRegistry().WithRefreshedPeople(Path.Combine(userData, "Characters"));
        SimulationWorld? loadedWorldRef = null;
        var setup = WorldBootstrap.Load(
            game,
            path,
            language,
            () => ReferenceEquals(world, loadedWorldRef) ? speed == 0 : (lotPool.TryGetLive(targetHouse, out var s) && s.Speed == 0),
            targetHouse,
            userDataPath: userData,
            scoreAutonomy: ScoreAutonomyChoice,
            existingRegistry: registry,
            neighborhoodManager: neighborhoodManager,
            connectedNeighborhoodEnabled: true,
            preloadedNeighborhood: neighborhood);

        var loadedWorld = setup.World;
        loadedWorldRef = loadedWorld;
        if (ConnectedNeighborhoodEnabled && lotPool.HomeHouse > 0 && loadedWorld.ActiveLotNumber != lotPool.HomeHouse)
        {
            if (loadedWorld.NeighborhoodData.Length > 1 && loadedWorld.NeighborhoodData[1] < 3)
                loadedWorld.NeighborhoodData[1] = 3;
            loadedWorld[SimulationGlobal.IsTutorialHouseNoYes] = 0;
            loadedWorld.TutorialOwner = null;
            foreach (var dlg in loadedWorld.DialogRecords)
                if (dlg.State == 0) { dlg.State = 2; dlg.Visible = false; }
        }
        global ??= setup.Global;
        neighborhood ??= setup.Neighborhood;
        neighborhoodPath ??= setup.NeighborhoodPath;
        loadedWorld.AutonomyForced = world?.AutonomyForced;
        if (setup.Registry is not null)
        {
            registry = setup.Registry;
            loadedWorld.UiTextFile ??= registry.UiText;
        }
        HydrateTypeAttributesFromNeighborhood(loadedWorld, setup.Lot, setup.Neighborhood);
        if (setup.ActiveFamilyChunkId is null && !loadedWorld.WalkOrder.OfType<XPerson>().Any(p => p[PersonData.FamilyNumber] > 0))
            loadedWorld[SimulationGlobal.SimlessBuildMode] = 1;
        else if (setup.ActiveFamilyChunkId is not null)
            loadedWorld[SimulationGlobal.SimlessBuildMode] = 0;

        RestorePersonAnimationRuntime(loadedWorld, runtime.PersonAnimations);
        RestoreDialogRecords(loadedWorld, runtime.Dialogs);
        if (runtime.ClockTicks is { } freshClockTicks)
            loadedWorld.Clock.RestoreProgress(freshClockTicks, runtime.ClockDay ?? 0);
        SyncNeighborhoodRecordsIntoLiveWorld(loadedWorld, neighborhood, CollectGloballyRemovedNeighbors());
        SyncWorldPeopleRelationshipsFromNeighbors(loadedWorld);
        ConfigureNeighborLotControllerFromWorld(loadedWorld);
        var catchUpApplied = neighborLotController.ApplyToWorld(loadedWorld, targetHouse, neighborhoodManager);
        neighborLotController.SetLotActive(targetHouse, true);
        neighborControllerSeedDirty = true;
        AttachTutorialFlash(loadedWorld);
        AttachNoticeListener(loadedWorld);
        InitializeFameCallbackTarget(loadedWorld);
        AttachConnectedNeighborhoodHooks(loadedWorld);

        var isCommunityOrExpAway = IsCommunityOrExpansionAwayLot(userData, targetHouse);
        var isAway = isCommunityOrExpAway && loadedWorld[SimulationGlobal.SimlessBuildMode] == 0;
        var homeHouse = lotPool.HomeHouse > 0
            ? lotPool.HomeHouse
            : (setup.ActiveFamily?.HouseNumber is > 0 and <= short.MaxValue
                ? (short)setup.ActiveFamily.HouseNumber
                : (!isCommunityOrExpAway && loadedWorld[SimulationGlobal.SimlessBuildMode] == 0 ? targetHouse : (short)0));

        var session = new LotSession
        {
            House = targetHouse,
            World = loadedWorld,
            Lot = setup.Lot,
            ActiveFamilyChunkId = setup.ActiveFamilyChunkId,
            Away = isAway,
            HomeHouse = homeHouse,
            LastTick = updates,
            CommittedVacationState = setup.ActiveFamilyChunkId is { } chunkId
                ? (chunkId, setup.ActiveFamily?.Field0x13C ?? 0)
                : null,
            LastRunningSpeed = NormalizeSaveSpeed(runtime.Speed),
            SaveDirty = catchUpApplied,
            InitialObjectValuation = SumPlacedObjectValues(loadedWorld),
            InitialValuationLot = setup.Lot,
            LotValuationEdited = false,
        };
        neighborhoodManager.SyncFromWorld(loadedWorld, targetHouse, preserveOtherLotLocations: true, activeLotIds: CollectActivePoolLotIds());
        return session;
    }

    private HashSet<short>? CollectTransitProtectedHouses()
    {
        if (activeCrossLotTransits.Count == 0) return null;
        var protectedHouses = new HashSet<short>();
        foreach (var transit in activeCrossLotTransits.Values)
        {
            if (transit.SourceHouse > 0) protectedHouses.Add(transit.SourceHouse);
            if (transit.TargetHouse > 0) protectedHouses.Add(transit.TargetHouse);
        }
        return protectedHouses;
    }

    private void EnforceMaxLiveSplitLots() =>
        EnforceMaxLiveSplitLots(Math.Max(1, connectedOptions.MaxLiveSplitLots), lotPool.FocusedHouse);

    private void EnforceMaxLiveSplitLots(int maxLiveAllowed, short keepHouse)
    {
        var limit = Math.Max(0, maxLiveAllowed);
        var userData = Neighborhoods.UserData(profile);
        var protectedHouses = CollectTransitProtectedHouses();
        while (lotPool.LiveSessions.Count > limit)
        {
            var candidate = lotPool.SelectEvictionCandidate(keepHouse, lotPool.HomeHouse, protectedHouses);
            if (candidate is null) break;
            var dehydrated = DehydrateSession(candidate);
            lotPool.PutSaved(dehydrated);
            RegisterDehydratedLotInControllerIfInactiveResidential(candidate, userData);
            neighborControllerSeedDirty = true;
        }
    }

    private object FocusLotInternal(short targetHouse)
    {
        if (!ConnectedNeighborhoodEnabled)
            throw new InvalidOperationException("Connected neighborhood mode is disabled.");
        if (targetHouse <= 0)
            throw new ArgumentException("focusLot requires a positive house number.");

        var userData = Neighborhoods.UserData(profile);
        if (targetHouse == house && world is not null)
        {
            lotPool.FocusedHouse = targetHouse;
            if (!lotPool.TryGetLive(targetHouse, out _))
                SeedFocusedSessionIntoPool();
            neighborhoodManager.SyncFromWorld(world, targetHouse, activeLotIds: CollectActivePoolLotIds());
            return new
            {
                house,
                focusedHouse = lotPool.FocusedHouse,
                homeHouse = EffectiveHomeHouse,
                away = IsVisitingAwayLot,
                simless = world[SimulationGlobal.SimlessBuildMode] != 0,
            };
        }

        if (!lotPool.TryGetLive(targetHouse, out _) &&
            !lotPool.TryGetSaved(targetHouse, out _) &&
            !File.Exists(Path.Combine(userData, "Houses", $"House{targetHouse:D2}.iff")))
        {
            throw new FileNotFoundException($"Lot {targetHouse} not found.");
        }

        if (world is not null && lot is not null && house > 0)
            FlushFocusedLotSessionToPool();

        SyncLiveNeighborRecordsToNeighborhood();

        RemoveTileCursor();
        ResetSeasonalOutfits();
        audio?.Reset();
        refreshedPersonFiles.Clear();

        LotSession targetSession;
        var isFreshLoad = false;
        if (lotPool.TryGetLive(targetHouse, out var liveSession))
        {
            SyncNeighborhoodRecordsIntoLiveWorld(liveSession.World, neighborhood, CollectGloballyRemovedNeighbors());
            targetSession = liveSession;
        }
        else if (lotPool.TryGetSaved(targetHouse, out var savedSession))
        {
            EnforceMaxLiveSplitLots(Math.Max(1, connectedOptions.MaxLiveSplitLots) - 1, targetHouse);
            targetSession = HydrateSession(savedSession, userData);
        }
        else
        {
            EnforceMaxLiveSplitLots(Math.Max(1, connectedOptions.MaxLiveSplitLots) - 1, targetHouse);
            targetSession = LoadFreshSessionForPool(targetHouse, userData);
            isFreshLoad = true;
        }

        lotPool.PutLive(targetSession);
        lotPool.FocusedHouse = targetHouse;
        EnforceMaxLiveSplitLots();
        neighborControllerSeedDirty = true;

        ApplyLotSessionToServer(targetSession);
        if (lotPool.HomeHouse == 0 &&
            !targetSession.Away &&
            targetSession.World[SimulationGlobal.SimlessBuildMode] == 0 &&
            targetSession.HomeHouse > 0)
        {
            lotPool.HomeHouse = targetSession.HomeHouse;
        }
        if (isFreshLoad && world?.FamilyBudget is FamilyBudget fb && fb.ActiveFamily is { Members.Count: > 0 } activeFamily)
        {
            var expectedKeys = fb.RestrictedActivePersonKeys
                ?? activeFamily.Members.Select(static m => unchecked((int)m)).ToArray();
            if (!world.WalkOrder.OfType<XPerson>().Any(p => expectedKeys.Contains(p.TypeRecord?.Key ?? 0)))
            {
                EnsureSelectedHouseholdMember(activeFamily, LotWidth / 2, LotHeight / 2);
                selectedSim = DefaultSelectedSimId();
                SyncSelectedSimFlags(world, selectedSim);
                targetSession.SelectedSim = selectedSim;
            }
        }
        neighborhoodManager.SyncFromWorld(world!, house, activeLotIds: CollectActivePoolLotIds());
        ClampAwayLotEditingState();

        if (audio?.Enabled == true)
            audio.PlayLotAmbience(house);

        Write(new { type = "lotChanged", house, away = IsVisitingAwayLot });
        Export();
        Write(new { type = "state", state = State() });

        return new
        {
            house,
            focusedHouse = lotPool.FocusedHouse,
            homeHouse = EffectiveHomeHouse,
            away = IsVisitingAwayLot,
            simless = world is not null && world[SimulationGlobal.SimlessBuildMode] != 0,
        };
    }

    private void ClearConnectedNeighborhoodPoolOnClose()
    {
        activeCrossLotTransits.Clear();
        pendingAutonomousReturnHomes.Clear();
        globallyRemovedNeighbors.Clear();
        neighborLotController.Clear();
        lotPool.Clear();
        neighborhoodManager.RestoreSnapshot(null);
        neighborControllerSeedDirty = true;
        lastSeededNeighborhood = null;
        lastSeededSavedLotCount = -1;
        lastSeededLiveLotCount = -1;
    }

    private void AttachConnectedNeighborhoodHooks(SimulationWorld? targetWorld)
    {
        if (targetWorld is null) return;
        targetWorld.TryInterceptConnectedVisitorRemoval = ConnectedNeighborhoodEnabled
            ? person => TryQueueAutonomousVisitorReturnHome(targetWorld, person)
            : null;
        targetWorld.NotifyRemovedNeighborCleared = ConnectedNeighborhoodEnabled
            ? HandleLiveWorldRemovedNeighborCleared
            : null;
    }

    private void HandleLiveWorldRemovedNeighborCleared(short neighborId)
    {
        if (neighborId <= 0 || isClearingRemovedNeighbor) return;
        isClearingRemovedNeighbor = true;
        try
        {
            globallyRemovedNeighbors.Remove(neighborId);
            world?.ClearRemovedNeighbor(neighborId);
            foreach (var live in lotPool.LiveSessions.Values)
                live.World.ClearRemovedNeighbor(neighborId);
        }
        finally
        {
            isClearingRemovedNeighbor = false;
        }
    }

    private bool TryQueueAutonomousVisitorReturnHome(SimulationWorld sourceWorld, XPerson person)
    {
        if (!ConnectedNeighborhoodEnabled || !sourceWorld.Neighborhood.TrackingEnabled)
            return false;

        var nid = person[PersonData.NeighborId];
        if (nid <= 0)
            return false;

        var sourceHouse = sourceWorld.ActiveLotNumber > 0
            ? checked((short)sourceWorld.ActiveLotNumber)
            : (lotPool.LiveSessions.Values.FirstOrDefault(s => ReferenceEquals(s.World, sourceWorld))?.House ??
               (ReferenceEquals(sourceWorld, world) ? house : (short)0));
        var homeHouse = EffectiveHomeHouseValue();
        var simDetails = neighborhoodManager.GetSimDetails(nid);
        if (homeHouse <= 0 && simDetails is { HomeLotId: > 0 })
            homeHouse = checked((short)simDetails.HomeLotId);
        if (homeHouse <= 0 || sourceHouse <= 0 || sourceHouse == homeHouse)
            return false;

        var homeFam = HomeFamily();
        var guid = PersonGuid(person);
        var isActiveHouseholdSim = homeFam is { Members.Count: > 0 }
            ? (guid != 0 && homeFam.Members.Contains(guid))
            : (simDetails is not null && simDetails.HomeLotId == homeHouse);
        if (!isActiveHouseholdSim)
            return false;

        person.Flags &= ~ConnectedPendingRemovalFlag;
        if (!pendingAutonomousReturnHomes.Any(entry => entry.NeighborId == nid))
            pendingAutonomousReturnHomes.Add((sourceHouse, nid, homeHouse));
        return true;
    }

    private void ProcessPendingAutonomousReturnHomes()
    {
        if (ConnectedNeighborhoodEnabled && IsVisitingAwayLot && world is not null)
        {
            foreach (var dlg in world.DialogRecords)
            {
                if (dlg.State == 0 && string.Equals(dlg.Title, "Help System", StringComparison.OrdinalIgnoreCase))
                {
                    dlg.State = 2;
                    dlg.Visible = false;
                }
            }
        }

        if (!ConnectedNeighborhoodEnabled || pendingAutonomousReturnHomes.Count == 0)
            return;

        var pending = pendingAutonomousReturnHomes.ToArray();
        pendingAutonomousReturnHomes.Clear();

        var previousFocusedHouse = house;
        var userData = Path.GetDirectoryName(neighborhoodPath) ?? Neighborhoods.UserData(profile);
        var homeFam = HomeFamily();
        var homeGuids = homeFam?.Members.ToHashSet() ?? [];

        foreach (var (sourceHouse, nid, targetHomeHouse) in pending)
        {
            if (nid <= 0 || targetHomeHouse <= 0 || sourceHouse == targetHomeHouse)
                continue;

            PruneNeighborFromOtherCrossLotTransits(nid);
            activeCrossLotTransits.Remove(nid);

            LotSession? sourceSession = null;
            if (sourceHouse == house && world is not null && lot is not null)
            {
                if (!lotPool.TryGetLive(house, out sourceSession))
                {
                    SeedFocusedSessionIntoPool();
                    lotPool.TryGetLive(house, out sourceSession);
                }
            }
            else
            {
                lotPool.TryGetLive(sourceHouse, out sourceSession);
            }

            if (sourceSession is null)
                continue;

            var person = sourceSession.World.WalkOrder.OfType<XPerson>()
                .FirstOrDefault(p => p[PersonData.NeighborId] == nid);
            if (person is null)
                continue;

            person.Flags &= ~ConnectedPendingRemovalFlag;
            if (sourceHouse == house)
                FlushFocusedLotSessionToPool();

            neighborhoodManager.OnSimLotSwitched(nid, sourceHouse, sourceHouse, isVisiting: true, lotObjectId: person.Id);
            ExecuteReturnHomeTransfer([nid], targetHomeHouse, homeFam, homeGuids, userData);

            if (previousFocusedHouse > 0 &&
                previousFocusedHouse != sourceHouse &&
                previousFocusedHouse != targetHomeHouse &&
                (lotPool.TryGetLive(previousFocusedHouse, out _) || lotPool.TryGetSaved(previousFocusedHouse, out _)))
            {
                FocusLotInternal(previousFocusedHouse);
            }
        }
    }

    private void RegisterOpenedLotInPool()
    {
        if (!ConnectedNeighborhoodEnabled || world is null || lot is null || house <= 0) return;
        world.Neighborhood = neighborhoodManager;
        AttachConnectedNeighborhoodHooks(world);
        neighborhoodManager.TrackingEnabled = true;
        var userData = Neighborhoods.UserData(profile);
        if (neighborhoodManager.GetAllSimLocations().Count == 0)
            neighborhoodManager.SyncFromUserData(userData, language);
        SyncWorldPeopleRelationshipsFromNeighbors(world);
        neighborhoodManager.SyncFromWorld(world, house, activeLotIds: CollectActivePoolLotIds());
        SeedFocusedSessionIntoPool();
        ConfigureNeighborLotControllerFromWorld(world);
        neighborLotController.ApplyToWorld(world, house, neighborhoodManager);
        neighborControllerSeedDirty = true;
        EnsureNeighborLotControllerSeeded();
        if (world is not null && lot is not null && house > 0 && connectedOptions.EnableAdjacentLotRendering)
            PrewarmAdjacentNeighborhoodParallel();
    }

    private void TickConnectedNeighborhoodSessions()
    {
        if (!ConnectedNeighborhoodEnabled || world is null) return;

        TickCrossLotTransits();

        var activeLotIds = CollectActivePoolLotIds();
        if (lotPool.LiveSessions.Count > 1)
        {
            var control = new SimulationClockControl(1, 1, 1);
            foreach (var session in lotPool.LiveSessions.Values.ToArray())
            {
                if (session.House == house ||
                    session.Speed == 0 ||
                    session.World[SimulationGlobal.SimlessBuildMode] != 0 ||
                    HasBlockingDialog(session.World) ||
                    session.World.PendingLotSwitch is not null)
                {
                    continue;
                }

                try
                {
                    session.World.AdvanceSimulationTick(control, 0, session.World.Clock.Ticks);
                    ProcessPendingAutonomousReturnHomes();
                    session.LastTick = updates + 1;
                    neighborhoodManager.SyncFromWorld(
                        session.World,
                        session.House,
                        preserveOtherLotLocations: true,
                        activeLotIds: activeLotIds);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    session.Speed = 0;
                    Console.Error.WriteLine($"Connected neighborhood background tick paused for house {session.House}: {ex.Message}");
                }
            }
        }

        if (connectedOptions.EnableStoryProgression)
        {
            EnsureNeighborLotControllerSeeded();
            neighborLotController.TickNeighborhood(
                1,
                world.Clock.Hour,
                world.Clock.Minute,
                world.Clock.Day,
                neighborhoodManager);
        }

        TickAdjacentPreviewWorlds();
        TickSharedMorningPaperDelivery();

        if (house > 0)
        {
            neighborhoodManager.SyncFromWorld(
                world,
                house,
                preserveOtherLotLocations: false,
                activeLotIds: activeLotIds);
        }
    }

    private bool TryApplyConnectedPendingLotSwitch(SimulationWorld sourceWorld, SimulationWorld.LotSwitchRequest request)
    {
        var userData = Neighborhoods.UserData(profile);
        var (travellingFamilyChunkId, travellingFamily) = ResolveTravellingFamily(sourceWorld, request);
        var isReturnHome = request.RawTargetHouse == -1;
        var travellerHomeHouse = travellingFamily?.HouseNumber is > 0 and <= short.MaxValue
            ? (short)travellingFamily.HouseNumber
            : (short)0;
        var resolvedHome = travellerHomeHouse > 0
            ? travellerHomeHouse
            : (lotPool.HomeHouse > 0
                ? lotPool.HomeHouse
                : (suspendedHomeLot?.HomeHouse ?? (short)0));
        var targetHouse = isReturnHome ? resolvedHome : request.RawTargetHouse;
        if (targetHouse <= 0) return false;

        var destPath = Path.Combine(userData, "Houses", $"House{targetHouse:D2}.iff");
        var hasCachedTarget = lotPool.TryGetLive(targetHouse, out var existingLive) ||
                              lotPool.TryGetSaved(targetHouse, out _);
        if (!hasCachedTarget && !File.Exists(destPath)) return false;

        var sourceHouse = house;
        var traveller = request.PersonGuid != 0
            ? sourceWorld.WalkOrder.OfType<XPerson>().FirstOrDefault(p => p.TypeRecord?.Key == request.PersonGuid)
            : selectedSim != 0 ? sourceWorld.WalkOrder.OfType<XPerson>().FirstOrDefault(p => p.Id == selectedSim) : null;
        var travellerNeighborId = traveller is not null ? traveller[PersonData.NeighborId] : (short)0;
        var sourceStateClass = sourceWorld.StateClass(sourceHouse);
        var destinationStateClass = sourceWorld.StateClass(targetHouse);
        var lotTransitInfo = sourceWorld[SimulationGlobal.LotTransitInfo];
        var fameScore = traveller is not null ? (int)traveller[PersonData.FameScore] : 0;
        var jobType = traveller is not null ? (int)traveller[PersonData.JobType] : 0;
        var away = !isReturnHome && IsAwayLotForPool(userData, targetHouse, resolvedHome > 0 ? resolvedHome : sourceHouse);

        Write(new
        {
            type = "lotSwitching",
            fromHouse = sourceHouse,
            house = targetHouse,
            away,
            returnHome = isReturnHome,
            sourceStateClass,
            destinationStateClass,
            lotTransitInfo,
            fameScore,
            jobType,
        });

        foreach (var person in sourceWorld.WalkOrder.OfType<XPerson>())
        {
            if (person[PersonData.NeighborId] > 0) sourceWorld.SavePersonRecord(person);
        }
        PersistNeighborhoodStateImmediately();

        int? pendingVacationField = null;
        if (travellingFamilyChunkId is not null)
        {
            if (!isReturnHome && targetHouse is >= 40 and <= 49)
                pendingVacationField = targetHouse;
            else if (isReturnHome)
                pendingVacationField = 0;
        }

        var sourceSpeed = speed > 0 ? speed : lastRunningSpeed;
        if (!lotPool.TryGetLive(sourceHouse, out _))
            SeedFocusedSessionIntoPool();
        else
            FlushFocusedLotSessionToPool();

        SyncLiveNeighborRecordsToNeighborhood();

        if (lotPool.HomeHouse == 0 && !isReturnHome && !IsAwayLot(userData, sourceHouse))
            lotPool.HomeHouse = sourceHouse;

        var sourceSlots = new Dictionary<int, short>(sourceWorld.PersonTypeRecordSlots);
        var clonedScopes = sourceWorld.InteractionScopes.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        var (arrivalHour, arrivalMinute) = ConsumeArrivalClock(clonedScopes, targetHouse);
        var travelScopeKeys = CollectTravelScopeKeys(sourceSlots, clonedScopes);

        RemoveTileCursor();
        ResetSeasonalOutfits();
        audio?.Reset();
        refreshedPersonFiles.Clear();

        registry = registry?.WithRefreshedPeople(Path.Combine(userData, "Characters")) ?? registry;

        LotSession targetSession;
        bool targetWasRestored;
        DehydratedLotSession? dehydratedRestored = null;
        if (existingLive is not null)
        {
            SyncNeighborhoodRecordsIntoLiveWorld(existingLive.World, neighborhood, CollectGloballyRemovedNeighbors());
            targetSession = existingLive;
            targetWasRestored = true;
        }
        else if (lotPool.TryGetSaved(targetHouse, out dehydratedRestored))
        {
            EnforceMaxLiveSplitLots(Math.Max(1, connectedOptions.MaxLiveSplitLots) - 1, targetHouse);
            targetSession = HydrateSession(dehydratedRestored, userData);
            targetWasRestored = true;
        }
        else
        {
            EnforceMaxLiveSplitLots(Math.Max(1, connectedOptions.MaxLiveSplitLots) - 1, targetHouse);
            SimulationWorld? loadedWorldRef = null;
            Func<bool> sessionIsPaused = () =>
                ReferenceEquals(world, loadedWorldRef)
                    ? speed == 0
                    : (lotPool.TryGetLive(targetHouse, out var s) && s.Speed == 0);
            var setup = WorldBootstrap.Load(
                game,
                destPath,
                language,
                sessionIsPaused,
                targetHouse,
                autonomyForced: sourceWorld.AutonomyForced ?? false,
                activeFamilyChunkId: travellingFamilyChunkId,
                userDataPath: userData,
                scoreAutonomy: ScoreAutonomyChoice,
                existingRegistry: registry,
                neighborhoodManager: neighborhoodManager,
                connectedNeighborhoodEnabled: true,
                preloadedNeighborhood: neighborhood);
            loadedWorldRef = setup.World;
            registry = setup.Registry ?? registry;
            neighborhoodPath ??= setup.NeighborhoodPath;
            neighborhood ??= setup.Neighborhood;
            HydrateTypeAttributesFromNeighborhood(setup.World, setup.Lot, setup.Neighborhood);
            SyncNeighborhoodRecordsIntoLiveWorld(setup.World, neighborhood, CollectGloballyRemovedNeighbors());
            SyncWorldPeopleRelationshipsFromNeighbors(setup.World);
            ConfigureNeighborLotControllerFromWorld(setup.World);
            var catchUpApplied = neighborLotController.ApplyToWorld(setup.World, targetHouse, neighborhoodManager);
            neighborLotController.SetLotActive(targetHouse, true);
            neighborControllerSeedDirty = true;
            targetSession = new LotSession
            {
                House = targetHouse,
                World = setup.World,
                Lot = setup.Lot,
                ActiveFamilyChunkId = setup.ActiveFamilyChunkId,
                Away = away,
                HomeHouse = resolvedHome > 0 ? resolvedHome : (lotPool.HomeHouse > 0 ? lotPool.HomeHouse : sourceHouse),
                LastTick = updates,
                CommittedVacationState = setup.ActiveFamilyChunkId is { } loadedChunkId
                    ? (loadedChunkId, setup.ActiveFamily?.Field0x13C ?? 0)
                    : null,
                SaveDirty = catchUpApplied,
                InitialObjectValuation = SumPlacedObjectValues(setup.World),
                InitialValuationLot = setup.Lot,
                LotValuationEdited = false,
            };
            targetWasRestored = false;
        }

        house = targetHouse;
        lot = targetSession.Lot;
        world = targetSession.World;
        world.Neighborhood = neighborhoodManager;
        AttachConnectedNeighborhoodHooks(world);
        world[SimulationGlobal.SimlessBuildMode] = 0;
        world.AutonomyForced = sourceWorld.AutonomyForced;
        activeFamilyChunkId = targetSession.ActiveFamilyChunkId ?? travellingFamilyChunkId;

        if (travellingFamilyChunkId is { } resolvedFamilyChunkId && pendingVacationField is { } nextVacField)
            SetInMemoryFamilyVacationField(world, resolvedFamilyChunkId, nextVacField);

        if (!isReturnHome && targetHouse is >= 40 and <= 49)
        {
            var targetActiveFamily = (world.FamilyBudget as FamilyBudget)?.ActiveFamily;
            var expectedFamilyNumber = targetActiveFamily?.FamilyNumber is > 0
                ? targetActiveFamily.FamilyNumber
                : (travellingFamilyChunkId is { } chunkIdNum ? unchecked((short)chunkIdNum) : 0);
            var allowedKeys = new HashSet<int>(travelScopeKeys);
            if (targetActiveFamily is { Members.Count: > 0 } famMembers)
                foreach (var memberGuid in famMembers.Members) allowedKeys.Add(unchecked((int)memberGuid));
            if (request.PersonGuid != 0) allowedKeys.Add(request.PersonGuid);
            var removedStale = false;
            foreach (var loadedPerson in world.WalkOrder.OfType<XPerson>().ToArray())
            {
                var pFamily = loadedPerson[PersonData.FamilyNumber];
                var pGuid = loadedPerson.TypeRecord?.Key ?? 0;
                if (pFamily > 0 && expectedFamilyNumber > 0 && pFamily != expectedFamilyNumber && !allowedKeys.Contains(pGuid))
                {
                    world.Remove(loadedPerson.Id);
                    removedStale = true;
                }
            }
            if (removedStale) world.RefreshNeighborInteractionManager();
        }

        if (registry is not null)
            world.UiTextFile ??= registry.UiText;
        AttachTutorialFlash(world);
        ClampAwayLotEditingState(away);

        if (!isReturnHome && request.PersonGuid != 0 && world.FamilyBudget is FamilyBudget awayBudget)
        {
            var restrictedKeys = new List<int> { request.PersonGuid };
            var familyMembers = awayBudget.ActiveFamily?.Members;
            if (familyMembers is not null)
            {
                foreach (var key in travelScopeKeys)
                {
                    if (key != request.PersonGuid &&
                        familyMembers.Contains(unchecked((uint)key)) &&
                        !restrictedKeys.Contains(key))
                    {
                        restrictedKeys.Add(key);
                    }
                }
            }
            awayBudget.RestrictActivePersonKeys(restrictedKeys);
            targetSession.RestrictedActivePersonKeys = restrictedKeys.ToArray();
        }
        else if (isReturnHome)
        {
            targetSession.RestrictedActivePersonKeys = null;
        }

        if (!isReturnHome)
        {
            if (world.ActivePersonManagerPresent)
            {
                foreach (var key in world.ActivePersonRecordKeys)
                {
                    if (key != 0) world.ScopedNeighborPersonRecordId(key);
                }
            }
            foreach (var key in travelScopeKeys)
            {
                if (key != 0 && key != request.PersonGuid)
                    world.ScopedNeighborPersonRecordId(key);
            }
        }
        else
        {
            foreach (var person in world.WalkOrder.OfType<XPerson>())
                SyncPersonFromNeighborRecord(world, person);
            InstantiateMissingFamilyMembersOnReturnHome(world, (world.FamilyBudget as FamilyBudget)?.ActiveFamily);
        }

        TransferScopes(world, sourceSlots, clonedScopes);

        var updatedTravelerIds = new HashSet<short>();
        foreach (var person in world.WalkOrder.OfType<XPerson>())
        {
            if (ApplyTransferredScopeState(world, person))
                updatedTravelerIds.Add(person.Id);
        }

        if (isReturnHome && dehydratedRestored is not null)
        {
            var travelerKeySet = new HashSet<int>(travelScopeKeys);
            if (request.PersonGuid != 0) travelerKeySet.Add(request.PersonGuid);
            var animationsToRestore = dehydratedRestored.Runtime.PersonAnimations?
                .Where(state => world.ObjectById(state.PersonId) is XPerson person &&
                                !updatedTravelerIds.Contains(person.Id) &&
                                !(person.TypeRecord?.Key is { } key && travelerKeySet.Contains(key)))
                .ToArray();
            RestorePersonAnimationRuntime(world, animationsToRestore);
            RestoreDialogRecords(world, dehydratedRestored.Runtime.Dialogs);
        }

        if (arrivalHour is { } h && arrivalMinute is { } m)
        {
            world.Clock.Restore(h, m, 0);
            world[SimulationGlobal.Hour] = checked((short)world.Clock.Hour);
            world[SimulationGlobal.Minute] = checked((short)world.Clock.Minute);
            world[SimulationGlobal.Second] = checked((short)world.Clock.Second);
        }

        ClearBuildHistory();
        ResetLotValuationBaseline();
        ClearCutaway();
        viewerMode = 0;
        viewLevel = 1;
        tileCursor = null;
        pieHead = null;
        saveDirty = targetWasRestored && targetSession.SaveDirty;
        committedVacationState = targetWasRestored
            ? targetSession.CommittedVacationState
            : (activeFamilyChunkId is { } vacChunkId
                ? (vacChunkId, (world.FamilyBudget as FamilyBudget)?.ActiveFamily?.Field0x13C ?? 0)
                : null);
        simulationError = null;
        if (viewerScale != 0) world[SimulationGlobal.ZoomLevelSML] = viewerScale;
        world[SimulationGlobal.Mode] = viewerMode;
        ClampAwayLotEditingState(away);
        InitializeFameCallbackTarget(world);
        if (!targetWasRestored)
            AttachNoticeListener(world);
        if (world.AutonomyHost is null && global is not null)
        {
            var autonomyWorld = world;
            world.AutonomyHost = new AutonomyHost(
                world,
                global,
                house,
                () => ReferenceEquals(world, autonomyWorld) ? speed == 0 : (lotPool.TryGetLive(targetHouse, out var s) && s.Speed == 0),
                ScoreAutonomyChoice);
        }

        selectedSim = request.PersonGuid != 0
            ? (PanelPeople().FirstOrDefault(p => p.TypeRecord?.Key == request.PersonGuid)?.Id ?? DefaultSelectedSimId())
            : DefaultSelectedSimId();
        SyncSelectedSimFlags(world, selectedSim);
        autoSpeedSaved = -1;

        targetSession.Away = away;
        targetSession.HomeHouse = resolvedHome > 0 ? resolvedHome : (lotPool.HomeHouse > 0 ? lotPool.HomeHouse : targetSession.HomeHouse);
        targetSession.SelectedSim = selectedSim;
        targetSession.InitialObjectValuation = initialObjectValuation;
        targetSession.InitialValuationLot = initialValuationLot;
        targetSession.LotValuationEdited = lotValuationEdited;
        lotPool.PutLive(targetSession);
        lotPool.FocusedHouse = targetHouse;
        EnforceMaxLiveSplitLots();
        neighborControllerSeedDirty = true;

        var activeLotIds = CollectActivePoolLotIds();
        var arrivedPerson = travellerNeighborId > 0
            ? world.WalkOrder.OfType<XPerson>().FirstOrDefault(p => p[PersonData.NeighborId] == travellerNeighborId)
            : null;
        if (travellerNeighborId > 0 && arrivedPerson is not null)
        {
            neighborhoodManager.OnSimLotSwitched(travellerNeighborId, sourceHouse, targetHouse, isVisiting: away, lotObjectId: arrivedPerson.Id);
            neighborhoodManager.SyncFromWorld(world, targetHouse, preserveOtherLotLocations: false, activeLotIds: activeLotIds);
        }
        else
        {
            neighborhoodManager.SyncFromWorld(world, targetHouse, preserveOtherLotLocations: false, activeLotIds: activeLotIds);
            if (travellerNeighborId > 0)
                neighborhoodManager.OnSimLotSwitched(travellerNeighborId, sourceHouse, targetHouse, isVisiting: away, lotObjectId: null);
        }

        var resumeSpeed = sourceSpeed > 0 ? sourceSpeed : 1;
        lastRunningSpeed = resumeSpeed;
        SetSimulationSpeed(resumeSpeed);
        audio?.PlayLotAmbience(house);

        Write(new { type = "lotChanged", house, away });
        Write(new { type = "environment", environment = plugins?.Environment(house) });
        Export();
        Write(new { type = "state", state = State() });
        return true;
    }

    private static bool ShouldFlushPooledLotToDisk(string userData, short targetHouse, bool away, bool simlessBuild, ushort? familyChunkId)
    {
        if (away) return false;
        if (simlessBuild) return true;
        var isCommunityOrAway = targetHouse is >= 20 and <= 30 or >= 81 and <= 89 or >= 93 and <= 98 ||
            (targetHouse is >= 40 and <= 49
                ? familyChunkId is null or 0
                : Neighborhoods.IsCommunityLot(userData, targetHouse));
        return !isCommunityOrAway;
    }

    private static bool IsDehydratedSimlessBuildMode(DehydratedLotSession saved)
    {
        if (saved.Away) return false;
        try
        {
            var iff = new IffFile(saved.LotBytes, $"House{saved.House:D2}.iff");
            if (iff.OfType(ResourceType.Parse("SIMI")).FirstOrDefault() is { } simiChunk)
            {
                var simi = SimulationState.Read(iff, simiChunk);
                if (simi.Globals.Count > (int)SimulationGlobal.SimlessBuildMode)
                    return simi.Globals[(int)SimulationGlobal.SimlessBuildMode] != 0;
            }
        }
        catch
        {
        }
        return saved.FamilyChunkId is null or 0;
    }

    private ConnectedNeighborhoodSaveState? CaptureConnectedNeighborhoodSaveState(bool saveLot)
    {
        if (!ConnectedNeighborhoodEnabled) return null;

        if (world is not null && lot is not null && house > 0)
        {
            FlushFocusedLotSessionToPool();
            neighborhoodManager.SyncFromWorld(world, house, activeLotIds: CollectActivePoolLotIds());
        }

        var savedLots = new Dictionary<short, ConnectedNeighborhoodSaveLot>();
        var userData = Neighborhoods.UserData(profile);
        var homeGuids = HomeFamily()?.Members.ToHashSet();

        foreach (var session in lotPool.LiveSessions.Values)
        {
            if (session.House == house) continue;
            var dehydrated = DehydrateSession(session);
            var persistedSaveDirty = dehydrated.SaveDirty;
            var isSimlessBuild = session.World[SimulationGlobal.SimlessBuildMode] != 0;
            if (saveLot && dehydrated.SaveDirty &&
                ShouldFlushPooledLotToDisk(userData, session.House, dehydrated.Away, isSimlessBuild, dehydrated.FamilyChunkId))
            {
                var housePath = Path.Combine(userData, "Houses", $"House{session.House:D2}.iff");
                Directory.CreateDirectory(Path.GetDirectoryName(housePath)!);
                WriteReplace(housePath, dehydrated.LotBytes);
                WriteReplace(CanonicalRuntimePath(housePath), JsonSerializer.SerializeToUtf8Bytes(dehydrated.Runtime, Json));
                session.SaveDirty = false;
                persistedSaveDirty = false;
            }
            if (!isSimlessBuild &&
                !IsCommunityOrExpansionAwayLot(userData, session.House) &&
                session.World.StateClass(session.House) != 2)
            {
                var exclude = session.House == EffectiveHomeHouse ? null : homeGuids;
                neighborLotController.RegisterFromWorld(
                    session.House,
                    session.World,
                    unchecked((short)(session.ActiveFamilyChunkId ?? 0)),
                    excludeGuids: exclude);
                neighborLotController.SetLotActive(session.House, true);
            }
            savedLots[session.House] = new ConnectedNeighborhoodSaveLot(
                dehydrated.House,
                dehydrated.FamilyChunkId,
                Convert.ToBase64String(dehydrated.LotBytes),
                dehydrated.Runtime,
                dehydrated.Speed,
                dehydrated.Away,
                dehydrated.HomeHouse,
                dehydrated.RestrictedActivePersonKeys,
                dehydrated.LastTick,
                dehydrated.SelectedSim,
                SaveDirty: persistedSaveDirty,
                CommittedVacationFamilyChunkId: dehydrated.CommittedVacationState?.FamilyChunkId,
                CommittedVacationHouse: dehydrated.CommittedVacationState?.VacationHouse,
                ViewLevel: dehydrated.ViewLevel,
                ViewerMode: dehydrated.ViewerMode,
                RoofsVisible: dehydrated.RoofsVisible,
                EditingView: dehydrated.EditingView,
                DoorWindowBuildToolActive: dehydrated.DoorWindowBuildToolActive,
                CutawayTileX: dehydrated.CutawayTile?.X,
                CutawayTileY: dehydrated.CutawayTile?.Y,
                CutawayRooms: dehydrated.CutawayRooms,
                LastRunningSpeed: dehydrated.LastRunningSpeed,
                ClockTicks: dehydrated.ClockTicks,
                ClockDay: dehydrated.ClockDay);
        }

        var globallyRemoved = CollectGloballyRemovedNeighbors();
        if (world is not null)
            SyncNeighborhoodRecordsIntoLiveWorld(world, neighborhood, globallyRemoved);
        Dictionary<short, int>? flushedBudgets = null;
        if (neighborhood is not null)
            neighborhood = neighborLotController.FlushToNeighborhood(neighborhood, out _, flushBudgets: saveLot, out flushedBudgets);

        foreach (var savedEntry in lotPool.SavedLotStates.Values.ToArray())
        {
            var saved = savedEntry;
            if (saved.House == house || savedLots.ContainsKey(saved.House)) continue;
            if (flushedBudgets is not null &&
                flushedBudgets.TryGetValue(saved.House, out var flushedDelta) &&
                flushedDelta != 0 &&
                saved.Runtime.Budget is { } oldBudget)
            {
                saved = saved with { Runtime = saved.Runtime with { Budget = oldBudget + flushedDelta } };
                lotPool.PutSaved(saved);
            }
            var persistedSaveDirty = saved.SaveDirty;
            if (saveLot && saved.SaveDirty &&
                ShouldFlushPooledLotToDisk(userData, saved.House, saved.Away, IsDehydratedSimlessBuildMode(saved), saved.FamilyChunkId))
            {
                var housePath = Path.Combine(userData, "Houses", $"House{saved.House:D2}.iff");
                Directory.CreateDirectory(Path.GetDirectoryName(housePath)!);
                WriteReplace(housePath, saved.LotBytes);
                WriteReplace(CanonicalRuntimePath(housePath), JsonSerializer.SerializeToUtf8Bytes(saved.Runtime, Json));
                lotPool.PutSaved(saved with { SaveDirty = false });
                persistedSaveDirty = false;
            }
            savedLots[saved.House] = new ConnectedNeighborhoodSaveLot(
                saved.House,
                saved.FamilyChunkId,
                Convert.ToBase64String(saved.LotBytes),
                saved.Runtime,
                saved.Speed,
                saved.Away,
                saved.HomeHouse,
                saved.RestrictedActivePersonKeys,
                saved.LastTick,
                saved.SelectedSim,
                SaveDirty: persistedSaveDirty,
                CommittedVacationFamilyChunkId: saved.CommittedVacationState?.FamilyChunkId,
                CommittedVacationHouse: saved.CommittedVacationState?.VacationHouse,
                ViewLevel: saved.ViewLevel,
                ViewerMode: saved.ViewerMode,
                RoofsVisible: saved.RoofsVisible,
                EditingView: saved.EditingView,
                DoorWindowBuildToolActive: saved.DoorWindowBuildToolActive,
                CutawayTileX: saved.CutawayTile?.X,
                CutawayTileY: saved.CutawayTile?.Y,
                CutawayRooms: saved.CutawayRooms,
                LastRunningSpeed: saved.LastRunningSpeed,
                ClockTicks: saved.ClockTicks,
                ClockDay: saved.ClockDay);
        }

        return new ConnectedNeighborhoodSaveState(
            connectedOptions.Clone(),
            lotPool.FocusedHouse > 0 ? lotPool.FocusedHouse : house,
            lotPool.HomeHouse > 0 ? lotPool.HomeHouse : EffectiveHomeHouse,
            savedLots,
            neighborhoodManager.CaptureSnapshot(),
            FocusedAway: IsVisitingAwayLot,
            StoryProgression: neighborLotController.CaptureSnapshot(),
            RemovedNeighbors: globallyRemoved.Count > 0 ? globallyRemoved.Keys.Order().ToArray() : null,
            RemovedNeighborGuids: globallyRemoved.Count > 0 ? new Dictionary<short, uint>(globallyRemoved) : null);
    }

    private void RestoreConnectedNeighborhoodFromEnvelope(SaveEnvelope? envelope, string userData)
    {
        lotPool.Clear();
        activeCrossLotTransits.Clear();
        pendingAutonomousReturnHomes.Clear();
        globallyRemovedNeighbors.Clear();
        neighborLotController.Clear();
        if (envelope?.ConnectedNeighborhood is { } cnState)
        {
            connectedOptions = cnState.Options.Clone();
            connectedOptions.Normalize();
            neighborhoodManager.TrackingEnabled = connectedOptions.Enabled;
            neighborLotController.RestoreSnapshot(cnState.StoryProgression);
            if (cnState.RemovedNeighbors is { Count: > 0 })
            {
                foreach (var remId in cnState.RemovedNeighbors)
                    globallyRemovedNeighbors[remId] = 0u;
            }
            if (cnState.RemovedNeighborGuids is { Count: > 0 })
            {
                foreach (var (remId, remGuid) in cnState.RemovedNeighborGuids)
                    globallyRemovedNeighbors[remId] = remGuid;
            }

            if (cnState.Neighborhood is not null)
                neighborhoodManager.RestoreSnapshot(cnState.Neighborhood);
            else
                neighborhoodManager.SyncFromUserData(userData, language);

            lotPool.FocusedHouse = cnState.FocusedHouse > 0 ? cnState.FocusedHouse : house;
            lotPool.HomeHouse = cnState.HomeHouse;

            foreach (var (lotHouse, savedLot) in cnState.Lots)
            {
                if (lotHouse <= 0 || lotHouse == house) continue;
                (ushort FamilyChunkId, int VacationHouse)? committedVac =
                    savedLot.CommittedVacationFamilyChunkId is { } vacFamId && savedLot.CommittedVacationHouse is { } vacHouse
                        ? (vacFamId, vacHouse)
                        : null;
                (int X, int Y)? savedCutawayTile =
                    savedLot.CutawayTileX is { } cutX && savedLot.CutawayTileY is { } cutY
                        ? (cutX, cutY)
                        : null;
                lotPool.PutSaved(new DehydratedLotSession(
                    lotHouse,
                    savedLot.FamilyChunkId,
                    Convert.FromBase64String(savedLot.LotState),
                    savedLot.Runtime,
                    savedLot.Speed,
                    savedLot.Away,
                    savedLot.HomeHouse,
                    savedLot.RestrictedActivePersonKeys,
                    savedLot.LastTick,
                    savedLot.SelectedSim,
                    SaveDirty: savedLot.SaveDirty,
                    CommittedVacationState: committedVac,
                    ViewLevel: savedLot.ViewLevel,
                    ViewerMode: savedLot.ViewerMode,
                    RoofsVisible: savedLot.RoofsVisible,
                    EditingView: savedLot.EditingView,
                    DoorWindowBuildToolActive: savedLot.DoorWindowBuildToolActive,
                    CutawayTile: savedCutawayTile,
                    CutawayRooms: savedLot.CutawayRooms,
                    LastRunningSpeed: NormalizeSaveSpeed(savedLot.LastRunningSpeed > 0 ? savedLot.LastRunningSpeed : savedLot.Runtime.Speed),
                    ClockTicks: savedLot.ClockTicks > 0 ? savedLot.ClockTicks : (savedLot.Runtime.ClockTicks ?? 0),
                    ClockDay: savedLot.ClockDay > 0 ? savedLot.ClockDay : (savedLot.Runtime.ClockDay ?? 0)));
            }

            if (world is not null && lot is not null && house > 0)
            {
                world.Neighborhood = neighborhoodManager;
                AttachConnectedNeighborhoodHooks(world);
                SyncNeighborhoodRecordsIntoLiveWorld(world, neighborhood, globallyRemovedNeighbors);
                SyncWorldPeopleRelationshipsFromNeighbors(world);
                var restrictedKeys = (world.FamilyBudget as FamilyBudget)?.RestrictedActivePersonKeys;
                var restoredAway = cnState.FocusedAway ??
                    (suspendedHomeLot is not null ||
                     restrictedKeys is { Count: > 0 } ||
                     (IsCommunityOrExpansionAwayLot(userData, house) && world[SimulationGlobal.SimlessBuildMode] == 0));
                SeedFocusedSessionIntoPool(
                    awayOverride: restoredAway,
                    homeHouseOverride: cnState.HomeHouse);
                if (ConnectedNeighborhoodEnabled)
                    ReconcileSuspendedHomeLotIntoPool();
                if (world.ObjectById(selectedSim) is not XPerson sel || !IsHouseholdPerson(sel))
                {
                    selectedSim = DefaultSelectedSimId();
                    SyncSelectedSimFlags(world, selectedSim);
                    if (lotPool.TryGetLive(house, out var focusedSession))
                        focusedSession.SelectedSim = selectedSim;
                }
                ClampAwayLotEditingState();
                neighborhoodManager.SyncFromWorld(world, house, activeLotIds: CollectActivePoolLotIds());
                ConfigureNeighborLotControllerFromWorld(world);
                neighborLotController.SetLotActive(house, true);
            }
            neighborControllerSeedDirty = true;
            lastSeededNeighborhood = null;
            if (ConnectedNeighborhoodEnabled)
                EnsureNeighborLotControllerSeeded();
            return;
        }

        if (ConnectedNeighborhoodEnabled)
        {
            neighborhoodManager.TrackingEnabled = true;
            neighborhoodManager.SyncFromUserData(userData, language, resetLocations: true);
            ReconcileSuspendedHomeLotIntoPool();
            if (world is not null && lot is not null && house > 0)
            {
                world.Neighborhood = neighborhoodManager;
                AttachConnectedNeighborhoodHooks(world);
                SyncNeighborhoodRecordsIntoLiveWorld(world, neighborhood, globallyRemovedNeighbors);
                SyncWorldPeopleRelationshipsFromNeighbors(world);
                neighborhoodManager.SyncFromWorld(world, house, activeLotIds: CollectActivePoolLotIds());
                SeedFocusedSessionIntoPool();
                ConfigureNeighborLotControllerFromWorld(world);
                neighborLotController.SetLotActive(house, true);
            }
            neighborControllerSeedDirty = true;
            lastSeededNeighborhood = null;
            EnsureNeighborLotControllerSeeded();
        }
    }

    private object AppendConnectedNeighborhoodToState(object vanillaPayload)
    {
        if (JsonSerializer.SerializeToNode(vanillaPayload, Json) is not JsonObject root)
            return vanillaPayload;

        var activeLots = new JsonArray();
        var seenHouses = new HashSet<short>();

        foreach (var session in lotPool.LiveSessions.Values.OrderBy(s => s.House))
        {
            seenHouses.Add(session.House);
            activeLots.Add(BuildActiveLotJson(session.House, isLive: true, session.Away, session.World));
        }

        foreach (var saved in lotPool.SavedLotStates.Values.OrderBy(s => s.House))
        {
            if (!seenHouses.Add(saved.House)) continue;
            activeLots.Add(BuildActiveLotJson(saved.House, isLive: false, saved.Away, targetWorld: null));
        }

        if (house > 0 && seenHouses.Add(house))
        {
            activeLots.Add(BuildActiveLotJson(house, isLive: true, IsVisitingAwayLot, world));
        }

        var focusedHouseVal = lotPool.FocusedHouse > 0 ? lotPool.FocusedHouse : house;
        var layoutPlan = connectedOptions.EnableAdjacentLotRendering && focusedHouseVal > 0
            ? ComputeCurrentNeighborhoodLayout(focusedHouseVal)
            : null;
        var layoutJson = new JsonArray();
        var roadStripsJson = new JsonArray();
        var adjacentLotsJson = new JsonArray();
        if (layoutPlan is not null)
        {
            foreach (var placement in layoutPlan.AdjacentLots)
            {
                layoutJson.Add(new JsonObject
                {
                    ["house"] = (int)placement.House,
                    ["offsetX"] = placement.TileOffsetX,
                    ["offsetY"] = placement.TileOffsetY,
                    ["roadEdge"] = placement.RoadEdge,
                    ["rotation"] = placement.Rotation,
                    ["lotName"] = placement.LotName,
                    ["familyName"] = placement.FamilyName is { Length: > 0 } fn ? JsonValue.Create(fn) : null,
                    ["occupied"] = placement.Occupied,
                });

                adjacentSceneCache.TryGetValue(placement.House, out var cachedScene);
                bool isAdjLive = lotPool.TryGetLive(placement.House, out _);
                adjacentLotsJson.Add(new JsonObject
                {
                    ["house"] = (int)placement.House,
                    ["offsetX"] = placement.TileOffsetX,
                    ["offsetY"] = placement.TileOffsetY,
                    ["lotName"] = placement.LotName,
                    ["familyName"] = placement.FamilyName is { Length: > 0 } afn ? JsonValue.Create(afn) : null,
                    ["occupied"] = placement.Occupied,
                    ["isLive"] = isAdjLive,
                    ["scenePath"] = cachedScene is not null &&
                                    cachedScene.CameraOrientation == cameraOrientation &&
                                    string.Equals(cachedScene.WallMode, wallMode, StringComparison.Ordinal) &&
                                    cachedScene.ViewLevel == viewLevel &&
                                    cachedScene.RoofsVisible == roofsVisible
                        ? JsonValue.Create(cachedScene.ScenePath)
                        : null,
                });
            }

            foreach (var strip in layoutPlan.RoadStrips)
            {
                roadStripsJson.Add(new JsonObject
                {
                    ["minTileX"] = strip.MinTileX,
                    ["minTileY"] = strip.MinTileY,
                    ["maxTileX"] = strip.MaxTileX,
                    ["maxTileY"] = strip.MaxTileY,
                    ["horizontal"] = strip.Horizontal,
                    ["isIntersection"] = strip.IsIntersection,
                });
            }
        }

        root["connectedNeighborhood"] = new JsonObject
        {
            ["enabled"] = connectedOptions.Enabled,
            ["adjacentRendering"] = connectedOptions.EnableAdjacentLotRendering,
            ["storyProgression"] = connectedOptions.EnableStoryProgression,
            ["maxLiveSplitLots"] = connectedOptions.MaxLiveSplitLots,
            ["focusedHouse"] = focusedHouseVal,
            ["homeHouse"] = EffectiveHomeHouse,
            ["adjacentVersion"] = adjacentRenderVersion,
            ["invitedHouses"] = new JsonArray(invitedInsideHouses.Select(h => (JsonNode)JsonValue.Create((int)h)!).ToArray()),
            ["activeLots"] = activeLots,
            ["layout"] = layoutJson,
            ["roadStrips"] = roadStripsJson,
            ["adjacentLots"] = adjacentLotsJson,
        };
        root["adjacentLots"] = adjacentLotsJson.DeepClone();

        if (IsVisitingAwayLot && (world?[SimulationGlobal.SimlessBuildMode] ?? 0) == 0 && HomeFamily() is { } activeHomeFamily)
            root["budget"] = activeHomeFamily.Budget;

        var currentLotName = neighborhoodManager.GetLotSummary(house)?.LotName ?? (house > 0 ? $"Lot {house}" : "");
        if (root["sims"] is JsonArray simsArray)
        {
            var presentNeighborIds = new HashSet<int>();
            foreach (var simNode in simsArray)
            {
                if (simNode is not JsonObject simObj) continue;
                var neighborId = simObj["neighborID"]?.GetValue<int>() ?? 0;
                if (neighborId > 0) presentNeighborIds.Add(neighborId);
                var details = neighborId > 0 && neighborId <= short.MaxValue
                    ? neighborhoodManager.GetSimDetails((short)neighborId)
                    : null;
                simObj["lotId"] = (int)house;
                simObj["lotName"] = currentLotName;
                simObj["isOnFocusedLot"] = true;
                simObj["activity"] = details?.ActivitySummary ?? "Idle";
            }

            if (EffectiveHomeHouse > 0 && HomeFamily() is { Members.Count: > 0 } homeFam)
            {
                foreach (var memberGuid in homeFam.Members)
                {
                    if (memberGuid == 0) continue;
                    var details = neighborhoodManager.GetSimLocationByGuid(memberGuid);
                    if (details is null || details.SimId <= 0 || !presentNeighborIds.Add(details.SimId))
                        continue;

                    var simLotId = details.CurrentLotId > 0 ? details.CurrentLotId : EffectiveHomeHouse;
                    var simLotName = neighborhoodManager.GetLotSummary((short)simLotId)?.LotName ?? $"Lot {simLotId}";
                    XPerson? livePerson = null;
                    SimulationWorld? liveWorld = null;
                    if (simLotId <= short.MaxValue && lotPool.TryGetLive((short)simLotId, out var liveSession))
                    {
                        liveWorld = liveSession.World;
                        livePerson = liveSession.World.WalkOrder.OfType<XPerson>()
                            .FirstOrDefault(p => p[PersonData.NeighborId] == details.SimId || PersonGuid(p) == memberGuid);
                    }
                    else if (simLotId <= short.MaxValue && adjacentPreviewWorlds.TryGetValue((short)simLotId, out var previewWorld))
                    {
                        liveWorld = previewWorld.World;
                        livePerson = previewWorld.World.WalkOrder.OfType<XPerson>()
                            .FirstOrDefault(p => p[PersonData.NeighborId] == details.SimId || PersonGuid(p) == memberGuid);
                    }

                    var x = livePerson is not null ? Math.Clamp(livePerson.Position.X / 16f, 0f, 63.999f) : 32f;
                    var y = livePerson is not null ? Math.Clamp(livePerson.Position.Y / 16f, 0f, 63.999f) : 32f;
                    var level = livePerson is not null ? (int)livePerson.Position.Height : 1;
                    var isChild = livePerson is not null && PersonAgeCategory.IsChild(livePerson[PersonData.PersonAge]);
                    var jobType = livePerson is not null ? (int)livePerson[PersonData.JobType] : 0;
                    var mood = livePerson is not null ? (int)(short)livePerson[Motive.Mood] : 0;
                    var displayName = livePerson is not null && !string.IsNullOrWhiteSpace(livePerson.Name) && !IsInternalUserLabel(livePerson.Name)
                        ? livePerson.Name
                        : !string.IsNullOrWhiteSpace(details.Name) && !IsInternalUserLabel(details.Name)
                            ? details.Name
                            : (livePerson?.TypeRecord?.CatalogName is { Length: > 0 } cat ? cat : details.Name);
                    var portraitObj = livePerson is not null && liveWorld is not null ? images?.Portrait(liveWorld, livePerson) : null;
                    var motivesObj = livePerson is not null
                        ? new JsonObject
                        {
                            ["Hunger"] = (short)livePerson[Motive.Hunger],
                            ["Comfort"] = (short)livePerson[Motive.Comfort],
                            ["Hygiene"] = (short)livePerson[Motive.Hygiene],
                            ["Bladder"] = (short)livePerson[Motive.Bladder],
                            ["Energy"] = (short)livePerson[Motive.Energy],
                            ["Fun"] = (short)livePerson[Motive.Fun],
                            ["Social"] = (short)livePerson[Motive.Social],
                            ["Room"] = (short)livePerson[Motive.Room],
                        }
                        : new JsonObject
                        {
                            ["Hunger"] = 0, ["Comfort"] = 0, ["Hygiene"] = 0, ["Bladder"] = 0,
                            ["Energy"] = 0, ["Fun"] = 0, ["Social"] = 0, ["Room"] = 0,
                        };

                    simsArray.Add(new JsonObject
                    {
                        ["id"] = (int)(livePerson?.Id ?? details.LotObjectId ?? 0),
                        ["guid"] = memberGuid,
                        ["name"] = displayName,
                        ["neighborID"] = (int)details.SimId,
                        ["household"] = true,
                        ["child"] = isChild,
                        ["jobType"] = jobType,
                        ["lotId"] = simLotId,
                        ["lotName"] = simLotName,
                        ["isOnFocusedLot"] = false,
                        ["activity"] = details.ActivitySummary,
                        ["x"] = x,
                        ["y"] = y,
                        ["level"] = level,
                        ["mood"] = mood,
                        ["motives"] = motivesObj,
                        ["portrait"] = portraitObj is not null ? JsonSerializer.SerializeToNode(portraitObj, Json) : null,
                        ["actions"] = new JsonArray(),
                    });
                }
            }
        }

        return root;
    }

    private JsonObject BuildActiveLotJson(short targetHouse, bool isLive, bool isAway, SimulationWorld? targetWorld)
    {
        var summary = neighborhoodManager.GetLotSummary(targetHouse);
        var simIds = new JsonArray();
        if (targetWorld is not null)
        {
            foreach (var person in targetWorld.WalkOrder.OfType<XPerson>())
            {
                var nid = person[PersonData.NeighborId];
                if (nid <= 0) continue;
                var trackedLot = neighborhoodManager.GetSimLocation(nid);
                if (trackedLot is > 0 && trackedLot != targetHouse) continue;
                simIds.Add((int)nid);
            }
        }
        else if (summary is not null)
        {
            foreach (var nid in summary.ResidentSimIds)
            {
                var trackedLot = neighborhoodManager.GetSimLocation(nid);
                if (trackedLot is null or 0 || trackedLot == targetHouse)
                    simIds.Add((int)nid);
            }
            foreach (var nid in summary.VisitorSimIds)
            {
                if (!summary.ResidentSimIds.Contains(nid)) simIds.Add((int)nid);
            }
        }

        return new JsonObject
        {
            ["house"] = (int)targetHouse,
            ["familyName"] = summary?.FamilyName is { Length: > 0 } famName ? JsonValue.Create(famName) : null,
            ["lotName"] = summary?.LotName is { Length: > 0 } lotName ? JsonValue.Create(lotName) : null,
            ["simIds"] = simIds,
            ["isAway"] = isAway,
            ["isLive"] = isLive,
            ["isFocused"] = targetHouse == (lotPool.FocusedHouse > 0 ? lotPool.FocusedHouse : house),
        };
    }

    // =========================================================================
    // F4: Visiting, Knocking/Entry, Home Household Ownership
    // F5: Cross-Lot Gateway Routing & Staged Sidewalk Handoff
    // F6: NeighborLotController Wiring
    // =========================================================================

    private sealed record TransferredSimSnapshot(
        short NeighborId,
        uint Guid,
        string Name,
        float[] Motives,
        float[] Saved7CC,
        short[] PersonData,
        IReadOnlyDictionary<int, IReadOnlyList<int>> Relationships);

    private Family? HomeFamily()
    {
        var homeHouse = EffectiveHomeHouse;
        if (homeHouse <= 0) return ActiveFamily();
        if (house == homeHouse && world is not null) return ActiveFamily();
        if (lotPool.TryGetLive(homeHouse, out var homeLive) &&
            (homeLive.World.FamilyBudget as FamilyBudget)?.ActiveFamily is { } liveFam)
        {
            return liveFam;
        }
        if (neighborhood is not null)
        {
            Family? fallback = null;
            ushort? preferredChunkId = lotPool.TryGetSaved(homeHouse, out var savedHome)
                ? savedHome.FamilyChunkId
                : null;
            foreach (var chunk in neighborhood.OfType(ResourceType.Parse("FAMI")))
            {
                if (chunk.Id == 0) continue;
                var fam = Family.Read(neighborhood, chunk);
                if (fam.HouseNumber != homeHouse) continue;
                if (preferredChunkId.HasValue && chunk.Id == preferredChunkId.Value)
                    return fam;
                if ((fam.Flags & 1) != 0)
                    return fam;
                fallback ??= fam;
            }
            if (fallback is not null) return fallback;
        }
        return ActiveFamily();
    }

    private bool IsConnectedHouseholdPerson(XPerson person)
    {
        var guid = PersonGuid(person);
        if (HomeFamily() is { Members.Count: > 0 } homeFam)
            return homeFam.Members.Contains(guid);
        if ((world?.FamilyBudget as FamilyBudget)?.RestrictedActivePersonKeys is { Count: > 0 } restricted)
            return restricted.Contains(unchecked((int)guid));
        return ActiveFamily()?.Members.Contains(guid) == true;
    }

    private static bool IsDoorObject(XObject target)
    {
        var name = target.TypeRecord?.Name;
        var catalog = target.TypeRecord?.CatalogName;
        if ((name is not null && name.Contains("Door", StringComparison.OrdinalIgnoreCase)) ||
            (catalog is not null && catalog.Contains("Door", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return target.RuntimeClass == ObjectClass.Portal &&
               target.Definition[(int)ObjdField.BuildModeType] == 1;
    }

    private bool TryGetConnectedDoorMenuEntries(XPerson actor, XObject target, out IEnumerable<object> entries)
    {
        entries = [];
        if (!ConnectedNeighborhoodEnabled || world is null || !IsVisitingAwayLot || !IsHouseholdPerson(actor))
            return false;

        var userData = Path.GetDirectoryName(neighborhoodPath) ?? Neighborhoods.UserData(profile);
        if (IsCommunityOrExpansionAwayLot(userData, house) || world.StateClass(house) == 2)
            return false;

        if (!IsDoorObject(target) || actor[PersonData.GreetStatus] > 0)
            return false;

        entries =
        [
            new
            {
                name = "Knock on Door",
                interaction = ConnectedKnockDoorInteractionId,
                param0 = (short)0,
                global = false,
                objectID = (int?)target.Id,
            }
        ];
        return true;
    }

    private bool TryPerformConnectedDoorKnock(XPerson actor, XObject target)
    {
        if (!ConnectedNeighborhoodEnabled || world is null || !IsVisitingAwayLot || !IsHouseholdPerson(actor))
            return false;

        var userData = Path.GetDirectoryName(neighborhoodPath) ?? Neighborhoods.UserData(profile);
        if (IsCommunityOrExpansionAwayLot(userData, house) || world.StateClass(house) == 2)
            return false;
        if (!IsDoorObject(target))
            return false;

        var visitors = world.WalkOrder.OfType<XPerson>().Where(IsHouseholdPerson).ToArray();
        if (visitors.Length == 0)
            visitors = [actor];

        PerformConnectedKnockGreeting(world, house, visitors);
        return true;
    }

    private void PerformConnectedKnockGreeting(SimulationWorld targetWorld, short targetHouse, IReadOnlyList<XPerson> visitors)
    {
        var homeGuids = HomeFamily()?.Members.ToHashSet() ?? [];
        var hasHost = targetWorld.WalkOrder.OfType<XPerson>()
            .Any(p => p.SelectionWord5CC == 0 && !homeGuids.Contains(PersonGuid(p)));

        foreach (var visitor in visitors)
        {
            if (targetWorld.SavedExecutionStacks.TryGetValue(visitor.Id, out var stack) &&
                stack.Frames.Any(static f => f.TreeId is 8286 or 8293))
            {
                GatewayRouting.CancelPersonRoutesAndActions(targetWorld, visitor);
                targetWorld.ResetObjectForChangedCode(visitor);
            }
            visitor[PersonData.GreetStatus] = 1;
            var nid = visitor[PersonData.NeighborId];
            if (nid > 0 && neighborhoodManager.GetSimDetails(nid) is { } loc)
                loc.ActivitySummary = hasHost ? "Visiting" : "Exploring";
        }

        if (hasHost)
            targetWorld.RecordUiMessage(0x109, $"Greeted at Lot {targetHouse}");

        bool newlyInvited = invitedInsideHouses.Add(targetHouse);
        if (newlyInvited)
        {
            adjacentRenderVersion++;
            if (targetHouse == house && streaming)
                Export();
        }
    }

    private bool IsCommunityUnifiedLot(short targetHouse) =>
        targetHouse > 0 &&
        (neighborhoodManager.GetLotSummary(targetHouse)?.IsCommunity == true ||
         IsCommunityOrExpansionAwayLot(Neighborhoods.UserData(profile), targetHouse));

    private bool IsFocusedHouseOccupiedByHost()
    {
        if (world is null || house <= 0 || IsCommunityUnifiedLot(house))
            return false;
        if (neighborhoodManager.GetLotSummary(house)?.IsOccupied == true)
            return true;
        var homeGuids = HomeFamily()?.Members.ToHashSet() ?? [];
        return world.WalkOrder.OfType<XPerson>()
            .Any(p => p[PersonData.NeighborId] > 0 &&
                      p[PersonData.FamilyNumber] > 0 &&
                      !homeGuids.Contains(PersonGuid(p)));
    }

    private void ResolveFocusedHousePrivacy(
        out string effectiveWallMode,
        out bool effectiveRoofsVisible,
        out bool hideInteriorPeople)
    {
        effectiveWallMode = wallMode;
        effectiveRoofsVisible = roofsVisible;
        hideInteriorPeople = false;

        if (!ConnectedNeighborhoodEnabled || world is null || house <= 0 || !IsVisitingAwayLotValue())
            return;

        if (IsCommunityUnifiedLot(house))
            return;

        bool occupied = IsFocusedHouseOccupiedByHost();
        if (!CanSeeInsideHouse(house, occupied))
        {
            effectiveWallMode = "up";
            effectiveRoofsVisible = true;
            hideInteriorPeople = true;
        }
    }

    private static void StripDangerousOrMagicDeliveryObjects(
        SimulationWorld targetWorld,
        bool stripMagicDelivery,
        bool stripDuplicatePaperCarrier = false)
    {
        const uint paperGirlGuid = 0xDD0A4385u;
        var toRemove = targetWorld.WalkOrder
            .Where(o =>
            {
                uint guid = o.TypeRecord is { } tr ? unchecked((uint)tr.Key) : 0u;
                string? n = o.TypeRecord?.Name;
                if (stripDuplicatePaperCarrier &&
                    (guid == paperGirlGuid ||
                     (n is not null &&
                      (n.Contains("Paper Girl", StringComparison.OrdinalIgnoreCase) ||
                       n.Contains("PaperCarrier", StringComparison.OrdinalIgnoreCase)))))
                {
                    return true;
                }
                if (o is XPerson || n is null)
                    return false;
                return (n.Contains("Fire", StringComparison.OrdinalIgnoreCase) &&
                        !n.Contains("Fireplace", StringComparison.OrdinalIgnoreCase) &&
                        !n.Contains("Alarm", StringComparison.OrdinalIgnoreCase)) ||
                       n.Contains("Grim Reaper", StringComparison.OrdinalIgnoreCase) ||
                       (stripMagicDelivery &&
                        (n.Contains("Mystery", StringComparison.OrdinalIgnoreCase) ||
                         n.Contains("Magic Box", StringComparison.OrdinalIgnoreCase) ||
                         n.Contains("Magic Starter", StringComparison.OrdinalIgnoreCase)));
            })
            .Select(o => o.Id)
            .ToList();
        foreach (var id in toRemove)
            targetWorld.Remove(id);
    }

    private void PrewarmAdjacentNeighborhoodParallel(int? cameraTileX = null, int? cameraTileY = null)
    {
        if (!ConnectedNeighborhoodEnabled || !connectedOptions.EnableAdjacentLotRendering ||
            world is null || lot is null || house <= 0 || registry is null)
        {
            return;
        }

        var focusedHouseVal = lotPool.FocusedHouse > 0 ? lotPool.FocusedHouse : house;
        var layoutPlan = ComputeCurrentNeighborhoodLayout(focusedHouseVal, cameraTileX, cameraTileY);
        var userData = Neighborhoods.UserData(profile);
        var missing = layoutPlan.AdjacentLots
            .Where(p => p.House > 0 &&
                        p.House != house &&
                        !lotPool.TryGetLive(p.House, out _) &&
                        !adjacentPreviewWorlds.ContainsKey(p.House))
            .ToList();

        if (missing.Count > 0)
        {
            // Warm thread-safe lookup tables on Neighborhood.iff once before parallel workers start reading.
            neighborhood?.Find(ResourceType.Str, 1);
            var loadedResults = new ConcurrentBag<(short House, AdjacentPreviewWorld Preview)>();
            var simClockTicks = world.Clock.Ticks;
            var simClockDay = world.Clock.Day;

            Parallel.ForEach(
                missing,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) },
                placement =>
                {
                    try
                    {
                        var lotPath = Path.Combine(userData, "Houses", $"House{placement.House:00}.iff");
                        if (!File.Exists(lotPath))
                            return;

                        var setup = WorldBootstrap.Load(
                            game,
                            lotPath,
                            language,
                            () => speed == 0,
                            placement.House,
                            userDataPath: userData,
                            scoreAutonomy: null,
                            existingRegistry: registry,
                            neighborhoodManager: null,
                            connectedNeighborhoodEnabled: false,
                            preloadedNeighborhood: neighborhood);
                        var loadedWorld = setup.World;
                        loadedWorld.Clock.RestoreProgress(simClockTicks, simClockDay);
                        if (loadedWorld.NeighborhoodData.Length > 1 && loadedWorld.NeighborhoodData[1] < 3)
                            loadedWorld.NeighborhoodData[1] = 3;
                        loadedWorld[SimulationGlobal.IsTutorialHouseNoYes] = 0;
                        loadedWorld.TutorialOwner = null;
                        loadedWorld[SimulationGlobal.FreeWill] = 1;
                        loadedWorld.AutonomyForced = true;

                        bool isCommunityLot = placement.IsCommunity || IsCommunityUnifiedLot(placement.House);
                        bool hasHousehold = placement.Occupied &&
                            (setup.ActiveFamilyChunkId is not null ||
                             loadedWorld.WalkOrder.OfType<XPerson>().Any(p => p[PersonData.FamilyNumber] > 0));
                        bool shouldSimulate = hasHousehold || isCommunityLot;
                        if (!shouldSimulate)
                        {
                            loadedWorld[SimulationGlobal.SimlessBuildMode] = 1;
                            StripDangerousOrMagicDeliveryObjects(loadedWorld, stripMagicDelivery: true, stripDuplicatePaperCarrier: true);
                        }
                        else
                        {
                            loadedWorld[SimulationGlobal.SimlessBuildMode] = 0;
                            var initControl = new SimulationClockControl(1, 1, 1);
                            for (int t = 0; t < 12; t++)
                            {
                                foreach (var dlg in loadedWorld.DialogRecords)
                                    if (dlg.State == 0) { dlg.State = 2; dlg.Visible = false; }
                                StripDangerousOrMagicDeliveryObjects(loadedWorld, stripMagicDelivery: false, stripDuplicatePaperCarrier: true);
                                try { loadedWorld.AdvanceSimulationTick(initControl, 0, loadedWorld.Clock.Ticks); }
                                catch { break; }
                            }
                        }

                        int loadedSize = loadedWorld.Globals.Length > 23 && loadedWorld.Globals[23] is > 0 and <= 64
                            ? loadedWorld.Globals[23]
                            : NeighborhoodStreetLayout.LotSizeTiles;
                        var preview = new AdjacentPreviewWorld(setup.Lot, loadedWorld, DetectRoadEdge(setup.Lot, loadedSize))
                        {
                            ActiveFamilyChunkId = setup.ActiveFamilyChunkId,
                            ActiveFamilyField0x13C = setup.ActiveFamily?.Field0x13C ?? 0,
                            TickingPaused = !shouldSimulate,
                        };
                        loadedResults.Add((placement.House, preview));
                    }
                    catch
                    {
                        // Individual lot prewarm errors fall back cleanly to on-demand loading.
                    }
                });

            foreach (var (loadedHouse, preview) in loadedResults)
            {
                preview.World.Neighborhood = neighborhoodManager;
                preview.World.AutonomyHost = null; // Attach score-backed autonomy cleanly on main thread if needed
                if (preview.World.AutonomyHost is null && setupGlobal() is { } g)
                {
                    preview.World.AutonomyHost = new AutonomyHost(preview.World, g, loadedHouse, () => speed == 0, ScoreAutonomyChoice);
                }
                adjacentPreviewWorlds[loadedHouse] = preview;
            }
        }

        // Phase B (sequential): export and cache all initial scenes on main thread before frame 1
        _ = ExportAdjacentLotScenesPayload(force: false, cameraTileX, cameraTileY);

        IffFile? setupGlobal() => global;
    }

    private bool CanSeeInsideHouse(short targetHouse, bool occupied)
    {
        if (!ConnectedNeighborhoodEnabled || !occupied || targetHouse <= 0)
            return true;
        if (IsCommunityUnifiedLot(targetHouse))
            return true;
        var home = EffectiveHomeHouseValue();
        if (home > 0 && targetHouse == home)
            return true;
        return invitedInsideHouses.Contains(targetHouse);
    }

    private void ConfigureNeighborLotControllerFromWorld(SimulationWorld? sourceWorld)
    {
        if (sourceWorld is null) return;
        neighborLotController.Configure(sourceWorld.PersonMotiveDecayTuning, sourceWorld.CareerHost);
    }

    private HashSet<int> CollectLivePoolLotIds()
    {
        var ids = new HashSet<int>();
        if (house > 0) ids.Add(house);
        foreach (var liveHouse in lotPool.LiveSessions.Keys)
            if (liveHouse > 0) ids.Add(liveHouse);
        return ids;
    }

    private void EnsureNeighborLotControllerSeeded()
    {
        if (!ConnectedNeighborhoodEnabled || neighborhood is null) return;
        if (!neighborControllerSeedDirty &&
            ReferenceEquals(lastSeededNeighborhood, neighborhood) &&
            lastSeededSavedLotCount == lotPool.SavedLotStates.Count &&
            lastSeededLiveLotCount == lotPool.LiveSessions.Count)
        {
            return;
        }

        var userData = Path.GetDirectoryName(neighborhoodPath) ?? Neighborhoods.UserData(profile);
        var activeIds = CollectLivePoolLotIds();
        var savedBytes = lotPool.SavedLotStates.Count == 0
            ? null
            : lotPool.SavedLotStates.ToDictionary(static kv => kv.Key, static kv => kv.Value.LotBytes);
        Func<uint, Objd?>? findDef = registry is null
            ? null
            : guid => registry.Find(guid)?.Definition;
        neighborLotController.EnsureSeededFromNeighborhood(
            neighborhood,
            userData,
            activeIds,
            savedBytes,
            findDef,
            neighborhoodManager);
        neighborControllerSeedDirty = false;
        lastSeededNeighborhood = neighborhood;
        lastSeededSavedLotCount = lotPool.SavedLotStates.Count;
        lastSeededLiveLotCount = lotPool.LiveSessions.Count;
    }

    private void RegisterDehydratedLotInControllerIfInactiveResidential(LotSession session, string userData)
    {
        if (session.House <= 0) return;
        if (IsCommunityOrExpansionAwayLot(userData, session.House) || session.World.StateClass(session.House) == 2)
            return;
        if (session.Away)
        {
            neighborLotController.SetLotActive(session.House, false);
            return;
        }

        var homeGuids = HomeFamily()?.Members.ToHashSet();
        var exclude = session.House == EffectiveHomeHouse ? null : homeGuids;
        neighborLotController.RegisterFromWorld(
            session.House,
            session.World,
            unchecked((short)(session.ActiveFamilyChunkId ?? 0)),
            excludeGuids: exclude);
        neighborLotController.SetLotActive(session.House, false);
    }

    private List<short> ResolveHouseholdVisitorNeighborIds(short? simNeighborId, short[]? companionNeighborIds, Family? homeFam)
    {
        var requested = new List<short>();
        if (simNeighborId is { } explicitNid)
        {
            requested.Add(explicitNid);
        }
        else if (SelectedPerson() is { } sel && IsHouseholdPerson(sel) && sel[PersonData.NeighborId] > 0)
        {
            requested.Add(sel[PersonData.NeighborId]);
        }
        else if (homeFam is { Members.Count: > 0 })
        {
            foreach (var guid in homeFam.Members)
            {
                var loc = neighborhoodManager.GetSimLocationByGuid(guid);
                if (loc is { SimId: > 0 })
                {
                    requested.Add(loc.SimId);
                    break;
                }
            }
        }

        if (companionNeighborIds is { Length: > 0 })
        {
            foreach (var compNid in companionNeighborIds)
            {
                if (!requested.Contains(compNid))
                    requested.Add(compNid);
            }
        }

        if (requested.Count == 0)
            throw new ArgumentException("No household Sim specified for lot travel.");

        var allowedGuids = homeFam?.Members.ToHashSet() ?? [];
        foreach (var nid in requested)
        {
            if (nid <= 0)
                throw new ArgumentException($"Invalid neighborID {nid}.");

            var details = neighborhoodManager.GetSimDetails(nid);
            var guid = details?.Guid ?? 0u;
            if (guid == 0 && world?.Neighbors.TryGetValue(nid, out var liveNbr) == true)
                guid = unchecked((uint)liveNbr.TypeRecordKey);
            if (guid == 0 && neighborhood is not null &&
                neighborhood.OfType(ResourceType.Parse("NBRS")).FirstOrDefault() is { } nbrsChunk)
            {
                var nbrs = Neighbors.ReadRecoveringSupportedPrefix(neighborhood, nbrsChunk);
                guid = nbrs.Entries.FirstOrDefault(e => e.NeighborId == nid)?.Guid ?? 0u;
            }

            if (allowedGuids.Count > 0)
            {
                if (guid == 0 || !allowedGuids.Contains(guid))
                    throw new ArgumentException($"Sim {nid} does not belong to the active household.");
            }
            else if (guid == 0)
            {
                throw new ArgumentException($"Sim {nid} could not be resolved in the active household.");
            }
        }

        return requested;
    }

    private LotSession GetOrLoadPoolSession(short targetHouse, string userData)
    {
        if (targetHouse == house && world is not null && lot is not null)
        {
            if (!lotPool.TryGetLive(house, out var focused))
            {
                SeedFocusedSessionIntoPool();
                focused = lotPool.LiveSessions[house];
            }
            return focused;
        }
        if (lotPool.TryGetLive(targetHouse, out var liveSession))
        {
            SyncNeighborhoodRecordsIntoLiveWorld(liveSession.World, neighborhood, CollectGloballyRemovedNeighbors());
            return liveSession;
        }
        if (lotPool.TryGetSaved(targetHouse, out var savedSession))
        {
            EnforceMaxLiveSplitLots(Math.Max(1, connectedOptions.MaxLiveSplitLots) - 1, targetHouse);
            var hydrated = HydrateSession(savedSession, userData);
            lotPool.PutLive(hydrated);
            return hydrated;
        }

        EnforceMaxLiveSplitLots(Math.Max(1, connectedOptions.MaxLiveSplitLots) - 1, targetHouse);
        var loaded = LoadFreshSessionForPool(targetHouse, userData);
        lotPool.PutLive(loaded);
        return loaded;
    }

    private TransferredSimSnapshot CaptureAndRemoveTravellerFromSession(
        LotSession sourceSession,
        short nid,
        Family? homeFam,
        string userData)
    {
        var sourceWorld = sourceSession.World;
        var guid = neighborhoodManager.GetSimDetails(nid)?.Guid ?? 0u;
        var person = sourceWorld.WalkOrder.OfType<XPerson>()
            .FirstOrDefault(p => p[PersonData.NeighborId] == nid || (guid != 0 && PersonGuid(p) == guid));
        if (person is null)
            throw new InvalidOperationException($"Sim {nid} is not currently instantiated on Lot {sourceSession.House}.");

        GatewayRouting.CancelPersonRoutesAndActions(sourceWorld, person);
        sourceWorld.TransitVisualOverrides.Remove(person.Id);
        sourceWorld.SavePersonRecord(person);

        var pGuid = PersonGuid(person);
        if (pGuid == 0) pGuid = guid;
        var rels = sourceWorld.Neighbors.TryGetValue(nid, out var nbrState)
            ? nbrState.Relationships.Snapshot()
            : person.Relationships.Snapshot();
        var relCopy = rels.ToDictionary(static kv => kv.Key, static kv => (IReadOnlyList<int>)kv.Value.ToArray());

        var resolvedSimName = !string.IsNullOrWhiteSpace(person.Name) && !IsInternalUserLabel(person.Name)
            ? person.Name
            : (!string.IsNullOrWhiteSpace(nbrState?.Name) && !IsInternalUserLabel(nbrState.Name)
                ? nbrState.Name!
                : (!string.IsNullOrWhiteSpace(person.TypeRecord?.CatalogName)
                    ? person.TypeRecord.CatalogName
                    : (!string.IsNullOrWhiteSpace(nbrState?.Name) ? nbrState.Name! : $"Sim {nid}")));

        var snapshot = new TransferredSimSnapshot(
            nid,
            pGuid,
            resolvedSimName,
            person.Motives.ToArray(),
            person.SavedPersonValues7CC.ToArray(),
            person.PersonData.ToArray(),
            relCopy);

        var removedId = person.Id;
        PruneNeighborFromOtherCrossLotTransits(nid);
        activeCrossLotTransits.Remove(nid);
        sourceWorld.Remove(removedId);
        sourceWorld.RefreshNeighborInteractionManager();

        var homeGuids = homeFam?.Members.ToHashSet() ?? [];
        if (sourceSession.SelectedSim == removedId)
        {
            var nextSel = sourceWorld.WalkOrder.OfType<XPerson>()
                .FirstOrDefault(p => homeGuids.Contains(PersonGuid(p)))?.Id ?? (short)0;
            sourceSession.SelectedSim = nextSel;
            if (sourceSession.House == house)
            {
                selectedSim = nextSel;
                SyncSelectedSimFlags(sourceWorld, selectedSim);
            }
        }

        if (!sourceSession.Away)
            sourceSession.SaveDirty = true;

        if (sourceSession.Away)
        {
            var remainingKeys = sourceWorld.WalkOrder.OfType<XPerson>()
                .Select(PersonGuid)
                .Where(g => g != 0 && homeGuids.Contains(g))
                .Select(static g => unchecked((int)g))
                .Distinct()
                .ToArray();
            sourceSession.RestrictedActivePersonKeys = remainingKeys.Length > 0 ? remainingKeys : null;
            if (sourceWorld.FamilyBudget is FamilyBudget fb)
                fb.RestrictActivePersonKeys(sourceSession.RestrictedActivePersonKeys);
        }

        return snapshot;
    }

    private void CleanupVacatedAwaySession(LotSession sourceSession, short keepFocusHouse, Family? homeFam, string userData)
    {
        if (sourceSession.House == keepFocusHouse || !sourceSession.Away) return;
        var homeGuids = homeFam?.Members.ToHashSet() ?? [];
        var hasRemainingHomeVisitor = sourceSession.World.WalkOrder.OfType<XPerson>()
            .Any(p => homeGuids.Contains(PersonGuid(p)));
        if (hasRemainingHomeVisitor) return;

        var isCommunity = IsCommunityOrExpansionAwayLot(userData, sourceSession.House) ||
                          sourceSession.World.StateClass(sourceSession.House) == 2;
        sourceSession.RestrictedActivePersonKeys = null;
        if (sourceSession.World.FamilyBudget is FamilyBudget fb)
            fb.RestrictActivePersonKeys(null);
        sourceSession.Away = isCommunity && sourceSession.World[SimulationGlobal.SimlessBuildMode] == 0;

        var dehydrated = DehydrateSession(sourceSession);
        lotPool.PutSaved(dehydrated);
        if (invitedInsideHouses.Remove(sourceSession.House))
            adjacentRenderVersion++;
        if (!isCommunity)
            RegisterDehydratedLotInControllerIfInactiveResidential(sourceSession, userData);
    }

    private XPerson InstantiateAndPlaceArrivingSim(
        LotSession targetSession,
        TransferredSimSnapshot snap,
        short sourceHouse,
        bool isVisiting,
        bool greetUnlocked,
        int spreadIndex,
        ObjectPosition? overrideEntryTile = null)
    {
        var targetWorld = targetSession.World;
        var existing = targetWorld.WalkOrder.OfType<XPerson>()
            .FirstOrDefault(p => p[PersonData.NeighborId] == snap.NeighborId ||
                                 (snap.Guid != 0 && PersonGuid(p) == snap.Guid));
        XPerson person;
        var isNew = false;
        if (existing is not null)
        {
            person = existing;
            person.Flags &= ~ConnectedPendingRemovalFlag;
            GatewayRouting.CancelPersonRoutesAndActions(targetWorld, person);
        }
        else
        {
            if (targetWorld.ObjectFactory?.Invoke(unchecked((int)snap.Guid)) is not XPerson created)
                throw new InvalidOperationException($"Unable to instantiate Sim {snap.NeighborId} (0x{snap.Guid:X8}) on Lot {targetSession.House}.");
            person = created;
            targetWorld.Add(person);
            targetWorld.InitializeCreatedObject(person);
            targetWorld.InitializeCreatedPerson(person);
            isNew = true;
        }

        Array.Copy(snap.PersonData, person.PersonData, Math.Min(person.PersonData.Length, snap.PersonData.Length));
        person[PersonData.NeighborId] = snap.NeighborId;
        if (!string.IsNullOrWhiteSpace(snap.Name))
            person.Name = snap.Name;

        SyncPersonFromNeighborRecord(targetWorld, person);
        Array.Copy(snap.PersonData, person.PersonData, Math.Min(person.PersonData.Length, snap.PersonData.Length));
        Array.Copy(snap.Motives, person.Motives, Math.Min(person.Motives.Length, snap.Motives.Length));
        Array.Copy(snap.Saved7CC, person.SavedPersonValues7CC, Math.Min(person.SavedPersonValues7CC.Length, snap.Saved7CC.Length));
        person[PersonData.NeighborId] = snap.NeighborId;
        person.StateValue = targetSession.House;
        person[PersonData.GreetStatus] = (short)(greetUnlocked ? 1 : 0);

        if (targetWorld.Neighbors.TryGetValue(snap.NeighborId, out var targetNbr))
        {
            foreach (var (otherKey, vals) in snap.Relationships)
            {
                if (otherKey is < short.MinValue or > short.MaxValue) continue;
                var otherId = (short)otherKey;
                targetNbr.Relationships.Resize(otherId, vals.Count);
                person.Relationships.Resize(otherId, vals.Count);
                for (var i = 0; i < vals.Count; i++)
                {
                    targetNbr.Relationships.Set(otherId, i, vals[i]);
                    person.Relationships.Set(otherId, i, vals[i]);
                }
            }
        }

        var entryTile = overrideEntryTile is { X: > 0, Y: > 0 } explicitEntry
            ? explicitEntry
            : GatewayRouting.ResolveWalkableSidewalkTile(targetWorld, targetSession.Lot, person, spreadIndex);
        var spawnPos = GatewayRouting.ClampPositionToLotBounds(entryTile, innerPlayable: true);
        if (isNew || person.Position.X < 0 || person.Position.Y < 0)
            targetWorld.PlaceCreatedObject(person, spawnPos);
        else
            targetWorld.SetObjectPosition(person, spawnPos);

        var arrivalTarget = GatewayRouting.ResolveArrivalWalkTarget(targetWorld, targetSession.Lot, person, spawnPos);
        if (arrivalTarget.X != spawnPos.X || arrivalTarget.Y != spawnPos.Y)
        {
            try
            {
                targetWorld.StartDirectPositionRoute(person, arrivalTarget);
            }
            catch
            {
                // Keep placed safely at spawnPos if arrival walk path is blocked.
            }
        }

        neighborhoodManager.OnSimLotSwitched(snap.NeighborId, sourceHouse, targetSession.House, isVisiting, person.Id);
        return person;
    }

    private void PruneNeighborFromOtherCrossLotTransits(short nid, short? exceptPrimaryNid = null)
    {
        if (nid <= 0 || activeCrossLotTransits.Count == 0) return;
        foreach (var (primaryNid, transit) in activeCrossLotTransits)
        {
            if (exceptPrimaryNid.HasValue && primaryNid == exceptPrimaryNid.Value)
                continue;
            transit.CompanionNeighborIds.RemoveAll(c => c == nid);
        }
    }

    private void CancelCrossLotTransitForNeighbor(short nid, bool cancelRoutes = true, bool repositionFromGateway = true)
    {
        if (nid <= 0) return;
        PruneNeighborFromOtherCrossLotTransits(nid);
        if (!activeCrossLotTransits.Remove(nid, out var cancelled))
            return;

        if (lotPool.TryGetLive(cancelled.SourceHouse, out var srcSession))
        {
            var srcPerson = srcSession.World.WalkOrder.OfType<XPerson>()
                .FirstOrDefault(p => p[PersonData.NeighborId] == nid);
            if (srcPerson is not null)
            {
                if (cancelRoutes)
                    GatewayRouting.CancelPersonRoutesAndActions(srcSession.World, srcPerson);
                GatewayRouting.ClearTransitWalkAnimation(srcSession.World, srcPerson);
                srcSession.World.TransitVisualOverrides.Remove(srcPerson.Id);

                if (cancelRoutes && repositionFromGateway)
                {
                    if (GatewayRouting.HasPortalLeaveStack(srcSession.World, srcPerson) ||
                        srcPerson[PersonData.GreetStatus] >= 2 ||
                        (srcSession.Away && cancelled.Phase == CrossLotTransitPhase.OffLotSeamWalk))
                    {
                        srcSession.World.ResetObjectForChangedCode(srcPerson);
                        GatewayRouting.CancelPersonRoutesAndActions(srcSession.World, srcPerson);
                    }

                    if (srcSession.Away && srcPerson[PersonData.GreetStatus] >= 2)
                        srcPerson[PersonData.GreetStatus] = 1;

                    if (cancelled.Phase == CrossLotTransitPhase.OffLotSeamWalk ||
                        GatewayRouting.IsNearGatewayOrPortal(srcSession.World, srcPerson.Position, cancelled.SourceGatewayTile, minTileDistance: 2))
                    {
                        var safePos = GatewayRouting.ResolveSafeNonPortalSidewalkTile(
                            srcSession.World,
                            srcSession.Lot,
                            srcPerson,
                            cancelled.SourceGatewayTile,
                            minTileDistance: 2);
                        if (safePos.X != srcPerson.Position.X || safePos.Y != srcPerson.Position.Y || safePos.Height != srcPerson.Position.Height)
                            srcSession.World.SetObjectPosition(srcPerson, safePos);
                    }
                }
            }
            if (cancelled.SimObjectId > 0)
                srcSession.World.TransitVisualOverrides.Remove(cancelled.SimObjectId);
            neighborhoodManager.SyncFromWorld(
                srcSession.World,
                srcSession.House,
                preserveOtherLotLocations: true,
                activeLotIds: CollectActivePoolLotIds());
        }
        else if (neighborhoodManager.GetSimDetails(nid) is { } loc)
        {
            loc.ActivitySummary = loc.IsAtWorkOrSchool
                ? "At work"
                : (loc.IsVisiting ? "Visiting" : "Idle");
        }
    }

    private void CancelCrossLotTransitForPerson(XPerson? actor, bool cancelRoutes = false)
    {
        if (!ConnectedNeighborhoodEnabled || actor is null) return;
        var nid = actor[PersonData.NeighborId];
        if (nid > 0)
            CancelCrossLotTransitForNeighbor(nid, cancelRoutes);
        else
            world?.TransitVisualOverrides.Remove(actor.Id);
    }

    private bool HasActiveCrossLotTransitForPerson(XPerson? actor)
    {
        if (!ConnectedNeighborhoodEnabled || world is null || actor is null || activeCrossLotTransits.Count == 0)
            return false;
        var nid = actor[PersonData.NeighborId];
        return nid > 0 &&
               activeCrossLotTransits.TryGetValue(nid, out var transit) &&
               transit.SourceHouse == house &&
               transit.Phase is CrossLotTransitPhase.RoutingToGateway or CrossLotTransitPhase.OffLotSeamWalk;
    }

    private bool TryHandleCancelConnectedTransitAction(XPerson actor, string? token)
    {
        if (!ConnectedNeighborhoodEnabled || world is null)
            return false;

        const string transitRootPrefix = "connected-transit-";
        if (token is not null && token.StartsWith(transitRootPrefix, StringComparison.Ordinal))
        {
            var actorTransitPrefix = $"{transitRootPrefix}{actor.Id}-";
            if (!token.StartsWith(actorTransitPrefix, StringComparison.Ordinal) ||
                !short.TryParse(token.AsSpan(actorTransitPrefix.Length), out var tokenNid) ||
                tokenNid <= 0 ||
                tokenNid != actor[PersonData.NeighborId] ||
                !HasActiveCrossLotTransitForPerson(actor))
            {
                throw new ArgumentException("Action is no longer cancellable");
            }

            world.CancelSyntheticDirectPositionRouteFor(actor);
            CancelCrossLotTransitForPerson(actor, cancelRoutes: true);
            return true;
        }

        if (HasActiveCrossLotTransitForPerson(actor) &&
            (token is null || !token.StartsWith("native-", StringComparison.Ordinal)))
        {
            world.CancelSyntheticDirectPositionRouteFor(actor);
            CancelCrossLotTransitForPerson(actor, cancelRoutes: true);
            return true;
        }

        return false;
    }

    private object? TryDescribeConnectedTransitAction(XPerson person)
    {
        if (!HasActiveCrossLotTransitForPerson(person))
            return null;

        var nid = person[PersonData.NeighborId];
        var transit = activeCrossLotTransits[nid];
        var targetLotName = neighborhoodManager.GetLotSummary(transit.TargetHouse)?.LotName;
        var actionName = transit.IsReturnHome
            ? "Return Home"
            : (!string.IsNullOrWhiteSpace(targetLotName) ? $"Visit {targetLotName}" : $"Visit Lot {transit.TargetHouse}");

        return new
        {
            name = actionName,
            callee = (short?)person.Id,
            token = $"connected-transit-{person.Id}-{nid}",
            active = true,
            visible = true,
            cancellable = true,
            cancelRequested = false,
            icon = images?.Portrait(world!, person),
        };
    }

    private object VisitLotInternal(
        short targetHouse,
        short? simNeighborId,
        short[]? companionNeighborIds,
        bool knock,
        bool walk = false,
        int? targetTileX = null,
        int? targetTileY = null)
    {
        if (!ConnectedNeighborhoodEnabled)
            throw new InvalidOperationException("Connected neighborhood mode is disabled.");
        if (targetHouse <= 0)
            throw new ArgumentException("visitLot requires a positive integer house.");
        if (world is null || lot is null)
            throw new InvalidOperationException("There is no loaded native lot.");

        var userData = Path.GetDirectoryName(neighborhoodPath) ?? Neighborhoods.UserData(profile);
        var hasLotInPool = lotPool.TryGetLive(targetHouse, out _) || lotPool.TryGetSaved(targetHouse, out _);
        if (!hasLotInPool && !File.Exists(Path.Combine(userData, "Houses", $"House{targetHouse:D2}.iff")))
            throw new FileNotFoundException($"Lot {targetHouse} not found.");

        FlushFocusedLotSessionToPool();
        if (lotPool.HomeHouse == 0 && EffectiveHomeHouseValue() is > 0 and var eh)
            lotPool.HomeHouse = eh;
        var homeHouse = lotPool.HomeHouse > 0 ? lotPool.HomeHouse : EffectiveHomeHouseValue();
        var homeFam = HomeFamily();
        var homeGuids = homeFam?.Members.ToHashSet() ?? [];

        var travellerNids = ResolveHouseholdVisitorNeighborIds(simNeighborId, companionNeighborIds, homeFam);

        if (walk)
        {
            var primaryNid = travellerNids[0];
            foreach (var nid in travellerNids)
                CancelCrossLotTransitForNeighbor(nid, cancelRoutes: false);
            var sourceHouse = (short)(neighborhoodManager.GetSimLocation(primaryNid) ?? house);
            if (sourceHouse <= 0) sourceHouse = house;
            if (sourceHouse != targetHouse)
            {
                var sourceSession = GetOrLoadPoolSession(sourceHouse, userData);
                var primaryPerson = sourceSession.World.WalkOrder.OfType<XPerson>()
                    .FirstOrDefault(p => p[PersonData.NeighborId] == primaryNid);
                if (primaryPerson is not null)
                {
                    GatewayRouting.CancelPersonRoutesAndActions(sourceSession.World, primaryPerson);
                    var sourceGateway = GatewayRouting.ResolveWalkableSidewalkTile(
                        sourceSession.World, sourceSession.Lot, primaryPerson);
                    var blueprint = EnsureWorldBlueprint();
                    lotPool.TryGetLive(targetHouse, out var liveTargetSession);
                    adjacentPreviewWorlds.TryGetValue(targetHouse, out var previewTargetWorld);
                    var destGateway = NeighborhoodStreetGraph.ResolveArrivalGatewayTile(
                        sourceHouse,
                        targetHouse,
                        liveTargetSession?.Lot ?? previewTargetWorld?.Lot,
                        liveTargetSession?.World ?? previewTargetWorld?.World,
                        blueprint);
                    var worldWaypoints = NeighborhoodStreetGraph.ComputeSidewalkPath(
                        blueprint, sourceHouse, sourceGateway, targetHouse, destGateway).ToList();
                    float totalDist = NeighborhoodStreetGraph.ComputeTotalDistance(worldWaypoints);
                    var startWorldPt = worldWaypoints.Count > 0 ? worldWaypoints[0] : (0f, 0f);
                    var destTarget = targetTileX is int dtx && targetTileY is int dty && dtx >= 1 && dtx < 63 && dty >= 1 && dty < 63
                        ? new ObjectPosition((short)(dtx * 16 + 8), (short)(dty * 16 + 8), 1)
                        : default;
                    var transit = new CrossLotTransitState
                    {
                        SimNeighborId = primaryNid,
                        SimObjectId = primaryPerson.Id,
                        SourceHouse = sourceHouse,
                        TargetHouse = targetHouse,
                        IsReturnHome = false,
                        KnockOnArrival = knock,
                        CompanionNeighborIds = travellerNids.Skip(1).ToList(),
                        Phase = CrossLotTransitPhase.RoutingToGateway,
                        SourceGatewayTile = sourceGateway,
                        DestinationEntryTile = destGateway,
                        DestinationGatewayTile = destGateway,
                        DestinationArrivalTarget = destTarget,
                        StageTicksElapsed = 0,
                        MaxStageTicks = 90,
                        WorldWaypoints = worldWaypoints,
                        WaypointIndex = worldWaypoints.Count > 1 ? 1 : 0,
                        CurrentWorldX = startWorldPt.Item1,
                        CurrentWorldY = startWorldPt.Item2,
                        TotalWorldDistance = totalDist,
                    };
                    activeCrossLotTransits[primaryNid] = transit;
                    if (neighborhoodManager.GetSimDetails(primaryNid) is { } loc)
                        loc.ActivitySummary = "In transit";

                    var distSq = Math.Pow((primaryPerson.Position.X - sourceGateway.X) / 16.0, 2) +
                                 Math.Pow((primaryPerson.Position.Y - sourceGateway.Y) / 16.0, 2);
                    if (distSq <= 2.25)
                    {
                        transit.Phase = CrossLotTransitPhase.OffLotSeamWalk;
                    }
                    else
                    {
                        try
                        {
                            sourceSession.World.StartDirectPositionRoute(primaryPerson, sourceGateway);
                        }
                        catch
                        {
                            transit.Phase = CrossLotTransitPhase.OffLotSeamWalk;
                        }
                    }

                    return new
                    {
                        house = lotPool.FocusedHouse > 0 ? lotPool.FocusedHouse : house,
                        targetHouse,
                        focusedHouse = lotPool.FocusedHouse > 0 ? lotPool.FocusedHouse : house,
                        homeHouse,
                        away = IsVisitingAwayLot,
                        inTransit = true,
                        phase = transit.Phase.ToString(),
                        state = State(),
                    };
                }
            }
        }

        // Cancel any prior staged transit for these travellers when executing immediate RPC transfer.
        foreach (var nid in travellerNids)
            CancelCrossLotTransitForNeighbor(nid, cancelRoutes: true, repositionFromGateway: false);

        return ExecuteVisitLotTransfer(targetHouse, travellerNids, knock, homeHouse, homeFam, homeGuids, userData);
    }

    private object ExecuteVisitLotTransfer(
        short targetHouse,
        IReadOnlyList<short> travellerNids,
        bool knock,
        short homeHouse,
        Family? homeFam,
        HashSet<uint> homeGuids,
        string userData,
        ObjectPosition? overrideEntryTile = null)
    {
        var sourceSpeed = speed > 0 ? speed : lastRunningSpeed;
        var captured = new List<(short SourceHouse, TransferredSimSnapshot Snapshot)>();
        var touchedSourceSessions = new Dictionary<short, LotSession>();
        var alreadyOnTarget = new List<XPerson>();

        foreach (var nid in travellerNids)
        {
            var sourceHouse = (short)(neighborhoodManager.GetSimLocation(nid) ?? homeHouse);
            if (sourceHouse <= 0) sourceHouse = house;
            if (sourceHouse == targetHouse)
            {
                var curSession = GetOrLoadPoolSession(targetHouse, userData);
                if (curSession.World.WalkOrder.OfType<XPerson>().FirstOrDefault(p => p[PersonData.NeighborId] == nid) is { } already)
                {
                    GatewayRouting.CancelPersonRoutesAndActions(curSession.World, already);
                    alreadyOnTarget.Add(already);
                    continue;
                }
            }

            var sourceSession = GetOrLoadPoolSession(sourceHouse, userData);
            touchedSourceSessions[sourceHouse] = sourceSession;
            var snap = CaptureAndRemoveTravellerFromSession(sourceSession, nid, homeFam, userData);
            captured.Add((sourceHouse, snap));
        }

        SyncLiveNeighborRecordsToNeighborhood();

        RemoveTileCursor();
        ResetSeasonalOutfits();
        audio?.Reset();
        refreshedPersonFiles.Clear();

        var targetSession = GetOrLoadPoolSession(targetHouse, userData);
        var targetWorld = targetSession.World;
        targetWorld.Neighborhood = neighborhoodManager;
        targetWorld[SimulationGlobal.SimlessBuildMode] = 0;

        var isCommunity = IsCommunityOrExpansionAwayLot(userData, targetHouse) || targetWorld.StateClass(targetHouse) == 2;
        var isOccupiedResidential = !isCommunity && (
            targetSession.ActiveFamilyChunkId is > 0 ||
            targetWorld.WalkOrder.OfType<XPerson>().Any(p => p[PersonData.FamilyNumber] > 0 && !homeGuids.Contains(PersonGuid(p))));
        var isVisitingAway = (homeHouse > 0 && targetHouse != homeHouse) || isCommunity;

        targetSession.Away = isVisitingAway;
        if (homeHouse > 0)
            targetSession.HomeHouse = homeHouse;
        if (isVisitingAway)
        {
            if (targetWorld.NeighborhoodData.Length > 1 && targetWorld.NeighborhoodData[1] < 3)
                targetWorld.NeighborhoodData[1] = 3;
            targetWorld[SimulationGlobal.IsTutorialHouseNoYes] = 0;
            targetWorld.TutorialOwner = null;
            foreach (var dlg in targetWorld.DialogRecords)
                if (dlg.State == 0) { dlg.State = 2; dlg.Visible = false; }
        }
        SyncNeighborhoodRecordsIntoLiveWorld(targetWorld, neighborhood, CollectGloballyRemovedNeighbors());

        var greetUnlocked = !isVisitingAway || isCommunity || !isOccupiedResidential || knock;
        var arrivedPeople = new List<XPerson>(alreadyOnTarget);
        for (var i = 0; i < captured.Count; i++)
        {
            var (sourceHouse, snap) = captured[i];
            var arrived = InstantiateAndPlaceArrivingSim(
                targetSession,
                snap,
                sourceHouse,
                isVisiting: isVisitingAway,
                greetUnlocked: greetUnlocked,
                spreadIndex: i,
                overrideEntryTile: i == 0 ? overrideEntryTile : null);
            arrivedPeople.Add(arrived);
        }

        if (isVisitingAway)
        {
            var restrictedKeys = targetWorld.WalkOrder.OfType<XPerson>()
                .Select(PersonGuid)
                .Where(g => g != 0 && homeGuids.Contains(g))
                .Select(static g => unchecked((int)g))
                .Distinct()
                .ToArray();
            targetSession.RestrictedActivePersonKeys = restrictedKeys.Length > 0 ? restrictedKeys : null;
            if (targetWorld.FamilyBudget is FamilyBudget fb)
                fb.RestrictActivePersonKeys(targetSession.RestrictedActivePersonKeys);
        }
        else
        {
            targetSession.RestrictedActivePersonKeys = null;
            if (targetWorld.FamilyBudget is FamilyBudget fb)
                fb.RestrictActivePersonKeys(null);
        }

        targetWorld.RefreshNeighborInteractionManager();

        if (knock && isVisitingAway && isOccupiedResidential)
            PerformConnectedKnockGreeting(targetWorld, targetHouse, arrivedPeople);

        foreach (var src in touchedSourceSessions.Values)
        {
            if (src.House != targetHouse)
                CleanupVacatedAwaySession(src, targetHouse, homeFam, userData);
        }

        lotPool.PutLive(targetSession);
        lotPool.FocusedHouse = targetHouse;
        EnforceMaxLiveSplitLots();
        neighborControllerSeedDirty = true;

        var primaryPerson = arrivedPeople.FirstOrDefault();
        if (primaryPerson is not null)
            targetSession.SelectedSim = primaryPerson.Id;

        ApplyLotSessionToServer(targetSession);
        if (primaryPerson is not null && targetWorld.ObjectById(primaryPerson.Id) is XPerson)
        {
            selectedSim = primaryPerson.Id;
            SyncSelectedSimFlags(world!, selectedSim);
            targetSession.SelectedSim = selectedSim;
        }
        else if (world!.ObjectById(selectedSim) is not XPerson sel || !IsHouseholdPerson(sel))
        {
            selectedSim = DefaultSelectedSimId();
            SyncSelectedSimFlags(world!, selectedSim);
            targetSession.SelectedSim = selectedSim;
        }

        neighborhoodManager.SyncFromWorld(world!, targetHouse, activeLotIds: CollectActivePoolLotIds());
        ClampAwayLotEditingState();
        var resumeSpeed = sourceSpeed > 0 ? sourceSpeed : 1;
        lastRunningSpeed = resumeSpeed;
        targetSession.Speed = resumeSpeed;
        SetSimulationSpeed(resumeSpeed, blockingDialog: HasBlockingDialog(world!));

        if (audio?.Enabled == true)
            audio.PlayLotAmbience(house);

        Write(new { type = "lotChanged", house, away = IsVisitingAwayLot });
        Export();
        var finalState = State();
        Write(new { type = "state", state = finalState });

        return new
        {
            house,
            focusedHouse = lotPool.FocusedHouse,
            homeHouse = EffectiveHomeHouse,
            away = IsVisitingAwayLot,
            state = finalState,
        };
    }

    private object ReturnHomeInternal(short? simNeighborId, bool walk = false)
    {
        if (!ConnectedNeighborhoodEnabled)
            throw new InvalidOperationException("Connected neighborhood mode is disabled.");
        if (world is null || lot is null)
            throw new InvalidOperationException("There is no loaded native lot.");

        FlushFocusedLotSessionToPool();
        var homeHouse = EffectiveHomeHouseValue();
        if (homeHouse <= 0)
            throw new InvalidOperationException("No residential home lot is configured for returnHome.");
        if (lotPool.HomeHouse == 0)
            lotPool.HomeHouse = homeHouse;

        var homeFam = HomeFamily();
        var homeGuids = homeFam?.Members.ToHashSet() ?? [];
        var userData = Path.GetDirectoryName(neighborhoodPath) ?? Neighborhoods.UserData(profile);

        List<short> targetNids;
        if (simNeighborId is { } explicitNid)
        {
            targetNids = ResolveHouseholdVisitorNeighborIds(explicitNid, null, homeFam);
        }
        else if (SelectedPerson() is { } sel && IsHouseholdPerson(sel) &&
                 sel[PersonData.NeighborId] is > 0 and var selNid &&
                 (neighborhoodManager.GetSimLocation(selNid) ?? house) != homeHouse)
        {
            targetNids = [selNid];
        }
        else
        {
            targetNids = [];
            if (homeFam is { Members.Count: > 0 })
            {
                foreach (var guid in homeFam.Members)
                {
                    var details = neighborhoodManager.GetSimLocationByGuid(guid);
                    if (details is { SimId: > 0 } && details.CurrentLotId > 0 && details.CurrentLotId != homeHouse)
                        targetNids.Add(details.SimId);
                }
            }
            if (targetNids.Count == 0 && SelectedPerson() is { } fallbackSel &&
                fallbackSel[PersonData.NeighborId] is > 0 and var fbNid && IsHouseholdPerson(fallbackSel))
            {
                targetNids.Add(fbNid);
            }
        }

        var awayNids = targetNids
            .Where(nid => (neighborhoodManager.GetSimLocation(nid) ?? house) != homeHouse)
            .ToList();

        if (awayNids.Count == 0)
        {
            foreach (var nid in targetNids)
                CancelCrossLotTransitForNeighbor(nid, cancelRoutes: true);
            if (!simNeighborId.HasValue && activeCrossLotTransits.Count > 0)
            {
                foreach (var kv in activeCrossLotTransits
                             .Where(kv => !kv.Value.IsReturnHome && kv.Value.SourceHouse == homeHouse)
                             .ToArray())
                {
                    CancelCrossLotTransitForNeighbor(kv.Key, cancelRoutes: true);
                }
            }
            if (house != homeHouse)
                FocusLotInternal(homeHouse);
            return new
            {
                house,
                focusedHouse = lotPool.FocusedHouse > 0 ? lotPool.FocusedHouse : house,
                homeHouse,
                away = IsVisitingAwayLot,
                alreadyHome = true,
                state = State(),
            };
        }

        if (walk)
        {
            var primaryNid = awayNids[0];
            foreach (var nid in awayNids)
                CancelCrossLotTransitForNeighbor(nid, cancelRoutes: false);
            var sourceHouse = (short)(neighborhoodManager.GetSimLocation(primaryNid) ?? house);
            var sourceSession = GetOrLoadPoolSession(sourceHouse, userData);
            var primaryPerson = sourceSession.World.WalkOrder.OfType<XPerson>()
                .FirstOrDefault(p => p[PersonData.NeighborId] == primaryNid);
            if (primaryPerson is not null)
            {
                if (GatewayRouting.HasPortalLeaveStack(sourceSession.World, primaryPerson) ||
                    primaryPerson[PersonData.GreetStatus] >= 2)
                {
                    sourceSession.World.ResetObjectForChangedCode(primaryPerson);
                }
                if (sourceSession.Away && primaryPerson[PersonData.GreetStatus] >= 2)
                    primaryPerson[PersonData.GreetStatus] = 1;
                GatewayRouting.CancelPersonRoutesAndActions(sourceSession.World, primaryPerson);
                var sourceGateway = GatewayRouting.ResolveWalkableSidewalkTile(
                    sourceSession.World, sourceSession.Lot, primaryPerson);
                var blueprint = EnsureWorldBlueprint();
                lotPool.TryGetLive(homeHouse, out var liveHomeSession);
                adjacentPreviewWorlds.TryGetValue(homeHouse, out var previewHomeWorld);
                var destGateway = NeighborhoodStreetGraph.ResolveArrivalGatewayTile(
                    sourceHouse,
                    homeHouse,
                    liveHomeSession?.Lot ?? previewHomeWorld?.Lot,
                    liveHomeSession?.World ?? previewHomeWorld?.World,
                    blueprint);
                var worldWaypoints = NeighborhoodStreetGraph.ComputeSidewalkPath(
                    blueprint, sourceHouse, sourceGateway, homeHouse, destGateway).ToList();
                float totalDist = NeighborhoodStreetGraph.ComputeTotalDistance(worldWaypoints);
                var startWorldPt = worldWaypoints.Count > 0 ? worldWaypoints[0] : (0f, 0f);
                var transit = new CrossLotTransitState
                {
                    SimNeighborId = primaryNid,
                    SimObjectId = primaryPerson.Id,
                    SourceHouse = sourceHouse,
                    TargetHouse = homeHouse,
                    IsReturnHome = true,
                    KnockOnArrival = false,
                    CompanionNeighborIds = awayNids.Skip(1).ToList(),
                    Phase = CrossLotTransitPhase.RoutingToGateway,
                    SourceGatewayTile = sourceGateway,
                    DestinationEntryTile = destGateway,
                    DestinationGatewayTile = destGateway,
                    StageTicksElapsed = 0,
                    MaxStageTicks = 90,
                    WorldWaypoints = worldWaypoints,
                    WaypointIndex = worldWaypoints.Count > 1 ? 1 : 0,
                    CurrentWorldX = startWorldPt.Item1,
                    CurrentWorldY = startWorldPt.Item2,
                    TotalWorldDistance = totalDist,
                };
                activeCrossLotTransits[primaryNid] = transit;
                if (neighborhoodManager.GetSimDetails(primaryNid) is { } loc)
                    loc.ActivitySummary = "In transit";

                try
                {
                    sourceSession.World.StartDirectPositionRoute(primaryPerson, sourceGateway);
                }
                catch
                {
                    transit.Phase = CrossLotTransitPhase.OffLotSeamWalk;
                }

                return new
                {
                    house = lotPool.FocusedHouse > 0 ? lotPool.FocusedHouse : house,
                    targetHouse = homeHouse,
                    focusedHouse = lotPool.FocusedHouse > 0 ? lotPool.FocusedHouse : house,
                    homeHouse,
                    away = IsVisitingAwayLot,
                    inTransit = true,
                    phase = transit.Phase.ToString(),
                    state = State(),
                };
            }
        }

        foreach (var nid in awayNids)
            CancelCrossLotTransitForNeighbor(nid, cancelRoutes: true, repositionFromGateway: false);

        return ExecuteReturnHomeTransfer(awayNids, homeHouse, homeFam, homeGuids, userData);
    }

    private object ExecuteReturnHomeTransfer(
        IReadOnlyList<short> awayNids,
        short homeHouse,
        Family? homeFam,
        HashSet<uint> homeGuids,
        string userData,
        ObjectPosition? overrideEntryTile = null)
    {
        var sourceSpeed = speed > 0 ? speed : lastRunningSpeed;
        var captured = new List<(short SourceHouse, TransferredSimSnapshot Snapshot)>();
        var touchedAwaySessions = new Dictionary<short, LotSession>();

        foreach (var nid in awayNids)
        {
            var sourceHouse = (short)(neighborhoodManager.GetSimLocation(nid) ?? house);
            if (sourceHouse <= 0 || sourceHouse == homeHouse) continue;
            var awaySession = GetOrLoadPoolSession(sourceHouse, userData);
            touchedAwaySessions[sourceHouse] = awaySession;
            var snap = CaptureAndRemoveTravellerFromSession(awaySession, nid, homeFam, userData);
            captured.Add((sourceHouse, snap));
        }

        SyncLiveNeighborRecordsToNeighborhood();

        RemoveTileCursor();
        ResetSeasonalOutfits();
        audio?.Reset();
        refreshedPersonFiles.Clear();

        var homeSession = GetOrLoadPoolSession(homeHouse, userData);
        var homeWorld = homeSession.World;
        homeWorld.Neighborhood = neighborhoodManager;
        homeWorld[SimulationGlobal.SimlessBuildMode] = 0;
        homeSession.Away = false;
        homeSession.HomeHouse = homeHouse;
        homeSession.RestrictedActivePersonKeys = null;
        if (homeWorld.FamilyBudget is FamilyBudget fb)
            fb.RestrictActivePersonKeys(null);
        SyncNeighborhoodRecordsIntoLiveWorld(homeWorld, neighborhood, CollectGloballyRemovedNeighbors());

        var returnedPeople = new List<XPerson>();
        for (var i = 0; i < captured.Count; i++)
        {
            var (sourceHouse, snap) = captured[i];
            var returned = InstantiateAndPlaceArrivingSim(
                homeSession,
                snap,
                sourceHouse,
                isVisiting: false,
                greetUnlocked: false,
                spreadIndex: i,
                overrideEntryTile: i == 0 ? overrideEntryTile : null);
            returnedPeople.Add(returned);
        }

        homeWorld.RefreshNeighborInteractionManager();

        foreach (var awaySession in touchedAwaySessions.Values)
            CleanupVacatedAwaySession(awaySession, homeHouse, homeFam, userData);

        lotPool.PutLive(homeSession);
        lotPool.FocusedHouse = homeHouse;
        EnforceMaxLiveSplitLots();
        neighborControllerSeedDirty = true;

        var primaryReturned = returnedPeople.FirstOrDefault();
        if (primaryReturned is not null)
            homeSession.SelectedSim = primaryReturned.Id;

        ApplyLotSessionToServer(homeSession);
        if (primaryReturned is not null && homeWorld.ObjectById(primaryReturned.Id) is XPerson)
        {
            selectedSim = primaryReturned.Id;
            SyncSelectedSimFlags(world!, selectedSim);
            homeSession.SelectedSim = selectedSim;
        }
        else if (world!.ObjectById(selectedSim) is not XPerson sel || !IsHouseholdPerson(sel))
        {
            selectedSim = DefaultSelectedSimId();
            SyncSelectedSimFlags(world!, selectedSim);
            homeSession.SelectedSim = selectedSim;
        }

        neighborhoodManager.SyncFromWorld(world!, homeHouse, activeLotIds: CollectActivePoolLotIds());
        ClampAwayLotEditingState();
        var resumeSpeed = sourceSpeed > 0 ? sourceSpeed : 1;
        lastRunningSpeed = resumeSpeed;
        homeSession.Speed = resumeSpeed;
        SetSimulationSpeed(resumeSpeed, blockingDialog: HasBlockingDialog(world!));

        if (audio?.Enabled == true)
            audio.PlayLotAmbience(house);

        Write(new { type = "lotChanged", house, away = IsVisitingAwayLot });
        Export();
        var finalState = State();
        Write(new { type = "state", state = finalState });

        return new
        {
            house,
            focusedHouse = lotPool.FocusedHouse,
            homeHouse = EffectiveHomeHouse,
            away = IsVisitingAwayLot,
            state = finalState,
        };
    }

    private void TickCrossLotTransits()
    {
        if (activeCrossLotTransits.Count == 0) return;

        var userData = Path.GetDirectoryName(neighborhoodPath) ?? Neighborhoods.UserData(profile);
        var homeHouse = lotPool.HomeHouse > 0 ? lotPool.HomeHouse : EffectiveHomeHouseValue();
        var homeFam = HomeFamily();
        var homeGuids = homeFam?.Members.ToHashSet() ?? [];

        foreach (var transit in activeCrossLotTransits.Values.ToArray())
        {
            if (!lotPool.TryGetLive(transit.SourceHouse, out var sourceSession))
            {
                activeCrossLotTransits.Remove(transit.SimNeighborId);
                continue;
            }

            if (sourceSession.House != house && sourceSession.Speed == 0)
                continue;

            var person = sourceSession.World.WalkOrder.OfType<XPerson>()
                .FirstOrDefault(p => p[PersonData.NeighborId] == transit.SimNeighborId);
            var trackedLot = neighborhoodManager.GetSimLocation(transit.SimNeighborId);
            if (person is null || (trackedLot is > 0 && trackedLot != transit.SourceHouse))
            {
                if (person is not null)
                    sourceSession.World.TransitVisualOverrides.Remove(person.Id);
                if (transit.SimObjectId > 0)
                    sourceSession.World.TransitVisualOverrides.Remove(transit.SimObjectId);
                activeCrossLotTransits.Remove(transit.SimNeighborId);
                continue;
            }

            transit.SimObjectId = person.Id;
            transit.StageTicksElapsed++;

            if (transit.Phase == CrossLotTransitPhase.RoutingToGateway)
            {
                var dx = (person.Position.X - transit.SourceGatewayTile.X) / 16f;
                var dy = (person.Position.Y - transit.SourceGatewayTile.Y) / 16f;
                var distSq = dx * dx + dy * dy;

                // If the initial sidewalk route ended early while still far from the sidewalk,
                // retry routing to an alternate safe sidewalk tile before handing off cleanly to OffLotSeamWalk.
                if (person.NativeRoute is null && distSq > 9f && transit.StageTicksElapsed < 60 && (transit.StageTicksElapsed % 20 == 1))
                {
                    var retryTile = GatewayRouting.ResolveWalkableSidewalkTile(
                        sourceSession.World, sourceSession.Lot, person, spreadIndex: transit.StageTicksElapsed / 20 + 1);
                    transit.SourceGatewayTile = retryTile;
                    try { sourceSession.World.StartDirectPositionRoute(person, retryTile); }
                    catch { }
                }

                var arrivedAtGateway = distSq <= 2.25f ||
                                       (person.NativeRoute is null && (distSq <= 9f || transit.StageTicksElapsed >= 60)) ||
                                       transit.StageTicksElapsed >= transit.MaxStageTicks;
                if (arrivedAtGateway)
                {
                    GatewayRouting.CancelPersonRoutesAndActions(sourceSession.World, person);
                    if (transit.WorldWaypoints.Count >= 1)
                    {
                        var srcParcelInit = EnsureWorldBlueprint().GetParcelOrDefault(transit.SourceHouse);
                        float initWx = srcParcelInit.WorldTileX + person.Position.X / 16f;
                        float initWy = srcParcelInit.WorldTileY + person.Position.Y / 16f;
                        transit.WorldWaypoints[0] = (initWx, initWy);
                        transit.CurrentWorldX = initWx;
                        transit.CurrentWorldY = initWy;
                        transit.WaypointIndex = transit.WorldWaypoints.Count > 1 ? 1 : 0;
                        transit.TotalWorldDistance = NeighborhoodStreetGraph.ComputeTotalDistance(transit.WorldWaypoints);
                        transit.TraveledWorldDistance = 0f;
                    }
                    transit.Phase = CrossLotTransitPhase.OffLotSeamWalk;
                    transit.StageTicksElapsed = 0;
                    transit.SeamProgress = 0f;
                }
            }
            else if (transit.Phase == CrossLotTransitPhase.OffLotSeamWalk)
            {
                int dir = person[ObjectData.Direction] & 7;
                float relTileX;
                float relTileY;

                if (transit.WorldWaypoints.Count >= 2 && transit.TotalWorldDistance > 0.25f)
                {
                    float remainingStep = MathF.Max(0.35f, transit.TotalWorldDistance / 22f);
                    float moveDx = 0f;
                    float moveDy = 0f;

                    while (remainingStep > 0.001f && transit.WaypointIndex < transit.WorldWaypoints.Count)
                    {
                        var (targetWx, targetWy) = transit.WorldWaypoints[transit.WaypointIndex];
                        float segDx = targetWx - transit.CurrentWorldX;
                        float segDy = targetWy - transit.CurrentWorldY;
                        float segLen = MathF.Sqrt(segDx * segDx + segDy * segDy);

                        if (segLen <= 0.001f)
                        {
                            transit.WaypointIndex++;
                            continue;
                        }

                        moveDx = segDx;
                        moveDy = segDy;

                        if (segLen <= remainingStep)
                        {
                            transit.CurrentWorldX = targetWx;
                            transit.CurrentWorldY = targetWy;
                            transit.TraveledWorldDistance += segLen;
                            remainingStep -= segLen;
                            transit.WaypointIndex++;
                        }
                        else
                        {
                            float ratio = remainingStep / segLen;
                            transit.CurrentWorldX += segDx * ratio;
                            transit.CurrentWorldY += segDy * ratio;
                            transit.TraveledWorldDistance += remainingStep;
                            remainingStep = 0f;
                        }
                    }

                    if (MathF.Abs(moveDx) > 0.001f || MathF.Abs(moveDy) > 0.001f)
                    {
                        float angle = MathF.Atan2(moveDx, -moveDy);
                        dir = ((int)MathF.Floor(angle / (MathF.PI / 4f) + 8.5f)) & 7;
                        person[ObjectData.Direction] = (short)dir;
                    }

                    var srcParcel = EnsureWorldBlueprint().GetParcelOrDefault(transit.SourceHouse);
                    // Unclamped relative coordinates allow walking smoothly across neighbors, intersections, and river bridges.
                    relTileX = transit.CurrentWorldX - srcParcel.WorldTileX;
                    relTileY = transit.CurrentWorldY - srcParcel.WorldTileY;
                    transit.SeamProgress = Math.Clamp(
                        transit.WaypointIndex >= transit.WorldWaypoints.Count
                            ? 1f
                            : transit.TraveledWorldDistance / MathF.Max(0.5f, transit.TotalWorldDistance),
                        0f,
                        1f);

                    try { GatewayRouting.EnsureTransitWalkAnimation(sourceSession.World, person); }
                    catch { }
                }
                else
                {
                    transit.SeamProgress = Math.Clamp(transit.StageTicksElapsed / 16f, 0f, 1f);
                    var (stepDx, stepDy) = dir switch
                    {
                        0 => ( 0f, -1f),
                        1 => ( 0.707f, -0.707f),
                        2 => ( 1f,  0f),
                        3 => ( 0.707f,  0.707f),
                        4 => ( 0f,  1f),
                        5 => (-0.707f,  0.707f),
                        6 => (-1f,  0f),
                        _ => (-0.707f, -0.707f),
                    };
                    relTileX = person.Position.X / 16f + stepDx * transit.SeamProgress * 1.5f;
                    relTileY = person.Position.Y / 16f + stepDy * transit.SeamProgress * 1.5f;
                }

                sourceSession.World.TransitVisualOverrides[person.Id] = new CrossLotTransitVisualOverride(
                    Math.Clamp(relTileX, 0f, 63.9375f),
                    Math.Clamp(relTileY, 0f, 63.9375f),
                    dir,
                    transit.SeamProgress,
                    transit.SourceHouse,
                    transit.TargetHouse,
                    WorldRelTileX: relTileX,
                    WorldRelTileY: relTileY);

                if (transit.SeamProgress >= 1f)
                {
                    GatewayRouting.ClearTransitWalkAnimation(sourceSession.World, person);
                    sourceSession.World.TransitVisualOverrides.Remove(person.Id);
                    activeCrossLotTransits.Remove(transit.SimNeighborId);
                    var arrivalTarget = transit.DestinationArrivalTarget;
                    var entryOverride = transit.DestinationGatewayTile.X > 0 && transit.DestinationGatewayTile.Y > 0
                        ? transit.DestinationGatewayTile
                        : (ObjectPosition?)null;
                    var nids = new List<short> { transit.SimNeighborId };
                    foreach (var compNid in transit.CompanionNeighborIds)
                    {
                        if (compNid <= 0 || compNid == transit.SimNeighborId) continue;
                        var compLoc = neighborhoodManager.GetSimLocation(compNid);
                        if ((compLoc is null or 0 || compLoc == transit.SourceHouse) &&
                            sourceSession.World.WalkOrder.OfType<XPerson>().Any(p => p[PersonData.NeighborId] == compNid))
                        {
                            nids.Add(compNid);
                        }
                    }
                    FlushFocusedLotSessionToPool();
                    if (transit.IsReturnHome)
                        ExecuteReturnHomeTransfer(nids, transit.TargetHouse, homeFam, homeGuids, userData, entryOverride);
                    else
                    {
                        ExecuteVisitLotTransfer(transit.TargetHouse, nids, transit.KnockOnArrival, homeHouse, homeFam, homeGuids, userData, entryOverride);
                        // Walk seamlessly from the neighbor's sidewalk to the exact tile the player clicked!
                        if (!transit.KnockOnArrival && arrivalTarget.X > 0 && arrivalTarget.Y > 0 &&
                            world?.ObjectById(selectedSim) is XPerson arrivedSim)
                        {
                            try { world.StartDirectPositionRoute(arrivedSim, arrivalTarget); }
                            catch { }
                        }
                    }
                }
            }
        }
    }

    // =========================================================================
    // F7: Cross-Lot Multi-Sim Split Control (selectSim across active lots)
    // F8: Synthetic Street-Grid Layout & Cached Adjacent Lot Scene Export
    // =========================================================================

    private bool TrySelectConnectedSim(JsonElement request, out object? result)
    {
        result = null;
        short requestedObjectId = 0;
        bool hasObjectId = request.TryGetProperty("objectID", out var objProp) &&
                           objProp.ValueKind == JsonValueKind.Number &&
                           objProp.TryGetInt16(out requestedObjectId);
        short requestedNeighborId = 0;
        bool hasNeighborId = request.TryGetProperty("neighborID", out var nidProp) &&
                             nidProp.ValueKind == JsonValueKind.Number &&
                             nidProp.TryGetInt16(out requestedNeighborId) &&
                             requestedNeighborId > 0;
        short requestedLotId = 0;
        bool hasLotId = request.TryGetProperty("lotId", out var lotProp) &&
                        lotProp.ValueKind == JsonValueKind.Number &&
                        lotProp.TryGetInt16(out requestedLotId) &&
                        requestedLotId > 0;

        // If the requested objectID already lives on the currently focused lot and no cross-lot target was named,
        // let the standard single-lot selectSim path handle it.
        if (hasObjectId && !hasNeighborId && (!hasLotId || requestedLotId == house) &&
            world?.ObjectById(requestedObjectId) is XPerson)
        {
            return false;
        }

        // Resolve target house + neighborId across live sessions and neighborhood tracking.
        short targetHouse = 0;
        short resolvedNeighborId = requestedNeighborId;

        if (hasLotId && requestedLotId != house)
        {
            targetHouse = requestedLotId;
        }

        if (resolvedNeighborId > 0 && targetHouse <= 0)
        {
            var trackedLot = neighborhoodManager.GetSimLocation(resolvedNeighborId);
            if (trackedLot is > 0 && trackedLot <= short.MaxValue)
                targetHouse = (short)trackedLot.Value;
        }

        if (targetHouse <= 0 && hasObjectId && requestedObjectId > 0)
        {
            foreach (var session in lotPool.LiveSessions.Values)
            {
                if (session.House == house) continue;
                if (session.World.ObjectById(requestedObjectId) is XPerson otherPerson)
                {
                    targetHouse = session.House;
                    if (resolvedNeighborId <= 0)
                        resolvedNeighborId = otherPerson[PersonData.NeighborId];
                    break;
                }
            }
        }

        if (targetHouse <= 0 && hasObjectId && requestedObjectId > 0)
        {
            foreach (var entry in neighborhoodManager.GetAllSimLocations().Values)
            {
                if (entry.LotObjectId == requestedObjectId && entry.CurrentLotId > 0 && entry.CurrentLotId <= short.MaxValue)
                {
                    targetHouse = (short)entry.CurrentLotId;
                    if (resolvedNeighborId <= 0)
                        resolvedNeighborId = entry.SimId;
                    break;
                }
            }
        }

        if (targetHouse <= 0)
            return false;

        if (targetHouse != house)
        {
            FocusLotInternal(targetHouse);
        }

        if (world is null)
            return false;

        XPerson? targetPerson = null;
        if (resolvedNeighborId > 0)
        {
            targetPerson = world.WalkOrder.OfType<XPerson>()
                .FirstOrDefault(p => p[PersonData.NeighborId] == resolvedNeighborId);
        }
        if (targetPerson is null && hasObjectId && requestedObjectId > 0)
        {
            targetPerson = world.ObjectById(requestedObjectId) as XPerson;
        }
        if (targetPerson is null)
            return false;

        selectedSim = targetPerson.Id;
        SyncSelectedSimFlags(world, selectedSim);
        world.DispatchNotice(0xe4, selectedSim);
        RefreshDynamicOrExport();
        result = State();
        return true;
    }

    private static (int[] Counts, float[] Centers) DetectAllRoadEdges(IffFile? targetLot, int lotSize)
    {
        int size = Math.Clamp(lotSize, 16, SimsEngine.World.LotFloors.Size);
        int[] counts = new int[4];
        int[] mins = [int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue];
        int[] maxs = [int.MinValue, int.MinValue, int.MinValue, int.MinValue];
        float[] centers = [4.5f, size - 4.5f, size - 4.5f, 4.5f];
        if (targetLot is null) return (counts, centers);
        try
        {
            var floors = SimsEngine.World.LotFloors.Read(targetLot, 1);
            var colCounts = new int[size];
            var rowCounts = new int[size];
            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y < size; y++)
                {
                    ushort p = floors[x, y];
                    if (p != 9) continue;
                    colCounts[x]++;
                    rowCounts[y]++;
                }
            }

            // A true road spans the full lot length (>= 15 tiles), whereas a perpendicular road crossing
            // only contributes 5..7 tiles to a perpendicular row/column — zero corner-lot skew!
            int minSpan = Math.Min(15, size / 2);
            int half = size / 2;
            for (int y = 0; y < size; y++)
            {
                if (rowCounts[y] < minSpan) continue;
                int edge = y < half ? 0 : 2;
                counts[edge] += rowCounts[y];
                if (y < mins[edge]) mins[edge] = y;
                if (y > maxs[edge]) maxs[edge] = y;
            }
            for (int x = 0; x < size; x++)
            {
                if (colCounts[x] < minSpan) continue;
                int edge = x < half ? 3 : 1;
                counts[edge] += colCounts[x];
                if (x < mins[edge]) mins[edge] = x;
                if (x > maxs[edge]) maxs[edge] = x;
            }

            for (int e = 0; e < 4; e++)
            {
                if (counts[e] > 0 && mins[e] <= maxs[e])
                    centers[e] = (mins[e] + maxs[e] + 1) * 0.5f;
            }
            return (counts, centers);
        }
        catch
        {
            return (counts, centers);
        }
    }

    private static (int RoadEdge, float RoadCenter) DetectRoadEdgeAndCenter(IffFile? targetLot, int lotSize)
    {
        var (counts, centers) = DetectAllRoadEdges(targetLot, lotSize);
        int bestEdge = 2;
        int bestCount = counts[2];
        for (int e = 0; e < 4; e++)
        {
            if (counts[e] > bestCount)
            {
                bestCount = counts[e];
                bestEdge = e;
            }
        }
        return (bestEdge, centers[bestEdge]);
    }

    private static int DetectRoadEdge(IffFile? targetLot, int lotSize = 64) =>
        DetectRoadEdgeAndCenter(targetLot, lotSize).RoadEdge;

    private void TickAdjacentPreviewWorlds()
    {
        if (!connectedOptions.EnableAdjacentLotRendering || world is null || speed == 0)
            return;

        // Detect when visitors arrive on or leave the focused lot so we immediately deduplicate them on adjacent lots.
        var focusedVisitorSig = string.Join(",",
            world.WalkOrder.OfType<XPerson>()
                .Select(p => p[PersonData.NeighborId])
                .Where(nid => nid > 0)
                .OrderBy(nid => nid));
        if (!string.Equals(lastFocusedVisitorSignature, focusedVisitorSig, StringComparison.Ordinal))
        {
            lastFocusedVisitorSignature = focusedVisitorSig;
            adjacentRenderVersion++;
            foreach (var preview in adjacentPreviewWorlds.Values)
                preview.Revision++;
        }

        if (adjacentPreviewWorlds.Count == 0)
            return;

        adjacentTickCounter++;
        var control = new SimulationClockControl(1, 1, 1);
        foreach (var (adjHouse, preview) in adjacentPreviewWorlds.ToArray())
        {
            if (adjHouse == house || lotPool.TryGetLive(adjHouse, out _) || preview.TickingPaused)
                continue;
            if (preview.World[SimulationGlobal.SimlessBuildMode] != 0 ||
                preview.World.PendingLotSwitch is not null)
                continue;

            // Auto-dismiss any tutorial or modal dialogs on background preview lots so neighbors never freeze.
            foreach (var dialog in preview.World.DialogRecords)
            {
                if (dialog.State == 0)
                {
                    dialog.State = 2;
                    dialog.Visible = false;
                }
            }

            preview.World[SimulationGlobal.IsTutorialHouseNoYes] = 0;
            preview.World[SimulationGlobal.FreeWill] = 1;
            // Prevent unvisited background preview houses from randomly burning down or spawning Grim Reapers or duplicate paperboys off-screen
            StripDangerousOrMagicDeliveryObjects(preview.World, stripMagicDelivery: false, stripDuplicatePaperCarrier: true);
            try
            {
                preview.World.AdvanceSimulationTick(control, 0, preview.World.Clock.Ticks);
            }
            catch
            {
                preview.TickingPaused = true;
            }
        }

        // Live 30fps multi-lot animation frames are streamed via EmitAdjacentDirectAnimations;
        // no need to trigger heavy full-scene reloads every 1.5s here.
    }

    private void TickSharedMorningPaperDelivery()
    {
        if (!ConnectedNeighborhoodEnabled || !connectedOptions.EnableAdjacentLotRendering ||
            world is null || registry is null)
        {
            return;
        }

        if (world.Clock.Hour is < 7 or >= 9 || world.Clock.Day == lastSharedPaperDeliveryDay)
            return;

        lastSharedPaperDeliveryDay = world.Clock.Day;
        const uint newspaperGuid = 0xE6B26801u;
        var blueprint = EnsureWorldBlueprint();

        static void DeliverPaperToWorld(short lotHouse, SimulationWorld targetWorld, IffFile? lotFile, NeighborhoodWorldBlueprint bp, ref bool delivered)
        {
            if (targetWorld[SimulationGlobal.SimlessBuildMode] != 0)
                return;

            bool hasPaper = targetWorld.WalkOrder.Any(o =>
                o.TypeRecord is { } tr && unchecked((uint)tr.Key) == newspaperGuid);
            if (hasPaper)
                return;

            if (targetWorld.ObjectFactory?.Invoke(unchecked((int)newspaperGuid)) is not XObject paper)
                return;

            try
            {
                targetWorld.Add(paper);
                targetWorld.InitializeCreatedObject(paper);
                var sidewalkPos = NeighborhoodStreetGraph.ResolveArrivalGatewayTile(lotHouse, lotHouse, lotFile, targetWorld, bp);
                targetWorld.PlaceCreatedObject(paper, sidewalkPos);
                delivered = true;
            }
            catch
            {
                try { targetWorld.Remove(paper.Id); } catch { }
            }
        }

        bool anyUpdated = false;
        foreach (var (adjHouse, preview) in adjacentPreviewWorlds)
        {
            if (adjHouse <= 0 || adjHouse == house || IsCommunityUnifiedLot(adjHouse) || preview.TickingPaused)
                continue;

            bool delivered = false;
            DeliverPaperToWorld(adjHouse, preview.World, preview.Lot, blueprint, ref delivered);
            if (delivered)
            {
                preview.Revision++;
                anyUpdated = true;
            }
        }

        if (anyUpdated)
            adjacentRenderVersion++;
    }

    private void EmitAdjacentDirectAnimations(float subTickAlpha)
    {
        if (AdjacentDirectAnimationSink is not { } adjSink ||
            !connectedOptions.EnableAdjacentLotRendering ||
            registry is null ||
            adjacentSceneCache.Count == 0)
        {
            return;
        }

        // Stream adjacent lot animations at ~15fps (every 2nd animation frame) for smooth real-time
        // neighbor movement without doubling full CPU cost on every single tick.
        if ((++adjacentAnimFrameCounter & 1) != 0)
            return;

        foreach (var (adjHouse, cached) in adjacentSceneCache)
        {
            if (adjHouse == house || cached.AnimationState.Kept is null || cached.AnimationState.Revision != cached.Revision)
                continue;

            IffFile? exportLot = null;
            SimulationWorld? exportWorld = null;
            if (lotPool.TryGetLive(adjHouse, out var liveSession) && liveSession is not null)
            {
                exportLot = liveSession.Lot;
                exportWorld = liveSession.World;
            }
            else if (adjacentPreviewWorlds.TryGetValue(adjHouse, out var preview))
            {
                exportLot = preview.Lot;
                exportWorld = preview.World;
            }

            if (exportLot is null || exportWorld is null)
                continue;

            try
            {
                if (SceneExporter.AnimationDirect(
                        exportLot,
                        exportWorld,
                        registry,
                        game,
                        cached.AdjDir,
                        adjHouse,
                        cached.Revision,
                        cached.AnimationState,
                        ++adjacentAnimationSequence,
                        updates,
                        viewLevel: cached.ViewLevel,
                        wallMode: cached.WallMode,
                        cameraOrientation: cached.CameraOrientation,
                        cutawayTile: null,
                        roofsVisible: cached.RoofsVisible,
                        subTickAlpha: subTickAlpha,
                        hideInteriorPeople: cached.HideInteriorPeople) is { } directFrame)
                {
                    adjSink(adjHouse, directFrame);
                }
            }
            catch
            {
                // Ignore transient animation errors on background adjacent lots
            }
        }
    }

    private NeighborhoodWorldBlueprint EnsureWorldBlueprint() =>
        cachedWorldBlueprint ??= NeighborhoodWorldBlueprint.LoadFromUserData(Neighborhoods.UserData(profile));

    private NeighborhoodLayoutPlan ComputeCurrentNeighborhoodLayout(
        short focusedHouse,
        int? cameraTileX = null,
        int? cameraTileY = null)
    {
        var summaries = neighborhoodManager.GetAllLotSummaries().Values.ToList();
        int focusedLotSize = world is not null && world.Globals.Length > 23 && world.Globals[23] is > 0 and <= 64
            ? world.Globals[23]
            : NeighborhoodStreetLayout.LotSizeTiles;
        int focusedRoadEdge = DetectRoadEdge(lot, focusedLotSize);
        return NeighborhoodStreetLayout.ComputeLayout(
            focusedHouse,
            summaries,
            maxAdjacentLots: 14,
            focusedRoadEdge: focusedRoadEdge,
            lotSizeTiles: focusedLotSize,
            blueprint: EnsureWorldBlueprint(),
            cameraTileX: cameraTileX,
            cameraTileY: cameraTileY);
    }

    private object ExportAdjacentLotScenesPayload(bool force = false, int? cameraTileX = null, int? cameraTileY = null)
    {
        if (!ConnectedNeighborhoodEnabled || !connectedOptions.EnableAdjacentLotRendering || house <= 0 || world is null)
        {
            return new
            {
                focusedHouse = (int)house,
                adjacentLots = Array.Empty<object>(),
                roadStrips = Array.Empty<object>(),
            };
        }

        var focusedHouseVal = lotPool.FocusedHouse > 0 ? lotPool.FocusedHouse : house;
        var blueprint = EnsureWorldBlueprint();
        var worldOrigin = blueprint.GetParcelOrDefault(focusedHouseVal);
        var layoutPlan = ComputeCurrentNeighborhoodLayout(focusedHouseVal, cameraTileX, cameraTileY);
        var userData = Neighborhoods.UserData(profile);
        int focusedLotSize = world.Globals.Length > 23 && world.Globals[23] is > 0 and <= 64
            ? world.Globals[23]
            : NeighborhoodStreetLayout.LotSizeTiles;
        var (focusedCounts, focusedCenters) = DetectAllRoadEdges(lot, focusedLotSize);
        var (focusedRoadEdge, focusedRoadCenter) = DetectRoadEdgeAndCenter(lot, focusedLotSize);
        int focusedSecondaryRoadEdge = -1;
        float focusedSecondaryRoadCenter = 0f;
        int perpA = (focusedRoadEdge + 1) & 3;
        int perpB = (focusedRoadEdge + 3) & 3;
        if (focusedCounts[perpA] >= 40 || focusedCounts[perpB] >= 40)
        {
            focusedSecondaryRoadEdge = focusedCounts[perpA] >= focusedCounts[perpB] ? perpA : perpB;
            focusedSecondaryRoadCenter = focusedCenters[focusedSecondaryRoadEdge];
        }
        var exportedLots = new List<object>(layoutPlan.AdjacentLots.Count);
        int minTileX = layoutPlan.MinTileX;
        int minTileY = layoutPlan.MinTileY;
        int maxTileX = layoutPlan.MaxTileX;
        int maxTileY = layoutPlan.MaxTileY;

        foreach (var placement in layoutPlan.AdjacentLots)
        {
            string? exportedPath = TryExportSingleAdjacentLotScene(placement, userData, force, out bool hasDoor, out int adjLotSize, out float adjRoadCenterInFocusedSpace);
            if (string.IsNullOrEmpty(exportedPath))
                continue;

            // Authoritative road-snapped map blueprint offsets are already symmetric and aligned to MasterRoadNetwork.
            int offsetX = placement.TileOffsetX;
            int offsetY = placement.TileOffsetY;

            minTileX = Math.Min(minTileX, offsetX);
            minTileY = Math.Min(minTileY, offsetY);
            maxTileX = Math.Max(maxTileX, offsetX + adjLotSize);
            maxTileY = Math.Max(maxTileY, offsetY + adjLotSize);

            exportedLots.Add(new
            {
                house = (int)placement.House,
                offsetX,
                offsetY,
                roadEdge = placement.RoadEdge,
                rotation = 0,
                lotName = placement.LotName,
                familyName = placement.FamilyName,
                occupied = placement.Occupied,
                isCommunity = placement.IsCommunity || IsCommunityUnifiedLot(placement.House),
                hasDoor,
                isLive = lotPool.TryGetLive(placement.House, out _),
                scenePath = exportedPath,
                revision = adjacentSceneCache.TryGetValue(placement.House, out var entry) ? entry.Revision : 0L,
            });
        }

        var strips = layoutPlan.RoadStrips.Select(s => new
        {
            minTileX = s.MinTileX,
            minTileY = s.MinTileY,
            maxTileX = s.MaxTileX,
            maxTileY = s.MaxTileY,
            horizontal = s.Horizontal,
            isIntersection = s.IsIntersection,
        }).ToArray();

        IReadOnlyList<NeighborhoodWorldAssetsService.VehicleSpriteAsset> vehicleAssets = [];
        IReadOnlyList<NeighborhoodWorldAssetsService.TreeSpriteAsset> treeAssets = [];
        if (registry is not null)
            (vehicleAssets, treeAssets) = NeighborhoodWorldAssetsService.GetOrBuildAssets(registry, SceneDirectory);

        var trafficSprites = vehicleAssets.Select(v => new
        {
            id = v.Id,
            dir = v.Direction,
            file = v.FilePath,
            width = v.Width,
            height = v.Height,
            anchorX = v.AnchorX,
            anchorY = v.AnchorY,
        }).ToArray();

        var treeSprites = treeAssets.Select(t => new
        {
            id = t.Id,
            file = t.FilePath,
            width = t.Width,
            height = t.Height,
            anchorX = t.AnchorX,
            anchorY = t.AnchorY,
        }).ToArray();

        return new
        {
            focusedHouse = (int)focusedHouseVal,
            worldOriginX = worldOrigin.WorldTileX,
            worldOriginY = worldOrigin.WorldTileY,
            focusedRoadEdge,
            focusedRoadCenter,
            focusedSecondaryRoadEdge,
            focusedSecondaryRoadCenter,
            focusedLotSize,
            minTileX,
            minTileY,
            maxTileX,
            maxTileY,
            adjacentLots = exportedLots,
            roadStrips = strips,
            trafficSprites,
            treeSprites,
        };
    }

    private HashSet<short> CollectNonLocalActiveNeighborIds(short targetHouse)
    {
        var presentElsewhere = new HashSet<short>();
        if (world is not null && house != targetHouse)
        {
            foreach (var p in world.WalkOrder.OfType<XPerson>())
            {
                var nid = p[PersonData.NeighborId];
                if (nid > 0) presentElsewhere.Add(nid);
            }
        }
        foreach (var session in lotPool.LiveSessions.Values)
        {
            if (session.House == targetHouse) continue;
            foreach (var p in session.World.WalkOrder.OfType<XPerson>())
            {
                var nid = p[PersonData.NeighborId];
                if (nid > 0) presentElsewhere.Add(nid);
            }
        }
        return presentElsewhere;
    }

    private string? TryExportSingleAdjacentLotScene(
        NeighborhoodLotPlacement placement,
        string userData,
        bool force,
        out bool hasDoor,
        out int adjLotSize,
        out float adjRoadCenterInFocusedSpace)
    {
        hasDoor = false;
        adjLotSize = NeighborhoodStreetLayout.LotSizeTiles;
        adjRoadCenterInFocusedSpace = adjLotSize - 4.5f;
        short targetHouse = placement.House;
        if (targetHouse <= 0 || targetHouse == house || registry is null)
            return null;

        bool isLive = lotPool.TryGetLive(targetHouse, out var liveSession);
        bool isCommunityLot = placement.IsCommunity || IsCommunityUnifiedLot(targetHouse);

        try
        {
            IffFile exportLot;
            SimulationWorld exportWorld;
            long expectedRevision;

            if (isLive && liveSession is not null)
            {
                exportLot = liveSession.Lot;
                exportWorld = liveSession.World;
                expectedRevision = revision + adjacentRenderVersion;
            }
            else
            {
                if (!adjacentPreviewWorlds.TryGetValue(targetHouse, out var preview))
                {
                    var lotPath = Path.Combine(userData, "Houses", $"House{targetHouse:00}.iff");
                    if (!File.Exists(lotPath))
                        return null;
                    var setup = WorldBootstrap.Load(
                        game,
                        lotPath,
                        language,
                        () => speed == 0,
                        targetHouse,
                        userDataPath: userData,
                        scoreAutonomy: ScoreAutonomyChoice,
                        existingRegistry: registry,
                        neighborhoodManager: neighborhoodManager,
                        connectedNeighborhoodEnabled: true,
                        preloadedNeighborhood: neighborhood);
                    var loadedWorld = setup.World;
                    if (world is not null)
                        loadedWorld.Clock.RestoreProgress(world.Clock.Ticks, world.Clock.Day);
                    if (loadedWorld.NeighborhoodData.Length > 1 && loadedWorld.NeighborhoodData[1] < 3)
                        loadedWorld.NeighborhoodData[1] = 3;
                    loadedWorld[SimulationGlobal.IsTutorialHouseNoYes] = 0;
                    loadedWorld.TutorialOwner = null;
                    loadedWorld[SimulationGlobal.FreeWill] = 1;
                    loadedWorld.AutonomyForced = true;

                    bool hasHousehold = placement.Occupied &&
                        (setup.ActiveFamilyChunkId is not null ||
                         loadedWorld.WalkOrder.OfType<XPerson>().Any(p => p[PersonData.FamilyNumber] > 0));
                    bool shouldSimulate = hasHousehold || isCommunityLot;
                    if (!shouldSimulate)
                    {
                        // Never simulate unoccupied vacant residential lots (prevents Mystery Man magic box & stray NPC spawns on empty grass)
                        loadedWorld[SimulationGlobal.SimlessBuildMode] = 1;
                    }
                    else
                    {
                        loadedWorld[SimulationGlobal.SimlessBuildMode] = 0;
                        // Advance initial ticks so occupied neighbors and Old Town community NPCs/pets populate and act.
                        var initControl = new SimulationClockControl(1, 1, 1);
                        for (int t = 0; t < 15; t++)
                        {
                            foreach (var dlg in loadedWorld.DialogRecords)
                                if (dlg.State == 0) { dlg.State = 2; dlg.Visible = false; }
                            try { loadedWorld.AdvanceSimulationTick(initControl, 0, loadedWorld.Clock.Ticks); }
                            catch { break; }
                        }
                    }
                    int loadedSize = loadedWorld.Globals.Length > 23 && loadedWorld.Globals[23] is > 0 and <= 64
                        ? loadedWorld.Globals[23]
                        : NeighborhoodStreetLayout.LotSizeTiles;
                    preview = new AdjacentPreviewWorld(setup.Lot, loadedWorld, DetectRoadEdge(setup.Lot, loadedSize))
                    {
                        ActiveFamilyChunkId = setup.ActiveFamilyChunkId,
                        ActiveFamilyField0x13C = setup.ActiveFamily?.Field0x13C ?? 0,
                        TickingPaused = !shouldSimulate,
                    };
                    adjacentPreviewWorlds[targetHouse] = preview;
                }
                exportLot = preview.Lot;
                exportWorld = preview.World;
                expectedRevision = preview.Revision + adjacentRenderVersion;
            }

            adjLotSize = exportWorld.Globals.Length > 23 && exportWorld.Globals[23] is > 0 and <= 64
                ? exportWorld.Globals[23]
                : NeighborhoodStreetLayout.LotSizeTiles;
            var (adjCounts, adjCenters) = DetectAllRoadEdges(exportLot, adjLotSize);
            int adjRoadEdge;
            float adjRoadCenter;
            if (adjCounts[placement.RoadEdge] >= 40)
            {
                adjRoadEdge = placement.RoadEdge;
                adjRoadCenter = adjCenters[placement.RoadEdge];
            }
            else
            {
                (adjRoadEdge, adjRoadCenter) = DetectRoadEdgeAndCenter(exportLot, adjLotSize);
            }

            adjRoadCenterInFocusedSpace = adjRoadCenter;

            // Maxis authored every HouseXX.iff on Neighborhood 1 & Old Town in the exact same world orientation!
            int adjCameraOrientation = cameraOrientation;

            // Sims 3-style interior privacy: keep uninvited residential neighbor houses closed (walls up + roof on + interior Sims hidden),
            // while keeping Community Lots, the player's own home lot, and any invited house completely open!
            bool canSeeInside = CanSeeInsideHouse(targetHouse, placement.Occupied);
            string adjWallMode = canSeeInside ? wallMode : "up";
            bool adjRoofsVisible = canSeeInside ? roofsVisible : true;
            bool hideInteriorPeople = !canSeeInside && !isCommunityLot;

            hasDoor = exportWorld.WalkOrder.Any(IsDoorObject);

            if (!force &&
                adjacentSceneCache.TryGetValue(targetHouse, out var cached) &&
                cached.CameraOrientation == adjCameraOrientation &&
                string.Equals(cached.WallMode, adjWallMode, StringComparison.Ordinal) &&
                cached.ViewLevel == viewLevel &&
                cached.RoofsVisible == adjRoofsVisible &&
                cached.HideInteriorPeople == hideInteriorPeople &&
                cached.Revision == expectedRevision &&
                (File.Exists(cached.ScenePath) || InMemorySessionAssets.TryReadBytes(cached.ScenePath, out _)))
            {
                hasDoor = cached.HasDoor;
                return cached.ScenePath;
            }

            // Deduplicate Sims: remove any Sim from the preview world who is currently visiting the focused lot or another live lot.
            if (!isLive)
            {
                var presentElsewhere = CollectNonLocalActiveNeighborIds(targetHouse);
                if (presentElsewhere.Count > 0)
                {
                    var duplicates = exportWorld.WalkOrder.OfType<XPerson>()
                        .Where(p => presentElsewhere.Contains(p[PersonData.NeighborId]))
                        .ToList();
                    foreach (var dup in duplicates)
                    {
                        exportWorld.Remove(dup.Id);
                    }
                }
            }

            var adjDir = Path.Combine(SceneDirectory, "adjacent", $"h{targetHouse:00}_o{adjCameraOrientation}_w{adjWallMode}_l{viewLevel}_r{(adjRoofsVisible ? 1 : 0)}");
            var adjAnimState = new SceneExporter.AnimationState();
            var exportedPath = SceneExporter.Export(
                exportLot,
                exportWorld,
                registry,
                game,
                adjDir,
                targetHouse,
                expectedRevision,
                viewLevel: viewLevel,
                wallMode: adjWallMode,
                cameraOrientation: adjCameraOrientation,
                cutawayTile: null,
                roofsVisible: adjRoofsVisible,
                state: adjAnimState,
                hideInteriorPeople: hideInteriorPeople);

            adjacentSceneCache[targetHouse] = new AdjacentSceneCacheEntry(
                targetHouse,
                adjCameraOrientation,
                adjWallMode,
                viewLevel,
                adjRoofsVisible,
                hideInteriorPeople,
                expectedRevision,
                hasDoor,
                exportedPath,
                adjDir,
                adjAnimState);
            return exportedPath;
        }
        catch
        {
            return null;
        }
    }
}
