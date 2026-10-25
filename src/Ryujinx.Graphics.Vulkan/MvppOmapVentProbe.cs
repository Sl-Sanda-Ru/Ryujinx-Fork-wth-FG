
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Ryujinx.Graphics.Vulkan
{
    static class MvppOmapVentProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_OMAPVENT") == "1";

        private const uint NoMap = 0x10;

        internal sealed class Row
        {
            public long Draws;
            public int Bind;
            public VkFormat Fmt;
            public int W;
            public int H;
            public int StorageId;
            public uint Guest;
            public uint Omap;
        }

        private static readonly object _lock = new();
        private static readonly Dictionary<(int, VkFormat, int, int, int, uint, uint), Row> _rows = new();
        private static readonly Row[] _emptyRows = Array.Empty<Row>();
        private static bool _armedLogged;
        private static long _heartbeatMs;
        private static long _totalDraws;

        public static Row[] Resolve(FramebufferParams fb, ShaderCollection program, uint[] guestMasks, int guestMaskCount)
        {
            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[OMAPVENT] armed: draw-level ventilation by (rt, storage); omap fix = {(MvppOmapMaskVk.Enabled ? "ON" : "OFF")}");
            }

            if (fb == null)
            {
                return _emptyRows;
            }

            int map = (program != null && !program.IsCompute) ? program.MvppFragmentOutputMap : -1;
            int colorCount = fb.ColorAttachmentsCount;
            Row[] rows = new Row[colorCount];
            int count = 0;

            lock (_lock)
            {
                for (int a = 0; a < colorCount; a++)
                {
                    TextureView view = fb.MvppGetColorView(a);
                    if (view == null)
                    {
                        continue;
                    }

                    int bind = fb.AttachmentIndices[a];
                    uint guest = (uint)bind < (uint)guestMaskCount ? guestMasks[bind] & 0xFu : 0xFu;
                    uint omap = map >= 0 ? (uint)(map >> (bind * 4)) & 0xFu : NoMap;
                    int storageId = view.Storage != null ? RuntimeHelpers.GetHashCode(view.Storage) : 0;

                    (int, VkFormat, int, int, int, uint, uint) key =
                        (bind, view.VkFormat, view.Width, view.Height, storageId, guest, omap);

                    if (!_rows.TryGetValue(key, out Row row))
                    {
                        row = new Row
                        {
                            Bind = bind,
                            Fmt = view.VkFormat,
                            W = view.Width,
                            H = view.Height,
                            StorageId = storageId,
                            Guest = guest,
                            Omap = omap,
                        };
                        _rows.Add(key, row);
                    }

                    rows[count++] = row;
                }
            }

            if (count != rows.Length)
            {
                Array.Resize(ref rows, count);
            }

            return rows;
        }

        public static void OnDraw(Row[] rows)
        {
            Interlocked.Increment(ref _totalDraws);

            for (int i = 0; i < rows.Length; i++)
            {
                Interlocked.Increment(ref rows[i].Draws);
            }

            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _heartbeatMs) >= 5000)
            {
                Interlocked.Exchange(ref _heartbeatMs, now);
                DumpRows();
            }
        }

        private static void DumpRows()
        {
            Row[] snapshot;
            lock (_lock)
            {
                snapshot = new Row[_rows.Count];
                _rows.Values.CopyTo(snapshot, 0);
            }

            Array.Sort(snapshot, (a, b) =>
            {
                bool a720 = a.H == 720;
                bool b720 = b.H == 720;
                if (a720 != b720)
                {
                    return a720 ? -1 : 1;
                }

                return Interlocked.Read(ref b.Draws).CompareTo(Interlocked.Read(ref a.Draws));
            });

            Logger.Warning?.Print(LogClass.Gpu,
                $"[OMAPVENT/HB ~5s] totalDraws={Interlocked.Read(ref _totalDraws)} rows={snapshot.Length} (720p first, top 24)");

            int printed = 0;
            foreach (Row row in snapshot)
            {
                if (printed++ >= 24)
                {
                    break;
                }

                bool reduced = row.Omap != NoMap && (row.Guest & row.Omap) != row.Guest;
                string omapText = row.Omap == NoMap ? "none" : $"0x{row.Omap:X}";

                Logger.Warning?.Print(LogClass.Gpu,
                    $"[OMAPVENT]   rt={row.Bind} {row.Fmt} {row.W}x{row.H} storage=0x{row.StorageId:X8} " +
                    $"guest=0x{row.Guest:X} omap={omapText}{(reduced ? " REDUCED" : "")} draws={Interlocked.Read(ref row.Draws)}");
            }
        }
    }
}
