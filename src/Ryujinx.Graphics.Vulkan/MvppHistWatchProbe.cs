
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Vulkan
{
    static class MvppHistWatchProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_HISTWATCH") == "1";

        private static readonly object _lock = new();
        private static readonly Dictionary<string, long> _events = new();
        private static bool _armedLogged;
        private static long _heartbeatMs;

        private static bool IsInteresting(TextureView view)
        {
            return view != null &&
                   ((view.Width == 640 && view.Height == 360) ||
                    view.VkFormat == Silk.NET.Vulkan.Format.R32Sfloat);
        }

        private static string Describe(TextureView view)
        {
            return view == null ? "null" : $"{view.VkFormat} {view.Width}x{view.Height}";
        }

        private static void Record(string evt)
        {
            lock (_lock)
            {
                _events.TryGetValue(evt, out long n);
                _events[evt] = n + 1;

                if (n == 0)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"[HISTWATCH] NEW: {evt}");
                }

                long now = Environment.TickCount64;
                if (now - _heartbeatMs >= 5000)
                {
                    _heartbeatMs = now;
                    foreach (KeyValuePair<string, long> kv in _events)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, $"[HISTWATCH/HB ~5s]   {kv.Value}x {kv.Key}");
                    }
                }
            }
        }

        private static void ArmOnce()
        {
            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    "[HISTWATCH] armed: watching copies / DrawTexture / host uploads touching 640x360 or R32Sfloat views");
            }
        }

        public static void OnCopy(TextureView src, TextureView dst, string path)
        {
            if (!Enabled)
            {
                return;
            }

            ArmOnce();

            if (IsInteresting(src) || IsInteresting(dst))
            {
                Record($"COPY[{path}] {Describe(src)} -> {Describe(dst)}");
            }
        }

        public static void OnSetData(TextureView dst)
        {
            if (!Enabled)
            {
                return;
            }

            ArmOnce();

            if (IsInteresting(dst))
            {
                Record($"SETDATA -> {Describe(dst)}");
            }
        }

        public static void OnDrawTexture(TextureView src, FramebufferParams fb)
        {
            if (!Enabled)
            {
                return;
            }

            ArmOnce();

            bool fbInteresting = false;

            if (fb != null)
            {
                for (int a = 0; a < fb.ColorAttachmentsCount; a++)
                {
                    if (IsInteresting(fb.MvppGetColorView(a)))
                    {
                        fbInteresting = true;
                        break;
                    }
                }
            }

            if (fbInteresting || IsInteresting(src))
            {
                Record($"DRAWTEXTURE src={Describe(src)} fbHas640x360OrR32F={fbInteresting}");
            }
        }
    }
}
