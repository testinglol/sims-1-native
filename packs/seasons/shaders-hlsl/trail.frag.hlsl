cbuffer Ambient : register(b0, space3)
{
    float4 ambient_view : packoffset(c0);
    float4 ambient_params : packoffset(c1);
    float4 ambient_light : packoffset(c2);
    float4 ambient_extent : packoffset(c3);
    float4 ambient_lamp : packoffset(c4);
    float4 ambient_contact : packoffset(c5);
    float4 ambient_frame : packoffset(c6);
    float4 ambient_sunLot : packoffset(c7);
    float4 ambient_clouds : packoffset(c8);
    float4 ambient_drift : packoffset(c9);
    float4 ambient_weather : packoffset(c10);
    float4 ambient_tone : packoffset(c11);
    float4 ambient_season : packoffset(c12);
    float4 ambient_lamps[16] : packoffset(c13);
    float4 ambient_prints[16] : packoffset(c29);
    float4 ambient_seasonExtra : packoffset(c45);
    float4 ambient_seasonExtra2 : packoffset(c46);
    float4 ambient_shadowView : packoffset(c47);
};

Texture2D<float4> previous : register(t0, space2);
SamplerState _previous_sampler : register(s0, space2);

static float4 gl_FragCoord;
static float4 color;

struct SPIRV_Cross_Input
{
    float4 gl_FragCoord : SV_Position;
};

struct SPIRV_Cross_Output
{
    float4 color : SV_Target0;
};

float print(float2 lot, float4 p)
{
    float foot = abs(p.w);
    bool paw = foot > 1.5f;
    float size = paw ? 1.0f : foot;
    float2 along = float2(cos(p.z), sin(p.z));
    float2 across = float2(-along.y, along.x);
    float _63;
    if (paw)
    {
        _63 = 0.04500000178813934326171875f;
    }
    else
    {
        _63 = 0.064999997615814208984375f * size;
    }
    float2 d = (lot - p.xy) + ((across * sign(p.w)) * _63);
    if (dot(d, d) > 0.0900000035762786865234375f)
    {
        return 0.0f;
    }
    float2 local = float2(dot(d, along), dot(d, across)) / size.xx;
    float edge;
    if (paw)
    {
        edge = (length(local / float2(0.0500000007450580596923828125f, 0.039999999105930328369140625f)) - 1.0f) * 0.039999999105930328369140625f;
    }
    else
    {
        float sole = (length((local - float2(0.02999999932944774627685546875f, 0.0f)) / float2(0.085000000894069671630859375f, 0.0529999993741512298583984375f)) - 1.0f) * 0.0529999993741512298583984375f;
        float heel = (length((local - float2(-0.039999999105930328369140625f, 0.0f)) / float2(0.04500000178813934326171875f, 0.0460000000894069671630859375f)) - 1.0f) * 0.0460000000894069671630859375f;
        edge = min(sole, heel) * size;
    }
    return clamp(0.5f - ((edge * 32.0f) / 1.5f), 0.0f, 1.0f);
}

void frag_main()
{
    int2 at = int2(gl_FragCoord.xy);
    float _166;
    if (ambient_season.w < (-0.5f))
    {
        _166 = 0.0f;
    }
    else
    {
        _166 = max(0.0f, previous.Load(int3(at, 0)).x - ambient_season.z);
    }
    float pressed = _166;
    float2 lot = (gl_FragCoord.xy / 32.0f.xx) - 1.0f.xx;
    for (int i = 0; i < 16; i++)
    {
        if (ambient_prints[i].w == 0.0f)
        {
            break;
        }
        float2 param = lot;
        float4 param_1 = ambient_prints[i];
        pressed = max(pressed, print(param, param_1));
    }
    color = float4(pressed, 0.0f, 0.0f, 1.0f);
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    gl_FragCoord = stage_input.gl_FragCoord;
    gl_FragCoord.w = 1.0 / gl_FragCoord.w;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.color = color;
    return stage_output;
}
