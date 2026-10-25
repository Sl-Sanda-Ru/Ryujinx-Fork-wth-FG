
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppCacheProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_CACHEOPS") == "1";

        private static long _nextLogMs;
        private static int _copyDeps;
        private static int _syncs;
        private static int _mismatch;
        private static int _realSyncs;
        private static int _realNoData;
        private static readonly Dictionary<string, int> _realShapes = new();
        private static readonly Dictionary<string, int> _pairs = new();

        public static void OnRealSync(int w, int h, Format fmt, bool hadData)
        {
            if (!_enabled)
            {
                return;
            }

            _realSyncs++;

            if (!hadData)
            {
                _realNoData++;
            }

            string key = $"{w}x{h} {fmt}";
            _realShapes[key] = _realShapes.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        public static void OnCopyDependency(Texture a, Texture b)
        {
            if (!_enabled || a == null || b == null)
            {
                return;
            }

            try
            {
                _copyDeps++;

                bool diff = a.Info.Width != b.Info.Width || a.Info.Height != b.Info.Height ||
                            a.Info.FormatInfo.Format != b.Info.FormatInfo.Format;

                if (diff)
                {
                    _mismatch++;
                }

                string key = $"{a.Info.Width}x{a.Info.Height} {a.Info.FormatInfo.Format} <-> {b.Info.Width}x{b.Info.Height} {b.Info.FormatInfo.Format}{(diff ? "  <<< DIFFERENT" : "")}";
                _pairs[key] = _pairs.TryGetValue(key, out int n) ? n + 1 : 1;
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP CACHEOPS: desactive apres erreur: {e.Message}");
            }
        }

        public static void OnSynchronize()
        {
            if (_enabled)
            {
                _syncs++;
            }
        }

        public static void OnPresent()
        {
            if (!_enabled)
            {
                return;
            }

            long now = Environment.TickCount64;

            if (now < _nextLogMs)
            {
                return;
            }

            _nextLogMs = now + 2000;

            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP CACHEOPS: {_copyDeps} copies dep ({_mismatch} DIFFERENTES) | {_syncs} appels sync dont " +
                $"{_realSyncs} RECHARGENT ({_realNoData} sans donnees prealables) sur {_realShapes.Count} formes.");

            int shown = 0;

            foreach (KeyValuePair<string, int> kv in _realShapes)
            {
                if (shown++ >= 10)
                {
                    break;
                }

                Logger.Info?.Print(LogClass.Gpu, $"MVPP CACHEOPS:   recharge x{kv.Value}  {kv.Key}");
            }

            _copyDeps = 0;
            _syncs = 0;
            _mismatch = 0;
            _realSyncs = 0;
            _realNoData = 0;
            _pairs.Clear();
            _realShapes.Clear();
        }
    }
}
