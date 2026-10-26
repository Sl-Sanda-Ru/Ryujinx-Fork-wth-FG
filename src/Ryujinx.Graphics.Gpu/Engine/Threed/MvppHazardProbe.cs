
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppHazardProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_TEX_HAZARD") == "1";

        private const int MaxTargets = 8;

        private static readonly ulong[] _rtAddr = new ulong[MaxTargets];
        private static readonly Image.Texture[] _rtTex = new Image.Texture[MaxTargets];
        private static int _rtCount;

        private static readonly Dictionary<string, int> _hits = new();
        private static long _lastLogMs;
        private static int _draws;
        private static int _hitDraws;

        public static void OnDraw(GpuChannel channel)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                OnDrawImpl(channel);
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"TEXHAZARD: disabled after unexpected error: {e}");
            }
        }

        private static void OnDrawImpl(GpuChannel channel)
        {
            _draws++;
            _rtCount = 0;

            channel.TextureManager.MvppEnumerateRenderTargets((index, tex) =>
            {
                if (tex != null && _rtCount < MaxTargets)
                {
                    _rtTex[_rtCount] = tex;
                    _rtAddr[_rtCount] = tex.Range.GetSubRange(0).Address;
                    _rtCount++;
                }
            });

            if (_rtCount == 0)
            {
                return;
            }

            bool hitThisDraw = false;

            channel.TextureManager.MvppEnumerateGraphicsInputsStage((stage, tex) =>
            {
                if (tex == null)
                {
                    return;
                }

                ulong addr = tex.Range.GetSubRange(0).Address;

                for (int i = 0; i < _rtCount; i++)
                {
                    if (_rtAddr[i] != addr)
                    {
                        continue;
                    }

                    hitThisDraw = true;

                    string key =
                        $"{tex.Info.Width}x{tex.Info.Height} {tex.Info.FormatInfo.Format} " +
                        $"{tex.Info.Target} @{addr:X10} (rt{i}, stage{stage})";

                    _hits.TryGetValue(key, out int n);
                    _hits[key] = n + 1;
                }
            });

            if (hitThisDraw)
            {
                _hitDraws++;
            }

            long now = Environment.TickCount64;

            if (now - _lastLogMs < 5000)
            {
                return;
            }

            _lastLogMs = now;

            System.Text.StringBuilder sb = new();
            sb.Append($"TEXHAZARD: {_hitDraws}/{_draws} draws sample a bound render target");

            if (_hits.Count == 0)
            {
                sb.Append(" -- NONE (mechanism ruled out for this window).");
            }
            else
            {
                sb.Append($", {_hits.Count} distinct textures:");
                int shown = 0;

                foreach (KeyValuePair<string, int> kv in _hits)
                {
                    if (shown++ >= 6)
                    {
                        sb.Append($" (+{_hits.Count - 6} others)");

                        break;
                    }

                    sb.Append($" [{kv.Key} x{kv.Value}]");
                }
            }

            Logger.Info?.Print(LogClass.Gpu, sb.ToString());

            _draws = 0;
            _hitDraws = 0;
            _hits.Clear();
        }
    }
}
