
using Ryujinx.Common.Logging;
using System;

namespace Ryujinx.Graphics.Vulkan
{
    static class MvppOmapMaskVk
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_OMAPMASK") == "1";

        private static bool _armedLogged;
        private static long _applications;
        private static long _reductions;
        private static long _summaryMs;

        public static uint Apply(uint componentMask, int rtIndex, int fragmentOutputMap)
        {
            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    "[OMAPMASK-VK] armed: colour write masks ANDed with the fragment output map at the Vulkan pipeline level (no export = no write, as OpenGL already does)");
            }

            _applications++;

            uint omap = (uint)(fragmentOutputMap >> (rtIndex * 4)) & 0xFu;
            uint effective = componentMask & omap;

            if (effective != componentMask)
            {
                _reductions++;
            }

            long now = Environment.TickCount64;
            if (now - _summaryMs >= 3000)
            {
                _summaryMs = now;
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[OMAPMASK-VK/HEARTBEAT ~3s] applications={_applications} reductions={_reductions}");
            }

            return effective;
        }
    }
}
