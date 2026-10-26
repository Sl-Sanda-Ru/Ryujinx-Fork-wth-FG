
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
                    $"MVPP HANGWATCH : watchdog ARMED (display silence of {StallMs} ms = hang). " +
                    "It only observes, it fixes nothing.");
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
                            $"MVPP HANGWATCH : display RESUMED after {silence} ms of silence " +
                            "-- so it was not permanent. Note what was happening on screen.");
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
                        $"MVPP HANGWATCH : NO FRAME PRESENTED for {silence} ms. " +
                        $"Draw counter at this moment: {draws}. " +
                        "Next line in a few seconds -- if this counter has ADVANCED, it is " +
                        "PRESENTATION that is blocked (the GPU thread is alive); if it is FROZEN, it is the " +
                        "GPU thread itself that is stuck.");

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
                    $"MVPP HANGWATCH : hang of {silence / 1000} s. Draws since the last line: " +
                    $"{delta} ⇒ " +
                    (delta > 0
                        ? "the GPU thread is ALIVE, PRESENTATION is what is blocked (family A)."
                        : "the GPU thread is STUCK too (family B)."));
            }
        }
    }
}
