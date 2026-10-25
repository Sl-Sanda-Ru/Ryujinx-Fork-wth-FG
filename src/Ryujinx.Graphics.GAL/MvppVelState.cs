
namespace Ryujinx.Graphics.GAL
{
    public static class MvppVelState
    {
        public static ITexture Coverage;

        public static ITexture CoverageBackend =>
            Coverage is Multithreading.Resources.ThreadedTexture t ? t.Base : Coverage;

        public static int Width;
        public static int Height;

        public static bool FrameClearPending;

        public static bool CoverageFresh;

        public static IProgram VariantProgram;

        public static bool VariantBound;

        public static int VariantRestores;

        public static int VariantN1Binding = -1;

        public static int VariantN1Set;
    }
}
