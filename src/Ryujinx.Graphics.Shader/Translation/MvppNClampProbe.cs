
using Ryujinx.Graphics.Shader.CodeGen;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Shader.Translation
{
    public static class MvppNClampProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_NCLAMP") == "1";

        private static long _applied;

        public static void OnApplied(ILogger logger)
        {
            if (Interlocked.Increment(ref _applied) == 1)
            {
                logger?.Log("[NCLAMP] armed: SPIR-V float clamps emitted as NClamp (NaN -> bound, console SAT semantics)");
            }
        }

        public static long Applied => Interlocked.Read(ref _applied);
    }
}
