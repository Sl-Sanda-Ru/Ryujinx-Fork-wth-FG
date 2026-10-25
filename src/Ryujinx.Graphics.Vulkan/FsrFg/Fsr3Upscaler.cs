using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan.FsrFg
{
    /// <summary>
    /// AMD FSR 3.1 temporal upscaler (<c>ffxProvider_FSR3Upscale</c> in amd_fidelityfx_vk.dll).
    ///
    /// <para>Takes the same inputs the DLSS pipeline hands Streamline -- colour, depth and motion
    /// vectors at render resolution plus the injected camera jitter -- so it can stand in for
    /// <c>StreamlineDlss.Evaluate</c> without the pipeline changing. The conventions are therefore
    /// the pipeline's: motion vectors in render-resolution pixels pointing to the previous frame,
    /// jitter in pixels, depth not inverted, every image in VK_IMAGE_LAYOUT_GENERAL.</para>
    /// </summary>
    class Fsr3Upscaler : IDisposable
    {
        private const ulong CreateContextDescTypeUpscale = 0x00010000u;
        private const ulong DispatchDescTypeUpscale = 0x00010001u;

        private const uint EnableHighDynamicRange = 1 << 0;
        private const uint EnableAutoExposure = 1 << 5;
        private const uint EnableDebugChecking = 1 << 7;

        /// <summary>
        /// Has FSR validate its inputs and report what it rejects. Off by default: the checks cost
        /// time, and without a callback FSR's complaints go nowhere.
        /// </summary>
        private static readonly bool _debugChecking = Environment.GetEnvironmentVariable("RYUJINX_FSR_UPSCALE_DEBUG") == "1";

        private static int _messageCount;

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static void OnMessage(uint type, IntPtr message)
        {
            try
            {
                // Capped: a per-frame complaint would otherwise flood the log.
                if (Interlocked.Increment(ref _messageCount) > 50)
                {
                    return;
                }

                string text = Marshal.PtrToStringUni(message) ?? string.Empty;

                if (type == 0)
                {
                    Logger.Error?.Print(LogClass.Gpu, $"FSR3 upscale (FidelityFX): {text}");
                }
                else
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"FSR3 upscale (FidelityFX): {text}");
                }
            }
            catch (Exception)
            {
                // Never let an exception cross back into native code.
            }
        }

        // Pipeline-to-FSR sign corrections, should a game's motion or jitter turn out mirrored.
        private static readonly float _jitterSignX = ParseFloat("RYUJINX_FSR_UPSCALE_JITTER_SIGNX", 1f);
        private static readonly float _jitterSignY = ParseFloat("RYUJINX_FSR_UPSCALE_JITTER_SIGNY", 1f);
        private static readonly float _mvScaleX = ParseFloat("RYUJINX_FSR_UPSCALE_MV_SCALEX", 1f);
        private static readonly float _mvScaleY = ParseFloat("RYUJINX_FSR_UPSCALE_MV_SCALEY", 1f);

        /// <summary>Opt-in while the integration settles: replaces the DLSS evaluate when set.</summary>
        public static bool Requested = Environment.GetEnvironmentVariable("RYUJINX_FSR_UPSCALE") == "1";

        /// <summary>
        /// Re-reads the fields above that come from the settings profile, so a changed upscaler
        /// setting applies at the next game start without restarting the app.
        /// </summary>
        internal static void ReloadProfileSettings()
        {
            {
                Requested = Environment.GetEnvironmentVariable("RYUJINX_FSR_UPSCALE") == "1";
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CreateContextDescUpscale
        {
            public FsrFgApi.FfxApiHeader Header;
            public uint Flags;
            public FsrFgApi.FfxApiDimensions2D MaxRenderSize;
            public FsrFgApi.FfxApiDimensions2D MaxUpscaleSize;
            public IntPtr FpMessage;
        }

        // C 'bool' is one byte; Sequential layout pads the floats after it the way the compiler does.
        [StructLayout(LayoutKind.Sequential)]
        private struct DispatchDescUpscale
        {
            public FsrFgApi.FfxApiHeader Header;
            public IntPtr CommandList;
            public FsrFgApi.FfxApiResource Color;
            public FsrFgApi.FfxApiResource Depth;
            public FsrFgApi.FfxApiResource MotionVectors;
            public FsrFgApi.FfxApiResource Exposure;
            public FsrFgApi.FfxApiResource Reactive;
            public FsrFgApi.FfxApiResource TransparencyAndComposition;
            public FsrFgApi.FfxApiResource Output;
            public FsrFgApi.FfxApiFloatCoords2D JitterOffset;
            public FsrFgApi.FfxApiFloatCoords2D MotionVectorScale;
            public FsrFgApi.FfxApiDimensions2D RenderSize;
            public FsrFgApi.FfxApiDimensions2D UpscaleSize;
            public byte EnableSharpening;
            public float Sharpness;
            public float FrameTimeDelta;
            public float PreExposure;
            public byte Reset;
            public float CameraNear;
            public float CameraFar;
            public float CameraFovAngleVertical;
            public float ViewSpaceToMetersFactor;
            public uint Flags;
        }

        private readonly VulkanRenderer _gd;
        private readonly Device _device;

        // Unmanaged for the same reason as FsrFgFrameGen: ffx-api writes through this address.
        private IntPtr _contextStorage;

        private uint _maxRenderWidth;
        private uint _maxRenderHeight;
        private uint _upscaleWidth;
        private uint _upscaleHeight;
        private bool _hdr;
        private bool _createFailed;

        private long _lastDispatchMs;
        private bool _dispatchFailLogged;
        private bool _sizeMismatchLogged;
        private bool _firstDispatchLogged;
        private int _dispatchCount;
        private int _jitterLogs;

        public Fsr3Upscaler(VulkanRenderer gd, Device device)
        {
            _gd = gd;
            _device = device;
        }

        private static float ParseFloat(string name, float fallback)
        {
            return float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value) ? value : fallback;
        }

        private unsafe ref IntPtr ContextRef
        {
            get
            {
                if (_contextStorage == IntPtr.Zero)
                {
                    _contextStorage = (IntPtr)NativeMemory.AllocZeroed((nuint)sizeof(IntPtr));
                }

                return ref System.Runtime.CompilerServices.Unsafe.AsRef<IntPtr>((void*)_contextStorage);
            }
        }

        private unsafe bool HasContext => _contextStorage != IntPtr.Zero && *(IntPtr*)_contextStorage != IntPtr.Zero;

        /// <summary>
        /// Records the upscale of <paramref name="color"/> into <paramref name="output"/>. Returns
        /// false when FSR could not run, so the caller can fall back as it would for DLSS.
        /// </summary>
        public unsafe bool Evaluate(
            CommandBufferScoped cbs,
            bool reset,
            float jitterX,
            float jitterY,
            in Dlss.StreamlineDlss.DlssTexture color,
            in Dlss.StreamlineDlss.DlssTexture output,
            in Dlss.StreamlineDlss.DlssTexture depth,
            in Dlss.StreamlineDlss.DlssTexture motion,
            bool hdr)
        {
            if (!EnsureContext(color.Width, color.Height, output.Width, output.Height, hdr))
            {
                return false;
            }

            if (!_sizeMismatchLogged &&
                (depth.Width != color.Width || depth.Height != color.Height || motion.Width != color.Width || motion.Height != color.Height))
            {
                // FSR reads depth and motion at render resolution with the colour's pixel
                // coordinates, so a mismatch shows as misaligned motion rather than an error.
                _sizeMismatchLogged = true;

                Logger.Warning?.Print(LogClass.Gpu,
                    $"FSR3 upscale: input sizes differ (color {color.Width}x{color.Height}, depth {depth.Width}x{depth.Height}, " +
                    $"motion {motion.Width}x{motion.Height}); FSR expects them equal.");
            }

            long now = Environment.TickCount64;
            float frameTimeDelta = _lastDispatchMs == 0 ? 16.667f : Math.Clamp(now - _lastDispatchMs, 1, 1000);
            _lastDispatchMs = now;

            DispatchDescUpscale desc = new()
            {
                Header = new FsrFgApi.FfxApiHeader(DispatchDescTypeUpscale),
                CommandList = (IntPtr)cbs.CommandBuffer.Handle,
                Color = Wrap(color, FsrFgApi.ResourceUsage.ReadOnly),
                Depth = Wrap(depth, FsrFgApi.ResourceUsage.DepthTarget),
                MotionVectors = Wrap(motion, FsrFgApi.ResourceUsage.ReadOnly),
                Exposure = FsrFgApi.EmptyResource(),
                Reactive = FsrFgApi.EmptyResource(),
                TransparencyAndComposition = FsrFgApi.EmptyResource(),
                Output = Wrap(output, FsrFgApi.ResourceUsage.Uav),
                JitterOffset = new FsrFgApi.FfxApiFloatCoords2D { X = jitterX * _jitterSignX, Y = jitterY * _jitterSignY },
                MotionVectorScale = new FsrFgApi.FfxApiFloatCoords2D { X = _mvScaleX, Y = _mvScaleY },
                RenderSize = new FsrFgApi.FfxApiDimensions2D { Width = color.Width, Height = color.Height },
                UpscaleSize = new FsrFgApi.FfxApiDimensions2D { Width = output.Width, Height = output.Height },
                EnableSharpening = 0,
                Sharpness = 0f,
                FrameTimeDelta = frameTimeDelta,
                PreExposure = 1f,
                Reset = reset ? (byte)1 : (byte)0,
                // Placeholders matching what the pipeline gives DLSS; nothing publishes the real camera.
                CameraNear = 0.1f,
                CameraFar = 10000f,
                CameraFovAngleVertical = 1.0f,
                ViewSpaceToMetersFactor = 1f,
                Flags = 0,
            };

            FsrFgApi.ReturnCode code = FsrFgApi.Dispatch(ref ContextRef, (IntPtr)(&desc));

            if (code != FsrFgApi.ReturnCode.Ok)
            {
                if (!_dispatchFailLogged)
                {
                    _dispatchFailLogged = true;
                    Logger.Warning?.Print(LogClass.Gpu, $"FSR3 upscale: dispatch failed ({code}); falling back.");
                }

                return false;
            }

            // Jitter of zero on every frame leaves FSR nothing to accumulate sub-pixel detail from,
            // which reads as blur; sample it a few times so the log shows whether it moves.
            if (++_dispatchCount % 120 == 0 && _jitterLogs < 5)
            {
                _jitterLogs++;
                Logger.Info?.Print(LogClass.Gpu,
                    $"FSR3 upscale: frame {_dispatchCount} jitter ({desc.JitterOffset.X:0.000},{desc.JitterOffset.Y:0.000}), " +
                    $"render {color.Width}x{color.Height} -> {output.Width}x{output.Height}.");
            }

            if (!_firstDispatchLogged)
            {
                _firstDispatchLogged = true;
                Logger.Info?.Print(LogClass.Gpu,
                    $"FSR3 upscale: first dispatch OK, render {color.Width}x{color.Height} -> {output.Width}x{output.Height}, " +
                    $"jitter ({desc.JitterOffset.X:0.000},{desc.JitterOffset.Y:0.000}), hdr={hdr}.");
            }

            return true;
        }

        private static FsrFgApi.FfxApiResource Wrap(in Dlss.StreamlineDlss.DlssTexture tex, FsrFgApi.ResourceUsage usage)
        {
            // UnorderedAccess is the state that maps to GENERAL, the layout the pipeline keeps every
            // image in; FidelityFX restores imported images to the declared state afterwards.
            return FsrFgApi.MakeImageResource(
                tex.Image,
                (Format)tex.NativeFormat,
                tex.Width,
                tex.Height,
                usage,
                FsrFgApi.ResourceState.UnorderedAccess);
        }

        private unsafe bool EnsureContext(uint renderWidth, uint renderHeight, uint upscaleWidth, uint upscaleHeight, bool hdr)
        {
            bool fits = HasContext &&
                renderWidth <= _maxRenderWidth && renderHeight <= _maxRenderHeight &&
                upscaleWidth == _upscaleWidth && upscaleHeight == _upscaleHeight && hdr == _hdr;

            if (fits)
            {
                return true;
            }

            bool sameRequest = upscaleWidth == _upscaleWidth && upscaleHeight == _upscaleHeight && hdr == _hdr &&
                renderWidth <= _maxRenderWidth && renderHeight <= _maxRenderHeight;

            if (_createFailed && sameRequest)
            {
                return false;
            }

            if (!FsrFgApi.Load(FsrFgApi.GetDefaultLibraryFolder()))
            {
                if (!_createFailed)
                {
                    Logger.Warning?.Print(LogClass.Gpu, "FSR3 upscale: amd_fidelityfx_vk.dll is not in the fsr folder.");
                }

                _createFailed = true;

                return false;
            }

            DestroyContext();

            // Room for the render size to grow up to the output, so the resolution scale can change
            // without a rebuild every time.
            _maxRenderWidth = Math.Max(renderWidth, upscaleWidth);
            _maxRenderHeight = Math.Max(renderHeight, upscaleHeight);
            _upscaleWidth = upscaleWidth;
            _upscaleHeight = upscaleHeight;
            _hdr = hdr;

            if (!_gd.Api.Context.TryGetProcAddress("vkGetDeviceProcAddr", out nint getDeviceProcAddr) || getDeviceProcAddr == 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, "FSR3 upscale: could not resolve vkGetDeviceProcAddr.");
                _createFailed = true;

                return false;
            }

            FsrFgApi.CreateBackendVkDesc backend = new()
            {
                Header = new FsrFgApi.FfxApiHeader(FsrFgApi.CreateContextDescTypeBackendVk),
                VkDevice = _device.Handle,
                VkPhysicalDevice = _gd.PhysicalDevice.Handle,
                VkDeviceProcAddr = FsrFgApi.MakeDeviceProcAddrShim(getDeviceProcAddr),
            };

            CreateContextDescUpscale desc = new()
            {
                Header = new FsrFgApi.FfxApiHeader(CreateContextDescTypeUpscale) { PNext = (IntPtr)(&backend) },
                Flags = (hdr ? EnableHighDynamicRange | EnableAutoExposure : 0) | (_debugChecking ? EnableDebugChecking : 0),
                MaxRenderSize = new FsrFgApi.FfxApiDimensions2D { Width = _maxRenderWidth, Height = _maxRenderHeight },
                MaxUpscaleSize = new FsrFgApi.FfxApiDimensions2D { Width = upscaleWidth, Height = upscaleHeight },
                FpMessage = _debugChecking ? (IntPtr)(delegate* unmanaged[Cdecl]<uint, IntPtr, void>)&OnMessage : IntPtr.Zero,
            };

            FsrFgApi.ReturnCode code = FsrFgApi.CreateContext(ref ContextRef, (IntPtr)(&desc));

            if (code != FsrFgApi.ReturnCode.Ok || !HasContext)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"FSR3 upscale: context creation failed ({code}).");
                _createFailed = true;

                return false;
            }

            _createFailed = false;
            _firstDispatchLogged = false;

            Logger.Info?.Print(LogClass.Gpu,
                $"FSR3 upscale: context created, max render {_maxRenderWidth}x{_maxRenderHeight}, " +
                $"output {upscaleWidth}x{upscaleHeight}, hdr={hdr}, debug checking={_debugChecking}.");

            return true;
        }

        private void DestroyContext()
        {
            if (!HasContext)
            {
                return;
            }

            // The context's resources are only used on the game queue, so draining that queue is
            // enough, and it stays safe while frame generation's threads use their own queues.
            FsrFgContext.WaitGameQueueIdle(_gd);
            FsrFgApi.DestroyContext(ref ContextRef);
        }

        public unsafe void Dispose()
        {
            DestroyContext();

            if (_contextStorage != IntPtr.Zero)
            {
                NativeMemory.Free((void*)_contextStorage);
                _contextStorage = IntPtr.Zero;
            }
        }
    }
}
