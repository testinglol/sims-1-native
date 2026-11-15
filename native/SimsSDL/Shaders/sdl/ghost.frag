#version 450
layout(set = 2, binding = 0) uniform sampler2D art;
layout(location = 0) in vec2 inUV;
layout(location = 2) flat in vec4 inDepthInterval;
layout(location = 3) flat in float inOpacity;
layout(location = 0) out vec4 color;
void main() {
    vec4 sampled = texture(art, inUV);
    if (inDepthInterval.w < 0.5 && sampled.a > 0.0) {
        sampled.rgb /= sampled.a;
        sampled.a = 1.0;
    }
    color = sampled * inOpacity;
}
