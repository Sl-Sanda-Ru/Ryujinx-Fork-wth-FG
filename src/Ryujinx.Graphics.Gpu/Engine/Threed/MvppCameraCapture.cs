
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppCameraCapture
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_CAP") == "1";

        private static readonly bool _capWhy =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_CAPWHY") == "1";

        internal static readonly bool Verbose =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VERBOSE") == "1";

        private static readonly bool _dupSkip =
            Environment.GetEnvironmentVariable("RYUJINX_DLSS_DUPSKIP") == "1";

        private static readonly bool _fgFeed =
            Environment.GetEnvironmentVariable("RYUJINX_DLSS_FGFEED") == "1";

        private static int _cwQual;
        private static int _cwNeed;
        private static int _cwImpl;
        private static int _cwCachedOk;
        private static int _cwHeld;
        private static int _cwRescanOk;
        private static int _cwSoloCall;
        private static int _cwSoloOk;

        private static int _cwRaw;
        private static int _cwFrameHadRaw;
        private static int _cwFrameHadQual;
        private static int _cwFramesNoDraw;
        private static int _cwFramesPrefiltred;
        private static int _cwFramesOk;

        private static int _capturedThisFrame;
        private static int _cachedSlot = -1;
        private static int _cachedOffset;
        private static long _lastHeartbeatMs;
        private static ulong _captures;
        private static ulong _rescans;

        private static long _pubWindowMs;
        private static int _statEnqueues;
        private static int _statPubFrames;
        private static int _statCapScene;
        private static int _statCapAux;
        private static int _statVpChanges;
        private static ulong _lastPubFp;
        private static bool _hasPubFp;
        private static int _vpTransitionLogs;
        private const int VpTransitionLogCap = 200;

        private static float _lastR3X, _lastR3Y, _lastR3Z, _lastR3W;
        private static bool _hasR3;
        private static int _statVpJumps;

        private static readonly bool _gateEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_CAP_GATE") == "1";

        private const float GateDirEps = 0.35f;
        private const float GatePosEps = 30f;
        private const int GateResyncFrames = 30;

        private static float _lastPosX, _lastPosY, _lastPosZ;
        private static float _rivalPosX, _rivalPosY, _rivalPosZ;
        private static float _rivalDirX, _rivalDirY, _rivalDirZ;
        private static bool _hasRival;
        private static int _rivalStreak;
        private static bool _rivalSeenThisFrame;
        private static bool _gateHeldSlot;
        private static int _statRivals;
        private static int _statLineageLate;
        private static int _statResyncs;

        private static int _statBlockChanges;

        private static readonly bool _pairProbe =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_PAIRPROBE") == "1";

        private static readonly byte[] _probeLastBlock = new byte[0xC0];
        private static bool _probeHasFp;
        private static int _probeChanges;
        private static readonly int[] _probeHist = new int[4];
        private static int _probeFrames;
        private static long _probeLogMs;

        private static bool _fifoMode =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VPFIFO") == "1";

        /// <summary>
        /// Re-reads the fields above that come from the settings profile, so a changed upscaler
        /// setting applies at the next game start without restarting the app.
        /// </summary>
        internal static void ReloadProfileSettings()
        {
            _enabled = Environment.GetEnvironmentVariable("RYUJINX_MVPP_CAP") == "1";
            {
                _fifoMode = Environment.GetEnvironmentVariable("RYUJINX_MVPP_VPFIFO") == "1";
            }

            DlssCameraState.Enabled = _enabled;
            DlssCameraState.FifoEnabled = _enabled && _fifoMode;
        }

        private static bool _hasSoloPushed;
        private static Matrix4x4 _lastSoloPushed;

        static MvppCameraCapture()
        {
            DlssCameraState.Enabled = _enabled;
            DlssCameraState.FifoEnabled = _enabled && _fifoMode;
        }

        public static void OnDraw(GpuChannel channel)
        {
            if (!_enabled)
            {
                return;
            }

            if (_capWhy || _dupSkip || _fgFeed)
            {
                if (_capWhy)
                {
                    _cwRaw++;
                }

                _cwFrameHadRaw = 1;
            }

            if (MvppSoloCamera.Enabled)
            {
                Image.Texture aspectDs = channel.TextureManager.RenderTargetDepthStencil;

                if (aspectDs != null)
                {
                    MvppSoloCamera.NoteDepth(aspectDs.Info.Width, aspectDs.Info.Height);
                }
            }

            if (channel.TextureManager.RenderTargetScale == 1f)
            {
                if (GraphicsConfig.ResScale != 1f)
                {
                    return;
                }

                Image.Texture nativeDs = channel.TextureManager.RenderTargetDepthStencil;
                if (nativeDs == null || nativeDs.Info.Width == nativeDs.Info.Height)
                {
                    return;
                }

                if (!_nativeArmLogged)
                {
                    _nativeArmLogged = true;
                    Logger.Info?.Print(LogClass.Gpu,
                        "MVPP capture: native config (res_scale=1) -- scene pass recognized by its non-square depth-stencil (native arm).");
                }
            }

            if (MvppCtxProbe.Enabled)
            {
                MvppCtxProbe.NoteQualifyingDraw(channel);
            }

            bool needCapture = Volatile.Read(ref _capturedThisFrame) == 0;
            bool probing = (_pairProbe || _fifoMode) && _cachedSlot >= 0;

            if (_capWhy)
            {
                _cwQual++;
                _cwFrameHadQual = 1;

                if (needCapture)
                {
                    _cwNeed++;
                }
            }

            if (needCapture && MvppScenePass.Enabled && !MvppScenePass.Allows(channel))
            {
                needCapture = false;
            }

            if (needCapture && MvppLdrSkip.Active && !MvppLdrSkip.Allows(channel))
            {
                needCapture = false;
            }

            try
            {
                StashSceneDepth(channel);

                if (needCapture && MvppSoloCamera.StaleAtElectedLocation(channel))
                {
                    needCapture = false;
                }

                if (needCapture)
                {
                    OnDrawImpl(channel);
                }
                else if (MvppSoloCamera.CapTime && Volatile.Read(ref _capturedThisFrame) == 1)
                {
                    MvppSoloCamera.NoteLateDraw(channel);
                }

                if (probing)
                {
                    ProbeDistinctValues(channel);
                }
            }
            catch (Exception e)
            {
                _enabled = false;
                DlssCameraState.Enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP capture: disabled after unexpected error: {e}");
            }
        }

        private static Image.Texture _sceneDepth;

        public static int StatScaledDraws;
        public static int StatDepthBound;

        public static int SceneDepthHostWidth;
        public static int SceneDepthHostHeight;

        private static int _guardRejects;
        private static long _guardLogMs;

        private static bool _nativeArmLogged;

        private static readonly bool _depthProbe =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_DEPTHPROBE") == "1";
        private static bool _depthProbeFaulted;

        private static readonly bool _depthFix =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_DEPTHFIX") != "0";

        private static readonly bool _colorCapProbe =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_COLORCAP_PROBE") == "1";
        private static readonly System.Collections.Generic.HashSet<long> _colorCapSeen = new();
        private static long _colorCapFrameId;

        private const int DepthCensusMax = 16;
        private static readonly Format[] _censusFmt = new Format[DepthCensusMax];
        private static readonly int[] _censusW = new int[DepthCensusMax];
        private static readonly int[] _censusH = new int[DepthCensusMax];
        private static readonly int[] _censusDraws = new int[DepthCensusMax];
        private static readonly bool[] _censusSq = new bool[DepthCensusMax];
        private static readonly ulong[] _censusIdent = new ulong[DepthCensusMax];
        private static readonly int[] _censusIdentN = new int[DepthCensusMax];
        private static int _censusCount;
        private static int _censusOverflow;

        private static ulong _depthSigLast;
        private static bool _hasDepthSig;
        private static int _depthTransitionLogs;
        private const int DepthTransitionLogCap = 400;
        private static long _depthWindowMs;
        private static int _statDepthPresents;
        private static int _statDepthCensusChanges;

        private static Image.Texture _sceneColor;

        public static Image.Texture TakeSceneColor()
        {
            Image.Texture c = _sceneColor;
            _sceneColor = null;
            return c;
        }

        public static long ColorCapFrameId => _colorCapFrameId;

        public static ulong SceneColorAddress => _sceneColor != null ? _sceneColor.Range.GetSubRange(0).Address : 0UL;

        private static void StashSceneDepth(GpuChannel channel)
        {
            StatScaledDraws++;

            Image.Texture ds = channel.TextureManager.RenderTargetDepthStencil;

            if (ds == null)
            {
                return;
            }

            StatDepthBound++;

            if (_depthProbe)
            {
                RecordDepthCensus(ds.Info.FormatInfo.Format, ds.Info.Width, ds.Info.Height,
                    ds.Info.Width == ds.Info.Height, ds.Range.GetSubRange(0).Address);
            }

            if (ds.Info.Width == ds.Info.Height)
            {
                _guardRejects++;

                long gnow = Environment.TickCount64;
                if (gnow - _guardLogMs >= 2000)
                {
                    _guardLogMs = gnow;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP capture guard: square DS rejected from scene publication ({ds.Info.Width}x{ds.Info.Height}, total {_guardRejects}).");
                }

                return;
            }

            if (_colorCapProbe)
            {
                Image.Texture col0 = channel.TextureManager.RenderTargetColor0;
                GAL.ITexture colHost = col0?.HostTexture;
                GAL.ITexture dsHost = ds.HostTexture;
                int cW = colHost?.Width ?? 0, cH = colHost?.Height ?? 0;
                int dW = dsHost?.Width ?? 0, dH = dsHost?.Height ?? 0;
                int cFmt = col0 != null ? (int)col0.Info.FormatInfo.Format : -1;
                long sig = ((long)((byte)cFmt) << 48) | ((long)(cW & 0x3FFF) << 34) | ((long)(cH & 0x3FFF) << 20)
                         | ((long)(dW & 0x3FF) << 10) | (long)(dH & 0x3FF);
                if (_colorCapSeen.Add(sig))
                {
                    ulong cAddr = col0 != null ? col0.Range.GetSubRange(0).Address : 0UL;
                    ulong dAddr = ds.Range.GetSubRange(0).Address;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP COLORCAP: frame={_colorCapFrameId} " +
                        $"COLOR0={(col0 == null ? "null" : $"{col0.Info.FormatInfo.Format} {cW}x{cH} @0x{cAddr:X}")} " +
                        $"DEPTH={ds.Info.FormatInfo.Format} {dW}x{dH} @0x{dAddr:X} " +
                        $"sameDims={(col0 != null && cW == dW && cH == dH)} colorBound={(col0 != null)}");
                }
            }

            if (MvppColorSnapshot.Enabled)
            {
                Image.Texture c0 = channel.TextureManager.RenderTargetColor0;
                if (c0 != null && c0.Info.Width != c0.Info.Height)
                {
                    Format cf = c0.Info.FormatInfo.Format;
                    if ((cf == Format.R11G11B10Float || cf == Format.R16G16B16A16Float) &&
                        (_sceneColor == null ||
                         (long)c0.Info.Width * c0.Info.Height > (long)_sceneColor.Info.Width * _sceneColor.Info.Height))
                    {
                        _sceneColor = c0;
                        DlssJitterState.SceneColorFrameId = DlssJitterState.FrameId;
                    }
                }
            }

            if (_sceneDepth == null ||
                (long)ds.Info.Width * ds.Info.Height > (long)_sceneDepth.Info.Width * _sceneDepth.Info.Height ||
                (_depthFix &&
                 (long)ds.Info.Width * ds.Info.Height == (long)_sceneDepth.Info.Width * _sceneDepth.Info.Height &&
                 DepthRank(ds.Info.FormatInfo.Format) > DepthRank(_sceneDepth.Info.FormatInfo.Format)))
            {
                _sceneDepth = ds;

                DlssJitterState.SceneDepthFrameId = DlssJitterState.FrameId;

                GAL.ITexture host = ds.HostTexture;
                if (host != null)
                {
                    SceneDepthHostWidth = host.Width;
                    SceneDepthHostHeight = host.Height;
                }
            }
        }

        private static int DepthRank(Format fmt) => fmt switch
        {
            Format.D32Float or Format.D32FloatS8Uint => 32,
            Format.D24UnormS8Uint or Format.S8UintD24Unorm or Format.X8UintD24Unorm => 24,
            Format.D16Unorm => 16,
            _ => 0,
        };

        public static ulong SceneDepthAddress => _sceneDepth != null ? _sceneDepth.Range.GetSubRange(0).Address : 0UL;

        public static Ryujinx.Graphics.GAL.Format SceneDepthFormat =>
            _sceneDepth != null ? _sceneDepth.Info.FormatInfo.Format : default;

        public static Ryujinx.Graphics.GAL.ITexture SceneDepthHost => _sceneDepth?.HostTexture;

        public static Image.Texture TakeSceneDepth()
        {
            Image.Texture d = _sceneDepth;
            _sceneDepth = null;

            if (_depthProbe && !_depthProbeFaulted)
            {
                try
                {
                    EmitDepthCensus(d);
                }
                catch (Exception e)
                {
                    _depthProbeFaulted = true;
                    Logger.Warning?.Print(LogClass.Gpu, $"MVPP depth probe: disabled after error: {e}");
                }

                _censusCount = 0;
                _censusOverflow = 0;
            }

            return d;
        }

        private static void RecordDepthCensus(Format fmt, int w, int h, bool sq, ulong ident)
        {
            for (int i = 0; i < _censusCount; i++)
            {
                if (_censusFmt[i] == fmt && _censusW[i] == w && _censusH[i] == h)
                {
                    _censusDraws[i]++;

                    if (ident != _censusIdent[i])
                    {
                        _censusIdent[i] = ident;
                        _censusIdentN[i]++;
                    }

                    return;
                }
            }

            if (_censusCount >= DepthCensusMax)
            {
                _censusOverflow++;

                return;
            }

            int n = _censusCount++;
            _censusFmt[n] = fmt;
            _censusW[n] = w;
            _censusH[n] = h;
            _censusDraws[n] = 1;
            _censusSq[n] = sq;
            _censusIdent[n] = ident;
            _censusIdentN[n] = 1;
        }

        private static void EmitDepthCensus(Image.Texture winner)
        {
            bool hasWinner = winner != null;
            Format wFmt = default;
            int wW = 0, wH = 0;

            if (hasWinner)
            {
                wFmt = winner.Info.FormatInfo.Format;
                wW = winner.Info.Width;
                wH = winner.Info.Height;
            }

            ulong sig = 14695981039346656037UL;
            for (int i = 0; i < _censusCount; i++)
            {
                sig = MixCensus(sig, (int)_censusFmt[i], _censusW[i], _censusH[i],
                    (_censusSq[i] ? 2 : 0) | (_censusIdentN[i] > 1 ? 1 : 0));
            }
            sig = MixCensus(sig, hasWinner ? (int)wFmt : -1, wW, wH, _censusOverflow > 0 ? 1 : 0);

            _statDepthPresents++;

            bool changed = !_hasDepthSig || sig != _depthSigLast;
            if (changed)
            {
                _hasDepthSig = true;
                _depthSigLast = sig;
                _statDepthCensusChanges++;
            }

            long now = Environment.TickCount64;
            if (now - _depthWindowMs >= 5000)
            {
                if (_depthWindowMs != 0)
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP depth window: presents {_statDepthPresents}, census-changes " +
                        $"{_statDepthCensusChanges}, change-lines {_depthTransitionLogs}/{DepthTransitionLogCap}.");
                }

                _depthWindowMs = now;
                _statDepthPresents = 0;
                _statDepthCensusChanges = 0;
            }

            if (!changed || _depthTransitionLogs >= DepthTransitionLogCap)
            {
                return;
            }

            _depthTransitionLogs++;

            StringBuilder sb = new();
            sb.Append($"MVPP depth census (change #{_statDepthCensusChanges}): ");

            if (_censusCount == 0)
            {
                sb.Append("(no DS bound this interval) ");
            }

            for (int i = 0; i < _censusCount; i++)
            {
                bool isWin = hasWinner && _censusFmt[i] == wFmt && _censusW[i] == wW && _censusH[i] == wH;

                sb.Append($"[{_censusFmt[i]} {_censusW[i]}x{_censusH[i]} x{_censusDraws[i]}, {_censusIdentN[i]} ident");
                if (_censusSq[i])
                {
                    sb.Append(" SQ-REJ");
                }
                if (isWin)
                {
                    sb.Append(" <== WINNER");
                }
                sb.Append("] ");
            }

            if (_censusOverflow > 0)
            {
                sb.Append($"(+{_censusOverflow} over cap) ");
            }
            if (!hasWinner)
            {
                sb.Append("(winner: none) ");
            }

            Logger.Info?.Print(LogClass.Gpu, sb.ToString());
        }

        private static ulong MixCensus(ulong h, int a, int b, int c, int d)
        {
            unchecked
            {
                h = (h ^ (uint)a) * 1099511628211UL;
                h = (h ^ (uint)b) * 1099511628211UL;
                h = (h ^ (uint)c) * 1099511628211UL;
                h = (h ^ (uint)d) * 1099511628211UL;
            }

            return h;
        }

        private static void ProbeDistinctValues(GpuChannel channel)
        {
            ulong address = channel.BufferManager.GetGraphicsUniformBufferAddress(0, _cachedSlot);
            int size = channel.BufferManager.GetGraphicsUniformBufferSize(0, _cachedSlot);

            if (address == 0 || address == ulong.MaxValue || _cachedOffset + 0xC0 > size)
            {
                return;
            }

            ReadOnlySpan<byte> block = channel.MemoryManager.Physical.GetSpan(address + (ulong)_cachedOffset, 0xC0);

            if (!_probeHasFp || !block.SequenceEqual(_probeLastBlock))
            {
                _probeHasFp = true;
                block.CopyTo(_probeLastBlock);
                _statBlockChanges++;

                if (_pairProbe)
                {
                    Interlocked.Increment(ref _probeChanges);
                }

                if (_fifoMode)
                {
                    ReadOnlySpan<float> f = MemoryMarshal.Cast<byte, float>(block);
                    ReadOnlySpan<float> view = f.Slice(0, 16);
                    ReadOnlySpan<float> proj = f.Slice(16, 16);
                    ReadOnlySpan<float> vp = f.Slice(32, 16);

                    if (IsOrthonormalView(view) && IsPerspectiveProj(proj) && ProductMatches(proj, view, vp))
                    {
                        if (IsSquareProjection(proj))
                        {
                            return;
                        }

                        Matrix4x4 m = new(
                            vp[0], vp[1], vp[2], vp[3],
                            vp[4], vp[5], vp[6], vp[7],
                            vp[8], vp[9], vp[10], vp[11],
                            vp[12], vp[13], vp[14], vp[15]);

                        DlssCameraState.PushOrdered(in m);
                        MvppFamHold.NotePush();
                    }
                }
            }
        }

        public static void OnFrameEnqueued()
        {
            _colorCapFrameId++;

            if (MvppHangWatch.Enabled)
            {
                MvppHangWatch.Ping();
            }

            if (MvppCtxProbe.Enabled)
            {
                MvppCtxProbe.OnFrame();
            }

            if (_gateEnabled)
            {
                if (Volatile.Read(ref _capturedThisFrame) == 1)
                {
                    _rivalStreak = 0;
                    _hasRival = false;
                }
                else if (_rivalSeenThisFrame)
                {
                    _rivalStreak++;
                }

                _rivalSeenThisFrame = false;
            }

            if (MvppScenePass.Enabled)
            {
                MvppScenePass.OnFrame(Volatile.Read(ref _capturedThisFrame) == 1);
            }

            if (MvppLdrSkip.Active)
            {
                MvppLdrSkip.OnFrame(Volatile.Read(ref _capturedThisFrame) == 1);
            }

            MvppSoloCamera.OnPresentBoundary(Volatile.Read(ref _capturedThisFrame) == 1);
            MvppSoloCamera.LogWhy();

            Volatile.Write(ref _capturedThisFrame, 0);

            if (_capWhy || _dupSkip || _fgFeed)
            {
                if ((_dupSkip || _fgFeed) && _cwFrameHadRaw == 1)
                {
                    System.Threading.Interlocked.Increment(ref DlssCameraState.RenderedFrameSeq);
                }

                if (_capWhy)
                {
                    if (_cwFrameHadRaw == 0)
                    {
                        _cwFramesNoDraw++;
                    }
                    else if (_cwFrameHadQual == 0)
                    {
                        _cwFramesPrefiltred++;
                    }
                    else
                    {
                        _cwFramesOk++;
                    }
                }

                _cwFrameHadRaw = 0;
                _cwFrameHadQual = 0;
            }

            _statEnqueues++;

            long nowPub = Environment.TickCount64;
            if (nowPub - _pubWindowMs >= 5000)
            {
                if (_pubWindowMs != 0 && _enabled && Verbose)
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP capture window: enq {_statEnqueues}, pub {_statPubFrames} " +
                        $"(scene {_statCapScene} / aux {_statCapAux}), vp-changes {_statVpChanges}, " +
                        $"vp-jumps {_statVpJumps}, blk-changes {_statBlockChanges}, " +
                        $"sq-rejects {StatSquareRejects}, rescans {_rescans}, " +
                        $"rivals {_statRivals}, late {_statLineageLate}, resyncs {_statResyncs}.");

                    if (_capWhy)
                    {
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP capture why: raw {_cwRaw}, qual {_cwQual}, need {_cwNeed}, impl {_cwImpl}, " +
                            $"cachedOk {_cwCachedOk}, held {_cwHeld}, rescanOk {_cwRescanOk}, " +
                            $"soloCall {_cwSoloCall}, soloOk {_cwSoloOk}, " +
                            $"none {_cwImpl - _cwCachedOk - _cwHeld - _cwRescanOk - _cwSoloOk} | " +
                            $"frames: noDraw {_cwFramesNoDraw}, allPrefiltered {_cwFramesPrefiltred}, ok {_cwFramesOk}.");
                    }
                }

                _pubWindowMs = nowPub;
                _statEnqueues = 0;
                _statPubFrames = 0;
                _statCapScene = 0;
                _statCapAux = 0;
                _statVpChanges = 0;
                _statVpJumps = 0;
                _statBlockChanges = 0;
                _statRivals = 0;
                _statLineageLate = 0;
                _cwQual = 0;
                _cwNeed = 0;
                _cwImpl = 0;
                _cwCachedOk = 0;
                _cwHeld = 0;
                _cwRescanOk = 0;
                _cwSoloCall = 0;
                _cwSoloOk = 0;
                _cwRaw = 0;
                _cwFramesNoDraw = 0;
                _cwFramesPrefiltred = 0;
                _cwFramesOk = 0;
            }

            if (_pairProbe)
            {
                int changes = Interlocked.Exchange(ref _probeChanges, 0);
                _probeHist[Math.Min(changes, 3)]++;
                _probeFrames++;

                long now = Environment.TickCount64;
                if (now - _probeLogMs >= 5000 && _probeFrames > 0)
                {
                    _probeLogMs = now;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP pair probe: {_probeFrames} frames, camera changes/frame " +
                        $"0:{_probeHist[0]} 1:{_probeHist[1]} 2:{_probeHist[2]} 3+:{_probeHist[3]}.");
                    Array.Clear(_probeHist);
                    _probeFrames = 0;
                }
            }
        }

        private static readonly bool _dupPair =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_DUPPAIR") == "1";

        internal static readonly bool JitTrace =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_JITTRACE") == "1";

        private static readonly bool _jitTrace = JitTrace;
        private static long _jitTraceMs;

        private static int _soloDistinct;
        private static int _soloSame;
        private static long _dupLogMs;
        private static readonly int[] _dupFailBase = new int[16];
        private static readonly int[] _dupFailNow = new int[16];

        private static void DupPairLog()
        {
            long now = Environment.TickCount64;

            if (now - _dupLogMs < 1000)
            {
                return;
            }

            _dupLogMs = now;

            MvppSoloCamera.CopyFailCounts(_dupFailNow);

            var sb = new System.Text.StringBuilder();
            sb.Append($"MVPP PRODUCTION: reads distinct={_soloDistinct} identical={_soloSame} | fails: ");

            int n = Math.Min(MvppSoloCamera.FailReasonCount, _dupFailNow.Length);
            int total = 0;

            for (int i = 0; i < n; i++)
            {
                total += _dupFailNow[i] - _dupFailBase[i];
            }

            for (int i = 0; i < n; i++)
            {
                int d = _dupFailNow[i] - _dupFailBase[i];

                if (d > 0)
                {
                    sb.Append($"{MvppSoloCamera.FailNames[i]} {d} ({(total > 0 ? 100f * d / total : 0f):0.#} %) · ");
                }

                _dupFailBase[i] = _dupFailNow[i];
            }

            sb.Append($"total {total}");

            Logger.Info?.Print(LogClass.Gpu, sb.ToString());
            Logger.Info?.Print(LogClass.Gpu, MvppSoloCamera.AltSummaryAndReset());

            _soloDistinct = 0;
            _soloSame = 0;
        }

        private static void OnDrawImpl(GpuChannel channel)
        {
            if (_dupPair)
            {
                DupPairLog();
            }

            if (_capWhy)
            {
                _cwImpl++;
            }

            _gateHeldSlot = false;

            if (_cachedSlot >= 0 && TryCapture(channel, _cachedSlot, _cachedOffset))
            {
                if (_capWhy)
                {
                    _cwCachedOk++;
                }

                return;
            }

            if (_gateHeldSlot)
            {
                if (_capWhy)
                {
                    _cwHeld++;
                }

                return;
            }

            uint mask = channel.BufferManager.GetGraphicsUniformBufferUseMask(0);

            for (int slot = 0; mask != 0; slot++, mask >>= 1)
            {
                if ((mask & 1) == 0)
                {
                    continue;
                }

                int size = Math.Min(channel.BufferManager.GetGraphicsUniformBufferSize(0, slot), 1024);

                for (int offset = 0; offset + 0xC0 <= size; offset += 0x40)
                {
                    if (TryCapture(channel, slot, offset))
                    {
                        _rescans++;
                        _cachedSlot = slot;
                        _cachedOffset = offset;

                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP capture: camera block found at stage0 cbuf{slot} +0x{offset:X3} (scan #{_rescans}).");

                        if (_capWhy)
                        {
                            _cwRescanOk++;
                        }

                        return;
                    }
                }
            }

            if (_capWhy && MvppSoloCamera.Enabled)
            {
                _cwSoloCall++;
            }

            if (MvppSoloCamera.Enabled && MvppSoloCamera.TryGetViewProjection(channel, out Matrix4x4 soloVp))
            {
                if (_capWhy)
                {
                    _cwSoloOk++;
                }

                DlssCameraState.PublishCurrent(in soloVp);
                Volatile.Write(ref _capturedThisFrame, 1);
                _captures++;
                _statPubFrames++;

                if (_fifoMode && (!_hasSoloPushed || soloVp != _lastSoloPushed))
                {
                    _hasSoloPushed = true;
                    _lastSoloPushed = soloVp;

                    float pushJx = MvppSoloCamera.LastJitterX;
                    float pushJy = MvppSoloCamera.LastJitterY;

                    _ = pushJx;
                    _ = pushJy;
                    DlssCameraState.PushOrdered(in soloVp);
                    MvppFamHold.NotePush();
                    _soloDistinct++;

                    if (_jitTrace && Environment.TickCount64 - _jitTraceMs >= 1000)
                    {
                        _jitTraceMs = Environment.TickCount64;
                        Logger.Info?.Print(LogClass.Gpu,
                            $"JITTRACE A (pushed) : jx={pushJx:0.000000} jy={pushJy:0.000000} " +
                            $"i.e. ({pushJx * 960f:0.000};{pushJy * 540f:0.000}) px at 1920x1080");
                    }
                }
                else if (_fifoMode)
                {
                    _soloSame++;
                }

                GAL.ITexture soloDs = channel.TextureManager.RenderTargetDepthStencil?.HostTexture;

                if (soloDs != null && soloDs.Width == SceneDepthHostWidth && soloDs.Height == SceneDepthHostHeight)
                {
                    _statCapScene++;
                }
                else
                {
                    _statCapAux++;
                }
            }
        }

        public static int StatSquareRejects;

        private static bool IsSquareProjection(ReadOnlySpan<float> proj)
        {
            float m00 = Math.Abs(proj[0]);
            float m11 = Math.Abs(proj[5]);

            if (m11 <= 0f)
            {
                return false;
            }

            bool square = Math.Abs(m00 / m11 - 1f) < 0.2f;

            if (square)
            {
                StatSquareRejects++;
            }

            return square;
        }

        private static bool TryCapture(GpuChannel channel, int slot, int offset)
        {
            ulong address = channel.BufferManager.GetGraphicsUniformBufferAddress(0, slot);
            int size = channel.BufferManager.GetGraphicsUniformBufferSize(0, slot);

            if (address == 0 || address == ulong.MaxValue || offset + 0xC0 > size)
            {
                return false;
            }

            ReadOnlySpan<float> f = MemoryMarshal.Cast<byte, float>(
                channel.MemoryManager.Physical.GetSpan(address + (ulong)offset, 0xC0));

            ReadOnlySpan<float> view = f.Slice(0, 16);
            ReadOnlySpan<float> proj = f.Slice(16, 16);
            ReadOnlySpan<float> vp = f.Slice(32, 16);

            if (!IsOrthonormalView(view) || !IsPerspectiveProj(proj) || !ProductMatches(proj, view, vp))
            {
                return false;
            }

            if (IsSquareProjection(proj))
            {
                return false;
            }

            float camPosX = -(view[0] * view[3] + view[4] * view[7] + view[8] * view[11]);
            float camPosY = -(view[1] * view[3] + view[5] * view[7] + view[9] * view[11]);
            float camPosZ = -(view[2] * view[3] + view[6] * view[7] + view[10] * view[11]);

            if (_gateEnabled && _hasR3)
            {
                bool lineage =
                    MathF.Abs(camPosX - _lastPosX) <= GatePosEps &&
                    MathF.Abs(camPosY - _lastPosY) <= GatePosEps &&
                    MathF.Abs(camPosZ - _lastPosZ) <= GatePosEps &&
                    MathF.Abs(vp[12] - _lastR3X) <= GateDirEps &&
                    MathF.Abs(vp[13] - _lastR3Y) <= GateDirEps &&
                    MathF.Abs(vp[14] - _lastR3Z) <= GateDirEps;

                if (!lineage)
                {
                    _statRivals++;
                    _rivalSeenThisFrame = true;

                    bool sameRival = _hasRival &&
                        MathF.Abs(camPosX - _rivalPosX) <= GatePosEps &&
                        MathF.Abs(camPosY - _rivalPosY) <= GatePosEps &&
                        MathF.Abs(camPosZ - _rivalPosZ) <= GatePosEps &&
                        MathF.Abs(vp[12] - _rivalDirX) <= GateDirEps &&
                        MathF.Abs(vp[13] - _rivalDirY) <= GateDirEps &&
                        MathF.Abs(vp[14] - _rivalDirZ) <= GateDirEps;

                    if (!sameRival)
                    {
                        _hasRival = true;
                        _rivalStreak = 0;
                    }

                    _rivalPosX = camPosX;
                    _rivalPosY = camPosY;
                    _rivalPosZ = camPosZ;
                    _rivalDirX = vp[12];
                    _rivalDirY = vp[13];
                    _rivalDirZ = vp[14];

                    if (_rivalStreak < GateResyncFrames)
                    {
                        _gateHeldSlot = true;

                        return false;
                    }

                    _statResyncs++;
                    _hasRival = false;
                    _rivalStreak = 0;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP capture gate: camera-cut resync #{_statResyncs}, " +
                        $"pos=[{camPosX:0.#} {camPosY:0.#} {camPosZ:0.#}], " +
                        $"r3=[{vp[12]:0.###} {vp[13]:0.###} {vp[14]:0.###} {vp[15]:0.###}].");
                }
                else if (_rivalSeenThisFrame)
                {
                    _statLineageLate++;
                }
            }

            Matrix4x4 m = new(
                vp[0], vp[1], vp[2], vp[3],
                vp[4], vp[5], vp[6], vp[7],
                vp[8], vp[9], vp[10], vp[11],
                vp[12], vp[13], vp[14], vp[15]);

            DlssCameraState.PublishCurrent(in m);
            Volatile.Write(ref _capturedThisFrame, 1);
            _captures++;
            _statPubFrames++;

            Image.Texture winnerDs = channel.TextureManager.RenderTargetDepthStencil;
            GAL.ITexture winnerDsHost = winnerDs?.HostTexture;
            bool sceneWinner = winnerDsHost != null &&
                winnerDsHost.Width == SceneDepthHostWidth &&
                winnerDsHost.Height == SceneDepthHostHeight;

            if (sceneWinner)
            {
                _statCapScene++;
            }
            else
            {
                _statCapAux++;
            }

            ulong pubFp = 14695981039346656037UL;
            foreach (byte b in MemoryMarshal.AsBytes(vp))
            {
                pubFp = (pubFp ^ b) * 1099511628211UL;
            }

            if (!_hasPubFp || pubFp != _lastPubFp)
            {
                _hasPubFp = true;
                _lastPubFp = pubFp;
                _statVpChanges++;
            }

            bool teleport = _hasR3 &&
                (MathF.Abs(camPosX - _lastPosX) > GatePosEps ||
                 MathF.Abs(camPosY - _lastPosY) > GatePosEps ||
                 MathF.Abs(camPosZ - _lastPosZ) > GatePosEps ||
                 MathF.Abs(vp[12] - _lastR3X) > GateDirEps ||
                 MathF.Abs(vp[13] - _lastR3Y) > GateDirEps ||
                 MathF.Abs(vp[14] - _lastR3Z) > GateDirEps);

            if (teleport || !_hasR3)
            {
                if (teleport)
                {
                    _statVpJumps++;
                }

                if (_vpTransitionLogs < VpTransitionLogCap)
                {
                    _vpTransitionLogs++;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP capture publish: VP {(teleport ? "TELEPORT" : "first")}, " +
                        $"winner={(sceneWinner ? "SCENE" : "AUX")} " +
                        $"ds={(winnerDsHost == null ? "none" : $"{winnerDsHost.Width}x{winnerDsHost.Height}")}, " +
                        $"pos=[{camPosX:0.#} {camPosY:0.#} {camPosZ:0.#}], " +
                        $"r3=[{vp[12]:0.###} {vp[13]:0.###} {vp[14]:0.###} {vp[15]:0.###}] " +
                        $"({_vpTransitionLogs}/{VpTransitionLogCap}).");
                }
            }

            _hasR3 = true;
            _lastR3X = vp[12];
            _lastR3Y = vp[13];
            _lastR3Z = vp[14];
            _lastR3W = vp[15];
            _lastPosX = camPosX;
            _lastPosY = camPosY;
            _lastPosZ = camPosZ;

            long now = Environment.TickCount64;
            if (GAL.MvppDev.Enabled && now - _lastHeartbeatMs >= 5000)
            {
                _lastHeartbeatMs = now;
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP capture: alive at cbuf{slot}+0x{offset:X3}, {_captures} frames captured, " +
                    $"vp r3=[{vp[12]:0.###} {vp[13]:0.###} {vp[14]:0.###} {vp[15]:0.###}].");
            }

            return true;
        }

        private static bool IsOrthonormalView(ReadOnlySpan<float> m)
        {
            if (MathF.Abs(m[12]) > 1e-3f || MathF.Abs(m[13]) > 1e-3f ||
                MathF.Abs(m[14]) > 1e-3f || MathF.Abs(m[15] - 1f) > 1e-3f)
            {
                return false;
            }

            for (int r = 0; r < 3; r++)
            {
                float len = m[r * 4] * m[r * 4] + m[r * 4 + 1] * m[r * 4 + 1] + m[r * 4 + 2] * m[r * 4 + 2];

                if (MathF.Abs(len - 1f) > 0.02f)
                {
                    return false;
                }
            }

            for (int a = 0; a < 3; a++)
            {
                for (int b = a + 1; b < 3; b++)
                {
                    float dot = m[a * 4] * m[b * 4] + m[a * 4 + 1] * m[b * 4 + 1] + m[a * 4 + 2] * m[b * 4 + 2];

                    if (MathF.Abs(dot) > 0.02f)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsPerspectiveProj(ReadOnlySpan<float> m)
        {
            return m[0] > 0.05f && MathF.Abs(m[1]) < 1e-4f && MathF.Abs(m[2]) < 1e-4f && MathF.Abs(m[3]) < 1e-4f &&
                   MathF.Abs(m[4]) < 1e-4f && m[5] > 0.05f && MathF.Abs(m[6]) < 1e-4f && MathF.Abs(m[7]) < 1e-4f &&
                   MathF.Abs(m[8]) < 1e-4f && MathF.Abs(m[9]) < 1e-4f &&
                   MathF.Abs(m[12]) < 1e-4f && MathF.Abs(m[13]) < 1e-4f &&
                   MathF.Abs(MathF.Abs(m[14]) - 1f) < 0.01f &&
                   MathF.Abs(m[15]) < 1e-3f;
        }

        private static bool ProductMatches(ReadOnlySpan<float> p, ReadOnlySpan<float> v, ReadOnlySpan<float> vp)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float expected = 0f;

                    for (int k = 0; k < 4; k++)
                    {
                        expected += p[r * 4 + k] * v[k * 4 + c];
                    }

                    float tolerance = MathF.Max(0.02f, MathF.Abs(expected) * 0.01f);

                    if (MathF.Abs(vp[r * 4 + c] - expected) > tolerance)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
