
using Ryujinx.Common.Logging;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System;

namespace Ryujinx.Graphics.Vulkan.Dlss
{
    class DlssFrameGenHooks : INativeContext
    {
        private static readonly string[] _hookedFunctions =
        {
            "vkCreateSwapchainKHR",
            "vkDestroySwapchainKHR",
            "vkGetSwapchainImagesKHR",
            "vkAcquireNextImageKHR",
            "vkQueuePresentKHR",
            "vkDeviceWaitIdle",
        };

        private readonly Vk _api;
        private readonly Device _device;
        private readonly IntPtr _devicePtr;
        private readonly IntPtr _deviceWaitIdleProxy;

        public KhrSwapchain SwapchainProxy { get; }

        private DlssFrameGenHooks(Vk api, Device device, IntPtr deviceWaitIdleProxy)
        {
            _api = api;
            _device = device;
            _devicePtr = (IntPtr)device.Handle;
            _deviceWaitIdleProxy = deviceWaitIdleProxy;

            SwapchainProxy = new KhrSwapchain(this);
        }

        public static DlssFrameGenHooks TryCreate(Vk api, Device device)
        {
            if (!Streamline.IsInitialized)
            {
                return null;
            }

            IntPtr devicePtr = (IntPtr)device.Handle;
            IntPtr deviceWaitIdleProxy = IntPtr.Zero;
            bool allResolved = true;

            foreach (string name in _hookedFunctions)
            {
                IntPtr slPtr = Streamline.GetVulkanDeviceProxy(devicePtr, name);
                api.Context.TryGetProcAddress(name, out nint nativePtr);

                if (slPtr == IntPtr.Zero)
                {
                    if (name == "vkDeviceWaitIdle")
                    {
                        Logger.Warning?.Print(LogClass.Gpu,
                            "DLSS-FG: interposer returned no proxy for vkDeviceWaitIdle; falling back to the native wait (resize safety depends on SL flushing via present).");
                    }
                    else
                    {
                        Logger.Warning?.Print(LogClass.Gpu,
                            $"DLSS-FG: interposer returned no proxy for {name}; manual hooking cannot be armed (present path stays native).");
                        allResolved = false;
                    }

                    continue;
                }

                if (name == "vkDeviceWaitIdle")
                {
                    deviceWaitIdleProxy = slPtr;
                }

                Logger.Info?.Print(LogClass.Gpu, slPtr == (IntPtr)nativePtr
                    ? $"DLSS-FG: {name} -> passthrough (interposer returned the native address)."
                    : $"DLSS-FG: {name} -> SL proxy installed (0x{slPtr:X} vs native 0x{nativePtr:X}).");
            }

            if (!allResolved)
            {
                return null;
            }

            return new DlssFrameGenHooks(api, device, deviceWaitIdleProxy);
        }

        public unsafe void DeviceWaitIdle()
        {
            if (_deviceWaitIdleProxy != IntPtr.Zero)
            {
                ((delegate* unmanaged<IntPtr, int>)_deviceWaitIdleProxy)(_devicePtr);
            }
            else
            {
                _api.DeviceWaitIdle(_device);
            }
        }

        public nint GetProcAddress(string proc, int? slot = default)
        {
            IntPtr addr = Streamline.GetVulkanDeviceProxy(_devicePtr, proc);
            if (addr != IntPtr.Zero)
            {
                return addr;
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
        }
    }
}
