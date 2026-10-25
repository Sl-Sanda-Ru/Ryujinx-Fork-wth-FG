
using System;
using System.Globalization;

namespace Ryujinx.Graphics.Shader.Translation
{
    public static class MufuPrecProbe
    {
        public const int MantissaBits = 23;

        public static readonly int? BitsKept = Parse();

        public static bool Enabled => BitsKept.HasValue;

        public static readonly ulong? AddressOverride = ParseAddr();

        private static ulong? ParseAddr()
        {
            string raw = Environment.GetEnvironmentVariable("RYUJINX_MUFU_ADDR");

            if (raw != null && ulong.TryParse(raw.Trim().Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong addr))
            {
                return addr;
            }

            return null;
        }

        private static int? Parse()
        {
            string raw = Environment.GetEnvironmentVariable("RYUJINX_MUFU_PREC");

            if (!string.IsNullOrWhiteSpace(raw) &&
                int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int bits))
            {
                return Math.Clamp(bits, 0, MantissaBits);
            }

            return null;
        }
    }
}
