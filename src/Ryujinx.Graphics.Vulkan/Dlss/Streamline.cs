
using Ryujinx.Common.Logging;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Vulkan.Dlss
{
    public static class Streamline
    {
        private const string InterposerName = "sl.interposer";
        private const string InterposerDll = "sl.interposer.dll";

        private const ulong KSdkVersion = ((ulong)2 << 48) | ((ulong)12 << 32) | ((ulong)0 << 16) | 0xfedc;

        public const uint FeatureDLSS = 0;
        public const uint FeatureNIS = 2;
        public const uint FeatureReflex = 3;
        public const uint FeaturePCL = 4;
        public const uint FeatureDLSS_G = 1000;
        public const uint FeatureDLSS_RR = 1001;

        private const uint RenderApiVulkan = 2;

        private const uint EngineTypeCustom = 0;

        private const uint LogLevelDefault = 1;
        private const uint LogLevelVerbose = 2;

        private const uint StructVersion1 = 1;
        private const uint StructVersion3 = 3;

        private const ulong PrefFlagDisableCLStateTracking = 1UL << 0;
        private const ulong PrefFlagUseManualHooking = 1UL << 2;
        private const ulong PrefFlagUseFrameBasedResourceTagging = 1UL << 7;

        public enum Result
        {
            Ok = 0,
            ErrorIO,
            ErrorDriverOutOfDate,
            ErrorOSOutOfDate,
            ErrorOSDisabledHWS,
            ErrorDeviceNotCreated,
            ErrorNoSupportedAdapterFound,
            ErrorAdapterNotSupported,
            ErrorNoPlugins,
            ErrorVulkanAPI,
            ErrorDXGIAPI,
            ErrorD3DAPI,
            ErrorNRDAPI,
            ErrorNVAPI,
            ErrorReflexAPI,
            ErrorNGXFailed,
            ErrorJSONParsing,
            ErrorMissingProxy,
            ErrorMissingResourceState,
            ErrorInvalidIntegration,
            ErrorMissingInputParameter,
            ErrorNotInitialized,
            ErrorComputeFailed,
            ErrorInitNotCalled,
            ErrorExceptionHandler,
            ErrorInvalidParameter,
            ErrorMissingConstants,
            ErrorDuplicatedConstants,
            ErrorMissingOrInvalidAPI,
            ErrorCommonConstantsMissing,
            ErrorUnsupportedInterface,
            ErrorFeatureMissing,
            ErrorFeatureNotSupported,
            ErrorFeatureMissingHooks,
            ErrorFeatureFailedToLoad,
            ErrorFeatureWrongPriority,
            ErrorFeatureMissingDependency,
            ErrorFeatureManagerInvalidState,
            ErrorInvalidState,
            WarnOutOfVRAM,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SlStructType
        {
            public uint Data1;
            public ushort Data2;
            public ushort Data3;
            public byte B0, B1, B2, B3, B4, B5, B6, B7;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Preferences
        {
            public IntPtr Next;
            public SlStructType StructType;
            public nuint StructVersion;
            public byte ShowConsole;
            public uint LogLevel;
            public IntPtr PathsToPlugins;
            public uint NumPathsToPlugins;
            public IntPtr PathToLogsAndData;
            public IntPtr AllocateCallback;
            public IntPtr ReleaseCallback;
            public IntPtr LogMessageCallback;
            public ulong Flags;
            public IntPtr FeaturesToLoad;
            public uint NumFeaturesToLoad;
            public uint ApplicationId;
            public uint Engine;
            public IntPtr EngineVersion;
            public IntPtr ProjectId;
            public uint RenderApi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AdapterInfo
        {
            public IntPtr Next;
            public SlStructType StructType;
            public nuint StructVersion;
            public IntPtr DeviceLuid;
            public uint DeviceLuidSizeInBytes;
            public IntPtr VkPhysicalDevice;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct VulkanInfo
        {
            public IntPtr Next;
            public SlStructType StructType;
            public nuint StructVersion;
            public IntPtr Device;
            public IntPtr Instance;
            public IntPtr PhysicalDevice;
            public uint ComputeQueueIndex;
            public uint ComputeQueueFamily;
            public uint GraphicsQueueIndex;
            public uint GraphicsQueueFamily;
            public uint OpticalFlowQueueIndex;
            public uint OpticalFlowQueueFamily;
            public byte UseNativeOpticalFlowMode;
            public uint ComputeQueueCreateFlags;
            public uint GraphicsQueueCreateFlags;
            public uint OpticalFlowQueueCreateFlags;
        }

        [DllImport(InterposerName, EntryPoint = "slInit", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result slInit(in Preferences pref, ulong sdkVersion);

        [DllImport(InterposerName, EntryPoint = "slShutdown", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result slShutdown();

        [DllImport(InterposerName, EntryPoint = "slIsFeatureSupported", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result slIsFeatureSupported(uint feature, in AdapterInfo adapterInfo);

        [DllImport(InterposerName, EntryPoint = "slSetVulkanInfo", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result slSetVulkanInfo(in VulkanInfo info);

        [DllImport(InterposerName, EntryPoint = "vkGetDeviceProcAddr", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr slVkGetDeviceProcAddr(IntPtr device, [MarshalAs(UnmanagedType.LPStr)] string name);

        private static IntPtr _interposerHandle;
        private static IntPtr _logPathPtr;
        private static IntPtr _featuresPtr;
        private static bool _initialized;
        private static bool _everInitialized;

        public static bool IsInitialized => _initialized;

        public static bool WasEverInitialized => _everInitialized;

        public static unsafe bool Initialize(string streamlineFolder, bool loadFrameGeneration = false)
        {
            if (_initialized)
            {
                return true;
            }

            if (!OperatingSystem.IsWindows())
            {
                Logger.Info?.Print(LogClass.Gpu, "DLSS: Streamline is Windows-only; skipping.");

                return false;
            }

            string interposerPath = Path.Combine(streamlineFolder ?? string.Empty, InterposerDll);
            if (!File.Exists(interposerPath))
            {
                Logger.Info?.Print(LogClass.Gpu, $"DLSS: {InterposerDll} not found at \"{interposerPath}\"; DLSS unavailable.");

                return false;
            }

            if (!NativeLibrary.TryLoad(interposerPath, out _interposerHandle))
            {
                Logger.Warning?.Print(LogClass.Gpu, $"DLSS: failed to load \"{interposerPath}\".");

                return false;
            }

            Preferences pref = default;
            pref.StructType = MakeGuid(0x1ca10965, 0xbf8e, 0x432b, 0x8d, 0xa1, 0x67, 0x16, 0xd8, 0x79, 0xfb, 0x14);
            pref.StructVersion = StructVersion1;
            pref.ShowConsole = 0;
            pref.LogLevel = LogLevelDefault;
            _logPathPtr = Marshal.StringToHGlobalUni(streamlineFolder);
            pref.PathToLogsAndData = _logPathPtr;
            pref.Engine = EngineTypeCustom;
            pref.RenderApi = RenderApiVulkan;
            pref.Flags = PrefFlagDisableCLStateTracking;

            if (loadFrameGeneration)
            {
                pref.Flags |= PrefFlagUseManualHooking;

                pref.Flags |= PrefFlagUseFrameBasedResourceTagging;
            }

            uint[] features = loadFrameGeneration
                ? new[] { FeatureDLSS, FeatureDLSS_G, FeatureReflex, FeaturePCL }
                : new[] { FeatureDLSS };

            _featuresPtr = Marshal.AllocHGlobal(sizeof(uint) * features.Length);
            for (int i = 0; i < features.Length; i++)
            {
                Marshal.WriteInt32(_featuresPtr, i * sizeof(uint), (int)features[i]);
            }
            pref.FeaturesToLoad = _featuresPtr;
            pref.NumFeaturesToLoad = (uint)features.Length;

            if (loadFrameGeneration)
            {
                Logger.Info?.Print(LogClass.Gpu, "DLSS-FG: requesting the DLSS_G plugin at slInit (RYUJINX_DLSS_FG=1).");
            }

            Result result;
            try
            {
                result = slInit(in pref, KSdkVersion);
            }
            catch (DllNotFoundException ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"DLSS: slInit could not be resolved: {ex.Message}");

                return false;
            }

            if (result != Result.Ok)
            {
                Logger.Info?.Print(LogClass.Gpu, $"DLSS: slInit failed ({result}); DLSS unavailable.");

                return false;
            }

            _initialized = true;
            _everInitialized = true;
            Logger.Info?.Print(LogClass.Gpu, "DLSS: Streamline initialized.");

            return true;
        }

        public static bool IsDlssSupported(IntPtr vkPhysicalDevice)
        {
            if (!_initialized)
            {
                return false;
            }

            AdapterInfo adapter = default;
            adapter.StructType = MakeGuid(0x0677315f, 0xa746, 0x4492, 0x9f, 0x42, 0xcb, 0x61, 0x42, 0xc9, 0xc3, 0xd4);
            adapter.StructVersion = StructVersion1;
            adapter.VkPhysicalDevice = vkPhysicalDevice;

            Result result = slIsFeatureSupported(FeatureDLSS, in adapter);

            if (result == Result.Ok)
            {
                Logger.Info?.Print(LogClass.Gpu, "DLSS: available.");

                return true;
            }

            Logger.Info?.Print(LogClass.Gpu, $"DLSS: not available ({result}).");

            return false;
        }

        public static bool IsDlssGSupported(IntPtr vkPhysicalDevice)
        {
            if (!_initialized)
            {
                return false;
            }

            AdapterInfo adapter = default;
            adapter.StructType = MakeGuid(0x0677315f, 0xa746, 0x4492, 0x9f, 0x42, 0xcb, 0x61, 0x42, 0xc9, 0xc3, 0xd4);
            adapter.StructVersion = StructVersion1;
            adapter.VkPhysicalDevice = vkPhysicalDevice;

            Result result;
            try
            {
                result = slIsFeatureSupported(FeatureDLSS_G, in adapter);
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"DLSS-FG: slIsFeatureSupported(DLSS_G) threw: {ex.Message}");

                return false;
            }

            if (result == Result.Ok)
            {
                Logger.Info?.Print(LogClass.Gpu, "DLSS-FG: supported on this adapter.");

                return true;
            }

            string hint = result switch
            {
                Result.ErrorFeatureMissing or Result.ErrorNoPlugins =>
                    "plugin not loaded - is sl.dlss_g.dll next to sl.interposer.dll?",
                Result.ErrorFeatureFailedToLoad =>
                    "plugin found but failed to load - check sl.log (NGX binary? driver?).",
                Result.ErrorFeatureMissingDependency =>
                    "missing dependency - FG requires Reflex/PCL (sl.reflex.dll + sl.pcl.dll, and they may need to be in featuresToLoad).",
                Result.ErrorFeatureMissingHooks =>
                    "hook problem - FG could not install its present/queue hooks (mono-queue mapping suspect).",
                Result.ErrorAdapterNotSupported or Result.ErrorFeatureNotSupported =>
                    "this GPU/driver combination rejects FG.",
                Result.ErrorDriverOutOfDate =>
                    "driver too old for FG.",
                Result.ErrorNGXFailed =>
                    "NGX failed - nvngx_dlssg.dll missing or unloadable?",
                _ => "see sl.log for details.",
            };

            Logger.Info?.Print(LogClass.Gpu, $"DLSS-FG: not available ({result}) - {hint}");

            return false;
        }

        public static bool SetVulkanInfo(IntPtr instance, IntPtr physicalDevice, IntPtr device, uint queueFamily, uint slQueueStartIndex)
        {
            if (!_initialized)
            {
                return false;
            }

            VulkanInfo info = default;
            info.StructType = MakeGuid(0x0eed6fd5, 0x82cd, 0x43a9, 0xbd, 0xb5, 0x47, 0xa5, 0xba, 0x2f, 0x45, 0xd6);
            info.StructVersion = StructVersion3;
            info.Device = device;
            info.Instance = instance;
            info.PhysicalDevice = physicalDevice;
            info.GraphicsQueueFamily = queueFamily;
            info.GraphicsQueueIndex = slQueueStartIndex;
            info.ComputeQueueFamily = queueFamily;
            info.ComputeQueueIndex = slQueueStartIndex;
            info.OpticalFlowQueueFamily = queueFamily;
            info.OpticalFlowQueueIndex = slQueueStartIndex;

            Result result = slSetVulkanInfo(in info);

            if (result == Result.Ok)
            {
                Logger.Info?.Print(LogClass.Gpu, "DLSS: Vulkan device registered with Streamline.");

                return true;
            }

            Logger.Warning?.Print(LogClass.Gpu, $"DLSS: slSetVulkanInfo failed ({result}).");

            return false;
        }

        public static IntPtr GetVulkanDeviceProxy(IntPtr device, string name)
        {
            if (!_initialized)
            {
                return IntPtr.Zero;
            }

            try
            {
                return slVkGetDeviceProcAddr(device, name);
            }
            catch (EntryPointNotFoundException)
            {
                return IntPtr.Zero;
            }
        }

        public static void Shutdown()
        {
            if (_initialized)
            {
                slShutdown();
                _initialized = false;
            }

            if (_logPathPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_logPathPtr);
                _logPathPtr = IntPtr.Zero;
            }

            if (_featuresPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_featuresPtr);
                _featuresPtr = IntPtr.Zero;
            }

            if (_interposerHandle != IntPtr.Zero)
            {
                NativeLibrary.Free(_interposerHandle);
                _interposerHandle = IntPtr.Zero;
            }
        }

        private static SlStructType MakeGuid(uint d1, ushort d2, ushort d3, byte b0, byte b1, byte b2, byte b3, byte b4, byte b5, byte b6, byte b7)
        {
            return new SlStructType
            {
                Data1 = d1,
                Data2 = d2,
                Data3 = d3,
                B0 = b0, B1 = b1, B2 = b2, B3 = b3, B4 = b4, B5 = b5, B6 = b6, B7 = b7,
            };
        }
    }
}
