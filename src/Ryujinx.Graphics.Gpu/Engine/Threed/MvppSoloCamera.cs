
using Ryujinx.Common.Logging;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppSoloCamera
    {
        public static bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VPSOLO") == "1";

        private const int ElectionIntervalMs = 1000;
        private const int MaxBytesPerBuffer = 4096;
        private const int MaxCandidates = 128;
        private const int MaxTracks = 64;
        private const int RingSize = 6;

        private const float PosAgreeEps = 0.5f;
        private const float TeleportEps = 1e5f;

        private static float ElectMaxStep =
            float.TryParse(System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_ELECT_MAXSTEP"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float electMaxStep) && electMaxStep > 0f
                ? electMaxStep
                : 0f;
        private const float AbsurdPos = 1e6f;

        private struct Candidate
        {
            public int Stage;
            public int Slot;
            public int Offset;
            public bool Transposed;
            public ulong Address;
            public float Aspect;
            public float X;
            public float Y;
            public float Z;
            public float Motion;
            public float Straightness;

            public float DirX;
            public float DirY;
            public float DirZ;
        }

        private struct Tracked
        {
            public ulong Address;
            public int Offset;
            public float LastX;
            public float LastY;
            public float LastZ;
            public bool HasLast;
            public float Motion;
            public int RingHead;
            public int RingCount;
            public float PathLength;

            public float LastDirX;
            public float LastDirY;
            public float LastDirZ;
            public bool HasLastDir;
        }

        private static readonly Candidate[] _cands = new Candidate[MaxCandidates];
        private static int _candCount;

        private static readonly Tracked[] _tracks = new Tracked[MaxTracks];
        private static int _trackCount;

        private static readonly float[] _ringX = new float[MaxTracks * RingSize];
        private static readonly float[] _ringY = new float[MaxTracks * RingSize];
        private static readonly float[] _ringZ = new float[MaxTracks * RingSize];
        private static readonly float[] _ringStep = new float[MaxTracks * RingSize];

        private static readonly float[] _tmp = new float[16];
        private static readonly float[] _read = new float[16];

        private static bool _hasElection;
        private static int _elStage;
        private static int _elSlot;
        private static int _elOffset;
        private static bool _elTransposed;

        public static readonly bool CapTime =
            System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_CAPTIME") == "1";

        private const int CtMax = 16;
        private static readonly float[] _ctHist = new float[CtMax * 16];
        private static readonly int[] _ctHistDraw = new int[CtMax];
        private static int _ctCount;
        private static int _ctBound;
        private static int _ctMissing;
        private static int _ctDraw;
        private static readonly float[] _ctPending = new float[16];
        private static readonly float[] _ctCaptured = new float[16];
        private static bool _ctHasCapture;
        private static long _ctFrameId;

        public static readonly bool CapTimeFix =
            System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_CAPTIME_FIX") == "1";

        private static readonly int _abBlock =
            int.TryParse(System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_CAPTIME_AB"), out int b) && b > 0 ? b : 0;

        private static long _abPresents;
        private static bool _abArmed;
        private static readonly long[] _abFrames = new long[2];
        private static readonly long[] _abCaptures = new long[2];
        private static readonly long[] _abMoved = new long[2];
        private static readonly long[] _abPreSum = new long[2];
        private static readonly long[] _abA = new long[2];
        private static readonly long[] _abB = new long[2];
        private static readonly long[] _abTrans = new long[2];
        private static readonly long[] _abFailAtSwitch = new long[2];
        private static char _abLastVerdict;

        private static bool FixActive => _abBlock > 0 ? _abArmed : CapTimeFix;

        private static long _whyCalled, _whyOff, _whyArmedCall, _whyNoElection, _whyNoRef;
        private static long _whyUnbound, _whyReadable, _whyBadShape, _whyDiffers, _whyArmed;
        private static long _whyDeadline, _whyNetted;
        private static long _whyLogMs;

        private static readonly int _ctMaxSkip =
            int.TryParse(System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_CAPTIME_MAX"), out int k) && k > 0 ? k : 0;

        private static bool _ctPrevFrameMissed;

        private static readonly bool _rankProbe =
            System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_RANKPROBE") == "1";

        private static readonly int[] _rkBuckets = new int[6];
        private static int _rkNever;
        private static int _rkNoCover;
        private static int _rkSaturated;
        private static int _rkFrames;
        private static long _rkDrawSum;
        private static long _rkLogMs;

        private static int _ctPreDraw;
        private static int _ctMovedFrames;
        private static int _ctTotalFrames;
        private static long _ctPreDrawSum;
        private static long _ctMovedLogMs;

        private static long _lastElectionMs;

        private static int _electHystMs =
            int.TryParse(System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_ELECT_HYST"), out int hy) && hy > 0 ? hy : 0;

        private static readonly bool _electHystAb =
            System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_ELECT_HYST_AB") == "1";

        private static bool _electWarp =
            System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_ELECT_WARP") == "1";

        private static readonly bool _rotMotion =
            System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_VPSOLO_ROT") == "1";

        private const float RotMotionScale = 30f;

        private static bool _rotArmedLogged;
        private static bool _armWitnessLogged;

        private static readonly bool _mirrors =
            System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_MIRRORS") == "1";

        private static readonly bool _mirrorsAb =
            System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_MIRRORS_AB") == "1";

        private const long MirrorsAbSliceMs = 30000;

        private static readonly float[] _mirSeatNow = new float[16];
        private static readonly float[] _mirSeatLast = new float[16];
        private static bool _mirHasSeatLast;
        private static bool _mirAbArmed;
        private static bool _mirAbKnown;
        private static int _mirStale;
        private static int _mirTried;
        private static int _mirFound;
        private static int _mirAccepted;
        private static int _mirRefused;
        private static int _mirNone;
        private static long _mirLogMs;
        private static int _mirSeatStage, _mirSeatSlot, _mirSeatOffset;

        private static void MirrorLog(long now)
        {
            if (now - _mirLogMs < 5000)
            {
                return;
            }

            _mirLogMs = now;

            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP mirrors: stale={_mirStale} detours={_mirTried} found={_mirFound} " +
                $"accepted={_mirAccepted} refused={_mirRefused} none={_mirNone} " +
                $"| last mirror chosen=stage{_mirSeatStage} cbuf{_mirSeatSlot} +0x{_mirSeatOffset:X3}" +
                $"{(_mirrorsAb ? $" | side={(_mirAbArmed ? "ARMED" : "OFF")}" : "")}");

            _mirStale = 0;
            _mirTried = 0;
            _mirFound = 0;
            _mirAccepted = 0;
            _mirRefused = 0;
            _mirNone = 0;
        }

        private const long HystAbSliceMs = 30000;

        private static long _lastSeatOkMs;
        private static bool _hystAbArmed;
        private static bool _hystAbSideKnown;
        private static int _hystBlocked;
        private static int _hystExpired;
        private static int _hystStarved;
        private static long _hystLogMs;

        private static long _liveMainArea;
        private static float _mainAspect;
        private static int _elections;

        private static int _electMaxStepRejected;

        private const int ElectBannedMax = 16;
        private static readonly int[] _electBanned = new int[ElectBannedMax];
        private static int _electBannedCount;

        private static int ElectBanKey(int stage, int slot, int offset)
        {
            return (stage << 24) | (slot << 16) | (offset & 0xFFFF);
        }

        private static bool ElectIsBanned(int stage, int slot, int offset)
        {
            int key = ElectBanKey(stage, slot, offset);

            for (int i = 0; i < _electBannedCount; i++)
            {
                if (_electBanned[i] == key)
                {
                    return true;
                }
            }

            return false;
        }
        private static long _lastLogMs;
        private static int _reads;
        private static int _readFails;

        private static readonly bool _camGuard =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_CAMGUARD") == "1";

        private const float MaxSpeed = 150f;
        private const float MinBudget = 5f;
        private const float CatchUpCapSec = 2f;
        private const float MaxFocalDrift = 0.15f;

        private static bool _deJitter =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_DEJITTER") == "1";

        private const int FailReasons = 7;
        private static readonly int[] _fail = new int[FailReasons];
        private static readonly string[] _failNames =
        {
            "buffer absent",
            "out of bounds",
            "not a camera",
            "square projection",
            "aspect",
            "absurd position",
            "matrix at origin",
        };

        private static long _lastFailLogMs;

        private static bool _probing;

        private static bool Fail(int reason)
        {
            if (_probing)
            {
                return false;
            }

            _fail[reason]++;

            long now = Environment.TickCount64;

            if (GAL.MvppDev.Enabled && now - _lastFailLogMs >= 5000)
            {
                _lastFailLogMs = now;

                int total = 0;

                for (int i = 0; i < FailReasons; i++)
                {
                    total += _fail[i];
                }

                if (total > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"MVPP read fails ({total} in 5s) : ");

                    for (int i = 0; i < FailReasons; i++)
                    {
                        if (_fail[i] > 0)
                        {
                            sb.Append($"{_failNames[i]} {_fail[i]} ({100f * _fail[i] / total:0.#} %) · ");
                        }
                    }

                    Logger.Info?.Print(LogClass.Gpu, sb.ToString().TrimEnd(' ', '·'));
                }

                Array.Clear(_fail);
            }

            return false;
        }

        private static readonly bool _altProbe =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_DUPPAIR") == "1";

        private static bool _multiAddr =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_MULTIADDR") == "1";

        private static readonly float[] _goodFlat = new float[16];
        private static readonly float[] _goodFlatPrev = new float[16];
        private static int _goodCount;
        private static int _multiTaken;
        private static int _multiBack;

        private static void Flatten(in Matrix4x4 m, float[] dst)
        {
            dst[0] = m.M11; dst[1] = m.M12; dst[2] = m.M13; dst[3] = m.M14;
            dst[4] = m.M21; dst[5] = m.M22; dst[6] = m.M23; dst[7] = m.M24;
            dst[8] = m.M31; dst[9] = m.M32; dst[10] = m.M33; dst[11] = m.M34;
            dst[12] = m.M41; dst[13] = m.M42; dst[14] = m.M43; dst[15] = m.M44;
        }

        private static float TrajectoryDot(in Matrix4x4 cand)
        {
            if (_goodCount < 2)
            {
                return 1f;
            }

            Flatten(in cand, _candFlat);

            float dot = 0f;

            for (int i = 0; i < 16; i++)
            {
                dot += (_candFlat[i] - _goodFlat[i]) * (_goodFlat[i] - _goodFlatPrev[i]);
            }

            return dot;
        }

        private static readonly float[] _candFlat = new float[16];

        private static float _multiMaxRot =
            float.TryParse(Environment.GetEnvironmentVariable("RYUJINX_MVPP_MULTIADDR_MAXROT"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float mr) && mr > 0f ? mr : 0f;
        private static int _multiFar;

        private static readonly int ElectRotMaxStreak =
            Math.Max(1, (int)(float.TryParse(Environment.GetEnvironmentVariable("RYUJINX_MVPP_ELECTROT_STREAK"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float es) ? es : 1f));
        private static readonly float _electRot =
            float.TryParse(Environment.GetEnvironmentVariable("RYUJINX_MVPP_ELECTROT"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float er) && er > 0f ? er : 0f;

        private static int _electRejectStreak;
        private static int _electRejected;
        private static int _electForced;

        private static readonly float _subProbe =
            float.TryParse(Environment.GetEnvironmentVariable("RYUJINX_MVPP_SUBPROBE"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float sp) && sp > 0f ? sp : 0f;

        private static int _subSuspects;
        private static int _subRescued;
        private static int _subNoAlt;
        private static readonly int[] _subByRank = new int[HistMax];
        private static float _subRotMax;
        private static float _subDistMax;
        private static float _subDistMin = float.MaxValue;
        private static float _subAltDistMax;
        private static float _okRotMax;
        private static float _okDistMax;
        private static long _subLogMs;

        private static float RotGapTo(in Matrix4x4 a, in Matrix4x4 b)
        {
            Flatten(in a, _candFlat);
            Flatten(in b, _goodFlatPrev2);

            float m = 0f;

            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    m = Math.Max(m, Math.Abs(_candFlat[r * 4 + c] - _goodFlatPrev2[r * 4 + c]));
                }
            }

            return m;
        }

        private static readonly float[] _goodFlatPrev2 = new float[16];

        private static float RotGap(in Matrix4x4 cand)
        {
            if (_goodCount < 1)
            {
                return 0f;
            }

            Flatten(in cand, _candFlat);

            float m = 0f;

            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    m = Math.Max(m, Math.Abs(_candFlat[r * 4 + c] - _goodFlat[r * 4 + c]));
                }
            }

            return m;
        }

        private static bool TryReadHistory(GpuChannel channel, out Matrix4x4 best, out float bx, out float by, out float bz)
        {
            best = default;
            bx = by = bz = 0f;

            float bestDot = 0f;
            bool found = false;

            _probing = true;

            try
            {
                for (int i = 0; i < _histCount; i++)
                {
                    (int stage, int slot, int offset, bool transposed) = _hist[i];

                    if (stage == _elStage && slot == _elSlot && offset == _elOffset && transposed == _elTransposed)
                    {
                        continue;
                    }

                    if (!TryReadAt(channel, stage, slot, offset, transposed, out Matrix4x4 cand,
                            out float cx, out float cy, out float cz))
                    {
                        continue;
                    }

                    if (_hasLastGood && cand.Equals(_lastGoodVp))
                    {
                        continue;
                    }

                    float dot = TrajectoryDot(in cand);

                    if (dot <= 0f)
                    {
                        _multiBack++;

                        continue;
                    }

                    if (_multiMaxRot > 0f && RotGap(in cand) >= _multiMaxRot)
                    {
                        _multiFar++;

                        continue;
                    }

                    if (!found || dot > bestDot)
                    {
                        found = true;
                        bestDot = dot;
                        best = cand;
                        bx = cx;
                        by = cy;
                        bz = cz;

                        _mirSeatStage = stage;
                        _mirSeatSlot = slot;
                        _mirSeatOffset = offset;
                    }
                }
            }
            finally
            {
                _probing = false;
            }

            if (found)
            {
                _multiTaken++;
            }

            return found;
        }

        private const int HistMax = 4;
        private static readonly (int Stage, int Slot, int Offset, bool Transposed)[] _hist =
            new (int, int, int, bool)[HistMax];
        private static int _histCount;
        private static int _altFound;
        private static int _altNone;
        private static readonly int[] _altByRank = new int[HistMax];
        private static readonly int[] _altDist = new int[4];
        private static float _altDistMax;
        private static int _altSame;
        private static int _altNew;

        private static void PushHistory(int stage, int slot, int offset, bool transposed)
        {
            for (int i = 0; i < _histCount; i++)
            {
                if (_hist[i].Stage == stage && _hist[i].Slot == slot &&
                    _hist[i].Offset == offset && _hist[i].Transposed == transposed)
                {
                    return;
                }
            }

            if (_histCount < HistMax)
            {
                _histCount++;
            }

            for (int i = _histCount - 1; i > 0; i--)
            {
                _hist[i] = _hist[i - 1];
            }

            _hist[0] = (stage, slot, offset, transposed);
        }

        private static void SubProbe(GpuChannel channel, in Matrix4x4 vp, float apx, float apy, float apz, long now)
        {
            float dx = apx - _lastPx;
            float dy = apy - _lastPy;
            float dz = apz - _lastPz;
            float dist = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));

            float rot = RotGapTo(in vp, in _lastGoodVp);

            if (rot < _subProbe)
            {
                _okRotMax = Math.Max(_okRotMax, rot);
                _okDistMax = Math.Max(_okDistMax, dist);
            }
            else
            {
                _subSuspects++;
                _subRotMax = Math.Max(_subRotMax, rot);
                _subDistMax = Math.Max(_subDistMax, dist);
                _subDistMin = Math.Min(_subDistMin, dist);

                bool found = false;

                _probing = true;

                try
                {
                    for (int i = 0; i < _histCount; i++)
                    {
                        (int stage, int slot, int offset, bool transposed) = _hist[i];

                        if (stage == _elStage && slot == _elSlot && offset == _elOffset && transposed == _elTransposed)
                        {
                            continue;
                        }

                        if (!TryReadAt(channel, stage, slot, offset, transposed, out Matrix4x4 alt,
                                out float bpx, out float bpy, out float bpz))
                        {
                            continue;
                        }

                        if (RotGapTo(in alt, in _lastGoodVp) >= _subProbe)
                        {
                            continue;
                        }

                        float ex = bpx - _lastPx;
                        float ey = bpy - _lastPy;
                        float ez = bpz - _lastPz;

                        _subAltDistMax = Math.Max(_subAltDistMax, MathF.Sqrt((ex * ex) + (ey * ey) + (ez * ez)));
                        _subByRank[i]++;
                        found = true;

                        break;
                    }
                }
                finally
                {
                    _probing = false;
                }

                if (found)
                {
                    _subRescued++;
                }
                else
                {
                    _subNoAlt++;
                }
            }

            if (now - _subLogMs < 1000)
            {
                return;
            }

            _subLogMs = now;

            var sb = new System.Text.StringBuilder();
            sb.Append($"MVPP SUBPROBE (rot threshold {_subProbe:0.###}) : {_subSuspects} intrusions adopted");

            if (_subSuspects > 0)
            {
                sb.Append($" (rot max {_subRotMax:0.#####}, distance {_subDistMin:0.#}-{_subDistMax:0.#} u)");
                sb.Append($" | FALLBACK AVAILABLE {_subRescued}, NONE {_subNoAlt}");

                for (int i = 0; i < HistMax; i++)
                {
                    if (_subByRank[i] > 0)
                    {
                        sb.Append($" | rank {i}: {_subByRank[i]}");
                    }
                }

                if (_subRescued > 0)
                {
                    sb.Append($" | max fallback distance {_subAltDistMax:0.#} u");
                }
            }

            sb.Append($" || legitimate control: max rot {_okRotMax:0.#####}, max distance {_okDistMax:0.#} u");
            sb.Append($" | {_histCount} known addresses");

            Logger.Info?.Print(LogClass.Gpu, sb.ToString());

            _subSuspects = 0;
            _subRescued = 0;
            _subNoAlt = 0;
            _subRotMax = 0f;
            _subDistMax = 0f;
            _subDistMin = float.MaxValue;
            _subAltDistMax = 0f;
            _okRotMax = 0f;
            _okDistMax = 0f;
            Array.Clear(_subByRank);
        }

        public static void ProbeAlternates(GpuChannel channel)
        {
            _probing = true;

            try
            {
                for (int i = 0; i < _histCount; i++)
                {
                    (int stage, int slot, int offset, bool transposed) = _hist[i];

                    if (stage == _elStage && slot == _elSlot && offset == _elOffset && transposed == _elTransposed)
                    {
                        continue;
                    }

                    if (TryReadAt(channel, stage, slot, offset, transposed, out Matrix4x4 alt,
                            out float apx, out float apy, out float apz))
                    {
                        _altFound++;
                        _altByRank[i]++;

                        if (_hasLastGood)
                        {
                            float dx = apx - _lastPx;
                            float dy = apy - _lastPy;
                            float dz = apz - _lastPz;
                            float dist = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));

                            int bucket = dist < 1f ? 0 : dist < 10f ? 1 : dist < 100f ? 2 : 3;
                            _altDist[bucket]++;

                            if (dist > _altDistMax)
                            {
                                _altDistMax = dist;
                            }

                            if (alt.Equals(_lastGoodVp))
                            {
                                _altSame++;
                            }
                            else
                            {
                                _altNew++;
                            }
                        }

                        return;
                    }
                }
            }
            finally
            {
                _probing = false;
            }

            _altNone++;
        }

        public static string AltSummaryAndReset()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"MVPP ALT: camera found again at an already known address {_altFound} times, " +
                      $"not found anywhere {_altNone} times");

            for (int i = 0; i < HistMax; i++)
            {
                if (_altByRank[i] > 0)
                {
                    sb.Append($" | rank {i}: {_altByRank[i]}");
                }

                _altByRank[i] = 0;
            }

            sb.Append($" | {_histCount} known addresses");
            sb.Append($" | distance to last accepted camera: <1u {_altDist[0]}, 1-10u {_altDist[1]}, " +
                      $"10-100u {_altDist[2]}, >100u {_altDist[3]}, max {_altDistMax:0.##}");
            sb.Append($" | new value {_altNew}, identical {_altSame}");
            sb.Append($" | MULTIADDR {(_multiAddr ? $"armed: {_multiTaken} reads recovered, {_multiBack} refused as going backwards" + (_multiMaxRot > 0f ? $", {_multiFar} refused as OTHER CAMERA (rot >= {_multiMaxRot:0.###})" : "") : "off")}");
            sb.Append($" | ELECTROT {(_electRot > 0f ? $"threshold {_electRot:0.###} : {_electRejected} reads refused (other camera), {_electForced} force-accepted" : "off")}");
            _electRejected = 0;
            _electForced = 0;

            _multiTaken = 0;
            _multiBack = 0;

            Array.Clear(_altDist);
            _altDistMax = 0f;
            _altSame = 0;
            _altNew = 0;
            _altFound = 0;
            _altNone = 0;

            return sb.ToString();
        }

        public static void CopyFailCounts(int[] dst)
        {
            for (int i = 0; i < FailReasons && i < dst.Length; i++)
            {
                dst[i] = _fail[i];
            }
        }

        public static string[] FailNames => _failNames;

        public static int FailReasonCount => FailReasons;

        private static float _lastJx, _lastJy;
        private static int _rejectLogs;

        public static float LastJitterX => _lastAcceptedJx;

        public static float LastJitterY => _lastAcceptedJy;

        private static float _lastAcceptedJx;
        private static float _lastAcceptedJy;

        private static readonly bool _jitProbe =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_JITPROBE") == "1";

        private static int _mainWidth;
        private static int _mainHeight;
        private static long _jitLogMs;
        private static int _jitN;
        private static float _jitMinX = float.MaxValue, _jitMaxX = float.MinValue;
        private static float _jitMinY = float.MaxValue, _jitMaxY = float.MinValue;
        private static readonly float[] _jitSeenX = new float[32];
        private static int _jitSeenN;

        private static void NoteJitter(float jx, float jy)
        {
            if (!_jitProbe || _mainWidth <= 0)
            {
                return;
            }

            float px = jx * _mainWidth * 0.5f;
            float py = jy * _mainHeight * 0.5f;

            _jitN++;
            _jitMinX = MathF.Min(_jitMinX, px);
            _jitMaxX = MathF.Max(_jitMaxX, px);
            _jitMinY = MathF.Min(_jitMinY, py);
            _jitMaxY = MathF.Max(_jitMaxY, py);

            bool seen = false;

            for (int i = 0; i < _jitSeenN; i++)
            {
                if (MathF.Abs(_jitSeenX[i] - px) < 0.01f)
                {
                    seen = true;

                    break;
                }
            }

            if (!seen && _jitSeenN < _jitSeenX.Length)
            {
                _jitSeenX[_jitSeenN++] = px;
            }

            long now = Environment.TickCount64;

            if (now - _jitLogMs < 1000)
            {
                return;
            }

            _jitLogMs = now;

            var sb = new System.Text.StringBuilder();
            sb.Append($"MVPP JITPROBE: GAME jitter offset x=[{_jitMinX:0.0000};{_jitMaxX:0.0000}] px ");
            sb.Append($"y=[{_jitMinY:0.0000};{_jitMaxY:0.0000}] px over {_jitN} reads ");
            sb.Append($"| distinct values in x = {_jitSeenN}{(_jitSeenN >= _jitSeenX.Length ? "+" : "")} : ");

            for (int i = 0; i < _jitSeenN && i < 10; i++)
            {
                sb.Append($"{_jitSeenX[i]:0.000} ");
            }

            sb.Append($"| render {_mainWidth}x{_mainHeight}");

            Logger.Info?.Print(LogClass.Gpu, sb.ToString());

            _jitN = 0;
            _jitMinX = float.MaxValue;
            _jitMaxX = float.MinValue;
            _jitMinY = float.MaxValue;
            _jitMaxY = float.MinValue;
            _jitSeenN = 0;
        }

        private static bool _aspectLock =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_ASPECTLOCK") == "1";

        private static bool _skyReject =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_SKYREJECT") == "1";

        private static bool _snapGuard =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_SNAPGUARD") == "1";

        private const float SnapFactor = 20f;
        private const int SnapWindow = 120;
        private const int SnapConfirm = 3;

        private static readonly bool _snapKeep =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_SNAPKEEP") == "1";

        private static readonly bool _snapLearn =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_SNAPLEARN") == "1";

        private static float _snapFloor =
            float.TryParse(Environment.GetEnvironmentVariable("RYUJINX_MVPP_SNAPFLOOR"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float sf) && sf > 0f ? sf : 0f;

        private static int _snapFloorUsed;

        private static int _snapHold =
            (int)(float.TryParse(Environment.GetEnvironmentVariable("RYUJINX_MVPP_SNAPHOLD"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float sh) && sh > 0f ? sh : 0f);

        /// <summary>
        /// Re-reads the fields above that come from the settings profile, so a changed upscaler
        /// setting applies at the next game start without restarting the app.
        /// </summary>
        internal static void ReloadProfileSettings()
        {
            {
                Enabled = Environment.GetEnvironmentVariable("RYUJINX_MVPP_VPSOLO") == "1";
            }
            {
                ElectMaxStep = float.TryParse(System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_ELECT_MAXSTEP"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float electMaxStep) && electMaxStep > 0f
                    ? electMaxStep
                    : 0f;
            }
            {
                _electHystMs = int.TryParse(System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_ELECT_HYST"), out int hy) && hy > 0 ? hy : 0;
            }
            {
                _electWarp = System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_ELECT_WARP") == "1";
            }
            {
                _deJitter = Environment.GetEnvironmentVariable("RYUJINX_MVPP_DEJITTER") == "1";
            }
            {
                _multiAddr = Environment.GetEnvironmentVariable("RYUJINX_MVPP_MULTIADDR") == "1";
            }
            {
                _multiMaxRot = float.TryParse(Environment.GetEnvironmentVariable("RYUJINX_MVPP_MULTIADDR_MAXROT"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float mr) && mr > 0f ? mr : 0f;
            }
            {
                _aspectLock = Environment.GetEnvironmentVariable("RYUJINX_MVPP_ASPECTLOCK") == "1";
            }
            {
                _skyReject = Environment.GetEnvironmentVariable("RYUJINX_MVPP_SKYREJECT") == "1";
            }
            {
                _snapGuard = Environment.GetEnvironmentVariable("RYUJINX_MVPP_SNAPGUARD") == "1";
            }
            {
                _snapFloor = float.TryParse(Environment.GetEnvironmentVariable("RYUJINX_MVPP_SNAPFLOOR"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float sf) && sf > 0f ? sf : 0f;
            }
            {
                _snapHold = (int)(float.TryParse(Environment.GetEnvironmentVariable("RYUJINX_MVPP_SNAPHOLD"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float sh) && sh > 0f ? sh : 0f);
            }
        }

        private static int _snapStreakMax;
        private static int _snapForced;

        private static readonly float[] _accSteps = new float[SnapWindow];
        private static int _accHead;
        private static int _accCount;
        private static int _consecutiveRejects;
        private static int _snapRefusals;
        private static long _lastSnapLogMs;

        private static readonly bool _cutOnJump =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_CUTONJUMP") == "1";

        private const float TeleportFactor = 50f;
        private const int WindowSize = 300;
        private const int ScaleRefresh = 30;

        private static readonly float[] _window = new float[WindowSize];
        private static readonly float[] _sorted = new float[WindowSize];
        private static int _windowHead;
        private static int _windowCount;
        private static int _scaleCountdown;
        private static float _scale;

        private static bool _hasLastGood;
        private static float _lastPx, _lastPy, _lastPz, _lastFx;
        private static long _lastAcceptMs;
        private static Matrix4x4 _lastGoodVp;
        private static int _guardRejects;

        internal static bool TryGetPublishedForProbe(out Matrix4x4 vp, out float px, out float py, out float pz)
        {
            vp = _lastGoodVp;
            px = _lastPx;
            py = _lastPy;
            pz = _lastPz;

            return _hasLastGood;
        }

        public static void NoteDepth(int width, int height)
        {
            if (!_armWitnessLogged)
            {
                _armWitnessLogged = true;
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP solo: ARMED (VPSOLO=1) — rotation-as-motion {(_rotMotion ? "ON (x" + RotMotionScale + ")" : "OFF")}.");
            }

            if (width == height || height <= 0)
            {
                return;
            }

            long area = (long)width * height;

            if (area >= _liveMainArea)
            {
                _liveMainArea = area;
                _mainWidth = width;
                _mainHeight = height;
                _mainAspect = (float)width / height;
            }
        }

        public static bool TryGetViewProjection(GpuChannel channel, out Matrix4x4 vp)
        {
            vp = default;

            if (!Enabled)
            {
                return false;
            }

            if (MvppFamHold.Enabled)
            {
                MvppFamHold.Sample(channel);
            }

            long now = Environment.TickCount64;

            if (_hasElection && TryReadAt(channel, _elStage, _elSlot, _elOffset, _elTransposed, out vp, out float apx, out float apy, out float apz))
            {
                _lastSeatOkMs = now;

                if (_mirrors)
                {
                    _ctPending.AsSpan().CopyTo(_mirSeatNow);

                    bool mirArmed = true;

                    if (_mirrorsAb)
                    {
                        mirArmed = ((now / MirrorsAbSliceMs) & 1) == 0;

                        if (!_mirAbKnown || mirArmed != _mirAbArmed)
                        {
                            _mirAbKnown = true;
                            _mirAbArmed = mirArmed;

                            Logger.Info?.Print(LogClass.Gpu,
                                $"MVPP mirrors AB: side={(mirArmed ? "ARMED" : "OFF")} (slices of {MirrorsAbSliceMs / 1000} s).");
                        }
                    }

                    if (mirArmed && _mirHasSeatLast && _mirSeatNow.AsSpan().SequenceEqual(_mirSeatLast))
                    {
                        _mirStale++;

                        if (_multiAddr)
                        {
                            _mirTried++;

                            if (TryReadHistory(channel, out Matrix4x4 mirVp, out float mpx, out float mpy, out float mpz))
                            {
                                _mirFound++;

                                Matrix4x4 mirCand = mirVp;

                                if (AcceptRead(channel, ref mirCand, mpx, mpy, mpz, now))
                                {
                                    _mirAccepted++;
                                    vp = mirCand;
                                    _mirSeatNow.AsSpan().CopyTo(_mirSeatLast);
                                    MirrorLog(now);

                                    if (CapTime)
                                    {
                                        NoteCapture();
                                    }

                                    return true;
                                }

                                _mirRefused++;
                            }
                            else
                            {
                                _mirNone++;
                            }
                        }
                    }

                    MirrorLog(now);
                }

                if (MvppCtxProbe.Enabled && _hasLastGood && !_probing)
                {
                    float cdx = apx - _lastPx;
                    float cdy = apy - _lastPy;
                    float cdz = apz - _lastPz;

                    MvppCtxProbe.NoteRead(channel, MathF.Sqrt(cdx * cdx + cdy * cdy + cdz * cdz),
                        apx, apy, apz, now - _lastAcceptMs);
                }

                bool accepted = AcceptRead(channel, ref vp, apx, apy, apz, now);

                if (_mirrors && accepted)
                {
                    _mirSeatNow.AsSpan().CopyTo(_mirSeatLast);
                    _mirHasSeatLast = true;
                }

                if (CapTime && accepted)
                {
                    NoteCapture();
                }

                return accepted;
            }

            if (_hasElection)
            {
                _readFails++;

                if (_altProbe)
                {
                    ProbeAlternates(channel);
                }

                if (_multiAddr && TryReadHistory(channel, out Matrix4x4 hvp, out float hpx, out float hpy, out float hpz))
                {
                    vp = hvp;

                    return AcceptRead(channel, ref vp, hpx, hpy, hpz, now);
                }
            }

            if (now - _lastElectionMs < ElectionIntervalMs)
            {
                return false;
            }

            if (_electHystMs > 0 && _hasElection)
            {
                bool armed = true;

                if (_electHystAb)
                {
                    armed = ((now / HystAbSliceMs) & 1) == 0;

                    if (!_hystAbSideKnown || armed != _hystAbArmed)
                    {
                        _hystAbSideKnown = true;
                        _hystAbArmed = armed;

                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP elect-hyst AB: side={(armed ? "ARMED" : "OFF")} (slices of {HystAbSliceMs / 1000} s).");
                    }
                }

                if (armed)
                {
                    bool seatAlive = now - _lastSeatOkMs < _electHystMs;
                    bool starving = now - _lastAcceptMs >= 2L * _electHystMs;

                    if (seatAlive && !starving)
                    {
                        _hystBlocked++;
                    }
                    else if (seatAlive)
                    {
                        _hystStarved++;
                    }
                    else
                    {
                        _hystExpired++;
                    }

                    if (now - _hystLogMs >= 5000)
                    {
                        _hystLogMs = now;

                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP elect-hyst: blocked={_hystBlocked} opened-seat-dead={_hystExpired} " +
                            $"opened-starved={_hystStarved} | seat last seen alive {now - _lastSeatOkMs} ms ago, " +
                            $"last acceptance {now - _lastAcceptMs} ms ago.");

                        _hystBlocked = 0;
                        _hystExpired = 0;
                        _hystStarved = 0;
                    }

                    if (seatAlive && !starving)
                    {
                        return false;
                    }
                }
            }

            _lastElectionMs = now;

            if (!Elect(channel))
            {
                return false;
            }

            if (!TryReadAt(channel, _elStage, _elSlot, _elOffset, _elTransposed, out vp, out float epx, out float epy, out float epz))
            {
                return false;
            }

            if (_hasLastGood)
            {
                float epdx = epx - _lastPx;
                float epdy = epy - _lastPy;
                float epdz = epz - _lastPz;
                float epStep = MathF.Sqrt(epdx * epdx + epdy * epdy + epdz * epdz);
                float epRot = RotGap(in vp);

                bool epWarp = false;

                if (_electWarp && (epRot >= 0.5f || epStep >= 10f))
                {
                    GAL.DlssCameraState.TeleportSeq++;
                    epWarp = true;
                }

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP election read: step={epStep:0.##} u, rotDelta={epRot:0.####}, " +
                    $"reference age {Environment.TickCount64 - _lastAcceptMs} ms" +
                    $"{(epWarp ? $" -> RESET requested (camera-warp #{GAL.DlssCameraState.TeleportSeq})" : "")}.");
            }

            return true;
        }

        private static bool AcceptRead(GpuChannel channel, ref Matrix4x4 vp, float apx, float apy, float apz, long now)
        {
            {
                _reads++;

                if (_electRot > 0f && _hasLastGood)
                {
                    if (RotGapTo(in vp, in _lastGoodVp) >= _electRot)
                    {
                        if (_electRejectStreak < ElectRotMaxStreak)
                        {
                            _electRejectStreak++;
                            _electRejected++;

                            return false;
                        }

                        _electRejectStreak = 0;
                        _electForced++;
                    }
                    else
                    {
                        _electRejectStreak = 0;
                    }
                }

                MvppProjAudit.Audit(in vp, apx, apy, apz);

                if (MvppCamTrace.Enabled)
                {
                    MvppCamTrace.Note(
                        channel.BufferManager.GetGraphicsUniformBufferAddress(_elStage, _elSlot),
                        apx, apy, apz, _lastJx, _lastJy, _deJitter);
                }

                bool continuous = IsContinuous(in vp, apx, apy, apz);

                if (!continuous)
                {
                    _guardRejects++;
                }

                if (_camGuard && !continuous && _hasLastGood)
                {
                    vp = _lastGoodVp;

                    return true;
                }

                if (_snapGuard && !AcceptStep(apx, apy, apz))
                {
                    vp = _lastGoodVp;

                    return true;
                }

                if (_subProbe > 0f && _hasLastGood && !_probing)
                {
                    SubProbe(channel, in vp, apx, apy, apz, now);
                }

                _lastGoodVp = vp;
                _lastPx = apx;
                _lastPy = apy;
                _lastPz = apz;
                _lastFx = MathF.Sqrt(vp.M11 * vp.M11 + vp.M12 * vp.M12 + vp.M13 * vp.M13);
                _lastAcceptMs = now;
                _hasLastGood = true;

                if (!_probing)
                {
                    _lastAcceptedJx = _lastJx;
                    _lastAcceptedJy = _lastJy;
                    NoteJitter(_lastJx, _lastJy);
                }

                if (GAL.MvppDev.Enabled && now - _lastLogMs >= 5000)
                {
                    _lastLogMs = now;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP solo camera: reading stage{_elStage} cbuf{_elSlot} +0x{_elOffset:X3}" +
                        $"{(_elTransposed ? "^T" : "")}, {_reads} reads, {_readFails} misses, {_elections} elections, " +
                        $"CAMGUARD {(_camGuard ? "ARMED" : "measure only")}: {_guardRejects} foreign reads out of {_reads} " +
                        $"({(_reads > 0 ? 100f * _guardRejects / _reads : 0f):0.##} %).");
                    _reads = 0;
                    _readFails = 0;
                    _guardRejects = 0;
                }

                if (_multiAddr)
                {
                    _goodFlat.CopyTo(_goodFlatPrev, 0);
                    Flatten(in vp, _goodFlat);

                    if (_goodCount < 2)
                    {
                        _goodCount++;
                    }
                }

                return true;
            }
        }

        internal static bool TryProbeRead(GpuChannel channel, out float dist)
        {
            dist = 0f;

            if (!_hasElection)
            {
                return false;
            }

            float savedJx = _lastJx;
            float savedJy = _lastJy;
            bool savedProbing = _probing;

            _probing = true;

            try
            {
                if (!TryReadAt(channel, _elStage, _elSlot, _elOffset, _elTransposed,
                        out _, out float px, out float py, out float pz))
                {
                    return false;
                }

                if (_hasLastGood)
                {
                    float dx = px - _lastPx;
                    float dy = py - _lastPy;
                    float dz = pz - _lastPz;

                    dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                }

                return true;
            }
            finally
            {
                _probing = savedProbing;
                _lastJx = savedJx;
                _lastJy = savedJy;
            }
        }

        private static bool TryReadAt(
            GpuChannel channel,
            int stage,
            int slot,
            int offset,
            bool transposed,
            out Matrix4x4 vp,
            out float px,
            out float py,
            out float pz)
        {
            vp = default;
            px = py = pz = 0f;

            if (!TryReadBuffer(channel, stage, slot, out ReadOnlySpan<float> data))
            {
                return Fail(0);
            }

            int idx = offset / 4;

            if (idx < 0 || idx + 16 > data.Length)
            {
                return Fail(1);
            }

            ReadOnlySpan<float> raw = data.Slice(idx, 16);

            if (transposed)
            {
                Transpose(raw, _tmp);
                _tmp.CopyTo(_read.AsSpan());
            }
            else
            {
                raw.CopyTo(_read.AsSpan());
            }

            if (CapTime || CapTimeFix || _mirrors)
            {
                raw.CopyTo(_ctPending);
            }

            float jx = 0f, jy = 0f;

            if (_deJitter || MvppCamTrace.Enabled || _jitProbe)
            {
                MeasureJitter(_read, out jx, out jy);
                _lastJx = jx;
                _lastJy = jy;
            }

            if (_deJitter)
            {
                RemoveJitter(_read, jx, jy);
            }

            ReadOnlySpan<float> m = _read;

            if (!IsViewProj(m, out float fx, out float fy))
            {
                return Fail(2);
            }

            if (MathF.Abs(fy / fx - 1f) < 0.2f)
            {
                return Fail(3);
            }

            if (_aspectLock && _mainAspect > 0f && MathF.Abs(fy / fx / _mainAspect - 1f) > 0.05f)
            {
                return Fail(4);
            }

            ViewProjPos(m, fx, fy, out px, out py, out pz);

            if (!float.IsFinite(px) || !float.IsFinite(py) || !float.IsFinite(pz) ||
                MathF.Abs(px) > AbsurdPos || MathF.Abs(py) > AbsurdPos || MathF.Abs(pz) > AbsurdPos)
            {
                return Fail(5);
            }

            if (_skyReject && _hasLastGood)
            {
                float here = MathF.Sqrt(px * px + py * py + pz * pz);
                float there = MathF.Sqrt(_lastPx * _lastPx + _lastPy * _lastPy + _lastPz * _lastPz);

                if (here < there * 0.01f)
                {
                    return Fail(6);
                }
            }

            vp = new Matrix4x4(
                m[0], m[1], m[2], m[3],
                m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11],
                m[12], m[13], m[14], m[15]);

            return true;
        }

        private static bool Elect(GpuChannel channel)
        {
            if (_rotMotion && !_rotArmedLogged)
            {
                _rotArmedLogged = true;
                Logger.Info?.Print(LogClass.Gpu,
                    "MVPP solo: rotation-as-motion armed (VPSOLO_ROT, scale x" + RotMotionScale + ").");
            }

            _candCount = 0;

            for (int stage = 0; stage < Constants.ShaderStages && _candCount < MaxCandidates; stage++)
            {
                uint mask = channel.BufferManager.GetGraphicsUniformBufferUseMask(stage);

                for (int slot = 0; mask != 0 && _candCount < MaxCandidates; slot++, mask >>= 1)
                {
                    if ((mask & 1) == 0)
                    {
                        continue;
                    }

                    if (!TryReadBuffer(channel, stage, slot, out ReadOnlySpan<float> data))
                    {
                        continue;
                    }

                    ulong address = channel.BufferManager.GetGraphicsUniformBufferAddress(stage, slot);

                    for (int i = 0; i + 16 <= data.Length && _candCount < MaxCandidates; i += 4)
                    {
                        ReadOnlySpan<float> raw = data.Slice(i, 16);
                        bool transposed = false;

                        if (!IsViewProj(raw, out float fx, out float fy))
                        {
                            Transpose(raw, _tmp);

                            if (!IsViewProj(_tmp, out fx, out fy))
                            {
                                continue;
                            }

                            transposed = true;
                        }

                        if (MathF.Abs(fy / fx - 1f) < 0.2f)
                        {
                            continue;
                        }

                        ReadOnlySpan<float> m = transposed ? _tmp : raw;
                        ViewProjPos(m, fx, fy, out float px, out float py, out float pz);

                        if (!float.IsFinite(px) || !float.IsFinite(py) || !float.IsFinite(pz) ||
                            MathF.Abs(px) > AbsurdPos || MathF.Abs(py) > AbsurdPos || MathF.Abs(pz) > AbsurdPos)
                        {
                            continue;
                        }

                        float cdx = 0f, cdy = 0f, cdz = 0f;

                        if (_rotMotion)
                        {
                            float dn = MathF.Sqrt(m[12] * m[12] + m[13] * m[13] + m[14] * m[14]);

                            if (float.IsFinite(dn) && dn > 1e-6f)
                            {
                                cdx = m[12] / dn;
                                cdy = m[13] / dn;
                                cdz = m[14] / dn;
                            }
                        }

                        _cands[_candCount++] = new Candidate
                        {
                            Stage = stage,
                            Slot = slot,
                            Offset = i * 4,
                            Transposed = transposed,
                            Address = address,
                            Aspect = fy / fx,
                            X = px,
                            Y = py,
                            Z = pz,
                            DirX = cdx,
                            DirY = cdy,
                            DirZ = cdz,
                        };
                    }
                }
            }

            if (_candCount == 0)
            {
                return false;
            }

            float posScale = 0f;

            if (_skyReject)
            {
                for (int i = 0; i < _candCount; i++)
                {
                    ref Candidate c = ref _cands[i];
                    float n = MathF.Sqrt(c.X * c.X + c.Y * c.Y + c.Z * c.Z);

                    if (n > posScale)
                    {
                        posScale = n;
                    }
                }
            }

            float bestMotion = 0f;
            float bestStraight = 0f;
            int considered = 0;

            for (int i = 0; i < _candCount; i++)
            {
                ref Candidate a = ref _cands[i];

                if (_mainAspect > 0f && MathF.Abs(a.Aspect / _mainAspect - 1f) > 0.05f)
                {
                    a.Motion = -1f;

                    continue;
                }

                if (posScale > 0f &&
                    MathF.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z) < posScale * 0.01f)
                {
                    a.Motion = -1f;

                    continue;
                }

                considered++;
                a.Motion = Track(in a, out float straight);
                a.Straightness = straight;

                bool aberrant = ElectMaxStep > 0f &&
                                (a.Motion > ElectMaxStep || ElectIsBanned(a.Stage, a.Slot, a.Offset));

                if (a.Motion > bestMotion && !aberrant)
                {
                    bestMotion = a.Motion;
                }

                if (a.Motion > 0f && straight > bestStraight && !aberrant)
                {
                    bestStraight = straight;
                }
            }

            int best = -1;
            int bestVotes = 0;

            for (int i = 0; i < _candCount; i++)
            {
                ref Candidate a = ref _cands[i];

                if (a.Motion < 0f)
                {
                    continue;
                }

                if (ElectMaxStep > 0f)
                {
                    bool abberantMaintenant = a.Motion > ElectMaxStep;
                    bool grille = ElectIsBanned(a.Stage, a.Slot, a.Offset);

                    if (abberantMaintenant && !grille && _electBannedCount < ElectBannedMax)
                    {
                        _electBanned[_electBannedCount++] = ElectBanKey(a.Stage, a.Slot, a.Offset);
                        grille = true;

                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP ELECTMAXSTEP: location BANNED -- stage{a.Stage} cbuf{a.Slot} " +
                            $"+0x{a.Offset:X3} motion={a.Motion:0.##} > threshold {ElectMaxStep:0.##} " +
                            $"(it can no longer be elected this session; {_electBannedCount} banned).");
                    }

                    if (abberantMaintenant || grille)
                    {
                        _electMaxStepRejected++;

                        if ((_electMaxStepRejected % 20000) == 0)
                        {
                            Logger.Info?.Print(LogClass.Gpu,
                                $"MVPP ELECTMAXSTEP: {_electMaxStepRejected} rejections in total, " +
                                $"{_electBannedCount} location(s) banned.");
                        }

                        continue;
                    }
                }

                if (bestMotion > 0f && a.Motion < bestMotion * 0.1f)
                {
                    continue;
                }

                if (bestStraight > 0f && a.Straightness < bestStraight * 0.35f)
                {
                    continue;
                }

                int votes = 0;

                for (int j = 0; j < _candCount; j++)
                {
                    ref Candidate b = ref _cands[j];

                    if (b.Motion < 0f ||
                        MathF.Abs(b.X - a.X) > PosAgreeEps ||
                        MathF.Abs(b.Y - a.Y) > PosAgreeEps ||
                        MathF.Abs(b.Z - a.Z) > PosAgreeEps)
                    {
                        continue;
                    }

                    bool duplicate = false;

                    for (int k = 0; k < j; k++)
                    {
                        if (_cands[k].Motion >= 0f && _cands[k].Address == b.Address && _cands[k].Offset == b.Offset &&
                            MathF.Abs(_cands[k].X - a.X) <= PosAgreeEps &&
                            MathF.Abs(_cands[k].Y - a.Y) <= PosAgreeEps &&
                            MathF.Abs(_cands[k].Z - a.Z) <= PosAgreeEps)
                        {
                            duplicate = true;

                            break;
                        }
                    }

                    if (!duplicate)
                    {
                        votes++;
                    }
                }

                if (votes < 2)
                {
                    continue;
                }

                if (votes > bestVotes)
                {
                    bestVotes = votes;
                    best = i;
                }
            }

            if (best < 0 || _mainAspect <= 0f || bestMotion <= 0f)
            {
                return false;
            }

            ref Candidate w = ref _cands[best];

            bool changed = !_hasElection || _elStage != w.Stage || _elSlot != w.Slot ||
                           _elOffset != w.Offset || _elTransposed != w.Transposed;

            _hasElection = true;
            _elStage = w.Stage;
            _elSlot = w.Slot;
            _elOffset = w.Offset;
            _elTransposed = w.Transposed;
            _elections++;

            PushHistory(w.Stage, w.Slot, w.Offset, w.Transposed);

            if (changed)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP solo camera: elected stage{w.Stage} cbuf{w.Slot} +0x{w.Offset:X3}" +
                    $"{(w.Transposed ? "^T" : "")} @{w.Address:X10} pos=[{w.X:0.#} {w.Y:0.#} {w.Z:0.#}] " +
                    $"aspect={w.Aspect:0.###} motion={w.Motion:0.##} straight={w.Straightness:0.##} " +
                    $"({bestVotes} agreeing buffers of {considered} at the right aspect, election #{_elections}).");
            }

            return true;
        }

        private static float Track(in Candidate c, out float straightness)
        {
            straightness = 1f;

            int idx = -1;

            for (int i = 0; i < _trackCount; i++)
            {
                if (_tracks[i].Address == c.Address && _tracks[i].Offset == c.Offset)
                {
                    idx = i;

                    break;
                }
            }

            if (idx < 0)
            {
                if (_trackCount >= _tracks.Length)
                {
                    return 0f;
                }

                idx = _trackCount++;
                _tracks[idx] = new Tracked { Address = c.Address, Offset = c.Offset };
            }

            ref Tracked t = ref _tracks[idx];

            t.Motion *= 0.5f;

            float step = 0f;

            if (t.HasLast)
            {
                float dx = c.X - t.LastX;
                float dy = c.Y - t.LastY;
                float dz = c.Z - t.LastZ;
                step = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

                if (!float.IsFinite(step) || step > TeleportEps)
                {
                    step = 0f;
                }

                if (step > t.Motion)
                {
                    t.Motion = step;
                }
            }

            float rotStep = 0f;

            if (_rotMotion && t.HasLastDir)
            {
                float rdx = c.DirX - t.LastDirX;
                float rdy = c.DirY - t.LastDirY;
                float rdz = c.DirZ - t.LastDirZ;
                float sdx = c.DirX + t.LastDirX;
                float sdy = c.DirY + t.LastDirY;
                float sdz = c.DirZ + t.LastDirZ;

                float dirStep = MathF.Min(
                    MathF.Sqrt(rdx * rdx + rdy * rdy + rdz * rdz),
                    MathF.Sqrt(sdx * sdx + sdy * sdy + sdz * sdz));

                if (float.IsFinite(dirStep))
                {
                    rotStep = dirStep * RotMotionScale;

                    if (rotStep > t.Motion)
                    {
                        t.Motion = rotStep;
                    }
                }
            }

            int slot = idx * RingSize;

            if (t.RingCount == RingSize)
            {
                t.PathLength -= _ringStep[slot + t.RingHead];
            }

            _ringX[slot + t.RingHead] = c.X;
            _ringY[slot + t.RingHead] = c.Y;
            _ringZ[slot + t.RingHead] = c.Z;
            _ringStep[slot + t.RingHead] = step;
            t.PathLength += step;
            t.RingHead = (t.RingHead + 1) % RingSize;

            if (t.RingCount < RingSize)
            {
                t.RingCount++;
            }

            if (t.RingCount >= 3 && t.PathLength > 1e-4f)
            {
                int oldest = (t.RingHead - t.RingCount + RingSize * 2) % RingSize;
                float nx = c.X - _ringX[slot + oldest];
                float ny = c.Y - _ringY[slot + oldest];
                float nz = c.Z - _ringZ[slot + oldest];

                straightness = MathF.Sqrt(nx * nx + ny * ny + nz * nz) / t.PathLength;
            }

            if (_rotMotion && rotStep > step)
            {
                straightness = 1f;
            }

            t.LastX = c.X;
            t.LastY = c.Y;
            t.LastZ = c.Z;
            t.HasLast = true;

            if (_rotMotion)
            {
                t.LastDirX = c.DirX;
                t.LastDirY = c.DirY;
                t.LastDirZ = c.DirZ;
                t.HasLastDir = true;
            }

            return t.Motion;
        }

        private static bool TryReadBuffer(GpuChannel channel, int stage, int slot, out ReadOnlySpan<float> data)
        {
            data = default;

            ulong address = channel.BufferManager.GetGraphicsUniformBufferAddress(stage, slot);
            int size = Math.Min(channel.BufferManager.GetGraphicsUniformBufferSize(stage, slot), MaxBytesPerBuffer);

            if (address == 0 || address == ulong.MaxValue || size < 64)
            {
                return false;
            }

            try
            {
                data = MemoryMarshal.Cast<byte, float>(channel.MemoryManager.Physical.GetSpan(address, size));
            }
            catch
            {
                return false;
            }

            return true;
        }

        public static void OnPresentBoundary(bool captured)
        {
            _ctPrevFrameMissed = !captured;

            if (_abBlock <= 0)
            {
                return;
            }

            int side = _abArmed ? 1 : 0;

            _abFrames[side]++;

            if (captured)
            {
                _abCaptures[side]++;
            }

            _abPresents++;

            bool armedNext = (_abPresents / _abBlock) % 2 == 1;

            if (armedNext == _abArmed)
            {
                return;
            }

            long f = _abFrames[side];
            long fails = 0;

            for (int i = 0; i < FailReasons; i++)
            {
                fails += _fail[i];
            }

            Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                $"CAPTIME-AB [{(side == 1 ? "ARMED" : "OFF  ")}] presents={f} " +
                $"capture={(f > 0 ? 100f * _abCaptures[side] / f : 0f):0.#} % " +
                $"noCapture={f - _abCaptures[side]} " +
                $"moved={(f > 0 ? 100f * _abMoved[side] / f : 0f):0.#} % " +
                $"avgDraws={(f > 0 ? (float)_abPreSum[side] / f : 0f):0.#} " +
                $"A={_abA[side]} B={_abB[side]} " +
                $"rateA={(_abA[side] + _abB[side] > 0 ? 100f * _abA[side] / (_abA[side] + _abB[side]) : 0f):0.#} % " +
                $"transitions={_abTrans[side]} " +
                $"fails+={fails - _abFailAtSwitch[side]}");

            _abFailAtSwitch[side] = fails;
            _abFrames[side] = 0;
            _abCaptures[side] = 0;
            _abMoved[side] = 0;
            _abPreSum[side] = 0;
            _abA[side] = 0;
            _abB[side] = 0;
            _abTrans[side] = 0;
            _abArmed = armedNext;
        }

        public static bool StaleAtElectedLocation(GpuChannel channel)
        {
            _whyCalled++;

            if (!FixActive || _ctMaxSkip <= 0)
            {
                _whyOff++;

                return false;
            }

            _whyArmedCall++;

            if (_ctPreDraw >= _ctMaxSkip)
            {
                _whyDeadline++;

                return false;
            }

            if (_ctPrevFrameMissed)
            {
                _whyNetted++;

                return false;
            }

            if (!_hasElection)
            {
                _whyNoElection++;

                return false;
            }

            if (!_ctHasCapture)
            {
                _whyNoRef++;

                return false;
            }

            ulong address = channel.BufferManager.GetGraphicsUniformBufferAddress(_elStage, _elSlot);
            int size = channel.BufferManager.GetGraphicsUniformBufferSize(_elStage, _elSlot);

            if (address == 0 || address == ulong.MaxValue || _elOffset + 64 > size)
            {
                _whyUnbound++;

                return false;
            }

            _whyReadable++;

            ReadOnlySpan<byte> block = channel.MemoryManager.Physical.GetSpan(address + (ulong)_elOffset, 64);
            ReadOnlySpan<float> vals = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(block);

            if (!IsViewProj(vals, out _, out _))
            {
                _whyBadShape++;

                return false;
            }

            if (!vals.SequenceEqual(_ctCaptured))
            {
                _whyDiffers++;

                return false;
            }

            _whyArmed++;
            _ctPreDraw++;

            return true;
        }

        public static void LogWhy()
        {
            if (!CapTime && !CapTimeFix && _abBlock <= 0)
            {
                return;
            }

            if (Environment.TickCount64 - _whyLogMs < 5000)
            {
                return;
            }

            _whyLogMs = Environment.TickCount64;

            Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                $"CAPTIME-WHY calls={_whyCalled} (off={_whyOff} armed={_whyArmedCall}) " +
                $"| noElection={_whyNoElection} noReference={_whyNoRef} " +
                $"slotUnbound={_whyUnbound} readable={_whyReadable} " +
                $"badShape={_whyBadShape} valueDiffers={_whyDiffers} " +
                $"deadline={_whyDeadline} net={_whyNetted} " +
                $"ARMED={_whyArmed} | K={_ctMaxSkip} elected=stage{_elStage} cbuf{_elSlot} +0x{_elOffset:X3} " +
                $"transpose={(_elTransposed ? 1 : 0)}");

            _whyCalled = 0;
            _whyOff = 0;
            _whyArmedCall = 0;
            _whyNoElection = 0;
            _whyNoRef = 0;
            _whyUnbound = 0;
            _whyReadable = 0;
            _whyBadShape = 0;
            _whyDiffers = 0;
            _whyArmed = 0;
            _whyDeadline = 0;
            _whyNetted = 0;
        }

        public static void NoteLateDraw(GpuChannel channel)
        {
            if (!CapTime || !_hasElection || !_ctHasCapture)
            {
                return;
            }

            _ctDraw++;

            ulong address = channel.BufferManager.GetGraphicsUniformBufferAddress(_elStage, _elSlot);
            int size = channel.BufferManager.GetGraphicsUniformBufferSize(_elStage, _elSlot);

            if (address == 0 || address == ulong.MaxValue || _elOffset + 64 > size)
            {
                _ctMissing++;

                return;
            }

            _ctBound++;

            ReadOnlySpan<byte> block = channel.MemoryManager.Physical.GetSpan(address + (ulong)_elOffset, 64);
            ReadOnlySpan<float> vals = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(block);

            if (!IsViewProj(vals, out _, out _))
            {
                return;
            }

            for (int i = 0; i < _ctCount; i++)
            {
                if (vals.SequenceEqual(_ctHist.AsSpan(i * 16, 16)))
                {
                    return;
                }
            }

            if (_ctCount < CtMax)
            {
                vals.CopyTo(_ctHist.AsSpan(_ctCount * 16, 16));
                _ctHistDraw[_ctCount] = _ctDraw;
                _ctCount++;
            }
        }

        private static void NoteCapture()
        {
            if (_ctHasCapture)
            {
                int seenAt = -1;

                for (int i = 0; i < _ctCount; i++)
                {
                    if (_ctPending.AsSpan().SequenceEqual(_ctHist.AsSpan(i * 16, 16)))
                    {
                        seenAt = _ctHistDraw[i];

                        break;
                    }
                }

                if (_abBlock > 0)
                {
                    int side = _abArmed ? 1 : 0;
                    char v = seenAt >= 0 ? 'A' : _ctBound > 0 ? 'B' : '?';

                    if (v == 'A')
                    {
                        _abA[side]++;
                    }
                    else if (v == 'B')
                    {
                        _abB[side]++;
                    }

                    if (v != '?')
                    {
                        if (_abLastVerdict != '\0' && _abLastVerdict != v)
                        {
                            _abTrans[side]++;
                        }

                        _abLastVerdict = v;
                    }

                    if (_ctPreDraw > 0)
                    {
                        _abMoved[side]++;
                    }

                    _abPreSum[side] += _ctPreDraw;
                }

                string verdict = seenAt >= 0 ? "A (already seen -> capture too early)"
                    : _ctBound > 0 ? "B (never seen -> the game had not produced it)"
                    : "DISCARDED (zero coverage)";

                if (_abBlock == 0)
                {
                    Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                        $"CAPTIME frame={_ctFrameId} verdict={verdict} seenAtDraw={seenAt} " +
                        $"distinct={_ctCount} slotBound={_ctBound} slotMissing={_ctMissing} " +
                        $"drawsAfterCapture={_ctDraw}");
                }
            }

            if (CapTimeFix)
            {
                _ctTotalFrames++;
                _ctPreDrawSum += _ctPreDraw;

                if (_ctPreDraw > 0)
                {
                    _ctMovedFrames++;
                }

                if (Environment.TickCount64 - _ctMovedLogMs >= 5000)
                {
                    _ctMovedLogMs = Environment.TickCount64;

                    Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                        $"CAPTIME-FIX: capture moved on {_ctMovedFrames}/{_ctTotalFrames} frames " +
                        $"({(_ctTotalFrames > 0 ? 100f * _ctMovedFrames / _ctTotalFrames : 0f):0.#} %), " +
                        $"draws let through: mean {(_ctTotalFrames > 0 ? (float)_ctPreDrawSum / _ctTotalFrames : 0f):0.#}, " +
                        $"last {_ctPreDraw}.");

                    _ctMovedFrames = 0;
                    _ctTotalFrames = 0;
                    _ctPreDrawSum = 0;
                }
            }

            if (_rankProbe && _ctHasCapture)
            {
                int firstNew = -1;

                for (int i = 0; i < _ctCount; i++)
                {
                    if (!_ctHist.AsSpan(i * 16, 16).SequenceEqual(_ctCaptured))
                    {
                        firstNew = _ctHistDraw[i];

                        break;
                    }
                }

                _rkFrames++;
                _rkDrawSum += _ctDraw;

                if (firstNew >= 0)
                {
                    int b = firstNew <= 40 ? 0 : firstNew <= 60 ? 1 : firstNew <= 80 ? 2
                        : firstNew <= 120 ? 3 : firstNew <= 160 ? 4 : 5;

                    _rkBuckets[b]++;
                }
                else if (_ctCount >= CtMax)
                {
                    _rkSaturated++;
                }
                else if (_ctBound > 0)
                {
                    _rkNever++;
                }
                else
                {
                    _rkNoCover++;
                }

                long rkNow = Environment.TickCount64;

                if (rkNow - _rkLogMs >= 5000)
                {
                    _rkLogMs = rkNow;

                    Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                        $"MVPP arrival-rank: frames={_rkFrames} | <=40:{_rkBuckets[0]} 41-60:{_rkBuckets[1]} " +
                        $"61-80:{_rkBuckets[2]} 81-120:{_rkBuckets[3]} 121-160:{_rkBuckets[4]} >160:{_rkBuckets[5]} " +
                        $"| NEVER:{_rkNever} discarded:{_rkNoCover} saturated:{_rkSaturated} " +
                        $"| mean draws-scanned={(_rkFrames > 0 ? (float)_rkDrawSum / _rkFrames : 0f):0.#} " +
                        $"| elected=stage{_elStage} cbuf{_elSlot} +0x{_elOffset:X3}");

                    Array.Clear(_rkBuckets);
                    _rkNever = 0;
                    _rkNoCover = 0;
                    _rkSaturated = 0;
                    _rkFrames = 0;
                    _rkDrawSum = 0;
                }
            }

            _ctPending.AsSpan().CopyTo(_ctCaptured);
            _ctHasCapture = true;
            _ctFrameId++;
            _ctCount = 0;
            _ctBound = 0;
            _ctMissing = 0;
            _ctDraw = 0;
            _ctPreDraw = 0;
        }

        private static bool IsViewProj(ReadOnlySpan<float> m, out float fx, out float fy)
        {
            fx = 0f;
            fy = 0f;

            float n3 = MathF.Sqrt(m[12] * m[12] + m[13] * m[13] + m[14] * m[14]);

            if (MathF.Abs(n3 - 1f) > 0.02f)
            {
                return false;
            }

            float n0 = MathF.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]);
            float n1 = MathF.Sqrt(m[4] * m[4] + m[5] * m[5] + m[6] * m[6]);

            if (!float.IsFinite(n0) || !float.IsFinite(n1) ||
                n0 < 0.05f || n1 < 0.05f || n0 > 50f || n1 > 50f)
            {
                return false;
            }

            float d01 = (m[0] * m[4] + m[1] * m[5] + m[2] * m[6]) / (n0 * n1);
            float d03 = (m[0] * m[12] + m[1] * m[13] + m[2] * m[14]) / n0;
            float d13 = (m[4] * m[12] + m[5] * m[13] + m[6] * m[14]) / n1;

            if (MathF.Abs(d01) > 0.02f || MathF.Abs(d03) > 0.02f || MathF.Abs(d13) > 0.02f)
            {
                return false;
            }

            float n2 = MathF.Sqrt(m[8] * m[8] + m[9] * m[9] + m[10] * m[10]);

            if (n2 > 1e-4f)
            {
                float cx = m[9] * m[14] - m[10] * m[13];
                float cy = m[10] * m[12] - m[8] * m[14];
                float cz = m[8] * m[13] - m[9] * m[12];

                if (MathF.Sqrt(cx * cx + cy * cy + cz * cz) / n2 > 0.02f)
                {
                    return false;
                }
            }

            bool rotIsIdentity =
                MathF.Abs(m[0] / n0 - 1f) < 1e-3f && MathF.Abs(m[1]) < 1e-3f && MathF.Abs(m[2]) < 1e-3f &&
                MathF.Abs(m[4]) < 1e-3f && MathF.Abs(m[5] / n1 - 1f) < 1e-3f && MathF.Abs(m[6]) < 1e-3f &&
                MathF.Abs(m[12]) < 1e-3f && MathF.Abs(m[13]) < 1e-3f;

            bool noTranslation =
                MathF.Abs(m[3]) < 1e-3f && MathF.Abs(m[7]) < 1e-3f && MathF.Abs(m[15]) < 1e-3f;

            if (rotIsIdentity && noTranslation)
            {
                return false;
            }

            fx = n0;
            fy = n1;

            return true;
        }

        private static void ViewProjPos(ReadOnlySpan<float> m, float fx, float fy, out float x, out float y, out float z)
        {
            float r00 = m[0] / fx, r01 = m[1] / fx, r02 = m[2] / fx;
            float r10 = m[4] / fy, r11 = m[5] / fy, r12 = m[6] / fy;
            float r20 = m[12], r21 = m[13], r22 = m[14];

            float tx = m[3] / fx;
            float ty = m[7] / fy;
            float tz = m[15];

            x = -(r00 * tx + r10 * ty + r20 * tz);
            y = -(r01 * tx + r11 * ty + r21 * tz);
            z = -(r02 * tx + r12 * ty + r22 * tz);
        }

        private static bool IsContinuous(in Matrix4x4 vp, float px, float py, float pz)
        {
            if (!_hasLastGood)
            {
                return true;
            }

            float dx = px - _lastPx;
            float dy = py - _lastPy;
            float dz = pz - _lastPz;

            float dtSec = MathF.Max(0f, (Environment.TickCount64 - _lastAcceptMs) / 1000f);
            float budget = MathF.Max(MinBudget, MathF.Min(dtSec, CatchUpCapSec) * MaxSpeed);
            float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            float fx = MathF.Sqrt(vp.M11 * vp.M11 + vp.M12 * vp.M12 + vp.M13 * vp.M13);

            if (dist > budget)
            {
                LogReject("distance", dist, budget, fx, dtSec);

                return false;
            }

            if (_lastFx > 1e-6f && MathF.Abs(fx / _lastFx - 1f) > MaxFocalDrift)
            {
                LogReject("focal", dist, budget, fx, dtSec);

                return false;
            }

            return true;
        }

        private static bool AcceptStep(float px, float py, float pz)
        {
            if (!_hasLastGood)
            {
                return true;
            }

            float dx = px - _lastPx;
            float dy = py - _lastPy;
            float dz = pz - _lastPz;
            float step = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

            if (!float.IsFinite(step))
            {
                return false;
            }

            if (_snapLearn && step <= 0f)
            {
                _consecutiveRejects = 0;

                return true;
            }

            float scale = 0f;

            for (int i = 0; i < _accCount; i++)
            {
                if (_accSteps[i] > scale)
                {
                    scale = _accSteps[i];
                }
            }

            if (_snapFloor > 0f && scale < _snapFloor)
            {
                scale = _snapFloor;
                _snapFloorUsed++;
            }

            if (_accCount < 8 || step <= scale * SnapFactor)
            {
                _accSteps[_accHead] = step;
                _accHead = (_accHead + 1) % SnapWindow;

                if (_accCount < SnapWindow)
                {
                    _accCount++;
                }

                _consecutiveRejects = 0;

                return true;
            }

            int confirm = _snapHold > 0 ? _snapHold : SnapConfirm;

            _snapStreakMax = Math.Max(_snapStreakMax, ++_consecutiveRejects);

            if (_consecutiveRejects < confirm)
            {
                _snapRefusals++;

                long snapNow = Environment.TickCount64;

                if (snapNow - _lastSnapLogMs >= 5000)
                {
                    _lastSnapLogMs = snapNow;

                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP snapguard: {_snapRefusals} reads refused (jump > {scale * SnapFactor:0.##}), " +
                        $"last = {step:0.##} from [{_lastPx:0.#} {_lastPy:0.#} {_lastPz:0.#}]" +
                        $" | cap {confirm}, forced-adoptions {_snapForced}, max-streak {_snapStreakMax}" +
                        $", scale {scale:0.###} over {_accCount} learned steps" +
                        $"{(_snapFloor > 0f ? $" | SNAPFLOOR {_snapFloor:0.###} (floor bar {_snapFloor * SnapFactor:0.#} u) : {_snapFloorUsed} times in effect" : "")}" +
                        $"{(scale <= 0f ? "  <-- BAR AT ZERO: the guard refuses everything" : "")}" +
                        $"{(_snapHold > 0 && _snapStreakMax >= confirm ? "  <-- STUCK AT CAP: too low" : "")}.");
                    _snapRefusals = 0;
                    _snapForced = 0;
                    _snapStreakMax = 0;
                }

                return false;
            }

            _consecutiveRejects = 0;
            _snapForced++;

            if (!_snapKeep)
            {
                _accCount = 0;
                _accHead = 0;
            }

            return true;
        }

        private static void NoteJump(float px, float py, float pz)
        {
            if (!_cutOnJump || !_hasLastGood)
            {
                return;
            }

            float dx = px - _lastPx;
            float dy = py - _lastPy;
            float dz = pz - _lastPz;
            float step = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

            if (!float.IsFinite(step))
            {
                return;
            }

            _window[_windowHead] = step;
            _windowHead = (_windowHead + 1) % WindowSize;

            if (_windowCount < WindowSize)
            {
                _windowCount++;

                return;
            }

            if (--_scaleCountdown <= 0)
            {
                _scaleCountdown = ScaleRefresh;
                _window.CopyTo(_sorted, 0);
                Array.Sort(_sorted);
                _scale = _sorted[(int)(WindowSize * 0.9f)];
            }

            if (_scale > 0f && step > _scale * TeleportFactor)
            {
                GAL.DlssCameraState.TeleportSeq++;

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP camera warp: step={step:0.##} vs typical {_scale:0.#####} " +
                    $"(x{step / _scale:0.} over) -> DLSS history reset #{GAL.DlssCameraState.TeleportSeq}.");
            }
        }

        private static void MeasureJitter(ReadOnlySpan<float> m, out float jx, out float jy)
        {
            float n3Sq = m[12] * m[12] + m[13] * m[13] + m[14] * m[14];

            if (n3Sq < 1e-12f)
            {
                jx = 0f;
                jy = 0f;

                return;
            }

            jx = (m[0] * m[12] + m[1] * m[13] + m[2] * m[14]) / n3Sq;
            jy = (m[4] * m[12] + m[5] * m[13] + m[6] * m[14]) / n3Sq;
        }

        private static void RemoveJitter(Span<float> m, float jx, float jy)
        {
            for (int i = 0; i < 4; i++)
            {
                m[i] -= jx * m[12 + i];
                m[4 + i] -= jy * m[12 + i];
            }
        }

        private static void LogReject(string reason, float dist, float budget, float fx, float dtSec)
        {
            if (!MvppCamTrace.Enabled || _rejectLogs >= 40)
            {
                return;
            }

            _rejectLogs++;

            Logger.Warning?.Print(LogClass.Gpu,
                $"MVPP camguard reject #{_rejectLogs} ({reason}): dist={dist:0.####} budget={budget:0.##} " +
                $"dt={dtSec:0.###}s fx={fx:0.#####} lastFx={_lastFx:0.#####} " +
                $"drift={(_lastFx > 1e-6f ? MathF.Abs(fx / _lastFx - 1f) : 0f):0.#####}.");
        }

        private static void Transpose(ReadOnlySpan<float> m, Span<float> dst)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    dst[r * 4 + c] = m[c * 4 + r];
                }
            }
        }
    }
}
