
using System;
using System.Threading;

namespace Ryujinx.Graphics.Shader.Translation
{
    public static class NanScrubProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_NANSCRUB") == "1";

        public static readonly bool SatScrub =
            Environment.GetEnvironmentVariable("RYUJINX_SATSCRUB") == "1";

        public static readonly float SatScrubThreshold = ParseThreshold();

        private static float ParseThreshold()
        {
            string raw = Environment.GetEnvironmentVariable("RYUJINX_SATSCRUB_T");

            return float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value)
                ? value
                : 0.999f;
        }

        private static long _armed;

        private static readonly System.Collections.Generic.HashSet<ulong> _armedAddresses = new();

        public static bool WasArmed(ulong address)
        {
            lock (_armedAddresses)
            {
                return _armedAddresses.Contains(address);
            }
        }

        public static int ArmedAddressCount()
        {
            lock (_armedAddresses)
            {
                return _armedAddresses.Count;
            }
        }

        public static void OnArmed(ulong address, IGpuAccessor gpuAccessor)
        {
            if (address != 0)
            {
                lock (_armedAddresses)
                {
                    _armedAddresses.Add(address);
                }
            }

            long n = Interlocked.Increment(ref _armed);

            if (n <= 3 || n % 50 == 0)
            {
                gpuAccessor.Log($"[NANSCRUB] armed shader #{n} (guest 0x{address:X}) -- outputs will be NaN-scrubbed"
                    + (SatScrub ? $" + sat-scrubbed at >= {SatScrubThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : ""));
            }
        }
    }
}
