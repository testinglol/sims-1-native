Texture2D<float4> art : register(t0, space2);
SamplerState _art_sampler : register(s0, space2);

static float2 inUV;
static float inTextureOnly;
static float4 color;
static float4 inTint;
static float3 inNormal;
static float3 inSunDir;

struct SPIRV_Cross_Input
{
    float2 inUV : TEXCOORD0;
    nointerpolation float4 inTint : TEXCOORD1;
    float3 inNormal : TEXCOORD2;
    nointerpolation float inTextureOnly : TEXCOORD3;
    nointerpolation float3 inSunDir : TEXCOORD4;
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
    bool _35 = inTextureOnly > 0.5f;
    bool _41;
    if (_35)
    {
        _41 = inTextureOnly < 1.5f;
    }
    else
    {
        _41 = _35;
    }
    if (_41)
    {
        color = float4((sampled.xyz * inTint.xyz) * inTint.w, sampled.w * inTint.w);
        return;
    }
    float nLenSq = dot(inNormal, inNormal);
    float3 _78;
    if (nLenSq > 9.9999999747524270787835121154785e-07f)
    {
        _78 = inNormal * rsqrt(nLenSq);
    }
    else
    {
        _78 = float3(0.0f, 1.0f, 0.0f);
    }
    float3 n = _78;
    if (inTextureOnly >= 1.5f)
    {
        float sLenSq = dot(inSunDir, inSunDir);
        float3 _102;
        if (sLenSq > 9.9999999747524270787835121154785e-07f)
        {
            _102 = inSunDir * rsqrt(sLenSq);
        }
        else
        {
            _102 = float3(0.7547318935394287109375f, 0.553470075130462646484375f, -0.3522082269191741943359375f);
        }
        float3 sunDir = _102;
        float ndotl = max(0.0f, dot(n, sunDir));
        float hemi = 0.5f + (0.5f * n.y);
        float diffuse = (0.4199999868869781494140625f + (0.579999983310699462890625f * ndotl)) + (0.07999999821186065673828125f * hemi);
        float3 slopeTint = lerp(float3(0.89999997615814208984375f, 0.930000007152557373046875f, 1.0f), float3(1.0299999713897705078125f, 1.0099999904632568359375f, 0.980000019073486328125f), clamp(ndotl, 0.0f, 1.0f).xxx);
        color = float4((((sampled.xyz * diffuse) * slopeTint) * inTint.xyz) * inTint.w, sampled.w * inTint.w);
        return;
    }
    float ndotl_1 = max(0.0f, dot(n, float3(-0.3244428336620330810546875f, 0.811107099056243896484375f, -0.4866642653942108154296875f)));
    float hemi_1 = 0.5f + (0.5f * n.y);
    float light = (0.60000002384185791015625f + (0.3300000131130218505859375f * ndotl_1)) + (0.070000000298023223876953125f * hemi_1);
    color = float4(((sampled.xyz * light) * inTint.xyz) * inTint.w, sampled.w * inTint.w);
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    inUV = stage_input.inUV;
    inTextureOnly = stage_input.inTextureOnly;
    inTint = stage_input.inTint;
    inNormal = stage_input.inNormal;
    inSunDir = stage_input.inSunDir;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.color = color;
    return stage_output;
}
