namespace Ryujinx.Graphics.GAL
{
    public static class DlssJitterState
    {
        public static bool Enabled;

        public static bool Inject = true;

        public static bool JitterUi;

        public static bool NoShadowJitter;

        public static bool CamPassOnly;

        public static int InputWidth;
        public static int InputHeight;

        public static float CamVpW;
        public static float CamVpH;

        public static float OffsetX;
        public static float OffsetY;

        public static float PresentX;
        public static float PresentY;

        public static long FrameId;
        public static long PresentFrameId;

        public static long SceneDepthFrameId;

        public static long SceneColorFrameId;
    }
}
