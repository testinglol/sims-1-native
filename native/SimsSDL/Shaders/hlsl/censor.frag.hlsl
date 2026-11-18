Texture2D<float4> lotColor : register(t0, space2);
SamplerState _lotColor_sampler : register(s0, space2);
Texture2D<float4> depthMap : register(t1, space2);
SamplerState _depthMap_sampler : register(s1, space2);

static float4 gl_FragCoord;
static float4 inDepthInterval;
static float4 color;
static float2 inUV;
static float4 inTint;
static float inOpacity;
static float4 inUVRect;

struct SPIRV_Cross_Input
{
    float2 inUV : TEXCOORD0;
    nointerpolation float4 inTint : TEXCOORD1;
    nointerpolation float4 inDepthInterval : TEXCOORD2;
    nointerpolation float inOpacity : TEXCOORD3;
    nointerpolation float4 inUVRect : TEXCOORD4;
    float4 gl_FragCoord : SV_Position;
};

struct SPIRV_Cross_Output
{
    float4 color : SV_Target0;
};

uint2 spvTextureSize(Texture2D<float4> Tex, uint Level, out uint Param)
{
    uint2 ret;
    Tex.GetDimensions(Level, ret.x, ret.y, Param);
    return ret;
}

void frag_main()
{
    uint _18_dummy_parameter;
    int2 dims = int2(spvTextureSize(lotColor, uint(0), _18_dummy_parameter));
    int2 fragCoord = clamp(int2(gl_FragCoord.xy), int2(0, 0), dims - int2(1, 1));
    float fragDepth = depthMap.Load(int3(fragCoord, 0)).x;
    if (fragDepth < inDepthInterval.x)
    {
        discard;
    }
    float block = max(2.0f, inDepthInterval.y);
    int2 cell = int2(floor(gl_FragCoord.xy / block.xx));
    int2 sampleCoord = clamp(int2((float2(cell) + 0.5f.xx) * block), int2(0, 0), dims - int2(1, 1));
    if (depthMap.Load(int3(sampleCoord, 0)).x < inDepthInterval.x)
    {
        sampleCoord = fragCoord;
    }
    float4 sampled = lotColor.Load(int3(sampleCoord, 0));
    uint h = uint((cell.x * 73856093) ^ (cell.y * 19349663));
    h = (h ^ (h >> 13u)) * 1274126177u;
    float lum = max(sampled.x, max(sampled.y, sampled.z));
    float ditherScale = lerp(0.25f, 1.0f, smoothstep(0.0199999995529651641845703125f, 0.25f, lum));
    float dither = ((float(h & 15u) - 8.0f) / 255.0f) * ditherScale;
    color = float4(clamp(sampled.xyz + dither.xxx, 0.0f.xxx, 1.0f.xxx), 1.0f);
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    gl_FragCoord = stage_input.gl_FragCoord;
    gl_FragCoord.w = 1.0 / gl_FragCoord.w;
    inDepthInterval = stage_input.inDepthInterval;
    inUV = stage_input.inUV;
    inTint = stage_input.inTint;
    inOpacity = stage_input.inOpacity;
    inUVRect = stage_input.inUVRect;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.color = color;
    return stage_output;
}
