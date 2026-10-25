using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Vulkan.FsrFg
{
    /// <summary>
    /// Bindings for AMD's FidelityFX API (<c>amd_fidelityfx_vk.dll</c>), used for FSR3 frame
    /// generation on the Vulkan backend.
    ///
    /// The Vulkan frame generation swapchain provider is marked deprecated by AMD in current SDK
    /// headers, but is still compiled into the shipped Vulkan library
    /// (<c>ffxProvider_FrameGenerationSwapChain_VK</c>). Struct layouts here follow FidelityFX SDK
    /// v1.1.4, the last release whose public headers declare it.
    /// </summary>
    public static partial class FsrFgApi
    {
        public const string FsrVkDll = "amd_fidelityfx_vk.dll";

        // ffx_api.h / ffx_api_vk.h / ffx_framegeneration.h struct types.
        public const ulong CreateContextDescTypeBackendVk = 0x0000003u;
        public const ulong CreateContextDescTypeFgSwapchainVk = 0x40001u;
        public const ulong CreateContextDescTypeFgSwapchainModeVk = 0x40010u;
        public const ulong CreateContextDescTypeFrameGeneration = 0x00020001u;
        public const ulong ConfigureDescTypeFrameGeneration = 0x00020002u;
        public const ulong DispatchDescTypeFrameGeneration = 0x00020003u;
        public const ulong DispatchDescTypeFrameGenerationPrepare = 0x00020004u;
        public const ulong QueryDescTypeFgSwapchainFunctionsVk = 0x40005u;
        public const ulong ConfigureDescTypeGlobalDebug1 = 0x0000001u;
        public const ulong QueryDescTypeGetVersions = 4u;

        public const uint GlobalDebugLevelVerbose = 0xfffffffu;
        public const ulong DispatchDescTypeFgSwapchainWaitForPresentsVk = 0x40007u;

        public enum ReturnCode : uint
        {
            Ok = 0,
            Error = 1,
            ErrorUnknownDescType = 2,
            ErrorRuntimeError = 3,
            NoProvider = 4,
            ErrorMemory = 5,
            ErrorParameter = 6,
        }

        [Flags]
        public enum FrameGenerationFlags : uint
        {
            None = 0,
            EnableAsyncWorkloadSupport = 1 << 0,
            DisplayResolutionMotionVectors = 1 << 1,
            MotionVectorsJitterCancellation = 1 << 2,
            DepthInverted = 1 << 3,
            DepthInfinite = 1 << 4,
            HighDynamicRange = 1 << 5,
            DebugChecking = 1 << 6,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfxApiHeader
        {
            public ulong Type;
            public IntPtr PNext;

            public FfxApiHeader(ulong type)
            {
                Type = type;
                PNext = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfxApiResourceDescription
        {
            public uint Type;
            public uint Format;
            public uint Width;
            public uint Height;
            public uint Depth;
            public uint MipCount;
            public uint Flags;
            public uint Usage;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfxApiResource
        {
            public IntPtr Resource;
            public FfxApiResourceDescription Description;
            public uint State;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfxApiDimensions2D
        {
            public uint Width;
            public uint Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfxApiRect2D
        {
            public int Left;
            public int Top;
            public int Width;
            public int Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkQueueInfoFfxApi
        {
            public IntPtr Queue;
            public uint FamilyIndex;
            public IntPtr SubmitFunc;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct QueryDescGetVersions
        {
            public FfxApiHeader Header;
            public ulong CreateDescType;
            public IntPtr Device;
            public IntPtr OutputCount;
            public IntPtr VersionIds;
            public IntPtr VersionNames;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ConfigureDescGlobalDebug1
        {
            public FfxApiHeader Header;
            public IntPtr FpMessage;
            public uint DebugLevel;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CreateBackendVkDesc
        {
            public FfxApiHeader Header;
            public IntPtr VkDevice;
            public IntPtr VkPhysicalDevice;
            public IntPtr VkDeviceProcAddr;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CreateContextDescFgSwapchainVk
        {
            public FfxApiHeader Header;
            public IntPtr PhysicalDevice;
            public IntPtr Device;
            public IntPtr Swapchain; // VkSwapchainKHR*, in/out
            public IntPtr Allocator;
            public SwapchainCreateInfoKHR CreateInfo;
            public VkQueueInfoFfxApi GameQueue;
            public VkQueueInfoFfxApi AsyncComputeQueue;
            public VkQueueInfoFfxApi PresentQueue;
            public VkQueueInfoFfxApi ImageAcquireQueue;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CreateContextDescFrameGeneration
        {
            public FfxApiHeader Header;
            public uint Flags;
            public FfxApiDimensions2D DisplaySize;
            public FfxApiDimensions2D MaxRenderSize;
            public uint BackBufferFormat;
        }

        /// <summary>
        /// Replacement entry points returned by the frame generation swapchain context. These stand
        /// in for the corresponding Vulkan functions, in the same way the Streamline interposer
        /// supplies proxies for the DLSS-FG path.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct QueryDescSwapchainReplacementFunctionsVk
        {
            public FfxApiHeader Header;
            public IntPtr CreateSwapchainFfxApi;
            public IntPtr DestroySwapchainFfxApi;
            public IntPtr GetSwapchainImagesKhr;
            public IntPtr AcquireNextImageKhr;
            public IntPtr QueuePresentKhr;
            public IntPtr SetHdrMetadataExt;
            public IntPtr GetLastPresentCountFfxApi;
        }

        private static IntPtr _libraryHandle;

        private static unsafe delegate* unmanaged[Cdecl]<IntPtr*, IntPtr, IntPtr, uint> _createContext;
        private static unsafe delegate* unmanaged[Cdecl]<IntPtr*, IntPtr, uint> _destroyContext;
        private static unsafe delegate* unmanaged[Cdecl]<IntPtr*, IntPtr, uint> _configure;
        private static unsafe delegate* unmanaged[Cdecl]<IntPtr*, IntPtr, uint> _query;
        private static unsafe delegate* unmanaged[Cdecl]<IntPtr*, IntPtr, uint> _dispatch;

        /// <summary>Set before device creation, so the queue count can account for FSR-FG.</summary>
        public static bool Requested =
            Environment.GetEnvironmentVariable("RYUJINX_FSR_FG") == "1";

        /// <summary>
        /// Re-reads the fields above that come from the settings profile, so a changed upscaler
        /// setting applies at the next game start without restarting the app.
        /// </summary>
        internal static void ReloadProfileSettings()
        {
            {
                Requested = Environment.GetEnvironmentVariable("RYUJINX_FSR_FG") == "1";
            }
        }

        public static bool IsLoaded { get; private set; }

        /// <summary>Default location for the user-supplied FidelityFX library, beside the executable.</summary>
        public static string GetDefaultLibraryFolder() => Path.Combine(AppContext.BaseDirectory, "fsr");

        public static bool IsAvailable(string folder) => File.Exists(Path.Combine(folder ?? string.Empty, FsrVkDll));

        public static unsafe bool Load(string folder)
        {
            if (IsLoaded)
            {
                return true;
            }

            // AMD ships the FidelityFX API as a Windows DLL only; there is no Linux equivalent.
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            string path = Path.Combine(folder ?? string.Empty, FsrVkDll);

            if (!File.Exists(path))
            {
                Logger.Info?.Print(LogClass.Gpu, $"FSR-FG: {FsrVkDll} not found in \"{folder}\"; frame generation unavailable.");

                return false;
            }

            if (!NativeLibrary.TryLoad(path, out _libraryHandle))
            {
                Logger.Warning?.Print(LogClass.Gpu, $"FSR-FG: failed to load \"{path}\".");

                return false;
            }

            if (!TryGetExport("ffxCreateContext", out IntPtr createContext) ||
                !TryGetExport("ffxDestroyContext", out IntPtr destroyContext) ||
                !TryGetExport("ffxConfigure", out IntPtr configure) ||
                !TryGetExport("ffxQuery", out IntPtr query) ||
                !TryGetExport("ffxDispatch", out IntPtr dispatch))
            {
                NativeLibrary.Free(_libraryHandle);
                _libraryHandle = IntPtr.Zero;

                return false;
            }

            _createContext = (delegate* unmanaged[Cdecl]<IntPtr*, IntPtr, IntPtr, uint>)createContext;
            _destroyContext = (delegate* unmanaged[Cdecl]<IntPtr*, IntPtr, uint>)destroyContext;
            _configure = (delegate* unmanaged[Cdecl]<IntPtr*, IntPtr, uint>)configure;
            _query = (delegate* unmanaged[Cdecl]<IntPtr*, IntPtr, uint>)query;
            _dispatch = (delegate* unmanaged[Cdecl]<IntPtr*, IntPtr, uint>)dispatch;

            IsLoaded = true;

            Logger.Info?.Print(LogClass.Gpu, $"FSR-FG: loaded \"{path}\".");

            ProbeVersions(CreateContextDescTypeFgSwapchainVk, "FG-swapchain-VK");
            ProbeVersions(CreateContextDescTypeFrameGeneration, "frame-generation");

            return true;
        }

        private static bool TryGetExport(string name, out IntPtr address)
        {
            if (!NativeLibrary.TryGetExport(_libraryHandle, name, out address))
            {
                Logger.Warning?.Print(LogClass.Gpu, $"FSR-FG: {FsrVkDll} is missing the {name} export.");

                return false;
            }

            return true;
        }

        // FidelityFX reports detail through this callback; ffxCreateContext on its own only ever
        // returns a coarse code such as ErrorRuntimeError.
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe void DebugMessage(uint type, char* message)
        {
            try
            {
                string text = message == null ? "(null)" : new string(message);

                if (type == 0)
                {
                    Logger.Error?.Print(LogClass.Gpu, $"FSR-FG [ffx]: {text}");
                }
                else
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"FSR-FG [ffx]: {text}");
                }
            }
            catch (Exception)
            {
                // Must not throw back into native code.
            }
        }

        /// <summary>
        /// Asks the library which provider versions it has for a given create-descriptor type. A
        /// count of zero means the effect is not available in this build, which distinguishes a
        /// missing provider from a descriptor this build does not recognise.
        /// </summary>
        private static unsafe void ProbeVersions(ulong createDescType, string label)
        {
            ulong count = 0;

            QueryDescGetVersions desc = new()
            {
                Header = new FfxApiHeader(QueryDescTypeGetVersions),
                CreateDescType = createDescType,
                Device = IntPtr.Zero,
                OutputCount = (IntPtr)(&count),
                VersionIds = IntPtr.Zero,
                VersionNames = IntPtr.Zero,
            };

            uint code = _query(null, (IntPtr)(&desc));

            if ((ReturnCode)code != ReturnCode.Ok)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"FSR-FG: version query for {label} failed ({(ReturnCode)code}).");

                return;
            }

            Logger.Info?.Print(LogClass.Gpu, $"FSR-FG: {label} reports {count} provider version(s).");

            if (count == 0 || count > 16)
            {
                return;
            }

            ulong[] ids = new ulong[count];
            IntPtr[] names = new IntPtr[count];

            fixed (ulong* idsPtr = ids)
            fixed (IntPtr* namesPtr = names)
            {
                desc.VersionIds = (IntPtr)idsPtr;
                desc.VersionNames = (IntPtr)namesPtr;

                if ((ReturnCode)_query(null, (IntPtr)(&desc)) != ReturnCode.Ok)
                {
                    return;
                }
            }

            for (ulong i = 0; i < count; i++)
            {
                string name = names[i] == IntPtr.Zero ? "(unnamed)" : Marshal.PtrToStringAnsi(names[i]);

                Logger.Info?.Print(LogClass.Gpu, $"FSR-FG:   {label}[{i}] id=0x{ids[i]:X} \"{name}\"");
            }
        }

        /// <summary>
        /// Routes FidelityFX diagnostics into the log. ffxConfigure rejects a null context pointer
        /// (ffx_api.cpp: VERIFY(context != nullptr, ...ERROR_PARAMETER)), despite the header saying
        /// otherwise, so this must be called with a context that already exists.
        /// </summary>
        public static unsafe void EnableDebugMessages(ref IntPtr context)
        {
            ConfigureDescGlobalDebug1 desc = new()
            {
                Header = new FfxApiHeader(ConfigureDescTypeGlobalDebug1),
                FpMessage = (IntPtr)(delegate* unmanaged[Cdecl]<uint, char*, void>)&DebugMessage,
                DebugLevel = GlobalDebugLevelVerbose,
            };

            ReturnCode code;

            fixed (IntPtr* ctx = &context)
            {
                code = (ReturnCode)_configure(ctx, (IntPtr)(&desc));
            }

            Logger.Info?.Print(LogClass.Gpu, $"FSR-FG: diagnostics callback registered ({code}).");
        }

        private static IntPtr _realGetDeviceProcAddr;

        /// <summary>Number of KHR-to-core redirections the shim performed.</summary>
        public static int ProcAddrRedirects;

        /// <summary>
        /// FidelityFX's Vulkan backend resolves promoted entry points by their KHR alias, e.g.
        /// "vkGetBufferMemoryRequirements2KHR", and calls the result without a null check. NVIDIA
        /// drivers return null for those aliases unless the corresponding extension was explicitly
        /// enabled, because the functions are core from Vulkan 1.1 -- which access-violates inside
        /// ffxCreateContext. This shim retries any failed KHR lookup under its core name.
        /// See FidelityFX-SDK issue #73.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe IntPtr GetDeviceProcAddrShim(IntPtr device, byte* name)
        {
            try
            {
                delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr> real =
                    (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr>)_realGetDeviceProcAddr;

                IntPtr resolved = real(device, name);

                if (resolved != IntPtr.Zero || name == null)
                {
                    return resolved;
                }

                int length = 0;

                while (name[length] != 0)
                {
                    length++;
                }

                if (length > 3 &&
                    name[length - 3] == (byte)'K' &&
                    name[length - 2] == (byte)'H' &&
                    name[length - 1] == (byte)'R')
                {
                    byte* core = stackalloc byte[length - 2];

                    for (int i = 0; i < length - 3; i++)
                    {
                        core[i] = name[i];
                    }

                    core[length - 3] = 0;

                    IntPtr coreResolved = real(device, core);

                    if (coreResolved != IntPtr.Zero)
                    {
                        ProcAddrRedirects++;

                        return coreResolved;
                    }
                }

                return IntPtr.Zero;
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Wraps <paramref name="realGetDeviceProcAddr"/> so promoted KHR aliases resolve to their
        /// core entry points. Pass the result as ffxCreateBackendVKDesc.vkDeviceProcAddr.
        /// </summary>
        public static unsafe IntPtr MakeDeviceProcAddrShim(IntPtr realGetDeviceProcAddr)
        {
            _realGetDeviceProcAddr = realGetDeviceProcAddr;
            ProcAddrRedirects = 0;

            return (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr>)&GetDeviceProcAddrShim;
        }

        public static unsafe ReturnCode CreateContext(ref IntPtr context, IntPtr desc)
        {
            fixed (IntPtr* contextPtr = &context)
            {
                return (ReturnCode)_createContext(contextPtr, desc, IntPtr.Zero);
            }
        }

        public static unsafe ReturnCode DestroyContext(ref IntPtr context)
        {
            fixed (IntPtr* contextPtr = &context)
            {
                return (ReturnCode)_destroyContext(contextPtr, IntPtr.Zero);
            }
        }

        public static unsafe ReturnCode Configure(ref IntPtr context, IntPtr desc)
        {
            fixed (IntPtr* contextPtr = &context)
            {
                return (ReturnCode)_configure(contextPtr, desc);
            }
        }

        public static unsafe ReturnCode Query(ref IntPtr context, IntPtr desc)
        {
            fixed (IntPtr* contextPtr = &context)
            {
                return (ReturnCode)_query(contextPtr, desc);
            }
        }

        public static unsafe ReturnCode Dispatch(ref IntPtr context, IntPtr desc)
        {
            fixed (IntPtr* contextPtr = &context)
            {
                return (ReturnCode)_dispatch(contextPtr, desc);
            }
        }

        public static void Unload()
        {
            if (_libraryHandle != IntPtr.Zero)
            {
                NativeLibrary.Free(_libraryHandle);
                _libraryHandle = IntPtr.Zero;
            }

            IsLoaded = false;
        }
    }
}
