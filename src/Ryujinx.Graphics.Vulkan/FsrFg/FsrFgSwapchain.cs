using Ryujinx.Common.Logging;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Vulkan.FsrFg
{
    /// <summary>
    /// FSR3 frame generation swapchain. Creating the context hands the application's
    /// <see cref="SwapchainKHR"/> over to FidelityFX, which replaces it with one that paces and
    /// presents generated frames.
    ///
    /// FidelityFX returns replacement entry points for the swapchain functions, so this mirrors
    /// <c>DlssFrameGenHooks</c>: present-path calls are routed through <see cref="SwapchainProxy"/>
    /// instead of the driver's own. Note that FidelityFX's create/destroy replacements take an extra
    /// context argument and are therefore <b>not</b> signature-compatible with
    /// <c>vkCreateSwapchainKHR</c>/<c>vkDestroySwapchainKHR</c>; those two must go through
    /// <see cref="CreateSwapchain"/> and <see cref="DestroySwapchain"/> rather than the proxy.
    /// </summary>
    class FsrFgSwapchain : INativeContext, IDisposable
    {
        private readonly Vk _api;
        private IntPtr _context;
        private IntPtr _swapchainStorage;
        private FsrFgApi.QueryDescSwapchainReplacementFunctionsVk _functions;

        /// <summary>Swapchain entry points backed by FidelityFX rather than the driver.</summary>
        public KhrSwapchain SwapchainProxy { get; }

        private FsrFgSwapchain(Vk api)
        {
            _api = api;

            SwapchainProxy = new KhrSwapchain(this);
        }

        /// <summary>
        /// Replaces <paramref name="swapchain"/> with a frame generation swapchain.
        /// </summary>
        /// <param name="swapchain">On input the swapchain to replace, on output the replacement.</param>
        /// <returns>The context, or null if frame generation could not be armed.</returns>
        public static unsafe FsrFgSwapchain TryCreate(
            Vk api,
            PhysicalDevice physicalDevice,
            Device device,
            ref SwapchainKHR swapchain,
            SwapchainCreateInfoKHR createInfo,
            FsrFgApi.VkQueueInfoFfxApi gameQueue,
            FsrFgApi.VkQueueInfoFfxApi asyncComputeQueue,
            FsrFgApi.VkQueueInfoFfxApi presentQueue,
            FsrFgApi.VkQueueInfoFfxApi imageAcquireQueue)
        {
            if (!FsrFgApi.IsLoaded)
            {
                return null;
            }

            FsrFgSwapchain result = new(api);

            // The handle storage is referenced by the descriptor and must outlive context creation.
            result._swapchainStorage = Marshal.AllocHGlobal(sizeof(ulong));
            Marshal.WriteInt64(result._swapchainStorage, (long)swapchain.Handle);

            FsrFgApi.CreateContextDescFgSwapchainVk desc = new()
            {
                Header = new FsrFgApi.FfxApiHeader(FsrFgApi.CreateContextDescTypeFgSwapchainVk),
                PhysicalDevice = physicalDevice.Handle,
                Device = device.Handle,
                Swapchain = result._swapchainStorage,
                Allocator = IntPtr.Zero,
                CreateInfo = createInfo,
                GameQueue = gameQueue,
                AsyncComputeQueue = asyncComputeQueue,
                PresentQueue = presentQueue,
                ImageAcquireQueue = imageAcquireQueue,
            };

            FsrFgApi.ReturnCode code = FsrFgApi.CreateContext(ref result._context, (IntPtr)(&desc));

            if (code != FsrFgApi.ReturnCode.Ok)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"FSR-FG: swapchain context creation failed ({code}); present path stays native.");

                result.Dispose();

                return null;
            }

            swapchain = new SwapchainKHR((ulong)Marshal.ReadInt64(result._swapchainStorage));

            if (!result.QueryReplacementFunctions())
            {
                result.Dispose();

                return null;
            }

            Logger.Info?.Print(LogClass.Gpu, "FSR-FG: frame generation swapchain armed.");

            // Now that a valid context exists, diagnostics can be turned on; FidelityFX will then
            // report its own errors for everything that follows.
            FsrFgApi.EnableDebugMessages(ref result._context);

            return result;
        }

        private unsafe bool QueryReplacementFunctions()
        {
            FsrFgApi.QueryDescSwapchainReplacementFunctionsVk desc = new()
            {
                Header = new FsrFgApi.FfxApiHeader(FsrFgApi.QueryDescTypeFgSwapchainFunctionsVk),
            };

            FsrFgApi.ReturnCode code = FsrFgApi.Query(ref _context, (IntPtr)(&desc));

            if (code != FsrFgApi.ReturnCode.Ok)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"FSR-FG: could not query swapchain replacement functions ({code}).");

                return false;
            }

            _functions = desc;

            if (_functions.QueuePresentKhr == IntPtr.Zero || _functions.AcquireNextImageKhr == IntPtr.Zero)
            {
                Logger.Warning?.Print(LogClass.Gpu, "FSR-FG: replacement present/acquire functions are null; cannot arm frame generation.");

                return false;
            }

            return true;
        }

        /// <summary>
        /// FidelityFX replacement for <c>vkCreateSwapchainKHR</c>, used when the swapchain is
        /// recreated but this context is kept (for example when toggling vsync).
        /// </summary>
        public unsafe Result CreateSwapchain(Device device, SwapchainCreateInfoKHR createInfo, ref SwapchainKHR swapchain)
        {
            if (_functions.CreateSwapchainFfxApi == IntPtr.Zero)
            {
                return Result.ErrorInitializationFailed;
            }

            ulong handle = swapchain.Handle;

            Result result = ((delegate* unmanaged<IntPtr, SwapchainCreateInfoKHR*, IntPtr, ulong*, IntPtr, Result>)
                _functions.CreateSwapchainFfxApi)(device.Handle, &createInfo, IntPtr.Zero, &handle, _context);

            swapchain = new SwapchainKHR(handle);

            return result;
        }

        /// <summary>FidelityFX replacement for <c>vkDestroySwapchainKHR</c>.</summary>
        public unsafe void DestroySwapchain(Device device, SwapchainKHR swapchain)
        {
            if (_functions.DestroySwapchainFfxApi == IntPtr.Zero)
            {
                return;
            }

            ((delegate* unmanaged<IntPtr, ulong, IntPtr, IntPtr, void>)
                _functions.DestroySwapchainFfxApi)(device.Handle, swapchain.Handle, IntPtr.Zero, _context);
        }

        /// <summary>
        /// Number of presents FidelityFX has issued on this swapchain since it was created. Higher
        /// than the number of frames the emulator rendered means generated frames are being shown.
        /// </summary>
        public unsafe ulong GetPresentCount(SwapchainKHR swapchain)
        {
            if (_functions.GetLastPresentCountFfxApi == IntPtr.Zero)
            {
                return 0;
            }

            return ((delegate* unmanaged<ulong, ulong>)_functions.GetLastPresentCountFfxApi)(swapchain.Handle);
        }

        /// <summary>Blocks until FidelityFX has drained its queued presents.</summary>
        public unsafe void WaitForPresents()
        {
            if (_context == IntPtr.Zero)
            {
                return;
            }

            FsrFgApi.FfxApiHeader desc = new(FsrFgApi.DispatchDescTypeFgSwapchainWaitForPresentsVk);

            FsrFgApi.Dispatch(ref _context, (IntPtr)(&desc));
        }

        public nint GetProcAddress(string proc, int? slot = default)
        {
            // Only the entry points whose signatures match the Vulkan originals can be served here.
            nint replacement = proc switch
            {
                "vkGetSwapchainImagesKHR" => _functions.GetSwapchainImagesKhr,
                "vkAcquireNextImageKHR" => _functions.AcquireNextImageKhr,
                "vkQueuePresentKHR" => _functions.QueuePresentKhr,
                "vkSetHdrMetadataEXT" => _functions.SetHdrMetadataExt,
                _ => IntPtr.Zero,
            };

            if (replacement != IntPtr.Zero)
            {
                return replacement;
            }

            return _api.Context.GetProcAddress(proc, slot);
        }

        public bool TryGetProcAddress(string proc, out nint addr, int? slot = default)
        {
            addr = GetProcAddress(proc, slot);

            return addr != 0;
        }

        public void Dispose()
        {
            if (_context != IntPtr.Zero)
            {
                FsrFgApi.DestroyContext(ref _context);
                _context = IntPtr.Zero;
            }

            if (_swapchainStorage != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_swapchainStorage);
                _swapchainStorage = IntPtr.Zero;
            }
        }
    }
}
