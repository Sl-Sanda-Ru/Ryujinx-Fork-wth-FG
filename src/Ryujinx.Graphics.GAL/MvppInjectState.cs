
namespace Ryujinx.Graphics.GAL
{
    public static class MvppInjectState
    {
        public static readonly bool Enabled =
            System.Environment.GetEnvironmentVariable("RYUJINX_DLSS_UPSCALE_PASS") == "1";
    }
}
