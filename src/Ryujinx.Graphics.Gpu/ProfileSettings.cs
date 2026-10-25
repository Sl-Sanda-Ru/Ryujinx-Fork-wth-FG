namespace Ryujinx.Graphics.Gpu
{
    /// <summary>
    /// Re-reads every GPU-side setting that comes from the upscaler settings profile. Called at each
    /// game start, after the profile's environment has been re-applied, so turning the upscaler or
    /// its options on or off between games needs no app restart.
    /// </summary>
    public static class ProfileSettings
    {
        public static void Reload()
        {
            Engine.Threed.MvppCameraCapture.ReloadProfileSettings();
            Engine.Threed.MvppLdrSkip.ReloadProfileSettings();
            Engine.Threed.MvppSoloCamera.ReloadProfileSettings();
            Image.Sampler.ReloadProfileSettings();
            Image.Texture.ReloadProfileSettings();
            Image.TextureCache.ReloadProfileSettings();
        }
    }
}
