
using Ryujinx.Common.Logging;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    static class MvppNoFragMask0Probe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_NOFRAG_MASK0") == "1";

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
                    $"[NOFRAG] fragment-less pipelines sanitized (mask=0, blend=off) so far: {n}");
            }
        }
    }
}
