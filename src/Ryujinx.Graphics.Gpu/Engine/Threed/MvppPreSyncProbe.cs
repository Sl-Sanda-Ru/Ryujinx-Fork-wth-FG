
using Ryujinx.Common.Logging;
using System;
using System.IO;

namespace Ryujinx.Graphics.Gpu.Engine.Threed
{
    static class MvppPreSyncProbe
    {
        private static bool _enabled =
            Environment.GetEnvironmentVariable("RYUJINX_PRESYNC") == "1";

        private static readonly int _vkey =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_PRESYNC_VKEY"), out int vk) && vk > 0 ? vk : 0x79;

        private static readonly int _maxShots =
            int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_PRESYNC_MAX"), out int mx) && mx > 0 ? mx : 4;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static bool _announced;
        private static int _shot;
        private static long _nextMs;
        private static byte[] _before;
        private static bool _armedThisFrame;
        private static int _guestShots;
        private static long _nextGuestMs;

        private static bool KeyHeld()
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                return (GetAsyncKeyState(_vkey) & 0x8000) != 0;
            }
            catch
            {
                return false;
            }
        }

        public static void DumpGuest(Image.Texture texture)
        {
            if (!_enabled || texture == null)
            {
                return;
            }

            try
            {
                long now = Environment.TickCount64;

                if (_guestShots >= _maxShots || now < _nextGuestMs || !KeyHeld())
                {
                    return;
                }

                ReadOnlySpan<byte> guest = texture.PhysicalMemory.GetSpan(texture.Range);

                Image.TextureInfo gi = texture.Info;
                string dir = Path.Combine("presync", $"guest{_guestShots:D2}");
                Directory.CreateDirectory(dir);

                File.WriteAllBytes(
                    Path.Combine(dir, $"MEMOIRE_{gi.Width}x{gi.Height}_lin{gi.IsLinear}_gobY{gi.GobBlocksInY}_stride{gi.Stride}_{gi.FormatInfo.Format}.bin"),
                    guest.ToArray());

                using GAL.PinnedSpan<byte> host = texture.HostTexture.GetData();
                File.WriteAllBytes(
                    Path.Combine(dir, $"HOTE_{gi.Width}x{gi.Height}_{gi.FormatInfo.Format}.bin"),
                    host.Get().ToArray());

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP PRESYNC GUEST shot {_guestShots}: guest memory {guest.Length} bytes + host texture, " +
                    $"{gi.Width}x{gi.Height} lin={gi.IsLinear} gobY={gi.GobBlocksInY} stride={gi.Stride} {gi.FormatInfo.Format} -> {dir}");

                _guestShots++;
                _nextGuestMs = now + 2000;
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP PRESYNC GUEST: disabled after error: {e.Message}");
            }
        }

        public static void Before(Image.Texture texture)
        {
            if (!_enabled || texture?.HostTexture == null)
            {
                return;
            }

            try
            {
                if (!_announced)
                {
                    _announced = true;
                    Logger.Info?.Print(LogClass.Gpu,
                        "MVPP PRESYNC: ON -- PRESS F10 when you SEE the artifact. Captures the presented frame " +
                        "BEFORE and AFTER the resync, to see whether IT is what destroys it.");
                }

                _armedThisFrame = false;

                if (_shot >= _maxShots)
                {
                    return;
                }

                long now = Environment.TickCount64;

                if (now < _nextMs || !KeyHeld())
                {
                    return;
                }

                using GAL.PinnedSpan<byte> data = texture.HostTexture.GetData();
                _before = data.Get().ToArray();
                _armedThisFrame = true;
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP PRESYNC: disabled after error (before): {e.Message}");
            }
        }

        public static void After(Image.Texture texture)
        {
            if (!_enabled || !_armedThisFrame || _before == null || texture?.HostTexture == null)
            {
                return;
            }

            _armedThisFrame = false;

            try
            {
                using GAL.PinnedSpan<byte> data = texture.HostTexture.GetData();
                byte[] after = data.Get().ToArray();

                bool identical = _before.Length == after.Length && _before.AsSpan().SequenceEqual(after);

                long diffBytes = 0;

                if (!identical && _before.Length == after.Length)
                {
                    for (int i = 0; i < after.Length; i++)
                    {
                        if (_before[i] != after[i])
                        {
                            diffBytes++;
                        }
                    }
                }

                string dir = Path.Combine("presync", $"shot{_shot:D2}");
                Directory.CreateDirectory(dir);
                string tag = $"{texture.Info.Width}x{texture.Info.Height}_{texture.Info.FormatInfo.Format}";
                File.WriteAllBytes(Path.Combine(dir, $"AVANT_{tag}.bin"), _before);
                File.WriteAllBytes(Path.Combine(dir, $"APRES_{tag}.bin"), after);

                double pct = after.Length > 0 ? 100.0 * diffBytes / after.Length : 0;

                Logger.Info?.Print(LogClass.Gpu,
                    $"MVPP PRESYNC shot {_shot}: {tag} -- {(identical ? "IDENTICAL (the resync changes nothing)" : $"DIFFERENT, {pct:F1}% of bytes changed by the resync")} -> {dir}");

                _shot++;
                _nextMs = Environment.TickCount64 + 2000;
                _before = null;
            }
            catch (Exception e)
            {
                _enabled = false;
                Logger.Warning?.Print(LogClass.Gpu, $"MVPP PRESYNC: disabled after error (after): {e.Message}");
            }
        }
    }
}
