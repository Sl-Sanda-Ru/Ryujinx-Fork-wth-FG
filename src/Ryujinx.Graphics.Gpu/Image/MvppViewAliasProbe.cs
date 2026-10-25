
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppViewAliasProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_VIEWPROBE") == "1";

        private static readonly HashSet<string> _reported = new();

        private static bool _armedLogged;
        private static bool _firstViewLogged;
        private static int _viewCount;

        public static void ReportArmed()
        {
            if (!Enabled || _armedLogged)
            {
                return;
            }

            _armedLogged = true;

            Logger.Warning?.Print(LogClass.Gpu, "[VIEWALIAS] ARMED (RYUJINX_VIEWPROBE=1). Expect a FIRST VIEW line if the view-creation path is ever reached.");
        }

        public static void OnViewCreated(TextureInfo child, Texture parent, int firstLayer, int firstLevel, string compatibility)
        {
            if (!Enabled || parent == null)
            {
                return;
            }

            TextureInfo p = parent.Info;

            _viewCount++;

            if (!_firstViewLogged)
            {
                _firstViewLogged = true;

                Logger.Warning?.Print(LogClass.Gpu,
                    $"[VIEWALIAS] FIRST VIEW reached: child {child.FormatInfo.Format} {child.Width}x{child.Height} " +
                    $"over parent {p.FormatInfo.Format} {p.Width}x{p.Height} | compat {compatibility}");
            }

            if ((_viewCount % 500) == 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"[VIEWALIAS] tally: {_viewCount} views created so far, {_reported.Count} distinct cross-format cases.");
            }

            if (child.FormatInfo.Format == p.FormatInfo.Format)
            {
                return;
            }

            string key = $"{child.FormatInfo.Format}|{p.FormatInfo.Format}|{child.Width}x{child.Height}|{p.Width}x{p.Height}";

            lock (_reported)
            {
                if (!_reported.Add(key))
                {
                    return;
                }
            }

            Logger.Warning?.Print(LogClass.Gpu,
                $"[VIEWALIAS] view {child.FormatInfo.Format} {child.Width}x{child.Height} " +
                $"({child.FormatInfo.BytesPerPixel} bpp) created OVER parent {p.FormatInfo.Format} " +
                $"{p.Width}x{p.Height} ({p.FormatInfo.BytesPerPixel} bpp) | parent addr 0x{parent.Range.GetSubRange(0).Address:X} " +
                $"| firstLayer {firstLayer} firstLevel {firstLevel} | compat {compatibility}");
        }
    }
}
