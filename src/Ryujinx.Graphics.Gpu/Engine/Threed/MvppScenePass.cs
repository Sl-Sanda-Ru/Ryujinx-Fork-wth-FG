
using Ryujinx.Common.Logging;
using System;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppScenePass
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_SCENEPASS") == "1";

        private const int MissesBeforeCatchUp = 2;
        private const int CatchUpFrames = 300;
        private const int CatchUpsBeforeDisarm = 5;
        private const int ReportMs = 5000;

        private static bool _disarmed;
        private static int _missStreak;
        private static int _catchUpLeft;
        private static int _catchUps;

        private static bool _qualThisFrame;

        private static int _frames;
        private static int _captured;
        private static int _skips;
        private static int _catchUpFramesSeen;
        private static long _lastReportMs;

        public static bool Allows(GpuChannel channel)
        {
            _qualThisFrame = true;

            if (_disarmed || _catchUpLeft > 0)
            {
                return true;
            }

            if (IsSceneColorPass(channel))
            {
                return true;
            }

            _skips++;

            return false;
        }

        internal static bool IsSceneColorPass(GpuChannel channel)
        {
            Image.Texture col0 = channel.TextureManager.RenderTargetColor0;

            if (col0 == null || col0.Info.Width == col0.Info.Height)
            {
                return false;
            }

            GAL.Format f = col0.Info.FormatInfo.Format;

            return f == GAL.Format.R11G11B10Float || f == GAL.Format.R16G16B16A16Float;
        }

        public static void OnFrame(bool captured)
        {
            if (!_qualThisFrame)
            {
                return;
            }

            _qualThisFrame = false;
            _frames++;

            if (_catchUpLeft > 0)
            {
                _catchUpLeft--;
                _catchUpFramesSeen++;
            }

            if (captured)
            {
                _captured++;
                _missStreak = 0;
            }
            else if (!_disarmed && _catchUpLeft == 0)
            {
                _missStreak++;

                if (_missStreak >= MissesBeforeCatchUp)
                {
                    _missStreak = 0;
                    _catchUps++;
                    _catchUpLeft = CatchUpFrames;

                    if (_catchUps >= CatchUpsBeforeDisarm)
                    {
                        _disarmed = true;

                        Logger.Warning?.Print(LogClass.Gpu,
                            $"MVPP SCENEPASS DESARME : {_catchUps} rattrapages, la passe de scene HDR " +
                            "n'est pas assez fiable sur ce jeu pour porter la capture. Retour au " +
                            "comportement d'avant pour le reste de la session.");
                    }
                    else
                    {
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP SCENEPASS rattrapage #{_catchUps} : {MissesBeforeCatchUp} images " +
                            $"d'affilee sans capture, restriction levee pour {CatchUpFrames} images.");
                    }
                }
            }

            long now = Environment.TickCount64;

            if (now - _lastReportMs >= ReportMs)
            {
                _lastReportMs = now;

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP SCENEPASS : {_frames} images 3D, {_captured} avec camera " +
                    $"({(_frames > 0 ? 100f * _captured / _frames : 0f):0.#} %), " +
                    $"{_skips} dessins sautes, {_catchUps} rattrapages " +
                    $"({_catchUpFramesSeen} images en rattrapage)" +
                    $"{(_disarmed ? " · DESARME" : "")}");
            }
        }
    }
}
