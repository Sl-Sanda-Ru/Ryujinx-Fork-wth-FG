
using System;
using System.Globalization;

namespace Ryujinx.Graphics.Shader.Translation
{
    public static class MvppTileCapProbe
    {
        public static readonly float? Value = Parse();

        public static bool Enabled => Value.HasValue;

        public static readonly string Channel =
            Environment.GetEnvironmentVariable("RYUJINX_TILECAP_CH");

        private static float? Parse()
        {
            string raw = Environment.GetEnvironmentVariable("RYUJINX_TILECAP");

            if (raw != null &&
                float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                return value;
            }

            return null;
        }
    }
}
