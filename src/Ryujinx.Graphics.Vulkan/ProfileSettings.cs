namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// Re-reads every Vulkan-side setting that comes from the upscaler settings profile: DLSS / FSR
    /// 3.1, the quality mode, sharpening, both frame generators and the MV++ tuning. Called at each
    /// game start, before the renderer is created, so they apply without restarting the app.
    /// </summary>
    public static class ProfileSettings
    {
        public static void Reload()
        {
            Dlss.DlssIntegration.ReloadProfileSettings();
            Dlss.DlssSharpenPass.ReloadProfileSettings();
            Dlss.DlssUpscaler.ReloadProfileSettings();
            Dlss.StreamlineFrameGen.ReloadProfileSettings();
            FsrFg.Fsr3Upscaler.ReloadProfileSettings();
            FsrFg.FsrFgApi.ReloadProfileSettings();
            VulkanRenderer.ReloadProfileSettings();
            Window.ReloadProfileSettings();
        }
    }
}
