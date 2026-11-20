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

Texture2D<float4> overheadMap : register(t3, space2);
SamplerState _overheadMap_sampler : register(s3, space2);
Texture2D<float4> depthMap : register(t1, space2);
SamplerState _depthMap_sampler : register(s1, space2);
Texture2D<float4> groundMap : register(t2, space2);
SamplerState _groundMap_sampler : register(s2, space2);
Texture2D<float4> ambientMap : register(t0, space2);
SamplerState _ambientMap_sampler : register(s0, space2);

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

float3 lotPoint(float2 scene, float key, bool terrain, float2 frame)
{
    float2 local = scene - frame;
    float _38;
    if (terrain)
    {
        _38 = ((16.0f * key) - local.y) / 40.0f;
    }
    else
    {
        _38 = ((16.0f * key) - local.y) / 52.799999237060546875f;
    }
    float h = _38;
    float _62;
    if (terrain)
    {
        _62 = key;
    }
    else
    {
        _62 = key - (0.800000011920928955078125f * h);
    }
    float s = _62;
    float u = local.x / 32.0f;
    return float3(0.5f * (s + u), 0.5f * (s - u), h);
}

float architecturalExposure(float3 _point)
{
    float2 size = ambient_drift.zw;
    int orientation = int(ambient_clouds.z + 0.5f);
    float2 _146;
    if (orientation == 1)
    {
        _146 = float2(_point.y, size.y - _point.x);
    }
    else
    {
        float2 _161;
        if (orientation == 2)
        {
            _161 = size - _point.xy;
        }
        else
        {
            float2 _172;
            if (orientation == 3)
            {
                _172 = float2(size.x - _point.y, _point.x);
            }
            else
            {
                _172 = _point.xy;
            }
            _161 = _172;
        }
        _146 = _161;
    }
    float2 source = _146;
    bool _194 = any(bool2(source.x < 0.0f.xx.x, source.y < 0.0f.xx.y));
    bool _202;
    if (!_194)
    {
        _202 = any(bool2(source.x >= size.x, source.y >= size.y));
    }
    else
    {
        _202 = _194;
    }
    if (_202)
    {
        return 1.0f;
    }
    uint _216_dummy_parameter;
    float2 texelsPerTile = float2(int2(spvTextureSize(overheadMap, uint(0), _216_dummy_parameter))) / max(size, 1.0f.xx);
    int2 at = int2(floor(source * texelsPerTile));
    uint _232_dummy_parameter;
    int2 _232 = int2(spvTextureSize(overheadMap, uint(0), _232_dummy_parameter));
    if (any(bool2(at.x >= _232.x, at.y >= _232.y)))
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

float exposedAt(float2 pixel)
{
    float depth = depthMap.Load(int3(int2(pixel), 0)).x;
    bool terrain = depth >= 1.0f;
    if (terrain)
    {
        depth = groundMap.Load(int3(int2(pixel), 0)).x;
    }
    if (depth >= 1.0f)
    {
        return 1.0f;
    }
    float _341;
    if (terrain)
    {
        _341 = ambient_view.w;
    }
    else
    {
        _341 = 0.0f;
    }
    float2 param = (pixel - ambient_view.xy) / ambient_view.z.xx;
    float param_1 = ((0.89999997615814208984375f - depth) / 0.0030000000260770320892333984375f) + _341;
    bool param_2 = terrain;
    float2 param_3 = ambient_frame.xy;
    float3 _point = lotPoint(param, param_1, param_2, param_3);
    float3 param_4 = _point;
    return architecturalExposure(param_4);
}

float hash12(float2 q)
{
    float3 r = frac(q.xyx * 0.103100001811981201171875f);
    r += dot(r, r.yzx + 33.3300018310546875f.xxx).xxx;
    return frac((r.x + r.y) * r.z);
}

void frag_main()
{
    float2 pixel = floor(gl_FragCoord.xy) + 0.5f.xx;
    bool _376 = pixel.x >= ambient_extent.x;
    bool _385;
    if (!_376)
    {
        _385 = pixel.y >= ambient_extent.y;
    }
    else
    {
        _385 = _376;
    }
    if (_385)
    {
        color = 0.0f.xxxx;
        return;
    }
    float scale = max(ambient_view.z, 0.25f);
    float cover = 0.0f;
    float rain = ambient_weather.x;
    float time = ambient_weather.z;
    float water = 1.0f - ambientMap.Load(int3(int2(pixel), 0)).w;
    float3 sky = lerp(float3(0.550000011920928955078125f, 0.660000026226043701171875f, 0.819999992847442626953125f), float3(0.62000000476837158203125f, 0.64999997615814208984375f, 0.699999988079071044921875f), clamp(ambient_clouds.x, 0.0f, 1.0f).xxx) * lerp(1.0f, 0.25f, ambient_weather.w);
    float4 reflection = (float4(sky, 1.0f) * 0.119999997317790985107421875f) * water;
    float2 param = pixel;
    float exposed = exposedAt(param);
    if (exposed <= 0.00999999977648258209228515625f)
    {
        color = reflection;
        return;
    }
    bool _458 = ambient_seasonExtra.z > 0.5f;
    bool _474;
    if (!_458)
    {
        bool _465 = abs(ambient_seasonExtra.z) < 0.25f;
        bool _473;
        if (_465)
        {
            _473 = ambient_season.x > 1.5f;
        }
        else
        {
            _473 = _465;
        }
        _474 = _473;
    }
    else
    {
        _474 = _458;
    }
    bool snowing = _474;
    float hail = clamp(ambient_seasonExtra2.x, 0.0f, 1.0f);
    float thunderPulse = clamp(ambient_seasonExtra2.z, 0.0f, 1.0f);
    float fireflies = clamp(ambient_seasonExtra.w, 0.0f, 1.0f);
    float2 q = (pixel - ambient_view.xy) / scale.xx;
    if ((rain > 0.0f) && snowing)
    {
        for (int layer = 0; layer < 3; layer++)
        {
            float cell = 26.0f + (14.0f * float(layer));
            float speed = 40.0f + (25.0f * float(layer));
            float radius = 1.2000000476837158203125f + (0.800000011920928955078125f * float(layer));
            float2 moved = (q - float2(((ambient_tone.y * speed) * 1.5f) * time, speed * time)) + (float(layer) * 113.0f).xx;
            float2 at = floor(moved / cell.xx);
            float2 param_1 = at + (float(layer) * 31.0f).xx;
            float r = hash12(param_1);
            if (r > (rain * 0.89999997615814208984375f))
            {
                continue;
            }
            float2 param_2 = at + 5.0f.xx;
            float2 param_3 = at + 9.0f.xx;
            float2 centre = ((at + 0.20000000298023223876953125f.xx) + (float2(hash12(param_2), hash12(param_3)) * 0.60000002384185791015625f)) * cell;
            centre.x += (4.0f * sin((time * (1.2000000476837158203125f + r)) + (r * 40.0f)));
            float d = length(moved - centre);
            cover += ((1.0f - smoothstep(radius * 0.4000000059604644775390625f, radius + (0.5f / scale), d)) * (0.5f + (0.20000000298023223876953125f * float(layer))));
        }
        cover = clamp(cover * 0.800000011920928955078125f, 0.0f, 0.75f) * exposed;
        color = float4(float3(0.949999988079071044921875f, 0.959999978542327880859375f, 1.0f) * cover, cover);
        return;
    }
    if (rain > 0.0f)
    {
        float u = q.x - (ambient_tone.y * q.y);
        for (int layer_1 = 0; layer_1 < 3; layer_1++)
        {
            float spacing = 9.0f + (5.0f * float(layer_1));
            float invSpacing = 1.0f / spacing;
            float uCell = u * invSpacing;
            float column = floor(uCell);
            float2 param_4 = float2(column, float(layer_1) * 17.0f);
            float r_1 = hash12(param_4);
            float across = abs((frac(uCell) * spacing) - ((0.20000000298023223876953125f + (0.60000002384185791015625f * r_1)) * spacing));
            float _line = 1.0f - smoothstep(0.300000011920928955078125f, 0.89999997615814208984375f + (0.300000011920928955078125f * float(layer_1)), across * scale);
            if (_line <= 0.001000000047497451305389404296875f)
            {
                continue;
            }
            float length_ = 14.0f + (8.0f * float(layer_1));
            float speed_1 = 520.0f + (260.0f * float(layer_1));
            float period = length_ * 5.0f;
            float v = ((q.y - ((time * speed_1) * (0.85000002384185791015625f + (0.300000011920928955078125f * r_1)))) / period) + (r_1 * 7.309999942779541015625f);
            float drop = floor(v);
            float along = frac(v);
            float on = smoothstep(0.0f, 0.039999999105930328369140625f, along) * (1.0f - smoothstep(0.14000000059604644775390625f, 0.20000000298023223876953125f, along));
            float2 param_5 = float2(column, drop) + (float(layer_1) * 31.0f).xx;
            float present = step(hash12(param_5), rain * 0.800000011920928955078125f);
            cover += (((_line * on) * present) * (0.5f + (0.25f * float(layer_1))));
        }
        float hailCover = 0.0f;
        if (hail > 0.0500000007450580596923828125f)
        {
            for (int layer_2 = 0; layer_2 < 2; layer_2++)
            {
                float cell_1 = 24.0f + (12.0f * float(layer_2));
                float speed_2 = 640.0f + (220.0f * float(layer_2));
                float2 moved_1 = (q - float2(((ambient_tone.y * speed_2) * 0.60000002384185791015625f) * time, speed_2 * time)) + (float(layer_2) * 79.0f).xx;
                float2 at_1 = floor(moved_1 / cell_1.xx);
                float2 param_6 = at_1 + (float(layer_2) * 53.0f).xx;
                if (hash12(param_6) > (hail * 0.449999988079071044921875f))
                {
                    continue;
                }
                float2 param_7 = at_1 + 11.0f.xx;
                float2 param_8 = at_1 + 19.0f.xx;
                float2 centre_1 = ((at_1 + 0.25f.xx) + (float2(hash12(param_7), hash12(param_8)) * 0.5f)) * cell_1;
                float r_2 = 1.2999999523162841796875f + (0.60000002384185791015625f * float(layer_2));
                hailCover += ((1.0f - smoothstep(r_2 * 0.4000000059604644775390625f, r_2 + (0.4000000059604644775390625f / scale), length(moved_1 - centre_1))) * 0.550000011920928955078125f);
            }
            hailCover = clamp(hailCover, 0.0f, 0.60000002384185791015625f) * exposed;
        }
        cover = clamp((cover * 0.180000007152557373046875f) * (0.60000002384185791015625f + (0.4000000059604644775390625f * rain)), 0.0f, 0.300000011920928955078125f) * exposed;
        if ((rain > 0.60000002384185791015625f) || (thunderPulse > 0.5f))
        {
            float slot = floor(time / 7.0f);
            float _946;
            if (thunderPulse > 0.5f)
            {
                _946 = 0.550000011920928955078125f;
            }
            else
            {
                _946 = (rain - 0.60000002384185791015625f) * 1.60000002384185791015625f;
            }
            float stormChance = _946;
            float2 param_9 = float2(slot, 3.0f);
            float strikes = step(hash12(param_9), stormChance);
            float2 param_10 = float2(slot, 7.0f);
            float dt = ((time - (slot * 7.0f)) - 1.0f) - (4.0f * hash12(param_10));
            float _978;
            if (dt < 0.0f)
            {
                _978 = 0.0f;
            }
            else
            {
                float _989;
                if (dt < 0.180000007152557373046875f)
                {
                    _989 = 0.0f;
                }
                else
                {
                    _989 = 0.699999988079071044921875f * exp((-(dt - 0.180000007152557373046875f)) * 9.0f);
                }
                _978 = exp((-dt) * 10.0f) + _989;
            }
            float flash = _978;
            flash = (clamp(flash * strikes, 0.0f, 1.0f) * 0.2800000011920928955078125f) * exposed;
            color = float4(((float3(0.800000011920928955078125f, 0.839999973773956298828125f, 0.89999997615814208984375f) * cover) + (float3(0.949999988079071044921875f, 0.980000019073486328125f, 1.0f) * hailCover)) + (float3(0.920000016689300537109375f, 0.939999997615814208984375f, 1.0f) * flash), clamp(cover + hailCover, 0.0f, 0.75f)) + (reflection * (1.0f - cover));
            return;
        }
        if (hailCover > 0.001000000047497451305389404296875f)
        {
            color = float4((float3(0.800000011920928955078125f, 0.839999973773956298828125f, 0.89999997615814208984375f) * cover) + (float3(0.949999988079071044921875f, 0.980000019073486328125f, 1.0f) * hailCover), clamp(cover + hailCover, 0.0f, 0.75f)) + (reflection * (1.0f - cover));
            return;
        }
    }
    float4 airborne = 0.0f.xxxx;
    if ((rain <= 0.0199999995529651641845703125f) && (exposed > 0.5f))
    {
        bool _1073 = ambient_season.x > 0.5f;
        bool _1079;
        if (_1073)
        {
            _1079 = ambient_season.x < 1.5f;
        }
        else
        {
            _1079 = _1073;
        }
        bool _1085;
        if (_1079)
        {
            _1085 = ambient_season.y > 0.25f;
        }
        else
        {
            _1085 = _1079;
        }
        if (_1085)
        {
            float leafDensity = clamp((ambient_season.y - 0.25f) * 0.2800000011920928955078125f, 0.0f, 0.2199999988079071044921875f);
            float dayDim = lerp(1.0f, 0.2800000011920928955078125f, ambient_weather.w);
            float3 _1288;
            for (int layer_3 = 0; layer_3 < 2; layer_3++)
            {
                float cell_2 = 96.0f + (44.0f * float(layer_3));
                float driftX = (38.0f + (18.0f * float(layer_3))) * (0.64999997615814208984375f + (0.5f * abs(ambient_tone.y)));
                float driftY = 18.0f + (9.0f * float(layer_3));
                float2 moved_2 = (q - float2(driftX * time, driftY * time)) + (float(layer_3) * 197.0f).xx;
                float2 at_2 = floor(moved_2 / cell_2.xx);
                float2 param_11 = at_2 + (float(layer_3) * 43.0f).xx;
                float r_3 = hash12(param_11);
                if (r_3 > leafDensity)
                {
                    continue;
                }
                float2 param_12 = at_2 + 7.0f.xx;
                float2 param_13 = at_2 + 13.0f.xx;
                float2 centre_2 = ((at_2 + 0.25f.xx) + (float2(hash12(param_12), hash12(param_13)) * 0.5f)) * cell_2;
                centre_2.x += (9.0f * sin((time * (1.10000002384185791015625f + r_3)) + (r_3 * 31.0f)));
                centre_2.y += (5.0f * cos((time * (1.60000002384185791015625f + r_3)) + (r_3 * 19.0f)));
                float2 d_1 = moved_2 - centre_2;
                float ang = (time * (2.2000000476837158203125f + (1.5f * r_3))) + (r_3 * 6.28318023681640625f);
                float ca = cos(ang);
                float sa = sin(ang);
                float2 rd = float2((d_1.x * ca) - (d_1.y * sa), (d_1.x * sa) + (d_1.y * ca));
                float shape = length(float2(rd.x / 2.400000095367431640625f, rd.y / 1.25f));
                float alpha = ((1.0f - smoothstep(0.64999997615814208984375f, 1.0499999523162841796875f, shape)) * 0.7799999713897705078125f) * exposed;
                if (alpha > 0.00999999977648258209228515625f)
                {
                    if (r_3 < (leafDensity * 0.3499999940395355224609375f))
                    {
                        _1288 = float3(0.7599999904632568359375f, 0.550000011920928955078125f, 0.1599999964237213134765625f);
                    }
                    else
                    {
                        bool3 _1306 = (r_3 < (leafDensity * 0.7200000286102294921875f)).xxx;
                        _1288 = float3(_1306.x ? float3(0.699999988079071044921875f, 0.36000001430511474609375f, 0.119999997317790985107421875f).x : float3(0.560000002384185791015625f, 0.23000000417232513427734375f, 0.100000001490116119384765625f).x, _1306.y ? float3(0.699999988079071044921875f, 0.36000001430511474609375f, 0.119999997317790985107421875f).y : float3(0.560000002384185791015625f, 0.23000000417232513427734375f, 0.100000001490116119384765625f).y, _1306.z ? float3(0.699999988079071044921875f, 0.36000001430511474609375f, 0.119999997317790985107421875f).z : float3(0.560000002384185791015625f, 0.23000000417232513427734375f, 0.100000001490116119384765625f).z);
                    }
                    float3 leafCol = _1288;
                    airborne += float4((leafCol * dayDim) * alpha, alpha);
                }
            }
        }
        else
        {
            bool _1326 = ambient_season.x < 0.5f;
            bool _1332;
            if (_1326)
            {
                _1332 = ambient_seasonExtra.y > 0.449999988079071044921875f;
            }
            else
            {
                _1332 = _1326;
            }
            bool _1338;
            if (_1332)
            {
                _1338 = ambient_weather.w < 0.5f;
            }
            else
            {
                _1338 = _1332;
            }
            if (_1338)
            {
                float petalDensity = clamp((ambient_seasonExtra.y - 0.449999988079071044921875f) * 0.2199999988079071044921875f, 0.0f, 0.119999997317790985107421875f);
                float cell_3 = 112.0f;
                float2 moved_3 = (q - float2(26.0f * time, 14.0f * time)) + 71.0f.xx;
                float2 at_3 = floor(moved_3 / cell_3.xx);
                float2 param_14 = at_3 + 61.0f.xx;
                float r_4 = hash12(param_14);
                if (r_4 <= petalDensity)
                {
                    float2 param_15 = at_3 + 3.0f.xx;
                    float2 param_16 = at_3 + 11.0f.xx;
                    float2 centre_3 = ((at_3 + 0.25f.xx) + (float2(hash12(param_15), hash12(param_16)) * 0.5f)) * cell_3;
                    centre_3.x += (7.0f * sin((time * (1.0f + r_4)) + (r_4 * 27.0f)));
                    centre_3.y += (4.0f * cos((time * (1.39999997615814208984375f + r_4)) + (r_4 * 17.0f)));
                    float alpha_1 = (((1.0f - smoothstep(0.699999988079071044921875f, 1.4500000476837158203125f, length(moved_3 - centre_3))) * 0.64999997615814208984375f) * (1.0f - ambient_weather.w)) * exposed;
                    airborne += float4(float3(0.980000019073486328125f, 0.87999999523162841796875f, 0.920000016689300537109375f) * alpha_1, alpha_1);
                }
            }
        }
        if (fireflies > 0.0500000007450580596923828125f)
        {
            float cell_4 = 320.0f;
            float2 moved_4 = q + float2(149.0f, 83.0f);
            float2 at_4 = floor(moved_4 / cell_4.xx);
            float2 param_17 = at_4 + 29.0f.xx;
            float r_5 = hash12(param_17);
            if (r_5 <= (fireflies * 0.054999999701976776123046875f))
            {
                float2 param_18 = at_4 + 5.0f.xx;
                float2 param_19 = at_4 + 17.0f.xx;
                float2 clump = ((at_4 + 0.25f.xx) + (float2(hash12(param_18), hash12(param_19)) * 0.5f)) * cell_4;
                if (length(moved_4 - clump) < 30.0f)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        float fk = float(k);
                        float2 param_20 = at_4 + float2(fk * 11.30000019073486328125f, 3.7000000476837158203125f);
                        float h1 = hash12(param_20);
                        float2 param_21 = at_4 + float2(7.099999904632568359375f, fk * 17.8999996185302734375f);
                        float h2 = hash12(param_21);
                        float2 pos = clump + float2(((h1 - 0.5f) * 18.0f) + (4.5f * sin(((time * (0.64999997615814208984375f + (0.449999988079071044921875f * h2))) + (fk * 2.099999904632568359375f)) + (h1 * 19.0f))), ((h2 - 0.5f) * 12.0f) + (3.2000000476837158203125f * cos(((time * (0.800000011920928955078125f + (0.3499999940395355224609375f * h1))) + (fk * 3.400000095367431640625f)) + (h2 * 13.0f))));
                        float pulse = pow(0.5f + (0.5f * sin(((time * (0.949999988079071044921875f + (0.699999988079071044921875f * h1))) + (fk * 1.89999997615814208984375f)) + (h2 * 29.0f))), 4.19999980926513671875f);
                        float dist = length(moved_4 - pos);
                        float core = ((((1.0f - smoothstep(0.1500000059604644775390625f, 0.85000002384185791015625f, dist)) * pulse) * fireflies) * 0.949999988079071044921875f) * exposed;
                        float halo = (((exp(((-dist) * dist) / 24.0f) * pulse) * fireflies) * 0.2199999988079071044921875f) * exposed;
                        airborne += float4((float3(1.0f, 0.939999997615814208984375f, 0.579999983310699462890625f) * core) + (float3(1.0f, 0.86000001430511474609375f, 0.3400000035762786865234375f) * halo), clamp((core * 0.75f) + (halo * 0.319999992847442626953125f), 0.0f, 0.85000002384185791015625f));
                    }
                }
            }
        }
    }
    airborne = clamp(airborne, 0.0f.xxxx, 1.0f.xxxx);
    color = (float4(float3(0.800000011920928955078125f, 0.839999973773956298828125f, 0.89999997615814208984375f) * cover, cover) + (reflection * (1.0f - cover))) + (airborne * (1.0f - cover));
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
