using Ryujinx.Common.Configuration;
using System;

namespace Ryujinx.Graphics.GAL
{
    public interface IWindow
    {
        void Present(ITexture texture, ITexture depthTexture, ImageCrop crop, Action swapBuffersCallback);

        void SetSize(int width, int height);

        void ChangeVSyncMode(VSyncMode vSyncMode);

        void SetAntiAliasing(AntiAliasing antialiasing);
        void SetScalingFilter(ScalingFilter type);
        void SetScalingFilterLevel(float level);
        void SetColorSpacePassthrough(bool colorSpacePassThroughEnabled);
        void SetHdrMode(bool enabled, float paperWhite, float peak, float curve, float gamma, float blend, float whiten);
    }
}
