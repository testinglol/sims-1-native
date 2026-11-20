struct AvatarVertex
{
    float4 position;
    float2 uv;
    float2 padding;
    float4 normal;
};

cbuffer Quad : register(b0, space1)
{
    float4 quad_rect : packoffset(c0);
    float4 quad_canvasPadding : packoffset(c1);
    float4 quad_uvRect : packoffset(c2);
    float4 quad_depthInterval : packoffset(c3);
    float4 quad_tint : packoffset(c4);
};

ByteAddressBuffer _63 : register(t0, space0);

static float4 gl_Position;
static int gl_VertexIndex;
static float2 outUV;
static float4 outTint;
static float3 outNormal;
static float outTextureOnly;
static float3 outSunDir;

struct SPIRV_Cross_Input
{
    uint gl_VertexIndex : SV_VertexID;
};

struct SPIRV_Cross_Output
{
    float2 outUV : TEXCOORD0;
    nointerpolation float4 outTint : TEXCOORD1;
    float3 outNormal : TEXCOORD2;
    nointerpolation float outTextureOnly : TEXCOORD3;
    nointerpolation float3 outSunDir : TEXCOORD4;
    float4 gl_Position : SV_Position;
};

float4 canvasPosition(float2 pixel, float z)
{
    float2 canvas = quad_canvasPadding.xy;
    return float4(((pixel.x / canvas.x) * 2.0f) - 1.0f, 1.0f - ((pixel.y / canvas.y) * 2.0f), z, 1.0f);
}

void vert_main()
{
    uint baseVertex = asuint(quad_depthInterval.x);
    AvatarVertex _73;
    _73.position = asfloat(_63.Load4((baseVertex + uint(gl_VertexIndex)) * 48 + 0));
    _73.uv = asfloat(_63.Load2((baseVertex + uint(gl_VertexIndex)) * 48 + 16));
    _73.padding = asfloat(_63.Load2((baseVertex + uint(gl_VertexIndex)) * 48 + 24));
    _73.normal = asfloat(_63.Load4((baseVertex + uint(gl_VertexIndex)) * 48 + 32));
    AvatarVertex vertex;
    vertex.position = _73.position;
    vertex.uv = _73.uv;
    vertex.padding = _73.padding;
    vertex.normal = _73.normal;
    float2 param = quad_rect.xy + (vertex.position.xy * quad_rect.zw);
    float param_1 = vertex.position.z;
    gl_Position = canvasPosition(param, param_1);
    outUV = vertex.uv;
    outTint = quad_tint;
    outNormal = vertex.normal.xyz;
    outTextureOnly = quad_uvRect.x;
    outSunDir = quad_uvRect.yzw;
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    gl_VertexIndex = int(stage_input.gl_VertexIndex);
    vert_main();
    SPIRV_Cross_Output stage_output;
    stage_output.gl_Position = gl_Position;
    stage_output.outUV = outUV;
    stage_output.outTint = outTint;
    stage_output.outNormal = outNormal;
    stage_output.outTextureOnly = outTextureOnly;
    stage_output.outSunDir = outSunDir;
    return stage_output;
}
