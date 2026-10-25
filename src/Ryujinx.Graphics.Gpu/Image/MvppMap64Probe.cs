
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppMap64Probe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MAP64_PROBE") == "1";

        private static bool _armedLogged;

        public static void ReportArmed()
        {
            if (!Enabled || _armedLogged)
            {
                return;
            }

            _armedLogged = true;

            Logger.Warning?.Print(LogClass.Gpu,
                "[MAP64] ARMED (RYUJINX_MAP64_PROBE=1). Watching 64x36 / 320x180 / 160x90: writer passes + viewports, guest uploads, group syncs, instance churn.");
        }

        private static bool IsWatched(TextureInfo info)
        {
            return (info.Width == 64 && info.Height == 36) ||
                   (info.Width == 320 && info.Height == 180) ||
                   (info.Width == 160 && info.Height == 90);
        }

        private struct In
        {
            public string Fmt;
            public int W;
            public int H;
            public ShaderStage Stage;
            public int Handle;
        }

        private static readonly List<In> _inputs = new();

        public static void OnInput(Texture texture, ShaderStage stage, int handle)
        {
            if (!Enabled || texture == null)
            {
                return;
            }

            lock (_inputs)
            {
                if (_inputs.Count >= 64)
                {
                    return;
                }

                TextureInfo info = texture.Info;
                _inputs.Add(new In
                {
                    Fmt = info.FormatInfo.Format.ToString(),
                    W = info.Width,
                    H = info.Height,
                    Stage = stage,
                    Handle = handle,
                });
            }
        }

        private static readonly HashSet<(ulong, string, int, int)> _seenWriters = new();

        private static readonly Dictionary<string, HashSet<int>> _instances = new();

        private static long _totalDraws;
        private static long _mapDraws;
        private static long _fullSyncs;
        private static long _fullSyncsEmpty;
        private static long _groupSyncs;
        private static long _summaryMs;

        public static void OnDraw(TextureManager texMgr, ulong fsAddr, ulong vsAddr, int vpW, int vpH)
        {
            _totalDraws++;

            try
            {
                if (texMgr == null)
                {
                    return;
                }

                for (int slot = 0; slot < texMgr.ColorTargetsLength; slot++)
                {
                    Texture ct = texMgr.GetColorTarget(slot);
                    if (ct == null || !IsWatched(ct.Info))
                    {
                        continue;
                    }

                    _mapDraws++;

                    TextureInfo info = ct.Info;
                    string shape = $"{info.FormatInfo.Format}/{info.Width}x{info.Height}";
                    int id = RuntimeHelpers.GetHashCode(ct);

                    lock (_instances)
                    {
                        if (!_instances.TryGetValue(shape, out HashSet<int> set))
                        {
                            _instances[shape] = set = new HashSet<int>();
                        }

                        set.Add(id);
                    }

                    bool isNew;
                    lock (_seenWriters)
                    {
                        isNew = _seenWriters.Add((fsAddr, shape, vpW, vpH));
                    }

                    if (isNew)
                    {
                        var sb = new StringBuilder();
                        lock (_inputs)
                        {
                            foreach (In i in _inputs)
                            {
                                sb.Append($" <-tcb_{i.Handle:X} {i.Fmt}/{i.W}x{i.H} {i.Stage};");
                            }
                        }

                        Logger.Warning?.Print(LogClass.Gpu,
                            $"[MAP64/WRITER] out_attr{slot} {shape} inst=0x{id:X8} | fs=0x{fsAddr:X} vs=0x{vsAddr:X} " +
                            $"| VIEWPORT {vpW}x{vpH}{(vpW < info.Width || vpH < info.Height ? " *** PARTIAL ***" : "")} " +
                            $"| inputs:{sb}");
                    }
                }
            }
            finally
            {
                lock (_inputs)
                {
                    _inputs.Clear();
                }

                long now = Environment.TickCount64;
                if (now - _summaryMs >= 3000 && _mapDraws > 0)
                {
                    _summaryMs = now;

                    var sb = new StringBuilder();
                    lock (_instances)
                    {
                        foreach (KeyValuePair<string, HashSet<int>> kv in _instances)
                        {
                            sb.Append($"{kv.Key}: {kv.Value.Count} instance(s); ");
                        }
                    }

                    Logger.Warning?.Print(LogClass.Gpu,
                        $"[MAP64/SUMMARY ~3s] mapDraws={_mapDraws} / totalDraws={_totalDraws} | " +
                        $"fullSyncs={_fullSyncs} (empty {_fullSyncsEmpty}) | groupSyncs={_groupSyncs} | {sb}");
                }
            }
        }

        private static long _fullSyncLogged;
        private static long _groupSyncLogged;
        private const long PerEventLogCap = 40;

        public static void OnFullSync(Texture texture, ReadOnlySpan<byte> data)
        {
            if (!Enabled || texture == null || !IsWatched(texture.Info))
            {
                return;
            }

            _fullSyncs++;

            bool empty = true;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != 0)
                {
                    empty = false;
                    break;
                }
            }

            if (empty)
            {
                _fullSyncsEmpty++;
            }

            if (_fullSyncLogged++ < PerEventLogCap)
            {
                TextureInfo info = texture.Info;
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[MAP64/FULLSYNC] {info.FormatInfo.Format}/{info.Width}x{info.Height} inst=0x{RuntimeHelpers.GetHashCode(texture):X8} " +
                    $"isView={texture.IsView} | guest bytes {data.Length} => {(empty ? "GUEST MEMORY EMPTY" : "guest has content")} " +
                    $"| fullSyncs so far {_fullSyncs}");
            }
        }

        public static void OnGroupSync(Texture texture)
        {
            if (!Enabled || texture == null || !IsWatched(texture.Info))
            {
                return;
            }

            _groupSyncs++;

            if (_groupSyncLogged++ < PerEventLogCap)
            {
                TextureInfo info = texture.Info;
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[MAP64/GROUPSYNC] {info.FormatInfo.Format}/{info.Width}x{info.Height} inst=0x{RuntimeHelpers.GetHashCode(texture):X8} " +
                    $"| groupSyncs so far {_groupSyncs}");
            }
        }
    }
}
