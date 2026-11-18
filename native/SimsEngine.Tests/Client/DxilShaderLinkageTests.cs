using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using SimsSDL;
using static SimsSDL.Interop.Sdl3Native;

namespace SimsEngine.Tests.Client;

public sealed class DxilShaderLinkageTests
{
    private static readonly (GPUContext.Program Program, string Vert, string Frag)[] CorePipelines =
    [
        (GPUContext.Program.Sprite, "sprite.vert", "sprite.frag"),
        (GPUContext.Program.Ghost, "sprite.vert", "ghost.frag"),
        (GPUContext.Program.SpriteDepth, "sprite.vert", "spriteDepth.frag"),
        (GPUContext.Program.Censor, "sprite.vert", "censor.frag"),
        (GPUContext.Program.Terrain, "terrain.vert", "terrain.frag"),
        (GPUContext.Program.Placement, "terrain.vert", "placement.frag"),
        (GPUContext.Program.Avatar, "avatar.vert", "avatar.frag"),
        (GPUContext.Program.Surface, "avatar.vert", "surface.frag"),
        (GPUContext.Program.AvatarMask, "avatar.vert", "avatarMask.frag"),
    ];

    private readonly record struct DxilSigElement(
        uint Register,
        string SemanticName,
        uint SemanticIndex,
        uint SystemValue,
        uint ComponentType,
        byte Mask);

    private static string LocateShadersDirectory()
    {
        var dir = AppContext.BaseDirectory;
        for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(dir); depth++)
        {
            string repoShaders = Path.Combine(dir, "native", "SimsSDL", "Shaders");
            if (Directory.Exists(repoShaders))
            {
                return repoShaders;
            }

            dir = Path.GetDirectoryName(dir);
        }

        string publishedShaders = Path.Combine(AppContext.BaseDirectory, "Shaders");
        if (Directory.Exists(publishedShaders))
        {
            return publishedShaders;
        }

        throw new DirectoryNotFoundException("Could not locate native/SimsSDL/Shaders directory.");
    }

    [Fact]
    public void AllCorePipelinePairs_HaveMatchingHlslStageLinkage()
    {
        string shadersDir = LocateShadersDirectory();
        string hlslDir = Path.Combine(shadersDir, "hlsl");
        Assert.True(Directory.Exists(hlslDir), $"Missing HLSL directory at {hlslDir}");

        static List<(string Interp, string Type, string Semantic)> ParseStruct(string hlsl, string structName)
        {
            var structMatch = Regex.Match(
                hlsl,
                $@"struct\s+{Regex.Escape(structName)}\s*\{{(.*?)\}};",
                RegexOptions.Singleline);
            Assert.True(structMatch.Success, $"Missing {structName} in HLSL");

            var list = new List<(string, string, string)>();
            foreach (string rawLine in structMatch.Groups[1].Value.Split('\n'))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                var fieldMatch = Regex.Match(line, @"^(nointerpolation\s+)?(\w+)\s+\w+\s*:\s*([A-Za-z0-9_]+)\s*;$");
                Assert.True(fieldMatch.Success, $"Unrecognized HLSL field: {line}");
                list.Add((fieldMatch.Groups[1].Value.Trim(), fieldMatch.Groups[2].Value.Trim(), fieldMatch.Groups[3].Value.Trim()));
            }

            return list;
        }

        foreach (var (_, vert, frag) in CorePipelines)
        {
            string vertHlsl = File.ReadAllText(Path.Combine(hlslDir, vert + ".hlsl"));
            string fragHlsl = File.ReadAllText(Path.Combine(hlslDir, frag + ".hlsl"));
            var vertOut = ParseStruct(vertHlsl, "SPIRV_Cross_Output");
            var fragIn = ParseStruct(fragHlsl, "SPIRV_Cross_Input");

            Assert.True(
                fragIn.Count <= vertOut.Count,
                $"{vert} -> {frag}: fragment input count ({fragIn.Count}) exceeds vertex output count ({vertOut.Count})");

            for (int i = 0; i < fragIn.Count; i++)
            {
                var vs = vertOut[i];
                var ps = fragIn[i];
                Assert.True(
                    vs.Semantic == ps.Semantic && vs.Type == ps.Type && (vs.Interp == ps.Interp || ps.Semantic == "SV_Position"),
                    $"{vert} -> {frag} slot {i} mismatch: VS=({vs.Interp}, {vs.Type}, {vs.Semantic}) vs PS=({ps.Interp}, {ps.Type}, {ps.Semantic})");
            }
        }
    }

    [Fact]
    public void DxilBinaries_HaveSignedContainersAndMatchingOsg1Isg1Registers()
    {
        string shadersDir = LocateShadersDirectory();
        foreach (var (_, vert, frag) in CorePipelines)
        {
            byte[] vertBytes = File.ReadAllBytes(Path.Combine(shadersDir, vert + ".dxil"));
            byte[] fragBytes = File.ReadAllBytes(Path.Combine(shadersDir, frag + ".dxil"));

            AssertSignedDxbcContainer(vert, vertBytes);
            AssertSignedDxbcContainer(frag, fragBytes);

            var osg1 = ParseSignatureChunk(vertBytes, "OSG1");
            var isg1 = ParseSignatureChunk(fragBytes, "ISG1");

            foreach (var psElem in isg1)
            {
                if (psElem.Register == uint.MaxValue)
                {
                    continue;
                }

                var match = osg1.FirstOrDefault(vs =>
                    vs.Register == psElem.Register
                    && string.Equals(vs.SemanticName, psElem.SemanticName, StringComparison.OrdinalIgnoreCase)
                    && vs.SemanticIndex == psElem.SemanticIndex);

                Assert.True(
                    !string.IsNullOrEmpty(match.SemanticName),
                    $"DXIL register mismatch in {vert} -> {frag}: PS expects ({psElem.SemanticName}{psElem.SemanticIndex} at reg {psElem.Register}, mask 0x{psElem.Mask:X}), VS OSG1=[{string.Join(", ", osg1.Select(v => $"{v.SemanticName}{v.SemanticIndex}@r{v.Register}:0x{v.Mask:X}"))}]");
                Assert.Equal(match.ComponentType, psElem.ComponentType);
                Assert.Equal(psElem.Mask, (byte)(match.Mask & psElem.Mask));
            }
        }
    }

    [Fact]
    public void WindowsD3D12_CreatesAllCoreGraphicsPipelines_AcrossDepthAndSampleCounts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string shadersDir = LocateShadersDirectory();
        string? prevShadersEnv = Environment.GetEnvironmentVariable("SIMS_SDL_SHADERS");
        Environment.SetEnvironmentVariable("SIMS_SDL_SHADERS", shadersDir);
        bool sdlInited = false;
        try
        {
            sdlInited = SDL_Init(SDL_INIT_VIDEO);
            Assert.True(sdlInited, $"SDL_Init(SDL_INIT_VIDEO) failed on Windows: {SDL_GetError()}");

            using var gpu = new GPUContext("direct3d12");
            Assert.Equal("direct3d12", gpu.DriverName);

            int[] sampleCounts = gpu.PreferredSampleCount > SDL_GPU_SAMPLECOUNT_1
                ? [SDL_GPU_SAMPLECOUNT_1, gpu.PreferredSampleCount]
                : [SDL_GPU_SAMPLECOUNT_1];

            GPUContext.DepthMode[] depthModes =
            [
                GPUContext.DepthMode.Flat,
                GPUContext.DepthMode.Mesh,
                GPUContext.DepthMode.MeshEdge,
                GPUContext.DepthMode.MeshEqual,
                GPUContext.DepthMode.MeshDepthPrepass,
            ];

            foreach (var (program, _, _) in CorePipelines)
            {
                foreach (var depth in depthModes)
                {
                    foreach (int sampleCount in sampleCounts)
                    {
                        if (program == GPUContext.Program.Censor && sampleCount != SDL_GPU_SAMPLECOUNT_1)
                        {
                            continue;
                        }

                        nint handle = gpu.Pipeline(program, depth, sampleCount);
                        Assert.NotEqual(nint.Zero, handle);
                    }
                }
            }
        }
        finally
        {
            if (sdlInited)
            {
                SDL_Quit();
            }

            Environment.SetEnvironmentVariable("SIMS_SDL_SHADERS", prevShadersEnv);
        }
    }

    private static void AssertSignedDxbcContainer(string name, ReadOnlySpan<byte> bytes)
    {
        Assert.True(bytes.Length >= 32, $"{name}.dxil is truncated ({bytes.Length} bytes)");
        Assert.Equal("DXBC", Encoding.ASCII.GetString(bytes[..4]));
        bool anyDigestByteNonZero = false;
        for (int i = 4; i < 20; i++)
        {
            if (bytes[i] != 0)
            {
                anyDigestByteNonZero = true;
                break;
            }
        }

        Assert.True(anyDigestByteNonZero, $"{name}.dxil has an unsigned all-zero DXBC digest");
    }

    private static List<DxilSigElement> ParseSignatureChunk(ReadOnlySpan<byte> container, string fourCc)
    {
        uint chunkCount = BinaryPrimitives.ReadUInt32LittleEndian(container.Slice(28, 4));
        for (int i = 0; i < chunkCount; i++)
        {
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(container.Slice(32 + i * 4, 4));
            string tag = Encoding.ASCII.GetString(container.Slice((int)offset, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(container.Slice((int)offset + 4, 4));
            if (!string.Equals(tag, fourCc, StringComparison.Ordinal))
            {
                continue;
            }

            ReadOnlySpan<byte> data = container.Slice((int)offset + 8, (int)size);
            uint elemCount = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]);
            var list = new List<DxilSigElement>((int)elemCount);
            for (int e = 0; e < elemCount; e++)
            {
                ReadOnlySpan<byte> rec = data.Slice(8 + e * 32, 32);
                uint nameOff = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(4, 4));
                uint semIdx = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(8, 4));
                uint sysVal = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(12, 4));
                uint compType = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(16, 4));
                uint reg = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(20, 4));
                byte mask = rec[24];

                int end = (int)nameOff;
                while (end < data.Length && data[end] != 0)
                {
                    end++;
                }

                string semName = Encoding.ASCII.GetString(data.Slice((int)nameOff, end - (int)nameOff));
                list.Add(new DxilSigElement(reg, semName, semIdx, sysVal, compType, mask));
            }

            return list;
        }

        throw new InvalidDataException($"Missing {fourCc} chunk in DXBC container.");
    }
}
