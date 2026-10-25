
namespace Ryujinx.Graphics.GAL
{
    public static class MvppDev
    {
        public static readonly bool Enabled =
            System.Environment.GetEnvironmentVariable("RYUJINX_MVPP_DEV") == "1";
    }
}
