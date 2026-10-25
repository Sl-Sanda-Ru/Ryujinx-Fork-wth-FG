#version 450 core

layout (binding = 0, set = 2) uniform sampler2D tex;

layout (std140, binding = 2) uniform hdr_params
{
    vec4 hdr_params_data;
    float hdr_blend_data;
    float hdr_whiten_data;
    float hdr_pq_data;
    float hdr_pad_data;
};

layout (location = 0) in vec2 tex_coord;
layout (location = 0) out vec4 colour;

vec3 sdrToLinear(vec3 c, float gamma)
{
    return pow(max(c, vec3(0.0)), vec3(gamma));
}

vec3 sdrToHdr(vec3 srgb)
{
    float gamma = hdr_params_data.w;
    vec3 lin = sdrToLinear(srgb, gamma);
    float paperWhite = hdr_params_data.x;
    float peak = hdr_params_data.y;
    float curve = hdr_params_data.z;
    float luma = dot(lin, vec3(0.2126, 0.7152, 0.0722));
    float maxc = max(max(lin.r, lin.g), lin.b);
    float hl = mix(luma, maxc, hdr_blend_data);
    float hlAmount = pow(clamp(hl, 0.0, 1.0), curve);
    float boost = mix(1.0, peak / paperWhite, hlAmount);
    vec3 outc = lin * paperWhite * boost;
    float t = hdr_whiten_data * hlAmount;
    float lumaOut = dot(outc, vec3(0.2126, 0.7152, 0.0722));
    return mix(outc, vec3(lumaOut), t);
}

vec3 scRgbToPq(vec3 scrgb)
{
    const mat3 rec709To2020 = mat3(
        0.6274, 0.0691, 0.0164,
        0.3293, 0.9195, 0.0880,
        0.0433, 0.0114, 0.8956);
    vec3 lin = max(rec709To2020 * scrgb, vec3(0.0));
    vec3 E = clamp(lin * (80.0 / 10000.0), vec3(0.0), vec3(1.0));
    const float m1 = 0.1593017578125;
    const float m2 = 78.84375;
    const float c1 = 0.8359375;
    const float c2 = 18.8515625;
    const float c3 = 18.6875;
    vec3 Em1 = pow(E, vec3(m1));
    return pow((c1 + c2 * Em1) / (1.0 + c3 * Em1), vec3(m2));
}

void main()
{
    vec3 c = sdrToHdr(texture(tex, tex_coord).rgb);
    if (hdr_pq_data > 0.5)
    {
        c = scRgbToPq(c);
    }
    colour = vec4(c, 1.0f);
}
