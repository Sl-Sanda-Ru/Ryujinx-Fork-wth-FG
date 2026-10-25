
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppGlowProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_GLOW_PROBE") == "1";

        private static readonly Dictionary<string, int> _boxes = new();
        private static readonly Dictionary<string, int> _inputs = new();
        private static long _lastLogMs;
        private static int _drawsToGlow;
        private static int _scissored;

        public static void OnDraw(GpuChannel channel, ref ThreedClassState state)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                OnDrawImpl(channel, ref state);
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"GLOWPROBE: disabled after unexpected error: {e}");
            }
        }

        private static void OnDrawImpl(GpuChannel channel, ref ThreedClassState state)
        {
            Image.Texture c0 = channel.TextureManager.RenderTargetColor0;

            if (c0 == null || !c0.Info.FormatInfo.Format.ToString().StartsWith("R11G11B10", StringComparison.Ordinal))
            {
                return;
            }

            _drawsToGlow++;

            ScissorState sc = state.ScissorState[0];
            ScreenScissorState screen = state.ScreenScissorState;

            int w = c0.Info.Width;
            int h = c0.Info.Height;

            bool boxed = sc.Enable && (sc.X1 > 0 || sc.Y1 > 0 || sc.X2 < w || sc.Y2 < h);

            if (boxed)
            {
                _scissored++;

                string key = $"{sc.X1 / 8 * 8},{sc.Y1 / 8 * 8} -> {sc.X2 / 8 * 8},{sc.Y2 / 8 * 8} " +
                             $"({(sc.X2 - sc.X1) / 8 * 8}x{(sc.Y2 - sc.Y1) / 8 * 8})";

                _boxes.TryGetValue(key, out int n);
                _boxes[key] = n + 1;
            }

            channel.TextureManager.MvppEnumerateGraphicsInputsWithSampler((stage, tex, smp) =>
            {
                if (tex == null || smp == null || _inputs.Count >= 24)
                {
                    return;
                }

                string k = $"{tex.Info.Width}x{tex.Info.Height} {tex.Info.FormatInfo.Format} " +
                           $"U={smp.ProbeAddressU} V={smp.ProbeAddressV} borderA={smp.ProbeBorderA:0.##} " +
                           $"min={smp.ProbeMinFilter}";

                _inputs.TryGetValue(k, out int c);
                _inputs[k] = c + 1;
            });

            long now = Environment.TickCount64;

            if (now - _lastLogMs < 5000)
            {
                return;
            }

            _lastLogMs = now;

            System.Text.StringBuilder sb = new();
            sb.Append($"GLOWPROBE: {_drawsToGlow} draws vers le buffer HDR {w}x{h}, {_scissored} avec une boite de decoupe");
            sb.Append($" (ecran {screen.X},{screen.Y} {screen.Width}x{screen.Height})");

            if (_boxes.Count == 0)
            {
                sb.Append(" -- AUCUNE boite: les rectangles ne viennent PAS du scissor.");
            }
            else
            {
                sb.Append($", {_boxes.Count} boites distinctes:");
                int shown = 0;

                foreach (KeyValuePair<string, int> kv in _boxes)
                {
                    if (shown++ >= 8)
                    {
                        sb.Append($" (+{_boxes.Count - 8})");

                        break;
                    }

                    sb.Append($" [{kv.Key} x{kv.Value}]");
                }
            }

            if (_inputs.Count > 0)
            {
                System.Text.StringBuilder ib = new("GLOWPROBE textures lues par ces draws:");
                int k = 0;

                foreach (KeyValuePair<string, int> kv in _inputs)
                {
                    if (k++ >= 10)
                    {
                        ib.Append($" (+{_inputs.Count - 10})");

                        break;
                    }

                    ib.Append($"\n    [{kv.Key}] x{kv.Value}");
                }

                Logger.Info?.Print(LogClass.Gpu, ib.ToString());
            }

            Logger.Info?.Print(LogClass.Gpu, sb.ToString());

            _inputs.Clear();
            _drawsToGlow = 0;
            _scissored = 0;
            _boxes.Clear();
        }
    }
}
