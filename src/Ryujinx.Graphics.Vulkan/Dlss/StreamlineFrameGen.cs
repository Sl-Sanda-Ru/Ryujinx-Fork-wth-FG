
using Ryujinx.Common.Logging;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Vulkan.Dlss
{
    public static unsafe class StreamlineFrameGen
    {
        private const string Interposer = "sl.interposer";

        private const uint FeatureReflex = 3;
        private const uint FeaturePCL = 4;
        private const uint FeatureDLSSG = 1000;

        private const uint MarkerSimulationStart = 0;
        private const uint MarkerSimulationEnd = 1;
        private const uint MarkerRenderSubmitStart = 2;
        private const uint MarkerRenderSubmitEnd = 3;
        private const uint MarkerPresentStart = 4;
        private const uint MarkerPresentEnd = 5;

        private const int ReflexModeLowLatency = 1;

        private const uint DlssgModeOn = 1;

        private const uint LifecycleValidUntilPresent = 1;

        private const uint BufferTypeDepth = 0;
        private const uint BufferTypeMotionVectors = 1;
        private const uint BufferTypeHudLessColor = 2;

        private const byte ResourceTypeTex2d = 0;

        private static readonly bool _activationEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_DLSS_FG_ACTIVATE") is not ("0" or "false" or "off");

        private static readonly uint _framesToGenerate =
            uint.TryParse(Environment.GetEnvironmentVariable("RYUJINX_DLSS_FG_FRAMES"), out uint n)
                ? Math.Clamp(n, 1u, 5u)
                : 1u;

        [StructLayout(LayoutKind.Sequential)]
        private struct StructType
        {
            public uint Data1;
            public ushort Data2;
            public ushort Data3;
            public byte B0, B1, B2, B3, B4, B5, B6, B7;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ViewportHandle
        {
            public IntPtr Next;
            public StructType Type;
            public nuint Version;
            public uint Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Resource
        {
            public IntPtr Next;
            public StructType Type;
            public nuint Version;
            public byte ResType;
            public IntPtr Native;
            public IntPtr Memory;
            public IntPtr View;
            public uint State;
            public uint Width;
            public uint Height;
            public uint NativeFormat;
            public uint MipLevels;
            public uint ArrayLayers;
            public ulong GpuVirtualAddress;
            public uint Flags;
            public uint Usage;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Extent
        {
            public uint Top, Left, Width, Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ResourceTag
        {
            public IntPtr Next;
            public StructType Type;
            public nuint Version;
            public IntPtr ResourcePtr;
            public uint BufferType;
            public uint Lifecycle;
            public Extent Extent;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ReflexOptions
        {
            public IntPtr Next;
            public StructType Type;
            public nuint Version;
            public int Mode;
            public uint FrameLimitUs;
            public byte UseMarkersToOptimize;
            public ushort VirtualKey;
            public uint IdThread;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DlssgOptions
        {
            public IntPtr Next;
            public StructType Type;
            public nuint Version;
            public uint Mode;
            public uint NumFramesToGenerate;
            public uint Flags;
            public uint DynamicResWidth;
            public uint DynamicResHeight;
            public uint NumBackBuffers;
            public uint MvecDepthWidth;
            public uint MvecDepthHeight;
            public uint ColorWidth;
            public uint ColorHeight;
            public uint ColorBufferFormat;
            public uint MvecBufferFormat;
            public uint DepthBufferFormat;
            public uint HudLessBufferFormat;
            public uint UiBufferFormat;
            public IntPtr OnErrorCallback;
            public byte BReserved15;
            public uint QueueParallelismMode;
            public byte EnableUserInterfaceRecomposition;
            public float DynamicTargetFrameRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DlssgState
        {
            public IntPtr Next;
            public StructType Type;
            public nuint Version;
            public ulong EstimatedVRAMUsageInBytes;
            public uint Status;
            public uint MinWidthOrHeight;
            public uint NumFramesActuallyPresented;
            public uint NumFramesToGenerateMax;
            public byte BReserved4;
            public byte BIsVsyncSupportAvailable;
            public IntPtr InputsProcessingCompletionFence;
            public ulong LastPresentInputsProcessingCompletionFenceValue;
            public byte BIsDynamicMFGSupported;
        }

        [DllImport(Interposer, EntryPoint = "slGetFeatureFunction", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern int slGetFeatureFunction(uint feature, [MarshalAs(UnmanagedType.LPStr)] string functionName, out IntPtr function);

        [DllImport(Interposer, EntryPoint = "slSetTagForFrame", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern int slSetTagForFrame(IntPtr frameToken, in ViewportHandle viewport, ResourceTag* tags, uint numTags, IntPtr cmdBuffer);

        private static delegate* unmanaged[Cdecl]<in ViewportHandle, in DlssgOptions, int> _slDLSSGSetOptions;
        private static delegate* unmanaged[Cdecl]<in ViewportHandle, ref DlssgState, IntPtr, int> _slDLSSGGetState;
        private static delegate* unmanaged[Cdecl]<in ReflexOptions, int> _slReflexSetOptions;
        private static delegate* unmanaged[Cdecl]<IntPtr, int> _slReflexSleep;
        private static delegate* unmanaged[Cdecl]<uint, IntPtr, int> _slPCLSetMarker;

        private const int SizeStabilityFrames = 30;

        private static bool _bindTried;
        private static bool _bound;
        private static bool _reflexOn;
        private static bool _optionsSet;
        private static uint _optColorW, _optColorH, _optMvecW, _optMvecH;
        private static DlssgOptions _lastEnabledOptions;
        private static uint _seenColorW, _seenColorH, _seenMvecW, _seenMvecH;
        private static int _sizeStableFrames;
        private static IntPtr _frameToken;
        private static bool _tagFailedLogged;
        private static bool _markerFailedLogged;
        private static bool _hudlessLogged;
        private static uint _presentsSinceStateLog;
        private static uint _lastLoggedGenerated;

        public static volatile int UiMultiplier = 1;

        /// <summary>
        /// Frames reaching the screen per second: the app's own present rate over the last second
        /// times the frames DLSS-G reports actually presenting per app present. Measured, not the
        /// configured multiplier, so dropped generated frames show up here.
        /// </summary>
        public static volatile float UiDisplayFps;

        public static bool UiArmed
        {
            get => Environment.TickCount64 - System.Threading.Interlocked.Read(ref _uiArmedAtMs) < 3000;
            set => System.Threading.Interlocked.Exchange(ref _uiArmedAtMs, value ? Environment.TickCount64 : 0);
        }

        private static long _uiArmedAtMs;

        private static int _appPresentsInWindow;
        private static long _windowStartMs;

        public static uint SwapchainImageCount;
        public static uint SwapchainFormat;
        public static uint SwapchainWidth;
        public static uint SwapchainHeight;

        private static StructType Guid(uint d1, ushort d2, ushort d3, byte b0, byte b1, byte b2, byte b3, byte b4, byte b5, byte b6, byte b7)
        {
            return new StructType
            {
                Data1 = d1, Data2 = d2, Data3 = d3,
                B0 = b0, B1 = b1, B2 = b2, B3 = b3, B4 = b4, B5 = b5, B6 = b6, B7 = b7,
            };
        }

        private static ViewportHandle MakeViewport(uint id)
        {
            ViewportHandle vp = default;
            vp.Type = Guid(0x171b6435, 0x9b3c, 0x4fc8, 0x99, 0x94, 0xfb, 0xe5, 0x25, 0x69, 0xaa, 0xa4);
            vp.Version = 1;
            vp.Value = id;

            return vp;
        }

        private static bool Bind()
        {
            if (_bindTried)
            {
                return _bound;
            }

            _bindTried = true;

            bool ok = true;
            ok &= BindOne(FeatureDLSSG, "slDLSSGSetOptions", out IntPtr pSetOpt);
            ok &= BindOne(FeatureDLSSG, "slDLSSGGetState", out IntPtr pGetState);
            ok &= BindOne(FeatureReflex, "slReflexSetOptions", out IntPtr pReflexOpt);
            ok &= BindOne(FeatureReflex, "slReflexSleep", out IntPtr pReflexSleep);
            ok &= BindOne(FeaturePCL, "slPCLSetMarker", out IntPtr pPclMarker);

            if (!ok)
            {
                Logger.Warning?.Print(LogClass.Gpu, "DLSS-FG: feature-function binding incomplete; frame generation stays OFF (present path unaffected).");

                return false;
            }

            _slDLSSGSetOptions = (delegate* unmanaged[Cdecl]<in ViewportHandle, in DlssgOptions, int>)pSetOpt;
            _slDLSSGGetState = (delegate* unmanaged[Cdecl]<in ViewportHandle, ref DlssgState, IntPtr, int>)pGetState;
            _slReflexSetOptions = (delegate* unmanaged[Cdecl]<in ReflexOptions, int>)pReflexOpt;
            _slReflexSleep = (delegate* unmanaged[Cdecl]<IntPtr, int>)pReflexSleep;
            _slPCLSetMarker = (delegate* unmanaged[Cdecl]<uint, IntPtr, int>)pPclMarker;
            _bound = true;

            Logger.Info?.Print(LogClass.Gpu,
                $"DLSS-FG: feature functions bound (sizes: DLSSGOptions={Marshal.SizeOf<DlssgOptions>()}(120) DLSSGState={Marshal.SizeOf<DlssgState>()}(88) ReflexOptions={Marshal.SizeOf<ReflexOptions>()}(48)).");

            return true;
        }

        private static bool BindOne(uint feature, string name, out IntPtr fn)
        {
            int r;
            try
            {
                r = slGetFeatureFunction(feature, name, out fn);
            }
            catch (EntryPointNotFoundException)
            {
                fn = IntPtr.Zero;
                Logger.Warning?.Print(LogClass.Gpu, "DLSS-FG: slGetFeatureFunction is not exported by this interposer.");

                return false;
            }

            if (r != 0 || fn == IntPtr.Zero)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"DLSS-FG: could not bind {name} (feature {feature}, result {r}).");

                return false;
            }

            return true;
        }

        private static bool EnableReflex()
        {
            if (_reflexOn)
            {
                return true;
            }

            ReflexOptions opt = default;
            opt.Type = Guid(0xf03af81a, 0x6d0b, 0x4902, 0xa6, 0x51, 0xc4, 0x96, 0x5e, 0x21, 0x54, 0x34);
            opt.Version = 1;
            opt.Mode = ReflexModeLowLatency;

            int r = _slReflexSetOptions(in opt);
            if (r != 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"DLSS-FG: slReflexSetOptions(eLowLatency) failed ({r}); FG cannot run without Reflex.");

                return false;
            }

            _reflexOn = true;
            Logger.Info?.Print(LogClass.Gpu, "DLSS-FG: Reflex eLowLatency enabled.");

            return true;
        }

        private static bool EnableFrameGeneration(uint viewportId, in StreamlineDlss.DlssTexture depth, in StreamlineDlss.DlssTexture motion)
        {
            if (_optionsSet &&
                _optColorW == SwapchainWidth && _optColorH == SwapchainHeight &&
                _optMvecW == motion.Width && _optMvecH == motion.Height)
            {
                return true;
            }

            if (SwapchainWidth != _seenColorW || SwapchainHeight != _seenColorH ||
                motion.Width != _seenMvecW || motion.Height != _seenMvecH)
            {
                _seenColorW = SwapchainWidth;
                _seenColorH = SwapchainHeight;
                _seenMvecW = motion.Width;
                _seenMvecH = motion.Height;
                _sizeStableFrames = 0;

                if (_optionsSet)
                {
                    DisableFrameGeneration(viewportId, "input sizes changed");
                }

                return false;
            }

            if (++_sizeStableFrames < SizeStabilityFrames)
            {
                return false;
            }

            DlssgOptions opt = default;
            opt.Type = Guid(0xfac5f1cb, 0x2dfd, 0x4f36, 0xa1, 0xe6, 0x3a, 0x9e, 0x86, 0x52, 0x56, 0xc5);
            opt.Version = 5;
            opt.Mode = DlssgModeOn;
            opt.NumFramesToGenerate = ClampFramesToGenerate(viewportId);
            opt.NumBackBuffers = SwapchainImageCount;
            opt.MvecDepthWidth = motion.Width;
            opt.MvecDepthHeight = motion.Height;
            opt.ColorWidth = SwapchainWidth;
            opt.ColorHeight = SwapchainHeight;
            opt.ColorBufferFormat = SwapchainFormat;
            opt.MvecBufferFormat = motion.NativeFormat;
            opt.DepthBufferFormat = depth.NativeFormat;
            opt.BReserved15 = 2;

            ViewportHandle vp = MakeViewport(viewportId);
            int r = _slDLSSGSetOptions(in vp, in opt);
            if (r != 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"DLSS-FG: slDLSSGSetOptions(eOn) failed ({SlResult(r)}); frame generation stays OFF.");

                return false;
            }

            _optionsSet = true;
            _optColorW = SwapchainWidth;
            _optColorH = SwapchainHeight;
            _optMvecW = motion.Width;
            _optMvecH = motion.Height;
            _lastEnabledOptions = opt;

            Logger.Info?.Print(LogClass.Gpu,
                $"DLSS-FG: DLSS-G ENABLED ({_framesToGenerate + 1}x): backbuffer {SwapchainWidth}x{SwapchainHeight} fmt {SwapchainFormat}, " +
                $"mvec/depth {motion.Width}x{motion.Height} fmt {motion.NativeFormat}/{depth.NativeFormat}, " +
                $"backbuffers {SwapchainImageCount}. HUD is baked into the color (no HUD-less source in an emulator) - expect HUD warping.");

            LogState(viewportId, force: true);

            return true;
        }

        private static void DisableFrameGeneration(uint viewportId, string reason)
        {
            DlssgOptions opt = _lastEnabledOptions;
            opt.Mode = 0;

            ViewportHandle vp = MakeViewport(viewportId);
            int r = _slDLSSGSetOptions(in vp, in opt);
            _optionsSet = false;
            UiMultiplier = 1;

            Logger.Info?.Print(LogClass.Gpu,
                $"DLSS-FG: {reason} - DLSS-G set to eOff (result {SlResult(r)}); re-enabling once sizes hold for {SizeStabilityFrames} frames.");
        }

        private static string SlResult(int r)
        {
            string[] names =
            [
                "eOk", "eErrorIO", "eErrorDriverOutOfDate", "eErrorOSOutOfDate",
                "eErrorOSDisabledHWS", "eErrorDeviceNotCreated", "eErrorNoSupportedAdapterFound",
                "eErrorAdapterNotSupported", "eErrorNoPlugins", "eErrorVulkanAPI",
                "eErrorDXGIAPI", "eErrorD3DAPI", "eErrorNRDAPI", "eErrorNVAPI",
                "eErrorReflexAPI", "eErrorNGXFailed", "eErrorJSONParsing", "eErrorMissingProxy",
                "eErrorMissingResourceState", "eErrorInvalidIntegration", "eErrorMissingInputParameter",
                "eErrorNotInitialized", "eErrorComputeFailed", "eErrorInitNotCalled",
                "eErrorExceptionHandler", "eErrorInvalidParameter", "eErrorMissingConstants",
                "eErrorDuplicatedConstants", "eErrorMissingOrInvalidAPI", "eErrorCommonConstantsMissing",
                "eErrorUnsupportedInterface", "eErrorFeatureMissing", "eErrorFeatureNotSupported",
                "eErrorFeatureMissingHooks", "eErrorFeatureFailedToLoad", "eErrorFeatureWrongPriority",
                "eErrorFeatureMissingDependency", "eErrorFeatureManagerInvalidState",
                "eErrorInvalidState", "eWarnOutOfVRAM",
            ];

            return r >= 0 && r < names.Length ? $"{r}/{names[r]}" : r.ToString();
        }

        public static void OnSwapchainTeardown(uint viewportId)
        {
            if (!_optionsSet || _slDLSSGSetOptions == null)
            {
                return;
            }

            DisableFrameGeneration(viewportId, "swapchain teardown");
            _sizeStableFrames = 0;
        }

        private static Resource MakeResource(in StreamlineDlss.DlssTexture tex)
        {
            Resource r = default;
            r.Type = Guid(0x3a9d70cf, 0x2418, 0x4b72, 0x83, 0x91, 0x13, 0xf8, 0x72, 0x1c, 0x72, 0x61);
            r.Version = 1;
            r.ResType = ResourceTypeTex2d;
            r.Native = tex.Image;
            r.View = tex.View;
            r.State = tex.Layout;
            r.Width = tex.Width;
            r.Height = tex.Height;
            r.NativeFormat = tex.NativeFormat;
            r.MipLevels = 1;
            r.ArrayLayers = 1;

            return r;
        }

        private static ResourceTag MakeTag(Resource* resource, uint bufferType, uint width, uint height)
        {
            ResourceTag t = default;
            t.Type = Guid(0x4c6a5aad, 0xb445, 0x496c, 0x87, 0xff, 0x1a, 0xf3, 0x84, 0x5b, 0xe6, 0x53);
            t.Version = 1;
            t.ResourcePtr = (IntPtr)resource;
            t.BufferType = bufferType;
            t.Lifecycle = LifecycleValidUntilPresent;
            t.Extent = new Extent { Top = 0, Left = 0, Width = width, Height = height };

            return t;
        }

        private static void SetMarker(uint marker)
        {
            if (_frameToken == IntPtr.Zero)
            {
                return;
            }

            int r = _slPCLSetMarker(marker, _frameToken);
            if (r != 0 && !_markerFailedLogged)
            {
                _markerFailedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu, $"DLSS-FG: slPCLSetMarker({marker}) failed ({r}) - pacing markers unreliable (logged once).");
            }
        }

        public static void OnFrameEvaluated(
            IntPtr cmdBuffer,
            uint viewportId,
            IntPtr frameToken,
            in StreamlineDlss.DlssTexture depth,
            in StreamlineDlss.DlssTexture motion,
            in StreamlineDlss.DlssTexture hudless)
        {
            if (!_activationEnabled || frameToken == IntPtr.Zero)
            {
                return;
            }

            if (!Bind() || !EnableReflex() || !EnableFrameGeneration(viewportId, in depth, in motion))
            {
                return;
            }

            _frameToken = frameToken;

            Resource rDepth = MakeResource(in depth);
            Resource rMotion = MakeResource(in motion);

            bool hasHudless = hudless.Image != IntPtr.Zero && hudless.Width > 0 && hudless.Height > 0;
            Resource rHudless = hasHudless ? MakeResource(in hudless) : default;

            uint nTags = hasHudless ? 3u : 2u;
            ResourceTag* tags = stackalloc ResourceTag[3];
            tags[0] = MakeTag(&rDepth, BufferTypeDepth, depth.Width, depth.Height);
            tags[1] = MakeTag(&rMotion, BufferTypeMotionVectors, motion.Width, motion.Height);

            if (hasHudless)
            {
                tags[2] = MakeTag(&rHudless, BufferTypeHudLessColor, hudless.Width, hudless.Height);
            }

            ViewportHandle vp = MakeViewport(viewportId);
            int r = slSetTagForFrame(frameToken, in vp, tags, nTags, cmdBuffer);

            if (hasHudless && !_hudlessLogged)
            {
                _hudlessLogged = true;
                Logger.Info?.Print(LogClass.Gpu,
                    $"DLSS-FG: HUD-less tague {hudless.Width}x{hudless.Height} " +
                    $"(couleur {SwapchainWidth}x{SwapchainHeight}) -> slSetTagForFrame = {SlResult(r)}.");
            }
            if (r != 0)
            {
                if (!_tagFailedLogged)
                {
                    _tagFailedLogged = true;
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"DLSS-FG: slSetTagForFrame failed ({r}) - FG has no inputs. If the result hints at tagging mode, " +
                        "PreferenceFlags::eUseFrameBasedResourceTagging (1<<7, sl_core_types.h) may need to be set at slInit (coordinate: Streamline.cs).");
                }

                _frameToken = IntPtr.Zero;

                return;
            }

            SetMarker(MarkerSimulationStart);
            SetMarker(MarkerSimulationEnd);
            SetMarker(MarkerRenderSubmitStart);
        }

        public static void BeforePresent()
        {
            SetMarker(MarkerRenderSubmitEnd);
            SetMarker(MarkerPresentStart);
        }

        public static void AfterPresent(uint viewportId)
        {
            if (_frameToken == IntPtr.Zero)
            {
                return;
            }

            SetMarker(MarkerPresentEnd);

            int r = _slReflexSleep(_frameToken);
            if (r != 0 && !_markerFailedLogged)
            {
                _markerFailedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu, $"DLSS-FG: slReflexSleep failed ({r}) (logged once).");
            }

            _frameToken = IntPtr.Zero;

            _appPresentsInWindow++;
            long now = Environment.TickCount64;
            if (_windowStartMs == 0)
            {
                _windowStartMs = now;
            }
            else if (now - _windowStartMs >= 1000)
            {
                ReportCounter(viewportId, (int)(now - _windowStartMs));
                _appPresentsInWindow = 0;
                _windowStartMs = now;
            }
        }

        private static void ReportCounter(uint viewportId, int elapsedMs)
        {
            DlssgState state = default;
            state.Type = Guid(0xcc8ac8e1, 0xa179, 0x44f5, 0x97, 0xfa, 0xe7, 0x41, 0x12, 0xf9, 0xbc, 0x61);
            state.Version = 4;

            ViewportHandle vp = MakeViewport(viewportId);
            if (_slDLSSGGetState(in vp, ref state, IntPtr.Zero) != 0)
            {
                return;
            }

            double secs = elapsedMs / 1000.0;
            double appFps = _appPresentsInWindow / secs;
            uint mult = state.NumFramesActuallyPresented;
            double displayFps = appFps * mult;

            UiMultiplier = mult >= 2 ? (int)mult : 1;
            UiDisplayFps = (float)(mult >= 1 ? displayFps : appFps);
            UiArmed = true;

            if (GAL.MvppDev.Enabled)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"DLSS-FG COUNTER: app {appFps:0.0} fps x{mult} (numFramesActuallyPresented) -> display ~{displayFps:0.0} fps " +
                    $"[app {_appPresentsInWindow} presents in {elapsedMs} ms] status=0x{state.Status:X}. " +
                    (mult >= 2 ? "FG IS INTERPOLATING." : "FG enabled but NOT generating extra frames."));
            }

            if (mult >= 2)
            {
                _fgEverInterpolated = true;
                _fgX1Reports = 0;
            }
            else if (_watchdogEnabled && _optionsSet && _fgEverInterpolated)
            {
                long wnow = Environment.TickCount64;

                if (++_fgX1Reports >= 8 && wnow - _fgWatchdogLastCycleMs >= 30000)
                {
                    _fgWatchdogLastCycleMs = wnow;
                    _fgX1Reports = 0;

                    Logger.Info?.Print(LogClass.Gpu,
                        "DLSS-FG WATCHDOG: enabled mais x1 persistant (~8 s) -> cycle eOff/eOn (auto-réanimation, même remède que F11).");

                    DisableFrameGeneration(viewportId, "watchdog x1 persistant");
                    _sizeStableFrames = 0;
                }
            }
        }

        private static bool _fgEverInterpolated;
        private static int _fgX1Reports;
        private static long _fgWatchdogLastCycleMs;
        private static readonly bool _watchdogEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_DLSS_FG_WATCHDOG") != "0";

        private static bool _framesClampLogged;

        /// <summary>
        /// The requested multiplier, capped at what this GPU's DLSS-G can produce. Multi-frame
        /// generation needs an RTX 50-series part; anything older reports a maximum of one, and
        /// asking for more makes every slDLSSGSetOptions call fail with eErrorInvalidState.
        /// </summary>
        private static uint ClampFramesToGenerate(uint viewportId)
        {
            DlssgState state = default;
            state.Type = Guid(0xcc8ac8e1, 0xa179, 0x44f5, 0x97, 0xfa, 0xe7, 0x41, 0x12, 0xf9, 0xbc, 0x61);
            state.Version = 4;

            ViewportHandle vp = MakeViewport(viewportId);

            if (_slDLSSGGetState(in vp, ref state, IntPtr.Zero) != 0 || state.NumFramesToGenerateMax == 0 ||
                _framesToGenerate <= state.NumFramesToGenerateMax)
            {
                return _framesToGenerate;
            }

            if (!_framesClampLogged)
            {
                _framesClampLogged = true;

                Logger.Warning?.Print(LogClass.Gpu,
                    $"DLSS-FG: {_framesToGenerate + 1}x requested but this GPU supports at most " +
                    $"{state.NumFramesToGenerateMax + 1}x (multi-frame generation needs an RTX 50-series GPU); " +
                    $"using {state.NumFramesToGenerateMax + 1}x.");
            }

            return state.NumFramesToGenerateMax;
        }

        private static void LogState(uint viewportId, bool force)
        {
            DlssgState state = default;
            state.Type = Guid(0xcc8ac8e1, 0xa179, 0x44f5, 0x97, 0xfa, 0xe7, 0x41, 0x12, 0xf9, 0xbc, 0x61);
            state.Version = 4;

            ViewportHandle vp = MakeViewport(viewportId);
            int r = _slDLSSGGetState(in vp, ref state, IntPtr.Zero);
            if (r != 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"DLSS-FG: slDLSSGGetState failed ({r}).");

                return;
            }

            if (force || state.Status != 0 || state.NumFramesActuallyPresented != _lastLoggedGenerated)
            {
                _lastLoggedGenerated = state.NumFramesActuallyPresented;
                Logger.Info?.Print(LogClass.Gpu,
                    $"DLSS-FG state: status=0x{state.Status:X} framesPresentedSinceLastPoll={state.NumFramesActuallyPresented} " +
                    $"maxGen={state.NumFramesToGenerateMax} minDim={state.MinWidthOrHeight} vram~{state.EstimatedVRAMUsageInBytes / (1024 * 1024)}MiB " +
                    $"vsyncOK={state.BIsVsyncSupportAvailable}.");
            }
        }
    }
}
