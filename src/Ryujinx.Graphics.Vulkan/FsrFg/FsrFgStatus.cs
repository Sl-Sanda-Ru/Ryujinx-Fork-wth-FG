using System;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan.FsrFg
{
    /// <summary>
    /// The window into frame generation that the UI gets. Separate from <see cref="FsrFgContext"/>
    /// because that type is internal — its members take Vulkan handles and command buffers the UI
    /// has no business holding — while the status bar lives in another assembly.
    ///
    /// <para>Armed means "measured recently", following
    /// <c>Dlss.StreamlineFrameGen.UiArmed</c>: the badge then clears itself when frames stop being
    /// generated, whether that is a pause, a swapchain rebuild or a shutdown, so no path has to
    /// remember to turn it off.</para>
    /// </summary>
    public static class FsrFgStatus
    {
        private const long StaleAfterMs = 3000;

        private static long _measuredAtMs;
        private static long _ratioMilli;

        /// <summary>True while frame generation reported a measurement in the last few seconds.</summary>
        public static bool UiArmed => Environment.TickCount64 - Interlocked.Read(ref _measuredAtMs) < StaleAfterMs;

        /// <summary>
        /// Presents per rendered frame as last measured, so 2.0 means one generated frame reached
        /// the screen for every rendered one. This is measured rather than configured: FidelityFX
        /// paces generated frames itself and drops them under load, so the figure moves.
        /// </summary>
        public static float UiPresentRatio => Interlocked.Read(ref _ratioMilli) / 1000f;

        /// <summary>The measured ratio as a whole multiplier, never below 1.</summary>
        public static int UiMultiplier => Math.Max(1, (int)MathF.Round(UiPresentRatio));

        private static long _displayFpsMilli;

        /// <summary>
        /// Frames per second FidelityFX actually presented, generated ones included, counted from
        /// its own present counter rather than derived from the game frame rate.
        /// </summary>
        public static float UiDisplayFps => Interlocked.Read(ref _displayFpsMilli) / 1000f;

        internal static void Report(double presentRatio, double displayFps)
        {
            Interlocked.Exchange(ref _ratioMilli, (long)Math.Round(presentRatio * 1000.0));
            Interlocked.Exchange(ref _displayFpsMilli, (long)Math.Round(displayFps * 1000.0));
            Interlocked.Exchange(ref _measuredAtMs, Environment.TickCount64);
        }

        internal static void Clear()
        {
            Interlocked.Exchange(ref _measuredAtMs, 0);
            Interlocked.Exchange(ref _ratioMilli, 0);
            Interlocked.Exchange(ref _displayFpsMilli, 0);
        }
    }
}
