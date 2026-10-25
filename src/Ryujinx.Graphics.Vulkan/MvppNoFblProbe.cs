
using Ryujinx.Common.Logging;
using System;

namespace Ryujinx.Graphics.Vulkan
{
    static class MvppNoFblProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_NOFBL") == "1";

        private static bool _logged;

        public static void ReportStripped()
        {
            if (_logged)
            {
                return;
            }

            _logged = true;

            Logger.Warning?.Print(LogClass.Gpu,
                "[NOFBL] armed: VK_EXT_attachment_feedback_loop_* reported ABSENT -- no feedback-loop image usage, pipeline flags or detection this run");
        }
    }
}
