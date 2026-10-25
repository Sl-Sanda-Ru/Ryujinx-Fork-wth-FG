
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
layout(rgba8, binding = 0, set = 3) uniform image2D out_texture;

#include "nis_coef.glsl"

#include "NisScaler.h"

void main()
{
    NVScaler(uvec2(gl_WorkGroupID.xy), gl_LocalInvocationID.x);
}
