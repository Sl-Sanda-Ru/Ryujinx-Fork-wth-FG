
#version 450 core
#extension GL_GOOGLE_include_directive : require

#define NIS_GLSL 1
#define NIS_SCALER 1
#define NIS_THREAD_GROUP_SIZE 256
#define NIS_BLOCK_WIDTH 32
#define NIS_BLOCK_HEIGHT 24

layout(local_size_x = NIS_THREAD_GROUP_SIZE, local_size_y = 1, local_size_z = 1) in;

layout(binding = 2) uniform cb
{
    float kDetectRatio;
    float kDetectThres;
    float kMinContrastRatio;
    float kRatioNorm;

    float kContrastBoost;
    float kEps;
    float kSharpStartY;
    float kSharpScaleY;

    float kSharpStrengthMin;
    float kSharpStrengthScale;
    float kSharpLimitMin;
    float kSharpLimitScale;

    float kScaleX;
    float kScaleY;

    float kDstNormX;
    float kDstNormY;
    float kSrcNormX;
    float kSrcNormY;

    uint kInputViewportOriginX;
    uint kInputViewportOriginY;
    uint kInputViewportWidth;
    uint kInputViewportHeight;

    uint kOutputViewportOriginX;
    uint kOutputViewportOriginY;
    uint kOutputViewportWidth;
    uint kOutputViewportHeight;

    float reserved0;
    float reserved1;
};

layout(binding = 1, set = 2) uniform sampler2D in_texture;
#ifdef PQ_OUTPUT
layout(rgb10_a2, binding = 0, set = 3) uniform image2D out_texture;
#else
layout(rgba16f, binding = 0, set = 3) uniform image2D out_texture;
#endif

layout(binding = 3, std140) uniform hdrParams
{
    float hdrPaperWhite;
    float hdrPeak;
    float hdrCurve;
    float hdrGamma;
    float hdrBlend;
    float hdrWhiten;
};

#include "nis_coef.glsl"

vec3 sdrToLinear(vec3 c, float g)
{
    return pow(max(c, vec3(0.0)), vec3(g));
}

vec3 sdrToHdr(vec3 srgb)
{
    vec3 lin = sdrToLinear(srgb, hdrGamma);
    float luma = dot(lin, vec3(0.2126, 0.7152, 0.0722));
    float maxc = max(max(lin.x, lin.y), lin.z);
    float hl = mix(luma, maxc, hdrBlend);
    float hlAmount = pow(clamp(hl, 0.0, 1.0), hdrCurve);
    float boost = mix(1.0, hdrPeak / hdrPaperWhite, hlAmount);
    vec3 outc = (lin * hdrPaperWhite) * boost;
    float t = hdrWhiten * hlAmount;
    float lumaOut = dot(outc, vec3(0.2126, 0.7152, 0.0722));
    return mix(outc, vec3(lumaOut), vec3(t));
}

#ifdef PQ_OUTPUT
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
#endif

vec4 NisStore(vec4 c)
{
#ifdef PQ_OUTPUT
    return vec4(scRgbToPq(sdrToHdr(c.rgb)), c.a);
#else
    return vec4(sdrToHdr(c.rgb), c.a);
#endif
}

#define NIS_OUTPUT_TRANSFORM(c) NisStore(c)

#include "NisScaler.h"

void main()
{
    NVScaler(uvec2(gl_WorkGroupID.xy), gl_LocalInvocationID.x);
}
