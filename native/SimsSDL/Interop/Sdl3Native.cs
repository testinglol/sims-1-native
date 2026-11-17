using System.Reflection;

namespace SimsSDL.Interop;

public static unsafe partial class Sdl3Native
{
    private const string LibraryName = "SDL3";

    static Sdl3Native()
    {
        NativeLibrary.SetDllImportResolver(typeof(Sdl3Native).Assembly, ResolveLibrary);
    }

    private static nint ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
            return nint.Zero;

        var envOverride = Environment.GetEnvironmentVariable("SIMS_SDL3_PATH");
        if (!string.IsNullOrWhiteSpace(envOverride) && NativeLibrary.TryLoad(envOverride, out var envHandle))
            return envHandle;

        var baseDir = AppContext.BaseDirectory;
        string[] candidates;
        if (OperatingSystem.IsMacOS())
        {
            candidates =
            [
                Path.Combine(baseDir, "libSDL3.0.dylib"),
                Path.Combine(baseDir, "libSDL3.dylib"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "Frameworks", "libSDL3.0.dylib")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "Frameworks", "libSDL3.dylib")),
                "/opt/homebrew/lib/libSDL3.0.dylib",
                "/opt/homebrew/lib/libSDL3.dylib",
                "/usr/local/lib/libSDL3.0.dylib",
                "/usr/local/lib/libSDL3.dylib",
            ];
        }
        else if (OperatingSystem.IsWindows())
        {
            candidates =
            [
                Path.Combine(baseDir, "SDL3.dll"),
            ];
        }
        else
        {
            candidates =
            [
                Path.Combine(baseDir, "libSDL3.so.0"),
                Path.Combine(baseDir, "libSDL3.so"),
                "libSDL3.so.0",
                "libSDL3.so",
            ];
        }

        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var fallback) ? fallback : nint.Zero;
    }

    public const uint SDL_INIT_AUDIO = 0x10u;
    public const uint SDL_INIT_VIDEO = 0x20u;

    public const ulong SDL_WINDOW_RESIZABLE = 0x20UL;
    public const ulong SDL_WINDOW_HIGH_PIXEL_DENSITY = 0x2000UL;

    public const uint SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK = 0xFFFFFFFFu;
    public const uint SDL_AUDIO_F32 = 0x8120u;
    public const uint SDL_PIXELFORMAT_RGBA32 = 0x16762004u;

    public const byte SDL_BUTTON_LEFT = 1;
    public const byte SDL_BUTTON_MIDDLE = 2;
    public const byte SDL_BUTTON_RIGHT = 3;

    public const ushort SDL_KMOD_SHIFT = 0x3;
    public const ushort SDL_KMOD_CTRL = 0xC0;
    public const ushort SDL_KMOD_ALT = 0x300;
    public const ushort SDL_KMOD_GUI = 0x0C00;

    public const uint SDLK_RETURN = 0xDu;
    public const uint SDLK_ESCAPE = 0x1Bu;
    public const uint SDLK_BACKSPACE = 0x8u;
    public const uint SDLK_TAB = 0x9u;
    public const uint SDLK_SPACE = 0x20u;
    public const uint SDLK_DELETE = 0x7Fu;
    public const uint SDLK_F1 = 0x4000003Au;
    public const uint SDLK_F2 = 0x4000003Bu;
    public const uint SDLK_F3 = 0x4000003Cu;
    public const uint SDLK_F4 = 0x4000003Du;
    public const uint SDLK_F5 = 0x4000003Eu;
    public const uint SDLK_HOME = 0x4000004Au;
    public const uint SDLK_PAGEUP = 0x4000004Bu;
    public const uint SDLK_END = 0x4000004Du;
    public const uint SDLK_PAGEDOWN = 0x4000004Eu;
    public const uint SDLK_RIGHT = 0x4000004Fu;
    public const uint SDLK_LEFT = 0x40000050u;
    public const uint SDLK_DOWN = 0x40000051u;
    public const uint SDLK_UP = 0x40000052u;
    public const uint SDLK_KP_MINUS = 0x40000056u;
    public const uint SDLK_KP_PLUS = 0x40000057u;
    public const int SDL_SCANCODE_RIGHT = 79;
    public const int SDL_SCANCODE_LEFT = 80;
    public const int SDL_SCANCODE_DOWN = 81;
    public const int SDL_SCANCODE_UP = 82;
    public const uint SDLK_KP_ENTER = 0x40000058u;
    public const uint SDLK_KP_1 = 0x40000059u;
    public const uint SDLK_KP_2 = 0x4000005Au;
    public const uint SDLK_KP_3 = 0x4000005Bu;
    public const uint SDLK_KP_0 = 0x40000062u;

    public const uint SDL_EVENT_QUIT = 0x100u;
    public const uint SDL_EVENT_WINDOW_MOUSE_LEAVE = 0x20Du;
    public const uint SDL_EVENT_WINDOW_FOCUS_GAINED = 0x20Eu;
    public const uint SDL_EVENT_WINDOW_FOCUS_LOST = 0x20Fu;
    public const uint SDL_EVENT_WINDOW_CLOSE_REQUESTED = 0x210u;
    public const uint SDL_EVENT_KEY_DOWN = 0x300u;
    public const uint SDL_EVENT_TEXT_INPUT = 0x303u;
    public const uint SDL_EVENT_MOUSE_MOTION = 0x400u;
    public const uint SDL_EVENT_MOUSE_BUTTON_DOWN = 0x401u;
    public const uint SDL_EVENT_MOUSE_BUTTON_UP = 0x402u;
    public const uint SDL_EVENT_MOUSE_WHEEL = 0x403u;

    public const uint SDL_MESSAGEBOX_ERROR = 0x10u;
    public const uint SDL_MESSAGEBOX_WARNING = 0x20u;
    public const uint SDL_MESSAGEBOX_INFORMATION = 0x40u;
    public const uint SDL_MESSAGEBOX_BUTTON_RETURNKEY_DEFAULT = 0x1u;
    public const uint SDL_MESSAGEBOX_BUTTON_ESCAPEKEY_DEFAULT = 0x2u;

    public const uint SDL_GPU_SHADERFORMAT_SPIRV = 1u << 1;
    public const uint SDL_GPU_SHADERFORMAT_DXIL = 1u << 3;
    public const uint SDL_GPU_SHADERFORMAT_MSL = 1u << 4;

    public const int SDL_GPU_SHADERSTAGE_VERTEX = 0;
    public const int SDL_GPU_SHADERSTAGE_FRAGMENT = 1;

    public const int SDL_GPU_TEXTURETYPE_2D = 0;
    public const uint SDL_GPU_TEXTUREFORMAT_R8_UNORM = 2u;
    public const uint SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM = 4u;
    public const uint SDL_GPU_TEXTUREFORMAT_R16G16B16A16_FLOAT = 0x1Du;
    public const uint SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM_SRGB = 0x34u;
    public const uint SDL_GPU_TEXTUREFORMAT_D32_FLOAT = 0x3Cu;

    public const uint SDL_GPU_TEXTUREUSAGE_SAMPLER = 1u << 0;
    public const uint SDL_GPU_TEXTUREUSAGE_COLOR_TARGET = 1u << 1;
    public const uint SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET = 1u << 2;

    public const uint SDL_GPU_BUFFERUSAGE_GRAPHICS_STORAGE_READ = 1u << 3;

    public const int SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD = 0;
    public const int SDL_GPU_TRANSFERBUFFERUSAGE_DOWNLOAD = 1;

    public const int SDL_GPU_SAMPLECOUNT_1 = 0;
    public const int SDL_GPU_SAMPLECOUNT_2 = 1;
    public const int SDL_GPU_SAMPLECOUNT_4 = 2;
    public const int SDL_GPU_PRIMITIVETYPE_TRIANGLELIST = 0;
    public const int SDL_GPU_PRIMITIVETYPE_POINTLIST = 4;

    public const int SDL_GPU_LOADOP_LOAD = 0;
    public const int SDL_GPU_LOADOP_CLEAR = 1;
    public const int SDL_GPU_LOADOP_DONT_CARE = 2;

    public const int SDL_GPU_STOREOP_STORE = 0;
    public const int SDL_GPU_STOREOP_DONT_CARE = 1;
    public const int SDL_GPU_STOREOP_RESOLVE = 2;

    public const int SDL_GPU_FILTER_NEAREST = 0;
    public const int SDL_GPU_FILTER_LINEAR = 1;
    public const int SDL_GPU_SAMPLERMIPMAPMODE_NEAREST = 0;
    public const int SDL_GPU_SAMPLERMIPMAPMODE_LINEAR = 1;
    public const int SDL_GPU_SAMPLERADDRESSMODE_REPEAT = 0;
    public const int SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE = 2;

    public const int SDL_GPU_BLENDOP_ADD = 1;
    public const int SDL_GPU_BLENDFACTOR_ZERO = 1;
    public const int SDL_GPU_BLENDFACTOR_ONE = 2;
    public const int SDL_GPU_BLENDFACTOR_SRC_COLOR = 3;
    public const int SDL_GPU_BLENDFACTOR_DST_COLOR = 5;
    public const int SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA = 8;

    public const byte SDL_GPU_COLORCOMPONENT_R = 1 << 0;
    public const byte SDL_GPU_COLORCOMPONENT_G = 1 << 1;
    public const byte SDL_GPU_COLORCOMPONENT_B = 1 << 2;
    public const byte SDL_GPU_COLORCOMPONENT_A = 1 << 3;

    public const int SDL_GPU_FILLMODE_FILL = 0;
    public const int SDL_GPU_CULLMODE_NONE = 0;
    public const int SDL_GPU_COMPAREOP_EQUAL = 3;
    public const int SDL_GPU_COMPAREOP_LESS_OR_EQUAL = 4;
    public const int SDL_GPU_COMPAREOP_ALWAYS = 8;

    public const int SDL_GPU_SWAPCHAINCOMPOSITION_SDR = 0;
    public const int SDL_GPU_SWAPCHAINCOMPOSITION_SDR_LINEAR = 1;
    public const int SDL_GPU_PRESENTMODE_VSYNC = 0;
    public const int SDL_GPU_PRESENTMODE_IMMEDIATE = 1;
    public const int SDL_FLIP_NONE = 0;

    public const int SDL_GPU_VERTEXINPUTRATE_VERTEX = 0;
    public const int SDL_GPU_VERTEXELEMENTFORMAT_FLOAT = 9;
    public const int SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2 = 10;
    public const int SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3 = 11;
    public const int SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4 = 12;

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_AudioSpec
    {
        public uint format;
        public int channels;
        public int freq;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_Surface
    {
        public uint flags;
        public uint format;
        public int w;
        public int h;
        public int pitch;
        public nint pixels;
        public int refcount;
        public nint reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_Rect
    {
        public int x;
        public int y;
        public int w;
        public int h;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_FColor
    {
        public float r;
        public float g;
        public float b;
        public float a;
    }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    public struct SDL_Event
    {
        [FieldOffset(0)] public uint type;

        [FieldOffset(24)] public int key_scancode;
        [FieldOffset(28)] public uint key_key;
        [FieldOffset(32)] public ushort key_mod;
        [FieldOffset(36)] public byte key_down;
        [FieldOffset(37)] public byte key_repeat;

        [FieldOffset(28)] public float motion_x;
        [FieldOffset(32)] public float motion_y;
        [FieldOffset(36)] public float motion_xrel;
        [FieldOffset(40)] public float motion_yrel;

        [FieldOffset(24)] public byte button_button;
        [FieldOffset(25)] public byte button_down;
        [FieldOffset(26)] public byte button_clicks;
        [FieldOffset(28)] public float button_x;
        [FieldOffset(32)] public float button_y;

        [FieldOffset(24)] public float wheel_x;
        [FieldOffset(28)] public float wheel_y;

        [FieldOffset(24)] public nint text_text;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_MessageBoxButtonData
    {
        public uint flags;
        public int buttonID;
        public nint text;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_MessageBoxData
    {
        public uint flags;
        public nint window;
        public nint title;
        public nint message;
        public int numbuttons;
        public nint buttons;
        public nint colorScheme;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUSamplerCreateInfo
    {
        public int min_filter;
        public int mag_filter;
        public int mipmap_mode;
        public int address_mode_u;
        public int address_mode_v;
        public int address_mode_w;
        public float mip_lod_bias;
        public float max_anisotropy;
        public int compare_op;
        public float min_lod;
        public float max_lod;
        public byte enable_anisotropy;
        public byte enable_compare;
        public byte padding1;
        public byte padding2;
        public uint props;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUShaderCreateInfo
    {
        public nuint code_size;
        public nint code;
        public nint entrypoint;
        public uint format;
        public int stage;
        public uint num_samplers;
        public uint num_storage_textures;
        public uint num_storage_buffers;
        public uint num_uniform_buffers;
        public uint props;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUVertexBufferDescription
    {
        public uint slot;
        public uint pitch;
        public int input_rate;
        public uint instance_step_rate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUVertexAttribute
    {
        public uint location;
        public uint buffer_slot;
        public int format;
        public uint offset;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUVertexInputState
    {
        public nint vertex_buffer_descriptions;
        public uint num_vertex_buffers;
        public nint vertex_attributes;
        public uint num_vertex_attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPURasterizerState
    {
        public int fill_mode;
        public int cull_mode;
        public int front_face;
        public float depth_bias_constant_factor;
        public float depth_bias_clamp;
        public float depth_bias_slope_factor;
        public byte enable_depth_bias;
        public byte enable_depth_clip;
        public byte padding1;
        public byte padding2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUMultisampleState
    {
        public int sample_count;
        public uint sample_mask;
        public byte enable_mask;
        public byte enable_alpha_to_coverage;
        public byte padding2;
        public byte padding3;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUStencilOpState
    {
        public int fail_op;
        public int pass_op;
        public int depth_fail_op;
        public int compare_op;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUDepthStencilState
    {
        public int compare_op;
        public SDL_GPUStencilOpState back_stencil_state;
        public SDL_GPUStencilOpState front_stencil_state;
        public byte compare_mask;
        public byte write_mask;
        public byte enable_depth_test;
        public byte enable_depth_write;
        public byte enable_stencil_test;
        public byte padding1;
        public byte padding2;
        public byte padding3;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUColorTargetBlendState
    {
        public int src_color_blendfactor;
        public int dst_color_blendfactor;
        public int color_blend_op;
        public int src_alpha_blendfactor;
        public int dst_alpha_blendfactor;
        public int alpha_blend_op;
        public byte color_write_mask;
        public byte enable_blend;
        public byte enable_color_write_mask;
        public byte padding1;
        public byte padding2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUColorTargetDescription
    {
        public uint format;
        public SDL_GPUColorTargetBlendState blend_state;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUGraphicsPipelineTargetInfo
    {
        public nint color_target_descriptions;
        public uint num_color_targets;
        public uint depth_stencil_format;
        public byte has_depth_stencil_target;
        public byte padding1;
        public byte padding2;
        public byte padding3;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUGraphicsPipelineCreateInfo
    {
        public nint vertex_shader;
        public nint fragment_shader;
        public SDL_GPUVertexInputState vertex_input_state;
        public int primitive_type;
        public SDL_GPURasterizerState rasterizer_state;
        public SDL_GPUMultisampleState multisample_state;
        public SDL_GPUDepthStencilState depth_stencil_state;
        public SDL_GPUGraphicsPipelineTargetInfo target_info;
        public uint props;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUTextureCreateInfo
    {
        public int type;
        public uint format;
        public uint usage;
        public uint width;
        public uint height;
        public uint layer_count_or_depth;
        public uint num_levels;
        public int sample_count;
        public uint props;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUBufferCreateInfo
    {
        public uint usage;
        public uint size;
        public uint props;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUTransferBufferCreateInfo
    {
        public int usage;
        public uint size;
        public uint props;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUTextureTransferInfo
    {
        public nint transfer_buffer;
        public uint offset;
        public uint pixels_per_row;
        public uint rows_per_layer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUTransferBufferLocation
    {
        public nint transfer_buffer;
        public uint offset;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUTextureRegion
    {
        public nint texture;
        public uint mip_level;
        public uint layer;
        public uint x;
        public uint y;
        public uint z;
        public uint w;
        public uint h;
        public uint d;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUBufferRegion
    {
        public nint buffer;
        public uint offset;
        public uint size;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUTextureLocation
    {
        public nint texture;
        public uint mip_level;
        public uint layer;
        public uint x;
        public uint y;
        public uint z;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUTextureSamplerBinding
    {
        public nint texture;
        public nint sampler;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUBlitRegion
    {
        public nint texture;
        public uint mip_level;
        public uint layer_or_depth_plane;
        public uint x;
        public uint y;
        public uint w;
        public uint h;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUBlitInfo
    {
        public SDL_GPUBlitRegion source;
        public SDL_GPUBlitRegion destination;
        public int load_op;
        public SDL_FColor clear_color;
        public int flip_mode;
        public int filter;
        public byte cycle;
        public byte padding1;
        public byte padding2;
        public byte padding3;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUColorTargetInfo
    {
        public nint texture;
        public uint mip_level;
        public uint layer_or_depth_plane;
        public SDL_FColor clear_color;
        public int load_op;
        public int store_op;
        public nint resolve_texture;
        public uint resolve_mip_level;
        public uint resolve_layer;
        public byte cycle;
        public byte cycle_resolve_texture;
        public byte padding1;
        public byte padding2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_GPUDepthStencilTargetInfo
    {
        public nint texture;
        public float clear_depth;
        public int load_op;
        public int store_op;
        public int stencil_load_op;
        public int stencil_store_op;
        public byte cycle;
        public byte clear_stencil;
        public byte mip_level;
        public byte layer;
    }

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_Init(uint flags);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_InitSubSystem(uint flags);

    [LibraryImport(LibraryName)]
    public static partial void SDL_Quit();

    [LibraryImport(LibraryName)]
    public static partial void SDL_QuitSubSystem(uint flags);

    [LibraryImport(LibraryName, EntryPoint = "SDL_GetError")]
    private static partial nint SDL_GetErrorPtr();

    public static string SDL_GetError() => Marshal.PtrToStringUTF8(SDL_GetErrorPtr()) ?? string.Empty;

    [LibraryImport(LibraryName)]
    public static partial void SDL_Delay(uint ms);

    [LibraryImport(LibraryName)]
    public static partial ulong SDL_GetTicks();

    [LibraryImport(LibraryName)]
    public static partial void SDL_free(nint mem);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_OpenURL(string url);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint SDL_CreateWindow(string title, int w, int h, ulong flags);

    [LibraryImport(LibraryName)]
    public static partial void SDL_DestroyWindow(nint window);

    [LibraryImport(LibraryName)]
    public static partial float SDL_GetWindowPixelDensity(nint window);

    [LibraryImport(LibraryName)]
    public static partial void SDL_WarpMouseInWindow(nint window, float x, float y);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_PollEvent(out SDL_Event ev);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_StartTextInput(nint window);

    [LibraryImport(LibraryName)]
    public static partial ushort SDL_GetModState();

    [LibraryImport(LibraryName)]
    public static partial byte* SDL_GetKeyboardState(out int numkeys);

    [LibraryImport(LibraryName)]
    public static partial SDL_Surface* SDL_CreateSurface(int width, int height, uint format);

    [LibraryImport(LibraryName)]
    public static partial void SDL_DestroySurface(SDL_Surface* surface);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_SaveBMP(SDL_Surface* surface, string file);

    [LibraryImport(LibraryName)]
    public static partial nint SDL_CreateColorCursor(SDL_Surface* surface, int hot_x, int hot_y);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_SetCursor(nint cursor);

    [LibraryImport(LibraryName)]
    public static partial nint SDL_GetDefaultCursor();

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_ShowSimpleMessageBox(uint flags, string title, string message, nint window);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_ShowMessageBox(in SDL_MessageBoxData messageboxdata, out int buttonid);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void SDL_ShowOpenFolderDialog(
        delegate* unmanaged[Cdecl]<nint, nint*, int, void> callback,
        nint userdata,
        nint window,
        string? default_location,
        [MarshalAs(UnmanagedType.U1)] bool allow_many);

    [LibraryImport(LibraryName)]
    public static partial nint SDL_IOFromConstMem(nint mem, nuint size);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_LoadWAV_IO(
        nint src,
        [MarshalAs(UnmanagedType.U1)] bool closeio,
        out SDL_AudioSpec spec,
        out byte* audio_buf,
        out uint audio_len);

    [LibraryImport(LibraryName)]
    public static partial nint SDL_OpenAudioDeviceStream(
        uint devid,
        in SDL_AudioSpec spec,
        nint callback,
        nint userdata);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_ResumeAudioStreamDevice(nint stream);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_ClearAudioStream(nint stream);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_PutAudioStreamData(nint stream, nint buf, int len);

    [LibraryImport(LibraryName)]
    public static partial void SDL_DestroyAudioStream(nint stream);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_ConvertAudioSamples(
        in SDL_AudioSpec src_spec,
        nint src_data,
        int src_len,
        in SDL_AudioSpec dst_spec,
        out byte* dst_data,
        out int dst_len);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint SDL_CreateGPUDevice(
        uint format_flags,
        [MarshalAs(UnmanagedType.U1)] bool debug_mode,
        string? name);

    [LibraryImport(LibraryName)]
    public static partial void SDL_DestroyGPUDevice(nint device);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_ClaimWindowForGPUDevice(nint device, nint window);

    [LibraryImport(LibraryName)]
    public static partial void SDL_ReleaseWindowFromGPUDevice(nint device, nint window);

    [LibraryImport(LibraryName)]
    public static partial uint SDL_GetGPUShaderFormats(nint device);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_GPUTextureSupportsSampleCount(nint device, uint format, int sample_count);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_WindowSupportsGPUSwapchainComposition(nint device, nint window, int swapchain_composition);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_SetGPUSwapchainParameters(nint device, nint window, int swapchain_composition, int present_mode);

    [LibraryImport(LibraryName)]
    public static partial nint SDL_CreateGPUSampler(nint device, in SDL_GPUSamplerCreateInfo createinfo);

    [LibraryImport(LibraryName)]
    public static partial void SDL_ReleaseGPUSampler(nint device, nint sampler);

    [LibraryImport(LibraryName)]
    public static partial nint SDL_CreateGPUShader(nint device, in SDL_GPUShaderCreateInfo createinfo);

    [LibraryImport(LibraryName)]
    public static partial void SDL_ReleaseGPUShader(nint device, nint shader);

    [LibraryImport(LibraryName)]
    public static partial nint SDL_CreateGPUGraphicsPipeline(nint device, in SDL_GPUGraphicsPipelineCreateInfo createinfo);

    [LibraryImport(LibraryName)]
    public static partial void SDL_ReleaseGPUGraphicsPipeline(nint device, nint graphics_pipeline);

    [LibraryImport(LibraryName)]
    public static partial nint SDL_CreateGPUTexture(nint device, in SDL_GPUTextureCreateInfo createinfo);

    [LibraryImport(LibraryName)]
    public static partial void SDL_ReleaseGPUTexture(nint device, nint texture);

    [LibraryImport(LibraryName)]
    public static partial nint SDL_CreateGPUBuffer(nint device, in SDL_GPUBufferCreateInfo createinfo);

    [LibraryImport(LibraryName)]
    public static partial void SDL_ReleaseGPUBuffer(nint device, nint buffer);

    [LibraryImport(LibraryName)]
    public static partial nint SDL_CreateGPUTransferBuffer(nint device, in SDL_GPUTransferBufferCreateInfo createinfo);

    [LibraryImport(LibraryName)]
    public static partial void SDL_ReleaseGPUTransferBuffer(nint device, nint transfer_buffer);

    [LibraryImport(LibraryName)]
    public static partial nint SDL_MapGPUTransferBuffer(nint device, nint transfer_buffer, [MarshalAs(UnmanagedType.U1)] bool cycle);

    [LibraryImport(LibraryName)]
    public static partial void SDL_UnmapGPUTransferBuffer(nint device, nint transfer_buffer);

    [LibraryImport(LibraryName)]
    public static partial nint SDL_AcquireGPUCommandBuffer(nint device);

    [LibraryImport(LibraryName)]
    public static partial nint SDL_BeginGPUCopyPass(nint command_buffer);

    [LibraryImport(LibraryName)]
    public static partial void SDL_EndGPUCopyPass(nint copy_pass);

    [LibraryImport(LibraryName)]
    public static partial void SDL_UploadToGPUTexture(
        nint copy_pass,
        in SDL_GPUTextureTransferInfo source,
        in SDL_GPUTextureRegion destination,
        [MarshalAs(UnmanagedType.U1)] bool cycle);

    [LibraryImport(LibraryName)]
    public static partial void SDL_UploadToGPUBuffer(
        nint copy_pass,
        in SDL_GPUTransferBufferLocation source,
        in SDL_GPUBufferRegion destination,
        [MarshalAs(UnmanagedType.U1)] bool cycle);

    [LibraryImport(LibraryName)]
    public static partial void SDL_CopyGPUTextureToTexture(
        nint copy_pass,
        in SDL_GPUTextureLocation source,
        in SDL_GPUTextureLocation destination,
        uint w,
        uint h,
        uint d,
        [MarshalAs(UnmanagedType.U1)] bool cycle);

    [LibraryImport(LibraryName)]
    public static partial void SDL_DownloadFromGPUTexture(
        nint copy_pass,
        in SDL_GPUTextureRegion source,
        in SDL_GPUTextureTransferInfo destination);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_WaitAndAcquireGPUSwapchainTexture(
        nint command_buffer,
        nint window,
        out nint swapchain_texture,
        out uint swapchain_texture_width,
        out uint swapchain_texture_height);

    [LibraryImport(LibraryName)]
    public static partial nint SDL_BeginGPURenderPass(
        nint command_buffer,
        SDL_GPUColorTargetInfo* color_target_infos,
        uint num_color_targets,
        SDL_GPUDepthStencilTargetInfo* depth_stencil_target_info);

    [LibraryImport(LibraryName)]
    public static partial void SDL_EndGPURenderPass(nint render_pass);

    [LibraryImport(LibraryName)]
    public static partial void SDL_BindGPUGraphicsPipeline(nint render_pass, nint graphics_pipeline);

    [LibraryImport(LibraryName)]
    public static partial void SDL_SetGPUScissor(nint render_pass, in SDL_Rect scissor);

    [LibraryImport(LibraryName)]
    public static partial void SDL_PushGPUVertexUniformData(nint command_buffer, uint slot_index, nint data, uint length);

    [LibraryImport(LibraryName)]
    public static partial void SDL_PushGPUFragmentUniformData(nint command_buffer, uint slot_index, nint data, uint length);

    [LibraryImport(LibraryName)]
    public static partial void SDL_BindGPUVertexSamplers(nint render_pass, uint first_slot, SDL_GPUTextureSamplerBinding* texture_sampler_bindings, uint num_bindings);

    [LibraryImport(LibraryName)]
    public static partial void SDL_BindGPUVertexStorageBuffers(nint render_pass, uint first_slot, nint* storage_buffers, uint num_bindings);

    [LibraryImport(LibraryName)]
    public static partial void SDL_BindGPUFragmentSamplers(nint render_pass, uint first_slot, SDL_GPUTextureSamplerBinding* texture_sampler_bindings, uint num_bindings);

    [LibraryImport(LibraryName)]
    public static partial void SDL_DrawGPUPrimitives(nint render_pass, uint num_vertices, uint num_instances, uint first_vertex, uint first_instance);

    [LibraryImport(LibraryName)]
    public static partial void SDL_BlitGPUTexture(nint command_buffer, in SDL_GPUBlitInfo info);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_SubmitGPUCommandBuffer(nint command_buffer);

    [LibraryImport(LibraryName)]
    public static partial nint SDL_SubmitGPUCommandBufferAndAcquireFence(nint command_buffer);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SDL_WaitForGPUFences(nint device, [MarshalAs(UnmanagedType.U1)] bool wait_all, nint* fences, uint num_fences);

    [LibraryImport(LibraryName)]
    public static partial void SDL_ReleaseGPUFence(nint device, nint fence);
}
