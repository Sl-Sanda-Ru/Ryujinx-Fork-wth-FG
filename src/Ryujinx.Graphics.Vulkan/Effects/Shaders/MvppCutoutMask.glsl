
#version 430 core
layout (local_size_x = 16, local_size_y = 16) in;

layout (r8, binding = 0, set = 3) uniform writeonly image2D imgMask;

layout (binding = 2) uniform params {
    float width;
    float height;
};

layout (std430, set = 1, binding = 0) readonly buffer Survivors {
    uint survivors[];
};

void main()
{
    ivec2 p = ivec2(gl_GlobalInvocationID.xy);

    if (p.x >= int(width) || p.y >= int(height))
    {
        return;
    }

    uint v = survivors[p.y * 4096 + p.x];

    imageStore(imgMask, p, vec4(v != 0u ? 1.0 : 0.0, 0.0, 0.0, 0.0));
}
