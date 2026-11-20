static const float2 _3118[8] = { float2(1.0f, 0.0f), float2(-1.0f, 0.0f), float2(0.0f, 1.0f), float2(0.0f, -1.0f), 0.707099974155426025390625f.xx, float2(-0.707099974155426025390625f, 0.707099974155426025390625f), float2(0.707099974155426025390625f, -0.707099974155426025390625f), (-0.707099974155426025390625f).xx };

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
Texture2D<float4> worldDepthMap : register(t7, space2);
SamplerState _worldDepthMap_sampler : register(s7, space2);
Texture2D<float4> heightMap : register(t2, space2);
SamplerState _heightMap_sampler : register(s2, space2);
Texture2D<float4> liftMap : register(t5, space2);
SamplerState _liftMap_sampler : register(s5, space2);
Texture2D<float4> footMap : register(t4, space2);
SamplerState _footMap_sampler : register(s4, space2);

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

float _noise(float2 pixel)
{
    return frac(52.98291778564453125f * frac(dot(pixel, float2(0.067110560834407806396484375f, 0.005837149918079376220703125f))));
}

float3 lotPoint(float2 scene, float key, bool terrain, float2 frame)
{
    float2 local = scene - frame;
    float _107;
    if (terrain)
    {
        _107 = ((16.0f * key) - local.y) / 40.0f;
    }
    else
    {
        _107 = ((16.0f * key) - local.y) / 52.799999237060546875f;
    }
    float h = _107;
    float _131;
    if (terrain)
    {
        _131 = key;
    }
    else
    {
        _131 = key - (0.800000011920928955078125f * h);
    }
    float s = _131;
    float u = local.x / 32.0f;
    return float3(0.5f * (s + u), 0.5f * (s - u), h);
}

float groundAt(float2 pixel)
{
    float ground = groundMap.Load(int3(int2(pixel), 0)).x;
    float _302;
    if (ground < 1.0f)
    {
        _302 = (((0.89999997615814208984375f - ground) / 0.0030000000260770320892333984375f) + ambient_view.w) * 27.7128124237060546875f;
    }
    else
    {
        _302 = -100000.0f;
    }
    return _302;
}

float riseAt(float2 pixel, float z)
{
    float2 param = pixel;
    float ground = groundAt(param);
    float2 param_1 = pixel + float2(0.0f, 4.0f);
    float below = groundAt(param_1);
    if ((ground < (-10000.0f)) || (below < (-10000.0f)))
    {
        return 0.0f;
    }
    float slope = ((below - ground) * ambient_view.z) / 4.0f;
    float _862;
    if (slope > 0.100000001490116119384765625f)
    {
        _862 = max(0.0f, (z - ground) / ((slope * 4.0f) / 3.0f));
    }
    else
    {
        _862 = 0.0f;
    }
    return _862;
}

int maskAt(int2 at, int band)
{
    uint _320_dummy_parameter;
    int2 size = int2(spvTextureSize(indoorMap, uint(0), _320_dummy_parameter));
    bool _323 = at.x < 0;
    bool _330;
    if (!_323)
    {
        _330 = at.y < 0;
    }
    else
    {
        _330 = _323;
    }
    bool _339;
    if (!_330)
    {
        _339 = at.x >= size.x;
    }
    else
    {
        _339 = _330;
    }
    bool _349;
    if (!_339)
    {
        _349 = at.y >= (size.y / 6);
    }
    else
    {
        _349 = _339;
    }
    if (_349)
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
    float _384;
    if (code == 0)
    {
        _384 = fallback;
    }
    else
    {
        _384 = ambient_contact.w + (float(code - 1) / 8.0f);
    }
    return _384;
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
    float _477;
    float _490;
    float2 _538;
    float2 _546;
    for (int i = 0; i < 4; i++)
    {
        if ((i & 1) == 1)
        {
            _477 = f.x;
        }
        else
        {
            _477 = 1.0f - f.x;
        }
        if ((i >> 1) == 1)
        {
            _490 = f.y;
        }
        else
        {
            _490 = 1.0f - f.y;
        }
        float weight = _477 * _490;
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
            _538 = float2(1.0f, -1.0f);
        }
        else
        {
            if (part == 2)
            {
                _546 = float2(-1.0f, 1.0f);
            }
            else
            {
                bool2 _557 = (part == 3).xx;
                _546 = float2(_557.x ? (-1.0f).xx.x : 1.0f.xx.x, _557.y ? (-1.0f).xx.y : 1.0f.xx.y);
            }
            _538 = _546;
        }
        float2 toward = _538;
        sum += (weight * smoothstep(-0.0599999986588954925537109375f, 0.0599999986588954925537109375f, dot(((lot.xy + ambient_frame.w.xx) - float2(at)) - 0.5f.xx, toward)));
    }
    return sum;
}

float2 hiddenAt(float3 q)
{
    float2 scene = float2(32.0f * (q.x - q.y), (16.0f * (q.x + q.y)) - (40.0f * q.z)) + ambient_frame.xy;
    float key = (q.x + q.y) + (0.800000011920928955078125f * q.z);
    if (ambient_shadowView.w > 0.5f)
    {
        uint _979_dummy_parameter;
        int2 wSize = int2(spvTextureSize(worldDepthMap, uint(0), _979_dummy_parameter));
        float2 wp = floor(ambient_shadowView.xy + (scene * ambient_shadowView.z)) + 0.5f.xx;
        bool _994 = any(bool2(wp.x < 0.0f.xx.x, wp.y < 0.0f.xx.y));
        bool _1004;
        if (!_994)
        {
            _1004 = wp.x >= float(wSize.x);
        }
        else
        {
            _1004 = _994;
        }
        bool _1014;
        if (!_1004)
        {
            _1014 = wp.y >= float(wSize.y);
        }
        else
        {
            _1014 = _1004;
        }
        if (_1014)
        {
            return 0.0f.xx;
        }
        float wDepth = worldDepthMap.Load(int3(int2(wp), 0)).x;
        if (wDepth < 1.0f)
        {
            float behind = ((0.89999997615814208984375f - wDepth) / 0.0030000000260770320892333984375f) - key;
            return float2(clamp((behind + 0.4000000059604644775390625f) / 0.300000011920928955078125f, 0.0f, 1.0f), clamp((behind + 0.100000001490116119384765625f) / 0.20000000298023223876953125f, 0.0f, 1.0f) * clamp((1.5f - behind) / 0.5f, 0.0f, 1.0f));
        }
        return 0.0f.xx;
    }
    float2 pixel = floor(ambient_view.xy + (scene * ambient_view.z)) + 0.5f.xx;
    bool _1068 = any(bool2(pixel.x < 0.0f.xx.x, pixel.y < 0.0f.xx.y));
    bool _1077;
    if (!_1068)
    {
        _1077 = pixel.x >= ambient_extent.x;
    }
    else
    {
        _1077 = _1068;
    }
    bool _1086;
    if (!_1077)
    {
        _1086 = pixel.y >= ambient_extent.y;
    }
    else
    {
        _1086 = _1077;
    }
    if (_1086)
    {
        return float2(1.0f, 0.0f);
    }
    float depth = depthMap.Load(int3(int2(pixel), 0)).x;
    if (depth < 1.0f)
    {
        float behind_1 = ((0.89999997615814208984375f - depth) / 0.0030000000260770320892333984375f) - key;
        return float2(clamp((behind_1 + 0.4000000059604644775390625f) / 0.300000011920928955078125f, 0.0f, 1.0f), clamp((behind_1 + 0.100000001490116119384765625f) / 0.20000000298023223876953125f, 0.0f, 1.0f) * clamp((1.5f - behind_1) / 0.5f, 0.0f, 1.0f));
    }
    float ground = groundMap.Load(int3(int2(pixel), 0)).x;
    if (ground >= 1.0f)
    {
        return 0.0f.xx;
    }
    float2 param = scene;
    float param_1 = ((0.89999997615814208984375f - ground) / 0.0030000000260770320892333984375f) + ambient_view.w;
    bool param_2 = true;
    float2 param_3 = ambient_frame.xy;
    float3 lot = lotPoint(param, param_1, param_2, param_3);
    return float2(clamp(((((lot.x + lot.y) + (0.800000011920928955078125f * lot.z)) - key) + 0.4000000059604644775390625f) / 0.300000011920928955078125f, 0.0f, 1.0f), 0.0f);
}

float3 sunMarch(float3 here, float jitter, float start)
{
    float lit = 1.0f;
    float litColumn = 1.0f;
    float litShell = 1.0f;
    uint _1956_dummy_parameter;
    float2 gridSize = float2(int2(spvTextureSize(heightMap, uint(0), _1956_dummy_parameter)));
    float span = max((ambient_sunLot.w + 0.20000000298023223876953125f) - start, 0.5f);
    for (int i = 1; i <= 32; i++)
    {
        float u = (float(i) - jitter) / 32.0f;
        float t = start + (span * ((0.550000011920928955078125f * u) + ((0.449999988079071044921875f * u) * u)));
        float3 q = here + (ambient_sunLot.xyz * t);
        float2 texel = (q.xy + ambient_frame.w.xx) * ambient_frame.z;
        bool _2014 = any(bool2(texel.x < 1.0f.xx.x, texel.y < 1.0f.xx.y));
        bool _2024;
        if (!_2014)
        {
            float2 _2021 = gridSize - 1.0f.xx;
            _2024 = any(bool2(texel.x > _2021.x, texel.y > _2021.y));
        }
        else
        {
            _2024 = _2014;
        }
        if (_2024)
        {
            break;
        }
        float4 tops = (0.5f.xxxx - heightMap.GatherRed(_heightMap_sampler, texel / gridSize)) * 64.0f;
        float4 blocked = clamp(((tops - q.z.xxxx) - 0.07999999821186065673828125f.xxxx) / ((0.0900000035762786865234375f * t) + 0.0500000007450580596923828125f).xxxx, 0.0f.xxxx, 1.0f.xxxx);
        float2 f = frac(texel - 0.5f.xx);
        float3 param = q;
        float2 seen = hiddenAt(param);
        float column = lerp(lerp(blocked.w, blocked.z, f.x), lerp(blocked.x, blocked.y, f.x), f.y);
        float shell = seen.y * (1.0f / (1.0f + (0.0599999986588954925537109375f * t)));
        float occ = max(column * seen.x, shell);
        lit = min(lit, 1.0f - occ);
        litColumn = min(litColumn, 1.0f - column);
        litShell = min(litShell, 1.0f - shell);
        if ((lit <= 0.00999999977648258209228515625f) && (litColumn <= 0.00999999977648258209228515625f))
        {
            break;
        }
    }
    return float3(lit, litColumn, litShell);
}

float4 throughWindow(float3 lot)
{
    float3 sun = ambient_sunLot.xyz;
    float across = length(sun.xy);
    if (across < 0.001000000047497451305389404296875f)
    {
        return 0.0f.xxxx;
    }
    float2 start = lot.xy + ambient_frame.w.xx;
    int2 cell = int2(floor(start));
    int2 param = cell;
    int param_1 = 0;
    float param_2 = lot.z;
    float ground = floorAt(param, param_1, param_2);
    int row = int(lot.z > ((ground + 2.9500000476837158203125f) - 0.100000001490116119384765625f));
    float _1317;
    if (row == 0)
    {
        _1317 = ground;
    }
    else
    {
        int2 param_3 = cell;
        int param_4 = 1;
        float param_5 = ground + 2.9500000476837158203125f;
        _1317 = floorAt(param_3, param_4, param_5);
    }
    float floorHeight = _1317;
    if (lot.z > ((floorHeight + 2.9500000476837158203125f) - 0.100000001490116119384765625f))
    {
        return 0.0f.xxxx;
    }
    float2 direction = sun.xy / across.xx;
    float rise = sun.z / across;
    int2 param_6 = cell;
    int param_7 = row;
    int room = maskAt(param_6, param_7);
    if (room < 2)
    {
        return 0.0f.xxxx;
    }
    int2 stepDir = int2(sign(direction));
    float2 delta = abs(1.0f.xx / (direction + (float2(bool2(direction.x == 0.0f.xx.x, direction.y == 0.0f.xx.y)) * 9.9999997473787516355514526367188e-06f)));
    float2 next = ((float2(cell) + max(float2(stepDir), 0.0f.xx)) - start) / (direction + (float2(bool2(direction.x == 0.0f.xx.x, direction.y == 0.0f.xx.y)) * 9.9999997473787516355514526367188e-06f));
    float _1410;
    int2 _1437;
    float _1487;
    for (int i = 0; i < 12; i++)
    {
        bool alongX = next.x < next.y;
        if (alongX)
        {
            _1410 = next.x;
        }
        else
        {
            _1410 = next.y;
        }
        float t = _1410;
        float height = (lot.z - floorHeight) + (t * rise);
        if (height > 2.4500000476837158203125f)
        {
            return 0.0f.xxxx;
        }
        if (alongX)
        {
            _1437 = int2(stepDir.x, 0);
        }
        else
        {
            _1437 = int2(0, stepDir.y);
        }
        int2 to = cell + _1437;
        int2 param_8 = to;
        int param_9 = row;
        int beyond = maskAt(param_8, param_9);
        if ((beyond >> 1) != (room >> 1))
        {
            int2 param_10 = cell;
            int param_11 = row;
            bool _1468 = (maskAt(param_10, param_11) & 1) == 0;
            bool _1475;
            if (!_1468)
            {
                _1475 = (beyond & 1) == 0;
            }
            else
            {
                _1475 = _1468;
            }
            if (_1475)
            {
                return 0.0f.xxxx;
            }
            float2 crossing = start + (direction * t);
            if (alongX)
            {
                _1487 = crossing.y;
            }
            else
            {
                _1487 = crossing.x;
            }
            float along = frac(_1487);
            float open = ((smoothstep(0.670000016689300537109375f, 0.829999983310699462890625f, height) * (1.0f - smoothstep(2.269999980926513671875f, 2.4300000667572021484375f, height))) * smoothstep(0.07999999821186065673828125f, 0.20000000298023223876953125f, along)) * smoothstep(0.07999999821186065673828125f, 0.20000000298023223876953125f, 1.0f - along);
            return float4(open, (crossing - ambient_frame.w.xx) + (direction * 1.5f), lot.z + ((t + 1.5f) * rise));
        }
        cell = to;
        if (alongX)
        {
            next.x += delta.x;
        }
        else
        {
            next.y += delta.y;
        }
    }
    return 0.0f.xxxx;
}

float2 lotSource(float2 camera)
{
    float2 size = ambient_drift.zw;
    int orientation = int(ambient_clouds.z + 0.5f);
    float2 _643;
    if (orientation == 1)
    {
        _643 = float2(camera.y, size.y - camera.x);
    }
    else
    {
        float2 _657;
        if (orientation == 2)
        {
            _657 = size - camera;
        }
        else
        {
            float2 _666;
            if (orientation == 3)
            {
                _666 = float2(size.x - camera.y, camera.x);
            }
            else
            {
                _666 = camera;
            }
            _657 = _666;
        }
        _643 = _657;
    }
    return _643;
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

float cloudLight(float3 lot)
{
    float cover = ambient_clouds.x;
    bool _1172 = cover <= 0.0f;
    bool _1179;
    if (!_1172)
    {
        _1179 = ambient_clouds.y <= 0.0f;
    }
    else
    {
        _1179 = _1172;
    }
    if (_1179)
    {
        return 1.0f;
    }
    float3 sun = ambient_sunLot.xyz;
    float2 param = lot.xy + ((sun.xy * (ambient_clouds.w - lot.z)) / max(sun.z, 0.25f).xx);
    float2 source = lotSource(param);
    float2 q = (source - ambient_drift.xy) / 9.0f.xx;
    float2 param_1 = q;
    float2 param_2 = (q * 2.0299999713897705078125f) + 17.0f.xx;
    float2 param_3 = (q * 4.0100002288818359375f) + 41.0f.xx;
    float2 param_4 = (q * 8.05000019073486328125f) + 7.0f.xx;
    float field = (((0.5f * valueNoise(param_1)) + (0.25f * valueNoise(param_2))) + (0.1500000059604644775390625f * valueNoise(param_3))) + (0.100000001490116119384765625f * valueNoise(param_4));
    float threshold = lerp(0.7200000286102294921875f, 0.180000007152557373046875f, cover);
    float density = smoothstep(threshold - 0.07999999821186065673828125f, threshold + 0.100000001490116119384765625f, field);
    return 1.0f - (ambient_clouds.y * density);
}

float2 sceneOf(float3 q)
{
    return float2(32.0f * (q.x - q.y), (16.0f * (q.x + q.y)) - (40.0f * q.z)) + ambient_frame.xy;
}

float lampNear(float3 q)
{
    float3 param = q;
    float2 at = sceneOf(param);
    float best = 0.0f;
    int lampCount = int(ambient_lamp.x);
    for (int index = 0; index < 16; index++)
    {
        if (index >= lampCount)
        {
            break;
        }
        float4 lamp = ambient_lamps[index];
        best = max(best, lamp.w * clamp(1.0f - (length(lamp.xy - at) / (0.800000011920928955078125f * ambient_lamp.y)), 0.0f, 1.0f));
    }
    return clamp(best * 2.0f, 0.0f, 1.0f);
}

float windowSpill(float3 here, float onGround)
{
    float2 at = here.xy + ambient_frame.w.xx;
    int2 cell = int2(floor(at));
    int2 param = cell;
    int param_1 = 0;
    float param_2 = here.z;
    float ground = floorAt(param, param_1, param_2);
    bool _1629 = onGround <= 0.5f;
    bool _1639;
    if (!_1629)
    {
        _1639 = here.z > ((ground + 2.9500000476837158203125f) - 0.100000001490116119384765625f);
    }
    else
    {
        _1639 = _1629;
    }
    bool _1648;
    if (!_1639)
    {
        int2 param_3 = cell;
        int param_4 = 0;
        _1648 = maskAt(param_3, param_4) >= 2;
    }
    else
    {
        _1648 = _1639;
    }
    if (_1648)
    {
        return 0.0f;
    }
    float best = 0.0f;
    float3 source = 0.0f.xxx;
    float3 glass = 0.0f.xxx;
    int2 _1707;
    int2 _1714;
    for (int y = -3; y <= 3; y++)
    {
        for (int x = -3; x <= 3; x++)
        {
            int2 room = cell + int2(x, y);
            int2 param_5 = room;
            int param_6 = 0;
            int code = maskAt(param_5, param_6);
            bool _1685 = code < 2;
            bool _1692;
            if (!_1685)
            {
                _1692 = (code & 1) == 0;
            }
            else
            {
                _1692 = _1685;
            }
            if (_1692)
            {
                continue;
            }
            for (int d = 0; d < 4; d++)
            {
                if (d == 0)
                {
                    _1707 = int2(1, 0);
                }
                else
                {
                    if (d == 1)
                    {
                        _1714 = int2(-1, 0);
                    }
                    else
                    {
                        bool2 _1724 = (d == 2).xx;
                        _1714 = int2(_1724.x ? int2(0, 1).x : int2(0, -1).x, _1724.y ? int2(0, 1).y : int2(0, -1).y);
                    }
                    _1707 = _1714;
                }
                int2 _step = _1707;
                int2 param_7 = room + _step;
                int param_8 = 0;
                int outside = maskAt(param_7, param_8);
                bool _1736 = outside >= 2;
                bool _1743;
                if (!_1736)
                {
                    _1743 = (outside & 1) == 0;
                }
                else
                {
                    _1743 = _1736;
                }
                if (_1743)
                {
                    continue;
                }
                float2 offset = at - ((float2(room) + 0.5f.xx) + (float2(_step) * 0.5f));
                float out_ = dot(offset, float2(_step));
                if (out_ < 0.0f)
                {
                    continue;
                }
                float side = max(0.0f, abs(dot(offset, float2(float(_step.y), float(_step.x)))) - 0.300000011920928955078125f);
                float fall = max(0.0f, 1.0f - (length(float2(out_, side)) / 2.599999904632568359375f));
                float clear = smoothstep(0.64999997615814208984375f, 0.89999997615814208984375f, (1.5f * out_) / (out_ + 1.0f));
                if (((fall * fall) * clear) > best)
                {
                    best = (fall * fall) * clear;
                    source = float3((float2(room) + 0.5f.xx) - ambient_frame.w.xx, ground + 1.5f);
                    glass = float3(((float2(room) + 0.5f.xx) + (float2(_step) * 0.5f)) - ambient_frame.w.xx, ground + 1.39999997615814208984375f);
                }
            }
        }
    }
    if (best <= 0.0f)
    {
        return 0.0f;
    }
    uint _1858_dummy_parameter;
    float2 gridSize = float2(int2(spvTextureSize(heightMap, uint(0), _1858_dummy_parameter)));
    float2 toGlass = glass.xy - here.xy;
    float span = length(toGlass);
    if (span < 0.4000000059604644775390625f)
    {
        float3 param_9 = source;
        return best * lampNear(param_9);
    }
    float3 end = float3(glass.xy - ((toGlass / span.xx) * 0.3499999940395355224609375f), glass.z);
    float open = 1.0f;
    for (int i = 1; i <= 6; i++)
    {
        float3 q = lerp(here, end, (float(i) / 6.0f).xxx);
        float top = (0.5f - heightMap.Sample(_heightMap_sampler, ((q.xy + ambient_frame.w.xx) * ambient_frame.z) / gridSize).x) * 64.0f;
        open = min(open, 1.0f - clamp(((top - q.z) - 0.100000001490116119384765625f) / 0.300000011920928955078125f, 0.0f, 1.0f));
    }
    float3 param_10 = source;
    return (best * open) * lampNear(param_10);
}

float architecturalExposure(float3 _point)
{
    float2 size = ambient_drift.zw;
    int orientation = int(ambient_clouds.z + 0.5f);
    float2 _696;
    if (orientation == 1)
    {
        _696 = float2(_point.y, size.y - _point.x);
    }
    else
    {
        float2 _710;
        if (orientation == 2)
        {
            _710 = size - _point.xy;
        }
        else
        {
            float2 _720;
            if (orientation == 3)
            {
                _720 = float2(size.x - _point.y, _point.x);
            }
            else
            {
                _720 = _point.xy;
            }
            _710 = _720;
        }
        _696 = _710;
    }
    float2 source = _696;
    bool _740 = any(bool2(source.x < 0.0f.xx.x, source.y < 0.0f.xx.y));
    bool _748;
    if (!_740)
    {
        _748 = any(bool2(source.x >= size.x, source.y >= size.y));
    }
    else
    {
        _748 = _740;
    }
    if (_748)
    {
        return 1.0f;
    }
    uint _756_dummy_parameter;
    float2 texelsPerTile = float2(int2(spvTextureSize(overheadMap, uint(0), _756_dummy_parameter))) / max(size, 1.0f.xx);
    int2 at = int2(floor(source * texelsPerTile));
    uint _770_dummy_parameter;
    int2 _770 = int2(spvTextureSize(overheadMap, uint(0), _770_dummy_parameter));
    if (any(bool2(at.x >= _770.x, at.y >= _770.y)))
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

float smoothNoise(float2 q)
{
    float2 i = floor(q);
    float2 f = frac(q);
    f = ((f * f) * f) * ((f * ((f * 6.0f) - 15.0f.xx)) + 10.0f.xx);
    float2 param = i;
    float2 param_1 = i + float2(1.0f, 0.0f);
    float2 param_2 = i + float2(0.0f, 1.0f);
    float2 param_3 = i + 1.0f.xx;
    return lerp(lerp(hash12(param), hash12(param_1), f.x), lerp(hash12(param_2), hash12(param_3), f.x), f.y);
}

void frag_main()
{
    float2 pixel = floor(gl_FragCoord.xy) + 0.5f.xx;
    bool _2138 = pixel.x >= ambient_extent.x;
    bool _2147;
    if (!_2138)
    {
        _2147 = pixel.y >= ambient_extent.y;
    }
    else
    {
        _2147 = _2138;
    }
    if (_2147)
    {
        color = float4(0.5f, 0.5f, 0.5f, 1.0f);
        return;
    }
    float2 param = pixel;
    float4 centre = positionAt(param);
    if (centre.w > 0.75f)
    {
        color = float4(0.5f, 0.5f, 0.5f, 1.0f);
        return;
    }
    float3 p = centre.xyz;
    float scale = ambient_view.z;
    float _step = max(1.0f, 1.5f * scale);
    float2 param_1 = pixel - float2(_step, 0.0f);
    float3 left = positionAt(param_1).xyz;
    float2 param_2 = pixel + float2(_step, 0.0f);
    float3 right = positionAt(param_2).xyz;
    float2 param_3 = pixel - float2(0.0f, _step);
    float3 up = positionAt(param_3).xyz;
    float2 param_4 = pixel + float2(0.0f, _step);
    float3 down = positionAt(param_4).xyz;
    float3 _2221;
    if (abs(right.z - p.z) < abs(p.z - left.z))
    {
        _2221 = right - p;
    }
    else
    {
        _2221 = p - left;
    }
    float3 dx = _2221;
    float3 _2246;
    if (abs(down.z - p.z) < abs(p.z - up.z))
    {
        _2246 = down - p;
    }
    else
    {
        _2246 = p - up;
    }
    float3 dy = _2246;
    float3 n = normalize(cross(dy, dx));
    if (n.z < 0.0f)
    {
        n = -n;
    }
    float3 normal = normalize(lerp(n, float3(0.0f, 0.0f, 1.0f), 0.25f.xxx));
    float radius = ambient_params.x;
    float thickness = ambient_params.w;
    float2 param_5 = pixel;
    float jitter = _noise(param_5);
    float visibility = 1.0f;
    if (ambient_params.y > 0.001000000047497451305389404296875f)
    {
        visibility = 0.0f;
        float2 _2359;
        float horizons[2];
        float _2475;
        for (int slice = 0; slice < 4; slice++)
        {
            float phi = ((float(slice) + jitter) * 3.1415927410125732421875f) / 4.0f;
            float2 direction = float2(cos(phi), sin(phi));
            float3 axis = float3(direction.y, -direction.x, 0.0f);
            float3 projected = normal - (axis * dot(normal, axis));
            float projectedLength = max(length(projected), 9.9999997473787516355514526367188e-05f);
            float cosN = clamp(projected.z / projectedLength, -1.0f, 1.0f);
            float n_1 = sign(dot(projected.xy, direction)) * acos(cosN);
            for (int side = 0; side < 2; side++)
            {
                if (side == 0)
                {
                    _2359 = direction;
                }
                else
                {
                    _2359 = -direction;
                }
                float2 sideDirection = _2359;
                float best = -1.0f;
                for (int i = 0; i < 6; i++)
                {
                    float t = (float(i) + frac(jitter + (float(i) * 0.61803400516510009765625f))) / 6.0f;
                    float2 offset = ((((sideDirection * radius) * t) * t) * scale) + (sideDirection * 1.5f);
                    float2 samplePixel = pixel + offset;
                    bool _2407 = any(bool2(samplePixel.x < 0.0f.xx.x, samplePixel.y < 0.0f.xx.y));
                    bool _2416;
                    if (!_2407)
                    {
                        _2416 = samplePixel.x >= ambient_extent.x;
                    }
                    else
                    {
                        _2416 = _2407;
                    }
                    bool _2425;
                    if (!_2416)
                    {
                        _2425 = samplePixel.y >= ambient_extent.y;
                    }
                    else
                    {
                        _2425 = _2416;
                    }
                    if (_2425)
                    {
                        break;
                    }
                    float2 param_6 = samplePixel;
                    float3 delta = positionAt(param_6).xyz - p;
                    float _distance = length(delta);
                    float falloff = clamp((radius - _distance) / (0.4000000059604644775390625f * radius), 0.0f, 1.0f) * clamp((thickness - delta.z) / (0.5f * thickness), 0.0f, 1.0f);
                    float cosine = lerp(-1.0f, delta.z / max(_distance, 9.9999997473787516355514526367188e-05f), falloff);
                    best = max(best, cosine);
                }
                if (side == 0)
                {
                    _2475 = acos(clamp(best, -1.0f, 1.0f));
                }
                else
                {
                    _2475 = -acos(clamp(best, -1.0f, 1.0f));
                }
                horizons[side] = _2475;
            }
            float h1 = n_1 + min(horizons[0] - n_1, 1.57079637050628662109375f);
            float h0 = n_1 + max(horizons[1] - n_1, -1.57079637050628662109375f);
            float sinN = sin(n_1);
            visibility += ((projectedLength * 0.25f) * ((((-cos((2.0f * h0) - n_1)) + cosN) + ((2.0f * h0) * sinN)) + (((-cos((2.0f * h1) - n_1)) + cosN) + ((2.0f * h1) * sinN))));
        }
        visibility = clamp(visibility / 4.0f, 0.0f, 1.0f);
    }
    float multiBounceVis = visibility * (1.0f + (0.14000000059604644775390625f * (1.0f - visibility)));
    float occlusion = clamp(lerp(1.0f, clamp(multiBounceVis, 0.0f, 1.0f), ambient_params.y), 0.180000007152557373046875f, 1.0f);
    float _2567;
    if (centre.w < 0.25f)
    {
        _2567 = (0.89999997615814208984375f - depthMap.Load(int3(int2(pixel), 0)).x) / 0.0030000000260770320892333984375f;
    }
    else
    {
        _2567 = ((0.89999997615814208984375f - groundMap.Load(int3(int2(pixel), 0)).x) / 0.0030000000260770320892333984375f) + ambient_view.w;
    }
    float key = _2567;
    float2 param_7 = p.xy;
    float param_8 = key;
    bool param_9 = centre.w > 0.25f;
    float2 param_10 = ambient_frame.xy;
    float3 here = lotPoint(param_7, param_8, param_9, param_10);
    float2 param_11 = pixel;
    float param_12 = p.z;
    float riseHere = riseAt(param_11, param_12);
    float onGround = 1.0f - smoothstep(2.0f, 6.0f, riseHere);
    uint _2621_dummy_parameter;
    float4 _2624;
    if (int2(spvTextureSize(liftMap, uint(0), _2621_dummy_parameter)).x > 1)
    {
        _2624 = liftMap.Load(int3(int2(pixel), 0));
    }
    else
    {
        _2624 = 0.0f.xxxx;
    }
    float4 lift = _2624;
    bool onSnow = lift.z > 0.5f;
    if (onSnow)
    {
        here += float3(0.5f * lift.x, 0.5f * lift.x, lift.y);
        onGround = 1.0f;
        occlusion = 1.0f;
    }
    float contact = 1.0f;
    if (ambient_sunLot.w > 0.0f)
    {
        bool _2662 = (centre.w > 0.25f) || onSnow;
        bool _2668;
        if (_2662)
        {
            _2668 = ambient_contact.y > 0.0f;
        }
        else
        {
            _2668 = _2662;
        }
        if (_2668)
        {
            float detail = ambient_frame.z;
            uint _2678_dummy_parameter;
            int2 footSize = int2(spvTextureSize(footMap, uint(0), _2678_dummy_parameter));
            float2 at = (here.xy + ambient_frame.w.xx) * detail;
            int2 centreTexel = int2(floor(at));
            float best_1 = 0.0f;
            float sum = 0.0f;
            for (int y = -4; y <= 4; y++)
            {
                for (int x = -4; x <= 4; x++)
                {
                    int2 texel = centreTexel + int2(x, y);
                    bool _2720 = any(bool2(texel.x < int2(0, 0).x, texel.y < int2(0, 0).y));
                    bool _2728;
                    if (!_2720)
                    {
                        _2728 = any(bool2(texel.x >= footSize.x, texel.y >= footSize.y));
                    }
                    else
                    {
                        _2728 = _2720;
                    }
                    if (_2728)
                    {
                        continue;
                    }
                    float stored = footMap.Load(int3(texel, 0)).x;
                    if (stored >= 1.0f)
                    {
                        continue;
                    }
                    float footZ = (0.5f - stored) * 64.0f;
                    if (abs(footZ - here.z) > 0.25f)
                    {
                        continue;
                    }
                    float near = max(0.0f, 1.0f - (length(((float2(texel) + 0.5f.xx) - at) / detail.xx) / 0.125f));
                    best_1 = max(best_1, near * near);
                    sum += (near * near);
                }
            }
            float feet = clamp((0.699999988079071044921875f * best_1) + (0.039999999105930328369140625f * sum), 0.0f, 1.0f);
            uint _2797_dummy_parameter;
            float2 gridSize = float2(int2(spvTextureSize(heightMap, uint(0), _2797_dummy_parameter)));
            float over = ((0.5f - heightMap.Sample(_heightMap_sampler, ((here.xy + ambient_frame.w.xx) * ambient_frame.z) / gridSize).x) * 64.0f) - here.z;
            float under = smoothstep(0.20000000298023223876953125f, 0.4000000059604644775390625f, over) * (1.0f - smoothstep(2.0f, 2.599999904632568359375f, over));
            contact = 1.0f - (ambient_contact.y * clamp(feet + (0.300000011920928955078125f * under), 0.0f, 1.0f));
        }
    }
    else
    {
        bool _2837 = onGround > 0.0f;
        bool _2843;
        if (_2837)
        {
            _2843 = ambient_contact.y > 0.0f;
        }
        else
        {
            _2843 = _2837;
        }
        if (_2843)
        {
            float contactRadius = ambient_contact.x;
            float2 footHere = p.xy + float2(0.0f, riseHere);
            float covered = 0.0f;
            for (int d = 0; d < 8; d++)
            {
                float phi_1 = (((float(d) + jitter) * 2.0f) * 3.1415927410125732421875f) / 8.0f;
                float2 direction_1 = float2(cos(phi_1), sin(phi_1));
                float best_2 = 0.0f;
                for (int i_1 = 1; i_1 <= 3; i_1++)
                {
                    float t_1 = (float(i_1) - (0.5f * frac((jitter * 7.0f) + (float(i_1) * 0.61803400516510009765625f)))) / 3.0f;
                    float2 samplePixel_1 = pixel + (((direction_1 * contactRadius) * t_1) * scale);
                    bool _2912 = any(bool2(samplePixel_1.x < 0.0f.xx.x, samplePixel_1.y < 0.0f.xx.y));
                    bool _2921;
                    if (!_2912)
                    {
                        _2921 = samplePixel_1.x >= ambient_extent.x;
                    }
                    else
                    {
                        _2921 = _2912;
                    }
                    bool _2930;
                    if (!_2921)
                    {
                        _2930 = samplePixel_1.y >= ambient_extent.y;
                    }
                    else
                    {
                        _2930 = _2921;
                    }
                    if (_2930)
                    {
                        break;
                    }
                    float2 param_13 = samplePixel_1;
                    float4 q = positionAt(param_13);
                    if (q.w > 0.75f)
                    {
                        continue;
                    }
                    float2 param_14 = samplePixel_1;
                    float param_15 = q.z;
                    float rise = riseAt(param_14, param_15);
                    float height = (rise - riseHere) / 0.866025388240814208984375f;
                    float along = length(((q.xy + float2(0.0f, rise)) - footHere) * float2(1.0f, 2.0f));
                    if ((height < 1.0f) || (along > contactRadius))
                    {
                        continue;
                    }
                    float near_1 = 1.0f - (along / contactRadius);
                    best_2 = max(best_2, (height / ((height + (0.5f * along)) + 2.0f)) * near_1);
                }
                covered += best_2;
            }
            contact = 1.0f - ((ambient_contact.y * onGround) * clamp((covered / 8.0f) * 4.0f, 0.0f, 1.0f));
        }
    }
    float lit = 1.0f;
    float litColumn = 1.0f;
    float litShell = 1.0f;
    float inside = 0.0f;
    if (ambient_sunLot.w > 0.0f)
    {
        float3 param_16 = here + float3(normalize(ambient_sunLot.xy + 9.9999997473787516355514526367188e-06f.xx) * 0.300000011920928955078125f, 0.0f);
        inside = indoors(param_16);
    }
    bool _3035 = ambient_sunLot.w > 0.0f;
    bool _3041;
    if (_3035)
    {
        _3041 = ambient_params.z > 0.001000000047497451305389404296875f;
    }
    else
    {
        _3041 = _3035;
    }
    if (_3041 && (inside < 0.9900000095367431640625f))
    {
        float3 param_17 = here;
        float param_18 = jitter;
        float param_19 = lerp(1.0f, 0.20000000298023223876953125f, onGround);
        float3 march = sunMarch(param_17, param_18, param_19);
        lit = march.x;
        litColumn = march.y;
        litShell = march.z;
        uint _3066_dummy_parameter;
        float2 gridSize_1 = float2(int2(spvTextureSize(heightMap, uint(0), _3066_dummy_parameter)));
        float shade = 0.0f;
        float centerOver = ((0.5f - heightMap.Sample(_heightMap_sampler, ((here.xy + ambient_frame.w.xx) * ambient_frame.z) / gridSize_1).x) * 64.0f) - here.z;
        shade += (smoothstep(1.7999999523162841796875f, 3.2000000476837158203125f, centerOver) * 0.2800000011920928955078125f);
        for (int i_2 = 0; i_2 < 8; i_2++)
        {
            float2 at_1 = here.xy + (_3118[i_2] * 0.550000011920928955078125f);
            float over_1 = ((0.5f - heightMap.Sample(_heightMap_sampler, ((at_1 + ambient_frame.w.xx) * ambient_frame.z) / gridSize_1).x) * 64.0f) - here.z;
            shade += (smoothstep(1.7999999523162841796875f, 3.2000000476837158203125f, over_1) * 0.0900000035762786865234375f);
        }
        lit = min(lit, 1.0f - ((0.3499999940395355224609375f * onGround) * shade));
    }
    float window = 0.0f;
    bool _3162 = ambient_sunLot.w > 0.0f;
    bool _3168;
    if (_3162)
    {
        _3168 = ambient_contact.z > 0.0f;
    }
    else
    {
        _3168 = _3162;
    }
    if (_3168 && (inside > 0.00999999977648258209228515625f))
    {
        float3 param_20 = here;
        float4 opening = throughWindow(param_20);
        if (opening.x > 0.0f)
        {
            float3 param_21 = opening.yzw;
            float param_22 = jitter;
            float param_23 = 0.20000000298023223876953125f;
            window = ((inside * onGround) * opening.x) * sunMarch(param_21, param_22, param_23).y;
        }
    }
    float _3202;
    if (ambient_sunLot.w > 0.0f)
    {
        float3 param_24 = here;
        _3202 = cloudLight(param_24);
    }
    else
    {
        _3202 = 1.0f;
    }
    float sky = _3202;
    window *= sky;
    lit = lerp(lit * sky, 1.0f, inside);
    float shadow = lerp(1.0f, lit, ambient_params.z);
    float3 lampLight = 0.0f.xxx;
    int lampCount = int(ambient_lamp.x);
    float reach = ambient_lamp.y;
    bool sides = ambient_sunLot.w > 0.0f;
    float _3237;
    if (sides)
    {
        float3 param_25 = here;
        _3237 = indoors(param_25);
    }
    else
    {
        _3237 = 0.0f;
    }
    float hereInside = _3237;
    for (int index = 0; index < 16; index++)
    {
        if (index >= lampCount)
        {
            break;
        }
        float4 lamp = ambient_lamps[index];
        float3 toLamp = float3(lamp.xy, lamp.z * 27.7128124237060546875f) - p;
        float _distance_1 = length(toLamp);
        if (_distance_1 >= reach)
        {
            continue;
        }
        float sameSide = 1.0f;
        if (sides)
        {
            float2 param_26 = lamp.xy;
            float param_27 = lamp.z;
            bool param_28 = false;
            float2 param_29 = ambient_frame.xy;
            float3 lampLot = lotPoint(param_26, param_27, param_28, param_29);
            float3 param_30 = lampLot - float3(0.0f, 0.0f, 0.5f);
            sameSide = 1.0f - abs(indoors(param_30) - hereInside);
            if (sameSide <= 0.0f)
            {
                continue;
            }
        }
        float3 direction_2 = toLamp / max(_distance_1, 9.9999997473787516355514526367188e-05f).xxx;
        float falloff_1 = 1.0f - (_distance_1 / reach);
        falloff_1 *= falloff_1;
        float facing = clamp((dot(normal, direction_2) * 0.60000002384185791015625f) + 0.4000000059604644775390625f, 0.0f, 1.0f);
        float open = 1.0f;
        for (int i_3 = 1; i_3 < 8; i_3++)
        {
            float t_2 = ((float(i_3) - jitter) / 8.0f) * _distance_1;
            float3 q_1 = p + (direction_2 * t_2);
            float2 samplePixel_2 = ambient_view.xy + (q_1.xy * scale);
            bool _3371 = any(bool2(samplePixel_2.x < 0.0f.xx.x, samplePixel_2.y < 0.0f.xx.y));
            bool _3380;
            if (!_3371)
            {
                _3380 = samplePixel_2.x >= ambient_extent.x;
            }
            else
            {
                _3380 = _3371;
            }
            bool _3389;
            if (!_3380)
            {
                _3389 = samplePixel_2.y >= ambient_extent.y;
            }
            else
            {
                _3389 = _3380;
            }
            if (_3389)
            {
                break;
            }
            float2 param_31 = samplePixel_2;
            float h = positionAt(param_31).z - q_1.z;
            if ((h > 2.0f) && (h < thickness))
            {
                open = min(open, 1.0f - clamp((h - 2.0f) / ((0.100000001490116119384765625f * t_2) + 3.0f), 0.0f, 1.0f));
            }
        }
        lampLight += (float3(1.0f, 0.819999992847442626953125f, 0.579999983310699462890625f) * ((((lamp.w * falloff_1) * facing) * open) * sameSide));
    }
    float nightFactor = clamp(ambient_weather.w, 0.0f, 1.0f);
    float lowSun = 1.0f - smoothstep(0.119999997317790985107421875f, 0.519999980926513671875f, ambient_sunLot.z);
    float3 dayWindowColor = lerp(float3(1.0f, 0.939999997615814208984375f, 0.819999992847442626953125f), float3(1.13999998569488525390625f, 0.680000007152557373046875f, 0.36000001430511474609375f), lowSun.xxx);
    float3 moonWindowColor = float3(0.680000007152557373046875f, 0.819999992847442626953125f, 1.14999997615814208984375f);
    float3 windowColor = lerp(dayWindowColor, moonWindowColor, smoothstep(0.550000011920928955078125f, 0.89999997615814208984375f, nightFactor).xxx);
    float3 lamps = lampLight * ambient_lamp.z;
    float outdoor = 1.0f - inside;
    float keyWrap = dot(normal, normalize(ambient_light.xyz + float3(0.0f, 0.0f, 9.9999997473787516355514526367188e-05f)));
    float shadowActive = clamp(ambient_params.z * 1.7999999523162841796875f, 0.0f, 1.0f);
    float dirShade = 1.0f + ((((((keyWrap * 0.180000007152557373046875f) - 0.039999999105930328369140625f) * outdoor) * lit) * shadowActive) * (1.0f - (0.449999988079071044921875f * onGround)));
    float sunBloom = 1.0f + ((((0.10999999940395355224609375f * (1.0f - nightFactor)) * lit) * outdoor) * shadowActive);
    float3 light = (((((shadow * dirShade) * sunBloom) * ambient_lamp.w).xxx + lamps) + (windowColor * (ambient_contact.z * window))) * (occlusion * contact);
    float daylight = clamp((1.0f - nightFactor) * clamp(ambient_params.z * 2.0f, 0.0f, 1.0f), 0.0f, 1.0f);
    float3 warmSun = lerp(float3(1.09000003337860107421875f, 1.0099999904632568359375f, 0.910000026226043701171875f), float3(1.2599999904632568359375f, 0.86000001430511474609375f, 0.579999983310699462890625f), lowSun.xxx);
    float3 coolShade = lerp(float3(0.839999973773956298828125f, 0.939999997615814208984375f, 1.17999994754791259765625f), float3(0.7200000286102294921875f, 0.839999973773956298828125f, 1.2400000095367431640625f), lowSun.xxx);
    float3 sunTint = lerp(coolShade, warmSun, lit.xxx);
    sunTint /= dot(sunTint, float3(0.2125999927520751953125f, 0.715200006961822509765625f, 0.072200000286102294921875f)).xxx;
    float3 tint = lerp(1.0f.xxx, sunTint, ((clamp(ambient_tone.x, 0.0f, 1.35000002384185791015625f) * daylight) * outdoor).xxx);
    float twilight = smoothstep(0.1500000059604644775390625f, 0.519999980926513671875f, nightFactor) * (1.0f - smoothstep(0.62000000476837158203125f, 0.949999988079071044921875f, nightFactor));
    float3 blueHour = float3(0.8899211883544921875f, 1.0011613368988037109375f, 1.3126337528228759765625f);
    float lampShare = dot(lamps, float3(0.2125999927520751953125f, 0.715200006961822509765625f, 0.072200000286102294921875f)) / ((ambient_lamp.w + dot(lamps, float3(0.2125999927520751953125f, 0.715200006961822509765625f, 0.072200000286102294921875f))) + 9.9999997473787516355514526367188e-05f);
    tint = lerp(tint, blueHour, ((((twilight * outdoor) * ambient_tone.x) * 0.64999997615814208984375f) * (1.0f - lampShare)).xxx);
    float3 moonLit = float3(0.887847900390625f, 1.004113674163818359375f, 1.2894933223724365234375f);
    float3 moonShade = float3(0.857108414173126220703125f, 0.996098995208740234375f, 1.4594008922576904296875f);
    float3 moon = lerp(moonShade, moonLit, lit.xxx);
    tint = lerp(tint, moon, (((nightFactor * outdoor) * 0.680000007152557373046875f) * (1.0f - lampShare)).xxx);
    light *= tint;
    bool _3670 = nightFactor > 0.0500000007450580596923828125f;
    bool _3676;
    if (_3670)
    {
        _3676 = ambient_sunLot.w > 0.0f;
    }
    else
    {
        _3676 = _3670;
    }
    bool _3682;
    if (_3676)
    {
        _3682 = centre.w > 0.25f;
    }
    else
    {
        _3682 = _3676;
    }
    if (_3682)
    {
        float3 param_32 = here;
        float param_33 = onGround;
        light += (float3(1.0f, 0.819999992847442626953125f, 0.579999983310699462890625f) * (((occlusion * 1.0499999523162841796875f) * nightFactor) * windowSpill(param_32, param_33)));
    }
    bool _3701 = ambient_weather.x > 0.60000002384185791015625f;
    bool _3708;
    if (!_3701)
    {
        _3708 = ambient_seasonExtra2.z > 0.5f;
    }
    else
    {
        _3708 = _3701;
    }
    bool _3715;
    if (_3708)
    {
        _3715 = ambient_seasonExtra.z < 0.5f;
    }
    else
    {
        _3715 = _3708;
    }
    if (_3715)
    {
        float lSlot = floor(ambient_weather.z / 7.0f);
        float _3727;
        if (ambient_seasonExtra2.z > 0.5f)
        {
            _3727 = 0.550000011920928955078125f;
        }
        else
        {
            _3727 = (ambient_weather.x - 0.60000002384185791015625f) * 1.60000002384185791015625f;
        }
        float lChance = _3727;
        float2 param_34 = float2(lSlot, 3.0f);
        float lStrikes = step(hash12(param_34), lChance);
        float2 param_35 = float2(lSlot, 7.0f);
        float lDt = ((ambient_weather.z - (lSlot * 7.0f)) - 1.0f) - (4.0f * hash12(param_35));
        float _3760;
        if (lDt < 0.0f)
        {
            _3760 = 0.0f;
        }
        else
        {
            float _3770;
            if (lDt < 0.180000007152557373046875f)
            {
                _3770 = 0.0f;
            }
            else
            {
                _3770 = 0.699999988079071044921875f * exp((-(lDt - 0.180000007152557373046875f)) * 9.0f);
            }
            _3760 = exp((-lDt) * 10.0f) + _3770;
        }
        float lFlash = _3760;
        lFlash = clamp(lFlash * lStrikes, 0.0f, 1.0f);
        light += ((float3(0.85000002384185791015625f, 0.949999988079071044921875f, 1.25f) * occlusion) * ((lFlash * 0.75f) * outdoor));
    }
    float _3803;
    if (ambient_season.x > 1.5f)
    {
        _3803 = 0.0f;
    }
    else
    {
        float3 param_36 = here;
        _3803 = (ambient_weather.y * outdoor) * architecturalExposure(param_36);
    }
    float wet = _3803;
    float standing = 0.0f;
    if (wet > 0.0f)
    {
        light *= lerp(1.0f.xxx, float3(0.800000011920928955078125f, 0.829999983310699462890625f, 0.87999999523162841796875f), (wet * lerp(0.5f, 1.0f, onGround)).xxx);
        if ((centre.w > 0.25f) && (onGround > 0.5f))
        {
            float2 param_37 = here.xy;
            float2 source = lotSource(param_37);
            float2 a = mul(source, float2x2(float2(0.800000011920928955078125f, 0.60000002384185791015625f), float2(-0.60000002384185791015625f, 0.800000011920928955078125f)));
            float2 b = mul(source, float2x2(float2(0.2800000011920928955078125f, -0.959999978542327880859375f), float2(0.959999978542327880859375f, 0.2800000011920928955078125f)));
            float2 param_38 = a / 1.2000000476837158203125f.xx;
            float2 param_39 = (b / 0.60000002384185791015625f.xx) + 13.0f.xx;
            float field = (0.699999988079071044921875f * smoothNoise(param_38)) + (0.300000011920928955078125f * smoothNoise(param_39));
            float level = 0.7799999713897705078125f - (0.100000001490116119384765625f * wet);
            float puddle = smoothstep(level, level + 0.039999999105930328369140625f, field) * smoothstep(0.25f, 0.5f, wet);
            light *= lerp(1.0f.xxx, float3(0.959999978542327880859375f, 1.019999980926513671875f, 1.14999997615814208984375f), puddle.xxx);
            standing = puddle;
            if ((ambient_weather.x > 0.0f) && (puddle > 0.00999999977648258209228515625f))
            {
                float2 g = source * 3.0f;
                float2 id = floor(g);
                float2 param_40 = id;
                float2 param_41 = id + 7.099999904632568359375f.xx;
                float2 drop = (id + 0.25f.xx) + (float2(hash12(param_40), hash12(param_41)) * 0.5f);
                float2 param_42 = id + 3.7000000476837158203125f.xx;
                float age = frac((ambient_weather.z * (0.800000011920928955078125f + (0.60000002384185791015625f * ambient_weather.x))) + hash12(param_42));
                float r1 = (1.0f - smoothstep(0.0f, 0.070000000298023223876953125f, abs(length(g - drop) - (age * 0.449999988079071044921875f)))) * (1.0f - age);
                float age2 = frac(age + 0.5f);
                float r2 = ((1.0f - smoothstep(0.0f, 0.0599999986588954925537109375f, abs(length(g - drop) - (age2 * 0.4199999868869781494140625f)))) * (1.0f - age2)) * 0.550000011920928955078125f;
                light *= (1.0f + (((0.75f * (r1 + r2)) * puddle) * ambient_weather.x));
            }
            else
            {
                light *= (1.0f + ((0.0599999986588954925537109375f * wet) * (1.0f - smoothstep(0.20000000298023223876953125f, 0.60000002384185791015625f, ambient_weather.w))));
            }
        }
    }
    float mode = ambient_extent.z;
    if (mode > 12.5f)
    {
        float3 param_43 = here;
        float param_44 = onGround;
        float3 param_45 = here;
        color = float4(0.0f, windowSpill(param_43, param_44), lampNear(param_45), 1.0f);
        return;
    }
    if (mode > 11.5f)
    {
        color = float4(sky.xxx, 1.0f);
        return;
    }
    if (mode > 10.5f)
    {
        int2 param_46 = int2(floor(here.xy + ambient_frame.w.xx));
        int param_47 = 0;
        int cellMask = maskAt(param_46, param_47);
        color = float4(window, float(cellMask & 1) * 0.60000002384185791015625f, inside * 0.5f, 1.0f);
        return;
    }
    if (mode > 9.5f)
    {
        color = float4(litColumn, lit, litShell, 1.0f);
        return;
    }
    if (mode > 8.5f)
    {
        uint _4078_dummy_parameter;
        float2 gridSize_2 = float2(int2(spvTextureSize(heightMap, uint(0), _4078_dummy_parameter)));
        float top = (0.5f - heightMap.Sample(_heightMap_sampler, ((here.xy + ambient_frame.w.xx) * ambient_frame.z) / gridSize_2).x) * 64.0f;
        color = float4(clamp(top / 6.0f, 0.0f, 1.0f), frac(top), float(top < (-16.0f)), 1.0f);
        return;
    }
    if (mode > 7.5f)
    {
        float depth = depthMap.Load(int3(int2(pixel), 0)).x;
        float ground = groundMap.Load(int3(int2(pixel), 0)).x;
        float _4130;
        if (depth < 1.0f)
        {
            _4130 = (0.89999997615814208984375f - depth) / 0.0030000000260770320892333984375f;
        }
        else
        {
            _4130 = ((0.89999997615814208984375f - ground) / 0.0030000000260770320892333984375f) + ambient_view.w;
        }
        float key_1 = _4130;
        float2 param_48 = p.xy;
        float param_49 = key_1;
        bool param_50 = depth >= 1.0f;
        float2 param_51 = ambient_frame.xy;
        float3 lot = lotPoint(param_48, param_49, param_50, param_51);
        float2 grid = step(0.89999997615814208984375f.xx, frac(lot.xy));
        color = float4(grid, step(0.5f, frac(lot.z)), 1.0f);
        float4 _4172 = color;
        float3 param_52 = lot;
        float3 _4180 = lerp(_4172.xyz, float3(1.0f, 0.800000011920928955078125f, 0.20000000298023223876953125f), (0.60000002384185791015625f * indoors(param_52)).xxx);
        color.x = _4180.x;
        color.y = _4180.y;
        color.z = _4180.z;
        return;
    }
    if (mode > 6.5f)
    {
        color = float4(contact.xxx, 1.0f);
        return;
    }
    if (mode > 5.5f)
    {
        color = float4(clamp(lampLight, 0.0f.xxx, 1.0f.xxx), 1.0f);
        return;
    }
    if (mode > 4.5f)
    {
        float ground_1 = groundMap.Load(int3(int2(pixel), 0)).x;
        float _4234;
        if ((centre.w < 0.25f) && (ground_1 < 1.0f))
        {
            _4234 = ((0.89999997615814208984375f - depthMap.Load(int3(int2(pixel), 0)).x) - (0.89999997615814208984375f - ground_1)) / 0.0030000000260770320892333984375f;
        }
        else
        {
            _4234 = 0.0f;
        }
        float difference = _4234;
        color = float4(clamp(0.5f + (difference / 64.0f), 0.0f, 1.0f), centre.w, float(ground_1 < 1.0f), 1.0f);
        return;
    }
    if (mode > 3.5f)
    {
        color = float4(frac(p.z / 32.0f), frac(p.z / 256.0f), centre.w, 1.0f);
        return;
    }
    if (mode > 2.5f)
    {
        color = float4((normal * 0.5f) + 0.5f.xxx, 1.0f);
        return;
    }
    if (mode > 1.5f)
    {
        color = float4(shadow.xxx, 1.0f);
        return;
    }
    if (mode > 0.5f)
    {
        color = float4((occlusion * contact).xxx, 1.0f);
        return;
    }
    color = float4(clamp(light * 0.5f, 0.0f.xxx, 1.0f.xxx), 1.0f - standing);
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
