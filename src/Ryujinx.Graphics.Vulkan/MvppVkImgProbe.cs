
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Vulkan
{
    static class MvppVkImgProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_VKIMG_PROBE") == "1" ||
            Environment.GetEnvironmentVariable("RYUJINX_VKPHASE_PROBE") == "1";

        public static readonly bool PhaseEnabled =
            Environment.GetEnvironmentVariable("RYUJINX_VKPHASE_PROBE") == "1";

        private static bool _armedLogged;

        private static long _attachBinds;
        private static long _sampledBinds;
        private static long _staleSampled;
        private static long _summaryMs;

        private static readonly HashSet<int> _attachIds = new();
        private static readonly HashSet<int> _sampledIds = new();
        private static readonly HashSet<int> _staleIds = new();

        private static readonly Dictionary<int, char> _letters = new();
        private static readonly HashSet<char> _frameWrites = new();
        private static readonly HashSet<char> _frameReads = new();
        private static readonly Dictionary<string, long> _signatures = new();
        private static long _frames;

        private static char Letter(int id)
        {
            if (!_letters.TryGetValue(id, out char c))
            {
                c = (char)('A' + Math.Min(_letters.Count, 25));
                _letters[id] = c;
            }

            return c;
        }

        private static bool IsMvShape(TextureView view)
        {
            return view != null &&
                   view.Valid &&
                   view.Info.Width == 1280 &&
                   view.Info.Height == 720 &&
                   view.Info.Format == Format.R10G10B10A2Unorm;
        }

        private static void ReportArmed()
        {
            if (!_armedLogged)
            {
                _armedLogged = true;
                Logger.Warning?.Print(LogClass.Gpu,
                    "[VKIMG] ARMED (RYUJINX_VKIMG_PROBE=1). Comparing VkImage (TextureStorage) identity of the MV buffer: attachment side vs sampled side.");
            }
        }

        public static void OnBind(Span<ITexture> colors)
        {
            if (!Enabled)
            {
                return;
            }

            ReportArmed();

            for (int i = 0; i < colors.Length; i++)
            {
                if (colors[i] is TextureView view && IsMvShape(view))
                {
                    _attachBinds++;

                    int id = RuntimeHelpers.GetHashCode(view.Storage);
                    lock (_attachIds)
                    {
                        if (_attachIds.Add(id))
                        {
                            Logger.Warning?.Print(LogClass.Gpu,
                                $"[VKIMG] ATTACHMENT storage 0x{id:X8} (new; {_attachIds.Count} attachment storage(s) so far)");
                        }

                        if (PhaseEnabled)
                        {
                            _frameWrites.Add(Letter(id));
                        }
                    }
                }
            }

            Heartbeat();
        }

        public static void OnSampled(TextureView view)
        {
            if (!Enabled || !IsMvShape(view))
            {
                return;
            }

            ReportArmed();
            _sampledBinds++;

            int id = RuntimeHelpers.GetHashCode(view.Storage);
            bool isNew;
            bool seenAsAttachment;

            lock (_attachIds)
            {
                isNew = _sampledIds.Add(id);
                seenAsAttachment = _attachIds.Contains(id);

                if (PhaseEnabled)
                {
                    _frameReads.Add(Letter(id));
                }

                if (!seenAsAttachment)
                {
                    _staleSampled++;
                    if (_staleIds.Add(id))
                    {
                        Logger.Warning?.Print(LogClass.Gpu,
                            $"[VKIMG] *** SAMPLED storage 0x{id:X8} NEVER SEEN AS ATTACHMENT (stale-twin suspect) *** | sampled {_sampledIds.Count}, attached {_attachIds.Count}");
                    }
                }
                else if (isNew)
                {
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"[VKIMG] SAMPLED storage 0x{id:X8} = same as an attachment storage (healthy) | sampled {_sampledIds.Count}, attached {_attachIds.Count}");
                }
            }

            Heartbeat();
        }

        public static void OnPresent()
        {
            if (!PhaseEnabled)
            {
                return;
            }

            lock (_attachIds)
            {
                _frames++;

                if (_frameWrites.Count > 0 || _frameReads.Count > 0)
                {
                    string w = _frameWrites.Count > 0 ? string.Join("+", _frameWrites) : "-";
                    string r = _frameReads.Count > 0 ? string.Join("+", _frameReads) : "-";
                    string sig = $"W:{w} R:{r}";

                    if (!_signatures.TryGetValue(sig, out long n))
                    {
                        Logger.Warning?.Print(LogClass.Gpu,
                            $"[VKPHASE] NEW frame signature: {sig} (frame {_frames})");
                    }

                    _signatures[sig] = n + 1;
                    _frameWrites.Clear();
                    _frameReads.Clear();
                }

                long now = Environment.TickCount64;
                if (now - _summaryMs >= 3000)
                {
                    _summaryMs = now;

                    string summary = "";
                    foreach (var kv in _signatures)
                    {
                        summary += $" [{kv.Key}]x{kv.Value}";
                    }

                    Logger.Warning?.Print(LogClass.Gpu,
                        $"[VKPHASE/HEARTBEAT ~3s] frames={_frames} signatures:{summary}");
                }
            }
        }

        private static void Heartbeat()
        {
            if (PhaseEnabled)
            {
                return;
            }

            long now = Environment.TickCount64;
            if (now - _summaryMs >= 3000)
            {
                _summaryMs = now;
                Logger.Warning?.Print(LogClass.Gpu,
                    $"[VKIMG/HEARTBEAT ~3s] attachBinds={_attachBinds} ids={_attachIds.Count} | sampledBinds={_sampledBinds} ids={_sampledIds.Count} | STALE-sampled binds={_staleSampled} ids={_staleIds.Count}");
            }
        }
    }
}
