using System;

namespace Ryujinx.Graphics.Vulkan
{
    internal static class ProvokingVertexOverride
    {
        internal const string ExtensionName = "VK_EXT_provoking_vertex";

        internal static readonly bool Requested =
            Environment.GetEnvironmentVariable("RYUJINX_VK_PROVOKING_LAST") == "1";

        internal static bool Active;
    }
}
