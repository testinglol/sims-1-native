Texture2D<float4> art : register(t0, space2);
SamplerState _art_sampler : register(s0, space2);

static float2 inUV;
static float4 color;

struct SPIRV_Cross_Input
{
    float2 inUV : TEXCOORD0;
};

struct SPIRV_Cross_Output
{
    float4 color : SV_Target0;
};

void frag_main()
{
    if (art.Sample(_art_sampler, inUV).w < 0.00999999977648258209228515625f)
    {
        discard;
    }
    color = 1.0f.xxxx;
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    inUV = stage_input.inUV;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.color = color;
    return stage_output;
}
