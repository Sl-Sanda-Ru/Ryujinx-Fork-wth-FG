
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;

namespace Ryujinx.Graphics.Vulkan
{
    static class MvppVkDropProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_VKDROP_PROBE") == "1";

        private static bool _armedLogged;

        private static long _validBinds;
        private static long _invalidBinds;
        private static long _nullSlots;
        private static long _summaryMs;

        private static bool IsMvShape(TextureView view)
        {
            return view != null &&
                   view.Info.Width == 1280 &&
                   view.Info.Height == 720 &&
                   view.Info.Format == Format.R10G10B10A2Unorm;
        }

        public static void OnBind(Span<ITexture> colors)
        {
            if (!Enabled)
            {
                return;
            }

            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    "[VKDROP] ARMED (RYUJINX_VKDROP_PROBE=1). Watching MV-shaped colour targets (R10G10B10A2 1280x720) for invalid-view binds at the Vulkan boundary.");
            }

            for (int i = 0; i < colors.Length; i++)
            {
                if (colors[i] is TextureView view && IsMvShape(view))
                {
                    if (view.Valid)
                    {
                        _validBinds++;
                    }
                    else
                    {
                        long n = ++_invalidBinds;

                        if (n <= 20 || n % 500 == 0)
                        {
                            Logger.Warning?.Print(LogClass.Gpu,
                                $"[VKDROP] MV attachment slot {i} bound with INVALID view (#{n}) -- attachment silently dropped: no draw, no clear will reach the image");
                        }
                    }
                }
                else if (colors[i] == null)
                {
                    _nullSlots++;
                }
            }

            long now = Environment.TickCount64;
            if (now - _summaryMs >= 3000)
            {
                _summaryMs = now;
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[VKDROP/HEARTBEAT ~3s] MV validBinds={_validBinds} INVALID-view binds={_invalidBinds}");
            }
        }
    }
}
