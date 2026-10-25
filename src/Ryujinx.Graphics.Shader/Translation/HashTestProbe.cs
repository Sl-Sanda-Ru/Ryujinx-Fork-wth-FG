
using System;
using System.Globalization;

namespace Ryujinx.Graphics.Shader.Translation
{
    public static class HashTestProbe
    {
        public static readonly float? ConstValue = Parse();

        public static bool Enabled => ConstValue.HasValue;

        private static float? Parse()
        {
            string raw = Environment.GetEnvironmentVariable("RYUJINX_HASH_CONST");

            if (raw != null &&
                float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                return value;
            }

            return null;
        }
    }
}
