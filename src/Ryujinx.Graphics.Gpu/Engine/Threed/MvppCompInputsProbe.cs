
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppCompInputsProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_COMPIN") == "1";

        private static bool _announced;
        private static readonly HashSet<string> _seen = new();

        private static bool RtSized(Image.TextureInfo i)
        {
            bool sized = (i.Width == 1280 && i.Height == 720) ||
                         (i.Width == 640 && i.Height == 360) ||
                         (i.Width == 512 && i.Height == 288);

            string fmt = i.FormatInfo.Format.ToString();
            bool uncompressed = !fmt.StartsWith("Bc", StringComparison.Ordinal) &&
                                !fmt.StartsWith("Astc", StringComparison.Ordinal);

            return sized && uncompressed;
        }

        private static void Note(Image.Texture tex, string role)
        {
            if (tex == null)
            {
                return;
            }

            Image.TextureInfo i = tex.Info;

            if (!RtSized(i))
            {
                return;
            }

            string sig = $"{role} {i.Width}x{i.Height} {i.FormatInfo.Format} gobY={i.GobBlocksInY} lin={i.IsLinear} stride={i.Stride}";

            if (_seen.Add(sig))
            {
                Logger.Info?.Print(LogClass.Gpu, $"MVPP COMPIN: {sig}");
            }
        }

        public static void OnDraw(GpuChannel channel, ref ThreedClassState state)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                if (!_announced)
                {
                    _announced = true;
                    Logger.Info?.Print(LogClass.Gpu,
                        "MVPP COMPIN: ON -- gobBlocksInY census of full-res textures (targets + inputs).");
                }

                channel.TextureManager.MvppEnumerateRenderTargets((slot, rt) => Note(rt, $"TARGET slot{slot}"));
                channel.TextureManager.MvppEnumerateGraphicsInputs(tex => Note(tex, "INPUT"));
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP COMPIN: disabled after error: {e.Message}");
            }
        }
    }
}
