
namespace Ryujinx.Graphics.GAL
{
    public static class MvppColorSnapshot
    {
        public static readonly bool Enabled =
            System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_COLORCAP_SNAP") == "1";

        public static ITexture SceneColorHost;
        public static long FrameId;

        public static long LastInjectMs;

        public static bool DumpArmed;

        public static ITexture SceneColorBackend =>
            SceneColorHost is Multithreading.Resources.ThreadedTexture t ? t.Base : SceneColorHost;

        public static ITexture SceneColorHeld;
    }
}
