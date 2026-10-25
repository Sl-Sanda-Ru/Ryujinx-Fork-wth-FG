
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppHdrCensusProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_CENSUS") == "1";

        private const int MaxPairsLogged = 96;

        private static readonly Dictionary<(ulong Vs, ulong Fs), int> _pairCounts = new();
        private static readonly HashSet<(ulong, ulong)> _pairLogged = new();
        private static long _summaryMs;

        public static void OnDraw(GpuChannel channel, ref ThreedClassState state, int count, bool indexed)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                OnDrawImpl(channel, ref state, count, indexed);
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP census probe: disabled after unexpected error: {e}");
            }
        }

        private static void OnDrawImpl(GpuChannel channel, ref ThreedClassState state, int count, bool indexed)
        {
            ulong rtAddr = 0;
            int rtW = 0, rtH = 0;
            bool isHdrTarget = false;
            channel.TextureManager.MvppEnumerateRenderTargets((slot, rt) =>
            {
                if (!isHdrTarget && rt.Info.FormatInfo.Format == Format.R11G11B10Float && rt.Info.Width >= 1500)
                {
                    isHdrTarget = true;
                    rtAddr = rt.Range.GetSubRange(0).Address;
                    rtW = rt.Info.Width;
                    rtH = rt.Info.Height;
                }
            });

            if (!isHdrTarget)
            {
                return;
            }

            ulong vs = (ulong)state.ShaderState[1].Offset;
            ulong fs = (ulong)state.ShaderState[5].Offset;
            var key = (vs, fs);

            _pairCounts.TryGetValue(key, out int c);
            _pairCounts[key] = c + 1;

            long now = Environment.TickCount64;
            if (now - _summaryMs >= 3000)
            {
                _summaryMs = now;
                LogSummary(rtAddr);
            }

            if (_pairLogged.Contains(key) || _pairLogged.Count >= MaxPairsLogged)
            {
                return;
            }
            _pairLogged.Add(key);

            int vtx = indexed ? -1 : count;
            int idx = indexed ? count : 0;

            bool depthT = state.DepthTestEnable;
            bool depthW = state.DepthWriteEnable;
            bool blend = state.BlendEnable[0];
            int vpW = (int)MathF.Round(MathF.Abs(state.ViewportTransform[0].ScaleX) * 2f);

            int vbCount = 0;
            for (int i = 0; i < 16; i++)
            {
                if (state.VertexBufferState[i].Address.Pack() != 0)
                {
                    vbCount++;
                }
            }

            int texCount = 0;
            channel.TextureManager.MvppEnumerateGraphicsInputs(_ => texCount++);
            int imgCount = 0;
            channel.TextureManager.MvppEnumerateGraphicsImages((_, _) => imgCount++);
            int arrCount = channel.TextureManager.MvppGraphicsArrayBindingCount();

            int ubufCount = 0, ubufMaxSize = 0;
            for (int stage = 0; stage < Constants.ShaderStages; stage++)
            {
                uint mask = channel.BufferManager.GetGraphicsUniformBufferUseMask(stage);
                ubufCount += BitOperations.PopCount(mask);
                for (int slot = 0; mask != 0; slot++, mask >>= 1)
                {
                    if ((mask & 1) != 0)
                    {
                        ubufMaxSize = Math.Max(ubufMaxSize, channel.BufferManager.GetGraphicsUniformBufferSize(stage, slot));
                    }
                }
            }

            string cls;
            bool geomLike = depthW && (idx > 100 || vtx > 100) && vbCount > 0;
            bool fullscreen = (vtx >= 3 && vtx <= 6) || (idx >= 3 && idx <= 6);
            if (geomLike)
            {
                cls = "A:GEOMETRY(native scene 1600)";
            }
            else if (fullscreen && !depthT)
            {
                cls = texCount > 0
                    ? "B:FULLSCREEN samples HDR source"
                    : (ubufCount > 0 ? "C:FULLSCREEN buffer/bindless-fed (no texture)" : "?:FULLSCREEN no source visible");
            }
            else
            {
                cls = "OTHER";
            }

            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP-CENSUS PASS vs@0x{vs:X} fs@0x{fs:X} -> HDR@0x{rtAddr:X} {rtW}x{rtH} | " +
                $"vtx={vtx} idx={idx} vb={vbCount} vpW={vpW} depthT={depthT} depthW={depthW} blend={blend} | " +
                $"tex={texCount} arr={arrCount} img={imgCount} ubuf={ubufCount} ubufMax={ubufMaxSize} => {cls}");

            ulong sceneAddr = MvppCameraCapture.SceneColorAddress;
            bool readsLowerResHdr = false;
            channel.TextureManager.MvppEnumerateGraphicsInputsStage((stage, tex) =>
            {
                ulong a = tex.Range.GetSubRange(0).Address;
                Format f = tex.Info.FormatInfo.Format;
                int w = tex.Info.Width, h = tex.Info.Height;
                bool hdr = f == Format.R11G11B10Float || f == Format.R16G16B16A16Float;
                bool isScenePin = sceneAddr != 0 && a == sceneAddr;
                bool lowerResHdr = hdr && w < 1500;
                readsLowerResHdr |= lowerResHdr || isScenePin;

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP-CENSUS   IN[s{stage}] @0x{a:X} {f} {w}x{h} mip={tex.Info.Levels}" +
                    $"{(hdr ? " [HDR]" : "")}{(lowerResHdr ? " [LOWRES-HDR]" : "")}{(isScenePin ? " [=SCENE-PIN]" : "")}");
            });

            if (readsLowerResHdr)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP-CENSUS   ***INJECTION CANDIDATE (CAS1): vs@0x{vs:X} fs@0x{fs:X} reads a lower-res HDR source AND writes HDR 1600 -> replace with DLSS.***");
            }
        }

        private static void LogSummary(ulong rtAddr)
        {
            int distinct = _pairCounts.Count;
            int total = 0;
            (ulong, ulong) top = default;
            int topN = -1;
            foreach (var kv in _pairCounts)
            {
                total += kv.Value;
                if (kv.Value > topN)
                {
                    topN = kv.Value;
                    top = kv.Key;
                }
            }

            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP-CENSUS SUMMARY HDR@0x{rtAddr:X}: {distinct} distinct shader-pairs, {total} draws total; " +
                $"dominant fs@0x{top.Item2:X} with {topN} draws " +
                $"({(distinct <= 2 ? "few pairs => fullscreen/post (B/C)" : "many pairs => scene geometry (A)")}).");
        }
    }
}
