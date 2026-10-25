
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppFeedbackProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_FEEDBACK_PROBE") == "1";

        private sealed class Rec
        {
            public long LastWriteFrame = long.MinValue;
            public long FirstAccessFrame = long.MinValue;
            public long CaseA;
            public long CaseB;
            public string Format;
            public int Width;
            public int Height;
            public ulong Va;
        }

        private static readonly Dictionary<ulong, Rec> _rts = new();
        private static long _frame;
        private static long _lastReportMs;
        private static int _announced;

        public static void AnnounceOnce()
        {
            if (System.Threading.Interlocked.Exchange(ref _announced, 1) != 0)
            {
                return;
            }

            string v = Environment.GetEnvironmentVariable("RYUJINX_FEEDBACK_PROBE");
            Logger.Info?.Print(LogClass.Gpu,
                $"FEEDBACKPROBE gate check: RYUJINX_FEEDBACK_PROBE={(v ?? "<unset>")} -> enabled={Enabled}. " +
                (Enabled ? "Probe ACTIVE." : "Probe OFF -- launch via XC2_FEEDBACK_PROBE.bat to enable."));
        }

        public static long Frame => System.Threading.Interlocked.Read(ref _frame);

        public static (long caseA, long caseB) Classify(ulong va)
        {
            lock (_rts)
            {
                return _rts.TryGetValue(va, out Rec r) ? (r.CaseA, r.CaseB) : (0, 0);
            }
        }

        private static ulong Va(Texture t)
        {
            try
            {
                return t.Range.GetSubRange(0).Address;
            }
            catch
            {
                return 0;
            }
        }

        public static void OnWrite(Texture texture)
        {
            if (texture == null)
            {
                return;
            }

            ulong va = Va(texture);

            lock (_rts)
            {
                if (!_rts.TryGetValue(va, out Rec rec))
                {
                    rec = new Rec();
                    _rts[va] = rec;
                }

                if (rec.FirstAccessFrame != _frame)
                {
                    rec.FirstAccessFrame = _frame;

                    if (rec.LastWriteFrame == _frame - 1)
                    {
                        rec.CaseB++;
                    }
                }

                rec.LastWriteFrame = _frame;

                TextureInfo info = texture.Info;
                rec.Format = info.FormatInfo.Format.ToString();
                rec.Width = info.Width;
                rec.Height = info.Height;
                rec.Va = va;
            }
        }

        public static void OnRead(Texture texture)
        {
            if (texture == null)
            {
                return;
            }

            ulong va = Va(texture);

            lock (_rts)
            {
                if (!_rts.TryGetValue(va, out Rec rec))
                {
                    return;
                }

                if (rec.FirstAccessFrame != _frame)
                {
                    rec.FirstAccessFrame = _frame;

                    if (rec.LastWriteFrame == _frame - 1)
                    {
                        rec.CaseA++;
                    }
                }
            }
        }

        public static void OnFrameBoundary()
        {
            lock (_rts)
            {
                _frame++;

                long now = Environment.TickCount64;
                if (now - _lastReportMs < 2000)
                {
                    return;
                }
                _lastReportMs = now;

                var caseA = new List<Rec>();
                foreach (Rec r in _rts.Values)
                {
                    if (r.CaseA > 0)
                    {
                        caseA.Add(r);
                    }
                }

                caseA.Sort((a, b) => b.CaseA.CompareTo(a.CaseA));

                Logger.Info?.Print(LogClass.Gpu,
                    $"FEEDBACKPROBE report: frame={_frame} trackedRTs={_rts.Count} caseA(history)={caseA.Count} " +
                    "(*** = also a DoF input; run with RYUJINX_DOF_PROBE=1 to populate) =====");

                foreach (Rec r in caseA)
                {
                    bool feedsDof = MvppDofProbe.DofInputVas.Contains(r.Va);

                    Logger.Info?.Print(LogClass.Gpu,
                        $"FEEDBACKPROBE   CASE A history: caseA={r.CaseA,6} caseB={r.CaseB,6}  {r.Format}/{r.Width}x{r.Height} " +
                        $"va=0x{r.Va:X}{(feedsDof ? "  *** FEEDS DoF" : "")}");
                }
            }
        }
    }
}
