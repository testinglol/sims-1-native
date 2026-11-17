#version 450
layout(set = 2, binding = 0) uniform sampler2D art;
layout(location = 0) in vec2 inUV;
layout(location = 1) flat in vec4 inTint;
layout(location = 2) flat in vec4 inDepthInterval;
layout(location = 4) flat in vec4 inUVRect;
layout(location = 0) out vec4 color;

float colorDist(vec4 a, vec4 b) {
    vec3 pa = sqrt(max(a.rgb, vec3(0.0)));
    vec3 pb = sqrt(max(b.rgb, vec3(0.0)));
    vec3 d = pa - pb;
    float da = a.a - b.a;
    return sqrt(dot(d, d) * 0.65 + da * da * 1.35);
}

float semiTap(float a) {
    return (a > 0.03 && a < 0.95) ? 1.0 : 0.0;
}

vec4 fetchArtClamped(ivec2 minTexel, ivec2 maxTexel, ivec2 c) {
    return texelFetch(art, clamp(c, minTexel, maxTexel), 0);
}

bool shouldBevelCorner(
    vec4 K, vec4 A, vec4 B, vec4 O,
    ivec2 minTexel, ivec2 maxTexel, ivec2 i0, ivec2 s1Off, ivec2 s2Off
) {
    vec4 M = 0.5 * (A + B);
    float dAB = colorDist(A, B);
    float dKA = colorDist(K, A);
    float dKB = colorDist(K, B);
    float minDK = min(dKA, dKB);
    if (dAB >= 0.22 || dAB * 2.0 >= minDK || minDK <= 0.08) {
        return false;
    }
    float dOM = colorDist(O, M);
    float dKM = colorDist(K, M);
    if (dOM < dKM * 0.65) {
        return true;
    }
    if (M.a > K.a + 0.25 && O.a < M.a - 0.25) {
        return true;
    }
    if (dOM < 0.22) {
        vec4 S1 = fetchArtClamped(minTexel, maxTexel, i0 + s1Off);
        vec4 S2 = fetchArtClamped(minTexel, maxTexel, i0 + s2Off);
        float dKS = min(colorDist(K, S1), colorDist(K, S2));
        return dKS < 0.24 && dKS * 1.8 < minDK;
    }
    return false;
}

vec4 catmullRomWeights(float t) {
    float t2 = t * t;
    float t3 = t2 * t;
    return vec4(
        -0.5 * t + t2 - 0.5 * t3,
        1.0 - 2.5 * t2 + 1.5 * t3,
        0.5 * t + 2.0 * t2 - 1.5 * t3,
        -0.5 * t2 + 0.5 * t3
    );
}

vec4 sampleBeveled(
    vec2 texel, vec2 fw, ivec2 minTexel, ivec2 maxTexel,
    out vec4 c00, out vec4 c10, out vec4 c01, out vec4 c11
) {
    vec2 p = texel - 0.5;
    ivec2 i0 = ivec2(floor(p));
    vec2 f = p - vec2(i0);

    c00 = fetchArtClamped(minTexel, maxTexel, i0);
    c10 = fetchArtClamped(minTexel, maxTexel, i0 + ivec2(1, 0));
    c01 = fetchArtClamped(minTexel, maxTexel, i0 + ivec2(0, 1));
    c11 = fetchArtClamped(minTexel, maxTexel, i0 + ivec2(1, 1));

    float semiX = mix(max(semiTap(c00.a), semiTap(c10.a)), max(semiTap(c01.a), semiTap(c11.a)), f.y);
    float semiY = mix(max(semiTap(c00.a), semiTap(c01.a)), max(semiTap(c10.a), semiTap(c11.a)), f.x);
    vec2 baseFw = min(fw, vec2(1.0));
    vec2 effFw = vec2(
        mix(baseFw.x, max(baseFw.x, 0.85), semiX),
        mix(baseFw.y, max(baseFw.y, 0.85), semiY)
    );
    vec2 ws = clamp((f - 0.5) / effFw + 0.5, 0.0, 1.0);
    ws = ws * ws * (3.0 - 2.0 * ws);

    float w00 = (1.0 - ws.x) * (1.0 - ws.y);
    float w10 = ws.x * (1.0 - ws.y);
    float w01 = (1.0 - ws.x) * ws.y;
    float w11 = ws.x * ws.y;

    float aaWidth = clamp(0.5 * max(effFw.x, effFw.y), 0.06, 0.5);
    if (f.x + f.y < 0.5 + aaWidth) {
        if (shouldBevelCorner(c00, c10, c01, c11, minTexel, maxTexel, i0, ivec2(-1, 1), ivec2(1, -1))) {
            float bevel = 1.0 - smoothstep(0.5 - aaWidth, 0.5 + aaWidth, f.x + f.y);
            float cut = max(0.0, w00 - bevel);
            w00 -= cut;
            w10 += 0.5 * cut;
            w01 += 0.5 * cut;
        }
    } else if ((1.0 - f.x) + (1.0 - f.y) < 0.5 + aaWidth) {
        if (shouldBevelCorner(c11, c10, c01, c00, minTexel, maxTexel, i0, ivec2(2, 0), ivec2(0, 2))) {
            float bevel = 1.0 - smoothstep(0.5 - aaWidth, 0.5 + aaWidth, (1.0 - f.x) + (1.0 - f.y));
            float cut = max(0.0, w11 - bevel);
            w11 -= cut;
            w10 += 0.5 * cut;
            w01 += 0.5 * cut;
        }
    } else if ((1.0 - f.x) + f.y < 0.5 + aaWidth) {
        if (shouldBevelCorner(c10, c00, c11, c01, minTexel, maxTexel, i0, ivec2(2, 1), ivec2(0, -1))) {
            float bevel = 1.0 - smoothstep(0.5 - aaWidth, 0.5 + aaWidth, (1.0 - f.x) + f.y);
            float cut = max(0.0, w10 - bevel);
            w10 -= cut;
            w00 += 0.5 * cut;
            w11 += 0.5 * cut;
        }
    } else if (f.x + (1.0 - f.y) < 0.5 + aaWidth) {
        if (shouldBevelCorner(c01, c00, c11, c10, minTexel, maxTexel, i0, ivec2(-1, 0), ivec2(1, 2))) {
            float bevel = 1.0 - smoothstep(0.5 - aaWidth, 0.5 + aaWidth, f.x + (1.0 - f.y));
            float cut = max(0.0, w01 - bevel);
            w01 -= cut;
            w00 += 0.5 * cut;
            w11 += 0.5 * cut;
        }
    }

    return c00 * w00 + c10 * w10 + c01 * w01 + c11 * w11;
}

void main() {
    ivec2 artDims = textureSize(art, 0);
    vec2 texSize = vec2(artDims);
    vec2 halfTexel = 0.5 / texSize;
    vec2 minUV = inUVRect.xy + halfTexel;
    vec2 maxUV = max(minUV, inUVRect.xy + inUVRect.zw - halfTexel);
    vec2 clampedUV = clamp(inUV, minUV, maxUV);
    vec2 texel = clampedUV * texSize;
    vec2 fw = max(fwidth(texel), vec2(1e-4));
    ivec2 minTexel = clamp(ivec2(floor(inUVRect.xy * texSize + vec2(1e-3))), ivec2(0), artDims - 1);
    ivec2 maxTexel = clamp(ivec2(ceil((inUVRect.xy + inUVRect.zw) * texSize - vec2(1e-3))) - 1, minTexel, artDims - 1);

    // UI & fullscreen 800x600 overlays: mipmapped minification, exact 1:1 snap, or hybrid
    // anti-ringing Catmull-Rom + edge bevel upscaling.
    if (inDepthInterval.z > 0.5) {
        if (max(fw.x, fw.y) > 1.05) {
            bool fullTexture = inUVRect.x <= 1e-4 && inUVRect.y <= 1e-4 && inUVRect.z >= 0.9999 && inUVRect.w >= 0.9999;
            if (fullTexture) {
                color = texture(art, clampedUV) * inTint;
            } else {
                float lod = max(0.0, log2(max(fw.x, fw.y)));
                vec2 mipHalf = (0.5 * exp2(lod)) / texSize;
                vec2 sMin = inUVRect.xy + mipHalf;
                vec2 sMax = max(sMin, inUVRect.xy + inUVRect.zw - mipHalf);
                color = textureLod(art, clamp(inUV, sMin, sMax), lod) * inTint;
            }
            return;
        }
        if (min(fw.x, fw.y) > 0.92) {
            vec2 i = floor(texel + 0.5);
            vec2 f = texel - i;
            vec2 sharp = (i + clamp(f / fw, -0.5, 0.5)) / texSize;
            vec2 uv = clamp(vec2(fw.x <= 1.0 ? sharp.x : inUV.x,
                                 fw.y <= 1.0 ? sharp.y : inUV.y),
                            minUV, maxUV);
            color = textureLod(art, uv, 0.0) * inTint;
            return;
        }

        vec2 p = texel - 0.5;
        ivec2 i0 = ivec2(floor(p));
        vec2 f = p - vec2(i0);
        vec4 c00, c10, c01, c11;
        vec4 beveled = sampleBeveled(texel, fw, minTexel, maxTexel, c00, c10, c01, c11);

        vec4 wx = catmullRomWeights(f.x);
        vec4 wy = catmullRomWeights(f.y);
        vec4 bicubic = vec4(0.0);
        for (int dy = -1; dy <= 2; dy++) {
            float ry = dy == -1 ? wy.x : (dy == 0 ? wy.y : (dy == 1 ? wy.z : wy.w));
            vec4 rowSum =
                fetchArtClamped(minTexel, maxTexel, i0 + ivec2(-1, dy)) * wx.x +
                fetchArtClamped(minTexel, maxTexel, i0 + ivec2(0, dy)) * wx.y +
                fetchArtClamped(minTexel, maxTexel, i0 + ivec2(1, dy)) * wx.z +
                fetchArtClamped(minTexel, maxTexel, i0 + ivec2(2, dy)) * wx.w;
            bicubic += rowSum * ry;
        }
        vec4 localMin = min(min(c00, c10), min(c01, c11));
        vec4 localMax = max(max(c00, c10), max(c01, c11));
        bicubic = clamp(bicubic, localMin, localMax);

        float contrast = max(max(colorDist(c00, c10), colorDist(c01, c11)),
                             max(colorDist(c00, c01), colorDist(c10, c11)));
        float edgeBlend = smoothstep(0.18, 0.42, contrast);
        color = mix(bicubic, beveled, edgeBlend) * inTint;
        return;
    }

    // Lot sprites with anti-aliasing disabled (preserve premultiplied alpha).
    if (inDepthInterval.w < 0.5) {
        ivec2 nearest = clamp(ivec2(clampedUV * texSize), minTexel, maxTexel);
        vec4 raw = texelFetch(art, nearest, 0);
        if (raw.a < (1.0 / 255.0)) discard;
        color = raw * inTint;
        return;
    }

    // Lot non-depth sprites & floor shadows with HQ subpixel + diagonal corner anti-aliasing.
    if (max(fw.x, fw.y) > 1.25) {
        color = texture(art, clampedUV) * inTint;
        return;
    }

    vec4 lc00, lc10, lc01, lc11;
    color = sampleBeveled(texel, fw, minTexel, maxTexel, lc00, lc10, lc01, lc11) * inTint;
}
