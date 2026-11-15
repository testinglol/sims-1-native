namespace SimsSDL;

/// <summary>
/// Plays a lot through the simulation host with the original UI: the macOS client's live-mode game logic (its app
/// delegate's event handling, clicks, hover, keys and the buy and build tools) on SDL input. Points are in canvas pixels.
/// </summary>
internal sealed partial class LiveController
{
    /// <summary>The lot's simulation host; null on the base neighborhood screen.</summary>
    public SimulationBridge? Bridge { get; private set; }
    public GameContent Content { get; }
    public string Workspace { get; }
    public LotRenderer Renderer { get; }
    public LiveUI Ui { get; }
    internal Options Options { get; }
    private OriginalUIStrings Strings => Ui.Strings;
    public bool HasBlockingDialog => Ui.GameDialog?.Dialog.Blocking == true && Ui.GameDialog?.Submitting == false;
    private bool _tutorialLiveModeClicked;

    private sealed class MenuRequest
    {
        public required string Id { get; init; }
        public required int Object { get; init; }
        public required int Caller { get; init; }
        public required CGPoint Anchor { get; init; }
        public (GroundTile Tile, int Level)? Ground { get; set; }
    }

    private (GroundTile Tile, int Level)? _pieGround;

    private readonly record struct PlacementKey(
        BuySelection.SelectionTarget Target,
        GroundTile Tile,
        int Direction,
        long Revision,
        int Level);

    private readonly record struct WallStrokeMode(bool Rectangle, bool Deleting);

    private readonly record struct WallStrokeSegment(WallBrushSelection Selection, ushort Style, ushort Pattern);

    private sealed class WallStroke
    {
        public required LotVertex Start { get; set; }
        public required ushort Style { get; init; }
        public required ushort Pattern { get; init; }
        public required WallStrokeMode Mode { get; set; }
        public List<WallStrokeSegment> Completed { get; } = [];
    }

    private sealed class LotSimInfo
    {
        public required string TooltipName { get; init; }
        public required int Gender { get; init; }
        public required uint Guid { get; init; }
        public required bool HideHover { get; init; }
        public int FameScore { get; set; }
        public int JobType { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public int Level { get; set; } = 1;
    }

    private readonly record struct PlacedObjectMeta(
        float X,
        float Y,
        int Level,
        int BuildModeType,
        int ResaleValue,
        string CatalogName);

    private int _selectedSim;
    private bool _windowActive = true;
    private List<int> _simIDs = [];
    private Dictionary<int, LotSimInfo> _lotSims = new();
    private List<string> _simAnimations = [];
    public bool ScriptSimIdle { get; private set; }
    /// <summary>The selected Sim's queued action names, for <c>--script</c> failure reports.</summary>
    public List<string> ScriptSimActions { get; private set; } = [];
    /// <summary>A state that arrived before the lot scene loaded, applied to the UI once it has.</summary>
    private Dictionary<string, JsonElement>? _stateAwaitingScene;
    /// <summary>A newly opened lot centres the view on the selected Sim once both are drawn, as the original does on entry.</summary>
    private bool _centresOnSelectedSim;
    private int? _edgeScrollRegionKey;
    private CGPoint? _rightOrigin;
    private CGPoint? _rightCurrent;
    private bool _rightScrolling;
    private double _edgeScrollStart;
    private double _edgeScrollClock;
    private MenuRequest? _pendingMenu;
    private CGPoint? _pendingPieMenuWarp;

    private readonly record struct PieHeadKey(OriginalPieOverlay Menu, int Sim, float Yaw, float Pitch);

    private PieHeadKey? _desiredPieHead;
    private PieHeadKey? _checkedPieHead;
    private (string Id, PieHeadKey Key)? _pendingPieHead;
    private string? _pendingGenericSimsCallPoll;
    private string? _pendingGenericSimsCallChoice;
    private double _lastGenericSimsCallPoll;
    private long? _activeGenericSimsCallRequest;
    private string? _pendingCatalog;
    private string? _pendingBuildCatalog;
    private string? _pendingWallMode;
    private bool? _pendingRoofsVisible;
    private int? _pendingViewLevel;
    private string? _pendingViewRequestID;
    private long? _pendingViewSceneRevision;
    private long _latestQueuedSceneRevision;
    private string EffectiveWallMode => _pendingWallMode ?? Ui.WallMode;
    private bool EffectiveRoofsVisible => _pendingRoofsVisible ?? Renderer.RoofsVisible;
    private int EffectiveViewLevel => _pendingViewLevel ?? Renderer.ViewLevel;

    private void ClearPendingViewState()
    {
        _pendingWallMode = null;
        _pendingRoofsVisible = null;
        _pendingViewLevel = null;
        _pendingViewRequestID = null;
        _pendingViewSceneRevision = null;
    }

    private (string Id, PlacementKey Key)? _pendingPlacement;
    private PlacementKey? _desiredPlacement;
    private PlacementKey? _checkedPlacement;
    private List<EditableLotObject> _editableObjects = [];
    private Dictionary<int, PlacedObjectMeta> _placedObjectMeta = new();
    private HashSet<string> _pendingSaleRequests = new(StringComparer.Ordinal);
    private (string Id, int ObjectID)? _pendingDisposePreviewRequest;
    /// <summary>Every object on the lot from the last host state, by simulation ID order, for <c>--script</c>.</summary>
    public CGPoint? ScriptSimPosition { get; private set; }
    private int? _scriptSimLevel;
    public List<(int Id, string Name)> ScriptObjects { get; private set; } = [];
    public bool? ScriptFreeWill { get; private set; }

    /// <summary>The original build cursor (the wall, wallpaper and terrain tools' object at the hovered vertex or wall), drawn by the host.</summary>
    private record struct BuildCursorKey
    {
        public required uint Guid { get; init; }
        public required int Graphic { get; init; }
        public required LotVertex Vertex { get; init; }
        public required int Level { get; init; }
        public int Direction { get; set; } = 1;
        public required SimsSDL.Core.LotCamera Orientation { get; init; }
        public bool Centered { get; set; }
        public ushort? Pattern { get; set; }
        public int WallDirection { get; set; }
        public int? WallEdge { get; set; }
        public int? WallSide { get; set; }
        public bool Fill { get; set; }
        public required long Revision { get; init; }

        public BuildCursorKey() { }
    }

    private BuildCursorKey? _desiredBuildCursor;
    /// <summary>The tiles the build tools may change on the viewed level, from the last host state.</summary>
    private BuildableArea? _buildable;
    /// <summary>When the build tools last refused an unbuildable spot; the original repeats its error sound at most every 400 ms.</summary>
    private ulong _lastBuildRefusal;
    private BuildCursorKey? _checkedBuildCursor;
    private (string Id, BuildCursorKey Key)? _pendingBuildCursor;
    private Dictionary<string, (string Success, string? Failure)> _pendingResponseSounds = new(StringComparer.Ordinal);
    private HashSet<string> _pendingSaveRequests = new(StringComparer.Ordinal);
    private Dictionary<string, Action<bool>> _pendingSaveCompletions = new(StringComparer.Ordinal);
    private HashSet<string> _pendingLoadRequests = new(StringComparer.Ordinal);
    private HashSet<string> _pendingScriptTileTraversals = new(StringComparer.Ordinal);
    private Dictionary<string, (List<int>? ObjectIDs, string? Error)> _scriptTileTraversalResults = new(StringComparer.Ordinal);
    private string? _pendingAudioState;
    private string? _pendingResetTutorialState;
    private Dictionary<string, OriginalSoundOptionsOverlay.VolumeChange> _pendingAudioVolumes = new(StringComparer.Ordinal);
    private Dictionary<string, string> _latestAudioVolumeRequest = new(StringComparer.Ordinal);
    private bool _soundGesture;
    /// <summary>Where a held object is being put down: dragging from here turns it, and releasing places it on this tile.</summary>
    private (CGPoint Point, GroundTile Tile)? _placementAnchor;
    private int? _lastPieHighlight;
    private string? _lastCutawayTile;
    private (string Key, int X, int Y, int Zoom, double Time)? _desiredCutaway;
    public CGRect? PendingPhoto { get; private set; }
    /// <summary>The caption of an automatic snapshot (the Special Effect's STR# 0x131 text); null for a photo from the camera mode.</summary>
    private string? _pendingPhotoCaption;
    private bool _pendingPhotoIsAutomatic;

    /// <summary>The open picture-in-picture window (<c>cWinPictureInPicture</c>) and the lot-view rectangle its next image is read from.</summary>
    private sealed class PictureInPictureWindow
    {
        public required int ObjectID { get; init; }
        public DateTime? Expires { get; set; }
        public int SizeIndex { get; set; }
        public int Zoom { get; set; }
        public string? Text { get; set; }
        public bool SnapshotPending { get; set; }
        public bool Built { get; set; }
    }

    private PictureInPictureWindow? _pictureInPicture;
    private bool _pipNeedsCapture;
    private RenderTargets? _pipRenderTargets;
    private int _pipPanAssignmentsBias;
    private bool _pipCloseTracking;
    private bool _dialogButtonTracking;
    private bool _chooserOKTracking;
    private int? _pendingPipCenterObjectID;
    private bool _pendingPipCenterChangesLevel;
    private long? _lastPictureInPictureSequence;
    /// <summary><c>CPState</c> keeps its selected speed when the separate pause flag is toggled; preserve it while the host represents pause as speed 0.</summary>
    private int _lastRunningSpeed = 1;
    private bool _liveModePaused;
    private string? _localDialogToken;
    private Action<byte>? _localDialogResponse;
    private HashSet<string> _answeredDialogs = new(StringComparer.Ordinal);
    /// <summary>The last pointer position over the window, for placement previews refreshed by host state.</summary>
    public CGPoint? Pointer { get; private set; }
    private CGSize _canvas = new(1, 1);
    public double ScriptPixelDensity { get; set; } = 1;
    /// <summary>Moves the real pointer, in canvas pixels (the window sets it).</summary>
    public Action<CGPoint>? WarpPointer { get; set; }
    private WallStroke? _wallStroke;
    private WallBrushSelection? _wallBrush;
    private (GroundTile Start, ushort Pattern, ushort? Selection, bool Deleting)? _floorStroke;
    private FloorBrushSelection? _floorBrush;
    private bool _floorBrushDeleting;
    private string? _floorBrushTexture;
    private (bool Deleting, ushort Pattern, HashSet<LotScene.WallSide> Visited)? _wallpaperStroke;
    private ushort _lastBuildModifiers;
    private TerrainBrushMode? _terrainStrokeMode;
    private CGPoint? _terrainStrokeStartPoint;
    private string? _terrainStrokeID;
    private byte? _terrainStrokeBaseHeight;
    private short? _lastStreamedTerrainDelta;
    private bool _terrainStrokePlayedSound;
    private TerrainBrushGesture _terrainGesture = new();
    /// <summary>A left press that started a build stroke; its drags and release go to the stroke instead of panning the view.</summary>
    public bool PrimaryGesture { get; private set; }
    public bool QuitRequested { get; set; }

    /// <summary>FUN_004f1b96: saving is off on community lots and Magic Town lots opened from the main neighborhood (Options skips the save prompts).</summary>
    private bool _lotSaveDisabled;
    private bool LotSaveDisabled
    {
        get => _lotSaveDisabled;
        set
        {
            _lotSaveDisabled = value;
            Ui.OptionsPanel.LotSaveDisabled = value;
        }
    }

    public int? HomeLotHouse { get; private set; }

    private bool _awayBuyBuildLocked;
    public bool AwayBuyBuildLocked
    {
        get => _awayBuyBuildLocked;
        private set
        {
            _awayBuyBuildLocked = value;
            Ui.AwayBuyBuildLocked = value;
        }
    }

    private bool _simlessBuildMode;
    private SDLAudioPlayer? _audio;
    private Vector2 _lastAudioPan = Vector2.Zero;
    private LotZoom _lastAudioZoom = LotZoom.Medium;
    private long _lastAudioSceneRevision = -1;
    private long _lastAudioAnimationSequence = -1;

    // Neighborhood, Select a Family, Create a Family and wardrobe state, as the macOS app delegate keeps it.
    private int _currentNeighborhood;
    private int? _currentHouse;
    private int? _lastNeighborhoodMode;
    private Action<int>? _neighborhoodHouseSelected;
    private bool _rezoneMode;
    private bool _evictMode;
    private OriginalNeighborhoodOverlay? _neighborhoodBeforeHouseholdCreation;
    private int? _pendingHouseholdCreation;
    private string? _pendingHouseholdCreationRequest;
    private string? _pendingHouseholdAppearanceRequest;
    private (string Gender, string Age, string Skin)? _deferredAppearanceCatalogRequest;
    private (string Gender, string Age, string Skin)? _queuedAppearanceCatalogRequest;
    private string? _pendingAppearanceCatalogKey;
    private bool _prewarmingAppearanceCatalog;
    private Dictionary<string, SimsSDL.Core.HouseholdAppearanceCatalog> _cachedAppearanceCatalogs = new(StringComparer.Ordinal);
    private bool _bridgeConnected;
    private bool _daemonMode;
    private bool _daemonWarmedUp;

    private readonly record struct DaemonOpenLotResult(bool Simless, bool Away, int? HomeHouse);

    private List<TaskCompletionSource> _pendingHelloContinuations = [];
    private Dictionary<string, (string Session, TaskCompletionSource<(string Session, string Manifest)> Continuation)> _pendingDaemonNeighborhoodRequests = new(StringComparer.Ordinal);
    private Dictionary<string, TaskCompletionSource<DaemonOpenLotResult>> _pendingDaemonOpenLotRequests = new(StringComparer.Ordinal);

    private bool HasActiveLot =>
        Bridge is not null && (_currentHouse ?? 0) > 0 && Ui.Neighborhood is null && Ui.HouseholdCreation is null;

    private bool IsLivePanelMinimized =>
        Ui.PanelMinimized && IsInLiveMode;

    private string? _pendingHouseholdPreviewRequest;
    private (string Gender, string Age, string Skin, string Head, string Body)? _queuedAppearancePreviewRequest;
    /// <summary>The preview key the pending request fills: 0 for the Sim being edited, 1–8 for a Create a Family member slot.</summary>
    private int _pendingHouseholdPreviewKey;
    private string _pendingHouseholdPreviewSignature = string.Empty;
    /// <summary>The appearance each member slot's preview was loaded for, so a slot is fetched again only when its Sim changes.</summary>
    private Dictionary<int, string> _familyPortraits = new();
    private (int House, int Family, string? Request)? _pendingFamilyMoveIn;

    /// <summary>A bin family chosen with Move In from the banner's Select a Family; the next vacant lot clicked is offered to it.</summary>
    private OriginalFamilyPickerOverlay.Family? _familyPlacement;
    private OriginalFamilyPickerOverlay.Family? FamilyPlacement
    {
        get => _familyPlacement;
        set
        {
            _familyPlacement = value;
            Ui.Neighborhood?.SetMovingInFamily(value);
        }
    }

    /// <summary>Create a Family was opened from the banner, so the new family goes to the bin instead of the host lot.</summary>
    private bool _householdCreationUnhoused;
    /// <summary>Unhoused Create a Family started from Select a Family, so finish/cancel returns directly to Select a Family.</summary>
    private bool _returnToFamilyPickerAfterHouseholdCreation;

    private readonly record struct NeighborhoodRequest(string Id, GameDialog Dialog, int Mode);

    private NeighborhoodRequest? _pendingNeighborhood;
    private string? _pendingNeighborhoodResponse;
    private GameDialog? _activeNeighborhoodDialog;
    private HashSet<string> _failedNeighborhoodTokens = new(StringComparer.Ordinal);
    private (string Id, GameDialog Dialog)? _pendingOutfitChoices;
    private string? _pendingOutfitPreview;
    private bool _loadingNeighborhood;
    private bool _editingNeighborhood;
    private bool _exportingWebPages;
    private bool _openingNeighborhoodLot;
    private bool _switchingLot;
    private bool _awaitingLotSwitchChange;
    /// <summary><c>Sims.exe</c> <c>004fb1a5.c:48-57</c>: permanently retires <c>cTSAppSplashWindow</c> (<c>setup.bmp</c> + <c>loadloop.wav</c>) once the initial launch finishes.</summary>
    private bool _hasCompletedInitialLoad;
    private long _highestReceivedSceneRevision;
    private long _minimumSceneRevisionToFinishLoading = 1;
    private int? _lastHouseStateClass;
    private int _lastLotTransitInfo;
    private (int Destination, OriginalTravelLoadScreen? Screen)? _pendingTravelSelection;
    private ulong _neighborhoodLoadGeneration;
    private int? _pendingNeighborhoodTarget;
    public bool IsLoadingNeighborhood => _loadingNeighborhood;
    private List<string> _splashProgressQueue = [];
    private double _lastSplashProgressStep;
    private SDLAudioPlayer? _startupLoadAudio;
    // The audio-only host that plays the neighborhood maps' music and ambience while no lot is loaded.
    private SimulationBridge? _frontendAudio;
    private SDLAudioPlayer? _frontendAudioPlayer;
    private bool _frontendAudioReady;
    private List<(string Operation, Dictionary<string, object?> Parameters)> _pendingFrontendAudio = [];
    /// <summary>Set by the script command <c>autodismiss</c>: original dialogs are answered with their first button as they open.</summary>
    public bool ScriptAnswersDialogs { get; set; }
    public string? ScriptDialogStop { get; set; }
    private string? _frontendMusic;
    private bool UsesNativeEngine => SimulationEngineSelection.UsesNativeEngine();
    /// <summary>The last values each plugin reported for its panels, by pack and panel ID.</summary>
    private Dictionary<string, Dictionary<string, Dictionary<string, string>>> _pluginPanelValues = new(StringComparer.Ordinal);

    // Later stored fields declared across LiveController.swift so every partial shares one field layout.
    private bool _catalogPopupWasVisible;
    /// <summary>
    /// The original cursor file (without <c>.cur</c>) for the pointer: <c>LivePerson</c> over a Sim, <c>LiveObjectAvail</c> over an object and
    /// <c>LiveNothing</c> over open ground in live mode; the plain arrow over panels, menus, dialogs and the other modes.
    /// </summary>
    public string HoverCursor { get; private set; } = "Arrow";
    /// <summary>Ctrl or Option held: the original delete modifier (see <c>Press</c>); set each frame by the SDL loop.</summary>
    public bool DeleteModifier { get; set; }
    /// <summary>The same modifier as held by a <c>--script</c> <c>hold</c> line.</summary>
    public bool ScriptDeleteModifier { get; set; }
    /// <summary>Shift modifier as held by a <c>--script</c> <c>hold</c> line.</summary>
    public bool ScriptShiftModifier { get; set; }
    private double _lastTutorialPanelCheck;
    private LotZoom? _simulatorZoom;
    private string? _simulatorMode;
    private bool? _simulatorViewScrolling;
    private Vector2? _lastSimulatorPan;
    private bool _rightSelectedSim;
    private GameplayPanelState.Relationship.LivePosition? _relationshipCenter;
    private bool _relationshipCenterChangesLevel;

    internal static void Log(string message) => Console.Error.WriteLine($"SimsSDL: {message}");

    private static double CurrentUptime() =>
        System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    private static void RunOnMainThread(Action action)
    {
        if (MainThreadDispatcher.IsMainThread)
        {
            action();
        }
        else
        {
            MainThreadDispatcher.Post(action);
        }
    }

    private static Task<T> RunOnMainThreadAsync<T>(Func<T> func)
    {
        if (MainThreadDispatcher.IsMainThread)
        {
            return Task.FromResult(func());
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThreadDispatcher.Post(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>Opens <paramref name="house"/> live, or the base neighborhood screen when <paramref name="house"/> is null.</summary>
    internal LiveController(Options options, int? house, LotRenderer renderer)
    {
        string workspace = options.Workspace ?? InstallLocator.DefaultWorkspace();
        string dataRoot = options.DataRoot ?? InstallLocator.DefaultDataRoot(workspace);
        var resolvedOptions = options;
        resolvedOptions.Workspace = workspace;
        resolvedOptions.DataRoot = dataRoot;
        string game = resolvedOptions.Game
            ?? InstallLocator.ResolveGameDirectory(explicitPath: null, workspace: workspace, dataRoot: dataRoot)
            ?? Path.Combine(workspace, "runtime", "game");
        resolvedOptions.Game = game;
        Content = PackTool.LoadContent(game, resolvedOptions.Packs);
        Workspace = workspace;
        Options = resolvedOptions;
        Renderer = renderer;
        _currentNeighborhood = resolvedOptions.Neighborhood;
        Ui = new LiveUI(Content);
        if (!options.AnswerDialogs)
        {
            ReviewPluginTrust();
        }

        if (house is int activeHouse)
        {
            if (UsesNativeEngine)
            {
                _daemonMode = true;
            }
            StartSimulation(activeHouse);
        }
        else if (UsesNativeEngine)
        {
            _daemonMode = true;
            StartWarmDaemon();
        }
        else
        {
            LoadBaseNeighborhood();
        }
    }

    /// <summary>The profile's saved music master volume (HIT group 1, <c>Settings/audio.json</c>), or full volume.</summary>
    private float LoadLoopVolume()
    {
        string profile = Path.Combine(Options.DataRoot ?? Workspace, "runtime", "profiles", Options.Profile, "Settings", "audio.json");
        try
        {
            if (!File.Exists(profile))
            {
                return 1f;
            }

            byte[] data = File.ReadAllBytes(profile);
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return 1f;
            }

            if (!doc.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out int schemaVersion) ||
                schemaVersion != 1)
            {
                return 1f;
            }

            if (!doc.RootElement.TryGetProperty("volumes", out var volumes) ||
                volumes.ValueKind != JsonValueKind.Array ||
                volumes.GetArrayLength() != 4)
            {
                return 1f;
            }

            var group1 = volumes[1];
            if (group1.ValueKind != JsonValueKind.Number || !group1.TryGetSingle(out float value))
            {
                return 1f;
            }

            return float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f;
        }
        catch
        {
            return 1f;
        }
    }

    private static string? LoadLoopURL(GameContent content)
    {
        string path = content.Root;
        string[] parts = ["Music", "Modes", "Load", "loadloop.wav"];
        foreach (string part in parts)
        {
            try
            {
                string? next = null;
                foreach (string entry in Directory.EnumerateFileSystemEntries(path))
                {
                    if (string.Equals(Path.GetFileName(entry), part, StringComparison.OrdinalIgnoreCase))
                    {
                        next = entry;
                        break;
                    }
                }

                if (next is null)
                {
                    return null;
                }

                path = next;
            }
            catch
            {
                return null;
            }
        }

        return path;
    }

    /// <summary>Starts the persistent warm simulation daemon (<c>house == 0</c>) so neighborhood loads, edits, CAS, audio, and lot opens share one host process.</summary>
    private void StartWarmDaemon()
    {
        _currentHouse = 0;
        HomeLotHouse = null;
        AwayBuyBuildLocked = false;
        _simlessBuildMode = false;
        Ui.PanelMinimized = false;
        StopFrontendAudio();
        var bridge = new SimulationBridge(Workspace, Options.DataRoot, Options.Profile, _currentNeighborhood);
        bridge.DiscardsContentOnExit = true;
        Bridge = bridge;
        _bridgeConnected = false;
        _daemonWarmedUp = false;
        Renderer.LiveMode = false;
        Renderer.DrawsLot = false;
        Renderer.ApplyEnvironment(null);
        BeginStartupSplash(startingAt: 3);
        AttachBridgeCallbacks(bridge);
        _audio?.Close();
        _audio = null;
        _lastAudioSceneRevision = -1;
        _lastAudioAnimationSequence = -1;
        if (Options.Audio)
        {
            try
            {
                var audio = new SDLAudioPlayer(bridge.Session);
                audio.Ended = voice => RunOnMainThread(() =>
                {
                    if (ReferenceEquals(_audio, audio))
                    {
                        _ = Send("audioEnded", new Dictionary<string, object?> { ["voice"] = voice });
                    }
                });
                audio.Spatial = (owner, panned, zoom) => SpatialMix(owner, panned, zoom);
                _audio = audio;
                if (LoadLoopURL(Content) is { } loop)
                {
                    audio.PlayLoadLoop(loop, LoadLoopVolume());
                }
            }
            catch (Exception error)
            {
                Log($"audio disabled: {error}");
            }
        }

        _ = StartWarmDaemonCoreAsync(bridge);
    }

    private async Task StartWarmDaemonCoreAsync(SimulationBridge bridge)
    {
        var weakSelf = new WeakReference<LiveController>(this);
        var weakBridge = new WeakReference<SimulationBridge>(bridge);
        try
        {
            await bridge.StartDaemonAsync(Content, nativeEngine: true).ConfigureAwait(false);
            bool active = await RunOnMainThreadAsync(() =>
            {
                if (!weakSelf.TryGetTarget(out var self) || !weakBridge.TryGetTarget(out var target))
                {
                    return false;
                }

                if (!ReferenceEquals(self.Bridge, target))
                {
                    target.Stop(save: false);
                    _ = target.WaitForExit(10);
                    return false;
                }

                Log($"daemon session {target.Session}");
                return true;
            }).ConfigureAwait(false);
            if (!active)
            {
                return;
            }

            await WaitForBridgeConnected().ConfigureAwait(false);
            MainThreadDispatcher.Post(() =>
            {
                if (!weakSelf.TryGetTarget(out var self) || !weakBridge.TryGetTarget(out var target))
                {
                    return;
                }

                if (!ReferenceEquals(self.Bridge, target))
                {
                    return;
                }

                self.LoadBaseNeighborhood(mode: 1);
            });
        }
        catch (Exception error)
        {
            MainThreadDispatcher.Post(() =>
            {
                if (!weakSelf.TryGetTarget(out var self) || !weakBridge.TryGetTarget(out var target))
                {
                    return;
                }

                if (!ReferenceEquals(self.Bridge, target))
                {
                    return;
                }

                Log($"Simulation daemon: {error}");
                self._audio?.StopLoadLoop();
                self.Ui.Loading = false;
                self._loadingNeighborhood = false;
                self.ShowNeighborhoodError(error);
            });
        }
    }

    private void AttachBridgeCallbacks(SimulationBridge bridge)
    {
        var weakSelf = new WeakReference<LiveController>(this);
        var weakBridge = new WeakReference<SimulationBridge>(bridge);
        bridge.Event = evt => RunOnMainThread(() =>
        {
            if (!weakSelf.TryGetTarget(out var self) || !weakBridge.TryGetTarget(out var target))
            {
                return;
            }

            if (!ReferenceEquals(self.Bridge, target))
            {
                return;
            }

            EventProfiler.Measure(evt.JsonString("type") ?? "response", () => self.SimulationEvent(evt));
        });
        bridge.Animation = frame => MainThreadDispatcher.Post(() =>
        {
            if (!weakSelf.TryGetTarget(out var self) || !weakBridge.TryGetTarget(out var target))
            {
                return;
            }

            if (!ReferenceEquals(self.Bridge, target))
            {
                return;
            }

            self.Renderer.QueueAnimation(frame);
        });
        bridge.Failed = error => RunOnMainThread(() =>
        {
            string sessionPath = weakBridge.TryGetTarget(out var targetForLog) ? targetForLog.Session : string.Empty;
            Log($"{error} (log: {Path.Combine(sessionPath, "simulation.log")})");
            if (!weakSelf.TryGetTarget(out var self) || targetForLog is null)
            {
                return;
            }

            if (!ReferenceEquals(self.Bridge, targetForLog))
            {
                return;
            }

            self.CancelPendingDaemonContinuations(new ContentError(error));
        });
    }

    private Task WaitForBridgeConnected()
    {
        if (MainThreadDispatcher.IsMainThread)
        {
            if (_bridgeConnected)
            {
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingHelloContinuations.Add(tcs);
            return tcs.Task;
        }

        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThreadDispatcher.Post(() =>
        {
            if (_bridgeConnected)
            {
                waiter.TrySetResult();
            }
            else
            {
                _pendingHelloContinuations.Add(waiter);
            }
        });
        return waiter.Task;
    }

    private void CancelPendingDaemonContinuations(Exception error)
    {
        var hellos = _pendingHelloContinuations;
        _pendingHelloContinuations = [];
        foreach (var continuation in hellos)
        {
            continuation.TrySetException(error);
        }

        var neighborhoods = _pendingDaemonNeighborhoodRequests;
        _pendingDaemonNeighborhoodRequests = new Dictionary<string, (string Session, TaskCompletionSource<(string Session, string Manifest)> Continuation)>(StringComparer.Ordinal);
        foreach (var entry in neighborhoods.Values)
        {
            entry.Continuation.TrySetException(error);
        }

        var openLots = _pendingDaemonOpenLotRequests;
        _pendingDaemonOpenLotRequests = new Dictionary<string, TaskCompletionSource<DaemonOpenLotResult>>(StringComparer.Ordinal);
        foreach (var continuation in openLots.Values)
        {
            continuation.TrySetException(error);
        }
    }

    /// <summary>Starts the simulation host on a lot, with its own audio player.</summary>
    private void StartSimulation(int house)
    {
        _currentHouse = house;
        HomeLotHouse = _pendingHouseholdCreation is null ? house : null;
        AwayBuyBuildLocked = false;
        _simlessBuildMode = false;
        Ui.PanelMinimized = false;
        if (_daemonMode)
        {
            _daemonWarmedUp = true;
        }
        StopFrontendAudio();
        var bridge = new SimulationBridge(Workspace, Options.DataRoot, Options.Profile, _currentNeighborhood);
        bridge.DiscardsContentOnExit = true;
        Bridge = bridge;
        _bridgeConnected = false;
        _deferredAppearanceCatalogRequest = null;
        _queuedAppearanceCatalogRequest = null;
        _pendingAppearanceCatalogKey = null;
        _prewarmingAppearanceCatalog = false;
        _cachedAppearanceCatalogs.Clear();
        _queuedAppearancePreviewRequest = null;
        _simulatorZoom = null;
        _simulatorMode = null;
        _simulatorViewScrolling = null;
        _lastSimulatorPan = Renderer.Pan;
        _tutorialLiveModeClicked = false;
        _lastRunningSpeed = Math.Max(1, Options.Speed ?? 1);
        _liveModePaused = Options.Speed == 0;
        _highestReceivedSceneRevision = 0;
        _minimumSceneRevisionToFinishLoading = 1;
        _awaitingLotSwitchChange = false;
        _switchingLot = false;
        _pendingTravelSelection = null;
        _lastHouseStateClass = null;
        _lastLotTransitInfo = 0;
        Renderer.LiveMode = true;
        Renderer.DrawsLot = Ui.ShowsLot;
        Renderer.ApplyEnvironment(null);
        AttachBridgeCallbacks(bridge);
        _audio?.Close();
        _audio = null;
        _lastAudioSceneRevision = -1;
        _lastAudioAnimationSequence = -1;
        if (Options.Audio)
        {
            try
            {
                var audio = new SDLAudioPlayer(bridge.Session);
                audio.Ended = voice => RunOnMainThread(() =>
                {
                    if (ReferenceEquals(_audio, audio))
                    {
                        _ = Send("audioEnded", new Dictionary<string, object?> { ["voice"] = voice });
                    }
                });
                audio.Spatial = (owner, panned, zoom) => SpatialMix(owner, panned, zoom);
                _audio = audio;
            }
            catch (Exception error)
            {
                Log($"audio disabled: {error}");
            }
        }

        if (_pendingHouseholdCreation is int lot)
        {
            // Sims.exe (0x00564119) opens Create a Family directly from Select a Family without the lot loading splash or loop.
            Ui.Loading = false;
            _openingNeighborhoodLot = false;
            _hasCompletedInitialLoad = true;
            try
            {
                OpenHouseholdCreationOverlay(lot);
            }
            catch
            {
                // Keep Swift `try? openHouseholdCreationOverlay(lot: lot)` tolerance.
            }
        }
        else if (!_hasCompletedInitialLoad)
        {
            // Cold launch into `--house`: show cTSAppSplashWindow (`setup.bmp`, ticker, `loadloop.wav`) once until the first scene renders.
            _openingNeighborhoodLot = false;
            BeginStartupSplash(startingAt: 5);
            if (_audio is { } activeAudio && LoadLoopURL(Content) is { } loop)
            {
                activeAudio.PlayLoadLoop(loop, LoadLoopVolume());
            }
        }
        else
        {
            // Entering a lot from Neighborhood (`00556352`, `00558829`, `004f1b96`): keep Neighborhood visible under the hourglass cursor.
            Ui.Loading = false;
            _openingNeighborhoodLot = true;
        }

        _centresOnSelectedSim = Ui.OptionsPanel.PlayOptions.AutoCenter;
        _pendingPieHead = null;
        _desiredPieHead = null;
        _checkedPieHead = null;
        _ = StartSimulationCoreAsync(bridge, house);
    }

    private async Task StartSimulationCoreAsync(SimulationBridge bridge, int house)
    {
        var weakSelf = new WeakReference<LiveController>(this);
        var weakBridge = new WeakReference<SimulationBridge>(bridge);
        try
        {
            await bridge.StartAsync(Content, house, UsesNativeEngine).ConfigureAwait(false);
            MainThreadDispatcher.Post(() =>
            {
                if (!weakSelf.TryGetTarget(out var self) || !weakBridge.TryGetTarget(out var target))
                {
                    return;
                }

                if (!ReferenceEquals(self.Bridge, target))
                {
                    target.Stop(save: false);
                    _ = target.WaitForExit(10);
                    return;
                }

                Log($"live session {target.Session}");
            });
        }
        catch (Exception error)
        {
            MainThreadDispatcher.Post(() =>
            {
                if (!weakSelf.TryGetTarget(out var self) || !weakBridge.TryGetTarget(out var target))
                {
                    return;
                }

                if (!ReferenceEquals(self.Bridge, target))
                {
                    return;
                }

                Log($"Open lot {house}: {error}");
                self.Bridge = null;
                self._audio?.Close();
                self._audio = null;
                self.Ui.Loading = false;
                self._openingNeighborhoodLot = false;
                self._switchingLot = false;
                self._awaitingLotSwitchChange = false;
                self._hasCompletedInitialLoad = true;
                self._pendingFamilyMoveIn = null;
                self.RestoreNeighborhoodAfterHouseholdCreation();
                self.ShowNeighborhoodError(error);
            });
        }
    }

    // Requests

    /// <summary>
    /// Asks the host for the selected Sim's head at the menu's current orientation, one request at a time, so the head
    /// follows the pointer continuously as in the original.
    /// </summary>
    private void RequestPieHeadPreview()
    {
        if (Ui.PieMenu is not { } menu)
        {
            _desiredPieHead = null;
            return;
        }

        _desiredPieHead = new PieHeadKey(
            menu,
            menu.SelectedSim,
            MathF.Round(menu.HeadOrientation.X * 2f, MidpointRounding.AwayFromZero) / 2f,
            MathF.Round(menu.HeadOrientation.Y * 2f, MidpointRounding.AwayFromZero) / 2f);
        if (_pendingPieHead is not null || _desiredPieHead is not { } key || key == _checkedPieHead)
        {
            return;
        }

        string? id = Send("pieHeadPreview", new Dictionary<string, object?> { ["yaw"] = key.Yaw, ["pitch"] = key.Pitch });
        if (id is null)
        {
            return;
        }

        _pendingPieHead = (id, key);
    }

    private string? Send(string operation, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        if (Bridge is not { } bridge)
        {
            return null;
        }

        try
        {
            return bridge.Send(operation, parameters);
        }
        catch (Exception error)
        {
            Log($"{operation}: {error}");
            return null;
        }
    }

    /// <summary>The UI sounds asked for while a script runs (<c>waitfor sound</c>), whether or not audio is on.</summary>
    public List<string>? ScriptUISounds { get; set; }

    public void PlayUISound(string name)
    {
        ScriptUISounds?.Add(name);
        if (Bridge is null)
        {
            SendFrontendAudio("audioEvent", new Dictionary<string, object?> { ["name"] = name });
            return;
        }

        if (_audio is not null)
        {
            _ = Send("audioEvent", new Dictionary<string, object?> { ["name"] = name });
        }
    }

    /// <summary>Buy and build mode music depends on the lot (expansion lots have their own tracks), so the simulation picks the event.</summary>
    private void PlayModeMusic(string mode)
    {
        if (Bridge is not null && _audio is not null)
        {
            _ = Send("modeMusic", new Dictionary<string, object?> { ["mode"] = mode });
        }
    }

    /// <summary>
    /// The pan and gain of a sound from an object, from where the object sits in the view, as the macOS client mixes it.
    /// An object with no drawn sprite or mesh (one above the view) is placed from its tile, and a sound from another floor
    /// is quieter than one on the viewed floor (<see cref="PositionalSound.FloorGain"/>).
    /// </summary>
    private (float Pan, float Gain) SpatialMix(int owner, bool panned, bool zoom)
    {
        if (Renderer.Scene is not { } scene || Renderer.SceneViewport(SimdCanvas) is not { } viewport)
        {
            return (0f, 1f);
        }

        (int Level, Vector2 Point)? placed = null;
        if (scene.Sprites.FirstOrDefault(s => s.ObjectID == owner) is { } sprite)
        {
            placed = (sprite.Level ?? 1, new Vector2(sprite.X + sprite.Width / 2f, sprite.Y + sprite.Height / 2f));
        }
        else if (scene.Meshes is { } meshes
                 && meshes.FirstOrDefault(m => m.ObjectID == owner) is { } mesh
                 && mesh.Vertices.Count > 0)
        {
            var vertex = mesh.Vertices[0];
            placed = (mesh.Level ?? 1, new Vector2(vertex.X, vertex.Y));
        }
        else if (scene.SoundSources is { } sources
                 && sources.FirstOrDefault(s => s.ObjectID == owner) is { } source
                 && PositionalSound.ScenePoint(source, scene) is { } sourcePoint)
        {
            placed = (source.Level, sourcePoint);
        }

        if (placed is not { } target)
        {
            return (0f, zoom ? Renderer.Zoom.SoundGain() : 1f);
        }

        var mix = PositionalSound.Mix(target.Point * viewport.Scale + viewport.Origin, viewport.Size, panned);
        float floor = PositionalSound.FloorGain(target.Level, Renderer.ViewLevel);
        return (mix.Pan, mix.Gain * floor * (zoom ? Renderer.Zoom.SoundGain() : 1f));
    }

    /// <summary>Once a frame: finished voices go back to the host and positional sounds follow the view.</summary>
    public void UpdateAudio()
    {
        _frontendAudioPlayer?.Poll();
        if (_audio is not { } audio)
        {
            return;
        }

        audio.Poll();
        audio.SetEnvironmentAudio(
            Renderer.Environment?.Effect.AudioGain ?? 1f,
            Renderer.Environment?.Effect.AudioCutoff ?? 0f);
        // A new scene or animation delta also moves/spawns sound owners and updates floor gain across stories.
        if (Renderer.Pan != _lastAudioPan
            || Renderer.Zoom != _lastAudioZoom
            || Renderer.SceneRevisionNumber != _lastAudioSceneRevision
            || Renderer.AnimationSequenceNumber != _lastAudioAnimationSequence)
        {
            _lastAudioPan = Renderer.Pan;
            _lastAudioZoom = Renderer.Zoom;
            _lastAudioSceneRevision = Renderer.SceneRevisionNumber;
            _lastAudioAnimationSequence = Renderer.AnimationSequenceNumber;
            audio.UpdateSpatial();
        }
    }

    /// <summary>Shuts down on quit or a termination signal: both hosts exit before the client does.</summary>
    public void Stop()
    {
        CancelPendingDaemonContinuations(new OperationCanceledException());
        _audio?.Close();
        _audio = null;
        _lastAudioSceneRevision = -1;
        _lastAudioAnimationSequence = -1;
        var hosts = new List<SimulationBridge>(2);
        if (Bridge is not null)
        {
            hosts.Add(Bridge);
        }

        if (_frontendAudio is not null)
        {
            hosts.Add(_frontendAudio);
        }

        Bridge?.Stop();
        StopFrontendAudio();
        foreach (var host in hosts)
        {
            if (!host.WaitForExit(10))
            {
                Log("the simulation host did not exit in time");
            }
        }
    }

    private void PlayUISoundOnResponse(string? id, string success, string? failure = "ui_error")
    {
        if (id is null)
        {
            return;
        }

        _pendingResponseSounds[id] = (success, failure);
    }

    private string? SendWithBuildSound(string operation, IReadOnlyDictionary<string, object?> parameters, string successSound = "ui_bld_dragtool_place")
    {
        string? id = Send(operation, parameters);
        PlayUISoundOnResponse(id, successSound);
        return id;
    }

    private bool IsInLiveMode => !Ui.BuyMode && !Ui.BuildMode && !Ui.OptionsMode && !Ui.Camera.Visible;

    public void SendSpeed(int value)
    {
        if (value > 0)
        {
            _lastRunningSpeed = value;
            _liveModePaused = false;
            if (!IsInLiveMode)
            {
                if (!Ui.BuyMode && !Ui.BuildMode && SpeedTransitionSound(0, value) is { } pendingSound)
                {
                    PlayUISound(pendingSound);
                }

                return;
            }
        }
        else
        {
            _liveModePaused = true;
            if (!IsInLiveMode)
            {
                return;
            }
        }

        if (Send("speed", new Dictionary<string, object?> { ["value"] = value }) is null)
        {
            return;
        }

        if (SpeedTransitionSound(Renderer.Speed, value) is { } sound)
        {
            PlayUISound(sound);
        }
    }

    private void PauseForNonLiveMode()
    {
        if (Renderer.Speed > 0)
        {
            _lastRunningSpeed = Renderer.Speed;
        }

        if (Send("speed", new Dictionary<string, object?> { ["value"] = 0 }) is null)
        {
            return;
        }

        if (SpeedTransitionSound(Renderer.Speed, 0) is { } sound)
        {
            PlayUISound(sound);
        }
    }

    private void ResumeLiveMode()
    {
        if (!IsInLiveMode)
        {
            return;
        }

        PlayModeMusic("live");
        int target = _liveModePaused ? 0 : _lastRunningSpeed;
        if (target <= 0)
        {
            return;
        }

        SendSpeed(target);
    }

    /// <summary>Original HIT events for speed-control transitions (<c>ui_speed_1to2</c>, <c>ui_speed_pto3</c>, ...); P is pause.</summary>
    public static string? SpeedTransitionSound(int current, int requested)
    {
        static string? Code(int speed) => speed switch
        {
            0 => "p",
            1 => "1",
            3 => "2",
            10 => "3",
            _ => null,
        };

        if (current == requested || Code(current) is not { } from || Code(requested) is not { } to)
        {
            return null;
        }

        return $"ui_speed_{from}to{to}";
    }

    private double Scale => Renderer.UIScale;
    private CGSize LogicalCanvas => new(_canvas.Width / Scale, _canvas.Height / Scale);
    private CGPoint Local(CGPoint point) => new(point.X / Scale, point.Y / Scale);
    private double OverlayScale => Ui.ShowsLot ? Scale : LiveUI.FullscreenOverlayScale(_canvas, Renderer.UIScale);
    private CGSize OverlayLogicalCanvas => new(_canvas.Width / OverlayScale, _canvas.Height / OverlayScale);
    private CGPoint OverlayLocal(CGPoint point) => new(point.X / OverlayScale, point.Y / OverlayScale);

    private CGRect OverlayScriptScreenRect(CGRect rect) =>
        new(rect.MinX * OverlayScale, rect.MinY * OverlayScale, rect.Width * OverlayScale, rect.Height * OverlayScale);

    private static Vector2 Simd(CGPoint point) => new((float)point.X, (float)point.Y);
    private Vector2 SimdCanvas => new((float)_canvas.Width, (float)_canvas.Height);

    // Modes

    public void DismissTransientMenu()
    {
        _wallStroke = null;
        _wallBrush = null;
        _floorStroke = null;
        _floorBrush = null;
        _floorBrushDeleting = false;
        _floorBrushTexture = null;
        _wallpaperStroke = null;
        try { Renderer.UpdateWallBrush(null); } catch { }
        try { Renderer.UpdateFloorBrush(null); } catch { }
        _terrainStrokeMode = null;
        _terrainStrokeStartPoint = null;
        _terrainStrokeID = null;
        _terrainStrokeBaseHeight = null;
        _lastStreamedTerrainDelta = null;
        _terrainStrokePlayedSound = false;
        _terrainGesture = new TerrainBrushGesture();
        _pendingMenu = null;
        _pendingPieMenuWarp = null;
        _pieGround = null;
        Ui.PieMenu = null;
        _desiredPieHead = null;
        _checkedPieHead = null;
    }

    public void DismissMenu()
    {
        DismissTransientMenu();
        Ui.Buy.CancelSelection();
        Ui.Build.CancelSelection();
        _desiredPlacement = null;
        _checkedPlacement = null;
        try { Renderer.UpdatePlacement(null); } catch { }
        ClearBuildCursor();
    }

    public void BrowseBuyCatalog()
    {
        if (HasBlockingDialog)
        {
            return;
        }

        if (AwayBuyBuildLocked)
        {
            PlayUISound("ui_error");
            return;
        }

        DismissMenu();
        CloseCameraOverlay();
        CloseOptionsOverlay();
        Ui.BuildMode = false;
        _pendingBuildCatalog = null;
        Ui.BuyMode = !Ui.BuyMode;
        EditingViewChanged();
        _pendingCatalog = null;
        if (Ui.BuyMode)
        {
            Ui.PanelMinimized = false;
            PlayModeMusic("buy");
            PauseForNonLiveMode();
            Ui.Buy.ClearCategory(0);
            RequestCatalog(category: 0, offset: 0);
        }
        else
        {
            Ui.PanelMinimized = false;
            ResumeLiveMode();
        }
    }

    public void BrowseBuildCatalog()
    {
        if (HasBlockingDialog)
        {
            return;
        }

        if (AwayBuyBuildLocked)
        {
            PlayUISound("ui_error");
            return;
        }

        DismissMenu();
        CloseCameraOverlay();
        CloseOptionsOverlay();
        Ui.BuyMode = false;
        _pendingCatalog = null;
        Ui.BuildMode = !Ui.BuildMode;
        EditingViewChanged();
        _pendingBuildCatalog = null;
        if (Ui.BuildMode)
        {
            Ui.PanelMinimized = false;
            PlayModeMusic("build");
            PauseForNonLiveMode();
            Ui.Build.ChangeKind(BuildCatalogPage.CatalogKind.Floor);
            RequestBuildCatalog(offset: 0);
        }
        else
        {
            Ui.PanelMinimized = false;
            ResumeLiveMode();
        }
    }

    private void EditingViewChanged() =>
        _ = Send("view", new Dictionary<string, object?> { ["editing"] = Ui.BuyMode || Ui.BuildMode });

    private static string BuildCatalogKindRawValue(BuildCatalogPage.CatalogKind kind) => kind switch
    {
        BuildCatalogPage.CatalogKind.Floor => "floor",
        BuildCatalogPage.CatalogKind.Wall => "wall",
        BuildCatalogPage.CatalogKind.WallStyle => "wallStyle",
        BuildCatalogPage.CatalogKind.Water => "water",
        BuildCatalogPage.CatalogKind.Roof => "roof",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private void RequestBuildCatalog(int offset)
    {
        int count = Ui.Build.ObjectTool is null && Ui.Build.Kind == BuildCatalogPage.CatalogKind.Roof
            ? OriginalBuildOverlay.RoofCapacity(LogicalCanvas)
            : OriginalBuildLayout.Capacity(LogicalCanvas);
        if (Ui.Build.ObjectTool is { } objectTool && objectTool.Category() is int category)
        {
            _pendingBuildCatalog = Send("catalogPage", new Dictionary<string, object?>
            {
                ["category"] = category,
                ["offset"] = offset,
                ["count"] = count,
            });
        }
        else
        {
            _pendingBuildCatalog = Send("buildCatalogPage", new Dictionary<string, object?>
            {
                ["kind"] = BuildCatalogKindRawValue(Ui.Build.Kind),
                ["offset"] = offset,
                ["count"] = count,
            });
        }
    }

    private void RequestCatalog(int category, int? room = null, int? subsort = null, int offset = 0)
    {
        var request = new Dictionary<string, object?>
        {
            ["category"] = category,
            ["offset"] = offset,
            ["count"] = OriginalBuyLayout.Capacity(LogicalCanvas),
        };
        if (room is int roomVal)
        {
            request["room"] = roomVal;
        }

        if (subsort is int subsortVal)
        {
            request["subsort"] = subsortVal;
        }

        _pendingCatalog = Send("catalogPage", request);
    }

    public void SelectStory(int level, bool? doorWindowTool = null)
    {
        if (HasBlockingDialog)
        {
            return;
        }

        if (level < 1 || level > Renderer.StoryCount || (level == EffectiveViewLevel && !EffectiveRoofsVisible && doorWindowTool is null))
        {
            PlayUISound("ui_error");
            return;
        }

        DismissMenu();
        Ui.Buy.Selection.Cancel();
        try { Renderer.UpdateFloorBrush(null); } catch { }
        _pendingViewLevel = level;
        _pendingRoofsVisible = false;
        _pendingViewSceneRevision = null;
        var payload = new Dictionary<string, object?>
        {
            ["value"] = level,
            ["roofs"] = false,
        };
        if (doorWindowTool is bool isDoorWindow)
        {
            payload["doorWindowTool"] = isDoorWindow;
        }

        _pendingViewRequestID = Send("level", payload);
    }

    public void NextSim()
    {
        DismissMenu();
        if (_simIDs.Count == 0)
        {
            return;
        }

        int current = _simIDs.IndexOf(_selectedSim);
        int next = _simIDs[(current + 1) % _simIDs.Count];
        _selectedSim = next;
        RequestAutoCenterOnSelection();
        _ = Send("selectSim", new Dictionary<string, object?> { ["objectID"] = next });
    }

    private void RequestAutoCenterOnSelection()
    {
        if (!Ui.OptionsPanel.PlayOptions.AutoCenter)
        {
            PlayUISound("ui_error");
            return;
        }

        _centresOnSelectedSim = true;
    }

    /// <summary>
    /// <c>cTSWinBtn</c>'s mouse handlers keep the button that sent its command (<c>lbl_10650BDC</c>, <c>Sims.exe</c> <c>DAT_006fe954</c>); a person
    /// button is known by its Sim, whose portrait is the button's image.
    /// </summary>
    private void RecordButton(int? image = null, int? person = null)
    {
        if (!UsesNativeEngine || !HasActiveLot)
        {
            return;
        }

        var payload = new Dictionary<string, object?>();
        if (image is int imageID)
        {
            payload["image"] = imageID;
        }

        if (person is int personID)
        {
            payload["objectID"] = personID;
        }

        _ = Send("uiButton", payload);
    }

    public void ToggleWalls()
    {
        DismissTransientMenu();
        _lastCutawayTile = null;
        _desiredCutaway = null;
        string mode = EffectiveWallMode == "up" ? "down" : "up";
        _pendingWallMode = mode;
        _pendingRoofsVisible = false;
        _pendingViewSceneRevision = null;
        _pendingViewRequestID = Send("walls", new Dictionary<string, object?> { ["mode"] = mode });
    }

    public void RotateCamera(int delta)
    {
        int next = (((int)Renderer.CameraOrientation + delta) % 4 + 4) % 4;
        _ = Send("cameraOrientation", new Dictionary<string, object?> { ["value"] = next });
    }

    /// <summary>A universal control button, clicked or reached by its shortcut key.</summary>
    public void UniversalControl(OriginalUniversalControl.Action action)
    {
        if (action == OriginalUniversalControl.Action.Help)
        {
            SetHelpVisible(true);
            return;
        }

        switch (action)
        {
            case OriginalUniversalControl.Action.Live:
                _tutorialLiveModeClicked = true;
                bool wasNonLive = !IsInLiveMode;
                CloseCameraOverlay();
                CloseOptionsOverlay();
                if (wasNonLive)
                {
                    Ui.PanelMinimized = false;
                }

                if (Ui.BuyMode)
                {
                    BrowseBuyCatalog();
                }
                else if (Ui.BuildMode)
                {
                    BrowseBuildCatalog();
                }
                else if (wasNonLive)
                {
                    ResumeLiveMode();
                }
                else
                {
                    Ui.PanelMinimized = !Ui.PanelMinimized;
                    Ui.Gameplay.ClearHoverPopups();
                }
                break;

            case OriginalUniversalControl.Action.Buy:
                if (AwayBuyBuildLocked)
                {
                    PlayUISound("ui_error");
                    return;
                }

                Ui.PanelMinimized = false;
                CloseCameraOverlay();
                CloseOptionsOverlay();
                if (!Ui.BuyMode)
                {
                    BrowseBuyCatalog();
                }
                else
                {
                    Ui.Buy.ToggleMode();
                    _pendingCatalog = null;
                    if (Ui.Buy.RoomMode)
                    {
                        RequestCatalog(category: -1, room: Ui.Buy.Room, offset: 0);
                    }
                    else
                    {
                        RequestCatalog(category: Ui.Buy.Category, offset: 0);
                    }
                }
                break;

            case OriginalUniversalControl.Action.Build:
                if (AwayBuyBuildLocked)
                {
                    PlayUISound("ui_error");
                    return;
                }

                Ui.PanelMinimized = false;
                CloseCameraOverlay();
                CloseOptionsOverlay();
                if (!Ui.BuildMode)
                {
                    BrowseBuildCatalog();
                }
                break;

            case OriginalUniversalControl.Action.ZoomIn:
            case OriginalUniversalControl.Action.ZoomOut:
                DismissTransientMenu();
                Renderer.SetZoom(Renderer.Zoom.Stepped(action == OriginalUniversalControl.Action.ZoomIn ? 1 : -1));
                break;

            case OriginalUniversalControl.Action.WallsUp:
            case OriginalUniversalControl.Action.WallsDown:
            case OriginalUniversalControl.Action.WallsCutaway:
                DismissTransientMenu();
                _lastCutawayTile = null;
                _desiredCutaway = null;
                string mode = action == OriginalUniversalControl.Action.WallsUp
                    ? "up"
                    : action == OriginalUniversalControl.Action.WallsCutaway
                        ? "cutaway"
                        : "down";
                _pendingWallMode = mode;
                _pendingRoofsVisible = false;
                _pendingViewSceneRevision = null;
                _pendingViewRequestID = Send("walls", new Dictionary<string, object?> { ["mode"] = mode });
                break;

            case OriginalUniversalControl.Action.Roofs:
                DismissTransientMenu();
                bool visible = !EffectiveRoofsVisible;
                _pendingRoofsVisible = visible;
                var payload = new Dictionary<string, object?> { ["visible"] = visible };
                if (visible)
                {
                    _pendingWallMode = "up";
                    if (Renderer.StoryCount > 1)
                    {
                        _pendingViewLevel = Renderer.StoryCount;
                        payload["level"] = Renderer.StoryCount;
                    }
                }

                _pendingViewSceneRevision = null;
                _pendingViewRequestID = Send("roofs", payload);
                break;

            case OriginalUniversalControl.Action.Story1:
                SelectStory(1);
                break;

            case OriginalUniversalControl.Action.Story2:
                SelectStory(2);
                break;

            case OriginalUniversalControl.Action.CameraLeft:
                RotateCamera(-1);
                break;

            case OriginalUniversalControl.Action.CameraRight:
                RotateCamera(1);
                break;

            case OriginalUniversalControl.Action.Options:
                if (Ui.OptionsMode)
                {
                    LeaveOptionsMode();
                }
                else
                {
                    EnterOptionsMode();
                }
                break;

            case OriginalUniversalControl.Action.Help:
                break;
        }
    }

    private void SetHelpVisible(bool visible)
    {
        if (Ui.HelpVisible == visible)
        {
            return;
        }

        Ui.HelpVisible = visible;
        SyncSimulatorBlock();
    }

    public void SetWindowActive(bool active)
    {
        if (_windowActive == active)
        {
            return;
        }

        _windowActive = active;
        SyncSimulatorBlock();
    }

    private void SyncSimulatorBlock()
    {
        bool blocked = Ui.HelpVisible || (!_windowActive && !Ui.OptionsPanel.PlayOptions.SimInBackground);
        _ = Send("blockSimulator", new Dictionary<string, object?> { ["blocked"] = blocked });
    }

    /// <summary>Home and End step the wall view through walls down, cutaway, walls up and roofs.</summary>
    private void StepWallView(int delta)
    {
        OriginalUniversalControl.Action[] views =
        [
            OriginalUniversalControl.Action.WallsDown,
            OriginalUniversalControl.Action.WallsCutaway,
            OriginalUniversalControl.Action.WallsUp,
            OriginalUniversalControl.Action.Roofs,
        ];
        int current = EffectiveRoofsVisible ? 3 : EffectiveWallMode == "down" ? 0 : EffectiveWallMode == "cutaway" ? 1 : 2;
        int next = Math.Clamp(current + delta, 0, views.Length - 1);
        if (next == current)
        {
            PlayUISound("ui_error");
            return;
        }

        UniversalControl(views[next]);
    }

    private void CloseCameraOverlay()
    {
        if (!Ui.Camera.Visible)
        {
            return;
        }

        Ui.Album.Close();
        Ui.Camera.Visible = false;
        PendingPhoto = null;
        _pendingPhotoIsAutomatic = false;
    }

    private void EnterCameraMode()
    {
        if (HasBlockingDialog || Ui.Album.Visible || !Ui.ShowsLot)
        {
            return;
        }

        if (Ui.Camera.Visible)
        {
            LeaveCameraMode();
            return;
        }

        Ui.PanelMinimized = false;
        DismissMenu();
        CloseOptionsOverlay();
        if (Ui.BuyMode || Ui.BuildMode)
        {
            Ui.BuyMode = false;
            _pendingCatalog = null;
            Ui.BuildMode = false;
            _pendingBuildCatalog = null;
            EditingViewChanged();
        }

        PauseForNonLiveMode();
        Ui.Camera.Visible = true;
        PlayModeMusic("camera");
    }

    private void LeaveCameraMode()
    {
        if (!Ui.Camera.Visible)
        {
            return;
        }

        CloseCameraOverlay();
        ResumeLiveMode();
    }

    /// <summary>
    /// Whether this frame must also be rendered without the interface and read back: a photo is pending, or the
    /// picture-in-picture window needs an image (Live PIP on, or the window not built yet).
    /// </summary>
    public bool PrepareFrameCapture()
    {
        UpdatePictureInPictureCaptureState();
        return PendingPhoto is not null || _pipNeedsCapture;
    }

    private static (byte[] Pixels, int Width, int Height) Crop(byte[] pixels, int width, int height, CGRect rect)
    {
        int x = Math.Max(0, (int)rect.MinX);
        int y = Math.Max(0, (int)rect.MinY);
        int w = Math.Min((int)rect.Width, width - x);
        int h = Math.Min((int)rect.Height, height - y);
        if (w <= 0 || h <= 0 || pixels.Length != width * height * 4)
        {
            throw new ContentError("Invalid camera frame");
        }

        byte[] cropped = new byte[w * h * 4];
        int rowBytes = w * 4;
        for (int row = 0; row < h; row++)
        {
            Buffer.BlockCopy(pixels, ((y + row) * width + x) * 4, cropped, row * rowBytes, rowBytes);
        }

        return (cropped, w, h);
    }

    private static byte[] ScaleRGBA(byte[] pixels, int srcW, int srcH, int dstW, int dstH)
    {
        if (dstW <= 0 || dstH <= 0 || srcW <= 0 || srcH <= 0 || pixels.Length != srcW * srcH * 4)
        {
            return pixels;
        }

        if (srcW == dstW && srcH == dstH)
        {
            return pixels;
        }

        byte[] scaled = new byte[dstW * dstH * 4];
        for (int y = 0; y < dstH; y++)
        {
            int sy = Math.Min(srcH - 1, y * srcH / dstH);
            for (int x = 0; x < dstW; x++)
            {
                int sx = Math.Min(srcW - 1, x * srcW / dstW);
                int sIdx = (sy * srcW + sx) * 4;
                int dIdx = (y * dstW + x) * 4;
                scaled[dIdx] = pixels[sIdx];
                scaled[dIdx + 1] = pixels[sIdx + 1];
                scaled[dIdx + 2] = pixels[sIdx + 2];
                scaled[dIdx + 3] = pixels[sIdx + 3];
            }
        }

        return scaled;
    }

    public void CapturePhoto(byte[] pixels, int width, int height)
    {
        if (_pipNeedsCapture)
        {
            _pipNeedsCapture = false;
            CapturePictureInPictureFrame();
        }

        if (PendingPhoto is not { } rect)
        {
            return;
        }

        bool automatic = _pendingPhotoIsAutomatic;
        string? caption = _pendingPhotoCaption;
        PendingPhoto = null;
        _pendingPhotoIsAutomatic = false;
        _pendingPhotoCaption = null;
        try
        {
            var frame = Crop(pixels, width, height, rect);
            int targetW = Math.Max(1, (int)Math.Round(rect.Width / Scale, MidpointRounding.AwayFromZero));
            int targetH = Math.Max(1, (int)Math.Round(rect.Height / Scale, MidpointRounding.AwayFromZero));
            byte[] finalPixels = ScaleRGBA(frame.Pixels, frame.Width, frame.Height, targetW, targetH);
            var album = new PhotoAlbum(Path.Combine(NeighborhoodUserData, "PhotoAlbum"));
            float[] qualities = [0.2f, 0.5f, 0.9f];
            float quality = qualities[Math.Clamp(Ui.Camera.Quality, 0, qualities.Length - 1)];
            _ = album.Capture(finalPixels, targetW, targetH, quality, caption ?? Strings.AlbumDescription());
            if (!automatic)
            {
                PlayUISound("ui_camera_photo");
            }
        }
        catch (Exception error)
        {
            if (!automatic)
            {
                PresentLocalFeedback("Photo Album", "Could not save this photo.");
            }

            Log($"Camera: {error}");
        }
    }

    private void AlbumAction(OriginalPhotoAlbumOverlay.Action action)
    {
        if (action == OriginalPhotoAlbumOverlay.Action.Delete)
        {
            PresentLocalConfirmation(
                Strings.Album(20, "Delete Photo?"),
                Strings.Album(21, "Are you sure you want to delete this photo?"),
                Strings.YesButton(),
                Strings.NoButton(),
                response: code =>
                {
                    if (code != 0)
                    {
                        return;
                    }

                    try
                    {
                        Ui.Album.Perform(OriginalPhotoAlbumOverlay.Action.Delete);
                        PlayUISound("ui_click");
                    }
                    catch
                    {
                        PresentLocalFeedback("Photo Album", "Could not delete this photo.");
                    }
                });
        }
        else
        {
            try
            {
                Ui.Album.Perform(action);
                PlayUISound("ui_click");
            }
            catch
            {
                PresentLocalFeedback("Photo Album", "Could not open this photo.");
            }
        }
    }

    private void CloseOptionsOverlay()
    {
        if (!Ui.OptionsMode)
        {
            return;
        }

        Ui.OptionsMode = false;
        Ui.OptionsPanel.Select(OriginalOptionsPanelOverlay.Page.None);
        if (Ui.SoundOptions.IsEmbedded)
        {
            Ui.SoundOptions.Dismiss();
        }
    }

    public void EnterOptionsMode()
    {
        if (HasBlockingDialog)
        {
            return;
        }

        Ui.PanelMinimized = false;
        DismissMenu();
        CloseCameraOverlay();
        if (Ui.BuyMode || Ui.BuildMode)
        {
            Ui.BuyMode = false;
            _pendingCatalog = null;
            Ui.BuildMode = false;
            _pendingBuildCatalog = null;
            EditingViewChanged();
        }

        Ui.OptionsPanel.Select(OriginalOptionsPanelOverlay.Page.None);
        Ui.OptionsMode = true;
        PlayUISound("ui_click");
        PauseForNonLiveMode();
        PlayModeMusic("options");
    }

    public void LeaveOptionsMode()
    {
        if (!Ui.OptionsMode)
        {
            return;
        }

        CloseOptionsOverlay();
        ResumeLiveMode();
    }

    private void SendAudioVolume(OriginalSoundOptionsOverlay.VolumeChange change)
    {
        string? id = Send("audioVolume", new Dictionary<string, object?>
        {
            ["group"] = change.Group,
            ["volume"] = change.Volume,
        });
        if (id is null)
        {
            Ui.SoundOptions.Restore(change);
            return;
        }

        _pendingAudioVolumes[id] = change;
        _latestAudioVolumeRequest[change.Group] = id;
    }

    private void ChooseOption(OriginalOptionsPanelOverlay.Button button)
    {
        PlayUISound("ui_click");
        switch (button)
        {
            case OriginalOptionsPanelOverlay.Button.Save:
                SaveGame();
                break;

            case OriginalOptionsPanelOverlay.Button.Sound:
                if (Ui.OptionsPanel.CurrentPage == OriginalOptionsPanelOverlay.Page.Sound)
                {
                    Ui.SoundOptions.HideEmbedded();
                    Ui.OptionsPanel.Select(OriginalOptionsPanelOverlay.Page.None);
                }
                else
                {
                    bool hadActivePage = Ui.OptionsPanel.CurrentPage != OriginalOptionsPanelOverlay.Page.None;
                    Ui.SoundOptions.ShowEmbedded(OriginalOptionsPanelOverlay.SoundRows(Strings), afterFadeOut: hadActivePage);
                    Ui.OptionsPanel.Select(OriginalOptionsPanelOverlay.Page.Sound);
                    _pendingAudioState = Send("audioState");
                }
                break;

            case OriginalOptionsPanelOverlay.Button.Neighborhood:
                // cWinOptions::TSOnCommand / FUN_00517c78: a lot whose saving is enabled offers to save first (UIText STR# 153
                // entries 3 and 4). Yes saves, No leaves without saving, Cancel stays; a save-disabled lot just leaves.
                if (LotSaveDisabled)
                {
                    ReturnToNeighborhood(saving: false);
                    break;
                }

                PresentLocalConfirmation(
                    Strings.SaveMenuTitle(),
                    Strings.SaveConfirmationMessage(),
                    Strings.YesButton(),
                    Strings.NoButton(),
                    Strings.CancelButton(),
                    code =>
                    {
                        if (code > 1)
                        {
                            return;
                        }

                        ReturnToNeighborhood(saving: code == 0);
                    });
                break;

            case OriginalOptionsPanelOverlay.Button.Quit:
                ConfirmQuit();
                break;

            case OriginalOptionsPanelOverlay.Button.ResetTutorial:
                // FUN_004f2661: confirm, offer to save, copy base UserData's Tutorial.FAM into the Import folder (refusing to
                // overwrite one already there) and return to the neighborhood, which imports it onto the tutorial house.
                PresentLocalConfirmation(
                    Strings.ResetTutorialTitle(),
                    Strings.ResetTutorialMessage(),
                    Strings.YesButton(),
                    Strings.NoButton(),
                    response: code =>
                    {
                        if (code != 0)
                        {
                            return;
                        }

                        _pendingResetTutorialState = Send("tutorialResetState");
                    });
                break;

            case OriginalOptionsPanelOverlay.Button.Play:
            case OriginalOptionsPanelOverlay.Button.Graphics:
                var target = button == OriginalOptionsPanelOverlay.Button.Play
                    ? OriginalOptionsPanelOverlay.Page.Play
                    : OriginalOptionsPanelOverlay.Page.Graphics;
                if (Ui.SoundOptions.IsEmbedded)
                {
                    Ui.SoundOptions.HideEmbedded();
                }

                Ui.OptionsPanel.Select(Ui.OptionsPanel.CurrentPage != target ? target : OriginalOptionsPanelOverlay.Page.None);
                break;
        }
    }

    /// <summary>The current neighborhood's folder in the profile: <c>UserData</c> for neighborhood 1, <c>UserDataN</c> otherwise.</summary>
    private string NeighborhoodUserData =>
        Path.Combine(
            Options.DataRoot ?? Workspace,
            "runtime",
            "profiles",
            Options.Profile,
            $"UserData{(_currentNeighborhood == 1 ? string.Empty : _currentNeighborhood.ToString(CultureInfo.InvariantCulture))}");

    private void CopyTutorialToImport()
    {
        string imports = Path.Combine(NeighborhoodUserData, "Import");
        Directory.CreateDirectory(imports);
        // CopyFileA with bFailIfExists: an earlier reset that has not been imported yet makes this fail.
        string @base = Path.Combine(Options.DataRoot ?? Workspace, "runtime", "profiles", Options.Profile, "UserData", "Tutorial.FAM");
        string source = File.Exists(@base) ? @base : Path.Combine(Content.Root, "UserData", "Tutorial.FAM");
        TutorialReset.Copy(source, imports);
    }

    private void FinishResetTutorial()
    {
        try
        {
            CopyTutorialToImport();
            ReturnToNeighborhood(saving: false, mode: 1);
        }
        catch (Exception error)
        {
            Log($"Reset Tutorial: {error}");
            PresentLocalFeedback(Strings.ResetTutorialTitle(), Strings.ResetTutorialFailedMessage());
        }
    }

    private void OfferResetTutorialSave()
    {
        PresentLocalConfirmation(
            Strings.SaveMenuTitle(),
            Strings.SaveConfirmationMessage(),
            Strings.YesButton(),
            Strings.NoButton(),
            Strings.CancelButton(),
            save =>
            {
                if (save > 1)
                {
                    return;
                }

                if (save == 0)
                {
                    // Mac DoSave asks even when saving is disabled, but skips SaveGame and returns success.
                    if (LotSaveDisabled)
                    {
                        FinishResetTutorial();
                        return;
                    }

                    SendSaveRequest(succeeded =>
                    {
                        if (!succeeded)
                        {
                            return;
                        }

                        FinishResetTutorial();
                    });
                }
                else
                {
                    FinishResetTutorial();
                }
            });
    }

    private string PlayOptionsURL =>
        Path.Combine(
            Options.DataRoot ?? Path.Combine(Options.Workspace ?? InstallLocator.DefaultWorkspace(), "runtime", "data"),
            "Settings",
            "options.json");

    public void LoadPlayOptions()
    {
        try
        {
            if (!File.Exists(PlayOptionsURL))
            {
                return;
            }

            byte[] data = File.ReadAllBytes(PlayOptionsURL);
            if (JsonSerializer.Deserialize<OriginalOptionsPanelOverlay.PlayOptionsState?>(data) is not { } loadedOptions)
            {
                return;
            }

            Ui.OptionsPanel.PlayOptions = loadedOptions;
            Ui.SoundOptions.InterfaceEffectsEnabled = loadedOptions.InterfaceEffects;
            _centresOnSelectedSim = loadedOptions.AutoCenter;
            Renderer.LightingEnabled = loadedOptions.Lighting && Options.Lighting;
            Renderer.AntiAliasedSprites = loadedOptions.AntiAlias;
            Renderer.ShadowsEnabled = loadedOptions.Shadows;
            Renderer.TerrainDetail = loadedOptions.Detail(OriginalOptionsPanelOverlay.DetailSetting.Terrain);
            Renderer.CharacterDetail = loadedOptions.Detail(OriginalOptionsPanelOverlay.DetailSetting.Character);
        }
        catch
        {
            // Keep default options when no valid options file is available yet.
        }
    }

    private void TogglePlayOption(OriginalOptionsPanelOverlay.PlayOption option)
    {
        var updated = option.Toggled(Ui.OptionsPanel.PlayOptions);
        if (option == OriginalOptionsPanelOverlay.PlayOption.AutoCenter && !updated.AutoCenter)
        {
            _centresOnSelectedSim = false;
        }

        SavePlayOptions(updated);
    }

    private void SetDetail(OriginalOptionsPanelOverlay.DetailSetting setting, int level)
    {
        var updated = Ui.OptionsPanel.PlayOptions;
        switch (setting)
        {
            case OriginalOptionsPanelOverlay.DetailSetting.Terrain:
                updated.TerrainDetail = level;
                break;

            case OriginalOptionsPanelOverlay.DetailSetting.Character:
                updated.CharacterDetail = level;
                break;
        }

        SavePlayOptions(updated);
    }

    private void SavePlayOptions(OriginalOptionsPanelOverlay.PlayOptionsState updated)
    {
        Ui.OptionsPanel.PlayOptions = updated;
        Ui.SoundOptions.InterfaceEffectsEnabled = updated.InterfaceEffects;
        try
        {
            string directory = Path.GetDirectoryName(PlayOptionsURL)!;
            Directory.CreateDirectory(directory);
            byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(updated);
            string temp = Path.Combine(directory, $".options.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temp, encoded);
                File.Move(temp, PlayOptionsURL, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    try { File.Delete(temp); } catch { }
                }
            }
        }
        catch (Exception error)
        {
            Log($"Play options: {error}");
        }

        PlayUISound("ui_click");
        ApplyPlayOptions();
        // Keep the command-line renderer override effective after an in-game options change, as it is on load.
        Renderer.LightingEnabled = updated.Lighting && Options.Lighting;
        Renderer.AntiAliasedSprites = updated.AntiAlias;
        Renderer.ShadowsEnabled = updated.Shadows;
        Renderer.TerrainDetail = updated.Detail(OriginalOptionsPanelOverlay.DetailSetting.Terrain);
        Renderer.CharacterDetail = updated.Detail(OriginalOptionsPanelOverlay.DetailSetting.Character);
        SyncSimulatorBlock();
    }

    public void ApplyPlayOptions()
    {
        _ = Send("freeWill", new Dictionary<string, object?> { ["enabled"] = Ui.OptionsPanel.PlayOptions.FreeWill });
        _ = Send("pictureInPicture", new Dictionary<string, object?> { ["live"] = Ui.OptionsPanel.PlayOptions.LivePIP });
    }

    public void SaveGame()
    {
        if (Ui.GameDialog is not null || LotSaveDisabled)
        {
            return;
        }

        DismissMenu();
        PresentLocalConfirmation(
            Strings.SaveHouseTitle(),
            Strings.SaveConfirmationMessage(),
            Strings.YesButton(),
            Strings.NoButton(),
            response: code =>
            {
                if (code != 0)
                {
                    return;
                }

                SendSaveRequest();
            });
    }

    private void SendSaveRequest(Action<bool>? completion = null)
    {
        string? id = Send("save", new Dictionary<string, object?> { ["slot"] = "manual", ["lot"] = true });
        if (id is null)
        {
            PresentLocalFeedback(Strings.SaveHouseTitle(), Strings.SaveFailedMessage());
            completion?.Invoke(false);
            return;
        }

        _pendingSaveRequests.Add(id);
        if (completion is not null)
        {
            _pendingSaveCompletions[id] = completion;
        }
    }

    public void LoadGame()
    {
        if (Ui.GameDialog is not null)
        {
            return;
        }

        DismissMenu();
        PresentLocalConfirmation(
            Strings.LoadHouseTitle(),
            Strings.LoadConfirmationMessage(),
            Strings.YesButton(),
            Strings.NoButton(),
            response: code =>
            {
                if (code != 0)
                {
                    return;
                }

                SendLoad(removingPacks: false);
            });
    }

    private void SendLoad(bool removingPacks)
    {
        var parameters = new Dictionary<string, object?> { ["slot"] = "manual" };
        if (removingPacks)
        {
            parameters["removePacks"] = true;
        }

        string? id = Send("load", parameters);
        if (id is null)
        {
            PresentLocalFeedback(Strings.LoadHouseTitle(), Strings.LoadFailedMessage());
            return;
        }

        _pendingLoadRequests.Add(id);
    }

    /// <summary>
    /// A save that needs uninstalled packs loads only once the player agrees to lose that content; the host backs the
    /// slot up first, so reinstalling the packs and restoring the backup brings it back.
    /// </summary>
    private void ConfirmPackRemoval(IReadOnlyList<string> packs)
    {
        string names = string.Join(
            ", ",
            packs.Select(p => p.Split('@', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? p));
        PresentLocalConfirmation(
            Strings.LoadHouseTitle(),
            $"This house uses custom content that is not installed: {names}. Load it anyway? "
            + "Objects from that content will be removed and refunded. A backup of the save is kept.",
            Strings.YesButton(),
            Strings.NoButton(),
            response: code =>
            {
                if (code != 0)
                {
                    return;
                }

                SendLoad(removingPacks: true);
            });
    }

    // Local dialogs

    private void PresentLocalFeedback(string title, string message)
    {
        if (Ui.GameDialog is not null)
        {
            return;
        }

        string token = "native-storage-" + Guid.NewGuid().ToString("D").ToUpperInvariant();
        try
        {
            Ui.GameDialog = new OriginalDialogOverlay(
                GameDialog.LocalFeedback(token, title, message, Strings.OkButton()),
                Ui.Font,
                Ui.Device,
                Ui.Content);
            _localDialogToken = token;
            _localDialogResponse = null;
        }
        catch (Exception error)
        {
            Log($"Dialog: {error}");
        }
    }

    private void PresentLocalConfirmation(
        string title,
        string message,
        string yes,
        string no,
        string? cancel = null,
        Action<byte>? response = null)
    {
        if (Ui.GameDialog is not null)
        {
            return;
        }

        string token = "native-storage-" + Guid.NewGuid().ToString("D").ToUpperInvariant();
        try
        {
            var dialog = GameDialog.LocalPrompt(
                token,
                title,
                message,
                kind: (byte)(cancel is null ? 1 : 2),
                yes,
                no,
                cancel);
            Ui.GameDialog = new OriginalDialogOverlay(dialog, Ui.Font, Ui.Device, Ui.Content);
            _localDialogToken = token;
            _localDialogResponse = response;
        }
        catch (Exception error)
        {
            Log($"Dialog: {error}");
        }
    }
}
