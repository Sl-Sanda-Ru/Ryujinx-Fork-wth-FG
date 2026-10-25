
using System;
using System.Globalization;
using System.Threading;

namespace Ryujinx.Graphics.Shader.Translation
{
    public static class MvppTruncEpsProbe
    {
        public static readonly float? Value = Parse();

        public static bool Enabled => Value.HasValue;

        private static long _applied;

        private static float? Parse()
        {
            string raw = Environment.GetEnvironmentVariable("RYUJINX_TRUNCEPS");

            if (raw != null &&
                float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                return value;
            }

            return null;
        }

        public static void OnApplied(IGpuAccessor gpuAccessor)
        {
            if (Interlocked.Increment(ref _applied) == 1)
            {
                gpuAccessor.Log(
                    $"[TRUNCEPS] armed: bokeh FS F2I inputs nudged by {Value.Value.ToString(CultureInfo.InvariantCulture)}");
            }
        }
    }
}
