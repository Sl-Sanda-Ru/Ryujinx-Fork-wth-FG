
using Ryujinx.Common.Logging;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    static class RenderSyncSwitch
    {
        public static readonly bool ForceRenderPassBarrier =
            Environment.GetEnvironmentVariable("RYUJINX_FORCE_RP_BARRIER") == "1";

        public static readonly bool ForceGpuIdlePerFrame =
            Environment.GetEnvironmentVariable("RYUJINX_FORCE_GPU_IDLE") == "1";

        private static int _announced;
        private static int _idleAnnounced;

        public static void AnnounceOnce()
        {
            if (Interlocked.Exchange(ref _announced, 1) == 0)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    "FORCE-RP-BARRIER: armed -- a full memory barrier is inserted at the end of every " +
                    "render pass (maximal inter-pass synchronization). Expect very low FPS; that is fine.");
            }
        }

        public static void AnnounceIdleOnce()
        {
            if (Interlocked.Exchange(ref _idleAnnounced, 1) == 0)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    "FORCE-GPU-IDLE: armed -- a full DeviceWaitIdle runs every frame (maximal CPU/GPU " +
                    "serialization, the mirror of the clean readback). Expect very low FPS; that is fine.");
            }
        }
    }
}
