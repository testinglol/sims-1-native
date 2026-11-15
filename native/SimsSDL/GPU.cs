using System.Numerics;

namespace SimsSDL;

/// <summary>
/// Exception wrapping the latest SDL error string when a native SDL call fails.
/// </summary>
public sealed class SDLError : Exception
{
    public SDLError(string context)
        : base($"{context}: {SDL_GetError()}")
    {
    }
}

/// <summary>
/// Mirrors the Metal Quad and the std140 block in Shaders/sdl/quad.glsl: rect, canvas size + (flip, opacity),
/// uv rect, depth interval, and tint.
/// </summary>
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

/// <summary>
/// Mirrors the TerrainVertex storage buffer layout in Shaders/sdl/terrain.vert.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TerrainVertex
{
    public Vector2 Position;

    /// <summary>x: grass state 0-1 (green to brown); y unused.</summary>
    public Vector2 Grass;

    /// <summary>Fully lit grass colour and the darkest shade of its row, both sRGB 0-1.</summary>
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

/// <summary>
/// Mirrors the AvatarVertex storage buffer layout in Shaders/sdl/avatar.vert.
/// </summary>
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

/// <summary>
/// Owns the SDL GPU device with reference counting so textures and buffers release before the device is destroyed,
/// regardless of GC finalizer ordering.
/// </summary>
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

/// <summary>
/// Ref-counted wrapper around an SDL_GPUTexture.
/// </summary>
public sealed class GPUTexture : IDisposable
{
    private readonly GPUDevice _device;
    private nint _handle;
    private int _disposed;

    public nint Handle => Volatile.Read(ref _handle);
    public int Width { get; }
    public int Height { get; }
    public uint Format { get; }

    public GPUTexture(nint handle, int width, int height, uint format, GPUDevice device)
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
        _device = device;
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
        _device.ReleaseRef();
    }

    public void Dispose()
    {
        ReleaseNative();
        GC.SuppressFinalize(this);
    }

    ~GPUTexture() => ReleaseNative();
}

/// <summary>
/// Ref-counted wrapper around an SDL_GPUBuffer.
/// </summary>
public sealed class GPUBuffer : IDisposable
{
    private readonly GPUDevice _device;
    private nint _handle;
    private int _disposed;

    public nint Handle => Volatile.Read(ref _handle);
    public int Count { get; }

    public GPUBuffer(nint handle, int count, GPUDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (handle == nint.Zero)
            throw new ArgumentException("GPU buffer handle cannot be zero.", nameof(handle));
        if (!device.Retain())
            throw new ObjectDisposedException(nameof(GPUDevice));
        _handle = handle;
        Count = count;
        _device = device;
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
        _device.ReleaseRef();
    }

    public void Dispose()
    {
        ReleaseNative();
        GC.SuppressFinalize(this);
    }

    ~GPUBuffer() => ReleaseNative();
}

/// <summary>
/// The SDL_GPU equivalents of the Metal renderer's pipeline states, depth states and samplers. SDL_GPU folds the depth
/// state into the pipeline, so each shader pair is built once per depth mode it is drawn with.
/// </summary>
public sealed class GPUContext : IDisposable
{
    public enum DepthMode
    {
        Flat,
        Mesh,
        MeshEdge,
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
        /// <summary>
        /// The depth-sprite ambient occlusion and sun shadow pass, written to a target of its own (or, when debugging,
        /// over the lot); <see cref="AmbientBlur"/> smooths it and multiplies it into the lot's colour at twice its value.
        /// </summary>
        Ambient,
        AmbientBlur,
        /// <summary>The rain or snow and the sky in puddles, blended over the lit lot with no depth.</summary>
        Atmosphere,
        /// <summary>
        /// Autumn or winter over the lot's unlit colour (from a copy of it), before the ambient light: turned leaves,
        /// fallen leaves and snow; a second target takes how far each pixel's lot point rises to the snow's surface.
        /// </summary>
        Season,
        /// <summary>Where feet have pressed the snow: the last frame's trail, filled in a little, with the feet stamped over it.</summary>
        Trail,
        /// <summary>The terrain's depth alone, into a depth target of its own.</summary>
        GroundDepth,
        /// <summary>The lot's depth dropped onto a top-down height map of the lot, one point per sampled pixel.</summary>
        HeightScatter,
        /// <summary>Where Sims show in front of the lot's depth, as 1 in a mask of its own, so the season pass leaves them alone.</summary>
        AvatarMask,
        /// <summary>Sims.exe cLotRenderer::DrawCensorRects (004ac916.c / 004ac510.c): pixelates naked Sims behind foreground objects/walls.</summary>
        Censor,
    }

    public const uint ColorFormat = SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM_SRGB;

    /// <summary>The season pass's second target: how far each pixel's lot point moves to the snow's surface.</summary>
    public const uint LiftFormat = SDL_GPU_TEXTUREFORMAT_R16G16B16A16_FLOAT;

    public const uint DepthFormat = SDL_GPU_TEXTUREFORMAT_D32_FLOAT;

    /// <summary>The avatar mask's target: 1 where a Sim was drawn.</summary>
    public const uint MaskFormat = SDL_GPU_TEXTUREFORMAT_R8_UNORM;

    private readonly GPUDevice _owner;
    private readonly Dictionary<string, nint> _shaders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, nint> _pipelines = new(StringComparer.Ordinal);
    private nint _batchCopy;
    private int _disposed;

    public nint Device => _owner.Handle;
    public nint Sampler { get; private set; }
    public nint RoofSampler { get; private set; }

    /// <summary>Nearest sampling for art drawn at a whole number of screen pixels per texel, so zoomed sprites stay sharp.</summary>
    public nint PixelSampler { get; private set; }

    public Dictionary<string, string> EffectShaders { get; set; } = new(StringComparer.Ordinal);

    public nint SpriteDepthPipeline => Pipeline(Program.SpriteDepth, DepthMode.Mesh);
    public nint SpriteDepthEdgePipeline => Pipeline(Program.SpriteDepth, DepthMode.MeshEdge);

    public GPUContext()
    {
        nint device = SDL_CreateGPUDevice(SDL_GPU_SHADERFORMAT_SPIRV | SDL_GPU_SHADERFORMAT_MSL, false, null);
        if (device == nint.Zero)
            throw new SDLError("SDL_CreateGPUDevice");

        _owner = new GPUDevice(device);
        // Keep an extra ref on _owner so finalizer-order release of pipelines/shaders/samplers always precedes SDL_DestroyGPUDevice.
        _owner.Retain();
        try
        {
            Sampler = MakeSampler(SDL_GPU_FILTER_LINEAR, SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE);
            RoofSampler = MakeSampler(SDL_GPU_FILTER_LINEAR, SDL_GPU_SAMPLERADDRESSMODE_REPEAT);
            PixelSampler = MakeSampler(SDL_GPU_FILTER_NEAREST, SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE);
        }
        catch
        {
            ReleaseNative();
            GC.SuppressFinalize(this);
            throw;
        }
    }

    private nint MakeSampler(int filter, int address)
    {
        var info = new SDL_GPUSamplerCreateInfo
        {
            min_filter = filter,
            mag_filter = filter,
            mipmap_mode = SDL_GPU_SAMPLERMIPMAPMODE_NEAREST,
            address_mode_u = address,
            address_mode_v = address,
            address_mode_w = address,
        };
        nint sampler = SDL_CreateGPUSampler(Device, in info);
        if (sampler == nint.Zero)
            throw new SDLError("SDL_CreateGPUSampler");
        return sampler;
    }

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

        bool metal = (SDL_GetGPUShaderFormats(Device) & SDL_GPU_SHADERFORMAT_MSL) != 0;
        if (string.IsNullOrEmpty(source))
            throw new ContentError("SDL shaders are missing");

        string path = source + (metal ? ".msl" : ".spv");
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
                format = metal ? SDL_GPU_SHADERFORMAT_MSL : SDL_GPU_SHADERFORMAT_SPIRV,
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

    public unsafe nint Pipeline(Program program, DepthMode depth)
    {
        string effectPart = EffectShaders.Count == 0
            ? string.Empty
            : string.Join("|", EffectShaders.OrderBy(static kv => kv.Key, StringComparer.Ordinal).Select(static kv => kv.Value));
        string key = $"{program}-{depth}-{effectPart}";
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
            blend_state = new SDL_GPUColorTargetBlendState
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
        if (multiplied)
        {
            // Double modulate, src*dst + dst*src: the ambient term is written at half scale so lamps can brighten.
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
            // The season pass also writes where the snow's surface lies, for the ambient pass to light it there.
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
            depth_stencil_state = new SDL_GPUDepthStencilState
            {
                enable_depth_test = !noDepth ? (byte)1 : (byte)0,
                // The mask and translucent sprite-edge pass test against depth without changing it.
                enable_depth_write = (depth == DepthMode.Mesh && !noDepth && program != Program.AvatarMask) ? (byte)1 : (byte)0,
                compare_op = depth == DepthMode.Flat ? SDL_GPU_COMPAREOP_ALWAYS : SDL_GPU_COMPAREOP_LESS_OR_EQUAL,
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

    public GPUTexture MakeTexture(int width, int height, uint format, uint usage)
    {
        var info = new SDL_GPUTextureCreateInfo
        {
            type = SDL_GPU_TEXTURETYPE_2D,
            format = format,
            usage = usage,
            width = (uint)Math.Max(1, width),
            height = (uint)Math.Max(1, height),
            layer_count_or_depth = 1,
            num_levels = 1,
            sample_count = SDL_GPU_SAMPLECOUNT_1,
        };
        nint texture = SDL_CreateGPUTexture(Device, in info);
        if (texture == nint.Zero)
            throw new SDLError("SDL_CreateGPUTexture");
        return new GPUTexture(texture, width, height, format, _owner);
    }

    /// <summary>Linear RGBA bytes for data maps; colour uploads use sRGB decoding.</summary>
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

    /// <summary>Creates sampled textures from tightly packed pixels (4 bytes per pixel for colour, 1 for depth8) in one copy pass.</summary>
    public List<GPUTexture> UploadTextures(IReadOnlyList<(int Width, int Height, bool Depth8, byte[] Pixels)> images)
    {
        var result = new List<GPUTexture>(images.Count);
        if (images.Count == 0)
            return result;

        try
        {
            var chunks = new ReadOnlyMemory<byte>[images.Count];
            for (int i = 0; i < images.Count; i++)
            {
                var image = images[i];
                result.Add(MakeTexture(
                    image.Width,
                    image.Height,
                    image.Depth8 ? SDL_GPU_TEXTUREFORMAT_R8_UNORM : ColorFormat,
                    SDL_GPU_TEXTUREUSAGE_SAMPLER));
                chunks[i] = image.Pixels;
            }

            Upload(chunks, (copy, transfer, offsets) =>
            {
                for (int i = 0; i < images.Count; i++)
                {
                    var image = images[i];
                    var source = new SDL_GPUTextureTransferInfo
                    {
                        transfer_buffer = transfer,
                        offset = offsets[i],
                        pixels_per_row = (uint)image.Width,
                        rows_per_layer = (uint)image.Height,
                    };
                    var region = new SDL_GPUTextureRegion
                    {
                        texture = result[i].Handle,
                        w = (uint)image.Width,
                        h = (uint)image.Height,
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

        var result = new GPUBuffer(buffer, elements.Length, _owner);
        try
        {
            ReadOnlyMemory<byte>[] chunks = [data];
            Upload(chunks, (copy, transfer, _) =>
            {
                var source = new SDL_GPUTransferBufferLocation
                {
                    transfer_buffer = transfer,
                    offset = 0,
                };
                var region = new SDL_GPUBufferRegion
                {
                    buffer = buffer,
                    offset = 0,
                    size = (uint)data.Length,
                };
                SDL_UploadToGPUBuffer(copy, in source, in region, false);
            });
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
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

            if (_batchCopy != nint.Zero)
            {
                encode(_batchCopy, transfer, offsets);
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

    /// <summary>
    /// Records every upload made in <paramref name="body"/> into one copy pass and command buffer. With a non-zero
    /// <paramref name="command"/>, the copy pass goes into that command buffer (ahead of its other passes) and the caller submits it.
    /// </summary>
    public TResult BatchingUploads<TResult>(nint command, Func<TResult> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (_batchCopy != nint.Zero)
            return body();

        bool owned = command == nint.Zero;
        if (owned)
        {
            command = SDL_AcquireGPUCommandBuffer(Device);
            if (command == nint.Zero)
                throw new SDLError("SDL_AcquireGPUCommandBuffer");
        }

        nint copy = SDL_BeginGPUCopyPass(command);
        if (copy == nint.Zero)
        {
            if (owned)
                _ = SDL_SubmitGPUCommandBuffer(command);
            throw new SDLError("SDL_BeginGPUCopyPass");
        }

        _batchCopy = copy;
        try
        {
            return body();
        }
        finally
        {
            _batchCopy = nint.Zero;
            SDL_EndGPUCopyPass(copy);
            if (owned)
                _ = SDL_SubmitGPUCommandBuffer(command);
        }
    }

    /// <summary>Reads a colour target back as tightly packed RGBA bytes (the sRGB-encoded values the target stores).</summary>
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
            if (PixelSampler != nint.Zero)
                SDL_ReleaseGPUSampler(dev, PixelSampler);
        }

        _pipelines.Clear();
        _shaders.Clear();
        Sampler = nint.Zero;
        RoofSampler = nint.Zero;
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
