
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Vulkan
{
    static class MvppMemAliasProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MEMALIAS") == "1";

        private sealed class Range
        {
            public ulong Handle;
            public ulong Offset;
            public ulong Size;
            public bool IsMv;
            public bool IsHost;
            public string MvDesc;
            public long BornMs;
        }

        private static readonly object _lock = new();
        private static readonly Dictionary<(ulong, ulong), Range> _live = new();
        private static readonly List<Range> _liveMv = new();
        private static bool _armedLogged;
        private static long _heartbeatMs;
        private static long _overlaps;
        private static long _allocs;

        private static bool Overlaps(Range a, Range b)
        {
            return a.Handle == b.Handle &&
                   a.Offset < b.Offset + b.Size &&
                   b.Offset < a.Offset + a.Size;
        }

        public static void OnAlloc(ulong memoryHandle, ulong offset, ulong size, bool host)
        {
            if (!Enabled || memoryHandle == 0)
            {
                return;
            }

            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    "[MEMALIAS] armed: tracking live device-memory ranges; MV-shaped images tagged; overlaps reported");
            }

            Range range = new()
            {
                Handle = memoryHandle,
                Offset = offset,
                Size = size,
                IsHost = host,
                BornMs = Environment.TickCount64,
            };

            lock (_lock)
            {
                _allocs++;
                _live[(memoryHandle, offset)] = range;

                foreach (Range mv in _liveMv)
                {
                    if (!ReferenceEquals(mv, range) && Overlaps(mv, range))
                    {
                        _overlaps++;
                        Logger.Warning?.Print(LogClass.Gpu,
                            $"[MEMALIAS] !! OVERLAP: new alloc mem=0x{memoryHandle:X} off={offset} size={size} " +
                            $"overlaps LIVE MV {mv.MvDesc} mem=0x{mv.Handle:X} off={mv.Offset} size={mv.Size}");
                    }
                }

                Heartbeat();
            }
        }

        public static void OnFree(ulong memoryHandle, ulong offset, ulong size)
        {
            if (!Enabled || memoryHandle == 0)
            {
                return;
            }

            lock (_lock)
            {
                if (_live.Remove((memoryHandle, offset), out Range range) && range.IsMv)
                {
                    _liveMv.Remove(range);
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"[MEMALIAS] MV FREED: {range.MvDesc} mem=0x{memoryHandle:X} off={offset} size={size} " +
                        $"lifetimeMs={Environment.TickCount64 - range.BornMs}");
                }
            }
        }

        public static void TagMv(ulong memoryHandle, ulong offset, string desc)
        {
            if (!Enabled || memoryHandle == 0)
            {
                return;
            }

            lock (_lock)
            {
                if (!_live.TryGetValue((memoryHandle, offset), out Range range))
                {
                    return;
                }

                range.IsMv = true;
                range.MvDesc = desc;
                _liveMv.Add(range);

                Logger.Warning?.Print(LogClass.Gpu,
                    $"[MEMALIAS] MV ALLOC: {desc} mem=0x{memoryHandle:X} off={offset} size={range.Size}");

                foreach (Range other in _live.Values)
                {
                    if (!ReferenceEquals(other, range) && Overlaps(range, other))
                    {
                        _overlaps++;
                        Logger.Warning?.Print(LogClass.Gpu,
                            $"[MEMALIAS] !! OVERLAP at MV birth: MV {desc} off={offset} size={range.Size} " +
                            $"overlaps live alloc mem=0x{other.Handle:X} off={other.Offset} size={other.Size} host={other.IsHost}");
                    }
                }
            }
        }

        private static void Heartbeat()
        {
            long now = Environment.TickCount64;
            if (now - _heartbeatMs < 5000)
            {
                return;
            }

            _heartbeatMs = now;
            Logger.Warning?.Print(LogClass.Gpu,
                $"[MEMALIAS/HB ~5s] liveAllocs={_live.Count} liveMv={_liveMv.Count} allocsTotal={_allocs} overlaps={_overlaps}");
        }
    }
}
