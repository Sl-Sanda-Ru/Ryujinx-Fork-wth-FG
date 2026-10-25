using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan.FsrFg
{
    /// <summary>
    /// FSR3 frame generation context. Owns the FidelityFX frame generation effect and feeds it the
    /// per-frame camera state, depth and motion vectors needed to interpolate a frame.
    ///
    /// <para>Creating it used to access-violate inside amd_fidelityfx_vk.dll, which is why it sits
    /// behind RYUJINX_FSR_FG_CONTEXT=1. That no longer reproduces: it creates and interpolates on
    /// both NVIDIA and Intel Arc. The no-backend probe in <see cref="TryCreate"/> is left from that
    /// investigation.</para>
    ///
    /// Pairs with <see cref="FsrFgSwapchain"/>, which owns presentation of the generated frames.
    /// </summary>
    class FsrFgFrameGen : IDisposable
    {
        /// <summary>
        /// Unmanaged storage holding the one <c>ffxContext</c> handle, i.e. the <c>ffxContext*</c>
        /// every ffx-api call is given.
        ///
        /// <para>It must not live in a managed field. Taking <c>ref</c> of a field hands native code
        /// the address of memory inside a managed object, which the collector is free to move
        /// afterwards, and taking <c>ref</c> of a local hands over a stack slot that dies when the
        /// method returns -- a different address on every call. ffx-api writes through this pointer,
        /// so either arrangement lets it write into memory that has since become something else.
        /// An unmanaged allocation gives one address that stays put for the context's whole life.</para>
        /// </summary>
        private IntPtr _contextStorage;

        private unsafe IntPtr _context
        {
            get => _contextStorage == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)_contextStorage;
        }

        /// <summary>The <c>ffxContext*</c> to pass to ffx-api, allocating the storage on first use.</summary>
        private unsafe ref IntPtr ContextRef
        {
            get
            {
                if (_contextStorage == IntPtr.Zero)
                {
                    _contextStorage = (IntPtr)NativeMemory.AllocZeroed((nuint)sizeof(IntPtr));
                }

                return ref Unsafe.AsRef<IntPtr>((void*)_contextStorage);
            }
        }

        /// <summary>
        /// Serialises every access to the frame generation context. FidelityFX documents the context
        /// as not thread safe and prescribes a mutex around the prepare dispatch, the generation
        /// dispatch and context destruction, because its own present path touches the same context.
        ///
        /// <para>With all four queues in one family the generation callback arrives on the thread
        /// that called present, so those calls were already serialised by accident. A present queue
        /// in a different family makes FidelityFX compose on a second thread, and the callback then
        /// races the render thread's prepare -- which corrupts memory rather than failing cleanly.</para>
        ///
        /// <para>Static because the callback is static and carries no user context. The lock is only
        /// ever held across a bounded native call, never across a present or a wait, so the callback
        /// thread cannot be blocked by anything that is itself waiting on the callback.</para>
        /// </summary>
        private static readonly Lock _contextLock = new();

        // FidelityFX invokes the frame generation callback with no context of ours, so the handle it
        // should dispatch on is held statically. One frame generation context exists at a time.
        private static IntPtr _callbackContextStorage;
        private static bool _callbackLogged;

        /// <summary>Times FidelityFX has asked us to generate a frame. Zero means it never asked.</summary>
        public static int CallbackInvocations;

        public static int CallbackFailures;

        // Referenced by the create descriptor's pNext chain, so it must outlive context creation.
        private IntPtr _backendDesc;

        public bool IsValid => _context != IntPtr.Zero;

        /// <summary>Raw context handle, for the unmanaged frame generation callback.</summary>
        public IntPtr Handle => _context;

        private FsrFgFrameGen()
        {
        }

        /// <param name="vkGetDeviceProcAddr">Raw <c>vkGetDeviceProcAddr</c> pointer, used by the FidelityFX Vulkan backend.</param>
        /// <param name="backBufferFormat">Swapchain format, mapped to an FFX surface format.</param>
        public static unsafe FsrFgFrameGen TryCreate(
            PhysicalDevice physicalDevice,
            Device device,
            IntPtr vkGetDeviceProcAddr,
            uint displayWidth,
            uint displayHeight,
            uint maxRenderWidth,
            uint maxRenderHeight,
            Format backBufferFormat,
            FsrFgApi.FrameGenerationFlags flags)
        {
            if (!FsrFgApi.IsLoaded)
            {
                return null;
            }

            FsrFgFrameGen result = new();

            FsrFgApi.CreateBackendVkDesc backend = new()
            {
                Header = new FsrFgApi.FfxApiHeader(FsrFgApi.CreateContextDescTypeBackendVk),
                VkDevice = device.Handle,
                VkPhysicalDevice = physicalDevice.Handle,
                VkDeviceProcAddr = vkGetDeviceProcAddr,
            };

            // Chained on the stack rather than through AllocHGlobal/StructureToPtr: both structs
            // live for the duration of the call, which is all CreateBackend needs, and it removes
            // our own marshalling from the equation.
            FsrFgApi.CreateContextDescFrameGeneration desc = new()
            {
                Header = new FsrFgApi.FfxApiHeader(FsrFgApi.CreateContextDescTypeFrameGeneration)
                {
                    PNext = (IntPtr)(&backend),
                },
                Flags = (uint)flags,
                DisplaySize = new FsrFgApi.FfxApiDimensions2D { Width = displayWidth, Height = displayHeight },
                MaxRenderSize = new FsrFgApi.FfxApiDimensions2D { Width = maxRenderWidth, Height = maxRenderHeight },
                BackBufferFormat = (uint)FsrFgApi.GetSurfaceFormat(backBufferFormat),
            };

            // Bisection probe: the same descriptor with nothing chained. ffx-api's CreateBackend
            // finds no backend and MustCreateBackend then returns FFX_API_RETURN_ERROR, so a clean
            // Error here proves the provider lookup and pNext walk are sound and the fault lies in
            // the backend creation that follows. An access violation here means the fault is
            // earlier than that.
            FsrFgApi.CreateContextDescFrameGeneration probe = desc;
            probe.Header.PNext = IntPtr.Zero;

            IntPtr probeContext = IntPtr.Zero;
            FsrFgApi.ReturnCode probeCode = FsrFgApi.CreateContext(ref probeContext, (IntPtr)(&probe));

            Logger.Info?.Print(LogClass.Gpu,
                $"FSR-FG: no-backend probe returned {probeCode} (Error expected; reaching this line at all means the dispatch path is sound).");

            if (probeContext != IntPtr.Zero)
            {
                FsrFgApi.DestroyContext(ref probeContext);
            }

            Logger.Info?.Print(LogClass.Gpu,
                $"FSR-FG: creating frame generation context, display={displayWidth}x{displayHeight}, " +
                $"maxRender={maxRenderWidth}x{maxRenderHeight}, fmt={backBufferFormat}, " +
                $"ffxFmt={(uint)FsrFgApi.GetSurfaceFormat(backBufferFormat)}, flags=0x{(uint)flags:X}, " +
                $"device=0x{device.Handle:X}, physical=0x{physicalDevice.Handle:X}, procAddr=0x{vkGetDeviceProcAddr:X}.");

            FsrFgApi.ReturnCode code = FsrFgApi.CreateContext(ref result.ContextRef, (IntPtr)(&desc));

            if (code != FsrFgApi.ReturnCode.Ok)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"FSR-FG: frame generation context creation failed ({code}).");

                result.Dispose();

                return null;
            }

            // The callback dispatches on the same stable address, not on a copy of the handle.
            _callbackContextStorage = result._contextStorage;

            Logger.Info?.Print(LogClass.Gpu,
                $"FSR-FG: frame generation context created ({displayWidth}x{displayHeight}, flags {flags}).");

            return result;
        }

        /// <summary>
        /// Invoked by the FidelityFX swapchain when it needs a frame generated. The descriptor is
        /// forwarded straight back as an ffxDispatch on this context, which is what AMD's own
        /// integrations do. Left null, the swapchain has no way to produce an interpolated frame.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe uint FrameGenerationDispatch(IntPtr desc, IntPtr userContext)
        {
            try
            {
                if (desc == IntPtr.Zero)
                {
                    return (uint)FsrFgApi.ReturnCode.Error;
                }

                CallbackInvocations++;

                // Which side of this native call the process dies on is the whole question. With a
                // present queue in another family FidelityFX changes composition mode and starts
                // calling this from its own worker thread rather than the render thread, so the
                // thread id is logged too: a first call on one thread and a fatal second call on
                // another points at the managed transition, not at the dispatch itself.
                long invocation = CallbackInvocations;

                if (invocation <= 3)
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"FSR-FG: generation callback #{invocation} entering dispatch on managed thread " +
                        $"{Environment.CurrentManagedThreadId}.");
                }

                FsrFgApi.ReturnCode result;

                // The storage is read under the lock because Dispose clears and frees it under the
                // same lock; read outside, it could be freed between the check and the dispatch.
                lock (_contextLock)
                {
                    IntPtr storage = _callbackContextStorage;

                    if (storage == IntPtr.Zero)
                    {
                        return (uint)FsrFgApi.ReturnCode.Error;
                    }

                    result = FsrFgApi.Dispatch(ref Unsafe.AsRef<IntPtr>((void*)storage), desc);
                }

                if (invocation <= 3)
                {
                    Logger.Info?.Print(LogClass.Gpu,
                        $"FSR-FG: generation callback #{invocation} returned {result}.");
                }

                if (!_callbackLogged)
                {
                    _callbackLogged = true;

                    Logger.Info?.Print(LogClass.Gpu,
                        $"FSR-FG: FidelityFX invoked the frame generation callback for the first time ({result}).");
                }
                else if (result != FsrFgApi.ReturnCode.Ok && CallbackFailures++ < 3)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"FSR-FG: frame generation dispatch returned {result}.");
                }

                return (uint)result;
            }
            catch (Exception)
            {
                return (uint)FsrFgApi.ReturnCode.Error;
            }
        }

        /// <summary>
        /// Updates frame generation state. Must be called once per rendered frame, with
        /// <paramref name="frameId"/> incrementing by exactly one; any other delta resets the
        /// interpolation history.
        /// </summary>
        public unsafe FsrFgApi.ReturnCode Configure(
            IntPtr swapchainContext,
            bool enabled,
            bool allowAsyncWorkloads,
            uint dispatchFlags,
            ulong frameId,
            FsrFgApi.FfxApiResource hudLessColor,
            FsrFgApi.FfxApiRect2D generationRect)
        {
            if (!IsValid)
            {
                return FsrFgApi.ReturnCode.Error;
            }

            FsrFgApi.ConfigureDescFrameGeneration desc = new()
            {
                Header = new FsrFgApi.FfxApiHeader(FsrFgApi.ConfigureDescTypeFrameGeneration),
                SwapChain = swapchainContext,
                PresentCallback = IntPtr.Zero,
                PresentCallbackUserContext = IntPtr.Zero,
                FrameGenerationCallback =
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint>)&FrameGenerationDispatch,
                FrameGenerationCallbackUserContext = IntPtr.Zero,
                FrameGenerationEnabled = enabled ? (byte)1 : (byte)0,
                AllowAsyncWorkloads = allowAsyncWorkloads ? (byte)1 : (byte)0,
                HudLessColor = hudLessColor,
                Flags = dispatchFlags,
                OnlyPresentGenerated = 0,
                GenerationRect = generationRect,
                FrameId = frameId,
            };

            lock (_contextLock)
            {
                return FsrFgApi.Configure(ref ContextRef, (IntPtr)(&desc));
            }
        }

        /// <summary>
        /// Records the frame generation preparation pass. Depth and motion vectors come from the
        /// existing Mvpp camera reprojection work; camera planes and field of view must match the
        /// matrices those passes recovered, or interpolation will disagree with the image.
        /// </summary>
        public unsafe FsrFgApi.ReturnCode Prepare(
            IntPtr commandBuffer,
            ulong frameId,
            uint dispatchFlags,
            uint renderWidth,
            uint renderHeight,
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
            float viewSpaceToMetersFactor = 1.0f)
        {
            if (!IsValid)
            {
                return FsrFgApi.ReturnCode.Error;
            }

            FsrFgApi.DispatchDescFrameGenerationPrepare desc = new()
            {
                Header = new FsrFgApi.FfxApiHeader(FsrFgApi.DispatchDescTypeFrameGenerationPrepare),
                FrameId = frameId,
                Flags = dispatchFlags,
                CommandList = commandBuffer,
                RenderSize = new FsrFgApi.FfxApiDimensions2D { Width = renderWidth, Height = renderHeight },
                JitterOffset = new FsrFgApi.FfxApiFloatCoords2D { X = jitterX, Y = jitterY },
                MotionVectorScale = new FsrFgApi.FfxApiFloatCoords2D { X = motionVectorScaleX, Y = motionVectorScaleY },
                FrameTimeDelta = frameTimeDeltaMs,
                UnusedReset = 0,
                CameraNear = cameraNear,
                CameraFar = cameraFar,
                CameraFovAngleVertical = cameraFovAngleVertical,
                ViewSpaceToMetersFactor = viewSpaceToMetersFactor,
                Depth = depth,
                MotionVectors = motionVectors,
            };

            lock (_contextLock)
            {
                return FsrFgApi.Dispatch(ref ContextRef, (IntPtr)(&desc));
            }
        }

        public void Dispose()
        {
            if (_contextStorage != IntPtr.Zero)
            {
                // Unpublished in the same critical section as the destroy, so a callback either
                // finishes its dispatch first or sees no context at all.
                lock (_contextLock)
                {
                    if (_callbackContextStorage == _contextStorage)
                    {
                        _callbackContextStorage = IntPtr.Zero;
                    }

                    if (_context != IntPtr.Zero)
                    {
                        FsrFgApi.DestroyContext(ref ContextRef);
                    }
                }

                unsafe
                {
                    NativeMemory.Free((void*)_contextStorage);
                }

                _contextStorage = IntPtr.Zero;
            }

            if (_backendDesc != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_backendDesc);
                _backendDesc = IntPtr.Zero;
            }
        }
    }
}
