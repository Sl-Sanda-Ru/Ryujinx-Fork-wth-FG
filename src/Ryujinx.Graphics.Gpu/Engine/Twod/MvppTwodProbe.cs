
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Engine.Twod
{
    static class MvppTwodProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_TWOD_PROBE") == "1";

        private static bool _announced;
        private static long _nextLogMs;
        private static int _blits;
        private static int _negatives;
        private static int _resized;
        private static readonly Dictionary<string, int> _shapes = new();

        public static void OnPresent()
        {
            if (!_enabled)
            {
                return;
            }

            long now = Environment.TickCount64;

            if (now < _nextLogMs)
            {
                return;
            }

            _nextLogMs = now + 2000;
            _announced = true;

            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP TWOD: {_blits} blits, {_negatives} out of bounds, {_resized} resized, {_shapes.Count} distinct shapes.");

            int shown = 0;

            foreach (KeyValuePair<string, int> kv in _shapes)
            {
                if (shown++ >= 8)
                {
                    break;
                }

                Logger.Info?.Print(LogClass.Gpu, $"MVPP TWOD:   x{kv.Value}  {kv.Key}");
            }

            _blits = 0;
            _negatives = 0;
            _resized = 0;
            _shapes.Clear();
        }

        public static void OnBlit(
            int srcX1, int srcY1, int srcX2, int srcY2,
            int dstX1, int dstY1, int dstX2, int dstY2,
            int srcW, int srcH, int dstW, int dstH)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                if (!_announced)
                {
                    _announced = true;
                    Logger.Info?.Print(LogClass.Gpu,
                        "MVPP TWOD: ON -- census of 2D engine blits, read-only, 1 line every 2 s.");
                }

                _blits++;

                if (srcX1 < 0 || srcY1 < 0 || dstX1 < 0 || dstY1 < 0 || srcX2 > srcW || srcY2 > srcH)
                {
                    _negatives++;
                }

                if ((srcX2 - srcX1) != (dstX2 - dstX1) || (srcY2 - srcY1) != (dstY2 - dstY1))
                {
                    _resized++;
                }

                string shape = $"src[{srcX1},{srcY1}->{srcX2},{srcY2}]/{srcW}x{srcH} dst[{dstX1},{dstY1}->{dstX2},{dstY2}]/{dstW}x{dstH}";
                _shapes[shape] = _shapes.TryGetValue(shape, out int n) ? n + 1 : 1;

            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP TWOD: disabled after error: {e.Message}");
            }
        }
    }
}
