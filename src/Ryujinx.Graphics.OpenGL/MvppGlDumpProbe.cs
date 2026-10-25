
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.OpenGL.Image;
using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.OpenGL
{
    static class MvppGlDumpProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_GLDUMP") == "1";

        private const long IntervalMs = 8000;
        private const long WindowMs = 400;

        private static bool _armedLogged;
        private static long _lastDumpMs;
        private static long _windowUntilMs;
        private static int _dumpRound;
        private static string _dir;

        private static bool IsWatched(TextureView view)
        {
            return (view.Info.Width, view.Info.Height, view.Info.Format) switch
            {
                (1280, 720, Format.R10G10B10A2Unorm) => true,
                (1280, 720, Format.R8G8B8A8Unorm) => true,
                (1280, 720, Format.R11G11B10Float) => true,
                (640, 360, Format.R32Float) => true,
                (512, 288, Format.R16G16B16A16Float) => true,
                (320, 180, Format.R8G8B8A8Unorm) => true,
                (64, 36, Format.R8G8B8A8Unorm) => true,
                (64, 36, Format.R8Unorm) => true,
                _ => false,
            };
        }

        public static void OnRenderTargetsChange(Framebuffer fb)
        {
            if (!Enabled || fb == null)
            {
                return;
            }

            long now = Environment.TickCount64;
            bool inWindow = now < _windowUntilMs;

            if (!inWindow && now - _lastDumpMs < IntervalMs)
            {
                return;
            }

            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    "[GLDUMP] armed: OpenGL-side chain dumps at end of pass (mirror of the Vulkan probe)");
            }

            bool any = false;

            for (int i = 0; i < 8; i++)
            {
                TextureView view = fb.MvppGetColor(i);

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

                    _dir ??= Directory.CreateDirectory("gldump").FullName;
                }

                int id = RuntimeHelpers.GetHashCode(view);

                try
                {
                    using PinnedSpan<byte> pinned = view.GetData(0, 0);
                    ReadOnlySpan<byte> data = pinned.Get();

                    long nonZero = 0;
                    for (int b = 0; b < data.Length; b++)
                    {
                        if (data[b] != 0)
                        {
                            nonZero++;
                        }
                    }

                    string file = Path.Combine(_dir,
                        $"round{_dumpRound:D3}_rt{i}_s{id:X8}_{view.Info.Width}x{view.Info.Height}_{view.Info.Format}_{now}.bin");
                    File.WriteAllBytes(file, data.ToArray());

                    Logger.Warning?.Print(LogClass.Gpu,
                        $"[GLDUMP] round={_dumpRound} rt={i} bytes={data.Length} nonZeroBytes={100.0 * nonZero / data.Length:F1}% file={Path.GetFileName(file)}");
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"[GLDUMP] rt={i} FAILED: {ex.Message}");
                }
            }
        }
    }
}
