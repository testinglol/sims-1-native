#version 450
// Per-pixel terrain shader using 128-shade light-to-dark grass ramps (shade 0 = base lit ground),
// 4x2 alternating dither offsets ([0, -8, 0, -6] / [-6, 0, -8, 0]), and procedural per-pixel grass blades
// (300..800 medium, 500..1200 high, quartered at far zoom; heights 1 px far/medium, 1..4 px near).
layout(location = 1) in vec2 inCoordinate;
layout(location = 2) in vec4 inLight;
layout(location = 3) in vec4 inDark;
layout(location = 4) in vec4 inTint;
layout(location = 5) in float inGrass;
layout(location = 6) flat in vec2 inDetail;
layout(location = 0) out vec4 color;

uint hash(ivec2 point, uint salt) {
    uint value = uint(point.x) * 0x8da6b343u ^ uint(point.y) * 0xd8163841u ^ salt * 0xcb1ab31fu;
    value ^= value >> 16; value *= 0x7feb352du;
    value ^= value >> 15; value *= 0x846ca68bu;
    value ^= value >> 16;
    return value;
}

vec3 toLinear(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }

void main() {
    int detail = int(inDetail.x + 0.5);
    int zoom = clamp(int(inDetail.y + 0.5), 1, 3);
    vec2 artCoord = inCoordinate * float(1 << zoom) * 0.25;
    ivec2 pixel = ivec2(floor(artCoord));
    vec2 fw = fwidth(artCoord);
    float ditherWeight = 1.0 - smoothstep(0.95, 1.85, max(fw.x, fw.y));
    int column = pixel.x & 3;
    float shade = ((pixel.y & 1) == 0 ? (column == 1 ? -8.0 : column == 3 ? -6.0 : 0.0)
                                      : (column == 0 ? -6.0 : column == 2 ? -8.0 : 0.0)) * ditherWeight;
    if (detail > 0) {
        float value = floor(inGrass * 255.0 + 0.5) - 128.0;
        float low = detail == 1 ? 300.0 : 500.0;
        float high = detail == 1 ? 800.0 : 1200.0;
        float count = value >= 85.0 ? high : value <= -73.0 ? low : floor(low + (high - low) * (value + 73.0) / 158.0);
        if (zoom == 1) count = floor(count / 4.0);
        float tile = float(16 << zoom);
        float coverage = 1.0 - exp(-count / (tile * tile * 0.25));
        int tallest = zoom == 3 ? 4 : 1;
        for (int below = 0; below < tallest; below++) {
            ivec2 start = pixel + ivec2(0, below);
            if (float(hash(start, 1u) & 0xffffffu) >= coverage * 16777216.0) continue;
            uint random = hash(start, 2u);
            int height = tallest > 1 ? tallest - int(random % uint(tallest / 2)) : tallest;
            if (value <= -73.0) height = max(1, height - 1);
            if (below >= height) continue;
            uint spread = (random >> 8) % 31u;
            shade = ((random >> 16) & 1u) == 1u ? 30.0 - float(spread) : 30.0 + float(spread);
            break;
        }
    }
    vec3 ramp = clamp(inLight.rgb + (inDark.rgb - inLight.rgb) * shade / 128.0, 0.0, 1.0);
    color = vec4(toLinear(floor(ramp * 255.0 + 0.5) / 255.0) * inTint.rgb, 1.0);
}
