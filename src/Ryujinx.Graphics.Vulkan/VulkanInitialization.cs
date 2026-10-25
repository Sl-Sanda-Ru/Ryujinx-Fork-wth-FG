using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Vulkan
{
    public unsafe static class VulkanInitialization
    {
        private const uint InvalidIndex = uint.MaxValue;
        private static readonly uint _minimalVulkanVersion = Vk.Version11.Value;
        private static readonly uint _minimalInstanceVulkanVersion = Vk.Version12.Value;
        private static readonly uint _maximumVulkanVersion = Vk.Version12.Value;
        private const string AppName = "Ryujinx.Graphics.Vulkan";
        private const int QueuesCount = 2;

        private static readonly string[] _desirableExtensions =
        [
            ExtConditionalRendering.ExtensionName,
            ExtExtendedDynamicState.ExtensionName,
            ExtTransformFeedback.ExtensionName,
            KhrDrawIndirectCount.ExtensionName,
            KhrPushDescriptor.ExtensionName,
            ExtExternalMemoryHost.ExtensionName,
            "VK_EXT_blend_operation_advanced",
            "VK_EXT_custom_border_color",
            "VK_EXT_descriptor_indexing", // Enabling this works around an issue with disposed buffer bindings on RADV.
            "VK_EXT_fragment_shader_interlock",
            "VK_EXT_index_type_uint8",
            "VK_EXT_primitive_topology_list_restart",
            "VK_EXT_robustness2",
            "VK_EXT_shader_stencil_export",
            "VK_KHR_shader_float16_int8",
            "VK_EXT_shader_subgroup_ballot",
            "VK_NV_geometry_shader_passthrough",
            "VK_NV_viewport_array2",
            "VK_EXT_depth_clip_control",
            "VK_KHR_portability_subset", // As per spec, we should enable this if present.
            "VK_EXT_4444_formats",
            "VK_KHR_8bit_storage",
            "VK_KHR_maintenance2",
            "VK_EXT_attachment_feedback_loop_layout",
            "VK_EXT_attachment_feedback_loop_dynamic_state"
        ];

        private static readonly string[] _requiredExtensions =
        [
            KhrSwapchain.ExtensionName
        ];

        internal static VulkanInstance CreateInstance(Vk api, GraphicsDebugLevel logLevel, string[] requiredExtensions)
        {
            List<string> enabledLayers = [];

            IReadOnlySet<string> instanceExtensions = VulkanInstance.GetInstanceExtensions(api);
            IReadOnlySet<string> instanceLayers = VulkanInstance.GetInstanceLayers(api);

            void AddAvailableLayer(string layerName)
            {
                if (instanceLayers.Contains(layerName))
                {
                    enabledLayers.Add(layerName);
                }
                else
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"Missing layer {layerName}");
                }
            }

            if (logLevel != GraphicsDebugLevel.None)
            {
                AddAvailableLayer("VK_LAYER_KHRONOS_validation");
            }

            string[] enabledExtensions = requiredExtensions;

            if (instanceExtensions.Contains("VK_EXT_debug_utils"))
            {
                enabledExtensions = enabledExtensions.Append(ExtDebugUtils.ExtensionName).ToArray();
            }

            nint appName = Marshal.StringToHGlobalAnsi(AppName);

            ApplicationInfo applicationInfo = new()
            {
                PApplicationName = (byte*)appName,
                ApplicationVersion = 1,
                PEngineName = (byte*)appName,
                EngineVersion = 1,
                ApiVersion = _maximumVulkanVersion,
            };

            nint* ppEnabledExtensions = stackalloc nint[enabledExtensions.Length];
            nint* ppEnabledLayers = stackalloc nint[enabledLayers.Count];

            for (int i = 0; i < enabledExtensions.Length; i++)
            {
                ppEnabledExtensions[i] = Marshal.StringToHGlobalAnsi(enabledExtensions[i]);
            }

            for (int i = 0; i < enabledLayers.Count; i++)
            {
                ppEnabledLayers[i] = Marshal.StringToHGlobalAnsi(enabledLayers[i]);
            }

            InstanceCreateInfo instanceCreateInfo = new()
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &applicationInfo,
                PpEnabledExtensionNames = (byte**)ppEnabledExtensions,
                PpEnabledLayerNames = (byte**)ppEnabledLayers,
                EnabledExtensionCount = (uint)enabledExtensions.Length,
                EnabledLayerCount = (uint)enabledLayers.Count,
            };

            Result result = VulkanInstance.Create(api, ref instanceCreateInfo, out VulkanInstance instance);

            Marshal.FreeHGlobal(appName);

            for (int i = 0; i < enabledExtensions.Length; i++)
            {
                Marshal.FreeHGlobal(ppEnabledExtensions[i]);
            }

            for (int i = 0; i < enabledLayers.Count; i++)
            {
                Marshal.FreeHGlobal(ppEnabledLayers[i]);
            }

            result.ThrowOnError();

            return instance;
        }

        internal static VulkanPhysicalDevice FindSuitablePhysicalDevice(Vk api, VulkanInstance instance, SurfaceKHR surface, string preferredGpuId)
        {
            instance.EnumeratePhysicalDevices(out VulkanPhysicalDevice[] physicalDevices).ThrowOnError();

            // First we try to pick the user preferred GPU.
            for (int i = 0; i < physicalDevices.Length; i++)
            {
                if (IsPreferredAndSuitableDevice(api, physicalDevices[i], surface, preferredGpuId))
                {
                    return physicalDevices[i];
                }
            }

            // If we fail to do that, just use the first compatible GPU.
            for (int i = 0; i < physicalDevices.Length; i++)
            {
                if (IsSuitableDevice(api, physicalDevices[i], surface))
                {
                    return physicalDevices[i];
                }
            }

            throw new VulkanException("Initialization failed, none of the available GPUs meets the minimum requirements.");
        }

        internal static DeviceInfo[] GetSuitablePhysicalDevices(Vk api)
        {
            nint appName = Marshal.StringToHGlobalAnsi(AppName);

            ApplicationInfo applicationInfo = new()
            {
                PApplicationName = (byte*)appName,
                ApplicationVersion = 1,
                PEngineName = (byte*)appName,
                EngineVersion = 1,
                ApiVersion = _maximumVulkanVersion,
            };

            InstanceCreateInfo instanceCreateInfo = new()
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &applicationInfo,
                PpEnabledExtensionNames = null,
                PpEnabledLayerNames = null,
                EnabledExtensionCount = 0,
                EnabledLayerCount = 0,
            };

            Result result = VulkanInstance.Create(api, ref instanceCreateInfo, out VulkanInstance rawInstance);

            Marshal.FreeHGlobal(appName);

            result.ThrowOnError();

            using VulkanInstance instance = rawInstance;

            // We currently assume that the instance is compatible with Vulkan 1.2
            // TODO: Remove this once we relax our initialization codepaths.
            if (instance.InstanceVersion < _minimalInstanceVulkanVersion)
            {
                return [];
            }

            instance.EnumeratePhysicalDevices(out VulkanPhysicalDevice[] physicalDevices).ThrowOnError();

            List<DeviceInfo> deviceInfos = [];

            foreach (VulkanPhysicalDevice physicalDevice in physicalDevices)
            {
                if (physicalDevice.PhysicalDeviceProperties.ApiVersion < _minimalVulkanVersion)
                {
                    continue;
                }

                deviceInfos.Add(physicalDevice.ToDeviceInfo());
            }

            return deviceInfos.ToArray();
        }

        private static bool IsPreferredAndSuitableDevice(Vk api, VulkanPhysicalDevice physicalDevice, SurfaceKHR surface, string preferredGpuId)
        {
            if (physicalDevice.Id != preferredGpuId)
            {
                return false;
            }

            return IsSuitableDevice(api, physicalDevice, surface);
        }

        private static bool IsSuitableDevice(Vk api, VulkanPhysicalDevice physicalDevice, SurfaceKHR surface)
        {
            int extensionMatches = 0;

            foreach (string requiredExtension in _requiredExtensions)
            {
                if (physicalDevice.IsDeviceExtensionPresent(requiredExtension))
                {
                    extensionMatches++;
                }
            }

            return extensionMatches == _requiredExtensions.Length && FindSuitableQueueFamily(api, physicalDevice, surface, out _) != InvalidIndex;
        }

        internal static uint FindSuitableQueueFamily(Vk api, VulkanPhysicalDevice physicalDevice, SurfaceKHR surface, out uint queueCount)
        {
            const QueueFlags RequiredFlags = QueueFlags.GraphicsBit | QueueFlags.ComputeBit;

            KhrSurface khrSurface = new(api.Context);

            for (uint index = 0; index < physicalDevice.QueueFamilyProperties.Length; index++)
            {
                ref QueueFamilyProperties property = ref physicalDevice.QueueFamilyProperties[index];

                khrSurface.GetPhysicalDeviceSurfaceSupport(physicalDevice.PhysicalDevice, index, surface, out Bool32 surfaceSupported).ThrowOnError();

                if (property.QueueFlags.HasFlag(RequiredFlags) && surfaceSupported)
                {
                    queueCount = property.QueueCount;

                    return index;
                }
            }

            queueCount = 0;

            return InvalidIndex;
        }

        internal static Device CreateDevice(
            Vk api,
            VulkanPhysicalDevice physicalDevice,
            uint queueFamilyIndex,
            uint queueCount,
            out uint opticalFlowQueueFamilyIndex,
            out uint fsrFgQueueFamilyIndex,
            out uint fsrFgQueueCount)
        {
            // FidelityFX frame generation requires four *distinct* queues, so it needs the same
            // lifted cap that DLSS-FG uses.
            bool wantsManyQueues = Dlss.DlssIntegration.FrameGenRequested || FsrFg.FsrFgApi.Requested;
            int cap = wantsManyQueues ? (int)queueCount : QueuesCount;
            if (queueCount > cap)
            {
                queueCount = (uint)cap;
            }

            float* queuePriorities = stackalloc float[(int)queueCount];

            for (int i = 0; i < queueCount; i++)
            {
                queuePriorities[i] = 1f;
            }

            opticalFlowQueueFamilyIndex = uint.MaxValue;
            if (Dlss.DlssIntegration.IsEnabled && physicalDevice.IsDeviceExtensionPresent("VK_NV_optical_flow"))
            {
                for (uint i = 0; i < physicalDevice.QueueFamilyProperties.Length; i++)
                {
                    if (i != queueFamilyIndex && ((uint)physicalDevice.QueueFamilyProperties[i].QueueFlags & 0x100u) != 0)
                    {
                        opticalFlowQueueFamilyIndex = i;

                        QueueFamilyProperties ofaProps = physicalDevice.QueueFamilyProperties[i];
                        QueueFamilyProperties gfxProps = physicalDevice.QueueFamilyProperties[queueFamilyIndex];
                        Logger.Info?.Print(LogClass.Gpu,
                            $"NVOFA: optical-flow queue family {i} selected -- queueCount={ofaProps.QueueCount}, " +
                            $"flags=0x{(uint)ofaProps.QueueFlags:X}, timestampValidBits={ofaProps.TimestampValidBits}; " +
                            $"graphics family {queueFamilyIndex} queueCount={gfxProps.QueueCount}; total families={physicalDevice.QueueFamilyProperties.Length}.");

                        break;
                    }
                }
            }

            // FidelityFX wants four distinct queues but places no requirement that they share a
            // family -- its only family rule is that each index exists. Intel never exposes a second
            // queue in the graphics family on any generation, and AMD exposes one there as well, so
            // on those parts the extra three roles have to come from another family or frame
            // generation can never arm. Three are taken because the game queue stays the graphics one.
            const uint FsrFgAuxQueuesWanted = 3;

            fsrFgQueueFamilyIndex = uint.MaxValue;
            fsrFgQueueCount = 0;

            if (FsrFg.FsrFgApi.Requested && queueCount < 4)
            {
                uint bestCount = 0;

                for (uint i = 0; i < physicalDevice.QueueFamilyProperties.Length; i++)
                {
                    if (i == queueFamilyIndex || i == opticalFlowQueueFamilyIndex)
                    {
                        continue;
                    }

                    QueueFamilyProperties candidate = physicalDevice.QueueFamilyProperties[i];

                    // Compute is what the interpolation workload needs; a transfer- or video-only
                    // family cannot run it. Presentation support is checked later, against the
                    // surface, which is not available here.
                    if ((candidate.QueueFlags & QueueFlags.ComputeBit) == 0)
                    {
                        continue;
                    }

                    if (candidate.QueueCount > bestCount)
                    {
                        bestCount = candidate.QueueCount;
                        fsrFgQueueFamilyIndex = i;
                    }
                }

                if (fsrFgQueueFamilyIndex != uint.MaxValue && bestCount >= FsrFgAuxQueuesWanted)
                {
                    fsrFgQueueCount = FsrFgAuxQueuesWanted;

                    Logger.Info?.Print(LogClass.Gpu,
                        $"FSR-FG: graphics family {queueFamilyIndex} offers {queueCount} queue(s), fewer than the four " +
                        $"FidelityFX needs; taking {fsrFgQueueCount} from compute family {fsrFgQueueFamilyIndex} " +
                        $"(queueCount={bestCount}).");
                }
                else
                {
                    fsrFgQueueFamilyIndex = uint.MaxValue;
                }
            }

            float ofaPriority = 1f;
            float* fsrFgPriorities = stackalloc float[(int)FsrFgAuxQueuesWanted];

            for (int i = 0; i < FsrFgAuxQueuesWanted; i++)
            {
                fsrFgPriorities[i] = 1f;
            }

            uint queueCreateInfoCount = 1u;
            DeviceQueueCreateInfo* queueCreateInfos = stackalloc DeviceQueueCreateInfo[3];

            queueCreateInfos[0] = new()
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = queueFamilyIndex,
                QueueCount = queueCount,
                PQueuePriorities = queuePriorities,
            };

            // Tracked so the entry can be taken back out if the optical-flow queue is later ruled out.
            int ofaQueueSlot = -1;

            if (opticalFlowQueueFamilyIndex != uint.MaxValue)
            {
                ofaQueueSlot = (int)queueCreateInfoCount;
                queueCreateInfos[queueCreateInfoCount++] = new()
                {
                    SType = StructureType.DeviceQueueCreateInfo,
                    QueueFamilyIndex = opticalFlowQueueFamilyIndex,
                    QueueCount = 1,
                    PQueuePriorities = &ofaPriority,
                };
            }

            if (fsrFgQueueFamilyIndex != uint.MaxValue)
            {
                queueCreateInfos[queueCreateInfoCount++] = new()
                {
                    SType = StructureType.DeviceQueueCreateInfo,
                    QueueFamilyIndex = fsrFgQueueFamilyIndex,
                    QueueCount = fsrFgQueueCount,
                    PQueuePriorities = fsrFgPriorities,
                };
            }

            bool useRobustBufferAccess = VendorUtils.FromId(physicalDevice.PhysicalDeviceProperties.VendorID) == Vendor.Nvidia;

            PhysicalDeviceFeatures2 features2 = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
            };

            PhysicalDeviceVulkan11Features supportedFeaturesVk11 = new()
            {
                SType = StructureType.PhysicalDeviceVulkan11Features,
                PNext = features2.PNext,
            };

            features2.PNext = &supportedFeaturesVk11;

            PhysicalDeviceCustomBorderColorFeaturesEXT supportedFeaturesCustomBorderColor = new()
            {
                SType = StructureType.PhysicalDeviceCustomBorderColorFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_custom_border_color"))
            {
                features2.PNext = &supportedFeaturesCustomBorderColor;
            }

            PhysicalDevicePrimitiveTopologyListRestartFeaturesEXT supportedFeaturesPrimitiveTopologyListRestart = new()
            {
                SType = StructureType.PhysicalDevicePrimitiveTopologyListRestartFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_primitive_topology_list_restart"))
            {
                features2.PNext = &supportedFeaturesPrimitiveTopologyListRestart;
            }

            PhysicalDeviceTransformFeedbackFeaturesEXT supportedFeaturesTransformFeedback = new()
            {
                SType = StructureType.PhysicalDeviceTransformFeedbackFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent(ExtTransformFeedback.ExtensionName))
            {
                features2.PNext = &supportedFeaturesTransformFeedback;
            }

            PhysicalDeviceRobustness2FeaturesEXT supportedFeaturesRobustness2 = new()
            {
                SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_robustness2"))
            {
                supportedFeaturesRobustness2.PNext = features2.PNext;

                features2.PNext = &supportedFeaturesRobustness2;
            }

            PhysicalDeviceDepthClipControlFeaturesEXT supportedFeaturesDepthClipControl = new()
            {
                SType = StructureType.PhysicalDeviceDepthClipControlFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_depth_clip_control"))
            {
                features2.PNext = &supportedFeaturesDepthClipControl;
            }

            PhysicalDeviceProvokingVertexFeaturesEXT supportedFeaturesProvokingVertex = new()
            {
                SType = StructureType.PhysicalDeviceProvokingVertexFeaturesExt,
                PNext = features2.PNext,
            };

            if (ProvokingVertexOverride.Requested &&
                physicalDevice.IsDeviceExtensionPresent(ProvokingVertexOverride.ExtensionName))
            {
                features2.PNext = &supportedFeaturesProvokingVertex;
            }

            PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesEXT supportedFeaturesAttachmentFeedbackLoopLayout = new()
            {
                SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_layout"))
            {
                features2.PNext = &supportedFeaturesAttachmentFeedbackLoopLayout;
            }

            PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesEXT supportedFeaturesDynamicAttachmentFeedbackLoopLayout = new()
            {
                SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_dynamic_state"))
            {
                features2.PNext = &supportedFeaturesDynamicAttachmentFeedbackLoopLayout;
            }

            PhysicalDeviceSynchronization2Features supportedFeaturesSync2 = new()
            {
                SType = StructureType.PhysicalDeviceSynchronization2Features,
            };

            PhysicalDeviceOpticalFlowFeaturesNV supportedFeaturesOpticalFlow = new()
            {
                SType = StructureType.PhysicalDeviceOpticalFlowFeaturesNV,
            };

            if (Dlss.DlssIntegration.IsEnabled && physicalDevice.IsDeviceExtensionPresent("VK_NV_optical_flow"))
            {
                supportedFeaturesSync2.PNext = features2.PNext;
                features2.PNext = &supportedFeaturesSync2;

                supportedFeaturesOpticalFlow.PNext = features2.PNext;
                features2.PNext = &supportedFeaturesOpticalFlow;
            }

            PhysicalDeviceVulkan12Features supportedPhysicalDeviceVulkan12Features = new()
            {
                SType = StructureType.PhysicalDeviceVulkan12Features,
                PNext = features2.PNext,
            };

            features2.PNext = &supportedPhysicalDeviceVulkan12Features;

            api.GetPhysicalDeviceFeatures2(physicalDevice.PhysicalDevice, &features2);

            PhysicalDeviceFeatures supportedFeatures = features2.Features;

            PhysicalDeviceFeatures features = new()
            {
                DepthBiasClamp = supportedFeatures.DepthBiasClamp,
                DepthClamp = supportedFeatures.DepthClamp,
                DualSrcBlend = supportedFeatures.DualSrcBlend,
                FragmentStoresAndAtomics = supportedFeatures.FragmentStoresAndAtomics,
                GeometryShader = supportedFeatures.GeometryShader,
                ImageCubeArray = supportedFeatures.ImageCubeArray,
                IndependentBlend = supportedFeatures.IndependentBlend,
                LogicOp = supportedFeatures.LogicOp,
                OcclusionQueryPrecise = supportedFeatures.OcclusionQueryPrecise,
                MultiViewport = supportedFeatures.MultiViewport,
                PipelineStatisticsQuery = supportedFeatures.PipelineStatisticsQuery,
                SamplerAnisotropy = supportedFeatures.SamplerAnisotropy,
                ShaderClipDistance = supportedFeatures.ShaderClipDistance,
                ShaderFloat64 = supportedFeatures.ShaderFloat64,
                ShaderImageGatherExtended = supportedFeatures.ShaderImageGatherExtended,
                ShaderStorageImageMultisample = supportedFeatures.ShaderStorageImageMultisample,
                ShaderStorageImageReadWithoutFormat = supportedFeatures.ShaderStorageImageReadWithoutFormat,
                ShaderStorageImageWriteWithoutFormat = supportedFeatures.ShaderStorageImageWriteWithoutFormat,
                TessellationShader = supportedFeatures.TessellationShader,
                VertexPipelineStoresAndAtomics = supportedFeatures.VertexPipelineStoresAndAtomics,
                RobustBufferAccess = useRobustBufferAccess,
                SampleRateShading = supportedFeatures.SampleRateShading,
            };

            void* pExtendedFeatures = null;

            PhysicalDeviceTransformFeedbackFeaturesEXT featuresTransformFeedback;

            if (physicalDevice.IsDeviceExtensionPresent(ExtTransformFeedback.ExtensionName))
            {
                featuresTransformFeedback = new PhysicalDeviceTransformFeedbackFeaturesEXT
                {
                    SType = StructureType.PhysicalDeviceTransformFeedbackFeaturesExt,
                    PNext = pExtendedFeatures,
                    TransformFeedback = supportedFeaturesTransformFeedback.TransformFeedback,
                };

                pExtendedFeatures = &featuresTransformFeedback;
            }

            PhysicalDevicePrimitiveTopologyListRestartFeaturesEXT featuresPrimitiveTopologyListRestart;

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_primitive_topology_list_restart"))
            {
                featuresPrimitiveTopologyListRestart = new PhysicalDevicePrimitiveTopologyListRestartFeaturesEXT
                {
                    SType = StructureType.PhysicalDevicePrimitiveTopologyListRestartFeaturesExt,
                    PNext = pExtendedFeatures,
                    PrimitiveTopologyListRestart = supportedFeaturesPrimitiveTopologyListRestart.PrimitiveTopologyListRestart,
                    PrimitiveTopologyPatchListRestart = supportedFeaturesPrimitiveTopologyListRestart.PrimitiveTopologyPatchListRestart,
                };

                pExtendedFeatures = &featuresPrimitiveTopologyListRestart;
            }

            PhysicalDeviceRobustness2FeaturesEXT featuresRobustness2;

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_robustness2"))
            {
                featuresRobustness2 = new PhysicalDeviceRobustness2FeaturesEXT
                {
                    SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
                    PNext = pExtendedFeatures,
                    NullDescriptor = supportedFeaturesRobustness2.NullDescriptor,
                };

                pExtendedFeatures = &featuresRobustness2;
            }

            PhysicalDeviceExtendedDynamicStateFeaturesEXT featuresExtendedDynamicState = new()
            {
                SType = StructureType.PhysicalDeviceExtendedDynamicStateFeaturesExt,
                PNext = pExtendedFeatures,
                ExtendedDynamicState = physicalDevice.IsDeviceExtensionPresent(ExtExtendedDynamicState.ExtensionName),
            };

            pExtendedFeatures = &featuresExtendedDynamicState;

            PhysicalDeviceVulkan11Features featuresVk11 = new()
            {
                SType = StructureType.PhysicalDeviceVulkan11Features,
                PNext = pExtendedFeatures,
                ShaderDrawParameters = supportedFeaturesVk11.ShaderDrawParameters,
            };

            pExtendedFeatures = &featuresVk11;

            PhysicalDeviceVulkan12Features featuresVk12 = new()
            {
                SType = StructureType.PhysicalDeviceVulkan12Features,
                PNext = pExtendedFeatures,
                DescriptorIndexing = supportedPhysicalDeviceVulkan12Features.DescriptorIndexing,
                DrawIndirectCount = supportedPhysicalDeviceVulkan12Features.DrawIndirectCount,
                UniformBufferStandardLayout = supportedPhysicalDeviceVulkan12Features.UniformBufferStandardLayout,
                UniformAndStorageBuffer8BitAccess = supportedPhysicalDeviceVulkan12Features.UniformAndStorageBuffer8BitAccess,
                StorageBuffer8BitAccess = supportedPhysicalDeviceVulkan12Features.StorageBuffer8BitAccess,
                ShaderSampledImageArrayNonUniformIndexing = supportedPhysicalDeviceVulkan12Features.ShaderSampledImageArrayNonUniformIndexing,
                ShaderStorageImageArrayNonUniformIndexing = supportedPhysicalDeviceVulkan12Features.ShaderStorageImageArrayNonUniformIndexing,
            };

            if (Dlss.DlssIntegration.IsEnabled)
            {
                featuresVk12.TimelineSemaphore = supportedPhysicalDeviceVulkan12Features.TimelineSemaphore;
                featuresVk12.BufferDeviceAddress = supportedPhysicalDeviceVulkan12Features.BufferDeviceAddress;
            }

            pExtendedFeatures = &featuresVk12;

            PhysicalDeviceIndexTypeUint8FeaturesEXT featuresIndexU8;

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_index_type_uint8"))
            {
                featuresIndexU8 = new PhysicalDeviceIndexTypeUint8FeaturesEXT
                {
                    SType = StructureType.PhysicalDeviceIndexTypeUint8FeaturesExt,
                    PNext = pExtendedFeatures,
                    IndexTypeUint8 = true,
                };

                pExtendedFeatures = &featuresIndexU8;
            }

            PhysicalDeviceFragmentShaderInterlockFeaturesEXT featuresFragmentShaderInterlock;

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_fragment_shader_interlock"))
            {
                featuresFragmentShaderInterlock = new PhysicalDeviceFragmentShaderInterlockFeaturesEXT
                {
                    SType = StructureType.PhysicalDeviceFragmentShaderInterlockFeaturesExt,
                    PNext = pExtendedFeatures,
                    FragmentShaderPixelInterlock = true,
                };

                pExtendedFeatures = &featuresFragmentShaderInterlock;
            }

            PhysicalDeviceCustomBorderColorFeaturesEXT featuresCustomBorderColor;

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_custom_border_color") &&
                supportedFeaturesCustomBorderColor.CustomBorderColors &&
                supportedFeaturesCustomBorderColor.CustomBorderColorWithoutFormat)
            {
                featuresCustomBorderColor = new PhysicalDeviceCustomBorderColorFeaturesEXT
                {
                    SType = StructureType.PhysicalDeviceCustomBorderColorFeaturesExt,
                    PNext = pExtendedFeatures,
                    CustomBorderColors = true,
                    CustomBorderColorWithoutFormat = true,
                };

                pExtendedFeatures = &featuresCustomBorderColor;
            }

            PhysicalDeviceDepthClipControlFeaturesEXT featuresDepthClipControl;

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_depth_clip_control") &&
                supportedFeaturesDepthClipControl.DepthClipControl)
            {
                featuresDepthClipControl = new PhysicalDeviceDepthClipControlFeaturesEXT
                {
                    SType = StructureType.PhysicalDeviceDepthClipControlFeaturesExt,
                    PNext = pExtendedFeatures,
                    DepthClipControl = true,
                };

                pExtendedFeatures = &featuresDepthClipControl;
            }

            PhysicalDeviceProvokingVertexFeaturesEXT featuresProvokingVertex;

            if (ProvokingVertexOverride.Requested &&
                physicalDevice.IsDeviceExtensionPresent(ProvokingVertexOverride.ExtensionName) &&
                supportedFeaturesProvokingVertex.ProvokingVertexLast)
            {
                featuresProvokingVertex = new PhysicalDeviceProvokingVertexFeaturesEXT
                {
                    SType = StructureType.PhysicalDeviceProvokingVertexFeaturesExt,
                    PNext = pExtendedFeatures,
                    ProvokingVertexLast = true,
                };

                pExtendedFeatures = &featuresProvokingVertex;

                ProvokingVertexOverride.Active = true;
            }

            if (ProvokingVertexOverride.Requested)
            {
                if (ProvokingVertexOverride.Active)
                {
                    Logger.Info?.Print(LogClass.Gpu, "PROVOKING: ARME - flat varyings resolved from the LAST vertex");
                }
                else
                {
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"PROVOKING: DEMANDE MAIS INACTIF - extension present: {physicalDevice.IsDeviceExtensionPresent(ProvokingVertexOverride.ExtensionName)}, " +
                        $"provokingVertexLast: {supportedFeaturesProvokingVertex.ProvokingVertexLast}. Le test n'a PAS eu lieu.");
                }
            }

            PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesEXT featuresAttachmentFeedbackLoopLayout;

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_layout") &&
                supportedFeaturesAttachmentFeedbackLoopLayout.AttachmentFeedbackLoopLayout)
            {
                featuresAttachmentFeedbackLoopLayout = new()
                {
                    SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesExt,
                    PNext = pExtendedFeatures,
                    AttachmentFeedbackLoopLayout = true,
                };

                pExtendedFeatures = &featuresAttachmentFeedbackLoopLayout;
            }

            PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesEXT featuresDynamicAttachmentFeedbackLoopLayout;

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_dynamic_state") &&
                supportedFeaturesDynamicAttachmentFeedbackLoopLayout.AttachmentFeedbackLoopDynamicState)
            {
                featuresDynamicAttachmentFeedbackLoopLayout = new()
                {
                    SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesExt,
                    PNext = pExtendedFeatures,
                    AttachmentFeedbackLoopDynamicState = true,
                };

                pExtendedFeatures = &featuresDynamicAttachmentFeedbackLoopLayout;
            }

            bool forceNoOpticalFlowQueue =
                Environment.GetEnvironmentVariable("RYUJINX_DLSS_NO_OFA") is "1" or "true" or "on";

            bool opticalFlowExtPresent =
                Dlss.DlssIntegration.IsEnabled && physicalDevice.IsDeviceExtensionPresent("VK_NV_optical_flow");
            bool sync2Supported = opticalFlowExtPresent && supportedFeaturesSync2.Synchronization2;
            bool opticalFlowSupported = opticalFlowExtPresent && supportedFeaturesOpticalFlow.OpticalFlow;

            bool useOpticalFlowQueue =
                !forceNoOpticalFlowQueue && opticalFlowQueueFamilyIndex != uint.MaxValue && sync2Supported && opticalFlowSupported;

            if (!useOpticalFlowQueue && opticalFlowQueueFamilyIndex != uint.MaxValue)
            {
                string why = forceNoOpticalFlowQueue ? "forced off (RYUJINX_DLSS_NO_OFA)" :
                    !sync2Supported ? "synchronization2 feature not supported" :
                    "opticalFlow feature not supported";
                Logger.Info?.Print(LogClass.Gpu, $"NVOFA: dedicated optical-flow queue not requested ({why}); DLSS/FG unaffected, NvOpticalFlow uses Lucas-Kanade.");
                opticalFlowQueueFamilyIndex = uint.MaxValue;

                // The queue entry was already built above; leaving it in would still create the queue.
                RemoveQueueCreateInfo(queueCreateInfos, ref queueCreateInfoCount, ofaQueueSlot);
                ofaQueueSlot = -1;
            }

            PhysicalDeviceSynchronization2Features featuresSync2;
            PhysicalDeviceOpticalFlowFeaturesNV featuresOpticalFlow;

            if (sync2Supported)
            {
                featuresSync2 = new()
                {
                    SType = StructureType.PhysicalDeviceSynchronization2Features,
                    PNext = pExtendedFeatures,
                    Synchronization2 = true,
                };

                pExtendedFeatures = &featuresSync2;
            }

            if (opticalFlowSupported)
            {
                featuresOpticalFlow = new()
                {
                    SType = StructureType.PhysicalDeviceOpticalFlowFeaturesNV,
                    PNext = pExtendedFeatures,
                    OpticalFlow = true,
                };

                pExtendedFeatures = &featuresOpticalFlow;
            }

            string[] enabledExtensions = _requiredExtensions.Union(_desirableExtensions.Intersect(physicalDevice.DeviceExtensions)).ToArray();

            if (ProvokingVertexOverride.Active)
            {
                enabledExtensions = enabledExtensions.Append(ProvokingVertexOverride.ExtensionName).ToArray();
            }

            if (Dlss.DlssIntegration.IsEnabled)
            {
                enabledExtensions = enabledExtensions
                    .Union(Dlss.DlssIntegration.DeviceExtensions.Intersect(physicalDevice.DeviceExtensions))
                    .ToArray();
            }

            if (opticalFlowExtPresent)
            {
                uint apiVer = physicalDevice.PhysicalDeviceProperties.ApiVersion;
                uint drvVer = physicalDevice.PhysicalDeviceProperties.DriverVersion;

                Logger.Info?.Print(LogClass.Gpu,
                    $"NVOFA DIAG: device '{physicalDevice.DeviceName}' " +
                    $"apiVersion={(apiVer >> 22) & 0x7F}.{(apiVer >> 12) & 0x3FF}.{apiVer & 0xFFF} driverVersion=0x{drvVer:X}; " +
                    $"VK_NV_optical_flow advertised={physicalDevice.IsDeviceExtensionPresent("VK_NV_optical_flow")}/enabled={enabledExtensions.Contains("VK_NV_optical_flow")}, " +
                    $"VK_KHR_synchronization2 advertised={physicalDevice.IsDeviceExtensionPresent("VK_KHR_synchronization2")}/enabled={enabledExtensions.Contains("VK_KHR_synchronization2")}, " +
                    $"VK_KHR_format_feature_flags2 advertised={physicalDevice.IsDeviceExtensionPresent("VK_KHR_format_feature_flags2")}/enabled={enabledExtensions.Contains("VK_KHR_format_feature_flags2")}; " +
                    $"featureQuery: sync2Supported={sync2Supported}, opticalFlowSupported={opticalFlowSupported}.");
            }

            nint* ppEnabledExtensions = stackalloc nint[enabledExtensions.Length];

            for (int i = 0; i < enabledExtensions.Length; i++)
            {
                ppEnabledExtensions[i] = Marshal.StringToHGlobalAnsi(enabledExtensions[i]);
            }

            DeviceCreateInfo deviceCreateInfo = new()
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = pExtendedFeatures,
                QueueCreateInfoCount = queueCreateInfoCount,
                PQueueCreateInfos = queueCreateInfos,
                PpEnabledExtensionNames = (byte**)ppEnabledExtensions,
                EnabledExtensionCount = (uint)enabledExtensions.Length,
                PEnabledFeatures = &features,
            };

            Result result = api.CreateDevice(physicalDevice.PhysicalDevice, in deviceCreateInfo, null, out Device device);

            if (result != Result.Success && useOpticalFlowQueue)
            {
                Logger.Warning?.Print(LogClass.Gpu,
                    $"NVOFA: device creation with the dedicated optical-flow queue failed ({result}); retrying without it. " +
                    "DLSS and Frame Generation stay on; hardware optical flow (NvOpticalFlow) is disabled.");

                opticalFlowQueueFamilyIndex = uint.MaxValue;

                // Dropping the optical-flow entry must not drop the frame generation entry sitting
                // behind it, so that one moves up into the freed slot.
                RemoveQueueCreateInfo(queueCreateInfos, ref queueCreateInfoCount, ofaQueueSlot);
                ofaQueueSlot = -1;

                deviceCreateInfo.QueueCreateInfoCount = queueCreateInfoCount;
                result = api.CreateDevice(physicalDevice.PhysicalDevice, in deviceCreateInfo, null, out device);
            }

            // A driver is free to refuse the extra compute-family queues. Frame generation is worth
            // less than a working device, so it is given up rather than failing the whole renderer.
            if (result != Result.Success && fsrFgQueueFamilyIndex != uint.MaxValue)
            {
                Logger.Warning?.Print(LogClass.Gpu,
                    $"FSR-FG: device creation with {fsrFgQueueCount} queue(s) from family {fsrFgQueueFamilyIndex} " +
                    $"failed ({result}); retrying without them. Frame generation will not arm.");

                fsrFgQueueFamilyIndex = uint.MaxValue;
                fsrFgQueueCount = 0;

                // The frame generation entry is always appended last.
                queueCreateInfoCount--;
                deviceCreateInfo.QueueCreateInfoCount = queueCreateInfoCount;
                result = api.CreateDevice(physicalDevice.PhysicalDevice, in deviceCreateInfo, null, out device);
            }

            for (int i = 0; i < enabledExtensions.Length; i++)
            {
                Marshal.FreeHGlobal(ppEnabledExtensions[i]);
            }

            result.ThrowOnError();

            return device;
        }

        private static unsafe void RemoveQueueCreateInfo(DeviceQueueCreateInfo* queueCreateInfos, ref uint count, int slot)
        {
            if (slot < 0 || slot >= count)
            {
                return;
            }

            for (int i = slot; i < count - 1; i++)
            {
                queueCreateInfos[i] = queueCreateInfos[i + 1];
            }

            count--;
        }
    }
}
