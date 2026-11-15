using System.Runtime.InteropServices;
using SimsSDL;
using SimsSDL.Interop;

namespace SimsEngine.Tests.Client;

public sealed class Sdl3AbiLayoutTests
{
    [Fact]
    public unsafe void Sdl3NativeStructs_MatchVerifiedCApiSizes()
    {
        Assert.Equal(168, sizeof(Sdl3Native.SDL_GPUGraphicsPipelineCreateInfo));
        Assert.Equal(24, sizeof(Sdl3Native.SDL_GPUGraphicsPipelineTargetInfo));
        Assert.Equal(44, sizeof(Sdl3Native.SDL_GPUDepthStencilState));
        Assert.Equal(16, sizeof(Sdl3Native.SDL_GPUStencilOpState));
        Assert.Equal(28, sizeof(Sdl3Native.SDL_GPURasterizerState));
        Assert.Equal(12, sizeof(Sdl3Native.SDL_GPUMultisampleState));
        Assert.Equal(32, sizeof(Sdl3Native.SDL_GPUVertexInputState));
        Assert.Equal(16, sizeof(Sdl3Native.SDL_GPUVertexBufferDescription));
        Assert.Equal(16, sizeof(Sdl3Native.SDL_GPUVertexAttribute));
        Assert.Equal(36, sizeof(Sdl3Native.SDL_GPUColorTargetDescription));
        Assert.Equal(32, sizeof(Sdl3Native.SDL_GPUColorTargetBlendState));
        Assert.Equal(56, sizeof(Sdl3Native.SDL_GPUShaderCreateInfo));
        Assert.Equal(52, sizeof(Sdl3Native.SDL_GPUSamplerCreateInfo));
        Assert.Equal(36, sizeof(Sdl3Native.SDL_GPUTextureCreateInfo));
        Assert.Equal(12, sizeof(Sdl3Native.SDL_GPUBufferCreateInfo));
        Assert.Equal(12, sizeof(Sdl3Native.SDL_GPUTransferBufferCreateInfo));
        Assert.Equal(64, sizeof(Sdl3Native.SDL_GPUColorTargetInfo));
        Assert.Equal(32, sizeof(Sdl3Native.SDL_GPUDepthStencilTargetInfo));
        Assert.Equal(96, sizeof(Sdl3Native.SDL_GPUBlitInfo));
        Assert.Equal(128, sizeof(Sdl3Native.SDL_Event));
        Assert.Equal(56, sizeof(Sdl3Native.SDL_MessageBoxData));
        Assert.Equal(16, sizeof(Sdl3Native.SDL_MessageBoxButtonData));
    }

    [Fact]
    public void Sdl3NativeStructs_MatchCriticalFieldOffsets()
    {
        Assert.Equal(0, Marshal.OffsetOf<Sdl3Native.SDL_Event>(nameof(Sdl3Native.SDL_Event.type)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<Sdl3Native.SDL_Event>(nameof(Sdl3Native.SDL_Event.key_scancode)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<Sdl3Native.SDL_Event>(nameof(Sdl3Native.SDL_Event.key_key)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<Sdl3Native.SDL_Event>(nameof(Sdl3Native.SDL_Event.key_mod)).ToInt32());
        Assert.Equal(36, Marshal.OffsetOf<Sdl3Native.SDL_Event>(nameof(Sdl3Native.SDL_Event.key_down)).ToInt32());
        Assert.Equal(37, Marshal.OffsetOf<Sdl3Native.SDL_Event>(nameof(Sdl3Native.SDL_Event.key_repeat)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<Sdl3Native.SDL_Event>(nameof(Sdl3Native.SDL_Event.motion_x)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<Sdl3Native.SDL_Event>(nameof(Sdl3Native.SDL_Event.motion_y)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<Sdl3Native.SDL_Event>(nameof(Sdl3Native.SDL_Event.button_button)).ToInt32());
        Assert.Equal(25, Marshal.OffsetOf<Sdl3Native.SDL_Event>(nameof(Sdl3Native.SDL_Event.button_down)).ToInt32());
        Assert.Equal(26, Marshal.OffsetOf<Sdl3Native.SDL_Event>(nameof(Sdl3Native.SDL_Event.button_clicks)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<Sdl3Native.SDL_Event>(nameof(Sdl3Native.SDL_Event.button_x)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<Sdl3Native.SDL_Event>(nameof(Sdl3Native.SDL_Event.button_y)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<Sdl3Native.SDL_Event>(nameof(Sdl3Native.SDL_Event.wheel_x)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<Sdl3Native.SDL_Event>(nameof(Sdl3Native.SDL_Event.wheel_y)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<Sdl3Native.SDL_Event>(nameof(Sdl3Native.SDL_Event.text_text)).ToInt32());

        Assert.Equal(92, Marshal.OffsetOf<Sdl3Native.SDL_GPUGraphicsPipelineCreateInfo>(
            nameof(Sdl3Native.SDL_GPUGraphicsPipelineCreateInfo.depth_stencil_state)).ToInt32());
        Assert.Equal(136, Marshal.OffsetOf<Sdl3Native.SDL_GPUGraphicsPipelineCreateInfo>(
            nameof(Sdl3Native.SDL_GPUGraphicsPipelineCreateInfo.target_info)).ToInt32());
    }

    [Fact]
    public unsafe void GpuShaderBufferStructs_MatchGlslAndMetalLayouts()
    {
        Assert.Equal(80, sizeof(Quad));
        Assert.Equal(48, sizeof(TerrainVertex));
        Assert.Equal(48, sizeof(AvatarVertex));
    }
}
