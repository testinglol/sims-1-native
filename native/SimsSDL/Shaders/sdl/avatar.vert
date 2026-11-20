#version 450
#extension GL_GOOGLE_include_directive : require
#include "quad.glsl"

struct AvatarVertex { vec4 position; vec2 uv; vec2 padding; vec4 normal; };
layout(std430, set = 0, binding = 0) readonly buffer Vertices { AvatarVertex vertices[]; };

layout(location = 0) out vec2 outUV;
layout(location = 1) flat out vec4 outTint;
layout(location = 2) out vec3 outNormal;
// quad.uvRect.x: 0 = avatar lit, 1 = unlit (Character Detail Low), 2 = roof slope shading.
layout(location = 3) flat out float outTextureOnly;
layout(location = 4) flat out vec3 outSunDir;

void main() {
    uint baseVertex = floatBitsToUint(quad.depthInterval.x);
    AvatarVertex vertex = vertices[baseVertex + uint(gl_VertexIndex)];
    gl_Position = canvasPosition(quad.rect.xy + vertex.position.xy * quad.rect.zw, vertex.position.z);
    outUV = vertex.uv;
    outTint = quad.tint;
    outNormal = vertex.normal.xyz;
    outTextureOnly = quad.uvRect.x;
    outSunDir = quad.uvRect.yzw;
}
