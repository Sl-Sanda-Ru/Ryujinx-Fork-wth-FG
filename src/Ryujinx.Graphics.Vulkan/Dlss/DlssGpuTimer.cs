
using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;

namespace Ryujinx.Graphics.Vulkan.Dlss
{
    internal sealed class DlssGpuTimer : IDisposable
    {
        private const int Ring = 4;
        private const int Count = Ring * 2;
        private const double SpikeMs = 100.0;

        private readonly string _label;
        private readonly Vk _api;
        private readonly Device _device;
        private readonly double _periodNs;
        private readonly bool _supported;

        private QueryPool _pool;

        private readonly long[] _slotFrame = new long[Ring];
        private long _frame;

        private bool _rawLogged;
        private double _maxMs;
        private double _sumMs;
        private int _count;

        public unsafe DlssGpuTimer(VulkanRenderer gd, Device device, string label = "DLSS")
        {
            _label = label;
            _api = gd.Api;
            _device = device;

            _api.GetPhysicalDeviceProperties(gd.PhysicalDevice, out PhysicalDeviceProperties props);
            _periodNs = props.Limits.TimestampPeriod;
            _supported = _periodNs > 0f;

            if (_supported)
            {
                QueryPoolCreateInfo info = new()
                {
                    SType = StructureType.QueryPoolCreateInfo,
                    QueryType = QueryType.Timestamp,
                    QueryCount = Count,
                };
                _api.CreateQueryPool(device, in info, null, out _pool).ThrowOnError();
            }

            for (int i = 0; i < Ring; i++)
            {
                _slotFrame[i] = -1;
            }

            Logger.Info?.Print(LogClass.Gpu, $"{_label} GPU timer ARMED: timestampPeriod={_periodNs:0.###} ns, supported={_supported}.");
        }

        public void Begin(CommandBuffer cmd)
        {
            if (!_supported)
            {
                return;
            }

            int slot = (int)(_frame % Ring);
            _api.CmdResetQueryPool(cmd, _pool, (uint)(slot * 2), 2);
            _api.CmdWriteTimestamp(cmd, PipelineStageFlags.TopOfPipeBit, _pool, (uint)(slot * 2));
            _slotFrame[slot] = _frame;
        }

        public void End(CommandBuffer cmd)
        {
            if (!_supported)
            {
                return;
            }

            int slot = (int)(_frame % Ring);
            _api.CmdWriteTimestamp(cmd, PipelineStageFlags.BottomOfPipeBit, _pool, (uint)(slot * 2 + 1));
            _frame++;
        }

        public unsafe void Collect()
        {
            if (!_supported)
            {
                return;
            }

            long target = _frame - Ring;
            if (target < 0)
            {
                return;
            }

            int slot = (int)(target % Ring);
            if (_slotFrame[slot] != target)
            {
                return;
            }

            ulong* data = stackalloc ulong[2];
            Result r = _api.GetQueryPoolResults(_device, _pool, (uint)(slot * 2), 2, (nuint)(2 * sizeof(ulong)), data, sizeof(ulong),
                QueryResultFlags.Result64Bit);

            if (!_rawLogged)
            {
                _rawLogged = true;
                Logger.Info?.Print(LogClass.Gpu, $"{_label} GPU timer first read: result={r}, start={data[0]}, end={data[1]} (frame {target}).");
            }

            if (r != Result.Success || data[1] <= data[0])
            {
                return;
            }

            double ms = (data[1] - data[0]) * _periodNs / 1_000_000.0;
            _maxMs = Math.Max(_maxMs, ms);
            _sumMs += ms;
            _count++;

            if (ms > SpikeMs)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"{_label} GPU time SPIKE: {ms:0.0} ms (frame {target}).");
            }

            if (_count >= 120)
            {
                Logger.Info?.Print(LogClass.Gpu, $"{_label} GPU time: avg {_sumMs / _count:0.0} ms / max {_maxMs:0.0} ms over {_count} frames.");
                _sumMs = 0;
                _count = 0;
                _maxMs = 0;
            }
        }

        public unsafe void Dispose()
        {
            if (_supported)
            {
                _api.DestroyQueryPool(_device, _pool, null);
            }

            _pool = default;
        }
    }
}
