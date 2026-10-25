
#version 430 core
layout (local_size_x = 16, local_size_y = 16) in;

layout (rg16f, binding = 0, set = 3) uniform image2D imgMotion;
layout (binding = 1, set = 2) uniform sampler2D CurrentColor;
layout (binding = 3, set = 2) uniform sampler2D PrevColor;

layout (binding = 2) uniform params {
    float width;
    float height;
    float maxMotion;
    float metrics;
    float dejitterX;
    float dejitterY;
};

layout (std430, set = 1, binding = 0) buffer SceneChange {
    uint changedCount;
    uint motionCount;
    uint statSamples;
    uint sumMagFx;
    uint sumMagSqFx;
    uint outlierCount;
};

float luma(vec3 c)
{
    return dot(c, vec3(0.299, 0.587, 0.114));
}

float lumaAt(ivec2 p, ivec2 maxc)
{
    ivec2 q = clamp(p, ivec2(0), maxc);
    return luma(texelFetch(CurrentColor, q, 0).rgb);
}

void main()
{
    ivec2 p = ivec2(gl_GlobalInvocationID.xy);
    int w = int(width);
    int h = int(height);
    if (p.x >= w || p.y >= h)
    {
        return;
    }

    ivec2 maxc = ivec2(w - 1, h - 1);

    float Sxx = 0.0, Sxy = 0.0, Syy = 0.0, Sxt = 0.0, Syt = 0.0, Stt = 0.0;
    const int R = 3;

    for (int dy = -R; dy <= R; ++dy)
    {
        for (int dx = -R; dx <= R; ++dx)
        {
            ivec2 q = clamp(p + ivec2(dx, dy), ivec2(0), maxc);

            float ix = 0.5 * (lumaAt(q + ivec2(1, 0), maxc) - lumaAt(q - ivec2(1, 0), maxc));
            float iy = 0.5 * (lumaAt(q + ivec2(0, 1), maxc) - lumaAt(q - ivec2(0, 1), maxc));
            float it = luma(texelFetch(CurrentColor, q, 0).rgb) - luma(texelFetch(PrevColor, q, 0).rgb);

            Sxx += ix * ix;
            Sxy += ix * iy;
            Syy += iy * iy;
            Sxt += ix * it;
            Syt += iy * it;
            Stt += it * it;
        }
    }

    float trace = Sxx + Syy;
    float reg = 1e-4 * trace + 1e-8;
    float Sxxr = Sxx + reg;
    float Syyr = Syy + reg;
    float detr = Sxxr * Syyr - Sxy * Sxy;

    float u = (-Sxt * Syyr + Syt * Sxy) / detr;
    float v = (-Syt * Sxxr + Sxt * Sxy) / detr;

    float det = Sxx * Syy - Sxy * Sxy;
    float disc = max(trace * trace - 4.0 * det, 0.0);
    float lambdaMin = 0.5 * (trace - sqrt(disc));
    float wStruct = clamp(lambdaMin / (0.15 * trace + 1e-6), 0.0, 1.0);

    float explained = -(u * Sxt + v * Syt);
    float wFit = clamp(explained / (Stt + 1e-6), 0.0, 1.0);

    float conf = wStruct * wFit;

    if (wFit < 0.4 && Stt > (Sxx + Syy) + 1e-5)
    {
        atomicAdd(changedCount, 1u);
    }

    vec2 mv = (clamp(-vec2(u, v), vec2(-maxMotion), vec2(maxMotion)) - vec2(dejitterX, dejitterY)) * conf;

    if (conf > 0.5 && dot(mv, mv) > 1.0)
    {
        atomicAdd(motionCount, 1u);
    }

    if (metrics > 0.5 && (p.x & 3) == 0 && (p.y & 3) == 0)
    {
        float mag = length(mv);
        if (mag > 2.0)
        {
            atomicAdd(outlierCount, 1u);
        }
        float magStat = min(mag, 4.0);
        atomicAdd(statSamples, 1u);
        atomicAdd(sumMagFx, uint(magStat * 256.0 + 0.5));
        atomicAdd(sumMagSqFx, uint(magStat * magStat * 1024.0 + 0.5));
    }

    imageStore(imgMotion, p, vec4(mv, 0.0, 0.0));
}
