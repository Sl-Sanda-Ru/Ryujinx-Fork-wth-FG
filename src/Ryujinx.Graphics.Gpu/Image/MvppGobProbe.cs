
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppGobProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_GOBZ_PROBE") == "1";

        public static readonly bool ClampDisabled =
            Environment.GetEnvironmentVariable("RYUJINX_NO_GOBZ_CLAMP") == "1";

        private static readonly object _lock = new();
        private static readonly Dictionary<string, int> _shapes = new();
        private static int _total;
        private static long _lastLogMs;

        public static void NoteClamp(int width, int height, int layers, int gobZ, string target, string format)
        {
            lock (_lock)
            {
                _total++;

                string key = $"{width}x{height}x{layers} {target} {format} gobZ={gobZ}";

                _shapes.TryGetValue(key, out int n);
                _shapes[key] = n + 1;

                long now = Environment.TickCount64;

                if (now - _lastLogMs < 5000)
                {
                    return;
                }

                _lastLogMs = now;

                System.Text.StringBuilder sb = new();
                sb.Append($"GOBPROBE: clamp gobBlocksInZ->1 fired {_total} times, {_shapes.Count} distinct shapes");
                sb.Append(ClampDisabled ? " [CLAMP DESACTIVE]" : "");
                sb.Append(':');

                int shown = 0;

                foreach (KeyValuePair<string, int> kv in _shapes)
                {
                    if (shown++ >= 8)
                    {
                        sb.Append($" (+{_shapes.Count - 8} autres)");

                        break;
                    }

                    sb.Append($" [{kv.Key} x{kv.Value}]");
                }

                Logger.Info?.Print(LogClass.Gpu, sb.ToString());
            }
        }
    }
}
