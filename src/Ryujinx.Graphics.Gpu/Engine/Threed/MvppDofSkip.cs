
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Image;
using System;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppDofSkip
    {
        public static readonly int Mode = ParseMode();

        public static bool Enabled => Mode != 0;

        private static int ParseMode()
        {
            return Environment.GetEnvironmentVariable("RYUJINX_MVPP_DOF_SCATTER_SKIP") switch
            {
                "1" => 1,
                "2" => 2,
                _ => 0,
            };
        }

        private const float WidthRatio = 0.4f;
        private const float AspectRatio = 16f / 9f;
        private const float Tolerance = 0.02f;

        private static bool _announced;
        private static long _skipped;
        private static long _lastLogMs;
        private static int _renderWidth;

        private const int CensusCap = 16;
        private static readonly long[] _shapes = new long[CensusCap];
        private static int _shapeCount;
        private static readonly object _censusLock = new();

        public static bool ShouldSkip(GpuChannel channel)
        {
            if (!Enabled || channel == null)
            {
                return false;
            }

            Image.Texture target;

            try
            {
                target = channel.TextureManager.GetColorTarget(0);
            }
            catch
            {
                return false;
            }

            if (target == null)
            {
                return false;
            }

            int rawW = target.Info.Width;
            int rawH = target.Info.Height;

            if (rawW > _renderWidth)
            {
                int previous = _renderWidth;
                _renderWidth = rawW;

                if (Mode == 2 && previous != 0)
                {
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"[DOFSKIP/DRY] render width reference moved: {previous} -> {rawW}. " +
                        $"The 40% target is now {rawW * WidthRatio:F0}px wide.");
                }
            }

            if (target.Info.FormatInfo.Format != GAL.Format.R16G16B16A16Float || _renderWidth == 0)
            {
                return false;
            }

            float widthRatio = rawW / (float)_renderWidth;
            float aspect = rawH == 0 ? 0f : rawW / (float)rawH;

            if (MathF.Abs(widthRatio - WidthRatio) > Tolerance ||
                MathF.Abs(aspect - AspectRatio) > Tolerance)
            {
                return false;
            }

            _skipped++;

            ReportShape(rawW, rawH, widthRatio);

            if (!_announced)
            {
                _announced = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[DOFSKIP] armed: skipping the scatter/bokeh pass (target {rawW}x{rawH} RGBA16F = " +
                    $"{widthRatio * 100f:F0}% of a {_renderWidth}px render, 16:9). " +
                    "Motion blur will be absent - this is the option's purpose.");
            }

            long now = Environment.TickCount64;

            if (now - _lastLogMs >= 10000)
            {
                _lastLogMs = now;
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[DOFSKIP{(Mode == 2 ? "/DRY" : "")}] draws matched so far: {_skipped}");
            }

            return Mode != 2;
        }

        private static void ReportShape(int width, int height, float widthRatio)
        {
            long key = ((long)width << 32) | (uint)height;

            lock (_censusLock)
            {
                for (int i = 0; i < _shapeCount; i++)
                {
                    if (_shapes[i] == key)
                    {
                        return;
                    }
                }

                if (_shapeCount >= CensusCap)
                {
                    return;
                }

                _shapes[_shapeCount++] = key;

                string tag = Mode == 2 ? "DOFSKIP/DRY" : "DOFSKIP";
                string verb = Mode == 2 ? "WOULD MATCH" : "MATCH";

                Logger.Warning?.Print(LogClass.Gpu,
                    $"[{tag}] {verb} #{_shapeCount}: {width}x{height} RGBA16F = " +
                    $"{widthRatio * 100f:F1}% of a {_renderWidth}px render. " +
                    (_shapeCount > 1
                        ? "SECOND DISTINCT SHAPE - the rule is claiming more than the bokeh pass."
                        : "This is the expected bokeh target."));
            }
        }
    }
}
