
#version 430 core
layout (local_size_x = 16, local_size_y = 16) in;

layout (rg16f, binding = 0, set = 3) uniform writeonly image2D imgOut;
layout (rg16f, binding = 1, set = 3) uniform readonly  image2D imgIn;

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
    uint sumMagFxF;
    uint sumMagSqFxF;
    uint outlierCountF;
};

float median9(float a[9])
{
    for (int i = 0; i < 5; ++i)
    {
        int m = i;
        for (int j = i + 1; j < 9; ++j)
        {
            if (a[j] < a[m])
            {
                m = j;
            }
        }
        float t = a[i];
        a[i] = a[m];
        a[m] = t;
    }
    return a[4];
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

    float xs[9];
    float ys[9];
    int k = 0;
    for (int dy = -1; dy <= 1; ++dy)
    {
        for (int dx = -1; dx <= 1; ++dx)
        {
            vec2 m = imageLoad(imgIn, clamp(p + ivec2(dx, dy), ivec2(0), maxc)).rg;
            xs[k] = m.x;
            ys[k] = m.y;
            ++k;
        }
    }

    vec2 mv = vec2(median9(xs), median9(ys));
    imageStore(imgOut, p, vec4(mv, 0.0, 0.0));

    if (metrics > 0.5 && (p.x & 3) == 0 && (p.y & 3) == 0)
    {
        float mag = length(mv);
        if (mag > 2.0)
        {
            atomicAdd(outlierCountF, 1u);
        }
        float magStat = min(mag, 4.0);
        atomicAdd(sumMagFxF, uint(magStat * 256.0 + 0.5));
        atomicAdd(sumMagSqFxF, uint(magStat * magStat * 1024.0 + 0.5));
    }
}
