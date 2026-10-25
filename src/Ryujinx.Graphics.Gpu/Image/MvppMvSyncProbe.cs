
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppMvSyncProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVSYNC_PROBE") == "1";

        private static bool _armedLogged;

        private static long _partialSyncs;
        private static long _fullSyncs;
        private static long _copyIns;
        private static long _events;
        private static long _summaryMs;

        public static bool IsMvBuffer(Texture t)
        {
            return t != null &&
                   t.Info.Width == 1280 &&
                   t.Info.Height == 720 &&
                   t.Info.FormatInfo.Format == Format.R10G10B10A2Unorm;
        }

        public static void OnPresent()
        {
            if (!Enabled)
            {
                return;
            }

            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    "[MVSYNC] ARMED (RYUJINX_MVSYNC_PROBE=1). Watching guest-data injection into R10G10B10A2 1280x720: partial sync / full re-sync / copy-dependency.");
            }

            long now = Environment.TickCount64;
            if (now - _summaryMs >= 3000)
            {
                _summaryMs = now;
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[MVSYNC/HEARTBEAT ~3s] partialSyncs={_partialSyncs} fullSyncs={_fullSyncs} copyIns={_copyIns}");
            }
        }

        public static void OnSyncDecision(Texture storage, bool partial, int regionCount, bool anyModified)
        {
            if (!Enabled || !IsMvBuffer(storage))
            {
                return;
            }

            long n;
            if (partial)
            {
                n = ++_partialSyncs;
            }
            else
            {
                n = ++_fullSyncs;
            }

            if (++_events <= 20 || _events % 100 == 0)
            {
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[MVSYNC] GUEST->TEXTURE {(partial ? "PARTIAL" : "FULL")} sync #{n} | regions={regionCount} anyModified={anyModified} | guest data uploaded OVER the MV buffer");
            }
        }

        public static void OnCopyIn(Texture storage)
        {
            if (!Enabled || !IsMvBuffer(storage))
            {
                return;
            }

            long n = ++_copyIns;

            if (++_events <= 20 || _events % 100 == 0)
            {
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[MVSYNC] COPY-IN #{n} | copy dependency wrote into the MV buffer from an overlapping texture");
            }
        }
    }
}
