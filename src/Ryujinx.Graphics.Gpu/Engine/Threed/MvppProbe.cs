
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_PROBE") == "1";

        private static readonly int _dumpSlot =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_MVPP_DUMP"), out int s) ? s : -1;

        private const int SampleIntervalMs = 2000;
        private const int MaxBytesPerBuffer = 4096;
        private const int MaxLoggedPerSample = 12;

        private static long _lastSampleMs;
        private static bool _readFailedLogged;
        private static readonly Dictionary<(int Stage, int Slot, int Offset), uint> _lastFingerprint = new();

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
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP probe: disabled after unexpected error: {e}");
            }
        }

        private static void OnDrawImpl(GpuChannel channel)
        {
            if (channel.TextureManager.RenderTargetScale == 1f)
            {
                return;
            }

            long now = Environment.TickCount64;
            if (now - _lastSampleMs < SampleIntervalMs)
            {
                return;
            }

            _lastSampleMs = now;

            if (_dumpSlot >= 0)
            {
                DumpCbuf(channel, _dumpSlot);

                return;
            }

            int candidates = 0;
            int changed = 0;
            int logged = 0;

            for (int stage = 0; stage < Constants.ShaderStages; stage++)
            {
                uint mask = channel.BufferManager.GetGraphicsUniformBufferUseMask(stage);

                for (int slot = 0; mask != 0; slot++, mask >>= 1)
                {
                    if ((mask & 1) == 0)
                    {
                        continue;
                    }

                    ulong address = channel.BufferManager.GetGraphicsUniformBufferAddress(stage, slot);
                    int size = Math.Min(channel.BufferManager.GetGraphicsUniformBufferSize(stage, slot), MaxBytesPerBuffer);

                    if (address == 0 || address == ulong.MaxValue || size < 64)
                    {
                        continue;
                    }

                    ReadOnlySpan<float> data;
                    try
                    {
                        data = MemoryMarshal.Cast<byte, float>(channel.MemoryManager.Physical.GetSpan(address, size));
                    }
                    catch (Exception e)
                    {
                        if (!_readFailedLogged)
                        {
                            _readFailedLogged = true;
                            Logger.Warning?.Print(LogClass.Gpu,
                                $"MVPP probe: cbuf read failed (stage{stage} cbuf{slot} @0x{address:X}): {e.GetType().Name} (logged once).");
                        }

                        continue;
                    }

                    for (int i = 0; i + 16 <= data.Length; i += 4)
                    {
                        ReadOnlySpan<float> window = data.Slice(i, 16);
                        if (!LooksLikeMat4(window))
                        {
                            continue;
                        }

                        candidates++;

                        uint fp = Fingerprint(window);
                        (int, int, int) key = (stage, slot, i * 4);

                        if (_lastFingerprint.TryGetValue(key, out uint prev) && prev != fp)
                        {
                            changed++;

                            if (logged < MaxLoggedPerSample)
                            {
                                logged++;
                                Logger.Info?.Print(LogClass.Gpu,
                                    $"MVPP probe CHANGED: stage{stage} cbuf{slot} +0x{i * 4:X3} " +
                                    $"r0=[{window[0]:0.###} {window[1]:0.###} {window[2]:0.###} {window[3]:0.###}] fp={fp:X8}");
                            }
                        }

                        _lastFingerprint[key] = fp;

                        i += 12;
                    }
                }
            }

            Logger.Info?.Print(LogClass.Gpu,
                $"MVPP probe sample: {candidates} mat4 candidates, {changed} changed since last sample.");
        }

        private static void DumpCbuf(GpuChannel channel, int slot)
        {
            ulong address = channel.BufferManager.GetGraphicsUniformBufferAddress(0, slot);
            int size = Math.Min(channel.BufferManager.GetGraphicsUniformBufferSize(0, slot), 512);

            if (address == 0 || address == ulong.MaxValue || size < 64)
            {
                return;
            }

            ReadOnlySpan<float> data = MemoryMarshal.Cast<byte, float>(channel.MemoryManager.Physical.GetSpan(address, size));

            for (int i = 0; i + 4 <= data.Length; i += 4)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP dump cbuf{slot} +0x{i * 4:X3}: {data[i]:0.####} {data[i + 1]:0.####} {data[i + 2]:0.####} {data[i + 3]:0.####}");
            }
        }

        private static bool LooksLikeMat4(ReadOnlySpan<float> m)
        {
            int nonZero = 0;

            for (int i = 0; i < 16; i++)
            {
                float v = m[i];

                if (!float.IsFinite(v) || MathF.Abs(v) > 1e6f)
                {
                    return false;
                }

                if (v != 0f)
                {
                    nonZero++;
                }
            }

            return nonZero >= 10;
        }

        private static uint Fingerprint(ReadOnlySpan<float> m)
        {
            uint fp = 2166136261u;

            for (int i = 0; i < 16; i++)
            {
                fp = (fp ^ (uint)BitConverter.SingleToInt32Bits(m[i])) * 16777619u;
            }

            return fp;
        }
    }
}
