
using Ryujinx.Common.Logging;
using System;
using System.IO;

namespace Ryujinx.Graphics.Vulkan
{
    static class MvppMvDumpProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVDUMP") == "1";

        public static readonly bool FlushEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVFLUSH") == "1";

        private const long IntervalMs = 8000;

        private const long WindowMs = 400;

        private static bool _armedLogged;
        private static long _lastDumpMs;
        private static long _windowUntilMs;
        private static int _dumpRound;
        private static string _dir;

        private static bool IsWatched(TextureView view)
        {
            return (view.Width, view.Height, view.VkFormat) switch
            {
                (1280, 720, Silk.NET.Vulkan.Format.A2B10G10R10UnormPack32) => true,
                (1280, 720, Silk.NET.Vulkan.Format.R8G8B8A8Unorm) => true,
                (1280, 720, Silk.NET.Vulkan.Format.B10G11R11UfloatPack32) => true,
                (640, 360, Silk.NET.Vulkan.Format.R32Sfloat) => true,
                (512, 288, Silk.NET.Vulkan.Format.R16G16B16A16Sfloat) => true,
                (320, 180, Silk.NET.Vulkan.Format.R8G8B8A8Unorm) => true,
                (64, 36, Silk.NET.Vulkan.Format.R8G8B8A8Unorm) => true,
                (64, 36, Silk.NET.Vulkan.Format.R8Unorm) => true,
                _ => false,
            };
        }

        private static long _flushCount;
        private static long _flushHeartbeatMs;

        public static bool MaybeDump(FramebufferParams fb)
        {
            if (fb == null || (!Enabled && !FlushEnabled))
            {
                return false;
            }

            long now = Environment.TickCount64;

            if (FlushEnabled)
            {
                return FlushPass(fb, now);
            }

            bool inWindow = now < _windowUntilMs;

            if (!inWindow && now - _lastDumpMs < IntervalMs)
            {
                return false;
            }

            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    "[MVDUMP v2] armed: raw copy-path dumps (vkCmdCopyImageToBuffer, no sampler) of MV-shaped attachments at END of their pass, every ~8s");
            }

            bool any = false;

            for (int a = 0; a < fb.ColorAttachmentsCount; a++)
            {
                TextureView view = fb.MvppGetColorView(a);

                if (view == null || !IsWatched(view))
                {
                    continue;
                }

                if (!any)
                {
                    any = true;

                    if (!inWindow)
                    {
                        _lastDumpMs = now;
                        _windowUntilMs = now + WindowMs;
                        _dumpRound++;
                    }

                    _dir ??= Directory.CreateDirectory("mvdump").FullName;
                }

                int bind = fb.AttachmentIndices[a];
                int storageId = view.Storage != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(view.Storage) : 0;

                try
                {
                    using Ryujinx.Graphics.GAL.PinnedSpan<byte> pinned = view.GetData(0, 0);
                    ReadOnlySpan<byte> data = pinned.Get();

                    long nonZero = 0;
                    long hot = 0;
                    int pixels = data.Length / 4;

                    for (int i = 0; i + 3 < data.Length; i += 4)
                    {
                        uint px = (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24));
                        uint rgb = px & 0x3FFFFFFFu;

                        if (rgb != 0)
                        {
                            nonZero++;
                        }

                        if ((px & 0x3FFu) >= 0x3CC || ((px >> 10) & 0x3FFu) >= 0x3CC || ((px >> 20) & 0x3FFu) >= 0x3CC)
                        {
                            hot++;
                        }
                    }

                    string file = Path.Combine(_dir, $"round{_dumpRound:D3}_rt{bind}_s{storageId:X8}_{view.Width}x{view.Height}_{view.VkFormat}_{now}.bin");
                    File.WriteAllBytes(file, data.ToArray());

                    Logger.Warning?.Print(LogClass.Gpu,
                        $"[MVDUMP] round={_dumpRound} rt={bind} storage=0x{storageId:X8} bytes={data.Length} " +
                        $"nonZero={100.0 * nonZero / pixels:F1}% hot={100.0 * hot / pixels:F2}% file={Path.GetFileName(file)}");
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"[MVDUMP] rt={bind} FAILED: {ex.Message}");
                }
            }

            return any;
        }

        private static bool FlushPass(FramebufferParams fb, long now)
        {
            bool any = false;

            for (int a = 0; a < fb.ColorAttachmentsCount; a++)
            {
                TextureView view = fb.MvppGetColorView(a);

                if (view == null ||
                    view.Width != 1280 ||
                    view.Height != 720 ||
                    view.VkFormat != Silk.NET.Vulkan.Format.A2B10G10R10UnormPack32)
                {
                    continue;
                }

                if (!_armedLogged)
                {
                    _armedLogged = true;
                    Logger.Warning?.Print(LogClass.Gpu,
                        "[MVFLUSH] armed: forced copy (vkCmdCopyImageToBuffer) of every MV-shaped attachment at END of every pass -- the copy IS the intervention");
                }

                try
                {
                    view.GetData(0, 0).Dispose();
                    any = true;
                    _flushCount++;
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"[MVFLUSH] rt={fb.AttachmentIndices[a]} FAILED: {ex.Message}");
                }
            }

            if (any && now - _flushHeartbeatMs >= 5000)
            {
                _flushHeartbeatMs = now;
                Logger.Warning?.Print(LogClass.Gpu, $"[MVFLUSH/HB ~5s] forced copies so far: {_flushCount}");
            }

            return any;
        }
    }
}
