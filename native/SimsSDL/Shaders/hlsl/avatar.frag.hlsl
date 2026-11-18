Texture2D<float4> art : register(t0, space2);
SamplerState _art_sampler : register(s0, space2);

static float2 inUV;
static float inTextureOnly;
static float4 color;
static float4 inTint;
static float3 inNormal;

struct SPIRV_Cross_Input
{
    float2 inUV : TEXCOORD0;
    nointerpolation float4 inTint : TEXCOORD1;
    float3 inNormal : TEXCOORD2;
    nointerpolation float inTextureOnly : TEXCOORD3;
};

struct SPIRV_Cross_Output
{
    float4 color : SV_Target0;
};

void frag_main()
{
    float4 sampled = art.Sample(_art_sampler, inUV);
    if (sampled.w < 0.00999999977648258209228515625f)
    {
        discard;
    }
    if (inTextureOnly > 0.5f)
    {
        color = float4((sampled.xyz * inTint.xyz) * inTint.w, sampled.w * inTint.w);
        return;
    }
    float nLenSq = dot(inNormal, inNormal);
    float3 _72;
    if (nLenSq > 9.9999999747524270787835121154785e-07f)
    {
        _72 = inNormal * rsqrt(nLenSq);
    }
    else
    {
        _72 = float3(0.0f, 1.0f, 0.0f);
    }
    float3 n = _72;
    float ndotl = max(0.0f, dot(n, float3(-0.3244428336620330810546875f, 0.811107099056243896484375f, -0.4866642653942108154296875f)));
    float hemi = 0.5f + (0.5f * n.y);
    float light = (0.60000002384185791015625f + (0.3300000131130218505859375f * ndotl)) + (0.070000000298023223876953125f * hemi);
    color = float4(((sampled.xyz * light) * inTint.xyz) * inTint.w, sampled.w * inTint.w);
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    inUV = stage_input.inUV;
    inTextureOnly = stage_input.inTextureOnly;
    inTint = stage_input.inTint;
    inNormal = stage_input.inNormal;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.color = color;
    return stage_output;
}
