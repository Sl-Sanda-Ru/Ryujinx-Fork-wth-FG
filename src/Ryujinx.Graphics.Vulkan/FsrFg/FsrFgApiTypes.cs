using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Vulkan.FsrFg
{
    /// <summary>
    /// FidelityFX API resource types and the frame generation descriptors.
    /// Transcribed from FidelityFX SDK v1.1.4 <c>ffx_api_types.h</c>, <c>ffx_framegeneration.h</c>
    /// and <c>vk/ffx_api_vk.h</c>.
    /// </summary>
    public static partial class FsrFgApi
    {
        /// <summary>Matches <c>enum FfxApiSurfaceFormat</c>; values are ordinal, so order matters.</summary>
        public enum SurfaceFormat : uint
        {
            Unknown,
            R32G32B32A32Typeless,
            R32G32B32A32Uint,
            R32G32B32A32Float,
            R16G16B16A16Float,
            R32G32B32Float,
            R32G32Float,
            R8Uint,
            R32Uint,
            R8G8B8A8Typeless,
            R8G8B8A8Unorm,
            R8G8B8A8Snorm,
            R8G8B8A8Srgb,
            B8G8R8A8Typeless,
            B8G8R8A8Unorm,
            B8G8R8A8Srgb,
            R11G11B10Float,
            R10G10B10A2Unorm,
            R16G16Float,
            R16G16Uint,
            R16G16Sint,
            R16Float,
            R16Uint,
            R16Unorm,
            R16Snorm,
            R8Unorm,
            R8G8Unorm,
            R8G8Uint,
            R32Float,
            R9G9B9E5SharedExp,
            R16G16B16A16Typeless,
            R32G32Typeless,
            R10G10B10A2Typeless,
            R16G16Typeless,
            R16Typeless,
            R8Typeless,
            R8G8Typeless,
            R32Typeless,
        }

        [Flags]
        public enum ResourceUsage : uint
        {
            ReadOnly = 0,
            RenderTarget = 1 << 0,
            Uav = 1 << 1,
            DepthTarget = 1 << 2,
            Indirect = 1 << 3,
            ArrayView = 1 << 4,
            StencilTarget = 1 << 5,
        }

        [Flags]
        public enum ResourceState : uint
        {
            Common = 1 << 0,
            UnorderedAccess = 1 << 1,
            ComputeRead = 1 << 2,
            PixelRead = 1 << 3,
            PixelComputeRead = PixelRead | ComputeRead,
            CopySrc = 1 << 4,
            CopyDest = 1 << 5,
            GenericRead = CopySrc | ComputeRead,
            IndirectArgument = 1 << 6,
            Present = 1 << 7,
            RenderTarget = 1 << 8,
        }

        public enum ResourceType : uint
        {
            Buffer,
            Texture1D,
            Texture2D,
            TextureCube,
            Texture3D,
        }

        public enum BackbufferTransferFunction : uint
        {
            Srgb,
            Pq,
            ScRgb,
        }

        /// <summary>Matches <c>enum FfxApiDispatchFramegenerationFlags</c>.</summary>
        [Flags]
        public enum DispatchFrameGenerationFlags : uint
        {
            None = 0,
            DrawDebugTearLines = 1 << 0,
            DrawDebugResetIndicators = 1 << 1,
            DrawDebugView = 1 << 2,
            NoSwapchainContextNotify = 1 << 3,
            DrawDebugPacingLines = 1 << 4,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FfxApiFloatCoords2D
        {
            public float X;
            public float Y;
        }

        // C 'bool' is one byte; these are kept as byte so the blittable layout matches the headers.
        [StructLayout(LayoutKind.Sequential)]
        public struct ConfigureDescFrameGeneration
        {
            public FfxApiHeader Header;
            public IntPtr SwapChain;
            public IntPtr PresentCallback;
            public IntPtr PresentCallbackUserContext;
            public IntPtr FrameGenerationCallback;
            public IntPtr FrameGenerationCallbackUserContext;
            public byte FrameGenerationEnabled;
            public byte AllowAsyncWorkloads;
            public FfxApiResource HudLessColor;
            public uint Flags;
            public byte OnlyPresentGenerated;
            public FfxApiRect2D GenerationRect;
            public ulong FrameId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DispatchDescFrameGenerationPrepare
        {
            public FfxApiHeader Header;
            public ulong FrameId;
            public uint Flags;
            public IntPtr CommandList;
            public FfxApiDimensions2D RenderSize;
            public FfxApiFloatCoords2D JitterOffset;
            public FfxApiFloatCoords2D MotionVectorScale;
            public float FrameTimeDelta;
            public byte UnusedReset;
            public float CameraNear;
            public float CameraFar;
            public float CameraFovAngleVertical;
            public float ViewSpaceToMetersFactor;
            public FfxApiResource Depth;
            public FfxApiResource MotionVectors;
        }

        /// <summary>Port of <c>ffxApiGetSurfaceFormatVK</c>. Typeless formats are not supported.</summary>
        public static SurfaceFormat GetSurfaceFormat(Format format)
        {
            return format switch
            {
                Format.R32G32B32A32Sfloat => SurfaceFormat.R32G32B32A32Float,
                Format.R32G32B32Sfloat => SurfaceFormat.R32G32B32Float,
                Format.R32G32B32A32Uint => SurfaceFormat.R32G32B32A32Uint,
                Format.R16G16B16A16Sfloat => SurfaceFormat.R16G16B16A16Float,
                Format.R32G32Sfloat => SurfaceFormat.R32G32Float,
                Format.R32Uint or Format.D24UnormS8Uint or Format.X8D24UnormPack32 => SurfaceFormat.R32Uint,
                Format.R8G8B8A8Unorm => SurfaceFormat.R8G8B8A8Unorm,
                Format.R8G8B8A8SNorm => SurfaceFormat.R8G8B8A8Snorm,
                Format.R8G8B8A8Srgb => SurfaceFormat.R8G8B8A8Srgb,
                Format.B8G8R8A8Unorm => SurfaceFormat.B8G8R8A8Unorm,
                Format.B8G8R8A8Srgb => SurfaceFormat.B8G8R8A8Srgb,
                Format.B10G11R11UfloatPack32 => SurfaceFormat.R11G11B10Float,
                Format.A2B10G10R10UnormPack32 => SurfaceFormat.R10G10B10A2Unorm,
                Format.R16G16Sfloat => SurfaceFormat.R16G16Float,
                Format.R16G16Uint => SurfaceFormat.R16G16Uint,
                Format.R16G16Sint => SurfaceFormat.R16G16Sint,
                Format.R16Sfloat => SurfaceFormat.R16Float,
                Format.R16Uint => SurfaceFormat.R16Uint,
                Format.R16Unorm or Format.D16Unorm or Format.D16UnormS8Uint => SurfaceFormat.R16Unorm,
                Format.R16SNorm => SurfaceFormat.R16Snorm,
                Format.R8Unorm => SurfaceFormat.R8Unorm,
                Format.R8Uint or Format.S8Uint => SurfaceFormat.R8Uint,
                Format.R8G8Unorm => SurfaceFormat.R8G8Unorm,
                Format.R8G8Uint => SurfaceFormat.R8G8Uint,
                Format.R32Sfloat or Format.D32Sfloat or Format.D32SfloatS8Uint => SurfaceFormat.R32Float,
                Format.E5B9G9R9UfloatPack32 => SurfaceFormat.R9G9B9E5SharedExp,
                _ => SurfaceFormat.Unknown,
            };
        }

        /// <summary>Port of <c>ffxApiIsDepthFormat</c>.</summary>
        public static bool IsDepthFormat(Format format)
        {
            return format is Format.D16Unorm
                or Format.X8D24UnormPack32
                or Format.D32Sfloat
                or Format.D16UnormS8Uint
                or Format.D24UnormS8Uint
                or Format.D32SfloatS8Uint;
        }

        /// <summary>Wraps a <c>VkImage</c> for the FidelityFX API.</summary>
        public static FfxApiResource MakeImageResource(
            IntPtr image,
            Format format,
            uint width,
            uint height,
            ResourceUsage usage,
            ResourceState state,
            uint mipCount = 1)
        {
            return new FfxApiResource
            {
                Resource = image,
                Description = new FfxApiResourceDescription
                {
                    Type = (uint)ResourceType.Texture2D,
                    Format = (uint)GetSurfaceFormat(format),
                    Width = width,
                    Height = height,
                    Depth = 1,
                    MipCount = mipCount,
                    Flags = 0,
                    Usage = (uint)usage,
                },
                State = (uint)state,
            };
        }

        /// <summary>An empty resource, for optional inputs such as the hudless colour buffer.</summary>
        public static FfxApiResource EmptyResource() => default;
    }
}
