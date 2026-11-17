using System.Globalization;
using System.Numerics;

namespace SimsSDL;

internal sealed class Options
{
    public string? Scene { get; set; }
    public string? Png { get; set; }
    public int Width { get; set; } = 1160;
    public int Height { get; set; } = 820;
    public LotZoom Zoom { get; set; } = LotZoom.Medium;

    public float? Scale { get; set; }

    public bool Lighting { get; set; } = true;

    public bool Ambient { get; set; } = true;

    public bool Audio { get; set; } = true;
    public int? House { get; set; }
    public string? Game { get; set; }
    public string? Workspace { get; set; }
    public string? DataRoot { get; set; }
    public string Profile { get; set; } = "player";
    public int Neighborhood { get; set; } = 1;

    public int? Speed { get; set; } = 1;

    public bool AnswerDialogs { get; set; }

    public string? Packs { get; set; }

    public PackTool.Command? PackCommand { get; set; }

    public string? Script { get; set; }

    public bool HelpRequested { get; set; }

    public Options()
    {
    }

    public Options(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        (string Name, bool Reserve)? guidName = null;
        string? packId = null;
        int index = 0;

        string ReadValue(string name)
        {
            if (index >= args.Length)
            {
                throw new ContentError($"{name} needs a value");
            }

            return args[index++];
        }

        while (index < args.Length)
        {
            string argument = args[index++];
            switch (argument)
            {
                case "--scene":
                    Scene = Path.GetFullPath(ReadValue(argument));
                    break;
                case "--png":
                    Png = Path.GetFullPath(ReadValue(argument));
                    break;
                case "--width":
                    if (int.TryParse(ReadValue(argument), NumberStyles.Integer, CultureInfo.InvariantCulture, out int width))
                    {
                        Width = width;
                    }
                    break;
                case "--height":
                    if (int.TryParse(ReadValue(argument), NumberStyles.Integer, CultureInfo.InvariantCulture, out int height))
                    {
                        Height = height;
                    }
                    break;
                case "--scale":
                    Scale = float.TryParse(ReadValue(argument), NumberStyles.Float, CultureInfo.InvariantCulture, out float scale)
                        ? scale
                        : null;
                    break;
                case "--no-lighting":
                    Lighting = false;
                    break;
                case "--no-ambient":
                    Ambient = false;
                    break;
                case "--no-audio":
                    Audio = false;
                    break;
                case "--house":
                    House = int.TryParse(ReadValue(argument), NumberStyles.Integer, CultureInfo.InvariantCulture, out int house)
                        ? house
                        : null;
                    break;
                case "--game":
                    Game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ReadValue(argument)));
                    break;
                case "--workspace":
                    Workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ReadValue(argument)));
                    break;
                case "--data-root":
                    DataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ReadValue(argument)));
                    break;
                case "--profile":
                    Profile = ReadValue(argument);
                    break;
                case "--neighborhood":
                    if (int.TryParse(ReadValue(argument), NumberStyles.Integer, CultureInfo.InvariantCulture, out int neighborhood))
                    {
                        Neighborhood = neighborhood;
                    }
                    break;
                case "--speed":
                    Speed = int.TryParse(ReadValue(argument), NumberStyles.Integer, CultureInfo.InvariantCulture, out int speed)
                        ? speed
                        : null;
                    break;
                case "--answer-dialogs":
                    AnswerDialogs = true;
                    break;
                case "--script":
                    Script = Path.GetFullPath(ReadValue(argument));
                    break;
                case "--packs":
                    Packs = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ReadValue(argument)));
                    break;
                case "--pack-report":
                    PackCommand = new PackTool.Command.Report();
                    break;
                case "--validate-packs":
                    PackCommand = new PackTool.Command.Validate();
                    break;
                case "--list-plugins":
                    PackCommand = new PackTool.Command.ListPlugins();
                    break;
                case "--mount":
                    PackCommand = new PackTool.Command.Mount(ReadValue(argument));
                    break;
                case "--trust-plugin":
                    PackCommand = new PackTool.Command.TrustPlugin(ReadValue(argument));
                    break;
                case "--revoke-plugin":
                    PackCommand = new PackTool.Command.RevokePlugin(ReadValue(argument));
                    break;
                case "--allocate-object-guid":
                    guidName = (ReadValue(argument), false);
                    break;
                case "--reserve-object-guid":
                    guidName = (ReadValue(argument), true);
                    break;
                case "--pack-id":
                    packId = ReadValue(argument);
                    break;
                case "--zoom":
                    Zoom = ReadValue(argument) switch
                    {
                        "far" => LotZoom.Far,
                        "medium" => LotZoom.Medium,
                        _ => LotZoom.Near,
                    };
                    break;
                case "--live":
                    break;
                case "--help":
                case "-h":
                    HelpRequested = true;
                    break;
                case var arg when arg.StartsWith("-psn_", StringComparison.Ordinal):
                    break;
                default:
                    throw new ContentError($"Unknown argument {argument}");
            }
        }

        if (HelpRequested)
        {
            return;
        }

        if (guidName is { } guidRequest)
        {
            if (packId is null)
            {
                throw new ContentError("Object allocation requires --pack-id");
            }

            PackCommand = guidRequest.Reserve
                ? new PackTool.Command.ReserveObjectGuid(packId, guidRequest.Name)
                : new PackTool.Command.AllocateObjectGuid(packId, guidRequest.Name);
        }
    }
}

internal sealed class RenderTargets : IDisposable
{
    public GPUTexture Color { get; }
    public GPUTexture Depth { get; }
    public GPUTexture? MsaaColor { get; }
    public GPUTexture? MsaaDepth { get; }

    public RenderTargets(GPUContext gpu, int width, int height)
    {
        Color = gpu.MakeTexture(
            width,
            height,
            GPUContext.ColorFormat,
            SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER);
        try
        {
            Depth = gpu.MakeTexture(
                width,
                height,
                GPUContext.DepthFormat,
                SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET | SDL_GPU_TEXTUREUSAGE_SAMPLER);
            if (gpu.PreferredSampleCount > SDL_GPU_SAMPLECOUNT_1)
            {
                MsaaColor = gpu.MakeTexture(
                    width,
                    height,
                    GPUContext.ColorFormat,
                    SDL_GPU_TEXTUREUSAGE_COLOR_TARGET,
                    sampleCount: gpu.PreferredSampleCount);
                MsaaDepth = gpu.MakeTexture(
                    width,
                    height,
                    GPUContext.DepthFormat,
                    SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET,
                    sampleCount: gpu.PreferredSampleCount);
            }
        }
        catch
        {
            MsaaDepth?.Dispose();
            MsaaColor?.Dispose();
            Color.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        MsaaDepth?.Dispose();
        MsaaColor?.Dispose();
        Color.Dispose();
        Depth.Dispose();
    }
}

internal static unsafe partial class Program
{
    [LibraryImport("SDL3")]
    private static partial void SDL_PumpEvents();

    [LibraryImport("SDL3")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SDL_SetWindowIcon(nint window, SDL_Surface* icon);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint dwProcessId);

    private sealed class FolderPickerResult
    {
        private readonly object _lock = new();
        private bool _done;
        private string? _path;

        public void Complete(string? selectedPath)
        {
            lock (_lock)
            {
                _path = selectedPath;
                _done = true;
            }
        }

        public (bool Done, string? Path) Snapshot()
        {
            lock (_lock)
            {
                return (_done, _path);
            }
        }
    }

    private const string UsageText = """
        SimsSDL: the cross-platform front end on SDL3 and SDL_GPU. It plays the game through the simulation host from the
        original neighborhood screen, opens one lot directly with --house, or shows an exported lot scene.

            SimsSDL --scene <dir/scene.json> [--png out.png] [--width 1280] [--height 960] [--zoom near|medium|far]
                    [--scale 2] [--no-lighting] [--no-ambient]
            SimsSDL [--house <1-100>] [--game <game dir>] [--workspace <repo>] [--data-root <dir>] [--profile player]
                    [--neighborhood 1] [--speed 0|1|3|10] [--answer-dialogs] [--no-audio] [--packs <dir>] [--script <file>]
            SimsSDL [--packs <dir>] --pack-report | --validate-packs | --list-plugins | --trust-plugin <pack> | --revoke-plugin <pack>
                    | --allocate-object-guid <name> --pack-id <pack> | --reserve-object-guid <name> --pack-id <pack> | --mount <dir>

        With --png it renders one frame offscreen and writes it out; otherwise it opens a window where the mouse wheel
        zooms, the arrow keys or a right drag pan, and a left click prints the object and wall under the cursor. A live lot
        plays with the original control panel, pie menus, dialogs and buy and build tools and the macOS client's keys
        (P pause, space next Sim, 1-3 speeds, W walls, B buy, F build, R rotate, [ ] camera, S save, E pack panels);
        --answer-dialogs answers each game dialog with its first choice. --script drives the window from a file of clicks, keys
        and screenshots (`InputScript`) and exits with status 2 when a step fails.
        """;

    public static int Main(string[] args)
    {
        if (OperatingSystem.IsWindows() && args.Length > 0)
        {
            _ = AttachConsole(unchecked((uint)-1));
        }

        MainThreadDispatcher.InitializeMainThread();

        try
        {
            var options = new Options(args);
            if (options.HelpRequested)
            {
                Console.WriteLine(UsageText);
                return 0;
            }

            string workspace = options.Workspace ?? DefaultWorkspace();
            string dataRoot = options.DataRoot ?? InstallLocator.DefaultDataRoot(workspace);
            options.Workspace = workspace;
            options.DataRoot = dataRoot;

            if (options.PackCommand is { } command)
            {
                string packGame = InstallLocator.ResolveGameDirectory(options.Game, workspace, dataRoot)
                    ?? Path.Combine(workspace, "runtime", "game");
                if (command is PackTool.Command.Validate)
                {
                    (byte[] report, bool valid) = PackTool.Validate(packGame, options.Packs);
                    WriteStdoutLine(report);
                    return valid ? 0 : 1;
                }

                GameContent content = PackTool.LoadContent(packGame, options.Packs);
                WriteStdoutLine(PackTool.Run(command, content, dataRoot));
                return 0;
            }

            string sweepRoot = dataRoot;
            _ = Task.Run(() =>
            {
                foreach (string session in SessionSweeper.Sweep(sweepRoot))
                {
                    Log($"reclaimed stale content mount {Path.GetFileName(Path.TrimEndingDirectorySeparator(session))}");
                }
            });

            if (!SDL_Init(SDL_INIT_VIDEO))
            {
                throw new SDLError("SDL_Init");
            }

            try
            {
                bool needsGameInstall = options.House is not null || (options.Scene is null && options.Png is null);
                if (needsGameInstall && options.Game is null)
                {
                    if (InstallLocator.ResolveGameDirectory(explicitPath: null, workspace: workspace, dataRoot: dataRoot) is { } discovered)
                    {
                        options.Game = discovered;
                    }
                    else if (options.Script is not null)
                    {
                        throw new ContentError("The Sims 1 game folder could not be found. Pass --game <path>.");
                    }
                    else
                    {
                        if (ChooseGameDirectoryWithDialog(dataRoot) is not { } picked)
                        {
                            return 0;
                        }

                        options.Game = picked;
                    }
                }

                using var gpu = new GPUContext();
                using var renderer = new LotRenderer(gpu)
                {
                    UIScale = options.Scale ?? 2f,
                    Zoom = options.Zoom,
                    LightingEnabled = options.Lighting,
                    AmbientEnabled = options.Ambient,
                };

                ApplyEnvironmentTuning(renderer, options);

                if (options.Scene is { } scene)
                {
                    renderer.LoadScene(scene);
                    ApplyScenePluginEnvironment(renderer, scene);
                }

                if (options.House is not null || (options.Scene is null && options.Png is null))
                {
                    var live = new LiveController(options, options.House, renderer);
                    bool stopped = false;
                    try
                    {
                        live.LoadPlayOptions();
                        InputScript? script = options.Script is { } scriptPath ? new InputScript(scriptPath) : null;
                        if (script is not null)
                        {
                            live.ScriptUISounds = [];
                        }

                        RunWindow(options, renderer, live, script);
                        if (script?.Failed is { } failed)
                        {
                            live.Stop();
                            stopped = true;
                            Log($"script failed: {failed}");
                            return 2;
                        }
                    }
                    finally
                    {
                        if (!stopped)
                        {
                            live.Stop();
                        }
                    }
                }
                else if (options.Png is { } pngPath)
                {
                    RenderPNG(options, renderer, pngPath);
                }
                else
                {
                    RunWindow(options, renderer);
                }

                return 0;
            }
            finally
            {
                SDL_Quit();
            }
        }
        catch (Exception ex)
        {
            Log(ex.Message);
            return 1;
        }
    }

    private static void RenderPNG(Options options, LotRenderer renderer, string outputPath)
    {
        using var targets = new RenderTargets(renderer.GPU, options.Width, options.Height);
        nint command = SDL_AcquireGPUCommandBuffer(renderer.GPU.Device);
        if (command == nint.Zero)
        {
            throw new SDLError("SDL_AcquireGPUCommandBuffer");
        }

        renderer.Encode(command, targets.Color, targets.Depth, targets.MsaaColor, targets.MsaaDepth);
        if (!SDL_SubmitGPUCommandBuffer(command))
        {
            throw new SDLError("SDL_SubmitGPUCommandBuffer");
        }

        byte[] pixels = renderer.GPU.ReadPixels(targets.Color);
        byte[] png = PNGEncoder.Encode(pixels, options.Width, options.Height);
        File.WriteAllBytes(outputPath, png);
    }

    private static string DefaultWorkspace() => InstallLocator.DefaultWorkspace();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FolderDialogCallback(nint userdata, nint* filelist, int filter)
    {
        if (userdata == nint.Zero)
        {
            return;
        }

        GCHandle handle = GCHandle.FromIntPtr(userdata);
        try
        {
            if (handle.Target is FolderPickerResult box)
            {
                string? selected = filelist != null && filelist[0] != nint.Zero
                    ? Marshal.PtrToStringUTF8(filelist[0])
                    : null;
                box.Complete(selected);
            }
        }
        catch
        {
        }
        finally
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }
    }

    private static string? ChooseGameDirectoryWithDialog(string dataRoot)
    {
        while (true)
        {
            _ = SDL_ShowSimpleMessageBox(
                SDL_MESSAGEBOX_INFORMATION,
                "Select The Sims 1 Folder",
                "Please select your installed The Sims 1 game folder (containing GameData and UIGraphics).",
                nint.Zero);

            var box = new FolderPickerResult();
            GCHandle handle = GCHandle.Alloc(box, GCHandleType.Normal);
            bool handedToCallback = false;
            try
            {
                SDL_ShowOpenFolderDialog(
                    &FolderDialogCallback,
                    GCHandle.ToIntPtr(handle),
                    nint.Zero,
                    default_location: null,
                    allow_many: false);
                handedToCallback = true;
            }
            finally
            {
                if (!handedToCallback && handle.IsAllocated)
                {
                    handle.Free();
                }
            }

            string? pickedPath;
            while (true)
            {
                (bool done, pickedPath) = box.Snapshot();
                if (done)
                {
                    break;
                }

                SDL_PumpEvents();
                while (SDL_PollEvent(out _))
                {
                }

                MainThreadDispatcher.Drain();
                SDL_Delay(16u);
            }

            if (string.IsNullOrEmpty(pickedPath))
            {
                return null;
            }

            string chosen = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pickedPath));
            if (InstallLocator.IsValidGameDirectory(chosen))
            {
                try
                {
                    InstallLocator.SaveInstallRoot(chosen, dataRoot);
                }
                catch
                {
                }

                return chosen;
            }

            _ = SDL_ShowSimpleMessageBox(
                SDL_MESSAGEBOX_ERROR,
                "Invalid Game Folder",
                "The selected folder does not contain GameData and UIGraphics. Please try again or cancel to quit.",
                nint.Zero);
        }
    }

    private static void RunWindow(Options options, LotRenderer renderer, LiveController? live = null, InputScript? script = null)
    {
        nint window = SDL_CreateWindow(
            "The Sims",
            options.Width,
            options.Height,
            SDL_WINDOW_RESIZABLE | SDL_WINDOW_HIGH_PIXEL_DENSITY);
        if (window == nint.Zero)
        {
            throw new SDLError("SDL_CreateWindow");
        }

        RenderTargets? targets = null;
        var framesInFlight = new List<nint>();
        nint device = renderer.GPU.Device;
        bool claimed = false;

        try
        {
            TryLoadWindowIcon(window, options.Workspace ?? DefaultWorkspace());

            if (!SDL_ClaimWindowForGPUDevice(device, window))
            {
                throw new SDLError("SDL_ClaimWindowForGPUDevice");
            }
            claimed = true;

            if (SDL_WindowSupportsGPUSwapchainComposition(device, window, SDL_GPU_SWAPCHAINCOMPOSITION_SDR_LINEAR))
            {
                _ = SDL_SetGPUSwapchainParameters(
                    device,
                    window,
                    SDL_GPU_SWAPCHAINCOMPOSITION_SDR_LINEAR,
                    SDL_GPU_PRESENTMODE_VSYNC);
            }

            bool dragging = false;
            bool middleDragging = false;
            bool leftDown = false;
            int debugFrame = 0;
            bool running = true;
            string? pendingShot = null;
            var profiler = new FrameProfiler();
            OriginalCursors? cursors = live is not null ? new OriginalCursors(live.Content) : null;
            LotZoom[] sortedZoomLevels = [.. Enum.GetValues<LotZoom>().OrderBy(static z => (int)z)];

            if (live is not null)
            {
                _ = SDL_StartTextInput(window);
                live.WarpPointer = point =>
                {
                    float density = SDL_GetWindowPixelDensity(window);
                    if (density <= 0f)
                    {
                        density = 1f;
                    }

                    SDL_WarpMouseInWindow(window, (float)point.X / density, (float)point.Y / density);
                };
            }

            CGSize CanvasSize() => new(targets?.Color.Width ?? 1, targets?.Color.Height ?? 1);

            static LiveController.Key? MapLiveKey(uint key) => key switch
            {
                SDLK_ESCAPE => new LiveController.Key.Escape(),
                SDLK_RETURN or SDLK_KP_ENTER => new LiveController.Key.Enter(),
                SDLK_BACKSPACE => new LiveController.Key.Backspace(),
                SDLK_DELETE => new LiveController.Key.Delete(),
                SDLK_TAB => new LiveController.Key.Tab(),
                SDLK_LEFT => new LiveController.Key.Left(),
                SDLK_RIGHT => new LiveController.Key.Right(),
                SDLK_UP => new LiveController.Key.Up(),
                SDLK_DOWN => new LiveController.Key.Down(),
                SDLK_PAGEUP => new LiveController.Key.PageUp(),
                SDLK_PAGEDOWN => new LiveController.Key.PageDown(),
                SDLK_HOME => new LiveController.Key.Home(),
                SDLK_END => new LiveController.Key.End(),
                SDLK_F1 => new LiveController.Key.Function(1),
                SDLK_F2 => new LiveController.Key.Function(2),
                SDLK_F3 => new LiveController.Key.Function(3),
                SDLK_F4 => new LiveController.Key.Function(4),
                SDLK_F5 => new LiveController.Key.Function(5),
                SDLK_KP_0 => new LiveController.Key.Character("0"),
                SDLK_KP_1 => new LiveController.Key.Character("1"),
                SDLK_KP_2 => new LiveController.Key.Character("2"),
                SDLK_KP_3 => new LiveController.Key.Character("3"),
                SDLK_KP_PLUS => new LiveController.Key.Character("+"),
                SDLK_KP_MINUS => new LiveController.Key.Character("-"),
                SDLK_SPACE => new LiveController.Key.Character(" "),
                >= 0x21u and < 0x7Fu => new LiveController.Key.Character(char.ToLowerInvariant((char)key).ToString()),
                _ => null,
            };

            bool windowCloseRequested = false;
            long lastLoopStamp = System.Diagnostics.Stopwatch.GetTimestamp();
            while (running && live?.QuitRequested != true)
            {
                profiler.Begin();
                renderer.BeginFrame();
                nint command = SDL_AcquireGPUCommandBuffer(device);
                if (command == nint.Zero)
                {
                    throw new SDLError("SDL_AcquireGPUCommandBuffer");
                }

                bool commandSubmitted = false;
                try
                {
                    if (!SDL_WaitAndAcquireGPUSwapchainTexture(command, window, out nint swapchain, out uint width, out uint height))
                    {
                        throw new SDLError("SDL_WaitAndAcquireGPUSwapchainTexture");
                    }

                    long loopStamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    double dtSeconds = Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(lastLoopStamp, loopStamp).TotalSeconds, 0.0, 0.1);
                    lastLoopStamp = loopStamp;

                    profiler.Mark("swapchain");
                    if (swapchain == nint.Zero || width == 0 || height == 0)
                    {
                        commandSubmitted = true;
                        _ = SDL_SubmitGPUCommandBuffer(command);
                        MainThreadDispatcher.Drain();
                        continue;
                    }

                    if (options.Scale is null)
                    {
                        renderer.UIScale = LiveUI.AutomaticUIScale((int)width, (int)height, SDL_GetWindowPixelDensity(window));
                    }

                    if (targets is null || targets.Color.Width != (int)width || targets.Color.Height != (int)height)
                    {
                        targets?.Dispose();
                        targets = null;
                        targets = new RenderTargets(renderer.GPU, (int)width, (int)height);
                    }

                    CGPoint? pendingMotionLocation = null;
                    Vector2 pendingPanDelta = Vector2.Zero;

                    void FlushPendingMotion()
                    {
                        if (pendingMotionLocation is { } location)
                        {
                            pendingMotionLocation = null;
                            Vector2 delta = pendingPanDelta;
                            pendingPanDelta = Vector2.Zero;
                            if (live is not null)
                            {
                                if (middleDragging)
                                {
                                    if (delta != Vector2.Zero)
                                    {
                                        live.Pan(delta);
                                    }
                                }
                                else if (leftDown && live.PrimaryGesture)
                                {
                                    live.Drag(location);
                                }
                                else if (live.RightButtonHeld)
                                {
                                    live.RightMove(location);
                                }
                                else
                                {
                                    live.RightMove(location);
                                    live.Hover(location, CanvasSize());
                                }
                            }
                            else if (dragging || middleDragging)
                            {
                                renderer.Pan += delta;
                            }
                        }
                        else if (pendingPanDelta != Vector2.Zero)
                        {
                            if (live is not null && middleDragging)
                            {
                                live.Pan(pendingPanDelta);
                            }
                            else if (dragging || middleDragging)
                            {
                                renderer.Pan += pendingPanDelta;
                            }

                            pendingPanDelta = Vector2.Zero;
                        }
                    }

                    while (SDL_PollEvent(out SDL_Event ev))
                    {
                        float density = SDL_GetWindowPixelDensity(window);
                        switch (ev.type)
                        {
                            case SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                                if (live is not null)
                                {
                                    windowCloseRequested = true;
                                    live.ConfirmQuit();
                                }
                                else
                                {
                                    running = false;
                                }
                                break;

                            case SDL_EVENT_QUIT:
                                if (windowCloseRequested)
                                {
                                    windowCloseRequested = false;
                                }
                                else
                                {
                                    running = false;
                                }
                                break;

                            case SDL_EVENT_MOUSE_WHEEL:
                                if (live is not null)
                                {
                                    live.Wheel(ev.wheel_y);
                                    break;
                                }

                                int currentZoomIndex = Array.IndexOf(sortedZoomLevels, renderer.Zoom);
                                if (currentZoomIndex >= 0)
                                {
                                    int deltaZoom = ev.wheel_y > 0 ? 1 : ev.wheel_y < 0 ? -1 : 0;
                                    int nextZoomIndex = Math.Clamp(currentZoomIndex + deltaZoom, 0, sortedZoomLevels.Length - 1);
                                    renderer.SetZoom(sortedZoomLevels[nextZoomIndex]);
                                }
                                break;

                            case SDL_EVENT_KEY_DOWN:
                            {
                                ushort mods = SDL_GetModState();
                                if ((mods & (SDL_KMOD_GUI | SDL_KMOD_CTRL)) != 0)
                                {
                                    if (ev.key_key is 0x2Du or SDLK_KP_MINUS)
                                    {
                                        float nextScale = MathF.Round(Math.Clamp(renderer.UIScale - 0.15f, 0.75f, 3.5f), 2);
                                        options.Scale = nextScale;
                                        renderer.UIScale = nextScale;
                                        break;
                                    }
                                    if (ev.key_key is 0x3Du or 0x2Bu or SDLK_KP_PLUS)
                                    {
                                        float nextScale = MathF.Round(Math.Clamp(renderer.UIScale + 0.15f, 0.75f, 3.5f), 2);
                                        options.Scale = nextScale;
                                        renderer.UIScale = nextScale;
                                        break;
                                    }
                                    if (ev.key_key is 0x30u or SDLK_KP_0)
                                    {
                                        options.Scale = null;
                                        renderer.UIScale = LiveUI.AutomaticUIScale((int)width, (int)height, density);
                                        break;
                                    }
                                }
                                if (live is not null)
                                {
                                    live.DeleteModifier = (mods & (SDL_KMOD_CTRL | SDL_KMOD_ALT)) != 0;
                                    if (MapLiveKey(ev.key_key) is { } key && live.HandleKeyDown(key))
                                    {
                                        break;
                                    }

                                    const float livePanStep = 64f;
                                    bool shift = (mods & SDL_KMOD_SHIFT) != 0;
                                    bool continuousArrow = script is null && !shift && live.AllowsContinuousArrowPan;
                                    switch (ev.key_key)
                                    {
                                        case SDLK_LEFT:
                                            if (shift)
                                            {
                                                if (live.Ui.ShowsLot)
                                                {
                                                    live.RotateCamera(-1);
                                                }
                                            }
                                            else if (!continuousArrow)
                                            {
                                                live.Pan(new Vector2(livePanStep, 0f));
                                            }
                                            break;
                                        case SDLK_RIGHT:
                                            if (shift)
                                            {
                                                if (live.Ui.ShowsLot)
                                                {
                                                    live.RotateCamera(1);
                                                }
                                            }
                                            else if (!continuousArrow)
                                            {
                                                live.Pan(new Vector2(-livePanStep, 0f));
                                            }
                                            break;
                                        case SDLK_UP:
                                            if (!continuousArrow)
                                            {
                                                live.Pan(new Vector2(0f, livePanStep));
                                            }
                                            break;
                                        case SDLK_DOWN:
                                            if (!continuousArrow)
                                            {
                                                live.Pan(new Vector2(0f, -livePanStep));
                                            }
                                            break;
                                    }
                                    break;
                                }

                                if (script is not null)
                                {
                                    const float scenePanStep = 64f;
                                    switch (ev.key_key)
                                    {
                                        case SDLK_LEFT:
                                            renderer.Pan += new Vector2(scenePanStep, 0f);
                                            break;
                                        case SDLK_RIGHT:
                                            renderer.Pan += new Vector2(-scenePanStep, 0f);
                                            break;
                                        case SDLK_UP:
                                            renderer.Pan += new Vector2(0f, scenePanStep);
                                            break;
                                        case SDLK_DOWN:
                                            renderer.Pan += new Vector2(0f, -scenePanStep);
                                            break;
                                        case SDLK_ESCAPE:
                                            running = false;
                                            break;
                                    }
                                }
                                else if (ev.key_key == SDLK_ESCAPE)
                                {
                                    running = false;
                                }
                                break;
                            }

                            case SDL_EVENT_TEXT_INPUT:
                                if (live is not null && ev.text_text != nint.Zero)
                                {
                                    if (Marshal.PtrToStringUTF8(ev.text_text) is { } text)
                                    {
                                        live.Text(text);
                                    }
                                }
                                break;

                            case SDL_EVENT_MOUSE_BUTTON_DOWN:
                                FlushPendingMotion();
                                Vector2 downPoint = new Vector2(ev.button_x, ev.button_y) * density;
                                if (ev.button_button == SDL_BUTTON_MIDDLE)
                                {
                                    middleDragging = true;
                                }

                                if (ev.button_button == SDL_BUTTON_RIGHT)
                                {
                                    var location = new CGPoint(downPoint.X, downPoint.Y);
                                    if (live is not null)
                                    {
                                        if (!live.ConsumesTutorialRightClick(location))
                                        {
                                            live.RightDown(location);
                                        }
                                    }
                                    else
                                    {
                                        dragging = true;
                                    }
                                }

                                if (ev.button_button == SDL_BUTTON_LEFT)
                                {
                                    if (live is not null)
                                    {
                                        ushort mods = SDL_GetModState();
                                        leftDown = true;
                                        live.Press(
                                            new CGPoint(downPoint.X, downPoint.Y),
                                            CanvasSize(),
                                            shift: (mods & SDL_KMOD_SHIFT) != 0,
                                            alt: (mods & SDL_KMOD_ALT) != 0,
                                            ctrl: (mods & SDL_KMOD_CTRL) != 0);
                                    }
                                    else if (targets is not null)
                                    {
                                        var canvas = new Vector2(targets.Color.Width, targets.Color.Height);
                                        int? obj = renderer.ObjectAt(downPoint, canvas);
                                        LotScene.WallSide? wall = renderer.WallAt(downPoint, canvas);
                                        string objText = obj is { } objectId
                                            ? objectId.ToString(CultureInfo.InvariantCulture)
                                            : "none";
                                        string wallText = wall is { } wallSide
                                            ? wallSide.ToString()
                                            : "none";
                                        Console.WriteLine(FormattableString.Invariant(
                                            $"click {downPoint}: object {objText}, wall {wallText}"));
                                    }
                                }
                                break;

                            case SDL_EVENT_MOUSE_BUTTON_UP:
                                FlushPendingMotion();
                                if (ev.button_button == SDL_BUTTON_MIDDLE)
                                {
                                    middleDragging = false;
                                    var location = new CGPoint(ev.button_x * density, ev.button_y * density);
                                    live?.Hover(location, CanvasSize());
                                }

                                if (ev.button_button == SDL_BUTTON_RIGHT)
                                {
                                    dragging = false;
                                    var location = new CGPoint(ev.button_x * density, ev.button_y * density);
                                    live?.RightUp(location);
                                    live?.Hover(location, CanvasSize());
                                }

                                if (ev.button_button == SDL_BUTTON_LEFT)
                                {
                                    leftDown = false;
                                    Vector2 upPoint = new Vector2(ev.button_x, ev.button_y) * density;
                                    live?.Release(new CGPoint(upPoint.X, upPoint.Y));
                                }
                                break;

                            case SDL_EVENT_MOUSE_MOTION:
                                Vector2 motionPoint = new Vector2(ev.motion_x, ev.motion_y) * density;
                                pendingPanDelta += new Vector2(ev.motion_xrel, ev.motion_yrel) * density;
                                pendingMotionLocation = new CGPoint(motionPoint.X, motionPoint.Y);
                                break;

                            case SDL_EVENT_WINDOW_MOUSE_LEAVE:
                                FlushPendingMotion();
                                live?.Leave();
                                break;

                            case SDL_EVENT_WINDOW_FOCUS_LOST:
                                middleDragging = false;
                                live?.SetWindowActive(false);
                                break;

                            case SDL_EVENT_WINDOW_FOCUS_GAINED:
                                live?.SetWindowActive(true);
                                break;
                        }
                    }

                    FlushPendingMotion();

                    if (script is null && dtSeconds > 0.0 && (SDL_GetModState() & SDL_KMOD_SHIFT) == 0 && (live is null || live.AllowsContinuousArrowPan))
                    {
                        byte* keyState = SDL_GetKeyboardState(out int numKeys);
                        if (keyState != null)
                        {
                            float kx = 0f;
                            float ky = 0f;
                            if (SDL_SCANCODE_LEFT < numKeys && keyState[SDL_SCANCODE_LEFT] != 0) kx += 1f;
                            if (SDL_SCANCODE_RIGHT < numKeys && keyState[SDL_SCANCODE_RIGHT] != 0) kx -= 1f;
                            if (SDL_SCANCODE_UP < numKeys && keyState[SDL_SCANCODE_UP] != 0) ky += 1f;
                            if (SDL_SCANCODE_DOWN < numKeys && keyState[SDL_SCANCODE_DOWN] != 0) ky -= 1f;
                            if (kx != 0f || ky != 0f)
                            {
                                var dir = new Vector2(kx, ky);
                                if (dir.LengthSquared() > 1f)
                                {
                                    dir = Vector2.Normalize(dir);
                                }

                                Vector2 panDelta = dir * (520f * MathF.Max(1f, renderer.UIScale) * (float)Math.Clamp(dtSeconds, 0.0, 0.05));
                                if (live is not null)
                                {
                                    live.Pan(panDelta);
                                }
                                else
                                {
                                    renderer.Pan += panDelta;
                                }
                            }
                        }
                    }

                    bool wasZoomInterpolating = renderer.IsZoomInterpolating;
                    renderer.StepSmoothZoom(dtSeconds, instant: script is not null || options.Png is not null);
                    if (wasZoomInterpolating && !renderer.IsZoomInterpolating)
                    {
                        live?.OnSmoothZoomSettled();
                    }

                    // Drains simulation bridge events immediately after swapchain wakeup so input drags and
                    // animation frames align with the current display refresh.
                    profiler.Mark("events");
                    MainThreadDispatcher.Drain();

                    if (live is not null && cursors is not null)
                    {
                        live.DeleteModifier = (SDL_GetModState() & (SDL_KMOD_CTRL | SDL_KMOD_ALT)) != 0;
                        cursors.Show((dragging || middleDragging)
                            ? "Pan"
                            : live.RightScrollActive
                                ? live.RightScrollCursor
                                : live.CursorName);
                    }

                    profiler.Mark("host");
                    try
                    {
                        renderer.ApplyPendingAnimation(command);
                    }
                    catch (Exception ex)
                    {
                        Log(ex.Message);
                    }

                    profiler.Mark("animation");
                    if (targets is null)
                    {
                        commandSubmitted = true;
                        _ = SDL_SubmitGPUCommandBuffer(command);
                        continue;
                    }

                    if (live is not null)
                    {
                        live.ScriptPixelDensity = SDL_GetWindowPixelDensity(window);
                        debugFrame += 1;
                        if (debugFrame % 120 == 0 && Environment.GetEnvironmentVariable("SIMS_DEBUG") is not null)
                        {
                            Log(FormattableString.Invariant(
                                $"frame {debugFrame}: pan {renderer.Pan} zoom {renderer.Zoom} scene {renderer.SceneRoot ?? "none"}"));
                        }

                        live.Tick(new CGSize(width, height));
                        if (script is not null && script.Step(live) is InputScript.Action.Shot shot)
                        {
                            pendingShot = shot.Path;
                        }

                        live.PrepareFrame();
                        live.UpdateAudio();
                        profiler.Mark("tick");
                        live.Ui.RoofsVisible = renderer.RoofsVisible;
                        live.Ui.ViewLevel = renderer.ViewLevel;

                        try
                        {
                            renderer.Overlay = live.Ui.Draws(new Vector2(width, height), renderer.UIScale, renderer.Speed);
                            renderer.PreviewPlacement = live.Ui.PreviewPlacement is { } preview
                                ? (preview.Index, preview.Rects as Dictionary<int, Vector4> ?? new Dictionary<int, Vector4>(preview.Rects))
                                : null;
                            renderer.GPU.BatchingUploads(command, renderer.UploadOverlay);
                        }
                        catch (Exception ex)
                        {
                            Log(ex.Message);
                        }

                        profiler.Mark("ui");
                    }

                    RenderTargets? photoTarget = null;
                    try
                    {
                        if (live?.PrepareFrameCapture() == true)
                        {
                            photoTarget = new RenderTargets(renderer.GPU, (int)width, (int)height);
                            var overlay = renderer.Overlay;
                            renderer.Overlay = [];
                            try
                            {
                                renderer.Encode(command, photoTarget.Color, photoTarget.Depth, photoTarget.MsaaColor, photoTarget.MsaaDepth);
                            }
                            finally
                            {
                                renderer.Overlay = overlay;
                            }
                        }

                        renderer.Encode(command, targets.Color, targets.Depth, targets.MsaaColor, targets.MsaaDepth);
                        profiler.Mark("encode");

                        var blit = new SDL_GPUBlitInfo
                        {
                            source = new SDL_GPUBlitRegion
                            {
                                texture = targets.Color.Handle,
                                w = width,
                                h = height,
                            },
                            destination = new SDL_GPUBlitRegion
                            {
                                texture = swapchain,
                                w = width,
                                h = height,
                            },
                            load_op = SDL_GPU_LOADOP_DONT_CARE,
                            filter = SDL_GPU_FILTER_NEAREST,
                        };
                        SDL_BlitGPUTexture(command, in blit);

                        commandSubmitted = true;
                        nint fence = SDL_SubmitGPUCommandBufferAndAcquireFence(command);
                        if (fence == nint.Zero)
                        {
                            throw new SDLError("SDL_SubmitGPUCommandBufferAndAcquireFence");
                        }

                        // Limits queued frames to two to prevent command buffer descriptor-set caches from
                        // growing unbounded on slow or software rasterizers.
                        framesInFlight.Add(fence);
                        if (framesInFlight.Count > 2)
                        {
                            nint oldest = framesInFlight[0];
                            framesInFlight.RemoveAt(0);
                            try
                            {
                                _ = SDL_WaitForGPUFences(device, true, &oldest, 1);
                            }
                            finally
                            {
                                SDL_ReleaseGPUFence(device, oldest);
                            }
                        }

                        if (photoTarget is not null)
                        {
                            live?.CapturePhoto(
                                renderer.GPU.ReadPixels(photoTarget.Color),
                                photoTarget.Color.Width,
                                photoTarget.Color.Height);
                        }

                        if (pendingShot is { } shotPath)
                        {
                            pendingShot = null;
                            try
                            {
                                byte[] rgba = renderer.GPU.ReadPixels(targets.Color);
                                byte[] png = PNGEncoder.Encode(rgba, targets.Color.Width, targets.Color.Height);
                                File.WriteAllBytes(shotPath, png);
                                Log($"script: wrote {shotPath}");
                            }
                            catch (Exception ex)
                            {
                                Log($"screenshot {shotPath}: {ex.Message}");
                            }
                        }
                    }
                    finally
                    {
                        photoTarget?.Dispose();
                    }

                    profiler.Mark("gpu wait");
                    profiler.EndFrame();
                }
                catch
                {
                    if (!commandSubmitted)
                    {
                        _ = SDL_SubmitGPUCommandBuffer(command);
                    }

                    throw;
                }
            }
        }
        finally
        {
            if (live is not null)
            {
                live.WarpPointer = null;
            }

            if (framesInFlight.Count > 0)
            {
                Span<nint> fences = CollectionsMarshal.AsSpan(framesInFlight);
                fixed (nint* fencesPtr = fences)
                {
                    _ = SDL_WaitForGPUFences(device, true, fencesPtr, (uint)fences.Length);
                }

                foreach (nint fence in framesInFlight)
                {
                    SDL_ReleaseGPUFence(device, fence);
                }
            }

            targets?.Dispose();

            if (claimed)
            {
                SDL_ReleaseWindowFromGPUDevice(device, window);
            }

            SDL_DestroyWindow(window);
        }
    }

    private static void ApplyEnvironmentTuning(LotRenderer renderer, Options options)
    {
        if (ParseFloatList(Environment.GetEnvironmentVariable("SIMS_SDL_AMBIENT")) is { Count: >= 5 } ambientValues)
        {
            renderer.Ambient.Radius = ambientValues[0];
            renderer.Ambient.Occlusion = ambientValues[1];
            renderer.Ambient.Shadow = ambientValues[2];
            renderer.Ambient.Thickness = ambientValues[3];
            renderer.Ambient.Reach = ambientValues[4];
            if (ambientValues.Count >= 8)
            {
                renderer.Ambient.Sun = new Vector3(ambientValues[5], ambientValues[6], ambientValues[7]);
            }

            if (ambientValues.Count >= 9)
            {
                renderer.Ambient.GroundOffset = ambientValues[8];
            }
        }

        renderer.AmbientDebug = int.TryParse(
            Environment.GetEnvironmentVariable("SIMS_SDL_AMBIENT_DEBUG"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int ambientDebug)
            ? ambientDebug
            : 0;

        // Freezes cloud drift at zero during still renders to ensure deterministic visual output across
        // captured frames.
        if (options.Png is not null)
        {
            renderer.Ambient.CloudTime = 0f;
        }

        if (ParseFloatList(Environment.GetEnvironmentVariable("SIMS_SDL_CLOUDS")) is { Count: >= 2 } cloudValues)
        {
            renderer.Ambient.CloudCover = cloudValues[0];
            renderer.Ambient.CloudShade = cloudValues[1];
            if (cloudValues.Count >= 3)
            {
                renderer.Ambient.CloudTime = cloudValues[2];
            }
        }
    }

    private static void ApplyScenePluginEnvironment(LotRenderer renderer, string scenePath)
    {
        string? envDescriptor = Environment.GetEnvironmentVariable("SIMS_SDL_ENVIRONMENT");
        string descriptor = !string.IsNullOrEmpty(envDescriptor)
            ? Path.GetFullPath(envDescriptor)
            : Path.Combine(Path.GetDirectoryName(scenePath) ?? ".", "environment.json");

        if (!File.Exists(descriptor))
        {
            return;
        }

        byte[] data = File.ReadAllBytes(descriptor);
        PluginEnvironment? effect = JsonSerializer.Deserialize<PluginEnvironment>(data);
        LotRenderer.AmbientParameters tuning = renderer.Ambient;
        renderer.ApplyEnvironment(effect);
        if (Environment.GetEnvironmentVariable("SIMS_SDL_AMBIENT") is not null)
        {
            renderer.Ambient.Radius = tuning.Radius;
            renderer.Ambient.Occlusion = tuning.Occlusion;
            renderer.Ambient.Shadow = tuning.Shadow;
            renderer.Ambient.Thickness = tuning.Thickness;
            renderer.Ambient.Reach = tuning.Reach;
        }
    }

    private static List<float>? ParseFloatList(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var values = new List<float>();
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            {
                values.Add(parsed);
            }
        }

        return values;
    }

    private static void TryLoadWindowIcon(nint window, string workspace)
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "sims1-icon.png"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "sims1-icon.png")),
            Path.Combine(workspace, "sims1-icon.png"),
            Path.Combine(workspace, "dist", "sims1-icon.png"),
        ];

        string? iconPath = candidates.FirstOrDefault(File.Exists);
        if (iconPath is null)
        {
            return;
        }

        try
        {
            byte[] pngBytes = File.ReadAllBytes(iconPath);
            StbImageSharp.ImageResult decoded = StbImageSharp.ImageResult.FromMemory(
                pngBytes,
                StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            if (decoded.Width <= 0 || decoded.Height <= 0)
            {
                return;
            }

            int rowBytes = decoded.Width * 4;
            if (decoded.Data.Length < rowBytes * decoded.Height)
            {
                return;
            }

            SDL_Surface* surface = SDL_CreateSurface(decoded.Width, decoded.Height, SDL_PIXELFORMAT_RGBA32);
            if (surface == null)
            {
                return;
            }

            try
            {
                if (surface->pixels == nint.Zero || surface->pitch < rowBytes)
                {
                    return;
                }

                byte* dstBase = (byte*)surface->pixels;
                for (int row = 0; row < decoded.Height; row++)
                {
                    decoded.Data.AsSpan(row * rowBytes, rowBytes)
                        .CopyTo(new Span<byte>(dstBase + row * surface->pitch, rowBytes));
                }

                _ = SDL_SetWindowIcon(window, surface);
            }
            finally
            {
                SDL_DestroySurface(surface);
            }
        }
        catch (Exception ex)
        {
            Log($"window icon: {ex.Message}");
        }
    }

    private static void WriteStdoutLine(ReadOnlySpan<byte> payload)
    {
        Stream stdout = Console.OpenStandardOutput();
        stdout.Write(payload);
        stdout.WriteByte(10);
        stdout.Flush();
    }

    private static void Log(string message) => Console.Error.WriteLine($"SimsSDL: {message}");
}
