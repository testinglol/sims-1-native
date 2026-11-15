#version 450
layout(set = 2, binding = 0) uniform sampler2D art;
layout(set = 2, binding = 1) uniform sampler2D depthMap;
layout(location = 0) in vec2 inUV;
layout(location = 1) flat in vec4 inTint;
layout(location = 2) flat in vec4 inDepthInterval;
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

vec4 fetchArt(ivec2 dims, ivec2 c) {
    return texelFetch(art, clamp(c, ivec2(0), dims - 1), 0);
}

float fetchDepth(ivec2 dims, ivec2 c) {
    return texelFetch(depthMap, clamp(c, ivec2(0), dims - 1), 0).r;
}

bool shouldBevelCorner(
    vec4 K, vec4 A, vec4 B, vec4 O,
    ivec2 dims, ivec2 i0, ivec2 s1Off, ivec2 s2Off
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
        vec4 S1 = fetchArt(dims, i0 + s1Off);
        vec4 S2 = fetchArt(dims, i0 + s2Off);
        float dKS = min(colorDist(K, S1), colorDist(K, S2));
        return dKS < 0.24 && dKS * 1.8 < minDK;
    }
    return false;
}

void main() {
    ivec2 artDims = textureSize(art, 0);
    ivec2 depthDims = textureSize(depthMap, 0);
    vec2 texSize = vec2(artDims);
    vec2 halfTexel = 0.5 / texSize;
    vec2 clampedUV = clamp(inUV, halfTexel, 1.0 - halfTexel);
    vec2 texel = clampedUV * texSize;
    vec2 fw = max(fwidth(texel), vec2(1e-4));

    // Non-AA SPR2 mode: classic binary mask at raw 5-bit alpha >= 8/31.
    if (inDepthInterval.w < 0.5) {
        ivec2 nearest = clamp(ivec2(clamp(inUV, 0.0, 1.0) * texSize), ivec2(0), artDims - 1);
        vec4 raw = texelFetch(art, nearest, 0);
        if (raw.a < inDepthInterval.z) discard;
        if (raw.a > 0.0) {
            raw.rgb /= raw.a;
            raw.a = 1.0;
        }
        ivec2 depthCoord = clamp(ivec2(clamp(inUV, 0.0, 1.0) * vec2(depthDims)), ivec2(0), depthDims - 1);
        float d = texelFetch(depthMap, depthCoord, 0).r;
        if (d >= 1.0) discard;
        color = raw * inTint;
        gl_FragDepth = mix(inDepthInterval.x, inDepthInterval.y, d * 255.0 / 254.0);
        return;
    }

    vec2 p = texel - 0.5;
    ivec2 i0 = ivec2(floor(p));
    vec2 f = p - vec2(i0);

    vec4 c00 = fetchArt(artDims, i0);
    vec4 c10 = fetchArt(artDims, i0 + ivec2(1, 0));
    vec4 c01 = fetchArt(artDims, i0 + ivec2(0, 1));
    vec4 c11 = fetchArt(artDims, i0 + ivec2(1, 1));

    vec4 sampled;
    if (max(fw.x, fw.y) > 1.25) {
        sampled = texture(art, clampedUV);
    } else {
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
            if (shouldBevelCorner(c00, c10, c01, c11, artDims, i0, ivec2(-1, 1), ivec2(1, -1))) {
                float bevel = 1.0 - smoothstep(0.5 - aaWidth, 0.5 + aaWidth, f.x + f.y);
                float cut = max(0.0, w00 - bevel);
                w00 -= cut;
                w10 += 0.5 * cut;
                w01 += 0.5 * cut;
            }
        } else if ((1.0 - f.x) + (1.0 - f.y) < 0.5 + aaWidth) {
            if (shouldBevelCorner(c11, c10, c01, c00, artDims, i0, ivec2(2, 0), ivec2(0, 2))) {
                float bevel = 1.0 - smoothstep(0.5 - aaWidth, 0.5 + aaWidth, (1.0 - f.x) + (1.0 - f.y));
                float cut = max(0.0, w11 - bevel);
                w11 -= cut;
                w10 += 0.5 * cut;
                w01 += 0.5 * cut;
            }
        } else if ((1.0 - f.x) + f.y < 0.5 + aaWidth) {
            if (shouldBevelCorner(c10, c00, c11, c01, artDims, i0, ivec2(2, 1), ivec2(0, -1))) {
                float bevel = 1.0 - smoothstep(0.5 - aaWidth, 0.5 + aaWidth, (1.0 - f.x) + f.y);
                float cut = max(0.0, w10 - bevel);
                w10 -= cut;
                w00 += 0.5 * cut;
                w11 += 0.5 * cut;
            }
        } else if (f.x + (1.0 - f.y) < 0.5 + aaWidth) {
            if (shouldBevelCorner(c01, c00, c11, c10, artDims, i0, ivec2(-1, 0), ivec2(1, 2))) {
                float bevel = 1.0 - smoothstep(0.5 - aaWidth, 0.5 + aaWidth, f.x + (1.0 - f.y));
                float cut = max(0.0, w01 - bevel);
                w01 -= cut;
                w00 += 0.5 * cut;
                w11 += 0.5 * cut;
            }
        }
        sampled = c00 * w00 + c10 * w10 + c01 * w01 + c11 * w11;
    }

    // Two-pass compositing:
    // Pass 1 (inDepthInterval.w == 1.0): opaque core (alpha >= cutoff) writes clean solid color + foreground Z.
    // Pass 2 (inDepthInterval.w == 2.0): translucent & AA fringe (1/255 <= alpha < cutoff) blends without Z-write.
    bool isEdgePass = inDepthInterval.w > 1.5;
    if (isEdgePass) {
        if (sampled.a >= inDepthInterval.z || sampled.a < (1.0 / 255.0)) discard;
    } else {
        if (sampled.a < inDepthInterval.z) discard;
        sampled = vec4(sampled.rgb / max(sampled.a, 1e-4), 1.0);
    }

    // Reconstruct foreground depth strictly from valid foreground taps (never mixing z=1.0 from alpha==0 background).
    float d00 = fetchDepth(depthDims, i0);
    float d10 = fetchDepth(depthDims, i0 + ivec2(1, 0));
    float d01 = fetchDepth(depthDims, i0 + ivec2(0, 1));
    float d11 = fetchDepth(depthDims, i0 + ivec2(1, 1));

    float minValidD = 2.0;
    float maxValidD = -1.0;
    float minCoreD = 2.0;
    if (d00 < 1.0 && c00.a > 0.0) { minValidD = min(minValidD, d00); maxValidD = max(maxValidD, d00); if (c00.a >= 0.5) minCoreD = min(minCoreD, d00); }
    if (d10 < 1.0 && c10.a > 0.0) { minValidD = min(minValidD, d10); maxValidD = max(maxValidD, d10); if (c10.a >= 0.5) minCoreD = min(minCoreD, d10); }
    if (d01 < 1.0 && c01.a > 0.0) { minValidD = min(minValidD, d01); maxValidD = max(maxValidD, d01); if (c01.a >= 0.5) minCoreD = min(minCoreD, d01); }
    if (d11 < 1.0 && c11.a > 0.0) { minValidD = min(minValidD, d11); maxValidD = max(maxValidD, d11); if (c11.a >= 0.5) minCoreD = min(minCoreD, d11); }
    if (minValidD >= 1.0) discard;

    float resolvedDepth;
    if (isEdgePass) {
        resolvedDepth = minCoreD < 1.0 ? minCoreD : minValidD;
    } else {
        ivec2 nearCoord = clamp(ivec2(clamp(inUV, 0.0, 1.0) * vec2(depthDims)), ivec2(0), depthDims - 1);
        float nearD = texelFetch(depthMap, nearCoord, 0).r;
        if (maxValidD - minValidD <= (4.0 / 255.0)) {
            float dw00 = (d00 < 1.0 && c00.a > 0.0) ? (1.0 - f.x) * (1.0 - f.y) * c00.a : 0.0;
            float dw10 = (d10 < 1.0 && c10.a > 0.0) ? f.x * (1.0 - f.y) * c10.a : 0.0;
            float dw01 = (d01 < 1.0 && c01.a > 0.0) ? (1.0 - f.x) * f.y * c01.a : 0.0;
            float dw11 = (d11 < 1.0 && c11.a > 0.0) ? f.x * f.y * c11.a : 0.0;
            float dwSum = dw00 + dw10 + dw01 + dw11;
            resolvedDepth = dwSum > 1e-5 ? (d00 * dw00 + d10 * dw10 + d01 * dw01 + d11 * dw11) / dwSum : minValidD;
        } else {
            resolvedDepth = nearD < 1.0 ? nearD : (minCoreD < 1.0 ? minCoreD : minValidD);
        }
    }

    float fragZ = mix(inDepthInterval.x, inDepthInterval.y, resolvedDepth * 255.0 / 254.0);
    if (isEdgePass) {
        float stepBias = max(2e-6, 0.25 * abs(inDepthInterval.y - inDepthInterval.x) / 254.0);
        fragZ = max(0.0, fragZ - stepBias);
    }
    color = sampled * inTint;
    gl_FragDepth = fragZ;
}
