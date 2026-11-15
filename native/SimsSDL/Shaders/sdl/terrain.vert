#version 450
#extension GL_GOOGLE_include_directive : require
#include "quad.glsl"

// Shared by the terrain and placement programs. quad.uvRect carries the terrain detail (x, 0–2) and zoom (y, 1–3) for
// the terrain program; placement leaves the default and reads only location 0.
struct TerrainVertex { vec2 position; vec2 grass; vec4 color; vec4 dark; };
layout(std430, set = 0, binding = 0) readonly buffer Vertices { TerrainVertex vertices[]; };

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec2 outCoordinate;
layout(location = 2) out vec4 outLight;
layout(location = 3) out vec4 outDark;
layout(location = 4) out vec4 outTint;
layout(location = 5) out float outGrass;
layout(location = 6) flat out vec2 outDetail;

void main() {
    TerrainVertex vertex = vertices[gl_VertexIndex];
    gl_Position = canvasPosition(quad.rect.xy + vertex.position * quad.rect.zw, 0.0);
    outColor = vertex.color * quad.tint;
    outCoordinate = vertex.position;
    outLight = vertex.color;
    outDark = vertex.dark;
    outTint = quad.tint;
    outGrass = vertex.grass.x;
    outDetail = quad.uvRect.xy;
}
