
namespace Ryujinx.Graphics.Shader.Translation
{
    public static class MvppSurviveProbe
    {
        public static readonly bool Enabled =
            System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_SURVIVE_PROBE") == "1";

        public static readonly bool MipMarkEnabled =
            System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_MIPMARK") == "1";

        public static readonly bool BufferEnabled =
            Enabled || MipMarkEnabled ||
            System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_ALPHA_HISTO") == "1";

        public const int HostBinding = 80;

        public const int BackendMirrorBinding = 81;

        public const int SetIndex = 1;

        public const int MapStride = 4096;
        public const int MapHeight = 2048;

        public const int BufferSize = MapStride * MapHeight * sizeof(uint);
    }
}
