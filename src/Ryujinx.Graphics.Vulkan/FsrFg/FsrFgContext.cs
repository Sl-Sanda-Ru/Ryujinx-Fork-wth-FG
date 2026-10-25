using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan.FsrFg
{
    /// <summary>
    /// Owns the FSR3 frame generation pieces and the queue plumbing they need, so the renderer and
    /// window only deal with one object. Mirrors the role <c>DlssFrameGenHooks</c> plays for
    /// DLSS-FG, but FidelityFX creates the swapchain itself rather than interposing
    /// <c>vkCreateSwapchainKHR</c>, so <see cref="ReplaceSwapchain"/> is called instead.
    /// </summary>
    class FsrFgContext : IDisposable
    {
        // Static because the FidelityFX submit callbacks are unmanaged and carry no user context.
        private static Vk _submitApi;
        private static Queue _gameQueue;
        private static Lock _gameQueueLock;
        private static Queue _asyncQueue;
        private static Lock _asyncQueueLock;

        // The family the three frame generation roles were taken from, which is the graphics family
        // on hardware that can spare four queues there and a compute family otherwise.
        private uint _fgFamilyIndex;

        /// <summary>
        /// Whether to hand FidelityFX no async compute queue at all. Its init treats that queue as
        /// optional, and the frame generation context is already created with async workloads off,
        /// so the queue was only ever giving it a submit path we do not want used. Across families
        /// that path is also the least trodden one in FidelityFX, so it is off by default there.
        /// </summary>
        private bool _noAsyncQueue;

        // FidelityFX needs the camera planes and field of view behind the projection the Mvpp probes
        // recover, but nothing in the pipeline publishes the projection separately yet. Until it
        // does these are tunable, so they can be dialled in against a running game.
        private static readonly float _cameraNear = ReadFloat("RYUJINX_FSR_FG_NEAR", 0.1f);
        private static readonly float _cameraFar = ReadFloat("RYUJINX_FSR_FG_FAR", 1000.0f);
        private static readonly float _cameraFov = ReadFloat("RYUJINX_FSR_FG_FOV_DEG", 60.0f) * MathF.PI / 180.0f;

        private readonly VulkanRenderer _gd;

        private FsrFgSwapchain _swapchain;
        private FsrFgFrameGen _frameGen;

        private ulong _swapchainHandle;
        private ulong _frameId;

        private int _fgWidth;
        private int _fgHeight;
        private bool _fgHdr;

        // Set when creation failed for the current size and HDR state, so it is not retried every
        // present; a size or HDR change clears it.
        private bool _fgCreateFailed;
        private long _lastFrameMs;
        private bool _prepareFailLogged;
        private bool _fgContextSkipLogged;
        private bool _flowModeLogged;
        private long _framesFed;
        private long _lastFramesFed;
        private long _presentLogMs;
        private ulong _lastPresentCount;
        private long _telemetryMs;
        private long _telemetryFramesFed;
        private ulong _telemetryPresentCount;
        private bool _fgDisplayResMvs;

        // Stand-ins for depth and motion vectors when the frame has none to give. FidelityFX skips
        // the descriptor write for an empty resource, so its shaders would read a binding that was
        // never written -- tolerated on NVIDIA, a GPU fault and a lost device on Intel.
        private TextureView _zeroDepth;
        private TextureView _zeroMotion;
        private int _zeroWidth;
        private int _zeroHeight;

        private static float ReadFloat(string name, float fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);

            return float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float parsed) ? parsed : fallback;
        }

        /// <summary>Swapchain entry points to use while frame generation is armed.</summary>
        public KhrSwapchain SwapchainProxy => _swapchain?.SwapchainProxy;

        public bool IsArmed => _swapchain != null;

        public bool HasFrameGen => _frameGen != null && _frameGen.IsValid;

        private FsrFgContext(VulkanRenderer gd)
        {
            _gd = gd;
        }

        /// <summary>
        /// Loads the FidelityFX library. Returns null when it is absent, which is the normal case
        /// for users who have not supplied it.
        /// </summary>
        public static FsrFgContext TryCreate(VulkanRenderer gd, string libraryFolder = null)
        {
            string folder = libraryFolder ?? FsrFgApi.GetDefaultLibraryFolder();

            if (!FsrFgApi.Load(folder))
            {
                return null;
            }

            // FrameInterpolationSwapChainVK::init rejects any repeated handle across its four queue
            // roles, so without four distinct queues there is no point arming.
            // The three non-game roles come from a compute family when the graphics family has too
            // few queues. FidelityFX allows that: its only family rule is that each index exists.
            bool crossFamily = gd.FsrFgQueueFamilyIndex != uint.MaxValue;
            uint fgFamilyIndex = crossFamily ? gd.FsrFgQueueFamilyIndex : gd.QueueFamilyIndex;
            Queue asyncQueue = crossFamily ? gd.FsrFgAsyncQueue : gd.BackgroundQueue;
            Lock asyncQueueLock = crossFamily ? gd.FsrFgAsyncQueueLock : gd.BackgroundQueueLock;

            if (asyncQueue.Handle == 0 || gd.FsrFgPresentQueue.Handle == 0 || gd.FsrFgAcquireQueue.Handle == 0)
            {
                // Which families exist decides whether this is worth working around at all, and the
                // warning on its own leaves no way to tell. Report them so the refusal is actionable
                // rather than a dead end.
                QueueFamilyProperties[] families = gd.QueueFamilyProperties;
                System.Text.StringBuilder report = new();

                for (int i = 0; i < families.Length; i++)
                {
                    report.Append(i == 0 ? string.Empty : ", ");
                    report.Append($"[{i}] count={families[i].QueueCount} flags={families[i].QueueFlags}");

                    // FidelityFX calls vkQueuePresentKHR on the queue it is given for the present
                    // role, so only a family that can present to this surface can fill it. That
                    // decides whether the four roles can be spread across families at all.
                    if (gd.SurfaceApi != null)
                    {
                        gd.SurfaceApi.GetPhysicalDeviceSurfaceSupport(
                            gd.PhysicalDevice, (uint)i, gd.Surface, out Silk.NET.Core.Bool32 canPresent);

                        report.Append(canPresent ? " present=yes" : " present=no");
                    }

                    if (i == gd.QueueFamilyIndex)
                    {
                        report.Append(" (graphics)");
                    }
                }

                Logger.Warning?.Print(LogClass.Gpu,
                    "FSR-FG: needs four distinct queues in the graphics family and fewer were available; not arming. " +
                    $"Queue families: {report}.");

                return null;
            }

            // FidelityFX calls vkQueuePresentKHR on the queue given for the present role, so that
            // family has to be able to present to this surface. Nothing guarantees a compute family
            // can, and finding out after handing over the swapchain would be far more expensive --
            // ffxReplaceSwapchainForFrameinterpolationVK destroys it even when it fails.
            if (crossFamily && gd.SurfaceApi != null)
            {
                gd.SurfaceApi.GetPhysicalDeviceSurfaceSupport(
                    gd.PhysicalDevice, fgFamilyIndex, gd.Surface, out Silk.NET.Core.Bool32 canPresent);

                if (!canPresent)
                {
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"FSR-FG: queue family {fgFamilyIndex} cannot present to this surface, and the " +
                        "graphics family has too few queues to supply all four roles; not arming.");

                    return null;
                }
            }

            _submitApi = gd.Api;
            _gameQueue = gd.Queue;
            _gameQueueLock = gd.QueueLock;
            _asyncQueue = asyncQueue;
            _asyncQueueLock = asyncQueueLock;

            // Left on for the single-family case, which is known to work and is not worth disturbing.
            bool noAsync = Environment.GetEnvironmentVariable("RYUJINX_FSR_FG_NOASYNC") switch
            {
                "1" => true,
                "0" => false,
                _ => crossFamily,
            };

            if (noAsync)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    "FSR-FG: handing FidelityFX no async compute queue; interpolation submits on the game queue " +
                    "(set RYUJINX_FSR_FG_NOASYNC=0 to supply one).");
            }

            return new FsrFgContext(gd) { _fgFamilyIndex = fgFamilyIndex, _noAsyncQueue = noAsync };
        }

        // FidelityFX submits its own work on the queues it is given. Those queues are also used by
        // the renderer, so submission is serialised through the same locks the renderer uses.
        private static int _gameSubmitCount;
        private static int _asyncSubmitCount;
        private static int _submitFailureCount;

        /// <summary>
        /// FidelityFX ignores the result of these submits, and a failed one never signals the
        /// command list it carried, which leaves that list busy for good: enough of them and the
        /// pool runs dry and FidelityFX dereferences null. Failures are logged because nothing else
        /// will report them.
        /// </summary>
        private static unsafe Result LogSubmitResult(string queueName, Result result, uint submitCount, SubmitInfo* submits)
        {
            if (result != Result.Success && Interlocked.Increment(ref _submitFailureCount) <= 10)
            {
                uint waits = submitCount > 0 ? submits[0].WaitSemaphoreCount : 0;
                uint signals = submitCount > 0 ? submits[0].SignalSemaphoreCount : 0;

                Logger.Error?.Print(LogClass.Gpu,
                    $"FSR-FG: {queueName} queue submit failed ({result}) on managed thread " +
                    $"{Environment.CurrentManagedThreadId}: {submitCount} submit(s), {waits} wait(s), {signals} signal(s).");
            }

            return result;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe Result SubmitGameQueue(uint submitCount, SubmitInfo* submits, Fence fence)
        {
            try
            {
                // FidelityFX composes on its own present thread once the present queue sits in
                // another family, so each of these may arrive on a thread the runtime has never
                // seen. The thread id is what distinguishes that from a same-thread submit.
                int gameSubmits = Interlocked.Increment(ref _gameSubmitCount);

                if (gameSubmits <= 6)
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"FSR-FG: game queue submit #{gameSubmits} on managed thread " +
                        $"{Environment.CurrentManagedThreadId}, {submitCount} submit(s).");
                }

                lock (_gameQueueLock)
                {
                    return LogSubmitResult("game", _submitApi.QueueSubmit(_gameQueue, submitCount, submits, fence), submitCount, submits);
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"FSR-FG: game queue submit threw: {ex}");

                return Result.ErrorUnknown;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe Result SubmitAsyncQueue(uint submitCount, SubmitInfo* submits, Fence fence)
        {
            try
            {
                int asyncSubmits = Interlocked.Increment(ref _asyncSubmitCount);

                if (asyncSubmits <= 6)
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"FSR-FG: async queue submit #{asyncSubmits} on managed thread " +
                        $"{Environment.CurrentManagedThreadId}, {submitCount} submit(s).");
                }

                lock (_asyncQueueLock)
                {
                    return LogSubmitResult("async", _submitApi.QueueSubmit(_asyncQueue, submitCount, submits, fence), submitCount, submits);
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"FSR-FG: async queue submit threw: {ex}");

                return Result.ErrorUnknown;
            }
        }

        private unsafe FsrFgApi.VkQueueInfoFfxApi MakeQueueInfo(Queue queue, bool async, bool serialise = true, uint? familyIndex = null)
        {
            IntPtr submitFunc = !serialise
                ? IntPtr.Zero
                : async
                    ? (IntPtr)(delegate* unmanaged[Cdecl]<uint, SubmitInfo*, Fence, Result>)&SubmitAsyncQueue
                    : (IntPtr)(delegate* unmanaged[Cdecl]<uint, SubmitInfo*, Fence, Result>)&SubmitGameQueue;

            return new FsrFgApi.VkQueueInfoFfxApi
            {
                Queue = queue.Handle,
                FamilyIndex = familyIndex ?? _gd.QueueFamilyIndex,
                SubmitFunc = submitFunc,
            };
        }

        /// <summary>
        /// Hands <paramref name="swapchain"/> to FidelityFX, which destroys it and returns a frame
        /// generation swapchain in its place.
        /// </summary>
        public bool ReplaceSwapchain(Device device, SwapchainCreateInfoKHR createInfo, ref SwapchainKHR swapchain)
        {
            // An existing context can recreate its own swapchain in place; FidelityFX supplies a
            // replacement for vkCreateSwapchainKHR for exactly that case.
            if (_swapchain != null)
            {
                // vkCreateSwapchainFFXAPI refuses while the context still holds a swapchain, so the
                // old one is destroyed through the replacement first. That also destroys the real
                // swapchain behind it, so nothing is left to retire: init() copies the create info
                // straight into vkCreateSwapchainKHR, and the FidelityFX handle is not a VkSwapchainKHR.
                _swapchain.DestroySwapchain(device, swapchain);
                swapchain = default;
                createInfo.OldSwapchain = default;

                Result inPlace = _swapchain.CreateSwapchain(device, createInfo, ref swapchain);

                if (inPlace == Result.Success)
                {
                    _swapchainHandle = swapchain.Handle;
                    RestartPresentTelemetry();

                    return true;
                }

                // The context owns the swapchain behind this handle and destroys it on dispose, so
                // there is nothing left to hand to a fresh replacement. The caller falls back to a
                // native swapchain, which the next recreate hands over again.
                Logger.Warning?.Print(LogClass.Gpu,
                    $"FSR-FG: in-place swapchain recreate failed ({inPlace}); presenting natively until the next recreate.");

                _frameGen?.Dispose();
                _frameGen = null;
                _fgWidth = 0;
                _fgHeight = 0;
                _fgCreateFailed = false;

                _swapchain.Dispose();
                _swapchain = null;
                _swapchainHandle = 0;

                swapchain = default;

                return false;
            }

            Logger.Info?.Print(LogClass.Gpu,
                $"FSR-FG: game family {_gd.QueueFamilyIndex}, fg family {_fgFamilyIndex}, game=0x{_gd.Queue.Handle:X}, " +
                $"async=0x{_asyncQueue.Handle:X}, present=0x{_gd.FsrFgPresentQueue.Handle:X}, " +
                $"acquire=0x{_gd.FsrFgAcquireQueue.Handle:X}, format={createInfo.ImageFormat}, " +
                $"presentMode={createInfo.PresentMode}, images={createInfo.MinImageCount}, " +
                $"extent={createInfo.ImageExtent.Width}x{createInfo.ImageExtent.Height}.");

            // The present and acquire queues are used only by FidelityFX, so they need no submit
            // callback; the other two are shared with the renderer and must be serialised.
            // No retry on failure: ffxReplaceSwapchainForFrameinterpolationVK destroys the swapchain
            // it was given even when it fails, so a second attempt would use a freed handle.
            _swapchain = FsrFgSwapchain.TryCreate(
                _gd.Api,
                _gd.PhysicalDevice,
                device,
                ref swapchain,
                createInfo,
                MakeQueueInfo(_gd.Queue, async: false),
                _noAsyncQueue
                    ? default
                    : MakeQueueInfo(_asyncQueue, async: true, familyIndex: _fgFamilyIndex),
                MakeQueueInfo(_gd.FsrFgPresentQueue, async: false, serialise: false, familyIndex: _fgFamilyIndex),
                MakeQueueInfo(_gd.FsrFgAcquireQueue, async: false, serialise: false, familyIndex: _fgFamilyIndex));

            if (_swapchain == null)
            {
                return false;
            }

            _swapchainHandle = swapchain.Handle;
            RestartPresentTelemetry();

            return true;
        }

        /// <summary>
        /// Present counts are per swapchain, so a new one restarts the count and the old baseline
        /// would compare frames fed on one swapchain against presents on another.
        /// </summary>
        private void RestartPresentTelemetry()
        {
            _telemetryMs = 0;
            _presentLogMs = 0;
        }

        /// <summary>Creates the frame generation effect for the current swapchain dimensions.</summary>
        public bool CreateFrameGen(
            Device device,
            uint displayWidth,
            uint displayHeight,
            Format backBufferFormat,
            bool hdr,
            bool depthInverted,
            bool allowAsyncWorkloads,
            bool displayResolutionMotionVectors)
        {
            _frameGen?.Dispose();

            // DebugChecking (1<<6) was added in v1.1.4 and this provider reports 1.1.3, so setting
            // it here would be an undefined flag bit.
            FsrFgApi.FrameGenerationFlags flags = FsrFgApi.FrameGenerationFlags.None;

            if (displayResolutionMotionVectors)
            {
                flags |= FsrFgApi.FrameGenerationFlags.DisplayResolutionMotionVectors;
            }

            if (hdr)
            {
                flags |= FsrFgApi.FrameGenerationFlags.HighDynamicRange;
            }

            if (depthInverted)
            {
                flags |= FsrFgApi.FrameGenerationFlags.DepthInverted;
            }

            if (allowAsyncWorkloads)
            {
                flags |= FsrFgApi.FrameGenerationFlags.EnableAsyncWorkloadSupport;
            }

            // Opt-in gate from when creating this context access-violated inside
            // amd_fidelityfx_vk.dll. That no longer reproduces, and the UI setting opens the gate;
            // it remains so a launcher can arm the swapchain alone.
            if (Environment.GetEnvironmentVariable("RYUJINX_FSR_FG_CONTEXT") != "1")
            {
                if (!_fgContextSkipLogged)
                {
                    _fgContextSkipLogged = true;

                    Logger.Info?.Print(LogClass.Gpu,
                        "FSR-FG: frame generation context creation is gated off (set RYUJINX_FSR_FG_CONTEXT=1 to attempt it); " +
                        "the FidelityFX swapchain is armed but will not interpolate.");
                }

                return false;
            }

            // FidelityFX's Vulkan backend calls through this pointer; a null one is an access
            // violation inside ffxCreateContext rather than an error return.
            IntPtr getDeviceProcAddr = IntPtr.Zero;

            if (_gd.Api.Context.TryGetProcAddress("vkGetDeviceProcAddr", out nint resolved))
            {
                getDeviceProcAddr = resolved;
            }

            Logger.Info?.Print(LogClass.Gpu, $"FSR-FG: vkGetDeviceProcAddr=0x{getDeviceProcAddr:X}");

            // ffxGetScratchMemorySizeVK calls exactly this to size its scratch buffer. Making the
            // call ourselves proves whether the physical device handle we hand over is usable.
            unsafe
            {
                uint extensionCount = 0;
                Result extResult = _gd.Api.EnumerateDeviceExtensionProperties(
                    _gd.PhysicalDevice, (byte*)null, &extensionCount, null);

                Logger.Info?.Print(LogClass.Gpu,
                    $"FSR-FG: vkEnumerateDeviceExtensionProperties -> {extResult}, count={extensionCount} " +
                    $"(physical=0x{_gd.PhysicalDevice.Handle:X}).");
            }

            if (getDeviceProcAddr == IntPtr.Zero)
            {
                Logger.Warning?.Print(LogClass.Gpu,
                    "FSR-FG: could not resolve vkGetDeviceProcAddr; frame generation cannot be created.");

                return false;
            }

            _frameGen = FsrFgFrameGen.TryCreate(
                _gd.PhysicalDevice,
                device,
                FsrFgApi.MakeDeviceProcAddrShim(getDeviceProcAddr),
                displayWidth,
                displayHeight,
                displayWidth,
                displayHeight,
                backBufferFormat,
                flags);

            Logger.Info?.Print(LogClass.Gpu,
                $"FSR-FG: {FsrFgApi.ProcAddrRedirects} KHR entry point(s) redirected to their core names.");

            return _frameGen != null;
        }

        /// <summary>
        /// Advances the frame counter and updates frame generation state. Call once per rendered
        /// frame, before <see cref="Prepare"/>.
        /// </summary>
        public ulong BeginFrame(bool enabled, bool allowAsyncWorkloads, uint dispatchFlags, int width, int height)
        {
            _frameId++;

            if (!HasFrameGen)
            {
                return _frameId;
            }

            FsrFgApi.FfxApiRect2D rect = new()
            {
                Left = 0,
                Top = 0,
                Width = width,
                Height = height,
            };

            // swapChain is the raw VkSwapchainKHR handle; ffxGetSwapchainVK is a plain cast.
            FsrFgApi.ReturnCode code = _frameGen.Configure(
                (IntPtr)_swapchainHandle,
                enabled,
                allowAsyncWorkloads,
                dispatchFlags,
                _frameId,
                FsrFgApi.EmptyResource(),
                rect);

            if (code != FsrFgApi.ReturnCode.Ok)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"FSR-FG: configure failed ({code}).");
            }

            return _frameId;
        }

        /// <summary>
        /// Records the preparation pass. <paramref name="depth"/> and
        /// <paramref name="motionVectors"/> come from the Mvpp reprojection passes; the camera
        /// values must match the matrices those passes recovered.
        /// </summary>
        public FsrFgApi.ReturnCode Prepare(
            CommandBufferScoped cbs,
            int renderWidth,
            int renderHeight,
            float jitterX,
            float jitterY,
            float motionVectorScaleX,
            float motionVectorScaleY,
            float frameTimeDeltaMs,
            float cameraNear,
            float cameraFar,
            float cameraFovAngleVertical,
            FsrFgApi.FfxApiResource depth,
            FsrFgApi.FfxApiResource motionVectors,
            uint dispatchFlags = 0)
        {
            if (!HasFrameGen)
            {
                return FsrFgApi.ReturnCode.Error;
            }

            return _frameGen.Prepare(
                cbs.CommandBuffer.Handle,
                _frameId,
                dispatchFlags,
                (uint)renderWidth,
                (uint)renderHeight,
                jitterX,
                jitterY,
                motionVectorScaleX,
                motionVectorScaleY,
                frameTimeDeltaMs,
                cameraNear,
                cameraFar,
                cameraFovAngleVertical,
                depth,
                motionVectors);
        }

        /// <summary>
        /// Zero-filled depth and motion vectors at render size: depth at the far plane (inverted
        /// depth is in use), and no motion, which leaves FidelityFX's optical flow to find it.
        /// </summary>
        private unsafe void EnsureZeroInputs(Device device, CommandBufferScoped cbs, int width, int height)
        {
            if (_zeroMotion != null && _zeroWidth == width && _zeroHeight == height)
            {
                return;
            }

            // No wait: disposal is deferred until the command buffers using the old pair finish. A
            // vkDeviceWaitIdle here would also be illegal, since it requires every queue to be
            // externally synchronised and FidelityFX's presenter thread submits to its own queues
            // at any moment. This runs when MV++ starts and the motion vector size changes, and
            // that collision hung the GPU on Intel with FSR 3.1 upscaling and frame generation on.
            _zeroDepth?.Dispose();
            _zeroMotion?.Dispose();

            _zeroDepth = _gd.CreateTexture(MakeZeroInputInfo(width, height, GAL.Format.D32Float)) as TextureView;
            _zeroMotion = _gd.CreateTexture(MakeZeroInputInfo(width, height, GAL.Format.R16G16Float)) as TextureView;
            _zeroWidth = width;
            _zeroHeight = height;

            ImageSubresourceRange colorRange = new()
            {
                AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1,
            };
            ImageSubresourceRange depthRange = new()
            {
                AspectMask = ImageAspectFlags.DepthBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1,
            };

            ClearColorValue zeroColor = new(0f, 0f, 0f, 0f);
            _gd.Api.CmdClearColorImage(cbs.CommandBuffer, _zeroMotion.GetImage().Get(cbs).Value, ImageLayout.General, in zeroColor, 1, in colorRange);
            ClearDepthStencilValue zeroDepth = new() { Depth = 0f, Stencil = 0 };
            _gd.Api.CmdClearDepthStencilImage(cbs.CommandBuffer, _zeroDepth.GetImage().Get(cbs).Value, ImageLayout.General, in zeroDepth, 1, in depthRange);

            _gd.Api.CmdPipelineBarrier(cbs.CommandBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
                0, 0, null, 0, null, 0, null);
        }

        private static GAL.TextureCreateInfo MakeZeroInputInfo(int width, int height, GAL.Format format)
        {
            return new GAL.TextureCreateInfo(
                width,
                height,
                1,
                1,
                1,
                1,
                1,
                4,
                format,
                GAL.DepthStencilMode.Depth,
                GAL.Target.Texture2D,
                GAL.SwizzleComponent.Red,
                GAL.SwizzleComponent.Green,
                GAL.SwizzleComponent.Blue,
                GAL.SwizzleComponent.Alpha);
        }

        /// <summary>Wraps a texture view's underlying <c>VkImage</c> for the FidelityFX API.</summary>
        public static FsrFgApi.FfxApiResource MakeResource(
            TextureView view,
            CommandBufferScoped cbs,
            FsrFgApi.ResourceUsage usage,
            FsrFgApi.ResourceState state)
        {
            if (view == null)
            {
                return FsrFgApi.EmptyResource();
            }

            Image image = view.GetImage().Get(cbs).Value;

            return FsrFgApi.MakeImageResource(
                (IntPtr)image.Handle,
                view.VkFormat,
                (uint)view.Width,
                (uint)view.Height,
                usage,
                state);
        }

        /// <summary>
        /// Per-frame feed: creates or resizes the frame generation effect, updates its state and
        /// records the preparation pass. Call once per present while frame generation is armed.
        ///
        /// The camera values default to a generic perspective setup. They must be replaced with the
        /// values behind the projection the Mvpp passes recover, otherwise interpolated motion will
        /// disagree with the rendered image. A null <paramref name="depth"/> or
        /// <paramref name="motionVectors"/> is replaced by a zeroed stand-in, which leaves FidelityFX
        /// relying on its internal optical flow alone and costs quality.
        /// </summary>
        public void FeedFrame(
            Device device,
            CommandBufferScoped cbs,
            TextureView depth,
            TextureView motionVectors,
            int width,
            int height,
            Format backBufferFormat,
            bool hdr)
        {
            if (!IsArmed || width <= 0 || height <= 0)
            {
                return;
            }

            // Motion vectors are produced at the resolution of the view that holds them, which is
            // not necessarily the display resolution.
            int renderWidth = motionVectors?.Width ?? width;
            int renderHeight = motionVectors?.Height ?? height;
            bool displayResMvs = renderWidth == width && renderHeight == height;

            // Rebuild only on a display-size or HDR change. The motion vector resolution is not part
            // of this: it varies with the upscaler's internal size, and rebuilding the context every
            // time it moves both thrashes creation and tears down resources with GPU work in flight,
            // which loses the device.
            bool sizeChanged = _fgWidth != width || _fgHeight != height || _fgHdr != hdr;

            if (sizeChanged || (!HasFrameGen && !_fgCreateFailed))
            {
                if (HasFrameGen)
                {
                    // The outgoing context's resources are only used on the game queue
                    // (interpolation runs there, async workloads are off), so that queue is all
                    // that has to drain -- and unlike the device it can be waited on safely while
                    // FidelityFX's own thread uses its queues.
                    WaitGameQueueIdle(_gd);
                }

                _fgWidth = width;
                _fgHeight = height;
                _fgHdr = hdr;
                _fgDisplayResMvs = displayResMvs;

                // Motion vectors come from the upscaler at its internal resolution, never at
                // display resolution, so this flag must stay off; claiming otherwise makes
                // FidelityFX scale the vectors wrongly.
                _fgCreateFailed = !CreateFrameGen(device, (uint)width, (uint)height, backBufferFormat, hdr,
                    depthInverted: true, allowAsyncWorkloads: false, displayResolutionMotionVectors: false);
            }

            if (!HasFrameGen)
            {
                return;
            }

            _framesFed++;

            // The crash on Intel lands somewhere between one generated frame and the next, and that
            // window still spans FidelityFX's present, the return to managed code and this whole
            // method. These mark it off for the first few frames so the gap can be closed.
            bool trace = _framesFed <= 3;

            if (trace)
            {
                Logger.Info?.Print(LogClass.Gpu, $"FSR-FG: frame {_framesFed} fed, entering configure.");
            }

            long presentNow = Environment.TickCount64;

            // The status bar reads the present ratio, so measure it on a far shorter cadence than
            // the log: a figure five seconds old reads as a stuck counter while the game moves.
            if (presentNow - _telemetryMs >= 500)
            {
                ulong presents = _swapchain.GetPresentCount(new SwapchainKHR(_swapchainHandle));

                // The count restarts with each new FidelityFX swapchain, so a count below the last
                // one is a restart rather than a negative interval, and unsigned would wrap.
                if (_telemetryMs != 0 && presents >= _telemetryPresentCount)
                {
                    long fed = _framesFed - _telemetryFramesFed;

                    if (fed > 0)
                    {
                        FsrFgStatus.Report(
                            (double)(presents - _telemetryPresentCount) / fed,
                            (presents - _telemetryPresentCount) * 1000.0 / (presentNow - _telemetryMs));
                    }
                }

                _telemetryPresentCount = presents;
                _telemetryFramesFed = _framesFed;
                _telemetryMs = presentNow;
            }

            if (presentNow - _presentLogMs >= 5000)
            {
                ulong presentsNow = _swapchain.GetPresentCount(new SwapchainKHR(_swapchainHandle));

                if (_presentLogMs != 0 && presentsNow >= _lastPresentCount)
                {
                    ulong presents = presentsNow;
                    ulong presented = presents - _lastPresentCount;
                    long fed = _framesFed - _lastFramesFed;

                    Logger.Info?.Print(LogClass.Gpu, fed > 0
                        ? $"FSR-FG: {presented} present(s) for {fed} rendered frame(s) over ~5 s " +
                          $"(ratio {(double)presented / fed:F2}), generation callback invoked " +
                          $"{FsrFgFrameGen.CallbackInvocations} time(s) total, {FsrFgFrameGen.CallbackFailures} failure(s)."
                        : "FSR-FG: no frames fed in the last ~5 s.");

                    _lastPresentCount = presents;
                    _lastFramesFed = _framesFed;
                }
                else
                {
                    _lastPresentCount = presentsNow;
                    _lastFramesFed = _framesFed;
                }

                _presentLogMs = presentNow;
            }

            long now = Environment.TickCount64;
            float deltaMs = _lastFrameMs == 0 ? 16.667f : Math.Clamp(now - _lastFrameMs, 1, 1000);
            _lastFrameMs = now;

            // FidelityFX can mark its own generated frames, which settles whether they reach the
            // screen at all: tear lines appear only on generated frames, pacing lines show the
            // pacing, and the debug view overlays the interpolation internals.
            uint debugFlags = Environment.GetEnvironmentVariable("RYUJINX_FSR_FG_DEBUG_VIEW") switch
            {
                "1" => (uint)(FsrFgApi.DispatchFrameGenerationFlags.DrawDebugTearLines |
                              FsrFgApi.DispatchFrameGenerationFlags.DrawDebugPacingLines),
                "2" => (uint)FsrFgApi.DispatchFrameGenerationFlags.DrawDebugView,
                _ => 0u,
            };

            BeginFrame(enabled: true, allowAsyncWorkloads: false, dispatchFlags: debugFlags, width, height);

            if (trace)
            {
                Logger.Info?.Print(LogClass.Gpu, $"FSR-FG: frame {_framesFed} configured, entering prepare.");
            }

            // The Mvpp passes write motion vectors in pixel units (DlssUpscaler reports
            // mvecScale=1/width when handing the same view to Streamline), which is what
            // FidelityFX expects at a scale of one.
            // Depth and motion vectors are only useful if the camera parameters that go with them are
            // right. For this game the Mvpp probes never match ("MV++ en pause"), so the vectors are
            // the upscaler's and the camera planes are placeholders -- which makes interpolation
            // reconstruct motion wrongly. Zeroed depth and motion leave FidelityFX to derive motion
            // from the colour frames with its own optical flow, which needs none of that and is how
            // frame-generation-on-final-frames normally works.
            bool opticalFlowOnly = Environment.GetEnvironmentVariable("RYUJINX_FSR_FG_FLOWONLY") != "0";

            // "Colour only" means zeroed inputs, never empty ones: see _zeroDepth. A missing view is
            // replaced the same way when real inputs were asked for.
            EnsureZeroInputs(device, cbs, renderWidth, renderHeight);

            TextureView depthInput = opticalFlowOnly || depth == null ? _zeroDepth : depth;
            TextureView motionInput = opticalFlowOnly || motionVectors == null ? _zeroMotion : motionVectors;

            FsrFgApi.ReturnCode codeTraced;

            FsrFgApi.ReturnCode code = codeTraced = Prepare(
                cbs,
                renderWidth,
                renderHeight,
                0.0f,
                0.0f,
                1.0f,
                1.0f,
                deltaMs,
                _cameraNear,
                _cameraFar,
                _cameraFov,
                // UnorderedAccess is the state that maps to VK_IMAGE_LAYOUT_GENERAL, which is the
                // layout Ryujinx keeps its images in. FidelityFX also restores imported images to
                // the state declared here after the dispatch, so declaring a read state would
                // leave them in SHADER_READ_ONLY_OPTIMAL behind the renderer's back.
                MakeResource(depthInput, cbs, FsrFgApi.ResourceUsage.DepthTarget, FsrFgApi.ResourceState.UnorderedAccess),
                MakeResource(motionInput, cbs, FsrFgApi.ResourceUsage.Uav, FsrFgApi.ResourceState.UnorderedAccess));

            if (!_flowModeLogged)
            {
                _flowModeLogged = true;

                Logger.Info?.Print(LogClass.Gpu, opticalFlowOnly
                    ? "FSR-FG: interpolating from colour only (optical flow); set RYUJINX_FSR_FG_FLOWONLY=0 to feed depth and motion vectors instead."
                    : "FSR-FG: interpolating with depth and motion vectors.");
            }

            if (trace)
            {
                Logger.Info?.Print(LogClass.Gpu, $"FSR-FG: frame {_framesFed} prepare returned {codeTraced}.");
            }

            if (code != FsrFgApi.ReturnCode.Ok && !_prepareFailLogged)
            {
                _prepareFailLogged = true;

                Logger.Warning?.Print(LogClass.Gpu, $"FSR-FG: prepare dispatch failed ({code}); generated frames will not appear.");
            }
        }

        /// <summary>
        /// Waits for the game queue alone, under the lock every submit to it takes. The safe form
        /// of "wait for the GPU" while frame generation is armed: vkDeviceWaitIdle would need every
        /// queue externally synchronised, including the ones FidelityFX's threads submit to.
        /// </summary>
        internal static void WaitGameQueueIdle(VulkanRenderer gd)
        {
            lock (gd.QueueLock)
            {
                gd.Api.QueueWaitIdle(gd.Queue);
            }
        }

        /// <summary>Drains queued presents. Call before tearing the swapchain down.</summary>
        public void WaitForPresents() => _swapchain?.WaitForPresents();

        public void Dispose()
        {
            FsrFgStatus.Clear();

            _frameGen?.Dispose();
            _frameGen = null;

            _swapchain?.Dispose();
            _swapchain = null;

            _zeroDepth?.Dispose();
            _zeroDepth = null;
            _zeroMotion?.Dispose();
            _zeroMotion = null;
        }
    }
}
