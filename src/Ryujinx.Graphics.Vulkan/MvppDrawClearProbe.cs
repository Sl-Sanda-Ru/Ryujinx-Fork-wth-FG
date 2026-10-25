
using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    static class MvppDrawClearProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_DRAWCLEAR") == "1";

        private static bool _armedLogged;
        private static long _rerouted;
        private static long _lastLogMs;

        public static bool ShouldReroute(FramebufferParams fb, int index)
        {
            if (!Enabled || fb == null)
            {
                return false;
            }

            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    "[DRAWCLEAR] ARMED (RYUJINX_DRAWCLEAR=1). Full-mask clears of 1280x720 A2B10G10R10 attachments go through the HelperShader draw path.");
            }

            bool match = fb.Width == 1280 && fb.Height == 720 &&
                         index >= 0 && index < fb.AttachmentFormats.Length &&
                         fb.AttachmentFormats[index] == Format.A2B10G10R10UnormPack32;

            if (match)
            {
                long n = Interlocked.Increment(ref _rerouted);
                long now = Environment.TickCount64;

                if (now - Interlocked.Read(ref _lastLogMs) >= 3000)
                {
                    Interlocked.Exchange(ref _lastLogMs, now);
                    Logger.Warning?.Print(LogClass.Gpu, $"[DRAWCLEAR] rerouted clears so far: {n}");
                }
            }

            return match;
        }
    }
}
