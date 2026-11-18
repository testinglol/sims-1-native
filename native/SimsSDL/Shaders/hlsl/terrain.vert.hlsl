struct TerrainVertex
{
    float2 position;
    float2 grass;
    float4 color;
    float4 dark;
};

cbuffer Quad : register(b0, space1)
{
    float4 quad_rect : packoffset(c0);
    float4 quad_canvasPadding : packoffset(c1);
    float4 quad_uvRect : packoffset(c2);
    float4 quad_depthInterval : packoffset(c3);
    float4 quad_tint : packoffset(c4);
};

ByteAddressBuffer _56 : register(t0, space0);

static float4 gl_Position;
static int gl_VertexIndex;
static float4 outColor;
static float2 outCoordinate;
static float4 outLight;
static float4 outDark;
static float4 outTint;
static float outGrass;
static float2 outDetail;

struct SPIRV_Cross_Input
{
    uint gl_VertexIndex : SV_VertexID;
};

struct SPIRV_Cross_Output
{
    float4 outColor : TEXCOORD0;
    float2 outCoordinate : TEXCOORD1;
    float4 outLight : TEXCOORD2;
    float4 outDark : TEXCOORD3;
    float4 outTint : TEXCOORD4;
    float outGrass : TEXCOORD5;
    nointerpolation float2 outDetail : TEXCOORD6;
    float4 gl_Position : SV_Position;
};

float4 canvasPosition(float2 pixel, float z)
{
    float2 canvas = quad_canvasPadding.xy;
    return float4(((pixel.x / canvas.x) * 2.0f) - 1.0f, 1.0f - ((pixel.y / canvas.y) * 2.0f), z, 1.0f);
}

void vert_main()
{
    TerrainVertex _63;
    _63.position = asfloat(_56.Load2(gl_VertexIndex * 48 + 0));
    _63.grass = asfloat(_56.Load2(gl_VertexIndex * 48 + 8));
    _63.color = asfloat(_56.Load4(gl_VertexIndex * 48 + 16));
    _63.dark = asfloat(_56.Load4(gl_VertexIndex * 48 + 32));
    TerrainVertex vertex;
    vertex.position = _63.position;
    vertex.grass = _63.grass;
    vertex.color = _63.color;
    vertex.dark = _63.dark;
    float2 param = quad_rect.xy + (vertex.position * quad_rect.zw);
    float param_1 = 0.0f;
    gl_Position = canvasPosition(param, param_1);
    outColor = vertex.color * quad_tint;
    outCoordinate = vertex.position;
    outLight = vertex.color;
    outDark = vertex.dark;
    outTint = quad_tint;
    outGrass = vertex.grass.x;
    outDetail = quad_uvRect.xy;
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    gl_VertexIndex = int(stage_input.gl_VertexIndex);
    vert_main();
    SPIRV_Cross_Output stage_output;
    stage_output.gl_Position = gl_Position;
    stage_output.outColor = outColor;
    stage_output.outCoordinate = outCoordinate;
    stage_output.outLight = outLight;
    stage_output.outDark = outDark;
    stage_output.outTint = outTint;
    stage_output.outGrass = outGrass;
    stage_output.outDetail = outDetail;
    return stage_output;
}
