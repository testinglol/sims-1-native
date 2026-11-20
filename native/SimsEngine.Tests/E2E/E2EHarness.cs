using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsSDL.Client;
using SimsSimulation;

namespace SimsEngine.Tests.E2E;

internal static class E2EHarness
{
    public static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string? TryLocateGameDir() => TestGameLocator.Locate();

    public static bool TryLocateGameDir(out string gameDir)
    {
        var located = TestGameLocator.Locate();
        if (located is not null && Directory.Exists(located))
        {
            gameDir = located;
            return true;
        }

        gameDir = string.Empty;
        return false;
    }

    public static string RequireGameDir() =>
        TestGameLocator.Locate()
        ?? throw new InvalidOperationException("Unable to locate The Sims 1 game directory (set SIMS_GAME_DIR or place runtime/game or The Sims 1 in repo).");

    public static bool HasHouses(string gameDir, params short[] houses)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            return false;

        var neighborhoodPath = Path.Combine(gameDir, "UserData", "Neighborhood.iff");
        if (!File.Exists(neighborhoodPath))
            return false;

        foreach (var house in houses)
        {
            if (FindHouseSource(gameDir, 1, house) is null)
                return false;
        }

        return true;
    }

    internal static string? FindHouseSource(string gameDir, int neighborhood, short house)
    {
        var userDataName = Neighborhoods.DirectoryName(neighborhood);
        var candidates = new List<string>
        {
            Path.Combine(gameDir, userDataName, "Houses", $"House{house:D2}.iff"),
            Path.Combine(gameDir, "UserData", "Houses", $"House{house:D2}.iff"),
            Path.Combine(gameDir, "TemplateUserData", "Houses", $"House{house:D2}.iff")
        };

        if (house is >= 21 and <= 30)
            candidates.Add(Path.Combine(gameDir, "TemplateDowntown", "Houses", $"House{house:D2}.iff"));
        else if (house is >= 40 and <= 48)
            candidates.Add(Path.Combine(gameDir, "TemplateVacation", "Houses", $"House{house:D2}.iff"));
        else if (house is >= 50 and <= 80)
            candidates.Add(Path.Combine(gameDir, "TemplateCommunity", "Houses", $"House{house:D2}.iff"));
        else if (house is >= 81 and <= 89)
            candidates.Add(Path.Combine(gameDir, "TemplateStudiotown", "Houses", $"House{house:D2}.iff"));
        else if (house is >= 90 and <= 99)
            candidates.Add(Path.Combine(gameDir, "TemplateMagictown", "Houses", $"House{house:D2}.iff"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public static IsolatedProfileScope CreateIsolatedProfile(string gameDir, params short[] houses) =>
        CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, houses);

    public static IsolatedProfileScope CreateIsolatedProfile(
        string gameDir,
        bool copyCharacters,
        int neighborhood = 1,
        bool copyDownloads = false,
        params short[] houses)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDir);

        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-e2e-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(tempRoot, "runtime", "profiles", "test");
        var userDataName = Neighborhoods.DirectoryName(neighborhood);
        var userData = Path.Combine(profile, userDataName);
        var housesDir = Path.Combine(userData, "Houses");
        var charsDir = Path.Combine(userData, "Characters");
        var session = Path.Combine(tempRoot, "session");

        Directory.CreateDirectory(housesDir);
        Directory.CreateDirectory(charsDir);
        Directory.CreateDirectory(session);

        var sourceUserData = Path.Combine(gameDir, userDataName);
        if (!Directory.Exists(sourceUserData))
            sourceUserData = Path.Combine(gameDir, "UserData");

        if (copyCharacters && Directory.Exists(Path.Combine(sourceUserData, "Characters")))
        {
            foreach (var charFile in Directory.EnumerateFiles(Path.Combine(sourceUserData, "Characters")))
            {
                File.Copy(charFile, Path.Combine(charsDir, Path.GetFileName(charFile)), overwrite: true);
            }
        }

        foreach (var iffName in new[] { "Neighborhood.iff", "LotZoning.iff", "LotLocations.iff", "StreetNames.iff" })
        {
            var src = Path.Combine(sourceUserData, iffName);
            if (!File.Exists(src))
                src = Path.Combine(gameDir, "UserData", iffName);
            if (File.Exists(src))
                File.Copy(src, Path.Combine(userData, iffName), overwrite: true);
        }

        foreach (var house in houses)
        {
            var src = FindHouseSource(gameDir, neighborhood, house);
            if (src is not null)
                File.Copy(src, Path.Combine(housesDir, $"House{house:D2}.iff"), overwrite: true);
        }

        if (copyDownloads)
        {
            var downloadsSrc = Path.Combine(gameDir, "Downloads");
            if (Directory.Exists(downloadsSrc))
            {
                CopyDirectoryRecursive(downloadsSrc, Path.Combine(profile, "Downloads"));
            }
        }

        var previousNeighborhood = Neighborhoods.Current;
        Neighborhoods.Current = neighborhood;

        return new IsolatedProfileScope(tempRoot, profile, userData, session, gameDir, neighborhood, previousNeighborhood);
    }

    public static ServerSession StartServer(
        IsolatedProfileScope profile,
        short house = 0,
        byte language = 1,
        string? installRoot = null,
        bool ownsProfile = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ServerSession(profile, house, language, installRoot, ownsProfile);
    }

    public static AudioSession CreateAudioSession(
        string? gameDir,
        string? sessionAudioDir = null,
        string? profile = null) =>
        new(gameDir, sessionAudioDir, profile);

    public static Task<FrontendAudioSession> StartFrontendAudioServerAsync(
        IsolatedProfileScope profile,
        byte languageCode = 1)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return FrontendAudioSession.StartAsync(profile, languageCode);
    }

    public static Task<ServerLoopSession> StartServerLoopAsync(
        IsolatedProfileScope profile,
        short house = 0,
        byte language = 1,
        string? installRoot = null,
        Action<SimulationServer>? beforeRun = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ServerLoopSession.StartAsync(profile, house, language, installRoot, beforeRun);
    }

    public static byte[] BuildSyntheticFarArchive(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        ms.Write("FAR!byAZ"u8);
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 1);
        ms.Write(u32);
        ms.Write(u32); // placeholder for manifest offset

        var offsets = new int[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            offsets[i] = (int)ms.Position;
            ms.Write(entries[i].Data);
        }

        uint manifestOffset = (uint)ms.Position;
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)entries.Length);
        ms.Write(u32);
        for (int i = 0; i < entries.Length; i++)
        {
            var nameBytes = Encoding.Latin1.GetBytes(entries[i].Name);
            BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)entries[i].Data.Length);
            ms.Write(u32);
            BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)entries[i].Data.Length);
            ms.Write(u32);
            BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)offsets[i]);
            ms.Write(u32);
            BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)nameBytes.Length);
            ms.Write(u32);
            ms.Write(nameBytes);
        }

        ms.Position = 12;
        BinaryPrimitives.WriteUInt32LittleEndian(u32, manifestOffset);
        ms.Write(u32);
        return ms.ToArray();
    }

    public static byte[] BuildSyntheticWav(int sampleRate = 22050, short channels = 1, int frames = 1102)
    {
        var pcm = new byte[frames * channels * 2];
        for (int i = 0; i < frames * channels; i++)
        {
            short val = (short)(Math.Sin(i * 0.1) * 12000);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), val);
        }

        return XaAudio.CreateRiffWav(sampleRate, channels, 16, pcm);
    }

    public static byte[] BuildValidSyntheticUtk(int targetSamples = 432)
    {
        // Construct a minimal valid UTM0 stream using non-multipulse mode (baseThre=15 -> threshold=17,
        // rc0 index=32 >= 17) so all remaining 0-bits decode cleanly as zero-pulse excitation frames.
        var bytes = new byte[256];
        bytes[0] = (byte)'U';
        bytes[1] = (byte)'T';
        bytes[2] = (byte)'M';
        bytes[3] = (byte)'0';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)(targetSamples * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 22050);

        // Bitstream starts at offset 0x20:
        // bit 0 = 0 (reducedBandwidth=false), bits 1..4 = 15 (baseThre=15), bits 5..8 = 0, bits 9..14 = 0,
        // bits 15..20 = 32 (rc0 index = 32 >= 17 -> non-multipulse mode).
        uint headerBits = (15u << 1) | (32u << 15);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x20, 4), headerBits);
        return bytes;
    }

    internal static void DeleteTempTree(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            SessionSweeper.DeleteEntrySafely(path);
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
            CopyDirectoryRecursive(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
    }
}

internal sealed class IsolatedProfileScope : IDisposable
{
    private bool _disposed;

    public IsolatedProfileScope(
        string tempRoot,
        string profile,
        string userData,
        string session,
        string gameDir,
        int neighborhoodNumber,
        int previousNeighborhood)
    {
        TempRoot = tempRoot;
        Profile = profile;
        UserData = userData;
        Session = session;
        GameDir = gameDir;
        NeighborhoodNumber = neighborhoodNumber;
        PreviousNeighborhood = previousNeighborhood;
    }

    public string TempRoot { get; }
    public string Profile { get; }
    public string UserData { get; }
    public string Session { get; }
    public string GameDir { get; }
    public int NeighborhoodNumber { get; }
    public int PreviousNeighborhood { get; }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Neighborhoods.Current = PreviousNeighborhood;
        E2EHarness.DeleteTempTree(TempRoot);
    }
}

internal sealed class ServerSession : IDisposable
{
    private readonly MemoryStream _outputStream;
    private readonly StreamWriter _writer;
    private readonly bool _ownsProfile;
    private readonly List<JsonElement> _allEvents = [];
    private int _nextId;
    private bool _disposed;

    public ServerSession(
        IsolatedProfileScope profile,
        short house = 0,
        byte language = 1,
        string? installRoot = null,
        bool ownsProfile = false)
    {
        Profile = profile;
        _ownsProfile = ownsProfile;
        _outputStream = new MemoryStream();
        _writer = new StreamWriter(_outputStream, new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            Server = SimulationServer.Load(profile.GameDir, profile.Session, profile.Profile, house, _writer, language, installRoot);
            var initial = DrainEvents();
            LastEvents = initial;
            _allEvents.AddRange(initial);
        }
        catch
        {
            _writer.Dispose();
            _outputStream.Dispose();
            if (_ownsProfile)
                Profile.Dispose();
            throw;
        }
    }

    public IsolatedProfileScope Profile { get; }
    public SimulationServer Server { get; }
    public SimulationWorld? World => Server.World;
    public IReadOnlyList<JsonElement> AllEvents => _allEvents;
    public JsonElement[] LastEvents { get; private set; }

    public bool Handle(string jsonLine) => Server.Handle(jsonLine);

    public JsonElement Send(string id, string op, object? parameters = null)
    {
        var payload = BuildRequestJson(id, op, parameters);
        Server.Handle(payload);
        var drained = DrainEvents();
        LastEvents = drained;
        _allEvents.AddRange(drained);
        return ResponseFor(drained, id);
    }

    public JsonElement HandleJson(string op, object? parameters = null, string? id = null)
    {
        var reqId = id ?? $"req-{Interlocked.Increment(ref _nextId)}";
        return Send(reqId, op, parameters);
    }

    public int BatchRun(int ticks) => Server.BatchRun(ticks);

    public JsonElement[] DrainEvents()
    {
        _writer.Flush();
        var text = Encoding.UTF8.GetString(_outputStream.GetBuffer(), 0, (int)_outputStream.Length);
        _outputStream.SetLength(0);
        _outputStream.Position = 0;
        var parsed = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
        return parsed;
    }

    public static JsonElement ResponseFor(JsonElement[] events, string id) =>
        events.Single(e => e.TryGetProperty("id", out var idEl) && idEl.GetString() == id);

    public JsonElement State()
    {
        var resp = HandleJson("state");
        Assert.True(resp.GetProperty("ok").GetBoolean(), resp.ToString());
        return resp.GetProperty("result").Clone();
    }

    public JsonElement Step(int count = 1)
    {
        var resp = HandleJson("step", new { count });
        Assert.True(resp.GetProperty("ok").GetBoolean(), resp.ToString());
        return resp.GetProperty("result").Clone();
    }

    public string LatestScenePath()
    {
        for (int i = _allEvents.Count - 1; i >= 0; i--)
        {
            var ev = _allEvents[i];
            if (ev.TryGetProperty("type", out var t) && t.GetString() == "frame" &&
                ev.TryGetProperty("scene", out var s) && s.ValueKind == JsonValueKind.String)
            {
                return s.GetString()!;
            }
        }

        var resp = HandleJson("scene");
        Assert.True(resp.GetProperty("ok").GetBoolean(), resp.ToString());
        for (int i = LastEvents.Length - 1; i >= 0; i--)
        {
            var ev = LastEvents[i];
            if (ev.TryGetProperty("type", out var t) && t.GetString() == "frame" &&
                ev.TryGetProperty("scene", out var s) && s.ValueKind == JsonValueKind.String)
            {
                return s.GetString()!;
            }
        }

        return resp.GetProperty("result").GetString()!;
    }

    public JsonDocument ReadLatestScene()
    {
        var path = LatestScenePath();
        var bytes = InMemorySessionAssets.ReadAllBytes(path);
        return JsonDocument.Parse(bytes);
    }

    private static string BuildRequestJson(string id, string op, object? parameters)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("id", id);
            json.WriteString("op", op);
            if (parameters is not null)
            {
                var paramEl = JsonSerializer.SerializeToElement(parameters, E2EHarness.CamelCaseJson);
                if (paramEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in paramEl.EnumerateObject())
                    {
                        if (prop.NameEquals("id") || prop.NameEquals("op"))
                            continue;
                        prop.WriteTo(json);
                    }
                }
            }
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _writer.Dispose();
        _outputStream.Dispose();
        if (_ownsProfile)
            Profile.Dispose();
    }
}

internal sealed class AudioSession : IDisposable
{
    private readonly string? _ownedTempRoot;
    private readonly List<JsonElement> _events = [];
    private readonly Lock _sync = new();
    private bool _disposed;

    public AudioSession(string? gameDir, string? sessionAudioDir = null, string? profile = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionAudioDir))
            {
                _ownedTempRoot = Path.Combine(Path.GetTempPath(), "sims-e2e-audio-" + Guid.NewGuid().ToString("N"));
                SessionAudioDirectory = Path.Combine(_ownedTempRoot, "audio");
                Directory.CreateDirectory(SessionAudioDirectory);
            }
            else
            {
                SessionAudioDirectory = sessionAudioDir;
                Directory.CreateDirectory(SessionAudioDirectory);
            }

            var profileDir = profile ?? SessionAudioDirectory;
            Bank = new SoundBank(gameDir, SessionAudioDirectory);
            Runtime = new HitAudioRuntime(SessionAudioDirectory, profileDir, OnAudioEvent, gameDir);
        }
        catch
        {
            Runtime?.Dispose();
            Bank?.Dispose();
            if (_ownedTempRoot is not null)
                E2EHarness.DeleteTempTree(_ownedTempRoot);
            throw;
        }
    }

    public string SessionAudioDirectory { get; }
    public SoundBank Bank { get; } = null!;
    public HitAudioRuntime Runtime { get; } = null!;

    public IReadOnlyList<JsonElement> Events
    {
        get
        {
            lock (_sync)
                return _events.ToArray();
        }
    }

    public JsonElement[] DrainAudioEvents()
    {
        lock (_sync)
        {
            var copy = _events.ToArray();
            _events.Clear();
            return copy;
        }
    }

    public IReadOnlyList<JsonElement> PlayEvents() => FilterByCommand("play");
    public IReadOnlyList<JsonElement> StopEvents() => FilterByCommand("stop");
    public IReadOnlyList<JsonElement> PauseEvents() => FilterByCommand("pause");
    public IReadOnlyList<JsonElement> ResumeEvents() => FilterByCommand("resume");
    public IReadOnlyList<JsonElement> UpdateEvents() => FilterByCommand("update");

    private IReadOnlyList<JsonElement> FilterByCommand(string command)
    {
        lock (_sync)
        {
            return _events
                .Where(e => e.TryGetProperty("command", out var cmd) && cmd.GetString() == command)
                .ToArray();
        }
    }

    private void OnAudioEvent(object evt)
    {
        var element = JsonSerializer.SerializeToElement(evt, E2EHarness.CamelCaseJson).Clone();
        lock (_sync)
            _events.Add(element);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Runtime.Dispose();
        Bank.Dispose();
        if (_ownedTempRoot is not null)
            E2EHarness.DeleteTempTree(_ownedTempRoot);
    }
}

internal sealed class FrontendAudioSession : IAsyncDisposable
{
    private sealed class SynchronizedBufferWriter : TextWriter
    {
        private readonly Lock _lock = new();
        private readonly StringBuilder _sb = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_lock) _sb.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            lock (_lock) _sb.Append(value);
        }

        public override void WriteLine(string? value)
        {
            lock (_lock) _sb.AppendLine(value);
        }

        public string Drain()
        {
            lock (_lock)
            {
                var text = _sb.ToString();
                _sb.Clear();
                return text;
            }
        }
    }

    private readonly Channel<string> _input;
    private readonly SynchronizedBufferWriter _output;
    private readonly Task _runTask;
    private readonly List<JsonElement> _pendingEvents = [];
    private int _nextId;
    private bool _disposed;

    private FrontendAudioSession(Channel<string> input, SynchronizedBufferWriter output, Task runTask)
    {
        _input = input;
        _output = output;
        _runTask = runTask;
    }

    public static async Task<FrontendAudioSession> StartAsync(IsolatedProfileScope profile, byte languageCode = 1)
    {
        var input = Channel.CreateUnbounded<string>();
        var output = new SynchronizedBufferWriter();
        Task? runTask = null;
        try
        {
            var server = new FrontendAudioServer(output, profile.Session, profile.Profile, profile.GameDir, languageCode);
            runTask = Task.Run(() => server.Run(input.Reader));

            var session = new FrontendAudioSession(input, output, runTask);
            await session.WaitForEventAsync(e =>
                e.TryGetProperty("type", out var t) && t.GetString() == "hello", TimeSpan.FromSeconds(5));
            return session;
        }
        catch
        {
            input.Writer.TryComplete();
            if (runTask is not null)
            {
                try { await runTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
            output.Dispose();
            throw;
        }
    }

    public async Task<JsonElement> SendAsync(string op, object? parameters = null, TimeSpan? timeout = null)
    {
        var id = $"fa-{Interlocked.Increment(ref _nextId)}";
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("id", id);
            json.WriteString("op", op);
            if (parameters is not null)
            {
                var paramEl = JsonSerializer.SerializeToElement(parameters, E2EHarness.CamelCaseJson);
                if (paramEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in paramEl.EnumerateObject())
                    {
                        if (prop.NameEquals("id") || prop.NameEquals("op"))
                            continue;
                        prop.WriteTo(json);
                    }
                }
            }
            json.WriteEndObject();
        }

        await _input.Writer.WriteAsync(Encoding.UTF8.GetString(stream.ToArray()));
        return await WaitForEventAsync(
            e => e.TryGetProperty("id", out var idEl) && idEl.GetString() == id,
            timeout ?? TimeSpan.FromSeconds(5));
    }

    public JsonElement[] DrainEvents()
    {
        var text = _output.Drain();
        var fresh = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
        if (_pendingEvents.Count == 0)
            return fresh;

        var combined = new List<JsonElement>(_pendingEvents.Count + fresh.Length);
        combined.AddRange(_pendingEvents);
        combined.AddRange(fresh);
        _pendingEvents.Clear();
        return combined.ToArray();
    }

    public async Task<JsonElement> WaitForEventAsync(Func<JsonElement, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var batch = DrainEvents();
            for (int i = 0; i < batch.Length; i++)
            {
                if (predicate(batch[i]))
                {
                    for (int j = i + 1; j < batch.Length; j++)
                        _pendingEvents.Add(batch[j]);
                    return batch[i];
                }
                _pendingEvents.Add(batch[i]);
            }
            await Task.Delay(5);
        }

        throw new TimeoutException("Timed out waiting for FrontendAudioServer event.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            await SendAsync("quit", timeout: TimeSpan.FromSeconds(2));
        }
        catch { }

        _input.Writer.TryComplete();
        try { await _runTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _output.Dispose();
    }
}

internal sealed class ServerLoopSession : IAsyncDisposable
{
    private sealed class SynchronizedBufferWriter : TextWriter
    {
        private readonly Lock _lock = new();
        private readonly StringBuilder _sb = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_lock) _sb.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            lock (_lock) _sb.Append(value);
        }

        public override void WriteLine(string? value)
        {
            lock (_lock) _sb.AppendLine(value);
        }

        public string Drain()
        {
            lock (_lock)
            {
                var text = _sb.ToString();
                _sb.Clear();
                return text;
            }
        }
    }

    private readonly Channel<string> _input;
    private readonly SynchronizedBufferWriter _output;
    private readonly Task _runTask;
    private readonly List<JsonElement> _pendingEvents = [];
    private int _nextId;
    private bool _stopped;
    private bool _disposed;

    private ServerLoopSession(SimulationServer server, Channel<string> input, SynchronizedBufferWriter output, Task runTask)
    {
        Server = server;
        _input = input;
        _output = output;
        _runTask = runTask;
    }

    public SimulationServer Server { get; }
    public SimulationWorld? World => Server.World;

    public static async Task<ServerLoopSession> StartAsync(
        IsolatedProfileScope profile,
        short house = 0,
        byte language = 1,
        string? installRoot = null,
        Action<SimulationServer>? beforeRun = null)
    {
        var input = Channel.CreateUnbounded<string>();
        var output = new SynchronizedBufferWriter();
        Task? runTask = null;
        try
        {
            var server = SimulationServer.Load(profile.GameDir, profile.Session, profile.Profile, house, output, language, installRoot);
            beforeRun?.Invoke(server);
            runTask = Task.Run(() => server.Run(input.Reader));

            var session = new ServerLoopSession(server, input, output, runTask);
            await session.WaitForEventAsync(e =>
                e.TryGetProperty("type", out var t) && t.GetString() == "hello", TimeSpan.FromSeconds(10));
            return session;
        }
        catch
        {
            input.Writer.TryComplete();
            if (runTask is not null)
            {
                try { await runTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
            output.Dispose();
            throw;
        }
    }

    public async Task<JsonElement> SendAsync(string op, object? parameters = null, TimeSpan? timeout = null)
    {
        var id = $"sl-{Interlocked.Increment(ref _nextId)}";
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("id", id);
            json.WriteString("op", op);
            if (parameters is not null)
            {
                var paramEl = JsonSerializer.SerializeToElement(parameters, E2EHarness.CamelCaseJson);
                if (paramEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in paramEl.EnumerateObject())
                    {
                        if (prop.NameEquals("id") || prop.NameEquals("op"))
                            continue;
                        prop.WriteTo(json);
                    }
                }
            }
            json.WriteEndObject();
        }

        await _input.Writer.WriteAsync(Encoding.UTF8.GetString(stream.ToArray()));
        return await WaitForEventAsync(
            e => e.TryGetProperty("id", out var idEl) && idEl.GetString() == id,
            timeout ?? TimeSpan.FromSeconds(10));
    }

    public JsonElement[] DrainEvents()
    {
        var text = _output.Drain();
        var fresh = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
        if (_pendingEvents.Count == 0)
            return fresh;

        var combined = new List<JsonElement>(_pendingEvents.Count + fresh.Length);
        combined.AddRange(_pendingEvents);
        combined.AddRange(fresh);
        _pendingEvents.Clear();
        return combined.ToArray();
    }

    public async Task<JsonElement> WaitForEventAsync(Func<JsonElement, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var batch = DrainEvents();
            for (int i = 0; i < batch.Length; i++)
            {
                if (predicate(batch[i]))
                {
                    for (int j = i + 1; j < batch.Length; j++)
                        _pendingEvents.Add(batch[j]);
                    return batch[i];
                }
                _pendingEvents.Add(batch[i]);
            }
            await Task.Delay(5);
        }

        throw new TimeoutException("Timed out waiting for SimulationServer loop event.");
    }

    public async Task StopAsync()
    {
        if (_stopped)
            return;

        _stopped = true;
        try
        {
            await SendAsync("quit", timeout: TimeSpan.FromSeconds(2));
        }
        catch { }

        _input.Writer.TryComplete();
        await _runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        try { await StopAsync(); } catch { }
        _output.Dispose();
    }
}
