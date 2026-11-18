using System.Numerics;

namespace SimsSDL;

public sealed class SDLError : Exception
{
    public SDLError(string context)
        : base($"{context}: {SDL_GetError()}")
    {
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct Quad
{
    public Vector4 Rect;
    public Vector4 CanvasPadding;
    public Vector4 UvRect;
    public Vector4 DepthInterval;
    public Vector4 Tint;

    public Quad()
    {
        Rect = Vector4.Zero;
        CanvasPadding = Vector4.Zero;
        UvRect = new Vector4(0f, 0f, 1f, 1f);
        DepthInterval = Vector4.Zero;
        Tint = Vector4.One;
    }

    public Quad(Vector4 rect, Vector2 canvas, bool flip = false, float opacity = 1f)
    {
        Rect = rect;
        CanvasPadding = new Vector4(canvas.X, canvas.Y, flip ? 1f : 0f, opacity);
        UvRect = new Vector4(0f, 0f, 1f, 1f);
        DepthInterval = Vector4.Zero;
        Tint = Vector4.One;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct TerrainVertex
{
    public Vector2 Position;

    /// <summary>Stores the grass wear state in the x component from 0 (green) to 1 (brown), with y unused.</summary>
    public Vector2 Grass;

    /// <summary>Stores the fully lit grass color and the darkest shade of its row, both as normalized sRGB
    /// values.</summary>
    public Vector4 Color;
    public Vector4 Dark;

    public TerrainVertex()
    {
        Position = Vector2.Zero;
        Grass = Vector2.Zero;
        Color = Vector4.Zero;
        Dark = Vector4.Zero;
    }

    public TerrainVertex(Vector2 position, Vector2 grass, Vector4 color, Vector4 dark)
    {
        Position = position;
        Grass = grass;
        Color = color;
        Dark = dark;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct AvatarVertex
{
    public Vector4 Position;
    public Vector2 Uv;
    public Vector2 Padding;
    public Vector4 Normal;

    public AvatarVertex()
    {
        Position = Vector4.Zero;
        Uv = Vector2.Zero;
        Padding = Vector2.Zero;
        Normal = Vector4.Zero;
    }

    public AvatarVertex(Vector4 position, Vector2 uv, Vector4 normal)
    {
        Position = position;
        Uv = uv;
        Padding = Vector2.Zero;
        Normal = normal;
    }
}

/// <summary>Manages the GPU device lifetime with reference counting to ensure textures and buffers release
/// before device destruction regardless of finalizer ordering.</summary>
public sealed class GPUDevice : IDisposable
{
    private nint _handle;
    private int _refs;
    private int _ownerReleased;

    public nint Handle => Volatile.Read(ref _handle);

    public GPUDevice(nint handle)
    {
        if (handle == nint.Zero)
            throw new ArgumentException("GPU device handle cannot be zero.", nameof(handle));
        _handle = handle;
        _refs = 1;
    }

    internal bool Retain()
    {
        while (true)
        {
            int current = Volatile.Read(ref _refs);
            if (current <= 0)
                return false;
            if (Interlocked.CompareExchange(ref _refs, current + 1, current) == current)
                return true;
        }
    }

    internal void ReleaseRef()
    {
        if (Interlocked.Decrement(ref _refs) == 0)
        {
            nint handle = Interlocked.Exchange(ref _handle, nint.Zero);
            if (handle != nint.Zero)
                SDL_DestroyGPUDevice(handle);
        }
    }

    private void ReleaseOwnerRef()
    {
        if (Interlocked.Exchange(ref _ownerReleased, 1) == 0)
            ReleaseRef();
    }

    public void Dispose()
    {
        ReleaseOwnerRef();
        GC.SuppressFinalize(this);
    }

    ~GPUDevice() => ReleaseOwnerRef();
}

public sealed class GPUTexture : IDisposable
{
    private readonly GPUDevice _device;
    private readonly long _byteSize;
    private nint _handle;
    private int _disposed;

    public nint Handle => Volatile.Read(ref _handle);
    public int Width { get; }
    public int Height { get; }
    public uint Format { get; }
    public uint NumLevels { get; }
    public int SampleCount { get; }

    public GPUTexture(
        nint handle,
        int width,
        int height,
        uint format,
        GPUDevice device,
        long byteSize = -1,
        uint numLevels = 1,
        int sampleCount = SDL_GPU_SAMPLECOUNT_1)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (handle == nint.Zero)
            throw new ArgumentException("GPU texture handle cannot be zero.", nameof(handle));
        if (!device.Retain())
            throw new ObjectDisposedException(nameof(GPUDevice));
        _handle = handle;
        Width = width;
        Height = height;
        Format = format;
        NumLevels = Math.Max(1u, numLevels);
        SampleCount = sampleCount;
        _device = device;
        _byteSize = byteSize >= 0 ? byteSize : EstimateByteSize(width, height, format, NumLevels, sampleCount);
        if (_byteSize > 0)
            GC.AddMemoryPressure(_byteSize);
    }

    private static long EstimateByteSize(int width, int height, uint format, uint numLevels, int sampleCount)
    {
        long baseBytes = (long)Math.Max(1, width) * Math.Max(1, height) * format switch
        {
            SDL_GPU_TEXTUREFORMAT_R8_UNORM => 1L,
            SDL_GPU_TEXTUREFORMAT_R16G16B16A16_FLOAT => 8L,
            _ => 4L,
        };
        long samples = sampleCount switch
        {
            SDL_GPU_SAMPLECOUNT_2 => 2L,
            SDL_GPU_SAMPLECOUNT_4 => 4L,
            _ => 1L,
        };
        long scaled = baseBytes * samples;
        return numLevels > 1 ? scaled * 4L / 3L : scaled;
    }

    private void ReleaseNative()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        nint handle = Interlocked.Exchange(ref _handle, nint.Zero);
        if (handle != nint.Zero)
        {
            nint dev = _device.Handle;
            if (dev != nint.Zero)
                SDL_ReleaseGPUTexture(dev, handle);
        }
        if (_byteSize > 0)
            GC.RemoveMemoryPressure(_byteSize);
        _device.ReleaseRef();
    }

    public void Dispose()
    {
        ReleaseNative();
        GC.SuppressFinalize(this);
    }

    ~GPUTexture() => ReleaseNative();
}

public sealed class GPUBuffer : IDisposable
{
    private readonly GPUDevice _device;
    private readonly long _byteSize;
    private nint _handle;
    private int _disposed;

    public nint Handle => Volatile.Read(ref _handle);
    public int Count { get; }
    public long ByteSize => _byteSize;

    public GPUBuffer(nint handle, int count, GPUDevice device, long byteSize = 0)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (handle == nint.Zero)
            throw new ArgumentException("GPU buffer handle cannot be zero.", nameof(handle));
        if (!device.Retain())
            throw new ObjectDisposedException(nameof(GPUDevice));
        _handle = handle;
        Count = count;
        _device = device;
        _byteSize = byteSize > 0 ? byteSize : Math.Max(0L, count);
        if (_byteSize > 0)
            GC.AddMemoryPressure(_byteSize);
    }

    private void ReleaseNative()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        nint handle = Interlocked.Exchange(ref _handle, nint.Zero);
        if (handle != nint.Zero)
        {
            nint dev = _device.Handle;
            if (dev != nint.Zero)
                SDL_ReleaseGPUBuffer(dev, handle);
        }
        if (_byteSize > 0)
            GC.RemoveMemoryPressure(_byteSize);
        _device.ReleaseRef();
    }

    public void Dispose()
    {
        ReleaseNative();
        GC.SuppressFinalize(this);
    }

    ~GPUBuffer() => ReleaseNative();
}

public sealed class GPUContext : IDisposable
{
    public enum DepthMode
    {
        Flat,
        Mesh,
        MeshEdge,
        MeshDepthPrepass,
        MeshEqual,
    }

    public enum Program
    {
        Sprite,
        Ghost,
        SpriteDepth,
        Terrain,
        Placement,
        Surface,
        Avatar,
        /// <summary>Renders depth-sprite ambient occlusion and directional sun shadows to a dedicated
        /// target, smoothed by <see cref="AmbientBlur"/> and multiplied into the lot color at double
        /// intensity.</summary>
        Ambient,
        AmbientBlur,
        /// <summary>Renders rain, snow, and sky reflections in puddles, blended over the lit lot without
        /// depth testing.</summary>
        Atmosphere,
        /// <summary>Renders seasonal foliage and snow over the unlit lot color before ambient lighting,
        /// writing vertical displacement to the snow surface into a secondary target.</summary>
        Season,
        /// <summary>Renders footprints into the accumulated snow displacement trail, partially decaying
        /// previous impressions while stamping new footprints.</summary>
        Trail,
        /// <summary>Renders the terrain depth pass into a dedicated depth render target.</summary>
        GroundDepth,
        /// <summary>Projects lot depth samples onto a top-down height map of the lot at one point per
        /// sampled pixel.</summary>
        HeightScatter,
        /// <summary>Masks visible characters positioned in front of lot depth into a dedicated target so
        /// seasonal overlay passes do not affect them.</summary>
        AvatarMask,
        /// <summary>Pixelates unclothed characters while correctly sorting behind foreground objects and
        /// walls.</summary>
        Censor,
    }

    public const uint ColorFormat = SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM_SRGB;

    /// <summary>Texture format for the seasonal pass secondary target storing surface displacement to the
    /// snow layer.</summary>
    public const uint LiftFormat = SDL_GPU_TEXTUREFORMAT_R16G16B16A16_FLOAT;

    public const uint DepthFormat = SDL_GPU_TEXTUREFORMAT_D32_FLOAT;

    /// <summary>Texture format for the character visibility mask marking pixels where a character was
    /// drawn.</summary>
    public const uint MaskFormat = SDL_GPU_TEXTUREFORMAT_R8_UNORM;

    private readonly GPUDevice _owner;
    private readonly Dictionary<string, nint> _shaders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, nint> _pipelines = new(StringComparer.Ordinal);
    private nint _batchCommand;
    private nint _batchCopy;
    private int _disposed;

    public nint Device => _owner.Handle;
    public string DriverName => SDL_GetGPUDeviceDriver(Device);
    public int PreferredSampleCount { get; private set; }
    public nint Sampler { get; private set; }
    public nint RoofSampler { get; private set; }
    public nint AnisoSampler { get; private set; }

    /// <summary>Nearest-neighbor sampler used when rendering at integer pixel ratios to keep zoomed sprites
    /// sharp.</summary>
    public nint PixelSampler { get; private set; }

    public Dictionary<string, string> EffectShaders { get; set; } = new(StringComparer.Ordinal);

    public nint SpriteDepthPipeline => Pipeline(Program.SpriteDepth, DepthMode.Mesh);
    public nint SpriteDepthEdgePipeline => Pipeline(Program.SpriteDepth, DepthMode.MeshEdge);

    public GPUContext(string? preferredDriver = null)
    {
        string? driverHint = string.IsNullOrWhiteSpace(preferredDriver)
            ? Environment.GetEnvironmentVariable("SIMS_GPU_DRIVER")
            : preferredDriver;
        if (string.IsNullOrWhiteSpace(driverHint))
            driverHint = null;

        uint requestedFormats = SDL_GPU_SHADERFORMAT_SPIRV | SDL_GPU_SHADERFORMAT_MSL |
            (ShaderDirectory is { } shaderDir && File.Exists(Path.Combine(shaderDir, "sprite.vert.dxil")) ? SDL_GPU_SHADERFORMAT_DXIL : 0);
        AppLog.Info(FormattableString.Invariant(
            $"Calling SDL_CreateGPUDevice (requestedFormats=0x{requestedFormats:X}, driverHint={driverHint ?? "<auto>"})..."));
        nint device = SDL_CreateGPUDevice(requestedFormats, false, driverHint);
        if (device == nint.Zero)
            throw new SDLError("SDL_CreateGPUDevice");

        _owner = new GPUDevice(device);
        // Retain device so child resources finalize before the device is destroyed.
        _owner.Retain();
        try
        {
            PreferredSampleCount = SDL_GPU_SAMPLECOUNT_1;
            foreach (int candidate in new[] { SDL_GPU_SAMPLECOUNT_4, SDL_GPU_SAMPLECOUNT_2 })
            {
                if (SDL_GPUTextureSupportsSampleCount(Device, ColorFormat, candidate) &&
                    SDL_GPUTextureSupportsSampleCount(Device, DepthFormat, candidate))
                {
                    PreferredSampleCount = candidate;
                    break;
                }
            }

            Sampler = MakeSampler(SDL_GPU_FILTER_LINEAR, SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE);
            RoofSampler = MakeSampler(SDL_GPU_FILTER_LINEAR, SDL_GPU_SAMPLERADDRESSMODE_REPEAT, SDL_GPU_SAMPLERMIPMAPMODE_LINEAR, 16f);
            AnisoSampler = MakeSampler(SDL_GPU_FILTER_LINEAR, SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE, SDL_GPU_SAMPLERMIPMAPMODE_LINEAR, 16f);
            PixelSampler = MakeSampler(SDL_GPU_FILTER_NEAREST, SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE);
        }
        catch
        {
            ReleaseNative();
            GC.SuppressFinalize(this);
            throw;
        }
    }

    private nint MakeSampler(
        int filter,
        int address,
        int mipmapMode = SDL_GPU_SAMPLERMIPMAPMODE_NEAREST,
        float maxAnisotropy = 1f)
    {
        var info = new SDL_GPUSamplerCreateInfo
        {
            min_filter = filter,
            mag_filter = filter,
            mipmap_mode = mipmapMode,
            address_mode_u = address,
            address_mode_v = address,
            address_mode_w = address,
            max_lod = mipmapMode == SDL_GPU_SAMPLERMIPMAPMODE_LINEAR ? 1000f : 0f,
            enable_anisotropy = maxAnisotropy > 1f ? (byte)1 : (byte)0,
            max_anisotropy = maxAnisotropy,
        };
        nint sampler = SDL_CreateGPUSampler(Device, in info);
        if (sampler == nint.Zero)
            throw new SDLError("SDL_CreateGPUSampler");
        return sampler;
    }

    internal static string? ResolvedShaderDirectory => ShaderDirectory;

    private static string? ShaderDirectory
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("SIMS_SDL_SHADERS");
            if (!string.IsNullOrEmpty(env))
                return env;
            return SimsAssetPaths.FindShadersDirectory();
        }
    }

    public bool SupportsShaderAsset(string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
            return false;

        uint formats = SDL_GetGPUShaderFormats(Device);
        if ((formats & SDL_GPU_SHADERFORMAT_MSL) != 0)
            return File.Exists(basePath + ".msl");
        if ((formats & SDL_GPU_SHADERFORMAT_DXIL) != 0 && File.Exists(basePath + ".dxil"))
            return true;
        if ((formats & SDL_GPU_SHADERFORMAT_SPIRV) != 0 && File.Exists(basePath + ".spv"))
            return true;
        return false;
    }

    private unsafe nint Shader(string name, int stage, uint samplers = 0, uint storageBuffers = 0, uint uniforms = 0)
    {
        string? source = EffectShaders.TryGetValue(name, out string? effectPath)
            ? effectPath
            : ShaderDirectory is { Length: > 0 } dir
                ? Path.Combine(dir, name)
                : null;
        string key = source ?? name;
        if (_shaders.TryGetValue(key, out nint cached))
            return cached;

        uint formats = SDL_GetGPUShaderFormats(Device);
        bool metal = (formats & SDL_GPU_SHADERFORMAT_MSL) != 0;
        bool dxil = !metal && (formats & SDL_GPU_SHADERFORMAT_DXIL) != 0 && source is not null && File.Exists(source + ".dxil");
        if (string.IsNullOrEmpty(source))
            throw new ContentError("SDL shaders are missing");

        string path = source + (metal ? ".msl" : dxil ? ".dxil" : ".spv");
        byte[] code;
        if (metal)
        {
            string text = File.ReadAllText(path, Encoding.UTF8)
                .Replace("#include <simd/simd.h>", string.Empty, StringComparison.Ordinal);
            int byteCount = Encoding.UTF8.GetByteCount(text);
            code = new byte[byteCount + 1];
            Encoding.UTF8.GetBytes(text, 0, text.Length, code, 0);
            code[byteCount] = 0;
        }
        else
        {
            code = File.ReadAllBytes(path);
        }

        if (code.Length == 0)
            throw new ContentError($"Empty SDL shader: {path}");

        ReadOnlySpan<byte> entryBytes = metal ? "main0\0"u8 : "main\0"u8;
        nint created;
        fixed (byte* codePtr = code)
        fixed (byte* entryPtr = entryBytes)
        {
            var info = new SDL_GPUShaderCreateInfo
            {
                code_size = (nuint)code.Length,
                code = (nint)codePtr,
                entrypoint = (nint)entryPtr,
                format = metal ? SDL_GPU_SHADERFORMAT_MSL : dxil ? SDL_GPU_SHADERFORMAT_DXIL : SDL_GPU_SHADERFORMAT_SPIRV,
                stage = stage,
                num_samplers = samplers,
                num_storage_buffers = storageBuffers,
                num_uniform_buffers = uniforms,
            };
            created = SDL_CreateGPUShader(Device, in info);
        }

        if (created == nint.Zero)
            throw new SDLError($"SDL_CreateGPUShader {name}");
        _shaders[key] = created;
        return created;
    }

    public unsafe nint Pipeline(Program program, DepthMode depth, int sampleCount = SDL_GPU_SAMPLECOUNT_1)
    {
        string effectPart = EffectShaders.Count == 0
            ? string.Empty
            : string.Join("|", EffectShaders.OrderBy(static kv => kv.Key, StringComparer.Ordinal).Select(static kv => kv.Value));
        string key = $"{program}-{depth}-{sampleCount}-{effectPart}";
        if (_pipelines.TryGetValue(key, out nint cached))
            return cached;

        nint vertex;
        nint fragment;
        bool blended = true;
        bool multiplied = false;
        bool depthOnly = false;
        switch (program)
        {
            case Program.GroundDepth:
                vertex = Shader("groundDepth.vert", SDL_GPU_SHADERSTAGE_VERTEX, storageBuffers: 1, uniforms: 1);
                fragment = Shader("depthOnly.frag", SDL_GPU_SHADERSTAGE_FRAGMENT);
                depthOnly = true;
                break;
            case Program.HeightScatter:
                vertex = Shader("heightScatter.vert", SDL_GPU_SHADERSTAGE_VERTEX, samplers: 1, uniforms: 1);
                fragment = Shader("depthOnly.frag", SDL_GPU_SHADERSTAGE_FRAGMENT);
                depthOnly = true;
                break;
            case Program.Ambient:
            case Program.AmbientBlur:
                vertex = Shader("fullscreen.vert", SDL_GPU_SHADERSTAGE_VERTEX);
                fragment = program == Program.Ambient
                    ? Shader("ambient.frag", SDL_GPU_SHADERSTAGE_FRAGMENT, samplers: 7, uniforms: 1)
                    : Shader("ambientBlur.frag", SDL_GPU_SHADERSTAGE_FRAGMENT, samplers: 4, uniforms: 1);
                blended = program == Program.AmbientBlur;
                multiplied = true;
                break;
            case Program.Atmosphere:
                vertex = Shader("fullscreen.vert", SDL_GPU_SHADERSTAGE_VERTEX);
                fragment = Shader("atmosphere.frag", SDL_GPU_SHADERSTAGE_FRAGMENT, samplers: 4, uniforms: 1);
                break;
            case Program.Season:
                vertex = Shader("fullscreen.vert", SDL_GPU_SHADERSTAGE_VERTEX);
                fragment = Shader("season.frag", SDL_GPU_SHADERSTAGE_FRAGMENT, samplers: 8, uniforms: 1);
                break;
            case Program.Trail:
                vertex = Shader("fullscreen.vert", SDL_GPU_SHADERSTAGE_VERTEX);
                fragment = Shader("trail.frag", SDL_GPU_SHADERSTAGE_FRAGMENT, samplers: 1, uniforms: 1);
                blended = false;
                break;
            case Program.Sprite:
            case Program.Ghost:
            case Program.SpriteDepth:
                vertex = Shader("sprite.vert", SDL_GPU_SHADERSTAGE_VERTEX, uniforms: 1);
                string fragName = program switch
                {
                    Program.Sprite => "sprite.frag",
                    Program.Ghost => "ghost.frag",
                    _ => "spriteDepth.frag",
                };
                fragment = Shader(fragName, SDL_GPU_SHADERSTAGE_FRAGMENT, samplers: program == Program.SpriteDepth ? 2u : 1u);
                break;
            case Program.Censor:
                vertex = Shader("sprite.vert", SDL_GPU_SHADERSTAGE_VERTEX, uniforms: 1);
                fragment = Shader("censor.frag", SDL_GPU_SHADERSTAGE_FRAGMENT, samplers: 2);
                blended = false;
                break;
            case Program.Terrain:
            case Program.Placement:
                vertex = Shader("terrain.vert", SDL_GPU_SHADERSTAGE_VERTEX, storageBuffers: 1, uniforms: 1);
                fragment = Shader(program == Program.Terrain ? "terrain.frag" : "placement.frag", SDL_GPU_SHADERSTAGE_FRAGMENT);
                blended = program == Program.Placement;
                break;
            case Program.Surface:
            case Program.Avatar:
                vertex = Shader("avatar.vert", SDL_GPU_SHADERSTAGE_VERTEX, storageBuffers: 1, uniforms: 1);
                fragment = Shader(program == Program.Surface ? "surface.frag" : "avatar.frag", SDL_GPU_SHADERSTAGE_FRAGMENT, samplers: 1);
                break;
            case Program.AvatarMask:
                vertex = Shader("avatar.vert", SDL_GPU_SHADERSTAGE_VERTEX, storageBuffers: 1, uniforms: 1);
                fragment = Shader("avatarMask.frag", SDL_GPU_SHADERSTAGE_FRAGMENT, samplers: 1);
                blended = false;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(program), program, null);
        }

        var color = new SDL_GPUColorTargetDescription
        {
            format = program == Program.AvatarMask ? MaskFormat : ColorFormat,
            blend_state = depth == DepthMode.MeshDepthPrepass
                ? new SDL_GPUColorTargetBlendState
                {
                    enable_blend = 0,
                    enable_color_write_mask = 1,
                    color_write_mask = 0,
                }
                : new SDL_GPUColorTargetBlendState
                {
                    enable_blend = blended ? (byte)1 : (byte)0,
                    src_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE,
                    dst_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
                    color_blend_op = SDL_GPU_BLENDOP_ADD,
                    src_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE,
                    dst_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
                    alpha_blend_op = SDL_GPU_BLENDOP_ADD,
                },
        };
        if (multiplied && depth != DepthMode.MeshDepthPrepass)
        {
            // Double modulate blend: ambient is written at half scale so lamps brighten.
            color.blend_state.src_color_blendfactor = SDL_GPU_BLENDFACTOR_DST_COLOR;
            color.blend_state.dst_color_blendfactor = SDL_GPU_BLENDFACTOR_SRC_COLOR;
            color.blend_state.src_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ZERO;
            color.blend_state.dst_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
        }

        bool noDepth = multiplied
            || program == Program.Atmosphere
            || program == Program.Season
            || program == Program.Trail
            || program == Program.Censor;

        int targetCount = program == Program.Season ? 2 : 1;
        SDL_GPUColorTargetDescription* targets = stackalloc SDL_GPUColorTargetDescription[2];
        targets[0] = color;
        if (program == Program.Season)
        {
            // Writes snow surface positions for the ambient pass to evaluate lighting.
            targets[1] = new SDL_GPUColorTargetDescription { format = LiftFormat };
        }

        var info = new SDL_GPUGraphicsPipelineCreateInfo
        {
            vertex_shader = vertex,
            fragment_shader = fragment,
            primitive_type = program == Program.HeightScatter ? SDL_GPU_PRIMITIVETYPE_POINTLIST : SDL_GPU_PRIMITIVETYPE_TRIANGLELIST,
            rasterizer_state = new SDL_GPURasterizerState
            {
                fill_mode = SDL_GPU_FILLMODE_FILL,
                cull_mode = SDL_GPU_CULLMODE_NONE,
            },
            multisample_state = new SDL_GPUMultisampleState
            {
                sample_count = sampleCount,
            },
            depth_stencil_state = new SDL_GPUDepthStencilState
            {
                enable_depth_test = !noDepth ? (byte)1 : (byte)0,
                // Mask and translucent sprite-edge passes read depth without writing to it.
                enable_depth_write = (depth is DepthMode.Mesh or DepthMode.MeshDepthPrepass && !noDepth && program != Program.AvatarMask) ? (byte)1 : (byte)0,
                compare_op = depth == DepthMode.Flat ? SDL_GPU_COMPAREOP_ALWAYS
                    : depth == DepthMode.MeshEqual ? SDL_GPU_COMPAREOP_EQUAL
                    : SDL_GPU_COMPAREOP_LESS_OR_EQUAL,
            },
            target_info = new SDL_GPUGraphicsPipelineTargetInfo
            {
                color_target_descriptions = depthOnly ? nint.Zero : (nint)targets,
                num_color_targets = depthOnly ? 0u : (uint)targetCount,
                depth_stencil_format = DepthFormat,
                has_depth_stencil_target = !noDepth ? (byte)1 : (byte)0,
            },
        };
        if (depthOnly)
        {
            info.depth_stencil_state.enable_depth_write = 1;
            info.depth_stencil_state.compare_op = SDL_GPU_COMPAREOP_LESS_OR_EQUAL;
        }

        nint created = SDL_CreateGPUGraphicsPipeline(Device, in info);
        if (created == nint.Zero)
            throw new SDLError($"SDL_CreateGPUGraphicsPipeline {key}");
        _pipelines[key] = created;
        return created;
    }

    public static uint CalcMipLevels(int width, int height) =>
        (uint)(1 + BitOperations.Log2((uint)Math.Max(1, Math.Max(width, height))));

    public GPUTexture MakeTexture(
        int width,
        int height,
        uint format,
        uint usage,
        uint numLevels = 1,
        int sampleCount = SDL_GPU_SAMPLECOUNT_1)
    {
        uint clampedLevels = Math.Max(1u, numLevels);
        var info = new SDL_GPUTextureCreateInfo
        {
            type = SDL_GPU_TEXTURETYPE_2D,
            format = format,
            usage = usage,
            width = (uint)Math.Max(1, width),
            height = (uint)Math.Max(1, height),
            layer_count_or_depth = 1,
            num_levels = clampedLevels,
            sample_count = sampleCount,
        };
        nint texture = SDL_CreateGPUTexture(Device, in info);
        if (texture == nint.Zero)
            throw new SDLError("SDL_CreateGPUTexture");
        return new GPUTexture(texture, width, height, format, _owner, numLevels: clampedLevels, sampleCount: sampleCount);
    }

    /// <summary>Uploads raw linear RGBA bytes for data maps without sRGB decoding.</summary>
    public GPUTexture UploadDataTexture(int width, int height, ReadOnlySpan<byte> pixels)
    {
        GPUTexture texture = MakeTexture(width, height, SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, SDL_GPU_TEXTUREUSAGE_SAMPLER);
        try
        {
            byte[] copyBytes = pixels.ToArray();
            ReadOnlyMemory<byte>[] chunks = [copyBytes];
            Upload(chunks, (copy, transfer, offsets) =>
            {
                var source = new SDL_GPUTextureTransferInfo
                {
                    transfer_buffer = transfer,
                    offset = offsets[0],
                    pixels_per_row = (uint)width,
                    rows_per_layer = (uint)height,
                };
                var region = new SDL_GPUTextureRegion
                {
                    texture = texture.Handle,
                    w = (uint)width,
                    h = (uint)height,
                    d = 1,
                };
                SDL_UploadToGPUTexture(copy, in source, in region, false);
            });
            return texture;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    public List<GPUTexture> UploadTextures(IReadOnlyList<(int Width, int Height, bool Depth8, byte[] Pixels)> images) =>
        UploadTextures(images, ReadOnlySpan<bool>.Empty);

    public List<GPUTexture> UploadTextures(
        IReadOnlyList<(int Width, int Height, bool Depth8, byte[] Pixels)> images,
        ReadOnlySpan<bool> mipmapped)
    {
        var result = new List<GPUTexture>(images.Count);
        if (images.Count == 0)
            return result;

        try
        {
            var chunks = new List<ReadOnlyMemory<byte>>(images.Count);
            var uploads = new List<(int TexIndex, uint MipLevel, int Width, int Height, int ChunkIndex)>(images.Count);

            for (int i = 0; i < images.Count; i++)
            {
                var image = images[i];
                bool wantMips = !image.Depth8
                    && i < mipmapped.Length
                    && mipmapped[i]
                    && (image.Width > 1 || image.Height > 1);
                uint numLevels = wantMips ? CalcMipLevels(image.Width, image.Height) : 1u;

                result.Add(MakeTexture(
                    image.Width,
                    image.Height,
                    image.Depth8 ? SDL_GPU_TEXTUREFORMAT_R8_UNORM : ColorFormat,
                    SDL_GPU_TEXTUREUSAGE_SAMPLER,
                    numLevels: numLevels));

                int levelWidth = image.Width;
                int levelHeight = image.Height;
                byte[] currentPixels = image.Pixels;
                int baseChunkIndex = chunks.Count;
                chunks.Add(currentPixels);
                uploads.Add((i, 0u, levelWidth, levelHeight, baseChunkIndex));

                for (uint level = 1; level < numLevels; level++)
                {
                    currentPixels = SimsSDL.Core.SpritePixels.DownsamplePremultipliedSrgbBox(currentPixels, levelWidth, levelHeight);
                    levelWidth = Math.Max(1, levelWidth >> 1);
                    levelHeight = Math.Max(1, levelHeight >> 1);
                    int chunkIndex = chunks.Count;
                    chunks.Add(currentPixels);
                    uploads.Add((i, level, levelWidth, levelHeight, chunkIndex));
                }
            }

            Upload(CollectionsMarshal.AsSpan(chunks), (copy, transfer, offsets) =>
            {
                foreach (var item in uploads)
                {
                    var source = new SDL_GPUTextureTransferInfo
                    {
                        transfer_buffer = transfer,
                        offset = offsets[item.ChunkIndex],
                        pixels_per_row = (uint)item.Width,
                        rows_per_layer = (uint)item.Height,
                    };
                    var region = new SDL_GPUTextureRegion
                    {
                        texture = result[item.TexIndex].Handle,
                        mip_level = item.MipLevel,
                        w = (uint)item.Width,
                        h = (uint)item.Height,
                        d = 1,
                    };
                    SDL_UploadToGPUTexture(copy, in source, in region, false);
                }
            });
            return result;
        }
        catch
        {
            foreach (GPUTexture texture in result)
                texture.Dispose();
            throw;
        }
    }

    public GPUBuffer? MakeStorageBuffer<TElement>(List<TElement> elements)
        where TElement : unmanaged
    {
        ArgumentNullException.ThrowIfNull(elements);
        return MakeStorageBuffer<TElement>(CollectionsMarshal.AsSpan(elements));
    }

    public GPUBuffer? MakeStorageBuffer<TElement>(TElement[] elements)
        where TElement : unmanaged
    {
        ArgumentNullException.ThrowIfNull(elements);
        return MakeStorageBuffer<TElement>(elements.AsSpan());
    }

    public GPUBuffer? MakeStorageBuffer<TElement>(IReadOnlyList<TElement> elements)
        where TElement : unmanaged
    {
        ArgumentNullException.ThrowIfNull(elements);
        if (elements.Count == 0)
            return null;
        if (elements is List<TElement> list)
            return MakeStorageBuffer<TElement>(CollectionsMarshal.AsSpan(list));
        if (elements is TElement[] array)
            return MakeStorageBuffer<TElement>(array.AsSpan());
        var copy = new TElement[elements.Count];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = elements[i];
        return MakeStorageBuffer<TElement>(copy.AsSpan());
    }

    public unsafe GPUBuffer? MakeStorageBuffer<TElement>(ReadOnlySpan<TElement> elements)
        where TElement : unmanaged
    {
        if (elements.IsEmpty)
            return null;

        ReadOnlySpan<byte> rawBytes = MemoryMarshal.AsBytes(elements);
        byte[] data = rawBytes.ToArray();
        var info = new SDL_GPUBufferCreateInfo
        {
            usage = SDL_GPU_BUFFERUSAGE_GRAPHICS_STORAGE_READ,
            size = (uint)data.Length,
        };
        nint buffer = SDL_CreateGPUBuffer(Device, in info);
        if (buffer == nint.Zero)
            throw new SDLError("SDL_CreateGPUBuffer");

        var result = new GPUBuffer(buffer, elements.Length, _owner, data.Length);
        try
        {
            UploadBufferBytes(result, rawBytes, cycle: false);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public unsafe GPUBuffer MakeStorageBufferCapacity<TElement>(int capacity)
        where TElement : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        long totalBytes = checked((long)capacity * sizeof(TElement));
        var info = new SDL_GPUBufferCreateInfo
        {
            usage = SDL_GPU_BUFFERUSAGE_GRAPHICS_STORAGE_READ,
            size = checked((uint)totalBytes),
        };
        nint buffer = SDL_CreateGPUBuffer(Device, in info);
        if (buffer == nint.Zero)
            throw new SDLError("SDL_CreateGPUBuffer");
        return new GPUBuffer(buffer, capacity, _owner, totalBytes);
    }

    public unsafe void UploadIntoBuffer<TElement>(GPUBuffer target, ReadOnlySpan<TElement> elements)
        where TElement : unmanaged
    {
        ArgumentNullException.ThrowIfNull(target);
        if (elements.IsEmpty)
            return;

        ReadOnlySpan<byte> rawBytes = MemoryMarshal.AsBytes(elements);
        if (rawBytes.Length > target.ByteSize)
            throw new ArgumentOutOfRangeException(nameof(elements), $"Upload of {rawBytes.Length} bytes exceeds buffer size {target.ByteSize}.");

        UploadBufferBytes(target, rawBytes, cycle: false);
    }

    private unsafe void UploadBufferBytes(GPUBuffer target, ReadOnlySpan<byte> rawBytes, bool cycle)
    {
        if (rawBytes.IsEmpty)
            return;

        int allocSize = Math.Max(4, (rawBytes.Length + 3) & ~3);
        var transferInfo = new SDL_GPUTransferBufferCreateInfo
        {
            usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
            size = (uint)allocSize,
        };
        nint transfer = SDL_CreateGPUTransferBuffer(Device, in transferInfo);
        if (transfer == nint.Zero)
            throw new SDLError("SDL_CreateGPUTransferBuffer");

        try
        {
            nint mapped = SDL_MapGPUTransferBuffer(Device, transfer, false);
            if (mapped == nint.Zero)
                throw new SDLError("SDL_MapGPUTransferBuffer");
            try
            {
                fixed (byte* srcPtr = rawBytes)
                {
                    Buffer.MemoryCopy(srcPtr, (void*)mapped, allocSize, rawBytes.Length);
                }
            }
            finally
            {
                SDL_UnmapGPUTransferBuffer(Device, transfer);
            }

            nint targetHandle = target.Handle;
            uint byteLength = (uint)rawBytes.Length;
            void Record(nint copy)
            {
                var source = new SDL_GPUTransferBufferLocation
                {
                    transfer_buffer = transfer,
                    offset = 0,
                };
                var region = new SDL_GPUBufferRegion
                {
                    buffer = targetHandle,
                    offset = 0,
                    size = byteLength,
                };
                SDL_UploadToGPUBuffer(copy, in source, in region, cycle);
            }

            if (EnsureBatchCopyPass() is var batchCopy && batchCopy != nint.Zero)
            {
                Record(batchCopy);
                return;
            }

            nint command = SDL_AcquireGPUCommandBuffer(Device);
            if (command == nint.Zero)
                throw new SDLError("SDL_AcquireGPUCommandBuffer");
            nint copy = SDL_BeginGPUCopyPass(command);
            if (copy == nint.Zero)
            {
                _ = SDL_SubmitGPUCommandBuffer(command);
                throw new SDLError("SDL_BeginGPUCopyPass");
            }
            Record(copy);
            SDL_EndGPUCopyPass(copy);
            if (!SDL_SubmitGPUCommandBuffer(command))
                throw new SDLError("SDL_SubmitGPUCommandBuffer");
        }
        finally
        {
            SDL_ReleaseGPUTransferBuffer(Device, transfer);
        }
    }

    private nint EnsureBatchCopyPass()
    {
        if (_batchCopy != nint.Zero)
            return _batchCopy;
        if (_batchCommand == nint.Zero)
            return nint.Zero;
        nint copy = SDL_BeginGPUCopyPass(_batchCommand);
        if (copy == nint.Zero)
            throw new SDLError("SDL_BeginGPUCopyPass");
        _batchCopy = copy;
        return copy;
    }

    private unsafe void Upload(ReadOnlySpan<ReadOnlyMemory<byte>> chunks, Action<nint, nint, uint[]> encode)
    {
        uint[] offsets = new uint[chunks.Length];
        int total = 0;
        for (int i = 0; i < chunks.Length; i++)
        {
            offsets[i] = (uint)total;
            total = checked(total + ((chunks[i].Length + 3) & ~3));
        }

        int allocSize = Math.Max(4, total);
        var info = new SDL_GPUTransferBufferCreateInfo
        {
            usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
            size = (uint)allocSize,
        };
        nint transfer = SDL_CreateGPUTransferBuffer(Device, in info);
        if (transfer == nint.Zero)
            throw new SDLError("SDL_CreateGPUTransferBuffer");

        try
        {
            nint mapped = SDL_MapGPUTransferBuffer(Device, transfer, false);
            if (mapped == nint.Zero)
                throw new SDLError("SDL_MapGPUTransferBuffer");
            try
            {
                byte* dstBase = (byte*)mapped;
                for (int i = 0; i < chunks.Length; i++)
                {
                    ReadOnlySpan<byte> span = chunks[i].Span;
                    if (span.IsEmpty)
                        continue;
                    fixed (byte* srcPtr = span)
                    {
                        Buffer.MemoryCopy(srcPtr, dstBase + offsets[i], allocSize - offsets[i], span.Length);
                    }
                }
            }
            finally
            {
                SDL_UnmapGPUTransferBuffer(Device, transfer);
            }

            if (EnsureBatchCopyPass() is var batchCopy && batchCopy != nint.Zero)
            {
                encode(batchCopy, transfer, offsets);
                return;
            }

            nint command = SDL_AcquireGPUCommandBuffer(Device);
            if (command == nint.Zero)
                throw new SDLError("SDL_AcquireGPUCommandBuffer");
            nint copy = SDL_BeginGPUCopyPass(command);
            if (copy == nint.Zero)
            {
                _ = SDL_SubmitGPUCommandBuffer(command);
                throw new SDLError("SDL_BeginGPUCopyPass");
            }
            encode(copy, transfer, offsets);
            SDL_EndGPUCopyPass(copy);
            if (!SDL_SubmitGPUCommandBuffer(command))
                throw new SDLError("SDL_SubmitGPUCommandBuffer");
        }
        finally
        {
            SDL_ReleaseGPUTransferBuffer(Device, transfer);
        }
    }

    public void BatchingUploads(Action body) => BatchingUploads(nint.Zero, body);

    public void BatchingUploads(nint command, Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        BatchingUploads<byte>(command, () =>
        {
            body();
            return 0;
        });
    }

    public TResult BatchingUploads<TResult>(Func<TResult> body) => BatchingUploads(nint.Zero, body);

    /// <summary>Batches texture uploads within <paramref name="body"/> into a single copy pass, prepending
    /// it to <paramref name="command"/> when provided.</summary>
    public TResult BatchingUploads<TResult>(nint command, Func<TResult> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (_batchCommand != nint.Zero)
            return body();

        bool owned = command == nint.Zero;
        if (owned)
        {
            command = SDL_AcquireGPUCommandBuffer(Device);
            if (command == nint.Zero)
                throw new SDLError("SDL_AcquireGPUCommandBuffer");
        }

        _batchCommand = command;
        try
        {
            return body();
        }
        finally
        {
            _batchCommand = nint.Zero;
            nint copy = _batchCopy;
            _batchCopy = nint.Zero;
            if (copy != nint.Zero)
                SDL_EndGPUCopyPass(copy);
            if (owned)
                _ = SDL_SubmitGPUCommandBuffer(command);
        }
    }

    public unsafe byte[] ReadPixels(GPUTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        int size = checked(texture.Width * texture.Height * 4);
        if (size <= 0)
            return [];

        var info = new SDL_GPUTransferBufferCreateInfo
        {
            usage = SDL_GPU_TRANSFERBUFFERUSAGE_DOWNLOAD,
            size = (uint)size,
        };
        nint transfer = SDL_CreateGPUTransferBuffer(Device, in info);
        if (transfer == nint.Zero)
            throw new SDLError("SDL_CreateGPUTransferBuffer");

        try
        {
            nint command = SDL_AcquireGPUCommandBuffer(Device);
            if (command == nint.Zero)
                throw new SDLError("readback");
            nint copy = SDL_BeginGPUCopyPass(command);
            if (copy == nint.Zero)
            {
                _ = SDL_SubmitGPUCommandBuffer(command);
                throw new SDLError("readback");
            }

            var region = new SDL_GPUTextureRegion
            {
                texture = texture.Handle,
                w = (uint)texture.Width,
                h = (uint)texture.Height,
                d = 1,
            };
            var destination = new SDL_GPUTextureTransferInfo
            {
                transfer_buffer = transfer,
                offset = 0,
                pixels_per_row = (uint)texture.Width,
                rows_per_layer = (uint)texture.Height,
            };
            SDL_DownloadFromGPUTexture(copy, in region, in destination);
            SDL_EndGPUCopyPass(copy);

            nint fence = SDL_SubmitGPUCommandBufferAndAcquireFence(command);
            if (fence == nint.Zero)
                throw new SDLError("SDL_SubmitGPUCommandBufferAndAcquireFence");
            nint fenceSlot = fence;
            _ = SDL_WaitForGPUFences(Device, true, &fenceSlot, 1);
            SDL_ReleaseGPUFence(Device, fence);

            nint mapped = SDL_MapGPUTransferBuffer(Device, transfer, false);
            if (mapped == nint.Zero)
                throw new SDLError("SDL_MapGPUTransferBuffer");
            try
            {
                byte[] result = new byte[size];
                Marshal.Copy(mapped, result, 0, size);
                return result;
            }
            finally
            {
                SDL_UnmapGPUTransferBuffer(Device, transfer);
            }
        }
        finally
        {
            SDL_ReleaseGPUTransferBuffer(Device, transfer);
        }
    }

    private void ReleaseNative()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        nint dev = _owner.Handle;
        if (dev != nint.Zero)
        {
            foreach (nint pipeline in _pipelines.Values)
                SDL_ReleaseGPUGraphicsPipeline(dev, pipeline);
            foreach (nint shader in _shaders.Values)
                SDL_ReleaseGPUShader(dev, shader);
            if (Sampler != nint.Zero)
                SDL_ReleaseGPUSampler(dev, Sampler);
            if (RoofSampler != nint.Zero)
                SDL_ReleaseGPUSampler(dev, RoofSampler);
            if (AnisoSampler != nint.Zero)
                SDL_ReleaseGPUSampler(dev, AnisoSampler);
            if (PixelSampler != nint.Zero)
                SDL_ReleaseGPUSampler(dev, PixelSampler);
        }

        _pipelines.Clear();
        _shaders.Clear();
        Sampler = nint.Zero;
        RoofSampler = nint.Zero;
        AnisoSampler = nint.Zero;
        PixelSampler = nint.Zero;

        _owner.ReleaseRef();
        _owner.Dispose();
    }

    public void Dispose()
    {
        ReleaseNative();
        GC.SuppressFinalize(this);
    }

    ~GPUContext() => ReleaseNative();
}
