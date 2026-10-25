
using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Vulkan
{
    static class MvppClearRectProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_CLEARRECT_PROBE") == "1";

        private static bool _armedLogged;
        private static long _clears720;
        private static readonly HashSet<string> _signatures = new();

        public static void OnClear(FramebufferParams fb, int index, Silk.NET.Vulkan.ClearRect rect, float r, float g, float b, float a)
        {
            if (!Enabled || fb == null)
            {
                return;
            }

            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    "[CLEARRECT] ARMED (RYUJINX_CLEARRECT_PROBE=1). Logging final vkCmdClearAttachments rects on 1280x720 framebuffers.");
            }

            if (fb.Width != 1280 || fb.Height != 720)
            {
                return;
            }

            _clears720++;

            bool full = rect.Rect.Offset.X == 0 && rect.Rect.Offset.Y == 0 &&
                        rect.Rect.Extent.Width == fb.Width && rect.Rect.Extent.Height == fb.Height;

            string fmt = index >= 0 && index < fb.AttachmentFormats.Length ? fb.AttachmentFormats[index].ToString() : "?";
            string sig = $"att{index} {fmt} rgba=({r:0.###},{g:0.###},{b:0.###},{a:0.###}) " +
                         $"rect={rect.Rect.Offset.X},{rect.Rect.Offset.Y} {rect.Rect.Extent.Width}x{rect.Rect.Extent.Height}" +
                         (full ? " (FULL)" : " *** PARTIAL ***");

            lock (_signatures)
            {
                if (_signatures.Add(sig))
                {
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"[CLEARRECT] NEW SIGNATURE: {sig} | 720p clears so far {_clears720}");
                }
            }
        }
    }
}
