
using Ryujinx.Common.Logging;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    static class MvppReadBarProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVREADBAR") == "1";

        private static long _applied;
        private static long _lastLogMs;

        public static void OnApplied()
        {
            long n = Interlocked.Increment(ref _applied);
            long now = Environment.TickCount64;

            if (now - Interlocked.Read(ref _lastLogMs) >= 3000)
            {
                Interlocked.Exchange(ref _lastLogMs, now);
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[MVREADBAR] immediate pre-draw barriers on MV-sampling draws so far: {n}");
            }
        }
    }
}
