
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppFullSyncProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_FULLSYNC") == "1";

        private static readonly HashSet<string> _reported = new();

        private static bool _armedLogged;
        private static int _count;

        public static void ReportArmed()
        {
            if (!Enabled || _armedLogged)
            {
                return;
            }

            _armedLogged = true;

            Logger.Warning?.Print(LogClass.Gpu, "[FULLSYNC] ARMED (RYUJINX_FULLSYNC=1). Expect one line per distinct texture shape taking the full-upload path.");
        }

        private static readonly Dictionary<string, int> _shapeCounts = new();

        private static int _fullResLogged;

        private const int FullResLogCap = 60;

        public static void OnFullSync(TextureInfo info, ReadOnlySpan<byte> data, bool isView)
        {
            if (!Enabled)
            {
                return;
            }

            _count++;

            string key = $"{info.FormatInfo.Format}|{info.Width}x{info.Height}|{info.Target}";

            lock (_shapeCounts)
            {
                _shapeCounts.TryGetValue(key, out int n);
                _shapeCounts[key] = n + 1;

                if (info.Width == 1280 && info.Height == 720 && _fullResLogged < FullResLogCap)
                {
                    _fullResLogged++;

                    Logger.Warning?.Print(LogClass.Gpu,
                        $"[FULLSYNC/720p] #{n + 1} {info.FormatInfo.Format} isView={isView} " +
                        $"| sync {_count}");
                }
            }

            lock (_reported)
            {
                if (!_reported.Add(key))
                {
                    return;
                }
            }

            int nonZero = 0;
            int sampled = 0;

            for (int i = 0; i < data.Length; i += 4093)
            {
                sampled++;

                if (data[i] != 0)
                {
                    nonZero++;
                }
            }

            Logger.Warning?.Print(LogClass.Gpu,
                $"[FULLSYNC] {info.FormatInfo.Format} {info.Width}x{info.Height} {info.Target} " +
                $"({info.FormatInfo.BytesPerPixel} bpp) uploaded FROM GUEST | guest bytes {data.Length} | " +
                $"sampled {sampled}, non-zero {nonZero} => {(nonZero == 0 ? "GUEST MEMORY IS EMPTY" : "guest has content")} | " +
                $"total full-syncs so far {_count}");
        }
    }
}
