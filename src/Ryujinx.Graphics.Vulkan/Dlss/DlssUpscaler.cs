
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using Silk.NET.Vulkan;
using System;
using Extents2D = Ryujinx.Graphics.GAL.Extents2D;
using Format = Ryujinx.Graphics.GAL.Format;
using SamplerCreateInfo = Ryujinx.Graphics.GAL.SamplerCreateInfo;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Ryujinx.Graphics.Vulkan.Dlss
{
    internal sealed class DlssUpscaler : IDisposable
    {
        internal const uint ViewportId = 0;
        private const float MaxMotion = 32f;
        private const float SceneChangeHighFraction = 0.85f;
        private const float SceneChangeLowFraction = 0.55f;
        private const int ResetCooldownFrames = 45;
        private const float SceneCutMaxMotion = 0.02f;
        private const int CounterCount = 9;

        private static readonly int _mvppReprojMode =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_MVPP_REPROJ"), out int mvppEnvMode) ? mvppEnvMode : 0;

        private static readonly bool _mvppFlipY =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_FLIPY") == "1";

        private static readonly bool _mvppVpDelay =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VPDELAY") == "1";

        private static readonly string _mvppZglEnv =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_ZGL");

        private bool _mvppZMinusOne;
        private bool _mvppZLogged;

        private static readonly bool _mvppTrace =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_TRACE") == "1";

        private static readonly bool _mvppZeroMv =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_ZERO") == "1";

        private static readonly bool _mvppDynMask =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_DYNMASK") == "1";

        private static readonly bool _mvppBorderFix =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_BORDERFIX") == "1";
        private static readonly float _mvppBorderFixScan = ParseEnvFloat("RYUJINX_MVPP_BORDERFIX_SCAN", 8f);
        private static readonly float _mvppBorderFixThresh = ParseEnvFloat("RYUJINX_MVPP_BORDERFIX_THRESH", 2f);

        private static readonly bool _mvppDynSonde =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_DYNSONDE") == "1";

        private static readonly bool _mvppDepthSonde =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_DEPTHSONDE") == "1";

        private static readonly bool _mvppChecker =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_CHECKER") == "1";

        private static readonly int _mvppWarmup =
            Math.Max(0, (int)ParseEnvFloat("RYUJINX_MVPP_WARMUP", 0f));

        private int _mvppWarmupCount;

        private static readonly bool _mvppDepthGuard =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_DEPTHGUARD") == "1";
        private static readonly float _depthGuardPx = ParseEnvFloat("RYUJINX_MVPP_DEPTHGUARD_PX", 1.2f);
        private static readonly float _depthGuardRatio = ParseEnvFloat("RYUJINX_MVPP_DEPTHGUARD_RATIO", 0.7f);
        private const int DepthGuardTripSeconds = 3;
        private const int DepthGuardRecoverSeconds = 10;
        private const float DepthGuardMinBinPct = 0.02f;
        private bool _depthGuardMuted;
        private int _depthGuardBadStreak;
        private int _depthGuardGoodStreak;

        private TextureView _mvppDepthSnapA;
        private TextureView _mvppDepthSnapB;
        private bool _mvppDepthSnapFlip;
        private bool _mvppHasPrevDepth;
        private TextureView _mvppBiasMask;

        private static readonly bool _mvppBias =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_BIAS") == "1";

        private static readonly bool _rawMvSr =
            Environment.GetEnvironmentVariable("RYUJINX_DLSS_RAW_MV_SR") == "1";

        private static readonly bool _mvppFgRawMv =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_FG_RAWMV") == "1";

        private static readonly bool _mvppDump =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_DUMP") == "1";

        private static readonly float _mvppDumpDelay = ParseEnvFloat("RYUJINX_MVPP_DUMP_DELAY", 0f);

        private static readonly float _mvppDumpIntervalMs = ParseEnvFloat("RYUJINX_MVPP_DUMP_INTERVAL_MS", 2000f);

        private static readonly int _mvppDumpBurst =
            Math.Max(1, (int)ParseEnvFloat("RYUJINX_MVPP_DUMP_BURST", 1f));

        private static readonly int _mvppDumpOnMove =
            Math.Max(0, (int)ParseEnvFloat("RYUJINX_MVPP_DUMP_ON_MOVE", 0f));

        private static readonly int _mvppDumpOnHold =
            Math.Max(0, (int)ParseEnvFloat("RYUJINX_MVPP_DUMP_ON_HOLD", 0f));

        private static readonly bool _mvppIntervalFix =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_INTERVAL_FIX") == "1";

        private static readonly float _resetCooldownMs = ParseEnvFloat("RYUJINX_DLSS_RESET_COOLDOWN_MS", 0f);

        private static readonly float _sceneCutMaxMotion = ParseEnvFloat("RYUJINX_DLSS_SCENECUT_MAX_MOTION", SceneCutMaxMotion);

        private int _lastTeleportSeq;

        private static readonly bool _forceDummyDepth =
            Environment.GetEnvironmentVariable("RYUJINX_DLSS_DEPTH") == "0";

        private static readonly float _jitterSignX = ParseEnvFloat("RYUJINX_DLSS_JITTER_SIGNX", 1f);
        private static readonly float _jitterSignY = ParseEnvFloat("RYUJINX_DLSS_JITTER_SIGNY", -1f);

        private static readonly bool _injectJitUseOffset = Environment.GetEnvironmentVariable("RYUJINX_DLSS_INJECT_JIT_OFFSET") == "1";
        private static readonly float _injectJitSignX = ParseEnvFloat("RYUJINX_DLSS_INJECT_JITSX", _jitterSignX);
        private static readonly float _injectJitSignY = ParseEnvFloat("RYUJINX_DLSS_INJECT_JITSY", _jitterSignY);
        private static readonly float _injectJitScale = ParseEnvFloat("RYUJINX_DLSS_INJECT_JIT_SCALE", 1f);

        private static readonly bool _injectPostColor =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_INJECT_POSTCOLOR") == "1";

        private static readonly bool _injectTintProbe =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_INJECT_TINTPROBE") == "1";

        private static readonly bool _dumpSubrect =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_DUMP_SUBRECT") == "1";
        private static long _subrectLastTryMs;
        private static bool _subrectLowDumped;
        private static bool _subrectFullDumped;
        private static int _subrectLowFrames;
        private static int _subrectFullFrames;
        private static long _subrectSummaryMs;

        private static readonly bool _injectDumpColor =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_INJECT_DUMPCOLOR") == "1";

        private static readonly bool _injectHeldColor =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_INJECT_HELDCOLOR") == "1";

        private static readonly bool _resetCount =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_RESET_COUNT") == "1";
        private static readonly bool _injectDynRes =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_INJECT_DYNRES") == "1";

        private static readonly bool _flashCap =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_FLASHCAP") == "1";

        private static readonly bool _injectSkyRot = Environment.GetEnvironmentVariable("RYUJINX_MVPP_INJECT_SKYROT") == "1";

        private static readonly bool _injectQuiet = Environment.GetEnvironmentVariable("RYUJINX_MVPP_INJECT_QUIET") == "1";

        private static readonly float _injectGapMs = ParseEnvFloat("RYUJINX_MVPP_INJECT_GAPMS", 50f);
        private static readonly int _injectMvFlipY = ParseInjectFlipOverride("RYUJINX_DLSS_INJECT_MV_FLIPY");

        private static int ParseInjectFlipOverride(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return v == "0" ? 0 : v == "1" ? 1 : -1;
        }

        private static readonly bool _noDejitter =
            Environment.GetEnvironmentVariable("RYUJINX_DLSS_NODEJITTER") == "1";

        private static readonly bool _jitterPhaseMinus1 =
            Environment.GetEnvironmentVariable("RYUJINX_DLSS_JITTER_PHASE") == "-1";

        private int _mvppDumpCount;
        private long _mvppDumpLastMs;
        private readonly long _mvppDumpNotBeforeMs =
            Environment.TickCount64 + (long)(_mvppDumpDelay * 1000);

        private static readonly float _mvppEdgeMode = ParseEnvFloat("RYUJINX_MVPP_EDGE", 0f);

        private static readonly bool _mvppEdgeAlways = ParseEnvFloat("RYUJINX_MVPP_EDGE_ALWAYS", 0f) > 0f;

        private static readonly float _mvppFarRot = ParseEnvFloat("RYUJINX_MVPP_FARROT", 0f);

        private static readonly bool _jitterval =
            System.Environment.GetEnvironmentVariable("RYUJINX_DLSS_JITTERVAL") == "1";

        private long _jvPres;

        private static readonly bool _flashOut =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_FLASHCAP_OUT") == "1";
        private static readonly int _flashOutSec =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_MVPP_FLASHCAP_TIMED"), out int foSec) && foSec > 0 ? foSec : 4;
        private static readonly int _flashOutBurst =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_MVPP_FLASHCAP_BURST"), out int foBurst) && foBurst > 0 ? foBurst : 3;
        private static readonly int _flashOutCap =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_MVPP_FLASHCAP_CAP"), out int foCap) && foCap > 0 ? foCap : 45;
        private long _flashOutNextMs;
        private int _flashOutNext;
        private int _flashOutCount;

        private void FlashOutTick()
        {
            if (!_flashOut || _output == null)
            {
                return;
            }

            long now = Environment.TickCount64;
            if (_flashOutNext == 0 && _flashOutCount < _flashOutCap && now >= _flashOutNextMs)
            {
                _flashOutNextMs = now + _flashOutSec * 1000L;
                _flashOutNext = _flashOutBurst;
            }

            if (_flashOutNext > 0 && _flashOutCount < _flashOutCap)
            {
                try
                {
                    byte[] data = _output.GetData(0, 0, _output.Width, _output.Height);
                    if (data != null && data.Length > 0)
                    {
                        string dir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "mvpp-colordump");
                        System.IO.Directory.CreateDirectory(dir);
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir,
                            $"flashout{_flashOutCount:D3}_{_output.Width}x{_output.Height}_{_output.Info.Format}.bin"), data);
                        _flashOutCount++;
                        _flashOutNext--;
                    }
                }
                catch (System.Exception e)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"MVPP-FLASHCAP-OUT: dump failed: {e.Message}");
                }
            }
        }

        private static float JvFrob(in System.Numerics.Matrix4x4 a, in System.Numerics.Matrix4x4 b)
        {
            System.Numerics.Matrix4x4 d = a - b;
            return MathF.Sqrt(
                d.M11 * d.M11 + d.M12 * d.M12 + d.M13 * d.M13 + d.M14 * d.M14 +
                d.M21 * d.M21 + d.M22 * d.M22 + d.M23 * d.M23 + d.M24 * d.M24 +
                d.M31 * d.M31 + d.M32 * d.M32 + d.M33 * d.M33 + d.M34 * d.M34 +
                d.M41 * d.M41 + d.M42 * d.M42 + d.M43 * d.M43 + d.M44 * d.M44);
        }

        private static readonly bool _align0 =
            System.Environment.GetEnvironmentVariable("RYUJINX_DLSS_ALIGN0") == "1";
        private long _align0Ms;
        private long _align0Sig = -1;

        private static readonly char _abMode =
            (System.Environment.GetEnvironmentVariable("RYUJINX_DLSS_ABMODE") ?? "A").ToUpperInvariant() is { Length: > 0 } s ? s[0] : 'A';

        private bool _bWasActive;
        private long _align1Ms;
        private long _align1Sig = -1;

        private TextureView _bNativeHeld;
        private bool _bHasNativeHeld;
        private long _bNativeHeldFrame;
        private bool _bLockedThisFrame;
        private int _bLiveNatW, _bLiveNatH;
        private static readonly bool _blockRes =
            (Environment.GetEnvironmentVariable("RYUJINX_DLSS_BLOCKRES") ?? "1") == "1";

        private static readonly bool _bNoReset =
            (Environment.GetEnvironmentVariable("RYUJINX_DLSS_BNORESET") ?? "1") == "1";
        private int _bSuppressedCuts;

        private static readonly bool _mvppSkyRot =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_SKYROT") == "1";

        private static readonly bool _mvppSkyFlow =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_SKYFLOW") == "1";

        private static readonly bool _mvppSkyDrift =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_SKYDRIFT") == "1";

        private static readonly int _mvppSkyCutCooldown =
            (int)ParseEnvFloat("RYUJINX_MVPP_SKYCUT_COOLDOWN", 30f);

        private static readonly int _mvppStatsStride =
            (int)ParseEnvFloat("RYUJINX_MVPP_STATS_STRIDE", 1f);

        private float _skyDriftEmaX;
        private float _skyDriftEmaY;

        private static readonly bool _mvppSkyCalm =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_SKYCALM") == "1";

        private const float SkyCalmMaxPxPerFrame = 0.1f;

        private static readonly float _skyDepth =
            ParseEnvFloat("RYUJINX_MVPP_SKYDEPTH", 1.0f);

        private static readonly float _skyGridClamp = ParseEnvFloat("RYUJINX_MVPP_SKYGRID_CLAMP", 0.15f);

        private static readonly bool _skyBand =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_SKYBAND") == "1";

        private readonly uint[] _bandCount = new uint[4];
        private readonly float[] _bandX = new float[4];
        private readonly float[] _bandY = new float[4];

        private static readonly uint _skyGridMinSamples =
            (uint)MathF.Max(1f, ParseEnvFloat("RYUJINX_MVPP_SKYGRID_MINSAMPLES", 200f));

        private static readonly bool _mvppSkyGrid =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_SKYGRID") == "1";

        private readonly float[] _zoneDriftEmaX = new float[32];
        private readonly float[] _zoneDriftEmaY = new float[32];

        private static readonly bool _dlssDynRes =
            Environment.GetEnvironmentVariable("RYUJINX_DLSS_DYNRES") == "1";

        private static readonly bool _dlssPinOut =
            Environment.GetEnvironmentVariable("RYUJINX_DLSS_PINOUT") == "1";

        private static readonly bool _injectPass = MvppInjectState.Enabled;
        private const uint InjectViewportId = 1;
        private const bool SubstituteActive = true;
        private uint _injectFrame;
        private TextureView _injectDepthZero;
        private TextureView _injectMotionZero;
        private int _injectAuxW, _injectAuxH;
        private long _injectStatusMs;
        private long _injectStatusSig = -1;
        private bool _injectPickWarned;
        private bool _injectDumped;
        private bool _injectPendingDump;
        private int _injectDiagCount;
        private const int InjectDumpSettleFrames = 60;
        private int _injectOkCount;
        private long _injectFirstOkMs;
        private TextureView _injectLastDst;
        private TextureView _injectMotion;
        private TextureView _injectMotionRaw;
        private TextureView _injectReprojDepthSnap;
        private TextureView _injectReprojBiasMask;
        private int _injectReprojW, _injectReprojH;
        private BufferHandle _injectReprojStats;
        private long _injectReprojLogMs;
        private bool _injectReprojWarned;
        private bool _injectHasHistory;
        private int _injectPrevRw, _injectPrevRh, _injectPrevOw, _injectPrevOh;
        private long _injectLastRunMs;
        private bool _injectSubstitutedThisFrame;
        private bool _injectPostColorUsed;
        private bool _injectHeldColorUsed;
        private TextureView _injectTintTex;
        private int _injectTintW, _injectTintH;
        private bool _injectTintUsed;
        private bool _injectColorDumped;
        private long _injectDumpLastTryMs;
        private int _rcDims, _rcGap, _rcMvzero, _rcFirst, _rcFrames;
        private long _rcSummaryMs;
        private TextureView _flashTiny;
        private int[] _flashCur, _flashPrev;
        private bool _flashPrevValid;
        private float _flashAvg, _flashMax;
        private int _flashFrames, _flashDumpCount, _flashDumpNext;
        private long _flashLogMs;
        private long _presentSkipLogMs;

        private static readonly bool _mvppDepthLinear =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_DEPTH_LINEAR") == "1";

        private static readonly bool _mvppVelCompose =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_COMPOSE") == "1";

        private int _mvppComposeOn;
        private int _mvppComposeStale;
        private bool _mvppComposedThisFrame;

        private static readonly float _mvppDeadLo = ParseEnvFloat("RYUJINX_MVPP_DEAD_LO", 5.0f);
        private static readonly float _mvppDeadHi = ParseEnvFloat("RYUJINX_MVPP_DEAD_HI", 12.0f);

        private static float ParseEnvFloat(string name, float fallback)
        {
            return float.TryParse(
                Environment.GetEnvironmentVariable(name),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float v) ? v : fallback;
        }
        private const int MvppStatsCount = 258;
        private const int MvppBorderFilledIndex = 256;
        private static readonly float MvppMaxMotion = ParseEnvFloat("RYUJINX_MVPP_MAXMOTION", 128f);
        private System.Numerics.Matrix4x4 _mvppVpCurr;
        private System.Numerics.Matrix4x4 _mvppVpPrev;
        private System.Numerics.Matrix4x4 _mvppVpPrev2;
        private bool _mvppHasCurr;
        private bool _mvppHasPair;
        private bool _mvppHasPrev2;

        private long _mvppVpCurrStamp;
        private long _mvppVpPrevStamp;
        private long _mvppVpPrev2Stamp;
        private int _mvppNoCamPresents;

        private int _mvppMoveStreak;

        private int _mvppStreakBeforeFreeze;
        private int _mvppHoldRun;
        private bool _mvppFrozenPair;

        private bool _mvppShiftSkipped;

        private int _mvppFifoDepth;

        private float _mvppRotDeltaMax;
        private double _mvppRotDeltaSum;
        private int _mvppRotDeltaN;

        private readonly int[] _mvppJumpBins = new int[5];
        private readonly float[] _mvppJumpVals = new float[8];
        private readonly int[] _mvppJumpHits = new int[8];
        private int _mvppJumpDistinct;
        private int _mvppJumpOverflow;

        private static readonly int _mvppCamSplitStreak =
            Math.Max(1, (int)ParseEnvFloat("RYUJINX_MVPP_CAMSPLIT_STREAK", 3f));
        private static readonly float _mvppCamSplit = ParseEnvFloat("RYUJINX_MVPP_CAMSPLIT", 0f);
        private int _mvppCamRejectStreak;
        private int _mvppCamRejected;
        private int _mvppCamForced;
        private int _mvppCamStreakMax;
        private long _mvppPauseLogMs;

        private static readonly bool _mvppVerbose =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VERBOSE") == "1";
        private bool _mvppPauseLoggedOnce;

        private bool _mvppHealthRunning;
        private bool _mvppHealthKnown;
        private int _mvppHealthStreak;
        private int _mvppPresents;
        private int _mvppChanged;
        private int _mvppAlternated;
        private float _mvppDeltaMax;
        private int _mvppDeltaIdx;
        private long _mvppHeartbeatMs;
        private readonly ShaderCollection _mvppReprojProgram;
        private readonly ShaderCollection _borderFixProgram;
        private TextureView _borderFixTex;
        private bool _borderFixSkipLogged;
        private TextureView _mvppMotion;

        private TextureView _lastMvSource;

        /// <summary>
        /// The motion vector view selected for the most recent frame, in pixel units at the
        /// resolution of the view itself. Consumed by the FSR frame generation path.
        /// </summary>
        internal TextureView LastMotionVectors => _lastMvSource;
        private TextureView _mvppMotionRaw;

        private static readonly bool _velDebug =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_DEBUG") == "1";
        private static readonly bool _velFreshGate =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_FRESH") == "1";

        private static readonly bool _velDebugMv =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_VEL_DEBUG_MV") == "1";
        private readonly ShaderCollection _velDebugProgram;
        private TextureView _velDebugTex;

        private long _vdLogMs;
        private int _vdMode0;
        private int _vdMode1;
        private int _vdMode2;
        private readonly BufferHandle _mvppStatsBuffer;
        private long _mvppStatsLogMs;

        private readonly VulkanRenderer _gd;
        private readonly Device _device;
        private DlssGpuTimer _gpuTimer;
        private DlssGpuTimer _reprojTimer;
        private DlssSharpenPass _sharpenPass;

        private TextureView _output;

        private static readonly bool _dupSkipEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_DLSS_DUPSKIP") == "1";

        private long _lastRenderedSeq = -1;
        private int _dupSkips;
        private int _dupEvals;
        private long _dupLogMs;
        private bool _dupArmedLogged;

        private const int DupSkipMaxConsecutive = 8;
        private int _dupConsecutive;
        private TextureView _depth;
        private TextureView _motion;
        private TextureView _motionFiltered;
        private TextureView _prevColor;
        private bool _hasPrev;

        private BufferHandle _sceneChangeBuffer;
        private bool _wasChanging;

        private bool _motionMeterWarm;
        private int _framesSinceReset;
        private long _lastResetMs = Environment.TickCount64;
        private string _lastSizeSig;

        private float _lastJitterX;
        private float _lastJitterY;
        private float _dejitterX;
        private float _dejitterY;
        private int _jitterLogCount;
        private int _depthState = -1;
        private int _mvppDepthSig = -1;

        private readonly System.Collections.Generic.Dictionary<(int, int, Format), TextureView> _heldDepthCache = new();
        private TextureView _heldDepth;
        private bool _hasHeldDepth;

        private readonly PipelineHelperShader _motionPipeline;
        private readonly ShaderCollection _motionProgram;

        private static readonly bool _injectCutoutBias =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_INJECT_CUTOUTBIAS") == "1";
        private readonly ShaderCollection _cutoutMaskProgram;
        private TextureView _injectCutoutMask;
        private bool _cutoutBiasLogged;
        private bool _cutoutBiasNoMaskLogged;
        private int _cutoutBiasFrames;
        private long _cutoutBiasSummaryMs;
        private readonly ShaderCollection _motionFilterProgram;
        private readonly ISampler _sampler;
        private readonly ISampler _samplerNearest;

        private int _inW, _inH, _outW, _outH;
        private StreamlineDlss.DlssMode _pinnedMode;
        private int _pinnedOutW;
        private int _pinnedOutH;
        private uint _frame;
        private bool _modeLogged;
        private bool _modeWarned;

        private int _autoResProbeTick;
        private int _autoResProbeCount;

        private static long _autoResAppliedSig = -1;

        private static bool _autoResDiagLogged;

        private static long _autoResPendingSig = -1;
        private static long _autoResPendingSinceMs;

        private const long AutoResSettleMs = 500;

        private const float AutoResQuantum = 0.1f;

        private static readonly int NoSceneBypassFrames =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_DLSS_NOSCENE_BYPASS"), out int nsb) && nsb > 0
                ? nsb
                : 0;

        private int _noSceneLastPushes = -1;
        private int _noSceneStreak;
        private bool _noSceneBypassing;

        private static readonly StreamlineDlss.DlssMode[] ModesByQuality =
        {
            StreamlineDlss.DlssMode.Dlaa,
            StreamlineDlss.DlssMode.MaxQuality,
            StreamlineDlss.DlssMode.Balanced,
            StreamlineDlss.DlssMode.MaxPerformance,
            StreamlineDlss.DlssMode.UltraPerformance,
        };

        public DlssUpscaler(VulkanRenderer gd, Device device)
        {
            _gd = gd;
            _device = device;

            _motionPipeline = new PipelineHelperShader(gd, device);
            _motionPipeline.Initialize();

            byte[] shader = EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Effects/Shaders/MotionVectors.spv");

            ResourceLayout layout = new ResourceLayoutBuilder()
                .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 2)
                .Add(ResourceStages.Compute, ResourceType.StorageBuffer, 0)
                .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 1)
                .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 3)
                .Add(ResourceStages.Compute, ResourceType.Image, 0, true).Build();

            _sampler = gd.CreateSampler(SamplerCreateInfo.Create(MinFilter.Linear, MagFilter.Linear));

            _samplerNearest = gd.CreateSampler(SamplerCreateInfo.Create(MinFilter.Nearest, MagFilter.Nearest));

            _motionProgram = gd.CreateProgramWithMinimalLayout([
                new ShaderSource(shader, ShaderStage.Compute, TargetLanguage.Spirv)
            ], layout);

            byte[] filterShader = EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Effects/Shaders/MotionFilter.spv");
            ResourceLayout filterLayout = new ResourceLayoutBuilder()
                .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 2)
                .Add(ResourceStages.Compute, ResourceType.StorageBuffer, 0)
                .Add(ResourceStages.Compute, ResourceType.Image, 0, true)
                .Add(ResourceStages.Compute, ResourceType.Image, 1, true).Build();

            _motionFilterProgram = gd.CreateProgramWithMinimalLayout([
                new ShaderSource(filterShader, ShaderStage.Compute, TargetLanguage.Spirv)
            ], filterLayout);

            _sceneChangeBuffer = gd.BufferManager.CreateWithHandle(gd, CounterCount * sizeof(uint));

            if (_mvppReprojMode > 0)
            {
                byte[] mvppShader = EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Effects/Shaders/MvppReproject.spv");
                ResourceLayout mvppLayout = new ResourceLayoutBuilder()
                    .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 2)
                    .Add(ResourceStages.Compute, ResourceType.StorageBuffer, 0)
                    .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 1)
                    .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 3)
                    .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 4)
                    .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 5)
                    .Add(ResourceStages.Compute, ResourceType.Image, 0, true)
                    .Add(ResourceStages.Compute, ResourceType.Image, 1, true)
                    .Add(ResourceStages.Compute, ResourceType.Image, 2, true)
                    .Add(ResourceStages.Compute, ResourceType.Image, 3, true).Build();

                _mvppReprojProgram = gd.CreateProgramWithMinimalLayout([
                    new ShaderSource(mvppShader, ShaderStage.Compute, TargetLanguage.Spirv)
                ], mvppLayout);

                if (_mvppBorderFix)
                {
                    byte[] borderShader = EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Effects/Shaders/BorderClamp.spv");
                    ResourceLayout borderLayout = new ResourceLayoutBuilder()
                        .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 2)
                        .Add(ResourceStages.Compute, ResourceType.StorageBuffer, 0)
                        .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 1)
                        .Add(ResourceStages.Compute, ResourceType.Image, 0, true).Build();

                    _borderFixProgram = gd.CreateProgramWithMinimalLayout([
                        new ShaderSource(borderShader, ShaderStage.Compute, TargetLanguage.Spirv)
                    ], borderLayout);
                }

                _mvppStatsBuffer = gd.BufferManager.CreateWithHandle(gd, MvppStatsCount * sizeof(uint));

                Span<uint> statsInitZero = stackalloc uint[MvppStatsCount];
                gd.BufferManager.SetData<uint>(_mvppStatsBuffer, 0, statsInitZero);
            }

            if (_injectCutoutBias)
            {
                byte[] cutoutShader = EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Effects/Shaders/MvppCutoutMask.spv");
                ResourceLayout cutoutLayout = new ResourceLayoutBuilder()
                    .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 2)
                    .Add(ResourceStages.Compute, ResourceType.StorageBuffer, 0)
                    .Add(ResourceStages.Compute, ResourceType.Image, 0, true).Build();

                _cutoutMaskProgram = gd.CreateProgramWithMinimalLayout([
                    new ShaderSource(cutoutShader, ShaderStage.Compute, TargetLanguage.Spirv)
                ], cutoutLayout);
            }

            if (_velDebug)
            {
                byte[] velDbgShader = EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Effects/Shaders/MvppVelDebug.spv");
                ResourceLayout velDbgLayout = new ResourceLayoutBuilder()
                    .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 2)
                    .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 1)
                    .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 3)
                    .Add(ResourceStages.Compute, ResourceType.Image, 0, true).Build();

                _velDebugProgram = gd.CreateProgramWithMinimalLayout([
                    new ShaderSource(velDbgShader, ShaderStage.Compute, TargetLanguage.Spirv)
                ], velDbgLayout);
            }
        }

        public bool TryRun(
            TextureView input,
            TextureView depth,
            CommandBufferScoped cbs,
            TextureView dst,
            int outW,
            int outH,
            Extents2D dstRegion,
            bool hdr,
            float paperWhite,
            float peak,
            float curve,
            float gamma,
            float blend,
            float whiten,
            bool pqOutput = false)
        {
            if (!DlssIntegration.IsEnabled || !Streamline.IsInitialized)
            {
                return false;
            }

            if (NoSceneBypassFrames > 0)
            {
                int pushesNow = GAL.DlssCameraState.StatPushes;

                if (pushesNow != _noSceneLastPushes)
                {
                    _noSceneLastPushes = pushesNow;
                    _noSceneStreak = 0;
                }
                else if (_noSceneStreak < int.MaxValue)
                {
                    _noSceneStreak++;
                }

                bool bypass = _noSceneStreak >= NoSceneBypassFrames;

                if (bypass != _noSceneBypassing)
                {
                    _noSceneBypassing = bypass;
                    Logger.Info?.Print(LogClass.Gpu, bypass
                        ? $"DLSS NOSCENE: aucune camera publiee depuis {_noSceneStreak} images -- DLSS mis de cote, image presentee telle quelle."
                        : "DLSS NOSCENE: la scene est revenue -- DLSS reprend.");
                }

                if (bypass)
                {
                    return false;
                }
            }

            _presentSerial++;
            DrainFgRetired(all: false);

            DlssCameraState.ConsumePresent();

            bool camSplitReject = false;
            if (_mvppCamSplit > 0f && _mvppHasCurr && DlssCameraState.PresentVpValid)
            {
                if (RotJump(in _mvppVpCurr, DlssCameraState.PresentVp) >= _mvppCamSplit)
                {
                    if (_mvppCamRejectStreak < _mvppCamSplitStreak)
                    {
                        camSplitReject = true;
                        _mvppCamRejectStreak++;
                        _mvppCamRejected++;
                        _mvppCamStreakMax = Math.Max(_mvppCamStreakMax, _mvppCamRejectStreak);
                    }
                    else
                    {
                        _mvppCamRejectStreak = 0;
                        _mvppCamForced++;
                    }
                }
                else
                {
                    _mvppCamRejectStreak = 0;
                }
            }

            _mvppFifoDepth = GAL.DlssCameraState.StatPushes
                - GAL.DlssCameraState.StatFresh
                - GAL.DlssCameraState.StatDrops;

            bool incomingDup = _mvppHasCurr
                && DlssCameraState.PresentVpValid
                && DlssCameraState.PresentVp.Equals(_mvppVpCurr);

            bool intervalSkip = _mvppIntervalFix && incomingDup && !_mvppShiftSkipped;

            if (DlssCameraState.PresentVpValid && !camSplitReject)
            {
                if (intervalSkip)
                {
                    _mvppShiftSkipped = true;
                }
                else
                {
                    _mvppVpPrev2 = _mvppVpPrev;
                    _mvppHasPrev2 = _mvppHasPair;
                    _mvppVpPrev = _mvppVpCurr;
                    _mvppHasPair = _mvppHasCurr;
                    _mvppVpCurr = DlssCameraState.PresentVp;
                    _mvppHasCurr = true;

                    _mvppVpPrev2Stamp = _mvppVpPrevStamp;
                    _mvppVpPrevStamp = _mvppVpCurrStamp;
                    _mvppVpCurrStamp = _presentSerial;

                    _mvppShiftSkipped = false;
                }

                _mvppPresents++;

                bool mvppFreshPair = _mvppHasPair && !incomingDup;
                _mvppFrozenPair = incomingDup;

                if (_mvppFrozenPair)
                {
                    if (_mvppHoldRun == 0)
                    {
                        _mvppStreakBeforeFreeze = _mvppMoveStreak;
                    }

                    _mvppHoldRun++;
                }
                else
                {
                    _mvppHoldRun = 0;
                }

                _mvppMoveStreak = mvppFreshPair ? _mvppMoveStreak + 1 : 0;

                if (mvppFreshPair)
                {
                    _mvppChanged++;

                    if (_mvppHasPrev2 && _mvppVpCurr.Equals(_mvppVpPrev2))
                    {
                        _mvppAlternated++;
                    }

                    Span<float> cur = stackalloc float[16];
                    Span<float> prv = stackalloc float[16];
                    WriteMatrix(cur, 0, in _mvppVpCurr);
                    WriteMatrix(prv, 0, in _mvppVpPrev);

                    for (int i = 0; i < 16; i++)
                    {
                        float d = Math.Abs(cur[i] - prv[i]);
                        if (d > _mvppDeltaMax)
                        {
                            _mvppDeltaMax = d;
                            _mvppDeltaIdx = i;
                        }
                    }

                    float rot = 0f;
                    for (int r = 0; r < 3; r++)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            rot = Math.Max(rot, Math.Abs(cur[r * 4 + c] - prv[r * 4 + c]));
                        }
                    }

                    _mvppRotDeltaMax = Math.Max(_mvppRotDeltaMax, rot);
                    _mvppRotDeltaSum += rot;
                    _mvppRotDeltaN++;

                    _mvppJumpBins[rot < 0.01f ? 0 : rot < 0.1f ? 1 : rot < 0.5f ? 2 : rot < 1.0f ? 3 : 4]++;

                    if (rot >= 0.5f)
                    {
                        bool matched = false;
                        for (int k = 0; k < _mvppJumpDistinct; k++)
                        {
                            if (Math.Abs(_mvppJumpVals[k] - rot) < 0.002f)
                            {
                                _mvppJumpHits[k]++;
                                matched = true;
                                break;
                            }
                        }

                        if (!matched)
                        {
                            if (_mvppJumpDistinct < _mvppJumpVals.Length)
                            {
                                _mvppJumpVals[_mvppJumpDistinct] = rot;
                                _mvppJumpHits[_mvppJumpDistinct] = 1;
                                _mvppJumpDistinct++;
                            }
                            else
                            {
                                _mvppJumpOverflow++;
                            }
                        }
                    }

                    if (_mvppTrace)
                    {
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP trace: t=({cur[3]:0.###}, {cur[7]:0.###}, {cur[11]:0.###}).");
                    }
                }

                long nowMs = Environment.TickCount64;
                if (nowMs - _mvppHeartbeatMs >= 5000)
                {
                    _mvppHeartbeatMs = nowMs;
                    if (_mvppVerbose)
                    {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP consumer: camera VP changed on {_mvppChanged}/{_mvppPresents} presents, A/B/A/B alternations {_mvppAlternated}, " +
                        $"NO-CAM {_mvppNoCamPresents} presents (paire figee -> vecteur en retard ou cumule), " +
                        $"paire actuelle curr@{_mvppVpCurrStamp} prev@{_mvppVpPrevStamp} (ecart {_mvppVpCurrStamp - _mvppVpPrevStamp}, doit valoir 1), " +
                        $"ROT delta max={_mvppRotDeltaMax:0.######} moy={(_mvppRotDeltaN > 0 ? _mvppRotDeltaSum / _mvppRotDeltaN : 0):0.######} sur {_mvppRotDeltaN} changements, " +
                        $"max element delta {_mvppDeltaMax:0.######} at m[{_mvppDeltaIdx / 4}][{_mvppDeltaIdx % 4}] (row-major).");
                    var top = new System.Text.StringBuilder();
                    for (int k = 0; k < _mvppJumpDistinct; k++)
                    {
                        top.Append($"{_mvppJumpVals[k]:0.#####}x{_mvppJumpHits[k]} ");
                    }

                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP JUMPDIST: bins <0.01={_mvppJumpBins[0]} <0.1={_mvppJumpBins[1]} <0.5={_mvppJumpBins[2]} " +
                        $"<1.0={_mvppJumpBins[3]} >=1.0={_mvppJumpBins[4]} | gros sauts distincts={_mvppJumpDistinct}" +
                        (_mvppJumpOverflow > 0 ? $" (+{_mvppJumpOverflow} hors table)" : "") +
                        $" : {(top.Length > 0 ? top.ToString().TrimEnd() : "aucun")}" +
                        (_mvppCamSplit > 0f
                            ? $" || CAMSPLIT seuil={_mvppCamSplit:0.###} plafond={_mvppCamSplitStreak} rejets={_mvppCamRejected} " +
                              $"adoptions-forcees={_mvppCamForced} salve-max={_mvppCamStreakMax}"
                            : ""));
                    }

                    _mvppCamRejected = 0;
                    _mvppCamForced = 0;
                    _mvppCamStreakMax = 0;

                    Array.Clear(_mvppJumpBins);
                    Array.Clear(_mvppJumpVals);
                    Array.Clear(_mvppJumpHits);
                    _mvppJumpDistinct = 0;
                    _mvppJumpOverflow = 0;
                    _mvppChanged = 0;
                    _mvppNoCamPresents = 0;
                    _mvppRotDeltaMax = 0f;
                    _mvppRotDeltaSum = 0;
                    _mvppRotDeltaN = 0;
                    _mvppPresents = 0;
                    _mvppAlternated = 0;
                    _mvppDeltaMax = 0f;
                    _mvppDeltaIdx = 0;
                }
            }
            else
            {
                _mvppNoCamPresents++;
            }

            if (input.Width == 0 || input.Height == 0)
            {
                return false;
            }

            bool modeB = false;
            bool bReset = false;
            string bResetReason = "none";
            _bLockedThisFrame = false;
            if (_abMode == 'B')
            {
                TextureView natLive = MvppColorSnapshot.SceneColorBackend as TextureView;
                _bLiveNatW = natLive?.Width ?? 0;
                _bLiveNatH = natLive?.Height ?? 0;
                bool liveNatOk = natLive != null && natLive.Width == 2666 && natLive.Height == 1500 && natLive.Info.Format == Format.R11G11B10Float;
                bool liveDepthOk = depth != null && depth.Width == 2666 && depth.Height == 1500;

                if (liveNatOk && liveDepthOk)
                {
                    if (_bNativeHeld == null)
                    {
                        _bNativeHeld = _gd.CreateTexture(MakeInfo(natLive.Info, 2666, 1500, natLive.Info.Format, natLive.Info.BytesPerPixel)) as TextureView;
                    }
                    natLive.CopyTo(_bNativeHeld, 0, 0);
                    _bHasNativeHeld = true;
                    _bNativeHeldFrame = DlssJitterState.PresentFrameId;

                    if (!_bWasActive)
                    {
                        bReset = true;
                        bResetReason = "A->B entry";
                    }
                    _bWasActive = true;
                    input = natLive;
                    modeB = true;
                }
                else if (_bWasActive && _bHasNativeHeld && _blockRes)
                {
                    input = _bNativeHeld;
                    modeB = true;
                    _bLockedThisFrame = true;
                }
                else
                {
                    _bWasActive = false;
                }
            }

            float JitterSignX = _jitterSignX;
            float JitterSignY = _jitterSignY;

            DlssJitter.Advance();

            if (_jitterval)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"JITTERVAL-PRES pres={_jvPres++} frame={DlssJitterState.PresentFrameId} " +
                    $"px={DlssJitterState.PresentX:0.0000} py={DlssJitterState.PresentY:0.0000} " +
                    $"fid={DlssJitterState.FrameId} ox={DlssJitterState.OffsetX:0.0000} oy={DlssJitterState.OffsetY:0.0000} " +
                    $"camFresh={(DlssCameraState.LastConsumeFresh ? 1 : 0)} " +
                    $"camPairEq={(_mvppHasPair && _mvppVpPrev.Equals(_mvppVpCurr) ? 1 : 0)} " +
                    $"camD={(_mvppHasPair ? JvFrob(in _mvppVpCurr, in _mvppVpPrev) : -1f):0.000000}");
            }

            FlashOutTick();

            DlssJitterState.InputWidth = input.Width;
            DlssJitterState.InputHeight = input.Height;

            if (_injectSubstitutedThisFrame)
            {
                _injectSubstitutedThisFrame = false;

                long nowPs = Environment.TickCount64;
                if (nowPs - _presentSkipLogMs >= 3000)
                {
                    _presentSkipLogMs = nowPs;
                    Logger.Info?.Print(LogClass.Gpu,
                        "MVPP-INJECT: PRESENT_DLSS_SKIPPED=true INJECT_DLSS_ACTIVE=true (inject substituted -> present blits the composite, no double DLSS).");
                }

                return false;
            }
            float frameJitterX = DlssJitterState.PresentX;
            float frameJitterY = DlssJitterState.PresentY;
            bool dejitterApplies = !_noDejitter && (_mvppReprojMode < 2 || _depthGuardMuted);
            _dejitterX = dejitterApplies ? JitterSignX * (frameJitterX - _lastJitterX) : 0f;
            _dejitterY = dejitterApplies ? JitterSignY * (frameJitterY - _lastJitterY) : 0f;
            float declJitterX = _jitterPhaseMinus1 ? _lastJitterX : frameJitterX;
            float declJitterY = _jitterPhaseMinus1 ? _lastJitterY : frameJitterY;
            _lastJitterX = frameJitterX;
            _lastJitterY = frameJitterY;

            if (DlssJitter.Enabled && _jitterLogCount < 5)
            {
                _jitterLogCount++;
                Logger.Info?.Print(LogClass.Gpu,
                    $"DLSS jitter probe: carrier=({frameJitterX:0.000},{frameJitterY:0.000}) " +
                    $"galNext=({DlssJitterState.OffsetX:0.000},{DlssJitterState.OffsetY:0.000}) dejitter=({_dejitterX:0.000},{_dejitterY:0.000}).");
            }

            double inAspect = (double)input.Width / input.Height;
            int contentW = outW;
            int contentH = (int)Math.Round(outW / inAspect);
            if (contentH > outH)
            {
                contentH = outH;
                contentW = (int)Math.Round(outH * inAspect);
            }
            contentW = Math.Max(contentW, 1);
            contentH = Math.Max(contentH, 1);

            bool isDlaa = DlssIntegration.Mode == DlssQualityMode.Native;

            if (modeB)
            {
                isDlaa = false;
            }

            if (!isDlaa && !modeB)
            {
                long autoResSig = ((long)contentW << 32) | (uint)contentH;
                if (DlssIntegration.AutoResScale && _autoResAppliedSig != autoResSig && AutoResSizeSettled(autoResSig))
                {
                    _autoResAppliedSig = autoResSig;

                    if (!_autoResDiagLogged)
                    {
                        _autoResDiagLogged = true;

                        foreach (StreamlineDlss.DlssMode dm in ModesByQuality)
                        {
                            bool okOpt = StreamlineDlss.GetOptimalRenderSize(dm, (uint)contentW, (uint)contentH, out uint oW, out uint oH);
                            bool okRng = StreamlineDlss.GetRenderRange(dm, (uint)contentW, (uint)contentH, out uint miW, out uint miH, out uint maW, out uint maH);
                            Logger.Info?.Print(LogClass.Gpu,
                                $"DLSS range diag [{dm}] @ {contentW}x{contentH}: optimal={(okOpt ? $"{oW}x{oH}" : "n/a")} " +
                                $"range={(okRng ? $"[{miW}..{maW}]x[{miH}..{maH}]" : "n/a")}.");
                        }
                    }

                    float s0 = DlssIntegration.ResolutionScaleGetter?.Invoke() ?? 0f;
                    float factor = DlssIntegration.RenderScaleFactor(DlssIntegration.Mode);
                    if (s0 > 0f && factor > 0f && input.Height > 0 && DlssIntegration.ResolutionScaleSetter != null)
                    {
                        float targetH = contentH * factor;
                        bool sr_desarme = input.Width >= contentW || input.Height >= contentH;
                        bool sousOptimal = input.Height < targetH;

                        if (!sr_desarme)
                        {
                            Logger.Info?.Print(LogClass.Gpu, sousOptimal
                                ? $"DLSS auto-res: rien a faire -- interne {input.Width}x{input.Height} sous " +
                                  $"l'optimal {(int)targetH}px, mais on ne MONTE plus (l'echelle appartient au joueur ; " +
                                  $"canari Ghostbusters 05/08 : la montee demandait 8x plus de pixels a un jeu en 480p)."
                                : $"DLSS auto-res: rien a faire -- interne {input.Width}x{input.Height} deja au-dessus " +
                                  $"de l'optimal {(int)targetH}px et sous la sortie {contentW}x{contentH} " +
                                  $"(mieux que {DlssIntegration.Mode}, on ne degrade pas).");
                        }
                        else
                        {

                        float ideal = Math.Clamp(s0 * factor * contentH / input.Height, 0.1f, 8f);

                        float newScale = ideal;

                        while (newScale > AutoResQuantum &&
                               MathF.Ceiling(input.Height * (newScale / s0)) >= contentH)
                        {
                            newScale -= AutoResQuantum;
                        }

                        newScale = MathF.Max(newScale, AutoResQuantum);

                        if (!sr_desarme || newScale > s0)
                        {
                            float origine = DlssIntegration.UserBaseResScale * factor;
                            if (origine > newScale &&
                                MathF.Ceiling(input.Height * (origine / s0)) < contentH)
                            {
                                newScale = origine;
                            }
                        }

                        if (MathF.Abs(newScale - s0) > s0 * 0.02f)
                        {
                            DlssIntegration.ResolutionScaleSetter(newScale);
                            _autoResProbeCount = 0;
                            _autoResProbeTick = 0;
                            Logger.Info?.Print(LogClass.Gpu,
                                $"DLSS auto-res: {DlssIntegration.Mode} internal {input.Width}x{input.Height} @ scale {s0:0.000} " +
                                $"-> scale {newScale:0.000} (target ~{(int)(contentH * factor)}px for display {contentW}x{contentH}).");
                            return false;
                        }
                        }
                    }
                }
            }

            if (!isDlaa && (input.Width >= contentW || input.Height >= contentH))
            {
                return false;
            }

            StreamlineDlss.DlssMode mode;
            int dlssOutW;
            int dlssOutH;

            if (isDlaa)
            {
                mode = StreamlineDlss.DlssMode.Dlaa;
                dlssOutW = input.Width;
                dlssOutH = input.Height;
            }
            else if (modeB)
            {
                if (TryPickMode(input.Width, input.Height, 3200, 1800, out mode))
                {
                    dlssOutW = 3200;
                    dlssOutH = 1800;
                }
                else
                {
                    if (_align0)
                    {
                        Logger.Info?.Print(LogClass.Gpu,
                            $"DLSS-ALIGN1[B]: BAIL no preset fits native {input.Width}x{input.Height} -> 3200x1800; fallback to A.");
                    }

                    return false;
                }
            }
            else
            {

                if (_dlssPinOut && _pinnedOutW != 0)
                {
                    mode = _pinnedMode;
                    dlssOutW = _pinnedOutW;
                    dlssOutH = _pinnedOutH;
                }
                else if (TryPickMode(input.Width, input.Height, contentW, contentH, out mode))
                {
                    dlssOutW = contentW;
                    dlssOutH = contentH;

                    if (_dlssPinOut)
                    {
                        _pinnedMode = mode;
                        _pinnedOutW = dlssOutW;
                        _pinnedOutH = dlssOutH;
                    }
                }
                else if (_dlssPinOut)
                {
                    return false;
                }
                else if (!PickModeAndOutput(input.Width, input.Height, contentW, contentH, out mode, out dlssOutW, out dlssOutH))
                {
                    if (!_modeWarned)
                    {
                        _modeWarned = true;
                        Logger.Warning?.Print(LogClass.Gpu,
                            $"DLSS: no preset fits internal {input.Width}x{input.Height} -> {contentW}x{contentH} " +
                            "(a global NVIDIA DLSS override may be forcing a fixed mode); falling back to normal scaling.");
                    }

                    return false;
                }
            }

            if (_dupSkipEnabled)
            {
                if (!_dupArmedLogged)
                {
                    _dupArmedLogged = true;
                    Logger.Info?.Print(LogClass.Gpu, "DLSS: anti-doublons ARME (DUPSKIP=1).");
                }

                long dupSeq = System.Threading.Interlocked.Read(ref GAL.DlssCameraState.RenderedFrameSeq);

                if (dupSeq != 0 && _hasPrev && _output != null &&
                    _output.Width == dlssOutW && _output.Height == dlssOutH &&
                    dupSeq == _lastRenderedSeq &&
                    _dupConsecutive < DupSkipMaxConsecutive)
                {
                    _dupSkips++;
                    _dupConsecutive++;

                    long nowDup = Environment.TickCount64;

                    if (nowDup - _dupLogMs >= 5000)
                    {
                        _dupLogMs = nowDup;
                        Logger.Info?.Print(LogClass.Gpu,
                            $"DLSS anti-doublons: {_dupSkips} sauts / {_dupEvals} evaluates (fenetre ~5 s).");
                        _dupSkips = 0;
                        _dupEvals = 0;
                    }

                    _gd.HelperShader.BlitColor(
                        _gd,
                        cbs,
                        _output,
                        dst,
                        new Extents2D(0, 0, _output.Width, _output.Height),
                        dstRegion,
                        true,
                        true,
                        hdr,
                        paperWhite,
                        peak,
                        curve,
                        gamma,
                        blend,
                        whiten,
                        pqOutput);

                    return true;
                }

                _lastRenderedSeq = dupSeq;
                _dupConsecutive = 0;
                _dupEvals++;
            }

            EnsureResources(input, dlssOutW, dlssOutH, cbs);

            if (!StreamlineDlss.SetOptions(ViewportId, mode, (uint)dlssOutW, (uint)dlssOutH, hdr))
            {
                return false;
            }

            if (!_modeLogged)
            {
                _modeLogged = true;
                Logger.Info?.Print(LogClass.Gpu, $"DLSS: using mode {mode} for internal {input.Width}x{input.Height} -> {dlssOutW}x{dlssOutH} (content {contentW}x{contentH}).");
            }

            string sizeSig = $"internal {input.Width}x{input.Height} mode={mode} out={dlssOutW}x{dlssOutH}";
            if (sizeSig != _lastSizeSig)
            {
                if (_lastSizeSig != null)
                {
                    Logger.Info?.Print(LogClass.Gpu, $"DLSS size probe (CHANGED): {sizeSig}");
                }

                _lastSizeSig = sizeSig;
            }

            if (DlssIntegration.AutoResScale && _autoResProbeCount < 8 && (_autoResProbeTick++ % 60) == 0)
            {
                _autoResProbeCount++;
                double blitPct = 100.0 * (1.0 - Math.Min((double)dlssOutW / contentW, (double)dlssOutH / contentH));
                bool ideal = blitPct < 1.0;
                Logger.Info?.Print(LogClass.Gpu,
                    $"DLSS auto-res probe #{_autoResProbeCount}: internal {input.Width}x{input.Height} -> {dlssOutW}x{dlssOutH} (content {contentW}x{contentH}); blit={blitPct:0.00}% ideal={ideal}.");
            }

            ReadCounters(out uint unexplained, out uint motion);
            int total = input.Width * input.Height;
            float changedFraction = total != 0 ? (float)unexplained / total : 0f;
            float motionFraction = total != 0 ? (float)motion / total : 0f;

            bool changing = _wasChanging
                ? changedFraction >= SceneChangeLowFraction
                : changedFraction >= SceneChangeHighFraction;

            _framesSinceReset++;

            if (motionFraction >= _sceneCutMaxMotion)
            {
                _motionMeterWarm = true;
            }

            bool sceneCut = _hasPrev && changing && !_wasChanging
                && _motionMeterWarm
                && motionFraction < _sceneCutMaxMotion
                && _framesSinceReset >= ResetCooldownFrames
                && Environment.TickCount64 - _lastResetMs >= (long)_resetCooldownMs;
            _wasChanging = changing;

            if (modeB && _bNoReset && sceneCut)
            {
                _bSuppressedCuts++;
                sceneCut = false;
            }

            bool camWarp = _hasPrev && GAL.DlssCameraState.TeleportSeq != _lastTeleportSeq;
            _lastTeleportSeq = GAL.DlssCameraState.TeleportSeq;

            bool reset = !_hasPrev || sceneCut || camWarp || bReset;
            string resetReason = !reset ? "none" : (!_hasPrev ? "first-frame" : sceneCut ? "scene-cut" : camWarp ? "camera-warp" : bResetReason);
            if (reset)
            {
                _framesSinceReset = 0;
                _lastResetMs = Environment.TickCount64;
                _skyDriftEmaX = 0f;
                _skyDriftEmaY = 0f;
                Array.Clear(_zoneDriftEmaX);
                Array.Clear(_zoneDriftEmaY);
            }

            if (sceneCut)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"DLSS: history reset (changed={100f * unexplained / total:0.#}% motion={100f * motion / total:0.#}%) skyCooldown={_mvppSkyCutCooldown}f.");
            }

            Span<uint> zero = stackalloc uint[] { 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u };
            _gd.BufferManager.SetData<uint>(_sceneChangeBuffer, 0, zero);

            if (DlssIntegration.GpuTimingEnabled)
            {
                _gpuTimer ??= new DlssGpuTimer(_gd, _device);
                _gpuTimer.Collect();
                _gpuTimer.Begin(cbs.CommandBuffer);
            }

            if (_mvppBorderFix && _borderFixProgram != null && _mvppReprojMode > 0)
            {
                if (input.Info.Format == Format.R8G8B8A8Unorm)
                {
                    if (_borderFixTex == null || _borderFixTex.Width != input.Width || _borderFixTex.Height != input.Height)
                    {
                        RetireFgVisible(_borderFixTex);
                        _borderFixTex = _gd.CreateTexture(MakeInfo(input.Info, input.Width, input.Height, Format.R8G8B8A8Unorm, 4)) as TextureView;
                    }

                    Span<float> bp = stackalloc float[4];
                    bp[0] = input.Width;
                    bp[1] = input.Height;
                    bp[2] = _mvppBorderFixScan;
                    bp[3] = _mvppBorderFixThresh;

                    using ScopedTemporaryBuffer bbuf = _gd.BufferManager.ReserveOrCreate(_gd, cbs, bp.Length * sizeof(float));
                    bbuf.Holder.SetDataUnchecked(bbuf.Offset, bp);

                    _motionPipeline.SetCommandBuffer(cbs);
                    _motionPipeline.SetProgram(_borderFixProgram);
                    _motionPipeline.SetUniformBuffers([new BufferAssignment(2, bbuf.Range)]);
                    _motionPipeline.SetStorageBuffers([new BufferAssignment(0,
                        new BufferRange(_mvppStatsBuffer, MvppBorderFilledIndex * sizeof(uint), 2 * sizeof(uint), true))]);
                    _motionPipeline.SetTextureAndSampler(ShaderStage.Compute, 1, input, _samplerNearest);
                    _motionPipeline.SetImage(0, _borderFixTex.GetImageView());

                    _motionPipeline.DispatchCompute((input.Width + 15) / 16, (input.Height + 15) / 16, 1);
                    _motionPipeline.ComputeBarrier();
                    _motionPipeline.Finish();

                    input = _borderFixTex;
                }
                else if (!_borderFixSkipLogged)
                {
                    _borderFixSkipLogged = true;
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"MVPP BORDERFIX: format {input.Info.Format} non couvert (rgba8 seulement), passe inerte.");
                }
            }

            if (_hasPrev)
            {
                RunMotionPass(input, cbs);
                RunMotionFilterPass(input, cbs);
            }

            StreamlineDlss.DlssTexture inTex = Describe(input, cbs);
            StreamlineDlss.DlssTexture outTex = Describe(_output, cbs);

            bool depthMatches = depth != null && depth.Width == input.Width && depth.Height == input.Height;

            bool depthUsable = depthMatches ||
                (depth != null &&
                 depth.Width * 2 >= input.Width &&
                 Math.Abs((float)depth.Width / depth.Height - (float)input.Width / input.Height) < 0.02f);

            bool lockKeepsDepth = _bLockedThisFrame && !(depth != null && depth.Width == 2666 && depth.Height == 1500);
            if (depthUsable && !lockKeepsDepth)
            {
                (int, int, Format) heldKey = (depth.Width, depth.Height, depth.Info.Format);
                if (!_heldDepthCache.TryGetValue(heldKey, out TextureView held))
                {
                    held = _gd.CreateTexture(MakeInfo(depth.Info, depth.Width, depth.Height, depth.Info.Format, depth.Info.BytesPerPixel)) as TextureView;
                    _heldDepthCache[heldKey] = held;
                }

                CopyDepth(depth, held, cbs);
                _heldDepth = held;
                _hasHeldDepth = true;
            }

            TextureView depthSource = _forceDummyDepth ? _depth : (depthMatches ? depth : (_hasHeldDepth ? _heldDepth : _depth));

            int depthState = _forceDummyDepth ? 0 : (depthMatches ? 2 : (_hasHeldDepth ? 1 : 0));
            if (_depthState != depthState)
            {
                _depthState = depthState;
                Logger.Info?.Print(LogClass.Gpu,
                    $"DLSS depth -> {(depthState == 2 ? "REAL" : depthState == 1 ? "HELD (last good)" : "dummy")} " +
                    $"(input {input.Width}x{input.Height}, capture {(depth != null ? $"{depth.Width}x{depth.Height}" : "null")}).");
            }

            StreamlineDlss.DlssTexture depthTex = Describe(depthSource, cbs);

            TextureView mvppDepth = depthMatches ? depth : null;
            if (mvppDepth == null && depth != null &&
                depth.Width * 2 >= input.Width &&
                Math.Abs((float)depth.Width / depth.Height - (float)input.Width / input.Height) < 0.02f)
            {
                mvppDepth = depth;
            }
            mvppDepth ??= _hasHeldDepth ? _heldDepth : null;

            if (_mvppReprojMode > 0)
            {
                int mvppDepthSig = mvppDepth == null ? 0 : (mvppDepth.Width << 16) | mvppDepth.Height;
                if (mvppDepthSig != _mvppDepthSig)
                {
                    _mvppDepthSig = mvppDepthSig;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP depth: {(mvppDepth == null ? "none" : $"{mvppDepth.Width}x{mvppDepth.Height} active {mvppDepth.ActiveWidth}x{mvppDepth.ActiveHeight}")} " +
                        $"for input {input.Width}x{input.Height} (uv-sampled over the ACTIVE sub-rect).");
                }
            }

            bool mvppRan = false;
            bool mvppPairOk = _mvppVpDelay ? _mvppHasPrev2 : _mvppHasPair;

            if (mvppPairOk)
            {
                if (_mvppWarmupCount < int.MaxValue)
                {
                    _mvppWarmupCount++;
                }
            }
            else
            {
                _mvppWarmupCount = 0;
            }

            bool warmedUp = _mvppWarmup == 0 || _mvppWarmupCount >= _mvppWarmup;

            if (_mvppWarmup > 0 && mvppPairOk && _mvppWarmupCount == _mvppWarmup)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP WARMUP: {_mvppWarmup} images de camera stable ecoulees, la reprojection commence a ecrire.");
            }

            if (_mvppReprojMode > 0 && mvppPairOk && warmedUp && mvppDepth != null && _hasPrev)
            {
                if (_mvppDepthSnapA == null || _mvppDepthSnapA.Width != input.Width || _mvppDepthSnapA.Height != input.Height)
                {
                    RetireFgVisible(_mvppDepthSnapA);
                    RetireFgVisible(_mvppDepthSnapB);
                    RetireFgVisible(_mvppBiasMask);
                    _mvppDepthSnapA = _gd.CreateTexture(MakeInfo(input.Info, input.Width, input.Height, Format.R32Float, 4)) as TextureView;
                    _mvppDepthSnapB = _gd.CreateTexture(MakeInfo(input.Info, input.Width, input.Height, Format.R32Float, 4)) as TextureView;
                    _mvppBiasMask = _gd.CreateTexture(MakeInfo(input.Info, input.Width, input.Height, Format.R8Unorm, 1)) as TextureView;
                    _mvppHasPrevDepth = false;
                }

                mvppRan = RunMvppReprojectPass(mvppDepth, input, cbs);

                if (mvppRan)
                {
                    _mvppDepthSnapFlip = !_mvppDepthSnapFlip;
                    _mvppHasPrevDepth = true;
                }
            }

            if (_mvppReprojMode > 0)
            {
                if (_mvppHealthKnown && mvppRan == _mvppHealthRunning)
                {
                    _mvppHealthStreak = 0;
                }
                else if (++_mvppHealthStreak >= 60)
                {
                    _mvppHealthStreak = 0;
                    _mvppHealthKnown = true;
                    _mvppHealthRunning = mvppRan;

                    Logger.Info?.Print(LogClass.Gpu, mvppRan
                        ? "MV++ ACTIF : reprojection en cours (camera appariee, vecteurs livres a DLSS)."
                        : "MV++ DECROCHE : " + (!mvppPairOk ? "camera non appariee (aucune passe scene detectee)"
                            : mvppDepth == null ? "profondeur non appariee"
                            : !_hasPrev ? "pas d'image precedente"
                            : "passe refusee en interne") + " -- DLSS tourne sans nos vecteurs.");
                }
            }

            if (_mvppReprojMode > 0 && !mvppRan)
            {
                long pauseNow = Environment.TickCount64;
                if (pauseNow - _mvppPauseLogMs >= 5000 && (_mvppVerbose || !_mvppPauseLoggedOnce))
                {
                    _mvppPauseLogMs = pauseNow;
                    _mvppPauseLoggedOnce = true;
                    string pauseCause = !mvppPairOk ? "caméra non appariée (aucune passe scène détectée)"
                        : mvppDepth == null ? "depth non appariée"
                        : !_hasPrev ? "pas d'image précédente"
                        : "passe refusée en interne";
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MV++ en pause : {pauseCause} " +
                        $"(pairOk={(mvppPairOk ? 1 : 0)}, depth={(mvppDepth != null ? "ok" : "aucune")}, prev={(_hasPrev ? 1 : 0)}).");
                }
            }

            TextureView mvSource = DlssIntegration.MvFilterEnabled ? _motionFiltered : _motion;
            if (_mvppReprojMode >= 2 && mvppRan && !_depthGuardMuted)
            {
                mvSource = _mvppMotion;
            }

            _lastMvSource = mvSource;

            if (_mvppDump && mvppRan && _mvppDumpCount < 10 &&
                Environment.TickCount64 >= _mvppDumpNotBeforeMs &&
                Environment.TickCount64 - _mvppDumpLastMs >=
                    (_mvppDumpCount % _mvppDumpBurst != 0 ? 0L : (long)_mvppDumpIntervalMs) &&
                (_mvppDumpCount % _mvppDumpBurst != 0
                 || (_mvppDumpOnHold > 0
                        ? (_mvppFrozenPair && _mvppHoldRun == 1 &&
                           _mvppStreakBeforeFreeze >= _mvppDumpOnHold)
                        : (_mvppDumpOnMove == 0 || _mvppMoveStreak >= _mvppDumpOnMove))))
            {
                _mvppDumpLastMs = Environment.TickCount64;

                try
                {
                    string mdir = System.IO.Path.Combine(AppContext.BaseDirectory, "mvpp-dump");
                    System.IO.Directory.CreateDirectory(mdir);
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(mdir, $"vp_{_mvppDumpCount:00}.txt"),
                        $"present={_presentSerial}\ncurrStamp={_mvppVpCurrStamp}\nprevStamp={_mvppVpPrevStamp}\n" +
                        $"prev2Stamp={_mvppVpPrev2Stamp}\necart={_mvppVpCurrStamp - _mvppVpPrevStamp}\n" +
                        $"vpDelay={(_mvppVpDelay ? 1 : 0)}\nnoCamSinceLastLog={_mvppNoCamPresents}\n" +
                        $"frozenPair={(_mvppFrozenPair ? 1 : 0)}\nholdRun={_mvppHoldRun}\n" +
                        $"streakBeforeFreeze={_mvppStreakBeforeFreeze}\nmoveStreak={_mvppMoveStreak}\n" +
                        $"fifoDepth={_mvppFifoDepth}\nfifoPushes={GAL.DlssCameraState.StatPushes}\n" +
                        $"fifoFresh={GAL.DlssCameraState.StatFresh}\nfifoHolds={GAL.DlssCameraState.StatHolds}\n" +
                        $"fifoDrops={GAL.DlssCameraState.StatDrops}\n" +
                        $"vpCurr={_mvppVpCurr}\nvpPrev={_mvppVpPrev}\nvpPrev2={_mvppVpPrev2}\n");
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"MVPP vpstamp dump FAILED: {ex.Message}");
                }

                DumpField(_mvppMotion, "mv");
                if (mvppDepth != null)
                {
                    DumpField(mvppDepth, "depth");
                }
                if (_output != null)
                {
                    DumpField(_output, "out");
                }
                DumpField(input, "in");

                if (_prevColor != null)
                {
                    DumpField(_prevColor, "prev");
                }

                if ((_mvppDynMask || _mvppDynSonde) && _mvppBiasMask != null)
                {
                    DumpField(_mvppBiasMask, "dynmask");
                }

                _mvppDumpCount++;
            }

            if (_velDebug && _velDebugProgram != null)
            {
                RunVelDebugView(mvSource, cbs);

                _gd.HelperShader.BlitColor(
                    _gd,
                    cbs,
                    _velDebugTex,
                    dst,
                    new Extents2D(0, 0, _velDebugTex.Width, _velDebugTex.Height),
                    dstRegion,
                    true,
                    true,
                    hdr,
                    paperWhite,
                    peak,
                    curve,
                    gamma,
                    blend,
                    whiten,
                    pqOutput);

                CopyToPrev(input, cbs);
                _hasPrev = true;

                return true;
            }

            StreamlineDlss.DlssTexture mvTex = Describe(mvSource, cbs);

            float evalJitterX = declJitterX * JitterSignX;
            float evalJitterY = declJitterY * JitterSignY;

            if (_jitterval)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"JITTERVAL-DLSS frame={DlssJitterState.PresentFrameId} inW={input.Width} inH={input.Height} " +
                    $"outW={dlssOutW} outH={dlssOutH} depthW={depthTex.Width} depthH={depthTex.Height} " +
                    $"motionW={mvTex.Width} motionH={mvTex.Height} presentX={frameJitterX:0.0000} presentY={frameJitterY:0.0000} " +
                    $"signX={JitterSignX:0.0} signY={JitterSignY:0.0} declPxX={evalJitterX:0.0000} declPxY={evalJitterY:0.0000}");
            }

            if (_align0)
            {
                long sig = ((long)input.Width << 40) ^ ((long)depthSource.Width << 26)
                         ^ ((long)(mvppDepth?.Width ?? 0) << 12) ^ ((long)(depthMatches ? 1 : 0) << 1)
                         ^ (object.ReferenceEquals(mvppDepth, depthSource) ? 1L : 0L);
                long now = System.Environment.TickCount64;
                if (sig != _align0Sig || now - _align0Ms >= 2000)
                {
                    _align0Sig = sig;
                    _align0Ms = now;
                    static int Hc(object o) => o == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
                    bool mvDepthObj = object.ReferenceEquals(mvppDepth, depthSource);
                    bool mvDepthDim = mvppDepth != null && mvppDepth.Width == depthSource.Width && mvppDepth.Height == depthSource.Height;
                    bool colorDepthSameFrame = DlssJitterState.PresentFrameId == DlssJitterState.SceneDepthFrameId;
                    TextureView natCand = MvppColorSnapshot.SceneColorBackend as TextureView;
                    string natStr = natCand == null ? "none"
                        : $"{natCand.Width}x{natCand.Height} {natCand.Info.Format} colorFrame={DlssJitterState.SceneColorFrameId} id={Hc(natCand)}";
                    bool natVsDepthSameFrame = natCand != null && DlssJitterState.SceneColorFrameId == DlssJitterState.SceneDepthFrameId;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"DLSS-ALIGN0[{_abMode}]: COLOR src={(modeB ? "native" : "presented")} {input.Width}x{input.Height} {input.Info.Format} id={Hc(input)} frame={DlssJitterState.PresentFrameId} | " +
                        $"DEPTH {depthSource.Width}x{depthSource.Height} {depthSource.Info.Format} match={depthMatches} id={Hc(depthSource)} frame={DlssJitterState.SceneDepthFrameId} | " +
                        $"MV {mvSource.Width}x{mvSource.Height} {mvSource.Info.Format} id={Hc(mvSource)} mvecScale=1/{mvSource.Width} mvDepth={(mvppDepth == null ? "null" : $"{mvppDepth.Width}x{mvppDepth.Height}")} mvDepthId={Hc(mvppDepth)} | " +
                        $"JITTER gen=({DlssJitterState.OffsetX:0.000},{DlssJitterState.OffsetY:0.000}) decl=({evalJitterX:0.000},{evalJitterY:0.000}) inputW={DlssJitterState.InputWidth} | " +
                        $"DLSS mode={(isDlaa ? "DLAA" : "SR")} in={input.Width}x{input.Height} out={dlssOutW}x{dlssOutH} hdr={hdr} | " +
                        $"MV_DEPTH_MATCH={mvDepthObj} dimMatch={mvDepthDim} COLOR_DEPTH_SAMEFRAME={colorDepthSameFrame} | " +
                        $"NATIVE_CANDIDATE={natStr} nativeVsDepthSameFrame={natVsDepthSameFrame}");
                }
            }

            bool useBiasMask = _mvppReprojMode >= 2 && mvppRan && !_depthGuardMuted && _mvppDynMask && _mvppBias && _mvppBiasMask != null;
            StreamlineDlss.DlssTexture biasTex = useBiasMask ? Describe(_mvppBiasMask, cbs) : default;

            bool ok = StreamlineDlss.Evaluate(
                (IntPtr)cbs.CommandBuffer.Handle,
                ViewportId,
                _frame++,
                reset,
                evalJitterX,
                evalJitterY,
                in inTex,
                in outTex,
                in depthTex,
                in mvTex,
                in biasTex,
                useBiasMask);

            if (_align0)
            {
                long sig1 = (modeB ? 1L : 0L) | (ok ? 2L : 0L) | (reset ? 4L : 0L) | (_bLockedThisFrame ? 8L : 0L) | ((long)input.Width << 8);
                long now1 = System.Environment.TickCount64;
                if (sig1 != _align1Sig || now1 - _align1Ms >= 2000)
                {
                    _align1Sig = sig1;
                    _align1Ms = now1;
                    long snapAge = _bHasNativeHeld ? DlssJitterState.PresentFrameId - _bNativeHeldFrame : -1;
                    string snapUsed = !modeB ? "none" : (_bLockedThisFrame ? "held" : "live");
                    Logger.Info?.Print(LogClass.Gpu,
                        $"DLSS-ALIGN1[{_abMode}]: modeB={modeB} color={(modeB ? "native" : "presented")} {input.Width}x{input.Height} {input.Info.Format} " +
                        $"depth={depthSource.Width}x{depthSource.Height} mv={mvSource.Width}x{mvSource.Height} mvecScale=1/{mvSource.Width} " +
                        $"inputW={DlssJitterState.InputWidth} nativeResolutionDetected={_bLiveNatW}x{_bLiveNatH} BLockedResolution={_bLockedThisFrame} " +
                        $"snapshot={snapUsed} snapFrame={_bNativeHeldFrame} snapAgeFrames={snapAge} " +
                        $"reset={reset} resetReason={resetReason} bSuppressedCuts={_bSuppressedCuts} evaluate={(ok ? "OK" : "FAIL")}");
                }
            }

            if (!ok)
            {
                return false;
            }

            if (_gd.FgHooks != null)
            {
                StreamlineDlss.DlssTexture fgMvTex = mvTex;
                if (_mvppFgRawMv && _mvppReprojMode >= 2 && mvppRan && !_depthGuardMuted)
                {
                    fgMvTex = Describe(_mvppMotionRaw, cbs);
                }

                StreamlineDlss.DlssTexture hudlessTex = default;

                if (GAL.MvppColorSnapshot.SceneColorHost is TextureView hudlessView)
                {
                    hudlessTex = Describe(hudlessView, cbs);
                }

                StreamlineFrameGen.OnFrameEvaluated(
                    (IntPtr)cbs.CommandBuffer.Handle,
                    ViewportId,
                    StreamlineDlss.LastFrameToken,
                    in depthTex,
                    in fgMvTex,
                    in hudlessTex);
            }

            _gpuTimer?.End(cbs.CommandBuffer);

            if (DlssIntegration.OutputBarrierEnabled)
            {
                BarrierOutput(cbs);
            }

            TextureView presentSrc = _output;

            if (DlssSharpenPass.Enabled)
            {
                _sharpenPass ??= new DlssSharpenPass(_gd, _device);
                presentSrc = _sharpenPass.Run(_output, cbs) ?? _output;
            }

            _gd.HelperShader.BlitColor(
                _gd,
                cbs,
                presentSrc,
                dst,
                new Extents2D(0, 0, presentSrc.Width, presentSrc.Height),
                dstRegion,
                true,
                true,
                hdr,
                paperWhite,
                peak,
                curve,
                gamma,
                blend,
                whiten,
                pqOutput);

            CopyToPrev(input, cbs);
            _hasPrev = true;

            return true;
        }

        private bool RunMvppReprojectPass(TextureView depth, TextureView input, CommandBufferScoped cbs)
        {
            if (_mvppMotion == null || _mvppMotion.Width != input.Width || _mvppMotion.Height != input.Height)
            {
                RetireFgVisible(_mvppMotion);
                RetireFgVisible(_mvppMotionRaw);
                _mvppMotion = _gd.CreateTexture(MakeInfo(input.Info, input.Width, input.Height, Format.R16G16Float, 4)) as TextureView;
                _mvppMotionRaw = _gd.CreateTexture(MakeInfo(input.Info, input.Width, input.Height, Format.R16G16Float, 4)) as TextureView;
            }

            System.Numerics.Matrix4x4 vpCurr = _mvppVpDelay ? _mvppVpPrev : _mvppVpCurr;
            System.Numerics.Matrix4x4 vpPrev = _mvppVpDelay ? _mvppVpPrev2 : _mvppVpPrev;

            if (!System.Numerics.Matrix4x4.Invert(vpCurr, out System.Numerics.Matrix4x4 invVpCurr))
            {
                return false;
            }

            System.Numerics.Matrix4x4 invT = System.Numerics.Matrix4x4.Transpose(invVpCurr);
            System.Numerics.Matrix4x4 prevT = System.Numerics.Matrix4x4.Transpose(vpPrev);

            float vpProj = VpProjJump(in vpCurr, in vpPrev);

            _projJumpRing[_projJumpRingIdx] = vpProj;
            _projJumpRingIdx = (_projJumpRingIdx + 1) & 7;

            bool preCalm = true;
            for (int i = 1; i <= ProjCutCalmWindow; i++)
            {
                if (_projJumpRing[(_projJumpRingIdx - 1 - i) & 7] > ProjCutCalmEps)
                {
                    preCalm = false;
                    break;
                }
            }

            if (_mvppSkyCutCooldown > 0 && vpProj > ProjCutMagnitude && preCalm)
            {
                if (MvppDev.Enabled && _projQuietFrames == 0)
                {
                    System.Text.StringBuilder serie = new();
                    for (int i = 0; i < 8; i++)
                    {
                        serie.Append($"{_projJumpRing[(_projJumpRingIdx + i) & 7]:0.000} ");
                    }

                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP dome quiet: projJump={vpProj:0.000} -> {2 * _mvppSkyCutCooldown}f. serie8={serie.ToString().TrimEnd()}");
                }
                else if (MvppDev.Enabled && _projQuietFrames > 0)
                {
                    _projRearms++;
                }

                _projQuietFrames = 2 * _mvppSkyCutCooldown;
            }
            else if (MvppDev.Enabled && _mvppSkyCutCooldown > 0 && vpProj > ProjJumpThreshold)
            {
                _projZoomSkips++;
            }
            else if (_projQuietFrames > 0)
            {
                _projQuietFrames--;
            }

            if (MvppDev.Enabled)
            {
                float vpJump = VpPairJump(in vpCurr, in vpPrev);
                _vpJumpN++;
                _vpJumpSum += vpJump;
                _vpProjSum += vpProj;
                if (vpJump > _vpJumpMax)
                {
                    _vpJumpMax = vpJump;
                }

                if (vpProj > _vpProjMax)
                {
                    _vpProjMax = vpProj;
                }
            }

            long nowMs = Environment.TickCount64;
            if ((MvppDev.Enabled || _mvppSkyDrift || _mvppSkyGrid || _mvppDynSonde || _mvppDepthSonde || _mvppDepthGuard) && nowMs - _mvppStatsLogMs >= 1000)
            {
                _mvppStatsLogMs = nowMs;

                using PinnedSpan<byte> data = _gd.BufferManager.GetData(_mvppStatsBuffer, 0, MvppStatsCount * sizeof(uint));
                ReadOnlySpan<uint> s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(data.Get());

                if (MvppDev.Enabled && s[0] > 0)
                {
                    uint vx = s[2] + s[3];
                    uint vy = s[4] + s[5];
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP reproject vs flow: {s[0]} samples, sign x same {(vx > 0 ? 100f * s[2] / vx : 0f):0.0}% ({vx} votes), y same {(vy > 0 ? 100f * s[4] / vy : 0f):0.0}% ({vy} votes), " +
                        $"mean |flow|=({s[6] / 16f / s[0]:0.00},{s[7] / 16f / s[0]:0.00}) px, |reproj|=({s[8] / 16f / s[0]:0.00},{s[9] / 16f / s[0]:0.00}) px, sky {s[1]}.");
                }

                if (s[1] > 0)
                {
                    float meanFX = unchecked((int)s[22]) / 16f / s[1];
                    float meanFY = unchecked((int)s[23]) / 16f / s[1];

                    bool calm = !_mvppSkyCalm ||
                                (MathF.Abs(meanFX) <= SkyCalmMaxPxPerFrame &&
                                 MathF.Abs(meanFY) <= SkyCalmMaxPxPerFrame);

                    if (_mvppSkyDrift && calm && _framesSinceReset >= _mvppSkyCutCooldown && _projQuietFrames == 0)
                    {
                        _skyDriftEmaX += 0.3f * (meanFX - _skyDriftEmaX);
                        _skyDriftEmaY += 0.3f * (meanFY - _skyDriftEmaY);

                        if (_mvppSkyGrid)
                        {
                            if (_skyBand)
                            {
                                for (int b = 0; b < 4; b++)
                                {
                                    uint bc = 0;
                                    long bx = 0, by = 0;

                                    for (int k = 0; k < 8; k++)
                                    {
                                        int idx = b * 8 + k;
                                        bc += s[92 + idx];
                                        bx += unchecked((int)s[28 + idx]);
                                        by += unchecked((int)s[60 + idx]);
                                    }

                                    _bandCount[b] = bc;
                                    _bandX[b] = bc > 0 ? bx / 16f / bc - meanFX : 0f;
                                    _bandY[b] = bc > 0 ? by / 16f / bc - meanFY : 0f;
                                }
                            }

                            for (int i = 0; i < 32; i++)
                            {
                                uint zc = s[92 + i];
                                float tx = 0f, ty = 0f;
                                if (zc >= _skyGridMinSamples)
                                {
                                    tx = Math.Clamp(unchecked((int)s[28 + i]) / 16f / zc - meanFX, -_skyGridClamp, _skyGridClamp);
                                    ty = Math.Clamp(unchecked((int)s[60 + i]) / 16f / zc - meanFY, -_skyGridClamp, _skyGridClamp);
                                }
                                else if (_skyBand && _bandCount[i / 8] >= _skyGridMinSamples)
                                {
                                    tx = Math.Clamp(_bandX[i / 8], -_skyGridClamp, _skyGridClamp);
                                    ty = Math.Clamp(_bandY[i / 8], -_skyGridClamp, _skyGridClamp);
                                }

                                _zoneDriftEmaX[i] += 0.25f * (tx - _zoneDriftEmaX[i]);
                                _zoneDriftEmaY[i] += 0.25f * (ty - _zoneDriftEmaY[i]);
                            }
                        }
                    }

                    if (MvppDev.Enabled)
                    {
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP SKYDRIFT sonde: sky={s[1]} d1={100f * s[24] / s[1]:0.0}% skyFlow=({meanFX:0.0000},{meanFY:0.0000}) px/frame (signé) " +
                            $"skyMV écrit=({s[20] / 1024f / s[1]:0.0000},{s[21] / 1024f / s[1]:0.0000}) px (x1024, cap 4) " +
                            $"driftEMA=({_skyDriftEmaX:0.0000},{_skyDriftEmaY:0.0000}) driftVuShader={BitConverter.UInt32BitsToSingle(s[25]):0.0000}.");
                    }
                }

                if (MvppDev.Enabled && s[1] > 0)
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP SKYCOH sonde: sky={s[1]} d2={s[26] / 16f / s[1]:0.0000} d16={s[27] / 16f / s[1]:0.0000} px.");
                }

                if (MvppDev.Enabled)
                {
                    int zValid = 0;
                    float zMinX = float.MaxValue, zMaxX = float.MinValue;
                    float zMinY = float.MaxValue, zMaxY = float.MinValue;
                    Span<float> rowX = stackalloc float[4];
                    Span<float> rowY = stackalloc float[4];
                    Span<uint> rowN = stackalloc uint[4];
                    for (int i = 0; i < 32; i++)
                    {
                        uint zc = s[92 + i];
                        int r = i / 8;
                        rowX[r] += unchecked((int)s[28 + i]) / 16f;
                        rowY[r] += unchecked((int)s[60 + i]) / 16f;
                        rowN[r] += zc;
                        if (zc >= 200)
                        {
                            zValid++;
                            float zmx = unchecked((int)s[28 + i]) / 16f / zc;
                            float zmy = unchecked((int)s[60 + i]) / 16f / zc;
                            zMinX = MathF.Min(zMinX, zmx);
                            zMaxX = MathF.Max(zMaxX, zmx);
                            zMinY = MathF.Min(zMinY, zmy);
                            zMaxY = MathF.Max(zMaxY, zmy);
                        }
                    }

                    if (zValid >= 2)
                    {
                        string rows = "";
                        for (int r = 0; r < 4; r++)
                        {
                            rows += rowN[r] > 0
                                ? $" y{r}=({rowX[r] / rowN[r]:0.0000},{rowY[r] / rowN[r]:0.0000})"
                                : $" y{r}=(-)";
                        }

                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP SKYGRID sonde: zones={zValid}/32 spreadX={zMaxX - zMinX:0.0000} spreadY={zMaxY - zMinY:0.0000} px |{rows}.");
                    }
                }

                if (_mvppDynSonde && s[16] > 0 && s[17] > 0)
                {
                    Span<uint> dRow = stackalloc uint[4];
                    uint dTot = 0;
                    uint dMaxC = 0;
                    int dMaxZi = 0;
                    for (int i = 0; i < 32; i++)
                    {
                        uint c = s[194 + i];
                        dRow[i / 8] += c;
                        dTot += c;
                        if (c > dMaxC)
                        {
                            dMaxC = c;
                            dMaxZi = i;
                        }
                    }

                    string dRows = dTot > 0
                        ? $"y0={100f * dRow[0] / dTot:0}% y1={100f * dRow[1] / dTot:0}% y2={100f * dRow[2] / dTot:0}% y3={100f * dRow[3] / dTot:0}% max=z{dMaxZi}({100f * dMaxC / dTot:0}%)"
                        : "-";
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP DYNSONDE: sampled={s[16]} dyn={100f * s[17] / s[16]:0.0}% flowAlive={100f * s[192] / s[17]:0.0}% " +
                        $"|flow|=({s[188] / 16f / s[17]:0.00},{s[189] / 16f / s[17]:0.00}) |flow-cam|=({s[190] / 16f / s[17]:0.00},{s[191] / 16f / s[17]:0.00}) px | {dRows}.");
                }

                if (_mvppDepthSonde && s[230] > 0)
                {
                    uint gridN = s[230];
                    uint farN = s[226];
                    uint midN = s[228];
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP DEPTHSONDE: grid={gridN} sky={100f * s[1] / gridN:0.0}% ok={100f * s[0] / gridN:0.0}% " +
                        $"degen={100f * s[231] / gridN:0.0}% behind={100f * s[232] / gridN:0.0}% | " +
                        $"far={(s[0] > 0 ? 100f * farN / s[0] : 0f):0.0}% farRes={(farN > 0 ? s[227] / 4f / farN : 0f):0.00} px " +
                        $"mid={(s[0] > 0 ? 100f * midN / s[0] : 0f):0.0}% midRes={(midN > 0 ? s[229] / 4f / midN : 0f):0.00} px.");
                }

                if (_mvppChecker && s[233] > 0)
                {
                    uint cN = s[233];
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP CHECKER: farBand={cN} split={100f * s[234] / cN:0.0}% diag={100f * s[235] / cN:0.0}% " +
                        $"sky={100f * s[236] / cN:0.0}% (diag = signature damier stricte ; split seul = non concluant).");
                }

                if (_mvppDepthGuard && s[230] > 0 && s[0] > 0)
                {
                    uint gFarN = s[226];
                    uint gMidN = s[228];
                    if (gFarN >= s[0] * DepthGuardMinBinPct && gMidN >= s[0] * DepthGuardMinBinPct)
                    {
                        float gFarRes = s[227] / 4f / gFarN;
                        float gMidRes = s[229] / 4f / gMidN;
                        float lo = Math.Min(gFarRes, gMidRes);
                        float hi = Math.Max(gFarRes, gMidRes);
                        bool bad = lo > _depthGuardPx && hi > 0f && (lo / hi) > _depthGuardRatio;

                        if (bad)
                        {
                            _depthGuardGoodStreak = 0;
                            if (++_depthGuardBadStreak >= DepthGuardTripSeconds && !_depthGuardMuted)
                            {
                                _depthGuardMuted = true;
                                Logger.Warning?.Print(LogClass.Gpu,
                                    $"MVPP DEPTHGUARD: depth dialect unsound (minRes={lo:0.00} px, ratio={lo / hi:0.00}, " +
                                    $"{DepthGuardTripSeconds}s) -> MV family muted, pre-mode-2 fallback.");
                            }
                        }
                        else
                        {
                            _depthGuardBadStreak = 0;
                            if (++_depthGuardGoodStreak >= DepthGuardRecoverSeconds && _depthGuardMuted)
                            {
                                _depthGuardMuted = false;
                                Logger.Warning?.Print(LogClass.Gpu,
                                    $"MVPP DEPTHGUARD: depth readings healthy again ({DepthGuardRecoverSeconds}s) -> MV family restored.");
                            }
                        }
                    }
                }

                if (MvppDev.Enabled && _mvppBorderFix)
                {
                    Logger.Info?.Print(LogClass.Gpu, $"MVPP BORDERFIX: filled={s[MvppBorderFilledIndex]}/s.");
                }

                if (MvppDev.Enabled && _vpJumpN > 0)
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP VPJUMP sonde: frames={_vpJumpN} mean={_vpJumpSum / _vpJumpN:0.00000} max={_vpJumpMax:0.00000} " +
                        $"projMean={_vpProjSum / _vpJumpN:0.00000} projMax={_vpProjMax:0.00000} rearms={_projRearms} zoomskip={_projZoomSkips}.");
                    _projRearms = 0;
                    _projZoomSkips = 0;
                    _vpJumpN = 0;
                    _vpJumpSum = 0f;
                    _vpJumpMax = 0f;
                    _vpProjSum = 0f;
                    _vpProjMax = 0f;
                }

                Span<uint> statsReset = stackalloc uint[MvppStatsCount];
                if (_mvppSkyGrid)
                {
                    for (int i = 0; i < 32; i++)
                    {
                        statsReset[124 + i] = BitConverter.SingleToUInt32Bits(_zoneDriftEmaX[i]);
                        statsReset[156 + i] = BitConverter.SingleToUInt32Bits(_zoneDriftEmaY[i]);
                    }
                }

                _gd.BufferManager.SetData<uint>(_mvppStatsBuffer, 0, statsReset);

                if (MvppDev.Enabled)
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP center: d={BitConverter.UInt32BitsToSingle(s[10]):0.#######}, zNdc={BitConverter.UInt32BitsToSingle(s[11]):0.####}, " +
                        $"worldW={BitConverter.UInt32BitsToSingle(s[12]):0.#####}, worldY={BitConverter.UInt32BitsToSingle(s[13]):0.##}, " +
                        $"mv=({BitConverter.UInt32BitsToSingle(s[14]):0.###},{BitConverter.UInt32BitsToSingle(s[15]):0.###}) px, " +
                        $"dyn masked {(s[16] > 0 ? 100f * s[17] / s[16] : 0f):0.0}%, prevD={BitConverter.UInt32BitsToSingle(s[18]):0.#######}.");
                }

                if (_mvppVelCompose)
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP compose: on={_mvppComposeOn} stale={_mvppComposeStale} (of {_mvppComposeOn + _mvppComposeStale} frames).");
                    _mvppComposeOn = 0;
                    _mvppComposeStale = 0;
                }
            }

            bool zMinusOne = _mvppZglEnv == "1" || (_mvppZglEnv != "0" && DlssCameraState.GuestDepthMinusOneToOne);
            if (!_mvppZLogged || zMinusOne != _mvppZMinusOne)
            {
                _mvppZLogged = true;
                _mvppZMinusOne = zMinusOne;
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP z convention: {(zMinusOne ? "MinusOneToOne (GL-style)" : "ZeroToOne")} " +
                    $"({(_mvppZglEnv != null ? "forced" : "auto from guest viewport")}).");

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP reproj tunables: deadband {_mvppDeadLo}/{_mvppDeadHi} px, depthTap={(_mvppDepthLinear ? "LINEAR" : "nearest")}, " +
                    $"farRot={_mvppFarRot}m, edge={_mvppEdgeMode}, compose={_mvppVelCompose}, vpDelay={_mvppVpDelay}, dynMask={_mvppDynMask}.");
            }

            TextureView mvppCov = _mvppVelCompose ? MvppVelState.CoverageBackend as TextureView : null;
            bool mvppCompose = false;
            if (mvppCov != null)
            {
                mvppCompose = MvppVelState.CoverageFresh;
                MvppVelState.CoverageFresh = false;
                if (mvppCompose) { _mvppComposeOn++; } else { _mvppComposeStale++; }
            }
            _mvppComposedThisFrame = mvppCompose;

            Span<float> p = stackalloc float[54];
            WriteMatrix(p, 0, in invT);
            WriteMatrix(p, 16, in prevT);
            p[32] = input.Width;
            p[33] = input.Height;
            p[34] = MvppMaxMotion;
            p[35] = _mvppReprojMode == 1 ? 1f : 0f;
            p[36] = _mvppFlipY ? 1f : 0f;
            p[37] = zMinusOne ? 1f : 0f;
            p[38] = _mvppZeroMv ? 1f : 0f;
            p[39] = _mvppDeadLo;
            p[40] = _mvppDeadHi;
            p[41] = !_mvppHasPrevDepth ? 0f : (_mvppDynMask ? 1f : (_mvppDynSonde ? 2f : 0f));
            p[42] = mvppCompose ? 1f : 0f;
            bool skyCut = _mvppSkyCutCooldown > 0
                && (_framesSinceReset < _mvppSkyCutCooldown || _projQuietFrames > 0);
            p[43] = skyCut ? 0f : _mvppFarRot;
            p[44] = (skyCut && !_mvppEdgeAlways) ? 0f : _mvppEdgeMode;

            p[45] = depth.ActiveWidth > 0 ? (float)depth.ActiveWidth / depth.Width : 1f;
            p[46] = depth.ActiveHeight > 0 ? (float)depth.ActiveHeight / depth.Height : 1f;
            p[47] = _mvppSkyRot && !skyCut ? 1f : 0f;
            p[48] = _mvppSkyFlow && !skyCut ? 1f : 0f;
            bool statsThisFrame = _mvppStatsStride <= 1 || (_framesSinceReset % _mvppStatsStride) == 0;
            p[49] = (MvppDev.Enabled || _mvppSkyDrift || _mvppDynSonde || _mvppDepthSonde || _mvppDepthGuard || _mvppChecker) && statsThisFrame ? 1f : 0f;
            p[50] = _mvppSkyDrift && !skyCut ? _skyDriftEmaX : 0f;
            p[51] = _mvppSkyDrift && !skyCut ? _skyDriftEmaY : 0f;

            p[52] = _skyDepth;

            long vpSpan = _mvppVpCurrStamp - _mvppVpPrevStamp;
            p[53] = _mvppIntervalFix && vpSpan == 2 ? 0.5f : 1.0f;

            using ScopedTemporaryBuffer buffer = _gd.BufferManager.ReserveOrCreate(_gd, cbs, p.Length * sizeof(float));
            buffer.Holder.SetDataUnchecked(buffer.Offset, p);

            _motionPipeline.SetCommandBuffer(cbs);
            _motionPipeline.SetProgram(_mvppReprojProgram);
            TextureView snapRead = _mvppDepthSnapFlip ? _mvppDepthSnapB : _mvppDepthSnapA;
            TextureView snapWrite = _mvppDepthSnapFlip ? _mvppDepthSnapA : _mvppDepthSnapB;

            _motionPipeline.SetTextureAndSampler(ShaderStage.Compute, 1, depth, _mvppDepthLinear ? _sampler : _samplerNearest);
            _motionPipeline.SetTextureAndSampler(ShaderStage.Compute, 3, _motion, _sampler);
            _motionPipeline.SetTextureAndSampler(ShaderStage.Compute, 4, _mvppHasPrevDepth ? snapRead : depth, _samplerNearest);
            _motionPipeline.SetTextureAndSampler(ShaderStage.Compute, 5, mvppCov ?? _motion, _samplerNearest);
            _motionPipeline.SetUniformBuffers([new BufferAssignment(2, buffer.Range)]);
            _motionPipeline.SetStorageBuffers([new BufferAssignment(0, new BufferRange(_mvppStatsBuffer, 0, MvppStatsCount * sizeof(uint), true))]);
            _motionPipeline.SetImage(0, _mvppMotion.GetImageView());
            _motionPipeline.SetImage(1, snapWrite.GetImageView());
            _motionPipeline.SetImage(2, _mvppBiasMask.GetImageView());
            _motionPipeline.SetImage(3, _mvppMotionRaw.GetImageView());

            bool timeReproj = DlssIntegration.GpuTimingEnabled && MvppDev.Enabled;
            if (timeReproj)
            {
                _reprojTimer ??= new DlssGpuTimer(_gd, _device, "MVPP-REPROJ");
                _reprojTimer.Collect();
                _reprojTimer.Begin(cbs.CommandBuffer);
            }

            _motionPipeline.DispatchCompute((input.Width + 15) / 16, (input.Height + 15) / 16, 1);

            if (timeReproj)
            {
                _reprojTimer.End(cbs.CommandBuffer);
            }

            _motionPipeline.ComputeBarrier();
            _motionPipeline.Finish();

            return true;
        }

        private bool RunInjectCutoutMask(TextureView scene, CommandBufferScoped cbs)
        {
            if (!MvppSurviveMaskState.HasMask || _cutoutMaskProgram == null)
            {
                return false;
            }

            if (_injectCutoutMask == null || _injectCutoutMask.Width != scene.Width || _injectCutoutMask.Height != scene.Height)
            {
                RetireFgVisible(_injectCutoutMask);
                _injectCutoutMask = _gd.CreateTexture(MakeInfo(scene.Info, scene.Width, scene.Height, Format.R8Unorm, 1)) as TextureView;
            }

            MemoryBarrier preBarrier = new()
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
            };

            unsafe
            {
                _gd.Api.CmdPipelineBarrier(
                    cbs.CommandBuffer,
                    PipelineStageFlags.FragmentShaderBit,
                    PipelineStageFlags.ComputeShaderBit,
                    0, 1, in preBarrier, 0, null, 0, null);
            }

            float[] p = new float[4];
            p[0] = scene.Width;
            p[1] = scene.Height;

            using ScopedTemporaryBuffer buffer = _gd.BufferManager.ReserveOrCreate(_gd, cbs, p.Length * sizeof(float));
            buffer.Holder.SetDataUnchecked(buffer.Offset, p);

            _motionPipeline.SetCommandBuffer(cbs);
            _motionPipeline.SetProgram(_cutoutMaskProgram);
            _motionPipeline.SetUniformBuffers([new BufferAssignment(2, buffer.Range)]);
            _motionPipeline.SetStorageBuffers([new BufferAssignment(0, MvppSurviveMaskState.MaskRange)]);
            _motionPipeline.SetImage(0, _injectCutoutMask.GetImageView());
            _motionPipeline.DispatchCompute((scene.Width + 15) / 16, (scene.Height + 15) / 16, 1);
            _motionPipeline.ComputeBarrier();
            _motionPipeline.Finish();

            return true;
        }

        private void ResetCutoutMaskBuffer(CommandBufferScoped cbs)
        {
            BufferRange range = MvppSurviveMaskState.MaskRange;
            Auto<DisposableBuffer> maskBuf = _gd.BufferManager.GetBuffer(cbs.CommandBuffer, range.Handle, true);

            unsafe
            {
                MemoryBarrier warBarrier = new()
                {
                    SType = StructureType.MemoryBarrier,
                    SrcAccessMask = AccessFlags.ShaderReadBit,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                };

                _gd.Api.CmdPipelineBarrier(
                    cbs.CommandBuffer,
                    PipelineStageFlags.ComputeShaderBit,
                    PipelineStageFlags.TransferBit,
                    0, 1, in warBarrier, 0, null, 0, null);

                _gd.Api.CmdFillBuffer(
                    cbs.CommandBuffer,
                    maskBuf.Get(cbs, range.Offset, range.Size, true).Value,
                    (ulong)range.Offset,
                    (ulong)range.Size,
                    0);

                MemoryBarrier postBarrier = new()
                {
                    SType = StructureType.MemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.ShaderReadBit,
                };

                _gd.Api.CmdPipelineBarrier(
                    cbs.CommandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.FragmentShaderBit,
                    0, 1, in postBarrier, 0, null, 0, null);
            }
        }

        private void RunVelDebugView(TextureView mv, CommandBufferScoped cbs)
        {
            if (_velDebugTex == null || _velDebugTex.Width != _output.Width || _velDebugTex.Height != _output.Height)
            {
                RetireFgVisible(_velDebugTex);
                _velDebugTex = _gd.CreateTexture(MakeInfo(_output.Info, _output.Width, _output.Height, Format.R8G8B8A8Unorm, 4)) as TextureView;
            }

            TextureView coverage = MvppVelState.CoverageBackend as TextureView;

            bool covFresh = true;
            if (_velFreshGate)
            {
                covFresh = MvppVelState.CoverageFresh;
                MvppVelState.CoverageFresh = false;
            }

            Span<float> p = stackalloc float[4];
            p[0] = _output.Width;
            p[1] = _output.Height;
            p[2] = 16f;
            p[3] = _mvppVelCompose ? 0f : ((coverage != null && covFresh) ? (_velDebugMv ? 2f : 1f) : 0f);

            if (p[3] > 1.5f) { _vdMode2++; } else if (p[3] > 0.5f) { _vdMode1++; } else { _vdMode0++; }

            long vdNow = Environment.TickCount64;
            if (vdNow - _vdLogMs >= 2000)
            {
                _vdLogMs = vdNow;
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP veldebug: mode2={_vdMode2} mode1={_vdMode1} mode0={_vdMode0} presents" +
                    $" (mvFlag={_velDebugMv}, coverage={(coverage != null ? "ok" : "NULL")}, freshNow={covFresh}).");
                _vdMode0 = 0;
                _vdMode1 = 0;
                _vdMode2 = 0;
            }

            using ScopedTemporaryBuffer buffer = _gd.BufferManager.ReserveOrCreate(_gd, cbs, p.Length * sizeof(float));
            buffer.Holder.SetDataUnchecked(buffer.Offset, p);

            _motionPipeline.SetCommandBuffer(cbs);
            _motionPipeline.SetProgram(_velDebugProgram);
            _motionPipeline.SetTextureAndSampler(ShaderStage.Compute, 1, mv, _sampler);
            _motionPipeline.SetTextureAndSampler(ShaderStage.Compute, 3, coverage ?? mv, _sampler);
            _motionPipeline.SetUniformBuffers([new BufferAssignment(2, buffer.Range)]);
            _motionPipeline.SetImage(0, _velDebugTex.GetImageView());

            _motionPipeline.DispatchCompute((_output.Width + 15) / 16, (_output.Height + 15) / 16, 1);
            _motionPipeline.ComputeBarrier();
            _motionPipeline.Finish();
        }

        private void DumpField(TextureView tex, string tag)
        {
            try
            {
                byte[] data = tex.GetData(0, 0, tex.Width, tex.Height);
                string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "mvpp-dump");
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir,
                    $"{tag}_{_mvppDumpCount:00}_{tex.Width}x{tex.Height}_{tex.Info.Format}.bin");
                System.IO.File.WriteAllBytes(file, data);
                Logger.Info?.Print(LogClass.Gpu, $"MVPP dump: {file} ({data.Length} bytes).");
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP dump FAILED ({tag}): {ex.Message}");
            }
        }

        private static float RotJump(in System.Numerics.Matrix4x4 a, in System.Numerics.Matrix4x4 b)
        {
            Span<float> fa = stackalloc float[16];
            Span<float> fb = stackalloc float[16];
            WriteMatrix(fa, 0, in a);
            WriteMatrix(fb, 0, in b);

            float m = 0f;
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    m = Math.Max(m, Math.Abs(fa[r * 4 + c] - fb[r * 4 + c]));
                }
            }

            return m;
        }

        private static float VpPairJump(in System.Numerics.Matrix4x4 a, in System.Numerics.Matrix4x4 b)
        {
            Span<float> fa = stackalloc float[16];
            Span<float> fb = stackalloc float[16];
            WriteMatrix(fa, 0, in a);
            WriteMatrix(fb, 0, in b);

            float sum = 0f;
            for (int i = 0; i < 16; i++)
            {
                sum += MathF.Abs(fa[i] - fb[i]) / (1f + MathF.Abs(fa[i]) + MathF.Abs(fb[i]));
            }

            return sum;
        }

        private static float VpProjJump(in System.Numerics.Matrix4x4 a, in System.Numerics.Matrix4x4 b)
        {
            static float RowNorm(float x, float y, float z) => MathF.Sqrt(x * x + y * y + z * z);
            static float Rel(float u, float v) => MathF.Abs(u - v) / (0.0001f + MathF.Max(MathF.Abs(u), MathF.Abs(v)));

            float sxA = RowNorm(a.M11, a.M12, a.M13), sxB = RowNorm(b.M11, b.M12, b.M13);
            float syA = RowNorm(a.M21, a.M22, a.M23), syB = RowNorm(b.M21, b.M22, b.M23);
            float wnA = RowNorm(a.M41, a.M42, a.M43), wnB = RowNorm(b.M41, b.M42, b.M43);

            return Rel(sxA, sxB) + Rel(syA, syB) + Rel(wnA, wnB);
        }

        private int _vpJumpN;
        private float _vpJumpSum;
        private float _vpJumpMax;
        private float _vpProjSum;
        private float _vpProjMax;

        private const float ProjJumpThreshold = 0.1f;
        private int _projQuietFrames;

        private const float ProjCutMagnitude = 0.5f;
        private const float ProjCutCalmEps = 0.02f;
        private const int ProjCutCalmWindow = 4;

        private readonly float[] _projJumpRing = new float[8];
        private int _projJumpRingIdx;
        private int _projRearms;
        private int _projZoomSkips;

        private static void WriteMatrix(Span<float> dst, int offset, in System.Numerics.Matrix4x4 m)
        {
            dst[offset + 0] = m.M11;
            dst[offset + 1] = m.M12;
            dst[offset + 2] = m.M13;
            dst[offset + 3] = m.M14;
            dst[offset + 4] = m.M21;
            dst[offset + 5] = m.M22;
            dst[offset + 6] = m.M23;
            dst[offset + 7] = m.M24;
            dst[offset + 8] = m.M31;
            dst[offset + 9] = m.M32;
            dst[offset + 10] = m.M33;
            dst[offset + 11] = m.M34;
            dst[offset + 12] = m.M41;
            dst[offset + 13] = m.M42;
            dst[offset + 14] = m.M43;
            dst[offset + 15] = m.M44;
        }

        private void RunMotionPass(TextureView input, CommandBufferScoped cbs)
        {
            _motionPipeline.SetCommandBuffer(cbs);
            _motionPipeline.SetProgram(_motionProgram);
            _motionPipeline.SetTextureAndSampler(ShaderStage.Compute, 1, input, _sampler);
            _motionPipeline.SetTextureAndSampler(ShaderStage.Compute, 3, _prevColor, _sampler);

            ReadOnlySpan<float> p = [input.Width, input.Height, MaxMotion, 0f, _dejitterX, _dejitterY, 0f, 0f];
            using ScopedTemporaryBuffer buffer = _gd.BufferManager.ReserveOrCreate(_gd, cbs, p.Length * sizeof(float));
            buffer.Holder.SetDataUnchecked(buffer.Offset, p);

            _motionPipeline.SetUniformBuffers([new BufferAssignment(2, buffer.Range)]);
            _motionPipeline.SetStorageBuffers([new BufferAssignment(0, new BufferRange(_sceneChangeBuffer, 0, CounterCount * sizeof(uint), true))]);
            _motionPipeline.SetImage(0, _motion.GetImageView());

            _motionPipeline.DispatchCompute((input.Width + 15) / 16, (input.Height + 15) / 16, 1);
            _motionPipeline.ComputeBarrier();
            _motionPipeline.Finish();
        }

        private void RunMotionFilterPass(TextureView input, CommandBufferScoped cbs)
        {
            _motionPipeline.SetCommandBuffer(cbs);
            _motionPipeline.SetProgram(_motionFilterProgram);

            ReadOnlySpan<float> p = [input.Width, input.Height, MaxMotion, 0f, _dejitterX, _dejitterY, 0f, 0f];
            using ScopedTemporaryBuffer buffer = _gd.BufferManager.ReserveOrCreate(_gd, cbs, p.Length * sizeof(float));
            buffer.Holder.SetDataUnchecked(buffer.Offset, p);

            _motionPipeline.SetUniformBuffers([new BufferAssignment(2, buffer.Range)]);
            _motionPipeline.SetStorageBuffers([new BufferAssignment(0, new BufferRange(_sceneChangeBuffer, 0, CounterCount * sizeof(uint), true))]);
            _motionPipeline.SetImage(0, _motionFiltered.GetImageView());
            _motionPipeline.SetImage(1, _motion.GetImageView());

            _motionPipeline.DispatchCompute((input.Width + 15) / 16, (input.Height + 15) / 16, 1);
            _motionPipeline.ComputeBarrier();
            _motionPipeline.Finish();
        }

        private void ReadCounters(out uint changed, out uint motion)
        {
            using PinnedSpan<byte> data = _gd.BufferManager.GetData(_sceneChangeBuffer, 0, CounterCount * sizeof(uint));
            ReadOnlySpan<byte> bytes = data.Get();

            changed = bytes.Length >= 1 * sizeof(uint) ? BitConverter.ToUInt32(bytes[(0 * sizeof(uint))..]) : 0u;
            motion  = bytes.Length >= 2 * sizeof(uint) ? BitConverter.ToUInt32(bytes[(1 * sizeof(uint))..]) : 0u;
        }

        private void CopyToPrev(TextureView input, CommandBufferScoped cbs)
        {
            ImageSubresourceLayers layers = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };

            ImageCopy region = new()
            {
                SrcSubresource = layers,
                DstSubresource = layers,
                Extent = new Extent3D((uint)input.Width, (uint)input.Height, 1),
            };

            _gd.Api.CmdCopyImage(
                cbs.CommandBuffer,
                input.GetImage().Get(cbs).Value,
                ImageLayout.General,
                _prevColor.GetImage().Get(cbs).Value,
                ImageLayout.General,
                1,
                in region);
        }

        private void CopyDepth(TextureView src, TextureView dst, CommandBufferScoped cbs)
        {
            ImageSubresourceLayers layers = new()
            {
                AspectMask = ImageAspectFlags.DepthBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };

            ImageCopy region = new()
            {
                SrcSubresource = layers,
                DstSubresource = layers,
                Extent = new Extent3D((uint)src.Width, (uint)src.Height, 1),
            };

            _gd.Api.CmdCopyImage(
                cbs.CommandBuffer,
                src.GetImage().Get(cbs).Value,
                ImageLayout.General,
                dst.GetImage().Get(cbs).Value,
                ImageLayout.General,
                1,
                in region);
        }

        private StreamlineDlss.DlssTexture Describe(TextureView tex, CommandBufferScoped cbs)
        {
            return new StreamlineDlss.DlssTexture
            {
                Image = (IntPtr)tex.GetImage().Get(cbs).Value.Handle,
                View = (IntPtr)tex.GetImageView().Get(cbs).Value.Handle,
                NativeFormat = (uint)tex.VkFormat,
                Layout = (uint)ImageLayout.General,
                Width = (uint)tex.Width,
                Height = (uint)tex.Height,
            };
        }

        private unsafe void BarrierOutput(CommandBufferScoped cbs)
        {
            Image image = _output.GetImage().Get(cbs).Value;

            ImageMemoryBarrier b = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.MemoryWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit | AccessFlags.MemoryReadBit,
                OldLayout = ImageLayout.General,
                NewLayout = ImageLayout.General,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };

            _gd.Api.CmdPipelineBarrier(cbs.CommandBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
                0, 0, null, 0, null, 1, &b);
        }

        private static bool AutoResSizeSettled(long sig)
        {
            if (_autoResAppliedSig < 0)
            {
                return true;
            }

            long nowMs = Environment.TickCount64;
            if (_autoResPendingSig != sig)
            {
                _autoResPendingSig = sig;
                _autoResPendingSinceMs = nowMs;

                return false;
            }

            return nowMs - _autoResPendingSinceMs >= AutoResSettleMs;
        }

        private static bool TryPickMode(int inW, int inH, int outW, int outH, out StreamlineDlss.DlssMode mode)
        {
            foreach (StreamlineDlss.DlssMode candidate in ModesByQuality)
            {
                if (StreamlineDlss.GetRenderRange(candidate, (uint)outW, (uint)outH, out uint minW, out uint minH, out uint maxW, out uint maxH) &&
                    inW >= minW && inW <= maxW && inH >= minH && inH <= maxH)
                {
                    mode = candidate;

                    return true;
                }
            }

            mode = StreamlineDlss.DlssMode.MaxPerformance;

            return false;
        }

        private static bool PickModeAndOutput(int inW, int inH, int displayW, int displayH,
            out StreamlineDlss.DlssMode mode, out int outW, out int outH)
        {
            mode = StreamlineDlss.DlssMode.MaxPerformance;
            outW = displayW;
            outH = displayH;

            long bestDelta = long.MaxValue;
            bool found = false;

            foreach (StreamlineDlss.DlssMode candidate in ModesByQuality)
            {
                if (candidate == StreamlineDlss.DlssMode.Dlaa)
                {
                    continue;
                }

                if (!StreamlineDlss.GetOptimalRenderSize(candidate, (uint)displayW, (uint)displayH,
                        out uint optW, out uint optH) || optW == 0 || optH == 0)
                {
                    continue;
                }

                double ratioW = (double)displayW / optW;
                double ratioH = (double)displayH / optH;

                bool degenerate = StreamlineDlss.GetRenderRange(candidate, (uint)displayW, (uint)displayH,
                        out uint dMinW, out _, out uint dMaxW, out _) && dMinW == dMaxW;

                int candOutW = degenerate ? (int)Math.Round(inW * ratioW) : (int)(inW * ratioW * 0.995);
                int candOutH = degenerate ? (int)Math.Round(inH * ratioH) : (int)(inH * ratioH * 0.995);

                if (!StreamlineDlss.GetRenderRange(candidate, (uint)candOutW, (uint)candOutH,
                        out uint minW, out uint minH, out uint maxW, out uint maxH)
                    || inW < minW || inW > maxW || inH < minH || inH > maxH)
                {
                    continue;
                }

                long delta = Math.Abs((long)candOutW - displayW) + Math.Abs((long)candOutH - displayH);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    mode = candidate;
                    outW = candOutW;
                    outH = candOutH;
                    found = true;
                }
            }

            return found;
        }

        public bool RunInjectMidFrame(CommandBufferScoped cbs, ITexture src, ITexture dst, ITexture depth)
        {
            if (!_injectPass || !DlssIntegration.IsEnabled || !Streamline.IsInitialized)
            {
                return false;
            }

            if (_injectPendingDump && !_injectDumped && _injectLastDst != null)
            {
                _injectPendingDump = false;
                _injectDumped = true;
                DumpInjectPng(_injectLastDst);
            }

            TextureView scene = src as TextureView;
            TextureView dstView = dst as TextureView;
            if (scene == null || dstView == null ||
                scene.Width == 0 || scene.Height == 0 || dstView.Width == 0 || dstView.Height == 0)
            {
                InjectStatus("midframe skip: source/destination not available (fallback to guest draw)", false);

                return false;
            }

            if (_injectDumpColor && !_injectColorDumped && _injectFrame >= 120 &&
                Environment.TickCount64 - _injectDumpLastTryMs > 1000)
            {
                _injectDumpLastTryMs = Environment.TickCount64;
                if (DumpInjectPng(scene, "srcraw", 30f))
                {
                    if (MvppColorSnapshot.SceneColorBackend is TextureView dumpSnap)
                    {
                        DumpInjectPng(dumpSnap, "snapshot");
                    }
                    MvppColorSnapshot.DumpArmed = true;
                    _injectColorDumped = true;
                }
            }

            if (_dumpSubrect)
            {
                float camW = DlssJitterState.CamVpW;
                float camH = DlssJitterState.CamVpH;
                bool lowStep = camW > 0f && camW <= scene.Width * 0.6f;
                bool fullStep = camW >= scene.Width * 0.9f;

                if (lowStep)
                {
                    _subrectLowFrames++;
                }
                else if (fullStep)
                {
                    _subrectFullFrames++;
                }

                long nowSub = Environment.TickCount64;

                if (nowSub - _subrectSummaryMs >= 8000)
                {
                    _subrectSummaryMs = nowSub;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-SUBRECT (~8s): injectFrames low={_subrectLowFrames} full={_subrectFullFrames} " +
                        $"camVp={camW:0}x{camH:0} taggedExtent={scene.Width}x{scene.Height}");
                    _subrectLowFrames = _subrectFullFrames = 0;
                }

                if (_injectFrame >= 120 && nowSub - _subrectLastTryMs > 1000)
                {
                    _subrectLastTryMs = nowSub;

                    if (lowStep && !_subrectLowDumped && DumpInjectPng(scene, "subrect_low", 8f))
                    {
                        _subrectLowDumped = true;
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP-SUBRECT LOW DUMP: activeVp={camW:0}x{camH:0} taggedExtent={scene.Width}x{scene.Height} " +
                            "(Evaluate declares NO subrect) -> subrect_low.png");
                    }
                    else if (fullStep && !_subrectFullDumped && DumpInjectPng(scene, "subrect_full", 8f))
                    {
                        _subrectFullDumped = true;
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP-SUBRECT FULL DUMP: activeVp={camW:0}x{camH:0} taggedExtent={scene.Width}x{scene.Height} " +
                            "-> subrect_full.png");
                    }
                }
            }

            _injectPostColorUsed = false;
            if (_injectPostColor)
            {
                TextureView postTm = MvppColorSnapshot.SceneColorBackend as TextureView;
                if (postTm != null && postTm.Width == scene.Width && postTm.Height == scene.Height &&
                    postTm.Info.Format == scene.Info.Format)
                {
                    scene = postTm;
                    _injectPostColorUsed = true;
                }
            }

            _injectHeldColorUsed = false;
            if (_injectHeldColor)
            {
                TextureView heldTm = MvppColorSnapshot.SceneColorHeld as TextureView;
                if (heldTm != null && heldTm.Width == scene.Width && heldTm.Height == scene.Height &&
                    heldTm.Info.Format == scene.Info.Format)
                {
                    scene = heldTm;
                    _injectHeldColorUsed = true;
                }
            }

            int rw = scene.Width, rh = scene.Height;
            int ow = dstView.Width, oh = dstView.Height;

            if (!TryPickMode(rw, rh, ow, oh, out StreamlineDlss.DlssMode mode))
            {
                if (!_injectPickWarned)
                {
                    _injectPickWarned = true;
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"MVPP-INJECT-C3: no DLSS-SR preset fits scene {rw}x{rh} -> real dst {ow}x{oh}; evaluate skipped (fallback to guest draw).");
                }

                return false;
            }

            EnsureInjectAux(rw, rh, scene, cbs);

            _injectTintUsed = false;
            if (_injectTintProbe)
            {
                EnsureInjectTint(rw, rh, scene, cbs);
                if (_injectTintTex != null)
                {
                    scene = _injectTintTex;
                    _injectTintUsed = true;
                }
            }

            TextureView realDepth = depth as TextureView;
            bool depthOk = realDepth != null && realDepth.Width == rw && realDepth.Height == rh;
            TextureView depthForDlss = depthOk ? realDepth : _injectDepthZero;
            string depthReason = depthOk ? "real"
                : (realDepth == null ? "zero(no-depth)" : $"zero(dim-mismatch {realDepth.Width}x{realDepth.Height} vs {rw}x{rh})");

            TextureView genMv = depthOk ? RunInjectReprojectPass(realDepth, cbs) : null;

            bool mvReal = genMv != null && genMv.Width == rw && genMv.Height == rh;
            TextureView mvForDlss = mvReal ? genMv : _injectMotionZero;

            if (_rawMvSr && mvReal && _injectMotionRaw != null &&
                _injectMotionRaw.Width == rw && _injectMotionRaw.Height == rh)
            {
                mvForDlss = _injectMotionRaw;
            }

            float jitRawX = _injectJitUseOffset ? DlssJitterState.OffsetX : DlssJitterState.PresentX;
            float jitRawY = _injectJitUseOffset ? DlssJitterState.OffsetY : DlssJitterState.PresentY;
            float declJitterX = jitRawX * _injectJitSignX * _injectJitScale;
            float declJitterY = jitRawY * _injectJitSignY * _injectJitScale;

            long nowC4 = Environment.TickCount64;
            bool inputDimsChanged = rw != _injectPrevRw || rh != _injectPrevRh;
            bool outputDimsChanged = ow != _injectPrevOw || oh != _injectPrevOh;
            bool dimsChanged = _injectDynRes ? outputDimsChanged : (inputDimsChanged || outputDimsChanged);
            bool gap = _injectHasHistory && (nowC4 - _injectLastRunMs) > _injectGapMs;
            bool reset = !_injectHasHistory || !mvReal || dimsChanged || gap;
            string resetReason = !_injectHasHistory ? "first" : !mvReal ? "mv-zero" : dimsChanged ? "dims-changed" : gap ? "gap" : "none";
            _injectPrevRw = rw; _injectPrevRh = rh; _injectPrevOw = ow; _injectPrevOh = oh;
            _injectLastRunMs = nowC4;

            if (_jitterval)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"JITTERVAL-INJ inj={_injectFrame} fid={DlssJitterState.FrameId} presFrame={DlssJitterState.PresentFrameId} " +
                    $"jitSrc={(_injectJitUseOffset ? "Offset" : "Present")} rawX={jitRawX:0.0000} rawY={jitRawY:0.0000} " +
                    $"declX={declJitterX:0.0000} declY={declJitterY:0.0000} " +
                    $"mvReal={(mvReal ? 1 : 0)} depthOk={(depthOk ? 1 : 0)} reset={(reset ? 1 : 0)}({resetReason}) " +
                    $"camFresh={(DlssCameraState.LastConsumeFresh ? 1 : 0)} " +
                    $"camPairEq={(_mvppHasPair && _mvppVpPrev.Equals(_mvppVpCurr) ? 1 : 0)} " +
                    $"camD={(_mvppHasPair ? JvFrob(in _mvppVpCurr, in _mvppVpPrev) : -1f):0.000000} " +
                    $"in={rw}x{rh} out={ow}x{oh}");
            }

            if (_resetCount)
            {
                _rcFrames++;
                if (reset)
                {
                    if (!_injectHasHistory) _rcFirst++;
                    else if (!mvReal) _rcMvzero++;
                    else if (dimsChanged) _rcDims++;
                    else if (gap) _rcGap++;
                }
                if (nowC4 - _rcSummaryMs >= 8000)
                {
                    _rcSummaryMs = nowC4;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-RESETS (~8s): frames={_rcFrames} resets: dims-changed={_rcDims} gap={_rcGap} " +
                        $"mv-zero={_rcMvzero} first={_rcFirst} | dynres={(_injectDynRes ? "ON" : "off")}");
                    _rcDims = _rcGap = _rcMvzero = _rcFirst = _rcFrames = 0;
                }
            }

            if (_injectDiagCount < 6)
            {
                _injectDiagCount++;
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP-INJECT-DIAG C4 dst={dstView.Width}x{dstView.Height} src={scene.Width}x{scene.Height} mode={mode} " +
                    $"DEPTH={(depthOk ? "REAL" : "ZERO")} MV_REAL={mvReal} mvSize={(mvReal ? $"{genMv.Width}x{genMv.Height}" : "zero")} MvecScale=1/{mvForDlss.Width} " +
                    $"reset={reset} resetReason={resetReason} " +
                    $"JITTER src={(_injectJitUseOffset ? "Offset" : "Present")} declared=({declJitterX:0.0000},{declJitterY:0.0000}) " +
                    $"signs=({_injectJitSignX:+0;-0},{_injectJitSignY:+0;-0}) scale={_injectJitScale:0.000} Present=({DlssJitterState.PresentX:0.0000},{DlssJitterState.PresentY:0.0000}) " +
                    $"Offset=({DlssJitterState.OffsetX:0.0000},{DlssJitterState.OffsetY:0.0000}) mvFlipY={(_injectMvFlipY >= 0 ? _injectMvFlipY.ToString() : $"def({(_mvppFlipY ? 1 : 0)})")} " +
                    $"postColor={(_injectPostColorUsed ? "ON" : "off")} tint={(_injectTintUsed ? "ON" : "off")} " +
                    $"held={(_injectHeldColorUsed ? "ON" : "off")}.");
            }

            if (!StreamlineDlss.SetOptions(InjectViewportId, mode, (uint)ow, (uint)oh, true))
            {
                InjectStatus("midframe skip: SetOptions failed", false);

                return false;
            }

            bool cutoutBiasOk = _injectCutoutBias && RunInjectCutoutMask(scene, cbs);

            StreamlineDlss.DlssTexture inTex = Describe(scene, cbs);
            StreamlineDlss.DlssTexture outTex = Describe(dstView, cbs);
            StreamlineDlss.DlssTexture depthTex = Describe(depthForDlss, cbs);
            StreamlineDlss.DlssTexture mvTex = Describe(mvForDlss, cbs);
            StreamlineDlss.DlssTexture cutoutBiasTex = cutoutBiasOk ? Describe(_injectCutoutMask, cbs) : default;

            bool ok = StreamlineDlss.Evaluate(
                (IntPtr)cbs.CommandBuffer.Handle,
                InjectViewportId,
                _injectFrame++,
                reset,
                declJitterX,
                declJitterY,
                in inTex,
                in outTex,
                in depthTex,
                in mvTex,
                in cutoutBiasTex,
                cutoutBiasOk);

            if (ok)
            {
                _injectHasHistory = true;
                MvppColorSnapshot.LastInjectMs = nowC4;

                if (cutoutBiasOk)
                {
                    ResetCutoutMaskBuffer(cbs);

                    _cutoutBiasFrames++;

                    if (!_cutoutBiasLogged)
                    {
                        _cutoutBiasLogged = true;
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP-INJECT-MASK: cutout bias mask {_injectCutoutMask.Width}x{_injectCutoutMask.Height} " +
                            $"(R8Unorm, = DLSS input {scene.Width}x{scene.Height}) converted from survivor SSBO " +
                            $"and PASSED to Evaluate as BiasCurrentColorHint.");
                    }

                    if (nowC4 - _cutoutBiasSummaryMs >= 8000)
                    {
                        _cutoutBiasSummaryMs = nowC4;
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP-INJECT-MASK (~8s): biasFrames={_cutoutBiasFrames} maskDims={_injectCutoutMask.Width}x{_injectCutoutMask.Height}");
                        _cutoutBiasFrames = 0;
                    }
                }
                else if (_injectCutoutBias && !_cutoutBiasNoMaskLogged)
                {
                    _cutoutBiasNoMaskLogged = true;
                    Logger.Info?.Print(LogClass.Gpu,
                        "MVPP-INJECT-MASK: bias flag ON but no mask available (survive probe mirror not seen yet) -- evaluating WITHOUT bias.");
                }

                BarrierInjectTarget(dstView, cbs);
                _injectLastDst = dstView;

                if (!_injectDumped && !_injectPendingDump)
                {
                    if (_injectOkCount == 0)
                    {
                        _injectFirstOkMs = Environment.TickCount64;
                    }

                    _injectOkCount++;

                    if (_injectOkCount >= InjectDumpSettleFrames)
                    {
                        _injectPendingDump = true;
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP-INJECT-DIAG C3a dump-armed (stabilized, real dst): okFrame={_injectOkCount} " +
                            $"elapsedMsSinceFirstOK={Environment.TickCount64 - _injectFirstOkMs} " +
                            $"dstHost={dstView.Width}x{dstView.Height} dstFmt={dstView.Info.Format} " +
                            $"(guest draw overwrites -> PNG shows the game frame; DLSS pixels validated in C3b).");
                    }
                }
            }

            bool substitute = ok && SubstituteActive;
            string reason = substitute ? "-" : (!ok ? "eval-FAIL" : "SubstituteActive=false");

            if (substitute)
            {
                _injectSubstitutedThisFrame = true;
            }

            if (!_injectQuiet)
            {
                InjectStatus(
                    $"midframe eval={(ok ? "OK" : "FAIL")} scene {rw}x{rh} -> REAL-DST {ow}x{oh} mode={mode} " +
                    $"DEPTH={(depthOk ? "REAL" : "ZERO")} MV_REAL={mvReal} reset={reset}({resetReason}) " +
                    $"jitter=({declJitterX:0.000},{declJitterY:0.000}) " +
                    $"SUBSTITUTE={(substitute ? "ON" : "OFF")} fallbackReason={reason} evalIndex={_injectFrame}", ok);
            }

            return substitute;
        }

        private void EnsureInjectReprojResources(int w, int h, TextureView template)
        {
            if (_injectMotion != null && _injectReprojW == w && _injectReprojH == h)
            {
                return;
            }

            RetireFgVisible(_injectMotion);
            RetireFgVisible(_injectMotionRaw);
            RetireFgVisible(_injectReprojDepthSnap);
            RetireFgVisible(_injectReprojBiasMask);
            _injectMotion = _gd.CreateTexture(MakeInfo(template.Info, w, h, Format.R16G16Float, 4)) as TextureView;
            _injectMotionRaw = _gd.CreateTexture(MakeInfo(template.Info, w, h, Format.R16G16Float, 4)) as TextureView;
            _injectReprojDepthSnap = _gd.CreateTexture(MakeInfo(template.Info, w, h, Format.R32Float, 4)) as TextureView;
            _injectReprojBiasMask = _gd.CreateTexture(MakeInfo(template.Info, w, h, Format.R8Unorm, 1)) as TextureView;
            _injectReprojW = w;
            _injectReprojH = h;

            if (_injectReprojStats == BufferHandle.Null)
            {
                _injectReprojStats = _gd.BufferManager.CreateWithHandle(_gd, MvppStatsCount * sizeof(uint));
            }
        }

        private TextureView RunInjectReprojectPass(TextureView depth, CommandBufferScoped cbs)
        {
            if (_mvppReprojProgram == null || !_mvppHasPair || depth == null)
            {
                if (!_injectReprojWarned)
                {
                    _injectReprojWarned = true;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-INJECT-MV: reproj unavailable (program={_mvppReprojProgram != null} hasPair={_mvppHasPair} depth={depth != null}); MV stay zero.");
                }

                return null;
            }

            int w = depth.Width, h = depth.Height;
            EnsureInjectReprojResources(w, h, depth);
            if (_injectMotion == null)
            {
                return null;
            }

            System.Numerics.Matrix4x4 vpCurr = _mvppVpCurr;
            System.Numerics.Matrix4x4 vpPrev = _mvppVpPrev;
            if (!System.Numerics.Matrix4x4.Invert(vpCurr, out System.Numerics.Matrix4x4 invVpCurr))
            {
                return null;
            }

            System.Numerics.Matrix4x4 invT = System.Numerics.Matrix4x4.Transpose(invVpCurr);
            System.Numerics.Matrix4x4 prevT = System.Numerics.Matrix4x4.Transpose(vpPrev);
            bool zMinusOne = _mvppZglEnv == "1" || (_mvppZglEnv != "0" && DlssCameraState.GuestDepthMinusOneToOne);

            long now = Environment.TickCount64;
            if (!_injectQuiet && now - _injectReprojLogMs >= 1000)
            {
                _injectReprojLogMs = now;
                using PinnedSpan<byte> data = _gd.BufferManager.GetData(_injectReprojStats, 0, MvppStatsCount * sizeof(uint));
                ReadOnlySpan<uint> s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(data.Get());
                float meanRX = s[0] > 0 ? s[8] / 16f / s[0] : 0f;
                float meanRY = s[0] > 0 ? s[9] / 16f / s[0] : 0f;
                float skyMX = s[1] > 0 ? s[20] / 1024f / s[1] : 0f;
                float skyMY = s[1] > 0 ? s[21] / 1024f / s[1] : 0f;
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP-INJECT-MV: gen {w}x{h} R16G16Float MvecScale=1/{w} samples={s[0]} sky={s[1]} " +
                    $"mean|reproj|=({meanRX:0.00},{meanRY:0.00})px center=({BitConverter.UInt32BitsToSingle(s[14]):0.###},{BitConverter.UInt32BitsToSingle(s[15]):0.###})px " +
                    $"skyMV=({skyMX:0.00},{skyMY:0.00})px skyRot={(_injectSkyRot ? 1 : 0)} " +
                    $"d={BitConverter.UInt32BitsToSingle(s[10]):0.#####} worldY={BitConverter.UInt32BitsToSingle(s[13]):0.#} " +
                    $"maxMotion={MvppMaxMotion} flipY={_mvppFlipY} zMinusOne={zMinusOne} (NOT fed to DLSS yet)");
            }

            Span<uint> statsZero = stackalloc uint[MvppStatsCount];
            _gd.BufferManager.SetData<uint>(_injectReprojStats, 0, statsZero);

            Span<float> p = stackalloc float[52];
            WriteMatrix(p, 0, in invT);
            WriteMatrix(p, 16, in prevT);
            p[32] = w;
            p[33] = h;
            p[34] = MvppMaxMotion;
            p[35] = _injectQuiet ? 0f : 1f;
            p[36] = _injectMvFlipY >= 0 ? _injectMvFlipY : (_mvppFlipY ? 1f : 0f);
            p[37] = zMinusOne ? 1f : 0f;
            p[38] = 0f;
            p[39] = _mvppDeadLo;
            p[40] = _mvppDeadHi;
            p[41] = 0f;
            p[42] = 0f;
            p[43] = 0f;
            p[44] = 0f;
            p[45] = depth.ActiveWidth > 0 ? (float)depth.ActiveWidth / depth.Width : 1f;
            p[46] = depth.ActiveHeight > 0 ? (float)depth.ActiveHeight / depth.Height : 1f;
            p[47] = _injectSkyRot ? 1f : 0f;

            using ScopedTemporaryBuffer buffer = _gd.BufferManager.ReserveOrCreate(_gd, cbs, p.Length * sizeof(float));
            buffer.Holder.SetDataUnchecked(buffer.Offset, p);

            _motionPipeline.SetCommandBuffer(cbs);
            _motionPipeline.SetProgram(_mvppReprojProgram);
            _motionPipeline.SetTextureAndSampler(ShaderStage.Compute, 1, depth, _mvppDepthLinear ? _sampler : _samplerNearest);
            _motionPipeline.SetTextureAndSampler(ShaderStage.Compute, 3, depth, _samplerNearest);
            _motionPipeline.SetTextureAndSampler(ShaderStage.Compute, 4, depth, _samplerNearest);
            _motionPipeline.SetTextureAndSampler(ShaderStage.Compute, 5, depth, _samplerNearest);
            _motionPipeline.SetUniformBuffers([new BufferAssignment(2, buffer.Range)]);
            _motionPipeline.SetStorageBuffers([new BufferAssignment(0, new BufferRange(_injectReprojStats, 0, MvppStatsCount * sizeof(uint), true))]);
            _motionPipeline.SetImage(0, _injectMotion.GetImageView());
            _motionPipeline.SetImage(1, _injectReprojDepthSnap.GetImageView());
            _motionPipeline.SetImage(2, _injectReprojBiasMask.GetImageView());
            _motionPipeline.SetImage(3, _injectMotionRaw.GetImageView());

            _motionPipeline.DispatchCompute((w + 15) / 16, (h + 15) / 16, 1);
            _motionPipeline.ComputeBarrier();
            _motionPipeline.Finish();

            return _injectMotion;
        }

        private unsafe void EnsureInjectAux(int rw, int rh, TextureView template, CommandBufferScoped cbs)
        {
            if (_injectMotionZero != null && _injectAuxW == rw && _injectAuxH == rh)
            {
                return;
            }

            RetireFgVisible(_injectDepthZero);
            RetireFgVisible(_injectMotionZero);
            _injectDepthZero = _gd.CreateTexture(MakeInfo(template.Info, rw, rh, Format.D32Float, 4)) as TextureView;
            _injectMotionZero = _gd.CreateTexture(MakeInfo(template.Info, rw, rh, Format.R16G16Float, 4)) as TextureView;
            _injectAuxW = rw;
            _injectAuxH = rh;

            ImageSubresourceRange colorRange = new()
            {
                AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1,
            };
            ImageSubresourceRange depthRange = new()
            {
                AspectMask = ImageAspectFlags.DepthBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1,
            };

            ClearColorValue zeroColor = new(0f, 0f, 0f, 0f);
            _gd.Api.CmdClearColorImage(cbs.CommandBuffer, _injectMotionZero.GetImage().Get(cbs).Value, ImageLayout.General, in zeroColor, 1, in colorRange);
            ClearDepthStencilValue zeroDepth = new() { Depth = 0f, Stencil = 0 };
            _gd.Api.CmdClearDepthStencilImage(cbs.CommandBuffer, _injectDepthZero.GetImage().Get(cbs).Value, ImageLayout.General, in zeroDepth, 1, in depthRange);

            _gd.Api.CmdPipelineBarrier(cbs.CommandBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
                0, 0, null, 0, null, 0, null);
        }

        private unsafe void EnsureInjectTint(int rw, int rh, TextureView template, CommandBufferScoped cbs)
        {
            if (_injectTintTex != null && _injectTintW == rw && _injectTintH == rh)
            {
                return;
            }

            RetireFgVisible(_injectTintTex);
            _injectTintTex = _gd.CreateTexture(MakeInfo(template.Info, rw, rh, template.Info.Format, template.Info.BytesPerPixel)) as TextureView;
            _injectTintW = rw;
            _injectTintH = rh;

            ImageSubresourceRange colorRange = new()
            {
                AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1,
            };

            ClearColorValue magenta = new(4f, 0f, 4f, 1f);
            _gd.Api.CmdClearColorImage(cbs.CommandBuffer, _injectTintTex.GetImage().Get(cbs).Value, ImageLayout.General, in magenta, 1, in colorRange);

            _gd.Api.CmdPipelineBarrier(cbs.CommandBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
                0, 0, null, 0, null, 0, null);
        }

        private unsafe void BarrierInjectTarget(TextureView target, CommandBufferScoped cbs)
        {
            Image image = target.GetImage().Get(cbs).Value;

            ImageMemoryBarrier b = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.MemoryWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit | AccessFlags.MemoryReadBit
                    | AccessFlags.ColorAttachmentWriteBit | AccessFlags.MemoryWriteBit,
                OldLayout = ImageLayout.General,
                NewLayout = ImageLayout.General,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };

            _gd.Api.CmdPipelineBarrier(cbs.CommandBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
                0, 0, null, 0, null, 1, &b);
        }

        private void InjectStatus(string msg, bool ok)
        {
            long sig = ((long)(uint)msg.GetHashCode()) | (ok ? 1L << 40 : 0L);
            long now = Environment.TickCount64;
            if (sig != _injectStatusSig || now - _injectStatusMs >= 3000)
            {
                _injectStatusSig = sig;
                _injectStatusMs = now;
                Logger.Info?.Print(LogClass.Gpu, $"MVPP-INJECT-B: {msg}");
            }
        }

        private void FlashCapTick(CommandBufferScoped cbs, TextureView lastDst)
        {
            try
            {
                const int TW = 96, TH = 54;
                if (_flashTiny == null)
                {
                    _flashTiny = _gd.CreateTexture(MakeInfo(lastDst.Info, TW, TH, Format.R11G11B10Float, 4)) as TextureView;
                }

                _gd.HelperShader.BlitColor(_gd, cbs, lastDst, _flashTiny,
                    new Extents2D(0, 0, lastDst.Width, lastDst.Height), new Extents2D(0, 0, TW, TH), true);

                byte[] packed = _flashTiny.GetData(0, 0, TW, TH);
                if (packed == null || packed.Length < TW * TH * 4)
                {
                    return;
                }

                int n = TW * TH;
                _flashCur ??= new int[n];
                _flashPrev ??= new int[n];
                for (int i = 0; i < n; i++)
                {
                    uint p = BitConverter.ToUInt32(packed, i * 4);
                    int r = ToSrgb8(DecodeFloat11(p & 0x7FF));
                    int g = ToSrgb8(DecodeFloat11((p >> 11) & 0x7FF));
                    int b = ToSrgb8(DecodeFloat10((p >> 22) & 0x3FF));
                    _flashCur[i] = r + g + b;
                }

                _flashFrames++;
                if (_flashPrevValid)
                {
                    long diff = 0;
                    for (int i = 0; i < n; i++)
                    {
                        diff += Math.Abs(_flashCur[i] - _flashPrev[i]);
                    }
                    float metric = (float)diff / (n * 3);
                    _flashAvg = _flashAvg <= 0f ? metric : 0.9f * _flashAvg + 0.1f * metric;
                    if (metric > _flashMax) _flashMax = metric;

                    bool spike = metric > 10f && metric > 3f * _flashAvg;
                    long now = Environment.TickCount64;
                    if (spike && _flashDumpNext == 0 && _flashDumpCount < 15)
                    {
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP-FLASHCAP: SPIKE metric={metric:0.0} baseline={_flashAvg:0.0} -> dumping full frames from flash{_flashDumpCount:D3}");
                        _flashDumpNext = 3;
                    }
                    if (_flashDumpNext > 0 && _flashDumpCount < 15)
                    {
                        DumpInjectPng(lastDst, $"flash{_flashDumpCount:D3}");
                        _flashDumpCount++;
                        _flashDumpNext--;
                    }
                    if (now - _flashLogMs >= 4000)
                    {
                        _flashLogMs = now;
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP-FLASHCAP (~4s): frames={_flashFrames} baseline={_flashAvg:0.0} maxDiff={_flashMax:0.0} dumps={_flashDumpCount}");
                        _flashFrames = 0; _flashMax = 0f;
                    }
                }

                (_flashPrev, _flashCur) = (_flashCur, _flashPrev);
                _flashPrevValid = true;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP-FLASHCAP failed: {ex.Message}");
            }
        }

        private bool DumpInjectPng(TextureView tex, string tag = "output", float minMeanSrgb = 0f)
        {
            try
            {
                int w = tex.Width, h = tex.Height;
                Format fmt = tex.Info.Format;
                byte[] packed = tex.GetData(0, 0, w, h);

                if (packed == null || w <= 0 || h <= 0 || packed.Length < w * h * 4 || fmt != Format.R11G11B10Float)
                {
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"MVPP-INJECT-B: dump skipped -- invalid intermediate (len={packed?.Length ?? -1}, fmt={fmt}, {w}x{h}).");

                    return false;
                }

                byte[] rgb = new byte[w * h * 3];
                long sum = 0;
                for (int i = 0; i < w * h; i++)
                {
                    uint p = BitConverter.ToUInt32(packed, i * 4);
                    byte r = ToSrgb8(DecodeFloat11(p & 0x7FF));
                    byte g = ToSrgb8(DecodeFloat11((p >> 11) & 0x7FF));
                    byte b = ToSrgb8(DecodeFloat10((p >> 22) & 0x3FF));
                    rgb[i * 3 + 0] = r;
                    rgb[i * 3 + 1] = g;
                    rgb[i * 3 + 2] = b;
                    sum += r + g + b;
                }

                float meanSrgb = rgb.Length > 0 ? (float)sum / rgb.Length : 0f;
                if (meanSrgb < minMeanSrgb)
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP-INJECT-B: dump '{tag}' SKIPPED -- too dark (mean={meanSrgb:0.0} < {minMeanSrgb:0.0}), waiting for a gameplay frame.");

                    return false;
                }

                string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "mvpp-dump");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, $"inject_{tag}_{w}x{h}_{fmt}.png");
                WritePng(path, rgb, w, h);

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP-INJECT-B: dump PNG created '{path}' dims={w}x{h} sourceFormat={fmt} mean={meanSrgb:0.0} (HDR decoded at full precision, tone-mapped Reinhard -> sRGB8 for viewing).");

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP-INJECT-B: dump FAILED: {ex.Message}");

                return false;
            }
        }

        private static float DecodeFloat11(uint v)
        {
            uint e = (v >> 6) & 0x1F, m = v & 0x3F;
            if (e == 0) return m * (1f / 64f) * 6.10351562e-5f;
            if (e == 0x1F) return m == 0 ? float.PositiveInfinity : float.NaN;
            return (1f + m * (1f / 64f)) * MathF.Pow(2f, (int)e - 15);
        }

        private static float DecodeFloat10(uint v)
        {
            uint e = (v >> 5) & 0x1F, m = v & 0x1F;
            if (e == 0) return m * (1f / 32f) * 6.10351562e-5f;
            if (e == 0x1F) return m == 0 ? float.PositiveInfinity : float.NaN;
            return (1f + m * (1f / 32f)) * MathF.Pow(2f, (int)e - 15);
        }

        private static byte ToSrgb8(float c)
        {
            if (float.IsNaN(c) || c < 0f) c = 0f;
            if (float.IsPositiveInfinity(c)) c = 1e4f;
            c = c / (1f + c);
            float s = c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
            return (byte)Math.Clamp((int)(s * 255f + 0.5f), 0, 255);
        }

        private static void WritePng(string path, byte[] rgb, int w, int h)
        {
            using System.IO.FileStream fs = System.IO.File.Create(path);
            fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

            byte[] ihdr = new byte[13];
            WriteBE(ihdr, 0, (uint)w);
            WriteBE(ihdr, 4, (uint)h);
            ihdr[8] = 8;
            ihdr[9] = 2;
            WriteChunk(fs, "IHDR", ihdr);

            using System.IO.MemoryStream raw = new();
            for (int y = 0; y < h; y++)
            {
                raw.WriteByte(0);
                raw.Write(rgb, y * w * 3, w * 3);
            }

            byte[] rawBytes = raw.ToArray();
            using System.IO.MemoryStream comp = new();
            using (System.IO.Compression.ZLibStream z =
                new(comp, System.IO.Compression.CompressionLevel.Optimal, true))
            {
                z.Write(rawBytes, 0, rawBytes.Length);
            }

            WriteChunk(fs, "IDAT", comp.ToArray());
            WriteChunk(fs, "IEND", Array.Empty<byte>());
        }

        private static void WriteBE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        private static void WriteChunk(System.IO.Stream fs, string type, byte[] data)
        {
            byte[] len = new byte[4];
            WriteBE(len, 0, (uint)data.Length);
            fs.Write(len);

            byte[] t = System.Text.Encoding.ASCII.GetBytes(type);
            fs.Write(t);
            fs.Write(data);

            uint crc = 0xFFFFFFFF;
            crc = Crc32(crc, t);
            crc = Crc32(crc, data);
            crc ^= 0xFFFFFFFF;

            byte[] c = new byte[4];
            WriteBE(c, 0, crc);
            fs.Write(c);
        }

        private static uint Crc32(uint crc, byte[] buf)
        {
            foreach (byte bb in buf)
            {
                crc ^= bb;
                for (int k = 0; k < 8; k++)
                {
                    crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
                }
            }

            return crc;
        }

        private readonly System.Collections.Generic.List<(TextureView View, long RetireAfter)> _fgRetired = new();
        private long _presentSerial;

        private void RetireFgVisible(TextureView view)
        {
            if (view == null)
            {
                return;
            }

            _fgRetired.Add((view, _presentSerial + 4));
        }

        private void DrainFgRetired(bool all)
        {
            for (int i = _fgRetired.Count - 1; i >= 0; i--)
            {
                if (all || _fgRetired[i].RetireAfter <= _presentSerial)
                {
                    _fgRetired[i].View.Dispose();
                    _fgRetired.RemoveAt(i);
                }
            }
        }

        private void EnsureResources(TextureView input, int outW, int outH, CommandBufferScoped cbs)
        {
            if (_output != null && _inW == input.Width && _inH == input.Height && _outW == outW && _outH == outH)
            {
                return;
            }

            if (_dlssDynRes && _output != null && _outW == outW && _outH == outH &&
                (_inW != input.Width || _inH != input.Height))
            {
                RetireFgVisible(_depth);
                RetireFgVisible(_motion);
                RetireFgVisible(_motionFiltered);
                RetireFgVisible(_prevColor);
                _heldDepth = null;
                _hasHeldDepth = false;

                _inW = input.Width;
                _inH = input.Height;

                _depth = _gd.CreateTexture(MakeInfo(input.Info, input.Width, input.Height, Format.D32Float, 4)) as TextureView;
                _motion = _gd.CreateTexture(MakeInfo(input.Info, input.Width, input.Height, Format.R16G16Float, 4)) as TextureView;
                _motionFiltered = _gd.CreateTexture(MakeInfo(input.Info, input.Width, input.Height, Format.R16G16Float, 4)) as TextureView;
                _prevColor = _gd.CreateTexture(MakeInfo(input.Info, input.Width, input.Height, input.Info.Format, input.Info.BytesPerPixel)) as TextureView;
                _hasPrev = false;

                ClearResources(cbs);

                return;
            }

            RetireFgVisible(_output);
            RetireFgVisible(_depth);
            RetireFgVisible(_motion);
            RetireFgVisible(_motionFiltered);
            RetireFgVisible(_prevColor);
            _heldDepth = null;
            _hasHeldDepth = false;

            _inW = input.Width;
            _inH = input.Height;
            _outW = outW;
            _outH = outH;

            _output = _gd.CreateTexture(MakeInfo(input.Info, outW, outH, input.Info.Format, input.Info.BytesPerPixel)) as TextureView;
            _depth = _gd.CreateTexture(MakeInfo(input.Info, input.Width, input.Height, Format.D32Float, 4)) as TextureView;
            _motion = _gd.CreateTexture(MakeInfo(input.Info, input.Width, input.Height, Format.R16G16Float, 4)) as TextureView;
            _motionFiltered = _gd.CreateTexture(MakeInfo(input.Info, input.Width, input.Height, Format.R16G16Float, 4)) as TextureView;
            _prevColor = _gd.CreateTexture(MakeInfo(input.Info, input.Width, input.Height, input.Info.Format, input.Info.BytesPerPixel)) as TextureView;
            _hasPrev = false;

            ClearResources(cbs);
        }

        private static TextureCreateInfo MakeInfo(TextureCreateInfo b, int w, int h, Format format, int bytesPerPixel)
        {
            return new TextureCreateInfo(
                w,
                h,
                1,
                1,
                1,
                1,
                1,
                bytesPerPixel,
                format,
                b.DepthStencilMode,
                Target.Texture2D,
                b.SwizzleR,
                b.SwizzleG,
                b.SwizzleB,
                b.SwizzleA);
        }

        private void ClearResources(CommandBufferScoped cbs)
        {
            ImageSubresourceRange colorRange = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };

            ImageSubresourceRange depthRange = new()
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };

            ClearColorValue zeroColor = default;
            _gd.Api.CmdClearColorImage(cbs.CommandBuffer, _output.GetImage().Get(cbs).Value, ImageLayout.General, in zeroColor, 1, in colorRange);
            _gd.Api.CmdClearColorImage(cbs.CommandBuffer, _motion.GetImage().Get(cbs).Value, ImageLayout.General, in zeroColor, 1, in colorRange);
            _gd.Api.CmdClearColorImage(cbs.CommandBuffer, _motionFiltered.GetImage().Get(cbs).Value, ImageLayout.General, in zeroColor, 1, in colorRange);
            _gd.Api.CmdClearColorImage(cbs.CommandBuffer, _prevColor.GetImage().Get(cbs).Value, ImageLayout.General, in zeroColor, 1, in colorRange);

            ClearDepthStencilValue zeroDepth = new() { Depth = 0f, Stencil = 0 };
            _gd.Api.CmdClearDepthStencilImage(cbs.CommandBuffer, _depth.GetImage().Get(cbs).Value, ImageLayout.General, in zeroDepth, 1, in depthRange);
        }

        public void Dispose()
        {
            DrainFgRetired(all: true);
            _sharpenPass?.Dispose();
            _output?.Dispose();
            _depth?.Dispose();
            _motion?.Dispose();
            _motionFiltered?.Dispose();
            _prevColor?.Dispose();
            _injectTintTex?.Dispose();
            _injectDepthZero?.Dispose();
            _injectMotionZero?.Dispose();
            _injectMotion?.Dispose();
            _injectMotionRaw?.Dispose();
            _injectReprojDepthSnap?.Dispose();
            _injectReprojBiasMask?.Dispose();
            if (_injectReprojStats != BufferHandle.Null)
            {
                _gd.BufferManager.Delete(_injectReprojStats);
            }
            _bNativeHeld?.Dispose();
            foreach (TextureView held in _heldDepthCache.Values)
            {
                held?.Dispose();
            }
            _heldDepthCache.Clear();
            _motionProgram?.Dispose();
            _mvppReprojProgram?.Dispose();
            _cutoutMaskProgram?.Dispose();
            _injectCutoutMask?.Dispose();
            _mvppMotion?.Dispose();
            _mvppMotionRaw?.Dispose();
            _velDebugProgram?.Dispose();
            _velDebugTex?.Dispose();
            if (_mvppStatsBuffer != BufferHandle.Null)
            {
                _gd.BufferManager.Delete(_mvppStatsBuffer);
            }
            _motionFilterProgram?.Dispose();
            _motionPipeline?.Dispose();
            _sampler?.Dispose();
            _samplerNearest?.Dispose();
            _mvppDepthSnapA?.Dispose();
            _mvppDepthSnapB?.Dispose();
            _mvppBiasMask?.Dispose();
            _borderFixTex?.Dispose();
            _gpuTimer?.Dispose();
            _reprojTimer?.Dispose();
            _gd.BufferManager.Delete(_sceneChangeBuffer);
        }
    }
}
