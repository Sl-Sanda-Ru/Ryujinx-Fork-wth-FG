#version 450

layout (local_size_x = 16, local_size_y = 16) in;

layout (rgba8, binding = 0, set = 3) uniform writeonly image2D imgDst;
layout (binding = 1, set = 2) uniform sampler2D TexColor;

layout (binding = 2) uniform Params {
    float width;
    float height;
    float scan;
    float thresh;
};

layout (std430, set = 1, binding = 0) buffer BorderStats {
    uint filled;
    uint pad;
};

void main()
{
    int w = int(width);
    int h = int(height);
    int S = int(scan);
    ivec2 p = ivec2(gl_GlobalInvocationID.xy);
    if (p.x >= w || p.y >= h)
    {
        return;
    }

    vec4 c = texelFetch(TexColor, p, 0);
    float t = thresh / 255.0;

    bool inBand = p.x < S || p.y < S || p.x >= w - S || p.y >= h - S;
    if (inBand && max(c.r, max(c.g, c.b)) <= t)
    {
        int dl = p.x;
        int dt = p.y;
        int dr = w - 1 - p.x;
        int db = h - 1 - p.y;
        int d = min(min(dl, dt), min(dr, db));
        ivec2 dir = d == dl ? ivec2(1, 0) :
                    d == dt ? ivec2(0, 1) :
                    d == dr ? ivec2(-1, 0) :
                              ivec2(0, -1);

        for (int j = d + 1; j < S; j++)
        {
            vec4 cq = texelFetch(TexColor, p + dir * (j - d), 0);
            if (max(cq.r, max(cq.g, cq.b)) > t)
            {
                c = cq;
                atomicAdd(filled, 1u);
                break;
            }
        }
    }

    imageStore(imgDst, p, c);
}
