
using Ryujinx.Common.Logging;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Memory
{
    static class MvppPalProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_PAL_PROBE") == "1";

        private const ulong MinTrackedSize = 0x1000;
        private const int MaxTracked = 32;
        private const int HashStride = 4093;
        private const int AnomalyLogCap = 24;

        private static bool _armedLogged;
        private static long _frame;

        private sealed class Rec
        {
            public ulong Address;
            public ulong Size;
            public uint LastHash;
            public bool HashValid;
            public bool SeenThisFrame;
            public bool ChangedThisFrame;
            public int UploadsThisFrame;
            public long FramesSeen;
            public long FramesChanged;
            public long FramesChangedWithUpload;
            public long FramesChangedNoUpload;
        }

        private static readonly Dictionary<ulong, Rec> _tracked = new();
        private static readonly object _lock = new();
        private static long _anomaliesLogged;
        private static long _summaryMs;

        public static void OnBind(int stage, int index, MultiRange range, PhysicalMemory pm)
        {
            if (!Enabled || stage != 0 || index > 5 || pm == null)
            {
                return;
            }

            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    "[PAL] ARMED v2 (RYUJINX_PAL_PROBE=1). Tracking big vertex SSBOs (slots 0/1, >=64KiB) BY ADDRESS: strided guest hash vs host uploads, per frame.");
            }

            MemoryRange sub = range.GetSubRange(0);
            if (sub.Address == MemoryManager.PteUnmapped || sub.Size < MinTrackedSize)
            {
                return;
            }

            lock (_lock)
            {
                if (!_tracked.TryGetValue(sub.Address, out Rec rec))
                {
                    if (_tracked.Count >= MaxTracked)
                    {
                        return;
                    }

                    _tracked[sub.Address] = rec = new Rec { Address = sub.Address, Size = sub.Size };

                    Logger.Warning?.Print(LogClass.Gpu,
                        $"[PAL] TRACKING palette 0x{sub.Address:X} size 0x{sub.Size:X} (slot {index}, {_tracked.Count} tracked)");
                }

                if (rec.SeenThisFrame)
                {
                    return;
                }

                rec.SeenThisFrame = true;
                rec.FramesSeen++;

                ReadOnlySpan<byte> data = pm.GetSpan(rec.Address, (int)rec.Size);

                uint hash = 2166136261;
                for (int i = 0; i < data.Length; i += HashStride)
                {
                    hash = (hash ^ data[i]) * 16777619;
                }

                rec.ChangedThisFrame = rec.HashValid && hash != rec.LastHash;
                rec.LastHash = hash;
                rec.HashValid = true;
            }
        }

        private static readonly HashSet<(ulong, int, int)> _computeBinds = new();

        public static void OnComputeBind(int index, MultiRange range, int flags)
        {
            if (!Enabled)
            {
                return;
            }

            MemoryRange sub = range.GetSubRange(0);

            lock (_lock)
            {
                foreach (Rec rec in _tracked.Values)
                {
                    if (sub.Address < rec.Address + rec.Size &&
                        rec.Address < sub.Address + sub.Size)
                    {
                        if (_computeBinds.Add((rec.Address, index, flags)))
                        {
                            Logger.Warning?.Print(LogClass.Gpu,
                                $"[PAL] COMPUTE BINDS palette 0x{rec.Address:X} at c-slot {index} flags=0x{flags:X} (bind range 0x{sub.Address:X}+0x{sub.Size:X})");
                        }
                    }
                }
            }
        }

        private static readonly HashSet<(ulong, int)> _xfbBinds = new();
        private static readonly HashSet<(ulong, ulong)> _copyHits = new();

        public static void OnXfbBind(int index, MultiRange range)
        {
            if (!Enabled)
            {
                return;
            }

            MemoryRange sub = range.GetSubRange(0);

            lock (_lock)
            {
                foreach (Rec rec in _tracked.Values)
                {
                    if (sub.Address < rec.Address + rec.Size &&
                        rec.Address < sub.Address + sub.Size)
                    {
                        if (_xfbBinds.Add((rec.Address, index)))
                        {
                            Logger.Warning?.Print(LogClass.Gpu,
                                $"[PAL] *** TRANSFORM FEEDBACK WRITES palette 0x{rec.Address:X} *** (xfb slot {index}, bind 0x{sub.Address:X}+0x{sub.Size:X})");
                        }
                    }
                }
            }
        }

        public static void OnGpuCopy(ulong dstAddress, ulong size)
        {
            if (!Enabled)
            {
                return;
            }

            lock (_lock)
            {
                foreach (Rec rec in _tracked.Values)
                {
                    if (dstAddress < rec.Address + rec.Size &&
                        rec.Address < dstAddress + size)
                    {
                        if (_copyHits.Add((rec.Address, dstAddress)))
                        {
                            Logger.Warning?.Print(LogClass.Gpu,
                                $"[PAL] *** GPU COPY WRITES palette 0x{rec.Address:X} *** (dst 0x{dstAddress:X}+0x{size:X})");
                        }
                    }
                }
            }
        }

        public static void OnUpload(ulong address, ulong size, int site = 0, PhysicalMemory pm = null)
        {
            MvppUpVolProbe.OnUpload(address, size, site, pm);

            if (!Enabled)
            {
                return;
            }

            lock (_lock)
            {
                foreach (Rec rec in _tracked.Values)
                {
                    if (rec.SeenThisFrame &&
                        address < rec.Address + rec.Size &&
                        rec.Address < address + size)
                    {
                        rec.UploadsThisFrame++;
                    }
                }
            }
        }

        public static void OnPresent()
        {
            if (!Enabled)
            {
                return;
            }

            lock (_lock)
            {
                _frame++;

                foreach (Rec rec in _tracked.Values)
                {
                    if (rec.SeenThisFrame && rec.ChangedThisFrame)
                    {
                        rec.FramesChanged++;

                        if (rec.UploadsThisFrame > 0)
                        {
                            rec.FramesChangedWithUpload++;
                        }
                        else
                        {
                            rec.FramesChangedNoUpload++;

                            if (_anomaliesLogged++ < AnomalyLogCap)
                            {
                                Logger.Warning?.Print(LogClass.Gpu,
                                    $"[PAL] frame {_frame}: palette 0x{rec.Address:X} guest CHANGED but ZERO host upload *** STALE DATA SERVED ***");
                            }
                        }
                    }

                    rec.SeenThisFrame = false;
                    rec.ChangedThisFrame = false;
                    rec.UploadsThisFrame = 0;
                }

                long now = Environment.TickCount64;
                if (now - _summaryMs >= 3000 && _frame > 0)
                {
                    _summaryMs = now;

                    var sb = new System.Text.StringBuilder();
                    foreach (Rec rec in _tracked.Values)
                    {
                        if (rec.FramesSeen > 0)
                        {
                            sb.Append($"0x{rec.Address:X}: seen {rec.FramesSeen}, changed {rec.FramesChanged} (ok {rec.FramesChangedWithUpload}, NOUP {rec.FramesChangedNoUpload}); ");
                        }
                    }

                    Logger.Warning?.Print(LogClass.Gpu, $"[PAL/SUMMARY ~3s] frames={_frame} | {sb}");
                }
            }
        }
    }
}
