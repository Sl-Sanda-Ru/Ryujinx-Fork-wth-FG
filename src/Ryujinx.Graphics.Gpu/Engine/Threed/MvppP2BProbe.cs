
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppP2BProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_MVPP_P2B_PROBE") == "1";

        private const int LogIntervalMs = 2000;
        private const float FrustumMargin = 1.3f;
        private const float MinTranslation = 0.5f;
        private const int SlotCount = 18;
        private const int ScanSampleShift = 4;
        private const int MaxScanFloats = 5120;

        private static readonly int[] _scanSlots = { 1, 5, 9, 13 };

        private static long _windowStartMs;
        private static int _draws;
        private static int _noCam;
        private static int _cb9Unbound;
        private static int _named;
        private static bool _sampleDumped;
        private static readonly int[] _slotBound = new int[SlotCount];
        private static readonly int[] _scanned = new int[4];
        private static readonly int[][][] _hits = CreateHits();

        private static int[][][] CreateHits()
        {
            int[][][] hits = new int[4][][];
            for (int s = 0; s < 4; s++)
            {
                hits[s] = [new int[MaxScanFloats], new int[MaxScanFloats]];
            }

            return hits;
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
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP P2B probe: disabled after unexpected error: {e}");
            }
        }

        private static void OnDrawImpl(GpuChannel channel)
        {
            if (channel.TextureManager.RenderTargetScale == 1f)
            {
                return;
            }

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

            _draws++;

            uint mask = channel.BufferManager.GetGraphicsUniformBufferUseMask(0);
            for (int s = 0; s < SlotCount; s++)
            {
                if ((mask & (1u << s)) != 0)
                {
                    _slotBound[s]++;
                }
            }

            if (!DlssCameraState.SnapshotCurrent(out Matrix4x4 vp))
            {
                _noCam++;

                return;
            }

            _named++;

            if ((_named & ((1 << ScanSampleShift) - 1)) != 0)
            {
                return;
            }

            for (int s = 0; s < _scanSlots.Length; s++)
            {
                int slot = _scanSlots[s];
                if ((mask & (1u << slot)) == 0)
                {
                    continue;
                }

                ulong addr = channel.BufferManager.GetGraphicsUniformBufferAddress(0, slot);
                int size = channel.BufferManager.GetGraphicsUniformBufferSize(0, slot);

                if (addr == 0 || addr == ulong.MaxValue || size < 48)
                {
                    continue;
                }

                _scanned[s]++;

                ReadOnlySpan<float> data = MemoryMarshal.Cast<byte, float>(
                    channel.MemoryManager.Physical.GetSpan(addr, Math.Min(size, MaxScanFloats * 4)));

                int count = data.Length;
                int[] contiguous = _hits[s][0];
                int[] spread = _hits[s][1];

                for (int f = 0; f < count; f++)
                {
                    if (f + 3 <= count)
                    {
                        float tx = data[f], ty = data[f + 1], tz = data[f + 2];
                        if (SaneT(tx, ty, tz) && ProjectsIntoFrustum(in vp, tx, ty, tz))
                        {
                            contiguous[f]++;
                        }
                    }

                    if (f + 9 < count)
                    {
                        float tx = data[f], ty = data[f + 4], tz = data[f + 8];
                        if (SaneT(tx, ty, tz) && ProjectsIntoFrustum(in vp, tx, ty, tz))
                        {
                            spread[f]++;
                        }
                    }
                }
            }

            if (!_sampleDumped && _named >= 500)
            {
                _sampleDumped = true;

                ulong cb9Addr = channel.BufferManager.GetGraphicsUniformBufferAddress(0, 9);
                ulong cb10Addr = channel.BufferManager.GetGraphicsUniformBufferAddress(0, 10);

                if (cb9Addr != 0 && cb9Addr != ulong.MaxValue && cb10Addr != 0 && cb10Addr != ulong.MaxValue)
                {
                    ReadOnlySpan<uint> h9 = MemoryMarshal.Cast<byte, uint>(channel.MemoryManager.Physical.GetSpan(cb9Addr, 32));
                    ReadOnlySpan<uint> h10 = MemoryMarshal.Cast<byte, uint>(channel.MemoryManager.Physical.GetSpan(cb10Addr, 48));

                    Logger.Info?.Print(LogClass.Gpu,
                        $"MVPP P2B heads: cb9 [{h9[0]:X8} {h9[1]:X8} {h9[2]:X8} {h9[3]:X8} {h9[4]:X8} {h9[5]:X8} {h9[6]:X8} {h9[7]:X8}] " +
                        $"cb10 [{h10[0]:X8} {h10[1]:X8} {h10[2]:X8} {h10[3]:X8} {h10[4]:X8} {h10[5]:X8} {h10[6]:X8} {h10[7]:X8} {h10[8]:X8} {h10[9]:X8} {h10[10]:X8} {h10[11]:X8}].");
                }
            }
        }

        private static bool SaneT(float x, float y, float z)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            {
                return false;
            }

            float ax = MathF.Abs(x), ay = MathF.Abs(y), az = MathF.Abs(z);

            return ax < 1e6f && ay < 1e6f && az < 1e6f &&
                   (ax >= MinTranslation || ay >= MinTranslation || az >= MinTranslation);
        }

        private static bool ProjectsIntoFrustum(in Matrix4x4 vp, float x, float y, float z)
        {
            float cw = vp.M41 * x + vp.M42 * y + vp.M43 * z + vp.M44;
            if (cw < 1e-3f)
            {
                return false;
            }

            float cx = vp.M11 * x + vp.M12 * y + vp.M13 * z + vp.M14;
            float cy = vp.M21 * x + vp.M22 * y + vp.M23 * z + vp.M24;

            return MathF.Abs(cx / cw) <= FrustumMargin && MathF.Abs(cy / cw) <= FrustumMargin;
        }

        private static void AppendTopOffsets(System.Text.StringBuilder sb, int[] hits, int scanned)
        {
            for (int rank = 0; rank < 4; rank++)
            {
                int best = -1;
                int bestHits = 0;

                for (int f = 0; f < hits.Length; f++)
                {
                    if (hits[f] > bestHits)
                    {
                        bestHits = hits[f];
                        best = f;
                    }
                }

                if (best < 0)
                {
                    break;
                }

                sb.Append($" 0x{best * 4:X}:{100f * bestHits / scanned:0}%");
                hits[best] = 0;
            }
        }

        private static void LogWindow()
        {
            if (_draws > 0)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP P2B probe: {_draws} draws, camera ok {(100f * _named / _draws):0.0}% (noCam {_noCam}, cb9-unbound {_cb9Unbound}).");

                System.Text.StringBuilder census = new();
                census.Append("MVPP P2B slots(vtx):");
                for (int s = 0; s < SlotCount; s++)
                {
                    if (_slotBound[s] > 0)
                    {
                        census.Append($" {s}:{100f * _slotBound[s] / _draws:0}%");
                    }
                }

                Logger.Info?.Print(LogClass.Gpu, census.ToString());

                for (int s = 0; s < _scanSlots.Length; s++)
                {
                    if (_scanned[s] == 0)
                    {
                        continue;
                    }

                    System.Text.StringBuilder scan = new();
                    scan.Append($"MVPP P2B scan cb{_scanSlots[s]}(n={_scanned[s]}): vec3");
                    AppendTopOffsets(scan, _hits[s][0], _scanned[s]);
                    scan.Append(" | spread");
                    AppendTopOffsets(scan, _hits[s][1], _scanned[s]);
                    Logger.Info?.Print(LogClass.Gpu, scan.ToString());
                }
            }

            _draws = 0;
            _noCam = 0;
            _cb9Unbound = 0;
            _named = 0;
            _sampleDumped = false;
            Array.Clear(_slotBound);
            Array.Clear(_scanned);

            for (int s = 0; s < 4; s++)
            {
                Array.Clear(_hits[s][0]);
                Array.Clear(_hits[s][1]);
            }
        }
    }
}
