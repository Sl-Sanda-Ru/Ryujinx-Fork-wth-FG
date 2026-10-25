
using Ryujinx.Common.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    static class UnsafeBlitProbe
    {
        public static readonly bool KillSwitch =
            Environment.GetEnvironmentVariable("RYUJINX_NO_UNSAFE_BLIT") == "1";

        private static readonly bool _probe =
            Environment.GetEnvironmentVariable("RYUJINX_UNSAFE_BLIT_PROBE") == "1";

        private static int _armed;
        private static long _entries;
        private static long _unsafe;
        private static int _killAnnounced;
        private static readonly ConcurrentDictionary<string, byte> _seenEntry = new();
        private static readonly ConcurrentDictionary<string, byte> _seenUnsafe = new();

        private static long _presents;
        private static int _presentReported;

        public static void PresentTick()
        {
            long p = Interlocked.Increment(ref _presents);

            if (Interlocked.Exchange(ref _presentReported, 1) == 0)
            {
                string v = Environment.GetEnvironmentVariable("RYUJINX_UNSAFE_BLIT_PROBE") ?? "(null)";
                Logger.Info?.Print(LogClass.Gpu,
                    $"UNSAFE-BLIT: present path alive. RYUJINX_UNSAFE_BLIT_PROBE seen as '{v}' " +
                    $"(probe {(_probe ? "ON" : "OFF")}). If ON, CopyToImpl totals follow.");
            }

            if (_probe && (p == 300 || p == 3000 || p == 30000))
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"UNSAFE-BLIT: after {p} presents -- {Interlocked.Read(ref _entries)} CopyToImpl calls, " +
                    $"{Interlocked.Read(ref _unsafe)} on the UNSAFE aliased branch, " +
                    $"{_seenEntry.Count} distinct shapes ({_seenUnsafe.Count} distinct unsafe).");
            }
        }

        private static string Sig(TextureView src, TextureView dst, bool srcStore, bool dstStore)
        {
            return $"{src.Info.Format} {src.Width}x{src.Height}(store={srcStore}) -> " +
                   $"{dst.Info.Format} {dst.Width}x{dst.Height}(store={dstStore})";
        }

        public static void NoteEntry(TextureView src, TextureView dst, bool srcUsesStorageFormat, bool dstUsesStorageFormat)
        {
            if (!_probe)
            {
                return;
            }

            if (Interlocked.Exchange(ref _armed, 1) == 0)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    "UNSAFE-BLIT: probe ARMED -- watching every TextureView.CopyToImpl. " +
                    "'unsafe' = the format-aliased branch that shares device memory (the hazard suspect).");
            }

            long n = Interlocked.Increment(ref _entries);

            if (_seenEntry.TryAdd(Sig(src, dst, srcUsesStorageFormat, dstUsesStorageFormat), 0))
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"UNSAFE-BLIT: CopyToImpl shape #{_seenEntry.Count}: {Sig(src, dst, srcUsesStorageFormat, dstUsesStorageFormat)}");
            }

            if (n == 1 || n == 2000 || n == 20000 || n == 200000)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"UNSAFE-BLIT: {n} CopyToImpl calls, {Interlocked.Read(ref _unsafe)} took the UNSAFE aliased branch, " +
                    $"{_seenEntry.Count} distinct shapes ({_seenUnsafe.Count} distinct on the unsafe branch).");
            }
        }

        public static void Note(TextureView src, TextureView dst, bool srcUsesStorageFormat, bool dstUsesStorageFormat)
        {
            if (!_probe)
            {
                return;
            }

            Interlocked.Increment(ref _unsafe);

            if (_seenUnsafe.TryAdd(Sig(src, dst, srcUsesStorageFormat, dstUsesStorageFormat), 0))
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"UNSAFE-BLIT: >>> UNSAFE aliased shape #{_seenUnsafe.Count}: {Sig(src, dst, srcUsesStorageFormat, dstUsesStorageFormat)} <<<");
            }
        }

        public static void AnnounceKillOnce()
        {
            if (Interlocked.Exchange(ref _killAnnounced, 1) == 0)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    "UNSAFE-BLIT: RYUJINX_NO_UNSAFE_BLIT=1 armed -- format-aliased blits are being " +
                    "redirected to the safe HelperShader path.");
            }
        }
    }
}
