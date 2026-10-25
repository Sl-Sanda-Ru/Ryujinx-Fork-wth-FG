
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Engine
{
    static class MvppBlitProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_BLITPROBE") == "1";

        private const int MaxSigs = 512;

        private static readonly HashSet<long> _seenBlitSigs = new();
        private static readonly HashSet<long> _seenDispatchSigs = new();
        private static int _op;
        private static int _dispatchCount;

        private static int _finalW, _finalH;
        private static readonly HashSet<ulong> _finalSrcs = new();
        private static readonly HashSet<ulong> _finalDsts = new();
        private static long _finalLogMs;

        public static void OnBlit(Image.Texture src, Image.Texture dst, long duDx, long dvDy)
        {
            if (!_enabled || src == null || dst == null)
            {
                return;
            }

            try
            {
                OnBlitImpl(src, dst, duDx, dvDy);
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP blit probe: disabled after unexpected error: {e}");
            }
        }

        private static void OnBlitImpl(Image.Texture src, Image.Texture dst, long duDx, long dvDy)
        {
            _op++;

            ulong sAddr = src.Range.GetSubRange(0).Address;
            ulong dAddr = dst.Range.GetSubRange(0).Address;
            Format sFmt = src.Info.FormatInfo.Format;
            Format dFmt = dst.Info.FormatInfo.Format;
            int sW = src.Info.Width, sH = src.Info.Height;
            int dW = dst.Info.Width, dH = dst.Info.Height;

            float sx = duDx != 0 ? (float)(4294967296.0 / duDx) : 0f;
            float sy = dvDy != 0 ? (float)(4294967296.0 / dvDy) : 0f;

            long sig = Fnv(sAddr, dAddr, (int)sFmt, (int)dFmt, sW, sH, dW, dH);
            if (_seenBlitSigs.Count < MaxSigs && _seenBlitSigs.Add(sig))
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP-BLIT op{_op}: src@0x{sAddr:X} {sFmt} {sW}x{sH} -> dst@0x{dAddr:X} {dFmt} {dW}x{dH} scale={sx:0.###}x{sy:0.###}");
            }

            bool ldr =
                dFmt == Format.R8G8B8A8Unorm || dFmt == Format.B8G8R8A8Unorm ||
                dFmt == Format.R8G8B8A8Srgb || dFmt == Format.B8G8R8A8Srgb;

            if (!ldr)
            {
                return;
            }

            if ((long)dW * dH > (long)_finalW * _finalH)
            {
                _finalW = dW;
                _finalH = dH;
                _finalSrcs.Clear();
                _finalDsts.Clear();
            }

            if (dW == _finalW && dH == _finalH)
            {
                _finalSrcs.Add(sAddr);
                _finalDsts.Add(dAddr);

                long now = Environment.TickCount64;
                if (now - _finalLogMs >= 1000)
                {
                    _finalLogMs = now;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-BLIT FINAL {_finalW}x{_finalH}: backbuffers={_finalDsts.Count} distinctSrcs={_finalSrcs.Count} " +
                        $"(>=2 srcs => HUD composited from a SEPARATE source)");
                }
            }
        }

        public static void OnDispatch(Image.TextureManager tm, int gridX, int gridY, int gridZ, ulong shaderVa)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                _dispatchCount++;

                long sig = ((long)(gridX & 0xFFFF) << 32) ^ ((long)(gridY & 0xFFFF) << 16) ^ (long)(shaderVa & 0xFFFFFFF);
                if (_seenDispatchSigs.Count >= MaxSigs || !_seenDispatchSigs.Add(sig))
                {
                    return;
                }

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP-DISPATCH grid={gridX}x{gridY}x{gridZ} shader@0x{shaderVa:X} (total={_dispatchCount})");

                if (tm == null)
                {
                    return;
                }

                List<(ulong Addr, Format Fmt, int W, int H)> ins = new();
                List<(ulong Addr, Format Fmt, int W, int H)> outs = new();

                tm.MvppEnumerateComputeBindings((isImage, isStore, tex) =>
                {
                    ulong a = tex.Range.GetSubRange(0).Address;
                    Format f = tex.Info.FormatInfo.Format;
                    int w = tex.Info.Width, h = tex.Info.Height;
                    if (isImage && isStore)
                    {
                        outs.Add((a, f, w, h));
                    }
                    else
                    {
                        ins.Add((a, f, w, h));
                    }
                });

                string sh = $"{shaderVa & 0xFFFFFF:X}";

                foreach ((ulong a, Format f, int w, int h) in outs)
                {
                    Logger.Info?.Print(LogClass.Gpu, $"MVPP-CIO s@{sh} OUT @0x{a:X} {f} {w}x{h}");
                }
                for (int i = 0; i < ins.Count && i < 16; i++)
                {
                    Logger.Info?.Print(LogClass.Gpu, $"MVPP-CIO s@{sh} IN  @0x{ins[i].Addr:X} {ins[i].Fmt} {ins[i].W}x{ins[i].H}");
                }

                foreach ((ulong oa, Format of, int ow, int oh) in outs)
                {
                    if (!IsLdr(of) || (long)ow * oh < 2_000_000L)
                    {
                        continue;
                    }

                    int ldrInputs = 0;
                    foreach ((ulong ia, Format iff, int iw, int ih) in ins)
                    {
                        if (IsLdr(iff) && ia != oa)
                        {
                            ldrInputs++;
                        }
                    }

                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-CIO s@{sh} VERDICT finalOut={ow}x{oh} separateLdrInputs={ldrInputs} " +
                        $"({(ldrInputs > 0 ? "SEPARATE UI SOURCE => GO" : "no separate LDR input => UI baked/absent => NO-GO")})");
                }
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP blit probe (dispatch): disabled after unexpected error: {e}");
            }
        }

        private static bool IsLdr(Format f)
        {
            return f == Format.R8G8B8A8Unorm || f == Format.B8G8R8A8Unorm ||
                   f == Format.R8G8B8A8Srgb || f == Format.B8G8R8A8Srgb;
        }

        private static long Fnv(ulong a, ulong b, int c, int d, int e, int f, int g, int h)
        {
            unchecked
            {
                ulong x = 14695981039346656037UL;
                foreach (ulong v in stackalloc ulong[] { a, b, (ulong)c, (ulong)d, (ulong)e, (ulong)f, (ulong)g, (ulong)h })
                {
                    x = (x ^ v) * 1099511628211UL;
                }
                return (long)x;
            }
        }
    }
}
