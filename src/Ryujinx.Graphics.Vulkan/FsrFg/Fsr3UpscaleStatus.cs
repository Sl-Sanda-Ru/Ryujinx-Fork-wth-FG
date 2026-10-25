namespace Ryujinx.Graphics.Vulkan.FsrFg
{
    /// <summary>
    /// The UI's view of FSR 3.1 upscaling, for the status bar. Separate from <see cref="Fsr3Upscaler"/>
    /// because that type takes Vulkan handles the UI has no business holding.
    /// </summary>
    public static class Fsr3UpscaleStatus
    {
        /// <summary>True when FSR 3.1 is the upscaler the temporal pipeline runs.</summary>
        public static bool Active => Fsr3Upscaler.Requested && Dlss.DlssIntegration.IsEnabled;
    }
}
