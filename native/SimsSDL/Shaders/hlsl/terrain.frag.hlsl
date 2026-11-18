static float2 inDetail;
static float2 inCoordinate;
static float inGrass;
static float4 inLight;
static float4 inDark;
static float4 color;
static float4 inTint;
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

float3 toLinear(float3 c)
{
    return lerp(c / 12.9200000762939453125f.xxx, pow((c + 0.054999999701976776123046875f.xxx) / 1.05499994754791259765625f.xxx, 2.400000095367431640625f.xxx), step(0.040449999272823333740234375f.xxx, c));
}

void frag_main()
{
    int detail = int(inDetail.x + 0.5f);
    int zoom = clamp(int(inDetail.y + 0.5f), 1, 3);
    float2 artCoord = (inCoordinate * float(1 << zoom)) * 0.25f;
    int2 pixel = int2(floor(artCoord));
    float2 fw = fwidth(artCoord);
    float ditherWeight = 1.0f - smoothstep(0.949999988079071044921875f, 1.85000002384185791015625f, max(fw.x, fw.y));
    int column = pixel.x & 3;
    float _144;
    if ((pixel.y & 1) == 0)
    {
        float _149;
        if (column == 1)
        {
            _149 = -8.0f;
        }
        else
        {
            _149 = (column == 3) ? (-6.0f) : 0.0f;
        }
        _144 = _149;
    }
    else
    {
        float _163;
        if (column == 0)
        {
            _163 = -6.0f;
        }
        else
        {
            _163 = (column == 2) ? (-8.0f) : 0.0f;
        }
        _144 = _163;
    }
    float shade = _144 * ditherWeight;
    if (detail > 0)
    {
        float value = floor((inGrass * 255.0f) + 0.5f) - 128.0f;
        float low = (detail == 1) ? 300.0f : 500.0f;
        float high = (detail == 1) ? 800.0f : 1200.0f;
        float _204;
        if (value >= 85.0f)
        {
            _204 = high;
        }
        else
        {
            float _212;
            if (value <= (-73.0f))
            {
                _212 = low;
            }
            else
            {
                _212 = floor(low + (((high - low) * (value + 73.0f)) / 158.0f));
            }
            _204 = _212;
        }
        float count = _204;
        if (zoom == 1)
        {
            count = floor(count / 4.0f);
        }
        float tile = float(16 << zoom);
        float coverage = 1.0f - exp((-count) / ((tile * tile) * 0.25f));
        int tallest = (zoom == 3) ? 4 : 1;
        int _295;
        float _332;
        for (int below = 0; below < tallest; below++)
        {
            int2 start = pixel + int2(0, below);
            int2 param = start;
            uint param_1 = 1u;
            if (float(hash(param, param_1) & 16777215u) >= (coverage * 16777216.0f))
            {
                continue;
            }
            int2 param_2 = start;
            uint param_3 = 2u;
            uint random = hash(param_2, param_3);
            if (tallest > 1)
            {
                _295 = tallest - int(random % uint(tallest / 2));
            }
            else
            {
                _295 = tallest;
            }
            int height = _295;
            if (value <= (-73.0f))
            {
                height = max(1, (height - 1));
            }
            if (below >= height)
            {
                continue;
            }
            uint spread = (random >> uint(8)) % 31u;
            if (((random >> uint(16)) & 1u) == 1u)
            {
                _332 = 30.0f - float(spread);
            }
            else
            {
                _332 = 30.0f + float(spread);
            }
            shade = _332;
            break;
        }
    }
    float3 ramp = clamp(inLight.xyz + (((inDark.xyz - inLight.xyz) * shade) / 128.0f.xxx), 0.0f.xxx, 1.0f.xxx);
    float3 param_4 = floor((ramp * 255.0f) + 0.5f.xxx) / 255.0f.xxx;
    color = float4(toLinear(param_4) * inTint.xyz, 1.0f);
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    inDetail = stage_input.inDetail;
    inCoordinate = stage_input.inCoordinate;
    inGrass = stage_input.inGrass;
    inLight = stage_input.inLight;
    inDark = stage_input.inDark;
    inTint = stage_input.inTint;
    inColor = stage_input.inColor;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.color = color;
    return stage_output;
}
