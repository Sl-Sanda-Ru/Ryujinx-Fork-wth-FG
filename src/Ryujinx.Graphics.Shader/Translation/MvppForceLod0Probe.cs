
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Shader.Translation
{
    public static class MvppForceLod0Probe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_FORCELOD0") == "1";

        private static readonly HashSet<int> _logged = new();

        public static void OnApplied(IGpuAccessor gpuAccessor, int handle)
        {
            lock (_logged)
            {
                if (_logged.Add(handle))
                {
                    gpuAccessor.Log($"[FORCELOD0] bokeh FS sampler handle 0x{handle:X}: implicit-LOD sample forced to textureLod(0)");
                }
            }
        }
    }
}
