
using System;

namespace Ryujinx.Graphics.Shader.Translation
{
    public static class RroReduceProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_RRO_REDUCE") == "1";
    }
}
