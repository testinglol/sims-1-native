Texture2D<float4> art : register(t0, space2);
SamplerState _art_sampler : register(s0, space2);

static float2 inUV;
static float4 color;
static float inOpacity;
static float4 inTint;
static float4 inDepthInterval;

struct SPIRV_Cross_Input
{
    float2 inUV : TEXCOORD0;
    nointerpolation float4 inTint : TEXCOORD1;
    nointerpolation float4 inDepthInterval : TEXCOORD2;
    nointerpolation float inOpacity : TEXCOORD3;
};

struct SPIRV_Cross_Output
{
    float4 color : SV_Target0;
};

void frag_main()
{
    float4 sampled = art.Sample(_art_sampler, inUV);
    color = sampled * inOpacity;
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    inUV = stage_input.inUV;
    inOpacity = stage_input.inOpacity;
    inTint = stage_input.inTint;
    inDepthInterval = stage_input.inDepthInterval;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.color = color;
    return stage_output;
}
