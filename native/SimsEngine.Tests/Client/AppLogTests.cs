using SimsSDL;
using SimsSDL.Client;

namespace SimsEngine.Tests.Client;

public sealed class AppLogTests
{
    [Fact]
    public void AppLogSession_WritesSessionAndLatestLogs_AndPrunesOldFiles()
    {
        string temp = Path.Combine(Path.GetTempPath(), "sims-applog-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string logsDir = Path.Combine(temp, "logs");
            Directory.CreateDirectory(logsDir);

            DateTime baseUtc = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 5; i++)
            {
                string oldLog = Path.Combine(logsDir, $"plumbob-engine-2025010{i + 1}-000000-100.log");
                File.WriteAllText(oldLog, $"old-{i}");
                File.SetLastWriteTimeUtc(oldLog, baseUtc.AddMinutes(i));
            }

            string? sessionPath;
            string? latestPath;
            using (var session = AppLogSession.Open(temp, maxRetainedLogs: 3))
            {
                sessionPath = session.LogFilePath;
                latestPath = session.LatestLogPath;
                Assert.NotNull(sessionPath);
                Assert.NotNull(latestPath);

                session.WriteLine("INFO", "Startup ok");
                session.WriteLine("FATAL", "Boom\n  at StackFrame1()\n  at StackFrame2()");
                session.WriteOnce("dup-key", "ERROR", "First occurrence");
                session.WriteOnce("dup-key", "ERROR", "Second occurrence should be ignored");
                session.Flush();
            }

            string[] remaining = Directory.GetFiles(logsDir, "plumbob-engine-*.log");
            Assert.Equal(3, remaining.Length);
            Assert.Contains(sessionPath!, remaining);

            string sessionContent = File.ReadAllText(sessionPath!);
            string latestContent = File.ReadAllText(latestPath!);
            Assert.Equal(sessionContent, latestContent);
            Assert.Contains("[INFO] Startup ok", sessionContent, StringComparison.Ordinal);
            Assert.Contains("[FATAL] Boom", sessionContent, StringComparison.Ordinal);
            Assert.Contains("at StackFrame1()", sessionContent, StringComparison.Ordinal);
            Assert.Contains("[ERROR] First occurrence", sessionContent, StringComparison.Ordinal);
            Assert.DoesNotContain("Second occurrence", sessionContent, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    [Fact]
    public void TeeTextWriter_ForwardsOutputAndEmitsCompleteLines()
    {
        using var inner = new StringWriter();
        var captured = new List<string>();
        using (var tee = new TeeTextWriter(inner, captured.Add))
        {
            tee.Write("SimsSDL: ");
            tee.Write("part1\nSimsSDL: part2\r\ntrailing");
            tee.WriteLine("-end");
            tee.Write("unterminated");
            tee.FlushPartialLine();
        }

        Assert.Equal(["SimsSDL: part1", "SimsSDL: part2", "trailing-end", "unterminated"], captured);
        Assert.Contains("SimsSDL: part1", inner.ToString(), StringComparison.Ordinal);
        Assert.Contains("trailing-end", inner.ToString(), StringComparison.Ordinal);
        Assert.EndsWith("unterminated", inner.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Options_ParsesExplicitLogPath_AndAppLogSummariesRecordDiagnostics()
    {
        string temp = Path.Combine(Path.GetTempPath(), "sims-applog-summary-" + Guid.NewGuid().ToString("N"));
        try
        {
            string explicitLog = Path.Combine(temp, "custom", "crash.log");
            var options = new Options(["--log", explicitLog, "--no-audio", "--gpu-driver", "vulkan"]);
            Assert.Equal(Path.GetFullPath(explicitLog), options.LogFile);
            Assert.Equal("vulkan", options.GpuDriver);
            Assert.False(options.Audio);

            string fakeGame = Path.Combine(temp, "game");
            Directory.CreateDirectory(Path.Combine(fakeGame, "GameData"));
            Directory.CreateDirectory(Path.Combine(fakeGame, "UIGraphics"));
            Directory.CreateDirectory(Path.Combine(fakeGame, "ExpansionPack7"));
            File.WriteAllBytes(Path.Combine(fakeGame, "Sims.exe"), new byte[1234]);

            string fakeShaders = Path.Combine(temp, "shaders");
            Directory.CreateDirectory(fakeShaders);
            File.WriteAllBytes(Path.Combine(fakeShaders, "sprite.vert.spv"), [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(fakeShaders, "sprite.frag.spv"), [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(fakeShaders, "sprite.vert.dxil"), [1, 2]);

            try
            {
                AppLog.Initialize(temp, explicitLog, attachConsoleError: false);
                AppLog.WriteStartupHeader(temp, temp, ["--no-audio"]);
                AppLog.WriteGameDirectorySummary(fakeGame);
                AppLog.WriteShaderDirectorySummary(fakeShaders);
            }
            finally
            {
                AppLog.Shutdown();
            }

            string logText = File.ReadAllText(explicitLog);
            Assert.Contains("=== Plumbob Engine Startup ===", logText, StringComparison.Ordinal);
            Assert.Contains("Sims.exe=present (1234 bytes)", logText, StringComparison.Ordinal);
            Assert.Contains("GameData, UIGraphics, ExpansionPack7", logText, StringComparison.Ordinal);
            Assert.Contains(".spv=2, .dxil=1, .msl=0", logText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AppLogWatchdog_LogsStalledStage_AndSuppressesDuringModalDialogs()
    {
        static string ReadSharedText(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }

        string temp = Path.Combine(Path.GetTempPath(), "sims-applog-watchdog-" + Guid.NewGuid().ToString("N"));
        try
        {
            string explicitLog = Path.Combine(temp, "watchdog.log");
            try
            {
                AppLog.Initialize(
                    temp,
                    explicitLog,
                    attachConsoleError: false,
                    enableWatchdog: true,
                    stallThreshold: TimeSpan.FromMilliseconds(80));

                AppLog.SetStage("Modal test stage");
                AppLog.BeginModalWait("Test folder dialog");
                await Task.Delay(180);
                AppLog.EndModalWait();

                string afterModal = ReadSharedText(explicitLog);
                Assert.DoesNotContain("STALL DETECTED", afterModal, StringComparison.Ordinal);

                AppLog.SetStage("Daemon: loading large neighborhood");
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(3))
                {
                    if (ReadSharedText(explicitLog).Contains("[WATCHDOG] STALL DETECTED: stage 'Daemon: loading large neighborhood'", StringComparison.Ordinal))
                    {
                        break;
                    }
                    await Task.Delay(20);
                }

                AppLog.SetStage("Ready: Neighborhood N1 (mode 1)", isSteadyState: true);
            }
            finally
            {
                AppLog.Shutdown();
            }

            string logText = File.ReadAllText(explicitLog);
            Assert.Contains("[STAGE] Daemon: loading large neighborhood", logText, StringComparison.Ordinal);
            Assert.Contains("[WATCHDOG] STALL DETECTED: stage 'Daemon: loading large neighborhood'", logText, StringComparison.Ordinal);
            Assert.Contains("[STAGE] Ready: Neighborhood N1 (mode 1)", logText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    [Fact]
    public void ProfileBootstrap_ImportsReadOnlySourceFiles_AndLeavesProfileFilesWritable()
    {
        string temp = Path.Combine(Path.GetTempPath(), "sims-readonly-import-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(temp, "game");
        string profile = Path.Combine(temp, "profile");
        try
        {
            static byte[] MinimalStrIff(ushort chunkId, string label)
            {
                using var ms = new MemoryStream();
                byte[] header = new byte[64];
                byte[] sig = System.Text.Encoding.ASCII.GetBytes("IFF FILE 2.5:TYPE FOLLOWED BY SIZE JAMIE DOORNBOS & MAXIS 1");
                sig.AsSpan(0, Math.Min(60, sig.Length)).CopyTo(header);
                ms.Write(header);
                byte[] payload = [0xFD, 0xFF, 0x00, 0x00];
                byte[] chunk = new byte[76 + payload.Length];
                System.Text.Encoding.ASCII.GetBytes("STR#").CopyTo(chunk, 0);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4, 4), (uint)chunk.Length);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(8, 2), chunkId);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(10, 2), 0x10);
                System.Text.Encoding.ASCII.GetBytes(label).CopyTo(chunk, 12);
                payload.CopyTo(chunk, 76);
                ms.Write(chunk);
                return ms.ToArray();
            }

            static void WriteReadOnly(string path, byte[] data)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, data);
                File.SetAttributes(path, FileAttributes.ReadOnly);
            }

            WriteReadOnly(Path.Combine(game, "GameData", "LotLocations.iff"), MinimalStrIff(1, "base_layout"));
            WriteReadOnly(Path.Combine(game, "UserData", "Neighborhood.iff"), MinimalStrIff(1, "nbhd"));
            WriteReadOnly(Path.Combine(game, "UserData", "LotLocations.iff"), MinimalStrIff(1, "base_layout"));
            WriteReadOnly(Path.Combine(game, "UserData", "StreetNames.iff"), MinimalStrIff(2001, "short"));
            WriteReadOnly(Path.Combine(game, "UserData", "Houses", "House01.iff"), MinimalStrIff(1, "house01"));
            WriteReadOnly(Path.Combine(game, "TemplateUserData", "LotLocations.iff"), MinimalStrIff(1, "upgraded_unleashed_layout"));
            WriteReadOnly(Path.Combine(game, "TemplateCommunity", "LotLocations.iff"), MinimalStrIff(2, "downtown_route"));
            WriteReadOnly(Path.Combine(game, "TemplateCommunity", "StreetNames.iff"), [.. MinimalStrIff(2001, "longer_street_names"), 0, 0, 0, 0]);

            bool imported = global::ProfileBootstrap.EnsureNeighborhoodImported(game, profile, 1);
            Assert.True(imported);

            string[] importedFiles = Directory.GetFiles(Path.Combine(profile, "UserData"), "*", SearchOption.AllDirectories);
            Assert.NotEmpty(importedFiles);
            foreach (string file in importedFiles)
            {
                Assert.Equal((FileAttributes)0, File.GetAttributes(file) & FileAttributes.ReadOnly);
            }
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                foreach (string file in Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(temp, recursive: true);
            }
        }
    }
}
