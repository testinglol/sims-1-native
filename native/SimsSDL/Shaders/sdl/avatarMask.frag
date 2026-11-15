#version 450
layout(set = 2, binding = 0) uniform sampler2D art;
layout(location = 0) in vec2 inUV;
layout(location = 0) out vec4 color;
void main() {
    if (texture(art, inUV).a < 0.01) discard;
    color = vec4(1.0);
}
