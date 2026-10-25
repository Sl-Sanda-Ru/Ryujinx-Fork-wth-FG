
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class MvppDofProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("RYUJINX_DOF_PROBE") == "1";

        private const int DofWidth = 512;
        private const int DofHeight = 288;

        private const int SmallRtMax = 640;

        private static readonly HashSet<string> _smallRtSizes = new();
        private static readonly Dictionary<string, long> _dofInputs = new();

        public static readonly HashSet<ulong> DofInputVas = new();
        private static long _lastSummaryMs;
        private static long _lastHeartbeatMs;
        private static long _totalBinds;
        private static bool _armed;
        private static int _announced;

        public static void AnnounceOnce()
        {
            if (System.Threading.Interlocked.Exchange(ref _announced, 1) != 0)
            {
                return;
            }

            string v = Environment.GetEnvironmentVariable("RYUJINX_DOF_PROBE");
            Logger.Info?.Print(LogClass.Gpu,
                $"DOFPROBE gate check: RYUJINX_DOF_PROBE={(v ?? "<unset>")} -> enabled={Enabled}. " +
                (Enabled ? "Probe ACTIVE." : "Probe OFF -- launch via XC2_DOF_PROBE.bat to enable."));
        }

        public static void OnInput(Texture texture, Texture renderTarget, ShaderStage stage)
        {
            if (texture == null || renderTarget == null)
            {
                return;
            }

            TextureInfo rt = renderTarget.Info;

            lock (_dofInputs)
            {
                _totalBinds++;

                if (!_armed)
                {
                    _armed = true;
                    Logger.Info?.Print(LogClass.Gpu,
                        "DOFPROBE armed: hook is live (RYUJINX_DOF_PROBE=1). Waiting for the low-res DoF pass -- get IN-GAME and move the camera.");
                }

                long hb = Environment.TickCount64;
                if (hb - _lastHeartbeatMs > 5000)
                {
                    _lastHeartbeatMs = hb;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"DOFPROBE alive: binds={_totalBinds} smallRTsizes={_smallRtSizes.Count} dofInputs={_dofInputs.Count}");
                }

                if (rt.Width <= SmallRtMax)
                {
                    string rtSize = $"{rt.Width}x{rt.Height} {rt.FormatInfo.Format}";
                    if (_smallRtSizes.Add(rtSize))
                    {
                        Logger.Info?.Print(LogClass.Gpu, $"DOFPROBE small-RT seen: {rtSize}");
                    }
                }

                if (rt.Width != DofWidth || rt.Height != DofHeight)
                {
                    return;
                }

                TextureInfo info = texture.Info;

                ulong va;
                try
                {
                    va = texture.Range.GetSubRange(0).Address;
                }
                catch
                {
                    va = 0;
                }

                DofInputVas.Add(va);

                string key =
                    $"stage={stage} @0x{va:X}/{info.FormatInfo.Format}/{info.Width}x{info.Height}/lv{info.Levels}/{info.Target}";

                if (_dofInputs.TryGetValue(key, out long n))
                {
                    _dofInputs[key] = n + 1;
                }
                else
                {
                    _dofInputs[key] = 1;
                    Logger.Info?.Print(LogClass.Gpu, $"DOFPROBE new DoF input: {key}");
                }

                long now = Environment.TickCount64;
                if (now - _lastSummaryMs > 5000)
                {
                    _lastSummaryMs = now;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"DOFPROBE summary: {_dofInputs.Count} distinct DoF inputs (most persistent = history candidate) =====");
                    foreach (KeyValuePair<string, long> kv in _dofInputs)
                    {
                        Logger.Info?.Print(LogClass.Gpu, $"DOFPROBE   hits={kv.Value,7}  {kv.Key}");
                    }
                }
            }
        }
    }
}
