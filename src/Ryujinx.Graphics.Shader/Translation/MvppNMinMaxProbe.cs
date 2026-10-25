
using Ryujinx.Graphics.Shader.CodeGen;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Shader.Translation
{
    public static class MvppNMinMaxProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_NMINMAX") == "1";

        private static long _applied;

        public static void OnApplied(ILogger logger)
        {
            if (Interlocked.Increment(ref _applied) == 1)
            {
                logger?.Log("[NMINMAX] armed: SPIR-V float min/max emitted as NMin/NMax (NaN -> other operand, hardware FMNMX semantics)");
            }
        }

        public static long Applied => Interlocked.Read(ref _applied);
    }
}
