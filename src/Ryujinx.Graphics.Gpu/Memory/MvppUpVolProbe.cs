
using Ryujinx.Common.Logging;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Memory
{
    static class MvppUpVolProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_UPVOL") == "1";

        private static bool _armedLogged;
        private static long _windowMs;
        private static long _calls;
        private static long _bytes;
        private static long _maxSize;
        private static long _site0Calls;
        private static long _site0Bytes;
        private static long _site1Calls;
        private static long _site1Bytes;
        private static readonly long[] _bucketCalls = new long[4];
        private static readonly long[] _bucketBytes = new long[4];

        private static ulong _benchAddr;
        private static long _benchSize;
        private static PhysicalMemory _benchPm;
        private static byte[] _benchScratch;
        private static byte[] _benchManaged;

        public static void OnUpload(ulong address, ulong size, int site, PhysicalMemory pm)
        {
            if (!Enabled)
            {
                return;
            }

            if ((long)size > Volatile.Read(ref _benchSize) && pm != null)
            {
                _benchAddr = address;
                _benchPm = pm;
                Volatile.Write(ref _benchSize, (long)size);
            }

            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Info?.Print(LogClass.Gpu, "[UPVOL] ARMED v2 (RYUJINX_UPVOL=1) - buffer upload volume per gate (0=full, 1=region), ~5 s window.");
            }

            long s = (long)size;
            Interlocked.Increment(ref _calls);
            Interlocked.Add(ref _bytes, s);

            long seenMax = Volatile.Read(ref _maxSize);
            while (s > seenMax && Interlocked.CompareExchange(ref _maxSize, s, seenMax) != seenMax)
            {
                seenMax = Volatile.Read(ref _maxSize);
            }

            if (site == 0)
            {
                Interlocked.Increment(ref _site0Calls);
                Interlocked.Add(ref _site0Bytes, s);
            }
            else
            {
                Interlocked.Increment(ref _site1Calls);
                Interlocked.Add(ref _site1Bytes, s);
            }

            int b = s < 0x1000 ? 0 : s < 0x10000 ? 1 : s < 0x100000 ? 2 : 3;
            Interlocked.Increment(ref _bucketCalls[b]);
            Interlocked.Add(ref _bucketBytes[b], s);

            long now = Environment.TickCount64;
            long window = Volatile.Read(ref _windowMs);

            if (window == 0)
            {
                Interlocked.CompareExchange(ref _windowMs, now, 0);
            }
            else if (now - window >= 5000 && Interlocked.CompareExchange(ref _windowMs, now, window) == window)
            {
                double sec = (now - window) / 1000.0;
                long calls = Interlocked.Exchange(ref _calls, 0);
                long bytes = Interlocked.Exchange(ref _bytes, 0);
                long max = Interlocked.Exchange(ref _maxSize, 0);
                long s0c = Interlocked.Exchange(ref _site0Calls, 0);
                long s0b = Interlocked.Exchange(ref _site0Bytes, 0);
                long s1c = Interlocked.Exchange(ref _site1Calls, 0);
                long s1b = Interlocked.Exchange(ref _site1Bytes, 0);
                long b0c = Interlocked.Exchange(ref _bucketCalls[0], 0);
                long b0b = Interlocked.Exchange(ref _bucketBytes[0], 0);
                long b1c = Interlocked.Exchange(ref _bucketCalls[1], 0);
                long b1b = Interlocked.Exchange(ref _bucketBytes[1], 0);
                long b2c = Interlocked.Exchange(ref _bucketCalls[2], 0);
                long b2b = Interlocked.Exchange(ref _bucketBytes[2], 0);
                long b3c = Interlocked.Exchange(ref _bucketCalls[3], 0);
                long b3b = Interlocked.Exchange(ref _bucketBytes[3], 0);

                Logger.Info?.Print(LogClass.Gpu,
                    $"[UPVOL ~5s] calls {calls} ({calls / sec:F0}/s), total {bytes / 1048576.0:F1} MB ({bytes / 1048576.0 / sec:F1} MB/s), max {max / 1024} KB | " +
                    $"FULL: {s0c} ({s0b / 1048576.0:F1} MB) | REGION: {s1c} ({s1b / 1048576.0:F1} MB) | " +
                    $"<4K: {b0c} ({b0b / 1024} KB) | 4-64K: {b1c} ({b1b / 1048576.0:F1} MB) | 64K-1M: {b2c} ({b2b / 1048576.0:F1} MB) | >=1M: {b3c} ({b3b / 1048576.0:F1} MB)");

                RunMirrorBench();
            }
        }

        private static void RunMirrorBench()
        {
            long benchSize = Interlocked.Exchange(ref _benchSize, 0);
            ulong benchAddr = _benchAddr;
            PhysicalMemory pm = _benchPm;

            if (pm == null || benchSize < 0x4000)
            {
                return;
            }

            int sz = (int)Math.Min(benchSize, 0x40000);

            if (_benchScratch == null || _benchScratch.Length < sz)
            {
                _benchScratch = new byte[sz];
                _benchManaged = new byte[sz];
            }

            try
            {
                ReadOnlySpan<byte> src = pm.GetSpan(benchAddr, sz);
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                src.CopyTo(_benchScratch);
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                src.CopyTo(_benchScratch);
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();

                _benchManaged.AsSpan(0, sz).CopyTo(_benchScratch);
                long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                _benchManaged.AsSpan(0, sz).CopyTo(_benchScratch);
                long t4 = System.Diagnostics.Stopwatch.GetTimestamp();

                double freq = System.Diagnostics.Stopwatch.Frequency;
                double mirrorColdMBs = sz / ((t1 - t0) / freq) / 1048576.0;
                double mirrorWarmMBs = sz / ((t2 - t1) / freq) / 1048576.0;
                double managedMBs = sz / ((t4 - t3) / freq) / 1048576.0;

                Logger.Info?.Print(LogClass.Gpu,
                    $"[MIRBENCH] {sz / 1024} KB @0x{benchAddr:X}: mirror COLD {mirrorColdMBs:F0} MB/s, warm {mirrorWarmMBs:F0} MB/s, managed {managedMBs:F0} MB/s (managed/cold {managedMBs / Math.Max(1, mirrorColdMBs):F1}x)");
            }
            catch (Exception e)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"[MIRBENCH] read failed @0x{benchAddr:X}+{sz}: {e.Message} (bench skipped for this window)");
            }
        }
    }
}
