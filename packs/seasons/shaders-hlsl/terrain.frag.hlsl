static float2 inDetail;
static float inGrass;
static float4 inLight;
static float4 inDark;
static float4 color;
static float4 inTint;
static float2 inCoordinate;
static float4 inColor;

struct SPIRV_Cross_Input
{
    float4 inColor : TEXCOORD0;
    float2 inCoordinate : TEXCOORD1;
    float4 inLight : TEXCOORD2;
    float4 inDark : TEXCOORD3;
    float4 inTint : TEXCOORD4;
    float inGrass : TEXCOORD5;
    nointerpolation float2 inDetail : TEXCOORD6;
};

struct SPIRV_Cross_Output
{
    float4 color : SV_Target0;
};

float3 toLinear(float3 c)
{
    return lerp(c / 12.9200000762939453125f.xxx, pow((c + 0.054999999701976776123046875f.xxx) / 1.05499994754791259765625f.xxx, 2.400000095367431640625f.xxx), step(0.040449999272823333740234375f.xxx, c));
}

uint hash(int2 _point, uint salt)
{
    uint value = ((uint(_point.x) * 2376512323u) ^ (uint(_point.y) * 3625334849u)) ^ (salt * 3407524639u);
    value ^= (value >> uint(16));
    value *= 2146121005u;
    value ^= (value >> uint(15));
    value *= 2221713035u;
    value ^= (value >> uint(16));
    return value;
}

float rand01(int2 _point, uint salt)
{
    int2 param = _point;
    uint param_1 = salt;
    return float(hash(param, param_1) & 16777215u) * 5.9604644775390625e-08f;
}

void frag_main()
{
    int detail = int(inDetail.x + 0.5f);
    float value = (inGrass * 255.0f) - 128.0f;
    float growth = clamp((value + 73.0f) / 158.0f, 0.0f, 1.0f);
    float greenness = (smoothstep(0.004999999888241291046142578125f, 0.0320000015199184417724609375f, inLight.y - inLight.x) * smoothstep(0.07999999821186065673828125f, 0.180000007152557373046875f, inLight.y - inLight.z)) * growth;
    float3 lightCol = clamp(lerp(inLight.xyz, (inLight.xyz * float3(0.949999988079071044921875f, 1.03999996185302734375f, 0.959999978542327880859375f)) + float3(0.0f, 0.00999999977648258209228515625f, 0.00200000009499490261077880859375f), (0.60000002384185791015625f * greenness).xxx), 0.0f.xxx, 1.0f.xxx);
    float3 darkCol = clamp(lerp(inDark.xyz, inDark.xyz * float3(0.930000007152557373046875f, 1.0299999713897705078125f, 0.959999978542327880859375f), (0.60000002384185791015625f * greenness).xxx), 0.0f.xxx, 1.0f.xxx);
    if (detail <= 0)
    {
        float3 param = lightCol;
        color = float4(toLinear(param) * inTint.xyz, 1.0f);
        return;
    }
    float maxDensity = (detail == 1) ? 0.7400000095367431640625f : 0.949999988079071044921875f;
    float minDensity = (detail == 1) ? 0.23999999463558197021484375f : 0.36000001430511474609375f;
    float density = lerp(minDensity, maxDensity, growth);
    float2 p = inCoordinate / float2(0.7799999713897705078125f, 0.680000007152557373046875f);
    float2 fw = fwidth(p);
    int2 grainCell = int2(floor(inCoordinate * 1.5f));
    int2 param_1 = grainCell;
    uint param_2 = 91u;
    float fineGrain = (rand01(param_1, param_2) - 0.5f) * 0.04500000178813934326171875f;
    float bedShade = (0.1500000059604644775390625f * density) + fineGrain;
    float3 turfSrgb = clamp(lerp(lightCol, darkCol, bedShade.xxx), 0.0f.xxx, 1.0f.xxx);
    float bladeLod = 1.0f - smoothstep(0.87999999523162841796875f, 1.5499999523162841796875f, fw.x);
    if (bladeLod > 0.00999999977648258209228515625f)
    {
        int2 baseCell = int2(floor(p));
        float aa = max(fw.x * 0.4799999892711639404296875f, 0.085000000894069671630859375f);
        float flow = 0.20000000298023223876953125f * sin((inCoordinate.x * 0.085000000894069671630859375f) + (0.75f * sin(inCoordinate.y * 0.104999996721744537353515625f)));
        int bladesPerRoot = (detail >= 2) ? 2 : 1;
        float _359;
        for (int dy = 0; dy <= 3; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int2 rc = baseCell + int2(dx, dy);
                for (int b = 0; b < 2; b++)
                {
                    if (b >= bladesPerRoot)
                    {
                        break;
                    }
                    uint salt = uint(b) * 37u;
                    if (b == 0)
                    {
                        _359 = density;
                    }
                    else
                    {
                        _359 = (density * growth) * 0.819999992847442626953125f;
                    }
                    float spawnThresh = _359;
                    int2 param_3 = rc;
                    uint param_4 = 1u + salt;
                    if (rand01(param_3, param_4) >= spawnThresh)
                    {
                        continue;
                    }
                    int2 param_5 = rc;
                    uint param_6 = 2u + salt;
                    int2 param_7 = rc;
                    uint param_8 = 3u + salt;
                    float2 root = float2(rc) + float2(0.1599999964237213134765625f + (0.680000007152557373046875f * rand01(param_5, param_6)), 0.180000007152557373046875f + (0.680000007152557373046875f * rand01(param_7, param_8)));
                    float rise = root.y - p.y;
                    if (rise <= 0.0f)
                    {
                        continue;
                    }
                    int2 param_9 = rc;
                    uint param_10 = 4u + salt;
                    float h = (lerp(1.75f, 3.4500000476837158203125f, rand01(param_9, param_10)) * lerp(0.680000007152557373046875f, 1.03999996185302734375f, growth)) * ((b == 0) ? 1.0f : 0.7799999713897705078125f);
                    float t = rise / h;
                    if (t >= 1.0f)
                    {
                        continue;
                    }
                    int2 param_11 = rc;
                    uint param_12 = 5u + salt;
                    float lean = ((rand01(param_11, param_12) - 0.5f) * 0.62000000476837158203125f) + flow;
                    float spineX = root.x + ((lean * t) * t);
                    float halfW = lerp(0.3499999940395355224609375f, 0.054999999701976776123046875f, pow(t, 0.75f));
                    float dist = abs(p.x - spineX);
                    float cov = (1.0f - smoothstep(halfW - aa, halfW + aa, dist)) * bladeLod;
                    if (cov <= 0.004999999888241291046142578125f)
                    {
                        continue;
                    }
                    int2 param_13 = rc;
                    uint param_14 = 6u + salt;
                    float bladeTone = rand01(param_13, param_14);
                    float shade = lerp(0.0500000007450580596923828125f, 0.439999997615814208984375f, bladeTone);
                    shade += (0.2199999988079071044921875f * (1.0f - smoothstep(0.0f, 0.4199999868869781494140625f, t)));
                    shade -= (0.14000000059604644775390625f * smoothstep(0.579999983310699462890625f, 1.0f, t));
                    float3 bladeCol = clamp(lerp(lightCol, darkCol, shade.xxx), 0.0f.xxx, 1.0f.xxx);
                    turfSrgb = lerp(turfSrgb, bladeCol, cov.xxx);
                }
            }
        }
    }
    else
    {
        int2 farCell = int2(floor(inCoordinate * 0.64999997615814208984375f));
        int2 param_15 = farCell;
        uint param_16 = 17u;
        float farTex = lerp(0.039999999105930328369140625f, 0.3400000035762786865234375f, rand01(param_15, param_16)) * density;
        turfSrgb = clamp(lerp(lightCol, darkCol, farTex.xxx), 0.0f.xxx, 1.0f.xxx);
    }
    float3 param_17 = turfSrgb;
    color = float4(toLinear(param_17) * inTint.xyz, 1.0f);
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    inDetail = stage_input.inDetail;
    inGrass = stage_input.inGrass;
    inLight = stage_input.inLight;
    inDark = stage_input.inDark;
    inTint = stage_input.inTint;
    inCoordinate = stage_input.inCoordinate;
    inColor = stage_input.inColor;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.color = color;
    return stage_output;
}
