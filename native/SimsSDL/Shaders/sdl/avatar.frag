#version 450
layout(set = 2, binding = 0) uniform sampler2D art;
layout(location = 0) in vec2 inUV;
layout(location = 1) flat in vec4 inTint;
layout(location = 2) in vec3 inNormal;
layout(location = 3) flat in float inTextureOnly;
layout(location = 0) out vec4 color;
void main() {
    vec4 sampled = texture(art, inUV);
    if (sampled.a < 0.01) discard;
    if (inTextureOnly > 0.5) { color = sampled; return; }
    float light = 0.65 + 0.35 * max(0.0, dot(normalize(inNormal), normalize(vec3(-0.4, 1.0, -0.6))));
    color = vec4(sampled.rgb * light * inTint.rgb, sampled.a);
}
