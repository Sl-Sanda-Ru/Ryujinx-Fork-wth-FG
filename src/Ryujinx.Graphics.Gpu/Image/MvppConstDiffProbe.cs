
using Ryujinx.Common.Logging;
using System;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppConstDiffProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_CONSTDIFF") == "1";

        private const ulong BuilderFsAddress = 0x1000AD730UL;
        private const int FragmentStageIndex = 4;
        private const int CbufSlot = 3;

        private static bool _armedLogged;
        private static bool _failLogged;
        private static float _lastA;
        private static float _lastB;
        private static int _holdCount;
        private static long _lines;
        private static long _draws;
        private static long _changes;
        private static int _maxHold;
        private static long _heartbeatMs;
        private static readonly float[] _slotLast = new float[8];
        private static readonly long[] _slotChanges = new long[8];
        private static readonly int[] _slotHold = new int[8];
        private static readonly int[] _slotMaxHold = new int[8];

        public static void OnDraw(GpuChannel channel, ulong fsAddr)
        {
            if (!Enabled || fsAddr != BuilderFsAddress)
            {
                return;
            }

            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    "[CONSTDIFF] armed: tracing the builder's fp_c3 prev-frame matrix per draw (repeat across frames in motion = stale)");

                for (int st = 0; st < 5; st++)
                {
                    uint mask;

                    try
                    {
                        mask = channel.BufferManager.GetGraphicsUniformBufferUseMask(st);
                    }
                    catch
                    {
                        mask = 0xDEAD;
                    }

                    Logger.Warning?.Print(LogClass.Gpu, $"[CONSTDIFF] stage {st}: cbuf use mask = 0x{mask:X}");
                }
            }

            _draws++;

            try
            {
                uint useMask = channel.BufferManager.GetGraphicsUniformBufferUseMask(FragmentStageIndex);

                for (int slot = 0; slot < 8; slot++)
                {
                    if ((useMask & (1u << slot)) == 0)
                    {
                        continue;
                    }

                    ulong addr = channel.BufferManager.GetGraphicsUniformBufferAddress(FragmentStageIndex, slot);

                    if (addr == 0 || addr == ulong.MaxValue)
                    {
                        continue;
                    }

                    float a = BitConverter.Int32BitsToSingle(channel.MemoryManager.Physical.Read<int>(addr + 5 * 16));

                    if (_slotLast[slot] != a)
                    {
                        _slotChanges[slot]++;
                        _slotLast[slot] = a;
                        _slotHold[slot] = 0;
                    }
                    else
                    {
                        _slotHold[slot]++;
                        if (_slotHold[slot] > _slotMaxHold[slot])
                        {
                            _slotMaxHold[slot] = _slotHold[slot];
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_failLogged)
                {
                    _failLogged = true;
                    Logger.Warning?.Print(LogClass.Gpu, $"[CONSTDIFF] read FAILED: {ex.Message}");
                }

                return;
            }

            long now = Environment.TickCount64;
            if (now - _heartbeatMs >= 5000)
            {
                _heartbeatMs = now;
                Logger.Warning?.Print(LogClass.Gpu, $"[CONSTDIFF/HB ~5s] builderDraws={_draws}");

                for (int slot = 0; slot < 8; slot++)
                {
                    if (_slotChanges[slot] > 0 || _slotMaxHold[slot] > 0)
                    {
                        Logger.Warning?.Print(LogClass.Gpu,
                            $"[CONSTDIFF/HB ~5s] slot={slot} draws={_draws} changes={_slotChanges[slot]} maxHold={_slotMaxHold[slot] + 1} last={_slotLast[slot]:G9}");
                    }
                }
            }
        }
    }
}
