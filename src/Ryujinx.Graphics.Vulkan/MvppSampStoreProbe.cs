
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Vulkan
{
    static class MvppSampStoreProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_SAMPSTORE") == "1";

        private sealed class Entry
        {
            public long Samples;
            public int StorageId;
            public string FirstSeen;
        }

        private static readonly object _lock = new();
        private static readonly Dictionary<int, Entry> _byStorage = new();
        private static bool _armedLogged;
        private static long _heartbeatMs;

        public static void OnSampled(ShaderStage stage, int binding, TextureView view)
        {
            if (!Enabled)
            {
                return;
            }

            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    "[SAMPSTORE] armed: recording storage identity of every sampled MV-shaped view (A2B10G10R10 1280x720)");
            }

            if (view == null ||
                view.Width != 1280 ||
                view.Height != 720 ||
                view.VkFormat != Silk.NET.Vulkan.Format.A2B10G10R10UnormPack32)
            {
                return;
            }

            int storageId = view.Storage != null ? RuntimeHelpers.GetHashCode(view.Storage) : 0;

            lock (_lock)
            {
                if (!_byStorage.TryGetValue(storageId, out Entry entry))
                {
                    entry = new Entry
                    {
                        StorageId = storageId,
                        FirstSeen = $"stage={stage} binding={binding}",
                    };
                    _byStorage.Add(storageId, entry);

                    Logger.Warning?.Print(LogClass.Gpu,
                        $"[SAMPSTORE] NEW sampled MV-shaped storage=0x{storageId:X8} ({entry.FirstSeen})");
                }

                entry.Samples++;

                long now = Environment.TickCount64;
                if (now - _heartbeatMs >= 5000)
                {
                    _heartbeatMs = now;

                    foreach (Entry e in _byStorage.Values)
                    {
                        Logger.Warning?.Print(LogClass.Gpu,
                            $"[SAMPSTORE/HB ~5s]   storage=0x{e.StorageId:X8} samples={e.Samples} (first: {e.FirstSeen})");
                    }
                }
            }
        }
    }
}
