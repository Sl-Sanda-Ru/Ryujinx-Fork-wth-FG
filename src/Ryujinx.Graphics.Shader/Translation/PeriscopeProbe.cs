
using System;

namespace Ryujinx.Graphics.Shader.Translation
{
    public static class PeriscopeProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_PERISCOPE") == "1";

        public static readonly bool Stage2 =
            Environment.GetEnvironmentVariable("RYUJINX_PERISCOPE2") == "1";
    }
}
