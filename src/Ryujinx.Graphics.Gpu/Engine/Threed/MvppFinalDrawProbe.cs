
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppFinalDrawProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_FINALPROBE") == "1";

        private const int MaxPerRt = 40;
        private const int MaxTargets = 24;

        private static readonly HashSet<ulong> _targets = new();
        private static readonly Dictionary<ulong, int> _loggedPerRt = new();

        private static int _texCount;
        private const int MaxTexLogged = 40;

        public static void OnDrawTexture(GpuChannel channel, Image.Texture src)
        {
            if (!_enabled || src == null)
            {
                return;
            }

            try
            {
                ulong dstAddr = 0;
                int dstSlot = -1;
                channel.TextureManager.MvppEnumerateRenderTargets((slot, rt) =>
                {
                    if (dstSlot < 0)
                    {
                        dstSlot = slot;
                        dstAddr = rt.Range.GetSubRange(0).Address;
                    }
                });

                bool dstTraced = dstSlot >= 0 && (MvppDestProbe.IsPresentedAddress(dstAddr) || _targets.Contains(dstAddr));
                if (_texCount >= MaxTexLogged && !dstTraced)
                {
                    return;
                }
                if (_texCount < MaxTexLogged)
                {
                    _texCount++;
                }

                ulong sAddr = src.Range.GetSubRange(0).Address;
                Format sFmt = src.Info.FormatInfo.Format;
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP-DRAWTEX src@0x{sAddr:X} {sFmt} {src.Info.Width}x{src.Info.Height} -> dst@0x{dstAddr:X} traced={dstTraced}");
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP drawtex probe: disabled after unexpected error: {e}");
            }
        }

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
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP final-draw probe: disabled after unexpected error: {e}");
            }
        }

        private static void OnDrawImpl(GpuChannel channel, ref ThreedClassState state)
        {
            ulong rtAddr = 0;
            int rtSlot = -1;
            Format rtFmt = default;
            int rw = 0, rh = 0;
            bool matched = false;
            bool isFinal = false;

            channel.TextureManager.MvppEnumerateRenderTargets((slot, rt) =>
            {
                if (matched)
                {
                    return;
                }

                ulong a = rt.Range.GetSubRange(0).Address;
                bool pres = MvppDestProbe.IsPresentedAddress(a);
                if (pres || _targets.Contains(a))
                {
                    matched = true;
                    isFinal = pres;
                    rtSlot = slot;
                    rtAddr = a;
                    rtFmt = rt.Info.FormatInfo.Format;
                    rw = rt.Info.Width;
                    rh = rt.Info.Height;
                }
            });

            if (!matched)
            {
                return;
            }

            _loggedPerRt.TryGetValue(rtAddr, out int n);
            if (n >= MaxPerRt)
            {
                return;
            }
            _loggedPerRt[rtAddr] = n + 1;

            ulong fragSh = (ulong)state.ShaderState[5].Offset;
            int arrays = channel.TextureManager.MvppGraphicsArrayBindingCount();

            Image.Texture depth = channel.TextureManager.RenderTargetDepthStencil;
            string depthStr = depth != null
                ? $"@0x{depth.Range.GetSubRange(0).Address:X} {depth.Info.FormatInfo.Format} {depth.Info.Width}x{depth.Info.Height}"
                : "none";

            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP-CHAIN draw -> RT slot{rtSlot} {rtFmt} {rw}x{rh} @0x{rtAddr:X} ({(isFinal ? "FINAL" : "intermediate")}) " +
                $"fragShader@0x{fragSh:X} depth={depthStr} arrays={arrays}");

            ulong sceneAddr = MvppCameraCapture.SceneColorAddress;

            channel.TextureManager.MvppEnumerateGraphicsInputs(tex =>
            {
                ulong a = tex.Range.GetSubRange(0).Address;
                Format f = tex.Info.FormatInfo.Format;
                int w = tex.Info.Width, h = tex.Info.Height;
                bool hdr = f == Format.R11G11B10Float || f == Format.R16G16B16A16Float;
                bool isScene = sceneAddr != 0 && a == sceneAddr;

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP-CHAIN   IN @0x{a:X} {f} {w}x{h}{(hdr ? " [HDR]" : "")}{(isScene ? " [=SCENE-PIN]" : "")}");

                if (hdr && !isScene)
                {
                    if (_targets.Count < MaxTargets)
                    {
                        _targets.Add(a);
                    }
                    MvppDestProbe.AddTracked(a);
                }
            });

            channel.TextureManager.MvppEnumerateGraphicsImages((tex, isStore) =>
            {
                ulong a = tex.Range.GetSubRange(0).Address;
                Format f = tex.Info.FormatInfo.Format;
                bool hdr = f == Format.R11G11B10Float || f == Format.R16G16B16A16Float;
                bool isScene = sceneAddr != 0 && a == sceneAddr;

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP-CHAIN   IMG{(isStore ? "(store)" : "(load)")} @0x{a:X} {f} {tex.Info.Width}x{tex.Info.Height}" +
                    $"{(hdr ? " [HDR]" : "")}{(isScene ? " [=SCENE-PIN]" : "")}");

                if (hdr && !isScene && !isStore && _targets.Count < MaxTargets)
                {
                    _targets.Add(a);
                }
            });
        }
    }
}
