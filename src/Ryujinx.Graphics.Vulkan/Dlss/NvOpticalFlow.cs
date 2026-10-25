
using Ryujinx.Common.Logging;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Vulkan.Dlss
{
    internal sealed unsafe class NvOpticalFlow
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate Result CreateSessionDelegate(Device device, OpticalFlowSessionCreateInfoNV* createInfo, AllocationCallbacks* allocator, OpticalFlowSessionNV* session);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DestroySessionDelegate(Device device, OpticalFlowSessionNV session, AllocationCallbacks* allocator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate Result BindImageDelegate(Device device, OpticalFlowSessionNV session, OpticalFlowSessionBindingPointNV bindingPoint, ImageView view, ImageLayout layout);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ExecuteDelegate(CommandBuffer commandBuffer, OpticalFlowSessionNV session, OpticalFlowExecuteInfoNV* executeInfo);

        private const uint UsageInput = 1;
        private const uint UsageOutput = 2;

        private uint _outputGrid = 4;

        private const uint BindInput = 1;
        private const uint BindReference = 2;
        private const uint BindFlowVector = 4;

        private CreateSessionDelegate _createSession;
        private DestroySessionDelegate _destroySession;
        private BindImageDelegate _bindImage;
        private ExecuteDelegate _execute;

        private Vk _api;
        private Device _device;
        private PhysicalDevice _physicalDevice;

        private Queue _ofaQueue;
        private uint _ofaFamily = uint.MaxValue;
        private uint _graphicsFamily;
        private CommandPool _cmdPool;
        private CommandBuffer _cmdBuffer;
        private Fence _fence;
        private bool _queueReady;

        private OpticalFlowSessionNV _session;

        private Image _imgInput, _imgRef, _imgFlow;
        private DeviceMemory _memInput, _memRef, _memFlow;
        private ImageView _viewInput, _viewRef, _viewFlow;

        private bool _bound;
        private int _execLogCount;

        private uint _flowW, _flowH;
        private Silk.NET.Vulkan.Buffer _readbackBuffer;
        private DeviceMemory _readbackMemory;
        private void* _readbackPtr;
        private int _measureLogCount;

        public bool Available { get; private set; }

        public bool SessionReady { get; private set; }

        public void TryBind(Vk api, Device device, PhysicalDevice physicalDevice, Queue opticalFlowQueue, uint opticalFlowQueueFamily, uint graphicsQueueFamily)
        {
            _api = api;
            _device = device;
            _physicalDevice = physicalDevice;
            _ofaQueue = opticalFlowQueue;
            _ofaFamily = opticalFlowQueueFamily;
            _graphicsFamily = graphicsQueueFamily;

            _createSession = Load<CreateSessionDelegate>(api, device, "vkCreateOpticalFlowSessionNV");
            _bindImage = Load<BindImageDelegate>(api, device, "vkBindOpticalFlowSessionImageNV");
            _execute = Load<ExecuteDelegate>(api, device, "vkCmdOpticalFlowExecuteNV");
            _destroySession = Load<DestroySessionDelegate>(api, device, "vkDestroyOpticalFlowSessionNV");

            Available = _createSession != null && _bindImage != null && _execute != null && _destroySession != null;

            Logger.Info?.Print(LogClass.Gpu,
                $"NVOFA probe: vkCreateOpticalFlowSessionNV={(_createSession != null ? "solved" : "NULL")}, " +
                $"vkBindOpticalFlowSessionImageNV={(_bindImage != null ? "solved" : "NULL")}, " +
                $"vkCmdOpticalFlowExecuteNV={(_execute != null ? "solved" : "NULL")}, " +
                $"vkDestroyOpticalFlowSessionNV={(_destroySession != null ? "solved" : "NULL")} -> " +
                (Available
                    ? "AVAILABLE (hardware optical flow ready; session setup may proceed)."
                    : "UNAVAILABLE (falling back to Lucas-Kanade motion estimation)."));

            if (Available)
            {
                QueryHardware();
                SetupOfaQueue(device);
            }
        }

        private void SetupOfaQueue(Device device)
        {
            if (_ofaFamily == uint.MaxValue)
            {
                Logger.Warning?.Print(LogClass.Gpu, "NVOFA queue setup: no dedicated optical-flow queue was created; OFA execute disabled.");

                return;
            }

            CommandPoolCreateInfo pci = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = _ofaFamily,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            };

            if (_api.CreateCommandPool(device, &pci, null, out _cmdPool) != Result.Success)
            {
                Logger.Warning?.Print(LogClass.Gpu, "NVOFA queue setup: command pool creation failed.");

                return;
            }

            CommandBufferAllocateInfo cbi = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _cmdPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };

            CommandBuffer cb;
            if (_api.AllocateCommandBuffers(device, &cbi, &cb) != Result.Success)
            {
                Logger.Warning?.Print(LogClass.Gpu, "NVOFA queue setup: command buffer allocation failed.");

                return;
            }

            _cmdBuffer = cb;

            FenceCreateInfo fci = new() { SType = StructureType.FenceCreateInfo };
            if (_api.CreateFence(device, &fci, null, out _fence) != Result.Success)
            {
                Logger.Warning?.Print(LogClass.Gpu, "NVOFA queue setup: fence creation failed.");

                return;
            }

            _queueReady = true;
            Logger.Info?.Print(LogClass.Gpu, $"NVOFA queue setup: command pool + buffer + fence ready on optical-flow family {_ofaFamily}.");
        }

        private void QueryHardware()
        {
            PhysicalDeviceOpticalFlowPropertiesNV ofaProps = new() { SType = StructureType.PhysicalDeviceOpticalFlowPropertiesNV };
            PhysicalDeviceProperties2 props2 = new() { SType = StructureType.PhysicalDeviceProperties2, PNext = &ofaProps };
            _api.GetPhysicalDeviceProperties2(_physicalDevice, &props2);

            uint grids = (uint)ofaProps.SupportedOutputGridSizes;

            if ((grids & 8) != 0) _outputGrid = 8;
            else if ((grids & 4) != 0) _outputGrid = 4;
            else if ((grids & 2) != 0) _outputGrid = 2;
            else if ((grids & 1) != 0) _outputGrid = 1;

            Logger.Info?.Print(LogClass.Gpu,
                $"NVOFA HW Query: supportedOutputGridSizes bitmask=0x{grids:X} " +
                $"(1x1={(grids & 1) != 0}, 2x2={(grids & 2) != 0}, 4x4={(grids & 4) != 0}, 8x8={(grids & 8) != 0}), " +
                $"min={ofaProps.MinWidth}x{ofaProps.MinHeight}, max={ofaProps.MaxWidth}x{ofaProps.MaxHeight} -> chosen grid {_outputGrid}x{_outputGrid}.");

            uint count = 0;
            _api.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref count, null);

            if (count != 0)
            {
                QueueFamilyProperties[] fams = new QueueFamilyProperties[count];
                fixed (QueueFamilyProperties* p = fams)
                {
                    _api.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref count, p);
                }

                const uint OfaQueueBit = 0x100;
                string s = "";
                for (int i = 0; i < count; i++)
                {
                    uint flags = (uint)fams[i].QueueFlags;
                    s += $"[fam{i}: flags=0x{flags:X} gfx={(flags & (uint)QueueFlags.GraphicsBit) != 0} compute={(flags & (uint)QueueFlags.ComputeBit) != 0} OFA={(flags & OfaQueueBit) != 0}] ";
                }

                Logger.Info?.Print(LogClass.Gpu, $"NVOFA HW Query: queue families = {s}(vkCmdOpticalFlowExecuteNV is only legal on a family with OFA=true).");
            }
        }

        public bool CreateSession(Device device, CommandBuffer cmd, uint width, uint height)
        {
            if (!Available)
            {
                return false;
            }

            DestroySession(device);

            OpticalFlowSessionCreateInfoNV info = new()
            {
                SType = StructureType.OpticalFlowSessionCreateInfoNV,
                Width = width,
                Height = height,
                ImageFormat = Format.R8Unorm,
                FlowVectorFormat = Format.R16G16Sfloat,
                CostFormat = Format.Undefined,
                OutputGridSize = (OpticalFlowGridSizeFlagsNV)_outputGrid,
                HintGridSize = (OpticalFlowGridSizeFlagsNV)0,
                PerformanceLevel = (OpticalFlowPerformanceLevelNV)2,
            };

            OpticalFlowSessionNV session;
            Result r = _createSession(device, &info, null, &session);

            if (r != Result.Success || session.Handle == 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"NVOFA Session: vkCreateOpticalFlowSessionNV failed (VkResult={r}); keeping Lucas-Kanade.");
                SessionReady = false;

                return false;
            }

            _session = session;

            uint flowW = (width + _outputGrid - 1) / _outputGrid;
            uint flowH = (height + _outputGrid - 1) / _outputGrid;

            if (!CreateImage(device, width, height, Format.R8Unorm, UsageInput, out _imgInput, out _memInput, out _viewInput) ||
                !CreateImage(device, width, height, Format.R8Unorm, UsageInput, out _imgRef, out _memRef, out _viewRef) ||
                !CreateImage(device, flowW, flowH, Format.R16G16Sfloat, UsageOutput, out _imgFlow, out _memFlow, out _viewFlow))
            {
                Logger.Warning?.Print(LogClass.Gpu, "NVOFA Session: optical flow image creation failed; keeping Lucas-Kanade.");
                DestroySession(device);

                return false;
            }

            TransitionToGeneral(cmd, _imgInput);
            TransitionToGeneral(cmd, _imgRef);
            TransitionToGeneral(cmd, _imgFlow);
            ClearImage(cmd, _imgInput);
            ClearImage(cmd, _imgRef);
            ClearImage(cmd, _imgFlow);

            Result rIn = _bindImage(device, _session, (OpticalFlowSessionBindingPointNV)BindInput, _viewInput, ImageLayout.General);
            Result rRef = _bindImage(device, _session, (OpticalFlowSessionBindingPointNV)BindReference, _viewRef, ImageLayout.General);
            Result rFlow = _bindImage(device, _session, (OpticalFlowSessionBindingPointNV)BindFlowVector, _viewFlow, ImageLayout.General);

            _bound = rIn == Result.Success && rRef == Result.Success && rFlow == Result.Success;

            if (!_bound)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"NVOFA Session: image bind failed (input={rIn}, reference={rRef}, flowVector={rFlow}); keeping Lucas-Kanade.");
                DestroySession(device);

                return false;
            }

            _flowW = flowW;
            _flowH = flowH;
            CreateReadbackBuffer(device, flowW, flowH);

            SessionReady = true;
            Logger.Info?.Print(LogClass.Gpu,
                $"NVOFA Session: vkCreateOpticalFlowSessionNV initialized successfully ({width}x{height} input, {flowW}x{flowH} flow @ {_outputGrid}x{_outputGrid} grid, R8_UNORM -> R16G16_SFLOAT, pNext images bound).");

            return true;
        }

        public void DestroySession(Device device)
        {
            DestroyImage(device, ref _imgInput, ref _memInput, ref _viewInput);
            DestroyImage(device, ref _imgRef, ref _memRef, ref _viewRef);
            DestroyImage(device, ref _imgFlow, ref _memFlow, ref _viewFlow);

            if (_readbackBuffer.Handle != 0)
            {
                if (_readbackPtr != null)
                {
                    _api.UnmapMemory(device, _readbackMemory);
                    _readbackPtr = null;
                }

                _api.DestroyBuffer(device, _readbackBuffer, null);
                _readbackBuffer = default;
            }

            if (_readbackMemory.Handle != 0)
            {
                _api.FreeMemory(device, _readbackMemory, null);
                _readbackMemory = default;
            }

            if (_session.Handle != 0 && _destroySession != null)
            {
                _destroySession(device, _session, null);
                _session = default;
            }

            SessionReady = false;
            _bound = false;
        }

        public void Execute(CommandBuffer cmd)
        {
            if (!Available || !SessionReady || !_bound)
            {
                return;
            }

            Barrier(cmd);

            OpticalFlowExecuteInfoNV info = new()
            {
                SType = StructureType.OpticalFlowExecuteInfoNV,
            };

            _execute(cmd, _session, &info);

            Barrier(cmd);

            if (_execLogCount < 3)
            {
                _execLogCount++;
                Logger.Info?.Print(LogClass.Gpu, "NVOFA Execute: Dummy hardware motion vectors generated (vkCmdOpticalFlowExecuteNV recorded, no error).");
            }
        }

        public void ExecuteOnOfaQueue()
        {
            if (!Available || !SessionReady || !_bound || !_queueReady)
            {
                return;
            }

            _api.ResetCommandBuffer(_cmdBuffer, 0);

            CommandBufferBeginInfo begin = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };

            if (_api.BeginCommandBuffer(_cmdBuffer, &begin) != Result.Success)
            {
                return;
            }

            Execute(_cmdBuffer);

            if (_readbackBuffer.Handle != 0)
            {
                BufferImageCopy copy = new()
                {
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    ImageExtent = new Extent3D(_flowW, _flowH, 1),
                };

                _api.CmdCopyImageToBuffer(_cmdBuffer, _imgFlow, ImageLayout.General, _readbackBuffer, 1, &copy);
            }

            if (_api.EndCommandBuffer(_cmdBuffer) != Result.Success)
            {
                return;
            }

            CommandBuffer cb = _cmdBuffer;
            SubmitInfo submit = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cb,
            };

            _api.ResetFences(_device, 1, in _fence);
            _api.QueueSubmit(_ofaQueue, 1, &submit, _fence);
            _api.WaitForFences(_device, 1, in _fence, true, ulong.MaxValue);

            MeasureFlow();
        }

        public void UploadFrames(CommandBuffer graphicsCmd, Image current, Image previous, uint width, uint height)
        {
            if (!Available || !SessionReady)
            {
                return;
            }

            BlitToR8(graphicsCmd, current, _imgInput, width, height);
            BlitToR8(graphicsCmd, previous, _imgRef, width, height);
        }

        private void BlitToR8(CommandBuffer cmd, Image src, Image dst, uint width, uint height)
        {
            ImageBlit region = new()
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            };

            region.SrcOffsets.Element0 = new Offset3D(0, 0, 0);
            region.SrcOffsets.Element1 = new Offset3D((int)width, (int)height, 1);
            region.DstOffsets.Element0 = new Offset3D(0, 0, 0);
            region.DstOffsets.Element1 = new Offset3D((int)width, (int)height, 1);

            _api.CmdBlitImage(cmd, src, ImageLayout.General, dst, ImageLayout.General, 1, &region, Filter.Nearest);
        }

        private void MeasureFlow()
        {
            if (_readbackPtr == null)
            {
                return;
            }

            short* data = (short*)_readbackPtr;
            int count = (int)(_flowW * _flowH);
            double sumSq = 0.0;
            double maxSq = 0.0;
            int outliers = 0;

            for (int i = 0; i < count; i++)
            {
                float x = data[(i * 2) + 0] / 32.0f;
                float y = data[(i * 2) + 1] / 32.0f;
                float magSq = (x * x) + (y * y);
                sumSq += magSq;

                if (magSq > maxSq)
                {
                    maxSq = magSq;
                }

                if (magSq > 4.0f)
                {
                    outliers++;
                }
            }

            if ((_measureLogCount++ % 120) == 0 && count > 0)
            {
                float rms = (float)Math.Sqrt(sumSq / count);
                float max = (float)Math.Sqrt(maxSq);
                Logger.Info?.Print(LogClass.Gpu,
                    $"NVOFA Flow Measure: HW vectors (S10.5) RMS={rms:0.000}px outliers={100f * outliers / count:0.0}% max={max:0.0}px ({_flowW}x{_flowH} @ {_outputGrid}x grid) -- compare vs the 'DLSS motion-field' Lucas-Kanade line.");
            }
        }

        private void CreateReadbackBuffer(Device device, uint flowW, uint flowH)
        {
            ulong size = (ulong)flowW * flowH * 4;

            BufferCreateInfo bci = new()
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = BufferUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
            };

            if (_api.CreateBuffer(device, &bci, null, out _readbackBuffer) != Result.Success)
            {
                _readbackBuffer = default;

                return;
            }

            _api.GetBufferMemoryRequirements(device, _readbackBuffer, out MemoryRequirements req);

            uint memType = FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            if (memType == uint.MaxValue)
            {
                _api.DestroyBuffer(device, _readbackBuffer, null);
                _readbackBuffer = default;

                return;
            }

            MemoryAllocateInfo ai = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = req.Size,
                MemoryTypeIndex = memType,
            };

            if (_api.AllocateMemory(device, &ai, null, out _readbackMemory) != Result.Success)
            {
                _api.DestroyBuffer(device, _readbackBuffer, null);
                _readbackBuffer = default;

                return;
            }

            _api.BindBufferMemory(device, _readbackBuffer, _readbackMemory, 0);

            void* ptr = null;
            _api.MapMemory(device, _readbackMemory, 0, size, 0, ref ptr);
            _readbackPtr = ptr;
        }

        public void Dispose(Device device)
        {
            DestroySession(device);

            if (_fence.Handle != 0)
            {
                _api.DestroyFence(device, _fence, null);
                _fence = default;
            }

            if (_cmdPool.Handle != 0)
            {
                _api.DestroyCommandPool(device, _cmdPool, null);
                _cmdPool = default;
            }

            _queueReady = false;
        }

        private bool CreateImage(Device device, uint width, uint height, Format format, uint ofaUsage, out Image image, out DeviceMemory memory, out ImageView view)
        {
            image = default;
            memory = default;
            view = default;

            OpticalFlowImageFormatInfoNV ofaInfo = new()
            {
                SType = StructureType.OpticalFlowImageFormatInfoNV,
                Usage = (OpticalFlowUsageFlagsNV)ofaUsage,
            };

            uint* families = stackalloc uint[2];
            families[0] = _graphicsFamily;
            families[1] = _ofaFamily;
            bool concurrent = _queueReady && _graphicsFamily != _ofaFamily;

            ImageCreateInfo ci = new()
            {
                SType = StructureType.ImageCreateInfo,
                PNext = &ofaInfo,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
                SharingMode = concurrent ? SharingMode.Concurrent : SharingMode.Exclusive,
                QueueFamilyIndexCount = concurrent ? 2u : 0u,
                PQueueFamilyIndices = concurrent ? families : null,
                InitialLayout = ImageLayout.Undefined,
            };

            Image img;
            if (_api.CreateImage(device, &ci, null, &img) != Result.Success)
            {
                return false;
            }

            _api.GetImageMemoryRequirements(device, img, out MemoryRequirements req);

            uint memType = FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
            if (memType == uint.MaxValue)
            {
                _api.DestroyImage(device, img, null);

                return false;
            }

            MemoryAllocateInfo ai = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = req.Size,
                MemoryTypeIndex = memType,
            };

            DeviceMemory mem;
            if (_api.AllocateMemory(device, &ai, null, &mem) != Result.Success)
            {
                _api.DestroyImage(device, img, null);

                return false;
            }

            _api.BindImageMemory(device, img, mem, 0);

            ImageViewCreateInfo vi = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = img,
                ViewType = ImageViewType.Type2D,
                Format = format,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };

            ImageView v;
            if (_api.CreateImageView(device, &vi, null, &v) != Result.Success)
            {
                _api.FreeMemory(device, mem, null);
                _api.DestroyImage(device, img, null);

                return false;
            }

            image = img;
            memory = mem;
            view = v;

            return true;
        }

        private uint FindMemoryType(uint typeBits, MemoryPropertyFlags props)
        {
            _api.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties memProps);

            for (uint i = 0; i < memProps.MemoryTypeCount; i++)
            {
                if ((typeBits & (1u << (int)i)) != 0 && (memProps.MemoryTypes[(int)i].PropertyFlags & props) == props)
                {
                    return i;
                }
            }

            return uint.MaxValue;
        }

        private void TransitionToGeneral(CommandBuffer cmd, Image image)
        {
            ImageMemoryBarrier b = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.General,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };

            _api.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.AllCommandsBit, 0, 0, null, 0, null, 1, &b);
        }

        private void ClearImage(CommandBuffer cmd, Image image)
        {
            ClearColorValue zero = default;
            ImageSubresourceRange range = new(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

            _api.CmdClearColorImage(cmd, image, ImageLayout.General, &zero, 1, &range);
        }

        private void DestroyImage(Device device, ref Image image, ref DeviceMemory memory, ref ImageView view)
        {
            if (view.Handle != 0)
            {
                _api.DestroyImageView(device, view, null);
                view = default;
            }

            if (image.Handle != 0)
            {
                _api.DestroyImage(device, image, null);
                image = default;
            }

            if (memory.Handle != 0)
            {
                _api.FreeMemory(device, memory, null);
                memory = default;
            }
        }

        private void Barrier(CommandBuffer cmd)
        {
            MemoryBarrier mb = new()
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.MemoryWriteBit,
                DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            };

            _api.CmdPipelineBarrier(cmd, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0, 1, &mb, 0, null, 0, null);
        }

        private static T Load<T>(Vk api, Device device, string name) where T : Delegate
        {
            PfnVoidFunction pfn = api.GetDeviceProcAddr(device, name);

            if (pfn.Handle == null)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"NVOFA: {name} could not be resolved (vkGetDeviceProcAddr returned null).");

                return null;
            }

            return Marshal.GetDelegateForFunctionPointer<T>((IntPtr)pfn.Handle);
        }
    }
}
