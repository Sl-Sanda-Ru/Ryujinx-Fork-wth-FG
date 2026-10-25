
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppInjectGate
    {
        private static readonly bool _verbose =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_INJECTGATE") == "1";
        private static bool _enabled = _verbose || MvppInjectState.Enabled;

        private const ulong TargetFs = 0x559E30;
        private const int MaxFullLogged = 40;

        private static long _total;
        private static long _candidates;
        private static long _nearMiss;
        private static int _fullLogged;
        private static long _summaryMs;
        private static readonly HashSet<(ulong, ulong)> _candidatePairs = new();

        private static long _relaxedCandidates;
        private static readonly HashSet<(ulong, ulong)> _relaxedPairs = new();

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
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP inject gate: disabled after unexpected error: {e}");
            }
        }

        private static void OnDrawImpl(GpuChannel channel, ref ThreedClassState state, int count, bool indexed)
        {
            _total++;

            bool idx3 = indexed && count == 3;
            bool depthOff = !state.DepthTestEnable;
            bool blendOff = !state.BlendEnable[0];

            ulong dstAddr = 0;
            Format dstFmt = default;
            int dw = 0, dh = 0;
            bool haveDst = false;
            channel.TextureManager.MvppEnumerateRenderTargets((slot, rt) =>
            {
                if (slot == 0)
                {
                    haveDst = true;
                    dstAddr = rt.Range.GetSubRange(0).Address;
                    dstFmt = rt.Info.FormatInfo.Format;
                    dw = rt.Info.Width;
                    dh = rt.Info.Height;
                }
            });

            int texCount = 0;
            ulong srcAddr = 0;
            Format srcFmt = default;
            int sw = 0, sh = 0;
            channel.TextureManager.MvppEnumerateGraphicsInputs(t =>
            {
                if (texCount == 0)
                {
                    srcAddr = t.Range.GetSubRange(0).Address;
                    srcFmt = t.Info.FormatInfo.Format;
                    sw = t.Info.Width;
                    sh = t.Info.Height;
                }
                texCount++;
            });

            ulong sceneAddr = MvppCameraCapture.SceneColorAddress;

            bool c1 = texCount == 1;
            bool c2 = sceneAddr != 0 && srcAddr == sceneAddr;
            bool c3 = srcFmt == Format.R11G11B10Float;
            bool c4 = haveDst && dstFmt == Format.R11G11B10Float && dw >= 1500 && dw <= 1700;
            bool c5 = sw > 0 && dw > 0 && sw < dw;
            bool c6 = idx3 && depthOff && blendOff;

            bool gate = c1 && c2 && c3 && c4 && c5 && c6;
            bool relaxed = c1 && c3 && c4 && c5 && c6;

            ulong vs = (ulong)state.ShaderState[1].Offset;
            ulong fs = (ulong)state.ShaderState[5].Offset;

            if (relaxed)
            {
                _relaxedCandidates++;
                _relaxedPairs.Add((vs, fs));
            }

            if (gate)
            {
                _candidates++;
                _candidatePairs.Add((vs, fs));

                if (_verbose && _fullLogged < MaxFullLogged)
                {
                    _fullLogged++;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-INJECT CANDIDATE: src=@0x{srcAddr:X} {srcFmt} {sw}x{sh} dst=@0x{dstAddr:X} {dstFmt} {dw}x{dh} " +
                        $"shader=VS@0x{vs:X}/FS@0x{fs:X} fsMatch={(fs == TargetFs)} criteria=1,2,3,4,5,6 PASS");
                }
            }
            else if (c2)
            {
                _nearMiss++;
                if (_verbose && _nearMiss <= 30)
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-INJECT near-miss (reads scene, gate fail): src=@0x{srcAddr:X} {srcFmt} {sw}x{sh} " +
                        $"dst=@0x{dstAddr:X} {dstFmt} {dw}x{dh} FS@0x{fs:X} | " +
                        $"c1={c1} c3={c3} c4={c4} c5={c5} c6={c6} (idx3={idx3} depthOff={depthOff} blendOff={blendOff})");
                }
            }

            long now = Environment.TickCount64;
            if (_verbose && now - _summaryMs >= 3000)
            {
                _summaryMs = now;
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP-INJECT SUMMARY: frame={MvppDestProbe.Frame} totalDrawImpl={_total} " +
                    $"strict(candidates={_candidates} pairs={_candidatePairs.Count}) " +
                    $"RELAXED(candidates={_relaxedCandidates} pairs={_relaxedPairs.Count}) nearMiss={_nearMiss}");

                if (_relaxedPairs.Count > 1)
                {
                    foreach ((ulong pvs, ulong pfs) in _relaxedPairs)
                    {
                        Logger.Info?.Print(LogClass.Gpu, $"MVPP-INJECT   relaxed-pair VS@0x{pvs:X}/FS@0x{pfs:X}");
                    }
                }
            }
        }
    }
}
