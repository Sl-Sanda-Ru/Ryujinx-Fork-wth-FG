
#version 430 core

layout (local_size_x = 16, local_size_y = 16) in;

layout (rgba16f, binding = 0, set = 3) uniform image2D imgOut;
layout (binding = 1, set = 2) uniform sampler2D Source;

layout (binding = 2) uniform params {
    float outWidth;
    float outHeight;
};

void main()
{
    ivec2 loc = ivec2(gl_GlobalInvocationID.xy);

    if (loc.x >= int(outWidth) || loc.y >= int(outHeight))
    {
        return;
    }

    vec2 uv = (vec2(loc) + 0.5) / vec2(outWidth, outHeight);
    imageStore(imgOut, loc, texture(Source, uv));
}
