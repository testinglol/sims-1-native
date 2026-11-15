#version 450
// Sims.exe cTerrainRenderer, per art pixel. Colours are 128-shade ramps from the light to the dark grass colour of the
// pixel's colour row (FUN_0049e846); shade 0 is the fully lit ground the scene exports.
// - Dither (fDitherOffsets 0/8/0/6): each row of a 4-pixel column draws [s, s-8, s, s-6], alternating with
//   [s-6, s, s-8, s] on the next row (FUN_0049db98).
// - Grass blades (Terrain Detail medium/high, FUN_0049c48b): per tile, Min..Max blades interpolated by the grass value
//   between -73 and 85 (300..800 medium, 500..1200 high; FUN_0049cf80), a quarter of that at far zoom. Each blade is a
//   vertical run drawn upward, 1 pixel at far and medium zoom and 4 at near, less up to half at random, one shorter at
//   or below -73 (FUN_0049e134, FUN_0049d914); its shade is the ground's plus 30 +- 0..30 (FUN_0049e2f6).
// Blade placement uses a hash per art pixel instead of the original seeded pool, so the pattern differs pixel by pixel.
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
    ivec2 pixel = ivec2(floor(inCoordinate * float(1 << zoom) * 0.25));
    int column = pixel.x & 3;
    float shade = (pixel.y & 1) == 0 ? (column == 1 ? -8.0 : column == 3 ? -6.0 : 0.0)
                                     : (column == 0 ? -6.0 : column == 2 ? -8.0 : 0.0);
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
