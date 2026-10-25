
using Ryujinx.Common.Logging;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppHangWatch
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_HANGWATCH") == "1";

        private const long StallMs = 3000;

        private const long RepeatMs = 5000;

        private static long _lastPresentMs;
        private static int _started;

        private static long _stallLoggedMs;
        private static int _drawsAtStall;
        private static bool _inStall;

        public static void Ping()
        {
            Volatile.Write(ref _lastPresentMs, Environment.TickCount64);

            if (Volatile.Read(ref _started) == 0 && Interlocked.Exchange(ref _started, 1) == 0)
            {
                Thread t = new(Loop)
                {
                    Name = "MvppHangWatch",
                    IsBackground = true,
                };

                t.Start();

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP HANGWATCH : surveillance armee (silence d'affichage de {StallMs} ms = gel). " +
                    "Elle observe seulement, elle ne repare rien.");
            }
        }

        private static void Loop()
        {
            while (true)
            {
                Thread.Sleep(500);

                long now = Environment.TickCount64;
                long last = Volatile.Read(ref _lastPresentMs);
                long silence = now - last;

                if (silence < StallMs)
                {
                    if (_inStall)
                    {
                        _inStall = false;

                        Logger.Warning?.Print(LogClass.Gpu,
                            $"MVPP HANGWATCH : l'affichage est REPARTI apres {silence} ms de silence " +
                            "-- ce n'etait donc pas definitif. Noter ce qui se passait a l'ecran.");
                    }

                    continue;
                }

                int draws = MvppCameraCapture.StatScaledDraws;

                if (!_inStall)
                {
                    _inStall = true;
                    _stallLoggedMs = now;
                    _drawsAtStall = draws;

                    Logger.Warning?.Print(LogClass.Gpu,
                        $"MVPP HANGWATCH : AUCUNE IMAGE PRESENTEE depuis {silence} ms. " +
                        $"Compteur de dessins a cet instant : {draws}. " +
                        "Prochaine ligne dans quelques secondes -- si ce compteur a AVANCE, c'est la " +
                        "PRESENTATION qui est bloquee (le fil graphique vit) ; s'il est FIGE, c'est le " +
                        "fil graphique lui-meme qui est coince.");

                    continue;
                }

                if (now - _stallLoggedMs < RepeatMs)
                {
                    continue;
                }

                _stallLoggedMs = now;

                int delta = draws - _drawsAtStall;
                _drawsAtStall = draws;

                Logger.Warning?.Print(LogClass.Gpu,
                    $"MVPP HANGWATCH : gel de {silence / 1000} s. Dessins depuis la derniere ligne : " +
                    $"{delta} ⇒ " +
                    (delta > 0
                        ? "le fil graphique VIT, c'est la PRESENTATION qui est bloquee (famille A)."
                        : "le fil graphique est COINCE lui aussi (famille B)."));
            }
        }
    }
}
