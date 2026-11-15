#version 450
#extension GL_GOOGLE_include_directive : require
#include "quad.glsl"

layout(location = 0) out vec2 outUV;
layout(location = 1) flat out vec4 outTint;
layout(location = 2) flat out vec4 outDepthInterval;
layout(location = 3) flat out float outOpacity;
layout(location = 4) flat out vec4 outUVRect;

const vec2 corners[6] = vec2[6](vec2(0, 0), vec2(1, 0), vec2(0, 1), vec2(0, 1), vec2(1, 0), vec2(1, 1));

void main() {
    vec2 uv = corners[gl_VertexIndex];
    gl_Position = canvasPosition(quad.rect.xy + uv * quad.rect.zw, 0.0);
    vec2 source = vec2(quad.canvasPadding.z > 0.0 ? 1.0 - uv.x : uv.x, uv.y);
    outUV = quad.uvRect.xy + source * quad.uvRect.zw;
    outTint = quad.tint;
    outDepthInterval = quad.depthInterval;
    outOpacity = quad.canvasPadding.w;
    outUVRect = quad.uvRect;
}
