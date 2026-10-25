
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Vulkan
{
    static class MvppStorageBarrierProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_STORAGEID") == "1";

        private static bool _armedLogged;

        private sealed class ShapeStats
        {
            public readonly HashSet<int> Instances = new();
            public int LoadOps;
            public int Skipped;
            public int ReadNotRecorded;
        }

        private static readonly Dictionary<string, ShapeStats> _stats = new();

        private static int _loadOpTotal;

        public static void ReportArmed()
        {
            if (!Enabled || _armedLogged)
            {
                return;
            }

            _armedLogged = true;

            Logger.Warning?.Print(LogClass.Gpu, "[STORAGEID] ARMED (RYUJINX_STORAGEID=1). Expect periodic lines for full-screen targets.");
        }

        public static void OnLoadOp(object storage, int width, int height, string format, bool barrierEmitted, bool readRecorded)
        {
            if (!Enabled)
            {
                return;
            }

            if (width != 1280 || height != 720)
            {
                return;
            }

            int id = RuntimeHelpers.GetHashCode(storage);

            lock (_stats)
            {
                if (!_stats.TryGetValue(format, out ShapeStats s))
                {
                    s = new ShapeStats();
                    _stats[format] = s;
                }

                s.Instances.Add(id);
                s.LoadOps++;

                if (!barrierEmitted)
                {
                    s.Skipped++;
                }

                if (!readRecorded)
                {
                    s.ReadNotRecorded++;
                }

                _loadOpTotal++;

                if ((_loadOpTotal % 400) == 0)
                {
                    foreach (KeyValuePair<string, ShapeStats> kv in _stats)
                    {
                        Logger.Warning?.Print(LogClass.Gpu,
                            $"[STORAGEID] {kv.Key} 1280x720 | storages {kv.Value.Instances.Count} " +
                            $"| loadOp {kv.Value.LoadOps} | no barrier {kv.Value.Skipped} " +
                            $"| READ NOT COVERED {kv.Value.ReadNotRecorded} " +
                            $"({(kv.Value.LoadOps == 0 ? 0 : kv.Value.ReadNotRecorded * 100L / kv.Value.LoadOps)}%)");
                    }
                }
            }
        }
    }
}
