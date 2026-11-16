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
    if (inTextureOnly > 0.5) { color = vec4(sampled.rgb * inTint.rgb * inTint.a, sampled.a * inTint.a); return; }
    float nLenSq = dot(inNormal, inNormal);
    vec3 n = nLenSq > 1e-6 ? inNormal * inversesqrt(nLenSq) : vec3(0.0, 1.0, 0.0);
    float ndotl = max(0.0, dot(n, normalize(vec3(-0.4, 1.0, -0.6))));
    float hemi = 0.5 + 0.5 * n.y;
    float light = 0.60 + 0.33 * ndotl + 0.07 * hemi;
    color = vec4(sampled.rgb * light * inTint.rgb * inTint.a, sampled.a * inTint.a);
}
