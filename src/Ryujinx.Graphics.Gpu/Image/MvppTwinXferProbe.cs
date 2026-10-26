
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppTwinXferProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_TWINXFER") == "1";

        private static bool _armedLogged;
        private static long _copies;
        private static long _imageBinds;
        private static long _dmaHits;
        private static long _summaryMs;

        private static readonly HashSet<(ulong, ulong, string)> _copyPairs = new();
        private static readonly HashSet<(ulong, ShaderStage, int, bool)> _imageTuples = new();
        private static readonly HashSet<(ulong, ulong, bool)> _dmaPairs = new();

        private const ulong TwinWindow = 0x400000;

        private static bool IsMv(Texture t)
        {
            return MvppMvSyncProbe.IsMvBuffer(t);
        }

        private static ulong Va(Texture t)
        {
            return t.Range.GetSubRange(0).Address;
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
                    "[TWINXFER] ARMED (RYUJINX_TWINXFER=1). Watching engine copies and storage-image bindings touching the MV twins.");
            }

            long now = Environment.TickCount64;
            if (now - _summaryMs >= 3000)
            {
                _summaryMs = now;
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[TWINXFER/HEARTBEAT ~3s] copies={_copies} distinctPairs={_copyPairs.Count} | imageBinds={_imageBinds} distinctTuples={_imageTuples.Count} | " +
                    $"dmaHits={_dmaHits} distinctDma={_dmaPairs.Count} (known twins: {MvppTwinMapProbe.TwinVas().Length})");
            }
        }

        public static void OnCopy(string path, Texture src, Texture dst)
        {
            if (!Enabled || (!IsMv(src) && !IsMv(dst)))
            {
                return;
            }

            _copies++;

            ulong srcVa = src != null ? Va(src) : 0;
            ulong dstVa = dst != null ? Va(dst) : 0;

            bool isNew;
            lock (_copyPairs)
            {
                isNew = _copyPairs.Add((srcVa, dstVa, path));
            }

            if (isNew)
            {
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[TWINXFER] COPY via {path}: src@0x{srcVa:X} ({src?.Info.FormatInfo.Format}) -> dst@0x{dstVa:X} ({dst?.Info.FormatInfo.Format}) | distinct pairs: {_copyPairs.Count}");
            }
        }

        public static void OnDma(ulong srcVa, ulong dstVa, int xCount, int yCount, bool copy2D, bool srcLinear, bool dstLinear)
        {
            if (!Enabled)
            {
                return;
            }

            ulong[] twins = MvppTwinMapProbe.TwinVas();
            if (twins.Length == 0)
            {
                return;
            }

            bool hit = false;
            foreach (ulong twin in twins)
            {
                if ((srcVa >= twin && srcVa < twin + TwinWindow) ||
                    (dstVa >= twin && dstVa < twin + TwinWindow))
                {
                    hit = true;
                    break;
                }
            }

            if (!hit)
            {
                return;
            }

            _dmaHits++;

            bool isNew;
            lock (_copyPairs)
            {
                isNew = _dmaPairs.Add((srcVa, dstVa, copy2D));
            }

            if (isNew)
            {
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[TWINXFER] *** DMA TOUCHING A TWIN ***: src=0x{srcVa:X} -> dst=0x{dstVa:X} x={xCount} y={yCount} copy2D={copy2D} srcLinear={srcLinear} dstLinear={dstLinear} | distinct: {_dmaPairs.Count}");
            }
        }

        public static void OnImageBind(Texture texture, ShaderStage stage, int handle, bool isStore)
        {
            if (!Enabled || !IsMv(texture))
            {
                return;
            }

            _imageBinds++;

            ulong va = Va(texture);
            bool isNew;
            lock (_copyPairs)
            {
                isNew = _imageTuples.Add((va, stage, handle, isStore));
            }

            if (isNew)
            {
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[TWINXFER] IMAGE BIND of twin@0x{va:X}: stage={stage} handle=0x{handle:X} store={(isStore ? "YES (writer!)" : "no")} | distinct: {_imageTuples.Count}");
            }
        }
    }
}
