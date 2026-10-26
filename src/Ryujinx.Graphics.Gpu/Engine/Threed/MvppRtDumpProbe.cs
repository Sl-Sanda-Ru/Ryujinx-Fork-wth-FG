
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.IO;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppRtDumpProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_RT_DUMP") == "1";

        private static readonly int _startSeconds =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_RT_DUMP_START"), out int st) && st > 0 ? st : 90;

        private static readonly int _everySeconds =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_RT_DUMP_EVERY"), out int s) && s > 0 ? s : 10;

        private static readonly int _maxBursts =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_RT_DUMP_MAX"), out int m) && m > 0 ? m : 4;

        private static readonly bool _dumpAll =
            Environment.GetEnvironmentVariable("RYUJINX_RT_DUMP_ALL") == "1";

        private static readonly bool _dumpInputs =
            Environment.GetEnvironmentVariable("RYUJINX_RT_DUMP_INPUTS") == "1";

        private static readonly bool _frameMode =
            Environment.GetEnvironmentVariable("RYUJINX_RT_DUMP_FRAME") == "1";

        private const int MaxShapesPerBurst = 26;

        private static long _nextBurstMs;
        private static bool _bursting;
        private static long _burstEndsMs;
        private static int _burstIndex;
        private static string _burstDir;
        private static bool _frameArmLogged;
        private static readonly HashSet<string> _doneThisBurst = new();

        public static void NotePresented(Image.Texture texture)
        {
            if (!_enabled)
            {
                return;
            }

            if (!_frameMode)
            {
                if (!_bursting || texture == null)
                {
                    return;
                }

                try
                {
                    TryDump(texture, "PRESENTED");
                }
                catch (Exception e)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"RTDUMP: presented unreadable: {e.GetType().Name} {e.Message}");
                }

                return;
            }

            try
            {
                if (_bursting)
                {
                    if (texture != null)
                    {
                        TryDump(texture, "PRESENTED");
                    }

                    _bursting = false;
                    _nextBurstMs = Environment.TickCount64 + _everySeconds * 1000L;

                    Logger.Info?.Print(LogClass.Gpu,
                        $"RTDUMP: frame {_burstIndex}/{_maxBursts} complete, {_doneThisBurst.Count} buffers.");

                    return;
                }

                if (_burstIndex >= _maxBursts)
                {
                    return;
                }

                long now = Environment.TickCount64;

                if (_nextBurstMs == 0)
                {
                    _nextBurstMs = now + _startSeconds * 1000L;
                }

                if (!_frameArmLogged)
                {
                    _frameArmLogged = true;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"RTDUMP: armed in FRAME MODE -- first frame in {_startSeconds}s, then every " +
                        $"{_everySeconds}s, {_maxBursts} frames. One burst == ONE frame: targets and presented result " +
                        "belong to the SAME frame.");
                }

                if (now < _nextBurstMs)
                {
                    return;
                }

                _burstIndex++;
                _doneThisBurst.Clear();
                _burstDir = Path.Combine("rtdump", $"image{_burstIndex:D2}");
                Directory.CreateDirectory(_burstDir);
                _bursting = true;

                Logger.Info?.Print(LogClass.Gpu, $"RTDUMP: frame {_burstIndex}/{_maxBursts} opened -> {_burstDir}");
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"RTDUMP: frame mode disabled after error: {e.Message}");
            }
        }

        public static void OnDraw(GpuChannel channel)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                OnDrawImpl(channel);
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"RTDUMP: disabled after unexpected error: {e}");
            }
        }

        private static void OnDrawImpl(GpuChannel channel)
        {
            long now = Environment.TickCount64;

            if (_frameMode)
            {
                if (!_bursting)
                {
                    return;
                }
            }
            else if (!_bursting)
            {
                if (_burstIndex >= _maxBursts)
                {
                    return;
                }

                if (_nextBurstMs == 0)
                {
                    _nextBurstMs = now + _startSeconds * 1000L;

                    Logger.Info?.Print(LogClass.Gpu,
                        $"RTDUMP: armed -- first capture in {_startSeconds}s, then every {_everySeconds}s, " +
                        $"{_maxBursts} bursts. Be in the scene with the defect visible by then.");

                    return;
                }

                if (now < _nextBurstMs)
                {
                    return;
                }

                _bursting = true;
                _burstEndsMs = now + 1000;
                _burstIndex++;
                _doneThisBurst.Clear();
                _burstDir = Path.Combine("rtdump", $"burst{_burstIndex:D2}");

                try
                {
                    Directory.CreateDirectory(_burstDir);
                }
                catch (Exception e)
                {
                    _enabled = false;
                    Logger.Warning?.Print(LogClass.Gpu, $"RTDUMP: cannot create {_burstDir}: {e.Message}");

                    return;
                }

                Logger.Info?.Print(LogClass.Gpu, $"RTDUMP: burst {_burstIndex}/{_maxBursts} started -> {_burstDir}");
            }

            if (!_frameMode && now > _burstEndsMs)
            {
                _bursting = false;
                _nextBurstMs = now + _everySeconds * 1000L;

                Logger.Info?.Print(LogClass.Gpu,
                    $"RTDUMP: burst {_burstIndex} done, {_doneThisBurst.Count} buffers captured.");

                return;
            }

            channel.TextureManager.MvppEnumerateRenderTargets((index, tex) => TryDump(tex, $"color{index}"));

            Image.Texture ds = channel.TextureManager.RenderTargetDepthStencil;

            if (ds != null)
            {
                TryDump(ds, "depth");
            }

            if (_dumpInputs)
            {
                channel.TextureManager.MvppEnumerateGraphicsInputsStage((stage, tex) => TryDump(tex, $"input{stage}"));
            }
        }

        private static void TryDump(Image.Texture tex, string role)
        {
            if (tex == null || (role != "PRESENTED" && _doneThisBurst.Count >= MaxShapesPerBurst))
            {
                return;
            }

            GAL.ITexture host = tex.HostTexture;

            if (host == null)
            {
                return;
            }

            string name = $"{role}_{tex.Info.Width}x{tex.Info.Height}_{tex.Info.FormatInfo.Format}";

            if (role != "PRESENTED" && !_dumpAll && !_dumpInputs &&
                !tex.Info.FormatInfo.Format.ToString().StartsWith("R11G11B10", StringComparison.Ordinal))
            {
                return;
            }

            ulong gpuAddr = 0;

            try
            {
                gpuAddr = tex.Range.GetSubRange(0).Address;
            }
            catch
            {
            }

            if (!_doneThisBurst.Add(gpuAddr != 0 ? $"@{gpuAddr:X}" : name))
            {
                return;
            }

            try
            {
                using GAL.PinnedSpan<byte> data = host.GetData();
                ReadOnlySpan<byte> bytes = data.Get();

                string path = Path.Combine(_burstDir, $"{name}_at{gpuAddr:X}" + ".bin");
                File.WriteAllBytes(path, bytes.ToArray());

                Logger.Info?.Print(LogClass.Gpu,
                    $"RTDUMP: {name}.bin ({bytes.Length} bytes, {tex.Info.Width}x{tex.Info.Height} " +
                    $"{tex.Info.FormatInfo.Format}, {tex.Info.Target}).");
            }
            catch (Exception e)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"RTDUMP: {name} unreadable: {e.GetType().Name} {e.Message}");
            }
        }
    }
}
