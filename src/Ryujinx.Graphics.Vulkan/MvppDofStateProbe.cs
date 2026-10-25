
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Vulkan
{
    static class MvppDofStateProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_DOFSTATE") == "1";

        private static readonly object _lock = new();
        private static readonly HashSet<string> _seen = new();
        private static bool _armedLogged;

        public static void OnDraw(FramebufferParams fb, ref PipelineState state)
        {
            if (!Enabled || fb == null)
            {
                return;
            }

            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    "[DOFSTATE] armed: logging fixed-function state of draws into DoF-chain-sized targets");
            }

            bool interesting = false;

            for (int a = 0; a < fb.ColorAttachmentsCount; a++)
            {
                TextureView view = fb.MvppGetColorView(a);

                if (view != null &&
                    ((view.Width == 512 && view.Height == 288) ||
                     (view.Width == 320 && view.Height == 180) ||
                     (view.Width == 64 && view.Height == 36)))
                {
                    interesting = true;
                    break;
                }
            }

            if (!interesting)
            {
                return;
            }

            var blend = state.Internal.ColorBlendAttachmentState[0];

            string line =
                $"fb={fb.AttachmentFormats[0]} {fb.Width}x{fb.Height} rts={fb.ColorAttachmentsCount} | " +
                $"topo={state.Topology} cull={state.CullMode} front={state.FrontFace} | " +
                $"blend={blend.BlendEnable} colorOp={blend.ColorBlendOp} src={blend.SrcColorBlendFactor} dst={blend.DstColorBlendFactor} " +
                $"alphaOp={blend.AlphaBlendOp} srcA={blend.SrcAlphaBlendFactor} dstA={blend.DstAlphaBlendFactor} mask=0x{(uint)blend.ColorWriteMask:X}";

            lock (_lock)
            {
                if (_seen.Add(line))
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"[DOFSTATE] NEW STATE: {line}");
                }
            }
        }
    }
}
