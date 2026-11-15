// Mirrors the Metal Quad: rect in pixels, canvas size + (flip, opacity), the uv sub-rectangle, the depth interval of
// the sprite's depth map and the lighting tint.
layout(std140, set = 1, binding = 0) uniform Quad {
    vec4 rect;
    vec4 canvasPadding;
    vec4 uvRect;
    vec4 depthInterval;
    vec4 tint;
} quad;

vec4 canvasPosition(vec2 pixel, float z) {
    vec2 canvas = quad.canvasPadding.xy;
    return vec4(pixel.x / canvas.x * 2.0 - 1.0, 1.0 - pixel.y / canvas.y * 2.0, z, 1.0);
}
