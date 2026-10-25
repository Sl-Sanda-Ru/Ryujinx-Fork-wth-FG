using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan.Effects;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Linq;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Ryujinx.Graphics.Vulkan
{
    class Window : WindowBase, IDisposable
    {
        private const int SurfaceWidth = 1280;
        private const int SurfaceHeight = 720;

        private readonly VulkanRenderer _gd;
        private readonly SurfaceKHR _surface;
        private readonly PhysicalDevice _physicalDevice;
        private readonly Device _device;
        private SwapchainKHR _swapchain;

        private Image[] _swapchainImages;
        private TextureView[] _swapchainImageViews;

        private Semaphore[] _imageAvailableSemaphores;
        private Semaphore[] _renderFinishedSemaphores;

        private int _frameIndex;

        private bool _swapchainViaFgProxy;
        private bool _swapchainViaFsrFg;

        private readonly PerformanceOverlayRenderer _overlay = new();
        private bool _fgPresentLogged;

        private static readonly bool _fgFeedEnabled =
            System.Environment.GetEnvironmentVariable("RYUJINX_DLSS_FGFEED") == "1";
        private const int FgFeedMaxConsecutive = 8;
        private long _fgFeedLastSeq;
        private int _fgFeedConsecutive;
        private int _fgFeedSwallowed;
        private int _fgFeedPresented;
        private long _fgFeedLogMs;
        private bool _fgFeedArmedLogged;

        private int _width;
        private int _height;
        private VSyncMode _vSyncMode;
        private bool _swapchainIsDirty;
        private VkFormat _format;
        private bool _isHdrPq;
        private bool _pqFilterBypassLogged;
        private AntiAliasing _currentAntiAliasing;
        private bool _updateEffect;
        private IPostProcessingEffect _effect;
        private IScalingFilter _scalingFilter;
        private Dlss.DlssUpscaler _dlss;
        private bool _dlssCropLogged;

        private readonly System.Collections.Generic.Dictionary<(int, int, GAL.Format), TextureView> _heldColorCache = new();
        private long _colorSnapLastSig = -1;
        private long _colorSnapDiagMs;
        private readonly bool _colorDumpEnabled = System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_COLORDUMP") == "1";
        private bool _colorDumped;

        private static readonly bool _presentCapArmed =
            System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_PRESENTCAP") == "1";
        private bool _presentCapUsable;
        private int _presentCapCount;
        private long _presentCapLastMs;
        private BufferHandle _presentCapBuffer;
        private int _presentCapBufferSize;
        private bool _presentCapPending;

        private static readonly int _presentCapBurst =
            int.TryParse(System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_PRESENTCAP_BURST"), out int pcb) && pcb > 0 ? pcb : 8;
        private static readonly int _presentCapGapMs =
            int.TryParse(System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_PRESENTCAP_GAP"), out int pcg) && pcg > 0 ? pcg : 3000;
        private static readonly int _presentCapMax =
            int.TryParse(System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_PRESENTCAP_MAX"), out int pcm) && pcm > 0 ? pcm : 48;
        private static readonly int _presentCapStartSec =
            int.TryParse(System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_PRESENTCAP_START"), out int pcs) && pcs > 0 ? pcs : 45;
        private long _presentCapArmMs;
        private bool _presentCapAnnounced;
        private int _presentCapBurstLeft;
        private BufferHandle _presentCapGuestBuffer;
        private int _presentCapGuestSize;
        private bool _presentCapGuestPending;
        private int _presentCapGuestW, _presentCapGuestH;
        private string _presentCapGuestFmt;
        private readonly bool _flashCap = System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_FLASHCAP") == "1";
        private byte[] _flashPrevSamples;
        private float _flashAvg, _flashMax;
        private int _flashDumpCount, _flashDumpNext;
        private long _flashLogMs, _flashLastTickMs;
        private readonly int _flashTimedSec =
            int.TryParse(System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_FLASHCAP_TIMED"), out int flashTimedSec) ? flashTimedSec : 0;
        private long _flashTimedNextMs;
        private readonly int _flashTimedBurst =
            int.TryParse(System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_FLASHCAP_BURST"), out int flashTimedBurst) && flashTimedBurst > 0 ? flashTimedBurst : 3;
        private readonly int _flashTimedCap =
            int.TryParse(System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_FLASHCAP_CAP"), out int flashTimedCap) && flashTimedCap > 0 ? flashTimedCap : 45;

        private static readonly bool _flashForce =
            System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_FLASHCAP_FORCE") == "1";

        private static readonly bool _syncFrame =
            System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_SYNCFRAME") == "1";
        private static bool _syncFrameLive = _syncFrame;
        private static bool _syncFrameAnnounced;
        private static long _syncFrameCount;
        private static long _syncFrameStallTicks;
        private static long _syncFrameNextLogMs;
        private bool _isLinear;
        private float _scalingFilterLevel;
        private (int, int, int, int, int, int, int, int, int, int, Type, Type)? _dlssSrPresentSig;
        private bool _updateScalingFilter;
        private ScalingFilter _currentScalingFilter;
        private bool _colorSpacePassthroughEnabled;
        private bool _hdrEnabled;
        private float _hdrPaperWhite = 200f;
        private float _hdrPeak = 1100f;
        private float _hdrCurve = 2.8f;
        private float _hdrGamma = 2.2f;
        private float _hdrBlend = 0.5f;
        private float _hdrWhiten = 0.4f;

        public unsafe Window(VulkanRenderer gd, SurfaceKHR surface, PhysicalDevice physicalDevice, Device device)
        {
            _gd = gd;
            _physicalDevice = physicalDevice;
            _device = device;
            _surface = surface;

            CreateSwapchain();
        }

        private KhrSwapchain ActiveSwapchainApi =>
            _swapchainViaFgProxy && _gd.FgHooks != null ? _gd.FgHooks.SwapchainProxy :
            _swapchainViaFsrFg && _gd.FsrFg != null ? _gd.FsrFg.SwapchainProxy : _gd.SwapchainApi;

        public void MarkSwapchainDirty()
        {
            _swapchainIsDirty = true;
        }

        public bool MvppRunInjectMidFrame(CommandBufferScoped cbs, GAL.ITexture src, GAL.ITexture dst, GAL.ITexture depth)
        {
            return _dlss?.RunInjectMidFrame(cbs, src, dst, depth) ?? false;
        }

        private readonly System.Collections.Generic.List<(SwapchainKHR Chain, KhrSwapchain Api, TextureView[] Views, int RetireAfter)> _retiredSwapchains = new();

        private void DrainRetiredSwapchains(bool all)
        {
            for (int i = _retiredSwapchains.Count - 1; i >= 0; i--)
            {
                if (all || _retiredSwapchains[i].RetireAfter <= _frameIndex)
                {
                    foreach (TextureView view in _retiredSwapchains[i].Views)
                    {
                        view.Dispose();
                    }

                    _retiredSwapchains[i].Api.DestroySwapchain(_device, _retiredSwapchains[i].Chain, Span<AllocationCallbacks>.Empty);
                    _retiredSwapchains.RemoveAt(i);
                }
            }
        }

        private void RecreateSwapchain()
        {
            SwapchainKHR oldSwapchain = _swapchain;
            _swapchainIsDirty = false;

            int diagOldW = _width, diagOldH = _height;
            Logger.Info?.Print(LogClass.Gpu,
                $"SWAPDIAG recreate BEGIN: old {_width}x{_height} dlss={Dlss.DlssIntegration.IsEnabled} fgProxy={_swapchainViaFgProxy} dlssCtx={_dlss != null} retire=deferred");

            TextureView[] oldViews = _swapchainImageViews;
            KhrSwapchain oldApi = ActiveSwapchainApi;
            bool oldWasProxy = _swapchainViaFgProxy;
            bool oldWasFsrFg = _swapchainViaFsrFg;

            if (_swapchainViaFgProxy && _gd.FgHooks != null)
            {
                Dlss.StreamlineFrameGen.OnSwapchainTeardown(Dlss.DlssUpscaler.ViewportId);

                _gd.FgHooks.DeviceWaitIdle();
            }
            else
            {
                if (oldWasFsrFg)
                {
                    _gd.FsrFg?.WaitForPresents();
                }

                _gd.Api.DeviceWaitIdle(_device);
            }

            unsafe
            {
                for (int i = 0; i < _imageAvailableSemaphores.Length; i++)
                {
                    _gd.Api.DestroySemaphore(_device, _imageAvailableSemaphores[i], null);
                }

                for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
                {
                    _gd.Api.DestroySemaphore(_device, _renderFinishedSemaphores[i], null);
                }
            }

            // A parked swapchain stays alive and keeps the surface occupied. FidelityFX creates its
            // replacement from scratch, so the old one must be released here, not retired.
            bool parkable = oldSwapchain.Handle != 0 && !oldWasProxy && !oldWasFsrFg
                && _gd.FgHooks == null && _gd.FsrFg == null;

            if (oldSwapchain.Handle != 0 && !parkable)
            {
                foreach (TextureView view in oldViews)
                {
                    view.Dispose();
                }

                // ffxReplaceSwapchainForFrameinterpolationVK replaces a *live* swapchain and
                // destroys it itself, so when FSR-FG is armed the old one must survive until
                // CreateSwapchain hands it over.
                if (!oldWasFsrFg && _gd.FsrFg == null)
                {
                    oldApi.DestroySwapchain(_device, oldSwapchain, Span<AllocationCallbacks>.Empty);
                }
            }

            CreateSwapchain();

            if (parkable)
            {
                _retiredSwapchains.Add((oldSwapchain, oldApi, oldViews, _frameIndex + 8));
            }

            Logger.Info?.Print(LogClass.Gpu,
                $"SWAPDIAG recreate END: {diagOldW}x{diagOldH} -> {_width}x{_height} fgProxy={_swapchainViaFgProxy}");
        }

        private unsafe void CreateSwapchain()
        {
            _gd.SurfaceApi.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out SurfaceCapabilitiesKHR capabilities);

            uint surfaceFormatsCount;

            _gd.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &surfaceFormatsCount, null);

            SurfaceFormatKHR[] surfaceFormats = new SurfaceFormatKHR[surfaceFormatsCount];

            fixed (SurfaceFormatKHR* pSurfaceFormats = surfaceFormats)
            {
                _gd.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &surfaceFormatsCount, pSurfaceFormats);
            }

            uint presentModesCount;

            _gd.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &presentModesCount, null);

            PresentModeKHR[] presentModes = new PresentModeKHR[presentModesCount];

            fixed (PresentModeKHR* pPresentModes = presentModes)
            {
                _gd.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &presentModesCount, pPresentModes);
            }

            uint imageCount = capabilities.MinImageCount + 1;
            if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
            {
                imageCount = capabilities.MaxImageCount;
            }

            SurfaceFormatKHR surfaceFormat = ChooseSwapSurfaceFormat(surfaceFormats, _colorSpacePassthroughEnabled, _hdrEnabled);

            Extent2D extent = ChooseSwapExtent(capabilities);

            _width = (int)extent.Width;
            _height = (int)extent.Height;
            _format = surfaceFormat.Format;
            _isHdrPq = surfaceFormat.ColorSpace == ColorSpaceKHR.ColorSpaceHdr10ST2084Ext;

            if (_isHdrPq)
            {
                Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                    $"HDR PQ: swapchain {surfaceFormat.Format} + HDR10/ST.2084 (FG-compatible HDR container).");
            }

            _presentCapUsable = _presentCapArmed && capabilities.SupportedUsageFlags.HasFlag(ImageUsageFlags.TransferSrcBit);
            if (_presentCapArmed && !_presentCapUsable)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                    "MVPP PRESENTCAP: TransferSrc non supporté par la surface, capture désarmée.");
            }

            SwapchainKHR oldSwapchain = _swapchain;

            SwapchainCreateInfoKHR swapchainCreateInfo = new()
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = _surface,
                MinImageCount = imageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = extent,
                ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.StorageBit |
                    (_presentCapArmed && capabilities.SupportedUsageFlags.HasFlag(ImageUsageFlags.TransferSrcBit)
                        ? ImageUsageFlags.TransferSrcBit
                        : 0),
                ImageSharingMode = SharingMode.Exclusive,
                ImageArrayLayers = 1,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = ChooseCompositeAlpha(capabilities.SupportedCompositeAlpha),
                PresentMode = ChooseSwapPresentMode(presentModes, _vSyncMode),
                Clipped = true,
            };

            TextureCreateInfo textureCreateInfo = new(
                _width,
                _height,
                1,
                1,
                1,
                1,
                1,
                1,
                FormatTable.GetFormat(surfaceFormat.Format),
                DepthStencilMode.Depth,
                Target.Texture2D,
                SwizzleComponent.Red,
                SwizzleComponent.Green,
                SwizzleComponent.Blue,
                SwizzleComponent.Alpha);

            bool viaFgProxy = _gd.FgHooks != null;
            bool viaFsrFg = !viaFgProxy && _gd.FsrFg != null;
            KhrSwapchain swapchainApi = viaFgProxy ? _gd.FgHooks.SwapchainProxy : _gd.SwapchainApi;

            // No sharing mode is set for FSR-FG across two queue families: FidelityFX forces
            // VK_SHARING_MODE_EXCLUSIVE on the real swapchain (getRealSwapchainCreateInfo) and does
            // the ownership transfers itself.

            if (!viaFgProxy && !viaFsrFg && !_swapchainViaFgProxy && !_swapchainViaFsrFg && oldSwapchain.Handle != 0)
            {
                swapchainCreateInfo.OldSwapchain = oldSwapchain;
            }

            if (viaFgProxy || viaFsrFg)
            {
                PresentModeKHR fgMode =
                    presentModes.Contains(PresentModeKHR.ImmediateKhr) ? PresentModeKHR.ImmediateKhr :
                    presentModes.Contains(PresentModeKHR.MailboxKhr) ? PresentModeKHR.MailboxKhr :
                    swapchainCreateInfo.PresentMode;

                if (fgMode != swapchainCreateInfo.PresentMode)
                {
                    Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                        $"{(viaFsrFg ? "FSR-FG" : "DLSS-FG")}: present mode {swapchainCreateInfo.PresentMode} -> {fgMode} (FG requires VSync OFF; VRR engagement under {fgMode} is driver-dependent).");
                    swapchainCreateInfo.PresentMode = fgMode;
                }
            }

            if (viaFsrFg)
            {
                // FidelityFX asserts a non-null swapchain: it replaces this one rather than
                // creating from scratch. The createInfo's oldSwapchain must match it.
                swapchainCreateInfo.OldSwapchain = oldSwapchain;
                _swapchain = oldSwapchain;

                if (_gd.FsrFg.ReplaceSwapchain(_device, swapchainCreateInfo, ref _swapchain))
                {
                    swapchainApi = _gd.FsrFg.SwapchainProxy;
                }
                else
                {
                    viaFsrFg = false;

                    // ffxReplaceSwapchainForFrameinterpolationVK destroys the swapchain it was given
                    // even when it fails, so there is no live old swapchain to retire here.
                    swapchainCreateInfo.OldSwapchain = default;
                    _swapchain = default;

                    swapchainApi.CreateSwapchain(_device, in swapchainCreateInfo, null, out _swapchain).ThrowOnError();
                }
            }
            else
            {
                swapchainApi.CreateSwapchain(_device, in swapchainCreateInfo, null, out _swapchain).ThrowOnError();
            }

            _swapchainViaFgProxy = viaFgProxy;
            _swapchainViaFsrFg = viaFsrFg;

            if (viaFgProxy)
            {
                Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                    $"DLSS-FG: swapchain created through the Streamline proxy ({extent.Width}x{extent.Height}, {imageCount} images, present mode {swapchainCreateInfo.PresentMode}).");

                if (swapchainCreateInfo.PresentMode != PresentModeKHR.ImmediateKhr &&
                    swapchainCreateInfo.PresentMode != PresentModeKHR.MailboxKhr)
                {
                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                        $"DLSS-FG: present mode {swapchainCreateInfo.PresentMode} implies VSync; DLSS-G requires VSync OFF (eVSyncOffRequired). Set VSync to Unbounded before enabling FG.");
                }
            }

            swapchainApi.GetSwapchainImages(_device, _swapchain, &imageCount, null);

            _swapchainImages = new Image[imageCount];

            fixed (Image* pSwapchainImages = _swapchainImages)
            {
                swapchainApi.GetSwapchainImages(_device, _swapchain, &imageCount, pSwapchainImages);
            }

            if (viaFgProxy)
            {
                BirthClearSwapchainImages();
            }

            if (viaFgProxy)
            {
                Dlss.StreamlineFrameGen.SwapchainImageCount = imageCount;
                Dlss.StreamlineFrameGen.SwapchainFormat = (uint)surfaceFormat.Format;
                Dlss.StreamlineFrameGen.SwapchainWidth = extent.Width;
                Dlss.StreamlineFrameGen.SwapchainHeight = extent.Height;
            }

            _swapchainImageViews = new TextureView[imageCount];

            for (int i = 0; i < _swapchainImageViews.Length; i++)
            {
                _swapchainImageViews[i] = CreateSwapchainImageView(_swapchainImages[i], surfaceFormat.Format, textureCreateInfo);
            }

            SemaphoreCreateInfo semaphoreCreateInfo = new()
            {
                SType = StructureType.SemaphoreCreateInfo,
            };

            _imageAvailableSemaphores = new Semaphore[imageCount];

            for (int i = 0; i < _imageAvailableSemaphores.Length; i++)
            {
                _gd.Api.CreateSemaphore(_device, in semaphoreCreateInfo, null, out _imageAvailableSemaphores[i]).ThrowOnError();
            }

            _renderFinishedSemaphores = new Semaphore[imageCount];

            for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
            {
                _gd.Api.CreateSemaphore(_device, in semaphoreCreateInfo, null, out _renderFinishedSemaphores[i]).ThrowOnError();
            }
        }

        private unsafe void BirthClearSwapchainImages()
        {
            CommandBufferScoped cbs;
            bool useTempCbs = !_gd.CommandBufferPool.OwnedByCurrentThread;

            if (useTempCbs)
            {
                cbs = _gd.BackgroundResources.Get().GetPool().Rent();
            }
            else
            {
                if (_gd.PipelineInternal != null)
                {
                    cbs = _gd.PipelineInternal.GetPreloadCommandBuffer();
                }
                else
                {
                    cbs = _gd.CommandBufferPool.Rent();
                    useTempCbs = true;
                }
            }

            ImageSubresourceRange range = new(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            ClearColorValue zeroColor = default;

            foreach (Image image in _swapchainImages)
            {
                ImageMemoryBarrier toClear = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = 0,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = image,
                    SubresourceRange = range,
                };

                _gd.Api.CmdPipelineBarrier(
                    cbs.CommandBuffer,
                    PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.TransferBit,
                    0, 0, null, 0, null, 1, in toClear);

                _gd.Api.CmdClearColorImage(cbs.CommandBuffer, image, ImageLayout.TransferDstOptimal, in zeroColor, 1, in range);

                ImageMemoryBarrier toPresent = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = 0,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.PresentSrcKhr,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = image,
                    SubresourceRange = range,
                };

                _gd.Api.CmdPipelineBarrier(
                    cbs.CommandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.BottomOfPipeBit,
                    0, 0, null, 0, null, 1, in toPresent);
            }

            if (useTempCbs)
            {
                cbs.Dispose();
            }
        }

        private unsafe TextureView CreateSwapchainImageView(Image swapchainImage, VkFormat format, TextureCreateInfo info)
        {
            ComponentMapping componentMapping = new(
                ComponentSwizzle.R,
                ComponentSwizzle.G,
                ComponentSwizzle.B,
                ComponentSwizzle.A);

            ImageAspectFlags aspectFlags = ImageAspectFlags.ColorBit;

            ImageSubresourceRange subresourceRange = new(aspectFlags, 0, 1, 0, 1);

            ImageViewCreateInfo imageCreateInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = swapchainImage,
                ViewType = ImageViewType.Type2D,
                Format = format,
                Components = componentMapping,
                SubresourceRange = subresourceRange,
            };

            _gd.Api.CreateImageView(_device, in imageCreateInfo, null, out ImageView imageView).ThrowOnError();

            return new TextureView(_gd, _device, new DisposableImageView(_gd.Api, _device, imageView), info, format);
        }

        private static bool _hdrPqRequested =
            Environment.GetEnvironmentVariable("RYUJINX_HDR_PQ") is "1" or "true" or "on";

        /// <summary>
        /// Re-reads the fields above that come from the settings profile, so a changed upscaler
        /// setting applies at the next game start without restarting the app.
        /// </summary>
        internal static void ReloadProfileSettings()
        {
            {
                _hdrPqRequested = Environment.GetEnvironmentVariable("RYUJINX_HDR_PQ") is "1" or "true" or "on";
            }
        }

        private static SurfaceFormatKHR ChooseSwapSurfaceFormat(SurfaceFormatKHR[] availableFormats, bool colorSpacePassthroughEnabled, bool hdrEnabled)
        {
            if (hdrEnabled)
            {
                if (_hdrPqRequested)
                {
                    foreach (SurfaceFormatKHR pqFormat in availableFormats)
                    {
                        if (pqFormat.Format == VkFormat.A2B10G10R10UnormPack32 &&
                            pqFormat.ColorSpace == ColorSpaceKHR.ColorSpaceHdr10ST2084Ext)
                        {
                            return pqFormat;
                        }
                    }

                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                        "HDR PQ requested but the surface does not expose RGB10A2 + HDR10/ST.2084; falling back to scRGB.");
                }

                foreach (SurfaceFormatKHR hdrFormat in availableFormats)
                {
                    if (hdrFormat.Format == VkFormat.R16G16B16A16Sfloat &&
                        hdrFormat.ColorSpace == ColorSpaceKHR.ColorSpaceExtendedSrgbLinearExt)
                    {
                        return hdrFormat;
                    }
                }
            }

            if (availableFormats.Length == 1 && availableFormats[0].Format == VkFormat.Undefined)
            {
                return new SurfaceFormatKHR(VkFormat.B8G8R8A8Unorm, ColorSpaceKHR.PaceSrgbNonlinearKhr);
            }

            SurfaceFormatKHR formatToReturn = availableFormats[0];
            if (colorSpacePassthroughEnabled)
            {
                foreach (SurfaceFormatKHR format in availableFormats)
                {
                    if (format.Format == VkFormat.B8G8R8A8Unorm && format.ColorSpace == ColorSpaceKHR.SpacePassThroughExt)
                    {
                        formatToReturn = format;
                        break;
                    }
                    else if (format.Format == VkFormat.B8G8R8A8Unorm && format.ColorSpace == ColorSpaceKHR.PaceSrgbNonlinearKhr)
                    {
                        formatToReturn = format;
                    }
                }
            }
            else
            {
                foreach (SurfaceFormatKHR format in availableFormats)
                {
                    if (format.Format == VkFormat.B8G8R8A8Unorm && format.ColorSpace == ColorSpaceKHR.PaceSrgbNonlinearKhr)
                    {
                        formatToReturn = format;
                        break;
                    }
                }
            }

            return formatToReturn;
        }

        private static CompositeAlphaFlagsKHR ChooseCompositeAlpha(CompositeAlphaFlagsKHR supportedFlags)
        {
            if (supportedFlags.HasFlag(CompositeAlphaFlagsKHR.OpaqueBitKhr))
            {
                return CompositeAlphaFlagsKHR.OpaqueBitKhr;
            }
            else if (supportedFlags.HasFlag(CompositeAlphaFlagsKHR.PreMultipliedBitKhr))
            {
                return CompositeAlphaFlagsKHR.PreMultipliedBitKhr;
            }
            else
            {
                return CompositeAlphaFlagsKHR.InheritBitKhr;
            }
        }

        private static PresentModeKHR ChooseSwapPresentMode(PresentModeKHR[] availablePresentModes, VSyncMode vSyncMode)
        {
            if (vSyncMode == VSyncMode.Vrr && availablePresentModes.Contains(PresentModeKHR.FifoRelaxedKhr))
            {
                return PresentModeKHR.FifoRelaxedKhr;
            }
            else if (vSyncMode == VSyncMode.Unbounded && availablePresentModes.Contains(PresentModeKHR.ImmediateKhr))
            {
                return PresentModeKHR.ImmediateKhr;
            }
            else if (availablePresentModes.Contains(PresentModeKHR.MailboxKhr))
            {
                return PresentModeKHR.MailboxKhr;
            }
            else
            {
                return PresentModeKHR.FifoKhr;
            }
        }

        public static Extent2D ChooseSwapExtent(SurfaceCapabilitiesKHR capabilities)
        {
            if (capabilities.CurrentExtent.Width != uint.MaxValue)
            {
                return capabilities.CurrentExtent;
            }

            uint width = Math.Max(capabilities.MinImageExtent.Width, Math.Min(capabilities.MaxImageExtent.Width, SurfaceWidth));
            uint height = Math.Max(capabilities.MinImageExtent.Height, Math.Min(capabilities.MaxImageExtent.Height, SurfaceHeight));

            return new Extent2D(width, height);
        }

        public unsafe override void Present(ITexture texture, ITexture depthTexture, ImageCrop crop, Action swapBuffersCallback)
        {
            if (_fgFeedEnabled)
            {
                bool fgArmed = _swapchainViaFgProxy && _gd.FgHooks != null;

                if (!_fgFeedArmedLogged)
                {
                    _fgFeedArmedLogged = true;
                    Logger.Info?.Print(LogClass.Gpu, "DLSS FG-nourrie: ARMEE (FGFEED=1).");

                    if (!fgArmed)
                    {
                        Logger.Warning?.Print(LogClass.Gpu,
                            "DLSS FG-nourrie: FG non armee (pas de proxy Streamline) -> avalage INERTE, presentation inchangee.");
                    }
                }

                long fgFeedNow = Environment.TickCount64;

                if (fgFeedNow - _fgFeedLogMs >= 5000)
                {
                    if (_fgFeedLogMs != 0)
                    {
                        Logger.Info?.Print(LogClass.Gpu,
                            $"DLSS FG-nourrie: {_fgFeedSwallowed} doublons avales / {_fgFeedPresented} presentes (fenetre ~5 s).");
                    }

                    _fgFeedLogMs = fgFeedNow;
                    _fgFeedSwallowed = 0;
                    _fgFeedPresented = 0;
                }

                long fgFeedSeq = System.Threading.Interlocked.Read(ref DlssCameraState.RenderedFrameSeq);

                if (fgArmed && fgFeedSeq != 0 &&
                    fgFeedSeq == _fgFeedLastSeq &&
                    _fgFeedConsecutive < FgFeedMaxConsecutive &&
                    !ScreenCaptureRequested)
                {
                    _fgFeedSwallowed++;
                    _fgFeedConsecutive++;

                    return;
                }

                _fgFeedLastSeq = fgFeedSeq;
                _fgFeedConsecutive = 0;
                _fgFeedPresented++;
            }

            MvppVkImgProbe.OnPresent();
            _gd.PipelineInternal.MvppDecompTick();

            _gd.PipelineInternal.AutoFlush.Present();

            DrainRetiredSwapchains(all: false);

            uint nextImage = 0;
            int semaphoreIndex = _frameIndex++ % _imageAvailableSemaphores.Length;

            while (true)
            {
                if (_swapchainIsDirty)
                {
                    RecreateSwapchain();
                    semaphoreIndex = (_frameIndex - 1) % _imageAvailableSemaphores.Length;
                }

                Result acquireResult = ActiveSwapchainApi.AcquireNextImage(
                    _device,
                    _swapchain,
                    ulong.MaxValue,
                    _imageAvailableSemaphores[semaphoreIndex],
                    new Fence(),
                    ref nextImage);

                if (acquireResult == Result.ErrorOutOfDateKhr)
                {
                    _swapchainIsDirty = true;
                    continue;
                }

                if (acquireResult == Result.SuboptimalKhr)
                {
                    _swapchainIsDirty = true;
                    break;
                }

                acquireResult.ThrowOnError();
                break;
            }

            Image swapchainImage = _swapchainImages[nextImage];

            try
            {
                _gd.FlushAllCommands();
            }
            catch (System.Exception swapdiagEx)
            {
                Logger.Error?.Print(LogClass.Gpu,
                    $"SWAPDIAG DEVICELOST at present flush #1: {_width}x{_height} dlss={Dlss.DlssIntegration.IsEnabled} fgProxy={_swapchainViaFgProxy} -- {swapdiagEx.Message}");
                throw;
            }

            CommandBufferScoped cbs = _gd.CommandBufferPool.Rent();

            Transition(
                cbs.CommandBuffer,
                swapchainImage,
                0,
                AccessFlags.TransferWriteBit,
                ImageLayout.Undefined,
                ImageLayout.General);

            TextureView view = (TextureView)texture;

            if (_swapchainViaFsrFg)
            {
                _gd.FsrFg?.FeedFrame(
                    _device,
                    cbs,
                    depthTexture as TextureView,
                    _dlss?.LastMotionVectors,
                    _width,
                    _height,
                    _format,
                    _format == VkFormat.R16G16B16A16Sfloat || _isHdrPq);
            }

            if (_flashCap && view.Width > 0)
            {
                FlashCapPresent(view);
            }

            if (GAL.MvppColorSnapshot.Enabled)
            {
                GAL.ITexture snapHost = GAL.MvppColorSnapshot.SceneColorBackend;
                if (System.Environment.TickCount64 - _colorSnapDiagMs >= 3000)
                {
                    _colorSnapDiagMs = System.Environment.TickCount64;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP COLORCAP BRIDGE: host={(snapHost == null ? "null" : snapHost.GetType().Name)} " +
                        $"isView={(snapHost is TextureView)} width={(snapHost?.Width ?? -1)}");
                }

                if (snapHost is TextureView srcColor && srcColor.Width > 0)
                {
                bool success = false;
                GAL.Format srcFmt = srcColor.Info.Format;
                int sW = srcColor.Width, sH = srcColor.Height, cW = 0, cH = 0;
                try
                {
                    var key = (sW, sH, srcFmt);
                    if (!_heldColorCache.TryGetValue(key, out TextureView held) || held == null)
                    {
                        held = _gd.CreateTexture(srcColor.Info) as TextureView;
                        _heldColorCache[key] = held;
                    }

                    if (held != null)
                    {
                        srcColor.CopyTo(held, 0, 0);
                        cW = held.Width;
                        cH = held.Height;
                        success = cW == sW && cH == sH;

                        if (success)
                        {
                            GAL.MvppColorSnapshot.SceneColorHeld = held;
                        }
                    }
                }
                catch (System.Exception e)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"MVPP COLORCAP SNAPSHOT: copy failed: {e.Message}");
                }

                long sig = ((long)sW << 20) | (uint)sH;
                if (sig != _colorSnapLastSig)
                {
                    _colorSnapLastSig = sig;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP COLORCAP SNAPSHOT: sourceFormat={srcFmt} sourceSize={sW}x{sH} " +
                        $"copiedSize={cW}x{cH} frame={GAL.MvppColorSnapshot.FrameId} success={success}");
                }

                if (_colorDumpEnabled && !_colorDumped && success && sW >= 2000 &&
                    (GAL.MvppColorSnapshot.DumpArmed || GAL.MvppColorSnapshot.FrameId > 5000))
                {
                    _colorDumped = true;
                    try
                    {
                        string dir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "mvpp-colordump");
                        System.IO.Directory.CreateDirectory(dir);
                        byte[] nat = srcColor.GetData(0, 0, srcColor.Width, srcColor.Height);
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir,
                            $"native_f{GAL.MvppColorSnapshot.FrameId}_{srcColor.Width}x{srcColor.Height}_{srcColor.Info.Format}.bin"), nat);
                        byte[] pres = view.GetData(0, 0, view.Width, view.Height);
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir,
                            $"presented_f{GAL.MvppColorSnapshot.FrameId}_{view.Width}x{view.Height}_{view.Info.Format}.bin"), pres);
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP COLORDUMP: native {srcColor.Width}x{srcColor.Height} {srcColor.Info.Format} ({nat.Length}B) + " +
                            $"presented {view.Width}x{view.Height} {view.Info.Format} ({pres.Length}B) -> mvpp-colordump/.");
                    }
                    catch (System.Exception e)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, $"MVPP COLORDUMP failed: {e.Message}");
                    }
                }
                }
            }

            UpdateEffect();

            if (_effect != null)
            {
                view = _effect.Run(view, cbs, _width, _height);
            }

            int srcX0, srcX1, srcY0, srcY1;

            if (crop.Left == 0 && crop.Right == 0)
            {
                srcX0 = 0;
                srcX1 = view.ActiveWidth;
            }
            else
            {
                srcX0 = crop.Left;
                srcX1 = crop.Right;
            }

            if (crop.Top == 0 && crop.Bottom == 0)
            {
                srcY0 = 0;
                srcY1 = view.ActiveHeight;
            }
            else
            {
                srcY0 = crop.Top;
                srcY1 = crop.Bottom;
            }

            (int, int, int, int, int, int, int, int, int, int, Type, Type) presentSig =
                (view.Width, view.Height, view.ActiveWidth, view.ActiveHeight,
                 srcX0, srcY0, srcX1, srcY1, _width, _height,
                 _scalingFilter?.GetType(), _effect?.GetType());

            if (_dlssSrPresentSig == null || !presentSig.Equals(_dlssSrPresentSig.Value))
            {
                bool first = _dlssSrPresentSig == null;
                _dlssSrPresentSig = presentSig;
                Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                    $"DLSS-SR probe{(first ? "" : " (CHANGED)")}: view {view.Width}x{view.Height} active {view.ActiveWidth}x{view.ActiveHeight} " +
                    $"src[{srcX0},{srcY0},{srcX1},{srcY1}] dst {_width}x{_height} " +
                    $"filter={_scalingFilter?.GetType().Name ?? "none"} effect={_effect?.GetType().Name ?? "none"}");
            }

            UnsafeBlitProbe.PresentTick();

            if (RenderSyncSwitch.ForceGpuIdlePerFrame)
            {
                RenderSyncSwitch.AnnounceIdleOnce();
                _gd.Api.DeviceWaitIdle(_device);
            }

            if (ScreenCaptureRequested)
            {
                if (_effect != null)
                {
                    _gd.FlushAllCommands();
                    _gd.CommandBufferPool.Return(
                        cbs,
                        null,
                        [PipelineStageFlags.ColorAttachmentOutputBit],
                        null);
                    cbs.GetFence().Wait();
                    cbs = _gd.CommandBufferPool.Rent();
                }

                CaptureFrame(view, srcX0, srcY0, srcX1 - srcX0, srcY1 - srcY0, view.Info.Format.IsBgr, crop.FlipX, crop.FlipY);

                ScreenCaptureRequested = false;
            }

            float ratioX = crop.IsStretched ? 1.0f : MathF.Min(1.0f, _height * crop.AspectRatioX / (_width * crop.AspectRatioY));
            float ratioY = crop.IsStretched ? 1.0f : MathF.Min(1.0f, _width * crop.AspectRatioY / (_height * crop.AspectRatioX));

            int dstWidth = (int)(_width * ratioX);
            int dstHeight = (int)(_height * ratioY);

            int dstPaddingX = (_width - dstWidth) / 2;
            int dstPaddingY = (_height - dstHeight) / 2;

            int dstX0 = crop.FlipX ? _width - dstPaddingX : dstPaddingX;
            int dstX1 = crop.FlipX ? dstPaddingX : _width - dstPaddingX;

            int dstY0 = crop.FlipY ? dstPaddingY : _height - dstPaddingY;
            int dstY1 = crop.FlipY ? _height - dstPaddingY : dstPaddingY;

            bool dlssHandled = false;

            if (Dlss.DlssIntegration.IsEnabled)
            {
                _dlss ??= new Dlss.DlssUpscaler(_gd, _device);

                if (!_dlssCropLogged)
                {
                    _dlssCropLogged = true;
                    Logger.Info?.Print(LogClass.Gpu, $"DLSS present crop: FlipY={crop.FlipY} FlipX={crop.FlipX}.");
                }

                dlssHandled = _dlss.TryRun(
                    view,
                    depthTexture as TextureView,
                    cbs,
                    _swapchainImageViews[nextImage],
                    _width,
                    _height,
                    new Extents2D(dstX0, dstY1, dstX1, dstY0),
                    _format == VkFormat.R16G16B16A16Sfloat || _isHdrPq,
                    _hdrPaperWhite / 80f,
                    _hdrPeak / 80f,
                    _hdrCurve,
                    _hdrGamma,
                    _hdrBlend,
                    _hdrWhiten,
                    _isHdrPq);

            }

            if (!dlssHandled)
            {
                RenderSpatial(_swapchainImageViews[nextImage]);
            }

            if (PerformanceOverlay.Enabled)
            {
                string scaler = dlssHandled
                    ? (FsrFg.Fsr3Upscaler.Requested ? "FSR 3.1" : "DLSS")
                    : _scalingFilter switch
                    {
                        Effects.FsrScalingFilter => "FSR 1.0",
                        Effects.NisScalingFilter => "NIS",
                        Effects.AreaScalingFilter => "AREA",
                        _ => string.Empty,
                    };

                PerformanceOverlay.ReportResolution(
                    Math.Abs(srcX1 - srcX0),
                    Math.Abs(srcY1 - srcY0),
                    dstWidth,
                    dstHeight,
                    scaler);
            }

            void RenderSpatial(TextureView spatialDst)
            {
                bool filterCanEncode = !_isHdrPq || _scalingFilter is Effects.NisScalingFilter or Effects.FsrScalingFilter;

                if (_isHdrPq && _scalingFilter != null && !filterCanEncode && !_pqFilterBypassLogged)
                {
                    _pqFilterBypassLogged = true;
                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                        "HDR PQ: this scaling filter has no PQ variant; using the plain HDR blit.");
                }

                if (filterCanEncode && _scalingFilter != null && _scalingFilter.IsResolutionSupported(view.Width, view.Height, _width, _height))
                {
                    _scalingFilter.Run(
                        view,
                        cbs,
                        spatialDst.GetImageViewForAttachment(),
                        _format,
                        _width,
                        _height,
                        new Extents2D(srcX0, srcY0, srcX1, srcY1),
                        new Extents2D(dstX0, dstY0, dstX1, dstY1),
                        _format == VkFormat.R16G16B16A16Sfloat || _isHdrPq,
                        _hdrPaperWhite / 80f,
                        _hdrPeak / 80f,
                        _hdrCurve,
                        _hdrGamma,
                        _hdrBlend,
                        _hdrWhiten,
                        _isHdrPq
                        );
                }
                else
                {
                    _gd.HelperShader.BlitColor(
                        _gd,
                        cbs,
                        view,
                        spatialDst,
                        new Extents2D(srcX0, srcY0, srcX1, srcY1),
                        new Extents2D(dstX0, dstY1, dstX1, dstY0),
                        _isLinear,
                        true,
                        _format == VkFormat.R16G16B16A16Sfloat || _isHdrPq,
                        _hdrPaperWhite / 80f,
                        _hdrPeak / 80f,
                        _hdrCurve,
                        _hdrGamma,
                        _hdrBlend,
                        _hdrWhiten,
                        _isHdrPq);
                }
            }

            if (_presentCapUsable && _presentCapCount < _presentCapMax)
            {
                long presNowMs = System.Environment.TickCount64;

                if (_presentCapArmMs == 0)
                {
                    _presentCapArmMs = presNowMs;
                }

                if (!_presentCapAnnounced)
                {
                    _presentCapAnnounced = true;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP PRESENTCAP: armed -- premiere rafale dans {_presentCapStartSec}s, " +
                        $"{_presentCapBurst} images CONSECUTIVES par rafale, pause {_presentCapGapMs}ms, {_presentCapMax} au total. " +
                        "Sois en jeu, l'artefact visible, avant la fin du compte a rebours.");
                }

                bool presReady = presNowMs - _presentCapArmMs >= _presentCapStartSec * 1000L;
                bool presInBurst = presReady && _presentCapBurstLeft > 0;

                if (presReady && !presInBurst && presNowMs - _presentCapLastMs >= _presentCapGapMs)
                {
                    _presentCapBurstLeft = _presentCapBurst;
                    presInBurst = true;
                }

                if (presInBurst)
                {
                    _presentCapBurstLeft--;
                    if (_presentCapBurstLeft == 0)
                    {
                        _presentCapLastMs = presNowMs;
                    }
                    try
                    {
                        int presBpp = _format == VkFormat.R16G16B16A16Sfloat ? 8 : 4;
                        int presSize = _width * _height * presBpp;
                        if (_presentCapBuffer == BufferHandle.Null)
                        {
                            _presentCapBuffer = _gd.BufferManager.CreateWithHandle(_gd, presSize);
                            _presentCapBufferSize = presSize;
                        }

                        if (presSize == _presentCapBufferSize)
                        {
                            Transition(cbs.CommandBuffer, swapchainImage,
                                AccessFlags.MemoryWriteBit, AccessFlags.TransferReadBit,
                                ImageLayout.General, ImageLayout.General);

                            var presBuf = _gd.BufferManager.GetBuffer(cbs.CommandBuffer, _presentCapBuffer, true).Get(cbs).Value;
                            ImageSubresourceLayers presSl = new(ImageAspectFlags.ColorBit, 0, 0, 1);
                            BufferImageCopy presRegion = new(0, 0, 0, presSl, new Offset3D(0, 0, 0),
                                new Extent3D((uint)_width, (uint)_height, 1));
                            _gd.Api.CmdCopyImageToBuffer(cbs.CommandBuffer, swapchainImage, ImageLayout.General, presBuf, 1, in presRegion);
                            _presentCapPending = true;

                            int guestBpp = view.Info.BytesPerPixel;
                            int guestSize = view.Width * view.Height * guestBpp;

                            if (_presentCapGuestBuffer == BufferHandle.Null)
                            {
                                _presentCapGuestBuffer = _gd.BufferManager.CreateWithHandle(_gd, guestSize);
                                _presentCapGuestSize = guestSize;
                            }

                            if (guestSize == _presentCapGuestSize)
                            {
                                Image guestImage = view.GetImage().Get(cbs).Value;

                                Transition(cbs.CommandBuffer, guestImage,
                                    AccessFlags.MemoryWriteBit, AccessFlags.TransferReadBit,
                                    ImageLayout.General, ImageLayout.General);

                                var guestBuf = _gd.BufferManager.GetBuffer(cbs.CommandBuffer, _presentCapGuestBuffer, true).Get(cbs).Value;
                                BufferImageCopy guestRegion = new(0, 0, 0, presSl, new Offset3D(0, 0, 0),
                                    new Extent3D((uint)view.Width, (uint)view.Height, 1));
                                _gd.Api.CmdCopyImageToBuffer(cbs.CommandBuffer, guestImage, ImageLayout.General, guestBuf, 1, in guestRegion);

                                _presentCapGuestPending = true;
                                _presentCapGuestW = view.Width;
                                _presentCapGuestH = view.Height;
                                _presentCapGuestFmt = view.Info.Format.ToString();
                            }
                        }
                    }
                    catch (System.Exception presEx)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, $"MVPP PRESENTCAP ÉCHEC (copie): {presEx.Message}");
                    }
                }
            }

            // Last thing drawn, so the scene, the upscaler and every probe above sit underneath it.
            _overlay.Draw(
                _gd,
                cbs,
                _swapchainImageViews[nextImage],
                _width,
                _height,
                _format == VkFormat.R16G16B16A16Sfloat || _isHdrPq,
                _hdrPaperWhite / 80f,
                _hdrPeak / 80f,
                _hdrCurve,
                _hdrGamma,
                _hdrBlend,
                _hdrWhiten,
                _isHdrPq);

            Transition(
                cbs.CommandBuffer,
                swapchainImage,
                0,
                0,
                ImageLayout.General,
                ImageLayout.PresentSrcKhr);

            try
            {
                _gd.FlushAllCommands();
            }
            catch (System.Exception swapdiagEx)
            {
                Logger.Error?.Print(LogClass.Gpu,
                    $"SWAPDIAG DEVICELOST at present flush #2: {_width}x{_height} dlss={Dlss.DlssIntegration.IsEnabled} fgProxy={_swapchainViaFgProxy} -- {swapdiagEx.Message}");
                throw;
            }

            if (_presentCapPending)
            {
                _presentCapPending = false;
                try
                {
                    _gd.Api.DeviceWaitIdle(_device);
                    using var presData = _gd.BufferManager.GetData(_presentCapBuffer, 0, _presentCapBufferSize);
                    string presDir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "mvpp-dump");
                    System.IO.Directory.CreateDirectory(presDir);
                    string presFile = System.IO.Path.Combine(presDir,
                        $"pres_{_presentCapCount:00}_{_width}x{_height}_{_format}.bin");
                    System.IO.File.WriteAllBytes(presFile, presData.Get().ToArray());

                    if (_presentCapGuestPending)
                    {
                        _presentCapGuestPending = false;
                        using var guestData = _gd.BufferManager.GetData(_presentCapGuestBuffer, 0, _presentCapGuestSize);
                        string guestFile = System.IO.Path.Combine(presDir,
                            $"guest_{_presentCapCount:00}_{_presentCapGuestW}x{_presentCapGuestH}_{_presentCapGuestFmt}.bin");
                        System.IO.File.WriteAllBytes(guestFile, guestData.Get().ToArray());
                        Logger.Info?.Print(LogClass.Gpu, $"MVPP PRESENTCAP paire: {guestFile}.");
                    }

                    _presentCapCount++;
                    Logger.Info?.Print(LogClass.Gpu, $"MVPP PRESENTCAP: {presFile}.");
                }
                catch (System.Exception presEx)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"MVPP PRESENTCAP ÉCHEC (lecture): {presEx.Message}");
                }
            }

            _gd.CommandBufferPool.Return(
                cbs,
                [_imageAvailableSemaphores[semaphoreIndex]],
                [PipelineStageFlags.ColorAttachmentOutputBit],
                [_renderFinishedSemaphores[semaphoreIndex]]);

            if (_syncFrameLive)
            {
                if (!_syncFrameAnnounced)
                {
                    _syncFrameAnnounced = true;
                    Logger.Info?.Print(LogClass.Gpu,
                        "MVPP SYNCFRAME: ON -- DeviceWaitIdle après chaque present (banc uniquement, perte de FPS attendue).");
                }

                long syncStart = System.Diagnostics.Stopwatch.GetTimestamp();

                try
                {
                    lock (_gd.QueueLock)
                    {
                        if (_swapchainViaFgProxy && _gd.FgHooks != null)
                        {
                            _gd.FgHooks.DeviceWaitIdle();
                        }
                        else
                        {
                            _gd.Api.DeviceWaitIdle(_device);
                        }
                    }

                    _syncFrameCount++;
                    _syncFrameStallTicks += System.Diagnostics.Stopwatch.GetTimestamp() - syncStart;

                    long syncNowMs = System.Environment.TickCount64;
                    if (syncNowMs >= _syncFrameNextLogMs)
                    {
                        _syncFrameNextLogMs = syncNowMs + 5000;
                        double syncAvgMs =
                            (_syncFrameStallTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency) / _syncFrameCount;
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP SYNCFRAME: {_syncFrameCount} images synchronisées, stall moyen {syncAvgMs:F2} ms, fgProxy={_swapchainViaFgProxy}.");
                    }
                }
                catch (System.Exception syncEx)
                {
                    _syncFrameLive = false;
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"MVPP SYNCFRAME: désarmé après erreur inattendue -- {syncEx.Message}");
                }
            }

            // TODO: Present queue.
            Semaphore semaphore = _renderFinishedSemaphores[semaphoreIndex];
            SwapchainKHR swapchain = _swapchain;

            Result result;

            PresentInfoKHR presentInfo = new()
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &semaphore,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &nextImage,
                PResults = &result,
            };

            if (_swapchainViaFgProxy)
            {
                Dlss.StreamlineFrameGen.BeforePresent();
            }

            if (_swapchainViaFsrFg && _gd.FsrFgQueueFamilyIndex != uint.MaxValue)
            {
                // Cross-family only, and safe without the lock: FidelityFX's queuePresent only
                // null-checks the queue it is handed and never submits to it. All of its game queue
                // work goes through the submit callback, which takes QueueLock itself. Holding the
                // lock here would instead hold it across queuePresent's closing wait on the
                // replacement buffer semaphore, which FidelityFX's own threads must signal.
                //
                // Keeping the lock for the single-family case leaves the configuration that works
                // untouched.
                ActiveSwapchainApi.QueuePresent(_gd.Queue, in presentInfo);
            }
            else
            {
                lock (_gd.QueueLock)
                {
                    ActiveSwapchainApi.QueuePresent(_gd.Queue, in presentInfo);
                }
            }

            if (result != Result.Success && result != Result.SuboptimalKhr)
            {
                Logger.Warning?.Print(LogClass.Gpu,
                    $"SWAPDIAG present result={result} at {_width}x{_height} fgProxy={_swapchainViaFgProxy}");
            }
            else
            {
                PerformanceOverlay.NotePresent();
            }

            if (_swapchainViaFgProxy)
            {
                Dlss.StreamlineFrameGen.AfterPresent(Dlss.DlssUpscaler.ViewportId);
            }

            if (_swapchainViaFgProxy && !_fgPresentLogged)
            {
                _fgPresentLogged = true;
                Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                    $"DLSS-FG: first present routed through the Streamline proxy (result {result}).");
            }
        }

        private void FlashCapPresent(TextureView view)
        {
            try
            {
                long now = System.Environment.TickCount64;
                if (_flashDumpNext == 0 && now - _flashLastTickMs < 28)
                {
                    return;
                }
                _flashLastTickMs = now;

                int w = view.Width, h = view.Height;
                byte[] packed = view.GetData(0, 0, w, h);
                if (packed == null || packed.Length < 64)
                {
                    return;
                }

                const int NSamp = 4096;
                int step = System.Math.Max(1, packed.Length / NSamp);
                int count = (packed.Length + step - 1) / step;
                if (count > NSamp)
                {
                    count = NSamp;
                }
                byte[] cur = new byte[count];
                for (int s = 0, i = 0; s < count; s++, i += step)
                {
                    cur[s] = packed[i];
                }

                bool warm = _flashForce || (now - GAL.MvppColorSnapshot.LastInjectMs) < 150;

                if (_flashPrevSamples != null && _flashPrevSamples.Length == count)
                {
                    long diff = 0;
                    for (int s = 0; s < count; s++)
                    {
                        diff += System.Math.Abs(cur[s] - _flashPrevSamples[s]);
                    }
                    float metric = (float)diff / count;
                    _flashAvg = _flashAvg <= 0f ? metric : 0.9f * _flashAvg + 0.1f * metric;
                    if (metric > _flashMax)
                    {
                        _flashMax = metric;
                    }

                    int dumpCap = _flashTimedSec > 0 ? _flashTimedCap : 15;

                    bool spike = _flashTimedSec == 0 && warm && metric > 8f && metric > 3f * _flashAvg;
                    if (spike && _flashDumpNext == 0 && _flashDumpCount < dumpCap)
                    {
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP-FLASHCAP: SPIKE metric={metric:0.0} baseline={_flashAvg:0.0} -> dumping presented flash{_flashDumpCount:D3}");
                        _flashDumpNext = 3;
                    }

                    if (_flashTimedSec > 0 && warm && _flashDumpNext == 0 && _flashDumpCount < dumpCap
                        && now >= _flashTimedNextMs)
                    {
                        _flashTimedNextMs = now + _flashTimedSec * 1000L;
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP-FLASHCAP: TIMED dump burst flash{_flashDumpCount:D3}");
                        _flashDumpNext = _flashTimedBurst;
                    }
                    if (_flashDumpNext > 0 && _flashDumpCount < dumpCap)
                    {
                        string dir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "mvpp-colordump");
                        System.IO.Directory.CreateDirectory(dir);
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir,
                            $"flash{_flashDumpCount:D3}_{w}x{h}_{view.Info.Format}.bin"), packed);
                        _flashDumpCount++;
                        _flashDumpNext--;
                    }
                    if (now - _flashLogMs >= 4000)
                    {
                        _flashLogMs = now;
                        Logger.Info?.Print(LogClass.Gpu,
                            $"MVPP-FLASHCAP (~4s): baseline={_flashAvg:0.0} maxDiff={_flashMax:0.0} dumps={_flashDumpCount} " +
                            $"armed={warm} injAgeMs={now - GAL.MvppColorSnapshot.LastInjectMs}");
                        _flashMax = 0f;
                    }
                }

                _flashPrevSamples = cur;
            }
            catch (System.Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP-FLASHCAP failed: {ex.Message}");
            }
        }

        public override void SetAntiAliasing(AntiAliasing effect)
        {
            if (_currentAntiAliasing == effect && _effect != null)
            {
                return;
            }

            _currentAntiAliasing = effect;

            _updateEffect = true;
        }

        public override void SetScalingFilter(ScalingFilter type)
        {
            if (_currentScalingFilter == type && _effect != null)
            {
                return;
            }

            _currentScalingFilter = type;

            _updateScalingFilter = true;
        }

        public override void SetColorSpacePassthrough(bool colorSpacePassthroughEnabled)
        {
            _colorSpacePassthroughEnabled = colorSpacePassthroughEnabled;
            _swapchainIsDirty = true;
        }

        private void UpdateEffect()
        {
            if (_updateEffect)
            {
                _updateEffect = false;

                AntiAliasing antiAliasing = Effects.TemporalFilter.IsEnabled ? AntiAliasing.Taa : _currentAntiAliasing;

                if (antiAliasing == AntiAliasing.Taa && Dlss.DlssIntegration.IsEnabled)
                {
                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                        "TAA is selected but DLSS is active: skipping the native TAA pass (DLSS already provides temporal AA).");

                    antiAliasing = AntiAliasing.None;
                }

                switch (antiAliasing)
                {
                    case AntiAliasing.Fxaa:
                        _effect?.Dispose();
                        _effect = new FxaaPostProcessingEffect(_gd, _device);
                        break;
                    case AntiAliasing.Taa:
                        if (_effect is not Effects.TemporalFilter)
                        {
                            _effect?.Dispose();
                            _effect = new Effects.TemporalFilter(_gd, _device);
                        }
                        break;
                    case AntiAliasing.None:
                        _effect?.Dispose();
                        _effect = null;
                        break;
                    case AntiAliasing.SmaaLow:
                    case AntiAliasing.SmaaMedium:
                    case AntiAliasing.SmaaHigh:
                    case AntiAliasing.SmaaUltra:
                        int quality = _currentAntiAliasing - AntiAliasing.SmaaLow;
                        if (_effect is SmaaPostProcessingEffect smaa)
                        {
                            smaa.Quality = quality;
                        }
                        else
                        {
                            _effect?.Dispose();
                            _effect = new SmaaPostProcessingEffect(_gd, _device, quality);
                        }

                        break;
                }
            }

            if (_updateScalingFilter)
            {
                _updateScalingFilter = false;

                switch (_currentScalingFilter)
                {
                    case ScalingFilter.Bilinear:
                    case ScalingFilter.Nearest:
                        _scalingFilter?.Dispose();
                        _scalingFilter = null;
                        _isLinear = _currentScalingFilter == ScalingFilter.Bilinear;
                        break;
                    case ScalingFilter.Fsr:
                        if (_scalingFilter is not FsrScalingFilter)
                        {
                            _scalingFilter?.Dispose();
                            _scalingFilter = new FsrScalingFilter(_gd, _device);
                        }

                        _scalingFilter.Level = _scalingFilterLevel;
                        break;
                    case ScalingFilter.Area:
                        if (_scalingFilter is not AreaScalingFilter)
                        {
                            _scalingFilter?.Dispose();
                            _scalingFilter = new AreaScalingFilter(_gd, _device);
                        }

                        break;
                    case ScalingFilter.Nis:
                        if (_scalingFilter is not NisScalingFilter)
                        {
                            _scalingFilter?.Dispose();
                            _scalingFilter = new NisScalingFilter(_gd, _device);
                        }

                        _scalingFilter.Level = _scalingFilterLevel;
                        break;
                }
            }
        }

        public override void SetScalingFilterLevel(float level)
        {
            _scalingFilterLevel = level;
            _updateScalingFilter = true;
        }

        private unsafe void Transition(
            CommandBuffer commandBuffer,
            Image image,
            AccessFlags srcAccess,
            AccessFlags dstAccess,
            ImageLayout srcLayout,
            ImageLayout dstLayout)
        {
            ImageSubresourceRange subresourceRange = new(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

            ImageMemoryBarrier barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = srcAccess,
                DstAccessMask = dstAccess,
                OldLayout = srcLayout,
                NewLayout = dstLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = subresourceRange,
            };

            _gd.Api.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.AllCommandsBit,
                0,
                0,
                null,
                0,
                null,
                1,
                in barrier);
        }

        private void CaptureFrame(TextureView texture, int x, int y, int width, int height, bool isBgra, bool flipX, bool flipY)
        {
            byte[] bitmap = texture.GetData(x, y, width, height);

            _gd.OnScreenCaptured(new ScreenCaptureImageInfo(width, height, isBgra, bitmap, flipX, flipY));
        }

        public override void SetSize(int width, int height)
        {
            // We don't need to use width and height as we can get the size from the surface.
            _swapchainIsDirty = true;
        }

        public override void ChangeVSyncMode(VSyncMode vSyncMode)
        {
            _vSyncMode = vSyncMode;
            //present mode may change, so mark the swapchain for recreation
            _swapchainIsDirty = true;
        }

        public override void SetHdrMode(bool enabled, float paperWhite, float peak, float curve, float gamma, float blend, float whiten)
        {
            _hdrPaperWhite = paperWhite;
            _hdrPeak = peak;
            _hdrCurve = curve;
            _hdrGamma = gamma;
            _hdrBlend = blend;
            _hdrWhiten = whiten;

            if (_hdrEnabled != enabled)
            {
                _hdrEnabled = enabled;
                _swapchainIsDirty = true;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                unsafe
                {
                    if (_swapchainViaFgProxy && _gd.FgHooks != null)
                    {
                        Dlss.StreamlineFrameGen.OnSwapchainTeardown(Dlss.DlssUpscaler.ViewportId);

                        _gd.FgHooks.DeviceWaitIdle();
                    }

                    DrainRetiredSwapchains(all: true);

                    for (int i = 0; i < _swapchainImageViews.Length; i++)
                    {
                        _swapchainImageViews[i].Dispose();
                    }

                    for (int i = 0; i < _imageAvailableSemaphores.Length; i++)
                    {
                        _gd.Api.DestroySemaphore(_device, _imageAvailableSemaphores[i], null);
                    }

                    for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
                    {
                        _gd.Api.DestroySemaphore(_device, _renderFinishedSemaphores[i], null);
                    }

                    if (_swapchainViaFsrFg)
                    {
                        // Released when the renderer disposes the FidelityFX context.
                        _gd.FsrFg?.WaitForPresents();
                    }
                    else
                    {
                        ActiveSwapchainApi.DestroySwapchain(_device, _swapchain, null);
                    }
                }

                _effect?.Dispose();
                _scalingFilter?.Dispose();
                _dlss?.Dispose();
                _overlay.Dispose();

                foreach (TextureView held in _heldColorCache.Values)
                {
                    held?.Dispose();
                }

                _heldColorCache.Clear();
            }
        }

        public override void Dispose()
        {
            Dispose(true);
        }
    }
}
