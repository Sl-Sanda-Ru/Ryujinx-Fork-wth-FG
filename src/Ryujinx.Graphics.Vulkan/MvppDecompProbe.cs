
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Vulkan
{
    static class MvppDecompProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVDECOMP") == "1";

        private static bool _armedLogged;
        private static long _roundTrips;
        private static long _frames;
        private static long _summaryMs;

        private static readonly Dictionary<int, WeakReference<TextureStorage>> _tracked = new();

        private static bool IsMvShape(TextureView view)
        {
            return view != null &&
                   view.Valid &&
                   view.Info.Width == 1280 &&
                   view.Info.Height == 720 &&
                   view.Info.Format == Ryujinx.Graphics.GAL.Format.R10G10B10A2Unorm;
        }

        public static void OnBind(Span<ITexture> colors)
        {
            if (!Enabled)
            {
                return;
            }

            for (int i = 0; i < colors.Length; i++)
            {
                if (colors[i] is TextureView view && IsMvShape(view))
                {
                    int id = RuntimeHelpers.GetHashCode(view.Storage);
                    lock (_tracked)
                    {
                        if (!_tracked.ContainsKey(id) && _tracked.Count < 4)
                        {
                            _tracked[id] = new WeakReference<TextureStorage>(view.Storage);
                            Logger.Warning?.Print(LogClass.Gpu,
                                $"[MVDECOMP] tracking MV storage 0x{id:X8} ({_tracked.Count} tracked)");
                        }
                    }
                }
            }
        }

        public static unsafe void Tick(VulkanRenderer gd, CommandBufferScoped cbs)
        {
            if (!Enabled)
            {
                return;
            }

            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    "[MVDECOMP] armed: per-frame layout round-trip General->TransferSrc->General on tracked MV storages (forced metadata decompression)");
            }

            _frames++;

            lock (_tracked)
            {
                foreach (var kv in _tracked)
                {
                    if (!kv.Value.TryGetTarget(out TextureStorage storage))
                    {
                        continue;
                    }

                    Image image = storage.GetImage().Get(cbs).Value;

                    ImageSubresourceRange range = new(
                        ImageAspectFlags.ColorBit,
                        0,
                        Vk.RemainingMipLevels,
                        0,
                        Vk.RemainingArrayLayers);

                    ImageMemoryBarrier toTransfer = new()
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = TextureStorage.DefaultAccessMask,
                        DstAccessMask = AccessFlags.TransferReadBit,
                        OldLayout = ImageLayout.General,
                        NewLayout = ImageLayout.TransferSrcOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = image,
                        SubresourceRange = range,
                    };

                    ImageMemoryBarrier backToGeneral = toTransfer with
                    {
                        SrcAccessMask = AccessFlags.TransferReadBit,
                        DstAccessMask = TextureStorage.DefaultAccessMask,
                        OldLayout = ImageLayout.TransferSrcOptimal,
                        NewLayout = ImageLayout.General,
                    };

                    gd.Api.CmdPipelineBarrier(
                        cbs.CommandBuffer,
                        PipelineStageFlags.AllCommandsBit,
                        PipelineStageFlags.TransferBit,
                        0,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &toTransfer);

                    gd.Api.CmdPipelineBarrier(
                        cbs.CommandBuffer,
                        PipelineStageFlags.TransferBit,
                        PipelineStageFlags.AllCommandsBit,
                        0,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &backToGeneral);

                    _roundTrips++;
                }
            }

            long now = Environment.TickCount64;
            if (now - _summaryMs >= 3000)
            {
                _summaryMs = now;
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[MVDECOMP/HEARTBEAT ~3s] frames={_frames} roundTrips={_roundTrips} tracked={_tracked.Count}");
            }
        }
    }
}
