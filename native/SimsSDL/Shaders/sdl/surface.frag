#version 450
layout(set = 2, binding = 0) uniform sampler2D art;
layout(location = 0) in vec2 inUV;
layout(location = 1) flat in vec4 inTint;
layout(location = 0) out vec4 color;
void main() {
    vec4 sampled = texture(art, inUV);
    if (sampled.a < 0.01) discard;
    color = sampled * inTint;
}
