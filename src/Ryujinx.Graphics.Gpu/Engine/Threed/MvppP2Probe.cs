
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppP2Probe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_P2_PROBE") == "1";

        private const int LogIntervalMs = 2000;

        private const float CoherentMaxDelta = 1.0f;

        private struct DrawRecord
        {
            public Matrix4x4 Mat0;
        }

        private static Dictionary<ulong, DrawRecord> _prev = new();
        private static Dictionary<ulong, DrawRecord> _curr = new();
        private static readonly Dictionary<ulong, int> _sigCounts = new();
        private static readonly object _sync = new();

        private static bool _activeLogged;
        private static bool _readFailedLogged;
        private static long _windowStartMs;
        private static int _frames;
        private static int _draws;
        private static int _covered;
        private static int _ambiguous;
        private static int _matched;
        private static int _coherent;
        private static double _sumDtRow;
        private static double _sumDtCol;
        private static float _maxDelta;
        private static long _sumCbufBytes;
        private static int _maxCbufBytes;

        public static void OnFrameEnqueued()
        {
            if (!_enabled)
            {
                return;
            }

            lock (_sync)
            {
                foreach (KeyValuePair<ulong, int> kv in _sigCounts)
                {
                    if (kv.Value > 1)
                    {
                        _ambiguous += kv.Value;
                    }
                }

                (_prev, _curr) = (_curr, _prev);
                _curr.Clear();
                _sigCounts.Clear();
                _frames++;

                long now = Environment.TickCount64;
                if (_windowStartMs == 0)
                {
                    _windowStartMs = now;
                }
                else if (now - _windowStartMs >= LogIntervalMs)
                {
                    LogWindow();
                    _windowStartMs = now;
                }
            }
        }

        public static void OnDraw(
            GpuChannel channel,
            ref ThreedClassState state,
            int firstIndex,
            int indexCount,
            int drawFirstVertex,
            int drawVertexCount)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                OnDrawImpl(channel, ref state, firstIndex, indexCount, drawFirstVertex, drawVertexCount);
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP P2 probe: disabled after unexpected error: {e}");
            }
        }

        private static void OnDrawImpl(
            GpuChannel channel,
            ref ThreedClassState state,
            int firstIndex,
            int indexCount,
            int drawFirstVertex,
            int drawVertexCount)
        {
            if (channel.TextureManager.RenderTargetScale == 1f)
            {
                return;
            }

            if (!_activeLogged)
            {
                _activeLogged = true;
                Logger.Info?.Print(LogClass.Gpu,
                    "MVPP P2 probe: active (frame-to-frame draw-identity matching, thresholds in docs/MVPP-JOURNAL.md).");
            }

            ulong sig = 14695981039346656037UL;
            sig = FnvStep(sig, state.ShaderState[1].Offset);
            sig = FnvStep(sig, state.ShaderState[5].Offset);
            sig = FnvStep(sig, state.VertexBufferState[0].Address.Pack());
            sig = FnvStep(sig, state.IndexBufferState.Address.Pack());
            sig = FnvStep(sig, (ulong)(uint)firstIndex);
            sig = FnvStep(sig, (ulong)(uint)indexCount);
            sig = FnvStep(sig, (ulong)(uint)drawFirstVertex);
            sig = FnvStep(sig, (ulong)(uint)drawVertexCount);

            ulong address = channel.BufferManager.GetGraphicsUniformBufferAddress(0, 5);
            int size = channel.BufferManager.GetGraphicsUniformBufferSize(0, 5);

            bool usable = address != 0 && address != ulong.MaxValue && size >= 64;
            Matrix4x4 mat0 = default;

            if (usable)
            {
                try
                {
                    ReadOnlySpan<float> data =
                        MemoryMarshal.Cast<byte, float>(channel.MemoryManager.Physical.GetSpan(address, 64));

                    for (int i = 0; i < 16; i++)
                    {
                        if (!float.IsFinite(data[i]))
                        {
                            usable = false;
                            break;
                        }
                    }

                    if (usable)
                    {
                        mat0 = MemoryMarshal.Read<Matrix4x4>(
                            MemoryMarshal.AsBytes(data));
                    }
                }
                catch (Exception e)
                {
                    usable = false;

                    if (!_readFailedLogged)
                    {
                        _readFailedLogged = true;
                        Logger.Warning?.Print(LogClass.Gpu,
                            $"MVPP P2 probe: cbuf5 read failed (@0x{address:X}): {e.GetType().Name} (logged once).");
                    }
                }
            }

            lock (_sync)
            {
                _draws++;

                int ordinal = _sigCounts.TryGetValue(sig, out int n) ? n : 0;
                _sigCounts[sig] = ordinal + 1;

                if (!usable)
                {
                    return;
                }

                _covered++;
                _sumCbufBytes += size;
                if (size > _maxCbufBytes)
                {
                    _maxCbufBytes = size;
                }

                ulong key = sig ^ (0x9E3779B97F4A7C15UL * (ulong)(ordinal + 1));
                _curr[key] = new DrawRecord { Mat0 = mat0 };

                if (_prev.TryGetValue(key, out DrawRecord prevRec))
                {
                    _matched++;

                    ReadOnlySpan<float> a = MemoryMarshal.Cast<Matrix4x4, float>(
                        MemoryMarshal.CreateReadOnlySpan(ref prevRec.Mat0, 1));
                    ReadOnlySpan<float> b = MemoryMarshal.Cast<Matrix4x4, float>(
                        MemoryMarshal.CreateReadOnlySpan(ref mat0, 1));

                    float maxd = 0f;
                    for (int i = 0; i < 16; i++)
                    {
                        float d = MathF.Abs(a[i] - b[i]);
                        if (d > maxd)
                        {
                            maxd = d;
                        }
                    }

                    if (maxd > _maxDelta)
                    {
                        _maxDelta = maxd;
                    }

                    if (maxd < CoherentMaxDelta)
                    {
                        _coherent++;

                        float drx = b[12] - a[12], dry = b[13] - a[13], drz = b[14] - a[14];
                        float dcx = b[3] - a[3], dcy = b[7] - a[7], dcz = b[11] - a[11];
                        _sumDtRow += MathF.Sqrt(drx * drx + dry * dry + drz * drz);
                        _sumDtCol += MathF.Sqrt(dcx * dcx + dcy * dcy + dcz * dcz);
                    }
                }
            }
        }

        private static ulong FnvStep(ulong hash, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                hash = (hash ^ (value & 0xFF)) * 1099511628211UL;
                value >>= 8;
            }

            return hash;
        }

        private static void LogWindow()
        {
            if (_draws > 0 && _frames > 0)
            {
                float cov = 100f * _covered / _draws;
                float amb = 100f * _ambiguous / _draws;
                float mat = _covered > 0 ? 100f * _matched / _covered : 0f;
                float coh = _matched > 0 ? 100f * _coherent / _matched : 0f;
                double dtRow = _coherent > 0 ? _sumDtRow / _coherent : 0.0;
                double dtCol = _coherent > 0 ? _sumDtCol / _coherent : 0.0;
                long avgCbuf = _covered > 0 ? _sumCbufBytes / _covered : 0;

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP P2 probe: {_frames} frames, {(float)_draws / _frames:0.0} scaled draws/frame, " +
                    $"coverage {cov:0.0}%, ambiguous {amb:0.0}%, matched {mat:0.0}% of covered, coherent {coh:0.0}% of matched, " +
                    $"mean|dT| row={dtRow:0.####} col={dtCol:0.####}, max|d| {_maxDelta:0.#}, cbuf5 avg {avgCbuf} max {_maxCbufBytes} B.");
            }

            _frames = 0;
            _draws = 0;
            _covered = 0;
            _ambiguous = 0;
            _matched = 0;
            _coherent = 0;
            _sumDtRow = 0.0;
            _sumDtCol = 0.0;
            _maxDelta = 0f;
            _sumCbufBytes = 0;
            _maxCbufBytes = 0;
        }
    }
}
