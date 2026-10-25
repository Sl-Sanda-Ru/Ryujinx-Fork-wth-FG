
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppCompositeProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_COMPOSITEPROBE") == "1";

        private const int MaxFullDumps = 4;

        private static int _drawNo;
        private static int _fullDumps;
        private static int _following;
        private static long _lastSummaryMs;
        private static int _sceneReadsSinceSummary;

        public static void OnDraw(GpuChannel channel, ref ThreedClassState state)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                OnDrawImpl(channel, ref state);
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP composite probe: disabled after unexpected error: {e}");
            }
        }

        private static void OnDrawImpl(GpuChannel channel, ref ThreedClassState state)
        {
            _drawNo++;

            ulong sceneAddr = MvppCameraCapture.SceneColorAddress;

            bool samplesScene = false;
            if (sceneAddr != 0)
            {
                ulong target = sceneAddr;
                channel.TextureManager.MvppEnumerateGraphicsInputs(tex =>
                {
                    if (!samplesScene && tex.Range.GetSubRange(0).Address == target)
                    {
                        samplesScene = true;
                    }
                });
            }

            if (samplesScene)
            {
                _sceneReadsSinceSummary++;

                if (_fullDumps < MaxFullDumps)
                {
                    _fullDumps++;

                    bool blend = state.BlendEnable[0];
                    bool depth = state.DepthTestEnable;
                    string proj = ClassifyProj(channel);
                    ulong sh = (ulong)state.ShaderState[5].Offset;

                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-COMPOSITE draw#{_drawNo} SAMPLES scene@0x{sceneAddr:X} | blend={blend} depth={depth} proj={proj} fragShader@0x{sh:X}");

                    channel.TextureManager.MvppEnumerateRenderTargets((slot, rt) =>
                    {
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP-COMPOSITE   OUT slot{slot} @0x{rt.Range.GetSubRange(0).Address:X} {rt.Info.FormatInfo.Format} {rt.Info.Width}x{rt.Info.Height}");
                    });

                    _following = 20;
                }
            }
            else if (_following > 0)
            {
                _following--;

                ulong out0 = 0;
                Format of = Format.R8G8B8A8Unorm;
                int ow = 0, oh = 0;
                int outSlot = -1;
                channel.TextureManager.MvppEnumerateRenderTargets((slot, rt) =>
                {
                    if (outSlot < 0)
                    {
                        outSlot = slot;
                        out0 = rt.Range.GetSubRange(0).Address;
                        of = rt.Info.FormatInfo.Format;
                        ow = rt.Info.Width;
                        oh = rt.Info.Height;
                    }
                });

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP-COMPOSITE  +after draw#{_drawNo}: outSlot{outSlot}@0x{out0:X} {of} {ow}x{oh} " +
                    $"blend={(bool)state.BlendEnable[0]} depth={(bool)state.DepthTestEnable} proj={ClassifyProj(channel)}");
            }

            long now = Environment.TickCount64;
            if (now - _lastSummaryMs >= 2000)
            {
                if (_sceneReadsSinceSummary > 0)
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-COMPOSITE summary: {_sceneReadsSinceSummary} scene-sampling draw(s) in the last ~2s window");
                }

                _lastSummaryMs = now;
                _sceneReadsSinceSummary = 0;
            }
        }

        private static string ClassifyProj(GpuChannel channel)
        {
            try
            {
                ulong addr = channel.BufferManager.GetGraphicsUniformBufferAddress(0, 5);
                int size = channel.BufferManager.GetGraphicsUniformBufferSize(0, 5);
                if (addr == 0 || addr == ulong.MaxValue || size < 64)
                {
                    return "n/a";
                }

                ReadOnlySpan<float> d = MemoryMarshal.Cast<byte, float>(channel.MemoryManager.Physical.GetSpan(addr, 64));
                for (int i = 0; i < 16; i++)
                {
                    if (!float.IsFinite(d[i]))
                    {
                        return "n/a";
                    }
                }

                float m34 = d[11], m44 = d[15];
                if (MathF.Abs(m44 - 1f) < 0.01f && MathF.Abs(m34) < 0.01f)
                {
                    return "ortho";
                }
                if (MathF.Abs(m34) > 0.5f)
                {
                    return "persp";
                }

                return "?";
            }
            catch
            {
                return "n/a";
            }
        }
    }
}
