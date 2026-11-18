Texture2D<float4> art : register(t0, space2);
SamplerState _art_sampler : register(s0, space2);

static float4 inUVRect;
static float2 inUV;
static float4 inDepthInterval;
static float4 color;
static float4 inTint;
static float inOpacity;

struct SPIRV_Cross_Input
{
    float2 inUV : TEXCOORD0;
    nointerpolation float4 inTint : TEXCOORD1;
    nointerpolation float4 inDepthInterval : TEXCOORD2;
    nointerpolation float inOpacity : TEXCOORD3;
    nointerpolation float4 inUVRect : TEXCOORD4;
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

float4 fetchArtClamped(int2 minTexel, int2 maxTexel, int2 c)
{
    return art.Load(int3(clamp(c, minTexel, maxTexel), 0));
}

float semiTap(float a)
{
    return float((a > 0.02999999932944774627685546875f) && (a < 0.949999988079071044921875f));
}

float colorDist(float4 a, float4 b)
{
    float3 pa = sqrt(max(a.xyz, 0.0f.xxx));
    float3 pb = sqrt(max(b.xyz, 0.0f.xxx));
    float3 d = pa - pb;
    float da = a.w - b.w;
    return sqrt((dot(d, d) * 0.64999997615814208984375f) + ((da * da) * 1.35000002384185791015625f));
}

bool shouldBevelCorner(float4 K, float4 A, float4 B, float4 O, int2 minTexel, int2 maxTexel, int2 i0, int2 s1Off, int2 s2Off)
{
    float4 M = (A + B) * 0.5f;
    float4 param = A;
    float4 param_1 = B;
    float dAB = colorDist(param, param_1);
    float4 param_2 = K;
    float4 param_3 = A;
    float dKA = colorDist(param_2, param_3);
    float4 param_4 = K;
    float4 param_5 = B;
    float dKB = colorDist(param_4, param_5);
    float minDK = min(dKA, dKB);
    bool _153 = dAB >= 0.2199999988079071044921875f;
    bool _162;
    if (!_153)
    {
        _162 = (dAB * 2.0f) >= minDK;
    }
    else
    {
        _162 = _153;
    }
    if (_162 || (minDK <= 0.07999999821186065673828125f))
    {
        return false;
    }
    float4 param_6 = O;
    float4 param_7 = M;
    float dOM = colorDist(param_6, param_7);
    float4 param_8 = K;
    float4 param_9 = M;
    float dKM = colorDist(param_8, param_9);
    if (dOM < (dKM * 0.64999997615814208984375f))
    {
        return true;
    }
    bool _197 = M.w > (K.w + 0.25f);
    bool _206;
    if (_197)
    {
        _206 = O.w < (M.w - 0.25f);
    }
    else
    {
        _206 = _197;
    }
    if (_206)
    {
        return true;
    }
    if (dOM < 0.2199999988079071044921875f)
    {
        int2 param_10 = minTexel;
        int2 param_11 = maxTexel;
        int2 param_12 = i0 + s1Off;
        float4 S1 = fetchArtClamped(param_10, param_11, param_12);
        int2 param_13 = minTexel;
        int2 param_14 = maxTexel;
        int2 param_15 = i0 + s2Off;
        float4 S2 = fetchArtClamped(param_13, param_14, param_15);
        float4 param_16 = K;
        float4 param_17 = S1;
        float4 param_18 = K;
        float4 param_19 = S2;
        float dKS = min(colorDist(param_16, param_17), colorDist(param_18, param_19));
        bool _248 = dKS < 0.23999999463558197021484375f;
        bool _256;
        if (_248)
        {
            _256 = (dKS * 1.7999999523162841796875f) < minDK;
        }
        else
        {
            _256 = _248;
        }
        return _256;
    }
    return false;
}

float4 sampleBeveled(float2 texel, float2 fw, int2 minTexel, int2 maxTexel, inout float4 c00, inout float4 c10, inout float4 c01, inout float4 c11)
{
    float2 p = texel - 0.5f.xx;
    int2 i0 = int2(floor(p));
    float2 f = p - float2(i0);
    int2 param = minTexel;
    int2 param_1 = maxTexel;
    int2 param_2 = i0;
    c00 = fetchArtClamped(param, param_1, param_2);
    int2 param_3 = minTexel;
    int2 param_4 = maxTexel;
    int2 param_5 = i0 + int2(1, 0);
    c10 = fetchArtClamped(param_3, param_4, param_5);
    int2 param_6 = minTexel;
    int2 param_7 = maxTexel;
    int2 param_8 = i0 + int2(0, 1);
    c01 = fetchArtClamped(param_6, param_7, param_8);
    int2 param_9 = minTexel;
    int2 param_10 = maxTexel;
    int2 param_11 = i0 + int2(1, 1);
    c11 = fetchArtClamped(param_9, param_10, param_11);
    float param_12 = c00.w;
    float param_13 = c10.w;
    float param_14 = c01.w;
    float param_15 = c11.w;
    float semiX = lerp(max(semiTap(param_12), semiTap(param_13)), max(semiTap(param_14), semiTap(param_15)), f.y);
    float param_16 = c00.w;
    float param_17 = c01.w;
    float param_18 = c10.w;
    float param_19 = c11.w;
    float semiY = lerp(max(semiTap(param_16), semiTap(param_17)), max(semiTap(param_18), semiTap(param_19)), f.x);
    float2 baseFw = min(fw, 1.0f.xx);
    float2 effFw = float2(lerp(baseFw.x, max(baseFw.x, 0.85000002384185791015625f), semiX), lerp(baseFw.y, max(baseFw.y, 0.85000002384185791015625f), semiY));
    float2 ws = clamp(((f - 0.5f.xx) / effFw) + 0.5f.xx, 0.0f.xx, 1.0f.xx);
    ws = (ws * ws) * (3.0f.xx - (ws * 2.0f));
    float w00 = (1.0f - ws.x) * (1.0f - ws.y);
    float w10 = ws.x * (1.0f - ws.y);
    float w01 = (1.0f - ws.x) * ws.y;
    float w11 = ws.x * ws.y;
    float aaWidth = clamp(0.5f * max(effFw.x, effFw.y), 0.0599999986588954925537109375f, 0.5f);
    if ((f.x + f.y) < (0.5f + aaWidth))
    {
        float4 param_20 = c00;
        float4 param_21 = c10;
        float4 param_22 = c01;
        float4 param_23 = c11;
        int2 param_24 = minTexel;
        int2 param_25 = maxTexel;
        int2 param_26 = i0;
        int2 param_27 = int2(-1, 1);
        int2 param_28 = int2(1, -1);
        if (shouldBevelCorner(param_20, param_21, param_22, param_23, param_24, param_25, param_26, param_27, param_28))
        {
            float bevel = 1.0f - smoothstep(0.5f - aaWidth, 0.5f + aaWidth, f.x + f.y);
            float cut = max(0.0f, w00 - bevel);
            w00 -= cut;
            w10 += (0.5f * cut);
            w01 += (0.5f * cut);
        }
    }
    else
    {
        if (((1.0f - f.x) + (1.0f - f.y)) < (0.5f + aaWidth))
        {
            float4 param_29 = c11;
            float4 param_30 = c10;
            float4 param_31 = c01;
            float4 param_32 = c00;
            int2 param_33 = minTexel;
            int2 param_34 = maxTexel;
            int2 param_35 = i0;
            int2 param_36 = int2(2, 0);
            int2 param_37 = int2(0, 2);
            if (shouldBevelCorner(param_29, param_30, param_31, param_32, param_33, param_34, param_35, param_36, param_37))
            {
                float bevel_1 = 1.0f - smoothstep(0.5f - aaWidth, 0.5f + aaWidth, (1.0f - f.x) + (1.0f - f.y));
                float cut_1 = max(0.0f, w11 - bevel_1);
                w11 -= cut_1;
                w10 += (0.5f * cut_1);
                w01 += (0.5f * cut_1);
            }
        }
        else
        {
            if (((1.0f - f.x) + f.y) < (0.5f + aaWidth))
            {
                float4 param_38 = c10;
                float4 param_39 = c00;
                float4 param_40 = c11;
                float4 param_41 = c01;
                int2 param_42 = minTexel;
                int2 param_43 = maxTexel;
                int2 param_44 = i0;
                int2 param_45 = int2(2, 1);
                int2 param_46 = int2(0, -1);
                if (shouldBevelCorner(param_38, param_39, param_40, param_41, param_42, param_43, param_44, param_45, param_46))
                {
                    float bevel_2 = 1.0f - smoothstep(0.5f - aaWidth, 0.5f + aaWidth, (1.0f - f.x) + f.y);
                    float cut_2 = max(0.0f, w10 - bevel_2);
                    w10 -= cut_2;
                    w00 += (0.5f * cut_2);
                    w11 += (0.5f * cut_2);
                }
            }
            else
            {
                if ((f.x + (1.0f - f.y)) < (0.5f + aaWidth))
                {
                    float4 param_47 = c01;
                    float4 param_48 = c00;
                    float4 param_49 = c11;
                    float4 param_50 = c10;
                    int2 param_51 = minTexel;
                    int2 param_52 = maxTexel;
                    int2 param_53 = i0;
                    int2 param_54 = int2(-1, 0);
                    int2 param_55 = int2(1, 2);
                    if (shouldBevelCorner(param_47, param_48, param_49, param_50, param_51, param_52, param_53, param_54, param_55))
                    {
                        float bevel_3 = 1.0f - smoothstep(0.5f - aaWidth, 0.5f + aaWidth, f.x + (1.0f - f.y));
                        float cut_3 = max(0.0f, w01 - bevel_3);
                        w01 -= cut_3;
                        w00 += (0.5f * cut_3);
                        w11 += (0.5f * cut_3);
                    }
                }
            }
        }
    }
    return (((c00 * w00) + (c10 * w10)) + (c01 * w01)) + (c11 * w11);
}

float4 catmullRomWeights(float t)
{
    float t2 = t * t;
    float t3 = t2 * t;
    return float4((((-0.5f) * t) + t2) - (0.5f * t3), (1.0f - (2.5f * t2)) + (1.5f * t3), ((0.5f * t) + (2.0f * t2)) - (1.5f * t3), ((-0.5f) * t2) + (0.5f * t3));
}

void frag_main()
{
    uint _741_dummy_parameter;
    int2 artDims = int2(spvTextureSize(art, uint(0), _741_dummy_parameter));
    float2 texSize = float2(artDims);
    float2 halfTexel = 0.5f.xx / texSize;
    float2 minUV = inUVRect.xy + halfTexel;
    float2 maxUV = max(minUV, (inUVRect.xy + inUVRect.zw) - halfTexel);
    float2 clampedUV = clamp(inUV, minUV, maxUV);
    float2 texel = clampedUV * texSize;
    float2 fw = max(fwidth(texel), 9.9999997473787516355514526367188e-05f.xx);
    int2 minTexel = clamp(int2(floor((inUVRect.xy * texSize) + 0.001000000047497451305389404296875f.xx)), int2(0, 0), artDims - int2(1, 1));
    int2 maxTexel = clamp(int2(ceil(((inUVRect.xy + inUVRect.zw) * texSize) - 0.001000000047497451305389404296875f.xx)) - int2(1, 1), minTexel, artDims - int2(1, 1));
    if (inDepthInterval.z > 0.5f)
    {
        if (max(fw.x, fw.y) > 1.0499999523162841796875f)
        {
            bool _837 = inUVRect.x <= 9.9999997473787516355514526367188e-05f;
            bool _843;
            if (_837)
            {
                _843 = inUVRect.y <= 9.9999997473787516355514526367188e-05f;
            }
            else
            {
                _843 = _837;
            }
            bool _850;
            if (_843)
            {
                _850 = inUVRect.z >= 0.99989998340606689453125f;
            }
            else
            {
                _850 = _843;
            }
            bool _856;
            if (_850)
            {
                _856 = inUVRect.w >= 0.99989998340606689453125f;
            }
            else
            {
                _856 = _850;
            }
            bool fullTexture = _856;
            if (fullTexture)
            {
                color = art.Sample(_art_sampler, clampedUV) * inTint;
            }
            else
            {
                float lod = max(0.0f, log2(max(fw.x, fw.y)));
                float2 mipHalf = (0.5f * exp2(lod)).xx / texSize;
                float2 sMin = inUVRect.xy + mipHalf;
                float2 sMax = max(sMin, (inUVRect.xy + inUVRect.zw) - mipHalf);
                color = art.SampleLevel(_art_sampler, clamp(inUV, sMin, sMax), lod) * inTint;
            }
            return;
        }
        if (min(fw.x, fw.y) > 0.920000016689300537109375f)
        {
            float2 i = floor(texel + 0.5f.xx);
            float2 f = texel - i;
            float2 sharp = (i + clamp(f / fw, (-0.5f).xx, 0.5f.xx)) / texSize;
            float _942;
            if (fw.x <= 1.0f)
            {
                _942 = sharp.x;
            }
            else
            {
                _942 = inUV.x;
            }
            float _954;
            if (fw.y <= 1.0f)
            {
                _954 = sharp.y;
            }
            else
            {
                _954 = inUV.y;
            }
            float2 uv = clamp(float2(_942, _954), minUV, maxUV);
            color = art.SampleLevel(_art_sampler, uv, 0.0f) * inTint;
            return;
        }
        float2 p = texel - 0.5f.xx;
        int2 i0 = int2(floor(p));
        float2 f_1 = p - float2(i0);
        float2 param = texel;
        float2 param_1 = fw;
        int2 param_2 = minTexel;
        int2 param_3 = maxTexel;
        float4 param_4;
        float4 param_5;
        float4 param_6;
        float4 param_7;
        float4 _1003 = sampleBeveled(param, param_1, param_2, param_3, param_4, param_5, param_6, param_7);
        float4 c00 = param_4;
        float4 c10 = param_5;
        float4 c01 = param_6;
        float4 c11 = param_7;
        float4 beveled = _1003;
        float param_8 = f_1.x;
        float4 wx = catmullRomWeights(param_8);
        float param_9 = f_1.y;
        float4 wy = catmullRomWeights(param_9);
        float4 bicubic = 0.0f.xxxx;
        float _1032;
        float _1040;
        float _1048;
        for (int dy = -1; dy <= 2; dy++)
        {
            if (dy == (-1))
            {
                _1032 = wy.x;
            }
            else
            {
                if (dy == 0)
                {
                    _1040 = wy.y;
                }
                else
                {
                    if (dy == 1)
                    {
                        _1048 = wy.z;
                    }
                    else
                    {
                        _1048 = wy.w;
                    }
                    _1040 = _1048;
                }
                _1032 = _1040;
            }
            float ry = _1032;
            int2 param_10 = minTexel;
            int2 param_11 = maxTexel;
            int2 param_12 = i0 + int2(-1, dy);
            int2 param_13 = minTexel;
            int2 param_14 = maxTexel;
            int2 param_15 = i0 + int2(0, dy);
            int2 param_16 = minTexel;
            int2 param_17 = maxTexel;
            int2 param_18 = i0 + int2(1, dy);
            int2 param_19 = minTexel;
            int2 param_20 = maxTexel;
            int2 param_21 = i0 + int2(2, dy);
            float4 rowSum = (((fetchArtClamped(param_10, param_11, param_12) * wx.x) + (fetchArtClamped(param_13, param_14, param_15) * wx.y)) + (fetchArtClamped(param_16, param_17, param_18) * wx.z)) + (fetchArtClamped(param_19, param_20, param_21) * wx.w);
            bicubic += (rowSum * ry);
        }
        float4 localMin = min(min(c00, c10), min(c01, c11));
        float4 localMax = max(max(c00, c10), max(c01, c11));
        bicubic = clamp(bicubic, localMin, localMax);
        float4 param_22 = c00;
        float4 param_23 = c10;
        float4 param_24 = c01;
        float4 param_25 = c11;
        float4 param_26 = c00;
        float4 param_27 = c01;
        float4 param_28 = c10;
        float4 param_29 = c11;
        float contrast = max(max(colorDist(param_22, param_23), colorDist(param_24, param_25)), max(colorDist(param_26, param_27), colorDist(param_28, param_29)));
        float edgeBlend = smoothstep(0.180000007152557373046875f, 0.4199999868869781494140625f, contrast);
        color = lerp(bicubic, beveled, edgeBlend.xxxx) * inTint;
        return;
    }
    if (inDepthInterval.w < 0.5f)
    {
        int2 nearest = clamp(int2(clampedUV * texSize), minTexel, maxTexel);
        float4 raw = art.Load(int3(nearest, 0));
        if (raw.w < 0.0039215688593685626983642578125f)
        {
            discard;
        }
        color = raw * inTint;
        return;
    }
    if (max(fw.x, fw.y) > 1.25f)
    {
        color = art.Sample(_art_sampler, clampedUV) * inTint;
        return;
    }
    float2 param_30 = texel;
    float2 param_31 = fw;
    int2 param_32 = minTexel;
    int2 param_33 = maxTexel;
    float4 param_34;
    float4 param_35;
    float4 param_36;
    float4 param_37;
    float4 _1239 = sampleBeveled(param_30, param_31, param_32, param_33, param_34, param_35, param_36, param_37);
    float4 lc00 = param_34;
    float4 lc10 = param_35;
    float4 lc01 = param_36;
    float4 lc11 = param_37;
    color = _1239 * inTint;
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    inUVRect = stage_input.inUVRect;
    inUV = stage_input.inUV;
    inDepthInterval = stage_input.inDepthInterval;
    inTint = stage_input.inTint;
    inOpacity = stage_input.inOpacity;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.color = color;
    return stage_output;
}
