
namespace Ryujinx.Graphics.Shader.Translation
{
    public static class MvppJitterEmit
    {
        private static readonly bool _forceOff =
            System.Environment.GetEnvironmentVariable("RYUJINX_NOJITTER_EMIT") == "1";

        private static readonly bool _jitterOn =
            System.Environment.GetEnvironmentVariable("RYUJINX_DLSS_JITTER") is "1" or "true" or "on";

        public static bool Disabled => _forceOff || !_jitterOn;
    }
}
