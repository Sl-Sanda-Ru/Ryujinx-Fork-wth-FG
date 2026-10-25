
using Ryujinx.Common.Logging;
using System;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppLdrSkip
    {
        public static bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_NOLDR") == "1";

        /// <summary>
        /// Re-reads the fields above that come from the settings profile, so a changed upscaler
        /// setting applies at the next game start without restarting the app.
        /// </summary>
        internal static void ReloadProfileSettings()
        {
            {
                Enabled = Environment.GetEnvironmentVariable("RYUJINX_MVPP_NOLDR") == "1";
            }
        }

        public static readonly bool SkipRank1 =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_NORANK1") == "1";

        public static readonly bool AbMode =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_NOLDR_AB") == "1";

        public static readonly bool MissWhy =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_MISSWHY") == "1";

        public static bool Active => Enabled || SkipRank1 || AbMode;

        private const int WarmCaptures = 200;

        private const int CalibrationFrames = 600;
        private const int WindowFrames = 600;
        private const float MarginPoints = 0.10f;
        private const int ReportMs = 5000;

        private const int SuspendFrames = 3000;
        private const int SuspendsBeforeGiveUp = 4;

        private const int PhaseWarming = 0;
        private const int PhaseCalibrating = 1;
        private const int PhaseArmed = 2;
        private const int PhaseDisarmed = 3;
        private const int PhaseSuspended = 4;

        private static int _suspendLeft;
        private static int _suspends;

        private static int _phase = PhaseWarming;
        private static int _warmCaptured;
        private static float _baseMissRate;
        private static float _lastWinMissRate = -1f;

        private static int _winFrames;
        private static int _winMisses;

        private static bool _qualThisFrame;
        private static int _skipsLdr;
        private static int _skipsRank;
        private static int _attempt;
        private static int _frameMark;
        private static int _seenMark;

        private static int _frSkipped;
        private static int _frAllowed;

        private static int _missA;
        private static int _missB;
        private static int _missC;
        private static long _lastReportMs;

        private static bool _abArmed;
        private static int _abWin;
        private static bool _abStarted;
        private static readonly int[] _abFrames = new int[2];
        private static readonly int[] _abMisses = new int[2];
        private static readonly int[] _abReads = new int[2];
        private static readonly int[] _abIntruders = new int[2];

        internal static bool ArmedNow => AbMode ? _abArmed : _phase == PhaseArmed;

        public static bool Allows(GpuChannel channel)
        {
            _qualThisFrame = true;

            int mark = _frameMark;

            if (_seenMark != mark)
            {
                _seenMark = mark;
                _attempt = 0;
                _frSkipped = 0;
                _frAllowed = 0;
            }

            _attempt++;

            if (!ArmedNow)
            {
                return true;
            }

            if (SkipRank1 && _attempt == 1)
            {
                _skipsRank++;
                _frSkipped++;

                return false;
            }

            if (Enabled && IsIntegerColorPass(channel))
            {
                _skipsLdr++;
                _frSkipped++;

                return false;
            }

            _frAllowed++;

            return true;
        }

        private static bool IsIntegerColorPass(GpuChannel channel)
        {
            Image.Texture col0 = channel.TextureManager.RenderTargetColor0;

            if (col0 == null)
            {
                return false;
            }

            return !IsFloat(col0.Info.FormatInfo.Format);
        }

        private static bool IsFloat(GAL.Format f)
        {
            switch (f)
            {
                case GAL.Format.R16Float:
                case GAL.Format.R32Float:
                case GAL.Format.R16G16Float:
                case GAL.Format.R32G32Float:
                case GAL.Format.R16G16B16Float:
                case GAL.Format.R32G32B32Float:
                case GAL.Format.R16G16B16A16Float:
                case GAL.Format.R32G32B32A32Float:
                case GAL.Format.R11G11B10Float:
                case GAL.Format.R9G9B9E5Float:
                    return true;
                default:
                    return false;
            }
        }

        internal static void NoteRead(bool intruder)
        {
            if (!AbMode || !_abStarted)
            {
                return;
            }

            int p = _abArmed ? 1 : 0;

            _abReads[p]++;

            if (intruder)
            {
                _abIntruders[p]++;
            }
        }

        public static void OnFrame(bool captured)
        {
            if (!_qualThisFrame)
            {
                return;
            }

            _qualThisFrame = false;

            if (MissWhy && !captured && ArmedNow)
            {
                if (_frSkipped == 0)
                {
                    _missC++;
                }
                else if (_frAllowed == 0)
                {
                    _missA++;
                }
                else
                {
                    _missB++;
                }
            }

            _frameMark++;

            if (_phase == PhaseWarming)
            {
                if (captured)
                {
                    _warmCaptured++;
                }

                if (_warmCaptured < WarmCaptures)
                {
                    return;
                }

                if (AbMode)
                {
                    _abStarted = true;
                    _abArmed = true;

                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP NOLDR A/B : camera chaude ({WarmCaptures} captures), debut de " +
                        $"l'alternance par tranches de {WindowFrames} images. La restriction " +
                        "s'allume et s'eteint toute seule ; les deux moities se comparent sur la " +
                        "MEME scene et le MEME geste.");
                }
                else
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP NOLDR : camera chaude ({WarmCaptures} captures), debut du calibrage " +
                        $"sur {CalibrationFrames} images SANS restriction.");
                }

                _phase = AbMode ? PhaseArmed : PhaseCalibrating;

                return;
            }

            if (AbMode)
            {
                int p = _abArmed ? 1 : 0;

                _abFrames[p]++;

                if (!captured)
                {
                    _abMisses[p]++;
                }

                if (++_abWin >= WindowFrames)
                {
                    _abWin = 0;
                    _abArmed = !_abArmed;
                }

                ReportAb();

                return;
            }

            if (_phase == PhaseSuspended)
            {
                if (--_suspendLeft <= 0)
                {
                    _phase = PhaseCalibrating;
                    _winFrames = 0;
                    _winMisses = 0;

                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP NOLDR fin de suspension #{_suspends} : recalibrage sur " +
                        $"{CalibrationFrames} images ICI, puis nouvelle tentative.");
                }

                ReportPlain();

                return;
            }

            _winFrames++;

            if (!captured)
            {
                _winMisses++;
            }

            if (_phase == PhaseCalibrating && _winFrames >= CalibrationFrames)
            {
                _baseMissRate = (float)_winMisses / _winFrames;
                _phase = PhaseArmed;
                _winFrames = 0;
                _winMisses = 0;

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP NOLDR calibrage termine : ratage de reference {100f * _baseMissRate:0.#} % " +
                    $"sur {CalibrationFrames} images, camera chaude, mesure ICI sans restriction. " +
                    $"Restriction ARMEE ; coupure si une fenetre de {WindowFrames} images depasse ce " +
                    $"taux de plus de {100f * MarginPoints:0} points.");

                return;
            }

            if (_phase == PhaseArmed && _winFrames >= WindowFrames)
            {
                float rate = (float)_winMisses / _winFrames;
                _lastWinMissRate = rate;
                _winFrames = 0;
                _winMisses = 0;

                if (rate > _baseMissRate + MarginPoints)
                {
                    _suspends++;

                    if (_suspends >= SuspendsBeforeGiveUp)
                    {
                        _phase = PhaseDisarmed;

                        Logger.Warning?.Print(LogClass.Gpu,
                            $"MVPP NOLDR ABANDON : {_suspends}e exces (ratage {100f * rate:0.#} % contre " +
                            $"{100f * _baseMissRate:0.#} % calibre). Ecarter la passe 8 bits affame la camera " +
                            "de facon repetee sur ce jeu. Retour au comportement d'avant pour le reste de " +
                            "la session, et il faut le savoir : le correctif ne tient pas ici.");
                    }
                    else
                    {
                        _phase = PhaseSuspended;
                        _suspendLeft = SuspendFrames;

                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP NOLDR suspendu #{_suspends} : ratage {100f * rate:0.#} % contre " +
                            $"{100f * _baseMissRate:0.#} % calibre (+{100f * (rate - _baseMissRate):0.#} points, " +
                            $"marge {100f * MarginPoints:0}). Restriction levee pour {SuspendFrames} images, " +
                            "puis RECALIBRAGE sur place et nouvelle tentative -- la famine est peut-etre " +
                            "propre a cette scene.");
                    }
                }
            }

            ReportPlain();
        }

        private static void ReportAb()
        {
            long now = Environment.TickCount64;

            if (now - _lastReportMs < ReportMs)
            {
                return;
            }

            _lastReportMs = now;

            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP NOLDR A/B [tranche en cours : {(_abArmed ? "AVEC" : "SANS")}] · " +
                $"ecartes : {_skipsRank} au rang 1, {_skipsLdr} en 8 bits");

            Dump("AVEC restriction", 1);
            Dump("SANS restriction", 0);
        }

        private static void Dump(string label, int p)
        {
            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP NOLDR A/B   {label} : {_abFrames[p]} images · " +
                $"ratage {(_abFrames[p] > 0 ? 100f * _abMisses[p] / _abFrames[p] : 0f):0.#} % · " +
                $"{_abReads[p]} lectures · {_abIntruders[p]} intruses " +
                $"({(_abReads[p] > 0 ? 100f * _abIntruders[p] / _abReads[p] : 0f):0.##} %)");
        }

        private static string Share(int n, int total)
        {
            return total > 0 ? $"{100f * n / total:0}%" : "-";
        }

        private static void ReportPlain()
        {
            long now = Environment.TickCount64;

            if (now - _lastReportMs < ReportMs)
            {
                return;
            }

            _lastReportMs = now;

            string phase = _phase switch
            {
                PhaseWarming => $"CHAUFFE ({_warmCaptured}/{WarmCaptures})",
                PhaseCalibrating => $"CALIBRAGE ({_winFrames}/{CalibrationFrames})",
                PhaseArmed => "ARME",
                PhaseSuspended => $"SUSPENDU ({_suspendLeft} img restantes, exces #{_suspends})",
                _ => $"ABANDONNE apres {_suspends} exces",
            };

            if (MissWhy)
            {
                int tot = _missA + _missB + _missC;

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP MISSWHY : {tot} images ratees avec restriction armee · " +
                    $"A (aucune alternative, la restriction EST la cause) {_missA} " +
                    $"({Share(_missA, tot)}) · " +
                    $"B (alternatives tentees et echouees, AMBIGU) {_missB} ({Share(_missB, tot)}) · " +
                    $"C (rien d'ecarte, ratage de fond) {_missC} ({Share(_missC, tot)})");
            }

            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP NOLDR [{phase}] : ecartes {_skipsRank} au rang 1 / {_skipsLdr} en 8 bits · ratage calibre " +
                $"{(_phase <= PhaseCalibrating ? "(en cours)" : $"{100f * _baseMissRate:0.#} %")}" +
                $"{(_lastWinMissRate >= 0f ? $" · derniere fenetre {100f * _lastWinMissRate:0.#} %" : "")}");
        }
    }
}
