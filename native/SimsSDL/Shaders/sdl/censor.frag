#version 450
layout(set = 2, binding = 0) uniform sampler2D lotColor;
layout(set = 2, binding = 1) uniform sampler2D depthMap;
layout(location = 0) in vec2 inUV;
layout(location = 1) flat in vec4 inTint;
layout(location = 2) flat in vec4 inDepthInterval;
layout(location = 3) flat in float inOpacity;
layout(location = 4) flat in vec4 inUVRect;
layout(location = 0) out vec4 color;
void main() {
    ivec2 dims = textureSize(lotColor, 0);
    ivec2 fragCoord = clamp(ivec2(gl_FragCoord.xy), ivec2(0), dims - 1);
    float fragDepth = texelFetch(depthMap, fragCoord, 0).r;
    if (fragDepth < inDepthInterval.x) discard;
    float block = max(2.0, inDepthInterval.y);
    ivec2 cell = ivec2(floor(gl_FragCoord.xy / block));
    ivec2 sampleCoord = clamp(ivec2((vec2(cell) + 0.5) * block), ivec2(0), dims - 1);
    if (texelFetch(depthMap, sampleCoord, 0).r < inDepthInterval.x) sampleCoord = fragCoord;
    vec4 sampled = texelFetch(lotColor, sampleCoord, 0);
    uint h = uint(cell.x * 73856093 ^ cell.y * 19349663);
    h = (h ^ (h >> 13u)) * 1274126177u;
    float lum = max(sampled.r, max(sampled.g, sampled.b));
    float ditherScale = mix(0.25, 1.0, smoothstep(0.02, 0.25, lum));
    float dither = (float(h & 15u) - 8.0) / 255.0 * ditherScale;
    color = vec4(clamp(sampled.rgb + dither, 0.0, 1.0), 1.0);
}
