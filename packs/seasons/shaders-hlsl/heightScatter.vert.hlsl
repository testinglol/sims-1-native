cbuffer Scatter : register(b0, space1)
{
    float4 scatter_view : packoffset(c0);
    float4 scatter_extent : packoffset(c1);
    float4 scatter_frame : packoffset(c2);
    float4 scatter_grid : packoffset(c3);
};

Texture2D<float4> depthMap : register(t0, space0);
SamplerState _depthMap_sampler : register(s0, space0);

static float4 gl_Position;
static float gl_PointSize;
static int gl_VertexIndex;
struct SPIRV_Cross_Input
{
    uint gl_VertexIndex : SV_VertexID;
};

struct SPIRV_Cross_Output
{
    float4 gl_Position : SV_Position;
};

float mod(float x, float y)
{
    return x - y * floor(x / y);
}

float2 mod(float2 x, float2 y)
{
    return x - y * floor(x / y);
}

float3 mod(float3 x, float3 y)
{
    return x - y * floor(x / y);
}

float4 mod(float4 x, float4 y)
{
    return x - y * floor(x / y);
}

float3 lotPoint(float2 scene, float key, bool terrain, float2 frame)
{
    float2 local = scene - frame;
    float _26;
    if (terrain)
    {
        _26 = ((16.0f * key) - local.y) / 40.0f;
    }
    else
    {
        _26 = ((16.0f * key) - local.y) / 52.799999237060546875f;
    }
    float h = _26;
    float _50;
    if (terrain)
    {
        _50 = key;
    }
    else
    {
        _50 = key - (0.800000011920928955078125f * h);
    }
    float s = _50;
    float u = local.x / 32.0f;
    return float3(0.5f * (s + u), 0.5f * (s - u), h);
}

void vert_main()
{
    gl_PointSize = 1.0f;
    int columns = int(scatter_extent.z);
    float2 pixel = floor((float2(float(gl_VertexIndex % columns), float(gl_VertexIndex / columns)) * scatter_view.w) + mod(scatter_view.xy, scatter_view.w.xx)) + 0.5f.xx;
    bool _134 = pixel.x < scatter_extent.x;
    bool _142;
    if (_134)
    {
        _142 = pixel.y < scatter_extent.y;
    }
    else
    {
        _142 = _134;
    }
    float _143;
    if (_142)
    {
        _143 = depthMap.Load(int3(int2(pixel), 0)).x;
    }
    else
    {
        _143 = 1.0f;
    }
    float depth = _143;
    if (depth >= 1.0f)
    {
        gl_Position = float4(-2.0f, -2.0f, 1.0f, 1.0f);
        return;
    }
    float2 param = (pixel - scatter_view.xy) / scatter_view.z.xx;
    float param_1 = (0.89999997615814208984375f - depth) / 0.0030000000260770320892333984375f;
    bool param_2 = false;
    float2 param_3 = scatter_frame.xy;
    float3 lot = lotPoint(param, param_1, param_2, param_3);
    if (scatter_extent.w > 0.5f)
    {
        float _step = max(scatter_view.z, 1.0f);
        bool ground = false;
        for (int i = 1; i <= 2; i++)
        {
            float2 below = pixel + float2(0.0f, float(i) * _step);
            bool _230;
            if (!ground)
            {
                _230 = below.y >= scatter_extent.y;
            }
            else
            {
                _230 = ground;
            }
            bool _241;
            if (!_230)
            {
                _241 = depthMap.Load(int3(int2(below), 0)).x >= 1.0f;
            }
            else
            {
                _241 = _230;
            }
            ground = _241;
        }
        float2 abovePixel = pixel - float2(0.0f, 3.0f * _step);
        float _255;
        if (abovePixel.y >= 0.0f)
        {
            _255 = depthMap.Load(int3(int2(abovePixel), 0)).x;
        }
        else
        {
            _255 = 1.0f;
        }
        float above = _255;
        float2 param_4 = (abovePixel - scatter_view.xy) / scatter_view.z.xx;
        float param_5 = (0.89999997615814208984375f - above) / 0.0030000000260770320892333984375f;
        bool param_6 = false;
        float2 param_7 = scatter_frame.xy;
        float3 up = lotPoint(param_4, param_5, param_6, param_7);
        bool _291 = (!ground) || (above >= 1.0f);
        bool _302;
        if (!_291)
        {
            _302 = (up.z - lot.z) < 0.02999999932944774627685546875f;
        }
        else
        {
            _302 = _291;
        }
        bool _314;
        if (!_302)
        {
            _314 = length(up.xy - lot.xy) > 0.20000000298023223876953125f;
        }
        else
        {
            _314 = _302;
        }
        if (_314)
        {
            gl_Position = float4(-2.0f, -2.0f, 1.0f, 1.0f);
            return;
        }
    }
    float2 texel = (lot.xy + scatter_frame.w.xx) * scatter_frame.z;
    gl_Position = float4(((texel.x / scatter_grid.x) * 2.0f) - 1.0f, 1.0f - ((texel.y / scatter_grid.y) * 2.0f), clamp(0.5f - (lot.z / 64.0f), 0.0f, 1.0f), 1.0f);
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    gl_VertexIndex = int(stage_input.gl_VertexIndex);
    vert_main();
    SPIRV_Cross_Output stage_output;
    stage_output.gl_Position = gl_Position;
    return stage_output;
}
