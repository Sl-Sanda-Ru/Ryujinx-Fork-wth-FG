
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppContentProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVHASH") == "1";

        private const ulong BuilderFsAddress = 0x1000AD730UL;
        private const int SampleIntervalMs = 500;
        private const int MaxSamples = 60;

        private static long _lastMs;
        private static int _samples;
        private static bool _failed;
        private static readonly List<double> _occupancy = new();
        private static readonly List<double> _meanAbs = new();

        public static void OnBuilderDraw(TextureManager texMgr, ulong fsAddr)
        {
            if (!Enabled || fsAddr != BuilderFsAddress || _failed || _samples >= MaxSamples)
            {
                return;
            }

            long now = Environment.TickCount64;

            if (now - _lastMs < SampleIntervalMs)
            {
                return;
            }

            _lastMs = now;

            try
            {
                Sample(texMgr);
            }
            catch (Exception ex)
            {
                _failed = true;
                Logger.Warning?.Print(LogClass.Gpu, $"[MVHASH] read FAILED: {ex.Message}");
            }
        }

        private static void Sample(TextureManager texMgr)
        {
            Texture twin = MvppBuilderInProbe.LastTwin;

            if (twin == null)
            {
                return;
            }

            ReadOnlySpan<byte> bytes = twin.HostTexture.GetData().Get();

            if (bytes.Length < 4)
            {
                return;
            }

            int words = bytes.Length / 4;
            long written = 0;
            double sumAbs = 0;
            ulong hash = 14695981039346656037UL;

            for (int i = 0; i < words; i++)
            {
                uint w = (uint)(bytes[i * 4] |
                                (bytes[i * 4 + 1] << 8) |
                                (bytes[i * 4 + 2] << 16) |
                                (bytes[i * 4 + 3] << 24));

                uint r = w & 1023;
                uint g = (w >> 10) & 1023;

                if (r != 0 || g != 0)
                {
                    written++;
                    sumAbs += (r + g) / 1023.0;
                }

                if ((i & 63) == 0)
                {
                    hash = (hash ^ w) * 1099511628211UL;
                }
            }

            double occ = 100.0 * written / words;
            double mean = written > 0 ? sumAbs / written : 0.0;

            _occupancy.Add(occ);
            _meanAbs.Add(mean);
            _samples++;

            Logger.Warning?.Print(LogClass.Gpu,
                $"[MVHASH] sample {_samples,2}: twin {twin.Info.Width}x{twin.Info.Height} " +
                $"occupancy={occ:F3}%  meanMag={mean:F5}  hash=0x{hash:X16}");

            if (_samples % 10 == 0)
            {
                Report();
            }
        }

        private static void Report()
        {
            if (_occupancy.Count == 0)
            {
                return;
            }

            List<double> o = new(_occupancy);
            o.Sort();

            Logger.Warning?.Print(LogClass.Gpu,
                $"[MVHASH/SUMMARY] n={o.Count}  occupancy min={o[0]:F3}%  " +
                $"median={o[o.Count / 2]:F3}%  max={o[^1]:F3}%");
        }
    }
}
