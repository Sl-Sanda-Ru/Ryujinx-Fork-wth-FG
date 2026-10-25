
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppPairProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_PAIRPROBE") == "1";

        private static readonly HashSet<string> _seen = new();
        private static int _capable;
        private static int _disabled;
        private static long _lastSummaryMs;

        public static void OnPair(Texture texture, Sampler sampler)
        {
            if (texture == null || sampler == null)
            {
                return;
            }

            TextureInfo info = texture.Info;

            if (info.Levels != 1 ||
                info.Width < 64 ||
                info.Height < 64)
            {
                return;
            }

            string mipFilter = sampler.ProbeMinFilter switch
            {
                MinFilter.NearestMipmapNearest or MinFilter.LinearMipmapNearest => "Nearest",
                MinFilter.NearestMipmapLinear or MinFilter.LinearMipmapLinear => "Linear",
                _ => "None",
            };

            bool capable = mipFilter != "None" && sampler.ProbeMaxLod > 0f;

            string line =
                $"tex {info.Width}x{info.Height} lv={info.Levels} {info.FormatInfo.Format} {info.Target} | " +
                $"smp min={sampler.ProbeMinFilter} mag={sampler.ProbeMagFilter} mip={mipFilter} " +
                $"lod=[{sampler.ProbeMinLod:0.##},{sampler.ProbeMaxLod:0.##}] bias={sampler.ProbeMipLodBias:0.##} | " +
                (capable ? "MIP-CAPABLE" : "MIP-DISABLED");

            lock (_seen)
            {
                if (_seen.Add(line))
                {
                    if (capable)
                    {
                        _capable++;
                    }
                    else
                    {
                        _disabled++;
                    }

                    Logger.Info?.Print(LogClass.Gpu, $"PAIRPROBE pair: {line}");
                }

                long now = Environment.TickCount64;
                if (now - _lastSummaryMs > 5000)
                {
                    _lastSummaryMs = now;

                    Logger.Info?.Print(LogClass.Gpu,
                        $"PAIRPROBE summary: combos={_seen.Count} MIP-CAPABLE={_capable} MIP-DISABLED={_disabled} (RGBA8 lv=1 >=64px)");
                }
            }
        }
    }
}
