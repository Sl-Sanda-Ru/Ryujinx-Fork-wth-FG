namespace Ryujinx.Common.Configuration
{
    public enum VSyncMode
    {
        Switch,
        Unbounded,
        Custom,
        Vrr
    }

    public static class VSyncModeExtensions
    {
        public static VSyncMode Next(this VSyncMode vsync, bool customEnabled = false) =>
            vsync switch
            {
                VSyncMode.Switch => VSyncMode.Unbounded,
                VSyncMode.Unbounded => VSyncMode.Vrr,
                VSyncMode.Vrr => VSyncMode.Switch,
                VSyncMode.Custom => VSyncMode.Switch,
                _ => VSyncMode.Switch
            };
    }
}
