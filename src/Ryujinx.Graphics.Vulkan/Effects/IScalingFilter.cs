using Silk.NET.Vulkan;
using System;
using Extent2D = Ryujinx.Graphics.GAL.Extents2D;

namespace Ryujinx.Graphics.Vulkan.Effects
{
    internal interface IScalingFilter : IDisposable
    {
        float Level { get; set; }
        bool IsResolutionSupported(int srcWidth, int srcHeight, int dstWidth, int dstHeight);
        void Run(
            TextureView view,
            CommandBufferScoped cbs,
            Auto<DisposableImageView> destinationTexture,
            Format format,
            int width,
            int height,
            Extent2D source,
            Extent2D destination,
            bool hdr = false,
            float paperWhite = 2.5f,
            float peak = 12.5f,
            float curve = 4.0f,
            float gamma = 2.2f,
            float blend = 0.5f,
            float whiten = 0.0f,
            bool pqOutput = false);
    }
}
