static const float2 _57[6] = { 0.0f.xx, float2(1.0f, 0.0f), float2(0.0f, 1.0f), float2(0.0f, 1.0f), float2(1.0f, 0.0f), 1.0f.xx };

cbuffer Quad : register(b0, space1)
{
    float4 quad_rect : packoffset(c0);
    float4 quad_canvasPadding : packoffset(c1);
    float4 quad_uvRect : packoffset(c2);
    float4 quad_depthInterval : packoffset(c3);
    float4 quad_tint : packoffset(c4);
};


static float4 gl_Position;
static int gl_VertexIndex;
static float2 outUV;
static float4 outTint;
static float4 outDepthInterval;
static float outOpacity;
static float4 outUVRect;

struct SPIRV_Cross_Input
{
    uint gl_VertexIndex : SV_VertexID;
};

struct SPIRV_Cross_Output
{
    float2 outUV : TEXCOORD0;
    nointerpolation float4 outTint : TEXCOORD1;
    nointerpolation float4 outDepthInterval : TEXCOORD2;
    nointerpolation float outOpacity : TEXCOORD3;
    nointerpolation float4 outUVRect : TEXCOORD4;
    float4 gl_Position : SV_Position;
};

float4 canvasPosition(float2 pixel, float z)
{
    float2 canvas = quad_canvasPadding.xy;
    return float4(((pixel.x / canvas.x) * 2.0f) - 1.0f, 1.0f - ((pixel.y / canvas.y) * 2.0f), z, 1.0f);
}

void vert_main()
{
    float2 uv = _57[gl_VertexIndex];
    float2 param = quad_rect.xy + (uv * quad_rect.zw);
    float param_1 = 0.0f;
    gl_Position = canvasPosition(param, param_1);
    float _91;
    if (quad_canvasPadding.z > 0.0f)
    {
        _91 = 1.0f - uv.x;
    }
    else
    {
        _91 = uv.x;
    }
    float2 source = float2(_91, uv.y);
    outUV = quad_uvRect.xy + (source * quad_uvRect.zw);
    outTint = quad_tint;
    outDepthInterval = quad_depthInterval;
    outOpacity = quad_canvasPadding.w;
    outUVRect = quad_uvRect;
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    gl_VertexIndex = int(stage_input.gl_VertexIndex);
    vert_main();
    SPIRV_Cross_Output stage_output;
    stage_output.gl_Position = gl_Position;
    stage_output.outUV = outUV;
    stage_output.outTint = outTint;
    stage_output.outDepthInterval = outDepthInterval;
    stage_output.outOpacity = outOpacity;
    stage_output.outUVRect = outUVRect;
    return stage_output;
}
