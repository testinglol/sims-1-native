using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;

namespace SimsSimulation;

internal sealed partial class SimulationServer
{
    internal sealed record SavedDialogSnapshot(string Token, short OwnerId, short? IconId, byte Kind, string Title,
        string Message, string? Yes, string? No, string? Cancel, bool Blocking, bool AwaitsResponse, int State,
        bool Visible, ushort ImageBitmapId, byte? BookMode = null);

    internal sealed record SaveRuntimeMetadata(int Version, int Speed, PersonAnimationRuntimeSnapshot[]? PersonAnimations = null,
        string? LotSha256 = null, SavedDialogSnapshot[]? Dialogs = null, int? Budget = null,
        IReadOnlyList<int>? RestrictedActivePersonKeys = null, SavedSuspendedHomeLot? SuspendedHome = null,
        int? ClockTicks = null, int? ClockDay = null);

    internal sealed record SavedSuspendedHomeLot(short HomeHouse, ushort? HomeFamilyChunkId, string LotState,
        SaveRuntimeMetadata Runtime, int Speed);

    internal sealed record SaveEnvelope(int SchemaVersion, string Engine, string PackFingerprint, short House, long Updates,
        string State, SaveCompatibility.PackRecord[]? Packs = null,
        Dictionary<string, PluginHost.PluginSaveRecord>? Plugins = null, int Neighborhood = 1,
        SaveRuntimeMetadata? Runtime = null, string? NeighborhoodState = null,
        SavedSuspendedHomeLot? SuspendedHome = null,
        IReadOnlyList<int>? RestrictedActivePersonKeys = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ConnectedNeighborhoodSaveState? ConnectedNeighborhood = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? CorePackFingerprint = null);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
    private static double TickIntervalSeconds(int speed) => speed switch
    {
        3 => (172.0 - 171.0 * 924.0 / 1000.0) / 1000.0,
        10 => (172.0 - 171.0 * 994.0 / 1000.0) / 1000.0,
        _ => (172.0 - 171.0 * 760.0 / 1000.0) / 1000.0,
    };
    private readonly TextWriter output;
    private readonly string session;
    private readonly string profile;
    private readonly string dataRoot;
    private readonly string game;
    private short house;
    private readonly byte language;
    private SimulationWorld? world;
    public SimulationWorld? World => world;
    private FameTuning? fameTuning;
    private IReadOnlyList<string>? fameTitles;
    private IffFile? lot;
    private ObjectRegistry? registry;
    private SuspendedHomeLot? suspendedHomeLot;
    private (ushort FamilyChunkId, int VacationHouse)? committedVacationState;
    private int? lastTravelNeighborhoodMode;
    private readonly Dictionary<uint, IffFile> refreshedPersonFiles = new();

    private sealed record SuspendedHomeLot(short HomeHouse, ushort? HomeFamilyChunkId, byte[] LotBytes,
        SaveRuntimeMetadata Runtime, int Speed);
    private PanelImages? images;
    private IffFile? global;
    private string? neighborhoodPath;
    private IffFile? neighborhood;
    private ushort? activeFamilyChunkId;
    private readonly string? loadError;
    private long updates;
    private long revision;
    private long previewRevision;
    private bool running = true;
    private bool streaming;
    private bool simulatorBlocked;
    private readonly SceneExporter.AnimationState animationState = new();
    private long animationSequence;
    private int speed;
    // Pausing sets speed to zero without modifying the configured simulation speed, requiring the last
    // running mode to be retained separately.
    private int lastRunningSpeed = 1;
    private int autoSpeedSaved = -1;
    private Dictionary<ushort, char>? floorSoundCodes;
    private string wallMode = "down";
    private bool editingView;
    private bool doorWindowBuildToolActive;
    private bool roofsVisible;
    private (int X, int Y)? cutawayTile;
    private readonly List<ushort> cutawayRooms = [];
    private long exportedCutawaySimKey = -1;
    private int cutawayZoom = 2;
    private int cameraOrientation;
    private readonly TutorialEvents tutorialEvents = new();
    // Viewer zoom scale values (1 far, 2 medium, 3 near) map to simulation global 2.
    private short viewerMode;
    private short viewerScale;
    private int? lastButtonImage;
    private short lastButtonPerson;
    private bool saveDirty;
    private int dialogResumeSpeed;
    private string? simulationError;
    private int loggedThreadErrors;
    private int loggedInteractionGuardErrors;
    private int loggedQueuedInteractionStarts;
    private int loggedPrimitiveResults;
    private int loggedExecutionTraces;
    private int loggedRinkInitTraces;
    private int loggedNativeRoutes;
    private int loggedRouteSearches;
    private int loggedRouteCandidates;
    private bool loggedSimRoster;
    private short selectedSim;
    private readonly bool profile_ = Environment.GetEnvironmentVariable("SIMS_PROFILE") is not null;
    private TimeSpan tickTime;
    private TimeSpan animationTime;
    private double maxTickMs;
    private long profileWindowStart;
    private int animationsTimed;
    private double nextAnimation;
    private int ticksTimed;
    private (short Sim, long Tick, List<AvatarSceneGeometry.PosedHeadMesh> Meshes)? pieHead;
    private int viewLevel = 1;
    private readonly bool traceActions = Environment.GetEnvironmentVariable("SIMS_NATIVE_TRACE_ACTIONS") == "1";
    private readonly bool testFixtureEnabled = Environment.GetEnvironmentVariable("SIMS_NATIVE_TEST_FIXTURE") == "1";
    private bool testFixtureStartPositionUsed;
    private PluginHost? plugins;
    private PluginWorld? pluginWorld;
    private readonly string packFingerprint;
    private readonly string? corePackFingerprint;
    private readonly string? explicitInstallRoot;
    private HitAudioRuntime? audio;
    private SimOutfits? engineOutfits;
    private SeasonalOutfitService? seasonalOutfits;
    private (int House, int Minute, int? Suit) cachedOutdoorSuit = (-1, -1, null);
    private string? lastCheatCommand;

    private void ResetSeasonalOutfits()
    {
        seasonalOutfits?.Reset(world);
        cachedOutdoorSuit = (-1, -1, null);
    }
    private (int FurnishingsObjects, int ArchitectureObjects) initialObjectValuation;
    private IffFile? initialValuationLot;
    private bool lotValuationEdited;
    internal Action<SceneExporter.DirectAnimationFrame>? DirectAnimationSink { get; set; }
    internal Action<short, SceneExporter.DirectAnimationFrame>? AdjacentDirectAnimationSink { get; set; }
    private static bool IsExplicitNativeEnv =>
        string.Equals(Environment.GetEnvironmentVariable("SIMS_ENGINE")?.Trim(), "native", StringComparison.OrdinalIgnoreCase);

    private string SceneDirectory => IsExplicitNativeEnv ? session : Path.Combine(session, "scene");

    private SimulationServer(TextWriter output, string session, string profile, string game, short house, SimulationWorld? world, IffFile? lot,
        ObjectRegistry? registry, IffFile? global, string? loadError, byte language = 1,
        string? neighborhoodPath = null, IffFile? neighborhood = null, ushort? activeFamilyChunkId = null,
        string? installRoot = null)
    {
        this.output = output;
        this.session = session;
        this.profile = profile;
        this.game = game;
        this.explicitInstallRoot = installRoot;
        this.house = house;
        this.world = world;
        this.lot = lot;
        this.registry = registry;
        Directory.CreateDirectory(session);
        Directory.CreateDirectory(SceneDirectory);
        images = registry is null ? null : new PanelImages(SceneDirectory, registry);
        this.global = global;
        this.neighborhoodPath = neighborhoodPath;
        this.neighborhood = neighborhood;
        this.activeFamilyChunkId = activeFamilyChunkId;
        this.loadError = loadError;
        this.language = language;
        var metadata = Path.Combine(game, ".pack-stack.json");
        if (File.Exists(metadata))
        {
            using var metadataDoc = JsonDocument.Parse(File.ReadAllText(metadata));
            packFingerprint = metadataDoc.RootElement.GetProperty("fingerprint").GetString() ?? "unmounted-base-1.90";
            corePackFingerprint = metadataDoc.RootElement.TryGetProperty("coreFingerprint", out var coreProp) &&
                                  coreProp.ValueKind == JsonValueKind.String
                ? coreProp.GetString()
                : null;
        }
        else
        {
            packFingerprint = "unmounted-base-1.90";
            corePackFingerprint = null;
        }
        audio = new HitAudioRuntime(Path.Combine(session, "audio"), profile, Write, game)
        {
            SuppressActiveDuplicates = true
        };
        this.dataRoot = Path.GetFullPath(Path.Combine(profile, "..", "..", ".."));
        this.pluginWorld = new SimsSimulation.PluginWorld(
            () => this.world,
            () => this.house,
            () => this.activeFamilyChunkId,
            () => this.registry,
            () => this.speed == 0,
            () => this.lot,
            () => LotWidth,
            person => seasonalOutfits?.IsWearing(person) ?? false,
            (simId, suit) => EnsureSeasonalOutfits()?.SetSuitOverride(simId, suit));
        plugins = PluginHost.Create(game, dataRoot, Write, () => this.house,
            host => this.pluginWorld, userData: Neighborhoods.UserData(profile), world: this.pluginWorld,
            playSoundResource: (path, owner, cue, loop) => audio.PlayResource(path, owner, cue, loop),
            stopSoundResource: (owner, cue) => audio.StopResource(owner, cue));
        this.pluginWorld.SetInteractionFilter(plugins.AllowInteraction);
        if (world is not null)
        {
            if (registry is not null)
            {
                world.UiTextFile ??= registry.UiText;
                var avatarGeometry = AvatarSceneGeometry.For(game, registry);
            }
            AttachTutorialFlash(world);
        }
        if (traceActions && world is not null) world.CaptureExecutionTrace = true;
        selectedSim = DefaultSelectedSimId();
        if (world is not null) SyncSelectedSimFlags(world, selectedSim);
        InitializeViewLevelFromSelectedSim();
        Directory.CreateDirectory(session);
        if (world is not null) AttachNoticeListener(world);
        ResetLotValuationBaseline();
    }

    private void AttachTutorialFlash(SimulationWorld targetWorld)
    {
        targetWorld.TutorialFlash = (kind, value, on) =>
        {
            if (!ReferenceEquals(world, targetWorld)) return;
            switch (kind)
            {
                case TutorialFlashKind.ImageButton:
                    Write(new { type = "tutorialFlash", target = "image", value = (int)value, on });
                    break;
                case TutorialFlashKind.PersonButton:
                    if (targetWorld.Neighbors.ContainsKey(value)
                        && targetWorld.WalkOrder.OfType<XPerson>().FirstOrDefault(person => person[PersonData.NeighborId] == value) is { } sim)
                        Write(new { type = "tutorialFlash", target = "person", value = (int)sim.Id, on });
                    break;
                case TutorialFlashKind.RelationshipButton:
                    if (targetWorld.Neighbors.ContainsKey(value)) Write(new { type = "tutorialFlash", target = "relationship", value = (int)value, on });
                    break;
            }
        };
    }

    private ObjectRegistry EnsureRegistry()
    {
        if (registry is null)
        {
            var charsDir = Path.Combine(Neighborhoods.UserData(profile), "Characters");
            registry = ObjectRegistry.Scan(game, charsDir);
        }
        images ??= new PanelImages(SceneDirectory, registry);
        global ??= WorldBootstrap.GetOrLoadGlobalLibrary(game).Global;
        return registry;
    }

    private SeasonalOutfitService? EnsureSeasonalOutfits()
    {
        if (seasonalOutfits is not null) return seasonalOutfits;
        if (registry is null) return null;
        seasonalOutfits = new SeasonalOutfitService(game, registry);
        return seasonalOutfits;
    }

    private SimsNative.Plugins.PluginGroundPoint GroundPoint(SimulationWorld currentWorld, ObjectPosition position)
    {
        var tx = position.X >> 4;
        var ty = position.Y >> 4;
        var level = position.Height;
        ushort floorPattern = 0;
        if (lot is { } currentLot && tx is >= 0 and < LotFloors.Size && ty is >= 0 and < LotFloors.Size)
        {
            try { floorPattern = LotFloors.Read(currentLot, level)[tx, ty]; }
            catch (InvalidDataException) { }
        }
        var bare = floorPattern == 0 && !currentWorld.WalkOrder.Any(item =>
            item is not XPerson && item.Position.Height == level &&
            (item.Position.X >> 4) == tx && (item.Position.Y >> 4) == ty &&
            item.TypeRecord?.Key != 0x7c4);
        var outside = currentWorld.RoomIsOutside(currentWorld.RoomAtTile(tx, ty, level));
        var sheltered = SceneExporter.IsShelteredAt(lot, currentWorld, LotWidth, position);
        return new SimsNative.Plugins.PluginGroundPoint(tx, ty, level, bare, outside, sheltered);
    }

    private string ResolveInstallRoot()
    {
        if (!string.IsNullOrEmpty(explicitInstallRoot) && Directory.Exists(Path.Combine(explicitInstallRoot, "UserData")))
            return Path.GetFullPath(explicitInstallRoot);
        if (Directory.Exists(Path.Combine(game, "UserData"))) return game;
        var gameDataPath = Path.Combine(game, "GameData");
        if (Directory.Exists(gameDataPath) &&
            new DirectoryInfo(gameDataPath).ResolveLinkTarget(true)?.FullName is { } resolvedGameData &&
            Path.GetDirectoryName(resolvedGameData) is { } parent &&
            Directory.Exists(Path.Combine(parent, "UserData")))
            return parent;
        if (TryReadSavedInstallRoot(dataRoot) is { } savedInstall && Directory.Exists(Path.Combine(savedInstall, "UserData")))
            return Path.GetFullPath(savedInstall);
        if (Environment.GetEnvironmentVariable("SIMS_GAME_DIR") is { Length: > 0 } envGame &&
            Directory.Exists(Path.Combine(envGame, "UserData")))
            return Path.GetFullPath(envGame);
        foreach (var candidate in new[] { Path.Combine(dataRoot, "runtime", "game"), Path.Combine(dataRoot, "The Sims 1") })
            if (Directory.Exists(Path.Combine(candidate, "UserData")))
                return Path.GetFullPath(candidate);
        return game;
    }

    internal static string? TryReadSavedInstallRoot(string root)
    {
        try
        {
            var settingsFile = Path.Combine(root, "Settings", "install.json");
            if (!File.Exists(settingsFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(settingsFile));
            if (doc.RootElement.TryGetProperty("gamePath", out var prop) &&
                prop.ValueKind == JsonValueKind.String &&
                prop.GetString() is { Length: > 0 } path)
                return path;
        }
        catch
        {
        }
        return null;
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SimulationWorld, Action<short, AvatarInventory>> neighborInventoryHandlers = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SimulationWorld, object> attachedNoticeWorlds = new();

    private void AttachNoticeListener(SimulationWorld target)
    {
        target.PlaySoundEvent = (name, owner, pan, zoom) =>
        {
            if (!ReferenceEquals(world, target) || audio is null) return false;
            char? voiceCode = null;
            int? pianoSkill = null;
            if (target.ObjectById(owner) is XPerson person)
            {
                if ((person[PersonData.RenderDisplayFlags] & 0x0C) != 0 &&
                    audio.IsDemographicVoxEvent(name))
                {
                    return false;
                }
                var age = person[PersonData.PersonAge];
                var gender = person[PersonData.Gender];
                voiceCode = PersonAgeCategory.IsChild(age)
                    ? 'k'
                    : gender == 0
                        ? 'm'
                        : gender == 1
                            ? 'f'
                            : gender == 2
                                ? 'k'
                                : null;
                pianoSkill = person[PersonData.CreativitySkill] / 100;
            }

            var played = audio.PlayForSimulationObject(name, owner, pan, zoom, voiceCode, pianoSkill);
            if (played) target.PositionalSoundOwners.Add(owner);
            return played;
        };
        target.StopOwnerSounds = owner =>
        {
            if (ReferenceEquals(world, target)) audio?.RemoveOwner(owner);
            target.PositionalSoundOwners.Remove(owner);
        };
        target.AnimationSound = (ownerId, cue) =>
        {
            if (ReferenceEquals(world, target)) HandleAnimationSound(ownerId, cue);
        };
        target.PictureInPicture ??= pictureInPicture;
        target.PersistNeighborhoodStateImmediately = () =>
        {
            if (ReferenceEquals(world, target)) PersistNeighborhoodStateImmediately();
        };
        target.PersistLotSimulationStateImmediately = () =>
        {
            if (!ReferenceEquals(world, target) || lot is null) return;
            var updatedLot = target.WriteSavedSimulationStateTo(lot);
            lot = updatedLot;
            if (house > 0)
            {
                var lotPath = Path.Combine(Neighborhoods.UserData(profile), "Houses", $"House{house:D2}.iff");
                if (File.Exists(lotPath))
                    WriteReplace(lotPath, updatedLot.Bytes);
            }
        };
        target.LiveLotValuationProvider = () => target.SavedSimulationState is { } s
            ? (ReferenceEquals(world, target) ? CurrentLotValuation(s) : (s.Field0xA0, s.Field0xA4))
            : (0, 0);
        WorldBootstrap.WireNeighborhoodDelegates(target, () => ReferenceEquals(world, target) ? neighborhood : null);
        WorldBootstrap.SyncFamilyBridgeState(
            target,
            (target.FamilyBudget as FamilyBudget)?.ActiveFamily,
            (target.FamilyBudget as FamilyBudget)?.FamilyChunkId);
        target.ReleaseGenericSimsCall03PrimaryHandle = _ =>
        {
            target.DismissVisibleDialogRecords();
            if (ReferenceEquals(world, target) && !HasBlockingDialog(target) && dialogResumeSpeed > 0)
            {
                SetSimulationSpeed(dialogResumeSpeed);
                dialogResumeSpeed = 0;
            }
        };
        target.ReleaseGenericSimsCall03SecondaryHandle = _ =>
        {
            target.DismissVisibleDialogRecords();
        };
        if (neighborInventoryHandlers.TryGetValue(target, out var prevInventoryHandler))
            target.NeighborInventoryChanged -= prevInventoryHandler;
        Action<short, AvatarInventory> inventoryHandler = (_, _) =>
        {
            if (ReferenceEquals(world, target))
            {
                PersistNeighborhoodStateImmediately();
            }
            else if (ConnectedNeighborhoodEnabled &&
                     lotPool.LiveSessions.Values.FirstOrDefault(s => ReferenceEquals(s.World, target)) is { } bgSession)
            {
                bgSession.SaveDirty = true;
            }
        };
        neighborInventoryHandlers.AddOrUpdate(target, inventoryHandler);
        target.NeighborInventoryChanged += inventoryHandler;
        if (attachedNoticeWorlds.TryGetValue(target, out _))
            return;
        attachedNoticeWorlds.Add(target, new object());
        target.AddNoticeListener(new SimulationNoticeListener((message, value) =>
        {
            if (!ReferenceEquals(world, target))
            {
                if (ConnectedNeighborhoodEnabled &&
                    lotPool.LiveSessions.Values.FirstOrDefault(s => ReferenceEquals(s.World, target)) is { } bgSession)
                {
                    if (message is 0x88 or 0xda or 0xdb or 0xe4 or 0xee) bgSession.SaveDirty = true;
                    if (message == 0xe4)
                    {
                        var bgSelected = value == 0
                            ? null
                            : target.WalkOrder.OfType<XPerson>().FirstOrDefault(person => person.RequestKey == value || person.Id == value);
                        if (value == 0 || bgSelected is not null)
                        {
                            bgSession.SelectedSim = bgSelected?.Id ?? (short)0;
                            SyncSelectedSimFlags(target, bgSession.SelectedSim);
                        }
                    }
                }
                return false;
            }
            if (message is 0x88 or 0xda or 0xdb or 0xe4 or 0xee) saveDirty = true;
            if (message == 0x20000000)
            {
                if (RefreshPendingPersonPortraits())
                {
                    saveDirty = true;
                    Write(new { type = "state", state = State() });
                }
                return true;
            }
            if (message == 0xe4)
            {
                var selected = value == 0
                    ? null
                    : target.WalkOrder.OfType<XPerson>().FirstOrDefault(person => person.RequestKey == value || person.Id == value);
                if (value == 0 || selected is not null)
                {
                    selectedSim = selected?.Id ?? (short)0;
                    SyncSelectedSimFlags(target, selectedSim);
                }
            }
            if (message == 0xda)
            {
                Write(new { type = "state", state = State() });
                return true;
            }
            return false;
        }));
    }

    private static Family UpdateFamilyFields(Family family, ushort chunkId, int? field0x120 = null, uint? flags = null, int? value = null) =>
        family.WithFields(
            familyNumber: chunkId == 0 ? 0 : family.FamilyNumber != 0 ? family.FamilyNumber : chunkId,
            value: value,
            field0x120: field0x120,
            flags: flags);

    private int RecountFamilyFriends(SimulationWorld currentWorld, Family family, short familyNumber)
    {
        var neighbors = currentWorld.NeighborWalkOrder.Count > 0
            ? currentWorld.NeighborWalkOrder.OfType<NeighborState>().ToArray()
            : currentWorld.Neighbors.Values.ToArray();
        var threshold = currentWorld.FriendshipThreshold > 0 ? currentWorld.FriendshipThreshold : (short)50;
        var memberNeighbors = neighbors
            .Where(n => family.Members.Contains(unchecked((uint)n.TypeRecordKey)) ||
                        (familyNumber != 0 && n.PersonData[(int)PersonData.FamilyNumber] == familyNumber))
            .ToArray();
        if (memberNeighbors.Length == 0) return 0;
        var count = 0;
        foreach (var candidate in neighbors)
        {
            if (family.Members.Contains(unchecked((uint)candidate.TypeRecordKey)) ||
                (familyNumber != 0 && candidate.PersonData[(int)PersonData.FamilyNumber] == familyNumber) ||
                candidate.PersonData[(int)PersonData.FamilyNumber] == 0 ||
                candidate.PersonData[(int)PersonData.Gender] >= 2)
                continue;

            foreach (var memberNeighbor in memberNeighbors)
            {
                if (candidate.Relationships.Count(memberNeighbor.Id) <= 0 ||
                    memberNeighbor.Relationships.Count(candidate.Id) <= 0)
                    continue;
                if (candidate.Relationships.Get(memberNeighbor.Id, 0) < threshold ||
                    memberNeighbor.Relationships.Get(candidate.Id, 0) < threshold)
                    continue;
                count++;
                break;
            }
        }
        return count;
    }

    /// <summary>Persists neighborhood state so that neighbor records and family projections are immediately
    /// visible across subsequent lot loads.</summary>
    private void PersistNeighborhoodStateImmediately() =>
        PersistNeighborhoodStateImmediatelyCore(commitVacationField: false);

    private void PersistNeighborhoodStateImmediatelyCore(bool commitVacationField)
    {
        plugins?.CommitNeighborhood();
        if (world is null || neighborhood is null || neighborhoodPath is null) return;

        RefreshPendingPersonPortraits();
        world.SaveAllPersistentPersonData();
        var updated = ConnectedNeighborhoodEnabled
            ? MergeWorldNeighborRecordsIntoNeighborhood(world, neighborhood, CollectGloballyRemovedNeighbors())
            : world.WriteSavedNeighborRelationshipsTo(neighborhood);
        var famiType = ResourceType.Parse("FAMI");

        if (world.DirtyRelationshipFamilies.Count > 0)
        {
            var dirtyIds = world.DirtyRelationshipFamilies.ToArray();
            world.DirtyRelationshipFamilies.Clear();
            foreach (var dirtyFamilyId in dirtyIds)
            {
                if (dirtyFamilyId == 0) continue;
                ushort? targetChunkId = null;
                Family? targetFamily = null;
                if (activeFamilyChunkId is { } activeId &&
                    world.FamilyBudget is FamilyBudget activeBudget &&
                    activeBudget.ActiveFamily is { } activeFam &&
                    (activeId == unchecked((ushort)dirtyFamilyId) || activeFam.FamilyNumber == dirtyFamilyId))
                {
                    targetChunkId = activeId;
                    targetFamily = activeFam;
                }
                else
                {
                    foreach (var chunk in updated.OfType(famiType))
                    {
                        if (chunk.Id == 0) continue;
                        var candidateFamily = Family.Read(updated, chunk);
                        if (chunk.Id == unchecked((ushort)dirtyFamilyId) || candidateFamily.FamilyNumber == dirtyFamilyId)
                        {
                            targetChunkId = chunk.Id;
                            targetFamily = candidateFamily;
                            break;
                        }
                    }
                }
                if (targetChunkId is { } foundChunkId && targetFamily is not null)
                {
                    var friendCount = RecountFamilyFriends(world, targetFamily, dirtyFamilyId);
                    var updatedFamily = UpdateFamilyFields(targetFamily, foundChunkId, field0x120: friendCount);
                    if (activeFamilyChunkId == foundChunkId && world.FamilyBudget is FamilyBudget budget)
                        budget.ActiveFamily = updatedFamily;
                    updated = updated.WithAddedOrReplacedChunk(famiType, foundChunkId, updatedFamily.Write(foundChunkId));
                }
            }
        }

        if (activeFamilyChunkId is { } familyChunkId &&
            world.FamilyBudget is FamilyBudget familyBudget && familyBudget.ActiveFamily is { } family)
        {
            // Family architecture value in FAMI combines lot and furnishings values with architecture value
            // scaled by the tuning multiplier (0.7), contributing to overall family net worth.
            if (suspendedHomeLot is null && !IsAwayLot(Path.GetDirectoryName(neighborhoodPath) ?? "", house) &&
                world.SavedSimulationState is { } valueState)
            {
                var (furnishingsValue, architectureValue) = CurrentLotValuation(valueState);
                family = family.WithValue(valueState.Field0x9C + furnishingsValue + (int)(architectureValue * (world.ArchitectureValueMultiplier ?? 0.7f)));
                familyBudget.ActiveFamily = family;
            }
            var diskFamily = family;
            if (!commitVacationField &&
                committedVacationState is { } committed &&
                committed.FamilyChunkId == familyChunkId &&
                diskFamily.Field0x13C != committed.VacationHouse)
            {
                diskFamily = diskFamily.WithFields(field0x13C: committed.VacationHouse);
            }
            updated = updated.WithAddedOrReplacedChunk(famiType, familyChunkId,
                diskFamily.Write(familyChunkId));
        }
        if (world.NeighborhoodFamilies is NeighborhoodFamilies families)
            foreach (var (chunkId, changedFamily) in families.Changed)
                updated = updated.WithAddedOrReplacedChunk(famiType, chunkId, changedFamily.Write(chunkId));

        if (world[SimulationGlobal.IsTutorialHouseNoYes] != 0)
            world.NeighborhoodData[0] = house;
        var ngbhType = ResourceType.Parse("NGBH");
        var existingNgbhChunk = updated.Find(ngbhType, 1);
        var existingNgbh = existingNgbhChunk is not null ? NeighborhoodData.Read(updated, existingNgbhChunk) : null;
        var inventories = new Dictionary<short, IReadOnlyList<NeighborhoodData.InventoryItem>>();
        var neighborIds = new SortedSet<short>();
        if (existingNgbh is not null)
        {
            foreach (var id in existingNgbh.Inventories.Keys)
                if (world.Neighbors.Count == 0 || world.Neighbors.ContainsKey(id))
                    neighborIds.Add(id);
        }
        foreach (var id in world.Neighbors.Keys)
            neighborIds.Add(id);
        foreach (var id in neighborIds)
        {
            var entries = world.NeighborInventory(id).Entries;
            if (entries.Count > 0 || (existingNgbh is not null && existingNgbh.Inventories.ContainsKey(id)))
            {
                inventories[id] = entries
                    .Select(entry => new NeighborhoodData.InventoryItem(entry.Key1, unchecked((uint)entry.Key2), unchecked((ushort)entry.Count)))
                    .ToArray();
            }
        }
        var ngbhSwap = existingNgbhChunk is not null && existingNgbhChunk.Data.Span.Length > 0 && existingNgbhChunk.Data.Span[0] != 0;
        var ngbhBytes = existingNgbh is not null && (existingNgbh.Version >= 0x40 || inventories.Count == 0)
            ? existingNgbh.WithData(world.NeighborhoodData).WithInventories(inventories).Write(ngbhSwap)
            : NeighborhoodData.Write(world.NeighborhoodData, inventories, 0x49, ngbhSwap);
        updated = updated.WithAddedOrReplacedChunk(ngbhType, 1, ngbhBytes);
        if (updated.Find(ngbhType, 1) is { } refreshedNgbhChunk)
            world.LoadNeighborhoodData(NeighborhoodData.Read(updated, refreshedNgbhChunk));

        var tattType = ResourceType.Parse("TATT");
        var existingTattChunk = updated.Find(tattType, 1);
        var existingTatt = existingTattChunk is not null ? TypeAttributes.Read(updated, existingTattChunk) : null;
        var tattMap = existingTatt is not null
            ? new Dictionary<uint, short[]>(existingTatt.AttributesByGuid)
            : new Dictionary<uint, short[]>();
        foreach (var obj in world.Objects.Values)
        {
            var guid = unchecked((uint)(obj.TypeRecord?.Key ?? 0));
            if (guid != 0 && obj.TypeAttributes is { Length: > 0 })
                tattMap[guid] = obj.TypeAttributes.ToArray();
        }
        if (existingTattChunk is not null || tattMap.Count > 0)
        {
            var tattVersion = existingTatt?.Version ?? 0x49;
            var tattSwap = existingTattChunk is not null && existingTattChunk.Data.Span.Length > 0 && existingTattChunk.Data.Span[0] != 0;
            var tattCoded = existingTattChunk is null || existingTattChunk.Data.Span.Length <= 12 || existingTattChunk.Data.Span[12] != 0;
            updated = updated.WithAddedOrReplacedChunk(tattType, 1, TypeAttributes.Write(tattMap, tattVersion, tattCoded, tattSwap));
        }

        var userData = Path.GetDirectoryName(neighborhoodPath);
        if (!string.IsNullOrEmpty(userData))
        {
            Directory.CreateDirectory(userData);
            if (registry is not null && registry.DirtyPeopleGuids.Count > 0)
            {
                var charactersDir = Path.Combine(userData, "Characters");
                Directory.CreateDirectory(charactersDir);
                foreach (var dirtyGuid in registry.DirtyPeopleGuids.ToArray())
                {
                    if (registry.Find(dirtyGuid) is { Scan: ObjectScan.People } dirtyEntry)
                    {
                        var fileName = Path.GetFileName(dirtyEntry.Source);
                        if (!string.IsNullOrEmpty(fileName))
                            WriteReplace(Path.Combine(charactersDir, fileName), dirtyEntry.File.Bytes);
                    }
                }
                registry.DirtyPeopleGuids.Clear();
            }
        }

        WriteReplace(neighborhoodPath, updated.Bytes);
        neighborhood = updated;
        if (ConnectedNeighborhoodEnabled)
            SyncAllLiveWorldsFromNeighborhood(globallyRemovedNeighbors);
    }

    /// <summary>Redirects lot entry to a family's vacation lot when their active vacation destination is
    /// nonzero and exists on disk.</summary>
    private static (short House, ushort? ActiveFamilyChunkId) ResolveVacationRedirect(string userData, short requestedHouse)
    {
        var nghPath = Path.Combine(userData, "Neighborhood.iff");
        if (!File.Exists(nghPath)) return (requestedHouse, null);
        try
        {
            var ngh = IffFile.Open(nghPath);
            foreach (var chunk in ngh.OfType(ResourceType.Parse("FAMI")))
            {
                if (chunk.Id == 0) continue;
                var family = Family.Read(ngh, chunk);
                if ((requestedHouse is (> 0 and < 20) or (>= 90 and <= 92)) &&
                    !Neighborhoods.IsCommunityLot(userData, requestedHouse) &&
                    family.HouseNumber == requestedHouse &&
                    family.Field0x13C > 0)
                {
                    var vacHouse = checked((short)family.Field0x13C);
                    var vacPath = Path.Combine(userData, "Houses", $"House{vacHouse:D2}.iff");
                    if (File.Exists(vacPath)) return (vacHouse, chunk.Id);
                }
                else if (requestedHouse is >= 40 and <= 49 && family.Field0x13C == requestedHouse)
                {
                    return (requestedHouse, chunk.Id);
                }
            }
        }
        catch
        {
        }
        return (requestedHouse, null);
    }

    private static void HydrateTypeAttributesFromNeighborhood(SimulationWorld targetWorld, IffFile? lotFile, IffFile? neighborhoodFile,
        bool overwriteExisting = false)
    {
        if (neighborhoodFile is null || (!overwriteExisting && lotFile?.Find(ResourceType.Parse("TATT"), 1) is not null)) return;
        if (neighborhoodFile.Find(ResourceType.Parse("TATT"), 1) is not { } tattChunk) return;
        var attributesByGuid = TypeAttributes.Read(neighborhoodFile, tattChunk).AttributesByGuid;
        foreach (var obj in targetWorld.Objects.Values)
        {
            var guid = unchecked((uint)(obj.TypeRecord?.Key ?? 0));
            if (guid == 0 || !attributesByGuid.TryGetValue(guid, out var savedAttrs) || savedAttrs.Length == 0) continue;
            if (obj.TypeAttributes.Length < savedAttrs.Length)
                obj.TypeAttributes = new short[savedAttrs.Length];
            Array.Copy(savedAttrs, obj.TypeAttributes, savedAttrs.Length);
        }
    }

    public static SimulationServer Load(string game, string session, string profile, short house, TextWriter output, byte language = 1, string? installRoot = null)
    {
        try
        {
            if (house == 0)
            {
                var emptyServer = new SimulationServer(output, session, profile, game, 0, null, null, null, null, loadError: null, language, installRoot: installRoot);
                emptyServer.InitializeConnectedNeighborhoodFromPacks();
                return emptyServer;
            }
            var userData = Neighborhoods.UserData(profile);
            var (effectiveHouse, redirectedFamilyChunkId) = ResolveVacationRedirect(userData, house);
            house = effectiveHouse;
            var path = Path.Combine(userData, "Houses", $"House{house:D2}.iff");
            if (!File.Exists(path)) return new SimulationServer(output, session, profile, game, house, null, null, null, null, $"Lot {house} not found.", language, installRoot: installRoot);
            var runtime = ReadSavedRuntimeMetadata(CanonicalRuntimePath(path), File.ReadAllBytes(path));
            var setup = WorldBootstrap.Load(game, path, language, () => false, house,
                userDataPath: userData, activeFamilyChunkId: redirectedFamilyChunkId);
            HydrateTypeAttributesFromNeighborhood(setup.World, setup.Lot, setup.Neighborhood);
            if (setup.ActiveFamilyChunkId is null && !setup.World.WalkOrder.OfType<XPerson>().Any(p => p[PersonData.FamilyNumber] > 0))
            {
                setup.World[SimulationGlobal.SimlessBuildMode] = 1;
            }
            else if (setup.ActiveFamilyChunkId is not null)
            {
                setup.World[SimulationGlobal.SimlessBuildMode] = 0;
            }
            RestorePersonAnimationRuntime(setup.World, runtime.PersonAnimations);
            RestoreDialogRecords(setup.World, runtime.Dialogs);
            if (runtime.ClockTicks is { } clockTicks)
                setup.World.Clock.RestoreProgress(clockTicks, runtime.ClockDay ?? 0);
            var server = new SimulationServer(output, session, profile, game, house, setup.World, setup.Lot,
                setup.Registry, setup.Global, null, language, setup.NeighborhoodPath, setup.Neighborhood, setup.ActiveFamilyChunkId,
                installRoot: installRoot);
            server.RestoreTravelRuntimeState(setup.World, userData, house, setup.ActiveFamilyChunkId, setup.ActiveFamily, runtime);
            server.ClampAwayLotEditingState();
            if (setup.ActiveFamily is { Members.Count: > 0 } activeFamily)
            {
                var expectedKeys = (setup.World.FamilyBudget as FamilyBudget)?.RestrictedActivePersonKeys
                    ?? activeFamily.Members.Select(static m => unchecked((int)m)).ToArray();
                if (!setup.World.WalkOrder.OfType<XPerson>().Any(p => expectedKeys.Contains(p.TypeRecord?.Key ?? 0)))
                {
                    server.EnsureSelectedHouseholdMember(activeFamily, server.LotWidth / 2, server.LotHeight / 2);
                }
            }
            if (setup.World.ObjectById(server.selectedSim) is not XPerson)
            {
                server.selectedSim = server.DefaultSelectedSimId();
                SyncSelectedSimFlags(setup.World, server.selectedSim);
            }
            server.SetSimulationSpeed(0);
            server.lastRunningSpeed = runtime.Speed;
            if (HasBlockingDialog(setup.World) && runtime.Speed > 0)
                server.dialogResumeSpeed = runtime.Speed;
            server.InitializeFameCallbackTarget(setup.World);
            if (setup.Global is { } global) setup.World.AutonomyHost = new AutonomyHost(setup.World, global, house, () => server.speed == 0, server.ScoreAutonomyChoice);
            server.InitializeViewLevelFromSelectedSim();
            server.InitializeConnectedNeighborhoodFromPacks();
            return server;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ResourceFormatException)
        {
            return new SimulationServer(output, session, profile, game, house, null, null, null, null, error.Message, language, installRoot: installRoot);
        }
    }

    private float ScoreAutonomyChoice(XPerson person, XObject target, short actionIndex, string actionName, float score) =>
        plugins is null
            ? score
            : plugins.ScoreAutonomy(new SimsNative.Plugins.PluginAutonomyChoice(person.Id, IsHouseholdPerson(person), target.Id,
                unchecked((uint)(target.TypeRecord?.Key ?? 0)), actionIndex, actionName), score);

    public async Task Run(System.Threading.Channels.ChannelReader<string>? externalInput = null)
    {
        Write(new
        {
            type = "hello",
            protocolVersion = 1,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            engine = "native-simsengine-v0",
            packFingerprint,
            packWarnings = Array.Empty<object>(),
            languageCode = (byte)1,
            saveSchemaVersion = 2,
            plugins = plugins?.Statuses ?? [],
            pluginPanels = plugins?.Panels.Select(panel => new { panel.PackId, panel.Id, panel.Path }).ToArray() ?? [],
            nativeEngine = true,
            house,
            homeHouse = EffectiveHomeHouse,
            away = IsVisitingAwayLot,
            simless = world is not null && world[SimulationGlobal.SimlessBuildMode] != 0,
            loadError,
            commands = new[] { "state", "audio", "audioEvent", "audioStopOwner", "audioEnded", "audioVolume", "audioState", "modeMusic", "speed", "blockSimulator", "tutorialInfo", "uiButton", "relationshipPopup", "tutorialPanelCheck", "step", "scene", "stream", "sceneAck", "selectSim", "setMotive", "menu", "groundMenu", "groundInteract", "interact", "catalog", "catalogPage", "buildCatalogPage", "previewObject", "buildCursor", "pieHeadPreview", "buyObject", "sellObject", "moveObject", "architecture", "architectureBatch", "roof", "undo", "redo", "cancelAction", "dialogResponse", "genericSimsCallPersonPicker", "appearanceCatalog", "appearancePreview", "createHousehold", "moveInFamily", "neighborhood", "saveBackups", "restoreBackup", "outfitChoices", "outfitPreview", "freeWill", "cheat", "pictureInPicture", "walls", "cutaway", "roofs", "cameraOrientation", "level", "view", "viewScrolling", "cursorObjectCreated", "tutorialResetState", "save", "load", "travelFilter", "plugins", "pluginPanels", "pluginAction", "warmup", "openLot", "closeLot", "quit" }
                .Concat(ConnectedNeighborhoodCommandNames)
                .Concat(testFixtureEnabled ? ["setTestPersonTile", "testTileContents"] : []).ToArray(),
        });
        if (world is not null)
        {
            var initialExport = Stopwatch.GetTimestamp();
            Export();
            Write(new { type = "state", state = State() });
            if (profile_) Console.Error.WriteLine($"Native load: first scene export {Stopwatch.GetElapsedTime(initialExport).TotalMilliseconds:F0} ms");
        }
        System.Threading.Channels.ChannelReader<string> reader;
        Task? read = null;
        if (externalInput is not null)
        {
            reader = externalInput;
        }
        else
        {
            var channel = System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(128)
            { SingleReader = true, SingleWriter = true });
            read = Task.Run(async () =>
            {
                try
                {
                    while (await Console.In.ReadLineAsync() is { } line) await channel.Writer.WriteAsync(line);
                }
                finally { channel.Writer.TryComplete(); }
            });
            reader = channel.Reader;
        }
        var watch = Stopwatch.StartNew();
        var nextUpdate = watch.Elapsed.TotalSeconds;
        var nextAudioTick = nextUpdate;
        var scheduledSpeed = speed;
        var nextScene = nextUpdate + 1;
        string DialogTokens()
        {
            if (world is not { DialogRecords.Count: > 0 } current) return "";
            var hasVisible = false;
            for (var i = 0; i < current.DialogRecords.Count; i++)
            {
                if (current.DialogRecords[i].Visible) { hasVisible = true; break; }
            }
            if (!hasVisible) return "";
            return string.Join(",", current.DialogRecords.Where(r => r.Visible).OrderByDescending(r => r.Blocking).Select(r => r.Token));
        }
        var lastDialogToken = DialogTokens();
        var hadActivePluginEnvironment = false;
        object? lastPluginEnvironment = null;
        string? pending = null;
        while (running)
        {
            while (pending is not null || reader.TryRead(out pending))
            {
                if (!Handle(pending)) break;
                pending = null;
            }
            if (reader.Completion.IsCompleted && pending is null) break;
            var now = watch.Elapsed.TotalSeconds;
            if (speed != scheduledSpeed)
            {
                scheduledSpeed = speed;
                nextUpdate = now;
            }
            if (now >= nextUpdate)
            {
                if (!simulatorBlocked && speed > 0 && world is not null)
                {
                    var tickInterval = TickIntervalSeconds(speed);
                    var maxBatchTicks = speed >= 10 ? 48 : speed >= 3 ? 6 : 2;
                    var batchTicks = 0;
                    var batchStartHouse = house;
                    var batchDeadline = now + (streaming ? (speed >= 10 ? 0.011 : 0.005) : 0.014);
                    while (now >= nextUpdate && batchTicks < maxBatchTicks && !simulatorBlocked && speed > 0 && pending is null && !reader.TryPeek(out _))
                    {
                        var started = Stopwatch.GetTimestamp();
                        if (AdvanceOneTick()) { updates++; plugins?.Tick(updates); }
                        if (profile_)
                        {
                            var elapsed = Stopwatch.GetElapsedTime(started);
                            tickTime += elapsed;
                            var ms = elapsed.TotalMilliseconds;
                            if (ms > maxTickMs) maxTickMs = ms;
                            ticksTimed++;
                        }
                        batchTicks++;
                        nextUpdate += tickInterval;
                        now = watch.Elapsed.TotalSeconds;
                        if (house != batchStartHouse || HasBlockingDialog(world) || now >= batchDeadline) break;
                    }
                    if (now - nextUpdate > 0.25) nextUpdate = now;
                }
                else
                {
                    if (!simulatorBlocked && speed == 0 && world is not null && (updates > 0 || world[SimulationGlobal.IsTutorialHouseNoYes] != 0)) AdvancePausedTick();
                    nextUpdate += TickIntervalSeconds(speed);
                    if (now - nextUpdate > 0.25) nextUpdate = now;
                }
                var currentDialogToken = DialogTokens();
                if (currentDialogToken != lastDialogToken)
                {
                    lastDialogToken = currentDialogToken;
                    if (streaming) Write(new { type = "state", state = State(includeArchitectureGrids: false) });
                }
            }
            if (now >= nextAudioTick)
            {
                if (audio is { } activeAudio && world is { } activeWorld) activeAudio.Tick(activeWorld);
                else audio?.Tick();
                nextAudioTick += 1.0 / 60;
                if (now - nextAudioTick > 0.25) nextAudioTick = now;
            }
            if (!simulatorBlocked && streaming && world is not null && lot is not null && registry is not null)
            {
                var currentCutawaySimKey = CutawaySimKey();
                if (currentCutawaySimKey != exportedCutawaySimKey)
                {
                    ResolveFocusedHousePrivacy(out var maskWallMode, out _, out _);
                    if (!SceneExporter.CutawayMaskMatches(world, animationState, revision, viewLevel, maskWallMode, cameraOrientation,
                            cutawayTile, cutawayRooms, CutawaySimTile(), cutawayZoom))
                        Export();
                    else
                        exportedCutawaySimKey = currentCutawaySimKey;
                }
            }
            if (!simulatorBlocked && streaming && speed > 0 && world is not null && lot is not null && registry is not null && now >= nextAnimation)
            {
                if (animationState.Kept is null || animationState.Revision != revision)
                    Export();
                var started = Stopwatch.GetTimestamp();
                var currentTickInterval = TickIntervalSeconds(speed);
                var subTickAlpha = currentTickInterval > 0 && nextUpdate > now
                    ? (float)Math.Clamp(1.0 - (nextUpdate - now) / currentTickInterval, 0.0, 0.999)
                    : 0f;
                ResolveFocusedHousePrivacy(out var effWallMode, out var effRoofsVisible, out var hideInteriorPeople);
                if (DirectAnimationSink is { } directSink)
                {
                    if (SceneExporter.AnimationDirect(lot, world, registry, game, SceneDirectory, house, revision, animationState, ++animationSequence, updates,
                            viewLevel, effWallMode, cameraOrientation, cutawayTile, effRoofsVisible, cutawayRooms: cutawayRooms, cutawaySim: CutawaySimTile(), cutawayZoom: cutawayZoom, showLoweredPortals: doorWindowBuildToolActive, subTickAlpha: subTickAlpha, hideInteriorPeople: hideInteriorPeople) is { } directFrame)
                        directSink(directFrame);
                    EmitAdjacentDirectAnimations(subTickAlpha);
                }
                else if (SceneExporter.Animation(lot, world, registry, game, SceneDirectory, house, revision, animationState, ++animationSequence, updates,
                        viewLevel, effWallMode, cameraOrientation, cutawayTile, effRoofsVisible, cutawayRooms: cutawayRooms, cutawaySim: CutawaySimTile(), cutawayZoom: cutawayZoom, showLoweredPortals: doorWindowBuildToolActive, subTickAlpha: subTickAlpha, hideInteriorPeople: hideInteriorPeople) is { } frame)
                {
                    Write(new { type = "animation", frame });
                }
                if (plugins is not null)
                {
                    var currentEnv = plugins.Environment(house);
                    if ((currentEnv is not null || hadActivePluginEnvironment) && !Equals(currentEnv, lastPluginEnvironment))
                    {
                        hadActivePluginEnvironment = currentEnv is not null;
                        lastPluginEnvironment = currentEnv;
                        Write(new { type = "environment", updates, environment = currentEnv });
                    }
                }
                if (profile_)
                {
                    if (profileWindowStart == 0) profileWindowStart = started;
                    animationTime += Stopwatch.GetElapsedTime(started);
                    if (++animationsTimed == 120)
                    {
                        var windowSec = Math.Max(Stopwatch.GetElapsedTime(profileWindowStart).TotalSeconds, 1e-6);
                        var avgTick = ticksTimed > 0 ? tickTime.TotalMilliseconds / ticksTimed : 0.0;
                        var ticksPerSec = ticksTimed / windowSec;
                        Console.Error.WriteLine(
                            $"Native profile (speed {speed}): animation {animationTime.TotalMilliseconds / animationsTimed:F2} ms/frame, ticks {ticksTimed} ({ticksPerSec:F0}/s, avg {avgTick:F2} ms, max {maxTickMs:F1} ms)");
                        animationTime = TimeSpan.Zero;
                        animationsTimed = 0;
                        tickTime = TimeSpan.Zero;
                        ticksTimed = 0;
                        maxTickMs = 0;
                        profileWindowStart = Stopwatch.GetTimestamp();
                    }
                }
                nextAnimation = now + (speed >= 10 ? 1.0 / 60 : 1.0 / 120);
            }
            if (now >= nextScene && streaming)
            {
                if (speed > 0 && !simulatorBlocked && world is not null)
                {
                    var currentObjectsSig = ComputeObjectsSignature();
                    var includeObjects = editingView || currentObjectsSig != lastStreamedObjectsSignature;
                    Write(new { type = "state", state = State(includeArchitectureGrids: false, includeObjects: includeObjects) });
                }
                nextScene = now + 0.25;
            }
            else if (now >= nextScene)
            {
                if (speed > 0 && !simulatorBlocked && world is not null)
                {
                    var started = Stopwatch.GetTimestamp();
                    Export();
                    if (profile_ && ticksTimed > 0)
                        Console.Error.WriteLine($"Native profile: {ticksTimed} ticks, {tickTime.TotalMilliseconds / ticksTimed:F2} ms/tick, scene export {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms");
                    tickTime = TimeSpan.Zero;
                    ticksTimed = 0;
                }
                nextScene = now + 1;
            }
            var afterWork = watch.Elapsed.TotalSeconds;
            var nextDeadline = Math.Min(nextUpdate, nextAudioTick);
            if (streaming && speed > 0 && !simulatorBlocked && nextAnimation < nextDeadline)
                nextDeadline = nextAnimation;
            var sleepSeconds = nextDeadline - afterWork;
            if (sleepSeconds > 0.0015 && !reader.TryPeek(out _))
            {
                if (sleepSeconds < 0.004)
                {
                    Thread.Sleep(1);
                }
                else
                {
                    var sleepMs = Math.Clamp((int)(sleepSeconds * 1000.0), 1, 15);
                    var readTask = reader.WaitToReadAsync().AsTask();
                    if (!readTask.IsCompleted)
                    {
                        using var delayCts = new CancellationTokenSource();
                        var delayTask = Task.Delay(sleepMs, delayCts.Token);
                        var completed = await Task.WhenAny(readTask, delayTask);
                        if (completed == readTask) delayCts.Cancel();
                    }
                }
            }
            else
            {
                await Task.Yield();
            }
        }
        if (read is not null && reader.Completion.IsCompleted) await read;
        plugins?.Dispose();
        audio?.Dispose();
    }

    public int BatchRun(int requestedTicks)
    {
        if (world is null || requestedTicks <= 0) return 0;
        int completed = 0;
        for (int i = 0; i < requestedTicks; i++)
        {
            if (AdvanceOneTick()) completed++;
        }
        return completed;
    }

    internal bool Handle(string line)
    {
        var id = "";
        var op = "";
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String)
                id = value.GetString() ?? "";
            if (root.TryGetProperty("op", out var opValue) && opValue.ValueKind == JsonValueKind.String)
                op = opValue.GetString() ?? "";
            try { return HandleCore(root, id, op); }
            catch (SaveCompatibility.MissingPacksException error)
            {
                Write(new { type = "response", id, operation = op, ok = false, error = error.Message, missingPacks = error.Packs });
                return true;
            }
            catch (Exception error)
            {
                Write(new { type = "response", id, operation = op, ok = false, error = error.Message });
                return true;
            }
        }
        catch (JsonException)
        {
            Write(new { type = "response", id = "", operation = "", ok = false, error = "invalid json" });
            return true;
        }
    }

    private bool HandleCore(JsonElement request, string id, string op)
    {
        object? result = null;
        string? error = null;
        switch (op)
        {
            case "state": result = State(); break;
            case "cursorObjectCreated":
            {
                var markDirty = !request.TryGetProperty("dirty", out var dirtyValue) || dirtyValue.ValueKind != JsonValueKind.False;
                if (markDirty) saveDirty = true;
                if (world is { } cursorWorld &&
                    request.TryGetProperty("objectID", out var cursorIdValue) &&
                    cursorIdValue.TryGetInt16(out var cursorObjectId) &&
                    cursorWorld.ObjectById(cursorObjectId) is { } cursorObject)
                {
                    result = new
                    {
                        dirty = saveDirty,
                        objectID = (int)cursorObjectId,
                        preview = ObjectDisposePreview(cursorWorld, cursorObject),
                    };
                }
                else
                {
                    result = new { dirty = saveDirty };
                }
                break;
            }
            case "tutorialResetState": result = new { requiresSave = saveDirty && house != 0 }; break;
            case "blockSimulator":
                simulatorBlocked = request.TryGetProperty("blocked", out var blockedValue) && blockedValue.ValueKind == JsonValueKind.True;
                result = new { blocked = simulatorBlocked };
                break;
            case "tutorialInfo":
            {
                var owner = world?.TutorialOwner;
                if (owner?.TypeRecord?.BehaviorScope is BehaviorScope scope)
                {
                    var tree = scope.FindNamedTree("show situation info");
                    if (tree != 0) TreeRunner.RunWithParameters(world!, owner, scope, tree, 0, []);
                }
                result = State();
                break;
            }
            case "uiButton":
                lastButtonImage = request.TryGetProperty("image", out var imageValue) && imageValue.TryGetInt32(out var image) ? image : null;
                lastButtonPerson = request.TryGetProperty("objectID", out var buttonSimValue) && buttonSimValue.TryGetInt16(out var buttonSim) ? buttonSim : (short)0;
                if (world is not null) world[SimulationGlobal.LastGZButtonID] = (short)(lastButtonImage ?? 0);
                result = new { recorded = true };
                break;
            case "relationshipPopup":
            {
                var current = world ?? throw new InvalidOperationException("No lot is loaded");
                var neighborId = request.GetProperty("neighborID").GetInt16();
                if (!current.Neighbors.TryGetValue(neighborId, out var neighbor)) throw new ArgumentException("Unknown neighbor");
                var selected = current.WalkOrder.OfType<XPerson>().FirstOrDefault(person => person.Id == selectedSim);
                if (selected is null || !current.Neighbors.TryGetValue(selected[PersonData.NeighborId], out var owner) ||
                    !owner.Relationships.Snapshot().ContainsKey(neighborId)) throw new ArgumentException("Neighbor is not in the selected Sim's relationships");
                var selector = registry?.Find(unchecked((uint)neighbor.TypeRecordKey));
                var liveTarget = current.WalkOrder.OfType<XPerson>().FirstOrDefault(person => person[PersonData.NeighborId] == neighbor.Id);
                var name = !string.IsNullOrWhiteSpace(liveTarget?.Name) ? liveTarget.Name
                    : !IsInternalUserLabel(neighbor.Name) ? neighbor.Name!
                    : selector?.CatalogString(0, language) ?? neighbor.Name ?? "";
                var zodiac = current.InitializeRelationshipZodiac(neighbor, !string.IsNullOrEmpty(name),
                    data => (short)(Neighborhoods.ComputeExportZodiac(data) ?? 0));
                result = new { zodiac = (int)zodiac };
                break;
            }
            case "mode":
                // Simulator global 19 stores the viewer mode: 0 for Live, 1 for Build, 2 for Buy, 3 for
                // Options, and 4 for Neighborhood.
                viewerMode = request.GetProperty("mode").GetString() switch
                {
                    "live" => 0, "build" => 1, "buy" => 2, "options" or "camera" => 3,
                    _ => throw new ArgumentException("mode must be live, build, buy, options or camera"),
                };
                if (IsVisitingAwayLot && viewerMode is 1 or 2)
                    viewerMode = 0;
                if (world is not null) world[SimulationGlobal.Mode] = viewerMode;
                if (viewerMode != 2) RemoveTileCursor();
                result = new { mode = viewerMode };
                break;
            case "zoom":
                viewerScale = request.TryGetProperty("scale", out var scaleValue) && scaleValue.TryGetInt16(out var scale) && scale is >= 1 and <= 3
                    ? scale : throw new ArgumentException("scale must be 1, 2 or 3");
                if (world is not null) world[SimulationGlobal.ZoomLevelSML] = viewerScale;
                result = new { scale = viewerScale };
                break;
            case "viewScrolling":
                var viewScrolling = request.TryGetProperty("value", out var scrollingValue) &&
                    scrollingValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? scrollingValue.GetBoolean()
                    : throw new ArgumentException("view scrolling state (value) must be a boolean");
                result = new { scrolling = viewScrolling };
                break;
            case "tutorialPanelCheck":
            {
                var live = request.TryGetProperty("live", out var liveValue) && liveValue.ValueKind == JsonValueKind.True;
                var popup = request.TryGetProperty("popup", out var popupValue) && popupValue.ValueKind == JsonValueKind.String
                    ? popupValue.GetString() : null;
                var scrolls = request.TryGetProperty("scrolls", out var scrollsValue) && scrollsValue.TryGetInt32(out var scrollCount) ? scrollCount : 0;
                var rotation = request.TryGetProperty("rotation", out var rotationValue) && rotationValue.TryGetInt32(out var rotationCount)
                    ? rotationCount : cameraOrientation;
                var setButtons = request.TryGetProperty("setButtons", out var setValue) && setValue.ValueKind == JsonValueKind.Array
                    ? setValue.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Number).Select(item => item.GetInt32()).ToArray() : [];
                var delivered = world is not null && tutorialEvents.Check(world, new TutorialUiState
                {
                    Live = live, Popup = popup, LastButtonImage = lastButtonImage, LastButtonPerson = lastButtonPerson,
                    Rotation = rotation, Scrolls = scrolls, SetButtons = setButtons,
                });
                result = delivered ? State() : new { delivered = false };
                break;
            }
            case "speed":
                var requestedSpeed = request.TryGetProperty("value", out var speedValue) && speedValue.ValueKind == JsonValueKind.Number && speedValue.TryGetInt32(out var requested) ? requested : 0;
                if (!testFixtureEnabled && (world is null || world[SimulationGlobal.SimlessBuildMode] != 0) && requestedSpeed > 0)
                    requestedSpeed = 0;
                // Manually changing speed while auto-speed is active updates the restored speed setting,
                // while pausing cancels auto-speed.
                if (autoSpeedSaved > -1)
                    autoSpeedSaved = requestedSpeed > 0 ? requestedSpeed : -1;
                if (HasBlockingDialog(world))
                {
                    dialogResumeSpeed = Math.Clamp(requestedSpeed, 0, 1000);
                    if (dialogResumeSpeed > 0) lastRunningSpeed = dialogResumeSpeed;
                    SetSimulationSpeed(0, blockingDialog: true);
                }
                else
                {
                    SetSimulationSpeed(requestedSpeed);
                    dialogResumeSpeed = 0;
                    if (speed > 0) lastRunningSpeed = speed;
                }
                result = State();
                break;
            case "step":
            {
                var count = request.TryGetProperty("count", out var cVal) && cVal.TryGetInt32(out var parsedCount) ? parsedCount : 1;
                if (count is < 1 or > 10000) throw new ArgumentException("Step count must be 1..10000");
                var previous = speed;
                try
                {
                    if (previous == 0) SetSimulationSpeed(1);
                    for (var i = 0; i < count; i++)
                    {
                        if (world is not null && AdvanceOneTick(allowAutoSpeed: previous > 0))
                        {
                            updates++;
                            plugins?.Tick(updates);
                            if (audio is { } activeAudio && world is { } activeWorld) activeAudio.Tick(activeWorld);
                            else audio?.Tick();
                        }
                        if (HasBlockingDialog(world)) break;
                    }
                }
                finally
                {
                    if (previous == 0)
                    {
                        SetSimulationSpeed(0);
                        dialogResumeSpeed = 0;
                    }
                }
                Export();
                result = State();
                break;
            }
            case "stream":
                streaming = (request.TryGetProperty("enabled", out var streamValue) || request.TryGetProperty("value", out streamValue)) && streamValue.ValueKind == JsonValueKind.True;
                nextAnimation = 0;
                result = new { enabled = streaming, framesPerSecond = 120 };
                if (streaming && world is not null)
                    Write(new { type = "state", state = State() });
                break;
            case "scene":
                var exportedPath = Export();
                result = IsExplicitNativeEnv ? new { revision } : exportedPath;
                break;
            case "pieHeadPreview":
            {
                var yaw = request.TryGetProperty("yaw", out var yv) && yv.TryGetSingle(out var y) ? y : 0f;
                var pitch = request.TryGetProperty("pitch", out var pv) && pv.TryGetSingle(out var p) ? p : 0f;
                var person = SelectedPerson();
                if (person is null) throw new InvalidOperationException("No Sim is selected for pie-head preview.");
                var avatarGeometry = AvatarSceneGeometry.For(game, registry ?? throw new InvalidOperationException("There is no loaded native object catalog."));
                if (pieHead is null || pieHead.Value.Sim != person.Id || pieHead.Value.Tick != updates)
                {
                    var built = avatarGeometry.BuildHeadMeshes(person, viewLevel, cameraOrientation, (x, y) => 0f, new LotCamera(cameraOrientation, LotWidth, LotHeight));
                    pieHead = (person.Id, updates, built);
                }
                var rgba = SimPortrait.PieHeadPreview(pieHead.Value.Meshes, yaw, pitch);
                result = new
                {
                    width = SimPortrait.PieHeadWidth,
                    height = SimPortrait.PieHeadHeight,
                    pixels = Convert.ToBase64String(rgba)
                };
                break;
            }
            case "sceneAck":
            {
                var acknowledged = request.GetProperty("revision").GetInt64();
                if (acknowledged < 1 || acknowledged > revision) throw new ArgumentException("Invalid scene acknowledgement");
                SimsSimulation.InMemorySessionAssets.PruneOldScenes(SceneDirectory, acknowledged - 1);
                if (Directory.Exists(SceneDirectory))
                {
                    foreach (var path in Directory.EnumerateFiles(SceneDirectory, "scene-*.json"))
                        if (long.TryParse(Path.GetFileNameWithoutExtension(path).AsSpan(6), out var number) && number < acknowledged - 1)
                            File.Delete(path);
                }
                result = new { ack = true };
                break;
            }
            case "level":
                var previousViewLevel = viewLevel;
                var previousLevelRoofs = roofsVisible;
                var previousLevelDw = doorWindowBuildToolActive;
                viewLevel = request.TryGetProperty("value", out var levelValue) && levelValue.ValueKind == JsonValueKind.Number && levelValue.TryGetInt32(out var requestedLevel)
                    ? Math.Clamp(requestedLevel, 1, Stories)
                    : 1;
                if (viewLevel != previousViewLevel) ClearCutaway();
                if (request.TryGetProperty("roofs", out var levelRoofsValue) && levelRoofsValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    roofsVisible = levelRoofsValue.GetBoolean();
                else if (!IsExplicitNativeEnv)
                    roofsVisible = false;
                if (request.TryGetProperty("doorWindowTool", out var levelDwValue) && levelDwValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    doorWindowBuildToolActive = editingView && levelDwValue.GetBoolean();
                if (!streaming || revision == 0 || viewLevel != previousViewLevel || roofsVisible != previousLevelRoofs || doorWindowBuildToolActive != previousLevelDw)
                    Export();
                result = State();
                break;
            case "view":
                var requestedEditingView = request.TryGetProperty("editing", out var editingValue) &&
                    editingValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? editingValue.GetBoolean()
                    : throw new ArgumentException("editing must be a boolean");
                var requestedDoorWindowView = requestedEditingView &&
                    (request.TryGetProperty("doorWindowTool", out var dwValue) && dwValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? dwValue.GetBoolean()
                        : doorWindowBuildToolActive);
                if (IsVisitingAwayLot)
                {
                    requestedEditingView = false;
                    requestedDoorWindowView = false;
                }
                if (editingView != requestedEditingView || doorWindowBuildToolActive != requestedDoorWindowView)
                {
                    editingView = requestedEditingView;
                    doorWindowBuildToolActive = requestedDoorWindowView;
                    Export();
                }
                result = State();
                break;
            case "walls":
                var previousWallMode = wallMode;
                var previousWallsRoofs = roofsVisible;
                var previousWallsDw = doorWindowBuildToolActive;
                if (request.TryGetProperty("mode", out var wallModeValue) && wallModeValue.ValueKind == JsonValueKind.String)
                    wallMode = wallModeValue.GetString() is { } requestedMode && requestedMode is "up" or "down" or "cutaway"
                        ? requestedMode : throw new ArgumentException("Wall mode must be up, down, or cutaway.");
                else if (request.TryGetProperty("up", out var wallsUpValue) && wallsUpValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    wallMode = wallsUpValue.GetBoolean() ? "up" : "down";
                else throw new ArgumentException("Wall mode is required.");
                // Switching wall display modes to walls-up, walls-down, or cutaway automatically hides
                // roofs.
                roofsVisible = false;
                if (wallMode != "cutaway")
                {
                    ClearCutaway();
                }
                else if (request.TryGetProperty("x", out _) && request.TryGetProperty("y", out _))
                {
                    ApplyCutawayTarget(request);
                }
                if (request.TryGetProperty("doorWindowTool", out var wallsDwValue) && wallsDwValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    doorWindowBuildToolActive = editingView && wallsDwValue.GetBoolean();
                if (!streaming || revision == 0 || wallMode != previousWallMode || roofsVisible != previousWallsRoofs || doorWindowBuildToolActive != previousWallsDw ||
                    (wallMode == "cutaway" && world is not null && lot is not null && !SceneExporter.CutawayMaskMatches(world, animationState, revision, viewLevel, wallMode, cameraOrientation,
                        cutawayTile, cutawayRooms, CutawaySimTile(), cutawayZoom)))
                    Export();
                result = State();
                break;
            case "roofs":
                var previousRoofsVisible = roofsVisible;
                var previousRoofsWallMode = wallMode;
                var previousRoofsLevel = viewLevel;
                var previousRoofsDw = doorWindowBuildToolActive;
                if (!request.TryGetProperty("visible", out var roofsValue) || roofsValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("Roof visibility (visible) is required.");
                roofsVisible = roofsValue.GetBoolean();
                if (roofsVisible)
                {
                    wallMode = "up";
                    if (request.TryGetProperty("level", out var roofsLevelValue) && roofsLevelValue.ValueKind == JsonValueKind.Number && roofsLevelValue.TryGetInt32(out var requestedRoofsLevel))
                        viewLevel = Math.Clamp(requestedRoofsLevel, 1, Stories);
                    else if (!IsExplicitNativeEnv)
                        viewLevel = Stories;
                    ClearCutaway();
                }
                if (request.TryGetProperty("doorWindowTool", out var roofsDwValue) && roofsDwValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    doorWindowBuildToolActive = editingView && roofsDwValue.GetBoolean();
                if (!streaming || revision == 0 || roofsVisible != previousRoofsVisible || wallMode != previousRoofsWallMode ||
                    viewLevel != previousRoofsLevel || doorWindowBuildToolActive != previousRoofsDw)
                    Export();
                result = State();
                break;
            case "cutaway":
                if (wallMode != "cutaway") { result = new { ignored = true }; break; }
                ApplyCutawayTarget(request);
                if (world is null || lot is null || !SceneExporter.CutawayMaskMatches(world, animationState, revision, viewLevel, wallMode, cameraOrientation,
                        cutawayTile, cutawayRooms, CutawaySimTile(), cutawayZoom))
                    Export();
                result = State(includeArchitectureGrids: !streaming);
                break;
            case "cameraOrientation":
                var previousCameraOrientation = cameraOrientation;
                var requestedOrientation = request.TryGetProperty("value", out var orientationValue) && orientationValue.TryGetInt32(out var orientation)
                    ? orientation : throw new ArgumentException("Camera orientation must be an integer.");
                cameraOrientation = ((requestedOrientation % 4) + 4) % 4;
                if (!streaming || revision == 0 || cameraOrientation != previousCameraOrientation)
                    Export();
                result = State();
                break;
            case "selectSim":
                if (ConnectedNeighborhoodEnabled && TrySelectConnectedSim(request, out var connectedSelectResult))
                {
                    result = connectedSelectResult!;
                    break;
                }
                var requestedSim = request.TryGetProperty("objectID", out var simValue) && simValue.ValueKind == JsonValueKind.Number && simValue.TryGetInt16(out var sim)
                    ? sim : throw new ArgumentException("objectID must be a person object ID.");
                if (world?.ObjectById(requestedSim) is not XPerson)
                    throw new ArgumentException("Select a Sim in the active household");
                var selectionChanged = selectedSim != requestedSim;
                selectedSim = requestedSim;
                // Selecting a Sim sets simulation global 3 to their person identifier and sets bit 2 of
                // their person Flags while clearing it on all other Sims.
                if (world is not null) SyncSelectedSimFlags(world, selectedSim);
                if (selectionChanged)
                {
                    world?.DispatchNotice(0xe4, requestedSim);
                    RefreshDynamicOrExport();
                }
                result = State();
                break;
            case "setMotive":
            {
                var actor = request.TryGetProperty("objectID", out var objIdVal) && objIdVal.TryGetInt16(out var oid) && world?.ObjectById(oid) is XPerson p
                    ? p : SelectedPerson();
                if (actor is null) throw new InvalidOperationException("No target Sim found.");
                var motiveName = request.TryGetProperty("motive", out var mVal) && mVal.ValueKind == JsonValueKind.String
                    ? mVal.GetString() : throw new ArgumentException("motive name required.");
                var motiveValue = request.TryGetProperty("value", out var vVal) && vVal.TryGetSingle(out var v)
                    ? v : throw new ArgumentException("motive value required.");
                if (!Enum.TryParse<Motive>(motiveName, true, out var motiveEnum))
                    throw new ArgumentException($"Unknown motive: {motiveName}");
                actor[motiveEnum] = Math.Clamp(motiveValue, -100f, 100f);
                if (motiveEnum == Motive.Mood && speed == 0) RefreshDynamicOrExport();
                result = State();
                break;
            }
            case "setTestPersonTile":
            {
                if (!testFixtureEnabled)
                    throw new InvalidOperationException("setTestPersonTile is available only in SIMS_NATIVE_TEST_FIXTURE=1 sessions");
                if (testFixtureStartPositionUsed)
                    throw new InvalidOperationException("The native test fixture start position can be set only once per session");
                if (speed != 0) throw new InvalidOperationException("The native test fixture requires speed 0");
                var actor = SelectedPerson();
                if (!ReferenceEquals(actor, RequestObject(request, "objectID")))
                    throw new ArgumentException("Test fixture placement must name the selected Sim");
                var queuedCount = unchecked(actor.ActionQueueEnd - actor.ActionQueueCurrent);
                if (actor.CurrentActionRecord is { Owner: not null } || queuedCount != 0 || actor.ActionQueue.Any(action => action.Owner is not null))
                    throw new InvalidOperationException("The native test fixture requires no started or queued action");
                if ((actor[ObjectData.ParentId] != 0 && actor[ObjectData.SlotHierNumber] > 0) || actor[ObjectData.ContainerId] != 0)
                    throw new InvalidOperationException("The native test fixture cannot reposition a parented or contained Sim");
                var tileX = request.TryGetProperty("tileX", out var tileXValue) && tileXValue.TryGetInt32(out var parsedTileX)
                    ? parsedTileX : throw new ArgumentException("tileX must be an integer lot tile");
                var tileY = request.TryGetProperty("tileY", out var tileYValue) && tileYValue.TryGetInt32(out var parsedTileY)
                    ? parsedTileY : throw new ArgumentException("tileY must be an integer lot tile");
                var height = request.TryGetProperty("height", out var heightValue) && heightValue.TryGetByte(out var parsedHeight)
                    ? parsedHeight : throw new ArgumentException("height must be a lot level byte");
                if (tileX < 0 || tileX >= LotWidth || tileY < 0 || tileY >= LotHeight)
                    throw new ArgumentException("Test fixture start tile is outside the lot");
                var destination = new ObjectPosition(tileX * 16 + 8, tileY * 16 + 8, height);
                var current = world ?? throw new InvalidOperationException("There is no loaded native lot");
                if (!current.CanPlaceObject(actor, destination))
                    throw new InvalidOperationException($"Native placement rejected test fixture start tile ({tileX},{tileY},{height}); error=0x{current.LastPlacementFailureCode:X2}");
                var originalPosition = actor.Position;
                current.PlaceObject(actor, destination);
                testFixtureStartPositionUsed = true;
                Console.Error.WriteLine($"Native test fixture start: sim={actor.Id} original=({originalPosition.X},{originalPosition.Y},{originalPosition.Height}) " +
                    $"placed=({actor.Position.X},{actor.Position.Y},{actor.Position.Height}) tile=({tileX},{tileY},{height}) actions=0");
                Export();
                result = new { objectID = actor.Id, tileX, tileY, height, x = actor.Position.X, y = actor.Position.Y,
                    room = actor.RoomWord84, actions = queuedCount };
                break;
            }
            case "testTileContents":
            {
                if (!testFixtureEnabled)
                    throw new InvalidOperationException("testTileContents is available only in SIMS_NATIVE_TEST_FIXTURE=1 sessions");
                var tileX = request.TryGetProperty("tileX", out var tileXValue) && tileXValue.TryGetInt32(out var parsedTileX)
                    ? parsedTileX : throw new ArgumentException("tileX must be an integer lot tile");
                var tileY = request.TryGetProperty("tileY", out var tileYValue) && tileYValue.TryGetInt32(out var parsedTileY)
                    ? parsedTileY : throw new ArgumentException("tileY must be an integer lot tile");
                var level = request.TryGetProperty("level", out var tileLevelValue) && tileLevelValue.TryGetInt32(out var parsedLevel)
                    ? parsedLevel : throw new ArgumentException("level must be an integer lot level");
                if (tileX < 0 || tileX >= LotWidth || tileY < 0 || tileY >= LotHeight || level < 1 || level > Stories)
                    throw new ArgumentException("Tile coordinates or level are outside the active lot");
                var current = world ?? throw new InvalidOperationException("There is no loaded native lot");
                result = new { tileX, tileY, level, objectIDs = current.ObjectsAtTile(tileX, tileY, level).Select(item => item.Id).ToArray() };
                break;
            }
            case "menu":
            {
                var actor = SelectedPerson();
                var clicked = RequestObject(request, "objectID");
                var target = InteractionMenus.Target(world!, clicked);
                var describedTarget = pluginWorld?.Describe(target);
                var nativeEntries = InteractionMenus.Read(world!, actor, clicked)
                    .Select(choice => (choice, interaction: MenuIndex(choice)))
                    .Where(item => plugins is null || describedTarget is null ||
                        plugins.AllowInteraction(actor.Id, describedTarget, new(item.interaction, item.choice.ActionText ?? "", item.choice.Parameters[0], false)))
                    .Select(item => (object)new
                    {
                        name = item.choice.ActionText ?? throw new NotSupportedException("Native interaction name is unavailable."),
                        interaction = item.interaction,
                        param0 = item.choice.Parameters[0],
                        global = false,
                        objectID = item.choice.Owner?.Id,
                    });
                var pluginEntries = (plugins is not null && describedTarget is not null && IsHouseholdPerson(actor)
                    ? plugins.MenuEntries(actor.Id, describedTarget)
                    : [])
                    .Select(entry => (object)new
                    {
                        name = entry.Name,
                        interaction = 0,
                        param0 = 0,
                        global = false,
                        plugin = entry.PackId,
                        entry = entry.Entry,
                    });
                var doorEntries = ConnectedNeighborhoodEnabled && TryGetConnectedDoorMenuEntries(actor, target, out var connectedDoorEntries)
                    ? connectedDoorEntries
                    : [];
                result = nativeEntries.Concat(doorEntries).Concat(pluginEntries).ToArray();
                break;
            }
            case "groundMenu":
            {
                var actor = SelectedPerson();
                var (position, target) = GroundPreview(request);
                try
                {
                    var choices = InteractionMenus.Read(world!, actor, target)
                        .Select(choice => (object)new
                        {
                            name = choice.ActionText ?? throw new NotSupportedException("Native interaction name is unavailable."),
                            interaction = MenuIndex(choice),
                            param0 = choice.Parameters[0],
                            global = false,
                            objectID = choice.Owner?.Id,
                        }).ToList();
                    if (IsHouseholdPerson(actor) && plugins is not null)
                    {
                        var point = GroundPoint(world!, position);
                        foreach (var entry in plugins.GroundMenuEntries(actor.Id, point))
                        {
                            choices.Add(new
                            {
                                name = entry.Name,
                                interaction = (byte)0,
                                param0 = (short)0,
                                global = false,
                                objectID = (int?)0,
                                plugin = entry.PackId,
                                entry = entry.Entry,
                            });
                        }
                    }
                    result = choices.ToArray();
                }
                finally { world!.Remove(target.Id); }
                break;
            }
            case "groundInteract":
            {
                var actor = SelectedPerson();
                if (ConnectedNeighborhoodEnabled)
                {
                    world?.CancelSyntheticDirectPositionRouteFor(actor);
                    CancelCrossLotTransitForPerson(actor, cancelRoutes: true);
                }
                if (request.TryGetProperty("plugin", out var groundPack) && groundPack.ValueKind == JsonValueKind.String)
                {
                    if (!IsHouseholdPerson(actor)) throw new InvalidOperationException("Plugin menu entries are for household Sims");
                    if (plugins is null || pluginWorld is null) throw new InvalidOperationException("No pack plugins are loaded.");
                    var (pluginPos, pluginTarget) = GroundPreview(request, persistent: false);
                    try
                    {
                        var point = GroundPoint(world!, pluginPos);
                        result = plugins.ChooseGroundMenuEntry(actor.Id, point, groundPack.GetString() ?? "",
                            request.GetProperty("entry").GetString() ?? "");
                    }
                    finally { world!.Remove(pluginTarget.Id); }
                    break;
                }
                var (position, target) = GroundPreview(request, persistent: true);
                var retained = false;
                try
                {
                    var interactionId = request.TryGetProperty("interaction", out var interactionValue) && interactionValue.TryGetByte(out var parsedInteraction)
                        ? parsedInteraction : throw new ArgumentException("Interaction must be an unsigned 8-bit index.");
                    var param0 = request.TryGetProperty("param0", out var parameterValue) && parameterValue.TryGetInt16(out var parsedParameter)
                        ? parsedParameter : (short)0;
                    var choice = InteractionMenus.Read(world!, actor, target).FirstOrDefault(choice =>
                        MenuIndex(choice) == interactionId && choice.Parameters[0] == param0);
                    if (choice is null) throw new ArgumentException("Ground interaction is not currently available.");
                    // The Go Here interaction from <c>Destination.iff</c> (GUID 0x7C4) executes on the
                    // person's normal action stack to prevent standalone routing from conflicting with its
                    // cleanup BHAV.
                    if (world!.IsImmediateMenuInteraction(choice))
                    {
                        world.RunImmediateMenuInteraction(actor, choice);
                    }
                    else if (!world.EnqueueInteractionAction(actor, choice))
                    {
                        throw new InvalidOperationException("Native action queue rejected the ground interaction.");
                    }
                    retained = true;
                    result = new { queued = true, state = State() };
                }
                finally { if (!retained) world!.Remove(target.Id); }
                break;
            }
            case "interact":
            {
                var actor = SelectedPerson();
                if (ConnectedNeighborhoodEnabled)
                {
                    world?.CancelSyntheticDirectPositionRouteFor(actor);
                    CancelCrossLotTransitForPerson(actor, cancelRoutes: true);
                }
                var clicked = RequestObject(request, "objectID");
                var target = InteractionMenus.Target(world!, clicked);
                if (request.TryGetProperty("plugin", out var menuPack))
                {
                    if (!IsHouseholdPerson(actor)) throw new InvalidOperationException("Plugin menu entries are for household Sims");
                    if (plugins is null || pluginWorld is null) throw new InvalidOperationException("No pack plugins are loaded.");
                    result = plugins.ChooseMenuEntry(actor.Id, pluginWorld.Describe(target), menuPack.GetString() ?? "",
                        request.GetProperty("entry").GetString() ?? "");
                    break;
                }
                var interactionId = request.TryGetProperty("interaction", out var interactionValue) && interactionValue.TryGetByte(out var parsedInteraction)
                    ? parsedInteraction : throw new ArgumentException("Interaction must be an unsigned 8-bit index.");
                var param0 = request.TryGetProperty("param0", out var parameterValue) && parameterValue.TryGetInt16(out var parsedParameter) ? parsedParameter : (short)0;
                var global = request.TryGetProperty("global", out var globalValue) && globalValue.ValueKind == JsonValueKind.True;
                if (global) throw new NotSupportedException("Global interaction choices are not yet represented by the native menu engine.");
                if (ConnectedNeighborhoodEnabled && interactionId == ConnectedKnockDoorInteractionId &&
                    TryPerformConnectedDoorKnock(actor, target))
                {
                    result = State();
                    break;
                }
                var choice = InteractionMenus.Read(world!, actor, clicked).FirstOrDefault(candidate =>
                    MenuIndex(candidate) == interactionId && candidate.Parameters[0] == param0 &&
                    (plugins is null || pluginWorld is null || plugins.AllowInteraction(actor.Id, pluginWorld.Describe(target),
                        new(interactionId, candidate.ActionText ?? "", candidate.Parameters[0], false))));
                if (choice is null) throw new ArgumentException("Interaction is not currently available");
                if (world!.IsImmediateMenuInteraction(choice))
                {
                    world.RunImmediateMenuInteraction(actor, choice);
                }
                else if (!world.EnqueueInteractionAction(actor, choice))
                {
                    throw new InvalidOperationException("Native action queue is full or rejected the interaction.");
                }
                result = State();
                break;
            }
            case "catalog":
                if (registry is null) throw new InvalidOperationException("There is no loaded native object catalog.");
                result = new
                {
                    floors = FloorCatalogPage.All(game, language),
                    walls = WallCatalogPage.All(game, language),
                    objects = ObjectCatalog.All(registry, language),
                };
                break;
            case "catalogPage":
            {
                if (registry is null) throw new InvalidOperationException("There is no loaded native object catalog.");
                var category = request.TryGetProperty("category", out var categoryValue) && categoryValue.TryGetInt32(out var parsedCategory)
                    ? parsedCategory : throw new ArgumentException("category must be an integer from 0 through 12");
                var offset = request.TryGetProperty("offset", out var offsetValue) && offsetValue.TryGetInt32(out var parsedOffset)
                    ? parsedOffset : throw new ArgumentException("offset must be an integer");
                var count = request.TryGetProperty("count", out var countValue) && countValue.TryGetInt32(out var parsedCount)
                    ? parsedCount : throw new ArgumentException("count must be an integer");
                var room = request.TryGetProperty("room", out var roomValue) && roomValue.TryGetInt32(out var parsedRoom) ? parsedRoom : (int?)null;
                int? subsort = null;
                if (request.TryGetProperty("subsort", out var subsortValue))
                    subsort = subsortValue.TryGetInt32(out var parsedSubsort)
                        ? parsedSubsort : throw new ArgumentException("subsort must be an integer from 0 through 7");
                result = ObjectCatalog.Page(registry, language, category, offset, count, SceneDirectory, room, subsort, game, CurrentCatalogLotType());
                var requestedDoorWindowTool = editingView && category is 8 or 9;
                if (doorWindowBuildToolActive != requestedDoorWindowTool)
                {
                    doorWindowBuildToolActive = requestedDoorWindowTool;
                    Export();
                }
                break;
            }
            case "buildCatalogPage":
            {
                if (doorWindowBuildToolActive)
                {
                    doorWindowBuildToolActive = false;
                    Export();
                }
                var kind = request.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : null;
                if (kind is not ("floor" or "wall" or "wallStyle" or "roof" or "water")) throw new NotSupportedException($"The native build catalog does not support '{kind}' patterns");
                var offset = request.TryGetProperty("offset", out var offsetValue) && offsetValue.TryGetInt32(out var parsedOffset)
                    ? parsedOffset : throw new ArgumentException("offset must be an integer");
                var count = request.TryGetProperty("count", out var countValue) && countValue.TryGetInt32(out var parsedCount)
                    ? parsedCount : throw new ArgumentException("count must be an integer");
                result = kind switch
                {
                    "floor" => FloorCatalogPage.Page(game, language, offset, count, SceneDirectory),
                    "water" => registry is null
                        ? throw new InvalidOperationException("There is no loaded native object catalog.")
                        : FloorCatalogPage.WaterPage(game, registry, language, offset, count, SceneDirectory),
                    "wallStyle" => registry is null
                        ? throw new InvalidOperationException("There is no loaded native object catalog.")
                        : SpecialBuildCatalogPage.WallStyles(game, registry, language, offset, count, SceneDirectory),
                    "roof" => SpecialBuildCatalogPage.Roofs(game, offset, count, SceneDirectory),
                    _ => WallCatalogPage.Page(game, language, offset, count, SceneDirectory),
                };
                break;
            }
            case "previewObject":
                result = PreviewObject(request);
                break;
            case "buildCursor":
                result = PreviewObject(request, cursor: true);
                break;
            case "buyObject":
            {
                EnsureLotEditingAllowed();
                RemoveTileCursor();
                var item = BuyObject(request, out var boughtPrice);
                saveDirty = true;
                Export();
                result = new
                {
                    bought = true,
                    objectID = item.Id,
                    guid = unchecked((uint)(item.TypeRecord?.Key ?? 0)),
                    price = boughtPrice,
                    budget = world!.FamilyBudget!.ReadFamilyBudget(),
                };
                break;
            }
            case "sellObject":
            {
                EnsureLotEditingAllowed();
                result = SellObject(request);
                MarkSaveDirtyIfChanged(result);
                Export();
                break;
            }
            case "moveObject":
            {
                EnsureLotEditingAllowed();
                result = MoveObject(request);
                MarkSaveDirtyIfChanged(result);
                Export();
                break;
            }
            case "architecture":
            {
                EnsureLotEditingAllowed();
                result = Architecture(request);
                MarkSaveDirtyIfChanged(result);
                Export();
                break;
            }
            case "architectureBatch":
            {
                EnsureLotEditingAllowed();
                result = ArchitectureBatch(request);
                MarkSaveDirtyIfChanged(result);
                Export();
                break;
            }
            case "roof":
            {
                EnsureLotEditingAllowed();
                result = RoofSettings(request);
                MarkSaveDirtyIfChanged(result);
                Export();
                break;
            }
            case "undo":
            case "redo":
            {
                EnsureLotEditingAllowed();
                result = ArchitectureHistory(request.GetProperty("op").GetString() == "undo");
                MarkSaveDirtyIfChanged(result);
                Export();
                break;
            }
            case "cancelAction":
            {
                var actor = SelectedPerson();
                var requestedActor = RequestObject(request, "objectID");
                if (!ReferenceEquals(actor, requestedActor)) throw new ArgumentException("Action belongs to another Sim");
                var token = request.TryGetProperty("token", out var tokenValue) && tokenValue.ValueKind == JsonValueKind.String
                    ? tokenValue.GetString() : null;
                if (ConnectedNeighborhoodEnabled && TryHandleCancelConnectedTransitAction(actor, token))
                {
                    result = State();
                    break;
                }
                var prefix = $"native-{actor.Id}-";
                if (token is null || !token.StartsWith(prefix, StringComparison.Ordinal) ||
                    !int.TryParse(token.AsSpan(prefix.Length), out var sequence))
                    throw new ArgumentException("Action is no longer cancellable");
                if (!world!.CancelInteractionAction(actor, sequence))
                    throw new ArgumentException("Action is no longer cancellable");
                if (ConnectedNeighborhoodEnabled)
                {
                    world.CancelSyntheticDirectPositionRouteFor(actor);
                    CancelCrossLotTransitForPerson(actor, cancelRoutes: true);
                }
                result = State();
                break;
            }
            case "dialogResponse":
            {
                if (world is null) throw new InvalidOperationException("There is no loaded native lot.");
                var token = request.TryGetProperty("token", out var tokenValue) && tokenValue.ValueKind == JsonValueKind.String
                    ? tokenValue.GetString() : null;
                var code = request.TryGetProperty("code", out var codeValue) && codeValue.TryGetByte(out var responseCode)
                    ? responseCode : throw new ArgumentException("Dialog response code must be a byte.");
                var responseText = request.TryGetProperty("text", out var textValue)
                    ? textValue.ValueKind == JsonValueKind.String ? textValue.GetString() : throw new ArgumentException("Dialog response text must be a string.")
                    : null;
                if (token is null) throw new ArgumentException("Dialog response token is required.");
                world.RespondMessageDialog(token, code, responseText);
                if (HasBlockingDialog(world))
                {
                    // Keeps the simulation paused across queued blocking dialogs, preserving the original
                    // resume speed until the queue empties.
                    SetSimulationSpeed(0, blockingDialog: true);
                }
                else
                {
                    if (dialogResumeSpeed > 0) SetSimulationSpeed(dialogResumeSpeed);
                    dialogResumeSpeed = 0;
                }
                result = State();
                RefreshDynamicOrExport();
                break;
            }
            case "genericSimsCallPersonPicker":
            {
                var current = world ?? throw new InvalidOperationException("There is no loaded native lot.");
                if (request.TryGetProperty("requestID", out var requestIDValue))
                {
                    if (!requestIDValue.TryGetInt64(out var requestID))
                        throw new ArgumentException("GenericSimsCall requestID must be an integer.");
                    var rowIndex = request.TryGetProperty("personRecordIndex", out var rowIndexValue) &&
                                   rowIndexValue.TryGetInt32(out var parsedRowIndex)
                        ? parsedRowIndex : throw new ArgumentException("personRecordIndex must be an integer.");
                    var accepted = current.TryChooseGenericSimsCallPerson(requestID, rowIndex);
                    result = new { accepted, state = State() };
                    break;
                }

                var pendingRequests = current.GenericSimsCallRequests.Snapshot();
                var pending = pendingRequests.FirstOrDefault(candidate =>
                    candidate.Status == 0 && candidate.Kind == GenericSimsCallRequestKind.Second &&
                    pendingRequests.FirstOrDefault(first => first.PersonKey == candidate.PersonKey).Id == candidate.Id);
                if (pending.Id == 0 ||
                    current.WalkOrder.OfType<XPerson>().FirstOrDefault(person => person.StackObjectKey == pending.Temporary0) is not { } owner)
                {
                    result = null;
                    break;
                }

                int? viewerCenterX = request.TryGetProperty("centerX", out var centerXValue) && centerXValue.TryGetInt32(out var parsedCenterX)
                    ? parsedCenterX : null;
                int? viewerCenterY = request.TryGetProperty("centerY", out var centerYValue) && centerYValue.TryGetInt32(out var parsedCenterY)
                    ? parsedCenterY : null;
                if (viewerCenterX.HasValue != viewerCenterY.HasValue)
                    throw new ArgumentException("GenericSimsCall chooser viewer center requires both coordinates.");
                var personCountBefore = current.WalkOrder.OfType<XPerson>().Count();
                var candidates = current.GenericSimsCallChooserCandidates(owner, viewerCenterX, viewerCenterY);
                if (current.WalkOrder.OfType<XPerson>().Count() != personCountBefore) Export();

                result = new
                {
                    requestID = pending.Id,
                    personKey = pending.PersonKey,
                    requestTarget = pending.Temporary0,
                    candidates,
                };
                break;
            }
            case "appearanceCatalog":
            {
                var reg = EnsureRegistry();
                var gender = request.GetProperty("gender").GetString()!;
                var age = request.GetProperty("age").GetString()!;
                var skin = request.GetProperty("skin").GetString()!;
                var catalog = HouseholdAppearance.Catalog(game, reg, gender, age, skin);
                result = catalog;
                break;
            }
            case "appearancePreview":
            {
                var reg = EnsureRegistry();
                var gender = request.GetProperty("gender").GetString()!;
                var age = request.GetProperty("age").GetString()!;
                var skin = request.GetProperty("skin").GetString()!;
                var headChoice = request.GetProperty("headChoice").GetString()!;
                var bodyChoice = request.GetProperty("bodyChoice").GetString()!;
                var portrait = request.TryGetProperty("portrait", out var portraitProp) && portraitProp.ValueKind is JsonValueKind.True;
                result = HouseholdAppearancePreview.Export(session, game, reg, gender, age, skin, headChoice, bodyChoice, portrait);
                break;
            }
            case "createHousehold":
                result = CreateHousehold(request);
                break;
            case "moveInFamily":
                result = MoveInFamily(request);
                break;
            case "warmup":
            {
                var reg = EnsureRegistry();
                _ = WorldBootstrap.GetOrLoadGlobalLibrary(game);
                _ = WorldBootstrap.GetOrLoadCareers(game);
                AvatarAnimations.GetSharedIndex(game, reg);
                _ = AvatarSceneGeometry.For(game, reg);
                result = new { warmed = true, entries = reg.Entries.Count };
                break;
            }
            case "openLot":
            {
                var requestedHouse = request.TryGetProperty("house", out var houseProp) && houseProp.TryGetInt16(out var hVal) && hVal > 0
                    ? hVal : throw new ArgumentException("house must be a positive integer");
                ushort? moveInFamily = request.TryGetProperty("moveInFamily", out var moveInFamProp) && moveInFamProp.ValueKind != JsonValueKind.Null
                    ? (moveInFamProp.TryGetUInt16(out var parsedMoveInFam) ? parsedMoveInFam : throw new ArgumentException("moveInFamily must be an unsigned 16-bit family id"))
                    : null;
                ushort? activeFamilyOverride = request.TryGetProperty("activeFamily", out var activeFamProp) && activeFamProp.ValueKind != JsonValueKind.Null
                    ? (activeFamProp.TryGetUInt16(out var parsedActiveFam) ? parsedActiveFam : throw new ArgumentException("activeFamily must be an unsigned 16-bit family id"))
                    : null;
                int? centerX = request.TryGetProperty("centerX", out var cxProp) && cxProp.TryGetInt32(out var cxVal) ? cxVal : null;
                int? centerY = request.TryGetProperty("centerY", out var cyProp) && cyProp.TryGetInt32(out var cyVal) ? cyVal : null;
                result = OpenLotInternal(requestedHouse, moveInFamily, activeFamilyOverride, (centerX, centerY));
                break;
            }
            case "closeLot":
            {
                var saveBeforeClose = request.TryGetProperty("save", out var saveProp) && saveProp.ValueKind == JsonValueKind.True;
                result = CloseLotInternal(saveBeforeClose);
                Write(new { type = "state", state = State() });
                break;
            }
            case "neighborhood":
            {
                var neighborhoodMode = request.TryGetProperty("mode", out var modeValue) ? modeValue.GetInt32() : 1;
                if (request.TryGetProperty("neighborhood", out var neighborhoodValue) &&
                    neighborhoodValue.ValueKind == JsonValueKind.Number &&
                    neighborhoodValue.TryGetInt32(out var requestedNeighborhood))
                {
                    if (requestedNeighborhood is < 1 or > Neighborhoods.Maximum)
                        throw new ArgumentException("neighborhood must be a neighborhood number from 1 through 99");
                    if (requestedNeighborhood != Neighborhoods.Current)
                    {
                        Neighborhoods.Current = requestedNeighborhood;
                        var folderName = Neighborhoods.DirectoryName(requestedNeighborhood);
                        var installRoot = ResolveInstallRoot();
                        if (!Directory.Exists(Path.Combine(profile, folderName)) || Directory.Exists(Path.Combine(installRoot, folderName)))
                            ProfileBootstrap.EnsureNeighborhoodImported(installRoot, profile, Neighborhoods.Current);
                        var nghDir = Neighborhoods.UserData(profile);
                        if (!File.Exists(Path.Combine(nghDir, "Neighborhood.iff")))
                            throw new InvalidOperationException($"Neighborhood {Neighborhoods.Current} is not installed in this profile");
                        this.neighborhood = null;
                        this.neighborhoodPath = null;
                        globallyRemovedNeighbors.Clear();
                        world?.ClearAllRemovedNeighbors();
                        foreach (var live in lotPool.LiveSessions.Values)
                            live.World.ClearAllRemovedNeighbors();
                        if (ConnectedNeighborhoodEnabled && world is null)
                            ClearConnectedNeighborhoodPoolOnClose();
                        if (registry is not null)
                            registry = registry.WithRefreshedPeople(Path.Combine(nghDir, "Characters"));
                    }
                }
                if (request.TryGetProperty("edit", out var editValue) &&
                    editValue.ValueKind == JsonValueKind.String &&
                    editValue.GetString() is { Length: > 0 } editSpec)
                {
                    var installRoot = ResolveInstallRoot();
                    foreach (var singleEdit in editSpec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        NeighborhoodEdits.Apply(singleEdit, installRoot, profile, language, neighborhoodMode);
                    globallyRemovedNeighbors.Clear();
                    world?.ClearAllRemovedNeighbors();
                    foreach (var live in lotPool.LiveSessions.Values)
                        live.World.ClearAllRemovedNeighbors();
                    var nghDiskPath = Path.Combine(Neighborhoods.UserData(profile), "Neighborhood.iff");
                    if (world is null)
                    {
                        this.neighborhood = null;
                        this.neighborhoodPath = null;
                    }
                    else if (File.Exists(nghDiskPath))
                    {
                        this.neighborhoodPath = nghDiskPath;
                        this.neighborhood = IffFile.Open(nghDiskPath);
                        if (ConnectedNeighborhoodEnabled)
                        {
                            SyncAllLiveWorldsFromNeighborhood(globallyRemovedNeighbors);
                            neighborhoodManager.SyncFromUserData(Neighborhoods.UserData(profile), language);
                        }
                    }
                    if (registry is not null)
                        registry = registry.WithRefreshedPeople(Path.Combine(Neighborhoods.UserData(profile), "Characters"));
                }
                var outputDirectory = request.TryGetProperty("outputDirectory", out var outDirValue) &&
                    outDirValue.ValueKind == JsonValueKind.String &&
                    outDirValue.GetString() is { Length: > 0 } customDir
                    ? Path.GetFullPath(customDir)
                    : session;
                Directory.CreateDirectory(outputDirectory);
                var webScenes = request.TryGetProperty("webScenes", out var webScenesValue) && webScenesValue.ValueKind == JsonValueKind.True;
                var exportInstallRoot = ResolveInstallRoot();
                NeighborhoodExport.GameRoot = game;
                if (neighborhoodMode is 2 or 3 or 4 or 5 or 7)
                    lastTravelNeighborhoodMode = neighborhoodMode;
                result = NeighborhoodExport.Export(outputDirectory, neighborhoodMode, profile, language, dataRoot, exportInstallRoot,
                    includeRenderScenes: webScenes, existingRegistry: registry, mountRoot: game);
                if (neighborhoodMode is 2 or 3 or 5 or 7)
                {
                    var manifestPath = Path.Combine(outputDirectory, "neighborhood.json");
                    var slot = TravelFilterSlotForMode(neighborhoodMode);
                    var fallback = neighborhoodMode is 5 or 7 ? 0x400 : 2;
                    var filter = TravelFilter.Load(Neighborhoods.UserData(profile), slot, language, fallback);
                    if (File.Exists(manifestPath) && JsonNode.Parse(File.ReadAllBytes(manifestPath)) is JsonObject manifestObj)
                    {
                        manifestObj["travelFilter"] = filter;
                        if (neighborhoodMode is 2 or 3 or 5 && manifestObj["entries"] is JsonArray entriesArray)
                        {
                            foreach (var node in entriesArray)
                            {
                                if (node is not JsonObject entryObj) continue;
                                var house = entryObj["house"]?.GetValue<int>() ?? 0;
                                var residential = entryObj["residential"]?.GetValue<bool>() == true;
                                var isExpansionCommunity = !residential && (
                                    (neighborhoodMode == 2 && house is >= 21 and <= 30) ||
                                    (neighborhoodMode == 3 && house is >= 40 and <= 49) ||
                                    (neighborhoodMode == 5 && house is >= 81 and <= 89));
                                if (isExpansionCommunity && entryObj["community"]?.GetValue<bool>() != true)
                                    entryObj["community"] = true;
                            }
                        }
                        WriteReplace(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifestObj, new JsonSerializerOptions { WriteIndented = true }));
                        result = manifestObj;
                    }
                }
                break;
            }
            case "saveBackups":
            {
                var backupSlot = Slot(request);
                result = new { slot = backupSlot, backups = SaveCompatibility.List(profile, Neighborhoods.StorageSlot(backupSlot, Neighborhoods.Current)) };
                break;
            }
            case "restoreBackup":
            {
                var restoreSlot = Slot(request);
                var storageSlot = Neighborhoods.StorageSlot(restoreSlot, Neighborhoods.Current);
                var envelopePath = SaveEnvelopePath(restoreSlot);
                var restored = SaveCompatibility.Restore(profile, storageSlot, envelopePath, request.GetProperty("backup").GetString() ?? "");
                if (File.Exists(envelopePath) &&
                    JsonSerializer.Deserialize<SaveEnvelope>(File.ReadAllBytes(envelopePath), Json) is { State.Length: > 0 } restoredEnvelope)
                {
                    Directory.CreateDirectory(Saves);
                    WriteReplace(SavePath(restoreSlot), Convert.FromBase64String(restoredEnvelope.State));
                    if (restoredEnvelope.Runtime is not null)
                        WriteReplace(SaveRuntimePath(restoreSlot), JsonSerializer.SerializeToUtf8Bytes(restoredEnvelope.Runtime, Json));
                    if (!string.IsNullOrEmpty(restoredEnvelope.NeighborhoodState))
                        WriteReplace(SaveNeighborhoodPath(restoreSlot), Convert.FromBase64String(restoredEnvelope.NeighborhoodState));
                }
                result = restored;
                break;
            }

            case "outfitChoices":
            {
                if (world is null) throw new InvalidOperationException("No lot is loaded.");
                var outfits = engineOutfits ??= new SimOutfits(game, registry ?? throw new InvalidOperationException("There is no loaded native object catalog."), language);
                var context = outfits.Find(world);
                var choices = outfits.Choices(context);
                result = new
                {
                    pet = context.Pet,
                    choices = choices.Select(c => new { name = c.Appearance }).ToArray()
                };
                break;
            }
            case "outfitPreview":
            {
                if (world is null) throw new InvalidOperationException("No lot is loaded.");
                var index = request.GetProperty("index").GetInt32();
                var outfits = engineOutfits ??= new SimOutfits(game, registry ?? throw new InvalidOperationException("There is no loaded native object catalog."), language);
                var context = outfits.Find(world);
                result = outfits.Preview(session, context, index);
                break;
            }
            case "freeWill":
            {
                if (!request.TryGetProperty("enabled", out var enabledValue) ||
                    enabledValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("Free Will requires a boolean enabled value.");
                if (world is not null)
                {
                    world.AutonomyForced = enabledValue.GetBoolean();
                }
                if (world?.AutonomyForced == false && simulationError?.Contains("opcode 0x3", StringComparison.Ordinal) == true)
                    simulationError = null;
                result = State();
                break;
            }
            case "cheat":
            {
                var rawCode = request.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String
                    ? codeValue.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(rawCode))
                {
                    result = new { applied = false, code = "" };
                    break;
                }

                string[] knownCheats =
                [
                    "rosebud", "klapaucius", "move_objects", "autonomy", "set_hour",
                    "water_tool", "map_edit", "magic_tokens", "nessie", "cars", "help"
                ];

                var rawSegments = rawCode.Split(';')
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToArray();
                if (rawSegments.Length == 0)
                {
                    result = new { applied = false, code = "" };
                    break;
                }

                object? lastResult = null;
                foreach (var rawSegment in rawSegments)
                {
                    var tokens = rawSegment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tokens[0] == "!")
                    {
                        if (string.IsNullOrEmpty(lastCheatCommand))
                            throw new ArgumentException("Sorry: No previous cheat.");
                        if (tokens.Length == 1)
                        {
                            tokens = lastCheatCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        }
                        else
                        {
                            var prevCmd = lastCheatCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                            tokens = [prevCmd, .. tokens[1..]];
                        }
                    }

                    var segment = string.Join(' ', tokens);
                    var cmd = tokens[0].ToLowerInvariant();
                    switch (cmd)
                    {
                        case "rosebud":
                        case "klapaucius":
                        {
                            if (world?.FamilyBudget is not FamilyBudget budget || budget.ActiveFamily is null)
                                throw new InvalidOperationException("Rosebud requires an active family on a lot.");
                            budget.AlterFamilyBudget(0, -1000);
                            Export();
                            lastResult = new { applied = true, code = cmd, amount = 1000, budget = budget.ReadFamilyBudget() };
                            break;
                        }
                        case "move_objects":
                        {
                            if (tokens.Length != 2 ||
                                (!string.Equals(tokens[1], "on", StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(tokens[1], "off", StringComparison.OrdinalIgnoreCase)))
                                throw new ArgumentException("Cheat Error: Usage: move_objects <on|off>");
                            if (world is null) throw new InvalidOperationException("There is no loaded native lot.");
                            var enabled = string.Equals(tokens[1], "on", StringComparison.OrdinalIgnoreCase);
                            world.FindLocationWaterOverride = enabled;
                            Export();
                            lastResult = new { applied = true, code = enabled ? "move_objects on" : "move_objects off", moveObjects = enabled };
                            break;
                        }
                        case "autonomy":
                        {
                            if (tokens.Length != 2 || !int.TryParse(tokens[1], out var level) || level is < 0 or > 100)
                                throw new ArgumentException("Cheat Error: Usage: autonomy <0-100>");
                            if (world is null) throw new InvalidOperationException("There is no loaded native lot.");
                            world.AutonomyForced = level > 0;
                            Export();
                            lastResult = new { applied = true, code = $"autonomy {level}", autonomy = level, freeWill = world.AutonomyForced };
                            break;
                        }
                        case "set_hour":
                        {
                            if (tokens.Length != 2 || !int.TryParse(tokens[1], out var parsedHour) || parsedHour is < 1 or > 24)
                                throw new ArgumentException("Cheat Error: Usage: set_hour <1-24>");
                            if (world is null) throw new InvalidOperationException("There is no loaded native lot.");
                            var normalizedHour = parsedHour == 24 ? 0 : parsedHour;
                            world.Clock.Restore(normalizedHour, world.Clock.Minute, world.Clock.Second);
                            world[SimulationGlobal.Hour] = checked((short)world.Clock.Hour);
                            world[SimulationGlobal.Minute] = checked((short)world.Clock.Minute);
                            world[SimulationGlobal.Second] = checked((short)world.Clock.Second);
                            Export();
                            lastResult = new { applied = true, code = $"set_hour {parsedHour}", hour = world.Clock.Hour };
                            break;
                        }
                        case "water_tool":
                        {
                            if (tokens.Length > 1)
                                throw new ArgumentException("Cheat Error: Usage: water_tool");
                            lastResult = new { applied = true, code = "water_tool" };
                            break;
                        }
                        case "map_edit":
                        {
                            if (tokens.Length != 2 ||
                                (!string.Equals(tokens[1], "on", StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(tokens[1], "off", StringComparison.OrdinalIgnoreCase)))
                                throw new ArgumentException("Cheat Error: Usage: map_edit <on|off>");
                            var enabled = string.Equals(tokens[1], "on", StringComparison.OrdinalIgnoreCase);
                            lastResult = new { applied = true, code = enabled ? "map_edit on" : "map_edit off", mapEdit = enabled };
                            break;
                        }
                        case "magic_tokens":
                        {
                            if (tokens.Length > 2)
                                throw new ArgumentException("Cheat Error: Usage: magic_tokens [0-10000]");
                            var amount = 1000;
                            if (tokens.Length == 2 && (!int.TryParse(tokens[1], out amount) || amount is < 0 or > 10000))
                                throw new ArgumentException("Cheat Error: Usage: magic_tokens [0-10000]");
                            var targetPerson = SelectedPerson();
                            if (world is null || targetPerson is null)
                                throw new InvalidOperationException("magic_tokens requires a selected Sim on a loaded lot.");
                            var neighborId = targetPerson[PersonData.NeighborId];
                            var inv = world.NeighborInventory(neighborId);
                            inv.Add(7, unchecked((int)NeighborhoodExport.MagiCoinTokenGuid), checked((short)amount));
                            Export();
                            lastResult = new { applied = true, code = "magic_tokens", amount, neighborId };
                            break;
                        }
                        case "nessie":
                        {
                            Write(new { type = "neighborhoodCheat", code = "nessie" });
                            lastResult = new { applied = true, code = "nessie" };
                            break;
                        }
                        case "cars":
                        {
                            var carSpeed = tokens.Length >= 2 && int.TryParse(tokens[1], out var parsedSpeed) && parsedSpeed is >= 1 and <= 2 ? parsedSpeed : 1;
                            var carCount = tokens.Length >= 3 && int.TryParse(tokens[2], out var parsedCount) && parsedCount > 0 ? Math.Min(parsedCount, 40) : 1;
                            Write(new { type = "neighborhoodCheat", code = "cars", speed = carSpeed, count = carCount });
                            lastResult = new { applied = true, code = "cars", speed = carSpeed, count = carCount };
                            break;
                        }
                        case "help":
                        {
                            if (tokens.Length > 2)
                                throw new ArgumentException("Cheat Error: Usage: help [search]");
                            var filter = tokens.Length == 2 ? tokens[1] : null;
                            var matches = string.IsNullOrEmpty(filter)
                                ? knownCheats
                                : knownCheats.Where(c => c.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
                            var text = matches.Length > 0
                                ? string.Join(", ", matches)
                                : "Sorry, no cheats match the search string.";
                            lastResult = new { applied = true, code = segment, cheats = matches, message = text };
                            break;
                        }
                        default:
                            throw new ArgumentException("Sorry: No such cheat.");
                    }
                    lastCheatCommand = segment;
                }
                result = lastResult;
                break;
            }
            case "pictureInPicture":
            {
                if (!request.TryGetProperty("live", out var liveValue) ||
                    liveValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("Picture-in-picture requires a boolean live value.");
                pictureInPicture.Live = liveValue.GetBoolean();
                result = State();
                break;
            }
            case "audio":
                audio!.Enable(request.GetProperty("enabled").GetBoolean());
                if (house > 0 && world is not null)
                    audio.PlayLotAmbience(house);
                result = new { enabled = audio.Enabled };
                break;
            case "audioEvent":
            {
                if (!audio!.Enabled)
                {
                    result = new { played = false };
                    break;
                }
                var name = request.GetProperty("name").GetString()!;
                int? owner = request.TryGetProperty("ownerID", out var ov) ? ov.GetInt32() : null;
                result = new { played = audio.Play(name, owner) };
                break;
            }
            case "audioStopOwner":
                audio!.RemoveOwner(request.GetProperty("ownerID").GetInt32());
                result = new { removed = true };
                break;
            case "modeMusic":
            {
                var mode = request.GetProperty("mode").GetString()!;
                // Mode music changes stop active background tracks without killing in-world object music
                // or global ambient audio, pausing lot ambience in Buy, Build, and Options modes while
                // playing it in Live mode.
                audio!.StopModeMusic();
                var isLive = mode.Equals("live", StringComparison.OrdinalIgnoreCase);
                audio.SetMusicModeActive(!isLive);
                var track = ModeMusic(mode);
                var played = !string.IsNullOrEmpty(track) && audio.Play(track, -1);
                result = new { name = track, played };
                break;
            }
            case "audioEnded":
                audio!.End(request.GetProperty("voice").GetInt64());
                result = new { accepted = true };
                break;
            case "audioVolume":
                audio!.SetVolume(request.GetProperty("group").GetString()!, request.GetProperty("volume").GetSingle());
                result = audio.State();
                break;
            case "audioState": result = audio!.State(); break;
            case "save":
            {
                var slot = Slot(request);
                var isCanonical = string.Equals(slot, "canonical", StringComparison.OrdinalIgnoreCase) ||
                    (request.TryGetProperty("canonical", out var cVal) &&
                     (cVal.ValueKind == JsonValueKind.True ||
                      (cVal.ValueKind == JsonValueKind.String && bool.TryParse(cVal.GetString(), out var cb) && cb)));
                var saveLot = isCanonical ||
                    (request.TryGetProperty("lot", out var lotProp) &&
                     (lotProp.ValueKind == JsonValueKind.True ||
                      (lotProp.ValueKind == JsonValueKind.String && bool.TryParse(lotProp.GetString(), out var lb) && lb)));
                result = SaveLotToSlot(slot, isCanonical, saveLot);
                break;
            }
            case "load":
            {
                var slot = Slot(request);
                var storageSlot = Neighborhoods.StorageSlot(slot, Neighborhoods.Current);
                var envelopePath = SaveEnvelopePath(slot);
                var path = SavePath(slot);
                if (!File.Exists(envelopePath) && !File.Exists(path))
                    throw new InvalidOperationException($"No native save '{slot}' exists.");
                var activeRegistry = EnsureRegistry();
                SaveEnvelope? envelope = null;
                SaveCompatibility.Result? compatibility = null;
                Dictionary<string, PluginHost.PluginSaveRecord>? savedPlugins = null;
                string? migrationBackup = null;
                if (File.Exists(envelopePath))
                {
                    envelope = JsonSerializer.Deserialize<SaveEnvelope>(File.ReadAllBytes(envelopePath), Json)
                        ?? throw new InvalidDataException("Save file is empty.");
                    if (envelope.House > 0)
                        house = envelope.House;
                    var removePacks = request.TryGetProperty("removePacks", out var removeValue) && removeValue.ValueKind == JsonValueKind.True;
                    compatibility = SaveCompatibility.Check(envelope.PackFingerprint, envelope.Packs,
                        packFingerprint, plugins?.MigratedPacks ?? [], removePacks,
                        envelope.CorePackFingerprint, corePackFingerprint);
                    var removedPackIds = compatibility.Removed
                        .Select(item => item.Split('@', 2)[0])
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    savedPlugins = envelope.Plugins is null
                        ? null
                        : envelope.Plugins.Where(entry => !removedPackIds.Contains(entry.Key))
                            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
                    plugins?.ValidateSaved(savedPlugins);
                    if (compatibility.Migrated)
                        migrationBackup = SaveCompatibility.Backup(profile, storageSlot, envelopePath);
                }
                var userData = Neighborhoods.UserData(profile);
                var snapshotNghPath = SaveNeighborhoodPath(slot);
                if (!string.IsNullOrEmpty(envelope?.NeighborhoodState))
                {
                    var targetNghPath = neighborhoodPath ?? Path.Combine(userData, "Neighborhood.iff");
                    WriteReplace(targetNghPath, Convert.FromBase64String(envelope.NeighborhoodState));
                }
                else if (File.Exists(snapshotNghPath))
                {
                    var targetNghPath = neighborhoodPath ?? Path.Combine(userData, "Neighborhood.iff");
                    WriteReplace(targetNghPath, File.ReadAllBytes(snapshotNghPath));
                }
                var lotBytes = !string.IsNullOrEmpty(envelope?.State)
                    ? Convert.FromBase64String(envelope.State)
                    : File.ReadAllBytes(path);
                var runtime = envelope?.Runtime ?? ReadSavedRuntimeMetadata(SaveRuntimePath(slot), lotBytes);
                var lotSourcePath = File.Exists(path) ? path : Path.Combine(userData, "Houses", $"House{house:D2}.iff");
                registry = activeRegistry.WithRefreshedPeople(Path.Combine(userData, "Characters"));
                var setup = WorldBootstrap.Load(game, lotSourcePath, language, () => speed == 0, house,
                    userDataPath: userData, preloadedLot: new IffFile(lotBytes, lotSourcePath),
                    scoreAutonomy: ScoreAutonomyChoice,
                    objectMigrations: compatibility?.Objects,
                    stripMissingObjects: compatibility?.Migrated == true,
                    existingRegistry: registry);
                refreshedPersonFiles.Clear();
                setup.World.AutonomyForced = world?.AutonomyForced;
                ResetSeasonalOutfits();
                lot = setup.Lot;
                ClearBuildHistory();
                world = setup.World;
                if (envelope is not null)
                {
                    updates = envelope.Updates;
                    plugins?.Restore(savedPlugins);
                }
                ResetLotValuationBaseline();
                var migrationRefund = setup.LotMigration?.Refund ?? 0;
                if (runtime.Budget is { } savedBudget &&
                    world.FamilyBudget is FamilyBudget restoredBudget &&
                    restoredBudget.ActiveFamily is { } restoredFamily)
                {
                    var effectiveBudget = checked(savedBudget + migrationRefund);
                    restoredBudget.ActiveFamily = restoredFamily.WithBudget(effectiveBudget);
                    world[SimulationGlobal.BudgetMod10000] = unchecked((short)(effectiveBudget % 10000));
                    world[SimulationGlobal.BudgetDiv10000] = unchecked((short)(effectiveBudget / 10000));
                }
                var animationsToRestore = setup.LotMigration is { ResetEntityIds.Count: > 0 } mig
                    ? runtime.PersonAnimations?.Where(a => !mig.ResetEntityIds.Contains(a.PersonId)).ToArray()
                    : runtime.PersonAnimations;
                RestorePersonAnimationRuntime(world, animationsToRestore);
                RestoreDialogRecords(world, runtime.Dialogs);
                if (runtime.ClockTicks is { } loadedClockTicks)
                    world.Clock.RestoreProgress(loadedClockTicks, runtime.ClockDay ?? 0);
                if (viewerScale != 0) world[SimulationGlobal.ZoomLevelSML] = viewerScale;
                // Entering a house resets the viewer mode to Live, overriding any mode saved in the lot
                // file.
                viewerMode = 0;
                tileCursor = null;
                world[SimulationGlobal.Mode] = viewerMode;
                InitializeFameCallbackTarget(world);
                neighborhoodPath = setup.NeighborhoodPath;
                neighborhood = setup.Neighborhood;
                activeFamilyChunkId = setup.ActiveFamilyChunkId;
                if (activeFamilyChunkId is null && !world.WalkOrder.OfType<XPerson>().Any(p => p[PersonData.FamilyNumber] > 0))
                    world[SimulationGlobal.SimlessBuildMode] = 1;
                else if (activeFamilyChunkId is not null)
                    world[SimulationGlobal.SimlessBuildMode] = 0;
                RestoreTravelRuntimeState(world, userData, house, activeFamilyChunkId, setup.ActiveFamily, runtime, envelope);
                HydrateTypeAttributesFromNeighborhood(world, lot, neighborhood);
                world.UiTextFile ??= registry?.UiText;
                AttachTutorialFlash(world);
                ClampAwayLotEditingState();
                if (world.ObjectById(selectedSim) is not XPerson)
                {
                    selectedSim = DefaultSelectedSimId();
                }
                SyncSelectedSimFlags(world, selectedSim);
                simulationError = null;
                saveDirty = false;
                viewLevel = 1;
                autoSpeedSaved = -1;
                SetSimulationSpeed(0);
                lastRunningSpeed = runtime.Speed;
                dialogResumeSpeed = HasBlockingDialog(world) && runtime.Speed > 0 ? runtime.Speed : 0;
                AttachNoticeListener(setup.World);
                RestoreConnectedNeighborhoodFromEnvelope(envelope, userData);
                audio?.Reset();
                if (audio?.Enabled == true)
                    audio.PlayLotAmbience(house);
                Export();
                if (compatibility is { Migrated: true } && setup.LotMigration is { } lotMigration)
                {
                    static uint ParseHexGuid(string value) =>
                        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var parsed)
                            ? parsed
                            : uint.TryParse(value, out var dec) ? dec : 0;
                    Write(new
                    {
                        type = "saveMigrated",
                        slot,
                        upgraded = compatibility.Upgraded,
                        added = compatibility.Added,
                        removed = compatibility.Removed,
                        removedObjects = lotMigration.RemovedObjects.Select(item => new { guid = ParseHexGuid(item.Guid), objectId = (int)item.ObjectId, name = item.Name, refund = item.Refund }).ToArray(),
                        migratedObjects = lotMigration.MigratedObjects.Select(item => new { from = ParseHexGuid(item.From), to = ParseHexGuid(item.To), objectId = (int)item.ObjectId }).ToArray(),
                        refund = lotMigration.Refund,
                        resetObjects = lotMigration.ResetEntityIds.Count,
                        backup = migrationBackup,
                    });
                }
                result = State();
                break;
            }
            case "travelFilter":
            {
                var filterMode = request.TryGetProperty("mode", out var modeValue) && modeValue.ValueKind == JsonValueKind.Number
                    ? modeValue.GetInt32()
                    : (lastTravelNeighborhoodMode ?? 4);
                var slot = TravelFilterSlotForMode(filterMode);
                TravelFilter.Save(Neighborhoods.UserData(profile), slot,
                    request.GetProperty("filter").GetInt32(), language);
                result = new { saved = true, mode = filterMode, slot };
                break;
            }
            case "plugins": result = new { plugins = plugins?.Statuses ?? [], panels = plugins?.Panels ?? [] }; break;
            case "pluginPanels": result = new { panels = plugins?.PanelState() ?? [] }; break;
            case "pluginAction":
                result = (plugins ?? throw new InvalidOperationException("No pack plugins are loaded.")).PanelAction(
                    request.GetProperty("packId").GetString() ?? "", request.GetProperty("panelId").GetString() ?? "", request.GetProperty("action").GetString() ?? "");
                break;
            case "quit":
                ReconcileCommittedVacationFieldOnDisk();
                running = false;
                result = new { quit = true };
                break;
            default:
                if (TryHandleConnectedNeighborhoodCommand(request, op, out result, out error)) break;
                error = $"native engine does not implement '{op}'";
                break;
        }
        if (error is not null) Write(new { type = "response", id, operation = op, ok = false, error });
        else Write(new { type = "response", id, operation = op, ok = true, result });
        return running;
    }

    private object SaveLotToSlot(string slot, bool isCanonical, bool saveLot)
    {
        if (world is null || lot is null) throw new InvalidOperationException("There is no native lot to save.");
        RefreshPendingPersonPortraits();
        if (ConnectedNeighborhoodEnabled)
        {
            world.CancelSyntheticDirectPositionRoutes();
            world.SaveAllPersistentPersonData();
        }
        UpdatePreSaveState(world, lot);
        if (world.SavedSimulationState is { } savedSimi)
        {
            var (furnishingsValue, architectureValue) = CurrentLotValuation(savedSimi);
            world.ReplaceSavedValuation(furnishingsValue, architectureValue);
        }
        var (withObjects, _) = world.WriteSavedLotObjectsTo(lot);
        var saved = world.WriteSavedSimulationStateTo(withObjects);
        Directory.CreateDirectory(Saves);
        var saveSpeed = speed > 0 ? speed : lastRunningSpeed;
        var savedSuspendedHome = suspendedHomeLot is { } suspendedHome
            ? new SavedSuspendedHomeLot(suspendedHome.HomeHouse, suspendedHome.HomeFamilyChunkId,
                Convert.ToBase64String(suspendedHome.LotBytes), suspendedHome.Runtime, suspendedHome.Speed)
            : null;
        var runtimeMetadata = CaptureRuntimeMetadata(world, saved.Bytes.Span, saveSpeed, savedSuspendedHome);
        var runtimeJson = JsonSerializer.SerializeToUtf8Bytes(runtimeMetadata, Json);
        WriteReplace(SavePath(slot), saved.Bytes);
        WriteReplace(SaveRuntimePath(slot), runtimeJson);
        var lotWritten = false;
        string? lotSkipped = null;
        if (saveLot)
        {
            var neighborhoodUserData = Neighborhoods.UserData(profile);
            var userData = neighborhoodUserData;
            var housePath = Path.Combine(userData, "Houses", $"House{house:D2}.iff");
            // Direct neighborhood saving requires Simless Build Mode to be disabled. Community lots (houses
            // 20–30, 81–89, 93–98) avoid overwriting the canonical house file, while vacation lots (houses
            // 40–49) only save when an active family is present.
            var ordinarySimulationMode = world[SimulationGlobal.SimlessBuildMode] == 0;
            var directNeighborhoodSave = ordinarySimulationMode &&
                ((ConnectedNeighborhoodEnabled && IsVisitingAwayLot) ||
                 house is >= 20 and <= 30 or >= 81 and <= 89 or >= 93 and <= 98 ||
                 (house is >= 40 and <= 49
                     ? activeFamilyChunkId is null or 0
                     : Neighborhoods.IsCommunityLot(neighborhoodUserData, house)));
            if (!directNeighborhoodSave)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(housePath)!);
                WriteReplace(housePath, saved.Bytes);
                WriteReplace(CanonicalRuntimePath(housePath), runtimeJson);
                DeleteUnhousedCharacters(userData);
                lotWritten = true;
            }
            else
            {
                lotSkipped = "away";
            }
        }
        if (lotWritten && activeFamilyChunkId is { } famId && world.FamilyBudget is FamilyBudget fb && fb.ActiveFamily is { } fam)
        {
            var committedVac = house is >= 40 and <= 49 ? house : 0;
            if (fam.Field0x13C != committedVac)
                fb.ActiveFamily = fam.WithFields(field0x13C: committedVac);
            committedVacationState = (famId, committedVac);
        }
        lot = saved;
        if (ConnectedNeighborhoodEnabled && neighborhood is null)
        {
            var userData = Neighborhoods.UserData(profile);
            var nghFallback = Path.Combine(userData, "Neighborhood.iff");
            if (File.Exists(nghFallback))
            {
                neighborhoodPath ??= nghFallback;
                neighborhood = IffFile.Open(nghFallback);
            }
        }
        var connectedState = CaptureConnectedNeighborhoodSaveState(saveLot);
        if (neighborhood is not null && neighborhoodPath is not null)
        {
            PersistNeighborhoodStateImmediatelyCore(commitVacationField: lotWritten);
        }
        else
        {
            plugins?.CommitNeighborhood();
        }
        if (!isCanonical && neighborhood is not null)
        {
            WriteReplace(SaveNeighborhoodPath(slot), neighborhood.Bytes);
        }
        var storageSlot = Neighborhoods.StorageSlot(slot, Neighborhoods.Current);
        var envelopePath = SaveEnvelopePath(slot);
        Directory.CreateDirectory(Path.GetDirectoryName(envelopePath)!);
        var backup = File.Exists(envelopePath) ? SaveCompatibility.Backup(profile, storageSlot, envelopePath) : null;
        var nghBytesForSave = !isCanonical && neighborhood is not null ? Convert.ToBase64String(neighborhood.Bytes.Span) : null;
        var envelope = new SaveEnvelope(2, "native-simsengine-v0", packFingerprint, house, updates,
            Convert.ToBase64String(saved.Bytes.Span),
            plugins?.MigratedPacks.Select(pack => new SaveCompatibility.PackRecord(pack.Id, pack.Version)).ToArray() ?? [],
            plugins?.Save() ?? [], Neighborhoods.Current, runtimeMetadata with { SuspendedHome = null },
            nghBytesForSave, savedSuspendedHome, runtimeMetadata.RestrictedActivePersonKeys, connectedState,
            CorePackFingerprint: corePackFingerprint);
        WriteReplace(envelopePath, JsonSerializer.SerializeToUtf8Bytes(envelope, Json));
        ResetLotValuationBaseline();
        saveDirty = false;
        return new { slot, path = envelopePath, saved = true, bytes = saved.Bytes.Length, backup, updates, canonical = isCanonical, lot = lotWritten, lotSkipped };
    }

    private object CloseLotInternal(bool save = false)
    {
        var saved = false;
        if (save && world is not null && lot is not null)
        {
            SaveLotToSlot("manual", isCanonical: false, saveLot: true);
            saved = true;
        }
        else
        {
            ReconcileCommittedVacationFieldOnDisk();
        }
        RemoveTileCursor();
        ResetSeasonalOutfits();
        audio?.Reset();
        audio?.SetMusicModeActive(false);
        world = null;
        lot = null;
        house = 0;
        activeFamilyChunkId = null;
        neighborhood = null;
        neighborhoodPath = null;
        suspendedHomeLot = null;
        committedVacationState = null;
        refreshedPersonFiles.Clear();
        simulationError = null;
        saveDirty = false;
        simulatorBlocked = false;
        speed = 0;
        lastRunningSpeed = 1;
        autoSpeedSaved = -1;
        dialogResumeSpeed = 0;
        selectedSim = 0;
        pieHead = null;
        cutawayTile = null;
        cutawayRooms.Clear();
        exportedCutawaySimKey = -1;
        ClearBuildHistory();
        revision = 0;
        previewRevision = 0;
        animationSequence = 0;
        animationState.Revision = 0;
        animationState.Kept = null;
        lastStreamedObjectsSignature = null;
        buildableLot = null;
        buildableMask = null;
        stateFloorsLot = null;
        stateFloorsCache = null;
        stateWallsLot = null;
        stateWallsCache = null;
        cachedRoofLot = null;
        initialObjectValuation = default;
        initialValuationLot = null;
        lotValuationEdited = false;
        viewLevel = 1;
        viewerMode = 0;
        editingView = false;
        doorWindowBuildToolActive = false;
        roofsVisible = false;
        pendingAutonomousReturnHomes.Clear();
        globallyRemovedNeighbors.Clear();
        if (ConnectedNeighborhoodEnabled) ClearConnectedNeighborhoodPoolOnClose();
        return new { closed = true, saved, house = 0 };
    }

    private object OpenLotInternal(short requestedHouse, ushort? moveInFamily = null,
        ushort? activeFamilyOverride = null, (int? X, int? Y)? spawnCenter = null) =>
        OpenLotInternal(requestedHouse, moveInFamily, activeFamilyOverride, spawnCenter, out _);

    private object OpenLotInternal(short requestedHouse, ushort? moveInFamily,
        ushort? activeFamilyOverride, (int? X, int? Y)? spawnCenter, out string? memberInstantiation)
    {
        memberInstantiation = null;
        CloseLotInternal(save: false);
        var userData = Neighborhoods.UserData(profile);
        var (effectiveHouse, redirectedFamilyChunkId) = ResolveVacationRedirect(userData, requestedHouse);
        var path = Path.Combine(userData, "Houses", $"House{effectiveHouse:D2}.iff");
        if (!File.Exists(path)) throw new FileNotFoundException($"Lot {effectiveHouse} not found.");
        var runtime = ReadSavedRuntimeMetadata(CanonicalRuntimePath(path), File.ReadAllBytes(path));
        registry = EnsureRegistry().WithRefreshedPeople(Path.Combine(userData, "Characters"));
        var setup = WorldBootstrap.Load(game, path, language, () => speed == 0, effectiveHouse,
            userDataPath: userData, activeFamilyChunkId: activeFamilyOverride ?? redirectedFamilyChunkId,
            scoreAutonomy: ScoreAutonomyChoice, existingRegistry: registry);
        house = effectiveHouse;
        lot = setup.Lot;
        world = setup.World;
        global ??= setup.Global;
        registry = setup.Registry;
        neighborhoodPath = setup.NeighborhoodPath;
        neighborhood = setup.Neighborhood;
        activeFamilyChunkId = setup.ActiveFamilyChunkId;
        HydrateTypeAttributesFromNeighborhood(world, lot, neighborhood);
        if (activeFamilyChunkId is null && moveInFamily is null &&
            !world.WalkOrder.OfType<XPerson>().Any(p => p[PersonData.FamilyNumber] > 0))
        {
            world[SimulationGlobal.SimlessBuildMode] = 1;
        }
        else if (activeFamilyChunkId is not null)
        {
            world[SimulationGlobal.SimlessBuildMode] = 0;
        }
        if (registry is not null)
            world.UiTextFile ??= registry.UiText;
        AttachTutorialFlash(world);
        if (traceActions) world.CaptureExecutionTrace = true;
        if (viewerScale != 0) world[SimulationGlobal.ZoomLevelSML] = viewerScale;
        world[SimulationGlobal.Mode] = viewerMode;
        ClampAwayLotEditingState();
        AttachNoticeListener(world);
        RestorePersonAnimationRuntime(world, runtime.PersonAnimations);
        RestoreDialogRecords(world, runtime.Dialogs);
        if (runtime.ClockTicks is { } openedClockTicks)
            world.Clock.RestoreProgress(openedClockTicks, runtime.ClockDay ?? 0);
        RestoreTravelRuntimeState(world, userData, house, activeFamilyChunkId, setup.ActiveFamily, runtime);
        ClampAwayLotEditingState();
        if (setup.ActiveFamily is { Members.Count: > 0 } activeFamily)
        {
            var expectedKeys = (world.FamilyBudget as FamilyBudget)?.RestrictedActivePersonKeys
                ?? activeFamily.Members.Select(static m => unchecked((int)m)).ToArray();
            if (!world.WalkOrder.OfType<XPerson>().Any(p => expectedKeys.Contains(p.TypeRecord?.Key ?? 0)))
            {
                memberInstantiation = EnsureSelectedHouseholdMember(activeFamily, spawnCenter?.X ?? LotWidth / 2, spawnCenter?.Y ?? LotHeight / 2);
            }
        }
        selectedSim = DefaultSelectedSimId();
        SyncSelectedSimFlags(world, selectedSim);
        SetSimulationSpeed(0);
        lastRunningSpeed = runtime.Speed;
        if (HasBlockingDialog(world) && runtime.Speed > 0)
            dialogResumeSpeed = runtime.Speed;
        InitializeFameCallbackTarget(world);
        if (global is not null)
            world.AutonomyHost = new AutonomyHost(world, global, house, () => speed == 0, ScoreAutonomyChoice);
        ResetLotValuationBaseline();
        if (moveInFamily is { } famId)
        {
            var (_, _, _, _, moveInstantiation) = MoveInFamilyCore(famId, spawnCenter?.X ?? LotWidth / 2, spawnCenter?.Y ?? LotHeight / 2);
            memberInstantiation = moveInstantiation;
            if (world is not null) world[SimulationGlobal.SimlessBuildMode] = 0;
            if (ConnectedNeighborhoodEnabled) RegisterOpenedLotInPool();
        }
        else
        {
            InitializeViewLevelFromSelectedSim();
            if (ConnectedNeighborhoodEnabled) RegisterOpenedLotInPool();
            Export();
        }
        if (audio?.Enabled == true)
            audio.PlayLotAmbience(house);
        Write(new { type = "state", state = State() });
        return new
        {
            house,
            homeHouse = EffectiveHomeHouse,
            away = IsVisitingAwayLot,
            simless = world![SimulationGlobal.SimlessBuildMode] != 0
        };
    }

    private string Saves => Path.Combine(profile, "native-saves");

    private static bool HasBlockingDialog(SimulationWorld? world) =>
        world is not null && (world.HasVisibleBlockingDialog ||
            world.DialogRecords.Any(record => record.Visible && record.State < 2 &&
                (record.Blocking || (record.Kind is 12 or 15 && record.BookMode is not null))));

    private void SetSimulationSpeed(int value, bool blockingDialog = false)
    {
        // Simulation speed is clamped to 0..1000 and mapped to SIMI global 16, while pause and resume
        // toggle SIMI global 17 without altering the configured running speed.
        speed = Math.Clamp(value, 0, 1000);
        audio?.SetSimulationSpeed(speed);
        audio?.SetSimulationPaused(speed == 0, blockingDialog);
        if (world is not { } current) return;
        if (speed > 0) current[SimulationGlobal.Speed] = speed switch { 3 => 924, 10 => 994, _ => 760 };
        current[SimulationGlobal.Paused] = (short)(speed == 0 ? 1 : 0);
    }

    private void MarkSaveDirtyIfChanged(object? result)
    {
        if (result is null) return;
        var type = result.GetType();
        if (type.GetProperty("bought")?.GetValue(result) is true ||
            type.GetProperty("sold")?.GetValue(result) is true ||
            type.GetProperty("moved")?.GetValue(result) is true ||
            type.GetProperty("applied")?.GetValue(result) is true && type.GetProperty("changed") is null)
        {
            saveDirty = true;
            return;
        }
        var changed = type.GetProperty("changed")?.GetValue(result);
        if (changed is true || changed is int count && count > 0) saveDirty = true;
    }

    private static void WriteReplace(string path, ReadOnlyMemory<byte> bytes)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes.ToArray());
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>When the simulation is paused, the object manager only updates objects with the OBJD
    /// SimulateOnPause flag enabled (bit 17), allowing controllers such as the tutorial to advance dialogs
    /// without advancing game time.</summary>
    private static int CountVisibleDialogs(SimulationWorld targetWorld)
    {
        var count = 0;
        var records = targetWorld.DialogRecords;
        for (var i = 0; i < records.Count; i++)
            if (records[i].Visible) count++;
        return count;
    }

    private void AdvancePausedTick()
    {
        try
        {
            var visible = CountVisibleDialogs(world!);
            var total = world!.DialogRecords.Count;
            world.AdvanceSimulationTick(new SimulationClockControl(0, 0, 0), 0, world.Clock.Ticks);
            LogNewThreadErrors();
            if (!streaming && (CountVisibleDialogs(world) != visible || world.DialogRecords.Count != total)) Export();
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Native paused tick failed: {error.Message}");
        }
    }

    private bool AdvanceOneTick(bool allowAutoSpeed = true)
    {
        try
        {
            var hadBlockingDialog = HasBlockingDialog(world);
            var previousHour = world!.Clock.Hour;
            Dictionary<short, (string Name, float Frame)>? routeAnimBefore = null;
            if (!world.AvatarAnimationRootOnly)
            {
                foreach (var walkItem in world.WalkOrder)
                {
                    if (walkItem is not XPerson person) continue;
                    if (person.AnimationPlayback is { RouteDriven: true, PracticeWeight: > 0f, Clip.SoundEvents.Count: > 0 } pb)
                    {
                        routeAnimBefore ??= [];
                        routeAnimBefore[person.Id] = (pb.Clip.Name, pb.Frame);
                    }
                }
            }
            var control = new SimulationClockControl(1, 1, 1);
            world.AdvanceSimulationTick(control, 0, world.Clock.Ticks);
            if (world.PendingLotSwitch is { } pendingLotSwitch)
            {
                world.PendingLotSwitch = null;
                if (TryApplyPendingLotSwitch(world, pendingLotSwitch)) return true;
            }
            if (ConnectedNeighborhoodEnabled)
                ProcessPendingAutonomousReturnHomes();
            if (RefreshPendingPersonPortraits())
            {
                saveDirty = true;
                Write(new { type = "state", state = State() });
            }
            if (selectedSim == 0 &&
                (PanelPeople().FirstOrDefault(p => IsHouseholdPerson(p) && IsControllableHuman(p))
                 ?? PanelPeople().FirstOrDefault(IsHouseholdPerson)) is { } arrivedPerson)
            {
                selectedSim = arrivedPerson.Id;
                SyncSelectedSimFlags(world, selectedSim);
                Export();
                Write(new { type = "state", state = State() });
            }
            if (!world.AvatarAnimationRootOnly && routeAnimBefore is not null)
                DispatchRouteAnimationSounds(world, routeAnimBefore);
            if (!world.AvatarAnimationRootOnly && plugins is not null && EnsureSeasonalOutfits() is { } outfits)
            {
                var suitMinute = world.Clock.Hour * 60 + world.Clock.Minute;
                if (cachedOutdoorSuit.House != house || cachedOutdoorSuit.Minute != suitMinute)
                    cachedOutdoorSuit = (house, suitMinute, plugins.Environment(house)?.Effect.OutdoorSuit);
                outfits.Tick(world, cachedOutdoorSuit.Suit);
            }
            if (world.Clock.Hour != previousHour) world.DispatchNotice(0xdb, world.Clock.Hour);
            if (allowAutoSpeed) UpdateAutoSpeed(world);
            if (traceActions) LogActionState();
            LogNewThreadErrors();
            LogNewInteractionGuardErrors();
            if (!hadBlockingDialog && HasBlockingDialog(world))
            {
                dialogResumeSpeed = speed;
                SetSimulationSpeed(0, blockingDialog: true);
                Export();
            }
            if (ConnectedNeighborhoodEnabled) TickConnectedNeighborhoodSessions();
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            simulationError = $"{error.GetType().Name}: {error.Message}";
            SetSimulationSpeed(0);
            Console.Error.WriteLine($"Native simulation paused: {simulationError}");
            Console.Error.WriteLine(error);
            if (error.Data["tickingObject"] is { } tickingObject)
                Console.Error.WriteLine($"Native simulation error object: {tickingObject}");
            if (error.Data["executionLocation"] is { } executionLocation)
                Console.Error.WriteLine($"Native simulation error location: {executionLocation}");
            Export();
            return false;
        }
    }

    private void LogActionState()
    {
        var person = world?.ObjectById(selectedSim) as XPerson;
        if (person is null) return;
        if (!loggedSimRoster)
        {
            loggedSimRoster = true;
            foreach (var sim in world!.WalkOrder.OfType<XPerson>())
                Console.Error.WriteLine($"Native Sim roster: id={sim.Id} family={sim[PersonData.FamilyNumber]} activeFamily={activeFamilyChunkId} " +
                    $"age={sim[PersonData.PersonAge]} type={sim[PersonData.PersonType]} selected={sim.Id == selectedSim} position=({sim.Position.X},{sim.Position.Y},{sim.Position.Height})");
        }
        while (loggedQueuedInteractionStarts < world!.QueuedInteractionStartTrace.Count)
        {
            var start = world.QueuedInteractionStartTrace[loggedQueuedInteractionStarts++];
            Console.Error.WriteLine($"Native queued interaction start: sim={start.PersonId} target={start.TargetId} tree={start.ActionTree} " +
                $"scope={start.TargetScopeFound} guard={start.InteractionPassed}:{start.GuardTree} pushed={start.ActionTreePushed} " +
                $"selection={start.PersonSelectionWord} age={start.PersonAge} gender={start.PersonGender} stateClass={start.StateClass} targetFlags={start.TargetFlags:X4}");
        }
        var primitiveResults = world.PrimitiveResultTrace.Skip(loggedPrimitiveResults)
            .Where(entry => entry.ObjectId == selectedSim).ToArray();
        loggedPrimitiveResults = world.PrimitiveResultTrace.Count;
        var executionTraces = world.ExecutionTrace.Skip(loggedExecutionTraces)
            .Where(entry => entry.ObjectId == selectedSim && entry.Opcode == 0x1b).ToArray();
        loggedExecutionTraces = world.ExecutionTrace.Count;
        foreach (var trace in executionTraces)
            Console.Error.WriteLine($"Native relative-route operand: sim={trace.ObjectId} tree={trace.Tree} instruction={trace.Instruction} " +
                $"operand={trace.Operand} stack={trace.StackObject} parameters=[{string.Join(',', trace.Parameters)}] locals=[{string.Join(',', trace.Locals)}]");
        var rink = world.Objects.Values.FirstOrDefault(item =>
            item.TypeRecord?.Interactions?.Interactions.Any(interaction => interaction.Name == "Skate") == true);
        if (rink is not null)
        {
            var rinkIds = world.Objects.Values.Where(item => item.Id == rink.Id || item.MultiTileMasterId == rink.Id)
                .Select(item => item.Id).ToHashSet();
            var initTraces = world.ExecutionTrace.Skip(loggedRinkInitTraces).Where(entry => rinkIds.Contains(entry.ObjectId) &&
                entry.Tree is 4119 or 4124 or 4125 or 4101 or 4105).ToArray();
            loggedRinkInitTraces = world.ExecutionTrace.Count;
            foreach (var trace in initTraces)
            {
                var type = world.ObjectById(trace.ObjectId)?.TypeRecord;
                Console.Error.WriteLine($"Native rink init trace: object={trace.ObjectId} subindex={type?.SubIndex} " +
                    $"tree={trace.Tree} instruction={trace.Instruction} opcode={trace.Opcode:X2} operand={trace.Operand} " +
                    $"parameters=[{string.Join(',', trace.Parameters)}] locals=[{string.Join(',', trace.Locals)}]");
            }
        }
        foreach (var result in primitiveResults)
            Console.Error.WriteLine($"Native primitive trace: sim={result.ObjectId} tree={result.Tree} instruction={result.Instruction} " +
                $"opcode={result.Opcode:X2} result={result.Result} parameters=[{string.Join(',', result.Parameters)}]");
        while (loggedNativeRoutes < world.NativeRouteTrace.Count)
        {
            var route = world.NativeRouteTrace[loggedNativeRoutes++];
            Console.Error.WriteLine($"Native route trace: actor={route.ActorId} anchor={route.AnchorId} destination={route.Destination} phase={route.Phase} failure={route.Failure}");
        }
        while (loggedRouteSearches < world.RouteSearchTrace.Count)
        {
            var route = world.RouteSearchTrace[loggedRouteSearches++];
            Console.Error.WriteLine($"Native route search: actor={route.ActorId} destination={route.Destination} start=({route.StartTile.X},{route.StartTile.Y}) " +
                $"end=({route.EndTile.X},{route.EndTile.Y}) rooms={route.StartRoom}->{route.EndRoom} visited={route.VisitedTiles} " +
                $"rejected={route.RejectedPlacements}/{route.RejectedEdges} visitedRooms={route.VisitedRooms}");
        }
        while (loggedRouteCandidates < world.RouteCandidateTrace.Count)
        {
            var candidate = world.RouteCandidateTrace[loggedRouteCandidates++];
            if (candidate.ActorId != selectedSim) continue;
            var blocker = candidate.BlockingObjectId is { } blockerId ? world.ObjectById(blockerId) : null;
            var blockerGuid = blocker is null ? 0u : (uint)(ushort)blocker.Definition[(int)ObjdField.Guid1] |
                ((uint)(ushort)blocker.Definition[(int)ObjdField.Guid2] << 16);
            var anchor = world.ObjectById(candidate.AnchorId);
            var anchorParts = anchor is null ? Array.Empty<XObject>() : world.Objects.Values
                .Where(item => item.Id == anchor.Id || item.MultiTileMasterId == anchor.Id).ToArray();
            Console.Error.WriteLine($"Native route candidate: actor={candidate.ActorId} anchor={candidate.AnchorId} position={candidate.Position} " +
                $"room={candidate.Room} direction={candidate.Direction} accepted={candidate.PlacementAccepted} " +
                $"failure={candidate.PlacementFailureCode} blocker={candidate.BlockingObjectId} " +
                $"blockerGuid=0x{blockerGuid:X8} blockerName={blocker?.TypeRecord?.Name ?? "<missing>"} " +
                $"blockerSubIndex={blocker?.TypeRecord?.SubIndex} blockerMaster={blocker?.MultiTileMasterId} " +
                $"blockerNext={blocker?.MultiTileNextId} blockerPosition={blocker?.Position} " +
                $"blockerDataFlags=0x{(ushort)(blocker?[ObjectData.Flags] ?? 0):X4} " +
                $"blockerAllowedHeight=0x{(ushort)(blocker?[ObjectData.AllowedHeightFlags] ?? 0):X4} " +
                $"blockerPlacementFlags=0x{(ushort)(blocker?[ObjectData.PlacementFlags] ?? 0):X4} " +
                $"blockerFunctions=[{string.Join(',', blocker?.TypeRecord?.FunctionTable?.Take(2) ?? [])}] " +
                $"anchorFlags=0x{(ushort)(anchor?[ObjectData.Flags] ?? 0):X4} " +
                $"anchorAllowedHeight=0x{(ushort)(anchor?[ObjectData.AllowedHeightFlags] ?? 0):X4} " +
                $"anchorPlacementFlags=0x{(ushort)(anchor?[ObjectData.PlacementFlags] ?? 0):X4} " +
                $"anchorPartCount={anchorParts.Length} reason={candidate.FailureReason}");
        }
        var queued = person.ActionQueueEnd - person.ActionQueueCurrent;
        var queue = queued is >= 0 and <= 8
            ? string.Join(",", Enumerable.Range(0, queued).Select(index =>
            {
                var action = person.ActionQueue[(person.ActionQueueCurrent + index) & 7];
                return $"{action.Owner?.Id ?? 0}:{action.ActionTree}:{action.Flags:X2}";
            }))
            : $"invalid({queued})";
        var stack = world!.SavedExecutionStacks.GetValueOrDefault(person.Id);
        var frames = stack is null ? "none" : string.Join("/", stack.Frames.TakeLast(5).Select(frame =>
            $"{frame.TreeId}:{frame.Instruction}:{frame.WaitState}:p[{string.Join(',', frame.Parameters)}]:l[{string.Join(',', frame.Locals)}]"));
        var active = person.CurrentActionRecord is { Owner: not null } current
            ? $"{current.Owner.Id}:{current.ActionTree}:{current.Flags:X2}" : "none";
        var target = world.ObjectById(110);
        var tracedRoute = person.NativeRoute;
        var playback = person.AnimationPlayback;
        Console.Error.WriteLine($"Native action trace: tick={updates} sim={person.Id} pos=({person.Position.X},{person.Position.Y},{person.Position.Height}) " +
            $"root=({person.AnimatorRootX:R},{person.AnimatorRootY:R}) direction={person.AnimatorRootDirection:R} " +
            $"control={person.AnimatorControlState} follow={tracedRoute?.AnimatorFollowState} skill={tracedRoute?.MotionAnimationIndex} " +
            $"routeFrame={tracedRoute?.MotionPracticeFrame:R} practice={playback?.Clip.Name} practiceFrame={playback?.Frame:R} " +
            $"weight={playback?.PracticeWeight:R} rootOnly={world.AvatarAnimationRootOnly} " +
            $"priority={person[PersonData.Priority]} nonInterruptible={person[PersonData.NonInterruptible]} stateClass={world.StateClass(person.StateValue)} inputFlags={person.InputFlags:X2} bladder={person[Motive.Bladder]:F2} active={active} queue=[{queue}] pending={person.PendingActionRequests.Count} toiletUseCount={(target is null ? "missing" : target[ObjectData.UseCount].ToString())} stack={frames}");
    }

    private void LogNewThreadErrors()
    {
        if (world is null) return;
        while (loggedThreadErrors < world.ThreadErrors.Count)
        {
            var error = world.ThreadErrors[loggedThreadErrors++];
            Console.Error.WriteLine($"Native SimAntics thread error: object {error.ObjectId} ({DescribeObject(error.ObjectId)}): {error.Message}");
            if (error.Frames is null) continue;
            foreach (var frame in error.Frames)
                Console.Error.WriteLine($"  BHAV {frame.Tree} instruction {frame.Instruction}, opcode {frame.Opcode?.ToString("X2") ?? "none"}, stack object {frame.StackObject} ({DescribeObject(frame.StackObject)}), code owner {frame.CodeOwner}, parameters [{string.Join(",", frame.Parameters)}], locals [{string.Join(",", frame.Locals)}]");
        }
    }

    private string DescribeObject(short id)
    {
        if (world?.ObjectById(id) is not { } item) return "none";
        var guid = unchecked((uint)(item.TypeRecord?.Key ?? 0));
        var name = registry?.Find(guid)?.CatalogString(0, language) ?? Path.GetFileName(registry?.Find(guid)?.Source ?? "");
        return $"GUID 0x{guid:X8} {name}";
    }

    private void LogNewInteractionGuardErrors()
    {
        if (world is null) return;
        while (loggedInteractionGuardErrors < world.InteractionGuardErrors.Count)
        {
            var error = world.InteractionGuardErrors[loggedInteractionGuardErrors++];
            Console.Error.WriteLine($"Native interaction check rejected: person {error.PersonId}, object {error.ObjectId}, " +
                $"TTAB entry {error.InteractionId}, BHAV {error.GuardTree}: error {(short)error.Error} " + error.Message);
        }
    }

    private CatalogLotType CurrentCatalogLotType()
    {
        if (world is null || world[SimulationGlobal.SimlessBuildMode] == 0)
            return CatalogLotType.Residential;
        var userData = Path.GetDirectoryName(neighborhoodPath) ?? Neighborhoods.UserData(profile);
        return house switch
        {
            >= 21 and <= 31 => CatalogLotType.Downtown,
            >= 40 and <= 49 => CatalogLotType.Vacation,
            >= 81 and <= 89 => CatalogLotType.StudioTown,
            >= 93 and <= 99 => CatalogLotType.MagicTown,
            _ when house > 0 && Neighborhoods.IsCommunityLot(userData, house) => CatalogLotType.Community,
            _ => CatalogLotType.Residential,
        };
    }

    private XObject BuyObject(JsonElement request, out int catalogPrice)
    {
        var current = world ?? throw new InvalidOperationException("There is no loaded native lot.");
        if (speed != 0) throw new InvalidOperationException("Pause the simulation before editing objects");
        if (current.HasVisibleBlockingDialog) throw new InvalidOperationException("Answer the active game dialog before editing objects");
        if (current.FamilyBudget is not FamilyBudget budget || (budget.ActiveFamily is null && current[SimulationGlobal.SimlessBuildMode] == 0))
            throw new InvalidOperationException("Object editing requires an active household");

        var guid = request.TryGetProperty("guid", out var guidValue) && guidValue.TryGetUInt32(out var parsedGuid)
            ? parsedGuid : throw new ArgumentException("guid must be an unsigned 32-bit object GUID");
        var entry = registry?.Find(guid) ?? throw new ArgumentException("Object is not in the loaded catalog");
        if (!ObjectCatalog.TryGetEntry(entry, language, CurrentCatalogLotType(), out var catalogItem) || catalogItem is null)
            throw new ArgumentException("Object is not a supported catalog item");

        var price = catalogItem.Price;
        catalogPrice = price;
        var isFireplace = entry.Definition[ObjdField.RoomSort] == 0 && entry.Definition[ObjdField.BuildModeType] == 5;
        if (!isFireplace && price > budget.ReadFamilyBudget()) throw new InvalidOperationException("Insufficient family funds");
        var x = Coordinate(request, "x");
        var y = Coordinate(request, "y");
        var level = request.TryGetProperty("level", out var levelValue) && levelValue.TryGetInt32(out var parsedLevel)
            ? parsedLevel : throw new ArgumentException("level must be an integer");
        var directionFlag = request.TryGetProperty("direction", out var directionValue) && directionValue.TryGetByte(out var parsedDirection)
            ? parsedDirection : throw new ArgumentException("direction must be 1, 4, 16 or 64");
        var direction = directionFlag switch
        {
            1 => 0,
            4 => 2,
            16 => 4,
            64 => 6,
            _ => throw new ArgumentException("direction must be 1, 4, 16 or 64"),
        };
        if (x < 0 || y < 0 || x >= LotWidth * 16 || y >= LotHeight * 16 || level < 1 || level > Stories)
            throw new ArgumentException("Object position is outside the lot");

        IReadOnlyList<XObject>? group = null;
        XObject? item = null;
        var destination = new ObjectPosition(x, y, level);
        IffFile? previousLotBeforeFireplace = null;
        var lotBeforeBuy = lot;
        using var savedTypes = current.BeginSavedObjectTypeRegistrationScope();
        try
        {
            group = current.ObjectGroupFactory?.Invoke(unchecked((int)guid));
            if (group is { Count: 0 }) throw new ArgumentException("Object group could not be created");
            if (group is { Count: > 0 }) item = group[0];
            else
            {
                if (entry.Definition[ObjdField.MasterId] != 0)
                    throw new ArgumentException("Multi-tile object group creation is unavailable");
                item = current.ObjectFactory?.Invoke(unchecked((int)guid))
                    ?? throw new ArgumentException("Object definition could not be loaded");
                // Initializes newly created candidate objects at off-lot coordinates (-16, -16) prior to
                // placement validation.
                item.Position = new ObjectPosition(-16, -16, level);
                current.Add(item);
                current.InitializeCreatedObject(item);
            }
            var members = group ?? [item];
            current.FaceCreatedObject(item, members, (short)direction);
            var buildMode = ObjectBuildModeType(item);
            if (!current.CanPlaceObjectFromEditor(item, destination, out var referenceObject, out var referenceSlot))
            {
                if (buildMode == 5 && lot is { } currentLot &&
                    TryPrepareFireplaceFloors(current, currentLot, item, members, destination, out var updatedLot, out var netFloorCost))
                {
                    if (price + netFloorCost > budget.ReadFamilyBudget())
                        throw new InvalidOperationException("Insufficient family funds");
                    previousLotBeforeFireplace = currentLot;
                    RefreshWorldRoomMaps(current, updatedLot);
                    if (!current.CanPlaceObjectFromEditor(item, destination, out referenceObject, out referenceSlot))
                        throw new InvalidOperationException("Object cannot be placed at that position");
                    lot = updatedLot;
                    price += netFloorCost;
                    previousLotBeforeFireplace = null;
                }
                else
                {
                    if (price > budget.ReadFamilyBudget())
                        throw new InvalidOperationException("Insufficient family funds");
                    throw new InvalidOperationException("Object cannot be placed at that position");
                }
            }
            else if (price > budget.ReadFamilyBudget())
            {
                throw new InvalidOperationException("Insufficient family funds");
            }
            current.PlaceCreatedObjectFromEditor(item, destination, referenceObject, referenceSlot);
            ApplyPortalWallEdits(CapturePortalWallEdits(current, item, resetToDefault: false));
            budget.AlterFamilyBudget(buildMode == 0 ? 6 : 7, price);
            savedTypes.Commit();
            lotValuationEdited = true;
            FlushPendingLotUpdates(current);
            if (buildMode == 0)
            {
                ClearBuildHistory();
            }
            else
            {
                var lead = MultiTileMembers(current, item).FirstOrDefault() ?? item;
                var token = new BuildObjectToken(lead.Id);
                liveTokens[lead.Id] = token;
                undoStack.Push(new BuildBuyEdit(token, guid,
                    destination, directionFlag, price, lotBeforeBuy, lot));
                redoStack.Clear();
            }
            return item;
        }
        catch
        {
            if (previousLotBeforeFireplace is not null)
            {
                lot = previousLotBeforeFireplace;
                RefreshWorldRoomMaps(current, previousLotBeforeFireplace);
            }
            if (group is { Count: > 0 }) current.RemoveCreatedObjectGroup(group[0]);
            else if (item is not null) current.Remove(item.Id);
            throw;
        }
    }

    /// <summary>Sells a placed object with the Hand tool, crediting its depreciated value to the household
    /// after verifying pick-up and sale eligibility on a paused lot.</summary>
    private object SellObject(JsonElement request)
    {
        var current = world ?? throw new InvalidOperationException("There is no loaded native lot.");
        if (speed != 0) throw new InvalidOperationException("Pause the simulation before editing objects");
        if (current.HasVisibleBlockingDialog) throw new InvalidOperationException("Answer the active game dialog before editing objects");
        if (current.FamilyBudget is not FamilyBudget budget || (budget.ActiveFamily is null && current[SimulationGlobal.SimlessBuildMode] == 0))
            throw new InvalidOperationException("Object editing requires an active household");
        var id = request.TryGetProperty("objectID", out var idValue) && idValue.TryGetInt16(out var parsedId)
            ? parsedId : throw new ArgumentException("objectID must be a 16-bit object id");
        var item = current.ObjectById(id);
        if (item is null || (item is XPerson && !current.CanSellObject(item))) throw new ArgumentException("Object cannot be sold");
        if (!current.CanSellObject(item)) throw new InvalidOperationException("Object cannot be sold");
        var soldMembers = MultiTileMembers(current, item);
        var lead = soldMembers.FirstOrDefault() ?? item;
        var spawnGuid = ResolveCatalogSpawnGuid(lead);
        var leadPosition = lead.Position;
        var leadDirectionFlag = (byte)(1 << (lead[ObjectData.Direction] & 6));
        var savedCurrentValue = lead[ObjectData.CurrentValue];
        var lotBeforeSell = lot;
        var guid = unchecked((uint)(item.TypeRecord?.Key ?? 0));
        var buildMode = ObjectBuildModeType(item);
        var portalEdits = CapturePortalWallEdits(current, item, resetToDefault: true);
        var baseRefund = current.SellObject(item);
        foreach (var member in soldMembers) audio?.RemoveOwner(member.Id);
        audio?.RemoveOwner(item.Id);
        ApplyPortalWallEdits(portalEdits);
        var refund = SimulationWorld.HandResaleValue(baseRefund, buildMode);
        budget.AlterFamilyBudget(buildMode == 0 ? 6 : 7, -refund);
        lotValuationEdited = true;
        FlushPendingLotUpdates(current);
        if (buildMode == 0)
        {
            ClearBuildHistory();
        }
        else
        {
            if (!liveTokens.Remove(lead.Id, out var token))
                token = new BuildObjectToken(lead.Id);
            undoStack.Push(new BuildSellEdit(token, spawnGuid, leadPosition, leadDirectionFlag,
                savedCurrentValue, -refund, lotBeforeSell, lot));
            redoStack.Clear();
        }
        return new { sold = true, objectID = (int)id, guid, refund, budget = budget.ReadFamilyBudget(), canUndo = undoStack.Count > 0, canRedo = redoStack.Count > 0 };
    }

    private uint ResolveCatalogSpawnGuid(XObject lead)
    {
        var tileGuid = unchecked((uint)(lead.TypeRecord?.Key ?? 0));
        if (registry is not null &&
            registry.ByGuid.TryGetValue(tileGuid, out var entry) &&
            entry.Definition[ObjdField.MasterId] is var masterId and not 0 &&
            entry.Definition[ObjdField.SubIndex] != ushort.MaxValue)
        {
            var root = registry.Entries.FirstOrDefault(candidate =>
                ReferenceEquals(candidate.File, entry.File) &&
                candidate.Definition[ObjdField.MasterId] == masterId &&
                candidate.Definition[ObjdField.SubIndex] == ushort.MaxValue);
            if (root is not null) return root.Guid;
        }
        return tileGuid;
    }

    private static List<XObject> MultiTileMembers(SimulationWorld current, XObject item)
    {
        var lead = item.MultiTileMasterId == 0 ? item : current.ObjectById(item.MultiTileMasterId) ?? item;
        var members = new List<XObject>();
        var visited = new HashSet<short>();
        for (var part = lead; part is not null && visited.Add(part.Id);)
        {
            members.Add(part);
            part = part.MultiTileNextId == 0 ? null : current.ObjectById(part.MultiTileNextId);
        }
        return members;
    }

    /// <summary>Captures cardinal wall edges and target styles for portal objects (using the OBJD WallStyle
    /// when placed, or default style 1 when removed) to keep the Arry 2 and 102 wall layers synchronized.
    /// </summary>
    private static List<(int Level, int OwnerX, int OwnerY, byte Bit, byte Style)> CapturePortalWallEdits(
        SimulationWorld current, XObject item, bool resetToDefault)
    {
        var edits = new List<(int Level, int OwnerX, int OwnerY, byte Bit, byte Style)>();
        if (item.RuntimeClass != ObjectClass.Portal) return edits;
        foreach (var member in MultiTileMembers(current, item))
        {
            if (member.Definition is not { Length: > (int)ObjdField.WallStyle }) continue;
            var sourceWallStyle = member.Definition[(int)ObjdField.WallStyle];
            if (sourceWallStyle == 0) continue;
            var localMask = member[ObjectData.WallPlacementFlags] & 5;
            var wallBit = localMask switch { 1 => 2u, 4 => 8u, _ => 0u };
            if (wallBit == 0) continue;
            wallBit = SimulationWorld.RotatePlacementWallBit(wallBit, (member[ObjectData.Direction] & 7) / 2);
            var tileX = member.Position.X >> 4;
            var tileY = member.Position.Y >> 4;
            var level = member.Position.Height;
            if (level is < 1 or > 2 || tileX <= 0 || tileY <= 0 || tileX >= LotWalls.Size - 1 || tileY >= LotWalls.Size - 1)
                continue;
            var (ownerX, ownerY, bit) = wallBit switch
            {
                1u => (tileX, tileY, (byte)1),
                2u => (tileX, tileY, (byte)2),
                4u => (tileX + 1, tileY, (byte)1),
                8u => (tileX, tileY + 1, (byte)2),
                _ => (-1, -1, (byte)0),
            };
            if (bit == 0 || ownerX < 0 || ownerY < 0 || ownerX >= LotWalls.Size || ownerY >= LotWalls.Size) continue;
            var style = resetToDefault ? (byte)1 : unchecked((byte)sourceWallStyle);
            edits.Add((level, ownerX, ownerY, bit, style));
        }
        return edits;
    }

    private void ApplyPortalWallEdits(IReadOnlyList<(int Level, int OwnerX, int OwnerY, byte Bit, byte Style)> edits)
    {
        if (edits.Count == 0 || lot is not { } currentLot) return;
        currentLot = ApplyPortalWallEditsToLot(currentLot, edits, out var anyChanged);
        lot = currentLot;
        if (anyChanged && world is { } activeWorld) RefreshWorldRoomMaps(activeWorld, currentLot);
    }

    private static IffFile ApplyPortalWallEditsToLot(
        IffFile currentLot,
        IEnumerable<(int Level, int OwnerX, int OwnerY, byte Bit, byte Style)> edits,
        out bool anyChanged)
    {
        anyChanged = false;
        foreach (var group in edits.GroupBy(edit => edit.Level))
        {
            var chunkId = (ushort)(group.Key == 1 ? 2 : 102);
            if (currentLot.Find(ResourceType.Parse("Arry"), chunkId) is null) continue;
            var walls = LotWalls.Read(currentLot, group.Key);
            var changed = false;
            foreach (var (_, ownerX, ownerY, bit, style) in group)
            {
                var cell = walls[ownerX, ownerY];
                if ((cell[0] & bit) == 0) continue;
                var styleIndex = bit == 1 ? 2 : 3;
                if (cell[styleIndex] == style) continue;
                cell[styleIndex] = style;
                changed = true;
            }
            if (changed)
            {
                currentLot = walls.WriteTo(currentLot);
                anyChanged = true;
            }
        }
        return currentLot;
    }

    private sealed class UnconstrainedFamilyBudget : IFamilyBudget
    {
        public int ReadFamilyBudget() => int.MaxValue / 2;
        public void AlterFamilyBudget(int expenseType, int amount) { }
    }

    /// <summary>Prepares floors for multi-story fireplaces by removing blocking upper-level floor tiles for
    /// chimney pieces lacking PlacementFlags bit 0 (0x0001) and inserting floor pattern 1 under upper-level
    /// hearth pieces lacking bit 9 (0x0200).</summary>
    private bool TryPrepareFireplaceFloors(SimulationWorld current, IffFile currentLot, XObject lead,
        IReadOnlyList<XObject> members, ObjectPosition destination, out IffFile updatedLot, out int netFloorCost)
    {
        updatedLot = currentLot;
        netFloorCost = 0;

        var catalog = floorCatalog ??= FloorCatalog.Open(game, language);
        var terrain = LotTerrain.Read(currentLot);
        var pool = LotPool.Read(currentLot);
        var lotTiles = LotTiles.Read(currentLot);
        var water = LotWater.Read(currentLot);
        var floorsByLevel = new Dictionary<int, LotFloors>
        {
            [1] = LotFloors.Read(currentLot, 1),
            [2] = LotFloors.Read(currentLot, 2),
        };
        var wallsByLevel = new Dictionary<int, LotWalls>
        {
            [1] = LotWalls.Read(currentLot, 1),
            [2] = LotWalls.Read(currentLot, 2),
        };
        var tempBudget = new UnconstrainedFamilyBudget();
        var toolsByLevel = new Dictionary<int, FloorTool>
        {
            [1] = new FloorTool(current, floorsByLevel[1], catalog, tempBudget, terrain, pool, lotTiles, wallsByLevel[1], water),
            [2] = new FloorTool(current, floorsByLevel[2], catalog, tempBudget, terrain, pool, lotTiles, wallsByLevel[2], water),
        };

        var rootState = lead.SavedMultiTileState;
        var rootOffsetX = (members.Count > 1 ? rootState?.Value0x1A0 ?? 0 : 0) << 4;
        var rootOffsetY = (members.Count > 1 ? rootState?.Value0x1A4 ?? 0 : 0) << 4;
        var rootOffsetLevel = members.Count > 1 ? rootState?.Value0x1A8 ?? 0 : 0;
        var anyChanged = false;

        foreach (var member in members)
        {
            var state = member.SavedMultiTileState;
            var memberX = (destination.X - rootOffsetX + ((state?.Value0x1A0 ?? 0) << 4)) >> 4;
            var memberY = (destination.Y - rootOffsetY + ((state?.Value0x1A4 ?? 0) << 4)) >> 4;
            var memberLevel = destination.Height - rootOffsetLevel + (state?.Value0x1A8 ?? 0);
            if (memberX < 1 || memberY < 1 || memberX >= LotFloors.Size - 1 || memberY >= LotFloors.Size - 1 || memberLevel < 1)
                return false;

            var placementFlags = unchecked((ushort)member[ObjectData.PlacementFlags]);
            if (memberLevel <= 2)
            {
                var floors = floorsByLevel[memberLevel];
                var floorTool = toolsByLevel[memberLevel];
                if ((placementFlags & 1) == 0 && floors[memberX, memberY] != 0)
                {
                    if (!floorTool.Tile(memberX, memberY, 0, out var erased) || !erased)
                        return false;
                    anyChanged = true;
                }
                if (memberLevel > 1 && (placementFlags & 0x200) == 0 && floors[memberX, memberY] == 0)
                {
                    if (!floorTool.Tile(memberX, memberY, 1, out var added) || !added)
                        return false;
                    anyChanged = true;
                }
            }
            else if ((placementFlags & 0x200) == 0)
            {
                return false;
            }
        }

        if (!anyChanged) return false;
        netFloorCost = toolsByLevel[1].Pending + toolsByLevel[2].Pending;
        updatedLot = pool.WriteTo(
            wallsByLevel[2].WriteTo(
                floorsByLevel[2].WriteTo(
                    wallsByLevel[1].WriteTo(
                        floorsByLevel[1].WriteTo(currentLot)))));
        return true;
    }

    private int ObjectBuildModeType(XObject item)
    {
        if (item.Definition is { Length: > (int)ObjdField.RoomSort } && item.Definition[(int)ObjdField.RoomSort] != 0)
            return 0;
        if (item.Definition is { Length: > (int)ObjdField.BuildModeType } &&
            item.Definition[(int)ObjdField.BuildModeType] is >= 1 and <= 7 and var direct)
            return direct;
        if (item.TypeRecord?.AutonomyAlternativeTypeFlag is >= 1 and <= 7 and var altFlag)
            return altFlag;
        var guid = unchecked((uint)(item.TypeRecord?.Key ?? 0));
        if (registry?.Find(guid) is not { } objectEntry) return 0;
        if (objectEntry.Definition[ObjdField.RoomSort] != 0) return 0;
        var buildMode = (int)objectEntry.Definition[ObjdField.BuildModeType];
        if (buildMode is >= 1 and <= 7) return buildMode;
        var master = objectEntry.Definition[ObjdField.MasterId];
        if (master != 0 && registry.FindAlternativeType(objectEntry) is { } alt && alt.Definition[ObjdField.RoomSort] == 0)
            buildMode = alt.Definition[ObjdField.BuildModeType];
        return buildMode is >= 1 and <= 7 ? buildMode : 0;
    }

    private ObjectEntry? ResolveCatalogEntry(SimulationWorld current, XObject item)
    {
        if (registry is null) return null;
        var lead = MultiTileMembers(current, item).FirstOrDefault() ?? item;
        var guid = unchecked((uint)(lead.TypeRecord?.Key ?? item.TypeRecord?.Key ?? 0));
        if (registry.Find(guid) is not { } entry) return null;
        return registry.FindAlternativeType(entry) ?? entry;
    }

    private string ObjectCatalogName(SimulationWorld current, XObject item)
    {
        if (ResolveCatalogEntry(current, item) is { } masterEntry &&
            masterEntry.CatalogString(0, language) is { Length: > 0 } catalogTitle)
            return catalogTitle;
        var lead = MultiTileMembers(current, item).FirstOrDefault() ?? item;
        if (lead.TypeRecord?.CatalogName is { Length: > 0 } leadName) return leadName;
        if (item.TypeRecord?.CatalogName is { Length: > 0 } itemName) return itemName;
        return "";
    }

    private object? ObjectDisposePreview(SimulationWorld current, XObject item)
    {
        if (ResolveCatalogEntry(current, item) is { } masterEntry &&
            ObjectCatalog.CatalogPreview(masterEntry, SceneDirectory) is { } preview)
            return preview;
        if (registry is not null)
        {
            var guid = unchecked((uint)(item.TypeRecord?.Key ?? 0));
            if (registry.Find(guid) is { } entry &&
                ObjectCatalog.CatalogPreview(entry, SceneDirectory) is { } fallbackPreview)
                return fallbackPreview;
        }
        return images?.ItemIcon(unchecked((uint)(item.TypeRecord?.Key ?? 0)));
    }

    private void FlushPendingLotUpdates(SimulationWorld current)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            current.AdvanceSimulationTick(new SimulationClockControl(0, 0, 0), 0, current.Clock.Ticks);
            LogNewThreadErrors();
        }
    }

    private IReadOnlyDictionary<int, RoomMap> ReadRoomMaps(IffFile currentLot)
    {
        if (world is { } current)
        {
            current.RoomManagerRecordsLoaded = false;
            current.LotRoomMaps.Clear();
            current.LotRoomLightMaps.Clear();
            current.PersonRoomMotiveEntries.Clear();
            var simVersion = Math.Max(current.SavedSimulationState?.Version ?? 0, 0x35);
            current.PlacementEligibilityGrids[1] = SavedLotObjectLoader.ReadPlacementEligibilityGrid(currentLot, simVersion);
        }
        return SavedLotObjectLoader.ReadRoomMaps(currentLot, world?.SavedSimulationState?.Version ?? 0);
    }

    private void RefreshWorldRoomMaps(SimulationWorld current, IffFile currentLot)
    {
        current.LotTerrain = currentLot.Find(ResourceType.Parse("Arry"), 0) is not null
            ? LotTerrain.Read(currentLot)
            : null;
        current.LoadRoomMaps(ReadRoomMaps(currentLot));
        current.RefreshSavedPortalWallStyles();
    }

    private FloorCatalog? floorCatalog;
    private WallCatalog? wallCatalogCache;
    private WallpaperCatalog? wallpaperCatalogCache;

    /// <summary>Executes floor rectangle painting or erasing between opposite corners on a level,
    /// normalizing signed deltas to support reverse-direction dragging.</summary>
    private object Architecture(JsonElement request) => ArchitectureCommand(request);

    /// <summary>Tracks committed build tool operations and financial charges against expense category 7.
    /// Undoing an action is refused if the refund reversal exceeds available household funds, and loading a
    /// lot or committing Buy-mode edits clears history.</summary>
    internal interface IBuildHistoryEntry
    {
        int Charge { get; }
    }

    private sealed class BuildObjectToken(short currentId)
    {
        public short CurrentId { get; set; } = currentId;
    }

    private sealed record DemolishedOpeningRecord(BuildObjectToken Token, uint Guid, ObjectPosition Position,
        byte DirectionFlag, short SavedCurrentValue,
        IReadOnlyList<(int Level, int OwnerX, int OwnerY, byte Bit, byte Style)> ResetWallEdits);

    private sealed record ArchitectureEdit(IffFile Before, IffFile After, int Charge, string? StrokeId = null,
        IReadOnlyList<DemolishedOpeningRecord>? DemolishedOpenings = null) : IBuildHistoryEntry;
    private sealed record BuildBuyEdit(BuildObjectToken Token, uint Guid, ObjectPosition Position, byte DirectionFlag,
        int Charge, IffFile? LotBefore, IffFile? LotAfter) : IBuildHistoryEntry;
    private sealed record BuildMoveEdit(BuildObjectToken Token, ObjectPosition OldPosition, byte OldDirectionFlag,
        ObjectPosition NewPosition, byte NewDirectionFlag, IffFile? LotBefore, IffFile? LotAfter) : IBuildHistoryEntry
    {
        public int Charge => 0;
    }
    private sealed record BuildSellEdit(BuildObjectToken Token, uint Guid, ObjectPosition Position, byte DirectionFlag,
        short SavedCurrentValue, int Charge, IffFile? LotBefore, IffFile? LotAfter) : IBuildHistoryEntry;

    private readonly Stack<IBuildHistoryEntry> undoStack = new();
    private readonly Stack<IBuildHistoryEntry> redoStack = new();
    private readonly Dictionary<short, BuildObjectToken> liveTokens = new();

    private void ClearBuildHistory()
    {
        undoStack.Clear();
        redoStack.Clear();
        liveTokens.Clear();
    }

    private XObject CreateAndPlaceBuildObject(SimulationWorld current, uint guid, ObjectPosition destination,
        byte directionFlag, IffFile? lotWithFloors)
    {
        var direction = directionFlag switch
        {
            1 => 0,
            4 => 2,
            16 => 4,
            64 => 6,
            _ => throw new ArgumentException("direction must be 1, 4, 16 or 64"),
        };
        IReadOnlyList<XObject>? group = null;
        XObject? item = null;
        var previousLot = lot;
        using var savedTypes = current.BeginSavedObjectTypeRegistrationScope();
        try
        {
            group = current.ObjectGroupFactory?.Invoke(unchecked((int)guid));
            if (group is { Count: 0 }) throw new ArgumentException("Object group could not be created");
            if (group is { Count: > 0 }) item = group[0];
            else
            {
                item = current.ObjectFactory?.Invoke(unchecked((int)guid))
                    ?? throw new ArgumentException("Object definition could not be loaded");
                item.Position = new ObjectPosition(-16, -16, destination.Height);
                current.Add(item);
                current.InitializeCreatedObject(item);
            }
            var members = group ?? [item];
            current.FaceCreatedObject(item, members, (short)direction);
            // Validates placement against the existing lot first so portal wall categories remain intact
            // before cutouts are reapplied, only swapping in pre-modified floors when structural support is
            // required.
            if (!current.CanPlaceObjectFromEditor(item, destination, out var referenceObject, out var referenceSlot))
            {
                if (lotWithFloors is not null && !ReferenceEquals(lot, lotWithFloors))
                {
                    lot = lotWithFloors;
                    RefreshWorldRoomMaps(current, lotWithFloors);
                    if (!current.CanPlaceObjectFromEditor(item, destination, out referenceObject, out referenceSlot))
                        throw new InvalidOperationException("Object cannot be placed at that position");
                }
                else
                {
                    throw new InvalidOperationException("Object cannot be placed at that position");
                }
            }
            current.PlaceCreatedObjectFromEditor(item, destination, referenceObject, referenceSlot);
            ApplyPortalWallEdits(CapturePortalWallEdits(current, item, resetToDefault: false));
            savedTypes.Commit();
            return item;
        }
        catch
        {
            if (previousLot is not null && !ReferenceEquals(lot, previousLot))
            {
                lot = previousLot;
                RefreshWorldRoomMaps(current, previousLot);
            }
            if (group is { Count: > 0 }) current.RemoveCreatedObjectGroup(group[0]);
            else if (item is not null) current.Remove(item.Id);
            throw;
        }
    }

    /// <summary>Updates roof pattern style and pitch (clamped to 0..2) as a free undoable operation.</summary>
    private object RoofSettings(JsonElement request)
    {
        var current = world ?? throw new InvalidOperationException("There is no loaded native lot.");
        var currentLot = lot ?? throw new InvalidOperationException("There is no loaded native lot.");
        if (speed != 0) throw new InvalidOperationException("Pause the simulation before editing architecture");
        if (current.FamilyBudget is not FamilyBudget budget || (budget.ActiveFamily is null && current[SimulationGlobal.SimlessBuildMode] == 0))
            throw new InvalidOperationException("Architecture editing requires an active household");
        var roof = LotRoof.Read(currentLot);
        var styles = LotRoof.Styles(game);
        var changed = false;
        if (request.TryGetProperty("style", out var styleValue))
        {
            string? name = styleValue.ValueKind switch
            {
                JsonValueKind.Number when styleValue.TryGetInt32(out var index) && index >= 0 && index < styles.Count => styles[index],
                JsonValueKind.String => styles.FirstOrDefault(item => string.Equals(item, styleValue.GetString(), StringComparison.OrdinalIgnoreCase)),
                _ => null,
            };
            if (name is null) throw new ArgumentException("Roof style is not installed");
            changed |= roof.SetStyle(name);
        }
        if (request.TryGetProperty("pitch", out var pitchValue))
        {
            if (pitchValue.ValueKind != JsonValueKind.Number || !pitchValue.TryGetSingle(out var pitch) || !float.IsFinite(pitch))
                throw new ArgumentException("Roof pitch must be a finite number");
            changed |= roof.SetPitch(pitch);
        }
        if (changed)
        {
            var updated = roof.WriteTo(currentLot);
            undoStack.Push(new ArchitectureEdit(currentLot, updated, 0));
            redoStack.Clear();
            lot = updated;
            lotValuationEdited = true;
        }
        var styleIndex = styles.ToList().FindIndex(style => string.Equals(style, roof.Style, StringComparison.OrdinalIgnoreCase));
        return new { applied = true, changed, roofStyle = roof.Style, roofStyleIndex = styleIndex >= 0 ? styleIndex : 0, roofPitch = roof.Pitch, canUndo = undoStack.Count > 0, canRedo = redoStack.Count > 0 };
    }

    private object ArchitectureHistory(bool undo)
    {
        var current = world ?? throw new InvalidOperationException("There is no loaded native lot.");
        if (speed != 0) throw new InvalidOperationException("Pause the simulation before editing architecture");
        if (current.FamilyBudget is not FamilyBudget budget || (budget.ActiveFamily is null && current[SimulationGlobal.SimlessBuildMode] == 0))
            throw new InvalidOperationException("Architecture editing requires an active household");
        var source = undo ? undoStack : redoStack;
        if (source.Count == 0) return new { applied = false, command = undo ? "UNDO" : "REDO", canUndo = undoStack.Count > 0, canRedo = redoStack.Count > 0, budget = budget.ReadFamilyBudget() };
        var edit = source.Peek();
        var charge = undo ? -edit.Charge : edit.Charge;
        if (charge > 0 && charge > budget.ReadFamilyBudget())
            return new { applied = false, command = undo ? "UNDO" : "REDO", insufficientFunds = true, canUndo = undoStack.Count > 0, canRedo = redoStack.Count > 0, budget = budget.ReadFamilyBudget() };
        switch (edit)
        {
            case ArchitectureEdit arch:
                if (!undo && arch.DemolishedOpenings is { Count: > 0 } redoDemolish)
                {
                    foreach (var rec in redoDemolish)
                    {
                        if (current.ObjectById(rec.Token.CurrentId) is { } portal)
                        {
                            current.DemolishObject(portal, runCleanup: true);
                            liveTokens.Remove(rec.Token.CurrentId);
                        }
                    }
                }
                lot = undo ? arch.Before : arch.After;
                lotValuationEdited = true;
                RefreshWorldRoomMaps(current, lot);
                if (undo && arch.DemolishedOpenings is { Count: > 0 } undoRestore)
                {
                    foreach (var rec in undoRestore)
                    {
                        ApplyPortalWallEdits(rec.ResetWallEdits);
                        var created = CreateAndPlaceBuildObject(current, rec.Guid, rec.Position, rec.DirectionFlag, null);
                        var lead = MultiTileMembers(current, created).FirstOrDefault() ?? created;
                        lead[ObjectData.CurrentValue] = rec.SavedCurrentValue;
                        rec.Token.CurrentId = lead.Id;
                        liveTokens[lead.Id] = rec.Token;
                    }
                    lot = arch.Before;
                    RefreshWorldRoomMaps(current, lot);
                }
                if (arch.DemolishedOpenings is { Count: > 0 })
                    FlushPendingLotUpdates(current);
                if (charge != 0) budget.AlterFamilyBudget(7, charge);
                break;
            case BuildBuyEdit buy:
                if (undo)
                {
                    var item = current.ObjectById(buy.Token.CurrentId) ?? throw new InvalidOperationException("Build-mode object is no longer on the lot");
                    current.DemolishObject(item, runCleanup: true);
                    liveTokens.Remove(buy.Token.CurrentId);
                    if (buy.LotBefore is not null && !ReferenceEquals(lot, buy.LotBefore))
                    {
                        lot = buy.LotBefore;
                        RefreshWorldRoomMaps(current, lot);
                    }
                }
                else
                {
                    var created = CreateAndPlaceBuildObject(current, buy.Guid, buy.Position, buy.DirectionFlag, buy.LotAfter);
                    var lead = MultiTileMembers(current, created).FirstOrDefault() ?? created;
                    buy.Token.CurrentId = lead.Id;
                    liveTokens[lead.Id] = buy.Token;
                    if (buy.LotAfter is not null && !ReferenceEquals(lot, buy.LotAfter))
                    {
                        lot = buy.LotAfter;
                        RefreshWorldRoomMaps(current, lot);
                    }
                }
                lotValuationEdited = true;
                if (charge != 0) budget.AlterFamilyBudget(7, charge);
                FlushPendingLotUpdates(current);
                break;
            case BuildMoveEdit move:
            {
                var item = current.ObjectById(move.Token.CurrentId) ?? throw new InvalidOperationException("Build-mode object is no longer on the lot");
                var targetPos = undo ? move.OldPosition : move.NewPosition;
                var targetDir = undo ? move.OldDirectionFlag : move.NewDirectionFlag;
                var targetLot = undo ? move.LotBefore : move.LotAfter;
                if (!current.MoveObjectFromEditor(item, targetPos, targetDir, TreeRunner.CreateTransientStack(current, item)))
                    throw new InvalidOperationException("Object cannot be placed at that position");
                if (targetLot is not null && !ReferenceEquals(lot, targetLot))
                {
                    lot = targetLot;
                    RefreshWorldRoomMaps(current, lot);
                }
                lotValuationEdited = true;
                FlushPendingLotUpdates(current);
                break;
            }
            case BuildSellEdit sell:
                if (undo)
                {
                    var created = CreateAndPlaceBuildObject(current, sell.Guid, sell.Position, sell.DirectionFlag, sell.LotBefore);
                    var lead = MultiTileMembers(current, created).FirstOrDefault() ?? created;
                    lead[ObjectData.CurrentValue] = sell.SavedCurrentValue;
                    sell.Token.CurrentId = lead.Id;
                    liveTokens[lead.Id] = sell.Token;
                    if (sell.LotBefore is not null && !ReferenceEquals(lot, sell.LotBefore))
                    {
                        lot = sell.LotBefore;
                        RefreshWorldRoomMaps(current, lot);
                    }
                }
                else
                {
                    var item = current.ObjectById(sell.Token.CurrentId) ?? throw new InvalidOperationException("Build-mode object is no longer on the lot");
                    current.DemolishObject(item, runCleanup: true);
                    liveTokens.Remove(sell.Token.CurrentId);
                    if (sell.LotAfter is not null && !ReferenceEquals(lot, sell.LotAfter))
                    {
                        lot = sell.LotAfter;
                        RefreshWorldRoomMaps(current, lot);
                    }
                }
                lotValuationEdited = true;
                if (charge != 0) budget.AlterFamilyBudget(7, charge);
                FlushPendingLotUpdates(current);
                break;
        }
        source.Pop();
        (undo ? redoStack : undoStack).Push(edit);
        return new { applied = true, command = undo ? "UNDO" : "REDO", charge, canUndo = undoStack.Count > 0, canRedo = redoStack.Count > 0, budget = budget.ReadFamilyBudget() };
    }

    /// <summary>Applies atomic terrain height and grass state modifications as a single charged and
    /// undoable batch, rolling back all changes if unaffordable or invalid.</summary>
    private object ArchitectureBatch(JsonElement request)
    {
        var current = world ?? throw new InvalidOperationException("There is no loaded native lot.");
        var currentLot = lot ?? throw new InvalidOperationException("There is no loaded native lot.");
        if (speed != 0) throw new InvalidOperationException("Pause the simulation before editing architecture");
        if (current.HasVisibleBlockingDialog) throw new InvalidOperationException("Answer the active game dialog before editing architecture");
        if (current.FamilyBudget is not FamilyBudget budget || (budget.ActiveFamily is null && current[SimulationGlobal.SimlessBuildMode] == 0))
            throw new InvalidOperationException("Architecture editing requires an active household");
        if (!request.TryGetProperty("commands", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 4096)
            throw new ArgumentException("commands must be an array of at most 4096 entries");
        var catalog = floorCatalog ??= FloorCatalog.Open(game, language);
        var price = catalog.TerrainPrice();
        var terrain = LotTerrain.Read(currentLot);
        var grass = LotGrass.Read(currentLot);
        var lotTiles = LotTiles.Read(currentLot);
        var tool = new TerrainTool(current, terrain, budget, price, lotTiles, LotPool.Read(currentLot),
            [LotFloors.Read(currentLot, 1), LotFloors.Read(currentLot, 2)], [LotWalls.Read(currentLot, 1), LotWalls.Read(currentLot, 2)],
            LotWater.Read(currentLot));
        var changed = 0;
        var grassCost = 0;
        var insufficient = false;
        foreach (var item in list.EnumerateArray())
        {
            int Number(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
                ? parsed : throw new ArgumentException($"{name} must be an integer");
            var command = item.TryGetProperty("command", out var commandValue) ? commandValue.GetString() : null;
            switch (command)
            {
                case "TERRAIN_RAISE":
                {
                    var smooth = item.TryGetProperty("pattern", out var patEl) && patEl.ValueKind == JsonValueKind.Number && patEl.TryGetInt32(out var patVal) && patVal != 0;
                    if (smooth)
                    {
                        var tx = Number("x");
                        var ty = Number("y");
                        var targetHeight = Math.Clamp((short)Number("style"), (short)0, (short)TerrainTool.MaximumHeight);
                        var currentHeight = (uint)tx < LotWalls.Size && (uint)ty < LotWalls.Size ? terrain.Height(tx, ty) : (byte)0;
                        var stepDelta = Math.Sign(targetHeight - currentHeight);
                        if (stepDelta == 0) break;
                        changed += tool.SmoothRaise(tx, ty, stepDelta);
                    }
                    else
                    {
                        changed += tool.Raise(Number("x"), Number("y"), (short)Number("style"));
                    }
                    break;
                }
                case "TERRAIN_FLATTEN":
                    changed += tool.Level(Number("x"), Number("y"), Number("x") + Number("x2") + 1, Number("y") + Number("y2") + 1, (short)Number("style"));
                    break;
                case "GRASS_DOT":
                {
                    var x = Number("x");
                    var y = Number("y");
                    var delta = (short)Number("pattern");
                    if (!terrain.EditableTile(x, y)) throw new ArgumentException("Grass tile is outside the lot");
                    if (!lotTiles.InsideLot(x, y)) break;
                    var state = grass[x, y];
                    var next = Math.Clamp(state + delta, 0, 255);
                    var cost = (Math.Abs(next - state) + 63) / 64 * price;
                    if (next != state && tool.Pending + grassCost + cost <= budget.ReadFamilyBudget())
                    {
                        grass[x, y] = next;
                        grassCost += cost;
                        changed++;
                    }
                    else if (next != state) insufficient = true;
                    break;
                }
                default:
                    throw new NotSupportedException($"Native terrain command '{command}' is not implemented");
            }
            insufficient |= tool.InsufficientFunds;
        }
        var strokeId = request.TryGetProperty("strokeId", out var strokeValue) &&
            strokeValue.ValueKind == JsonValueKind.String &&
            strokeValue.GetString() is { Length: > 0 } parsedStroke
            ? parsedStroke : null;
        var charge = tool.Pending + grassCost;
        if (changed > 0)
        {
            tool.Commit();
            if (grassCost > 0) budget.AlterFamilyBudget(TerrainTool.ExpenseCategory, grassCost);
            var updated = grass.WriteTo(terrain.WriteTo(currentLot));
            if (strokeId is not null &&
                undoStack.TryPeek(out var topEntry) &&
                topEntry is ArchitectureEdit prev &&
                string.Equals(prev.StrokeId, strokeId, StringComparison.Ordinal))
            {
                undoStack.Pop();
                undoStack.Push(new ArchitectureEdit(prev.Before, updated, prev.Charge + charge, strokeId));
            }
            else
            {
                undoStack.Push(new ArchitectureEdit(currentLot, updated, charge, strokeId));
            }
            redoStack.Clear();
            lot = updated;
            lotValuationEdited = true;
            RefreshWorldRoomMaps(current, lot);
        }
        return new { applied = true, changed, charge = changed > 0 ? charge : 0, insufficientFunds = insufficient, refused = tool.Refused, canUndo = undoStack.Count > 0, canRedo = redoStack.Count > 0, budget = budget.ReadFamilyBudget() };
    }

    private object ArchitectureCommand(JsonElement request)
    {
        var current = world ?? throw new InvalidOperationException("There is no loaded native lot.");
        var currentLot = lot ?? throw new InvalidOperationException("There is no loaded native lot.");
        if (speed != 0) throw new InvalidOperationException("Pause the simulation before editing architecture");
        if (current.HasVisibleBlockingDialog) throw new InvalidOperationException("Answer the active game dialog before editing architecture");
        if (current.FamilyBudget is not FamilyBudget budget || (budget.ActiveFamily is null && current[SimulationGlobal.SimlessBuildMode] == 0))
            throw new InvalidOperationException("Architecture editing requires an active household");
        var command = request.TryGetProperty("command", out var commandValue) ? commandValue.GetString() : null;
        if (command is "TERRAIN_RAISE" or "TERRAIN_FLATTEN" or "GRASS_DOT")
        {
            using var batchDoc = JsonSerializer.SerializeToDocument(new { commands = new[] { request } });
            return ArchitectureBatch(batchDoc.RootElement);
        }
        if (command is not ("FLOOR_RECT" or "FLOOR_FILL" or "FLOOR_FILL_ERASE" or "POOL_RECT" or "POOL_ERASE" or "WATER_RECT" or "WATER_ERASE" or "WALL_LINE" or "WALL_RECT" or "WALL_ERASE" or "WALL_STROKE" or "PATTERN_DOT" or "PATTERN_ERASE" or "PATTERN_FILL" or "PATTERN_FILL_ERASE")) throw new NotSupportedException($"Native architecture command '{command}' is not implemented");
        int Number(string name) => request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed : throw new ArgumentException($"{name} must be an integer");
        bool IsWaterBuildModeObject(int guid)
        {
            if (registry?.Find(unchecked((uint)guid)) is not { } objectEntry) return false;
            if (objectEntry.Definition[ObjdField.RoomSort] != 0) return false;
            if (objectEntry.Definition[ObjdField.BuildModeType] == 7) return true;
            var masterId = objectEntry.Definition[ObjdField.MasterId];
            if (masterId == 0) return false;
            if (registry.FindAlternativeType(objectEntry) is { } alt && alt.Definition[ObjdField.RoomSort] == 0 && alt.Definition[ObjdField.BuildModeType] == 7) return true;
            return objectEntry.File.OfType(ResourceType.Objd)
                .Select(chunk => Objd.Read(objectEntry.File, chunk))
                .Any(def => def[ObjdField.MasterId] == masterId && def[ObjdField.SubIndex] == ushort.MaxValue && def[ObjdField.RoomSort] == 0 && def[ObjdField.BuildModeType] == 7);
        }
        var isFloorPond = command == "FLOOR_RECT" && request.TryGetProperty("pattern", out var floorPatternProp)
            && floorPatternProp.ValueKind == JsonValueKind.Number && floorPatternProp.TryGetInt32(out var floorPatternVal) && floorPatternVal == 65534;
        if (command is "WATER_RECT" or "WATER_ERASE" || isFloorPond)
        {
            var waterLevel = Number("level");
            if (waterLevel != 1) throw new ArgumentException("Water can only be edited on the ground floor");
            var waterCatalog = floorCatalog ??= FloorCatalog.Open(game, language);
            var waterFloors = LotFloors.Read(currentLot, 1);
            var waterLayer = LotWater.Read(currentLot);
            var waterPools = LotPool.Read(currentLot);
            var waterTiles = LotTiles.Read(currentLot);
            var waterTool = new WaterTool(current, waterLayer, waterFloors, waterPools, waterTiles, LotTerrain.Read(currentLot),
                LotWalls.Read(currentLot, 1), waterCatalog, budget, IsWaterBuildModeObject);
            var waterX = Number("x");
            var waterY = Number("y");
            var waterDeltaX = Number("x2");
            var waterDeltaY = Number("y2");
            if (Math.Abs((long)waterDeltaX) > 128 || Math.Abs((long)waterDeltaY) > 128)
                throw new ArgumentException("Water rectangle extent is outside the lot");
            var waterErase = command == "WATER_ERASE";
            var waterResult = waterTool.Rectangle(waterX, waterY, checked(waterX + waterDeltaX), checked(waterY + waterDeltaY), waterErase);
            if (waterResult.Applied)
            {
                var updated = waterLayer.WriteTo(waterFloors.WriteTo(currentLot));
                undoStack.Push(new ArchitectureEdit(currentLot, updated, waterResult.Charge));
                redoStack.Clear();
                lot = updated;
                lotValuationEdited = true;
                RefreshWorldRoomMaps(current, lot);
            }
            return new { applied = waterResult.Applied, command, changed = waterResult.Changed, charge = waterResult.Charge, insufficientFunds = waterResult.InsufficientFunds, refused = waterResult.Refused, canUndo = undoStack.Count > 0, canRedo = redoStack.Count > 0, budget = budget.ReadFamilyBudget() };
        }
        if (command is "POOL_RECT" or "POOL_ERASE")
        {
            var poolLevel = Number("level");
            if (poolLevel != 1) throw new ArgumentException("Pools can only be edited on the ground floor");
            var poolCatalog = floorCatalog ??= FloorCatalog.Open(game, language);
            if (!poolCatalog.Contains(65535)) throw new InvalidDataException("Build.iff has no pool price");
            var poolFloors = LotFloors.Read(currentLot, 1);
            var poolLayer = LotPool.Read(currentLot);
            var poolTiles = LotTiles.Read(currentLot);
            var poolTool = new PoolTool(current, poolFloors, poolLayer, poolTiles, LotTerrain.Read(currentLot), LotWalls.Read(currentLot, 1), poolCatalog, budget,
                IsWaterBuildModeObject, LotWater.Read(currentLot));
            var poolX = Number("x");
            var poolY = Number("y");
            var poolDeltaX = Number("x2");
            var poolDeltaY = Number("y2");
            if (Math.Abs((long)poolDeltaX) > 128 || Math.Abs((long)poolDeltaY) > 128)
                throw new ArgumentException("Pool rectangle extent is outside the lot");
            var poolResult = poolTool.Rectangle(poolX, poolY, checked(poolX + poolDeltaX), checked(poolY + poolDeltaY), command == "POOL_ERASE");
            if (poolResult.Applied)
            {
                var updated = poolLayer.WriteTo(poolFloors.WriteTo(currentLot));
                undoStack.Push(new ArchitectureEdit(currentLot, updated, poolResult.Charge));
                redoStack.Clear();
                lot = updated;
                lotValuationEdited = true;
                RefreshWorldRoomMaps(current, lot);
            }
            return new { applied = poolResult.Applied, command, changed = poolResult.Changed, charge = poolResult.Charge, insufficientFunds = poolResult.InsufficientFunds, refused = poolResult.Refused, canUndo = undoStack.Count > 0, canRedo = redoStack.Count > 0, budget = budget.ReadFamilyBudget() };
        }
        if (command is "FLOOR_FILL" or "FLOOR_FILL_ERASE")
        {
            var fillLevel = Number("level");
            if (fillLevel is < 1 or > 2) throw new ArgumentException("level must be 1 or 2");
            var fillErase = command == "FLOOR_FILL_ERASE";
            var fillPattern = fillErase ? 0 : Number("pattern");
            var fillCatalog = floorCatalog ??= FloorCatalog.Open(game, language);
            if (fillPattern != 0 && !fillCatalog.Contains(fillPattern)) throw new ArgumentException($"Floor pattern {fillPattern} is not in the build catalog");
            var fillFloors = LotFloors.Read(currentLot, fillLevel);
            var fillWalls = LotWalls.Read(currentLot, fillLevel);
            var fillTiles = LotTiles.Read(currentLot);
            var fillPool = LotPool.Read(currentLot);
            var fillTool = new FloorTool(current, fillFloors, fillCatalog, budget, LotTerrain.Read(currentLot), fillPool, fillTiles, fillWalls, LotWater.Read(currentLot));
            var fillRooms = RoomMap.Build(currentLot, fillLevel, LotWidth, LotHeight);
            var fillHalf = request.TryGetProperty("half", out _) ? Number("half") : 0;
            var filled = fillTool.Fill(fillRooms, Number("x"), Number("y"), fillPattern, fillHalf);
            var fillInsufficient = fillTool.InsufficientFunds;
            var fillCharge = fillTool.Commit();
            if (filled > 0)
            {
                var updated = fillWalls.WriteTo(fillFloors.WriteTo(currentLot));
                if (fillLevel == 1) updated = fillPool.WriteTo(updated);
                undoStack.Push(new ArchitectureEdit(currentLot, updated, fillCharge));
                redoStack.Clear();
                lot = updated;
                lotValuationEdited = true;
                RefreshWorldRoomMaps(current, lot);
            }
            return new { applied = true, command, changed = filled, charge = fillCharge, insufficientFunds = fillInsufficient, canUndo = undoStack.Count > 0, canRedo = redoStack.Count > 0, budget = budget.ReadFamilyBudget() };
        }
        if (command is "PATTERN_FILL" or "PATTERN_FILL_ERASE")
        {
            var fillLevel = Number("level");
            if (fillLevel is < 1 or > 2) throw new ArgumentException("level must be 1 or 2");
            var fillErase = command == "PATTERN_FILL_ERASE";
            var fillPattern = fillErase ? 0 : Number("pattern");
            var fillCatalog = wallpaperCatalogCache ??= WallpaperCatalog.Open(game, language);
            var fillWalls = LotWalls.Read(currentLot, fillLevel);
            var fillTool = new WallpaperTool(fillWalls, fillCatalog, budget, LotTiles.Read(currentLot));
            var fillRooms = RoomMap.Build(currentLot, fillLevel, LotWidth, LotHeight);
            var filled = fillTool.Fill(fillRooms, Number("x"), Number("y"), fillPattern, fillErase, request.TryGetProperty("half", out _) ? Number("half") : 0);
            var fillInsufficient = fillTool.InsufficientFunds;
            var fillError = fillTool.Error;
            var fillCharge = fillTool.Commit();
            if (filled > 0)
            {
                var updated = fillWalls.WriteTo(currentLot);
                undoStack.Push(new ArchitectureEdit(currentLot, updated, fillCharge));
                redoStack.Clear();
                lot = updated;
                lotValuationEdited = true;
                RefreshWorldRoomMaps(current, lot);
            }
            return new { applied = true, command, changed = filled, charge = fillCharge, insufficientFunds = fillInsufficient, error = fillError, canUndo = undoStack.Count > 0, canRedo = redoStack.Count > 0, budget = budget.ReadFamilyBudget() };
        }
        if (command is "PATTERN_DOT" or "PATTERN_ERASE")
        {
            // Identifies a tile wall edge by bit flag (1, 2, 4, 8, 16, or 32), with side indicating the
            // facing of diagonal walls.
            var dotLevel = Number("level");
            if (dotLevel is < 1 or > 2) throw new ArgumentException("level must be 1 or 2");
            var erase = command == "PATTERN_ERASE";
            var dotPattern = erase ? 0 : Number("pattern");
            var patternCatalog = wallpaperCatalogCache ??= WallpaperCatalog.Open(game, language);
            var patternWalls = LotWalls.Read(currentLot, dotLevel);
            var patternTool = new WallpaperTool(patternWalls, patternCatalog, budget, LotTiles.Read(currentLot));
            var dotEdge = request.TryGetProperty("edge", out _) ? Number("edge") : (1 << Number("x2"));
            var painted = patternTool.Dot(Number("x"), Number("y"), dotEdge, request.TryGetProperty("side", out _) ? Number("side") : 0, dotPattern, erase);
            var patternInsufficient = patternTool.InsufficientFunds;
            var patternError = patternTool.Error;
            var patternCharge = patternTool.Commit();
            if (painted)
            {
                var updated = patternWalls.WriteTo(currentLot);
                undoStack.Push(new ArchitectureEdit(currentLot, updated, patternCharge));
                redoStack.Clear();
                lot = updated;
                lotValuationEdited = true;
                RefreshWorldRoomMaps(current, lot);
            }
            return new { applied = true, command, changed = painted ? 1 : 0, charge = patternCharge, insufficientFunds = patternInsufficient, error = patternError, canUndo = undoStack.Count > 0, canRedo = redoStack.Count > 0, budget = budget.ReadFamilyBudget() };
        }
        if (command != "FLOOR_RECT")
        {
            var wallLevel = Number("level");
            if (wallLevel is < 1 or > 2) throw new ArgumentException("level must be 1 or 2");
            var wallStyle = request.TryGetProperty("style", out _) ? Number("style") : 1;
            var wallPattern = request.TryGetProperty("pattern", out _) ? Number("pattern") : 0;
            var wallCatalog = wallCatalogCache ??= WallCatalog.Open(game, language);
            var walls = LotWalls.Read(currentLot, wallLevel);
            var wallFloors = LotFloors.Read(currentLot, wallLevel);
            var wallTool = new WallTool(current, walls, LotTerrain.Read(currentLot), wallCatalog, budget, LotPool.Read(currentLot), LotTiles.Read(currentLot), LotWater.Read(currentLot), wallFloors);
            var beforePortals = current.WalkOrder
                .Where(item => item is not XPerson &&
                               item.Definition[(int)ObjdField.Type] == 8 &&
                               item.Position.Height == wallLevel &&
                               (item.MultiTileMasterId == 0 || item.MultiTileMasterId == item.Id))
                .Select(lead => (
                    Lead: lead,
                    Guid: ResolveCatalogSpawnGuid(lead),
                    Position: lead.Position,
                    DirectionFlag: (byte)(1 << (lead[ObjectData.Direction] & 6)),
                    SavedCurrentValue: lead[ObjectData.CurrentValue],
                    ResetWallEdits: CapturePortalWallEdits(current, lead, resetToDefault: true)))
                .ToArray();
            var segments = 0;
            var wallInsufficient = false;
            if (command == "WALL_STROKE")
            {
                if (!request.TryGetProperty("segments", out var path) || path.ValueKind != JsonValueKind.Array || path.GetArrayLength() > 128)
                    throw new ArgumentException("Wall stroke must contain at most 128 segments");
                foreach (var segment in path.EnumerateArray())
                {
                    if (segment.ValueKind != JsonValueKind.Object || !segment.TryGetProperty("command", out var actionValue))
                        throw new ArgumentException("Wall stroke segment is invalid");
                    var action = actionValue.GetString();
                    if (action is not ("WALL_LINE" or "WALL_RECT" or "WALL_ERASE")) throw new ArgumentException("Wall stroke action is invalid");
                    int SegmentNumber(string name) => segment.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
                        ? parsed : throw new ArgumentException($"Wall stroke {name} must be an integer");
                    var segmentStyle = SegmentNumber("style");
                    var segmentPattern = SegmentNumber("pattern");
                    var changedInSegment = action == "WALL_RECT"
                        ? wallTool.Rectangle(SegmentNumber("x"), SegmentNumber("y"), SegmentNumber("x2"), SegmentNumber("y2"), segmentStyle, segmentPattern)
                        : wallTool.Line(SegmentNumber("x"), SegmentNumber("y"), SegmentNumber("x2"), SegmentNumber("y2"), segmentStyle, segmentPattern, action == "WALL_ERASE");
                    segments += changedInSegment;
                    wallInsufficient |= wallTool.InsufficientFunds;
                }
            }
            else
            {
                segments = command == "WALL_RECT"
                    ? wallTool.Rectangle(Number("x"), Number("y"), Number("x2"), Number("y2"), wallStyle, wallPattern)
                    : wallTool.Line(Number("x"), Number("y"), Number("x2"), Number("y2"), wallStyle, wallPattern, command == "WALL_ERASE");
                wallInsufficient = wallTool.InsufficientFunds;
            }
            var wallCharge = wallTool.Commit();
            if (segments > 0)
            {
                List<DemolishedOpeningRecord>? demolishedOpenings = null;
                foreach (var portal in beforePortals)
                {
                    if (current.ObjectById(portal.Lead.Id) is not null) continue;
                    if (!liveTokens.Remove(portal.Lead.Id, out var token))
                        token = new BuildObjectToken(portal.Lead.Id);
                    demolishedOpenings ??= [];
                    demolishedOpenings.Add(new DemolishedOpeningRecord(
                        token, portal.Guid, portal.Position, portal.DirectionFlag, portal.SavedCurrentValue,
                        portal.ResetWallEdits));
                }
                if (demolishedOpenings is { Count: > 0 })
                {
                    FlushPendingLotUpdates(current);
                    foreach (var rec in demolishedOpenings)
                    {
                        foreach (var (editLevel, ownerX, ownerY, bit, style) in rec.ResetWallEdits)
                        {
                            if (editLevel != wallLevel) continue;
                            var cell = walls[ownerX, ownerY];
                            if ((cell[0] & bit) == 0) continue;
                            var styleIndex = bit == 1 ? 2 : 3;
                            if (cell[styleIndex] != style) cell[styleIndex] = style;
                        }
                    }
                }
                var updated = wallFloors.WriteTo(walls.WriteTo(currentLot));
                if (demolishedOpenings is { Count: > 0 })
                {
                    var crossLevelEdits = demolishedOpenings
                        .SelectMany(rec => rec.ResetWallEdits)
                        .Where(edit => edit.Level != wallLevel)
                        .ToArray();
                    if (crossLevelEdits.Length > 0)
                        updated = ApplyPortalWallEditsToLot(updated, crossLevelEdits, out _);
                }
                undoStack.Push(new ArchitectureEdit(currentLot, updated, wallCharge, DemolishedOpenings: demolishedOpenings));
                redoStack.Clear();
                lot = updated;
                lotValuationEdited = true;
                RefreshWorldRoomMaps(current, lot);
            }
            return new { applied = true, command, changed = segments, charge = wallCharge, insufficientFunds = wallInsufficient, refused = wallTool.Refused, error = wallTool.Error, canUndo = undoStack.Count > 0, canRedo = redoStack.Count > 0, budget = budget.ReadFamilyBudget() };
        }
        var x = Number("x");
        var y = Number("y");
        var level = Number("level");
        var width = Number("x2");
        var depth = Number("y2");
        var pattern = Number("pattern");
        if (level is < 1 or > 2) throw new ArgumentException("level must be 1 or 2");
        if (x < 0 || y < 0 || x >= LotWidth || y >= LotHeight) throw new ArgumentException("Architecture command is outside the lot");
        if (Math.Abs((long)width) > 128 || Math.Abs((long)depth) > 128) throw new ArgumentException("Architecture extent is outside the lot");
        var catalog = floorCatalog ??= FloorCatalog.Open(game, language);
        if (pattern != 0 && !catalog.Contains(pattern)) throw new ArgumentException($"Floor pattern {pattern} is not in the build catalog");
        var floors = LotFloors.Read(currentLot, level);
        var floorWalls = LotWalls.Read(currentLot, level);
        var floorLotTiles = LotTiles.Read(currentLot);
        var floorPool = LotPool.Read(currentLot);
        var tool = new FloorTool(current, floors, catalog, budget, LotTerrain.Read(currentLot), floorPool, floorLotTiles, floorWalls, LotWater.Read(currentLot));
        var half = request.TryGetProperty("half", out _) ? Number("half") : 0;
        var changed = tool.Rectangle(x, y, x + width, y + depth, pattern, half);
        var insufficient = tool.InsufficientFunds;
        var charge = tool.Commit();
        if (changed > 0)
        {
            var updated = floorWalls.WriteTo(floors.WriteTo(currentLot));
            if (level == 1) updated = floorPool.WriteTo(updated);
            undoStack.Push(new ArchitectureEdit(currentLot, updated, charge));
            redoStack.Clear();
            lot = updated;
            lotValuationEdited = true;
            RefreshWorldRoomMaps(current, lot);
        }
        return new { applied = true, command, changed, charge, insufficientFunds = insufficient, canUndo = undoStack.Count > 0, canRedo = redoStack.Count > 0, budget = budget.ReadFamilyBudget() };
    }

    private object MoveObject(JsonElement request)
    {
        var current = world ?? throw new InvalidOperationException("There is no loaded native lot.");
        if (speed != 0) throw new InvalidOperationException("Pause the simulation before editing objects");
        if (current.HasVisibleBlockingDialog) throw new InvalidOperationException("Answer the active game dialog before editing objects");
        if (current.FamilyBudget is not FamilyBudget budget || (budget.ActiveFamily is null && current[SimulationGlobal.SimlessBuildMode] == 0))
            throw new InvalidOperationException("Object editing requires an active household");
        var id = request.TryGetProperty("objectID", out var idValue) && idValue.TryGetInt16(out var parsedId)
            ? parsedId : throw new ArgumentException("objectID must be a 16-bit object id");
        var x = Coordinate(request, "x");
        var y = Coordinate(request, "y");
        var level = request.TryGetProperty("level", out var levelValue) && levelValue.TryGetInt32(out var parsedLevel)
            ? parsedLevel : throw new ArgumentException("level must be an integer");
        var directionFlag = request.TryGetProperty("direction", out var directionValue) && directionValue.TryGetByte(out var parsedDirection)
            ? parsedDirection : throw new ArgumentException("direction must be 1, 4, 16 or 64");
        if (directionFlag is not (1 or 4 or 16 or 64)) throw new ArgumentException("direction must be 1, 4, 16 or 64");
        if (x < 0 || y < 0 || x >= LotWidth * 16 || y >= LotHeight * 16 || level < 1 || level > Stories)
            throw new ArgumentException("Object position is outside the lot");
        var item = current.ObjectById(id);
        if (item is null || (item is XPerson && !current.CanPickUpObject(item))) throw new ArgumentException("Object cannot be moved");
        if (!current.CanPickUpObject(item)) throw new InvalidOperationException("Object cannot be moved");
        var lead = MultiTileMembers(current, item).FirstOrDefault() ?? item;
        var oldPosition = lead.Position;
        var oldDirectionFlag = (byte)(1 << (lead[ObjectData.Direction] & 6));
        var lotBeforeMove = lot;
        var oldPortalEdits = CapturePortalWallEdits(current, item, resetToDefault: true);
        // Executes placement-check and user-placement BHAV routines within a transient stack to validate
        // multi-tile object groups when moved.
        if (!current.MoveObjectFromEditor(item, new ObjectPosition(x, y, level), directionFlag,
                TreeRunner.CreateTransientStack(current, item)))
            throw new InvalidOperationException("Object cannot be placed at that position");
        ApplyPortalWallEdits(oldPortalEdits);
        ApplyPortalWallEdits(CapturePortalWallEdits(current, item, resetToDefault: false));
        FlushPendingLotUpdates(current);
        var buildMode = ObjectBuildModeType(lead);
        if (buildMode == 0)
        {
            ClearBuildHistory();
        }
        else
        {
            if (!liveTokens.TryGetValue(lead.Id, out var token))
            {
                token = new BuildObjectToken(lead.Id);
                liveTokens[lead.Id] = token;
            }
            undoStack.Push(new BuildMoveEdit(token, oldPosition, oldDirectionFlag,
                lead.Position, directionFlag, lotBeforeMove, lot));
            redoStack.Clear();
        }
        return new { moved = true, objectID = (int)id, x = item.Position.X / 16f, y = item.Position.Y / 16f, level = item.Position.Height, canUndo = undoStack.Count > 0, canRedo = redoStack.Count > 0 };
    }

    /// <summary>Generates a placement footprint preview without committing objects to the world, or renders
    /// zero-cost build cursors such as walls (GUID 0x439) or terrain (GUID 0x2F39B7A6).</summary>
    private object PreviewObject(JsonElement request, bool cursor = false)
    {
        var current = world ?? throw new InvalidOperationException("There is no loaded native lot.");
        if (speed != 0) throw new InvalidOperationException("Pause the simulation before editing objects");
        if (current.HasVisibleBlockingDialog) throw new InvalidOperationException("Answer the active game dialog before editing objects");
        if (current.FamilyBudget is not FamilyBudget budget || (budget.ActiveFamily is null && current[SimulationGlobal.SimlessBuildMode] == 0))
            throw new InvalidOperationException("Object editing requires an active household");

        var hasGuid = request.TryGetProperty("guid", out var guidValue);
        var hasObject = request.TryGetProperty("objectID", out _);
        if (hasGuid == hasObject && !cursor) throw new ArgumentException("Supply either a catalog GUID or an object ID");
        XObject? existing = null;
        if (hasObject && !cursor)
        {
            existing = RequestObject(request, "objectID");
            if (!current.CanPickUpObject(existing)) throw new InvalidOperationException("Object cannot be moved");
        }
        var guid = existing is not null ? unchecked((uint)(existing.TypeRecord?.Key ?? 0))
            : hasGuid && guidValue.TryGetUInt32(out var parsedGuid)
                ? parsedGuid : throw new ArgumentException("guid must be an unsigned 32-bit object GUID");
        if (cursor && guid is not (0x439u or 0x2F39B7A6u)) throw new ArgumentException("Invalid build cursor");
        var entry = registry?.Find(guid) ?? throw new ArgumentException("Object is not in the loaded catalog");
        ObjectCatalogEntry? catalogItem = null;
        if (!cursor && existing is null && (!ObjectCatalog.TryGetEntry(entry, language, CurrentCatalogLotType(), out catalogItem) || catalogItem is null))
            throw new ArgumentException("Object is not a supported catalog item");

        var tileX = Coordinate(request, "x");
        var tileY = Coordinate(request, "y");
        var x = tileX;
        var y = tileY;
        short graphic = 0;
        var centered = false;
        if (cursor)
        {
            graphic = request.TryGetProperty("graphic", out var graphicValue) && graphicValue.TryGetInt16(out var parsedGraphic) && parsedGraphic is >= 0 and <= 3
                ? parsedGraphic : throw new ArgumentException("graphic must be an integer from 0 through 3");
            centered = request.TryGetProperty("centered", out var centeredValue) && centeredValue.GetBoolean();
            x = x * 16 + (centered ? 8 : 0);
            y = y * 16 + (centered ? 8 : 0);
        }
        var level = request.TryGetProperty("level", out var levelValue) && levelValue.TryGetInt32(out var parsedLevel)
            ? parsedLevel : throw new ArgumentException("level must be an integer");
        var directionFlag = request.TryGetProperty("direction", out var directionValue) && directionValue.TryGetByte(out var parsedDirection)
            ? parsedDirection : throw new ArgumentException("direction must be 1, 4, 16 or 64");
        var direction = directionFlag switch
        {
            1 => 0,
            4 => 2,
            16 => 4,
            64 => 6,
            _ => throw new ArgumentException("direction must be 1, 4, 16 or 64"),
        };
        if (x < 0 || y < 0 || x >= LotWidth * 16 || y >= LotHeight * 16 || level < 1 || level > Stories)
            throw new ArgumentException("Object position is outside the lot");

        IReadOnlyList<XObject>? group = null;
        XObject? item = null;
        bool valid = false;
        object? ghost;
        object? wallPreview = null;
        var destination = new ObjectPosition(x, y, level);
        IReadOnlyList<object> tiles;
        var effectivePrice = catalogItem?.Price ?? 0;
        try
        {
            var groupGuid = existing is not null && entry.Definition[ObjdField.MasterId] is var masterId and not 0 &&
                entry.Definition[ObjdField.SubIndex] != ushort.MaxValue &&
                registry.Entries.FirstOrDefault(candidate => ReferenceEquals(candidate.File, entry.File) &&
                    candidate.Definition[ObjdField.MasterId] == masterId &&
                    candidate.Definition[ObjdField.SubIndex] == ushort.MaxValue) is { } masterEntry
                ? masterEntry.Guid
                : guid;
            group = current.PlacementPreviewGroupFactory?.Invoke(unchecked((int)groupGuid));
            if (group is { Count: 0 }) throw new ArgumentException("Object group could not be created");
            if (group is { Count: > 0 })
            {
                item = existing is not null
                    ? group.FirstOrDefault(member => member.TypeRecord?.Key == existing.TypeRecord?.Key) ?? group[0]
                    : group[0];
            }
            else
            {
                if (entry.Definition[ObjdField.MasterId] != 0)
                    throw new ArgumentException("Multi-tile object preview is unavailable");
                item = (cursor ? current.BuildCursorFactory : current.PlacementPreviewFactory)?.Invoke(unchecked((int)guid))
                    ?? throw new ArgumentException("Object definition could not be loaded");
                item.Position = new ObjectPosition(-16, -16, level);
                current.Add(item);
                current.InitializeCreatedObject(item);
            }
            var members = group ?? [item];
            if (cursor) foreach (var member in members) member[ObjectData.Graphic] = graphic;
            else if (existing is not null)
            {
                var existingMembers = MultiTileMembers(current, existing);
                foreach (var member in members)
                {
                    var source = existingMembers.FirstOrDefault(candidate => candidate.TypeRecord?.Key == member.TypeRecord?.Key) ?? existing;
                    member[ObjectData.Graphic] = source[ObjectData.Graphic];
                    for (var flagIndex = 0; flagIndex < Math.Min(member.DynamicSpriteFlags.Length, source.DynamicSpriteFlags.Length); flagIndex++)
                        member.DynamicSpriteFlags[flagIndex] = source.DynamicSpriteFlags[flagIndex];
                }
            }
            int? hoveredWallDir = request.TryGetProperty("wallDirection", out var hoveredWallDirEl) &&
                hoveredWallDirEl.ValueKind == JsonValueKind.Number &&
                hoveredWallDirEl.TryGetInt32(out var parsedHoveredWallDir) &&
                parsedHoveredWallDir is >= 0 and <= 3
                ? parsedHoveredWallDir
                : null;
            current.FaceCreatedObject(item, members, (short)direction);
            XObject? previewReferenceObject = null;
            int previewReferenceSlot = -1;
            if (existing is not null)
            {
                var move = current.PreviewEditorMove(existing, destination, directionFlag,
                    TreeRunner.CreateTransientStack(current, existing));
                if (!move.Valid && !cursor && hoveredWallDir is not null)
                {
                    var altDir = (direction + 4) & 7;
                    var altDirFlag = (byte)(1 << altDir);
                    current.FaceCreatedObject(item, members, (short)altDir);
                    var altMove = current.PreviewEditorMove(existing, destination, altDirFlag,
                        TreeRunner.CreateTransientStack(current, existing));
                    if (altMove.Valid)
                    {
                        direction = altDir;
                        directionFlag = altDirFlag;
                        move = altMove;
                    }
                    else
                    {
                        current.FaceCreatedObject(item, members, (short)direction);
                    }
                }
                valid = move.Valid;
                tiles = move.Tiles.Select(tile => (object)new { x = tile.X, y = tile.Y, level = tile.Level }).ToArray();
                previewReferenceObject = move.ReferenceObject;
                previewReferenceSlot = move.ReferenceSlot;
            }
            else
            {
                valid = cursor
                    ? current.CanPlaceObject(item, destination)
                    : current.CanPlaceObjectFromEditor(item, destination, out previewReferenceObject, out previewReferenceSlot);
                if (!valid && !cursor && hoveredWallDir is not null)
                {
                    var altDir = (direction + 4) & 7;
                    var altDirFlag = (byte)(1 << altDir);
                    current.FaceCreatedObject(item, members, (short)altDir);
                    if (current.CanPlaceObjectFromEditor(item, destination, out var altRefObj, out var altRefSlot))
                    {
                        direction = altDir;
                        directionFlag = altDirFlag;
                        valid = true;
                        previewReferenceObject = altRefObj;
                        previewReferenceSlot = altRefSlot;
                    }
                    else
                    {
                        current.FaceCreatedObject(item, members, (short)direction);
                    }
                }
                if (!valid && !cursor && ObjectBuildModeType(item) == 5 && lot is { } currentPreviewLot &&
                    TryPrepareFireplaceFloors(current, currentPreviewLot, item, members, destination, out var previewUpdatedLot, out var previewFloorCost))
                {
                    try
                    {
                        RefreshWorldRoomMaps(current, previewUpdatedLot);
                        valid = current.CanPlaceObjectFromEditor(item, destination, out previewReferenceObject, out previewReferenceSlot);
                        if (valid) effectivePrice += previewFloorCost;
                    }
                    finally
                    {
                        RefreshWorldRoomMaps(current, currentPreviewLot);
                    }
                }
                tiles = PlacementTiles(item, group, destination);
            }
            PositionPreviewMembers(current, item, members, destination, previewReferenceObject, previewReferenceSlot);
            ghost = null;
            if (lot is not null && registry is not null)
            {
                try
                {
                    var exportLot = lot;
                    HashSet<(int X, int Y, int Level, int Direction)>? changedWalls = null;
                    if (cursor && guid == 0x439u && graphic == 2 && centered &&
                        request.TryGetProperty("pattern", out var cursorPatternProp) &&
                        cursorPatternProp.ValueKind == JsonValueKind.Number &&
                        cursorPatternProp.TryGetUInt16(out var cursorPattern) &&
                        level is >= 1 and <= 2)
                    {
                        var cursorWallDir = request.TryGetProperty("wallDirection", out var wallDirProp) && wallDirProp.TryGetInt32(out var parsedWallDir)
                            ? parsedWallDir : 0;
                        if (cursorWallDir is < 0 or > 3) throw new ArgumentException("Invalid wallpaper direction");
                        var cursorFill = request.TryGetProperty("fill", out var fillProp) &&
                            fillProp.ValueKind is JsonValueKind.True or JsonValueKind.False && fillProp.GetBoolean();
                        var wallpaperCatalog = wallpaperCatalogCache ??= WallpaperCatalog.Open(game, language);
                        if (cursorPattern == 0 || wallpaperCatalog.Contains(cursorPattern))
                        {
                            var originalWalls = LotWalls.Read(lot, level);
                            var previewWalls = LotWalls.Read(lot, level);
                            var previewTool = new WallpaperTool(previewWalls, wallpaperCatalog, new UnconstrainedFamilyBudget(), LotTiles.Read(lot));
                            var erase = cursorPattern == 0;
                            int edge;
                            int side;
                            if (request.TryGetProperty("wallEdge", out var edgeProp) && edgeProp.ValueKind == JsonValueKind.Number && edgeProp.TryGetInt32(out var parsedEdge))
                            {
                                edge = parsedEdge;
                                side = request.TryGetProperty("wallSide", out var sideProp) && sideProp.ValueKind == JsonValueKind.Number && sideProp.TryGetInt32(out var parsedSide)
                                    ? parsedSide : 0;
                            }
                            else
                            {
                                var seg = tileX >= 0 && tileX < LotWalls.Size && tileY >= 0 && tileY < LotWalls.Size
                                    ? (previewWalls[tileX, tileY][0] & 0x30)
                                    : 0;
                                if (seg is 0x10 or 0x20)
                                {
                                    edge = seg;
                                    (_, side) = SceneExporter.DiagonalFacingSlotAndSide(seg, cameraOrientation);
                                }
                                else { edge = 1 << cursorWallDir; side = 0; }
                            }
                            if (cursorFill)
                            {
                                var previewRooms = RoomMap.Build(lot, level, LotWidth, LotHeight);
                                previewTool.Fill(previewRooms, tileX, tileY, cursorPattern, erase, side);
                            }
                            else
                            {
                                previewTool.Dot(tileX, tileY, edge, side, cursorPattern, erase);
                            }
                            changedWalls = new HashSet<(int X, int Y, int Level, int Direction)>();
                            for (var sy = 0; sy < LotWalls.Size; sy++)
                            for (var sx = 0; sx < LotWalls.Size; sx++)
                            {
                                var oldCell = originalWalls[sx, sy];
                                var newCell = previewWalls[sx, sy];
                                if ((newCell[0] & 0x30) != 0)
                                {
                                    if (newCell[6] != oldCell[6] || newCell[7] != oldCell[7])
                                    {
                                        for (var d = 0; d < 4; d++) changedWalls.Add((sx, sy, level, d));
                                    }
                                }
                                else
                                {
                                    if (newCell[4] != oldCell[4]) changedWalls.Add((sx, sy, level, 0));
                                    if (newCell[5] != oldCell[5]) changedWalls.Add((sx, sy, level, 1));
                                    if (newCell[7] != oldCell[7]) changedWalls.Add((sx, sy, level, 2));
                                    if (newCell[6] != oldCell[6]) changedWalls.Add((sx, sy, level, 3));
                                }
                            }
                            if (changedWalls.Count > 0)
                                exportLot = previewWalls.WriteTo(lot);
                        }
                    }

                    var sceneDir = SceneDirectory;
                    var ghostIds = members.Select(member => member.Id).ToHashSet();
                    var savedSlotChildren = new List<(XObject Child, ObjectPosition Position, short Direction, short ContainerId, short ParentId)>();
                    if (existing is not null)
                    {
                        var existingMembers = MultiTileMembers(current, existing);
                        foreach (var existingMember in existingMembers)
                        {
                            var previewMember = members.FirstOrDefault(candidate => candidate.TypeRecord?.Key == existingMember.TypeRecord?.Key) ?? item;
                            var dirDelta = (previewMember[ObjectData.Direction] - existingMember[ObjectData.Direction]) & 7;
                            foreach (var child in current.EnumerateContainedSlotChildren(existingMember))
                            {
                                if (!ghostIds.Add(child.Id)) continue;
                                savedSlotChildren.Add((child, child.Position, child[ObjectData.Direction], child[ObjectData.ContainerId], child[ObjectData.ParentId]));
                                child.Position = previewMember.Position;
                                if ((child.Definition is not { Length: > (int)ObjdField.MasterId } ||
                                     child.Definition[(int)ObjdField.MasterId] == 0) &&
                                    child[ObjectData.RotationNotches] is 0 or 2)
                                {
                                    child[ObjectData.Direction] = (short)((child[ObjectData.Direction] + dirDelta) & 7);
                                }
                                if (child[ObjectData.ContainerId] == existingMember.ClockManagerIndex && previewMember.ClockManagerIndex > 0)
                                    child[ObjectData.ContainerId] = previewMember.ClockManagerIndex;
                                if (child[ObjectData.ParentId] == existingMember.ClockManagerIndex && previewMember.ClockManagerIndex > 0)
                                    child[ObjectData.ParentId] = previewMember.ClockManagerIndex;
                            }
                        }
                    }
                    string? scenePath = null;
                    try
                    {
                        scenePath = SceneExporter.Export(exportLot, current, registry, game, sceneDir, house,
                            1_000_000 + ++previewRevision, level, wallMode, cameraOrientation, cutawayTile, roofsVisible,
                            cutawayRooms: cutawayRooms, cutawaySim: CutawaySimTile(), cutawayZoom: cutawayZoom,
                            showLoweredPortals: true);
                        var referenceScenePath = Path.Combine(sceneDir, $"scene-{revision}.json");
                        ghost = SceneExporter.PlacementGhost(scenePath, ghostIds, referenceScenePath);
                        if (changedWalls is { Count: > 0 })
                            wallPreview = SceneExporter.WallPaintGhost(scenePath, changedWalls, referenceScenePath);
                    }
                    finally
                    {
                        foreach (var saved in savedSlotChildren)
                        {
                            saved.Child.Position = saved.Position;
                            saved.Child[ObjectData.Direction] = saved.Direction;
                            saved.Child[ObjectData.ContainerId] = saved.ContainerId;
                            saved.Child[ObjectData.ParentId] = saved.ParentId;
                        }
                        if (scenePath is not null)
                        {
                            SimsSimulation.InMemorySessionAssets.DeleteFile(scenePath);
                            if (File.Exists(scenePath)) File.Delete(scenePath);
                        }
                    }
                }
                catch (IOException) { }
                catch (InvalidDataException) { }
            }
        }
        finally
        {
            if (group is { Count: > 0 }) current.RemoveCreatedObjectGroup(group[0]);
            else if (item is not null) current.Remove(item.Id);
        }
        if (!cursor && viewerMode == 2) EnsureTileCursor(current);
        var status = valid && catalogItem is not null && effectivePrice > budget.ReadFamilyBudget() ? "InsufficientFunds" : valid ? "Success" : "CannotPlace";
        return new
        {
            schemaVersion = 1,
            valid = status == "Success",
            status,
            direction = (int)directionFlag,
            tiles,
            ghost,
            wallPreview,
        };
    }

    private const int TileCursorGuid = 0x437;
    private XObject? tileCursor;

    /// <summary>Ensures the tile cursor object (<c>Cursors.iff</c>, GUID 0x437, OBJD type 9) is
    /// instantiated on the lot while an item is held in Buy mode.</summary>
    private void EnsureTileCursor(SimulationWorld current)
    {
        if (tileCursor is not null && current.ObjectById(tileCursor.Id) is not null) return;
        var cursorObject = current.BuildCursorFactory?.Invoke(TileCursorGuid);
        if (cursorObject is null) return;
        cursorObject.Position = new ObjectPosition(-16, -16, 1);
        current.Add(cursorObject);
        current.InitializeCreatedObject(cursorObject);
        tileCursor = cursorObject;
    }

    private string ReloadAfterHouseholdCreation(ushort familyChunkId, int spawnCenterX, int spawnCenterY)
    {
        var previous = world;
        var autonomyForced = previous?.AutonomyForced ?? true;
        RemoveTileCursor();
        ResetSeasonalOutfits();
        audio?.Reset();

        var userData = Neighborhoods.UserData(profile);
        registry = registry?.WithRefreshedPeople(Path.Combine(userData, "Characters")) ?? registry;
        var lotPath = Path.Combine(userData, "Houses", $"House{house:D2}.iff");
        var setup = WorldBootstrap.Load(game, lotPath, language, () => true, house,
            autonomyForced: autonomyForced, activeFamilyChunkId: familyChunkId, existingRegistry: registry,
            neighborhoodManager: ConnectedNeighborhoodEnabled ? neighborhoodManager : null,
            connectedNeighborhoodEnabled: ConnectedNeighborhoodEnabled);
        lot = setup.Lot;
        ClearBuildHistory();
        world = setup.World;
        world[SimulationGlobal.SimlessBuildMode] = 0;
        ResetLotValuationBaseline();
        setup.World[SimulationGlobal.FreeWill] = (short)(autonomyForced ? 1 : 0);
        InitializeFameCallbackTarget(setup.World);
        neighborhoodPath = setup.NeighborhoodPath;
        neighborhood = setup.Neighborhood;
        activeFamilyChunkId = setup.ActiveFamilyChunkId;
        HydrateTypeAttributesFromNeighborhood(world, lot, neighborhood);
        simulationError = null;
        viewerMode = 0;
        viewLevel = 1;
        tileCursor = null;
        if (viewerScale != 0) world[SimulationGlobal.ZoomLevelSML] = viewerScale;
        world[SimulationGlobal.Mode] = viewerMode;
        ClampAwayLotEditingState();
        if (registry is not null) world.UiTextFile ??= registry.UiText;
        world.PlaySoundEvent = (name, owner, pan, zoom) =>
        {
            char? voiceCode = null;
            int? pianoSkill = null;
            if (world.ObjectById(owner) is XPerson person)
            {
                var age = person[PersonData.PersonAge];
                var gender = person[PersonData.Gender];
                voiceCode = PersonAgeCategory.IsChild(age) ? 'k' : ((gender & 1) != 0 ? 'f' : 'm');
                pianoSkill = person[PersonData.CreativitySkill] / 100;
            }

            var played = audio!.PlayForSimulationObject(name, owner, pan, zoom, voiceCode, pianoSkill);
            if (played) world.PositionalSoundOwners.Add(owner);
            return played;
        };
        world.StopOwnerSounds = owner =>
        {
            audio!.RemoveOwner(owner);
            world.PositionalSoundOwners.Remove(owner);
        };
        AttachTutorialFlash(world);
        if (traceActions) world.CaptureExecutionTrace = true;
        selectedSim = DefaultSelectedSimId();
        SyncSelectedSimFlags(world, selectedSim);
        var familyResource = setup.Neighborhood?.Find(ResourceType.Parse("FAMI"), familyChunkId);
        var memberInstantiation = EnsureSelectedHouseholdMember(familyResource is null || setup.Neighborhood is null
            ? null
            : Family.Read(setup.Neighborhood, familyResource), spawnCenterX, spawnCenterY);
        world.AutonomyHost = new AutonomyHost(world, setup.Global ?? global!, house, () => speed == 0, ScoreAutonomyChoice);
        world.AnimationSound = HandleAnimationSound;
        autoSpeedSaved = -1;
        SetSimulationSpeed(0);
        lastRunningSpeed = 1;
        dialogResumeSpeed = HasBlockingDialog(world) ? 1 : 0;
        AttachNoticeListener(setup.World);
        if (audio?.Enabled == true)
            audio.PlayLotAmbience(house);
        if (ConnectedNeighborhoodEnabled)
        {
            world.Neighborhood = neighborhoodManager;
            AttachConnectedNeighborhoodHooks(world);
            neighborhoodManager.TrackingEnabled = true;
            neighborhoodManager.SyncFromUserData(userData, language);
            neighborhoodManager.SyncFromWorld(world, house, activeLotIds: CollectActivePoolLotIds());
            SeedFocusedSessionIntoPool();
            if (lot is not null && house > 0 && connectedOptions.EnableAdjacentLotRendering)
                PrewarmAdjacentNeighborhoodParallel();
        }
        InitializeViewLevelFromSelectedSim();
        Export();
        Write(new { type = "state", state = State() });
        return memberInstantiation;
    }

    // When loading a lot, household member 0 is selected by default; if uninstantiated, a selector object
    // searches outward from (0, 0) for a valid placement location.
    private string EnsureSelectedHouseholdMember(Family? family, int centerX, int centerY)
    {
        if (world is null || family is null) return "no-active-family";
        if (family.Members.Count == 0) return "empty-family";
        var targetMemberGuids = (world.FamilyBudget as FamilyBudget)?.RestrictedActivePersonKeys is { Count: > 0 } restrictedKeys
            ? restrictedKeys.Select(static k => unchecked((uint)k)).ToArray()
            : family.Members.ToArray();
        short firstId = 0;
        string? status = null;
        foreach (var guid in targetMemberGuids)
        {
            if (guid == 0) continue;
            var existing = world.WalkOrder.OfType<XPerson>().FirstOrDefault(person => person.TypeRecord?.Key == unchecked((int)guid));
            if (existing is not null)
            {
                if (firstId == 0) firstId = existing.Id;
                status ??= "already-present";
                continue;
            }

            if (world.ObjectFactory?.Invoke(unchecked((int)guid)) is not XPerson person)
            {
                status ??= "character-factory-failed";
                continue;
            }
            world.Add(person);
            world.InitializeCreatedObject(person);
            world.InitializeCreatedPerson(person);
            if (!world.FindGoodLocation(person, centerX, centerY, out var position))
            {
                world.Remove(person.Id);
                status ??= "location-search-failed";
                continue;
            }
            world.PlaceCreatedObject(person, position);
            if (firstId == 0)
            {
                firstId = person.Id;
                status = $"placed:{firstId}@{position.X},{position.Y},{position.Height}";
            }
        }
        world.RefreshNeighborInteractionManager();
        if (firstId != 0)
        {
            selectedSim = firstId;
            SyncSelectedSimFlags(world, selectedSim);
        }
        return status ?? "empty-family";
    }

    private static void SyncSelectedSimFlags(SimulationWorld target, short simId)
    {
        target[SimulationGlobal.SelectedPersonID] = simId;
        foreach (var candidate in target.WalkOrder.OfType<XPerson>())
            candidate.Flags = candidate.Id == simId ? (candidate.Flags | 2u) : (candidate.Flags & ~2u);
    }

    private void RemoveTileCursor()
    {
        if (tileCursor is not null && world?.ObjectById(tileCursor.Id) is not null) world.Remove(tileCursor.Id);
        tileCursor = null;
    }

    private static IReadOnlyList<object> PlacementTiles(XObject item, IReadOnlyList<XObject>? group, ObjectPosition destination)
    {
        var members = group ?? [item];
        var rootState = item.SavedMultiTileState;
        var rootOffsetX = (rootState?.Value0x1A0 ?? 0) << 4;
        var rootOffsetY = (rootState?.Value0x1A4 ?? 0) << 4;
        var rootOffsetLevel = rootState?.Value0x1A8 ?? 0;
        return members.Select(member =>
        {
            var state = member.SavedMultiTileState;
            var memberX = (destination.X - rootOffsetX + ((state?.Value0x1A0 ?? 0) << 4)) >> 4;
            var memberY = (destination.Y - rootOffsetY + ((state?.Value0x1A4 ?? 0) << 4)) >> 4;
            var memberLevel = destination.Height - rootOffsetLevel + (state?.Value0x1A8 ?? 0);
            return (object)new { x = memberX, y = memberY, level = memberLevel };
        }).ToArray();
    }

    private static void PositionPreviewMembers(SimulationWorld current, XObject root, IReadOnlyList<XObject> members,
        ObjectPosition destination, XObject? referenceObject = null, int referenceSlot = -1)
    {
        if (referenceObject is not null && referenceSlot >= 0 && members.Count == 1)
        {
            var slotNumber = checked((short)(referenceSlot + 1));
            root[ObjectData.ContainerId] = referenceObject.ClockManagerIndex;
            root[ObjectData.SlotNumber] = slotNumber;
            root[ObjectData.ParentId] = referenceObject.ClockManagerIndex;
            root[ObjectData.SlotHierNumber] = slotNumber;
            current.SetObjectPosition(root, referenceObject.Position);
            return;
        }
        var rootState = root.SavedMultiTileState;
        var rootOffsetX = (rootState?.Value0x1A0 ?? 0) << 4;
        var rootOffsetY = (rootState?.Value0x1A4 ?? 0) << 4;
        var rootOffsetLevel = rootState?.Value0x1A8 ?? 0;
        foreach (var member in members)
        {
            var state = member.SavedMultiTileState;
            current.SetObjectPosition(member, new ObjectPosition(
                destination.X - rootOffsetX + ((state?.Value0x1A0 ?? 0) << 4),
                destination.Y - rootOffsetY + ((state?.Value0x1A4 ?? 0) << 4),
                destination.Height - rootOffsetLevel + (state?.Value0x1A8 ?? 0)));
        }
    }

    private static int Coordinate(JsonElement request, string property) =>
        request.TryGetProperty(property, out var value) && value.TryGetInt32(out var coordinate)
            ? coordinate : throw new ArgumentException($"{property} must be an integer in sixteenths of a tile");

    private static int TileCoordinate(JsonElement request, string property) =>
        request.TryGetProperty(property, out var value) && value.TryGetInt32(out var coordinate)
            ? coordinate : throw new ArgumentException($"{property} must be an integer lot tile coordinate");

    private XPerson SelectedPerson() => world?.ObjectById(selectedSim) as XPerson
        ?? throw new InvalidOperationException("Select a Sim before requesting an interaction.");

    private static byte MenuIndex(ActionQueueSlot choice)
    {
        if (choice.RawDword10 is < byte.MinValue or > byte.MaxValue)
            throw new NotSupportedException($"Interaction index {choice.RawDword10} exceeds the runtime protocol's 8-bit interaction field.");
        return (byte)choice.RawDword10;
    }

    private XObject RequestObject(JsonElement request, string property)
    {
        if (world is null) throw new InvalidOperationException("There is no loaded native lot.");
        if (!request.TryGetProperty(property, out var value) || !value.TryGetInt16(out var id))
            throw new ArgumentException($"{property} must be an object ID.");
        return world.ObjectById(id) ?? throw new ArgumentException($"Object {id} is not in the active lot.");
    }

    /// <summary>Determines whether outdoor terrain is snow-covered on Vacation Island winter lots (houses
    /// 40, 41, and 42).</summary>
    private bool GroundSnow() => house is 40 or 41 or 42;

    private (ObjectPosition Position, XObject Target) GroundPreview(JsonElement request, bool persistent = false)
    {
        var current = world ?? throw new InvalidOperationException("There is no loaded native lot.");
        var x = Coordinate(request, "x");
        var y = Coordinate(request, "y");
        var level = request.TryGetProperty("level", out var levelValue) && levelValue.TryGetInt32(out var parsedLevel)
            ? parsedLevel : throw new ArgumentException("level must be an integer");
        if (x < 0 || y < 0 || x >= LotWidth * 16 || y >= LotHeight * 16 || level < 1 || level > Stories)
            throw new ArgumentException("Ground position is outside the lot.");
        var target = (persistent ? current.ObjectFactory : current.PlacementPreviewFactory)?.Invoke(0x7c4)
            ?? throw new InvalidOperationException("The installed Go Here object is unavailable.");
        var position = new ObjectPosition(x, y, level);
        try
        {
            current.Add(target);
            current.InitializeCreatedObject(target);
            current.SetObjectPosition(target, position);
            return (position, target);
        }
        catch
        {
            current.Remove(target.Id);
            throw;
        }
    }

    private static string Slot(JsonElement request) =>
        request.TryGetProperty("slot", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 64 } slot
            ? new string(slot.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_').ToArray()) is { Length: > 0 } safe ? safe : "manual"
            : "manual";

    private string SaveEnvelopePath(string slot) => Path.Combine(profile, "Saves", Neighborhoods.StorageSlot(slot, Neighborhoods.Current) + ".json");

    private string SavePath(string slot) => Path.Combine(Saves, Neighborhoods.StorageSlot(slot, Neighborhoods.Current) + ".iff");

    private string SaveRuntimePath(string slot) => Path.Combine(Saves, Neighborhoods.StorageSlot(slot, Neighborhoods.Current) + ".runtime.json");

    private string SaveNeighborhoodPath(string slot) => Path.Combine(Saves, Neighborhoods.StorageSlot(slot, Neighborhoods.Current) + ".neighborhood.iff");

    private static string CanonicalRuntimePath(string housePath) => housePath + ".runtime.json";

    /// <summary>Updates LotSize (global 23), LotHasHouse (global 22), FilterFlagsForTransitScreen (global
    /// 37), and active family FAMI flags (bit 0 for baby present, bit 1 for phone present, clearing bit 3)
    /// before saving.</summary>
    private void UpdatePreSaveState(SimulationWorld current, IffFile currentLot) =>
        UpdatePreSaveState(current, currentLot, activeFamilyChunkId);

    private void UpdatePreSaveState(SimulationWorld current, IffFile currentLot, ushort? targetFamilyChunkId)
    {
        // Sets simulation global LotSize to the lot dimension (52 for House 5, 64 by default).
        if (current[SimulationGlobal.LotSize] <= 3)
            current[SimulationGlobal.LotSize] = LotGeometry.LotDimension;
        current[SimulationGlobal.Hour] = checked((short)current.Clock.Hour);
        current[SimulationGlobal.Minute] = checked((short)current.Clock.Minute);

        var lotTiles = LotTiles.Read(currentLot);
        var pool = LotPool.Read(currentLot);
        var hasArchitecture = false;
        for (var level = 1; level <= 2 && !hasArchitecture; level++)
        {
            var walls = LotWalls.Read(currentLot, level);
            var floors = LotFloors.Read(currentLot, level);
            for (var x = 0; x < LotWalls.Size && !hasArchitecture; x++)
            for (var y = 0; y < LotWalls.Size; y++)
            {
                if (!lotTiles.InsideLot(x, y)) continue;
                if ((walls[x, y][0] & 0x3f) != 0 || floors[x, y] != 0 || (level == 1 && pool.IsPool(x, y, 1)))
                {
                    hasArchitecture = true;
                    break;
                }
            }
        }

        var hasBaby = false;
        var hasPhone = false;
        var hasBuildableObject = false;
        var hasCashRegister = false;
        var hasCat4B = false;
        var hasCat4C = false;
        var hasShopDisplay = false;
        var hasCat61 = false;
        var filterFlags = 0u;

        foreach (var item in current.Objects.Values)
        {
            var category = item[ObjectData.Category];
            if (category > 0x3b)
            {
                if (category < 0x51)
                {
                    if (category == 0x50) filterFlags |= 0x80;
                    else if (category == 0x47) filterFlags |= 0x1;
                    else if (category == 0x48) filterFlags |= 0x2;
                    else if (category == 0x49) filterFlags |= 0x4;
                    else if (category == 0x4a) hasShopDisplay = true;
                    else if (category == 0x4b) hasCat4B = true;
                    else if (category == 0x4c) hasCat4C = true;
                    else if (category == 0x4d) filterFlags |= 0x40;
                }
                else if (category == 0x53) filterFlags |= 0x400;
                else if (category == 0x54) filterFlags |= 0x100;
                else if (category == 0x55) filterFlags |= 0x200;
                else if (category is 0x5d or 0x5e) filterFlags |= 0x1000;
                else if (category == 0x5f) filterFlags |= 0x800;
                else if (category == 0x61) hasCat61 = true;
            }
            else if (category is 0x3a or 0x3b)
            {
                hasShopDisplay = true;
            }
            else if (category < 0x1f)
            {
                if (category == 0x1e) hasShopDisplay = true;
                else if (category < 0xb)
                {
                    if (category == 10) hasCashRegister = true;
                    else if (category == 1 && item.Position.X >= 0 && item.Position.Y >= 0) hasBaby = true;
                    else if (category == 4) filterFlags |= 0x1;
                    else if (category == 9) hasPhone = true;
                }
                else if (category == 0x11 || category is >= 0x15 and <= 0x18) hasShopDisplay = true;
                else if (category is 0x19 or 0x1a) filterFlags |= 0x2;
            }
            else if (category < 0x32)
            {
                if (category is 0x31 or 0x2b) filterFlags |= 0x40;
                else if (category is 0x27 or 0x28) hasShopDisplay = true;
                else if (category is >= 0x2e and <= 0x30) filterFlags |= 0x1;
            }
            else if (category == 0x32) filterFlags |= 0x2;
            else if (category == 0x33) filterFlags |= 0x40;

            if (item is XPerson || (item.Definition is { Length: > (int)ObjdField.Type } && item.Definition[(int)ObjdField.Type] == 7))
                continue;
            if (item.MultiTileMasterId != 0 && item.MultiTileMasterId != item.Id)
                continue;
            if (item.Position.X >= 0 && item.Position.Y >= 0)
            {
                var roomSort = item.Definition is { Length: > (int)ObjdField.RoomSort } ? item.Definition[(int)ObjdField.RoomSort] : (short)0;
                var funcSort = item.Definition is { Length: > (int)ObjdField.FunctionSort } ? item.Definition[(int)ObjdField.FunctionSort] : (short)0;
                var buildMode = ObjectBuildModeType(item);
                if ((roomSort != 0 || funcSort != 0 || buildMode != 0) && lotTiles.InsideLot(item.Position.X >> 4, item.Position.Y >> 4))
                    hasBuildableObject = true;
            }
        }

        if (!hasCashRegister)
        {
            hasCat4B = false;
            hasCat4C = false;
            hasShopDisplay = false;
        }
        if (hasShopDisplay) filterFlags |= 0x8;
        if (hasCat4B) filterFlags |= 0x10;
        if (hasCat4C || hasCat61) filterFlags |= 0x20;

        current[SimulationGlobal.LotHasHouse] = (short)(hasArchitecture || hasBuildableObject ? 1 : 0);
        current[SimulationGlobal.FilterFlagsForTransitScreen] = unchecked((short)filterFlags);

        if (targetFamilyChunkId is { } familyChunkId &&
            current.FamilyBudget is FamilyBudget budget &&
            budget.ActiveFamily is { } activeFamily &&
            (budget.FamilyChunkId == familyChunkId || budget.FamilyChunkId is null))
        {
            var flags = activeFamily.Flags & ~0xbu;
            if (hasBaby) flags |= 1u;
            if (hasPhone) flags |= 2u;
            var updatedFamily = UpdateFamilyFields(activeFamily, familyChunkId, flags: flags);
            budget.ActiveFamily = updatedFamily;
            if (neighborhood is not null)
            {
                var famiType = ResourceType.Parse("FAMI");
                var existingChunk = neighborhood.Find(famiType, familyChunkId);
                neighborhood = neighborhood.WithAddedOrReplacedChunk(famiType, familyChunkId,
                    updatedFamily.Write(familyChunkId), existingChunk?.Flags ?? 0, existingChunk?.Label ?? "");
            }
        }
    }

    /// <summary>Deletes removed unhoused character files from the Characters directory following a lot
    /// save.</summary>
    private void DeleteUnhousedCharacters(string userData)
    {
        if (world is null) return;
        var families = neighborhood?.OfType(ResourceType.Parse("FAMI"))
            .Select(chunk => SimsEngine.Formats.Family.Read(neighborhood, chunk)).ToArray() ?? [];
        var characters = Path.GetFullPath(Path.Combine(userData, "Characters")) + Path.DirectorySeparatorChar;
        var guids = world.Neighbors.ToDictionary(pair => pair.Key, pair => unchecked((uint)pair.Value.TypeRecordKey));
        foreach (var id in world.DeleteUnhousedCharacters(guid => families.Any(family => family.Members.Contains(unchecked((uint)guid)))))
        {
            if (registry?.Find(guids[id]) is not { Scan: ObjectScan.People } entry) continue;
            var file = Path.GetFullPath(entry.Source);
            if (file.StartsWith(characters, StringComparison.OrdinalIgnoreCase) && File.Exists(file)) File.Delete(file);
            registry?.Remove(guids[id]);
        }
    }

    private object CreateHousehold(JsonElement request)
    {
        if (speed != 0 || simulatorBlocked)
            throw new InvalidOperationException("Household creation requires a paused residential lot without a blocking dialog or active trip");

        if (!request.TryGetProperty("familyName", out var familyNameElement) ||
            familyNameElement.ValueKind != JsonValueKind.String ||
            familyNameElement.GetString() is not { Length: > 0 } familyName)
            throw new ArgumentException("Family name is required.", nameof(request));

        var filteredChars = new HashSet<char> {
            '\\', '/', '|', '*', '?', ':', '<', '>', '\"', '\'', '%', '(', ')', '&', ';', '@', '!', '#', ',', '.'
        };
        if (Encoding.UTF8.GetByteCount(familyName) > 24 ||
            familyName.Any(c => char.IsControl(c) || filteredChars.Contains(c)))
            throw new ArgumentException("Family name exceeds the original 24-byte field capacity or contains illegal characters.", nameof(request));

        if (!request.TryGetProperty("members", out var membersElement) ||
            membersElement.ValueKind != JsonValueKind.Array ||
            membersElement.GetArrayLength() is < 1 or > 8)
            throw new ArgumentException("A household must contain one to eight Sims.", nameof(request));

        var members = membersElement.EnumerateArray().Select(m =>
        {
            var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Member name cannot be empty.");
            var bio = m.TryGetProperty("bio", out var b) ? b.GetString() ?? "" : "";
            var code = m.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            var skin = m.TryGetProperty("skin", out var s) ? s.GetString() ?? "lgt" : "lgt";
            var headChoice = m.TryGetProperty("headChoice", out var hc) && hc.ValueKind == JsonValueKind.String
                ? hc.GetString()
                : m.TryGetProperty("head", out var hAlt) && hAlt.ValueKind == JsonValueKind.String ? hAlt.GetString() : null;
            var bodyChoice = m.TryGetProperty("bodyChoice", out var bc) && bc.ValueKind == JsonValueKind.String
                ? bc.GetString()
                : m.TryGetProperty("body", out var bAlt) && bAlt.ValueKind == JsonValueKind.String ? bAlt.GetString() : null;
            var zodiac = m.TryGetProperty("zodiac", out var zProp) && zProp.ValueKind == JsonValueKind.Number && zProp.TryGetInt16(out var zVal)
                ? (short?)zVal : null;
            var isChild = (m.TryGetProperty("child", out var chProp) && chProp.ValueKind == JsonValueKind.True) ||
                (code.Length >= 2 && char.ToLowerInvariant(code[1]) == 'c') ||
                code.StartsWith("child", StringComparison.OrdinalIgnoreCase) ||
                (m.TryGetProperty("age", out var ageProp) && ageProp.ValueKind == JsonValueKind.String && string.Equals(ageProp.GetString(), "child", StringComparison.OrdinalIgnoreCase));
            var isFemale = (code.Length >= 1 && char.ToLowerInvariant(code[0]) == 'f') ||
                (string.IsNullOrEmpty(code) && m.TryGetProperty("gender", out var gProp) && gProp.ValueKind == JsonValueKind.String &&
                 gProp.GetString() is { Length: > 0 } genderText && char.ToLowerInvariant(genderText[0]) == 'f');
            var personality = m.TryGetProperty("personality", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(v => checked((short)v.GetInt32())).ToArray()
                : [500, 500, 500, 500, 500];
            return (Name: name, Bio: bio, Code: code, Skin: skin, Child: isChild, Female: isFemale, HeadChoice: headChoice, BodyChoice: bodyChoice, Zodiac: zodiac, Personality: personality);
        }).ToArray();

        if (!members.Any(member => !member.Child))
            throw new ArgumentException("A household must contain at least one adult Sim.", nameof(request));

        var moveIn = !request.TryGetProperty("moveIn", out var moveInElement) ||
            (moveInElement.ValueKind == JsonValueKind.True ||
             (moveInElement.ValueKind == JsonValueKind.String && bool.TryParse(moveInElement.GetString(), out var mb) && mb));

        var userDataPath = Neighborhoods.UserData(profile);
        var charDir = Path.Combine(userDataPath, "Characters");
        Directory.CreateDirectory(charDir);
        registry = EnsureRegistry().WithRefreshedPeople(charDir);

        var installRoot = ResolveInstallRoot();
        var templatePath = Path.Combine(userDataPath, "Characters", "User00000.iff");
        if (!File.Exists(templatePath))
            templatePath = Path.Combine(installRoot, "UserData", "Characters", "User00000.iff");
        if (!File.Exists(templatePath))
            templatePath = Path.Combine(installRoot, "TemplateUserData", "Characters", "User00000.iff");
        if (!File.Exists(templatePath))
            templatePath = Path.Combine(installRoot, "TemplateUserData", "UserData", "Characters", "User00000.iff");
        if (!File.Exists(templatePath))
        {
            var candidates = Directory.GetFiles(charDir, "User*.iff");
            var fallback = candidates.FirstOrDefault(file =>
            {
                try
                {
                    var iff = IffFile.Open(file);
                    if (iff.OfType(ResourceType.Objd).FirstOrDefault() is not { } objdChunk) return false;
                    var bodyId = Objd.Read(iff, objdChunk)[ObjdField.BodyStringsId];
                    if (bodyId == 0 || iff.Find(ResourceType.Str, bodyId) is not { } strChunk) return true;
                    var strings = StringTable.Read(iff, strChunk).ForLanguage(1);
                    var mode = strings.Count > 12 ? strings[12].Trim().ToLowerInvariant() : "";
                    return !mode.StartsWith("dog", StringComparison.Ordinal) && !mode.StartsWith("cat", StringComparison.Ordinal);
                }
                catch
                {
                    return false;
                }
            }) ?? candidates.FirstOrDefault();
            if (fallback != null) templatePath = fallback;
        }
        if (!File.Exists(templatePath))
            throw new FileNotFoundException($"Cannot find character template to clone in {userDataPath} or {game}.");

        var templateIff = IffFile.Open(templatePath);
        var memberGuids = new List<uint>();
        var memberNeighborIds = new List<short>();

        if (world is null)
        {
            neighborhoodPath = Path.Combine(userDataPath, "Neighborhood.iff");
            neighborhood = File.Exists(neighborhoodPath) ? IffFile.Open(neighborhoodPath) : null;
        }
        else
        {
            neighborhoodPath ??= Path.Combine(userDataPath, "Neighborhood.iff");
            if (neighborhood is null && File.Exists(neighborhoodPath))
                neighborhood = IffFile.Open(neighborhoodPath);
        }

        var nbrsChunk = neighborhood?.OfType(ResourceType.Parse("NBRS")).FirstOrDefault();
        var existingNeighbors = nbrsChunk is not null ? Neighbors.Read(neighborhood!, nbrsChunk) : null;
        var neighborEntries = existingNeighbors?.Entries
            .Where(e => registry is null || registry.Find(e.Guid) is not null)
            .ToList() ?? new List<Neighbor>();
        var usedNeighborIds = new HashSet<short>(neighborEntries.Select(e => e.NeighborId));
        var usedSlots = new HashSet<int>(neighborEntries.Select(e => e.Slot));

        var existingFamilies = neighborhood?.OfType(ResourceType.Parse("FAMI"))
            .Select(c => Family.Read(neighborhood!, c)).ToList() ?? new List<Family>();
        var usedFamilyChunkIds = new HashSet<int>(neighborhood?.OfType(ResourceType.Parse("FAMI")).Select(c => (int)c.Id) ?? []);
        var familyChunkId = 1;
        while (usedFamilyChunkIds.Contains(familyChunkId)) familyChunkId++;
        var familyNumber = existingFamilies.Count == 0 ? 1 : existingFamilies.Max(f => f.FamilyNumber) + 1;

        var requestedHouse = request.TryGetProperty("house", out var houseProp) && houseProp.ValueKind != JsonValueKind.Null
            ? (houseProp.TryGetInt16(out var parsedHouse) ? parsedHouse : throw new ArgumentException("house must be a 16-bit integer"))
            : (short)0;
        var familyBudget = 20_000;
        var targetHouse = (short)0;
        var lotPrice = 0;
        if (moveIn)
        {
            targetHouse = requestedHouse > 0 ? requestedHouse : house;
            if (targetHouse <= 0)
                throw new InvalidOperationException("Moving in requires a target residential lot");
            if (world?.SavedSimulationState is { } simi && targetHouse == house)
            {
                var (furnishingsValue, architectureValue) = CurrentLotValuation(simi);
                lotPrice = Math.Max(0, simi.Field0x9C + furnishingsValue + (int)(architectureValue * (world.ArchitectureValueMultiplier ?? 0.7f)));
            }
            else
            {
                var targetHousePath = Path.Combine(userDataPath, "Houses", $"House{targetHouse:D2}.iff");
                if (!File.Exists(targetHousePath))
                    throw new FileNotFoundException($"House{targetHouse:D2}.iff not found in {userDataPath}");
                var targetHouseFile = IffFile.Open(targetHousePath);
                if (targetHouseFile.Find(ResourceType.Parse("SIMI"), 1) is { } targetSimiChunk)
                {
                    var targetSimi = SimulationState.Read(targetHouseFile, targetSimiChunk);
                    lotPrice = Math.Max(0, targetSimi.Field0x9C + targetSimi.Field0xA0 + (int)(targetSimi.Field0xA4 * 0.7f));
                }
            }
            if (lotPrice > 20_000)
                throw new InvalidOperationException("The selected lot costs more than a new household can afford");
            familyBudget = 20_000 - lotPrice;
        }

        var charNumber = 1;
        var existingGuids = new HashSet<uint>(neighborEntries.Select(e => e.Guid));
        if (world != null)
        {
            foreach (var g in world.Neighbors.Values.Select(n => unchecked((uint)n.TypeRecordKey))) existingGuids.Add(g);
        }

        foreach (var member in members)
        {
            while (File.Exists(Path.Combine(charDir, $"User{charNumber:D5}.iff")))
            {
                charNumber++;
                if (charNumber > 32000) charNumber = 1;
            }
            var charFileName = $"User{charNumber:D5}.iff";
            var charPath = Path.Combine(charDir, charFileName);
            charNumber++;

            uint newGuid;
            do
            {
                var timePart = (uint)Environment.TickCount;
                var randPart = (uint)(Random.Shared.Next() & 0xffff);
                newGuid = (timePart << 16) ^ randPart;
            } while (newGuid == 0 || !existingGuids.Add(newGuid));

            memberGuids.Add(newGuid);

            var objdChunk = templateIff.Find(ResourceType.Parse("OBJD"), 128)
                ?? throw new InvalidDataException("Template person has no OBJD #128 chunk.");
            var objdBytes = objdChunk.Data.ToArray();
            var templateGuid = BinaryPrimitives.ReadUInt32LittleEndian(objdBytes.AsSpan(0x1c, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(objdBytes.AsSpan(0x8c, 4), templateGuid);
            BinaryPrimitives.WriteUInt32LittleEndian(objdBytes.AsSpan(0x1c, 4), newGuid);

            var ctssChunk = templateIff.Find(ResourceType.Parse("CTSS"), 2000);
            byte[] ctssBytes;
            if (ctssChunk is not null)
            {
                var ctssTable = StringTable.Read(templateIff, ctssChunk);
                ctssBytes = UpdateCtssStrings(ctssTable, member.Name, member.Bio);
            }
            else
            {
                var ctssEncoding = ResourceReader.EncodingForLanguage(1);
                using var ms = new MemoryStream();
                using var w = new BinaryWriter(ms);
                w.Write((short)-3);
                w.Write((short)2);
                w.Write((byte)1);
                w.Write(ctssEncoding.GetBytes(member.Name));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((byte)1);
                w.Write(ctssEncoding.GetBytes(member.Bio));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Flush();
                ctssBytes = ms.ToArray();
            }

            var isChild = member.Child;
            var isFemale = member.Female;
            var strChunk = templateIff.Find(ResourceType.Parse("STR#"), 200);
            var strTable = strChunk is not null ? StringTable.Read(templateIff, strChunk) : null;
            var strBytes = UpdateBodystrings(strTable, isChild, isFemale, member.BodyChoice, member.HeadChoice, member.Skin);

            var newCharIff = templateIff.WithChunkDataResized(ResourceType.Parse("OBJD"), 128, objdBytes);
            if (ctssChunk is not null)
                newCharIff = newCharIff.WithChunkDataResized(ctssChunk.Type, ctssChunk.Id, ctssBytes);
            newCharIff = strChunk is not null
                ? newCharIff.WithChunkDataResized(strChunk.Type, strChunk.Id, strBytes)
                : newCharIff.WithAddedOrReplacedChunk(ResourceType.Parse("STR#"), 200, strBytes);

            if (registry is not null)
            {
                try
                {
                    var genderCode = isFemale ? "f" : "m";
                    var ageCode = isChild ? "child" : "adult";
                    var catalog = HouseholdAppearance.Catalog(game, registry, genderCode, ageCode, member.Skin);
                    var headChoice = !string.IsNullOrWhiteSpace(member.HeadChoice) ? member.HeadChoice! : catalog.Heads[0].Id;
                    var bodyChoice = !string.IsNullOrWhiteSpace(member.BodyChoice) ? member.BodyChoice! : catalog.Bodies[0].Id;
                    var previewPerson = HouseholdAppearancePreview.CreatePreviewPerson(game, registry, genderCode, ageCode, member.Skin, headChoice, bodyChoice);
                    var headMeshes = AvatarSceneGeometry.For(game, registry).BuildHeadMeshes(previewPerson, 1, 0, (_, _) => 0, new LotCamera(0, 128, 128));
                    newCharIff = SimPortrait.WithPortraits(newCharIff, headMeshes);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"SimsSimulation: portrait for {member.Name} failed: {error.Message}");
                }
            }

            WriteReplace(charPath, newCharIff.Bytes);
            registry?.RegisterFile(newCharIff, charPath);

            var nextSlot = 0;
            while (usedSlots.Contains(nextSlot) || usedNeighborIds.Contains((short)(nextSlot + 1)))
                nextSlot++;
            var nextNeighborId = checked((short)(nextSlot + 1));
            usedSlots.Add(nextSlot);
            usedNeighborIds.Add(nextNeighborId);
            memberNeighborIds.Add(nextNeighborId);
            globallyRemovedNeighbors.Remove(nextNeighborId);
            world?.ClearRemovedNeighbor(nextNeighborId);
            foreach (var live in lotPool.LiveSessions.Values)
                live.World.ClearRemovedNeighbor(nextNeighborId);

            // NBRS record version 0x49 stores 88 16-bit integers of serialized person data.
            var personData = new short[88];
            if (member.Personality.Length >= 5)
            {
                personData[(int)PersonData.NeatQuietPersonality] = member.Personality[0];
                personData[(int)PersonData.OutgoingFriendlyPersonality] = member.Personality[1];
                personData[(int)PersonData.ActivePlayfulPersonality] = member.Personality[2];
                personData[(int)PersonData.PlayfulSmartPersonality] = member.Personality[3];
                personData[(int)PersonData.NiceLoyalPersonality] = member.Personality[4];
            }
            personData[(int)PersonData.PersonAge] = isChild ? (short)9 : (short)27;
            var skinColor = member.Skin.ToLowerInvariant() switch
            {
                "med" => (short)2,
                "drk" => (short)3,
                _ => (short)1,
            };
            personData[59] = skinColor;
            personData[(int)PersonData.SkinColor] = skinColor;
            personData[(int)PersonData.FamilyNumber] = (short)familyChunkId;
            personData[(int)PersonData.Gender] = isFemale ? (short)1 : (short)0;
            personData[(int)PersonData.LingeringHouseNumber] = 0;
            var zodiac = member.Zodiac is >= 1 and <= 12
                ? member.Zodiac.Value
                : (short)(Neighborhoods.ComputeExportZodiac(personData) ?? 1);
            personData[45] = zodiac;
            personData[(int)PersonData.Zodiac] = zodiac;

            var neighbor = new Neighbor(
                Slot: nextSlot,
                RecordVersion: 10,
                Name: member.Name,
                Field0x1C: 0,
                PersonData: personData,
                NeighborId: nextNeighborId,
                Guid: newGuid,
                Relationships: new Dictionary<int, IReadOnlyList<int>>(),
                StoredFormat: 10,
                PersonDataTrailingWord: 0);

            neighborEntries.Add(neighbor);
        }

        if (neighborhood is not null)
        {
            var newFamily = Family.CreateNew(checked((short)familyNumber), targetHouse, memberGuids, familyBudget)
                .WithValue(moveIn ? lotPrice : 0);
            var famiBytes = newFamily.Write((ushort)familyChunkId);
            neighborhood = neighborhood.WithAddedOrReplacedChunk(ResourceType.Parse("FAMI"), (ushort)familyChunkId, famiBytes, 0, familyName);

            var famsBytes = CreateFAMsChunk(familyName);
            neighborhood = neighborhood.WithAddedOrReplacedChunk(ResourceType.Parse("FAMs"), (ushort)familyChunkId, famsBytes, 0, familyName);

            var maxSlot = neighborEntries.Count == 0 ? 0 : neighborEntries.Max(e => e.Slot) + 1;
            var updatedSlotCount = Math.Max(existingNeighbors?.SlotCount ?? 0, maxSlot);
            var nbrsVersion = existingNeighbors?.Version ?? 73;
            var updatedNbrs = new Neighbors(nbrsVersion, updatedSlotCount, neighborEntries);
            var nbrsBytes = updatedNbrs.WriteChunkData(swap: false);
            neighborhood = neighborhood.WithAddedOrReplacedChunk(ResourceType.Parse("NBRS"), 1, nbrsBytes);

            if (neighborhoodPath is not null)
            {
                WriteReplace(neighborhoodPath, neighborhood.Bytes);
            }
            if (ConnectedNeighborhoodEnabled)
            {
                SyncAllLiveWorldsFromNeighborhood(globallyRemovedNeighbors);
                neighborhoodManager.SyncFromUserData(userDataPath, language);
            }
        }

        if (moveIn)
        {
            int? centerX = request.TryGetProperty("centerX", out var cxProp) && cxProp.TryGetInt32(out var cxVal)
                ? cxVal : null;
            int? centerY = request.TryGetProperty("centerY", out var cyProp) && cyProp.TryGetInt32(out var cyVal)
                ? cyVal : null;
            string? memberInstantiation;
            if (world is not null && targetHouse == house)
            {
                memberInstantiation = ReloadAfterHouseholdCreation((ushort)familyChunkId, centerX ?? LotWidth / 2, centerY ?? LotHeight / 2);
            }
            else
            {
                OpenLotInternal(targetHouse, moveInFamily: null, activeFamilyOverride: (ushort)familyChunkId,
                    spawnCenter: (centerX, centerY), out memberInstantiation);
            }
            return new { family = familyChunkId, house = targetHouse, budget = familyBudget, members = members.Length, memberInstantiation };
        }

        return new { family = familyChunkId, house = targetHouse, budget = familyBudget, members = members.Length };
    }

    private object MoveInFamily(JsonElement request)
    {
        if (speed != 0 || simulatorBlocked || HasBlockingDialog(world))
            throw new InvalidOperationException("Moving in requires a paused residential lot without a blocking dialog or active trip");
        var requestedFamilyId = checked((ushort)request.GetProperty("family").GetInt32());
        var centerX = request.TryGetProperty("centerX", out var cxProp) && cxProp.TryGetInt32(out var cxVal)
            ? cxVal : LotWidth / 2;
        var centerY = request.TryGetProperty("centerY", out var cyProp) && cyProp.TryGetInt32(out var cyVal)
            ? cyVal : LotHeight / 2;
        var (familyId, targetHouse, budget, memberCount, memberInstantiation) = MoveInFamilyCore(requestedFamilyId, centerX, centerY);
        return new { family = (int)familyId, house = targetHouse, budget, members = memberCount, memberInstantiation };
    }

    private (ushort FamilyId, short House, int Budget, int Members, string MemberInstantiation) MoveInFamilyCore(
        ushort requestedFamilyId, int centerX, int centerY)
    {
        var userDataPath = Neighborhoods.UserData(profile);
        var nghPath = neighborhoodPath ?? Path.Combine(userDataPath, "Neighborhood.iff");
        if (!File.Exists(nghPath)) throw new FileNotFoundException($"Neighborhood.iff not found in {userDataPath}");
        neighborhood ??= IffFile.Open(nghPath);
        neighborhoodPath = nghPath;
        var famiChunk = neighborhood.Find(ResourceType.Parse("FAMI"), requestedFamilyId)
            ?? throw new ArgumentException("The selected family does not exist");
        var family = Family.Read(neighborhood, famiChunk);
        if (family.HouseNumber != 0)
            throw new InvalidOperationException("The selected family already lives on a lot");
        var lotPrice = 0;
        if (world?.SavedSimulationState is { } simi)
        {
            var (furnishingsValue, architectureValue) = CurrentLotValuation(simi);
            lotPrice = Math.Max(0, simi.Field0x9C + furnishingsValue + (int)(architectureValue * (world?.ArchitectureValueMultiplier ?? 0.7f)));
        }
        if (lotPrice > family.Budget)
            throw new InvalidOperationException("The selected family cannot afford this lot");
        var updatedFamily = family.WithHouseNumber(house).WithBudget(family.Budget - lotPrice).WithValue(lotPrice);
        neighborhood = neighborhood.WithAddedOrReplacedChunk(ResourceType.Parse("FAMI"), requestedFamilyId,
            updatedFamily.Write(requestedFamilyId), famiChunk.Flags, famiChunk.Label);
        WriteReplace(nghPath, neighborhood.Bytes);
        var memberInstantiation = ReloadAfterHouseholdCreation(requestedFamilyId, centerX, centerY);
        return (requestedFamilyId, house, updatedFamily.Budget, updatedFamily.Members.Count, memberInstantiation);
    }

    internal static void SetLanguageStringEntry(List<StringEntry> entries, int oneBasedIndex, string value)
    {
        var target = entries.Any(e => e.Language == 1) ? (byte)1 : (byte)0;
        var seen = 0;
        var at = entries.FindIndex(e => (e.Language == target || target == 0) && ++seen == oneBasedIndex);
        if (at >= 0)
        {
            entries[at] = entries[at] with { Value = value };
            return;
        }

        var insertLanguage = target == 0 ? (byte)1 : target;
        while (seen < oneBasedIndex - 1)
        {
            entries.Add(new StringEntry("", "", insertLanguage));
            seen++;
        }
        entries.Add(new StringEntry(value, "", insertLanguage));
    }

    internal static byte[] UpdateCtssStrings(StringTable table, string name, string bio)
    {
        var entries = table.Entries.ToList();
        SetLanguageStringEntry(entries, 1, name);
        SetLanguageStringEntry(entries, 2, bio);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((short)-3);
        writer.Write(checked((short)entries.Count));
        foreach (var entry in entries)
        {
            var encoding = ResourceReader.EncodingForLanguage(entry.Language);
            writer.Write(entry.Language);
            writer.Write(encoding.GetBytes(entry.Value));
            writer.Write((byte)0);
            writer.Write(encoding.GetBytes(entry.Comment));
            writer.Write((byte)0);
        }
        writer.Flush();
        return output.ToArray();
    }

    private byte[] UpdateBodystrings(StringTable? table, bool isChild, bool isFemale, string? bodyChoice, string? headChoice, string skin) =>
        HouseholdAppearance.BuildCharacterBodyStrings(table, game, registry, isChild, isFemale, bodyChoice, headChoice, skin);

    private static byte[] CreateFAMsChunk(string familyName)
    {
        var encoding = ResourceReader.EncodingForLanguage(1);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((short)-3);
        writer.Write((short)1);
        writer.Write((byte)1);
        writer.Write(encoding.GetBytes(familyName));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Flush();
        return stream.ToArray();
    }

    private bool IsAwayLot(string userData, short targetHouse) =>
        suspendedHomeLot is not null && suspendedHomeLot.HomeHouse != targetHouse ||
        targetHouse is (>= 21 and <= 30) or (>= 40 and <= 49) or (>= 81 and <= 89) or (>= 93 and <= 99) ||
        (!string.IsNullOrEmpty(userData) && Neighborhoods.IsCommunityLot(userData, targetHouse));

    private short EffectiveHomeHouse =>
        ConnectedNeighborhoodEnabled
            ? EffectiveHomeHouseValue()
            : suspendedHomeLot?.HomeHouse ??
              (world is not null && house > 0 && world[SimulationGlobal.SimlessBuildMode] == 0 && !IsVisitingAwayLot ? house : (short)0);

    private bool IsVisitingAwayLot =>
        ConnectedNeighborhoodEnabled
            ? IsVisitingAwayLotValue()
            : world is not null &&
              house != 0 &&
              ((suspendedHomeLot is not null && suspendedHomeLot.HomeHouse != house) ||
               (world[SimulationGlobal.SimlessBuildMode] == 0 &&
                IsAwayLot(Path.GetDirectoryName(neighborhoodPath) ?? Neighborhoods.UserData(profile), house)));

    private void EnsureLotEditingAllowed()
    {
        if (IsVisitingAwayLot)
            throw new InvalidOperationException("Buy and Build modes are disabled while visiting an away lot.");
        if (world is { WorldLotListEntries.Count: > 0 } active &&
            active.WorldLotListEntries.Any(item => ReferenceEquals(active.ObjectById(item.Id), item)))
            throw new InvalidOperationException("Buy and Build modes are disabled during an active emergency.");
    }

    private void ClampAwayLotEditingState(bool forceAway = false)
    {
        if (!forceAway && !IsVisitingAwayLot) return;
        editingView = false;
        doorWindowBuildToolActive = false;
        if (viewerMode is 1 or 2)
            viewerMode = 0;
        if (world is not null)
            world[SimulationGlobal.Mode] = viewerMode;
    }

    private static SaveRuntimeMetadata CaptureRuntimeMetadata(SimulationWorld currentWorld, ReadOnlySpan<byte> lotBytes, int saveSpeed,
        SavedSuspendedHomeLot? suspendedHome = null)
    {
        var animations = currentWorld.Objects.Values.OfType<XPerson>().OrderBy(person => person.Id).Select(person =>
        {
            var stack = currentWorld.SavedExecutionStacks.GetValueOrDefault(person.Id);
            return new PersonAnimationRuntimeSnapshot(person.Id, person.AnimationPlayback?.SaveState(),
                person.BackgroundAnimationPlayback?.SaveState(), person.CarryAnimationPlayback?.SaveState(),
                person.InteractionAttachments.Order(StringComparer.Ordinal).ToArray(),
                RoutePersistence.Capture(person, stack), person.BackgroundPracticePersonState,
                person.AnimatorWalkingScale, person.PendingAnimationEvents.ToArray(), person.AnimatorControlState,
                person.AnimatorRootX, person.AnimatorRootY, person.AnimatorPoolTransitionState,
                person.AnimatorGaitEntryState, person.AnimatorMilliseconds, person.AnimatorPoolTile,
                person.AnimatorRootDirection, person.AnimatorDisplacementX, person.AnimatorDisplacementY,
                person.AnimatorDisplacementDirection, person.AnimatorRouteTransitionState,
                person.AnimatorStateTailField198, person.AnimatorCurrentSkill,
                person.AnimatorPracticeScaleEnabled);
        }).ToArray();
        var savedDialogs = currentWorld.DialogRecords.Select(record => new SavedDialogSnapshot(
            record.Token, record.Owner.Id, record.Icon?.Id, record.Kind, record.Title, record.Message,
            record.Yes, record.No, record.Cancel, record.Blocking, record.AwaitsResponse, record.State,
            record.Visible, record.ImageBitmapId, record.BookMode)).ToArray();
        var restrictedKeys = (currentWorld.FamilyBudget as FamilyBudget)?.RestrictedActivePersonKeys?.ToArray();
        return new SaveRuntimeMetadata(14, saveSpeed, animations,
            Convert.ToHexString(SHA256.HashData(lotBytes)), savedDialogs,
            currentWorld.FamilyBudget?.ReadFamilyBudget(), restrictedKeys, suspendedHome,
            currentWorld.Clock.Ticks, currentWorld.Clock.Day);
    }

    private static int TravelFilterSlotForMode(int mode) => mode switch
    {
        2 => 1,
        3 => 2,
        4 => 3,
        5 => 4,
        7 => 5,
        _ => TravelFilter.Unleashed,
    };

    private static void SetInMemoryFamilyVacationField(SimulationWorld targetWorld, ushort familyChunkId, int vacationHouse)
    {
        if (targetWorld.FamilyBudget is FamilyBudget fb &&
            fb.FamilyChunkId == familyChunkId &&
            fb.ActiveFamily is { } fam &&
            fam.Field0x13C != vacationHouse)
        {
            fb.ActiveFamily = fam.WithFields(field0x13C: vacationHouse);
        }
    }

    private void ReconcileCommittedVacationFieldOnDisk()
    {
        if (committedVacationState is not { } committed ||
            neighborhoodPath is null ||
            !File.Exists(neighborhoodPath))
        {
            return;
        }
        try
        {
            var currentNgh = neighborhood ?? IffFile.Open(neighborhoodPath);
            var famiType = ResourceType.Parse("FAMI");
            if (currentNgh.Find(famiType, committed.FamilyChunkId) is not { } chunk) return;
            var currentFam = Family.Read(currentNgh, chunk);
            if (currentFam.Field0x13C == committed.VacationHouse) return;
            var updated = currentNgh.WithAddedOrReplacedChunk(famiType, committed.FamilyChunkId,
                currentFam.WithFields(field0x13C: committed.VacationHouse).Write(committed.FamilyChunkId));
            WriteReplace(neighborhoodPath, updated.Bytes);
            neighborhood = updated;
        }
        catch
        {
        }
    }

    private void RestoreTravelRuntimeState(SimulationWorld targetWorld, string userData, short loadedHouse,
        ushort? loadedFamilyChunkId, Family? loadedFamily, SaveRuntimeMetadata runtime, SaveEnvelope? envelope = null)
    {
        var savedHome = envelope?.SuspendedHome ?? runtime.SuspendedHome;
        if (savedHome is not null)
        {
            suspendedHomeLot = new SuspendedHomeLot(savedHome.HomeHouse, savedHome.HomeFamilyChunkId,
                Convert.FromBase64String(savedHome.LotState), savedHome.Runtime, savedHome.Speed);
        }
        else if (loadedFamily is { HouseNumber: > 0 } fam &&
                 loadedHouse != fam.HouseNumber &&
                 IsAwayLot(userData, loadedHouse))
        {
            var homePath = Path.Combine(userData, "Houses", $"House{(short)fam.HouseNumber:D2}.iff");
            if (File.Exists(homePath))
            {
                var homeBytes = File.ReadAllBytes(homePath);
                var homeRuntime = ReadSavedRuntimeMetadata(CanonicalRuntimePath(homePath), homeBytes);
                suspendedHomeLot = new SuspendedHomeLot((short)fam.HouseNumber, loadedFamilyChunkId,
                    homeBytes, homeRuntime, homeRuntime.Speed);
            }
            else
            {
                suspendedHomeLot = null;
            }
        }
        else
        {
            suspendedHomeLot = null;
        }

        if (targetWorld.FamilyBudget is FamilyBudget budget)
        {
            var restrictedKeys = envelope?.RestrictedActivePersonKeys ?? runtime.RestrictedActivePersonKeys;
            if (restrictedKeys is { Count: > 0 })
            {
                budget.RestrictActivePersonKeys(restrictedKeys);
            }
            else if (runtime.Version < 14 &&
                     suspendedHomeLot is not null &&
                     loadedHouse is not (>= 40 and <= 49) &&
                     budget.ActiveFamily is { Members.Count: > 1 } legacyFamily)
            {
                var presentKeys = legacyFamily.Members
                    .Select(static m => unchecked((int)m))
                    .Where(k => targetWorld.WalkOrder.OfType<XPerson>().Any(p => p.TypeRecord?.Key == k))
                    .ToArray();
                if (presentKeys.Length > 0 && presentKeys.Length < legacyFamily.Members.Count)
                    budget.RestrictActivePersonKeys(presentKeys);
            }
        }

        var diskField0x13C = loadedFamily?.Field0x13C ?? 0;
        var diskNghPath = neighborhoodPath ?? Path.Combine(userData, "Neighborhood.iff");
        if (loadedFamilyChunkId is { } checkChunkId && File.Exists(diskNghPath))
        {
            try
            {
                var diskNgh = IffFile.Open(diskNghPath);
                if (diskNgh.Find(ResourceType.Parse("FAMI"), checkChunkId) is { } diskChunk)
                    diskField0x13C = Family.Read(diskNgh, diskChunk).Field0x13C;
            }
            catch
            {
            }
        }
        committedVacationState = loadedFamilyChunkId is { } committedChunkId ? (committedChunkId, diskField0x13C) : null;

        if (loadedHouse is >= 40 and <= 49 &&
            suspendedHomeLot is not null &&
            loadedFamilyChunkId is { } vacFamId &&
            loadedFamily is { Field0x13C: 0 })
        {
            SetInMemoryFamilyVacationField(targetWorld, vacFamId, loadedHouse);
        }
    }

    private bool RefreshPendingPersonPortraits()
    {
        if (registry is null || string.IsNullOrEmpty(game)) return false;
        HashSet<uint>? candidates = null;
        var drained = WardrobeSuitService.DrainPendingPortraitGuids(registry);
        if (drained.Length > 0)
            candidates = [.. drained];

        if (registry.DirtyPeopleGuids.Count > 0)
        {
            foreach (var dirtyGuid in registry.DirtyPeopleGuids)
            {
                if (registry.Find(dirtyGuid) is { Scan: ObjectScan.People } dirtyEntry &&
                    (!refreshedPersonFiles.TryGetValue(dirtyGuid, out var lastRefreshed) || !ReferenceEquals(lastRefreshed, dirtyEntry.File)))
                {
                    candidates ??= [];
                    candidates.Add(dirtyGuid);
                }
            }
        }
        if (candidates is null || candidates.Count == 0) return false;

        var refreshedAny = false;
        var geometry = AvatarSceneGeometry.For(game, registry);
        foreach (var guid in candidates)
        {
            if (registry.Find(guid) is not { Scan: ObjectScan.People } entry) continue;
            var livePerson = world?.WalkOrder.OfType<XPerson>().FirstOrDefault(p => unchecked((uint)(p.TypeRecord?.Key ?? 0)) == guid);
            var targetPerson = livePerson is { SavedCharacter: not null } ? livePerson : CreatePortraitPersonFromEntry(entry, language);
            if (targetPerson is null) continue;

            var savedPresentation = targetPerson.PresentationCharacter;
            var savedOutfit = targetPerson[PersonData.CurrentOutfit];
            try
            {
                targetPerson.PresentationCharacter = null;
                targetPerson[PersonData.CurrentOutfit] = 0;
                var headMeshes = geometry.BuildHeadMeshes(targetPerson, 1, 0, static (_, _) => 0, new LotCamera(0, 128, 128));
                if (headMeshes.Count == 0) continue;
                var updatedIff = SimPortrait.WithPortraits(entry.File, headMeshes);
                registry.ReplaceEntryFile(guid, updatedIff, markDirty: true);
                images?.SetRegistry(registry);
                refreshedPersonFiles[guid] = updatedIff;
                if (livePerson is not null && pieHead?.Sim == livePerson.Id)
                    pieHead = null;
                refreshedAny = true;
            }
            catch
            {
            }
            finally
            {
                targetPerson.PresentationCharacter = savedPresentation;
                targetPerson[PersonData.CurrentOutfit] = savedOutfit;
            }
        }
        return refreshedAny;
    }

    private static XPerson? CreatePortraitPersonFromEntry(ObjectEntry entry, byte language)
    {
        var stringsId = entry.Definition[ObjdField.BodyStringsId];
        var strChunk = stringsId == 0 ? null : entry.File.Find(ResourceType.Str, stringsId);
        if (strChunk is null) return null;
        var values = StringTable.Read(entry.File, strChunk).ForLanguage(language);
        static string StrAt(IReadOnlyList<string> list, int oneBased) =>
            oneBased >= 1 && oneBased <= list.Count ? list[oneBased - 1].Trim() : string.Empty;
        static (string, string) Pair(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec) || spec.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                return (string.Empty, string.Empty);
            var comma = spec.IndexOf(',');
            var mesh = (comma >= 0 ? spec[..comma] : spec).Trim();
            var tex = comma >= 0 ? spec[(comma + 1)..].Trim() : string.Empty;
            if (mesh.EndsWith(".apr", StringComparison.OrdinalIgnoreCase))
                mesh = mesh[..^4];
            return (mesh, tex);
        }

        var bodySpec = StrAt(values, 0x02);
        if (bodySpec.Length == 0) return null;
        var headSpec = StrAt(values, 0x03);
        var leftHandSpec = StrAt(values, 0x12);
        var rightHandSpec = StrAt(values, 0x13);
        var genderRaw = StrAt(values, 0x0d).ToLowerInvariant();
        var genderMode = genderRaw switch
        {
            "male" => 0,
            "female" => 1,
            "dogmale" or "dog_male" => 2,
            "dogfemale" or "dog_female" => 3,
            "catmale" or "cat_male" => 4,
            "catfemale" or "cat_female" => 5,
            _ => 0,
        };
        var isChild = StrAt(values, 0x01).Equals("child", StringComparison.OrdinalIgnoreCase);
        var parts = Enumerable.Repeat((string.Empty, string.Empty), 5).ToArray();
        parts[0] = Pair(bodySpec);
        if (genderMode is < 2 or > 5)
        {
            parts[2] = Pair(leftHandSpec);
            parts[3] = Pair(rightHandSpec);
            parts[4] = Pair(headSpec);
        }
        var character = new PersonCharacter(0, 0, parts, [], [string.Empty, string.Empty, string.Empty], 3, new float[9]);
        var person = new XPerson(1, new short[XObject.DefinitionCount])
        {
            TypeRecord = new TypeRecord(unchecked((int)entry.Guid)),
            SavedCharacter = character,
            Position = new ObjectPosition(64 * 16, 64 * 16, 1),
            AnimatorBodyStringMode = (short)genderMode,
        };
        person[PersonData.PersonAge] = (short)(isChild ? 9 : 27);
        person[ObjectData.Direction] = 3;
        return person;
    }

    private (ushort? ChunkId, Family? Family) ResolveTravellingFamily(SimulationWorld sourceWorld,
        SimulationWorld.LotSwitchRequest request)
    {
        if (activeFamilyChunkId is { } activeChunkId)
        {
            var liveFamily = (sourceWorld.FamilyBudget as FamilyBudget)?.ActiveFamily;
            if (liveFamily is not null) return (activeChunkId, liveFamily);
            if (neighborhood?.Find(ResourceType.Parse("FAMI"), activeChunkId) is { } activeChunk)
                return (activeChunkId, Family.Read(neighborhood, activeChunk));
        }

        if (neighborhood is not null)
        {
            foreach (var chunk in neighborhood.OfType(ResourceType.Parse("FAMI")))
            {
                var candidate = Family.Read(neighborhood, chunk);
                if (request.PersonGuid != 0 && candidate.Members.Contains(unchecked((uint)request.PersonGuid)))
                    return (chunk.Id, candidate);
                if (request.FamilyNumber != 0 && (candidate.FamilyNumber == request.FamilyNumber || chunk.Id == request.FamilyNumber))
                    return (chunk.Id, candidate);
            }
        }
        return (activeFamilyChunkId, null);
    }

    private void UpdateFamilyVacationField(ushort familyChunkId, int vacationHouse)
    {
        if (neighborhood is null || neighborhoodPath is null) return;
        var famiType = ResourceType.Parse("FAMI");
        if (neighborhood.Find(famiType, familyChunkId) is not { } chunk) return;
        var current = Family.Read(neighborhood, chunk);
        if (current.Field0x13C == vacationHouse) return;
        var updatedFamily = current.WithFields(field0x13C: vacationHouse);
        var updated = neighborhood.WithAddedOrReplacedChunk(famiType, familyChunkId, updatedFamily.Write(familyChunkId));
        WriteReplace(neighborhoodPath, updated.Bytes);
        neighborhood = updated;
    }

    private static (int? Hour, int? Minute) ConsumeArrivalClock(
        Dictionary<short, InteractionScopeTable> clonedScopes, short targetHouse)
    {
        int? hour = null;
        int? minute = null;
        foreach (var scope in clonedScopes.Values)
        {
            short hourCursor = -1;
            if (scope.TryFindNext(2, ref hourCursor, 7, out var hourWord))
            {
                hour ??= hourWord;
                scope.TryAdvance(hourCursor, -1);
            }

            short minuteCursor = -1;
            if (scope.TryFindNext(2, ref minuteCursor, 8, out var minuteWord))
            {
                minute ??= minuteWord;
                scope.TryAdvance(minuteCursor, -1);
            }
        }
        if (targetHouse == 99 && hour is null && minute is null)
            return (12, 0);
        if (hour is null && minute is not null) hour = 0;
        if (minute is null && hour is not null) minute = 0;
        return (hour, minute);
    }

    private static List<int> CollectTravelScopeKeys(
        Dictionary<int, short> sourceSlots, Dictionary<short, InteractionScopeTable> clonedScopes)
    {
        var keys = new List<int>();
        foreach (var (key, sourceSlot) in sourceSlots)
        {
            if (key == 0 || !clonedScopes.TryGetValue(sourceSlot, out var scope)) continue;
            short cursor = -1;
            while (scope.TryFindNext(2, ref cursor, 0, out _) && scope.TryGet(cursor, out var entry))
            {
                if (entry.Mode is (>= 0 and <= 6) or (>= 9 and <= 12))
                {
                    keys.Add(key);
                    break;
                }
            }
        }
        return keys;
    }

    private static void SyncPersonFromNeighborRecord(SimulationWorld targetWorld, XPerson person)
    {
        if (person.TypeRecord?.Key is not { } typeKey) return;
        var neighbor = targetWorld.Neighbors.Values.FirstOrDefault(n => n.TypeRecordKey == typeKey);
        if (neighbor is null) return;

        targetWorld.BindPersonToNeighborRecord(person);
        if (!string.IsNullOrWhiteSpace(neighbor.Name) &&
            !IsInternalUserLabel(neighbor.Name) &&
            !string.Equals(person.Name, neighbor.Name, StringComparison.Ordinal))
        {
            person.Name = neighbor.Name;
        }

        var neighborRelationships = neighbor.Relationships.Snapshot();
        foreach (var existingTarget in person.Relationships.Snapshot().Keys.ToArray())
        {
            if (!neighborRelationships.ContainsKey(existingTarget))
                person.Relationships.RemoveTarget(checked((short)existingTarget));
        }
        foreach (var (otherId, values) in neighborRelationships)
        {
            var targetId = checked((short)otherId);
            person.Relationships.Resize(targetId, values.Count);
            for (var index = 0; index < values.Count; index++)
                person.Relationships.Set(targetId, index, values[index]);
        }
    }

    private static bool ApplyTransferredScopeState(SimulationWorld targetWorld, XPerson person)
    {
        var scope = targetWorld.InteractionExecutorScope(person);
        var remaining = scope.CountValue(2);
        if (remaining <= 0) return false;

        var applied = false;
        short cursor = -1;
        while (remaining-- > 0 && scope.TryFindNext(2, ref cursor, 0, out _) && scope.TryGet(cursor, out var fetched))
        {
            var payload = unchecked((short)fetched.Result);
            var kind = fetched.Mode;
            var removeEntry = true;
            if ((uint)kind <= 6)
            {
                var selector = kind switch
                {
                    0 => 9,
                    1 => 6,
                    2 => 5,
                    3 => 15,
                    4 => 7,
                    5 => 8,
                    _ => 14,
                };
                person.SetInteractionExecutorFloat(selector, payload);
                if ((uint)selector < person.Motives.Length)
                    person.Motives[selector] = payload;
                applied = true;
            }
            else if (kind is >= 9 and <= 12)
            {
                var offset = kind switch
                {
                    9 => 0x59c,
                    10 => 0x62c,
                    11 => 0x630,
                    _ => 0x62e,
                };
                person.SetInteractionExecutorWord(offset, payload);
                applied = true;
            }
            else
            {
                removeEntry = false;
            }

            if (removeEntry)
            {
                var before = scope.CountValue(2);
                scope.TryAdvance(cursor, -1);
                if (scope.CountValue(2) < before) cursor--;
            }
        }
        return applied;
    }

    private void InstantiateMissingFamilyMembersOnReturnHome(SimulationWorld targetWorld, Family? activeFamily)
    {
        if (activeFamily is not { Members.Count: > 0 }) return;
        var addedAny = false;
        foreach (var guid in activeFamily.Members)
        {
            if (guid == 0) continue;
            var key = unchecked((int)guid);
            if (targetWorld.WalkOrder.OfType<XPerson>().Any(p => p.TypeRecord?.Key == key))
                continue;

            targetWorld.ScopedNeighborPersonRecordId(key);
            var created = targetWorld.WalkOrder.OfType<XPerson>().FirstOrDefault(p => p.TypeRecord?.Key == key);
            if (created is null && targetWorld.ObjectFactory?.Invoke(key) is XPerson fallbackPerson)
            {
                targetWorld.Add(fallbackPerson);
                targetWorld.InitializeCreatedPerson(fallbackPerson);
                targetWorld.InitializeCreatedObject(fallbackPerson);
                targetWorld.InitializeCreatedPerson(fallbackPerson);
                created = fallbackPerson;
            }

            if (created is not null)
            {
                addedAny = true;
                if (created.Position.X < 0 && created.Position.Y < 0 &&
                    targetWorld.FindGoodLocation(created, LotWidth / 2, LotHeight / 2, out var pos))
                {
                    targetWorld.PlaceCreatedObject(created, pos);
                }
            }
        }
        if (addedAny) targetWorld.RefreshNeighborInteractionManager();
    }

    private static void TransferScopes(SimulationWorld destWorld,
        Dictionary<int, short> sourceSlots, Dictionary<short, InteractionScopeTable> clonedScopes)
    {
        foreach (var (key, sourceSlot) in sourceSlots)
        {
            if (!clonedScopes.TryGetValue(sourceSlot, out var sourceScope)) continue;
            short targetSlot;
            if (destWorld.PersonTypeRecordSlots.TryGetValue(key, out var existingSlot))
            {
                targetSlot = existingSlot;
            }
            else if (!destWorld.PersonTypeRecordSlots.ContainsValue(sourceSlot) &&
                     !destWorld.InteractionScopes.ContainsKey(sourceSlot))
            {
                targetSlot = sourceSlot;
                destWorld.PersonTypeRecordSlots[key] = targetSlot;
            }
            else
            {
                short candidate = 1;
                while (destWorld.PersonTypeRecordSlots.ContainsValue(candidate) ||
                       destWorld.InteractionScopes.ContainsKey(candidate))
                    candidate++;
                targetSlot = candidate;
                destWorld.PersonTypeRecordSlots[key] = targetSlot;
            }

            XPerson? destBackedPerson = null;
            foreach (var obj in destWorld.WalkOrder)
            {
                if (obj.TypeRecord?.Key != key) continue;
                obj.InteractionScopeKey5CA = targetSlot;
                if (destBackedPerson is null && obj is XPerson p && p[PersonData.NeighborId] > 0)
                    destBackedPerson = p;
            }

            if (destBackedPerson is not null)
            {
                var destNeighborId = destBackedPerson[PersonData.NeighborId];
                var inventory = destWorld.NeighborInventory(destNeighborId);
                inventory.FilterOut(2);
                for (short i = 0; i < sourceScope.Count; i++)
                {
                    if (sourceScope.TryGet(i, out var entry) && entry.Value == 2)
                        inventory.Add(entry.Value, entry.Mode, unchecked((short)entry.Result));
                }
                var backed = new InteractionScopeTable(() => destWorld.NeighborInventory(destNeighborId));
                backed.AvatarObjects.AddRange(sourceScope.AvatarObjects);
                destWorld.InteractionScopes[targetSlot] = backed;
            }
            else
            {
                destWorld.InteractionScopes[targetSlot] = sourceScope;
            }
        }

        foreach (var (slot, scope) in clonedScopes)
        {
            if (!sourceSlots.ContainsValue(slot) && !destWorld.InteractionScopes.ContainsKey(slot))
                destWorld.InteractionScopes[slot] = scope;
        }
    }

    internal bool TryApplyPendingLotSwitch(SimulationWorld sourceWorld, SimulationWorld.LotSwitchRequest request)
    {
        if (ConnectedNeighborhoodEnabled)
            return TryApplyConnectedPendingLotSwitch(sourceWorld, request);
        var userData = Neighborhoods.UserData(profile);
        var (travellingFamilyChunkId, travellingFamily) = ResolveTravellingFamily(sourceWorld, request);
        var isReturnHome = request.RawTargetHouse == -1;
        var targetHouse = isReturnHome
            ? (suspendedHomeLot?.HomeHouse ?? checked((short)(travellingFamily?.HouseNumber ?? 0)))
            : request.RawTargetHouse;
        if (targetHouse <= 0) return false;

        var destPath = Path.Combine(userData, "Houses", $"House{targetHouse:D2}.iff");
        var returningHome = isReturnHome && suspendedHomeLot is { } suspended && suspended.HomeHouse == targetHouse
            ? suspended
            : null;
        if (returningHome is null && !File.Exists(destPath)) return false;

        var traveller = request.PersonGuid != 0
            ? sourceWorld.WalkOrder.OfType<XPerson>().FirstOrDefault(p => p.TypeRecord?.Key == request.PersonGuid)
            : selectedSim != 0 ? sourceWorld.WalkOrder.OfType<XPerson>().FirstOrDefault(p => p.Id == selectedSim) : null;
        var sourceStateClass = sourceWorld.StateClass(house);
        var destinationStateClass = sourceWorld.StateClass(targetHouse);
        var lotTransitInfo = sourceWorld[SimulationGlobal.LotTransitInfo];
        var fameScore = traveller is not null ? (int)traveller[PersonData.FameScore] : 0;
        var jobType = traveller is not null ? (int)traveller[PersonData.JobType] : 0;
        var willSuspendHome = !isReturnHome && suspendedHomeLot is null && lot is not null;
        var away = !isReturnHome && (willSuspendHome ||
                                     (suspendedHomeLot is not null && suspendedHomeLot.HomeHouse != targetHouse) ||
                                     targetHouse is (>= 21 and <= 30) or (>= 40 and <= 49) or (>= 50 and <= 89) or (>= 93 and <= 99) ||
                                     Neighborhoods.IsCommunityLot(userData, targetHouse));
        Write(new
        {
            type = "lotSwitching",
            fromHouse = house,
            house = targetHouse,
            away,
            returnHome = isReturnHome,
            sourceStateClass,
            destinationStateClass,
            lotTransitInfo,
            fameScore,
            jobType
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
        if (!isReturnHome && suspendedHomeLot is null && lot is not null)
        {
            UpdatePreSaveState(sourceWorld, lot);
            var (withObjects, _) = sourceWorld.WriteSavedLotObjectsTo(lot);
            var savedHomeLot = sourceWorld.WriteSavedSimulationStateTo(withObjects);
            var homeRuntime = CaptureRuntimeMetadata(sourceWorld, savedHomeLot.Bytes.Span, sourceSpeed);
            suspendedHomeLot = new SuspendedHomeLot(house, activeFamilyChunkId, savedHomeLot.Bytes.ToArray(),
                homeRuntime, sourceSpeed);
            if (house is (> 0 and < 20) or (>= 90 and <= 92) && !Neighborhoods.IsCommunityLot(userData, house))
            {
                var homeHousePath = Path.Combine(userData, "Houses", $"House{house:D2}.iff");
                Directory.CreateDirectory(Path.GetDirectoryName(homeHousePath)!);
                WriteReplace(homeHousePath, savedHomeLot.Bytes);
                WriteReplace(CanonicalRuntimePath(homeHousePath), JsonSerializer.SerializeToUtf8Bytes(homeRuntime, Json));
            }
        }

        var sourceSlots = new Dictionary<int, short>(sourceWorld.PersonTypeRecordSlots);
        var clonedScopes = sourceWorld.InteractionScopes.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        var (arrivalHour, arrivalMinute) = ConsumeArrivalClock(clonedScopes, targetHouse);
        var travelScopeKeys = CollectTravelScopeKeys(sourceSlots, clonedScopes);

        RemoveTileCursor();
        ResetSeasonalOutfits();
        audio?.Reset();
        refreshedPersonFiles.Clear();

        registry = registry?.WithRefreshedPeople(Path.Combine(userData, "Characters")) ?? registry;
        var preloadedLot = returningHome is null ? null : new IffFile(returningHome.LotBytes, destPath);
        var setup = WorldBootstrap.Load(game, destPath, language, () => speed == 0, targetHouse,
            autonomyForced: sourceWorld.AutonomyForced ?? false,
            activeFamilyChunkId: travellingFamilyChunkId,
            userDataPath: userData,
            preloadedLot: preloadedLot,
            scoreAutonomy: ScoreAutonomyChoice,
            existingRegistry: registry);

        house = targetHouse;
        lot = setup.Lot;
        world = setup.World;
        world[SimulationGlobal.SimlessBuildMode] = 0;
        world.AutonomyForced = sourceWorld.AutonomyForced;
        registry = setup.Registry ?? registry;
        neighborhoodPath = setup.NeighborhoodPath;
        neighborhood = setup.Neighborhood;
        activeFamilyChunkId = setup.ActiveFamilyChunkId;
        HydrateTypeAttributesFromNeighborhood(world, lot, neighborhood, overwriteExisting: returningHome is not null);
        if (travellingFamilyChunkId is { } resolvedFamilyChunkId && pendingVacationField is { } nextVacField)
        {
            SetInMemoryFamilyVacationField(world, resolvedFamilyChunkId, nextVacField);
        }
        if (!isReturnHome && targetHouse is >= 40 and <= 49)
        {
            var expectedFamilyNumber = (setup.ActiveFamily?.FamilyNumber is > 0 ? setup.ActiveFamily.FamilyNumber : 0);
            if (expectedFamilyNumber == 0 && travellingFamilyChunkId is { } chunkIdNum)
                expectedFamilyNumber = unchecked((short)chunkIdNum);
            var allowedKeys = new HashSet<int>(travelScopeKeys);
            if (setup.ActiveFamily is { Members.Count: > 0 } famMembers)
                foreach (var memberGuid in famMembers.Members) allowedKeys.Add(unchecked((int)memberGuid));
            if (request.PersonGuid != 0) allowedKeys.Add(request.PersonGuid);
            var removedStale = false;
            foreach (var loadedPerson in world.WalkOrder.OfType<XPerson>().ToArray())
            {
                var pFamily = loadedPerson[PersonData.FamilyNumber];
                var pGuid = loadedPerson.TypeRecord?.Key ?? 0;
                if (pFamily > 0 &&
                    expectedFamilyNumber > 0 &&
                    pFamily != expectedFamilyNumber &&
                    !allowedKeys.Contains(pGuid))
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
        }

        // Instantiates active household members on an away lot prior to transferring scopes and selecting
        // the active Sim.
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
        else if (returningHome is not null)
        {
            foreach (var person in world.WalkOrder.OfType<XPerson>())
                SyncPersonFromNeighborRecord(world, person);
            InstantiateMissingFamilyMembersOnReturnHome(world, setup.ActiveFamily);
        }

        TransferScopes(world, sourceSlots, clonedScopes);

        var updatedTravelerIds = new HashSet<short>();
        foreach (var person in world.WalkOrder.OfType<XPerson>())
        {
            if (ApplyTransferredScopeState(world, person))
                updatedTravelerIds.Add(person.Id);
        }

        if (returningHome is not null)
        {
            var travelerKeySet = new HashSet<int>(travelScopeKeys);
            if (request.PersonGuid != 0) travelerKeySet.Add(request.PersonGuid);
            var animationsToRestore = returningHome.Runtime.PersonAnimations?
                .Where(state => world.ObjectById(state.PersonId) is XPerson person &&
                                !updatedTravelerIds.Contains(person.Id) &&
                                !(person.TypeRecord?.Key is { } key && travelerKeySet.Contains(key)))
                .ToArray();
            RestorePersonAnimationRuntime(world, animationsToRestore);
            RestoreDialogRecords(world, returningHome.Runtime.Dialogs);
            suspendedHomeLot = null;
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
        saveDirty = returningHome is not null && saveDirty;
        simulationError = null;
        if (viewerScale != 0) world[SimulationGlobal.ZoomLevelSML] = viewerScale;
        world[SimulationGlobal.Mode] = viewerMode;
        ClampAwayLotEditingState(away);
        InitializeFameCallbackTarget(world);
        AttachNoticeListener(world);
        world.AutonomyHost = new AutonomyHost(world, setup.Global ?? global!, house, () => speed == 0, ScoreAutonomyChoice);
        selectedSim = request.PersonGuid != 0
            ? (PanelPeople().FirstOrDefault(p => p.TypeRecord?.Key == request.PersonGuid)?.Id ?? DefaultSelectedSimId())
            : DefaultSelectedSimId();
        SyncSelectedSimFlags(world, selectedSim);
        autoSpeedSaved = -1;

        var resumeSpeed = returningHome is { Speed: > 0 } ? returningHome.Speed : (sourceSpeed > 0 ? sourceSpeed : 1);
        lastRunningSpeed = resumeSpeed;
        SetSimulationSpeed(resumeSpeed);
        audio?.PlayLotAmbience(house);

        Write(new { type = "lotChanged", house, away });
        Write(new { type = "environment", environment = plugins?.Environment(house) });
        Export();
        Write(new { type = "state", state = State() });
        return true;
    }

    private static SaveRuntimeMetadata ReadSavedRuntimeMetadata(string path, ReadOnlyMemory<byte> lotBytes)
    {
        if (!File.Exists(path)) return new SaveRuntimeMetadata(1, 1);
        var metadata = JsonSerializer.Deserialize<SaveRuntimeMetadata>(File.ReadAllBytes(path), Json)
            ?? throw new InvalidDataException("Invalid native save runtime metadata.");
        if (metadata.Version is < 1 or > 14 || metadata.Speed is not (1 or 3 or 10) ||
            (metadata.Version == 1 && metadata.PersonAnimations is { Length: > 0 }))
            throw new InvalidDataException("Unsupported native save runtime metadata.");
        if (metadata.LotSha256 is { } expectedHash &&
            !string.Equals(expectedHash, Convert.ToHexString(SHA256.HashData(lotBytes.Span)), StringComparison.Ordinal))
            return new SaveRuntimeMetadata(1, 1);
        return metadata;
    }

    private static void RestorePersonAnimationRuntime(SimulationWorld world, PersonAnimationRuntimeSnapshot[]? states)
    {
        if (states is null) return;
        if (states.Select(state => state.PersonId).Distinct().Count() != states.Length)
            throw new InvalidDataException("Native save repeats a Sim animation state.");
        foreach (var state in states)
        {
            if (world.ObjectById(state.PersonId) is not XPerson person)
                throw new InvalidDataException($"Native save animation state references missing Sim {state.PersonId}.");
            person.AnimationPlayback = state.Primary is null ? null : AvatarAnimationPlayback.RestoreState(state.Primary);
            person.BackgroundAnimationPlayback = state.Background is null ? null : AvatarAnimationPlayback.RestoreState(state.Background);
            person.CarryAnimationPlayback = state.Carry is null ? null : AvatarAnimationPlayback.RestoreState(state.Carry);
            RoutePersistence.RestorePendingAnimationEvents(person, state.PendingAnimationEvents);
            person.BackgroundPracticePersonState = state.BackgroundPracticePersonState;
            person.AnimatorWalkingScale = RoutePersistence.RestoreAnimatorWalkingScale(
                state.AnimatorWalkingScale, state.Route);
            person.AnimatorControlState = state.AnimatorControlState ?? 3;
            person.AnimatorPoolTransitionState = state.AnimatorPoolTransitionState ?? 0;
            person.AnimatorRouteTransitionState = state.AnimatorRouteTransitionState ?? 0;
            person.AnimatorStateTailField198 = state.AnimatorStateTailField198 ?? 0;
            person.AnimatorCurrentSkill = state.AnimatorCurrentSkill ??
                (state.Primary is { RouteDriven: true } ? state.Route?.Current?.MotionAnimationIndex ?? -1 : -1);
            person.AnimatorPracticeScaleEnabled = state.AnimatorPracticeScaleEnabled ?? false;
            person.AnimatorGaitEntryState = state.AnimatorGaitEntryState ?? 0;
            person.AnimatorMilliseconds = state.AnimatorMilliseconds ?? 0;
            person.AnimatorPoolTile = state.AnimatorPoolTile ?? false;
            person.AnimatorRootX = state.AnimatorRootX ?? state.Route?.Current?.MotionX ?? state.Route?.Parents.LastOrDefault()?.MotionX;
            person.AnimatorRootY = state.AnimatorRootY ?? state.Route?.Current?.MotionY ?? state.Route?.Parents.LastOrDefault()?.MotionY;
            person.AnimatorRootDirection = state.AnimatorRootDirection ?? person[ObjectData.Direction];
            person.SetAnimatorDisplacements(state.AnimatorDisplacementX ?? 0f, state.AnimatorDisplacementY ?? 0f,
                state.AnimatorDisplacementDirection ?? 0f);
            person.InteractionAttachments.Clear();
            person.InteractionAttachments.UnionWith(state.InteractionAttachments ?? []);
            RoutePersistence.Restore(person, world.SavedExecutionStacks.GetValueOrDefault(person.Id), state.Route);
        }
    }

    private static void RestoreDialogRecords(SimulationWorld world, SavedDialogSnapshot[]? dialogs)
    {
        if (dialogs is not { Length: > 0 } || world.DialogRecords.Count > 0) return;
        foreach (var snapshot in dialogs)
        {
            if (world.ObjectById(snapshot.OwnerId) is not { } owner) continue;
            var icon = snapshot.IconId is { } iconId and not 0 ? world.ObjectById(iconId) : null;
            var record = new SimulationDialogRecord(snapshot.Token, owner, icon, snapshot.Kind, snapshot.Title,
                snapshot.Message, snapshot.Yes, snapshot.No, snapshot.Cancel, snapshot.Blocking, snapshot.AwaitsResponse)
            {
                State = snapshot.State,
                Visible = snapshot.Visible,
                ImageFile = snapshot.ImageBitmapId != 0 ? (owner.TypeRecord?.BehaviorScope as BehaviorScope)?.Private : null,
                ImageBitmapId = snapshot.ImageBitmapId,
                BookMode = snapshot.BookMode,
            };
            world.DialogRecords.Add(record);
        }
        world.SyncGenericSimsCall03DialogHandle();
    }

    /// <summary>Selects mode music based on house number ranges and zoning categories, distinguishing
    /// residential, Downtown, Vacation, Unleashed, Superstar, and Magic Town lots.</summary>
    internal static string ModeMusic(string mode, short house, bool isCommunityLot = false)
    {
        switch (mode.ToLowerInvariant())
        {
            case "live":
                return "bkground_live";
            case "options":
                return "bkground_option";
            case "camera":
                return "bkground_camera";
            case "buy":
                if (house is >= 40 and <= 49) return "station_vacation";
                if (house is >= 81 and <= 89) return "station_superstar";
                if (house is >= 21 and <= 30) return "station_dtbuy";
                if (house is >= 93 and <= 99 || (house is >= 90 and <= 99 && isCommunityLot)) return "music_magictown";
                if (house is >= 90 and <= 92) return "music_magictown_buy";
                if (isCommunityLot) return "station_unleashed";
                return "bkground_buy1";
            case "build":
                if (house is >= 40 and <= 49 or >= 81 and <= 89 or >= 21 and <= 30 or >= 93 and <= 99 ||
                    (house is >= 90 and <= 99 && isCommunityLot))
                    return "";
                if (isCommunityLot) return "station_unleashed";
                if (house is >= 90 and <= 92) return "music_magictown_build";
                return "bkground_build";
            default:
                return "bkground_live";
        }
    }

    private string ModeMusic(string mode) =>
        ModeMusic(mode, house, Neighborhoods.IsCommunityLot(Neighborhoods.UserData(profile), house));

    private void UpdateAutoSpeed(SimulationWorld current)
    {
        // On residential lots, switches to ultra-speed (10) when all selectable household Sims are asleep
        // or at work/school, restoring the previous speed when any Sim wakes or returns.
        if (testFixtureEnabled || house is 7 or >= 20 and <= 89 or >= 93 and <= 99 ||
            current[SimulationGlobal.SimlessBuildMode] != 0 ||
            current[SimulationGlobal.IsTutorialHouseNoYes] != 0 ||
            Neighborhoods.IsCommunityLot(Neighborhoods.UserData(profile), house))
            return;

        var humanResidents = current.WalkOrder.OfType<XPerson>()
            .Where(person => person[PersonData.PersonType] == 0 && person[PersonData.Gender] < 2)
            .ToArray();
        if (humanResidents.Length == 0) return;

        var anyAwakeOnLot = humanResidents.Any(person =>
            person[ObjectData.Hidden] == 0 &&
            person.Position.X >= 0 && person.Position.Y >= 0 &&
            person[PersonData.JobData] == 0 &&
            person[PersonData.IsPersonAtWorkSchool] == 0 &&
            person[Motive.SleepState] >= 0f);

        if (!anyAwakeOnLot)
        {
            if (autoSpeedSaved < 0 && speed > 0)
            {
                autoSpeedSaved = speed;
                SetSimulationSpeed(10);
                lastRunningSpeed = 10;
            }
        }
        else if (autoSpeedSaved > -1)
        {
            var restored = autoSpeedSaved > 0 ? autoSpeedSaved : 1;
            autoSpeedSaved = -1;
            if (speed > 0)
            {
                SetSimulationSpeed(restored);
                lastRunningSpeed = restored;
            }
        }
    }

    private void DispatchRouteAnimationSounds(SimulationWorld current, Dictionary<short, (string Name, float Frame)> before)
    {
        foreach (var (personId, prev) in before)
        {
            if (current.ObjectById(personId) is not XPerson person ||
                person.AnimationPlayback is not { RouteDriven: true, PracticeWeight: > 0f, Clip: { FrameCount: > 0, SoundEvents.Count: > 0 } clip } pb ||
                !string.Equals(clip.Name, prev.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            var duration = clip.DurationMilliseconds > 0f ? clip.DurationMilliseconds : clip.FrameCount * (1000f / 30f);
            var prevTime = prev.Frame * (duration / clip.FrameCount);
            var currTime = pb.Frame * (duration / clip.FrameCount);
            if (MathF.Abs(currTime - prevTime) < 0.01f) continue;
            var backwards = pb.PracticeScale < 0f;
            if (!backwards)
            {
                if (currTime >= prevTime)
                {
                    foreach (var sound in clip.SoundEvents!)
                        if (sound.TimeMilliseconds > prevTime && sound.TimeMilliseconds <= currTime)
                            HandleAnimationSound(person.Id, sound);
                }
                else
                {
                    var dispatchedWrap = false;
                    foreach (var sound in clip.SoundEvents!)
                    {
                        if (sound.TimeMilliseconds > prevTime && sound.TimeMilliseconds <= duration)
                        {
                            HandleAnimationSound(person.Id, sound);
                            dispatchedWrap = true;
                        }
                    }
                    if (!dispatchedWrap)
                    {
                        foreach (var sound in clip.SoundEvents!)
                        {
                            if (sound.TimeMilliseconds >= 0 && sound.TimeMilliseconds <= currTime)
                                HandleAnimationSound(person.Id, sound);
                        }
                    }
                }
            }
            else
            {
                if (currTime <= prevTime)
                {
                    for (var i = clip.SoundEvents!.Count - 1; i >= 0; i--)
                    {
                        var sound = clip.SoundEvents[i];
                        if (sound.TimeMilliseconds < prevTime && sound.TimeMilliseconds >= currTime)
                            HandleAnimationSound(person.Id, sound);
                    }
                }
                else
                {
                    var dispatchedWrap = false;
                    for (var i = clip.SoundEvents!.Count - 1; i >= 0; i--)
                    {
                        var sound = clip.SoundEvents[i];
                        if (sound.TimeMilliseconds < prevTime && sound.TimeMilliseconds >= 0)
                        {
                            HandleAnimationSound(person.Id, sound);
                            dispatchedWrap = true;
                        }
                    }
                    if (!dispatchedWrap)
                    {
                        for (var i = clip.SoundEvents.Count - 1; i >= 0; i--)
                        {
                            var sound = clip.SoundEvents[i];
                            if (sound.TimeMilliseconds <= duration && sound.TimeMilliseconds >= currTime)
                                HandleAnimationSound(person.Id, sound);
                        }
                    }
                }
            }
        }
    }

    private void HandleAnimationSound(short ownerId, AvatarAnimationSoundEvent cue)
    {
        if (!string.IsNullOrEmpty(cue.PackId))
        {
            plugins?.PlayAnimationSound(cue.PackId, cue.Resource, ownerId, cue.Cue, cue.Loop, cue.Stop);
            return;
        }
        var current = world;
        if (current?.ObjectById(ownerId) is not XPerson person || string.IsNullOrWhiteSpace(cue.Cue)) return;

        switch (cue.Resource)
        {
            case "sound":
            {
                var inWorld = person[ObjectData.Hidden] == 0 && person.Position.X >= 0 && person.Position.Y >= 0;
                if (!inWorld)
                {
                    if (person[PersonData.Gender] >= 2)
                        audio?.Play(cue.Cue, -4);
                }
                else
                {
                    current.PlaySoundEvent?.Invoke(cue.Cue, person.Id, true, true);
                }
                break;
            }
            case "selectedsound":
                if (person.Id == selectedSim)
                    current.PlaySoundEvent?.Invoke(cue.Cue, person.Id, true, true);
                break;
            case "deselectedsound":
                if (person.Id != selectedSim)
                    current.PlaySoundEvent?.Invoke(cue.Cue, person.Id, true, true);
                break;
            case "footstep":
            {
                if ((person[PersonData.IsLivingGhost] & 1) != 0 ||
                    (person[PersonData.IsGhost] & 1) != 0 ||
                    (person[PersonData.RenderDisplayFlags] & 8) != 0)
                    return;
                if (int.TryParse(cue.Cue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var stepValue))
                {
                    var backward = (person.AnimationPlayback?.PracticeScale ?? 1f) < 0f;
                    if (!backward && stepValue is not (1 or 2)) return;
                    if (backward && stepValue is not (-1 or -2)) return;
                }
                if (person[ObjectData.Hidden] != 0 || person.Position.X < 0 || person.Position.Y < 0)
                    return;
                var footstepCue = ResolveFootstepCue(person);
                current.PlaySoundEvent?.Invoke(footstepCue, person.Id, true, true);
                break;
            }
        }
    }

    private string ResolveFootstepCue(XPerson person)
    {
        // Outfit values 1, 2, 5, and 14 represent barefoot attire for footsteps.
        var barefoot = person[PersonData.CurrentOutfit] is 1 or 2 or 5 or 14;
        var tx = person.Position.X >> 4;
        var ty = person.Position.Y >> 4;
        var level = Math.Max(1, (int)person.Position.Height);

        if (person[PersonData.IsSwimming] != 0 ||
            (level == 1 && lot is not null && LotPool.Read(lot).IsPool(tx, ty, 1)))
            return "footstep_pool_swim_stroke";

        if (level == 1 && lot is not null && LotWater.Read(lot).IsWater(tx, ty, 1))
            return "footstep_puddle";

        if (ResolveObjectSurfaceFootstepCue(world, tx, ty, level) is { } objectCue)
            return objectCue;

        ushort floorPattern = 0;
        if (lot is not null && tx is >= 0 and < LotFloors.Size && ty is >= 0 and < LotFloors.Size)
        {
            try { floorPattern = LotFloors.Read(lot, level)[tx, ty]; } catch { }
        }

        if (floorPattern == 0)
        {
            if (GroundSnow())
                return "footstep_snow";
            if (house == 99)
                return barefoot ? "footstep_soft_noshoe" : "footstep_soft";
            return barefoot ? "footstep_terrain_noshoe" : "footstep_terrain";
        }

        if (floorPattern == 0xff)
            return barefoot ? "footstep_hard_noshoe" : "footstep_hard";

        return GetFloorSoundCode(floorPattern) switch
        {
            'H' => barefoot ? "footstep_medium_noshoe" : "footstep_medium",
            'S' => barefoot ? "footstep_soft_noshoe" : "footstep_soft",
            _ => barefoot ? "footstep_hard_noshoe" : "footstep_hard",
        };
    }

    private static string? ResolveObjectSurfaceFootstepCue(SimulationWorld? currentWorld, int tx, int ty, int level)
    {
        if (currentWorld is null) return null;

        var bestPriority = 0;
        void CheckObject(XObject item)
        {
            if (item is XPerson || item[ObjectData.ContainerId] != 0) return;
            var guid = item.TypeRecord is { } tr
                ? unchecked((uint)tr.Key)
                : (uint)(ushort)item.Definition[(int)ObjdField.Guid1] | ((uint)(ushort)item.Definition[(int)ObjdField.Guid2] << 16);
            var priority = guid switch
            {
                0x3E7470F6u or 0x5C67FC8Fu or 0x9BE030B9u => 10,
                99999999u => 9,
                0x63416BA1u => 8,
                0x7F907075u or 0xBF62F653u => 7,
                0x96E3398Du or 0xA2DAA08Cu or 0xC02B406Au or 0x65274A4Fu => 6,
                _ => 0,
            };
            if (priority > bestPriority) bestPriority = priority;
        }

        foreach (var item in currentWorld.ObjectsAtTile(tx, ty, level))
            CheckObject(item);

        foreach (var item in currentWorld.WalkOrder)
        {
            if ((item.Position.X >> 4) == tx &&
                (item.Position.Y >> 4) == ty &&
                Math.Max(1, (int)item.Position.Height) == level)
                CheckObject(item);
        }

        return bestPriority switch
        {
            10 => "footstep_puddle",
            9 => "footstep_roach",
            8 => "footstep_ash",
            7 => "footstep_trash",
            6 => "footstep_plant",
            _ => null,
        };
    }

    private char GetFloorSoundCode(ushort pattern)
    {
        if (floorSoundCodes is null)
        {
            floorSoundCodes = [];
            try
            {
                var gameData = Path.Combine(game, "GameData");
                if (!Directory.Exists(gameData)) gameData = game;
                if (Directory.Exists(gameData))
                {
                    var floorPath = Directory.EnumerateFiles(gameData).FirstOrDefault(path =>
                        Path.GetFileName(path).Equals("floors.iff", StringComparison.OrdinalIgnoreCase));
                    if (floorPath is not null)
                    {
                        var globals = IffFile.Open(floorPath);
                        for (ushort id = 1; id <= 29; id++)
                        {
                            if (ReadNonEmptyFloorSoundCode(globals, id, (ushort)(id + 256)) is { } code)
                                floorSoundCodes[id] = code;
                        }
                    }
                    foreach (var (id, path) in FloorCatalog.DynamicFiles(gameData))
                    {
                        try
                        {
                            var iff = IffFile.Open(path);
                            if (ReadNonEmptyFloorSoundCode(iff, 1, 257) is { } code)
                                floorSoundCodes[(ushort)id] = code;
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
        return floorSoundCodes.GetValueOrDefault(pattern, 'M');
    }

    private static char? ReadNonEmptyFloorSoundCode(IffFile iff, params ushort[] ids)
    {
        var spr2 = ResourceType.Parse("SPR2");
        foreach (var id in ids)
        {
            var chunk = iff.Find(spr2, id);
            if (chunk?.Label is { Length: > 0 } name)
                return char.ToUpperInvariant(name[0]);
        }
        return null;
    }

    private object PhoneBook(SimulationDialogRecord record)
    {
        var current = world;
        if (current is null || record.Owner is not XPerson owner ||
            !current.Neighbors.TryGetValue(owner[PersonData.NeighborId], out var caller))
            return Array.Empty<object>();

        var familyStrings = neighborhood is null ? null : neighborhood.OfType(ResourceType.Parse("FAMs"))
            .ToDictionary(chunk => chunk.Id, chunk => StringTable.Read(neighborhood, chunk).ForLanguage(language).FirstOrDefault() ?? "");

        if (neighborhood is not null)
        {
            var neighborsByGuid = current.Neighbors.Values
                .Where(neighbor => neighbor.TypeRecordKey != 0)
                .GroupBy(neighbor => unchecked((uint)neighbor.TypeRecordKey))
                .ToDictionary(group => group.Key, group => group.First());
            var callerFamilyId = caller.PersonData[(int)PersonData.FamilyNumber];
            var result = new List<object>();

            foreach (var chunk in neighborhood.OfType(ResourceType.Parse("FAMI")))
            {
                var familyId = (int)chunk.Id;
                var family = Family.Read(neighborhood, chunk);
                // Excludes the caller's family and requires a family chunk ID above 1999 or an unhoused
                // family with a positive house number.
                if (familyId == callerFamilyId || familyId == activeFamilyChunkId ||
                    !(familyId > 1999 || (familyId != 0 && family.HouseNumber > 0)))
                    continue;

                var familyContacts = new List<object>();
                foreach (var guid in family.Members)
                {
                    // Requires phone contacts to be human (gender values below 2) with at least one
                    // existing relationship entry to the caller.
                    if (!neighborsByGuid.TryGetValue(guid, out var contact) ||
                        contact.Id == caller.Id ||
                        contact.PersonData[(int)PersonData.Gender] >= 2 ||
                        caller.Relationships.Count(contact.Id) < 1)
                        continue;

                    var catalogName = registry?.Find(guid)?.CatalogString(0, language);
                    var livePerson = current.WalkOrder.OfType<XPerson>().FirstOrDefault(person => person[PersonData.NeighborId] == contact.Id);
                    var contactName = !string.IsNullOrWhiteSpace(livePerson?.Name) ? livePerson.Name
                        : !IsInternalUserLabel(contact.Name) ? contact.Name!
                        : !string.IsNullOrWhiteSpace(catalogName) ? catalogName
                        : livePerson?.TypeRecord?.CatalogName is { Length: > 0 } liveCatalog ? liveCatalog
                        : contact.Name ?? livePerson?.TypeRecord?.Name ?? "";
                    familyContacts.Add(new { neighborId = (int)contact.Id, name = contactName, portrait = images?.Portrait(guid) });
                }

                if (familyContacts.Count > 0)
                {
                    result.Add(new
                    {
                        familyId,
                        name = familyStrings?.GetValueOrDefault(chunk.Id) is { Length: > 0 } label ? label : familyId.ToString(),
                        contacts = familyContacts.ToArray(),
                    });
                }
            }
            return result.ToArray();
        }

        var contacts = new List<(short Family, short Neighbor, string Name, object? Portrait)>();
        foreach (var relation in caller.Relationships.Snapshot().Keys)
        {
            if (relation is <= 0 or > short.MaxValue || relation == caller.Id || caller.Relationships.Count((short)relation) < 1 ||
                !current.Neighbors.TryGetValue((short)relation, out var contact)) continue;
            var family = contact.PersonData[(int)PersonData.FamilyNumber];
            if (family <= 0 || family == caller.PersonData[(int)PersonData.FamilyNumber] ||
                contact.PersonData[(int)PersonData.Gender] > 1) continue;
            var guid = unchecked((uint)contact.TypeRecordKey);
            var catalogName = registry?.Find(guid)?.CatalogString(0, language);
            var livePerson = current.WalkOrder.OfType<XPerson>().FirstOrDefault(person => person[PersonData.NeighborId] == relation);
            var name = !string.IsNullOrWhiteSpace(livePerson?.Name) ? livePerson.Name
                : !IsInternalUserLabel(contact.Name) ? contact.Name!
                : !string.IsNullOrWhiteSpace(catalogName) ? catalogName
                : livePerson?.TypeRecord?.CatalogName is { Length: > 0 } liveCatalog ? liveCatalog
                : contact.Name ?? livePerson?.TypeRecord?.Name ?? "";
            contacts.Add((family, (short)relation, name, images?.Portrait(guid)));
        }

        return contacts.GroupBy(contact => contact.Family).Select(group => (object)new
        {
            familyId = (int)group.Key,
            name = familyStrings?.GetValueOrDefault(unchecked((ushort)group.Key)) is { Length: > 0 } label ? label : group.Key.ToString(),
            contacts = group.Select(contact => new { neighborId = (int)contact.Neighbor, name = contact.Name, portrait = contact.Portrait }).ToArray(),
        }).ToArray();
    }

    private static bool IsInternalUserLabel(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        (value.Length >= 5 && value.StartsWith("user", StringComparison.OrdinalIgnoreCase) && value.AsSpan(4).ToArray().All(char.IsAsciiDigit));

    private Family? ActiveFamily() => (world?.FamilyBudget as FamilyBudget)?.ActiveFamily;

    private uint PersonGuid(XPerson person) => unchecked((uint)(person.TypeRecord?.Key ??
        (world?.Neighbors.TryGetValue(person[PersonData.NeighborId], out var neighbor) == true ? neighbor.TypeRecordKey : 0)));

    private bool IsHouseholdPerson(XPerson person) =>
        ConnectedNeighborhoodEnabled && IsVisitingAwayLot
            ? IsConnectedHouseholdPerson(person)
            : ActiveFamily()?.Members.Contains(PersonGuid(person)) == true;

    private static bool IsControllableHuman(XPerson person) =>
        person[PersonData.Gender] < 2 && (person.AnimatorBodyStringMode & 6) == 0;

    private short DefaultSelectedSimId()
    {
        var people = PanelPeople();
        return people.FirstOrDefault(p => IsHouseholdPerson(p) && IsControllableHuman(p) && PersonAgeCategory.IsAdult(p[PersonData.PersonAge]))?.Id
            ?? people.FirstOrDefault(p => IsHouseholdPerson(p) && IsControllableHuman(p))?.Id
            ?? people.FirstOrDefault(IsHouseholdPerson)?.Id
            ?? people.FirstOrDefault(IsControllableHuman)?.Id
            ?? people.FirstOrDefault()?.Id
            ?? (short)0;
    }

    /// <summary>Orders Sim portraits by FAMI chunk member sequence, appending non-resident visitors
    /// afterward.</summary>
    private XPerson[] PanelPeople()
    {
        if (world is null) return [];
        var people = world.WalkOrder.OfType<XPerson>().ToArray();
        if (ConnectedNeighborhoodEnabled && IsVisitingAwayLot)
        {
            var homeOrder = (HomeFamily()?.Members ?? [])
                .Select((guid, index) => (guid, index))
                .ToDictionary(item => item.guid, item => item.index);
            var hostOrder = (ActiveFamily()?.Members ?? [])
                .Select((guid, index) => (guid, order: 1000 + index))
                .ToDictionary(item => item.guid, item => item.order);
            return people.OrderBy(person =>
                {
                    var guid = PersonGuid(person);
                    if (homeOrder.TryGetValue(guid, out var homeIdx)) return homeIdx;
                    if (hostOrder.TryGetValue(guid, out var hostIdx)) return hostIdx;
                    return person[PersonData.PersonType] == 0 && IsControllableHuman(person) ? 2000 : int.MaxValue;
                })
                .ThenBy(person => person.Id)
                .ToArray();
        }
        var roster = ActiveFamily()?.Members ?? [];
        var order = roster.Select((guid, index) => (guid, index)).ToDictionary(item => item.guid, item => item.index);
        return people.OrderBy(person => order.GetValueOrDefault(PersonGuid(person), int.MaxValue))
            .ThenBy(person => person.Id).ToArray();
    }

    private static object Skills(XPerson person) => new Dictionary<string, short>
    {
        ["Cooking"] = person[PersonData.CookingHouseBrokenSkill],
        ["Mechanical"] = person[PersonData.MechanicalTricksSkill],
        ["Charisma"] = person[PersonData.CharismaObedienceSkill],
        ["Body"] = person[PersonData.BodyFurnitureSkill],
        ["Logic"] = person[PersonData.LogicSkill],
        ["Creativity"] = person[PersonData.CreativitySkill],
    };

    private static object PetSkills(XPerson person) => new Dictionary<string, short>
    {
        ["HouseBroken"] = person[PersonData.CookingHouseBrokenSkill],
        ["Obedience"] = person[PersonData.CharismaObedienceSkill],
        ["Tricks"] = person[PersonData.MechanicalTricksSkill],
    };

    private int FamilyFriends()
    {
        var current = world;
        var family = ActiveFamily();
        if (current is null || family is null) return 0;
        // Calculates family friends by counting non-family human neighbors (gender below 2) who maintain
        // mutual relationships meeting or exceeding the friendship threshold.
        var members = family.Members.Select(guid => current.Neighbors.Values.FirstOrDefault(neighbor =>
            unchecked((uint)neighbor.TypeRecordKey) == guid)).Where(neighbor => neighbor is not null &&
                neighbor.PersonData[(int)PersonData.PersonType] <= 1 &&
                neighbor.PersonData[(int)PersonData.Gender] < 2).Cast<NeighborState>().ToArray();
        var memberGuids = family.Members.ToHashSet();
        var friends = new HashSet<short>();
        foreach (var member in members)
        foreach (var relation in member.Relationships.Snapshot())
        {
            if (relation.Key is <= 0 or > short.MaxValue || current.Neighbors.GetValueOrDefault((short)relation.Key) is not { } target ||
                memberGuids.Contains(unchecked((uint)target.TypeRecordKey)) ||
                target.PersonData[(int)PersonData.PersonType] > 1 ||
                target.PersonData[(int)PersonData.Gender] >= 2 ||
                relation.Value.Count == 0 || target.Relationships.Count(member.Id) == 0) continue;
            if (relation.Value[0] >= current.FriendshipThreshold && target.Relationships.Get(member.Id, 0) >= current.FriendshipThreshold)
                friends.Add((short)relation.Key);
        }
        return friends.Count;
    }

    private object[] Relationships(XPerson person)
    {
        var current = world;
        if (current is null || !current.Neighbors.TryGetValue(person[PersonData.NeighborId], out var owner)) return [];
        var familyGuids = ActiveFamily()?.Members.ToHashSet() ?? [];
        return owner.Relationships.Snapshot().OrderBy(pair => pair.Key).Select(pair =>
        {
            if (pair.Key is <= 0 or > short.MaxValue || current.Neighbors.GetValueOrDefault((short)pair.Key) is not { } target || target.Id == owner.Id)
                return null;
            var guid = unchecked((uint)target.TypeRecordKey);
            var reverseDaily = target.Relationships.Count(owner.Id) > 0 ? target.Relationships.Get(owner.Id, 0) : 0;
            var daily = pair.Value.Count > 0 ? pair.Value[0] : 0;
            var catalogName = registry?.Find(guid)?.CatalogString(0, language);
            var livePerson = current.WalkOrder.OfType<XPerson>().FirstOrDefault(sim => sim[PersonData.NeighborId] == target.Id);
            var targetData = livePerson?.PersonData ?? target.PersonData;
            return (object)new
            {
                neighborID = (int)target.Id,
                guid,
                name = !string.IsNullOrWhiteSpace(livePerson?.Name) ? livePerson.Name
                    : !IsInternalUserLabel(target.Name) ? target.Name!
                    : !string.IsNullOrWhiteSpace(catalogName) ? catalogName
                    : livePerson?.TypeRecord?.CatalogName is { Length: > 0 } liveCatalog ? liveCatalog
                    : target.Name ?? livePerson?.TypeRecord?.Name ?? "",
                description = registry?.Find(guid)?.CatalogString(1, language) ?? "",
                zodiac = (int)targetData[(int)PersonData.Zodiac],
                personality = new Dictionary<string, short>
                {
                    ["Neat"] = targetData[(int)PersonData.NeatQuietPersonality],
                    ["Outgoing"] = targetData[(int)PersonData.OutgoingFriendlyPersonality],
                    ["Active"] = targetData[(int)PersonData.ActivePlayfulPersonality],
                    ["Playful"] = targetData[(int)PersonData.PlayfulSmartPersonality],
                    ["Nice"] = targetData[(int)PersonData.NiceLoyalPersonality],
                },
                values = pair.Value,
                household = familyGuids.Contains(guid),
                portrait = images?.NeighborPortrait(guid),
                reverseDaily,
                famous = targetData.ElementAtOrDefault(81) != 0,
                mutualFriend = daily >= current.FriendshipThreshold && reverseDaily >= current.FriendshipThreshold,
                livePosition = livePerson is null ? null : new { x = livePerson.Position.X / 16f, y = livePerson.Position.Y / 16f, level = (int)livePerson.Position.Height },
            };
        }).Where(item => item is not null).Cast<object>().ToArray();
    }

    private object? CareerPanel(XPerson person, int? cachedFamilyFriends = null)
    {
        if (person[PersonData.PersonType] is < 0 or > 1 || person[PersonData.Gender] >= 2) return null;
        if (IsFamePanelEligible(person)) return null;
        var career = world?.CareerHost as CareerCatalog;
        var type = person[PersonData.JobType];
        var level = person[PersonData.JobPromotionLevel];
        var age = person[PersonData.PersonAge];
        var female = (person[PersonData.Gender] & 1) != 0;
        var school = PersonAgeCategory.IsChild(age);
        int[]? Requirements(int levelIndex)
        {
            if (career?.CareerExists(type) != true || career.JobData(type, (short)levelIndex, 0) is null) return null;
            var values = new int[10];
            for (var i = 0; i < 10; i++)
            {
                if (career.JobData(type, (short)levelIndex, (short)(i + 2)) is not { } value) return null;
                values[i] = value;
            }
            return values;
        }
        var performance = person[PersonData.JobPerformance];
        var ratingIndex = school ? Math.Clamp(performance + 1, 0, 15) : performance < -60 ? 0 : performance < -30 ? 1 : performance < 30 ? 2 : performance < 60 ? 3 : 4;
        return new
        {
            type = (int)type,
            level = (int)level,
            performance = (int)performance,
            age = (int)age,
            atWorkSchool = (int)person[PersonData.JobData],
            school,
            track = career?.TrackName(type),
            title = career?.JobText(type, level, JobTextKind.Title, female, language),
            jobDescription = career?.JobText(type, level, JobTextKind.Description, female, language),
            salary = career?.JobData(type, level, 1),
            startHour = career?.JobData(type, level, 12),
            endHour = career?.JobData(type, level, 13),
            currentLevelRequirements = Requirements(level),
            nextLevelRequirements = Requirements(level + 1),
            performanceLabel = career?.GradeString(ratingIndex, language),
            // School report card grades index STR# 0x1001 in <c>work.iff</c> at performance plus one.
            reportCardGrade = school ? career?.GradeString(performance + 1, language) : null,
            familyFriends = cachedFamilyFriends ?? FamilyFriends(),
        };
    }

    private object? FamePanel(XPerson person)
    {
        if (!IsFamePanelEligible(person)) return null;
        var (tuning, titles) = FameResources();
        // PersonData field 81 stores the Sim's Star Power level (0 to 10).
        var level = Math.Clamp((int)person[PersonData.FameStarPower09], 0, 10);
        var title = level < titles.Count ? titles[level] : "?";
        var skillData = new[]
        {
            person[PersonData.CookingHouseBrokenSkill], person[PersonData.MechanicalTricksSkill],
            person[PersonData.CharismaObedienceSkill], person[PersonData.BodyFurnitureSkill],
            person[PersonData.LogicSkill], person[PersonData.CreativitySkill],
        };
        var skillRequirements = Enumerable.Range(0, 6).Select(index =>
            level < 10 && skillData[index] < tuning.FameSkillThresholds[index][level] * 100
                ? tuning.FameSkillThresholds[index][level] : 0).ToArray();
        var starPower = world?.FameFriendStarPower(world.PersonRecordIndex(person)) ?? 0;
        return new
        {
            score = (int)person[PersonData.FameScore],
            level,
            title,
            skills = skillData.Select(value => Math.Clamp((int)value, 0, 1000)).ToArray(),
            skillRequirements,
            friendStarPower = starPower,
            friendsNeeded = level < tuning.FameFriendThresholds.Count ? tuning.FameFriendThresholds[level] : 0,
            nextScore = level < tuning.FameStarThresholds.Count - 1 ? tuning.FameStarThresholds[level + 1] : 1000,
        };
    }

    private bool IsFamePanelEligible(XPerson person)
    {
        if (person[PersonData.PersonType] is < 0 or > 1 || person[PersonData.FameScore] <= 0 || !HasSuperstarPack()) return false;
        var career = world?.CareerHost as CareerCatalog;
        var jobType = person[PersonData.JobType];
        // Displays the Fame panel in place of the Career panel only when the Sim has no active career.
        return jobType == 0 || career?.JobData(jobType, person[PersonData.JobPromotionLevel], 0) is null;
    }

    private (FameTuning Tuning, IReadOnlyList<string> Titles) FameResources()
    {
        if (fameTuning is not null && fameTitles is not null) return (fameTuning, fameTitles);
        var famePath = Path.Combine(game, "GameData", "fame.iff");
        var file = new IffFile(File.ReadAllBytes(famePath), famePath);
        var titleChunk = file.Find(ResourceType.Str, 1)
            ?? throw new InvalidDataException("fame.iff is missing STR# 1 fame titles");
        fameTuning = FameTuning.Read(file);
        fameTitles = StringTable.Read(file, titleChunk).ForLanguage(language);
        return (fameTuning, fameTitles);
    }

    /// <summary>Initializes fame scoring thresholds from <c>fame.iff</c> when Superstar expansion resources
    /// are present.</summary>
    private void InitializeFameCallbackTarget(SimulationWorld targetWorld)
    {
        targetWorld.GenericSimsCallRecordCallbackTarget = HasSuperstarPack()
            ? GenericSimsCallRecordTarget.FromFameTuning(FameResources().Tuning)
            : new GenericSimsCallRecordTarget();
    }

    private bool HasSuperstarPack()
    {
        var folder = Path.Combine(game, "ExpansionPack6");
        return Directory.Exists(folder) || File.Exists(folder + ".far");
    }

    private readonly PictureInPictureOptions pictureInPicture = new();

    private object[] PictureInPictureEvents(SimulationWorld lotWorld)
    {
        lotWorld.PictureInPicture ??= pictureInPicture;
        return lotWorld.PictureInPictureEvents.Select(item =>
        {
            var target = item.ObjectId != 0 ? lotWorld.ObjectById((short)item.ObjectId) : null;
            var insideLotEdge = target is null || SimulationWorld.IsInsideLotEdge(target);
            return (object)new
            {
                sequence = item.Sequence,
                objectId = item.ObjectId,
                open = item.Open,
                force = item.Force,
                snapshot = item.SnapshotRequested,
                durationMs = item.DurationMilliseconds,
                flags = (int)item.Flags,
                checkOffScreen = (item.Flags & 4) == 0,
                insideLotEdge,
                size = (int)item.Byte2,
                zoom = (int)item.Byte3,
                byte2 = (int)item.Byte2,
                byte3 = (int)item.Byte3,
                text = item.Text,
            };
        }).ToArray();
    }

    private ulong? lastStreamedObjectsSignature;

    private ulong ComputeObjectsSignature()
    {
        if (world is null) return 0;
        var hash = 14695981039346656037UL;
        void Mix(long v)
        {
            hash ^= unchecked((ulong)v);
            hash *= 1099511628211UL;
        }
        Mix(world.WalkOrder.Count);
        foreach (var item in world.WalkOrder)
        {
            Mix(item.Id);
            Mix(item.TypeRecord?.Key ?? 0);
            Mix(item.MultiTileMasterId);
            Mix(item.MultiTileNextId);
            Mix(item.Position.X);
            Mix(item.Position.Y);
            Mix(item.Position.Height);
            Mix(item[ObjectData.Direction]);
            Mix(item[ObjectData.UseCount]);
            Mix(item[ObjectData.Flags]);
            Mix(world.InteractionLifecycle.LeaseCount(item));
            Mix(world.InteractionLifecycle.WaiterCount(item));
        }
        return hash;
    }

    private object State(bool includeArchitectureGrids = true, bool includeObjects = true)
    {
        if (registry is not null) images?.SetRegistry(registry);
        if (includeObjects) lastStreamedObjectsSignature = ComputeObjectsSignature();
        var computedFamilyFriends = world is null ? (int?)null : FamilyFriends();
        var sharedValuation = world?.SavedSimulationState is { Version: >= 0x25 } savedValuation
            ? CurrentLotValuation(savedValuation)
            : ((int FurnishingsValue, int ArchitectureValue)?)null;
        var roof = lot is null ? ((string Style, float Pitch, int StyleIndex)?)null : RoofInfo(lot);
        var vanillaPayload = new
        {
        house,
        homeHouse = EffectiveHomeHouse,
        away = IsVisitingAwayLot,
        houseStateClass = world?.StateClass(house) ?? 0,
        lotTransitInfo = world?[SimulationGlobal.LotTransitInfo] ?? 0,
        family = activeFamilyChunkId,
        tutorialResetRequiresSave = saveDirty && house != 0,
        tutorialIcon = world is { TutorialOwner: not null } activeWorld
            ? images?.TutorialIcon(activeWorld) ?? new { fallback = true }
            : null,
        updates,
        speed,
        simulationError,
        selectedSim,
        housePanel = HousePanel(sharedValuation),
        freeWill = world?.AutonomyForced,
        budget = world?.FamilyBudget?.ReadFamilyBudget(),
        familyFriends = computedFamilyFriends,
        budgetReport = BudgetReport(sharedValuation),
        hours = world?.Clock.Hour ?? 0,
        minutes = world?.Clock.Minute ?? 0,
        environment = world is null ? null : plugins?.Environment(house),
        seconds = world?.Clock.Second ?? 0,
        wallsUp = wallMode == "up",
        wallMode,
        editingView,
        cameraOrientation,
        roofsVisible,
        roofStyle = roof?.Style,
        roofStyleIndex = roof?.StyleIndex,
        roofPitch = roof?.Pitch,
        simlessBuildMode = world is not null && world[SimulationGlobal.SimlessBuildMode] != 0,
        canUndo = undoStack.Count > 0,
        canRedo = redoStack.Count > 0,
        viewLevel,
        stories = Stories,
        dialogs = world is null ? Array.Empty<object>() : world.DialogRecords.Where(record => record.Visible).OrderByDescending(record => record.Blocking).Select(record => (object)new
        {
            token = record.Token,
            blocking = record.Blocking,
            caller = (int)record.Owner.Id,
            tutorial = world.TutorialOwner is { } tutorialOwner && ReferenceEquals(record.Owner, tutorialOwner),
            icon = (int)(record.Icon?.Id ?? 0),
            kind = record.Kind,
            title = record.Kind == 10 && string.IsNullOrEmpty(record.Title) && record.Owner is XPerson callerOwner
                ? (!string.IsNullOrWhiteSpace(callerOwner.Name) ? callerOwner.Name
                    : world.Neighbors.TryGetValue(callerOwner[PersonData.NeighborId], out var callerNeighbor) && !IsInternalUserLabel(callerNeighbor.Name) ? callerNeighbor.Name!
                    : callerOwner.TypeRecord?.CatalogName is { Length: > 0 } cat ? cat
                    : registry?.Find(PersonGuid(callerOwner))?.CatalogString(0, language) ?? "")
                : record.Title,
            message = record.Message,
            yes = record.Yes,
            no = record.No,
            cancel = record.Cancel,
            image = record.Kind == 10 && record.Owner is XPerson phoneCaller
                ? images?.Portrait(world, phoneCaller)
                : images?.DialogImage(record),
            phoneBook = record.Kind == 10 ? PhoneBook(record) : null,
            bookMode = record.BookMode,
            hideBookItem9 = record.HideBookItem9,
            hideBookItem4 = record.HideBookItem4,
            bookAvailability = record.BookAvailability?.Select(item => new
            {
                contextItemId = (int)item.ContextItemId,
                itemId = (int)item.ItemId,
                made = item.Made,
            }).ToArray(),
        }).ToArray(),
        pictureInPicture = world is null ? Array.Empty<object>() : PictureInPictureEvents(world),
        uiMessages = world is null ? Array.Empty<object>() : world.UiMessages.TakeLast(16).Select(message => (object)new
        {
            sequence = message.Sequence,
            message = message.Message,
            text = message.Text,
        }).ToArray(),
        width = LotWidth,
        height = LotHeight,
        buildable = includeArchitectureGrids ? Buildable() : null,
        floors = includeArchitectureGrids ? StateFloors() : null,
        walls = includeArchitectureGrids ? StateWalls() : null,
        objects = !includeObjects ? null : world is null ? Array.Empty<object>() : world.WalkOrder.Select(item =>
        {
            var isPerson = item is XPerson;
            var buildType = !isPerson ? ObjectBuildModeType(item) : 0;
            var canSell = !isPerson && world.CanSellObject(item);
            var resaleValue = !isPerson ? SimulationWorld.HandResaleValue(world.ObjectSaleValue(item), buildType) : 0;
            return new
            {
                id = (int)item.Id,
                name = item.TypeRecord?.Name ?? "",
                guid = (uint)(item.TypeRecord?.Key ?? 0),
                multiTileMasterID = (int)item.MultiTileMasterId,
                multiTileNextID = (int)item.MultiTileNextId,
                x = item.Position.X / 16f,
                y = item.Position.Y / 16f,
                level = item.Position.Height,
                // Converts an eighth-turn direction index (0..7) to bit flags for editor placement.
                direction = (byte)(1 << (item[ObjectData.Direction] & 7)),
                useCount = (short)item[ObjectData.UseCount],
                interactionLeaseCount = world.InteractionLifecycle.LeaseCount(item),
                interactionWaiterCount = world.InteractionLifecycle.WaiterCount(item),
                avatar = isPerson,
                movable = !isPerson && world.CanPickUpObject(item),
                sellable = canSell,
                buildModeType = buildType,
                resaleValue,
                catalogName = !isPerson ? ObjectCatalogName(world, item) : "",
                catalogCategory = buildType is >= 1 and <= 7 ? (int?)(buildType + 7) : null,
            };
        }).ToArray(),
        sims = PanelPeople().Select(person => new
        {
            id = (int)person.Id,
            guid = (uint)(person.TypeRecord?.Key ?? 0),
            hideHover = (person.Definition[91] & 4) != 0,
            // Resolves a Sim's display name using runtime-assigned names first, falling back to neighbor
            // record labels, and finally the catalog selector name.
            name = !string.IsNullOrWhiteSpace(person.Name) && !IsInternalUserLabel(person.Name)
                ? person.Name
                : world!.Neighbors.TryGetValue(person[PersonData.NeighborId], out var neighbor) && !IsInternalUserLabel(neighbor.Name)
                ? neighbor.Name!
                : person.TypeRecord?.CatalogName is { } catalogName && !string.IsNullOrWhiteSpace(catalogName)
                ? catalogName
                : world.Neighbors.TryGetValue(person[PersonData.NeighborId], out var fallbackNeighbor) && !string.IsNullOrWhiteSpace(fallbackNeighbor.Name)
                    ? fallbackNeighbor.Name
                    : person.TypeRecord?.Name ?? "",
            internalName = world!.Neighbors.TryGetValue(person[PersonData.NeighborId], out var neighborForId) && !string.IsNullOrWhiteSpace(neighborForId.Name)
                ? neighborForId.Name
                : null,
            tooltipName = !string.IsNullOrWhiteSpace(person.Name) && !IsInternalUserLabel(person.Name)
                ? person.Name
                : world!.Neighbors.TryGetValue(person[PersonData.NeighborId], out var tooltipNeighbor) && !IsInternalUserLabel(tooltipNeighbor.Name)
                    ? tooltipNeighbor.Name!
                    : person.TypeRecord?.CatalogName ?? person.TypeRecord?.Name ?? "",
            neighborID = (int)person[PersonData.NeighborId],
            household = IsHouseholdPerson(person),
            child = PersonAgeCategory.IsChild(person[PersonData.PersonAge]),
            mood = (short)person[Motive.Mood],
            personType = (int)person[PersonData.PersonType],
            gender = (int)person[PersonData.Gender],
            zodiac = (int)person[PersonData.Zodiac],
            fameScore = (int)person[PersonData.FameScore],
            jobType = (int)person[PersonData.JobType],
            personality = new Dictionary<string, short>
            {
                ["Neat"] = (short)person[PersonData.NeatQuietPersonality],
                ["Outgoing"] = (short)person[PersonData.OutgoingFriendlyPersonality],
                ["Active"] = (short)person[PersonData.ActivePlayfulPersonality],
                ["Playful"] = (short)person[PersonData.PlayfulSmartPersonality],
                ["Nice"] = (short)person[PersonData.NiceLoyalPersonality],
            },
            // Reads interest values from PersonData fields, where fields 46 through 49 map to age-specific
            // categories for child Sims.
            interests = new[] { 46, 47, 48, 49, 50, 51, 52, 53, 46, 47, 48, 49, 13, 14, 16, 20, 26, 54, 55 }
                .Select(field => (short)person.PersonData[field]).ToArray(),
            x = person.Position.X / 16f,
            y = person.Position.Y / 16f,
            level = person.Position.Height,
            animation = person.AnimationPlayback?.Clip.Name,
            interactionAttachments = person.InteractionAttachments.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            winterClothes = seasonalOutfits?.IsWearing(person) ?? false,
            outfitChanging = person.NativeOutfitTransitionActive,
            motives = new Dictionary<string, short>
            {
                ["Hunger"] = (short)person[Motive.Hunger], ["Comfort"] = (short)person[Motive.Comfort],
                ["Hygiene"] = (short)person[Motive.Hygiene], ["Bladder"] = (short)person[Motive.Bladder],
                ["Energy"] = (short)person[Motive.Energy], ["Fun"] = (short)person[Motive.Fun],
                ["Social"] = (short)person[Motive.Social], ["Room"] = (short)person[Motive.Room],
            },
            skills = Skills(person),
            petSkills = PetSkills(person),
            career = CareerPanel(person, computedFamilyFriends),
            fame = FamePanel(person),
            relationships = Relationships(person),
            portrait = world is null ? null : images?.Portrait(world, person),
            inventory = (world is null ? Array.Empty<AvatarInventory.Entry>() : world.NeighborInventory((short)person[PersonData.NeighborId]).Entries)
                .Select(item => new { type = item.Key1, guid = unchecked((uint)item.Key2), count = (int)item.Count,
                    name = images?.ItemName(unchecked((uint)item.Key2), language) ?? $"Unknown Item ({item.Key2})",
                    description = images?.ItemDescription(unchecked((uint)item.Key2), language) ?? "", icon = images?.ItemIcon(unchecked((uint)item.Key2)) }).ToArray(),
            actions = Actions(person),
        }).ToArray(),
        nativeEngine = true,
        };
        return ConnectedNeighborhoodEnabled ? AppendConnectedNeighborhoodToState(vanillaPayload) : vanillaPayload;
    }

    private object? BudgetReport((int FurnishingsValue, int ArchitectureValue)? precomputedValuation = null)
    {
        if (world?.SavedSimulationState is not { Version: >= 0x26, BudgetDays.Count: 6 } saved) return null;
        var days = saved.BudgetDays;
        var today = days[0].Values.Take(8).Concat(Enumerable.Repeat(0, 8)).Take(8).ToArray();
        // Calculates three-day expenses by summing today's expenses with the previous two days of recorded
        // history.
        var threeDay = Enumerable.Range(0, 8).Select(index =>
            today[index] + (days[1].Values.Count > index ? days[1].Values[index] : 0)
                       + (days[2].Values.Count > index ? days[2].Values[index] : 0)).ToArray();
        var daysSinceMoveIn = (int)(world[SimulationGlobal.DaysRunning]);
        var funds = world.FamilyBudget?.ReadFamilyBudget() ?? saved.Field0x90;
        var (furnishingsValue, rawArchitectureValue) = precomputedValuation ?? CurrentLotValuation(saved);
        var architectureValue = (int)(rawArchitectureValue * (world.ArchitectureValueMultiplier ?? 0.7f));
        var lotValue = saved.Field0x9C;
        var propertyValue = lotValue + furnishingsValue + architectureValue;
        var netWorth = funds + propertyValue;
        return new
        {
            today,
            threeDay,
            daysSinceMoveIn,
            objects = furnishingsValue,
            architecture = architectureValue,
            lotValue,
            propertyValue,
            netWorth,
        };
    }

    private void ResetLotValuationBaseline()
    {
        initialObjectValuation = SumPlacedObjectValues(world);
        initialValuationLot = lot;
        lotValuationEdited = false;
    }

    /// <summary>Calculates live lot furnishings and architecture valuations, preserving saved SIMI values
    /// until modified by build, buy, depreciation, or breakage changes.</summary>
    private (int FurnishingsValue, int ArchitectureValue) CurrentLotValuation(SimulationState saved) =>
        ComputeLotValuation(world, lot, initialObjectValuation, initialValuationLot, lotValuationEdited, saved);

    private (int FurnishingsValue, int ArchitectureValue) ComputeLotValuation(
        SimulationWorld? targetWorld,
        IffFile? targetLot,
        (int FurnishingsObjects, int ArchitectureObjects) baselineObjects,
        IffFile? baselineLot,
        bool edited,
        SimulationState saved)
    {
        var currentObjects = SumPlacedObjectValues(targetWorld);
        var furnishingsDelta = currentObjects.FurnishingsObjects - baselineObjects.FurnishingsObjects;
        var archObjectsDelta = currentObjects.ArchitectureObjects - baselineObjects.ArchitectureObjects;
        var archLayerDelta = (edited || (targetLot is not null && baselineLot is not null && !ReferenceEquals(targetLot, baselineLot)))
            && targetLot is not null && baselineLot is not null && !ReferenceEquals(targetLot, baselineLot)
            ? SumArchitectureLayerValue(targetLot) - SumArchitectureLayerValue(baselineLot)
            : 0;
        return (Math.Max(0, saved.Field0xA0 + furnishingsDelta),
                Math.Max(0, saved.Field0xA4 + archObjectsDelta + archLayerDelta));
    }

    private (int FurnishingsObjects, int ArchitectureObjects) SumPlacedObjectValues(SimulationWorld? target)
    {
        if (target is null) return (0, 0);
        var furnishings = 0;
        var architecture = 0;
        foreach (var item in target.Objects.Values)
        {
            if (item is XPerson || (item.Definition is { Length: > (int)ObjdField.Type } && item.Definition[(int)ObjdField.Type] == 7)) continue;
            if (item.MultiTileMasterId != 0 && item.MultiTileMasterId != item.Id) continue;
            if (item.Position.X < 0 || item.Position.Y < 0) continue;
            var buildMode = ObjectBuildModeType(item);
            if (buildMode == 0)
            {
                var movementFlags = unchecked((ushort)item[ObjectData.MovementFlags]);
                if ((movementFlags & 0x10) == 0 && (movementFlags & 0x08) != 0)
                    furnishings += target.ObjectSaleValue(item);
            }
            else if (buildMode != 4)
            {
                architecture += target.ObjectSaleValue(item);
            }
        }
        return (furnishings, architecture);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IffFile, System.Runtime.CompilerServices.StrongBox<int>> architectureLayerValueCache = new();

    /// <summary>Computes architectural valuation by summing wall structure half-prices, wallpaper coverings
    /// on styles 1, 7, 8, and 9, floor tile prices, diagonal split floor half-prices, and ground pool
    /// tiles.</summary>
    private int SumArchitectureLayerValue(IffFile targetLot) =>
        architectureLayerValueCache.GetValue(targetLot, ComputeArchitectureLayerValue).Value;

    private System.Runtime.CompilerServices.StrongBox<int> ComputeArchitectureLayerValue(IffFile targetLot)
    {
        var floorsCatalog = floorCatalog ??= FloorCatalog.Open(game, language);
        var wallsCatalog = wallCatalogCache ??= WallCatalog.Open(game, language);
        var wallpaperCatalog = wallpaperCatalogCache ??= WallpaperCatalog.Open(game, language);
        var lotTiles = LotTiles.Read(targetLot);
        var pool = LotPool.Read(targetLot);
        var poolPrice = floorsCatalog.Contains(65535) ? floorsCatalog.Price(65535) : 0;
        var wallTotal = 0;
        var otherTotal = 0;
        for (var level = 1; level <= 2; level++)
        {
            var walls = LotWalls.Read(targetLot, level);
            var floors = LotFloors.Read(targetLot, level);
            for (var x = 0; x < LotWalls.Size; x++)
            for (var y = 0; y < LotWalls.Size; y++)
            {
                if (!lotTiles.InsideLot(x, y)) continue;
                var cell = walls[x, y];
                int StylePrice(int style) => style > 0 && wallsCatalog.Contains(style) ? wallsCatalog.Price(style) : 0;
                int PaperPrice(int style, int pattern) =>
                    style is 1 or 7 or 8 or 9 && pattern is > 0 and < 255 && wallpaperCatalog.Contains(pattern)
                        ? wallpaperCatalog.Price(pattern)
                        : 0;
                int SplitFloorHalfPrice(int pattern) =>
                    pattern is > 0 and < 255 && floorsCatalog.Contains(pattern) ? floorsCatalog.Price(pattern) / 2 : 0;

                var segments = cell[0];
                var style1 = (int)cell[2];
                var style2 = (int)cell[3];
                var style4 = x + 1 < LotWalls.Size ? (int)walls[x + 1, y][2] : 0;
                var style8 = y + 1 < LotWalls.Size ? (int)walls[x, y + 1][3] : 0;
                var styleDiag = (int)cell[3];
                var pattern1 = (int)cell[4];
                var pattern2 = (int)cell[5];
                var pattern8 = (int)cell[6];
                var pattern4 = (int)cell[7];

                if ((segments & 1) != 0) wallTotal += StylePrice(style1);
                if ((segments & 2) != 0) wallTotal += StylePrice(style2);
                if ((segments & 4) != 0) wallTotal += StylePrice(style4);
                if ((segments & 8) != 0) wallTotal += StylePrice(style8);
                if ((segments & 16) != 0) wallTotal += 2 * StylePrice(styleDiag);
                if ((segments & 32) != 0) wallTotal += 2 * StylePrice(styleDiag);

                if ((segments & 1) != 0) otherTotal += PaperPrice(style1, pattern1);
                if ((segments & 2) != 0) otherTotal += PaperPrice(style2, pattern2);
                if ((segments & 4) != 0) otherTotal += PaperPrice(style4, pattern4);
                if ((segments & 8) != 0) otherTotal += PaperPrice(style8, pattern8);
                if ((segments & 48) != 0)
                {
                    if (styleDiag == 255)
                    {
                        otherTotal += SplitFloorHalfPrice(cell[2]) + SplitFloorHalfPrice(cell[4]);
                    }
                    else
                    {
                        otherTotal += PaperPrice(styleDiag, pattern8) + PaperPrice(styleDiag, pattern4);
                    }
                }

                var floorPattern = floors[x, y];
                if (floorPattern is > 0 and < 65534 && floorsCatalog.Contains(floorPattern))
                    otherTotal += floorsCatalog.Price(floorPattern);
                if (level == 1 && pool.IsPool(x, y, 1))
                    otherTotal += poolPrice;
            }
        }
        return new System.Runtime.CompilerServices.StrongBox<int>(wallTotal / 2 + otherTotal);
    }

    private IReadOnlyList<string>? cachedRoofStyles;
    private IffFile? cachedRoofLot;
    private (string Style, float Pitch, int StyleIndex) cachedRoofInfo;

    private (string Style, float Pitch, int StyleIndex) RoofInfo(IffFile currentLot)
    {
        if (ReferenceEquals(cachedRoofLot, currentLot)) return cachedRoofInfo;
        var settings = LotRoof.Read(currentLot);
        var styles = cachedRoofStyles ??= LotRoof.Styles(game);
        var styleIndex = 0;
        for (var i = 0; i < styles.Count; i++)
        {
            if (string.Equals(styles[i], settings.Style, StringComparison.OrdinalIgnoreCase))
            {
                styleIndex = i;
                break;
            }
        }
        cachedRoofLot = currentLot;
        return cachedRoofInfo = (settings.Style, settings.Pitch, styleIndex);
    }

    private bool furnishingsCurveLoaded;
    private (float X, float Y)[]? cachedFurnishingsCurve;

    private (float X, float Y)[]? FurnishingsCurvePoints()
    {
        if (furnishingsCurveLoaded) return cachedFurnishingsCurve;
        furnishingsCurveLoaded = true;
        if (global?.Find(ResourceType.Str, 505) is not { } chunk) return null;
        var curves = StringTable.Read(global, chunk).ForLanguage(language);
        if (curves.Count < 2) return null;
        var points = new List<(float X, float Y)>();
        foreach (var token in curves[1].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = token.Trim('(', ')').Split(';');
            if (pair.Length != 2 || !float.TryParse(pair[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(pair[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y)) return null;
            points.Add((x, y));
        }
        points.Sort((left, right) => left.X.CompareTo(right.X));
        return cachedFurnishingsCurve = points.Count == 0 ? null : points.ToArray();
    }

    private object[] Actions(XPerson person)
    {
        var queued = new List<object>();
        if (person.CurrentActionRecord is { Owner: not null, Secondary: not null } current)
            queued.Add(DescribeAction(current, person, true));
        var count = person.ActionQueueEnd - person.ActionQueueCurrent;
        if (count is >= 0 and <= 8)
        {
            for (var index = 0; index < count; index++)
            {
                var action = person.ActionQueue[(person.ActionQueueCurrent + index) & 7];
                if (action.Owner is not null && action.Secondary is not null) queued.Add(DescribeAction(action, person, false));
            }
        }
        if (ConnectedNeighborhoodEnabled && TryDescribeConnectedTransitAction(person) is { } transitAction)
            queued.Add(transitAction);
        return queued.ToArray();
    }

    private object DescribeAction(ActionQueueSlot action, XPerson person, bool active) => new
    {
        name = action.ActionText ?? action.Owner?.TypeRecord?.Interactions?.Interactions.FirstOrDefault(entry => entry.TtaIndex == action.RawDword10)?.Name ?? "",
        callee = action.Owner?.Id,
        token = $"native-{person.Id}-{action.Sequence}",
        active = active && person[PersonData.RoutingComplete] != 0,
        visible = true,
        cancellable = person[PersonData.Gender] < 2,
        cancelRequested = (action.Flags & 0x100) != 0 ||
            world!.InteractionLifecycle.InterruptRequested(person, action.Sequence) ||
            (active && person.ActiveActionCancelRequested),
        icon = images?.Icon(world!, action.Secondary) ?? images?.Icon(world!, action.Owner) ?? images?.Portrait(world!, person),
    };

    private int Stories => lot is null ? 1 : SceneExporter.Stories(lot);

    private object HousePanel((int FurnishingsValue, int ArchitectureValue)? precomputedValuation = null)
    {
        var squareFeet = HouseSquareFeet();
        var roomCounts = HouseRoomCounts();
        var householdSize = 0;
        if (world is not null)
        {
            foreach (var item in world.WalkOrder)
                if (item is XPerson) householdSize++;
        }
        return new
        {
            squareFeet,
            bedrooms = roomCounts?.Bedrooms,
            bathrooms = roomCounts?.Bathrooms,
            lotSize = HouseLotSizeIndex(),
            sizeScore = squareFeet is null ? (int?)null : HouseSizeScore(squareFeet.Value, householdSize),
            furnishingsScore = HouseFurnishingsScore(precomputedValuation),
            yardScore = HouseYardScore(),
            upkeepScore = HouseUpkeepScore(),
            layoutScore = HouseLayoutScore(),
        };
    }

    // Evaluates house furnishings score by applying HouseScore curve 1 from <c>Global.iff</c> STR# 505 to
    // furnishings and architecture values.
    private int? HouseFurnishingsScore((int FurnishingsValue, int ArchitectureValue)? precomputedValuation = null)
    {
        if (world?.SavedSimulationState is not { Version: >= 0x25 } saved || FurnishingsCurvePoints() is not { } points)
            return null;

        var (savedObjectsValue, savedArchitectureValue) = precomputedValuation ?? CurrentLotValuation(saved);
        var input = savedObjectsValue == 0 ? 100f : (float)savedArchitectureValue / savedObjectsValue;
        var curve = input < points[0].X
            ? points[0].Y
            : input >= points[^1].X
                ? points[^1].Y
                : Interpolate(points, input);
        return (int)Math.Round(Math.Clamp(curve, 0f, 100f), MidpointRounding.ToEven);

        static float Interpolate((float X, float Y)[] points, float value)
        {
            for (var index = points.Length - 2; index >= 0; index--)
            {
                var start = points[index];
                var end = points[index + 1];
                if (value < start.X) continue;
                var fraction = (value - start.X) / (end.X - start.X);
                return (1 - fraction) * start.Y + fraction * end.Y;
            }
            return points[0].Y;
        }
    }

    // Maps house numbers to lot size classes: 0 for houses up to 39, 1 for houses 40 through 49, and 2 for
    // houses 50 and above.
    private int HouseLotSizeIndex() => house > 0x31 ? 2 : house > 0x27 ? 1 : 0;

    // Counts indoor bedrooms and bathrooms based on rooms containing bedroom (category 4) or bathroom
    // (categories 5 through 7) fixtures.
    private (int Bedrooms, int Bathrooms)? HouseRoomCounts()
    {
        if (world is null || !world.RoomManagerRecordsLoaded || world.LotRoomMaps.Count == 0 ||
            world.LotRoomMaps.Values.Any(map => !map.HasAuthoritativeOutsideRooms) ||
            world.PersonRoomMotiveEntries.Values.Any(room => room.NeedsRefresh)) return null;

        var indoorRooms = world.PersonRoomMotiveEntries.Values.Where(room => !room.IsOutside);
        return (
            indoorRooms.Count(room => room.HouseCategory4ObjectCount58 > 0),
            indoorRooms.Count(room => room.HouseCategory5To7ObjectCount5c > 0));
    }

    // Calculates house square footage by multiplying the total number of enclosed indoor room tiles by
    // nine.
    private int? HouseSquareFeet()
    {
        if (world is null || world.LotRoomMaps.Count == 0 || world.LotRoomMaps.Values.Any(map => !map.HasAuthoritativeOutsideRooms)) return null;
        var tiles = 0;
        foreach (var map in world.LotRoomMaps.Values)
        foreach (var room in map.RoomIds)
            if (!map.IsOutside(room)) tiles += map.TilesInRoom(room).Count();
        return checked(tiles * 9);
    }

    // Calculates the yard score by accumulating outdoor room scores scaled by tuning multipliers.
    private int? HouseYardScore()
    {
        if (world is null || !world.RoomManagerRecordsLoaded || !float.IsFinite(world.HouseYardScoreMultiplier) || world.LotRoomMaps.Count == 0 ||
            world.LotRoomMaps.Values.Any(map => !map.HasAuthoritativeOutsideRooms) ||
            world.PersonRoomMotiveEntries.Values.Any(room => room.NeedsRefresh)) return null;
        var score = world.PersonRoomMotiveEntries.Values.Where(room => room.IsOutside).Sum(room => room.InteriorValue) * world.HouseYardScoreMultiplier;
        return float.IsFinite(score) ? (int)Math.Round(Math.Clamp(score, 0f, 100f), MidpointRounding.ToEven) : null;
    }

    // Calculates house upkeep score as 100 * (count - penalties) / count across catalog objects, applying
    // penalties for broken items or dirty states.
    private int? HouseUpkeepScore()
    {
        if (world is null) return null;
        var count = 0;
        var penalty = 0;
        foreach (var item in world.WalkOrder)
        {
            if (item.Definition[(int)ObjdField.RoomSort] == 0 &&
                item.Definition[(int)ObjdField.FunctionSort] == 0 &&
                item.Definition[(int)ObjdField.BuildModeType] == 0) continue;

            count++;
            if (item[ObjectData.RepairState] != 0)
                penalty += world.HouseUpkeepPenaltyPerBrokenObject;
            else if (item[ObjectData.DirtyLevel] > 0)
                penalty += world.HouseUpkeepPenaltyPerDirtyObject;
        }
        if (count == 0) return 0;
        var ratio = Math.Clamp((count - penalty) / (float)count, 0f, 1f);
        return (int)Math.Round(ratio * 100f, MidpointRounding.ToEven);
    }

    // HOUS chunk 1 stores six historical layout value pairs used to derive the layout score.
    private int? HouseLayoutScore()
    {
        if (world is null || lot is null) return null;
        return HouseLayoutHistory.Read(lot, world.HouseLayoutHistoryFillValue)?.Score;
    }

    // Maps square footage per household member to a size score using curve points (0, 0), (150, 25), (300,
    // 50), (600, 75), and (1200, 100).
    private static int HouseSizeScore(int squareFeet, int householdSize)
    {
        if (householdSize == 0) return 0;
        var areaPerMember = (double)squareFeet / householdSize;
        if (areaPerMember <= 0) return 0;
        if (areaPerMember >= 1200) return 100;
        var (start, end) = areaPerMember switch
        {
            < 150 => (0d, 150d),
            < 300 => (150d, 300d),
            < 600 => (300d, 600d),
            _ => (600d, 1200d),
        };
        var score = areaPerMember switch
        {
            < 150 => 0d,
            < 300 => 25d,
            < 600 => 50d,
            _ => 75d,
        };
        return (int)Math.Round(score + (areaPerMember - start) / (end - start) * 25d, MidpointRounding.ToEven);
    }

    private int LotWidth => world is null ? 64 : LotGeometry.LotDimension;
    private int LotHeight => world is null ? 64 : LotGeometry.LotDimension;

    private IffFile? buildableLot;
    private bool[]? buildableMask;

    // Filters buildable tiles by verifying coordinates are inside lot bounds and carry the buildable flag
    // bit.
    private bool[] Buildable()
    {
        if (lot is not { } currentLot) return Enumerable.Repeat(true, LotWidth * LotHeight).ToArray();
        if (ReferenceEquals(buildableLot, currentLot) && buildableMask is not null) return buildableMask;
        var tiles = LotTiles.Read(currentLot);
        var mask = new bool[LotWidth * LotHeight];
        for (var y = 0; y < LotHeight; y++)
            for (var x = 0; x < LotWidth; x++)
                mask[y * LotWidth + x] = tiles.InsideLot(x, y) && tiles.Buildable(x, y, 1);
        buildableLot = currentLot;
        return buildableMask = mask;
    }

    private IffFile? stateFloorsLot;
    private int stateFloorsLevel;
    private int[]? stateFloorsCache;

    private int[] StateFloors()
    {
        if (lot is not { } currentLot) return new int[LotWidth * LotHeight];
        var level = Math.Clamp(viewLevel, 1, 2);
        if (ReferenceEquals(stateFloorsLot, currentLot) && stateFloorsLevel == level && stateFloorsCache is not null)
            return stateFloorsCache;
        var result = new int[LotWidth * LotHeight];
        try
        {
            var floors = LotFloors.Read(currentLot, level);
            var pool = level == 1 ? LotPool.Read(currentLot) : null;
            var water = level == 1 ? LotWater.Read(currentLot) : null;
            for (var y = 0; y < LotHeight; y++)
            for (var x = 0; x < LotWidth; x++)
            {
                if (x >= LotFloors.Size || y >= LotFloors.Size) continue;
                int pattern = floors[x, y];
                if (pattern == byte.MaxValue) pattern = 0;
                if (pool is not null && pool.IsPool(x, y, 1)) pattern = 65535;
                else if (water is not null && water.IsWater(x, y, 1)) pattern = 65534;
                result[y * LotWidth + x] = pattern;
            }
        }
        catch { }
        stateFloorsLot = currentLot;
        stateFloorsLevel = level;
        return stateFloorsCache = result;
    }

    private IffFile? stateWallsLot;
    private int stateWallsLevel;
    private object[]? stateWallsCache;

    private object[] StateWalls()
    {
        if (lot is not { } currentLot)
            return Enumerable.Repeat((object)new { segments = (byte)0, topLeftStyle = 0, topRightStyle = 0, topLeftPattern = 0, topRightPattern = 0, bottomLeftPattern = 0, bottomRightPattern = 0 }, LotWidth * LotHeight).ToArray();
        var level = Math.Clamp(viewLevel, 1, 2);
        if (ReferenceEquals(stateWallsLot, currentLot) && stateWallsLevel == level && stateWallsCache is not null)
            return stateWallsCache;
        var result = new object[LotWidth * LotHeight];
        try
        {
            var walls = LotWalls.Read(currentLot, level);
            for (var y = 0; y < LotHeight; y++)
            for (var x = 0; x < LotWidth; x++)
            {
                if (x >= LotWalls.Size || y >= LotWalls.Size)
                {
                    result[y * LotWidth + x] = new { segments = (byte)0, topLeftStyle = 0, topRightStyle = 0, topLeftPattern = 0, topRightPattern = 0, bottomLeftPattern = 0, bottomRightPattern = 0 };
                    continue;
                }
                var cell = walls[x, y];
                result[y * LotWidth + x] = new
                {
                    segments = cell[0],
                    topLeftStyle = (int)cell[2],
                    topRightStyle = (int)cell[3],
                    topLeftPattern = (int)cell[4],
                    topRightPattern = (int)cell[5],
                    bottomLeftPattern = (int)cell[6],
                    bottomRightPattern = (int)cell[7],
                };
            }
        }
        catch
        {
            for (var i = 0; i < result.Length; i++)
                result[i] ??= new { segments = (byte)0, topLeftStyle = 0, topRightStyle = 0, topLeftPattern = 0, topRightPattern = 0, bottomLeftPattern = 0, bottomRightPattern = 0 };
        }
        stateWallsLot = currentLot;
        stateWallsLevel = level;
        return stateWallsCache = result;
    }

    private string Export()
    {
        revision++;
        animationSequence = 0;
        exportedCutawaySimKey = CutawaySimKey();
        ResolveFocusedHousePrivacy(out var effWallMode, out var effRoofsVisible, out var hideInteriorPeople);
        var sceneDirectory = SceneDirectory;
        var scenePath = world is not null && lot is not null && registry is not null
            ? SceneExporter.Export(lot, world, registry, game, sceneDirectory, house, revision, viewLevel, effWallMode, cameraOrientation, cutawayTile, effRoofsVisible, animationState, cutawayRooms, cutawaySim: CutawaySimTile(), cutawayZoom: cutawayZoom, showLoweredPortals: doorWindowBuildToolActive, hideInteriorPeople: hideInteriorPeople)
            : PlaceholderScene();
        plugins?.ExportEnvironment(Path.GetDirectoryName(scenePath)!, house);
        Write(new { type = "frame", revision, scene = scenePath, state = State() });
        return scenePath;
    }

    private void RefreshDynamicOrExport()
    {
        ResolveFocusedHousePrivacy(out var effWallMode, out var effRoofsVisible, out var hideInteriorPeople);
        if (streaming && world is not null && lot is not null && registry is not null &&
            animationState.Kept is not null && animationState.Revision == revision &&
            SceneExporter.CutawayMaskMatches(world, animationState, revision, viewLevel, effWallMode, cameraOrientation,
                cutawayTile, cutawayRooms, CutawaySimTile(), cutawayZoom))
        {
            exportedCutawaySimKey = CutawaySimKey();
            if (DirectAnimationSink is { } directSink)
            {
                if (SceneExporter.AnimationDirect(lot, world, registry, game, SceneDirectory, house, revision, animationState,
                        ++animationSequence, updates, viewLevel, effWallMode, cameraOrientation, cutawayTile, effRoofsVisible,
                        cutawayRooms: cutawayRooms, cutawaySim: CutawaySimTile(), cutawayZoom: cutawayZoom,
                        showLoweredPortals: doorWindowBuildToolActive, hideInteriorPeople: hideInteriorPeople) is { } directFrame)
                    directSink(directFrame);
            }
            else if (SceneExporter.Animation(lot, world, registry, game, SceneDirectory, house, revision, animationState,
                    ++animationSequence, updates, viewLevel, effWallMode, cameraOrientation, cutawayTile, effRoofsVisible,
                    cutawayRooms: cutawayRooms, cutawaySim: CutawaySimTile(), cutawayZoom: cutawayZoom,
                    showLoweredPortals: doorWindowBuildToolActive, hideInteriorPeople: hideInteriorPeople) is { } frame)
            {
                Write(new { type = "animation", frame });
            }
            return;
        }
        Export();
    }

    // Applies dynamic room cutaways around the active Sim only in Live mode on the currently viewed story.
    private (int X, int Y)? CutawaySimTile()
    {
        if (wallMode != "cutaway" || editingView || world?.ObjectById(selectedSim) is not XPerson person) return null;
        if (person.Position.Height != viewLevel || person.Position.X < 0 || person.Position.Y < 0) return null;
        return (person.Position.X >> 4, person.Position.Y >> 4);
    }

    // Recomputes dynamic wall cutaways whenever the selected Sim changes rooms or moves while outdoors.
    private long CutawaySimKey()
    {
        if (CutawaySimTile() is not { } tile || world is null || !world.LotRoomMaps.TryGetValue(viewLevel, out var map) ||
            tile.X >= map.Width || tile.Y >= map.Height) return -1;
        var room = map.RoomAt(tile.X, tile.Y);
        return map.IsOutside(room) ? ((long)tile.X << 40) | ((long)tile.Y << 20) | room : room;
    }

    private void InitializeViewLevelFromSelectedSim()
    {
        if (world is null)
        {
            viewLevel = 1;
            return;
        }

        var level = (world.ObjectById(selectedSim) as XPerson)?.Position.Height ?? 1;
        viewLevel = Math.Clamp(level, 1, Math.Max(1, Stories));
    }

    private void ApplyCutawayTarget(JsonElement request)
    {
        if (request.TryGetProperty("zoom", out var zoomValue) && zoomValue.ValueKind == JsonValueKind.Number && zoomValue.TryGetInt32(out var requestedZoom))
        {
            cutawayZoom = Math.Clamp(requestedZoom, 1, 3);
        }
        var cutawayX = TileCoordinate(request, "x");
        var cutawayY = TileCoordinate(request, "y");
        if (cutawayX < 0 || cutawayY < 0) ClearCutaway();
        else if (cutawayX >= LotWidth || cutawayY >= LotHeight) throw new ArgumentException("Cutaway tile is outside the lot.");
        else
        {
            cutawayTile = (cutawayX, cutawayY);
            if (world is not null && world.LotRoomMaps.TryGetValue(viewLevel, out var roomMap) &&
                cutawayX < roomMap.Width && cutawayY < roomMap.Height)
            {
                var cutawayCamera = new LotCamera(cameraOrientation, LotWidth, LotHeight);
                var hoveredRoom = roomMap.RoomAt(cutawayX, cutawayY);
                if (hoveredRoom != RoomMap.NoRoom && !cutawayRooms.Contains(hoveredRoom))
                {
                    cutawayRooms.Add(hoveredRoom);
                    if (cutawayRooms.Count > 3) cutawayRooms.RemoveAt(0);
                }
                var probeRoom = SceneExporter.CutawayProbeRoom(roomMap, cutawayCamera, LotWidth, cutawayX, cutawayY, cutawayZoom);
                if (probeRoom != RoomMap.NoRoom && !cutawayRooms.Contains(probeRoom))
                {
                    cutawayRooms.Add(probeRoom);
                    if (cutawayRooms.Count > 3) cutawayRooms.RemoveAt(0);
                }
            }
        }
    }

    private void ClearCutaway()
    {
        cutawayTile = null;
        cutawayRooms.Clear();
    }

    private string PlaceholderScene()
    {
        var sceneDir = SceneDirectory;
        Directory.CreateDirectory(sceneDir);
        var scenePath = Path.Combine(sceneDir, $"scene-{revision}.json");
        var scene = new
        {
            schemaVersion = 1,
            house,
            width = LotWidth * 16f,
            height = LotHeight * 16f,
            cameraOrientation,
            wallMode,
            wallsUp = wallMode == "up",
            viewLevel = 1,
            stories = 1,
            textures = Array.Empty<object>(),
            sprites = Array.Empty<object>(),
            meshes = Array.Empty<object>(),
            limitations = new[] { "native engine placeholder scene" },
        };
        File.WriteAllText(scenePath, JsonSerializer.Serialize(scene, Json));
        return scenePath;
    }

    private void Write(object message)
    {
        if (output is StreamWriter streamWriter && streamWriter.Encoding.CodePage == 65001)
        {
            streamWriter.Flush();
            JsonSerializer.Serialize(streamWriter.BaseStream, message, Json);
            streamWriter.BaseStream.WriteByte((byte)'\n');
            streamWriter.BaseStream.Flush();
            return;
        }
        output.WriteLine(JsonSerializer.Serialize(message, Json));
        output.Flush();
    }
}
