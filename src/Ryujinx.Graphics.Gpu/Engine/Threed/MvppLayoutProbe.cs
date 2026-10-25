
using Ryujinx.Common.Logging;
using System;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppLayoutProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_LAYOUT") == "1";

        private static bool _announced;
        private static long _nextMs;
        private static string _lastSig = "";

        public static void Compare(Image.TextureInfo want, Image.Texture got)
        {
            if (!_enabled || got == null)
            {
                return;
            }

            try
            {
                if (!_announced)
                {
                    _announced = true;
                    Logger.Info?.Print(LogClass.Gpu,
                        "MVPP LAYOUT: ON -- demande (jeu) vs fourni (cache) pour la texture presentee, lecture seule.");
                }

                Image.TextureInfo g = got.Info;

                bool linMismatch = want.IsLinear != g.IsLinear;
                bool gobMismatch = want.GobBlocksInY != g.GobBlocksInY;
                bool strideMismatch = want.Stride != g.Stride;
                bool dimMismatch = want.Width != g.Width || want.Height != g.Height;
                bool fmtMismatch = want.FormatInfo.Format != g.FormatInfo.Format;

                bool any = linMismatch || gobMismatch || strideMismatch || dimMismatch || fmtMismatch;

                string sig =
                    $"DEMANDE {want.Width}x{want.Height} lin={want.IsLinear} gobY={want.GobBlocksInY} stride={want.Stride} {want.FormatInfo.Format} " +
                    $"|| FOURNI {g.Width}x{g.Height} lin={g.IsLinear} gobY={g.GobBlocksInY} stride={g.Stride} {g.FormatInfo.Format}" +
                    $"{(any ? "  <<< DESACCORD:" : "  (accord)")}" +
                    $"{(linMismatch ? " isLinear" : "")}{(gobMismatch ? " gobBlocksInY" : "")}{(strideMismatch ? " stride" : "")}" +
                    $"{(dimMismatch ? " dimensions" : "")}{(fmtMismatch ? " format" : "")}";

                long now = Environment.TickCount64;

                if (sig != _lastSig || now >= _nextMs)
                {
                    _lastSig = sig;
                    _nextMs = now + 2000;
                    Logger.Info?.Print(LogClass.Gpu, $"MVPP LAYOUT: {sig}");
                }
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP LAYOUT: desactive apres erreur: {e.Message}");
            }
        }
    }
}
