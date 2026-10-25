
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppDlssUpscale
    {
        private static readonly bool _enabled = MvppInjectState.Enabled;

        private static readonly bool _quiet = System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_INJECT_QUIET") == "1";

        private static long _statusMs;
        private static long _statusSig = -1;
        private static int _diagCount;

        private static bool IsHdrColor(Format f) =>
            f == Format.R11G11B10Float ||
            f == Format.R16G16B16A16Float;

        public static bool Decide(GpuChannel channel, ref ThreedClassState state, int count, bool indexed,
            out ITexture src, out ITexture dst, out ITexture depth)
        {
            src = null;
            dst = null;
            depth = null;

            if (!_enabled)
            {
                return false;
            }

            bool fullscreen = indexed && (count == 3 || count == 6);
            if (!fullscreen || state.DepthTestEnable || state.BlendEnable[0])
            {
                return false;
            }

            ulong dstAddr = 0;
            Format dstFmt = default;
            int dw = 0, dh = 0;
            bool haveDst = false;
            ITexture dstHost = null;
            channel.TextureManager.MvppEnumerateRenderTargets((slot, rt) =>
            {
                if (slot == 0)
                {
                    haveDst = true;
                    dstAddr = rt.Range.GetSubRange(0).Address;
                    dstFmt = rt.Info.FormatInfo.Format;
                    dw = rt.Info.Width;
                    dh = rt.Info.Height;
                    dstHost = rt.HostTexture;
                }
            });

            int texCount = 0;
            ulong srcAddr = 0;
            Format srcFmt = default;
            int sw = 0, sh = 0;
            ITexture srcHost = null;
            channel.TextureManager.MvppEnumerateGraphicsInputs(t =>
            {
                if (texCount == 0)
                {
                    srcAddr = t.Range.GetSubRange(0).Address;
                    srcFmt = t.Info.FormatInfo.Format;
                    sw = t.Info.Width;
                    sh = t.Info.Height;
                    srcHost = t.HostTexture;
                }

                texCount++;
            });

            bool detected =
                texCount == 1 &&
                haveDst && srcHost != null && dstHost != null &&
                IsHdrColor(srcFmt) && IsHdrColor(dstFmt) &&
                sw > 0 && sh > 0 && dw > sw && dh > sh;

            if (!detected)
            {
                return false;
            }

            ulong sceneAddr = MvppCameraCapture.SceneColorAddress;
            bool inject = sceneAddr != 0 && srcAddr == sceneAddr;

            if (inject)
            {
                src = srcHost;
                dst = dstHost;
                depth = MvppCameraCapture.SceneDepthHost;

                if (_diagCount < 4)
                {
                    _diagCount++;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-INJECT-DIAG capture: frame={MvppDestProbe.Frame} " +
                        $"srcGuest={sw}x{sh} srcHost={srcHost.Width}x{srcHost.Height} srcAddr=0x{srcAddr:X} sceneAddr=0x{sceneAddr:X} " +
                        $"dstGuest={dw}x{dh} dstHost={dstHost.Width}x{dstHost.Height} dstAddr=0x{dstAddr:X} dstFmt={dstFmt}");

                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-INJECT-DEPTH candidate: depthAddr=0x{MvppCameraCapture.SceneDepthAddress:X} " +
                        $"depthFmt={MvppCameraCapture.SceneDepthFormat} " +
                        $"depthHost={MvppCameraCapture.SceneDepthHostWidth}x{MvppCameraCapture.SceneDepthHostHeight} " +
                        $"depthFrameId={DlssJitterState.SceneDepthFrameId} colorFrameId={DlssJitterState.SceneColorFrameId} " +
                        $"depthColorSameFrame={DlssJitterState.SceneDepthFrameId == DlssJitterState.SceneColorFrameId} " +
                        $"depthMatchesSrcHost={(MvppCameraCapture.SceneDepthHostWidth == srcHost.Width && MvppCameraCapture.SceneDepthHostHeight == srcHost.Height)}");
                }
            }

            Status($"detect src guest {sw}x{sh} {srcFmt} -> dst guest {dw}x{dh} host {dstHost.Width}x{dstHost.Height} {dstFmt} " +
                   $"sceneCoherent={inject} inject={inject}");

            return inject;
        }

        private static void Status(string msg)
        {
            if (_quiet) { return; }
            long sig = (uint)msg.GetHashCode();
            long now = Environment.TickCount64;
            if (sig != _statusSig || now - _statusMs >= 3000)
            {
                _statusSig = sig;
                _statusMs = now;
                Logger.Info?.Print(LogClass.Gpu, $"MVPP-INJECT-C: {msg}");
            }
        }
    }
}
