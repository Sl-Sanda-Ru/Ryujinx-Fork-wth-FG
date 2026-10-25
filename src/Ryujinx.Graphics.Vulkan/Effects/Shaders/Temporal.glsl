
#version 430 core

layout (local_size_x = 16, local_size_y = 16) in;

layout (rgba8,   binding = 0, set = 3) uniform image2D imgOutput;
layout (rgba16f, binding = 1, set = 3) uniform image2D imgHistoryWrite;
layout (binding = 1, set = 2) uniform sampler2D Source;
layout (binding = 3, set = 2) uniform sampler2D HistoryRead;
layout (binding = 5, set = 2) uniform sampler2D Motion;

layout (binding = 2) uniform params {
    float width;
    float height;
    float blend;
    float hasHistory;
    float motionSign;
    float hasMotion;
    float clampGamma;
    float mvScale;
};

const float VEL_DECAY    = 0.85;
const float VEL_LOW      = 0.10;
const float VEL_HIGH     = 1.00;
const float STATIC_CLAMP = 2.00;
const float MOVING_BLEND = 0.92;
const float VEL_MAX      = 64.0;

vec3 RGBToYCoCg(vec3 c)
{
    return vec3(
         0.25 * c.r + 0.5 * c.g + 0.25 * c.b,
         0.5  * c.r             - 0.5  * c.b,
        -0.25 * c.r + 0.5 * c.g - 0.25 * c.b);
}

vec3 YCoCgToRGB(vec3 c)
{
    float y = c.x;
    float co = c.y;
    float cg = c.z;

    return vec3(y + co - cg, y + cg, y - co - cg);
}

void main()
{
    ivec2 loc = ivec2(gl_GlobalInvocationID.xy);

    int iw = int(width);
    int ih = int(height);

    if (loc.x >= iw || loc.y >= ih)
    {
        return;
    }

    vec4 current = texelFetch(Source, loc, 0);
    vec3 currentY = RGBToYCoCg(current.rgb);

    vec3 m1 = vec3(0.0);
    vec3 m2 = vec3(0.0);

    for (int dy = -1; dy <= 1; ++dy)
    {
        for (int dx = -1; dx <= 1; ++dx)
        {
            ivec2 p = clamp(loc + ivec2(dx, dy), ivec2(0), ivec2(iw - 1, ih - 1));
            vec3 c = RGBToYCoCg(texelFetch(Source, p, 0).rgb);
            m1 += c;
            m2 += c * c;
        }
    }

    vec3 mean = m1 / 9.0;
    vec3 sigma = sqrt(max(vec3(0.0), m2 / 9.0 - mean * mean));

    vec2 locUv = (vec2(loc) + 0.5) / vec2(width, height);
    vec2 mv = hasMotion > 0.5 ? texture(Motion, locUv).xy * mvScale : vec2(0.0);
    vec2 prevPos = vec2(loc) + 0.5 + motionSign * mv;
    vec2 uv = prevPos / vec2(width, height);

    bool onScreen = all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0)));
    bool valid = hasHistory > 0.5 && onScreen;

    vec4 histSample = texture(HistoryRead, uv);
    float prevVSmooth = valid ? histSample.a : 0.0;

    float vRaw = length(mv);
    float vSmooth = min(max(vRaw, prevVSmooth * VEL_DECAY), VEL_MAX);
    float motion = smoothstep(VEL_LOW, VEL_HIGH, vSmooth);

    float gamma = mix(clampGamma * STATIC_CLAMP, clampGamma, motion);
    float wBlend = mix(blend, blend * MOVING_BLEND, motion);

    vec3 boxMin = mean - gamma * sigma;
    vec3 boxMax = mean + gamma * sigma;
    vec3 historyY = clamp(RGBToYCoCg(histSample.rgb), boxMin, boxMax);

    float w = valid ? wBlend : 0.0;
    vec3 resultY = mix(currentY, historyY, w);
    vec3 resultRGB = YCoCgToRGB(resultY);

    imageStore(imgOutput, loc, vec4(resultRGB, current.a));
    imageStore(imgHistoryWrite, loc, vec4(resultRGB, vSmooth));
}
