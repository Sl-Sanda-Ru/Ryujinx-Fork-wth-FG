
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;
using System.Text;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppMvBufProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVBUF_PROBE") == "1";

        private static bool _armedLogged;

        public static void ReportArmed()
        {
            if (!Enabled || _armedLogged)
            {
                return;
            }

            _armedLogged = true;

            Logger.Warning?.Print(LogClass.Gpu,
                "[MVBUF] ARMED (RYUJINX_MVBUF_PROBE=1). Watching R10G10B10A2 1280x720: engine clears (value/mask/scissor), writer draws, per-frame reliability.");
        }

        private static bool IsMvBuffer(Texture t)
        {
            return t != null &&
                   t.Info.Width == 1280 &&
                   t.Info.Height == 720 &&
                   t.Info.FormatInfo.Format == Format.R10G10B10A2Unorm;
        }

        private static long _frames;
        private static int _clearsThisFrame;
        private static int _writesThisFrame;
        private static long _frames0Clear;
        private static long _frames1Clear;
        private static long _framesMultiClear;
        private static long _frames0Write;
        private static long _framesWithWrite;

        private static long _clears;
        private static long _writerDraws;
        private static long _summaryMs;

        private static readonly HashSet<string> _clearSignatures = new();
        private static readonly HashSet<(ulong, int, int)> _writers = new();

        private static readonly HashSet<string> _writerStates = new();
        private static long _drawsBlendOnMv;
        private static long _drawsPartialMaskOnMv;

        private static readonly HashSet<int> _clearedIds = new();
        private static readonly HashSet<int> _sampledIds = new();

        public static void OnPresent()
        {
            if (!Enabled)
            {
                return;
            }

            if (_clears + _writerDraws > 0)
            {
                _frames++;

                if (_clearsThisFrame == 0)
                {
                    _frames0Clear++;
                }
                else if (_clearsThisFrame == 1)
                {
                    _frames1Clear++;
                }
                else
                {
                    _framesMultiClear++;
                }

                if (_writesThisFrame == 0)
                {
                    _frames0Write++;
                }
                else
                {
                    _framesWithWrite++;
                }
            }

            _clearsThisFrame = 0;
            _writesThisFrame = 0;

            long now = Environment.TickCount64;
            if (now - _summaryMs >= 3000 && _frames > 0)
            {
                _summaryMs = now;

                Logger.Warning?.Print(LogClass.Gpu,
                    $"[MVBUF/SUMMARY ~3s] frames={_frames} | clears/frame: 0x{_frames0Clear} 1x{_frames1Clear} multi x{_framesMultiClear} | " +
                    $"writes/frame: 0 x{_frames0Write} >=1 x{_framesWithWrite} | totals: clears={_clears} writerDraws={_writerDraws} | " +
                    $"clearSigs={_clearSignatures.Count} writers={_writers.Count} | " +
                    $"BLEND-on-MV draws={_drawsBlendOnMv} partial-mask draws={_drawsPartialMaskOnMv}");
            }
        }

        public static void OnClear(Texture target, int index, uint componentMask, ColorF color, bool customScissor, int sx, int sy, int sw, int sh)
        {
            if (!Enabled || !IsMvBuffer(target))
            {
                return;
            }

            _clears++;
            _clearsThisFrame++;

            string sig = $"rgba=({color.Red:0.###},{color.Green:0.###},{color.Blue:0.###},{color.Alpha:0.###}) mask=0x{componentMask:X} " +
                         (customScissor ? $"scissor={sx},{sy} {sw}x{sh}" : "scissor=full");

            lock (_clearSignatures)
            {
                if (_clearSignatures.Add(sig))
                {
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"[MVBUF/CLEAR] out_attr{index} NEW SIGNATURE: {sig} | clear #{_clears}");
                }
            }

            int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(target);
            lock (_clearedIds)
            {
                if (_clearedIds.Add(id))
                {
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"[MVBUF/ID] CLEAR target instance 0x{id:X8} (new; {_clearedIds.Count} cleared instance(s) so far)");
                }
            }
        }

        public static void OnSampled(Texture texture)
        {
            if (!Enabled || !IsMvBuffer(texture))
            {
                return;
            }

            int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(texture);
            lock (_clearedIds)
            {
                if (_sampledIds.Add(id))
                {
                    bool overlaps = _clearedIds.Contains(id);
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"[MVBUF/ID] SAMPLED instance 0x{id:X8} isView={texture.IsView} " +
                        $"=> {(overlaps ? "SAME as a cleared instance" : "*** NEVER CLEARED (desync suspect) ***")} " +
                        $"| sampled {_sampledIds.Count}, cleared {_clearedIds.Count}");
                }
            }
        }

        public static void OnDraw(TextureManager texMgr, ulong fsAddr, int vpW, int vpH, ReadOnlySpan<bool> blendEnable, ReadOnlySpan<uint> writeMasks)
        {
            if (texMgr == null)
            {
                return;
            }

            for (int slot = 0; slot < texMgr.ColorTargetsLength; slot++)
            {
                Texture ct = texMgr.GetColorTarget(slot);
                if (!IsMvBuffer(ct))
                {
                    continue;
                }

                _writerDraws++;
                _writesThisFrame++;

                bool blend = slot < blendEnable.Length && blendEnable[slot];
                uint mask = slot < writeMasks.Length ? writeMasks[slot] : 0xFu;

                if (blend && mask != 0)
                {
                    _drawsBlendOnMv++;
                }

                if (mask != 0xF && mask != 0)
                {
                    _drawsPartialMaskOnMv++;
                }

                string state = $"out_attr{slot} fs=0x{fsAddr:X} vp={vpW}x{vpH} blend={(blend ? "ON" : "off")} mask=0x{mask:X}";
                bool isNew;
                lock (_writerStates)
                {
                    isNew = _writerStates.Add(state);
                    _writers.Add((fsAddr, vpW, vpH));
                }

                if (isNew && (blend || mask != 0xF))
                {
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"[MVBUF/WRITER-STATE] {state}{(blend && mask != 0 ? " *** BLEND INTO MV ***" : "")}");
                }
            }
        }
    }
}
