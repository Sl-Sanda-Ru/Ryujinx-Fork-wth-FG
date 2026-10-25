
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppSeamProbe
    {
        private const int MaxShapes = 400;

        private static readonly int[] _watchX;
        private static readonly int[] _watchY;
        private static readonly int _tol;
        private static bool _enabled;

        private static readonly HashSet<long> _seenShapes = new();
        private static int _matches;
        private static long _summaryMs;

        static MvppSeamProbe()
        {
            string spec = Environment.GetEnvironmentVariable("RYUJINX_MVPP_SEAMPROBE");

            if (string.IsNullOrEmpty(spec))
            {
                _watchX = _watchY = Array.Empty<int>();
                return;
            }

            List<int> xs = new(), ys = new();

            foreach (string token in spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length > 1 && int.TryParse(token.AsSpan(1), out int v))
                {
                    if (char.ToLowerInvariant(token[0]) == 'x')
                    {
                        xs.Add(v);
                        continue;
                    }

                    if (char.ToLowerInvariant(token[0]) == 'y')
                    {
                        ys.Add(v);
                        continue;
                    }
                }

                Logger.Warning?.Print(LogClass.Gpu, $"SEAMPROBE: ignored malformed token '{token}' (want x<int> or y<int>)");
            }

            _watchX = xs.ToArray();
            _watchY = ys.ToArray();
            _tol = int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_MVPP_SEAMPROBE_TOL"), out int t)
                ? Math.Clamp(t, 0, 32)
                : 2;
            _enabled = _watchX.Length + _watchY.Length > 0;

            if (_enabled)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"SEAMPROBE armed: x=[{string.Join(",", _watchX)}] y=[{string.Join(",", _watchY)}] tol={_tol}");
            }
        }

        private static bool _announced;

        public static void Announce()
        {
            if (_announced)
            {
                return;
            }

            _announced = true;
            Logger.Info?.Print(LogClass.Gpu,
                $"SEAMPROBE status: enabled={_enabled} x=[{string.Join(",", _watchX)}] y=[{string.Join(",", _watchY)}] tol={_tol}");
        }

        public static void OnScaledCopy(
            string op,
            Texture src, Texture dst,
            float srcGx1, float srcGy1, float srcGx2, float srcGy2,
            float dstGx1, float dstGy1, float dstGx2, float dstGy2,
            float srcHx1, float srcHy1, float srcHx2, float srcHy2,
            float dstHx1, float dstHy1, float dstHx2, float dstHy2,
            string filter)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                if (src != null && dst != null &&
                    src.EffectiveScaleX == 1f && src.EffectiveScaleY == 1f &&
                    dst.EffectiveScaleX == 1f && dst.EffectiveScaleY == 1f)
                {
                    return;
                }

                string hit = EdgeHit(srcGx1, srcGx2, dstGx1, dstGx2, _watchX, "x")
                          ?? EdgeHit(srcGy1, srcGy2, dstGy1, dstGy2, _watchY, "y");

                if (hit == null)
                {
                    return;
                }

                _matches++;

                long shape = Fnv(
                    (ulong)op.GetHashCode(),
                    src?.Range.GetSubRange(0).Address ?? 0,
                    dst?.Range.GetSubRange(0).Address ?? 0,
                    ((long)(int)srcGx1 << 32) ^ (int)srcGy1,
                    ((long)(int)srcGx2 << 32) ^ (int)srcGy2,
                    ((long)(int)dstGx1 << 32) ^ (int)dstGy1,
                    ((long)(int)dstGx2 << 32) ^ (int)dstGy2);

                if (_seenShapes.Count < MaxShapes && _seenShapes.Add(shape))
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"SEAMPROBE {op} hit={hit} filter={filter} " +
                        $"src={Describe(src)} g[{srcGx1:0.#},{srcGy1:0.#},{srcGx2:0.#},{srcGy2:0.#}] h[{srcHx1:0.#},{srcHy1:0.#},{srcHx2:0.#},{srcHy2:0.#}] -> " +
                        $"dst={Describe(dst)} g[{dstGx1:0.#},{dstGy1:0.#},{dstGx2:0.#},{dstGy2:0.#}] h[{dstHx1:0.#},{dstHy1:0.#},{dstHx2:0.#},{dstHy2:0.#}]");
                }

                long now = Environment.TickCount64;
                if (now - _summaryMs >= 5000)
                {
                    _summaryMs = now;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"SEAMPROBE summary: matches={_matches} shapes={_seenShapes.Count}");
                }
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"SEAMPROBE: disabled after unexpected error: {e}");
            }
        }

        private static string EdgeHit(float sA, float sB, float dA, float dB, int[] watch, string axis)
        {
            foreach (int w in watch)
            {
                if (Math.Abs(sA - w) <= _tol || Math.Abs(sB - w) <= _tol)
                {
                    return $"{axis}{w}(src)";
                }

                if (Math.Abs(dA - w) <= _tol || Math.Abs(dB - w) <= _tol)
                {
                    return $"{axis}{w}(dst)";
                }
            }

            return null;
        }

        private static string Describe(Texture t)
        {
            if (t == null)
            {
                return "null";
            }

            return $"@0x{t.Range.GetSubRange(0).Address:X}/{t.Info.FormatInfo.Format}/{t.Info.Width}x{t.Info.Height}" +
                   $"/lvl{t.FirstLevel}+{t.Info.Levels}/eff({t.EffectiveScaleX:0.####},{t.EffectiveScaleY:0.####})";
        }

        private static long Fnv(params ulong[] vals)
        {
            unchecked
            {
                ulong x = 14695981039346656037UL;
                foreach (ulong v in vals)
                {
                    x = (x ^ v) * 1099511628211UL;
                }
                return (long)x;
            }
        }

        private static long Fnv(ulong a, ulong b, ulong c, long d, long e, long f, long g)
        {
            return Fnv(a, b, c, (ulong)d, (ulong)e, (ulong)f, (ulong)g);
        }
    }
}
