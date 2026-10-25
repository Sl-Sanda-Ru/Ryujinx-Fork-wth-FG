
#version 430 core
#extension GL_KHR_shader_subgroup_basic : require
#extension GL_KHR_shader_subgroup_arithmetic : require
#extension GL_KHR_shader_subgroup_vote : require
layout (local_size_x = 16, local_size_y = 16) in;

layout (rg16f, binding = 0, set = 3) uniform image2D imgReprojMv;
layout (r32f, binding = 1, set = 3) uniform image2D imgDepthSnap;
layout (r8, binding = 2, set = 3) uniform image2D imgBiasMask;
layout (rg16f, binding = 3, set = 3) uniform image2D imgRawMv;
layout (binding = 1, set = 2) uniform sampler2D DepthTex;
layout (binding = 3, set = 2) uniform sampler2D FlowMvTex;
layout (binding = 4, set = 2) uniform sampler2D PrevDepthTex;
layout (binding = 5, set = 2) uniform sampler2D CovTex;

layout (binding = 2) uniform params {
    mat4 invVpCurr;
    mat4 vpPrev;
    float width;
    float height;
    float maxMotion;
    float diagMode;
    float flipY;
    float zMinusOne;
    float zeroMv;
    float deadLo;
    float deadHi;
    float dynMask;
    float covCompose;
    float farRotStart;
    float edgeMode;
    float depthActiveX;
    float depthActiveY;
    float skyRot;
    float skyFlow;
    float devStats;
    float driftX;
    float driftY;
    float skyDepth;

    float intervalScale;
};

layout (std430, set = 1, binding = 0) buffer ReprojStats {
    uint samples;
    uint skyCount;
    uint voteXSame;
    uint voteXOpp;
    uint voteYSame;
    uint voteYOpp;
    uint sumFlowXFx;
    uint sumFlowYFx;
    uint sumReprojXFx;
    uint sumReprojYFx;
    uint dbgCenterD;
    uint dbgCenterZ;
    uint dbgCenterW;
    uint dbgCenterY;
    uint dbgCenterMvX;
    uint dbgCenterMvY;
    uint maskSampled;
    uint maskMasked;
    uint dbgCenterPrevD;
    uint dbgPad;
    uint skySumXFx;
    uint skySumYFx;
    int  skyFlowSumXFx;
    int  skyFlowSumYFx;
    uint skyD1Count;
    uint skyDbgDriftY;
    uint skyNbDiff2Fx;
    uint skyNbDiff16Fx;
    int  zoneFlowXFx[32];
    int  zoneFlowYFx[32];
    uint zoneCount[32];
    uint zoneDriftX[32];
    uint zoneDriftY[32];
    uint dynFlowXFx;
    uint dynFlowYFx;
    uint dynDiffXFx;
    uint dynDiffYFx;
    uint dynFlowAlive;
    uint dynPad;
    uint dynZoneCount[32];
    uint depthFarN;
    uint depthFarResFx;
    uint depthMidN;
    uint depthMidResFx;
    uint depthGridN;
    uint depthDegenN;
    uint depthBehindN;

    uint chkN;
    uint chkSplit;
    uint chkDiag;
    uint chkSky;
};

vec2 ZoneDrift(int zx, int zy)
{
    int zi = zy * 8 + zx;
    return vec2(uintBitsToFloat(zoneDriftX[zi]), uintBitsToFloat(zoneDriftY[zi]));
}

vec2 ZoneDriftBilinear(ivec2 pp, int w, int h)
{
    float fx = clamp(8.0 * float(pp.x) / float(w) - 0.5, 0.0, 7.0);
    float fy = clamp(4.0 * float(pp.y) / float(h) - 0.5, 0.0, 3.0);
    int x0 = int(fx);
    int y0 = int(fy);
    int x1 = min(x0 + 1, 7);
    int y1 = min(y0 + 1, 3);
    float tx = fx - float(x0);
    float ty = fy - float(y0);
    vec2 a = mix(ZoneDrift(x0, y0), ZoneDrift(x1, y0), tx);
    vec2 b = mix(ZoneDrift(x0, y1), ZoneDrift(x1, y1), tx);
    return mix(a, b, ty);
}

vec2 RotOnlyMv(vec2 uvIn, int w, int h)
{
    float ndcYr = flipY > 0.5 ? (1.0 - 2.0 * uvIn.y) : (uvIn.y * 2.0 - 1.0);
    float zA = zMinusOne > 0.5 ? -0.5 : 0.25;
    float zB = zMinusOne > 0.5 ? 0.5 : 0.75;
    vec4 wA = invVpCurr * vec4(uvIn.x * 2.0 - 1.0, ndcYr, zA, 1.0);
    vec4 wB = invVpCurr * vec4(uvIn.x * 2.0 - 1.0, ndcYr, zB, 1.0);

    if (abs(wA.w) <= 1e-9 || abs(wB.w) <= 1e-9)
    {
        return vec2(0.0);
    }

    vec3 rayDir = wB.xyz / wB.w - wA.xyz / wA.w;
    vec4 pc = vpPrev * vec4(rayDir, 0.0);

    if (pc.w < 0.0)
    {
        pc = -pc;
    }

    if (pc.w <= 1e-6)
    {
        return vec2(0.0);
    }

    vec2 pn = pc.xy / pc.w;
    float puy = flipY > 0.5 ? (0.5 - 0.5 * pn.y) : (pn.y * 0.5 + 0.5);

    return intervalScale *
        clamp((vec2(pn.x * 0.5 + 0.5, puy) - uvIn) * vec2(w, h), vec2(-maxMotion), vec2(maxMotion));
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

    vec2 uv = (vec2(p) + 0.5) / vec2(w, h);
    float d = textureLod(DepthTex, uv * vec2(depthActiveX, depthActiveY), 0.0).r;

    bool statPixel = (diagMode > 0.5 || devStats > 0.5) && (p.y % 32) < 2;

    if (statPixel)
    {
        uint rG = subgroupAdd(1u);
        if (subgroupElect())
        {
            atomicAdd(depthGridN, rG);
        }
    }

    if (p == ivec2(0))
    {
        skyDbgDriftY = floatBitsToUint(driftY);
    }

    bool centerPixel = p.x == w / 2 && p.y == h / 2;
    if (centerPixel)
    {
        dbgCenterD = floatBitsToUint(d);
    }

    imageStore(imgDepthSnap, p, vec4(d, 0.0, 0.0, 0.0));

    if (statPixel)
    {
        bool skyHere = d <= 0.0 || d >= skyDepth;
        if (skyHere || d >= 0.98)
        {
            ivec2 lim = ivec2(w - 1, h - 1);
            vec2 uvR = (vec2(min(p + ivec2(1, 0), lim)) + 0.5) / vec2(w, h);
            vec2 uvD = (vec2(min(p + ivec2(0, 1), lim)) + 0.5) / vec2(w, h);
            vec2 uvX = (vec2(min(p + ivec2(1, 1), lim)) + 0.5) / vec2(w, h);
            vec2 act = vec2(depthActiveX, depthActiveY);
            float dR = textureLod(DepthTex, uvR * act, 0.0).r;
            float dD = textureLod(DepthTex, uvD * act, 0.0).r;
            float dX = textureLod(DepthTex, uvX * act, 0.0).r;

            bool sR = dR <= 0.0 || dR >= skyDepth;
            bool sD = dD <= 0.0 || dD >= skyDepth;
            bool sX = dX <= 0.0 || dX >= skyDepth;

            bool split = (skyHere != sR) || (skyHere != sD);
            bool diag = (skyHere != sR) && (skyHere != sD) && (skyHere == sX);

            uint rN = subgroupAdd(1u);
            uint rS = subgroupAdd(split ? 1u : 0u);
            uint rD = subgroupAdd(diag ? 1u : 0u);
            uint rK = subgroupAdd(skyHere ? 1u : 0u);
            if (subgroupElect())
            {
                atomicAdd(chkN, rN);
                atomicAdd(chkSplit, rS);
                atomicAdd(chkDiag, rD);
                atomicAdd(chkSky, rK);
            }
        }
    }

    if (d <= 0.0 || d >= skyDepth)
    {
        bool useRot = farRotStart > 0.0 || (skyRot > 0.5 && d >= skyDepth);
        vec2 mvFar = useRot ? RotOnlyMv(uv, w, h) : vec2(0.0);

        bool flowTaken = false;
        if (skyFlow > 0.5 && d >= skyDepth)
        {
            vec2 flowSky = texelFetch(FlowMvTex, p, 0).rg;
            float flowMag = length(flowSky);
            if (flowMag > deadLo && flowMag <= maxMotion)
            {
                mvFar = flowSky;
                flowTaken = true;
            }
        }

        imageStore(imgRawMv, p, vec4(mvFar, 0.0, 0.0));
        mvFar *= smoothstep(deadLo, deadHi, length(mvFar));

        if (!flowTaken && d >= skyDepth)
        {
            mvFar += vec2(driftX, driftY) + ZoneDriftBilinear(p, w, h);
        }

        imageStore(imgReprojMv, p, vec4(mvFar, 0.0, 0.0));
        imageStore(imgBiasMask, p, vec4(0.0));
        if (statPixel)
        {
            bool d1 = d >= skyDepth;
            vec2 flowProbe = texelFetch(FlowMvTex, p, 0).rg;
            ivec2 sz = textureSize(FlowMvTex, 0);
            vec2 flowN2 = texelFetch(FlowMvTex, min(p + ivec2(2, 0), sz - 1), 0).rg;
            vec2 flowN16 = texelFetch(FlowMvTex, min(p + ivec2(16, 0), sz - 1), 0).rg;

            uint rCount = subgroupAdd(1u);
            uint rD1    = subgroupAdd(d1 ? 1u : 0u);
            uint rSumX  = subgroupAdd(uint(min(abs(mvFar.x), 4.0) * 1024.0));
            uint rSumY  = subgroupAdd(uint(min(abs(mvFar.y), 4.0) * 1024.0));
            int  rFlowX = subgroupAdd(int(clamp(flowProbe.x, -maxMotion, maxMotion) * 16.0));
            int  rFlowY = subgroupAdd(int(clamp(flowProbe.y, -maxMotion, maxMotion) * 16.0));
            uint rNb2   = subgroupAdd(uint(min(length(flowProbe - flowN2), maxMotion) * 16.0));
            uint rNb16  = subgroupAdd(uint(min(length(flowProbe - flowN16), maxMotion) * 16.0));

            if (subgroupElect())
            {
                atomicAdd(skyCount, rCount);
                atomicAdd(skyD1Count, rD1);
                atomicAdd(skySumXFx, rSumX);
                atomicAdd(skySumYFx, rSumY);
                atomicAdd(skyFlowSumXFx, rFlowX);
                atomicAdd(skyFlowSumYFx, rFlowY);
                atomicAdd(skyNbDiff2Fx, rNb2);
                atomicAdd(skyNbDiff16Fx, rNb16);
            }

            if (d1)
            {
                int zx = clamp(int(8.0 * float(p.x) / w), 0, 7);
                int zy = clamp(int(4.0 * float(p.y) / h), 0, 3);
                int zi = zy * 8 + zx;
                int zvx = int(clamp(flowProbe.x, -maxMotion, maxMotion) * 16.0);
                int zvy = int(clamp(flowProbe.y, -maxMotion, maxMotion) * 16.0);
                if (subgroupAllEqual(zi))
                {
                    int  rzx = subgroupAdd(zvx);
                    int  rzy = subgroupAdd(zvy);
                    uint rzc = subgroupAdd(1u);
                    if (subgroupElect())
                    {
                        atomicAdd(zoneFlowXFx[zi], rzx);
                        atomicAdd(zoneFlowYFx[zi], rzy);
                        atomicAdd(zoneCount[zi], rzc);
                    }
                }
                else
                {
                    atomicAdd(zoneFlowXFx[zi], zvx);
                    atomicAdd(zoneFlowYFx[zi], zvy);
                    atomicAdd(zoneCount[zi], 1u);
                }
            }
        }
        return;
    }

    float ndcY = flipY > 0.5 ? (1.0 - 2.0 * uv.y) : (uv.y * 2.0 - 1.0);

    float zNdc = zMinusOne > 0.5 ? (d * 2.0 - 1.0) : d;
    vec4 ndc = vec4(uv.x * 2.0 - 1.0, ndcY, zNdc, 1.0);
    vec4 worldH = invVpCurr * ndc;
    if (abs(worldH.w) < 1e-9)
    {
        vec2 mvInf = farRotStart > 0.0 ? RotOnlyMv(uv, w, h) : vec2(0.0);

        imageStore(imgRawMv, p, vec4(mvInf, 0.0, 0.0));
        mvInf *= smoothstep(deadLo, deadHi, length(mvInf));
        imageStore(imgReprojMv, p, vec4(mvInf, 0.0, 0.0));
        imageStore(imgBiasMask, p, vec4(0.0));

        if (statPixel)
        {
            uint rDg = subgroupAdd(1u);
            if (subgroupElect())
            {
                atomicAdd(depthDegenN, rDg);
            }
        }
        return;
    }
    vec4 world = vec4(worldH.xyz / worldH.w, 1.0);

    if (centerPixel)
    {
        dbgCenterZ = floatBitsToUint(zNdc);
        dbgCenterW = floatBitsToUint(worldH.w);
        dbgCenterY = floatBitsToUint(world.y);
    }

    vec4 prevClip = vpPrev * world;
    if (prevClip.w <= 1e-6)
    {
        vec2 mvBk = farRotStart > 0.0 ? RotOnlyMv(uv, w, h) : vec2(0.0);

        imageStore(imgRawMv, p, vec4(mvBk, 0.0, 0.0));
        mvBk *= smoothstep(deadLo, deadHi, length(mvBk));
        imageStore(imgReprojMv, p, vec4(mvBk, 0.0, 0.0));
        imageStore(imgBiasMask, p, vec4(1.0));

        if (statPixel)
        {
            uint rBk = subgroupAdd(1u);
            if (subgroupElect())
            {
                atomicAdd(depthBehindN, rBk);
            }
        }
        return;
    }
    vec2 prevNdc = prevClip.xy / prevClip.w;
    float prevUvY = flipY > 0.5 ? (0.5 - 0.5 * prevNdc.y) : (prevNdc.y * 0.5 + 0.5);
    vec2 prevUv = vec2(prevNdc.x * 0.5 + 0.5, prevUvY);

    bool edgeBias = false;

    float edgeOutAmt = 0.0;

    if (prevUv.x < 0.0 || prevUv.x > 1.0 || prevUv.y < 0.0 || prevUv.y > 1.0)
    {
        edgeOutAmt = max(max(max(-prevUv.x, prevUv.x - 1.0), max(-prevUv.y, prevUv.y - 1.0)), 0.0);

        if (edgeMode < 0.5)
        {
            imageStore(imgReprojMv, p, vec4(0.0));
            imageStore(imgRawMv, p, vec4(0.0));
            imageStore(imgBiasMask, p, vec4(1.0));
            return;
        }
        else if (edgeMode < 1.5)
        {
            prevUv = clamp(prevUv, vec2(0.0), vec2(1.0));
        }

        if (edgeMode >= 2.5)
        {
            edgeBias = true;
        }
    }

    float edgeBiasStrength = 1.0;
    if (edgeMode >= 5.5)
    {
        edgeBiasStrength = clamp(edgeOutAmt / 0.02, 0.0, 1.0);
    }
    else if (edgeMode >= 4.5)
    {
        edgeBiasStrength = 0.3;
    }
    else if (edgeMode >= 3.5)
    {
        edgeBiasStrength = 0.5;
    }

    bool dynPixel = false;
    if (dynMask > 0.5)
    {
        float predNdcZ = prevClip.z / prevClip.w;
        float invA = max(1.0 - predNdcZ, 1e-5);

        vec2 ts = 1.0 / vec2(w, h);
        float bestRel = 1e9;
        for (int i = 0; i < 5; i++)
        {
            vec2 offs = i == 0 ? vec2(0.0, 0.0) :
                        i == 1 ? vec2( ts.x, 0.0) :
                        i == 2 ? vec2(-ts.x, 0.0) :
                        i == 3 ? vec2(0.0,  ts.y) :
                                 vec2(0.0, -ts.y);

            float pd = textureLod(PrevDepthTex, prevUv + offs, 0.0).r;
            float pz = zMinusOne > 0.5 ? (pd * 2.0 - 1.0) : pd;
            float ib = max(1.0 - pz, 1e-5);
            bestRel = min(bestRel, abs(invA - ib) / max(invA, ib));

            if (i == 0 && centerPixel)
            {
                dbgCenterPrevD = floatBitsToUint(pd);
            }
        }

        dynPixel = bestRel > 0.15;

        if ((p.y % 32) < 2)
        {
            uint rS = subgroupAdd(1u);
            uint rM = subgroupAdd(dynPixel ? 1u : 0u);
            if (subgroupElect())
            {
                atomicAdd(maskSampled, rS);
                atomicAdd(maskMasked, rM);
            }
        }
    }

    vec2 mv = intervalScale * ((prevUv - uv) * vec2(w, h));

    vec2 mvPointPx = mv;

    if (farRotStart > 0.0)
    {
        float viewDist = abs(1.0 / worldH.w);
        float t = smoothstep(farRotStart, farRotStart * 2.0, viewDist);

        if (t > 0.0)
        {
            mv = mix(mv, RotOnlyMv(uv, w, h), t);
        }
    }

    mv = clamp(mv, vec2(-maxMotion), vec2(maxMotion));

    if (dynPixel && dynMask < 1.5)
    {
        mv = vec2(0.0);
    }

    imageStore(imgBiasMask, p, vec4(dynPixel ? 1.0 : (edgeBias ? edgeBiasStrength : 0.0)));

    if (dynMask > 1.5 && dynPixel && (p.y % 32) < 2)
    {
        vec2 dynFlow = texelFetch(FlowMvTex, p, 0).rg;
        uint aFx = uint(clamp(abs(dynFlow.x), 0.0, maxMotion) * 16.0);
        uint aFy = uint(clamp(abs(dynFlow.y), 0.0, maxMotion) * 16.0);
        uint aDx = uint(clamp(abs(dynFlow.x - mv.x), 0.0, maxMotion) * 16.0);
        uint aDy = uint(clamp(abs(dynFlow.y - mv.y), 0.0, maxMotion) * 16.0);
        uint alive = length(dynFlow) > deadLo ? 1u : 0u;

        uint rFx = subgroupAdd(aFx);
        uint rFy = subgroupAdd(aFy);
        uint rDx = subgroupAdd(aDx);
        uint rDy = subgroupAdd(aDy);
        uint rAl = subgroupAdd(alive);
        if (subgroupElect())
        {
            atomicAdd(dynFlowXFx, rFx);
            atomicAdd(dynFlowYFx, rFy);
            atomicAdd(dynDiffXFx, rDx);
            atomicAdd(dynDiffYFx, rDy);
            atomicAdd(dynFlowAlive, rAl);
        }

        int zx = clamp(int(8.0 * float(p.x) / w), 0, 7);
        int zy = clamp(int(4.0 * float(p.y) / h), 0, 3);
        int zi = zy * 8 + zx;
        if (subgroupAllEqual(zi))
        {
            uint rzc = subgroupAdd(1u);
            if (subgroupElect())
            {
                atomicAdd(dynZoneCount[zi], rzc);
            }
        }
        else
        {
            atomicAdd(dynZoneCount[zi], 1u);
        }
    }

    if (centerPixel)
    {
        dbgCenterMvX = floatBitsToUint(mv.x);
        dbgCenterMvY = floatBitsToUint(mv.y);
    }

    if (zeroMv > 0.5)
    {
        mv = vec2(0.0);
    }

    vec2 covPx = vec2(0.0);
    if (covCompose > 0.5 && zeroMv < 0.5)
    {
        vec2 dndc = textureLod(CovTex, uv, 0.0).rg;
        if (!isnan(dndc.x) && !isnan(dndc.y) && !isinf(dndc.x) && !isinf(dndc.y) &&
            dndc.x > -900.0 && abs(dndc.x) <= 1.0 && abs(dndc.y) <= 1.0)
        {
            covPx = vec2(-0.5 * dndc.x * float(w),
                         (flipY > 0.5 ? 0.5 : -0.5) * dndc.y * float(h));
        }
    }

    imageStore(imgRawMv, p, vec4(clamp(mv + covPx, vec2(-maxMotion), vec2(maxMotion)), 0.0, 0.0));

    mv *= smoothstep(deadLo, deadHi, length(mv));

    imageStore(imgReprojMv, p, vec4(clamp(mv + covPx, vec2(-maxMotion), vec2(maxMotion)), 0.0, 0.0));

    if (statPixel)
    {
        vec2 flow = texelFetch(FlowMvTex, p, 0).rg;

        bool vx = abs(flow.x) > 0.1 && abs(mv.x) > 0.5;
        bool vy = abs(flow.y) > 0.1 && abs(mv.y) > 0.5;
        uint rSamples = subgroupAdd(1u);
        uint rXSame   = subgroupAdd(vx && (flow.x > 0.0) == (mv.x > 0.0) ? 1u : 0u);
        uint rXOpp    = subgroupAdd(vx && (flow.x > 0.0) != (mv.x > 0.0) ? 1u : 0u);
        uint rYSame   = subgroupAdd(vy && (flow.y > 0.0) == (mv.y > 0.0) ? 1u : 0u);
        uint rYOpp    = subgroupAdd(vy && (flow.y > 0.0) != (mv.y > 0.0) ? 1u : 0u);
        uint rFlowX   = subgroupAdd(uint(min(abs(flow.x), 128.0) * 16.0));
        uint rFlowY   = subgroupAdd(uint(min(abs(flow.y), 128.0) * 16.0));
        uint rReprojX = subgroupAdd(uint(min(abs(mv.x),   maxMotion) * 16.0));
        uint rReprojY = subgroupAdd(uint(min(abs(mv.y),   maxMotion) * 16.0));

        bool farBin = d >= 0.98;
        bool midBin = d >= 0.50 && d < 0.90;
        vec2 rotRef = (farBin || midBin) ? RotOnlyMv(uv, w, h) : vec2(0.0);
        uint resFx  = uint(min(length(mvPointPx - rotRef), 64.0) * 4.0);
        uint rFarN   = subgroupAdd(farBin ? 1u : 0u);
        uint rFarRes = subgroupAdd(farBin ? resFx : 0u);
        uint rMidN   = subgroupAdd(midBin ? 1u : 0u);
        uint rMidRes = subgroupAdd(midBin ? resFx : 0u);

        if (subgroupElect())
        {
            atomicAdd(samples, rSamples);
            atomicAdd(voteXSame, rXSame);
            atomicAdd(voteXOpp, rXOpp);
            atomicAdd(voteYSame, rYSame);
            atomicAdd(voteYOpp, rYOpp);
            atomicAdd(sumFlowXFx, rFlowX);
            atomicAdd(sumFlowYFx, rFlowY);
            atomicAdd(sumReprojXFx, rReprojX);
            atomicAdd(sumReprojYFx, rReprojY);
            atomicAdd(depthFarN, rFarN);
            atomicAdd(depthFarResFx, rFarRes);
            atomicAdd(depthMidN, rMidN);
            atomicAdd(depthMidResFx, rMidRes);
        }
    }
}
