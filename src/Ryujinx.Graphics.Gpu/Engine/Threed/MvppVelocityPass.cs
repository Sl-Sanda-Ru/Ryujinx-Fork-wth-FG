
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppVelocityPass
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL") == "1";

        private static readonly string _graftDrawMode =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_DRAW");
        private static readonly bool _graftDraw = _graftDrawMode is "1" or "all";
        private static readonly bool _graftDrawAll = _graftDrawMode == "all";

        private static bool _alive = Enabled;

        private static ITexture _coverage;
        private static int _covWidth;
        private static int _covHeight;
        private static readonly Dictionary<(int, int), ITexture> _coverageBySize = new();
        private static readonly ITexture[] _covColors = new ITexture[1];
        private static int _grafts;
        private static int _graftSameProg;
        private static object _lastGraftVariant;
        private static int _redraws;
        private static bool _graftLogged;

        private static bool _redrawDoneThisFrame;

        private static readonly float _depthEps = float.TryParse(
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_DEPTH_EPS"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out float eps) ? eps : 0f;

        private static readonly uint[] _covMasks = [0xfu];

        private static readonly bool _ringEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_RING") == "1";

        private static readonly bool _twoPassEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_TWOPASS") == "1";

        private static readonly bool _variantEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_VARIANT") == "1";

        private static readonly bool _covForceR8 =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_COV_R8") == "1";

        private static readonly bool _targetedRestore =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_TARGETED_RESTORE") == "1";

        private static readonly bool _n1Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_N1") == "1";
        private static bool _n1BindLogged;
        private static bool _variantBindLogged;
        private static bool _variantFallbackLogged;
        private static int _variantBinds;
        private static int _variantFallbacks;
        private const int RingSlotBytes = 16 * 1024 * 1024;
        private const int RingMaxPalette = 16384;
        private const int RingMaxSlots = 4;
        private static readonly BufferHandle[] _ringBuf = new BufferHandle[RingMaxSlots];
        private static bool _ringCreated;
        private static int _ringSlot;
        private static int _ringOffset;
        private static int _ringDraws;
        private static long _ringLogMs;

        private static readonly bool _pairEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_PAIR") == "1";
        private const int PairMaxDraws = 4096;
        private static readonly int[] _pairOff = new int[RingMaxSlots * 2 * PairMaxDraws];
        private static readonly int[] _pairSize = new int[RingMaxSlots * 2 * PairMaxDraws];
        private static readonly int[] _pairCount = new int[RingMaxSlots * 2];
        private static readonly int[] _pairOrd = new int[2];
        private static int _pairHit;
        private static int _pairMiss;
        private static int _pairSzDiff;
        private static int _pairOvf;

        private static readonly int[] _pairHitB = new int[2];
        private static readonly int[] _pairMissB = new int[2];
        private static readonly int[] _pairSzDiffB = new int[2];

        private static readonly bool _t2Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_T2") == "1";
        private const int T2WindowBytes = 256;
        private const int T2EvictAgeFrames = 120;

        private struct T2Entry
        {
            public ulong Hash;
            public long LastFrame;
            public bool AnimThisFrame;
        }

        private static readonly Dictionary<ulong, T2Entry> _t2Table = new(16384);
        private static readonly List<ulong> _t2EvictList = new(1024);
        private static long _t2Frame;
        private static int _t2Seen;
        private static int _t2Static;
        private static int _t2Anim;

        private static readonly bool _t2gEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_T2G") == "1";
        private static readonly bool _t2gActive = _t2gEnabled && _t2Enabled;

        private static readonly int _t2gMin =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_T2G_MIN"), out int t2gMinEnv) ? t2gMinEnv : 4096;
        private static bool _t2gWarned;
        private static int _t2gB0Graft;
        private static int _t2gExtGraft;
        private static int _t2gB1Gated;
        private static readonly int[] _t2gSzHist = new int[5];
        private static bool _t2gB1Sampled;
        private static bool _t2gHoleSampled;
        private static int _t2gExtHit;
        private static int _t2gExtMiss;
        private static int _t2gExtSzDiff;

        private static readonly bool _n1BindEnvSet =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_N1BIND") == "1";
        private static readonly bool _n1BindActive = _n1BindEnvSet && _ringEnabled && _pairEnabled && _n1Enabled;
        private static readonly int _ringSlotCount = _n1BindActive ? RingMaxSlots : 2;
        private static bool _n1BindWarned;
        private static bool _n1RealLogged;
        private static int _n1RealBinds;
        private static int _n1TrivialBinds;

        private static readonly bool _debugMvSentinel =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_DEBUG_MV") == "1";

        private static readonly bool _depthAlways =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_DEPTH_ALWAYS") == "1";

        private static readonly bool _samplesProbe =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_SAMPLEQ") == "1";
        private static bool _sqArmed;
        private static int _sqLogged;

        private static readonly bool _readbackProbe =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_READBACK") == "1";
        private static bool _rbArmed;
        private static int _rbLogged;

        private static int _ringN1Slot;
        private static int _ringN1Offset = -1;
        private static int _ringN1Size;
        private static bool _ringN1IsReal;

        private static readonly float _plausThreshold = float.TryParse(
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_PLAUSIBILITY"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out float plausEnv) ? plausEnv : 3.0f;
        private static readonly float[] _pairRootT = new float[RingMaxSlots * 2 * PairMaxDraws * 3];
        private static int _plausOk;
        private static int _plausRej;

        private static volatile bool _ringFlipPending;

        private static int PIdx(int slot, int band, int ord) => (slot * 2 + band) * PairMaxDraws + ord;

        private static ulong T2Hash(ReadOnlySpan<float> data)
        {
            ReadOnlySpan<ulong> q = System.Runtime.InteropServices.MemoryMarshal.Cast<float, ulong>(data);
            const ulong Prime = 1099511628211UL;
            ulong h = 14695981039346656037UL;

            int w = T2WindowBytes / 8;
            int head = Math.Min(w, q.Length);
            for (int i = 0; i < head; i++)
            {
                h = (h ^ q[i]) * Prime;
            }

            for (int i = Math.Max(head, q.Length - w); i < q.Length; i++)
            {
                h = (h ^ q[i]) * Prime;
            }

            return h;
        }

        private static void T2EvictOld()
        {
            long frame = System.Threading.Interlocked.Read(ref _t2Frame);

            foreach (KeyValuePair<ulong, T2Entry> kv in _t2Table)
            {
                if (frame - kv.Value.LastFrame > T2EvictAgeFrames)
                {
                    _t2EvictList.Add(kv.Key);
                }
            }

            for (int i = 0; i < _t2EvictList.Count; i++)
            {
                _t2Table.Remove(_t2EvictList[i]);
            }

            _t2EvictList.Clear();
        }

        private static int _ringLastOffset = -1;
        private static int _ringLastSize;

        private static void SnapshotToRing(GpuContext context, GpuChannel channel)
        {
            if (_pairEnabled && _ringFlipPending)
            {
                _ringFlipPending = false;

                _pairCount[_ringSlot * 2] = _pairOrd[0];
                _pairCount[_ringSlot * 2 + 1] = _pairOrd[1];
                _pairOrd[0] = 0;
                _pairOrd[1] = 0;

                _ringSlot = (_ringSlot + 1) % _ringSlotCount;
                _ringOffset = 0;
                _ringDraws = 0;
            }

            if (!_ringCreated)
            {
                for (int i = 0; i < _ringSlotCount; i++)
                {
                    _ringBuf[i] = context.Renderer.CreateBuffer(RingSlotBytes, BufferAccess.HostMemory);
                }
                _ringCreated = true;
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP ring: {_ringSlotCount}x16MB GPU palette ring created, per-draw upload" +
                    $"{(_n1BindActive ? " (N1BIND: real N-1 reads armed)" : " (nobody reads it yet -- pipe transparency)")}.");
            }

            _ringLastOffset = -1;
            _ringN1Offset = -1;
            _ringN1IsReal = false;

            int cb5Size = channel.BufferManager.GetGraphicsUniformBufferSize(0, 5);
            int sz = Math.Min(cb5Size, RingMaxPalette);
            if (sz < 48 || _ringOffset + sz > RingSlotBytes)
            {
                return;
            }

            ulong addr = channel.BufferManager.GetGraphicsUniformBufferAddress(0, 5);
            if (addr == 0 || addr == ulong.MaxValue)
            {
                return;
            }

            ReadOnlySpan<byte> pal = channel.MemoryManager.Physical.GetSpan(addr, sz);
            context.Renderer.SetBufferData(_ringBuf[_ringSlot], _ringOffset, pal);

            _ringLastOffset = _ringOffset;
            _ringLastSize = sz;

            _ringN1Slot = _ringSlot;
            _ringN1Offset = _ringOffset;
            _ringN1Size = sz;

            if (_pairEnabled)
            {
                int band = (sz >= 4096 && sz < 5120) ? 0 : 1;
                int ord = _pairOrd[band];

                if (ord >= PairMaxDraws)
                {
                    _pairOvf++;
                }
                else
                {
                    int prevSlot = (_ringSlot + _ringSlotCount - 1) % _ringSlotCount;

                    bool extDraw = _t2gActive && band == 1 && sz >= _t2gMin && sz < 4096;

                    if (ord >= _pairCount[prevSlot * 2 + band])
                    {
                        _pairMiss++;
                        _pairMissB[band]++;
                        if (extDraw) { _t2gExtMiss++; }
                    }
                    else if (_pairSize[PIdx(prevSlot, band, ord)] != sz)
                    {
                        _pairSzDiff++;
                        _pairSzDiffB[band]++;
                        if (extDraw) { _t2gExtSzDiff++; }
                    }
                    else
                    {
                        _pairHit++;
                        _pairHitB[band]++;
                        if (extDraw) { _t2gExtHit++; }

                        if (_n1BindActive)
                        {
                            ReadOnlySpan<float> curF = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(pal);
                            int prevT = PIdx(prevSlot, band, ord) * 3;
                            float dx = MathF.Abs(curF[3] - _pairRootT[prevT]);
                            float dy = MathF.Abs(curF[7] - _pairRootT[prevT + 1]);
                            float dz = MathF.Abs(curF[11] - _pairRootT[prevT + 2]);

                            if (dx <= _plausThreshold && dy <= _plausThreshold && dz <= _plausThreshold &&
                                float.IsFinite(dx) && float.IsFinite(dy) && float.IsFinite(dz))
                            {
                                _plausOk++;
                                _ringN1Slot = prevSlot;
                                _ringN1Offset = _pairOff[PIdx(prevSlot, band, ord)];
                                _ringN1Size = sz;
                                _ringN1IsReal = true;
                            }
                            else
                            {
                                _plausRej++;
                            }
                        }
                    }

                    int cur = PIdx(_ringSlot, band, ord);
                    _pairOff[cur] = _ringLastOffset;
                    _pairSize[cur] = sz;

                    ReadOnlySpan<float> palF = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(pal);
                    int curT = cur * 3;
                    _pairRootT[curT] = palF[3];
                    _pairRootT[curT + 1] = palF[7];
                    _pairRootT[curT + 2] = palF[11];

                    _pairOrd[band] = ord + 1;
                }
            }

            _ringOffset += (sz + 255) & ~255;
            _ringDraws++;
        }

        public static void OnFrameEnqueued()
        {
            if (Enabled)
            {
                MvppVelState.FrameClearPending = true;
                _redrawDoneThisFrame = false;

                if (_t2Enabled)
                {
                    System.Threading.Interlocked.Increment(ref _t2Frame);
                }

                if (_deltaEnabled)
                {
                    _dSlot ^= 1;
                    _dOrd[0] = 0;
                    _dOrd[1] = 0;
                }

                if (_ringEnabled)
                {
                    long now = Environment.TickCount64;
                    if (now - _ringLogMs >= 2000 && _ringDraws > 0)
                    {
                        _ringLogMs = now;

                        string pair = "";
                        string pairSplit = null;
                        if (_pairEnabled)
                        {
                            int total = _pairHit + _pairMiss + _pairSzDiff;
                            pair = $" | pair: hit {_pairHit} miss {_pairMiss} szdiff {_pairSzDiff} ovf {_pairOvf}" +
                                   $" (rate {(total > 0 ? 100f * _pairHit / total : 0f):0.0}%)";

                            int t0 = _pairHitB[0] + _pairMissB[0] + _pairSzDiffB[0];
                            int t1 = _pairHitB[1] + _pairMissB[1] + _pairSzDiffB[1];
                            pairSplit =
                                $"MVPP pair split: B0 hit {_pairHitB[0]} miss {_pairMissB[0]} szdiff {_pairSzDiffB[0]}" +
                                $" (rate {(t0 > 0 ? 100f * _pairHitB[0] / t0 : 0f):0.0}%)" +
                                $" | B1 hit {_pairHitB[1]} miss {_pairMissB[1]} szdiff {_pairSzDiffB[1]}" +
                                $" (rate {(t1 > 0 ? 100f * _pairHitB[1] / t1 : 0f):0.0}%)";

                            _pairHit = 0;
                            _pairMiss = 0;
                            _pairSzDiff = 0;
                            _pairOvf = 0;
                            _pairHitB[0] = 0; _pairHitB[1] = 0;
                            _pairMissB[0] = 0; _pairMissB[1] = 0;
                            _pairSzDiffB[0] = 0; _pairSzDiffB[1] = 0;
                        }

                        if (_t2Enabled)
                        {
                            pair += $" | T2 seen={_t2Seen} static={_t2Static} anim={_t2Anim}";
                            _t2Seen = 0;
                            _t2Static = 0;
                            _t2Anim = 0;
                        }

                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP ring: {_ringDraws} palettes, {_ringOffset / 1024} KB uploaded to slot {_ringSlot} last frame (UNREAD).{pair}");

                        if (pairSplit != null)
                        {
                            Logger.Info?.Print(LogClass.Gpu, pairSplit);
                        }

                        if (_t2gActive)
                        {
                            Logger.Info?.Print(LogClass.Gpu,
                                $"MVPP T2G: graft_b0={_t2gB0Graft} graft_ext={_t2gExtGraft} gated={_t2gB1Gated}");
                            Logger.Info?.Print(LogClass.Gpu,
                                $"MVPP T2G szhist: a={_t2gSzHist[0]} b={_t2gSzHist[1]} c={_t2gSzHist[2]} d={_t2gSzHist[3]} e={_t2gSzHist[4]}");

                            int extTotal = _t2gExtHit + _t2gExtMiss + _t2gExtSzDiff;
                            Logger.Info?.Print(LogClass.Gpu,
                                $"MVPP T2G EXT pair: hit={_t2gExtHit} miss={_t2gExtMiss} szdiff={_t2gExtSzDiff}" +
                                $" (rate {(extTotal > 0 ? 100f * _t2gExtHit / extTotal : 0f):0.0}%)");

                            _t2gB0Graft = 0;
                            _t2gExtGraft = 0;
                            _t2gB1Gated = 0;
                            _t2gSzHist[0] = 0; _t2gSzHist[1] = 0; _t2gSzHist[2] = 0; _t2gSzHist[3] = 0; _t2gSzHist[4] = 0;
                            _t2gExtHit = 0;
                            _t2gExtMiss = 0;
                            _t2gExtSzDiff = 0;
                            _t2gB1Sampled = false;
                            _t2gHoleSampled = false;
                        }
                    }

                    if (_pairEnabled)
                    {
                        _ringFlipPending = true;
                    }
                    else
                    {
                        _ringSlot = (_ringSlot + 1) % _ringSlotCount;
                        _ringOffset = 0;
                        _ringDraws = 0;
                    }
                }
            }
        }

        private const int LogIntervalMs = 2000;
        private const int PaletteMinBytes = 4096;

        private static long _windowStartMs;
        private static int _draws;
        private static int _writeOffSkips;

        private static int _scale1Skips;
        private static int _candNoSnap;
        private static int _candProjFail;

        private static long _firstCallMs;
        private static bool _sawScaledDraw;
        private static bool _sawCandidate;
        private static bool _sawSnapshot;
        private static bool _sawClassified;

        private const int ProjSamplesPerWindow = 5;
        private static int _projSamples;

        private static readonly float _tMin = float.TryParse(
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_TMIN"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out float tmin) ? tmin : 0.5f;

        private static readonly bool _v4Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_V4") == "1";

        private static int _accContig;
        private static int _accSpread;

        private static readonly bool _cohEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_COH") == "1";
        private const int CohMinBones = 6;
        private const float CohMaxSpan = 15f;
        private static int _cohRejCount;
        private static int _cohRejSpan;
        private static bool _cohDiagLogged;

        private static readonly bool _coh2Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_COH2") == "1";
        private const int CohMinBones2 = 3;
        private const float CohInlierFrac = 0.6f;
        private static int _coh2Rejects;

        private static readonly float _coh2Floor = float.TryParse(
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_COH2_FLOOR"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out float c2f) ? c2f : 4f;

        private static readonly bool _coh2NoProj =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_COH2_NOPROJ") == "1";

        private static bool _cohDiagAccLogged;

        private const uint IntStrictMask = (1u << 3) | (1u << 4);
        private const uint IntBroadMask = (1u << 3) | (1u << 4) | (1u << 5) | (1u << 6);
        private static int _clsActor;
        private static int _clsActorIntS;
        private static int _clsActorIntB;
        private static int _clsProp;
        private static int _clsPropIntS;
        private static int _clsPropIntB;

        private static readonly bool _deltaEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_DELTA") == "1";
        private const int DeltaMaxDraws = 512;
        private const int DeltaMaxTris = 64;
        private static readonly float[] _dTris = new float[2 * 2 * DeltaMaxDraws * DeltaMaxTris * 3];
        private static readonly int[] _dCb5 = new int[2 * 2 * DeltaMaxDraws];
        private static readonly int[] _dCount = new int[2 * 2 * DeltaMaxDraws];
        private static int _dSlot;
        private static readonly int[] _dOrd = new int[2];
        private static readonly int[] _dMatched = new int[2];
        private static readonly int[] _dStatic = new int[2];
        private static readonly int[][] _dHist = { new int[5], new int[5] };
        private static readonly float[] _dSumMax = new float[2];
        private static readonly float[] _dSumMed = new float[2];
        private static readonly float[] _dGMax = new float[2];

        private static int DIdx(int slot, int band, int ord) => (slot * 2 + band) * DeltaMaxDraws + ord;

        private static void ProbeDelta(GpuChannel channel, int cb5Size, int band)
        {
            int ord = _dOrd[band];
            if (ord >= DeltaMaxDraws)
            {
                return;
            }
            _dOrd[band] = ord + 1;

            ulong addr = channel.BufferManager.GetGraphicsUniformBufferAddress(0, 5);
            if (addr == 0 || addr == ulong.MaxValue)
            {
                return;
            }

            int readBytes = Math.Min(cb5Size, DeltaMaxTris * 12 * 4);
            ReadOnlySpan<float> data = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                channel.MemoryManager.Physical.GetSpan(addr, readBytes));

            int cur = DIdx(_dSlot, band, ord);
            int triBase = cur * DeltaMaxTris * 3;
            int n = 0;
            for (int j = 0; j * 12 + 11 < data.Length && n < DeltaMaxTris; j++, n++)
            {
                _dTris[triBase + n * 3] = data[j * 12 + 3];
                _dTris[triBase + n * 3 + 1] = data[j * 12 + 7];
                _dTris[triBase + n * 3 + 2] = data[j * 12 + 11];
            }
            _dCb5[cur] = cb5Size;
            _dCount[cur] = n;

            int prev = DIdx(_dSlot ^ 1, band, ord);
            if (_dCb5[prev] != cb5Size || _dCount[prev] == 0 || n == 0)
            {
                return;
            }

            int m = Math.Min(n, _dCount[prev]);
            int prevTriBase = prev * DeltaMaxTris * 3;
            Span<float> deltas = stackalloc float[DeltaMaxTris];
            int dc = 0;
            float maxD = 0f;

            for (int j = 0; j < m; j++)
            {
                float dx = _dTris[triBase + j * 3] - _dTris[prevTriBase + j * 3];
                float dy = _dTris[triBase + j * 3 + 1] - _dTris[prevTriBase + j * 3 + 1];
                float dz = _dTris[triBase + j * 3 + 2] - _dTris[prevTriBase + j * 3 + 2];

                if (!float.IsFinite(dx) || !float.IsFinite(dy) || !float.IsFinite(dz))
                {
                    continue;
                }

                float d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                deltas[dc++] = d;
                if (d > maxD)
                {
                    maxD = d;
                }
            }

            if (dc == 0)
            {
                return;
            }

            deltas.Slice(0, dc).Sort();
            float med = deltas[dc / 2];

            _dMatched[band]++;
            if (maxD < 0.01f)
            {
                _dStatic[band]++;
            }
            int bkt = maxD < 0.01f ? 0 : maxD < 0.5f ? 1 : maxD < 2f ? 2 : maxD < 10f ? 3 : 4;
            _dHist[band][bkt]++;
            _dSumMax[band] += maxD;
            _dSumMed[band] += med;
            if (maxD > _dGMax[band])
            {
                _dGMax[band] = maxD;
            }
        }

        private static readonly int _palReadCap = int.TryParse(
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_PALCAP"),
            out int palcap) && palcap > 0 ? palcap : 0xC00;
        private static int _candDsScene;
        private static int _candDsOther;
        private static int _candDsNone;
        private static int _candDsOtherW;
        private static int _candDsOtherH;

        private static void FirstEvent(ref bool seen, string what)
        {
            if (!seen)
            {
                seen = true;
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP velocity first: {what} at +{(Environment.TickCount64 - _firstCallMs) / 1000.0:0.0}s.");
            }
        }
        private static int _bigCb5;
        private static int _intAttr;
        private static int _skinned;
        private static long _skinnedCb5Sum;
        private static int _skinnedCb5Max;
        private static bool _sampleLogged;

        private static readonly int[] _typeCensus = new int[8];
        private static int _censusDraws;
        private static int _instancedAttr;
        private static int _nonInstanced;
        private static int _niCb5None;
        private static int _niCb5Small;
        private static int _niCb5Palette;
        private static int _niCb5Big;

        public static void AfterDraw(
            DrawManager drawManager,
            ThreedClass engine,
            GpuContext context,
            GpuChannel channel,
            ref ThreedClassState state,
            int count,
            int instanceCount,
            int firstIndex,
            int firstVertex,
            int firstInstance,
            bool indexed)
        {
            if (!_alive)
            {
                return;
            }

            try
            {
                if (ClassifyAndCount(channel, ref state, count, instanceCount))
                {
                    Graft(drawManager, engine, context, channel, state.DepthTestFunc,
                        count, instanceCount, firstIndex, firstVertex, firstInstance, indexed);
                }
            }
            catch (Exception e)
            {
                _alive = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP velocity: disabled after unexpected error: {e}");
            }
        }

        private static bool ClassifyAndCount(GpuChannel channel, ref ThreedClassState state,
            int drawCount, int drawInstances)
        {
            long now = Environment.TickCount64;
            if (_windowStartMs == 0)
            {
                _windowStartMs = now;
                _firstCallMs = now;
                Logger.Info?.Print(LogClass.Gpu, "MVPP velocity first: draw reached the classifier (t0).");

                if (_t2gEnabled && !_t2Enabled && !_t2gWarned)
                {
                    _t2gWarned = true;
                    Logger.Warning?.Print(LogClass.Gpu,
                        "MVPP T2G: RYUJINX_MVPP_VEL_T2G=1 requires RYUJINX_MVPP_VEL_T2=1 -- T2G ignored.");
                }

                if (_n1BindEnvSet && !_n1BindActive && !_n1BindWarned)
                {
                    _n1BindWarned = true;
                    Logger.Warning?.Print(LogClass.Gpu,
                        "MVPP N1BIND: RYUJINX_MVPP_VEL_N1BIND=1 requires RING+PAIR+N1 -- N1BIND ignored.");
                }
            }
            else if (now - _windowStartMs >= LogIntervalMs)
            {
                LogWindow();

                if (_t2Enabled)
                {
                    T2EvictOld();
                }

                if (_samplesProbe)
                {
                    _sqArmed = true;
                }

                if (_readbackProbe)
                {
                    _rbArmed = true;
                }

                _windowStartMs = now;
            }

            if (channel.TextureManager.RenderTargetScale == 1f)
            {
                _scale1Skips++;

                return false;
            }

            _draws++;
            FirstEvent(ref _sawScaledDraw, "scaled draw");

            if (!state.DepthWriteEnable)
            {
                _writeOffSkips++;

                return false;
            }

            int cb5Size = 0;
            bool bigCb5 = false;
            if ((channel.BufferManager.GetGraphicsUniformBufferUseMask(0) & (1u << 5)) != 0)
            {
                ulong addr = channel.BufferManager.GetGraphicsUniformBufferAddress(0, 5);
                if (addr != 0 && addr != ulong.MaxValue)
                {
                    cb5Size = channel.BufferManager.GetGraphicsUniformBufferSize(0, 5);
                    bigCb5 = cb5Size >= PaletteMinBytes;
                }
            }

            if (bigCb5)
            {
                _bigCb5++;
            }

            bool intAttr = false;
            int intAttrIndex = -1;
            uint seenTypes = 0;
            bool anyInstanced = false;

            for (int i = 0; i < Constants.TotalVertexAttribs; i++)
            {
                VertexAttribState attr = state.VertexAttribState[i];

                if (attr.UnpackIsConstant())
                {
                    continue;
                }

                int bufferIndex = attr.UnpackBufferIndex();
                ref VertexBufferState vb = ref state.VertexBufferState[bufferIndex];

                if (!vb.UnpackEnable())
                {
                    continue;
                }

                VertexAttribType type = attr.UnpackType();
                seenTypes |= 1u << (int)type;

                if (bufferIndex < 16 &&
                    state.VertexBufferInstanced[bufferIndex] &&
                    vb.Divisor != 0)
                {
                    anyInstanced = true;

                    continue;
                }

                if (type == VertexAttribType.Sint || type == VertexAttribType.Uint)
                {
                    intAttr = true;
                    intAttrIndex = i;
                }
            }

            if (bigCb5)
            {
                _censusDraws++;
                for (int t = 1; t < 8; t++)
                {
                    if ((seenTypes & (1u << t)) != 0)
                    {
                        _typeCensus[t]++;
                    }
                }

                if (anyInstanced)
                {
                    _instancedAttr++;
                }
            }

            if (intAttr)
            {
                _intAttr++;
            }

            if (!anyInstanced)
            {
                _nonInstanced++;

                if (cb5Size == 0)
                {
                    _niCb5None++;
                }
                else if (cb5Size < 512)
                {
                    _niCb5Small++;
                }
                else if (cb5Size < PaletteMinBytes)
                {
                    _niCb5Palette++;
                }
                else
                {
                    _niCb5Big++;
                }
            }

            bool skinnedV3 = false;
            if (cb5Size >= 512)
            {
                FirstEvent(ref _sawCandidate, "candidate draw (write-on, cb5 >= 512 B)");

                Image.Texture candDs = channel.TextureManager.RenderTargetDepthStencil;
                ITexture candDsHost = candDs?.HostTexture;
                if (candDsHost == null)
                {
                    _candDsNone++;
                }
                else if (candDsHost.Width == MvppCameraCapture.SceneDepthHostWidth &&
                         candDsHost.Height == MvppCameraCapture.SceneDepthHostHeight)
                {
                    _candDsScene++;
                }
                else
                {
                    _candDsOther++;
                    _candDsOtherW = candDsHost.Width;
                    _candDsOtherH = candDsHost.Height;
                }

                if (!Ryujinx.Graphics.GAL.DlssCameraState.SnapshotCurrent(out System.Numerics.Matrix4x4 vp))
                {
                    _candNoSnap++;
                }
                else
                {
                    FirstEvent(ref _sawSnapshot, "camera snapshot present at a candidate draw");

                    ulong cb5Addr = channel.BufferManager.GetGraphicsUniformBufferAddress(0, 5);
                    if (cb5Addr != 0 && cb5Addr != ulong.MaxValue)
                    {
                        ReadOnlySpan<float> data = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                            channel.MemoryManager.Physical.GetSpan(cb5Addr, Math.Min(cb5Size, _v4Enabled ? _palReadCap : 0x180)));

                        if (_t2Enabled)
                        {
                            _t2Seen++;

                            ulong t2h = T2Hash(data);
                            long t2f = System.Threading.Interlocked.Read(ref _t2Frame);
                            bool anim;
                            ulong t2Prev = 0;

                            if (_t2Table.TryGetValue(cb5Addr, out T2Entry t2))
                            {
                                t2Prev = t2.Hash;

                                if (t2.LastFrame == t2f)
                                {
                                    anim = t2.AnimThisFrame;
                                }
                                else
                                {
                                    anim = t2.Hash != t2h;
                                }

                                t2.Hash = t2h;
                                t2.LastFrame = t2f;
                                t2.AnimThisFrame = anim;
                                _t2Table[cb5Addr] = t2;
                            }
                            else
                            {
                                anim = false;
                                _t2Table[cb5Addr] = new T2Entry { Hash = t2h, LastFrame = t2f, AnimThisFrame = false };
                            }

                            if (!anim)
                            {
                                _t2Static++;

                                return false;
                            }

                            _t2Anim++;

                            if (_t2gActive)
                            {
                                int szg = Math.Min(cb5Size, RingMaxPalette);
                                int bucket = szg < 3072 ? 0 : szg < 3840 ? 1 : szg < 4096 ? 2 : szg < 5120 ? 3 : 4;
                                _t2gSzHist[bucket]++;

                                if ((bucket == 1 || bucket == 2) && !_t2gHoleSampled)
                                {
                                    _t2gHoleSampled = true;
                                    Logger.Info?.Print(LogClass.Gpu,
                                        $"MVPP T2G hole sample: cb5={cb5Size} B, addr=0x{cb5Addr:X}, " +
                                        $"hashDelta=0x{t2Prev ^ t2h:X16}, verts={drawCount} inst={drawInstances}.");
                                }

                                if (szg < _t2gMin || szg >= 5120)
                                {
                                    _t2gB1Gated++;

                                    if (!_t2gB1Sampled)
                                    {
                                        _t2gB1Sampled = true;
                                        Logger.Info?.Print(LogClass.Gpu,
                                            $"MVPP T2G gated sample: cb5={cb5Size} B, addr=0x{cb5Addr:X}, " +
                                            $"hashDelta=0x{t2Prev ^ t2h:X16}, verts={drawCount} inst={drawInstances}.");
                                    }

                                    return false;
                                }

                                if (szg >= 4096)
                                {
                                    _t2gB0Graft++;
                                }
                                else
                                {
                                    _t2gExtGraft++;
                                }
                            }
                        }

                        bool haveSample = false;
                        float sTx = 0f, sTy = 0f, sTz = 0f, sCw = 0f, sCx = 0f, sCy = 0f;

                        bool contigHit = false;
                        for (int f = 0; f + 3 <= data.Length; f++)
                        {
                            float tx = data[f], ty = data[f + 1], tz = data[f + 2];

                            if (!float.IsFinite(tx) || !float.IsFinite(ty) || !float.IsFinite(tz))
                            {
                                continue;
                            }

                            float ax = MathF.Abs(tx), ay = MathF.Abs(ty), az = MathF.Abs(tz);
                            if (ax >= 1e6f || ay >= 1e6f || az >= 1e6f ||
                                (ax < _tMin && ay < _tMin && az < _tMin))
                            {
                                continue;
                            }

                            float cw = vp.M41 * tx + vp.M42 * ty + vp.M43 * tz + vp.M44;

                            if (!haveSample)
                            {
                                haveSample = true;
                                sTx = tx; sTy = ty; sTz = tz;
                                sCw = cw;
                                sCx = vp.M11 * tx + vp.M12 * ty + vp.M13 * tz + vp.M14;
                                sCy = vp.M21 * tx + vp.M22 * ty + vp.M23 * tz + vp.M24;
                            }

                            if (cw < 1e-3f)
                            {
                                continue;
                            }

                            float cx = vp.M11 * tx + vp.M12 * ty + vp.M13 * tz + vp.M14;
                            float cy = vp.M21 * tx + vp.M22 * ty + vp.M23 * tz + vp.M24;

                            if (MathF.Abs(cx / cw) <= 1.3f && MathF.Abs(cy / cw) <= 1.3f)
                            {
                                contigHit = true;
                                break;
                            }
                        }

                        bool spreadHit = false;
                        if (_v4Enabled)
                        {
                            bool spreadProj = false;
                            int spreadCount = 0;
                            float minX = 0f, maxX = 0f, minY = 0f, maxY = 0f, minZ = 0f, maxZ = 0f;
                            float seedX = 0f, seedY = 0f, seedZ = 0f;
                            float sumTX = 0f, sumTY = 0f, sumTZ = 0f;

                            bool collectAll = _cohEnabled || _coh2Enabled;
                            float spreadFloor = _coh2Enabled ? _coh2Floor : _tMin;

                            for (int b = 3; b + 8 < data.Length; b += 12)
                            {
                                float tx = data[b], ty = data[b + 4], tz = data[b + 8];

                                if (!float.IsFinite(tx) || !float.IsFinite(ty) || !float.IsFinite(tz))
                                {
                                    continue;
                                }

                                float ax = MathF.Abs(tx), ay = MathF.Abs(ty), az = MathF.Abs(tz);
                                if (ax >= 1e6f || ay >= 1e6f || az >= 1e6f ||
                                    (ax < spreadFloor && ay < spreadFloor && az < spreadFloor))
                                {
                                    continue;
                                }

                                if (spreadCount == 0)
                                {
                                    minX = maxX = tx; minY = maxY = ty; minZ = maxZ = tz;
                                }
                                else
                                {
                                    if (tx < minX) { minX = tx; } else if (tx > maxX) { maxX = tx; }
                                    if (ty < minY) { minY = ty; } else if (ty > maxY) { maxY = ty; }
                                    if (tz < minZ) { minZ = tz; } else if (tz > maxZ) { maxZ = tz; }
                                }

                                sumTX += tx; sumTY += ty; sumTZ += tz;
                                spreadCount++;

                                if (!spreadProj)
                                {
                                    float cw = vp.M41 * tx + vp.M42 * ty + vp.M43 * tz + vp.M44;
                                    if (cw >= 1e-3f)
                                    {
                                        float cx = vp.M11 * tx + vp.M12 * ty + vp.M13 * tz + vp.M14;
                                        float cy = vp.M21 * tx + vp.M22 * ty + vp.M23 * tz + vp.M24;

                                        if (MathF.Abs(cx / cw) <= 1.3f && MathF.Abs(cy / cw) <= 1.3f)
                                        {
                                            spreadProj = true;
                                            seedX = tx; seedY = ty; seedZ = tz;
                                        }
                                    }
                                }

                                if (spreadProj && !collectAll)
                                {
                                    break;
                                }
                            }

                            if (_coh2Enabled)
                            {
                                bool coherent = false;
                                int inliers = 0;

                                if (spreadCount >= CohMinBones2 && (spreadProj || _coh2NoProj))
                                {
                                    float ccx = sumTX / spreadCount, ccy = sumTY / spreadCount, ccz = sumTZ / spreadCount;

                                    for (int b = 3; b + 8 < data.Length; b += 12)
                                    {
                                        float tx = data[b], ty = data[b + 4], tz = data[b + 8];
                                        if (!float.IsFinite(tx) || !float.IsFinite(ty) || !float.IsFinite(tz))
                                        {
                                            continue;
                                        }

                                        float ax = MathF.Abs(tx), ay = MathF.Abs(ty), az = MathF.Abs(tz);
                                        if (ax >= 1e6f || ay >= 1e6f || az >= 1e6f ||
                                            (ax < spreadFloor && ay < spreadFloor && az < spreadFloor))
                                        {
                                            continue;
                                        }

                                        if (MathF.Abs(tx - ccx) <= CohMaxSpan &&
                                            MathF.Abs(ty - ccy) <= CohMaxSpan &&
                                            MathF.Abs(tz - ccz) <= CohMaxSpan)
                                        {
                                            inliers++;
                                        }
                                    }

                                    coherent = inliers >= CohInlierFrac * spreadCount;
                                }

                                bool projOk = spreadProj || _coh2NoProj;
                                spreadHit = projOk && coherent;

                                if (projOk && !coherent)
                                {
                                    _coh2Rejects++;
                                }

                                if (cb5Size >= PaletteMinBytes)
                                {
                                    bool acc = spreadHit;
                                    if ((acc && !_cohDiagAccLogged) || (!acc && !_cohDiagLogged))
                                    {
                                        if (acc) { _cohDiagAccLogged = true; } else { _cohDiagLogged = true; }

                                        Span<int> hist = stackalloc int[6];
                                        for (int b = 3; b + 8 < data.Length; b += 12)
                                        {
                                            float tx = data[b], ty = data[b + 4], tz = data[b + 8];
                                            if (!float.IsFinite(tx) || !float.IsFinite(ty) || !float.IsFinite(tz))
                                            {
                                                continue;
                                            }

                                            float m = MathF.Max(MathF.Abs(tx), MathF.Max(MathF.Abs(ty), MathF.Abs(tz)));
                                            if (m >= 1e6f) { continue; }

                                            int bucket = m < 2f ? 0 : m < 5f ? 1 : m < 15f ? 2 : m < 50f ? 3 : m < 150f ? 4 : 5;
                                            hist[bucket]++;
                                        }

                                        float span = MathF.Max(maxX - minX, MathF.Max(maxY - minY, maxZ - minZ));
                                        Logger.Info?.Print(LogClass.Gpu,
                                            $"MVPP COH2 diag [{(acc ? "ACCEPT" : "REJECT")}]: cb5={cb5Size} B, " +
                                            $"verts={drawCount} inst={drawInstances}, supraFloor={spreadCount} (floor {spreadFloor:0.#}), " +
                                            $"inliers={inliers} span={span:0.#}, " +
                                            $"magHist[<2 2-5 5-15 15-50 50-150 150+]=[{hist[0]} {hist[1]} {hist[2]} {hist[3]} {hist[4]} {hist[5]}].");
                                    }
                                }
                            }
                            else if (_cohEnabled)
                            {
                                float span = MathF.Max(maxX - minX, MathF.Max(maxY - minY, maxZ - minZ));
                                bool coherent = spreadCount >= CohMinBones && span <= CohMaxSpan;
                                spreadHit = spreadProj && coherent;

                                if (spreadProj && !coherent)
                                {
                                    if (spreadCount < CohMinBones)
                                    {
                                        _cohRejCount++;
                                    }
                                    else
                                    {
                                        _cohRejSpan++;
                                    }

                                    if (cb5Size >= PaletteMinBytes && !_cohDiagLogged)
                                    {
                                        _cohDiagLogged = true;
                                        int cluster = 0;
                                        float sumX = 0f, sumY = 0f, sumZ = 0f;
                                        int n = 0;

                                        for (int b = 3; b + 8 < data.Length; b += 12)
                                        {
                                            float tx = data[b], ty = data[b + 4], tz = data[b + 8];
                                            if (!float.IsFinite(tx) || !float.IsFinite(ty) || !float.IsFinite(tz))
                                            {
                                                continue;
                                            }

                                            float ax = MathF.Abs(tx), ay = MathF.Abs(ty), az = MathF.Abs(tz);
                                            if (ax >= 1e6f || ay >= 1e6f || az >= 1e6f ||
                                                (ax < _tMin && ay < _tMin && az < _tMin))
                                            {
                                                continue;
                                            }

                                            if (MathF.Abs(tx - seedX) <= CohMaxSpan &&
                                                MathF.Abs(ty - seedY) <= CohMaxSpan &&
                                                MathF.Abs(tz - seedZ) <= CohMaxSpan)
                                            {
                                                cluster++;
                                            }

                                            sumX += tx; sumY += ty; sumZ += tz;
                                            n++;
                                        }

                                        float median = 0f;
                                        if (n > 0)
                                        {
                                            float ccx = sumX / n, ccy = sumY / n, ccz = sumZ / n;
                                            Span<float> dists = stackalloc float[128];
                                            int k = 0;

                                            for (int b = 3; b + 8 < data.Length && k < dists.Length; b += 12)
                                            {
                                                float tx = data[b], ty = data[b + 4], tz = data[b + 8];
                                                if (!float.IsFinite(tx) || !float.IsFinite(ty) || !float.IsFinite(tz))
                                                {
                                                    continue;
                                                }

                                                float ax = MathF.Abs(tx), ay = MathF.Abs(ty), az = MathF.Abs(tz);
                                                if (ax >= 1e6f || ay >= 1e6f || az >= 1e6f ||
                                                    (ax < _tMin && ay < _tMin && az < _tMin))
                                                {
                                                    continue;
                                                }

                                                float dx = tx - ccx, dy = ty - ccy, dz = tz - ccz;
                                                dists[k++] = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                                            }

                                            if (k > 0)
                                            {
                                                dists.Slice(0, k).Sort();
                                                median = dists[k / 2];
                                            }
                                        }

                                        Logger.Info?.Print(LogClass.Gpu,
                                            $"MVPP COH diag: cb5={cb5Size} B (read {Math.Min(cb5Size, _palReadCap)}, " +
                                            $"{Math.Min(cb5Size, _palReadCap) / 48} mtx), draw verts={drawCount} inst={drawInstances}, " +
                                            $"spreadCount={spreadCount}, cause={(spreadCount < CohMinBones ? "COUNT" : "SPAN")}, " +
                                            $"globalSpan={span:0.#}, clusterWithin{CohMaxSpan:0}={cluster}, " +
                                            $"medianDistToCentroid={median:0.#}, seed=[{seedX:0.#} {seedY:0.#} {seedZ:0.#}].");
                                    }
                                }
                            }
                            else
                            {
                                spreadHit = spreadProj;
                            }

                            if (contigHit)
                            {
                                _accContig++;
                            }

                            if (spreadHit)
                            {
                                _accSpread++;
                            }
                        }

                        skinnedV3 = _v4Enabled ? spreadHit : contigHit;

                        if (!skinnedV3 && _projSamples < ProjSamplesPerWindow)
                        {
                            _projSamples++;

                            string t = haveSample
                                ? $"t=[{sTx:0.#} {sTy:0.#} {sTz:0.#}] " +
                                  $"ndc=[{(sCw != 0f ? sCx / sCw : float.NaN):0.##} {(sCw != 0f ? sCy / sCw : float.NaN):0.##}] cw={sCw:0.#}"
                                : $"t=NONE (first raw: [{(data.Length > 0 ? data[0] : 0f):G3} {(data.Length > 1 ? data[1] : 0f):G3} {(data.Length > 2 ? data[2] : 0f):G3}])";

                            Logger.Info?.Print(LogClass.Gpu,
                                $"MVPP projfail sample: {t}, " +
                                $"dsDraw={(candDsHost == null ? "none" : $"{candDsHost.Width}x{candDsHost.Height}")}, " +
                                $"dsScene={MvppCameraCapture.SceneDepthHostWidth}x{MvppCameraCapture.SceneDepthHostHeight}, " +
                                $"cb5={cb5Size} B.");
                        }
                    }

                    if (!skinnedV3)
                    {
                        _candProjFail++;
                    }
                }
            }

            if (!skinnedV3)
            {
                return false;
            }

            _skinned++;
            FirstEvent(ref _sawClassified, "classified skinned-v3 draw");
            _skinnedCb5Sum += cb5Size;
            if (cb5Size > _skinnedCb5Max)
            {
                _skinnedCb5Max = cb5Size;
            }

            bool intStrict = (seenTypes & IntStrictMask) != 0;
            bool intBroad = (seenTypes & IntBroadMask) != 0;
            bool actorBand = cb5Size >= 4096 && cb5Size < 5120;

            if (actorBand)
            {
                _clsActor++;
                if (intStrict) { _clsActorIntS++; }
                if (intBroad) { _clsActorIntB++; }
            }
            else
            {
                _clsProp++;
                if (intStrict) { _clsPropIntS++; }
                if (intBroad) { _clsPropIntB++; }
            }

            if (_deltaEnabled)
            {
                ProbeDelta(channel, cb5Size, actorBand ? 0 : 1);
            }

            if (!_sampleLogged)
            {
                _sampleLogged = true;
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP velocity sample: skinned-v3 draw, cb5 {cb5Size} B (int-attr found: {intAttr}, idx {intAttrIndex}).");
            }

            return true;
        }

        private static void Graft(
            DrawManager drawManager,
            ThreedClass engine,
            GpuContext context,
            GpuChannel channel,
            CompareOp depthFunc,
            int count,
            int instanceCount,
            int firstIndex,
            int firstVertex,
            int firstInstance,
            bool indexed)
        {
            Image.Texture ds = channel.TextureManager.RenderTargetDepthStencil;
            ITexture dsHost = ds?.HostTexture;

            if (dsHost == null)
            {
                return;
            }

            int w = dsHost.Width;
            int h = dsHost.Height;

            if (w != MvppCameraCapture.SceneDepthHostWidth ||
                h != MvppCameraCapture.SceneDepthHostHeight)
            {
                return;
            }

            if (_ringEnabled)
            {
                SnapshotToRing(context, channel);
            }

            if (_coverage == null || _covWidth != w || _covHeight != h)
            {
                if (!_coverageBySize.TryGetValue((w, h), out ITexture coverage))
                {
                    coverage = context.Renderer.CreateTexture(new TextureCreateInfo(
                        w, h, 1, 1, 1, 1, 1, 1,
                        (_variantEnabled && !_covForceR8) ? Format.R16G16Float : Format.R8Unorm,
                        DepthStencilMode.Depth,
                        Target.Texture2D,
                        SwizzleComponent.Red,
                        SwizzleComponent.Green,
                        SwizzleComponent.Blue,
                        SwizzleComponent.Alpha));
                    _coverageBySize[(w, h)] = coverage;

                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP velocity: coverage created {w}x{h} (cached sizes: {_coverageBySize.Count}).");
                }

                _coverage = coverage;
                _covWidth = w;
                _covHeight = h;

                MvppVelState.Coverage = coverage;
                MvppVelState.Width = w;
                MvppVelState.Height = h;
            }

            _covColors[0] = _coverage;
            context.Renderer.Pipeline.SetRenderTargets(_covColors, dsHost);

            if (MvppVelState.FrameClearPending)
            {
                MvppVelState.FrameClearPending = false;
                context.Renderer.Pipeline.ClearRenderTargetColor(0, 0, 1, 0xF,
                    _debugMvSentinel ? new ColorF(-1000f, -1000f, 0f, 0f) : new ColorF(0f, 0f, 0f, 0f));
            }

            CompareOp drawOp = CompareOp.Equal;
            float biasUnits = 0f;

            if (_graftDraw && _depthEps != 0f)
            {
                CompareOp gameFunc = depthFunc >= CompareOp.NeverGl
                    ? (CompareOp)((int)depthFunc - (int)CompareOp.NeverGl + (int)CompareOp.Never)
                    : depthFunc;

                if (gameFunc == CompareOp.Greater || gameFunc == CompareOp.GreaterOrEqual)
                {
                    drawOp = CompareOp.GreaterOrEqual;
                    biasUnits = MathF.Abs(_depthEps);
                }
                else if (gameFunc == CompareOp.Less || gameFunc == CompareOp.LessOrEqual || gameFunc == CompareOp.Equal)
                {
                    drawOp = CompareOp.LessOrEqual;
                    biasUnits = -MathF.Abs(_depthEps);
                }
            }

            if (_graftDraw && _depthAlways)
            {
                drawOp = CompareOp.Always;
                biasUnits = 0f;
            }

            context.Renderer.Pipeline.SetDepthTest(new DepthTestDescriptor(true, false,
                _graftDraw ? drawOp : CompareOp.Never));

            if (_graftDraw && (_graftDrawAll || !_redrawDoneThisFrame))
            {
                _redrawDoneThisFrame = true;

                context.Renderer.Pipeline.SetStencilTest(new StencilTestDescriptor(
                    false,
                    CompareOp.Always, StencilOp.Keep, StencilOp.Keep, StencilOp.Keep, 0, 0xff, 0xff,
                    CompareOp.Always, StencilOp.Keep, StencilOp.Keep, StencilOp.Keep, 0, 0xff, 0xff));
                context.Renderer.Pipeline.SetBlendState(0, new BlendDescriptor(
                    false, new ColorF(0f, 0f, 0f, 0f),
                    BlendOp.Add, BlendFactor.One, BlendFactor.Zero,
                    BlendOp.Add, BlendFactor.One, BlendFactor.Zero));
                context.Renderer.Pipeline.SetRenderTargetColorMasks(_covMasks);

                if (biasUnits != 0f)
                {
                    context.Renderer.Pipeline.SetDepthBias(
                        PolygonModeMask.Point | PolygonModeMask.Line | PolygonModeMask.Fill,
                        0f, biasUnits, 0f);
                }

                if (_variantEnabled)
                {
                    var variant = MvppVelState.VariantProgram;
                    if (variant != null)
                    {
                        if (ReferenceEquals(variant, _lastGraftVariant))
                        {
                            _graftSameProg++;
                        }
                        _lastGraftVariant = variant;

                        context.Renderer.Pipeline.SetProgram(variant);
                        MvppVelState.VariantBound = true;
                        _variantBinds++;
                        if (!_variantBindLogged)
                        {
                            _variantBindLogged = true;
                            Logger.Info?.Print(LogClass.Gpu, "MVPP variant (b): VARIANT bound via SetProgram for the re-draw.");
                        }
                    }
                    else
                    {
                        _variantFallbacks++;
                        if (!_variantFallbackLogged)
                        {
                            _variantFallbackLogged = true;
                            Logger.Warning?.Print(LogClass.Gpu, "MVPP variant (d): SILENT FALLBACK -- VariantProgram null, re-draw uses BASE.");
                        }
                    }
                }

                if (_n1Enabled && MvppVelState.VariantN1Binding >= 0 && _ringN1Offset >= 0)
                {
                    Span<BufferAssignment> n1 = stackalloc BufferAssignment[1];
                    n1[0] = new BufferAssignment(
                        MvppVelState.VariantN1Binding,
                        new BufferRange(_ringBuf[_ringN1Slot], _ringN1Offset, _ringN1Size));
                    context.Renderer.Pipeline.SetUniformBuffers(n1);

                    if (_ringN1IsReal)
                    {
                        _n1RealBinds++;

                        if (!_n1RealLogged)
                        {
                            _n1RealLogged = true;
                            Logger.Info?.Print(LogClass.Gpu,
                                $"MVPP N-1: REAL N-1 bind ACTIVE -- slot {_ringN1Slot} offset {_ringN1Offset} size {_ringN1Size} " +
                                $"({_ringSlotCount}-slot ring; expect MV != 0 on moving actors, image UNCHANGED pre-J3).");
                        }
                    }
                    else
                    {
                        _n1TrivialBinds++;
                    }

                    if (!_n1BindLogged)
                    {
                        _n1BindLogged = true;
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP N-1: ring bound to binding {MvppVelState.VariantN1Binding} at offset {_ringN1Offset} size {_ringN1Size}" +
                            $"{(_n1BindActive ? " (N1BIND armed: real on pair hits, trivial on misses)" : " (5a: current palette = trivial -> expect MV=0)")}.");
                    }
                }

                bool sqThisDraw = _samplesProbe && _sqArmed;
                if (sqThisDraw)
                {
                    _sqArmed = false;
                    context.Renderer.ReportCounter(CounterType.SamplesPassed, (_, _) => { }, 1f, false);
                }

                if (indexed)
                {
                    context.Renderer.Pipeline.DrawIndexed(count, instanceCount, firstIndex, firstVertex, firstInstance);
                }
                else
                {
                    context.Renderer.Pipeline.Draw(count, instanceCount, firstVertex, firstInstance);
                }

                if (sqThisDraw)
                {
                    int verts = count;
                    context.Renderer.ReportCounter(CounterType.SamplesPassed, (_, ev) =>
                    {
                        if (_sqLogged < 90)
                        {
                            _sqLogged++;
                            Logger.Warning?.Print(LogClass.Gpu,
                                $"MVPP samplesq: variant re-draw samples={ev} (verts={verts}) [{_sqLogged}/90].");
                        }
                    }, 1f, false);
                }

                if (_readbackProbe && _rbArmed && _rbLogged < 90)
                {
                    _rbArmed = false;
                    _rbLogged++;

                    try
                    {
                        using PinnedSpan<byte> data = _coverage.GetData();
                        ReadOnlySpan<ushort> h16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(data.Get());
                        int sentinel = 0, zeroish = 0, written = 0;
                        float maxAbs = 0f;

                        for (int i = 0; i + 1 < h16.Length; i += 2)
                        {
                            float r = (float)BitConverter.UInt16BitsToHalf(h16[i]);
                            float g = (float)BitConverter.UInt16BitsToHalf(h16[i + 1]);

                            if (r < -900f) { sentinel++; }
                            else if (MathF.Abs(r) <= 1e-4f && MathF.Abs(g) <= 1e-4f) { zeroish++; }
                            else
                            {
                                written++;
                                float a = MathF.Max(MathF.Abs(r), MathF.Abs(g));
                                if (a > maxAbs) { maxAbs = a; }
                            }
                        }

                        Logger.Warning?.Print(LogClass.Gpu,
                            $"MVPP readback: sentinel={sentinel} zero={zeroish} written={written} " +
                            $"(texels={h16.Length / 2}) max|rg|={maxAbs:0.####} [{_rbLogged}/90].");
                    }
                    catch (Exception e)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, $"MVPP readback failed: {e.Message}");
                    }
                }

                if (_twoPassEnabled)
                {
                    if (indexed)
                    {
                        context.Renderer.Pipeline.DrawIndexed(count, instanceCount, firstIndex, firstVertex, firstInstance);
                    }
                    else
                    {
                        context.Renderer.Pipeline.Draw(count, instanceCount, firstVertex, firstInstance);
                    }
                }

                _redraws++;
                MvppVelState.CoverageFresh = true;
            }

            _grafts++;

            if (!_graftLogged)
            {
                _graftLogged = true;
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP velocity graft: ACTIVE ({(_graftDrawAll ? "DRAW-ALL" : _graftDraw ? "DRAW-1" : "no-op")} mode, depth eps {_depthEps}), coverage {w}x{h}.");
            }

            channel.TextureManager.MvppInvalidateHostRenderTargets();

            if (_targetedRestore)
            {
                engine.ForceStateDirtyByIndex(StateUpdater.BlendStateIndex);
                engine.ForceStateDirtyByIndex(StateUpdater.StencilTestStateIndex);
                engine.ForceStateDirtyByIndex(StateUpdater.DepthTestStateIndex);
                engine.ForceStateDirtyByIndex(StateUpdater.DepthBiasStateIndex);
                engine.ForceStateDirtyByIndex(StateUpdater.RtColorMaskIndex);
                engine.ForceStateDirtyByIndex(StateUpdater.RasterizerStateIndex);
                engine.ForceStateDirtyByIndex(StateUpdater.ScissorStateIndex);
                engine.ForceStateDirtyByIndex(StateUpdater.ShaderStateIndex);
                engine.ForceStateDirtyByIndex(StateUpdater.RenderTargetStateIndex);
            }
            else
            {
                engine.ForceStateDirty();
            }

            engine.UpdateState();
        }

        private static void LogWindow()
        {
            if (_draws > 0 || _scale1Skips > 0)
            {
                int d = Math.Max(_draws, 1);
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP velocity: {_draws} scaled draws, big-cb5 {(100f * _bigCb5 / d):0.0}%, " +
                    $"int-attr {(100f * _intAttr / d):0.0}%, SKINNED {(100f * _skinned / d):0.0}% " +
                    $"({_skinned}, cb5 avg {(_skinned > 0 ? _skinnedCb5Sum / _skinned : 0)} max {_skinnedCb5Max} B), grafts {_grafts} (sameprog {(100f * _graftSameProg / Math.Max(_grafts, 1)):0}%), redraws {_redraws}, wskip {_writeOffSkips}, " +
                    $"scale1 {_scale1Skips}, nosnap {_candNoSnap}, projfail {_candProjFail}, " +
                    $"dsScene {MvppCameraCapture.SceneDepthHostWidth}x{MvppCameraCapture.SceneDepthHostHeight}, " +
                    $"candDs scene/other/none {_candDsScene}/{_candDsOther}/{_candDsNone}" +
                    $"{(_candDsOther > 0 ? $" (other last {_candDsOtherW}x{_candDsOtherH})" : "")}" +
                    $"{(_v4Enabled ? $", accC {_accContig}, accS {_accSpread}" : "")}" +
                    $"{(_cohEnabled ? $", cohRcount {_cohRejCount}, cohRspan {_cohRejSpan}" : "")}" +
                    $"{(_coh2Enabled ? $", coh2R {_coh2Rejects}" : "")}" +
                    $"{(_variantEnabled ? $", vBind {_variantBinds}, vFallback {_variantFallbacks}, vRestore {MvppVelState.VariantRestores}" : "")}" +
                    $"{(_n1BindActive ? $", vN1 {_n1RealBinds}/{_n1TrivialBinds}, plaus {_plausOk}/{_plausRej}" : "")}.");

                if (_nonInstanced > 0)
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP velocity cb5 bands (non-instanced, n={_nonInstanced}): " +
                        $"none {100f * _niCb5None / _nonInstanced:0}%, <512 {100f * _niCb5Small / _nonInstanced:0}%, " +
                        $"512-4K {100f * _niCb5Palette / _nonInstanced:0}%, >=4K {100f * _niCb5Big / _nonInstanced:0}%.");
                }

                if (_v4Enabled && (_clsActor > 0 || _clsProp > 0))
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP int-attr probe (of CLASSIFIED): actor-band(4-5KB) {_clsActor} " +
                        $"[intStrict {(_clsActor > 0 ? 100f * _clsActorIntS / _clsActor : 0):0}%, " +
                        $"intBroad {(_clsActor > 0 ? 100f * _clsActorIntB / _clsActor : 0):0}%], " +
                        $"prop(rest) {_clsProp} " +
                        $"[intStrict {(_clsProp > 0 ? 100f * _clsPropIntS / _clsProp : 0):0}%, " +
                        $"intBroad {(_clsProp > 0 ? 100f * _clsPropIntB / _clsProp : 0):0}%].");
                }

                if (_deltaEnabled)
                {
                    for (int band = 0; band < 2; band++)
                    {
                        int mm = _dMatched[band];
                        if (mm == 0)
                        {
                            continue;
                        }

                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP delta {(band == 0 ? "actor" : "prop")}: matched {mm}, " +
                            $"static(<0.01) {100f * _dStatic[band] / mm:0}%, " +
                            $"maxHist[<.01 .01-.5 .5-2 2-10 >10]=" +
                            $"[{_dHist[band][0]} {_dHist[band][1]} {_dHist[band][2]} {_dHist[band][3]} {_dHist[band][4]}], " +
                            $"avgMax {_dSumMax[band] / mm:0.###}, avgMed {_dSumMed[band] / mm:0.###}, gMax {_dGMax[band]:0.##}.");
                    }
                }
            }

            _draws = 0;
            _bigCb5 = 0;
            _intAttr = 0;
            _skinned = 0;
            _skinnedCb5Sum = 0;
            _skinnedCb5Max = 0;
            _sampleLogged = false;
            _grafts = 0;
            _graftSameProg = 0;
            _redraws = 0;
            _writeOffSkips = 0;
            _scale1Skips = 0;
            _candNoSnap = 0;
            _candProjFail = 0;
            _projSamples = 0;
            _candDsScene = 0;
            _candDsOther = 0;
            _candDsNone = 0;
            _accContig = 0;
            _accSpread = 0;
            _cohRejCount = 0;
            _cohRejSpan = 0;
            _coh2Rejects = 0;
            _cohDiagLogged = false;
            _cohDiagAccLogged = false;
            _variantBinds = 0;
            _variantFallbacks = 0;
            _n1RealBinds = 0;
            _n1TrivialBinds = 0;
            _plausOk = 0;
            _plausRej = 0;
            MvppVelState.VariantRestores = 0;
            _clsActor = 0;
            _clsActorIntS = 0;
            _clsActorIntB = 0;
            _clsProp = 0;
            _clsPropIntS = 0;
            _clsPropIntB = 0;

            if (_deltaEnabled)
            {
                for (int b = 0; b < 2; b++)
                {
                    _dMatched[b] = 0;
                    _dStatic[b] = 0;
                    _dSumMax[b] = 0f;
                    _dSumMed[b] = 0f;
                    _dGMax[b] = 0f;
                    Array.Clear(_dHist[b]);
                }
            }
            _censusDraws = 0;
            _instancedAttr = 0;
            _nonInstanced = 0;
            _niCb5None = 0;
            _niCb5Small = 0;
            _niCb5Palette = 0;
            _niCb5Big = 0;
            Array.Clear(_typeCensus);
        }
    }
}
