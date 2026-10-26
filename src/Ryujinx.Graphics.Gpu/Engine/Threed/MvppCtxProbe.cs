
using Ryujinx.Common.Logging;
using System;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppCtxProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_CTXPROBE") == "1";

        public static readonly bool TrialRead =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_HDRTRY") == "1";

        private const float IntruderDist = 100f;

        private const long FreshRefMs = 60;

        private const int MaxCtx = 32;
        private const int ReportMs = 5000;

        private struct Ctx
        {
            public bool Used;
            public int Fmt;
            public int CW;
            public int CH;
            public int DW;
            public int DH;
            public ulong ColorAddr;
            public ulong DepthAddr;
            public int Reads;
            public int Intruders;
            public int MinRank;
            public int MaxRank;
            public float MaxDist;
        }

        private static readonly Ctx[] _readCtx = new Ctx[MaxCtx];
        private static readonly Ctx[] _qualCtx = new Ctx[MaxCtx];

        private static int _rank;
        private static int _frameMark;
        private static int _seenMark;

        private static int _frames;
        private static int _qualSum;
        private static int _qualMin = int.MaxValue;
        private static int _qualMax;

        private static int _reads;
        private static int _intruders;
        private static int _ctxOverflow;

        private const int MaxIntr = 16;
        private static readonly float[] _intrDist = new float[MaxIntr];
        private static readonly int[] _intrCount = new int[MaxIntr];
        private static readonly float[] _intrPx = new float[MaxIntr];
        private static readonly float[] _intrPy = new float[MaxIntr];
        private static readonly float[] _intrPz = new float[MaxIntr];
        private static int _intrN;
        private static int _intrOverflow;

        private static long _rankSumClean;
        private static int _rankNClean;
        private static long _rankSumIntr;
        private static int _rankNIntr;
        private static readonly int[] _rankHistClean = new int[5];
        private static readonly int[] _rankHistIntr = new int[5];

        private static int _staleJumps;
        private static long _ageSumClean;
        private static int _ageNClean;
        private static long _ageSumIntr;
        private static int _ageNIntr;
        private static long _ageMaxIntr;

        private static bool _probedThisFrame;
        private static int _hdrFrames;
        private static int _hdrReadable;
        private static int _hdrUnreadable;
        private static int _hdrIntruders;

        private static long _lastReportMs;

        public static void NoteQualifyingDraw(GpuChannel channel)
        {
            int mark = _frameMark;

            if (_seenMark != mark)
            {
                _seenMark = mark;
                _rank = 0;
            }

            _rank++;

            if (TrialRead && !_probedThisFrame && MvppScenePass.IsSceneColorPass(channel))
            {
                _probedThisFrame = true;
                _hdrFrames++;

                if (MvppSoloCamera.TryProbeRead(channel, out float pdist))
                {
                    _hdrReadable++;

                    if (pdist >= IntruderDist)
                    {
                        _hdrIntruders++;
                    }
                }
                else
                {
                    _hdrUnreadable++;
                }
            }

            Describe(channel, out int fmt, out int cw, out int ch, out int dw, out int dh,
                out ulong cAddr, out ulong dAddr);

            Record(_qualCtx, fmt, cw, ch, dw, dh, cAddr, dAddr, _rank, 0f, false);
        }

        public static void NoteRead(GpuChannel channel, float dist, float px, float py, float pz, long ageMs)
        {
            _reads++;

            bool freshRef = ageMs <= FreshRefMs;
            bool bigJump = dist >= IntruderDist;
            bool intruder = bigJump && freshRef;

            if (bigJump && !freshRef)
            {
                _staleJumps++;
            }

            if (intruder)
            {
                _ageSumIntr += ageMs;
                _ageNIntr++;

                if (ageMs > _ageMaxIntr)
                {
                    _ageMaxIntr = ageMs;
                }
            }
            else
            {
                _ageSumClean += ageMs;
                _ageNClean++;
            }

            if (intruder)
            {
                _intruders++;
                NoteIntruder(dist, px, py, pz);
            }

            int bucket = _rank <= 1 ? 0 : _rank <= 3 ? 1 : _rank <= 10 ? 2 : _rank <= 50 ? 3 : 4;

            if (intruder)
            {
                _rankSumIntr += _rank;
                _rankNIntr++;
                _rankHistIntr[bucket]++;
            }
            else
            {
                _rankSumClean += _rank;
                _rankNClean++;
                _rankHistClean[bucket]++;
            }

            MvppLdrSkip.NoteRead(intruder);

            Describe(channel, out int fmt, out int cw, out int ch, out int dw, out int dh,
                out ulong cAddr, out ulong dAddr);

            Record(_readCtx, fmt, cw, ch, dw, dh, cAddr, dAddr, _rank, dist, intruder);

            long now = Environment.TickCount64;

            if (now - _lastReportMs >= ReportMs)
            {
                _lastReportMs = now;
                Report();
            }
        }

        public static void OnFrame()
        {
            if (_rank > 0)
            {
                _frames++;
                _qualSum += _rank;

                if (_rank < _qualMin)
                {
                    _qualMin = _rank;
                }

                if (_rank > _qualMax)
                {
                    _qualMax = _rank;
                }
            }

            _frameMark++;
            _probedThisFrame = false;
        }

        private static void NoteIntruder(float dist, float px, float py, float pz)
        {
            for (int i = 0; i < _intrN; i++)
            {
                if (MathF.Abs(_intrPx[i] - px) < 1f &&
                    MathF.Abs(_intrPy[i] - py) < 1f &&
                    MathF.Abs(_intrPz[i] - pz) < 1f)
                {
                    _intrCount[i]++;

                    return;
                }
            }

            if (_intrN >= MaxIntr)
            {
                _intrOverflow++;

                return;
            }

            _intrDist[_intrN] = dist;
            _intrPx[_intrN] = px;
            _intrPy[_intrN] = py;
            _intrPz[_intrN] = pz;
            _intrCount[_intrN] = 1;
            _intrN++;
        }

        private static void Describe(
            GpuChannel channel,
            out int fmt,
            out int cw,
            out int ch,
            out int dw,
            out int dh,
            out ulong cAddr,
            out ulong dAddr)
        {
            Image.Texture col0 = channel.TextureManager.RenderTargetColor0;
            Image.Texture ds = channel.TextureManager.RenderTargetDepthStencil;

            GAL.ITexture colHost = col0?.HostTexture;
            GAL.ITexture dsHost = ds?.HostTexture;

            fmt = col0 != null ? (int)col0.Info.FormatInfo.Format : -1;
            cw = colHost?.Width ?? 0;
            ch = colHost?.Height ?? 0;
            dw = dsHost?.Width ?? 0;
            dh = dsHost?.Height ?? 0;
            cAddr = col0 != null ? col0.Range.GetSubRange(0).Address : 0UL;
            dAddr = ds != null ? ds.Range.GetSubRange(0).Address : 0UL;
        }

        private static void Record(
            Ctx[] table,
            int fmt,
            int cw,
            int ch,
            int dw,
            int dh,
            ulong cAddr,
            ulong dAddr,
            int rank,
            float dist,
            bool intruder)
        {
            for (int i = 0; i < MaxCtx; i++)
            {
                ref Ctx c = ref table[i];

                if (!c.Used)
                {
                    c.Used = true;
                    c.Fmt = fmt;
                    c.CW = cw;
                    c.CH = ch;
                    c.DW = dw;
                    c.DH = dh;
                    c.DepthAddr = dAddr;
                    c.MinRank = rank;
                    c.MaxRank = rank;
                }
                else if (c.Fmt != fmt || c.CW != cw || c.CH != ch || c.DW != dw || c.DH != dh ||
                         c.DepthAddr != dAddr)
                {
                    continue;
                }

                c.ColorAddr = cAddr;
                c.Reads++;

                if (rank < c.MinRank)
                {
                    c.MinRank = rank;
                }

                if (rank > c.MaxRank)
                {
                    c.MaxRank = rank;
                }

                if (intruder)
                {
                    c.Intruders++;
                }

                if (dist > c.MaxDist)
                {
                    c.MaxDist = dist;
                }

                return;
            }

            _ctxOverflow++;
        }

        private static void Report()
        {
            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP CTXPROBE : {_reads} reads · {_intruders} INTRUDERS (step >= {IntruderDist:0} u " +
                $"against a FRESH reference) · {_staleJumps} large jumps discarded (stale reference, " +
                $"not intruders) · " +
                $"qualifying passes per frame: min {(_qualMin == int.MaxValue ? 0 : _qualMin)} / " +
                $"max {_qualMax} / mean {(_frames > 0 ? (float)_qualSum / _frames : 0f):0.##} over {_frames} frames" +
                $"{(_ctxOverflow > 0 ? $" · TABLE FULL, {_ctxOverflow} contexts not counted" : "")}");

            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP CTXPROBE VERDICT HDR PASS : {_frames} 3D frames · " +
                $"{_hdrFrames} with an HDR pass ({(_frames > 0 ? 100f * _hdrFrames / _frames : 0f):0.#} %) · " +
                $"camera READABLE on {_hdrReadable} " +
                $"({(_hdrFrames > 0 ? 100f * _hdrReadable / _hdrFrames : 0f):0.#} % of those) · " +
                $"unreadable on {_hdrUnreadable} · " +
                $"intruders among the readable: {_hdrIntruders} " +
                $"({(_hdrReadable > 0 ? 100f * _hdrIntruders / _hdrReadable : 0f):0.##} %)");

            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP CTXPROBE REFERENCE AGE · clean: " +
                $"{(_ageNClean > 0 ? (float)_ageSumClean / _ageNClean : 0f):0.#} ms mean over {_ageNClean} · " +
                $"intruders: {(_ageNIntr > 0 ? (float)_ageSumIntr / _ageNIntr : 0f):0.#} ms over {_ageNIntr} " +
                $"(max {_ageMaxIntr} ms)");

            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP CTXPROBE RANK · clean: mean " +
                $"{(_rankNClean > 0 ? (float)_rankSumClean / _rankNClean : 0f):0.#} over {_rankNClean} " +
                $"[rank1 {Pct(_rankHistClean[0], _rankNClean)} · 2-3 {Pct(_rankHistClean[1], _rankNClean)} · " +
                $"4-10 {Pct(_rankHistClean[2], _rankNClean)} · 11-50 {Pct(_rankHistClean[3], _rankNClean)} · " +
                $"51+ {Pct(_rankHistClean[4], _rankNClean)}]");

            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP CTXPROBE RANK · intruders: mean " +
                $"{(_rankNIntr > 0 ? (float)_rankSumIntr / _rankNIntr : 0f):0.#} over {_rankNIntr} " +
                $"[rank1 {Pct(_rankHistIntr[0], _rankNIntr)} · 2-3 {Pct(_rankHistIntr[1], _rankNIntr)} · " +
                $"4-10 {Pct(_rankHistIntr[2], _rankNIntr)} · 11-50 {Pct(_rankHistIntr[3], _rankNIntr)} · " +
                $"51+ {Pct(_rankHistIntr[4], _rankNIntr)}]");

            for (int i = 0; i < _intrN; i++)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP CTXPROBE INTRUDER #{i} : step {_intrDist[i]:0.0} u · " +
                    $"from [{_intrPx[i]:0.0} {_intrPy[i]:0.0} {_intrPz[i]:0.0}] · " +
                    $"seen {_intrCount[i]} times");
            }

            if (_intrOverflow > 0)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP CTXPROBE INTRUDERS : {_intrOverflow} distinct values not counted (table full)");
            }

            Dump("READ CONTEXT", _readCtx, true);
            Dump("QUALIFYING PASSES", _qualCtx, false);
        }

        private static string Pct(int n, int total)
        {
            return total > 0 ? $"{100f * n / total:0}%" : "-";
        }

        private static void Dump(string title, Ctx[] table, bool withIntruders)
        {
            for (int i = 0; i < MaxCtx; i++)
            {
                ref Ctx c = ref table[i];

                if (!c.Used)
                {
                    break;
                }

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP CTXPROBE {title} #{i} : " +
                    $"COLOR {(c.Fmt < 0 ? "none" : $"{(GAL.Format)c.Fmt} {c.CW}x{c.CH} @0x{c.ColorAddr:X}")} · " +
                    $"DEPTH {c.DW}x{c.DH} @0x{c.DepthAddr:X} · " +
                    $"rank {c.MinRank}-{c.MaxRank} · {c.Reads} seen" +
                    (withIntruders
                        ? $" · {c.Intruders} intruders · max step {c.MaxDist:0.#} u"
                        : ""));
            }
        }
    }
}
