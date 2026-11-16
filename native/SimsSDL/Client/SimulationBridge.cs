using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace SimsSDL.Client;

public sealed class SimulationBridge : IDisposable
{
    private const int MaxMessageBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Lock DiscardLock = new();
    private static readonly Lock SharedMountsLock = new();
    private static readonly Dictionary<string, string> SharedMounts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Task<string>> SharedMountPreparations = new(StringComparer.Ordinal);

    private readonly Lock _lifecycleLock = new();
    private Channel<string>? _inputChannel;
    private Task? _serverTask;
    private LineForwardingTextWriter? _writer;
    private long _sequence;
    private long _generation;
    private long _stopSequence;
    private volatile bool _stopped;
    private volatile bool _preparing;
    private string? _contentMountForExit;

    public string Workspace { get; }
    public string DataRoot { get; }
    public string Session { get; }
    public string ProfileId { get; }

    public int Neighborhood { get; }

    public bool DiscardsContentOnExit { get; set; }

    public bool IsRunning => _serverTask is { IsCompleted: false };

    public Action<Dictionary<string, JsonElement>>? OnEvent { get; set; }

    public Action<Dictionary<string, JsonElement>>? Event
    {
        get => OnEvent;
        set => OnEvent = value;
    }

    public Action<Dictionary<string, JsonElement>>? OnScene { get; set; }
    public Action<Dictionary<string, JsonElement>>? OnAudio { get; set; }

    public Action<SceneAnimation>? Animation { get; set; }

    public Action<string>? Failed { get; set; }

    public Action<string?>? OnExit { get; set; }

    public SimulationBridge(
        string workspace,
        string? dataRoot = null,
        string profileId = "player",
        int neighborhood = 1)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspace);
        global::ProfileBootstrap.ValidateProfileId(profileId);
        SimulationHost.ValidateNeighborhood(neighborhood);

        Workspace = workspace;
        DataRoot = dataRoot ?? workspace;
        ProfileId = profileId;
        Neighborhood = neighborhood;
        Session = Path.Combine(DataRoot, "runtime", "sessions", Guid.NewGuid().ToString("D"));
    }

    public void Start(GameContent content, int house)
    {
        ArgumentNullException.ThrowIfNull(content);
        SimulationHost.ValidateHouse(house, allowDaemonZero: false);
        Close();
        StartSimulationSync(content, house, nativeEngine: false);
    }

    public Task StartAsync(
        GameContent content,
        int house,
        bool nativeEngine = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        SimulationHost.ValidateHouse(house, allowDaemonZero: false);
        return OpenAsyncCore(content, house, nativeEngine, cancellationToken);
    }

    public Task Open(
        GameContent content,
        int house,
        bool nativeEngine = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        SimulationHost.ValidateHouse(house, allowDaemonZero: true);
        return OpenAsyncCore(content, house, nativeEngine, cancellationToken);
    }

    public void StartDaemon(GameContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Close();
        StartSimulationSync(content, house: 0, nativeEngine: false);
    }

    public Task StartDaemonAsync(
        GameContent content,
        bool nativeEngine = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return OpenAsyncCore(content, house: 0, nativeEngine, cancellationToken);
    }

    public void StartFrontendAudio(GameContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Close();
        StartFrontendAudioSync(content, nativeEngine: false);
    }

    public Task StartFrontendAudioAsync(
        GameContent content,
        bool nativeEngine = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return OpenFrontendAudioAsyncCore(content, nativeEngine, cancellationToken);
    }

    public Task OpenFrontendAudio(
        GameContent content,
        bool nativeEngine = false,
        CancellationToken cancellationToken = default) =>
        StartFrontendAudioAsync(content, nativeEngine, cancellationToken);

    private async Task OpenAsyncCore(
        GameContent content,
        int house,
        bool nativeEngine,
        CancellationToken cancellationToken)
    {
        await CloseAsync().ConfigureAwait(false);
        var expectedStopSequence = Volatile.Read(ref _stopSequence);
        var mountedContent = await PrepareContentAsync(
            content,
            materializeFiles: content.Packs.Ordered.Count > 0,
            nativeEngine,
            expectedStopSequence,
            cancellationToken).ConfigureAwait(false);

        LaunchSimulationServer(content, house, mountedContent, nativeEngine, privateMountToDiscard: null, expectedStopSequence);
    }

    private async Task OpenFrontendAudioAsyncCore(
        GameContent content,
        bool nativeEngine,
        CancellationToken cancellationToken)
    {
        await CloseAsync().ConfigureAwait(false);
        var expectedStopSequence = Volatile.Read(ref _stopSequence);
        _ = await PrepareContentAsync(
            content,
            materializeFiles: false,
            nativeEngine,
            expectedStopSequence,
            cancellationToken).ConfigureAwait(false);

        LaunchFrontendAudioServer(content, nativeEngine, expectedStopSequence);
    }

    private void StartSimulationSync(GameContent content, int house, bool nativeEngine)
    {
        var expectedStopSequence = Volatile.Read(ref _stopSequence);
        PrepareSessionDirectory();
        string mountedContent;
        string? privateMount = null;
        if (content.Packs.Ordered.Count == 0)
        {
            mountedContent = content.Root;
        }
        else
        {
            mountedContent = Path.Combine(Session, "content");
            content.Materialize(mountedContent);
            if (DiscardsContentOnExit)
            {
                privateMount = mountedContent;
            }
        }

        LaunchSimulationServer(content, house, mountedContent, nativeEngine, privateMount, expectedStopSequence);
    }

    private void StartFrontendAudioSync(GameContent content, bool nativeEngine)
    {
        var expectedStopSequence = Volatile.Read(ref _stopSequence);
        PrepareSessionDirectory();
        if (nativeEngine)
        {
            SimulationHost.ApplyNativeEngineEnvironmentIfSelected(nativeEngine);
        }

        LaunchFrontendAudioServer(content, nativeEngine, expectedStopSequence);
    }

    private async Task<string> PrepareContentAsync(
        GameContent content,
        bool materializeFiles,
        bool nativeEngine,
        long expectedStopSequence,
        CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            if (_preparing || _serverTask is { IsCompleted: false } || Volatile.Read(ref _stopSequence) != expectedStopSequence)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            _stopped = false;
            _preparing = true;
        }

        try
        {
            await Task.Run(PrepareSessionDirectory, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopped || Volatile.Read(ref _stopSequence) != expectedStopSequence)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var root = materializeFiles
                ? await SharedContentMount(content, DataRoot, nativeEngine).ConfigureAwait(false)
                : content.Root;

            cancellationToken.ThrowIfCancellationRequested();
            if (_stopped || Volatile.Read(ref _stopSequence) != expectedStopSequence)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return root;
        }
        finally
        {
            _preparing = false;
        }
    }

    private void PrepareSessionDirectory()
    {
        Directory.CreateDirectory(Session);
        SessionSweeper.Claim(Session);
    }

    private void LaunchSimulationServer(
        GameContent content,
        int house,
        string mountedContent,
        bool nativeEngine,
        string? privateMountToDiscard,
        long expectedStopSequence)
    {
        long generation;
        Channel<string> channel;
        LineForwardingTextWriter writer;
        SimsSimulation.SimulationServer server;

        lock (_lifecycleLock)
        {
            if (Volatile.Read(ref _stopSequence) != expectedStopSequence)
            {
                throw new OperationCanceledException();
            }

            _stopped = false;
            generation = ++_generation;
            _contentMountForExit = privateMountToDiscard;
            channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
            writer = CreateWriter(generation);

            lock (SimulationHost.SimulationStateLock)
            {
                _cachedAppearanceRegistry = null;
                SimsSimulation.InMemorySessionAssets.EnableRoot(Session);
                SimulationHost.ApplyNativeEngineEnvironmentIfSelected(nativeEngine);
                var (profilePath, _) = SimulationHost.PrepareNeighborhood(content.Root, DataRoot, ProfileId, Neighborhood);
                server = SimsSimulation.SimulationServer.Load(
                    mountedContent,
                    Session,
                    profilePath,
                    (short)house,
                    writer,
                    (byte)content.Language.Code);
                server.DirectAnimationSink = directFrame =>
                {
                    if (_stopped || generation != Volatile.Read(ref _generation) || Animation is not { } handler)
                    {
                        return;
                    }

                    handler(ConvertDirectAnimation(directFrame));
                };
            }

            _inputChannel = channel;
            _writer = writer;
            _serverTask = Task.Run(() => RunSimulationLoopAsync(server, channel.Reader, generation));
        }
    }

    private static SceneAnimation ConvertDirectAnimation(SimsSimulation.SceneExporter.DirectAnimationFrame frame)
    {
        SceneAnimation.Mesh[] meshes = frame.Meshes.Length == 0
            ? Array.Empty<SceneAnimation.Mesh>()
            : new SceneAnimation.Mesh[frame.Meshes.Length];
        for (int i = 0; i < frame.Meshes.Length; i++)
        {
            var m = frame.Meshes[i];
            meshes[i] = new SceneAnimation.Mesh
            {
                Texture = m.Texture,
                ObjectID = m.ObjectID,
                Room = m.Room,
                Level = m.Level,
                FloorShadow = LotScene.ToZoomImage(m.FloorShadow),
                FloorShadowNear = LotScene.ToZoomImage(m.FloorShadowNear),
                FloorShadowFar = LotScene.ToZoomImage(m.FloorShadowFar),
                Depth = m.Depth,
                Vertices = Array.Empty<byte>(),
                DirectVertices = LotScene.ToMeshVertices(m.Vertices, m.ShiftX, m.ShiftY),
                Tint = m.Tint,
            };
        }

        return new SceneAnimation
        {
            SchemaVersion = 1,
            Lighting = LotScene.ToLighting(frame.Lighting),
            BaseRevision = frame.BaseRevision,
            Sequence = frame.Sequence,
            Updates = frame.Updates,
            TextureStart = frame.TextureStart,
            Textures = LotScene.ToTextures(frame.Textures),
            Sprites = LotScene.ToSprites(frame.Sprites),
            Meshes = meshes,
            CensorRects = LotScene.ToCensorRects(frame.CensorRects),
            IsTrustedInProcess = true,
        };
    }

    private void LaunchFrontendAudioServer(GameContent content, bool nativeEngine, long expectedStopSequence)
    {
        long generation;
        Channel<string> channel;
        LineForwardingTextWriter writer;
        global::FrontendAudioServer audioServer;

        lock (_lifecycleLock)
        {
            if (Volatile.Read(ref _stopSequence) != expectedStopSequence)
            {
                throw new OperationCanceledException();
            }

            _stopped = false;
            generation = ++_generation;
            _contentMountForExit = null;
            channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
            writer = CreateWriter(generation);

            lock (SimulationHost.SimulationStateLock)
            {
                SimulationHost.ApplyNativeEngineEnvironmentIfSelected(nativeEngine);
                var (profilePath, _) = SimulationHost.PrepareNeighborhood(content.Root, DataRoot, ProfileId, Neighborhood);
                audioServer = new global::FrontendAudioServer(
                    writer,
                    Session,
                    profilePath,
                    content.Root,
                    (byte)content.Language.Code);
            }

            _inputChannel = channel;
            _writer = writer;
            _serverTask = Task.Run(() => RunFrontendAudioLoopAsync(audioServer, channel.Reader, generation));
        }
    }

    private LineForwardingTextWriter CreateWriter(long generation) =>
        new(
            Session,
            shouldDecodeAnimation: () => Animation is not null,
            onAnimation: frame =>
            {
                if (_stopped || generation != Volatile.Read(ref _generation))
                {
                    return;
                }

                Animation?.Invoke(frame);
            },
            onEvent: message => MainThreadDispatcher.Post(() => DeliverEvent(message, generation)),
            onFailure: error => MainThreadDispatcher.Post(() =>
            {
                if (_stopped || generation != Volatile.Read(ref _generation))
                {
                    return;
                }

                Failed?.Invoke(error);
                if (string.Equals(error, LineForwardingTextWriter.SizeLimitError, StringComparison.Ordinal))
                {
                    Stop();
                }
            }));

    private async Task RunSimulationLoopAsync(
        SimsSimulation.SimulationServer server,
        ChannelReader<string> reader,
        long generation)
    {
        string? exitError = null;
        try
        {
            await server.Run(reader).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            exitError = error.Message;
            PostFailureAndExit(error.Message, generation);
        }
        finally
        {
            CleanupAfterRun(generation, exitError);
        }
    }

    private async Task RunFrontendAudioLoopAsync(
        global::FrontendAudioServer server,
        ChannelReader<string> reader,
        long generation)
    {
        string? exitError = null;
        try
        {
            await server.Run(reader).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            exitError = error.Message;
            PostFailureAndExit(error.Message, generation);
        }
        finally
        {
            CleanupAfterRun(generation, exitError);
        }
    }

    private void PostFailureAndExit(string errorMessage, long generation) =>
        MainThreadDispatcher.Post(() =>
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            Failed?.Invoke(errorMessage);
        });

    private void CleanupAfterRun(long generation, string? exitError)
    {
        string? mountToDiscard;
        LineForwardingTextWriter? writerToDispose;
        lock (_lifecycleLock)
        {
            if (generation != _generation)
            {
                return;
            }

            mountToDiscard = _contentMountForExit;
            _contentMountForExit = null;
            writerToDispose = _writer;
            _writer = null;
        }

        writerToDispose?.Dispose();
        if (!string.IsNullOrEmpty(mountToDiscard))
        {
            Discard(mountToDiscard);
        }

        MainThreadDispatcher.Post(() =>
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            OnExit?.Invoke(exitError);
        });
    }

    public static async Task<string> SharedContentMount(
        GameContent content,
        string dataRoot,
        bool nativeEngine = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);

        var fingerprint = content.Packs.Report().Fingerprint;
        var identity = string.Join(
            '\n',
            NormalizePath(dataRoot),
            NormalizePath(content.Root),
            fingerprint,
            nativeEngine ? "native" : "legacy");
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));

        Task<string> preparation;
        string? createdCacheDirectory = null;
        lock (SharedMountsLock)
        {
            if (SharedMounts.TryGetValue(key, out var cached) && Directory.Exists(cached))
            {
                return cached;
            }

            if (SharedMountPreparations.TryGetValue(key, out var inFlight))
            {
                preparation = inFlight;
            }
            else
            {
                var sharedRoot = Path.Combine(dataRoot, "runtime", "content");
                var cacheDirectory = Path.Combine(sharedRoot, key);
                if (SessionSweeper.IsOwnedByAnotherProcess(cacheDirectory))
                {
                    cacheDirectory = Path.Combine(sharedRoot, $"{key}-{Guid.NewGuid():D}");
                }

                var selectedCacheDirectory = cacheDirectory;
                var mount = Path.Combine(selectedCacheDirectory, "content");
                preparation = Task.Run(() =>
                {
                    var targetCacheDirectory = selectedCacheDirectory;
                    var targetMount = mount;
                    if (Directory.Exists(targetCacheDirectory))
                    {
                        if (content.ReusableMount(targetMount, identity) && Directory.Exists(targetMount))
                        {
                            SessionSweeper.Claim(targetCacheDirectory);
                            _ = Task.Run(() => SessionSweeper.SweepContentCaches(dataRoot, preserving: targetCacheDirectory));
                            return targetMount;
                        }

                        SessionSweeper.DeleteEntrySafely(targetCacheDirectory);
                    }

                    if (File.Exists(targetMount) || Directory.Exists(targetMount) || PackContentPaths.IsSymbolicLink(targetMount))
                    {
                        targetCacheDirectory = Path.Combine(sharedRoot, $"{key}-{Guid.NewGuid():D}");
                        targetMount = Path.Combine(targetCacheDirectory, "content");
                    }

                    createdCacheDirectory = targetCacheDirectory;
                    Directory.CreateDirectory(targetCacheDirectory);
                    SessionSweeper.Claim(targetCacheDirectory);
                    content.Materialize(targetMount, cacheIdentity: identity, nativeOverlay: true);
                    _ = Task.Run(() => SessionSweeper.SweepContentCaches(dataRoot, preserving: targetCacheDirectory));
                    return targetMount;
                });

                SharedMountPreparations[key] = preparation;
            }
        }

        try
        {
            var ready = await preparation.ConfigureAwait(false);
            lock (SharedMountsLock)
            {
                SharedMountPreparations.Remove(key);
                SharedMounts[key] = ready;
            }

            return ready;
        }
        catch
        {
            lock (SharedMountsLock)
            {
                SharedMountPreparations.Remove(key);
            }

            if (!string.IsNullOrEmpty(createdCacheDirectory) &&
                !SessionSweeper.IsOwnedByAnotherProcess(createdCacheDirectory))
            {
                await Task.Run(() => Discard(createdCacheDirectory)).ConfigureAwait(false);
            }

            throw;
        }
    }

    public string Send(string operation, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);

        Channel<string>? channel;
        long seq;
        lock (_lifecycleLock)
        {
            channel = _inputChannel;
            if (_stopped || channel is null || _serverTask is null or { IsCompleted: true })
            {
                throw new ContentError("Simulation is not running");
            }

            seq = Interlocked.Increment(ref _sequence);
        }

        var message = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (parameters is not null)
        {
            foreach (var (key, value) in parameters)
            {
                message[key] = value;
            }
        }

        var id = seq.ToString(CultureInfo.InvariantCulture);
        message["op"] = operation;
        message["id"] = id;
        var json = JsonSerializer.Serialize(message, CamelCaseOptions);
        if (!channel.Writer.TryWrite(json))
        {
            throw new ContentError("Simulation is not running");
        }

        return id;
    }

    public string SendCommand(string operation, IReadOnlyDictionary<string, object?>? parameters = null) =>
        Send(operation, parameters);

    public bool TrySendRaw(string jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return false;
        }

        Channel<string>? channel;
        lock (_lifecycleLock)
        {
            channel = _inputChannel;
            if (_stopped || channel is null || _serverTask is null or { IsCompleted: true })
            {
                return false;
            }
        }

        return channel.Writer.TryWrite(jsonLine);
    }

    public void Stop(bool save = false)
    {
        Channel<string>? channel;
        bool wasRunning;
        lock (_lifecycleLock)
        {
            _stopped = true;
            Interlocked.Increment(ref _stopSequence);
            channel = _inputChannel;
            wasRunning = channel is not null && _serverTask is { IsCompleted: false };
        }

        if (wasRunning && channel is not null)
        {
            if (save)
            {
                var saveId = Interlocked.Increment(ref _sequence).ToString(CultureInfo.InvariantCulture);
                var saveJson = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["id"] = saveId,
                    ["op"] = "save",
                    ["slot"] = "autosave",
                }, CamelCaseOptions);
                channel.Writer.TryWrite(saveJson);
            }

            var quitId = Interlocked.Increment(ref _sequence).ToString(CultureInfo.InvariantCulture);
            var quitJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = quitId,
                ["op"] = "quit",
            }, CamelCaseOptions);
            channel.Writer.TryWrite(quitJson);
        }

        channel?.Writer.TryComplete();
    }

    public bool WaitForExit(double timeoutSeconds)
    {
        if (_preparing)
        {
            return false;
        }

        Task? task;
        lock (_lifecycleLock)
        {
            task = _serverTask;
        }

        if (task is not null && !task.IsCompleted)
        {
            try
            {
                if (!task.Wait(TimeSpan.FromSeconds(Math.Max(0, timeoutSeconds))))
                {
                    return false;
                }
            }
            catch
            {
            }
        }

        string? mountToDiscard;
        lock (_lifecycleLock)
        {
            mountToDiscard = _contentMountForExit;
            _contentMountForExit = null;
        }

        if (!string.IsNullOrEmpty(mountToDiscard))
        {
            Discard(mountToDiscard);
        }

        SimsSimulation.InMemorySessionAssets.DisableAndClearRoot(Session);
        return true;
    }

    public void Close(bool save = false, TimeSpan? timeout = null)
    {
        Stop(save);
        _ = WaitForExit((timeout ?? TimeSpan.FromSeconds(2)).TotalSeconds);
    }

    public async Task CloseAsync(bool save = false, TimeSpan? timeout = null)
    {
        Stop(save);
        Task? task;
        lock (_lifecycleLock)
        {
            task = _serverTask;
        }

        if (task is not null && !task.IsCompleted)
        {
            try
            {
                await task.WaitAsync(timeout ?? TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        string? mountToDiscard;
        lock (_lifecycleLock)
        {
            mountToDiscard = _contentMountForExit;
            _contentMountForExit = null;
        }

        if (!string.IsNullOrEmpty(mountToDiscard))
        {
            Discard(mountToDiscard);
        }

        SimsSimulation.InMemorySessionAssets.DisableAndClearRoot(Session);
    }

    public (string Session, string Manifest) RunExportNeighborhood(
        GameContent content,
        int mode = 1,
        string? edit = null,
        bool discardsContent = true,
        bool nativeEngine = false,
        bool includeWebScenes = false) =>
        NeighborhoodExport.CreateSynchronously(
            content,
            Workspace,
            DataRoot,
            mode,
            ProfileId,
            edit,
            Neighborhood,
            discardsContent,
            contentRoot: null,
            nativeEngine,
            includeWebScenes);

    public Task<(string Session, string Manifest)> RunExportNeighborhoodAsync(
        GameContent content,
        int mode = 1,
        string? edit = null,
        bool discardsContent = true,
        bool nativeEngine = false,
        bool includeWebScenes = false) =>
        NeighborhoodExport.Create(
            content,
            Workspace,
            DataRoot,
            mode,
            ProfileId,
            edit,
            Neighborhood,
            discardsContent,
            nativeEngine,
            includeWebScenes);

    private static (string InstallRoot, string CharactersDir, int FileCount, long MaxWriteTicks, SimsEngine.Content.ObjectRegistry Registry)? _cachedAppearanceRegistry;

    private static SimsEngine.Content.ObjectRegistry GetOrScanAppearanceRegistry(string installRoot, string userDataPath)
    {
        var fullInstall = Path.GetFullPath(installRoot);
        var charsDir = Path.GetFullPath(Path.Combine(userDataPath, "Characters"));
        var count = 0;
        var maxTicks = 0L;
        if (Directory.Exists(charsDir))
        {
            foreach (var f in Directory.EnumerateFiles(charsDir, "*.iff"))
            {
                count++;
                var ticks = File.GetLastWriteTimeUtc(f).Ticks;
                if (ticks > maxTicks) maxTicks = ticks;
            }
        }
        if (_cachedAppearanceRegistry is { } hit &&
            string.Equals(hit.InstallRoot, fullInstall, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(hit.CharactersDir, charsDir, StringComparison.OrdinalIgnoreCase) &&
            hit.FileCount == count &&
            hit.MaxWriteTicks == maxTicks)
        {
            return hit.Registry;
        }
        var scanned = SimsEngine.Content.ObjectRegistry.Scan(installRoot, charsDir);
        _cachedAppearanceRegistry = (fullInstall, charsDir, count, maxTicks, scanned);
        return scanned;
    }

    public HouseholdAppearanceCatalog GetAppearanceCatalog(
        string installRoot,
        string gender,
        string age,
        string skin) =>
        GetAppearanceCatalog(installRoot, DataRoot, ProfileId, Neighborhood, gender, age, skin);

    public static HouseholdAppearanceCatalog GetAppearanceCatalog(
        string installRoot,
        string dataRoot,
        string profileId,
        int neighborhood,
        string gender,
        string age,
        string skin)
    {
        ArgumentException.ThrowIfNullOrEmpty(installRoot);
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);
        ArgumentException.ThrowIfNullOrEmpty(gender);
        ArgumentException.ThrowIfNullOrEmpty(age);
        ArgumentException.ThrowIfNullOrEmpty(skin);

        lock (SimulationHost.SimulationStateLock)
        {
            var previousNeighborhood = global::Neighborhoods.Current;
            try
            {
                var (_, userDataPath) = SimulationHost.PrepareNeighborhood(installRoot, dataRoot, profileId, neighborhood);
                var registry = GetOrScanAppearanceRegistry(installRoot, userDataPath);
                var catalog = global::HouseholdAppearance.Catalog(installRoot, registry, gender, age, skin);
                return new HouseholdAppearanceCatalog(
                    catalog.Gender,
                    catalog.Age,
                    catalog.Skin,
                    catalog.Heads.Select(h => new HouseholdAppearanceCatalog.Choice(h.Id, h.Texture, h.BodyType)).ToList(),
                    catalog.Bodies.Select(b => new HouseholdAppearanceCatalog.Choice(b.Id, b.Texture, b.BodyType)).ToList());
            }
            finally
            {
                global::Neighborhoods.Current = previousNeighborhood;
            }
        }
    }

    public Task<HouseholdAppearanceCatalog> GetAppearanceCatalogAsync(
        string installRoot,
        string gender,
        string age,
        string skin) =>
        Task.Run(() => GetAppearanceCatalog(installRoot, gender, age, skin));

    public string GetAppearanceCatalogJson(string installRoot, string gender, string age, string skin) =>
        JsonSerializer.Serialize(GetAppearanceCatalog(installRoot, gender, age, skin), CamelCaseOptions);

    public Dictionary<string, JsonElement> ExportAppearancePreview(
        string previewSession,
        string installRoot,
        string gender,
        string age,
        string skin,
        string headChoice,
        string bodyChoice,
        bool portrait = false) =>
        ExportAppearancePreview(
            previewSession,
            installRoot,
            DataRoot,
            ProfileId,
            Neighborhood,
            gender,
            age,
            skin,
            headChoice,
            bodyChoice,
            portrait);

    public static Dictionary<string, JsonElement> ExportAppearancePreview(
        string previewSession,
        string installRoot,
        string dataRoot,
        string profileId,
        int neighborhood,
        string gender,
        string age,
        string skin,
        string headChoice,
        string bodyChoice,
        bool portrait = false)
    {
        var json = ExportAppearancePreviewJson(
            previewSession,
            installRoot,
            dataRoot,
            profileId,
            neighborhood,
            gender,
            age,
            skin,
            headChoice,
            bodyChoice,
            portrait);
        return JsonMapExtensions.DecodeJsonLine(json);
    }

    public Task<Dictionary<string, JsonElement>> ExportAppearancePreviewAsync(
        string previewSession,
        string installRoot,
        string gender,
        string age,
        string skin,
        string headChoice,
        string bodyChoice,
        bool portrait = false) =>
        Task.Run(() => ExportAppearancePreview(previewSession, installRoot, gender, age, skin, headChoice, bodyChoice, portrait));

    public static string ExportAppearancePreviewJson(
        string previewSession,
        string installRoot,
        string dataRoot,
        string profileId,
        int neighborhood,
        string gender,
        string age,
        string skin,
        string headChoice,
        string bodyChoice,
        bool portrait = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(previewSession);
        ArgumentException.ThrowIfNullOrEmpty(installRoot);
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);

        lock (SimulationHost.SimulationStateLock)
        {
            var previousNeighborhood = global::Neighborhoods.Current;
            try
            {
                var (_, userDataPath) = SimulationHost.PrepareNeighborhood(installRoot, dataRoot, profileId, neighborhood);
                var registry = GetOrScanAppearanceRegistry(installRoot, userDataPath);
                var preview = global::HouseholdAppearancePreview.Export(
                    Path.GetFullPath(previewSession),
                    installRoot,
                    registry,
                    gender,
                    age,
                    skin,
                    headChoice,
                    bodyChoice,
                    portrait);
                return JsonSerializer.Serialize(preview, CamelCaseOptions);
            }
            finally
            {
                global::Neighborhoods.Current = previousNeighborhood;
            }
        }
    }

    public void Dispose() => Close();

    private void DeliverEvent(Dictionary<string, JsonElement> message, long generation)
    {
        if (_stopped || generation != Volatile.Read(ref _generation))
        {
            return;
        }

        var type = message.JsonString("type");
        if (string.Equals(type, "scene", StringComparison.Ordinal))
        {
            OnScene?.Invoke(message);
        }
        else if (string.Equals(type, "audio", StringComparison.Ordinal))
        {
            OnAudio?.Invoke(message);
        }

        OnEvent?.Invoke(message);
    }

    internal static void Discard(string mount)
    {
        lock (DiscardLock)
        {
            if (!Directory.Exists(mount) && !File.Exists(mount))
            {
                return;
            }

            try
            {
                SessionSweeper.DeleteEntrySafely(mount);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"Cannot remove content mount {mount}: {error.Message}");
            }
        }
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private sealed class LineForwardingTextWriter : TextWriter
    {
        internal const string SizeLimitError = "Simulation message exceeds the protocol size limit";
        private const string AnimationPrefix = "{\"type\":\"animation\",";

        private sealed class AnimationEventEnvelope
        {
            public SceneAnimation? Frame { get; init; }
        }

        private readonly Lock _bufferLock = new();
        private readonly StringBuilder _partial = new();
        private readonly string _resolvedSessionRoot;
        private readonly Func<bool> _shouldDecodeAnimation;
        private readonly Action<SceneAnimation> _onAnimation;
        private readonly Action<Dictionary<string, JsonElement>> _onEvent;
        private readonly Action<string> _onFailure;
        private bool _overflowed;

        public override Encoding Encoding => Encoding.UTF8;

        public LineForwardingTextWriter(
            string sessionRoot,
            Func<bool> shouldDecodeAnimation,
            Action<SceneAnimation> onAnimation,
            Action<Dictionary<string, JsonElement>> onEvent,
            Action<string> onFailure)
        {
            _resolvedSessionRoot = ResolveRealPath(sessionRoot);
            _shouldDecodeAnimation = shouldDecodeAnimation;
            _onAnimation = onAnimation;
            _onEvent = onEvent;
            _onFailure = onFailure;
        }

        public override void Write(char value)
        {
            string? completedLine = null;
            var hitLimit = false;
            lock (_bufferLock)
            {
                if (_overflowed)
                {
                    return;
                }

                if (value == '\n')
                {
                    completedLine = ConsumePartialLocked();
                }
                else if (value != '\r')
                {
                    _partial.Append(value);
                    if (Encoding.UTF8.GetByteCount(_partial.ToString()) > MaxMessageBytes)
                    {
                        _overflowed = true;
                        _partial.Clear();
                        hitLimit = true;
                    }
                }
            }

            if (hitLimit)
            {
                _onFailure(SizeLimitError);
            }
            else if (completedLine is not null)
            {
                ProcessLine(completedLine);
            }
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            List<string>? completed = null;
            var hitLimit = false;
            lock (_bufferLock)
            {
                if (_overflowed)
                {
                    return;
                }

                var start = 0;
                while (start < value.Length)
                {
                    var nl = value.IndexOf('\n', start);
                    if (nl < 0)
                    {
                        _partial.Append(value, start, value.Length - start);
                        if (_partial.Length * 3 > MaxMessageBytes &&
                            Encoding.UTF8.GetByteCount(_partial.ToString()) > MaxMessageBytes)
                        {
                            _overflowed = true;
                            _partial.Clear();
                            hitLimit = true;
                        }
                        break;
                    }

                    var segmentLen = nl - start;
                    if (segmentLen > 0 && value[nl - 1] == '\r')
                    {
                        segmentLen--;
                    }

                    string line;
                    if (_partial.Length == 0)
                    {
                        line = value.Substring(start, segmentLen);
                    }
                    else
                    {
                        _partial.Append(value, start, segmentLen);
                        line = ConsumePartialLocked();
                    }

                    if (Encoding.UTF8.GetByteCount(line) > MaxMessageBytes)
                    {
                        _overflowed = true;
                        _partial.Clear();
                        hitLimit = true;
                        break;
                    }

                    completed ??= [];
                    completed.Add(line);
                    start = nl + 1;
                }
            }

            if (completed is not null)
            {
                foreach (var line in completed)
                {
                    ProcessLine(line);
                }
            }

            if (hitLimit)
            {
                _onFailure(SizeLimitError);
            }
        }

        public override void WriteLine() => Write('\n');

        public override void WriteLine(string? value)
        {
            string? fastLine = null;
            var hitLimit = false;
            lock (_bufferLock)
            {
                if (_overflowed)
                {
                    return;
                }

                if (_partial.Length == 0 && (value is null || !value.Contains('\n')))
                {
                    fastLine = (value ?? string.Empty).TrimEnd('\r');
                    if (Encoding.UTF8.GetByteCount(fastLine) > MaxMessageBytes)
                    {
                        _overflowed = true;
                        hitLimit = true;
                        fastLine = null;
                    }
                }
            }

            if (hitLimit)
            {
                _onFailure(SizeLimitError);
                return;
            }

            if (fastLine is not null)
            {
                ProcessLine(fastLine);
                return;
            }

            if (!string.IsNullOrEmpty(value))
            {
                Write(value);
            }

            Write('\n');
        }

        private string ConsumePartialLocked()
        {
            var length = _partial.Length;
            if (length > 0 && _partial[length - 1] == '\r')
            {
                length--;
            }

            var line = _partial.ToString(0, length);
            _partial.Clear();
            if (_partial.Capacity > (1 << 20))
            {
                _partial.Capacity = 0;
            }

            return line;
        }

        private void ProcessLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            try
            {
                if (_shouldDecodeAnimation() && line.StartsWith(AnimationPrefix, StringComparison.Ordinal))
                {
                    var envelope = JsonSerializer.Deserialize<AnimationEventEnvelope>(line, CamelCaseOptions);
                    if (envelope?.Frame is null)
                    {
                        throw new ContentError("Invalid animation event");
                    }

                    _onAnimation(envelope.Frame);
                    return;
                }

                var message = JsonMapExtensions.DecodeJsonLine(line);
                if (message.TryGetValue("scene", out var sceneEl) &&
                    sceneEl.ValueKind == JsonValueKind.String &&
                    sceneEl.GetString() is { } scenePath)
                {
                    var resolvedScene = ResolveRealPath(scenePath);
                    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    var rootWithSep = _resolvedSessionRoot + Path.DirectorySeparatorChar;
                    if (!resolvedScene.StartsWith(rootWithSep, comparison) &&
                        !string.Equals(resolvedScene, _resolvedSessionRoot, comparison))
                    {
                        throw new ContentError("Simulation scene escapes session");
                    }
                }

                _onEvent(message);
            }
            catch (Exception error)
            {
                _onFailure(error.Message);
            }
        }

        private static string ResolveRealPath(string path)
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            try
            {
                var parent = Path.GetDirectoryName(fullPath);
                var leaf = Path.GetFileName(fullPath);
                if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(leaf))
                {
                    fullPath = Path.Combine(ResolveRealPath(parent), leaf);
                }

                if (File.Exists(fullPath))
                {
                    return Path.TrimEndingDirectorySeparator(
                        File.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName ?? fullPath);
                }

                if (Directory.Exists(fullPath))
                {
                    return Path.TrimEndingDirectorySeparator(
                        Directory.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName ?? fullPath);
                }
            }
            catch
            {
            }

            return fullPath;
        }
    }
}
