
#version 430 core
layout (local_size_x = 16, local_size_y = 16) in;

layout (rgba8, binding = 0, set = 3) uniform image2D imgOut;
layout (binding = 1, set = 2) uniform sampler2D MvTex;
layout (binding = 3, set = 2) uniform sampler2D CovTex;

layout (binding = 2) uniform params {
    float width;
    float height;
    float maxMag;
    float hasCov;
};

void main()
{
    ivec2 p = ivec2(gl_GlobalInvocationID.xy);
    int w = int(width);
    int h = int(height);
    if (p.x >= w || p.y >= h)
    {
        return;
    }

    vec2 uv = (vec2(p) + 0.5) / vec2(w, h);
    vec2 mv = textureLod(MvTex, uv, 0.0).rg;

    float mag = length(mv);
    float hue = atan(mv.y, mv.x) / 6.2831853 + 0.5;
    float v = clamp(mag / maxMag, 0.0, 1.0);

    vec3 c = clamp(abs(fract(hue + vec3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0, 0.0, 1.0);
    vec3 outc = v * c;

    if (hasCov > 1.5)
    {
        vec2 mvRaw = textureLod(CovTex, uv, 0.0).rg;

        outc *= 0.35;

        if (isnan(mvRaw.r) || isnan(mvRaw.g) || isinf(mvRaw.r) || isinf(mvRaw.g))
        {
            outc = vec3(1.0, 0.0, 0.0);
        }
        else if (mvRaw.r > -900.0)
        {
            vec2 mvA = mvRaw * 0.5 * vec2(w, h);
            float magA = length(mvA);

            if (magA > 0.02)
            {
                float hueA = atan(mvA.y, mvA.x) / 6.2831853 + 0.5;
                float vA = clamp(magA / (maxMag * 0.25), 0.0, 1.0);
                vec3 cA = clamp(abs(fract(hueA + vec3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0, 0.0, 1.0);

                outc = max(vA, 0.35) * cA;
            }
            else
            {
                outc = vec3(0.0, 0.15, 0.5);
            }
        }
    }
    else if (hasCov > 0.5 && textureLod(CovTex, uv, 0.0).r > 0.003)
    {
        outc = mix(outc, vec3(0.0, 1.0, 0.2), 0.6);
    }

    imageStore(imgOut, p, vec4(outc, 1.0));
}
