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

Texture2D<float4> depthMap : register(t0, space2);
SamplerState _depthMap_sampler : register(s0, space2);
Texture2D<float4> groundMap : register(t1, space2);
SamplerState _groundMap_sampler : register(s1, space2);
Texture2D<float4> indoorMap : register(t3, space2);
SamplerState _indoorMap_sampler : register(s3, space2);
Texture2D<float4> overheadMap : register(t6, space2);
SamplerState _overheadMap_sampler : register(s6, space2);
Texture2D<float4> heightMap : register(t2, space2);
SamplerState _heightMap_sampler : register(s2, space2);
Texture2D<float4> trailMap : register(t5, space2);
SamplerState _trailMap_sampler : register(s5, space2);
Texture2D<float4> source : register(t4, space2);
SamplerState _source_sampler : register(s4, space2);
Texture2D<float4> simMap : register(t7, space2);
SamplerState _simMap_sampler : register(s7, space2);

static float4 gl_FragCoord;
static float4 color;
static float4 lift;

struct SPIRV_Cross_Input
{
    float4 gl_FragCoord : SV_Position;
};

struct SPIRV_Cross_Output
{
    float4 color : SV_Target0;
    float4 lift : SV_Target1;
};

uint2 spvTextureSize(Texture2D<float4> Tex, uint Level, out uint Param)
{
    uint2 ret;
    Tex.GetDimensions(Level, ret.x, ret.y, Param);
    return ret;
}

float4 positionAt(float2 pixel)
{
    float2 scene = (pixel - ambient_view.xy) / ambient_view.z.xx;
    float depth = depthMap.Load(int3(int2(pixel), 0)).x;
    if (depth < 1.0f)
    {
        return float4(scene, ((0.89999997615814208984375f - depth) / 0.0030000000260770320892333984375f) * 27.7128124237060546875f, 0.0f);
    }
    float ground = groundMap.Load(int3(int2(pixel), 0)).x;
    if (ground < 1.0f)
    {
        return float4(scene, (((0.89999997615814208984375f - ground) / 0.0030000000260770320892333984375f) + ambient_view.w) * 27.7128124237060546875f, 0.5f);
    }
    return float4(scene, -100000.0f, 1.0f);
}

float3 lotPoint(float2 scene, float key, bool terrain, float2 frame)
{
    float2 local = scene - frame;
    float _113;
    if (terrain)
    {
        _113 = ((16.0f * key) - local.y) / 40.0f;
    }
    else
    {
        _113 = ((16.0f * key) - local.y) / 52.799999237060546875f;
    }
    float h = _113;
    float _137;
    if (terrain)
    {
        _137 = key;
    }
    else
    {
        _137 = key - (0.800000011920928955078125f * h);
    }
    float s = _137;
    float u = local.x / 32.0f;
    return float3(0.5f * (s + u), 0.5f * (s - u), h);
}

int maskAt(int2 at, int band)
{
    uint _302_dummy_parameter;
    int2 size = int2(spvTextureSize(indoorMap, uint(0), _302_dummy_parameter));
    bool _305 = at.x < 0;
    bool _312;
    if (!_305)
    {
        _312 = at.y < 0;
    }
    else
    {
        _312 = _305;
    }
    bool _321;
    if (!_312)
    {
        _321 = at.x >= size.x;
    }
    else
    {
        _321 = _312;
    }
    bool _332;
    if (!_321)
    {
        _332 = at.y >= (size.y / 6);
    }
    else
    {
        _332 = _321;
    }
    if (_332)
    {
        return 0;
    }
    return int((indoorMap.Load(int3(int2(at.x, at.y + (band * (size.y / 6))), 0)).x * 255.0f) + 0.5f);
}

float floorAt(int2 at, int row, float fallback)
{
    int2 param = at;
    int param_1 = row + 2;
    int code = maskAt(param, param_1);
    float _367;
    if (code == 0)
    {
        _367 = fallback;
    }
    else
    {
        _367 = ambient_contact.w + (float(code - 1) / 8.0f);
    }
    return _367;
}

float indoors(float3 lot)
{
    int2 param = int2(floor(lot.xy + ambient_frame.w.xx));
    int param_1 = 0;
    float param_2 = lot.z;
    float ground = floorAt(param, param_1, param_2);
    if (lot.z > ((ground + 5.900000095367431640625f) - 0.100000001490116119384765625f))
    {
        return 0.0f;
    }
    int row = int(lot.z > ((ground + 2.9500000476837158203125f) - 0.100000001490116119384765625f));
    float2 cell = (lot.xy + ambient_frame.w.xx) - 0.5f.xx;
    int2 base = int2(floor(cell));
    float2 f = clamp(((frac(cell) - 0.5f.xx) / 0.20000000298023223876953125f.xx) + 0.5f.xx, 0.0f.xx, 1.0f.xx);
    float sum = 0.0f;
    float _460;
    float _473;
    float2 _521;
    float2 _529;
    for (int i = 0; i < 4; i++)
    {
        if ((i & 1) == 1)
        {
            _460 = f.x;
        }
        else
        {
            _460 = 1.0f - f.x;
        }
        if ((i >> 1) == 1)
        {
            _473 = f.y;
        }
        else
        {
            _473 = 1.0f - f.y;
        }
        float weight = _460 * _473;
        int2 at = base + int2(i & 1, i >> 1);
        int2 param_3 = at;
        int param_4 = row;
        if (maskAt(param_3, param_4) < 2)
        {
            continue;
        }
        int2 param_5 = at;
        int param_6 = row + 4;
        int part = maskAt(param_5, param_6) & 7;
        if (part == 0)
        {
            sum += weight;
            continue;
        }
        if (part == 1)
        {
            _521 = float2(1.0f, -1.0f);
        }
        else
        {
            if (part == 2)
            {
                _529 = float2(-1.0f, 1.0f);
            }
            else
            {
                bool2 _540 = (part == 3).xx;
                _529 = float2(_540.x ? (-1.0f).xx.x : 1.0f.xx.x, _540.y ? (-1.0f).xx.y : 1.0f.xx.y);
            }
            _521 = _529;
        }
        float2 toward = _521;
        sum += (weight * smoothstep(-0.0599999986588954925537109375f, 0.0599999986588954925537109375f, dot(((lot.xy + ambient_frame.w.xx) - float2(at)) - 0.5f.xx, toward)));
    }
    return sum;
}

float architecturalExposure(float3 _point)
{
    float2 size = ambient_drift.zw;
    int orientation = int(ambient_clouds.z + 0.5f);
    float2 _679;
    if (orientation == 1)
    {
        _679 = float2(_point.y, size.y - _point.x);
    }
    else
    {
        float2 _693;
        if (orientation == 2)
        {
            _693 = size - _point.xy;
        }
        else
        {
            float2 _703;
            if (orientation == 3)
            {
                _703 = float2(size.x - _point.y, _point.x);
            }
            else
            {
                _703 = _point.xy;
            }
            _693 = _703;
        }
        _679 = _693;
    }
    float2 source_1 = _679;
    bool _723 = any(bool2(source_1.x < 0.0f.xx.x, source_1.y < 0.0f.xx.y));
    bool _731;
    if (!_723)
    {
        _731 = any(bool2(source_1.x >= size.x, source_1.y >= size.y));
    }
    else
    {
        _731 = _723;
    }
    if (_731)
    {
        return 1.0f;
    }
    uint _739_dummy_parameter;
    float2 texelsPerTile = float2(int2(spvTextureSize(overheadMap, uint(0), _739_dummy_parameter))) / max(size, 1.0f.xx);
    int2 at = int2(floor(source_1 * texelsPerTile));
    uint _753_dummy_parameter;
    int2 _753 = int2(spvTextureSize(overheadMap, uint(0), _753_dummy_parameter));
    if (any(bool2(at.x >= _753.x, at.y >= _753.y)))
    {
        return 1.0f;
    }
    int4 data = int4((overheadMap.Load(int3(at, 0)) * 255.0f) + 0.5f.xxxx);
    if (data.w == 0)
    {
        return 1.0f;
    }
    float base = float(((data.y * 256) + data.z) - 32768) / 256.0f;
    int story = int(floor(((_point.z - base) + 0.100000001490116119384765625f) / 2.9500000476837158203125f));
    if ((story < 0) || (story > 4))
    {
        return 1.0f;
    }
    return ((data.x & (1 << story)) != 0) ? 0.0f : 1.0f;
}

float2 lotSource(float2 camera)
{
    float2 size = ambient_drift.zw;
    int orientation = int(ambient_clouds.z + 0.5f);
    float2 _626;
    if (orientation == 1)
    {
        _626 = float2(camera.y, size.y - camera.x);
    }
    else
    {
        float2 _640;
        if (orientation == 2)
        {
            _640 = size - camera;
        }
        else
        {
            float2 _649;
            if (orientation == 3)
            {
                _649 = float2(size.x - camera.y, camera.x);
            }
            else
            {
                _649 = camera;
            }
            _640 = _649;
        }
        _626 = _640;
    }
    return _626;
}

float topAt(float2 at)
{
    uint _819_dummy_parameter;
    float2 gridSize = float2(int2(spvTextureSize(heightMap, uint(0), _819_dummy_parameter)));
    return (0.5f - heightMap.Sample(_heightMap_sampler, ((at + ambient_frame.w.xx) * ambient_frame.z) / gridSize).x) * 64.0f;
}

float3 srgbToLinear(float3 c)
{
    return lerp(c / 12.9200000762939453125f.xxx, pow((c + 0.054999999701976776123046875f.xxx) / 1.05499994754791259765625f.xxx, 2.400000095367431640625f.xxx), step(0.040449999272823333740234375f.xxx, c));
}

float3 recolor(float3 like, float3 hue)
{
    return hue * (dot(like, float3(0.2125999927520751953125f, 0.715200006961822509765625f, 0.072200000286102294921875f)) / max(dot(hue, float3(0.2125999927520751953125f, 0.715200006961822509765625f, 0.072200000286102294921875f)), 0.001000000047497451305389404296875f));
}

float hash12(float2 q)
{
    float3 r = frac(q.xyx * 0.103100001811981201171875f);
    r += dot(r, r.yzx + 33.3300018310546875f.xxx).xxx;
    return frac((r.x + r.y) * r.z);
}

float valueNoise(float2 q)
{
    float2 i = floor(q);
    float2 f = frac(q);
    f = (f * f) * (3.0f.xx - (f * 2.0f));
    float2 param = i;
    float2 param_1 = i + float2(1.0f, 0.0f);
    float2 param_2 = i + float2(0.0f, 1.0f);
    float2 param_3 = i + 1.0f.xx;
    return lerp(lerp(hash12(param), hash12(param_1), f.x), lerp(hash12(param_2), hash12(param_3), f.x), f.y);
}

float3 autumnColorSrgb(float v)
{
    float3 olive = float3(0.4199999868869781494140625f, 0.5f, 0.189999997615814208984375f);
    float3 ygreen = float3(0.660000026226043701171875f, 0.61000001430511474609375f, 0.180000007152557373046875f);
    float3 gold = float3(0.839999973773956298828125f, 0.62999999523162841796875f, 0.1599999964237213134765625f);
    float3 orange = float3(0.819999992847442626953125f, 0.439999997615814208984375f, 0.12999999523162841796875f);
    float3 russet = float3(0.680000007152557373046875f, 0.2700000107288360595703125f, 0.119999997317790985107421875f);
    if (v < 0.14000000059604644775390625f)
    {
        return lerp(olive, ygreen, (v / 0.14000000059604644775390625f).xxx);
    }
    if (v < 0.4199999868869781494140625f)
    {
        return lerp(ygreen, gold, ((v - 0.14000000059604644775390625f) / 0.2800000011920928955078125f).xxx);
    }
    if (v < 0.7799999713897705078125f)
    {
        return lerp(gold, orange, ((v - 0.4199999868869781494140625f) / 0.36000001430511474609375f).xxx);
    }
    return lerp(orange, russet, ((v - 0.7799999713897705078125f) / 0.2199999988079071044921875f).xxx);
}

float pavedAt(inout float2 q)
{
    float2 param = q;
    float2 sourcePoint = lotSource(param);
    bool _940 = any(bool2(sourcePoint.x < 1.0f.xx.x, sourcePoint.y < 1.0f.xx.y));
    bool _952;
    if (!_940)
    {
        float2 _949 = ambient_drift.zw - 1.0f.xx;
        _952 = any(bool2(sourcePoint.x > _949.x, sourcePoint.y > _949.y));
    }
    else
    {
        _952 = _940;
    }
    bool scenery = _952;
    float2 size = ambient_drift.zw;
    int orientation = int(ambient_clouds.z + 0.5f);
    if ((orientation == 1) || (orientation == 3))
    {
        size = size.yx;
    }
    float2 edge = clamp(q, 1.5f.xx, size - 1.5f.xx);
    int2 inward = int2(sign(edge - q));
    float2 beyond = abs(edge - q);
    int reach = min(12, int(ceil(max(beyond.x, beyond.y) - 0.5f)));
    q = edge;
    float2 cell = (q + ambient_frame.w.xx) - 0.5f.xx;
    int2 base = int2(floor(cell));
    float2 f = clamp(((frac(cell) - 0.5f.xx) / 0.4000000059604644775390625f.xx) + 0.5f.xx, 0.0f.xx, 1.0f.xx);
    float sum = 0.0f;
    float _1111;
    float _1124;
    float2 _1145;
    float2 _1151;
    for (int i = 0; i < 4; i++)
    {
        int2 at = base + int2(i & 1, i >> 1);
        int2 param_1 = at;
        int param_2 = 2;
        bool _1047 = maskAt(param_1, param_2) == 0;
        bool _1056;
        if (!_1047)
        {
            int2 param_3 = at;
            int param_4 = 0;
            _1056 = maskAt(param_3, param_4) >= 2;
        }
        else
        {
            _1056 = _1047;
        }
        if (_1056)
        {
            continue;
        }
        int2 param_5 = at;
        int param_6 = 4;
        int f4 = maskAt(param_5, param_6);
        if ((f4 & 128) != 0)
        {
            continue;
        }
        if (scenery)
        {
            bool street = true;
            for (int k = 0; (k < reach) && street; k++)
            {
                int2 param_7 = at + (inward * k.xx);
                int param_8 = 4;
                street = (maskAt(param_7, param_8) & 64) != 0;
            }
            if (!street)
            {
                continue;
            }
        }
        if ((i & 1) == 1)
        {
            _1111 = f.x;
        }
        else
        {
            _1111 = 1.0f - f.x;
        }
        if ((i >> 1) == 1)
        {
            _1124 = f.y;
        }
        else
        {
            _1124 = 1.0f - f.y;
        }
        float weight = _1111 * _1124;
        int part = f4 & 7;
        if (part != 0)
        {
            if (part == 1)
            {
                _1145 = float2(1.0f, -1.0f);
            }
            else
            {
                if (part == 2)
                {
                    _1151 = float2(-1.0f, 1.0f);
                }
                else
                {
                    bool2 _1157 = (part == 3).xx;
                    _1151 = float2(_1157.x ? (-1.0f).xx.x : 1.0f.xx.x, _1157.y ? (-1.0f).xx.y : 1.0f.xx.y);
                }
                _1145 = _1151;
            }
            float2 toward = _1145;
            weight *= smoothstep(-0.0599999986588954925537109375f, 0.0599999986588954925537109375f, dot(((q + ambient_frame.w.xx) - float2(at)) - 0.5f.xx, toward));
        }
        sum += weight;
    }
    return sum;
}

float peakRiseAt(float2 q, float base)
{
    uint _1422_dummy_parameter;
    int2 size = int2(spvTextureSize(heightMap, uint(0), _1422_dummy_parameter));
    float2 t = ((q + ambient_frame.w.xx) * ambient_frame.z) - 0.5f.xx;
    int2 b = int2(floor(t));
    float peak = 0.0f;
    for (int i = 0; i < 4; i++)
    {
        int2 c = clamp(b + int2(i & 1, i >> 1), int2(0, 0), size - int2(1, 1));
        peak = max(peak, ((0.5f - heightMap.Load(int3(c, 0)).x) * 64.0f) - base);
    }
    return peak;
}

float treeCrownAt(float2 q, float baseZ)
{
    float2 param = q;
    float param_1 = baseZ;
    float peak = peakRiseAt(param, param_1);
    if (peak <= 2.0499999523162841796875f)
    {
        return 0.0f;
    }
    float3 p = float3(q, baseZ + 0.100000001490116119384765625f);
    float3 param_2 = p;
    bool _1498 = indoors(param_2) > 0.0500000007450580596923828125f;
    bool _1507;
    if (!_1498)
    {
        float3 param_3 = p;
        _1507 = architecturalExposure(param_3) < 0.949999988079071044921875f;
    }
    else
    {
        _1507 = _1498;
    }
    if (_1507)
    {
        return 0.0f;
    }
    float _1523;
    float _1533;
    for (int i = 0; i < 4; i++)
    {
        if (i == 0)
        {
            _1523 = 1.0f;
        }
        else
        {
            _1523 = (i == 1) ? (-1.0f) : 0.0f;
        }
        if (i == 2)
        {
            _1533 = 1.0f;
        }
        else
        {
            _1533 = (i == 3) ? (-1.0f) : 0.0f;
        }
        float2 d = float2(_1523, _1533) * 0.550000011920928955078125f;
        float3 np = float3(q + d, baseZ + 0.100000001490116119384765625f);
        float3 param_4 = np;
        bool _1556 = indoors(param_4) > 0.07999999821186065673828125f;
        bool _1565;
        if (!_1556)
        {
            float3 param_5 = np;
            _1565 = architecturalExposure(param_5) < 0.920000016689300537109375f;
        }
        else
        {
            _1565 = _1556;
        }
        if (_1565)
        {
            return 0.0f;
        }
    }
    return smoothstep(2.0499999523162841796875f, 2.650000095367431640625f, peak);
}

float isoPixelHash(float2 u, float2 uFw, float seed)
{
    float2 aa = clamp(uFw * 0.75f, 0.0599999986588954925537109375f.xx, 0.4799999892711639404296875f.xx);
    float2 base = floor(u - 0.5f.xx);
    float2 f = smoothstep(0.5f.xx - aa, 0.5f.xx + aa, frac(u - 0.5f.xx));
    float2 param = base + seed.xx;
    float v00 = hash12(param);
    float2 param_1 = (base + float2(1.0f, 0.0f)) + seed.xx;
    float v10 = hash12(param_1);
    float2 param_2 = (base + float2(0.0f, 1.0f)) + seed.xx;
    float v01 = hash12(param_2);
    float2 param_3 = (base + 1.0f.xx) + seed.xx;
    float v11 = hash12(param_3);
    return lerp(lerp(v00, v10, f.x), lerp(v01, v11, f.x), f.y);
}

float groundAt(float2 pixel)
{
    float ground = groundMap.Load(int3(int2(pixel), 0)).x;
    float _284;
    if (ground < 1.0f)
    {
        _284 = (((0.89999997615814208984375f - ground) / 0.0030000000260770320892333984375f) + ambient_view.w) * 27.7128124237060546875f;
    }
    else
    {
        _284 = -100000.0f;
    }
    return _284;
}

float pressedAt(float2 at)
{
    uint _843_dummy_parameter;
    int2 size = int2(spvTextureSize(trailMap, uint(0), _843_dummy_parameter));
    if (size.x < 4)
    {
        return 0.0f;
    }
    float2 param = at;
    float2 texel = ((lotSource(param) + 1.0f.xx) * 32.0f) - 0.5f.xx;
    int2 base = int2(floor(texel));
    float2 f = texel - float2(base);
    float sum = 0.0f;
    float _898;
    float _912;
    for (int i = 0; i < 4; i++)
    {
        int2 c = clamp(base + int2(i & 1, i >> 1), int2(0, 0), size - int2(1, 1));
        if ((i & 1) == 1)
        {
            _898 = f.x;
        }
        else
        {
            _898 = 1.0f - f.x;
        }
        if ((i >> 1) == 1)
        {
            _912 = f.y;
        }
        else
        {
            _912 = 1.0f - f.y;
        }
        sum += ((trailMap.Load(int3(c, 0)).x * _898) * _912);
    }
    return smoothstep(0.3499999940395355224609375f, 0.64999997615814208984375f, sum);
}

float snowHeight(float2 q, float base, float full, float amount, float nearWall)
{
    float3 p3 = float3(q, base + 0.100000001490116119384765625f);
    float3 param = p3;
    float outside = architecturalExposure(param);
    if (outside <= 0.001000000047497451305389404296875f)
    {
        return 0.0f;
    }
    float3 param_1 = p3;
    outside = min(1.0f - indoors(param_1), outside);
    if (outside <= 0.001000000047497451305389404296875f)
    {
        return 0.0f;
    }
    float2 param_2 = q;
    float2 l = lotSource(param_2);
    float2 param_3 = q;
    float overhead = smoothstep(1.2000000476837158203125f, 2.5f, max(0.0f, topAt(param_3) - base));
    float2 param_4 = l * 1.2999999523162841796875f;
    float2 param_5 = (l * 4.099999904632568359375f) + 5.0f.xx;
    float drift = (valueNoise(param_4) * 0.60000002384185791015625f) + (valueNoise(param_5) * 0.4000000059604644775390625f);
    float lying = smoothstep(drift - 0.1500000059604644775390625f, drift + 0.1500000059604644775390625f, (amount * (1.0f - (0.300000011920928955078125f * overhead))) * 1.25f);
    if (lying <= 0.001000000047497451305389404296875f)
    {
        return 0.0f;
    }
    float2 param_6 = (l * 0.449999988079071044921875f) + 9.0f.xx;
    float swell = valueNoise(param_6);
    float2 param_7 = q;
    float _1290 = pavedAt(param_7);
    float snow = ((((full * lying) * outside) * (0.699999988079071044921875f + (0.60000002384185791015625f * swell))) * (1.0f + (0.3499999940395355224609375f * nearWall))) * (1.0f - (0.89999997615814208984375f * _1290));
    if (snow <= 0.001000000047497451305389404296875f)
    {
        return 0.0f;
    }
    float2 param_8 = q;
    return snow - (min(0.02500000037252902984619140625f, snow * 0.5f) * pressedAt(param_8));
}

void frag_main()
{
    float2 pixel = floor(gl_FragCoord.xy) + 0.5f.xx;
    color = 0.0f.xxxx;
    lift = 0.0f.xxxx;
    bool _1662 = pixel.x >= ambient_extent.x;
    bool _1671;
    if (!_1662)
    {
        _1671 = pixel.y >= ambient_extent.y;
    }
    else
    {
        _1671 = _1662;
    }
    if (_1671)
    {
        return;
    }
    float2 param = pixel;
    float4 centre = positionAt(param);
    if (centre.w > 0.75f)
    {
        return;
    }
    float3 src = source.Load(int3(int2(pixel), 0)).xyz;
    bool ground = centre.w > 0.25f;
    bool mapped = ambient_sunLot.w > 0.0f;
    float _1705;
    if (ground)
    {
        _1705 = ((0.89999997615814208984375f - groundMap.Load(int3(int2(pixel), 0)).x) / 0.0030000000260770320892333984375f) + ambient_view.w;
    }
    else
    {
        _1705 = (0.89999997615814208984375f - depthMap.Load(int3(int2(pixel), 0)).x) / 0.0030000000260770320892333984375f;
    }
    float key = _1705;
    float2 param_1 = centre.xy;
    float param_2 = key;
    bool param_3 = ground;
    float2 param_4 = ambient_frame.xy;
    float3 here = lotPoint(param_1, param_2, param_3, param_4);
    float _1744;
    if (mapped)
    {
        float3 param_5 = here;
        _1744 = 1.0f - indoors(param_5);
    }
    else
    {
        _1744 = 1.0f;
    }
    float outdoor = _1744;
    if (mapped && (!ground))
    {
        float3 param_6 = here - float3(0.0f, 0.0f, 0.25f);
        outdoor = min(outdoor, 1.0f - indoors(param_6));
    }
    float _1769;
    if (mapped)
    {
        float3 _1773;
        if (ground)
        {
            _1773 = here;
        }
        else
        {
            _1773 = here + float3(0.0f, 0.0f, 0.449999988079071044921875f);
        }
        float3 param_7 = _1773;
        _1769 = architecturalExposure(param_7);
    }
    else
    {
        _1769 = 1.0f;
    }
    float skyExposed = _1769;
    if (outdoor <= 0.0f)
    {
        return;
    }
    float2 param_8 = here.xy;
    float2 lot = lotSource(param_8);
    float2 isoPix = float2((lot.x - lot.y) * 32.0f, (lot.x + lot.y) * 16.0f);
    float2 isoFw = fwidth(isoPix);
    float amount = ambient_season.y;
    float springBloom = clamp(ambient_seasonExtra.y, 0.0f, 1.0f);
    float frost = clamp(ambient_seasonExtra2.y, 0.0f, 1.0f);
    float lum = dot(src, float3(0.2125999927520751953125f, 0.715200006961822509765625f, 0.072200000286102294921875f));
    float green = smoothstep(0.0599999986588954925537109375f, 0.2199999988079071044921875f, (src.y - max(src.x, src.z)) / max(src.y, 0.039999999105930328369140625f));
    float _1846;
    if (mapped)
    {
        float2 param_9 = here.xy;
        _1846 = topAt(param_9) - here.z;
    }
    else
    {
        _1846 = 0.0f;
    }
    float over = _1846;
    float covered = smoothstep(0.300000011920928955078125f, 0.89999997615814208984375f, over);
    bool _1867;
    if (mapped)
    {
        _1867 = ambient_season.x > 1.5f;
    }
    else
    {
        _1867 = mapped;
    }
    if (_1867)
    {
        float _1881;
        float _1891;
        for (int i = 0; i < 4; i++)
        {
            if (i == 0)
            {
                _1881 = 1.0f;
            }
            else
            {
                _1881 = (i == 1) ? (-1.0f) : 0.0f;
            }
            if (i == 2)
            {
                _1891 = 1.0f;
            }
            else
            {
                _1891 = (i == 3) ? (-1.0f) : 0.0f;
            }
            float2 offset = float2(_1881, _1891) * 0.3499999940395355224609375f;
            float2 param_10 = here.xy + offset;
            covered += smoothstep(0.300000011920928955078125f, 0.89999997615814208984375f, topAt(param_10) - here.z);
        }
        covered /= 5.0f;
    }
    float3 result = src;
    uint _1923_dummy_parameter;
    bool _1925 = int2(spvTextureSize(simMap, uint(0), _1923_dummy_parameter)).x > 1;
    bool _1935;
    if (_1925)
    {
        _1935 = simMap.Load(int3(int2(pixel), 0)).x > 0.5f;
    }
    else
    {
        _1935 = _1925;
    }
    bool sim = _1935;
    if (ambient_season.x < 0.5f)
    {
        if ((((springBloom > 0.00999999977648258209228515625f) && (skyExposed > 0.001000000047497451305389404296875f)) && ground) && (green > 0.0500000007450580596923828125f))
        {
            float3 param_11 = float3(0.3400000035762786865234375f, 0.449999988079071044921875f, 0.180000007152557373046875f);
            float3 param_12 = src;
            float3 param_13 = srgbToLinear(param_11);
            result = lerp(src, recolor(param_12, param_13), (((green * skyExposed) * springBloom) * 0.180000007152557373046875f).xxx);
        }
        if ((((frost > 0.0199999995529651641845703125f) && (skyExposed > 0.001000000047497451305389404296875f)) && (!sim)) && (green > 0.0500000007450580596923828125f))
        {
            float2 param_14 = lot * 6.0f;
            float2 param_15 = (lot * 19.0f) + 7.099999904632568359375f.xx;
            float rime = smoothstep(0.3499999940395355224609375f, 0.819999992847442626953125f, (valueNoise(param_14) * 0.550000011920928955078125f) + (valueNoise(param_15) * 0.449999988079071044921875f));
            float3 param_16 = float3(0.7799999713897705078125f, 0.86000001430511474609375f, 0.949999988079071044921875f);
            float3 frostTint = srgbToLinear(param_16) * clamp(lum / 0.119999997317790985107421875f, 0.3499999940395355224609375f, 1.25f);
            result = lerp(result, frostTint, ((((frost * rime) * green) * skyExposed) * (ground ? 0.3400000035762786865234375f : 0.2199999988079071044921875f)).xxx);
        }
        float3 _2029 = abs(result - src);
        if (all(bool3(_2029.x < 9.9999997473787516355514526367188e-05f.xxx.x, _2029.y < 9.9999997473787516355514526367188e-05f.xxx.y, _2029.z < 9.9999997473787516355514526367188e-05f.xxx.z)))
        {
            return;
        }
    }
    else
    {
        if (ambient_season.x < 1.5f)
        {
            if (!ground)
            {
                float sunlit = smoothstep(0.02500000037252902984619140625f, 0.13500000536441802978515625f, lum);
                float2 param_17 = (lot * 0.23999999463558197021484375f) + 7.30000019073486328125f.xx;
                float treeHue = valueNoise(param_17);
                float2 param_18 = ((lot * 5.5f) + float2(here.z * 3.099999904632568359375f, (-here.z) * 2.400000095367431640625f)) + 19.1000003814697265625f.xx;
                float leafDetail = valueNoise(param_18);
                float paletteT = clamp((((0.180000007152557373046875f + (0.4199999868869781494140625f * sunlit)) + (0.2800000011920928955078125f * treeHue)) + (0.180000007152557373046875f * leafDetail)) + (0.14000000059604644775390625f * (amount - 0.5f)), 0.0f, 1.0f);
                float turned = ((sim ? 0.0f : green) * outdoor) * smoothstep(0.0599999986588954925537109375f + (0.1599999964237213134765625f * (1.0f - sunlit)), 0.2800000011920928955078125f + (0.180000007152557373046875f * (1.0f - sunlit)), amount);
                float param_19 = paletteT;
                float3 hueSrgb = autumnColorSrgb(param_19);
                float shade = clamp(pow(max(lum, 9.9999997473787516355514526367188e-05f) / 0.054999999701976776123046875f, 0.62000000476837158203125f), 0.37999999523162841796875f, 1.5499999523162841796875f);
                float3 param_20 = hueSrgb;
                float3 turnedColor = srgbToLinear(param_20) * (0.3400000035762786865234375f * shade);
                float blend = lerp(0.62000000476837158203125f, 0.939999997615814208984375f, sunlit);
                result = lerp(src, min(turnedColor, 1.0f.xxx), (turned * blend).xxx);
            }
            else
            {
                float3 param_21 = src;
                float3 param_22 = float3(0.62000000476837158203125f, 0.579999983310699462890625f, 0.300000011920928955078125f);
                result = lerp(src, recolor(param_21, param_22), ((((green * outdoor) * skyExposed) * 0.449999988079071044921875f) * amount).xxx);
                float2 param_23 = here.xy;
                float _2168 = pavedAt(param_23);
                float onTurf = green * (1.0f - (0.949999988079071044921875f * _2168));
                if (mapped && (onTurf > 0.0500000007450580596923828125f))
                {
                    float2 param_24 = here.xy;
                    float param_25 = here.z;
                    float canopy = treeCrownAt(param_24, param_25);
                    for (int i_1 = 0; i_1 < 8; i_1++)
                    {
                        float a = float(i_1) * 0.785398185253143310546875f;
                        float2 dir = float2(cos(a), sin(a));
                        float2 param_26 = here.xy + (dir * 0.800000011920928955078125f);
                        float param_27 = here.z;
                        canopy = max(canopy, 0.89999997615814208984375f * treeCrownAt(param_26, param_27));
                        float2 param_28 = here.xy + (dir * 1.5499999523162841796875f);
                        float param_29 = here.z;
                        canopy = max(canopy, 0.550000011920928955078125f * treeCrownAt(param_28, param_29));
                    }
                    if (canopy > 0.0199999995529651641845703125f)
                    {
                        float2 param_30 = (lot * 2.400000095367431640625f) + 3.7000000476837158203125f.xx;
                        float2 param_31 = (lot * 5.19999980926513671875f) + 11.30000019073486328125f.xx;
                        float patchNoise = (valueNoise(param_30) * 0.64999997615814208984375f) + (valueNoise(param_31) * 0.3499999940395355224609375f);
                        float coverStrength = ((amount * onTurf) * canopy) * smoothstep(0.23999999463558197021484375f, 0.699999988079071044921875f, patchNoise + (0.3400000035762786865234375f * canopy));
                        float2 param_32 = isoPix * 0.25f;
                        float2 param_33 = isoFw * 0.25f;
                        float param_34 = 5.099999904632568359375f;
                        float2 param_35 = isoPix * 0.5f;
                        float2 param_36 = isoFw * 0.5f;
                        float param_37 = 17.299999237060546875f;
                        float clump = (0.560000002384185791015625f * isoPixelHash(param_32, param_33, param_34)) + (0.439999997615814208984375f * isoPixelHash(param_35, param_36, param_37));
                        float threshold = 1.0f - (0.680000007152557373046875f * coverStrength);
                        float leafMask = smoothstep(threshold - 0.0500000007450580596923828125f, threshold + 0.0500000007450580596923828125f, clump);
                        if (leafMask > 0.004999999888241291046142578125f)
                        {
                            float2 param_38 = isoPix * 0.5f;
                            float2 param_39 = isoFw * 0.5f;
                            float param_40 = 41.700000762939453125f;
                            float huePick = isoPixelHash(param_38, param_39, param_40);
                            float2 param_41 = isoPix * 0.5f;
                            float2 param_42 = isoFw * 0.5f;
                            float param_43 = 73.90000152587890625f;
                            float shadePick = isoPixelHash(param_41, param_42, param_43);
                            float3 _2330;
                            if (huePick < 0.37999999523162841796875f)
                            {
                                _2330 = float3(0.4900000095367431640625f, 0.37000000476837158203125f, 0.180000007152557373046875f);
                            }
                            else
                            {
                                bool3 _2343 = (huePick < 0.7400000095367431640625f).xxx;
                                _2330 = float3(_2343.x ? float3(0.439999997615814208984375f, 0.2700000107288360595703125f, 0.14000000059604644775390625f).x : float3(0.3400000035762786865234375f, 0.23000000417232513427734375f, 0.12999999523162841796875f).x, _2343.y ? float3(0.439999997615814208984375f, 0.2700000107288360595703125f, 0.14000000059604644775390625f).y : float3(0.3400000035762786865234375f, 0.23000000417232513427734375f, 0.12999999523162841796875f).y, _2343.z ? float3(0.439999997615814208984375f, 0.2700000107288360595703125f, 0.14000000059604644775390625f).z : float3(0.3400000035762786865234375f, 0.23000000417232513427734375f, 0.12999999523162841796875f).z);
                            }
                            float3 leafSrgb = _2330;
                            leafSrgb *= (0.87999999523162841796875f + (0.20000000298023223876953125f * shadePick));
                            float3 param_44 = leafSrgb;
                            float3 leafLin = srgbToLinear(param_44) * clamp(lum / 0.097999997437000274658203125f, 0.2800000011920928955078125f, 1.17999994754791259765625f);
                            result = lerp(result, leafLin, ((((leafMask * outdoor) * skyExposed) * onTurf) * 0.7200000286102294921875f).xxx);
                        }
                    }
                }
            }
            bool _2382 = ((frost > 0.0199999995529651641845703125f) && (skyExposed > 0.001000000047497451305389404296875f)) && (!sim);
            bool _2389;
            if (_2382)
            {
                _2389 = ground || (green > 0.0500000007450580596923828125f);
            }
            else
            {
                _2389 = _2382;
            }
            if (_2389)
            {
                float2 param_45 = lot * 6.0f;
                float2 param_46 = (lot * 19.0f) + 7.099999904632568359375f.xx;
                float rime_1 = smoothstep(0.37999999523162841796875f, 0.839999973773956298828125f, (valueNoise(param_45) * 0.550000011920928955078125f) + (valueNoise(param_46) * 0.449999988079071044921875f));
                float _2409;
                if (ground)
                {
                    float2 param_47 = here.xy;
                    float _2416 = pavedAt(param_47);
                    _2409 = 1.0f - (0.85000002384185791015625f * _2416);
                }
                else
                {
                    _2409 = green;
                }
                float mask = _2409;
                float3 param_48 = float3(0.7799999713897705078125f, 0.86000001430511474609375f, 0.949999988079071044921875f);
                float3 frostTint_1 = srgbToLinear(param_48) * clamp(lum / 0.119999997317790985107421875f, 0.3499999940395355224609375f, 1.25f);
                result = lerp(result, frostTint_1, ((((frost * rime_1) * mask) * skyExposed) * (ground ? 0.300000011920928955078125f : 0.20000000298023223876953125f)).xxx);
            }
        }
        else
        {
            float winterOutdoor = min(outdoor, skyExposed);
            if (winterOutdoor > 0.0f)
            {
                float3 baseColor = result;
                float3 _2456;
                if (sim)
                {
                    _2456 = baseColor;
                }
                else
                {
                    float3 param_49 = baseColor;
                    float3 param_50 = float3(0.300000011920928955078125f, 0.4199999868869781494140625f, 0.36000001430511474609375f);
                    _2456 = lerp(baseColor, recolor(param_49, param_50), (0.4000000059604644775390625f * green).xxx);
                }
                float3 dull = _2456;
                float deep = clamp(amount - 1.0f, 0.0f, 1.0f);
                bool _2477 = !sim;
                bool _2485;
                if (_2477)
                {
                    _2485 = (!ground) || (!mapped);
                }
                else
                {
                    _2485 = _2477;
                }
                if (_2485)
                {
                    float facing = 1.0f;
                    if (!ground)
                    {
                        float3 p = centre.xyz;
                        float _step = max(1.0f, 1.5f * ambient_view.z);
                        float2 param_51 = pixel - float2(_step, 0.0f);
                        float3 left = positionAt(param_51).xyz;
                        float2 param_52 = pixel + float2(_step, 0.0f);
                        float3 right = positionAt(param_52).xyz;
                        float2 param_53 = pixel - float2(0.0f, _step);
                        float3 up = positionAt(param_53).xyz;
                        float2 param_54 = pixel + float2(0.0f, _step);
                        float3 down = positionAt(param_54).xyz;
                        float3 _2547;
                        if (abs(right.z - p.z) < abs(p.z - left.z))
                        {
                            _2547 = right - p;
                        }
                        else
                        {
                            _2547 = p - left;
                        }
                        float3 dx = _2547;
                        float3 _2572;
                        if (abs(down.z - p.z) < abs(p.z - up.z))
                        {
                            _2572 = down - p;
                        }
                        else
                        {
                            _2572 = p - up;
                        }
                        float3 dy = _2572;
                        float3 n = normalize(cross(dy, dx));
                        if (n.z < 0.0f)
                        {
                            n = -n;
                        }
                        facing = dot(n, float3(0.0f, -0.866025388240814208984375f, 0.5f));
                    }
                    float2 param_55 = lot * 1.2999999523162841796875f;
                    float2 param_56 = (lot * 4.099999904632568359375f) + 5.0f.xx;
                    float drift = (valueNoise(param_55) * 0.60000002384185791015625f) + (valueNoise(param_56) * 0.4000000059604644775390625f);
                    float depth = (amount * (1.0f - (0.300000011920928955078125f * covered))) * 1.25f;
                    float lying = smoothstep(drift - 0.1500000059604644775390625f, drift + 0.1500000059604644775390625f, depth);
                    float _2629;
                    if (mapped)
                    {
                        float2 param_57 = pixel;
                        _2629 = groundAt(param_57);
                    }
                    else
                    {
                        _2629 = -100000.0f;
                    }
                    float baseAlt = _2629;
                    float _2641;
                    if (baseAlt > (-10000.0f))
                    {
                        float2 param_58 = centre.xy;
                        float param_59 = baseAlt / 27.7128124237060546875f;
                        bool param_60 = true;
                        float2 param_61 = ambient_frame.xy;
                        _2641 = lotPoint(param_58, param_59, param_60, param_61).z;
                    }
                    else
                    {
                        _2641 = 0.0f;
                    }
                    float roofBase = _2641;
                    bool _2663 = (!ground) && mapped;
                    bool _2672;
                    if (_2663)
                    {
                        _2672 = (here.z - roofBase) > 2.124000072479248046875f;
                    }
                    else
                    {
                        _2672 = _2663;
                    }
                    bool _2683;
                    if (_2672)
                    {
                        float2 param_62 = here.xy;
                        _2683 = (topAt(param_62) - here.z) < 0.3499999940395355224609375f;
                    }
                    else
                    {
                        _2683 = _2672;
                    }
                    bool onRoof = _2683;
                    float _2686;
                    if (ground)
                    {
                        _2686 = 1.0f;
                    }
                    else
                    {
                        float _2691;
                        if (onRoof)
                        {
                            _2691 = smoothstep(0.2199999988079071044921875f, 0.560000002384185791015625f, facing);
                        }
                        else
                        {
                            _2691 = smoothstep((green > 0.5f) ? 0.25f : 0.449999988079071044921875f, (green > 0.5f) ? 0.60000002384185791015625f : 0.800000011920928955078125f, facing);
                        }
                        _2686 = _2691;
                    }
                    float settle = _2686;
                    float2 param_63 = lot * 5.0f;
                    float2 param_64 = lot * 17.0f;
                    float crust = (valueNoise(param_63) * 0.5f) + (valueNoise(param_64) * 0.5f);
                    float2 param_65 = (lot * 0.449999988079071044921875f) + 9.0f.xx;
                    float swell = valueNoise(param_65);
                    float3 snow = lerp(float3(0.699999988079071044921875f, 0.7599999904632568359375f, 0.87999999523162841796875f), float3(0.819999992847442626953125f, 0.85000002384185791015625f, 0.910000026226043701171875f), smoothstep(0.20000000298023223876953125f, 0.800000011920928955078125f, swell).xxx) * ((0.839999973773956298828125f + (0.100000001490116119384765625f * crust)) + ((0.5f * (1.0f - (0.699999988079071044921875f * deep))) * (lum - 0.300000011920928955078125f)));
                    float daySun = max(0.0f, 1.0f - ambient_weather.w);
                    float2 param_66 = lot * 28.0f;
                    float2 param_67 = (lot * 47.0f) + 31.700000762939453125f.xx;
                    float glint = smoothstep(0.60000002384185791015625f, 0.89999997615814208984375f, valueNoise(param_66) * valueNoise(param_67)) * daySun;
                    snow += ((float3(0.0240000002086162567138671875f, 0.02999999932944774627685546875f, 0.0419999994337558746337890625f) * glint) * winterOutdoor);
                    float _2784;
                    if (ground)
                    {
                        _2784 = lying * winterOutdoor;
                    }
                    else
                    {
                        _2784 = ((lying * settle) * winterOutdoor) * (onRoof ? 0.89999997615814208984375f : 0.85000002384185791015625f);
                    }
                    float cover = _2784;
                    result = lerp(dull, snow, clamp(cover, 0.0f, 1.0f).xxx);
                }
                else
                {
                    result = dull;
                }
                if (mapped)
                {
                    float2 param_68 = pixel;
                    float terrain = groundAt(param_68);
                    float _2817;
                    if (ground)
                    {
                        _2817 = here.z;
                    }
                    else
                    {
                        float _2825;
                        if (terrain > (-10000.0f))
                        {
                            float2 param_69 = centre.xy;
                            float param_70 = terrain / 27.7128124237060546875f;
                            bool param_71 = true;
                            float2 param_72 = ambient_frame.xy;
                            _2825 = lotPoint(param_69, param_70, param_71, param_72).z;
                        }
                        else
                        {
                            _2825 = here.z;
                        }
                        _2817 = _2825;
                    }
                    float base = _2817;
                    float full = (0.039999999105930328369140625f * min(amount, 1.0f)) + (0.300000011920928955078125f * deep);
                    float _2855;
                    if (ground)
                    {
                        _2855 = 0.0f;
                    }
                    else
                    {
                        _2855 = max(0.0f, here.z - base);
                    }
                    float h0 = _2855;
                    float top = full * 1.9500000476837158203125f;
                    float wall = 0.0f;
                    if (((!ground) && (h0 < top)) && (!sim))
                    {
                        for (int i_2 = 0; i_2 < 4; i_2++)
                        {
                            float2 offset_1 = float2((i_2 < 2) ? 1.0f : (-1.0f), ((i_2 & 1) == 1) ? 1.0f : (-1.0f)) * 0.300000011920928955078125f;
                            float3 param_73 = float3(here.xy + offset_1, base + 0.100000001490116119384765625f);
                            wall = max(wall, indoors(param_73));
                        }
                    }
                    if (((h0 < top) && (wall < 0.0500000007450580596923828125f)) && (!sim))
                    {
                        float2 param_74 = here.xy + float2(0.3499999940395355224609375f, 0.0f);
                        float2 param_75 = here.xy - float2(0.3499999940395355224609375f, 0.0f);
                        float2 param_76 = here.xy + float2(0.0f, 0.3499999940395355224609375f);
                        float2 param_77 = here.xy - float2(0.0f, 0.3499999940395355224609375f);
                        float nearWall = smoothstep(0.20000000298023223876953125f, 1.2000000476837158203125f, 0.25f * (((max(0.0f, topAt(param_74) - base) + max(0.0f, topAt(param_75) - base)) + max(0.0f, topAt(param_76) - base)) + max(0.0f, topAt(param_77) - base)));
                        int steps = (amount > 1.0f) ? 6 : 3;
                        float dt = ((top - h0) / 0.4000000059604644775390625f) / float(steps);
                        float hitT = -1.0f;
                        float hitS = 0.0f;
                        float nextS = -1.0f;
                        float prevDiff = 0.0f;
                        float prevS = 0.0f;
                        bool hasPrev = false;
                        int _2986 = steps - 1;
                        for (int i_3 = _2986; i_3 >= 0; i_3--)
                        {
                            float t = float(i_3) * dt;
                            float2 param_78 = here.xy + (0.5f * t).xx;
                            float param_79 = base;
                            float param_80 = full;
                            float param_81 = amount;
                            float param_82 = nearWall;
                            float sh = snowHeight(param_78, param_79, param_80, param_81, param_82);
                            float diff = sh - (h0 + (0.4000000059604644775390625f * t));
                            if (diff > 0.0f)
                            {
                                hitT = t;
                                hitS = sh;
                                if (hasPrev)
                                {
                                    hitT += ((dt * diff) / max(diff - prevDiff, 9.9999997473787516355514526367188e-05f));
                                    nextS = prevS;
                                }
                                break;
                            }
                            prevDiff = diff;
                            prevS = sh;
                            hasPrev = true;
                        }
                        if (hitT >= 0.0f)
                        {
                            float2 q = here.xy + (0.5f * hitT).xx;
                            bool _3061 = nextS >= 0.0f;
                            bool _3072;
                            if (_3061)
                            {
                                _3072 = (hitS - nextS) > ((0.4799999892711639404296875f * dt) + 0.014999999664723873138427734375f);
                            }
                            else
                            {
                                _3072 = _3061;
                            }
                            bool face = _3072;
                            float e = 0.119999997317790985107421875f;
                            float s0 = h0 + (0.4000000059604644775390625f * hitT);
                            float2 param_83 = q + float2(e, 0.0f);
                            float param_84 = base;
                            float param_85 = full;
                            float param_86 = amount;
                            float param_87 = nearWall;
                            float gx = (snowHeight(param_83, param_84, param_85, param_86, param_87) - s0) / e;
                            float2 param_88 = q + float2(0.0f, e);
                            float param_89 = base;
                            float param_90 = full;
                            float param_91 = amount;
                            float param_92 = nearWall;
                            float gy = (snowHeight(param_88, param_89, param_90, param_91, param_92) - s0) / e;
                            float3 n2 = normalize(float3(-gx, -gy, 1.0f));
                            float3 sunDir = normalize(ambient_sunLot.xyz);
                            float lit = clamp(1.0f + (0.89999997615814208984375f * (dot(n2, sunDir) - sunDir.z)), 0.699999988079071044921875f, 1.2000000476837158203125f);
                            float2 param_93 = q;
                            float2 lq = lotSource(param_93);
                            float2 param_94 = lq * 5.0f;
                            float2 param_95 = lq * 17.0f;
                            float qCrust = (valueNoise(param_94) * 0.5f) + (valueNoise(param_95) * 0.5f);
                            float2 param_96 = (lq * 0.449999988079071044921875f) + 9.0f.xx;
                            float qSwell = valueNoise(param_96);
                            float3 qSnow = lerp(float3(0.699999988079071044921875f, 0.7599999904632568359375f, 0.87999999523162841796875f), float3(0.819999992847442626953125f, 0.85000002384185791015625f, 0.910000026226043701171875f), smoothstep(0.20000000298023223876953125f, 0.800000011920928955078125f, qSwell).xxx) * (0.86000001430511474609375f + (0.07999999821186065673828125f * qCrust));
                            float under = ground ? lum : 0.20999999344348907470703125f;
                            qSnow *= (1.0f + ((0.5f * (1.0f - smoothstep(0.02999999932944774627685546875f, 0.119999997317790985107421875f, hitS))) * (under - 0.300000011920928955078125f)));
                            float2 param_97 = q;
                            float pressed = pressedAt(param_97);
                            if (pressed > 0.0f)
                            {
                                float2 toSun = normalize(ambient_sunLot.xy + 9.9999997473787516355514526367188e-05f.xx) * 0.02999999932944774627685546875f;
                                float2 param_98 = q + toSun;
                                float shaded = clamp(pressed - pressedAt(param_98), 0.0f, 1.0f);
                                float2 param_99 = q - toSun;
                                float facing2 = clamp(pressed - pressedAt(param_99), 0.0f, 1.0f);
                                qSnow = lerp(qSnow, float3(0.65799999237060546875f, 0.714399993419647216796875f, 0.827199995517730712890625f), (0.25f * pressed).xxx) * ((1.0f - (0.119999997317790985107421875f * shaded)) + (0.070000000298023223876953125f * facing2));
                            }
                            float daySun2 = max(0.0f, 1.0f - ambient_weather.w);
                            float2 param_100 = lq * 28.0f;
                            float2 param_101 = (lq * 47.0f) + 31.700000762939453125f.xx;
                            float qGlint = smoothstep(0.60000002384185791015625f, 0.89999997615814208984375f, valueNoise(param_100) * valueNoise(param_101)) * daySun2;
                            qSnow += (float3(0.0240000002086162567138671875f, 0.02999999932944774627685546875f, 0.0419999994337558746337890625f) * qGlint);
                            float3 _3263;
                            if (face)
                            {
                                _3263 = float3(0.699999988079071044921875f, 0.7599999904632568359375f, 0.87999999523162841796875f) * (0.800000011920928955078125f + (0.0599999986588954925537109375f * qCrust));
                            }
                            else
                            {
                                _3263 = qSnow * lit;
                            }
                            float3 seen = _3263;
                            float edge = smoothstep(0.0040000001899898052215576171875f, 0.0199999995529651641845703125f, hitS);
                            result = lerp(result, seen, edge.xxx);
                            if (edge > 0.5f)
                            {
                                lift = float4(hitT, (base + hitS) - here.z, 1.0f, 0.0f);
                            }
                        }
                    }
                }
            }
            else
            {
                float3 _3300 = abs(result - src);
                if (all(bool3(_3300.x < 9.9999997473787516355514526367188e-05f.xxx.x, _3300.y < 9.9999997473787516355514526367188e-05f.xxx.y, _3300.z < 9.9999997473787516355514526367188e-05f.xxx.z)))
                {
                    return;
                }
            }
        }
    }
    color = float4(result, 1.0f);
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    gl_FragCoord = stage_input.gl_FragCoord;
    gl_FragCoord.w = 1.0 / gl_FragCoord.w;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.color = color;
    stage_output.lift = lift;
    return stage_output;
}
