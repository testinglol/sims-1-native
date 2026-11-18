using System.Diagnostics;

namespace SimsSDL.Client;

/// <summary>Thread-safe file logger writing timestamped session logs and a rolling <c>latest.log</c> mirror
/// under the user data root, while optionally teeing <see cref="Console.Error"/> output.</summary>
public sealed class AppLogSession : IDisposable
{
    private const int MaxDeduplicatedKeys = 256;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock _lock = new();
    private readonly HashSet<string> _onceKeys = new(StringComparer.Ordinal);
    private StreamWriter? _sessionWriter;
    private StreamWriter? _latestWriter;
    private bool _disposed;

    public string? LogFilePath { get; }
    public string? LatestLogPath { get; }
    public string? LogsDirectory { get; }

    private AppLogSession(
        string? logFilePath,
        string? latestLogPath,
        string? logsDirectory,
        StreamWriter? sessionWriter,
        StreamWriter? latestWriter)
    {
        LogFilePath = logFilePath;
        LatestLogPath = latestLogPath;
        LogsDirectory = logsDirectory;
        _sessionWriter = sessionWriter;
        _latestWriter = latestWriter;
    }

    public static AppLogSession Open(string dataRoot, string? explicitLogFile = null, int maxRetainedLogs = 10)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);
        if (!string.IsNullOrWhiteSpace(explicitLogFile))
        {
            string fullPath = Path.GetFullPath(explicitLogFile);
            string? dir = Path.GetDirectoryName(fullPath);
            StreamWriter? explicitWriter = TryOpenWriter(fullPath, FileMode.Append);
            return new AppLogSession(
                explicitWriter is not null ? fullPath : null,
                latestLogPath: null,
                logsDirectory: dir,
                explicitWriter,
                latestWriter: null);
        }

        string primaryDir = Path.Combine(dataRoot, "logs");
        if (TryOpenLogDirectory(primaryDir, maxRetainedLogs) is { } opened)
        {
            return opened;
        }

        string fallbackDir = Path.Combine(Path.GetTempPath(), "TheSimsNative", "logs");
        if (TryOpenLogDirectory(fallbackDir, maxRetainedLogs) is { } fallback)
        {
            return fallback;
        }

        return new AppLogSession(null, null, null, null, null);
    }

    private static AppLogSession? TryOpenLogDirectory(string logsDir, int maxRetainedLogs)
    {
        try
        {
            Directory.CreateDirectory(logsDir);
        }
        catch
        {
            return null;
        }

        PruneOldSessionLogs(logsDir, Math.Max(1, maxRetainedLogs) - 1);

        DateTime now = DateTime.Now;
        string baseName = $"sims-native-{now:yyyyMMdd-HHmmss}-{Environment.ProcessId}";
        string sessionPath = Path.Combine(logsDir, $"{baseName}.log");
        int suffix = 1;
        while (File.Exists(sessionPath) && suffix < 100)
        {
            sessionPath = Path.Combine(logsDir, $"{baseName}-{suffix}.log");
            suffix++;
        }

        StreamWriter? sessionWriter = TryOpenWriter(sessionPath, FileMode.Append);
        if (sessionWriter is null)
        {
            return null;
        }

        string latestPath = Path.Combine(logsDir, "latest.log");
        StreamWriter? latestWriter = TryOpenWriter(latestPath, FileMode.Create);
        return new AppLogSession(
            sessionPath,
            latestWriter is not null ? latestPath : null,
            logsDir,
            sessionWriter,
            latestWriter);
    }

    private static StreamWriter? TryOpenWriter(string path, FileMode mode)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var stream = new FileStream(path, mode, FileAccess.Write, FileShare.ReadWrite);
            return new StreamWriter(stream, Utf8NoBom) { AutoFlush = true };
        }
        catch
        {
            return null;
        }
    }

    private static void PruneOldSessionLogs(string logsDir, int keepCount)
    {
        try
        {
            var files = new DirectoryInfo(logsDir)
                .GetFiles("sims-native-*.log")
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .ThenByDescending(static f => f.Name, StringComparer.Ordinal)
                .ToArray();

            for (int i = Math.Max(0, keepCount); i < files.Length; i++)
            {
                try
                {
                    files[i].Delete();
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    public void WriteLine(string level, string message)
    {
        if (message is null)
        {
            return;
        }

        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string normalized = message.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\r', '\n');
        string[] lines = normalized.Split('\n');

        lock (_lock)
        {
            if (_disposed || (_sessionWriter is null && _latestWriter is null))
            {
                return;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string formatted = i == 0
                    ? $"[{timestamp}] [{level}] {lines[i]}"
                    : $"[{timestamp}] [{level}]   {lines[i]}";
                WriteRawLocked(formatted);
            }
        }
    }

    public void WriteOnce(string key, string level, string message)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_onceKeys.Count >= MaxDeduplicatedKeys && !_onceKeys.Contains(key))
            {
                return;
            }

            if (!_onceKeys.Add(key))
            {
                return;
            }
        }

        WriteLine(level, message);
    }

    public void WriteStderrLine(string line) => WriteLine("STDERR", line);

    public void Flush()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _sessionWriter?.Flush();
            }
            catch
            {
            }

            try
            {
                _latestWriter?.Flush();
            }
            catch
            {
            }
        }
    }

    private void WriteRawLocked(string line)
    {
        try
        {
            _sessionWriter?.WriteLine(line);
        }
        catch
        {
        }

        try
        {
            _latestWriter?.WriteLine(line);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _sessionWriter?.Dispose();
            }
            catch
            {
            }

            try
            {
                _latestWriter?.Dispose();
            }
            catch
            {
            }

            _sessionWriter = null;
            _latestWriter = null;
        }
    }
}

/// <summary>Forwards all text written to an inner <see cref="TextWriter"/> while emitting each complete line
/// to a callback.</summary>
internal sealed class TeeTextWriter : TextWriter
{
    private readonly Lock _lock = new();
    private readonly TextWriter _inner;
    private readonly Action<string> _onLine;
    private readonly StringBuilder _pending = new();

    public override Encoding Encoding => _inner.Encoding;

    public TextWriter Inner => _inner;

    public TeeTextWriter(TextWriter inner, Action<string> onLine)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(onLine);
        _inner = inner;
        _onLine = onLine;
    }

    public override void Write(char value)
    {
        string? completed = null;
        lock (_lock)
        {
            _inner.Write(value);
            if (value == '\n')
            {
                completed = DrainPendingLocked();
            }
            else if (value != '\r')
            {
                _pending.Append(value);
            }
        }

        if (completed is not null)
        {
            _onLine(completed);
        }
    }

    public override void Write(char[] buffer, int index, int count) =>
        Write(buffer.AsSpan(index, count));

    public override void Write(ReadOnlySpan<char> buffer)
    {
        List<string>? completed = null;
        lock (_lock)
        {
            _inner.Write(buffer);
            AppendSpanLocked(buffer, ref completed);
        }

        if (completed is not null)
        {
            foreach (string line in completed)
            {
                _onLine(line);
            }
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        Write(value.AsSpan());
    }

    public override void WriteLine()
    {
        string completed;
        lock (_lock)
        {
            _inner.WriteLine();
            completed = DrainPendingLocked();
        }

        _onLine(completed);
    }

    public override void WriteLine(string? value)
    {
        List<string>? completed = null;
        string tail;
        lock (_lock)
        {
            _inner.WriteLine(value);
            if (!string.IsNullOrEmpty(value))
            {
                AppendSpanLocked(value.AsSpan(), ref completed);
            }

            tail = DrainPendingLocked();
        }

        if (completed is not null)
        {
            foreach (string line in completed)
            {
                _onLine(line);
            }
        }

        _onLine(tail);
    }

    public override void Flush()
    {
        lock (_lock)
        {
            _inner.Flush();
        }
    }

    public void FlushPartialLine()
    {
        string? remaining = null;
        lock (_lock)
        {
            if (_pending.Length > 0)
            {
                remaining = DrainPendingLocked();
            }

            _inner.Flush();
        }

        if (remaining is not null)
        {
            _onLine(remaining);
        }
    }

    private void AppendSpanLocked(ReadOnlySpan<char> span, ref List<string>? completed)
    {
        for (int i = 0; i < span.Length; i++)
        {
            char ch = span[i];
            if (ch == '\n')
            {
                completed ??= [];
                completed.Add(DrainPendingLocked());
            }
            else if (ch != '\r')
            {
                _pending.Append(ch);
            }
        }
    }

    private string DrainPendingLocked()
    {
        string line = _pending.ToString();
        _pending.Clear();
        return line;
    }
}

/// <summary>Process-wide diagnostic log facade for <c>SimsSDL</c>.</summary>
internal static class AppLog
{
    private static readonly Lock StateLock = new();
    private static AppLogSession? _session;
    private static TeeTextWriter? _stderrTee;
    private static Timer? _watchdogTimer;
    private static long _startupTimestamp;
    private static long _stageTimestamp;
    private static string _currentStage = "Uninitialized";
    private static bool _stageTransient;
    private static int _stageWarnLevel;
    private static string? _modalWaitReason;
    private static long _modalWaitStartedTimestamp;
    private static bool _windowLoopActive;
    private static long _lastFrameTimestamp;
    private static long _renderedFrames;
    private static int _frameWarnLevel;
    private static TimeSpan _watchdogThreshold = TimeSpan.FromSeconds(10);

    public static string? CurrentLogPath
    {
        get
        {
            lock (StateLock)
            {
                return _session?.LogFilePath;
            }
        }
    }

    public static string? CurrentLogsDirectory
    {
        get
        {
            lock (StateLock)
            {
                return _session?.LogsDirectory;
            }
        }
    }

    public static string CurrentStage
    {
        get
        {
            lock (StateLock)
            {
                return _currentStage;
            }
        }
    }

    public static void Initialize(
        string dataRoot,
        string? explicitLogFile = null,
        bool attachConsoleError = true,
        bool enableWatchdog = false,
        TimeSpan? stallThreshold = null)
    {
        lock (StateLock)
        {
            ShutdownLocked();
            long now = Stopwatch.GetTimestamp();
            _startupTimestamp = now;
            _stageTimestamp = now;
            _currentStage = "Startup";
            _stageTransient = true;
            _stageWarnLevel = 0;
            _modalWaitReason = null;
            _modalWaitStartedTimestamp = 0;
            _windowLoopActive = false;
            _lastFrameTimestamp = now;
            _renderedFrames = 0;
            _frameWarnLevel = 0;
            _watchdogThreshold = stallThreshold is { TotalMilliseconds: > 10 } custom
                ? custom
                : TimeSpan.FromSeconds(10);

            _session = AppLogSession.Open(dataRoot, explicitLogFile);
            if (attachConsoleError && _session.LogFilePath is not null)
            {
                var currentErr = Console.Error;
                if (currentErr is TeeTextWriter existingTee)
                {
                    currentErr = existingTee.Inner;
                }

                var captured = _session;
                _stderrTee = new TeeTextWriter(currentErr, captured.WriteStderrLine);
                Console.SetError(_stderrTee);
            }

            if (enableWatchdog && _session.LogFilePath is not null)
            {
                int periodMs = (int)Math.Clamp(_watchdogThreshold.TotalMilliseconds / 3.0, 25.0, 2000.0);
                _watchdogTimer = new Timer(OnWatchdogTick, null, periodMs, periodMs);
            }
        }
    }

    public static void SetStage(string stage, bool isSteadyState = false)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return;
        }

        AppLogSession? session;
        double deltaMs;
        double uptimeMs;
        lock (StateLock)
        {
            session = _session;
            long now = Stopwatch.GetTimestamp();
            deltaMs = _stageTimestamp > 0 ? Stopwatch.GetElapsedTime(_stageTimestamp, now).TotalMilliseconds : 0.0;
            uptimeMs = _startupTimestamp > 0 ? Stopwatch.GetElapsedTime(_startupTimestamp, now).TotalMilliseconds : 0.0;
            _currentStage = stage;
            _stageTimestamp = now;
            _stageTransient = !isSteadyState;
            _stageWarnLevel = 0;
        }

        session?.WriteLine(
            "STAGE",
            FormattableString.Invariant($"{stage} (+{deltaMs:0} ms, uptime {uptimeMs / 1000.0:0.00}s)"));
    }

    public static void BeginModalWait(string reason)
    {
        AppLogSession? session;
        lock (StateLock)
        {
            session = _session;
            _modalWaitReason = reason;
            _modalWaitStartedTimestamp = Stopwatch.GetTimestamp();
        }

        session?.WriteLine("INFO", $"Waiting on native dialog: {reason}");
    }

    public static void EndModalWait()
    {
        AppLogSession? session;
        string? reason;
        double elapsedMs = 0.0;
        lock (StateLock)
        {
            session = _session;
            reason = _modalWaitReason;
            long now = Stopwatch.GetTimestamp();
            if (_modalWaitStartedTimestamp > 0)
            {
                elapsedMs = Stopwatch.GetElapsedTime(_modalWaitStartedTimestamp, now).TotalMilliseconds;
            }

            _modalWaitReason = null;
            _modalWaitStartedTimestamp = 0;
            _lastFrameTimestamp = now;
            _frameWarnLevel = 0;
            if (_stageTimestamp > 0)
            {
                _stageTimestamp = now;
                _stageWarnLevel = 0;
            }
        }

        if (reason is not null)
        {
            session?.WriteLine(
                "INFO",
                FormattableString.Invariant($"Native dialog closed: {reason} ({elapsedMs:0} ms)"));
        }
    }

    public static void SetWindowLoopActive(bool active)
    {
        lock (StateLock)
        {
            _windowLoopActive = active;
            _lastFrameTimestamp = Stopwatch.GetTimestamp();
            _frameWarnLevel = 0;
        }
    }

    public static void RecordMainThreadFrame()
    {
        Interlocked.Exchange(ref _lastFrameTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref _renderedFrames);
        Interlocked.Exchange(ref _frameWarnLevel, 0);
    }

    private static void OnWatchdogTick(object? state)
    {
        AppLogSession? session;
        string? stageAlert = null;
        string? frameAlert = null;

        lock (StateLock)
        {
            session = _session;
            if (session is null || _modalWaitReason is not null)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            long frames = Interlocked.Read(ref _renderedFrames);
            long lastFrame = Interlocked.Read(ref _lastFrameTimestamp);
            double frameIdleSec = lastFrame > 0 ? Stopwatch.GetElapsedTime(lastFrame, now).TotalSeconds : 0.0;
            double baseSec = _watchdogThreshold.TotalSeconds;

            if (_stageTransient && _stageTimestamp > 0)
            {
                double stageSec = Stopwatch.GetElapsedTime(_stageTimestamp, now).TotalSeconds;
                int nextLevel = stageSec >= baseSec * 6.0 ? 3
                    : stageSec >= baseSec * 3.0 ? 2
                    : stageSec >= baseSec ? 1
                    : 0;
                if (nextLevel > _stageWarnLevel)
                {
                    _stageWarnLevel = nextLevel;
                    stageAlert = FormattableString.Invariant(
                        $"[WATCHDOG] STALL DETECTED: stage '{_currentStage}' still in progress after {stageSec:0.1}s (framesRendered={frames}, mainThreadIdle={frameIdleSec:0.1}s)");
                }
            }

            if (_windowLoopActive && lastFrame > 0)
            {
                int currentFrameWarn = Volatile.Read(ref _frameWarnLevel);
                int nextFrameLevel = frameIdleSec >= baseSec * 6.0 ? 3
                    : frameIdleSec >= baseSec * 3.0 ? 2
                    : frameIdleSec >= baseSec ? 1
                    : 0;
                if (nextFrameLevel > currentFrameWarn)
                {
                    Interlocked.Exchange(ref _frameWarnLevel, nextFrameLevel);
                    frameAlert = FormattableString.Invariant(
                        $"[WATCHDOG] MAIN THREAD UNRESPONSIVE: no rendered frame for {frameIdleSec:0.1}s (stage='{_currentStage}', framesRendered={frames})");
                }
            }
        }

        if (stageAlert is not null)
        {
            session.WriteLine("WARN", stageAlert);
            session.Flush();
        }

        if (frameAlert is not null)
        {
            session.WriteLine("WARN", frameAlert);
            session.Flush();
        }
    }

    public static void Info(string message) => CurrentSession?.WriteLine("INFO", message);

    public static void Warn(string message) => CurrentSession?.WriteLine("WARN", message);

    public static void Error(string message) => CurrentSession?.WriteLine("ERROR", message);

    public static void Fatal(string context, Exception exception)
    {
        if (CurrentSession is { } session)
        {
            session.WriteLine("FATAL", $"{context}: {exception}");
            session.Flush();
        }
    }

    public static void WriteExceptionOnce(string context, Exception exception) =>
        CurrentSession?.WriteOnce($"{context}:{exception.GetType().FullName}:{exception.Message}", "ERROR", $"{context}: {exception}");

    public static void WriteSdl(int category, int priority, string message)
    {
        string categoryName = category switch
        {
            0 => "APP",
            1 => "ERROR",
            2 => "ASSERT",
            3 => "SYSTEM",
            4 => "AUDIO",
            5 => "VIDEO",
            6 => "RENDER",
            7 => "INPUT",
            8 => "TEST",
            9 => "GPU",
            _ => $"CAT{category}",
        };

        string priorityName = priority switch
        {
            1 => "TRACE",
            2 => "VERBOSE",
            3 => "DEBUG",
            4 => "INFO",
            5 => "WARN",
            6 => "ERROR",
            7 => "CRITICAL",
            _ => $"PRI{priority}",
        };

        CurrentSession?.WriteLine($"SDL:{categoryName}:{priorityName}", message);
    }

    public static void WriteStartupHeader(string workspace, string dataRoot, IReadOnlyList<string> args)
    {
        if (CurrentSession is not { } session)
        {
            return;
        }

        DateTime nowLocal = DateTime.Now;
        DateTime nowUtc = nowLocal.ToUniversalTime();
        bool standalone = false;
        try
        {
            standalone = InstallLocator.IsStandaloneDistribution(workspace);
        }
        catch
        {
        }

        session.WriteLine("INFO", "=== The Sims Native Startup ===");
        session.WriteLine("INFO", FormattableString.Invariant($"Time: {nowLocal:O} (UTC: {nowUtc:O})"));
        session.WriteLine("INFO", FormattableString.Invariant($"PID: {Environment.ProcessId}"));
        session.WriteLine("INFO", FormattableString.Invariant($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}, process {RuntimeInformation.ProcessArchitecture}, cpus={Environment.ProcessorCount})"));
        session.WriteLine("INFO", $"Runtime: {RuntimeInformation.FrameworkDescription}");
        session.WriteLine("INFO", $"Executable: {Environment.ProcessPath ?? "<unknown>"}");
        session.WriteLine("INFO", $"BaseDirectory: {AppContext.BaseDirectory}");
        session.WriteLine("INFO", $"WorkingDirectory: {Environment.CurrentDirectory}");
        session.WriteLine("INFO", $"Workspace: {workspace} (standalone={standalone})");
        session.WriteLine("INFO", $"DataRoot: {dataRoot}");
        session.WriteLine("INFO", $"LogFile: {session.LogFilePath ?? "<none>"}");
        if (session.LatestLogPath is { } latestPath)
        {
            session.WriteLine("INFO", $"LatestLogMirror: {latestPath}");
        }
        session.WriteLine("INFO", args.Count == 0 ? "Args: (none)" : $"Args: [{string.Join(", ", args)}]");

        string installJsonPath = Path.Combine(dataRoot, "Settings", "install.json");
        if (File.Exists(installJsonPath))
        {
            string? savedGame = InstallLocator.SavedInstallRoot(dataRoot);
            session.WriteLine("INFO", $"InstallConfig: {installJsonPath} (validSavedPath={savedGame ?? "<invalid-or-missing-target>"})");
        }
        else
        {
            session.WriteLine("INFO", $"InstallConfig: {installJsonPath} (not created yet)");
        }

        string[] trackedEnvVars =
        [
            "SIMS_GAME_DIR",
            "SIMS_GPU_DRIVER",
            "SDL_GPU_DRIVER",
            "SDL_VIDEODRIVER",
            "SDL_AUDIODRIVER",
            "SIMS_ENGINE",
            "SIMS_DEBUG",
        ];
        var activeEnv = new List<string>();
        foreach (string envKey in trackedEnvVars)
        {
            string? val = Environment.GetEnvironmentVariable(envKey);
            if (!string.IsNullOrEmpty(val))
            {
                activeEnv.Add($"{envKey}={val}");
            }
        }
        if (activeEnv.Count > 0)
        {
            session.WriteLine("INFO", $"EnvOverrides: {string.Join("; ", activeEnv)}");
        }
    }

    public static void WriteGameDirectorySummary(string? gameDir)
    {
        if (CurrentSession is not { } session)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(gameDir))
        {
            session.WriteLine("INFO", "GameDirectory: <none>");
            return;
        }

        session.WriteLine("INFO", $"GameDirectory: {gameDir} (exists={Directory.Exists(gameDir)})");
        if (!Directory.Exists(gameDir))
        {
            return;
        }

        try
        {
            string simsExe = Path.Combine(gameDir, "Sims.exe");
            string simsExeStatus = File.Exists(simsExe)
                ? FormattableString.Invariant($"present ({new FileInfo(simsExe).Length} bytes)")
                : "absent";

            string[] probes =
            [
                "GameData",
                "UIGraphics",
                "Music",
                "SoundData",
                "UserData",
                "TemplateUserData",
                "TemplateCommunity",
                "TemplateDowntown",
                "TemplateVacation",
                "TemplateStudiotown",
                "TemplateMagicTown",
                "ExpansionShared",
                "ExpansionPack",
                "ExpansionPack2",
                "ExpansionPack3",
                "ExpansionPack4",
                "ExpansionPack5",
                "ExpansionPack6",
                "ExpansionPack7",
                "Downloads",
                "Packs",
            ];
            var found = probes.Where(sub => Directory.Exists(Path.Combine(gameDir, sub)));
            int readOnlyUserFiles = 0;
            string userDataDir = Path.Combine(gameDir, "UserData");
            if (Directory.Exists(userDataDir))
            {
                foreach (string f in Directory.EnumerateFiles(userDataDir, "*", SearchOption.TopDirectoryOnly))
                {
                    if ((File.GetAttributes(f) & FileAttributes.ReadOnly) != 0)
                    {
                        readOnlyUserFiles++;
                    }
                }
            }
            session.WriteLine(
                "INFO",
                FormattableString.Invariant(
                    $"GameContents: Sims.exe={simsExeStatus}; readOnlyTopUserDataFiles={readOnlyUserFiles}; folders=[{string.Join(", ", found)}]"));
        }
        catch (Exception ex)
        {
            session.WriteLine("WARN", $"GameContents probe failed: {ex.Message}");
        }
    }

    public static void WriteShaderDirectorySummary(string? shaderDir)
    {
        if (CurrentSession is not { } session)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(shaderDir) || !Directory.Exists(shaderDir))
        {
            session.WriteLine("WARN", $"ShaderDirectory: {shaderDir ?? "<null>"} (missing)");
            return;
        }

        try
        {
            int spv = 0;
            int dxil = 0;
            int msl = 0;
            foreach (string file in Directory.EnumerateFiles(shaderDir))
            {
                switch (Path.GetExtension(file).ToLowerInvariant())
                {
                    case ".spv":
                        spv++;
                        break;
                    case ".dxil":
                        dxil++;
                        break;
                    case ".msl":
                        msl++;
                        break;
                }
            }

            session.WriteLine("INFO", FormattableString.Invariant(
                $"ShaderDirectory: {shaderDir} (.spv={spv}, .dxil={dxil}, .msl={msl})"));
        }
        catch (Exception ex)
        {
            session.WriteLine("WARN", $"ShaderDirectory probe failed: {ex.Message}");
        }
    }

    public static void Flush()
    {
        lock (StateLock)
        {
            _stderrTee?.FlushPartialLine();
            _session?.Flush();
        }
    }

    public static void Shutdown()
    {
        lock (StateLock)
        {
            ShutdownLocked();
        }
    }

    public static void RevealLogInFileManager(string? logFilePath, string? logsDirectory)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!string.IsNullOrEmpty(logFilePath) && File.Exists(logFilePath))
                {
                    _ = Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{logFilePath}\"",
                        UseShellExecute = true,
                    });
                    return;
                }

                if (!string.IsNullOrEmpty(logsDirectory) && Directory.Exists(logsDirectory))
                {
                    _ = Process.Start(new ProcessStartInfo
                    {
                        FileName = logsDirectory,
                        UseShellExecute = true,
                    });
                }

                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                if (!string.IsNullOrEmpty(logFilePath) && File.Exists(logFilePath))
                {
                    var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                    psi.ArgumentList.Add("-R");
                    psi.ArgumentList.Add(logFilePath);
                    _ = Process.Start(psi);
                    return;
                }

                if (!string.IsNullOrEmpty(logsDirectory) && Directory.Exists(logsDirectory))
                {
                    var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                    psi.ArgumentList.Add(logsDirectory);
                    _ = Process.Start(psi);
                }

                return;
            }

            if (!string.IsNullOrEmpty(logsDirectory) && Directory.Exists(logsDirectory))
            {
                var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(logsDirectory);
                _ = Process.Start(psi);
            }
        }
        catch
        {
        }
    }

    private static AppLogSession? CurrentSession
    {
        get
        {
            lock (StateLock)
            {
                return _session;
            }
        }
    }

    private static void ShutdownLocked()
    {
        if (_watchdogTimer is { } timer)
        {
            try
            {
                timer.Dispose();
            }
            catch
            {
            }

            _watchdogTimer = null;
        }

        _windowLoopActive = false;
        _modalWaitReason = null;

        if (_stderrTee is { } tee)
        {
            try
            {
                tee.FlushPartialLine();
                if (ReferenceEquals(Console.Error, tee))
                {
                    Console.SetError(tee.Inner);
                }
            }
            catch
            {
            }

            _stderrTee = null;
        }

        if (_session is { } session)
        {
            session.Dispose();
            _session = null;
        }
    }
}
