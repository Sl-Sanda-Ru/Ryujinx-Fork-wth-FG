
using Ryujinx.Common.Logging;
using System;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Vulkan
{
    static class MvppDescTruthProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_DESCTRUTH") == "1";

        private static bool _armedLogged;
        private static long _checks;
        private static long _stale;
        private static long _staleLogged;
        private static long _heartbeatMs;

        public static void OnDescriptorWrite(TextureView view, Auto<DisposableImageView> cachedImageView)
        {
            if (!Enabled || view == null)
            {
                return;
            }

            if (view.Width != 1280 ||
                view.Height != 720 ||
                view.VkFormat != Silk.NET.Vulkan.Format.A2B10G10R10UnormPack32)
            {
                return;
            }

            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    "[DESCTRUTH] armed: comparing the CACHED image-view Auto written into descriptors vs the view's live one, MV-shaped views only");
            }

            _checks++;

            Auto<DisposableImageView> current = view.GetImageView();

            if (!ReferenceEquals(cachedImageView, current))
            {
                _stale++;

                if (_staleLogged < 20)
                {
                    _staleLogged++;
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"[DESCTRUTH] !! STALE DESCRIPTOR: cachedAuto=0x{(cachedImageView != null ? RuntimeHelpers.GetHashCode(cachedImageView) : 0):X8} " +
                        $"currentAuto=0x{(current != null ? RuntimeHelpers.GetHashCode(current) : 0):X8} storage=0x{(view.Storage != null ? RuntimeHelpers.GetHashCode(view.Storage) : 0):X8}");
                }
            }

            long now = Environment.TickCount64;
            if (now - _heartbeatMs >= 5000)
            {
                _heartbeatMs = now;
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[DESCTRUTH/HB ~5s] checks={_checks} stale={_stale}");
            }
        }
    }
}
